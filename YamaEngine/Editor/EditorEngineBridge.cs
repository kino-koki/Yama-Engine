// ==================================================================================================
//  EditorEngineBridge.cs — yama引擎编辑器-引擎桥接层
//
//  命名空间: YamaEngine.Editor
//  依赖:     RHI, ECS, Physics, Assets, Scenes, Input, Audio
//
//  功能:
//    连接 WinUI 编辑器 (YamaEditor) 和引擎核心 (YamaEngine) 的关键层。
//    编辑器通过此桥接层访问引擎的所有子系统, 而不直接依赖引擎内部实现。
//    不复用 EngineBootstrap (它依赖太多重量级子系统), 而是按需初始化轻量级实例。
//
//  架构:
//    EditorEngineBridge
//    ├── RHI: RHIDevice + RHISwapChain (编辑器视口渲染)
//    ├── ECS: EntityComponentSystem (运行时实体管理)
//    ├── Physics: PhysicsWorld (物理模拟, 可选)
//    ├── Assets: AssetDatabase (资产库 + 场景对象)
//    ├── Selection: SelectionSystem (编辑器选择)
//    ├── PIE: PieStateMachine (Play-In-Editor)
//    ├── Layers: LayerSystem (层管理)
//    └── Input: InputSystem (输入)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using YamaEngine.Assets;
using YamaEngine.Assets.Importers;
using YamaEngine.Modeling;
using YamaEngine.Audio;
using YamaEngine.ECS;
using YamaEngine.Input;
using YamaEngine.Physics;
using YamaEngine.Rendering;
using YamaEngine.RHI;
using YamaEngine.Scenes;

