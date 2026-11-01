// ============================================================================
// YamaEngine - yama游戏引擎
// 世界系统集成辅助模块
// WorldIntegrationHelper.cs
// 提供世界渲染系统各模块间的胶水代码，负责子系统注册、连接与协调
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using YamaEngine.RHI;
using YamaEngine.MicroCluster;
using PixelFormat = YamaEngine.RHI.RHIFormat;

namespace YamaEngine.Rendering
{
    // ========================================================================
    // 渲染通道枚举 — 描述渲染管线中所有可用的渲染阶段
    // ========================================================================
    public enum RenderPass
    {
        DepthPrePass,          // 深度预通道
        GeometryBufferPass,           // 几何缓冲通道 (PBR基础通道)
        ShadowCascadePass,     // 级联阴影通道
        ShadowSpotPass,        // 聚光灯阴影通道
        MicroClusterRasterPass,      // MicroCluster虚拟几何光栅化通道
        MicroClusterImposterPass,    // MicroCluster替身渲染通道
        ProbeFieldTracePass,        // ProbeField光线追踪通道
        ProbeFieldSurfaceCache,     // ProbeField表面缓存更新通道
        ProbeFieldVoxelPass,        // ProbeField体素化通道
        SSRPass,               // 屏幕空间反射通道
        SSAOPass,              // 屏幕空间环境光遮蔽通道
        SSAOPassTemporal,      // 时域SSAO通道
        VolumetricFogPass,     // 体积雾通道
        AtmosphericScatter,    // 大气散射通道
        ForwardOpaquePass,     // 前向渲染-不透明通道
        ForwardTranslucentPass,// 前向渲染-半透明通道
        PostProcessTonemap,    // 后处理-色调映射
        PostProcessBloom,      // 后处理-泛光
        PostProcessDOF,        // 后处理-景深
        PostProcessMotionBlur, // 后处理-运动模糊
        PostProcessTAA,        // 后处理-时域抗锯齿
        PostProcessFXAA,       // 后处理-FXAA抗锯齿
        SuperResolutionPass,   // 超分辨率通道 (DLSS/FSR/Neural)
        UIOverlayPass,         // UI叠加通道
        DebugOverlayPass,      // 调试叠加通道
        PresentPass,           // 呈现通道
        Count,                 // 通道总数（数组维度使用）

        // ── WorldRenderPipeline / 历史调用方所需的兼容别名与新增通道 ──
        // 别名映射到已有等价通道，保证数值落在 [0, Count) 数组索引范围内；
        // LightResolve / PostProcess 为取自空闲槽位的新增通道定义。
        ZPrepass = DepthPrePass,               // 深度预通道（兼容旧名）
        ShadowMap = ShadowCascadePass,         // 级联阴影通道（兼容旧名）
        GeometryBuffer = GeometryBufferPass,   // 几何缓冲通道（兼容旧名）
        SSAO = SSAOPass,                       // 屏幕空间环境光遮蔽（兼容旧名）
        ForwardPass = ForwardOpaquePass,       // 前向不透明通道（兼容旧名）
        Translucency = ForwardTranslucentPass, // 半透明通道（兼容旧名）
        UI = UIOverlayPass,                    // UI 叠加通道（兼容旧名）
        LightResolve = 13,                     // 延迟光照解析通道（As...空闲槽位）
        PostProcess = 16,                      // 后处理合成通道（PostProcessTonemap 槽位）
    }

    // ========================================================================
    // 渲染通道依赖关系 — 描述通道间的执行顺序约束
    // ========================================================================
    [Flags]
    public enum RenderPassFlags
    {
        None                    = 0,
        RequiresDepthBuffer     = 1 << 0,
        RequiresGeometryBuffer         = 1 << 1,
        RequiresShadowMaps      = 1 << 2,
        RequiresMicroCluster          = 1 << 3,
        RequiresProbeField           = 1 << 4,
        OutputsToScreen         = 1 << 5,
        IsPostProcess           = 1 << 6,
        IsOptional              = 1 << 7,
        IsDebugOnly             = 1 << 8
    }

