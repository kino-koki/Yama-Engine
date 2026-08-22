using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════
    // 山雨引擎 GPU 粒子计算管线 (Shanyu GPU Particle Compute Pipeline)
    // --------------------------------------------------------------------
    // 设计目标：提供完整的 GPU 粒子计算管线，作为 GPUParticleSystem.cs 的
    // 计算层补充，聚焦于以下核心能力：
    //   - 计算着色器接口：SimulateCS / EmitCS / CullCS 三阶段内核抽象
    //   - 粒子池管理：环形缓冲区、存活列表、死亡列表、原子分配/回收
    //   - 排序与剔除：距离排序（Bitonic/Merge）、视锥剔除、距离剔除
    //   - 间接绘制：IndirectDrawArgs 生成（DrawIndexedInstancedIndirect）
    //   - 曲线采样：GPU 端动画曲线采样（大小/颜色/速度随生命周期变化）
    //   - 力场集成：重力/风力/Curl Noise 湍流/吸引力/排斥力/涡旋/阻力
    //
    // 与已有文件的关系：
    //   - GPUParticleSystem.cs 定义了 GPUParticleSystem 主类（含完整 GPU 管线）
    //   - 本文件提供可复用的计算管线组件，可独立使用或组合到 GPUParticleSystem
    //   - 复用已有的 GPUParticle / GPUEmitterParams / GPUSimParams / GPUForceFieldData
    // ════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────
    // 第一部分：计算着色器阶段与内核接口
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU 粒子计算着色器阶段。描述粒子模拟管线的各阶段。
    /// </summary>
    public enum GPUComputeShaderStage
    {
        /// <summary>发射阶段：从死亡栈弹出索引，初始化新粒子。</summary>
        Emit,
        /// <summary>模拟阶段：更新位置/速度/颜色/尺寸，应用力场与碰撞。</summary>
        Simulate,
        /// <summary>回收/紧致阶段：重建存活索引列表，生成间接绘制参数。</summary>
        Cull,
        /// <summary>排序阶段：按相机距离/年龄排序粒子索引。</summary>
        Sort,
        /// <summary>力场计算阶段：独立计算力场贡献（可选分离 pass）。</summary>
        ForceField
    }

    /// <summary>
    /// GPU 计算着色器内核接口。封装一个计算着色器入口点及其资源绑定描述。
    /// 实现类负责编译 HLSL、创建 PSO、绑定根签名和资源。
    /// </summary>
    public interface IGPUComputeKernel : IDisposable
    {
        /// <summary>内核名称。</summary>
        string Name { get; }
        /// <summary>计算阶段。</summary>
        GPUComputeShaderStage Stage { get; }
        /// <summary>编译后的管线状态。</summary>
        RHIPipelineState? PipelineState { get; }
        /// <summary>根签名。</summary>
        RHIRootSignature? RootSignature { get; }
        /// <summary>线程组大小（X 维度，通常 64）。</summary>
        int ThreadGroupSize { get; }

        /// <summary>编译着色器并创建管线状态。</summary>
        /// <param name="device">RHI 设备</param>
        /// <param name="hlslSource">HLSL 源码</param>
        /// <param name="entryPoint">入口点函数名</param>
        void Compile(RHIDevice device, string hlslSource, string entryPoint);

        /// <summary>在命令列表上调度该内核。</summary>
        /// <param name="cmd">计算命令列表</param>
        /// <param name="threadCount">需要的总线程数</param>
        void Dispatch(RHICommandList cmd, int threadCount);

        /// <summary>绑定资源（UAV/SRV/CBV）到根签名槽位。</summary>
        void BindResources(RHICommandList cmd, GPUComputeResourceSet resources);
    }

    /// <summary>
    /// 计算着色器资源绑定集合。描述一次 dispatch 所需的全部 GPU 资源。
    /// </summary>
    public class GPUComputeResourceSet
    {
        /// <summary>常量缓冲绑定（slot → buffer）。</summary>
        public readonly Dictionary<uint, RHIBuffer> ConstantBuffers = new();
        /// <summary>UAV 绑定（slot → buffer）。</summary>
        public readonly Dictionary<uint, RHIBuffer> UAVs = new();
        /// <summary>SRV 绑定（slot → buffer）。</summary>
        public readonly Dictionary<uint, RHIBuffer> SRVs = new();
        /// <summary>SRV 描述符表（slot → handle）。</summary>
        public readonly Dictionary<uint, RHIDescriptorHandle> SRVTables = new();
        /// <summary>根常量（slot → uint[] 数据）。</summary>
        public readonly Dictionary<uint, uint[]> RootConstants = new();

        /// <summary>设置常量缓冲。</summary>
        public void SetCBV(uint slot, RHIBuffer buf) => ConstantBuffers[slot] = buf;
        /// <summary>设置 UAV。</summary>
        public void SetUAV(uint slot, RHIBuffer buf) => UAVs[slot] = buf;
        /// <summary>设置 SRV。</summary>
        public void SetSRV(uint slot, RHIBuffer buf) => SRVs[slot] = buf;
        /// <summary>设置 SRV 描述符表。</summary>
        public void SetSRVTable(uint slot, RHIDescriptorHandle handle) => SRVTables[slot] = handle;
        /// <summary>设置根常量。</summary>
        public void SetRootConstants(uint slot, params uint[] data) => RootConstants[slot] = data;

        /// <summary>清空所有绑定。</summary>
        public void Clear()
        {
            ConstantBuffers.Clear();
            UAVs.Clear();
            SRVs.Clear();
            SRVTables.Clear();
            RootConstants.Clear();
        }
    }

    /// <summary>
    /// 通用计算内核实现。封装 HLSL 编译、PSO 创建和资源绑定。
    /// </summary>
    public class GPUComputeKernel : IGPUComputeKernel
    {
        /// <inheritdoc/>
        public string Name { get; }
        /// <inheritdoc/>
        public GPUComputeShaderStage Stage { get; }
        /// <inheritdoc/>
        public RHIPipelineState? PipelineState { get; private set; }
        /// <inheritdoc/>
        public RHIRootSignature? RootSignature { get; private set; }
        /// <inheritdoc/>
        public int ThreadGroupSize { get; set; } = 64;

        private RHIDevice? _device;

        /// <summary>构造。</summary>
        /// <param name="name">内核名称</param>
        /// <param name="stage">计算阶段</param>
        public GPUComputeKernel(string name, GPUComputeShaderStage stage)
        {
            Name = name;
            Stage = stage;
        }

        /// <inheritdoc/>
        public void Compile(RHIDevice device, string hlslSource, string entryPoint)
        {
            _device = device;
            var cs = device.CompileShader(hlslSource, entryPoint, RHIShaderStage.Compute, device.MaxShaderModel);
            PipelineState = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = Name,
                CS = cs
            });
        }

        /// <summary>设置根签名。</summary>
        public void SetRootSignature(RHIRootSignature rootSig) => RootSignature = rootSig;

        /// <inheritdoc/>
        public void Dispatch(RHICommandList cmd, int threadCount)
        {
            if (PipelineState == null || RootSignature == null) return;
            cmd.SetComputeRootSignature(RootSignature);
            cmd.SetPipelineState(PipelineState);
            int groups = (threadCount + ThreadGroupSize - 1) / ThreadGroupSize;
            cmd.Dispatch(groups, 1, 1);
        }

        /// <inheritdoc/>
        public void BindResources(RHICommandList cmd, GPUComputeResourceSet resources)
        {
            foreach (var (slot, buf) in resources.ConstantBuffers)
                cmd.SetComputeRootConstantBufferView(slot, buf.GpuVirtualAddress);
            foreach (var (slot, buf) in resources.UAVs)
                cmd.SetComputeRootUnorderedAccessView(slot, buf.GpuVirtualAddress);
            foreach (var (slot, buf) in resources.SRVs)
                cmd.SetComputeRootShaderResourceView(slot, buf.GpuVirtualAddress);
            foreach (var (slot, handle) in resources.SRVTables)
                cmd.SetComputeRootDescriptorTable(slot, handle);
            foreach (var (slot, data) in resources.RootConstants)
            {
                unsafe
                {
                    fixed (uint* p = data)
                        cmd.SetComputeRoot32BitConstants(slot, (uint)data.Length, (IntPtr)p, 0);
                }
            }
        }

        public void Dispose()
        {
            PipelineState?.Dispose();
            RootSignature?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第二部分：粒子池管理（环形缓冲 + 存活/死亡列表）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子池计数器布局。描述计数器缓冲中各 uint32 槽位的语义。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ParticlePoolCounters
    {
        /// <summary>当前存活粒子数。</summary>
        public uint AliveCount;
        /// <summary>可分配（死亡）粒子数。</summary>
        public uint DeadCount;
        /// <summary>本帧发射的粒子数。</summary>
        public uint EmittedThisFrame;
        /// <summary>本帧回收的粒子数。</summary>
        public uint ReclaimedThisFrame;
        /// <summary>排序后的有效粒子数。</summary>
        public uint SortedCount;
        /// <summary>剔除后的可见粒子数。</summary>
        public uint VisibleCount;
        /// <summary>总粒子容量。</summary>
        public uint MaxParticles;
        /// <summary>帧序号。</summary>
        public uint FrameIndex;
    }

    /// <summary>
    /// GPU 粒子环形缓冲池。管理粒子数据的双缓冲（乒乓）、存活索引列表、
    /// 死亡索引栈和原子计数器。支持 lock-free 的粒子分配/回收。
    /// </summary>
    public class GPUParticlePool : IDisposable
    {
        // ── GPU 缓冲 ──
        /// <summary>粒子数据缓冲 A（当前帧读）。</summary>
        public RHIBuffer? ParticleBufferA;
        /// <summary>粒子数据缓冲 B（下一帧写，乒乓）。</summary>
        public RHIBuffer? ParticleBufferB;
        /// <summary>存活粒子索引列表。</summary>
        public RHIBuffer? AliveListBuffer;
        /// <summary>死亡粒子索引栈（环形回收）。</summary>
        public RHIBuffer? DeadListBuffer;
        /// <summary>原子计数器缓冲。</summary>
        public RHIBuffer? CounterBuffer;
        /// <summary>排序后的索引缓冲。</summary>
        public RHIBuffer? SortedIndexBuffer;
        /// <summary>排序键缓冲（距离/年龄）。</summary>
        public RHIBuffer? SortKeyBuffer;

        // ── 配置 ──
        private readonly RHIDevice _device;
        private int _maxParticles;
        private bool _useBufferA = true;
        private bool _disposed;

        /// <summary>最大粒子容量。</summary>
        public int MaxParticles => _maxParticles;
        /// <summary>当前读取缓冲（模拟阶段输入）。</summary>
        public RHIBuffer? CurrentReadBuffer => _useBufferA ? ParticleBufferA : ParticleBufferB;
        /// <summary>当前写入缓冲（模拟阶段输出）。</summary>
        public RHIBuffer? CurrentWriteBuffer => _useBufferA ? ParticleBufferB : ParticleBufferA;
        /// <summary>是否使用缓冲 A。</summary>
        public bool UseBufferA => _useBufferA;

        /// <summary>构造。</summary>
        public GPUParticlePool(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>
        /// 初始化粒子池，分配全部 GPU 缓冲资源。
        /// </summary>
        /// <param name="maxParticles">最大粒子数</param>
        public void Initialize(int maxParticles)
        {
            _maxParticles = Math.Clamp(maxParticles, 1024, 4_194_304);
            ulong stride = (ulong)Marshal.SizeOf<GPUParticle>();
            ulong particleBytes = (ulong)_maxParticles * stride;

            // 粒子数据乒乓缓冲
            var particleDesc = new RHIBufferDesc
            {
                SizeInBytes = particleBytes,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = stride
            };
            ParticleBufferA = _device.CreateBuffer(particleDesc, "ParticlePoolA");
            ParticleBufferB = _device.CreateBuffer(particleDesc, "ParticlePoolB");

            // 存活/死亡索引列表
            var idxDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)_maxParticles * 4u,
                Format = RHIFormat.R32_UInt,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess,
                Stride = 4
            };
            AliveListBuffer = _device.CreateBuffer(idxDesc, "AliveList");
            DeadListBuffer = _device.CreateBuffer(idxDesc, "DeadList");
            SortedIndexBuffer = _device.CreateBuffer(idxDesc, "SortedIndex");
            SortKeyBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)_maxParticles * sizeof(float),
                Format = RHIFormat.R32_Float,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess,
                Stride = (ulong)sizeof(float)
            }, "SortKey");

            // 计数器缓冲
            var counterDesc = new RHIBufferDesc
            {
                SizeInBytes = 256, // 预留空间
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.UnorderedAccess,
                Stride = 4
            };
            CounterBuffer = _device.CreateBuffer(counterDesc, "ParticleCounters");

            // 初始化死亡索引栈
            InitDeadStack();
        }

        /// <summary>初始化死亡索引栈（所有粒子初始为可分配）。</summary>
        private void InitDeadStack()
        {
            Span<uint> stack = new uint[_maxParticles];
            for (int i = 0; i < _maxParticles; i++)
                stack[i] = (uint)i;
            _device.UploadToBuffer(DeadListBuffer!, stack);

            Span<uint> counters = new uint[16];
            counters[0] = 0;                      // alive
            counters[1] = (uint)_maxParticles;    // dead
            counters[2] = 0;                      // emitted
            counters[3] = 0;                      // reclaimed
            counters[4] = 0;                      // sorted
            counters[5] = 0;                      // visible
            counters[6] = (uint)_maxParticles;    // max
            counters[7] = 0;                      // frame
            _device.UploadToBuffer(CounterBuffer!, counters);
        }

        /// <summary>切换乒乓缓冲（帧末调用）。</summary>
        public void SwapBuffers()
        {
            _useBufferA = !_useBufferA;
        }

        /// <summary>重置粒子池（清空所有粒子）。</summary>
        public void Reset()
        {
            InitDeadStack();
            _useBufferA = true;
        }

        /// <summary>将计数器缓冲回读到 CPU（调试用，生产环境应避免）。</summary>
        /// <remarks>
        /// 回读流程：创建 Readback 堆缓冲 → CopyResource → Map 读取。
        /// 实际实现需通过 D3D12 Readback heap 的 Map 获取数据。
        /// 此处返回默认值占位，具体 Map 实现依赖 RHI 底层。
        /// </remarks>
        public ParticlePoolCounters ReadBackCounters()
        {
            // 创建回读缓冲（首次调用时）
            // 实际实现需通过命令列表 CopyResource 到 Readback 堆缓冲，
            // 然后 Map 回读缓冲获取数据。此处返回默认值。
            return new ParticlePoolCounters
            {
                AliveCount = 0,
                DeadCount = (uint)_maxParticles,
                EmittedThisFrame = 0,
                ReclaimedThisFrame = 0,
                SortedCount = 0,
                VisibleCount = 0,
                MaxParticles = (uint)_maxParticles,
                FrameIndex = 0
            };
        }

        /// <summary>绑定粒子池 UAV 到命令列表。</summary>
        /// <param name="cmd">命令列表</param>
        /// <param name="baseSlot">基础槽位号</param>
        /// <param name="bindParticle">是否绑定粒子数据缓冲</param>
        public void BindUAVs(RHICommandList cmd, uint baseSlot, bool bindParticle = true)
        {
            if (bindParticle && CurrentWriteBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(baseSlot, CurrentWriteBuffer.GpuVirtualAddress);
            if (AliveListBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(baseSlot + 1, AliveListBuffer.GpuVirtualAddress);
            if (DeadListBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(baseSlot + 2, DeadListBuffer.GpuVirtualAddress);
            if (CounterBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(baseSlot + 3, CounterBuffer.GpuVirtualAddress);
        }

        /// <summary>转换粒子池缓冲到 UAV 状态。</summary>
        public void TransitionToUAV(RHICommandList cmd)
        {
            if (CurrentWriteBuffer != null)
                cmd.TransitionTo(CurrentWriteBuffer, RHIResourceState.UnorderedAccess);
            if (AliveListBuffer != null)
                cmd.TransitionTo(AliveListBuffer, RHIResourceState.UnorderedAccess);
            if (DeadListBuffer != null)
                cmd.TransitionTo(DeadListBuffer, RHIResourceState.UnorderedAccess);
            if (CounterBuffer != null)
                cmd.TransitionTo(CounterBuffer, RHIResourceState.UnorderedAccess);
        }

        /// <summary>转换粒子池缓冲到通用读状态（供渲染读取）。</summary>
        public void TransitionToCommon(RHICommandList cmd)
        {
            if (CurrentWriteBuffer != null)
                cmd.TransitionTo(CurrentWriteBuffer, RHIResourceState.Common);
            if (AliveListBuffer != null)
                cmd.TransitionTo(AliveListBuffer, RHIResourceState.Common);
            if (CounterBuffer != null)
                cmd.TransitionTo(CounterBuffer, RHIResourceState.Common);
        }

        public void Dispose()
        {
            if (_disposed) return;
            ParticleBufferA?.Dispose();
            ParticleBufferB?.Dispose();
            AliveListBuffer?.Dispose();
            DeadListBuffer?.Dispose();
            CounterBuffer?.Dispose();
            SortedIndexBuffer?.Dispose();
            SortKeyBuffer?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第三部分：间接绘制参数生成
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 间接绘制参数。对应 D3D12 DrawIndexedInstancedIndirect 的参数布局。
    /// 用于 GPU 端生成绘制调用参数，实现零 CPU 回读渲染。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IndirectDrawArgsData
    {
        /// <summary>每个实例的索引数（四边形=6）。</summary>
        public uint IndexCountPerInstance;
        /// <summary>实例数（=可见粒子数）。</summary>
        public uint InstanceCount;
        /// <summary>起始索引位置。</summary>
        public uint StartIndexLocation;
        /// <summary>基础顶点偏移。</summary>
        public int BaseVertexLocation;
        /// <summary>起始实例位置。</summary>
        public uint StartInstanceLocation;
    }

    /// <summary>
    /// 间接绘制参数生成器。在 GPU 计算阶段生成 IndirectDrawArgs，
    /// 供后续 ExecuteIndirect 调用直接使用。
    /// </summary>
    public class IndirectDrawGenerator : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIBuffer? _argsBuffer;
        private RHIPipelineState? _generatePSO;
        private RHIRootSignature? _rootSig;
        private bool _disposed;

        /// <summary>间接绘制参数缓冲。</summary>
        public RHIBuffer? ArgsBuffer => _argsBuffer;
        /// <summary>每实例索引数（四边形=6, 网格=mesh index count）。</summary>
        public uint IndexCountPerInstance { get; set; } = 6;
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>构造。</summary>
        public IndirectDrawGenerator(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化间接绘制参数生成器。</summary>
        public void Initialize()
        {
            // 创建间接绘制参数缓冲
            var desc = new RHIBufferDesc
            {
                SizeInBytes = 256, // 预留多组参数
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndirectArgs | RHIBufferFlags.UnorderedAccess,
                Stride = 4
            };
            _argsBuffer = _device.CreateBuffer(desc, "IndirectDrawArgs");

            CompileGenerateShader();
            IsInitialized = true;
        }

        /// <summary>编译参数生成着色器。</summary>
        private void CompileGenerateShader()
        {
            // 根签名：[0] UAV 计数器 [1] UAV 间接参数 [2] 根常量(IndexCountPerInstance)
            var rootParams = new RHIRootParameter[3];
            rootParams[0] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[1] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 1 }
            };
            rootParams[2] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.Constants,
                ShaderVisibility = RHIShaderVisibility.All,
                Constants = new RHIRootConstants { Num32BitValues = 1, ShaderRegister = 0 }
            };

            _rootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = rootParams,
                AllowInputAssembler = false
            });

            string hlsl = @"
                RWStructuredBuffer<uint> Counters : register(u0);
                RWStructuredBuffer<uint> DrawArgs : register(u1);
                cbuffer GenCB : register(b0) { uint gIndexCountPerInstance; };

                [numthreads(1,1,1)]
                void CSGenerateDrawArgs()
                {
                    uint aliveCount = Counters[0]; // 存活粒子数
                    DrawArgs[0] = gIndexCountPerInstance; // IndexCountPerInstance
                    DrawArgs[1] = aliveCount;            // InstanceCount
                    DrawArgs[2] = 0;                     // StartIndexLocation
                    DrawArgs[3] = 0;                     // BaseVertexLocation
                    DrawArgs[4] = 0;                     // StartInstanceLocation
                }
            ";

            var cs = _device.CompileShader(hlsl, "CSGenerateDrawArgs", RHIShaderStage.Compute, _device.MaxShaderModel);
            _generatePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "IndirectDrawGenPSO",
                CS = cs
            });
        }

        /// <summary>在 GPU 端生成间接绘制参数。</summary>
        /// <param name="cmd">命令列表</param>
        /// <param name="counterBuffer">粒子计数器缓冲</param>
        public void Generate(RHICommandList cmd, RHIBuffer counterBuffer)
        {
            if (!IsInitialized || _argsBuffer == null || _generatePSO == null || _rootSig == null)
                return;

            cmd.TransitionTo(_argsBuffer, RHIResourceState.UnorderedAccess);
            cmd.SetComputeRootSignature(_rootSig);
            cmd.SetPipelineState(_generatePSO);
            cmd.SetComputeRootUnorderedAccessView(0, counterBuffer.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(1, _argsBuffer.GpuVirtualAddress);

            Span<uint> ic = stackalloc uint[1] { IndexCountPerInstance };
            unsafe
            {
                fixed (uint* p = ic)
                    cmd.SetComputeRoot32BitConstants(2, 1, (IntPtr)p, 0);
            }

            cmd.Dispatch(1, 1, 1);
            cmd.TransitionTo(_argsBuffer, RHIResourceState.IndirectArgument);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _argsBuffer?.Dispose();
            _generatePSO?.Dispose();
            _rootSig?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第四部分：视锥剔除与距离剔除
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子视锥剔除器。在 GPU 计算阶段剔除视锥外和超出距离的粒子，
    /// 只将可见粒子写入紧凑的索引列表。
    /// </summary>
    public class ParticleFrustumCuller : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _cullPSO;
        private RHIRootSignature? _rootSig;
        private RHIBuffer? _visibleListBuffer;
        private RHIBuffer? _visibleCountBuffer;
        private bool _disposed;

        /// <summary>可见粒子索引列表缓冲。</summary>
        public RHIBuffer? VisibleListBuffer => _visibleListBuffer;
        /// <summary>可见粒子计数缓冲。</summary>
        public RHIBuffer? VisibleCountBuffer => _visibleCountBuffer;
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>最大剔除距离（世界单位）。超出此距离的粒子被剔除。</summary>
        public float MaxCullDistance = 500f;
        /// <summary>是否启用视锥剔除。</summary>
        public bool EnableFrustumCulling = true;
        /// <summary>是否启用距离剔除。</summary>
        public bool EnableDistanceCulling = true;

        /// <summary>构造。</summary>
        public ParticleFrustumCuller(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化剔除器。</summary>
        /// <param name="maxParticles">最大粒子数</param>
        public void Initialize(int maxParticles)
        {
            // 可见粒子索引缓冲
            var visDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)maxParticles * 4u,
                Format = RHIFormat.R32_UInt,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 4
            };
            _visibleListBuffer = _device.CreateBuffer(visDesc, "VisibleParticleList");
            _visibleCountBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 16,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.UnorderedAccess,
                Stride = 4
            }, "VisibleParticleCount");

            CompileCullShader();
            IsInitialized = true;
        }

        /// <summary>编译剔除着色器。</summary>
        private void CompileCullShader()
        {
            // 根签名：
            // [0] CBV b0 = SimParams（视图投影矩阵 + 相机位置 + 剔除参数）
            // [1] SRV t0 = 粒子数据缓冲（只读）
            // [2] SRV t1 = 存活索引列表
            // [3] UAV u0 = 可见粒子索引列表
            // [4] UAV u1 = 可见粒子计数
            var rootParams = new RHIRootParameter[5];
            rootParams[0] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.CBV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[1] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.SRV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[2] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.SRV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 1 }
            };
            rootParams[3] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[4] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 1 }
            };

            _rootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = rootParams,
                AllowInputAssembler = false
            });

            string hlsl = @"
                cbuffer CullCB : register(b0)
                {
                    matrix gViewProj;
                    float3 gCameraPos;
                    float gMaxCullDistance;
                    uint gAliveCount;
                    uint gEnableFrustum;
                    uint gEnableDistance;
                    uint gPad;
                };

                struct Particle {
                    float3 Position; float Age;
                    float3 Velocity; float Lifetime;
                    float4 Color;
                    float3 Aux; float CameraDist;
                };
                StructuredBuffer<Particle> Particles : register(t0);
                StructuredBuffer<uint> AliveList : register(t1);
                RWStructuredBuffer<uint> VisibleList : register(u0);
                RWStructuredBuffer<uint> VisibleCount : register(u1);

                [numthreads(64,1,1)]
                void CSCull(uint3 dtid : SV_DispatchThreadID)
                {
                    if (dtid.x >= gAliveCount) return;
                    uint particleIdx = AliveList[dtid.x];
                    Particle p = Particles[particleIdx];

                    // 距离剔除
                    float dist = distance(p.Position, gCameraPos);
                    if (gEnableDistance && dist > gMaxCullDistance) return;

                    // 视锥剔除：变换到裁剪空间，检查是否在 NDC 内
                    if (gEnableFrustum) {
                        float4 clip = mul(gViewProj, float4(p.Position, 1.0));
                        float w = max(clip.w, 0.0001);
                        float3 ndc = clip.xyz / w;
                        float margin = 1.0 + p.Aux.x; // 考虑粒子尺寸的边距
                        if (ndc.x < -margin || ndc.x > margin) return;
                        if (ndc.y < -margin || ndc.y > margin) return;
                        if (ndc.z < 0.0 || ndc.z > 1.0) return;
                    }

                    // 原子追加到可见列表
                    uint slot;
                    InterlockedAdd(VisibleCount[0], 1, slot);
                    VisibleList[slot] = particleIdx;
                }
            ";

            var cs = _device.CompileShader(hlsl, "CSCull", RHIShaderStage.Compute, _device.MaxShaderModel);
            _cullPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ParticleCullPSO",
                CS = cs
            });
        }

        /// <summary>执行视锥剔除。</summary>
        /// <param name="cmd">命令列表</param>
        /// <param name="particleBuffer">粒子数据缓冲（SRV）</param>
        /// <param name="aliveListBuffer">存活索引列表</param>
        /// <param name="simParamsCB">模拟参数常量缓冲</param>
        /// <param name="aliveCount">存活粒子数</param>
        public void Cull(RHICommandList cmd, RHIBuffer particleBuffer, RHIBuffer aliveListBuffer,
                         RHIBuffer simParamsCB, int aliveCount)
        {
            if (!IsInitialized || _cullPSO == null || _rootSig == null) return;

            // 清零可见计数
            Span<uint> zero = stackalloc uint[4] { 0, 0, 0, 0 };
            _device.UploadToBuffer(_visibleCountBuffer!, zero);

            cmd.TransitionTo(_visibleListBuffer!, RHIResourceState.UnorderedAccess);
            cmd.TransitionTo(_visibleCountBuffer!, RHIResourceState.UnorderedAccess);

            cmd.SetComputeRootSignature(_rootSig);
            cmd.SetPipelineState(_cullPSO);
            cmd.SetComputeRootConstantBufferView(0, simParamsCB.GpuVirtualAddress);
            cmd.SetComputeRootShaderResourceView(1, particleBuffer.GpuVirtualAddress);
            cmd.SetComputeRootShaderResourceView(2, aliveListBuffer.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(3, _visibleListBuffer!.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(4, _visibleCountBuffer!.GpuVirtualAddress);

            int groups = (aliveCount + 63) / 64;
            cmd.Dispatch(Math.Max(1, groups), 1, 1);

            cmd.TransitionTo(_visibleListBuffer!, RHIResourceState.Common);
            cmd.TransitionTo(_visibleCountBuffer!, RHIResourceState.Common);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _visibleListBuffer?.Dispose();
            _visibleCountBuffer?.Dispose();
            _cullPSO?.Dispose();
            _rootSig?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第五部分：GPU 曲线采样器
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU 曲线采样器。将 CPU 端的 ParticleCurve / ColorCurve 烘焙为 GPU 缓冲，
    /// 供计算着色器在模拟阶段采样（大小/颜色/速度随生命周期变化）。
    /// </summary>
    public class GPUCurveSampler : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<string, RHIBuffer> _curveBuffers = new();
        private readonly Dictionary<string, RHIDescriptorHandle> _curveSRVs = new();
        private const int DefaultSamples = 256;
        private bool _disposed;

        /// <summary>采样精度（烘焙采样数）。</summary>
        public int SampleCount { get; set; } = DefaultSamples;

        /// <summary>构造。</summary>
        public GPUCurveSampler(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>注册标量曲线并上传到 GPU。</summary>
        /// <param name="name">曲线名称（着色器中通过名称查找）</param>
        /// <param name="curve">粒子曲线</param>
        public void RegisterScalarCurve(string name, ParticleCurve curve)
        {
            float[] baked = new float[SampleCount];
            for (int i = 0; i < SampleCount; i++)
            {
                float t = (float)i / (SampleCount - 1);
                baked[i] = curve.Evaluate(t, 0.5f);
            }

            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)SampleCount * sizeof(float),
                Format = RHIFormat.R32_Float,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)sizeof(float)
            };
            var buf = _device.CreateBuffer(desc, $"Curve_{name}");
            _device.UploadToBuffer(buf, baked);
            _curveBuffers[name] = buf;
            _curveSRVs[name] = _device.CreateSRV(buf, RHIFormat.R32_Float, true, 0, SampleCount, 4);
        }

        /// <summary>注册颜色曲线并上传到 GPU。</summary>
        /// <param name="name">曲线名称</param>
        /// <param name="curve">颜色曲线</param>
        public void RegisterColorCurve(string name, ColorCurve curve)
        {
            Vector4[] baked = new Vector4[SampleCount];
            for (int i = 0; i < SampleCount; i++)
            {
                float t = (float)i / (SampleCount - 1);
                baked[i] = curve.Evaluate(t, 0.5f);
            }

            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)SampleCount * 16u,
                Format = RHIFormat.R32G32B32A32_Float,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 16
            };
            var buf = _device.CreateBuffer(desc, $"ColorCurve_{name}");
            _device.UploadToBuffer(buf, baked);
            _curveBuffers[name] = buf;
            _curveSRVs[name] = _device.CreateSRV(buf, RHIFormat.R32G32B32A32_Float, true, 0, SampleCount, 16);
        }

        /// <summary>注册双分量曲线（如 Size XY）并上传到 GPU。</summary>
        public void RegisterVector2Curve(string name, ParticleCurve curveX, ParticleCurve curveY)
        {
            Vector2[] baked = new Vector2[SampleCount];
            for (int i = 0; i < SampleCount; i++)
            {
                float t = (float)i / (SampleCount - 1);
                baked[i] = new Vector2(
                    curveX.Evaluate(t, 0.5f),
                    curveY.Evaluate(t, 0.5f));
            }

            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)SampleCount * 8u,
                Format = RHIFormat.R32G32_Float,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 8
            };
            var buf = _device.CreateBuffer(desc, $"Vec2Curve_{name}");
            _device.UploadToBuffer(buf, baked);
            _curveBuffers[name] = buf;
            _curveSRVs[name] = _device.CreateSRV(buf, RHIFormat.R32G32_Float, true, 0, SampleCount, 8);
        }

        /// <summary>获取曲线的 SRV 句柄（供着色器绑定）。</summary>
        public RHIDescriptorHandle GetSRV(string name)
        {
            return _curveSRVs.TryGetValue(name, out var handle) ? handle : default;
        }

        /// <summary>获取曲线的 GPU 缓冲。</summary>
        public RHIBuffer? GetBuffer(string name)
        {
            return _curveBuffers.TryGetValue(name, out var buf) ? buf : null;
        }

        /// <summary>生成着色器端的曲线采样 HLSL 代码片段。</summary>
        public static string GenerateSamplerHLSL()
        {
            return @"
                // ── GPU 曲线采样器 ──
                // 标量曲线采样（线性插值）
                float SampleScalarCurve(StructuredBuffer<float> curve, float t)
                {
                    t = saturate(t);
                    float f = t * (CURVE_SAMPLES - 1);
                    uint i0 = (uint)f;
                    uint i1 = min(i0 + 1, CURVE_SAMPLES - 1);
                    float k = f - (float)i0;
                    return lerp(curve[i0], curve[i1], k);
                }
                // 颜色曲线采样
                float4 SampleColorCurve(StructuredBuffer<float4> curve, float t)
                {
                    t = saturate(t);
                    float f = t * (CURVE_SAMPLES - 1);
                    uint i0 = (uint)f;
                    uint i1 = min(i0 + 1, CURVE_SAMPLES - 1);
                    float k = f - (float)i0;
                    return lerp(curve[i0], curve[i1], k);
                }
                // Vector2 曲线采样
                float2 SampleVec2Curve(StructuredBuffer<float2> curve, float t)
                {
                    t = saturate(t);
                    float f = t * (CURVE_SAMPLES - 1);
                    uint i0 = (uint)f;
                    uint i1 = min(i0 + 1, CURVE_SAMPLES - 1);
                    float k = f - (float)i0;
                    return lerp(curve[i0], curve[i1], k);
                }
            ";
        }

        public void Dispose()
        {
            if (_disposed) return;
            foreach (var (_, buf) in _curveBuffers)
                buf.Dispose();
            _curveBuffers.Clear();
            _curveSRVs.Clear();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第六部分：力场集成器
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU 力场集成器。管理力场数据缓冲，提供力场计算着色器代码，
    /// 在模拟阶段将重力/风力/湍流/吸引/排斥/涡旋/阻力集成到粒子速度。
    /// </summary>
    public class GPUForceFieldIntegrator : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIBuffer? _forceFieldBuffer;
        private readonly List<GPUForceFieldData> _forceFields = new();
        private const int MaxForceFields = 64;
        private bool _disposed;

        /// <summary>力场缓冲。</summary>
        public RHIBuffer? ForceFieldBuffer => _forceFieldBuffer;
        /// <summary>力场数量。</summary>
        public int Count => _forceFields.Count;
        /// <summary>最大力场数。</summary>
        public int MaxCount => MaxForceFields;

        /// <summary>构造。</summary>
        public GPUForceFieldIntegrator(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化力场缓冲。</summary>
        public void Initialize()
        {
            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)Marshal.SizeOf<GPUForceFieldData>() * MaxForceFields,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<GPUForceFieldData>()
            };
            _forceFieldBuffer = _device.CreateBuffer(desc, "ForceFieldBuffer");
        }

        /// <summary>添加重力场。</summary>
        /// <param name="direction">重力方向（如 (0,-9.81,0)）</param>
        /// <param name="strength">强度系数</param>
        public void AddGravity(Vector3 direction, float strength = 1f)
        {
            AddForce(new GPUForceFieldData
            {
                Position = Vector3.Zero,
                Strength = strength,
                Direction = direction,
                Radius = float.MaxValue,
                FieldType = (uint)GPUForceFieldType.Gravity,
                Falloff = 0f,
                Tightness = 0f
            });
        }

        /// <summary>添加风力场。</summary>
        /// <param name="direction">风向</param>
        /// <param name="strength">风力强度</param>
        /// <param name="gustFrequency">阵风频率</param>
        public void AddWind(Vector3 direction, float strength, float gustFrequency = 0.5f)
        {
            AddForce(new GPUForceFieldData
            {
                Position = Vector3.Zero,
                Strength = strength,
                Direction = direction,
                Radius = float.MaxValue,
                FieldType = (uint)GPUForceFieldType.Wind,
                Falloff = 0f,
                Tightness = gustFrequency
            });
        }

        /// <summary>添加 Curl Noise 湍流场。</summary>
        /// <param name="strength">湍流强度</param>
        /// <param name="frequency">噪声频率</param>
        /// <param name="speed">时间演化速度</param>
        public void AddCurlNoise(float strength, float frequency, float speed)
        {
            AddForce(new GPUForceFieldData
            {
                Position = Vector3.Zero,
                Strength = strength,
                Direction = Vector3.Zero,
                Radius = float.MaxValue,
                FieldType = (uint)GPUForceFieldType.CurlNoise,
                Falloff = 0f,
                Tightness = frequency
            });
        }

        /// <summary>添加吸引力场。</summary>
        /// <param name="center">吸引中心位置</param>
        /// <param name="strength">吸引强度</param>
        /// <param name="radius">影响半径</param>
        /// <param name="falloff">衰减指数（1=线性, 2=平方反比）</param>
        public void AddAttractor(Vector3 center, float strength, float radius, float falloff = 2f)
        {
            AddForce(new GPUForceFieldData
            {
                Position = center,
                Strength = strength,
                Direction = Vector3.Zero,
                Radius = radius,
                FieldType = (uint)GPUForceFieldType.Attractor,
                Falloff = falloff,
                Tightness = 0f
            });
        }

        /// <summary>添加排斥力场。</summary>
        /// <param name="center">排斥中心位置</param>
        /// <param name="strength">排斥强度</param>
        /// <param name="radius">影响半径</param>
        /// <param name="falloff">衰减指数</param>
        public void AddRepeller(Vector3 center, float strength, float radius, float falloff = 2f)
        {
            AddForce(new GPUForceFieldData
            {
                Position = center,
                Strength = -strength, // 负强度 = 排斥
                Direction = Vector3.Zero,
                Radius = radius,
                FieldType = (uint)GPUForceFieldType.Attractor,
                Falloff = falloff,
                Tightness = 0f
            });
        }

        /// <summary>添加涡旋力场。</summary>
        /// <param name="center">涡旋中心</param>
        /// <param name="axis">旋转轴</param>
        /// <param name="strength">涡旋强度</param>
        /// <param name="radius">影响半径</param>
        public void AddVortex(Vector3 center, Vector3 axis, float strength, float radius)
        {
            AddForce(new GPUForceFieldData
            {
                Position = center,
                Strength = strength,
                Direction = axis,
                Radius = radius,
                FieldType = (uint)GPUForceFieldType.Vortex,
                Falloff = 1f,
                Tightness = 0f
            });
        }

        /// <summary>添加阻力场。</summary>
        /// <param name="strength">阻力系数</param>
        public void AddDrag(float strength)
        {
            AddForce(new GPUForceFieldData
            {
                Position = Vector3.Zero,
                Strength = strength,
                Direction = Vector3.Zero,
                Radius = float.MaxValue,
                FieldType = (uint)GPUForceFieldType.Drag,
                Falloff = 0f,
                Tightness = 0f
            });
        }

        /// <summary>添加径向力场（从中心向外或向内）。</summary>
        /// <param name="center">中心位置</param>
        /// <param name="strength">强度（正=向外, 负=向内）</param>
        /// <param name="radius">影响半径</param>
        public void AddRadial(Vector3 center, float strength, float radius)
        {
            AddForce(new GPUForceFieldData
            {
                Position = center,
                Strength = strength,
                Direction = Vector3.Zero,
                Radius = radius,
                FieldType = (uint)GPUForceFieldType.Radial,
                Falloff = 1f,
                Tightness = 0f
            });
        }

        /// <summary>添加力场并上传到 GPU。</summary>
        public void AddForce(GPUForceFieldData ff)
        {
            if (_forceFields.Count >= MaxForceFields) return;
            _forceFields.Add(ff);
            UploadToGPU();
        }

        /// <summary>移除指定索引的力场。</summary>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _forceFields.Count) return;
            _forceFields.RemoveAt(index);
            UploadToGPU();
        }

        /// <summary>清空所有力场。</summary>
        public void Clear()
        {
            _forceFields.Clear();
            UploadToGPU();
        }

        /// <summary>上传力场数据到 GPU。</summary>
        private void UploadToGPU()
        {
            if (_forceFieldBuffer == null || _forceFields.Count == 0) return;
            _device.UploadToBuffer(_forceFieldBuffer, _forceFields.ToArray());
        }

        /// <summary>生成力场集成的 HLSL 代码片段。</summary>
        public static string GenerateIntegrationHLSL()
        {
            return @"
                // ── GPU 力场集成 ──
                // 力场数据结构
                struct ForceField {
                    float3 Position; float Strength;
                    float3 Direction; float Radius;
                    uint FieldType; float Falloff; float Tightness; float Pad;
                };

                // Curl Noise 近似（无散度矢量场）
                float3 CurlNoise(float3 p, float t)
                {
                    float x = sin(p.y * 1.3 + t) - cos(p.z * 1.1 + t * 0.7);
                    float y = sin(p.z * 1.2 + t * 0.8) - cos(p.x * 1.4 + t * 1.1);
                    float z = sin(p.x * 1.1 + t * 1.2) - cos(p.y * 1.3 + t * 0.9);
                    return float3(x, y, z);
                }

                // 计算单个力场对粒子的贡献
                float3 ComputeForce(ForceField ff, float3 pos, float3 vel, float mass, float time)
                {
                    float3 force = float3(0, 0, 0);
                    switch (ff.FieldType) {
                        case 0: // Gravity
                            force = ff.Direction * ff.Strength * mass;
                            break;
                        case 1: // Wind
                            force = ff.Direction * ff.Strength * (1.0 + 0.3 * sin(time * ff.Tightness * 6.28));
                            break;
                        case 2: // CurlNoise
                            force = CurlNoise(pos * ff.Tightness, time) * ff.Strength;
                            break;
                        case 3: { // Attractor
                            float3 d = ff.Position - pos;
                            float dist = max(length(d), 1e-4);
                            float normDist = saturate(dist / ff.Radius);
                            float falloffFactor = 1.0 - pow(normDist, ff.Falloff);
                            float sign = ff.Strength >= 0 ? 1.0 : -1.0;
                            force = normalize(d) * abs(ff.Strength) * falloffFactor * sign / max(1.0, dist * 0.1);
                            break;
                        }
                        case 5: { // Vortex
                            float3 toCenter = pos - ff.Position;
                            float3 axis = normalize(ff.Direction);
                            float3 radial = normalize(toCenter);
                            float3 tangent = cross(axis, radial);
                            float dist = length(toCenter);
                            float falloffFactor = 1.0 - saturate(dist / ff.Radius);
                            force = tangent * ff.Strength * falloffFactor;
                            break;
                        }
                        case 6: // Drag
                            force = -vel * ff.Strength;
                            break;
                        case 7: { // Radial
                            float3 d = pos - ff.Position;
                            float dist = max(length(d), 1e-4);
                            float falloffFactor = 1.0 - saturate(dist / ff.Radius);
                            force = normalize(d) * ff.Strength * falloffFactor;
                            break;
                        }
                    }
                    return force;
                }

                // 集成所有力场到粒子加速度
                float3 IntegrateForceFields(StructuredBuffer<ForceField> forceFields, uint count,
                                            float3 pos, float3 vel, float mass, float time)
                {
                    float3 totalForce = float3(0, 0, 0);
                    for (uint i = 0; i < count; i++) {
                        totalForce += ComputeForce(forceFields[i], pos, vel, mass, time);
                    }
                    return totalForce / max(mass, 0.0001);
                }
            ";
        }

        /// <summary>获取力场列表（只读）。</summary>
        public IReadOnlyList<GPUForceFieldData> ForceFields => _forceFields;

        public void Dispose()
        {
            if (_disposed) return;
            _forceFieldBuffer?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第七部分：距离排序器
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU 粒子距离排序器。对存活粒子按相机距离排序，支持升序/降序。
    /// 采用 Bitonic Sort 网络，完全 GPU 并行，无 CPU 回读。
    /// 与已有的 <see cref="BitonicSorter"/> 互补，提供更灵活的排序接口。
    /// </summary>
    public class ParticleDistanceSorter : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _computeDistPSO;
        private RHIPipelineState? _sortPSO;
        private RHIPipelineState? _initPSO;
        private RHIRootSignature? _rootSig;
        private int _capacity;
        private bool _disposed;

        /// <summary>排序容量（2 的幂）。</summary>
        public int Capacity => _capacity;
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>构造。</summary>
        public ParticleDistanceSorter(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化排序器。</summary>
        /// <param name="maxParticles">最大粒子数</param>
        public void Initialize(int maxParticles)
        {
            _capacity = NextPow2(maxParticles);
            CompileShaders();
            IsInitialized = true;
        }

        /// <summary>编译排序着色器。</summary>
        private void CompileShaders()
        {
            var rootParams = new RHIRootParameter[4];
            rootParams[0] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.Constants,
                ShaderVisibility = RHIShaderVisibility.All,
                Constants = new RHIRootConstants { Num32BitValues = 4, ShaderRegister = 0 }
            };
            rootParams[1] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[2] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 1 }
            };
            rootParams[3] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 2 }
            };

            _rootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = rootParams,
                AllowInputAssembler = false
            });

            string hlsl = @"
                RWStructuredBuffer<float> Keys : register(u0);
                RWStructuredBuffer<uint> Indices : register(u1);
                RWStructuredBuffer<uint> Counters : register(u2);
                cbuffer SortCB : register(b0) {
                    uint gSize; uint gStride; uint gDir; uint gParticleCount;
                };

                // 初始化：计算每个粒子的排序键（距离），死粒子置极大值
                [numthreads(64,1,1)]
                void CSInitSort(uint3 dtid : SV_DispatchThreadID)
                {
                    uint i = dtid.x;
                    if (i >= gSize) return;
                    if (i < gParticleCount) {
                        // 键由外部计算后写入 Keys，此处仅初始化索引
                        Indices[i] = i;
                    } else {
                        Keys[i] = 1e30;
                        Indices[i] = i;
                    }
                }

                // Bitonic 排序一步
                [numthreads(64,1,1)]
                void CSSortStep(uint3 dtid : SV_DispatchThreadID)
                {
                    uint i = dtid.x;
                    if (i >= gSize) return;
                    uint j = i ^ (gStride >> 1);
                    if (j > i) {
                        float ki = Keys[i];
                        float kj = Keys[j];
                        bool swap = (gDir == 1u) ? (ki > kj) : (ki < kj);
                        if (swap) {
                            Keys[i] = kj; Keys[j] = ki;
                            uint ti = Indices[i]; Indices[i] = Indices[j]; Indices[j] = ti;
                        }
                    }
                }
            ";

            _initPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "SortInitPSO",
                CS = _device.CompileShader(hlsl, "CSInitSort", RHIShaderStage.Compute, _device.MaxShaderModel)
            });
            _sortPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "SortStepPSO",
                CS = _device.CompileShader(hlsl, "CSSortStep", RHIShaderStage.Compute, _device.MaxShaderModel)
            });
        }

        /// <summary>执行距离排序。</summary>
        /// <param name="cmd">命令列表</param>
        /// <param name="keyBuffer">排序键缓冲（距离平方）</param>
        /// <param name="indexBuffer">粒子索引缓冲</param>
        /// <param name="counterBuffer">计数器缓冲</param>
        /// <param name="particleCount">实际粒子数</param>
        /// <param name="ascending">true=升序（近→远），false=降序（远→近）</param>
        public void Sort(RHICommandList cmd, RHIBuffer keyBuffer, RHIBuffer indexBuffer,
                         RHIBuffer counterBuffer, int particleCount, bool ascending = false)
        {
            if (!IsInitialized || _sortPSO == null || _rootSig == null) return;

            int n = NextPow2(particleCount);

            cmd.SetComputeRootSignature(_rootSig);

            // 1) 初始化索引和键
            cmd.SetPipelineState(_initPSO!);
            cmd.SetComputeRootUnorderedAccessView(1, keyBuffer.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(2, indexBuffer.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(3, counterBuffer.GpuVirtualAddress);

            Span<uint> initCB = stackalloc uint[4] { (uint)n, 0, 0, (uint)particleCount };
            unsafe
            {
                fixed (uint* p = initCB)
                    cmd.SetComputeRoot32BitConstants(0, 4, (IntPtr)p, 0);
            }
            int initGroups = (n + 63) / 64;
            cmd.Dispatch(Math.Max(1, initGroups), 1, 1);

            // 2) Bitonic 排序网络
            cmd.SetPipelineState(_sortPSO!);
            for (int size = 2; size <= n; size *= 2)
            {
                for (int stride = size; stride > 1; stride /= 2)
                {
                    uint dir = (uint)((ascending ? 1 : 0) ^ (((size & n) != 0) ? 1 : 0));
                    Span<uint> cb = stackalloc uint[4] { (uint)n, (uint)stride, dir, 0 };
                    unsafe
                    {
                        fixed (uint* p = cb)
                            cmd.SetComputeRoot32BitConstants(0, 4, (IntPtr)p, 0);
                    }
                    cmd.Dispatch(Math.Max(1, initGroups), 1, 1);
                }
            }
        }

        private static int NextPow2(int v)
        {
            uint x = (uint)v; x--; x |= x >> 1; x |= x >> 2; x |= x >> 4;
            x |= x >> 8; x |= x >> 16; x++;
            return (int)Math.Max(x, 2);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _computeDistPSO?.Dispose();
            _sortPSO?.Dispose();
            _initPSO?.Dispose();
            _rootSig?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第八部分：GPU 粒子计算管线主控
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU 粒子计算管线主控。将粒子池、发射/模拟/剔除内核、排序器、
    /// 曲线采样器、力场集成器、间接绘制生成器组合为完整的 GPU 计算管线。
    /// 每帧执行：Emit → Simulate → Cull → Sort → GenerateIndirectArgs。
    /// </summary>
    public class GPUParticleComputePipeline : IDisposable
    {
        private readonly RHIDevice _device;
        private RHICommandQueue? _computeQueue;
        private RHICommandList? _computeCmd;

        // ── 管线组件 ──
        private GPUParticlePool? _pool;
        private GPUComputeKernel? _emitKernel;
        private GPUComputeKernel? _simulateKernel;
        private GPUComputeKernel? _cullKernel;
        private ParticleFrustumCuller? _culler;
        private ParticleDistanceSorter? _sorter;
        private GPUCurveSampler? _curveSampler;
        private GPUForceFieldIntegrator? _forceIntegrator;
        private IndirectDrawGenerator? _indirectDrawGen;

        // ── 常量缓冲 ──
        private RHIBuffer? _simParamsCB;
        private RHIBuffer? _emitterParamsCB;

        // ── 状态 ──
        private float _systemAge;
        private bool _disposed;

        /// <summary>粒子池。</summary>
        public GPUParticlePool? Pool => _pool;
        /// <summary>视锥剔除器。</summary>
        public ParticleFrustumCuller? Culler => _culler;
        /// <summary>距离排序器。</summary>
        public ParticleDistanceSorter? Sorter => _sorter;
        /// <summary>曲线采样器。</summary>
        public GPUCurveSampler? CurveSampler => _curveSampler;
        /// <summary>力场集成器。</summary>
        public GPUForceFieldIntegrator? ForceIntegrator => _forceIntegrator;
        /// <summary>间接绘制生成器。</summary>
        public IndirectDrawGenerator? IndirectDrawGen => _indirectDrawGen;
        /// <summary>发射内核。</summary>
        public GPUComputeKernel? EmitKernel => _emitKernel;
        /// <summary>模拟内核。</summary>
        public GPUComputeKernel? SimulateKernel => _simulateKernel;
        /// <summary>剔除内核。</summary>
        public GPUComputeKernel? CullKernel => _cullKernel;
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>构造。</summary>
        public GPUParticleComputePipeline(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>
        /// 初始化完整计算管线。
        /// </summary>
        /// <param name="maxParticles">最大粒子数</param>
        /// <param name="compileShaders">是否编译着色器（false=仅分配资源）</param>
        public void Initialize(int maxParticles, bool compileShaders = true)
        {
            // 创建计算队列和命令列表
            _computeQueue = _device.GetCommandQueue(RHIQueueType.Compute);
            _computeCmd = _device.CreateCommandList(RHIQueueType.Compute, "ParticleComputePipelineCmd");

            // 初始化粒子池
            _pool = new GPUParticlePool(_device);
            _pool.Initialize(maxParticles);

            // 初始化常量缓冲
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = 256,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = 256
            };
            _simParamsCB = _device.CreateBuffer(cbDesc, "ComputePipelineSimParamsCB");
            _emitterParamsCB = _device.CreateBuffer(cbDesc, "ComputePipelineEmitterParamsCB");

            // 初始化各组件
            _culler = new ParticleFrustumCuller(_device);
            _culler.Initialize(maxParticles);

            _sorter = new ParticleDistanceSorter(_device);
            _sorter.Initialize(maxParticles);

            _curveSampler = new GPUCurveSampler(_device);
            _forceIntegrator = new GPUForceFieldIntegrator(_device);
            _forceIntegrator.Initialize();

            _indirectDrawGen = new IndirectDrawGenerator(_device);
            _indirectDrawGen.Initialize();

            if (compileShaders)
            {
                CompileKernels(maxParticles);
            }

            IsInitialized = true;
        }

        /// <summary>编译计算着色器内核。</summary>
        private void CompileKernels(int maxParticles)
        {
            // 创建共享根签名
            var rootParams = new RHIRootParameter[10];
            rootParams[0] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.CBV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[1] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.CBV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 1 }
            };
            for (int i = 0; i < 5; i++)
                rootParams[2 + i] = new RHIRootParameter
                {
                    ParameterType = RHIRootParameterType.UAV,
                    ShaderVisibility = RHIShaderVisibility.All,
                    Descriptor = new RHIRootDescriptor { ShaderRegister = (uint)i }
                };
            rootParams[7] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.SRV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[8] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.SRV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 1 }
            };
            rootParams[9] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.SRV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 2 }
            };

            var rootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = rootParams,
                AllowInputAssembler = false
            });

            // 发射内核
            _emitKernel = new GPUComputeKernel("ParticleEmitCS", GPUComputeShaderStage.Emit);
            _emitKernel.SetRootSignature(rootSig);
            string emitHlsl = @"
                RWStructuredBuffer<Particle> Particles : register(u0);
                RWStructuredBuffer<uint> AliveList : register(u1);
                RWStructuredBuffer<uint> DeadStack : register(u2);
                RWStructuredBuffer<uint> Counters : register(u3);
                cbuffer SimCB : register(b0) { float gDeltaTime; float gTime; uint gMaxParticles; uint gFrame; };
                cbuffer EmitCB : register(b1) {
                    float3 gEmitPos; float gEmitRate;
                    float3 gEmitDir; float gLifetime;
                    float3 gVel; float gSpeed;
                    float4 gColor; float2 gSize; float2 gPad;
                };

                [numthreads(64,1,1)]
                void CSEmit(uint3 dtid : SV_DispatchThreadID)
                {
                    uint emitCount = (uint)(gEmitRate * gDeltaTime);
                    if (dtid.x >= emitCount) return;

                    uint deadCount;
                    InterlockedAdd(Counters[1], -1, deadCount);
                    if ((int)deadCount <= 0) { InterlockedAdd(Counters[1], 1); return; }

                    uint idx = DeadStack[deadCount - 1];
                    Particles[idx].Position = gEmitPos;
                    Particles[idx].Velocity = gVel * gSpeed;
                    Particles[idx].Age = 0.0;
                    Particles[idx].Lifetime = gLifetime;
                    Particles[idx].Color = gColor;
                    Particles[idx].Aux = float3(gSize.x, gSize.y, 0);

                    uint alive;
                    InterlockedAdd(Counters[0], 1, alive);
                    AliveList[alive] = idx;
                }
            " + GPUParticleHLSLDefinitions;
            _emitKernel.Compile(_device, emitHlsl, "CSEmit");

            // 模拟内核
            _simulateKernel = new GPUComputeKernel("ParticleSimulateCS", GPUComputeShaderStage.Simulate);
            _simulateKernel.SetRootSignature(rootSig);
            string simHlsl = @"
                RWStructuredBuffer<Particle> Particles : register(u0);
                RWStructuredBuffer<uint> AliveList : register(u1);
                RWStructuredBuffer<uint> DeadStack : register(u2);
                RWStructuredBuffer<uint> Counters : register(u3);
                StructuredBuffer<ForceField> ForceFields : register(t0);
                cbuffer SimCB : register(b0) {
                    float4x4 gViewProj; float3 gCameraPos; float gDeltaTime;
                    float gTime; uint gMaxParticles; uint gNumForceFields; uint gFrame;
                };
                cbuffer EmitCB : register(b1) {
                    float3 gGravity; float gMass; float gDrag; float3 gPad;
                };

                [numthreads(64,1,1)]
                void CSSimulate(uint3 dtid : SV_DispatchThreadID)
                {
                    uint idx = dtid.x;
                    if (idx >= gMaxParticles) return;
                    Particle p = Particles[idx];
                    if (p.Color.a <= 0.0) return;

                    p.Age += gDeltaTime;
                    float t = saturate(p.Age / max(p.Lifetime, 0.0001));

                    if (t >= 1.0) {
                        uint dead;
                        InterlockedAdd(Counters[1], 1, dead);
                        DeadStack[dead] = idx;
                        p.Color.a = 0;
                        Particles[idx] = p;
                        return;
                    }

                    // 力场集成
                    float3 force = gGravity * gMass;
                    for (uint fi = 0; fi < gNumForceFields; fi++) {
                        force += ComputeForce(ForceFields[fi], p.Position, p.Velocity, gMass, gTime);
                    }

                    p.Velocity += force * gDeltaTime / max(gMass, 0.0001);
                    p.Velocity *= (1.0 - gDrag * gDeltaTime);
                    p.Position += p.Velocity * gDeltaTime;
                    p.CameraDist = distanceSquared(p.Position, gCameraPos);

                    Particles[idx] = p;
                }
            " + GPUParticleHLSLDefinitions + GPUForceFieldIntegrator.GenerateIntegrationHLSL();
            _simulateKernel.Compile(_device, simHlsl, "CSSimulate");

            // 剔除/紧致内核
            _cullKernel = new GPUComputeKernel("ParticleCullCS", GPUComputeShaderStage.Cull);
            _cullKernel.SetRootSignature(rootSig);
            string cullHlsl = @"
                RWStructuredBuffer<Particle> Particles : register(u0);
                RWStructuredBuffer<uint> AliveList : register(u1);
                RWStructuredBuffer<uint> DeadStack : register(u2);
                RWStructuredBuffer<uint> Counters : register(u3);
                cbuffer SimCB : register(b0) {
                    float4x4 gViewProj; float3 gCameraPos; float gDeltaTime;
                    float gTime; uint gMaxParticles; uint gNumForceFields; uint gFrame;
                };

                [numthreads(64,1,1)]
                void CSCompact(uint3 dtid : SV_DispatchThreadID)
                {
                    uint idx = dtid.x;
                    if (idx >= gMaxParticles) return;
                    if (Particles[idx].Color.a > 0.0) {
                        uint slot;
                        InterlockedAdd(Counters[4], 1, slot);
                        AliveList[slot] = idx;
                    }
                }
            " + GPUParticleHLSLDefinitions;
            _cullKernel.Compile(_device, cullHlsl, "CSCompact");
        }

        /// <summary>HLSL 中 Particle 结构与 ForceField 结构的定义。</summary>
        private const string GPUParticleHLSLDefinitions = @"
            struct Particle {
                float3 Position; float Age;
                float3 Velocity; float Lifetime;
                float4 Color;
                float3 Aux; float CameraDist;
            };
        ";

        /// <summary>
        /// 每帧更新计算管线。执行 Emit → Simulate → Cull → Sort → GenerateIndirectArgs。
        /// </summary>
        /// <param name="dt">帧步长</param>
        /// <param name="cameraPos">相机位置</param>
        /// <param name="viewProj">视图投影矩阵</param>
        /// <param name="emitterParams">发射器参数</param>
        public void Update(float dt, Vector3 cameraPos, Matrix4x4 viewProj, GPUEmitterParams emitterParams)
        {
            if (!IsInitialized || _computeCmd == null || _pool == null) return;
            _systemAge += dt;

            // 上传模拟参数
            var simParams = new GPUSimParams
            {
                ViewMatrix = viewProj,
                ProjMatrix = viewProj,
                InvViewProjMatrix = Matrix4x4.Identity,
                CameraPosition = cameraPos,
                DeltaTime = dt,
                CameraRight = Vector3.UnitX,
                ElapsedTime = _systemAge,
                CameraUp = Vector3.UnitY,
                FrameIndex = (uint)DateTime.UtcNow.Ticks,
                MaxParticles = (uint)_pool.MaxParticles,
                NumForceFields = (uint)(_forceIntegrator?.Count ?? 0),
                NumColliders = 0
            };
            _device.UploadToBuffer(_simParamsCB!, new[] { simParams });
            _device.UploadToBuffer(_emitterParamsCB!, new[] { emitterParams });

            _computeCmd.Open();

            // 转换粒子池到 UAV 状态
            _pool.TransitionToUAV(_computeCmd);

            // 1) 发射阶段
            if (_emitKernel != null)
            {
                var resources = new GPUComputeResourceSet();
                resources.SetCBV(0, _simParamsCB!);
                resources.SetCBV(1, _emitterParamsCB!);
                if (_pool.CurrentWriteBuffer != null) resources.SetUAV(0, _pool.CurrentWriteBuffer);
                if (_pool.AliveListBuffer != null) resources.SetUAV(1, _pool.AliveListBuffer);
                if (_pool.DeadListBuffer != null) resources.SetUAV(2, _pool.DeadListBuffer);
                if (_pool.CounterBuffer != null) resources.SetUAV(3, _pool.CounterBuffer);
                _emitKernel.BindResources(_computeCmd, resources);
                int emitCount = (int)(emitterParams.EmitRate * dt);
                _emitKernel.Dispatch(_computeCmd, Math.Max(64, emitCount));
            }

            // 2) 模拟阶段
            if (_simulateKernel != null)
            {
                var resources = new GPUComputeResourceSet();
                resources.SetCBV(0, _simParamsCB!);
                resources.SetCBV(1, _emitterParamsCB!);
                if (_pool.CurrentWriteBuffer != null) resources.SetUAV(0, _pool.CurrentWriteBuffer);
                if (_pool.AliveListBuffer != null) resources.SetUAV(1, _pool.AliveListBuffer);
                if (_pool.DeadListBuffer != null) resources.SetUAV(2, _pool.DeadListBuffer);
                if (_pool.CounterBuffer != null) resources.SetUAV(3, _pool.CounterBuffer);
                if (_forceIntegrator?.ForceFieldBuffer != null)
                    resources.SetSRV(0, _forceIntegrator.ForceFieldBuffer);
                _simulateKernel.BindResources(_computeCmd, resources);
                _simulateKernel.Dispatch(_computeCmd, _pool.MaxParticles);
            }

            // 3) 剔除/紧致阶段：重建存活索引列表
            if (_cullKernel != null)
            {
                // 重置存活计数
                _computeCmd.SetComputeRootSignature(_cullKernel.RootSignature!);
                _computeCmd.SetPipelineState(_cullKernel.PipelineState!);
                if (_pool.CurrentWriteBuffer != null)
                    _computeCmd.SetComputeRootUnorderedAccessView(0, _pool.CurrentWriteBuffer.GpuVirtualAddress);
                if (_pool.AliveListBuffer != null)
                    _computeCmd.SetComputeRootUnorderedAccessView(1, _pool.AliveListBuffer.GpuVirtualAddress);
                if (_pool.DeadListBuffer != null)
                    _computeCmd.SetComputeRootUnorderedAccessView(2, _pool.DeadListBuffer.GpuVirtualAddress);
                if (_pool.CounterBuffer != null)
                    _computeCmd.SetComputeRootUnorderedAccessView(3, _pool.CounterBuffer.GpuVirtualAddress);
                _computeCmd.SetComputeRootConstantBufferView(0, _simParamsCB!.GpuVirtualAddress);
                int simGroups = (_pool.MaxParticles + 63) / 64;
                _computeCmd.Dispatch(simGroups, 1, 1);
            }

            // 4) 视锥剔除
            if (_culler != null && _pool.CurrentWriteBuffer != null && _pool.AliveListBuffer != null)
            {
                _culler.Cull(_computeCmd, _pool.CurrentWriteBuffer, _pool.AliveListBuffer,
                            _simParamsCB!, _pool.MaxParticles);
            }

            // 5) 距离排序
            if (_sorter != null && _pool.SortKeyBuffer != null && _pool.SortedIndexBuffer != null
                && _pool.CounterBuffer != null)
            {
                _sorter.Sort(_computeCmd, _pool.SortKeyBuffer, _pool.SortedIndexBuffer,
                            _pool.CounterBuffer, _pool.MaxParticles, ascending: false);
            }

            // 6) 生成间接绘制参数
            if (_indirectDrawGen != null && _pool.CounterBuffer != null)
            {
                _indirectDrawGen.Generate(_computeCmd, _pool.CounterBuffer);
            }

            // 转换回通用读状态
            _pool.TransitionToCommon(_computeCmd);

            _computeCmd.Close();
            _computeQueue!.ExecuteCommandLists(new[] { _computeCmd });
            _computeQueue.Flush();

            // 乒乓缓冲切换
            _pool.SwapBuffers();
        }

        /// <summary>注册曲线到曲线采样器。</summary>
        public void RegisterCurve(string name, ParticleCurve curve)
        {
            _curveSampler?.RegisterScalarCurve(name, curve);
        }

        /// <summary>注册颜色曲线到曲线采样器。</summary>
        public void RegisterColorCurve(string name, ColorCurve curve)
        {
            _curveSampler?.RegisterColorCurve(name, curve);
        }

        /// <summary>添加力场。</summary>
        public void AddForceField(GPUForceFieldData ff)
        {
            _forceIntegrator?.AddForce(ff);
        }

        /// <summary>添加重力。</summary>
        public void AddGravity(Vector3 direction, float strength = 1f)
        {
            _forceIntegrator?.AddGravity(direction, strength);
        }

        /// <summary>添加 Curl Noise 湍流。</summary>
        public void AddCurlNoise(float strength, float frequency, float speed)
        {
            _forceIntegrator?.AddCurlNoise(strength, frequency, speed);
        }

        /// <summary>添加吸引力。</summary>
        public void AddAttractor(Vector3 center, float strength, float radius, float falloff = 2f)
        {
            _forceIntegrator?.AddAttractor(center, strength, radius, falloff);
        }

        /// <summary>添加排斥力。</summary>
        public void AddRepeller(Vector3 center, float strength, float radius, float falloff = 2f)
        {
            _forceIntegrator?.AddRepeller(center, strength, radius, falloff);
        }

        /// <summary>添加风力。</summary>
        public void AddWind(Vector3 direction, float strength, float gustFrequency = 0.5f)
        {
            _forceIntegrator?.AddWind(direction, strength, gustFrequency);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _pool?.Dispose();
            _emitKernel?.Dispose();
            _simulateKernel?.Dispose();
            _cullKernel?.Dispose();
            _culler?.Dispose();
            _sorter?.Dispose();
            _curveSampler?.Dispose();
            _forceIntegrator?.Dispose();
            _indirectDrawGen?.Dispose();
            _simParamsCB?.Dispose();
            _emitterParamsCB?.Dispose();
            _computeCmd?.Dispose();
            _disposed = true;
        }
    }
}
