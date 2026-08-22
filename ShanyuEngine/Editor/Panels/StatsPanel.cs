using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.Assets;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 统计面板 (StatsPanel)
    //
    // 提供实时性能分析:
    //   - FPS 折线图 (120 帧滚动历史)
    //   - 帧时间分解 (Update / Render / Physics / Audio / GC)
    //   - 内存使用 (Managed / GPU / Texture / Mesh)
    //   - DrawCall 统计 (总数 / Shadow / Transparent / Opaque)
    //   - 实体 / 组件统计
    //
    // 数据由 MainEditorWindow 每帧通过 UpdateStats 推送。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>帧时间分解 (毫秒)。</summary>
    public struct FrameTimeBreakdown
    {
        public float UpdateMs;
        public float RenderMs;
        public float PhysicsMs;
        public float AudioMs;
        public float GcMs;

        public float TotalMs => UpdateMs + RenderMs + PhysicsMs + AudioMs + GcMs;
    }

    /// <summary>内存使用统计 (字节)。</summary>
    public struct MemoryStats
    {
        public long ManagedBytes;
        public long GpuBytes;
        public long TextureBytes;
        public long MeshBytes;

        public long TotalBytes => ManagedBytes + GpuBytes + TextureBytes + MeshBytes;
    }

    /// <summary>DrawCall 统计。</summary>
    public struct DrawCallStats
    {
        public int Total;
        public int Shadow;
        public int Transparent;
        public int Opaque;
        public int Triangles;
    }

    /// <summary>统计面板: 实时性能分析仪表盘。</summary>
    public sealed class StatsPanel : EditorPanel
    {
        // ── 历史缓冲 ──
        private const int HistorySize = 120;
        private readonly float[] _fpsHistory = new float[HistorySize];
        private readonly float[] _frameTimeHistory = new float[HistorySize];
        private int _historyOffset;
        private int _historyCount;

        // ── 当前统计 ──
        public float CurrentFps;
        public float CurrentFrameTimeMs;
        public FrameTimeBreakdown FrameTime;
        public MemoryStats Memory;
        public DrawCallStats DrawCalls;

        // ── 峰值/谷值 ──
        private float _minFps = float.MaxValue;
        private float _maxFps;
        private float _maxFrameTimeMs;

        // ── 显示开关 ──
        private bool _showFpsChart = true;
        private bool _showFrameTime = true;
        private bool _showMemory = true;
        private bool _showDrawCalls = true;
        private bool _showEntities = true;

        // ── ImGui 颜色 ──
        private static readonly Vector4 ColUpdate = new(0.30f, 0.60f, 0.95f, 1.0f);
        private static readonly Vector4 ColRender = new(0.95f, 0.55f, 0.30f, 1.0f);
        private static readonly Vector4 ColPhysics = new(0.30f, 0.85f, 0.45f, 1.0f);
        private static readonly Vector4 ColAudio = new(0.80f, 0.45f, 0.90f, 1.0f);
        private static readonly Vector4 ColGc = new(0.95f, 0.75f, 0.20f, 1.0f);

        private static readonly Vector4 ColManaged = new(0.30f, 0.60f, 0.95f, 1.0f);
        private static readonly Vector4 ColGpu = new(0.95f, 0.55f, 0.30f, 1.0f);
        private static readonly Vector4 ColTexture = new(0.30f, 0.85f, 0.45f, 1.0f);
        private static readonly Vector4 ColMesh = new(0.80f, 0.45f, 0.90f, 1.0f);

        public StatsPanel() : base("统计", "Stats")
        {
            CanClose = true;
            MinSize = new Vector2(240, 180);
        }

        /// <summary>每帧推送统计数据 (由 MainEditorWindow 调用)。</summary>
        public void UpdateStats(float fps, float frameMs, FrameTimeBreakdown frame, MemoryStats mem, DrawCallStats dc)
        {
            CurrentFps = fps;
            CurrentFrameTimeMs = frameMs;
            FrameTime = frame;
            Memory = mem;
            DrawCalls = dc;

            _fpsHistory[_historyOffset] = fps;
            _frameTimeHistory[_historyOffset] = frameMs;
            _historyOffset = (_historyOffset + 1) % HistorySize;
            if (_historyCount < HistorySize) _historyCount++;

            if (fps < _minFps) _minFps = fps;
            if (fps > _maxFps) _maxFps = fps;
            if (frameMs > _maxFrameTimeMs) _maxFrameTimeMs = frameMs;
        }

        protected override void OnDraw(EditorContext ctx)
        {
            DrawHeader();
            ImGui.Separator();

            if (_showFpsChart) DrawFpsChart();
            if (_showFrameTime) DrawFrameTimeBreakdown();
            if (_showMemory) DrawMemory(ctx);
            if (_showDrawCalls) DrawDrawCalls();
            if (_showEntities) DrawEntityStats(ctx);
        }

        // ─────────────────────────── 头部 ───────────────────────────

        private void DrawHeader()
        {
            // FPS / FrameTime 大字显示
            float fps = CurrentFps;
            var fpsColor = fps >= 55 ? new Vector4(0.3f, 0.85f, 0.4f, 1f)
                        : fps >= 30 ? new Vector4(0.95f, 0.75f, 0.2f, 1f)
                        : new Vector4(0.9f, 0.3f, 0.3f, 1f);

            ImGui.PushStyleColor(ImGuiCol.Text, fpsColor);
            ImGui.Text($"  {fps:F1} FPS");
            ImGui.PopStyleColor();

            ImGui.SameLine(180);
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.75f, 1f), $"帧时间: {CurrentFrameTimeMs:F2} ms");
            ImGui.SameLine(320);
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.6f, 1f), $"min {_minFps:F0} / max {_maxFps:F0}");

            // 显示开关
            ImGui.Checkbox("FPS 图表", ref _showFpsChart);
            ImGui.SameLine();
            ImGui.Checkbox("帧分解", ref _showFrameTime);
            ImGui.SameLine();
            ImGui.Checkbox("内存", ref _showMemory);
            ImGui.SameLine();
            ImGui.Checkbox("DrawCall", ref _showDrawCalls);
            ImGui.SameLine();
            ImGui.Checkbox("实体", ref _showEntities);
        }

        // ─────────────────────────── FPS 图表 ───────────────────────────

        private void DrawFpsChart()
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "FPS 历史");

            float minVal = 0f;
            float maxVal = MathF.Max(120f, _maxFps * 1.1f);
            string overlay = $"{CurrentFps:F1} FPS / {CurrentFrameTimeMs:F2}ms";

            // PlotLines 需要从偏移开始绘制环形缓冲
            unsafe
            {
                fixed (float* p = _fpsHistory)
                {
                    ImGui.PlotLines("##fps_chart", ref p[0], HistorySize, _historyOffset,
                                    overlay, minVal, maxVal, new Vector2(-1, 80));
                }
            }

            // 帧时间图
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "帧时间 (ms)");
            float ftMax = MathF.Max(33f, _maxFrameTimeMs * 1.2f);
            unsafe
            {
                fixed (float* p = _frameTimeHistory)
                {
                    ImGui.PlotLines("##ft_chart", ref p[0], HistorySize, _historyOffset,
                                    $"{CurrentFrameTimeMs:F2} ms", 0f, ftMax, new Vector2(-1, 50));
                }
            }

            ImGui.Separator();
        }

        // ─────────────────────────── 帧时间分解 ───────────────────────────

        private void DrawFrameTimeBreakdown()
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "帧时间分解");

            float total = FrameTime.TotalMs;
            if (total <= 0) { ImGui.TextDisabled("  无数据"); ImGui.Separator(); return; }

            DrawCategoryBar("Update", FrameTime.UpdateMs, total, ColUpdate);
            DrawCategoryBar("Render", FrameTime.RenderMs, total, ColRender);
            DrawCategoryBar("Physics", FrameTime.PhysicsMs, total, ColPhysics);
            DrawCategoryBar("Audio", FrameTime.AudioMs, total, ColAudio);
            DrawCategoryBar("GC", FrameTime.GcMs, total, ColGc);

            // 总计
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.75f, 1f),
                $"  总计: {total:F2} ms  (目标: 16.67 ms @ 60 FPS)");

            // 堆叠条形图 (总宽度的比例分配)
            var avail = ImGui.GetContentRegionAvail();
            float barW = avail.X - 16;
            if (barW < 100) barW = 100;
            float barH = 18;
            var pos = ImGui.GetCursorScreenPos();
            pos.X += 8;

            var drawList = ImGui.GetWindowDrawList();
            float x = pos.X;
            DrawStackSegment(drawList, x, pos.Y, barW * (FrameTime.UpdateMs / total), barH, ColUpdate);
            x += barW * (FrameTime.UpdateMs / total);
            DrawStackSegment(drawList, x, pos.Y, barW * (FrameTime.RenderMs / total), barH, ColRender);
            x += barW * (FrameTime.RenderMs / total);
            DrawStackSegment(drawList, x, pos.Y, barW * (FrameTime.PhysicsMs / total), barH, ColPhysics);
            x += barW * (FrameTime.PhysicsMs / total);
            DrawStackSegment(drawList, x, pos.Y, barW * (FrameTime.AudioMs / total), barH, ColAudio);
            x += barW * (FrameTime.AudioMs / total);
            DrawStackSegment(drawList, x, pos.Y, barW * (FrameTime.GcMs / total), barH, ColGc);

            ImGui.Dummy(new Vector2(barW, barH + 4));
            ImGui.Separator();
        }

        private void DrawCategoryBar(string label, float ms, float total, Vector4 color)
        {
            float ratio = total > 0 ? ms / total : 0;
            string text = $"  {label,-10} {ms,6:F2} ms  ({ratio * 100,5:F1}%)";

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(ratio, new Vector2(-1, 14), text);
            ImGui.PopStyleColor();
        }

        private void DrawStackSegment(ImDrawListPtr drawList, float x, float y, float w, float h, Vector4 color)
        {
            if (w < 1) return;
            uint ucol = ImGui.ColorConvertFloat4ToU32(color);
            drawList.AddRectFilled(new Vector2(x, y), new Vector2(x + w, y + h), ucol);
        }

        // ─────────────────────────── 内存 ───────────────────────────

        private void DrawMemory(EditorContext ctx)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "内存使用");

            long total = Memory.TotalBytes;
            if (total <= 0)
            {
                // 实时获取托管内存
                Memory.ManagedBytes = GC.GetTotalMemory(false);
                total = Memory.TotalBytes;
            }

            DrawMemoryBar("Managed", Memory.ManagedBytes, total, ColManaged);
            DrawMemoryBar("GPU", Memory.GpuBytes, total, ColGpu);
            DrawMemoryBar("Texture", Memory.TextureBytes, total, ColTexture);
            DrawMemoryBar("Mesh", Memory.MeshBytes, total, ColMesh);

            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.75f, 1f),
                $"  总计: {FormatBytes(total)}");
            ImGui.Separator();
        }

        private void DrawMemoryBar(string label, long bytes, long total, Vector4 color)
        {
            float ratio = total > 0 ? (float)bytes / total : 0;
            string text = $"  {label,-10} {FormatBytes(bytes),10}  ({ratio * 100,5:F1}%)";

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(ratio, new Vector2(-1, 14), text);
            ImGui.PopStyleColor();
        }

        // ─────────────────────────── DrawCall ───────────────────────────

        private void DrawDrawCalls()
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "DrawCall 统计");

            DrawStatRow("总数", DrawCalls.Total, 1000, new Vector4(0.3f, 0.6f, 0.95f, 1f));
            DrawStatRow("Opaque", DrawCalls.Opaque, 1000, new Vector4(0.3f, 0.85f, 0.45f, 1f));
            DrawStatRow("Transparent", DrawCalls.Transparent, 500, new Vector4(0.8f, 0.45f, 0.9f, 1f));
            DrawStatRow("Shadow", DrawCalls.Shadow, 500, new Vector4(0.95f, 0.55f, 0.3f, 1f));

            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.75f, 1f),
                $"  三角形: {DrawCalls.Triangles:N0}");
            ImGui.Separator();
        }

        private void DrawStatRow(string label, int value, int max, Vector4 color)
        {
            float ratio = max > 0 ? Math.Clamp((float)value / max, 0, 1) : 0;
            string text = $"  {label,-12} {value,6}";

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(ratio, new Vector2(-1, 14), text);
            ImGui.PopStyleColor();
        }

        // ─────────────────────────── 实体/组件 ───────────────────────────

        private void DrawEntityStats(EditorContext ctx)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "实体 / 组件");

            int entityCount = ctx?.Assets?.ObjectCount ?? 0;
            int visibleCount = 0;
            int meshCount = 0;
            int lightCount = 0;
            int cameraCount = 0;
            int componentCount = 0;

            if (ctx?.Assets != null)
            {
                foreach (var obj in ctx.Assets.SceneObjects)
                {
                    if (obj == null) continue;
                    if (obj.IsVisible) visibleCount++;
                    if (!string.IsNullOrEmpty(obj.MeshAssetPath)) meshCount++;
                    if (obj.Components != null) componentCount += obj.Components.Count;
                }
            }

            ImGui.Columns(2, "##entity_stats", false);
            ImGui.Text($"  实体总数:  {entityCount}");
            ImGui.NextColumn();
            ImGui.Text($"  可见实体:  {visibleCount}");
            ImGui.NextColumn();
            ImGui.Text($"  网格对象:  {meshCount}");
            ImGui.NextColumn();
            ImGui.Text($"  组件总数:  {componentCount}");
            ImGui.NextColumn();
            ImGui.Text($"  选中数量:  {ctx?.Selection?.Count ?? 0}");
            ImGui.NextColumn();
            ImGui.Text($"  撤销栈:    {ctx?.Undo?.UndoCount ?? 0}");
            ImGui.Columns(1);

            ImGui.Separator();
        }

        // ─────────────────────────── 工具 ───────────────────────────

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
