// ============================================================================================
//  EngineRuntimeModules.cs
//
//  yama引擎 (YamaEngine) 运行时模块集合。
//  将 EngineBootstrap 的手动初始化序列拆分为独立的 ModuleManager 模块，
//  实现按依赖拓扑排序、自动并行初始化。
//
//  每个模块的依赖通过 Dependencies 属性声明，ModuleManager 自动排序。
//
//  命名空间：YamaEngine.Core
//  依赖：EngineModule.cs（基类 EngineModuleBase）
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
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

namespace YamaEngine.Core
{
    // ════════════════════════════════════════════════════════════════
    //  RHIModule — RHI 设备、命令队列、交换链、描述符堆
    // ════════════════════════════════════════════════════════════════

    /// <summary>RHI 渲染后端模块。初始化 D3D12/Vulkan 设备、队列和交换链。</summary>
    [EngineModule("RHI", AutoRegister = false)]
    public sealed class RHIModule : EngineModuleBase
    {
        public override string Name => "RHI";
        public override int Priority => -50;
        public override string[] Dependencies => new[] { "Core", "Config" };

        /// <summary>RHI 设备。</summary>
        public RHIDevice Device { get; private set; } = null!;

        /// <summary>图形命令队列。</summary>
        public RHICommandQueue GraphicsQueue { get; private set; } = null!;

        /// <summary>交换链（可选）。</summary>
        public RHISwapChain? SwapChain { get; private set; }

        /// <summary>帧同步 Fence。</summary>
        public RHIFence FrameFence { get; private set; } = null!;

        /// <summary>帧命令列表。</summary>
        public RHICommandList[] FrameCommandLists { get; private set; } = null!;

        /// <summary>帧内飞行数量。</summary>
        public int FramesInFlight => 2;

        // 描述符堆
        public RHIDescriptorHeap? CbvSrvUavHeap { get; private set; }
        public RHIDescriptorHeap? SamplerHeap { get; private set; }
        public RHIDescriptorHeap? RtvHeap { get; private set; }
        public RHIDescriptorHeap? DsvHeap { get; private set; }

        private int _frameIndex;
        private EngineConfig? _config;

        public void SetConfig(EngineConfig config) => _config = config;

        public override bool Initialize()
        {
            var config = _config ?? new EngineConfig();
            var sw = Stopwatch.StartNew();

            Diagnostics.Log.Info("创建 RHI 设备...", Diagnostics.LogCategory.Core);
            Device = RHIDeviceFactory.Create(
                config.Backend,
                enableDebug: config.EnableDebugLayer,
                enableGpuBasedValidation: config.EnableGPUValidation);
            Diagnostics.Log.Info($"  适配器: {Device.AdapterName}", Diagnostics.LogCategory.Core);
            Diagnostics.Log.Info($"  显存: {Device.DedicatedVideoMemory / (1024 * 1024 * 1024):F1} GB", Diagnostics.LogCategory.Core);

            GraphicsQueue = Device.GetCommandQueue(RHIQueueType.Graphics);

            // 交换链
            if (config.OutputWindow != IntPtr.Zero)
            {
                Diagnostics.Log.Info("创建交换链...", Diagnostics.LogCategory.Core);
                SwapChain = Device.CreateSwapChain(
                    config.OutputWindow,
                    config.WindowWidth,
                    config.WindowHeight,
                    RHIFormat.R8G8B8A8_UNorm,
                    FramesInFlight + 1,
                    config.VSync);
            }

            Diagnostics.Log.Info("创建帧同步...", Diagnostics.LogCategory.Core);
            FrameFence = Device.CreateFence(0);
            FrameCommandLists = new RHICommandList[FramesInFlight];
            for (int i = 0; i < FramesInFlight; i++)
                FrameCommandLists[i] = Device.CreateCommandList(RHIQueueType.Graphics, $"FrameCL_{i}");

            Diagnostics.Log.Info("创建描述符堆...", Diagnostics.LogCategory.Core);
            CbvSrvUavHeap = Device.CreateDescriptorHeap(RHIDescriptorHeapType.CBV_SRV_UAV, 65536, true);
            SamplerHeap = Device.CreateDescriptorHeap(RHIDescriptorHeapType.Sampler, 2048, true);
            RtvHeap = Device.CreateDescriptorHeap(RHIDescriptorHeapType.RTV, 256, false);
            DsvHeap = Device.CreateDescriptorHeap(RHIDescriptorHeapType.DSV, 128, false);

            sw.Stop();
            Diagnostics.Log.Info($"RHI 模块初始化完成 ({sw.Elapsed.TotalMilliseconds:F0}ms)", Diagnostics.LogCategory.Core);
            return true;
        }

