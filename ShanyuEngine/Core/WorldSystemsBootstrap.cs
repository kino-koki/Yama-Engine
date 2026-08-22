// ============================================================================
// ShanyuEngine - 山雨游戏引擎
// 引擎世界系统引导模块
// WorldSystemsBootstrap.cs
// 引擎启动时创建和初始化所有世界级子系统，管理生命周期和依赖关系
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Rendering;
using ShanyuEngine.Audio;
using ShanyuEngine.Input;
using ShanyuEngine.MicroCluster;

namespace ShanyuEngine.Core
{
    // ========================================================================
    // GraphicsAPI — 图形API选择
    // ========================================================================
    public enum GraphicsAPI
    {
        /// <summary>Direct3D 12 (Windows平台首选)</summary>
        D3D12,
        /// <summary>Vulkan (跨平台首选)</summary>
        Vulkan
    }

    // ========================================================================
    // PhysicsEngineType — 物理引擎选择
    // ========================================================================
    public enum PhysicsEngineType
    {
        /// <summary>山雨引擎内置物理引擎</summary>
        Internal,
        /// <summary>NVIDIA PhysX</summary>
        PhysX,
        /// <summary>Jolt Physics</summary>
        Jolt
    }

    // ========================================================================
    // ScriptBackend — 脚本后端选择
    // ========================================================================
    public enum ScriptBackend
    {
        /// <summary>不启用脚本系统</summary>
        None,
        /// <summary>Lua脚本后端</summary>
        Lua,
        /// <summary>Python脚本后端</summary>
        Python,
        /// <summary>IL/.NET脚本后端 (C#热重载)</summary>
        IL
    }

    // ========================================================================
    // BootstrapConfig — 引导配置
    // 控制引擎启动时各子系统的行为和参数
    // ========================================================================
    public class BootstrapConfig
    {
        // --------------------------------------------------------------------
        // 图形配置
        // --------------------------------------------------------------------
        /// <summary>图形API选择</summary>
        public GraphicsAPI GraphicsAPI { get; set; } = GraphicsAPI.D3D12;

        /// <summary>窗口宽度 (像素)</summary>
        public int WindowWidth { get; set; } = 1920;

        /// <summary>窗口高度 (像素)</summary>
        public int WindowHeight { get; set; } = 1080;

        /// <summary>是否全屏</summary>
        public bool Fullscreen { get; set; } = false;

        /// <summary>最大纹理内存 (MB)</summary>
        public ulong MaxTextureMemory { get; set; } = 2048;

        /// <summary>最大GPU缓冲内存 (MB)</summary>
        public ulong MaxBufferMemory { get; set; } = 1024;

        /// <summary>垂直同步模式 (0=关闭, 1=半垂直同步, 2=垂直同步)</summary>
        public int VSyncMode { get; set; } = 0;

        // --------------------------------------------------------------------
        // MicroCluster虚拟几何配置
        // --------------------------------------------------------------------
        /// <summary>是否启用MicroCluster虚拟几何系统</summary>
        public bool EnableMicroCluster { get; set; } = true;

        /// <summary>MicroCluster集群最大三角形数</summary>
        public int MicroClusterClusterMaxTriangles { get; set; } = 128;

        /// <summary>MicroCluster可见性缓冲区大小 (MB)</summary>
        public int MicroClusterVisibilityBufferSize { get; set; } = 64;

        // --------------------------------------------------------------------
        // ProbeField全局光照配置
        // --------------------------------------------------------------------
        /// <summary>是否启用ProbeField全局光照系统</summary>
        public bool EnableProbeField { get; set; } = true;

        /// <summary>ProbeField表面缓存分辨率倍数</summary>
        public float ProbeFieldSurfaceCacheScale { get; set; } = 0.5f;

        /// <summary>ProbeField光线追踪最大弹射次数</summary>
        public int ProbeFieldMaxBounces { get; set; } = 4;

        // --------------------------------------------------------------------
        // 音频配置
        // --------------------------------------------------------------------
        /// <summary>是否启用音频系统</summary>
        public bool EnableAudio { get; set; } = true;

        /// <summary>音频采样率</summary>
        public int AudioSampleRate { get; set; } = 48000;

        /// <summary>音频缓冲区大小 (帧数)</summary>
        public int AudioBufferSize { get; set; } = 1024;

        // --------------------------------------------------------------------
        // 物理配置
        // --------------------------------------------------------------------
        /// <summary>物理引擎类型</summary>
        public PhysicsEngineType PhysicsEngine { get; set; } = PhysicsEngineType.Internal;

        /// <summary>物理模拟固定步长 (秒)</summary>
        public float PhysicsFixedTimestep { get; set; } = 1.0f / 60.0f;

        /// <summary>物理子步进最大次数</summary>
        public int PhysicsMaxSubSteps { get; set; } = 4;

        // --------------------------------------------------------------------
        // 脚本配置
        // --------------------------------------------------------------------
        /// <summary>脚本后端类型</summary>
        public ScriptBackend ScriptBackend { get; set; } = ScriptBackend.None;

        /// <summary>脚本热重载是否启用</summary>
        public bool ScriptHotReload { get; set; } = true;

        // --------------------------------------------------------------------
        // 高级配置
        // --------------------------------------------------------------------
        /// <summary>是否启用自适应画质</summary>
        public bool EnableAdaptiveQuality { get; set; } = true;

        /// <summary>是否启用神经超分辨率</summary>
        public bool EnableNeuralSuperResolution { get; set; } = false;

        /// <summary>超分辨率质量模式 (0=质量优先, 1=平衡, 2=性能优先)</summary>
        public int SuperResolutionQuality { get; set; } = 1;

