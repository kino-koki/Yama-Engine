using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════
    // 山雨引擎粒子模块系统
    // --------------------------------------------------------------------
    // 设计理念：超越 UE7 FlowVFX 的数据导向(DOD)模块架构
    //   - 模块化：每个行为（发射/生命周期/力/颜色/尺寸/速度）封装为独立模块
    //   - SOA 布局：粒子数据按属性分列连续内存，缓存友好、SIMD 友好、GPU 友好
    //   - 参数曲线：支持常量/线性/贝塞尔/双值随机四种求值模式
    //   - 事件系统：碰撞/死亡/生成事件可挂载处理器与子发射器
    //   - 子发射器：碰撞/死亡/生成时派生新粒子，支持继承父粒子状态
    //   - 模块参数可直接序列化为 GPU 常量/结构化缓冲，与 RHI 无缝对接
    // ════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────
    // 第一部分：参数曲线系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 曲线求值模式。决定 MinMaxCurve 如何在给定归一化时间 t∈[0,1] 处取值。
    /// </summary>
    public enum ParticleCurveMode
    {
        /// <summary>常量：始终返回固定值。</summary>
        Constant,
        /// <summary>线性插值：在关键帧之间做线性插值。</summary>
        Linear,
        /// <summary>三次贝塞尔：在关键帧之间用三次贝塞尔曲线平滑插值。</summary>
        Bezier,
        /// <summary>双值随机：在 Min/Max 之间取随机值（每次粒子取值固定）。</summary>
        TwoConstants,
        /// <summary>双曲线随机：在两条曲线之间取随机插值。</summary>
        TwoCurves
    }

    /// <summary>
    /// 单个关键帧（时间-值-切线）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CurveKey
    {
        /// <summary>归一化时间 [0,1]。</summary>
        public float Time;
        /// <summary>该时刻的值。</summary>
        public float Value;
        /// <summary>入切线（贝塞尔模式用）。</summary>
        public float InTangent;
        /// <summary>出切线（贝塞尔模式用）。</summary>
        public float OutTangent;

        public CurveKey(float time, float value, float inT = 0f, float outT = 0f)
        {
            Time = Math.Clamp(time, 0f, 1f);
            Value = value;
            InTangent = inT;
            OutTangent = outT;
        }
    }

    /// <summary>
    /// 粒子参数曲线。支持常量/线性/贝塞尔/双值随机求值，覆盖 FlowVFX 全部曲线模式。
    /// 该结构可平铺为 GPU 端曲线纹理或结构化缓冲供着色器采样。
    /// </summary>
    public class ParticleCurve
    {
        public ParticleCurveMode Mode = ParticleCurveMode.Constant;
        public float ConstantValue = 1f;
        public float MinValue;
        public float MaxValue = 1f;
        public List<CurveKey> Keys = new();
        /// <summary>贝塞尔模式下的第二条曲线（TwoCurves 模式用）。</summary>
        public List<CurveKey> KeysMax = new();

        public ParticleCurve() { }
        public ParticleCurve(float constant) { Mode = ParticleCurveMode.Constant; ConstantValue = constant; }
        public ParticleCurve(float min, float max) { Mode = ParticleCurveMode.TwoConstants; MinValue = min; MaxValue = max; }

        /// <summary>
        /// 在归一化时间 t 处求值。randomFactor 用于双值/双曲线随机模式。
        /// </summary>
        public float Evaluate(float t, float randomFactor = 0f)
        {
            t = Math.Clamp(t, 0f, 1f);
            switch (Mode)
            {
                case ParticleCurveMode.Constant:
                    return ConstantValue;
                case ParticleCurveMode.TwoConstants:
                    return MinValue + (MaxValue - MinValue) * randomFactor;
                case ParticleCurveMode.Linear:
                    return EvaluateLinear(Keys, t);
                case ParticleCurveMode.Bezier:
                    return EvaluateBezier(Keys, t);
                case ParticleCurveMode.TwoCurves:
                    {
                        float lo = EvaluateLinear(Keys, t);
                        float hi = EvaluateLinear(KeysMax, t);
                        return lo + (hi - lo) * randomFactor;
                    }
                default:
                    return ConstantValue;
            }
        }

        /// <summary>线性插值求值。</summary>
        private static float EvaluateLinear(List<CurveKey> keys, float t)
        {
            if (keys.Count == 0) return 0f;
            if (keys.Count == 1 || t <= keys[0].Time) return keys[0].Value;
            if (t >= keys[^1].Time) return keys[^1].Value;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (t >= keys[i].Time && t <= keys[i + 1].Time)
                {
                    float range = keys[i + 1].Time - keys[i].Time;
                    if (range <= 1e-6f) return keys[i].Value;
                    float k = (t - keys[i].Time) / range;
                    return keys[i].Value + (keys[i + 1].Value - keys[i].Value) * k;
                }
            }
            return keys[^1].Value;
        }

        /// <summary>
        /// 三次贝塞尔曲线求值。使用 Hermite 形式（由切线导出控制点）。
        /// p(t) = (2t³-3t²+1)p0 + (t³-2t²+t)m0 + (-2t³+3t²)p1 + (t³-t²)m1
        /// 其中 m0/m1 为端点切线方向（由 OutTangent/InTangent 缩放得到）。
        /// </summary>
        private static float EvaluateBezier(List<CurveKey> keys, float t)
        {
            if (keys.Count == 0) return 0f;
            if (keys.Count == 1 || t <= keys[0].Time) return keys[0].Value;
            if (t >= keys[^1].Time) return keys[^1].Value;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (t >= keys[i].Time && t <= keys[i + 1].Time)
                {
                    float range = keys[i + 1].Time - keys[i].Time;
                    if (range <= 1e-6f) return keys[i].Value;
                    float u = (t - keys[i].Time) / range;
                    float p0 = keys[i].Value;
                    float p1 = keys[i + 1].Value;
                    // 切线缩放：将每秒切线转换为区间内控制量
                    float m0 = keys[i].OutTangent * range;
                    float m1 = keys[i + 1].InTangent * range;
                    float u2 = u * u;
                    float u3 = u2 * u;
                    float h00 = 2 * u3 - 3 * u2 + 1;
                    float h10 = u3 - 2 * u2 + u;
                    float h01 = -2 * u3 + 3 * u2;
                    float h11 = u3 - u2;
                    return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
                }
            }
            return keys[^1].Value;
        }

        /// <summary>
        /// 将曲线烘焙为固定采样数的浮点数组，用于上传 GPU 纹理/缓冲。
        /// </summary>
        public float[] Bake(int samples = 64)
        {
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / (samples - 1);
                data[i] = Evaluate(t, 0.5f);
            }
            return data;
        }

        /// <summary>上传烘焙数据到 GPU 结构化缓冲（供着色器采样）。</summary>
        public RHIBuffer UploadToGPU(RHIDevice device, string name)
        {
            var baked = Bake(64);
            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)baked.Length * sizeof(float),
                Format = RHIFormat.R32_Float,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)sizeof(float)
            };
            var buf = device.CreateBuffer(desc, name);
            device.UploadToBuffer(buf, baked);
            return buf;
        }
    }

    /// <summary>
    /// RGBA 四分量曲线（颜色曲线）。
    /// </summary>
    public class ColorCurve
    {
        public ParticleCurve R = new(1f);
        public ParticleCurve G = new(1f);
        public ParticleCurve B = new(1f);
        public ParticleCurve A = new(1f);

        public Vector4 Evaluate(float t, float rand = 0f) => new(
            R.Evaluate(t, rand), G.Evaluate(t, rand), B.Evaluate(t, rand), A.Evaluate(t, rand));

        /// <summary>烘焙为 RGBA 纹理上传 GPU。</summary>
        public RHIBuffer UploadToGPU(RHIDevice device, string name)
        {
            int samples = 64;
            var data = new Vector4[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / (samples - 1);
                data[i] = Evaluate(t, 0.5f);
            }
            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)samples * 16u,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 16
            };
            var buf = device.CreateBuffer(desc, name);
            device.UploadToBuffer(buf, data);
            return buf;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第二部分：SOA 粒子数据布局（数据导向设计）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子数据的 SOA（Structure of Arrays）布局。
    /// 每个属性独立连续存储，相比 AoS（Array of Structures）具有：
    ///   1) 更好的缓存局部性（模拟阶段只读写需要的属性）
    ///   2) SIMD 向量化更友好
    ///   3) 直接映射到 GPU 多个 StructuredBuffer，无需拆包
    /// 该结构既可在 CPU 端模拟使用，也可作为 GPU 缓冲的 CPU 侧镜像。
    /// </summary>
    public class ParticleSOA : IDisposable
    {
        public int Capacity;
        // 位置/速度（每分量独立或打包为 Vector3 数组）
        public Vector3[] Position;
        public Vector3[] Velocity;
        public Vector3[] Acceleration;
        public Vector4[] Color;
        public Vector4[] BaseColor;   // 起始色（曲线基准）
        public Vector2[] Size;
        public float[] Rotation;
        public float[] AngularVelocity;
        public float[] Age;
        public float[] Lifetime;
        public float[] Mass;
        public float[] RandomSeed;
        public uint[] Flags;          // 位标志：bit0=alive bit1=collided bit2=child
        public float[] CameraDistSq;

        // GPU 缓冲镜像（按需创建）
        private readonly Dictionary<string, RHIBuffer> _gpuBuffers = new();
        private RHIDevice? _device;

        public int AliveCount { get; private set; }

        public ParticleSOA(int capacity, RHIDevice? device = null)
        {
            Capacity = capacity;
            _device = device;
            Position = new Vector3[capacity];
            Velocity = new Vector3[capacity];
            Acceleration = new Vector3[capacity];
            Color = new Vector4[capacity];
            BaseColor = new Vector4[capacity];
            Size = new Vector2[capacity];
            Rotation = new float[capacity];
            AngularVelocity = new float[capacity];
            Age = new float[capacity];
            Lifetime = new float[capacity];
            Mass = new float[capacity];
            RandomSeed = new float[capacity];
            Flags = new uint[capacity];
            CameraDistSq = new float[capacity];
        }

        /// <summary>标记某槽位为存活。</summary>
        public void MarkAlive(int i) { Flags[i] |= 1u; }
        public void MarkDead(int i) { Flags[i] &= ~1u; }
        public bool IsAlive(int i) => (Flags[i] & 1u) != 0;

        /// <summary>重置全部数据。</summary>
        public void Clear()
        {
            Array.Clear(Flags, 0, Capacity);
            AliveCount = 0;
        }

        /// <summary>更新存活计数（扫描 Flags）。</summary>
        public int RecountAlive()
        {
            int c = 0;
            for (int i = 0; i < Capacity; i++)
                if ((Flags[i] & 1u) != 0) c++;
            AliveCount = c;
            return c;
        }

        /// <summary>将指定属性数组上传到 GPU 结构化缓冲（按名称缓存）。</summary>
        public RHIBuffer GetOrCreateGPUBuffer<T>(string name, RHIBufferFlags extraFlags = RHIBufferFlags.None) where T : struct
        {
            if (_gpuBuffers.TryGetValue(name, out var b)) return b;
            int stride = Marshal.SizeOf<T>();
            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)Capacity * (ulong)stride,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource | RHIBufferFlags.UnorderedAccess | extraFlags,
                Stride = (ulong)stride
            };
            var buf = _device!.CreateBuffer(desc, "ParticleSOA_" + name);
            _gpuBuffers[name] = buf;
            return buf;
        }

        /// <summary>同步全部 SOA 数据到 GPU 缓冲。</summary>
        public void SyncToGPU(RHICommandList cmd)
        {
            if (_device == null) return;
            _device.UploadToBuffer(GetOrCreateGPUBuffer<Vector3>("Position"), Position);
            _device.UploadToBuffer(GetOrCreateGPUBuffer<Vector3>("Velocity"), Velocity);
            _device.UploadToBuffer(GetOrCreateGPUBuffer<Vector4>("Color"), Color);
            _device.UploadToBuffer(GetOrCreateGPUBuffer<Vector2>("Size"), Size);
            _device.UploadToBuffer(GetOrCreateGPUBuffer<float>("Age", RHIBufferFlags.None), Age);
            _device.UploadToBuffer(GetOrCreateGPUBuffer<float>("Lifetime"), Lifetime);
            _device.UploadToBuffer(GetOrCreateGPUBuffer<uint>("Flags"), Flags);
        }

        public void Dispose()
        {
            foreach (var kv in _gpuBuffers) kv.Value.Dispose();
            _gpuBuffers.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第三部分：模块基类与模块上下文
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 模块执行上下文。在单次模拟步进中传递给所有模块，包含时间、相机、SOA 数据等。
    /// </summary>
    public struct ModuleContext
    {
        public float DeltaTime;
        public float ElapsedTime;
        public Vector3 CameraPosition;
        public Vector3 EmitterPosition;
        public Matrix4x4 ViewMatrix;
        public Matrix4x4 ProjMatrix;
        public ParticleSOA Data;
        public ParticleEventQueue Events;
    }

    /// <summary>
    /// 粒子模块抽象基类。每个模块负责粒子行为的某个方面。
    /// 模块按顺序执行（Emitter → Lifetime → Force → Velocity → Color → Size → Collision → SubEmitter）。
    /// </summary>
    public abstract class ParticleModuleBase
    {
        /// <summary>模块是否启用。</summary>
        public bool Enabled = true;
        /// <summary>模块执行优先级（数值小的先执行）。</summary>
        public int ExecutionOrder = 100;
        /// <summary>模块显示名。</summary>
        public abstract string ModuleName { get; }

        /// <summary>每帧执行模块逻辑。</summary>
        public abstract void Execute(ref ModuleContext ctx);

        /// <summary>模块初始化（系统启动时调用一次，可创建 GPU 资源）。</summary>
        public virtual void Initialize(RHIDevice device) { }

        /// <summary>批量遍历存活粒子并执行回调（SIMD 友好写法）。</summary>
        protected static void ForEachAlive(ParticleSOA data, Action<int> body)
        {
            for (int i = 0; i < data.Capacity; i++)
                if ((data.Flags[i] & 1u) != 0) body(i);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第四部分：具体模块实现
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发射模块。按速率/脉冲生成粒子，支持多种发射形状。
    /// </summary>
    public class EmitterModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Emitter";
        public ParticleCurve Rate = new(500f);          // 每秒发射数曲线
        public float BurstCount;                        // 脉冲数量
        public float BurstInterval;                     // 脉冲间隔
        public GPUEmissionShape Shape = GPUEmissionShape.Cone;
        public float Radius = 1f;
        public float Angle = 25f;
        public Vector3 BoxExtents = Vector3.One;
        public ParticleCurve Speed = new(2f, 5f);        // 初速度（双值随机）
        public Vector3 EmissionDirection = Vector3.UnitY;

        private float _timeAccum;
        private float _burstTimer;
        private readonly Random _rng = new();

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            float rate = Rate.Evaluate(ctx.ElapsedTime, 0f);
            _timeAccum += ctx.DeltaTime * rate;
            int toEmit = (int)_timeAccum;
            _timeAccum -= toEmit;

            // 脉冲发射
            if (BurstInterval > 0)
            {
                _burstTimer += ctx.DeltaTime;
                if (_burstTimer >= BurstInterval)
                {
                    _burstTimer = 0;
                    toEmit += (int)BurstCount;
                }
            }

            for (int e = 0; e < toEmit; e++)
                SpawnParticle(ref ctx);
        }

        private void SpawnParticle(ref ModuleContext ctx)
        {
            var data = ctx.Data;
            // 查找空槽位
            int slot = -1;
            for (int i = 0; i < data.Capacity; i++)
                if ((data.Flags[i] & 1u) == 0) { slot = i; break; }
            if (slot < 0) return;

            SampleShape(out var pos, out var dir);
            float r = (float)_rng.NextDouble();
            data.Position[slot] = ctx.EmitterPosition + pos;
            data.Velocity[slot] = dir * Speed.Evaluate(0f, r);
            data.Acceleration[slot] = Vector3.Zero;
            data.Age[slot] = 0f;
            data.Mass[slot] = 1f;
            data.RandomSeed[slot] = (float)_rng.NextDouble();
            data.Flags[slot] = 1u;
            // 生成事件
            ctx.Events.Enqueue(ParticleEventType.Spawned, slot, ctx.EmitterPosition);
        }

        /// <summary>按形状采样初始位置与方向。</summary>
        private void SampleShape(out Vector3 pos, out Vector3 dir)
        {
            float r1 = (float)_rng.NextDouble();
            float r2 = (float)_rng.NextDouble();
            switch (Shape)
            {
                case GPUEmissionShape.Sphere:
                    {
                        float theta = r1 * MathF.PI * 2f;
                        float phi = MathF.Acos(2f * r2 - 1f);
                        dir = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                        pos = dir * Radius * (float)_rng.NextDouble();
                        break;
                    }
                case GPUEmissionShape.Cone:
                    {
                        float a = Angle * MathF.PI / 180f;
                        float cr = MathF.Tan(a);
                        dir = Vector3.Normalize(new Vector3(MathF.Cos(r1 * MathF.PI * 2f) * cr, 1f, MathF.Sin(r1 * MathF.PI * 2f) * cr));
                        pos = new Vector3((r1 - 0.5f) * Radius, 0, (r2 - 0.5f) * Radius);
                        break;
                    }
                case GPUEmissionShape.Box:
                    pos = new Vector3((r1 - 0.5f) * BoxExtents.X * 2f, (r2 - 0.5f) * BoxExtents.Y * 2f, ((float)_rng.NextDouble() - 0.5f) * BoxExtents.Z * 2f);
                    dir = EmissionDirection;
                    break;
                default:
                    pos = Vector3.Zero;
                    dir = EmissionDirection;
                    break;
            }
            dir = Vector3.Normalize(dir);
        }
    }

    /// <summary>
    /// 生命周期模块。管理粒子年龄增长与死亡回收。
    /// </summary>
    public class LifetimeModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Lifetime";
        public ParticleCurve Lifetime = new(5f);
        public ParticleCurve LifetimeMax = new(8f);
        public bool KillOnLifetimeEnd = true;

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            var data = ctx.Data;
            for (int i = 0; i < data.Capacity; i++)
            {
                if ((data.Flags[i] & 1u) == 0) continue;
                // 首次激活时初始化生命周期
                if (data.Lifetime[i] <= 0f)
                    data.Lifetime[i] = Lifetime.Evaluate(0f, data.RandomSeed[i]);
                data.Age[i] += ctx.DeltaTime;
                if (KillOnLifetimeEnd && data.Age[i] >= data.Lifetime[i])
                {
                    data.Flags[i] &= ~1u; // 死亡
                    ctx.Events.Enqueue(ParticleEventType.Died, i, data.Position[i]);
                }
            }
        }
    }

    /// <summary>
    /// 力模块。对粒子施加加速度（重力/风/自定义力曲线）。
    /// </summary>
    public class ForceModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Force";
        public ParticleCurve ForceX = new(0f);
        public ParticleCurve ForceY = new(-9.81f);
        public ParticleCurve ForceZ = new(0f);
        public bool UseLocalSpace;
        public float CurlNoiseStrength;
        public float CurlNoiseFrequency = 0.5f;
        public float CurlNoiseSpeed = 0.2f;

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            var data = ctx.Data;
            float elapsedTime = ctx.ElapsedTime;
            float deltaTime = ctx.DeltaTime;
            ForEachAlive(data, i =>
            {
                float t = data.Age[i] / MathF.Max(data.Lifetime[i], 1e-4f);
                float fx = ForceX.Evaluate(t, data.RandomSeed[i]);
                float fy = ForceY.Evaluate(t, data.RandomSeed[i]);
                float fz = ForceZ.Evaluate(t, data.RandomSeed[i]);
                // 简化 Curl Noise：基于位置与时间的正弦扰动近似（完整实现见着色器）
                if (CurlNoiseStrength > 0f)
                {
                    float n = elapsedTime * CurlNoiseSpeed;
                    Vector3 p = data.Position[i] * CurlNoiseFrequency;
                    fx += (MathF.Sin(p.Y + n) - MathF.Cos(p.Z + n)) * CurlNoiseStrength;
                    fy += (MathF.Sin(p.Z + n) - MathF.Cos(p.X + n)) * CurlNoiseStrength;
                    fz += (MathF.Sin(p.X + n) - MathF.Cos(p.Y + n)) * CurlNoiseStrength;
                }
                data.Acceleration[i] = new Vector3(fx, fy, fz);
                data.Velocity[i] += data.Acceleration[i] * deltaTime;
            });
        }
    }

    /// <summary>
    /// 速度模块。直接设置/缩放速度（覆盖或叠加速度曲线）。
    /// </summary>
    public class VelocityModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Velocity";
        public ParticleCurve VelX = new(0f);
        public ParticleCurve VelY = new(0f);
        public ParticleCurve VelZ = new(0f);
        public float SpeedScale = 1f;
        public bool Additive; // true=叠加到现有速度，false=直接设置

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            var data = ctx.Data;
            float deltaTime = ctx.DeltaTime;
            ForEachAlive(data, i =>
            {
                float t = data.Age[i] / MathF.Max(data.Lifetime[i], 1e-4f);
                var v = new Vector3(VelX.Evaluate(t, data.RandomSeed[i]),
                                    VelY.Evaluate(t, data.RandomSeed[i]),
                                    VelZ.Evaluate(t, data.RandomSeed[i])) * SpeedScale;
                if (Additive) data.Velocity[i] += v * deltaTime;
                else data.Velocity[i] = v;
                data.Position[i] += data.Velocity[i] * deltaTime;
            });
        }
    }

    /// <summary>
    /// 颜色模块。按生命周期曲线插值颜色。
    /// </summary>
    public class ColorModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Color";
        public ColorCurve ColorOverLifetime = new();
        public Vector4 StartColor = new(1, 1, 1, 1);
        public Vector4 EndColor = new(1, 1, 1, 0);

        public ColorModuleV2()
        {
            ColorOverLifetime.R = new ParticleCurve(1f);
            ColorOverLifetime.G = new ParticleCurve(1f);
            ColorOverLifetime.B = new ParticleCurve(1f);
            ColorOverLifetime.A = new ParticleCurve(1f, 0f) { Mode = ParticleCurveMode.Linear };
            ColorOverLifetime.A.Keys.Add(new CurveKey(0f, 1f));
            ColorOverLifetime.A.Keys.Add(new CurveKey(1f, 0f));
        }

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            var data = ctx.Data;
            ForEachAlive(data, i =>
            {
                float t = data.Age[i] / MathF.Max(data.Lifetime[i], 1e-4f);
                data.Color[i] = ColorOverLifetime.Evaluate(t, data.RandomSeed[i]);
            });
        }
    }

    /// <summary>
    /// 尺寸模块。按生命周期曲线插值粒子尺寸。
    /// </summary>
    public class SizeModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Size";
        public ParticleCurve SizeX = new(0.2f);
        public ParticleCurve SizeY = new(0.2f);
        public bool AspectRatioLocked = true;

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            var data = ctx.Data;
            ForEachAlive(data, i =>
            {
                float t = data.Age[i] / MathF.Max(data.Lifetime[i], 1e-4f);
                float sx = SizeX.Evaluate(t, data.RandomSeed[i]);
                float sy = AspectRatioLocked ? sx : SizeY.Evaluate(t, data.RandomSeed[i]);
                data.Size[i] = new Vector2(sx, sy);
            });
        }
    }

    /// <summary>
    /// 旋转模块。管理粒子角速度与朝向。
    /// </summary>
    public class RotationModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Rotation";
        public ParticleCurve AngularVelocity = new(0f);

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            var data = ctx.Data;
            float deltaTime = ctx.DeltaTime;
            ForEachAlive(data, i =>
            {
                float t = data.Age[i] / MathF.Max(data.Lifetime[i], 1e-4f);
                data.AngularVelocity[i] = AngularVelocity.Evaluate(t, data.RandomSeed[i]);
                data.Rotation[i] += data.AngularVelocity[i] * deltaTime;
            });
        }
    }

    /// <summary>
    /// 碰撞模块。CPU 端平面/球碰撞检测（GPU 端碰撞见 GPUParticleSystem）。
    /// 碰撞时生成碰撞事件，可供子发射器消费。
    /// </summary>
    public class CollisionModuleV2 : ParticleModuleBase
    {
        public override string ModuleName => "Collision";
        public float Bounce = 0.5f;
        public float Friction = 0.1f;
        public float KillThreshold = 0.05f;
        public List<GPUColliderData> Colliders = new();

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled) return;
            var data = ctx.Data;
            ForEachAlive(data, i =>
            {
                foreach (var c in Colliders)
                {
                    if ((GPUCollisionType)c.ColliderType == GPUCollisionType.Plane)
                    {
                        float h = Vector3.Dot(data.Position[i] - c.Position, c.Normal);
                        if (h < 0)
                        {
                            data.Position[i] -= c.Normal * h;
                            float vn = Vector3.Dot(data.Velocity[i], c.Normal);
                            if (vn < 0)
                                data.Velocity[i] -= c.Normal * (1f + Bounce) * vn;
                            data.Velocity[i] *= (1f - Friction);
                            if (data.Velocity[i].LengthSquared() < KillThreshold)
                            {
                                data.Flags[i] &= ~1u;
                                ctx.Events.Enqueue(ParticleEventType.Died, i, data.Position[i]);
                            }
                            ctx.Events.Enqueue(ParticleEventType.Collided, i, data.Position[i]);
                        }
                    }
                    else if ((GPUCollisionType)c.ColliderType == GPUCollisionType.Sphere)
                    {
                        Vector3 d = data.Position[i] - c.Position;
                        float dist = d.Length();
                        if (dist < c.Radius && dist > 1e-4f)
                        {
                            Vector3 n = d / dist;
                            data.Position[i] = c.Position + n * c.Radius;
                            float vn = Vector3.Dot(data.Velocity[i], n);
                            if (vn < 0)
                                data.Velocity[i] -= n * (1f + Bounce) * vn;
                            ctx.Events.Enqueue(ParticleEventType.Collided, i, data.Position[i]);
                        }
                    }
                }
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第五部分：事件系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>粒子事件类型。</summary>
    public enum ParticleEventType
    {
        /// <summary>粒子被生成。</summary>
        Spawned,
        /// <summary>粒子发生碰撞。</summary>
        Collided,
        /// <summary>粒子死亡（寿命结束/碰撞杀死）。</summary>
        Died,
        /// <summary>粒子被外部信号杀死。</summary>
        Killed,
        /// <summary>自定义用户事件。</summary>
        Custom
    }

    /// <summary>单个粒子事件记录。</summary>
    public struct ParticleEvent
    {
        public ParticleEventType Type;
        public int ParticleIndex;
        public Vector3 Position;
        public Vector3 Normal;
        public float Speed;
        public uint UserData;
    }

    /// <summary>事件处理器委托。</summary>
    public delegate void ParticleEventHandler(in ParticleEvent evt);

    /// <summary>
    /// 粒子事件队列。模块执行期间产生的事件入队，帧末统一派发给订阅者。
    /// </summary>
    public class ParticleEventQueue
    {
        private readonly List<ParticleEvent> _events = new();
        private readonly Dictionary<ParticleEventType, List<ParticleEventHandler>> _handlers = new();

        /// <summary>入队一个事件。</summary>
        public void Enqueue(ParticleEventType type, int index, Vector3 pos)
        {
            _events.Add(new ParticleEvent { Type = type, ParticleIndex = index, Position = pos });
        }

        /// <summary>订阅某类事件。</summary>
        public void Subscribe(ParticleEventType type, ParticleEventHandler handler)
        {
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<ParticleEventHandler>();
                _handlers[type] = list;
            }
            list.Add(handler);
        }

        /// <summary>派发所有排队事件给订阅者。</summary>
        public void Dispatch()
        {
            foreach (var evt in _events)
            {
                if (_handlers.TryGetValue(evt.Type, out var list))
                    foreach (var h in list) h(in evt);
            }
            _events.Clear();
        }

        /// <summary>获取本帧全部事件（供子发射器消费）。</summary>
        public IReadOnlyList<ParticleEvent> Events => _events;
    }

    // ──────────────────────────────────────────────────────────────────
    // 第六部分：子发射器系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>子发射器触发时机。</summary>
    public enum SubEmitterTrigger
    {
        /// <summary>父粒子生成时。</summary>
        OnSpawn,
        /// <summary>父粒子碰撞时。</summary>
        OnCollide,
        /// <summary>父粒子死亡时。</summary>
        OnDeath
    }

    /// <summary>
    /// 子发射器描述。在特定事件发生时派生新粒子，可继承父粒子状态。
    /// </summary>
    public class SubEmitterDesc
    {
        public string Name = "SubEmitter";
        public SubEmitterTrigger Trigger = SubEmitterTrigger.OnDeath;
        public int SpawnCount = 5;
        public bool InheritVelocity = true;
        public float InheritedVelocityScale = 0.5f;
        public bool InheritPosition = true;
        public bool InheritColor = false;
        public GPUEmissionShape Shape = GPUEmissionShape.Sphere;
        public float Radius = 0.2f;
        public ParticleCurve Speed = new(1f, 3f);
        public ParticleCurve Lifetime = new(1f, 2f);
        public Vector4 Color = new(1, 1, 1, 1);
        public Vector3 EmissionDirection = Vector3.UnitY;
    }

    /// <summary>
    /// 子发射器模块。监听事件队列，在触发时机生成子粒子。
    /// </summary>
    public class SubEmitterModule : ParticleModuleBase
    {
        public override string ModuleName => "SubEmitter";
        public readonly List<SubEmitterDesc> SubEmitters = new();
        private readonly Random _rng = new();

        public override void Execute(ref ModuleContext ctx)
        {
            if (!Enabled || SubEmitters.Count == 0) return;
            var data = ctx.Data;
            foreach (var evt in ctx.Events.Events)
            {
                foreach (var sub in SubEmitters)
                {
                    bool match = sub.Trigger switch
                    {
                        SubEmitterTrigger.OnSpawn => evt.Type == ParticleEventType.Spawned,
                        SubEmitterTrigger.OnCollide => evt.Type == ParticleEventType.Collided,
                        SubEmitterTrigger.OnDeath => evt.Type == ParticleEventType.Died,
                        _ => false
                    };
                    if (!match) continue;

                    // 继承父粒子状态
                    Vector3 basePos = sub.InheritPosition ? evt.Position : ctx.EmitterPosition;
                    Vector3 baseVel = sub.InheritVelocity
                        ? (evt.ParticleIndex >= 0 && evt.ParticleIndex < data.Capacity
                            ? data.Velocity[evt.ParticleIndex] * sub.InheritedVelocityScale
                            : Vector3.Zero)
                        : Vector3.Zero;
                    Vector4 baseCol = sub.InheritColor && evt.ParticleIndex >= 0 && evt.ParticleIndex < data.Capacity
                        ? data.Color[evt.ParticleIndex] : sub.Color;

                    for (int s = 0; s < sub.SpawnCount; s++)
                        SpawnChild(data, sub, basePos, baseVel, baseCol);
                }
            }
        }

        private void SpawnChild(ParticleSOA data, SubEmitterDesc sub, Vector3 pos, Vector3 vel, Vector4 col)
        {
            int slot = -1;
            for (int i = 0; i < data.Capacity; i++)
                if ((data.Flags[i] & 1u) == 0) { slot = i; break; }
            if (slot < 0) return;

            float r1 = (float)_rng.NextDouble();
            float theta = r1 * MathF.PI * 2f;
            Vector3 offset = sub.Shape == GPUEmissionShape.Sphere
                ? new Vector3(MathF.Cos(theta), MathF.Sin(theta), MathF.Sin(theta + 1f)) * sub.Radius * (float)_rng.NextDouble()
                : Vector3.Zero;
            Vector3 dir = Vector3.Normalize(offset.LengthSquared() > 1e-6f ? offset : sub.EmissionDirection);

            data.Position[slot] = pos + offset;
            data.Velocity[slot] = vel + dir * sub.Speed.Evaluate(0f, (float)_rng.NextDouble());
            data.Color[slot] = col;
            data.Age[slot] = 0f;
            data.Lifetime[slot] = sub.Lifetime.Evaluate(0f, (float)_rng.NextDouble());
            data.RandomSeed[slot] = (float)_rng.NextDouble();
            data.Flags[slot] = 1u | 4u; // alive + child 标记
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第七部分：模块容器/管线
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子模块管线。管理一组模块的注册、排序与顺序执行。
    /// </summary>
    public class ParticleModulePipeline
    {
        private readonly List<ParticleModuleBase> _modules = new();
        private readonly ParticleEventQueue _eventQueue = new();

        public ParticleSOA? Data { get; private set; }
        public IReadOnlyList<ParticleModuleBase> Modules => _modules;
        public ParticleEventQueue Events => _eventQueue;

        /// <summary>添加模块。</summary>
        public void AddModule(ParticleModuleBase module)
        {
            _modules.Add(module);
            _modules.Sort((a, b) => a.ExecutionOrder.CompareTo(b.ExecutionOrder));
        }

        /// <summary>获取或添加指定类型的模块（单例）。</summary>
        public T GetOrAdd<T>() where T : ParticleModuleBase, new()
        {
            foreach (var m in _modules)
                if (m is T t) return t;
            var nm = new T();
            AddModule(nm);
            return nm;
        }

        /// <summary>初始化管线与 SOA 数据。</summary>
        public void Initialize(RHIDevice device, int maxParticles)
        {
            Data = new ParticleSOA(maxParticles, device);
            foreach (var m in _modules) m.Initialize(device);
        }

        /// <summary>每帧执行全部模块。</summary>
        public void Update(float dt, Vector3 cameraPos, Vector3 emitterPos, Matrix4x4 view, Matrix4x4 proj)
        {
            if (Data == null) return;
            var ctx = new ModuleContext
            {
                DeltaTime = dt,
                ElapsedTime = 0f,
                CameraPosition = cameraPos,
                EmitterPosition = emitterPos,
                ViewMatrix = view,
                ProjMatrix = proj,
                Data = Data,
                Events = _eventQueue
            };
            foreach (var m in _modules)
                if (m.Enabled) m.Execute(ref ctx);
            _eventQueue.Dispatch();
            Data.RecountAlive();
        }

        /// <summary>清空全部粒子。</summary>
        public void Clear() => Data?.Clear();
    }
}
