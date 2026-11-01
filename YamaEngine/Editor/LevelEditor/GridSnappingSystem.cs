using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 网格吸附系统 (GridSnappingSystem)
    //
    // 网格吸附与渲染系统:
    //   * SnapMode 枚举 (None/Grid/Vertex/Surface/Increment)
    //   * GridSettings: 大小/旋转步长/缩放步长/可见性/轴颜色
    //   * GridRenderer: 无限网格渲染 (主线/次线/轴高亮/原点标记)
    //   * 吸附函数 (SnapToGrid/SnapToIncrement/SnapToSurface/SnapToVertex)
    //   * 吸附切换 (快捷键切换/临时按住吸附)
    //   * 自定义网格 (不同工作区不同网格大小)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>吸附模式。</summary>
    public enum SnapMode
    {
        /// <summary>无吸附。</summary>
        None,
        /// <summary>吸附到网格。</summary>
        Grid,
        /// <summary>吸附到顶点。</summary>
        Vertex,
        /// <summary>吸附到表面。</summary>
        Surface,
        /// <summary>按增量吸附。</summary>
        Increment
    }

    // ──────────────────────────────────────────────────────────────
    // 网格设置
    // ──────────────────────────────────────────────────────────────

    /// <summary>网格设置: 控制网格间距与步长。</summary>
    public sealed class GridSettings
    {
        /// <summary>主网格大小 (世界单位)。</summary>
        public float PrimarySize { get; set; } = 1f;
        /// <summary>细分网格大小 (子网格)。</summary>
        public float SubdivisionSize { get; set; } = 0.1f;

        /// <summary>旋转吸附步长 (度)。</summary>
        public float RotationStep { get; set; } = 15f;
        /// <summary>缩放吸附步长。</summary>
        public float ScaleStep { get; set; } = 0.1f;

        /// <summary>是否显示主网格。</summary>
        public bool ShowPrimaryGrid { get; set; } = true;
        /// <summary>是否显示子网格。</summary>
        public bool ShowSubGrid { get; set; } = false;
        /// <summary>是否显示坐标轴。</summary>
        public bool ShowAxes { get; set; } = true;

        /// <summary>主网格线颜色。</summary>
        public Vector3 PrimaryLineColor { get; set; } = new(0.3f, 0.3f, 0.32f);
        /// <summary>子网格线颜色。</summary>
        public Vector3 SubLineColor { get; set; } = new(0.2f, 0.2f, 0.22f);
        /// <summary>X 轴颜色。</summary>
        public Vector3 AxisXColor { get; set; } = new(0.8f, 0.2f, 0.2f);
        /// <summary>Y 轴颜色。</summary>
        public Vector3 AxisYColor { get; set; } = new(0.2f, 0.8f, 0.2f);
        /// <summary>Z 轴颜色。</summary>
        public Vector3 AxisZColor { get; set; } = new(0.2f, 0.4f, 0.9f);

        /// <summary>网格平面法线 (默认 Y 轴, 即 XZ 平面)。</summary>
        public Vector3 PlaneNormal { get; set; } = Vector3.UnitY;

        /// <summary>主网格每主格的细分数。</summary>
        public int SubdivisionsPerPrimary { get; set; } = 10;
    }

    // ──────────────────────────────────────────────────────────────
    // 网格渲染器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 无限网格渲染器: 构建网格线段供视口叠加层绘制。
    /// 自适应密度 (根据摄像机距离动态调整网格密度)。
    /// </summary>
    public sealed class GridRenderer
    {
        private readonly GridSettings _settings;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        public GridRenderer(GridSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>构建网格线段 (XZ 平面, 自适应密度)。</summary>
        public List<(Vector3 A, Vector3 B, Vector3 Color)> BuildGridLines(
            Vector3 viewCenter, float viewExtent, float yPlane)
        {
            var lines = new List<(Vector3, Vector3, Vector3)>();
            if (!Enabled || !_settings.ShowPrimaryGrid) return lines;

            float gridSize = _settings.PrimarySize;
            float extent = viewExtent + gridSize * 2f;

            int minLine = (int)MathF.Floor((viewCenter.X - extent) / gridSize);
            int maxLine = (int)MathF.Ceiling((viewCenter.X + extent) / gridSize);
            int minLineZ = (int)MathF.Floor((viewCenter.Z - extent) / gridSize);
            int maxLineZ = (int)MathF.Ceiling((viewCenter.Z + extent) / gridSize);

            // 主网格线
            for (int i = minLine; i <= maxLine; i++)
            {
                float x = i * gridSize;
                bool isAxis = i == 0;
                Vector3 color = isAxis ? _settings.AxisZColor : _settings.PrimaryLineColor;
                if (isAxis) color = _settings.AxisXColor;
                lines.Add((new Vector3(x, yPlane, viewCenter.Z - extent),
                           new Vector3(x, yPlane, viewCenter.Z + extent), color));
            }

            for (int i = minLineZ; i <= maxLineZ; i++)
            {
                float z = i * gridSize;
                bool isAxis = i == 0;
                Vector3 color = isAxis ? _settings.AxisZColor : _settings.PrimaryLineColor;
                lines.Add((new Vector3(viewCenter.X - extent, yPlane, z),
                           new Vector3(viewCenter.X + extent, yPlane, z), color));
            }

            // 子网格线 (如果启用)
            if (_settings.ShowSubGrid)
            {
                float subSize = _settings.SubdivisionSize;
                int subMin = (int)MathF.Floor((viewCenter.X - extent) / subSize);
                int subMax = (int)MathF.Ceiling((viewCenter.X + extent) / subSize);
                int subMinZ = (int)MathF.Floor((viewCenter.Z - extent) / subSize);
                int subMaxZ = (int)MathF.Ceiling((viewCenter.Z + extent) / subSize);

                for (int i = subMin; i <= subMax; i++)
                {
                    if (i % _settings.SubdivisionsPerPrimary == 0) continue;
                    float x = i * subSize;
                    lines.Add((new Vector3(x, yPlane, viewCenter.Z - extent),
                               new Vector3(x, yPlane, viewCenter.Z + extent), _settings.SubLineColor));
                }

                for (int i = subMinZ; i <= subMaxZ; i++)
                {
                    if (i % _settings.SubdivisionsPerPrimary == 0) continue;
                    float z = i * subSize;
                    lines.Add((new Vector3(viewCenter.X - extent, yPlane, z),
                               new Vector3(viewCenter.X + extent, yPlane, z), _settings.SubLineColor));
                }
            }

            // 坐标轴高亮
            if (_settings.ShowAxes)
            {
                // X 轴 (红色, 稍微抬高)
                lines.Add((new Vector3(viewCenter.X - extent, yPlane + 0.01f, 0),
                           new Vector3(viewCenter.X + extent, yPlane + 0.01f, 0), _settings.AxisXColor));
                // Z 轴 (蓝色)
                lines.Add((new Vector3(0, yPlane + 0.01f, viewCenter.Z - extent),
                           new Vector3(0, yPlane + 0.01f, viewCenter.Z + extent), _settings.AxisZColor));
            }

            return lines;
        }

        /// <summary>构建原点标记。</summary>
        public List<(Vector3 A, Vector3 B, Vector3 Color)> BuildOriginMarker(float yPlane)
        {
            var lines = new List<(Vector3, Vector3, Vector3)>();
            if (!_settings.ShowAxes) return lines;

            float len = _settings.PrimarySize * 5f;
            lines.Add((new Vector3(0, yPlane + 0.02f, 0), new Vector3(len, yPlane + 0.02f, 0), _settings.AxisXColor));
            lines.Add((new Vector3(0, yPlane + 0.02f, 0), new Vector3(0, yPlane + 0.02f, len), _settings.AxisZColor));
            return lines;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 自定义网格工作区
    // ──────────────────────────────────────────────────────────────

    /// <summary>网格工作区: 不同区域使用不同的网格大小。</summary>
    public sealed class GridWorkspace
    {
        /// <summary>工作区名称。</summary>
        public string Name { get; set; }
        /// <summary>网格设置。</summary>
        public GridSettings Settings { get; set; } = new();
        /// <summary>是否激活。</summary>
        public bool IsActive { get; set; }

        public GridWorkspace(string name)
        {
            Name = name;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 网格吸附系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网格吸附系统: 管理吸附模式/设置/自定义工作区, 提供吸附计算。
    /// </summary>
    public sealed class GridSnappingSystem
    {
        /// <summary>当前吸附模式。</summary>
        public SnapMode ActiveSnapMode { get; set; } = SnapMode.Grid;

        /// <summary>是否启用位置吸附。</summary>
        public bool PositionSnapEnabled { get; set; } = true;
        /// <summary>是否启用旋转吸附。</summary>
        public bool RotationSnapEnabled { get; set; } = true;
        /// <summary>是否启用缩放吸附。</summary>
        public bool ScaleSnapEnabled { get; set; } = true;

        /// <summary>网格设置 (默认工作区)。</summary>
        public GridSettings Settings { get; set; } = new();

        /// <summary>网格渲染器。</summary>
        public GridRenderer Renderer { get; private set; }

        /// <summary>自定义工作区列表。</summary>
        public readonly List<GridWorkspace> Workspaces = new();

        /// <summary>吸附模式切换事件。</summary>
        public event Action<SnapMode, SnapMode> OnSnapModeChanged;

        // 临时吸附 (按住快捷键时临时切换)
        private SnapMode _snapModeBeforeTemp;
        private bool _isTempOverride;

        public GridSnappingSystem()
        {
            Renderer = new GridRenderer(Settings);
        }

        /// <summary>切换吸附模式。</summary>
        public void SetSnapMode(SnapMode mode)
        {
            var prev = ActiveSnapMode;
            ActiveSnapMode = mode;
            OnSnapModeChanged?.Invoke(prev, mode);
        }

        /// <summary>临时启用/禁用吸附 (按住快捷键时)。</summary>
        public void BeginTemporaryOverride(SnapMode tempMode)
        {
            if (_isTempOverride) return;
            _snapModeBeforeTemp = ActiveSnapMode;
            ActiveSnapMode = tempMode;
            _isTempOverride = true;
        }

        /// <summary>结束临时吸附 (释放快捷键)。</summary>
        public void EndTemporaryOverride()
        {
            if (!_isTempOverride) return;
            ActiveSnapMode = _snapModeBeforeTemp;
            _isTempOverride = false;
        }

        /// <summary>吸附位置到网格。</summary>
        public Vector3 SnapToGrid(Vector3 position)
        {
            if (!PositionSnapEnabled || ActiveSnapMode != SnapMode.Grid) return position;
            float grid = Settings.PrimarySize;
            return new Vector3(
                MathF.Round(position.X / grid) * grid,
                MathF.Round(position.Y / grid) * grid,
                MathF.Round(position.Z / grid) * grid);
        }

        /// <summary>按增量吸附位置。</summary>
        public Vector3 SnapToIncrement(Vector3 position)
        {
            if (!PositionSnapEnabled || ActiveSnapMode != SnapMode.Increment) return position;
            float inc = Settings.PrimarySize;
            return new Vector3(
                MathF.Round(position.X / inc) * inc,
                position.Y, // 增量吸附通常不影响 Y
                MathF.Round(position.Z / inc) * inc);
        }

        /// <summary>吸附位置到表面 (射线与表面求交)。</summary>
        public Vector3 SnapToSurface(Vector3 position, Vector3 surfaceNormal, float offset = 0f)
        {
            if (ActiveSnapMode != SnapMode.Surface) return position;
            // 简化: 将位置投影到沿法线方向最近的表面
            // 真实实现: 射线求交
            return position + surfaceNormal * offset;
        }

        /// <summary>吸附位置到最近顶点。</summary>
        public Vector3 SnapToVertex(Vector3 position, IReadOnlyList<Vector3> vertices, float threshold = 1f)
        {
            if (ActiveSnapMode != SnapMode.Vertex || vertices == null || vertices.Count == 0)
                return position;

            Vector3 best = position;
            float bestDist = threshold;
            foreach (var v in vertices)
            {
                float d = Vector3.Distance(position, v);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = v;
                }
            }
            return best;
        }

        /// <summary>吸附旋转到步长。</summary>
        public Quaternion SnapRotation(Quaternion rotation)
        {
            if (!RotationSnapEnabled) return rotation;

            // 四元数转欧拉角, 吸附步长, 再转回
            var euler = QuaternionToEuler(rotation);
            float stepRad = Settings.RotationStep * MathF.PI / 180f;
            euler.X = MathF.Round(euler.X / stepRad) * stepRad;
            euler.Y = MathF.Round(euler.Y / stepRad) * stepRad;
            euler.Z = MathF.Round(euler.Z / stepRad) * stepRad;
            return EulerToQuaternion(euler);
        }

        /// <summary>吸附缩放到步长。</summary>
        public Vector3 SnapScale(Vector3 scale)
        {
            if (!ScaleSnapEnabled) return scale;
            float step = Settings.ScaleStep;
            return new Vector3(
                MathF.Round(scale.X / step) * step,
                MathF.Round(scale.Y / step) * step,
                MathF.Round(scale.Z / step) * step);
        }

        /// <summary>通用吸附入口: 根据当前模式自动选择吸附方式。</summary>
        public Vector3 SnapPosition(Vector3 position, IReadOnlyList<Vector3> vertices = null)
        {
            return ActiveSnapMode switch
            {
                SnapMode.Grid => SnapToGrid(position),
                SnapMode.Increment => SnapToIncrement(position),
                SnapMode.Vertex => SnapToVertex(position, vertices ?? Array.Empty<Vector3>()),
                _ => position
            };
        }

        /// <summary>添加自定义工作区。</summary>
        public GridWorkspace AddWorkspace(string name, float gridSize)
        {
            var ws = new GridWorkspace(name)
            {
                Settings = { PrimarySize = gridSize }
            };
            Workspaces.Add(ws);
            return ws;
        }

        /// <summary>切换到指定工作区。</summary>
        public bool SwitchWorkspace(string name)
        {
            var ws = Workspaces.Find(w => w.Name == name);
            if (ws == null) return false;

            foreach (var w in Workspaces) w.IsActive = false;
            ws.IsActive = true;
            Settings = ws.Settings;
            Renderer = new GridRenderer(Settings);
            return true;
        }

        // 四元数转欧拉角 (弧度, YXZ 顺序)
        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            float sinX = 2f * (q.W * q.X + q.Y * q.Z);
            float cosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float x = MathF.Atan2(sinX, cosX);

            float sinY = 2f * (q.W * q.Y - q.Z * q.X);
            float y = MathF.Abs(sinY) >= 1f
                ? MathF.CopySign(MathF.PI / 2f, sinY)
                : MathF.Asin(sinY);

            float sinZ = 2f * (q.W * q.Z + q.X * q.Y);
            float cosZ = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float z = MathF.Atan2(sinZ, cosZ);

            return new Vector3(x, y, z);
        }

        // 欧拉角转四元数 (YXZ 顺序)
        private static Quaternion EulerToQuaternion(Vector3 euler)
        {
            return Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);
        }
    }
}
