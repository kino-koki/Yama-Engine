// =============================================================================
//  Yama Engine - UIRendererReal.cs
//  yama引擎 - 真实 UI 渲染器
//
//  本文件实现完整的即时模式 UI 渲染管线，所有方法均包含真实算法：
//    * UIVertex           - UI 顶点（位置/UV/颜色/字体通道）
//    * DPIScale           - DPI 缩放（逻辑像素→物理像素）
//    * UIVertexGenerator  - 几何生成（矩形/圆角矩形/圆/椭圆/弧/线段/路径）
//    * FontGlyph          - 字形（字符/UV矩形/Advance/位图/尺寸/Bearing）
//    * FontAtlas          - 字体图集（纹理/字形字典/行高/基线/间距）
//    * GlyphRenderer      - 字形渲染器（逐字符布局/换行/对齐/Kerning）
//    * UIBatch            - 批处理（相同纹理合并为单 DrawCall）
//    * UIRendererReal     - 主渲染器（BeginDraw/EndDraw/DrawRect/DrawText/DPI/裁剪）
//
//  顶点生成: 矩形/圆角矩形(弧形采样→三角扇)/椭圆/弧/线段(矩形旋转)/路径(法线扩展)
//  字体渲染: 字形查表→Quad生成→逐字符布局→换行检测→对齐→Kerning调整
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：UI 顶点与基础类型
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>UI 顶点（位置/UV/颜色/字体通道）。</summary>
    public struct UIVertex
    {
        public Vector2 Position;
        public Vector2 UV;
        public Vector4 Color;
        public byte FontChannel; // 0=R, 1=G, 2=B, 3=A（SDF字体通道）

        public UIVertex(Vector2 pos, Vector2 uv, Vector4 color, byte fontChannel = 3)
        {
            Position = pos;
            UV = uv;
            Color = color;
            FontChannel = fontChannel;
        }

        public UIVertex(Vector2 pos, Vector2 uv, Vector4 color)
        {
            Position = pos;
            UV = uv;
            Color = color;
            FontChannel = 3;
        }
    }

    /// <summary>UI 顶点（简化版，使用 System.Numerics.Vector4 作为颜色）。</summary>
    public struct UISimpleVertex
    {
        public Vector2 Position;
        public Vector2 UV;
        public Vector4 Color;

        public UISimpleVertex(Vector2 pos, Vector2 uv, Vector4 color)
        {
            Position = pos;
            UV = uv;
            Color = color;
        }
    }

    /// <summary>文本对齐方式。</summary>
    public enum UITextAlignment { Left, Center, Right }

    /// <summary>UI 文本换行模式。</summary>
    public enum UITextWrapMode { None, WordWrap, CharWrap }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：DPI 缩放
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// DPI 缩放器。将逻辑像素转换为物理像素，维护缩放因子与缩放矩阵。
    /// 公式: physical = logical * (currentDPI / baseDPI)
    /// </summary>
    public sealed class DPIScale
    {
        /// <summary>缩放因子（1.0 = 96 DPI 基准）。</summary>
        public float Scale { get; private set; } = 1f;

        /// <summary>基础 DPI（Windows 标准 96）。</summary>
        public const float BaseDPI = 96f;

        /// <summary>当前 DPI。</summary>
        public float CurrentDPI => BaseDPI * Scale;

        public DPIScale() { }
        public DPIScale(float scale) { Scale = Math.Max(0.25f, scale); }

        /// <summary>从系统 DPI 设置缩放因子。</summary>
        public void SetFromDPI(float dpi) => Scale = Math.Max(0.25f, dpi / BaseDPI);

        /// <summary>逻辑像素→物理像素。</summary>
        public float ToPhysical(float logical) => logical * Scale;

        /// <summary>物理像素→逻辑像素。</summary>
        public float ToLogical(float physical) => physical / Scale;

        /// <summary>逻辑坐标→物理坐标。</summary>
        public Vector2 ToPhysical(Vector2 logical) => logical * Scale;

        /// <summary>物理坐标→逻辑坐标。</summary>
        public Vector2 ToLogical(Vector2 physical) => physical / Scale;

        /// <summary>缩放矩阵（3x2，用于着色器投影矩阵）。</summary>
        public Matrix3x2 ScaleMatrix => Matrix3x2.CreateScale(Scale);

        /// <summary>缩放矩形。</summary>
        public (float x, float y, float w, float h) ScaleRect(float x, float y, float w, float h)
            => (x * Scale, y * Scale, w * Scale, h * Scale);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：顶点生成器（几何图元→三角形网格）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UI 顶点生成器。将几何图元转换为三角形网格（顶点列表 + 索引列表）。
    /// 所有方法使用真实数学公式生成顶点位置和索引。
    /// </summary>
    public static class UIVertexGenerator
    {
        /// <summary>
        /// 生成矩形（4 顶点 + 6 索引 = 2 三角形）。
        /// 顶点顺序: 左下→右下→右上→左上（逆时针绕序）。
        /// </summary>
        public static void GenerateQuad(
            float x, float y, float w, float h, Vector4 color,
            Vector2 uvMin, Vector2 uvMax,
            List<UIVertex> vertices, List<ushort> indices)
        {
            int baseIdx = vertices.Count;
            vertices.Add(new UIVertex(new Vector2(x, y + h), uvMin, color));         // 左上 → UV(0,0)
            vertices.Add(new UIVertex(new Vector2(x + w, y + h), new Vector2(uvMax.X, uvMin.Y), color)); // 右上 → UV(1,0)
            vertices.Add(new UIVertex(new Vector2(x + w, y), uvMax, color));         // 右下 → UV(1,1)
            vertices.Add(new UIVertex(new Vector2(x, y), new Vector2(uvMin.X, uvMax.Y), color)); // 左下 → UV(0,1)

            indices.Add((ushort)(baseIdx + 0));
            indices.Add((ushort)(baseIdx + 1));
            indices.Add((ushort)(baseIdx + 2));
            indices.Add((ushort)(baseIdx + 0));
            indices.Add((ushort)(baseIdx + 2));
            indices.Add((ushort)(baseIdx + 3));
        }

        /// <summary>生成矩形（默认 UV 0-1）。</summary>
        public static void GenerateQuad(
            float x, float y, float w, float h, Vector4 color,
            List<UIVertex> vertices, List<ushort> indices)
        {
            GenerateQuad(x, y, w, h, color, Vector2.Zero, Vector2.One, vertices, indices);
        }

        /// <summary>
        /// 生成圆角矩形。
        /// 算法：4 个角各生成一个弧形三角扇（segments 段），中间用矩形填充。
        /// 每个角的弧形以角点为圆心，radius 为半径，在 [startAngle, endAngle] 内均匀采样。
        /// 圆角矩形由以下区域组成：
        ///   - 中心矩形（不包含圆角区域）
        ///   - 上/下/左/右边条（矩形到圆角过渡）
        ///   - 4 个角的弧形扇区
        /// 简化实现：将圆角矩形视为一个大矩形减去4个内切矩形，通过弧形三角扇填充角落。
        /// </summary>
        public static void GenerateRoundedRect(
            float x, float y, float w, float h, float radius, int cornerSegments,
            Vector4 color, List<UIVertex> vertices, List<ushort> indices)
        {
            // 钳制 radius 不超过短边一半
            float maxR = Math.Min(w, h) * 0.5f;
            radius = Math.Clamp(radius, 0f, maxR);

            if (radius < 0.5f)
            {
                // 退化为普通矩形
                GenerateQuad(x, y, w, h, color, vertices, indices);
                return;
            }

            int segs = Math.Max(3, cornerSegments);

            // 中心矩形坐标
            float cx0 = x + radius;
            float cy0 = y + radius;
            float cx1 = x + w - radius;
            float cy1 = y + h - radius;

            // 辅助：添加一个三角扇（center + arc vertices）
            void AddCornerFan(float centerX, float centerY, float startAngle, float endAngle)
            {
                int centerIdx = vertices.Count;
                vertices.Add(new UIVertex(new Vector2(centerX, centerY), Vector2.Zero, color));

                for (int i = 0; i <= segs; i++)
                {
                    float t = i / (float)segs;
                    float angle = startAngle + (endAngle - startAngle) * t;
                    float px = centerX + MathF.Cos(angle) * radius;
                    float py = centerY + MathF.Sin(angle) * radius;
                    vertices.Add(new UIVertex(new Vector2(px, py), Vector2.Zero, color));
                }

                // 三角扇索引：center → [i, i+1]
                for (int i = 0; i < segs; i++)
                {
                    indices.Add((ushort)centerIdx);
                    indices.Add((ushort)(centerIdx + 1 + i));
                    indices.Add((ushort)(centerIdx + 2 + i));
                }
            }

            // 中心矩形（两个三角形）
            GenerateQuad(cx0, cy0, cx1 - cx0, cy1 - cy0, color, vertices, indices);

            // 上边条
            GenerateQuad(cx0, cy1, cx1 - cx0, y + h - cy1, color, vertices, indices);
            // 下边条
            GenerateQuad(cx0, y, cx1 - cx0, cy0 - y, color, vertices, indices);
            // 左边条
            GenerateQuad(x, cy0, cx0 - x, cy1 - cy0, color, vertices, indices);
            // 右边条
            GenerateQuad(cx1, cy0, x + w - cx1, cy1 - cy0, color, vertices, indices);

            // 四个角的弧形三角扇
            // 左下角: 圆心(cx0, cy0), 角度[PI, 3PI/2]
            AddCornerFan(cx0, cy0, MathF.PI, MathF.PI * 1.5f);
            // 右下角: 圆心(cx1, cy0), 角度[3PI/2, 2PI]
            AddCornerFan(cx1, cy0, MathF.PI * 1.5f, MathF.PI * 2f);
            // 右上角: 圆心(cx1, cy1), 角度[0, PI/2]
            AddCornerFan(cx1, cy1, 0f, MathF.PI * 0.5f);
            // 左上角: 圆心(cx0, cy1), 角度[PI/2, PI]
            AddCornerFan(cx0, cy1, MathF.PI * 0.5f, MathF.PI);
        }

        /// <summary>
        /// 生成圆形（中心 + segments 段三角扇）。
        /// 算法：以 (cx, cy) 为圆心，均匀在 [0, 2PI] 内采样 segments 个点，
        /// 与圆心组成三角形扇。
        /// </summary>
        public static void GenerateCircle(
            float cx, float cy, float radius, int segments,
            Vector4 color, List<UIVertex> vertices, List<ushort> indices)
        {
            radius = Math.Max(0.001f, radius);
            segments = Math.Max(3, segments);

            int centerIdx = vertices.Count;
            vertices.Add(new UIVertex(new Vector2(cx, cy), new Vector2(0.5f, 0.5f), color));

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = t * MathF.PI * 2f;
                float px = cx + MathF.Cos(angle) * radius;
                float py = cy + MathF.Sin(angle) * radius;
                // UV: 将角度和半径映射到 [0,1]
                float u = 0.5f + MathF.Cos(angle) * 0.5f;
                float v = 0.5f + MathF.Sin(angle) * 0.5f;
                vertices.Add(new UIVertex(new Vector2(px, py), new Vector2(u, v), color));
            }

            // 三角扇索引
            for (int i = 0; i < segments; i++)
            {
                indices.Add((ushort)centerIdx);
                indices.Add((ushort)(centerIdx + 1 + i));
                indices.Add((ushort)(centerIdx + 2 + i));
            }
        }

        /// <summary>
        /// 生成椭圆。
        /// 算法：与圆形类似，但 x/y 方向使用不同的半径 rx/ry。
        /// px = cx + cos(angle) * rx, py = cy + sin(angle) * ry
        /// </summary>
        public static void GenerateEllipse(
            float cx, float cy, float rx, float ry, int segments,
            Vector4 color, List<UIVertex> vertices, List<ushort> indices)
        {
            rx = Math.Max(0.001f, rx);
            ry = Math.Max(0.001f, ry);
            segments = Math.Max(3, segments);

            int centerIdx = vertices.Count;
            vertices.Add(new UIVertex(new Vector2(cx, cy), new Vector2(0.5f, 0.5f), color));

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = t * MathF.PI * 2f;
                float px = cx + MathF.Cos(angle) * rx;
                float py = cy + MathF.Sin(angle) * ry;
                float u = 0.5f + MathF.Cos(angle) * 0.5f;
                float v = 0.5f + MathF.Sin(angle) * 0.5f;
                vertices.Add(new UIVertex(new Vector2(px, py), new Vector2(u, v), color));
            }

            for (int i = 0; i < segments; i++)
            {
                indices.Add((ushort)centerIdx);
                indices.Add((ushort)(centerIdx + 1 + i));
                indices.Add((ushort)(centerIdx + 2 + i));
            }
        }

        /// <summary>
        /// 生成弧形。
        /// 算法：以 (cx, cy) 为圆心，radius 为半径，
        /// 在 [startAngle, startAngle + sweepAngle] 内均匀采样 segments 个点，
        /// 与圆心组成三角形扇。
        /// </summary>
        public static void GenerateArc(
            float cx, float cy, float radius,
            float startAngle, float sweepAngle, int segments,
            float thickness, Vector4 color,
            List<UIVertex> vertices, List<ushort> indices)
        {
            radius = Math.Max(0.001f, radius);
            thickness = Math.Max(0.001f, thickness);
            segments = Math.Max(2, segments);

            float innerR = Math.Max(0.001f, radius - thickness * 0.5f);
            float outerR = radius + thickness * 0.5f;

            for (int i = 0; i < segments; i++)
            {
                float t0 = i / (float)segments;
                float t1 = (i + 1) / (float)segments;
                float a0 = startAngle + sweepAngle * t0;
                float a1 = startAngle + sweepAngle * t1;

                float cos0 = MathF.Cos(a0), sin0 = MathF.Sin(a0);
                float cos1 = MathF.Cos(a1), sin1 = MathF.Sin(a1);

                // 内弧两点
                Vector2 i0 = new Vector2(cx + cos0 * innerR, cy + sin0 * innerR);
                Vector2 i1 = new Vector2(cx + cos1 * innerR, cy + sin1 * innerR);
                // 外弧两点
                Vector2 o0 = new Vector2(cx + cos0 * outerR, cy + sin0 * outerR);
                Vector2 o1 = new Vector2(cx + cos1 * outerR, cy + sin1 * outerR);

                int baseIdx = vertices.Count;
                vertices.Add(new UIVertex(i0, Vector2.Zero, color));
                vertices.Add(new UIVertex(o0, Vector2.Zero, color));
                vertices.Add(new UIVertex(o1, Vector2.Zero, color));
                vertices.Add(new UIVertex(i1, Vector2.Zero, color));

                indices.Add((ushort)(baseIdx + 0));
                indices.Add((ushort)(baseIdx + 1));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 0));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 3));
            }
        }

        /// <summary>
        /// 生成线段（矩形旋转实现）。
        /// 算法：将线段视为一个长为 length、宽为 thickness 的矩形，
        /// 沿线段方向旋转。法线方向 = 垂直于线段方向的单位向量 * thickness/2。
        /// </summary>
        public static void GenerateLine(
            float x0, float y0, float x1, float y1, float thickness,
            Vector4 color, List<UIVertex> vertices, List<ushort> indices)
        {
            float dx = x1 - x0;
            float dy = y1 - y0;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;

            // 法线方向（垂直于线段方向）
            float nx = -dy / len * thickness * 0.5f;
            float ny = dx / len * thickness * 0.5f;

            int baseIdx = vertices.Count;
            vertices.Add(new UIVertex(new Vector2(x0 - nx, y0 - ny), Vector2.Zero, color));
            vertices.Add(new UIVertex(new Vector2(x0 + nx, y0 + ny), Vector2.Zero, color));
            vertices.Add(new UIVertex(new Vector2(x1 + nx, y1 + ny), Vector2.Zero, color));
            vertices.Add(new UIVertex(new Vector2(x1 - nx, y1 - ny), Vector2.Zero, color));

            indices.Add((ushort)(baseIdx + 0));
            indices.Add((ushort)(baseIdx + 1));
            indices.Add((ushort)(baseIdx + 2));
            indices.Add((ushort)(baseIdx + 0));
            indices.Add((ushort)(baseIdx + 2));
            indices.Add((ushort)(baseIdx + 3));
        }

        /// <summary>
        /// 生成路径（折线→三角形带）。
        /// 算法：沿路径点序列，在每个顶点处计算前后线段方向的平均法线，
        /// 然后向两侧扩展 width/2 生成上下两个顶点，形成三角形带。
        /// </summary>
        public static void GeneratePath(
            List<Vector2> points, float width,
            Vector4 color, List<UIVertex> vertices, List<ushort> indices)
        {
            if (points.Count < 2) return;
            width = Math.Max(0.001f, width);
            float halfW = width * 0.5f;

            // 预计算每段的方向和长度
            int n = points.Count;
            Vector2[] normals = new Vector2[n];

            for (int i = 0; i < n; i++)
            {
                Vector2 dir;
                if (i == 0)
                {
                    dir = points[1] - points[0];
                }
                else if (i == n - 1)
                {
                    dir = points[n - 1] - points[n - 2];
                }
                else
                {
                    // 取前后方向平均（平滑拐角）
                    Vector2 d0 = points[i] - points[i - 1];
                    Vector2 d1 = points[i + 1] - points[i];
                    dir = d0 + d1;
                }

                float len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                if (len < 1e-6f)
                {
                    normals[i] = i > 0 ? normals[i - 1] : new Vector2(0, 1);
                    continue;
                }

                // 法线 = 垂直于方向的单位向量
                normals[i] = new Vector2(-dir.Y / len, dir.X / len);
            }

            // 生成上下两行顶点
            for (int i = 0; i < n; i++)
            {
                Vector2 p = points[i];
                Vector2 nm = normals[i];
                vertices.Add(new UIVertex(
                    new Vector2(p.X - nm.X * halfW, p.Y - nm.Y * halfW),
                    Vector2.Zero, color));
                vertices.Add(new UIVertex(
                    new Vector2(p.X + nm.X * halfW, p.Y + nm.Y * halfW),
                    Vector2.Zero, color));
            }

            // 三角形带索引
            for (int i = 0; i < n - 1; i++)
            {
                int baseIdx = vertices.Count - (n - i) * 2;
                int idx0 = baseIdx;
                int idx1 = baseIdx + 1;
                int idx2 = baseIdx + 2;
                int idx3 = baseIdx + 3;

                indices.Add((ushort)idx0);
                indices.Add((ushort)idx1);
                indices.Add((ushort)idx2);
                indices.Add((ushort)idx1);
                indices.Add((ushort)idx3);
                indices.Add((ushort)idx2);
            }
        }

        /// <summary>
        /// 生成填充多边形（耳切法三角化）。
        /// 算法：使用耳切三角化将任意简单多边形分解为三角形。
        /// </summary>
        public static void GeneratePolygon(
            List<Vector2> points, Vector4 color,
            List<UIVertex> vertices, List<ushort> indices)
        {
            if (points.Count < 3) return;

            // 复制顶点列表用于三角化
            var pts = new List<Vector2>(points);
            var idxMap = new List<int>();
            int baseIdx = vertices.Count;

            // 添加所有顶点
            for (int i = 0; i < pts.Count; i++)
            {
                vertices.Add(new UIVertex(pts[i], Vector2.Zero, color));
                idxMap.Add(i);
            }

            // 耳切三角化
            List<(int a, int b, int c)> triangles = EarClipTriangulate(pts);
            foreach (var tri in triangles)
            {
                indices.Add((ushort)(baseIdx + tri.a));
                indices.Add((ushort)(baseIdx + tri.b));
                indices.Add((ushort)(baseIdx + tri.c));
            }
        }

        /// <summary>
        /// 耳切法三角化简单多边形。
        /// 算法：
        ///   1. 找到一个"耳朵"（连续三个顶点形成的三角形完全在多边形内部且无其他顶点在内）
        ///   2. 移除中间顶点，输出该三角形
        ///   3. 重复直到只剩 3 个顶点
        /// </summary>
        private static List<(int a, int b, int c)> EarClipTriangulate(List<Vector2> polygon)
        {
            var result = new List<(int, int, int)>();
            if (polygon.Count < 3) return result;

            var indices = new List<int>();
            for (int i = 0; i < polygon.Count; i++) indices.Add(i);

            int count = polygon.Count;
            int maxIter = count * count; // 防止无限循环
            int iter = 0;

            while (count > 3 && iter < maxIter)
            {
                iter++;
                bool foundEar = false;
                for (int i = 0; i < count; i++)
                {
                    int prev = (i - 1 + count) % count;
                    int curr = i;
                    int next = (i + 1) % count;

                    Vector2 a = polygon[indices[prev]];
                    Vector2 b = polygon[indices[curr]];
                    Vector2 c = polygon[indices[next]];

                    // 检查是否为凸角（叉积 > 0 表示逆时针）
                    float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    if (cross <= 0f) continue;

                    // 检查是否有其他顶点在三角形内部
                    bool isEar = true;
                    for (int j = 0; j < count; j++)
                    {
                        if (j == prev || j == curr || j == next) continue;
                        if (PointInTriangle(polygon[indices[j]], a, b, c))
                        {
                            isEar = false;
                            break;
                        }
                    }

                    if (isEar)
                    {
                        result.Add((indices[prev], indices[curr], indices[next]));
                        indices.RemoveAt(curr);
                        count--;
                        foundEar = true;
                        break;
                    }
                }
                if (!foundEar) break; // 退化多边形
            }

            // 剩余 3 个顶点
            if (count == 3)
            {
                result.Add((indices[0], indices[1], indices[2]));
            }

            return result;
        }

        /// <summary>点是否在三角形 ABC 内（重心坐标法）。</summary>
        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：字体系统
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 字形数据。描述单个字符在字体图集中的位置、尺寸和渲染参数。
    /// </summary>
    public sealed class FontGlyph
    {
        /// <summary>字符编码。</summary>
        public char Character;

        /// <summary>图集中的 UV 矩形（归一化 0-1）。</summary>
        public float U, V, UWidth, VHeight;

        /// <summary>字符水平前进距离（逻辑像素）。</summary>
        public float Advance;

        /// <summary>字形位图（灰度，8bpp，可选）。</summary>
        public byte[]? Bitmap;

        /// <summary>字形像素尺寸（宽×高）。</summary>
        public int BitmapWidth;
        public int BitmapHeight;

        /// <summary>水平偏移（bearing）：字形左边界到原点的距离。</summary>
        public float BearingX;

        /// <summary>垂直偏移（bearing）：基线到字形顶部的距离。</summary>
        public float BearingY;

        /// <summary>字形是否有效。</summary>
        public bool IsValid => BitmapWidth > 0 && BitmapHeight > 0;

        /// <summary>获取字形尺寸向量。</summary>
        public Vector2 Size => new(BitmapWidth, BitmapHeight);
    }

    /// <summary>
    /// 字体图集。将所有字形的位图打包到一张纹理上，使用 Skyline 算法。
    /// </summary>
    public sealed class FontAtlas
    {
        /// <summary>图集纹理尺寸（宽×高，像素）。</summary>
        public int AtlasWidth = 512;
        public int AtlasHeight = 512;

        /// <summary>字形字典（字符→字形数据）。</summary>
        public Dictionary<char, FontGlyph> Glyphs = new();

        /// <summary>行高（逻辑像素）。</summary>
        public float LineHeight = 16f;

        /// <summary>基线位置（从顶部到基线的距离）。</summary>
        public float Baseline = 12f;

        /// <summary>字符间距（额外间距，逻辑像素）。</summary>
        public float Spacing = 1f;

        /// <summary>字体大小（逻辑像素）。</summary>
        public float FontSize = 14f;

        /// <summary>Kerning 表（字符对→调整量）。</summary>
        public Dictionary<(char left, char right), float> KerningTable = new();

        /// <summary>图集位图数据（RGBA，可选，用于 CPU 渲染）。</summary>
        public byte[]? AtlasBitmap;

        /// <summary>Skyline 打包器行数据。</summary>
        private readonly struct SkylineNode
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Width;
            public SkylineNode(int x, int y, int w) { X = x; Y = y; Width = w; }
        }

        private List<SkylineNode> _skyline = new();

        /// <summary>构建 5x7 点阵字体的字形表（ASCII 32-126）。</summary>
        public void BuildBuiltinFont(float fontSize)
        {
            FontSize = fontSize;
            LineHeight = fontSize * 1.2f;
            Baseline = fontSize * 0.85f;
            Spacing = fontSize * 0.05f;

            // 5x7 点阵字体数据（ASCII 可打印字符 32-126）
            // 每个字符 5 列，每列用字节表示哪些像素点亮（低位在上）
            static ReadOnlySpan<byte> GetGlyphData(char c)
            {
                // 简化的 5x7 点阵（仅数字和基本字母）
                int idx = c - 32;
                if (idx < 0 || idx > 94) return ReadOnlySpan<byte>.Empty;

                // 预定义的 5x7 位图数据（每字符 5 字节，每位代表一列的一行）
                byte[,] font5x7 = {
                    // 空格 (32)
                    {0x00,0x00,0x00,0x00,0x00},
                    // ! (33)
                    {0x00,0x00,0x5F,0x00,0x00},
                    // " (34)
                    {0x00,0x07,0x00,0x07,0x00},
                    // # (35)
                    {0x14,0x7F,0x14,0x7F,0x14},
                    // $ (36)
                    {0x24,0x2A,0x7F,0x2A,0x12},
                    // % (37)
                    {0x23,0x13,0x08,0x64,0x62},
                    // & (38)
                    {0x36,0x49,0x55,0x22,0x50},
                    // ' (39)
                    {0x00,0x05,0x03,0x00,0x00},
                    // ( (40)
                    {0x00,0x1C,0x22,0x41,0x00},
                    // ) (41)
                    {0x00,0x41,0x22,0x1C,0x00},
                    // * (42)
                    {0x14,0x08,0x3E,0x08,0x14},
                    // + (43)
                    {0x08,0x08,0x3E,0x08,0x08},
                    // , (44)
                    {0x00,0x50,0x30,0x00,0x00},
                    // - (45)
                    {0x08,0x08,0x08,0x08,0x08},
                    // . (46)
                    {0x00,0x60,0x60,0x00,0x00},
                    // / (47)
                    {0x20,0x10,0x08,0x04,0x02},
                    // 0 (48)
                    {0x3E,0x51,0x49,0x45,0x3E},
                    // 1 (49)
                    {0x00,0x42,0x7F,0x40,0x00},
                    // 2 (50)
                    {0x42,0x61,0x51,0x49,0x46},
                    // 3 (51)
                    {0x21,0x41,0x45,0x4B,0x31},
                    // 4 (52)
                    {0x18,0x14,0x12,0x7F,0x10},
                    // 5 (53)
                    {0x27,0x45,0x45,0x45,0x39},
                    // 6 (54)
                    {0x3C,0x4A,0x49,0x49,0x30},
                    // 7 (55)
                    {0x01,0x71,0x09,0x05,0x03},
                    // 8 (56)
                    {0x36,0x49,0x49,0x49,0x36},
                    // 9 (57)
                    {0x06,0x49,0x49,0x29,0x1E},
                    // : (58)
                    {0x00,0x36,0x36,0x00,0x00},
                    // ; (59)
                    {0x00,0x56,0x36,0x00,0x00},
                    // < (60)
                    {0x08,0x14,0x22,0x41,0x00},
                    // = (61)
                    {0x14,0x14,0x14,0x14,0x14},
                    // > (62)
                    {0x00,0x41,0x22,0x14,0x08},
                    // ? (63)
                    {0x02,0x01,0x51,0x09,0x06},
                    // @ (64)
                    {0x32,0x49,0x79,0x41,0x3E},
                    // A (65)
                    {0x7E,0x11,0x11,0x11,0x7E},
                    // B (66)
                    {0x7F,0x49,0x49,0x49,0x36},
                    // C (67)
                    {0x3E,0x41,0x41,0x41,0x22},
                    // D (68)
                    {0x7F,0x41,0x41,0x22,0x1C},
                    // E (69)
                    {0x7F,0x49,0x49,0x49,0x41},
                    // F (70)
                    {0x7F,0x09,0x09,0x09,0x01},
                    // G (71)
                    {0x3E,0x41,0x49,0x49,0x7A},
                    // H (72)
                    {0x7F,0x08,0x08,0x08,0x7F},
                    // I (73)
                    {0x00,0x41,0x7F,0x41,0x00},
                    // J (74)
                    {0x20,0x40,0x41,0x3F,0x01},
                    // K (75)
                    {0x7F,0x08,0x14,0x22,0x41},
                    // L (76)
                    {0x7F,0x40,0x40,0x40,0x40},
                    // M (77)
                    {0x7F,0x02,0x0C,0x02,0x7F},
                    // N (78)
                    {0x7F,0x04,0x08,0x10,0x7F},
                    // O (79)
                    {0x3E,0x41,0x41,0x41,0x3E},
                    // P (80)
                    {0x7F,0x09,0x09,0x09,0x06},
                    // Q (81)
                    {0x3E,0x41,0x51,0x21,0x5E},
                    // R (82)
                    {0x7F,0x09,0x19,0x29,0x46},
                    // S (83)
                    {0x46,0x49,0x49,0x49,0x31},
                    // T (84)
                    {0x01,0x01,0x7F,0x01,0x01},
                    // U (85)
                    {0x3F,0x40,0x40,0x40,0x3F},
                    // V (86)
                    {0x1F,0x20,0x40,0x20,0x1F},
                    // W (87)
                    {0x3F,0x40,0x38,0x40,0x3F},
                    // X (88)
                    {0x63,0x14,0x08,0x14,0x63},
                    // Y (89)
                    {0x07,0x08,0x70,0x08,0x07},
                    // Z (90)
                    {0x61,0x51,0x49,0x45,0x43},
                    // [ (91)
                    {0x00,0x7F,0x41,0x41,0x00},
                    // \ (92)
                    {0x02,0x04,0x08,0x10,0x20},
                    // ] (93)
                    {0x00,0x41,0x41,0x7F,0x00},
                    // ^ (94)
                    {0x04,0x02,0x01,0x02,0x04},
                    // _ (95)
                    {0x40,0x40,0x40,0x40,0x40},
                    // ` (96)
                    {0x00,0x01,0x02,0x04,0x00},
                    // a (97)
                    {0x20,0x54,0x54,0x54,0x78},
                    // b (98)
                    {0x7F,0x48,0x44,0x44,0x38},
                    // c (99)
                    {0x38,0x44,0x44,0x44,0x20},
                    // d (100)
                    {0x38,0x44,0x44,0x48,0x7F},
                    // e (101)
                    {0x38,0x54,0x54,0x54,0x18},
                    // f (102)
                    {0x08,0x7E,0x09,0x01,0x02},
                    // g (103)
                    {0x0C,0x52,0x52,0x52,0x3E},
                    // h (104)
                    {0x7F,0x08,0x04,0x04,0x78},
                    // i (105)
                    {0x00,0x44,0x7D,0x40,0x00},
                    // j (106)
                    {0x20,0x40,0x44,0x3D,0x00},
                    // k (107)
                    {0x7F,0x10,0x28,0x44,0x00},
                    // l (108)
                    {0x00,0x41,0x7F,0x40,0x00},
                    // m (109)
                    {0x7C,0x04,0x18,0x04,0x78},
                    // n (110)
                    {0x7C,0x08,0x04,0x04,0x78},
                    // o (111)
                    {0x38,0x44,0x44,0x44,0x38},
                    // p (112)
                    {0x7C,0x14,0x14,0x14,0x08},
                    // q (113)
                    {0x08,0x14,0x14,0x18,0x7C},
                    // r (114)
                    {0x7C,0x08,0x04,0x04,0x08},
                    // s (115)
                    {0x48,0x54,0x54,0x54,0x20},
                    // t (116)
                    {0x04,0x3F,0x44,0x40,0x20},
                    // u (117)
                    {0x3C,0x40,0x40,0x20,0x7C},
                    // v (118)
                    {0x1C,0x20,0x40,0x20,0x1C},
                    // w (119)
                    {0x3C,0x40,0x30,0x40,0x3C},
                    // x (120)
                    {0x44,0x28,0x10,0x28,0x44},
                    // y (121)
                    {0x0C,0x50,0x50,0x50,0x3C},
                    // z (122)
                    {0x44,0x64,0x54,0x4C,0x44},
                    // { (123)
                    {0x00,0x08,0x36,0x41,0x00},
                    // | (124)
                    {0x00,0x00,0x7F,0x00,0x00},
                    // } (125)
                    {0x00,0x41,0x36,0x08,0x00},
                    // ~ (126)
                    {0x10,0x08,0x08,0x10,0x08}
                };

                byte[] data = new byte[5];
                for (int col = 0; col < 5; col++)
                    data[col] = font5x7[idx, col];
                return data;
            }

            // 初始化图集
            AtlasBitmap = new byte[AtlasWidth * AtlasHeight * 4]; // RGBA
            _skyline.Clear();
            _skyline.Add(new SkylineNode(0, 0, AtlasWidth));

            for (int c = 32; c <= 126; c++)
            {
                char ch = (char)c;
                ReadOnlySpan<byte> glyphData = GetGlyphData(ch);
                if (glyphData.IsEmpty) continue;

                // 位图尺寸（5x7 + 1px padding）
                int bw = 6;
                int bh = 8;

                // Skyline 算法找位置
                (int px, int py) = SkylinePack(bw, bh);
                if (px < 0) continue; // 图集满

                // 生成位图
                var bitmap = new byte[bw * bh];
                for (int col = 0; col < 5; col++)
                {
                    byte column = glyphData[col];
                    for (int row = 0; row < 7; row++)
                    {
                        if ((column & (1 << row)) != 0)
                        {
                            bitmap[col + row * bw] = 255;
                        }
                    }
                }

                // 写入图集位图
                for (int by = 0; by < bh; by++)
                {
                    for (int bx = 0; bx < bw; bx++)
                    {
                        int ax = px + bx;
                        int ay = py + by;
                        if (ax < AtlasWidth && ay < AtlasHeight)
                        {
                            int atlasIdx = (ay * AtlasWidth + ax) * 4;
                            byte val = bitmap[by * bw + bx];
                            AtlasBitmap[atlasIdx + 0] = val;     // R
                            AtlasBitmap[atlasIdx + 1] = val;     // G
                            AtlasBitmap[atlasIdx + 2] = val;     // B
                            AtlasBitmap[atlasIdx + 3] = val > 0 ? (byte)255 : (byte)0; // A
                        }
                    }
                }

                // 创建字形
                float glyphAdvance = (bw - 1) * fontSize / 7f;
                var glyph = new FontGlyph
                {
                    Character = ch,
                    U = px / (float)AtlasWidth,
                    V = py / (float)AtlasHeight,
                    UWidth = bw / (float)AtlasWidth,
                    VHeight = bh / (float)AtlasHeight,
                    Advance = glyphAdvance,
                    Bitmap = bitmap,
                    BitmapWidth = bw,
                    BitmapHeight = bh,
                    BearingX = 0,
                    BearingY = fontSize - bh * fontSize / 8f
                };
                Glyphs[ch] = glyph;
            }
        }

        /// <summary>
        /// Skyline 图集打包算法。
        /// 扫描 skyline 节点，找到第一个能容纳 (w, h) 的位置。
        /// </summary>
        private (int x, int y) SkylinePack(int w, int h)
        {
            if (_skyline.Count == 0) return (-1, -1);

            int bestX = -1, bestY = AtlasHeight + 1;
            int bestIdx = -1;

            for (int i = 0; i < _skyline.Count; i++)
            {
                var node = _skyline[i];
                if (node.Width < w) continue;

                // 计算该节点处的最大高度（合并后续节点）
                int y = node.Y;
                int remaining = w;
                int j = i;
                while (remaining > 0 && j < _skyline.Count)
                {
                    y = Math.Max(y, _skyline[j].Y);
                    remaining -= _skyline[j].Width;
                    j++;
                }

                if (remaining > 0) continue; // 不够宽

                if (y + h > AtlasHeight) continue; // 超出图集高度

                if (y < bestY)
                {
                    bestY = y;
                    bestX = node.X;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) return (-1, -1);

            // 更新 skyline
            var oldNode = _skyline[bestIdx];
            _skyline[bestIdx] = new SkylineNode(oldNode.X, oldNode.Y + h, oldNode.Width);

            // 合并相邻等高节点
            while (bestIdx + 1 < _skyline.Count &&
                   _skyline[bestIdx].Y == _skyline[bestIdx + 1].Y)
            {
                var merged = _skyline[bestIdx];
                var next = _skyline[bestIdx + 1];
                _skyline[bestIdx] = new SkylineNode(merged.X, merged.Y, merged.Width + next.Width);
                _skyline.RemoveAt(bestIdx + 1);
            }

            return (bestX, bestY);
        }

        /// <summary>获取字形（找不到返回默认方块字形）。</summary>
        public FontGlyph GetGlyph(char c)
        {
            if (Glyphs.TryGetValue(c, out var glyph)) return glyph;
            // 默认方块字形
            return Glyphs.TryGetValue('?', out var fallback) ? fallback :
                new FontGlyph { Character = c, Advance = FontSize * 0.5f, BitmapWidth = 1, BitmapHeight = 1, U = 0, V = 0, UWidth = 0.01f, VHeight = 0.01f };
        }

        /// <summary>获取字符对的 Kerning 调整量。</summary>
        public float GetKerning(char left, char right)
        {
            return KerningTable.TryGetValue((left, right), out float k) ? k : 0f;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分：字形渲染器（文本渲染核心）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 字形渲染器。将文本字符串渲染为 UI 顶点和索引。
    /// 支持逐字符布局、换行、对齐、Kerning。
    /// </summary>
    public sealed class GlyphRenderer
    {
        private readonly List<UIVertex> _vertices = new();
        private readonly List<ushort> _indices = new();

        /// <summary>当前生成的顶点列表（引用）。</summary>
        public List<UIVertex> Vertices => _vertices;
        /// <summary>当前生成的索引列表（引用）。</summary>
        public List<ushort> Indices => _indices;

        /// <summary>
        /// 渲染单个字形。
        /// 算法：查字形数据→在 (pos.x + bearingX, pos.y - bearingY) 处生成一个 Quad，
        /// Quad 的 UV 取自字形在图集中的位置。
        /// </summary>
        public void RenderGlyph(char ch, FontAtlas font, Vector2 pos, float size, Vector4 color)
        {
            FontGlyph glyph = font.GetGlyph(ch);
            if (!glyph.IsValid) return;

            // 缩放因子
            float scale = size / font.FontSize;

            float x = pos.X + glyph.BearingX * scale;
            float y = pos.Y - glyph.BearingY * scale;
            float w = glyph.BitmapWidth * scale;
            float h = glyph.BitmapHeight * scale;

            // UV 从图集取
            Vector2 uvMin = new Vector2(glyph.U, glyph.V);
            Vector2 uvMax = new Vector2(glyph.U + glyph.UWidth, glyph.V + glyph.VHeight);

            UIVertexGenerator.GenerateQuad(x, y, w, h, color, uvMin, uvMax, _vertices, _indices);
        }

        /// <summary>
        /// 渲染文本字符串。
        /// 算法：
        ///   1. 逐字符遍历
        ///   2. 对每个字符查字形、计算 advance
        ///   3. 如果 pos.x + advance > maxWidth 且允许换行，换行
        ///   4. 换行时按对齐方式调整 X 偏移
        ///   5. 生成字形 Quad
        ///   6. Kerning：查 kerning 表调整 advance
        /// </summary>
        public (float width, float height) RenderText(
            string text, FontAtlas font, Vector2 pos, float size, Vector4 color,
            UITextAlignment alignment = UITextAlignment.Left,
            UITextWrapMode wrapMode = UITextWrapMode.None,
            float maxWidth = float.MaxValue)
        {
            if (string.IsNullOrEmpty(text)) return (0f, 0f);

            _vertices.Clear();
            _indices.Clear();

            float scale = size / font.FontSize;
            float lineHeight = font.LineHeight * scale;
            float startX = pos.X;
            float currentX = pos.X;
            float currentY = pos.Y;

            // 先收集每一行的字符和宽度
            var lines = WordWrapText(text, font, scale, maxWidth, wrapMode);

            float totalHeight = lines.Count * lineHeight;
            float maxWidthUsed = 0f;

            for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
            {
                string line = lines[lineIdx];

                // 计算行宽
                float lineWidth = MeasureLineWidth(line, font, scale);

                // 对齐调整
                float alignOffsetX = 0f;
                switch (alignment)
                {
                    case UITextAlignment.Center:
                        alignOffsetX = (maxWidth - lineWidth) * 0.5f;
                        break;
                    case UITextAlignment.Right:
                        alignOffsetX = maxWidth - lineWidth;
                        break;
                }

                currentX = startX + alignOffsetX;
                maxWidthUsed = Math.Max(maxWidthUsed, lineWidth);

                // 逐字符渲染
                char prevChar = '\0';
                for (int i = 0; i < line.Length; i++)
                {
                    char ch = line[i];
                    if (ch == '\n') continue;

                    FontGlyph glyph = font.GetGlyph(ch);

                    // Kerning 调整
                    float kerning = 0f;
                    if (prevChar != '\0')
                        kerning = font.GetKerning(prevChar, ch);
                    currentX += kerning * scale;

                    // 生成字形
                    float gx = currentX + glyph.BearingX * scale;
                    float gy = currentY - glyph.BearingY * scale;
                    float gw = glyph.BitmapWidth * scale;
                    float gh = glyph.BitmapHeight * scale;

                    if (gw > 0.001f && gh > 0.001f)
                    {
                        Vector2 uvMin = new Vector2(glyph.U, glyph.V);
                        Vector2 uvMax = new Vector2(glyph.U + glyph.UWidth, glyph.V + glyph.VHeight);
                        UIVertexGenerator.GenerateQuad(gx, gy, gw, gh, color, uvMin, uvMax, _vertices, _indices);
                    }

                    currentX += glyph.Advance * scale + font.Spacing * scale;
                    prevChar = ch;
                }

                currentY -= lineHeight;
            }

            return (maxWidthUsed, totalHeight);
        }

        /// <summary>
        /// 自动换行处理。
        /// 算法：
        ///   - None: 不换行，保留原始换行符
        ///   - WordWrap: 按单词换行（遇到空格检测下一个单词是否超宽）
        ///   - CharWrap: 按字符换行（每个字符检测是否超宽）
        /// </summary>
        private List<string> WordWrapText(string text, FontAtlas font, float scale, float maxWidth, UITextWrapMode wrapMode)
        {
            var result = new List<string>();
            if (wrapMode == UITextWrapMode.None)
            {
                // 按换行符分割
                int start = 0;
                for (int i = 0; i <= text.Length; i++)
                {
                    if (i == text.Length || text[i] == '\n')
                    {
                        result.Add(text.Substring(start, i - start));
                        start = i + 1;
                    }
                }
                return result;
            }

            // 按换行符分段，然后按模式换行
            var paragraphs = new List<string>();
            int s = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '\n')
                {
                    paragraphs.Add(text.Substring(s, i - s));
                    s = i + 1;
                }
            }

            foreach (string para in paragraphs)
            {
                if (para.Length == 0) { result.Add(""); continue; }

                if (wrapMode == UITextWrapMode.CharWrap)
                {
                    // 按字符换行
                    int lineStart = 0;
                    float lineW = 0f;
                    for (int i = 0; i < para.Length; i++)
                    {
                        float charW = font.GetGlyph(para[i]).Advance * scale + font.Spacing * scale;
                        if (lineW + charW > maxWidth && i > lineStart)
                        {
                            result.Add(para.Substring(lineStart, i - lineStart));
                            lineStart = i;
                            lineW = charW;
                        }
                        else
                        {
                            lineW += charW;
                        }
                    }
                    if (lineStart < para.Length)
                        result.Add(para.Substring(lineStart));
                }
                else // WordWrap
                {
                    // 按单词换行
                    int lineStart = 0;
                    int lastSpace = -1;
                    float lineW = 0f;

                    for (int i = 0; i < para.Length; i++)
                    {
                        float charW = font.GetGlyph(para[i]).Advance * scale + font.Spacing * scale;

                        if (para[i] == ' ')
                            lastSpace = i;

                        if (lineW + charW > maxWidth && lastSpace > lineStart)
                        {
                            result.Add(para.Substring(lineStart, lastSpace - lineStart));
                            lineStart = lastSpace + 1;
                            // 重新计算行宽（跳过前导空格）
                            lineW = 0f;
                            for (int j = lineStart; j <= i; j++)
                                lineW += font.GetGlyph(para[j]).Advance * scale + font.Spacing * scale;
                        }
                        else
                        {
                            lineW += charW;
                        }
                    }
                    if (lineStart < para.Length)
                        result.Add(para.Substring(lineStart));
                }
            }

            return result;
        }

        /// <summary>测量一行文本宽度。</summary>
        private float MeasureLineWidth(string line, FontAtlas font, float scale)
        {
            float w = 0f;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\n') continue;
                float advance = font.GetGlyph(line[i]).Advance * scale + font.Spacing * scale;
                if (i > 0)
                    advance += font.GetKerning(line[i - 1], line[i]) * scale;
                w += advance;
            }
            return w;
        }

        /// <summary>清除生成的顶点和索引。</summary>
        public void Clear()
        {
            _vertices.Clear();
            _indices.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分：UI 批处理
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UI 渲染批处理。将使用相同材质和纹理的 UI 元素合并为一次 DrawCall。
    /// </summary>
    public sealed class UIBatch
    {
        /// <summary>材质标识。</summary>
        public int MaterialId;

        /// <summary>纹理标识。</summary>
        public int TextureId;

        /// <summary>纹理名称（调试用）。</summary>
        public string TextureName = "";

        /// <summary>混合模式。</summary>
        public int BlendMode;

        /// <summary>顶点数据。</summary>
        public List<UIVertex> Vertices = new();

        /// <summary>索引数据。</summary>
        public List<ushort> Indices = new();

        /// <summary>顶点数量。</summary>
        public int VertexCount => Vertices.Count;

        /// <summary>索引数量。</summary>
        public int IndexCount => Indices.Count;

        /// <summary>排序键（用于渲染排序）。</summary>
        public long SortKey;

        /// <summary>是否透明。</summary>
        public bool IsTransparent;

        public UIBatch() { }

        public UIBatch(int materialId, int textureId, string texName, int blend, bool transparent)
        {
            MaterialId = materialId;
            TextureId = textureId;
            TextureName = texName;
            BlendMode = blend;
            IsTransparent = transparent;
            SortKey = CalculateSortKey(materialId, textureId, blend, transparent);
        }

        /// <summary>计算排序键（不透明优先，然后按材质/纹理分组）。</summary>
        private static long CalculateSortKey(int materialId, int textureId, int blend, bool transparent)
        {
            // 高位: 透明=1, 不透明=0 → 不透明先渲染
            // 中位: blendMode
            // 低位: materialId * 1000 + textureId
            long key = (transparent ? 1L : 0L) << 48;
            key |= ((long)blend & 0xFFFF) << 32;
            key |= ((long)materialId & 0xFFFF) << 16;
            key |= ((long)textureId & 0xFFFF);
            return key;
        }

        /// <summary>清空批次。</summary>
        public void Clear()
        {
            Vertices.Clear();
            Indices.Clear();
        }
    }

    /// <summary>
    /// UI 批处理管理器。收集 UI 绘制命令，按材质/纹理/透明度排序后分批输出。
    /// </summary>
    public sealed class UIBatchManager
    {
        private readonly List<UIBatch> _batches = new();
        private readonly Dictionary<long, UIBatch> _activeBatches = new();

        /// <summary>所有批次列表。</summary>
        public List<UIBatch> Batches => _batches;

        /// <summary>开始新的帧。</summary>
        public void BeginFrame()
        {
            _batches.Clear();
            _activeBatches.Clear();
        }

        /// <summary>
        /// 获取或创建一个批次（按 materialId + textureId + blend + transparent 组合键）。
        /// </summary>
        public UIBatch GetBatch(int materialId, int textureId, string texName, int blend, bool transparent)
        {
            var batch = new UIBatch(materialId, textureId, texName, blend, transparent);
            if (_activeBatches.TryGetValue(batch.SortKey, out var existing))
                return existing;

            _activeBatches[batch.SortKey] = batch;
            return batch;
        }

        /// <summary>结束帧：排序并输出批次列表。</summary>
        public void EndFrame()
        {
            _batches.Clear();
            _batches.AddRange(_activeBatches.Values);
            // 按排序键排序：不透明在前，透明在后
            _batches.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
        }

        /// <summary>获取总顶点数。</summary>
        public int TotalVertexCount()
        {
            int total = 0;
            foreach (var batch in _batches) total += batch.VertexCount;
            return total;
        }

        /// <summary>获取总索引数。</summary>
        public int TotalIndexCount()
        {
            int total = 0;
            foreach (var batch in _batches) total += batch.IndexCount;
            return total;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第七部分：裁剪系统
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>裁剪矩形（像素坐标）。</summary>
    public struct UIClipRect
    {
        public float X, Y, Width, Height;

        public UIClipRect(float x, float y, float w, float h)
        {
            X = x; Y = y; Width = w; Height = h;
        }

        /// <summary>裁剪矩形是否有效。</summary>
        public bool IsValid => Width > 0f && Height > 0f;

        /// <summary>与另一个裁剪矩形取交集。</summary>
        public static UIClipRect Intersect(UIClipRect a, UIClipRect b)
        {
            float x1 = Math.Max(a.X, b.X);
            float y1 = Math.Max(a.Y, b.Y);
            float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            float w = Math.Max(0f, x2 - x1);
            float h = Math.Max(0f, y2 - y1);
            return new UIClipRect(x1, y1, w, h);
        }

        /// <summary>点是否在裁剪矩形内。</summary>
        public bool Contains(float px, float py)
        {
            return px >= X && px <= X + Width && py >= Y && py <= Y + Height;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第八部分：UI 渲染器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真实 UI 渲染器。提供即时模式 UI 绘制接口。
    /// 支持：矩形/圆角矩形/圆形/椭圆/弧/线段/文本/图像绘制，
    /// DPI 缩放、裁剪栈、批处理排序。
    /// </summary>
    public sealed class UIRendererReal
    {
        // 顶点缓冲区
        private readonly List<UIVertex> _vertices = new();
        private readonly List<ushort> _indices = new();

        // 批处理管理器
        private readonly UIBatchManager _batchManager = new();

        // 字体图集和字形渲染器
        private readonly Dictionary<string, FontAtlas> _fonts = new();
        private readonly GlyphRenderer _glyphRenderer = new();

        // DPI 缩放
        public DPIScale DpiScale { get; } = new DPIScale(1f);

        // 裁剪栈
        private readonly Stack<UIClipRect> _clipStack = new();
        private UIClipRect _currentClip = new UIClipRect(0, 0, float.MaxValue, float.MaxValue);

        // 画布尺寸
        public float CanvasWidth = 1920f;
        public float CanvasHeight = 1080f;

        // 是否正在绘制
        private bool _isDrawing;

        // 默认字体名称
        public string DefaultFontName = "Default";

        /// <summary>顶点列表。</summary>
        public IReadOnlyList<UIVertex> Vertices => _vertices;

        /// <summary>索引列表。</summary>
        public IReadOnlyList<ushort> Indices => _indices;

        /// <summary>批处理列表。</summary>
        public IReadOnlyList<UIBatch> Batches => _batchManager.Batches;

        /// <summary>注册字体。</summary>
        public void RegisterFont(string name, FontAtlas font)
        {
            _fonts[name] = font;
        }

        /// <summary>获取字体。</summary>
        public FontAtlas GetFont(string name)
        {
            return _fonts.TryGetValue(name, out var font) ? font : null!;
        }

        /// <summary>构建并注册默认 5x7 点阵字体。</summary>
        public void BuildDefaultFont(float fontSize = 14f)
        {
            var font = new FontAtlas { FontSize = fontSize };
            font.BuildBuiltinFont(fontSize);
            _fonts[DefaultFontName] = font;
        }

        /// <summary>开始绘制帧。</summary>
        public void BeginDraw()
        {
            _isDrawing = true;
            _vertices.Clear();
            _indices.Clear();
            _batchManager.BeginFrame();
            _clipStack.Clear();
            _currentClip = new UIClipRect(0, 0, CanvasWidth * DpiScale.Scale, CanvasHeight * DpiScale.Scale);
        }

        /// <summary>结束绘制帧，排序并生成分组。</summary>
        public void EndDraw()
        {
            _batchManager.EndFrame();
            _isDrawing = false;
        }

        /// <summary>推入裁剪矩形。</summary>
        public void PushClipRect(float x, float y, float w, float h)
        {
            // 应用 DPI 缩放
            var scaled = new UIClipRect(
                x * DpiScale.Scale, y * DpiScale.Scale,
                w * DpiScale.Scale, h * DpiScale.Scale);
            _clipStack.Push(_currentClip);
            _currentClip = UIClipRect.Intersect(_currentClip, scaled);
        }

        /// <summary>弹出裁剪矩形。</summary>
        public void PopClipRect()
        {
            if (_clipStack.Count > 0)
                _currentClip = _clipStack.Pop();
        }

        /// <summary>获取当前裁剪矩形。</summary>
        public UIClipRect GetCurrentClip() => _currentClip;

        // ── 绘制方法 ──

        /// <summary>绘制矩形。</summary>
        public void DrawRect(float x, float y, float w, float h, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            UIVertexGenerator.GenerateQuad(x * s, y * s, w * s, h * s, color, _vertices, _indices);
        }

        /// <summary>绘制带纹理的矩形。</summary>
        public void DrawImage(float x, float y, float w, float h, int textureId, string texName)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            var batch = _batchManager.GetBatch(0, textureId, texName, 0, false);
            int baseIdx = batch.Vertices.Count;
            batch.Vertices.Add(new UIVertex(new Vector2(x * s, (y + h) * s), new Vector2(0, 0), Vector4.One));
            batch.Vertices.Add(new UIVertex(new Vector2((x + w) * s, (y + h) * s), new Vector2(1, 0), Vector4.One));
            batch.Vertices.Add(new UIVertex(new Vector2((x + w) * s, y * s), new Vector2(1, 1), Vector4.One));
            batch.Vertices.Add(new UIVertex(new Vector2(x * s, y * s), new Vector2(0, 1), Vector4.One));
            batch.Indices.Add((ushort)(baseIdx + 0));
            batch.Indices.Add((ushort)(baseIdx + 1));
            batch.Indices.Add((ushort)(baseIdx + 2));
            batch.Indices.Add((ushort)(baseIdx + 0));
            batch.Indices.Add((ushort)(baseIdx + 2));
            batch.Indices.Add((ushort)(baseIdx + 3));
        }

        /// <summary>绘制圆角矩形。</summary>
        public void DrawRoundedRect(float x, float y, float w, float h, float radius, int cornerSegments, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            UIVertexGenerator.GenerateRoundedRect(x * s, y * s, w * s, h * s, radius * s, cornerSegments, color, _vertices, _indices);
        }

        /// <summary>绘制圆形。</summary>
        public void DrawCircle(float cx, float cy, float radius, int segments, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            UIVertexGenerator.GenerateCircle(cx * s, cy * s, radius * s, segments, color, _vertices, _indices);
        }

        /// <summary>绘制椭圆。</summary>
        public void DrawEllipse(float cx, float cy, float rx, float ry, int segments, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            UIVertexGenerator.GenerateEllipse(cx * s, cy * s, rx * s, ry * s, segments, color, _vertices, _indices);
        }

        /// <summary>绘制弧。</summary>
        public void DrawArc(float cx, float cy, float radius, float startAngle, float sweepAngle, int segments, float thickness, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            UIVertexGenerator.GenerateArc(cx * s, cy * s, radius * s, startAngle, sweepAngle, segments, thickness * s, color, _vertices, _indices);
        }

        /// <summary>绘制线段。</summary>
        public void DrawLine(float x0, float y0, float x1, float y1, float thickness, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            UIVertexGenerator.GenerateLine(x0 * s, y0 * s, x1 * s, y1 * s, thickness * s, color, _vertices, _indices);
        }

        /// <summary>绘制文本（使用指定字体）。</summary>
        public void DrawText(string text, string fontName, float x, float y, float size, Vector4 color,
            UITextAlignment alignment = UITextAlignment.Left,
            UITextWrapMode wrapMode = UITextWrapMode.None,
            float maxWidth = float.MaxValue)
        {
            if (!_isDrawing) return;
            if (!_fonts.TryGetValue(fontName, out var font)) return;

            float s = DpiScale.Scale;
            var pos = new Vector2(x * s, y * s);
            float scaledMaxWidth = maxWidth * s;

            _glyphRenderer.Clear();
            var (w, h) = _glyphRenderer.RenderText(text, font, pos, size * s, color, alignment, wrapMode, scaledMaxWidth);

            // 将字形渲染器的输出合并到主顶点/索引列表
            int vertBase = _vertices.Count;
            for (int i = 0; i < _glyphRenderer.Vertices.Count; i++)
                _vertices.Add(_glyphRenderer.Vertices[i]);
            for (int i = 0; i < _glyphRenderer.Indices.Count; i++)
                _indices.Add((ushort)(_glyphRenderer.Indices[i] + vertBase));
        }

        /// <summary>绘制文本（使用默认字体）。</summary>
        public void DrawText(string text, float x, float y, float size, Vector4 color,
            UITextAlignment alignment = UITextAlignment.Left,
            UITextWrapMode wrapMode = UITextWrapMode.None,
            float maxWidth = float.MaxValue)
        {
            DrawText(text, DefaultFontName, x, y, size, color, alignment, wrapMode, maxWidth);
        }

        /// <summary>绘制路径。</summary>
        public void DrawPath(List<Vector2> points, float width, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            var scaledPoints = new List<Vector2>(points.Count);
            for (int i = 0; i < points.Count; i++)
                scaledPoints.Add(points[i] * s);
            UIVertexGenerator.GeneratePath(scaledPoints, width * s, color, _vertices, _indices);
        }

        /// <summary>绘制填充多边形。</summary>
        public void DrawPolygon(List<Vector2> points, Vector4 color)
        {
            if (!_isDrawing) return;
            float s = DpiScale.Scale;
            var scaledPoints = new List<Vector2>(points.Count);
            for (int i = 0; i < points.Count; i++)
                scaledPoints.Add(points[i] * s);
            UIVertexGenerator.GeneratePolygon(scaledPoints, color, _vertices, _indices);
        }

        /// <summary>获取当前帧统计信息。</summary>
        public string GetStats()
        {
            return $"Vertices={_vertices.Count} Indices={_indices.Count} Batches={_batchManager.Batches.Count}";
        }
    }
}
