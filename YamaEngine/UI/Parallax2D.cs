// =============================================================================
//  Yama Engine - Parallax2D.cs
//  yama引擎 - 视差滚动背景（对应 Godot 的 ParallaxBackground / ParallaxLayer）
//
//  对应 Godot 视差语义：
//    * ParallaxLayer extends Node2D：
//        MotionScale     (Vector2) — 相机移动时层的相对移动速率。
//        MotionMirroring (Vector2) — 层在该间距上重复平铺。
//    * ParallaxBackground：
//        跟踪 Camera2D 的位置，每帧计算 ScrollOffset = -camera_center，
//        把 ScrollBaseOffset + ScrollOffset * MotionScale 应用到每个 ParallaxLayer。
//        MotionScale=(0,0) → 固定不动（无限远）；
//        MotionScale=(1,1) → 与相机同速反向移动（等于世界坐标中的普通层）。
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>
    /// 视差层（对应 Godot ParallaxLayer）：挂在 ParallaxBackground 下，
    /// 由 ParallaxBackground.UpdateScroll 每帧设置位置。
    /// </summary>
    public sealed class ParallaxLayer : Node2D
    {
        /// <summary>
        /// 相机移动时该层的相对移动速率。
        /// (0,0) = 固定不动（无限远）；(1,1) = 与世界坐标同步移动。
        /// </summary>
        public Vector2 MotionScale { get; set; } = Vector2.One;

        /// <summary>
        /// 平铺镜像间距（像素）。
        /// 非零分量让层位置在该间距上取模循环，实现无限平铺。
        /// </summary>
        public Vector2 MotionMirroring { get; set; } = Vector2.Zero;

        /// <summary>上一帧 ApplyScroll 写入的屏幕偏移。</summary>
        public Vector2 ScreenOffset { get; private set; }

        public ParallaxLayer(string name = "ParallaxLayer") : base(name) { }

        /// <summary>
        /// 计算并应用该层在相机移动后的屏幕位置。
        /// screen = baseOffset + scrollOffset * MotionScale，再按 MotionMirroring 取模。
        /// </summary>
        internal void ApplyScroll(Vector2 baseOffset, Vector2 scrollOffset)
        {
            var pos = baseOffset + scrollOffset * MotionScale;

            if (MotionMirroring.X > 0f)
                pos.X = Wrap(pos.X, MotionMirroring.X);
            if (MotionMirroring.Y > 0f)
                pos.Y = Wrap(pos.Y, MotionMirroring.Y);

            Position = pos;
            ScreenOffset = pos;
        }

        /// <summary>Python 风格取模：结果始终落在 [0, mod) 内。</summary>
        private static float Wrap(float value, float mod)
        {
            var r = value % mod;
            return r < 0f ? r + mod : r;
        }
    }

    /// <summary>
    /// 视差背景容器（对应 Godot ParallaxBackground）：
    /// 跟踪 Camera2D，每帧计算 ScrollOffset 并推送到所有 ParallaxLayer。
    /// 不继承 CanvasLayerReal（保持轻量；渲染层排序由外部 LayerStack 处理）。
    /// </summary>
    public sealed class ParallaxBackground
    {
        /// <summary>背景名称。</summary>
        public string Name { get; set; } = "ParallaxBackground";

        /// <summary>渲染层号（配合 CanvasLayerStack 排序）。</summary>
        public int Layer { get; set; }

        /// <summary>是否可见。</summary>
        public bool Visible { get; set; } = true;

        /// <summary>跟踪的相机（为 null 时 UpdateScroll 不生效）。</summary>
        public Camera2D? Camera { get; set; }

        /// <summary>滚动基准偏移（对应 Godot scroll_base_offset）。</summary>
        public Vector2 ScrollBaseOffset { get; set; } = Vector2.Zero;

        /// <summary>当前滚动偏移（每帧 UpdateScroll 更新，= -camera_center）。</summary>
        public Vector2 ScrollOffset { get; private set; }

        /// <summary>是否忽略相机缩放（相机 zoom 变化时背景不额外移动）。</summary>
        public bool IgnoreCameraZoom { get; set; }

        private readonly List<ParallaxLayer> _layers = new();

        /// <summary>视差层数量。</summary>
        public int LayerCount => _layers.Count;

        /// <summary>只读层列表。</summary>
        public IReadOnlyList<ParallaxLayer> Layers => _layers;

        public ParallaxBackground() { }
        public ParallaxBackground(string name) { Name = name; }

        /// <summary>添加视差层。</summary>
        public void AddLayer(ParallaxLayer layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            _layers.Add(layer);
        }

        /// <summary>移除视差层。</summary>
        public bool RemoveLayer(ParallaxLayer layer) => _layers.Remove(layer);

        /// <summary>
        /// 每帧调用：计算 ScrollOffset = -camera_center，推送到所有 ParallaxLayer。
        /// Camera 为 null 时不做任何事。
        /// </summary>
        public void UpdateScroll()
        {
            if (Camera == null) return;
            ScrollOffset = -Camera.GetCenter();
            foreach (var layer in _layers)
                layer.ApplyScroll(ScrollBaseOffset, ScrollOffset);
        }

        /// <summary>
        /// 便捷方法：用相机当前位置直接计算某 motion_scale 下的预期屏幕位置。
        /// 测试可用它做断言。
        /// </summary>
        public static Vector2 PredictScreenOffset(Vector2 cameraCenter, Vector2 motionScale, Vector2 baseOffset)
            => baseOffset + (-cameraCenter) * motionScale;
    }
}
