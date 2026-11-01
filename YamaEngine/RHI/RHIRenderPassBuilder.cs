// ════════════════════════════════════════════════════════════════════════════
// yama引擎 - RHI RenderPass 构建器 (RHIRenderPassBuilder)
//
// 提供流式 API 来构建 RenderPass 描述。
// 参考 Granite 和 bgfx 的 render pass 设计，比 D3D12 原生的 PSO+OMSetRenderTargets 好用多了。
//
// 主要功能:
//   * 流式 API 构建 render pass
//   * 支持 MRT (多渲染目标)
//   * 支持 depth/stencil
   //   * 支持 attachment 的 load/store 操作 (clear/load/dont_care)
//   * 支持 resolve (MSAA)
//   * 自动生成屏障
//   * RenderPass 缓存（相同的 desc 复用）
//
// 用法:
//   var rp = builder
//       .AddColorAttachment(rt0, RHIFormat.R8G8B8A8_UNorm, LoadOp.Clear, StoreOp.Store)
//       .AddColorAttachment(rt1, RHIFormat.R32_Float, LoadOp.Load, StoreOp.Store)
//       .SetDepthAttachment(depth, RHIFormat.D32_Float, LoadOp.Clear, StoreOp.Store)
//       .SetSamples(4)
//       .Build();
//   cmdList.BeginRenderPass(rp);
//   ...
//   cmdList.EndRenderPass();
//
// FIXME: 暂时不支持 layer 选择和多 viewport
// TODO: 加个 hash 缓存，相同配置的 render pass 不重复创建
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace YamaEngine.RHI
{
    /// <summary>
    /// RenderPass 构建器：用流式 API 构建 render pass 描述。
    /// </summary>
    public sealed class RHIRenderPassBuilder
    {
        // ──────────────────────────────────────────────────────────────────────
        // 内部状态
        // ──────────────────────────────────────────────────────────────────────

        private readonly List<RenderPassColorAttachment> _colorAttachments = new(8);
        private RenderPassDepthAttachment _depthAttachment;
        private bool _hasDepth;
        private uint _samples;
        private uint _width;
        private uint _height;
        private uint _layers;
        private string _name;

        // 缓存
        private static readonly Dictionary<long, RHIRenderPassDesc> s_cache = new();
        private static readonly object s_cacheLock = new();
        private static int s_cacheHits;
        private static int s_cacheMisses;
        private static int s_totalCreated;

        /// <summary>最大颜色附件数</summary>
        public const int MaxColorAttachments = 8;

        // ──────────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>创建新的 builder。</summary>
        public RHIRenderPassBuilder()
        {
            Reset();
        }

        /// <summary>创建指定尺寸的 builder。</summary>
        public RHIRenderPassBuilder(uint width, uint height) : this()
        {
            _width = width;
            _height = height;
        }

        /// <summary>重置 builder 到初始状态。</summary>
        public RHIRenderPassBuilder Reset()
        {
            _colorAttachments.Clear();
            _depthAttachment = default;
            _hasDepth = false;
            _samples = 1;
            _width = 0;
            _height = 0;
            _layers = 1;
            _name = null;
            return this;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 构建 API
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>设置 pass 尺寸。</summary>
        public RHIRenderPassBuilder SetSize(uint width, uint height)
        {
            _width = width;
            _height = height;
            return this;
        }

        /// <summary>设置 MSAA 采样数。</summary>
        public RHIRenderPassBuilder SetSamples(uint samples)
        {
            if (samples != 1 && samples != 2 && samples != 4 && samples != 8 && samples != 16)
                throw new ArgumentException($"不支持的采样数: {samples}，必须是 1/2/4/8/16");
            _samples = samples;
            return this;
        }

        /// <summary>设置渲染的 layer 数（用于 cubemap 或 texture array）。</summary>
        public RHIRenderPassBuilder SetLayers(uint layers)
        {
            _layers = Math.Max(1, layers);
            return this;
        }

        /// <summary>设置 pass 名称（调试用）。</summary>
        public RHIRenderPassBuilder SetName(string name)
        {
            _name = name;
            return this;
        }

        /// <summary>
        /// 添加颜色附件。
        /// </summary>
        /// <param name="texture">渲染目标纹理</param>
        /// <param name="format">纹理格式</param>
        /// <param name="loadOp">加载操作 (Clear/Load/DontCare)</param>
        /// <param name="storeOp">存储操作 (Store/DontCare)</param>
        public RHIRenderPassBuilder AddColorAttachment(
            RHIResource texture,
            RHIFormat format,
            RHILoadOp loadOp = RHILoadOp.Clear,
            RHIStoreOp storeOp = RHIStoreOp.Store)
        {
            if (_colorAttachments.Count >= MaxColorAttachments)
                throw new InvalidOperationException($"颜色附件已达上限 {MaxColorAttachments}");
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));

            _colorAttachments.Add(new RenderPassColorAttachment
            {
                Texture = texture,
                Format = format,
                LoadOp = loadOp,
                StoreOp = storeOp,
                ResolveTexture = null,
                ClearColor = new(0, 0, 0, 1),
                InitialState = RHIResourceState.RenderTarget,
                FinalState = RHIResourceState.RenderTarget,
            });
            return this;
        }

        /// <summary>
        /// 添加颜色附件，带 resolve (MSAA)。
        /// </summary>
        public RHIRenderPassBuilder AddColorAttachmentWithResolve(
            RHIResource msaaTexture,
            RHIResource resolveTexture,
            RHIFormat format,
            RHILoadOp loadOp = RHILoadOp.Clear,
            RHIStoreOp storeOp = RHIStoreOp.Store,
            RHIStoreOp resolveStoreOp = RHIStoreOp.Store)
        {
            if (_colorAttachments.Count >= MaxColorAttachments)
                throw new InvalidOperationException($"颜色附件已达上限 {MaxColorAttachments}");
            if (_samples == 1)
                throw new InvalidOperationException("需要先 SetSamples > 1 才能使用 resolve");

            _colorAttachments.Add(new RenderPassColorAttachment
            {
                Texture = msaaTexture,
                Format = format,
                LoadOp = loadOp,
                StoreOp = storeOp,
                ResolveTexture = resolveTexture,
                ResolveStoreOp = resolveStoreOp,
                ClearColor = new(0, 0, 0, 1),
                InitialState = RHIResourceState.RenderTarget,
                FinalState = RHIResourceState.RenderTarget,
            });
            return this;
        }

        /// <summary>设置颜色附件的 clear 值。</summary>
        public RHIRenderPassBuilder SetClearColor(int index, float r, float g, float b, float a)
        {
            if (index < 0 || index >= _colorAttachments.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var att = _colorAttachments[index];
            att.ClearColor = new(r, g, b, a);
            _colorAttachments[index] = att;
            return this;
        }

        /// <summary>设置颜色附件的 clear 前后状态。</summary>
        public RHIRenderPassBuilder SetColorAttachmentStates(int index,
                                                              RHIResourceState initial,
                                                              RHIResourceState final)
        {
            if (index < 0 || index >= _colorAttachments.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var att = _colorAttachments[index];
            att.InitialState = initial;
            att.FinalState = final;
            _colorAttachments[index] = att;
            return this;
        }

        /// <summary>
        /// 设置深度/模板附件。
        /// </summary>
        public RHIRenderPassBuilder SetDepthAttachment(
            RHIResource texture,
            RHIFormat format,
            RHILoadOp depthLoadOp = RHILoadOp.Clear,
            RHIStoreOp depthStoreOp = RHIStoreOp.Store,
            RHILoadOp stencilLoadOp = RHILoadOp.DontCare,
            RHIStoreOp stencilStoreOp = RHIStoreOp.DontCare)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));

            _depthAttachment = new RenderPassDepthAttachment
            {
                Texture = texture,
                Format = format,
                DepthLoadOp = depthLoadOp,
                DepthStoreOp = depthStoreOp,
                StencilLoadOp = stencilLoadOp,
                StencilStoreOp = stencilStoreOp,
                ClearDepth = 1.0f,
                ClearStencil = 0,
                InitialState = RHIResourceState.DepthWrite,
                FinalState = RHIResourceState.DepthWrite,
                ReadOnlyDepth = false,
            };
            _hasDepth = true;
            return this;
        }

        /// <summary>设置深度 clear 值。</summary>
        public RHIRenderPassBuilder SetClearDepth(float depth)
        {
            if (!_hasDepth)
                throw new InvalidOperationException("需要先调用 SetDepthAttachment");
            _depthAttachment.ClearDepth = depth;
            return this;
        }

        /// <summary>设置模板 clear 值。</summary>
        public RHIRenderPassBuilder SetClearStencil(uint stencil)
        {
            if (!_hasDepth)
                throw new InvalidOperationException("需要先调用 SetDepthAttachment");
            _depthAttachment.ClearStencil = stencil;
            return this;
        }

        /// <summary>设置深度附件为只读模式（不写深度）。</summary>
        public RHIRenderPassBuilder SetDepthReadOnly(bool readOnly)
        {
            if (!_hasDepth)
                throw new InvalidOperationException("需要先调用 SetDepthAttachment");
            _depthAttachment.ReadOnlyDepth = readOnly;
            _depthAttachment.InitialState = readOnly ? RHIResourceState.DepthRead : RHIResourceState.DepthWrite;
            _depthAttachment.FinalState = readOnly ? RHIResourceState.DepthRead : RHIResourceState.DepthWrite;
            return this;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 构建最终 desc
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 构建 RenderPass 描述。如果缓存中有相同的 desc，会直接返回缓存的。
        /// </summary>
        public RHIRenderPassDesc Build()
        {
            if (_colorAttachments.Count == 0 && !_hasDepth)
                throw new InvalidOperationException("至少需要一个颜色附件或深度附件");

            // 如果尺寸没设置，尝试从附件推断
            if (_width == 0 || _height == 0)
            {
                // 尝试从第一个附件获取尺寸
                // FIXME: 需要从 texture 获取尺寸
                _width = _width == 0 ? 1920 : _width;
                _height = _height == 0 ? 1080 : _height;
            }

            var desc = new RHIRenderPassDesc
            {
                ColorAttachments = _colorAttachments.ToArray(),
                HasDepth = _hasDepth,
                DepthAttachment = _depthAttachment,
                Samples = _samples,
                Width = _width,
                Height = _height,
                Layers = _layers,
                Name = _name ?? $"RenderPass_{s_totalCreated}",
            };

            // 计算 hash 用于缓存
            desc.Hash = ComputeHash(desc);

            // 缓存查找
            lock (s_cacheLock)
            {
                if (s_cache.TryGetValue(desc.Hash, out var cached))
                {
                    s_cacheHits++;
                    return cached;
                }
                s_cache[desc.Hash] = desc;
                s_cacheMisses++;
                s_totalCreated++;
            }

            return desc;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 预设
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>创建一个简单的单 RT + Depth 的 pass。</summary>
        public static RHIRenderPassDesc Simple(RHIResource color, RHIResource depth,
                                                RHIFormat colorFormat, RHIFormat depthFormat,
                                                uint width, uint height)
        {
            return new RHIRenderPassBuilder(width, height)
                .AddColorAttachment(color, colorFormat, RHILoadOp.Clear, RHIStoreOp.Store)
                .SetDepthAttachment(depth, depthFormat, RHILoadOp.Clear, RHIStoreOp.Store)
                .Build();
        }

        /// <summary>创建一个只有颜色的 pass。</summary>
        public static RHIRenderPassDesc ColorOnly(RHIResource color, RHIFormat format,
                                                   uint width, uint height,
                                                   RHILoadOp loadOp = RHILoadOp.Clear)
        {
            return new RHIRenderPassBuilder(width, height)
                .AddColorAttachment(color, format, loadOp, RHIStoreOp.Store)
                .Build();
        }

        /// <summary>创建一个 depth-only pass（如 shadow map）。</summary>
        public static RHIRenderPassDesc DepthOnly(RHIResource depth, RHIFormat format,
                                                   uint width, uint height,
                                                   RHILoadOp loadOp = RHILoadOp.Clear)
        {
            return new RHIRenderPassBuilder(width, height)
                .SetDepthAttachment(depth, format, loadOp, RHIStoreOp.Store)
                .Build();
        }

        /// <summary>创建 MSAA pass。</summary>
        public static RHIRenderPassDesc MSAA(RHIResource msaaColor, RHIResource resolveColor,
                                              RHIResource depth, RHIFormat colorFormat, RHIFormat depthFormat,
                                              uint width, uint height, uint samples = 4)
        {
            var builder = new RHIRenderPassBuilder(width, height)
                .SetSamples(samples)
                .AddColorAttachmentWithResolve(msaaColor, resolveColor, colorFormat,
                    RHILoadOp.Clear, RHIStoreOp.Store, RHIStoreOp.Store);
            if (depth != null)
                builder.SetDepthAttachment(depth, depthFormat, RHILoadOp.Clear, RHIStoreOp.DontCare);
            return builder.Build();
        }

        /// <summary>创建 post-process pass（单 RT，无 depth，load=Load）。</summary>
        public static RHIRenderPassDesc PostProcess(RHIResource color, RHIFormat format,
                                                     uint width, uint height)
        {
            return new RHIRenderPassBuilder(width, height)
                .AddColorAttachment(color, format, RHILoadOp.Load, RHIStoreOp.Store)
                .Build();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 缓存管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>缓存命中次数</summary>
        public static int CacheHits => s_cacheHits;

        /// <summary>缓存未命中次数</summary>
        public static int CacheMisses => s_cacheMisses;

        /// <summary>缓存大小</summary>
        public static int CacheSize => s_cache.Count;

        /// <summary>清空缓存。</summary>
        public static void ClearCache()
        {
            lock (s_cacheLock)
            {
                s_cache.Clear();
                s_cacheHits = 0;
                s_cacheMisses = 0;
                s_totalCreated = 0;
            }
        }

        /// <summary>获取缓存统计信息。</summary>
        public static string GetCacheStats()
        {
            float hitRate = (s_cacheHits + s_cacheMisses) > 0
                ? (float)s_cacheHits / (s_cacheHits + s_cacheMisses) * 100f
                : 0f;
            return $"RenderPass 缓存: 大小={s_cache.Count}, 命中={s_cacheHits}, 未命中={s_cacheMisses}, " +
                   $"命中率={hitRate:F1}%, 总创建={s_totalCreated}";
        }

        // ──────────────────────────────────────────────────────────────────────
        // Hash 计算
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>计算 RenderPass desc 的 hash 用于缓存。</summary>
        private static long ComputeHash(RHIRenderPassDesc desc)
        {
            unchecked
            {
                long hash = 17;
                hash = hash * 31 + desc.Width;
                hash = hash * 31 + desc.Height;
                hash = hash * 31 + desc.Samples;
                hash = hash * 31 + desc.Layers;
                hash = hash * 31 + desc.ColorAttachments.Length;
                foreach (var att in desc.ColorAttachments)
                {
                    hash = hash * 31 + (att.Texture?.GetHashCode() ?? 0);
                    hash = hash * 31 + (int)att.Format;
                    hash = hash * 31 + (int)att.LoadOp;
                    hash = hash * 31 + (int)att.StoreOp;
                    hash = hash * 31 + (att.ResolveTexture?.GetHashCode() ?? 0);
                }
                if (desc.HasDepth)
                {
                    hash = hash * 31 + (desc.DepthAttachment.Texture?.GetHashCode() ?? 0);
                    hash = hash * 31 + (int)desc.DepthAttachment.Format;
                    hash = hash * 31 + (int)desc.DepthAttachment.DepthLoadOp;
                    hash = hash * 31 + (int)desc.DepthAttachment.DepthStoreOp;
                    hash = hash * 31 + desc.DepthAttachment.ReadOnlyDepth.GetHashCode();
                }
                return hash;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // RenderPass 描述结构
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>RenderPass 描述</summary>
    public sealed class RHIRenderPassDesc
    {
        public RenderPassColorAttachment[] ColorAttachments;
        public bool HasDepth;
        public RenderPassDepthAttachment DepthAttachment;
        public uint Samples;
        public uint Width;
        public uint Height;
        public uint Layers;
        public string Name;
        public long Hash;

        /// <summary>颜色附件数</summary>
        public int ColorAttachmentCount => ColorAttachments?.Length ?? 0;

        /// <summary>是否有 resolve</summary>
        public bool HasResolve
        {
            get
            {
                if (ColorAttachments == null) return false;
                foreach (var att in ColorAttachments)
                    if (att.ResolveTexture != null) return true;
                return false;
            }
        }

        /// <summary>是否使用 MSAA</summary>
        public bool IsMSAA => Samples > 1;

        public override string ToString()
        {
            return $"RenderPass '{Name}': {Width}x{Height}x{Layers}, samples={Samples}, " +
                   $"colors={ColorAttachmentCount}, depth={HasDepth}, hash={Hash}";
        }

        /// <summary>生成调试用的详细描述。</summary>
        public string ToDetailedString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"RenderPass: {Name}");
            sb.AppendLine($"  尺寸: {Width}x{Height}, layers={Layers}, samples={Samples}");
            sb.AppendLine($"  颜色附件 ({ColorAttachmentCount}):");
            for (int i = 0; i < ColorAttachmentCount; i++)
            {
                var att = ColorAttachments[i];
                sb.AppendLine($"    [{i}] format={att.Format}, load={att.LoadOp}, store={att.StoreOp}" +
                              $"{(att.ResolveTexture != null ? ", resolve=yes" : "")}");
                sb.AppendLine($"        clear=({att.ClearColor.R:F2}, {att.ClearColor.G:F2}, " +
                              $"{att.ClearColor.B:F2}, {att.ClearColor.A:F2})");
            }
            if (HasDepth)
            {
                sb.AppendLine($"  深度附件:");
                sb.AppendLine($"    format={DepthAttachment.Format}, " +
                              $"dLoad={DepthAttachment.DepthLoadOp}, dStore={DepthAttachment.DepthStoreOp}");
                sb.AppendLine($"    sLoad={DepthAttachment.StencilLoadOp}, " +
                              $"sStore={DepthAttachment.StencilStoreOp}, readOnly={DepthAttachment.ReadOnlyDepth}");
                sb.AppendLine($"    clearDepth={DepthAttachment.ClearDepth:F2}, " +
                              $"clearStencil={DepthAttachment.ClearStencil}");
            }
            return sb.ToString();
        }
    }

    /// <summary>颜色附件描述</summary>
    public struct RenderPassColorAttachment
    {
        public RHIResource Texture;
        public RHIFormat Format;
        public RHILoadOp LoadOp;
        public RHIStoreOp StoreOp;
        public RHIResource ResolveTexture;
        public RHIStoreOp ResolveStoreOp;
        public ClearColorValue ClearColor;
        public RHIResourceState InitialState;
        public RHIResourceState FinalState;

        public bool HasResolve => ResolveTexture != null;
    }

    /// <summary>深度附件描述</summary>
    public struct RenderPassDepthAttachment
    {
        public RHIResource Texture;
        public RHIFormat Format;
        public RHILoadOp DepthLoadOp;
        public RHIStoreOp DepthStoreOp;
        public RHILoadOp StencilLoadOp;
        public RHIStoreOp StencilStoreOp;
        public float ClearDepth;
        public uint ClearStencil;
        public RHIResourceState InitialState;
        public RHIResourceState FinalState;
        public bool ReadOnlyDepth;
    }

    /// <summary>clear 颜色值</summary>
    public struct ClearColorValue
    {
        public float R, G, B, A;

        public ClearColorValue(float r, float g, float b, float a)
        {
            R = r; G = g; B = b; A = a;
        }

        public static ClearColorValue Black => new(0, 0, 0, 1);
        public static ClearColorValue White => new(1, 1, 1, 1);
        public static ClearColorValue Transparent => new(0, 0, 0, 0);
        public static ClearColorValue Red => new(1, 0, 0, 1);
        public static ClearColorValue Green => new(0, 1, 0, 1);
        public static ClearColorValue Blue => new(0, 0, 1, 1);

        public override string ToString() => $"({R:F2},{G:F2},{B:F2},{A:F2})";
    }

    /// <summary>加载操作</summary>
    public enum RHILoadOp
    {
        /// <summary>清除附件</summary>
        Clear,
        /// <summary>加载之前的内容</summary>
        Load,
        /// <summary>不关心之前的内容</summary>
        DontCare,
    }

    /// <summary>存储操作</summary>
    public enum RHIStoreOp
    {
        /// <summary>存储内容</summary>
        Store,
        /// <summary>不关心存储后的内容</summary>
        DontCare,
        /// <summary>不关心，但保证内容不变（某些 API 支持）</summary>
        None,
    }

    // ════════════════════════════════════════════════════════════════════════════
    // RenderPass 执行器
    // 负责把 RHIRenderPassDesc 转换成实际的 API 调用
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RenderPass 执行器：管理 RenderPass 的开始/结束，自动处理屏障和 clear。
    /// </summary>
    public sealed class RHIRenderPassExecutor : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RHIBarrierTracker _barrierTracker;
        private readonly IRHICommandList _cmdList;
        private RHIRenderPassDesc _currentPass;
        private bool _inPass;

        /// <summary>是否正在 pass 中</summary>
        public bool InPass => _inPass;

        /// <summary>当前 pass</summary>
        public RHIRenderPassDesc CurrentPass => _currentPass;

        /// <summary>统计信息</summary>
        public RenderPassStats Stats { get; } = new();

        /// <summary>是否自动插入屏障</summary>
        public bool AutoBarriers { get; set; } = true;

        /// <summary>是否自动 clear</summary>
        public bool AutoClear { get; set; } = true;

        public RHIRenderPassExecutor(RHIDevice device, IRHICommandList cmdList,
                                      RHIBarrierTracker barrierTracker = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _cmdList = cmdList ?? throw new ArgumentNullException(nameof(cmdList));
            _barrierTracker = barrierTracker;
        }

        /// <summary>
        /// 开始 render pass。自动插入屏障和 clear。
        /// </summary>
        public void Begin(RHIRenderPassDesc desc)
        {
            if (_inPass)
                throw new InvalidOperationException("已经在 render pass 中，不能重复 Begin");
            if (desc == null)
                throw new ArgumentNullException(nameof(desc));

            _currentPass = desc;
            _inPass = true;
            Stats.TotalPasses++;

            // 1. 插入屏障：把附件转到可写状态
            if (AutoBarriers && _barrierTracker != null)
            {
                _barrierTracker.BeginBatch();
                foreach (var att in desc.ColorAttachments)
                {
                    if (att.Texture != null)
                        _barrierTracker.TransitionTo(att.Texture, RHIResourceState.RenderTarget);
                }
                if (desc.HasDepth)
                {
                    var state = desc.DepthAttachment.ReadOnlyDepth
                        ? RHIResourceState.DepthRead
                        : RHIResourceState.DepthWrite;
                    _barrierTracker.TransitionTo(desc.DepthAttachment.Texture, state);
                }
                _barrierTracker.EndBatch(_cmdList);
            }

            // 2. 设置渲染目标
            SetRenderTargets(desc);

            // 3. 执行 clear
            if (AutoClear)
            {
                ExecuteClears(desc);
            }

            // 4. 设置 viewport 和 scissor
            SetDefaultViewportAndScissor(desc);
        }

        /// <summary>
        /// 结束 render pass。自动插入结束屏障。
        /// </summary>
        public void End()
        {
            if (!_inPass)
                throw new InvalidOperationException("不在 render pass 中");

            // 结束 pass
            // 实际的 EndRenderPass 调用
            _cmdList.EndRenderPass();

            // 插入结束屏障：把附件转到最终状态
            if (AutoBarriers && _barrierTracker != null)
            {
                _barrierTracker.BeginBatch();
                foreach (var att in _currentPass.ColorAttachments)
                {
                    if (att.Texture != null && att.FinalState != RHIResourceState.RenderTarget)
                        _barrierTracker.TransitionTo(att.Texture, att.FinalState);
                }
                if (_currentPass.HasDepth && _currentPass.DepthAttachment.FinalState != RHIResourceState.DepthWrite)
                {
                    _barrierTracker.TransitionTo(_currentPass.DepthAttachment.Texture,
                        _currentPass.DepthAttachment.FinalState);
                }
                _barrierTracker.EndBatch(_cmdList);
            }

            _inPass = false;
            _currentPass = null;
        }

        /// <summary>
        /// 设置渲染目标到命令列表。
        /// </summary>
        private void SetRenderTargets(RHIRenderPassDesc desc)
        {
            // 构建 RTV/DSV 数组
            int colorCount = desc.ColorAttachmentCount;
            var rtvs = new RHIRenderTargetView[colorCount];
            for (int i = 0; i < colorCount; i++)
            {
                rtvs[i] = new RHIRenderTargetView
                {
                    Resource = desc.ColorAttachments[i].Texture,
                    Format = desc.ColorAttachments[i].Format,
                };
            }

            RHIDepthStencilView? dsv = null;
            if (desc.HasDepth)
            {
                dsv = new RHIDepthStencilView
                {
                    Resource = desc.DepthAttachment.Texture,
                    Format = desc.DepthAttachment.Format,
                    ReadOnlyDepth = desc.DepthAttachment.ReadOnlyDepth,
                };
            }

            _cmdList.SetRenderTargets(rtvs, dsv);
        }

        /// <summary>
        /// 执行 clear 操作。
        /// </summary>
        private void ExecuteClears(RHIRenderPassDesc desc)
        {
            for (int i = 0; i < desc.ColorAttachmentCount; i++)
            {
                var att = desc.ColorAttachments[i];
                if (att.LoadOp == RHILoadOp.Clear)
                {
                    _cmdList.ClearRenderTargetView(i, att.ClearColor.R, att.ClearColor.G,
                                                     att.ClearColor.B, att.ClearColor.A);
                    Stats.TotalClears++;
                }
            }

            if (desc.HasDepth)
            {
                var att = desc.DepthAttachment;
                bool clearDepth = att.DepthLoadOp == RHILoadOp.Clear;
                bool clearStencil = att.StencilLoadOp == RHILoadOp.Clear;
                if (clearDepth || clearStencil)
                {
                    _cmdList.ClearDepthStencilView(clearDepth, clearStencil,
                                                     att.ClearDepth, att.ClearStencil);
                    Stats.TotalClears++;
                }
            }
        }

        /// <summary>
        /// 设置默认 viewport 和 scissor。
        /// </summary>
        private void SetDefaultViewportAndScissor(RHIRenderPassDesc desc)
        {
            var viewport = new RHIViewport
            {
                X = 0, Y = 0,
                Width = desc.Width,
                Height = desc.Height,
                MinDepth = 0f,
                MaxDepth = 1f,
            };
            var scissor = new RHIRect
            {
                X = 0, Y = 0,
                Width = (int)desc.Width,
                Height = (int)desc.Height,
            };
            _cmdList.SetViewport(viewport);
            _cmdList.SetScissor(scissor);
        }

        /// <summary>
        /// 在 pass 中途清除某个颜色附件。
        /// </summary>
        public void ClearColor(int index, float r, float g, float b, float a)
        {
            if (!_inPass) throw new InvalidOperationException("不在 render pass 中");
            _cmdList.ClearRenderTargetView(index, r, g, b, a);
            Stats.TotalClears++;
        }

        /// <summary>
        /// 在 pass 中途清除深度/模板。
        /// </summary>
        public void ClearDepthStencil(bool clearDepth, bool clearStencil, float depth, uint stencil)
        {
            if (!_inPass) throw new InvalidOperationException("不在 render pass 中");
            _cmdList.ClearDepthStencilView(clearDepth, clearStencil, depth, stencil);
            Stats.TotalClears++;
        }

        public void Dispose()
        {
            if (_inPass)
            {
                try { End(); }
                catch { /* 忽略 Dispose 中的错误 */ }
            }
        }
    }

    /// <summary>RenderPass 统计</summary>
    public class RenderPassStats
    {
        public int TotalPasses;
        public int TotalClears;

        public override string ToString() => $"Passes={TotalPasses}, Clears={TotalClears}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // RenderPass scope (用 using 确保 End 被调用)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RenderPass scope：用 using 语法确保 render pass 正确结束。
    /// </summary>
    public readonly struct RenderPassScope : IDisposable
    {
        private readonly RHIRenderPassExecutor _executor;

        public RenderPassScope(RHIRenderPassExecutor executor, RHIRenderPassDesc desc)
        {
            _executor = executor;
            _executor.Begin(desc);
        }

        public void Dispose()
        {
            _executor.End();
        }
    }

    /// <summary>
    /// RHIRenderPassExecutor 的扩展方法。
    /// </summary>
    public static class RenderPassExecutorExtensions
    {
        /// <summary>创建一个 scope，用 using 确保 End 被调用。</summary>
        public static RenderPassScope Scope(this RHIRenderPassExecutor executor, RHIRenderPassDesc desc)
        {
            return new RenderPassScope(executor, desc);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // RenderPass 序列管理器
    // 管理一帧中所有的 render pass，支持依赖分析和顺序优化
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RenderPass 序列管理器：管理一帧中的 render pass 序列。
    /// 支持 pass 之间的依赖分析和屏障优化。
    /// </summary>
    public sealed class RenderPassSequence : IDisposable
    {
        private readonly List<PassEntry> _passes = new();
        private readonly RHIRenderPassExecutor _executor;
        private readonly RHIBarrierTracker _barrierTracker;
        private int _currentPass;

        /// <summary>pass 数量</summary>
        public int Count => _passes.Count;

        /// <summary>当前 pass 索引</summary>
        public int CurrentIndex => _currentPass;

        public RenderPassSequence(RHIRenderPassExecutor executor, RHIBarrierTracker barrierTracker = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _barrierTracker = barrierTracker;
        }

        /// <summary>添加一个 pass。</summary>
        public RenderPassSequence Add(RHIRenderPassDesc desc, string name = null,
                                       Action<IRHICommandList> callback = null)
        {
            _passes.Add(new PassEntry
            {
                Desc = desc,
                Name = name ?? desc.Name,
                Callback = callback,
                ReadResources = new List<RHIResource>(),
                WrittenResources = new List<RHIResource>(),
            });
            return this;
        }

        /// <summary>标记 pass 读取的资源。</summary>
        public RenderPassSequence Reads(RHIResource resource)
        {
            if (_passes.Count == 0) return this;
            _passes[^1].ReadResources.Add(resource);
            return this;
        }

        /// <summary>标记 pass 写入的资源。</summary>
        public RenderPassSequence Writes(RHIResource resource)
        {
            if (_passes.Count == 0) return this;
            _passes[^1].WrittenResources.Add(resource);
            return this;
        }

        /// <summary>执行所有 pass。</summary>
        public void Execute(IRHICommandList cmdList)
        {
            _currentPass = 0;
            foreach (var entry in _passes)
            {
                using var scope = _executor.Scope(entry.Desc);
                entry.Callback?.Invoke(cmdList);
                _currentPass++;
            }
        }

        /// <summary>分析 pass 之间的依赖关系。</summary>
        public PassDependencyGraph AnalyzeDependencies()
        {
            var graph = new PassDependencyGraph(_passes.Count);

            for (int i = 0; i < _passes.Count; i++)
            {
                for (int j = i + 1; j < _passes.Count; j++)
                {
                    // 如果 pass j 读取了 pass i 写入的资源，就有依赖
                    foreach (var written in _passes[i].WrittenResources)
                    {
                        if (_passes[j].ReadResources.Contains(written) ||
                            _passes[j].WrittenResources.Contains(written))
                        {
                            graph.AddEdge(i, j);
                            break;
                        }
                    }
                }
            }

            return graph;
        }

        /// <summary>清空序列。</summary>
        public void Clear()
        {
            _passes.Clear();
            _currentPass = 0;
        }

        public void Dispose()
        {
            Clear();
        }

        private struct PassEntry
        {
            public RHIRenderPassDesc Desc;
            public string Name;
            public Action<IRHICommandList> Callback;
            public List<RHIResource> ReadResources;
            public List<RHIResource> WrittenResources;
        }
    }

    /// <summary>Pass 依赖图</summary>
    public sealed class PassDependencyGraph
    {
        private readonly int _nodeCount;
        private readonly List<(int from, int to)> _edges = new();
        private readonly Dictionary<int, List<int>> _adjacency = new();

        public int NodeCount => _nodeCount;
        public int EdgeCount => _edges.Count;

        public PassDependencyGraph(int nodeCount)
        {
            _nodeCount = nodeCount;
        }

        public void AddEdge(int from, int to)
        {
            _edges.Add((from, to));
            if (!_adjacency.ContainsKey(from))
                _adjacency[from] = new List<int>();
            _adjacency[from].Add(to);
        }

        /// <summary>拓扑排序。</summary>
        public List<int> TopologicalSort()
        {
            var inDegree = new int[_nodeCount];
            foreach (var (_, to) in _edges)
                inDegree[to]++;

            var queue = new Queue<int>();
            for (int i = 0; i < _nodeCount; i++)
                if (inDegree[i] == 0) queue.Enqueue(i);

            var result = new List<int>(_nodeCount);
            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                result.Add(node);
                if (_adjacency.TryGetValue(node, out var neighbors))
                {
                    foreach (var n in neighbors)
                    {
                        if (--inDegree[n] == 0)
                            queue.Enqueue(n);
                    }
                }
            }

            return result.Count == _nodeCount ? result : null; // null 表示有环
        }

        /// <summary>生成图的描述（用于调试）。</summary>
        public string ToDebugString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"PassDependencyGraph: nodes={_nodeCount}, edges={_edges.Count}");
            foreach (var (from, to) in _edges)
                sb.AppendLine($"  Pass{from} -> Pass{to}");
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 接口占位（实际实现在其他文件中）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>RHI 设备接口</summary>

    /// <summary>命令列表扩展接口</summary>
    public partial interface IRHICommandList
    {
        void SetRenderTargets(RHIRenderTargetView[] rtvs, RHIDepthStencilView? dsv);
        void ClearRenderTargetView(int index, float r, float g, float b, float a);
        void ClearDepthStencilView(bool clearDepth, bool clearStencil, float depth, uint stencil);
        void SetViewport(RHIViewport viewport);
        void SetScissor(RHIRect scissor);
        void EndRenderPass();
    }

    /// <summary>RenderTarget view</summary>
    public struct RHIRenderTargetView
    {
        public RHIResource Resource;
        public RHIFormat Format;
    }

    /// <summary>DepthStencil view</summary>
    public struct RHIDepthStencilView
    {
        public RHIResource Resource;
        public RHIFormat Format;
        public bool ReadOnlyDepth;
    }

    /// <summary>Viewport</summary>

    /// <summary>Scissor rect</summary>

    /// <summary>Texture desc</summary>

    /// <summary>Buffer desc</summary>

    /// <summary>Resource usage flags</summary>
    [Flags]
    public enum RHIResourceUsage : uint
    {
        None = 0,
        Default = 1,
        Upload = 2,
        Readback = 4,
        RenderTarget = 8,
        DepthStencil = 16,
        ShaderResource = 32,
        UnorderedAccess = 64,
    }
}
