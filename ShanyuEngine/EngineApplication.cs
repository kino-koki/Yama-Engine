// ==================================================================================================
//  EngineApplication.cs — 山雨引擎应用主循环（核心文件）
//
//  命名空间: ShanyuEngine
//  依赖:     Platform.Win32Window, Platform.PlatformClock, RHI.RHIDevice, RHI.Vulkan.VulkanDevice
//            Rendering.NullRenderDevice, ECS.EntityComponentSystem, Physics.PhysicsWorldReal,
//            Input.InputSystem, Audio.AdvancedAudioEngine
//
//  功能:
//    把所有子系统串起来的轻量级主循环，不依赖 EngineBootstrap（它依赖太多子系统）。
//    流程: 初始化 Win32Window → RHI Device → ECS → 简单渲染器 → 主循环
//    支持条件编译：Vulkan 成功则真实 GPU 渲染（清屏+三角形），失败则 fallback 到 NullRenderDevice
// ==================================================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ShanyuEngine.Platform;
using ShanyuEngine.RHI;
using ShanyuEngine.ECS;
using ShanyuEngine.Input;
using ShanyuEngine.Physics;
using ShanyuEngine.Rendering;
using ShanyuEngine.Assets;
using ShanyuEngine.Assets.Importers;
using ShanyuEngine.Modeling;
using ShanyuEngine.Animation;
using ShanyuEngine.Gameplay;
using ShanyuEngine.Scenes;
using ShanyuEngine.UI;
using ShanyuEngine.Particles;
using ShanyuEngine.AI;
using ShanyuEngine.Network;
using ShanyuEngine.Audio;

namespace ShanyuEngine
{
    /// <summary>
    /// 山雨引擎应用主循环。
    /// 轻量级入口，不依赖 EngineBootstrap 的重量级子系统链。
    /// 通过条件编译/fallback 机制，在 Vulkan 设备创建失败时退化为无GPU的控制台模式。
    /// </summary>
    public sealed class EngineApplication : IDisposable
    {
        // ──────────────────────────────────────────────────────────
        // 配置
        // ──────────────────────────────────────────────────────────

        public sealed class AppConfig
        {
            public string WindowTitle = "ShanyuEngine v1.0";
            public int WindowWidth = 1280;
            public int WindowHeight = 720;
            public bool VSync = true;
            public bool UseVulkan = true;       // 尝试创建真实 Vulkan 设备
            public bool EnableDebugLayer = true;
            public bool EnableFixedTimeStep = true;
            public float FixedTimeStep = 1f / 60f;
            public int MaxFixedStepsPerFrame = 4;
        }

        // ──────────────────────────────────────────────────────────
        // 平台层
        // ──────────────────────────────────────────────────────────

        private Win32Window? _window;
        private PlatformClock _clock;

        // ──────────────────────────────────────────────────────────
        // RHI / 渲染
        // ──────────────────────────────────────────────────────────

        private RHIDevice? _rhiDevice;
        private RHICommandQueue? _graphicsQueue;
        private RHISwapChain? _swapChain;
        private RHIFence? _frameFence;
        private RHICommandList? _commandList;
        private RHIDescriptorHeap? _rtvHeap;

        /// <summary>是否成功初始化了真实 GPU 设备</summary>
        public bool IsGpuAvailable { get; private set; }

        /// <summary>Null 渲染设备（无 GPU fallback 时使用）</summary>
        private NullRenderDevice? _nullDevice;

        // ── 场景渲染器 ──
        private SceneRenderer? _sceneRenderer;
        private SimpleSceneRenderer? _simpleRenderer;
        private SceneRendererReal? _sceneRendererReal;
        private TextureManager? _textureManager;
        private float _renderTime;

        // ── SceneRendererReal 二次渲染用的深度缓冲 ──
        private RHITexture? _depthTextureReal;
        private RHIDescriptorHandle _dsvHandleReal;

        // ── 资产数据库 ──
        private AssetDatabase? _assetDatabase;
        public AssetDatabase? AssetDatabase => _assetDatabase;

        // ──────────────────────────────────────────────────────────
        // 子系统
        // ──────────────────────────────────────────────────────────

        private EntityComponentSystem? _ecs;
        private PhysicsWorldReal? _physics;
        private InputSystem? _input;
        private AdvancedAudioEngine? _audio;
        private AnimationRuntime? _animationRuntime;

        // ── UE2级别 子系统集成 ──
        private GameplayWorld? _gameplayWorld;
        private SceneManager? _sceneManager;
        private UmgRoot? _umgRoot;
        private GPUParticleSystem? _gpuParticles;
        private PostProcessingStack? _postProcess;
        private NavigationSystem? _navigation;
        private NetworkManager? _network;
        private string? _sceneFilePath;
        private bool _gameplayWorldStarted;

        // ── 物理-渲染同步 ──
        private readonly List<(RenderItem Item, int BodyIndex)> _physicsRenderMappings = new();

        // ──────────────────────────────────────────────────────────
        // 帧管理
        // ──────────────────────────────────────────────────────────

        private ulong _frameCount;
        private int _frameIndex;
        private const int FramesInFlight = 2;
        private bool _isRunning;
        private bool _resizeRequested;

        // ──────────────────────────────────────────────────────────
        // FPS 控制台输出
        // ──────────────────────────────────────────────────────────

        private double _fpsConsoleTimer;
        private const double FpsConsoleInterval = 1.0;

        // ──────────────────────────────────────────────────────────
        // 相机状态 (FPS 风格：WASD 移动 + 鼠标右键拖拽视角)
        // ──────────────────────────────────────────────────────────

        private Vector3 _cameraPos = new Vector3(0, 5, -10);
        private float _cameraYaw = MathF.PI;     // Y 轴旋转（弧度），初始朝 +Z
        private float _cameraPitch = -0.45f;     // X 轴旋转（弧度），初始略微向下
        private const float CameraMoveSpeed = 8.0f;
        private const float CameraMouseSensitivity = 0.003f;
        private int _prevMouseX = -1;
        private int _prevMouseY = -1;

        // ──────────────────────────────────────────────────────────
        // 配置
        // ──────────────────────────────────────────────────────────

        public AppConfig Config { get; }

        // ──────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────

        public EngineApplication(AppConfig? config = null)
        {
            Config = config ?? new AppConfig();
            _clock = new PlatformClock(
                Config.FixedTimeStep,
                Config.EnableFixedTimeStep);
        }

