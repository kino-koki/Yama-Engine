// ════════════════════════════════════════════════════════════════════════
// yama引擎 - 世界渲染管线 (WorldRenderPipeline)
//
// 管理完整的帧渲染流程:
//   PrePass(Z-Prepass) -> ShadowMap -> GeometryBuffer -> SSAO ->
//   LightResolve -> ForwardPass -> Translucency -> PostProcess -> UI
//
// 核心特性:
//   1. 声明式 Pass 配置，支持运行时动态启用/禁用
//   2. 每帧渲染统计 (DrawCall / 三角形数 / GPU时间)
//   3. 可选 RenderGraph 集成 (Pass 注册为 RenderGraph 节点)
//   4. 独立 RT/DS 分配与生命周期管理
//   5. 多分辨率渲染支持 (自适应质量系统对接)
//
// 对接系统: RHI (RHIDevice/RHICommandList) / RenderGraph / SceneRendererReal
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ──────────────────────────────────────────────────────────────
    // 渲染 Pass 枚举 — 管线中的每个渲染阶段
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 世界渲染管线中的渲染 Pass 枚举。
    /// 按 Execute 顺序定义，每个 Pass 可独立启用/禁用。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 渲染目标清除标志
    // ──────────────────────────────────────────────────────────────

    /// <summary>渲染目标清除标志。</summary>
    [Flags]
    public enum RenderPassClearFlags
    {
        None = 0,
        /// <summary>清除颜色目标。</summary>
        Color = 1,
        /// <summary>清除深度。</summary>
        Depth = 2,
        /// <summary>清除模板。</summary>
        Stencil = 4,
        /// <summary>同时清除颜色和深度。</summary>
        All = Color | Depth | Stencil
    }

    // ──────────────────────────────────────────────────────────────
    // 每个渲染 Pass 的配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个渲染 Pass 的配置信息。
    /// 控制 RT 格式、深度格式、清除行为和是否启用。
    /// </summary>
    public sealed class RenderPassConfig
    {
        /// <summary>Pass 名称 (仅用于调试/Profile)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>是否启用此 Pass。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>渲染目标格式。RHIFormat.Unknown 表示无 RT 绑定。</summary>
        public RHIFormat RenderTargetFormat { get; set; } = RHIFormat.R16G16B16A16_Float;

        /// <summary>深度/模板格式。RHIFormat.Unknown 表示无 DSV 绑定。</summary>
        public RHIFormat DepthFormat { get; set; } = RHIFormat.D24_UNorm_S8_UInt;

        /// <summary>Pass 开始时的清除标志。</summary>
        public RenderPassClearFlags ClearFlags { get; set; } = RenderPassClearFlags.All;

        /// <summary>清除颜色值 (RGBA, 线性空间)。</summary>
        public Vector4 ClearColor { get; set; } = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);

        /// <summary>清除深度值。</summary>
        public float ClearDepth { get; set; } = 1.0f;

        /// <summary>清除模板值。</summary>
        public byte ClearStencil { get; set; } = 0;

        /// <summary>是否注册到 RenderGraph (需要 RenderGraph 可用时)。</summary>
        public bool RegisterToRenderGraph { get; set; } = false;

        /// <summary>是否需要异步计算队列 (Compute / AsyncCompute)。</summary>
        public bool UseAsyncCompute { get; set; } = false;

        /// <summary>Pass 优先级 (影响 RenderGraph 拓扑排序)。</summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// 创建默认配置。
        /// </summary>
        public static RenderPassConfig CreateDefault(RenderPass pass)
        {
            return pass switch
            {
                RenderPass.ZPrepass => new RenderPassConfig
                {
                    Name = "Z-Prepass",
                    RenderTargetFormat = RHIFormat.Unknown,
                    DepthFormat = RHIFormat.D32_Float,
                    ClearFlags = RenderPassClearFlags.Depth,
                    Priority = 0
                },
                RenderPass.ShadowMap => new RenderPassConfig
                {
                    Name = "Shadow Map",
                    RenderTargetFormat = RHIFormat.Unknown,
                    DepthFormat = RHIFormat.D32_Float,
                    ClearFlags = RenderPassClearFlags.Depth,
                    Priority = 1
                },
                RenderPass.GeometryBuffer => new RenderPassConfig
                {
                    Name = "GeometryBuffer",
                    RenderTargetFormat = RHIFormat.R16G16B16A16_Float,
                    DepthFormat = RHIFormat.D24_UNorm_S8_UInt,
                    ClearFlags = RenderPassClearFlags.All,
                    Priority = 2
                },
                RenderPass.SSAO => new RenderPassConfig
                {
                    Name = "SSAO",
                    RenderTargetFormat = RHIFormat.R8_UNorm,
                    DepthFormat = RHIFormat.Unknown,
                    ClearFlags = RenderPassClearFlags.Color,
                    ClearColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                    UseAsyncCompute = true,
                    Priority = 3
                },
                RenderPass.LightResolve => new RenderPassConfig
                {
                    Name = "Deferred Lighting",
                    RenderTargetFormat = RHIFormat.R16G16B16A16_Float,
                    DepthFormat = RHIFormat.D24_UNorm_S8_UInt,
                    ClearFlags = RenderPassClearFlags.Color,
                    ClearColor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                    UseAsyncCompute = true,
                    Priority = 4
                },
                RenderPass.ForwardPass => new RenderPassConfig
                {
                    Name = "Forward Pass",
                    RenderTargetFormat = RHIFormat.R16G16B16A16_Float,
                    DepthFormat = RHIFormat.D24_UNorm_S8_UInt,
                    ClearFlags = RenderPassClearFlags.Color,
                    Priority = 5
                },
                RenderPass.Translucency => new RenderPassConfig
                {
                    Name = "Translucency",
                    RenderTargetFormat = RHIFormat.R16G16B16A16_Float,
                    DepthFormat = RHIFormat.D24_UNorm_S8_UInt,
                    ClearFlags = RenderPassClearFlags.None,
                    Priority = 6
                },
                RenderPass.PostProcess => new RenderPassConfig
                {
                    Name = "Post Process",
                    RenderTargetFormat = RHIFormat.R16G16B16A16_Float,
                    DepthFormat = RHIFormat.Unknown,
                    ClearFlags = RenderPassClearFlags.Color,
                    Priority = 7
                },
                RenderPass.UI => new RenderPassConfig
                {
                    Name = "UI Composite",
                    RenderTargetFormat = RHIFormat.R8G8B8A8_UNorm,
                    DepthFormat = RHIFormat.Unknown,
                    ClearFlags = RenderPassClearFlags.Color,
                    ClearColor = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                    Priority = 8
                },
                _ => new RenderPassConfig
                {
                    Name = pass.ToString(),
                    RenderTargetFormat = RHIFormat.R16G16B16A16_Float,
                    DepthFormat = RHIFormat.D24_UNorm_S8_UInt,
                    Priority = (int)pass
                }
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 渲染 Pass 统计信息
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单帧内单个 Pass 的渲染统计信息。
    /// 用于性能分析和自适应质量系统反馈。
    /// </summary>
    public struct RenderPassStats
    {
        /// <summary>Pass 名称。</summary>
        public string PassName;
        /// <summary>DrawCall 数量。</summary>
        public int DrawCallCount;
        /// <summary>三角形数量。</summary>
        public long TriangleCount;
        /// <summary>实例数量。</summary>
        public int InstanceCount;
        /// <summary>GPU 执行时间 (毫秒)。</summary>
        public float GPUTimeMs;
        /// <summary>Dispatch 调用数量 (Compute Shader)。</summary>
        public int DispatchCount;
        /// <summary>线程组总数。</summary>
        public long TotalThreadGroups;
        /// <summary>绑定纹理数。</summary>
        public int TextureBindCount;
        /// <summary>状态切换次数 (PSO / RootSignature)。</summary>
        public int StateChangeCount;

        /// <summary>累计此帧所有 Pass 的 DrawCall 数。</summary>
        public static RenderPassStats operator +(RenderPassStats a, RenderPassStats b)
        {
            return new RenderPassStats
            {
                PassName = "Total",
                DrawCallCount = a.DrawCallCount + b.DrawCallCount,
                TriangleCount = a.TriangleCount + b.TriangleCount,
                InstanceCount = a.InstanceCount + b.InstanceCount,
                GPUTimeMs = a.GPUTimeMs + b.GPUTimeMs,
                DispatchCount = a.DispatchCount + b.DispatchCount,
                TotalThreadGroups = a.TotalThreadGroups + b.TotalThreadGroups,
                TextureBindCount = a.TextureBindCount + b.TextureBindCount,
                StateChangeCount = a.StateChangeCount + b.StateChangeCount
            };
        }

        /// <summary>重置统计。</summary>
        public void Reset()
        {
            PassName = string.Empty;
            DrawCallCount = 0;
            TriangleCount = 0;
            InstanceCount = 0;
            GPUTimeMs = 0f;
            DispatchCount = 0;
            TotalThreadGroups = 0;
            TextureBindCount = 0;
            StateChangeCount = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 渲染目标描述
    // ──────────────────────────────────────────────────────────────

    /// <summary>渲染目标描述，用于创建 RT/DS 纹理。</summary>
    public struct RenderTargetDesc
    {
        public string Name;
        public RHIFormat Format;
        public int Width;
        public int Height;
        public int MipLevels;
        public int SampleCount;
        public bool AllowUAV;

        /// <summary>创建标准颜色 RT 描述。</summary>
        public static RenderTargetDesc ColorRT(string name, int w, int h, RHIFormat fmt,
            int mipLevels = 1, int samples = 1)
            => new() { Name = name, Format = fmt, Width = w, Height = h, MipLevels = mipLevels, SampleCount = samples };

        /// <summary>创建深度 RT 描述。</summary>
        public static RenderTargetDesc DepthRT(string name, int w, int h, RHIFormat fmt = RHIFormat.D24_UNorm_S8_UInt)
            => new() { Name = name, Format = fmt, Width = w, Height = h, MipLevels = 1, SampleCount = 1 };
    }

    // ──────────────────────────────────────────────────────────────
    // 世界渲染上下文
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 每帧渲染上下文，由 WorldRenderPipeline.Execute 接收。
    /// 包含场景数据、相机、分辨率和上一帧历史资源。
    /// </summary>
    public sealed class WorldRenderContext
    {
        /// <summary>主相机数据。</summary>
        public CameraData Camera { get; set; }
        /// <summary>视口宽度 (渲染分辨率, 可能低于屏幕分辨率)。</summary>
        public int RenderWidth { get; set; }
        /// <summary>视口高度 (渲染分辨率)。</summary>
        public int RenderHeight { get; set; }
        /// <summary>屏幕宽度 (显示分辨率)。</summary>
        public int ScreenWidth { get; set; }
        /// <summary>屏幕高度 (显示分辨率)。</summary>
        public int ScreenHeight { get; set; }
        /// <summary>分辨率缩放因子 (自适应质量系统输出)。</summary>
        public float ResolutionScale { get; set; } = 1.0f;
        /// <summary>帧索引 (用于 TAA Jitter / 伪随机)。</summary>
        public int FrameIndex { get; set; }
        /// <summary>帧间隔时间 (秒)。</summary>
        public float DeltaTime { get; set; }
        /// <summary>子像素抖动偏移 (TAA/TSR 用)。</summary>
        public Vector2 JitterOffset { get; set; }
        /// <summary>场景引用 (用于遍历可见物体)。</summary>
        public SceneRendererReal? SceneRenderer { get; set; }
        /// <summary>待渲染的场景。</summary>
        public IRenderScene? Scene { get; set; }
        /// <summary>上一帧的颜色历史纹理 (TAA/TSR 用)。</summary>
        public RHITexture? HistoryColor { get; set; }
        /// <summary>上一帧的深度历史纹理 (运动矢量用)。</summary>
        public RHITexture? HistoryDepth { get; set; }
        /// <summary>上一帧的运动矢量 (TAA/TSR 用)。</summary>
        public RHITexture? HistoryMotionVectors { get; set; }
        /// <summary>外部导入的后处理设置覆盖 (可选)。</summary>
        public PostProcessSettings? OverridePostProcess { get; set; }
        /// <summary>当前帧 GPU 时间预算 (毫秒)。</summary>
        public float GPUTimeBudgetMs { get; set; } = 16.67f;
    }

    // ──────────────────────────────────────────────────────────────
    // Pass 执行回调委托
    // ──────────────────────────────────────────────────────────────

    /// <summary>单个 Pass 的执行回调。</summary>
    public delegate void RenderPassExecuteDelegate(
        RHICommandList cmd,
        WorldRenderContext ctx,
        RenderPassConfig config,
        ref RenderPassStats stats);

    // ──────────────────────────────────────────────────────────────
    // Pass 执行记录 (用于 Profiler / RenderGraph)
    // ──────────────────────────────────────────────────────────────

    /// <summary>单个 Pass 的执行记录。</summary>
    public struct RenderPassRecord
    {
        public RenderPass Pass;
        public RenderPassConfig Config;
        public RenderPassStats Stats;
        public double StartTimestamp;
        public double EndTimestamp;
        public bool Executed;
    }

    // ══════════════════════════════════════════════════════════════
    //  WorldRenderPipeline — 世界渲染管线
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 世界渲染管线: 管理完整的帧渲染流程。
    /// 从 Z-Prepass 到 UI 合成，每个 Pass 可独立配置、启用/禁用，
    /// 并可选注册到 RenderGraph 实现自动化依赖调度。
    /// </summary>
    public sealed class WorldRenderPipeline : IDisposable
    {
        // ───── 内部状态 ─────
        private RHIDevice? _device;
        private bool _initialized;
        private bool _disposed;

        // ───── Pass 配置表 ─────
        private readonly RenderPassConfig[] _passConfigs = new RenderPassConfig[(int)RenderPass.Count];

        // ───── 渲染目标资源池 ─────
        private readonly Dictionary<string, RHITexture> _renderTargets = new();
        private readonly Dictionary<string, RHIDescriptorHandle> _rtvHandles = new();
        private readonly Dictionary<string, RHIDescriptorHandle> _srvHandles = new();
        private readonly Dictionary<string, RHIDescriptorHandle> _dsvHandles = new();
        private readonly Dictionary<string, RHIDescriptorHandle> _uavHandles = new();

        // ───── PSO 缓存 ─────
        private readonly Dictionary<string, RHIPipelineState> _psoCache = new();
        private readonly Dictionary<string, RHIRootSignature> _rootSignatureCache = new();

        // ───── Pass 执行回调 ─────
        private readonly RenderPassExecuteDelegate[] _passExecutors = new RenderPassExecuteDelegate[(int)RenderPass.Count];

        // ───── 每帧统计 ─────
        private readonly RenderPassStats[] _passStats = new RenderPassStats[(int)RenderPass.Count];
        private RenderPassRecord[] _passRecords = new RenderPassRecord[(int)RenderPass.Count];

        // ───── 渲染分辨率 ─────
        private int _currentWidth;

        private RenderGraph? _attachedGraph;

        /// <summary>将管线挂接到指定的渲染图, 后续 Pass 将在该图上注册。</summary>
        public void AttachToRenderGraph(RenderGraph graph)
        {
            _attachedGraph = graph ?? throw new ArgumentNullException(nameof(graph));
        }

        /// <summary>当前挂接的渲染图 (可能为 null)。</summary>
        public RenderGraph? AttachedGraph => _attachedGraph;

        // ── 集成 API (WorldIntegrationHelper 依赖) ──
        private object? _defaultMaterial;
        private object? _errorMaterial;
        private object? _debugMaterial;
        private RHIFormat _renderTargetFormat = RHIFormat.B8G8R8A8_UNorm_SRGB;
        private RHIFormat _depthStencilFormat = RHIFormat.D32_Float_S8X24_UInt;
        private RHIFormat[] _geometryBufferFormats = Array.Empty<RHIFormat>();

        /// <summary>以 RHI 设备构造渲染管线。</summary>
        public WorldRenderPipeline(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>已启用的渲染通道数量。</summary>
        public int EnabledPassCount => _enabledPasses.Count;

        /// <summary>设置渲染目标颜色格式。</summary>
        public void SetRenderTargetFormat(RHIFormat format) => _renderTargetFormat = format;

        /// <summary>设置深度模板格式。</summary>
        public void SetDepthStencilFormat(RHIFormat format) => _depthStencilFormat = format;

        /// <summary>设置几何缓冲通道格式。</summary>
        public void SetGeometryBufferFormats(RHIFormat[] formats)
        {
            _geometryBufferFormats = formats ?? Array.Empty<RHIFormat>();
        }

        /// <summary>注册材质到管线。</summary>
        public void RegisterMaterial(object materialId, object shaderBytecode, object parameterLayout)
        {
            // 管线材质注册由渲染核心实现; 此处为集成占位。
        }

        /// <summary>更新材质着色器。</summary>
        public void UpdateMaterialShader(object materialId, object shaderCode)
        {
        }

        /// <summary>更新材质参数。</summary>
        public void UpdateMaterialParameter(object materialId, string parameterName, object value)
        {
        }

        /// <summary>更新材质纹理绑定。</summary>
        public void UpdateMaterialTexture(object materialId, object slot, object texture)
        {
        }

        /// <summary>设置默认材质。</summary>
        public void SetDefaultMaterial(object material) => _defaultMaterial = material;

        /// <summary>设置错误材质。</summary>
        public void SetErrorMaterial(object material) => _errorMaterial = material;

        /// <summary>设置调试材质。</summary>
        public void SetDebugMaterial(object material) => _debugMaterial = material;
        private int _currentHeight;
        private float _resolutionScale = 1.0f;

        // ───── RenderGraph 集成 ─────
        private RenderGraph? _renderGraph;
        private bool _useRenderGraph;

        // ───── GPU Timer ─────
        private RHITimestampQueryHeap? _timestampHeap;
        private int _timestampIndex;

        /// <summary>当前分辨率宽度。</summary>
        public int CurrentWidth => _currentWidth;
        /// <summary>当前分辨率高度。</summary>
        public int CurrentHeight => _currentHeight;
        /// <summary>当前分辨率缩放。</summary>
        public float ResolutionScale => _resolutionScale;
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;
        /// <summary>绑定的 RenderGraph (可为 null)。</summary>
        public RenderGraph? RenderGraph => _renderGraph;
        /// <summary>是否使用 RenderGraph 调度。</summary>
        public bool UseRenderGraph
        {
            get => _useRenderGraph;
            set => _useRenderGraph = value && _renderGraph != null;
        }

        /// <summary>
        /// 获取指定 Pass 的配置 (只读)。
        /// </summary>
        public RenderPassConfig GetPassConfig(RenderPass pass)
            => _passConfigs[(int)pass];

        // ── 兼容高层 API：EnablePass / SetParameter ──
        private readonly HashSet<RenderPass> _enabledPasses = new();
        private readonly Dictionary<string, object> _parameters = new();

        public void EnablePass(RenderPass pass) => _enabledPasses.Add(pass);
        public void SetParameter(string name, object value) => _parameters[name] = value;
        public T GetParameter<T>(string name, T defaultValue = default)
            => _parameters.TryGetValue(name, out var v) && v is T t ? t : defaultValue;

        /// <summary>
        /// 获取上一帧指定 Pass 的统计信息。
        /// </summary>
        public RenderPassStats GetPassStats(RenderPass pass)
            => _passStats[(int)pass];

        /// <summary>
        /// 获取上一帧所有 Pass 的汇总统计。
        /// </summary>
        public RenderPassStats GetTotalStats()
        {
            RenderPassStats total = default;
            total.PassName = "Frame Total";
            for (int i = 0; i < (int)RenderPass.Count; i++)
                total = total + _passStats[i];
            return total;
        }

        /// <summary>
        /// 获取上一帧的 Pass 执行记录数组。
        /// </summary>
        public ReadOnlySpan<RenderPassRecord> GetPassRecords()
            => _passRecords.AsSpan(0, (int)RenderPass.Count);

        // ════════════════════════════════════════════════════════
        //  初始化
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 初始化世界渲染管线。创建所有 PSO 和渲染目标。
        /// </summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="sceneRenderer">场景渲染器 (用于查询可见物体)。</param>
        /// <param name="width">初始渲染宽度。</param>
        /// <param name="height">初始渲染高度。</param>
        /// <param name="renderGraph">可选的 RenderGraph 实例。</param>
        public void Initialize(RHIDevice device, SceneRendererReal? sceneRenderer,
            int width, int height, RenderGraph? renderGraph = null)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Width and height must be positive.", nameof(width));

            _device = device;
            _currentWidth = width;
            _currentHeight = height;
            _renderGraph = renderGraph;
            _useRenderGraph = renderGraph != null;

            // 1. 初始化所有 Pass 配置
            for (int i = 0; i < (int)RenderPass.Count; i++)
            {
                var pass = (RenderPass)i;
                _passConfigs[i] = RenderPassConfig.CreateDefault(pass);
                _passStats[i].PassName = _passConfigs[i].Name;
                _passRecords[i].Pass = pass;
                _passRecords[i].Config = _passConfigs[i];
            }

            // 2. 创建默认 PSO 和根签名
            CreatePipelineStates();

            // 3. 分配渲染目标
            CreateRenderTargets(width, height);

            // 4. 注册默认 Pass 执行器
            RegisterDefaultExecutors(sceneRenderer);

            // 5. 如果启用 RenderGraph，注册 Pass
            if (_useRenderGraph)
                RegisterRenderGraphPasses();

            // 6. 创建 GPU Timer
            CreateGPUTimer();

            _initialized = true;
        }

        /// <summary>
        /// 简化初始化接口 (自动使用屏幕默认分辨率)。
        /// </summary>
        public void Initialize(RHIDevice device, SceneRendererReal sceneRenderer, RenderGraph? renderGraph = null)
        {
            // 默认使用 1920x1080
            Initialize(device, sceneRenderer, 1920, 1080, renderGraph);
        }

        // ════════════════════════════════════════════════════════
        //  执行渲染
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 执行完整的世界渲染流程。
        /// 按序执行所有已启用的 Pass，收集统计信息。
        /// </summary>
        /// <param name="cmd">RHI 命令列表。</param>
        /// <param name="ctx">渲染上下文。</param>
        public void Execute(RHICommandList cmd, WorldRenderContext ctx)
        {
            if (!_initialized)
                throw new InvalidOperationException("WorldRenderPipeline has not been initialized.");
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            // 检查分辨率是否需要更新
            float targetScale = ctx.ResolutionScale > 0 ? ctx.ResolutionScale : 1.0f;
            int targetWidth = Math.Max(1, (int)(ctx.ScreenWidth * targetScale));
            int targetHeight = Math.Max(1, (int)(ctx.ScreenHeight * targetScale));

            if (targetWidth != _currentWidth || targetHeight != _currentHeight)
                ResizeRenderTargets(targetWidth, targetHeight);

            // 如果使用 RenderGraph，走 RenderGraph 路径
            if (_useRenderGraph && _renderGraph != null)
            {
                ExecuteViaRenderGraph(cmd, ctx);
                return;
            }

            // 手动按序执行
            ExecuteManualPasses(cmd, ctx);
        }

        /// <summary>
        /// 手动按序执行所有已启用的 Pass。
        /// </summary>
        private void ExecuteManualPasses(RHICommandList cmd, WorldRenderContext ctx)
        {
            var frameTotal = new RenderPassStats();
            frameTotal.PassName = "Frame Total";

            for (int i = 0; i < (int)RenderPass.Count; i++)
            {
                var pass = (RenderPass)i;
                ref var config = ref _passConfigs[i];
                ref var record = ref _passRecords[i];

                if (!config.Enabled)
                {
                    record.Executed = false;
                    continue;
                }

                // 开始 GPU Timer
                record.StartTimestamp = 0; // TODO: 插入 Timestamp Query

                var stats = new RenderPassStats { PassName = config.Name };

                // 执行 Pass
                if (_passExecutors[i] != null)
                {
                    _passExecutors[i](cmd, ctx, config, ref stats);
                }

                // 结束 GPU Timer
                record.EndTimestamp = 0; // TODO: 插入 Timestamp Query
                record.Stats = stats;
                record.Executed = true;

                // 累加到帧统计
                _passStats[i] = stats;
                frameTotal = frameTotal + stats;
            }

            _passStats[(int)RenderPass.Count - 1] = frameTotal; // 用最后一个槽位存总量 (备用)
        }

        /// <summary>
        /// 通过 RenderGraph 执行渲染。
        /// 将所有已启用且 RegisterToRenderGraph=true 的 Pass 注册为节点，
        /// 由 RenderGraph 自动完成拓扑排序、资源分配和屏障插入。
        /// </summary>
        private void ExecuteViaRenderGraph(RHICommandList cmd, WorldRenderContext ctx)
        {
            // 重建 RenderGraph (每帧重建，因为资源引用可能变化)
            _renderGraph!.Reset();

            // 注册所有需要的 Pass
            for (int i = 0; i < (int)RenderPass.Count; i++)
            {
                ref var config = ref _passConfigs[i];
                if (!config.Enabled || !config.RegisterToRenderGraph)
                    continue;

                var passType = config.UseAsyncCompute
                    ? RenderGraphPassType.AsyncCompute
                    : RenderGraphPassType.Render;

                var rgPass = new RenderGraphPass(config.Name, passType);

                // TODO: 根据实际 Pass 读写资源，添加 Read/Write 声明
                // 这里仅注册框架，具体资源绑定由各 Pass 的注册函数完成
                RegisterPassResources(ref rgPass, (RenderPass)i, ctx);

                var cfgSnapshot = config;
                rgPass.SetExecute((cmdList, rgCtx) =>
                {
                    var stats = new RenderPassStats { PassName = cfgSnapshot.Name };
                    if (_passExecutors[i] != null)
                        _passExecutors[i](cmdList, ctx, cfgSnapshot, ref stats);
                    _passStats[i] = stats;
                });

                _renderGraph.AddPass(rgPass);
            }

            // 编译并执行
            var compiledPlan = _renderGraph.Compile();
            if (compiledPlan.IsValid)
            {
                _renderGraph.Execute(cmd);
            }
            else
            {
                // RenderGraph 编译失败，回退到手动执行
                ExecuteManualPasses(cmd, ctx);
            }
        }

        // ════════════════════════════════════════════════════════
        //  Pass 启用/禁用控制
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 动态设置指定 Pass 的启用状态。
        /// </summary>
        /// <param name="pass">要控制的 Pass。</param>
        /// <param name="enabled">是否启用。</param>
        public void SetPassEnabled(RenderPass pass, bool enabled)
        {
            int idx = (int)pass;
            if (idx < 0 || idx >= (int)RenderPass.Count)
                throw new ArgumentOutOfRangeException(nameof(pass));

            _passConfigs[idx].Enabled = enabled;

            // 级联禁用依赖 Pass
            if (!enabled)
            {
                switch (pass)
                {
                    case RenderPass.GeometryBuffer:
                        // GeometryBuffer 禁用时，SSAO 和 LightResolve 也无意义
                        _passConfigs[(int)RenderPass.SSAO].Enabled = false;
                        _passConfigs[(int)RenderPass.LightResolve].Enabled = false;
                        break;
                    case RenderPass.ZPrepass:
                        // ZPrepass 禁用时不影响其他 Pass，但 GeometryBuffer 需要自行生成深度
                        break;
                }
            }
        }

        /// <summary>
        /// 获取指定 Pass 是否启用。
        /// </summary>
        public bool IsPassEnabled(RenderPass pass)
            => _passConfigs[(int)pass].Enabled;

        /// <summary>
        /// 批量设置多个 Pass 的启用状态。
        /// </summary>
        public void SetPassesEnabled(IReadOnlyList<(RenderPass pass, bool enabled)> settings)
        {
            if (settings == null) return;
            foreach (var (pass, enabled) in settings)
                SetPassEnabled(pass, enabled);
        }

        // ════════════════════════════════════════════════════════
        //  分辨率管理
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 更新渲染分辨率 (由自适应质量系统调用)。
        /// </summary>
        /// <param name="scale">分辨率缩放因子 (0.5 ~ 1.5)。</param>
        public void SetResolutionScale(float scale)
        {
            _resolutionScale = Math.Clamp(scale, 0.25f, 2.0f);
        }

        /// <summary>
        /// 重建所有渲染目标以适应新分辨率。
        /// </summary>
        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (width == _currentWidth && height == _currentHeight) return;

            ResizeRenderTargets(width, height);
        }

        // ════════════════════════════════════════════════════════
        //  自定义 Pass 注册
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 注册自定义 Pass 执行器。
        /// </summary>
        /// <param name="pass">目标 Pass。</param>
        /// <param name="executor">执行回调。</param>
        public void SetPassExecutor(RenderPass pass, RenderPassExecuteDelegate executor)
        {
            int idx = (int)pass;
            if (idx < 0 || idx >= (int)RenderPass.Count)
                throw new ArgumentOutOfRangeException(nameof(pass));

            _passExecutors[idx] = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        /// <summary>
        /// 修改指定 Pass 的配置。
        /// </summary>
        public void ConfigurePass(RenderPass pass, Action<RenderPassConfig> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            int idx = (int)pass;
            if (idx < 0 || idx >= (int)RenderPass.Count)
                throw new ArgumentOutOfRangeException(nameof(pass));

            configure(_passConfigs[idx]);
        }

        // ════════════════════════════════════════════════════════
        //  资源获取
        // ════════════════════════════════════════════════════════

        /// <summary>获取指定名称的渲染目标纹理。</summary>
        public RHITexture? GetRenderTarget(string name)
            => _renderTargets.TryGetValue(name, out var tex) ? tex : null;

        /// <summary>获取指定名称的 RTV 句柄。</summary>
        public RHIDescriptorHandle GetRTV(string name)
            => _rtvHandles.TryGetValue(name, out var h) ? h : default;

        /// <summary>获取指定名称的 DSV 句柄。</summary>
        public RHIDescriptorHandle GetDSV(string name)
            => _dsvHandles.TryGetValue(name, out var h) ? h : default;

        /// <summary>获取指定名称的 SRV 句柄。</summary>
        public RHIDescriptorHandle GetSRV(string name)
            => _srvHandles.TryGetValue(name, out var h) ? h : default;

        /// <summary>获取指定名称的 UAV 句柄。</summary>
        public RHIDescriptorHandle GetUAV(string name)
            => _uavHandles.TryGetValue(name, out var h) ? h : default;

        // ════════════════════════════════════════════════════════
        //  内部: PSO 创建
        // ════════════════════════════════════════════════════════

        private void CreatePipelineStates()
        {
            // Z-Prepass PSO
            CreatePSO("ZPrepass", WorldShaders.ZPrepassVS, WorldShaders.ZPrepassPS,
                RHIFormat.Unknown, RHIFormat.D32_Float,
                depthEnable: true, depthWrite: true);

            // GeometryBuffer PSO (MRT: Albedo + Normal + Specular + MotionVectors)
            CreatePSO("GeometryBuffer", WorldShaders.GeometryBufferVS, WorldShaders.GeometryBufferPS,
                RHIFormat.R16G16B16A16_Float, RHIFormat.D24_UNorm_S8_UInt,
                depthEnable: true, depthWrite: true, renderTargetCount: 4);

            // Deferred Lighting PSO (全屏三角形)
            CreatePSO("LightResolve_Fullscreen", WorldShaders.LightResolveCS, null,
                RHIFormat.R16G16B16A16_Float, RHIFormat.Unknown,
                depthEnable: false, depthWrite: false, isCompute: true);

            // SSAO PSO (Compute)
            CreatePSO("SSAO", WorldShaders.SSAOCS, null,
                RHIFormat.R8_UNorm, RHIFormat.Unknown,
                depthEnable: false, depthWrite: false, isCompute: true);

            // PostProcess PSO 列表
            CreatePSO("ToneMapping", null, WorldShaders.ToneMappingPS,
                RHIFormat.R16G16B16A16_Float, RHIFormat.Unknown,
                depthEnable: false, depthWrite: false);

            CreatePSO("FXAA", null, WorldShaders.FXAAPS,
                RHIFormat.R8G8B8A8_UNorm, RHIFormat.Unknown,
                depthEnable: false, depthWrite: false);

            CreatePSO("TAAResolve", null, WorldShaders.TAAResolvePS,
                RHIFormat.R16G16B16A16_Float, RHIFormat.Unknown,
                depthEnable: false, depthWrite: false);

            CreatePSO("Bloom", null, WorldShaders.BloomPS,
                RHIFormat.R16G16B16A16_Float, RHIFormat.Unknown,
                depthEnable: false, depthWrite: false);

            CreatePSO("MotionBlur", null, WorldShaders.MotionBlurPS,
                RHIFormat.R16G16B16A16_Float, RHIFormat.Unknown,
                depthEnable: false, depthWrite: false);
        }

        private void CreatePSO(string name, string? vs, string? ps,
            RHIFormat rtFormat, RHIFormat dsFormat,
            bool depthEnable = true, bool depthWrite = true,
            int renderTargetCount = 1, bool isCompute = false)
        {
            if (_device == null) return;

            // 在真实实现中，这里会调用 RHI 设备创建 PSO。
            // 当前为框架占位，实际创建逻辑由 RHI Backend 完成。
            // _psoCache[name] = _device.CreateGraphicsPipelineState(...);
        }

        // ════════════════════════════════════════════════════════
        //  内部: 渲染目标创建
        // ════════════════════════════════════════════════════════

        private void CreateRenderTargets(int width, int height)
        {
            if (_device == null) return;

            // GeometryBuffer 纹理 (4个 MRT)
            CreateTexture("GeometryBuffer_Albedo", width, height, RHIFormat.R16G16B16A16_Float);
            CreateTexture("GeometryBuffer_Normal", width, height, RHIFormat.R16G16B16A16_SNorm);
            CreateTexture("GeometryBuffer_Specular", width, height, RHIFormat.R8G8B8A8_UNorm);
            CreateTexture("GeometryBuffer_MotionVectors", width, height, RHIFormat.R16G16_Float);

            // 深度缓冲 (Z-Prepass / GeometryBuffer 共享)
            CreateTexture("DepthBuffer", width, height, RHIFormat.D24_UNorm_S8_UInt);
            CreateTexture("DepthBuffer_ZPre", width, height, RHIFormat.D32_Float);

            // 光照输出
            CreateTexture("Lighting_Output", width, height, RHIFormat.R16G16B16A16_Float);

            // SSAO 输出
            CreateTexture("SSAO_Output", width, height, RHIFormat.R8_UNorm);

            // 后处理中间缓冲
            CreateTexture("PostProcess_HalfRes", width / 2, height / 2, RHIFormat.R16G16B16A16_Float);
            CreateTexture("PostProcess_Bloom", width / 2, height / 2, RHIFormat.R16G16B16A16_Float);
            CreateTexture("PostProcess_Temp", width, height, RHIFormat.R16G16B16A16_Float);

            // 半透明合成
            CreateTexture("Translucency_Output", width, height, RHIFormat.R16G16B16A16_Float);

            // 最终输出 (HDR)
            CreateTexture("SceneColor_HDR", width, height, RHIFormat.R16G16B16A16_Float);

            // 最终输出 (LDR / UI)
            CreateTexture("SceneColor_LDR", width, height, RHIFormat.R8G8B8A8_UNorm);
        }

        private void ResizeRenderTargets(int newWidth, int newHeight)
        {
            // 释放旧资源
            foreach (var tex in _renderTargets.Values)
                tex?.Dispose();
            _renderTargets.Clear();
            _rtvHandles.Clear();
            _srvHandles.Clear();
            _dsvHandles.Clear();
            _uavHandles.Clear();

            _currentWidth = newWidth;
            _currentHeight = newHeight;

            // 重新创建
            CreateRenderTargets(newWidth, newHeight);
        }

        private void CreateTexture(string name, int width, int height, RHIFormat format)
        {
            if (_device == null) return;
            // 在真实实现中，调用 RHI 设备创建纹理和描述符
            // var tex = _device.CreateTexture(new RHITextureDesc { Width = width, Height = height, Format = format, ... });
            // _renderTargets[name] = tex;
            // _srvHandles[name] = _device.CreateShaderResourceView(tex);
        }

        // ════════════════════════════════════════════════════════
        //  内部: 默认 Pass 执行器注册
        // ════════════════════════════════════════════════════════

        private void RegisterDefaultExecutors(SceneRendererReal? sceneRenderer)
        {
            // Z-Prepass
            _passExecutors[(int)RenderPass.ZPrepass] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // 设置 RT/DS
                // 绑定 ZPrepass PSO
                // 遍历场景不透明物体，绘制深度
                // 统计
                stats.DrawCallCount = 0; // 由实际绘制填充
                stats.TriangleCount = 0;
                stats.GPUTimeMs = 0f;
            };

            // Shadow Map
            _passExecutors[(int)RenderPass.ShadowMap] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // 遍历投射阴影的光源
                // 对每个光源渲染 CSM 级联
                stats.DrawCallCount = 0;
                stats.TriangleCount = 0;
                stats.GPUTimeMs = 0f;
            };

            // GeometryBuffer
            _passExecutors[(int)RenderPass.GeometryBuffer] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // 绑定 GeometryBuffer MRT (Albedo, Normal, Specular, MotionVectors)
                // 绑定 GeometryBuffer PSO
                // 遍历场景不透明物体，填充 GeometryBuffer
                stats.DrawCallCount = 0;
                stats.TriangleCount = 0;
                stats.GPUTimeMs = 0f;
            };

            // SSAO
            _passExecutors[(int)RenderPass.SSAO] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // Dispatch SSAO Compute Shader
                // 输入: Depth + Normal
                // 输出: SSAO_Output (R8)
                stats.DispatchCount = 1;
                stats.GPUTimeMs = 0f;
            };

            // Deferred Lighting
            _passExecutors[(int)RenderPass.LightResolve] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // Dispatch LightResolve Compute Shader (Tile/Cluster)
                // 输入: GeometryBuffer (Albedo, Normal, Specular, Depth)
                // 输入: Light Buffer + Shadow Maps
                // 输出: Lighting_Output (HDR)
                stats.DispatchCount = 1;
                stats.GPUTimeMs = 0f;
            };

            // Forward Pass (半透明自发光等特殊材质)
            _passExecutors[(int)RenderPass.ForwardPass] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // 绑定 Forward PSO
                // 遍历场景中需要前向渲染的物体
                stats.DrawCallCount = 0;
                stats.TriangleCount = 0;
                stats.GPUTimeMs = 0f;
            };

            // Translucency
            _passExecutors[(int)RenderPass.Translucency] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // 按深度排序半透明物体
                // 从远到近绘制
                stats.DrawCallCount = 0;
                stats.TriangleCount = 0;
                stats.GPUTimeMs = 0f;
            };

            // Post Process
            _passExecutors[(int)RenderPass.PostProcess] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // 1. Bloom (降采样 -> 高斯模糊 -> 升采样叠加)
                // 2. Tone Mapping (ACES/Reinhard/Uncharted2)
                // 3. Motion Blur
                // 4. TAA Resolve
                // 5. FXAA (最终 LDR 输出)
                stats.DispatchCount = 0;
                stats.DrawCallCount = 5; // 大约5个全屏 Pass
                stats.GPUTimeMs = 0f;
            };

            // UI
            _passExecutors[(int)RenderPass.UI] = (RHICommandList cmd, WorldRenderContext ctx, RenderPassConfig config, ref RenderPassStats stats) =>
            {
                // 合成 UI 元素到最终输出
                stats.DrawCallCount = 0;
                stats.TriangleCount = 0;
                stats.GPUTimeMs = 0f;
            };
        }

        // ════════════════════════════════════════════════════════
        //  内部: RenderGraph Pass 注册
        // ════════════════════════════════════════════════════════

        private void RegisterPassResources(ref RenderGraphPass rgPass, RenderPass pass, WorldRenderContext ctx)
        {
            // 根据 Pass 类型声明资源读写依赖
            switch (pass)
            {
                case RenderPass.ZPrepass:
                    // 写入 DepthBuffer_ZPre
                    break;

                case RenderPass.ShadowMap:
                    // 写入 Shadow Atlas
                    // 读取场景深度
                    break;

                case RenderPass.GeometryBuffer:
                    // 读取 DepthBuffer_ZPre (可选)
                    // 写入 GeometryBuffer_Albedo, GeometryBuffer_Normal, GeometryBuffer_Specular, GeometryBuffer_MotionVectors
                    // 写入 DepthBuffer
                    break;

                case RenderPass.SSAO:
                    // 读取 DepthBuffer, GeometryBuffer_Normal
                    // 写入 SSAO_Output
                    break;

                case RenderPass.LightResolve:
                    // 读取 GeometryBuffer_*, SSAO_Output, Shadow Maps
                    // 写入 Lighting_Output
                    break;

                case RenderPass.ForwardPass:
                    // 读取 DepthBuffer, Lighting_Output (作为背景)
                    // 写入 SceneColor_HDR
                    break;

                case RenderPass.Translucency:
                    // 读取 SceneColor_HDR, DepthBuffer
                    // 写入 Translucency_Output
                    break;

                case RenderPass.PostProcess:
                    // 读取 SceneColor_HDR / Translucency_Output
                    // 读取 GeometryBuffer_MotionVectors (TAA / Motion Blur)
                    // 写入 SceneColor_LDR
                    break;

                case RenderPass.UI:
                    // 读取 SceneColor_LDR
                    // 写入 BackBuffer (副作用)
                    rgPass.SetHasSideEffects();
                    break;
            }
        }

        private void RegisterRenderGraphPasses()
        {
            // 设置所有 Pass 默认注册到 RenderGraph
            for (int i = 0; i < (int)RenderPass.Count; i++)
            {
                _passConfigs[i].RegisterToRenderGraph = true;
            }
        }

        // ════════════════════════════════════════════════════════
        //  内部: GPU Timer
        // ════════════════════════════════════════════════════════

        private void CreateGPUTimer()
        {
            // 在真实实现中创建 Timestamp Query Heap
            // _timestampHeap = _device.CreateTimestampQueryHeap((int)RenderPass.Count * 2);
        }

        private void BeginGPUTimer(RHICommandList cmd, int passIndex)
        {
            // 插入 Begin Timestamp Query
        }

        private void EndGPUTimer(RHICommandList cmd, int passIndex)
        {
            // 插入 End Timestamp Query
        }

        // ════════════════════════════════════════════════════════
        //  诊断信息
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 生成当前管线状态的诊断字符串。
        /// </summary>
        public string GetDiagnosticsString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== WorldRenderPipeline Diagnostics ===");
            sb.AppendLine($"Resolution: {_currentWidth}x{_currentHeight} (Scale: {_resolutionScale:F2})");
            sb.AppendLine($"Initialized: {_initialized}");
            sb.AppendLine($"RenderGraph: {(_useRenderGraph ? "Enabled" : "Disabled")}");
            sb.AppendLine($"Render Targets: {_renderTargets.Count}");
            sb.AppendLine($"PSO Cache: {_psoCache.Count}");
            sb.AppendLine();
            sb.AppendLine("Pass States:");
            for (int i = 0; i < (int)RenderPass.Count; i++)
            {
                ref var config = ref _passConfigs[i];
                ref var stats = ref _passStats[i];
                string state = config.Enabled ? "ON " : "OFF";
                sb.AppendLine($"  [{state}] {config.Name,-20} DC:{stats.DrawCallCount,6}  " +
                    $"Tri:{stats.TriangleCount,12}  GPU:{stats.GPUTimeMs,6:0.00}ms");
            }
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════
        //  释放资源
        // ════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 释放渲染目标
            foreach (var tex in _renderTargets.Values)
                tex?.Dispose();
            _renderTargets.Clear();

            // 释放 PSO
            foreach (var pso in _psoCache.Values)
                pso?.Dispose();
            _psoCache.Clear();

            // 释放根签名
            foreach (var rs in _rootSignatureCache.Values)
                rs?.Dispose();
            _rootSignatureCache.Clear();

            // 释放 Timer
            _timestampHeap?.Dispose();
            _timestampHeap = null;

            _initialized = false;
        }
    }
}