        /// <summary>是否启用调试层</summary>
        public bool EnableDebugLayer { get; set; } = false;

        /// <summary>GPU验证层级别</summary>
        public int GPUValidationLevel { get; set; } = 0;

        /// <summary>渲染线程模式 (0=单线程, 1=多线程, 2=自动)</summary>
        public int RenderThreadMode { get; set; } = 2;

        /// <summary>应用程序名称 (用于GPU调试标记)</summary>
        public string ApplicationName { get; set; } = "ShanyuEngine Application";
    }

    // ========================================================================
    // SubsystemInfo — 子系统初始化信息
    // ========================================================================
    public struct SubsystemInfo
    {
        /// <summary>子系统名称</summary>
        public string Name;

        /// <summary>子系统类型</summary>
        public string TypeName;

        /// <summary>是否成功初始化</summary>
        public bool IsInitialized;

        /// <summary>初始化耗时</summary>
        public TimeSpan InitDuration;

        /// <summary>估计内存占用 (字节)</summary>
        public long EstimatedMemoryUsage;

        /// <summary>错误信息 (初始化失败时)</summary>
        public string ErrorMessage;

        public override string ToString()
        {
            var status = IsInitialized ? "OK" : "FAILED";
            var memStr = EstimatedMemoryUsage > 0 ? FormatMemory(EstimatedMemoryUsage) : "N/A";
            return $"[{status}] {Name} ({TypeName}) - {InitDuration.TotalMilliseconds:F2}ms, Mem: {memStr}";
        }

        private static string FormatMemory(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    // ========================================================================
    // SubsystemDependency — 子系统依赖描述
    // ========================================================================
    internal struct SubsystemDependency
    {
        public string SubsystemName;
        public string[] DependsOn;
        public int InitializeOrder;
    }

    // ========================================================================
    // WorldSystemsBootstrap — 引擎世界系统引导
    // 管理引擎所有世界级子系统的创建、初始化和销毁
    // ========================================================================
    public class WorldSystemsBootstrap
    {
        // --------------------------------------------------------------------
        // 单例实例
        // --------------------------------------------------------------------
        private static WorldSystemsBootstrap s_Current;

        /// <summary>
        /// 获取当前引导实例 (单例)
        /// </summary>
        public static WorldSystemsBootstrap GetCurrentBootstrap()
        {
            return s_Current;
        }

        // --------------------------------------------------------------------
        // 引导配置
        // --------------------------------------------------------------------
        private BootstrapConfig m_Config;

        /// <summary>
        /// 获取当前引导配置
        /// </summary>
        public BootstrapConfig GetConfig()
        {
            return m_Config;
        }

        // --------------------------------------------------------------------
        // 子系统实例引用
        // --------------------------------------------------------------------
        private RHIDevice m_RHIDevice;
        private SwapChain m_SwapChain;
        private RenderGraph m_RenderGraph;
        private SceneRendererReal m_SceneRenderer;
        private GPUDrivenPipeline m_GPUDrivenPipeline;
        private MicroClusterClusterizer m_MicroClusterClusterizer;
        private MicroClusterVisibility m_MicroClusterVisibility;
        private MicroClusterStreaming m_MicroClusterStreaming;
        private ProbeFieldHardwareRayTracer m_ProbeFieldHWRT;
        private ProbeFieldSurfaceCache m_ProbeFieldSurfaceCache;
        private ProbeFieldVoxelSystem m_ProbeFieldVoxel;
        private TerrainRenderer m_TerrainRenderer;
        private WorldRenderer m_WorldRenderer;
        private WorldRenderPipeline m_WorldRenderPipeline;
        private MaterialLibrary m_MaterialLibrary;
        private BuiltinMaterials m_BuiltinMaterials;
        private AdaptiveQualitySystem m_AdaptiveQuality;
        private NeuralSuperResolution m_NeuralSuperRes;
        private AudioSystem m_AudioSystem;
        private PhysicsSystem m_PhysicsSystem;
        private InputSystem m_InputSystem;
        private UISystem m_UISystem;
        private ScriptEngine m_ScriptEngine;
        private GameInstance m_GameInstance;

        // --------------------------------------------------------------------
        // 子系统状态跟踪
        // --------------------------------------------------------------------
        private readonly List<SubsystemInfo> m_SubsystemInfos = new List<SubsystemInfo>();
        private bool m_IsInitialized;

        /// <summary>是否已完成初始化</summary>
        public bool IsInitialized => m_IsInitialized;

        /// <summary>是否已启用MicroCluster</summary>
        public bool MicroClusterEnabled => m_Config?.EnableMicroCluster ?? false;

        /// <summary>是否已启用ProbeField</summary>
        public bool ProbeFieldEnabled => m_Config?.EnableProbeField ?? false;

        // --------------------------------------------------------------------
        // 启动诊断信息
        // --------------------------------------------------------------------
        private Stopwatch m_TotalInitStopwatch;

        // ====================================================================
        // 构造函数 (私有 — 通过Initialize静态方法创建)
        // ====================================================================
        private WorldSystemsBootstrap() { }

        // ====================================================================
        // 初始化 — 按正确顺序创建和初始化所有子系统
        // ====================================================================

        /// <summary>
        /// 初始化引擎所有世界级子系统
        /// </summary>
        /// <param name="config">引导配置</param>
        /// <returns>引导实例</returns>
        public static WorldSystemsBootstrap Initialize(BootstrapConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config), "引导配置不能为null");

            if (s_Current != null && s_Current.m_IsInitialized)
            {
                throw new InvalidOperationException("引擎已初始化。请先调用Shutdown()后再重新初始化。");
            }

            // 验证配置
            ValidateConfig(config);

            var bootstrap = new WorldSystemsBootstrap();
            bootstrap.m_Config = config;
            bootstrap.m_TotalInitStopwatch = Stopwatch.StartNew();
            s_Current = bootstrap;

            Debug.WriteLine("============================================================");
            Debug.WriteLine("  山雨引擎 (ShanyuEngine) — 世界系统引导启动");
            Debug.WriteLine("  GraphicsAPI: " + config.GraphicsAPI);
            Debug.WriteLine($"  Resolution:  {config.WindowWidth}x{config.WindowHeight}");
            Debug.WriteLine($"  MicroCluster:      {config.EnableMicroCluster}");
            Debug.WriteLine($"  ProbeField:       {config.EnableProbeField}");
            Debug.WriteLine($"  Audio:       {config.EnableAudio}");
            Debug.WriteLine($"  Physics:     {config.PhysicsEngine}");
            Debug.WriteLine($"  Script:      {config.ScriptBackend}");
            Debug.WriteLine("============================================================");

            try
            {
                bootstrap.ExecuteInitialization();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bootstrap] 初始化失败: {ex.Message}");
                Debug.WriteLine($"[Bootstrap] 正在执行紧急关闭...");
                bootstrap.Shutdown();
                s_Current = null;
                throw new InvalidOperationException($"引擎初始化失败: {ex.Message}", ex);
            }

