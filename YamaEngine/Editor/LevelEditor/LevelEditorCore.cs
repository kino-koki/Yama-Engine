using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using YamaEngine.Assets;
using YamaEngine.ECS;
using YamaEngine.Input;
using YamaEngine.Rendering;
using YamaEngine.RHI;

namespace YamaEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 关卡编辑器核心 (LevelEditorCore)
    //
    // 完整工业级关卡编辑器核心, 提供:
    //   * 编辑器模式枚举 (Select/Translate/Rotate/Scale/Place/Landscape/Paint)
    //   * 鼠标/键盘事件参数 (EditorMouseEvent / EditorKeyEvent / ToolRenderContext)
    //   * 工具基类架构 (LevelEditorTool) 与完整工具集
    //   * 撤销/重做系统 (UndoStack/RedoStack, 事务式操作, 复合命令)
    //   * 拖放放置 (DragDrop: 资产拖入视口场景)
    //   * 复制/粘贴/删除 (剪贴板系统, 深拷贝/实例化)
    //   * 场景层级树 (Actor hierarchy tree, 父子关系, 搜索/排序)
    //   * Play-In-Editor (PIE模式): 快照/恢复/暂停/单步
    //   * 编辑器偏好设置 (EditorPreferences: 快捷键/网格/捕捉/主题)
    //   * 工具栏管理器 (ToolbarManager)
    //   * 层级树管理器 (HierarchyTreeManager)
    //   * 拖放管理器 (DragDropManager)
    //
    // 对接系统: Assets / ECS / Rendering / RHI / Input
    // ════════════════════════════════════════════════════════════════════════

    // ─────────────────────────── 枚举定义 ───────────────────────────

    /// <summary>关卡编辑器模式枚举。</summary>
    public enum LevelEditorMode
    {
        /// <summary>选择模式。</summary>
        Select,
        /// <summary>移动模式。</summary>
        Move,
        /// <summary>旋转模式。</summary>
        Rotate,
        /// <summary>缩放模式。</summary>
        Scale,
        /// <summary>放置模式。</summary>
        Place,
        /// <summary>绘制模式。</summary>
        Paint,
        /// <summary>地形编辑模式。</summary>
        Landscape,
        /// <summary>植被模式。</summary>
        Foliage,
        /// <summary>触发区域编辑模式。</summary>
        Trigger,
        /// <summary>体积编辑模式。</summary>
        Volume,
        /// <summary>路径编辑模式。</summary>
        Path,
        /// <summary>样条编辑模式。</summary>
        Spline,
        /// <summary>几何体笔刷模式。</summary>
        GeometryBrush,
        /// <summary>地形雕刻模式。</summary>
        TerrainSculpt
    }

    /// <summary>坐标空间。</summary>
    public enum CoordinateSpace
    {
        /// <summary>世界空间。</summary>
        World,
        /// <summary>本地空间。</summary>
        Local,
        /// <summary>自定义轴。</summary>
        CustomAxis
    }

    /// <summary>鼠标按钮。</summary>
    public enum EditorMouseButton
    {
        Left,
        Right,
        Middle
    }

    /// <summary>笔刷形状。</summary>
    public enum BrushShape
    {
        Circle,
        Square,
        Noise,
        Smooth,
        Falloff
    }

    /// <summary>触发器形状。</summary>
    public enum TriggerShape
    {
        Box,
        Sphere,
        Capsule,
        Polygon
    }

    /// <summary>体积类型。</summary>
    public enum VolumeType
    {
        PostProcess,
        Light,
        Fog,
        Particle,
        Audio,
        Physics
    }

    /// <summary>路径类型。</summary>
    public enum PathType
    {
        Spline,
        Line,
        Curve,
        AiPath,
        CameraPath
    }

    /// <summary>地形笔刷操作。</summary>
    public enum LandscapeBrushOp
    {
        Raise,
        Lower,
        Smooth,
        Flatten,
        Erode,
        HydraulicErode,
        Noise,
        Layer
    }

    /// <summary>植被笔刷操作。</summary>
    public enum FoliageBrushOp
    {
        Paint,
        Erase,
        Select
    }

    /// <summary>选择子模式。</summary>
    public enum SelectSubMode
    {
        Point,
        Box,
        Lasso,
        Paint
    }

    /// <summary>变换子类型。</summary>
    public enum TransformSubType
    {
        Move,
        Rotate,
        Scale
    }

    /// <summary>变换轴向。</summary>
    public enum TransformAxis
    {
        None,
        X,
        Y,
        Z,
        XY,
        XZ,
        YZ,
        Screen
    }

    /// <summary>鼠标动作。</summary>
    public enum MouseAction
    {
        Press,
        Release,
        Move,
        DoubleClick
    }

    /// <summary>修饰键标志。</summary>
    [Flags]
    public enum ModifierKeys
    {
        None = 0,
        Shift = 1 << 0,
        Control = 1 << 1,
        Alt = 1 << 2
    }

    /// <summary>PIE (Play-In-Editor) 状态。</summary>
    public enum PieState
    {
        /// <summary>编辑模式 (未运行)。</summary>
        Editing,
        /// <summary>正在运行游戏逻辑。</summary>
        Playing,
        /// <summary>暂停 (游戏逻辑冻结, 但可单步)。</summary>
        Paused,
        /// <summary>正在从运行态恢复到编辑态 (回滚场景)。</summary>
        Restoring
    }

    /// <summary>层级树排序模式。</summary>
    public enum HierarchySortMode
    {
        Alphabetical,
        Type,
        Layer,
        CreationOrder
    }

    /// <summary>层级树节点展开状态。</summary>
    public enum HierarchyNodeState
    {
        Collapsed,
        Expanded
    }

    // ─────────────────────────── 事件参数 ───────────────────────────

    /// <summary>编辑器鼠标事件参数。</summary>
    public struct EditorMouseEvent
    {
        public Vector2 ScreenPosition;
        public Vector2 WorldPosition;
        public EditorMouseButton Button;
        public MouseAction Action;
        public Vector2 Delta;
        public float WheelDelta;
        public ModifierKeys Modifiers;
        public bool Shift => (Modifiers & ModifierKeys.Shift) != 0;
        public bool Ctrl => (Modifiers & ModifierKeys.Control) != 0;
        public bool Alt => (Modifiers & ModifierKeys.Alt) != 0;
    }

    /// <summary>键盘事件参数。</summary>
    public struct EditorKeyEvent
    {
        public int KeyCode;
        public char Character;
        public bool IsDown;
        public ModifierKeys Modifiers;
    }

    /// <summary>编辑器工具渲染上下文。</summary>
    public struct ToolRenderContext
    {
        public Vector2 ViewportSize;
        public bool IsOverlayPass;
        public float DeltaTime;
        public float ElapsedTime;
    }

    /// <summary>拖放事件参数。</summary>
    public struct DragDropEventArgs
    {
        public string AssetPath;
        public string AssetType;
        public Vector2 ScreenPosition;
        public Vector3 WorldPosition;
        public bool IsAccepted;
    }

    // ─────────────────────────── 坐标辅助 ───────────────────────────

    /// <summary>Vector2 (地面 XZ 平面屏幕坐标) -> 世界 Vector3 (Y=0)。</summary>
    internal static class LevelEditorVectorExtensions
    {
        public static Vector3 ToXZ(this Vector2 v) => new(v.X, 0f, v.Y);
    }

    // ─────────────────────────── 撤销/重做系统 ───────────────────────────

    /// <summary>
    /// 撤销/重做事务系统: 支持复合命令 (一次操作包含多个子命令),
    /// 事务式分组 (BeginTransaction/CommitTransaction) 以及命令描述。
    /// </summary>
    public sealed class LevelEditorUndoSystem
    {
        private readonly Stack<IEditorCommand> _undoStack = new();
        private readonly Stack<IEditorCommand> _redoStack = new();

        /// <summary>当前事务 (复合命令)。</summary>
        private CompositeEditorCommand _currentTransaction;
        private int _transactionDepth;
        private const int MaxUndoLevels = 200;

        /// <summary>撤销栈中命令数。</summary>
        public int UndoCount => _undoStack.Count;
        /// <summary>重做栈中命令数。</summary>
        public int RedoCount => _redoStack.Count;
        /// <summary>是否可撤销。</summary>
        public bool CanUndo => _undoStack.Count > 0;
        /// <summary>是否可重做。</summary>
        public bool CanRedo => _redoStack.Count > 0;
        /// <summary>撤销描述。</summary>
        public string UndoDescription => CanUndo ? _undoStack.Peek().Description : string.Empty;
        /// <summary>重做描述。</summary>
        public string RedoDescription => CanRedo ? _redoStack.Peek().Description : string.Empty;
        /// <summary>是否处于事务中。</summary>
        public bool IsInTransaction => _transactionDepth > 0;

        /// <summary>撤销/重做状态变更事件。</summary>
        public event Action OnStateChanged;

        /// <summary>执行命令 (进撤销栈)。</summary>
        public void Execute(IEditorCommand command)
        {
            if (command == null) return;

            if (_currentTransaction != null)
            {
                _currentTransaction.Add(command);
            }
            else
            {
                command.Execute();
                _undoStack.Push(command);
                _redoStack.Clear();
                TrimStack();
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>撤销。</summary>
        public bool Undo()
        {
            if (!CanUndo) return false;
            var cmd = _undoStack.Pop();
            cmd.Undo();
            _redoStack.Push(cmd);
            OnStateChanged?.Invoke();
            return true;
        }

        /// <summary>重做。</summary>
        public bool Redo()
        {
            if (!CanRedo) return false;
            var cmd = _redoStack.Pop();
            cmd.Execute();
            _undoStack.Push(cmd);
            OnStateChanged?.Invoke();
            return true;
        }

        /// <summary>清除撤销/重做栈。</summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _transactionDepth = 0;
            _currentTransaction = null;
            OnStateChanged?.Invoke();
        }

        /// <summary>开始事务 (后续 Execute 将被收集为复合命令)。</summary>
        public void BeginTransaction(string description)
        {
            _transactionDepth++;
            if (_currentTransaction == null)
            {
                _currentTransaction = new CompositeEditorCommand(description);
            }
        }

        /// <summary>提交事务 (将收集的命令作为原子操作执行)。</summary>
        public void CommitTransaction()
        {
            _transactionDepth--;
            if (_transactionDepth <= 0 && _currentTransaction != null)
            {
                _transactionDepth = 0;
                if (_currentTransaction.Count > 0)
                {
                    _currentTransaction.Execute();
                    _undoStack.Push(_currentTransaction);
                    _redoStack.Clear();
                    TrimStack();
                }
                _currentTransaction = null;
                OnStateChanged?.Invoke();
            }
        }

        /// <summary>回滚事务 (丢弃收集的命令)。</summary>
        public void RollbackTransaction()
        {
            _transactionDepth = 0;
            _currentTransaction = null;
        }

        private void TrimStack()
        {
            while (_undoStack.Count > MaxUndoLevels)
            {
                // 移除最底部的命令
                var temp = new Stack<IEditorCommand>();
                while (_undoStack.Count > 1)
                    temp.Push(_undoStack.Pop());
                _undoStack.Clear();
                foreach (var c in temp) _undoStack.Push(c);
            }
        }
    }

    /// <summary>复合编辑器命令: 将多个命令作为原子操作。</summary>
    public sealed class CompositeEditorCommand : IEditorCommand
    {
        private readonly string _description;
        private readonly List<IEditorCommand> _commands = new();

        public string Description => _description;
        public int Count => _commands.Count;

        public CompositeEditorCommand(string description)
        {
            _description = description ?? "Composite";
        }

        public void Add(IEditorCommand cmd)
        {
            if (cmd != null) _commands.Add(cmd);
        }

        public void Execute()
        {
            foreach (var cmd in _commands) cmd.Execute();
        }

        public void Undo()
        {
            for (int i = _commands.Count - 1; i >= 0; i--)
                _commands[i].Undo();
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ─────────────────────────── 工具基类 ───────────────────────────

    /// <summary>
    /// 关卡编辑器工具基类: 定义所有工具的生命周期接口。
    /// </summary>
    public abstract class LevelEditorTool
    {
        /// <summary>工具对应的编辑模式。</summary>
        public abstract LevelEditorMode Mode { get; }
        /// <summary>工具显示名称。</summary>
        public abstract string DisplayName { get; }
        /// <summary>工具是否处于激活状态。</summary>
        public bool IsActive { get; private set; }
        /// <summary>关联的快捷键 (可空)。</summary>
        public virtual string ShortcutKey => null;
        /// <summary>工具提示。</summary>
        public virtual string Tooltip => string.Empty;
        /// <summary>工具图标名称 (用于工具栏图标)。</summary>
        public virtual string IconName => Mode.ToString().ToLower();

        public virtual void OnActivate() { IsActive = true; }
        public virtual void OnDeactivate() { IsActive = false; }
        public virtual void OnMouseEvent(EditorMouseEvent e) { }
        public virtual void OnKeyEvent(EditorKeyEvent e) { }
        public virtual void OnUpdate(float dt) { }
        public virtual void OnRenderOverlay(ToolRenderContext ctx) { }
    }

    // ─────────────────────────── 选择工具 ───────────────────────────

    /// <summary>
    /// 选择工具: 点选/框选/套索/刷选, 配合 Shift/Ctrl 多选/反选。
    /// </summary>
    public sealed class SelectTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Select;
        public override string DisplayName => "Select";
        public override string ShortcutKey => "Q";
        public override string Tooltip => "Q: Select | Shift+Click: Add | Ctrl+A: All";

        public SelectSubMode SubMode { get; set; } = SelectSubMode.Point;
        public Vector2? BoxSelectStart { get; private set; }
        public Vector2? BoxSelectEnd { get; private set; }
        public readonly List<Vector2> LassoPoints = new();
        public readonly List<Vector2> PaintStroke = new();
        public bool IsDragging { get; private set; }
        public float BrushRadius { get; set; } = 30f;

        public event Action<List<Guid>, bool> OnSelectionChanged;
        public event Action<string> OnSelectByType;
        public event Action OnInvertSelection;

        private bool _hasDragged;

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            switch (e.Action)
            {
                case MouseAction.Press when e.Button == EditorMouseButton.Left:
                    BoxSelectStart = e.ScreenPosition;
                    BoxSelectEnd = e.ScreenPosition;
                    IsDragging = true;
                    _hasDragged = false;
                    if (SubMode == SelectSubMode.Lasso) LassoPoints.Clear();
                    if (SubMode == SelectSubMode.Paint) PaintStroke.Clear();
                    break;

                case MouseAction.Move when IsDragging:
                    BoxSelectEnd = e.ScreenPosition;
                    float dragDist = Vector2.Distance(BoxSelectStart ?? Vector2.Zero, e.ScreenPosition);
                    if (dragDist > 3f) _hasDragged = true;
                    if (SubMode == SelectSubMode.Lasso && e.Button == EditorMouseButton.Left)
                        LassoPoints.Add(e.ScreenPosition);
                    if (SubMode == SelectSubMode.Paint)
                        PaintStroke.Add(e.ScreenPosition);
                    break;

                case MouseAction.Release when e.Button == EditorMouseButton.Left && IsDragging:
                    if (!_hasDragged)
                    {
                        OnSelectionChanged?.Invoke(new List<Guid>(), e.Ctrl);
                    }
                    else if (SubMode == SelectSubMode.Box)
                    {
                        OnSelectionChanged?.Invoke(new List<Guid>(), e.Ctrl);
                    }
                    else if (SubMode == SelectSubMode.Lasso && LassoPoints.Count > 2)
                    {
                        OnSelectionChanged?.Invoke(new List<Guid>(), e.Ctrl);
                    }
                    else if (SubMode == SelectSubMode.Paint && PaintStroke.Count > 0)
                    {
                        OnSelectionChanged?.Invoke(new List<Guid>(), e.Ctrl);
                    }
                    IsDragging = false;
                    BoxSelectStart = null;
                    BoxSelectEnd = null;
                    LassoPoints.Clear();
                    PaintStroke.Clear();
                    break;
            }
        }

        public override void OnKeyEvent(EditorKeyEvent e)
        {
            if (e.IsDown && e.KeyCode == 'I' && e.Modifiers == ModifierKeys.Control)
                OnInvertSelection?.Invoke();
        }

        public (Vector2 Min, Vector2 Max)? GetBoxSelectRect()
        {
            if (BoxSelectStart == null || BoxSelectEnd == null) return null;
            return (Vector2.Min(BoxSelectStart.Value, BoxSelectEnd.Value),
                    Vector2.Max(BoxSelectStart.Value, BoxSelectEnd.Value));
        }

        public void RequestSelectByType(string typeName) => OnSelectByType?.Invoke(typeName);

        /// <summary>检测屏幕点是否在刷选笔刷范围内。</summary>
        public bool IsInPaintBrush(Vector2 point)
        {
            if (PaintStroke.Count == 0) return false;
            foreach (var p in PaintStroke)
                if (Vector2.Distance(point, p) <= BrushRadius) return true;
            return false;
        }
    }

    // ─────────────────────────── 变换工具 ───────────────────────────

    /// <summary>
    /// 变换工具: 移动/旋转/缩放, 世界/本地/自定义轴坐标空间, 精确数值输入。
    /// </summary>
    public sealed class TransformTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Move;
        public override string DisplayName => "Transform";
        public override string Tooltip => "W=Move, E=Rotate, R=Scale";

        public TransformSubType SubType { get; set; } = TransformSubType.Move;
        public CoordinateSpace Space { get; set; } = CoordinateSpace.World;
        public Vector3 CustomAxis = Vector3.UnitY;
        public TransformAxis ActiveAxis { get; private set; } = TransformAxis.None;
        public bool IsDragging { get; private set; }

        public Vector3 PreciseTranslation = Vector3.Zero;
        public Vector3 PreciseRotation = Vector3.Zero;
        public Vector3 PreciseScale = Vector3.One;
        public bool UsePreciseInput { get; set; }

        public Vector3 AccumulatedTranslation;
        public Quaternion AccumulatedRotation = Quaternion.Identity;
        public Vector3 AccumulatedScale = Vector3.One;

        public float GizmoScreenSize = 80f;
        public float SnapValue { get; set; } = 1f;
        public bool UseSnapping { get; set; }

        public event Action<TransformSubType, Vector3, Quaternion, Vector3> OnApplyTransform;
        public event Action OnTransformBegin;
        public event Action OnTransformEnd;

        public LevelEditorMode GetActualMode() => SubType switch
        {
            TransformSubType.Rotate => LevelEditorMode.Rotate,
            TransformSubType.Scale => LevelEditorMode.Scale,
            _ => LevelEditorMode.Move
        };

        public override void OnActivate()
        {
            base.OnActivate();
            ResetAccumulated();
        }

        public override void OnDeactivate()
        {
            CommitTransform();
            base.OnDeactivate();
        }

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            if (e.Button != EditorMouseButton.Left) return;
            switch (e.Action)
            {
                case MouseAction.Press:
                    ActiveAxis = TransformAxis.None;
                    IsDragging = true;
                    AccumulatedTranslation = Vector3.Zero;
                    AccumulatedRotation = Quaternion.Identity;
                    AccumulatedScale = Vector3.One;
                    OnTransformBegin?.Invoke();
                    break;

                case MouseAction.Release when IsDragging:
                    CommitTransform();
                    IsDragging = false;
                    ActiveAxis = TransformAxis.None;
                    OnTransformEnd?.Invoke();
                    break;
            }
        }

        public override void OnKeyEvent(EditorKeyEvent e)
        {
            if (!e.IsDown) return;
            switch (e.KeyCode)
            {
                case 'W': SubType = TransformSubType.Move; Space = CoordinateSpace.World; break;
                case 'E': SubType = TransformSubType.Rotate; break;
                case 'R': SubType = TransformSubType.Scale; break;
            }
        }

        public void ApplyPreciseInput()
        {
            if (!UsePreciseInput) return;
            OnApplyTransform?.Invoke(TransformSubType.Move,
                PreciseTranslation,
                Quaternion.CreateFromYawPitchRoll(
                    PreciseRotation.Y * MathF.PI / 180f,
                    PreciseRotation.X * MathF.PI / 180f,
                    PreciseRotation.Z * MathF.PI / 180f),
                PreciseScale);
        }

        public void Accumulate(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            AccumulatedTranslation += translation;
            AccumulatedRotation = rotation * AccumulatedRotation;
            AccumulatedScale *= scale;
        }

        public void ResetAccumulated()
        {
            AccumulatedTranslation = Vector3.Zero;
            AccumulatedRotation = Quaternion.Identity;
            AccumulatedScale = Vector3.One;
        }

        /// <summary>捕捉到网格。</summary>
        public Vector3 SnapToGrid(Vector3 value)
        {
            if (!UseSnapping || SnapValue <= 0f) return value;
            return new Vector3(
                MathF.Round(value.X / SnapValue) * SnapValue,
                MathF.Round(value.Y / SnapValue) * SnapValue,
                MathF.Round(value.Z / SnapValue) * SnapValue);
        }

        private void CommitTransform()
        {
            if (IsDragging || (AccumulatedTranslation == Vector3.Zero &&
                AccumulatedRotation == Quaternion.Identity &&
                AccumulatedScale == Vector3.One)) return;
            OnApplyTransform?.Invoke(SubType, AccumulatedTranslation,
                AccumulatedRotation, AccumulatedScale);
            ResetAccumulated();
        }

        public Matrix4x4 GetSpaceMatrix(Quaternion objectRotation)
        {
            return Space switch
            {
                CoordinateSpace.Local => Matrix4x4.CreateFromQuaternion(objectRotation),
                CoordinateSpace.CustomAxis => Matrix4x4.CreateFromQuaternion(
                    Quaternion.CreateFromAxisAngle(CustomAxis, 0)),
                _ => Matrix4x4.Identity
            };
        }
    }

    // ─────────────────────────── 放置工具 ───────────────────────────

    /// <summary>
    /// 放置工具: 拖放放置/随机旋转/表面对齐/批量散射。
    /// </summary>
    public sealed class PlaceTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Place;
        public override string DisplayName => "Place";
        public override string ShortcutKey => "B";
        public override string Tooltip => "B: Place | Click=Single, Drag=Scatter";

        public string AssetPath { get; set; } = string.Empty;
        public bool RandomRotation { get; set; }
        public float RandomRotationRange { get; set; } = 360f;
        public bool AlignToSurface { get; set; }
        public float SurfaceOffset { get; set; }
        public float ScatterRadius { get; set; } = 5f;
        public float ScatterDensity { get; set; } = 1f;
        public float ScaleVariance { get; set; }
        public bool IsScatterMode { get; set; }
        public int PlacedCount { get; private set; }
        public float SnapToGridSize { get; set; } = 1f;
        public bool SnapEnabled { get; set; }

        public event Action<string, Vector3, Quaternion, Vector3> OnPlaceRequest;
        public event Action<string, Vector3, float, float, float, float> OnScatterRequest;

        private readonly Random _rng = new();

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            if (e.Button != EditorMouseButton.Left) return;

            if (e.Action == MouseAction.Press && !IsScatterMode)
            {
                var position = SnapEnabled
                    ? SnapVector(e.WorldPosition.ToXZ(), SnapToGridSize)
                    : e.WorldPosition.ToXZ();
                var rotation = RandomRotation
                    ? Quaternion.CreateFromAxisAngle(Vector3.UnitY,
                        _rng.NextSingle() * RandomRotationRange * MathF.PI / 180f)
                    : Quaternion.Identity;
                var scale = ScaleVariance > 0
                    ? Vector3.One * (1f + (_rng.NextSingle() - 0.5f) * ScaleVariance * 2f)
                    : Vector3.One;
                OnPlaceRequest?.Invoke(AssetPath, position, rotation, scale);
                PlacedCount++;
            }
            else if (e.Action == MouseAction.Release && IsScatterMode)
            {
                OnScatterRequest?.Invoke(AssetPath, e.WorldPosition.ToXZ(),
                    ScatterRadius, ScatterDensity, RandomRotationRange, ScaleVariance);
            }
        }

        /// <summary>处理外部拖放 (从内容浏览器拖入视口)。</summary>
        public void HandleDragDrop(DragDropEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.AssetPath)) return;
            var position = SnapEnabled
                ? SnapVector(args.WorldPosition, SnapToGridSize)
                : args.WorldPosition;
            var rotation = RandomRotation
                ? Quaternion.CreateFromAxisAngle(Vector3.UnitY,
                    _rng.NextSingle() * RandomRotationRange * MathF.PI / 180f)
                : Quaternion.Identity;
            OnPlaceRequest?.Invoke(args.AssetPath, position, rotation, Vector3.One);
            PlacedCount++;
            args.IsAccepted = true;
        }

        public void ResetCount() => PlacedCount = 0;

        private static Vector3 SnapVector(Vector3 v, float size)
        {
            if (size <= 0f) return v;
            return new Vector3(
                MathF.Round(v.X / size) * size,
                MathF.Round(v.Y / size) * size,
                MathF.Round(v.Z / size) * size);
        }
    }

    // ─────────────────────────── 地形工具 ───────────────────────────

    /// <summary>
    /// 地形工具: 升高/降低/平滑/平坦/侵蚀/噪声。
    /// </summary>
    public sealed class LandscapeTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Landscape;
        public override string DisplayName => "Landscape";
        public override string Tooltip => "Sculpt terrain";

        public LandscapeBrushOp BrushOp { get; set; } = LandscapeBrushOp.Raise;
        public float BrushStrength { get; set; } = 0.5f;
        public float BrushRadius { get; set; } = 10f;
        public float BrushFalloff { get; set; } = 0.5f;
        public BrushShape Shape { get; set; } = BrushShape.Circle;
        public int TargetLayer { get; set; }
        public float FlattenHeight { get; set; }
        public float NoiseScale { get; set; } = 0.1f;
        public float NoiseStrength { get; set; } = 1f;
        public int NoiseOctaves { get; set; } = 4;

        public event Action<LandscapeBrushOp, Vector3, float, float, float, BrushShape> OnBrushApply;

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            if (e.Button == EditorMouseButton.Left &&
                (e.Action == MouseAction.Press || e.Action == MouseAction.Move))
            {
                OnBrushApply?.Invoke(BrushOp, e.WorldPosition.ToXZ(),
                    BrushStrength, BrushRadius, BrushFalloff, Shape);
            }
        }
    }

    // ─────────────────────────── 植被工具 ───────────────────────────

    /// <summary>植被工具: 在地形上散射/删除植被。</summary>
    public sealed class FoliageTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Foliage;
        public override string DisplayName => "Foliage";

        public FoliageBrushOp BrushOp { get; set; } = FoliageBrushOp.Paint;
        public float BrushRadius { get; set; } = 5f;
        public float Density { get; set; } = 2f;
        public float RandomRotationRange { get; set; } = 360f;
        public float RandomScaleMin { get; set; } = 0.8f;
        public float RandomScaleMax { get; set; } = 1.2f;
        public bool SnapToGround { get; set; } = true;
        public float GroundOffset { get; set; }
        public readonly List<string> FoliageTypes = new();
        public int SelectedTypeIndex { get; set; }

        public event Action<string, Vector3, float, float, float, float, bool, float> OnFoliagePaint;
        public event Action<Vector3, float> OnFoliageErase;

        private readonly Random _rng = new();

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            if (e.Button != EditorMouseButton.Left) return;
            if (e.Action == MouseAction.Press || e.Action == MouseAction.Move)
            {
                if (BrushOp == FoliageBrushOp.Paint && FoliageTypes.Count > 0)
                {
                    string type = FoliageTypes[Math.Clamp(SelectedTypeIndex, 0, FoliageTypes.Count - 1)];
                    float rot = _rng.NextSingle() * RandomRotationRange * MathF.PI / 180f;
                    float scl = RandomScaleMin + _rng.NextSingle() * (RandomScaleMax - RandomScaleMin);
                    OnFoliagePaint?.Invoke(type, e.WorldPosition.ToXZ(), BrushRadius, Density,
                        rot, scl, SnapToGround, GroundOffset);
                }
                else if (BrushOp == FoliageBrushOp.Erase)
                {
                    OnFoliageErase?.Invoke(e.WorldPosition.ToXZ(), BrushRadius);
                }
            }
        }
    }

    // ─────────────────────────── 触发器工具 ───────────────────────────

    /// <summary>触发器工具: 创建和编辑触发区域。</summary>
    public sealed class TriggerTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Trigger;
        public override string DisplayName => "Trigger";

        public TriggerShape Shape { get; set; } = TriggerShape.Box;
        public Vector3 BoxSize { get; set; } = new(10f, 5f, 10f);
        public float SphereRadius { get; set; } = 5f;
        public float CapsuleRadius { get; set; } = 2f;
        public float CapsuleHalfHeight { get; set; } = 5f;
        public readonly List<Vector2> PolygonVertices = new();
        public float PolygonHeight { get; set; } = 5f;
        public string NameTemplate { get; set; } = "Trigger";

        public event Action<TriggerShape, Vector3, Vector3, float, float, float, List<Vector2>, float, string> OnTriggerCreate;

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            if (e.Button == EditorMouseButton.Left && e.Action == MouseAction.DoubleClick)
            {
                OnTriggerCreate?.Invoke(Shape, e.WorldPosition.ToXZ(), BoxSize,
                    SphereRadius, CapsuleRadius, CapsuleHalfHeight,
                    PolygonVertices, PolygonHeight, NameTemplate);
            }
        }
    }

    // ─────────────────────────── 体积工具 ───────────────────────────

    /// <summary>体积工具: 创建和编辑各类体积。</summary>
    public sealed class VolumeTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Volume;
        public override string DisplayName => "Volume";

        public VolumeType VolumeKind { get; set; } = VolumeType.PostProcess;
        public Vector3 VolumeSize { get; set; } = new(20f, 20f, 20f);
        public bool UseSphere { get; set; }
        public float SphereRadius { get; set; } = 10f;
        public float Priority { get; set; }

        public event Action<VolumeType, Vector3, Vector3, bool, float, float> OnVolumeCreate;

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            if (e.Button == EditorMouseButton.Left && e.Action == MouseAction.DoubleClick)
            {
                OnVolumeCreate?.Invoke(VolumeKind, e.WorldPosition.ToXZ(), VolumeSize,
                    UseSphere, SphereRadius, Priority);
            }
        }
    }

    // ─────────────────────────── 路径工具 ───────────────────────────

    /// <summary>路径点数据。</summary>
    public struct PathPoint
    {
        public Vector3 Position;
        public float ArriveSpeed;
        public bool IsKeyframe;
        public float CustomData;
    }

    /// <summary>路径工具: 创建和编辑路径。</summary>
    public sealed class PathTool : LevelEditorTool
    {
        public override LevelEditorMode Mode => LevelEditorMode.Path;
        public override string DisplayName => "Path";

        public PathType PathType { get; set; } = PathType.Spline;
        public readonly List<PathPoint> Points = new();
        public bool IsClosed { get; set; }
        public int SplineResolution { get; set; } = 32;
        public float SplineTension { get; set; } = 0.5f;
        public bool ShowConnections { get; set; } = true;
        public bool ShowNormals { get; set; }
        public int SelectedPointIndex { get; set; } = -1;

        public event Action<List<PathPoint>, bool, int> OnPathChanged;
        public event Action<int> OnPointSelected;

        public override void OnMouseEvent(EditorMouseEvent e)
        {
            if (e.Button != EditorMouseButton.Left) return;
            if (e.Action == MouseAction.DoubleClick)
            {
                Points.Add(new PathPoint { Position = e.WorldPosition.ToXZ(), ArriveSpeed = 5f, IsKeyframe = true });
                SelectedPointIndex = Points.Count - 1;
                OnPathChanged?.Invoke(Points, IsClosed, SplineResolution);
                OnPointSelected?.Invoke(SelectedPointIndex);
            }
            else if (e.Action == MouseAction.Press && e.Ctrl)
            {
                if (SelectedPointIndex >= 0 && SelectedPointIndex < Points.Count)
                {
                    Points.RemoveAt(SelectedPointIndex);
                    SelectedPointIndex = -1;
                    OnPathChanged?.Invoke(Points, IsClosed, SplineResolution);
                }
            }
        }

        public void ClearPath()
        {
            Points.Clear();
            SelectedPointIndex = -1;
            OnPathChanged?.Invoke(Points, IsClosed, SplineResolution);
        }

        public void UpdatePoint(int index, Vector3 position)
        {
            if (index < 0 || index >= Points.Count) return;
            Points[index] = Points[index] with { Position = position };
            OnPathChanged?.Invoke(Points, IsClosed, SplineResolution);
        }
    }

    // ─────────────────────────── 场景层级树 ───────────────────────────

    /// <summary>
    /// 层级树节点: 表示场景中一个 Actor/对象, 维护父子关系和 UI 状态。
    /// </summary>
    public sealed class HierarchyNode
    {
        /// <summary>关联的场景对象。</summary>
        public SceneObject SceneObject;
        /// <summary>父节点。</summary>
        public HierarchyNode Parent;
        /// <summary>子节点列表。</summary>
        public readonly List<HierarchyNode> Children = new();
        /// <summary>展开状态。</summary>
        public HierarchyNodeState State = HierarchyNodeState.Collapsed;
        /// <summary>显示名称 (可被搜索过滤)。</summary>
        public string DisplayName => SceneObject?.Name ?? "Null";
        /// <summary>对象 GUID。</summary>
        public Guid Id => SceneObject?.Id ?? Guid.Empty;
        /// <summary>深度 (根节点=0)。</summary>
        public int Depth => Parent?.Depth + 1 ?? 0;
        /// <summary>是否为根节点。</summary>
        public bool IsRoot => Parent == null;
        /// <summary>可见性 (由用户控制, 独立于 SceneObject.IsVisible)。</summary>
        public bool IsVisibleInTree = true;

        /// <summary>添加子节点。</summary>
        public void AddChild(HierarchyNode child)
        {
            if (child == null) return;
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
        }

        /// <summary>移除子节点。</summary>
        public bool RemoveChild(HierarchyNode child)
        {
            if (child == null || !Children.Contains(child)) return false;
            child.Parent = null;
            Children.Remove(child);
            return true;
        }

        /// <summary>递归获取所有后代节点。</summary>
        public List<HierarchyNode> GetDescendants()
        {
            var result = new List<HierarchyNode>();
            foreach (var c in Children)
            {
                result.Add(c);
                result.AddRange(c.GetDescendants());
            }
            return result;
        }

        /// <summary>递归查找节点 (按名称, 忽略大小写)。</summary>
        public HierarchyNode FindByName(string name, bool ignoreCase = true)
        {
            if (SceneObject != null)
            {
                bool match = ignoreCase
                    ? string.Equals(SceneObject.Name, name, StringComparison.OrdinalIgnoreCase)
                    : SceneObject.Name == name;
                if (match) return this;
            }
            foreach (var c in Children)
            {
                var found = c.FindByName(name, ignoreCase);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>递归查找节点 (按 GUID)。</summary>
        public HierarchyNode FindById(Guid id)
        {
            if (Id == id) return this;
            foreach (var c in Children)
            {
                var found = c.FindById(id);
                if (found != null) return found;
            }
            return null;
        }
    }

    /// <summary>
    /// 场景层级树管理器: 维护 Actor 层级结构, 支持搜索/排序/展开/折叠/父子关系操作。
    /// </summary>
    public sealed class HierarchyTreeManager
    {
        private readonly AssetDatabase _assets;
        private readonly LevelEditorUndoSystem _undo;

        /// <summary>根节点列表。</summary>
        private readonly List<HierarchyNode> _roots = new();

        /// <summary>搜索过滤文本。</summary>
        public string SearchFilter { get; set; } = string.Empty;
        /// <summary>排序模式。</summary>
        public HierarchySortMode SortMode { get; set; } = HierarchySortMode.CreationOrder;
        /// <summary>是否显示隐藏对象。</summary>
        public bool ShowHiddenObjects { get; set; } = true;

        /// <summary>树结构变更事件。</summary>
        public event Action OnTreeChanged;
        /// <summary>选择请求事件 (从层级树发起)。</summary>
        public event Action<List<Guid>, bool> OnSelectionRequest;
        /// <summary>重命名请求事件。</summary>
        public event Action<Guid, string> OnRenameRequest;
        /// <summary>展开/折叠事件。</summary>
        public event Action<Guid, bool> OnNodeExpandChanged;

        public IReadOnlyList<HierarchyNode> Roots => _roots;

        public HierarchyTreeManager(AssetDatabase assets, LevelEditorUndoSystem undo)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _undo = undo;
        }

        /// <summary>从场景对象列表重建层级树。</summary>
        public void RebuildFromScene()
        {
            _roots.Clear();
            foreach (var obj in _assets.SceneObjects)
            {
                if (obj == null) continue;
                var node = new HierarchyNode { SceneObject = obj };

                // 查找父对象
                if (!string.IsNullOrEmpty(obj.ParentGuid) &&
                    Guid.TryParse(obj.ParentGuid, out var parentId))
                {
                    var parentNode = FindNodeByIdRecursive(_roots, parentId);
                    if (parentNode != null)
                    {
                        parentNode.AddChild(node);
                        continue;
                    }
                }

                _roots.Add(node);
            }

            ApplySorting();
            OnTreeChanged?.Invoke();
        }

        /// <summary>添加场景对象到树。</summary>
        public void AddObject(SceneObject obj)
        {
            if (obj == null) return;
            var node = new HierarchyNode { SceneObject = obj };
            _roots.Add(node);
            ApplySorting();
            OnTreeChanged?.Invoke();
        }

        /// <summary>从树中移除场景对象。</summary>
        public bool RemoveObject(Guid id)
        {
            var node = FindNodeByIdRecursive(_roots, id);
            if (node == null) return false;
            if (node.Parent != null)
                node.Parent.RemoveChild(node);
            else
                _roots.Remove(node);
            OnTreeChanged?.Invoke();
            return true;
        }

        /// <summary>设置父节点 (重新排序层级)。</summary>
        public bool SetParent(Guid childId, Guid? newParentId)
        {
            var childNode = FindNodeByIdRecursive(_roots, childId);
            if (childNode == null) return false;

            // 防止循环引用
            if (newParentId.HasValue)
            {
                var newParent = FindNodeByIdRecursive(_roots, newParentId.Value);
                if (newParent == null) return false;
                // 检查 newParent 是否是 childNode 的后代
                if (IsDescendantOf(newParent, childNode)) return false;
            }

            if (childNode.Parent != null)
                childNode.Parent.RemoveChild(childNode);
            else
                _roots.Remove(childNode);

            if (newParentId.HasValue)
            {
                var parentNode = FindNodeByIdRecursive(_roots, newParentId.Value);
                parentNode?.AddChild(childNode);
            }
            else
            {
                _roots.Add(childNode);
            }

            OnTreeChanged?.Invoke();
            return true;
        }

        /// <summary>获取扁平化的可见节点列表 (考虑搜索过滤和展开状态)。</summary>
        public List<HierarchyNode> GetVisibleNodes()
        {
            var result = new List<HierarchyNode>();
            foreach (var root in _roots)
                CollectVisibleNodes(root, result);
            return result;
        }

        /// <summary>展开/折叠节点。</summary>
        public void ToggleExpand(Guid id)
        {
            var node = FindNodeByIdRecursive(_roots, id);
            if (node == null) return;
            node.State = node.State == HierarchyNodeState.Collapsed
                ? HierarchyNodeState.Expanded
                : HierarchyNodeState.Collapsed;
            OnNodeExpandChanged?.Invoke(id, node.State == HierarchyNodeState.Expanded);
        }

        /// <summary>展开所有节点。</summary>
        public void ExpandAll()
        {
            foreach (var root in _roots)
                SetExpandRecursive(root, HierarchyNodeState.Expanded);
        }

        /// <summary>折叠所有节点。</summary>
        public void CollapseAll()
        {
            foreach (var root in _roots)
                SetExpandRecursive(root, HierarchyNodeState.Collapsed);
        }

        /// <summary>请求选择节点。</summary>
        public void RequestSelection(Guid id, bool additive)
        {
            OnSelectionRequest?.Invoke(new List<Guid> { id }, additive);
        }

        /// <summary>请求重命名节点。</summary>
        public void RequestRename(Guid id, string newName)
        {
            OnRenameRequest?.Invoke(id, newName);
        }

        // 递归收集可见节点
        private void CollectVisibleNodes(HierarchyNode node, List<HierarchyNode> result)
        {
            if (node == null) return;

            // 搜索过滤
            if (!string.IsNullOrEmpty(SearchFilter))
            {
                bool nameMatch = node.DisplayName.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase);
                bool childMatch = HasMatchingDescendant(node);
                if (!nameMatch && !childMatch)
                {
                    // 强制展开以显示匹配的子节点
                    node.State = HierarchyNodeState.Expanded;
                    foreach (var c in node.Children)
                        CollectVisibleNodes(c, result);
                    return;
                }
                if (childMatch && !nameMatch)
                {
                    node.State = HierarchyNodeState.Expanded;
                    foreach (var c in node.Children)
                        CollectVisibleNodes(c, result);
                    return;
                }
            }

            if (!ShowHiddenObjects && node.SceneObject != null && !node.SceneObject.IsVisible)
                return;

            node.IsVisibleInTree = true;
            result.Add(node);

            if (node.State == HierarchyNodeState.Expanded)
            {
                foreach (var c in node.Children)
                    CollectVisibleNodes(c, result);
            }
        }

        private bool HasMatchingDescendant(HierarchyNode node)
        {
            foreach (var c in node.Children)
            {
                if (c.DisplayName.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase)) return true;
                if (HasMatchingDescendant(c)) return true;
            }
            return false;
        }

        private void SetExpandRecursive(HierarchyNode node, HierarchyNodeState state)
        {
            if (node == null) return;
            node.State = state;
            foreach (var c in node.Children)
                SetExpandRecursive(c, state);
        }

        private bool IsDescendantOf(HierarchyNode node, HierarchyNode ancestor)
        {
            var current = node.Parent;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = current.Parent;
            }
            return false;
        }

        private HierarchyNode FindNodeByIdRecursive(List<HierarchyNode> nodes, Guid id)
        {
            foreach (var n in nodes)
            {
                if (n.Id == id) return n;
                var found = FindNodeByIdRecursive(n.Children, id);
                if (found != null) return found;
            }
            return null;
        }

        private void ApplySorting()
        {
            foreach (var root in _roots)
                SortChildren(root);
            _roots.Sort((a, b) => CompareNodes(a, b));
        }

        private void SortChildren(HierarchyNode node)
        {
            if (node == null) return;
            node.Children.Sort((a, b) => CompareNodes(a, b));
            foreach (var c in node.Children)
                SortChildren(c);
        }

        private int CompareNodes(HierarchyNode a, HierarchyNode b)
        {
            return SortMode switch
            {
                HierarchySortMode.Alphabetical => string.Compare(
                    a.DisplayName, b.DisplayName, StringComparison.Ordinal),
                HierarchySortMode.Type => string.Compare(
                    a.SceneObject?.MeshAssetPath ?? "",
                    b.SceneObject?.MeshAssetPath ?? "", StringComparison.Ordinal),
                HierarchySortMode.Layer => (a.SceneObject?.Layer ?? 0).CompareTo(
                    b.SceneObject?.Layer ?? 0),
                _ => 0
            };
        }
    }

    // ─────────────────────────── 拖放管理器 ───────────────────────────

    /// <summary>
    /// 拖放管理器: 处理从外部 (内容浏览器/文件系统) 拖入资产到编辑器视口。
    /// 支持网格/模型/材质/预制体/纹理等资产类型。
    /// </summary>
    public sealed class DragDropManager
    {
        private readonly AssetDatabase _assets;
        private readonly LevelEditorUndoSystem _undo;

        /// <summary>是否正在拖拽。</summary>
        public bool IsDragging { get; private set; }
        /// <summary>当前拖拽的资产路径。</summary>
        public string CurrentDragAssetPath { get; private set; }
        /// <summary>当前拖拽的资产类型。</summary>
        public string CurrentDragAssetType { get; private set; }
        /// <summary>放置偏移 (每次粘贴递增)。</summary>
        private Vector3 _pasteOffset = new(0, 0, 0);
        private int _pasteCount;

        /// <summary>拖拽开始事件。</summary>
        public event Action<string, string> OnDragBegin;
        /// <summary>拖拽结束事件。</summary>
        public event Action OnDragEnd;
        /// <summary>资产放置事件。</summary>
        public event Action<string, Vector3, Quaternion, Vector3> OnAssetDropped;

        /// <summary>拖拽悬停位置 (世界坐标)。</summary>
        public Vector3 HoverWorldPosition { get; set; }
        /// <summary>拖拽悬停位置 (屏幕坐标)。</summary>
        public Vector2 HoverScreenPosition { get; set; }

        /// <summary>支持的拖放资产类型。</summary>
        public static readonly HashSet<string> SupportedAssetTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "mesh", "model", "material", "texture", "prefab", "scene", "audio", "particle"
        };

        public DragDropManager(AssetDatabase assets, LevelEditorUndoSystem undo)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _undo = undo;
        }

        /// <summary>开始拖拽。</summary>
        public void BeginDrag(string assetPath, string assetType)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            IsDragging = true;
            CurrentDragAssetPath = assetPath;
            CurrentDragAssetType = assetType ?? "unknown";
            _pasteCount = 0;
            OnDragBegin?.Invoke(assetPath, CurrentDragAssetType);
        }

        /// <summary>更新拖拽位置。</summary>
        public void UpdateDragPosition(Vector2 screenPos, Vector3 worldPos)
        {
            HoverScreenPosition = screenPos;
            HoverWorldPosition = worldPos;
        }

        /// <summary>处理放置。</summary>
        public bool HandleDrop(Vector3 worldPosition, Func<string, Vector3, Vector2, Ray> screenToWorldRay = null)
        {
            if (!IsDragging || string.IsNullOrWhiteSpace(CurrentDragAssetPath)) return false;

            // 计算放置位置: 射线与地面 (y=0) 求交
            Vector3 placePos = worldPosition;
            if (screenToWorldRay != null)
            {
                var ray = screenToWorldRay(CurrentDragAssetPath, worldPosition, HoverScreenPosition);
                if (MathF.Abs(ray.Direction.Y) > 1e-4f)
                {
                    float t = -ray.Origin.Y / ray.Direction.Y;
                    if (t > 0) placePos = ray.Origin + ray.Direction * t;
                }
            }

            // 递增偏移以避免重叠
            Vector3 offset = _pasteOffset * _pasteCount;
            placePos += offset;
            _pasteCount++;

            var obj = _assets.CreateObject(
                Path.GetFileNameWithoutExtension(CurrentDragAssetPath),
                CurrentDragAssetType,
                placePos);

            OnAssetDropped?.Invoke(CurrentDragAssetPath, placePos,
                obj?.Rotation ?? Quaternion.Identity, obj?.Scale ?? Vector3.One);

            return true;
        }

        /// <summary>结束拖拽。</summary>
        public void EndDrag()
        {
            IsDragging = false;
            CurrentDragAssetPath = null;
            CurrentDragAssetType = null;
            _pasteCount = 0;
            OnDragEnd?.Invoke();
        }

        /// <summary>检查资产类型是否支持拖放。</summary>
        public static bool IsSupportedAssetType(string type)
        {
            return !string.IsNullOrEmpty(type) && SupportedAssetTypes.Contains(type);
        }
    }

    // ─────────────────────────── PIE 状态机 ───────────────────────────

    /// <summary>
    /// Play-In-Editor (PIE) 状态机: 管理编辑/运行/暂停/恢复之间的转换。
    /// 进入 Playing 前快照编辑器场景; 退出时恢复。
    /// </summary>
    public sealed class LevelEditorPieStateMachine
    {
        public PieState State { get; private set; } = PieState.Editing;
        public event Action<PieState, PieState> OnStateChanged;

        private string _snapshot;
        private readonly AssetDatabase _assets;
        private readonly EntityComponentSystem _ecs;

        /// <summary>PIE 运行时长 (秒)。</summary>
        public float PlayTime { get; private set; }
        /// <summary>PIE 帧计数。</summary>
        public ulong FrameCount { get; private set; }

        /// <summary>运行前场景对象数量 (用于验证恢复)。</summary>
        private int _objectCountBeforePlay;

        public LevelEditorPieStateMachine(AssetDatabase assets, EntityComponentSystem ecs)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _ecs = ecs;
        }

        public bool IsPlaying => State == PieState.Playing;
        public bool IsPaused => State == PieState.Paused;
        public bool IsEditing => State == PieState.Editing;

        /// <summary>开始运行。</summary>
        public void Play()
        {
            if (State != PieState.Editing) return;
            _snapshot = SerializeSnapshot();
            _objectCountBeforePlay = _assets.SceneObjects.Count;
            PlayTime = 0f;
            FrameCount = 0;
            TransitionTo(PieState.Playing);
        }

        /// <summary>暂停。</summary>
        public void Pause()
        {
            if (State != PieState.Playing) return;
            TransitionTo(PieState.Paused);
        }

        /// <summary>恢复运行。</summary>
        public void Resume()
        {
            if (State != PieState.Paused) return;
            TransitionTo(PieState.Playing);
        }

        /// <summary>单步一帧。</summary>
        public void StepFrame()
        {
            if (State != PieState.Paused) return;
            FrameCount++;
            // 真实实现: 通知游戏循环执行一帧
        }

        /// <summary>停止运行, 恢复编辑器场景。</summary>
        public void Stop()
        {
            if (State == PieState.Editing) return;
            TransitionTo(PieState.Restoring);
            RestoreSnapshot();
            TransitionTo(PieState.Editing);
        }

        /// <summary>每帧更新 (仅在 Playing 状态)。</summary>
        public void Update(float dt)
        {
            if (State == PieState.Playing)
            {
                PlayTime += dt;
                FrameCount++;
            }
        }

        private void TransitionTo(PieState next)
        {
            var prev = State;
            State = next;
            OnStateChanged?.Invoke(prev, next);
        }

        private string SerializeSnapshot()
        {
            var data = _assets.SceneObjects.Select(o => new SnapshotDto
            {
                Id = o.Id,
                Name = o.Name,
                Pos = new[] { o.Position.X, o.Position.Y, o.Position.Z },
                Rot = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W },
                Scl = new[] { o.Scale.X, o.Scale.Y, o.Scale.Z },
                MeshAssetPath = o.MeshAssetPath,
                BaseColor = new[] { o.BaseColor.X, o.BaseColor.Y, o.BaseColor.Z },
                Metallic = o.Metallic,
                Roughness = o.Roughness,
                Layer = o.Layer,
                IsVisible = o.IsVisible
            });
            return JsonSerializer.Serialize(data);
        }

        private sealed class SnapshotDto
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
            public float[] Pos { get; set; }
            public float[] Rot { get; set; }
            public float[] Scl { get; set; }
            public string MeshAssetPath { get; set; }
            public float[] BaseColor { get; set; }
            public float Metallic { get; set; }
            public float Roughness { get; set; }
            public int Layer { get; set; }
            public bool IsVisible { get; set; }
        }

        private void RestoreSnapshot()
        {
            if (string.IsNullOrEmpty(_snapshot)) return;
            try
            {
                _assets.ClearScene();
                var dtos = JsonSerializer.Deserialize<List<SnapshotDto>>(_snapshot);
                if (dtos != null)
                {
                    foreach (var d in dtos)
                    {
                        var obj = new SceneObject
                        {
                            Id = d.Id,
                            Name = d.Name ?? "Object",
                            Position = FromArray(d.Pos, Vector3.Zero),
                            Rotation = d.Rot != null && d.Rot.Length >= 4
                                ? new Quaternion(d.Rot[0], d.Rot[1], d.Rot[2], d.Rot[3])
                                : Quaternion.Identity,
                            Scale = FromArray(d.Scl, Vector3.One),
                            MeshAssetPath = d.MeshAssetPath,
                            BaseColor = FromArray(d.BaseColor, Vector3.One),
                            Metallic = d.Metallic,
                            Roughness = d.Roughness,
                            Layer = d.Layer,
                            IsVisible = d.IsVisible
                        };
                        _assets.AddObject(obj);
                    }
                }
                _ecs?.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PIE snapshot restore failed: {ex.Message}");
            }
            _snapshot = null;
        }

        private static Vector3 FromArray(float[] arr, Vector3 fallback) =>
            arr != null && arr.Length >= 3 ? new Vector3(arr[0], arr[1], arr[2]) : fallback;
    }

    // ─────────────────────────── 编辑器偏好设置 ───────────────────────────

    /// <summary>
    /// 编辑器偏好设置: 持久化的用户配置, 包括网格/捕捉/快捷键/视口/PIE 等。
    /// 支持 JSON 序列化/反序列化到磁盘。
    /// </summary>
    public sealed class EditorPreferences
    {
        // ── 网格设置 ──
        public float GridSize { get; set; } = 1f;
        public int GridHalfExtent { get; set; } = 50;
        public bool ShowGrid { get; set; } = true;
        public bool ShowWorldAxes { get; set; } = true;
        public bool SnapToGrid { get; set; } = false;
        public float SnapValue { get; set; } = 1f;
        public float SnapAngleValue { get; set; } = 15f;
        public float SnapScaleValue { get; set; } = 0.1f;

        // ── 视口设置 ──
        public float FieldOfView { get; set; } = 60f;
        public float NearClip { get; set; } = 0.1f;
        public float FarClip { get; set; } = 2000f;
        public float CameraMoveSpeed { get; set; } = 8f;
        public float CameraBoostMultiplier { get; set; } = 3f;
        public float CameraLookSensitivity { get; set; } = 0.25f;

        // ── 选择设置 ──
        public bool HighlightSelected { get; set; } = true;
        public float OutlineWidth { get; set; } = 2f;
        public bool ShowSelectionBounds { get; set; } = true;
        public bool ShowGizmo { get; set; } = true;
        public float GizmoSize { get; set; } = 80f;

        // ── PIE 设置 ──
        public bool AutoSaveBeforePlay { get; set; } = true;
        public float MaxPlayTime { get; set; } = 300f; // 5分钟
        public bool ShowPIEOverlay { get; set; } = true;

        // ── 自动保存 ──
        public float AutoSaveInterval { get; set; } = 300f;
        public int MaxAutoSaveBackups { get; set; } = 10;

        // ── UI 设置 ──
        public string ThemeName { get; set; } = "Dark";
        public float UILayerOpacity { get; set; } = 0.92f;
        public float OutlinerSplitRatio { get; set; } = 0.25f;
        public float InspectorSplitRatio { get; set; } = 0.30f;

        // ── 快捷键映射 (键名 -> 动作名) ──
        public Dictionary<string, string> KeyBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Q"] = "Tool.Select",
            ["W"] = "Tool.Move",
            ["E"] = "Tool.Rotate",
            ["R"] = "Tool.Scale",
            ["B"] = "Tool.Place",
            ["Delete"] = "Edit.Delete",
            ["Ctrl+D"] = "Edit.Duplicate",
            ["Ctrl+C"] = "Edit.Copy",
            ["Ctrl+V"] = "Edit.Paste",
            ["Ctrl+X"] = "Edit.Cut",
            ["Ctrl+Z"] = "Edit.Undo",
            ["Ctrl+Y"] = "Edit.Redo",
            ["Ctrl+A"] = "Edit.SelectAll",
            ["Ctrl+S"] = "File.Save",
            ["Ctrl+Shift+S"] = "File.SaveAs",
            ["F"] = "View.FrameSelection",
            ["Ctrl+P"] = "PIE.Play",
            ["Ctrl+Shift+P"] = "PIE.Stop"
        };

        // ── 路径 ──
        public string PreferencesFilePath { get; set; } = "EditorPreferences.json";

        /// <summary>偏好设置变更事件。</summary>
        public event Action OnPreferencesChanged;

        /// <summary>应用快捷键绑定到命令注册表。</summary>
        public void ApplyKeyBindings(CommandRegistry registry, EditorContext ctx)
        {
            // 可扩展: 将 KeyBindings 注册到 CommandRegistry
        }

        /// <summary>保存到 JSON 文件。</summary>
        public bool SaveToFile(string path = null)
        {
            try
            {
                path ??= PreferencesFilePath;
                var opts = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, opts);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save preferences failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>从 JSON 文件加载。</summary>
        public bool LoadFromFile(string path = null)
        {
            try
            {
                path ??= PreferencesFilePath;
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path);
                var prefs = JsonSerializer.Deserialize<EditorPreferences>(json);
                if (prefs == null) return false;

                // 逐属性复制
                GridSize = prefs.GridSize;
                GridHalfExtent = prefs.GridHalfExtent;
                ShowGrid = prefs.ShowGrid;
                ShowWorldAxes = prefs.ShowWorldAxes;
                SnapToGrid = prefs.SnapToGrid;
                SnapValue = prefs.SnapValue;
                SnapAngleValue = prefs.SnapAngleValue;
                SnapScaleValue = prefs.SnapScaleValue;
                FieldOfView = prefs.FieldOfView;
                NearClip = prefs.NearClip;
                FarClip = prefs.FarClip;
                CameraMoveSpeed = prefs.CameraMoveSpeed;
                CameraBoostMultiplier = prefs.CameraBoostMultiplier;
                CameraLookSensitivity = prefs.CameraLookSensitivity;
                HighlightSelected = prefs.HighlightSelected;
                OutlineWidth = prefs.OutlineWidth;
                ShowSelectionBounds = prefs.ShowSelectionBounds;
                ShowGizmo = prefs.ShowGizmo;
                GizmoSize = prefs.GizmoSize;
                AutoSaveBeforePlay = prefs.AutoSaveBeforePlay;
                MaxPlayTime = prefs.MaxPlayTime;
                ShowPIEOverlay = prefs.ShowPIEOverlay;
                AutoSaveInterval = prefs.AutoSaveInterval;
                MaxAutoSaveBackups = prefs.MaxAutoSaveBackups;
                ThemeName = prefs.ThemeName;
                UILayerOpacity = prefs.UILayerOpacity;
                OutlinerSplitRatio = prefs.OutlinerSplitRatio;
                InspectorSplitRatio = prefs.InspectorSplitRatio;
                if (prefs.KeyBindings != null)
                    KeyBindings = prefs.KeyBindings;
                PreferencesFilePath = prefs.PreferencesFilePath;

                OnPreferencesChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Load preferences failed: {ex.Message}");
                return false;
            }
        }
    }

    // ─────────────────────────── 剪贴板 ───────────────────────────

    /// <summary>
    /// 编辑器剪贴板: 支持复制/剪切/粘贴/实例化, 维护内部剪贴板数据。
    /// </summary>
    public sealed class LevelEditorClipboard
    {
        private readonly AssetDatabase _assets;
        private readonly LevelEditorUndoSystem _undo;

        private readonly List<SceneObject> _clipboard = new();
        private Vector3 _pasteOffset = new(1, 0, 1);
        private int _pasteCount;

        /// <summary>剪贴板是否有内容。</summary>
        public bool HasContent => _clipboard.Count > 0;
        /// <summary>剪贴板中的对象数量。</summary>
        public int Count => _clipboard.Count;

        /// <summary>剪贴板变更事件。</summary>
        public event Action OnClipboardChanged;

        public LevelEditorClipboard(AssetDatabase assets, LevelEditorUndoSystem undo)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _undo = undo;
        }

        /// <summary>复制。</summary>
        public void Copy(IEnumerable<SceneObject> selection)
        {
            _clipboard.Clear();
            _pasteCount = 0;
            foreach (var o in selection)
            {
                if (o == null) continue;
                _clipboard.Add(CloneObject(o));
            }
            OnClipboardChanged?.Invoke();
        }

        /// <summary>剪切。</summary>
        public void Cut(IEnumerable<SceneObject> selection)
        {
            Copy(selection);
            _pasteCount = 0;
            foreach (var o in selection.ToList())
            {
                var cmd = new DeleteObjectCommand(_assets, o);
                _undo.Execute(cmd);
            }
        }

        /// <summary>粘贴。</summary>
        public List<SceneObject> Paste(Vector3? position = null)
        {
            var pasted = new List<SceneObject>();
            Vector3 offset = _pasteOffset * _pasteCount;
            _pasteCount++;

            foreach (var o in _clipboard)
            {
                var pos = (position ?? o.Position) + offset;
                var cmd = new CreateObjectCommand(_assets, o.Name, o.MeshAssetPath, pos);
                _undo.Execute(cmd);
                // 获取刚创建的对象并同步属性
                var created = _assets.SceneObjects.LastOrDefault();
                if (created != null)
                {
                    created.Rotation = o.Rotation;
                    created.Scale = o.Scale;
                    created.BaseColor = o.BaseColor;
                    created.Metallic = o.Metallic;
                    created.Roughness = o.Roughness;
                    created.Layer = o.Layer;
                    pasted.Add(created);
                }
            }
            return pasted;
        }

        /// <summary>清空剪贴板。</summary>
        public void Clear()
        {
            _clipboard.Clear();
            _pasteCount = 0;
            OnClipboardChanged?.Invoke();
        }

        private static SceneObject CloneObject(SceneObject source)
        {
            return new SceneObject
            {
                Name = source.Name,
                Position = source.Position,
                Rotation = source.Rotation,
                Scale = source.Scale,
                LocalPosition = source.LocalPosition,
                LocalRotation = source.LocalRotation,
                LocalScale = source.LocalScale,
                MeshAssetPath = source.MeshAssetPath,
                MaterialAssetPath = source.MaterialAssetPath,
                BaseColor = source.BaseColor,
                Metallic = source.Metallic,
                Roughness = source.Roughness,
                CastShadow = source.CastShadow,
                ReceiveShadow = source.ReceiveShadow,
                Bounds = source.Bounds,
                Layer = source.Layer,
                IsVisible = source.IsVisible,
                PrefabSourceGuid = source.PrefabSourceGuid
            };
        }
    }

    // ─────────────────────────── 工具栏管理器 ───────────────────────────

    /// <summary>
    /// 工具栏管理器: 管理当前活动工具、工具切换、快捷键绑定。
    /// </summary>
    public sealed class ToolbarManager
    {
        private readonly Dictionary<LevelEditorMode, LevelEditorTool> _tools = new();
        private readonly Dictionary<string, LevelEditorMode> _shortcutMap = new();

        public LevelEditorTool CurrentTool { get; private set; }
        public LevelEditorMode CurrentMode { get; private set; } = LevelEditorMode.Select;

        public event Action<LevelEditorMode, LevelEditorMode> OnToolChanged;

        public void RegisterTool(LevelEditorTool tool)
        {
            if (tool == null) return;
            _tools[tool.Mode] = tool;
            if (!string.IsNullOrEmpty(tool.ShortcutKey))
                _shortcutMap[tool.ShortcutKey.ToLower()] = tool.Mode;
        }

        public bool SwitchTo(LevelEditorMode mode)
        {
            if (!_tools.TryGetValue(mode, out var newTool)) return false;
            if (CurrentTool == newTool) return true;

            var prevMode = CurrentMode;
            CurrentTool?.OnDeactivate();
            CurrentMode = mode;
            CurrentTool = newTool;
            CurrentTool.OnActivate();
            OnToolChanged?.Invoke(prevMode, mode);
            return true;
        }

        public bool SwitchByShortcut(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (_shortcutMap.TryGetValue(key.ToLower(), out var mode))
                return SwitchTo(mode);
            return false;
        }

        public void HandleKeyEvent(EditorKeyEvent e)
        {
            if (!e.IsDown) return;
            string key = ((char)e.KeyCode).ToString().ToLower();
            if (e.Modifiers == ModifierKeys.None)
                SwitchByShortcut(key);
            CurrentTool?.OnKeyEvent(e);
        }

        public void HandleMouseEvent(EditorMouseEvent e)
        {
            CurrentTool?.OnMouseEvent(e);
        }

        public void Update(float dt)
        {
            CurrentTool?.OnUpdate(dt);
        }

        public void RenderOverlay(ToolRenderContext ctx)
        {
            CurrentTool?.OnRenderOverlay(ctx);
        }

        public T GetTool<T>(LevelEditorMode mode) where T : LevelEditorTool
        {
            if (_tools.TryGetValue(mode, out var tool) && tool is T typed)
                return typed;
            return null;
        }

        public IReadOnlyDictionary<LevelEditorMode, LevelEditorTool> GetAllTools() => _tools;

        /// <summary>使用默认工具集初始化。</summary>
        public void InitializeDefaults()
        {
            RegisterTool(new SelectTool());
            RegisterTool(new TransformTool());
            RegisterTool(new PlaceTool());
            RegisterTool(new LandscapeTool());
            RegisterTool(new FoliageTool());
            RegisterTool(new TriggerTool());
            RegisterTool(new VolumeTool());
            RegisterTool(new PathTool());
            SwitchTo(LevelEditorMode.Select);
        }
    }

    // ─────────────────────────── 关卡编辑器核心 (总装) ───────────────────────────

    /// <summary>
    /// 关卡编辑器核心: 顶层控制器, 聚合所有子系统:
    ///   工具栏 / 撤销重做 / 剪贴板 / 层级树 / 拖放 / PIE / 偏好设置
    /// 由宿主程序每帧调用 Update/Render, 接收输入事件。
    /// </summary>
    public sealed class LevelEditorCore : IDisposable
    {
        // ── 外部依赖 ──
        private readonly AssetDatabase _assets;
        private readonly EntityComponentSystem _ecs;

        // ── 子系统 ──
        public readonly LevelEditorUndoSystem UndoSystem = new();
        public readonly LevelEditorClipboard Clipboard;
        public readonly HierarchyTreeManager Hierarchy;
        public readonly DragDropManager DragDrop;
        public readonly LevelEditorPieStateMachine Pie;
        public readonly EditorPreferences Preferences = new();
        public readonly ToolbarManager Toolbar = new();

        /// <summary>当前编辑器模式。</summary>
        public LevelEditorMode CurrentMode => Toolbar.CurrentMode;
        /// <summary>当前活动工具。</summary>
        public LevelEditorTool CurrentTool => Toolbar.CurrentTool;

        /// <summary>当前场景名称。</summary>
        public string CurrentSceneName { get; set; } = "Untitled";
        /// <summary>当前场景文件路径。</summary>
        public string CurrentScenePath { get; set; } = "";
        /// <summary>场景是否被修改。</summary>
        public bool IsDirty { get; private set; }

        /// <summary>模式变更事件。</summary>
        public event Action<LevelEditorMode, LevelEditorMode> OnModeChanged;
        /// <summary>场景脏标记变更事件。</summary>
        public event Action<bool> OnDirtyChanged;
        /// <summary>PIE 状态变更事件。</summary>
        public event Action<PieState, PieState> OnPieStateChanged;

        private bool _disposed;

        public LevelEditorCore(AssetDatabase assets, EntityComponentSystem ecs)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));

            Clipboard = new LevelEditorClipboard(assets, UndoSystem);
            Hierarchy = new HierarchyTreeManager(assets, UndoSystem);
            DragDrop = new DragDropManager(assets, UndoSystem);
            Pie = new LevelEditorPieStateMachine(assets, ecs);

            // 初始化默认工具
            Toolbar.InitializeDefaults();

            // 工具切换时通知
            Toolbar.OnToolChanged += (prev, next) =>
            {
                OnModeChanged?.Invoke(prev, next);
            };

            // PIE 状态变更时转发
            Pie.OnStateChanged += (prev, next) =>
            {
                if (next == PieState.Editing)
                {
                    _ecs?.Clear();
                    Hierarchy.RebuildFromScene();
                }
                OnPieStateChanged?.Invoke(prev, next);
            };
        }

        // ──────────────────────────────────────────────────────────────
        // 生命周期
        // ──────────────────────────────────────────────────────────────

        /// <summary>每帧更新。</summary>
        public void Update(float dt)
        {
            Pie.Update(dt);
            Toolbar.Update(dt);
        }

        /// <summary>处理鼠标事件。</summary>
        public void HandleMouseEvent(EditorMouseEvent e)
        {
            if (Pie.IsPlaying) return; // PIE 运行时不处理编辑器输入
            Toolbar.HandleMouseEvent(e);
        }

        /// <summary>处理键盘事件 (含全局快捷键)。</summary>
        public void HandleKeyEvent(EditorKeyEvent e)
        {
            if (Pie.IsPlaying)
            {
                // PIE 模式下的快捷键
                HandlePieShortcuts(e);
                return;
            }

            // 全局编辑器快捷键
            if (e.IsDown)
            {
                switch (e.KeyCode)
                {
                    case 'Z' when e.Modifiers == ModifierKeys.Control:
                        if (e.Modifiers == ModifierKeys.Control) Undo();
                        return;
                    case 'Y' when e.Modifiers == ModifierKeys.Control:
                        Redo();
                        return;
                    case 'A' when e.Modifiers == ModifierKeys.Control:
                        SelectAll();
                        return;
                    case 'C' when e.Modifiers == ModifierKeys.Control:
                        Copy();
                        return;
                    case 'V' when e.Modifiers == ModifierKeys.Control:
                        Paste();
                        return;
                    case 'X' when e.Modifiers == ModifierKeys.Control:
                        Cut();
                        return;
                    case 'D' when e.Modifiers == ModifierKeys.Control:
                        Duplicate();
                        return;
                    case 'S' when e.Modifiers == ModifierKeys.Control:
                        SaveScene();
                        return;
                    case (int)('P') when e.Modifiers == ModifierKeys.Control:
                        TogglePie();
                        return;
                    case 127: // Delete
                        Delete();
                        return;
                }
            }

            Toolbar.HandleKeyEvent(e);
        }

        private void HandlePieShortcuts(EditorKeyEvent e)
        {
            if (!e.IsDown) return;
            if (e.KeyCode == 'P' && e.Modifiers == ModifierKeys.Control)
                Pie.Stop();
            else if (e.KeyCode == (int)('P') && e.Modifiers == ModifierKeys.Control)
                Pie.Pause();
        }

        // ──────────────────────────────────────────────────────────────
        // 撤销/重做
        // ──────────────────────────────────────────────────────────────

        /// <summary>执行命令。</summary>
        public void ExecuteCommand(IEditorCommand cmd)
        {
            UndoSystem.Execute(cmd);
            MarkDirty();
        }

        /// <summary>开始事务。</summary>
        public void BeginTransaction(string description)
        {
            UndoSystem.BeginTransaction(description);
        }

        /// <summary>提交事务。</summary>
        public void CommitTransaction()
        {
            UndoSystem.CommitTransaction();
            MarkDirty();
        }

        /// <summary>回滚事务。</summary>
        public void RollbackTransaction()
        {
            UndoSystem.RollbackTransaction();
        }

        /// <summary>撤销。</summary>
        public bool Undo()
        {
            if (!UndoSystem.Undo()) return false;
            MarkDirty();
            return true;
        }

        /// <summary>重做。</summary>
        public bool Redo()
        {
            if (!UndoSystem.Redo()) return false;
            MarkDirty();
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        // 复制/粘贴/删除
        // ──────────────────────────────────────────────────────────────

        /// <summary>复制选中对象。</summary>
        public void Copy()
        {
            var selected = _assets.SceneObjects.Where(o => o.IsSelected).ToList();
            Clipboard.Copy(selected);
        }

        /// <summary>剪切选中对象。</summary>
        public void Cut()
        {
            var selected = _assets.SceneObjects.Where(o => o.IsSelected).ToList();
            Clipboard.Cut(selected);
            MarkDirty();
        }

        /// <summary>粘贴。</summary>
        public void Paste()
        {
            if (!Clipboard.HasContent) return;
            var pasted = Clipboard.Paste();
            if (pasted.Count > 0)
            {
                foreach (var o in _assets.SceneObjects) o.IsSelected = false;
                foreach (var p in pasted) p.IsSelected = true;
                MarkDirty();
            }
        }

        /// <summary>复制 (Duplicate) 选中对象。</summary>
        public void Duplicate()
        {
            var selected = _assets.SceneObjects.Where(o => o.IsSelected).ToList();
            if (selected.Count == 0) return;

            UndoSystem.BeginTransaction("Duplicate");
            foreach (var o in selected)
            {
                var dup = _assets.DuplicateObject(o);
                if (dup != null)
                {
                    dup.Position += new Vector3(1, 0, 1);
                    dup.IsSelected = true;
                    o.IsSelected = false;
                }
            }
            UndoSystem.CommitTransaction();
            MarkDirty();
        }

        /// <summary>删除选中对象。</summary>
        public void Delete()
        {
            var selected = _assets.SceneObjects.Where(o => o.IsSelected).ToList();
            if (selected.Count == 0) return;

            UndoSystem.BeginTransaction("Delete");
            foreach (var o in selected)
                UndoSystem.Execute(new DeleteObjectCommand(_assets, o));
            UndoSystem.CommitTransaction();
            MarkDirty();
        }

        /// <summary>全选。</summary>
        public void SelectAll()
        {
            foreach (var o in _assets.SceneObjects) o.IsSelected = true;
        }

        // ──────────────────────────────────────────────────────────────
        // 场景操作
        // ──────────────────────────────────────────────────────────────

        /// <summary>新建场景。</summary>
        public void NewScene(string name = "Untitled")
        {
            _assets.ClearScene();
            UndoSystem.Clear();
            CurrentSceneName = name;
            CurrentScenePath = "";
            IsDirty = false;
            Hierarchy.RebuildFromScene();
            OnDirtyChanged?.Invoke(false);
        }

        /// <summary>加载场景。</summary>
        public bool LoadScene(string path)
        {
            if (!_assets.LoadScene(path)) return false;
            UndoSystem.Clear();
            CurrentScenePath = path;
            CurrentSceneName = Path.GetFileNameWithoutExtension(path);
            IsDirty = false;
            Hierarchy.RebuildFromScene();
            OnDirtyChanged?.Invoke(false);
            return true;
        }

        /// <summary>保存场景。</summary>
        public bool SaveScene()
        {
            if (string.IsNullOrEmpty(CurrentScenePath))
                return SaveSceneAs(CurrentSceneName + ".yamaasset");
            if (_assets.SaveScene(CurrentScenePath, CurrentSceneName))
            {
                IsDirty = false;
                OnDirtyChanged?.Invoke(false);
                return true;
            }
            return false;
        }

        /// <summary>另存为。</summary>
        public bool SaveSceneAs(string path)
        {
            if (_assets.SaveScene(path, CurrentSceneName))
            {
                CurrentScenePath = path;
                CurrentSceneName = Path.GetFileNameWithoutExtension(path);
                IsDirty = false;
                OnDirtyChanged?.Invoke(false);
                return true;
            }
            return false;
        }

        /// <summary>创建对象。</summary>
        public SceneObject CreateObject(string name, string meshType, Vector3 position)
        {
            var cmd = new CreateObjectCommand(_assets, name, meshType, position);
            UndoSystem.Execute(cmd);
            MarkDirty();
            Hierarchy.RebuildFromScene();
            return _assets.SceneObjects.LastOrDefault();
        }

        // ──────────────────────────────────────────────────────────────
        // PIE
        // ──────────────────────────────────────────────────────────────

        /// <summary>切换 PIE 模式。</summary>
        public void TogglePie()
        {
            if (Pie.IsEditing) Pie.Play();
            else Pie.Stop();
        }

        // ──────────────────────────────────────────────────────────────
        // 偏好设置
        // ──────────────────────────────────────────────────────────────

        /// <summary>加载偏好设置。</summary>
        public void LoadPreferences() => Preferences.LoadFromFile();

        /// <summary>保存偏好设置。</summary>
        public void SavePreferences() => Preferences.SaveToFile();

        // ──────────────────────────────────────────────────────────────
        // 拖放
        // ──────────────────────────────────────────────────────────────

        /// <summary>处理拖放放置。</summary>
        public bool HandleDragDrop(string assetPath, Vector3 worldPosition)
        {
            return DragDrop.HandleDrop(worldPosition);
        }

        // ──────────────────────────────────────────────────────────────
        // 内部辅助
        // ──────────────────────────────────────────────────────────────

        private void MarkDirty()
        {
            if (!IsDirty)
            {
                IsDirty = true;
                OnDirtyChanged?.Invoke(true);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
