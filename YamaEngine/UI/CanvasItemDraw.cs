// =============================================================================
//  CanvasItemDraw.cs
//  yama引擎 - CanvasItem 即时绘制命令（对应 Godot 的 CanvasItem.draw_* API）
//
//  对应 Godot 2D 自定义绘制语义：
//    * draw_line / draw_rect / draw_circle / draw_polyline / draw_colored_polygon
//    * 绘制命令在**本地空间**录入，渲染时用节点全局变换转到世界空间。
//    * 每帧清空（immediate mode）——引擎在 draw 帧回调里重建命令缓冲。
//    * filled / outline 区分（filled=false 为描边，配 width）。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>绘制基元类型（对应 Godot 的各种 draw_* 方法）。</summary>
    public enum DrawPrimitive
    {
        Line,
        Rect,
        Circle,
        Polyline,
        Polygon,
    }

    /// <summary>单条绘制命令（本地空间，颜色为 RGBA 0~1 浮点）。</summary>
    public sealed class DrawCommand
    {
        /// <summary>基元类型。</summary>
        public DrawPrimitive Primitive { get; set; }

        /// <summary>主点：线起点 / 矩形左上 / 圆心 / 折线与多边形主列表。</summary>
        public Vector2 A { get; set; }

        /// <summary>次点：线终点（Line 专用）。</summary>
        public Vector2 B { get; set; }

        /// <summary>矩形尺寸（Rect 专用）。</summary>
        public Vector2 Size { get; set; }

        /// <summary>半径（Circle 专用）。</summary>
        public float Radius { get; set; }

        /// <summary>圆近似段数（Circle 专用，默认 16）。</summary>
        public int CircleSegments { get; set; } = 16;

        /// <summary>线宽（描边用，像素单位）。</summary>
        public float Width { get; set; } = 1f;

        /// <summary>颜色 RGBA。</summary>
        public Vector4 Color { get; set; } = Vector4.One;

        /// <summary>是否填充（false = 仅描边）。</summary>
        public bool Filled { get; set; } = true;

        /// <summary>折线/多边形顶点（本地空间）。</summary>
        public List<Vector2> Points { get; } = new();
    }

    /// <summary>
    /// CanvasItem2D 的即时绘制扩展（对应 Godot CanvasItem.draw_*）。
    /// 命令在本地空间录入，GetWorldCommand 返回按全局变换转换后的副本。
    /// </summary>
    public static class CanvasItemDrawExtensions
    {
        /// <summary>画线段（对应 draw_line）。</summary>
        public static DrawCommand DrawLine(
            this CanvasItem2D item, Vector2 from, Vector2 to, Vector4 color, float width = 1f)
        {
            var cmd = new DrawCommand
            {
                Primitive = DrawPrimitive.Line,
                A = from,
                B = to,
                Color = color,
                Width = width,
                Filled = false,
            };
            item.DrawCommands.Add(cmd);
            return cmd;
        }

        /// <summary>画矩形（对应 draw_rect）。position 为左上角。</summary>
        public static DrawCommand DrawRect(
            this CanvasItem2D item, Vector2 position, Vector2 size,
            Vector4 color, bool filled = true, float width = 1f)
        {
            var cmd = new DrawCommand
            {
                Primitive = DrawPrimitive.Rect,
                A = position,
                Size = size,
                Color = color,
                Filled = filled,
                Width = width,
            };
            item.DrawCommands.Add(cmd);
            return cmd;
        }

        /// <summary>画圆（对应 draw_circle）。</summary>
        public static DrawCommand DrawCircle(
            this CanvasItem2D item, Vector2 position, float radius,
            Vector4 color, bool filled = true, int segments = 16)
        {
            var cmd = new DrawCommand
            {
                Primitive = DrawPrimitive.Circle,
                A = position,
                Radius = radius,
                Color = color,
                Filled = filled,
                CircleSegments = segments,
            };
            item.DrawCommands.Add(cmd);
            return cmd;
        }

        /// <summary>画折线（对应 draw_polyline，不闭合）。</summary>
        public static DrawCommand DrawPolyline(
            this CanvasItem2D item, IReadOnlyList<Vector2> points, Vector4 color, float width = 1f)
        {
            var cmd = new DrawCommand
            {
                Primitive = DrawPrimitive.Polyline,
                Color = color,
                Width = width,
                Filled = false,
            };
            cmd.Points.AddRange(points);
            item.DrawCommands.Add(cmd);
            return cmd;
        }

        /// <summary>画填充多边形（对应 draw_colored_polygon）。</summary>
        public static DrawCommand DrawColoredPolygon(
            this CanvasItem2D item, IReadOnlyList<Vector2> points, Vector4 color)
        {
            var cmd = new DrawCommand
            {
                Primitive = DrawPrimitive.Polygon,
                Color = color,
                Filled = true,
            };
            cmd.Points.AddRange(points);
            item.DrawCommands.Add(cmd);
            return cmd;
        }

        /// <summary>把一条命令按矩阵转到世界空间（返回新命令，不修改原命令）。</summary>
        public static DrawCommand TransformCommand(DrawCommand cmd, Matrix3x2 xform)
        {
            var outCmd = new DrawCommand
            {
                Primitive = cmd.Primitive,
                A = Vector2.Transform(cmd.A, xform),
                B = Vector2.Transform(cmd.B, xform),
                Size = cmd.Size,
                Radius = cmd.Radius,
                CircleSegments = cmd.CircleSegments,
                Width = cmd.Width,
                Color = cmd.Color,
                Filled = cmd.Filled,
            };
            foreach (var p in cmd.Points)
                outCmd.Points.Add(Vector2.Transform(p, xform));
            return outCmd;
        }
    }
}