            return bootstrap;
        }

        // --------------------------------------------------------------------
        // 配置验证
        // --------------------------------------------------------------------
        private static void ValidateConfig(BootstrapConfig config)
        {
            if (config.WindowWidth <= 0 || config.WindowHeight <= 0)
                throw new ArgumentException($"窗口尺寸无效: {config.WindowWidth}x{config.WindowHeight}");

            if (config.MaxTextureMemory == 0)
                throw new ArgumentException("最大纹理内存必须大于0");

            if (config.PhysicsFixedTimestep <= 0)
                throw new ArgumentException("物理步长必须大于0");

            if (config.ProbeFieldMaxBounces < 1)
                throw new ArgumentException("ProbeField最大弹射次数必须至少为1");

            if (config.AudioSampleRate != 44100 && config.AudioSampleRate != 48000 && config.AudioSampleRate != 96000)
                Debug.WriteLine($"[Bootstrap] 警告: 非标准采样率 {config.AudioSampleRate}，推荐使用 48000");
        }

        // --------------------------------------------------------------------
        // 执行初始化序列
        // --------------------------------------------------------------------
        private void ExecuteInitialization()
        {
            // 步骤 1: 创建 RHI Device
            InitSubsystem("RHIDevice", () =>
            {
                var deviceDesc = new RHIDeviceDescriptor
                {
                    API = m_Config.GraphicsAPI,
                    EnableDebugLayer = m_Config.EnableDebugLayer,
                    GPUValidationLevel = m_Config.GPUValidationLevel,
                    ApplicationName = m_Config.ApplicationName,
                    MaxTextureMemory = m_Config.MaxTextureMemory * 1024 * 1024,
                    MaxBufferMemory = m_Config.MaxBufferMemory * 1024 * 1024
                };
                m_RHIDevice = RHIDevice.Create(deviceDesc);
                Debug.WriteLine($"[Bootstrap] RHI设备创建成功: {m_RHIDevice.AdapterName}, VRAM: {m_RHIDevice.TotalVRAM / (1024 * 1024)}MB");
            });

            // 步骤 2: 创建 SwapChain
            InitSubsystem("SwapChain", () =>
            {
                m_SwapChain = new SwapChain(m_RHIDevice, m_Config.WindowWidth, m_Config.WindowHeight, m_Config.VSyncMode, m_Config.Fullscreen);
            });

            // 步骤 3: 初始化 RenderGraph
            InitSubsystem("RenderGraph", () =>
            {
                m_RenderGraph = new RenderGraph(m_RHIDevice);
                m_RenderGraph.SetBackBuffer(m_SwapChain.BackBuffer);
            });

            // 步骤 4: 初始化 SceneRenderer
            InitSubsystem("SceneRenderer", () =>
            {
                m_SceneRenderer = new SceneRendererReal(m_RHIDevice, m_RenderGraph);
                m_SceneRenderer.SetViewport(m_Config.WindowWidth, m_Config.WindowHeight);
            });

            // 步骤 5: 初始化 GPUDrivenPipeline
            InitSubsystem("GPUDrivenPipeline", () =>
            {
                m_GPUDrivenPipeline = new GPUDrivenPipeline(m_RHIDevice, m_RenderGraph);
                m_GPUDrivenPipeline.SetSceneRenderer(m_SceneRenderer);
            });

            // 步骤 6: 初始化 MicroCluster 系统 (可选)
            if (m_Config.EnableMicroCluster)
            {
                if (!m_RHIDevice.SupportsMeshShader)
                {
                    Debug.WriteLine("[Bootstrap] 警告: GPU不支持Mesh Shader，MicroCluster将被禁用");
                }
                else
                {
                    // 6a: MicroCluster Clusterizer
                    InitSubsystem("MicroCluster.Clusterizer", () =>
                    {
                        m_MicroClusterClusterizer = new MicroClusterClusterizer(m_RHIDevice);
                        m_MicroClusterClusterizer.MaxTrianglesPerCluster = m_Config.MicroClusterClusterMaxTriangles;
                    });

                    // 6b: MicroCluster Visibility
                    InitSubsystem("MicroCluster.Visibility", () =>
                    {
                        m_MicroClusterVisibility = new MicroClusterVisibility(m_RHIDevice, m_Config.MicroClusterVisibilityBufferSize * 1024 * 1024);
                        Rendering.WorldIntegrationHelper.RegisterMicroClusterWithScene(m_SceneRenderer, m_MicroClusterVisibility);
                    });

                    // 6c: MicroCluster Streaming
                    InitSubsystem("MicroCluster.Streaming", () =>
                    {
                        m_MicroClusterStreaming = new MicroClusterStreaming(m_RHIDevice, m_MicroClusterClusterizer);
                        m_MicroClusterStreaming.SetMaxStreamingBandwidth(256 * 1024 * 1024); // 256MB/s
                    });
                }
            }

            // 步骤 7: 初始化 ProbeField 系统 (可选)
            if (m_Config.EnableProbeField)
            {
                if (!m_RHIDevice.SupportsRayTracing)
                {
                    Debug.WriteLine("[Bootstrap] 警告: GPU不支持光线追踪，ProbeField将被禁用");
                }
                else
                {
                    // 7a: ProbeField Hardware Ray Tracer
                    InitSubsystem("ProbeField.HWRT", () =>
                    {
                        m_ProbeFieldHWRT = new ProbeFieldHardwareRayTracer(m_RHIDevice, m_RenderGraph);
                        m_ProbeFieldHWRT.MaxBounces = m_Config.ProbeFieldMaxBounces;
                    });

                    // 7b: ProbeField Surface Cache
                    InitSubsystem("ProbeField.SurfaceCache", () =>
                    {
                        int cacheWidth = Math.Max(1, (int)(m_Config.WindowWidth * m_Config.ProbeFieldSurfaceCacheScale));
                        int cacheHeight = Math.Max(1, (int)(m_Config.WindowHeight * m_Config.ProbeFieldSurfaceCacheScale));
                        m_ProbeFieldSurfaceCache = new ProbeFieldSurfaceCache(m_RHIDevice, cacheWidth, cacheHeight);
                    });

                    // 7c: ProbeField Voxel System
                    InitSubsystem("ProbeField.Voxel", () =>
                    {
                        m_ProbeFieldVoxel = new ProbeFieldVoxelSystem(m_RHIDevice);
                        m_ProbeFieldVoxel.SetVoxelSize(0.2f);
                    });

                    // 创建ProbeField集成并注册到场景
                    InitSubsystem("ProbeField.Integration", () =>
                    {
                        var probefieldIntegration = new ProbeFieldIntegration(m_ProbeFieldHWRT, m_ProbeFieldSurfaceCache, m_ProbeFieldVoxel);
                        Rendering.WorldIntegrationHelper.RegisterProbeFieldWithScene(m_SceneRenderer, probefieldIntegration);
                    });
                }
            }

            // 步骤 8: 初始化 TerrainRenderer
            InitSubsystem("TerrainRenderer", () =>
            {
                m_TerrainRenderer = new TerrainRenderer(m_RHIDevice, m_RenderGraph);
                Rendering.WorldIntegrationHelper.RegisterTerrainWithWorld(m_WorldRenderer, m_TerrainRenderer);
            });

            // 步骤 9: 初始化 WorldRenderer
            InitSubsystem("WorldRenderer", () =>
            {
                m_WorldRenderer = new WorldRenderer(m_RHIDevice, m_RenderGraph, m_SceneRenderer);
            });

            // 步骤 10: 初始化 WorldRenderPipeline
            InitSubsystem("WorldRenderPipeline", () =>
            {
                Rendering.WorldIntegrationHelper.CreateDefaultRenderPipeline(m_RHIDevice, out m_WorldRenderPipeline);
                m_WorldRenderPipeline.AttachToRenderGraph(m_RenderGraph);
            });

            // 步骤 11: 初始化 MaterialLibrary
            InitSubsystem("MaterialLibrary", () =>
            {
                m_MaterialLibrary = new MaterialLibrary(m_RHIDevice);
                Rendering.WorldIntegrationHelper.RegisterMaterialsToRenderPipeline(m_WorldRenderPipeline, m_MaterialLibrary);
            });

            // 步骤 12: 初始化 BuiltinMaterials
            InitSubsystem("BuiltinMaterials", () =>
            {
                m_BuiltinMaterials = new BuiltinMaterials(m_MaterialLibrary, m_RHIDevice);
                m_BuiltinMaterials.InitializeDefaultMaterials();
            });

            // 步骤 13: 初始化 AdaptiveQualitySystem
            if (m_Config.EnableAdaptiveQuality)
            {
                InitSubsystem("AdaptiveQuality", () =>
                {
                    m_AdaptiveQuality = new AdaptiveQualitySystem();
                    m_AdaptiveQuality.SetTargetFrameRate(60);
                    m_AdaptiveQuality.SetMinResolutionScale(0.5f);
                    m_AdaptiveQuality.SetMaxResolutionScale(1.0f);
                    m_AdaptiveQuality.AttachToPipeline(m_WorldRenderPipeline);
                });
            }

            // 步骤 14: 初始化 NeuralSuperResolution
            if (m_Config.EnableNeuralSuperResolution && m_RHIDevice.SupportsSuperResolution)
            {
                InitSubsystem("NeuralSuperResolution", () =>
                {
                    m_NeuralSuperRes = new NeuralSuperResolution(m_RHIDevice, m_RenderGraph);
                    m_NeuralSuperRes.SetQualityMode(m_Config.SuperResolutionQuality);
                    m_NeuralSuperRes.AttachToPipeline(m_WorldRenderPipeline);
                });
            }

            // 步骤 15: 初始化 AudioSystem
            if (m_Config.EnableAudio)
            {
                InitSubsystem("AudioSystem", () =>
                {
                    m_AudioSystem = new AudioSystem();
                    m_AudioSystem.Initialize(m_Config.AudioSampleRate, m_Config.AudioBufferSize);
                    m_AudioSystem.SetMasterVolume(1.0f);
                });
            }

            // 步骤 16: 初始化 PhysicsSystem
            InitSubsystem("PhysicsSystem", () =>
            {
                m_PhysicsSystem = new PhysicsSystem(m_Config.PhysicsEngine);
                m_PhysicsSystem.SetFixedTimestep(m_Config.PhysicsFixedTimestep);
                m_PhysicsSystem.SetMaxSubSteps(m_Config.PhysicsMaxSubSteps);
                m_PhysicsSystem.Initialize();
            });

            // 步骤 17: 初始化 InputSystem
            InitSubsystem("InputSystem", () =>
            {
                m_InputSystem = new InputSystem();
                m_InputSystem.Initialize();
            });

            // 步骤 18: 初始化 UISystem
            InitSubsystem("UISystem", () =>
            {
                m_UISystem = new UISystem(m_RHIDevice, m_RenderGraph);
                m_UISystem.SetViewport(m_Config.WindowWidth, m_Config.WindowHeight);
            });

            // 步骤 19: 初始化 ScriptEngine (可选)
            if (m_Config.ScriptBackend != ScriptBackend.None)
            {
                InitSubsystem("ScriptEngine", () =>
                {
                    m_ScriptEngine = new ScriptEngine(m_Config.ScriptBackend);
                    m_ScriptEngine.HotReloadEnabled = m_Config.ScriptHotReload;
                    m_ScriptEngine.Initialize();
                    m_ScriptEngine.BindEngineAPIs(this);
                });
            }

            // 步骤 20: 初始化 GameInstance
            InitSubsystem("GameInstance", () =>
            {
                m_GameInstance = new GameInstance(this);
                m_GameInstance.Initialize();
            });

            // 标记初始化完成
            m_IsInitialized = true;
            m_TotalInitStopwatch.Stop();

            Debug.WriteLine("============================================================");
            Debug.WriteLine($"  引擎初始化完成! 总耗时: {m_TotalInitStopwatch.Elapsed.TotalMilliseconds:F2}ms");
            Debug.WriteLine($"  成功初始化子系统: {m_SubsystemInfos.Count(si => si.IsInitialized)}/{m_SubsystemInfos.Count}");
            Debug.WriteLine("============================================================");
        }