        public override void Shutdown()
        {
            Diagnostics.Log.Info("关闭 RHI 模块...", Diagnostics.LogCategory.Core);
            DsvHeap?.Dispose();
            RtvHeap?.Dispose();
            SamplerHeap?.Dispose();
            CbvSrvUavHeap?.Dispose();
            if (FrameCommandLists != null)
                foreach (var cl in FrameCommandLists) cl?.Dispose();
            FrameFence?.Dispose();
            SwapChain?.Dispose();
            GraphicsQueue?.Dispose();
            Device?.Dispose();
        }

        public int AdvanceFrameIndex()
        {
            _frameIndex = (_frameIndex + 1) % FramesInFlight;
            return _frameIndex;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  ECSModule — 实体组件系统
    // ════════════════════════════════════════════════════════════════

    [EngineModule("ECS", AutoRegister = false)]
    public sealed class ECSModule : EngineModuleBase
    {
        public override string Name => "ECS";
        public override int Priority => -30;
        public override string[] Dependencies => new[] { "Core" };

        public EntityComponentSystem ECS { get; private set; } = null!;

        public override bool Initialize()
        {
            Diagnostics.Log.Info("初始化 ECS...", Diagnostics.LogCategory.Core);
            ECS = new EntityComponentSystem();
            return true;
        }

        public override void Shutdown() => ECS?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  AssetModule — 资源数据库
    // ════════════════════════════════════════════════════════════════

    [EngineModule("AssetDatabase", AutoRegister = false)]
    public sealed class AssetModule : EngineModuleBase
    {
        public override string Name => "AssetDatabase";
        public override int Priority => -25;
        public override string[] Dependencies => new[] { "Core", "Config", "VirtualFileSystem" };

        public AssetDatabase Assets { get; private set; } = null!;
        private string _assetRoot = "Assets/";

        public void SetAssetRoot(string path) => _assetRoot = path;

        public override bool Initialize()
        {
            Diagnostics.Log.Info("初始化资源系统...", Diagnostics.LogCategory.Core);
            Assets = new AssetDatabase(_assetRoot);
            return true;
        }

        public override void Shutdown() => Assets?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  PhysicsModule — 物理引擎（统一 PhysicsWorldReal）
    // ════════════════════════════════════════════════════════════════

    [EngineModule("Physics", AutoRegister = false)]
    public sealed class PhysicsModule : EngineModuleBase
    {
        public override string Name => "Physics";
        public override int Priority => -20;
        public override string[] Dependencies => new[] { "Core", "ECS" };

        public IPhysicsWorld PhysicsWorld { get; private set; } = null!;

        public override bool Initialize()
        {
            Diagnostics.Log.Info("初始化物理引擎 (PhysicsWorldReal)...", Diagnostics.LogCategory.Core);
            PhysicsWorld = new PhysicsWorldReal(YamaEngine.Physics.PhysicsWorldConfig.Default);
            return true;
        }

        public override void Shutdown() => PhysicsWorld?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  AnimationModule — 动画系统
    // ════════════════════════════════════════════════════════════════

    [EngineModule("Animation", AutoRegister = false)]
    public sealed class AnimationModule : EngineModuleBase
    {
        public override string Name => "Animation";
        public override int Priority => -15;
        public override string[] Dependencies => new[] { "Core", "RHI" };

        public AnimationSystem Animation { get; private set; } = null!;

        public override bool Initialize()
        {
            Diagnostics.Log.Info("初始化动画系统...", Diagnostics.LogCategory.Core);
            Animation = new AnimationSystem();
            var rhiMod = ModuleManager.Get<RHIModule>();
            if (rhiMod != null)
                Animation.Initialize(rhiMod.Device, rhiMod.GraphicsQueue);
            return true;
        }

        public override void Shutdown() => Animation?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  ScriptModule — 脚本引擎（Roslyn）
    // ════════════════════════════════════════════════════════════════

    [EngineModule("Scripting", AutoRegister = false)]
    public sealed class ScriptModule : EngineModuleBase
    {
        public override string Name => "Scripting";
        public override int Priority => -10;
        public override string[] Dependencies => new[] { "Core", "ECS" };

        public ScriptEngine Scripts { get; private set; } = null!;
        private string _scriptPath = "Scripts/";

        public void SetScriptPath(string path) => _scriptPath = path;

        public override bool Initialize()
        {
            Diagnostics.Log.Info("初始化脚本引擎 (Roslyn)...", Diagnostics.LogCategory.Core);
            var ecsMod = ModuleManager.Get<ECSModule>();
            Scripts = new ScriptEngine(ecsMod?.ECS ?? new EntityComponentSystem(), new EngineSystems());
            Scripts.CompileDirectory(_scriptPath);
            return true;
        }

        public override void Shutdown() => Scripts?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  RenderModule — 渲染管线（阴影、GI、超分、后处理、渲染器）
    // ════════════════════════════════════════════════════════════════

    [EngineModule("Rendering", AutoRegister = false)]
    public sealed class RenderModule : EngineModuleBase
    {
        public override string Name => "Rendering";
        public override int Priority => 0;
        public override string[] Dependencies => new[] { "Core", "RHI" };

        // 子系统
        public ShadowSystem Shadows { get; private set; } = null!;
        public MicroClusterVirtualGeometry? MicroCluster { get; private set; }
        public ProbeFieldGlobalIllumination? ProbeField { get; private set; }
        public TemporalSuperResolution? TSR { get; private set; }
        public PathTracer? PathTracer { get; private set; }
        public PostProcessingStack? PostProcess { get; private set; }

        /// <summary>主渲染器（Forward 或 Deferred）。</summary>
        public dynamic Renderer { get; private set; } = null!;

        public GPUParticleSystem Particles { get; private set; } = null!;

        private EngineConfig? _config;

        public void SetConfig(EngineConfig config) => _config = config;

        public override bool Initialize()
        {
            var config = _config ?? new EngineConfig();
            var rhiMod = ModuleManager.Get<RHIModule>();
            if (rhiMod == null)
            {
                Diagnostics.Log.Error("RenderModule 需要 RHIModule");
                return false;
            }
            var device = rhiMod.Device;
            var queue = rhiMod.GraphicsQueue;

            Diagnostics.Log.Info("初始化渲染子系统...", Diagnostics.LogCategory.Core);

            // 阴影
            Shadows = new ShadowSystem();
            Shadows.EnsureResources(device);

            // MicroCluster
            if (config.EnableMicroCluster)
            {
                MicroCluster = new MicroClusterVirtualGeometry();
                MicroCluster.Initialize(device, queue, config.MicroClusterConfig);
                Diagnostics.Log.Info("  → MicroCluster 虚拟几何体: 已启用", Diagnostics.LogCategory.Core);
            }

            // ProbeField
            if (config.EnableProbeField)
            {
                ProbeField = new ProbeFieldGlobalIllumination(device);
                ProbeField.Initialize();
                Diagnostics.Log.Info("  → ProbeField 实时全局光照: 已启用", Diagnostics.LogCategory.Core);
            }

            // TSR
            TSR = new TemporalSuperResolution(device);
            TSR.Initialize(config.WindowWidth, config.WindowHeight);

            // 路径追踪
            if (config.EnablePathTracing && device.SupportsRayTracing)
            {
                PathTracer = new PathTracer(device);
                PathTracer.Initialize();
                Diagnostics.Log.Info("  → 路径追踪器: 已启用", Diagnostics.LogCategory.Core);
            }

            // 后处理
            PostProcess = new PostProcessingStack(device);
            PostProcess.Initialize(config.WindowWidth, config.WindowHeight);

            // 主渲染器
            if (config.RendererType == RendererType.Deferred)
            {
                Renderer = new HybridLightingRenderer(device);
                Diagnostics.Log.Info("  → 渲染器: 延迟渲染 (Deferred)", Diagnostics.LogCategory.Core);
            }
            else
            {
                Renderer = new ForwardRenderer(device);
                Diagnostics.Log.Info("  → 渲染器: 前向渲染 (Forward)", Diagnostics.LogCategory.Core);
            }

            var rendererConfig = new RendererConfig
            {
                Width = config.WindowWidth,
                Height = config.WindowHeight,
                MicroCluster = MicroCluster,
                ProbeField = ProbeField,
                TSR = TSR,
                PathTracer = PathTracer,
                PostProcess = PostProcess,
                Shadows = Shadows
            };
            Renderer.Initialize(device, rendererConfig);

            // GPU 粒子
            Diagnostics.Log.Info("初始化 GPU 粒子系统...", Diagnostics.LogCategory.Core);
            Particles = new GPUParticleSystem(device);
            Particles.Initialize(config.MaxParticles);

            return true;
        }

        public override void Shutdown()
        {
            Particles?.Dispose();
            PostProcess?.Dispose();
            PathTracer?.Dispose();
            TSR?.Dispose();
            ProbeField?.Dispose();
            MicroCluster?.Dispose();
            Shadows?.Dispose();
            Renderer?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  WorldModule — 世界流式、导航、音频
    // ════════════════════════════════════════════════════════════════

    [EngineModule("World", AutoRegister = false)]
    public sealed class WorldModule : EngineModuleBase
    {
        public override string Name => "World";
        public override int Priority => 10;
        public override string[] Dependencies => new[] { "Core" };

        public WorldStreamingSystem WorldStreaming { get; private set; } = null!;
        public NavigationSystem Navigation { get; private set; } = null!;
        public AdvancedAudioEngine Audio { get; private set; } = null!;

        private EngineConfig? _config;

        public void SetConfig(EngineConfig config) => _config = config;

        public override bool Initialize()
        {
            var config = _config ?? new EngineConfig();

            Diagnostics.Log.Info("初始化世界/导航/音频...", Diagnostics.LogCategory.Core);

            WorldStreaming = new WorldStreamingSystem();

            Navigation = new NavigationSystem();
            Navigation.Initialize(config.NavMeshConfig);

            Audio = new AdvancedAudioEngine();
            Audio.Initialize(config.AudioConfig);
            Audio.SetMasterVolume(config.MasterVolume);

            return true;
        }

        public override void Shutdown()
        {
            Audio?.Dispose();
            Navigation?.Dispose();
            WorldStreaming = null!;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  MaterialModule — 材质系统
    // ════════════════════════════════════════════════════════════════

    [EngineModule("MaterialEditor", AutoRegister = false)]
    public sealed class MaterialModule : EngineModuleBase
    {
        public override string Name => "MaterialEditor";
        public override int Priority => 15;
        public override string[] Dependencies => new[] { "Core", "RHI" };

        public MaterialEditorSystem MaterialSystem { get; private set; } = null!;

        public override bool Initialize()
        {
            Diagnostics.Log.Info("初始化材质系统...", Diagnostics.LogCategory.Core);
            var rhiMod = ModuleManager.Get<RHIModule>();
            if (rhiMod != null)
                MaterialSystem = new MaterialEditorSystem(rhiMod.Device);
            return true;
        }

        public override void Shutdown() => MaterialSystem?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  UIModule — 运行时 UI
    // ════════════════════════════════════════════════════════════════

    [EngineModule("UI", AutoRegister = false)]
    public sealed class UIModule : EngineModuleBase
    {
        public override string Name => "UI";
        public override int Priority => 20;
        public override string[] Dependencies => new[] { "Core", "RHI" };

        public UmgRoot? UI { get; private set; }
        private bool _isEditorMode = true;

        public void SetIsEditorMode(bool isEditor) => _isEditorMode = isEditor;

        public override bool Initialize()
        {
            if (_isEditorMode)
            {
                Diagnostics.Log.Info("编辑器模式：跳过运行时 UI 初始化", Diagnostics.LogCategory.Core);
                return true;
            }

            Diagnostics.Log.Info("初始化 UI 系统...", Diagnostics.LogCategory.Core);
            var rhiMod = ModuleManager.Get<RHIModule>();
            if (rhiMod != null)
            {
                UI = new UmgRoot();
                UI.Initialize(rhiMod.Device, 1920, 1080);
            }
            return true;
        }

        public override void Shutdown() => UI?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  InputModule — 输入系统
    // ════════════════════════════════════════════════════════════════

    [EngineModule("Input", AutoRegister = false)]
    public sealed class InputModule : EngineModuleBase
    {
        public override string Name => "Input";
        public override int Priority => 25;
        public override string[] Dependencies => new[] { "Core" };

        public InputSystem Input { get; private set; } = null!;

        public override bool Initialize()
        {
            Diagnostics.Log.Info("初始化输入系统...", Diagnostics.LogCategory.Core);
            Input = new InputSystem();
            return true;
        }

        public override void Shutdown() => Input = null!;
    }

    // ════════════════════════════════════════════════════════════════
    //  NetworkModule — 网络复制
    // ════════════════════════════════════════════════════════════════

    [EngineModule("Networking", AutoRegister = false)]
    public sealed class NetworkModule : EngineModuleBase
    {
        public override string Name => "Networking";
        public override int Priority => 30;
        public override string[] Dependencies => new[] { "Core" };

        public NetworkReplicationSystem? Network { get; private set; }
        private bool _enabled;
        private NetworkConfig _networkConfig = new();

        public void Enable(NetworkConfig config) { _enabled = true; _networkConfig = config; }

        public override bool Initialize()
        {
            if (!_enabled)
            {
                Diagnostics.Log.Info("网络复制: 未启用", Diagnostics.LogCategory.Core);
                return true;
            }

            Diagnostics.Log.Info("初始化网络复制...", Diagnostics.LogCategory.Core);
            Network = new NetworkReplicationSystem();
            Network.Initialize(_networkConfig);
            return true;
        }

        public override void Shutdown() => Network?.Dispose();
    }
}
