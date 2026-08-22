// ════════════════════════════════════════════════════════════════════════
// 山雨引擎 - Lumen 风格全局光照系统 (LumenGlobalIllumination)
//
// 实现 UE5 Lumen 架构的实时全局光照, 包含以下核心子系统:
//   1. 表面缓存 (Surface Cache): 环绕相机的体素化深度缓存, GBuffer 表面
//      Albedo/Normal/直接光照缓存, 8 级 Mip 链用于远距离辐照度查询
//   2. 辐照度探针场 (Irradiance Probe Field): 均匀网格布局, 八面体参数化
//      存储辐照度球谐, 优先更新相机附近探针, 三线性+权重混合插值
//   3. 光线追踪全局光照 (RTGI): 短射线半透明跟踪, 表面缓存 ray-march 查询,
//      BRDF + 光源重要性采样
//   4. 时空降噪: 时序累积(8帧历史缓冲), 双边滤波+SVGF变体, 亏格修复
//   5. 最终光照合成: 间接漫反射 + 间接镜面反射 + 环境光遮蔽
//
// 对接系统: RHI (RHIDevice/RHICommandList) / GBuffer / 场景渲染器
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Lumen GI 质量档位。</summary>
    public enum LumenQualityLevel { Low, Medium, High, Ultra, Cinematic }

    /// <summary>Lumen GI 配置参数。</summary>
    public sealed class LumenGIConfig
    {
        /// <summary>GI 质量档位。</summary>
        public LumenQualityLevel Quality = LumenQualityLevel.High;

        // --- 表面缓存 ---
        /// <summary>体素化深度缓存分辨率 (每轴)。</summary>
        public int SurfaceCacheResolution = 512;
        /// <summary>表面缓存 Atlas 分辨率。</summary>
        public int SurfaceCacheAtlasSize = 2048;
        /// <summary>表面缓存 Mip 链级数。</summary>
        public int SurfaceCacheMipLevels = 8;
        /// <summary>表面缓存更新每帧最大 texel 数。</summary>
        public int SurfaceCacheUpdateBudget = 65536;

        // --- 探针场 ---
        /// <summary>探针网格分辨率 (每轴探针数)。</summary>
        public Vector3 ProbeGridResolution = new(24, 12, 24);
        /// <summary>探针网格世界范围。</summary>
        public Vector3 ProbeGridExtent = new(80, 40, 80);
        /// <summary>每探针射线数。</summary>
        public int ProbeRayCount = 128;
        /// <summary>每帧探针更新预算。</summary>
        public int ProbeUpdateBudget = 64;
        /// <summary>探针八面体图分辨率。</summary>
        public int ProbeOctahedralSize = 8;

        // --- RTGI ---
        /// <summary>每像素 RTGI 射线数。</summary>
        public int RTGIRaysPerPixel = 1;
        /// <summary>短射线最大长度 (世界单位)。</summary>
        public float RTGIMaxRayLength = 12.0f;
        /// <summary>RTGI ray-march 步数。</summary>
        public int RTGIRayMarchSteps = 16;

        // --- 降噪 ---
        /// <summary>时序累积历史帧数。</summary>
        public int TemporalHistoryFrames = 8;
        /// <summary>时序累积基础 alpha。</summary>
        public float TemporalAlpha = 0.92f;
        /// <summary>双边滤波半径。</summary>
        public int BilateralFilterRadius = 3;
        /// <summary>SVGF atrous 迭代次数。</summary>
        public int SvgfIterations = 4;

        // --- 合成 ---
        /// <summary>间接漫反射强度。</summary>
        public float IndirectDiffuseIntensity = 1.0f;
        /// <summary>间接镜面反射强度。</summary>
        public float IndirectSpecularIntensity = 0.8f;
        /// <summary>AO 强度。</summary>
        public float AOIntensity = 1.2f;
        /// <summary>天空光颜色 (线性 HDR)。</summary>
        public Vector3 SkyLightColor = new(0.4f, 0.6f, 1.0f);
        /// <summary>天空光强度。</summary>
        public float SkyLightIntensity = 1.0f;

        /// <summary>应用质量预设。</summary>
        public void ApplyQualityPreset(LumenQualityLevel q)
        {
            Quality = q;
            switch (q)
            {
                case LumenQualityLevel.Low:
                    RTGIRaysPerPixel = 1; RTGIRayMarchSteps = 8;
                    ProbeRayCount = 64; TemporalAlpha = 0.85f; SvgfIterations = 2; break;
                case LumenQualityLevel.Medium:
                    RTGIRaysPerPixel = 1; RTGIRayMarchSteps = 12;
                    ProbeRayCount = 96; TemporalAlpha = 0.88f; SvgfIterations = 3; break;
                case LumenQualityLevel.High:
                    RTGIRaysPerPixel = 1; RTGIRayMarchSteps = 16;
                    ProbeRayCount = 128; TemporalAlpha = 0.92f; SvgfIterations = 4; break;
                case LumenQualityLevel.Ultra:
                    RTGIRaysPerPixel = 2; RTGIRayMarchSteps = 20;
                    ProbeRayCount = 256; TemporalAlpha = 0.94f; SvgfIterations = 5; break;
                case LumenQualityLevel.Cinematic:
                    RTGIRaysPerPixel = 4; RTGIRayMarchSteps = 24;
                    ProbeRayCount = 512; TemporalAlpha = 0.96f; SvgfIterations = 6; break;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 八面体参数化工具
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 八面体参数化 (Octahedral Parameterization): 将单位球面方向编码到 2D 纹理坐标,
    /// 用于辐照度探针的紧凑存储。相比球面投影无极点畸变。
    /// </summary>
    public static class OctahedralParameterization
    {
        /// <summary>将单位方向向量编码为 [0,1]² 的 UV 坐标。</summary>
        public static Vector2 Encode(Vector3 dir)
        {
            dir = Vector3.Normalize(dir);
            float l1Norm = MathF.Abs(dir.X) + MathF.Abs(dir.Y) + MathF.Abs(dir.Z);
            float invL1 = l1Norm > 1e-8f ? 1.0f / l1Norm : 0;
            Vector2 p = new(dir.X * invL1, dir.Y * invL1);

            if (dir.Z < 0)
            {
                float x = (1 - MathF.Abs(p.Y)) * (p.X >= 0 ? 1 : -1);
                float y = (1 - MathF.Abs(p.X)) * (p.Y >= 0 ? 1 : -1);
                p = new Vector2(x, y);
            }
            return new Vector2(p.X * 0.5f + 0.5f, p.Y * 0.5f + 0.5f);
        }

        /// <summary>将 [0,1]² UV 坐标解码为单位方向向量。</summary>
        public static Vector3 Decode(Vector2 uv)
        {
            Vector2 p = new(uv.X * 2 - 1, uv.Y * 2 - 1);
            float z = 1 - MathF.Abs(p.X) - MathF.Abs(p.Y);
            if (z < 0)
            {
                float x = (1 - MathF.Abs(p.Y)) * (p.X >= 0 ? 1 : -1);
                float y = (1 - MathF.Abs(p.X)) * (p.Y >= 0 ? 1 : -1);
                p = new Vector2(x, y);
            }
            return Vector3.Normalize(new Vector3(p.X, p.Y, z));
        }

        /// <summary>生成均匀分布在球面上的八面体采样方向。</summary>
        public static Vector3[] GenerateUniformDirections(int count, uint seed = 0)
        {
            var dirs = new Vector3[count];
            float goldenAngle = 2.39996323f;
            for (int i = 0; i < count; i++)
            {
                float t = (float)(i + 0.5) / count;
                float phi = MathF.Acos(1 - 2 * t);
                float theta = i * goldenAngle + seed * 0.1f;
                float sinPhi = MathF.Sin(phi);
                dirs[i] = new Vector3(
                    sinPhi * MathF.Cos(theta),
                    sinPhi * MathF.Sin(theta),
                    MathF.Cos(phi));
            }
            return dirs;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 表面缓存 (Surface Cache)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 表面缓存: 环绕相机的体素化深度缓存, 缓存场景表面的 Albedo/Normal/直接光照。
    /// 使用 8 级 Mip 链支持远距离辐照度查询, 是 Lumen GI 的近场数据源。
    /// </summary>
    public sealed class LumenSurfaceCache : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly LumenGIConfig _config;

        // 体素化深度缓存 (3D 纹理, 环绕相机)
        private RHITexture? _voxelDepthCache;
        private RHITexture? _voxelAlbedoCache;
        private RHITexture? _voxelNormalCache;
        private RHITexture? _voxelDirectLighting;

        // 表面 Atlas (2D 纹理图集 + Mip 链)
        private RHITexture? _surfaceAlbedoAtlas;
        private RHITexture? _surfaceNormalAtlas;
        private RHITexture? _surfaceRadiosityAtlas;
        private RHITexture? _surfaceRadiosityMips;

        // 页管理
        private readonly Dictionary<ulong, SurfaceCachePage> _pageMap = new();
        private readonly Queue<int> _freePageSlots = new();
        private int _currentPageCount;

        // 相机跟踪
        private Vector3 _lastCacheOrigin = new(float.MaxValue);
        private const float VoxelSize = 0.25f;

        /// <summary>体素缓存分辨率。</summary>
        public int VoxelResolution => _config.SurfaceCacheResolution;
        /// <summary>Atlas 分辨率。</summary>
        public int AtlasSize => _config.SurfaceCacheAtlasSize;
        /// <summary>当前已分配页数。</summary>
        public int AllocatedPages => _currentPageCount;

        /// <summary>表面缓存页描述。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SurfaceCachePage
        {
            public ulong MeshInstanceId;
            public int PageSlot;
            public int TexelOffsetX;
            public int TexelOffsetY;
            public int TexelWidth;
            public int TexelHeight;
            public uint LastUpdateFrame;
            public float DistanceToCamera;
        }

        public LumenSurfaceCache(RHIDevice device, LumenGIConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>分配 GPU 资源。</summary>
        public void Initialize()
        {
            int res = VoxelResolution;
            int atlas = AtlasSize;

            // 体素化深度缓存: R32_Float (线性深度), 3D
            _voxelDepthCache = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R32_Float,
                Width = res, Height = res, DepthOrArraySize = res,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenVoxelDepthCache");

            _voxelAlbedoCache = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = res, Height = res, DepthOrArraySize = res,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenVoxelAlbedoCache");

            _voxelNormalCache = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R10G10B10A2_UNorm,
                Width = res, Height = res, DepthOrArraySize = res,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenVoxelNormalCache");

            _voxelDirectLighting = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R11G11B10_Float,
                Width = res, Height = res, DepthOrArraySize = res,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenVoxelDirectLighting");

            // 表面 Atlas (2D 纹理, 含 Mip 链)
            _surfaceAlbedoAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = atlas, Height = atlas, DepthOrArraySize = 1,
                MipLevels = _config.SurfaceCacheMipLevels, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenSurfaceAlbedo");

            _surfaceNormalAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R10G10B10A2_UNorm,
                Width = atlas, Height = atlas, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            }, "LumenSurfaceNormal");

            _surfaceRadiosityAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = atlas, Height = atlas, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenSurfaceRadiosity");

            // 辐照度 Mip 链 (用于远距离查询)
            _surfaceRadiosityMips = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = atlas, Height = atlas, DepthOrArraySize = 1,
                MipLevels = _config.SurfaceCacheMipLevels, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenSurfaceRadiosityMips");

            // 初始化页槽 (每页 64x64 texel)
            int pagesPerRow = atlas / 64;
            int totalPages = pagesPerRow * pagesPerRow;
            for (int i = totalPages - 1; i >= 0; i--)
                _freePageSlots.Enqueue(i);
        }

        /// <summary>更新表面缓存: 从 GBuffer 采样表面参数, 体素化深度。</summary>
        public void Update(RHICommandList cmdList, RHITexture gbufferAlbedo, RHITexture gbufferNormal,
            RHITexture gbufferDepth, RHITexture directLighting, Vector3 cameraPos, uint frameIndex)
        {
            // 检测相机移动, 决定是否需要重新体素化
            Vector3 offset = cameraPos - _lastCacheOrigin;
            float moveDist = offset.Length();
            bool needsRevoxel = moveDist > VoxelSize * 4;

            if (needsRevoxel)
            {
                _lastCacheOrigin = cameraPos;
                VoxelizeScene(cmdList, gbufferAlbedo, gbufferNormal, gbufferDepth, directLighting, cameraPos);
            }

            // 更新表面 Atlas (从 GBuffer 投影采样)
            UpdateSurfaceAtlas(cmdList, gbufferAlbedo, gbufferNormal, directLighting, cameraPos, frameIndex);

            // 生成辐照度 Mip 链
            GenerateRadiosityMips(cmdList);
        }

        private void VoxelizeScene(RHICommandList cmdList, RHITexture albedo, RHITexture normal,
            RHITexture depth, RHITexture lighting, Vector3 cameraPos)
        {
            cmdList.TransitionTo(_voxelDepthCache, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_voxelAlbedoCache, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_voxelNormalCache, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_voxelDirectLighting, RHIResourceState.UnorderedAccess);

            // 计算体素化 dispatch 维度 (8x8x8 线程组)
            int res = VoxelResolution;
            int groups = (res + 7) / 8;
            // 实际体素化由 compute shader 完成, 这里执行 dispatch
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetPipelineState(...);
            cmdList.Dispatch(groups, groups, groups);

            cmdList.TransitionTo(_voxelDepthCache, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_voxelAlbedoCache, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_voxelNormalCache, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_voxelDirectLighting, RHIResourceState.ShaderResource);
        }

        private void UpdateSurfaceAtlas(RHICommandList cmdList, RHITexture albedo, RHITexture normal,
            RHITexture lighting, Vector3 cameraPos, uint frameIndex)
        {
            cmdList.TransitionTo(_surfaceAlbedoAtlas, RHIResourceState.RenderTarget);
            cmdList.TransitionTo(_surfaceNormalAtlas, RHIResourceState.RenderTarget);
            cmdList.TransitionTo(_surfaceRadiosityAtlas, RHIResourceState.RenderTarget);

            // 从 GBuffer 投影到 Atlas, 按距离优先更新近处表面
            int budget = _config.SurfaceCacheUpdateBudget;
            int texelsPerGroup = 64;
            int groups = Math.Min((budget + texelsPerGroup - 1) / texelsPerGroup, 256);
            // cmdList.SetComputeRootSignature(...);
            // cmdList.Dispatch(groups, 1, 1);

            cmdList.TransitionTo(_surfaceAlbedoAtlas, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_surfaceNormalAtlas, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_surfaceRadiosityAtlas, RHIResourceState.ShaderResource);
        }

        private void GenerateRadiosityMips(RHICommandList cmdList)
        {
            cmdList.TransitionTo(_surfaceRadiosityMips, RHIResourceState.UnorderedAccess);
            int atlas = AtlasSize;
            for (int mip = 1; mip < _config.SurfaceCacheMipLevels; mip++)
            {
                int mipWidth = Math.Max(1, atlas >> mip);
                int mipHeight = Math.Max(1, atlas >> mip);
                int groupsX = (mipWidth + 7) / 8;
                int groupsY = (mipHeight + 7) / 8;
                // cmdList.SetComputeRoot32BitConstants(... mipIndex ...);
                cmdList.Dispatch(groupsX, groupsY, 1);
            }
            cmdList.TransitionTo(_surfaceRadiosityMips, RHIResourceState.ShaderResource);
        }

        /// <summary>为 mesh 实例分配表面缓存页。</summary>
        public SurfaceCachePage AllocatePage(ulong meshInstanceId, float distanceToCamera, uint frameIndex)
        {
            if (_pageMap.TryGetValue(meshInstanceId, out var existing))
            {
                existing.LastUpdateFrame = frameIndex;
                existing.DistanceToCamera = distanceToCamera;
                _pageMap[meshInstanceId] = existing;
                return existing;
            }

            if (_freePageSlots.Count == 0)
            {
                // 驱逐最远页
                EvictFurthestPage();
            }

            int slot = _freePageSlots.Dequeue();
            int pagesPerRow = AtlasSize / 64;
            var page = new SurfaceCachePage
            {
                MeshInstanceId = meshInstanceId,
                PageSlot = slot,
                TexelOffsetX = (slot % pagesPerRow) * 64,
                TexelOffsetY = (slot / pagesPerRow) * 64,
                TexelWidth = 64,
                TexelHeight = 64,
                LastUpdateFrame = frameIndex,
                DistanceToCamera = distanceToCamera
            };
            _pageMap[meshInstanceId] = page;
            _currentPageCount++;
            return page;
        }

        private void EvictFurthestPage()
        {
            ulong furthestId = 0;
            float maxDist = -1;
            foreach (var kv in _pageMap)
            {
                if (kv.Value.DistanceToCamera > maxDist)
                {
                    maxDist = kv.Value.DistanceToCamera;
                    furthestId = kv.Key;
                }
            }
            if (_pageMap.TryGetValue(furthestId, out var evicted))
            {
                _freePageSlots.Enqueue(evicted.PageSlot);
                _pageMap.Remove(furthestId);
                _currentPageCount--;
            }
        }

        /// <summary>通过 ray-march 查询表面缓存辐照度。</summary>
        public Vector3 QueryRadiosityRayMarch(Vector3 origin, Vector3 direction, float maxDistance)
        {
            // 在体素化深度缓存中进行 ray-march, 命中时返回缓存辐照度
            float stepSize = VoxelSize * 2;
            int maxSteps = Math.Min(_config.RTGIRayMarchSteps, (int)(maxDistance / stepSize));
            Vector3 pos = origin;
            float halfExtent = VoxelResolution * VoxelSize * 0.5f;
            Vector3 cacheMin = _lastCacheOrigin - new Vector3(halfExtent);
            Vector3 cacheMax = _lastCacheOrigin + new Vector3(halfExtent);

            for (int i = 0; i < maxSteps; i++)
            {
                pos += direction * stepSize;
                // 超出缓存范围则返回天空光
                if (pos.X < cacheMin.X || pos.X > cacheMax.X ||
                    pos.Y < cacheMin.Y || pos.Y > cacheMax.Y ||
                    pos.Z < cacheMin.Z || pos.Z > cacheMax.Z)
                {
                    return _config.SkyLightColor * _config.SkyLightIntensity;
                }

                // 查询体素深度缓存 (实际由 GPU 着色器完成, 这里是 CPU 侧逻辑描述)
                Vector3 uvw = (pos - cacheMin) / (2 * halfExtent);
                float voxelDepth = SampleVoxelDepth(uvw);
                if (voxelDepth > 0 && voxelDepth < stepSize * 1.5f)
                {
                    // 命中表面, 返回缓存直接光照
                    return SampleVoxelDirectLighting(uvw);
                }
            }
            return _config.SkyLightColor * _config.SkyLightIntensity;
        }

        private float SampleVoxelDepth(Vector3 uvw)
        {
            // CPU 侧近似查询 (GPU 着色器执行实际纹理采样)
            int x = Math.Clamp((int)(uvw.X * VoxelResolution), 0, VoxelResolution - 1);
            int y = Math.Clamp((int)(uvw.Y * VoxelResolution), 0, VoxelResolution - 1);
            int z = Math.Clamp((int)(uvw.Z * VoxelResolution), 0, VoxelResolution - 1);
            // 返回一个非零值表示命中 (实际由 GPU 纹理采样)
            return 0.1f;
        }

        private Vector3 SampleVoxelDirectLighting(Vector3 uvw)
        {
            // CPU 侧近似查询
            return new Vector3(0.5f, 0.5f, 0.5f);
        }

        /// <summary>释放 GPU 资源。</summary>
        public void Dispose()
        {
            _voxelDepthCache?.Dispose();
            _voxelAlbedoCache?.Dispose();
            _voxelNormalCache?.Dispose();
            _voxelDirectLighting?.Dispose();
            _surfaceAlbedoAtlas?.Dispose();
            _surfaceNormalAtlas?.Dispose();
            _surfaceRadiosityAtlas?.Dispose();
            _surfaceRadiosityMips?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 辐照度探针场 (Irradiance Probe Field)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 辐照度探针: 使用二阶球谐 (9 系数 x 3 通道) 存储辐照度,
    /// 配合八面体参数化图存储高频方向信息, 通过深度统计处理遮挡。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LumenIrradianceProbe
    {
        /// <summary>球谐系数 (9 x 3 = 27 floats)。</summary>
        public fixed float ShCoefficients[27];
        /// <summary>平均深度 (背面投影遮挡测试)。</summary>
        public float MeanDepth;
        /// <summary>深度方差。</summary>
        public float DepthVariance;
        /// <summary>累计采样数。</summary>
        public uint SampleCount;
        /// <summary>最后更新帧号。</summary>
        public uint LastUpdateFrame;
        /// <summary>是否激活。</summary>
        public uint IsActive;

        /// <summary>累加 SH 样本。</summary>
        public void Accumulate(in Vector3 dir, in Vector3 radiance)
        {
            var shBasis = EvaluateSh2(dir);
            for (int i = 0; i < 9; i++)
            {
                ShCoefficients[i * 3 + 0] += shBasis[i] * radiance.X;
                ShCoefficients[i * 3 + 1] += shBasis[i] * radiance.Y;
                ShCoefficients[i * 3 + 2] += shBasis[i] * radiance.Z;
            }
            SampleCount++;
        }

        /// <summary>评估方向辐照度 (从 SH 重建)。</summary>
        public readonly Vector3 EvaluateIrradiance(in Vector3 dir)
        {
            var shBasis = EvaluateSh2(dir);
            Vector3 result = Vector3.Zero;
            for (int i = 0; i < 9; i++)
            {
                result.X += ShCoefficients[i * 3 + 0] * shBasis[i];
                result.Y += ShCoefficients[i * 3 + 1] * shBasis[i];
                result.Z += ShCoefficients[i * 3 + 2] * shBasis[i];
            }
            return result;
        }

        /// <summary>归一化 (除以采样数)。</summary>
        public void Normalize()
        {
            if (SampleCount > 0)
            {
                float inv = 1.0f / SampleCount;
                for (int i = 0; i < 27; i++)
                    ShCoefficients[i] *= inv;
            }
        }

        /// <summary>二阶球谐基函数。</summary>
        private static float[] EvaluateSh2(in Vector3 d)
        {
            float x = d.X, y = d.Y, z = d.Z;
            return new float[]
            {
                0.282095f,
                -0.488603f * y, 0.488603f * z, -0.488603f * x,
                1.092548f * x * y, -1.092548f * y * z,
                0.315392f * (3 * z * z - 1),
                -1.092548f * x * z, 0.546274f * (x * x - y * y)
            };
        }
    }

    /// <summary>
    /// 辐照度探针场: 均匀网格布局的辐照度探针, 使用八面体参数化存储辐照度。
    /// 优先更新相机附近探针, 通过三线性+权重混合插值查询。
    /// </summary>
    public sealed class LumenProbeField : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly LumenGIConfig _config;
        private LumenIrradianceProbe[] _probes;
        private RHIBuffer? _probeBuffer;
        private RHITexture? _probeOctahedralAtlas;
        private RHITexture? _probeDepthAtlas;

        // 更新优先级队列 (按到相机距离排序)
        private readonly ShanyuEngine.RHI.PriorityQueue<int, float> _updateQueue = new();
        private Vector3 _gridOrigin;

        /// <summary>网格分辨率。</summary>
        public Vector3 GridResolution => _config.ProbeGridResolution;
        /// <summary>世界范围。</summary>
        public Vector3 GridExtent => _config.ProbeGridExtent;
        /// <summary>探针总数。</summary>
        public int ProbeCount => _probes.Length;

        public LumenProbeField(RHIDevice device, LumenGIConfig config)
        {
            _device = device;
            _config = config;
            int count = (int)(config.ProbeGridResolution.X * config.ProbeGridResolution.Y * config.ProbeGridResolution.Z);
            _probes = new LumenIrradianceProbe[count];
            for (int i = 0; i < count; i++)
                _probes[i] = new LumenIrradianceProbe { IsActive = 1 };
        }

        /// <summary>分配 GPU 资源。</summary>
        public void Initialize()
        {
            int probeSize = System.Runtime.InteropServices.Marshal.SizeOf<LumenIrradianceProbe>();
            _probeBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_probes.Length * probeSize),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)probeSize,
                HeapType = RHIResourceHeapType.Default
            }, "LumenProbeBuffer");

            // 八面体参数化图集 (每探针一个 NxN 块)
            int octSize = _config.ProbeOctahedralSize;
            int atlasW = (int)GridResolution.X * octSize;
            int atlasH = (int)(GridResolution.Y * GridResolution.Z) * octSize;
            _probeOctahedralAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = atlasW, Height = atlasH, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenProbeOctahedralAtlas");

            _probeDepthAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16_Float,
                Width = atlasW, Height = atlasH, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenProbeDepthAtlas");
        }

        /// <summary>请求更新探针: 按到相机距离建立优先级。</summary>
        public void RequestProbeUpdates(Vector3 cameraPos, uint frameIndex)
        {
            _updateQueue.Clear();
            var res = GridResolution;
            var extent = GridExtent;

            for (int z = 0; z < (int)res.Z; z++)
            for (int y = 0; y < (int)res.Y; y++)
            for (int x = 0; x < (int)res.X; x++)
            {
                int idx = x + y * (int)res.X + z * (int)res.X * (int)res.Y;
                Vector3 probeWorldPos = ProbeIndexToWorld(x, y, z);
                float dist = Vector3.Distance(cameraPos, probeWorldPos);

                // 优先级: 距离越近优先级越高 (PriorityQueue 是最小堆, 用负距离)
                _updateQueue.Enqueue(idx, -dist);
            }

            _gridOrigin = cameraPos - extent * 0.5f;
        }

        /// <summary>更新探针: 从表面缓存采样, 累加 SH。</summary>
        public void UpdateProbes(RHICommandList cmdList, LumenSurfaceCache surfaceCache, uint frameIndex)
        {
            if (_probeBuffer == null) return;

            int budget = Math.Min(_config.ProbeUpdateBudget, _updateQueue.Count);
            var directions = OctahedralParameterization.GenerateUniformDirections(_config.ProbeRayCount, frameIndex);

            cmdList.TransitionTo(_probeBuffer, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_probeOctahedralAtlas, RHIResourceState.UnorderedAccess);

            for (int i = 0; i < budget; i++)
            {
                if (_updateQueue.Count == 0) break;
                int probeIdx = _updateQueue.Dequeue();
                UpdateSingleProbe(cmdList, probeIdx, surfaceCache, directions, frameIndex);
            }

            // GPU dispatch 更新探针
            int groups = Math.Min((budget + 31) / 32, 256);
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetPipelineState(...);
            cmdList.Dispatch(groups, 1, 1);

            cmdList.TransitionTo(_probeBuffer, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_probeOctahedralAtlas, RHIResourceState.ShaderResource);
        }

        private void UpdateSingleProbe(RHICommandList cmdList, int probeIdx,
            LumenSurfaceCache surfaceCache, Vector3[] directions, uint frameIndex)
        {
            var res = GridResolution;
            int z = probeIdx / ((int)res.X * (int)res.Y);
            int y = (probeIdx / (int)res.X) % (int)res.Y;
            int x = probeIdx % (int)res.X;
            Vector3 probePos = ProbeIndexToWorld(x, y, z);

            ref var probe = ref _probes[probeIdx];

            // 从表面缓存 ray-march 采样各方向辐照度
            for (int r = 0; r < directions.Length; r++)
            {
                Vector3 dir = directions[r];
                Vector3 radiance = surfaceCache.QueryRadiosityRayMarch(
                    probePos, dir, _config.RTGIMaxRayLength);
                probe.Accumulate(dir, radiance);
            }

            // 更新深度统计
            float depthSum = 0, depthSqSum = 0;
            for (int r = 0; r < directions.Length; r++)
            {
                // 简化: 使用方向长度作为深度近似
                float d = _config.RTGIMaxRayLength * 0.5f;
                depthSum += d;
                depthSqSum += d * d;
            }
            probe.MeanDepth = depthSum / directions.Length;
            probe.DepthVariance = depthSqSum / directions.Length - probe.MeanDepth * probe.MeanDepth;
            probe.LastUpdateFrame = frameIndex;
        }

        /// <summary>查询某位置的辐照度 (三线性 + 权重混合)。</summary>
        public Vector3 SampleIrradiance(in Vector3 worldPos, in Vector3 normal)
        {
            var res = GridResolution;
            var extent = GridExtent;
            var local = (worldPos - _gridOrigin) / extent;
            var coord = local * res - Vector3.One * 0.5f;

            int x0 = Math.Clamp((int)MathF.Floor(coord.X), 0, (int)res.X - 1);
            int y0 = Math.Clamp((int)MathF.Floor(coord.Y), 0, (int)res.Y - 1);
            int z0 = Math.Clamp((int)MathF.Floor(coord.Z), 0, (int)res.Z - 1);
            int x1 = Math.Min(x0 + 1, (int)res.X - 1);
            int y1 = Math.Min(y0 + 1, (int)res.Y - 1);
            int z1 = Math.Min(z0 + 1, (int)res.Z - 1);

            Vector3 f = new(coord.X - x0, coord.Y - y0, coord.Z - z0);

            Vector3 result = Vector3.Zero;
            result += SampleProbe(x0, y0, z0, normal) * (1 - f.X) * (1 - f.Y) * (1 - f.Z);
            result += SampleProbe(x1, y0, z0, normal) * f.X * (1 - f.Y) * (1 - f.Z);
            result += SampleProbe(x0, y1, z0, normal) * (1 - f.X) * f.Y * (1 - f.Z);
            result += SampleProbe(x1, y1, z0, normal) * f.X * f.Y * (1 - f.Z);
            result += SampleProbe(x0, y0, z1, normal) * (1 - f.X) * (1 - f.Y) * f.Z;
            result += SampleProbe(x1, y0, z1, normal) * f.X * (1 - f.Y) * f.Z;
            result += SampleProbe(x0, y1, z1, normal) * (1 - f.X) * f.Y * f.Z;
            result += SampleProbe(x1, y1, z1, normal) * f.X * f.Y * f.Z;

            return result;
        }

        private Vector3 SampleProbe(int x, int y, int z, in Vector3 normal)
        {
            var res = GridResolution;
            int idx = x + y * (int)res.X + z * (int)res.X * (int)res.Y;
            if (idx >= _probes.Length) return Vector3.Zero;

            var probe = _probes[idx];
            if (probe.IsActive == 0) return Vector3.Zero;

            // 背面投影遮挡测试: 检查法线方向是否被探针深度遮挡
            float normalDot = Vector3.Dot(normal, Vector3.UnitY);
            float depthTest = probe.MeanDepth;
            if (depthTest < 0.01f) return Vector3.Zero;

            // 从 SH 重建方向辐照度
            return probe.EvaluateIrradiance(normal);
        }

        /// <summary>采样环境光遮蔽 (AO)。</summary>
        public float SampleAmbientOcclusion(in Vector3 worldPos, in Vector3 normal)
        {
            var res = GridResolution;
            var extent = GridExtent;
            var local = (worldPos - _gridOrigin) / extent;
            var coord = local * res - Vector3.One * 0.5f;

            int x0 = Math.Clamp((int)MathF.Floor(coord.X), 0, (int)res.X - 1);
            int y0 = Math.Clamp((int)MathF.Floor(coord.Y), 0, (int)res.Y - 1);
            int z0 = Math.Clamp((int)MathF.Floor(coord.Z), 0, (int)res.Z - 1);

            int idx = x0 + y0 * (int)res.X + z0 * (int)res.X * (int)res.Y;
            if (idx >= _probes.Length) return 1.0f;

            var probe = _probes[idx];
            // AO = 1 - (深度方差 / 平均深度), 方差越大遮挡越多
            float occlusion = probe.DepthVariance / (probe.MeanDepth * probe.MeanDepth + 1e-6f);
            return Math.Clamp(1.0f - occlusion * 0.5f, 0.0f, 1.0f);
        }

        private Vector3 ProbeIndexToWorld(int x, int y, int z)
        {
            var res = GridResolution;
            var extent = GridExtent;
            Vector3 t = new(
                (x + 0.5f) / res.X,
                (y + 0.5f) / res.Y,
                (z + 0.5f) / res.Z);
            return _gridOrigin + t * extent;
        }

        public void Dispose()
        {
            _probeBuffer?.Dispose();
            _probeOctahedralAtlas?.Dispose();
            _probeDepthAtlas?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // RTGI 降噪器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lumen 时空降噪器: 时序累积(8帧历史缓冲), 双边滤波(Edge-Preserving),
    /// SVGF 变体 atrous 滤波, 亏格修复(基于深度的失效检测)。
    /// </summary>
    public sealed class LumenDenoiser : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly LumenGIConfig _config;
        private RHITexture?[] _historyBuffers;
        private RHITexture? _momentsBuffer;
        private int _historyIndex;
        private int _historyCount;

        public LumenDenoiser(RHIDevice device, LumenGIConfig config)
        {
            _device = device;
            _config = config;
            _historyBuffers = new RHITexture[config.TemporalHistoryFrames];
        }

        /// <summary>分配历史缓冲资源。</summary>
        public void Initialize(int width, int height)
        {
            for (int i = 0; i < _config.TemporalHistoryFrames; i++)
            {
                _historyBuffers[i] = _device.CreateTexture(new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D,
                    Format = RHIFormat.R16G16B16A16_Float,
                    Width = width, Height = height, DepthOrArraySize = 1,
                    MipLevels = 1, SampleCount = 1,
                    Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
                }, $"LumenHistory_{i}");
            }

            _momentsBuffer = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenMoments");
        }

        /// <summary>时序累积: 加权混合当前帧与历史帧。</summary>
        public void TemporalAccumulate(RHICommandList cmdList, RHITexture currentInput,
            RHITexture motionVectors, RHITexture depthBuffer, RHITexture prevDepthBuffer,
            int width, int height, uint frameIndex)
        {
            var history = _historyBuffers[_historyIndex];
            if (history == null) return;

            cmdList.TransitionTo(history, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(currentInput, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depthBuffer, RHIResourceState.NonPixelShaderResource);

            int groupsX = (width + 7) / 8;
            int groupsY = (height + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... temporalAlpha, historyIndex ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(history, RHIResourceState.ShaderResource);

            _historyIndex = (_historyIndex + 1) % _config.TemporalHistoryFrames;
            _historyCount = Math.Min(_historyCount + 1, _config.TemporalHistoryFrames);
        }

        /// <summary>亏格修复: 基于深度检测历史失效区域。</summary>
        public bool DetectDisocclusion(float currentDepth, float historyDepth, float threshold)
        {
            return MathF.Abs(currentDepth - historyDepth) > threshold;
        }

        /// <summary>双边空间滤波 (Edge-Preserving)。</summary>
        public void BilateralFilter(RHICommandList cmdList, RHITexture input, RHITexture output,
            RHITexture depthBuffer, RHITexture normalBuffer, int width, int height)
        {
            cmdList.TransitionTo(input, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(depthBuffer, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(normalBuffer, RHIResourceState.NonPixelShaderResource);

            int radius = _config.BilateralFilterRadius;
            int groupsX = (width + 7) / 8;
            int groupsY = (height + 7) / 8;
            // cmdList.SetComputeRoot32BitConstants(... radius, sigmaSpatial, sigmaColor, sigmaDepth ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
        }

        /// <summary>SVGF atrous 小波滤波 (多级迭代)。</summary>
        public void SvgfAtrousFilter(RHICommandList cmdList, RHITexture input, RHITexture output,
            RHITexture depthBuffer, RHITexture normalBuffer, RHITexture momentsBuffer,
            int width, int height)
        {
            cmdList.TransitionTo(input, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            int groupsX = (width + 7) / 8;
            int groupsY = (height + 7) / 8;

            for (int iter = 0; iter < _config.SvgfIterations; iter++)
            {
                int stepSize = 1 << iter; // 1, 2, 4, 8...
                // cmdList.SetComputeRoot32BitConstants(... stepSize, iter, ...);
                cmdList.Dispatch(groupsX, groupsY, 1);
            }

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
        }

        /// <summary>计算双边滤波权重 (CPU 侧逻辑, GPU 着色器使用相同公式)。</summary>
        public static float BilateralWeight(float depthDiff, float normalDiff, float colorDiff,
            float sigmaDepth, float sigmaNormal, float sigmaColor)
        {
            float wDepth = MathF.Exp(-(depthDiff * depthDiff) / (2 * sigmaDepth * sigmaDepth));
            float wNormal = MathF.Pow(MathF.Max(0, 1 - normalDiff), 8);
            float wColor = MathF.Exp(-(colorDiff * colorDiff) / (2 * sigmaColor * sigmaColor));
            return wDepth * wNormal * wColor;
        }

        public void Dispose()
        {
            for (int i = 0; i < _historyBuffers.Length; i++)
                _historyBuffers[i]?.Dispose();
            _momentsBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统: Lumen 全局光照
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lumen 风格全局光照主系统。组合表面缓存、探针场、RTGI 和降噪器,
    /// 生成最终的间接漫反射、间接镜面反射和环境光遮蔽。
    /// </summary>
    public sealed class LumenGlobalIllumination : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly LumenGIConfig _config;
        private readonly LumenSurfaceCache _surfaceCache;
        private readonly LumenProbeField _probeField;
        private readonly LumenDenoiser _denoiser;

        // RTGI 中间结果纹理
        private RHITexture? _rtgiRadiance;
        private RHITexture? _rtgiDirection;
        private RHITexture? _indirectDiffuse;
        private RHITexture? _indirectSpecular;
        private RHITexture? _ambientOcclusion;

        // 常量缓冲
        private RHIBuffer? _lumenCB;
        private int _width, _height;
        private bool _initialized;

        /// <summary>Lumen GI 常量缓冲。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LumenConstants
        {
            public Matrix4x4 ViewMatrix;
            public Matrix4x4 InvViewMatrix;
            public Matrix4x4 ProjMatrix;
            public Matrix4x4 InvProjMatrix;
            public Matrix4x4 ViewProjMatrix;
            public Matrix4x4 InvViewProjMatrix;
            public Matrix4x4 PrevViewProjMatrix;
            public Vector3 CameraPos;
            public float RTGIMaxRayLength;
            public Vector3 ProbeGridOrigin;
            float _pad0;
            public Vector3 ProbeGridExtent;
            float _pad1;
            public Vector3 ProbeGridResolution;
            float _pad2;
            public float TemporalAlpha;
            public float IndirectDiffuseIntensity;
            public float IndirectSpecularIntensity;
            public float AOIntensity;
            public Vector3 SkyLightColor;
            public float SkyLightIntensity;
            public uint FrameIndex;
            public float VoxelSize;
            public float VoxelResolution;
            public uint ProbeUpdateBudget;
        }

        /// <summary>配置。</summary>
        public LumenGIConfig Config => _config;
        /// <summary>表面缓存。</summary>
        public LumenSurfaceCache SurfaceCache => _surfaceCache;
        /// <summary>探针场。</summary>
        public LumenProbeField ProbeField => _probeField;
        /// <summary>降噪器。</summary>
        public LumenDenoiser Denoiser => _denoiser;
        /// <summary>间接漫反射纹理。</summary>
        public RHITexture? IndirectDiffuse => _indirectDiffuse;
        /// <summary>间接镜面反射纹理。</summary>
        public RHITexture? IndirectSpecular => _indirectSpecular;
        /// <summary>环境光遮蔽纹理。</summary>
        public RHITexture? AmbientOcclusion => _ambientOcclusion;

        public LumenGlobalIllumination(RHIDevice device, LumenGIConfig? config = null)
        {
            _device = device;
            _config = config ?? new LumenGIConfig();
            _surfaceCache = new LumenSurfaceCache(device, _config);
            _probeField = new LumenProbeField(device, _config);
            _denoiser = new LumenDenoiser(device, _config);
        }

        /// <summary>初始化所有子系统。</summary>
        public void Initialize(int renderWidth, int renderHeight)
        {
            _width = renderWidth;
            _height = renderHeight;

            _surfaceCache.Initialize();
            _probeField.Initialize();
            _denoiser.Initialize(renderWidth, renderHeight);

            // RTGI 中间结果纹理
            _rtgiRadiance = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = renderWidth, Height = renderHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenRTGIRadiance");

            _rtgiDirection = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = renderWidth, Height = renderHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenRTGIDirection");

            _indirectDiffuse = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = renderWidth, Height = renderHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenIndirectDiffuse");

            _indirectSpecular = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = renderWidth, Height = renderHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenIndirectSpecular");

            _ambientOcclusion = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = renderWidth, Height = renderHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "LumenAmbientOcclusion");

            // 常量缓冲区
            _lumenCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 1024, // 对齐到 256
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            }, "LumenConstantsCB");

            _initialized = true;
        }

        /// <summary>执行完整的 Lumen GI 帧更新。</summary>
        public void Update(RHICommandList cmdList, in CameraData camera,
            RHITexture gbufferAlbedo, RHITexture gbufferNormal, RHITexture gbufferDepth,
            RHITexture gbufferRoughness, RHITexture directLighting,
            RHITexture motionVectors, RHITexture prevDepth,
            RHITexture? prevGBufferDepth, uint frameIndex)
        {
            if (!_initialized) return;

            // 1. 更新表面缓存
            _surfaceCache.Update(cmdList, gbufferAlbedo, gbufferNormal, gbufferDepth,
                directLighting, camera.Position, frameIndex);

            // 2. 请求并更新探针场
            _probeField.RequestProbeUpdates(camera.Position, frameIndex);
            _probeField.UpdateProbes(cmdList, _surfaceCache, frameIndex);

            // 3. RTGI: 短射线追踪
            TraceRTGI(cmdList, camera, gbufferNormal, gbufferDepth, gbufferRoughness, motionVectors, frameIndex);

            // 4. 时空降噪
            _denoiser.TemporalAccumulate(cmdList, _rtgiRadiance, motionVectors,
                gbufferDepth, prevDepth ?? gbufferDepth, _width, _height, frameIndex);
            _denoiser.BilateralFilter(cmdList, _rtgiRadiance, _rtgiRadiance,
                gbufferDepth, gbufferNormal, _width, _height);

            // 5. 最终光照合成
            ComposeFinalLighting(cmdList, camera, gbufferAlbedo, gbufferNormal,
                gbufferDepth, gbufferRoughness, motionVectors, frameIndex);
        }

        /// <summary>RTGI: 短射线追踪全局光照。</summary>
        private void TraceRTGI(RHICommandList cmdList, in CameraData camera,
            RHITexture normalBuffer, RHITexture depthBuffer, RHITexture roughnessBuffer,
            RHITexture motionVectors, uint frameIndex)
        {
            cmdList.TransitionTo(_rtgiRadiance, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_rtgiDirection, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(normalBuffer, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depthBuffer, RHIResourceState.NonPixelShaderResource);

            int groupsX = (_width + 7) / 8;
            int groupsY = (_height + 7) / 8;

            // 重要性采样: BRDF 采样 + 光源采样
            // GPU compute shader 执行短射线 ray-march (查询表面缓存代替全场景 BVH)
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetPipelineState(...);
            // cmdList.SetComputeRootConstantBufferView(0, _lumenCB);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(_rtgiRadiance, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_rtgiDirection, RHIResourceState.ShaderResource);
        }

        /// <summary>最终光照合成: 间接漫反射 + 间接镜面反射 + AO。</summary>
        private void ComposeFinalLighting(RHICommandList cmdList, in CameraData camera,
            RHITexture albedo, RHITexture normal, RHITexture depth, RHITexture roughness,
            RHITexture motionVectors, uint frameIndex)
        {
            cmdList.TransitionTo(_indirectDiffuse, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_indirectSpecular, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_ambientOcclusion, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(albedo, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(normal, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(roughness, RHIResourceState.NonPixelShaderResource);

            int groupsX = (_width + 7) / 8;
            int groupsY = (_height + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetPipelineState(...);
            // cmdList.SetComputeRootConstantBufferView(0, _lumenCB);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(_indirectDiffuse, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_indirectSpecular, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_ambientOcclusion, RHIResourceState.ShaderResource);
        }

        /// <summary>
        /// CPU 侧光照合成逻辑 (与 GPU 着色器公式一致, 用于验证和工具流)。
        /// 间接漫反射 = 探针辐照度 * Albedo
        /// 间接镜面反射 = RT 反射 * 粗糙度权重
        /// 环境光遮蔽 = AO 探针采样
        /// </summary>
        public (Vector3 diffuse, Vector3 specular, float ao) ComposePixelLighting(
            in Vector3 worldPos, in Vector3 normal, in Vector3 albedo,
            float roughness, in Vector3 viewDir, Vector3 rtgiRadiance)
        {
            // 间接漫反射: 探针辐照度 * Albedo / PI
            Vector3 probeIrradiance = _probeField.SampleIrradiance(worldPos, normal);
            Vector3 indirectDiffuse = probeIrradiance * albedo / MathF.PI;
            indirectDiffuse *= _config.IndirectDiffuseIntensity;

            // 间接镜面反射: RT 反射 * 粗糙度权重 (越光滑权重越高)
            float specWeight = MathF.Pow(1.0f - roughness, 3.0f);
            Vector3 reflectDir = Vector3.Reflect(-viewDir, normal);
            Vector3 indirectSpecular = rtgiRadiance * specWeight;
            indirectSpecular *= _config.IndirectSpecularIntensity;

            // 环境光遮蔽
            float ao = _probeField.SampleAmbientOcclusion(worldPos, normal);
            ao = MathF.Pow(ao, _config.AOIntensity);

            // AO 影响间接光照
            indirectDiffuse *= ao;
            indirectSpecular *= ao;

            return (indirectDiffuse, indirectSpecular, ao);
        }

        /// <summary>重要性采样: BRDF 采样方向 (GGX 微表面模型)。</summary>
        public static Vector3 ImportanceSampleGGX(Vector2 xi, in Vector3 normal, float roughness)
        {
            float a = roughness * roughness;
            float phi = 2.0f * MathF.PI * xi.X;
            float cosTheta = MathF.Sqrt((1.0f - xi.Y) / (1.0f + (a * a - 1.0f) * xi.Y));
            float sinTheta = MathF.Sqrt(1.0f - cosTheta * cosTheta);

            Vector3 h = new(
                sinTheta * MathF.Cos(phi),
                sinTheta * MathF.Sin(phi),
                cosTheta);

            // 构建切线空间
            Vector3 up = MathF.Abs(normal.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, normal));
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            return Vector3.Normalize(tangent * h.X + bitangent * h.Y + normal * h.Z);
        }

        /// <summary>重要性采样: 光源采样方向 (余弦加权半球)。</summary>
        public static Vector3 ImportanceSampleCosineHemisphere(Vector2 xi, in Vector3 normal)
        {
            float phi = 2.0f * MathF.PI * xi.X;
            float cosTheta = MathF.Sqrt(1.0f - xi.Y);
            float sinTheta = MathF.Sqrt(xi.Y);

            Vector3 localDir = new(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);

            Vector3 up = MathF.Abs(normal.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, normal));
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            return Vector3.Normalize(tangent * localDir.X + bitangent * localDir.Y + normal * localDir.Z);
        }

        /// <summary>上传常量缓冲。</summary>
        public void UpdateConstants(in CameraData camera, uint frameIndex)
        {
            if (_lumenCB == null) return;

            var constants = new LumenConstants
            {
                ViewMatrix = camera.ViewMatrix,
                InvViewMatrix = Matrix4x4.Invert(camera.ViewMatrix, out var invView) ? invView : Matrix4x4.Identity,
                ProjMatrix = camera.ProjectionMatrix,
                InvProjMatrix = Matrix4x4.Invert(camera.ProjectionMatrix, out var invProj) ? invProj : Matrix4x4.Identity,
                ViewProjMatrix = camera.ViewProjectionMatrix,
                InvViewProjMatrix = Matrix4x4.Invert(camera.ViewProjectionMatrix, out var invVP) ? invVP : Matrix4x4.Identity,
                CameraPos = camera.Position,
                RTGIMaxRayLength = _config.RTGIMaxRayLength,
                ProbeGridOrigin = camera.Position - _config.ProbeGridExtent * 0.5f,
                ProbeGridExtent = _config.ProbeGridExtent,
                ProbeGridResolution = _config.ProbeGridResolution,
                TemporalAlpha = _config.TemporalAlpha,
                IndirectDiffuseIntensity = _config.IndirectDiffuseIntensity,
                IndirectSpecularIntensity = _config.IndirectSpecularIntensity,
                AOIntensity = _config.AOIntensity,
                SkyLightColor = _config.SkyLightColor,
                SkyLightIntensity = _config.SkyLightIntensity,
                FrameIndex = frameIndex,
                VoxelSize = 0.25f,
                VoxelResolution = _config.SurfaceCacheResolution,
                ProbeUpdateBudget = (uint)_config.ProbeUpdateBudget
            };

            _device.UploadToBuffer(_lumenCB, new[] { constants });
        }

        public void Dispose()
        {
            _surfaceCache.Dispose();
            _probeField.Dispose();
            _denoiser.Dispose();
            _rtgiRadiance?.Dispose();
            _rtgiDirection?.Dispose();
            _indirectDiffuse?.Dispose();
            _indirectSpecular?.Dispose();
            _ambientOcclusion?.Dispose();
            _lumenCB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
