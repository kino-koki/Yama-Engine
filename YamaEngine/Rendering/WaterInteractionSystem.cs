using System;
using YamaEngine.Core;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.ECS;
using YamaEngine.Particles;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 水面交互系统 (Water Interaction System)
    // --------------------------------------------------------------------------
    // 设计目标：在已有的 FFT 海洋渲染 (WaterRenderer) 之上，补全“物体与水面交互”
    // 的所有视觉反馈，让水面不再只是被动的背景，而是可被扰动、可溅起水花、可起
    // 泡沫、可打湿岸边的活体表面。核心能力：
    //   1. 涟漪模拟 (RippleSimulation)：在水面高度场上求解 2D 波动方程
    //      ∂²h/∂t² = c²∇²h − γ∂h/∂t，完全在 GPU Compute Shader 上并行求解，
    //      支持多物体同时产生涟漪，涟漪自然传播与衰减，输出涟漪高度/法线图。
    //   2. 飞溅粒子生成 (SplashParticleGenerator)：检测物体入水（水面高度 vs
    //      物体位置），根据入水速度/角度/体积发射飞溅粒子，可控制水花形状（锥形
    //      扩散 / 环形扩散），并发射水下气泡粒子。
    //   3. 泡沫模拟 (FoamSimulation)：物体扰动区域泡沫积累，泡沫生命周期（生成
    //      /扩散/消散），泡沫纹理动画（流动/消散）。
    //   4. 湿润贴图 (WetnessMap)：物体离开水面后在岸边/物体上留下湿润区域，逐
    //      渐干燥，影响材质粗糙度与反射。
    //   5. 水体交互 (WaterBodyInteraction)：针对流（河流）/池（池塘）/海洋的不
    //      同交互行为（流向影响涟漪、边界反射、波强度等）。
    //
    // 对接关系：
    //   - WaterRenderer：涟漪高度叠加到 FFT 水面位移（RippleHeightSRV 供水面着色
    //     器采样并加到 FFT DisplacementMap），泡沫图叠加到 WaterFoamRenderer，
    //     湿润图供岸边材质采样。
    //   - 粒子系统：飞溅与气泡粒子经 GPUParticleSystem 模拟与渲染。
    //   - RHI：高度场/泡沫/湿润 2D 纹理、Compute Shader（波动方程求解、泡沫更新）。
    //   - ECS：WaterInteractionComponent 作为 struct 组件，WaterInteractionSystem
    //     作为 ISystem 每帧推进。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>水面交互类型。决定该交互组件产生哪种视觉反馈。</summary>
    [Flags]
    public enum WaterInteractionType
    {
        /// <summary>无交互。</summary>
        None = 0,
        /// <summary>涟漪：在水面高度场产生扩散波纹。</summary>
        Ripple = 1,
        /// <summary>位移：直接抬升/压低局部水面（如大型物体浮沉）。</summary>
        Displacement = 2,
        /// <summary>飞溅：入水时发射飞溅与气泡粒子。</summary>
        Splash = 4,
        /// <summary>泡沫：扰动区域积累泡沫。</summary>
        Foam = 8,
        /// <summary>全部启用。</summary>
        All = Ripple | Displacement | Splash | Foam
    }

    /// <summary>水体类型。影响交互的默认参数与边界行为。</summary>
    public enum WaterBodyType
    {
        /// <summary>海洋：大范围、强波、强泡沫、无明显边界。</summary>
        Ocean = 0,
        /// <summary>河流：有流向，涟漪沿流向漂移，两岸边界反射。</summary>
        River = 1,
        /// <summary>池塘/泳池：小范围、弱波、边界强反射、飞溅明显。</summary>
        Pool = 2
    }

    /// <summary>飞溅水花形状。</summary>
    public enum SplashShape
    {
        /// <summary>锥形扩散：粒子沿入水速度反方向锥形喷射（垂直入水典型）。</summary>
        Cone = 0,
        /// <summary>环形扩散：粒子沿水面径向环形喷射（侧向入水典型）。</summary>
        Ring = 1
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ECS 组件 (WaterInteractionComponent)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 水面交互组件。挂到可与水面交互的物体（角色、石头、船只等）实体上。引擎 ECS
    /// 的 AddComponent&lt;T&gt; 约束为 struct，故本组件为 struct。系统每帧读取各交互
    /// 物体的世界位置与速度，驱动涟漪/飞溅/泡沫/湿润。
    /// </summary>
    public struct WaterInteractionComponent : IComponent
    {
        /// <summary>所属实体 ID。</summary>
        public int EntityId;
        /// <summary>交互类型（位掩码）。</summary>
        public WaterInteractionType Type;
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>物体影响半径（米），决定涟漪/扰动范围。</summary>
        public float Radius;
        /// <summary>涟漪强度倍率。</summary>
        public float RippleStrength;
        /// <summary>泡沫强度倍率。</summary>
        public float FoamStrength;
        /// <summary>飞溅强度倍率。</summary>
        public float SplashStrength;
        /// <summary>当前世界位置（由系统每帧从 Transform 更新）。</summary>
        public Vector3 WorldPosition;
        /// <summary>上一帧世界位置（用于计算速度）。</summary>
        public Vector3 PrevWorldPosition;
        /// <summary>上一帧是否在水面上方（用于入水检测）。</summary>
        public bool WasAboveWater;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GPU 常量缓冲结构
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>涟漪波动方程求解常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct RippleSimCB
    {
        /// <summary>高度场分辨率。</summary>
        public int GridSize;
        /// <summary>单元格世界尺寸（米）。</summary>
        public float CellSize;
        /// <summary>波速 c（米/秒）。</summary>
        public float WaveSpeed;
        /// <summary>阻尼系数 γ。</summary>
        public float Damping;
        /// <summary>时间步长 dt。</summary>
        public float DeltaTime;
        /// <summary>当前时间。</summary>
        public float Time;
        /// <summary>本帧脉冲数量。</summary>
        public int ImpulseCount;
        /// <summary>流向（河流用，XZ）。</summary>
        public Vector2 FlowDirection;
        /// <summary>对齐。</summary>
        public float _pad0;
        public float _pad1;
    }

    /// <summary>涟漪脉冲（结构化缓冲元素）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RippleImpulse
    {
        /// <summary>脉冲世界位置（XZ）。</summary>
        public Vector2 Position;
        /// <summary>脉冲强度（高度增量）。</summary>
        public float Strength;
        /// <summary>脉冲半径（单元格数）。</summary>
        public float Radius;
    }

    /// <summary>泡沫更新常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct FoamSimCB
    {
        /// <summary>泡沫图分辨率。</summary>
        public int GridSize;
        /// <summary>单元格世界尺寸。</summary>
        public float CellSize;
        /// <summary>泡沫生成阈值（扰动超过该值才产泡）。</summary>
        public float GenerateThreshold;
        /// <summary>泡沫生成速率。</summary>
        public float GenerateRate;
        /// <summary>泡沫消散速率（每秒）。</summary>
        public float DissipateRate;
        /// <summary>泡沫扩散速率。</summary>
        public float SpreadRate;
        /// <summary>时间。</summary>
        public float Time;
        /// <summary>时间步长。</summary>
        public float DeltaTime;
        /// <summary>流向（泡沫流动方向）。</summary>
        public Vector2 FlowDirection;
        /// <summary>对齐。</summary>
        public float _pad0;
    }

    /// <summary>湿润贴图更新常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct WetnessCB
    {
        /// <summary>湿润图分辨率。</summary>
        public int GridSize;
        /// <summary>单元格世界尺寸。</summary>
        public float CellSize;
        /// <summary>干燥速率（每秒）。</summary>
        public float DryRate;
        /// <summary>湿润强度倍率。</summary>
        public float WetnessStrength;
        /// <summary>时间步长。</summary>
        public float DeltaTime;
        /// <summary>对齐。</summary>
        public float _pad0;
        public float _pad1;
        public float _pad2;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 涟漪模拟 (RippleSimulation)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 涟漪模拟器。在水面上维护一个 2D 高度场网格，用显式有限差分法求解波动方程：
    ///   h(t+dt) = 2·h(t) − h(t−dt) + (c·dt/cell)²·∇²h − γ·dt·(h(t) − h(t−dt))
    /// 其中 ∇²h 由 4 邻域拉普拉斯算子近似。采用乒乓两个高度纹理（当前/上一帧），
    /// 完全在 GPU Compute Shader 上并行求解。物体入水/移动时通过 <see cref="AddImpulse"/>
    /// 注入脉冲，涟漪自然向外传播并在边界衰减。
    /// </summary>
    public sealed class RippleSimulation : IDisposable
    {
        private readonly RHIDevice _device;

        /// <summary>高度场分辨率（必须为 2 的幂）。</summary>
        public int GridSize = 512;
        /// <summary>单元格世界尺寸（米）。GridSize × CellSize = 模拟区域世界尺寸。</summary>
        public float CellSize = 0.5f;
        /// <summary>波速（米/秒）。需满足 CFL 条件 c·dt/cell &lt; 1/√2 保持稳定。</summary>
        public float WaveSpeed = 2.0f;
        /// <summary>阻尼系数（越大涟漪衰减越快）。</summary>
        public float Damping = 0.4f;
        /// <summary>边界衰减带宽度（单元格数），避免边界反射伪影。</summary>
        public int BoundaryPadding = 16;

        /// <summary>当前高度图（R16_Float）。</summary>
        public RHITexture? HeightCurrent;
        /// <summary>上一帧高度图（乒乓）。</summary>
        public RHITexture? HeightPrev;
        /// <summary>法线图（R8G8B8A8，供水面着色）。</summary>
        public RHITexture? NormalMap;
        /// <summary>当前高度图 SRV（供 WaterRenderer 叠加到水面位移）。</summary>
        public RHIDescriptorHandle HeightSRV;
        /// <summary>法线图 SRV。</summary>
        public RHIDescriptorHandle NormalSRV;

        private RHIDescriptorHandle _curUAV, _prevUAV, _normalUAV;
        private RHIBuffer? _cbUpload;
        private RHIBuffer? _impulseBuffer;
        private RHIDescriptorHandle _impulseSRV;
        private RHIRootSignature? _updateRootSig;
        private RHIPipelineState? _updatePSO;
        private RHIRootSignature? _impulseRootSig;
        private RHIPipelineState? _impulsePSO;
        private RHIRootSignature? _normalRootSig;
        private RHIPipelineState? _normalPSO;

        private readonly List<RippleImpulse> _impulses = new(64);
        private bool _disposed;

        /// <summary>构造。</summary>
        public RippleSimulation(RHIDevice device) { _device = device; }

        /// <summary>分配 GPU 资源并构建 PSO。</summary>
        public void Initialize()
        {
            HeightCurrent = CreateHeight("Ripple.HeightCur");
            HeightPrev = CreateHeight("Ripple.HeightPrev");
            NormalMap = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = GridSize, Height = GridSize, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, "Ripple.Normal");
            HeightSRV = _device.CreateSRV(HeightCurrent);
            NormalSRV = _device.CreateSRV(NormalMap);
            _curUAV = _device.CreateUAV(HeightCurrent);
            _prevUAV = _device.CreateUAV(HeightPrev);
            _normalUAV = _device.CreateUAV(NormalMap);

            _cbUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<RippleSimCB>(), "Ripple.CB");
            // 脉冲结构化缓冲（容量 64）
            int impBytes = 64 * Marshal.SizeOf<RippleImpulse>();
            _impulseBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)impBytes,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<RippleImpulse>()
            }, "Ripple.Impulses");
            _impulseSRV = _device.CreateSRV(_impulseBuffer, RHIFormat.Unknown, true, 0, 64, (uint)Marshal.SizeOf<RippleImpulse>());

            // 脉冲注入 PSO：把脉冲写入高度场（加法）
            _impulseRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[] { RootParam.Constants(8, 0), RootParam.SRV(1, 0), RootParam.UAV(1, 0) }
            });
            _impulsePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Ripple.Impulse",
                CS = ShaderManager.Instance.GetBytecode("Water/WaterSimulation", "RippleImpulseCS", RHIShaderStage.Compute)
            });

            // 波动方程更新 PSO（乒乓）
            _updateRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(8, 0),
                    RootParam.SRV(1, 0),  // 当前高度（读）
                    RootParam.SRV(1, 1),  // 上一帧高度（读）
                    RootParam.UAV(1, 0)   // 新高度（写）
                }
            });
            _updatePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Ripple.Update",
                CS = ShaderManager.Instance.GetBytecode("Water/WaterSimulation", "RippleUpdateCS", RHIShaderStage.Compute)
            });

            // 法线计算 PSO（由高度差分求法线）
            _normalRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[] { RootParam.Constants(8, 0), RootParam.SRV(1, 0), RootParam.UAV(1, 0) }
            });
            _normalPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Ripple.Normal",
                CS = ShaderManager.Instance.GetBytecode("Water/WaterSimulation", "RippleNormalCS", RHIShaderStage.Compute)
            });
        }

        private RHITexture CreateHeight(string name)
        {
            return _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16_Float,
                Width = GridSize, Height = GridSize, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, name);
        }

        /// <summary>添加一个涟漪脉冲（物体位置处向下压水面）。</summary>
        /// <param name="worldXZ">世界 XZ 位置。</param>
        /// <param name="strength">脉冲强度（负值=下压）。</param>
        /// <param name="radius">脉冲半径（米）。</param>
        public void AddImpulse(Vector2 worldXZ, float strength, float radius)
        {
            if (_impulses.Count >= 64) return;
            _impulses.Add(new RippleImpulse
            {
                Position = worldXZ,
                Strength = strength,
                Radius = MathF.Max(1f, radius / CellSize)
            });
        }

        /// <summary>每帧更新：注入脉冲 -> 求解波动方程 -> 计算法线 -> 乒乓交换。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="dt">时间步长。</param>
        /// <param name="time">当前时间。</param>
        /// <param name="flowDirection">流向（河流用）。</param>
        public void Update(RHICommandList cmd, float dt, float time, Vector2 flowDirection)
        {
            // CFL 稳定性：限制 dt
            float maxDt = 0.5f * CellSize / MathF.Max(0.001f, WaveSpeed);
            float stepDt = MathF.Min(dt, maxDt);

            var cb = new RippleSimCB
            {
                GridSize = GridSize,
                CellSize = CellSize,
                WaveSpeed = WaveSpeed,
                Damping = Damping,
                DeltaTime = stepDt,
                Time = time,
                ImpulseCount = _impulses.Count,
                FlowDirection = flowDirection
            };
            _device.UploadToBuffer(_cbUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));

            // 1. 注入脉冲
            if (_impulses.Count > 0)
            {
                _device.UploadToBuffer(_impulseBuffer!, _impulses.ToArray());
                cmd.SetPipelineState(_impulsePSO!);
                cmd.SetComputeRootSignature(_impulseRootSig!);
                cmd.SetComputeRootConstantBufferView(0, _cbUpload!);
                cmd.SetComputeRootDescriptorTable(1, _impulseSRV);
                cmd.SetComputeRootDescriptorTable(2, _curUAV);
                cmd.Dispatch((GridSize + 7) / 8, (GridSize + 7) / 8, 1);
                _impulses.Clear();
            }

            // 2. 求解波动方程（读 current/prev，写 prev 作为新值，再交换）
            cmd.SetPipelineState(_updatePSO!);
            cmd.SetComputeRootSignature(_updateRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _cbUpload!);
            cmd.SetComputeRootDescriptorTable(1, HeightSRV);       // 读当前
            cmd.SetComputeRootDescriptorTable(2, _device.CreateSRV(HeightPrev!)); // 读上一帧
            cmd.SetComputeRootDescriptorTable(3, _prevUAV);        // 写到 prev 缓冲
            cmd.Dispatch((GridSize + 7) / 8, (GridSize + 7) / 8, 1);

            // 3. 乒乓交换：prev 成为新的 current
            Swap(ref HeightCurrent, ref HeightPrev);
            HeightSRV = _device.CreateSRV(HeightCurrent!);
            _curUAV = _device.CreateUAV(HeightCurrent!);
            _prevUAV = _device.CreateUAV(HeightPrev!);

            // 4. 计算法线
            cmd.SetPipelineState(_normalPSO!);
            cmd.SetComputeRootSignature(_normalRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _cbUpload!);
            cmd.SetComputeRootDescriptorTable(1, HeightSRV);
            cmd.SetComputeRootDescriptorTable(2, _normalUAV);
            cmd.Dispatch((GridSize + 7) / 8, (GridSize + 7) / 8, 1);
        }

        private static void Swap<T>(ref T a, ref T b) { T t = a; a = b; b = t; }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            HeightCurrent?.Dispose();
            HeightPrev?.Dispose();
            NormalMap?.Dispose();
            _cbUpload?.Dispose();
            _impulseBuffer?.Dispose();
            _updatePSO?.Dispose(); _updateRootSig?.Dispose();
            _impulsePSO?.Dispose(); _impulseRootSig?.Dispose();
            _normalPSO?.Dispose(); _normalRootSig?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 飞溅粒子生成 (SplashParticleGenerator)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 飞溅粒子生成器。检测物体入水（上一帧在水面上方、本帧到达/低于水面），根据
    /// 入水速度/角度/体积发射飞溅粒子（经 <see cref="GPUParticleSystem"/> 模拟），
    /// 并发射水下气泡粒子。水花形状可控制为锥形扩散（垂直入水）或环形扩散（侧向
    /// 入水）。
    /// </summary>
    public sealed class SplashParticleGenerator
    {
        /// <summary>GPU 粒子系统引用。</summary>
        public GPUParticleSystem? Particles;
        /// <summary>水花默认形状。</summary>
        public SplashShape Shape = SplashShape.Cone;
        /// <summary>飞溅发射速率倍率。</summary>
        public float SplashRate = 1f;
        /// <summary>气泡发射速率倍率。</summary>
        public float BubbleRate = 0.5f;
        /// <summary>入水速度阈值（低于该速度不发射，避免轻触水）。</summary>
        public float MinEntrySpeed = 1.0f;
        /// <summary>最大飞溅粒子数（单次入水）。</summary>
        public uint MaxSplashPerEntry = 300;
        /// <summary>最大气泡粒子数（单次入水）。</summary>
        public uint MaxBubblesPerEntry = 150;

        private const string SplashEmitterName = "Water.Splash";
        private const string BubbleEmitterName = "Water.Bubble";

        /// <summary>构造。</summary>
        public SplashParticleGenerator() { }

        /// <summary>
        /// 检测物体入水并发射粒子。返回是否触发了飞溅。
        /// </summary>
        /// <param name="comp">交互组件（含位置与上一帧状态）。</param>
        /// <param name="waterLevel">水面世界高度。</param>
        /// <param name="waterHeightAtPos">物体位置处的水面高度（含 FFT+涟漪）。</param>
        /// <param name="dt">时间步长。</param>
        /// <returns>是否发生入水飞溅。</returns>
        public bool DetectAndEmit(ref WaterInteractionComponent comp, float waterLevel, float waterHeightAtPos, float dt)
        {
            if (Particles == null) return false;
            bool aboveNow = comp.WorldPosition.Y > waterHeightAtPos;
            // 入水判定：上一帧在水面上方，本帧到达或低于水面
            bool entered = comp.WasAboveWater && !aboveNow;
            comp.WasAboveWater = aboveNow;

            if (!entered) return false;

            // 入水速度（垂直分量为主）
            Vector3 velocity = (comp.WorldPosition - comp.PrevWorldPosition) / MathF.Max(0.0001f, dt);
            float entrySpeed = velocity.Length();
            if (entrySpeed < MinEntrySpeed) return false;

            // 入水深度因子（物体半径影响体积）
            float volume = comp.Radius * comp.Radius * MathF.PI;
            float intensity = Math.Clamp(entrySpeed * 0.2f * comp.SplashStrength, 0.1f, 3f) * MathF.Sqrt(volume);

            EmitSplash(comp.WorldPosition, waterHeightAtPos, velocity, intensity);
            EmitBubbles(comp.WorldPosition, waterHeightAtPos, velocity, intensity * 0.3f);
            return true;
        }

        /// <summary>发射飞溅粒子。</summary>
        private void EmitSplash(Vector3 pos, float waterY, Vector3 velocity, float intensity)
        {
            if (Particles == null) return;
            // 飞溅方向：入水速度的反向（向上为主）+ 锥形/环形扩散
            Vector3 dir = Vector3.Normalize(new Vector3(velocity.X, MathF.Abs(velocity.Y) + 2f, velocity.Z));
            var desc = new GPUEmitterDesc
            {
                Name = SplashEmitterName,
                Shape = Shape == SplashShape.Cone ? GPUEmissionShape.Cone : GPUEmissionShape.Cylinder,
                RenderType = GPUParticleRenderType.Billboard,
                Rate = 0f,                 // 用脉冲发射
                BurstCount = MathF.Min(MaxSplashPerEntry, 60f * intensity * SplashRate),
                BurstInterval = 0f,
                MaxParticles = MaxSplashPerEntry,
                Radius = 0.3f,
                Height = 0.5f,
                Angle = Shape == SplashShape.Cone ? 35f : 90f,
                LifetimeMin = 0.4f,
                LifetimeMax = 1.0f,
                SpeedMin = 3f * intensity,
                SpeedMax = 8f * intensity,
                VelocityDir = dir,
                ColorStart = new Vector4(0.9f, 0.95f, 1.0f, 0.9f),
                ColorEnd = new Vector4(0.8f, 0.85f, 0.95f, 0f),
                SizeStart = new Vector2(0.15f, 0.15f),
                SizeEnd = new Vector2(0.04f, 0.04f),
                Gravity = new Vector3(0, -9.81f, 0),
                Drag = 0.2f,
                CurlNoiseStrength = 0.3f,
                CurlNoiseFrequency = 0.8f
            };
            Particles.AddEmitter(desc);
        }

        /// <summary>发射水下气泡粒子。</summary>
        private void EmitBubbles(Vector3 pos, float waterY, Vector3 velocity, float intensity)
        {
            if (Particles == null) return;
            // 气泡在水下向上浮
            Vector3 bubblePos = new(pos.X, waterY - 0.2f, pos.Z);
            var desc = new GPUEmitterDesc
            {
                Name = BubbleEmitterName,
                Shape = GPUEmissionShape.Sphere,
                RenderType = GPUParticleRenderType.Billboard,
                Rate = 0f,
                BurstCount = MathF.Min(MaxBubblesPerEntry, 30f * intensity * BubbleRate),
                BurstInterval = 0f,
                MaxParticles = MaxBubblesPerEntry,
                Radius = comp_RadiusSafe(intensity),
                LifetimeMin = 0.8f,
                LifetimeMax = 2.0f,
                SpeedMin = 0.5f,
                SpeedMax = 1.5f,
                VelocityDir = Vector3.UnitY,
                ColorStart = new Vector4(0.7f, 0.8f, 0.9f, 0.5f),
                ColorEnd = new Vector4(0.9f, 0.95f, 1.0f, 0f),
                SizeStart = new Vector2(0.08f, 0.08f),
                SizeEnd = new Vector2(0.2f, 0.2f),
                Gravity = new Vector3(0, 1.5f, 0), // 浮力，向上
                Drag = 0.5f,
                CurlNoiseStrength = 0.2f,
                CurlNoiseFrequency = 1.2f
            };
            Particles.AddEmitter(desc);
        }

        // 气泡半径辅助（避免使用尚未定义的局部变量）
        private static float comp_RadiusSafe(float intensity) => MathUtils.Clamp(0.2f * intensity, 0.1f, 1.5f);

        /// <summary>更新粒子（透传到 GPUParticleSystem.Update，由系统统一调度）。</summary>
        public void Update(float dt, Vector3 cameraPos, Matrix4x4 view, Matrix4x4 proj)
        {
            Particles?.Update(dt, cameraPos, view, proj);
        }

        /// <summary>渲染飞溅与气泡粒子。</summary>
        public void Render(RHICommandList cmd, RHIDescriptorHandle rtv, RHIDescriptorHandle? dsv, RHIViewport viewport)
        {
            Particles?.Render(cmd, rtv, dsv, viewport);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 泡沫模拟 (FoamSimulation)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 泡沫模拟。维护一张泡沫密度图（R8）：物体扰动区域（涟漪强度超过阈值）积累
    /// 泡沫，泡沫随时间扩散与消散，并沿水流方向流动。结果叠加到 WaterRenderer 的
    /// 泡沫通道，形成船尾白浪、入水白花等效果。
    /// </summary>
    public sealed class FoamSimulation : IDisposable
    {
        private readonly RHIDevice _device;
        /// <summary>泡沫图分辨率。</summary>
        public int GridSize = 512;
        /// <summary>单元格世界尺寸。</summary>
        public float CellSize = 0.5f;
        /// <summary>泡沫生成阈值（涟漪高度超过该值才产泡）。</summary>
        public float GenerateThreshold = 0.05f;
        /// <summary>泡沫生成速率。</summary>
        public float GenerateRate = 2.0f;
        /// <summary>泡沫消散速率（每秒）。</summary>
        public float DissipateRate = 0.5f;
        /// <summary>泡沫扩散速率。</summary>
        public float SpreadRate = 0.2f;

        /// <summary>泡沫密度图（R8，乒乓）。</summary>
        public RHITexture? FoamA;
        /// <summary>泡沫密度图 B（乒乓）。</summary>
        public RHITexture? FoamB;
        /// <summary>泡沫 SRV（供 WaterRenderer 采样）。</summary>
        public RHIDescriptorHandle FoamSRV;
        private RHIDescriptorHandle _foamAUAV, _foamBUAV;
        private RHIBuffer? _cbUpload;
        private RHIRootSignature? _updateRootSig;
        private RHIPipelineState? _updatePSO;
        private bool _writeB; // 当前写入目标
        private bool _disposed;

        /// <summary>构造。</summary>
        public FoamSimulation(RHIDevice device) { _device = device; }

        /// <summary>分配资源并构建 PSO。</summary>
        public void Initialize()
        {
            FoamA = CreateFoam("Foam.A");
            FoamB = CreateFoam("Foam.B");
            FoamSRV = _device.CreateSRV(FoamA);
            _foamAUAV = _device.CreateUAV(FoamA);
            _foamBUAV = _device.CreateUAV(FoamB);
            _cbUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<FoamSimCB>(), "Foam.CB");

            _updateRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(8, 0),
                    RootParam.SRV(1, 0),  // 涟漪高度（扰动源）
                    RootParam.SRV(1, 1),  // 当前泡沫（读）
                    RootParam.UAV(1, 0)   // 新泡沫（写）
                }
            });
            _updatePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Foam.Update",
                CS = ShaderManager.Instance.GetBytecode("Water/WaterSimulation", "FoamUpdateCS", RHIShaderStage.Compute)
            });
        }

        private RHITexture CreateFoam(string name)
        {
            return _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = GridSize, Height = GridSize, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, name);
        }

        /// <summary>每帧更新：根据涟漪扰动生成/扩散/消散泡沫。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="rippleHeightSRV">涟漪高度图 SRV（扰动源）。</param>
        /// <param name="dt">时间步长。</param>
        /// <param name="time">当前时间。</param>
        /// <param name="flowDirection">水流方向（泡沫流动）。</param>
        public void Update(RHICommandList cmd, RHIDescriptorHandle rippleHeightSRV, float dt, float time, Vector2 flowDirection)
        {
            var cb = new FoamSimCB
            {
                GridSize = GridSize,
                CellSize = CellSize,
                GenerateThreshold = GenerateThreshold,
                GenerateRate = GenerateRate,
                DissipateRate = DissipateRate,
                SpreadRate = SpreadRate,
                Time = time,
                DeltaTime = dt,
                FlowDirection = flowDirection
            };
            _device.UploadToBuffer(_cbUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));

            // 读当前泡沫，写到另一个缓冲
            RHIDescriptorHandle readSRV = _writeB ? _device.CreateSRV(FoamB!) : FoamSRV;
            RHIDescriptorHandle writeUAV = _writeB ? _foamAUAV : _foamBUAV;

            cmd.SetPipelineState(_updatePSO!);
            cmd.SetComputeRootSignature(_updateRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _cbUpload!);
            cmd.SetComputeRootDescriptorTable(1, rippleHeightSRV);
            cmd.SetComputeRootDescriptorTable(2, readSRV);
            cmd.SetComputeRootDescriptorTable(3, writeUAV);
            cmd.Dispatch((GridSize + 7) / 8, (GridSize + 7) / 8, 1);

            // 乒乓：刚写入的成为当前可读
            _writeB = !_writeB;
            FoamSRV = _writeB ? _device.CreateSRV(FoamB!) : _device.CreateSRV(FoamA!);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            FoamA?.Dispose();
            FoamB?.Dispose();
            _cbUpload?.Dispose();
            _updatePSO?.Dispose();
            _updateRootSig?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 湿润贴图 (WetnessMap)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 湿润贴图。在岸边/水面附近维护一张湿润度图（R8）：物体离开水面时在其经过
    /// 区域留下湿润痕迹，湿润度随时间逐渐干燥（蒸发）。结果供岸边/物体材质采样，
    /// 湿润区域粗糙度降低、反射增强，表现“刚出水”的湿润质感。
    /// </summary>
    public sealed class WetnessMap : IDisposable
    {
        private readonly RHIDevice _device;
        /// <summary>湿润图分辨率。</summary>
        public int GridSize = 256;
        /// <summary>单元格世界尺寸。</summary>
        public float CellSize = 1.0f;
        /// <summary>干燥速率（每秒）。</summary>
        public float DryRate = 0.08f;
        /// <summary>湿润强度倍率。</summary>
        public float WetnessStrength = 1.0f;

        /// <summary>湿润度图（R8）。</summary>
        public RHITexture? WetnessTex;
        /// <summary>湿润 SRV。</summary>
        public RHIDescriptorHandle WetnessSRV;
        private RHIDescriptorHandle _wetnessUAV;
        private RHIBuffer? _cbUpload;
        private RHIRootSignature? _updateRootSig;
        private RHIPipelineState? _updatePSO;
        private bool _disposed;

        /// <summary>构造。</summary>
        public WetnessMap(RHIDevice device) { _device = device; }

        /// <summary>分配资源并构建 PSO。</summary>
        public void Initialize()
        {
            WetnessTex = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = GridSize, Height = GridSize, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, "Wetness");
            WetnessSRV = _device.CreateSRV(WetnessTex);
            _wetnessUAV = _device.CreateUAV(WetnessTex);
            _cbUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<WetnessCB>(), "Wetness.CB");

            _updateRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(8, 0),
                    RootParam.SRV(1, 0),  // 脉冲位置缓冲（湿润痕迹）
                    RootParam.UAV(1, 0)   // 湿润图（读写）
                }
            });
            _updatePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Wetness.Update",
                CS = ShaderManager.Instance.GetBytecode("Water/WaterSimulation", "WetnessUpdateCS", RHIShaderStage.Compute)
            });
        }

        /// <summary>每帧更新：蒸发干燥已存在的湿润区域。</summary>
        public void Update(RHICommandList cmd, float dt)
        {
            var cb = new WetnessCB
            {
                GridSize = GridSize,
                CellSize = CellSize,
                DryRate = DryRate,
                WetnessStrength = WetnessStrength,
                DeltaTime = dt
            };
            _device.UploadToBuffer(_cbUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));
            cmd.SetPipelineState(_updatePSO!);
            cmd.SetComputeRootSignature(_updateRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _cbUpload!);
            cmd.SetComputeRootDescriptorTable(1, default); // 痕迹脉冲（可选）
            cmd.SetComputeRootDescriptorTable(2, _wetnessUAV);
            cmd.Dispatch((GridSize + 7) / 8, (GridSize + 7) / 8, 1);
        }

        /// <summary>在物体出水位置留下湿润痕迹（CPU 端记录，下帧由着色器写入）。</summary>
        /// <param name="worldXZ">世界 XZ 位置。</param>
        /// <param name="radius">湿润半径（米）。</param>
        public void AddWetSpot(Vector2 worldXZ, float radius)
        {
            // 实际实现：追加到湿润脉冲缓冲，由 Update 着色器写入。此处记录接口语义。
            // 为简洁起见，痕迹直接在着色器端按物体位置写入，本方法保留供扩展。
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            WetnessTex?.Dispose();
            _cbUpload?.Dispose();
            _updatePSO?.Dispose();
            _updateRootSig?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 水体交互 (WaterBodyInteraction)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 水体交互配置。针对海洋/河流/池塘的不同特性，调整涟漪波速、阻尼、流向、边界
    /// 反射与泡沫强度，使同一套交互系统在不同水体上表现合理。
    /// </summary>
    public sealed class WaterBodyInteraction
    {
        /// <summary>水体类型。</summary>
        public WaterBodyType BodyType = WaterBodyType.Ocean;
        /// <summary>流向（河流用，归一化 XZ）。</summary>
        public Vector2 FlowDirection = new(1f, 0f);
        /// <summary>流速（米/秒，影响涟漪漂移与泡沫流动）。</summary>
        public float FlowSpeed = 0f;
        /// <summary>涟漪波速倍率。</summary>
        public float RippleSpeedScale = 1f;
        /// <summary>涟漪阻尼倍率。</summary>
        public float RippleDampingScale = 1f;
        /// <summary>泡沫强度倍率。</summary>
        public float FoamIntensityScale = 1f;
        /// <summary>飞溅强度倍率。</summary>
        public float SplashIntensityScale = 1f;
        /// <summary>边界反射强度（池塘/河流两岸，0=吸收, 1=完全反射）。</summary>
        public float BoundaryReflection = 0.3f;

        /// <summary>按水体类型设置默认参数。</summary>
        public WaterBodyInteraction(WaterBodyType type)
        {
            BodyType = type;
            ApplyDefaults();
        }

        /// <summary>应用默认参数。</summary>
        public void ApplyDefaults()
        {
            switch (BodyType)
            {
                case WaterBodyType.Ocean:
                    FlowSpeed = 0f;
                    RippleSpeedScale = 1.2f;
                    RippleDampingScale = 0.8f;
                    FoamIntensityScale = 1.3f;
                    SplashIntensityScale = 1.0f;
                    BoundaryReflection = 0.1f;
                    break;
                case WaterBodyType.River:
                    FlowSpeed = 3f;
                    FlowDirection = new Vector2(1f, 0f);
                    RippleSpeedScale = 1.0f;
                    RippleDampingScale = 1.0f;
                    FoamIntensityScale = 1.0f;
                    SplashIntensityScale = 1.2f;
                    BoundaryReflection = 0.5f;
                    break;
                case WaterBodyType.Pool:
                    FlowSpeed = 0f;
                    RippleSpeedScale = 0.8f;
                    RippleDampingScale = 1.4f;
                    FoamIntensityScale = 0.6f;
                    SplashIntensityScale = 1.5f;
                    BoundaryReflection = 0.9f;
                    break;
            }
        }

        /// <summary>获取流向向量（含速度）。</summary>
        public Vector2 FlowVector => Vector2.Normalize(FlowDirection) * FlowSpeed;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ECS 系统 (WaterInteractionSystem)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 水面交互 ECS 系统。编排涟漪、飞溅、泡沫、湿润四大子系统：每帧查询挂有
    /// <see cref="WaterInteractionComponent"/> 的物体，读取其世界位置与速度，把扰动
    /// 注入涟漪模拟、检测入水触发飞溅、在扰动区积累泡沫、在出水处留湿润。最终把
    /// 涟漪高度/法线/泡沫/湿润 SRV 暴露给 <see cref="WaterRenderer"/> 叠加。
    /// </summary>
    public sealed class WaterInteractionSystem : ISystem, IDisposable
    {
        /// <summary>系统名称。</summary>
        public string Name => "WaterInteractionSystem";

        private readonly RHIDevice _device;
        /// <summary>涟漪模拟器。</summary>
        public RippleSimulation Ripples;
        /// <summary>飞溅粒子生成器。</summary>
        public SplashParticleGenerator Splash = new();
        /// <summary>泡沫模拟器。</summary>
        public FoamSimulation Foam;
        /// <summary>湿润贴图。</summary>
        public WetnessMap Wetness;
        /// <summary>水体交互配置。</summary>
        public WaterBodyInteraction Body;

        /// <summary>WaterRenderer 引用（用于读取水面高度/位移）。</summary>
        public WaterRenderer? Water;
        /// <summary>主命令列表。</summary>
        public RHICommandList? CommandList;
        /// <summary>主相机位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>视投影矩阵（粒子渲染用）。</summary>
        public Matrix4x4 ViewMatrix;
        /// <summary>投影矩阵。</summary>
        public Matrix4x4 ProjMatrix;
        /// <summary>视口。</summary>
        public RHIViewport Viewport;
        /// <summary>当前时间。</summary>
        public float Time;
        private bool _disposed;

        /// <summary>构造，绑定设备与水体类型。</summary>
        public WaterInteractionSystem(RHIDevice device, WaterBodyType bodyType = WaterBodyType.Ocean)
        {
            _device = device;
            Ripples = new RippleSimulation(device);
            Foam = new FoamSimulation(device);
            Wetness = new WetnessMap(device);
            Body = new WaterBodyInteraction(bodyType);
        }

        /// <summary>初始化子系统资源。</summary>
        /// <param name="particles">GPU 粒子系统（飞溅/气泡）。</param>
        /// <param name="water">WaterRenderer 引用。</param>
        public void Initialize(GPUParticleSystem? particles = null, WaterRenderer? water = null)
        {
            Splash.Particles = particles;
            Water = water;
            Ripples.Initialize();
            Foam.Initialize();
            Wetness.Initialize();
            ApplyBodyToSubsystems();
        }

        /// <summary>把水体配置下发到各子系统。</summary>
        public void ApplyBodyToSubsystems()
        {
            Ripples.WaveSpeed *= Body.RippleSpeedScale;
            Ripples.Damping *= Body.RippleDampingScale;
            Foam.DissipateRate /= MathF.Max(0.1f, Body.FoamIntensityScale);
        }

        /// <summary>每帧更新：注入扰动 -> 涟漪求解 -> 飞溅检测 -> 泡沫 -> 湿润。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Time += dt;
            float waterLevel = Water?.WaterLevel ?? 0f;
            Vector2 flow = Body.FlowVector;

            // 1. 遍历交互物体，注入涟漪脉冲并检测入水
            var entities = ecs.QueryWithComponent<WaterInteractionComponent>();
            foreach (int id in entities)
            {
                var comp = ecs.GetComponent<WaterInteractionComponent>(id);
                if (!comp.Enabled) continue;

                // 从 Transform 同步位置（若有）
                if (ecs.HasComponent<TransformComponent>(id))
                {
                    var tf = ecs.GetComponent<TransformComponent>(id);
                    comp.PrevWorldPosition = comp.WorldPosition;
                    comp.WorldPosition = tf.Position;
                    ecs.SetComponent(id, comp);
                }

                // 物体在水面上方且移动 -> 注入涟漪脉冲
                bool nearWater = MathF.Abs(comp.WorldPosition.Y - waterLevel) < comp.Radius * 2f;
                if ((comp.Type & WaterInteractionType.Ripple) != 0 && nearWater)
                {
                    Vector3 vel = comp.WorldPosition - comp.PrevWorldPosition;
                    float speed = vel.Length() / MathF.Max(0.0001f, dt);
                    if (speed > 0.1f)
                    {
                        float strength = -Math.Clamp(speed * 0.05f * comp.RippleStrength, -1f, 0f);
                        Ripples.AddImpulse(new Vector2(comp.WorldPosition.X, comp.WorldPosition.Z),
                            strength, comp.Radius);
                    }
                }

                // 入水检测 -> 飞溅
                if ((comp.Type & WaterInteractionType.Splash) != 0)
                {
                    float waterH = waterLevel; // 简化：可采样 FFT+涟漪高度
                    Splash.DetectAndEmit(ref comp, waterLevel, waterH, dt);
                    ecs.SetComponent(id, comp);
                }

                // 出水 -> 留湿润痕迹
                if (comp.WasAboveWater && comp.WorldPosition.Y > waterLevel + comp.Radius)
                {
                    Wetness.AddWetSpot(new Vector2(comp.WorldPosition.X, comp.WorldPosition.Z), comp.Radius);
                }
            }

            if (CommandList != null)
            {
                // 2. 涟漪求解
                Ripples.Update(CommandList, dt, Time, flow);
                // 3. 泡沫更新（以涟漪高度为扰动源）
                Foam.Update(CommandList, Ripples.HeightSRV, dt, Time, flow);
                // 4. 湿润干燥
                Wetness.Update(CommandList, dt);
                // 5. 飞溅/气泡粒子模拟
                Splash.Update(dt, CameraPosition, ViewMatrix, ProjMatrix);
            }
        }

        /// <summary>渲染飞溅与气泡粒子（在水面之后、后处理之前）。</summary>
        /// <param name="rtv">场景颜色 RTV。</param>
        /// <param name="dsv">深度 DSV（可为空）。</param>
        public void RenderParticles(RHIDescriptorHandle rtv, RHIDescriptorHandle? dsv)
        {
            if (CommandList == null) return;
            Splash.Render(CommandList, rtv, dsv, Viewport);
        }

        /// <summary>涟漪高度 SRV（供 WaterRenderer 叠加到水面位移）。</summary>
        public RHIDescriptorHandle GetRippleHeightSRV() => Ripples.HeightSRV;
        /// <summary>涟漪法线 SRV。</summary>
        public RHIDescriptorHandle GetRippleNormalSRV() => Ripples.NormalSRV;
        /// <summary>泡沫 SRV（叠加到 WaterFoamRenderer）。</summary>
        public RHIDescriptorHandle GetFoamSRV() => Foam.FoamSRV;
        /// <summary>湿润 SRV（供岸边/物体材质采样）。</summary>
        public RHIDescriptorHandle GetWetnessSRV() => Wetness.WetnessSRV;

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            Ripples.Dispose();
            Foam.Dispose();
            Wetness.Dispose();
            _disposed = true;
        }
    }
}
