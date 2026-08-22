using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  RigidBodyDynamics.cs
    //
    //  刚体动力学的数学核心。PhysicsWorldV2里的BodySlot是运行时数据，这里提供更完整的
    //  刚体物理计算：质量属性计算、惯性张量、各种积分器、力/扭矩的应用等。
    //
    //  核心方程：牛顿-欧拉方程
    //    线性: F = m * a,    v' = v + (F/m) * dt,    x' = x + v * dt
    //    角度: tau = I * alpha + omega x (I * omega)   (刚体在3D下要加科里奥利项)
    //
    //  惯性张量I是3x3矩阵，描述物体对绕不同轴旋转的"抵抗力"。形状不同I就不同。
    //
    //  注意：质量不能为0，用invMass=0表示静态体(无穷大质量)
    //  TODO: 椭球体的惯性张量公式没写，常用但容易记错
    // ============================================================================================

    /// <summary>
    /// 刚体动力学的纯数学计算工具。无状态，全静态方法。
    /// </summary>
    public static class RigidBodyDynamics
    {
        // =========================================================================
        //  惯性张量计算
        //  各种几何形状的局部系惯性张量(假设均匀密度，原点在质心)
        // =========================================================================

        /// <summary>实心球的惯性张量。I = (2/5) * m * r^2 * 单位阵。</summary>
        public static Matrix4x4 SphereInertia(float mass, float radius)
        {
            // 注意：质量不能为0，但这里只是算张量，invMass=0表示无穷大质量在别处处理
            float i = 0.4f * mass * radius * radius;
            return new Matrix4x4(
                i, 0, 0, 0,
                0, i, 0, 0,
                0, 0, i, 0,
                0, 0, 0, 1);
        }

        /// <summary>实心长方体的惯性张量。假设长宽高分别是hx*2, hy*2, hz*2(半尺寸)。</summary>
        public static Matrix4x4 BoxInertia(float mass, Vector3 halfExtents)
        {
            float hx = halfExtents.X, hy = halfExtents.Y, hz = halfExtents.Z;
            float ix = (1f / 3f) * mass * (hy * hy + hz * hz);
            float iy = (1f / 3f) * mass * (hx * hx + hz * hz);
            float iz = (1f / 3f) * mass * (hx * hx + hy * hy);
            return new Matrix4x4(
                ix, 0, 0, 0,
                0, iy, 0, 0,
                0, 0, iz, 0,
                0, 0, 0, 1);
        }

        /// <summary>实心圆柱(沿Y轴)的惯性张量。</summary>
        public static Matrix4x4 CylinderInertia(float mass, float radius, float height)
        {
            float r2 = radius * radius;
            float h2 = height * height;
            // 沿轴向(Y)
            float iy = 0.5f * mass * r2;
            // 垂直轴向(X/Z)
            float ixz = (1f / 12f) * mass * (3f * r2 + h2);
            return new Matrix4x4(
                ixz, 0, 0, 0,
                0, iy, 0, 0,
                0, 0, ixz, 0,
                0, 0, 0, 1);
        }

        /// <summary>胶囊(沿Y轴)的惯性张量。近似为圆柱+两个半球。</summary>
        public static Matrix4x4 CapsuleInertia(float mass, float radius, float height)
        {
            // 总长height，包含两端的半球
            float cylinderLen = MathF.Max(0, height - 2f * radius);
            float r2 = radius * radius;

            // 简化：把质量按体积分配到圆柱和两个半球
            float volCyl = MathF.PI * r2 * cylinderLen;
            float volSphere = (4f / 3f) * MathF.PI * r2 * radius;
            float volTotal = volCyl + volSphere;
            if (volTotal < 1e-8f) return Matrix4x4.Identity;

            float massCyl = mass * volCyl / volTotal;
            float massSph = mass * volSphere / volTotal;

            // 圆柱部分
            float iyCyl = 0.5f * massCyl * r2;
            float ixzCyl = (1f / 12f) * massCyl * (3f * r2 + cylinderLen * cylinderLen);

            // 两个半球部分(简化为整球)
            float iySph = 0.4f * massSph * r2;
            float ixzSph = 0.4f * massSph * r2;
            // 平行轴定理: I = I_cm + m * d^2，半球中心距原点(cylinderLen/2 + radius*某系数)
            float offset = cylinderLen * 0.5f;
            ixzSph += massSph * offset * offset;

            float iy = iyCyl + iySph;
            float ixz = ixzCyl + ixzSph;
            return new Matrix4x4(
                ixz, 0, 0, 0,
                0, iy, 0, 0,
                0, 0, ixz, 0,
                0, 0, 0, 1);
        }

        /// <summary>
        /// 凸包的惯性张量。用顶点列表通过四面体分解积分。
        /// 这个算法比较复杂，简化版用包围盒近似。
        /// </summary>
        public static Matrix4x4 ConvexHullInertia(float mass, ReadOnlySpan<Vector3> vertices)
        {
            // 简化：用AABB近似
            Vector3 min = Vector3.Zero, max = Vector3.Zero;
            if (vertices.Length > 0)
            {
                min = max = vertices[0];
                for (int i = 1; i < vertices.Length; i++)
                {
                    min = Vector3.Min(min, vertices[i]);
                    max = Vector3.Max(max, vertices[i]);
                }
            }
            Vector3 halfExtents = (max - min) * 0.5f;
            return BoxInertia(mass, halfExtents);
            // TODO: 精确计算用四面体分解
        }

        /// <summary>求逆矩阵。如果不可逆返回单位阵。</summary>
        public static Matrix4x4 InvertOrIdentity(Matrix4x4 m)
        {
            if (Matrix4x4.Invert(m, out var inv)) return inv;
            return Matrix4x4.Identity;
        }

        /// <summary>把局部系惯性张量转到世界系: Iw = R * Il * R^T</summary>
        public static Matrix4x4 TransformInertiaToWorld(Matrix4x4 localInertia, Quaternion orientation)
        {
            var R = Matrix4x4.CreateFromQuaternion(orientation);
            var Rt = Matrix4x4.Transpose(R);
            return Matrix4x4.Multiply(Matrix4x4.Multiply(R, localInertia), Rt);
        }

        // =========================================================================
        //  积分器
        // =========================================================================

        /// <summary>
        /// 半隐式欧拉积分(Symplectic Euler)。
        /// 先更新速度再更新位置。比显式欧拉稳定，比RK4快。
        /// 大多数游戏物理引擎默认用这个。
        /// </summary>
        public static void IntegrateSemiImplicitEuler(ref Vector3 position, ref Quaternion orientation,
            ref Vector3 linearVelocity, ref Vector3 angularVelocity,
            Vector3 force, Vector3 torque, float invMass, Matrix4x4 invInertiaWorld,
            float dt, float linearDamping, float angularDamping)
        {
            // 速度更新
            linearVelocity += force * invMass * dt;
            angularVelocity += TransformByInertia(torque, invInertiaWorld) * dt;

            // 阻尼
            ApplyDamping(ref linearVelocity, ref angularVelocity, linearDamping, angularDamping, dt);

            // 位置更新
            position += linearVelocity * dt;
            IntegrateOrientation(ref orientation, angularVelocity, dt);
        }

        /// <summary>
        /// 显式欧拉积分。简单但不稳定，能量会逐渐增加。只用于教学或低速场景。
        /// </summary>
        public static void IntegrateExplicitEuler(ref Vector3 position, ref Quaternion orientation,
            ref Vector3 linearVelocity, ref Vector3 angularVelocity,
            Vector3 force, Vector3 torque, float invMass, Matrix4x4 invInertiaWorld,
            float dt, float linearDamping, float angularDamping)
        {
            // 用旧速度更新位置
            position += linearVelocity * dt;
            IntegrateOrientation(ref orientation, angularVelocity, dt);

            // 然后更新速度
            linearVelocity += force * invMass * dt;
            angularVelocity += TransformByInertia(torque, invInertiaWorld) * dt;
            ApplyDamping(ref linearVelocity, ref angularVelocity, linearDamping, angularDamping, dt);
        }

        /// <summary>
        /// Verlet积分(速度Verlet版本)。比欧拉稳定得多，常用于布料/分子动力学。
        /// </summary>
        public static void IntegrateVerlet(ref Vector3 position, ref Vector3 velocity,
            Vector3 acceleration, float dt, float damping)
        {
            // x(t+dt) = x(t) + v(t)*dt + 0.5*a(t)*dt^2
            // v(t+dt) = v(t) + 0.5*(a(t) + a(t+dt))*dt
            // 注意：需要a(t+dt)，所以这里只能用近似(假设a不变)
            position += velocity * dt + 0.5f * acceleration * dt * dt;
            velocity += acceleration * dt;
            velocity *= MathF.Pow(1f - damping, dt);
        }

        /// <summary>
        /// RK4积分(Runge-Kutta 4)。精度最高但代价大，每步要算4次导数。
        /// 一般不用于实时物理，但精度敏感场景(轨道力学)会用。
        /// </summary>
        public static void IntegrateRK4(ref Vector3 position, ref Vector3 velocity,
            Func<Vector3, Vector3, Vector3> accelFunc, float dt)
        {
            Vector3 k1v = accelFunc(position, velocity);
            Vector3 k1x = velocity;

            Vector3 k2v = accelFunc(position + k1x * dt * 0.5f, velocity + k1v * dt * 0.5f);
            Vector3 k2x = velocity + k1v * dt * 0.5f;

            Vector3 k3v = accelFunc(position + k2x * dt * 0.5f, velocity + k2v * dt * 0.5f);
            Vector3 k3x = velocity + k2v * dt * 0.5f;

            Vector3 k4v = accelFunc(position + k3x * dt, velocity + k3v * dt);
            Vector3 k4x = velocity + k3v * dt;

            position += (k1x + 2f * k2x + 2f * k3x + k4x) * (dt / 6f);
            velocity += (k1v + 2f * k2v + 2f * k3v + k4v) * (dt / 6f);
        }

        /// <summary>
        /// 用角速度积分朝向四元数。
        /// q' = q + 0.5 * omega * q * dt (注意是四元数乘法)
        /// 然后归一化避免漂移。
        /// </summary>
        public static void IntegrateOrientation(ref Quaternion orientation, Vector3 angularVelocity, float dt)
        {
            if (angularVelocity.LengthSquared() < 1e-12f) return;

            float halfDt = 0.5f * dt;
            // 把角速度转成纯四元数(实部0)
            var dq = new Quaternion(angularVelocity.X * halfDt, angularVelocity.Y * halfDt,
                                     angularVelocity.Z * halfDt, 0f);
            dq = Quaternion.Multiply(dq, orientation);
            orientation = Quaternion.Normalize(orientation + dq);
        }

        /// <summary>施加阻尼。用 MathF.Pow(1-d, dt) 而不是 d*dt，保证帧率无关。</summary>
        public static void ApplyDamping(ref Vector3 linearVel, ref Vector3 angularVel,
            float linearDamping, float angularDamping, float dt)
        {
            if (linearDamping > 0)
                linearVel *= MathF.Pow(1f - linearDamping, dt);
            if (angularDamping > 0)
                angularVel *= MathF.Pow(1f - angularDamping, dt);
        }

        // =========================================================================
        //  力和扭矩
        // =========================================================================

        /// <summary>在指定世界点施加力。会同时产生扭矩。</summary>
        public static void ApplyForceAtPoint(ref Vector3 forceAccum, ref Vector3 torqueAccum,
            Vector3 force, Vector3 worldPoint, Vector3 centerOfMass)
        {
            forceAccum += force;
            Vector3 r = worldPoint - centerOfMass;
            torqueAccum += Vector3.Cross(r, force);
        }

        /// <summary>在质心施加力(不产生扭矩)。</summary>
        public static void ApplyForceAtCenter(ref Vector3 forceAccum, Vector3 force)
        {
            forceAccum += force;
        }

        /// <summary>施加扭矩(不影响线速度)。</summary>
        public static void ApplyTorque(ref Vector3 torqueAccum, Vector3 torque)
        {
            torqueAccum += torque;
        }

        /// <summary>施加线性冲量(瞬时改变速度)。</summary>
        public static void ApplyLinearImpulse(ref Vector3 linearVel, Vector3 impulse, float invMass)
        {
            linearVel += impulse * invMass;
        }

        /// <summary>施加角冲量。</summary>
        public static void ApplyAngularImpulse(ref Vector3 angularVel, Vector3 angularImpulse,
            Matrix4x4 invInertiaWorld)
        {
            angularVel += TransformByInertia(angularImpulse, invInertiaWorld);
        }

        /// <summary>在指定世界点施加冲量(同时改变线速度和角速度)。</summary>
        public static void ApplyImpulseAtPoint(ref Vector3 linearVel, ref Vector3 angularVel,
            Vector3 impulse, Vector3 worldPoint, Vector3 centerOfMass,
            float invMass, Matrix4x4 invInertiaWorld)
        {
            linearVel += impulse * invMass;
            Vector3 r = worldPoint - centerOfMass;
            Vector3 angularImpulse = Vector3.Cross(r, impulse);
            angularVel += TransformByInertia(angularImpulse, invInertiaWorld);
        }

        // =========================================================================
        //  常见力
        // =========================================================================

        /// <summary>重力。F = m * g。注意质量不能为0。</summary>
        public static Vector3 ComputeGravityForce(float mass, Vector3 gravity, float gravityScale = 1f)
        {
            return mass * gravity * gravityScale;
        }

        /// <summary>
        /// 弹簧力(Hooke定律)。F = -k * x - c * v
        /// k是劲度系数，c是阻尼系数，x是相对平衡位置的位移，v是相对速度。
        /// </summary>
        public static Vector3 ComputeSpringForce(Vector3 displacement, Vector3 relativeVelocity,
            float stiffness, float damping)
        {
            return -stiffness * displacement - damping * relativeVelocity;
        }

        /// <summary>
        /// 线性阻尼力。F = -c * v
        /// 和ApplyDamping的区别：这个是连续力，每帧施加；ApplyDamping是后处理直接缩放速度。
        /// </summary>
        public static Vector3 ComputeLinearDragForce(Vector3 velocity, float dragCoefficient)
        {
            return -dragCoefficient * velocity;
        }

        /// <summary>
        /// 二次阻力(空气阻力)。F = -c * |v| * v
        /// 速度越快阻力越大。
        /// </summary>
        public static Vector3 ComputeQuadraticDragForce(Vector3 velocity, float dragCoefficient)
        {
            float speed = velocity.Length();
            if (speed < 1e-6f) return Vector3.Zero;
            return -dragCoefficient * speed * velocity;
        }

        /// <summary>向心力。F = m * v^2 / r * (-r_hat)。</summary>
        public static Vector3 ComputeCentripetalForce(float mass, Vector3 velocity, Vector3 centerToBody)
        {
            float r = centerToBody.Length();
            if (r < 1e-6f) return Vector3.Zero;
            float speed = velocity.Length();
            float mag = mass * speed * speed / r;
            return -mag * centerToBody / r;
        }

        // =========================================================================
        //  辅助
        // =========================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 TransformByInertia(Vector3 v, Matrix4x4 m)
        {
            return new Vector3(
                m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
                m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
                m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);
        }

        /// <summary>算两体碰撞后的相对速度(沿接触点)。</summary>
        public static Vector3 ComputeRelativeVelocityAtPoint(
            Vector3 linVelA, Vector3 angVelA, Vector3 posA,
            Vector3 linVelB, Vector3 angVelB, Vector3 posB,
            Vector3 contactPoint)
        {
            Vector3 rA = contactPoint - posA;
            Vector3 rB = contactPoint - posB;
            Vector3 vA = linVelA + Vector3.Cross(angVelA, rA);
            Vector3 vB = linVelB + Vector3.Cross(angVelB, rB);
            return vB - vA;
        }

        /// <summary>计算质心。给定一组带质量的点。</summary>
        public static Vector3 ComputeCenterOfMass(ReadOnlySpan<Vector3> positions, ReadOnlySpan<float> masses)
        {
            if (positions.Length == 0 || positions.Length != masses.Length)
                return Vector3.Zero;

            Vector3 sum = Vector3.Zero;
            float totalMass = 0f;
            for (int i = 0; i < positions.Length; i++)
            {
                sum += positions[i] * masses[i];
                totalMass += masses[i];
            }
            if (totalMass < 1e-8f) return Vector3.Zero;
            return sum / totalMass;
        }

        /// <summary>平行轴定理：把绕某轴的惯性张量转到另一个平行轴。</summary>
        public static Matrix4x4 ParallelAxisTheorem(Matrix4x4 inertiaCM, float mass, Vector3 offset)
        {
            // I' = I_cm + m * (d*d - d⊗d)  其中d是位移向量
            // 简化为：I' = I_cm + m * |d|^2 * I - m * d⊗d
            float d2 = offset.LengthSquared();
            float mxx = mass * offset.X * offset.X;
            float mxy = mass * offset.X * offset.Y;
            float mxz = mass * offset.X * offset.Z;
            float myy = mass * offset.Y * offset.Y;
            float myz = mass * offset.Y * offset.Z;
            float mzz = mass * offset.Z * offset.Z;

            return new Matrix4x4(
                inertiaCM.M11 + mass * d2 - mxx, inertiaCM.M12 - mxy, inertiaCM.M13 - mxz, 0,
                inertiaCM.M21 - mxy, inertiaCM.M22 + mass * d2 - myy, inertiaCM.M23 - myz, 0,
                inertiaCM.M31 - mxz, inertiaCM.M32 - myz, inertiaCM.M33 + mass * d2 - mzz, 0,
                0, 0, 0, 1);
        }
    }

    // ============================================================================================
    //  MassProperties - 质量属性的封装
    // ============================================================================================

    /// <summary>
    /// 刚体的质量属性：质量、质心、惯性张量。
    /// </summary>
    public struct MassProperties
    {
        public float Mass;
        public Vector3 CenterOfMass;
        public Matrix4x4 InertiaTensor;   // 局部系(以质心为原点)

        public float InverseMass => Mass > 1e-8f ? 1f / Mass : 0f;
        public Matrix4x4 InverseInertia => RigidBodyDynamics.InvertOrIdentity(InertiaTensor);

        /// <summary>用形状计算质量属性。</summary>
        public static MassProperties FromShape(Shape shape, float density)
        {
            var mp = new MassProperties();
            mp.Mass = shape.GetVolume() * density;
            mp.CenterOfMass = Vector3.Zero;
            mp.InertiaTensor = shape.ComputeInertia(mp.Mass);
            return mp;
        }

        /// <summary>组合多个质量属性(用于compound形状)。</summary>
        public static MassProperties Combine(ReadOnlySpan<MassProperties> parts, ReadOnlySpan<Vector3> positions)
        {
            var result = new MassProperties();
            result.Mass = 0;
            result.CenterOfMass = Vector3.Zero;
            result.InertiaTensor = Matrix4x4.Identity;

            if (parts.Length == 0 || parts.Length != positions.Length) return result;

            // 总质量
            for (int i = 0; i < parts.Length; i++)
                result.Mass += parts[i].Mass;

            if (result.Mass < 1e-8f) return result;

            // 质心
            for (int i = 0; i < parts.Length; i++)
                result.CenterOfMass += positions[i] * parts[i].Mass;
            result.CenterOfMass /= result.Mass;

            // 惯性张量：各部分用平行轴定理转到组合质心
            for (int i = 0; i < parts.Length; i++)
            {
                Vector3 offset = positions[i] + parts[i].CenterOfMass - result.CenterOfMass;
                var shifted = RigidBodyDynamics.ParallelAxisTheorem(parts[i].InertiaTensor, parts[i].Mass, offset);
                result.InertiaTensor = AddMatrix(result.InertiaTensor, shifted);
            }

            return result;
        }

        private static Matrix4x4 AddMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            return new Matrix4x4(
                a.M11 + b.M11, a.M12 + b.M12, a.M13 + b.M13, 0,
                a.M21 + b.M21, a.M22 + b.M22, a.M23 + b.M23, 0,
                a.M31 + b.M31, a.M32 + b.M32, a.M33 + b.M33, 0,
                0, 0, 0, 1);
        }
    }

    // ============================================================================================
    //  ForceGenerator - 力生成器
    //  把持续施加的力(重力、弹簧、浮力等)抽象出来，每帧统一更新
    // ============================================================================================

    /// <summary>力生成器接口。</summary>
    public interface IForceGenerator
    {
        /// <summary>每帧调用，更新作用在body上的力。</summary>
        void ApplyForce(ref BodySlot body, float dt);
    }

    /// <summary>重力。</summary>
    public sealed class GravityForce : IForceGenerator
    {
        public Vector3 Gravity;
        public GravityForce(Vector3 gravity) { Gravity = gravity; }
        public void ApplyForce(ref BodySlot body, float dt)
        {
            if (body.MotionType != BodyMotionType.Dynamic) return;
            body.ForceAccumulator += body.Mass * Gravity * body.GravityScale;
        }
    }

    /// <summary>弹簧力(连接两个body或者body和固定点)。</summary>
    public sealed class SpringForce : IForceGenerator
    {
        public Vector3 Anchor;           // 固定点(世界系)
        public Vector3 LocalAttach;      // body局部系上的连接点
        public float RestLength;
        public float Stiffness;
        public float Damping;

        public SpringForce(Vector3 anchor, Vector3 localAttach, float restLength,
            float stiffness, float damping)
        {
            Anchor = anchor;
            LocalAttach = localAttach;
            RestLength = restLength;
            Stiffness = stiffness;
            Damping = damping;
        }

        public void ApplyForce(ref BodySlot body, float dt)
        {
            if (body.MotionType != BodyMotionType.Dynamic) return;

            Vector3 worldAttach = Vector3.Transform(LocalAttach, body.Orientation) + body.Position;
            Vector3 displacement = worldAttach - Anchor;
            float dist = displacement.Length();
            if (dist < 1e-6f) return;

            Vector3 dir = displacement / dist;
            // 弹簧力
            float stretch = dist - RestLength;
            Vector3 force = -Stiffness * stretch * dir;

            // 阻尼: F_d = -c * v_rel沿弹簧方向的分量
            Vector3 velocity = body.LinearVelocity +
                Vector3.Cross(body.AngularVelocity, worldAttach - body.Position);
            float velAlongSpring = Vector3.Dot(velocity, dir);
            force -= Damping * velAlongSpring * dir;

            // 在连接点施加
            body.ForceAccumulator += force;
            Vector3 r = worldAttach - body.Position;
            body.TorqueAccumulator += Vector3.Cross(r, force);
        }
    }

    /// <summary>两个body之间的弹簧。</summary>
    public sealed class BodySpringForce : IForceGenerator
    {
        public int OtherBodyIndex;
        public Vector3 LocalAttachA;
        public Vector3 LocalAttachB;
        public float RestLength;
        public float Stiffness;
        public float Damping;
        public List<BodySlot> Bodies;

        public BodySpringForce(List<BodySlot> bodies, int otherIdx,
            Vector3 localAttachA, Vector3 localAttachB,
            float restLength, float stiffness, float damping)
        {
            Bodies = bodies;
            OtherBodyIndex = otherIdx;
            LocalAttachA = localAttachA;
            LocalAttachB = localAttachB;
            RestLength = restLength;
            Stiffness = stiffness;
            Damping = damping;
        }

        public void ApplyForce(ref BodySlot body, float dt)
        {
            if (body.MotionType != BodyMotionType.Dynamic) return;
            if (OtherBodyIndex < 0 || OtherBodyIndex >= Bodies.Count) return;
            ref var other = ref Bodies.AsSpan()[OtherBodyIndex];
            if (!other.Active) return;

            Vector3 wa = Vector3.Transform(LocalAttachA, body.Orientation) + body.Position;
            Vector3 wb = Vector3.Transform(LocalAttachB, other.Orientation) + other.Position;
            Vector3 d = wb - wa;
            float dist = d.Length();
            if (dist < 1e-6f) return;

            Vector3 dir = d / dist;
            float stretch = dist - RestLength;
            Vector3 force = Stiffness * stretch * dir;

            // 阻尼
            Vector3 vA = body.LinearVelocity + Vector3.Cross(body.AngularVelocity, wa - body.Position);
            Vector3 vB = other.LinearVelocity + Vector3.Cross(other.AngularVelocity, wb - other.Position);
            float relVel = Vector3.Dot(vB - vA, dir);
            force += Damping * relVel * dir;

            // 对A施加+B方向的力(被B拉)，对B反向
            body.ForceAccumulator += force;
            body.TorqueAccumulator += Vector3.Cross(wa - body.Position, force);

            if (other.MotionType == BodyMotionType.Dynamic)
            {
                other.ForceAccumulator -= force;
                other.TorqueAccumulator -= Vector3.Cross(wb - other.Position, force);
            }
        }
    }

    /// <summary>恒定力(比如推力)。</summary>
    public sealed class ConstantForce : IForceGenerator
    {
        public Vector3 Force;
        public Vector3? LocalPoint;  // null表示作用在质心

        public ConstantForce(Vector3 force, Vector3? localPoint = null)
        {
            Force = force;
            LocalPoint = localPoint;
        }

        public void ApplyForce(ref BodySlot body, float dt)
        {
            if (body.MotionType != BodyMotionType.Dynamic) return;
            body.ForceAccumulator += Force;
            if (LocalPoint.HasValue)
            {
                Vector3 worldPoint = Vector3.Transform(LocalPoint.Value, body.Orientation) + body.Position;
                body.TorqueAccumulator += Vector3.Cross(worldPoint - body.Position, Force);
            }
        }
    }

    /// <summary>阻力(线性+二次)。</summary>
    public sealed class DragForce : IForceGenerator
    {
        public float LinearDrag;
        public float QuadraticDrag;

        public DragForce(float linear, float quadratic)
        {
            LinearDrag = linear;
            QuadraticDrag = quadratic;
        }

        public void ApplyForce(ref BodySlot body, float dt)
        {
            if (body.MotionType != BodyMotionType.Dynamic) return;
            Vector3 v = body.LinearVelocity;
            float speed = v.Length();
            if (speed < 1e-6f) return;
            Vector3 force = -LinearDrag * v - QuadraticDrag * speed * v;
            body.ForceAccumulator += force;
        }
    }

    /// <summary>力注册表，管理所有力生成器。</summary>
    public sealed class ForceRegistry
    {
        private readonly List<(BodyHandle body, IForceGenerator generator)> _entries = new(64);

        public void Add(BodyHandle body, IForceGenerator generator)
        {
            _entries.Add((body, generator));
        }

        public void Remove(BodyHandle body, IForceGenerator generator)
        {
            _entries.RemoveAll(e => e.body == body && ReferenceEquals(e.generator, generator));
        }

        public void Clear() => _entries.Clear();

        /// <summary>每帧调用，把所有力施加到对应body上。</summary>
        public void ApplyForces(List<BodySlot> bodies, float dt)
        {
            var span = bodies.AsSpan();
            for (int i = 0; i < _entries.Count; i++)
            {
                ref var e = ref _entries.AsSpan()[i];
                if (e.body.Index < 0 || e.body.Index >= span.Length) continue;
                ref var b = ref span[e.body.Index];
                if (!b.Active) continue;
                if (b.Generation != e.body.Generation) continue;
                e.generator.ApplyForce(ref b, dt);
            }
        }
    }
}