        // --------------------------------------------------------------------
        // 子系统初始化辅助方法
        // --------------------------------------------------------------------
        private void InitSubsystem(string name, Action initAction)
        {
            var info = new SubsystemInfo
            {
                Name = name,
                TypeName = string.Empty,
                IsInitialized = false,
                InitDuration = TimeSpan.Zero,
                EstimatedMemoryUsage = 0,
                ErrorMessage = null
            };

            var sw = Stopwatch.StartNew();
            try
            {
                Debug.WriteLine($"[Bootstrap] 初始化子系统: {name}...");
                initAction();

                sw.Stop();
                info.IsInitialized = true;
                info.InitDuration = sw.Elapsed;
                info.EstimatedMemoryUsage = EstimateSubsystemMemory(name);

                Debug.WriteLine($"[Bootstrap] {name} 初始化完成 ({sw.Elapsed.TotalMilliseconds:F2}ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                info.IsInitialized = false;
                info.InitDuration = sw.Elapsed;
                info.ErrorMessage = ex.Message;

                Debug.WriteLine($"[Bootstrap] {name} 初始化失败: {ex.Message}");
                // 非关键子系统允许继续，关键子系统由调用方决定是否抛出
            }

            m_SubsystemInfos.Add(info);
        }

        // --------------------------------------------------------------------
        // 估计子系统内存占用
        // --------------------------------------------------------------------
        private long EstimateSubsystemMemory(string name)
        {
            switch (name)
            {
                case "RHIDevice":
                    return 0; // GPU内存，不由进程直接分配
                case "SwapChain":
                    return (long)m_Config.WindowWidth * m_Config.WindowHeight * 4 * 3; // 3个back buffer
                case "RenderGraph":
                    return 1024 * 1024; // 图结构本身约1MB
                case "MicroCluster.Visibility":
                    return m_Config.MicroClusterVisibilityBufferSize * 1024 * 1024;
                case "ProbeField.SurfaceCache":
                    int w = Math.Max(1, (int)(m_Config.WindowWidth * m_Config.ProbeFieldSurfaceCacheScale));
                    int h = Math.Max(1, (int)(m_Config.WindowHeight * m_Config.ProbeFieldSurfaceCacheScale));
                    return (long)w * h * 16; // 近似
                case "MaterialLibrary":
                    return 4 * 1024 * 1024; // 基础约4MB
                case "AudioSystem":
                    return m_Config.AudioBufferSize * 4 * 8; // 8个缓冲区
                case "PhysicsSystem":
                    return 8 * 1024 * 1024; // 约8MB
                default:
                    return 0;
            }
        }

        // ====================================================================
        // 关闭 — 反序关闭所有子系统
        // ====================================================================

        /// <summary>
        /// 关闭所有子系统并释放资源 (反序)
        /// </summary>
        public void Shutdown()
        {
            if (!m_IsInitialized && m_SubsystemInfos.Count == 0)
            {
                Debug.WriteLine("[Bootstrap] 引擎未初始化，无需关闭");
                return;
            }

            Debug.WriteLine("============================================================");
            Debug.WriteLine("  山雨引擎 — 世界系统引导关闭");
            Debug.WriteLine("============================================================");

            // 反序关闭
            var shutdownSequence = new Action[]
            {
                () => ShutdownRef(ref m_GameInstance,         "GameInstance"),
                () => ShutdownRef(ref m_ScriptEngine,         "ScriptEngine"),
                () => ShutdownRef(ref m_UISystem,             "UISystem"),
                () => ShutdownRef(ref m_InputSystem,          "InputSystem"),
                () => ShutdownRef(ref m_PhysicsSystem,        "PhysicsSystem"),
                () => ShutdownRef(ref m_AudioSystem,          "AudioSystem"),
                () => ShutdownRef(ref m_NeuralSuperRes,       "NeuralSuperResolution"),
                () => ShutdownRef(ref m_AdaptiveQuality,      "AdaptiveQuality"),
                () => ShutdownRef(ref m_BuiltinMaterials,     "BuiltinMaterials"),
                () => ShutdownRef(ref m_MaterialLibrary,      "MaterialLibrary"),
                () => ShutdownRef(ref m_WorldRenderPipeline,  "WorldRenderPipeline"),
                () => ShutdownRef(ref m_WorldRenderer,        "WorldRenderer"),
                () => ShutdownRef(ref m_TerrainRenderer,      "TerrainRenderer"),
                () => ShutdownRef(ref m_ProbeFieldVoxel,           "ProbeField.Voxel"),
                () => ShutdownRef(ref m_ProbeFieldSurfaceCache,    "ProbeField.SurfaceCache"),
                () => ShutdownRef(ref m_ProbeFieldHWRT,            "ProbeField.HWRT"),
                () => ShutdownRef(ref m_MicroClusterStreaming,      "MicroCluster.Streaming"),
                () => ShutdownRef(ref m_MicroClusterVisibility,     "MicroCluster.Visibility"),
                () => ShutdownRef(ref m_MicroClusterClusterizer,    "MicroCluster.Clusterizer"),
                () => ShutdownRef(ref m_GPUDrivenPipeline,    "GPUDrivenPipeline"),
                () => ShutdownRef(ref m_SceneRenderer,        "SceneRenderer"),
                () => ShutdownRef(ref m_RenderGraph,          "RenderGraph"),
                () => ShutdownRef(ref m_SwapChain,            "SwapChain"),
                () => ShutdownRef(ref m_RHIDevice,            "RHIDevice"),
            };

            foreach (var shutdown in shutdownSequence)
            {
                try
                {
                    shutdown();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Bootstrap] 关闭时出错: {ex.Message}");
                }
            }

            m_IsInitialized = false;
            m_SubsystemInfos.Clear();
            s_Current = null;

            Debug.WriteLine("[Bootstrap] 所有子系统已关闭");
        }

