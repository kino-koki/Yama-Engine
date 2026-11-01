using System.Numerics;
using ImGuiNET;
using YamaEngine.Editor;
using LogLevel = YamaEngine.Config.LogLevel;

namespace YamaEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // 工具栏面板 (ToolbarPanel)
    //
    // 集中放置编辑器常用控制:
    //   * PIE 控制 (Play / Stop / Pause / Resume / Step)
    //   * 变换模式切换 (Translate / Rotate / Scale, 对应 W/E/R)
    //   * 捕捉设置 (位置 / 旋转 / 缩放, 开关 + 数值)
    //   * 网格显示开关 + 网格大小
    //   * 渲染模式切换 (Lit/Unlit/Wireframe/Albedo/Normal/Roughness/Metallic/Emission/Depth/Velocity)
    //   * 统计覆盖层开关
    //
    // 捕捉与渲染模式状态通过公共属性供视口/Gizmo 读取。
    // ════════════════════════════════════════════════════════════════════════

    public enum ViewportRenderMode
    {
        Lit, Unlit, Wireframe, Albedo, Normal, Roughness, Metallic, Emission, Depth, Velocity
    }

    public sealed class SnapSettings
    {
        public bool PositionSnap = true;
        public float Position = 0.25f;

        public bool RotationSnap = true;
        public float Rotation = 15.0f;

        public bool ScaleSnap = false;
        public float Scale = 0.25f;
    }

    public sealed class ToolbarPanel : EditorPanel
    {
        private static readonly string[] RenderModeNames =
        {
            "Lit (光照)", "Unlit (无光)", "Wireframe (线框)", "Albedo (反照率)",
            "Normal (法线)", "Roughness (粗糙度)", "Metallic (金属度)", "Emission (自发光)",
            "Depth (深度)", "Velocity (速度)"
        };

        public SnapSettings Snap { get; } = new();
        public ViewportRenderMode RenderMode { get; set; } = ViewportRenderMode.Lit;
        public bool ShowGrid { get; set; } = true;
        public float GridSize { get; set; } = 1.0f;
        public bool ShowStatsOverlay { get; set; } = true;

        public ToolbarPanel() : base("工具栏", "Toolbar")
        {
            CanClose = true;
            MinSize = new Vector2(300, 200);
        }

        protected override void OnDraw(EditorContext ctx)
        {
            DrawPie(ctx);
            ImGui.Separator();
            DrawTransformMode(ctx);
            ImGui.Separator();
            DrawSnap(ctx);
            ImGui.Separator();
            DrawGrid(ctx);
            ImGui.Separator();
            DrawRenderMode(ctx);
            ImGui.Separator();
            DrawOverlayToggle(ctx);
        }

        // ─────────────────────── PIE ───────────────────────

        private void DrawPie(EditorContext ctx)
        {
            ImGui.Text("Play-In-Editor");
            var pie = ctx.World.Pie;
            bool playing = pie.IsPlaying;
            bool paused = pie.IsPaused;

            if (!playing)
            {
                if (ToggleButton("▶ 播放", false)) pie.Play();
            }
            else
            {
                ImGui.BeginDisabled(true);
                ToggleButton("▶ 播放", true);
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
            if (ImGui.Button("■ 停止")) pie.Stop();
            ImGui.SameLine();
            if (!playing) { ImGui.BeginDisabled(true); }
            if (paused) { if (ImGui.Button("▶ 继续")) pie.Resume(); }
            else { if (ImGui.Button("⏸ 暂停")) pie.Pause(); }
            if (!playing) { ImGui.EndDisabled(); }
            ImGui.SameLine();
            if (!playing || !paused) { ImGui.BeginDisabled(true); }
            if (ImGui.Button("⏭ 单步")) pie.StepFrame();
            if (!playing || !paused) { ImGui.EndDisabled(); }

            ImGui.SameLine();
            ImGui.TextDisabled(playing ? (paused ? "已暂停" : "运行中") : "已停止");
        }

        // ─────────────────────── 变换模式 ───────────────────────

        private void DrawTransformMode(EditorContext ctx)
        {
            ImGui.Text("变换模式");
            var gizmo = ctx.Editor?.Viewport?.Gizmo;
            var mode = gizmo?.Mode ?? GizmoMode.Move;

            if (ToggleButton("移动 (W)", mode == GizmoMode.Move)) SetMode(ctx, GizmoMode.Move);
            ImGui.SameLine();
            if (ToggleButton("旋转 (E)", mode == GizmoMode.Rotate)) SetMode(ctx, GizmoMode.Rotate);
            ImGui.SameLine();
            if (ToggleButton("缩放 (R)", mode == GizmoMode.Scale)) SetMode(ctx, GizmoMode.Scale);
            ImGui.SameLine();
            ImGui.Checkbox("世界坐标", ref _useWorldSpace);
            if (gizmo != null) gizmo.UseWorldSpace = _useWorldSpace;
        }

        private bool _useWorldSpace = true;

        private void SetMode(EditorContext ctx, GizmoMode mode)
        {
            var gizmo = ctx.Editor?.Viewport?.Gizmo;
            if (gizmo != null) gizmo.Mode = mode;
        }

        // ─────────────────────── 捕捉 ───────────────────────

        private void DrawSnap(EditorContext ctx)
        {
            ImGui.Text("捕捉");
            ImGui.Checkbox("位置", ref Snap.PositionSnap);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.DragFloat("##PosSnap", ref Snap.Position, 0.05f, 0.001f, 100f);

            ImGui.Checkbox("旋转", ref Snap.RotationSnap);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.DragFloat("##RotSnap", ref Snap.Rotation, 0.5f, 1f, 90f, "%.1f°");

            ImGui.Checkbox("缩放", ref Snap.ScaleSnap);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.DragFloat("##SclSnap", ref Snap.Scale, 0.05f, 0.001f, 10f);
        }

        // ─────────────────────── 网格 ───────────────────────

        private void DrawGrid(EditorContext ctx)
        {
            ImGui.Text("网格");
            ImGui.Checkbox("显示网格", ref _showGridField);
            ShowGrid = _showGridField;
            var vp = ctx.Editor?.Viewport;
            if (vp != null) vp.ShowGrid = ShowGrid;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.DragFloat("网格大小", ref _gridSizeField, 0.25f, 0.0625f, 16f);
            GridSize = _gridSizeField;
        }

        private bool _showGridField = true;
        private float _gridSizeField = 1.0f;

        // ─────────────────────── 渲染模式 ───────────────────────

        private void DrawRenderMode(EditorContext ctx)
        {
            ImGui.Text("渲染模式");
            int idx = (int)RenderMode;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.Combo("##RenderMode", ref idx, RenderModeNames, RenderModeNames.Length))
            {
                RenderMode = (ViewportRenderMode)idx;
                ctx.Console?.AddLog(LogLevel.Debug, $"渲染模式: {RenderMode}", "Toolbar");
            }
        }

        // ─────────────────────── 覆盖层 ───────────────────────

        private void DrawOverlayToggle(EditorContext ctx)
        {
            ImGui.Checkbox("显示统计覆盖层", ref _overlayField);
            ShowStatsOverlay = _overlayField;
            var vp = ctx.Editor?.Viewport;
            if (vp != null) vp.Overlay.Enabled = ShowStatsOverlay;
        }

        private bool _overlayField = true;

        // ─────────────────────── 工具 ───────────────────────

        private static bool ToggleButton(string label, bool active)
        {
            if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.26f, 0.59f, 0.98f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.35f, 0.66f, 1f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.2f, 0.5f, 0.85f, 1f));
            }
            bool clicked = ImGui.Button(label);
            if (active) ImGui.PopStyleColor(3);
            return clicked;
        }
    }
}