namespace YamaEngine.Editor
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  EditorScene — 编辑器场景信息 (轻量级, 不依赖 YamaEditor 命名空间)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 编辑器场景的轻量级信息载体。由 <see cref="EditorEngineBridge.GetCurrentScene"/> 返回,
    /// 供编辑器 UI 层使用, 避免引擎层反向依赖 YamaEditor.EngineScene。
    /// </summary>
    public sealed class EditorScene
    {
        /// <summary>场景名称。</summary>
        public string Name { get; set; } = "Untitled Scene";

        /// <summary>是否处于活动状态。</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>场景中的对象数量。</summary>
        public int ObjectCount { get; set; }

        /// <summary>当前场景文件的磁盘路径 (空表示未保存)。</summary>
        public string FilePath { get; set; } = string.Empty;

        public override string ToString() => $"{Name} ({ObjectCount} objects)";
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  TextureHandle — 纹理句柄
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 纹理资源的弱句柄, 由 <see cref="EditorEngineBridge.ImportTexture"/> 返回。
    /// 内部持有资产的 <see cref="Guid"/> 和可选的 RHI 纹理引用。
    /// </summary>
    public readonly struct TextureHandle : IEquatable<TextureHandle>
    {
        /// <summary>资产 GUID。</summary>
        public readonly Guid AssetGuid;

        /// <summary>资产元数据路径 (相对项目根)。</summary>
        public readonly string AssetPath;

        /// <summary>是否有效。</summary>
        public bool IsValid => AssetGuid != Guid.Empty;

        public TextureHandle(Guid guid, string assetPath)
        {
            AssetGuid = guid;
            AssetPath = assetPath ?? string.Empty;
        }

        public bool Equals(TextureHandle other) => AssetGuid == other.AssetGuid;
        public override bool Equals(object? obj) => obj is TextureHandle h && Equals(h);
        public override int GetHashCode() => AssetGuid.GetHashCode();
        public static bool operator ==(TextureHandle a, TextureHandle b) => a.Equals(b);
        public static bool operator !=(TextureHandle a, TextureHandle b) => !a.Equals(b);

        public override string ToString() => $"Texture({AssetGuid})";
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  EditorEngineBridge — 编辑器专用引擎接口
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 编辑器-引擎桥接层。连接 WinUI 编辑器和引擎核心, 提供编辑器所需的所有引擎操作接口。
    /// 此类是编辑器访问引擎的唯一入口, 封装了 ECS / Physics / Assets / Selection / RHI 等子系统。
    /// </summary>
    public sealed class EditorEngineBridge : IDisposable
    {
        // ── 子系统引用 ──────────────────────────────────────────────────────────

        /// <summary>ECS 实体组件系统 (运行时实体管理)。</summary>
        public EntityComponentSystem ECS { get; }

        /// <summary>物理世界 (IPhysicsWorld 实现, 使用 PhysicsWorldReal)。</summary>
        public IPhysicsWorld? Physics { get; private set; }

        /// <summary>资产数据库 (资产索引 + 场景对象管理)。</summary>
        public AssetDatabase Assets { get; }

        /// <summary>编辑器选择系统 (管理当前选中的对象)。</summary>
        public SelectionSystem Selection { get; }

        /// <summary>PIE (Play-In-Editor) 状态机。</summary>
        public PieStateMachine PIE { get; }

        /// <summary>层系统 (管理编辑器层定义)。</summary>
        public LayerSystem Layers { get; }

        /// <summary>输入系统。</summary>
        public InputSystem Input { get; }

        /// <summary>音频引擎 (可能为 null)。</summary>
        public AdvancedAudioEngine? Audio { get; private set; }

        // ── RHI / 渲染 ──────────────────────────────────────────────────────────

        /// <summary>RHI 设备 (可能为 null, 当 GPU 不可用时)。</summary>
        public RHIDevice? RhiDevice { get; private set; }

        /// <summary>编辑器视口的 SwapChain。</summary>
        public RHISwapChain? ViewportSwapChain { get; private set; }

        /// <summary>是否成功初始化了真实 GPU 设备。</summary>
        public bool IsGpuAvailable { get; private set; }

        private RHICommandQueue? _graphicsQueue;
        private RHICommandList? _commandList;
        private RHIFence? _frameFence;
        private RHIDescriptorHeap? _rtvHeap;
        private ulong _frameFenceValue;

        // ── 场景渲染器 ──────────────────────────────────────────────────────────
        private SimpleSceneRenderer? _sceneRenderer;
        private float _sceneTime;

        /// <summary>编辑器后处理控制器。</summary>
        private EditorPostProcessController? _postProcess;

        /// <summary>编辑器相机控制器。</summary>
        private EditorCameraController? _cameraController;

        // ── 导入桥梁 ────────────────────────────────────────────────────────────
        private MeshImporterBridge? _meshImporter;
        private MaterialImporterBridge? _materialImporter;

        // ── Actor ↔ Entity 映射 ─────────────────────────────────────────────────

        /// <summary>Actor GUID → ECS 实体 ID 的映射。</summary>
        private readonly Dictionary<Guid, int> _actorToEntity = new();

        /// <summary>Actor GUID → SceneObject 的映射 (缓存, 与 Assets.SceneObjects 同步)。</summary>
        private readonly Dictionary<Guid, SceneObject> _actorsByGuid = new();

        // ── 物理→渲染桥接映射 ───────────────────────────────────────────────────

        /// <summary>物理体索引 → 场景对象 ID 的映射 (用于物理步进后同步变换到渲染)。</summary>
        private readonly Dictionary<int, string> _physicsRenderTargets = new(); // bodyIndex -> sceneObjectId

        /// <summary>物理→渲染桥接器 (可选, 封装批量变换同步逻辑)。</summary>
        private PhysicsRenderBridge? _physicsBridge;

        // ── 物理→渲染注册 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 注册物理体与场景对象的映射关系, 使物理步进后自动将变换同步到渲染对象。
        /// </summary>
        /// <param name="bodyIndex">物理体在 PhysicsWorld 中的索引。</param>
        /// <param name="sceneObjectId">对应的 SceneObject 的 Id 字符串。</param>
        public void RegisterPhysicsRenderTarget(int bodyIndex, string sceneObjectId)
        {
            _physicsRenderTargets[bodyIndex] = sceneObjectId;
        }

        /// <summary>
        /// 获取当前注册的物理渲染目标数量。
        /// </summary>
        public int PhysicsRenderTargetCount => _physicsRenderTargets.Count;

        // ── 状态 ────────────────────────────────────────────────────────────────

        /// <summary>当前场景信息。</summary>
        public EditorScene CurrentScene { get; } = new();

        /// <summary>编辑器视口宽度。</summary>
        public int ViewportWidth { get; private set; } = 1280;

        /// <summary>编辑器视口高度。</summary>
        public int ViewportHeight { get; private set; } = 720;

        /// <summary>编辑器相机（可能为 null，当 GPU 不可用时）。</summary>
        public EditorCameraController? Camera => _cameraController;

        /// <summary>是否已被释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>日志消息事件 (编辑器可订阅以显示到控制台)。</summary>
        public event Action<string>? OnLogMessage;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建编辑器引擎桥接实例。
        /// </summary>
        /// <param name="projectPath">项目根目录路径 (资产库将在此目录下工作)。</param>
        public EditorEngineBridge(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                projectPath = Path.Combine(Path.GetTempPath(), "YamaEngine_Projects");

            // 初始化子系统 (轻量级, 不依赖 native 库)
            ECS = new EntityComponentSystem();
            Assets = new AssetDatabase(projectPath);
            Selection = new SelectionSystem(Assets);
            PIE = new PieStateMachine(Assets);
            Layers = new LayerSystem();
            Input = new InputSystem();

            // 物理系统 — 使用 PhysicsWorldReal (纯 C# 实现, 无外部依赖)
            Physics = new PhysicsWorldReal(PhysicsWorldConfig.Default);

            // 音频系统 — 可选
            try
            {
                Audio = new AdvancedAudioEngine();
            }
            catch (Exception ex)
            {
                Log($"AdvancedAudioEngine 初始化失败 (预期行为): {ex.Message}");
                Audio = null;
            }

            // 初始化导入桥梁
            _meshImporter = new MeshImporterBridge(Assets);
            _materialImporter = new MaterialImporterBridge(Assets);

            Log("EditorEngineBridge 已创建 (ECS + Assets + Selection + PIE + Layers + Input + Importers)");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Initialize — 初始化编辑器引擎实例
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化编辑器引擎实例。使用传入的 RHI 设备 (或尝试自动创建 D3D12 设备)。
        /// 不复用 EngineBootstrap, 保持轻量级。
        /// </summary>
        /// <param name="device">
        /// 外部传入的 RHI 设备。如果为 null, 将尝试通过 <see cref="RHIDeviceFactory"/> 自动创建。
        /// </param>
        public void Initialize(RHIDevice? device = null)
        {
            if (device != null)
            {
                RhiDevice = device;
                IsGpuAvailable = true;
                Log($"使用外部 RHI 设备: {RhiDevice.AdapterName}");
            }
            else
            {
                // 尝试自动创建 D3D12 设备 (与 EngineApplication 相同的 fallback 策略)
                try
                {
                    RhiDevice = RHIDeviceFactory.Create(RHIBackendType.Vulkan, enableDebug: true);
                    IsGpuAvailable = true;
                    Log($"RHI 设备创建成功: {RhiDevice.AdapterName}");
                    Log($"  显存: {RhiDevice.DedicatedVideoMemory / (1024 * 1024 * 1024):F1} GB");
                    Log($"  Mesh Shader: {RhiDevice.SupportsMeshShaders}");
                    Log($"  Ray Tracing: {RhiDevice.SupportsRayTracing}");
                }
                catch (Exception ex)
                {
                    Log($"D3D12 设备创建失败, 编辑器将以无 GPU 模式运行: {ex.Message}");
                    RhiDevice = null;
                    IsGpuAvailable = false;
                }
            }

            // 初始化渲染资源 (仅当 GPU 可用时)
            if (IsGpuAvailable && RhiDevice != null)
            {
                _graphicsQueue = RhiDevice.GetCommandQueue(RHIQueueType.Graphics);
                _frameFence = RhiDevice.CreateFence(0, "EditorFrameFence");
                _commandList = RhiDevice.CreateCommandList(RHIQueueType.Graphics, "EditorCL");
                _rtvHeap = RhiDevice.CreateDescriptorHeap(RHIDescriptorHeapType.RTV, 16, shaderVisible: false);
            }

            // 创建物理演示场景（如果物理系统可用且场景为空）
            if (Physics != null && Assets.SceneObjects.Count == 0)
            {
                try
                {
                    var demo = PhysicsDemoScene.CreateSimpleDemo();
                    Physics = demo.World;
                    _physicsBridge = demo.Bridge;

                    // 为每个物理渲染目标创建场景对象
                    foreach (var target in demo.RenderTargets)
                    {
                        var sceneObj = new SceneObject
                        {
                            Name = target.Name,
                            Position = target.Position,
                            Rotation = target.Rotation,
                            Scale = target.Scale
                        };
                        Assets.AddObject(sceneObj);

                        // 注册物理体索引到场景对象的映射
                        int bodyIndex = demo.RenderTargets.IndexOf(target);
                        if (bodyIndex >= 0)
                        {
                            _physicsRenderTargets[bodyIndex] = sceneObj.Id.ToString();
                        }
                    }

                    Log($"物理演示场景已创建: {demo.RenderTargets.Count} 个物体");
                }
                catch (Exception ex)
                {
                    Log($"物理演示场景创建失败: {ex.Message}");
                }
            }

            Log("EditorEngineBridge 初始化完成");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  ViewportSwapChain — 为编辑器视口创建 SwapChain
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 为编辑器视口创建 SwapChain。
        /// </summary>
        /// <param name="hwnd">窗口句柄 (Win32 HWND 或 SwapChainPanel 的 COM 指针)。</param>
        /// <param name="width">视口宽度。</param>
        /// <param name="height">视口高度。</param>
        /// <returns>创建的 SwapChain, 如果 GPU 不可用则返回 null。</returns>
        public RHISwapChain? CreateViewportSwapChain(IntPtr hwnd, int width, int height)
        {
            ViewportWidth = Math.Max(1, width);
            ViewportHeight = Math.Max(1, height);

            if (!IsGpuAvailable || RhiDevice == null)
            {
                Log("无法创建 SwapChain: GPU 不可用");
                return null;
            }

            try
            {
                ViewportSwapChain = RhiDevice.CreateSwapChain(
                    hwnd,
                    ViewportWidth,
                    ViewportHeight,
                    RHIFormat.R8G8B8A8_UNorm,
                    3,  // 2+1 buffers
                    true,
                    "EditorViewportSwapChain");

                Log($"编辑器视口 SwapChain 创建成功: {ViewportSwapChain.Width}x{ViewportSwapChain.Height}");

                // 初始化场景渲染器（需要 SwapChain）
                try
                {
                    _sceneRenderer = new SimpleSceneRenderer(
                        RhiDevice, ViewportSwapChain, _graphicsQueue!, _frameFence!, _commandList!);
                    _sceneRenderer.Init(debugShaders: true, rtvFormats: new[] { RHIFormat.R16G16B16A16_Float });
                    _sceneRenderer.LightDirection = new Vector3(-0.5f, -1.0f, -0.5f);
                    _sceneRenderer.LightColor = new Vector3(0.9f, 0.85f, 0.7f);
                    _sceneRenderer.ClearColor = new Vector4(0.12f, 0.12f, 0.14f, 1.0f);

                    // 添加默认测试网格：一个立方体
                    var cubeMesh = EngineBuiltinGeometry.GenerateCube(1.0f);
                    _sceneRenderer.AddMesh(cubeMesh, new Vector3(0, 0, 0), Vector3.One,
                        new Vector4(0.6f, 0.7f, 0.9f, 1.0f), "TestCube");
                    cubeMesh.Dispose();

                    // 添加一个地面平面
                    var planeMesh = EngineBuiltinGeometry.GeneratePlane(10.0f, 10.0f);
                    _sceneRenderer.AddMesh(planeMesh, new Vector3(0, -0.5f, 0), Vector3.One,
                        new Vector4(0.4f, 0.45f, 0.5f, 1.0f), "GroundPlane");
                    planeMesh.Dispose();

                    // 添加方向光源到 AssetDatabase
                    if (Assets.FindObjectByName("DirectionalLight") == null)
                    {
                        var lightObj = new SceneObject
                        {
                            Name = "DirectionalLight",
                            Position = new Vector3(5, 10, 5),
                            MeshAssetPath = "Light_Directional"
                        };
                        var lightComp = lightObj.AddComponent<YamaEngine.Assets.LightComponent>();
                        lightComp.LightType = "Directional";
                        lightComp.Color = Vector3.One;
                        lightComp.Intensity = 1.2f;
                        Assets.AddObject(lightObj);
                    }

                    Log("场景渲染器初始化成功，已添加默认测试网格 (立方体 + 地面)");

                    // 初始化编辑器相机
                    _cameraController = new EditorCameraController
                    {
                        Position = new Vector3(2.5f, 2.0f, 3.0f),
                        FieldOfView = MathF.PI / 3f, // 60°
                        NearClip = 0.1f,
                        FarClip = 10000f,
                        MoveSpeed = 10f,
                        RotateSpeed = 2f,
                        ZoomSpeed = 5f
                    };
                    _cameraController.LookAt(Vector3.Zero);

                    // 初始化编辑器后处理
                    try
                    {
                        _postProcess = new EditorPostProcessController(RhiDevice, ViewportWidth, ViewportHeight);
                        _postProcess.EnableBloom(0.5f);
                        _postProcess.EnableFXAA();
                        Log("编辑器后处理初始化成功 (Bloom + FXAA)");
                    }
                    catch (Exception ex)
                    {
                        Log($"编辑器后处理初始化失败: {ex.Message}");
                        _postProcess?.Dispose();
                        _postProcess = null;
                    }
                }
                catch (Exception ex)
                {
                    Log($"场景渲染器初始化失败: {ex.Message}");
                    _sceneRenderer?.Dispose();
                    _sceneRenderer = null;
                }

                return ViewportSwapChain;
            }
            catch (Exception ex)
            {
                Log($"SwapChain 创建失败: {ex.Message}");
                ViewportSwapChain = null;
                return null;
            }
        }

        /// <summary>
        /// 调整编辑器视口 SwapChain 的尺寸。
        /// </summary>
        public void ResizeViewport(int width, int height)
        {
            ViewportWidth = Math.Max(1, width);
            ViewportHeight = Math.Max(1, height);

            if (ViewportSwapChain != null)
            {
                // 等待 GPU 空闲后再 resize
                WaitForGpu();

                try
                {
                    ViewportSwapChain.Resize(ViewportWidth, ViewportHeight);
                    _sceneRenderer?.OnResize(ViewportWidth, ViewportHeight);
                    _postProcess?.Resize(ViewportWidth, ViewportHeight);
                    Log($"视口 resize: {ViewportWidth}x{ViewportHeight}");
                }
                catch (Exception ex)
                {
                    Log($"视口 resize 失败: {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  RenderViewportScene — 渲染当前场景到编辑器视口
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 渲染当前场景到编辑器视口。
        /// 当 GPU 不可用时, 此方法为空操作 (编辑器仍可进行非渲染操作)。
        /// </summary>
        public void RenderViewportScene()
        {
            if (!IsGpuAvailable || RhiDevice == null || ViewportSwapChain == null)
                return;

            if (_commandList == null || _graphicsQueue == null || _frameFence == null || _rtvHeap == null)
                return;

            // 场景渲染器可用时：委托给它做完整帧渲染（含 ClearRTV + 绘制 + Present）
            if (_sceneRenderer != null && _sceneRenderer.IsInitialized)
            {
                try
                {
                    _sceneTime += 1.0f / 60.0f; // 近似帧时间

                    Matrix4x4 view, proj;
                    if (_cameraController != null)
                    {
                        // 使用编辑器相机
                        _cameraController.Update(1f / 60f);
                        view = _cameraController.ViewMatrix;
                        _cameraController.Aspect = ViewportWidth / (float)Math.Max(1, ViewportHeight);
                        proj = _cameraController.ProjectionMatrix;
                    }
                    else
                    {
                        // 回退：默认硬编码相机
                        view = Matrix4x4.CreateLookAt(
                            new Vector3(2.5f, 2.0f, 3.0f),
                            new Vector3(0, 0, 0),
                            Vector3.UnitY);
                        proj = Matrix4x4.CreatePerspectiveFieldOfView(
                            MathF.PI / 4f,
                            ViewportWidth / (float)Math.Max(1, ViewportHeight),
                            0.1f, 100.0f);
                    }

                    // ── 后处理路径：渲染到中间 HDR 纹理 → 后处理 → SwapChain ──
                    if (_postProcess != null && _postProcess.Enabled && _postProcess.SceneColorTexture != null)
                    {
                        // 等待 GPU 空闲
                        _frameFence.Wait(_frameFenceValue);

                        _commandList!.Reset(null);

                        // 渲染场景到中间 HDR 纹理
                        _commandList.TransitionTo(_postProcess.SceneColorTexture, RHIResourceState.RenderTarget);
                        _sceneRenderer.RenderToTexture(
                            _postProcess.SceneColorRTV,
                            _postProcess.SceneDepthDSV,
                            ViewportWidth,
                            ViewportHeight,
                            view, proj, _sceneTime,
                            _commandList);

                        // 获取 SwapChain 后缓冲
                        int bbIndex = ViewportSwapChain.CurrentBackBufferIndex;
                        var backBuffer = ViewportSwapChain.GetBackBuffer(bbIndex);
                        var rtvHandle = ViewportSwapChain.GetBackBufferRTV(bbIndex);

                        // 后处理：中间纹理 → SwapChain 后缓冲
                        _postProcess.RenderPostProcess(
                            _commandList,
                            1.0f / 60.0f,
                            targetRtv: rtvHandle,
                            invProjMatrix: Matrix4x4.Invert(proj, out var invProj) ? invProj : Matrix4x4.Identity);

                        // Transition 到 Present
                        _commandList.TransitionTo(backBuffer, RHIResourceState.Present);

                        _commandList.Close();
                        _graphicsQueue!.ExecuteCommandLists(_commandList);

                        // 同步
                        _frameFenceValue++;
                        _graphicsQueue.Signal(_frameFence!, _frameFenceValue);
                        _frameFence.Wait(_frameFenceValue);

                        // Present
                        ViewportSwapChain.Present(true);
                    }
                    else
                    {
                        // 无后处理：直接渲染到 SwapChain
                        _sceneRenderer.Render(view, proj, _sceneTime, vsync: true);
                    }
                }
                catch (Exception ex)
                {
                    Log($"场景渲染失败: {ex.Message}");
                }
                return;
            }

            // 回退：仅清屏（场景渲染器不可用时的 fallback）
            try
            {
                // 获取当前后缓冲
                int backBufferIndex = ViewportSwapChain.CurrentBackBufferIndex;
                var rtvHandle = ViewportSwapChain.GetBackBufferRTV(backBufferIndex);
                var backBuffer = ViewportSwapChain.GetBackBuffer(backBufferIndex);

                // 录制命令 (Reset 后命令列表处于打开状态)
                _commandList.Reset(null);

                // 屏障: PRESENT → RENDER_TARGET
                _commandList.ResourceBarrier(backBuffer, RHIResourceState.Present, RHIResourceState.RenderTarget);

                // 设置视口和裁剪矩形
                _commandList.RSSetViewport(0, 0, ViewportWidth, ViewportHeight, 0f, 1f);
                _commandList.RSSetScissorRect(0, 0, ViewportWidth, ViewportHeight);

                // 清屏 (编辑器背景色: 深灰色)
                _commandList.ClearRenderTargetView(rtvHandle, new Vector4(0.12f, 0.12f, 0.14f, 1.0f));

                // 注意: 场景渲染由上方 _sceneRenderer.Render() 处理

                // 屏障: RENDER_TARGET → PRESENT
                _commandList.ResourceBarrier(backBuffer, RHIResourceState.RenderTarget, RHIResourceState.Present);

                _commandList.Close();

                // 执行
                _graphicsQueue.ExecuteCommandLists(_commandList);

                // 同步
                _frameFenceValue++;
                _graphicsQueue.Signal(_frameFence, _frameFenceValue);
                _frameFence.Wait(_frameFenceValue);

                // 呈现
                ViewportSwapChain.Present(true);
            }
            catch (Exception ex)
            {
                Log($"视口渲染失败: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  GetCurrentScene — 获取当前场景
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取当前编辑器场景的信息。
        /// </summary>
        public EditorScene GetCurrentScene()
        {
            CurrentScene.ObjectCount = Assets.ObjectCount;
            return CurrentScene;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Actor 操作 — 选择
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 选中指定 GUID 的 Actor。如果 additive 为 false, 清除其他选择。
        /// </summary>
        public void SelectActor(Guid actorId, bool additive = false)
        {
            var obj = FindActor(actorId);
            if (obj == null)
            {
                Log($"SelectActor: 未找到 Actor {actorId}");
                return;
            }
            Selection.Select(obj, additive);
        }

        /// <summary>
        /// 获取当前选中的所有 Actor 的 GUID 列表。
        /// </summary>
        public List<Guid> GetSelectedActors()
        {
            var result = new List<Guid>();
            foreach (var obj in Selection.Selected)
            {
                result.Add(obj.Id);
            }
            return result;
        }

        /// <summary>
        /// 清除所有选择。
        /// </summary>
        public void ClearSelection()
        {
            Selection.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Actor 操作 — 创建
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建一个新的 Actor (场景对象), 返回其 GUID。
        /// 同时在 ECS 中创建对应的实体。
        /// </summary>
        /// <param name="name">Actor 名称。</param>
        /// <param name="position">初始位置。</param>
        /// <returns>新创建 Actor 的 GUID。</returns>
        public Guid CreateActor(string name, Vector3 position)
        {
            var obj = new SceneObject
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(name) ? "GameObject" : name,
                Position = position,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                LocalPosition = position,
                LocalRotation = Quaternion.Identity,
                LocalScale = Vector3.One,
                IsVisible = true,
            };

            Assets.AddObject(obj);
            _actorsByGuid[obj.Id] = obj;

            // 在 ECS 中创建对应实体
            int entityId = ECS.CreateEntity(obj.Name, position, Quaternion.Identity, Vector3.One);
            _actorToEntity[obj.Id] = entityId;

            // 添加 TransformComponent (确保 ECS 中有变换数据)
            ECS.AddComponent(entityId, new TransformComponent
            {
                Position = position,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            });

            Log($"创建 Actor: {obj.Name} (GUID={obj.Id}, Entity={entityId})");
            return obj.Id;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Actor 操作 — 删除
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 删除指定 GUID 的 Actor。
        /// 同时删除 ECS 中的对应实体。
        /// </summary>
        public void DeleteActor(Guid actorId)
        {
            var obj = FindActor(actorId);
            if (obj == null)
            {
                Log($"DeleteActor: 未找到 Actor {actorId}");
                return;
            }

            // 从选择中移除
            Selection.Deselect(obj);

            // 从资产库移除
            Assets.RemoveObject(obj);
            _actorsByGuid.Remove(actorId);

            // 从 ECS 移除
            if (_actorToEntity.TryGetValue(actorId, out int entityId))
            {
                if (ECS.HasEntity(entityId))
                    ECS.DestroyEntity(entityId);
                _actorToEntity.Remove(actorId);
            }

            Log($"删除 Actor: {obj.Name} (GUID={actorId})");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Actor 操作 — 复制
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 复制指定 GUID 的 Actor, 返回新 Actor 的 GUID。
        /// 新 Actor 位置偏移 (1, 0, 1) 以避免重叠。
        /// </summary>
        public Guid DuplicateActor(Guid actorId)
        {
            var obj = FindActor(actorId);
            if (obj == null)
            {
                Log($"DuplicateActor: 未找到 Actor {actorId}");
                return Guid.Empty;
            }

            var newPos = obj.Position + new Vector3(1, 0, 1);
            var newId = CreateActor(obj.Name + " (Copy)", newPos);

            // 复制属性
            var newObj = FindActor(newId);
            if (newObj != null)
            {
                newObj.Rotation = obj.Rotation;
                newObj.Scale = obj.Scale;
                newObj.LocalRotation = obj.Rotation;
                newObj.LocalScale = obj.Scale;
                newObj.MeshAssetPath = obj.MeshAssetPath;
                newObj.MaterialAssetPath = obj.MaterialAssetPath;
                newObj.BaseColor = obj.BaseColor;
                newObj.Metallic = obj.Metallic;
                newObj.Roughness = obj.Roughness;
                newObj.CastShadow = obj.CastShadow;
                newObj.ReceiveShadow = obj.ReceiveShadow;
                newObj.Layer = obj.Layer;
                newObj.IsVisible = obj.IsVisible;
            }

            // 同步 ECS 变换
            SetActorTransform(newId, newPos, obj.Rotation, obj.Scale);

            Log($"复制 Actor: {obj.Name} → {newId}");
            return newId;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Actor 操作 — 变换
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 设置指定 Actor 的变换 (位置、旋转、缩放)。
        /// 同时更新 ECS 中的 TransformComponent。
        /// </summary>
        public void SetActorTransform(Guid actorId, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var obj = FindActor(actorId);
            if (obj == null)
            {
                Log($"SetActorTransform: 未找到 Actor {actorId}");
                return;
            }

            obj.Position = pos;
            obj.Rotation = rot;
            obj.Scale = scale;
            obj.LocalPosition = pos;
            obj.LocalRotation = rot;
            obj.LocalScale = scale;

            // 同步到 ECS
            if (_actorToEntity.TryGetValue(actorId, out int entityId) && ECS.HasEntity(entityId))
            {
                ECS.SetComponent(entityId, new TransformComponent
                {
                    Position = pos,
                    Rotation = rot,
                    Scale = scale,
                });
            }
        }

        /// <summary>
        /// 获取指定 Actor 的变换。
        /// </summary>
        /// <returns>包含位置、旋转、缩放的元组。如果 Actor 不存在, 返回零值。</returns>
        public (Vector3 pos, Quaternion rot, Vector3 scale) GetActorTransform(Guid actorId)
        {
            var obj = FindActor(actorId);
            if (obj == null)
                return (Vector3.Zero, Quaternion.Identity, Vector3.One);

            return (obj.Position, obj.Rotation, obj.Scale);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  场景操作 — 保存 / 加载
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 保存当前场景到指定路径 (.yama 格式)。
        /// </summary>
        public void SaveScene(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Log("SaveScene: 路径为空");
                return;
            }

            try
            {
                SceneSerializer.Save(path, Assets.SceneObjects, CurrentScene.Name);
                CurrentScene.FilePath = path;
                Log($"场景已保存: {path} ({Assets.ObjectCount} objects)");
            }
            catch (Exception ex)
            {
                Log($"保存场景失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从指定路径加载场景 (.yama 格式)。
        /// </summary>
        public void LoadScene(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log($"LoadScene: 文件不存在 {path}");
                return;
            }

            try
            {
                var doc = SceneSerializer.Load(path);
                var objects = SceneSerializer.Materialize(doc);

                // 清除当前场景
                Assets.ClearScene();
                _actorsByGuid.Clear();

                // 清除 ECS 实体
                foreach (var kvp in _actorToEntity)
                {
                    if (ECS.HasEntity(kvp.Value))
                        ECS.DestroyEntity(kvp.Value);
                }
                _actorToEntity.Clear();

                // 加载新场景对象
                foreach (var obj in objects)
                {
                    Assets.AddObject(obj);
                    _actorsByGuid[obj.Id] = obj;

                    // 在 ECS 中创建实体
                    int entityId = ECS.CreateEntity(obj.Name, obj.Position, obj.Rotation, obj.Scale);
                    _actorToEntity[obj.Id] = entityId;

                    ECS.AddComponent(entityId, new TransformComponent
                    {
                        Position = obj.Position,
                        Rotation = obj.Rotation,
                        Scale = obj.Scale,
                    });
                }

                CurrentScene.Name = doc.Name ?? "Main";
                CurrentScene.FilePath = path;
                CurrentScene.IsActive = true;

                Log($"场景已加载: {path} ({objects.Count} objects)");
            }
            catch (Exception ex)
            {
                Log($"加载场景失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建新的空场景。
        /// </summary>
        public void CreateNewScene(string name = "Untitled Scene")
        {
            Assets.ClearScene();
            _actorsByGuid.Clear();

            foreach (var kvp in _actorToEntity)
            {
                if (ECS.HasEntity(kvp.Value))
                    ECS.DestroyEntity(kvp.Value);
            }
            _actorToEntity.Clear();

            Selection.Clear();

            CurrentScene.Name = name;
            CurrentScene.FilePath = string.Empty;
            CurrentScene.IsActive = true;

            Log($"新场景已创建: {name}");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  PIE — Play-In-Editor
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 进入 Play-In-Editor 模式。快照当前编辑器场景, 开始运行游戏逻辑。
        /// </summary>
        public void PlayInEditor()
        {
            PIE.Play();
            Log("PIE: 进入运行模式");
        }

        /// <summary>
        /// 停止 PIE, 恢复编辑器场景。
        /// </summary>
        public void StopPIE()
        {
            PIE.Stop();
            Log("PIE: 停止运行, 恢复编辑器场景");
        }

        /// <summary>
        /// 暂停 PIE (游戏逻辑冻结, 但可单步)。
        /// </summary>
        public void PausePIE()
        {
            PIE.Pause();
            Log("PIE: 已暂停");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  资产导入
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 导入模型文件 (fbx/obj/gltf 等), 创建对应的 Actor, 返回 Actor 的 GUID。
        /// </summary>
        /// <param name="path">模型文件路径。</param>
        /// <returns>新创建 Actor 的 GUID, 失败返回 Guid.Empty。</returns>
        public Guid ImportModel(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log($"ImportModel: 文件不存在 {path}");
                return Guid.Empty;
            }

            try
            {
                var metadata = Assets.ImportAsset(path, AssetType.Mesh);
                if (metadata == null)
                {
                    Log($"ImportModel: 导入失败 {path}");
                    return Guid.Empty;
                }

                // 创建 Actor
                string name = Path.GetFileNameWithoutExtension(path);
                var actorId = CreateActor(name, Vector3.Zero);

                var obj = FindActor(actorId);
                if (obj != null)
                {
                    obj.MeshAssetPath = metadata.AssetPath ?? path;
                }

                // 通过内置导入器获取 InterchangeScene 并上传到 GPU
                string ext = Path.GetExtension(path).ToLowerInvariant();
                InterchangeScene? scene = null;
                switch (ext)
                {
                    case ".fbx":
                        scene = FbxImporterFactory.Import(path);
                        break;
                    case ".gltf":
                    case ".glb":
                        scene = new GlTFImporter(path).Import();
                        break;
                }

                if (scene != null && scene.Meshes.Count > 0 && _sceneRenderer != null)
                {
                    var editableMeshes = ModelToMeshConverter.Convert(scene);
                    foreach (var mesh in editableMeshes)
                    {
                        string meshPath = $"{metadata.AssetPath ?? path}#{mesh.Name}";
                        var gpuMesh = _sceneRenderer.UploadMesh(meshPath, mesh);
                        _sceneRenderer.AddMesh(gpuMesh, obj?.Position ?? Vector3.Zero, Vector3.One, Vector4.One, mesh.Name);
                    }
                }

                Log($"导入模型: {path} → Actor {actorId}");
                return actorId;
            }
            catch (Exception ex)
            {
                Log($"ImportModel 异常: {ex.Message}");
                return Guid.Empty;
            }
        }

        /// <summary>
        /// 导入纹理文件 (png/jpg/tga 等), 返回纹理句柄。
        /// </summary>
        /// <param name="path">纹理文件路径。</param>
        /// <returns>纹理句柄, 失败返回无效句柄。</returns>
        public TextureHandle ImportTexture(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log($"ImportTexture: 文件不存在 {path}");
                return default;
            }

            try
            {
                var metadata = Assets.ImportAsset(path, AssetType.Texture2D);
                if (metadata == null)
                {
                    Log($"ImportTexture: 导入失败 {path}");
                    return default;
                }

                Log($"导入纹理: {path} → {metadata.Guid}");
                return new TextureHandle(metadata.Guid, metadata.AssetPath ?? path);
            }
            catch (Exception ex)
            {
                Log($"ImportTexture 异常: {ex.Message}");
                return default;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  模型导入到渲染 — 通过 MeshImporterBridge
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>导入3D模型文件并在场景中创建渲染对象。</summary>
        public async Task<bool> ImportModelAsync(string filePath)
        {
            if (_sceneRenderer == null || RhiDevice == null) return false;
            try
            {
                var meshes = await _meshImporter!.ImportFromInterchangeAsync(filePath, RhiDevice);
                foreach (var mesh in meshes)
                {
                    _sceneRenderer.AddMesh(mesh, Vector3.Zero, Vector3.One, Vector4.One, Path.GetFileNameWithoutExtension(filePath));
                }
                Log($"导入成功: {filePath} ({meshes.Count} 个网格)");
                return true;
            }
            catch (Exception ex)
            {
                Log($"导入失败: {filePath} - {ex.Message}");
                return false;
            }
        }

        /// <summary>清除场景中所有导入的模型，保留默认测试网格。</summary>
        public void ClearImportedModels()
        {
            _sceneRenderer?.ClearRenderItemsExceptDefault();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Tick — 编辑器帧更新 (逻辑, 不渲染)
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 编辑器帧更新。只更新逻辑, 不执行渲染 (渲染由 <see cref="RenderViewportScene"/> 单独调用)。
        /// </summary>
        /// <param name="dt">帧间隔时间 (秒)。</param>
        public void Tick(float dt)
        {
            if (IsDisposed) return;

            // 更新输入系统
            Input?.Update(dt);

            // PIE 运行时: 步进物理
            if (PIE.IsPlaying && !PIE.IsPaused)
            {
                if (Physics != null && !Physics.IsPaused)
                {
                    try
                    {
                        Physics.Step(dt);
                    }
                    catch (Exception ex)
                    {
                        Log($"物理步进失败: {ex.Message}");
                    }
                }

                // 物理→场景对象变换同步 (将物理模拟结果回写到 SceneObject)
                if (Physics != null && _physicsRenderTargets != null)
                {
                    for (int i = 0; i < Physics.BodyCount; i++)
                    {
                        var body = Physics.GetBody(i);
                        if (body == null) continue;

                        // 从 _physicsRenderTargets 查找对应的 SceneObject
                        if (_physicsRenderTargets.TryGetValue(i, out var sceneObjId))
                        {
                            SceneObject? sceneObj = null;
                            if (Guid.TryParse(sceneObjId, out var guid))
                            {
                                sceneObj = FindActor(guid);
                            }

                            // 回退: 按名称查找
                            if (sceneObj == null)
                            {
                                sceneObj = Assets.FindObjectByName(sceneObjId);
                            }

                            if (sceneObj != null)
                            {
                                // PhysicsVector3/PhysicsQuaternion 有到 Vector3/Quaternion 的隐式转换
                                sceneObj.Position = body.Position;
                                sceneObj.Rotation = body.Rotation;
                            }
                        }
                    }
                }

                // 物理桥接器批量同步 (可选, 封装更高效的批量变换同步)
                if (_physicsBridge != null)
                {
                    _physicsBridge.SyncTransforms();
                }
            }

            // 更新音频引擎
            // (AdvancedAudioEngine 的更新由其内部定时器驱动, 这里不主动调用)

            // 更新场景对象的世界矩阵
            foreach (var obj in Assets.SceneObjects)
            {
                obj.WorldMatrix = Matrix4x4.CreateScale(obj.Scale) *
                                  Matrix4x4.CreateFromQuaternion(obj.Rotation) *
                                  Matrix4x4.CreateTranslation(obj.Position);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  查询
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按 GUID 查找 Actor (SceneObject)。
        /// </summary>
        public SceneObject? FindActor(Guid actorId)
        {
            // 先查缓存
            if (_actorsByGuid.TryGetValue(actorId, out var cached))
                return cached;

            // 缓存未命中, 遍历资产库
            foreach (var obj in Assets.SceneObjects)
            {
                if (obj.Id == actorId)
                {
                    _actorsByGuid[actorId] = obj;
                    return obj;
                }
            }

            return null;
        }

        /// <summary>
        /// 获取 Actor 对应的 ECS 实体 ID。如果不存在, 返回 -1。
        /// </summary>
        public int GetActorEntityId(Guid actorId)
        {
            return _actorToEntity.TryGetValue(actorId, out int id) ? id : -1;
        }

        /// <summary>
        /// 获取所有 Actor 的 GUID 列表。
        /// </summary>
        public List<Guid> GetAllActors()
        {
            var result = new List<Guid>();
            foreach (var obj in Assets.SceneObjects)
                result.Add(obj.Id);
            return result;
        }

        /// <summary>
        /// 获取所有 Actor 的 (GUID, 名称) 列表, 供 Outliner 显示。
        /// </summary>
        public List<(Guid id, string name)> GetAllActorNames()
        {
            var result = new List<(Guid, string)>();
            foreach (var obj in Assets.SceneObjects)
                result.Add((obj.Id, obj.Name));
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  内部辅助
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>等待 GPU 完成当前帧。</summary>
        private void WaitForGpu()
        {
            if (_frameFence == null || _graphicsQueue == null) return;
            _frameFenceValue++;
            _graphicsQueue.Signal(_frameFence, _frameFenceValue);
            _frameFence.Wait(_frameFenceValue);
        }

        /// <summary>记录日志消息。</summary>
        private void Log(string message)
        {
            Debug.WriteLine($"[EditorEngineBridge] {message}");
            OnLogMessage?.Invoke(message);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  相机交互方法（由编辑器视口鼠标/键盘事件调用）
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>视口鼠标左键拖拽（轨道旋转或飞行）。</summary>
        public void ViewportMouseDrag(float deltaX, float deltaY, bool altHeld, bool middleButton)
        {
            _cameraController?.OrbitRotate(deltaX * 0.01f, deltaY * 0.01f);
        }

        /// <summary>视口鼠标中键拖拽（平移）。</summary>
        public void ViewportPan(float deltaX, float deltaY)
        {
            _cameraController?.Pan(deltaX * 0.01f, deltaY * 0.01f);
        }

        /// <summary>视口滚轮（缩放）。</summary>
        public void ViewportZoom(float delta)
        {
            _cameraController?.Zoom(delta * 0.1f);
        }

        /// <summary>视口键盘WASD（飞行移动）。</summary>
        public void ViewportFly(float forward, float strafe, float vertical)
        {
            if (_cameraController == null) return;
            _cameraController.FlyForward(forward);
            _cameraController.FlyStrafe(strafe);
            _cameraController.FlyVertical(vertical);
        }

        /// <summary>聚焦选中物体。</summary>
        public void FocusSelection()
        {
            // 获取选中物体的中心位置
            var obj = Selection.Active;
            if (obj == null) return;
            _cameraController?.FocusOn(obj.Position);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  后处理 UI 绑定方法
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>设置泛光强度。</summary>
        public void SetPostProcessBloom(float intensity) => _postProcess?.SetBloomIntensity(intensity);

        /// <summary>设置景深对焦距离。</summary>
        public void SetPostProcessDOF(float focusDistance) => _postProcess?.SetDOFFocusDistance(focusDistance);

        /// <summary>切换指定后处理特效。</summary>
        public void TogglePostProcess(PostProcessEffectType effect) => _postProcess?.ToggleEffect(effect);

        /// <summary>后处理是否启用。</summary>
        public bool IsPostProcessEnabled => _postProcess?.Enabled ?? false;

        /// <summary>设置后处理全局开关。</summary>
        public void SetPostProcessEnabled(bool enabled)
        {
            if (_postProcess != null) _postProcess.Enabled = enabled;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            if (IsGpuAvailable)
            {
                WaitForGpu();
            }

            ViewportSwapChain?.Dispose();
            _sceneRenderer?.Dispose();
            _postProcess?.Dispose();
            _rtvHeap?.Dispose();
            _commandList?.Dispose();
            _frameFence?.Dispose();
            _graphicsQueue?.Dispose();
            RhiDevice?.Dispose();

            Physics?.Dispose();
            Audio?.Dispose();
            Assets.Dispose();

            ViewportSwapChain = null;
            _sceneRenderer = null;
            _postProcess = null;
            _cameraController = null;
            _meshImporter = null;
            _materialImporter = null;
            _rtvHeap = null;
            _commandList = null;
            _frameFence = null;
            _graphicsQueue = null;
            RhiDevice = null;
            Physics = null;
            Audio = null;

            Log("EditorEngineBridge 已释放");
        }
    }
}