        private void ShutdownRef<T>(ref T obj, string name) where T : class, IDisposable
        {
            if (obj != null)
            {
                Debug.WriteLine($"[Bootstrap] 关闭子系统: {name}");
                obj.Dispose();
                obj = null;
            }
        }

        // ====================================================================
        // 依赖关系验证
        // ====================================================================

        /// <summary>
        /// 定义的子系统依赖关系
        /// </summary>
        private static readonly SubsystemDependency[] s_Dependencies = new SubsystemDependency[]
        {
            new SubsystemDependency { SubsystemName = "RHIDevice",          DependsOn = new string[] { }, Order = 1 },
            new SubsystemDependency { SubsystemName = "SwapChain",          DependsOn = new string[] { "RHIDevice" }, Order = 2 },
            new SubsystemDependency { SubsystemName = "RenderGraph",        DependsOn = new string[] { "RHIDevice", "SwapChain" }, Order = 3 },
            new SubsystemDependency { SubsystemName = "SceneRenderer",      DependsOn = new string[] { "RHIDevice", "RenderGraph" }, Order = 4 },
            new SubsystemDependency { SubsystemName = "GPUDrivenPipeline",  DependsOn = new string[] { "RHIDevice", "RenderGraph", "SceneRenderer" }, Order = 5 },
            new SubsystemDependency { SubsystemName = "MicroCluster.Clusterizer", DependsOn = new string[] { "RHIDevice" }, Order = 6 },
            new SubsystemDependency { SubsystemName = "MicroCluster.Visibility",  DependsOn = new string[] { "RHIDevice", "SceneRenderer", "MicroCluster.Clusterizer" }, Order = 7 },
            new SubsystemDependency { SubsystemName = "MicroCluster.Streaming",   DependsOn = new string[] { "RHIDevice", "MicroCluster.Clusterizer" }, Order = 8 },
            new SubsystemDependency { SubsystemName = "ProbeField.HWRT",         DependsOn = new string[] { "RHIDevice", "RenderGraph" }, Order = 6 },
            new SubsystemDependency { SubsystemName = "ProbeField.SurfaceCache", DependsOn = new string[] { "RHIDevice" }, Order = 7 },
            new SubsystemDependency { SubsystemName = "ProbeField.Voxel",        DependsOn = new string[] { "RHIDevice" }, Order = 7 },
            new SubsystemDependency { SubsystemName = "ProbeField.Integration",  DependsOn = new string[] { "SceneRenderer", "ProbeField.HWRT", "ProbeField.SurfaceCache", "ProbeField.Voxel" }, Order = 8 },
            new SubsystemDependency { SubsystemName = "TerrainRenderer",    DependsOn = new string[] { "RHIDevice", "RenderGraph" }, Order = 9 },
            new SubsystemDependency { SubsystemName = "WorldRenderer",      DependsOn = new string[] { "RHIDevice", "RenderGraph", "SceneRenderer" }, Order = 10 },
            new SubsystemDependency { SubsystemName = "WorldRenderPipeline",DependsOn = new string[] { "RHIDevice", "RenderGraph", "SceneRenderer" }, Order = 11 },
            new SubsystemDependency { SubsystemName = "MaterialLibrary",    DependsOn = new string[] { "RHIDevice" }, Order = 12 },
            new SubsystemDependency { SubsystemName = "BuiltinMaterials",   DependsOn = new string[] { "MaterialLibrary" }, Order = 13 },
            new SubsystemDependency { SubsystemName = "AdaptiveQuality",    DependsOn = new string[] { "WorldRenderPipeline" }, Order = 14 },
            new SubsystemDependency { SubsystemName = "NeuralSuperResolution", DependsOn = new string[] { "RHIDevice", "WorldRenderPipeline" }, Order = 15 },
            new SubsystemDependency { SubsystemName = "AudioSystem",        DependsOn = new string[] { }, Order = 16 },
            new SubsystemDependency { SubsystemName = "PhysicsSystem",      DependsOn = new string[] { }, Order = 17 },
            new SubsystemDependency { SubsystemName = "InputSystem",        DependsOn = new string[] { }, Order = 18 },
            new SubsystemDependency { SubsystemName = "UISystem",           DependsOn = new string[] { "RHIDevice", "RenderGraph" }, Order = 19 },
            new SubsystemDependency { SubsystemName = "ScriptEngine",       DependsOn = new string[] { }, Order = 20 },
            new SubsystemDependency { SubsystemName = "GameInstance",       DependsOn = new string[] { "WorldRenderer", "SceneRenderer" }, Order = 21 },
        };

