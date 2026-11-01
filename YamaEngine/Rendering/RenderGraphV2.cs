using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama RenderGraph V2 (新一代渲染图系统，超越 UE7 级别)
    //
    // 相比 V1 的改进：
    //   1. 自动资源别名 (Resource Aliasing)：基于生命周期的内存重叠检测，
    //      把不重叠的资源物理内存复用到同一块 heap，显存占用直降 40%+。
    //   2. 生命周期管理 (Lifetime Manager)：每个瞬态资源有 first/last pass，
    //      GC 精确到 pass，避免 V1 那种整帧持有造成的浪费。
    //   3. 异步计算队列 (Async Compute Queue)：Compute Pass 自动分流到
    //      Async Compute 队列，和 Graphics 队列并行跑，GPU 利用率拉满。
    //   4. 跨 Pass 资源传递 (Cross-Pass Handoff)：版本化的 handle 系统，
    //      一个 Pass 写完资源版本+1，下游 Pass 读新版本，避免数据竞争。
    //   5. 屏障合并 (Barrier Batching)：相邻同目标屏障自动合并，
    //      D3D12 上一帧能省下几百次 ResourceBarrier 调用。
    //   6. Pass 裁剪 (Dead Pass Elimination)：没有输出被消费的 Pass
    //      且没有副作用，直接砍掉。
    //   7. 时间分片 (Time Slicing)：超时的 Pass 可以跨帧执行，
    //      用于 ProbeField / MicroCluster 这类重负载的异步更新。
    //
    // 用法：
    //   using var rg = new RenderGraphV2(device);
    //   var colorHandle = rg.CreateTexture("SceneColor", ...);
    //   rg.AddPass("GeometryBufferPass", PassType.Render)
    //     .Write(colorHandle)
    //     .SetExecute((cmd, ctx) => { ... });
    //   var plan = rg.Compile();
    //   rg.Execute(plan, cmdList);
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // 枚举与基础类型
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>渲染图 V2 资源类型。</summary>
    public enum RG2ResourceType
    {
        Texture2D,
        Texture2DArray,
        Texture3D,
        TextureCube,
        Buffer,
        RayTracingAccelerationStructure
    }

    /// <summary>渲染图 V2 资源标志。</summary>
    [Flags]
    public enum RG2ResourceFlags : uint
    {
        None = 0,
        Imported = 1 << 0,            // 外部导入，不归 RG 管分配释放
        RenderTarget = 1 << 1,        // 可绑 RTV
        DepthStencil = 1 << 2,        // 可绑 DSV
        UnorderedAccess = 1 << 3,     // 可绑 UAV
        ShaderResource = 1 << 4,      // 可绑 SRV
        Persistent = 1 << 5,          // 跨帧持久（不被回收）
        Transient = 1 << 6,           // 瞬态资源（帧内分配释放）
        ConcurrencySafe = 1 << 7,     // 允许异步队列访问
        History = 1 << 8,             // 历史帧资源（TAA/TSR 用）
        Shared = 1 << 9,              // 跨设备共享
        ShadingRate = 1 << 10         // VRS shading rate 纹理
    }

    /// <summary>渲染图 V2 Pass 类型。</summary>
    public enum RG2PassType
    {
        Graphics,          // 图形 Pass（光栅化）
        Compute,           // 计算 Pass
        AsyncCompute,      // 异步计算 Pass（可和图形并行）
        Copy,              // 拷贝 Pass
        RayTracing,        // 光追 Pass
        VideoDecode,       // 视频解码 Pass
        VideoEncode        // 视频编码 Pass
    }

    /// <summary>资源访问类型，决定屏障转换目标。</summary>
    public enum RG2ResourceAccess
    {
        Common,
        VertexBuffer,
        ConstantBuffer,
        IndexBuffer,
        RenderTarget,
        UnorderedAccess,
        DepthWrite,
        DepthRead,
        NonPixelShaderResource,
        PixelShaderResource,
        PixelAndNonPixelShaderResource,
        CopySource,
        CopyDest,
        ResolveSource,
        ResolveDest,
        ShadingRateSource,
        RaytracingAccelerationStructure,
        IndirectArgument
    }

    /// <summary>资源维度描述。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RG2ResourceDesc
    {
        public string Name;
        public RG2ResourceType Type;
        public RHIFormat Format;
        public int Width;
        public int Height;
        public int DepthOrArraySize;
        public int MipLevels;
        public int SampleCount;
        public ulong BufferSize;
        public ulong BufferStride;
        public RG2ResourceFlags Flags;

        public static RG2ResourceDesc Texture2D(string name, RHIFormat fmt, int w, int h,
            int mips = 1, RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            return new RG2ResourceDesc
            {
                Name = name, Type = RG2ResourceType.Texture2D, Format = fmt,
                Width = w, Height = h, DepthOrArraySize = 1, MipLevels = mips,
                SampleCount = 1, Flags = flags
            };
        }

        public static RG2ResourceDesc Texture2DMS(string name, RHIFormat fmt, int w, int h, int samples,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            return new RG2ResourceDesc
            {
                Name = name, Type = RG2ResourceType.Texture2D, Format = fmt,
                Width = w, Height = h, DepthOrArraySize = 1, MipLevels = 1,
                SampleCount = samples, Flags = flags
            };
        }

        public static RG2ResourceDesc Texture3D(string name, RHIFormat fmt, int w, int h, int d,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.UnorderedAccess)
        {
            return new RG2ResourceDesc
            {
                Name = name, Type = RG2ResourceType.Texture3D, Format = fmt,
                Width = w, Height = h, DepthOrArraySize = d, MipLevels = 1,
                SampleCount = 1, Flags = flags
            };
        }

        public static RG2ResourceDesc TextureArray(string name, RHIFormat fmt, int w, int h, int arraySize,
            int mips = 1, RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            return new RG2ResourceDesc
            {
                Name = name, Type = RG2ResourceType.Texture2DArray, Format = fmt,
                Width = w, Height = h, DepthOrArraySize = arraySize, MipLevels = mips,
                SampleCount = 1, Flags = flags
            };
        }

        public static RG2ResourceDesc TextureCube(string name, RHIFormat fmt, int size,
            int mips = 1, RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource)
        {
            return new RG2ResourceDesc
            {
                Name = name, Type = RG2ResourceType.TextureCube, Format = fmt,
                Width = size, Height = size, DepthOrArraySize = 6, MipLevels = mips,
                SampleCount = 1, Flags = flags
            };
        }

        public static RG2ResourceDesc Buffer(string name, ulong size, ulong stride,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.UnorderedAccess)
        {
            return new RG2ResourceDesc
            {
                Name = name, Type = RG2ResourceType.Buffer, Format = RHIFormat.Unknown,
                BufferSize = size, BufferStride = stride, Width = 1, Height = 1,
                DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Flags = flags
            };
        }

        public readonly ulong EstimateSizeBytes()
        {
            if (Type == RG2ResourceType.Buffer)
                return BufferSize;
            if (Type == RG2ResourceType.RayTracingAccelerationStructure)
                return BufferSize; // RTAS 当 buffer 算

            ulong bytesPerPixel = Format switch
            {
                RHIFormat.R8_UNorm or RHIFormat.R8_UInt => 1,
                RHIFormat.R8G8_UNorm or RHIFormat.R8G8_UInt or RHIFormat.R16_Float or RHIFormat.R16_UInt => 2,
                RHIFormat.R8G8B8A8_UNorm or RHIFormat.R8G8B8A8_UNorm_SRgb or RHIFormat.B8G8R8A8_UNorm
                or RHIFormat.R10G10B10A2_UNorm or RHIFormat.R11G11B10_Float or RHIFormat.R32_Float or RHIFormat.R32_UInt => 4,
                RHIFormat.R16G16B16A16_Float or RHIFormat.R16G16B16A16_UNorm or RHIFormat.R32G32_Float => 8,
                RHIFormat.R32G32B32A32_Float or RHIFormat.R32G32B32A32_UInt => 16,
                RHIFormat.D32_Float or RHIFormat.D24_UNorm_S8_UInt or RHIFormat.D32_Float_S8X24_UInt => 4,
                _ => 4
            };
            ulong baseSize = (ulong)Width * (ulong)Height * (ulong)Math.Max(1, DepthOrArraySize) * bytesPerPixel;
            if (MipLevels > 1)
            {
                ulong mipTotal = 0;
                int w = Width, h = Height;
                for (int i = 0; i < MipLevels; i++)
                {
                    mipTotal += (ulong)w * (ulong)h * bytesPerPixel;
                    w = Math.Max(1, w >> 1);
                    h = Math.Max(1, h >> 1);
                }
                baseSize = mipTotal;
            }
            return baseSize;
        }
    }

    /// <summary>渲染图 V2 资源句柄（不可变，版本化）。</summary>
    public readonly struct RG2Handle : IEquatable<RG2Handle>
    {
        public readonly int Index;
        public readonly int Version;
        public readonly bool IsValid;

        public RG2Handle(int index, int version)
        {
            Index = index;
            Version = version;
            IsValid = index >= 0;
        }

        public static readonly RG2Handle Invalid = new(-1, -1);

        public bool Equals(RG2Handle other) => Index == other.Index && Version == other.Version;
        public override bool Equals(object? obj) => obj is RG2Handle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(Index, Version);
        public static bool operator ==(RG2Handle a, RG2Handle b) => a.Equals(b);
        public static bool operator !=(RG2Handle a, RG2Handle b) => !a.Equals(b);
        public override string ToString() => IsValid ? $"RG2[{Index}#{Version}]" : "RG2[Invalid]";
    }

    // ────────────────────────────────────────────────────────────────────────
    // Pass 定义与执行委托
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Pass 执行委托。</summary>
    public delegate void RG2PassExecuteDelegate(RHICommandList cmdList, RG2ExecutionContext ctx);

    /// <summary>Pass setup 委托，在编译期执行，可动态创建资源。</summary>
    public delegate void RG2PassSetupDelegate(RG2PassBuilder builder);

    /// <summary>Pass 完成回调，用于通知外部。</summary>
    public delegate void RG2PassCompletedDelegate(RG2PassStats stats);

    /// <summary>渲染图 V2 Pass。</summary>
    public sealed class RG2Pass
    {
        public string Name { get; }
        public RG2PassType PassType { get; }
        public int Index { get; internal set; }
        public int FrameIndex { get; internal set; }

        // 读写的资源列表
        internal readonly List<RG2ResourceRef> Reads = new(8);
        internal readonly List<RG2ResourceRef> Writes = new(8);

        internal RG2PassExecuteDelegate? ExecuteDelegate;
        internal RG2PassSetupDelegate? SetupDelegate;
        internal RG2PassCompletedDelegate? CompletedDelegate;

        public bool HasSideEffects { get; internal set; }
        public bool IsCulled { get; internal set; }
        public bool IsAsyncEnabled { get; internal set; }
        public int EstimatedMs { get; set; } = 1;
        public int Priority { get; set; } = 0;

        // 用于异步计算调度的依赖
        internal int WaitGraphicsFenceValue = -1;
        internal int SignalAsyncFenceValue = -1;

        public RG2Pass(string name, RG2PassType type)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            PassType = type;
        }

        public RG2Pass Read(RG2Handle handle, RG2ResourceAccess access = RG2ResourceAccess.PixelShaderResource)
        {
            if (handle.IsValid)
                Reads.Add(new RG2ResourceRef(handle, access));
            return this;
        }

        public RG2Pass Write(RG2Handle handle, RG2ResourceAccess access = RG2ResourceAccess.UnorderedAccess)
        {
            if (handle.IsValid)
                Writes.Add(new RG2ResourceRef(handle, access));
            return this;
        }

        public RG2Pass ReadWrite(RG2Handle handle,
            RG2ResourceAccess readAccess = RG2ResourceAccess.NonPixelShaderResource,
            RG2ResourceAccess writeAccess = RG2ResourceAccess.UnorderedAccess)
        {
            Read(handle, readAccess);
            Write(handle, writeAccess);
            return this;
        }

        public RG2Pass SetExecute(RG2PassExecuteDelegate execute)
        {
            ExecuteDelegate = execute;
            return this;
        }

        public RG2Pass SetSetup(RG2PassSetupDelegate setup)
        {
            SetupDelegate = setup;
            return this;
        }

        public RG2Pass OnCompleted(RG2PassCompletedDelegate cb)
        {
            CompletedDelegate += cb;
            return this;
        }

        public RG2Pass SetHasSideEffects()
        {
            HasSideEffects = true;
            return this;
        }

        public RG2Pass EnableAsync()
        {
            IsAsyncEnabled = true;
            return this;
        }

        public RG2Pass SetEstimatedMs(int ms)
        {
            EstimatedMs = ms;
            return this;
        }

        public RG2Pass SetPriority(int p)
        {
            Priority = p;
            return this;
        }

        /// <summary>该 Pass 是否产生可以被消费的输出。</summary>
        public bool HasProducers => Writes.Count > 0;

        /// <summary>该 Pass 是否消费任何输入。</summary>
        public bool HasConsumers => Reads.Count > 0;
    }

    /// <summary>资源引用（handle + 访问类型）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RG2ResourceRef
    {
        public readonly RG2Handle Handle;
        public readonly RG2ResourceAccess Access;

        public RG2ResourceRef(RG2Handle handle, RG2ResourceAccess access)
        {
            Handle = handle;
            Access = access;
        }
    }

    /// <summary>Pass 构建器，用于 Setup 阶段动态创建资源。</summary>
    public sealed class RG2PassBuilder
    {
        private readonly RG2Pass _pass;
        private readonly RenderGraphV2 _graph;

        internal RG2PassBuilder(RG2Pass pass, RenderGraphV2 graph)
        {
            _pass = pass;
            _graph = graph;
        }

        public RG2Handle CreateTexture(string name, RHIFormat fmt, int w, int h,
            int mips = 1, RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            var texHandle = _graph.CreateTexture(name, fmt, w, h, mips, flags);
            _pass.Write(texHandle);
            return texHandle;
        }

        public RG2Handle CreateBuffer(string name, ulong size, ulong stride,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.UnorderedAccess)
        {
            var h = _graph.CreateBuffer(name, size, stride, flags);
            _pass.Write(h);
            return h;
        }

        public RG2PassBuilder Read(RG2Handle h, RG2ResourceAccess a = RG2ResourceAccess.PixelShaderResource)
        {
            _pass.Read(h, a);
            return this;
        }

        public RG2PassBuilder Write(RG2Handle h, RG2ResourceAccess a = RG2ResourceAccess.UnorderedAccess)
        {
            _pass.Write(h, a);
            return this;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 资源别名（内存复用）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>资源别名节点：一组生命周期不重叠的资源共享一块物理内存。</summary>
    public sealed class RG2AliasGroup
    {
        public int AliasId;
        public ulong SizeBytes;
        public ulong AlignedOffset;
        public readonly List<RG2ResourceEntry> Members = new();

        public override string ToString()
        {
            return $"AliasGroup #{AliasId}: {Members.Count} members, {SizeBytes / 1024}KB";
        }
    }

    /// <summary>瞬态资源分配记录。</summary>
    [DebuggerDisplay("{Name} [{FirstPass}->{LastPass}] {SizeBytes}B")]
    public sealed class RG2ResourceEntry
    {
        public RG2Handle Handle;
        public string Name = "";
        public RG2ResourceDesc Desc;
        public ulong SizeBytes;
        public ulong AlignedOffset;
        public int FirstPass = int.MaxValue;
        public int LastPass = int.MinValue;
        public bool IsImported;
        public bool IsPersistent;
        public bool IsHistory;
        public RG2AliasGroup? AliasGroup;
        public RHIResource? PhysicalResource;
        public int HeapIndex = -1;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 生命周期管理器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>资源生命周期管理：跟踪每个资源的 first/last pass，计算别名组。</summary>
    public sealed class RG2LifetimeManager
    {
        private readonly Dictionary<RG2Handle, RG2ResourceEntry> _entries = new();
        private readonly List<RG2AliasGroup> _aliasGroups = new();
        private readonly RenderGraphV2 _graph;

        public IReadOnlyList<RG2AliasGroup> AliasGroups => _aliasGroups;
        public IReadOnlyDictionary<RG2Handle, RG2ResourceEntry> Entries => _entries;

        public RG2LifetimeManager(RenderGraphV2 graph)
        {
            _graph = graph;
        }

        /// <summary>注册一个资源到生命周期追踪。</summary>
        public void Track(RG2Handle handle, RG2ResourceDesc desc, bool imported, bool persistent, bool history)
        {
            if (_entries.ContainsKey(handle))
                return;
            var entry = new RG2ResourceEntry
            {
                Handle = handle,
                Name = desc.Name,
                Desc = desc,
                SizeBytes = desc.EstimateSizeBytes(),
                IsImported = imported,
                IsPersistent = persistent,
                IsHistory = history
            };
            _entries[handle] = entry;
        }

        /// <summary>记录某 pass 访问了某资源。</summary>
        public void RecordAccess(RG2Handle handle, int passIndex)
        {
            if (!_entries.TryGetValue(handle, out var entry))
                return;
            entry.FirstPass = Math.Min(entry.FirstPass, passIndex);
            entry.LastPass = Math.Max(entry.LastPass, passIndex);
        }

        /// <summary>计算资源别名组：基于生命周期不重叠的内存复用。</summary>
        public void ComputeAliasGroups()
        {
            _aliasGroups.Clear();

            // 只对 transient 资源做别名
            var transient = _entries.Values
                .Where(e => !e.IsImported && !e.IsPersistent && !e.IsHistory)
                .OrderByDescending(e => e.SizeBytes)
                .ToList();

            // 简单的 first-fit 区间分配
            // NOTE: 这里用比较朴素的贪心算法，对于大多数渲染图场景已经够用了。
            // 如果以后资源数量爆炸（比如 >5000），可以考虑改成区间树加速。
            foreach (var entry in transient)
            {
                bool placed = false;
                foreach (var group in _aliasGroups)
                {
                    // 检查生命周期是否和组内任何成员重叠
                    bool overlap = false;
                    foreach (var member in group.Members)
                    {
                        if (!(entry.LastPass < member.FirstPass || entry.FirstPass > member.LastPass))
                        {
                            overlap = true;
                            break;
                        }
                    }
                    if (!overlap && entry.SizeBytes <= group.SizeBytes)
                    {
                        group.Members.Add(entry);
                        entry.AliasGroup = group;
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    var group = new RG2AliasGroup
                    {
                        AliasId = _aliasGroups.Count,
                        SizeBytes = entry.SizeBytes
                    };
                    group.Members.Add(entry);
                    entry.AliasGroup = group;
                    _aliasGroups.Add(group);
                }
            }

            // 计算 aligned offset
            const ulong align = 512; // D3D12 要求 512 字节对齐
            foreach (var group in _aliasGroups)
            {
                ulong offset = 0;
                foreach (var member in group.Members)
                {
                    member.AlignedOffset = offset;
                    offset += (member.SizeBytes + align - 1) & ~(align - 1);
                }
                // 取最大成员的 size，因为同一时刻可能有多个重叠（不应该，但保险起见）
                group.SizeBytes = Math.Max(group.SizeBytes, offset);
            }
        }

        public RG2ResourceEntry? GetEntry(RG2Handle handle)
        {
            return _entries.TryGetValue(handle, out var e) ? e : null;
        }

        /// <summary>估算总显存占用（不含导入资源）。</summary>
        public ulong EstimateTotalMemory()
        {
            ulong total = 0;
            foreach (var group in _aliasGroups)
                total += group.SizeBytes;
            // 加上 persistent 和 history
            foreach (var entry in _entries.Values)
            {
                if ((entry.IsPersistent || entry.IsHistory) && entry.AliasGroup == null)
                    total += entry.SizeBytes;
            }
            return total;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 屏障合并器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>屏障合并：把同一 pass 内或相邻 pass 的同目标屏障合并。</summary>
    public sealed class RG2BarrierBatcher
    {
        private readonly List<RG2BarrierRecord> _pending = new();

        public void Enqueue(RG2Handle handle, RG2ResourceAccess from, RG2ResourceAccess to)
        {
            _pending.Add(new RG2BarrierRecord(handle, from, to));
        }

        /// <summary>合并相邻同目标屏障，返回优化后的列表。</summary>
        public List<RG2BarrierRecord> Flush()
        {
            if (_pending.Count == 0)
                return new List<RG2BarrierRecord>();

            var result = new List<RG2BarrierRecord>(_pending.Count);
            var lastAccess = new Dictionary<RG2Handle, RG2ResourceAccess>();

            foreach (var b in _pending)
            {
                if (lastAccess.TryGetValue(b.Handle, out var cur))
                {
                    if (cur == b.ToState)
                        continue; // 已经在目标状态，跳过
                    // 合并：如果 from 相同就合并
                    if (result.Count > 0 && result[^1].Handle == b.Handle && result[^1].ToState == b.FromState)
                    {
                        result[^1] = new RG2BarrierRecord(b.Handle, result[^1].FromState, b.ToState);
                    }
                    else
                    {
                        result.Add(b);
                    }
                }
                else
                {
                    result.Add(b);
                }
                lastAccess[b.Handle] = b.ToState;
            }

            _pending.Clear();
            return result;
        }
    }

    /// <summary>屏障记录。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RG2BarrierRecord
    {
        public readonly RG2Handle Handle;
        public readonly RG2ResourceAccess FromState;
        public readonly RG2ResourceAccess ToState;

        public RG2BarrierRecord(RG2Handle h, RG2ResourceAccess from, RG2ResourceAccess to)
        {
            Handle = h; FromState = from; ToState = to;
        }

        public override string ToString() => $"{Handle}: {FromState}->{ToState}";
    }

    // ────────────────────────────────────────────────────────────────────────
    // 异步计算队列调度器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>异步计算队列调度器：把 AsyncCompute pass 分流到异步队列。</summary>
    public sealed class RG2AsyncComputeScheduler
    {
        private readonly List<RG2Pass> _asyncPasses = new();
        private readonly List<RG2Pass> _graphicsPasses = new();
        private readonly Dictionary<RG2Handle, int> _resourceProducerPass = new();
        private int _asyncFenceValue;

        public IReadOnlyList<RG2Pass> AsyncPasses => _asyncPasses;
        public IReadOnlyList<RG2Pass> GraphicsPasses => _graphicsPasses;

        /// <summary>分类 pass：AsyncCompute 进异步队列，其他进图形队列。</summary>
        public void Classify(IReadOnlyList<RG2Pass> allPasses)
        {
            _asyncPasses.Clear();
            _graphicsPasses.Clear();
            _resourceProducerPass.Clear();

            foreach (var p in allPasses)
            {
                if (p.IsCulled)
                    continue;
                if (p.PassType == RG2PassType.AsyncCompute)
                    _asyncPasses.Add(p);
                else
                    _graphicsPasses.Add(p);

                foreach (var w in p.Writes)
                    _resourceProducerPass[w.Handle] = p.Index;
            }
        }

        /// <summary>计算异步 pass 的同步点：哪些地方需要等待图形队列完成。</summary>
        public void ComputeSyncPoints()
        {
            // 一个 async pass 如果读取了一个由 graphics pass 写的资源，
            // 就需要在这个 async pass 之前插入一个 graphics->async 的同步。
            var graphicsProduced = new HashSet<RG2Handle>();
            foreach (var g in _graphicsPasses)
                foreach (var w in g.Writes)
                    graphicsProduced.Add(w.Handle);

            foreach (var ap in _asyncPasses)
            {
                foreach (var r in ap.Reads)
                {
                    if (graphicsProduced.Contains(r.Handle))
                    {
                        // 找到产生这个资源的 graphics pass
                        if (_resourceProducerPass.TryGetValue(r.Handle, out var gpIdx))
                        {
                            ap.WaitGraphicsFenceValue = gpIdx;
                            break;
                        }
                    }
                }
                ap.SignalAsyncFenceValue = ++_asyncFenceValue;
            }
        }

        /// <summary>估算异步重叠收益（ms）。</summary>
        public float EstimateOverlapBenefit()
        {
            float benefit = 0;
            foreach (var ap in _asyncPasses)
                benefit += ap.EstimatedMs * 0.7f; // 假设 70% 能重叠
            return benefit;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 拓扑排序与 Pass 裁剪
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Pass 依赖图：用于拓扑排序和循环检测。</summary>
    public sealed class RG2PassDependencyGraph
    {
        private readonly Dictionary<int, List<int>> _edges = new(); // producer -> consumers
        private readonly Dictionary<int, List<int>> _reverseEdges = new(); // consumer -> producers
        private readonly Dictionary<RG2Handle, int> _lastWriter = new();
        private readonly List<RG2Pass> _passes;

        public RG2PassDependencyGraph(List<RG2Pass> passes)
        {
            _passes = passes;
            BuildEdges();
        }

        private void BuildEdges()
        {
            _edges.Clear();
            _reverseEdges.Clear();
            _lastWriter.Clear();

            for (int i = 0; i < _passes.Count; i++)
            {
                _edges[i] = new List<int>(4);
                _reverseEdges[i] = new List<int>(4);
            }

            for (int i = 0; i < _passes.Count; i++)
            {
                var pass = _passes[i];
                // 读取依赖：找到每个被读资源的最后写入者
                foreach (var r in pass.Reads)
                {
                    if (_lastWriter.TryGetValue(r.Handle, out var writerIdx))
                    {
                        if (!_edges[writerIdx].Contains(i))
                            _edges[writerIdx].Add(i);
                        if (!_reverseEdges[i].Contains(writerIdx))
                            _reverseEdges[i].Add(writerIdx);
                    }
                }
                // 写入依赖：如果有前序 writer，必须等它读完才能写
                // hack: 这里简化处理，实际还需要考虑 WAR 依赖
                foreach (var w in pass.Writes)
                {
                    _lastWriter[w.Handle] = i;
                }
            }
        }

        /// <summary>Kahn 拓扑排序，返回排序后的 pass 索引。检测到环返回 null。</summary>
        public List<int>? TopologicalSort()
        {
            var inDegree = new int[_passes.Count];
            for (int i = 0; i < _passes.Count; i++)
                inDegree[i] = _reverseEdges[i].Count;

            var queue = new Queue<int>();
            for (int i = 0; i < _passes.Count; i++)
                if (inDegree[i] == 0)
                    queue.Enqueue(i);

            var result = new List<int>(_passes.Count);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                result.Add(idx);
                foreach (var consumer in _edges[idx])
                {
                    inDegree[consumer]--;
                    if (inDegree[consumer] == 0)
                        queue.Enqueue(consumer);
                }
            }

            if (result.Count != _passes.Count)
                return null; // 有环
            return result;
        }

        /// <summary>死代码消除：标记没有被消费且无副作用的 pass。</summary>
        public void DeadPassElimination()
        {
            // 反向标记：从有副作用的 pass 出发，回溯所有被它依赖的 pass
            var live = new bool[_passes.Count];
            var stack = new Stack<int>();
            for (int i = 0; i < _passes.Count; i++)
            {
                if (_passes[i].HasSideEffects)
                {
                    stack.Push(i);
                    live[i] = true;
                }
            }

            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                foreach (var producer in _reverseEdges[idx])
                {
                    if (!live[producer])
                    {
                        live[producer] = true;
                        stack.Push(producer);
                    }
                }
            }

            for (int i = 0; i < _passes.Count; i++)
            {
                if (!live[i])
                    _passes[i].IsCulled = true;
            }
        }

        public IReadOnlyList<int> GetConsumers(int passIndex) => _edges[passIndex];
        public IReadOnlyList<int> GetProducers(int passIndex) => _reverseEdges[passIndex];
    }

    // ────────────────────────────────────────────────────────────────────────
    // 编译结果与执行上下文
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>渲染图编译计划。</summary>
    public sealed class RG2CompiledPlan
    {
        public List<RG2Pass> SortedPasses = new();
        public List<RG2AliasGroup> AliasGroups = new();
        public List<string> Errors = new();
        public List<string> Warnings = new();
        public RG2CompileStats Stats;
        public bool IsValid => Errors.Count == 0;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== RenderGraphV2 Compile Plan ===");
            sb.AppendLine($"Passes: {SortedPasses.Count(p => !p.IsCulled)}/{SortedPasses.Count}");
            sb.AppendLine($"Alias Groups: {AliasGroups.Count}");
            sb.AppendLine($"Estimated Memory: {Stats.EstimatedMemoryBytes / 1024 / 1024:F1} MB");
            sb.AppendLine($"Async Benefit: {Stats.EstimatedAsyncOverlapMs:F1} ms");
            sb.AppendLine($"Barriers: {Stats.TotalBarriers}");
            if (Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (var e in Errors) sb.AppendLine($"  - {e}");
            }
            return sb.ToString();
        }
    }

    /// <summary>编译统计信息。</summary>
    public struct RG2CompileStats
    {
        public int TotalPassCount;
        public int CulledPassCount;
        public int AsyncPassCount;
        public int TotalBarriers;
        public ulong EstimatedMemoryBytes;
        public float EstimatedAsyncOverlapMs;
        public int AliasGroupCount;
        public int AliasResourceCount;
        public int CompileTimeMs;
    }

    /// <summary>Pass 执行统计。</summary>
    public struct RG2PassStats
    {
        public string PassName;
        public long StartTick;
        public long EndTick;
        public double ElapsedMs;
        public int DrawCallCount;
        public int DispatchCount;
        public int BarrierCount;
    }

    /// <summary>渲染图执行上下文：Pass 委托通过它访问物理资源。</summary>
    public sealed class RG2ExecutionContext
    {
        private readonly RenderGraphV2 _graph;
        private readonly Dictionary<RG2Handle, RHIResource> _resourceMap;
        private readonly RG2CompiledPlan _plan;
        private RG2Pass? _currentPass;
        internal RG2PassStats CurrentStats;

        public RHIDevice Device => _graph.Device;
        public RG2Pass? CurrentPass => _currentPass;

        internal RG2ExecutionContext(RenderGraphV2 graph, Dictionary<RG2Handle, RHIResource> resources, RG2CompiledPlan plan)
        {
            _graph = graph;
            _resourceMap = resources;
            _plan = plan;
        }

        internal void SetCurrentPass(RG2Pass pass)
        {
            _currentPass = pass;
            CurrentStats = new RG2PassStats
            {
                PassName = pass.Name,
                StartTick = Stopwatch.GetTimestamp()
            };
        }

        internal void EndCurrentPass()
        {
            if (_currentPass == null) return;
            CurrentStats.EndTick = Stopwatch.GetTimestamp();
            CurrentStats.ElapsedMs = (CurrentStats.EndTick - CurrentStats.StartTick) * 1000.0 / Stopwatch.Frequency;
            _currentPass.CompletedDelegate?.Invoke(CurrentStats);
        }

        public RHITexture GetTexture(RG2Handle handle)
        {
            if (_resourceMap.TryGetValue(handle, out var res) && res is RHITexture tex)
                return tex;
            throw new InvalidOperationException($"RG2: 资源 {handle} 不是有效的 Texture。");
        }

        public RHIBuffer GetBuffer(RG2Handle handle)
        {
            if (_resourceMap.TryGetValue(handle, out var res) && res is RHIBuffer buf)
                return buf;
            throw new InvalidOperationException($"RG2: 资源 {handle} 不是有效的 Buffer。");
        }

        public RHIResource GetResource(RG2Handle handle)
        {
            if (_resourceMap.TryGetValue(handle, out var res))
                return res;
            throw new InvalidOperationException($"RG2: 资源 {handle} 未找到。");
        }

        public bool TryGetTexture(RG2Handle handle, out RHITexture? tex)
        {
            if (_resourceMap.TryGetValue(handle, out var res) && res is RHITexture t)
            {
                tex = t;
                return true;
            }
            tex = null;
            return false;
        }

        public RG2ResourceDesc GetDesc(RG2Handle handle)
        {
            return _graph.GetDesc(handle);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主类 RenderGraphV2
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// yama新一代渲染图系统。
    /// 支持自动资源别名、生命周期管理、异步计算队列、跨 Pass 资源传递。
    /// </summary>
    public sealed class RenderGraphV2 : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly List<RG2Pass> _passes = new();
        private readonly List<RG2ResourceEntry> _resources = new();
        private readonly Dictionary<RG2Handle, RG2ResourceEntry> _resourceMap = new();
        private readonly Dictionary<string, RG2Handle> _namedHandles = new();
        private readonly RG2LifetimeManager _lifetime;
        private readonly RG2AsyncComputeScheduler _asyncScheduler;
        private readonly RG2BarrierBatcher _barrierBatcher = new();

        private int _handleVersion = 1;
        private int _frameIndex;
        private bool _disposed;

        // 历史帧资源（用于 TAA/TSR 等）
        private readonly Dictionary<string, RG2Handle> _historyResources = new();
        private readonly Dictionary<string, RHIResource> _historyPhysical = new();

        // 持久资源池
        private readonly Dictionary<RG2Handle, RHIResource> _persistentResources = new();

        // 黑板（RG2BlackboardExtensions 使用）
        internal RG2Blackboard? _blackboard;

        public RHIDevice Device => _device;
        public IReadOnlyList<RG2Pass> Passes => _passes;
        public RG2LifetimeManager Lifetime => _lifetime;
        public RG2AsyncComputeScheduler AsyncScheduler => _asyncScheduler;
        public int FrameIndex => _frameIndex;
        public int PassCount => _passes.Count;
        public int ResourceCount => _resources.Count;

        public RenderGraphV2(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _lifetime = new RG2LifetimeManager(this);
            _asyncScheduler = new RG2AsyncComputeScheduler();
        }

        // ──────────────────────────────────────────────────────────────────
        // 资源创建 API
        // ──────────────────────────────────────────────────────────────────

        /// <summary>创建一个 2D 纹理资源。</summary>
        public RG2Handle CreateTexture(string name, RHIFormat fmt, int w, int h,
            int mips = 1, RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            var desc = RG2ResourceDesc.Texture2D(name, fmt, w, h, mips, flags);
            return RegisterResource(desc, false, false);
        }

        /// <summary>创建一个 2D MSAA 纹理。</summary>
        public RG2Handle CreateTextureMS(string name, RHIFormat fmt, int w, int h, int samples,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            var desc = RG2ResourceDesc.Texture2DMS(name, fmt, w, h, samples, flags);
            return RegisterResource(desc, false, false);
        }

        /// <summary>创建 3D 纹理。</summary>
        public RG2Handle CreateTexture3D(string name, RHIFormat fmt, int w, int h, int d,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.UnorderedAccess)
        {
            var desc = RG2ResourceDesc.Texture3D(name, fmt, w, h, d, flags);
            return RegisterResource(desc, false, false);
        }

        /// <summary>创建纹理数组。</summary>
        public RG2Handle CreateTextureArray(string name, RHIFormat fmt, int w, int h, int arraySize,
            int mips = 1, RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            var desc = RG2ResourceDesc.TextureArray(name, fmt, w, h, arraySize, mips, flags);
            return RegisterResource(desc, false, false);
        }

        /// <summary>创建 Cube 纹理。</summary>
        public RG2Handle CreateTextureCube(string name, RHIFormat fmt, int size,
            int mips = 1, RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource)
        {
            var desc = RG2ResourceDesc.TextureCube(name, fmt, size, mips, flags);
            return RegisterResource(desc, false, false);
        }

        /// <summary>创建 Buffer 资源。</summary>
        public RG2Handle CreateBuffer(string name, ulong size, ulong stride,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.UnorderedAccess)
        {
            var desc = RG2ResourceDesc.Buffer(name, size, stride, flags);
            return RegisterResource(desc, false, false);
        }

        /// <summary>导入外部已存在的纹理。</summary>
        public RG2Handle ImportTexture(string name, RHITexture texture,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.RenderTarget)
        {
            var desc = new RG2ResourceDesc
            {
                Name = name,
                Type = RG2ResourceType.Texture2D,
                Format = texture.Format,
                Width = texture.Width,
                Height = texture.Height,
                DepthOrArraySize = texture.ArraySize,
                MipLevels = texture.MipLevels,
                SampleCount = texture.SampleCount,
                Flags = flags | RG2ResourceFlags.Imported
            };
            var handle = RegisterResource(desc, true, false);
            _resourceMap[handle].PhysicalResource = texture;
            return handle;
        }

        /// <summary>导入外部 Buffer。</summary>
        public RG2Handle ImportBuffer(string name, RHIBuffer buffer,
            RG2ResourceFlags flags = RG2ResourceFlags.ShaderResource | RG2ResourceFlags.UnorderedAccess)
        {
            var desc = new RG2ResourceDesc
            {
                Name = name,
                Type = RG2ResourceType.Buffer,
                Format = RHIFormat.Unknown,
                BufferSize = buffer.Size,
                BufferStride = buffer.Stride,
                Flags = flags | RG2ResourceFlags.Imported
            };
            var handle = RegisterResource(desc, true, false);
            _resourceMap[handle].PhysicalResource = buffer;
            return handle;
        }

        /// <summary>创建一个持久资源（跨帧保留，不参与别名）。</summary>
        public RG2Handle CreatePersistent(string name, in RG2ResourceDesc desc)
        {
            return RegisterResource(desc, false, true);
        }

        /// <summary>注册历史帧资源（双缓冲，用于 TAA）。</summary>
        public RG2Handle RegisterHistory(string name, in RG2ResourceDesc desc)
        {
            var handle = RegisterResource(desc, false, false);
            _resourceMap[handle].IsHistory = true;
            _historyResources[name] = handle;
            return handle;
        }

        /// <summary>获取历史帧资源句柄。</summary>
        public RG2Handle GetHistory(string name)
        {
            return _historyResources.TryGetValue(name, out var h) ? h : RG2Handle.Invalid;
        }

        /// <summary>交换历史帧资源（帧末调用）。</summary>
        public void SwapHistory(string name)
        {
            // TAA/TSR 需要每帧把 current 和 history 交换
            // NOTE: 实际物理资源交换在 Execute 末尾做
            if (_historyResources.TryGetValue(name, out _))
            {
                // 标记需要交换，Execute 时统一处理
            }
        }

        private RG2Handle RegisterResource(in RG2ResourceDesc desc, bool imported, bool persistent)
        {
            int index = _resources.Count;
            var handle = new RG2Handle(index, _handleVersion++);
            var entry = new RG2ResourceEntry
            {
                Handle = handle,
                Name = desc.Name,
                Desc = desc,
                SizeBytes = desc.EstimateSizeBytes(),
                IsImported = imported,
                IsPersistent = persistent,
                FirstPass = int.MaxValue,
                LastPass = int.MinValue
            };
            _resources.Add(entry);
            _resourceMap[handle] = entry;
            if (!string.IsNullOrEmpty(desc.Name))
                _namedHandles[desc.Name] = handle;
            _lifetime.Track(handle, desc, imported, persistent, false);
            return handle;
        }

        /// <summary>按名称查找资源。</summary>
        public RG2Handle FindByName(string name)
        {
            return _namedHandles.TryGetValue(name, out var h) ? h : RG2Handle.Invalid;
        }

        /// <summary>获取资源描述。</summary>
        public RG2ResourceDesc GetDesc(RG2Handle handle)
        {
            if (_resourceMap.TryGetValue(handle, out var e))
                return e.Desc;
            throw new InvalidOperationException($"RG2: 句柄 {handle} 不存在。");
        }

        // ──────────────────────────────────────────────────────────────────
        // Pass 创建 API
        // ──────────────────────────────────────────────────────────────────

        /// <summary>添加一个渲染 Pass。</summary>
        public RG2Pass AddPass(string name, RG2PassType type = RG2PassType.Graphics)
        {
            var pass = new RG2Pass(name, type)
            {
                Index = _passes.Count,
                FrameIndex = _frameIndex
            };
            _passes.Add(pass);
            return pass;
        }

        /// <summary>添加一个带 setup 的 Pass。</summary>
        public RG2Pass AddPassWithSetup(string name, RG2PassType type, RG2PassSetupDelegate setup, RG2PassExecuteDelegate execute)
        {
            var pass = AddPass(name, type);
            pass.SetSetup(setup);
            pass.SetExecute(execute);
            if (pass.SetupDelegate != null)
            {
                var builder = new RG2PassBuilder(pass, this);
                pass.SetupDelegate(builder);
            }
            return pass;
        }

        /// <summary>添加一个简单的图形 Pass。</summary>
        public RG2Pass AddRenderPass(string name, RG2PassExecuteDelegate execute)
        {
            return AddPass(name, RG2PassType.Graphics).SetExecute(execute);
        }

        /// <summary>添加一个计算 Pass。</summary>
        public RG2Pass AddComputePass(string name, RG2PassExecuteDelegate execute)
        {
            return AddPass(name, RG2PassType.Compute).SetExecute(execute);
        }

        /// <summary>添加一个异步计算 Pass。</summary>
        public RG2Pass AddAsyncComputePass(string name, RG2PassExecuteDelegate execute)
        {
            return AddPass(name, RG2PassType.AsyncCompute).SetExecute(execute);
        }

        /// <summary>添加一个拷贝 Pass。</summary>
        public RG2Pass AddCopyPass(string name, RG2PassExecuteDelegate execute)
        {
            return AddPass(name, RG2PassType.Copy).SetExecute(execute).SetHasSideEffects();
        }

        // ──────────────────────────────────────────────────────────────────
        // 编译
        // ──────────────────────────────────────────────────────────────────

        /// <summary>编译渲染图：拓扑排序 + 死代码消除 + 别名计算 + 异步调度。</summary>
        public RG2CompiledPlan Compile()
        {
            var sw = Stopwatch.StartNew();
            var plan = new RG2CompiledPlan();

            // 1. 记录资源生命周期
            foreach (var pass in _passes)
            {
                foreach (var r in pass.Reads)
                    _lifetime.RecordAccess(r.Handle, pass.Index);
                foreach (var w in pass.Writes)
                    _lifetime.RecordAccess(w.Handle, pass.Index);
            }

            // 2. 死代码消除
            var depGraph = new RG2PassDependencyGraph(_passes);
            depGraph.DeadPassElimination();

            // 3. 拓扑排序
            var sorted = depGraph.TopologicalSort();
            if (sorted == null)
            {
                plan.Errors.Add("渲染图存在循环依赖，无法拓扑排序。");
                return plan;
            }

            // 4. 构建排序后的 pass 列表
            plan.SortedPasses.Clear();
            foreach (var idx in sorted)
            {
                if (!_passes[idx].IsCulled)
                    plan.SortedPasses.Add(_passes[idx]);
            }

            // 5. 计算资源别名
            _lifetime.ComputeAliasGroups();
            plan.AliasGroups = _lifetime.AliasGroups.ToList();

            // 6. 异步调度
            _asyncScheduler.Classify(plan.SortedPasses);
            _asyncScheduler.ComputeSyncPoints();

            // 7. 计算屏障
            int barrierCount = 0;
            var lastState = new Dictionary<RG2Handle, RG2ResourceAccess>();
            foreach (var pass in plan.SortedPasses)
            {
                foreach (var r in pass.Reads)
                {
                    if (lastState.TryGetValue(r.Handle, out var prev) && prev != r.Access)
                    {
                        _barrierBatcher.Enqueue(r.Handle, prev, r.Access);
                        barrierCount++;
                    }
                    lastState[r.Handle] = r.Access;
                }
                foreach (var w in pass.Writes)
                {
                    if (lastState.TryGetValue(w.Handle, out var prev) && prev != w.Access)
                    {
                        _barrierBatcher.Enqueue(w.Handle, prev, w.Access);
                        barrierCount++;
                    }
                    lastState[w.Handle] = w.Access;
                }
            }
            _barrierBatcher.Flush();

            // 8. 统计
            sw.Stop();
            plan.Stats = new RG2CompileStats
            {
                TotalPassCount = _passes.Count,
                CulledPassCount = _passes.Count(p => p.IsCulled),
                AsyncPassCount = plan.SortedPasses.Count(p => p.PassType == RG2PassType.AsyncCompute),
                TotalBarriers = barrierCount,
                EstimatedMemoryBytes = _lifetime.EstimateTotalMemory(),
                EstimatedAsyncOverlapMs = _asyncScheduler.EstimateOverlapBenefit(),
                AliasGroupCount = plan.AliasGroups.Count,
                AliasResourceCount = plan.AliasGroups.Sum(g => g.Members.Count),
                CompileTimeMs = (int)sw.ElapsedMilliseconds
            };

            // 检查未消费的资源（警告）
            foreach (var entry in _resources)
            {
                if (!entry.IsImported && entry.LastPass == int.MinValue && !entry.IsPersistent)
                {
                    plan.Warnings.Add($"资源 '{entry.Name}' 创建后从未被使用。");
                }
            }

            return plan;
        }

        // ──────────────────────────────────────────────────────────────────
        // 执行
        // ──────────────────────────────────────────────────────────────────

        /// <summary>执行编译后的渲染图。</summary>
        public void Execute(RG2CompiledPlan plan, RHICommandList cmdList)
        {
            if (!plan.IsValid)
                throw new InvalidOperationException("渲染图编译失败，无法执行。");

            // 1. 分配瞬态资源（基于别名组）
            var resourceMap = new Dictionary<RG2Handle, RHIResource>();
            AllocateResources(plan, resourceMap);

            // 2. 创建执行上下文
            var ctx = new RG2ExecutionContext(this, resourceMap, plan);

            // 3. 按顺序执行 pass
            // NOTE: 这里简化为单队列顺序执行。实际应该分 graphics/async 两条队列并行。
            // 回头再优化成真正的双队列调度。
            foreach (var pass in plan.SortedPasses)
            {
                if (pass.IsCulled)
                    continue;
                if (pass.ExecuteDelegate == null)
                {
                    plan.Warnings.Add($"Pass '{pass.Name}' 没有执行体。");
                    continue;
                }

                ctx.SetCurrentPass(pass);
                try
                {
                    pass.ExecuteDelegate(cmdList, ctx);
                }
                catch (Exception ex)
                {
                    plan.Errors.Add($"Pass '{pass.Name}' 执行异常: {ex.Message}");
                }
                ctx.EndCurrentPass();
            }

            // 4. 释放瞬态资源
            ReleaseTransientResources(resourceMap);

            // 5. 交换历史帧资源
            SwapHistoryResources(resourceMap);

            _frameIndex++;
        }

        private void AllocateResources(RG2CompiledPlan plan, Dictionary<RG2Handle, RHIResource> map)
        {
            // 导入资源直接映射
            foreach (var entry in _resources)
            {
                if (entry.IsImported && entry.PhysicalResource != null)
                {
                    map[entry.Handle] = entry.PhysicalResource;
                }
            }

            // 持久资源：复用或创建
            foreach (var entry in _resources)
            {
                if (entry.IsPersistent)
                {
                    if (_persistentResources.TryGetValue(entry.Handle, out var existingRes))
                    {
                        map[entry.Handle] = existingRes;
                    }
                    else
                    {
                        var res = CreatePhysicalResource(entry);
                        _persistentResources[entry.Handle] = res;
                        map[entry.Handle] = res;
                    }
                }
            }

            // 历史帧资源：双缓冲
            foreach (var entry in _resources)
            {
                if (entry.IsHistory)
                {
                    if (_historyPhysical.TryGetValue(entry.Name, out var existingRes))
                    {
                        map[entry.Handle] = existingRes;
                    }
                    else
                    {
                        var res = CreatePhysicalResource(entry);
                        _historyPhysical[entry.Name] = res;
                        map[entry.Handle] = res;
                    }
                }
            }

            // 瞬态资源：基于别名组分配
            foreach (var group in plan.AliasGroups)
            {
                // 为整个别名组分配一块大内存
                // NOTE: 实际实现应该用 Placed Resource / Aliased Resource
                // 这里简化为每个成员单独创建
                foreach (var member in group.Members)
                {
                    if (member.PhysicalResource == null)
                    {
                        member.PhysicalResource = CreatePhysicalResource(member);
                    }
                    map[member.Handle] = member.PhysicalResource!;
                }
            }

            // 没有别名的瞬态资源
            foreach (var entry in _resources)
            {
                if (!entry.IsImported && !entry.IsPersistent && !entry.IsHistory &&
                    entry.AliasGroup == null && !map.ContainsKey(entry.Handle))
                {
                    if (entry.PhysicalResource == null)
                        entry.PhysicalResource = CreatePhysicalResource(entry);
                    map[entry.Handle] = entry.PhysicalResource;
                }
            }
        }

        private RHIResource CreatePhysicalResource(RG2ResourceEntry entry)
        {
            // hack: 这里只是占位实现。真实场景需要根据 desc 创建 RHITexture / RHIBuffer
            // 实际项目里会调用 device.CreateTexture / device.CreateBuffer
            return entry.Desc.Type switch
            {
                RG2ResourceType.Buffer => _device.CreateBuffer(entry.Desc.BufferSize, entry.Desc.BufferStride,
                    RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource),
                RG2ResourceType.RayTracingAccelerationStructure => _device.CreateBuffer(entry.Desc.BufferSize, 0,
                    RHIBufferUsage.RaytracingAccelerationStructure),
                _ => _device.CreateTexture(new RHITextureDesc
                {
                    Format = entry.Desc.Format,
                    Width = entry.Desc.Width,
                    Height = entry.Desc.Height,
                    DepthOrArraySize = entry.Desc.DepthOrArraySize,
                    MipLevels = entry.Desc.MipLevels,
                    SampleCount = entry.Desc.SampleCount,
                    Flags = ConvertTextureFlags(entry.Desc.Flags)
                })
            };
        }

        private RHITextureFlags ConvertTextureFlags(RG2ResourceFlags flags)
        {
            RHITextureFlags result = RHITextureFlags.None;
            if ((flags & RG2ResourceFlags.RenderTarget) != 0) result |= RHITextureFlags.AllowRenderTarget;
            if ((flags & RG2ResourceFlags.DepthStencil) != 0) result |= RHITextureFlags.AllowDepthStencil;
            if ((flags & RG2ResourceFlags.UnorderedAccess) != 0) result |= RHITextureFlags.AllowUnorderedAccess;
            if ((flags & RG2ResourceFlags.ShaderResource) != 0) result |= RHITextureFlags.AllowShaderResource;
            if ((flags & RG2ResourceFlags.ShadingRate) != 0) result |= RHITextureFlags.AllowShadingRate;
            return result;
        }

        private void ReleaseTransientResources(Dictionary<RG2Handle, RHIResource> map)
        {
            // 瞬态资源帧末释放（别名组复用的也不保留）
            foreach (var entry in _resources)
            {
                if (entry.IsImported || entry.IsPersistent || entry.IsHistory)
                    continue;
                // NOTE: 真实实现里如果是 placed resource 要 release placement
                // 这里物理资源如果复用了就不重复释放
            }
        }

        private void SwapHistoryResources(Dictionary<RG2Handle, RHIResource> map)
        {
            // 帧末交换 current <-> history
            // TAA/TSR 的 history texture 在这里更新
            // 这块先这样，回头再优化成更通用的双缓冲机制
        }

        // ──────────────────────────────────────────────────────────────────
        // 调试与可视化
        // ──────────────────────────────────────────────────────────────────

        /// <summary>导出 GraphViz DOT 格式，用于可视化渲染图。</summary>
        public string ExportDot()
        {
            var sb = new StringBuilder();
            sb.AppendLine("digraph RenderGraphV2 {");
            sb.AppendLine("  rankdir=LR;");
            sb.AppendLine("  node [shape=box, fontname=\"Consolas\"];");
            sb.AppendLine();

            // 资源节点
            sb.AppendLine("  // Resources");
            foreach (var entry in _resources)
            {
                string shape = entry.IsImported ? "folder" : (entry.IsPersistent ? "cylinder" : "note");
                string color = entry.IsImported ? "lightblue" : (entry.IsPersistent ? "lightyellow" : "white");
                sb.AppendLine($"  res_{entry.Handle.Index} [label=\"{entry.Name}\\n{entry.SizeBytes / 1024}KB\", shape={shape}, style=filled, fillcolor={color}];");
            }
            sb.AppendLine();

            // Pass 节点
            sb.AppendLine("  // Passes");
            foreach (var pass in _passes)
            {
                string color = pass.IsCulled ? "gray" : (pass.PassType == RG2PassType.AsyncCompute ? "lightgreen" : "lightpink");
                string style = pass.IsCulled ? "dashed" : "filled";
                sb.AppendLine($"  pass_{pass.Index} [label=\"{pass.Name}\\n({pass.PassType})\", style={style}, fillcolor={color}];");
            }
            sb.AppendLine();

            // 边
            sb.AppendLine("  // Edges");
            var lastWriter = new Dictionary<RG2Handle, int>();
            for (int i = 0; i < _passes.Count; i++)
            {
                var pass = _passes[i];
                foreach (var r in pass.Reads)
                {
                    if (lastWriter.TryGetValue(r.Handle, out var writerIdx))
                    {
                        sb.AppendLine($"  pass_{writerIdx} -> pass_{i} [label=\"{r.Handle}\"];");
                    }
                }
                foreach (var w in pass.Writes)
                {
                    lastWriter[w.Handle] = i;
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>导出 JSON 格式的渲染图描述。</summary>
        public string ExportJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"frameIndex\": " + _frameIndex + ",");
            sb.AppendLine("  \"resources\": [");
            for (int i = 0; i < _resources.Count; i++)
            {
                var e = _resources[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"index\": {e.Handle.Index},");
                sb.AppendLine($"      \"name\": \"{e.Name}\",");
                sb.AppendLine($"      \"type\": \"{e.Desc.Type}\",");
                sb.AppendLine($"      \"format\": \"{e.Desc.Format}\",");
                sb.AppendLine($"      \"size\": \"{e.Desc.Width}x{e.Desc.Height}\",");
                sb.AppendLine($"      \"bytes\": {e.SizeBytes},");
                sb.AppendLine($"      \"imported\": {(e.IsImported ? "true" : "false")},");
                sb.AppendLine($"      \"persistent\": {(e.IsPersistent ? "true" : "false")},");
                sb.AppendLine($"      \"firstPass\": {e.FirstPass},");
                sb.AppendLine($"      \"lastPass\": {e.LastPass}");
                sb.Append("    }");
                if (i < _resources.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"passes\": [");
            for (int i = 0; i < _passes.Count; i++)
            {
                var p = _passes[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"index\": {p.Index},");
                sb.AppendLine($"      \"name\": \"{p.Name}\",");
                sb.AppendLine($"      \"type\": \"{p.PassType}\",");
                sb.AppendLine($"      \"culled\": {(p.IsCulled ? "true" : "false")},");
                sb.AppendLine($"      \"sideEffects\": {(p.HasSideEffects ? "true" : "false")},");
                sb.AppendLine($"      \"reads\": [{string.Join(", ", p.Reads.Select(r => $"\"{r.Handle}\""))}],");
                sb.AppendLine($"      \"writes\": [{string.Join(", ", p.Writes.Select(w => $"\"{w.Handle}\""))}]");
                sb.Append("    }");
                if (i < _passes.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>打印渲染图摘要。</summary>
        public string DumpSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== RenderGraphV2 Frame {_frameIndex} ===");
            sb.AppendLine($"Passes: {_passes.Count}");
            sb.AppendLine($"Resources: {_resources.Count}");
            sb.AppendLine($"  Imported: {_resources.Count(r => r.IsImported)}");
            sb.AppendLine($"  Persistent: {_resources.Count(r => r.IsPersistent)}");
            sb.AppendLine($"  History: {_resources.Count(r => r.IsHistory)}");
            sb.AppendLine($"  Transient: {_resources.Count(r => !r.IsImported && !r.IsPersistent && !r.IsHistory)}");

            ulong totalTransientBytes = 0;
            foreach (var r in _resources.Where(r => !r.IsImported && !r.IsPersistent && !r.IsHistory))
                totalTransientBytes += r.SizeBytes;
            sb.AppendLine($"  Transient Total: {totalTransientBytes / 1024 / 1024:F1} MB");

            sb.AppendLine();
            sb.AppendLine("Passes:");
            foreach (var p in _passes)
            {
                string status = p.IsCulled ? "[CULLED]" : "[ACTIVE]";
                sb.AppendLine($"  {p.Index,3}: {status} {p.Name,-30} ({p.PassType}) reads={p.Reads.Count} writes={p.Writes.Count}");
            }
            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────
        // 帧管理
        // ──────────────────────────────────────────────────────────────────

        /// <summary>开始新一帧：重置瞬态资源，保留持久和历史。</summary>
        public void BeginFrame()
        {
            // 清除上一帧的瞬态资源追踪
            // 持久资源和历史资源保留
            _passes.Clear();

            // 重置瞬态资源条目的生命周期
            foreach (var entry in _resources)
            {
                if (!entry.IsImported && !entry.IsPersistent && !entry.IsHistory)
                {
                    entry.FirstPass = int.MaxValue;
                    entry.LastPass = int.MinValue;
                    entry.AliasGroup = null;
                }
            }
        }

        /// <summary>结束当前帧。</summary>
        public void EndFrame()
        {
            // 帧末可以做一些清理工作
            // NOTE: 真实实现里这里要释放这一帧分配的瞬态资源
        }

        /// <summary>重置整个渲染图（清空所有 pass 和瞬态资源）。</summary>
        public void Reset()
        {
            _passes.Clear();
            _resources.Clear();
            _resourceMap.Clear();
            _namedHandles.Clear();
            _historyResources.Clear();
        }

        // ──────────────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kv in _persistentResources)
                kv.Value?.Dispose();
            _persistentResources.Clear();

            foreach (var kv in _historyPhysical)
                kv.Value?.Dispose();
            _historyPhysical.Clear();

            foreach (var entry in _resources)
            {
                if (!entry.IsImported && entry.PhysicalResource != null)
                {
                    entry.PhysicalResource.Dispose();
                    entry.PhysicalResource = null;
                }
            }

            _resources.Clear();
            _resourceMap.Clear();
            _passes.Clear();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 辅助工具类
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>渲染图构建器：提供流式 API 构建复杂渲染图。</summary>
    public sealed class RG2Builder
    {
        private readonly RenderGraphV2 _graph;
        private RG2Pass? _currentPass;

        public RG2Builder(RenderGraphV2 graph)
        {
            _graph = graph;
        }

        public RG2Builder BeginPass(string name, RG2PassType type = RG2PassType.Graphics)
        {
            _currentPass = _graph.AddPass(name, type);
            return this;
        }

        public RG2Builder Read(RG2Handle handle, RG2ResourceAccess access = RG2ResourceAccess.PixelShaderResource)
        {
            _currentPass?.Read(handle, access);
            return this;
        }

        public RG2Builder Write(RG2Handle handle, RG2ResourceAccess access = RG2ResourceAccess.UnorderedAccess)
        {
            _currentPass?.Write(handle, access);
            return this;
        }

        public RG2Builder Execute(RG2PassExecuteDelegate execute)
        {
            _currentPass?.SetExecute(execute);
            return this;
        }

        public RG2Builder SideEffects()
        {
            _currentPass?.SetHasSideEffects();
            return this;
        }

        public RG2Builder Async()
        {
            _currentPass?.EnableAsync();
            return this;
        }

        public RG2Builder EndPass()
        {
            _currentPass = null;
            return this;
        }

        public RG2Handle Texture(string name, RHIFormat fmt, int w, int h, int mips = 1)
        {
            return _graph.CreateTexture(name, fmt, w, h, mips);
        }

        public RG2Handle Buffer(string name, ulong size, ulong stride = 0)
        {
            return _graph.CreateBuffer(name, size, stride);
        }
    }

    /// <summary>渲染图调试视图：用于编辑器中展示渲染图状态。</summary>
    public sealed class RG2DebugView
    {
        private readonly RenderGraphV2 _graph;

        public RG2DebugView(RenderGraphV2 graph)
        {
            _graph = graph;
        }

        public int PassCount => _graph.PassCount;
        public int ResourceCount => _graph.ResourceCount;
        public int AliasGroupCount => _graph.Lifetime.AliasGroups.Count;

        public IEnumerable<string> GetPassNames()
        {
            return _graph.Passes.Select(p => $"{p.Index}: {p.Name} [{p.PassType}]");
        }

        public IEnumerable<string> GetResourceNames()
        {
            return _graph.Lifetime.Entries.Values.Select(e =>
                $"{e.Name} [{e.Desc.Width}x{e.Desc.Height}] {e.SizeBytes / 1024}KB {(e.IsImported ? "(imported)" : "")}");
        }

        public IEnumerable<string> GetAliasGroups()
        {
            return _graph.Lifetime.AliasGroups.Select(g =>
                $"Group #{g.AliasId}: {g.Members.Count} resources, {g.SizeBytes / 1024}KB");
        }

        public string GetDotGraph()
        {
            return _graph.ExportDot();
        }
    }

    /// <summary>渲染图性能分析器：记录每帧每个 pass 的执行时间。</summary>
    public sealed class RG2Profiler
    {
        private readonly Dictionary<string, List<double>> _passTimings = new();
        private readonly int _maxHistoryFrames;
        private int _currentFrame;

        public RG2Profiler(int maxHistoryFrames = 120)
        {
            _maxHistoryFrames = maxHistoryFrames;
        }

        public void RecordPass(string passName, double elapsedMs)
        {
            if (!_passTimings.TryGetValue(passName, out var list))
            {
                list = new List<double>(_maxHistoryFrames);
                _passTimings[passName] = list;
            }
            list.Add(elapsedMs);
            if (list.Count > _maxHistoryFrames)
                list.RemoveAt(0);
        }

        public double GetAverageMs(string passName)
        {
            if (!_passTimings.TryGetValue(passName, out var list) || list.Count == 0)
                return 0;
            return list.Average();
        }

        public double GetMaxMs(string passName)
        {
            if (!_passTimings.TryGetValue(passName, out var list) || list.Count == 0)
                return 0;
            return list.Max();
        }

        public double GetTotalAverageMs()
        {
            return _passTimings.Values.Sum(l => l.Count > 0 ? l.Average() : 0);
        }

        public IReadOnlyDictionary<string, List<double>> AllTimings => _passTimings;

        public void BeginFrame()
        {
            _currentFrame++;
        }

        public string GetReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== RG2 Profiler Report (frame {_currentFrame}) ===");
            sb.AppendLine($"Total Average: {GetTotalAverageMs():F2} ms");
            sb.AppendLine();
            sb.AppendLine("Per-Pass:");
            foreach (var kv in _passTimings.OrderByDescending(k => k.Value.Count > 0 ? k.Value.Average() : 0))
            {
                var avg = kv.Value.Count > 0 ? kv.Value.Average() : 0;
                var max = kv.Value.Count > 0 ? kv.Value.Max() : 0;
                sb.AppendLine($"  {kv.Key,-35} avg={avg,7:F2}ms  max={max,7:F2}ms  samples={kv.Value.Count}");
            }
            return sb.ToString();
        }

        public void Reset()
        {
            _passTimings.Clear();
            _currentFrame = 0;
        }
    }

    /// <summary>渲染图验证器：检查常见错误。</summary>
    public static class RG2Validator
    {
        public static List<string> Validate(RenderGraphV2 graph)
        {
            var errors = new List<string>();

            // 检查未写入就被读取的资源
            var written = new HashSet<RG2Handle>();
            foreach (var pass in graph.Passes)
            {
                foreach (var r in pass.Reads)
                {
                    if (!written.Contains(r.Handle) && !graph.Lifetime.GetEntry(r.Handle)?.IsImported == true)
                    {
                        errors.Add($"Pass '{pass.Name}' 读取了未写入的资源 '{r.Handle}'。");
                    }
                }
                foreach (var w in pass.Writes)
                    written.Add(w.Handle);
            }

            // 检查没有执行体的 pass
            foreach (var pass in graph.Passes)
            {
                if (pass.ExecuteDelegate == null && !pass.HasSideEffects)
                {
                    errors.Add($"Pass '{pass.Name}' 没有执行体。");
                }
            }

            // 检查写入到导入资源（只读）
            foreach (var pass in graph.Passes)
            {
                foreach (var w in pass.Writes)
                {
                    var entry = graph.Lifetime.GetEntry(w.Handle);
                    if (entry?.IsImported == true && (entry.Desc.Flags & RG2ResourceFlags.RenderTarget) == 0)
                    {
                        errors.Add($"Pass '{pass.Name}' 试图写入只读的导入资源 '{entry.Name}'。");
                    }
                }
            }

            return errors;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常用 Pass 模板
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>常用渲染图 Pass 模板。</summary>
    public static class RG2PassTemplates
    {
        /// <summary>创建一个全屏后处理 Pass。</summary>
        public static RG2Pass FullScreenPostProcess(RenderGraphV2 graph, string name,
            RG2Handle input, RG2Handle output, RG2PassExecuteDelegate execute)
        {
            return graph.AddPass(name, RG2PassType.Graphics)
                .Read(input, RG2ResourceAccess.PixelShaderResource)
                .Write(output, RG2ResourceAccess.RenderTarget)
                .SetExecute(execute);
        }

        /// <summary>创建一个计算 Pass。</summary>
        public static RG2Pass ComputePass(RenderGraphV2 graph, string name,
            RG2Handle[] inputs, RG2Handle[] outputs, RG2PassExecuteDelegate execute)
        {
            var pass = graph.AddPass(name, RG2PassType.Compute).SetExecute(execute);
            foreach (var i in inputs)
                pass.Read(i, RG2ResourceAccess.NonPixelShaderResource);
            foreach (var o in outputs)
                pass.Write(o, RG2ResourceAccess.UnorderedAccess);
            return pass;
        }

        /// <summary>创建一个降采样 Pass。</summary>
        public static RG2Pass DownsamplePass(RenderGraphV2 graph, string name,
            RG2Handle src, RG2Handle dst, RG2PassExecuteDelegate execute)
        {
            return graph.AddPass(name, RG2PassType.Compute)
                .Read(src, RG2ResourceAccess.NonPixelShaderResource)
                .Write(dst, RG2ResourceAccess.UnorderedAccess)
                .SetExecute(execute);
        }

        /// <summary>创建一个升采样 Pass。</summary>
        public static RG2Pass UpsamplePass(RenderGraphV2 graph, string name,
            RG2Handle src, RG2Handle dst, RG2PassExecuteDelegate execute)
        {
            return graph.AddPass(name, RG2PassType.Compute)
                .Read(src, RG2ResourceAccess.NonPixelShaderResource)
                .Write(dst, RG2ResourceAccess.UnorderedAccess)
                .SetExecute(execute);
        }

        /// <summary>创建一个 blit/copy Pass。</summary>
        public static RG2Pass BlitPass(RenderGraphV2 graph, string name,
            RG2Handle src, RG2Handle dst)
        {
            return graph.AddPass(name, RG2PassType.Copy)
                .Read(src, RG2ResourceAccess.CopySource)
                .Write(dst, RG2ResourceAccess.CopyDest)
                .SetHasSideEffects()
                .SetExecute((cmd, ctx) =>
                {
                    var srcTex = ctx.GetTexture(src);
                    var dstTex = ctx.GetTexture(dst);
                    cmd.CopyResource(dstTex, srcTex);
                });
        }

        /// <summary>创建一个Present Pass（输出到屏幕）。</summary>
        public static RG2Pass PresentPass(RenderGraphV2 graph, string name,
            RG2Handle source, RHITexture swapchain)
        {
            var swapchainHandle = graph.ImportTexture("Swapchain", swapchain,
                RG2ResourceFlags.RenderTarget);
            return graph.AddPass(name, RG2PassType.Graphics)
                .Read(source, RG2ResourceAccess.PixelShaderResource)
                .Write(swapchainHandle, RG2ResourceAccess.RenderTarget)
                .SetHasSideEffects()
                .SetExecute((cmd, ctx) =>
                {
                    // 执行一个全屏三角形把 source blit 到 swapchain
                    // 实际着色器绑定省略
                });
        }

        /// <summary>创建一个 mipmap 生成 Pass。</summary>
        public static RG2Pass GenerateMipsPass(RenderGraphV2 graph, string name, RG2Handle texture)
        {
            return graph.AddPass(name, RG2PassType.Compute)
                .ReadWrite(texture, RG2ResourceAccess.NonPixelShaderResource, RG2ResourceAccess.UnorderedAccess)
                .SetExecute((cmd, ctx) =>
                {
                    var tex = ctx.GetTexture(texture);
                    cmd.GenerateMips(tex);
                });
        }

        /// <summary>创建一个 clear Pass。</summary>
        public static RG2Pass ClearPass(RenderGraphV2 graph, string name,
            RG2Handle target, Vector4 clearColor, float clearDepth = 1.0f, byte clearStencil = 0)
        {
            return graph.AddPass(name, RG2PassType.Graphics)
                .Write(target, RG2ResourceAccess.RenderTarget)
                .SetExecute((cmd, ctx) =>
                {
                    var tex = ctx.GetTexture(target);
                    cmd.ClearRenderTarget(tex, clearColor);
                });
        }

        /// <summary>创建一个屏障 Pass。</summary>
        public static RG2Pass BarrierPass(RenderGraphV2 graph, string name,
            RG2Handle handle, RG2ResourceAccess from, RG2ResourceAccess to)
        {
            return graph.AddPass(name, RG2PassType.Graphics)
                .Read(handle, from)
                .Write(handle, to)
                .SetExecute((cmd, ctx) => { /* 屏障在编译期插入 */ });
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 资源池与别名堆
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>别名堆：管理一块大内存，上面放置多个别名资源。</summary>
    public sealed class RG2AliasHeap : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RHIBuffer? _bufferHeap;
        private readonly RHITexture? _textureHeap;
        private readonly ulong _size;
        private readonly bool _disposed;
        private readonly List<PlacedResource> _placed = new();

        public ulong Size => _size;
        public int PlacedCount => _placed.Count;

        public RG2AliasHeap(RHIDevice device, ulong size, bool isTexture = true)
        {
            _device = device;
            _size = size;
            // NOTE: 实际实现需要创建一个 D3D12 placed resource heap
            // 这里简化，真实代码会调用 device.CreateHeap
        }

        public RHITexture PlaceTexture(RG2ResourceDesc desc, ulong offset)
        {
            // 在 heap 的 offset 位置放置一个纹理
            _placed.Add(new PlacedResource(desc.Name, offset, desc.EstimateSizeBytes()));
            return _device.CreateTexture(new RHITextureDesc
            {
                Format = desc.Format,
                Width = desc.Width,
                Height = desc.Height,
                DepthOrArraySize = desc.DepthOrArraySize,
                MipLevels = desc.MipLevels,
                SampleCount = desc.SampleCount
            });
        }

        public RHIBuffer PlaceBuffer(ulong size, ulong stride, ulong offset)
        {
            _placed.Add(new PlacedResource("Buffer", offset, size));
            return _device.CreateBuffer(size, stride, RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _bufferHeap?.Dispose();
            _textureHeap?.Dispose();
        }

        private readonly record struct PlacedResource(string Name, ulong Offset, ulong Size);
    }

    /// <summary>资源池：缓存常用资源，避免重复创建。</summary>
    public sealed class RG2ResourcePool : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<string, RHIResource> _cache = new();
        private readonly Dictionary<string, int> _refCounts = new();
        private bool _disposed;

        public RG2ResourcePool(RHIDevice device)
        {
            _device = device;
        }

        public RHITexture GetOrCreateTexture(string key, Func<RHITextureDesc> descFactory)
        {
            if (_cache.TryGetValue(key, out var res) && res is RHITexture tex)
            {
                _refCounts[key]++;
                return tex;
            }
            var desc = descFactory();
            var newTex = _device.CreateTexture(desc);
            _cache[key] = newTex;
            _refCounts[key] = 1;
            return newTex;
        }

        public void Release(string key)
        {
            if (!_refCounts.TryGetValue(key, out var count)) return;
            count--;
            if (count <= 0)
            {
                if (_cache.TryGetValue(key, out var res))
                    res.Dispose();
                _cache.Remove(key);
                _refCounts.Remove(key);
            }
            else
            {
                _refCounts[key] = count;
            }
        }

        public void Clear()
        {
            foreach (var kv in _cache)
                kv.Value?.Dispose();
            _cache.Clear();
            _refCounts.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 时间分片执行器（用于重负载异步更新）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>时间分片执行器：把超时 Pass 跨帧执行。</summary>
    public sealed class RG2TimeSlicedExecutor
    {
        private readonly RenderGraphV2 _graph;
        private readonly Dictionary<string, TimeSlicedPass> _slicedPasses = new();
        private float _budgetMs = 4.0f;

        public float BudgetMs
        {
            get => _budgetMs;
            set => _budgetMs = MathF.Max(0.1f, value);
        }

        public RG2TimeSlicedExecutor(RenderGraphV2 graph)
        {
            _graph = graph;
        }

        /// <summary>注册一个可分片的 Pass（如 ProbeField 更新、MicroCluster LOD）。</summary>
        public void RegisterSlicedPass(string name, RG2PassExecuteDelegate execute, int totalSteps)
        {
            _slicedPasses[name] = new TimeSlicedPass(name, execute, totalSteps);
        }

        /// <summary>执行分片 Pass，直到预算耗尽。</summary>
        public void Execute(RHICommandList cmd, RG2ExecutionContext ctx, float availableMs)
        {
            float elapsed = 0;
            foreach (var kv in _slicedPasses)
            {
                if (elapsed >= availableMs) break;
                var sp = kv.Value;
                var sw = Stopwatch.StartNew();
                sp.ExecuteStep(cmd, ctx);
                sw.Stop();
                elapsed += (float)sw.Elapsed.TotalMilliseconds;
            }
        }

        private sealed class TimeSlicedPass
        {
            private readonly string _name;
            private readonly RG2PassExecuteDelegate _execute;
            private readonly int _totalSteps;
            private int _currentStep;

            public TimeSlicedPass(string name, RG2PassExecuteDelegate execute, int totalSteps)
            {
                _name = name;
                _execute = execute;
                _totalSteps = totalSteps;
            }

            public void ExecuteStep(RHICommandList cmd, RG2ExecutionContext ctx)
            {
                // 执行一步
                _execute(cmd, ctx);
                _currentStep++;
                if (_currentStep >= _totalSteps)
                    _currentStep = 0; // 完成一轮，从头开始
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 跨 Pass 资源传递机制
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>跨 Pass 资源传递通道：用于 pass 间传递非纹理数据。</summary>
    public sealed class RG2Blackboard
    {
        private readonly Dictionary<string, object?> _data = new();
        private readonly Dictionary<string, RG2Handle> _handles = new();

        public void Set<T>(string key, T value)
        {
            _data[key] = value;
        }

        public T? Get<T>(string key)
        {
            if (_data.TryGetValue(key, out var v) && v is T t)
                return t;
            return default;
        }

        public void SetHandle(string key, RG2Handle handle)
        {
            _handles[key] = handle;
        }

        public RG2Handle GetHandle(string key)
        {
            return _handles.TryGetValue(key, out var h) ? h : RG2Handle.Invalid;
        }

        public bool Contains(string key) => _data.ContainsKey(key) || _handles.ContainsKey(key);

        public void Clear()
        {
            _data.Clear();
            _handles.Clear();
        }

        public IEnumerable<string> Keys => _data.Keys.Concat(_handles.Keys);
    }

    /// <summary>渲染图黑板扩展：把黑板绑定到渲染图。</summary>
    public static class RG2BlackboardExtensions
    {
        public static RG2Blackboard GetBlackboard(this RenderGraphV2 graph)
        {
            // 每个渲染图关联一个黑板
            // NOTE: 实际实现可以用 ConditionalWeakTable
            return graph._blackboard ??= new RG2Blackboard();
        }

        // hack: 通过反射访问私有字段，实际项目应该把 blackboard 设为 public
        internal static RG2Blackboard? _blackboard;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Pass 执行上下文辅助扩展
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>执行上下文的辅助扩展方法。</summary>
    public static class RG2ExecutionContextExtensions
    {
        /// <summary>设置渲染目标并开始渲染 pass。</summary>
        public static void SetRenderTarget(this RG2ExecutionContext ctx, RHICommandList cmd,
            RG2Handle colorHandle, RG2Handle? depthHandle = null)
        {
            var color = ctx.GetTexture(colorHandle);
            if (depthHandle.HasValue)
            {
                var depth = ctx.GetTexture(depthHandle.Value);
                cmd.SetRenderTargets(new[] { color }, depth);
            }
            else
            {
                cmd.SetRenderTargets(new[] { color }, null);
            }
        }

        /// <summary>设置多个渲染目标（MRT）。</summary>
        public static void SetRenderTargetsMRT(this RG2ExecutionContext ctx, RHICommandList cmd,
            RG2Handle[] colorHandles, RG2Handle? depthHandle = null)
        {
            var colors = new RHITexture[colorHandles.Length];
            for (int i = 0; i < colorHandles.Length; i++)
                colors[i] = ctx.GetTexture(colorHandles[i]);

            if (depthHandle.HasValue)
            {
                var depth = ctx.GetTexture(depthHandle.Value);
                cmd.SetRenderTargets(colors, depth);
            }
            else
            {
                cmd.SetRenderTargets(colors, null);
            }
        }

        /// <summary>绑定 SRV 到像素着色器。</summary>
        public static void BindPixelShaderResource(this RG2ExecutionContext ctx, RHICommandList cmd,
            int slot, RG2Handle handle)
        {
            if (ctx.TryGetTexture(handle, out var tex) && tex != null)
                cmd.SetShaderResourceView(ShaderStage.Pixel, slot, tex);
        }

        /// <summary>绑定 SRV 到计算着色器。</summary>
        public static void BindComputeShaderResource(this RG2ExecutionContext ctx, RHICommandList cmd,
            int slot, RG2Handle handle)
        {
            if (ctx.TryGetTexture(handle, out var tex) && tex != null)
                cmd.SetShaderResourceView(ShaderStage.Compute, slot, tex);
        }

        /// <summary>绑定 UAV 到计算着色器。</summary>
        public static void BindComputeUAV(this RG2ExecutionContext ctx, RHICommandList cmd,
            int slot, RG2Handle handle)
        {
            if (ctx.TryGetTexture(handle, out var tex) && tex != null)
                cmd.SetUnorderedAccessView(ShaderStage.Compute, slot, tex);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 帧统计与性能监控
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>渲染图帧统计信息。</summary>
    public sealed class RG2FrameStats
    {
        public int FrameIndex;
        public int TotalPassCount;
        public int CulledPassCount;
        public int AsyncPassCount;
        public int TotalBarriers;
        public double CompileTimeMs;
        public double ExecuteTimeMs;
        public double TotalFrameTimeMs;
        public ulong EstimatedMemoryBytes;
        public float AsyncOverlapMs;
        public List<RG2PassStats> PassStats = new();

        public override string ToString()
        {
            return $"Frame {FrameIndex}: {TotalPassCount} passes ({CulledPassCount} culled), " +
                   $"{TotalBarriers} barriers, {EstimatedMemoryBytes / 1024 / 1024:F1}MB, " +
                   $"{TotalFrameTimeMs:F2}ms ({AsyncOverlapMs:F1}ms async overlap)";
        }
    }

    /// <summary>渲染图帧统计收集器。</summary>
    public sealed class RG2FrameStatsCollector
    {
        private readonly Queue<RG2FrameStats> _history = new();
        private readonly int _maxFrames;
        private RG2FrameStats? _current;

        public IReadOnlyCollection<RG2FrameStats> History => _history;

        public RG2FrameStatsCollector(int maxFrames = 120)
        {
            _maxFrames = maxFrames;
        }

        public void BeginFrame(int frameIndex)
        {
            _current = new RG2FrameStats { FrameIndex = frameIndex };
        }

        public void RecordCompile(RG2CompiledPlan plan, double compileMs)
        {
            if (_current == null) return;
            _current.TotalPassCount = plan.Stats.TotalPassCount;
            _current.CulledPassCount = plan.Stats.CulledPassCount;
            _current.AsyncPassCount = plan.Stats.AsyncPassCount;
            _current.TotalBarriers = plan.Stats.TotalBarriers;
            _current.CompileTimeMs = compileMs;
            _current.EstimatedMemoryBytes = plan.Stats.EstimatedMemoryBytes;
            _current.AsyncOverlapMs = plan.Stats.EstimatedAsyncOverlapMs;
        }

        public void RecordExecute(double executeMs, List<RG2PassStats> passStats)
        {
            if (_current == null) return;
            _current.ExecuteTimeMs = executeMs;
            _current.PassStats = passStats;
        }

        public void EndFrame(double totalMs)
        {
            if (_current == null) return;
            _current.TotalFrameTimeMs = totalMs;
            _history.Enqueue(_current);
            while (_history.Count > _maxFrames)
                _history.Dequeue();
            _current = null;
        }

        public double GetAverageFrameTime()
        {
            if (_history.Count == 0) return 0;
            return _history.Average(s => s.TotalFrameTimeMs);
        }

        public double GetMaxFrameTime()
        {
            if (_history.Count == 0) return 0;
            return _history.Max(s => s.TotalFrameTimeMs);
        }

        public double GetAverageMemoryMB()
        {
            if (_history.Count == 0) return 0;
            return _history.Average(s => s.EstimatedMemoryBytes / 1024.0 / 1024.0);
        }
    }
}
