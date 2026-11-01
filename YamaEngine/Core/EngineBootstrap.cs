using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using YamaEngine.RHI;
using YamaEngine.Rendering;
using YamaEngine.Physics;
using YamaEngine.Animation;
using YamaEngine.Scripting;
using YamaEngine.ECS;
using YamaEngine.Assets;
using YamaEngine.Audio;
using YamaEngine.Particles;
using YamaEngine.World;
using YamaEngine.AI;
using YamaEngine.Editor;
using YamaEngine.Materials;
using YamaEngine.UI;
using YamaEngine.Networking;
using YamaEngine.Input;
using YamaEngine.Scenes;
using YamaEngine.Build;
using YamaEngine.Core;

namespace YamaEngine.Core
{
    /// <summary>
    /// yama引擎启动器 —— 统一初始化和驱动所有引擎子系统。
    /// 这是整个引擎的入口点，负责按正确顺序初始化所有系统、
    /// 管理主循环、处理帧间资源回收和优雅关闭。
    /// </summary>
    public sealed class EngineBootstrap : IDisposable
    {
        // ──────────────────────────────────────────────────────────
        // 单例
        // ──────────────────────────────────────────────────────────

        private static EngineBootstrap? _instance;
        public static EngineBootstrap Instance => _instance ??= new EngineBootstrap();

        // ──────────────────────────────────────────────────────────
        // 核心系统引用
        // ──────────────────────────────────────────────────────────

        /// <summary>RHI 设备（D3D12 或 Vulkan）</summary>
        public RHIDevice RHI { get; private set; } = null!;

        /// <summary>命令队列（图形）</summary>
        public RHICommandQueue GraphicsQueue { get; private set; } = null!;

        /// <summary>交换链</summary>
        public RHISwapChain? SwapChain { get; private set; }

        /// <summary>帧同步 Fence</summary>
        public RHIFence FrameFence { get; private set; } = null!;

        /// <summary>ECS 世界</summary>
        public EntityComponentSystem ECS { get; private set; } = null!;

        /// <summary>渲染器（前向或延迟）</summary>
        public dynamic Renderer { get; private set; } = null!;

        /// <summary>物理世界（基于 PhysicsWorldReal 纯 C# 实现）</summary>
        public PhysicsWorldReal Physics { get; private set; } = null!;

        /// <summary>动画系统</summary>
        public AnimationSystem Animation { get; private set; } = null!;

        /// <summary>脚本引擎</summary>
        public ScriptEngine Scripts { get; private set; } = null!;

        /// <summary>资源数据库</summary>
        public AssetDatabase Assets { get; private set; } = null!;

        /// <summary>音频引擎</summary>
        public AdvancedAudioEngine Audio { get; private set; } = null!;

        /// <summary>GPU 粒子系统</summary>
        public GPUParticleSystem Particles { get; private set; } = null!;

        /// <summary>世界流式加载</summary>
        public WorldStreamingSystem WorldStreaming { get; private set; } = null!;

        /// <summary>导航系统</summary>
        public NavigationSystem Navigation { get; private set; } = null!;

        /// <summary>材质系统</summary>
        public MaterialEditorSystem MaterialSystem { get; private set; } = null!;

        /// <summary>UI 根</summary>
        public UmgRoot? UI { get; private set; }

        /// <summary>网络复制</summary>
        public NetworkReplicationSystem? Network { get; private set; }

        /// <summary>输入系统</summary>
        public InputSystem Input { get; private set; } = null!;

        // ──────────────────────────────────────────────────────────
        // 帧管理
        // ──────────────────────────────────────────────────────────

        /// <summary>每帧的命令列表（每帧一个，N 帧轮换）</summary>
        private RHICommandList[] _frameCommandLists;
private int _framesInFlight = 2;
        private int _frameIndex;
        private ulong _frameCount;
        private readonly Stopwatch _timer = new();
        private double _lastFrameTime;
        private float _deltaTime;

        /// <summary>每帧分配上限的描述符</summary>
        private RHIDescriptorHeap? _cbvSrvUavHeap;
        private RHIDescriptorHeap? _samplerHeap;
        private RHIDescriptorHeap? _rtvHeap;
        private RHIDescriptorHeap? _dsvHeap;

        // ──────────────────────────────────────────────────────────
        // 渲染子系统引用
        // ──────────────────────────────────────────────────────────

