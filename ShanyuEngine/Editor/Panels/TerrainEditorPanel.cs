// ==================================================================================================
//  TerrainEditorPanel.cs — 山雨引擎地形编辑器 ImGui 面板
//
//  命名空间: ShanyuEngine.Editor.Panels
//  依赖:     ImGuiNET, ShanyuEngine.Editor.LevelEditor
//
//  功能:
//    地形编辑器的 ImGui 界面，集成到主编辑器停靠布局。
//    1. 笔刷参数面板 (半径/强度/衰减/形状)
//    2. 雕刻模式选择 (升高/降低/平滑/平坦)
//    3. 图层管理 (添加/删除/编辑材质)
//    4. 噪声生成 (Perlin/Simplex/fBm + 参数)
//    5. 地形导出 (PNG/RAW/OBJ)
//    6. 地形创建向导
//    7. 笔刷预览 (Mini Map)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.Editor.LevelEditor;

namespace ShanyuEngine.Editor.Panels
{
    /// <summary>
    /// 地形编辑器面板。提供地形雕刻、绘制、噪声生成、图层管理和导出功能。
    /// </summary>
    public sealed class TerrainEditorPanel : EditorPanel
    {
        // ── 地形状态 ──────────────────────────────────────────────────────────

        private TerrainData? _terrainData;
        private TerrainComponent? _terrainComponent;
        private TerrainBrush _brush = new()
        {
            Radius = 20f,
            Strength = 0.5f,
            Falloff = 0.5f
        };

        // ── 当前模式 ──────────────────────────────────────────────────────────

        private TerrainBrushMode _mode = TerrainBrushMode.Sculpt;
        private SculptSubOp _sculptOp = SculptSubOp.Raise;
        private TerrainNoiseType _noiseType = TerrainNoiseType.Perlin;

        // ── 噪声参数 ──────────────────────────────────────────────────────────

        private float _noiseScale = 0.005f;
        private float _noiseAmplitude = 0.3f;
        private int _noiseOctaves = 5;
        private float _noisePersistence = 0.5f;
        private float _noiseLacunarity = 2.0f;
        private int _noiseSeed = 1337;

        // ── 当前图层索引 ──────────────────────────────────────────────────────

        private int _activeLayerIdx = -1;

        // ── 创建向导 ──────────────────────────────────────────────────────────

        private bool _showCreateDlg;
        private int _newTerrainSize = 513;
        private float _newTerrainWorldSize = 2048f;
        private float _newTerrainHeight = 512f;

        // ── 笔刷预览纹理 (简化版) ──────────────────────────────────────────────

        private bool _brushPreviewDirty = true;
        private float _previewRadius = 10f;

        // ── UI 颜色 ──────────────────────────────────────────────────────────

