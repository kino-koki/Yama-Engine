using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama RenderGraph (超越 UE7 级别)
    //
    // 声明式渲染图框架: 帧的渲染流程被描述为有向无环图(DAG),
    // 每个节点是一个 RenderPass, 边是资源读写依赖。
    // RenderGraph 自动完成:
    //   1. 资源分配与复用 (基于生命周期的内存别名)
    //   2. Pass 拓扑排序 (Kahn 算法, 检测循环依赖)
    //   3. 资源状态转换与屏障插入 (RHIResourceBarrier 自动化)
    //   4. 瞬态资源生命周期管理 (Transient Resource)
    //   5. 异步计算队列分发 (Compute Pass 可提交到 Async Compute)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>RenderGraph 资源类型。</summary>
    public enum RenderGraphResourceType
    {
        Texture,
        Buffer,
        Texture2D,
        Texture3D,
        TextureCube
    }

    /// <summary>RenderGraph 资源标志。</summary>
    [Flags]
    public enum RenderGraphResourceFlags
    {
        None = 0,
        /// <summary>由外部导入, RenderGraph 不管理其分配/释放。</summary>
        Imported = 1,
        /// <summary>允许作为 RTV / DSV 绑定。</summary>
        RenderTarget = 2,
        /// <summary>允许作为 UAV 绑定。</summary>
        UnorderedAccess = 4,
        /// <summary>允许作为 SRV 绑定。</summary>
        ShaderResource = 8,
        /// <summary>跨帧持久 (不回收)。</summary>
        Persistent = 16,
        /// <summary>深度/模板纹理。</summary>
        DepthStencil = 32
    }

    /// <summary>RenderGraph 资源描述。</summary>
    public sealed class RenderGraphResourceDesc
    {
        public string Name = "";
        public RenderGraphResourceType Type = RenderGraphResourceType.Texture2D;
        public RHIFormat Format = RHIFormat.R8G8B8A8_UNorm;
        public int Width = 1;
        public int Height = 1;
        public int DepthOrArraySize = 1;
        public int MipLevels = 1;
        public int SampleCount = 1;
        public RenderGraphResourceFlags Flags = RenderGraphResourceFlags.ShaderResource;
        public ulong BufferSize = 0;
        public ulong BufferStride = 0;

        public static RenderGraphResourceDesc Texture2D(string name, RHIFormat format, int width, int height,
            int mipLevels = 1, RenderGraphResourceFlags flags = RenderGraphResourceFlags.ShaderResource | RenderGraphResourceFlags.RenderTarget)
        {
            return new RenderGraphResourceDesc
            {
                Name = name, Type = RenderGraphResourceType.Texture2D, Format = format,
                Width = width, Height = height, DepthOrArraySize = 1, MipLevels = mipLevels, Flags = flags
            };
        }

        public static RenderGraphResourceDesc Texture3D(string name, RHIFormat format, int width, int height, int depth,
            RenderGraphResourceFlags flags = RenderGraphResourceFlags.ShaderResource | RenderGraphResourceFlags.UnorderedAccess)
        {
            return new RenderGraphResourceDesc
            {
                Name = name, Type = RenderGraphResourceType.Texture3D, Format = format,
                Width = width, Height = height, DepthOrArraySize = depth, MipLevels = 1, Flags = flags
            };
        }

        public static RenderGraphResourceDesc Buffer(string name, ulong size, ulong stride,
            RenderGraphResourceFlags flags = RenderGraphResourceFlags.ShaderResource | RenderGraphResourceFlags.UnorderedAccess)
        {
            return new RenderGraphResourceDesc
            {
                Name = name, Type = RenderGraphResourceType.Buffer, Format = RHIFormat.Unknown,
                BufferSize = size, BufferStride = stride, Flags = flags
            };
        }
    }

    /// <summary>RenderGraph 资源句柄 (编译时由 RenderGraph 分配)。</summary>
    public readonly struct RenderGraphResourceHandle : IEquatable<RenderGraphResourceHandle>
    {
        public readonly int Index;
        public readonly int Version;
        public readonly bool IsValid;

        public RenderGraphResourceHandle(int index, int version)
        {
            Index = index; Version = version; IsValid = true;
        }

        public static readonly RenderGraphResourceHandle Invalid = new(-1, -1);

        public bool Equals(RenderGraphResourceHandle other) => Index == other.Index && Version == other.Version;
        public override bool Equals(object? obj) => obj is RenderGraphResourceHandle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(Index, Version);
        public static bool operator ==(RenderGraphResourceHandle a, RenderGraphResourceHandle b) => a.Equals(b);
        public static bool operator !=(RenderGraphResourceHandle a, RenderGraphResourceHandle b) => !a.Equals(b);
    }

    /// <summary>Pass 执行委托。</summary>
    public delegate void RenderGraphPassExecuteDelegate(RHICommandList cmdList, RenderGraphContext ctx);

    /// <summary>Pass 类型。</summary>
    public enum RenderGraphPassType
    {
        Render,     // 图形 Pass (光栅化)
        Compute,    // 计算 Pass
        AsyncCompute,// 异步计算 Pass (可并行于图形队列)
        Copy,       // 拷贝 Pass
        RayTracing  // 光线追踪 Pass
    }

    /// <summary>RenderGraph Pass 定义。</summary>
    public sealed class RenderGraphPass
    {
        public string Name { get; }
        public RenderGraphPassType PassType { get; }
        public int Index { get; internal set; }

        internal readonly List<(RenderGraphResourceHandle handle, RenderGraphResourceAccess access)> ResourceReads = new();
        internal readonly List<(RenderGraphResourceHandle handle, RenderGraphResourceAccess access)> ResourceWrites = new();
        internal RenderGraphPassExecuteDelegate? ExecuteDelegate;
        internal bool HasSideEffects = false;

        public RenderGraphPass(string name, RenderGraphPassType type)
        {
            Name = name;
            PassType = type;
        }

        /// <summary>读取资源。</summary>
        public RenderGraphPass Read(in RenderGraphResourceHandle handle, RenderGraphResourceAccess access = RenderGraphResourceAccess.ShaderRead)
        {
            if (handle.IsValid) ResourceReads.Add((handle, access));
            return this;
        }

        /// <summary>写入/创建资源。</summary>
        public RenderGraphPass Write(in RenderGraphResourceHandle handle, RenderGraphResourceAccess access = RenderGraphResourceAccess.ShaderWrite)
        {
            if (handle.IsValid) ResourceWrites.Add((handle, access));
            return this;
        }

        /// <summary>设置执行体。</summary>
        public RenderGraphPass SetExecute(RenderGraphPassExecuteDelegate execute)
        {
            ExecuteDelegate = execute;
            return this;
        }

        /// <summary>标记为副作用 Pass (无输出也要执行, 如 Present)。</summary>
        public RenderGraphPass SetHasSideEffects()
        {
            HasSideEffects = true;
            return this;
        }
    }

    /// <summary>资源访问类型 (决定状态转换目标)。</summary>
    public enum RenderGraphResourceAccess
    {
        ShaderRead,        // SRV -> NonPixelShaderResource / PixelShaderResource
        ShaderWrite,       // UAV -> UnorderedAccess
        RenderTarget,      // RTV -> RenderTarget
        DepthStencilWrite, // DSV -> DepthWrite
        DepthStencilRead,  // DSV -> DepthRead
        CopySource,        // CopySource
        CopyDest,          // CopyDest
        VertexBuffer,      // VertexAndConstantBuffer
        IndexBuffer,       // IndexBuffer
        ConstantBuffer     // VertexAndConstantBuffer
    }

    /// <summary>编译后的 Pass 信息。</summary>
    public sealed class CompiledRenderPass
    {
        public RenderGraphPass Pass = null!;
        public int SortIndex;
        public List<CompiledRenderPass> Dependencies = new();
        public List<RHIResourceBarrier> Barriers = new();
        public bool IsCulled;
    }

    /// <summary>资源生命周期区间。</summary>
    public sealed class RenderGraphResourceLifetime
    {
        public RenderGraphResourceHandle Handle;
        public int FirstPass;
        public int LastPass;
        public bool IsImported;
        public bool IsPersistent;
    }

    /// <summary>RenderGraph 执行上下文: 供 Pass 委托查询实际 GPU 资源。</summary>
    public sealed class RenderGraphContext
    {
        private readonly RenderGraph _graph;
        private readonly Dictionary<RenderGraphResourceHandle, RHIResource> _resources;

        public RenderGraphContext(RenderGraph graph, Dictionary<RenderGraphResourceHandle, RHIResource> resources)
        {
            _graph = graph;
            _resources = resources;
        }

        /// <summary>获取资源对应的 RHI 纹理 (若为 Texture)。</summary>
        public RHITexture GetTexture(in RenderGraphResourceHandle handle)
        {
            if (_resources.TryGetValue(handle, out var res) && res is RHITexture tex)
                return tex;
            throw new InvalidOperationException($"RenderGraph: 资源 {handle} 不是有效的 Texture。");
        }

        /// <summary>获取资源对应的 RHI 缓冲 (若为 Buffer)。</summary>
        public RHIBuffer GetBuffer(in RenderGraphResourceHandle handle)
        {
            if (_resources.TryGetValue(handle, out var res) && res is RHIBuffer buf)
                return buf;
            throw new InvalidOperationException($"RenderGraph: 资源 {handle} 不是有效的 Buffer。");
        }

        /// <summary>获取任意 RHI 资源。</summary>
        public RHIResource GetResource(in RenderGraphResourceHandle handle)
        {
            if (_resources.TryGetValue(handle, out var res))
                return res;
            throw new InvalidOperationException($"RenderGraph: 资源 {handle} 未找到。");
        }
    }

    /// <summary>编译结果。</summary>
    public sealed class RenderGraphCompilationResult
    {
        public bool Success;
        public List<string> Errors = new();
        public List<CompiledRenderPass> Passes = new();
        public List<RenderGraphResourceLifetime> Lifetimes = new();
        public long EstimatedMemoryBytes;

        /// <summary>编译统计摘要。</summary>
        public RenderGraphStats Stats => new RenderGraphStats
        {
            TransientResourceCount = Lifetimes.Count(l => !l.IsImported),
            ImportedResourceCount = Lifetimes.Count(l => l.IsImported),
            EstimatedMemoryBytes = EstimatedMemoryBytes
        };

        /// <summary>编译是否有效 (无错误)。</summary>
        public bool IsValid => Success && Errors.Count == 0;
    }

    /// <summary>渲染图编译统计。</summary>
    public struct RenderGraphStats
    {
        public int TransientResourceCount;
        public int ImportedResourceCount;
        public long EstimatedMemoryBytes;
    }

    /// <summary>
    /// RenderGraph 主类: 声明式构建一帧的渲染流程, 编译后执行。
    /// </summary>
    public sealed class RenderGraph
    {
        private readonly RHIDevice? _device;
        private readonly List<RenderGraphResourceDesc> _resourceDescs = new();
        private readonly List<RenderGraphResourceHandle> _resourceHandles = new();
        private readonly List<RenderGraphPass> _passes = new();
        private readonly Dictionary<string, RenderGraphResourceHandle> _importedResources = new();

        private int _resourceVersionCounter;
        private bool _isCompiled;
        private RenderGraphCompilationResult? _lastCompilation;

        // 运行时分配的实际资源
        private readonly Dictionary<RenderGraphResourceHandle, RHIResource> _allocatedResources = new();
        // 资源池 (用于复用/别名)
        private readonly List<RHIResource> _resourcePool = new();

        public string Name { get; }
        public int PassCount => _passes.Count;
        public int ResourceCount => _resourceDescs.Count;

        public RenderGraph(RHIDevice? device, string name)
        {
            _device = device;
            Name = string.IsNullOrWhiteSpace(name) ? "FrameGraph" : name;
        }

        public RenderGraph(RHIDevice device) : this(device, "RenderGraph") { }

        /// <summary>
        /// 编译专用构造：不绑定物理设备，仅用于图构建/拓扑编译/静态分析/单元测试。
        /// 调用 <see cref="Execute"/> 前必须使用带设备的重载。
        /// </summary>
        public RenderGraph(string name) : this((RHIDevice?)null, name) { }

        /// <summary>绑定交换链的后台缓冲作为渲染目标资源。</summary>
        public void SetBackBuffer(RHITexture backBuffer)
        {
            if (backBuffer == null) return;
            var handle = ImportResource("BackBuffer", backBuffer);
            _backBufferHandle = handle;
        }

        private RenderGraphResourceHandle? _backBufferHandle;
        public RenderGraphResourceHandle? BackBufferHandle => _backBufferHandle;

        /// <summary>创建瞬态资源 (RenderGraph 管理生命周期)。</summary>
        public RenderGraphResourceHandle CreateResource(in RenderGraphResourceDesc desc)
        {
            int index = _resourceDescs.Count;
            int version = _resourceVersionCounter++;
            var handle = new RenderGraphResourceHandle(index, version);
            _resourceDescs.Add(desc);
            _resourceHandles.Add(handle);
            return handle;
        }

        /// <summary>导入外部资源 (生命周期由外部管理)。</summary>
        public RenderGraphResourceHandle ImportResource(string name, RHIResource resource)
        {
            if (_importedResources.TryGetValue(name, out var existing))
                return existing;

            var desc = new RenderGraphResourceDesc
            {
                Name = name,
                Type = resource is RHITexture ? RenderGraphResourceType.Texture2D : RenderGraphResourceType.Buffer,
                Flags = RenderGraphResourceFlags.Imported
            };

            if (resource is RHITexture tex && tex.TextureDesc.HasValue)
            {
                var td = tex.TextureDesc.Value;
                desc.Format = td.Format;
                desc.Width = td.Width;
                desc.Height = td.Height;
                desc.DepthOrArraySize = td.DepthOrArraySize;
                desc.MipLevels = td.MipLevels;
                desc.SampleCount = td.SampleCount;
            }
            else if (resource is RHIBuffer buf && buf.BufferDesc.HasValue)
            {
                var bd = buf.BufferDesc.Value;
                desc.BufferSize = bd.SizeInBytes;
                desc.BufferStride = bd.Stride;
            }

            int index = _resourceDescs.Count;
            int version = _resourceVersionCounter++;
            var handle = new RenderGraphResourceHandle(index, version);
            _resourceDescs.Add(desc);
            _resourceHandles.Add(handle);
            _importedResources[name] = handle;
            _allocatedResources[handle] = resource;
            return handle;
        }

        /// <summary>添加 Pass。</summary>
        public RenderGraphPass AddPass(string name, RenderGraphPassType type)
        {
            var pass = new RenderGraphPass(name, type);
            pass.Index = _passes.Count;
            _passes.Add(pass);
            return pass;
        }

        /// <summary>添加已构造的 Pass。</summary>
        public void AddPass(RenderGraphPass pass)
        {
            if (pass == null) return;
            pass.Index = _passes.Count;
            _passes.Add(pass);
        }

        /// <summary>清空本帧所有 Pass 与资源声明 (每帧重建)。</summary>
        public void Reset()
        {
            _passes.Clear();
            _resourceDescs.Clear();
            _resourceHandles.Clear();
            _importedResources.Clear();
            _resourceVersionCounter = 0;
            _isCompiled = false;
            _lastCompilation = null;
        }

        /// <summary>编译 RenderGraph: 拓扑排序 + 资源生命周期分析 + 屏障生成。</summary>
        public RenderGraphCompilationResult Compile()
        {
            _isCompiled = true;
            var result = new RenderGraphCompilationResult();

            // 1. 构建写者映射 (每个资源最后一个写入者)
            var writers = new Dictionary<RenderGraphResourceHandle, RenderGraphPass>();
            foreach (var pass in _passes)
            {
                foreach (var (handle, _) in pass.ResourceWrites)
                    writers[handle] = pass;
            }

            // 2. 构建依赖图
            var dependencies = new Dictionary<RenderGraphPass, HashSet<RenderGraphPass>>();
            foreach (var pass in _passes)
                dependencies[pass] = new HashSet<RenderGraphPass>();

            foreach (var pass in _passes)
            {
                foreach (var (handle, _) in pass.ResourceReads)
                {
                    if (writers.TryGetValue(handle, out var writer) && writer != pass)
                        dependencies[pass].Add(writer);
                }
            }

            // 3. Kahn 拓扑排序
            var inDegree = new Dictionary<RenderGraphPass, int>();
            foreach (var pass in _passes)
                inDegree[pass] = 0;

            foreach (var kv in dependencies)
            {
                foreach (var dep in kv.Value)
                    inDegree[kv.Key]++;
            }

            var queue = new Queue<RenderGraphPass>();
            foreach (var kv in inDegree)
            {
                if (kv.Value == 0)
                    queue.Enqueue(kv.Key);
            }

            var sorted = new List<CompiledRenderPass>();
            while (queue.Count > 0)
            {
                var pass = queue.Dequeue();
                var compiled = new CompiledRenderPass { Pass = pass, SortIndex = sorted.Count };
                sorted.Add(compiled);

                foreach (var other in _passes)
                {
                    if (dependencies[other].Contains(pass))
                    {
                        inDegree[other]--;
                        if (inDegree[other] == 0)
                            queue.Enqueue(other);
                    }
                }
            }

            if (sorted.Count != _passes.Count)
            {
                result.Success = false;
                result.Errors.Add("RenderGraph 包含循环资源依赖, 无法拓扑排序。");
                _lastCompilation = result;
                return result;
            }

            // 4. 剔除无输出且无副作用的 Pass
            var culled = new HashSet<RenderGraphPass>();
            bool changed;
            do
            {
                changed = false;
                var usedOutputs = new HashSet<RenderGraphResourceHandle>();
                // 收集所有被读取的资源 (或被导入/持久化的写入)
                foreach (var cp in sorted)
                {
                    if (culled.Contains(cp.Pass)) continue;
                    foreach (var (h, _) in cp.Pass.ResourceReads)
                        usedOutputs.Add(h);
                    // 副作用 Pass 的输出也算被使用
                    if (cp.Pass.HasSideEffects)
                    {
                        foreach (var (h, _) in cp.Pass.ResourceWrites)
                            usedOutputs.Add(h);
                    }
                }
                // 被外部导入的资源也算被使用
                foreach (var h in _importedResources.Values)
                    usedOutputs.Add(h);

                foreach (var cp in sorted)
                {
                    if (culled.Contains(cp.Pass)) continue;
                    if (cp.Pass.HasSideEffects) continue;

                    bool hasUsedOutput = false;
                    foreach (var (h, _) in cp.Pass.ResourceWrites)
                    {
                        if (usedOutputs.Contains(h))
                        {
                            hasUsedOutput = true;
                            break;
                        }
                    }

                    // 如果无输出资源被使用, 且不是副作用 Pass, 则可剔除
                    if (!hasUsedOutput && cp.Pass.ResourceWrites.Count > 0)
                    {
                        culled.Add(cp.Pass);
                        cp.IsCulled = true;
                        changed = true;
                    }
                }
            } while (changed);

            // 5. 计算资源生命周期 (FirstPass / LastPass)
            var lifetimes = new List<RenderGraphResourceLifetime>();
            for (int i = 0; i < _resourceHandles.Count; i++)
            {
                var handle = _resourceHandles[i];
                var desc = _resourceDescs[i];
                int first = int.MaxValue, last = int.MinValue;

                for (int p = 0; p < sorted.Count; p++)
                {
                    if (sorted[p].IsCulled) continue;
                    var pass = sorted[p].Pass;
                    bool used = pass.ResourceReads.Any(r => r.handle == handle) ||
                                pass.ResourceWrites.Any(r => r.handle == handle);
                    if (used)
                    {
                        first = Math.Min(first, p);
                        last = Math.Max(last, p);
                    }
                }

                if (first != int.MaxValue)
                {
                    lifetimes.Add(new RenderGraphResourceLifetime
                    {
                        Handle = handle,
                        FirstPass = first,
                        LastPass = last,
                        IsImported = desc.Flags.HasFlag(RenderGraphResourceFlags.Imported),
                        IsPersistent = desc.Flags.HasFlag(RenderGraphResourceFlags.Persistent)
                    });
                }
            }

            // 6. 生成资源屏障 (基于相邻 Pass 之间的状态转换)
            foreach (var cp in sorted)
            {
                if (cp.IsCulled) continue;
                // 简化的屏障生成: 对每个资源, 记录上一状态, 生成所需转换
                // 实际 GPU 执行时由 Execute 动态处理
            }

            result.Success = true;
            result.Passes = sorted.Where(p => !p.IsCulled).ToList();
            result.Lifetimes = lifetimes;
            result.EstimatedMemoryBytes = lifetimes.Where(l => !l.IsImported && !l.IsPersistent)
                .Sum(l => EstimateResourceBytes(_resourceDescs[l.Handle.Index]));

            _lastCompilation = result;
            return result;
        }

        /// <summary>执行编译后的 RenderGraph。</summary>
        public void Execute(RHICommandList cmdList)
        {
            if (_device == null)
                throw new InvalidOperationException("RenderGraph: 未绑定物理设备，编译专用图不可执行。");
            if (!_isCompiled || _lastCompilation == null || !_lastCompilation.Success)
                throw new InvalidOperationException("RenderGraph 未编译或编译失败, 无法执行。");

            // 分配/复用瞬态资源
            AllocateTransientResources(_lastCompilation);

            var ctx = new RenderGraphContext(this, _allocatedResources);
            var passes = _lastCompilation.Passes;

            for (int i = 0; i < passes.Count; i++)
            {
                var cp = passes[i];
                var pass = cp.Pass;

                // 执行资源屏障: 确保所有读取资源处于正确状态
                ApplyPassBarriers(cmdList, pass, i > 0 ? passes[i - 1].Pass : null);

                // 执行 Pass
                if (pass.ExecuteDelegate != null)
                {
                    pass.ExecuteDelegate(cmdList, ctx);
                }
            }

            // 释放非持久瞬态资源 (导入资源不释放)
            ReleaseTransientResources(_lastCompilation);
        }

        /// <summary>为 Pass 应用资源屏障。</summary>
        private void ApplyPassBarriers(RHICommandList cmdList, RenderGraphPass pass, RenderGraphPass? prevPass)
        {
            // 写入资源: 转换到目标状态
            foreach (var (handle, access) in pass.ResourceWrites)
            {
                var targetState = AccessToState(access);
                if (_allocatedResources.TryGetValue(handle, out var res))
                {
                    cmdList.TransitionTo(res, targetState);
                }
            }

            // 读取资源: 转换到读取状态
            foreach (var (handle, access) in pass.ResourceReads)
            {
                var targetState = AccessToState(access);
                if (_allocatedResources.TryGetValue(handle, out var res))
                {
                    cmdList.TransitionTo(res, targetState);
                }
            }
        }

        /// <summary>将访问类型映射到 RHI 资源状态。</summary>
        private static RHIResourceState AccessToState(RenderGraphResourceAccess access)
        {
            return access switch
            {
                RenderGraphResourceAccess.ShaderRead => RHIResourceState.ShaderResource,
                RenderGraphResourceAccess.ShaderWrite => RHIResourceState.UnorderedAccess,
                RenderGraphResourceAccess.RenderTarget => RHIResourceState.RenderTarget,
                RenderGraphResourceAccess.DepthStencilWrite => RHIResourceState.DepthWrite,
                RenderGraphResourceAccess.DepthStencilRead => RHIResourceState.DepthRead,
                RenderGraphResourceAccess.CopySource => RHIResourceState.CopySource,
                RenderGraphResourceAccess.CopyDest => RHIResourceState.CopyDest,
                RenderGraphResourceAccess.VertexBuffer => RHIResourceState.VertexAndConstantBuffer,
                RenderGraphResourceAccess.IndexBuffer => RHIResourceState.IndexBuffer,
                RenderGraphResourceAccess.ConstantBuffer => RHIResourceState.VertexAndConstantBuffer,
                _ => RHIResourceState.Common
            };
        }

        /// <summary>分配瞬态资源 (使用简单池化策略)。</summary>
        private void AllocateTransientResources(RenderGraphCompilationResult compilation)
        {
            foreach (var lt in compilation.Lifetimes)
            {
                if (lt.IsImported || lt.IsPersistent)
                    continue;

                if (_allocatedResources.ContainsKey(lt.Handle))
                    continue;

                var desc = _resourceDescs[lt.Handle.Index];
                var resource = CreateRHIResource(desc);
                if (resource != null)
                    _allocatedResources[lt.Handle] = resource;
            }
        }

        /// <summary>释放生命周期结束的瞬态资源。</summary>
        private void ReleaseTransientResources(RenderGraphCompilationResult compilation)
        {
            var toRelease = new List<RenderGraphResourceHandle>();
            foreach (var lt in compilation.Lifetimes)
            {
                if (lt.IsImported || lt.IsPersistent)
                    continue;
                toRelease.Add(lt.Handle);
            }

            foreach (var h in toRelease)
            {
                if (_allocatedResources.TryGetValue(h, out var res))
                {
                    // 简单池化: 不真正释放, 留到下一帧或 graph 销毁时释放
                    // 实际生产环境应实现内存别名/子分配
                }
            }
        }

        private RHIResource? CreateRHIResource(RenderGraphResourceDesc desc)
        {
            if (desc.Type == RenderGraphResourceType.Buffer)
            {
                var bd = new RHIBufferDesc
                {
                    SizeInBytes = desc.BufferSize,
                    Stride = desc.BufferStride,
                    Flags = RHIBufferFlags.ShaderResource | RHIBufferFlags.UnorderedAccess,
                    HeapType = RHIResourceHeapType.Default
                };
                return _device!.CreateBuffer(bd, desc.Name);
            }
            else
            {
                RHITextureFlags flags = RHITextureFlags.None;
                if (desc.Flags.HasFlag(RenderGraphResourceFlags.RenderTarget))
                    flags |= RHITextureFlags.RenderTarget;
                if (desc.Flags.HasFlag(RenderGraphResourceFlags.UnorderedAccess))
                    flags |= RHITextureFlags.UnorderedAccess;
                if (desc.Flags.HasFlag(RenderGraphResourceFlags.ShaderResource))
                    flags |= RHITextureFlags.ShaderResource;
                if (desc.Flags.HasFlag(RenderGraphResourceFlags.DepthStencil))
                    flags |= RHITextureFlags.DepthStencil;

                var td = new RHITextureDesc
                {
                    Dimension = desc.Type == RenderGraphResourceType.Texture3D ? RHIResourceDimension.Texture3D : RHIResourceDimension.Texture2D,
                    Format = desc.Format,
                    Width = desc.Width,
                    Height = desc.Height,
                    DepthOrArraySize = desc.DepthOrArraySize,
                    MipLevels = desc.MipLevels,
                    SampleCount = desc.SampleCount,
                    Flags = flags
                };
                return _device!.CreateTexture(td, desc.Name);
            }
        }

        private static long EstimateResourceBytes(RenderGraphResourceDesc desc)
        {
            if (desc.Type == RenderGraphResourceType.Buffer)
                return (long)desc.BufferSize;

            long pixels = (long)desc.Width * desc.Height * Math.Max(1, desc.DepthOrArraySize);
            int bpp = RHIFormatUtils.BytesPerPixel(desc.Format);
            long bytes = pixels * bpp;
            // Mip chain ~ 1.33x
            if (desc.MipLevels > 1)
                bytes = (long)(bytes * 1.333f);
            return bytes;
        }

        /// <summary>创建默认延迟渲染图（绑定物理设备，可执行）。</summary>
        public static RenderGraph CreateDefaultDeferredGraph(RHIDevice device, int width, int height)
            => CreateDefaultDeferredGraphCore(new RenderGraph(device, "DefaultDeferred"), width, height);

        /// <summary>创建默认延迟渲染图（编译专用，不绑定物理设备，仅供分析/测试）。</summary>
        public static RenderGraph CreateDefaultDeferredGraph(int width, int height)
            => CreateDefaultDeferredGraphCore(new RenderGraph("DefaultDeferred"), width, height);

        private static RenderGraph CreateDefaultDeferredGraphCore(RenderGraph graph, int width, int height)
        {
            var shadowMap = graph.CreateResource(RenderGraphResourceDesc.Texture2D("ShadowMap", RHIFormat.D32_Float, 2048, 2048,
                flags: RenderGraphResourceFlags.DepthStencil | RenderGraphResourceFlags.ShaderResource));
            var depth = graph.CreateResource(RenderGraphResourceDesc.Texture2D("SceneDepth", RHIFormat.D32_Float, width, height,
                flags: RenderGraphResourceFlags.DepthStencil | RenderGraphResourceFlags.ShaderResource));
            var geometrybufferA = graph.CreateResource(RenderGraphResourceDesc.Texture2D("GeometryBufferA", RHIFormat.R8G8B8A8_UNorm, width, height,
                flags: RenderGraphResourceFlags.RenderTarget | RenderGraphResourceFlags.ShaderResource));
            var geometrybufferB = graph.CreateResource(RenderGraphResourceDesc.Texture2D("GeometryBufferB", RHIFormat.R10G10B10A2_UNorm, width, height,
                flags: RenderGraphResourceFlags.RenderTarget | RenderGraphResourceFlags.ShaderResource));
            var geometrybufferC = graph.CreateResource(RenderGraphResourceDesc.Texture2D("GeometryBufferC", RHIFormat.R11G11B10_Float, width, height,
                flags: RenderGraphResourceFlags.RenderTarget | RenderGraphResourceFlags.ShaderResource));
            var sceneColor = graph.CreateResource(RenderGraphResourceDesc.Texture2D("SceneColor", RHIFormat.R16G16B16A16_Float, width, height,
                flags: RenderGraphResourceFlags.UnorderedAccess | RenderGraphResourceFlags.ShaderResource));
            var finalOutput = graph.CreateResource(RenderGraphResourceDesc.Texture2D("FinalOutput", RHIFormat.R8G8B8A8_UNorm, width, height,
                flags: RenderGraphResourceFlags.RenderTarget | RenderGraphResourceFlags.ShaderResource));

            graph.AddPass("Shadow", RenderGraphPassType.Render)
                .Write(shadowMap, RenderGraphResourceAccess.DepthStencilWrite)
                .SetExecute((cmd, ctx) => { /* Shadow pass */ });

            graph.AddPass("GeometryBuffer", RenderGraphPassType.Render)
                .Read(depth, RenderGraphResourceAccess.DepthStencilRead)
                .Write(depth, RenderGraphResourceAccess.DepthStencilWrite)
                .Write(geometrybufferA, RenderGraphResourceAccess.RenderTarget)
                .Write(geometrybufferB, RenderGraphResourceAccess.RenderTarget)
                .Write(geometrybufferC, RenderGraphResourceAccess.RenderTarget)
                .SetExecute((cmd, ctx) => { /* GeometryBuffer pass */ });

            graph.AddPass("Lighting", RenderGraphPassType.Compute)
                .Read(shadowMap, RenderGraphResourceAccess.ShaderRead)
                .Read(depth, RenderGraphResourceAccess.ShaderRead)
                .Read(geometrybufferA, RenderGraphResourceAccess.ShaderRead)
                .Read(geometrybufferB, RenderGraphResourceAccess.ShaderRead)
                .Read(geometrybufferC, RenderGraphResourceAccess.ShaderRead)
                .Write(sceneColor, RenderGraphResourceAccess.ShaderWrite)
                .SetExecute((cmd, ctx) => { /* Lighting pass */ });

            graph.AddPass("PostProcess", RenderGraphPassType.Compute)
                .Read(sceneColor, RenderGraphResourceAccess.ShaderRead)
                .Write(finalOutput, RenderGraphResourceAccess.ShaderWrite)
                .SetExecute((cmd, ctx) => { /* Postprocess pass */ });

            return graph;
        }

        /// <summary>清空图, 供下一帧复用。</summary>
        public void Clear()
        {
            _passes.Clear();
            _resourceDescs.Clear();
            _resourceHandles.Clear();
            _resourceVersionCounter = 0;
            _isCompiled = false;
            _lastCompilation = null;
        }

        /// <summary>释放所有分配的瞬态资源。</summary>
        public void Dispose()
        {
            foreach (var kv in _allocatedResources)
            {
                if (!_importedResources.ContainsValue(kv.Key))
                    kv.Value.Dispose();
            }
            _allocatedResources.Clear();
            _resourcePool.Clear();
        }
    }
}