        public MicroClusterVirtualGeometry? MicroCluster { get; private set; }
        public ProbeFieldGlobalIllumination? ProbeField { get; private set; }
        public TemporalSuperResolution? TSR { get; private set; }
        public PathTracer? PathTracer { get; private set; }
        public PostProcessingStack? PostProcess { get; private set; }
        public ShadowSystem? Shadows { get; private set; }

        // ──────────────────────────────────────────────────────────
        // 引擎配置
        // ──────────────────────────────────────────────────────────

        public EngineConfig Config { get; set; } = new();

        /// <summary>是否运行中</summary>
        public bool IsRunning { get; private set; }

        /// <summary>是否暂停（编辑器 Play 模式暂停）</summary>
        public bool IsPaused { get; set; }

        /// <summary>是否为编辑器模式</summary>
        public bool IsEditorMode { get; set; } = true;

        // ──────────────────────────────────────────────────────────
        // 统计
        // ──────────────────────────────────────────────────────────

        public EngineStats Stats { get; } = new();

        // ──────────────────────────────────────────────────────────
        // 初始化
        // ──────────────────────────────────────────────────────────

        private EngineBootstrap()
        {
            _frameCommandLists = new RHICommandList[_framesInFlight];
        }

        /// <summary>
        /// 初始化整个引擎。通过 ModuleManager 模块化注册所有子系统，
        /// 按依赖拓扑自动排序初始化。
        /// </summary>
        public void Initialize(EngineConfig? config = null)
        {
            if (config != null) Config = config;

            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║  yama引擎 YamaEngine — 正在初始化...       ║");
            Console.WriteLine("║  版本: 1.0.0 | 模式: ModuleManager           ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");

            var sw = Stopwatch.StartNew();

            // ── 注册核心模块（EngineModule.cs 中的内置模块通过自动扫描注册） ──
            ModuleDiscovery.AutoRegisterFromAssembly();

            // ── 注册运行时模块 ──
            // RHI
            var rhiMod = new RHIModule();
            rhiMod.SetConfig(Config);
            ModuleManager.Register(rhiMod);

            // ECS
            var ecsMod = new ECSModule();
            ModuleManager.Register(ecsMod);

            // 资源
            var assetMod = new AssetModule();
            assetMod.SetAssetRoot(Config.AssetRootPath);
            ModuleManager.Register(assetMod);

            // 物理（统一 PhysicsWorldReal）
            var physicsMod = new PhysicsModule();
            ModuleManager.Register(physicsMod);

            // 动画
            var animMod = new AnimationModule();
            ModuleManager.Register(animMod);

            // 脚本
            var scriptMod = new ScriptModule();
            scriptMod.SetScriptPath(Config.ScriptSourcePath);
            ModuleManager.Register(scriptMod);

            // 渲染
            var renderMod = new RenderModule();
            renderMod.SetConfig(Config);
            ModuleManager.Register(renderMod);

            // 世界/导航/音频
            var worldMod = new WorldModule();
            worldMod.SetConfig(Config);
            ModuleManager.Register(worldMod);

            // 材质
            var matMod = new MaterialModule();
            ModuleManager.Register(matMod);

            // UI
            var uiMod = new UIModule();
            uiMod.SetIsEditorMode(IsEditorMode);
            ModuleManager.Register(uiMod);

            // 输入
            var inputMod = new InputModule();
            ModuleManager.Register(inputMod);

            // 网络
            if (Config.EnableNetworking)
            {
                var netMod = new NetworkModule();
                netMod.Enable(Config.NetworkConfig);
                ModuleManager.Register(netMod);
            }

            // ── 初始化所有模块（按依赖拓扑排序） ──
            bool success = ModuleManager.InitializeAll();
            if (!success)
                Console.WriteLine("⚠ 引擎部分模块初始化失败，请检查日志。");

            // ── 从模块提取引用到便捷属性 ──
            var rhi = ModuleManager.Get<RHIModule>();
            if (rhi != null)
            {
                RHI = rhi.Device;
                GraphicsQueue = rhi.GraphicsQueue;
                SwapChain = rhi.SwapChain;
                FrameFence = rhi.FrameFence;
                _framesInFlight = rhi.FramesInFlight;
                _frameCommandLists = rhi.FrameCommandLists;
                _cbvSrvUavHeap = rhi.CbvSrvUavHeap;
                _samplerHeap = rhi.SamplerHeap;
                _rtvHeap = rhi.RtvHeap;
                _dsvHeap = rhi.DsvHeap;
            }

            var ecs = ModuleManager.Get<ECSModule>();
            ECS = ecs?.ECS ?? new EntityComponentSystem();

            var assets = ModuleManager.Get<AssetModule>();
            Assets = assets?.Assets ?? new AssetDatabase(Config.AssetRootPath);

            var physics = ModuleManager.Get<PhysicsModule>();
            Physics = physics?.PhysicsWorld as PhysicsWorldReal
                ?? new PhysicsWorldReal(YamaEngine.Physics.PhysicsWorldConfig.Default);
            PhysicsWorldReal.Instance = Physics;

            var anim = ModuleManager.Get<AnimationModule>();
            Animation = anim?.Animation ?? new AnimationSystem();

            var scripts = ModuleManager.Get<ScriptModule>();
            Scripts = scripts?.Scripts ?? new ScriptEngine(ECS, new EngineSystems());

            var render = ModuleManager.Get<RenderModule>();
            if (render != null)
            {
                Shadows = render.Shadows;
                MicroCluster = render.MicroCluster;
                ProbeField = render.ProbeField;
                TSR = render.TSR;
                PathTracer = render.PathTracer;
                PostProcess = render.PostProcess;
                Renderer = render.Renderer;
                Particles = render.Particles;
            }

            var world = ModuleManager.Get<WorldModule>();
            if (world != null)
            {
                WorldStreaming = world.WorldStreaming;
                Navigation = world.Navigation;
                Audio = world.Audio;
            }

            var mat = ModuleManager.Get<MaterialModule>();
            MaterialSystem = mat?.MaterialSystem ?? new MaterialEditorSystem(RHI);

            var ui = ModuleManager.Get<UIModule>();
            UI = ui?.UI;

            var input = ModuleManager.Get<InputModule>();
            Input = input?.Input ?? new InputSystem();

            var net = ModuleManager.Get<NetworkModule>();
            Network = net?.Network;

            // ── 启动引擎循环 ──
            _timer.Start();
            IsRunning = true;

            sw.Stop();
            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine($"║  引擎初始化完成！耗时: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"║  渲染后端: {RHI?.AdapterName ?? "N/A"}");
            Console.WriteLine($"║  帧缓冲数: {_framesInFlight} (Frames in Flight)");
            Console.WriteLine($"║  模块数: {ModuleManager.ModuleCount} ({ModuleManager.FailedModuleCount} 失败)");
            Console.WriteLine("╚══════════════════════════════════════════════╝");
        }

        // ──────────────────────────────────────────────────────────
        // 主循环
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 执行一帧。由外部消息循环或编辑器调用。
        /// </summary>
        public void Tick()
        {
            if (!IsRunning) return;

            // 计算帧时间
            double now = _timer.Elapsed.TotalSeconds;
            _deltaTime = (float)(now - _lastFrameTime);
            _lastFrameTime = now;
            _frameCount++;

            Stats.FrameTime = _deltaTime * 1000.0f;
            Stats.FPS = 1.0f / _deltaTime;
            Stats.FrameCount = _frameCount;

            if (IsPaused)
            {
                // 暂停模式下仍渲染但不模拟
                RenderFrame();
                return;
            }

            // ── 模拟阶段 ──
            Stats.BeginPhase("Simulation");

            // 输入更新
            Input.Update(_deltaTime);

            // 脚本 OnUpdate
            Scripts.Update(_deltaTime);

            // 物理步进
            Physics.Step(_deltaTime);

            // 动画更新
            Animation.Update(_deltaTime);

            // 世界流式加载
            WorldStreaming.Update(_deltaTime);

            // 导航更新
            Navigation.Update(_deltaTime);

            // 粒子模拟
            Particles.Update(_deltaTime, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity);

            // 音频更新
            Audio.Update(_deltaTime);

            // 网络复制
            Network?.Update(_deltaTime);

            // UI 更新
            UI?.Update(_deltaTime);

            Stats.EndPhase("Simulation");

            // ── 渲染阶段 ──
            RenderFrame();

            // ── 帧结束 ──
            ProcessEndOfFrame();
        }

        /// <summary>
        /// 执行完整渲染管线。
        /// </summary>
        private void RenderFrame()
        {
            Stats.BeginPhase("Render");

            int fi = _frameIndex;
            var cmd = _frameCommandLists[fi];
            cmd.Reset();

            // 等待此帧的命令列表完成
            ulong fenceValue = _frameCount;
            if (FrameFence.CompletedValue < fenceValue - 1)
                FrameFence.Wait(fenceValue - 1);

            cmd.Open();

            // 渲染场景
            var renderContext = new RenderContext
            {
                CommandList = cmd,
                Device = RHI,
                ECS = ECS,
                Camera = ActiveCamera,
                Viewport = new RHIViewport(Config.WindowWidth, Config.WindowHeight),
                DeltaTime = _deltaTime,
                FrameCount = _frameCount
            };

            // 设置渲染目标
            RHIDescriptorHandle rtv = default;
            if (SwapChain != null)
            {
                int bbIndex = SwapChain.CurrentBackBufferIndex;
                var backBuffer = SwapChain.GetBackBuffer(bbIndex);

                // 过渡到渲染目标
                cmd.TransitionTo(backBuffer, RHIResourceState.RenderTarget);

                // 清屏
                rtv = RHI.CreateRTV(backBuffer);
                cmd.ClearRenderTargetView(rtv, new Vector4(0.1f, 0.1f, 0.12f, 1.0f));
            }

            // 执行渲染器
            Renderer.Render(renderContext);

            // 渲染粒子
            Particles.Render(cmd, rtv, null, renderContext.Viewport);

            // 渲染 UI
            UI?.Render(renderContext.Viewport);

            // 过渡回 Present
            if (SwapChain != null)
            {
                int bbIndex = SwapChain.CurrentBackBufferIndex;
                var backBuffer = SwapChain.GetBackBuffer(bbIndex);
                cmd.TransitionTo(backBuffer, RHIResourceState.Present);
            }

            cmd.Close();

            // 执行
            GraphicsQueue.ExecuteCommandLists(new[] { cmd });

            // 信号
            GraphicsQueue.Signal(FrameFence, fenceValue);

            // 呈现
            SwapChain?.Present(Config.VSync);

            Stats.EndPhase("Render");
        }

        /// <summary>
        /// 帧结束处理：资源回收、轮换帧索引。
        /// </summary>
        private void ProcessEndOfFrame()
        {
            // 处理延迟释放
            RHI.ProcessDeferredReleases(FrameFence.CompletedValue);

            // 轮换帧索引
            _frameIndex = (_frameIndex + 1) % _framesInFlight;

            // 更新统计
            Stats.Update();
        }

        // ──────────────────────────────────────────────────────────
        // 活动相机
        // ──────────────────────────────────────────────────────────

        private RenderCamera? _activeCamera;
        public RenderCamera? ActiveCamera
        {
            get => _activeCamera;
            set => _activeCamera = value;
        }

        // ──────────────────────────────────────────────────────────
        // 窗口大小变化
        // ──────────────────────────────────────────────────────────

        public void OnResize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            // 等待 GPU 空闲
            GraphicsQueue.Flush();

            Config.WindowWidth = width;
            Config.WindowHeight = height;

            SwapChain?.Resize(width, height);
            Renderer.OnResize(width, height);
            TSR?.Resize(width, height);
            PostProcess?.Resize(width, height);
            UI?.OnResize(width, height);

            Console.WriteLine($"[Engine] 窗口大小已调整: {width}x{height}");
        }