        /// <summary>
        /// 验证子系统依赖关系 — 检查循环依赖和缺失依赖
        /// </summary>
        /// <returns>验证结果字符串，包含所有发现的问题</returns>
        public string ValidateDependencies()
        {
            var issues = new List<string>();
            var dependencyMap = new Dictionary<string, List<string>>();

            // 构建依赖图
            foreach (var dep in s_Dependencies)
            {
                dependencyMap[dep.SubsystemName] = new List<string>(dep.DependsOn);
            }

            // 检查缺失依赖
            foreach (var dep in s_Dependencies)
            {
                foreach (var required in dep.DependsOn)
                {
                    if (!dependencyMap.ContainsKey(required))
                    {
                        issues.Add($"[缺失依赖] 子系统 '{dep.SubsystemName}' 依赖的 '{required}' 未在依赖表中定义");
                    }
                }
            }

            // 检查循环依赖 (DFS)
            var visited = new HashSet<string>();
            var recStack = new HashSet<string>();
            var hasCycle = false;

            void CheckCycle(string node, string path)
            {
                if (recStack.Contains(node))
                {
                    issues.Add($"[循环依赖] 检测到循环: {path} -> {node}");
                    hasCycle = true;
                    return;
                }
                if (visited.Contains(node))
                    return;

                visited.Add(node);
                recStack.Add(node);

                if (dependencyMap.TryGetValue(node, out var deps))
                {
                    foreach (var dep in deps)
                    {
                        CheckCycle(dep, string.IsNullOrEmpty(path) ? node : path + " -> " + node);
                    }
                }

                recStack.Remove(node);
            }

            foreach (var key in dependencyMap.Keys)
            {
                CheckCycle(key, string.Empty);
            }

            if (!hasCycle && issues.Count == 0)
            {
                return "依赖关系验证通过: 无循环依赖，无缺失依赖。";
            }

            return $"依赖关系验证发现 {issues.Count} 个问题:\n" + string.Join("\n", issues);
        }

