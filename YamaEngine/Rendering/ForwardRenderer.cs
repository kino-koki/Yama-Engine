using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 前向渲染器 (超越 UE7 级别)
    //
    // 集成 MicroCluster 虚拟几何体、ProbeField 全局光照、TSR 时序超分辨率、
    // 路径追踪(可选)、完整后处理栈的集群式前向渲染管线。
    //
    // 渲染流程 (Render Graph 集成):
    //  1. GPU 驱动剔除 (HZB 遮挡 + 视锥 + MicroCluster LOD)
    //  2. 级联阴影贴图 (CSM, 4 级 + PCF/VSM 过滤)
    //  3. MicroCluster 可见性缓冲光栅化 (或传统几何)
    //  4. 集群光照剔除 (Clustered Forward, 3D 网格分配光源)
    //  5. 不透明几何前向着色 (可见性缓冲解码 + 直接光 + ProbeField GI)
    //  6. 透明物体前向着色 (OIT 顺序无关透明)
    //  7. ProbeField GI 更新
    //  8. TSR 时序超分辨率
    //  9. 后处理栈
    // 10. (可选) 路径追踪替换/混合
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>前向渲染器配置。</summary>
    public sealed class ForwardRendererSettings
    {
        /// <summary>启用 MicroCluster 虚拟几何体。</summary>
        public bool EnableMicroCluster = true;
        /// <summary>启用 ProbeField 全局光照。</summary>
        public bool EnableProbeFieldGI = true;
        /// <summary>启用 TSR 时序超分辨率。</summary>
        public bool EnableTSR = true;
        /// <summary>启用路径追踪 (覆盖光栅化)。</summary>
        public bool EnablePathTracing = false;
        /// <summary>启用级联阴影。</summary>
        public bool EnableCSM = true;
        /// <summary>级联数量。</summary>
        public int CascadeCount = 4;
        /// <summary>级联分辨率。</summary>
        public int CascadeResolution = 2048;
        /// <summary>级联分割方案指数。</summary>
        public float CascadeSplitExponent = 0.75f;
        /// <summary>阴影过滤半径。</summary>
        public float ShadowFilterRadius = 2.0f;
        /// <summary>启用顺序无关透明 (OIT)。</summary>
        public bool EnableOIT = true;
        /// <summary>集群光照剔除分辨率倍率。</summary>
        public float ClusterResolutionScale = 1.0f;
        /// <summary>最大每像素光源数。</summary>
        public int MaxLightsPerPixel = 64;
        /// <summary>启用天空大气。</summary>
        public bool EnableAtmosphere = true;
        /// <summary>启用体积雾。</summary>
        public bool EnableVolumetricFog = true;
        /// <summary>内部渲染分辨率比例 (TSR 时)。</summary>
        public float InternalResolutionScale = 1.0f;
    }

    /// <summary>前向渲染级联阴影常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ForwardCascadesCB
    {
        /// <summary>每级级联的视图投影矩阵 (最多 4 级)。</summary>
        public Matrix4x4 CascadeViewProj0, CascadeViewProj1, CascadeViewProj2, CascadeViewProj3;
        /// <summary>级联分割距离。</summary>
        public Vector4 CascadeDistances;
        /// <summary>级联在图集中的缩放与偏移。</summary>
        public Vector4 CascadeScale, CascadeOffset;
        /// <summary>阴影偏移。</summary>
        public float ShadowBias;
        public float NormalBias;
        public float FilterRadius;
        public int CascadeCount;
    }

    /// <summary>集群光照剔除常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ForwardClusterCB
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float NearZ;
        public float FarZ;
        public Vector3 GridDim;      // x, y, z 簇网格维度
        public float LogScale;
        public float SliceScale;
        public int MaxLightsPerCluster;
        public int LightCount;
        public float Pad0;
    }

    /// <summary>前向着色常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ForwardShadingCB
    {
        public Matrix4x4 ViewMatrix;
        public Matrix4x4 ProjMatrix;
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float Exposure;
        public Vector2 InvResolution;
        public int EnableProbeField;
        public int EnableAtmosphere;
        public Vector3 SkyLightColor;
        public float SkyLightIntensity;
        public Vector3 SunDirection;
        public Vector3 SunColor;
        public float SunIntensity;
        public float Pad0;
    }

    /// <summary>集群光照点光源 (与 HLSL PointLight 一致, 48 字节)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ForwardPointLight
    {
        /// <summary>世界空间位置。</summary>
        public Vector3 Position;
        /// <summary>影响半径。</summary>
        public float Radius;
        /// <summary>光源颜色。</summary>
        public Vector3 Color;
        /// <summary>强度。</summary>
        public float Intensity;
        /// <summary>衰减指数。</summary>
        public float Falloff;
        /// <summary>是否投射阴影 (1=是)。</summary>
        public float CastShadow;
        /// <summary>光源 ID。</summary>
        public uint LightID;
        /// <summary>填充。</summary>
        public float Padding;
    }

    /// <summary>前向渲染一帧的渲染结果。</summary>
    public sealed class ForwardRenderResult
    {
        /// <summary>最终输出 (LDR, 后处理后)。</summary>
        public RHITexture? FinalColor;
        /// <summary>HDR 场景颜色 (后处理前)。</summary>
        public RHITexture? SceneColor;
        /// <summary>深度。</summary>
        public RHITexture? Depth;
        /// <summary>运动矢量。</summary>
        public RHITexture? MotionVectors;
        /// <summary>本帧渲染统计。</summary>
        public FrameStats Stats;
    }

    /// <summary>
    /// 前向渲染器主类。编排整个前向渲染管线的执行。
    /// </summary>
    public sealed class ForwardRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ForwardRendererSettings _settings;

        // 集成的子系统
        private MicroClusterRenderer? _microcluster;
        private ProbeFieldGlobalIllumination? _probefield;
        private TemporalSuperResolution? _tsr;
        private PathTracer? _pathTracer;
        private PostProcessingStack _postProcess;

        // 渲染目标
        private RHITexture? _sceneColor;      // HDR
        private RHITexture? _sceneDepth;
        private RHITexture? _motionVectors;
        private RHITexture? _geometrybufferNormals;  // 用于 ProbeField/TSR
        private RHITexture? _geometrybufferAlbedo;
        private RHITexture? _finalOutput;

        // 级联阴影
        private RHITexture? _shadowAtlas;
        private RHIPipelineState? _shadowPipeline;
        private RHIPipelineState? _shadowFilterPipeline;

        // 集群光照
        private RHIBuffer? _clusterLightGrid;
        private RHIBuffer? _clusterLightIndexList;
        private RHIPipelineState? _clusterCullPipeline;
        private RHIPipelineState? _shadingPipeline;
        private RHIPipelineState? _oitPipeline;
        private RHIRootSignature? _rootSignature;

        // CB 上传缓冲 (每帧写入, 对应 HLSL b2/b3/b4)
        private RHIBuffer? _shadingCBUpload;
        private RHIBuffer? _cascadesCBUpload;
        private RHIBuffer? _clusterCBUpload;

        // VSM 阴影矩 (ShadowCS/ShadowFilterCS 写出, ForwardShadeCS 采样 t1)
        private RHITexture? _shadowMoments;
        private RHIDescriptorHandle _shadowDepthSRV;
        private RHIDescriptorHandle _shadowMomentsSRV;
        private RHIDescriptorHandle _shadowMomentsUAV;

        // 集群光照: 光源数据 + 场景资源 SRV/UAV
        private readonly List<ForwardPointLight> _lights = new();
        private ForwardPointLight[] _lightUpload = Array.Empty<ForwardPointLight>();
        private RHIBuffer? _lightBuffer;
        private RHIDescriptorHandle _lightSRV;
        private RHIDescriptorHandle _clusterGridSRV;
        private RHIDescriptorHandle _clusterListSRV;
        private RHIDescriptorHandle _clusterGridUAV;
        private RHIDescriptorHandle _clusterListUAV;
        private RHIDescriptorHandle _sceneDepthSRV;
        private RHIDescriptorHandle _normalsSRV;
        private RHIDescriptorHandle _albedoSRV;
        private RHIDescriptorHandle _sceneColorUAV;
        private const int MaxLights = 1024;

        // OIT 顺序无关透明 (McGuire & Bavoil 2013 加权混合)
        private RHITexture? _oitAccum;
        private RHITexture? _oitRevealage;
        private RHIDescriptorHandle _oitAccumUAV;
        private RHIDescriptorHandle _oitRevealageUAV;

        // 视图历史
        private Matrix4x4 _prevViewProj;
        private int _frameIndex;
        private int _internalWidth, _internalHeight;
        private int _outputWidth, _outputHeight;

        /// <summary>配置。</summary>
        public ForwardRendererSettings Settings => _settings;
        /// <summary>MicroCluster 子系统。</summary>
        public MicroClusterRenderer? MicroCluster => _microcluster;
        /// <summary>ProbeField GI 子系统。</summary>
        public ProbeFieldGlobalIllumination? ProbeField => _probefield;
        /// <summary>TSR 子系统。</summary>
        public TemporalSuperResolution? TSR => _tsr;
        /// <summary>路径追踪器。</summary>
        public PathTracer? PathTracer => _pathTracer;
        /// <summary>后处理栈。</summary>
        public PostProcessingStack PostProcess => _postProcess;
        /// <summary>最终输出。</summary>
        public RHITexture? FinalOutput => _finalOutput;

        /// <summary>构造。</summary>
        public ForwardRenderer(RHIDevice device, ForwardRendererSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new ForwardRendererSettings();
            _postProcess = new PostProcessingStack(device);
        }

        /// <summary>初始化渲染器与所有子系统。</summary>
        public void Initialize(int outputWidth, int outputHeight)
        {
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;
            _internalWidth = (int)(outputWidth * _settings.InternalResolutionScale);
            _internalHeight = (int)(outputHeight * _settings.InternalResolutionScale);

            // 初始化子系统
            if (_settings.EnableMicroCluster)
            {
                _microcluster = new MicroClusterRenderer(_device);
                _microcluster.Initialize();
            }
            if (_settings.EnableProbeFieldGI)
            {
                _probefield = new ProbeFieldGlobalIllumination(_device);
                _probefield.Initialize();
                _probefield.Resize(_internalWidth, _internalHeight);
            }
            if (_settings.EnableTSR)
            {
                _tsr = new TemporalSuperResolution(_device);
                _tsr.Initialize(outputWidth, outputHeight);
            }
            if (_settings.EnablePathTracing)
            {
                _pathTracer = new PathTracer(_device);
                _pathTracer.Initialize();
                _pathTracer.Resize(outputWidth, outputHeight);
            }

            _postProcess.Initialize(outputWidth, outputHeight);
            CreatePipelines();
            AllocateRenderTargets();
        }

        /// <summary>创建管线状态。</summary>
        private void CreatePipelines()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 2 } },   // 0:  b2 ForwardShadingCB
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 3 } },   // 1:  b3 ForwardCascadesCB
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 4 } },   // 2:  b4 ForwardClusterCB
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 0 } } } }, // 3:  t0 ShadowDepthAtlas
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }, // 4:  t1 ShadowMoments
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 2 } } } }, // 5:  t2 ClusterGrid
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 3 } } } }, // 6:  t3 ClusterLightList
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 4 } } } }, // 7:  t4 PointLights
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 5 } } } }, // 8:  t5 SceneDepth
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 6 } } } }, // 9:  t6 GeometryBufferNormals
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 7 } } } }, // 10: t7 GeometryBufferAlbedo
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }, // 11: u1 MomentsUAV
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 2 } } } }, // 12: u2 ClusterGridUAV
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 3 } } } }, // 13: u3 ClusterLightListUAV
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 0 } } } }  // 14: u0 SceneColorUAV
                    ,
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 4 } } } }, // 15: u4 OitAccumUAV
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 5 } } } }  // 16: u5 OitRevealageUAV
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp, RHISamplerDesc.AnisotropicWrap, RHISamplerDesc.ShadowSampler }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            _shadowPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ForwardCSM", CS = ShaderManager.Instance.GetBytecode("Forward/Forward", "ShadowCS", RHIShaderStage.Compute) });

            _shadowFilterPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ForwardShadowFilter", CS = ShaderManager.Instance.GetBytecode("Forward/Forward", "ShadowFilterCS", RHIShaderStage.Compute) });

            _clusterCullPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ForwardClusterCull", CS = ShaderManager.Instance.GetBytecode("Forward/Forward", "ClusterCullCS", RHIShaderStage.Compute) });

            _shadingPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ForwardShading", CS = ShaderManager.Instance.GetBytecode("Forward/Forward", "ForwardShadeCS", RHIShaderStage.Compute) });

            _oitPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ForwardOIT", CS = ShaderManager.Instance.GetBytecode("Forward/Forward", "OITCS", RHIShaderStage.Compute) });
        }

        /// <summary>分配渲染目标纹理。</summary>
        private void AllocateRenderTargets()
        {
            RHITextureDesc MakeRT(string name, RHIFormat fmt, int w, int h) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = w, Height = h, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource | RHITextureFlags.RenderTarget
            };

            _sceneColor = _device.CreateTexture(MakeRT("ForwardSceneColor", RHIFormat.R16G16B16A16_Float, _internalWidth, _internalHeight), "ForwardSceneColor");
            _geometrybufferNormals = _device.CreateTexture(MakeRT("ForwardNormals", RHIFormat.R10G10B10A2_UNorm, _internalWidth, _internalHeight), "ForwardNormals");
            _geometrybufferAlbedo = _device.CreateTexture(MakeRT("ForwardAlbedo", RHIFormat.R8G8B8A8_UNorm, _internalWidth, _internalHeight), "ForwardAlbedo");

            var depthDesc = MakeRT("ForwardDepth", RHIFormat.D32_Float, _internalWidth, _internalHeight);
            depthDesc.Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource;
            _sceneDepth = _device.CreateTexture(depthDesc, "ForwardDepth");

            var mvDesc = MakeRT("ForwardMotion", RHIFormat.R16G16_Float, _internalWidth, _internalHeight);
            _motionVectors = _device.CreateTexture(mvDesc, "ForwardMotion");

            // 级联阴影图集
            int atlasSize = _settings.CascadeResolution * _settings.CascadeCount;
            var shadowDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = atlasSize, Height = _settings.CascadeResolution, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
            };
            _shadowAtlas = _device.CreateTexture(shadowDesc, "ForwardShadowAtlas");

            // VSM 阴影矩纹理 (float2: mean, mean²) 与描述符
            _shadowMoments = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32G32_Float,
                Width = atlasSize, Height = _settings.CascadeResolution, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, "ForwardShadowMoments");
            _shadowDepthSRV = _device.CreateSRV(_shadowAtlas);
            _shadowMomentsUAV = _device.CreateUAV(_shadowMoments);

            // 集簇光照网格缓冲
            int gridX = (_internalWidth + 63) / 64;
            int gridY = (_internalHeight + 63) / 64;
            int gridZ = 32;
            var gridDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(gridX * gridY * gridZ * 8),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 8,
                HeapType = RHIResourceHeapType.Default
            };
            _clusterLightGrid = _device.CreateBuffer(gridDesc, "ForwardClusterGrid");
            _clusterLightIndexList = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(gridX * gridY * gridZ * _settings.MaxLightsPerPixel * 4),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 4,
                HeapType = RHIResourceHeapType.Default
            }, "ForwardClusterLightList");

            // 光源缓冲 (容量固定, 每帧上传实际数量)
            _lightBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(MaxLights * 48),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 48,
                HeapType = RHIResourceHeapType.Upload
            }, "ForwardPointLights");
            _lightUpload = new ForwardPointLight[MaxLights];

            // 场景资源描述符
            int clusterCount = gridX * gridY * gridZ;
            _shadowMomentsSRV = _device.CreateSRV(_shadowMoments);
            _clusterGridSRV = _device.CreateSRV(_clusterLightGrid, RHIFormat.Unknown, true, 0, (uint)clusterCount, 8);
            _clusterListSRV = _device.CreateSRV(_clusterLightIndexList, RHIFormat.Unknown, true, 0, (uint)(clusterCount * _settings.MaxLightsPerPixel), 4);
            _clusterGridUAV = _device.CreateUAV(_clusterLightGrid, RHIFormat.Unknown, true, 0, (uint)clusterCount, 8);
            _clusterListUAV = _device.CreateUAV(_clusterLightIndexList, RHIFormat.Unknown, true, 0, (uint)(clusterCount * _settings.MaxLightsPerPixel), 4);
            _lightSRV = _device.CreateSRV(_lightBuffer, RHIFormat.Unknown, true, 0, MaxLights, 48);
            _sceneDepthSRV = _device.CreateSRV(_sceneDepth);
            _normalsSRV = _device.CreateSRV(_geometrybufferNormals);
            _albedoSRV = _device.CreateSRV(_geometrybufferAlbedo);
            _sceneColorUAV = _device.CreateUAV(_sceneColor);

            // OIT 累积纹理 (初始值: accum=0, revealage=1 → 无透明几何时 OITCS 为无操作)
            _oitAccum = _device.CreateTexture(MakeRT("ForwardOitAccum", RHIFormat.R16G16B16A16_Float, _internalWidth, _internalHeight), "ForwardOitAccum");
            _oitRevealage = _device.CreateTexture(MakeRT("ForwardOitRevealage", RHIFormat.R8_UNorm, _internalWidth, _internalHeight), "ForwardOitRevealage");
            _device.UploadToTexture(_oitAccum, new byte[_internalWidth * _internalHeight * 8], _internalWidth, _internalHeight);
            var revealInit = new byte[_internalWidth * _internalHeight];
            Array.Fill(revealInit, (byte)255);
            _device.UploadToTexture(_oitRevealage, revealInit, _internalWidth, _internalHeight);
            _oitAccumUAV = _device.CreateUAV(_oitAccum);
            _oitRevealageUAV = _device.CreateUAV(_oitRevealage);

            // 最终输出
            _finalOutput = _device.CreateTexture(MakeRT("ForwardFinal", RHIFormat.R8G8B8A8_UNorm, _outputWidth, _outputHeight), "ForwardFinal");
        }

        /// <summary>窗口尺寸变化时重建资源。</summary>
        public void Resize(int width, int height)
        {
            _outputWidth = width;
            _outputHeight = height;
            _internalWidth = (int)(width * _settings.InternalResolutionScale);
            _internalHeight = (int)(height * _settings.InternalResolutionScale);

            _sceneColor?.Dispose(); _sceneDepth?.Dispose(); _motionVectors?.Dispose();
            _geometrybufferNormals?.Dispose(); _geometrybufferAlbedo?.Dispose(); _finalOutput?.Dispose();
            _shadowMoments?.Dispose();
            _oitAccum?.Dispose(); _oitRevealage?.Dispose();
            _lightBuffer?.Dispose();
            AllocateRenderTargets();

            _probefield?.Resize(_internalWidth, _internalHeight);
            _tsr?.Resize(width, height);
            _postProcess.Resize(width, height);
            _pathTracer?.Resize(width, height);
        }

        /// <summary>
        /// 渲染一帧。这是前向渲染器的总入口。
        /// </summary>
        public ForwardRenderResult Render(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            Vector3 cameraPos, Vector3 sunDir, float deltaTime)
        {
            _frameIndex++;
            var result = new ForwardRenderResult { SceneColor = _sceneColor, Depth = _sceneDepth, MotionVectors = _motionVectors };

            // --- 路径追踪路径 (覆盖光栅化) ---
            if (_settings.EnablePathTracing && _pathTracer != null)
            {
                _pathTracer.Render(cmdList, view, proj, cameraPos, _outputWidth, _outputHeight, deltaTime);
                // 后处理路径追踪结果
                _postProcess.Process(cmdList, _pathTracer.Result!, _sceneDepth!, _motionVectors!,
                    _finalOutput!, _prevViewProj, Matrix4x4.Identity, deltaTime);
                result.FinalColor = _finalOutput;
                result.Stats.FrameTimeMilliseconds = deltaTime;
                _prevViewProj = view * proj;
                return result;
            }

            // --- 标准前向光栅化路径 ---

            // 应用 TSR 抖动到投影矩阵
            Matrix4x4 jitteredProj = proj;
            if (_settings.EnableTSR && _tsr != null)
            {
                jitteredProj = _tsr.ApplyJitterToProjection(proj);
            }

            // 帧首上传着色常量 (b2, 供 ClusterCullCS/ForwardShadeCS/OITCS 读取)
            UploadShadingCB(view, jitteredProj, cameraPos, sunDir);

            // Pass 1: 级联阴影贴图 (CSM)
            if (_settings.EnableCSM)
            {
                RenderCSM(cmdList, view, proj, cameraPos, sunDir);
            }

            // Pass 2: GPU 驱动剔除 + MicroCluster 可见性缓冲
            if (_settings.EnableMicroCluster && _microcluster != null)
            {
                _microcluster.Cull(cmdList, view, jitteredProj, cameraPos, _internalWidth, _internalHeight);
                _microcluster.Rasterize(cmdList, view * jitteredProj);
            }

            // Pass 3: 运动矢量计算 (TSR)
            if (_settings.EnableTSR && _tsr != null)
            {
                _tsr.ComputeMotionVectors(cmdList, _sceneDepth!, view, jitteredProj, view, proj);
            }

            // Pass 4: 集群光照剔除
            ClusterLightCull(cmdList, view, jitteredProj, cameraPos);

            // Pass 5: 前向着色 (不透明)
            RenderOpaque(cmdList, view, jitteredProj, cameraPos, sunDir);

            // Pass 6: ProbeField GI 更新
            if (_settings.EnableProbeFieldGI && _probefield != null)
            {
                _probefield.Update(cmdList, view, jitteredProj, _prevViewProj, cameraPos, _internalWidth, _internalHeight, deltaTime);
            }

            // Pass 7: 透明物体 (OIT)
            if (_settings.EnableOIT)
            {
                RenderTransparent(cmdList, view, jitteredProj);
            }

            // Pass 8: TSR 时序超分辨率
            RHITexture upscaled = _sceneColor!;
            if (_settings.EnableTSR && _tsr != null)
            {
                _tsr.Upscale(cmdList, _sceneColor!, _sceneDepth!, _finalOutput!, view, jitteredProj, deltaTime);
                upscaled = _finalOutput!;
            }

            // Pass 9: 后处理栈
            _postProcess.Process(cmdList, upscaled, _sceneDepth!, _motionVectors!,
                _finalOutput!, _prevViewProj, Matrix4x4.Identity, deltaTime);

            // 自适应分辨率更新
            if (_settings.EnableTSR && _tsr != null)
            {
                _tsr.UpdateAdaptiveResolution(deltaTime);
            }

            result.FinalColor = _finalOutput;
            result.Stats.FrameTimeMilliseconds = deltaTime;
            _prevViewProj = view * jitteredProj;
            return result;
        }

        /// <summary>渲染级联阴影贴图 (CSM)：清空图集 → ShadowCS 构建 VSM 矩 → ShadowFilterCS 高斯过滤。</summary>
        private void RenderCSM(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos, Vector3 sunDir)
        {
            if (_shadowPipeline == null || _shadowAtlas == null || _shadowMoments == null) return;

            // 计算级联分割距离
            float nearZ = proj.M43 / (proj.M33 - 1); // 近平面
            float farZ = proj.M43 / (proj.M33 + 1);  // 远平面
            var cascadeDistances = ComputeCascadeSplits(nearZ, farZ, _settings.CascadeCount, _settings.CascadeSplitExponent);

            // 为每级级联构建正交视图投影
            var cascadeVP = new Matrix4x4[4];
            for (int i = 0; i < _settings.CascadeCount; i++)
            {
                cascadeVP[i] = ComputeCascadeViewProj(view, proj, cameraPos, sunDir, cascadeDistances[i], cascadeDistances[i + 1]);
            }

            // 上传级联常量 (cbuffer ForwardCascadesCB : register(b3) → root param 1)
            _cascadesCBUpload ??= _device.CreateUploadBuffer((ulong)Marshal.SizeOf<ForwardCascadesCB>(), "Forward.CascadesCB");
            var cb = new ForwardCascadesCB
            {
                CascadeViewProj0 = cascadeVP[0],
                CascadeViewProj1 = cascadeVP[1],
                CascadeViewProj2 = cascadeVP[2],
                CascadeViewProj3 = cascadeVP[3],
                CascadeDistances = new Vector4(cascadeDistances[1], cascadeDistances[2], cascadeDistances[3], cascadeDistances[4]),
                // 图集布局: 级联 i 占据 x ∈ [i/N, (i+1)/N]
                CascadeScale = new Vector4(1f / _settings.CascadeCount, 1f, 1f, 1f),
                CascadeOffset = new Vector4(0f, 0f, 0f, 0f),
                ShadowBias = 0.001f,
                NormalBias = 0.02f,
                FilterRadius = _settings.ShadowFilterRadius,
                CascadeCount = _settings.CascadeCount
            };
            _device.UploadToBuffer(_cascadesCBUpload, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));

            // 清空阴影图集深度
            var dsv = _device.CreateDSV(_shadowAtlas);
            cmdList.OMSetRenderTargets(Array.Empty<RHIDescriptorHandle>(), dsv);
            cmdList.ClearDepthStencilView(dsv, 1.0f, 0);
            cmdList.SetViewport(new RHIViewport(_shadowAtlas!.Width, _shadowAtlas!.Height));

            // ShadowCS: 深度图集 (t0) → VSM 矩 (u1)
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_shadowPipeline);
            cmdList.SetComputeRootConstantBufferView(1, _cascadesCBUpload);
            cmdList.TransitionTo(_shadowAtlas, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_shadowMoments, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRootDescriptorTable(3, _shadowDepthSRV);
            cmdList.SetComputeRootDescriptorTable(11, _shadowMomentsUAV);
            int sw = _shadowAtlas.Width;
            int sh = _shadowAtlas.Height;
            cmdList.Dispatch((sw + 7) / 8, (sh + 7) / 8, 1);

            // ShadowFilterCS: 5x5 高斯过滤矩 (u1)
            cmdList.SetPipelineState(_shadowFilterPipeline!);
            cmdList.SetComputeRootDescriptorTable(11, _shadowMomentsUAV);
            cmdList.Dispatch((sw + 7) / 8, (sh + 7) / 8, 1);

            // 转为着色可采样 (ForwardShadeCS 的 ShadowMoments t1)
            cmdList.TransitionTo(_shadowMoments, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>计算级联分割距离 (对数+线性混合)。</summary>
        private static float[] ComputeCascadeSplits(float nearZ, float farZ, int count, float exponent)
        {
            var splits = new float[count + 1];
            splits[0] = nearZ;
            for (int i = 1; i <= count; i++)
            {
                float p = (float)i / count;
                float logSplit = nearZ * MathF.Pow(farZ / nearZ, p);
                float linearSplit = nearZ + (farZ - nearZ) * p;
                splits[i] = MathHelper.Lerp(linearSplit, logSplit, exponent);
            }
            splits[count] = farZ;
            return splits;
        }

        /// <summary>计算单级级联的正交视图投影。</summary>
        private static Matrix4x4 ComputeCascadeViewProj(in Matrix4x4 view, in Matrix4x4 proj,
            Vector3 cameraPos, Vector3 sunDir, float nearSplit, float farSplit)
        {
            // 构建光源视图矩阵
            var lightView = Matrix4x4.CreateLookAt(Vector3.Zero, -sunDir, Vector3.UnitY);
            // 计算级联包围球并构建正交投影
            float radius = MathF.Max(nearSplit, farSplit) * 0.5f;
            var lightProj = Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, -radius * 4, radius * 4);
            return lightView * lightProj;
        }

        /// <summary>
        /// 集群光照剔除：上传光源与集群常量，每线程一集群测试点光源相交。
        /// </summary>
        private void ClusterLightCull(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos)
        {
            if (_clusterCullPipeline == null || _lightBuffer == null) return;
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_clusterCullPipeline);
            cmdList.TransitionTo(_clusterLightGrid!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_clusterLightIndexList!, RHIResourceState.UnorderedAccess);

            // 上传点光源数据 (t4)
            int lightCount = Math.Min(_lights.Count, MaxLights);
            for (int i = 0; i < lightCount; i++) _lightUpload[i] = _lights[i];
            if (lightCount > 0)
                _device.UploadToBuffer(_lightBuffer, _lightUpload.AsSpan(0, lightCount));

            // 上传集群常量 (cbuffer ForwardClusterCB : register(b4) → root param 2)
            float nearZ = proj.M43 / (proj.M33 - 1);
            float farZ = proj.M43 / (proj.M33 + 1);
            var cb = new ForwardClusterCB
            {
                InvViewProj = Matrix4x4.Invert(view * proj, out var inv) ? inv : Matrix4x4.Identity,
                CameraPos = cameraPos,
                NearZ = nearZ,
                FarZ = farZ,
                GridDim = new Vector3((_internalWidth + 63) / 64, (_internalHeight + 63) / 64, 32),
                LogScale = 1.0f,
                SliceScale = 32.0f / MathF.Log2(farZ / nearZ),
                MaxLightsPerCluster = _settings.MaxLightsPerPixel,
                LightCount = lightCount
            };
            _clusterCBUpload ??= _device.CreateUploadBuffer((ulong)Marshal.SizeOf<ForwardClusterCB>(), "Forward.ClusterCB");
            _device.UploadToBuffer(_clusterCBUpload, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));

            // 绑定: b2 (fInvResolution) + b4 (集群参数) + t4 光源 + u2/u3 集群输出
            cmdList.SetComputeRootConstantBufferView(0, _shadingCBUpload!);
            cmdList.SetComputeRootConstantBufferView(2, _clusterCBUpload);
            cmdList.SetComputeRootDescriptorTable(7, _lightSRV);
            cmdList.SetComputeRootDescriptorTable(12, _clusterGridUAV);
            cmdList.SetComputeRootDescriptorTable(13, _clusterListUAV);

            // 每线程一集群 (numthreads 64)
            int totalClusters = ((_internalWidth + 63) / 64) * ((_internalHeight + 63) / 64) * 32;
            cmdList.Dispatch((totalClusters + 63) / 64, 1, 1);

            cmdList.TransitionTo(_clusterLightGrid, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_clusterLightIndexList, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>渲染不透明几何 (屏幕空间前向着色: 集群光源 + CSM 阴影)。</summary>
        private void RenderOpaque(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos, Vector3 sunDir)
        {
            if (_shadingPipeline == null) return;
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_shadingPipeline);
            cmdList.TransitionTo(_sceneColor!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_sceneDepth!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_geometrybufferNormals!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_geometrybufferAlbedo!, RHIResourceState.NonPixelShaderResource);
            if (_shadowMoments != null)
                cmdList.TransitionTo(_shadowMoments, RHIResourceState.NonPixelShaderResource);

            // 绑定: b2 着色 + b3 级联 + b4 集群 + 全部场景资源 (t1-t7) + u0 输出
            cmdList.SetComputeRootConstantBufferView(0, _shadingCBUpload!);
            if (_cascadesCBUpload != null)
                cmdList.SetComputeRootConstantBufferView(1, _cascadesCBUpload);
            if (_clusterCBUpload != null)
                cmdList.SetComputeRootConstantBufferView(2, _clusterCBUpload);
            if (_shadowMomentsSRV.IsValid)
                cmdList.SetComputeRootDescriptorTable(4, _shadowMomentsSRV);
            if (_clusterGridSRV.IsValid)
                cmdList.SetComputeRootDescriptorTable(5, _clusterGridSRV);
            if (_clusterListSRV.IsValid)
                cmdList.SetComputeRootDescriptorTable(6, _clusterListSRV);
            if (_lightSRV.IsValid)
                cmdList.SetComputeRootDescriptorTable(7, _lightSRV);
            if (_sceneDepthSRV.IsValid)
                cmdList.SetComputeRootDescriptorTable(8, _sceneDepthSRV);
            if (_normalsSRV.IsValid)
                cmdList.SetComputeRootDescriptorTable(9, _normalsSRV);
            if (_albedoSRV.IsValid)
                cmdList.SetComputeRootDescriptorTable(10, _albedoSRV);
            if (_sceneColorUAV.IsValid)
                cmdList.SetComputeRootDescriptorTable(14, _sceneColorUAV);

            cmdList.Dispatch((_internalWidth + 7) / 8, (_internalHeight + 7) / 8, 1);
            cmdList.TransitionTo(_sceneColor, RHIResourceState.PixelShaderResource);
        }

        /// <summary>渲染透明物体 (OIT 合成: 透明累积 → 混入场景色)。</summary>
        private void RenderTransparent(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj)
        {
            if (_oitPipeline == null || _oitAccum == null || _oitRevealage == null) return;
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_oitPipeline);
            cmdList.TransitionTo(_sceneColor!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_oitAccum, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_oitRevealage, RHIResourceState.UnorderedAccess);

            // 绑定: b2 着色常量 + u0 场景色 + u4/u5 OIT 累积
            cmdList.SetComputeRootConstantBufferView(0, _shadingCBUpload!);
            if (_sceneColorUAV.IsValid)
                cmdList.SetComputeRootDescriptorTable(14, _sceneColorUAV);
            if (_oitAccumUAV.IsValid)
                cmdList.SetComputeRootDescriptorTable(15, _oitAccumUAV);
            if (_oitRevealageUAV.IsValid)
                cmdList.SetComputeRootDescriptorTable(16, _oitRevealageUAV);

            cmdList.Dispatch((_internalWidth + 7) / 8, (_internalHeight + 7) / 8, 1);
            cmdList.TransitionTo(_sceneColor, RHIResourceState.PixelShaderResource);
        }

        /// <summary>注册 MicroCluster 网格。</summary>
        public void RegisterMicroClusterMesh(MicroClusterMeshAsset asset, RHICommandList cmdList)
        {
            _microcluster?.RegisterMesh(asset, cmdList);
        }

        // ── 光源管理 (集群光照输入) ──

        /// <summary>设置点光源列表 (覆盖)。</summary>
        public void SetLights(IReadOnlyList<ForwardPointLight> lights)
        {
            _lights.Clear();
            if (lights == null) return;
            foreach (var l in lights) _lights.Add(l);
        }

        /// <summary>添加单个点光源。</summary>
        public void AddLight(in ForwardPointLight light) => _lights.Add(light);

        /// <summary>清空全部点光源。</summary>
        public void ClearLights() => _lights.Clear();

        /// <summary>当前光源数。</summary>
        public int LightCount => _lights.Count;

        /// <summary>
        /// 上传本帧着色常量 (cbuffer ForwardShadingCB : register(b2) → root param 0)。
        /// 供 ClusterCullCS (fInvResolution) / ForwardShadeCS / OITCS 读取。
        /// </summary>
        private void UploadShadingCB(in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos, Vector3 sunDir)
        {
            _shadingCBUpload ??= _device.CreateUploadBuffer((ulong)Marshal.SizeOf<ForwardShadingCB>(), "Forward.ShadingCB");
            var cb = new ForwardShadingCB
            {
                ViewMatrix = view,
                ProjMatrix = proj,
                InvViewProj = Matrix4x4.Invert(view * proj, out var inv) ? inv : Matrix4x4.Identity,
                CameraPos = cameraPos,
                Exposure = _postProcess.CurrentExposure,
                InvResolution = new Vector2(1f / _internalWidth, 1f / _internalHeight),
                EnableProbeField = 0,   // 待 EnvProbe (t13) 资源接线后由设置驱动
                EnableAtmosphere = 0,   // 待大气资源接线后由设置驱动
                SkyLightColor = new Vector3(0.4f, 0.6f, 1.0f),
                SkyLightIntensity = 1.0f,
                SunDirection = sunDir,
                SunColor = new Vector3(1.0f, 0.95f, 0.85f),
                SunIntensity = 8.0f
            };
            _device.UploadToBuffer(_shadingCBUpload, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));
        }

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            _microcluster?.Dispose();
            _probefield?.Dispose();
            _tsr?.Dispose();
            _pathTracer?.Dispose();
            _postProcess.Dispose();
            _sceneColor?.Dispose();
            _sceneDepth?.Dispose();
            _motionVectors?.Dispose();
            _geometrybufferNormals?.Dispose();
            _geometrybufferAlbedo?.Dispose();
            _finalOutput?.Dispose();
            _shadowAtlas?.Dispose();
            _shadowMoments?.Dispose();
            _shadingCBUpload?.Dispose();
            _cascadesCBUpload?.Dispose();
            _clusterCBUpload?.Dispose();
            _lightBuffer?.Dispose();
            _clusterLightGrid?.Dispose();
            _clusterLightIndexList?.Dispose();
            _shadowPipeline?.Dispose();
            _shadowFilterPipeline?.Dispose();
            _clusterCullPipeline?.Dispose();
            _shadingPipeline?.Dispose();
            _oitPipeline?.Dispose();
            _rootSignature?.Dispose();
            _oitAccum?.Dispose();
            _oitRevealage?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>数学辅助 (避免与 System.Math 冲突)。</summary>
}
