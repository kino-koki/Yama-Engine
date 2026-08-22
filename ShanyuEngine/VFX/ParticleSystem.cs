using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - VFX 粒子系统核心
    //
    // 类似 UE FlowVFX / Unity VFX Graph:
    //   * Particle 结构体: 完整粒子状态 (位置/速度/加速度/颜色/生命/大小/旋转/Alpha/自定义数据)
    //   * ParticleEmitter: 发射器 (发射速率/突发/寿命/初始速度/方向锥体/球面/平面发射)
    //   * ParticleModule 基类 + 20+ 内置模块
    //   * ParticleSystem: 发射器/模块管理 + 粒子池 + Update/CPU-GPU 切换
    //   * 粒子渲染: Billboard/Point/Line/Mesh/Trail/Beam/Ribbon/GPU Instanced
    //   * 粒子 LOD / 视锥剔除 / 距离剔除 / 屏幕空间大小剔除
    //   * 粒子池化: 循环缓冲区 + 死亡粒子复用
    // ════════════════════════════════════════════════════════════════════════

    #region 枚举

    /// <summary>粒子模拟空间。</summary>
    public enum ParticleSimulationSpace
    {
        Local,
        World,
        SimulatingInLocalSpace
    }

    /// <summary>VFX 粒子渲染模式。</summary>
    public enum VFXParticleRenderMode
    {
        Billboard,
        Point,
        Line,
        Mesh,
        Trail,
        Beam,
        Ribbon,
        GPUInstanced,
        StretchedBillboard
    }

    /// <summary>VFX 粒子排序模式。</summary>
    public enum VFXParticleSortMode
    {
        None,
        ByDistance,
        ByAge,
        ByLifetime
    }

    /// <summary>VFX 碰撞响应类型。</summary>
    public enum VFXCollisionResponse
    {
        None,
        Bounce,
        Stick,
        Kill,
        Spawn
    }

    /// <summary>发射器形状。</summary>
    public enum EmitterShape
    {
        Point,
        Cone,
        Sphere,
        Hemisphere,
        Box,
        Plane,
        Circle,
        Ring,
        Line,
        MeshSurface
    }

    /// <summary>计算模式 (CPU/GPU)。</summary>
    public enum ParticleComputeMode
    {
        CPU,
        GPU
    }

    /// <summary>曲线采样模式。</summary>
    public enum CurveSamplingMode
    {
        Constant,
        Linear,
        Curve
    }

    /// <summary>拖尾渲染模式。</summary>
    public enum TrailRenderMode
    {
        Line,
        Ribbon,
        Tube
    }

    #endregion

    #region 数学辅助

    /// <summary>最小-最大曲线 (浮点)。</summary>
    public struct MinMaxCurve
    {
        public float Constant;
        public float Min;
        public float Max;
        public bool IsTwoValues;
        public bool IsCurve;
        public CurveSamplingMode SamplingMode;

        public MinMaxCurve(float value)
        {
            Constant = value;
            Min = value;
            Max = value;
            IsTwoValues = false;
            IsCurve = false;
            SamplingMode = CurveSamplingMode.Constant;
        }

        public MinMaxCurve(float min, float max)
        {
            Min = min;
            Max = max;
            Constant = (min + max) * 0.5f;
            IsTwoValues = true;
            IsCurve = false;
            SamplingMode = CurveSamplingMode.Linear;
        }

        public MinMaxCurve(float min, float max, bool useCurve)
        {
            Min = min;
            Max = max;
            Constant = (min + max) * 0.5f;
            IsTwoValues = true;
            IsCurve = useCurve;
            SamplingMode = useCurve ? CurveSamplingMode.Curve : CurveSamplingMode.Linear;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Evaluate(float t, float random)
        {
            if (!IsTwoValues) return Constant;
            if (IsCurve)
            {
                // 简化的曲线采样: 使用 Hermite 插值
                float a = Min + (Max - Min) * Math.Clamp(t, 0, 1);
                return a;
            }
            return Min + (Max - Min) * random;
        }

        public static implicit operator MinMaxCurve(float v) => new MinMaxCurve(v);
    }

    /// <summary>最小-最大曲线 (颜色)。</summary>
    public struct MinMaxColorCurve
    {
        public Vector4 Constant;
        public Vector4 Min;
        public Vector4 Max;
        public bool IsTwoValues;

        public MinMaxColorCurve(Vector4 value)
        {
            Constant = value;
            Min = value;
            Max = value;
            IsTwoValues = false;
        }

        public MinMaxColorCurve(Vector4 min, Vector4 max)
        {
            Constant = (min + max) * 0.5f;
            Min = min;
            Max = max;
            IsTwoValues = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 Evaluate(float random)
        {
            if (!IsTwoValues) return Constant;
            return Vector4.Lerp(Min, Max, random);
        }

        public Vector4 EvaluateOverLife(float lifeRatio, float random)
        {
            if (!IsTwoValues) return Constant;
            return Vector4.Lerp(Min, Max, random * lifeRatio);
        }

        public static implicit operator MinMaxColorCurve(Vector4 v) => new MinMaxColorCurve(v);
    }

    /// <summary>简易浮点曲线 (离散采样)。</summary>
    public class FloatCurve
    {
        private readonly List<(float Time, float Value)> _keys = new List<(float, float)>();
        public int KeyCount => _keys.Count;

        public void AddKey(float time, float value) => _keys.Add((time, value));

        public float Evaluate(float time)
        {
            if (_keys.Count == 0) return 0f;
            if (_keys.Count == 1) return _keys[0].Value;

            // 二分查找
            int lo = 0, hi = _keys.Count - 1;
            if (time <= _keys[lo].Time) return _keys[lo].Value;
            if (time >= _keys[hi].Time) return _keys[hi].Value;

            while (lo < hi - 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid].Time <= time) lo = mid;
                else hi = mid;
            }

            float t = (time - _keys[lo].Time) / (_keys[hi].Time - _keys[lo].Time);
            return MathHelper.Lerp(_keys[lo].Value, _keys[hi].Value, t);
        }
    }

    /// <summary>简易颜色曲线。</summary>
    public class ColorCurve
    {
        private readonly List<(float Time, Vector4 Value)> _keys = new List<(float, Vector4)>();
        public int KeyCount => _keys.Count;

        public void AddKey(float time, Vector4 value) => _keys.Add((time, value));

        public Vector4 Evaluate(float time)
        {
            if (_keys.Count == 0) return Vector4.One;
            if (_keys.Count == 1) return _keys[0].Value;

            int lo = 0, hi = _keys.Count - 1;
            if (time <= _keys[lo].Time) return _keys[lo].Value;
            if (time >= _keys[hi].Time) return _keys[hi].Value;

            while (lo < hi - 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid].Time <= time) lo = mid;
                else hi = mid;
            }

            float t = (time - _keys[lo].Time) / (_keys[hi].Time - _keys[lo].Time);
            return Vector4.Lerp(_keys[lo].Value, _keys[hi].Value, t);
        }
    }

    #endregion

    #region Particle 结构体

    /// <summary>
    /// 粒子数据结构。包含完整的粒子状态。
    /// </summary>
    public struct Particle
    {
        // ── 位置/运动 ──
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Acceleration;

        // ── 视觉 ──
        public Vector4 Color;
        public float Size;
        public float Rotation;
        public float RotationSpeed;
        public float Alpha;

        // ── 生命 ──
        public float Life;
        public float MaxLife;

        // ── 自定义 ──
        public Vector4 CustomData;
        public float RandomSeed;

        // ── 状态 ──
        public bool IsAlive;
        public int EmitterIndex;

        // ── 碰撞 ──
        public Vector3 PreviousPosition;

        /// <summary>生命归一化比例 [0..1]。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly float LifeRatio => MaxLife > 0f ? Life / MaxLife : 0f;

        /// <summary>创建一个新的死亡粒子。</summary>
        public static Particle Dead() => new Particle
        {
            IsAlive = false,
            Life = 0f,
            MaxLife = 1f,
            Size = 1f,
            Color = Vector4.One,
            Alpha = 1f,
            Rotation = 0f,
            CustomData = Vector4.Zero,
            RandomSeed = 0f
        };

        /// <summary>初始化粒子。</summary>
        public void Initialize(float maxLife, float seed)
        {
            IsAlive = true;
            Life = maxLife;
            MaxLife = maxLife;
            RandomSeed = seed;
            Position = Vector3.Zero;
            Velocity = Vector3.Zero;
            Acceleration = Vector3.Zero;
            Color = Vector4.One;
            Size = 1f;
            Rotation = 0f;
            RotationSpeed = 0f;
            Alpha = 1f;
            CustomData = Vector4.Zero;
            PreviousPosition = Vector3.Zero;
        }
    }

    #endregion

    #region 拖尾数据

    /// <summary>拖尾历史数据点。</summary>
    public struct TrailPoint
    {
        public Vector3 Position;
        public float Width;
        public Vector4 Color;
        public float Life;
    }

    /// <summary>拖尾数据缓冲。</summary>
    public struct TrailData
    {
        public TrailPoint[] Points;
        public int Head;
        public int Count;
        public int MaxPoints;
        public bool IsActive;

        public TrailData(int maxPoints)
        {
            MaxPoints = maxPoints;
            Points = new TrailPoint[maxPoints];
            Head = 0;
            Count = 0;
            IsActive = true;
        }

        public void AddPoint(Vector3 position, float width, Vector4 color, float life)
        {
            if (MaxPoints <= 0) return;
            Points[Head] = new TrailPoint
            {
                Position = position,
                Width = width,
                Color = color,
                Life = life
            };
            Head = (Head + 1) % MaxPoints;
            Count = Math.Min(Count + 1, MaxPoints);
        }

        public void Reset()
        {
            Head = 0;
            Count = 0;
        }
    }

    #endregion

    #region ParticleModule 基类

    /// <summary>
    /// 粒子模块基类。所有粒子行为模块的抽象。
    /// 类似 UE FlowVFX Module / Unity VFX Graph Block。
    /// </summary>
    public abstract class ParticleModuleCore
    {
        public bool Enabled { get; set; } = true;
        public string ModuleName { get; protected set; } = "UnnamedModule";

        /// <summary>模块类型标记 (Spawn/Update/Render)。</summary>
        public enum ModuleStage
        {
            Spawn,
            Update,
            Render
        }

        public ModuleStage Stage { get; protected set; } = ModuleStage.Update;

        /// <summary>模块执行顺序 (越小越先执行)。</summary>
        public int ExecutionOrder { get; set; } = 0;

        /// <summary>是否需要GPU支持。</summary>
        public bool RequiresGPU { get; set; } = false;

        /// <summary>初始化单个粒子 (在 Spawn 时调用)。</summary>
        public virtual void InitializeParticle(ref Particle particle, float randomSeed) { }

        /// <summary>更新单个粒子 (每帧调用)。</summary>
        public virtual void UpdateParticle(ref Particle particle, float dt, float randomSeed) { }

        /// <summary>模块 Tick (全局, 非 per-particle)。</summary>
        public virtual void Update(float systemTime, float dt) { }

        /// <summary>重置模块内部状态。</summary>
        public virtual void Reset() { }
    }

    #endregion

    #region 内置 Spawn 模块

    /// <summary>突发发射模块: 一次性发射指定数量粒子。</summary>
    public class SpawnBurstModule : ParticleModuleCore
    {
        public struct BurstInfo
        {
            public float Time;
            public int Count;
            public float CountVariance;
            public bool HasFired;
            public int CycleCount;
            public float CycleInterval;
        }

        public List<BurstInfo> Bursts = new List<BurstInfo>();
        public int TotalBurstParticlesEmitted { get; private set; }

        public SpawnBurstModule()
        {
            Stage = ModuleStage.Spawn;
            ModuleName = "SpawnBurst";
            ExecutionOrder = -100;
        }

        public void AddBurst(float time, int count, float countVariance = 0, int cycleCount = 1, float cycleInterval = 0)
        {
            Bursts.Add(new BurstInfo
            {
                Time = time,
                Count = count,
                CountVariance = countVariance,
                HasFired = false,
                CycleCount = cycleCount,
                CycleInterval = cycleInterval
            });
        }

        public int GetBurstCount(float systemTime)
        {
            int total = 0;
            foreach (var burst in Bursts)
            {
                if (burst.CycleCount > 1)
                {
                    float cycleTime = systemTime % (burst.CycleCount * burst.CycleInterval);
                    if (Math.Abs(cycleTime - burst.Time) < 0.016f && !burst.HasFired)
                    {
                        total += burst.Count + (int)(burst.CountVariance * (Random.Shared.NextSingle() * 2 - 1));
                        burst.HasFired = true;
                    }
                }
                else if (!burst.HasFired && systemTime >= burst.Time)
                {
                    total += burst.Count + (int)(burst.CountVariance * (Random.Shared.NextSingle() * 2 - 1));
                    burst.HasFired = true;
                }
            }
            TotalBurstParticlesEmitted += total;
            return total;
        }

        public override void Reset()
        {
            TotalBurstParticlesEmitted = 0;
            for (int i = 0; i < Bursts.Count; i++)
                Bursts[i] = new BurstInfo
                {
                    Time = Bursts[i].Time,
                    Count = Bursts[i].Count,
                    CountVariance = Bursts[i].CountVariance,
                    HasFired = false,
                    CycleCount = Bursts[i].CycleCount,
                    CycleInterval = Bursts[i].CycleInterval
                };
        }
    }

    /// <summary>持续发射模块: 按速率持续生成粒子。</summary>
    public class SpawnRateModule : ParticleModuleCore
    {
        public MinMaxCurve Rate;
        public float RateOverDistance;
        public float Accumulator;
        public float DistanceAccumulator;

        public SpawnRateModule(float rate = 10f)
        {
            Rate = new MinMaxCurve(rate);
            Stage = ModuleStage.Spawn;
            ModuleName = "SpawnRate";
            ExecutionOrder = -99;
        }

        public int GetSpawnCount(float dt, float deltaDistance)
        {
            float rate = Rate.Evaluate(0, Random.Shared.NextSingle());
            Accumulator += rate * dt;
            DistanceAccumulator += RateOverDistance * deltaDistance;

            int count = (int)Accumulator + (int)DistanceAccumulator;
            if (count > 0)
            {
                float consumed = Math.Min(Accumulator, count);
                Accumulator -= consumed;
                DistanceAccumulator -= (count - (int)consumed);
            }
            return Math.Max(0, count);
        }

        public override void Reset()
        {
            Accumulator = 0f;
            DistanceAccumulator = 0f;
        }
    }

    #endregion

    #region 内置 Update 模块

    /// <summary>生命周期模块: 控制粒子寿命。</summary>
    public class LifetimeModule : ParticleModuleCore
    {
        public MinMaxCurve MinLifetime = new MinMaxCurve(1f);
        public MinMaxCurve MaxLifetime = new MinMaxCurve(1f);
        public bool UseRandomRange;

        public LifetimeModule(float minLife = 1f, float maxLife = 1f)
        {
            MinLifetime = new MinMaxCurve(minLife);
            MaxLifetime = new MinMaxCurve(maxLife);
            ModuleName = "Lifetime";
            Stage = ModuleStage.Spawn;
            ExecutionOrder = -98;
        }

        public float GetLifetime(float random)
        {
            if (UseRandomRange)
                return MinLifetime.Evaluate(0, random) + (MaxLifetime.Evaluate(0, random) - MinLifetime.Evaluate(0, random)) * random;
            return MaxLifetime.Evaluate(0, random);
        }

        public override void InitializeParticle(ref Particle particle, float randomSeed)
        {
            particle.MaxLife = GetLifetime(randomSeed);
            particle.Life = particle.MaxLife;
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            particle.Life -= dt;
            if (particle.Life <= 0f)
                particle.IsAlive = false;
        }
    }

    /// <summary>大小模块: 控制粒子大小 (含随生命曲线)。</summary>

    /// <summary>颜色模块: 控制粒子颜色 (初始/渐变/随机/曲线)。</summary>

    /// <summary>速度模块: 初速度/方向/锥角/速度曲线。</summary>
    public class VelocityModule : ParticleModuleCore
    {
        public MinMaxCurve Speed = new MinMaxCurve(1f);
        public EmitterShape Shape = EmitterShape.Cone;
        public float ConeAngle = 15f; // 度
        public Vector3 Direction = Vector3.UnitY;
        public bool DirectionFromEmitter;
        public bool RandomDirection;
        public FloatCurve SpeedOverLife;

        public VelocityModule(float speed = 1f)
        {
            Speed = new MinMaxCurve(speed);
            ModuleName = "Velocity";
            Stage = ModuleStage.Spawn;
            ExecutionOrder = -90;
        }

        public Vector3 GetInitialVelocity(float randomSeed)
        {
            float spd = Speed.Evaluate(0, randomSeed);

            if (RandomDirection)
                return GetRandomDirection() * spd;

            switch (Shape)
            {
                case EmitterShape.Cone:
                    return GetConeDirection(ConeAngle, randomSeed) * spd;
                case EmitterShape.Sphere:
                    return GetRandomDirection() * spd;
                case EmitterShape.Hemisphere:
                    return GetHemisphereDirection(randomSeed) * spd;
                case EmitterShape.Plane:
                case EmitterShape.Circle:
                    return GetCircleDirection(randomSeed) * spd;
                default:
                    return Direction * spd;
            }
        }

        public override void InitializeParticle(ref Particle particle, float randomSeed)
        {
            particle.Velocity = GetInitialVelocity(randomSeed);
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            if (SpeedOverLife != null && SpeedOverLife.KeyCount > 0)
            {
                float speedScale = SpeedOverLife.Evaluate(particle.LifeRatio);
                if (particle.Velocity.LengthSquared() > 0.0001f)
                {
                    particle.Velocity = Vector3.Normalize(particle.Velocity) *
                        particle.Velocity.Length() * speedScale;
                }
            }
        }

        private static Vector3 GetRandomDirection()
        {
            float theta = Random.Shared.NextSingle() * MathF.PI * 2;
            float phi = MathF.Acos(2f * Random.Shared.NextSingle() - 1f);
            return new Vector3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Cos(phi),
                MathF.Sin(phi) * MathF.Sin(theta));
        }

        private Vector3 GetConeDirection(float angleDeg, float seed)
        {
            float angleRad = angleDeg * MathHelper.DegreesToRadians;
            float halfAngle = angleRad * 0.5f;
            float r = MathF.Sqrt(seed) * MathF.Sin(halfAngle);
            float z = MathF.Cos(halfAngle);
            float theta = seed * MathF.PI * 2;
            return new Vector3(r * MathF.Cos(theta), z, r * MathF.Sin(theta));
        }

        private static Vector3 GetHemisphereDirection(float seed)
        {
            float theta = seed * MathF.PI * 2;
            float phi = MathF.Acos(seed) * 0.5f;
            return new Vector3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Cos(phi),
                MathF.Sin(phi) * MathF.Sin(theta));
        }

        private static Vector3 GetCircleDirection(float seed)
        {
            float theta = seed * MathF.PI * 2;
            return new Vector3(MathF.Cos(theta), 0, MathF.Sin(theta));
        }
    }

    /// <summary>重力模块: 施加重力加速度。</summary>
    public class GravityModule : ParticleModuleCore
    {
        public Vector3 Gravity = new Vector3(0, -9.81f, 0);
        public float GravityScale = 1f;
        public bool WorldGravity;

        public GravityModule(float gravityY = -9.81f)
        {
            Gravity = new Vector3(0, gravityY, 0);
            ModuleName = "Gravity";
            ExecutionOrder = 30;
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            particle.Acceleration = Gravity * GravityScale;
            particle.Velocity += particle.Acceleration * dt;
        }
    }

    /// <summary>空气阻力模块。</summary>
    public class DragModule : ParticleModuleCore
    {
        public float LinearDrag;
        public float QuadraticDrag;
        public bool DragOverLife;
        public FloatCurve DragCurve;

        public DragModule(float linearDrag = 0f, float quadraticDrag = 0f)
        {
            LinearDrag = linearDrag;
            QuadraticDrag = quadraticDrag;
            ModuleName = "Drag";
            ExecutionOrder = 35;
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            float dragCoeff = LinearDrag;
            if (DragOverLife && DragCurve != null)
                dragCoeff = DragCurve.Evaluate(particle.LifeRatio);

            float speed = particle.Velocity.Length();
            if (speed < 0.0001f) return;

            float dragForce = dragCoeff + QuadraticDrag * speed;
            float newSpeed = Math.Max(0f, speed - dragForce * dt);
            particle.Velocity = Vector3.Normalize(particle.Velocity) * newSpeed;
        }
    }

    /// <summary>旋转模块。</summary>
    public class RotationModule : ParticleModuleCore
    {
        public float InitialRotationMin;
        public float InitialRotationMax = MathF.PI * 2;
        public bool RandomInitialRotation;
        public MinMaxCurve RotationSpeed = new MinMaxCurve(0f);
        public FloatCurve RotationOverLife;

        public RotationModule()
        {
            RandomInitialRotation = true;
            ModuleName = "Rotation";
            ExecutionOrder = 25;
        }

        public override void InitializeParticle(ref Particle particle, float randomSeed)
        {
            particle.Rotation = RandomInitialRotation
                ? InitialRotationMin + (InitialRotationMax - InitialRotationMin) * randomSeed
                : 0f;
            particle.RotationSpeed = RotationSpeed.Evaluate(0, randomSeed);
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            float rotSpeed = particle.RotationSpeed;
            if (RotationOverLife != null && RotationOverLife.KeyCount > 0)
                rotSpeed = RotationOverLife.Evaluate(particle.LifeRatio);

            particle.Rotation += rotSpeed * dt;
        }
    }

    /// <summary>透明度模块: 淡入/淡出曲线。</summary>
    public class AlphaModule : ParticleModuleCore
    {
        public float InitialAlpha = 1f;
        public bool FadeIn;
        public float FadeInDuration = 0.2f;
        public bool FadeOut;
        public float FadeOutDuration = 0.5f;
        public FloatCurve AlphaOverLife;

        public AlphaModule()
        {
            ModuleName = "Alpha";
            ExecutionOrder = 22;
        }

        public override void InitializeParticle(ref Particle particle, float randomSeed)
        {
            particle.Alpha = InitialAlpha;
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            if (AlphaOverLife != null && AlphaOverLife.KeyCount > 0)
            {
                particle.Alpha = AlphaOverLife.Evaluate(particle.LifeRatio);
                return;
            }

            float alpha = InitialAlpha;

            if (FadeIn)
            {
                float age = particle.MaxLife - particle.Life;
                alpha *= Math.Min(1f, age / FadeInDuration);
            }

            if (FadeOut)
            {
                alpha *= Math.Min(1f, particle.Life / FadeOutDuration);
            }

            particle.Alpha = alpha;
        }
    }

    /// <summary>碰撞检测模块: 碰撞 + 弹跳/粘附/杀死。</summary>
    public class CollisionModule : ParticleModuleCore
    {
        public float CollisionRadius = 0.01f;
        public float CollisionHeight = 0.01f;
        public VFXCollisionResponse Response = VFXCollisionResponse.Bounce;
        public float Bounciness = 0.5f;
        public float Friction = 0.1f;
        public Vector3 CollisionPlaneNormal = Vector3.UnitY;
        public float CollisionPlaneOffset;
        public bool UseWorldCollision;

        public CollisionModule()
        {
            ModuleName = "Collision";
            ExecutionOrder = 80;
        }

        public override void InitializeParticle(ref Particle particle, float randomSeed)
        {
            particle.PreviousPosition = particle.Position;
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            particle.PreviousPosition = particle.Position;

            // 简单平面碰撞
            if (!UseWorldCollision)
            {
                float dist = Vector3.Dot(particle.Position, CollisionPlaneNormal) - CollisionPlaneOffset;
                float prevDist = Vector3.Dot(particle.PreviousPosition, CollisionPlaneNormal) - CollisionPlaneOffset;

                if (dist < CollisionRadius && prevDist >= CollisionRadius)
                {
                    switch (Response)
                    {
                        case VFXCollisionResponse.Bounce:
                            particle.Position += CollisionPlaneNormal * (CollisionRadius - dist);
                            particle.Velocity = Vector3.Reflect(particle.Velocity, CollisionPlaneNormal) * Bounciness;
                            // 摩擦: 减少切线速度
                            Vector3 tangent = particle.Velocity - Vector3.Dot(particle.Velocity, CollisionPlaneNormal) * CollisionPlaneNormal;
                            particle.Velocity -= tangent * Friction;
                            break;

                        case VFXCollisionResponse.Stick:
                            particle.Position += CollisionPlaneNormal * (CollisionRadius - dist);
                            particle.Velocity = Vector3.Zero;
                            particle.Acceleration = Vector3.Zero;
                            break;

                        case VFXCollisionResponse.Kill:
                            particle.IsAlive = false;
                            break;
                    }
                }
            }
        }
    }

    /// <summary>噪声扰动模块。</summary>

    /// <summary>湍流模块: 3D Curl Noise。</summary>
    public class TurbulenceModule : ParticleModuleCore
    {
        public float Strength = 1f;
        public float Frequency = 1f;
        public int Octaves = 3;
        public float Lacunarity = 2f;
        public float Persistence = 0.5f;
        public float Drag = 0f;

        public TurbulenceModule(float strength = 1f)
        {
            Strength = strength;
            ModuleName = "Turbulence";
            ExecutionOrder = 55;
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            // 3D Curl Noise 逼近 (基于偏导数差的旋转场)
            Vector3 pos = particle.Position * Frequency;
            float eps = 0.01f;

            // 简化 Curl: 使用 sin-based 旋转场
            float cx = CurlX(pos, eps);
            float cy = CurlY(pos, eps);
            float cz = CurlZ(pos, eps);

            Vector3 curlForce = new Vector3(cx, cy, cz) * Strength;

            if (Drag > 0f)
            {
                particle.Velocity = Vector3.Lerp(particle.Velocity + curlForce * dt, particle.Velocity, Math.Exp(-Drag * dt));
            }
            else
            {
                particle.Velocity += curlForce * dt;
            }
        }

        private static float Noise3D(float x, float y, float z)
        {
            return MathF.Sin(x * 1.27f + y * 2.14f + z * 0.89f + MathF.Sin(x * y * 0.71f + z * 1.53f) * 2.77f) * 0.5f;
        }

        private static float CurlX(Vector3 p, float e)
        {
            float dy = Noise3D(p.X, p.Y + e, p.Z) - Noise3D(p.X, p.Y - e, p.Z);
            float dz = Noise3D(p.X, p.Y, p.Z + e) - Noise3D(p.X, p.Y, p.Z - e);
            return (dy - dz) / (2f * e);
        }

        private static float CurlY(Vector3 p, float e)
        {
            float dx = Noise3D(p.X + e, p.Y, p.Z) - Noise3D(p.X - e, p.Y, p.Z);
            float dz = Noise3D(p.X, p.Y, p.Z + e) - Noise3D(p.X, p.Y, p.Z - e);
            return (dz - dx) / (2f * e);
        }

        private static float CurlZ(Vector3 p, float e)
        {
            float dx = Noise3D(p.X + e, p.Y, p.Z) - Noise3D(p.X - e, p.Y, p.Z);
            float dy = Noise3D(p.X, p.Y + e, p.Z) - Noise3D(p.X, p.Y - e, p.Z);
            return (dx - dy) / (2f * e);
        }
    }

    /// <summary>外力模块: 吸引/排斥/旋涡/风力场。</summary>
    public class ForceModule : ParticleModuleCore
    {
        public enum ForceType
        {
            Attract,
            Repel,
            Vortex,
            Wind,
            Point
        }

        public ForceType Type = ForceType.Attract;
        public Vector3 ForcePosition = Vector3.Zero;
        public Vector3 ForceDirection = Vector3.UnitY;
        public float Strength = 10f;
        public float Radius = 10f;
        public float Falloff = 1f; // 0 = 常量, 1 = 线性衰减
        public bool AffectsRotation;
        public float RotationStrength = 1f;

        public ForceModule()
        {
            ModuleName = "Force";
            ExecutionOrder = 40;
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            Vector3 force = Vector3.Zero;

            switch (Type)
            {
                case ForceType.Attract:
                case ForceType.Repel:
                case ForceType.Point:
                {
                    Vector3 toParticle = particle.Position - ForcePosition;
                    float dist = toParticle.Length();
                    if (dist > Radius || dist < 0.001f) break;
                    float falloff = Falloff > 0 ? 1f - dist / Radius : 1f;
                    Vector3 dir = Vector3.Normalize(toParticle);
                    float sign = Type == ForceType.Repel ? -1f : 1f;
                    force = -dir * sign * Strength * falloff;
                    break;
                }

                case ForceType.Vortex:
                {
                    Vector3 toCenter = ForcePosition - particle.Position;
                    float dist = toCenter.Length();
                    if (dist > Radius || dist < 0.001f) break;
                    float falloff = Falloff > 0 ? 1f - dist / Radius : 1f;
                    Vector3 up = Vector3.UnitY;
                    Vector3 radial = Vector3.Cross(up, Vector3.Normalize(toCenter));
                    force = radial * Strength * falloff;
                    break;
                }

                case ForceType.Wind:
                    force = ForceDirection * Strength;
                    break;
            }

            particle.Velocity += force * dt;

            if (AffectsRotation)
            {
                particle.RotationSpeed += force.Length() * RotationStrength * dt;
            }
        }
    }

    /// <summary>轨道运动模块。</summary>

    /// <summary>粒子发光模块: 粒子作为点光源。</summary>
    public class LightModule : ParticleModuleCore
    {
        public bool EmitLight;
        public float LightRadius = 5f;
        public float LightIntensity = 1f;
        public bool ColorFromParticle = true;
        public Vector3 LightColor = Vector3.One;
        public float IntensityOverLife = 1f;
        public bool AffectsWorld = true;

        public LightModule()
        {
            ModuleName = "Light";
            Stage = ModuleStage.Render;
            ExecutionOrder = 100;
        }

        public Vector3 GetLightPosition(ref Particle particle)
        {
            return particle.Position;
        }

        public Vector3 GetLightColor(ref Particle particle)
        {
            if (ColorFromParticle)
                return new Vector3(particle.Color.X, particle.Color.Y, particle.Color.Z);
            return LightColor;
        }

        public float GetLightIntensity(ref Particle particle)
        {
            return LightIntensity * particle.Alpha * IntensityOverLife * particle.LifeRatio;
        }
    }

    /// <summary>拖尾模块: 记录历史位置并渲染拖尾。</summary>
    public class TrailModule : ParticleModuleCore
    {
        public TrailRenderMode RenderMode = TrailRenderMode.Ribbon;
        public int MaxTrailPoints = 64;
        public float TrailWidth = 0.5f;
        public MinMaxCurve WidthOverLife = new MinMaxCurve(0.5f);
        public bool ColorFromParticle = true;
        public float TrailLifetime = 0.5f;
        public int TrailSegments = 16;
        public float UpdateInterval = 0.01f;
        public float _updateTimer;

        private Dictionary<int, TrailData> _trailData = new Dictionary<int, TrailData>();

        public TrailModule()
        {
            ModuleName = "Trail";
            Stage = ModuleStage.Render;
            ExecutionOrder = 90;
        }

        public override void InitializeParticle(ref Particle particle, float randomSeed)
        {
            _trailData[particle.GetHashCode()] = new TrailData(MaxTrailPoints);
        }

        public override void UpdateParticle(ref Particle particle, float dt, float randomSeed)
        {
            _updateTimer += dt;
            if (_updateTimer < UpdateInterval) return;
            _updateTimer = 0f;

            int key = particle.GetHashCode();
            if (!_trailData.TryGetValue(key, out var trail))
            {
                trail = new TrailData(MaxTrailPoints);
                _trailData[key] = trail;
            }

            float width = WidthOverLife.Evaluate(particle.LifeRatio, randomSeed) * TrailWidth;
            Vector4 color = ColorFromParticle ? particle.Color : Vector4.One;
            trail.AddPoint(particle.Position, width, color, particle.Life);
        }

        public bool TryGetTrail(int particleHash, out TrailData trail)
        {
            return _trailData.TryGetValue(particleHash, out trail);
        }

        public override void Reset()
        {
            _trailData.Clear();
            _updateTimer = 0f;
        }
    }

    /// <summary>子纹理动画模块。</summary>
    public class SubUVModule : ParticleModuleCore
    {
        public int Columns = 4;
        public int Rows = 4;
        public float FramesPerSecond = 30f;
        public bool RandomStartFrame;
        public bool Flipbook;
        public int RandomStartColumn;
        public int RandomStartRow;

        private float _frameTimer;

        public SubUVModule(int columns = 4, int rows = 4)
        {
            Columns = columns;
            Rows = rows;
            ModuleName = "SubUV";
            Stage = ModuleStage.Render;
            ExecutionOrder = 95;
        }

        public (int Column, int Row, float UVX, float UVY) GetCurrentFrame(float particleLife, float randomSeed)
        {
            int totalFrames = Columns * Rows;
            int frame;
            if (RandomStartFrame)
            {
                int startFrame = RandomStartRow * Columns + RandomStartColumn;
                frame = (startFrame + (int)(particleLife * FramesPerSecond)) % totalFrames;
            }
            else
            {
                frame = (int)(particleLife * FramesPerSecond) % totalFrames;
            }

            int col = frame % Columns;
            int row = frame / Columns;
            float uvX = (float)col / Columns;
            float uvY = (float)row / Rows;
            return (col, row, uvX, uvY);
        }

        public float GetUSize() => 1f / Columns;
        public float GetVSize() => 1f / Rows;
    }

    /// <summary>事件模块: 死亡/碰撞事件触发。</summary>
    public class EventModule : ParticleModuleCore
    {
        public delegate void ParticleEventAction(ref Particle particle);

        public event ParticleEventAction OnParticleDeath;
        public event ParticleEventAction OnParticleCollision;
        public event ParticleEventAction OnParticleSpawn;

        public bool FireOnDeath;
        public bool FireOnCollision;
        public bool FireOnSpawn;
        public int MaxEventsPerFrame = 10;
        private int _eventsThisFrame;

        public EventModule()
        {
            ModuleName = "Event";
            ExecutionOrder = 200;
        }

        public void NotifyDeath(ref Particle particle)
        {
            if (FireOnDeath && OnParticleDeath != null && _eventsThisFrame < MaxEventsPerFrame)
            {
                _eventsThisFrame++;
                OnParticleDeath(ref particle);
            }
        }

        public void NotifyCollision(ref Particle particle)
        {
            if (FireOnCollision && OnParticleCollision != null && _eventsThisFrame < MaxEventsPerFrame)
            {
                _eventsThisFrame++;
                OnParticleCollision(ref particle);
            }
        }

        public void NotifySpawn(ref Particle particle)
        {
            if (FireOnSpawn && OnParticleSpawn != null)
            {
                OnParticleSpawn(ref particle);
            }
        }

        public override void Update(float systemTime, float dt)
        {
            _eventsThisFrame = 0;
        }

        public override void InitializeParticle(ref Particle particle, float randomSeed)
        {
            NotifySpawn(ref particle);
        }
    }

    #endregion

    #region ParticleEmitter

    /// <summary>
    /// 粒子发射器。管理发射逻辑和粒子生命周期。
    /// </summary>
    public class ParticleEmitter
    {
        // ── 标识 ──
        public string Name { get; set; } = "Emitter";
        public bool IsEnabled { get; set; } = true;

        // ── 发射参数 ──
        public EmitterShape Shape { get; set; } = EmitterShape.Cone;
        public float ConeAngle { get; set; } = 15f;
        public float SphereRadius { get; set; } = 1f;
        public Vector3 BoxSize { get; set; } = Vector3.One;
        public float PlaneWidth { get; set; } = 1f;
        public float PlaneHeight { get; set; } = 1f;
        public Vector3 PositionOffset { get; set; } = Vector3.Zero;
        public Vector3 RotationEuler { get; set; } = Vector3.Zero;

        // ── 粒子参数 ──
        public int MaxParticles { get; set; } = 10000;
        public ParticleSimulationSpace SimulationSpace { get; set; } = ParticleSimulationSpace.Local;

        // ── 渲染 ──
        public VFXParticleRenderMode RenderMode { get; set; } = VFXParticleRenderMode.Billboard;
        public VFXParticleSortMode SortMode { get; set; } = VFXParticleSortMode.ByDistance;
        public bool CastShadows { get; set; }
        public bool ReceiveShadows { get; set; }
        public string MaterialAsset { get; set; }
        public string MeshAsset { get; set; }

        // ── 内部 ──
        public float SystemTime { get; private set; }
        public int ActiveParticleCount { get; private set; }

        /// <summary>生成粒子在指定形状内的位置。</summary>
        public Vector3 GetSpawnPosition(float randomSeed)
        {
            switch (Shape)
            {
                case EmitterShape.Point:
                    return PositionOffset;

                case EmitterShape.Cone:
                {
                    float r = randomSeed * SphereRadius * 0.1f;
                    float theta = randomSeed * MathF.PI * 2;
                    return PositionOffset + new Vector3(
                        r * MathF.Cos(theta),
                        0,
                        r * MathF.Sin(theta));
                }

                case EmitterShape.Sphere:
                {
                    float u = randomSeed;
                    float v = 1f - randomSeed * 2f;
                    float theta = MathF.Acos(v);
                    float phi = MathF.PI * 2 * u;
                    float r = MathF.Pow(randomSeed, 1f / 3f) * SphereRadius;
                    return PositionOffset + new Vector3(
                        r * MathF.Sin(theta) * MathF.Cos(phi),
                        r * MathF.Cos(theta),
                        r * MathF.Sin(theta) * MathF.Sin(phi));
                }

                case EmitterShape.Hemisphere:
                {
                    float u = randomSeed;
                    float theta = MathF.PI * 0.5f * randomSeed;
                    float phi = MathF.PI * 2 * u;
                    float r = SphereRadius * MathF.Sqrt(randomSeed);
                    return PositionOffset + new Vector3(
                        r * MathF.Sin(theta) * MathF.Cos(phi),
                        r * MathF.Cos(theta),
                        r * MathF.Sin(theta) * MathF.Sin(phi));
                }

                case EmitterShape.Box:
                    return PositionOffset + new Vector3(
                        (randomSeed * 2f - 1f) * BoxSize.X * 0.5f,
                        (randomSeed * 2f - 1f) * BoxSize.Y * 0.5f,
                        (randomSeed * 2f - 1f) * BoxSize.Z * 0.5f);

                case EmitterShape.Plane:
                    return PositionOffset + new Vector3(
                        (randomSeed * 2f - 1f) * PlaneWidth * 0.5f,
                        0,
                        (randomSeed * 2f - 1f) * PlaneHeight * 0.5f);

                case EmitterShape.Circle:
                {
                    float r = SphereRadius * MathF.Sqrt(randomSeed);
                    float theta = randomSeed * MathF.PI * 2;
                    return PositionOffset + new Vector3(
                        r * MathF.Cos(theta),
                        0,
                        r * MathF.Sin(theta));
                }

                case EmitterShape.Ring:
                {
                    float theta = randomSeed * MathF.PI * 2;
                    float r = SphereRadius + (randomSeed - 0.5f) * 0.1f * SphereRadius;
                    return PositionOffset + new Vector3(
                        r * MathF.Cos(theta),
                        0,
                        r * MathF.Sin(theta));
                }

                case EmitterShape.Line:
                {
                    float t = randomSeed * 2f - 1f;
                    return PositionOffset + new Vector3(
                        t * PlaneWidth * 0.5f, 0, 0);
                }

                case EmitterShape.MeshSurface:
                    return PositionOffset + new Vector3(
                        randomSeed * 2f - 1f,
                        randomSeed,
                        randomSeed * 2f - 1f) * SphereRadius;

                default:
                    return PositionOffset;
            }
        }

        public void Tick(float dt)
        {
            if (!IsEnabled) return;
            SystemTime += dt;
        }

        public void SetActiveCount(int count)
        {
            ActiveParticleCount = count;
        }

        public void Reset()
        {
            SystemTime = 0f;
            ActiveParticleCount = 0;
        }
    }

    #endregion

    #region 粒子 LOD

    /// <summary>粒子 LOD 级别。</summary>
    public struct ParticleLODLevel
    {
        public int Level;
        public float MaxDistance;
        public float RateMultiplier;
        public float SizeMultiplier;
        public bool DisableTrails;
        public bool DisableCollision;
    }

    /// <summary>粒子 LOD 系统。</summary>
    public class ParticleLODSystem
    {
        public List<ParticleLODLevel> LODLevels = new List<ParticleLODLevel>
        {
            new ParticleLODLevel { Level = 0, MaxDistance = 50f, RateMultiplier = 1f, SizeMultiplier = 1f },
            new ParticleLODLevel { Level = 1, MaxDistance = 100f, RateMultiplier = 0.5f, SizeMultiplier = 0.8f, DisableTrails = false },
            new ParticleLODLevel { Level = 2, MaxDistance = 200f, RateMultiplier = 0.25f, SizeMultiplier = 0.6f, DisableTrails = true, DisableCollision = true },
            new ParticleLODLevel { Level = 3, MaxDistance = 500f, RateMultiplier = 0.1f, SizeMultiplier = 0.4f, DisableTrails = true, DisableCollision = true }
        };

        public bool ForceLOD;
        public int ForcedLODLevel;

        public int CurrentLOD { get; private set; }

        public ParticleLODLevel EvaluateLOD(float distance)
        {
            if (ForceLOD)
            {
                int idx = Math.Clamp(ForcedLODLevel, 0, LODLevels.Count - 1);
                return LODLevels[idx];
            }

            for (int i = LODLevels.Count - 1; i >= 0; i--)
            {
                if (distance <= LODLevels[i].MaxDistance)
                {
                    CurrentLOD = i;
                    return LODLevels[i];
                }
            }

            CurrentLOD = LODLevels.Count - 1;
            return LODLevels[CurrentLOD];
        }
    }

    #endregion

    #region 粒子剔除

    /// <summary>视锥体 (简化)。</summary>
    public struct BoundingFrustum
    {
        public Matrix4x4 Matrix;

        public BoundingFrustum(Matrix4x4 viewProjection)
        {
            Matrix = viewProjection;
        }

        public bool Contains(Vector3 point)
        {
            // 简化的 NDC 空间剔除
            Vector4 clip = Vector4.Transform(new Vector4(point, 1f), Matrix);
            float w = clip.W;
            if (w <= 0f) return false;
            return clip.X >= -w && clip.X <= w &&
                   clip.Y >= -w && clip.Y <= w &&
                   clip.Z >= 0f && clip.Z <= w;
        }

        public ContainmentType IntersectsSphere(Vector3 center, float radius)
        {
            Vector4 clip = Vector4.Transform(new Vector4(center, 1f), Matrix);
            float w = clip.W;
            if (w <= 0f) return ContainmentType.Disjoint;
            float nx = clip.X / w, ny = clip.Y / w, nz = clip.Z / w;
            float r = radius / w;
            if (nx < -1 - r || nx > 1 + r || ny < -1 - r || ny > 1 + r || nz < -r || nz > 1 + r)
                return ContainmentType.Disjoint;
            if (nx >= -1 + r && nx <= 1 - r && ny >= -1 + r && ny <= 1 - r && nz >= r && nz <= 1 - r)
                return ContainmentType.Contains;
            return ContainmentType.Intersects;
        }
    }

    /// <summary>包含类型。</summary>
    public enum ContainmentType
    {
        Disjoint,
        Contains,
        Intersects
    }

    /// <summary>粒子剔除系统。</summary>
    public class ParticleCullingSystem
    {
        public float MaxDistance { get; set; } = 500f;
        public float MinScreenSize { get; set; } = 0.01f; // 像素
        public bool EnableFrustumCulling { get; set; } = true;
        public bool EnableDistanceCulling { get; set; } = true;
        public bool EnableScreenSizeCulling { get; set; } = false;
        public BoundingFrustum CurrentFrustum;
        public int CulledCount { get; private set; }
        public int TotalChecked { get; private set; }

        public bool IsVisible(ref Particle particle, Vector3 cameraPos, ref Matrix4x4 viewProj)
        {
            TotalChecked++;

            // 距离剔除
            if (EnableDistanceCulling)
            {
                float distSq = Vector3.DistanceSquared(particle.Position, cameraPos);
                if (distSq > MaxDistance * MaxDistance)
                {
                    CulledCount++;
                    return false;
                }
            }

            // 视锥剔除
            if (EnableFrustumCulling)
            {
                if (!CurrentFrustum.Contains(particle.Position))
                {
                    CulledCount++;
                    return false;
                }
            }

            return true;
        }

        public void ResetStats()
        {
            CulledCount = 0;
            TotalChecked = 0;
        }
    }

    #endregion

    #region 粒子池

    /// <summary>循环缓冲区粒子池。</summary>
    public class ParticlePool
    {
        private Particle[] _particles;
        private int _head;
        private int _aliveCount;
        private readonly int _capacity;
        private readonly bool[] _aliveFlags;

        public int Capacity => _capacity;
        public int AliveCount => _aliveCount;
        public int DeadCount => _capacity - _aliveCount;
        public float Utilization => (float)_aliveCount / _capacity;
        public Span<Particle> Particles => _particles.AsSpan();

        public ParticlePool(int capacity)
        {
            _capacity = capacity;
            _particles = new Particle[capacity];
            _aliveFlags = new bool[capacity];
            _head = 0;
            _aliveCount = 0;

            for (int i = 0; i < capacity; i++)
            {
                _particles[i] = Particle.Dead();
                _aliveFlags[i] = false;
            }
        }

        /// <summary>从池中获取一个死亡粒子槽位，返回索引和引用。</summary>
        public bool TrySpawn(out int index, out Particle particle)
        {
            // 优先查找死亡粒子
            for (int i = 0; i < _capacity; i++)
            {
                int idx = (_head + i) % _capacity;
                if (!_aliveFlags[idx])
                {
                    _aliveFlags[idx] = true;
                    _head = (idx + 1) % _capacity;
                    _aliveCount++;
                    index = idx;
                    particle = ref _particles[idx];
                    return true;
                }
            }

            // 池已满: 强制回收最老的粒子 (循环缓冲区策略)
            int recycled = _head;
            _head = (_head + 1) % _capacity;
            _aliveFlags[recycled] = true;
            index = recycled;
            particle = ref _particles[recycled];
            return true;
        }

        /// <summary>杀死一个粒子。</summary>
        public void Kill(int index)
        {
            if (index >= 0 && index < _capacity && _aliveFlags[index])
            {
                _aliveFlags[index] = false;
                _particles[index].IsAlive = false;
                _aliveCount--;
            }
        }

        /// <summary>批量杀死。</summary>
        public void KillDeadParticles()
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_aliveFlags[i] && !_particles[i].IsAlive)
                {
                    _aliveFlags[i] = false;
                    _aliveCount--;
                }
            }
        }

        /// <summary>获取活着的粒子索引列表。</summary>
        public List<int> GetAliveIndices(List<int> buffer)
        {
            buffer.Clear();
            for (int i = 0; i < _capacity; i++)
            {
                if (_aliveFlags[i])
                    buffer.Add(i);
            }
            return buffer;
        }

        /// <summary>重置池。</summary>
        public void Reset()
        {
            for (int i = 0; i < _capacity; i++)
            {
                _particles[i] = Particle.Dead();
                _aliveFlags[i] = false;
            }
            _head = 0;
            _aliveCount = 0;
        }
    }

    #endregion

    #region ParticleSystem

    /// <summary>
    /// 粒子系统。管理发射器列表、模块列表、粒子池和更新逻辑。
    /// 类似 UE FlowVFX System / Unity VFX Graph。
    /// </summary>
    public class ParticleSystem
    {
        // ── 发射器 & 模块 ──
        public string SystemName { get; set; } = "ParticleSystem";
        public bool IsPlaying { get; set; } = true;
        public bool IsPaused { get; set; }
        public ParticleComputeMode ComputeMode { get; set; } = ParticleComputeMode.CPU;
        public bool Looping { get; set; } = true;
        public float Duration { get; set; } = 10f;

        public readonly List<ParticleEmitter> Emitters = new List<ParticleEmitter>();
        public readonly List<ParticleModuleCore> Modules = new List<ParticleModuleCore>();
        public readonly List<ParticleModuleCore> SpawnModules = new List<ParticleModuleCore>();
        public readonly List<ParticleModuleCore> UpdateModules = new List<ParticleModuleCore>();
        public readonly List<ParticleModuleCore> RenderModules = new List<ParticleModuleCore>();

        // ── 粒子池 ──
        public int MaxTotalParticles { get; set; } = 50000;
        public ParticlePool Pool { get; private set; }

        // ── LOD & 剔除 ──
        public ParticleLODSystem LODSystem { get; } = new ParticleLODSystem();
        public ParticleCullingSystem CullingSystem { get; } = new ParticleCullingSystem();

        // ── 状态 ──
        public float SystemTime { get; private set; }
        public int TotalActiveParticles { get; private set; }
        public Vector3 WorldPosition { get; set; } = Vector3.Zero;
        public Quaternion WorldRotation { get; set; } = Quaternion.Identity;

        // ── 统计 ──
        public int TotalSpawnedThisFrame { get; private set; }
        public int TotalKilledThisFrame { get; private set; }
        public float LastFrameGPUTime { get; set; }
        public float LastFrameCPUTime { get; set; }

        // ── 内部缓冲 ──
        private List<int> _aliveIndices = new List<int>();
        private EventModule _eventModule;

        public ParticleSystem(int maxParticles = 50000)
        {
            MaxTotalParticles = maxParticles;
            Pool = new ParticlePool(maxParticles);
        }

        /// <summary>添加发射器。</summary>
        public void AddEmitter(ParticleEmitter emitter)
        {
            Emitters.Add(emitter);
        }

        /// <summary>添加模块 (自动分类到对应管线阶段)。</summary>
        public void AddModule(ParticleModuleCore module)
        {
            Modules.Add(module);
            switch (module.Stage)
            {
                case ParticleModuleCore.ModuleStage.Spawn:
                    SpawnModules.Add(module);
                    break;
                case ParticleModuleCore.ModuleStage.Update:
                    UpdateModules.Add(module);
                    break;
                case ParticleModuleCore.ModuleStage.Render:
                    RenderModules.Add(module);
                    break;
            }

            if (module is EventModule em)
                _eventModule = em;
        }

        /// <summary>移除模块。</summary>
        public bool RemoveModule(ParticleModuleCore module)
        {
            Modules.Remove(module);
            SpawnModules.Remove(module);
            UpdateModules.Remove(module);
            RenderModules.Remove(module);
            if (module == _eventModule) _eventModule = null;
            return true;
        }

        /// <summary>按名称查找模块。</summary>
        public T FindModule<T>(string name) where T : ParticleModuleCoreCore
        {
            foreach (var m in Modules)
            {
                if (m is T typed && typed.ModuleName == name)
                    return typed;
            }
            return null;
        }

        /// <summary>播放系统。</summary>
        public void Play()
        {
            IsPlaying = true;
            IsPaused = false;
        }

        /// <summary>暂停系统。</summary>
        public void Pause()
        {
            IsPaused = true;
        }

        /// <summary>停止并重置系统。</summary>
        public void Stop()
        {
            IsPlaying = false;
            IsPaused = false;
            Reset();
        }

        /// <summary>重置系统。</summary>
        public void Reset()
        {
            SystemTime = 0f;
            TotalActiveParticles = 0;
            TotalSpawnedThisFrame = 0;
            TotalKilledThisFrame = 0;
            Pool.Reset();
            _aliveIndices.Clear();

            foreach (var emitter in Emitters)
                emitter.Reset();

            foreach (var module in Modules)
                module.Reset();
        }

        /// <summary>
        /// 主更新函数。每帧调用。
        /// </summary>
        public void Update(float dt, Vector3 cameraPosition, ref Matrix4x4 viewProjection)
        {
            if (!IsPlaying || IsPaused) return;

            float frameStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // 循环检测
            if (Looping && SystemTime >= Duration)
            {
                SystemTime -= Duration;
                foreach (var module in Modules)
                    module.Reset();
            }

            if (!Looping && SystemTime >= Duration)
            {
                IsPlaying = false;
                return;
            }

            // 全局模块更新
            foreach (var module in Modules)
                module.Update(SystemTime, dt);

            // 发射新粒子
            TotalSpawnedThisFrame = 0;
            foreach (var emitter in Emitters)
            {
                if (!emitter.IsEnabled) continue;

                emitter.Tick(dt);

                // 评估突发发射
                int burstCount = 0;
                foreach (var module in SpawnModules)
                {
                    if (module is SpawnBurstModule burstModule)
                        burstCount += burstModule.GetBurstCount(emitter.SystemTime);
                }

                // 评估持续发射
                int rateCount = 0;
                foreach (var module in SpawnModules)
                {
                    if (module is SpawnRateModule rateModule)
                        rateCount += rateModule.GetSpawnCount(dt, 0f);
                }

                // LOD 调整
                float distToCamera = Vector3.Distance(WorldPosition + emitter.PositionOffset, cameraPosition);
                var lod = LODSystem.EvaluateLOD(distToCamera);
                int adjustedBurst = (int)(burstCount * lod.RateMultiplier);
                int adjustedRate = (int)(rateCount * lod.RateMultiplier);

                int totalSpawn = adjustedBurst + adjustedRate;

                for (int i = 0; i < totalSpawn; i++)
                {
                    SpawnParticle(emitter, cameraPosition, ref viewProjection, lod);
                }

                emitter.SetActiveCount(adjustedRate > 0 ? totalSpawn : adjustedBurst);
            }

            // 更新现有粒子
            TotalKilledThisFrame = 0;
            _aliveIndices.Clear();
            Pool.GetAliveIndices(_aliveIndices);

            for (int i = 0; i < _aliveIndices.Count; i++)
            {
                int idx = _aliveIndices[i];
                ref Particle p = ref GetParticleRef(idx);

                if (!p.IsAlive)
                {
                    KillParticle(idx);
                    continue;
                }

                // 运行 Update 模块
                foreach (var module in UpdateModules)
                {
                    if (!module.Enabled) continue;
                    module.UpdateParticle(ref p, dt, p.RandomSeed);
                }

                // 基础运动积分
                p.Position += p.Velocity * dt;
                p.Life -= dt;

                // 死亡检测
                if (p.Life <= 0f || !p.IsAlive)
                {
                    KillParticle(idx);
                }
            }

            // 清理死亡粒子
            Pool.KillDeadParticles();

            // 更新剔除统计
            CullingSystem.ResetStats();

            TotalActiveParticles = Pool.AliveCount;
            SystemTime += dt;

            float frameEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            LastFrameCPUTime = (frameEnd - frameStart) / (float)System.Diagnostics.Stopwatch.Frequency * 1000f;
        }

        /// <summary>生成单个粒子。</summary>
        private void SpawnParticle(ParticleEmitter emitter, Vector3 cameraPos, ref Matrix4x4 viewProj, ParticleLODLevel lod)
        {
            if (!Pool.TrySpawn(out int index, out Particle particle))
                return;

            float seed = Random.Shared.NextSingle();

            // 初始化基础状态
            particle.Initialize(1f, seed);
            particle.Position = emitter.GetSpawnPosition(seed);

            // 模拟空间转换
            if (emitter.SimulationSpace == ParticleSimulationSpace.World)
            {
                particle.Position = Vector3.Transform(particle.Position, 
                    Matrix4x4.CreateFromQuaternion(WorldRotation)) + WorldPosition;
            }

            // 运行 Spawn 模块
            foreach (var module in SpawnModules)
            {
                if (!module.Enabled) continue;
                module.InitializeParticle(ref particle, seed);
            }

            // LOD 大小缩放
            particle.Size *= lod.SizeMultiplier;

            // 写回
            SetParticle(index, particle);
            TotalSpawnedThisFrame++;

            // 通知事件
            _eventModule?.NotifySpawn(ref particle);
        }

        /// <summary>杀死粒子并触发事件。</summary>
        private void KillParticle(int index)
        {
            ref Particle p = ref GetParticleRef(index);
            _eventModule?.NotifyDeath(ref p);
            Pool.Kill(index);
            TotalKilledThisFrame++;
        }

        /// <summary>获取粒子引用。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Particle GetParticleRef(int index)
        {
            return ref Pool.Particles[index];
        }

        /// <summary>设置粒子数据。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetParticle(int index, in Particle particle)
        {
            Pool.Particles[index] = particle;
        }

        /// <summary>获取排序后的可见粒子列表 (用于渲染)。</summary>
        public void GetVisibleParticles(Vector3 cameraPos, ref Matrix4x4 viewProj, List<int> visibleIndices)
        {
            visibleIndices.Clear();
            CullingSystem.CurrentFrustum = new BoundingFrustum(viewProj);

            _aliveIndices.Clear();
            Pool.GetAliveIndices(_aliveIndices);

            for (int i = 0; i < _aliveIndices.Count; i++)
            {
                int idx = _aliveIndices[i];
                ref Particle p = ref GetParticleRef(idx);

                if (p.IsAlive && CullingSystem.IsVisible(ref p, cameraPos, ref viewProj))
                {
                    visibleIndices.Add(idx);
                }
            }

            // 排序
            SortParticles(visibleIndices, cameraPos);
        }

        private void SortParticles(List<int> indices, Vector3 cameraPos)
        {
            var firstEmitter = Emitters.Count > 0 ? Emitters[0] : null;
            if (firstEmitter == null) return;

            switch (firstEmitter.SortMode)
            {
                case VFXParticleSortMode.ByDistance:
                    indices.Sort((a, b) =>
                    {
                        float distA = Vector3.DistanceSquared(GetParticleRef(a).Position, cameraPos);
                        float distB = Vector3.DistanceSquared(GetParticleRef(b).Position, cameraPos);
                        return distB.CompareTo(distA); // 远→近 (用于 Alpha 混合)
                    });
                    break;

                case VFXParticleSortMode.ByAge:
                    indices.Sort((a, b) =>
                    {
                        float ageA = GetParticleRef(a).LifeRatio;
                        float ageB = GetParticleRef(b).LifeRatio;
                        return ageA.CompareTo(ageB);
                    });
                    break;
            }
        }

        /// <summary>切换到 GPU 计算模式。</summary>
        public void SwitchToGPU()
        {
            ComputeMode = ParticleComputeMode.GPU;
            // GPU 模式下, Update 函数会使用计算着色器
            // 这里只做标记, 实际 GPU 实现由渲染管线完成
        }

        /// <summary>切换到 CPU 计算模式。</summary>
        public void SwitchToCPU()
        {
            ComputeMode = ParticleComputeMode.CPU;
        }
    }

    #endregion

    #region 数学辅助工具类

    /// <summary>数学辅助函数。</summary>

    #endregion
}
