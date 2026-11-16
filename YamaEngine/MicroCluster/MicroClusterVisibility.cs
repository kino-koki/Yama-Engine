using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.RHI;
using YamaEngine.Rendering;

namespace YamaEngine.MicroCluster
{
    /// <summary>
    /// MicroCluster可见性系统 — GPU驱动的遮挡剔除和LOD选择。
    /// 使用硬件光栅化进行可见性缓冲生成。
    /// CPU 侧提供确定性的视锥剔除 + LOD 选择管线（<see cref="UpdateVisibility"/> /
    /// <see cref="PopulateCullResult"/>），由渲染面消费；GPU 侧经由
    /// <see cref="RenderClusters"/> 录制实际绘制命令。
    /// </summary>
    public class MicroClusterVisibility : IDisposable
    {
        private readonly RHIDevice? _device;
        private RHIPipelineState? _visibilityPSO;
        private RHIRootSignature? _rootSignature;
        private RHIBuffer? _indirectArgsBuffer;
        private RHIBuffer? _visibleClustersBuffer;
        private RHIBuffer? _clusterDataBuffer;

        // 可见性缓冲
        private RHITexture? _visibilityTexture;
        public int VisibilityTextureWidth { get; set; } = 1920;
        public int VisibilityTextureHeight { get; set; } = 1080;

        // 聚类数据
        private readonly List<MicroClusterCluster> _clusters = new();
        private readonly Dictionary<int, MicroClusterCluster> _clusterById = new();
        private int _nextAutoId;
        private int _maxClusters = 65536;

        // ── 每帧可见性状态 (CPU 侧确定性管线) ──
        private readonly List<int> _visibleClusters = new();
        private readonly HashSet<int> _visibleClusterSet = new();
        private readonly HashSet<int> _streamedOutClusters = new();
        private Matrix4x4 _lastView = Matrix4x4.Identity;
        private Matrix4x4 _lastProj = Matrix4x4.Identity;
        private ViewFrustum _lastFrustum = default;

        // ── 统计 ──
        private int _frameIndex;
        private int _frustumCulledCount;
        private long _lastVisibilityTicks;

        /// <summary>当前帧索引 (BeginFrame 递增)。</summary>
        public int FrameIndex => _frameIndex;

        /// <summary>当前帧可见聚类数。</summary>
        public int VisibleClusterCount => _visibleClusters.Count;

        /// <summary>当前帧可见聚类 ID 列表 (只读快照)。</summary>
        public IReadOnlyList<int> VisibleClusterIds => _visibleClusters;

        /// <summary>本帧被视锥剔除的聚类数。</summary>
        public int FrustumCulledCount => _frustumCulledCount;

        /// <summary>最近一次可见性计算的耗时 (毫秒)。</summary>
        public double LastVisibilityMs => _lastVisibilityTicks / (double)TimeSpan.TicksPerMillisecond;

        /// <summary>LOD 切换的屏幕误差阈值 (投影像素低于该值时回退到父聚类)。</summary>
        public float ScreenErrorThreshold { get; set; } = 4f;

        /// <summary>用于 LOD 投影估算的屏幕高度 (像素)。</summary>
        public float ScreenHeightPixels { get; set; } = 1080f;

        /// <summary>视锥测试使用的最小距离 (防止除零)。</summary>
        public float MinLodDistance { get; set; } = 0.1f;

        public MicroClusterVisibility(RHIDevice? device)
        {
            _device = device;
            InitializeBuffers();
        }

        public MicroClusterVisibility(RHIDevice? device, int bufferSizeBytes)
        {
            _device = device;
            InitializeBuffers();
        }
        /// <summary>聚类数据缓冲（渲染面可读取）。</summary>
        public RHIBuffer? ClusterBuffer => _clusterDataBuffer;

        /// <summary>可见聚类缓冲。</summary>
        public RHIBuffer? VisibleClusterBuffer => _visibleClustersBuffer;

        /// <summary>间接参数/页索引缓冲。</summary>
        public RHIBuffer? PageIndexBuffer => _indirectArgsBuffer;

        /// <summary>是否已注册到场景。</summary>
        public bool IsRegistered { get; set; }

