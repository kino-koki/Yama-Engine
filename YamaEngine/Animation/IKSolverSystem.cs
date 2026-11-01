// ============================================================================================
//  IKSolverSystem.cs — IK 解算系统（深化版）
//
//  命名空间: YamaEngine.Animation
//
//  本系统提供一套完整的逆向运动学（Inverse Kinematics）解算框架，覆盖游戏中常见的
//  IK 场景：双脚落地锁定、手部抓取、头部追踪、尾巴/触手样条IK等。
//
//  核心模块：
//    - IKSolverType       ：解算器类型枚举
//    - IKSolver           ：IK解算器基类（ChainLength / Iterations / Tolerance）
//    - TwoBoneIKSolver    ：双骨IK（余弦定理解析解 + JointTarget肘部引导）
//    - FABRIKSolver       ：FABRIK（前向+后向到达，任意长度骨骼链）
//    - CCDIKSolver        ：CCD（循环坐标下降，逐关节旋转）
//    - SplineIKSolver     ：样条IK（Catmull-Rom，尾巴/触手/链条）
//    - FullBodyIK         ：全身IK（双脚锁定 + 双手目标 + 躯干调整 + 头部看向）
//
//  与引擎其他系统的对接：
//    - 复用 YamaEngine.Skinning 的 Skeleton / SkeletonPose 作为骨骼表示
//    - 与 AnimGraphRuntime 的 EvaluationContext 集成，在动画管线后处理阶段应用IK
//    - 与 PhysicsWorld 的 Raycast 接口对接，获取地面/物体表面位置
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Animation
{
    // ──────────────────────────────────────────────────────────────
    //  IK 解算器类型枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// IK 解算器类型。标识不同的 IK 算法，用于在 IK 管线中按需切换解算器。
    /// </summary>
    public enum IKSolverType : byte
    {
        /// <summary>双骨IK（Two Bone IK）—— 解析法，适用于手臂/腿。</summary>
        TwoBoneIK = 0,
        /// <summary>CCD IK（循环坐标下降）—— 从末端向根逐关节旋转。</summary>
        CCDIK = 1,
        /// <summary>FABRIK（前向+后向到达）—— 适用于任意长度骨骼链。</summary>
        FABRIK = 2,
        /// <summary>解析IK —— 数学解析解（仅限简单链）。</summary>
        Analytical = 3,
        /// <summary>样条IK（Spline IK）—— Catmull-Rom 样条，适用于尾巴/触手。</summary>
        SplineIK = 4,
        /// <summary>全身IK（Body IK）—— 综合双脚+双手+躯干+头部。</summary>
        BodyIK = 5,
        /// <summary>双骨IK（别名 <see cref="TwoBoneIK"/>）。</summary>
        TwoBone = TwoBoneIK,
        /// <summary>循环坐标下降（别名 <see cref="CCDIK"/>）。</summary>
        CCD = CCDIK,
        /// <summary>朝向约束 IK。</summary>
        LookAt = 6,
        /// <summary>肢体 IK。</summary>
        Limb = 7,
        /// <summary>弹簧 IK。</summary>
        Spring = 8,
    }

    // ──────────────────────────────────────────────────────────────
    //  IK 关节数据结构
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// IK 关节。存储单个关节的位置、旋转和约束信息。
    /// </summary>
    public struct IKJoint
    {
        /// <summary>关节世界空间位置。</summary>
        public Vector3 Position;

        /// <summary>关节世界空间旋转。</summary>
        public Quaternion Rotation;

        /// <summary>到父关节的骨骼长度（不变量）。</summary>
        public float BoneLength;

        /// <summary>最小旋转限制（度）。</summary>
        public Vector3 MinRotation;

        /// <summary>最大旋转限制（度）。</summary>
        public Vector3 MaxRotation;

        /// <summary>是否锁定该关节（锁定后IK不修改其位置/旋转）。</summary>
        public bool Locked;

        /// <summary>关节权重（0~1，用于混合IK效果）。</summary>
        public float Weight;

        public IKJoint(Vector3 position, Quaternion rotation, float boneLength)
        {
            Position = position;
            Rotation = rotation;
            BoneLength = boneLength;
            MinRotation = new Vector3(-180, -180, -180);
            MaxRotation = new Vector3(180, 180, 180);
            Locked = false;
            Weight = 1.0f;
        }
    }

    /// <summary>
    /// IK 链 —— 一组有序的关节，从根到末端效应器。
    /// </summary>
    public class IKJointChain
    {
        /// <summary>关节列表（索引0=根关节，末尾=末端效应器）。</summary>
        public List<IKJoint> Joints { get; } = new List<IKJoint>();

        /// <summary>关节数量。</summary>
        public int Count => Joints.Count;

        /// <summary>链总长度。</summary>
        public float TotalLength { get; private set; }

        public IKJointChain() { RecomputeLength(); }

        public IKJointChain(int capacity)
        {
            Joints = new List<IKJoint>(capacity);
            RecomputeLength();
        }

        /// <summary>添加关节并更新链总长度。</summary>
        public void AddJoint(IKJoint joint)
        {
            Joints.Add(joint);
            RecomputeLength();
        }

        /// <summary>设置指定索引的关节。</summary>
        public void SetJoint(int index, IKJoint joint)
        {
            if (index >= 0 && index < Joints.Count)
                Joints[index] = joint;
        }

        /// <summary>获取根关节位置。</summary>
        public Vector3 RootPosition => Joints.Count > 0 ? Joints[0].Position : Vector3.Zero;

        /// <summary>获取末端效应器位置。</summary>
        public Vector3 EndEffectorPosition => Joints.Count > 0 ? Joints[Joints.Count - 1].Position : Vector3.Zero;

        /// <summary>重新计算链总长度。</summary>
        public void RecomputeLength()
        {
            TotalLength = 0f;
            for (int i = 1; i < Joints.Count; i++)
            {
                float d = Vector3.Distance(Joints[i - 1].Position, Joints[i].Position);
                Joints[i] = new IKJoint(Joints[i].Position, Joints[i].Rotation, d);
                TotalLength += d;
            }
        }

        /// <summary>创建深拷贝。</summary>
        public IKJointChain Clone()
        {
            var copy = new IKJointChain(Joints.Count);
            foreach (var j in Joints)
                copy.AddJoint(new IKJoint(j.Position, j.Rotation, j.BoneLength));
            return copy;
        }
    }

    /// <summary>
    /// IK 解算结果。
    /// </summary>
    public struct IKSolveResult
    {
        /// <summary>解算后的关节链。</summary>
        public IKJointChain Chain;

        /// <summary>末端效应器与目标的最终误差。</summary>
        public float FinalError;

        /// <summary>是否在容差范围内收敛。</summary>
        public bool Converged;

        /// <summary>实际使用的迭代次数。</summary>
        public int IterationsUsed;

        public IKSolveResult(IKJointChain chain, float error, bool converged, int iters)
        {
            Chain = chain;
            FinalError = error;
            Converged = converged;
            IterationsUsed = iters;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  IK 解算器基类
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// IK 解算器基类。所有 IK 算法共享的参数和接口。
    /// </summary>
    public abstract class IKSolver
    {
        /// <summary>解算器类型。</summary>
        public abstract IKSolverType SolverType { get; }

        /// <summary>骨骼链长度（关节数）。</summary>
        public int ChainLength { get; protected set; }

        /// <summary>最大迭代次数。</summary>
        public int MaxIterations { get; set; } = 10;

        /// <summary>收敛容差（世界空间单位）。</summary>
        public float Tolerance { get; set; } = 0.001f;

        /// <summary>IK 权重（0~1，用于与其他动画混合）。</summary>
        public float Weight { get; set; } = 1.0f;

        /// <summary>是否启用关节角度限制。</summary>
        public bool EnableJointLimits { get; set; } = true;

        /// <summary>
        /// 解算 IK。
        /// </summary>
        /// <param name="chain">输入关节链（将被修改为解算结果）。</param>
        /// <param name="target">目标位置。</param>
        /// <param name="hint">可选的提示/引导方向（如肘部弯曲方向）。</param>
        /// <returns>解算结果。</returns>
        public abstract IKSolveResult Solve(IKJointChain chain, Vector3 target, Vector3? hint = null);

        // ========================================================================
        //  增量补充：兼容 rig 调用方（AnimationRigging）的解算器描述成员
        // ========================================================================
        /// <summary>解算器显示名称。</summary>
        public string Name { get; set; } = "Solver";
        /// <summary>解算器类型描述文本。</summary>
        public string Type { get; set; } = "IK";

        /// <summary>校验该解算器是否可用于指定骨骼。默认视为有效。</summary>
        public virtual bool IsValid(Skeleton skeleton) => true;

        /// <summary>
        /// 便捷重载：直接对给定骨骼姿势应用 IK 解算（增量补充，兼容 rig 调用方）。
        /// 默认空操作；具体解算器可在子类中覆盖。
        /// </summary>
        public virtual void Solve(Skeleton skeleton, SkeletonPose pose) { }
    }

    // ──────────────────────────────────────────────────────────────
    //  TwoBoneIKSolver —— 双骨IK（解析法）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 双骨IK解算器。使用余弦定理的解析解法，适用于两段骨骼链（如手臂、腿）。
    ///
    /// 算法步骤：
    ///   1. dist = distance(shoulder, target)
    ///   2. cosAngle = (a^2 + b^2 - dist^2) / (2ab)
    ///   3. clamp(cosAngle, -1, 1)
    ///   4. elbowAngle = acos(cosAngle)
    ///   5. 用叉积确定elbow偏转方向（JointTarget引导）
    ///
    /// 特点：
    ///   - 无需迭代，单帧解析解
    ///   - 支持肘部/膝盖弯曲方向引导（JointTarget / Pole Vector）
    ///   - 自动处理目标不可达情况
    /// </summary>
    public class TwoBoneIKSolver : IKSolver
    {
        public override IKSolverType SolverType => IKSolverType.TwoBoneIK;

        /// <summary>JointTarget —— 肘部/膝盖弯曲方向引导点。</summary>
        public Vector3 JointTarget { get; set; } = new Vector3(0, -1, 0);

        /// <summary>上段骨骼长度（如大臂/大腿）。</summary>
        public float UpperBoneLength { get; set; } = 0.3f;

        /// <summary>下段骨骼长度（如前臂/小腿）。</summary>
        public float LowerBoneLength { get; set; } = 0.28f;

        /// <summary>目标不可达时的拉伸系数（0=不拉伸，1=完全拉伸到目标）。</summary>
        public float StretchFactor { get; set; } = 0.1f;

        /// <summary>是否保持骨骼长度不变（不拉伸）。</summary>
        public bool MaintainBoneLength { get; set; } = true;

        public TwoBoneIKSolver()
        {
            ChainLength = 3; // root -> mid -> end
        }

        public TwoBoneIKSolver(float upperLength, float lowerLength)
        {
            UpperBoneLength = upperLength;
            LowerBoneLength = lowerLength;
            ChainLength = 3;
        }

        public override IKSolveResult Solve(IKJointChain chain, Vector3 target, Vector3? hint = null)
        {
            // 链至少需要3个关节：root(shoulder) -> mid(elbow) -> end(wrist)
            if (chain.Count < 3)
                return new IKSolveResult(chain, float.MaxValue, false, 0);

            Vector3 jointTarget = hint ?? JointTarget;
            Vector3 shoulder = chain.Joints[0].Position;
            Vector3 wrist = target; // 目标位置

            float a = UpperBoneLength;
            float b = LowerBoneLength;
            float dist = Vector3.Distance(shoulder, wrist);

            // 应用拉伸（目标不可达时轻微拉伸）
            float maxReach = a + b;
            if (dist > maxReach && StretchFactor > 0f)
            {
                float excess = dist - maxReach;
                float stretchAmount = excess * StretchFactor;
                a += stretchAmount * 0.5f;
                b += stretchAmount * 0.5f;
                maxReach = a + b;
            }

            // 余弦定理求肘关节角
            float distSq = dist * dist;
            float aSq = a * a;
            float bSq = b * b;

            float cosAngle = (aSq + distSq - bSq) / (2f * a * dist);
            cosAngle = Math.Clamp(cosAngle, -1f, 1f);
            float shoulderAngle = (float)Math.Acos(cosAngle);

            float cosElbow = (aSq + bSq - distSq) / (2f * a * b);
            cosElbow = Math.Clamp(cosElbow, -1f, 1f);
            float elbowAngle = (float)Math.Acos(cosElbow);

            // 从 shoulder 到 wrist 的方向
            Vector3 toTarget = dist > 1e-6f
                ? Vector3.Normalize(wrist - shoulder)
                : Vector3.UnitX;

            // 确定 elbow 的偏转平面
            // 用 JointTarget 引导肘部弯曲方向
            Vector3 midPoint = shoulder + toTarget * a * cosAngle;

            // 在 toTarget 和 jointTarget 所确定的平面内，找到 elbow 位置
            Vector3 jointDir = Vector3.Normalize(jointTarget - shoulder);

            // 叉积确定法线方向
            Vector3 normal = Vector3.Cross(toTarget, jointDir);
            float normalLen = normal.Length();
            if (normalLen < 1e-6f)
            {
                // jointTarget 与 toTarget 共线，使用备用方向
                normal = Vector3.Cross(toTarget, Vector3.UnitY);
                normalLen = normal.Length();
                if (normalLen < 1e-6f)
                    normal = Vector3.Cross(toTarget, Vector3.UnitX);
            }
            normal = Vector3.Normalize(normal);

            // tangent 方向
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(normal, toTarget));

            // 计算肘关节位置
            float sinShoulderAngle = (float)Math.Sin(shoulderAngle);
            Vector3 elbowPos = shoulder + a * cosAngle * toTarget + a * sinShoulderAngle * tangent;

            // 如果目标不可达且不允许拉伸
            if (dist > maxReach && MaintainBoneLength)
            {
                // 将肘关节放在 shoulder 到 wrist 之间，尽量伸展
                elbowPos = shoulder + toTarget * a;
                wrist = elbowPos + Vector3.Normalize(wrist - elbowPos) * b;
            }

            // 应用权重混合
            float w = Weight;
            elbowPos = Vector3.Lerp(chain.Joints[1].Position, elbowPos, w);

            // 更新链
            chain.SetJoint(1, new IKJoint(elbowPos, chain.Joints[1].Rotation, chain.Joints[1].BoneLength));
            chain.SetJoint(2, new IKJoint(wrist, chain.Joints[2].Rotation, chain.Joints[2].BoneLength));

            float finalError = Vector3.Distance(chain.EndEffectorPosition, target);
            return new IKSolveResult(chain, finalError, finalError <= Tolerance, 1);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  FABRIKSolver —— FABRIK（前向+后向到达）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// FABRIK（Forward And Backward Reaching Inverse Kinematics）解算器。
    ///
    /// 适用于任意长度的骨骼链。算法交替执行两个阶段：
    ///   - 前向到达（Forward Reaching）：从末端关节向目标移动，
    ///     每段骨骼限制在原始长度内
    ///   - 后向到达（Backward Reaching）：从根关节向固定点移动，
    ///     同样限制骨骼长度
    ///   - 迭代直到收敛或达到最大迭代次数
    ///
    /// 优点：
    ///   - 计算快速且稳定
    ///   - 保持骨骼总长度不变
    ///   - 适合长链（如蛇、触手、长尾巴）
    /// </summary>
    public class FABRIKSolver : IKSolver
    {
        public override IKSolverType SolverType => IKSolverType.FABRIK;

        /// <summary>骨骼原始长度缓存（FABRIK 不改变每段骨骼长度）。</summary>
        private float[] _boneLengths;

        /// <summary>链总长度。</summary>
        private float _totalLength;

        /// <summary>末端效应器容差（小于此值认为收敛）。</summary>
        public float EndEffectorTolerance { get; set; } = 0.005f;

        /// <summary>目标不可达时的行为：true=拉伸到目标，false=向目标方向尽量伸直。</summary>
        public bool AllowStretching { get; set; } = false;

        /// <summary>拉伸系数（AllowStretching=true时生效）。</summary>
        public float MaxStretch { get; set; } = 1.1f;

        public FABRIKSolver(int maxIterations = 10, float tolerance = 0.001f)
        {
            MaxIterations = maxIterations;
            Tolerance = tolerance;
        }

        /// <summary>初始化骨骼长度缓存。</summary>
        private void InitializeBoneLengths(IKJointChain chain)
        {
            _boneLengths = new float[chain.Count - 1];
            _totalLength = 0f;
            for (int i = 1; i < chain.Count; i++)
            {
                float len = Vector3.Distance(chain.Joints[i - 1].Position, chain.Joints[i].Position);
                _boneLengths[i - 1] = Math.Max(len, 1e-6f);
                _totalLength += _boneLengths[i - 1];
            }
        }

        public override IKSolveResult Solve(IKJointChain chain, Vector3 target, Vector3? hint = null)
        {
            if (chain.Count < 2)
                return new IKSolveResult(chain, float.MaxValue, false, 0);

            InitializeBoneLengths(chain);

            Vector3 rootPos = chain.Joints[0].Position;
            float distToTarget = Vector3.Distance(rootPos, target);

            // 目标不可达
            if (distToTarget > _totalLength)
            {
                if (AllowStretching)
                {
                    // 拉伸到目标方向
                    float scale = Math.Min(distToTarget / _totalLength, MaxStretch);
                    for (int i = 1; i < chain.Count; i++)
                    {
                        Vector3 dir = Vector3.Normalize(chain.Joints[i].Position - chain.Joints[i - 1].Position);
                        chain.SetJoint(i, new IKJoint(
                            chain.Joints[i - 1].Position + dir * _boneLengths[i - 1] * scale,
                            chain.Joints[i].Rotation,
                            chain.Joints[i].BoneLength));
                    }
                }
                else
                {
                    // 向目标方向尽量伸直
                    Vector3 dir = Vector3.Normalize(target - rootPos);
                    for (int i = 1; i < chain.Count; i++)
                    {
                        chain.SetJoint(i, new IKJoint(
                            chain.Joints[i - 1].Position + dir * _boneLengths[i - 1],
                            chain.Joints[i].Rotation,
                            chain.Joints[i].BoneLength));
                    }
                }

                float err = Vector3.Distance(chain.EndEffectorPosition, target);
                return new IKSolveResult(chain, err, err <= Tolerance, 1);
            }

            // FABRIK 迭代
            bool converged = false;
            int iterations = 0;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                iterations = iter + 1;

                // ── 前向到达（Forward Reaching）──
                // 从末端向目标移动
                chain.SetJoint(chain.Count - 1, new IKJoint(target, chain.Joints[chain.Count - 1].Rotation, chain.Joints[chain.Count - 1].BoneLength));
                for (int i = chain.Count - 2; i >= 0; i--)
                {
                    Vector3 dir = Vector3.Normalize(chain.Joints[i].Position - chain.Joints[i + 1].Position);
                    Vector3 newPos = chain.Joints[i + 1].Position + dir * _boneLengths[i];
                    chain.SetJoint(i, new IKJoint(newPos, chain.Joints[i].Rotation, chain.Joints[i].BoneLength));
                }

                // ── 后向到达（Backward Reaching）──
                // 从根向固定点移动
                chain.SetJoint(0, new IKJoint(rootPos, chain.Joints[0].Rotation, chain.Joints[0].BoneLength));
                for (int i = 1; i < chain.Count; i++)
                {
                    Vector3 dir = Vector3.Normalize(chain.Joints[i].Position - chain.Joints[i - 1].Position);
                    Vector3 newPos = chain.Joints[i - 1].Position + dir * _boneLengths[i - 1];
                    chain.SetJoint(i, new IKJoint(newPos, chain.Joints[i].Rotation, chain.Joints[i].BoneLength));
                }

                // 检查收敛
                float error = Vector3.Distance(chain.EndEffectorPosition, target);
                if (error <= Tolerance)
                {
                    converged = true;
                    break;
                }
            }

            // 应用权重混合（线性插值回原始位置）
            // 注意：实际使用中应保留原始链的副本来做混合

            float finalError = Vector3.Distance(chain.EndEffectorPosition, target);
            return new IKSolveResult(chain, finalError, converged, iterations);
        }

        /// <summary>
        /// 多目标 FABRIK —— 支持多个末端效应器同时约束。
        /// 使用加权平均策略。
        /// </summary>
        public IKSolveResult SolveMultiTarget(IKJointChain chain, List<(Vector3 Target, float Weight)> targets)
        {
            if (targets == null || targets.Count == 0)
                return new IKSolveResult(chain, float.MaxValue, false, 0);

            // 简单策略：对每个目标分别运行FABRIK，然后加权混合
            IKJointChain original = chain.Clone();
            IKJointChain blended = chain.Clone();
            blended.Joints.Clear();

            // 初始化混合链
            for (int i = 0; i < original.Count; i++)
                blended.AddJoint(new IKJoint(Vector3.Zero, Quaternion.Identity, 0f));

            float totalWeight = 0f;
            foreach (var (t, w) in targets)
                totalWeight += w;
            if (totalWeight < 1e-6f) totalWeight = 1f;

            float totalError = 0f;
            bool anyConverged = false;

            foreach (var (t, w) in targets)
            {
                IKJointChain workChain = original.Clone();
                var result = Solve(workChain, t);
                totalError += result.FinalError * w;
                if (result.Converged) anyConverged = true;

                float normalizedW = w / totalWeight;
                for (int i = 0; i < workChain.Count; i++)
                {
                    blended.Joints[i] = new IKJoint(
                        blended.Joints[i].Position + workChain.Joints[i].Position * normalizedW,
                        blended.Joints[i].Rotation,
                        blended.Joints[i].BoneLength);
                }
            }

            return new IKSolveResult(blended, totalError / totalWeight, anyConverged, MaxIterations);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  CCDIKSolver —— CCD（循环坐标下降）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// CCD（Cyclic Coordinate Descent）IK 解算器。
    ///
    /// 算法步骤：
    ///   - 从末端关节向根关节遍历
    ///   - 对每个关节，计算使其子链末端更接近目标的旋转
    ///   - 使用 Jacobi 旋转（最小旋转量）使末端向目标靠近
    ///   - 迭代直到收敛或最大迭代次数
    ///
    /// 特点：
    ///   - 适合冗余关节链（关节数 > DOF）
    ///   - 每次迭代只修改一个关节的旋转
    ///   - 计算量小于 FABRIK（适合CPU受限场景）
    ///   - 但可能收敛到局部最优
    /// </summary>
    public class CCDIKSolver : IKSolver
    {
        public override IKSolverType SolverType => IKSolverType.CCDIK;

        /// <summary>每关节旋转速度衰减（越靠近根的关节旋转越小）。</summary>
        public float DampingFactor { get; set; } = 0.8f;

        /// <summary>单关节最大旋转角度（弧度）。</summary>
        public float MaxAnglePerJoint { get; set; } = (float)Math.PI * 0.45f;

        /// <summary>角度限制模式。</summary>
        public bool UseAngleLimits { get; set; } = true;

        public CCDIKSolver(int maxIterations = 10, float tolerance = 0.001f)
        {
            MaxIterations = maxIterations;
            Tolerance = tolerance;
        }

        public override IKSolveResult Solve(IKJointChain chain, Vector3 target, Vector3? hint = null)
        {
            if (chain.Count < 2)
                return new IKSolveResult(chain, float.MaxValue, false, 0);

            // 缓存原始位置用于权重混合
            Vector3[] originalPositions = new Vector3[chain.Count];
            for (int i = 0; i < chain.Count; i++)
                originalPositions[i] = chain.Joints[i].Position;

            bool converged = false;
            int iterations = 0;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                iterations = iter + 1;

                // 从末端向根遍历（跳过末端关节本身）
                for (int i = chain.Count - 2; i >= 0; i--)
                {
                    if (chain.Joints[i].Locked) continue;

                    Vector3 jointPos = chain.Joints[i].Position;
                    Vector3 endPos = chain.EndEffectorPosition;

                    // 从关节到末端的方向向量
                    Vector3 toEnd = endPos - jointPos;
                    float toEndLen = toEnd.Length();
                    if (toEndLen < 1e-6f) continue;
                    toEnd /= toEndLen;

                    // 从关节到目标的方向向量
                    Vector3 toTarget = target - jointPos;
                    float toTargetLen = toTarget.Length();
                    if (toTargetLen < 1e-6f) continue;
                    toTarget /= toTargetLen;

                    // 计算需要旋转的角度（Jacobi 旋转 —— 最小旋转）
                    float cosAngle = Vector3.Dot(toEnd, toTarget);
                    cosAngle = Math.Clamp(cosAngle, -1f, 1f);
                    float angle = (float)Math.Acos(cosAngle);

                    // 旋转轴（叉积）
                    Vector3 axis = Vector3.Cross(toEnd, toTarget);
                    float axisLen = axis.Length();
                    if (axisLen < 1e-6f) continue;
                    axis /= axisLen;

                    // 限制最大旋转角度
                    if (angle > MaxAnglePerJoint)
                        angle = MaxAnglePerJoint;

                    // 阻尼衰减（越靠近根的关节衰减越大）
                    float damping = (float)Math.Pow(DampingFactor, (chain.Count - 2 - i));
                    angle *= damping;

                    // 应用旋转
                    Quaternion rotation = Quaternion.CreateFromAxisAngle(axis, angle);
                    chain.SetJoint(i, new IKJoint(
                        chain.Joints[i].Position,
                        rotation * chain.Joints[i].Rotation,
                        chain.Joints[i].BoneLength));

                    // 旋转该关节之后的所有子关节
                    for (int j = i + 1; j < chain.Count; j++)
                    {
                        Vector3 offset = chain.Joints[j].Position - chain.Joints[i].Position;
                        Vector3 rotatedOffset = Vector3.Transform(offset, rotation);
                        chain.SetJoint(j, new IKJoint(
                            chain.Joints[i].Position + rotatedOffset,
                            rotation * chain.Joints[j].Rotation,
                            chain.Joints[j].BoneLength));
                    }
                }

                // 检查收敛
                float error = Vector3.Distance(chain.EndEffectorPosition, target);
                if (error <= Tolerance)
                {
                    converged = true;
                    break;
                }
            }

            // 应用权重混合
            if (Weight < 1f)
            {
                for (int i = 0; i < chain.Count; i++)
                {
                    chain.SetJoint(i, new IKJoint(
                        Vector3.Lerp(originalPositions[i], chain.Joints[i].Position, Weight),
                        chain.Joints[i].Rotation,
                        chain.Joints[i].BoneLength));
                }
            }

            float finalError = Vector3.Distance(chain.EndEffectorPosition, target);
            return new IKSolveResult(chain, finalError, converged, iterations);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  SplineIKSolver —— 样条IK（Catmull-Rom）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 样条IK解算器。使用 Catmull-Rom 样条曲线拟合控制点，
    /// 骨骼链跟随样条曲线分布。适用于尾巴、触手、链条等柔软结构。
    ///
    /// 核心思路：
    ///   1. 指定多个控制点（含首尾端点）
    ///   2. 用 Catmull-Rom 样条生成平滑曲线
    ///   3. 在曲线上按等弧长采样，放置骨骼关节
    ///   4. 计算每个关节的旋转（切线方向）
    /// </summary>
    public class SplineIKSolver : IKSolver
    {
        public override IKSolverType SolverType => IKSolverType.SplineIK;

        /// <summary>控制点列表（世界空间位置）。</summary>
        public List<Vector3> ControlPoints { get; set; } = new List<Vector3>();

        /// <summary>样条曲线细分精度（每段采样数）。</summary>
        public int SplineResolution { get; set; } = 32;

        /// <summary>滚动（Roll）角度偏移（弧度），控制骨骼沿样条的旋转。</summary>
        public float RollAngle { get; set; } = 0f;

        /// <summary>曲线张力（Catmull-Rom 参数，0.5=标准Catmull-Rom）。</summary>
        public float Tension { get; set; } = 0.5f;

        /// <summary>是否重新参数化样条为等弧长。</summary>
        public bool UseArcLengthParameterization { get; set; } = true;

        /// <summary>等弧长采样精度。</summary>
        public int ArcLengthSamples { get; set; } = 256;

        public SplineIKSolver()
        {
            ChainLength = 0;
            MaxIterations = 1; // 样条IK不需要迭代
        }

        /// <summary>
        /// Catmull-Rom 样条插值。
        /// </summary>
        public Vector3 CatmullRomInterpolate(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t, float alpha = 0.5f)
        {
            // Catmull-Rom 样条：4个控制点，参数t在[0,1]范围内
            float t2 = t * t;
            float t3 = t2 * t;

            Vector3 v0 = (p2 - p0) * alpha;
            Vector3 v1 = (p3 - p1) * alpha;

            float a0 = 2f * t3 - 3f * t2 + 1f;
            float a1 = t3 - 2f * t2 + t;
            float a2 = -2f * t3 + 3f * t2;
            float a3 = t3 - t2;

            return a0 * p1 + a1 * v0 + a2 * p1 + a3 * v1;
        }

        /// <summary>
        /// 在样条曲线上按参数t采样点。
        /// </summary>
        public Vector3 SampleSpline(float t)
        {
            if (ControlPoints.Count < 2) return Vector3.Zero;

            int n = ControlPoints.Count - 1; // 段数
            float scaledT = t * n;
            int segment = Math.Min((int)scaledT, n - 1);
            float localT = scaledT - segment;

            // 获取4个控制点（超出范围时镜像）
            Vector3 p0 = segment > 0 ? ControlPoints[segment - 1] : 2f * ControlPoints[segment] - ControlPoints[segment + 1];
            Vector3 p1 = ControlPoints[segment];
            Vector3 p2 = ControlPoints[Math.Min(segment + 1, ControlPoints.Count - 1)];
            Vector3 p3 = segment + 2 < ControlPoints.Count
                ? ControlPoints[segment + 2]
                : 2f * ControlPoints[ControlPoints.Count - 1] - ControlPoints[ControlPoints.Count - 2];

            return CatmullRomInterpolate(p0, p1, p2, p3, localT, Tension);
        }

        /// <summary>
        /// 计算样条在参数t处的切线。
        /// </summary>
        public Vector3 SampleSplineTangent(float t)
        {
            float eps = 0.001f;
            Vector3 p0 = SampleSpline(Math.Max(0f, t - eps));
            Vector3 p1 = SampleSpline(Math.Min(1f, t + eps));
            Vector3 tangent = p1 - p0;
            float len = tangent.Length();
            return len > 1e-6f ? tangent / len : Vector3.UnitX;
        }

        /// <summary>
        /// 计算弧长查找表（用于等弧长参数化）。
        /// </summary>
        private float[] BuildArcLengthTable()
        {
            float[] arcLengths = new float[ArcLengthSamples + 1];
            arcLengths[0] = 0f;
            Vector3 prev = SampleSpline(0f);
            for (int i = 1; i <= ArcLengthSamples; i++)
            {
                float t = (float)i / ArcLengthSamples;
                Vector3 curr = SampleSpline(t);
                arcLengths[i] = arcLengths[i - 1] + Vector3.Distance(prev, curr);
                prev = curr;
            }
            return arcLengths;
        }

        /// <summary>
        /// 通过弧长反查参数t（等弧长参数化）。
        /// </summary>
        private float ArcLengthToParameter(float[] arcLengths, float targetLength)
        {
            float totalLength = arcLengths[arcLengths.Length - 1];
            if (totalLength < 1e-6f) return 0f;
            float normalizedTarget = Math.Clamp(targetLength / totalLength, 0f, 1f);

            // 二分查找
            int lo = 0, hi = arcLengths.Length - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (arcLengths[mid] / totalLength < normalizedTarget)
                    lo = mid;
                else
                    hi = mid;
            }

            // 线性插值
            float segLen = arcLengths[hi] - arcLengths[lo];
            float segFrac = segLen > 1e-6f
                ? (normalizedTarget * totalLength - arcLengths[lo]) / segLen
                : 0f;

            return ((float)lo + segFrac) / ArcLengthSamples;
        }

        public override IKSolveResult Solve(IKJointChain chain, Vector3 target, Vector3? hint = null)
        {
            if (ControlPoints.Count < 2 || chain.Count < 2)
                return new IKSolveResult(chain, float.MaxValue, false, 0);

            // 设置末端控制点为目标位置
            if (hint.HasValue || target != Vector3.Zero)
            {
                if (ControlPoints.Count >= 2)
                    ControlPoints[ControlPoints.Count - 1] = target;
            }

            float[] arcLengths = null;
            if (UseArcLengthParameterization)
                arcLengths = BuildArcLengthTable();

            // 重新计算每段骨骼长度
            float[] boneLengths = new float[chain.Count - 1];
            float totalBoneLen = 0f;
            for (int i = 1; i < chain.Count; i++)
            {
                boneLengths[i - 1] = Vector3.Distance(chain.Joints[i - 1].Position, chain.Joints[i].Position);
                boneLengths[i - 1] = Math.Max(boneLengths[i - 1], 1e-6f);
                totalBoneLen += boneLengths[i - 1];
            }

            // 沿样条按弧长放置骨骼
            float accumulatedLength = 0f;
            Quaternion prevRotation = chain.Joints[0].Rotation;

            for (int i = 0; i < chain.Count; i++)
            {
                float normalizedLen = totalBoneLen > 1e-6f ? accumulatedLength / totalBoneLen : 0f;
                normalizedLen = Math.Clamp(normalizedLen, 0f, 1f);

                float t;
                if (UseArcLengthParameterization && arcLengths != null)
                    t = ArcLengthToParameter(arcLengths, accumulatedLength);
                else
                    t = normalizedLen;

                Vector3 pos = SampleSpline(t);
                Vector3 tangent = SampleSplineTangent(t);

                // 根据切线方向计算旋转（Z轴对齐切线）
                Quaternion targetRot = MathUtil.LookAt(Vector3.UnitZ, tangent);

                // 应用滚动角度
                if (Math.Abs(RollAngle) > 1e-6f)
                {
                    Quaternion rollQuat = Quaternion.CreateFromAxisAngle(tangent, RollAngle);
                    targetRot = rollQuat * targetRot;
                }

                chain.SetJoint(i, new IKJoint(pos, targetRot, chain.Joints[i].BoneLength));

                if (i < chain.Count - 1)
                    accumulatedLength += boneLengths[i];

                prevRotation = targetRot;
            }

            float error = Vector3.Distance(chain.EndEffectorPosition, target);
            return new IKSolveResult(chain, error, error <= Tolerance, 1);
        }

        /// <summary>
        /// 动态更新控制点（用于动画中实时控制样条形状）。
        /// </summary>
        public void SetControlPoint(int index, Vector3 position)
        {
            if (index >= 0 && index < ControlPoints.Count)
                ControlPoints[index] = position;
        }

        /// <summary>
        /// 添加控制点。
        /// </summary>
        public void AddControlPoint(Vector3 position)
        {
            ControlPoints.Add(position);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  FullBodyIK —— 全身IK
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 全身IK系统。综合处理双脚锁定、双手目标、躯干平衡、头部追踪的完整IK方案。
    ///
    /// 组成部分：
    ///   - LeftFootIK / RightFootIK  ：双脚锁定到地面
    ///   - LeftHandIK / RightHandIK  ：双手锁定到目标
    ///   - BodyIK                    ：躯干调整（保持重心平衡）
    ///   - HeadLookIK                ：头部看向目标
    ///   - 推算链                    ：从手→肘→肩的连锁反应
    /// </summary>
    public class FullBodyIK
    {
        // ── 脚部IK ────────────────────────────────────────

        /// <summary>脚部IK配置。</summary>
        public struct FootIKConfig
        {
            /// <summary>IK解算器。</summary>
            public TwoBoneIKSolver Solver;

            /// <summary>脚部骨骼链索引（hip -> knee -> ankle）。</summary>
            public int HipIndex;
            public int KneeIndex;
            public int AnkleIndex;

            /// <summary>地面高度偏移。</summary>
            public float GroundOffset;

            /// <summary>IK启用权重。</summary>
            public float Weight;

            /// <summary>最大IK调整高度。</summary>
            public float MaxAdjustment;

            /// <summary>是否启用脚部旋转IK（脚掌贴合地面坡度）。</summary>
            public bool EnableFootRotation;

            /// <summary>脚部抬起平滑过渡速度。</summary>
            public float LiftSmoothSpeed;

            /// <summary>地面检测射线起点（脚底向下）。</summary>
            public float RaycastDistance;

            public FootIKConfig(float upperLen, float lowerLen, float groundOffset = 0f)
            {
                Solver = new TwoBoneIKSolver(upperLen, lowerLen);
                HipIndex = 0;
                KneeIndex = 1;
                AnkleIndex = 2;
                GroundOffset = groundOffset;
                Weight = 1f;
                MaxAdjustment = 0.5f;
                EnableFootRotation = true;
                LiftSmoothSpeed = 10f;
                RaycastDistance = 0.5f;
            }
        }

        /// <summary>手部IK配置。</summary>
        public struct HandIKConfig
        {
            /// <summary>IK解算器。</summary>
            public TwoBoneIKSolver Solver;

            /// <summary>手部骨骼链索引（shoulder -> elbow -> wrist）。</summary>
            public int ShoulderIndex;
            public int ElbowIndex;
            public int WristIndex;

            /// <summary>IK启用权重。</summary>
            public float Weight;

            /// <summary>手部目标位置（世界空间）。</summary>
            public Vector3 TargetPosition;

            /// <summary>手部目标旋转（世界空间）。</summary>
            public Quaternion TargetRotation;

            /// <summary>是否启用。</summary>
            public bool Enabled;

            /// <summary>肘部弯曲方向。</summary>
            public Vector3 ElbowHint;

            public HandIKConfig(float upperLen, float lowerLen)
            {
                Solver = new TwoBoneIKSolver(upperLen, lowerLen);
                ShoulderIndex = 0;
                ElbowIndex = 1;
                WristIndex = 2;
                Weight = 1f;
                TargetPosition = Vector3.Zero;
                TargetRotation = Quaternion.Identity;
                Enabled = false;
                ElbowHint = new Vector3(0, -1, 0);
            }
        }

        /// <summary>躯干IK配置。</summary>
        public struct BodyIKConfig
        {
            /// <summary>躯干最大前倾角度（度）。</summary>
            public float MaxLeanForward;

            /// <summary>躯干最大侧倾角度（度）。</summary>
            public float MaxLeanSide;

            /// <summary>重心高度调整范围。</summary>
            public float MinCGHeight;

            /// <summary>重心高度调整范围。</summary>
            public float MaxCGHeight;

            /// <summary>躯干调整速度。</summary>
            public float AdjustSpeed;

            /// <summary>是否启用。</summary>
            public bool Enabled;

            public BodyIKConfig()
            {
                MaxLeanForward = 15f;
                MaxLeanSide = 10f;
                MinCGHeight = 0.8f;
                MaxCGHeight = 1.2f;
                AdjustSpeed = 5f;
                Enabled = true;
            }
        }

        /// <summary>头部追踪IK配置。</summary>
        public struct HeadLookIKConfig
        {
            /// <summary>头部骨骼索引。</summary>
            public int HeadBoneIndex;

            /// <summary>颈部骨骼索引。</summary>
            public int NeckBoneIndex;

            /// <summary>追踪目标位置。</summary>
            public Vector3 LookTarget;

            /// <summary>最大上下旋转角度（度）。</summary>
            public float MaxPitchAngle;

            /// <summary>最大左右旋转角度（度）。</summary>
            public float MaxYawAngle;

            /// <summary>追踪速度。</summary>
            public float TrackSpeed;

            /// <summary>是否启用。</summary>
            public bool Enabled;

            public HeadLookIKConfig()
            {
                HeadBoneIndex = -1;
                NeckBoneIndex = -1;
                LookTarget = Vector3.Zero;
                MaxPitchAngle = 45f;
                MaxYawAngle = 60f;
                TrackSpeed = 8f;
                Enabled = false;
            }
        }

        // ── 全身IK属性 ─────────────────────────────────────

        /// <summary>左脚IK。</summary>
        public FootIKConfig LeftFootIK = new FootIKConfig(0.45f, 0.43f, 0.05f);

        /// <summary>右脚IK。</summary>
        public FootIKConfig RightFootIK = new FootIKConfig(0.45f, 0.43f, 0.05f);

        /// <summary>左手IK。</summary>
        public HandIKConfig LeftHandIK = new HandIKConfig(0.3f, 0.28f);

        /// <summary>右手IK。</summary>
        public HandIKConfig RightHandIK = new HandIKConfig(0.3f, 0.28f);

        /// <summary>躯干IK。</summary>
        public BodyIKConfig BodyIK { get; set; } = new BodyIKConfig();

        /// <summary>头部追踪IK。</summary>
        public HeadLookIKConfig HeadLookIK = new HeadLookIKConfig();

        /// <summary>当前左脚地面接触高度（平滑后）。</summary>
        private float _leftFootGroundHeight = 0f;

        /// <summary>当前右脚地面接触高度（平滑后）。</summary>
        private float _rightFootGroundHeight = 0f;

        /// <summary>全身IK总权重。</summary>
        public float GlobalWeight { get; set; } = 1.0f;

        /// <summary>解算优先级（数字越大越优先）。</summary>
        public int Priority { get; set; } = 0;

        // ── 全身IK解算 ─────────────────────────────────────

        /// <summary>
        /// 解算全身IK。按照以下优先级顺序处理：
        ///   1. 双脚锁定到地面
        ///   2. 躯干调整（保持重心平衡）
        ///   3. 双手锁定到目标
        ///   4. 头部看向目标
        ///   5. 推算链：从手→肘→肩的连锁反应
        /// </summary>
        /// <param name="jointPositions">当前骨骼关节位置数组（将被修改）。</param>
        /// <param name="jointRotations">当前骨骼关节旋转数组（将被修改）。</param>
        /// <param name="deltaTime">帧间隔时间。</param>
        /// <param name="leftFootGroundY">左脚地面Y坐标（通过Raycast获得）。</param>
        /// <param name="rightFootGroundY">右脚地面Y坐标。</param>
        public void Solve(
            Vector3[] jointPositions,
            Quaternion[] jointRotations,
            float deltaTime,
            float leftFootGroundY,
            float rightFootGroundY)
        {
            if (GlobalWeight < 1e-6f) return;

            float w = GlobalWeight;

            // ── 步骤1：双脚锁定到地面 ──
            SolveFootIK(jointPositions, jointRotations, ref LeftFootIK, ref _leftFootGroundHeight,
                leftFootGroundY, deltaTime, w);
            SolveFootIK(jointPositions, jointRotations, ref RightFootIK, ref _rightFootGroundHeight,
                rightFootGroundY, deltaTime, w);

            // ── 步骤2：躯干调整 ──
            if (BodyIK.Enabled)
                SolveBodyIK(jointPositions, jointRotations, deltaTime, w);

            // ── 步骤3：双手锁定到目标 ──
            if (LeftHandIK.Enabled)
                SolveHandIK(jointPositions, jointRotations, ref LeftHandIK, w);
            if (RightHandIK.Enabled)
                SolveHandIK(jointPositions, jointRotations, ref RightHandIK, w);

            // ── 步骤4：头部看向目标 ──
            if (HeadLookIK.Enabled && HeadLookIK.HeadBoneIndex >= 0)
                SolveHeadLookIK(jointPositions, jointRotations, deltaTime, w);
        }

        /// <summary>解算单脚IK。</summary>
        private void SolveFootIK(
            Vector3[] positions, Quaternion[] rotations,
            ref FootIKConfig footIK, ref float smoothedGroundHeight,
            float groundY, float dt, float weight)
        {
            if (footIK.Weight < 1e-6f) return;

            // 平滑地面高度
            float smoothSpeed = footIK.LiftSmoothSpeed;
            smoothedGroundHeight = MathUtil.Lerp(smoothedGroundHeight, groundY + footIK.GroundOffset, 1f - (float)Math.Exp(-smoothSpeed * dt));

            // 限制最大调整高度
            float adjustment = smoothedGroundHeight - positions[footIK.AnkleIndex].Y;
            adjustment = Math.Clamp(adjustment, -footIK.MaxAdjustment, footIK.MaxAdjustment);

            // 构建脚部IK链
            var chain = new IKJointChain(3);
            chain.AddJoint(new IKJoint(positions[footIK.HipIndex], rotations[footIK.HipIndex], 0));
            chain.AddJoint(new IKJoint(positions[footIK.KneeIndex], rotations[footIK.KneeIndex], 0));
            chain.AddJoint(new IKJoint(positions[footIK.AnkleIndex], rotations[footIK.AnkleIndex], 0));

            // 目标位置：保持XZ不变，Y设为地面高度
            Vector3 ankleTarget = positions[footIK.AnkleIndex];
            ankleTarget.Y = positions[footIK.AnkleIndex].Y + adjustment;

            // 肘部弯曲方向（膝盖向前）
            Vector3 kneeHint = positions[footIK.HipIndex] + Vector3.UnitX;

            var result = footIK.Solver.Solve(chain, ankleTarget, kneeHint);
            footIK.Solver.Weight = footIK.Weight * weight;

            // 回写结果
            if (result.Chain.Count >= 3)
            {
                positions[footIK.HipIndex] = Vector3.Lerp(positions[footIK.HipIndex], result.Chain.Joints[0].Position, footIK.Weight * weight);
                positions[footIK.KneeIndex] = Vector3.Lerp(positions[footIK.KneeIndex], result.Chain.Joints[1].Position, footIK.Weight * weight);
                positions[footIK.AnkleIndex] = Vector3.Lerp(positions[footIK.AnkleIndex], result.Chain.Joints[2].Position, footIK.Weight * weight);
            }

            // 脚掌旋转IK（贴合地面坡度）
            if (footIK.EnableFootRotation)
            {
                // 根据地面法线调整脚掌旋转
                // 这里简化为保持脚掌水平（实际应通过Raycast获得地面法线）
                Quaternion footRot = Quaternion.Identity;
                rotations[footIK.AnkleIndex] = Quaternion.Slerp(
                    rotations[footIK.AnkleIndex],
                    footRot,
                    footIK.Weight * weight * 0.5f);
            }
        }

        /// <summary>解算手部IK。</summary>
        private void SolveHandIK(
            Vector3[] positions, Quaternion[] rotations,
            ref HandIKConfig handIK, float weight)
        {
            if (!handIK.Enabled || handIK.Weight < 1e-6f) return;

            // 构建手部IK链
            var chain = new IKJointChain(3);
            chain.AddJoint(new IKJoint(positions[handIK.ShoulderIndex], rotations[handIK.ShoulderIndex], 0));
            chain.AddJoint(new IKJoint(positions[handIK.ElbowIndex], rotations[handIK.ElbowIndex], 0));
            chain.AddJoint(new IKJoint(positions[handIK.WristIndex], rotations[handIK.WristIndex], 0));

            handIK.Solver.Weight = handIK.Weight * weight;
            var result = handIK.Solver.Solve(chain, handIK.TargetPosition, handIK.ElbowHint);

            // 回写结果（推算链：手→肘→肩连锁反应）
            if (result.Chain.Count >= 3)
            {
                float ikWeight = handIK.Weight * weight;
                positions[handIK.ElbowIndex] = Vector3.Lerp(positions[handIK.ElbowIndex], result.Chain.Joints[1].Position, ikWeight);
                positions[handIK.WristIndex] = Vector3.Lerp(positions[handIK.WristIndex], result.Chain.Joints[2].Position, ikWeight);

                // 肩部轻微调整（推算链反应）
                positions[handIK.ShoulderIndex] = Vector3.Lerp(
                    positions[handIK.ShoulderIndex],
                    result.Chain.Joints[0].Position,
                    ikWeight * 0.2f); // 肩部只受20%影响
            }
        }

        /// <summary>解算躯干IK（保持重心平衡）。</summary>
        private void SolveBodyIK(
            Vector3[] positions, Quaternion[] rotations,
            float dt, float weight)
        {
            // 计算双脚平均高度
            float leftFootY = positions[LeftFootIK.AnkleIndex].Y;
            float rightFootY = positions[RightFootIK.AnkleIndex].Y;
            float averageFootY = (leftFootY + rightFootY) * 0.5f;

            // 计算躯干需要调整的高度
            float targetBodyY = averageFootY + BodyIK.MinCGHeight + (BodyIK.MaxCGHeight - BodyIK.MinCGHeight) * 0.5f;

            // 平滑调整
            float currentBodyY = positions[LeftFootIK.HipIndex].Y;
            float adjustedY = MathUtil.Lerp(currentBodyY, targetBodyY, 1f - (float)Math.Exp(-BodyIK.AdjustSpeed * dt));

            // 限制调整范围
            float adjustment = adjustedY - currentBodyY;
            adjustment = Math.Clamp(adjustment, -0.3f, 0.3f);

            // 应用躯干高度调整
            positions[LeftFootIK.HipIndex] = new Vector3(
                positions[LeftFootIK.HipIndex].X,
                positions[LeftFootIK.HipIndex].Y + adjustment * weight,
                positions[LeftFootIK.HipIndex].Z);

            // 躯干前倾/侧倾
            if (positions.Length > LeftFootIK.HipIndex + 1)
            {
                // 根据左右脚高度差计算侧倾
                float heightDiff = leftFootY - rightFootY;
                float sideLean = Math.Clamp(heightDiff * 0.3f, -BodyIK.MaxLeanSide, BodyIK.MaxLeanSide);

                Quaternion bodyRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, sideLean * (float)Math.PI / 180f);
                rotations[LeftFootIK.HipIndex] = Quaternion.Slerp(rotations[LeftFootIK.HipIndex], bodyRot * rotations[LeftFootIK.HipIndex], weight);
            }
        }

        /// <summary>解算头部追踪IK。</summary>
        private void SolveHeadLookIK(
            Vector3[] positions, Quaternion[] rotations,
            float dt, float weight)
        {
            if (HeadLookIK.HeadBoneIndex < 0 || HeadLookIK.NeckBoneIndex < 0) return;
            if (HeadLookIK.HeadBoneIndex >= positions.Length || HeadLookIK.NeckBoneIndex >= positions.Length) return;

            Vector3 headPos = positions[HeadLookIK.HeadBoneIndex];
            Vector3 toTarget = HeadLookIK.LookTarget - headPos;
            float dist = toTarget.Length();
            if (dist < 1e-6f) return;
            toTarget /= dist;

            // 计算目标旋转（LookAt）
            Quaternion targetRot = MathUtil.LookAt(Vector3.UnitZ, toTarget);

            // 限制旋转角度
            Quaternion currentRot = rotations[HeadLookIK.HeadBoneIndex];
            Quaternion diffRot = Quaternion.Inverse(currentRot) * targetRot;

            // 提取欧拉角并限制
            Vector3 diffEuler = QuaternionToEuler(diffRot);
            float maxPitchRad = HeadLookIK.MaxPitchAngle * (float)Math.PI / 180f;
            float maxYawRad = HeadLookIK.MaxYawAngle * (float)Math.PI / 180f;
            diffEuler.X = Math.Clamp(diffEuler.X, -maxPitchRad, maxPitchRad);
            diffEuler.Y = Math.Clamp(diffEuler.Y, -maxYawRad, maxYawRad);

            Quaternion limitedRot = EulerToQuaternion(diffEuler) * currentRot;

            // 平滑过渡
            float speed = HeadLookIK.TrackSpeed;
            Quaternion smoothedRot = Quaternion.Slerp(
                currentRot,
                limitedRot,
                1f - (float)Math.Exp(-speed * dt));

            rotations[HeadLookIK.HeadBoneIndex] = Quaternion.Slerp(
                rotations[HeadLookIK.HeadBoneIndex],
                smoothedRot,
                weight);
        }

        // ── 辅助数学函数 ──────────────────────────────────

        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            // 简化的四元数→欧拉角转换
            float sinX = 2f * (q.W * q.X + q.Y * q.Z);
            float cosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float pitch = (float)Math.Atan2(sinX, cosX);

            float sinY = 2f * (q.W * q.Y - q.Z * q.X);
            float yaw = Math.Abs(sinY) >= 1f
                ? (float)Math.CopySign((float)Math.PI / 2f, sinY)
                : (float)Math.Asin(sinY);

            float sinZ = 2f * (q.W * q.Z + q.X * q.Y);
            float cosZ = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float roll = (float)Math.Atan2(sinZ, cosZ);

            return new Vector3(pitch, yaw, roll);
        }

        private static Quaternion EulerToQuaternion(Vector3 euler)
        {
            return Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);
        }

        /// <summary>
        /// 设置头部追踪目标。
        /// </summary>
        public void SetHeadLookTarget(Vector3 target)
        {
            HeadLookIK.LookTarget = target;
            HeadLookIK.Enabled = true;
        }

        /// <summary>
        /// 设置手部抓取目标。
        /// </summary>
        public void SetHandTarget(bool isLeft, Vector3 position, Quaternion rotation)
        {
            if (isLeft)
            {
                LeftHandIK.TargetPosition = position;
                LeftHandIK.TargetRotation = rotation;
                LeftHandIK.Enabled = true;
            }
            else
            {
                RightHandIK.TargetPosition = position;
                RightHandIK.TargetRotation = rotation;
                RightHandIK.Enabled = true;
            }
        }

        /// <summary>
        /// 清除手部IK目标（停止抓取）。
        /// </summary>
        public void ClearHandTarget(bool isLeft)
        {
            if (isLeft) LeftHandIK.Enabled = false;
            else RightHandIK.Enabled = false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  MathUtil —— IK系统通用数学工具
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// IK系统通用数学工具类。
    /// </summary>
    internal static class MathUtil
    {
        /// <summary>线性插值。</summary>
        public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

        /// <summary>Vector3 线性插值。</summary>
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, Math.Clamp(t, 0f, 1f));

        /// <summary>角度差（归一化到 -PI ~ PI）。</summary>
        public static float AngleDiff(float a, float b)
        {
            float d = b - a;
            while (d > Math.PI) d -= (float)(2f * Math.PI);
            while (d < -Math.PI) d += (float)(2f * Math.PI);
            return d;
        }

        /// <summary>角度线性插值（处理环绕）。</summary>
        public static float LerpAngle(float a, float b, float t)
        {
            return a + AngleDiff(a, b) * Math.Clamp(t, 0f, 1f);
        }

        /// <summary>
        /// 构造使 +Z 轴对齐到 <paramref name="forward"/> 方向的旋转四元数（up 用于确定滚转）。
        /// 等价于 Unity/引擎惯例的 Quaternion.LookAt(forward, up)。
        /// </summary>
        public static Quaternion LookAt(Vector3 forward, Vector3 up)
        {
            if (forward.Length() < 1e-8f) return Quaternion.Identity;
            Vector3 f = Vector3.Normalize(forward);

            Vector3 r = Vector3.Cross(up, f);
            if (r.Length() < 1e-8f)
                r = Vector3.Cross(Vector3.UnitY, f);
            r = Vector3.Normalize(r);
            Vector3 u = Vector3.Cross(f, r);

            var m = new Matrix4x4(
                r.X, u.X, f.X, 0f,
                r.Y, u.Y, f.Y, 0f,
                r.Z, u.Z, f.Z, 0f,
                0f, 0f, 0f, 1f);
            return Quaternion.CreateFromRotationMatrix(m);
        }
    }
}