        // ──────────────────────────────────────────────────────────
        // 关闭
        // ──────────────────────────────────────────────────────────

        public void Shutdown()
        {
            Console.WriteLine("[Engine] 正在关闭引擎...");

            IsRunning = false;

            // 等待 GPU 完成
            GraphicsQueue?.Flush();

            // 通过 ModuleManager 按依赖逆序关闭所有模块
            ModuleManager.ShutdownAll();

            // 重置便捷引用
            RHI = null!;
            GraphicsQueue = null!;
            SwapChain = null;
            FrameFence = null!;
            ECS = null!;
            Renderer = null!;
            Physics = null!;
            Animation = null!;
            Scripts = null!;
            Assets = null!;
            Audio = null!;
            Particles = null!;
            WorldStreaming = null!;
            Navigation = null!;
            MaterialSystem = null!;
            UI = null;
            Network = null;
            Input = null!;
            Shadows = null!;
            MicroCluster = null;
            ProbeField = null;
            TSR = null;
            PathTracer = null;
            PostProcess = null;

            Console.WriteLine("[Engine] 引擎已关闭。");
        }

        public void Dispose() => Shutdown();
    }

    // ──────────────────────────────────────────────────────────
    // 引擎配置
    // ──────────────────────────────────────────────────────────

    public class EngineConfig
    {
        // 渲染
        public RHIBackendType Backend = RHIBackendType.Auto;
        public bool EnableDebugLayer = true;
        public bool EnableGPUValidation = false;
        public int WindowWidth = 1920;
        public int WindowHeight = 1080;
        public bool VSync = true;
        public IntPtr OutputWindow = IntPtr.Zero;
        public RendererType RendererType = RendererType.Deferred;

