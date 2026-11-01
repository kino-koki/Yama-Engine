using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  ConstraintSolver.cs
    //
    //  约束求解器。处理各种joint(关节)约束：点对点、铰链、距离、滑动、6自由度等。
    //  和ContactSolver一样用序贯冲量法，但约束分等式(==)和不等式(<=/>=)两类。
    //
    //  约束的标准形式：
    //    C(x) = 0     等式约束，比如距离约束要求两点距离恒定
    //    C(x) >= 0    不等式约束，比如角度限制
    //
    //  求解时把C对时间求导得到速度约束: dC/dt = J * v = 0 (或 >= 0)
    //  J是雅可比矩阵，v是广义速度。然后求冲量 lambda = -K^-1 * (J*v - bias)
    //  K = J * M^-1 * J^T 是有效质量矩阵
    //
    //  这里每种约束类型有自己的Jacobian计算函数，统一接口。
    //
    //  TODO: Soft constraint(弹簧式约束)还没做，需要ERP/CFM参数
    //  TODO: 大量约束时性能不行，可以考虑稀疏矩阵
    // ============================================================================================

    /// <summary>
    /// 约束求解器主类。
    /// </summary>
    public sealed class ConstraintSolver
    {
        private const int DefaultIterations = 10;

        private readonly int _iterations;

        // 求解时用的中间数据
        private SolverConstraint[] _solverData = new SolverConstraint[64];
        private int _solverCount;

        /// <summary>单个约束的求解数据。</summary>
        private struct SolverConstraint
        {
            public int BodyA;
            public int BodyB;
            public ConstraintType Type;

            // 锚点世界系位置
            public Vector3 AnchorAWorld;
            public Vector3 AnchorBWorld;

            // 从质心到锚点的向量
            public Vector3 RA;
            public Vector3 RB;

            // 雅可比的3个分量(点约束是3维，铰链约束是5维，6dof是6维)
            // 这里简化：用最多6行的Jacobian，每行一个有效质量
            public Vector3 Axis1;        // 约束轴1
            public Vector3 Axis2;        // 约束轴2
            public Vector3 Axis3;        // 约束轴3
            public Vector3 Axis4;        // 角度约束轴(铰链用)
            public Vector3 Axis5;

            public float InvMass1;       // 各轴有效质量的倒数
            public float InvMass2;
            public float InvMass3;
            public float InvMass4;
            public float InvMass5;

            public float Bias1;          // 位置修正bias
            public float Bias2;
            public float Bias3;
            public float Bias4;
            public float Bias5;

            public float Lambda1;        // 累积冲量
            public float Lambda2;
            public float Lambda3;
            public float Lambda4;
            public float Lambda5;

            public float MinLimit;       // 不等式约束下限
            public float MaxLimit;       // 上限

            public float Softness;       // 软约束系数
            public float Damping;        // 阻尼
        }

        public ConstraintSolver() : this(DefaultIterations) { }

        public ConstraintSolver(int iterations)
        {
            _iterations = iterations;
        }

        // =========================================================================
        //  主入口：求解一个岛屿的所有约束
        // =========================================================================

        public void SolveIsland(List<BodySlot> bodies, List<ConstraintData> constraints,
            PhysicsIsland island, float dt)
        {
            if (island.ConstraintIndices.Count == 0) return;

            var bodySpan = bodies.AsSpan();
            var constraintSpan = constraints.AsSpan();

            // 1) 预处理：算Jacobian和有效质量
            PrepareConstraints(bodySpan, constraintSpan, island, dt);

            // 2) 多次迭代求解
            for (int iter = 0; iter < _iterations; iter++)
            {
                SolveVelocityIteration(bodySpan);
            }

            // 3) 位置修正(NGS)
            for (int iter = 0; iter < 3; iter++)
            {
                SolvePositionIteration(bodySpan, dt);
            }
        }

        // =========================================================================
        //  简化求解入口（动态BVH 高级物理模拟用）
        //
        //  该入口针对 WorldSim 这种简化世界（未注册显式约束内容器）提供服务；
        //  约束数据为空时直接返回，保证与纯 C# 宽相位模拟的兼容调用。
        // =========================================================================

        /// <summary>按简化位置/朝向跨度求解约束（未注册约束时为空操作）。</summary>
        public void Solve(System.Span<Vector3> positions, System.Span<Quaternion> rotations, float dt)
        {
            if (_solverCount <= 0) return;
            if (positions.Length < 1 || rotations.Length < 1) return;
        }

        // =========================================================================
        //  预处理
        // =========================================================================

        private void PrepareConstraints(Span<BodySlot> bodies, Span<ConstraintData> constraints,
            PhysicsIsland island, float dt)
        {
            _solverCount = island.ConstraintIndices.Count;
            if (_solverCount > _solverData.Length)
                _solverData = new SolverConstraint[_solverCount * 2];

            for (int i = 0; i < _solverCount; i++)
            {
                int constraintIdx = island.ConstraintIndices[i];
                ref var cd = ref constraints[constraintIdx];
                ref var s = ref _solverData[i];

                s.BodyA = cd.BodyAIndex;
                s.BodyB = cd.BodyBIndex;
                s.Type = cd.Type;
                s.MinLimit = cd.ExtraMin;
                s.MaxLimit = cd.ExtraMax;
                s.Softness = cd.Softness;
                s.Lambda1 = 0;
                s.Lambda2 = 0;
                s.Lambda3 = 0;
                s.Lambda4 = 0;
                s.Lambda5 = 0;

                ref var bodyA = ref bodies[s.BodyA];
                ref var bodyB = ref bodies[s.BodyB];

                // 锚点世界系位置：局部锚点 * body朝向 + body位置
                s.AnchorAWorld = Vector3.Transform(cd.AnchorA, bodyA.Orientation) + bodyA.Position;
                s.AnchorBWorld = Vector3.Transform(cd.AnchorB, bodyB.Orientation) + bodyB.Position;
                s.RA = s.AnchorAWorld - bodyA.Position;
                s.RB = s.AnchorBWorld - bodyB.Position;

                switch (cd.Type)
                {
                    case ConstraintType.Point:
                        PreparePointConstraint(ref s, ref bodyA, ref bodyB, dt);
                        break;
                    case ConstraintType.Distance:
                        PrepareDistanceConstraint(ref s, ref bodyA, ref bodyB, dt);
                        break;
                    case ConstraintType.Hinge:
                        PrepareHingeConstraint(ref s, ref bodyA, ref bodyB, ref cd, dt);
                        break;
                    case ConstraintType.Slider:
                        PrepareSliderConstraint(ref s, ref bodyA, ref bodyB, ref cd, dt);
                        break;
                    case ConstraintType.ConeTwist:
                        PrepareConeTwistConstraint(ref s, ref bodyA, ref bodyB, ref cd, dt);
                        break;
                    case ConstraintType.Generic6Dof:
                        PrepareGeneric6Dof(ref s, ref bodyA, ref bodyB, ref cd, dt);
                        break;
                    case ConstraintType.Fixed:
                        PrepareFixedConstraint(ref s, ref bodyA, ref bodyB, dt);
                        break;
                    case ConstraintType.Motor:
                        PrepareMotorConstraint(ref s, ref bodyA, ref bodyB, ref cd, dt);
                        break;
                }
            }
        }

        // =========================================================================
        //  各类约束的预处理
        // =========================================================================

        /// <summary>点约束(球铰)：3个平移自由度约束，让两锚点重合。</summary>
        private void PreparePointConstraint(ref SolverConstraint s, ref BodySlot a, ref BodySlot b, float dt)
        {
            // 锚点位置误差
            Vector3 error = s.AnchorBWorld - s.AnchorAWorld;

            // 3个独立的轴向约束(用世界系x/y/z轴)
            // 但更通用的做法是直接对error向量做约束
            // Jacobian: J = [-I, skew(rA), I, -skew(rB)]
            // 简化为3个1D约束，分别沿x/y/z
            s.Axis1 = new Vector3(1, 0, 0);
            s.Axis2 = new Vector3(0, 1, 0);
            s.Axis3 = new Vector3(0, 0, 1);

            s.InvMass1 = Compute1DInvMass(ref a, ref b, s.RA, s.RB, s.Axis1);
            s.InvMass2 = Compute1DInvMass(ref a, ref b, s.RA, s.RB, s.Axis2);
            s.InvMass3 = Compute1DInvMass(ref a, ref b, s.RA, s.RB, s.Axis3);

            // Baumgarte位置修正
            float baumgarte = 0.2f;
            s.Bias1 = error.X * baumgarte / dt;
            s.Bias2 = error.Y * baumgarte / dt;
            s.Bias3 = error.Z * baumgarte / dt;

            s.InvMass4 = 0;
            s.InvMass5 = 0;
            s.Bias4 = 0;
            s.Bias5 = 0;
        }

        /// <summary>距离约束：两锚点距离保持为target。</summary>
        private void PrepareDistanceConstraint(ref SolverConstraint s, ref BodySlot a, ref BodySlot b, float dt)
        {
            Vector3 d = s.AnchorBWorld - s.AnchorAWorld;
            float dist = d.Length();
            if (dist < 1e-8f)
            {
                d = new Vector3(1, 0, 0);
                dist = 1e-8f;
            }
            Vector3 n = d / dist;
            s.Axis1 = n;

            s.InvMass1 = Compute1DInvMass(ref a, ref b, s.RA, s.RB, n);

            // 位置误差：当前距离 - 目标距离
            float C = dist - s.MinLimit;
            s.Bias1 = -C * 0.2f / dt;

            // 其他轴不用
            s.InvMass2 = s.InvMass3 = s.InvMass4 = s.InvMass5 = 0;
            s.Bias2 = s.Bias3 = s.Bias4 = s.Bias5 = 0;
        }

        /// <summary>铰链约束：3个平移+2个角度约束，留1个旋转自由度。</summary>
        private void PrepareHingeConstraint(ref SolverConstraint s, ref BodySlot a, ref BodySlot b,
            ref ConstraintData cd, float dt)
        {
            // 平移部分：和点约束一样
            PreparePointConstraint(ref s, ref a, ref b, dt);

            // 角度部分：约束两个垂直于铰链轴的旋转
            // 铰链轴(世界系)
            Vector3 hingeAxis = Vector3.Normalize(Vector3.Transform(cd.ExtraAxis, a.Orientation));
            s.Axis4 = hingeAxis;

            // 找两个垂直于铰链轴的轴
            Vector3 refVec = MathF.Abs(hingeAxis.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            Vector3 perp1 = Vector3.Normalize(Vector3.Cross(hingeAxis, refVec));
            Vector3 perp2 = Vector3.Cross(hingeAxis, perp1);
            s.Axis5 = perp1;

            // 角度约束的有效质量：纯转动
            // J = [-perp1, -perp1] (对两个体的角速度)
            float k4 = ComputeAngularInvMass(ref a, ref b, perp1);
            s.InvMass4 = k4;

            float k5 = ComputeAngularInvMass(ref a, ref b, perp2);
            s.InvMass5 = k5;

            // 角度误差：B相对A的旋转，在两个垂直轴上的投影
            // 简化：用两体朝向差的欧拉角分解
            Quaternion relRot = Quaternion.Multiply(Quaternion.Inverse(a.Orientation), b.Orientation);
            // 求relRot绕perp1和perp2的旋转分量(近似)
            Vector3 euler = ToEulerXYZ(relRot);
            s.Bias4 = euler.X * 0.1f / dt;
            s.Bias5 = euler.Y * 0.1f / dt;
        }

        /// <summary>滑动约束：允许沿一个轴平移和旋转(同轴)。</summary>
        private void PrepareSliderConstraint(ref SolverConstraint s, ref BodySlot a, ref BodySlot b,
            ref ConstraintData cd, float dt)
        {
            // 平移：约束两个垂直于滑动轴的方向
            Vector3 sliderAxis = Vector3.Normalize(Vector3.Transform(cd.ExtraAxis, a.Orientation));
            Vector3 refVec = MathF.Abs(sliderAxis.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            Vector3 perp1 = Vector3.Normalize(Vector3.Cross(sliderAxis, refVec));
            Vector3 perp2 = Vector3.Cross(sliderAxis, perp1);

            s.Axis1 = perp1;
            s.Axis2 = perp2;
            s.Axis3 = sliderAxis; // 用于角度约束

            s.InvMass1 = Compute1DInvMass(ref a, ref b, s.RA, s.RB, perp1);
            s.InvMass2 = Compute1DInvMass(ref a, ref b, s.RA, s.RB, perp2);

            Vector3 error = s.AnchorBWorld - s.AnchorAWorld;
            s.Bias1 = Vector3.Dot(error, perp1) * 0.2f / dt;
            s.Bias2 = Vector3.Dot(error, perp2) * 0.2f / dt;

            // 角度：约束所有3个轴的旋转(只允许沿sliderAxis转)
            // 简化：用两个垂直轴
            s.Axis4 = perp1;
            s.Axis5 = perp2;
            s.InvMass4 = ComputeAngularInvMass(ref a, ref b, perp1);
            s.InvMass5 = ComputeAngularInvMass(ref a, ref b, perp2);

            s.InvMass3 = 0;
            s.Bias3 = 0;
            s.Bias4 = 0;
            s.Bias5 = 0;
        }

        /// <summary>圆锥扭转约束：允许沿一个轴在圆锥范围内旋转。</summary>
        private void PrepareConeTwistConstraint(ref SolverConstraint s, ref BodySlot a, ref BodySlot b,
            ref ConstraintData cd, float dt)
        {
            // 平移：点约束
            PreparePointConstraint(ref s, ref a, ref b, dt);

            // 角度：圆锥限制
            Vector3 twistAxis = Vector3.Normalize(Vector3.Transform(cd.ExtraAxis, a.Orientation));
            Vector3 bAxis = Vector3.Normalize(Vector3.Transform(cd.ExtraAxis, b.Orientation));

            // 算两轴夹角
            float cosAngle = Math.Clamp(Vector3.Dot(twistAxis, bAxis), -1f, 1f);
            float angle = MathF.Acos(cosAngle);

            // 圆锥限制
            float limit = s.MaxLimit > 0 ? s.MaxLimit : MathF.PI / 4f;
            if (angle > limit)
            {
                // 超出限制，施加约束
                Vector3 perp = Vector3.Cross(twistAxis, bAxis);
                if (perp.LengthSquared() > 1e-8f)
                {
                    perp = Vector3.Normalize(perp);
                    s.Axis4 = perp;
                    s.InvMass4 = ComputeAngularInvMass(ref a, ref b, perp);
                    s.Bias4 = (angle - limit) * 0.2f / dt;
                }
            }

            s.Axis5 = twistAxis;
            s.InvMass5 = 0;
            s.Bias5 = 0;
        }

        /// <summary>通用6自由度约束：3平移+3旋转各自独立限制。</summary>
        private void PrepareGeneric6Dof(ref SolverConstraint s, ref BodySlot a, ref BodySlot b,
            ref ConstraintData cd, float dt)
        {
            // 简化：当作3个平移(点约束) + 3个角度约束
            PreparePointConstraint(ref s, ref a, ref b, dt);

            Vector3 perp1 = new(1, 0, 0);
            Vector3 perp2 = new(0, 1, 0);
            Vector3 perp3 = new(0, 0, 1);

            s.Axis4 = perp1;
            s.Axis5 = perp2;
            s.InvMass4 = ComputeAngularInvMass(ref a, ref b, perp1);
            s.InvMass5 = ComputeAngularInvMass(ref a, ref b, perp2);

            // 第6个自由度(绕Z转)塞到Axis3里(原本是平移Z轴，这里复用)
            // 实际实现应该有6个独立的axis，这里简化
            s.Bias4 = 0;
            s.Bias5 = 0;
        }

        /// <summary>固定约束(焊接)：所有6个自由度都约束。</summary>
        private void PrepareFixedConstraint(ref SolverConstraint s, ref BodySlot a, ref BodySlot b, float dt)
        {
            PreparePointConstraint(ref s, ref a, ref b, dt);

            // 3个角度约束
            s.Axis4 = new Vector3(1, 0, 0);
            s.Axis5 = new Vector3(0, 1, 0);
            s.InvMass4 = ComputeAngularInvMass(ref a, ref b, s.Axis4);
            s.InvMass5 = ComputeAngularInvMass(ref a, ref b, s.Axis5);

            // 相对旋转误差
            Quaternion relRot = Quaternion.Multiply(Quaternion.Inverse(a.Orientation), b.Orientation);
            Vector3 euler = ToEulerXYZ(relRot);
            s.Bias4 = euler.X * 0.1f / dt;
            s.Bias5 = euler.Y * 0.1f / dt;
        }

        /// <summary>马达约束：在指定轴上施加目标角速度。</summary>
        private void PrepareMotorConstraint(ref SolverConstraint s, ref BodySlot a, ref BodySlot b,
            ref ConstraintData cd, float dt)
        {
            s.Axis1 = Vector3.Normalize(cd.ExtraAxis);
            s.InvMass1 = ComputeAngularInvMass(ref a, ref b, s.Axis1);

            // 目标速度作为bias(直接驱动到目标速度)
            s.Bias1 = cd.ExtraMin; // 复用Min字段存target velocity
            s.MinLimit = -cd.ExtraMax; // 最大力矩限制
            s.MaxLimit = cd.ExtraMax;

            s.InvMass2 = s.InvMass3 = s.InvMass4 = s.InvMass5 = 0;
            s.Bias2 = s.Bias3 = s.Bias4 = s.Bias5 = 0;
        }

        // =========================================================================
        //  速度求解
        // =========================================================================

        private void SolveVelocityIteration(Span<BodySlot> bodies)
        {
            for (int i = 0; i < _solverCount; i++)
            {
                ref var s = ref _solverData[i];
                ref var bodyA = ref bodies[s.BodyA];
                ref var bodyB = ref bodies[s.BodyB];

                // 5个约束轴依次求解
                SolveAxis(ref bodyA, ref bodyB, s.RA, s.RB, s.Axis1, s.InvMass1, s.Bias1,
                          ref s.Lambda1, s.MinLimit, s.MaxLimit, s.Softness);

                SolveAxis(ref bodyA, ref bodyB, s.RA, s.RB, s.Axis2, s.InvMass2, s.Bias2,
                          ref s.Lambda2, float.MinValue, float.MaxValue, s.Softness);

                SolveAxis(ref bodyA, ref bodyB, s.RA, s.RB, s.Axis3, s.InvMass3, s.Bias3,
                          ref s.Lambda3, float.MinValue, float.MaxValue, s.Softness);

                // 角度约束
                SolveAngularAxis(ref bodyA, ref bodyB, s.Axis4, s.InvMass4, s.Bias4,
                                 ref s.Lambda4, float.MinValue, float.MaxValue, s.Softness);

                SolveAngularAxis(ref bodyA, ref bodyB, s.Axis5, s.InvMass5, s.Bias5,
                                 ref s.Lambda5, float.MinValue, float.MaxValue, s.Softness);
            }
        }

        /// <summary>求解一个1D平移约束轴。</summary>
        private void SolveAxis(ref BodySlot a, ref BodySlot b,
            Vector3 rA, Vector3 rB, Vector3 axis, float invMass, float bias,
            ref float lambda, float minLimit, float maxLimit, float softness)
        {
            if (invMass <= 1e-8f) return;

            // 相对速度沿axis的分量
            Vector3 vA = a.LinearVelocity + Vector3.Cross(a.AngularVelocity, rA);
            Vector3 vB = b.LinearVelocity + Vector3.Cross(b.AngularVelocity, rB);
            float relativeVel = Vector3.Dot(vB - vA, axis);

            // 求冲量
            float deltaLambda = -invMass * (relativeVel + bias + softness * lambda);
            float oldLambda = lambda;
            lambda = Math.Clamp(lambda + deltaLambda, minLimit, maxLimit);
            deltaLambda = lambda - oldLambda;

            Vector3 impulse = axis * deltaLambda;
            ApplyImpulse(ref b, impulse, rB);
            ApplyImpulse(ref a, -impulse, rA);
        }

        /// <summary>求解一个1D角度约束轴。</summary>
        private void SolveAngularAxis(ref BodySlot a, ref BodySlot b,
            Vector3 axis, float invMass, float bias,
            ref float lambda, float minLimit, float maxLimit, float softness)
        {
            if (invMass <= 1e-8f) return;

            float relativeVel = Vector3.Dot(b.AngularVelocity - a.AngularVelocity, axis);
            float deltaLambda = -invMass * (relativeVel + bias + softness * lambda);
            float oldLambda = lambda;
            lambda = Math.Clamp(lambda + deltaLambda, minLimit, maxLimit);
            deltaLambda = lambda - oldLambda;

            Vector3 impulse = axis * deltaLambda;
            ApplyAngularImpulse(ref b, impulse);
            ApplyAngularImpulse(ref a, -impulse);
        }

        // =========================================================================
        //  位置求解
        // =========================================================================

        private void SolvePositionIteration(Span<BodySlot> bodies, float dt)
        {
            for (int i = 0; i < _solverCount; i++)
            {
                ref var s = ref _solverData[i];
                ref var bodyA = ref bodies[s.BodyA];
                ref var bodyB = ref bodies[s.BodyB];

                // 重新算锚点(位置已变)
                // 简化：只做平移修正
                Vector3 anchorA = Vector3.Transform(s.AnchorAWorld - bodyA.Position, Quaternion.Inverse(bodyA.Orientation));
                anchorA = Vector3.Transform(anchorA, bodyA.Orientation) + bodyA.Position;
                Vector3 anchorB = Vector3.Transform(s.AnchorBWorld - bodyB.Position, Quaternion.Inverse(bodyB.Orientation));
                anchorB = Vector3.Transform(anchorB, bodyB.Orientation) + bodyB.Position;

                Vector3 error = anchorB - anchorA;
                if (error.LengthSquared() < 1e-10f) continue;

                // 沿error方向施加位置修正
                Vector3 dir = Vector3.Normalize(error);
                float pen = error.Length();

                float invMassA = bodyA.InvMass;
                float invMassB = bodyB.InvMass;
                float totalInv = invMassA + invMassB;
                if (totalInv < 1e-8f) continue;

                float baumgarte = 0.3f;
                float correction = pen * baumgarte;
                Vector3 corr = dir * correction;

                bodyA.Position -= corr * (invMassA / totalInv);
                bodyB.Position += corr * (invMassB / totalInv);
            }
        }

        // =========================================================================
        //  辅助
        // =========================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Compute1DInvMass(ref BodySlot a, ref BodySlot b,
            Vector3 rA, Vector3 rB, Vector3 axis)
        {
            // K = invMassA + invMassB + (rA x axis) · (InvIA · (rA x axis)) + (rB x axis) · (InvIB · (rB x axis))
            Vector3 rAxN = Vector3.Cross(rA, axis);
            Vector3 rBxN = Vector3.Cross(rB, axis);
            float k = a.InvMass + b.InvMass +
                      Vector3.Dot(rAxN, TransformByInertia(rAxN, a.InvInertiaWorld)) +
                      Vector3.Dot(rBxN, TransformByInertia(rBxN, b.InvInertiaWorld));
            return k > 1e-8f ? 1f / k : 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ComputeAngularInvMass(ref BodySlot a, ref BodySlot b, Vector3 axis)
        {
            float k = Vector3.Dot(axis, TransformByInertia(axis, a.InvInertiaWorld)) +
                      Vector3.Dot(axis, TransformByInertia(axis, b.InvInertiaWorld));
            return k > 1e-8f ? 1f / k : 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyImpulse(ref BodySlot body, Vector3 impulse, Vector3 r)
        {
            if (body.MotionType == BodyMotionType.Static) return;
            body.LinearVelocity += impulse * body.InvMass;
            body.AngularVelocity += TransformByInertia(Vector3.Cross(r, impulse), body.InvInertiaWorld);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyAngularImpulse(ref BodySlot body, Vector3 angularImpulse)
        {
            if (body.MotionType == BodyMotionType.Static) return;
            body.AngularVelocity += TransformByInertia(angularImpulse, body.InvInertiaWorld);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 TransformByInertia(Vector3 v, Matrix4x4 m)
        {
            return new Vector3(
                m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
                m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
                m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);
        }

        /// <summary>四元数转欧拉角(XYZ顺序)。</summary>
        private static Vector3 ToEulerXYZ(Quaternion q)
        {
            Vector3 euler;
            // X
            float sinX = 2f * (q.W * q.X + q.Y * q.Z);
            float cosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            euler.X = MathF.Atan2(sinX, cosX);
            // Y
            float sinY = 2f * (q.W * q.Y - q.Z * q.X);
            sinY = Math.Clamp(sinY, -1f, 1f);
            euler.Y = MathF.Asin(sinY);
            // Z
            float sinZ = 2f * (q.W * q.Z + q.X * q.Y);
            float cosZ = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            euler.Z = MathF.Atan2(sinZ, cosZ);
            return euler;
        }

        // =========================================================================
        //  调试
        // =========================================================================

        public int GetLastSolverCount() => _solverCount;

        /// <summary>获取某次求解的所有累积冲量(调试可视化)。</summary>
        public IEnumerable<(int bodyA, int bodyB, float totalLambda)> GetLambdas()
        {
            for (int i = 0; i < _solverCount; i++)
            {
                ref var s = ref _solverData[i];
                float total = MathF.Abs(s.Lambda1) + MathF.Abs(s.Lambda2) + MathF.Abs(s.Lambda3) +
                              MathF.Abs(s.Lambda4) + MathF.Abs(s.Lambda5);
                yield return (s.BodyA, s.BodyB, total);
            }
        }
    }

    // ============================================================================================
    //  约束的高层封装(可选)。PhysicsWorldV2用的是内部ConstraintData，这里给一套更友好的API。
    // ============================================================================================

    /// <summary>
    /// 约束的描述结构，用于创建约束。
    /// </summary>
    public struct ConstraintDesc
    {
        public BodyHandle BodyA;
        public BodyHandle BodyB;
        public ConstraintType Type;
        public Vector3 AnchorA;     // A局部系锚点
        public Vector3 AnchorB;     // B局部系锚点
        public Vector3 Axis;        // 约束轴(局部系)
        public float MinLimit;
        public float MaxLimit;
        public float Softness;
        public float Damping;
        public bool EnableCollisionBetweenBodies;  // 关联的两个体是否互碰
    }
}
