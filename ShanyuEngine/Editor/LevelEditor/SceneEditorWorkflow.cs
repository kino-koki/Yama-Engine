using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Assets;

namespace ShanyuEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 场景编辑工作流 (SceneEditorWorkflow)
    //
    // 提供完整的场景编辑工作流支持:
    //   * SceneEditor: 场景操作上下文
    //   * SceneBookmark: 书签 (位置+朝向+名称+截图)
    //   * SceneSnapshot: 快照 (完整场景状态保存/恢复)
    //   * SceneDiff: 差异比较 (两快照间变更列表)
    //   * Undo/Redo 系统:
    //     - UndoStack: 操作栈
    //     - UndoableAction: 可撤销操作 (Do/Undo/Redo/描述)
    //     - 内置操作 (移动/旋转/缩放/创建/删除/属性修改/分组)
    //     - 多级撤销 (无限/可配置上限)
    //     - 事务支持 (BeginTransaction/CommitTransaction/Rollback)
    //   * 场景层级拖放 (父级改变)
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 场景编辑器
    // ──────────────────────────────────────────────────────────────

    /// <summary>场景编辑器: 场景操作的上下文和入口。</summary>
    public sealed class SceneEditor
    {
        /// <summary>场景名称。</summary>
        public string SceneName { get; set; } = "Untitled";

        /// <summary>书签管理器。</summary>
        public SceneBookmarkManager Bookmarks { get; } = new();
        /// <summary>快照管理器。</summary>
        public SceneSnapshotManager Snapshots { get; } = new();
        /// <summary>撤销/重做栈。</summary>
        public SceneUndoStack UndoStack { get; } = new();

        /// <summary>场景已修改事件。</summary>
        public event Action OnSceneModified;

        /// <summary>通知场景已修改。</summary>
        public void MarkModified() => OnSceneModified?.Invoke();
    }

    // ──────────────────────────────────────────────────────────────
    // 场景书签
    // ──────────────────────────────────────────────────────────────

    /// <summary>场景书签: 保存摄像机位置/朝向, 快速跳转。</summary>
    public sealed class SceneBookmark
    {
        /// <summary>书签 ID。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>书签名称。</summary>
        public string Name { get; set; }
        /// <summary>摄像机位置。</summary>
        public Vector3 CameraPosition { get; set; }
        /// <summary>摄像机朝向 (前向)。</summary>
        public Vector3 CameraForward { get; set; } = new(0f, 0f, -1f);
        /// <summary>摄像机上向。</summary>
        public Vector3 CameraUp { get; set; } = Vector3.UnitY;
        /// <summary>视场角。</summary>
        public float FieldOfView { get; set; } = 60f;
        /// <summary>创建时间。</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        /// <summary>截图路径 (可选)。</summary>
        public string ScreenshotPath { get; set; } = string.Empty;

        /// <summary>构建视图矩阵。</summary>
        public Matrix4x4 BuildViewMatrix()
        {
            return Matrix4x4.CreateLookAt(CameraPosition, CameraPosition + CameraForward, CameraUp);
        }
    }

    /// <summary>书签管理器。</summary>
    public sealed class SceneBookmarkManager
    {
        /// <summary>所有书签。</summary>
        public readonly List<SceneBookmark> Bookmarks = new();

        /// <summary>书签变更事件。</summary>
        public event Action OnBookmarksChanged;

        /// <summary>添加书签。</summary>
        public SceneBookmark Add(string name, Vector3 position, Vector3 forward, float fov = 60f)
        {
            var bookmark = new SceneBookmark
            {
                Name = name,
                CameraPosition = position,
                CameraForward = forward,
                FieldOfView = fov
            };
            Bookmarks.Add(bookmark);
            OnBookmarksChanged?.Invoke();
            return bookmark;
        }

        /// <summary>删除书签。</summary>
        public bool Remove(Guid id)
        {
            int idx = Bookmarks.FindIndex(b => b.Id == id);
            if (idx < 0) return false;
            Bookmarks.RemoveAt(idx);
            OnBookmarksChanged?.Invoke();
            return true;
        }

        /// <summary>跳转到书签。</summary>
        public SceneBookmark FindByName(string name) =>
            Bookmarks.Find(b => b.Name == name);
    }

    // ──────────────────────────────────────────────────────────────
    // 场景快照
    // ──────────────────────────────────────────────────────────────

    /// <summary>场景快照: 完整场景状态的序列化保存。</summary>
    public sealed class SceneSnapshot
    {
        /// <summary>快照 ID。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>快照名称。</summary>
        public string Name { get; set; }
        /// <summary>创建时间。</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        /// <summary>序列化数据 (JSON)。</summary>
        public string SerializedData { get; set; } = string.Empty;
        /// <summary>对象数量 (快照时)。</summary>
        public int ObjectCount { get; set; }

        /// <summary>对象记录。</summary>
        public readonly List<ObjectSnapshotRecord> Records = new();
    }

    /// <summary>单个对象的快照记录。</summary>
    public sealed class ObjectSnapshotRecord
    {
        public Guid ObjectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float SclX, SclY, SclZ;
        public int Layer { get; set; }
        public bool IsVisible { get; set; } = true;
        public string MeshAssetPath { get; set; } = string.Empty;
    }

    /// <summary>快照管理器。</summary>
    public sealed class SceneSnapshotManager
    {
        /// <summary>所有快照。</summary>
        public readonly List<SceneSnapshot> Snapshots = new();

        /// <summary>快照变更事件。</summary>
        public event Action OnSnapshotsChanged;

        /// <summary>创建快照。</summary>
        public SceneSnapshot Create(string name, string serializedData, int objectCount)
        {
            var snapshot = new SceneSnapshot
            {
                Name = name,
                SerializedData = serializedData,
                ObjectCount = objectCount
            };
            Snapshots.Add(snapshot);
            OnSnapshotsChanged?.Invoke();
            return snapshot;
        }

        /// <summary>删除快照。</summary>
        public bool Remove(Guid id)
        {
            int idx = Snapshots.FindIndex(s => s.Id == id);
            if (idx < 0) return false;
            Snapshots.RemoveAt(idx);
            OnSnapshotsChanged?.Invoke();
            return true;
        }

        /// <summary>比较两个快照的差异。</summary>
        public static SceneDiff Compare(SceneSnapshot before, SceneSnapshot after)
        {
            return SceneDiff.Compare(before, after);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 场景差异
    // ──────────────────────────────────────────────────────────────

    /// <summary>变更类型。</summary>
    public enum ChangeType
    {
        Added,
        Removed,
        Moved,
        Rotated,
        Scaled,
        PropertyChanged,
        Reparented
    }

    /// <summary>单个变更记录。</summary>
    public sealed class ChangeRecord
    {
        /// <summary>变更类型。</summary>
        public ChangeType Type { get; set; }
        /// <summary>对象名称。</summary>
        public string ObjectName { get; set; } = string.Empty;
        /// <summary>对象 ID。</summary>
        public Guid ObjectId { get; set; }
        /// <summary>变更描述。</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>属性路径 (仅 PropertyChanged)。</summary>
        public string PropertyPath { get; set; } = string.Empty;
        /// <summary>旧值 (序列化)。</summary>
        public string OldValue { get; set; } = string.Empty;
        /// <summary>新值 (序列化)。</summary>
        public string NewValue { get; set; } = string.Empty;
    }

    /// <summary>场景差异: 两个快照间的变更列表。</summary>
    public sealed class SceneDiff
    {
        /// <summary>变更列表。</summary>
        public readonly List<ChangeRecord> Changes = new();
        /// <summary>快照 A (之前)。</summary>
        public SceneSnapshot SnapshotA { get; }
        /// <summary>快照 B (之后)。</summary>
        public SceneSnapshot SnapshotB { get; }
        /// <summary>是否有变更。</summary>
        public bool HasChanges => Changes.Count > 0;

        private SceneDiff(SceneSnapshot a, SceneSnapshot b)
        {
            SnapshotA = a;
            SnapshotB = b;
        }

        /// <summary>比较两个快照。</summary>
        public static SceneDiff Compare(SceneSnapshot before, SceneSnapshot after)
        {
            var diff = new SceneDiff(before, after);

            if (before == null || after == null) return diff;

            // 对比对象列表找出新增/删除/修改
            var beforeIds = new HashSet<Guid>();
            var afterIds = new HashSet<Guid>();

            foreach (var r in before.Records) beforeIds.Add(r.ObjectId);
            foreach (var r in after.Records) afterIds.Add(r.ObjectId);

            // 新增
            foreach (var r in after.Records)
            {
                if (!beforeIds.Contains(r.ObjectId))
                {
                    diff.Changes.Add(new ChangeRecord
                    {
                        Type = ChangeType.Added,
                        ObjectName = r.Name,
                        ObjectId = r.ObjectId,
                        Description = $"Added '{r.Name}'"
                    });
                }
            }

            // 删除
            foreach (var r in before.Records)
            {
                if (!afterIds.Contains(r.ObjectId))
                {
                    diff.Changes.Add(new ChangeRecord
                    {
                        Type = ChangeType.Removed,
                        ObjectName = r.Name,
                        ObjectId = r.ObjectId,
                        Description = $"Removed '{r.Name}'"
                    });
                }
            }

            // 修改 (对比共有对象的属性)
            foreach (var afterRecord in after.Records)
            {
                if (!beforeIds.Contains(afterRecord.ObjectId)) continue;
                var beforeRecord = before.Records.Find(r => r.ObjectId == afterRecord.ObjectId);
                if (beforeRecord == null) continue;

                if (MathF.Abs(beforeRecord.PosX - afterRecord.PosX) > 0.001f ||
                    MathF.Abs(beforeRecord.PosY - afterRecord.PosY) > 0.001f ||
                    MathF.Abs(beforeRecord.PosZ - afterRecord.PosZ) > 0.001f)
                {
                    diff.Changes.Add(new ChangeRecord
                    {
                        Type = ChangeType.Moved,
                        ObjectName = afterRecord.Name,
                        ObjectId = afterRecord.ObjectId,
                        Description = $"Moved '{afterRecord.Name}'"
                    });
                }

                if (MathF.Abs(beforeRecord.SclX - afterRecord.SclX) > 0.001f ||
                    MathF.Abs(beforeRecord.SclY - afterRecord.SclY) > 0.001f ||
                    MathF.Abs(beforeRecord.SclZ - afterRecord.SclZ) > 0.001f)
                {
                    diff.Changes.Add(new ChangeRecord
                    {
                        Type = ChangeType.Scaled,
                        ObjectName = afterRecord.Name,
                        ObjectId = afterRecord.ObjectId,
                        Description = $"Scaled '{afterRecord.Name}'"
                    });
                }
            }

            return diff;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Undo/Redo 系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 可撤销操作接口。
    /// </summary>
    public interface ISceneUndoAction
    {
        /// <summary>操作描述 (用于 UI 显示)。</summary>
        string Description { get; }
        /// <summary>执行操作。</summary>
        void Do();
        /// <summary>撤销操作。</summary>
        void Undo();
        /// <summary>重做操作。</summary>
        void Redo();
    }

    /// <summary>场景撤销栈: 支持多级撤销/重做和事务。</summary>
    public sealed class SceneUndoStack
    {
        /// <summary>最大撤销步数 (0 = 无限)。</summary>
        public int MaxUndoSteps { get; set; } = 100;

        private readonly Stack<ISceneUndoAction> _undo = new();
        private readonly Stack<ISceneUndoAction> _redo = new();
        private readonly Stack<ISceneUndoAction> _transactionBuffer = new();
        private int _transactionDepth;

        /// <summary>是否可以撤销。</summary>
        public bool CanUndo => _undo.Count > 0;
        /// <summary>是否可以重做。</summary>
        public bool CanRedo => _redo.Count > 0;
        /// <summary>撤销栈大小。</summary>
        public int UndoCount => _undo.Count;
        /// <summary>重做栈大小。</summary>
        public int RedoCount => _redo.Count;

        /// <summary>栈变更事件。</summary>
        public event Action OnStackChanged;

        /// <summary>执行操作并压入撤销栈。</summary>
        public void Execute(ISceneUndoAction action)
        {
            if (action == null) return;

            if (_transactionDepth > 0)
            {
                _transactionBuffer.Push(action);
                action.Do();
            }
            else
            {
                action.Do();
                Push(action);
            }
        }

        /// <summary>撤销。</summary>
        public bool Undo()
        {
            if (!CanUndo) return false;
            var action = _undo.Pop();
            action.Undo();
            _redo.Push(action);
            OnStackChanged?.Invoke();
            return true;
        }

        /// <summary>重做。</summary>
        public bool Redo()
        {
            if (!CanRedo) return false;
            var action = _redo.Pop();
            action.Redo();
            _undo.Push(action);
            OnStackChanged?.Invoke();
            return true;
        }

        /// <summary>清除栈。</summary>
        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _transactionBuffer.Clear();
            _transactionDepth = 0;
            OnStackChanged?.Invoke();
        }

        /// <summary>开始事务 (所有操作合并为一个原子撤销步)。</summary>
        public void BeginTransaction()
        {
            _transactionDepth++;
        }

        /// <summary>提交事务。</summary>
        public void CommitTransaction()
        {
            if (_transactionDepth <= 0) return;
            _transactionDepth--;

            if (_transactionDepth == 0 && _transactionBuffer.Count > 0)
            {
                var actions = _transactionBuffer.ToArray();
                _transactionBuffer.Clear();
                var compound = new CompoundUndoAction(actions);
                _undo.Push(compound);
                _redo.Clear();
                TrimStack();
                OnStackChanged?.Invoke();
            }
        }

        /// <summary>回滚事务 (丢弃所有缓冲操作)。</summary>
        public void RollbackTransaction()
        {
            if (_transactionDepth <= 0) return;
            // 撤销缓冲中的所有操作
            while (_transactionBuffer.Count > 0)
            {
                var action = _transactionBuffer.Pop();
                action.Undo();
            }
            _transactionDepth--;
        }

        private void Push(ISceneUndoAction action)
        {
            _undo.Push(action);
            _redo.Clear();
            TrimStack();
            OnStackChanged?.Invoke();
        }

        private void TrimStack()
        {
            if (MaxUndoSteps <= 0) return;
            while (_undo.Count > MaxUndoSteps)
            {
                // 移除最旧的 (栈底)
                var temp = new Stack<ISceneUndoAction>();
                while (_undo.Count > MaxUndoSteps)
                    temp.Push(_undo.Pop());
                temp.Clear();
            }
        }
    }

    /// <summary>复合操作: 将多个操作合并为一个原子操作。</summary>
    public sealed class CompoundUndoAction : ISceneUndoAction
    {
        private readonly ISceneUndoAction[] _actions;

        public string Description { get; }

        public CompoundUndoAction(ISceneUndoAction[] actions)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            Description = actions.Length > 0
                ? $"{actions.Length} actions (transaction)"
                : "Empty transaction";
        }

        public void Do()
        {
            foreach (var a in _actions) a.Do();
        }

        public void Undo()
        {
            for (int i = _actions.Length - 1; i >= 0; i--)
                _actions[i].Undo();
        }

        public void Redo()
        {
            foreach (var a in _actions) a.Redo();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 内置可撤销操作
    // ──────────────────────────────────────────────────────────────

    /// <summary>移动操作。</summary>
    public sealed class MoveUndoAction : ISceneUndoAction
    {
        private readonly object _target;
        private readonly Vector3 _before;
        private readonly Vector3 _after;
        public string Description => $"Move";

        public MoveUndoAction(object target, Vector3 before, Vector3 after)
        {
            _target = target;
            _before = before;
            _after = after;
        }

        public void Do() { Apply(_after); }
        public void Undo() { Apply(_before); }
        public void Redo() { Apply(_after); }

        private void Apply(Vector3 position)
        {
            if (_target is Assets.SceneObject obj)
            {
                obj.Position = position;
                obj.LocalPosition = position;
            }
        }
    }

    /// <summary>旋转操作。</summary>
    public sealed class RotateUndoAction : ISceneUndoAction
    {
        private readonly object _target;
        private readonly Quaternion _before;
        private readonly Quaternion _after;
        public string Description => $"Rotate";

        public RotateUndoAction(object target, Quaternion before, Quaternion after)
        {
            _target = target;
            _before = before;
            _after = after;
        }

        public void Do() { Apply(_after); }
        public void Undo() { Apply(_before); }
        public void Redo() { Apply(_after); }

        private void Apply(Quaternion rotation)
        {
            if (_target is Assets.SceneObject obj)
            {
                obj.Rotation = rotation;
                obj.LocalRotation = rotation;
            }
        }
    }

    /// <summary>缩放操作。</summary>
    public sealed class ScaleUndoAction : ISceneUndoAction
    {
        private readonly object _target;
        private readonly Vector3 _before;
        private readonly Vector3 _after;
        public string Description => $"Scale";

        public ScaleUndoAction(object target, Vector3 before, Vector3 after)
        {
            _target = target;
            _before = before;
            _after = after;
        }

        public void Do() { Apply(_after); }
        public void Undo() { Apply(_before); }
        public void Redo() { Apply(_after); }

        private void Apply(Vector3 scale)
        {
            if (_target is Assets.SceneObject obj)
            {
                obj.Scale = scale;
                obj.LocalScale = scale;
            }
        }
    }

    /// <summary>创建对象操作。</summary>
    public sealed class CreateUndoAction : ISceneUndoAction
    {
        private readonly Func<Assets.SceneObject> _factory;
        private readonly Action<Assets.SceneObject> _remover;
        private Assets.SceneObject _created;
        public string Description => $"Create Object";

        public CreateUndoAction(Func<Assets.SceneObject> factory, Action<Assets.SceneObject> remover)
        {
            _factory = factory;
            _remover = remover;
        }

        public void Do()
        {
            _created = _factory();
            Description = $"Create '{_created?.Name}'";
        }

        public void Undo() { _remover?.Invoke(_created); }
        public void Redo() { Do(); }
    }

    /// <summary>删除对象操作。</summary>
    public sealed class DeleteUndoAction : ISceneUndoAction
    {
        private readonly Assets.SceneObject _target;
        private readonly Action<Assets.SceneObject> _adder;
        private readonly Action<Assets.SceneObject> _remover;
        public string Description => $"Delete '{_target?.Name}'";

        public DeleteUndoAction(Assets.SceneObject target, Action<Assets.SceneObject> adder, Action<Assets.SceneObject> remover)
        {
            _target = target;
            _adder = adder;
            _remover = remover;
        }

        public void Do() { _remover?.Invoke(_target); }
        public void Undo() { _adder?.Invoke(_target); }
        public void Redo() { _remover?.Invoke(_target); }
    }

    /// <summary>属性修改操作。</summary>
    public sealed class PropertyChangeUndoAction : ISceneUndoAction
    {
        private readonly object _target;
        private readonly string _propertyPath;
        private readonly string _oldValue;
        private readonly string _newValue;
        public string Description => $"Set {_propertyPath}";

        public PropertyChangeUndoAction(object target, string propertyPath, string oldValue, string newValue)
        {
            _target = target;
            _propertyPath = propertyPath;
            _oldValue = oldValue;
            _newValue = newValue;
        }

        public void Do() { /* 由外部应用 */ }
        public void Undo() { /* 由外部应用 */ }
        public void Redo() { /* 由外部应用 */ }
    }

    /// <summary>分组操作。</summary>
    public sealed class GroupUndoAction : ISceneUndoAction
    {
        private readonly List<Assets.SceneObject> _children;
        private readonly Func<Assets.SceneObject, Assets.SceneObject, Assets.SceneObject> _grouper;
        private readonly Action<Assets.SceneObject, Assets.SceneObject> _ungrouper;
        private Assets.SceneObject _groupParent;
        public string Description => $"Group {_children?.Count ?? 0} objects";

        public GroupUndoAction(
            List<Assets.SceneObject> children,
            Func<Assets.SceneObject, Assets.SceneObject, Assets.SceneObject> grouper,
            Action<Assets.SceneObject, Assets.SceneObject> ungrouper)
        {
            _children = children;
            _grouper = grouper;
            _ungrouper = ungrouper;
        }

        public void Do()
        {
            // 简化: 创建父对象并将所有子对象挂到父
            // 实际实现由外部处理
        }

        public void Undo()
        {
            if (_groupParent != null)
                _ungrouper?.Invoke(_groupParent, _groupParent);
        }

        public void Redo() { Do(); }
    }

    // ──────────────────────────────────────────────────────────────
    // 场景层级拖放
    // ──────────────────────────────────────────────────────────────

    /// <summary>场景层级拖放操作: 改变对象的父级关系。</summary>
    public sealed class ReparentUndoAction : ISceneUndoAction
    {
        private readonly Assets.SceneObject _child;
        private readonly Assets.SceneObject _oldParent;
        private readonly Assets.SceneObject _newParent;
        private readonly Vector3 _oldWorldPosition;
        public string Description => $"Reparent '{_child?.Name}'";

        public ReparentUndoAction(Assets.SceneObject child, Assets.SceneObject newParent)
        {
            _child = child;
            _oldParent = child?.Parent;
            _newParent = newParent;
            _oldWorldPosition = child != null ? child.Position : Vector3.Zero;
        }

        public void Do()
        {
            if (_child == null) return;
            if (_newParent != null)
                _newParent.AttachChild(_child);
            else
                _child.DetachFromParent();
        }

        public void Undo()
        {
            if (_child == null) return;
            if (_oldParent != null)
                _oldParent.AttachChild(_child);
            else
                _child.DetachFromParent();
            _child.Position = _oldWorldPosition;
        }

        public void Redo() { Do(); }
    }
}
