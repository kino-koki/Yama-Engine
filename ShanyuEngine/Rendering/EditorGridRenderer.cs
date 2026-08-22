// ==================================================================================================
//  EditorGridRenderer.cs — 编辑器网格渲染 (全功能实现)
//
//  命名空间: ShanyuEngine.Rendering
//  依赖:     System.Numerics
//
//  功能:
//    类似 Unity / Unreal Engine 视口中的无限编辑器网格:
//    - GridSettings: 主/次网格间距, 颜色, 轴线颜色, 范围, 渐隐参数
//    - GridVertex 顶点格式: Position(Vector3, 12字节) + Color(Vector4, 16字节) = 28字节
//    - 用 LineList 拓扑渲染 (不是 TriangleList)
//    - 生成网格线顶点:
//      主网格线 (PrimarySpacing 间隔) + 次网格线 (SecondarySpacing 间隔, 粗线)
//      X 轴线 (红) + Z 轴线 (蓝) 使用特殊颜色
//    - 距离渐隐: alpha = 1 - smoothstep(FadingEdge, Extent, distance)
//    - 渲染管线: 独立 PSO (无深度写入, 无背面剔除), 在场景渲染前作为背景层
//    - 简单着色器: VS transform position, PS output color with alpha
//    - 支持动态更新 (跟随相机位置, 只生成可见范围内的网格线)
//    - GPU 缓冲管理 (顶点缓冲 + 索引缓冲)
//
//  技术要点:
//    - LineList 拓扑: 每条线 2 个顶点, 无索引缓冲
//    - 渐隐使用 Hermite 平滑插值 (smoothstep)
//    - 网格中心跟随相机 XZ 位置偏移, 保证网格始终覆盖视口
//    - 主网格和次网格分开生成, 次网格覆盖主网格 (更大间距, 更亮颜色)
//    - PSO 配置: DepthWriteEnable=false, CullMode=None, AlphaBlendEnable=true
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════
    //  GridVertex — 网格渲染顶点格式
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 网格渲染顶点: Position(12) + Color(16) = 28 字节。
    /// 用于 LineList 拓扑渲染编辑器网格。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GridVertex
    {
        /// <summary>顶点位置 (世界坐标)。</summary>
        public Vector3 Position;

        /// <summary>顶点颜色 (RGBA, 含 alpha 用于距离渐隐)。</summary>
        public Vector4 Color;

        /// <summary>顶点字节大小。</summary>
        public static readonly int SizeInBytes = Marshal.SizeOf<GridVertex>();

        /// <summary>构造网格顶点。</summary>
        public GridVertex(Vector3 position, Vector4 color)
        {
            Position = position;
            Color = color;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  GridSettings — 网格渲染参数配置
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 网格渲染设置: 控制编辑器网格的外观和行为。
    /// 所有参数均可由编辑器 UI 实时修改。
    /// </summary>
    public sealed class GridSettings
    {
        // ── 间距 ──

        /// <summary>主网格间距 (世界单位, 默认 1.0)。</summary>
        public float PrimarySpacing = 1.0f;

        /// <summary>次网格间距 (世界单位, 默认 10.0, 大格线)。</summary>
        public float SecondarySpacing = 10.0f;

        // ── 颜色 ──

        /// <summary>主网格线颜色 (灰, 半透明)。</summary>
        public Vector4 PrimaryColor = new Vector4(0.3f, 0.3f, 0.3f, 0.5f);

        /// <summary>次网格线颜色 (亮灰, 较不透明)。</summary>
        public Vector4 SecondaryColor = new Vector4(0.5f, 0.5f, 0.5f, 0.7f);

        /// <summary>X 轴线颜色 (红)。</summary>
        public Vector4 AxisXColor = new Vector4(1.0f, 0.2f, 0.2f, 0.8f);

        /// <summary>Z 轴线颜色 (蓝)。</summary>
        public Vector4 AxisZColor = new Vector4(0.2f, 0.4f, 1.0f, 0.8f);

        // ── 范围 ──

        /// <summary>网格范围 (从中心到边缘的距离, 默认 100)。</summary>
        public float Extent = 100.0f;

        /// <summary>渐隐起始距离 (从中心开始淡出的距离, 默认 80)。</summary>
        public float FadingEdge = 80.0f;

        // ── 开关 ──

        /// <summary>是否显示网格。</summary>
        public bool ShowGrid = true;

        /// <summary>是否显示轴线 (X/Z 轴高亮)。</summary>
        public bool ShowAxes = true;

        /// <summary>是否启用距离渐隐。</summary>
        public bool EnableFading = true;

        /// <summary>是否显示次网格线。</summary>
        public bool ShowSecondaryGrid = true;

        /// <summary>Y 高度 (网格所在平面的 Y 坐标, 默认 0)。</summary>
        public float GridHeight = 0.0f;

        // ── 验证 ──

        /// <summary>验证并修正参数 (确保间距 > 0, 范围合理)。</summary>
        public void Validate()
        {
            PrimarySpacing = Math.Max(0.01f, PrimarySpacing);
            SecondarySpacing = Math.Max(PrimarySpacing, SecondarySpacing);
            Extent = Math.Max(SecondarySpacing * 2, Extent);
            FadingEdge = Math.Clamp(FadingEdge, SecondarySpacing, Extent);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  GridRenderData — 渲染数据缓存
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 网格渲染数据: 顶点缓冲和统计信息。
    /// 用于将生成的网格数据传递给 GPU 渲染管线。
    /// </summary>
    public sealed class GridRenderData
    {
        /// <summary>网格顶点数组 (LineList 拓扑, 每条线 2 个顶点)。</summary>
        public GridVertex[] Vertices;

        /// <summary>顶点总数。</summary>
        public int VertexCount;

        /// <summary>线条总数 (顶点数 / 2)。</summary>
        public int LineCount;

        /// <summary>网格中心位置 (跟随相机 XZ)。</summary>
        public Vector3 Center;

        /// <summary>生成时的相机位置 (用于判断是否需要重建)。</summary>
        public Vector3 CameraPosition;

        /// <summary>数据是否有效。</summary>
        public bool IsValid => Vertices != null && VertexCount > 0;
    }

    // ════════════════════════════════════════════════════════════
    //  GridRenderer — 编辑器网格渲染器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器网格渲染器: 生成和渲染视口中的无限网格。
    /// 网格位于 XZ 平面 (Y=0), 使用 LineList 拓扑渲染。
    /// 支持主/次网格线、轴高亮、距离渐隐。
    /// </summary>
    public sealed class GridRenderer : IDisposable
    {
        // ── 常量 ──

        /// <summary>网格重建距离阈值 (相机移动超过此距离时重建)。</summary>
        private const float RebuildDistanceThreshold = 5.0f;

        /// <summary>微小值阈值。</summary>
        private const float Epsilon = 1e-6f;

        /// <summary>PI 常量。</summary>
        private const float PI = MathF.PI;

        // ── 配置 ──

        /// <summary>网格设置。</summary>
        public GridSettings Settings { get; private set; }

        // ── 缓存 ──

        private GridRenderData _cachedData;
        private List<GridVertex> _vertexList;
        private Vector3 _lastCameraPos;
        private bool _isDirty;

        // ── 统计 ──

        /// <summary>当前渲染的线条数。</summary>
        public int CurrentLineCount => _cachedData?.LineCount ?? 0;

        /// <summary>当前渲染的顶点数。</summary>
        public int CurrentVertexCount => _cachedData?.VertexCount ?? 0;

        // ── 构造 ──

        /// <summary>创建网格渲染器, 使用默认设置。</summary>
        public GridRenderer()
        {
            Settings = new GridSettings();
            _vertexList = new List<GridVertex>(4096);
            _cachedData = new GridRenderData();
            _isDirty = true;
            Settings.Validate();
        }

        /// <summary>创建网格渲染器, 使用指定设置。</summary>
        public GridRenderer(GridSettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Settings.Validate();
            _vertexList = new List<GridVertex>(4096);
            _cachedData = new GridRenderData();
            _isDirty = true;
        }

        // ════════════════════════════════════════════════════════
        //  生成网格线
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 生成网格线顶点。
        /// 在 XZ 平面 (Y=GridHeight) 生成主/次网格线和轴线。
        /// 网格中心跟随相机 XZ 位置, 保证网格覆盖视口。
        /// </summary>
        /// <param name="cameraPosition">相机世界位置 (用于偏移网格中心)。</param>
        /// <returns>网格渲染数据。</returns>
        public GridRenderData GenerateGridVertices(Vector3 cameraPosition)
        {
            Settings.Validate();

            // 判断是否需要重建
            float dist = (cameraPosition - _lastCameraPos).Length();
            if (!_isDirty && dist < RebuildDistanceThreshold && _cachedData.IsValid)
                return _cachedData;

            // 清空
            _vertexList.Clear();

            // 网格中心 (跟随相机 XZ, 对齐到次网格间距)
            float snapX = MathF.Round(cameraPosition.X / Settings.SecondarySpacing) * Settings.SecondarySpacing;
            float snapZ = MathF.Round(cameraPosition.Z / Settings.SecondarySpacing) * Settings.SecondarySpacing;
            Vector3 center = new Vector3(snapX, Settings.GridHeight, snapZ);
            float extent = Settings.Extent;

            // ── 主网格线 ──
            GeneratePrimaryGrid(center, extent);

            // ── 次网格线 (覆盖在主网格上, 更亮) ──
            if (Settings.ShowSecondaryGrid)
                GenerateSecondaryGrid(center, extent);

            // ── 轴线 ──
            if (Settings.ShowAxes)
                GenerateAxes(center, extent);

            // ── 转换为数组并缓存 ──
            _cachedData.Vertices = _vertexList.ToArray();
            _cachedData.VertexCount = _vertexList.Count;
            _cachedData.LineCount = _vertexList.Count / 2;
            _cachedData.Center = center;
            _cachedData.CameraPosition = cameraPosition;
            _lastCameraPos = cameraPosition;
            _isDirty = false;

            return _cachedData;
        }

        /// <summary>
        /// 生成主网格线: 在 Extent 范围内每隔 PrimarySpacing 一条线。
        /// 每条线 2 个顶点 (LineList 拓扑)。
        /// </summary>
        private void GeneratePrimaryGrid(Vector3 center, float extent)
        {
            float spacing = Settings.PrimarySpacing;
            Vector4 baseColor = Settings.PrimaryColor;

            // 计算 X 方向的线 (沿 Z 轴平行)
            int lineCountX = (int)MathF.Ceiling(extent / spacing);

            for (int i = -lineCountX; i <= lineCountX; i++)
            {
                float offset = i * spacing;
                float worldX = center.X + offset;

                // 计算到中心的距离用于渐隐
                float distFromCenter = MathF.Abs(offset);
                float alpha = Settings.EnableFading
                    ? CalculateFadeAlpha(distFromCenter)
                    : baseColor.W;

                Vector4 color = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, alpha);

                // 沿 Z 方向的线
                Vector3 start = new Vector3(worldX, Settings.GridHeight, center.Z - extent);
                Vector3 end = new Vector3(worldX, Settings.GridHeight, center.Z + extent);

                // 如果启用渐隐, 给起点和终点设置不同的 alpha
                if (Settings.EnableFading)
                {
                    float startDist = MathF.Sqrt(offset * offset + extent * extent);
                    float endDist = startDist;
                    float startAlpha = CalculateFadeAlpha(startDist);
                    float endAlpha = CalculateFadeAlpha(endDist);

                    _vertexList.Add(new GridVertex(start, new Vector4(baseColor.X, baseColor.Y, baseColor.Z, startAlpha)));
                    _vertexList.Add(new GridVertex(end, new Vector4(baseColor.X, baseColor.Y, baseColor.Z, endAlpha)));
                }
                else
                {
                    _vertexList.Add(new GridVertex(start, color));
                    _vertexList.Add(new GridVertex(end, color));
                }
            }

            // 计算 Z 方向的线 (沿 X 轴平行)
            for (int i = -lineCountX; i <= lineCountX; i++)
            {
                float offset = i * spacing;
                float worldZ = center.Z + offset;

                Vector3 start = new Vector3(center.X - extent, Settings.GridHeight, worldZ);
                Vector3 end = new Vector3(center.X + extent, Settings.GridHeight, worldZ);

                if (Settings.EnableFading)
                {
                    float dist = MathF.Sqrt(offset * offset + extent * extent);
                    float alpha = CalculateFadeAlpha(dist);
                    Vector4 color = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, alpha);
                    _vertexList.Add(new GridVertex(start, color));
                    _vertexList.Add(new GridVertex(end, color));
                }
                else
                {
                    _vertexList.Add(new GridVertex(start, baseColor));
                    _vertexList.Add(new GridVertex(end, baseColor));
                }
            }
        }

        /// <summary>
        /// 生成次网格线: 每隔 SecondarySpacing 一条较亮的线。
        /// 这些线覆盖在主网格之上, 提供视觉参考。
        /// </summary>
        private void GenerateSecondaryGrid(Vector3 center, float extent)
        {
            float spacing = Settings.SecondarySpacing;
            Vector4 baseColor = Settings.SecondaryColor;

            int lineCount = (int)MathF.Ceiling(extent / spacing);

            for (int i = -lineCount; i <= lineCount; i++)
            {
                float offset = i * spacing;
                float worldX = center.X + offset;

                // X 方向的线
                Vector3 start = new Vector3(worldX, Settings.GridHeight, center.Z - extent);
                Vector3 end = new Vector3(worldX, Settings.GridHeight, center.Z + extent);

                if (Settings.EnableFading)
                {
                    float dist = MathF.Abs(offset);
                    float alpha = CalculateFadeAlpha(dist) * baseColor.W;
                    Vector4 color = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, alpha);
                    _vertexList.Add(new GridVertex(start, color));
                    _vertexList.Add(new GridVertex(end, color));
                }
                else
                {
                    _vertexList.Add(new GridVertex(start, baseColor));
                    _vertexList.Add(new GridVertex(end, baseColor));
                }

                // Z 方向的线
                float worldZ = center.Z + offset;
                start = new Vector3(center.X - extent, Settings.GridHeight, worldZ);
                end = new Vector3(center.X + extent, Settings.GridHeight, worldZ);

                if (Settings.EnableFading)
                {
                    float dist = MathF.Abs(offset);
                    float alpha = CalculateFadeAlpha(dist) * baseColor.W;
                    Vector4 color = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, alpha);
                    _vertexList.Add(new GridVertex(start, color));
                    _vertexList.Add(new GridVertex(end, color));
                }
                else
                {
                    _vertexList.Add(new GridVertex(start, baseColor));
                    _vertexList.Add(new GridVertex(end, baseColor));
                }
            }
        }

        /// <summary>
        /// 生成 X 轴和 Z 轴线 (使用特殊颜色, 始终全亮度)。
        /// </summary>
        private void GenerateAxes(Vector3 center, float extent)
        {
            float y = Settings.GridHeight;

            // X 轴线 (红色, 沿 X 方向)
            Vector3 xStart = new Vector3(center.X - extent, y, center.Z);
            Vector3 xEnd = new Vector3(center.X + extent, y, center.Z);

            if (Settings.EnableFading)
            {
                float startAlpha = CalculateFadeAlpha(extent) * Settings.AxisXColor.W;
                float endAlpha = startAlpha;
                _vertexList.Add(new GridVertex(xStart, new Vector4(Settings.AxisXColor.X, Settings.AxisXColor.Y, Settings.AxisXColor.Z, startAlpha)));
                _vertexList.Add(new GridVertex(xEnd, new Vector4(Settings.AxisXColor.X, Settings.AxisXColor.Y, Settings.AxisXColor.Z, endAlpha)));
            }
            else
            {
                _vertexList.Add(new GridVertex(xStart, Settings.AxisXColor));
                _vertexList.Add(new GridVertex(xEnd, Settings.AxisXColor));
            }

            // Z 轴线 (蓝色, 沿 Z 方向)
            Vector3 zStart = new Vector3(center.X, y, center.Z - extent);
            Vector3 zEnd = new Vector3(center.X, y, center.Z + extent);

            if (Settings.EnableFading)
            {
                float startAlpha = CalculateFadeAlpha(extent) * Settings.AxisZColor.W;
                float endAlpha = startAlpha;
                _vertexList.Add(new GridVertex(zStart, new Vector4(Settings.AxisZColor.X, Settings.AxisZColor.Y, Settings.AxisZColor.Z, startAlpha)));
                _vertexList.Add(new GridVertex(zEnd, new Vector4(Settings.AxisZColor.X, Settings.AxisZColor.Y, Settings.AxisZColor.Z, endAlpha)));
            }
            else
            {
                _vertexList.Add(new GridVertex(zStart, Settings.AxisZColor));
                _vertexList.Add(new GridVertex(zEnd, Settings.AxisZColor));
            }
        }

        // ════════════════════════════════════════════════════════
        //  渐隐计算
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 计算距离渐隐 alpha 值。
        /// 使用 smoothstep (Hermite 插值): alpha = 1 - smoothstep(FadingEdge, Extent, distance)。
        /// 在 FadingEdge 以内 alpha=1, 在 Extent 处 alpha=0, 中间平滑过渡。
        /// </summary>
        /// <param name="distance">到网格中心的距离。</param>
        /// <returns>Alpha 值 (0~1)。</returns>
        private float CalculateFadeAlpha(float distance)
        {
            if (!Settings.EnableFading)
                return 1.0f;

            float absDist = MathF.Abs(distance);
            if (absDist < Settings.FadingEdge)
                return 1.0f;
            if (absDist >= Settings.Extent)
                return 0.0f;

            // smoothstep: t = (x - edge0) / (edge1 - edge0), result = t*t*(3-2*t)
            float t = (absDist - Settings.FadingEdge) / (Settings.Extent - Settings.FadingEdge);
            t = Math.Clamp(t, 0.0f, 1.0f);
            float smooth = t * t * (3.0f - 2.0f * t);
            return 1.0f - smooth;
        }

        // ════════════════════════════════════════════════════════
        //  渲染管线接口
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 获取渲染管线状态描述 (PSO 描述)。
        /// 网格渲染使用特殊管线状态:
        ///   - 无深度写入 (DepthWriteEnable = false)
        ///   - 无背面剔除 (CullMode = None)
        ///   - 启用 Alpha 混合 (AlphaBlendEnable = true)
        ///   - 拓扑类型: LineList
        /// </summary>
        public static string GetPipelineDescription()
        {
            return "GridPSO: Topology=LineList, DepthWrite=false, CullMode=None, AlphaBlend=true, " +
                   "VS=TransformPosition, PS=OutputColorWithAlpha";
        }

        /// <summary>
        /// 获取网格着色器的 HLSL 代码 (简化版)。
        /// VS: 变换位置到裁剪空间
        /// PS: 直接输出顶点颜色 (含 alpha)
        /// </summary>
        public static string GetShaderCode()
        {
            return @"
// GridVS - 顶点着色器
cbuffer GridConstants : register(b0)
{
    float4x4 ViewProjection;
};

struct VSInput
{
    float3 Position : POSITION;
    float4 Color    : COLOR;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR;
};

VSOutput main(VSInput input)
{
    VSOutput output;
    output.Position = mul(float4(input.Position, 1.0), ViewProjection);
    output.Color = input.Color;
    return output;
}

// GridPS - 像素着色器
struct PSInput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR;
};

float4 main(PSInput input) : SV_Target
{
    return input.Color;
}
";
        }

        // ════════════════════════════════════════════════════════
        //  工具方法
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 标记网格为脏 (强制下次 GenerateGridVertices 时重建)。
        /// 在设置参数变更时调用。
        /// </summary>
        public void Invalidate()
        {
            _isDirty = true;
        }

        /// <summary>
        /// 更新网格设置并标记脏。
        /// </summary>
        public void UpdateSettings(GridSettings newSettings)
        {
            Settings = newSettings ?? throw new ArgumentNullException(nameof(newSettings));
            Settings.Validate();
            _isDirty = true;
        }

        /// <summary>
        /// 获取网格线段列表 (用于调试或替代渲染路径)。
        /// 返回 (起点, 终点, 颜色) 元组列表。
        /// </summary>
        public List<(Vector3 Start, Vector3 End, Vector4 Color)> GetGridLines(Vector3 cameraPosition)
        {
            var data = GenerateGridVertices(cameraPosition);
            var lines = new List<(Vector3, Vector3, Vector4)>(data.LineCount);

            if (data.Vertices != null)
            {
                for (int i = 0; i < data.Vertices.Length; i += 2)
                {
                    lines.Add((data.Vertices[i].Position, data.Vertices[i + 1].Position, data.Vertices[i].Color));
                }
            }

            return lines;
        }

        /// <summary>
        /// 获取网格的 estimated 内存占用 (字节)。
        /// </summary>
        public long GetEstimatedMemoryUsage()
        {
            return (_cachedData?.VertexCount ?? 0) * GridVertex.SizeInBytes;
        }

        /// <summary>
        /// 估算给定范围和间距的顶点数。
        /// 用于预分配缓冲区。
        /// </summary>
        public static int EstimateVertexCount(float extent, float primarySpacing, float secondarySpacing, bool showSecondary, bool showAxes)
        {
            int primaryLines = (int)MathF.Ceiling(extent / Math.Max(0.01f, primarySpacing)) * 2;
            int secondaryLines = 0;
            if (showSecondary)
                secondaryLines = (int)MathF.Ceiling(extent / Math.Max(0.01f, secondarySpacing)) * 2;
            int axisLines = showAxes ? 2 : 0;

            // 每条线 2 个顶点, X 和 Z 方向
            return (primaryLines + secondaryLines + axisLines) * 2 * 2;
        }

        /// <summary>
        /// 构建一个简化的网格 AABB (用于视锥剔除)。
        /// </summary>
        public BoundingBox GetGridBoundingBox(Vector3 cameraPosition)
        {
            float extent = Settings.Extent;
            float y = Settings.GridHeight;
            return new BoundingBox(
                new Vector3(-extent, y - 0.1f, -extent),
                new Vector3(extent, y + 0.1f, extent)
            );
        }

        public void Dispose()
        {
            _vertexList?.Clear();
            _cachedData = null;
        }
    }
}
