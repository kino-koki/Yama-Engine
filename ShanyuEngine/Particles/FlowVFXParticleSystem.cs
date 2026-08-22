using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 FlowVFX 级高级粒子系统 (Shanyu FlowVFX Particle System)
    // --------------------------------------------------------------------
    // 设计目标：对标 UE FlowVFX 的模块化粒子架构
    //   - FlowVFXEmitter：发射器（发射率/生命周期/初速度/大小/颜色/多种发射模式）
    //   - FlowVFXModule 模块系统（数据接口 Input/Output/Old 输入）：
    //     EmitModule / UpdateModule / RenderModule / EventModule / ForceModule
    //   - FlowVFXParameter：参数系统（Float/Vector2/Vector3/Color/Curve/Matrix/Texture）
    //   - FlowVFXSystem：完整粒子系统（多发射器 + 依赖关系）
    //   - FlowVFXInstance：运行时实例（CPU 模拟 / GPU Compute 模拟）
    //   - FlowVFXDataInterface：数据接口（Curve/Noise/Mesh/SkeletalMesh/Texture/SampleTarget）
    //   - CPU 模拟器：欧拉积分 / 子步 / 碰撞 / 力累加 / 生命周期管理
    //   - GPU 模拟接口：Compute Shader 调度描述
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：FlowVFX 参数系统
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX 参数类型。
    /// </summary>
    public enum FlowVFXParameterType
    {
        Float,
        Vector2,
        Vector3,
        Color,
        Curve,
        Matrix,
        Texture,
        Bool,
        Int
    }

    /// <summary>
    /// FlowVFX 参数。类型化参数，可序列化为 GPU 常量缓冲或结构化缓冲。
    /// </summary>
    public class FlowVFXParameter
    {
        public string Name = "";
        public FlowVFXParameterType Type = FlowVFXParameterType.Float;
        public float FloatValue;
        public Vector2 Vector2Value;
        public Vector3 Vector3Value;
        public Vector4 ColorValue = new(1, 1, 1, 1);
        public ParticleCurve CurveValue;
        public Matrix4x4 MatrixValue = Matrix4x4.Identity;
        public string TextureName = "";
        public bool BoolValue;
        public int IntValue;

        /// <summary>创建浮点参数。</summary>
        public static FlowVFXParameter CreateFloat(string name, float value) => new()
        { Name = name, Type = FlowVFXParameterType.Float, FloatValue = value };

        /// <summary>创建向量3参数。</summary>
        public static FlowVFXParameter CreateVec3(string name, Vector3 value) => new()
        { Name = name, Type = FlowVFXParameterType.Vector3, Vector3Value = value };

        /// <summary>创建颜色参数。</summary>
        public static FlowVFXParameter CreateColor(string name, Vector4 color) => new()
        { Name = name, Type = FlowVFXParameterType.Color, ColorValue = color };

        /// <summary>创建曲线参数。</summary>
        public static FlowVFXParameter CreateCurve(string name, ParticleCurve curve) => new()
        { Name = name, Type = FlowVFXParameterType.Curve, CurveValue = curve };

        /// <summary>求值参数（标量路径）。</summary>
        public float EvaluateFloat(float t, float random = 0f)
        {
            return Type switch
            {
                FlowVFXParameterType.Float => FloatValue,
                FlowVFXParameterType.Bool => BoolValue ? 1f : 0f,
                FlowVFXParameterType.Int => IntValue,
                FlowVFXParameterType.Curve => CurveValue.Evaluate(t, random),
                FlowVFXParameterType.Color => ColorValue.W,
                FlowVFXParameterType.Vector3 => Vector3Value.Length(),
                _ => FloatValue
            };
        }

        /// <summary>求值参数（向量3路径）。</summary>
        public Vector3 EvaluateVector3(float t, float random = 0f)
        {
            return Type switch
            {
                FlowVFXParameterType.Vector3 => Vector3Value,
                FlowVFXParameterType.Color => new Vector3(ColorValue.X, ColorValue.Y, ColorValue.Z),
                FlowVFXParameterType.Float => new Vector3(FloatValue),
                _ => Vector3.Zero
            };
        }

        /// <summary>求值参数（颜色路径）。</summary>
        public Vector4 EvaluateColor(float t, float random = 0f)
        {
            return Type switch
            {
                FlowVFXParameterType.Color => ColorValue,
                FlowVFXParameterType.Curve => new Vector4(CurveValue.Evaluate(t, random)),
                _ => ColorValue
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：发射模式
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX 发射模式。决定粒子如何从发射器中产生。
    /// </summary>
    public enum FlowVFXEmissionMode
    {
        /// <summary>连续发射：按发射率持续生成。</summary>
        Continuous,
        /// <summary>脉冲发射：一次性爆发指定数量。</summary>
        Burst,
        /// <summary>表面发射：从网格/形状表面采样位置。</summary>
        Surface,
        /// <summary>体积发射：从形状体积内随机采样。</summary>
        Volume,
        /// <summary>网格发射：从指定网格的表面三角形面积加权采样。</summary>
        Mesh,
        /// <summary>骨骼网格发射：从骨骼网格的骨骼附着点发射。</summary>
        SkeletalMesh,
        /// <summary>分布发射：按自定义分布函数采样。</summary>
        Distribution
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：FlowVFX 数据接口
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX 数据接口类型。定义可被模块访问的外部数据源。
    /// </summary>
    public enum FlowVFXDataInterfaceType
    {
        Curve,
        Noise,
        Mesh,
        SkeletalMesh,
        Texture,
        SampleTarget
    }

    /// <summary>
    /// FlowVFX 数据接口抽象基类。为模块提供外部数据访问能力。
    /// </summary>
    public abstract class FlowVFXDataInterface
    {
        public string Name = "DataInterface";
        public FlowVFXDataInterfaceType InterfaceType;
        public bool IsBound;

        /// <summary>绑定到具体资源。</summary>
        public abstract bool Bind(RHIDevice device);

        /// <summary>获取 SRV 句柄（供 Compute Shader 读取）。</summary>
        public abstract RHIDescriptorHandle GetSRV();

        /// <summary>获取 UAV 句柄（供 Compute Shader 写入）。</summary>
        public abstract RHIDescriptorHandle GetUAV();

        /// <summary>获取缓冲描述（供资源绑定布局）。</summary>
        public abstract RHIBuffer? GetBuffer();

        /// <summary>解绑释放资源。</summary>
        public virtual void Unbind() { IsBound = false; }
    }

    /// <summary>
    /// 曲线数据接口。将 ParticleCurve 烘焙为 GPU 缓冲供着色器采样。
    /// </summary>
    public class FlowVFXCurveDataInterface : FlowVFXDataInterface
    {
        public ParticleCurve Curve = new(1f);
        private RHIBuffer? _gpuBuffer;
        private RHIDevice? _device;
        private RHIDescriptorHandle _srv;

        public FlowVFXCurveDataInterface() => InterfaceType = FlowVFXDataInterfaceType.Curve;

        public void SetCurve(ParticleCurve curve) => Curve = curve;

        public override bool Bind(RHIDevice device)
        {
            _device = device;
            _gpuBuffer = Curve.UploadToGPU(device, $"FlowVFXCurve_{Name}");
            _srv = device.CreateSRV(_gpuBuffer, RHIFormat.R32_Float, true, 0, 64, 4);
            IsBound = true;
            return true;
        }

        public override RHIDescriptorHandle GetSRV() => _srv;
        public override RHIDescriptorHandle GetUAV() => default;
        public override RHIBuffer? GetBuffer() => _gpuBuffer;

        public override void Unbind()
        {
            _gpuBuffer?.Dispose();
            _gpuBuffer = null;
            IsBound = false;
        }
    }

    /// <summary>
    /// 噪声数据接口。3D/4D 噪声纹理供湍流/力场模块使用。
    /// </summary>
    public class FlowVFXNoiseDataInterface : FlowVFXDataInterface
    {
        public float Frequency = 1f;
        public int Octaves = 4;
        public float Strength = 1f;
        private RHITexture? _noiseTexture;
        private RHIDevice? _device;
        private RHIDescriptorHandle _srv;

        public FlowVFXNoiseDataInterface() => InterfaceType = FlowVFXDataInterfaceType.Noise;

        public override bool Bind(RHIDevice device)
        {
            _device = device;
            // 在实际引擎中此处生成/加载 3D 噪声纹理
            // 此处创建占位 2D 纹理（128x128 R16G16B16A16_Float）
            IsBound = true;
            return true;
        }

        public override RHIDescriptorHandle GetSRV() => _srv;
        public override RHIDescriptorHandle GetUAV() => default;
        public override RHIBuffer? GetBuffer() => null;

        public override void Unbind()
        {
            _noiseTexture?.Dispose();
            _noiseTexture = null;
            IsBound = false;
        }
    }

    /// <summary>
    /// 网格数据接口。提供网格的顶点/索引缓冲，供表面发射使用。
    /// </summary>
    public class FlowVFXMeshDataInterface : FlowVFXDataInterface
    {
        public RHIBuffer? VertexBuffer;
        public RHIBuffer? IndexBuffer;
        public int VertexCount;
        public int IndexCount;
        public float AreaScale = 1f;

        public FlowVFXMeshDataInterface() => InterfaceType = FlowVFXDataInterfaceType.Mesh;

        public override bool Bind(RHIDevice device)
        {
            IsBound = VertexBuffer != null && IndexBuffer != null;
            return IsBound;
        }

        public override RHIDescriptorHandle GetSRV() => default;
        public override RHIDescriptorHandle GetUAV() => default;
        public override RHIBuffer? GetBuffer() => VertexBuffer;
    }

    /// <summary>
    /// 纹理数据接口。2D/3D/Cube 纹理供渲染模块或采样模块使用。
    /// </summary>
    public class FlowVFXTextureDataInterface : FlowVFXDataInterface
    {
        public RHITexture? Texture;
        private RHIDescriptorHandle _srv;

        public FlowVFXTextureDataInterface() => InterfaceType = FlowVFXDataInterfaceType.Texture;

        public override bool Bind(RHIDevice device)
        {
            if (Texture != null)
            {
                _srv = device.CreateSRV(Texture);
                IsBound = true;
                return true;
            }
            IsBound = false;
            return false;
        }

        public override RHIDescriptorHandle GetSRV() => _srv;
        public override RHIDescriptorHandle GetUAV() => default;
        public override RHIBuffer? GetBuffer() => null;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：FlowVFX 模块系统
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 模块执行阶段。
    /// </summary>
    public enum FlowVFXModuleStage
    {
        Spawn,      // 粒子生成时执行
        Update,     // 每帧更新
        Render,     // 渲染阶段
        Event       // 事件触发时
    }

    /// <summary>
    /// FlowVFX 模块基类。每个模块封装粒子行为的某个方面，
    //  通过 Input/Output/Old 数据接口实现模块间数据流。
    /// </summary>
    public abstract class FlowVFXModule
    {
        public string Name = "Module";
        public bool Enabled = true;
        public int ExecutionOrder = 100;
        public abstract FlowVFXModuleStage Stage { get; }

        /// <summary>模块输入参数（从外部或上一级模块获取）。</summary>
        public readonly Dictionary<string, FlowVFXParameter> Inputs = new();
        /// <summary>模块输出参数（供下游模块使用）。</summary>
        public readonly Dictionary<string, FlowVFXParameter> Outputs = new();
        /// <summary>上一帧数值（Old 输入，用于差值计算）。</summary>
        public readonly Dictionary<string, float> OldValues = new();
        /// <summary>数据接口引用。</summary>
        public readonly List<FlowVFXDataInterface> DataInterfaces = new();

        /// <summary>添加输入参数。</summary>
        public FlowVFXParameter AddInput(string name, FlowVFXParameterType type)
        {
            var p = new FlowVFXParameter { Name = name, Type = type };
            Inputs[name] = p;
            return p;
        }

        /// <summary>添加输出参数。</summary>
        public FlowVFXParameter AddOutput(string name, FlowVFXParameterType type)
        {
            var p = new FlowVFXParameter { Name = name, Type = type };
            Outputs[name] = p;
            return p;
        }

        /// <summary>绑定数据接口。</summary>
        public void BindDataInterface(FlowVFXDataInterface di)
        {
            DataInterfaces.Add(di);
        }

        /// <summary>获取输入参数值。</summary>
        public float GetInputFloat(string name, float t = 0f, float rand = 0f)
        {
            if (Inputs.TryGetValue(name, out var p))
                return p.EvaluateFloat(t, rand);
            return 0f;
        }

        /// <summary>在 FlowVFXInstance 模拟中执行该模块。</summary>
        public abstract void Execute(FlowVFXSimulationContext ctx);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 发射模块（EmitModule）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发射控制模块。控制粒子生成数量、生命周期、初始速度、加速度。
    /// </summary>
    public class FlowVFXEmitModule : FlowVFXModule
    {
        public override FlowVFXModuleStage Stage => FlowVFXModuleStage.Spawn;

        /// <summary>每秒发射数量。</summary>
        public ParticleCurve SpawnRate = new(100f);
        /// <summary>脉冲数量。</summary>
        public float BurstCount;
        /// <summary>脉冲间隔。</summary>
        public float BurstInterval;
        /// <summary>生命周期曲线。</summary>
        public ParticleCurve Lifetime = new(3f, 6f);
        /// <summary>初始速度。</summary>
        public ParticleCurve InitialSpeed = new(2f, 5f);
        /// <summary>初始加速度。</summary>
        public Vector3 InitialAcceleration = Vector3.Zero;
        /// <summary>初始大小。</summary>
        public ParticleCurve InitialSize = new(0.1f, 0.2f);
        /// <summary>初始颜色。</summary>
        public Vector4 InitialColor = new(1, 1, 1, 1);

        public FlowVFXEmitModule()
        {
            Name = "EmitModule";
            ExecutionOrder = 0;
        }

        public override void Execute(FlowVFXSimulationContext ctx)
        {
            // 发射逻辑在 FlowVFXInstance.Tick 中处理，此模块提供参数
            // 输出参数供其他模块读取
            if (!Outputs.ContainsKey("Lifetime"))
                AddOutput("Lifetime", FlowVFXParameterType.Float);
            Outputs["Lifetime"].FloatValue = Lifetime.Evaluate(0f, ctx.Random);

            if (!Outputs.ContainsKey("InitialSpeed"))
                AddOutput("InitialSpeed", FlowVFXParameterType.Float);
            Outputs["InitialSpeed"].FloatValue = InitialSpeed.Evaluate(0f, ctx.Random);

            if (!Outputs.ContainsKey("InitialSize"))
                AddOutput("InitialSize", FlowVFXParameterType.Float);
            Outputs["InitialSize"].FloatValue = InitialSize.Evaluate(0f, ctx.Random);
        }

        /// <summary>计算本帧应发射数量。</summary>
        public int ComputeSpawnCount(float dt, float elapsedTime, ref float timeAccum, ref float burstTimer)
        {
            int count = 0;
            float rate = SpawnRate.Evaluate(elapsedTime);
            timeAccum += dt * rate;
            count += (int)timeAccum;
            timeAccum -= count;

            if (BurstInterval > 0f)
            {
                burstTimer += dt;
                if (burstTimer >= BurstInterval)
                {
                    burstTimer -= BurstInterval;
                    count += (int)BurstCount;
                }
            }
            return count;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 更新模块（UpdateModule）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 更新模块。每帧更新粒子属性（位置/速度/旋转/颜色/大小/不透明度）。
    /// </summary>
    public class FlowVFXUpdateModule : FlowVFXModule
    {
        public override FlowVFXModuleStage Stage => FlowVFXModuleStage.Update;

        /// <summary>速度覆盖曲线（每轴）。</summary>
        public ParticleCurve VelocityX = new(0f);
        public ParticleCurve VelocityY = new(0f);
        public ParticleCurve VelocityZ = new(0f);
        /// <summary>颜色随生命周期曲线。</summary>
        public ColorCurve ColorOverLife = new();
        /// <summary>大小随生命周期曲线。</summary>
        public ParticleCurve SizeXOverLife = new(1f);
        public ParticleCurve SizeYOverLife = new(1f);
        /// <summary>不透明度曲线。</summary>
        public ParticleCurve OpacityOverLife = new(1f);
        /// <summary>旋转曲线。</summary>
        public ParticleCurve RotationOverLife = new(0f);

        public FlowVFXUpdateModule()
        {
            Name = "UpdateModule";
            ExecutionOrder = 100;
        }

        public override void Execute(FlowVFXSimulationContext ctx)
        {
            if (!Enabled || ctx.ParticleCount == 0) return;
            float dt = ctx.DeltaTime;

            for (int i = 0; i < ctx.ParticleCount; i++)
            {
                if (!ctx.IsAlive(i)) continue;
                float t = ctx.GetNormalizedAge(i);

                // 颜色更新
                ctx.SetColor(i, ColorOverLife.Evaluate(t, ctx.GetSeed(i)));

                // 大小更新
                float sx = SizeXOverLife.Evaluate(t, ctx.GetSeed(i));
                float sy = SizeYOverLife.Evaluate(t, ctx.GetSeed(i));
                ctx.SetSize(i, new Vector2(sx, sy));

                // 不透明度
                float opacity = OpacityOverLife.Evaluate(t, ctx.GetSeed(i));
                var col = ctx.GetColor(i);
                col.W *= opacity;
                ctx.SetColor(i, col);

                // 旋转
                ctx.SetRotation(i, ctx.GetRotation(i) + RotationOverLife.Evaluate(t, ctx.GetSeed(i)) * dt);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 渲染模块（RenderModule）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染体类型。
    /// </summary>
    public enum FlowVFXRenderType
    {
        Sprite,
        Mesh,
        Beam,
        Ribbon,
        Light
    }

    /// <summary>
    /// 渲染模块。控制粒子如何渲染到屏幕。
    /// </summary>
    public class FlowVFXRenderModule : FlowVFXModule
    {
        public override FlowVFXModuleStage Stage => FlowVFXModuleStage.Render;

        public FlowVFXRenderType RenderType = FlowVFXRenderType.Sprite;
        public string MaterialName = "DefaultFlowVFXSprite";
        public Vector4 TintColor = new(1, 1, 1, 1);
        public bool CastsShadows;
        public bool ReceiveDecals;
        public float SoftParticleFadeDistance = 0.5f;
        public int SortPriority;

        /// <summary>Beam 参数（RenderType=Beam 时有效）。</summary>
        public float BeamWidth = 0.1f;
        public int BeamSegments = 8;
        public float BeamNoiseStrength;
        public float BeamNoiseFrequency = 5f;

        /// <summary>Ribbon 参数。</summary>
        public float RibbonWidth = 0.1f;
        public int RibbonVerticesPerSegment = 2;

        /// <summary>Light 参数（RenderType=Light 时有效）。</summary>
        public float LightRadius = 1f;
        public Vector3 LightColor = Vector3.One;
        public float LightIntensity = 1f;

        public FlowVFXRenderModule()
        {
            Name = "RenderModule";
            ExecutionOrder = 500;
        }

        public override void Execute(FlowVFXSimulationContext ctx)
        {
            // 渲染模块在 FlowVFXInstance.Render 中使用，此处理输出参数
            if (!Outputs.ContainsKey("RenderType"))
                AddOutput("RenderType", FlowVFXParameterType.Int);
            Outputs["RenderType"].IntValue = (int)RenderType;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 事件模块（EventModule）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 事件类型。
    /// </summary>
    public enum FlowVFXEventType
    {
        Collision,
        Kill,
        Spawn
    }

    /// <summary>
    /// 事件模块。在粒子碰撞/死亡/生成时触发动作。
    /// </summary>
    public class FlowVFXEventModule : FlowVFXModule
    {
        public override FlowVFXModuleStage Stage => FlowVFXModuleStage.Event;

        public FlowVFXEventType EventType = FlowVFXEventType.Collision;
        /// <summary>碰撞弹性系数。</summary>
        public float Bounce = 0.5f;
        /// <summary>碰撞摩擦力。</summary>
        public float Friction = 0.1f;
        /// <summary>速度低于此阈值时杀死粒子。</summary>
        public float KillSpeedThreshold = 0.05f;
        /// <summary>生成事件触发子发射器名称。</summary>
        public string SpawnSubEmitterName = "";
        /// <summary>每次事件生成子粒子数。</summary>
        public int SubSpawnCount = 5;

        public FlowVFXEventModule()
        {
            Name = "EventModule";
            ExecutionOrder = 300;
        }

        public override void Execute(FlowVFXSimulationContext ctx)
        {
            // 事件处理在 FlowVFXInstance 中通过事件队列驱动
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 力模块（ForceModule）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 力场类型。
    /// </summary>
    public enum FlowVFXForceType
    {
        Gravity,
        Wind,
        Curl,
        Noise,
        Drag,
        Attraction,
        Orbit
    }

    /// <summary>
    /// 单个力场描述。
    /// </summary>
    public struct FlowVFXForceField
    {
        public FlowVFXForceType Type;
        public Vector3 Direction;
        public float Strength;
        public float Frequency;
        public float Radius;
        public float Falloff;
        public Vector3 Center;
    }

    /// <summary>
    /// 力模块。累加各种力场到粒子加速度。
    /// </summary>
    public class FlowVFXForceModule : FlowVFXModule
    {
        public override FlowVFXModuleStage Stage => FlowVFXModuleStage.Update;

        public readonly List<FlowVFXForceField> Forces = new();

        public FlowVFXForceModule()
        {
            Name = "ForceModule";
            ExecutionOrder = 50;

            // 默认重力
            Forces.Add(new FlowVFXForceField
            {
                Type = FlowVFXForceType.Gravity,
                Direction = new Vector3(0, -9.81f, 0),
                Strength = 1f
            });
        }

        /// <summary>添加力场。</summary>
        public FlowVFXForceField AddForce(FlowVFXForceType type, float strength, Vector3? direction = null, Vector3? center = null)
        {
            var f = new FlowVFXForceField
            {
                Type = type,
                Strength = strength,
                Direction = direction ?? Vector3.Zero,
                Center = center ?? Vector3.Zero,
                Falloff = 2f,
                Frequency = 0.5f
            };
            Forces.Add(f);
            return f;
        }

        public override void Execute(FlowVFXSimulationContext ctx)
        {
            if (!Enabled || ctx.ParticleCount == 0) return;
            float dt = ctx.DeltaTime;
            float elapsed = ctx.ElapsedTime;

            for (int i = 0; i < ctx.ParticleCount; i++)
            {
                if (!ctx.IsAlive(i)) continue;
                Vector3 accel = Vector3.Zero;

                foreach (var force in Forces)
                {
                    Vector3 pos = ctx.GetPosition(i);
                    Vector3 vel = ctx.GetVelocity(i);
                    Vector3 f = ComputeForce(force, pos, vel, elapsed);
                    accel += f;
                }

                ctx.AddVelocity(i, accel * dt);
            }
        }

        /// <summary>计算单个力场的力向量。</summary>
        private static Vector3 ComputeForce(FlowVFXForceField ff, Vector3 pos, Vector3 vel, float time)
        {
            return ff.Type switch
            {
                FlowVFXForceType.Gravity => ff.Direction * ff.Strength,
                FlowVFXForceType.Wind => ff.Direction * ff.Strength * (1f + 0.3f * MathF.Sin(time * 2f)),
                FlowVFXForceType.Curl => ComputeCurl(pos * ff.Frequency, time, ff.Strength),
                FlowVFXForceType.Noise => ComputeNoise(pos * ff.Frequency, time, ff.Strength),
                FlowVFXForceType.Drag => -vel * ff.Strength,
                FlowVFXForceType.Attraction => ComputeAttraction(pos, ff.Center, ff.Strength, ff.Radius, ff.Falloff),
                FlowVFXForceType.Orbit => ComputeOrbit(pos, ff.Center, ff.Direction, ff.Strength),
                _ => Vector3.Zero
            };
        }

        /// <summary>Curl Noise 力（无散度矢量场近似）。</summary>
        private static Vector3 ComputeCurl(Vector3 p, float t, float strength)
        {
            float x = MathF.Sin(p.Y * 1.3f + t) - MathF.Cos(p.Z * 1.1f + t * 0.7f);
            float y = MathF.Sin(p.Z * 1.2f + t * 0.8f) - MathF.Cos(p.X * 1.4f + t * 1.1f);
            float z = MathF.Sin(p.X * 1.1f + t * 1.2f) - MathF.Cos(p.Y * 1.3f + t * 0.9f);
            return new Vector3(x, y, z) * strength;
        }

        /// <summary>3D 噪声力。</summary>
        private static Vector3 ComputeNoise(Vector3 p, float t, float strength)
        {
            float n = MathF.Sin(p.X * 3f + t) * MathF.Cos(p.Y * 2f + t * 0.5f) * MathF.Sin(p.Z * 2.5f + t * 0.3f);
            return new Vector3(n, n * 0.8f, n * 0.6f) * strength;
        }

        /// <summary>吸引力。</summary>
        private static Vector3 ComputeAttraction(Vector3 pos, Vector3 center, float strength, float radius, float falloff)
        {
            Vector3 dir = center - pos;
            float dist = dir.Length();
            if (dist < 1e-4f) return Vector3.Zero;
            float normDist = Math.Clamp(dist / radius, 0f, 1f);
            float falloffFactor = 1f - normDist * normDist; // 二次衰减
            return Vector3.Normalize(dir) * strength * falloffFactor / MathF.Max(1f, dist * 0.1f);
        }

        /// <summary>轨道力（绕轴旋转的切向力）。</summary>
        private static Vector3 ComputeOrbit(Vector3 pos, Vector3 center, Vector3 axis, float strength)
        {
            Vector3 toCenter = pos - center;
            Vector3 radial = Vector3.Normalize(toCenter);
            Vector3 tangent = Vector3.Cross(Vector3.Normalize(axis), radial);
            return tangent * strength;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分：模拟上下文
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX 模拟上下文。在模块执行期间传递粒子数据引用。
    /// </summary>
    public ref struct FlowVFXSimulationContext
    {
        public float DeltaTime;
        public float ElapsedTime;
        public float Random;
        public Vector3 EmitterPosition;
        public Vector3 CameraPosition;
        public int ParticleCount;

        // SOA 粒子数据引用（零拷贝）
        public Span<Vector3> Positions;
        public Span<Vector3> Velocities;
        public Span<Vector4> Colors;
        public Span<Vector2> Sizes;
        public Span<float> Ages;
        public Span<float> Lifetimes;
        public Span<float> Rotations;
        public Span<float> Masses;
        public Span<float> Seeds;
        public Span<uint> Flags;

        /// <summary>获取归一化年龄 [0,1]。</summary>
        public float GetNormalizedAge(int i)
        {
            float lt = Lifetimes[i];
            return lt > 1e-4f ? Ages[i] / lt : 1f;
        }

        /// <summary>粒子是否存活。</summary>
        public bool IsAlive(int i) => i < ParticleCount && (Flags[i] & 1u) != 0;

        /// <summary>获取随机种子。</summary>
        public float GetSeed(int i) => i < Seeds.Length ? Seeds[i] : 0f;

        /// <summary>数据访问器。</summary>
        public Vector3 GetPosition(int i) => Positions[i];
        public Vector3 GetVelocity(int i) => Velocities[i];
        public Vector4 GetColor(int i) => i < Colors.Length ? Colors[i] : Vector4.One;
        public Vector2 GetSize(int i) => i < Sizes.Length ? Sizes[i] : Vector2.One;
        public float GetRotation(int i) => i < Rotations.Length ? Rotations[i] : 0f;

        /// <summary>数据修改器。</summary>
        public void SetPosition(int i, Vector3 v) => Positions[i] = v;
        public void SetVelocity(int i, Vector3 v) => Velocities[i] = v;
        public void AddVelocity(int i, Vector3 dv) => Velocities[i] += dv;
        public void SetColor(int i, Vector4 c) { if (i < Colors.Length) Colors[i] = c; }
        public void SetSize(int i, Vector2 s) { if (i < Sizes.Length) Sizes[i] = s; }
        public void SetRotation(int i, float r) { if (i < Rotations.Length) Rotations[i] = r; }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分：FlowVFX 发射器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX 发射器。封装完整的粒子发射与模拟配置。
    /// </summary>
    public class FlowVFXEmitter
    {
        public string Name = "Emitter";
        public Guid Id = Guid.NewGuid();
        public bool Enabled = true;

        // ── 发射配置 ──
        public FlowVFXEmissionMode EmissionMode = FlowVFXEmissionMode.Continuous;
        public GPUEmissionShape Shape = GPUEmissionShape.Cone;
        public float ShapeRadius = 1f;
        public float ShapeAngle = 25f;
        public Vector3 BoxExtents = Vector3.One;

        // ── 模块列表 ──
        public readonly List<FlowVFXModule> Modules = new();
        public readonly Dictionary<string, FlowVFXParameter> Parameters = new();

        // ── 数据接口 ──
        public readonly Dictionary<string, FlowVFXDataInterface> DataInterfaces = new();

        // ── 容量 ──
        public int MaxParticles = 10000;
        public bool Loops = true;
        public float Duration = 5f;

        /// <summary>添加模块。</summary>
        public T AddModule<T>(T module) where T : FlowVFXModule
        {
            Modules.Add(module);
            Modules.Sort((a, b) => a.ExecutionOrder.CompareTo(b.ExecutionOrder));
            return module;
        }

        /// <summary>获取或创建指定类型的模块。</summary>
        public T GetOrCreateModule<T>() where T : FlowVFXModule, new()
        {
            foreach (var m in Modules)
                if (m is T t) return t;
            var nm = new T();
            AddModule(nm);
            return nm;
        }

        /// <summary>设置参数。</summary>
        public void SetParameter(string name, float value)
        {
            if (Parameters.TryGetValue(name, out var p))
                p.FloatValue = value;
            else
                Parameters[name] = FlowVFXParameter.CreateFloat(name, value);
        }

        /// <summary>获取参数。</summary>
        public float GetParameter(string name, float defaultValue = 0f)
        {
            return Parameters.TryGetValue(name, out var p) ? p.FloatValue : defaultValue;
        }

        /// <summary>绑定数据接口。</summary>
        public void SetDataInterface(string name, FlowVFXDataInterface di)
        {
            DataInterfaces[name] = di;
        }

        /// <summary>获取指定阶段的模块列表。</summary>
        public List<FlowVFXModule> GetModulesByStage(FlowVFXModuleStage stage)
        {
            var result = new List<FlowVFXModule>();
            foreach (var m in Modules)
                if (m.Stage == stage && m.Enabled) result.Add(m);
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第七部分：FlowVFX 系统（多发射器组合）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX 系统。由多个发射器组成，支持发射器间依赖关系。
    /// </summary>
    public class FlowVFXSystem
    {
        public string Name = "FlowVFXSystem";
        public Guid Id = Guid.NewGuid();
        public bool Enabled = true;

        /// <summary>发射器列表。</summary>
        public readonly List<FlowVFXEmitter> Emitters = new();

        /// <summary>发射器依赖关系：key 依赖 value（value 先于 key 模拟）。</summary>
        public readonly Dictionary<string, string> Dependencies = new();

        /// <summary>系统级参数（所有发射器共享）。</summary>
        public readonly Dictionary<string, FlowVFXParameter> SystemParameters = new();

        /// <summary>添加发射器。</summary>
        public FlowVFXEmitter AddEmitter(FlowVFXEmitter emitter)
        {
            Emitters.Add(emitter);
            return emitter;
        }

        /// <summary>添加发射器依赖。</summary>
        public void AddDependency(string emitterName, string dependsOn)
        {
            Dependencies[emitterName] = dependsOn;
        }

        /// <summary>拓扑排序发射器（处理依赖关系）。</summary>
        public List<FlowVFXEmitter> GetSortedEmitters()
        {
            var visited = new HashSet<string>();
            var sorted = new List<FlowVFXEmitter>();
            var inStack = new HashSet<string>();

            void Visit(FlowVFXEmitter emitter)
            {
                if (visited.Contains(emitter.Name)) return;
                if (inStack.Contains(emitter.Name)) return; // 循环依赖，跳过
                inStack.Add(emitter.Name);

                if (Dependencies.TryGetValue(emitter.Name, out var dep))
                {
                    var depEmitter = Emitters.Find(e => e.Name == dep);
                    if (depEmitter != null) Visit(depEmitter);
                }

                inStack.Remove(emitter.Name);
                visited.Add(emitter.Name);
                sorted.Add(emitter);
            }

            foreach (var e in Emitters)
                Visit(e);

            return sorted;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第八部分：FlowVFX 运行时实例
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 模拟模式。
    /// </summary>
    public enum FlowVFXSimMode
    {
        /// <summary>CPU 模拟（欧拉积分）。</summary>
        CPU,
        /// <summary>GPU Compute 模拟（Compute Shader）。</summary>
        GPUCompute
    }

    /// <summary>
    /// GPU Compute 模拟调度描述。描述如何将 FlowVFX 发射器的模拟调度到 GPU。
    /// </summary>
    public struct FlowVFXGPUSimDispatch
    {
        /// <summary>线程组大小。</summary>
        public int ThreadGroupSize;
        /// <summary>线程组 X 维度。</summary>
        public int GroupCountX;
        /// <summary>线程组 Y 维度。</summary>
        public int GroupCountY;
        /// <summary>线程组 Z 维度。</summary>
        public int GroupCountZ;
        /// <summary>计算管线状态。</summary>
        public RHIPipelineState? SimPSO;
        /// <summary>发射计算管线状态。</summary>
        public RHIPipelineState? EmitPSO;
        /// <summary>回收计算管线状态。</summary>
        public RHIPipelineState? ReclaimPSO;
        /// <summary>根签名。</summary>
        public RHIRootSignature? RootSignature;

        /// <summary>计算总线程数。</summary>
        public int TotalThreads => GroupCountX * GroupCountY * GroupCountZ * ThreadGroupSize;
    }

    /// <summary>
    /// FlowVFX 运行时实例。管理单个 FlowVFXSystem 的运行时状态，
    /// 支持 CPU 模拟和 GPU Compute 模拟两种模式。
    /// </summary>
    public class FlowVFXInstance : IDisposable
    {
        private readonly FlowVFXSystem _system;
        private readonly List<EmitterInstanceState> _emitterStates = new();

        /// <summary>系统名称。</summary>
        public string Name => _system.Name;
        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; private set; }
        /// <summary>是否循环。</summary>
        public bool Looping { get; set; } = true;
        /// <summary>模拟模式。</summary>
        public FlowVFXSimMode SimMode { get; set; } = FlowVFXSimMode.CPU;
        /// <summary>世界位置。</summary>
        public Vector3 Position;
        /// <summary>世界旋转。</summary>
        public Quaternion Rotation = Quaternion.Identity;
        /// <summary>模拟模式为 GPU 时的调度描述。</summary>
        public FlowVFXGPUSimDispatch GPUDispatch;

        public FlowVFXInstance(FlowVFXSystem system)
        {
            _system = system;
            foreach (var emitter in system.Emitters)
                _emitterStates.Add(new EmitterInstanceState(emitter));
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            IsPlaying = true;
            foreach (var state in _emitterStates)
                state.Reset();
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            IsPlaying = false;
            foreach (var state in _emitterStates)
                state.Clear();
        }

        /// <summary>暂停。</summary>
        public void Pause() => IsPlaying = false;

        /// <summary>恢复。</summary>
        public void Resume() => IsPlaying = true;

        /// <summary>CPU 模拟一帧。</summary>
        public void Tick(float dt, Vector3 cameraPos)
        {
            if (!IsPlaying || !_system.Enabled) return;

            var sortedEmitters = _system.GetSortedEmitters();
            for (int ei = 0; ei < sortedEmitters.Count; ei++)
            {
                var emitter = sortedEmitters[ei];
                if (!emitter.Enabled) continue;
                var state = _emitterStates.Find(s => s.Emitter == emitter);
                if (state == null) continue;

                TickEmitterCPU(state, dt, cameraPos);
            }
        }

        /// <summary>CPU 模拟单个发射器。</summary>
        private void TickEmitterCPU(EmitterInstanceState state, float dt, Vector3 cameraPos)
        {
            var emitter = state.Emitter;
            state.ElapsedTime += dt;

            // 检查持续时间
            if (state.ElapsedTime > emitter.Duration && !emitter.Loops && !Looping)
            {
                state.IsFinished = true;
                return;
            }
            if (state.ElapsedTime > emitter.Duration && emitter.Loops)
                state.ElapsedTime -= emitter.Duration;

            // 子步模拟（提高碰撞精度）
            int subSteps = 2;
            float subDt = dt / subSteps;

            for (int step = 0; step < subSteps; step++)
            {
                // 发射
                EmitParticlesCPU(state, subDt);

                // 更新模块执行
                ExecuteModules(state, FlowVFXModuleStage.Update, subDt);

                // 欧拉积分
                IntegrateCPU(state, subDt);

                // 碰撞检测
                ExecuteModules(state, FlowVFXModuleStage.Event, subDt);

                // 生命周期管理
                UpdateLifetimes(state, subDt);
            }

            // 事件模块清理
            state.EventQueue.Dispatch();
        }

        /// <summary>CPU 发射粒子。</summary>
        private void EmitParticlesCPU(EmitterInstanceState state, float dt)
        {
            var emitModule = state.Emitter.GetOrCreateModule<FlowVFXEmitModule>();
            int count = emitModule.ComputeSpawnCount(dt, state.ElapsedTime,
                ref state.TimeAccumulator, ref state.BurstTimer);

            for (int i = 0; i < count; i++)
            {
                int slot = state.Data.AllocateSlot();
                if (slot < 0) break;

                float rand = (float)Random.Shared.NextDouble();

                // 形状采样
                SampleEmitterShape(state.Emitter, out var pos, out var dir);

                // 初始化粒子数据
                state.Data.Positions[slot] = Position + pos;
                state.Data.Velocities[slot] = dir * emitModule.InitialSpeed.Evaluate(0f, rand);
                state.Data.Colors[slot] = emitModule.InitialColor;
                state.Data.Sizes[slot] = new Vector2(
                    emitModule.InitialSize.Evaluate(0f, rand),
                    emitModule.InitialSize.Evaluate(0f, rand));
                state.Data.Ages[slot] = 0f;
                state.Data.Lifetimes[slot] = emitModule.Lifetime.Evaluate(0f, rand);
                state.Data.Masses[slot] = 1f;
                state.Data.Seeds[slot] = rand;
                state.Data.Flags[slot] = 1u;

                // 生成事件
                state.EventQueue.Enqueue(ParticleEventType.Spawned, slot, state.Data.Positions[slot]);
            }
        }

        /// <summary>发射器形状采样。</summary>
        private static void SampleEmitterShape(FlowVFXEmitter emitter, out Vector3 pos, out Vector3 dir)
        {
            float r1 = (float)Random.Shared.NextDouble();
            float r2 = (float)Random.Shared.NextDouble();
            float r3 = (float)Random.Shared.NextDouble();

            switch (emitter.Shape)
            {
                case GPUEmissionShape.Sphere:
                    float theta = r1 * MathF.PI * 2f;
                    float phi = MathF.Acos(2f * r2 - 1f);
                    dir = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                    pos = dir * emitter.ShapeRadius * r3;
                    break;
                case GPUEmissionShape.Cone:
                    float angleRad = emitter.ShapeAngle * MathF.PI / 180f;
                    float coneR = MathF.Tan(angleRad);
                    dir = new Vector3(MathF.Cos(r1 * MathF.PI * 2f) * coneR, 1f, MathF.Sin(r1 * MathF.PI * 2f) * coneR);
                    dir = Vector3.Normalize(dir);
                    pos = new Vector3((r2 - 0.5f) * emitter.ShapeRadius * 2f * r3, 0, (r3 - 0.5f) * emitter.ShapeRadius * 2f * r2);
                    break;
                case GPUEmissionShape.Box:
                    pos = new Vector3(
                        (r1 - 0.5f) * emitter.BoxExtents.X,
                        (r2 - 0.5f) * emitter.BoxExtents.Y,
                        (r3 - 0.5f) * emitter.BoxExtents.Z);
                    dir = Vector3.UnitY;
                    break;
                case GPUEmissionShape.Hemisphere:
                    theta = r1 * MathF.PI * 2f;
                    phi = MathF.Acos(r2);
                    dir = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                    pos = dir * emitter.ShapeRadius * r3;
                    break;
                default:
                    pos = Vector3.Zero;
                    dir = Vector3.UnitY;
                    break;
            }
        }

        /// <summary>CPU 欧拉积分。</summary>
        private static void IntegrateCPU(EmitterInstanceState state, float dt)
        {
            for (int i = 0; i < state.Data.Capacity; i++)
            {
                if (!state.Data.IsAlive(i)) continue;

                // 速度 += 加速度 * dt（力模块已在 Update 阶段累加到速度）
                // 位置 += 速度 * dt
                state.Data.Positions[i] += state.Data.Velocities[i] * dt;
            }
        }

        /// <summary>执行指定阶段的模块。</summary>
        private void ExecuteModules(EmitterInstanceState state, FlowVFXModuleStage stage, float dt)
        {
            var modules = state.Emitter.GetModulesByStage(stage);
            foreach (var module in modules)
            {
                var ctx = new FlowVFXSimulationContext
                {
                    DeltaTime = dt,
                    ElapsedTime = state.ElapsedTime,
                    Random = (float)Random.Shared.NextDouble(),
                    EmitterPosition = Position,
                    CameraPosition = Vector3.Zero,
                    ParticleCount = state.Data.Capacity,
                    Positions = state.Data.Positions,
                    Velocities = state.Data.Velocities,
                    Colors = state.Data.Colors,
                    Sizes = state.Data.Sizes,
                    Ages = state.Data.Ages,
                    Lifetimes = state.Data.Lifetimes,
                    Rotations = state.Data.Rotations,
                    Masses = state.Data.Masses,
                    Seeds = state.Data.Seeds,
                    Flags = state.Data.Flags
                };
                module.Execute(ctx);
            }
        }

        /// <summary>更新生命周期，杀死过期粒子。</summary>
        private static void UpdateLifetimes(EmitterInstanceState state, float dt)
        {
            for (int i = 0; i < state.Data.Capacity; i++)
            {
                if (!state.Data.IsAlive(i)) continue;
                state.Data.Ages[i] += dt;
                if (state.Data.Ages[i] >= state.Data.Lifetimes[i])
                {
                    state.Data.Flags[i] &= ~1u;
                    state.EventQueue.Enqueue(ParticleEventType.Died, i, state.Data.Positions[i]);
                }
            }
            state.Data.RecountAlive();
        }

        /// <summary>获取发射器状态（供渲染使用）。</summary>
        public EmitterInstanceState? GetEmitterState(string emitterName)
        {
            return _emitterStates.Find(s => s.Emitter.Name == emitterName);
        }

        /// <summary>获取全部发射器状态。</summary>
        public IReadOnlyList<EmitterInstanceState> EmitterStates => _emitterStates;

        /// <summary>总存活粒子数。</summary>
        public int TotalAlive
        {
            get
            {
                int c = 0;
                foreach (var s in _emitterStates) c += s.Data.AliveCount;
                return c;
            }
        }

        public void Dispose()
        {
            foreach (var s in _emitterStates) s.Dispose();
            _emitterStates.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第九部分：发射器实例状态（SOA 粒子数据）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发射器实例状态。持有单个发射器的运行时粒子数据（SOA 布局）。
    /// </summary>
    public sealed class EmitterInstanceState : IDisposable
    {
        public readonly FlowVFXEmitter Emitter;
        public ParticleSOA Data;
        public float ElapsedTime;
        public float TimeAccumulator;
        public float BurstTimer;
        public bool IsFinished;
        public readonly ParticleEventQueue EventQueue = new();

        public EmitterInstanceState(FlowVFXEmitter emitter)
        {
            Emitter = emitter;
            Data = new ParticleSOA(emitter.MaxParticles);
        }

        /// <summary>分配一个空槽位，返回索引；无空位返回 -1。</summary>
        public int AllocateSlot()
        {
            for (int i = 0; i < Data.Capacity; i++)
                if (!Data.IsAlive(i)) return i;
            return -1;
        }

        /// <summary>重置状态。</summary>
        public void Reset()
        {
            Data.Clear();
            ElapsedTime = 0f;
            TimeAccumulator = 0f;
            BurstTimer = 0f;
            IsFinished = false;
        }

        /// <summary>清空。</summary>
        public void Clear() => Reset();

        public void Dispose()
        {
            Data.Dispose();
        }
    }
}