        // 高级渲染特性
        public bool EnableMicroCluster = true;
        public bool EnableProbeField = true;
        public bool EnablePathTracing = false;
        public MicroClusterConfig MicroClusterConfig = new();
        public ProbeFieldConfig ProbeFieldConfig = new();
        public TSRConfig TSRConfig = new();
        public PathTracerConfig PathTracerConfig = new();
        public PostProcessingConfig PostProcessConfig = new();
        public ShadowConfig ShadowConfig = new();

        // 资源
        public string AssetRootPath = "Assets/";
        public string ScriptSourcePath = "Scripts/";

        // 物理
        public Vector3 Gravity = new(0, -9.81f, 0);

        // 粒子
        public int MaxParticles = 1_000_000;

        // 音频
        public AudioConfig AudioConfig = new();
        public float MasterVolume = 1.0f;

        // 世界
        public WorldStreamingConfig WorldStreamingConfig = new();
        public NavMeshConfig NavMeshConfig = new();

        // 网络
        public bool EnableNetworking = false;
        public NetworkConfig NetworkConfig = new();
    }

    public enum RendererType { Forward, Deferred }

    // ──────────────────────────────────────────────────────────
    // 渲染子系统配置类
    // ──────────────────────────────────────────────────────────

    public class MicroClusterConfig
    {
        public int ClusterSize = 128;
        public int MaxClusterGroups = 1_000_000;
        public int MaxVisibleClusters = 65536;
        public bool EnableSoftwareRasterization = true;
        public bool EnableHardwareRasterization = true;
        public float TargetTriangleDensity = 4.0f; // 每像素目标三角形数
    }

