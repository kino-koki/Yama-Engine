// ============================================================================
// YamaEngine - yama游戏引擎
// 场景渲染器集成辅助类型
// SceneRendererIntegrationTypes.cs
//
// 为 WorldIntegrationHelper 与 SceneRendererReal 配合使用提供回调形参辅助类型。
// 仅暴露各调用点实际用到的成员（最小补全），不含过度设计。
// ============================================================================

using System.Collections.Generic;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ── 回调委托 ──
    /// <summary>
    /// 剔除提供商委托: 每帧对给定视图执行剔除, 并将结果填充到剔除结果容器。
    /// </summary>
    public delegate void CullProviderDelegate(SceneViewInfo viewInfo, SceneCullResult cullResult);

    /// <summary>
    /// 渲染提供商委托: 在对应渲染通道录制 GPU 命令。
    /// </summary>
    public delegate void RenderProviderDelegate(RHICommandList commandList, SceneRenderView renderView);

    /// <summary>
    /// 流式加载处理器委托: 处理 LOD 资源的流式加载 / 卸载请求。
    /// </summary>
    public delegate void StreamingHandlerDelegate(SceneStreamingRequest streamingRequest);

    // ── 视图信息 (RegisterCullProvider 的 viewInfo 形参) ──
    /// <summary>
    /// 场景视图信息: 包含视图/投影矩阵、视锥与相机位置, 用于剔除阶段。
    /// </summary>
    public sealed class SceneViewInfo
    {
        /// <summary>视图矩阵。</summary>
        public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.Identity;
        /// <summary>投影矩阵。</summary>
        public Matrix4x4 ProjectionMatrix { get; set; } = Matrix4x4.Identity;
        /// <summary>视锥 (用于视锥剔除)。</summary>
        public ViewFrustum ViewFrustum { get; set; }
        /// <summary>相机位置 (用于 LOD 距离选择)。</summary>
        public Vector3 CameraPosition { get; set; }
    }

    /// <summary>
    /// 视锥体 (最小结构体)。
    /// </summary>
    public struct ViewFrustum
    {
        /// <summary>相机位置。</summary>
        public Vector3 Position;
        /// <summary>视锥朝向。</summary>
        public Vector3 Direction;
        /// <summary>近裁剪面距离。</summary>
        public float Near;
        /// <summary>远裁剪面距离。</summary>
        public float Far;
        /// <summary>垂直视野(弧度)。</summary>
        public float VerticalFov;
    }

    /// <summary>
    /// 剔除结果 (RegisterCullProvider 的 cullResult 形参)。由可见性系统填充。
    /// </summary>
    public sealed class SceneCullResult
    {
        private readonly List<uint> _visiblePrimitives = new();

        /// <summary>可见图元 ID 列表。</summary>
        public IList<uint> VisiblePrimitives => _visiblePrimitives;

        /// <summary>可见图元数量。</summary>
        public int Count => _visiblePrimitives.Count;
    }

    // ── 渲染视图 (RegisterRenderProvider 的 renderView 形参) ──
    /// <summary>
    /// 渲染视图: 描述一次绘制所需的视图/投影矩阵与 GBuffer 深度资源。
    /// </summary>
    public sealed class SceneRenderView
    {
        /// <summary>视图矩阵。</summary>
        public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.Identity;
        /// <summary>投影矩阵。</summary>
        public Matrix4x4 ProjectionMatrix { get; set; } = Matrix4x4.Identity;
        /// <summary>几何缓冲资源组。</summary>
        public GeometryBufferResources? GeometryBufferResources { get; set; }
        /// <summary>深度缓冲。</summary>
        public RHIResource? DepthBuffer { get; set; }
    }

    // ── 流式加载请求 (RegisterStreamingHandler 的 streamingRequest 形参) ──
    /// <summary>
    /// LOD 资源流式加载 / 卸载请求。
    /// </summary>
    public sealed class SceneStreamingRequest
    {
        /// <summary>目标簇偏移。</summary>
        public long ClusterOffset;
        /// <summary>目标区域偏移。</summary>
        public long RegionOffset;
        /// <summary>资源键名。</summary>
        public string Key = string.Empty;
        /// <summary>是否请求加载 (false 表示卸载)。</summary>
        public bool ShouldLoad = true;
    }

    // ── 渲染图元 (OnPrimitiveAdded 事件形参) ──
    /// <summary>
    /// 场景渲染图元 (供事件/回调传递的最小表示)。
    /// </summary>
    public sealed class RenderPrimitive
    {
        /// <summary>图元 ID。</summary>
        public int PrimitiveId { get; set; }
        /// <summary>是否为全局光照 (ProbeField) 关注的图元。</summary>
        public bool ProbeFieldAffected { get; set; }
    }

    // ── 场景资源管理器 ──
    /// <summary>
    /// 管理外部子系统注册到场景的 GPU 缓冲 / 纹理。
    /// </summary>
    public sealed class SceneResourceManager
    {
        private readonly Dictionary<string, RHIResource> _buffers = new();
        private readonly Dictionary<string, RHITexture> _textures = new();

        /// <summary>注册外部缓冲。</summary>
        public void RegisterExternalBuffer(string name, RHIResource resource)
        {
            if (!string.IsNullOrEmpty(name) && resource != null)
                _buffers[name] = resource;
        }

        /// <summary>注册外部纹理。</summary>
        public void RegisterExternalTexture(string name, RHITexture texture)
        {
            if (!string.IsNullOrEmpty(name) && texture != null)
                _textures[name] = texture;
        }

        /// <summary>尝试获取已注册的外部缓冲。</summary>
        public bool TryGetBuffer(string name, out RHIResource resource)
        {
            return _buffers.TryGetValue(name, out resource!);
        }

        /// <summary>尝试获取已注册的外部纹理。</summary>
        public bool TryGetTexture(string name, out RHITexture texture)
        {
            return _textures.TryGetValue(name, out texture!);
        }
    }
}