        /// <summary>聚类总数。</summary>
        public int TotalClusterCount => _clusters.Count;

        /// <summary>页总数。</summary>
        public int TotalPageCount { get; set; }

        private void InitializeBuffers()
        {
            // 容忍空设备: 缓冲按需惰性创建 (CPU 可见性管线不依赖 RHI, 便于无设备测试)
            if (_device == null) return;

            _indirectArgsBuffer = _device.CreateBuffer(new uint[5] { 0, 0, 0, 0, 0 }, RHIBufferUsage.Indirect);
            _visibleClustersBuffer = _device.CreateBuffer(new byte[_maxClusters * 4], RHIBufferUsage.Structured | RHIBufferUsage.UAV);
            _clusterDataBuffer = _device.CreateBuffer(new byte[_maxClusters * 64], RHIBufferUsage.Structured | RHIBufferUsage.SRV);
        }

        // ════════════════════════════════════════════════════════════
        //  聚类注册 / 管理
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 注册一个聚类到可见性系统。若聚类未显式分配 <see cref="MicroClusterCluster.ClusterId"/>
        /// 或 ID 冲突, 则自动分配唯一 ID。
        /// </summary>
        public void RegisterCluster(MicroClusterCluster cluster)
        {
            if (cluster == null) return;

            int id = cluster.ClusterId;
            if (id <= 0 || _clusterById.ContainsKey(id))
            {
                id = ++_nextAutoId;
                cluster.ClusterId = id;
            }

            _clusters.Add(cluster);
            _clusterById[id] = cluster;
        }

        /// <summary>批量注册聚类。</summary>
        public void RegisterClusters(IEnumerable<MicroClusterCluster> clusters)
        {
            if (clusters == null) return;
            foreach (var c in clusters) RegisterCluster(c);
        }

        /// <summary>移除指定聚类 (按 ID)。</summary>
        public bool RemoveCluster(int clusterId)
        {
            if (!_clusterById.TryGetValue(clusterId, out var cluster))
                return false;

            _clusterById.Remove(clusterId);
            _clusters.Remove(cluster);
            _streamedOutClusters.Remove(clusterId);
            return true;
        }

        /// <summary>清空全部聚类。</summary>
        public void ClearClusters()
        {
            _clusters.Clear();
            _clusterById.Clear();
            _streamedOutClusters.Clear();
            _visibleClusters.Clear();
            _visibleClusterSet.Clear();
        }

        /// <summary>按 ID 查询聚类。</summary>
        public MicroClusterCluster? GetCluster(int clusterId)
            => _clusterById.TryGetValue(clusterId, out var c) ? c : null;

        /// <summary>查询某聚类在当前帧是否可见。</summary>
        public bool IsClusterVisible(int clusterId) => _visibleClusterSet.Contains(clusterId);

        /// <summary>查询某聚类是否已流式加载 (未标记为流式卸载)。</summary>
        public bool IsStreamedIn(int clusterId) => !_streamedOutClusters.Contains(clusterId);

        // ════════════════════════════════════════════════════════════
        //  CPU 可见性管线 (确定性, 可测试)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 使用场景视图更新可见性: 视锥剔除 + LOD 选择, 填充每帧可见聚类列表。
        /// 由渲染面 (cull 阶段) 调用。
        /// </summary>
        public void UpdateVisibility(Matrix4x4 view, Matrix4x4 proj, ViewFrustum frustum)
        {
            _lastView = view;
            _lastProj = proj;
            _lastFrustum = frustum;

            var start = System.Diagnostics.Stopwatch.GetTimestamp();

            _visibleClusters.Clear();
            _visibleClusterSet.Clear();
            _frustumCulledCount = 0;

            if (_clusters.Count == 0)
            {
                _lastVisibilityTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                return;
            }

            // Gribb-Hartmann 视锥平面提取 (与 RenderPipeline.Frustum 同约定)
            var viewProj = view * proj;
            var planes = ExtractPlanes(viewProj);

            var camPos = frustum.Position;
            float screenHeightPx = Math.Max(1f, ScreenHeightPixels);
            float fovY = frustum.VerticalFov > 0f ? frustum.VerticalFov : MathF.PI * 0.5f;

            foreach (var cluster in _clusters)
            {
                int id = cluster.ClusterId;

                // 流式卸载的聚类不可见
                if (_streamedOutClusters.Contains(id))
                    continue;

                // 视锥剔除 (AABB vs 6 平面)
                var aabb = new AABB(cluster.BoundsMin, cluster.BoundsMax);
                if (TestAabbOutside(planes, aabb))
                {
                    _frustumCulledCount++;
                    continue;
                }

                // LOD 选择: 投影像素过小则回退父聚类
                var selected = SelectLodForCluster(cluster, camPos, screenHeightPx, fovY);
                if (selected == null) continue;

                AddVisible(selected.ClusterId);
            }

            _lastVisibilityTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }

        /// <summary>
        /// 将当前可见簇结果填充到场景剔除结果容器。
        /// 由渲染面 (cull 阶段, 紧随 UpdateVisibility) 调用。
        /// </summary>
        public void PopulateCullResult(SceneCullResult result)
        {
            if (result == null) return;
            foreach (var id in _visibleClusters)
                result.VisiblePrimitives.Add((uint)id);
        }

        /// <summary>查询当前帧是否可见 (供测试/调试)。</summary>
        public bool IsVisible(int clusterId) => _visibleClusterSet.Contains(clusterId);

        // ════════════════════════════════════════════════════════════
        //  GPU 光栅化路径
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 在渲染视图上光栅化当前可见簇 (虚拟几何通道)。
        /// 由渲染面 (render 阶段) 调用; 此处迭代可见聚类并录制绘制命令。
        /// </summary>
        public void RenderClusters(RHICommandList cmd, SceneRenderView renderView)
        {
            if (cmd == null) return;

            // 可见性缓冲绘制: 对每个可见聚类, 以简化包围盒写入可见性纹理。
            // 实际 GPU 实例化绘制由渲染管线以 ClusterBuffer + VisibleClusterBuffer 驱动。
            BuildVisibilityTexture(cmd, _lastView * _lastProj, SnapshotVisibleClusters());
        }

        /// <summary>
        /// 处理 LOD 资源的流式加载 / 卸载请求。
        /// 由渲染面 (streaming 阶段) 调用。
        /// </summary>
        public void HandleStreamingRequest(SceneStreamingRequest request)
        {
            if (request == null) return;

            // 请求以聚类 ID 寻址 (ClusterOffset 为聚类索引)
            int clusterId = (int)request.ClusterOffset;
            if (request.ShouldLoad)
                _streamedOutClusters.Remove(clusterId);
            else
                _streamedOutClusters.Add(clusterId);
        }

        // ════════════════════════════════════════════════════════════
        //  帧生命周期
        // ════════════════════════════════════════════════════════════

        /// <summary>场景帧开始 (重置每帧可见性状态)。由 OnSceneBeginFrame 驱动。</summary>
        public void BeginFrame()
        {
            _frameIndex++;
            _visibleClusters.Clear();
            _visibleClusterSet.Clear();
            _frustumCulledCount = 0;
        }

        /// <summary>场景帧结束 (推进缓冲/统计)。由 OnSceneEndFrame 驱动。</summary>
        public void EndFrame()
        {
            // 可见性统计已在 UpdateVisibility 中更新; 此处仅推进帧统计占位。
        }

        // ════════════════════════════════════════════════════════════
        //  内部辅助
        // ════════════════════════════════════════════════════════════

        private void AddVisible(int clusterId)
        {
            if (_visibleClusterSet.Add(clusterId))
                _visibleClusters.Add(clusterId);
        }

        private List<MicroClusterCluster> SnapshotVisibleClusters()
        {
            var list = new List<MicroClusterCluster>(_visibleClusters.Count);
            foreach (var id in _visibleClusters)
                if (_clusterById.TryGetValue(id, out var c))
                    list.Add(c);
            return list;
        }