    public class ProbeFieldConfig
    {
        public bool EnableHardwareRayTracing = true;
        public bool EnableSurfaceCache = true;
        public bool EnableDDGI = true;
        public int SurfaceCacheResolution = 512;
        public int DDGIGridSize = 32;
        public int MaxBounces = 2;
        public float IndirectLightingScale = 1.0f;
    }

    public class TSRConfig
    {
        public TSRMode Mode = TSRMode.TSR;
        public float RenderScale = 0.66f; // 渲染分辨率缩放
        public bool EnableSharpening = true;
        public float SharpeningAmount = 0.3f;
    }

    public enum TSRMode { TSR, DLSS, FSR, XeSS, Native }

    public class PathTracerConfig
    {
        public int MaxBounces = 8;
        public int SamplesPerPixel = 1;
        public bool EnableDenoiser = true;
        public bool EnableReSTIR = true;
        public int AccumulationFrames = 256;
    }

    public class PostProcessingConfig
    {
        public bool EnableBloom = true;
        public bool EnableDoF = false;
        public bool EnableMotionBlur = true;
        public bool EnableChromaticAberration = false;
        public bool EnableVignette = true;
        public bool EnableFilmGrain = false;
        public bool EnableAutoExposure = true;
        public TonemapOperator TonemapOperator = TonemapOperator.ACES;
        public float ExposureCompensation = 0.0f;
    }

    public class ShadowConfig
    {
        public int CascadeCount = 4;
        public int ShadowMapResolution = 2048;
        public ShadowFilterMode FilterMode = ShadowFilterMode.PCF_5x5;
        public float ShadowDistance = 200.0f;
        public bool EnableVSM = false;
    }

    // ──────────────────────────────────────────────────────────
    // 其他配置
    // ──────────────────────────────────────────────────────────

    public class PhysicsConfig
    {
        public Vector3 Gravity = new(0, -9.81f, 0);
        public int MaxBodies = 65536;
        public int MaxContacts = 32768;
        public int BroadPhaseLayerCount = 12;
        public int ObjectLayerCount = 4096;
    }

