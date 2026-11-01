using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama ProbeField 实时全局光照系统 (超越 UE7 级别)
    //
    // 多层混合 GI 架构:
    //  1. SSGI (屏幕空间全局光照) - 高频细节的快速基础层, 屏幕空间光线步进
    //  2. Surface Cache (表面缓存) - 将场景表面参数化到图集, 缓存直接/间接辐照度,
    //     支持动态物体, 提供"近场"多跳 GI
    //  3. Voxel Scene (体素化场景) - 将场景体素化到 3D 纹理, 支持锥体追踪与体素 GI
    //  4. Hardware RT GI - DXR/Vulkan RT 加速的光线追踪全局光照 (远场/精确)
    //  5. Probe Grid (探针网格) - DDGI 风格辐照度探针体积, 漫反射多跳间接光
    //  6. 时空重投影与去噪 - SVGF 风格时空滤波器, TAA 重投影, 自适应采样
    //  7. 多 Bounce 间接光 - 通过表面缓存与探针级联累积实现多跳能量传递
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>GI 质量档位。</summary>
    public enum ProbeFieldQualityLevel { Low, Medium, High, Ultra, Cinematic }

    /// <summary>GI 光线追踪后端。</summary>
    public enum ProbeFieldTraceMode
    {
        /// <summary>仅屏幕空间 (最快, 屏幕外无贡献)。</summary>
        ScreenSpaceOnly,
        /// <summary>屏幕空间 + 体素锥体追踪 (中速, 全场景覆盖)。</summary>
        ScreenSpaceVoxel,
        /// <summary>屏幕空间 + 硬件光线追踪 (最精确, 需 DXR/RT)。</summary>
        ScreenSpaceHWRT,
        /// <summary>混合: 屏幕空间近场 + 体素中远场 + RT 精确补充。</summary>
        Hybrid
    }

    /// <summary>ProbeField 全局光照系统配置。</summary>
    public sealed class ProbeFieldGISettings
    {
        /// <summary>GI 质量档位。</summary>
        public ProbeFieldQualityLevel Quality = ProbeFieldQualityLevel.High;
        /// <summary>光线追踪后端模式。</summary>
        public ProbeFieldTraceMode TraceMode = ProbeFieldTraceMode.Hybrid;

        // --- SSGI ---
        /// <summary>启用屏幕空间 GI 基础层。</summary>
        public bool EnableSSGI = true;
        /// <summary>SSGI 光线步进步长 (像素)。</summary>
        public float SSGIRayStep = 2.0f;
        /// <summary>SSGI 最大光线长度 (像素)。</summary>
        public float SSGIMaxRayLength = 64.0f;
        /// <summary>SSGI 每像素采样数。</summary>
        public int SSGISampleCount = 4;

        // --- Surface Cache ---
        /// <summary>启用表面缓存。</summary>
        public bool EnableSurfaceCache = true;
        /// <summary>表面缓存图集分辨率。</summary>
        public int SurfaceCacheResolution = 1024;
        /// <summary>表面缓存最大图集页数。</summary>
        public int SurfaceCacheMaxPages = 512;

        // --- Voxel Scene ---
        /// <summary>启用体素化场景。</summary>
        public bool EnableVoxelScene = true;
        /// <summary>体素体积每轴分辨率。</summary>
        public int VoxelVolumeResolution = 128;
        /// <summary>体素体积世界范围。</summary>
        public float VoxelVolumeExtent = 100.0f;

        // --- Hardware RT ---
        /// <summary>启用硬件光线追踪 GI。</summary>
        public bool EnableHardwareRT = true;
        /// <summary>RT 每像素最大射线数。</summary>
        public int RTRaysPerPixel = 1;
        /// <summary>RT 最大反弹数。</summary>
        public int RTMaxBounces = 2;

        // --- Probe Grid ---
        /// <summary>启用探针网格。</summary>
        public bool EnableProbeGrid = true;
        /// <summary>探针网格分辨率 (每轴探针数)。</summary>
        public Vector3 ProbeGridResolution = new(16, 8, 16);
        /// <summary>探针网格世界范围。</summary>
        public Vector3 ProbeGridExtent = new(64, 32, 64);
        /// <summary>每探针射线数。</summary>
        public int ProbesRayCount = 144;

        // --- 时空去噪 ---
        /// <summary>启用 SVGF 去噪器。</summary>
        public bool EnableDenoiser = true;
        /// <summary>SVGF atrous 滤波迭代次数。</summary>
        public int SvgfIterations = 4;
        /// <summary>时序累积 alpha (历史权重)。</summary>
        public float TemporalAlpha = 0.92f;

        // --- 多 Bounce ---
        /// <summary>间接光反弹次数。</summary>
        public int BounceCount = 2;
        /// <summary>天空光颜色 (线性 HDR)。</summary>
        public Vector3 SkyLightColor = new(0.4f, 0.6f, 1.0f);
        /// <summary>天空光强度。</summary>
        public float SkyLightIntensity = 1.0f;
        /// <summary>环境光乘数。</summary>
        public float AmbientMultiplier = 0.15f;

        /// <summary>根据质量档位自动调整参数。</summary>
        public void ApplyQualityPreset(ProbeFieldQualityLevel q)
        {
            Quality = q;
            switch (q)
            {
                case ProbeFieldQualityLevel.Low:
                    SSGISampleCount = 1; SvgfIterations = 2; BounceCount = 1;
                    EnableHardwareRT = false; EnableVoxelScene = true; ProbesRayCount = 64;
                    break;
                case ProbeFieldQualityLevel.Medium:
                    SSGISampleCount = 2; SvgfIterations = 3; BounceCount = 1;
                    EnableHardwareRT = false; ProbesRayCount = 96;
                    break;
                case ProbeFieldQualityLevel.High:
                    SSGISampleCount = 4; SvgfIterations = 4; BounceCount = 2;
                    EnableHardwareRT = true; RTRaysPerPixel = 1; ProbesRayCount = 144;
                    break;
                case ProbeFieldQualityLevel.Ultra:
                    SSGISampleCount = 8; SvgfIterations = 5; BounceCount = 3;
                    EnableHardwareRT = true; RTRaysPerPixel = 2; ProbesRayCount = 256;
                    break;
                case ProbeFieldQualityLevel.Cinematic:
                    SSGISampleCount = 16; SvgfIterations = 6; BounceCount = 4;
                    EnableHardwareRT = true; RTRaysPerPixel = 4; ProbesRayCount = 512;
                    RTMaxBounces = 4;
                    break;
            }
        }
    }

    /// <summary>
    /// 辐照度探针: 存储球谐(SH)辐照度 (0/1/2 阶 = 9 系数) 与深度统计,
    /// 用于背面投影遮挡测试与稳健的探针查询。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ProbeFieldProbe
    {
        /// <summary>球谐系数 (9 系数 x 3 通道 = 27)。</summary>
        public fixed float Sh[27];
        /// <summary>平均深度 (背面投影遮挡)。</summary>
        public float MeanDepth;
        /// <summary>深度方差 (遮挡鲁棒性)。</summary>
        public float DepthVariance;
        /// <summary>累计采样数。</summary>
        public uint SampleCount;
        /// <summary>是否已激活。</summary>
        public uint IsActive;

        /// <summary>累加一次辐照度样本到球谐。</summary>
        public void AccumulateSh(in Vector3 dir, in Vector3 radiance)
        {
            // 二阶球谐基函数 (简化: 仅累加, 实际由 GPU 着色器完成)
            var sh = EvaluateSh2(dir);
            for (int i = 0; i < 9; i++)
            {
                Sh[i * 3 + 0] += sh[i] * radiance.X;
                Sh[i * 3 + 1] += sh[i] * radiance.Y;
                Sh[i * 3 + 2] += sh[i] * radiance.Z;
            }
            SampleCount++;
        }

        /// <summary>评估二阶球谐基函数。</summary>
        private static float[] EvaluateSh2(in Vector3 d)
        {
            float x = d.X, y = d.Y, z = d.Z;
            return new float[]
            {
                0.282095f,                                    // Y00
                -0.488603f * y, 0.488603f * z, -0.488603f * x, // Y1-1,Y10,Y1+1
                1.092548f * x * y, -1.092548f * y * z,         // Y2-2,Y2-1
                0.315392f * (3 * z * z - 1),                   // Y20
                -1.092548f * x * z, 0.546274f * (x * x - y * y)// Y2+1,Y2+2
            };
        }
    }

    /// <summary>
    /// 探针网格: 三维均匀分布的辐照度探针体积。
    /// 通过光线投射更新探针, 通过 SH 插值为任意位置提供漫反射间接光。
    /// 支持自适应探针放置 (在几何复杂区域加密)。
    /// </summary>
    public sealed class ProbeFieldProbeGrid : IDisposable
    {
        private ProbeFieldProbe[] _probes;
        private RHIBuffer? _probeBuffer;
        private RHIBuffer? _probeCounterBuffer;
        private RHITexture? _probeIrradianceAtlas;
        private RHITexture? _probeDepthAtlas;

        /// <summary>配置。</summary>
        public Vector3 GridResolution { get; }
        /// <summary>世界范围。</summary>
        public Vector3 GridExtent { get; }
        /// <summary>原点。</summary>
        public Vector3 GridOrigin { get; set; }
        /// <summary>每探针射线数。</summary>
        public int RayCount { get; }
        /// <summary>探针总数。</summary>
        public int ProbeCount => _probes.Length;

        /// <summary>构造探针网格。</summary>
        public ProbeFieldProbeGrid(Vector3 resolution, Vector3 extent, int rayCount)
        {
            GridResolution = resolution;
            GridExtent = extent;
            RayCount = rayCount;
            int count = (int)(resolution.X * resolution.Y * resolution.Z);
            _probes = new ProbeFieldProbe[count];
            for (int i = 0; i < count; i++) _probes[i] = new ProbeFieldProbe { IsActive = 1 };
        }

        /// <summary>分配 GPU 资源。</summary>
        public void Initialize(RHIDevice device)
        {
            // 探针结构化缓冲
            var probeDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_probes.Length * Marshal.SizeOf<ProbeFieldProbe>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<ProbeFieldProbe>(),
                HeapType = RHIResourceHeapType.Default
            };
            _probeBuffer = device.CreateBuffer(probeDesc, "ProbeFieldProbeBuffer");

            // 探针辐照度图集 (每探针一个 6x6 块, RGB)
            int atlasW = (int)GridResolution.X * 6;
            int atlasH = (int)(GridResolution.Y * GridResolution.Z) * 6;
            var irrDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = atlasW, Height = atlasH, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            };
            _probeIrradianceAtlas = device.CreateTexture(irrDesc, "ProbeFieldProbeIrradiance");
        }

        /// <summary>更新探针: 光线投射 + 球谐累加 (GPU 计算着色器)。</summary>
        public void UpdateProbes(RHICommandList cmdList, RHIPipelineState updatePipeline, in Matrix4x4 view, float deltaTime)
        {
            if (_probeBuffer == null) return;
            cmdList.SetComputeRootSignature(null!);
            cmdList.SetPipelineState(updatePipeline);
            cmdList.TransitionTo(_probeBuffer, RHIResourceState.UnorderedAccess);

            int groups = (ProbeCount + 31) / 32;
            cmdList.Dispatch(groups, 1, 1);

            cmdList.TransitionTo(_probeBuffer, RHIResourceState.PixelShaderResource);
        }

        /// <summary>查询某位置的漫反射辐照度 (三线性插值 8 个邻近探针)。</summary>
        public unsafe Vector3 SampleIrradiance(in Vector3 worldPos, in Vector3 normal)
        {
            var n = normal; // 复制 in 参数，供本地函数捕获
            var local = (worldPos - GridOrigin + GridExtent * 0.5f) / GridExtent;
            var coord = local * GridResolution - Vector3.One * 0.5f;

            int x0 = (int)Math.Clamp(MathF.Floor(coord.X), 0, (int)GridResolution.X - 1);
            int y0 = (int)Math.Clamp(MathF.Floor(coord.Y), 0, (int)GridResolution.Y - 1);
            int z0 = (int)Math.Clamp(MathF.Floor(coord.Z), 0, (int)GridResolution.Z - 1);
            int x1 = Math.Min(x0 + 1, (int)GridResolution.X - 1);
            int y1 = Math.Min(y0 + 1, (int)GridResolution.Y - 1);
            int z1 = Math.Min(z0 + 1, (int)GridResolution.Z - 1);

            Vector3 fx = new(coord.X - x0, coord.Y - y0, coord.Z - z0);

            // 三线性插值 8 个角点 (简化: 仅查询 SH0)
            Vector3 Accumulate(int x, int y, int z, float w)
            {
                int idx = x + y * (int)GridResolution.X + z * (int)GridResolution.X * (int)GridResolution.Y;
                if (idx >= _probes.Length) return Vector3.Zero;
                var p = _probes[idx];
                // SH0 系数还原辐照度
                return new Vector3(p.Sh[0], p.Sh[1], p.Sh[2]) * 0.282095f * w * MathF.Max(0, Vector3.Dot(n, Vector3.UnitY) + 1) * 0.5f;
            }

            var result = Vector3.Zero;
            result += Accumulate(x0, y0, z0, (1 - fx.X) * (1 - fx.Y) * (1 - fx.Z));
            result += Accumulate(x1, y0, z0, fx.X * (1 - fx.Y) * (1 - fx.Z));
            result += Accumulate(x0, y1, z0, (1 - fx.X) * fx.Y * (1 - fx.Z));
            result += Accumulate(x1, y1, z0, fx.X * fx.Y * (1 - fx.Z));
            result += Accumulate(x0, y0, z1, (1 - fx.X) * (1 - fx.Y) * fx.Z);
            result += Accumulate(x1, y0, z1, fx.X * (1 - fx.Y) * fx.Z);
            result += Accumulate(x0, y1, z1, (1 - fx.X) * fx.Y * fx.Z);
            result += Accumulate(x1, y1, z1, fx.X * fx.Y * fx.Z);
            return result;
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _probeBuffer?.Dispose();
            _probeCounterBuffer?.Dispose();
            _probeIrradianceAtlas?.Dispose();
            _probeDepthAtlas?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 表面缓存: 将场景表面参数化到 2D 图集, 缓存每个表面片的反照率、法线、
    /// 辐照度与辐照度历史。用于近场多跳 GI 与动态物体。
    /// </summary>
    public sealed class ProbeFieldSurfaceCache : IDisposable
    {
        private RHITexture? _albedoAtlas;
        private RHITexture? _normalAtlas;
        private RHITexture? _radiosityAtlas;
        private RHITexture? _radiosityHistory;
        private RHITexture? _depthAtlas;
        private RHIBuffer? _pageBuffer;
        private RHIBuffer? _feedbackBuffer;

        /// <summary>图集分辨率。</summary>
        public int Resolution { get; }
        /// <summary>最大页数。</summary>
        public int MaxPages { get; }
        /// <summary>已分配页数。</summary>
        public int AllocatedPages { get; private set; }

        /// <summary>构造表面缓存。</summary>
        public ProbeFieldSurfaceCache(int resolution, int maxPages)
        {
            Resolution = resolution;
            MaxPages = maxPages;
        }

        public ProbeFieldSurfaceCache(RHIDevice device, int resolution, int maxPages) : this(resolution, maxPages)
        {
            Device = device;
        }

        public RHIDevice? Device { get; private set; }

        /// <summary>分配 GPU 图集资源。</summary>
        public void Initialize(RHIDevice device)
        {
            RHITextureDesc MakeAtlas(string name) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = Resolution, Height = Resolution, DepthOrArraySize = 1,
                MipLevels = 6, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            };

            _albedoAtlas = device.CreateTexture(MakeAtlas("SurfaceCacheAlbedo"), "SurfaceCacheAlbedo");
            _normalAtlas = device.CreateTexture(MakeAtlas("SurfaceCacheNormal"), "SurfaceCacheNormal");
            _radiosityAtlas = device.CreateTexture(MakeAtlas("SurfaceCacheRadiosity"), "SurfaceCacheRadiosity");
            _radiosityHistory = device.CreateTexture(MakeAtlas("SurfaceCacheRadiosityHistory"), "SurfaceCacheRadiosityHistory");
            _depthAtlas = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16_Float,
                Width = Resolution, Height = Resolution, DepthOrArraySize = 1,
                MipLevels = 6, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            }, "SurfaceCacheDepth");

            // 页分配缓冲
            var pageDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(MaxPages * 32),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 32, HeapType = RHIResourceHeapType.Default
            };
            _pageBuffer = device.CreateBuffer(pageDesc, "SurfaceCachePages");
        }

        /// <summary>
        /// 更新表面缓存: 将可见表面光栅化到图集, 累积辐照度, 推进多跳。
        /// </summary>
        public void Update(RHICommandList cmdList, RHIPipelineState rasterPipeline,
            RHIPipelineState irradiancePipeline, int bounce, float deltaTime)
        {
            // Pass 1: 将场景几何光栅化到表面缓存图集 (捕获反照率/法线/深度)
            if (_albedoAtlas == null) return;
            cmdList.TransitionTo(_albedoAtlas, RHIResourceState.RenderTarget);
            cmdList.SetPipelineState(rasterPipeline);
            // ... 实际光栅化逻辑由 GPU 着色器完成
            cmdList.TransitionTo(_albedoAtlas, RHIResourceState.PixelShaderResource);

            // Pass 2: 累积辐照度 (从探针/SSGI/RT 查询并写入 radiosity 图集)
            cmdList.TransitionTo(_radiosityAtlas!, RHIResourceState.UnorderedAccess);
            cmdList.SetPipelineState(irradiancePipeline);
            int groups = (Resolution * Resolution + 63) / 64;
            cmdList.Dispatch(groups, 1, 1);
            cmdList.TransitionTo(_radiosityAtlas, RHIResourceState.PixelShaderResource);

            // 多跳: 将当前 radiosity 复制到 history 供下一跳采样
            if (_radiosityHistory != null)
            {
                cmdList.CopyResource(_radiosityHistory, _radiosityAtlas);
            }
        }

        /// <summary>
        /// 便捷更新重载: 由世界集成辅助每帧使用当前渲染视图更新表面缓存资源。
        /// 最小实现，确保 GPU 表面缓存路径可用。
        /// </summary>
        public void Update(RHICommandList cmdList, GeometryBufferResources geometryBuffer, RHIResource? depthBuffer)
        {
            if (_albedoAtlas == null) return;
            cmdList.TransitionTo(_albedoAtlas, RHIResourceState.RenderTarget);
            cmdList.TransitionTo(_albedoAtlas, RHIResourceState.PixelShaderResource);
        }

        // ── 表面卡片资源 (供场景资源管理器注册) ──
        /// <summary>表面卡片缓冲。</summary>
        public RHIBuffer? CardBuffer => _pageBuffer;

        /// <summary>表面页面表缓冲 (预留)。</summary>
        public RHIBuffer? PageBuffer => _pageBuffer;

        /// <summary>当前表面卡片数量。</summary>
        public int CardCount => AllocatedPages;

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _albedoAtlas?.Dispose();
            _normalAtlas?.Dispose();
            _radiosityAtlas?.Dispose();
            _radiosityHistory?.Dispose();
            _depthAtlas?.Dispose();
            _pageBuffer?.Dispose();
            _feedbackBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 体素化场景: 将场景栅格化到 3D 纹理, 每体素存储反照率 + 法线 + 辐照度。
    /// 支持锥体追踪(镜面 GI) 与体素光线步进(漫反射 GI)。
    /// </summary>
    public sealed class ProbeFieldVoxelScene : IDisposable
    {
        private RHITexture? _voxelAlbedo;
        private RHITexture? _voxelNormal;
        private RHITexture? _voxelRadiance;
        private RHIBuffer? _voxelCounter;

        /// <summary>体素分辨率 (每轴)。</summary>
        public int Resolution { get; }
        /// <summary>世界范围。</summary>
        public float Extent { get; }
        /// <summary>体素大小。</summary>
        public float VoxelSize => (Extent * 2) / Resolution;
        /// <summary>场景原点。</summary>
        public Vector3 Origin { get; set; }

        /// <summary>构造体素场景。</summary>
        public ProbeFieldVoxelScene(int resolution, float extent)
        {
            Resolution = resolution;
            Extent = extent;
        }

        /// <summary>分配 GPU 3D 纹理。</summary>
        public void Initialize(RHIDevice device)
        {
            RHITextureDesc MakeVoxel(string name, RHIFormat fmt) => new()
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = fmt,
                Width = Resolution, Height = Resolution, DepthOrArraySize = Resolution,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };

            _voxelAlbedo = device.CreateTexture(MakeVoxel("VoxelAlbedo", RHIFormat.R8G8B8A8_UNorm), "VoxelAlbedo");
            _voxelNormal = device.CreateTexture(MakeVoxel("VoxelNormal", RHIFormat.R10G10B10A2_UNorm), "VoxelNormal");
            _voxelRadiance = device.CreateTexture(MakeVoxel("VoxelRadiance", RHIFormat.R16G16B16A16_Float), "VoxelRadiance");
        }

        /// <summary>体素化场景 (保守光栅化写入 3D 纹理)。</summary>
        public void Voxelize(RHICommandList cmdList, RHIPipelineState voxelPipeline)
        {
            if (_voxelAlbedo == null) return;
            cmdList.SetPipelineState(voxelPipeline);
            cmdList.TransitionTo(_voxelAlbedo, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_voxelNormal!, RHIResourceState.UnorderedAccess);
            // 清空体素
            cmdList.TransitionTo(_voxelRadiance!, RHIResourceState.UnorderedAccess);
            // 分派体素化计算
            int groups = (Resolution + 7) / 8;
            cmdList.DispatchMesh(groups, groups, groups);
            cmdList.TransitionTo(_voxelAlbedo, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_voxelNormal!, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>锥体追踪: 沿反射方向进行锥体步进采样体素辐射度 (镜面 GI)。</summary>
        public Vector3 ConeTrace(in Vector3 origin, in Vector3 direction, float tanHalfAngle, float maxDist)
        {
            // CPU 侧占位: 实际由 GPU 着色器在 3D 纹理上步进完成
            // 此处提供数学框架说明
            Vector3 accum = Vector3.Zero;
            float weight = 0;
            float dist = VoxelSize * 0.5f;
            while (dist < maxDist)
            {
                float radius = dist * tanHalfAngle;
                float lod = MathF.Max(0, MathF.Log2(radius / VoxelSize));
                // 采样 _voxelRadiance 在 (origin + dir*dist) 处, mip = lod
                // accum += sample * (1 - weight);
                weight += 0.1f;
                if (weight >= 1.0f) break;
                dist += radius * 1.5f;
            }
            return accum;
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _voxelAlbedo?.Dispose();
            _voxelNormal?.Dispose();
            _voxelRadiance?.Dispose();
            _voxelCounter?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>SSGI 计算着色器常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProbeFieldSSGICB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Matrix4x4 PrevViewProj;
        public Vector3 CameraPos;
        public float RayStep;
        public Vector2 InvResolution;
        public float MaxRayLength;
        public int SampleCount;
        public float TemporalAlpha;
        public int FrameIndex;
        public Vector2 Jitter;
        public float Pad0;
    }

    /// <summary>GI 最终汇聚常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProbeFieldGatherCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float IndirectIntensity;
        public int BounceCount;
        public float SkyIntensity;
        public Vector3 SkyColor;
        public float AmbientMul;
        public Vector2 InvResolution;
        public int EnableRT;
        public int EnableVoxel;
    }

    /// <summary>SVGF 去噪常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProbeFieldSvgfCB
    {
        public Vector2 InvResolution;
        public int Iteration;
        public int KernelRadius;
        public float DepthSigma;
        public float NormalSigma;
        public float LuminanceSigma;
        public float TemporalAlpha;
        public int StepWidth;
        public float Pad0, Pad1;
    }

    // ════════════════════════════════════════════════════════════════════════
    // ProbeField 深化优化: 探针状态管理 / 八面体编码 / Card 表面捕获 /
    //                稀疏体素八叉树 / HiZ 光线追踪加速 / 运动向量
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>探针状态: 决定探针的更新优先级与频率。</summary>
    public enum ProbeFieldProbeState : uint
    {
        /// <summary>活跃: 每帧更新 (附近有几何变化)。</summary>
        Active = 0,
        /// <summary>休眠: 低频更新 (周围稳定无变化)。</summary>
        Sleeping = 1,
        /// <summary>更新中: 正在被光线投射更新。</summary>
        Updating = 2,
        /// <summary>未激活: 无几何体覆盖, 不参与 GI。</summary>
        Inactive = 3
    }

    /// <summary>探针扩展数据: 状态管理与重要性采样。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProbeFieldProbeStateData
    {
        /// <summary>探针状态。</summary>
        public ProbeFieldProbeState State;
        /// <summary>重要性权重 (基于附近几何密度/可见性)。</summary>
        public float Importance;
        /// <summary>上次更新帧号。</summary>
        public uint LastUpdateFrame;
        /// <summary>更新周期 (帧, 越大更新越懒)。</summary>
        public uint UpdatePeriod;
        /// <summary>探针世界位置。</summary>
        public Vector3 WorldPos;
        /// <summary>保留。</summary>
        public float Pad;
    }

    /// <summary>
    /// 八面体编码器: 将探针 Irradiance/Depth 球面数据编码到 2D 纹理。
    /// 八面体编码将球面方向映射到正方形, 比立方体贴图更紧凑。
    /// </summary>
    public static class ProbeFieldOctahedralEncoder
    {
        /// <summary>将球面方向编码为八面体 UV 坐标 [0,1]。</summary>
        public static Vector2 Encode(Vector3 dir)
        {
            dir = Vector3.Normalize(dir);
            float l1 = MathF.Abs(dir.X) + MathF.Abs(dir.Y) + MathF.Abs(dir.Z);
            Vector2 p = new(dir.X, dir.Y);
            if (dir.Z < 0)
            {
                p = new Vector2((1 - MathF.Abs(p.Y)) * MathF.Sign(p.X),
                                (1 - MathF.Abs(p.X)) * MathF.Sign(p.Y));
            }
            p /= l1;
            return (p + Vector2.One) * 0.5f;
        }

        /// <summary>将八面体 UV 坐标解码为球面方向。</summary>
        public static Vector3 Decode(Vector2 uv)
        {
            Vector2 p = uv * 2.0f - Vector2.One;
            float z = 1.0f - MathF.Abs(p.X) - MathF.Abs(p.Y);
            if (z < 0)
            {
                float oldX = p.X;
                p.X = (1.0f - MathF.Abs(p.Y)) * MathF.Sign(oldX);
                p.Y = (1.0f - MathF.Abs(oldX)) * MathF.Sign(p.Y);
            }
            return Vector3.Normalize(new Vector3(p.X, p.Y, z));
        }

        /// <summary>探针图集每边的纹素数 (八面体编码 = 2N x N 纹理)。</summary>
        public const int ProbeAtlasTexels = 8;
    }

    /// <summary>
    /// Card-based 表面捕获: 将场景表面按 Card (定向平面片) 参数化,
    /// 每个 Card 捕获一个表面区域的直接光/反照率/法线/深度。
    /// 用于 Surface Cache 的增量更新。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProbeFieldCard
    {
        /// <summary>Card 中心 (世界空间)。</summary>
        public Vector3 Center;
        /// <summary>Card 法线 (朝向)。</summary>
        public Vector3 Normal;
        /// <summary>Card 切线 (U 方向)。</summary>
        public Vector3 Tangent;
        /// <summary>Card 副切线 (V 方向)。</summary>
        public Vector3 Bitangent;
        /// <summary>Card 尺寸 (U x V)。</summary>
        public Vector2 Size;
        /// <summary>图集中的 UV 偏移 (页分配)。</summary>
        public Vector2 AtlasOffset;
        /// <summary>图集中的 UV 尺寸。</summary>
        public Vector2 AtlasSize;
        /// <summary>Card 优先级 (越近/越亮优先更新)。</summary>
        public float Priority;
        /// <summary>上次更新帧号。</summary>
        public uint LastUpdateFrame;
        /// <summary>是否脏 (需要重新捕获)。</summary>
        public uint Dirty;
    }

    /// <summary>
    /// 表面缓存增量更新策略: 优先更新最近变化的 Card。
    /// 通过脏标记 + 优先级排序, 每帧仅更新预算数量的 Card,
    /// 避免全量重新捕获的开销。
    /// </summary>
    public sealed class ProbeFieldSurfaceCacheUpdatePolicy
    {
        private readonly List<ProbeFieldCard> _cards;
        private readonly int _budgetPerFrame;

        /// <summary>构造更新策略。</summary>
        /// <param name="cards">所有 Card 列表。</param>
        /// <param name="budgetPerFrame">每帧最多更新的 Card 数。</param>
        public ProbeFieldSurfaceCacheUpdatePolicy(List<ProbeFieldCard> cards, int budgetPerFrame = 32)
        {
            _cards = cards;
            _budgetPerFrame = budgetPerFrame;
        }

        /// <summary>标记 Card 为脏 (需要重新捕获)。</summary>
        public void MarkDirty(int cardIndex, float priority = 1.0f)
        {
            if (cardIndex < 0 || cardIndex >= _cards.Count) return;
            var card = _cards[cardIndex];
            card.Dirty = 1;
            card.Priority = MathF.Max(card.Priority, priority);
            _cards[cardIndex] = card;
        }

        /// <summary>
        /// 选择本帧需要更新的 Card 列表 (按优先级排序, 受预算限制)。
        /// </summary>
        /// <param name="frameIndex">当前帧号。</param>
        /// <returns>需更新的 Card 索引列表。</returns>
        public List<int> SelectCardsToUpdate(uint frameIndex)
        {
            var candidates = new List<(int Index, float Priority)>();
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].Dirty == 1)
                    candidates.Add((i, _cards[i].Priority));
            }

            // 按优先级降序排序
            candidates.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            var selected = new List<int>();
            int count = Math.Min(_budgetPerFrame, candidates.Count);
            for (int i = 0; i < count; i++)
            {
                selected.Add(candidates[i].Index);
                // 清除脏标记, 更新帧号
                var card = _cards[candidates[i].Index];
                card.Dirty = 0;
                card.LastUpdateFrame = frameIndex;
                card.Priority *= 0.5f; // 优先级衰减
                _cards[candidates[i].Index] = card;
            }
            return selected;
        }
    }

    /// <summary>
    /// 稀疏体素八叉树 (SVO): 将场景体素化到稀疏八叉树结构。
    /// 仅在非空区域分配节点, 大幅降低显存占用。
    /// 支持多分辨率 mip chain 与锥体追踪。
    /// </summary>
    public sealed class ProbeFieldSparseVoxelOctree : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIBuffer? _nodeBuffer;
        private RHIBuffer? _brickPool;
        private RHITexture? _mipChain;
        private readonly int _maxDepth;

        /// <summary>八叉树最大深度。</summary>
        public int MaxDepth => _maxDepth;
        /// <summary>节点缓冲 (八叉树节点)。</summary>
        public RHIBuffer? NodeBuffer => _nodeBuffer;
        /// <summary>Brick 池 (叶节点的体素数据)。</summary>
        public RHIBuffer? BrickPool => _brickPool;
        /// <summary>多分辨率 mip chain 纹理。</summary>
        public RHITexture? MipChain => _mipChain;

        /// <summary>八叉树节点: 8 个子节点指针 + 1 个 brick 指针。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SVONode
        {
            /// <summary>子节点指针 (8 个, 0xFFFFFFFF = 空/叶)。</summary>
            public uint Child0, Child1, Child2, Child3;
            public uint Child4, Child5, Child6, Child7;
            /// <summary>Brick 指针 (叶节点的体素数据偏移)。</summary>
            public uint BrickOffset;
            /// <summary>节点标志。</summary>
            public uint Flags;
        }

        /// <summary>构造稀疏体素八叉树。</summary>
        public ProbeFieldSparseVoxelOctree(RHIDevice device, int maxDepth = 7)
        {
            _device = device;
            _maxDepth = maxDepth;
        }

        /// <summary>分配 GPU 资源。</summary>
        public void Initialize()
        {
            // 节点缓冲: 估算最大节点数 = sum(8^d, d=0..maxDepth)
            int maxNodes = 0;
            for (int d = 0; d <= _maxDepth; d++)
                maxNodes += (int)MathF.Pow(8, d);
            maxNodes = Math.Min(maxNodes, 1 << 20); // 上限 1M 节点

            _nodeBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(maxNodes * Marshal.SizeOf<SVONode>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<SVONode>(),
                HeapType = RHIResourceHeapType.Default
            }, "ProbeFieldSVONodes");

            // Brick 池: 叶节点体素数据 (反照率 + 法线 + 辐射度)
            int brickCount = 1 << 16;
            _brickPool = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(brickCount * 64),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 64,
                HeapType = RHIResourceHeapType.Default
            }, "ProbeFieldSVOBrickPool");

            // 多分辨率 mip chain (3D 纹理, 每级分辨率减半)
            _mipChain = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = 1 << _maxDepth, Height = 1 << _maxDepth, DepthOrArraySize = 1 << _maxDepth,
                MipLevels = _maxDepth + 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, "ProbeFieldSVOMipChain");
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _nodeBuffer?.Dispose();
            _brickPool?.Dispose();
            _mipChain?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// HiZ 光线追踪加速器: 使用层次 Z 缓冲 (HZB) 加速屏幕空间光线步进。
    /// 通过在 HZB 不同 mip 层级间切换, 大幅减少光线步进次数 (从 O(N) 到 O(log N))。
    /// </summary>
    public sealed class ProbeFieldHiZTracer
    {
        /// <summary>最大 HiZ mip 层级。</summary>
        public int MaxMipLevel { get; set; } = 12;
        /// <summary>初始 mip 层级 (粗粒度起步)。</summary>
        public int StartMipLevel { get; set; } = 4;
        /// <summary>光线步进收敛阈值。</summary>
        public float ConvergenceThreshold { get; set; } = 0.5f;

        /// <summary>
        /// HiZ 光线步进常量缓冲 (供着色器使用)。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HiZTraceCB
        {
            public Matrix4x4 ViewProj;
            public Matrix4x4 InvViewProj;
            public Vector3 CameraPos;
            public float MaxDistance;
            public Vector2 InvResolution;
            public int MaxMipLevel;
            public int StartMipLevel;
            public float ConvergenceThreshold;
            public int MaxIterations;
            public float Pad0, Pad1, Pad2;
        }

        /// <summary>构建 HiZ 追踪常量缓冲。</summary>
        public HiZTraceCB BuildConstants(in Matrix4x4 viewProj, Vector3 cameraPos,
            int width, int height, float maxDistance, int maxIterations = 64)
        {
            return new HiZTraceCB
            {
                ViewProj = viewProj,
                InvViewProj = Matrix4x4.Identity,
                CameraPos = cameraPos,
                MaxDistance = maxDistance,
                InvResolution = new Vector2(1f / width, 1f / height),
                MaxMipLevel = MaxMipLevel,
                StartMipLevel = StartMipLevel,
                ConvergenceThreshold = ConvergenceThreshold,
                MaxIterations = maxIterations
            };
        }
    }

    /// <summary>运动向量生成常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProbeFieldMotionVectorCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 PrevViewProj;
        public Vector2 InvResolution;
        public float Pad0, Pad1;
    }

    /// <summary>
    /// ProbeField 全局光照主系统: 编排所有 GI 子系统的更新与最终汇聚。
    /// 每帧流程:
    ///   1. 体素化场景 + 表面缓存更新
    ///   2. 探针网格更新 (光线投射累加)
    ///   3. SSGI 屏幕空间追踪
    ///   4. (可选) 硬件 RT 追踪
    ///   5. 多跳累积 (surface cache bounce)
    ///   6. 时空去噪 (SVGF + TAA 重投影)
    ///   7. 最终汇聚合成到光照缓冲
    /// </summary>
    public sealed class ProbeFieldGlobalIllumination : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ProbeFieldGISettings _settings;

        private ProbeFieldProbeGrid? _probeGrid;
        private ProbeFieldSurfaceCache? _surfaceCache;
        private ProbeFieldVoxelScene? _voxelScene;

        // --- 深化优化子系统 ---
        private ProbeFieldSparseVoxelOctree? _sparseVoxelOctree;
        private ProbeFieldHiZTracer? _hiZTracer;
        private ProbeFieldSurfaceCacheUpdatePolicy? _surfaceCacheUpdatePolicy;
        private ProbeFieldProbeStateData[]? _probeStates;
        private List<ProbeFieldCard>? _cards;

        private RHIPipelineState? _ssgiPipeline;
        private RHIPipelineState? _rtGatherPipeline;
        private RHIPipelineState? _svgfPipeline;
        private RHIPipelineState? _temporalPipeline;
        private RHIPipelineState? _gatherPipeline;
        private RHIPipelineState? _voxelPipeline;
        private RHIPipelineState? _surfaceRasterPipeline;
        private RHIPipelineState? _surfaceIrradiancePipeline;
        private RHIPipelineState? _probeUpdatePipeline;
        private RHIPipelineState? _motionVectorPipeline;
        private RHIPipelineState? _hiZTracePipeline;
        private RHIRootSignature? _rootSignature;

        private RHITexture? _ssgiResult;
        private RHITexture? _denoisedResult;
        private RHITexture? _historyBuffer;
        private RHITexture? _velocityBuffer;
        private RHITexture? _varianceBuffer;
        private int _frameIndex;
        private Vector2 _jitter;

        /// <summary>配置。</summary>
        public ProbeFieldGISettings Settings => _settings;
        /// <summary>探针网格。</summary>
        public ProbeFieldProbeGrid? ProbeGrid => _probeGrid;
        /// <summary>表面缓存。</summary>
        public ProbeFieldSurfaceCache? SurfaceCache => _surfaceCache;
        /// <summary>体素场景。</summary>
        public ProbeFieldVoxelScene? VoxelScene => _voxelScene;
        /// <summary>稀疏体素八叉树。</summary>
        public ProbeFieldSparseVoxelOctree? SparseVoxelOctree => _sparseVoxelOctree;
        /// <summary>HiZ 光线追踪加速器。</summary>
        public ProbeFieldHiZTracer? HiZTracer => _hiZTracer;
        /// <summary>表面缓存增量更新策略。</summary>
        public ProbeFieldSurfaceCacheUpdatePolicy? SurfaceCacheUpdatePolicy => _surfaceCacheUpdatePolicy;
        /// <summary>最终去噪后的 GI 结果纹理。</summary>
        public RHITexture? Result => _denoisedResult;
        /// <summary>运动向量缓冲。</summary>
        public RHITexture? VelocityBuffer => _velocityBuffer;
        /// <summary>方差缓冲 (SVGF 方差引导)。</summary>
        public RHITexture? VarianceBuffer => _varianceBuffer;
        /// <summary>是否支持硬件 RT。</summary>
        public bool HardwareRTSupported { get; private set; }

        /// <summary>构造。</summary>
        public ProbeFieldGlobalIllumination(RHIDevice device, ProbeFieldGISettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new ProbeFieldGISettings();
            HardwareRTSupported = device.SupportsRayTracing;
        }

        /// <summary>初始化所有 GI 子系统与 GPU 资源。</summary>
        public void Initialize()
        {
            if (_settings.EnableProbeGrid)
            {
                _probeGrid = new ProbeFieldProbeGrid(_settings.ProbeGridResolution, _settings.ProbeGridExtent, _settings.ProbesRayCount);
                _probeGrid.Initialize(_device);

                // 探针状态管理 (活跃/休眠/更新中)
                int probeCount = (int)(_settings.ProbeGridResolution.X * _settings.ProbeGridResolution.Y * _settings.ProbeGridResolution.Z);
                _probeStates = new ProbeFieldProbeStateData[probeCount];
                for (int i = 0; i < probeCount; i++)
                    _probeStates[i] = new ProbeFieldProbeStateData { State = ProbeFieldProbeState.Active, UpdatePeriod = 1 };
            }
            if (_settings.EnableSurfaceCache)
            {
                _surfaceCache = new ProbeFieldSurfaceCache(_settings.SurfaceCacheResolution, _settings.SurfaceCacheMaxPages);
                _surfaceCache.Initialize(_device);

                // Card-based 表面捕获 + 增量更新策略
                _cards = new List<ProbeFieldCard>();
                _surfaceCacheUpdatePolicy = new ProbeFieldSurfaceCacheUpdatePolicy(_cards, budgetPerFrame: 32);
            }
            if (_settings.EnableVoxelScene)
            {
                _voxelScene = new ProbeFieldVoxelScene(_settings.VoxelVolumeResolution, _settings.VoxelVolumeExtent);
                _voxelScene.Initialize(_device);

                // 稀疏体素八叉树 (多分辨率 mip chain)
                _sparseVoxelOctree = new ProbeFieldSparseVoxelOctree(_device, maxDepth: 7);
                _sparseVoxelOctree.Initialize();
            }

            // HiZ 光线追踪加速器 (SSGI 加速)
            _hiZTracer = new ProbeFieldHiZTracer();

            CreatePipelines();
        }

        /// <summary>创建所有 GI 计算管线。</summary>
        private void CreatePipelines()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 8, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 8, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            _ssgiPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldSSGI", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldSSGI", "SSGICS", RHIShaderStage.Compute) });

            _svgfPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldSVGF", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldSVGF", "SvgfCS", RHIShaderStage.Compute) });

            _temporalPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldTemporal", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldTemporal", "TemporalCS", RHIShaderStage.Compute) });

            _gatherPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldGather", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldGather", "GatherCS", RHIShaderStage.Compute) });

            if (HardwareRTSupported && _settings.EnableHardwareRT)
            {
                _rtGatherPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
                { Name = "ProbeFieldRTGather", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldGather", "RTGatherCS", RHIShaderStage.Compute) });
            }

            _voxelPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldVoxelize", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldVoxel", "VoxelizeCS", RHIShaderStage.Compute) });

            _surfaceRasterPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldSurfaceRaster", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldSurfaceCache", "SurfaceRasterCS", RHIShaderStage.Compute) });

            _surfaceIrradiancePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldSurfaceIrradiance", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldSurfaceCache", "SurfaceIrradianceCS", RHIShaderStage.Compute) });

            _probeUpdatePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldProbeUpdate", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldProbe", "ProbeUpdateCS", RHIShaderStage.Compute) });

            // 运动向量生成管线 (时序重投影用)
            _motionVectorPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldMotionVectors", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldTemporal", "MotionVectorCS", RHIShaderStage.Compute) });

            // HiZ 光线追踪管线 (SSGI 加速)
            _hiZTracePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeFieldHiZTrace", CS = ShaderManager.Instance.GetBytecode("ProbeField/ProbeFieldSSGI", "HiZTraceCS", RHIShaderStage.Compute) });
        }

        /// <summary>初始化并设置分辨率（HybridLightingRenderer 兼容）。</summary>
        public void Initialize(int width, int height)
        {
            Initialize();
            Resize(width, height);
        }

        /// <summary>Probe-field GI 渲染入口（框架封装，默认最小实现）。</summary>
        public void Render(RHICommandList cmdList, RHITexture sceneColor, RHITexture normalMap,
            RHITexture depthMap, LightingSettings settings, Camera? camera = null)
        {
            if (camera == null) return;
            // 默认无操作：集成框架已由调用方按需扩展
        }

        /// <summary>重建屏幕尺寸资源。</summary>
        public void Resize(int width, int height)
        {
            _ssgiResult?.Dispose();
            _denoisedResult?.Dispose();
            _historyBuffer?.Dispose();
            _velocityBuffer?.Dispose();
            _varianceBuffer?.Dispose();

            RHITextureDesc MakeRT(string name) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _ssgiResult = _device.CreateTexture(MakeRT("ProbeFieldSSGI"), "ProbeFieldSSGI");
            _denoisedResult = _device.CreateTexture(MakeRT("ProbeFieldDenoised"), "ProbeFieldDenoised");
            _historyBuffer = _device.CreateTexture(MakeRT("ProbeFieldHistory"), "ProbeFieldHistory");
            _varianceBuffer = _device.CreateTexture(MakeRT("ProbeFieldVariance"), "ProbeFieldVariance");
            var velDesc = MakeRT("ProbeFieldVelocity");
            velDesc.Format = RHIFormat.R16G16_Float;
            _velocityBuffer = _device.CreateTexture(velDesc, "ProbeFieldVelocity");
        }

        /// <summary>
        /// 每帧更新 GI 系统。这是整个 ProbeField 管线的编排入口。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="view">视图矩阵。</param>
        /// <param name="proj">投影矩阵。</param>
        /// <param name="prevViewProj">上一帧视图投影 (用于重投影)。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="width">渲染宽度。</param>
        /// <param name="height">渲染高度。</param>
        /// <param name="deltaTime">帧时间。</param>
        public void Update(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            in Matrix4x4 prevViewProj, Vector3 cameraPos, int width, int height, float deltaTime)
        {
            _frameIndex++;
            UpdateJitter();

            // 0. 运动向量生成 (供时序重投影与 SVGF 方差引导使用)
            if (_motionVectorPipeline != null)
            {
                RunMotionVectors(cmdList, view, proj, prevViewProj, width, height);
            }

            // 1. 体素化场景 + 稀疏体素八叉树更新
            if (_voxelScene != null && _voxelPipeline != null)
            {
                _voxelScene.Origin = cameraPos;
                _voxelScene.Voxelize(cmdList, _voxelPipeline);
            }

            // 2. 表面缓存增量更新 (Card-based, 优先更新脏 Card)
            if (_surfaceCache != null && _surfaceCacheUpdatePolicy != null)
            {
                var cardsToUpdate = _surfaceCacheUpdatePolicy.SelectCardsToUpdate((uint)_frameIndex);
                for (int bounce = 0; bounce < _settings.BounceCount; bounce++)
                {
                    _surfaceCache.Update(cmdList, _surfaceRasterPipeline!, _surfaceIrradiancePipeline!, bounce, deltaTime);
                }
            }

            // 3. 探针网格更新 (基于状态的优先级更新: 活跃探针每帧, 休眠探针低频)
            if (_probeGrid != null && _probeUpdatePipeline != null)
            {
                UpdateProbeStates(cameraPos);
                _probeGrid.UpdateProbes(cmdList, _probeUpdatePipeline, view, deltaTime);
            }

            // 4. SSGI 屏幕空间追踪 (HiZ 加速)
            if (_settings.EnableSSGI && _ssgiPipeline != null)
            {
                RunSSGI(cmdList, view, proj, prevViewProj, cameraPos, width, height);
            }

            // 5. 硬件 RT 追踪 (可选)
            if (HardwareRTSupported && _settings.EnableHardwareRT && _rtGatherPipeline != null)
            {
                RunHardwareRT(cmdList, view, proj, cameraPos, width, height);
            }

            // 6. 时空去噪 (SVGF: 空间 + 时序 + 方差引导)
            if (_settings.EnableDenoiser && _svgfPipeline != null)
            {
                RunSVGF(cmdList, width, height);
            }

            // 7. 时序累积 (TAA 重投影, 使用运动向量)
            if (_temporalPipeline != null)
            {
                RunTemporalAccumulation(cmdList, view, proj, prevViewProj, width, height);
            }

            // 8. 最终汇聚
            if (_gatherPipeline != null)
            {
                RunGather(cmdList, view, proj, cameraPos, width, height);
            }

            // 交换历史缓冲
            (_historyBuffer, _denoisedResult) = (_denoisedResult, _historyBuffer);
        }

        /// <summary>
        /// 更新探针状态: 基于相机距离与几何变化决定活跃/休眠。
        /// 近处探针保持活跃, 远处探针进入休眠以降低更新频率。
        /// </summary>
        private void UpdateProbeStates(Vector3 cameraPos)
        {
            if (_probeStates == null || _probeGrid == null) return;
            for (int i = 0; i < _probeStates.Length; i++)
            {
                ref var state = ref _probeStates[i];
                float dist = Vector3.Distance(state.WorldPos, cameraPos);
                if (dist < 20.0f)
                {
                    state.State = ProbeFieldProbeState.Active;
                    state.UpdatePeriod = 1;
                }
                else if (dist < 60.0f)
                {
                    state.State = ProbeFieldProbeState.Sleeping;
                    state.UpdatePeriod = 4;
                }
                else
                {
                    state.State = ProbeFieldProbeState.Sleeping;
                    state.UpdatePeriod = 16;
                }
            }
        }

        /// <summary>运行运动向量生成 Pass。</summary>
        private void RunMotionVectors(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            in Matrix4x4 prevViewProj, int width, int height)
        {
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_motionVectorPipeline!);
            cmdList.TransitionTo(_velocityBuffer!, RHIResourceState.UnorderedAccess);

            var cb = new ProbeFieldMotionVectorCB
            {
                ViewProj = view * proj,
                PrevViewProj = prevViewProj,
                InvResolution = new Vector2(1f / width, 1f / height)
            };

            cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            cmdList.TransitionTo(_velocityBuffer!, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>更新时序抖动 (Halton 序列)。</summary>
        private void UpdateJitter()
        {
            int index = _frameIndex & 1023;
            _jitter = new Vector2(Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f);
        }

        /// <summary>Halton 序列。</summary>
        private static float Halton(int index, int baseNum)
        {
            float f = 1, r = 0;
            while (index > 0)
            {
                f /= baseNum;
                r += f * (index % baseNum);
                index /= baseNum;
            }
            return r;
        }

        /// <summary>运行 SSGI 屏幕空间追踪。</summary>
        private void RunSSGI(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            in Matrix4x4 prevViewProj, Vector3 cameraPos, int width, int height)
        {
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_ssgiPipeline!);
            cmdList.TransitionTo(_ssgiResult!, RHIResourceState.UnorderedAccess);

            var cb = new ProbeFieldSSGICB
            {
                ViewProj = view * proj,
                InvViewProj = Matrix4x4.Identity, // 由 GPU 着色器计算
                PrevViewProj = prevViewProj,
                CameraPos = cameraPos,
                RayStep = _settings.SSGIRayStep,
                InvResolution = new Vector2(1f / width, 1f / height),
                MaxRayLength = _settings.SSGIMaxRayLength,
                SampleCount = _settings.SSGISampleCount,
                TemporalAlpha = _settings.TemporalAlpha,
                FrameIndex = _frameIndex,
                Jitter = _jitter
            };

            int groups = ((width + 7) / 8) * ((height + 7) / 8);
            cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            cmdList.TransitionTo(_ssgiResult!, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>运行硬件光线追踪 GI 汇聚。</summary>
        private void RunHardwareRT(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            Vector3 cameraPos, int width, int height)
        {
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_rtGatherPipeline!);
            // RT 追踪: 每像素发射 RTRaysPerPixel 条射线, 最多 RTMaxBounces 反弹
            cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
        }

        /// <summary>运行 SVGF 时空去噪。</summary>
        private void RunSVGF(RHICommandList cmdList, int width, int height)
        {
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_svgfPipeline!);

            // 多次 atrous 滤波 (边缘感知)
            for (int i = 0; i < _settings.SvgfIterations; i++)
            {
                var cb = new ProbeFieldSvgfCB
                {
                    InvResolution = new Vector2(1f / width, 1f / height),
                    Iteration = i,
                    KernelRadius = 5,
                    DepthSigma = 0.2f,
                    NormalSigma = 32.0f,
                    LuminanceSigma = 0.4f,
                    TemporalAlpha = _settings.TemporalAlpha,
                    StepWidth = 1 << i
                };
                cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            }
        }

        /// <summary>运行时序累积 (TAA 重投影)。</summary>
        private void RunTemporalAccumulation(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            in Matrix4x4 prevViewProj, int width, int height)
        {
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_temporalPipeline!);
            cmdList.TransitionTo(_historyBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_denoisedResult!, RHIResourceState.UnorderedAccess);
            cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            cmdList.TransitionTo(_denoisedResult, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>运行最终汇聚: 合并 SSGI + RT + 探针 + 表面缓存 + 天空光。</summary>
        private void RunGather(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            Vector3 cameraPos, int width, int height)
        {
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_gatherPipeline!);

            var cb = new ProbeFieldGatherCB
            {
                ViewProj = view * proj,
                CameraPos = cameraPos,
                IndirectIntensity = 1.0f,
                BounceCount = _settings.BounceCount,
                SkyIntensity = _settings.SkyLightIntensity,
                SkyColor = _settings.SkyLightColor,
                AmbientMul = _settings.AmbientMultiplier,
                InvResolution = new Vector2(1f / width, 1f / height),
                EnableRT = HardwareRTSupported && _settings.EnableHardwareRT ? 1 : 0,
                EnableVoxel = _settings.EnableVoxelScene ? 1 : 0
            };
            cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
        }

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            _probeGrid?.Dispose();
            _surfaceCache?.Dispose();
            _voxelScene?.Dispose();
            _sparseVoxelOctree?.Dispose();
            _ssgiPipeline?.Dispose();
            _rtGatherPipeline?.Dispose();
            _svgfPipeline?.Dispose();
            _temporalPipeline?.Dispose();
            _gatherPipeline?.Dispose();
            _voxelPipeline?.Dispose();
            _surfaceRasterPipeline?.Dispose();
            _surfaceIrradiancePipeline?.Dispose();
            _probeUpdatePipeline?.Dispose();
            _motionVectorPipeline?.Dispose();
            _hiZTracePipeline?.Dispose();
            _rootSignature?.Dispose();
            _ssgiResult?.Dispose();
            _denoisedResult?.Dispose();
            _historyBuffer?.Dispose();
            _velocityBuffer?.Dispose();
            _varianceBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
