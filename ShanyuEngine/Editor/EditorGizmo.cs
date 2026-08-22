using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShanyuEngine.Assets;
using ShanyuEngine.Editor.Panels;
using ShanyuEngine.Input;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 编辑器 Gizmo 系统 (EditorGizmo)
    //
    // 在 GizmoSystem (低层渲染/交互) 之上提供编辑器级管理:
    //   - 变换模式切换 (Move / Rotate / Scale)
    //   - 世界 / 本地坐标系对齐
    //   - 射线拾取 (点击选中场景对象)
    //   - 捕捉支持 (位置 / 旋转 / 缩放)
    //   - 多选包围盒 + 中心 Gizmo
    //   - 拖拽结束自动记录撤销命令
    //   - 框选 (BoxSelect) 支持
    //
    // 生命周期:
    //   1. EditorViewport.Update 处理 Gizmo 交互 (HitTest / BeginDrag / UpdateDrag / EndDrag)
    //   2. EditorGizmo.PostUpdate 在其后调用, 处理捕捉 + 撤销记录
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器 Gizmo 管理器: 封装 GizmoSystem 的编辑器级逻辑。</summary>
    public sealed class EditorGizmo
    {
        private readonly GizmoSystem _gizmo;
        private readonly AssetDatabase _assets;
        private readonly EditorCommandStack _undo;

        // ── 捕捉设置 ──
        public bool PositionSnapEnabled = true;
        public float PositionSnapSize = 0.25f;
        public bool RotationSnapEnabled = true;
        public float RotationSnapDegrees = 15.0f;
        public bool ScaleSnapEnabled = false;
        public float ScaleSnapSize = 0.25f;

        // ── 拖拽状态追踪 ──
        private bool _wasDragging;
        private Dictionary<Guid, TransformSnapshot> _preDragState;
        private Vector2 _boxSelectStart;
        private bool _boxSelectActive;

        /// <summary>变换快照 (拖拽前记录)。</summary>
        private struct TransformSnapshot
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        // ── 属性代理 ──

        public GizmoMode Mode
        {
            get => _gizmo.Mode;
            set => _gizmo.Mode = value;
        }

        public bool UseWorldSpace
        {
            get => _gizmo.UseWorldSpace;
            set => _gizmo.UseWorldSpace = value;
        }

        public GizmoAxis ActiveAxis => _gizmo.ActiveAxis;
        public bool IsDragging => _gizmo.IsDragging;
        public float ScreenSize
        {
            get => _gizmo.ScreenSize;
            set => _gizmo.ScreenSize = value;
        }

        public EditorGizmo(GizmoSystem gizmo, AssetDatabase assets, EditorCommandStack undo)
        {
            _gizmo = gizmo ?? throw new ArgumentNullException(nameof(gizmo));
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        }

        // ─────────────────────────── 模式切换 ───────────────────────────

        /// <summary>设置变换模式并切换 Gizmo。</summary>
        public void SetMode(GizmoMode mode)
        {
            _gizmo.Mode = mode;
        }

        /// <summary>切换世界 / 本地坐标系。</summary>
        public void ToggleSpace()
        {
            _gizmo.UseWorldSpace = !_gizmo.UseWorldSpace;
        }

        /// <summary>从 ToolbarPanel 的 SnapSettings 同步捕捉参数。</summary>
        public void SyncSnapSettings(SnapSettings snap)
        {
            if (snap == null) return;
            PositionSnapEnabled = snap.PositionSnap;
            PositionSnapSize = snap.Position;
            RotationSnapEnabled = snap.RotationSnap;
            RotationSnapDegrees = snap.Rotation;
            ScaleSnapEnabled = snap.ScaleSnap;
            ScaleSnapSize = snap.Scale;
        }

        // ─────────────────────────── 拾取 ───────────────────────────

        /// <summary>执行射线拾取, 返回最近命中的对象。</summary>
        /// <param name="screenPx">屏幕像素坐标 (相对视口左上角)。</param>
        /// <param name="cam">视口摄像机。</param>
        /// <param name="viewportSize">视口尺寸。</param>
        /// <param name="layerMask">层掩码 (0 = 全部)。</param>
        public SceneObject Pick(Vector2 screenPx, ViewportCamera cam, Vector2 viewportSize, uint layerMask = 0)
        {
            if (cam == null || _assets == null) return null;
            var ray = cam.ScreenToWorldRay(screenPx, viewportSize);
            return RayPicking.Pick(_assets, ray, layerMask);
        }

        /// <summary>执行拾取并更新选择系统。</summary>
        public SceneObject PickAndSelect(Vector2 screenPx, ViewportCamera cam, Vector2 viewportSize,
                                         SelectionSystem selection, bool additive, uint layerMask = 0)
        {
            var hit = Pick(screenPx, cam, viewportSize, layerMask);
            if (selection == null) return hit;

            if (hit == null)
            {
                if (!additive) selection.Clear();
                return null;
            }

            // Ctrl+点击: 切换选择
            if (additive)
            {
                if (hit.IsSelected)
                    selection.Deselect(hit);
                else
                    selection.Select(hit, additive: true);
            }
            else
            {
                selection.Select(hit, additive: false);
            }

            return hit;
        }

        // ─────────────────────────── 拖拽后处理 (每帧) ───────────────────────────

        /// <summary>
        /// 在 EditorViewport.Update 之后调用, 处理:
        ///   - 拖拽开始时记录变换快照
        ///   - 拖拽中应用捕捉
        ///   - 拖拽结束时记录撤销命令
        /// </summary>
        public void PostUpdate(EditorContext ctx, ViewportCamera cam, Vector2 viewportSize)
        {
            if (ctx?.Selection == null) return;
            var selection = ctx.Selection.Selected.ToList();
            if (selection.Count == 0) { _wasDragging = false; return; }

            // ── 拖拽开始: 记录快照 ──
            if (_gizmo.IsDragging && !_wasDragging)
            {
                if (_gizmo.Mode == GizmoMode.BoxSelect)
                {
                    _boxSelectActive = true;
                }
                else
                {
                    _preDragState = new Dictionary<Guid, TransformSnapshot>();
                    foreach (var obj in selection)
                    {
                        if (obj == null) continue;
                        _preDragState[obj.Id] = new TransformSnapshot
                        {
                            Position = obj.Position,
                            Rotation = obj.Rotation,
                            Scale = obj.Scale
                        };
                    }
                }
            }

            // ── 拖拽中: 应用捕捉 ──
            if (_gizmo.IsDragging && _gizmo.Mode != GizmoMode.BoxSelect)
            {
                ApplySnapToSelection(selection);
            }

            // ── 拖拽结束: 记录撤销 ──
            if (!_gizmo.IsDragging && _wasDragging)
            {
                if (_boxSelectActive)
                {
                    // 框选结束: 执行选择
                    _boxSelectActive = false;
                }
                else if (_preDragState != null)
                {
                    RecordDragUndo(selection, ctx);
                    _preDragState = null;
                }
            }

            _wasDragging = _gizmo.IsDragging;
        }

        // ─────────────────────────── 捕捉 ───────────────────────────

        /// <summary>对选中对象应用捕捉 (直接修改对象变换)。</summary>
        private void ApplySnapToSelection(List<SceneObject> selection)
        {
            foreach (var obj in selection)
            {
                if (obj == null) continue;

                if (PositionSnapEnabled && _gizmo.Mode == GizmoMode.Move)
                {
                    obj.Position = SnapVector(obj.Position, PositionSnapSize);
                    obj.LocalPosition = obj.Position;
                }

                if (RotationSnapEnabled && _gizmo.Mode == GizmoMode.Rotate)
                {
                    obj.Rotation = SnapRotation(obj.Rotation, RotationSnapDegrees);
                    obj.LocalRotation = obj.Rotation;
                }

                if (ScaleSnapEnabled && _gizmo.Mode == GizmoMode.Scale)
                {
                    obj.Scale = SnapVector(obj.Scale, ScaleSnapSize);
                    obj.LocalScale = obj.Scale;
                }
            }
        }

        /// <summary>将向量各分量取整到最近的网格倍数。</summary>
        private static Vector3 SnapVector(Vector3 v, float gridSize)
        {
            if (gridSize <= 0) return v;
            return new Vector3(
                MathF.Round(v.X / gridSize) * gridSize,
                MathF.Round(v.Y / gridSize) * gridSize,
                MathF.Round(v.Z / gridSize) * gridSize);
        }

        /// <summary>将四元数旋转捕捉到最近的 snapDegrees 倍数。</summary>
        private static Quaternion SnapRotation(Quaternion q, float snapDegrees)
        {
            if (snapDegrees <= 0) return q;
            var euler = EditorMathExtensions.ToEulerDegrees(q);
            float snapRad = snapDegrees;
            euler = new Vector3(
                MathF.Round(euler.X / snapRad) * snapRad,
                MathF.Round(euler.Y / snapRad) * snapRad,
                MathF.Round(euler.Z / snapRad) * snapRad);
            return EditorMathExtensions.FromEulerDegrees(euler);
        }

        // ─────────────────────────── 撤销记录 ───────────────────────────

        /// <summary>拖拽结束后, 为每个被修改的对象创建撤销命令。</summary>
        private void RecordDragUndo(List<SceneObject> selection, EditorContext ctx)
        {
            if (_preDragState == null) return;

            bool anyChange = false;
            foreach (var obj in selection)
            {
                if (obj == null) continue;
                if (!_preDragState.TryGetValue(obj.Id, out var before)) continue;

                // 位置变化
                if (obj.Position != before.Position)
                {
                    var after = obj.Position;
                    // 临时恢复 before 以让命令正确记录
                    obj.Position = before.Position;
                    obj.LocalPosition = before.Position;
                    _undo.Execute(new MoveObjectCommand(obj, after));
                    anyChange = true;
                }

                // 旋转变化
                if (obj.Rotation != before.Rotation)
                {
                    var after = obj.Rotation;
                    obj.Rotation = before.Rotation;
                    obj.LocalRotation = before.Rotation;
                    _undo.Execute(new RotateObjectCommand(obj, after));
                    anyChange = true;
                }

                // 缩放变化
                if (obj.Scale != before.Scale)
                {
                    var after = obj.Scale;
                    obj.Scale = before.Scale;
                    obj.LocalScale = before.Scale;
                    _undo.Execute(new ScaleObjectCommand(obj, after));
                    anyChange = true;
                }
            }

            if (anyChange)
            {
                ctx.SceneManager?.MarkDirty();
            }
        }

        // ─────────────────────────── 框选 ───────────────────────────

        /// <summary>执行屏幕空间框选。</summary>
        /// <param name="min">框选矩形左上角 (像素)。</param>
        /// <param name="max">框选矩形右下角 (像素)。</param>
        /// <param name="cam">视口摄像机。</param>
        /// <param name="viewportSize">视口尺寸。</param>
        /// <param name="selection">选择系统。</param>
        /// <param name="additive">是否为追加选择 (Shift)。</param>
        public void ApplyBoxSelect(Vector2 min, Vector2 max, ViewportCamera cam,
                                   Vector2 viewportSize, SelectionSystem selection, bool additive)
        {
            if (_assets == null || cam == null || selection == null) return;

            var candidates = new List<SceneObject>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (obj == null || !obj.IsVisible) continue;

                // 将对象世界位置投影到屏幕, 检查是否在矩形内
                var ndc = cam.WorldToScreen(obj.Position);
                var screen = new Vector2(
                    (ndc.X + 1f) * 0.5f * viewportSize.X,
                    (1f - ndc.Y) * 0.5f * viewportSize.Y);

                if (screen.X >= min.X && screen.X <= max.X &&
                    screen.Y >= min.Y && screen.Y <= max.Y)
                {
                    candidates.Add(obj);
                }
            }

            selection.BoxSelect(candidates, additive);
        }

        // ─────────────────────────── 多选包围盒 ───────────────────────────

        /// <summary>计算选中对象的合并包围盒 (世界空间)。</summary>
        public BoundingBox ComputeSelectionBounds(List<SceneObject> selection)
        {
            if (selection == null || selection.Count == 0)
                return new BoundingBox { Min = Vector3.Zero, Max = Vector3.Zero };

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            foreach (var obj in selection)
            {
                if (obj == null) continue;
                var b = obj.Bounds.Transform(obj.LocalToWorldMatrix);
                min = Vector3.Min(min, b.Min);
                max = Vector3.Max(max, b.Max);
            }

            if (min.X == float.MaxValue) return new BoundingBox { Min = Vector3.Zero, Max = Vector3.Zero };
            return new BoundingBox { Min = min, Max = max };
        }

        /// <summary>计算多选 Gizmo 位置 (选中对象包围盒中心)。</summary>
        public Vector3 GetMultiSelectCenter(List<SceneObject> selection)
        {
            if (selection == null || selection.Count == 0) return Vector3.Zero;
            if (selection.Count == 1) return selection[0].Position;

            var bounds = ComputeSelectionBounds(selection);
            return bounds.Center;
        }

        // ─────────────────────────── Gizmo 轴拾取 ───────────────────────────

        /// <summary>测试鼠标悬停在哪个 Gizmo 轴上 (代理 GizmoSystem.HitTest)。</summary>
        public GizmoAxis HitTest(Vector2 screenPx, ViewportCamera cam, Vector2 viewportSize, List<SceneObject> selection)
        {
            return _gizmo.HitTest(screenPx, cam, viewportSize, selection);
        }

        /// <summary>获取当前 Gizmo 的世界位置 (代理 GizmoSystem.GetGizmoPosition)。</summary>
        public Vector3 GetGizmoPosition(List<SceneObject> selection, ViewportCamera cam)
        {
            return _gizmo.GetGizmoPosition(selection, cam);
        }

        // ─────────────────────────── 工具 ───────────────────────────

        /// <summary>检查点击是否落在 Gizmo 上 (而非场景对象)。</summary>
        public bool IsMouseOverGizmo(Vector2 screenPx, ViewportCamera cam, Vector2 viewportSize, List<SceneObject> selection)
        {
            if (_gizmo.Mode == GizmoMode.None || selection == null || selection.Count == 0)
                return false;
            return _gizmo.HitTest(screenPx, cam, viewportSize, selection) != GizmoAxis.None ||
                   _gizmo.ActiveAxis != GizmoAxis.None;
        }

        /// <summary>重置 Gizmo 状态。</summary>
        public void Reset()
        {
            _wasDragging = false;
            _preDragState = null;
            _boxSelectActive = false;
            _gizmo.EndDrag();
        }
    }
}