    public class WorldStreamingConfig
    {
        public int GridCellSize = 256;
        public float NearLoadRadius = 500.0f;
        public float FarLoadRadius = 2000.0f;
        public long MemoryBudgetMB = 4096;
        public int MaxConcurrentLoads = 4;
    }

    public class NavMeshConfig
    {
        public float CellSize = 0.3f;
        public float CellHeight = 0.2f;
        public float AgentRadius = 0.5f;
        public float AgentHeight = 2.0f;
        public float AgentMaxSlope = 45.0f;
        public float AgentMaxClimb = 0.9f;
        public float RegionMinSize = 8.0f;
    }

    public class AudioConfig
    {
        public int SampleRate = 48000;
        public int Channels = 2;
        public int BufferSize = 1024;
        public bool EnableHRTF = true;
        public bool EnableReverb = true;
    }

    public class NetworkConfig
    {
        public int Port = 7777;
        public int MaxConnections = 64;
        public float TickRate = 60.0f;
        public bool IsServer = false;
        public string ServerAddress = "127.0.0.1";
    }

    // ──────────────────────────────────────────────────────────
    // 渲染上下文
    // ──────────────────────────────────────────────────────────

    public class RenderContext
    {
        public RHICommandList CommandList = null!;
        public RHIDevice Device = null!;
        public EntityComponentSystem ECS = null!;
        public RenderCamera? Camera;
        public RHIViewport Viewport;
        public float DeltaTime;
        public ulong FrameCount;
    }

    // ──────────────────────────────────────────────────────────
    // 渲染相机
    // ──────────────────────────────────────────────────────────

    public class RenderCamera
    {
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public float Fov = 60.0f;
        public float NearPlane = 0.1f;
        public float FarPlane = 5000.0f;
        public float AspectRatio = 16.0f / 9.0f;

        public Matrix4x4 ViewMatrix =>
            Matrix4x4.CreateLookAt(Position, Position + Vector3.Transform(Vector3.UnitZ, Rotation),
                Vector3.UnitY);

        public Matrix4x4 ProjectionMatrix =>
            Matrix4x4.CreatePerspectiveFieldOfView(
                float.DegreesToRadians(Fov), AspectRatio, NearPlane, FarPlane);

        public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

        public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);
        public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
        public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);
    }

    // ──────────────────────────────────────────────────────────
    // 渲染器接口
    // ──────────────────────────────────────────────────────────

    public interface IRenderer : IDisposable
    {
        void Initialize(RHIDevice device, RendererConfig config);
        void Render(RenderContext context);
        void OnResize(int width, int height);
    }

    public class RendererConfig
    {
        public int Width = 1920;
        public int Height = 1080;
        public MicroClusterVirtualGeometry? MicroCluster;
        public ProbeFieldGlobalIllumination? ProbeField;
        public TemporalSuperResolution? TSR;
        public PathTracer? PathTracer;
        public PostProcessingStack? PostProcess;
        public ShadowSystem? Shadows;
    }

    // ──────────────────────────────────────────────────────────
    // 引擎统计
    // ──────────────────────────────────────────────────────────

    public class EngineStats
    {
        public float FrameTime;
        public float FPS;
        public ulong FrameCount;

        private readonly Dictionary<string, PhaseStat> _phases = new();
        private readonly Dictionary<string, long> _phaseStartTimes = new();

        public void BeginPhase(string name)
        {
            _phaseStartTimes[name] = Stopwatch.GetTimestamp();
        }

        public void EndPhase(string name)
        {
            if (!_phaseStartTimes.TryGetValue(name, out var start)) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            double ms = elapsed * 1000.0 / Stopwatch.Frequency;

            if (!_phases.TryGetValue(name, out var stat))
            {
                stat = new PhaseStat();
                _phases[name] = stat;
            }
            stat.LastMs = ms;
            stat.TotalMs += ms;
            stat.Count++;
            stat.AvgMs = stat.TotalMs / stat.Count;
        }

        public IReadOnlyDictionary<string, PhaseStat> Phases => _phases;

        public void Update()
        {
            // 可以在这里采样/聚合统计
        }
    }

    public class PhaseStat
    {
        public double LastMs;
        public double AvgMs;
        public double TotalMs;
        public long Count;
    }
}
