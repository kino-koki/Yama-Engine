using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.RHI;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  PhysicsField.cs
    //
    //  物理场系统。为物理世界提供空间分布的力场与速度场，用于模拟风力、爆炸冲击波、
    //  涡旋、湍流等非接触力对刚体和粒子的影响。
    //
    //  本系统包含以下核心模块：
    //    - IPhysicsField        ：物理场统一接口（Sample 返回力或速度）
    //    - ForceField            ：力场（方向力/径向力/旋涡力/噪声力）
    //    - VectorField           ：矢量场（3D 纹理存储速度场，GPU 采样）
    //    - UniformVectorField    ：均匀矢量场（全场恒定速度）
    //    - RadialVectorField     ：径向矢量场（向中心汇聚或向外发散）
    //    - VortexField           ：涡旋场（绕轴旋转）
    //    - NoiseField            ：噪声力场（Curl Noise 湍流）
    //    - PhysicsFieldVolume    ：物理场体积（Box/Sphere 限定作用范围）
    //    - Falloff               ：衰减函数（线性/指数/平滑步进）
    //    - PhysicsFieldComponent ：ECS 组件（可附加到实体）
    //
    //  与引擎其他系统的对接：
    //    - PhysicsWorld ：在力施加阶段采样场并将力施加到刚体
    //    - RHI          ：3D 纹理矢量场的创建与上传
    //    - ECS          ：通过 PhysicsFieldComponent 与游戏逻辑层集成
    // ============================================================================================

    /// <summary>
    ///   物理场类型。区分场返回的是"力"（加速度积分前）还是"速度"
    /// （直接设定或叠加的速度）。力场需要乘以质量倒数才得到加速度，
    /// 速度场则常用于流体驱动或风场对粒子的影响。
    /// </summary>
    public enum PhysicsFieldType : byte
    {
        /// <summary>力场：Sample 返回的是力向量（牛顿）。</summary>
        Force = 0,
        /// <summary>速度场：Sample 返回的是速度向量（米/秒）。</summary>
        Velocity = 1,
    }

    /// <summary>
    ///   衰减函数类型。控制物理场强度随到体积中心距离的衰减方式。
    /// </summary>
    public enum FalloffType : byte
    {
        /// <summary>无衰减：场内强度恒定。</summary>
        None = 0,
        /// <summary>线性衰减：从中心到边缘线性递减到零。</summary>
        Linear = 1,
        /// <summary>指数衰减：e^(-d/λ)，平滑过渡。</summary>
        Exponential = 2,
        /// <summary>平滑步进：smoothstep 衰减，边缘更柔和。</summary>
        Smoothstep = 3,
    }

    // ──────────────────────────────────────────────────────────────
    //  衰减函数
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   衰减函数工具类。提供各种衰减曲线的计算，输入归一化距离 [0,1]，
    ///   返回衰减系数 [0,1]。0 表示完全在场中心（无衰减），1 表示在体积边缘（完全衰减）。
    /// </summary>
    public static class Falloff
    {
        /// <summary>
        ///   计算衰减系数。
        /// </summary>
        /// <param name="normalizedDistance">归一化距离 [0,1]，0=中心，1=边缘。</param>
        /// <param name="type">衰减类型。</param>
        /// <param name="lambda">指数衰减的波长参数（仅 Exponential 使用）。</param>
        /// <returns>衰减系数 [0,1]，1 表示无衰减（全力），0 表示完全衰减。</returns>
        public static float Evaluate(float normalizedDistance, FalloffType type, float lambda = 0.3f)
        {
            float d = Math.Clamp(normalizedDistance, 0f, 1f);
            return type switch
            {
                FalloffType.None => 1f,
                FalloffType.Linear => 1f - d,
                FalloffType.Exponential => MathF.Exp(-d / MathF.Max(0.0001f, lambda)),
                FalloffType.Smoothstep => 1f - Smoothstep(d),
                _ => 1f,
            };
        }

        /// <summary>smoothstep 函数：t*t*(3-2t)。</summary>
        public static float Smoothstep(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>反向 smoothstep：1 - smoothstep(t)。</summary>
        public static float InvSmoothstep(float t) => 1f - Smoothstep(t);
    }

    // ──────────────────────────────────────────────────────────────
    //  物理场体积
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   物理场体积类型。限定物理场的作用空间范围。
    /// </summary>
    public enum FieldVolumeType : byte
    {
        /// <summary>球形体积。</summary>
        Sphere = 0,
        /// <summary>轴对齐盒体体积。</summary>
        Box = 1,
        /// <summary>无限范围（全场全局有效）。</summary>
        Infinite = 2,
    }

    /// <summary>
    ///   物理场体积。定义物理场在空间中的作用范围，并计算某点是否在范围内
    ///   及其归一化距离（用于衰减）。
    /// </summary>
    public struct PhysicsFieldVolume
    {
        /// <summary>体积类型。</summary>
        public FieldVolumeType Type;

        /// <summary>体积中心位置（世界空间）。</summary>
        public Vector3 Center;

        /// <summary>球形体积的半径（Type=Sphere 时有效）。</summary>
        public float SphereRadius;

        /// <summary>盒体体积的半尺寸（Type=Box 时有效，从中心到各面的距离）。</summary>
        public Vector3 BoxHalfExtents;

        /// <summary>衰减类型。</summary>
        public FalloffType Falloff;

        /// <summary>指数衰减波长。</summary>
        public float FalloffLambda;

        /// <summary>创建球形体积。</summary>
        public static PhysicsFieldVolume Sphere(Vector3 center, float radius,
            FalloffType falloff = FalloffType.Smoothstep, float lambda = 0.3f)
        {
            return new PhysicsFieldVolume
            {
                Type = FieldVolumeType.Sphere,
                Center = center,
                SphereRadius = radius,
                BoxHalfExtents = Vector3.Zero,
                Falloff = falloff,
                FalloffLambda = lambda,
            };
        }

        /// <summary>创建盒体体积。</summary>
        public static PhysicsFieldVolume Box(Vector3 center, Vector3 halfExtents,
            FalloffType falloff = FalloffType.Smoothstep, float lambda = 0.3f)
        {
            return new PhysicsFieldVolume
            {
                Type = FieldVolumeType.Box,
                Center = center,
                SphereRadius = 0f,
                BoxHalfExtents = halfExtents,
                Falloff = falloff,
                FalloffLambda = lambda,
            };
        }

        /// <summary>创建无限体积（全局有效，无衰减）。</summary>
        public static PhysicsFieldVolume Infinite()
        {
            return new PhysicsFieldVolume
            {
                Type = FieldVolumeType.Infinite,
                Center = Vector3.Zero,
                SphereRadius = float.MaxValue,
                BoxHalfExtents = new Vector3(float.MaxValue),
                Falloff = FalloffType.None,
                FalloffLambda = 0.3f,
            };
        }

        /// <summary>
        ///   计算某点是否在体积内。
        /// </summary>
        public readonly bool Contains(Vector3 position)
        {
            switch (Type)
            {
                case FieldVolumeType.Sphere:
                    return Vector3.DistanceSquared(position, Center) <= SphereRadius * SphereRadius;
                case FieldVolumeType.Box:
                    var d = position - Center;
                    return MathF.Abs(d.X) <= BoxHalfExtents.X &&
                           MathF.Abs(d.Y) <= BoxHalfExtents.Y &&
                           MathF.Abs(d.Z) <= BoxHalfExtents.Z;
                case FieldVolumeType.Infinite:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        ///   计算某点到体积中心的归一化距离 [0,1]（0=中心，1=边缘，>1=外部）。
        ///   用于衰减计算。
        /// </summary>
        public readonly float NormalizedDistance(Vector3 position)
        {
            switch (Type)
            {
                case FieldVolumeType.Sphere:
                    if (SphereRadius <= 0f) return 1f;
                    return Vector3.Distance(position, Center) / SphereRadius;

                case FieldVolumeType.Box:
                    {
                        var d = position - Center;
                        // 取最大轴向归一化距离
                        float nx = BoxHalfExtents.X > 0f ? MathF.Abs(d.X) / BoxHalfExtents.X : 0f;
                        float ny = BoxHalfExtents.Y > 0f ? MathF.Abs(d.Y) / BoxHalfExtents.Y : 0f;
                        float nz = BoxHalfExtents.Z > 0f ? MathF.Abs(d.Z) / BoxHalfExtents.Z : 0f;
                        return Math.Max(nx, Math.Max(ny, nz));
                    }

                case FieldVolumeType.Infinite:
                    return 0f;

                default:
                    return 1f;
            }
        }

        /// <summary>
        ///   计算某点的衰减系数 [0,1]。
        /// </summary>
        public readonly float EvaluateFalloff(Vector3 position)
        {
            float nd = NormalizedDistance(position);
            return YamaEngine.Physics.Falloff.Evaluate(nd, Falloff, FalloffLambda);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  物理场接口
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   物理场统一接口。所有物理场（力场、矢量场等）实现此接口。
    ///   <see cref="Sample"/> 在给定世界位置采样场，返回力或速度向量。
    ///   通过 <see cref="FieldType"/> 区分返回值是力还是速度。
    /// </summary>
    public interface IPhysicsField
    {
        /// <summary>场类型（力或速度）。</summary>
        PhysicsFieldType FieldType { get; }

        /// <summary>场的作用体积。</summary>
        PhysicsFieldVolume Volume { get; set; }

        /// <summary>场的基础强度（缩放系数）。</summary>
        float Strength { get; set; }

        /// <summary>
        ///   在指定世界位置采样场。
        /// </summary>
        /// <param name="position">世界空间采样位置。</param>
        /// <param name="deltaTime">当前帧间隔（部分场需要时间演化）。</param>
        /// <returns>力或速度向量（已乘以强度和体积衰减）。</returns>
        Vector3 Sample(Vector3 position, float deltaTime);

        /// <summary>场是否已启用。</summary>
        bool IsEnabled { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    //  力场（ForceField）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   力场类型。决定 ForceField 如何施加力。
    /// </summary>
    public enum ForceFieldMode : byte
    {
        /// <summary>方向力：沿固定方向施加恒定力（如风、重力）。</summary>
        Directional = 0,
        /// <summary>径向力：从中心向外（或向内）施加力（如爆炸、引力）。</summary>
        Radial = 1,
        /// <summary>旋涡力：绕固定轴施加切向力（如龙卷风）。</summary>
        Vortex = 2,
        /// <summary>噪声力：基于 Curl Noise 的湍流力。</summary>
        Noise = 3,
    }

    /// <summary>
    ///   力场。可配置为方向力、径向力、旋涡力或噪声力四种模式。
    ///   这是游戏中最常用的物理场类型，适用于风、爆炸冲击波、引力井、龙卷风等。
    /// </summary>
    public sealed class ForceField : IPhysicsField
    {
        /// <summary>力场模式。</summary>
        public ForceFieldMode Mode { get; set; } = ForceFieldMode.Directional;

        public PhysicsFieldType FieldType => PhysicsFieldType.Force;

        public PhysicsFieldVolume Volume { get; set; }

        public float Strength { get; set; } = 1.0f;

        public bool IsEnabled { get; set; } = true;

        /// <summary>方向力模式下的力方向（需归一化）。</summary>
        public Vector3 Direction { get; set; } = new(1, 0, 0);

        /// <summary>径向力模式：true=向外推，false=向内吸。</summary>
        public bool RadialOutward { get; set; } = true;

        /// <summary>旋涡力模式下的旋转轴（需归一化）。</summary>
        public Vector3 VortexAxis { get; set; } = new(0, 1, 0);

        /// <summary>噪声力模式下的噪声频率。</summary>
        public float NoiseFrequency { get; set; } = 0.5f;

        /// <summary>噪声力模式下的噪声演化速度（时间动画）。</summary>
        public float NoiseEvolutionSpeed { get; set; } = 0.2f;

        // 噪声时间偏移（内部演化）
        private float _noiseTime;

        public ForceField()
        {
            Volume = PhysicsFieldVolume.Infinite();
        }

        public Vector3 Sample(Vector3 position, float deltaTime)
        {
            if (!IsEnabled) return Vector3.Zero;

            // 体积检测与衰减
            if (!Volume.Contains(position)) return Vector3.Zero;
            float falloff = Volume.EvaluateFalloff(position);
            if (falloff <= 0f) return Vector3.Zero;

            Vector3 force = Mode switch
            {
                ForceFieldMode.Directional => SampleDirectional(),
                ForceFieldMode.Radial => SampleRadial(position),
                ForceFieldMode.Vortex => SampleVortex(position),
                ForceFieldMode.Noise => SampleNoise(position, deltaTime),
                _ => Vector3.Zero,
            };

            return force * Strength * falloff;
        }

        private Vector3 SampleDirectional()
        {
            Vector3 dir = Direction;
            if (dir.LengthSquared() > 1e-8f)
                dir = Vector3.Normalize(dir);
            return dir;
        }

        private Vector3 SampleRadial(Vector3 position)
        {
            Vector3 toCenter = position - Volume.Center;
            float lenSq = toCenter.LengthSquared();
            if (lenSq < 1e-8f) return Vector3.Zero;
            Vector3 dir = toCenter / MathF.Sqrt(lenSq);
            return RadialOutward ? dir : -dir;
        }

        private Vector3 SampleVortex(Vector3 position)
        {
            Vector3 axis = VortexAxis;
            if (axis.LengthSquared() > 1e-8f)
                axis = Vector3.Normalize(axis);
            else
                axis = Vector3.UnitY;

            // 从中心到位置的向量
            Vector3 r = position - Volume.Center;
            // 投影到垂直于轴的平面
            float proj = Vector3.Dot(r, axis);
            Vector3 planar = r - axis * proj;

            if (planar.LengthSquared() < 1e-8f) return Vector3.Zero;

            // 切向方向 = axis × planar（右手定则）
            Vector3 tangent = Vector3.Cross(axis, planar);
            if (tangent.LengthSquared() > 1e-8f)
                tangent = Vector3.Normalize(tangent);

            return tangent;
        }

        private Vector3 SampleNoise(Vector3 position, float deltaTime)
        {
            _noiseTime += deltaTime * NoiseEvolutionSpeed;
            // 使用 Curl Noise 保证无散度（无源场），避免粒子聚集
            return CurlNoise.Sample(
                position * NoiseFrequency,
                new Vector3(_noiseTime, _noiseTime * 0.7f, _noiseTime * 1.3f));
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Curl Noise（用于 NoiseField 与 ForceField.Noise 模式）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   Curl Noise（旋度噪声）生成器。通过对一个 3D 标量噪声场取旋度，
    ///   得到一个无散度（divergence-free）的矢量场。无散度特性保证了
    ///   噪声力场不会在空间中产生"源"或"汇"，使粒子模拟看起来像真实的
    ///   不可压缩流体湍流，而非被吸向某些点。
    ///
    ///   本实现使用简化的 Perlin 风格梯度噪声作为标量势场，再通过中心
    ///   差分近似计算旋度。
    /// </summary>
    public static class CurlNoise
    {
        // 基于哈希的伪随机梯度表
        private static readonly int[] _perm = new int[512];

        static CurlNoise()
        {
            // 初始化排列表（Perlin 噪声标准做法）
            var p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            var rand = new Random(12345);
            for (int i = 255; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
        }

        /// <summary>
        ///   在给定位置和时间采样 Curl Noise 向量。
        /// </summary>
        /// <param name="pos">空间坐标（已乘以频率）。</param>
        /// <param name="timeOffset">时间偏移（用于噪声演化）。</param>
        /// <returns>无散度的旋度向量。</returns>
        public static Vector3 Sample(Vector3 pos, Vector3 timeOffset)
        {
            float eps = 0.1f;

            // 对三个标量势场分别计算偏导，组合成旋度
            // curl(F) = (∂Pz/∂y - ∂Py/∂z, ∂Px/∂z - ∂Pz/∂x, ∂Py/∂x - ∂Px/∂y)
            // 这里用三个独立的噪声场作为 Px, Py, Pz

            float n1_y = Noise(pos.X, pos.Y + eps, pos.Z, timeOffset);
            float n1_y0 = Noise(pos.X, pos.Y - eps, pos.Z, timeOffset);
            float n1_z = Noise(pos.X, pos.Y, pos.Z + eps, timeOffset);
            float n1_z0 = Noise(pos.X, pos.Y, pos.Z - eps, timeOffset);

            float n2_x = Noise(pos.X + eps + 100, pos.Y, pos.Z, timeOffset);
            float n2_x0 = Noise(pos.X - eps + 100, pos.Y, pos.Z, timeOffset);
            float n2_z = Noise(pos.X + 100, pos.Y, pos.Z + eps, timeOffset);
            float n2_z0 = Noise(pos.X + 100, pos.Y, pos.Z - eps, timeOffset);

            float n3_x = Noise(pos.X + eps + 200, pos.Y, pos.Z, timeOffset);
            float n3_x0 = Noise(pos.X - eps + 200, pos.Y, pos.Z, timeOffset);
            float n3_y = Noise(pos.X + 200, pos.Y + eps, pos.Z, timeOffset);
            float n3_y0 = Noise(pos.X + 200, pos.Y - eps, pos.Z, timeOffset);

            float inv2E = 0.5f / eps;
            float curlX = (n3_y - n3_y0) * inv2E - (n2_z - n2_z0) * inv2E;
            float curlY = (n1_z - n1_z0) * inv2E - (n3_x - n3_x0) * inv2E;
            float curlZ = (n2_x - n2_x0) * inv2E - (n1_y - n1_y0) * inv2E;

            return new Vector3(curlX, curlY, curlZ);
        }

        /// <summary>简化的 3D 梯度噪声（Perlin 风格）。</summary>
        private static float Noise(float x, float y, float z, Vector3 t)
        {
            // 加入时间偏移使噪声随时间演化
            x += t.X; y += t.Y; z += t.Z;

            int xi = (int)MathF.Floor(x) & 255;
            int yi = (int)MathF.Floor(y) & 255;
            int zi = (int)MathF.Floor(z) & 255;

            float xf = x - MathF.Floor(x);
            float yf = y - MathF.Floor(y);
            float zf = z - MathF.Floor(z);

            // 缓和曲线
            float u = Fade(xf);
            float v = Fade(yf);
            float w = Fade(zf);

            int aaa = _perm[_perm[_perm[xi] + yi] + zi];
            int aba = _perm[_perm[_perm[xi] + yi + 1] + zi];
            int aab = _perm[_perm[_perm[xi] + yi] + zi + 1];
            int abb = _perm[_perm[_perm[xi] + yi + 1] + zi + 1];
            int baa = _perm[_perm[_perm[xi + 1] + yi] + zi];
            int bba = _perm[_perm[_perm[xi + 1] + yi + 1] + zi];
            int bab = _perm[_perm[_perm[xi + 1] + yi] + zi + 1];
            int bbb = _perm[_perm[_perm[xi + 1] + yi + 1] + zi + 1];

            float x1 = Lerp(Grad(aaa, xf, yf, zf), Grad(baa, xf - 1, yf, zf), u);
            float x2 = Lerp(Grad(aba, xf, yf - 1, zf), Grad(bba, xf - 1, yf - 1, zf), u);
            float y1 = Lerp(x1, x2, v);
            float x3 = Lerp(Grad(aab, xf, yf, zf - 1), Grad(bab, xf - 1, yf, zf - 1), u);
            float x4 = Lerp(Grad(abb, xf, yf - 1, zf - 1), Grad(bbb, xf - 1, yf - 1, zf - 1), u);
            float y2 = Lerp(x3, x4, v);

            return Lerp(y1, y2, w);
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Grad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  矢量场（VectorField）与具体实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   均匀矢量场。全场范围内速度恒定，最简单的矢量场。
    ///   适用于全局风向、恒定水流等场景。
    /// </summary>
    public sealed class UniformVectorField : IPhysicsField
    {
        public PhysicsFieldType FieldType => PhysicsFieldType.Velocity;

        public PhysicsFieldVolume Volume { get; set; }

        public float Strength { get; set; } = 1.0f;

        public bool IsEnabled { get; set; } = true;

        /// <summary>均匀速度向量。</summary>
        public Vector3 Velocity { get; set; } = new(1, 0, 0);

        public UniformVectorField()
        {
            Volume = PhysicsFieldVolume.Infinite();
        }

        public Vector3 Sample(Vector3 position, float deltaTime)
        {
            if (!IsEnabled) return Vector3.Zero;
            if (!Volume.Contains(position)) return Vector3.Zero;
            float falloff = Volume.EvaluateFalloff(position);
            return Velocity * Strength * falloff;
        }
    }

    /// <summary>
    ///   径向矢量场。速度方向从中心向外发散（或向内汇聚），
    ///   速度大小可随距离衰减或保持恒定。适用于爆炸冲击波、吸力场等。
    /// </summary>
    public sealed class RadialVectorField : IPhysicsField
    {
        public PhysicsFieldType FieldType => PhysicsFieldType.Velocity;

        public PhysicsFieldVolume Volume { get; set; }

        public float Strength { get; set; } = 1.0f;

        public bool IsEnabled { get; set; } = true;

        /// <summary>true=向外发散，false=向内汇聚。</summary>
        public bool Outward { get; set; } = true;

        /// <summary>速度大小是否随距离线性衰减。</summary>
        public bool FalloffWithDistance { get; set; } = true;

        public RadialVectorField()
        {
            Volume = PhysicsFieldVolume.Infinite();
        }

        public Vector3 Sample(Vector3 position, float deltaTime)
        {
            if (!IsEnabled) return Vector3.Zero;
            if (!Volume.Contains(position)) return Vector3.Zero;

            Vector3 toPos = position - Volume.Center;
            float dist = toPos.Length();
            if (dist < 1e-6f) return Vector3.Zero;

            Vector3 dir = toPos / dist;
            if (!Outward) dir = -dir;

            // 体积衰减
            float volFalloff = Volume.EvaluateFalloff(position);
            // 距离衰减（可选）
            float distFactor = FalloffWithDistance ? (1f / (1f + dist)) : 1f;

            return dir * Strength * volFalloff * distFactor;
        }
    }

    /// <summary>
    ///   涡旋场。速度方向绕固定轴旋转，形成涡旋。
    ///   速度大小随到轴的距离变化（典型龙卷风模型：中心风速低，外围风速高，
    ///   超过一定半径后衰减）。
    /// </summary>
    public sealed class VortexField : IPhysicsField
    {
        public PhysicsFieldType FieldType => PhysicsFieldType.Velocity;

        public PhysicsFieldVolume Volume { get; set; }

        public float Strength { get; set; } = 1.0f;

        public bool IsEnabled { get; set; } = true;

        /// <summary>旋转轴方向（需归一化）。</summary>
        public Vector3 Axis { get; set; } = new(0, 1, 0);

        /// <summary>涡旋最大速度半径（在此半径处速度最大）。</summary>
        public float MaxVelocityRadius { get; set; } = 2.0f;

        public VortexField()
        {
            Volume = PhysicsFieldVolume.Infinite();
        }

        public Vector3 Sample(Vector3 position, float deltaTime)
        {
            if (!IsEnabled) return Vector3.Zero;
            if (!Volume.Contains(position)) return Vector3.Zero;

            Vector3 axis = Axis;
            if (axis.LengthSquared() > 1e-8f)
                axis = Vector3.Normalize(axis);
            else
                axis = Vector3.UnitY;

            Vector3 r = position - Volume.Center;
            float proj = Vector3.Dot(r, axis);
            Vector3 planar = r - axis * proj;

            float radius = planar.Length();
            if (radius < 1e-6f) return Vector3.Zero;

            Vector3 tangent = Vector3.Cross(axis, planar / radius);

            // 龙卷风速度剖面：半径 < MaxVelocityRadius 时线性增长，
            // 之后按 1/r 衰减
            float speedFactor;
            if (radius < MaxVelocityRadius)
                speedFactor = radius / MathF.Max(0.0001f, MaxVelocityRadius);
            else
                speedFactor = MaxVelocityRadius / radius;

            float volFalloff = Volume.EvaluateFalloff(position);
            return tangent * Strength * speedFactor * volFalloff;
        }
    }

    /// <summary>
    ///   噪声力场。基于 Curl Noise 的湍流力场，产生无散度的随机扰动力。
    ///   适用于烟雾扰动、风场细节、粒子湍流等效果。
    /// </summary>
    public sealed class NoiseField : IPhysicsField
    {
        public PhysicsFieldType FieldType => PhysicsFieldType.Force;

        public PhysicsFieldVolume Volume { get; set; }

        public float Strength { get; set; } = 1.0f;

        public bool IsEnabled { get; set; } = true;

        /// <summary>噪声空间频率。</summary>
        public float Frequency { get; set; } = 0.5f;

        /// <summary>噪声时间演化速度。</summary>
        public float EvolutionSpeed { get; set; } = 0.2f;

        private float _time;

        public NoiseField()
        {
            Volume = PhysicsFieldVolume.Infinite();
        }

        public Vector3 Sample(Vector3 position, float deltaTime)
        {
            if (!IsEnabled) return Vector3.Zero;
            if (!Volume.Contains(position)) return Vector3.Zero;

            _time += deltaTime * EvolutionSpeed;
            float volFalloff = Volume.EvaluateFalloff(position);

            Vector3 curl = CurlNoise.Sample(
                position * Frequency,
                new Vector3(_time, _time * 0.7f, _time * 1.3f));

            return curl * Strength * volFalloff;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  3D 纹理矢量场（VectorField）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   3D 纹理矢量场。将速度场数据存储在 3D 纹理中，支持 GPU 采样。
    ///   适用于从流体模拟导入的速度场、外部烘焙的风场数据等。
    ///
    ///   纹理格式为 R16G16B16A16_Float，RGB 分量存储速度向量，
    ///   A 分量可选存储额外信息（如密度）。坐标通过世界包围盒映射到
    ///   纹理 UVW 空间。
    /// </summary>
    public sealed class VectorField : IPhysicsField, IDisposable
    {
        public PhysicsFieldType FieldType => PhysicsFieldType.Velocity;

        public PhysicsFieldVolume Volume { get; set; }

        public float Strength { get; set; } = 1.0f;

        public bool IsEnabled { get; set; } = true;

        /// <summary>3D 纹理分辨率（各轴）。</summary>
        public int ResolutionX { get; set; } = 32;
        public int ResolutionY { get; set; } = 32;
        public int ResolutionZ { get; set; } = 32;

        /// <summary>速度场数据（展平的 [Z][Y][X] 数组）。</summary>
        public Vector3[] Data { get; private set; } = Array.Empty<Vector3>();

        // RHI 资源
        private RHIDevice? _device;
        private RHITexture? _texture3D;
        private RHIDescriptorHandle _srv;
        private bool _uploaded;
        private bool _disposed;

        public VectorField()
        {
            Volume = PhysicsFieldVolume.Infinite();
        }

        /// <summary>
        ///   初始化速度场数据数组。
        /// </summary>
        public void InitializeData()
        {
            Data = new Vector3[ResolutionX * ResolutionY * ResolutionZ];
        }

        /// <summary>设置某个体素的速度值。</summary>
        public void SetVoxel(int x, int y, int z, Vector3 velocity)
        {
            if (Data.Length == 0) InitializeData();
            int idx = (z * ResolutionY + y) * ResolutionX + x;
            if (idx >= 0 && idx < Data.Length)
                Data[idx] = velocity;
        }

        /// <summary>获取某个体素的速度值。</summary>
        public Vector3 GetVoxel(int x, int y, int z)
        {
            int idx = (z * ResolutionY + y) * ResolutionX + x;
            if (idx >= 0 && idx < Data.Length)
                return Data[idx];
            return Vector3.Zero;
        }

        /// <summary>
        ///   在 GPU 上创建 3D 纹理并上传速度场数据。
        /// </summary>
        /// <param name="device">RHI 设备。</param>
        public void UploadToGPU(RHIDevice device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            _device = device;

            _texture3D?.Dispose();
            _texture3D = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R32G32B32A32_Float,
                Width = ResolutionX,
                Height = ResolutionY,
                DepthOrArraySize = ResolutionZ,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess,
            }, "VectorField3D");

            // 将 Vector3[] 数据转为 float4[]（A 分量填 1）再转字节上传
            int voxelCount = ResolutionX * ResolutionY * ResolutionZ;
            var floatData = new Vector4[voxelCount];
            for (int i = 0; i < voxelCount && i < Data.Length; i++)
                floatData[i] = new Vector4(Data[i], 1f);

            byte[] bytes = new byte[voxelCount * 16]; // R32G32B32A32 = 16 bytes
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(floatData).CopyTo(bytes);
            device.UploadToTexture(_texture3D, bytes, ResolutionX, ResolutionY, 0, 0);

            _srv = device.CreateSRV(_texture3D, 0, 0);
            _uploaded = true;
        }

        /// <summary>获取 3D 纹理的 SRV 句柄（用于 GPU 采样）。</summary>
        public RHIDescriptorHandle GetSRV()
        {
            if (!_uploaded) throw new InvalidOperationException("VectorField 尚未上传到 GPU，请先调用 UploadToGPU。");
            return _srv;
        }

        /// <summary>获取 3D 纹理资源（用于直接绑定）。</summary>
        public RHITexture? GetTexture() => _texture3D;

        public Vector3 Sample(Vector3 position, float deltaTime)
        {
            if (!IsEnabled) return Vector3.Zero;
            if (!Volume.Contains(position)) return Vector3.Zero;

            float volFalloff = Volume.EvaluateFalloff(position);

            // 将世界位置映射到体素空间并三线性插值
            Vector3 uvw = WorldToVoxelUVW(position);
            Vector3 vel = TrilinearSample(uvw);

            return vel * Strength * volFalloff;
        }

        /// <summary>将世界坐标映射到 [0,1]^3 体素 UVW 空间。</summary>
        private Vector3 WorldToVoxelUVW(Vector3 worldPos)
        {
            Vector3 min, max;
            if (Volume.Type == FieldVolumeType.Box)
            {
                min = Volume.Center - Volume.BoxHalfExtents;
                max = Volume.Center + Volume.BoxHalfExtents;
            }
            else if (Volume.Type == FieldVolumeType.Sphere)
            {
                min = Volume.Center - new Vector3(Volume.SphereRadius);
                max = Volume.Center + new Vector3(Volume.SphereRadius);
            }
            else
            {
                // 无限体积：以 Volume.Center 为中心，使用默认范围
                min = Volume.Center - Vector3.One;
                max = Volume.Center + Vector3.One;
            }

            Vector3 size = max - min;
            if (size.X < 1e-6f) size.X = 1f;
            if (size.Y < 1e-6f) size.Y = 1f;
            if (size.Z < 1e-6f) size.Z = 1f;

            return new Vector3(
                (worldPos.X - min.X) / size.X,
                (worldPos.Y - min.Y) / size.Y,
                (worldPos.Z - min.Z) / size.Z);
        }

        /// <summary>三线性插值采样速度场。</summary>
        private Vector3 TrilinearSample(Vector3 uvw)
        {
            float fx = Math.Clamp(uvw.X, 0f, 1f) * (ResolutionX - 1);
            float fy = Math.Clamp(uvw.Y, 0f, 1f) * (ResolutionY - 1);
            float fz = Math.Clamp(uvw.Z, 0f, 1f) * (ResolutionZ - 1);

            int x0 = (int)MathF.Floor(fx), x1 = Math.Min(x0 + 1, ResolutionX - 1);
            int y0 = (int)MathF.Floor(fy), y1 = Math.Min(y0 + 1, ResolutionY - 1);
            int z0 = (int)MathF.Floor(fz), z1 = Math.Min(z0 + 1, ResolutionZ - 1);

            float tx = fx - x0, ty = fy - y0, tz = fz - z0;

            // 8 个角点的速度
            Vector3 c000 = GetVoxel(x0, y0, z0);
            Vector3 c100 = GetVoxel(x1, y0, z0);
            Vector3 c010 = GetVoxel(x0, y1, z0);
            Vector3 c110 = GetVoxel(x1, y1, z0);
            Vector3 c001 = GetVoxel(x0, y0, z1);
            Vector3 c101 = GetVoxel(x1, y0, z1);
            Vector3 c011 = GetVoxel(x0, y1, z1);
            Vector3 c111 = GetVoxel(x1, y1, z1);

            // 三线性插值
            Vector3 c00 = Vector3.Lerp(c000, c100, tx);
            Vector3 c10 = Vector3.Lerp(c010, c110, tx);
            Vector3 c01 = Vector3.Lerp(c001, c101, tx);
            Vector3 c11 = Vector3.Lerp(c011, c111, tx);

            Vector3 c0 = Vector3.Lerp(c00, c10, ty);
            Vector3 c1 = Vector3.Lerp(c01, c11, ty);

            return Vector3.Lerp(c0, c1, tz);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _texture3D?.Dispose();
            _texture3D = null;
            _uploaded = false;
            GC.SuppressFinalize(this);
        }

        ~VectorField() => Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    //  物理场管理器（与 PhysicsWorld 集成）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   物理场管理器。管理场景中所有激活的物理场，并在物理模拟的力施加阶段
    ///   采样场并将力/速度施加到 PhysicsWorld 中的刚体。
    ///
    ///   集成流程：
    ///     1. 在 PhysicsWorld.Step 之前调用 ApplyFields
    ///     2. ApplyFields 遍历所有场，对场体积内的刚体采样并施加力/速度
    ///     3. 对于力场，调用 PhysicsWorld.AddForce
    ///     4. 对于速度场，将采样速度叠加到刚体线速度（或通过冲量实现）
    /// </summary>
    public sealed class PhysicsFieldManager
    {
        private readonly List<IPhysicsField> _fields = new();
        private readonly PhysicsWorld? _world;

        /// <summary>速度场施加模式。</summary>
        public enum VelocityApplicationMode
        {
            /// <summary>将速度场作为力施加（F = v * dragCoefficient）。</summary>
            AsForce,
            /// <summary>直接叠加到刚体速度（适合轻粒子）。</summary>
            AddToVelocity,
            /// <summary>通过冲量施加（I = m * Δv）。</summary>
            AsImpulse,
        }

        /// <summary>速度场施加模式。</summary>
        public VelocityApplicationMode VelocityMode { get; set; } = VelocityApplicationMode.AsForce;

        /// <summary>AsForce 模式下的阻力系数（速度转力的转换系数）。</summary>
        public float DragCoefficient { get; set; } = 1.0f;

        public PhysicsFieldManager(PhysicsWorld? world = null)
        {
            _world = world;
        }

        /// <summary>添加一个物理场。</summary>
        public void AddField(IPhysicsField field)
        {
            if (field != null && !_fields.Contains(field))
                _fields.Add(field);
        }

        /// <summary>移除一个物理场。</summary>
        public bool RemoveField(IPhysicsField field) => _fields.Remove(field);

        /// <summary>清空所有物理场。</summary>
        public void ClearFields() => _fields.Clear();

        /// <summary>获取所有物理场。</summary>
        public IReadOnlyList<IPhysicsField> Fields => _fields;

        /// <summary>当前注册的物理场数量。</summary>
        public int FieldCount => _fields.Count;

        /// <summary>
        ///   在指定位置采样所有场的合力/合速度。
        /// </summary>
        /// <param name="position">世界空间位置。</param>
        /// <param name="deltaTime">帧间隔。</param>
        /// <returns>(总力, 总速度)。</returns>
        public (Vector3 totalForce, Vector3 totalVelocity) SampleAt(Vector3 position, float deltaTime)
        {
            Vector3 force = Vector3.Zero;
            Vector3 velocity = Vector3.Zero;
            foreach (var field in _fields)
            {
                if (!field.IsEnabled) continue;
                var v = field.Sample(position, deltaTime);
                if (field.FieldType == PhysicsFieldType.Force)
                    force += v;
                else
                    velocity += v;
            }
            return (force, velocity);
        }

        /// <summary>
        ///   将所有物理场施加到 PhysicsWorld 中的刚体。应在 PhysicsWorld.Step 之前调用。
        ///   此方法遍历场体积内的刚体，对每个刚体采样场并施加力/速度。
        ///
        ///   注：此方法需要 PhysicsWorld 实例。若构造时未传入 world，则使用
        ///   ApplyToBody 对单个刚体施加。
        /// </summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        /// <param name="bodyIds">需要施加场的刚体 ID 列表。</param>
        public void ApplyFields(float deltaTime, IEnumerable<uint> bodyIds)
        {
            if (_world == null) return;

            foreach (var bodyId in bodyIds)
            {
                Vector3 pos = _world.GetPosition(bodyId);
                var (force, velocity) = SampleAt(pos, deltaTime);

                // 施加力场
                if (force != Vector3.Zero)
                {
                    _world.AddForce(bodyId, force);
                }

                // 施加速度场
                if (velocity != Vector3.Zero)
                {
                    switch (VelocityMode)
                    {
                        case VelocityApplicationMode.AsForce:
                            // 将速度转为力：F = v * drag
                            _world.AddForce(bodyId, velocity * DragCoefficient);
                            break;
                        case VelocityApplicationMode.AddToVelocity:
                            var currentVel = _world.GetLinearVelocity(bodyId);
                            _world.SetLinearVelocity(bodyId, currentVel + velocity);
                            break;
                        case VelocityApplicationMode.AsImpulse:
                            _world.AddImpulse(bodyId, velocity);
                            break;
                    }
                }
            }
        }

        /// <summary>
        ///   将物理场施加到单个刚体（不依赖 PhysicsWorld 实例，由调用者负责施加）。
        ///   返回该位置应受的合力与合速度，由调用者决定如何施加。
        /// </summary>
        public (Vector3 force, Vector3 velocity) ApplyToBody(Vector3 position, float deltaTime)
        {
            return SampleAt(position, deltaTime);
        }

        /// <summary>
        ///   每帧更新所有场（部分场有内部时间演化需要推进）。
        ///   实际上 Sample 调用时已内部更新，此方法提供统一的更新入口。
        /// </summary>
        public void Update(float deltaTime)
        {
            // 场的内部状态在 Sample 时更新，此处无需额外操作。
            // 预留给未来需要全局协调的场逻辑。
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  ECS 集成
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   物理场 ECS 组件。将一个物理场附加到实体上，场的体积中心会跟随实体的
    ///   TransformComponent 位置。适用于跟随角色移动的风场、爆炸点等。
    /// </summary>
    public struct PhysicsFieldComponent : IComponent
    {
        /// <summary>该实体持有的物理场实例。</summary>
        public IPhysicsField? Field;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>场体积是否跟随实体位置移动（true=跟随，false=固定在世界空间）。</summary>
        public bool FollowEntity;

        /// <summary>是否将场注册到全局 PhysicsFieldManager。</summary>
        public bool RegisterToManager;
    }

    /// <summary>
    ///   物理场更新系统。每帧同步场体积中心到实体位置（如果 FollowEntity），
    ///   并确保场已注册到 PhysicsFieldManager。该系统应在物理步进之前更新。
    /// </summary>
    public sealed class PhysicsFieldUpdateSystem : ISystem
    {
        /// <summary>关联的物理场管理器（用于注册/注销场）。</summary>
        public PhysicsFieldManager? Manager { get; set; }

        /// <summary>系统名称。</summary>
        public string Name => "PhysicsFieldSystem";

        public PhysicsFieldUpdateSystem(PhysicsFieldManager? manager = null)
        {
            Manager = manager;
        }

        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;
            var entities = ecs.QueryWithComponent<PhysicsFieldComponent>();
            foreach (var id in entities)
            {
                var pfc = ecs.GetComponent<PhysicsFieldComponent>(id);
                if (!pfc.Enabled || pfc.Field == null) continue;

                // 同步场体积中心到实体位置
                if (pfc.FollowEntity && ecs.HasComponent<TransformComponent>(id))
                {
                    var transform = ecs.GetComponent<TransformComponent>(id);
                    var volume = pfc.Field.Volume;
                    volume.Center = transform.Position;
                    pfc.Field.Volume = volume;
                }

                // 自动注册到管理器
                if (pfc.RegisterToManager && Manager != null)
                {
                    if (!Manager.Fields.Contains(pfc.Field))
                        Manager.AddField(pfc.Field);
                }

                ecs.SetComponent(id, pfc);
            }

            // 更新管理器内部状态
            Manager?.Update(dt);
        }
    }

    /// <summary>
    ///   物理场系统管理器。提供场创建的便捷工厂方法，统一管理场资源。
    /// </summary>
    public sealed class PhysicsFieldSystem
    {
        private readonly PhysicsFieldManager _manager;

        public PhysicsFieldSystem(PhysicsWorld? world = null)
        {
            _manager = new PhysicsFieldManager(world);
        }

        /// <summary>获取物理场管理器。</summary>
        public PhysicsFieldManager Manager => _manager;

        /// <summary>创建并添加一个方向力场（如风）。</summary>
        public ForceField CreateDirectionalForceField(Vector3 direction, float strength,
            PhysicsFieldVolume? volume = null)
        {
            var field = new ForceField
            {
                Mode = ForceFieldMode.Directional,
                Direction = direction,
                Strength = strength,
                Volume = volume ?? PhysicsFieldVolume.Infinite(),
            };
            _manager.AddField(field);
            return field;
        }

        /// <summary>创建并添加一个径向力场（如爆炸）。</summary>
        public ForceField CreateRadialForceField(Vector3 center, float radius, float strength,
            bool outward = true, FalloffType falloff = FalloffType.Smoothstep)
        {
            var field = new ForceField
            {
                Mode = ForceFieldMode.Radial,
                RadialOutward = outward,
                Strength = strength,
                Volume = PhysicsFieldVolume.Sphere(center, radius, falloff),
            };
            _manager.AddField(field);
            return field;
        }

        /// <summary>创建并添加一个旋涡力场（如龙卷风）。</summary>
        public ForceField CreateVortexForceField(Vector3 center, float radius, Vector3 axis,
            float strength, FalloffType falloff = FalloffType.Smoothstep)
        {
            var field = new ForceField
            {
                Mode = ForceFieldMode.Vortex,
                VortexAxis = axis,
                Strength = strength,
                Volume = PhysicsFieldVolume.Sphere(center, radius, falloff),
            };
            _manager.AddField(field);
            return field;
        }

        /// <summary>创建并添加一个噪声湍流力场。</summary>
        public NoiseField CreateNoiseField(float frequency, float strength,
            PhysicsFieldVolume? volume = null)
        {
            var field = new NoiseField
            {
                Frequency = frequency,
                Strength = strength,
                Volume = volume ?? PhysicsFieldVolume.Infinite(),
            };
            _manager.AddField(field);
            return field;
        }

        /// <summary>创建并添加一个均匀矢量场（如恒定风）。</summary>
        public UniformVectorField CreateUniformVectorField(Vector3 velocity, float strength,
            PhysicsFieldVolume? volume = null)
        {
            var field = new UniformVectorField
            {
                Velocity = velocity,
                Strength = strength,
                Volume = volume ?? PhysicsFieldVolume.Infinite(),
            };
            _manager.AddField(field);
            return field;
        }

        /// <summary>创建并添加一个涡旋矢量场。</summary>
        public VortexField CreateVortexVectorField(Vector3 center, float radius, Vector3 axis,
            float strength, FalloffType falloff = FalloffType.Smoothstep)
        {
            var field = new VortexField
            {
                Axis = axis,
                Strength = strength,
                Volume = PhysicsFieldVolume.Sphere(center, radius, falloff),
            };
            _manager.AddField(field);
            return field;
        }

        /// <summary>创建并添加一个 3D 纹理矢量场（需后续 UploadToGPU）。</summary>
        public VectorField CreateTextureVectorField(int resolution, PhysicsFieldVolume volume)
        {
            var field = new VectorField
            {
                ResolutionX = resolution,
                ResolutionY = resolution,
                ResolutionZ = resolution,
                Volume = volume,
            };
            field.InitializeData();
            _manager.AddField(field);
            return field;
        }

        /// <summary>移除所有物理场。</summary>
        public void ClearAll() => _manager.ClearFields();
    }
}