        // ====================================================================
        // 启动诊断信息
        // ====================================================================

        /// <summary>
        /// 生成启动诊断报告
        /// </summary>
        /// <returns>格式化的诊断信息字符串</returns>
        public string StartupDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║          山雨引擎 (ShanyuEngine) 启动诊断报告              ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
            sb.AppendLine();

            // 引擎配置概要
            sb.AppendLine("【引擎配置】");
            sb.AppendLine($"  图形API:        {m_Config.GraphicsAPI}");
            sb.AppendLine($"  窗口分辨率:     {m_Config.WindowWidth} x {m_Config.WindowHeight}");
            sb.AppendLine($"  全屏:           {m_Config.Fullscreen}");
            var vsyncText = m_Config.VSyncMode == 0 ? "关闭" : m_Config.VSyncMode == 1 ? "半垂直同步" : "开启";
            sb.AppendLine($"  垂直同步:       {vsyncText}");
            sb.AppendLine($"  MicroCluster:         {(m_Config.EnableMicroCluster ? "启用" : "禁用")}");
            sb.AppendLine($"  ProbeField:          {(m_Config.EnableProbeField ? "启用" : "禁用")}");
            sb.AppendLine($"  音频:           {(m_Config.EnableAudio ? "启用" : "禁用")}");
            sb.AppendLine($"  物理引擎:       {m_Config.PhysicsEngine}");
            sb.AppendLine($"  脚本后端:       {m_Config.ScriptBackend}");
            sb.AppendLine($"  自适应画质:     {(m_Config.EnableAdaptiveQuality ? "启用" : "禁用")}");
            sb.AppendLine($"  神经超分辨率:   {(m_Config.EnableNeuralSuperResolution ? "启用" : "禁用")}");
            sb.AppendLine($"  调试层:         {(m_Config.EnableDebugLayer ? "启用" : "禁用")}");
            sb.AppendLine();

