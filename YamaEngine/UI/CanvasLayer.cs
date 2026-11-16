// =============================================================================
//  Yama Engine - CanvasLayer.cs
//  yama引擎 - Canvas 渲染层（对应 Godot 的 CanvasLayer 节点）
//
//  对应 Godot CanvasLayer 的核心语义：
//    * 独立渲染层：拥有独立的变换（offset/rotation/scale），不随父 CanvasItem。
//    * layer 排序：按 Layer 升序渲染，值越小越先画（在底层）。
//    * 整层可见性：Visible=false 时该层所有子节点跳过渲染。
//    * follow_viewport：层变换叠加视口变换（视差/背景层跟随相机）。
//    * 自定义视口（custom_viewport）可选。
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>
    /// 视口（对应 Godot Viewport）：为 CanvasLayer 提供 follow_viewport 的相机变换。
    /// </summary>
    public sealed class ViewportReal
    {
        /// <summary>视口名。</summary>
        public string Name { get; set; } = "Viewport";

        /// <summary>视口尺寸（像素）。</summary>
        public Vector2 Size { get; set; } = new(1920, 1080);

        /// <summary>相机世界位置。</summary>
        public Vector2 CameraPosition { get; set; } = Vector2.Zero;

        /// <summary>相机缩放（zoom）。</summary>
        public float CameraZoom { get; set; } = 1f;

        public ViewportReal() { }
        public ViewportReal(string name, Vector2 size)
        {
            Name = name;
            Size = size;
        }
    }

    /// <summary>
    /// Canvas 渲染层：独立于 CanvasItem 树的变换层。
    /// 用法：
    /// <code>
    /// var layer = new CanvasLayerReal("HUD") { Layer = 1 };
    /// var root = new RectTransform("HUDRoot");
    /// layer.AddChild(root);
    /// scene.AddCanvasLayer(layer);
    /// scene.SortCanvasLayers();       // 按 Layer 升序
    /// scene.GetVisibleLayers();       // Visible=true 的层按序返回
    /// </code>
    /// </summary>
    public sealed class CanvasLayerReal
    {
        /// <summary>层名。</summary>
        public string Name { get; set; }

        /// <summary>层序号（升序渲染，值小在底层）。</summary>
        public int Layer { get; set; }

        /// <summary>整层可见性（false 时子节点全部跳过渲染）。</summary>
        public bool Visible { get; set; } = true;

        /// <summary>层平移偏移（像素）。</summary>
        public Vector2 Offset { get; set; } = Vector2.Zero;

        /// <summary>层旋转（弧度）。</summary>
        public float Rotation { get; set; }

        /// <summary>层缩放。</summary>
        public Vector2 Scale { get; set; } = Vector2.One;

        /// <summary>是否跟随视口变换（视差/背景层）。</summary>
        public bool FollowViewportEnabled { get; set; }

        /// <summary>跟随视口时的缩放因子（叠加在视口变换之上）。</summary>
        public float FollowViewportScale { get; set; } = 1f;

        /// <summary>自定义视口（null 则用默认视口）。</summary>
        public ViewportReal? CustomViewport { get; set; }

        /// <summary>层根 RectTransform（层的变换锚点，可空）。</summary>
        public RectTransform? LayerRoot { get; private set; }

        /// <summary>该层的直接子节点数。</summary>
        public int ChildCount => LayerRoot?.Children.Count ?? 0;

        public CanvasLayerReal(string name = "CanvasLayer")
        {
            Name = name;
        }

        public CanvasLayerReal(string name, int layer) : this(name)
        {
            Layer = layer;
        }

        /// <summary>添加子节点到层根。</summary>
        public void AddChild(RectTransform child)
        {
            if (LayerRoot == null)
            {
                LayerRoot = new RectTransform { Name = Name + "Root" };
            }
            LayerRoot.AddChild(child);
        }

        /// <summary>移除子节点。</summary>
        public void RemoveChild(RectTransform child)
        {
            LayerRoot?.RemoveChild(child);
        }

        /// <summary>
        /// 计算层的变换矩阵：Offset → Rotation → Scale，再叠加 follow_viewport 的视口变换。
        /// 对应 Godot 的 CanvasLayer.get_transform()。
        /// </summary>
        public Matrix3x2 GetTransform(ViewportReal? viewport = null)
        {
            var m = Matrix3x2.CreateScale(Scale);
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateRotation(Rotation));
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateTranslation(Offset));

            if (FollowViewportEnabled)
            {
                var vp = viewport ?? CustomViewport;
                if (vp != null)
                {
                    // 视口变换：(p - cam) * zoom —— 先平移（减相机），再缩放。
                    // Matrix3x2.Multiply(v1, v2) = 先应用 v1 后应用 v2，
                    // 故顺序为 Translate(-cam) * Scale(zoom)。
                    var vpTransform =
                        Matrix3x2.Multiply(
                            Matrix3x2.CreateTranslation(-vp.CameraPosition),
                            Matrix3x2.CreateScale(vp.CameraZoom));
                    // 叠加 follow 缩放因子（在最外层再缩放）
                    if (FollowViewportScale != 1f)
                    {
                        vpTransform = Matrix3x2.Multiply(
                            Matrix3x2.CreateScale(FollowViewportScale), vpTransform);
                    }
                    m = Matrix3x2.Multiply(m, vpTransform);
                }
            }
            return m;
        }

        /// <summary>用层的变换矩阵把局部点转为世界点。</summary>
        public Vector2 LocalToWorld(Vector2 localPoint, ViewportReal? viewport = null)
            => Vector2.Transform(localPoint, GetTransform(viewport));
    }

    /// <summary>
    /// CanvasLayer 容器（对应 SceneTree 的 CanvasLayer 管理）。
    /// 负责层的注册、按 Layer 排序、可见层查询。
    /// </summary>
    public sealed class CanvasLayerStack
    {
        private readonly List<CanvasLayerReal> _layers = new();

        /// <summary>已注册的层数量。</summary>
        public int Count => _layers.Count;

        /// <summary>按 Layer 升序的只读视图。</summary>
        public IReadOnlyList<CanvasLayerReal> Sorted => _layers;

        /// <summary>默认视口（follow_viewport 时使用）。</summary>
        public ViewportReal? DefaultViewport { get; set; }

        /// <summary>注册一个层。</summary>
        public void AddLayer(CanvasLayerReal layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (!_layers.Contains(layer))
                _layers.Add(layer);
        }

        /// <summary>注销一个层。</summary>
        public bool RemoveLayer(CanvasLayerReal layer) => _layers.Remove(layer);

        /// <summary>按 Layer 升序排序（值小先渲染，在底层）。</summary>
        public void SortLayers()
            => _layers.Sort((a, b) => a.Layer.CompareTo(b.Layer));

        /// <summary>返回 Visible=true 的层，保持当前顺序。</summary>
        public List<CanvasLayerReal> GetVisibleLayers()
        {
            var result = new List<CanvasLayerReal>();
            foreach (var l in _layers)
                if (l.Visible) result.Add(l);
            return result;
        }

        /// <summary>按 Layer 值查找层（未找到返回 null）。</summary>
        public CanvasLayerReal? GetLayerByValue(int layerValue)
        {
            foreach (var l in _layers)
                if (l.Layer == layerValue) return l;
            return null;
        }

        /// <summary>按名查找层。</summary>
        public CanvasLayerReal? GetLayerByName(string name)
        {
            foreach (var l in _layers)
                if (l.Name == name) return l;
            return null;
        }

        /// <summary>
        /// 求解渲染顺序：Visible=true 的层按 Layer 升序返回名称。
        /// 隐藏层被跳过。
        /// </summary>
        public List<string> GetRenderOrder()
        {
            SortLayers();
            var order = new List<string>();
            foreach (var l in _layers)
                if (l.Visible) order.Add(l.Name);
            return order;
        }
    }
}