    // ========================================================================
    // WorldIntegrationHelper — 世界渲染系统集成辅助静态类
    // 提供模块间注册、连接与协调的胶水代码
    // ========================================================================
    public static class WorldIntegrationHelper
    {
        // --------------------------------------------------------------------
        // 渲染通道可读名称映射
        // --------------------------------------------------------------------
        private static readonly Dictionary<RenderPass, string> s_PassNames = new Dictionary<RenderPass, string>
        {
            { RenderPass.DepthPrePass,         "深度预通道 (Depth Pre-Pass)" },
            { RenderPass.GeometryBufferPass,          "几何缓冲通道 (G-Buffer)" },
            { RenderPass.ShadowCascadePass,    "级联阴影通道 (Cascade Shadows)" },
            { RenderPass.ShadowSpotPass,       "聚光灯阴影通道 (Spot Shadows)" },
            { RenderPass.MicroClusterRasterPass,     "MicroCluster光栅化通道 (MicroCluster Raster)" },
            { RenderPass.MicroClusterImposterPass,   "MicroCluster替身通道 (MicroCluster Imposter)" },
            { RenderPass.ProbeFieldTracePass,       "ProbeField光线追踪 (ProbeField Trace)" },
            { RenderPass.ProbeFieldSurfaceCache,    "ProbeField表面缓存 (ProbeField Surface Cache)" },
            { RenderPass.ProbeFieldVoxelPass,       "ProbeField体素化 (ProbeField Voxelization)" },
            { RenderPass.SSRPass,              "屏幕空间反射 (SSR)" },
            { RenderPass.SSAOPass,             "屏幕空间环境光遮蔽 (SSAO)" },
            { RenderPass.SSAOPassTemporal,     "时域SSAO (Temporal SSAO)" },
            { RenderPass.VolumetricFogPass,    "体积雾 (Volumetric Fog)" },
            { RenderPass.AtmosphericScatter,   "大气散射 (Atmospheric Scattering)" },
            { RenderPass.ForwardOpaquePass,    "前向渲染-不透明 (Forward Opaque)" },
            { RenderPass.ForwardTranslucentPass, "前向渲染-半透明 (Forward Translucent)" },
            { RenderPass.PostProcessTonemap,   "色调映射 (Tone Mapping)" },
            { RenderPass.PostProcessBloom,     "泛光 (Bloom)" },
            { RenderPass.PostProcessDOF,       "景深 (Depth of Field)" },
            { RenderPass.PostProcessMotionBlur,"运动模糊 (Motion Blur)" },
            { RenderPass.PostProcessTAA,       "时域抗锯齿 (TAA)" },
            { RenderPass.PostProcessFXAA,      "FXAA抗锯齿 (FXAA)" },
            { RenderPass.SuperResolutionPass,  "超分辨率 (Super Resolution)" },
            { RenderPass.UIOverlayPass,        "UI叠加层 (UI Overlay)" },
            { RenderPass.DebugOverlayPass,     "调试叠加层 (Debug Overlay)" },
            { RenderPass.PresentPass,          "呈现 (Present)" }
        };

        // --------------------------------------------------------------------
        // 渲染通道标志映射
        // --------------------------------------------------------------------
        private static readonly Dictionary<RenderPass, RenderPassFlags> s_PassFlags = new Dictionary<RenderPass, RenderPassFlags>
        {
            { RenderPass.DepthPrePass,         RenderPassFlags.RequiresDepthBuffer },
            { RenderPass.GeometryBufferPass,          RenderPassFlags.RequiresDepthBuffer | RenderPassFlags.RequiresMicroCluster },
            { RenderPass.ShadowCascadePass,    RenderPassFlags.RequiresDepthBuffer | RenderPassFlags.IsOptional },
            { RenderPass.ShadowSpotPass,       RenderPassFlags.RequiresDepthBuffer | RenderPassFlags.IsOptional },
            { RenderPass.MicroClusterRasterPass,     RenderPassFlags.RequiresMicroCluster | RenderPassFlags.RequiresDepthBuffer },
            { RenderPass.MicroClusterImposterPass,   RenderPassFlags.RequiresMicroCluster | RenderPassFlags.IsOptional },
            { RenderPass.ProbeFieldTracePass,       RenderPassFlags.RequiresProbeField | RenderPassFlags.RequiresGeometryBuffer },
            { RenderPass.ProbeFieldSurfaceCache,    RenderPassFlags.RequiresProbeField | RenderPassFlags.RequiresGeometryBuffer | RenderPassFlags.IsOptional },
            { RenderPass.ProbeFieldVoxelPass,       RenderPassFlags.RequiresProbeField | RenderPassFlags.IsOptional },
            { RenderPass.SSRPass,              RenderPassFlags.RequiresGeometryBuffer | RenderPassFlags.IsOptional },
            { RenderPass.SSAOPass,             RenderPassFlags.RequiresDepthBuffer | RenderPassFlags.RequiresGeometryBuffer },
            { RenderPass.SSAOPassTemporal,     RenderPassFlags.RequiresDepthBuffer | RenderPassFlags.RequiresGeometryBuffer | RenderPassFlags.IsOptional },
            { RenderPass.VolumetricFogPass,    RenderPassFlags.RequiresDepthBuffer | RenderPassFlags.RequiresShadowMaps | RenderPassFlags.IsOptional },
            { RenderPass.AtmosphericScatter,   RenderPassFlags.RequiresDepthBuffer | RenderPassFlags.IsOptional },
            { RenderPass.ForwardOpaquePass,    RenderPassFlags.RequiresDepthBuffer },
            { RenderPass.ForwardTranslucentPass, RenderPassFlags.RequiresDepthBuffer },
            { RenderPass.PostProcessTonemap,   RenderPassFlags.IsPostProcess | RenderPassFlags.OutputsToScreen },
            { RenderPass.PostProcessBloom,     RenderPassFlags.IsPostProcess | RenderPassFlags.IsOptional },
            { RenderPass.PostProcessDOF,       RenderPassFlags.IsPostProcess | RenderPassFlags.IsOptional },
            { RenderPass.PostProcessMotionBlur, RenderPassFlags.IsPostProcess | RenderPassFlags.IsOptional },
            { RenderPass.PostProcessTAA,       RenderPassFlags.IsPostProcess },
            { RenderPass.PostProcessFXAA,      RenderPassFlags.IsPostProcess | RenderPassFlags.IsOptional },
            { RenderPass.SuperResolutionPass,  RenderPassFlags.IsPostProcess | RenderPassFlags.OutputsToScreen | RenderPassFlags.IsOptional },
            { RenderPass.UIOverlayPass,        RenderPassFlags.OutputsToScreen },
            { RenderPass.DebugOverlayPass,     RenderPassFlags.OutputsToScreen | RenderPassFlags.IsDebugOnly },
            { RenderPass.PresentPass,          RenderPassFlags.OutputsToScreen }
        };

