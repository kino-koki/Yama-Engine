using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 粒子模块系统 (类似 UE FlowVFX Module)
    // --------------------------------------------------------------------
    // 设计理念：
    //   - ParticleModule  : 模块基类（Spawn/Update 三阶段）
    //   - SpawnModule     : 生成时执行（生命周期/大小/颜色初值）
    //   - UpdateModule    : 每帧更新（力/速度/颜色/大小/旋转/碰撞等）
    //   - ModulePipeline  : 按顺序执行所有模块
    //   - ModuleData      : 模块间数据传递接口
    // ════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────
    // 模块执行阶段
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 模块执行阶段，决定模块在粒子生命周期的何时执行。
    /// </summary>
    public enum ModuleExecutionStage
    {
        /// <summary>粒子生成时执行（仅一次）。</summary>
        Spawn,
        /// <summary>每帧更新执行。</summary>
        Update,
        /// <summary>两者都执行。</summary>
        Both
    }

    /// <summary>
    /// 碰撞响应类型。
    /// </summary>
    public enum CollisionResponse
    {
        /// <summary>粒子在碰撞时死亡。</summary>
        Kill,
        /// <summary>粒子在碰撞时反弹。</summary>
        Bounce,
        /// <summary>粒子在碰撞时停止。</summary>
        Halt,
        /// <summary>粒子穿过碰撞体，不产生效果。</summary>
        None
    }

    /// <summary>
    /// 噪声域类型。
    /// </summary>
    public enum NoiseDomainType
    {
        /// <summary>1D 噪声。</summary>
        OneD,
        /// <summary>2D 噪声。</summary>
        TwoD,
        /// <summary>3D 噪声。</summary>
        ThreeD
    }

    /// <summary>
    /// 精灵面向模式。
    /// </summary>
    public enum SpriteFacingMode
    {
        /// <summary>面向摄像机。</summary>
        CameraFacing,
        /// <summary>沿速度方向面向。</summary>
        VelocityFacing,
        /// <summary>沿发射器轴向面向。</summary>
        AxisAligned,
        /// <summary>固定世界方向。</summary>
        FixedWorld
    }

    /// <summary>
    /// 销毁条件类型。
    /// </summary>
    public enum KillConditionType
    {
        /// <summary>按年龄销毁。</summary>
        Age,
        /// <summary>按大小销毁。</summary>
        Size,
        /// <summary>按速度销毁。</summary>
        Velocity,
        /// <summary>按碰撞次数销毁。</summary>
        CollisionCount,
        /// <summary>按自定义条件销毁。</summary>
        Custom
    }

    /// <summary>
    /// 吸引子类型。
    /// </summary>
    public enum AttractorType
    {
        /// <summary>点吸引子。</summary>
        Point,
        /// <summary>线段吸引子。</summary>
        Line,
        /// <summary>曲线吸引子。</summary>
        Curve,
        /// <summary>平面吸引子。</summary>
        Plane
    }

    // ──────────────────────────────────────────────────────────────────
    // 模块数据接口：模块间数据传递
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 模块间数据传递接口。粒子系统每帧创建一个实例，在各模块间共享。
    /// </summary>
    public sealed class ModuleData
    {
        /// <summary>全局时间（秒）。</summary>
        public float GlobalTime;
        /// <summary>帧间隔时间。</summary>
        public float DeltaTime;
        /// <summary>全局风力。</summary>
        public Vector3 Wind;
        /// <summary>全局重力。</summary>
        public Vector3 Gravity;
        /// <summary>全局大小倍率。</summary>
        public float SizeMultiplier;
        /// <summary>全局颜色倍率。</summary>
        public Vector4 ColorMultiplier;
        /// <summary>摄像机位置（用于排序和面向）。</summary>
        public Vector3 CameraPosition;
        /// <summary>发射器世界位置。</summary>
        public Vector3 EmitterPosition;
        /// <summary>发射器世界旋转。</summary>
        public Quaternion EmitterRotation;
        /// <summary>模拟空间。</summary>
        public ParticleSimulationSpace SimulationSpace;
        /// <summary>自定义数据字典。</summary>
        public Dictionary<string, float> FloatData = new();
        public Dictionary<string, Vector3> VectorData = new();
        public Dictionary<string, object?> ObjectData = new();

        public void SetFloat(string key, float value) => FloatData[key] = value;
        public float GetFloat(string key) => FloatData.TryGetValue(key, out var v) ? v : 0f;
        public void SetVector(string key, Vector3 value) => VectorData[key] = value;
        public Vector3 GetVector(string key) => VectorData.TryGetValue(key, out var v) ? v : Vector3.Zero;
    }

    // ──────────────────────────────────────────────────────────────────
    // 模块基类
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子模块基类。所有粒子行为模块继承此基类。
    /// </summary>
    public abstract class ParticleModule
    {
        /// <summary>模块名称。</summary>
        public string Name = "Module";
        /// <summary>是否启用。</summary>
        public bool Enabled = true;
        /// <summary>执行阶段。</summary>
        public abstract ModuleExecutionStage Stage { get; }

        /// <summary>
        /// 在粒子生成时调用（仅当 Stage 包含 Spawn 时）。
        /// </summary>
        public virtual void Initialize(ref Particle particle, Random rng, ModuleData data) { }

        /// <summary>
        /// 每帧更新调用（仅当 Stage 包含 Update 时）。
        /// </summary>
        public virtual void Update(ref Particle particle, float deltaTime, ModuleData data) { }

        /// <summary>
        /// 模块系统初始化（仅在粒子系统启动时调用一次）。
        /// </summary>
        public virtual void OnSystemInitialize(ModuleData data) { }
    }

    // ──────────────────────────────────────────────────────────────────
    // Spawn 模块：粒子生成时执行
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 生成模块：控制粒子的初始数量/速率/生命周期/大小等参数。
    /// </summary>
    public sealed class SpawnModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Spawn;

        /// <summary>生命周期最小值。</summary>
        public float LifetimeMin = 1f;
        /// <summary>生命周期最大值。</summary>
        public float LifetimeMax = 2f;
        /// <summary>初始大小最小值。</summary>
        public float SizeMin = 0.1f;
        /// <summary>初始大小最大值。</summary>
        public float SizeMax = 0.2f;
        /// <summary>初始速度最小值。</summary>
        public float SpeedMin = 1f;
        /// <summary>初始速度最大值。</summary>
        public float SpeedMax = 5f;
        /// <summary>初始质量（影响物理计算）。</summary>
        public float Mass = 1f;

        public override void Initialize(ref Particle particle, Random rng, ModuleData data)
        {
            particle.Lifetime = LifetimeMin + (float)rng.NextDouble() * (LifetimeMax - LifetimeMin);
            particle.Lifetime = Math.Max(0.01f, particle.Lifetime);
            float size = SizeMin + (float)rng.NextDouble() * (SizeMax - SizeMin);
            particle.Size = new Vector2(Math.Max(0.001f, size), Math.Max(0.001f, size));
            float speed = SpeedMin + (float)rng.NextDouble() * (SpeedMax - SpeedMin);
            float len = particle.Velocity.Length();
            if (len > 0.001f)
                particle.Velocity = Vector3.Normalize(particle.Velocity) * speed;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Update 模块：每帧更新执行
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 速度/力模块：施加力、加速度、重力、阻力到粒子。
    /// </summary>

    /// <summary>
    /// 颜色模块：控制粒子颜色随生命周期的变化。
    /// </summary>
    public sealed class ColorModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Both;

        /// <summary>起始颜色。</summary>
        public Vector4 ColorStart = new Vector4(1, 1, 1, 1);
        /// <summary>结束颜色。</summary>
        public Vector4 ColorEnd = new Vector4(1, 1, 1, 0);
        /// <summary>颜色随生命周期的变化模式：true=使用曲线，false=线性插值。</summary>
        public bool UseCurve = false;
        /// <summary>颜色梯度停止点（时间->颜色）。</summary>
        public List<(float Time, Vector4 Color)> GradientStops = new();

        public override void Initialize(ref Particle particle, Random rng, ModuleData data)
        {
            if (GradientStops.Count > 0)
                particle.Color = GradientStops[0].Color;
            else
                particle.Color = ColorStart;
        }

        public override void Update(ref Particle particle, float deltaTime, ModuleData data)
        {
            if (!Enabled) return;
            float t = particle.NormalizedAge;

            if (UseCurve && GradientStops.Count > 0)
            {
                particle.Color = SampleGradient(t);
            }
            else
            {
                particle.Color = Vector4.Lerp(ColorStart, ColorEnd, t);
            }

            // 应用全局颜色倍率
            particle.Color *= data.ColorMultiplier;
        }

        private Vector4 SampleGradient(float t)
        {
            if (GradientStops.Count == 0) return Vector4.One;
            if (GradientStops.Count == 1) return GradientStops[0].Color;

            for (int i = 0; i < GradientStops.Count - 1; i++)
            {
                if (t >= GradientStops[i].Time && t <= GradientStops[i + 1].Time)
                {
                    float range = GradientStops[i + 1].Time - GradientStops[i].Time;
                    float localT = range > 0f ? (t - GradientStops[i].Time) / range : 0f;
                    return Vector4.Lerp(GradientStops[i].Color, GradientStops[i + 1].Color, localT);
                }
            }
            return GradientStops[GradientStops.Count - 1].Color;
        }
    }

    /// <summary>
    /// 大小模块：控制粒子大小随生命周期的变化。
    /// </summary>
    public sealed class SizeModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Both;

        /// <summary>大小倍率随生命周期的曲线停止点。</summary>
        public List<(float Time, float Scale)> SizeCurve = new();
        /// <summary>是否启用大小曲线。</summary>
        public bool UseCurve = false;
        /// <summary>起始大小倍率。</summary>
        public float StartScale = 1f;
        /// <summary>结束大小倍率。</summary>
        public float EndScale = 0f;

        public override void Initialize(ref Particle particle, Random rng, ModuleData data)
        {
            if (UseCurve && SizeCurve.Count > 0)
            {
                particle.Size *= SizeCurve[0].Scale * data.SizeMultiplier;
            }
            else
            {
                particle.Size *= StartScale * data.SizeMultiplier;
            }
        }

        public override void Update(ref Particle particle, float deltaTime, ModuleData data)
        {
            if (!Enabled) return;
            float t = particle.NormalizedAge;
            float scale;

            if (UseCurve && SizeCurve.Count > 0)
            {
                scale = SampleCurve(t) * data.SizeMultiplier;
            }
            else
            {
                scale = MathF.Lerp(StartScale, EndScale, t) * data.SizeMultiplier;
            }

            particle.Size = new Vector2(
                Math.Max(0.001f, particle.Size.X * scale),
                Math.Max(0.001f, particle.Size.Y * scale));
        }

        private float SampleCurve(float t)
        {
            if (SizeCurve.Count == 0) return 1f;
            if (SizeCurve.Count == 1) return SizeCurve[0].Scale;

            for (int i = 0; i < SizeCurve.Count - 1; i++)
            {
                if (t >= SizeCurve[i].Time && t <= SizeCurve[i + 1].Time)
                {
                    float range = SizeCurve[i + 1].Time - SizeCurve[i].Time;
                    float localT = range > 0f ? (t - SizeCurve[i].Time) / range : 0f;
                    return MathF.Lerp(SizeCurve[i].Scale, SizeCurve[i + 1].Scale, localT);
                }
            }
            return SizeCurve[SizeCurve.Count - 1].Scale;
        }
    }

    /// <summary>
    /// 旋转模块：控制粒子旋转。
    /// </summary>

    /// <summary>
    /// 光照模块：粒子自发光和光照衰减。
    /// </summary>

    /// <summary>
    /// 拖尾模块：为粒子生成拖尾效果。
    /// </summary>

    /// <summary>
    /// 噪声模块：对粒子施加噪声扰动。
    /// </summary>
    public sealed class NoiseModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Update;

        /// <summary>噪声强度。</summary>
        public float Strength = 1f;
        /// <summary>噪声频率。</summary>
        public float Frequency = 1f;
        /// <summary>噪声域类型。</summary>
        public NoiseDomainType DomainType = NoiseDomainType.ThreeD;
        /// <summary>噪声是否随时间滚动。</summary>
        public bool Animated = true;
        /// <summary>滚动速度。</summary>
        public float ScrollSpeed = 1f;
        /// <summary>是否影响位置。</summary>
        public bool AffectPosition = true;
        /// <summary>是否影响速度。</summary>
        public bool AffectVelocity = true;
        /// <summary>粗糙度（迭代次数越多越精细）。</summary>
        public int Octaves = 1;
        /// <summary>持久度。</summary>
        public float Persistence = 0.5f;
        /// <summary>间隙度。</summary>
        public float Lacunarity = 2f;

        public override void Update(ref Particle particle, float deltaTime, ModuleData data)
        {
            if (!Enabled) return;

            float t = data.GlobalTime * ScrollSpeed;
            float nx = 0f, ny = 0f, nz = 0f;

            // 简化的噪声模拟（实际应使用 Perlin/Simplex 噪声）
            nx = SimpleNoise(particle.Position.X * Frequency + t);
            ny = SimpleNoise(particle.Position.Y * Frequency + t + 100f);
            nz = SimpleNoise(particle.Position.Z * Frequency + t + 200f);

            Vector3 noiseVec = new Vector3(nx, ny, nz) * Strength;

            if (AffectPosition)
            {
                particle.Position += noiseVec * deltaTime;
            }
            if (AffectVelocity)
            {
                particle.Velocity += noiseVec * deltaTime;
            }
        }

        private static float SimpleNoise(float x)
        {
            float i = MathF.Floor(x);
            float f = x - i;
            float t = f * f * (3f - 2f * f); // smoothstep
            float a = Hash((int)i);
            float b = Hash((int)i + 1);
            return MathF.Lerp(a, b, t) * 2f - 1f;
        }

        private static float Hash(int n)
        {
            n = (n << 13) ^ n;
            return (float)((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 2147483647f;
        }
    }

    /// <summary>
    /// 碰撞模块：粒子与场景碰撞检测与响应。
    /// </summary>

    /// <summary>
    /// SubUV 模块：子UV帧动画（精灵图集动画）。
    /// </summary>

    /// <summary>
    /// 精灵模块：精灵渲染参数设置。
    /// </summary>
    public sealed class SpriteModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Both;

        /// <summary>面向模式。</summary>
        public SpriteFacingMode FacingMode = SpriteFacingMode.CameraFacing;
        /// <summary>固定世界方向（FixedWorld 模式用）。</summary>
        public Vector3 FixedDirection = Vector3.UnitY;
        /// <summary>排序顺序偏移。</summary>
        public float SortOrder = 0f;
        /// <summary>是否双面渲染。</summary>
        public bool TwoSided = true;

        public override void Initialize(ref Particle particle, Random rng, ModuleData data)
        {
            // 初始化面向模式标记
            particle.CustomData2 = (int)FacingMode;
        }
    }

    /// <summary>
    /// Ribbon 模块：Ribbon（彩带）渲染参数。
    /// </summary>
    public sealed class RibbonModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Update;

        /// <summary>Ribbon 宽度。</summary>
        public float Width = 0.1f;
        /// <summary>宽度随生命周期缩放。</summary>
        public float WidthScale = 1f;
        /// <summary>Ribbon 颜色。</summary>
        public Vector4 RibbonColor = new Vector4(1, 1, 1, 1);
        /// <summary>是否使用粒子颜色。</summary>
        public bool UseParticleColor = true;
        /// <summary>Ribbon 纹理平铺。</summary>
        public float TextureTiling = 1f;
        /// <summary>最大点数（Ribbon 长度限制）。</summary>
        public int MaxPoints = 100;

        public override void Update(ref Particle particle, float deltaTime, ModuleData data)
        {
            if (!Enabled) return;
            particle.CustomData1 = 2f; // 标记需要 Ribbon 渲染
        }
    }

    /// <summary>
    /// 网格粒子模块：使用网格替代精灵渲染粒子。
    /// </summary>
    public sealed class MeshModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Spawn;

        /// <summary>网格资源句柄。</summary>
        public object? MeshAsset;
        /// <summary>是否随速度旋转。</summary>
        public bool RotateWithVelocity = false;
        /// <summary>网格缩放。</summary>
        public Vector3 MeshScale = Vector3.One;

        public override void Initialize(ref Particle particle, Random rng, ModuleData data)
        {
            particle.CustomData1 = 3f; // 标记网格渲染
        }
    }

    /// <summary>
    /// 轨道模块：粒子围绕中心点做轨道运动。
    /// </summary>
    public sealed class OrbitModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Both;

        /// <summary>轨道中心（局部空间）。</summary>
        public Vector3 Center = Vector3.Zero;
        /// <summary>轨道半径偏移。</summary>
        public float RadiusOffset = 0f;
        /// <summary>轨道速度（弧度/秒）。</summary>
        public float OrbitSpeed = 1f;
        /// <summary>轨道随机偏移。</summary>
        public float RandomOffset = 0.5f;
        /// <summary>轨道轴。</summary>
        public Vector3 Axis = Vector3.UnitY;
        /// <summary>是否多轴轨道。</summary>
        public bool MultiAxis = false;
        /// <summary>第二轴。</summary>
        public Vector3 SecondaryAxis = Vector3.UnitX;
        /// <summary>第二轴速度倍率。</summary>
        public float SecondarySpeed = 0.5f;

        public override void Initialize(ref Particle particle, Random rng, ModuleData data)
        {
            particle.OrbitRadius += RadiusOffset + (float)(rng.NextDouble() - 0.5) * 2 * RandomOffset;
            particle.OrbitRadius = Math.Max(0.001f, particle.OrbitRadius);
        }

        public override void Update(ref Particle particle, float deltaTime, ModuleData data)
        {
            if (!Enabled) return;
            particle.OrbitAngle += OrbitSpeed * deltaTime;

            float cos = MathF.Cos(particle.OrbitAngle);
            float sin = MathF.Sin(particle.OrbitAngle);

            Vector3 orbitPos = Center + Axis * particle.OrbitRadius * cos;
            if (MultiAxis)
            {
                orbitPos += SecondaryAxis * particle.OrbitRadius * sin * SecondarySpeed;
            }

            // 将轨道偏移叠加到粒子位置
            Vector3 offset = orbitPos - Center;
            particle.Position += offset * deltaTime;
        }
    }

    /// <summary>
    /// 吸引子模块：将粒子向吸引子位置/线/曲线吸引。
    /// </summary>
    public sealed class AttractorModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Update;

        /// <summary>吸引子类型。</summary>
        public AttractorType Type = AttractorType.Point;
        /// <summary>吸引子位置。</summary>
        public Vector3 Position = Vector3.Zero;
        /// <summary>吸引子终点（Line 类型）。</summary>
        public Vector3 LineEnd = new Vector3(0, 5, 0);
        /// <summary>力场强度。</summary>
        public float Strength = 10f;
        /// <summary>作用范围（0=无限）。</summary>
        public float Range = 10f;
        /// <summary>强度衰减曲线。</summary>
        public float Falloff = 2f;
        /// <summary>吸引子是否跟随发射器。</summary>
        public bool FollowEmitter = true;
        /// <summary>是否排斥（反向吸引）。</summary>
        public bool Repulse = false;

        public override void Update(ref Particle particle, float deltaTime, ModuleData data)
        {
            if (!Enabled) return;

            Vector3 attractorPos = FollowEmitter ? data.EmitterPosition + Position : Position;

            Vector3 direction;
            if (Type == AttractorType.Point)
            {
                direction = attractorPos - particle.Position;
            }
            else if (Type == AttractorType.Line)
            {
                direction = ClosestPointOnLine(attractorPos, LineEnd, particle.Position) - particle.Position;
            }
            else
            {
                direction = attractorPos - particle.Position;
            }

            float distance = direction.Length();
            if (distance < 0.001f) return;
            if (Range > 0f && distance > Range) return;

            Vector3 dirNorm = Vector3.Normalize(direction);
            float forceMag = Strength;

            // 距离衰减
            if (Range > 0f)
            {
                float normalizedDist = distance / Range;
                forceMag *= MathF.Pow(1f - normalizedDist, Falloff);
            }

            if (Repulse) forceMag = -forceMag;

            particle.Velocity += dirNorm * forceMag * deltaTime;
        }

        private static Vector3 ClosestPointOnLine(Vector3 a, Vector3 b, Vector3 point)
        {
            Vector3 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 0.0001f) return a;
            float t = Math.Clamp(Vector3.Dot(point - a, ab) / lenSq, 0f, 1f);
            return a + ab * t;
        }
    }

    /// <summary>
    /// 销毁条件模块：按条件销毁粒子。
    /// </summary>
    public sealed class KillModule : ParticleModule
    {
        public override ModuleExecutionStage Stage => ModuleExecutionStage.Update;

        /// <summary>销毁条件类型。</summary>
        public KillConditionType Condition = KillConditionType.Age;
        /// <summary>最小年龄（Age 模式：低于此值不销毁）。</summary>
        public float MinAge = 0f;
        /// <summary>最大年龄（Age 模式：超过此值销毁）。</summary>
        public float MaxAge = 10f;
        /// <summary>最小大小（Size 模式）。</summary>
        public float MinSize = 0f;
        /// <summary>最小速度（Velocity 模式）。</summary>
        public float MinSpeed = 0f;
        /// <summary>最大碰撞次数（CollisionCount 模式）。</summary>
        public int MaxCollisionCount = 5;
        /// <summary>自定义销毁条件回调（接收归一化年龄）。</summary>
        public Func<float, bool>? CustomCondition;

        public override void Update(ref Particle particle, float deltaTime, ModuleData data)
        {
            if (!Enabled) return;

            switch (Condition)
            {
                case KillConditionType.Age:
                    if (particle.Age > MaxAge || particle.Age < MinAge)
                        particle.IsAlive = false;
                    break;

                case KillConditionType.Size:
                    if (particle.Size.X < MinSize && particle.Size.Y < MinSize)
                        particle.IsAlive = false;
                    break;

                case KillConditionType.Velocity:
                    if (particle.Velocity.Length() < MinSpeed)
                        particle.IsAlive = false;
                    break;

                case KillConditionType.CollisionCount:
                    if (particle.CustomData0 >= MaxCollisionCount)
                        particle.IsAlive = false;
                    break;

                case KillConditionType.Custom:
                    if (CustomCondition != null && CustomCondition(particle.NormalizedAge))
                        particle.IsAlive = false;
                    break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 模块管线：按顺序执行所有模块
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 模块管线：管理模块列表并按顺序执行所有模块。
    /// 类似 UE FlowVFX 的模块执行管线。
    /// </summary>
    public sealed class ModulePipeline
    {
        /// <summary>所有模块列表。</summary>
        public List<ParticleModule> Modules { get; } = new();

        /// <summary>共享数据实例。</summary>
        private ModuleData _data = new();

        /// <summary>
        /// 添加模块到管线。
        /// </summary>
        public void AddModule(ParticleModule module)
        {
            Modules.Add(module);
            module.OnSystemInitialize(_data);
        }

        /// <summary>
        /// 移除模块。
        /// </summary>
        public bool RemoveModule(ParticleModule module)
        {
            return Modules.Remove(module);
        }

        /// <summary>
        /// 获取指定类型的模块（首个匹配）。
        /// </summary>
        public T? GetModule<T>() where T : ParticleModule
        {
            foreach (var m in Modules)
            {
                if (m is T t) return t;
            }
            return null;
        }

        /// <summary>
        /// 获取指定类型的所有模块。
        /// </summary>
        public List<T> GetModules<T>() where T : ParticleModule
        {
            var result = new List<T>();
            foreach (var m in Modules)
            {
                if (m is T t) result.Add(t);
            }
            return result;
        }

        /// <summary>
        /// 更新共享数据。
        /// </summary>
        public void SetModuleData(ModuleData data)
        {
            _data = data;
        }

        /// <summary>
        /// 系统初始化：对所有模块调用 OnSystemInitialize。
        /// </summary>
        public void Initialize(ModuleData data)
        {
            _data = data;
            foreach (var module in Modules)
            {
                module.OnSystemInitialize(data);
            }
        }

        /// <summary>
        /// 初始化单个粒子（执行所有 Spawn 阶段模块）。
        /// </summary>
        public void SpawnParticle(ref Particle particle, Random rng)
        {
            foreach (var module in Modules)
            {
                if (!module.Enabled) continue;
                if (module.Stage == ModuleExecutionStage.Spawn || module.Stage == ModuleExecutionStage.Both)
                {
                    module.Initialize(ref particle, rng, _data);
                }
            }
        }

        /// <summary>
        /// 更新单个粒子（执行所有 Update 阶段模块）。
        /// </summary>
        public void UpdateParticle(ref Particle particle, float deltaTime)
        {
            foreach (var module in Modules)
            {
                if (!module.Enabled) continue;
                if (module.Stage == ModuleExecutionStage.Update || module.Stage == ModuleExecutionStage.Both)
                {
                    module.Update(ref particle, deltaTime, _data);
                }
            }
        }

        /// <summary>
        /// 批量初始化粒子。
        /// </summary>
        public void SpawnParticles(Particle[] particles, int count, Random rng)
        {
            for (int i = 0; i < count; i++)
            {
                ref Particle p = ref particles[i];
                SpawnParticle(ref p, rng);
            }
        }

        /// <summary>
        /// 批量更新粒子。
        /// </summary>
        public void UpdateParticles(Particle[] particles, int count, float deltaTime)
        {
            for (int i = 0; i < count; i++)
            {
                ref Particle p = ref particles[i];
                if (!p.IsAlive) continue;
                UpdateParticle(ref p, deltaTime);
            }
        }
    }
}