            // GPU信息
            if (m_RHIDevice != null)
            {
                sb.AppendLine("【GPU信息】");
                sb.AppendLine($"  适配器:         {m_RHIDevice.AdapterName}");
                sb.AppendLine($"  显存总量:       {m_RHIDevice.TotalVRAM / (1024 * 1024)} MB");
                sb.AppendLine($"  可用显存:       {m_RHIDevice.AvailableVRAM / (1024 * 1024)} MB");
                sb.AppendLine($"  Mesh Shader:    {(m_RHIDevice.SupportsMeshShader ? "支持" : "不支持")}");
                sb.AppendLine($"  光线追踪:       {(m_RHIDevice.SupportsRayTracing ? "支持" : "不支持")}");
                sb.AppendLine($"  超分辨率:       {(m_RHIDevice.SupportsSuperResolution ? "支持" : "不支持")}");
                sb.AppendLine();
            }

            // 子系统状态
            sb.AppendLine("【子系统状态】");
            sb.AppendLine($"  {"子系统",-30} {"状态",-8} {"耗时",-12} {"内存占用",-12}");
            sb.AppendLine($"  {new string('-', 30)} {new string('-', 8)} {new string('-', 12)} {new string('-', 12)}");

            long totalMemory = 0;
            foreach (var info in m_SubsystemInfos)
            {
                var status = info.IsInitialized ? "OK" : "FAILED";
                var duration = $"{info.InitDuration.TotalMilliseconds:F2} ms";
                var memory = info.EstimatedMemoryUsage > 0 ? FormatBytes(info.EstimatedMemoryUsage) : "-";
                sb.AppendLine($"  {info.Name,-30} {status,-8} {duration,-12} {memory,-12}");
                totalMemory += info.EstimatedMemoryUsage;
            }

            sb.AppendLine($"  {new string('-', 30)} {new string('-', 8)} {new string('-', 12)} {new string('-', 12)}");
            var successCount = m_SubsystemInfos.Count(si => si.IsInitialized);
            var failCount = m_SubsystemInfos.Count(si => !si.IsInitialized);
            var totalMs = m_SubsystemInfos.Sum(si => si.InitDuration.TotalMilliseconds);
            sb.AppendLine($"  合计 ({successCount}成功/{failCount}失败)         {totalMs,-12:F2} ms {FormatBytes(totalMemory),-12}");
            sb.AppendLine();

            // 依赖验证结果
            sb.AppendLine("【依赖验证】");
            sb.AppendLine($"  {ValidateDependencies()}");
            sb.AppendLine();

            // 性能建议
            sb.AppendLine("【性能建议】");
            if (m_Config.EnableProbeField && !m_RHIDevice?.SupportsRayTracing == true)
                sb.AppendLine("  ! ProbeField已启用但GPU不支持光线追踪，请考虑关闭ProbeField以提升性能");
            if (m_Config.EnableMicroCluster && !m_RHIDevice?.SupportsMeshShader == true)
                sb.AppendLine("  ! MicroCluster已启用但GPU不支持Mesh Shader，请考虑关闭MicroCluster");
            if (m_Config.WindowWidth > 2560 || m_Config.WindowHeight > 1440)
                sb.AppendLine("  * 高分辨率渲染建议启用超分辨率以维持帧率");
            if (m_Config.PhysicsEngine == PhysicsEngineType.PhysX)
                sb.AppendLine("  * PhysX物理引擎需要额外安装PhysX运行时库");
            if (m_Config.ScriptBackend == ScriptBackend.Python)
                sb.AppendLine("  * Python脚本后端会增加启动时间，考虑使用Lua或IL后端以获得更好性能");
            if (m_Config.EnableDebugLayer)
                sb.AppendLine("  * 调试层已启用，发布版本建议关闭以获得最佳性能");
            sb.AppendLine();

            sb.AppendLine("══════════════════════════════════════════════════════════════");

            return sb.ToString();
        }

        /// <summary>
        /// 格式化字节数为可读字符串
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        // ====================================================================
        // 子系统访问接口
        // ====================================================================

        /// <summary>获取RHI设备</summary>
        public RHIDevice GetRHIDevice() => m_RHIDevice;

        /// <summary>获取交换链</summary>
        public SwapChain GetSwapChain() => m_SwapChain;

        /// <summary>获取渲染图</summary>
        public RenderGraph GetRenderGraph() => m_RenderGraph;

        /// <summary>获取场景渲染器</summary>
        public SceneRendererReal GetSceneRenderer() => m_SceneRenderer;

        /// <summary>获取世界渲染器</summary>
        public WorldRenderer GetWorldRenderer() => m_WorldRenderer;

        /// <summary>获取世界渲染管线</summary>
        public WorldRenderPipeline GetWorldRenderPipeline() => m_WorldRenderPipeline;

        /// <summary>获取材质库</summary>
        public MaterialLibrary GetMaterialLibrary() => m_MaterialLibrary;

        /// <summary>获取地形渲染器</summary>
        public TerrainRenderer GetTerrainRenderer() => m_TerrainRenderer;

        /// <summary>获取音频系统</summary>
        public AudioSystem GetAudioSystem() => m_AudioSystem;

        /// <summary>获取物理系统</summary>
        public PhysicsSystem GetPhysicsSystem() => m_PhysicsSystem;

        /// <summary>获取输入系统</summary>
        public InputSystem GetInputSystem() => m_InputSystem;

        /// <summary>获取UI系统</summary>
        public UISystem GetUISystem() => m_UISystem;

        /// <summary>获取脚本引擎</summary>
        public ScriptEngine GetScriptEngine() => m_ScriptEngine;

        /// <summary>获取游戏实例</summary>
        public GameInstance GetGameInstance() => m_GameInstance;

        /// <summary>获取所有子系统信息</summary>
        public IReadOnlyList<SubsystemInfo> GetSubsystemInfos() => m_SubsystemInfos.AsReadOnly();
    }
}