        /// <summary>
        /// 获取所有渲染通道的可读名称映射
        /// </summary>
        public static Dictionary<RenderPass, string> GetRenderPassNames()
        {
            return new Dictionary<RenderPass, string>(s_PassNames);
        }

        /// <summary>
        /// 获取指定渲染通道的可读名称
        /// </summary>
        public static string GetRenderPassName(RenderPass pass)
        {
            return s_PassNames.TryGetValue(pass, out var name) ? name : pass.ToString();
        }

        /// <summary>
        /// 获取指定渲染通道的标志
        /// </summary>
        public static RenderPassFlags GetRenderPassFlags(RenderPass pass)
        {
            return s_PassFlags.TryGetValue(pass, out var flags) ? flags : RenderPassFlags.None;
        }

        // ====================================================================
        // MicroCluster系统注册
        // ====================================================================

        /// <summary>
        /// 将MicroCluster可见性系统注册到场景渲染器
        /// </summary>
        /// <param name="scene">目标场景渲染器</param>
        /// <param name="microclusterVis">MicroCluster可见性系统实例</param>
        public static void RegisterMicroClusterWithScene(SceneRendererReal scene, MicroClusterVisibility microclusterVis)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene), "场景渲染器不能为null");
            if (microclusterVis == null)
                throw new ArgumentNullException(nameof(microclusterVis), "MicroCluster可见性系统不能为null");

            Debug.WriteLine($"[WorldIntegration] 注册MicroCluster可见性系统到场景渲染器 (Scene={scene.SceneName})");

            // 注册MicroCluster可见性查询回调到场景的剔除阶段
            scene.RegisterCullProvider("MicroClusterVisibility", (viewInfo, cullResult) =>
            {
                microclusterVis.UpdateVisibility(viewInfo.ViewMatrix, viewInfo.ProjectionMatrix, viewInfo.ViewFrustum);
                microclusterVis.PopulateCullResult(cullResult);
            });

            // 注册MicroCluster渲染回调
            scene.RegisterRenderProvider("MicroClusterRaster", (commandList, renderView) =>
            {
                microclusterVis.RenderClusters(commandList, renderView);
            });

            // 注册MicroCluster数据流回调 — 用于LOD资源的流式加载/卸载
            scene.RegisterStreamingHandler("MicroClusterStreaming", (streamingRequest) =>
            {
                microclusterVis.HandleStreamingRequest(streamingRequest);
            });

            // 将MicroCluster的GPU缓冲区注册到场景的资源管理器中
            scene.ResourceManager.RegisterExternalBuffer("MicroClusterClusterBuffer", microclusterVis.ClusterBuffer);
            scene.ResourceManager.RegisterExternalBuffer("MicroClusterVisibleClusterSWHW", microclusterVis.VisibleClusterBuffer);
            scene.ResourceManager.RegisterExternalBuffer("MicroClusterPageIndexBuffer", microclusterVis.PageIndexBuffer);

            // 设置MicroCluster与场景的帧同步
            scene.OnSceneBeginFrame += () => microclusterVis.BeginFrame();
            scene.OnSceneEndFrame += () => microclusterVis.EndFrame();

            microclusterVis.IsRegistered = true;
            Debug.WriteLine($"[WorldIntegration] MicroCluster可见性系统注册完成 (Clusters: {microclusterVis.TotalClusterCount}, Pages: {microclusterVis.TotalPageCount})");
        }

        // ====================================================================
        // ProbeField系统注册
        // ====================================================================

        /// <summary>
        /// 将ProbeField全局光照系统注册到场景渲染器
        /// </summary>
        /// <param name="scene">目标场景渲染器</param>
        /// <param name="probefield">ProbeField集成系统实例</param>
        public static void RegisterProbeFieldWithScene(SceneRendererReal scene, ProbeFieldIntegration probefield)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene), "场景渲染器不能为null");
            if (probefield == null)
                throw new ArgumentNullException(nameof(probefield), "ProbeField全局光照系统不能为null");

            Debug.WriteLine($"[WorldIntegration] 注册ProbeField全局光照系统到场景渲染器 (Scene={scene.SceneName})");

            // 注册ProbeField表面缓存更新回调 — 每帧更新ProbeField表面缓存
            scene.RegisterRenderProvider("ProbeFieldSurfaceCacheUpdate", (commandList, renderView) =>
            {
                probefield.SurfaceCache.Update(commandList, renderView.GeometryBufferResources, renderView.DepthBuffer);
            });

            // 注册ProbeField硬件光线追踪回调
            scene.RegisterRenderProvider("ProbeFieldHWRayTrace", (commandList, renderView) =>
            {
                probefield.HardwareRayTracer.TraceIndirectLighting(
                    commandList,
                    renderView.ViewMatrix,
                    renderView.ProjectionMatrix,
                    probefield.SurfaceCache,
                    renderView.GeometryBufferResources
                );
            });

            // 注册ProbeField体素可视化调试回调
            scene.RegisterRenderProvider("ProbeFieldVoxelDebug", (commandList, renderView) =>
            {
                if (probefield.VoxelSystem.EnableDebugVisualization)
                {
                    probefield.VoxelSystem.RenderVoxels(commandList, renderView);
                }
            });

            // 注册ProbeField场景变更通知 — 当场景中物体移动时通知ProbeField更新
            scene.OnPrimitiveTransformChanged += (primitiveId, newTransform) =>
            {
                probefield.MarkPrimitiveDirty(primitiveId, newTransform);
            };

            scene.OnPrimitiveAdded += (primitive) =>
            {
                if (primitive.ProbeFieldAffected)
                {
                    probefield.RegisterPrimitive(primitive);
                }
            };

            scene.OnPrimitiveRemoved += (primitiveId) =>
            {
                probefield.UnregisterPrimitive(primitiveId);
            };

            // 注册ProbeField的GPU资源
            scene.ResourceManager.RegisterExternalBuffer("ProbeFieldCardBuffer", probefield.SurfaceCache.CardBuffer);
            scene.ResourceManager.RegisterExternalBuffer("ProbeFieldCardPageBuffer", probefield.SurfaceCache.PageBuffer);
            scene.ResourceManager.RegisterExternalTexture("ProbeFieldBrdfTexture", probefield.BrdfLut);

            probefield.IsRegistered = true;
            Debug.WriteLine($"[WorldIntegration] ProbeField全局光照系统注册完成 (Mode={probefield.TraceMode}, CardCount={probefield.SurfaceCache.CardCount})");
        }

        // ====================================================================
        // 地形系统注册
        // ====================================================================

        /// <summary>
        /// 将地形渲染器注册到世界渲染器
        /// </summary>
        /// <param name="world">目标世界渲染器</param>
        /// <param name="terrain">地形渲染器实例</param>
        public static void RegisterTerrainWithWorld(WorldRenderer world, TerrainRenderer terrain)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world), "世界渲染器不能为null");
            if (terrain == null)
                throw new ArgumentNullException(nameof(terrain), "地形渲染器不能为null");

            Debug.WriteLine($"[WorldIntegration] 注册地形渲染器到世界渲染器 (World={world.WorldName})");

            // 将地形作为特殊场景节点注册到世界渲染器
            world.RegisterSpecialNode("TerrainRoot", terrain.TerrainRootNode);

            // 注册地形LOD切换回调
            world.OnViewChanged += (viewInfo) =>
            {
                terrain.UpdateLOD(viewInfo.CameraPosition, viewInfo.ViewFrustum);
            };

            // 注册地形裁剪回调 — 世界渲染器在执行剔除时调用
            world.RegisterCullProvider("TerrainCulling", (viewInfo, cullResult) =>
            {
                terrain.CullTerrainPatches(viewInfo.ViewFrustum, cullResult);
            });

            // 注册地形渲染回调 — 在GeometryBuffer通道中渲染地形
            world.RegisterRenderProvider("TerrainGeometryBuffer", (commandList, renderView) =>
            {
                terrain.RenderGeometryBuffer(commandList, renderView);
            });

            // 注册地形阴影渲染回调
            world.RegisterRenderProvider("TerrainShadow", (commandList, shadowView) =>
            {
                terrain.RenderShadow(commandList, shadowView);
            });

            // 地形与物理系统的碰撞数据共享
            terrain.OnHeightmapUpdated += (heightmapData) =>
            {
                world.NotifyPhysicsHeightmapChanged(heightmapData);
            };

            terrain.IsRegistered = true;
            Debug.WriteLine($"[WorldIntegration] 地形渲染器注册完成 (Patches: {terrain.VisiblePatchCount}, ChunkSize: {terrain.ChunkSize}, LODs: {terrain.MaxLODLevel})");
        }

        // ====================================================================
        // 材质库与渲染管线连接
        // ====================================================================

        /// <summary>
        /// 将材质库连接到世界渲染管线
        /// </summary>
        /// <param name="pipeline">目标渲染管线</param>
        /// <param name="library">材质库实例</param>
        public static void RegisterMaterialsToRenderPipeline(WorldRenderPipeline pipeline, MaterialLibrary library)
        {
            if (pipeline == null)
                throw new ArgumentNullException(nameof(pipeline), "渲染管线不能为null");
            if (library == null)
                throw new ArgumentNullException(nameof(library), "材质库不能为null");

            Debug.WriteLine("[WorldIntegration] 将材质库连接到渲染管线");

            // 注册材质编译完成回调 — 材质编译后自动更新管线
            library.OnMaterialCompiled += (materialId, shaderCode) =>
            {
                pipeline.UpdateMaterialShader(materialId, shaderCode);
            };

            // 注册材质参数变更回调
            library.OnMaterialParameterChanged += (materialId, parameterName, value) =>
            {
                pipeline.UpdateMaterialParameter(materialId, parameterName, value);
            };

            // 注册材质纹理绑定变更回调
            library.OnMaterialTextureChanged += (materialId, slot, texture) =>
            {
                pipeline.UpdateMaterialTexture(materialId, slot, texture);
            };

            // 将材质库中所有已编译材质同步到管线
            var materialCount = 0;
            foreach (var material in library.GetAllMaterials())
            {
                if (material.IsCompiled)
                {
                    pipeline.RegisterMaterial(material.MaterialId, material.GetShaderBytecode(), material.GetParameterLayout());
                    materialCount++;
                }
            }

            // 注册材质热重载回调 — 运行时材质文件修改时自动重编译
            library.OnMaterialHotReload += (materialId) =>
            {
                var material = library.GetMaterial(materialId);
                if (material != null && material.Recompile())
                {
                    pipeline.UpdateMaterialShader(materialId, material.GetShaderBytecode());
                    Debug.WriteLine($"[WorldIntegration] 材质热重载完成: {materialId}");
                }
            };

            // 设置管线使用的默认材质
            pipeline.SetDefaultMaterial(library.GetDefaultMaterial());
            pipeline.SetErrorMaterial(library.GetErrorMaterial());
            pipeline.SetDebugMaterial(library.GetDebugMaterial());

            Debug.WriteLine($"[WorldIntegration] 材质库连接完成 (已同步材质数: {materialCount}, 材质总数: {library.TotalMaterialCount})");
        }

        // ====================================================================
        // 默认渲染管线创建
        // ====================================================================

        /// <summary>
        /// 创建默认世界渲染管线配置
        /// 启用所有渲染通道，使用默认格式和参数
        /// </summary>
        /// <param name="device">RHI设备</param>
        /// <param name="pipeline">输出: 创建的渲染管线</param>
        public static void CreateDefaultRenderPipeline(RHIDevice device, out WorldRenderPipeline pipeline)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device), "RHI设备不能为null");

            Debug.WriteLine("[WorldIntegration] 创建默认渲染管线配置");

            pipeline = new WorldRenderPipeline(device);

            // 配置渲染目标格式
            pipeline.SetRenderTargetFormat(PixelFormat.B8G8R8A8_UNorm_SRGB);
            pipeline.SetDepthStencilFormat(PixelFormat.D32_Float_S8X24_UInt);

            // 配置GeometryBuffer格式
            pipeline.SetGeometryBufferFormats(new[]
            {
                PixelFormat.R16G16B16A16_Float,  // GeometryBuffer 0: BaseColor + Alpha
                PixelFormat.R16G16B16A16_Float,  // GeometryBuffer 1: Normal + ShadingModel
                PixelFormat.R8G8B8A8_UNorm,        // GeometryBuffer 2: AO + Roughness + Metallic + ShadingModelID
                PixelFormat.R11G11B10_Float       // GeometryBuffer 3: WorldPosition (encoded)
            });

            // 启用所有核心渲染通道
            pipeline.EnablePass(RenderPass.DepthPrePass);
            pipeline.EnablePass(RenderPass.GeometryBufferPass);
            pipeline.EnablePass(RenderPass.ShadowCascadePass);
            pipeline.EnablePass(RenderPass.ShadowSpotPass);

            // MicroCluster相关通道 (如果设备支持)
            if (device.SupportsMeshShader)
            {
                pipeline.EnablePass(RenderPass.MicroClusterRasterPass);
                pipeline.EnablePass(RenderPass.MicroClusterImposterPass);
            }

            // ProbeField相关通道 (如果设备支持光线追踪)
            if (device.SupportsRayTracing)
            {
                pipeline.EnablePass(RenderPass.ProbeFieldTracePass);
                pipeline.EnablePass(RenderPass.ProbeFieldSurfaceCache);
                pipeline.EnablePass(RenderPass.ProbeFieldVoxelPass);
            }

            // 屏幕空间效果
            pipeline.EnablePass(RenderPass.SSRPass);
            pipeline.EnablePass(RenderPass.SSAOPass);
            pipeline.EnablePass(RenderPass.VolumetricFogPass);
            pipeline.EnablePass(RenderPass.AtmosphericScatter);

            // 前向渲染通道 (用于特殊材质和半透明)
            pipeline.EnablePass(RenderPass.ForwardOpaquePass);
            pipeline.EnablePass(RenderPass.ForwardTranslucentPass);

            // 后处理链
            pipeline.EnablePass(RenderPass.PostProcessTonemap);
            pipeline.EnablePass(RenderPass.PostProcessBloom);
            pipeline.EnablePass(RenderPass.PostProcessDOF);
            pipeline.EnablePass(RenderPass.PostProcessMotionBlur);
            pipeline.EnablePass(RenderPass.PostProcessTAA);

            // 超分辨率
            if (device.SupportsSuperResolution())
            {
                pipeline.EnablePass(RenderPass.SuperResolutionPass);
            }

            // UI和调试
            pipeline.EnablePass(RenderPass.UIOverlayPass);
            pipeline.EnablePass(RenderPass.DebugOverlayPass);
            pipeline.EnablePass(RenderPass.PresentPass);

            // 配置默认管线参数
            pipeline.SetParameter("ShadowMapSize", 2048);
            pipeline.SetParameter("ShadowCascadeCount", 4);
            pipeline.SetParameter("SSAO.SampleCount", 16);
            pipeline.SetParameter("SSR.MaxRoughness", 0.6f);
            pipeline.SetParameter("Bloom.Threshold", 1.0f);
            pipeline.SetParameter("Bloom.Intensity", 0.3f);
            pipeline.SetParameter("TAA.Sharpness", 0.3f);
            pipeline.SetParameter("DOF.NearDistance", 10.0f);
            pipeline.SetParameter("DOF.FarDistance", 100.0f);
            pipeline.SetParameter("VolumetricFog.FroxelSize", 4);
            pipeline.SetParameter("ProbeField.SurfaceCacheResolution", 128);
            pipeline.SetParameter("ProbeField.CardCount", 65536);

            Debug.WriteLine("[WorldIntegration] 默认渲染管线创建完成 (已启用通道数: " + pipeline.EnabledPassCount + ")");
        }
    }

    // ========================================================================
    // EngineSubsystemRegistry — 引擎子系统注册中心
    // 管理引擎所有子系统的注册、获取、初始化和关闭
    // ========================================================================
    public class EngineSubsystemRegistry
    {
        // --------------------------------------------------------------------
        // 子系统条目 — 记录每个子系统的实例和元数据
        // --------------------------------------------------------------------
        private struct SubsystemEntry
        {
            public string Name;
            public object Instance;
            public int Priority;        // 初始化优先级 (越小越先初始化)
            public List<string> Dependencies; // 依赖的其他子系统名称
            public bool IsInitialized;
            public TimeSpan InitDuration;
        }

        // 子系统注册表
        private readonly Dictionary<string, SubsystemEntry> m_Subsystems = new Dictionary<string, SubsystemEntry>();

        // 初始化顺序 (按Priority排序)
        private List<string> m_InitializationOrder;

        // 是否已完成依赖排序
        private bool m_DependencyResolved;

        // 内建子系统列表及其默认优先级和依赖
        private static readonly (string Name, int Priority, string[] Dependencies)[] s_BuiltinSubsystems = new[]
        {
            ("RHIDevice",       0,  new string[] { }),
            ("SceneRenderer",   10, new string[] { "RHIDevice" }),
            ("MicroCluster",          20, new string[] { "RHIDevice", "SceneRenderer" }),
            ("ProbeField",           21, new string[] { "RHIDevice", "SceneRenderer" }),
            ("Terrain",         22, new string[] { "RHIDevice", "SceneRenderer" }),
            ("MaterialLibrary", 30, new string[] { "RHIDevice" }),
            ("Audio",           40, new string[] { }),
            ("Physics",         50, new string[] { }),
            ("Input",           60, new string[] { }),
            ("UI",              70, new string[] { "RHIDevice" }),
            ("Scripting",       80, new string[] { })
        };

        // --------------------------------------------------------------------
        // 构造函数
        // --------------------------------------------------------------------
        public EngineSubsystemRegistry()
        {
            m_InitializationOrder = new List<string>();
            m_DependencyResolved = false;

            // 预注册内建子系统占位 (实例为null，等待后续RegisterSubsystem填充)
            foreach (var (name, priority, deps) in s_BuiltinSubsystems)
            {
                m_Subsystems[name] = new SubsystemEntry
                {
                    Name = name,
                    Instance = null,
                    Priority = priority,
                    Dependencies = new List<string>(deps),
                    IsInitialized = false,
                    InitDuration = TimeSpan.Zero
                };
            }
        }

        // --------------------------------------------------------------------
        // 注册子系统
        // --------------------------------------------------------------------

        /// <summary>
        /// 注册子系统实例到注册中心
        /// </summary>
        /// <param name="name">子系统名称 (应与内建列表一致)</param>
        /// <param name="subsystem">子系统实例</param>
        public void RegisterSubsystem(string name, object subsystem)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("子系统名称不能为空", nameof(name));
            if (subsystem == null)
                throw new ArgumentNullException(nameof(subsystem), "子系统实例不能为null");

            if (m_Subsystems.TryGetValue(name, out var entry))
            {
                // 更新已有条目的实例
                entry.Instance = subsystem;
                m_Subsystems[name] = entry;
                Debug.WriteLine($"[SubsystemRegistry] 注册子系统: {name} ({subsystem.GetType().Name})");
            }
            else
            {
                // 注册新的自定义子系统 (默认最低优先级，无依赖)
                m_Subsystems[name] = new SubsystemEntry
                {
                    Name = name,
                    Instance = subsystem,
                    Priority = 100,
                    Dependencies = new List<string>(),
                    IsInitialized = false,
                    InitDuration = TimeSpan.Zero
                };
                Debug.WriteLine($"[SubsystemRegistry] 注册自定义子系统: {name} ({subsystem.GetType().Name})");
            }

            // 新注册后需要重新解析依赖
            m_DependencyResolved = false;
        }

        /// <summary>
        /// 注册自定义子系统并指定优先级和依赖
        /// </summary>
        public void RegisterSubsystem(string name, object subsystem, int priority, string[] dependencies)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("子系统名称不能为空", nameof(name));
            if (subsystem == null)
                throw new ArgumentNullException(nameof(subsystem), "子系统实例不能为null");

            m_Subsystems[name] = new SubsystemEntry
            {
                Name = name,
                Instance = subsystem,
                Priority = priority,
                Dependencies = dependencies != null ? new List<string>(dependencies) : new List<string>(),
                IsInitialized = false,
                InitDuration = TimeSpan.Zero
            };

            m_DependencyResolved = false;
            Debug.WriteLine($"[SubsystemRegistry] 注册子系统: {name} (Priority={priority}, Deps={dependencies?.Length ?? 0})");
        }

        // --------------------------------------------------------------------
        // 获取子系统
        // --------------------------------------------------------------------

        /// <summary>
        /// 获取指定名称的子系统实例 (强类型)
        /// </summary>
        /// <typeparam name="T">子系统类型</typeparam>
        /// <param name="name">子系统名称</param>
        /// <returns>子系统实例</returns>
        public T GetSubsystem<T>(string name) where T : class
        {
            if (!m_Subsystems.TryGetValue(name, out var entry))
                throw new KeyNotFoundException($"子系统 '{name}' 未注册");

            if (entry.Instance == null)
                throw new InvalidOperationException($"子系统 '{name}' 尚未提供实例 (注册了占位但未填充)");

            if (!(entry.Instance is T typed))
                throw new InvalidCastException($"子系统 '{name}' 的类型为 {entry.Instance.GetType().Name}，无法转换为 {typeof(T).Name}");

            return typed;
        }

        /// <summary>
        /// 尝试获取子系统实例
        /// </summary>
        public bool TryGetSubsystem<T>(string name, out T subsystem) where T : class
        {
            subsystem = null;
            if (!m_Subsystems.TryGetValue(name, out var entry))
                return false;
            subsystem = entry.Instance as T;
            return subsystem != null;
        }

        // --------------------------------------------------------------------
        // 依赖排序
        // --------------------------------------------------------------------

        /// <summary>
        /// 解析子系统依赖关系并计算初始化顺序 (拓扑排序)
        /// </summary>
        private void ResolveDependencies()
        {
            if (m_DependencyResolved)
                return;

            var sorted = new List<string>();
            var visited = new HashSet<string>();
            var inStack = new HashSet<string>();

            // 验证依赖是否存在
            foreach (var kvp in m_Subsystems)
            {
                foreach (var dep in kvp.Value.Dependencies)
                {
                    if (!m_Subsystems.ContainsKey(dep))
                    {
                        throw new InvalidOperationException(
                            $"子系统 '{kvp.Key}' 依赖的子系统 '{dep}' 未注册。请确保所有依赖的子系统都已注册。");
                    }
                }
            }

            // 拓扑排序 (DFS)
            void Visit(string name)
            {
                if (visited.Contains(name))
                    return;
                if (inStack.Contains(name))
                    throw new InvalidOperationException($"检测到循环依赖: 涉及子系统 '{name}'");

                inStack.Add(name);
                var entry = m_Subsystems[name];

                // 先处理依赖
                foreach (var dep in entry.Dependencies)
                {
                    Visit(dep);
                }

                inStack.Remove(name);
                visited.Add(name);
                sorted.Add(name);
            }

            // 按优先级顺序访问
            var sortedByPriority = m_Subsystems.Values
                .OrderBy(e => e.Priority)
                .Select(e => e.Name)
                .ToList();

            foreach (var name in sortedByPriority)
            {
                Visit(name);
            }

            m_InitializationOrder = sorted;
            m_DependencyResolved = true;

            Debug.WriteLine($"[SubsystemRegistry] 依赖解析完成，初始化顺序: {string.Join(" -> ", m_InitializationOrder)}");
        }

        // --------------------------------------------------------------------
        // 初始化所有子系统
        // --------------------------------------------------------------------

        /// <summary>
        /// 按依赖顺序初始化所有子系统
        /// </summary>
        public void InitializeAll()
        {
            ResolveDependencies();

            Debug.WriteLine("[SubsystemRegistry] 开始初始化所有子系统...");
            var totalStart = Stopwatch.GetTimestamp();

            foreach (var name in m_InitializationOrder)
            {
                var entry = m_Subsystems[name];

                if (entry.Instance == null)
                {
                    Debug.WriteLine($"[SubsystemRegistry] 跳过子系统 '{name}': 无实例 (占位未填充)");
                    continue;
                }

                if (entry.IsInitialized)
                {
                    Debug.WriteLine($"[SubsystemRegistry] 跳过子系统 '{name}': 已初始化");
                    continue;
                }

                var initStart = Stopwatch.GetTimestamp();

                // 通过反射调用 Initialize 方法 (如果存在)
                var initMethod = entry.Instance.GetType().GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (initMethod != null)
                {
                    initMethod.Invoke(entry.Instance, null);
                }

                var elapsed = Stopwatch.GetElapsedTime(initStart);
                entry.IsInitialized = true;
                entry.InitDuration = elapsed;
                m_Subsystems[name] = entry;

                Debug.WriteLine($"[SubsystemRegistry] 初始化子系统 '{name}' 完成 (耗时: {elapsed.TotalMilliseconds:F2}ms)");
            }

            var totalElapsed = Stopwatch.GetElapsedTime(totalStart);
            Debug.WriteLine($"[SubsystemRegistry] 所有子系统初始化完成 (总耗时: {totalElapsed.TotalMilliseconds:F2}ms)");
        }

        // --------------------------------------------------------------------
        // 关闭所有子系统 (反序)
        // --------------------------------------------------------------------

        /// <summary>
        /// 按反序关闭所有子系统
        /// </summary>
        public void ShutdownAll()
        {
            if (!m_DependencyResolved)
                ResolveDependencies();

            Debug.WriteLine("[SubsystemRegistry] 开始关闭所有子系统 (反序)...");

            // 反向遍历初始化顺序
            var shutdownOrder = new List<string>(m_InitializationOrder);
            shutdownOrder.Reverse();

            foreach (var name in shutdownOrder)
            {
                var entry = m_Subsystems[name];

                if (entry.Instance == null || !entry.IsInitialized)
                    continue;

                // 通过反射调用 Shutdown 方法 (如果存在)
                var shutdownMethod = entry.Instance.GetType().GetMethod("Shutdown", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (shutdownMethod != null)
                {
                    shutdownMethod.Invoke(entry.Instance, null);
                }

                entry.IsInitialized = false;
                m_Subsystems[name] = entry;

                Debug.WriteLine($"[SubsystemRegistry] 关闭子系统 '{name}' 完成");
            }

            Debug.WriteLine("[SubsystemRegistry] 所有子系统关闭完成");
        }

        // --------------------------------------------------------------------
        // 查询接口
        // --------------------------------------------------------------------

        /// <summary>
        /// 获取所有已注册子系统的名称
        /// </summary>
        public string[] GetRegisteredSubsystemNames()
        {
            return m_Subsystems.Keys.ToArray();
        }

        /// <summary>
        /// 获取子系统的初始化状态
        /// </summary>
        public bool IsSubsystemInitialized(string name)
        {
            return m_Subsystems.TryGetValue(name, out var entry) && entry.IsInitialized;
        }

        /// <summary>
        /// 获取子系统的初始化耗时
        /// </summary>
        public TimeSpan GetSubsystemInitDuration(string name)
        {
            return m_Subsystems.TryGetValue(name, out var entry) ? entry.InitDuration : TimeSpan.Zero;
        }

        /// <summary>
        /// 获取已注册的子系统总数
        /// </summary>
        public int SubsystemCount => m_Subsystems.Count;

        /// <summary>
        /// 获取已初始化的子系统数量
        /// </summary>
        public int InitializedCount => m_Subsystems.Values.Count(e => e.IsInitialized);
    }
}
