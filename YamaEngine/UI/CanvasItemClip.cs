// =============================================================================
//  CanvasItemClip.cs
//  yama引擎 - CanvasItem 裁剪（对应 Godot 的 clip_contents + VisibilityRect）
//
//  对应 Godot 3 的裁剪语义：
//    * clip_contents：父节点开启后，子节点绘制限制在父节点的矩形范围内
//    * clip_children：CLIP_CHILDREN_DISABLED / CLIP_CHILDREN_ONLY /
//      CLIP_CHILDREN_AND_DRAW / CLIP_CHILDREN_MASK_ONLY
//    * VisibilityRect：可见性矩形（局部空间），用于视锥剔除（culling）
//    * 裁剪判定：子节点的全局 AABB 与父裁剪矩形（全局）求交
//    * 继承链：多个祖先开启裁剪时，取交集（最严格的一个生效）
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>裁剪模式（对应 Godot CanvasItem.clip_children）。</summary>
    public enum ClipMode
    {
        Disabled,   // 不裁剪子节点
        Only,       // 裁剪子节点，自身不绘制
        AndDraw,    // 裁剪子节点，自身仍绘制
        MaskOnly,   // 仅作为遮罩，自身与子节点都不绘制
    }

    /// <summary>
    /// 裁剪矩形（对应 Godot 的 clip 范围）。局部空间定义，全局通过 Node2D 变换。
    /// </summary>
    public sealed class ClipRect
    {
        /// <summary>局部空间左上角。</summary>
        public Vector2 Position { get; set; }

        /// <summary>局部空间尺寸（宽高）。</summary>
        public Vector2 Size { get; set; }

        public ClipRect() { Position = Vector2.Zero; Size = Vector2.Zero; }
        public ClipRect(Vector2 position, Vector2 size) { Position = position; Size = size; }
        public ClipRect(float x, float y, float w, float h) { Position = new(x, y); Size = new(w, h); }

        /// <summary>局部 AABB 右下角。</summary>
        public Vector2 End => Position + Size;

        /// <summary>局部中心。</summary>
        public Vector2 Center => Position + Size * 0.5f;

        /// <summary>是否有效（尺寸非负）。</summary>
        public bool IsValid => Size.X >= 0f && Size.Y >= 0f;
    }

    /// <summary>
    /// 可见性矩形（对应 Godot VisibilityRect / get_visibility_rect）。
    /// 局部空间 AABB，用于视锥剔除判定。
    /// </summary>
    public sealed class VisibilityRect
    {
        /// <summary>局部空间左上角。</summary>
        public Vector2 Position { get; set; } = Vector2.Zero;

        /// <summary>局部空间尺寸。</summary>
        public Vector2 Size { get; set; } = new(32f, 32f);

        public VisibilityRect() { }
        public VisibilityRect(Vector2 position, Vector2 size) { Position = position; Size = size; }
        public VisibilityRect(float x, float y, float w, float h) { Position = new(x, y); Size = new(w, h); }

        /// <summary>局部 AABB 右下角。</summary>
        public Vector2 End => Position + Size;

        /// <summary>局部中心。</summary>
        public Vector2 Center => Position + Size * 0.5f;
    }

    /// <summary>
    /// 裁剪扩展（给 CanvasItem2D / Node2D 加 clip_contents、clip_mode、clip_rect、visibility_rect）。
    /// 用外部字典存储数据，避免修改 CanvasItem2D 基类。
    /// </summary>
    public static class CanvasItemClipExtensions
    {
        private static readonly Dictionary<CanvasItem2D, ClipMode> _clipModes = new();
        private static readonly Dictionary<CanvasItem2D, ClipRect> _clipRects = new();
        private static readonly Dictionary<CanvasItem2D, VisibilityRect> _visibilityRects = new();

        /// <summary>裁剪模式（默认 Disabled）。</summary>
        public static ClipMode GetClipMode(this CanvasItem2D item)
            => _clipModes.TryGetValue(item, out var m) ? m : ClipMode.Disabled;

        public static void SetClipMode(this CanvasItem2D item, ClipMode mode)
        {
            if (mode == ClipMode.Disabled) _clipModes.Remove(item);
            else _clipModes[item] = mode;
        }

        /// <summary>裁剪矩形（局部空间）。null 表示未设置（默认按 Size 或可见矩形推断）。</summary>
        public static ClipRect? GetClipRect(this CanvasItem2D item)
            => _clipRects.TryGetValue(item, out var r) ? r : null;

        public static void SetClipRect(this CanvasItem2D item, ClipRect rect)
            => _clipRects[item] = rect;

        public static void ClearClipRect(this CanvasItem2D item) => _clipRects.Remove(item);

        /// <summary>可见性矩形（局部空间）。null 时用默认 (0,0,32,32)。</summary>
        public static VisibilityRect? GetVisibilityRect(this CanvasItem2D item)
            => _visibilityRects.TryGetValue(item, out var r) ? r : null;

        public static void SetVisibilityRect(this CanvasItem2D item, VisibilityRect rect)
            => _visibilityRects[item] = rect;

        public static void ClearVisibilityRect(this CanvasItem2D item) => _visibilityRects.Remove(item);

        /// <summary>清理节点的所有裁剪数据（节点销毁时调用）。</summary>
        public static void ClearClipData(this CanvasItem2D item)
        {
            _clipModes.Remove(item);
            _clipRects.Remove(item);
            _visibilityRects.Remove(item);
        }

        /// <summary>
        /// 获取节点的全局裁剪矩形（考虑 Node2D 变换）。
        /// 返回 null 表示该节点不裁剪。
        /// </summary>
        public static Rect2? GetGlobalClipRect(this Node2D node)
        {
            var mode = node.GetClipMode();
            if (mode == ClipMode.Disabled) return null;

            var clipRect = node.GetClipRect();
            if (clipRect == null)
            {
                // 未显式设置：用可见性矩形或默认 (0,0,32,32)
                var vis = node.GetVisibilityRect();
                clipRect = vis != null
                    ? new ClipRect(vis.Position, vis.Size)
                    : new ClipRect(0f, 0f, 32f, 32f);
            }

            // 局部四角 → 全局
            var lt = Vector2.Transform(clipRect.Position, node.GlobalTransform);
            var rb = Vector2.Transform(clipRect.End, node.GlobalTransform);
            // 假设无旋转：min/max
            float minX = MathF.Min(lt.X, rb.X), minY = MathF.Min(lt.Y, rb.Y);
            float maxX = MathF.Max(lt.X, rb.X), maxY = MathF.Max(lt.Y, rb.Y);
            return new Rect2(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 获取节点的全局可见性矩形（考虑 Node2D 变换）。
        /// </summary>
        public static Rect2 GetGlobalVisibilityRect(this Node2D node)
        {
            var vis = node.GetVisibilityRect() ?? new VisibilityRect();
            var lt = Vector2.Transform(vis.Position, node.GlobalTransform);
            var rb = Vector2.Transform(vis.End, node.GlobalTransform);
            float minX = MathF.Min(lt.X, rb.X), minY = MathF.Min(lt.Y, rb.Y);
            float maxX = MathF.Max(lt.X, rb.X), maxY = MathF.Max(lt.Y, rb.Y);
            return new Rect2(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 判断节点是否被**任一祖先**裁剪剔除（全局 AABB 与祖先裁剪矩形求交）。
        /// 返回 true 表示应被剔除（不绘制）。
        /// </summary>
        public static bool IsClippedByAncestors(this CanvasItem2D item)
        {
            if (item is not Node2D node) return false;
            var itemGlobal = node.GetGlobalVisibilityRect();

            var p = node.Parent;
            while (p != null)
            {
                if (p is Node2D pNode)
                {
                    var clipGlobal = pNode.GetGlobalClipRect();
                    if (clipGlobal != null)
                    {
                        // AABB 求交：不相交则被剔除
                        if (!RectsIntersect(itemGlobal, clipGlobal.Value))
                            return true;
                    }
                }
                p = p.Parent;
            }
            return false;
        }

        /// <summary>AABB 相交测试。</summary>
        public static bool RectsIntersect(Rect2 a, Rect2 b)
        {
            if (a.Width <= 0f || a.Height <= 0f) return false;
            if (b.Width <= 0f || b.Height <= 0f) return false;
            return a.Position.X < b.Position.X + b.Width
                && a.Position.X + a.Width > b.Position.X
                && a.Position.Y < b.Position.Y + b.Height
                && a.Position.Y + a.Height > b.Position.Y;
        }

        /// <summary>AABB 求交（返回交集矩形；不相交返回 null）。</summary>
        public static Rect2? Intersect(Rect2 a, Rect2 b)
        {
            float minX = MathF.Max(a.Position.X, b.Position.X);
            float minY = MathF.Max(a.Position.Y, b.Position.Y);
            float maxX = MathF.Min(a.Position.X + a.Width, b.Position.X + b.Width);
            float maxY = MathF.Min(a.Position.Y + a.Height, b.Position.Y + b.Height);
            if (maxX <= minX || maxY <= minY) return null;
            return new Rect2(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 计算节点绘制时受祖先裁剪影响后的**有效可见矩形**（全局空间）。
        /// 多个祖先裁剪矩形取交集。
        /// </summary>
        public static Rect2? GetEffectiveVisibleRect(this Node2D node)
        {
            var vis = node.GetGlobalVisibilityRect();
            var p = node.Parent;
            while (p != null)
            {
                if (p is Node2D pNode)
                {
                    var clip = pNode.GetGlobalClipRect();
                    if (clip != null)
                    {
                        var intersected = Intersect(vis, clip.Value);
                        if (intersected == null) return null; // 完全被裁
                        vis = intersected.Value;
                    }
                }
                p = p.Parent;
            }
            return vis;
        }
    }
}