        /// <summary>选择应绘制的聚类 (自身或父聚类) 的 LOD 层级。</summary>
        private MicroClusterCluster? SelectLodForCluster(MicroClusterCluster cluster, Vector3 cameraPos, float screenHeightPx, float fovY)
        {
            float distance = Vector3.Distance(cameraPos, cluster.BoundsCenter);
            float projectedPixels = EstimateProjectedSize(cluster.Bounds, distance, screenHeightPx, fovY);

            // 投影像素过小且存在父聚类 → 使用父聚类 (更高级 LOD, 三角形更少)
            if (projectedPixels < ScreenErrorThreshold && cluster.ParentCluster != null)
                return cluster.ParentCluster;

            return cluster;
        }

        /// <summary>估算包围盒的投影像素尺寸。</summary>
        private float EstimateProjectedSize(BoundingBox bounds, float distance, float screenHeightPx, float fovY)
        {
            float worldSize = bounds.Extents.Length();
            // 屏幕半高 (像素) 每弧度: 投影像素 = worldSize / distance * (screenHeight / (2*tan(fov/2)))
            float halfScreenPx = screenHeightPx * 0.5f;
            float focal = halfScreenPx / MathF.Tan(fovY * 0.5f);
            return (worldSize / Math.Max(distance, MinLodDistance)) * focal;
        }

        /// <summary>从视图投影矩阵提取 6 个视锥平面 (Gribb-Hartmann)。</summary>
        private static Vector4[] ExtractPlanes(Matrix4x4 m)
        {
            var planes = new Vector4[6];
            // 左/右
            planes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41);
            planes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);
            // 下/上
            planes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42);
            planes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);
            // 近/远
            planes[4] = new Vector4(m.M13, m.M23, m.M33, m.M43);
            planes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);
            return planes;
        }

        /// <summary>AABB 是否完全在视锥外 (任一侧所有顶点皆负则外)。</summary>
        private static bool TestAabbOutside(Vector4[] planes, AABB b)
        {
            for (int i = 0; i < 6; i++)
            {
                var p = planes[i];
                // 平面法线取 AABB 的正顶点 (最靠近平面法线方向的角)
                var pos = new Vector3(
                    p.X >= 0 ? b.Max.X : b.Min.X,
                    p.Y >= 0 ? b.Max.Y : b.Min.Y,
                    p.Z >= 0 ? b.Max.Z : b.Min.Z);
                if (pos.X * p.X + pos.Y * p.Y + pos.Z * p.Z + p.W < 0)
                    return true;
            }
            return false;
        }

        // ─── 保留的 GPU 可见性缓冲辅助 ───

        /// <summary>构建/更新可见性纹理 (硬件光栅化可见性缓冲)。</summary>
        public void BuildVisibilityTexture(RHICommandList cmd, Matrix4x4 viewProj, List<MicroClusterCluster> clusters)
        {
            if (_device == null || cmd == null) return;

            if (_visibilityTexture == null || _visibilityTexture.Width != VisibilityTextureWidth)
            {
                _visibilityTexture?.Dispose();
                _visibilityTexture = _device.CreateTexture2D(VisibilityTextureWidth, VisibilityTextureHeight,
                    RHIPixelFormat.R32G32_UInt, RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource);
            }

            cmd.SetRenderTargets(new[] { _visibilityTexture }, null);
            cmd.ClearRenderTarget(_visibilityTexture, new Vector4(0, 0, 0, 0));

            // 对每个可见cluster绘制到可见性缓冲
            foreach (var cluster in clusters)
            {
                if (!IsClusterVisibleInFrustum(cluster, viewProj)) continue;
                RenderClusterToVisibility(cmd, cluster);
            }
        }

        private bool IsClusterVisibleInFrustum(MicroClusterCluster cluster, Matrix4x4 viewProj)
        {
            var aabb = new AABB(cluster.BoundsMin, cluster.BoundsMax);
            return !TestAabbOutside(ExtractPlanes(viewProj), aabb);
        }

        private void RenderClusterToVisibility(RHICommandList cmd, MicroClusterCluster cluster)
        {
            // 使用cluster的简化表示进行可见性渲染
            // 实际实现中，这会使用硬件实例化绘制所有三角形
        }

        public void Dispose()
        {
            _visibilityTexture?.Dispose();
            _indirectArgsBuffer?.Dispose();
            _visibleClustersBuffer?.Dispose();
            _clusterDataBuffer?.Dispose();
            _visibilityPSO?.Dispose();
            _rootSignature?.Dispose();
        }
    }
}
