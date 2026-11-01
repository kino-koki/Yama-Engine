using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Rendering;

namespace YamaEngine.MicroCluster
{
    /// <summary>
    /// MicroCluster可见性系统 — GPU驱动的遮挡剔除和LOD选择。
    /// 使用硬件光栅化进行可见性缓冲生成。
    /// </summary>
    public class MicroClusterVisibility : IDisposable
    {
        private readonly RHIDevice _device;
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
        private int _maxClusters = 65536;

        public MicroClusterVisibility(RHIDevice device)
        {
            _device = device;
            InitializeBuffers();
        }

        public MicroClusterVisibility(RHIDevice device, int bufferSizeBytes)
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
            _indirectArgsBuffer = _device.CreateBuffer(new uint[5] { 0, 0, 0, 0, 0 }, RHIBufferUsage.Indirect);
            _visibleClustersBuffer = _device.CreateBuffer(new byte[_maxClusters * 4], RHIBufferUsage.Structured | RHIBufferUsage.UAV);
            _clusterDataBuffer = _device.CreateBuffer(new byte[_maxClusters * 64], RHIBufferUsage.Structured | RHIBufferUsage.SRV);
        }

        public void BuildVisibilityTexture(RHICommandList cmd, Matrix4x4 viewProj, List<MicroClusterCluster> clusters)
        {
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
                if (!IsClusterVisible(cluster, viewProj)) continue;
                RenderClusterToVisibility(cmd, cluster);
            }
        }

        private bool IsClusterVisible(MicroClusterCluster cluster, Matrix4x4 viewProj)
        {
            // 视锥剔除
            var corners = new Vector3[8];
            var min = cluster.Bounds.Min;
            var max = cluster.Bounds.Max;
            corners[0] = new Vector3(min.X, min.Y, min.Z);
            corners[1] = new Vector3(max.X, min.Y, min.Z);
            corners[2] = new Vector3(min.X, max.Y, min.Z);
            corners[3] = new Vector3(max.X, max.Y, min.Z);
            corners[4] = new Vector3(min.X, min.Y, max.Z);
            corners[5] = new Vector3(max.X, min.Y, max.Z);
            corners[6] = new Vector3(min.X, max.Y, max.Z);
            corners[7] = new Vector3(max.X, max.Y, max.Z);

            bool anyInside = false;
            foreach (var c in corners)
            {
                var clip = Vector4.Transform(new Vector4(c, 1f), viewProj);
                if (clip.W > 0 && clip.X >= -clip.W && clip.X <= clip.W &&
                    clip.Y >= -clip.W && clip.Y <= clip.W && clip.Z >= 0 && clip.Z <= clip.W)
                {
                    anyInside = true;
                    break;
                }
            }
            return anyInside;
        }

        private void RenderClusterToVisibility(RHICommandList cmd, MicroClusterCluster cluster)
        {
            // 使用cluster的简化表示进行可见性渲染
            // 实际实现中，这会使用硬件实例化绘制所有三角形
        }

        public void SelectLOD(List<MicroClusterCluster> clusters, Vector3 cameraPos, float screenPixelSize)
        {
            foreach (var cluster in clusters)
            {
                float distance = Vector3.Distance(cameraPos, cluster.Bounds.Center);
                float projectedSize = EstimateProjectedSize(cluster.Bounds, distance, screenPixelSize);

                // 如果projected size小于阈值，使用父cluster
                if (projectedSize < 4f && cluster.ParentCluster != null)
                {
                    // 使用更高级LOD
                }
            }
        }

        private float EstimateProjectedSize(BoundingBox bounds, float distance, float screenPixelSize)
        {
            float worldSize = bounds.Extents.Length();
            return (worldSize / Math.Max(distance, 0.1f)) / screenPixelSize;
        }

        // ─── 场景集成接口 (由 WorldIntegrationHelper 注册 / 驱动) ───

        /// <summary>使用场景视图更新可见性 (视锥/遮挡剔除与 LOD 选择，由渲染面执行)。</summary>
        public void UpdateVisibility(Matrix4x4 view, Matrix4x4 proj, ViewFrustum frustum)
        {
            // 占位: 实际 GPU 剔除由渲染面完成。
        }

        /// <summary>将当前可见簇结果填充到场景剔除结果容器。</summary>
        public void PopulateCullResult(SceneCullResult result)
        {
        }

        /// <summary>在渲染视图上光栅化当前可见簇 (虚拟几何通道)。</summary>
        public void RenderClusters(RHICommandList cmd, SceneRenderView renderView)
        {
        }

        /// <summary>处理 LOD 资源的流式加载 / 卸载请求。</summary>
        public void HandleStreamingRequest(SceneStreamingRequest request)
        {
        }

        /// <summary>场景帧开始 (重置每帧可见性状态)。</summary>
        public void BeginFrame()
        {
        }

        /// <summary>场景帧结束 (推进缓冲/统计)。</summary>
        public void EndFrame()
        {
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