        // ──────────────────────────────────────────────────────────
        // 主入口
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化所有子系统并进入主循环。
        /// </summary>
        /// <returns>退出码（0 = 正常退出）</returns>
        public int Run()
        {
            try
            {
                Initialize();
                MainLoop();
                Shutdown();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EngineApplication] FATAL: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 初始化
        // ──────────────────────────────────────────────────────────

        private void Initialize()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Console.WriteLine("================================================================");
            Console.WriteLine("  ShanyuEngine v1.0 — 引擎应用启动中...");
            Console.WriteLine("================================================================");

            // ── 1. 创建 Win32 窗口 ──
            Console.WriteLine("[1/5] 创建 Win32 窗口...");
            _window = new Win32Window(Config.WindowTitle, Config.WindowWidth, Config.WindowHeight);
            _window.VSync = Config.VSync;
            _window.OnResize += HandleResize;
            _window.OnClose += () => false; // 允许关闭
            _window.OnKeyDown += (vk) => HandleKeyDown(vk);
            _window.OnKeyUp += (vk) => HandleKeyUp(vk);
            _window.OnMouseMove += (x, y) => HandleMouseMove(x, y);
            _window.OnMouseDown += (btn) => HandleMouseDown(btn);
            _window.OnMouseUp += (btn) => HandleMouseUp(btn);
            _window.Create();
            Console.WriteLine($"  -> HWND=0x{_window.Handle.ToInt64():X}  Size={_window.Width}x{_window.Height}");

            // ── 2. 初始化 RHI 设备 ──
            Console.WriteLine("[2/5] 初始化 RHI 设备...");
            InitializeRHI();

            // ── 2b. 初始化场景渲染器（编译着色器 + 创建 PSO + 上传网格） ──
            if (IsGpuAvailable)
            {
                InitializeSceneRenderer();
            }

            // ── 2c. 初始化默认场景（AssetDatabase + 光源） ──
            InitializeDefaultScene();

            // ── 2d. 初始化动画运行时 ──
            _animationRuntime = new AnimationRuntime();

            // ── 3. 初始化 ECS ──
            Console.WriteLine("[3/5] 初始化 ECS...");
            _ecs = new EntityComponentSystem();
            Console.WriteLine("  -> EntityComponentSystem 已创建");

            // ── 4. 初始化输入系统 ──
            Console.WriteLine("[4/5] 初始化输入系统...");
            _input = new InputSystem();
            Console.WriteLine("  -> InputSystem 已创建");

            // ── 4b. 初始化脚本输入桥接 ──
            Assets.ScriptInput.SetInputSystem(_input);

            // ── 5. 初始化物理 ──
            Console.WriteLine("[5/5] 物理系统...");
            InitializePhysics();
            InitializePhysicsBodies();

            // 音频系统 — 可选，可能依赖 native 库
            try
            {
                _audio = new AdvancedAudioEngine();
                bool audioOk = _audio.Initialize(new AudioConfig { SampleRate = 48000, Channels = 2, BufferSize = 1024 });
                Console.WriteLine(audioOk
                    ? "  -> AdvancedAudioEngine 已初始化"
                    : "  -> AdvancedAudioEngine 初始化失败（回退到 Dummy）");
            }
            catch
            {
                Console.WriteLine("  -> AdvancedAudioEngine 创建失败（预期行为，跳过音频）");
            }

            // ── 6. UE2级别 子系统初始化 ──
            InitializeUE2Subsystems();

            // ── 启动时钟 ──
            _clock.Start();
            _isRunning = true;

            sw.Stop();
            Console.WriteLine("================================================================");
            Console.WriteLine($"  引擎初始化完成！耗时: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"  渲染后端: {(IsGpuAvailable ? _rhiDevice!.AdapterName : "Null (Console)")}");
            Console.WriteLine($"  窗口尺寸: {_window.Width}x{_window.Height}  VSync: {Config.VSync}");
            Console.WriteLine("================================================================");
        }

        // ──────────────────────────────────────────────────────────
        // UE2级别 子系统初始化
        // ──────────────────────────────────────────────────────────

        private void InitializeUE2Subsystems()
        {
            // 1. SceneManager — 场景管理器
            try
            {
                _sceneManager = new SceneManager(_ecs ?? new EntityComponentSystem());
                Console.WriteLine("  -> SceneManager 已初始化");

                // 处理命令行参数 --scene <path>
                var args = Environment.GetCommandLineArgs();
                int sceneIdx = Array.IndexOf(args, "--scene");
                if (sceneIdx >= 0 && sceneIdx + 1 < args.Length)
                {
                    _sceneFilePath = args[sceneIdx + 1];
                    if (File.Exists(_sceneFilePath))
                    {
                        Console.WriteLine($"  -> 加载场景: {_sceneFilePath}");
                        _sceneManager.LoadScene(_sceneFilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> SceneManager 初始化失败: {ex.Message}");
            }

            // 2. GameplayWorld — 游戏世界（Actor 生命周期管理）
            try
            {
                var gm = new GameMode
                {
                    Name = "ShanyuEngine_Default",
                    DefaultPawnFactory = () => new Pawn
                    {
                        Name = "DefaultPawn",
                        Position = new Vector3(0, 2, 0),
                        MoveSpeed = 5f
                    }
                };
                _gameplayWorld = new GameplayWorld(gm);

                // 注册默认场景物体为 Actor
                if (_assetDatabase != null)
                {
                    foreach (var obj in _assetDatabase.SceneObjects)
                    {
                        var actor = new Actor
                        {
                            Name = obj.Name,
                            Position = obj.Position,
                            Rotation = obj.Rotation,
                            Scale = obj.Scale
                        };
                        _gameplayWorld.RegisterActor(actor);
                    }
                }

                Console.WriteLine("  -> GameplayWorld 已初始化");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> GameplayWorld 初始化失败: {ex.Message}");
            }

            // 3. UmgRoot — UI 系统
            if (IsGpuAvailable && _rhiDevice != null)
            {
                try
                {
                    _umgRoot = new UmgRoot();
                    _umgRoot.Initialize(_rhiDevice, _window!.Width, _window.Height);
                    Console.WriteLine("  -> UmgRoot UI 系统已初始化");

                    // 添加简单的运行状态信息显示 (FPS + 游戏状态)
                    try
                    {
                        var fpsText = new TextWidget
                        {
                            Name = "FPSCounter",
                            Text = "ShanyuEngine v1.0",
                            Color = new Color(1, 1, 1, 0.8f),
                            Anchors = Anchors.TopLeft,
                            Offsets = new Margins { Left = 10, Top = 10, Right = 0, Bottom = 0 }
                        };
                        _umgRoot.AddToViewport(fpsText);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  -> UmgRoot 初始化失败: {ex.Message}");
                }
            }

            // 4. GPUParticleSystem — GPU 粒子系统
            if (IsGpuAvailable && _rhiDevice != null)
            {
                try
                {
                    _gpuParticles = new GPUParticleSystem(_rhiDevice);
                    _gpuParticles.Initialize(10000);
                    Console.WriteLine("  -> GPUParticleSystem 已初始化 (最大粒子数: 10000)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  -> GPUParticleSystem 初始化失败: {ex.Message}");
                }
            }

            // 5. PostProcessingStack — 后处理栈
            if (IsGpuAvailable && _rhiDevice != null)
            {
                try
                {
                    _postProcess = new PostProcessingStack(_rhiDevice);
                    _postProcess.Initialize(_window!.Width, _window.Height);
                    Console.WriteLine("  -> PostProcessingStack 已初始化");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  -> PostProcessingStack 初始化失败: {ex.Message}");
                }
            }

            // 6. NavigationSystem — AI 导航系统
            try
            {
                _navigation = new NavigationSystem();
                _navigation.Initialize(new Core.NavMeshConfig());
                Console.WriteLine("  -> NavigationSystem 已初始化");

                // 使用场景几何体构建 NavMesh（地面+立方体）
                if (_assetDatabase != null)
                {
                    BuildNavMeshFromScene();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> NavigationSystem 初始化失败: {ex.Message}");
            }

            // 7. NetworkManager — 网络系统
            try
            {
                _network = NetworkManager.Instance;
                var args = Environment.GetCommandLineArgs();

                // --server [port] — 启动服务器
                int serverIdx = Array.IndexOf(args, "--server");
                if (serverIdx >= 0)
                {
                    int port = 7777;
                    if (serverIdx + 1 < args.Length && int.TryParse(args[serverIdx + 1], out int p))
                        port = p;
                    if (_network.StartServer(port))
                        Console.WriteLine($"  -> NetworkManager 服务器已启动 (端口 {port})");
                }
                // --host [port] — 启动主机模式
                else if (args.Contains("--host"))
                {
                    int hostIdx = Array.IndexOf(args, "--host");
                    int port = 7777;
                    if (hostIdx + 1 < args.Length && int.TryParse(args[hostIdx + 1], out int p))
                        port = p;
                    if (_network.StartHost(port))
                        Console.WriteLine($"  -> NetworkManager 主机模式已启动 (端口 {port})");
                }
                // --client host:port — 连接客户端
                else
                {
                    int clientIdx = Array.IndexOf(args, "--client");
                    if (clientIdx >= 0 && clientIdx + 1 < args.Length)
                    {
                        var hostPort = args[clientIdx + 1];
                        var parts = hostPort.Split(':');
                        string host = parts[0];
                        int port = parts.Length > 1 && int.TryParse(parts[1], out int p2) ? p2 : 7777;
                        if (_network.ConnectToServer(host, port))
                            Console.WriteLine($"  -> NetworkManager 已连接到服务器 {host}:{port}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> NetworkManager 初始化失败: {ex.Message}");
            }

            Console.WriteLine("  -> UE2级别子系统初始化完成");
        }

        // ──────────────────────────────────────────────────────────
        // NavMesh 构建（使用场景几何体）
        // ──────────────────────────────────────────────────────────

        private void BuildNavMeshFromScene()
        {
            if (_navigation == null || _assetDatabase == null) return;

            try
            {
                // 从场景对象构建简单网格（地面 + 障碍物）
                var vertices = new List<Vector3>();
                var indices = new List<int>();
                int baseIndex = 0;

                foreach (var obj in _assetDatabase.SceneObjects)
                {
                    // 为每个对象创建一个简单的包围盒平面（简化 NavMesh 构建）
                    var bounds = obj.Bounds;
                    if (bounds.Size.X <= 0 || bounds.Size.Z <= 0) continue;

                    // 顶面四个顶点（Y = max）
                    var min = bounds.Min;
                    var max = bounds.Max;

                    vertices.Add(new Vector3(min.X, max.Y, min.Z));
                    vertices.Add(new Vector3(max.X, max.Y, min.Z));
                    vertices.Add(new Vector3(max.X, max.Y, max.Z));
                    vertices.Add(new Vector3(min.X, max.Y, max.Z));

                    // 两个三角形
                    indices.Add(baseIndex + 0);
                    indices.Add(baseIndex + 1);
                    indices.Add(baseIndex + 2);
                    indices.Add(baseIndex + 0);
                    indices.Add(baseIndex + 2);
                    indices.Add(baseIndex + 3);

                    baseIndex += 4;
                }

                if (vertices.Count > 0)
                {
                    var verts = vertices.ToArray();
                    var inds = indices.ToArray();

                    // 计算场景包围盒
                    Vector3 min = verts[0];
                    Vector3 max = verts[0];
                    foreach (var v in verts)
                    {
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                    }
                    var origin = min;
                    var worldSize = max - min;

                    _navigation.BuildNavMesh(verts, inds, origin, worldSize);
                    Console.WriteLine($"  -> NavMesh 构建完成: {verts.Length} 顶点, {inds.Length / 3} 三角形");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> NavMesh 构建失败: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────
        // 物理世界初始化
        // ──────────────────────────────────────────────────────────

        private void InitializePhysics()
        {
            _physics = new PhysicsWorldReal(PhysicsWorldConfig.Default);
            Console.WriteLine("  -> PhysicsWorldReal (pure C#) 已初始化");
        }

        // ──────────────────────────────────────────────────────────
        // 物理初始化：为场景渲染项创建刚体
        // ──────────────────────────────────────────────────────────

        private void InitializePhysicsBodies()
        {
            if (_physics == null) return;

            // 地面（静态）
            var groundBody = new RigidBody(new BoxShape(new PhysicsVector3(15f, 0.1f, 15f)), 0f)
            {
                BodyType = BodyType.Static,
                Position = new PhysicsVector3(0f, -0.1f, 0f)
            };
            _physics.AddBody(groundBody);

            // 收集渲染项
            var renderItems = CollectRenderItems();
            int entityId = 1;
            foreach (var item in renderItems)
            {
                if (item.Name.Contains("Ground", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Contains("Plane", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Matrix4x4.Decompose(item.WorldMatrix, out Vector3 scale, out _, out Vector3 position))
                {
                    position = new Vector3(item.WorldMatrix.M41, item.WorldMatrix.M42, item.WorldMatrix.M43);
                    scale = Vector3.One;
                }

                // 立方体：GenerateCube(2.0f) => 本地 halfExtents = (1,1,1)
                var shape = new BoxShape(new PhysicsVector3(1.0f * scale.X, 1.0f * scale.Y, 1.0f * scale.Z));
                var body = new RigidBody(shape, 1.0f)
                {
                    BodyType = BodyType.Dynamic,
                    Position = position,
                    Restitution = 0.2f,
                    Friction = 0.5f
                };
                int index = _physics.AddBody(body);
                _physicsRenderMappings.Add((item, index));
                entityId++;
            }

            Console.WriteLine($"  -> 物理刚体已创建: {_physics.BodyCount} 个 (地面 + {_physicsRenderMappings.Count} 个动态物体)");
        }

        private List<RenderItem> CollectRenderItems()
        {
            var renderItems = new List<RenderItem>();
            if (_sceneRenderer != null)
                renderItems.AddRange(_sceneRenderer.GetRenderItems());
            else if (_simpleRenderer != null)
                renderItems.AddRange(_simpleRenderer.GetRenderItems());
            return renderItems;
        }

        /// <summary>将物理世界的变换同步回渲染项。</summary>
        private void SyncPhysicsToRenderers()
        {
            if (_physics == null) return;
            foreach (var mapping in _physicsRenderMappings)
            {
                var body = _physics.GetBody(mapping.BodyIndex);
                if (body == null) continue;

                if (!Matrix4x4.Decompose(mapping.Item.WorldMatrix, out Vector3 scale, out _, out _))
                    scale = Vector3.One;

                mapping.Item.WorldMatrix =
                    Matrix4x4.CreateScale(scale) *
                    Matrix4x4.CreateFromQuaternion((Quaternion)body.Rotation) *
                    Matrix4x4.CreateTranslation((Vector3)body.Position);
            }
        }

        // ──────────────────────────────────────────────────────────
        // RHI 初始化（核心：尝试 Vulkan → fallback Null）
        // ──────────────────────────────────────────────────────────

        private void InitializeRHI()
        {
            if (!Config.UseVulkan)
            {
                Console.WriteLine("  -> Vulkan 被禁用，使用 NullRenderDevice");
                IsGpuAvailable = false;
                _nullDevice = new NullRenderDevice("Shanyu Null Fallback");
                return;
            }

            try
            {
                // 通过 RHIDeviceFactory 创建 Vulkan 1.4 设备
                _rhiDevice = RHIDeviceFactory.Create(
                    RHIBackendType.Vulkan,
                    Config.EnableDebugLayer,
                    enableGpuBasedValidation: false);

                Console.WriteLine($"  -> 适配器: {_rhiDevice.AdapterName}");
                Console.WriteLine($"  -> 显存: {_rhiDevice.DedicatedVideoMemory / (1024 * 1024 * 1024):F1} GB");
                Console.WriteLine($"  -> Mesh Shader: {_rhiDevice.SupportsMeshShaders}");
                Console.WriteLine($"  -> Ray Tracing: {_rhiDevice.SupportsRayTracing}");

                // 创建命令队列
                _graphicsQueue = _rhiDevice.GetCommandQueue(RHIQueueType.Graphics);

                // 创建交换链
                _swapChain = _rhiDevice.CreateSwapChain(
                    _window!.Handle,
                    _window.Width,
                    _window.Height,
                    RHIFormat.R8G8B8A8_UNorm,
                    FramesInFlight + 1,
                    Config.VSync,
                    "MainSwapChain");

                Console.WriteLine($"  -> SwapChain: {_swapChain.Width}x{_swapChain.Height}  Buffers={_swapChain.BufferCount}");

                // 创建帧同步
                _frameFence = _rhiDevice.CreateFence(0, "FrameFence");

                // 创建命令列表
                _commandList = _rhiDevice.CreateCommandList(RHIQueueType.Graphics, "MainCL");

                // 创建 RTV 堆（至少 4 个描述符用于交换链后缓冲）
                _rtvHeap = _rhiDevice.CreateDescriptorHeap(RHIDescriptorHeapType.RTV, 16, shaderVisible: false);

                IsGpuAvailable = true;
                Console.WriteLine("  -> Vulkan 设备初始化成功！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> Vulkan 初始化失败: {ex.Message}");
                Console.WriteLine("     回退到 NullRenderDevice（无 GPU 模式）");

                IsGpuAvailable = false;
                _nullDevice = new NullRenderDevice("Shanyu Null Fallback");

                // 释放可能已部分创建的资源
                _commandList?.Dispose();
                _frameFence?.Dispose();
                _swapChain?.Dispose();
                _graphicsQueue?.Dispose();
                _rhiDevice?.Dispose();
                _commandList = null;
                _frameFence = null;
                _swapChain = null;
                _graphicsQueue = null;
                _rhiDevice = null;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 场景渲染器初始化
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化场景渲染器：编译着色器、创建 PSO、上传网格、添加渲染项。
        /// 创建场景内容：3 个彩色立方体 + 1 个地面平面。
        /// </summary>
        private void InitializeSceneRenderer()
        {
            if (_rhiDevice == null || _swapChain == null || _graphicsQueue == null
                || _frameFence == null || _commandList == null)
                return;

            try
            {
                Console.WriteLine("[SceneRenderer] 正在初始化场景渲染器...");

                // 创建场景渲染器
                _sceneRenderer = new SceneRenderer(
                    _rhiDevice,
                    _swapChain,
                    _graphicsQueue,
                    _frameFence,
                    _commandList);

                // 初始化（编译着色器 + 创建 PSO + 创建深度缓冲）
                _sceneRenderer.Initialize(debugShaders: Config.EnableDebugLayer);

                // 创建真实场景渲染器（用于导入模型上传）
                _sceneRendererReal = new SceneRendererReal(_rhiDevice);

                // 创建纹理管理器并设置给真实渲染器，同时创建默认白色纹理
                _textureManager = new TextureManager(_rhiDevice);
                _sceneRendererReal.SetTextureManager(_textureManager);
                _sceneRendererReal.EnsureDefaultTexture();

                // 设置光照参数
                _sceneRenderer.LightDirection = new Vector3(-0.5f, -1.0f, -0.5f);
                _sceneRenderer.LightColor = new Vector3(0.9f, 0.85f, 0.7f);
                _sceneRenderer.AmbientColor = new Vector3(0.2f, 0.2f, 0.25f);
                _sceneRenderer.ClearColor = new Vector4(0.08f, 0.08f, 0.12f, 1.0f);

                // ── 创建网格并添加渲染项 ──
                Console.WriteLine("[SceneRenderer] 创建网格资源...");

                // 立方体网格（共享一份几何体，3 个实例用不同颜色和位置）
                var cubeMesh = EngineBuiltinGeometry.GenerateCube(2.0f);
                var cubeGpuMesh = MeshRenderResource.FromProceduralMesh(
                    _rhiDevice, cubeMesh, Vector4.One, "Cube");

                // 3 个立方体在不同位置，不同颜色
                _sceneRenderer.AddRenderItem(new RenderItem(
                    cubeGpuMesh,
                    new Vector3(0, 1, 0),       // 位置
                    Vector3.One,                 // 缩放
                    new Vector4(0.9f, 0.2f, 0.2f, 1.0f),  // 红色
                    "Cube_Red"));

                _sceneRenderer.AddRenderItem(new RenderItem(
                    cubeGpuMesh,
                    new Vector3(-4, 1, 0),
                    Vector3.One,
                    new Vector4(0.2f, 0.9f, 0.2f, 1.0f),  // 绿色
                    "Cube_Green"));

                _sceneRenderer.AddRenderItem(new RenderItem(
                    cubeGpuMesh,
                    new Vector3(4, 1, 0),
                    Vector3.One,
                    new Vector4(0.2f, 0.3f, 0.9f, 1.0f),  // 蓝色
                    "Cube_Blue"));

                // 地面平面
                var planeMesh = EngineBuiltinGeometry.GeneratePlane(30.0f, 30.0f, 1, 1);
                var planeGpuMesh = MeshRenderResource.FromProceduralMesh(
                    _rhiDevice, planeMesh, new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Plane");
                _sceneRenderer.AddRenderItem(new RenderItem(
                    planeGpuMesh,
                    Vector3.Zero,
                    Vector3.One,
                    new Vector4(0.4f, 0.4f, 0.4f, 1.0f),  // 灰色
                    "Ground"));

                Console.WriteLine($"[SceneRenderer] 场景初始化完成，渲染项数量: {_sceneRenderer.RenderItemCount}");
                Console.WriteLine("  -> 3 个彩色立方体 (红/绿/蓝) + 1 个地面平面");
                Console.WriteLine("  -> WASD 移动相机，鼠标右键拖拽旋转视角");

                // ── 同时初始化 SimpleSceneRenderer（使用 ShaderCompiler + PipelineStateObject） ──
                InitializeSimpleSceneRenderer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SceneRenderer] 初始化失败: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                _sceneRenderer?.Dispose();
                _sceneRenderer = null;
                _simpleRenderer?.Dispose();
                _simpleRenderer = null;
            }
        }

        // ──────────────────────────────────────────────────────────
        // SimpleSceneRenderer 初始化（ShaderCompiler + PipelineStateObject）
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化 SimpleSceneRenderer：
        /// 使用 ShaderCompiler (Vortice.D3DCompiler) 编译着色器，
        /// 使用 PipelineStateObject (RootSignature + GraphicsPipelineState)，
        /// 上传 3 个彩色立方体 + 1 个地面平面到 GPU。
        /// 如果 SceneRenderer 初始化成功，SimpleSceneRenderer 作为备用渲染器存在。
        /// 如果 SceneRenderer 失败，SimpleSceneRenderer 将接管渲染。
        /// </summary>
        private void InitializeSimpleSceneRenderer()
        {
            if (_rhiDevice == null || _swapChain == null || _graphicsQueue == null
                || _frameFence == null || _commandList == null)
                return;

            try
            {
                Console.WriteLine("[SimpleSceneRenderer] 正在初始化...");

                _simpleRenderer = new SimpleSceneRenderer(
                    _rhiDevice, _swapChain, _graphicsQueue,
                    _frameFence, _commandList);

                // Init: 编译着色器 → 创建 PSO → 创建深度模板
                _simpleRenderer.Init(debugShaders: Config.EnableDebugLayer);

                // 创建真实场景渲染器（用于导入模型上传）
                _sceneRendererReal = new SceneRendererReal(_rhiDevice);

                // 设置光照参数
                _simpleRenderer.LightDirection = new Vector3(-0.5f, -1.0f, -0.5f);
                _simpleRenderer.LightColor = new Vector3(0.9f, 0.85f, 0.7f);
                _simpleRenderer.AmbientColor = new Vector3(0.2f, 0.2f, 0.25f);
                _simpleRenderer.ClearColor = new Vector4(0.08f, 0.08f, 0.12f, 1.0f);

                // ── 创建网格并添加渲染项 ──
                // 立方体：使用 EngineBuiltinGeometry.GenerateCube()
                var cubeMesh = EngineBuiltinGeometry.GenerateCube(2.0f);

                // 3 个立方体在不同位置，不同颜色
                _simpleRenderer.AddMesh(cubeMesh,
                    new Vector3(0, 1, 0),
                    Vector3.One,
                    new Vector4(0.9f, 0.2f, 0.2f, 1.0f),  // 红色
                    "SR_Cube_Red");

                _simpleRenderer.AddMesh(cubeMesh,
                    new Vector3(-4, 1, 0),
                    Vector3.One,
                    new Vector4(0.2f, 0.9f, 0.2f, 1.0f),  // 绿色
                    "SR_Cube_Green");

                _simpleRenderer.AddMesh(cubeMesh,
                    new Vector3(4, 1, 0),
                    Vector3.One,
                    new Vector4(0.2f, 0.3f, 0.9f, 1.0f),  // 蓝色
                    "SR_Cube_Blue");

                // 地面平面：使用 EngineBuiltinGeometry.GeneratePlane()
                var planeMesh = EngineBuiltinGeometry.GeneratePlane(30.0f, 30.0f, 1, 1);
                _simpleRenderer.AddMesh(planeMesh,
                    Vector3.Zero,
                    Vector3.One,
                    new Vector4(0.4f, 0.4f, 0.4f, 1.0f),  // 灰色
                    "SR_Ground");

                Console.WriteLine($"[SimpleSceneRenderer] 初始化完成，渲染项数量: {_simpleRenderer.RenderItemCount}");
                Console.WriteLine("  -> 3 个彩色立方体 (红/绿/蓝) + 1 个地面平面");
                Console.WriteLine("  -> ShaderCompiler + PipelineStateObject + MeshGpuResource");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimpleSceneRenderer] 初始化失败: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                _simpleRenderer?.Dispose();
                _simpleRenderer = null;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 脚本更新（简化版）
        // ──────────────────────────────────────────────────────────

        private void TickScripts(float deltaTime)
        {
            if (_assetDatabase == null) return;
            foreach (var obj in _assetDatabase.SceneObjects)
            {
                var script = obj.GetComponent<Assets.ScriptComponent>();
                if (script != null)
                {
                    script.OnUpdate(deltaTime);
                }
            }
        }

        // ──────────────────────────────────────────────────────────
        // 动画更新（对象级关键帧动画 + 骨骼动画）
        // ──────────────────────────────────────────────────────────

        private void TickAnimations(float deltaTime)
        {
            if (_assetDatabase == null) return;

            // 1. 对象级关键帧动画
            foreach (var obj in _assetDatabase.SceneObjects)
            {
                var animator = obj.GetComponent<Assets.ObjectAnimatorComponent>();
                if (animator != null)
                {
                    animator.Update(deltaTime, obj);
                }
            }

            // 2. 骨骼动画：自动注册带 SkinnedMeshAnimatorComponent 的对象到 AnimationRuntime
            if (_animationRuntime != null)
            {
                foreach (var obj in _assetDatabase.SceneObjects)
                {
                    var skinned = obj.GetComponent<SkinnedMeshAnimatorComponent>();
                    if (skinned == null) continue;
                    if (skinned.Skeleton == null || skinned.AnimationClip == null) continue;

                    if (!skinned.IsRegistered)
                    {
                        _animationRuntime.PlayAnimation(obj, skinned.Skeleton, skinned.AnimationClip, skinned.CurrentStateName);
                        skinned.IsRegistered = true;
                    }
                }

                // 更新所有活跃动画
                _animationRuntime.Update(deltaTime);

                // 将计算后的骨骼矩阵写回组件，供渲染器读取
                foreach (var obj in _assetDatabase.SceneObjects)
                {
                    var skinned = obj.GetComponent<SkinnedMeshAnimatorComponent>();
                    if (skinned == null || !skinned.IsRegistered) continue;

                    var instance = _animationRuntime.GetInstance(obj);
                    if (instance != null)
                    {
                        skinned.CurrentBoneMatrices = instance.BoneMatrices;
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────
        // 光照更新
        // ──────────────────────────────────────────────────────────

        private void TickLights(float deltaTime)
        {
            if (_assetDatabase == null) return;
            foreach (var obj in _assetDatabase.SceneObjects)
            {
                var light = obj.GetComponent<Assets.LightComponent>();
                if (light != null)
                {
                    light.Update(deltaTime, obj);
                }
            }
        }

        // ──────────────────────────────────────────────────────────
        // UE2级别 子系统 Tick（GameMode / SceneManager / Particles / UI）
        // ──────────────────────────────────────────────────────────

        private void TickUE2Subsystems(float dt)
        {
            // 1. GameplayWorld Tick（Actor 每帧更新）
            if (_gameplayWorld != null)
            {
                try
                {
                    if (!_gameplayWorldStarted)
                    {
                        _gameplayWorld.Start();
                        _gameplayWorldStarted = true;
                    }

                    // 连接输入到 GameplayWorld
                    if (_input != null)
                    {
                        // WASD → 移动输入（InputKey 枚举）
                        if (_input.IsKeyDown(InputKey.W))
                            _gameplayWorld.DispatchInput(new PlayerInputCommand(PlayerInputKind.MoveForward, 1f));
                        if (_input.IsKeyDown(InputKey.S))
                            _gameplayWorld.DispatchInput(new PlayerInputCommand(PlayerInputKind.MoveForward, -1f));
                        if (_input.IsKeyDown(InputKey.A))
                            _gameplayWorld.DispatchInput(new PlayerInputCommand(PlayerInputKind.MoveRight, -1f));
                        if (_input.IsKeyDown(InputKey.D))
                            _gameplayWorld.DispatchInput(new PlayerInputCommand(PlayerInputKind.MoveRight, 1f));
                    }

                    _gameplayWorld.Tick(dt);

                    // 检查游戏状态
                    if (_gameplayWorld.GameMode.MatchHasEnded)
                    {
                        // 比赛结束，可重新开始
                    }
                }
                catch { /* GameplayWorld 错误不中断主循环 */ }
            }

            // 2. SceneManager 场景管理
            if (_sceneManager != null)
            {
                try
                {
                    _sceneManager.IsDirty = false; // 运行时默认不标记脏
                }
                catch { }
            }

            // 3. GPU 粒子系统
            if (_gpuParticles != null)
            {
                try
                {
                    _gpuParticles.Update(dt, _cameraPos, Matrix4x4.Identity, Matrix4x4.Identity);
                }
                catch { }
            }

            // 4. UmgRoot UI 系统
            if (_umgRoot != null)
            {
                try
                {
                    _umgRoot.Update(dt);
                }
                catch { }
            }

            // 5. PostProcess 后处理
            if (_postProcess != null)
            {
                try
                {
                    // 后处理更新由渲染阶段处理
                }
                catch { }
            }

            // 6. NavigationSystem AI 导航更新
            if (_navigation != null)
            {
                try
                {
                    _navigation.Crowd?.Update(dt);
                }
                catch { }
            }

            // 7. NetworkManager 网络更新
            if (_network != null)
            {
                try
                {
                    _network.Update(dt);
                }
                catch { }
            }
        }

        // ──────────────────────────────────────────────────────────
        // 场景初始化
        // ──────────────────────────────────────────────────────────

        private void InitializeDefaultScene()
        {
            _assetDatabase = new AssetDatabase(Path.Combine(Path.GetTempPath(), "ShanyuEngine_Default"));

            // 3 个立方体
            var cube1 = _assetDatabase.CreateObject("Cube_Red", "Box", new Vector3(0, 1, 0));
            cube1.BaseColor = new Vector3(0.9f, 0.2f, 0.2f);
            cube1.Scale = new Vector3(2, 2, 2);

            var cube2 = _assetDatabase.CreateObject("Cube_Green", "Box", new Vector3(-4, 1, 0));
            cube2.BaseColor = new Vector3(0.2f, 0.9f, 0.2f);
            cube2.Scale = new Vector3(2, 2, 2);

            var cube3 = _assetDatabase.CreateObject("Cube_Blue", "Box", new Vector3(4, 1, 0));
            cube3.BaseColor = new Vector3(0.2f, 0.3f, 0.9f);
            cube3.Scale = new Vector3(2, 2, 2);

            // 地面
            var ground = _assetDatabase.CreateObject("Ground", "Plane", Vector3.Zero);
            ground.BaseColor = new Vector3(0.4f, 0.4f, 0.4f);
            ground.Scale = new Vector3(3, 1, 3);

            // 方向光源
            var lightObj = new SceneObject
            {
                Name = "DirectionalLight",
                Position = new Vector3(5, 10, 5),
                MeshAssetPath = "Light_Directional"
            };
            var lightComp = lightObj.AddComponent<LightComponent>();
            lightComp.LightType = "Directional";
            lightComp.Color = Vector3.One;
            lightComp.Intensity = 1.2f;

            // 计算旋转，使方向光的局部 +Z 指向硬编码光照方向 (-0.5, -1.0, -0.5)
            Vector3 targetDir = Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.5f));
            Vector3 defaultForward = Vector3.UnitZ;
            Vector3 rotationAxis = Vector3.Cross(defaultForward, targetDir);
            float dot = Vector3.Dot(defaultForward, targetDir);
            if (rotationAxis.LengthSquared() > 1e-6f)
            {
                rotationAxis = Vector3.Normalize(rotationAxis);
                float angle = MathF.Acos(Math.Clamp(dot, -1.0f, 1.0f));
                lightObj.Rotation = Quaternion.CreateFromAxisAngle(rotationAxis, angle);
            }
            else if (dot < 0)
            {
                lightObj.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
            }

            _assetDatabase.AddObject(lightObj);
        }

        /// <summary>
        /// 加载并渲染导入的模型（fbx / gltf / glb）。
        /// </summary>
        public void LoadAndRenderImportedModel(string assetPath)
        {
            if (!File.Exists(assetPath) || _assetDatabase == null)
                return;

            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            InterchangeScene? scene = null;

            switch (ext)
            {
                case ".fbx":
                    scene = FbxImporterFactory.Import(assetPath);
                    break;
                case ".gltf":
                case ".glb":
                    scene = new GlTFImporter(assetPath).Import();
                    break;
            }

            if (scene == null || scene.Meshes.Count == 0)
                return;

            var editableMeshes = ModelToMeshConverter.Convert(scene);

            foreach (var editableMesh in editableMeshes)
            {
                string meshPath = $"{assetPath}#{editableMesh.Name}";

                // 上传到真实渲染器（如果已初始化）
                _sceneRendererReal?.UploadMesh(meshPath, editableMesh);

                // 同时上传到简化渲染器，确保主渲染管线可见
                if (_simpleRenderer != null)
                {
                    var gpuMesh = _simpleRenderer.UploadMesh(meshPath, editableMesh);
                    _simpleRenderer.AddMesh(gpuMesh, Vector3.Zero, Vector3.One, Vector4.One, editableMesh.Name);
                }

                // 创建场景对象
                var obj = new SceneObject
                {
                    Name = editableMesh.Name,
                    MeshAssetPath = meshPath,
                    Position = Vector3.Zero
                };
                _assetDatabase.AddObject(obj);
            }
        }

        // ──────────────────────────────────────────────────────────
        // 主循环
        // ──────────────────────────────────────────────────────────

        private void MainLoop()
        {
            while (_isRunning)
            {
                // a. 泵出 Win32 消息
                if (_window != null && !_window.PumpMessages())
                {
                    _isRunning = false;
                    break;
                }

                // b. 时钟 Tick
                float dt = _clock.Tick();

                // c. 处理窗口 resize
                if (_resizeRequested && _swapChain != null)
                {
                    HandleSwapChainResize();
                    _resizeRequested = false;
                }

                // d. 输入更新
                _input?.Update(dt);

                // e. ECS 更新
                _ecs?.Update(dt);

                // f. 物理更新（固定步长）
                if (_clock.UseFixedTimeStep)
                {
                    int fixedSteps = _clock.ConsumeFixedSteps();
                    if (fixedSteps > 0)
                    {
                        if (_physics != null)
                        {
                            for (int i = 0; i < fixedSteps; i++)
                                _physics.Step(_clock.FixedTimeStep);
                        }
                        SyncPhysicsToRenderers();
                    }
                }

                // g. 脚本更新（简化版：遍历 SceneObject 上的 ScriptComponent）
                TickScripts(dt);

                // h. 动画更新（简化版：对象级关键帧动画）
                TickAnimations(dt);

                // i. 光照更新
                TickLights(dt);

                // UE2级别 Tick: GameMode, Scene, Particles, UI
                TickUE2Subsystems(dt);

                // j. 渲染
                RenderFrame(dt);

                // h. 音频更新
                try { _audio?.Update(dt); } catch { /* 音频失败不中断渲染 */ }

                // i. FPS 控制台输出
                UpdateFpsConsole(dt);

                // j. 帧计数
                _frameCount++;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 渲染
        // ──────────────────────────────────────────────────────────

        private void RenderFrame(float dt)
        {
            if (IsGpuAvailable && _rhiDevice != null && _swapChain != null
                && _commandList != null && _graphicsQueue != null && _frameFence != null)
            {
                RenderFrameVulkan(dt);
            }
            else
            {
                // Null 渲染器：仅输出帧率
                RenderFrameNull(dt);
            }
        }

        /// <summary>
        /// Vulkan 真实渲染：更新相机 → 渲染场景 → Present。
        /// UE2级别：优先使用 SceneRendererReal（PBR + 阴影 + 骨骼动画）作为主渲染器。
        /// 回退到 SceneRenderer / SimpleSceneRenderer / 清屏模式。
        /// </summary>
        private void RenderFrameVulkan(float dt)
        {
            // ── 更新相机 ──
            UpdateCamera(dt);

            // ── 累计渲染时间 ──
            _renderTime += dt;

            var view = ComputeViewMatrix();
            var projection = ComputeProjectionMatrix();

            // ── 优先使用 SceneRendererReal 作为主渲染器（UE2级别）──
            if (_sceneRendererReal != null && _assetDatabase != null
                && _commandList != null && _swapChain != null
                && _graphicsQueue != null && _frameFence != null)
            {
                RenderFrameSceneRendererReal(dt, view, projection);
            }
            // ── 回退：SceneRenderer（轻量级前向渲染）──
            else if (_sceneRenderer != null && _sceneRenderer.IsInitialized)
            {
                _sceneRenderer.RenderFrame(view, projection, _renderTime, _window!.VSync);
            }
            // ── 回退：SimpleSceneRenderer ──
            else if (_simpleRenderer != null && _simpleRenderer.IsInitialized)
            {
                _simpleRenderer.Render(view, projection, _renderTime, _window!.VSync);
            }
            // ── 最终回退：仅清屏 ──
            else
            {
                RenderFrameClearOnly();
            }
        }

        /// <summary>
        /// 更新相机状态：WASD 移动 + 鼠标右键拖拽视角。
        /// </summary>
        private void UpdateCamera(float dt)
        {
            if (_input == null) return;

            float moveSpeed = CameraMoveSpeed * dt;

            // WASD 移动（相对于相机朝向）
            Vector3 forward = GetCameraForward();
            Vector3 right = GetCameraRight();

            if (_input.IsKeyDown(InputKey.W))
                _cameraPos += forward * moveSpeed;
            if (_input.IsKeyDown(InputKey.S))
                _cameraPos -= forward * moveSpeed;
            if (_input.IsKeyDown(InputKey.A))
                _cameraPos -= right * moveSpeed;
            if (_input.IsKeyDown(InputKey.D))
                _cameraPos += right * moveSpeed;
            if (_input.IsKeyDown(InputKey.Space))
                _cameraPos += Vector3.UnitY * moveSpeed;
            if (_input.IsKeyDown(InputKey.Control))
                _cameraPos -= Vector3.UnitY * moveSpeed;

            // 鼠标右键拖拽旋转视角
            if (_input.IsKeyDown(InputKey.MouseRight) && _input.MouseDelta.LengthSquared() > 0)
            {
                _cameraYaw += _input.MouseDelta.X * CameraMouseSensitivity;
                _cameraPitch -= _input.MouseDelta.Y * CameraMouseSensitivity;
                // 限制俯仰角
                _cameraPitch = Math.Clamp(_cameraPitch, -MathF.PI / 2 + 0.1f, MathF.PI / 2 - 0.1f);
            }
        }

        /// <summary>计算相机前方向（XZ 平面投影，用于移动）</summary>
        private Vector3 GetCameraForward()
        {
            return Vector3.Normalize(new Vector3(
                MathF.Sin(_cameraYaw) * MathF.Cos(_cameraPitch),
                -MathF.Sin(_cameraPitch),
                MathF.Cos(_cameraYaw) * MathF.Cos(_cameraPitch)));
        }

        /// <summary>计算相机右方向</summary>
        private Vector3 GetCameraRight()
        {
            return Vector3.Normalize(new Vector3(
                MathF.Cos(_cameraYaw),
                0,
                -MathF.Sin(_cameraYaw)));
        }

        /// <summary>计算视图矩阵（右手坐标系，System.Numerics 约定）</summary>
        private Matrix4x4 ComputeViewMatrix()
        {
            Vector3 target = _cameraPos + GetCameraForward();
            Vector3 up = Vector3.UnitY;
            return Matrix4x4.CreateLookAt(_cameraPos, target, up);
        }

        /// <summary>计算投影矩阵（透视，60° FOV，近 0.1，远 1000）</summary>
        private Matrix4x4 ComputeProjectionMatrix()
        {
            float aspectRatio = (float)_window!.Width / Math.Max(1, _window.Height);
            return Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3.0f,  // 60 度 FOV
                aspectRatio,
                0.1f,             // 近平面
                1000.0f);         // 远平面
        }

        /// <summary>
        /// UE2级别主渲染：使用 SceneRendererReal 渲染完整场景（PBR + 阴影 + 骨骼动画）。
        /// 包含 GPU 粒子和 UI 覆盖层的渲染，然后执行 Present。
        /// </summary>
        private void RenderFrameSceneRendererReal(float dt, Matrix4x4 view, Matrix4x4 projection)
        {
            try
            {
                // 从 AssetDatabase 构建完整场景
                var scene = RenderSceneBuilder.FromAssetDatabase(_assetDatabase!, "Main");

                // 构建相机数据
                var camera = new CameraData
                {
                    Position = _cameraPos,
                    Forward = GetCameraForward(),
                    Up = Vector3.UnitY,
                    FieldOfView = 60f,
                    NearClip = 0.1f,
                    FarClip = 1000f,
                    AspectRatio = (float)_window!.Width / Math.Max(1, _window.Height),
                    ViewMatrix = view,
                    ProjectionMatrix = projection,
                    ViewProjectionMatrix = view * projection
                };

                // 确保深度缓冲存在
                EnsureDepthBufferForReal();

                // 开始录制命令
                _commandList!.Reset();

                // 获取后缓冲
                int bbIndex = _swapChain!.CurrentBackBufferIndex;
                var backBuffer = _swapChain.GetBackBuffer(bbIndex);
                var rtvHandle = _swapChain.GetBackBufferRTV(bbIndex);

                // 设置渲染目标
                _commandList.TransitionTo(backBuffer, RHIResourceState.RenderTarget);
                _commandList.OMSetRenderTargets(new[] { rtvHandle }, _dsvHandleReal);
                _commandList.RSSetViewport(0, 0, _swapChain.Width, _swapChain.Height, 0, 1);
                _commandList.RSSetScissorRect(0, 0, _swapChain.Width, _swapChain.Height);

                // ── 1. 主场景渲染（PBR + 阴影）──
                _sceneRendererReal!.RenderScene(_commandList, scene, camera, dt);

                // ── 2. GPU 粒子渲染 ──
                if (_gpuParticles != null)
                {
                    _gpuParticles.Render(_commandList, rtvHandle, null,
                        new RHIViewport(0, 0, _swapChain.Width, _swapChain.Height));
                }

                // ── 3. UI 覆盖层渲染 ──
                if (_umgRoot != null)
                {
                    try { _umgRoot.Render(new RHIViewport(0, 0, _swapChain.Width, _swapChain.Height)); }
                    catch { }
                }

                // 过渡到 Present
                _commandList.TransitionTo(backBuffer, RHIResourceState.Present);

                // 关闭并执行
                _commandList.Close();
                _graphicsQueue!.ExecuteCommandLists(_commandList);
                _graphicsQueue.Signal(_frameFence!, _frameCount);
                _swapChain.Present(_window!.VSync);
            }
            catch (Exception ex)
            {
                if (_frameCount < 2)
                    Console.WriteLine($"[EngineApplication] SceneRendererReal 主渲染失败: {ex.Message}");
            }
        }

        /// <summary>确保 SceneRendererReal 二次渲染用的深度缓冲已创建。</summary>
        private void EnsureDepthBufferForReal()
        {
            if (_rhiDevice == null || _swapChain == null) return;

            int width = _swapChain.Width;
            int height = _swapChain.Height;
            if (width <= 0 || height <= 0) return;

            // 如果已存在且尺寸匹配，复用
            if (_depthTextureReal != null &&
                _depthTextureReal.Width == width &&
                _depthTextureReal.Height == height)
                return;

            // 释放旧的深度缓冲
            _depthTextureReal?.Dispose();
            _depthTextureReal = null;

            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.DepthStencil,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };

            _depthTextureReal = _rhiDevice.CreateTexture(depthDesc, "DepthBuffer_SceneRendererReal");
            _depthTextureReal.CurrentState = RHIResourceState.DepthStencilWrite;
            _dsvHandleReal = _rhiDevice.CreateDSV(_depthTextureReal);
        }

        /// <summary>
        /// Fallback 清屏模式（SceneRenderer 不可用时）。
        /// </summary>
        private void RenderFrameClearOnly()
        {
            try
            {
                int bbIndex = _swapChain!.CurrentBackBufferIndex;
                var backBuffer = _swapChain.GetBackBuffer(bbIndex);

                _commandList!.Reset();
                _commandList.TransitionTo(backBuffer, RHIResourceState.RenderTarget);

                RHIDescriptorHandle rtv = _swapChain.GetBackBufferRTV(bbIndex);
                _commandList.ClearRenderTargetView(rtv, new Vector4(0.15f, 0.25f, 0.45f, 1.0f));

                _commandList.RSSetViewport(0, 0, _swapChain.Width, _swapChain.Height, 0, 1);
                _commandList.RSSetScissorRect(0, 0, _swapChain.Width, _swapChain.Height);

                _commandList.TransitionTo(backBuffer, RHIResourceState.Present);
                _commandList.Close();

                _graphicsQueue!.ExecuteCommandLists(_commandList);
                _graphicsQueue.Signal(_frameFence!, _frameCount);
                _swapChain.Present(_window!.VSync);
            }
            catch (Exception ex)
            {
                if (_frameCount < 2)
                    Console.WriteLine($"[EngineApplication] 清屏渲染失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Null 渲染器：仅控制台输出帧率
        /// </summary>
        private void RenderFrameNull(float dt)
        {
            // Null 模式下无实际渲染，仅消耗少量 CPU
            // FPS 信息在 UpdateFpsConsole 中输出
        }

        // ──────────────────────────────────────────────────────────
        // 窗口 Resize
        // ──────────────────────────────────────────────────────────

        private void HandleResize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            _resizeRequested = true;
        }

        private void HandleSwapChainResize()
        {
            if (_swapChain == null || _window == null || _graphicsQueue == null) return;

            int newWidth = _window.Width;
            int newHeight = _window.Height;

            // 等待 GPU 空闲
            _graphicsQueue.Flush();

            try
            {
                _swapChain.Resize(newWidth, newHeight);
                Console.WriteLine($"[EngineApplication] SwapChain 已调整: {newWidth}x{newHeight}");

                // 重建深度缓冲
                _sceneRenderer?.OnResize(newWidth, newHeight);
                _simpleRenderer?.OnResize(newWidth, newHeight);
                // SceneRendererReal 的深度缓冲在下一次渲染时惰性重建（EnsureDepthBufferForReal 自动检测尺寸变化）
                _depthTextureReal?.Dispose();
                _depthTextureReal = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EngineApplication] SwapChain Resize 失败: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────
        // 输入事件桥接
        // ──────────────────────────────────────────────────────────

        private void HandleKeyDown(int virtualKey)
        {
            var key = MapVirtualKeyToInputKey(virtualKey);
            if (key != Input.InputKey.None)
                _input?.SetKeyDown(key);
        }

        private void HandleKeyUp(int virtualKey)
        {
            var key = MapVirtualKeyToInputKey(virtualKey);
            if (key != Input.InputKey.None)
                _input?.SetKeyUp(key);
        }

        private void HandleMouseMove(int x, int y)
        {
            if (_input != null && _window != null)
            {
                // 计算鼠标移动增量（相对于上一帧）
                int dx = _prevMouseX >= 0 ? x - _prevMouseX : 0;
                int dy = _prevMouseY >= 0 ? y - _prevMouseY : 0;
                _prevMouseX = x;
                _prevMouseY = y;

                _input.UpdateMousePosition(x, y, dx, dy, _window.Width, _window.Height);
            }
        }

        private void HandleMouseDown(int button)
        {
            if (_input == null) return;
            var key = button switch
            {
                0 => Input.InputKey.MouseLeft,
                1 => Input.InputKey.MouseRight,
                2 => Input.InputKey.MouseMiddle,
                _ => Input.InputKey.None
            };
            if (key != Input.InputKey.None)
                _input.SetKeyDown(key);
        }

        private void HandleMouseUp(int button)
        {
            if (_input == null) return;
            var key = button switch
            {
                0 => Input.InputKey.MouseLeft,
                1 => Input.InputKey.MouseRight,
                2 => Input.InputKey.MouseMiddle,
                _ => Input.InputKey.None
            };
            if (key != Input.InputKey.None)
                _input.SetKeyUp(key);
        }

        /// <summary>
        /// 将 Win32 虚拟键码映射到引擎 InputKey 枚举。
        /// </summary>
        private static Input.InputKey MapVirtualKeyToInputKey(int vk)
        {
            // Win32 VK 常量
            return vk switch
            {
                0x1B => Input.InputKey.Escape,
                0x20 => Input.InputKey.Space,
                0x0D => Input.InputKey.Enter,
                0x08 => Input.InputKey.Backspace,
                0x09 => Input.InputKey.Tab,
                0x14 => Input.InputKey.CapsLock,
                0x10 => Input.InputKey.Shift,
                0x11 => Input.InputKey.Control,
                0x12 => Input.InputKey.Alt,
                0x25 => Input.InputKey.LeftArrow,
                0x27 => Input.InputKey.RightArrow,
                0x26 => Input.InputKey.UpArrow,
                0x28 => Input.InputKey.DownArrow,
                0x2D => Input.InputKey.Insert,
                0x2E => Input.InputKey.Delete,
                0x24 => Input.InputKey.Home,
                0x23 => Input.InputKey.End,
                0x21 => Input.InputKey.PageUp,
                0x22 => Input.InputKey.PageDown,
                0xBC => Input.InputKey.Comma,
                0xBE => Input.InputKey.Period,
                0xBF => Input.InputKey.Slash,
                0xBA => Input.InputKey.Semicolon,
                0xDE => Input.InputKey.Apostrophe,
                0xDB => Input.InputKey.LeftBracket,
                0xDD => Input.InputKey.RightBracket,
                0x5C => Input.InputKey.Backslash,
                0xC0 => Input.InputKey.Grave,
                0xBD => Input.InputKey.Minus,
                0xBB => Input.InputKey.Equals,
                >= 0x30 and <= 0x39 => (Input.InputKey)(Input.InputKey.D0 + (vk - 0x30)),
                >= 0x41 and <= 0x5A => (Input.InputKey)(Input.InputKey.A + (vk - 0x41)),
                >= 0x70 and <= 0x7B => (Input.InputKey)(Input.InputKey.F1 + (vk - 0x70)),
                _ => Input.InputKey.None
            };
        }

        // ──────────────────────────────────────────────────────────
        // FPS 控制台输出
        // ──────────────────────────────────────────────────────────

        private void UpdateFpsConsole(float dt)
        {
            _fpsConsoleTimer += dt;
            if (_fpsConsoleTimer >= FpsConsoleInterval)
            {
                float fps = _clock.SmoothedFPS;
                float frameMs = _clock.DeltaTime * 1000f;

                if (IsGpuAvailable)
                {
                    Console.Title = $"ShanyuEngine | FPS: {fps:F1} | Frame: {_clock.DeltaTime * 1000:F1}ms | GPU: {_rhiDevice?.AdapterName ?? "Unknown"}";
                }
                else
                {
                    Console.Title = $"ShanyuEngine (Null) | FPS: {fps:F1} | Frame: {frameMs:F1}ms";
                }

                // 首帧和每 5 秒输出一次到控制台
                if (_frameCount < 2 || Math.Floor(_fpsConsoleTimer) % 5.0 < 1.0)
                {
                    Console.WriteLine($"[Frame {_frameCount}] FPS: {fps:F1} | DT: {frameMs:F1}ms | Total: {_clock.TotalTime:F1}s");
                }

                _fpsConsoleTimer -= FpsConsoleInterval;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 关闭
        // ──────────────────────────────────────────────────────────

        private void Shutdown()
        {
            Console.WriteLine("[EngineApplication] 正在关闭引擎...");
            _isRunning = false;

            // 等待 GPU 完成
            try { _graphicsQueue?.Flush(); } catch { }

            // 逆序释放
            try { _audio?.Dispose(); } catch { }
            try { _physics?.Dispose(); } catch { }
            try { _input?.Dispose(); } catch { }
            try { _ecs?.Dispose(); } catch { }
            try { _animationRuntime?.Clear(); } catch { }
            _animationRuntime = null;
            try { _textureManager?.Dispose(); } catch { }
            _textureManager = null;

            // UE2级别 子系统释放
            try { _gpuParticles?.Dispose(); } catch { }
            _gpuParticles = null;
            try { _postProcess?.Dispose(); } catch { }
            _postProcess = null;
            try { _navigation?.Dispose(); } catch { }
            _navigation = null;
            try { _network?.StopServer(); } catch { }
            try { _network?.DisconnectFromServer(); } catch { }
            _network = null;
            try { _sceneManager?.Dispose(); } catch { }
            _sceneManager = null;
            try { _gameplayWorld?.Shutdown(); } catch { }
            _gameplayWorld = null;
            _umgRoot = null;

            // 释放场景渲染器（在 RHI 资源之前）
            try { _simpleRenderer?.Dispose(); } catch { }
            _simpleRenderer = null;
            try { _sceneRenderer?.Dispose(); } catch { }
            _sceneRenderer = null;

            try { _depthTextureReal?.Dispose(); } catch { }
            _depthTextureReal = null;

            try { _commandList?.Dispose(); } catch { }
            try { _frameFence?.Dispose(); } catch { }
            try { _swapChain?.Dispose(); } catch { }
            try { _graphicsQueue?.Dispose(); } catch { }
            try { _rhiDevice?.Dispose(); } catch { }

            try { _window?.Dispose(); } catch { }

            Console.WriteLine("[EngineApplication] 引擎已关闭。");
            Console.WriteLine($"  总帧数: {_frameCount}  总时间: {_clock.TotalTime:F1}s  平均 FPS: {_frameCount / Math.Max(1, _clock.TotalTime):F1}");
        }

        // ──────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_isRunning)
                Shutdown();
        }
    }
}
