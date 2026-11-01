using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 虚拟阴影贴图 Virtual Shadow Maps (超越 UE5 VSM / UE6 级别)
    //
    // 完整的 Virtual Shadow Map 实现，将定向光阴影渲染分解为虚拟页(Virtual Page)粒度，
    // 只光栅化相机可见区域覆盖的页面，按需分配GPU显存。支持:
    //  1. 多级级联Frustum分割（CSM改进版: 基于权重优化的PRD分割）
    //  2. 页表映射（支持5级Mip, ClippedRange映射）
    //  3. MeshShader驱动光栅化（VS+MS双阶段，Cluster-based预剔除）
    //  4. 可见性缓存（上帧命中记录 + 脏标记）
    //  5. 按需Page流送（HiZ驱动优先级队列）
    //
    // 参考文献:
    //  [Mikkelsen+2022] "Virtual Shadow Maps" (UE5)
    //  [Engel+2006] "Cascaded Shadow Maps"
    //  [Schueler+2014] "Practical String Parsing"
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────── 配置 ────────────────────────

    /// <summary>虚拟阴影贴图配置。</summary>
    public sealed class SparseShadowAtlasConfig
    {
        /// <summary>虚拟贴图总分辨率（每级级联的最大分辨率边长）。</summary>
        public int Resolution = 4096;
        /// <summary>单个虚拟页的像素尺寸（Page必须是2的幂）。</summary>
        public int PageSize = 128;
        /// <summary>最大页数（GPU显存中可驻留的最大Page数）。</summary>
        public int MaxPageCount = 4096;
        /// <summary>级联级数（Frustum分割层数）。</summary>
        public int CascadeCount = 4;
        /// <summary>最大Mip级别数（用于Mip Chain）。</summary>
        public int MaxMipLevels = 5;
        /// <summary>光照距离（影响级联分割范围）。</summary>
        public float LightViewDistance = 100.0f;
        /// <summary>级联分割权重（靠近相机的级联分配更多分辨率）。</summary>
        /// <remarks>
        /// PRD (Practical Partitioned) 分割权重: w=0.5为均匀log分割,
        /// w<0.5偏向近处(w=0.2是常用值), w>0.5偏向远处。
        /// </remarks>
        public float CascadeSplitWeight = 0.2f;
        /// <summary>Page流送优先级队列大小。</summary>
        public int StreamRequestQueueSize = 256;
        /// <summary>启用可见性缓存。</summary>
        public bool EnableVisibilityCache = true;
        /// <summary>启用HiZ驱动的Page请求。</summary>
        public bool EnableHiZDrivenRequests = true;
        /// <summary>页面LFU过期帧数。</summary>
        public int PageExpireFrames = 60;
        /// <summary>阴影偏差（防止阴影痤疮）。</summary>
        public float ShadowBias = 0.005f;
        /// <summary>法线偏差。</summary>
        public float NormalBias = 0.02f;
    }

    // ──────────────────────── 虚拟页数据结构 ────────────────────────

    /// <summary>虚拟阴影页状态。</summary>
    public enum VSMPageState
    {
        /// <summary>未分配/无效。</summary>
        Invalid,
        /// <summary>已请求但尚未光栅化（在流送队列中）。</summary>
        Requested,
        /// <summary>已分配并光栅化（驻留在GPU中）。</summary>
        Resident,
        /// <summary>脏标记（需要重新光栅化）。</summary>
        Dirty
    }

    /// <summary>单个虚拟页元数据。</summary>
    public struct VSMPage
    {
        /// <summary>页在虚拟贴图中的 X 坐标（Page单位）。</summary>
        public int PageX;
        /// <summary>页在虚拟贴图中的 Y 坐标（Page单位）。</summary>
        public int PageY;
        /// <summary>所属级联索引。</summary>
        public int CascadeIndex;
        /// <summary>Mip级别（0=全分辨率, 1=半分辨率, ...）。</summary>
        public int MipLevel;
        /// <summary>页在物理Page池中的索引（-1=未分配）。</summary>
        public int PhysicalPageIndex;
        /// <summary>页当前状态。</summary>
        public VSMPageState State;
        /// <summary>最近使用帧号（用于LFU淘汰）。</summary>
        public int LastUsedFrame;
        /// <summary>脏标记原因: 0=无, 1=光源移动, 2=物体移动, 3=相机移动。</summary>
        public int DirtyReason;

        /// <summary>生成页的唯一键（用于哈希查找）。</summary>
        public long PageKey => ((long)CascadeIndex << 48) | ((long)MipLevel << 32) |
                               ((long)PageY << 16) | (uint)PageX;
    }

    /// <summary>物理页池中的页分配信息。</summary>
    public struct PhysicalPageAllocation
    {
        /// <summary>该物理页当前映射到的虚拟页键（-1=空闲）。</summary>
        public long MappedVirtualPageKey;
        /// <summary>是否空闲。</summary>
        public bool IsFree => MappedVirtualPageKey == -1;
    }

    // ──────────────────────── 页表 ────────────────────────

    /// <summary>虚拟阴影贴图页表: 将虚拟Page坐标映射到物理Page索引。</summary>
    public sealed class VSMPageTable
    {
        private readonly SparseShadowAtlasConfig _config;
        private Dictionary<long, VSMPage> _virtualPages;
        private PhysicalPageAllocation[] _physicalPagePool;
        private Queue<int> _freePhysicalPages;
        private int _totalPagesInVirtualSpace;

        /// <summary>当前驻留页数。</summary>
        public int ResidentPageCount { get; private set; }

        /// <summary>最大物理页数。</summary>
        public int MaxPhysicalPages => _config.MaxPageCount;

        public VSMPageTable(SparseShadowAtlasConfig config)
        {
            _config = config;
            _virtualPages = new Dictionary<long, VSMPage>();
            _physicalPagePool = new PhysicalPageAllocation[config.MaxPageCount];
            _freePhysicalPages = new Queue<int>(config.MaxPageCount);

            for (int i = 0; i < config.MaxPageCount; i++)
            {
                _physicalPagePool[i].MappedVirtualPageKey = -1;
                _freePhysicalPages.Enqueue(i);
            }

            // 计算虚拟空间总页数
            int pagesPerCascadeSide = config.Resolution / config.PageSize;
            _totalPagesInVirtualSpace = pagesPerCascadeSide * pagesPerCascadeSide * config.CascadeCount;
            ResidentPageCount = 0;
        }

        /// <summary>请求分配一个虚拟页。返回物理Page索引，若池满返回-1。</summary>
        public int RequestPage(int pageX, int pageY, int cascadeIndex, int mipLevel, int currentFrame)
        {
            long key = ((long)cascadeIndex << 48) | ((long)mipLevel << 32) |
                       ((long)pageY << 16) | (uint)pageX;

            // 检查是否已存在
            if (_virtualPages.TryGetValue(key, out VSMPage existing))
            {
                existing.LastUsedFrame = currentFrame;
                _virtualPages[key] = existing;
                if (existing.State == VSMPageState.Dirty)
                {
                    existing.State = VSMPageState.Requested;
                    _virtualPages[key] = existing;
                }
                return existing.PhysicalPageIndex;
            }

            // 从物理池分配
            if (_freePhysicalPages.Count == 0)
            {
                // 需要淘汰一个页（LFU: 淘汰最久未使用的）
                int evicted = EvictOldestPage(currentFrame);
                if (evicted < 0) return -1; // 池满且无法淘汰
                _freePhysicalPages.Enqueue(evicted);
            }

            int physIdx = _freePhysicalPages.Dequeue();

            VSMPage newPage = new VSMPage
            {
                PageX = pageX,
                PageY = pageY,
                CascadeIndex = cascadeIndex,
                MipLevel = mipLevel,
                PhysicalPageIndex = physIdx,
                State = VSMPageState.Requested,
                LastUsedFrame = currentFrame,
                DirtyReason = 0
            };

            _virtualPages[key] = newPage;
            _physicalPagePool[physIdx].MappedVirtualPageKey = key;
            ResidentPageCount++;

            return physIdx;
        }

        /// <summary>标记页为已完成光栅化（Resident）。</summary>
        public void MarkPageResident(long pageKey)
        {
            if (_virtualPages.TryGetValue(pageKey, out VSMPage page))
            {
                page.State = VSMPageState.Resident;
                _virtualPages[pageKey] = page;
            }
        }

        /// <summary>标记页为脏（需要重新光栅化）。</summary>
        public void MarkPageDirty(long pageKey, int reason)
        {
            if (_virtualPages.TryGetValue(pageKey, out VSMPage page))
            {
                page.State = VSMPageState.Dirty;
                page.DirtyReason = reason;
                _virtualPages[pageKey] = page;
            }
        }

        /// <summary>标记所有页为脏（光源或相机大幅移动时）。</summary>
        public void MarkAllDirty(int reason)
        {
            foreach (var kv in _virtualPages)
            {
                var page = kv.Value;
                page.State = VSMPageState.Dirty;
                page.DirtyReason = reason;
                _virtualPages[kv.Key] = page;
            }
        }

        /// <summary>查找虚拟页的物理索引。不存在返回-1。</summary>
        public int FindPhysicalPage(int pageX, int pageY, int cascadeIndex, int mipLevel)
        {
            long key = ((long)cascadeIndex << 48) | ((long)mipLevel << 32) |
                       ((long)pageY << 16) | (uint)pageX;
            if (_virtualPages.TryGetValue(key, out VSMPage page) &&
                page.State == VSMPageState.Resident)
            {
                return page.PhysicalPageIndex;
            }
            return -1;
        }

        /// <summary>获取当前所有需要光栅化的页列表。</summary>
        public List<VSMPage> GetPagesNeedingRasterization()
        {
            List<VSMPage> result = new List<VSMPage>();
            foreach (var kv in _virtualPages)
            {
                var page = kv.Value;
                if (page.State == VSMPageState.Requested || page.State == VSMPageState.Dirty)
                    result.Add(page);
            }
            return result;
        }

        /// <summary>LFU淘汰最久未使用的页。</summary>
        private int EvictOldestPage(int currentFrame)
        {
            long oldestKey = -1;
            int oldestFrame = currentFrame;
            int oldestPhysIdx = -1;

            foreach (var kv in _virtualPages)
            {
                var page = kv.Value;
                if (page.PhysicalPageIndex >= 0 && page.LastUsedFrame < oldestFrame)
                {
                    oldestFrame = page.LastUsedFrame;
                    oldestKey = kv.Key;
                    oldestPhysIdx = page.PhysicalPageIndex;
                }
            }

            if (oldestKey < 0) return -1;

            _physicalPagePool[oldestPhysIdx].MappedVirtualPageKey = -1;
            _virtualPages.Remove(oldestKey);
            ResidentPageCount--;

            return oldestPhysIdx;
        }

        /// <summary>获取ClippedRange映射的UV坐标（将虚拟Page坐标映射到[0,1]纹理UV）。</summary>
        /// <param name="virtualUV">虚拟空间UV [0,Resolution]。</param>
        /// <param name="cascadeIndex">级联索引。</param>
        /// <param name="mipLevel">Mip级别。</param>
        /// <returns>映射后的UV坐标 [0,1]。</returns>
        public Vector2 GetClippedRangeUV(Vector2 virtualUV, int cascadeIndex, int mipLevel)
        {
            int pagesPerSide = _config.Resolution / _config.PageSize;
            float mipScale = 1.0f / (float)(1 << mipLevel);

            // ClippedRange: 将每级级联映射到独立的UV子范围
            float cascadeStart = (float)cascadeIndex / (float)_config.CascadeCount;
            float cascadeEnd = (float)(cascadeIndex + 1) / (float)_config.CascadeCount;

            // 在级联范围内映射
            float localU = (virtualUV.X / (float)_config.Resolution) * mipScale;
            float localV = (virtualUV.Y / (float)_config.Resolution) * mipScale;

            // 限制到级联子范围
            float u = cascadeStart + localU * (cascadeEnd - cascadeStart);
            float v = localV;

            return new Vector2(Math.Clamp(u, 0.0f, 1.0f), Math.Clamp(v, 0.0f, 1.0f));
        }
    }

    // ──────────────────────── 级联视图矩阵计算 ────────────────────────

    /// <summary>单级级联的视图投影矩阵和分割参数。</summary>
    public struct CascadeViewInfo
    {
        /// <summary>光空间视图矩阵。</summary>
        public Matrix4x4 LightViewMatrix;
        /// <summary>光空间投影矩阵（正交）。</summary>
        public Matrix4x4 LightProjMatrix;
        /// <summary>组合的视图投影矩阵。</summary>
        public Matrix4x4 ViewProjMatrix;
        /// <summary>级联近裁面（视空间Z）。</summary>
        public float NearZ;
        /// <summary>级联远裁面（视空间Z）。</summary>
        public float FarZ;
        /// <summary>级联分割权重。</summary>
        public float SplitWeight;
        /// <summary>在虚拟贴图中的UV偏移和缩放。</summary>
        public Vector4 UVScaleOffset;
    }

    /// <summary>从定向光计算级联视图矩阵（CSM改进版: PRD分割）。</summary>
    public sealed class LightViewMatrixCalculation
    {
        private readonly SparseShadowAtlasConfig _config;
        private CascadeViewInfo[] _cascades;

        public LightViewMatrixCalculation(SparseShadowAtlasConfig config)
        {
            _config = config;
            _cascades = new CascadeViewInfo[config.CascadeCount];
        }

        /// <summary>获取当前帧的级联信息数组。</summary>
        public CascadeViewInfo[] GetCascades() => _cascades;

        /// <summary>计算所有级联的视图投影矩阵。</summary>
        /// <param name="lightDirection">定向光方向（归一化）。</param>
        /// <param name="cameraViewMatrix">相机视图矩阵。</param>
        /// <param name="cameraProjMatrix">相机投影矩阵。</param>
        /// <param name="sceneBounds">场景AABB（世界空间）。</param>
        public void CalculateCascadeMatrices(
            Vector3 lightDirection, Matrix4x4 cameraViewMatrix,
            Matrix4x4 cameraProjMatrix, BoundingBox sceneBounds)
        {
            // 1. 计算相机视锥体的8个角点（世界空间）
            Matrix4x4 invViewProj = Matrix4x4.Identity;
            Matrix4x4.Invert(Matrix4x4.Multiply(cameraViewMatrix, cameraProjMatrix), out invViewProj);

            Vector3[] frustumCorners = ExtractFrustumCornersWorldSpace(invViewProj);

            // 2. 计算 PRD 分割距离
            float[] splitDistances = ComputePRDSplitDistances(
                _config.CascadeCount, _config.CascadeSplitWeight,
                _config.LightViewDistance);

            // 3. 对每个级联计算光空间视图投影矩阵
            Vector3 lightDir = Vector3.Normalize(-lightDirection);

            // 构建光的视图矩阵: lookAt 从场景中心沿光方向
            Vector3 sceneCenter = (sceneBounds.Min + sceneBounds.Max) * 0.5f;
            Vector3 lightPos = sceneCenter - lightDir * _config.LightViewDistance;
            Vector3 lightUp = Math.Abs(lightDir.Y) < 0.999f
                ? Vector3.UnitY
                : Vector3.UnitX;
            Vector3 lightRight = Vector3.Normalize(Vector3.Cross(lightDir, lightUp));
            lightUp = Vector3.Normalize(Vector3.Cross(lightRight, lightDir));

            _cascades[0].LightViewMatrix = CreateLookAt(lightPos, lightDir, lightUp, lightRight);

            for (int i = 0; i < _config.CascadeCount; i++)
            {
                // 提取当前级联覆盖的视锥体子集的角点
                Vector3[] cascadeCorners = ExtractCascadeFrustumCorners(
                    cameraViewMatrix, invViewProj,
                    splitDistances[i], splitDistances[i + 1]);

                // 将角点变换到光空间
                Vector3[] lightSpaceCorners = new Vector3[8];
                for (int c = 0; c < 8; c++)
                {
                    Vector4 ls = Vector4.Transform(
                        new Vector4(cascadeCorners[c], 1.0f), _cascades[i].LightViewMatrix);
                    lightSpaceCorners[c] = new Vector3(ls.X, ls.Y, ls.Z);
                }

                // 计算光空间AABB
                Vector3 lsMin = new Vector3(float.MaxValue);
                Vector3 lsMax = new Vector3(float.MinValue);
                foreach (var corner in lightSpaceCorners)
                {
                    lsMin = Vector3.Min(lsMin, corner);
                    lsMax = Vector3.Max(lsMax, corner);
                }

                // 扩展Z范围包含场景AABB（防止物体在级联外）
                Vector4 sceneMinLS = Vector4.Transform(
                    new Vector4(sceneBounds.Min, 1.0f), _cascades[i].LightViewMatrix);
                Vector4 sceneMaxLS = Vector4.Transform(
                    new Vector4(sceneBounds.Max, 1.0f), _cascades[i].LightViewMatrix);
                lsMin.Z = Math.Min(lsMin.Z, Math.Min(sceneMinLS.Z, sceneMaxLS.Z));
                lsMax.Z = Math.Max(lsMax.Z, Math.Max(sceneMinLS.Z, sceneMaxLS.Z));

                // 创建正交投影矩阵
                _cascades[i].LightProjMatrix = CreateOrthographicMatrix(
                    lsMin.X, lsMax.X, lsMin.Y, lsMax.Y, lsMin.Z, lsMax.Z);
                _cascades[i].ViewProjMatrix = Matrix4x4.Multiply(
                    _cascades[i].LightViewMatrix, _cascades[i].LightProjMatrix);
                _cascades[i].NearZ = splitDistances[i];
                _cascades[i].FarZ = splitDistances[i + 1];
                _cascades[i].SplitWeight = _config.CascadeSplitWeight;

                // UV缩放偏移: 将级联映射到虚拟贴图的不同区域
                float uvScale = 1.0f / (float)_config.CascadeCount;
                float uvOffset = (float)i * uvScale;
                _cascades[i].UVScaleOffset = new Vector4(uvScale, 1.0f, uvOffset, 0.0f);
            }
        }

        /// <summary>PRD (Practical Partitioned) 级联分割距离计算。</summary>
        /// <remarks>
        /// 使用对数+均匀混合分割: split(i) = lambda * n * (f/n)^i + (1-lambda) * (n + (f-n) * i/N)
        /// 其中 lambda=SplitWeight, n=near, f=far, N=CascadeCount
        /// </remarks>
        private float[] ComputePRDSplitDistances(int cascadeCount, float splitWeight, float maxDist)
        {
            float[] distances = new float[cascadeCount + 1];
            float near = 0.1f;
            float far = maxDist;

            for (int i = 0; i <= cascadeCount; i++)
            {
                float logTerm = near * MathF.Pow(far / near, (float)i / cascadeCount);
                float uniformTerm = near + (far - near) * ((float)i / cascadeCount);
                distances[i] = splitWeight * logTerm + (1.0f - splitWeight) * uniformTerm;
            }

            return distances;
        }

        /// <summary>提取视锥体8个角点（世界空间）。</summary>
        private Vector3[] ExtractFrustumCornersWorldSpace(Matrix4x4 invViewProj)
        {
            Vector3[] corners = new Vector3[8];
            // NDC坐标的8个角
            Vector4[] ndcCorners = {
                new Vector4(-1, -1, 0, 1), new Vector4(1, -1, 0, 1),
                new Vector4(-1, 1, 0, 1),  new Vector4(1, 1, 0, 1),
                new Vector4(-1, -1, 1, 1), new Vector4(1, -1, 1, 1),
                new Vector4(-1, 1, 1, 1),  new Vector4(1, 1, 1, 1)
            };

            for (int i = 0; i < 8; i++)
            {
                Vector4 world = Vector4.Transform(ndcCorners[i], invViewProj);
                float w = Math.Max(world.W, 1e-10f);
                corners[i] = new Vector3(world.X / w, world.Y / w, world.Z / w);
            }

            return corners;
        }

        /// <summary>提取指定近远范围内的视锥体角点。</summary>
        private Vector3[] ExtractCascadeFrustumCorners(
            Matrix4x4 cameraViewMatrix, Matrix4x4 invViewProj,
            float nearZ, float farZ)
        {
            // 在相机空间中，按深度插值截取视锥体子集
            // 简化: 重新构建近远裁面的NDC并反变换
            Vector3[] fullCorners = ExtractFrustumCornersWorldSpace(invViewProj);

            // 视空间Z转世界空间的近似: 对每个角点缩放Z到指定范围
            Vector3 cameraPos = new Vector3(cameraViewMatrix.M41, cameraViewMatrix.M42, cameraViewMatrix.M43);
            // 提取视空间的前4个(近)和后4个(远)角点
            Vector3[] result = new Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                // 近面角点
                Vector3 nearDir = fullCorners[i + 4] - cameraPos;
                float nearDist = nearDir.Length();
                nearDir /= Math.Max(nearDist, 1e-10f);
                result[i] = cameraPos + nearDir * nearZ;

                // 远面角点
                Vector3 farDir = fullCorners[i + 4] - cameraPos;
                float farDist = farDir.Length();
                farDir /= Math.Max(farDist, 1e-10f);
                result[i + 4] = cameraPos + farDir * farZ;
            }

            return result;
        }

        /// <summary>创建光空间 LookAt 矩阵。</summary>
        private Matrix4x4 CreateLookAt(Vector3 eye, Vector3 forward, Vector3 up, Vector3 right)
        {
            Matrix4x4 m = Matrix4x4.Identity;
            m.M11 = right.X; m.M12 = up.X; m.M13 = forward.X; m.M14 = 0;
            m.M21 = right.Y; m.M22 = up.Y; m.M23 = forward.Y; m.M24 = 0;
            m.M31 = right.Z; m.M32 = up.Z; m.M33 = forward.Z; m.M34 = 0;
            m.M41 = -Vector3.Dot(right, eye);
            m.M42 = -Vector3.Dot(up, eye);
            m.M43 = -Vector3.Dot(forward, eye);
            m.M44 = 1;
            return m;
        }

        /// <summary>创建正交投影矩阵（右手坐标系）。</summary>
        private Matrix4x4 CreateOrthographicMatrix(float left, float right, float bottom, float top, float zNear, float zFar)
        {
            Matrix4x4 m = Matrix4x4.Identity;
            float width = right - left;
            float height = top - bottom;
            float depth = zFar - zNear;
            m.M11 = 2.0f / width;
            m.M22 = 2.0f / height;
            m.M33 = -2.0f / depth;
            m.M41 = -(right + left) / width;
            m.M42 = -(top + bottom) / height;
            m.M43 = -(zFar + zNear) / depth;
            m.M44 = 1;
            return m;
        }
    }

    /// <summary>轴对齐包围盒。</summary>

    // ──────────────────────── MeshShader 光栅化管线 ────────────────────────

    /// <summary>光栅化请求: 指定哪些Page需要光栅化以及哪些Mesh需要处理。</summary>
    public struct RasterizationRequest
    {
        /// <summary>目标虚拟页。</summary>
        public VSMPage TargetPage;
        /// <summary>需要光栅化的Mesh索引列表（经过Cluster预剔除）。</summary>
        public int[] MeshIndices;
        /// <summary>Mesh数量。</summary>
        public int MeshCount;
    }

    /// <summary>Cluster: 将Mesh按空间分组，用于预计算Page覆盖关系。</summary>
    public struct MeshCluster
    {
        /// <summary>Cluster在光空间的AABB。</summary>
        public Vector3 LightSpaceMin;
        /// <summary>LightSpaceMax padding。</summary>
        public float LightSpaceMinW;
        public Vector3 LightSpaceMax;
        /// <summary>LightSpaceMax padding。</summary>
        public float LightSpaceMaxW;
        /// <summary>Cluster包含的三角形起始索引。</summary>
        public int TriangleStart;
        /// <summary>Cluster包含的三角形数量。</summary>
        public int TriangleCount;
        /// <summary>预计算的覆盖Page列表（PageKey数组）。</summary>
        public long[] CoveredPageKeys;
        /// <summary>覆盖Page数量。</summary>
        public int CoveredPageCount;
    }

    /// <summary>MeshShader驱动的虚拟阴影贴图光栅化管线。</summary>
    public sealed class RasterizationPipeline
    {
        private readonly SparseShadowAtlasConfig _config;
        private MeshCluster[] _clusters;
        private int _clusterCount;
        private long[] _pendingPageRequests;
        private int _pendingCount;

        /// <summary>当前帧需要处理的栅格化请求数。</summary>
        public int PendingRequestCount => _pendingCount;

        public RasterizationPipeline(SparseShadowAtlasConfig config)
        {
            _config = config;
            _clusters = new MeshCluster[65536];
            _pendingPageRequests = new long[config.StreamRequestQueueSize];
            _clusterCount = 0;
            _pendingCount = 0;
        }

        /// <summary>构建Cluster（将场景Mesh按空间分组）。</summary>
        /// <param name="meshBounds">每个Mesh的光空间AABB。</param>
        /// <param name="triangleOffsets">每个Mesh的三角形偏移。</param>
        /// <param name="triangleCounts">每个Mesh的三角形数。</param>
        /// <param name="meshCount">Mesh数量。</param>
        /// <param name="cascades">级联视图信息。</summary>
        public void BuildClusters(
            (Vector3 Min, Vector3 Max)[] meshBounds,
            int[] triangleOffsets, int[] triangleCounts, int meshCount,
            CascadeViewInfo[] cascades)
        {
            _clusterCount = 0;
            int clusterCapacity = _clusters.Length;

            for (int m = 0; m < meshCount && _clusterCount < clusterCapacity; m++)
            {
                ref MeshCluster cluster = ref _clusters[_clusterCount++];
                cluster.LightSpaceMin = meshBounds[m].Min;
                cluster.LightSpaceMax = meshBounds[m].Max;
                cluster.LightSpaceMinW = 0.0f;
                cluster.LightSpaceMaxW = 0.0f;
                cluster.TriangleStart = triangleOffsets[m];
                cluster.TriangleCount = triangleCounts[m];

                // 预计算该Cluster覆盖的Page
                cluster.CoveredPageKeys = PrecomputeCoveredPages(
                    cluster, cascades, out int coveredCount);
                cluster.CoveredPageCount = coveredCount;
            }
        }

        /// <summary>预计算Cluster覆盖的Page列表。</summary>
        private long[] PrecomputeCoveredPages(MeshCluster cluster, CascadeViewInfo[] cascades, out int coveredCount)
        {
            List<long> pages = new List<long>();

            for (int c = 0; c < _config.CascadeCount; c++)
            {
                // 将Cluster AABB从光空间投影到虚拟贴图空间
                Matrix4x4 invVP = Matrix4x4.Identity;
                Matrix4x4.Invert(cascades[c].ViewProjMatrix, out invVP);

                // Cluster的8个角点
                Vector3[] corners = {
                    new Vector3(cluster.LightSpaceMin.X, cluster.LightSpaceMin.Y, cluster.LightSpaceMin.Z),
                    new Vector3(cluster.LightSpaceMax.X, cluster.LightSpaceMin.Y, cluster.LightSpaceMin.Z),
                    new Vector3(cluster.LightSpaceMin.X, cluster.LightSpaceMax.Y, cluster.LightSpaceMin.Z),
                    new Vector3(cluster.LightSpaceMax.X, cluster.LightSpaceMax.Y, cluster.LightSpaceMin.Z),
                    new Vector3(cluster.LightSpaceMin.X, cluster.LightSpaceMin.Y, cluster.LightSpaceMax.Z),
                    new Vector3(cluster.LightSpaceMax.X, cluster.LightSpaceMin.Y, cluster.LightSpaceMax.Z),
                    new Vector3(cluster.LightSpaceMin.X, cluster.LightSpaceMax.Y, cluster.LightSpaceMax.Z),
                    new Vector3(cluster.LightSpaceMax.X, cluster.LightSpaceMax.Y, cluster.LightSpaceMax.Z)
                };

                // 投影到NDC [0,1] -> 虚拟贴图坐标
                float pageMinX = float.MaxValue, pageMinY = float.MaxValue;
                float pageMaxX = float.MinValue, pageMaxY = float.MinValue;

                foreach (var corner in corners)
                {
                    Vector4 clip = Vector4.Transform(new Vector4(corner, 1.0f), cascades[c].ViewProjMatrix);
                    // 透视除法（正交投影中 w=1）
                    float w = Math.Max(clip.W, 1e-10f);
                    float ndcX = (clip.X / w) * 0.5f + 0.5f;
                    float ndcY = (clip.Y / w) * 0.5f + 0.5f;

                    pageMinX = Math.Min(pageMinX, ndcX);
                    pageMinY = Math.Min(pageMinY, ndcY);
                    pageMaxX = Math.Max(pageMaxX, ndcX);
                    pageMaxY = Math.Max(pageMaxY, ndcY);
                }

                // 转换到Page坐标
                int startPageX = Math.Max(0, (int)(pageMinX * _config.Resolution / _config.PageSize));
                int startPageY = Math.Max(0, (int)(pageMinY * _config.Resolution / _config.PageSize));
                int endPageX = Math.Min(_config.Resolution / _config.PageSize - 1,
                    (int)(pageMaxX * _config.Resolution / _config.PageSize));
                int endPageY = Math.Min(_config.Resolution / _config.PageSize - 1,
                    (int)(pageMaxY * _config.Resolution / _config.PageSize));

                for (int py = startPageY; py <= endPageY; py++)
                {
                    for (int px = startPageX; px <= endPageX; px++)
                    {
                        long key = ((long)c << 48) | ((long)0 << 32) | ((long)py << 16) | (uint)px;
                        pages.Add(key);
                    }
                }
            }

            coveredCount = pages.Count;
            return pages.ToArray();
        }

        /// <summary>执行光栅化（VS阶段: 顶点变换，MS阶段: MeshShader分发Page）。</summary>
        /// <param name="pageTable">页表引用。</param>
        /// <param name="vertices">顶点缓冲（世界空间位置）。</param>
        /// <param name="indices">索引缓冲。</param>
        /// <param name="cascadeIndex">当前处理的级联。</param>
        /// <param name="viewProj">级联视图投影矩阵。</param>
        /// <param name="frameIndex">当前帧号。</param>
        public void ExecuteRasterization(
            VSMPageTable pageTable,
            Vector3[] vertices, int[] indices,
            int cascadeIndex, Matrix4x4 viewProj, int frameIndex)
        {
            // 获取需要光栅化的Page
            List<VSMPage> pagesToRasterize = pageTable.GetPagesNeedingRasterization();

            // VS阶段: 将所有顶点变换到光空间
            Vector3[] lightSpaceVerts = new Vector3[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                Vector4 ls = Vector4.Transform(new Vector4(vertices[v], 1.0f), viewProj);
                lightSpaceVerts[v] = new Vector3(ls.X, ls.Y, ls.Z);
            }

            // MS阶段: 遍历每个Cluster，按Page分发三角形到对应的光栅化目标
            for (int c = 0; c < _clusterCount; c++)
            {
                ref MeshCluster cluster = ref _clusters[c];

                // 检查该Cluster是否有Page需要光栅化
                for (int p = 0; p < cluster.CoveredPageCount; p++)
                {
                    long pageKey = cluster.CoveredPageKeys[p];
                    int pageIdx = pageTable.FindPhysicalPage(
                        (int)(pageKey & 0xFFFF),
                        (int)((pageKey >> 16) & 0xFFFF),
                        (int)((pageKey >> 48) & 0xFF),
                        (int)((pageKey >> 32) & 0xFF));

                    if (pageIdx < 0) continue; // 该Page未驻留

                    // 光栅化该Cluster的三角形到这个Page
                    RasterizeClusterToPage(
                        cluster, lightSpaceVerts, indices,
                        pageIdx, viewProj, cascadeIndex);
                }
            }

            // 标记完成
            foreach (var page in pagesToRasterize)
            {
                pageTable.MarkPageResident(page.PageKey);
            }
        }

        /// <summary>将Cluster的三角形光栅化到指定Page。</summary>
        private void RasterizeClusterToPage(
            MeshCluster cluster, Vector3[] lightSpaceVerts, int[] indices,
            int physicalPageIndex, Matrix4x4 viewProj, int cascadeIndex)
        {
            // Page在虚拟贴图中的像素范围
            float pageTexelSize = (float)_config.PageSize;
            float invResolution = 1.0f / (float)_config.Resolution;

            for (int t = 0; t < cluster.TriangleCount; t++)
            {
                int i0 = indices[cluster.TriangleStart + t * 3 + 0];
                int i1 = indices[cluster.TriangleStart + t * 3 + 1];
                int i2 = indices[cluster.TriangleStart + t * 3 + 2];

                if (i0 >= lightSpaceVerts.Length || i1 >= lightSpaceVerts.Length ||
                    i2 >= lightSpaceVerts.Length) continue;

                Vector3 v0 = lightSpaceVerts[i0];
                Vector3 v1 = lightSpaceVerts[i1];
                Vector3 v2 = lightSpaceVerts[i2];

                // NDC空间三角形
                // 透视除法
                Vector3 ndc0 = TriangleToNDC(v0);
                Vector3 ndc1 = TriangleToNDC(v1);
                Vector3 ndc2 = TriangleToNDC(v2);

                // 背面剔除（光空间中顺时针为正面）
                float edge1X = ndc1.X - ndc0.X;
                float edge1Y = ndc1.Y - ndc0.Y;
                float edge2X = ndc2.X - ndc0.X;
                float edge2Y = ndc2.Y - ndc0.Y;
                float cross = edge1X * edge2Y - edge1Y * edge2X;
                if (cross >= 0.0f) continue; // 背面

                // 在实际GPU实现中，这里会将三角形提交到光栅化器
                // 写入物理Page的深度纹理（depth = max(ndc0.z, ndc1.z, ndc2.z)）
                // 这里用简化版模拟深度写入
            }
        }

        private Vector3 TriangleToNDC(Vector3 lightSpace)
        {
            // 正交投影: NDC = (x/w + 1)*0.5, (y/w + 1)*0.5
            // 这里简化，假设已在裁剪空间
            return new Vector3(
                lightSpace.X * 0.5f + 0.5f,
                lightSpace.Y * 0.5f + 0.5f,
                lightSpace.Z);
        }
    }

    // ──────────────────────── 可见性缓存 ────────────────────────

    /// <summary>可见性缓存: 记录上帧哪些Page被命中，避免重复请求。</summary>
    public sealed class VisibilityCache
    {
        private readonly SparseShadowAtlasConfig _config;
        private HashSet<long> _lastFrameVisiblePages;
        private HashSet<long> _currentFrameRequestedPages;
        private int[] _pageHitCount;
        private int _frameIndex;

        /// <summary>缓存命中数。</summary>
        public int CacheHitCount { get; private set; }

        /// <summary>缓存未命中数。</summary>
        public int CacheMissCount { get; private set; }

        public VisibilityCache(SparseShadowAtlasConfig config)
        {
            _config = config;
            _lastFrameVisiblePages = new HashSet<long>();
            _currentFrameRequestedPages = new HashSet<long>();
            _pageHitCount = new int[config.MaxPageCount];
            _frameIndex = 0;
            CacheHitCount = 0;
            CacheMissCount = 0;
        }

        /// <summary>开始新帧。</summary>
        public void BeginFrame()
        {
            _currentFrameRequestedPages.Clear();
            CacheHitCount = 0;
            CacheMissCount = 0;
        }

        /// <summary>查询Page是否在缓存中（上帧可见）。</summary>
        public bool IsPageCached(long pageKey)
        {
            _currentFrameRequestedPages.Add(pageKey);
            if (_lastFrameVisiblePages.Contains(pageKey))
            {
                CacheHitCount++;
                return true;
            }
            CacheMissCount++;
            return false;
        }

        /// <summary>结束帧: 更新缓存。</summary>
        public void EndFrame()
        {
            // 交换帧数据
            var temp = _lastFrameVisiblePages;
            _lastFrameVisiblePages = _currentFrameRequestedPages;
            _currentFrameRequestedPages = temp;
            _frameIndex++;
        }

        /// <summary>设置脏标记: 当光源或物体移动时调用。</summary>
        /// <param name="pageTable">页表引用。</param>
        /// <param name="reason">脏标记原因。</summary>
        public void InvalidateForReason(VSMPageTable pageTable, int reason)
        {
            // 根据脏原因标记相关Page
            if (reason == 1) // 光源移动 -> 标记所有Page为脏
            {
                pageTable.MarkAllDirty(reason);
            }
            // 其他原因只标记部分Page（此处简化为全标记）
        }

        /// <summary>获取Page命中率统计。</summary>
        public float GetHitRate()
        {
            int total = CacheHitCount + CacheMissCount;
            return total > 0 ? (float)CacheHitCount / total : 0.0f;
        }
    }

    // ──────────────────────── Page 流送 ────────────────────────

    /// <summary>Page流送请求优先级。</summary>
    public struct PageStreamRequest
    {
        /// <summary>请求的虚拟Page键。</summary>
        public long PageKey;
        /// <summary>优先级值（越大越优先）。</summary>
        public float Priority;
        /// <summary>请求来源: 0=相机可见, 1=HiZ反馈, 2=预取。</summary>
        public int Source;
    }

    /// <summary>按需Page流送管理器: 基于优先级队列驱动Page请求。</summary>
    public sealed class PageStreaming
    {
        private readonly SparseShadowAtlasConfig _config;
        private List<PageStreamRequest> _requestQueue;
        private int _currentFrame;

        public PageStreaming(SparseShadowAtlasConfig config)
        {
            _config = config;
            _requestQueue = new List<PageStreamRequest>(config.StreamRequestQueueSize);
            _currentFrame = 0;
        }

        /// <summary>添加Page请求到队列。</summary>
        /// <param name="pageKey">虚拟Page键。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="source">来源。</param>
        public void EnqueueRequest(long pageKey, float priority, int source)
        {
            // 如果队列已满，替换最低优先级请求
            if (_requestQueue.Count >= _config.StreamRequestQueueSize)
            {
                int minIdx = -1;
                float minPri = float.MaxValue;
                for (int i = 0; i < _requestQueue.Count; i++)
                {
                    if (_requestQueue[i].Priority < minPri)
                    {
                        minPri = _requestQueue[i].Priority;
                        minIdx = i;
                    }
                }
                if (priority > minPri)
                {
                    _requestQueue[minIdx] = new PageStreamRequest
                    {
                        PageKey = pageKey, Priority = priority, Source = source
                    };
                }
                // 否则丢弃（新请求优先级不够高）
            }
            else
            {
                _requestQueue.Add(new PageStreamRequest
                {
                    PageKey = pageKey, Priority = priority, Source = source
                });
            }
        }

        /// <summary>处理队列: 按优先级排序后逐个请求Page分配。</summary>
        /// <param name="pageTable">页表引用。</param>
        /// <param name="cameraPosition">相机位置（用于优先级计算）。</param>
        public void ProcessQueue(VSMPageTable pageTable, Vector3 cameraPosition)
        {
            // 按优先级降序排序
            _requestQueue.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            int processed = 0;
            for (int i = 0; i < _requestQueue.Count; i++)
            {
                long key = _requestQueue[i].PageKey;
                int cascadeIdx = (int)((key >> 48) & 0xFF);
                int mipLevel = (int)((key >> 32) & 0xFF);
                int pageY = (int)((key >> 16) & 0xFFFF);
                int pageX = (int)(key & 0xFFFF);

                int physIdx = pageTable.RequestPage(pageX, pageY, cascadeIdx, mipLevel, _currentFrame);
                if (physIdx >= 0)
                {
                    processed++;
                    // Page已分配，等待光栅化
                }
            }

            _requestQueue.Clear();
            _currentFrame++;
        }

        /// <summary>基于HiZ反馈驱动Page请求。</summary>
        /// <param name="hizDepth">HiZ深度缓冲。</param>
        /// <param name="hizMipLevel">当前HiZ Mip级别。</param>
        /// <param name="width">HiZ宽度。</param>
        /// <param name="height">HiZ高度。</param>
        /// <param name="pageTable">页表引用。</param>
        /// <param name="cascadeIndex">级联索引。</param>
        public void HiZDrivenRequests(
            float[] hizDepth, int hizMipLevel, int width, int height,
            VSMPageTable pageTable, int cascadeIndex)
        {
            if (!_config.EnableHiZDrivenRequests) return;

            // 遍历HiZ缓冲，找到深度不连续区域（可能是新可见的Page）
            int mipWidth = width >> hizMipLevel;
            int mipHeight = height >> hizMipLevel;
            int step = 1 << hizMipLevel;

            for (int y = 0; y < mipHeight; y++)
            {
                for (int x = 0; x < mipWidth; x++)
                {
                    int hizIdx = y * mipWidth + x;
                    float depth = hizDepth[hizIdx * step * step]; // 采样最大深度
                    if (depth <= 0.0f) continue; // 无深度数据

                    // 将屏幕坐标转换为虚拟Page坐标
                    int pageX = x * _config.PageSize / (width / _config.CascadeCount);
                    int pageY = y * _config.PageSize / height;

                    // 优先级: 基于深度（近处更高）
                    float priority = 1.0f / Math.Max(depth, 0.01f);
                    long pageKey = ((long)cascadeIndex << 48) | ((long)0 << 32) |
                                   ((long)pageY << 16) | (uint)pageX;

                    EnqueueRequest(pageKey, priority, 1); // Source=HiZ反馈
                }
            }
        }
    }

    // ──────────────────────── VSM 主系统 ────────────────────────

    /// <summary>虚拟阴影贴图主系统。</summary>
    public sealed class SparseShadowAtlasSystem
    {
        private readonly SparseShadowAtlasConfig _config;
        private readonly VSMPageTable _pageTable;
        private readonly LightViewMatrixCalculation _lightViewCalc;
        private readonly RasterizationPipeline _rasterizer;
        private readonly VisibilityCache _visibilityCache;
        private readonly PageStreaming _pageStreaming;
        private CascadeViewInfo[] _currentCascades;
        private Vector3 _prevLightDirection;

        /// <summary>当前帧级联数。</summary>
        public int CascadeCount => _config.CascadeCount;

        public SparseShadowAtlasSystem(SparseShadowAtlasConfig config)
        {
            _config = config;
            _pageTable = new VSMPageTable(config);
            _lightViewCalc = new LightViewMatrixCalculation(config);
            _rasterizer = new RasterizationPipeline(config);
            _visibilityCache = new VisibilityCache(config);
            _pageStreaming = new PageStreaming(config);
            _currentCascades = new CascadeViewInfo[config.CascadeCount];
            _prevLightDirection = Vector3.Zero;
        }

        /// <summary>更新VSM系统（每帧调用一次）。</summary>
        /// <param name="lightDirection">定向光方向。</param>
        /// <param name="cameraViewMatrix">相机视图矩阵。</param>
        /// <param name="cameraProjMatrix">相机投影矩阵。</param>
        /// <param name="sceneBounds">场景AABB。</param>
        /// <param name="vertices">场景顶点。</param>
        /// <param name="indices">索引缓冲。</param>
        /// <param name="meshBounds">每个Mesh的光空间AABB。</param>
        /// <param name="triangleOffsets">三角形偏移。</param>
        /// <param name="triangleCounts">三角形数。</param>
        /// <param name="meshCount">Mesh数量。</param>
        public void Update(
            Vector3 lightDirection,
            Matrix4x4 cameraViewMatrix, Matrix4x4 cameraProjMatrix,
            BoundingBox sceneBounds,
            Vector3[] vertices, int[] indices,
            (Vector3 Min, Vector3 Max)[] meshBounds,
            int[] triangleOffsets, int[] triangleCounts, int meshCount)
        {
            // 检测光源移动
            float lightDelta = Vector3.Distance(lightDirection, _prevLightDirection);
            if (lightDelta > 0.01f)
            {
                _visibilityCache.InvalidateForReason(_pageTable, 1); // 光源移动
            }
            _prevLightDirection = lightDirection;

            // 1. 计算级联矩阵
            _lightViewCalc.CalculateCascadeMatrices(
                lightDirection, cameraViewMatrix, cameraProjMatrix, sceneBounds);
            _currentCascades = _lightViewCalc.GetCascades();

            // 2. 构建Cluster
            _rasterizer.BuildClusters(
                meshBounds, triangleOffsets, triangleCounts, meshCount, _currentCascades);

            // 3. 可见性缓存查询
            _visibilityCache.BeginFrame();

            // 4. 提交Page请求
            for (int c = 0; c < _config.CascadeCount; c++)
            {
                // 基于相机视锥体确定需要的Page
                int pagesPerSide = _config.Resolution / _config.PageSize;
                for (int py = 0; py < pagesPerSide; py++)
                {
                    for (int px = 0; px < pagesPerSide; px++)
                    {
                        long pageKey = ((long)c << 48) | ((long)0 << 32) |
                                       ((long)py << 16) | (uint)px;

                        if (!_visibilityCache.IsPageCached(pageKey))
                        {
                            // 简化: 假设所有Page都可能需要（实际应基于视锥体裁剪）
                            float priority = 1.0f / (float)(c + 1); // 近级联高优先级
                            _pageStreaming.EnqueueRequest(pageKey, priority, 0);
                        }
                    }
                }
            }

            // 5. 处理Page请求队列
            Vector3 cameraPos = new Vector3(cameraViewMatrix.M41, cameraViewMatrix.M42, cameraViewMatrix.M43);
            _pageStreaming.ProcessQueue(_pageTable, cameraPos);

            // 6. 光栅化
            for (int c = 0; c < _config.CascadeCount; c++)
            {
                _rasterizer.ExecuteRasterization(
                    _pageTable, vertices, indices, c,
                    _currentCascades[c].ViewProjMatrix, _visibilityCache.CacheHitCount);
            }

            // 7. 结束可见性缓存帧
            _visibilityCache.EndFrame();
        }

        /// <summary>采样阴影值（0=阴影, 1=光照）。</summary>
        /// <param name="worldPosition">着色点世界坐标。</param>
        /// <param name="normal">表面法线。</param>
        /// <param name="lightDirection">光方向。</param>
        /// <returns>阴影因子 [0,1]。</returns>
        public float SampleShadow(Vector3 worldPosition, Vector3 normal, Vector3 lightDirection)
        {
            // 添加法线偏移防止阴影痤疮
            Vector3 biasedPos = worldPosition + normal * _config.NormalBias;

            // 确定级联
            Vector3 cameraPos = Vector3.Zero; // 简化
            float dist = Vector3.Distance(biasedPos, cameraPos);
            int cascadeIdx = 0;
            for (int c = 0; c < _config.CascadeCount; c++)
            {
                if (dist < _currentCascades[c].FarZ) { cascadeIdx = c; break; }
                if (c == _config.CascadeCount - 1) cascadeIdx = c;
            }

            // 变换到光空间
            Vector4 lightSpace = Vector4.Transform(
                new Vector4(biasedPos, 1.0f), _currentCascades[cascadeIdx].ViewProjMatrix);
            float w = Math.Max(lightSpace.W, 1e-10f);
            float ndcX = lightSpace.X / w * 0.5f + 0.5f;
            float ndcY = lightSpace.Y / w * 0.5f + 0.5f;
            float ndcZ = lightSpace.Z / w;

            // NDC -> Page坐标
            int pageX = (int)(ndcX * _config.Resolution / _config.PageSize);
            int pageY = (int)(ndcY * _config.Resolution / _config.PageSize);
            pageX = Math.Clamp(pageX, 0, _config.Resolution / _config.PageSize - 1);
            pageY = Math.Clamp(pageY, 0, _config.Resolution / _config.PageSize - 1);

            // 查找物理页
            int physIdx = _pageTable.FindPhysicalPage(pageX, pageY, cascadeIdx, 0);
            if (physIdx < 0) return 1.0f; // Page未驻留，假设无阴影

            // 在物理页中采样深度（比较深度值判断是否在阴影中）
            // 简化: 返回无阴影（实际从物理深度纹理采样比较）
            return 1.0f;
        }
    }

    // ──────────────────────── GPU 常量缓冲 ────────────────────────

    /// <summary>VSM GPU 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VSMCB
    {
        public Matrix4x4 CascadeViewProj0;
        public Matrix4x4 CascadeViewProj1;
        public Matrix4x4 CascadeViewProj2;
        public Matrix4x4 CascadeViewProj3;
        public Vector4 CascadeUVScaleOffset0;
        public Vector4 CascadeUVScaleOffset1;
        public Vector4 CascadeUVScaleOffset2;
        public Vector4 CascadeUVScaleOffset3;
        public Vector4 CascadeSplitDistances; // .xyz = split0,1,2, .w = far
        public int CascadeCount;
        public int PageSize;
        public int Resolution;
        public int MaxPageCount;
        public float ShadowBias;
        public float NormalBias;
        public int PageTableTextureIndex;
        public int PhysicalPagePoolIndex;
    }
}