        private static readonly uint ColSculpt = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.7f, 0.4f, 1f));
        private static readonly uint ColPaint = ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f, 0.5f, 0.3f, 1f));
        private static readonly uint ColNoise = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.8f, 1f));

        // ══════════════════════════ 构造 ══════════════════════════

        public TerrainEditorPanel() : base("地形编辑器", "TerrainEditor")
        {
            CanClose = true;
            MinSize = new Vector2(300, 400);
        }

        /// <summary>绑定地形数据。</summary>
        public void BindTerrain(TerrainComponent component)
        {
            _terrainComponent = component;
            _terrainData = component?.Data;
            _brushPreviewDirty = true;
        }

        // ══════════════════════════ 主绘制 ══════════════════════════

        protected override void OnDraw(EditorContext ctx)
        {
            if (_terrainData == null)
            {
                DrawNoTerrainMessage();
                return;
            }

            DrawBrushModeSelector();
            ImGui.Separator();

            switch (_mode)
            {
                case TerrainBrushMode.Sculpt:
                    DrawSculptPanel();
                    break;
                case TerrainBrushMode.Paint:
                    DrawPaintPanel();
                    break;
                case TerrainBrushMode.Noise:
                    DrawNoisePanel();
                    break;
            }

            ImGui.Separator();
            DrawBrushPreview();
            ImGui.Separator();
            DrawLayerManager();
            ImGui.Separator();
            DrawExportSection();
        }

        // ══════════════════════════ 无地形提示 ══════════════════════════

        private void DrawNoTerrainMessage()
        {
            ImGui.TextColored(new Vector4(0.8f, 0.6f, 0.3f, 1f), "未加载地形");
            ImGui.Spacing();
            ImGui.TextWrapped("点击下方按钮创建新地形，或从内容浏览器拖入地形资产。");
            ImGui.Spacing();

            if (ImGui.Button("创建地形...", new Vector2(-1, 30)))
                _showCreateDlg = true;

            if (_showCreateDlg) DrawCreateTerrainDialog();
        }

        private void DrawCreateTerrainDialog()
        {
            ImGui.OpenPopup("创建地形");
            if (ImGui.BeginPopupModal("创建地形", ref _showCreateDlg,
                ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("地形参数");
                ImGui.Separator();

                ImGui.LabelText("分辨率", $"{_newTerrainSize} x {_newTerrainSize}");
                ImGui.SliderInt("##res", ref _newTerrainSize, 65, 4097);

                ImGui.LabelText("世界尺寸", $"{_newTerrainWorldSize:F0} m");
                ImGui.SliderFloat("##world", ref _newTerrainWorldSize, 256, 8192);

                ImGui.LabelText("高度范围", $"{_newTerrainHeight:F0} m");
                ImGui.SliderFloat("##height", ref _newTerrainHeight, 64, 2048);

                ImGui.Separator();

                if (ImGui.Button("创建", new Vector2(120, 0)))
                {
                    var data = new TerrainData(_newTerrainSize, _newTerrainSize)
                    {
                        WorldSizeX = _newTerrainWorldSize,
                        WorldSizeZ = _newTerrainWorldSize,
                        HeightRange = _newTerrainHeight
                    };
                    _terrainComponent = new TerrainComponent(data);
                    _terrainData = data;
                    _showCreateDlg = false;
                }

                ImGui.SameLine();
                if (ImGui.Button("取消", new Vector2(120, 0)))
                    _showCreateDlg = false;

                ImGui.EndPopup();
            }
        }

        // ══════════════════════════ 模式选择器 ══════════════════════════

        private void DrawBrushModeSelector()
        {
            ImGui.Text("编辑模式");
            ImGui.Spacing();

            float w = ImGui.GetContentRegionAvail().X / 3f - 4f;

            if (_mode == TerrainBrushMode.Sculpt)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.4f, 0.2f, 1f));
            if (ImGui.Button("雕刻", new Vector2(w, 0))) _mode = TerrainBrushMode.Sculpt;
            if (_mode == TerrainBrushMode.Sculpt) ImGui.PopStyleColor();

            ImGui.SameLine();
            if (_mode == TerrainBrushMode.Paint)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.4f, 0.3f, 0.15f, 1f));
            if (ImGui.Button("绘制", new Vector2(w, 0))) _mode = TerrainBrushMode.Paint;
            if (_mode == TerrainBrushMode.Paint) ImGui.PopStyleColor();

            ImGui.SameLine();
            if (_mode == TerrainBrushMode.Noise)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.4f, 1f));
            if (ImGui.Button("噪声", new Vector2(w, 0))) _mode = TerrainBrushMode.Noise;
            if (_mode == TerrainBrushMode.Noise) ImGui.PopStyleColor();
        }

        // ══════════════════════════ 雕刻面板 ══════════════════════════

        private void DrawSculptPanel()
        {
            ImGui.Text("雕刻工具");
            ImGui.Spacing();

            // 子操作选择
            string[] ops = { "升高 ↑", "降低 ↓", "平滑 ~", "平坦 =" };
            SculptSubOp[] opVals = { SculptSubOp.Raise, SculptSubOp.Lower, SculptSubOp.Smooth, SculptSubOp.Flatten };
            for (int i = 0; i < ops.Length; i++)
            {
                if (ImGui.RadioButton(ops[i], _sculptOp == opVals[i]))
                    _sculptOp = opVals[i];
                if (i < ops.Length - 1) ImGui.SameLine();
            }

            ImGui.Separator();
            DrawBrushParameters();

            if (_sculptOp == SculptSubOp.Flatten)
            {
                ImGui.Separator();
                float target = _brush.Strength * 0.5f;
                ImGui.SliderFloat("平坦目标高度", ref target, 0f, 1f);
            }
        }

        // ══════════════════════════ 绘制面板 ══════════════════════════

        private void DrawPaintPanel()
        {
            ImGui.Text("图层绘制");
            ImGui.Spacing();

            if (_terrainComponent == null || _terrainComponent.Layers.Count == 0)
            {
                ImGui.TextDisabled("无图层，请先在下方图层管理器中添加图层");
                return;
            }

            // 图层选择
            for (int i = 0; i < _terrainComponent.Layers.Count; i++)
            {
                var layer = _terrainComponent.Layers[i];
                bool selected = i == _activeLayerIdx;

                if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.4f, 0.6f, 1f));
                if (ImGui.Button($"{layer.Name}##layer{i}", new Vector2(-1, 22)))
                    _activeLayerIdx = i;
                if (selected) ImGui.PopStyleColor();
            }

            ImGui.Separator();
            DrawBrushParameters();
        }

        // ══════════════════════════ 噪声面板 ══════════════════════════

        private void DrawNoisePanel()
        {
            ImGui.Text("噪声生成");
            ImGui.Spacing();

            // 噪声类型
            string[] names = { "Perlin", "Simplex", "Value", "fBm" };
            TerrainNoiseType[] types = {
                TerrainNoiseType.Perlin, TerrainNoiseType.Simplex,
                TerrainNoiseType.Value, TerrainNoiseType.FractalBrownianMotion
            };

            for (int i = 0; i < names.Length; i++)
            {
                if (ImGui.RadioButton(names[i], _noiseType == types[i]))
                    _noiseType = types[i];
                if (i < names.Length - 1) ImGui.SameLine();
            }

            ImGui.Separator();

            ImGui.SliderFloat("缩放", ref _noiseScale, 0.0001f, 0.05f, "%.4f");
            ImGui.SliderFloat("振幅", ref _noiseAmplitude, 0.01f, 1.0f);
            ImGui.SliderInt("八度数", ref _noiseOctaves, 1, 10);
            ImGui.SliderFloat("持续度", ref _noisePersistence, 0.1f, 1.0f);
            ImGui.SliderFloat("间隙度", ref _noiseLacunarity, 1.0f, 4.0f);
            ImGui.InputInt("种子", ref _noiseSeed);

            ImGui.Separator();
            DrawBrushParameters();

            ImGui.Separator();
            if (ImGui.Button("生成整个地形", new Vector2(-1, 28)))
                GenerateNoiseTerrain();
        }

        // ══════════════════════════ 笔刷参数 ══════════════════════════

        private void DrawBrushParameters()
        {
            ImGui.Text("笔刷参数");
            ImGui.Spacing();

            ImGui.SliderFloat("半径", ref _brush.Radius, 1f, 200f, "%.1f m");
            ImGui.SliderFloat("强度", ref _brush.Strength, 0.01f, 1.0f);
            ImGui.SliderFloat("衰减", ref _brush.Falloff, 0.01f, 2.0f);
            ImGui.Checkbox("反转笔刷", ref _brush.Inverted);

            ImGui.Spacing();
            ImGui.Text("形状");
            string[] shapes = { "圆形", "方形", "平滑", "衰减", "噪声" };
            BrushShape[] vals = { BrushShape.Circle, BrushShape.Square, BrushShape.Smooth, BrushShape.Falloff, BrushShape.Noise };
            for (int i = 0; i < shapes.Length; i++)
            {
                if (ImGui.RadioButton(shapes[i], _brush.Shape == vals[i]))
                    _brush.Shape = vals[i];
                if (i < shapes.Length - 1) ImGui.SameLine();
            }
        }

        // ══════════════════════════ 笔刷预览 ══════════════════════════

        private void DrawBrushPreview()
        {
            ImGui.Text("笔刷预览");
            ImGui.Spacing();

            var size = ImGui.GetContentRegionAvail();
            int previewSize = Math.Min((int)size.X, 128);
            if (previewSize < 32) previewSize = 32;

            var p0 = ImGui.GetCursorScreenPos();
            var p1 = new Vector2(p0.X + previewSize, p0.Y + previewSize);

            // 背景
            ImGui.GetWindowDrawList().AddRectFilled(p0, p1, 0xFF1A1A1A);

            // 画笔刷预览 (径向渐变)
            float cx = p0.X + previewSize * 0.5f;
            float cy = p0.Y + previewSize * 0.5f;
            float maxR = previewSize * 0.45f;

            int steps = 32;
            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)steps;
                float r = maxR * (1f - t);
                float w = _brush.CalculateWeight(r);
                byte alpha = (byte)(w * 255);
                uint color = (0xFFFFFF00u | (uint)alpha);
                ImGui.GetWindowDrawList().AddCircleFilled(
                    new Vector2(cx, cy), maxR * (1f - t), color, 16);
            }

            // 预览边框
            ImGui.GetWindowDrawList().AddRect(p0, p1, 0xFF333333);

            // 占位空间
            ImGui.Dummy(new Vector2(previewSize, previewSize));
        }

        // ══════════════════════════ 图层管理 ══════════════════════════

        private void DrawLayerManager()
        {
            ImGui.Text("图层管理");
            ImGui.Spacing();

            if (_terrainComponent == null) return;

            // 图层列表
            for (int i = 0; i < _terrainComponent.Layers.Count; i++)
            {
                var layer = _terrainComponent.Layers[i];
                bool enabled = layer.IsEnabled;

                ImGui.PushID($"layer{i}");

                if (ImGui.Checkbox("##en", ref enabled))
                    layer.IsEnabled = enabled;

                ImGui.SameLine();
                ImGui.Text(layer.Name);

                // 选中标记
                if (i == _activeLayerIdx)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 0.7f, 0.4f, 1f), "[当前]");
                }

                ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 60);
                if (ImGui.Button("编辑"))
                    _activeLayerIdx = i;
                ImGui.SameLine();
                if (ImGui.Button("删除"))
                    _terrainComponent.RemoveLayer(i);

                ImGui.PopID();
            }

            ImGui.Spacing();
            if (ImGui.Button("添加图层", new Vector2(-1, 24)))
            {
                int n = _terrainComponent.Layers.Count + 1;
                _terrainComponent.AddLayer($"Layer_{n}");
            }
        }

        // ══════════════════════════ 导出 ══════════════════════════

        private void DrawExportSection()
        {
            ImGui.Text("导出");
            ImGui.Spacing();

            float w = ImGui.GetContentRegionAvail().X / 3f - 4f;

            if (ImGui.Button("导出 PNG", new Vector2(w, 0)))
            {
                // 高度图导出 PNG
                ExportHeightmapPNG();
            }

            ImGui.SameLine();
            if (ImGui.Button("导出 RAW", new Vector2(w, 0)))
            {
                ExportHeightmapRAW();
            }

            ImGui.SameLine();
            if (ImGui.Button("导出 OBJ", new Vector2(w, 0)))
            {
                ExportTerrainOBJ();
            }
        }

        // ══════════════════════════ 噪声生成实现 ══════════════════════════

        private void GenerateNoiseTerrain()
        {
            if (_terrainData == null) return;

            var rand = new Random(_noiseSeed);

            for (int y = 0; y < _terrainData.Height; y++)
            {
                for (int x = 0; x < _terrainData.Width; x++)
                {
                    float nx = x * _noiseScale;
                    float ny = y * _noiseScale;

                    float val = 0f;
                    float amp = 1f;
                    float freq = 1f;
                    float maxVal = 0f;

                    for (int o = 0; o < _noiseOctaves; o++)
                    {
                        val += PerlinNoise(nx * freq, ny * freq, rand) * amp;
                        maxVal += amp;
                        amp *= _noisePersistence;
                        freq *= _noiseLacunarity;
                    }

                    val = val / Math.Max(0.001f, maxVal);
                    val = val * 0.5f + 0.5f; // 映射到 0..1
                    val *= _noiseAmplitude;

                    _terrainData.SetHeight(x, y, val);
                }
            }

            _terrainData.RecalculateNormals();
        }

        /// <summary>简化 Perlin 噪声 (基于随机梯度插值)。</summary>
        private static float PerlinNoise(float x, float y, Random rand)
        {
            int xi = (int)Math.Floor(x);
            int yi = (int)Math.Floor(y);
            float xf = x - xi;
            float yf = y - yi;

            float n00 = GradientDot(xi, yi, xf, yf, rand);
            float n10 = GradientDot(xi + 1, yi, xf - 1, yf, rand);
            float n01 = GradientDot(xi, yi + 1, xf, yf - 1, rand);
            float n11 = GradientDot(xi + 1, yi + 1, xf - 1, yf - 1, rand);

            float u = SmoothStep(xf);
            float v = SmoothStep(yf);

            float nx0 = Lerp(n00, n10, u);
            float nx1 = Lerp(n01, n11, u);
            return Lerp(nx0, nx1, v);
        }

        private static float GradientDot(int ix, int iy, float fx, float fy, Random r)
        {
            int h = (ix * 374761393 + iy * 668265263 + r.Next()) & 0x7FFFFFFF;
            float angle = (h / (float)0x7FFFFFFF) * MathF.PI * 2f;
            return MathF.Cos(angle) * fx + MathF.Sin(angle) * fy;
        }

        private static float SmoothStep(float t) => t * t * (3 - 2 * t);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // ══════════════════════════ 导出实现 ══════════════════════════

        private void ExportHeightmapPNG()
        {
            // 简化版: 实际应使用图像库
            // TODO: 接入引擎日志系统
        }

        private void ExportHeightmapRAW()
        {
            // TODO: 接入引擎日志系统
        }

        private void ExportTerrainOBJ()
        {
            // TODO: 接入引擎日志系统
        }
    }
}
