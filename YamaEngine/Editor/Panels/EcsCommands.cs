// ==================================================================================================
//  EcsCommands.cs — yama引擎 ECS 撤销/重做命令集
// ==================================================================================================
//  命名空间: YamaEngine.Editor.Panels
//  依赖:     YamaEngine.ECS, YamaEngine.Editor (IEditorCommand)
//
//  功能:
//    为 ECS 实体操作提供命令化封装，集成到 EditorCommandStack：
//      * EcsCreateEntityCommand    — 创建实体（Undo=销毁，Redo=重新创建）
//      * EcsDeleteEntityCommand    — 删除实体（Undo=恢复完整快照）
//      * EcsSetComponentCommand<T> — 修改组件字段（Undo=恢复旧值）
//      * EcsSetEntityNameCommand   — 重命名
//      * EcsSetTransformCommand    — 修改 Transform（位置/旋转/缩放）
//      * EcsSetParentCommand       — 修改父子关系
//      * EcsAddComponentCommand<T> — 添加组件
//      * EcsRemoveComponentCommand<T> — 移除组件
//
//  所有命令都支持 Execute/Undo，通过 EcsEntitySnapshot 保存/恢复完整实体状态。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.ECS;

namespace YamaEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════════
    //  EcsEntitySnapshot — 实体完整快照
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>ECS 实体完整快照，用于撤销/重做时恢复实体状态。</summary>
    public sealed class EcsEntitySnapshot
    {
        /// <summary>实体名称。</summary>
        public string Name { get; set; } = "Entity";

        /// <summary>变换组件。</summary>
        public TransformComponent? Transform { get; set; }

        /// <summary>网格组件。</summary>
        public MeshComponent? Mesh { get; set; }

        /// <summary>灯光组件。</summary>
        public LightComponent? Light { get; set; }

        /// <summary>相机组件。</summary>
        public CameraComponent? Camera { get; set; }

        /// <summary>标签组件。</summary>
        public TagComponent? Tag { get; set; }

        /// <summary>父子关系组件。</summary>
        public RelationshipComponent? Relationship { get; set; }

        /// <summary>从 ECS 捕获实体快照。</summary>
        public static EcsEntitySnapshot Capture(EntityComponentSystem ecs, int entityId)
        {
            var snapshot = new EcsEntitySnapshot
            {
                Name = ecs.GetEntityName(entityId) ?? "Entity",
            };

            if (ecs.HasComponent<TransformComponent>(entityId))
                snapshot.Transform = ecs.GetComponent<TransformComponent>(entityId);
            if (ecs.HasComponent<MeshComponent>(entityId))
                snapshot.Mesh = ecs.GetComponent<MeshComponent>(entityId);
            if (ecs.HasComponent<LightComponent>(entityId))
                snapshot.Light = ecs.GetComponent<LightComponent>(entityId);
            if (ecs.HasComponent<CameraComponent>(entityId))
                snapshot.Camera = ecs.GetComponent<CameraComponent>(entityId);
            if (ecs.HasComponent<TagComponent>(entityId))
                snapshot.Tag = ecs.GetComponent<TagComponent>(entityId);
            if (ecs.HasComponent<RelationshipComponent>(entityId))
                snapshot.Relationship = ecs.GetComponent<RelationshipComponent>(entityId);

            return snapshot;
        }

        /// <summary>将快照恢复到指定实体（实体必须已存在）。</summary>
        public void RestoreTo(EntityComponentSystem ecs, int entityId)
        {
            ecs.SetEntityName(entityId, Name);

            if (Transform.HasValue) ecs.SetComponent(entityId, Transform.Value);
            if (Mesh.HasValue) ecs.SetComponent(entityId, Mesh.Value);
            if (Light.HasValue) ecs.SetComponent(entityId, Light.Value);
            if (Camera.HasValue) ecs.SetComponent(entityId, Camera.Value);
            if (Tag.HasValue) ecs.SetComponent(entityId, Tag.Value);
            if (Relationship.HasValue) ecs.SetComponent(entityId, Relationship.Value);
        }

        /// <summary>创建新实体并应用快照。返回新实体 ID。</summary>
        public int CreateAndRestore(EntityComponentSystem ecs)
        {
            int id = ecs.CreateEntity(Name,
                position: Transform?.Position ?? Vector3.Zero,
                rotation: Transform?.Rotation ?? Quaternion.Identity,
                scale: Transform?.Scale ?? Vector3.One);

            if (Mesh.HasValue) ecs.AddComponent(id, Mesh.Value);
            if (Light.HasValue) ecs.AddComponent(id, Light.Value);
            if (Camera.HasValue) ecs.AddComponent(id, Camera.Value);
            if (Tag.HasValue) ecs.AddComponent(id, Tag.Value);
            if (Relationship.HasValue) ecs.AddComponent(id, Relationship.Value);

            return id;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsCreateEntityCommand
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>创建 ECS 实体命令。</summary>
    public sealed class EcsCreateEntityCommand : IEditorCommand
    {
        private readonly EntityComponentSystem _ecs;
        private readonly EcsEntitySnapshot _snapshot;
        private int _entityId = -1;
        private bool _executed;

        public string Description => $"创建实体 '{_snapshot.Name}'";

        public int EntityId => _entityId;

        public EcsCreateEntityCommand(EntityComponentSystem ecs, EcsEntitySnapshot snapshot)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public void Execute()
        {
            if (_executed && _entityId >= 0 && _ecs.HasEntity(_entityId))
            {
                // 已经存在，不需要重新创建
                return;
            }

            _entityId = _snapshot.CreateAndRestore(_ecs);
            _executed = true;
        }

        public void Undo()
        {
            if (_entityId >= 0 && _ecs.HasEntity(_entityId))
            {
                _ecs.DestroyEntity(_entityId);
            }
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsDeleteEntityCommand
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>删除 ECS 实体命令。</summary>
    public sealed class EcsDeleteEntityCommand : IEditorCommand
    {
        private readonly EntityComponentSystem _ecs;
        private EcsEntitySnapshot _snapshot;
        private int _entityId;
        private bool _wasDestroyed;

        public string Description => $"删除实体 '{_snapshot.Name}'";

        public EcsDeleteEntityCommand(EntityComponentSystem ecs, int entityId)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            if (entityId < 0 || !ecs.HasEntity(entityId))
                throw new ArgumentException("Entity does not exist.", nameof(entityId));

            _entityId = entityId;
            _snapshot = EcsEntitySnapshot.Capture(ecs, entityId);
            _wasDestroyed = false;
        }

        public void Execute()
        {
            if (_wasDestroyed) return;
            if (_ecs.HasEntity(_entityId))
            {
                _snapshot = EcsEntitySnapshot.Capture(_ecs, _entityId); // 重新捕获最新状态
                _ecs.DestroyEntity(_entityId);
                _wasDestroyed = true;
            }
        }

        public void Undo()
        {
            if (!_wasDestroyed) return;
            _entityId = _snapshot.CreateAndRestore(_ecs);
            _wasDestroyed = false;
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsSetComponentCommand<T>
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 设置 ECS 组件值命令。记录修改前后的完整组件值。
    /// </summary>
    public sealed class EcsSetComponentCommand<T> : IEditorCommand where T : struct, IComponent
    {
        private readonly EntityComponentSystem _ecs;
        private readonly int _entityId;
        private readonly T _before;
        private T _after;
        private readonly string _fieldName;

        public string Description => $"修改 #{_entityId} 的 {_fieldName}";

        public EcsSetComponentCommand(EntityComponentSystem ecs, int entityId, T after, string fieldName = "")
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _entityId = entityId;
            _after = after;
            _fieldName = string.IsNullOrEmpty(fieldName) ? typeof(T).Name : fieldName;
            _before = ecs.HasComponent<T>(entityId) ? ecs.GetComponent<T>(entityId) : default;
        }

        public void Execute()
        {
            if (_ecs.HasEntity(_entityId))
            {
                if (!_ecs.HasComponent<T>(_entityId))
                    _ecs.AddComponent(_entityId, _after);
                else
                    _ecs.SetComponent(_entityId, _after);
            }
        }

        public void Undo()
        {
            if (_ecs.HasEntity(_entityId))
            {
                if (!_ecs.HasComponent<T>(_entityId))
                    _ecs.AddComponent(_entityId, _before);
                else
                    _ecs.SetComponent(_entityId, _before);
            }
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsSetEntityNameCommand
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>重命名 ECS 实体命令。</summary>
    public sealed class EcsSetEntityNameCommand : IEditorCommand
    {
        private readonly EntityComponentSystem _ecs;
        private readonly int _entityId;
        private readonly string _before;
        private readonly string _after;

        public string Description => $"重命名 '{_before}' → '{_after}'";

        public EcsSetEntityNameCommand(EntityComponentSystem ecs, int entityId, string newName)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _entityId = entityId;
            _before = ecs.GetEntityName(entityId) ?? "Entity";
            _after = newName ?? "Entity";
        }

        public void Execute() => _ecs.SetEntityName(_entityId, _after);
        public void Undo() => _ecs.SetEntityName(_entityId, _before);
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsSetTransformCommand
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>修改 ECS 实体 Transform 命令（支持部分修改）。</summary>
    public sealed class EcsSetTransformCommand : IEditorCommand
    {
        private readonly EntityComponentSystem _ecs;
        private readonly int _entityId;
        private readonly TransformComponent _before;
        private readonly TransformComponent _after;

        public string Description => $"变换 #{_entityId}";

        public EcsSetTransformCommand(EntityComponentSystem ecs, int entityId,
            Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _entityId = entityId;
            _before = ecs.HasComponent<TransformComponent>(entityId)
                ? ecs.GetComponent<TransformComponent>(entityId)
                : new TransformComponent();

            _after = _before;
            if (position.HasValue) _after.Position = position.Value;
            if (rotation.HasValue) _after.Rotation = rotation.Value;
            if (scale.HasValue) _after.Scale = scale.Value;
        }

        public void Execute()
        {
            if (_ecs.HasEntity(_entityId))
            {
                if (!_ecs.HasComponent<TransformComponent>(_entityId))
                    _ecs.AddComponent(_entityId, _after);
                else
                    _ecs.SetComponent(_entityId, _after);
            }
        }

        public void Undo()
        {
            if (_ecs.HasEntity(_entityId))
            {
                if (!_ecs.HasComponent<TransformComponent>(_entityId))
                    _ecs.AddComponent(_entityId, _before);
                else
                    _ecs.SetComponent(_entityId, _before);
            }
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsSetParentCommand
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>修改 ECS 实体父子关系命令。</summary>
    public sealed class EcsSetParentCommand : IEditorCommand
    {
        private readonly EntityComponentSystem _ecs;
        private readonly int _childId;
        private readonly int _oldParentId;
        private readonly int _newParentId;
        private readonly List<int>? _oldParentChildren;

        public string Description => $"设置 #{_childId} 的父实体";

        public EcsSetParentCommand(EntityComponentSystem ecs, int childId, int newParentId)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _childId = childId;
            _newParentId = newParentId;

            if (ecs.HasComponent<RelationshipComponent>(childId))
                _oldParentId = ecs.GetComponent<RelationshipComponent>(childId).ParentId;
            else
                _oldParentId = 0;

            // 保存旧父的 Children 列表
            if (_oldParentId != 0 && ecs.HasComponent<RelationshipComponent>(_oldParentId))
                _oldParentChildren = new List<int>(ecs.GetComponent<RelationshipComponent>(_oldParentId).Children ?? new List<int>());
        }

        public void Execute() => ApplyParent(_childId, _newParentId);
        public void Undo() => ApplyParent(_childId, _oldParentId);

        private void ApplyParent(int childId, int parentId)
        {
            if (!_ecs.HasEntity(childId)) return;

            // 断开旧父
            if (_ecs.HasComponent<RelationshipComponent>(childId))
            {
                var rel = _ecs.GetComponent<RelationshipComponent>(childId);
                int oldP = rel.ParentId;
                if (oldP != 0 && _ecs.HasComponent<RelationshipComponent>(oldP))
                {
                    var pRel = _ecs.GetComponent<RelationshipComponent>(oldP);
                    pRel.Children?.Remove(childId);
                    _ecs.SetComponent(oldP, pRel);
                }
                rel.ParentId = parentId;
                _ecs.SetComponent(childId, rel);
            }

            // 连接新父
            if (parentId != 0 && _ecs.HasEntity(parentId))
            {
                if (!_ecs.HasComponent<RelationshipComponent>(parentId))
                    _ecs.AddComponent(parentId, new RelationshipComponent { Children = new List<int>() });

                var pRel = _ecs.GetComponent<RelationshipComponent>(parentId);
                pRel.Children ??= new List<int>();
                if (!pRel.Children.Contains(childId))
                    pRel.Children.Add(childId);
                _ecs.SetComponent(parentId, pRel);
            }
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsAddComponentCommand<T>
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>添加 ECS 组件命令（Undo=移除组件）。</summary>
    public sealed class EcsAddComponentCommand<T> : IEditorCommand where T : struct, IComponent
    {
        private readonly EntityComponentSystem _ecs;
        private readonly int _entityId;
        private readonly T _component;

        public string Description => $"添加 {typeof(T).Name} 到 #{_entityId}";

        public EcsAddComponentCommand(EntityComponentSystem ecs, int entityId, T component)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _entityId = entityId;
            _component = component;
        }

        public void Execute()
        {
            if (_ecs.HasEntity(_entityId) && !_ecs.HasComponent<T>(_entityId))
                _ecs.AddComponent(_entityId, _component);
        }

        public void Undo()
        {
            if (_ecs.HasEntity(_entityId) && _ecs.HasComponent<T>(_entityId))
                _ecs.RemoveComponent<T>(_entityId);
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsRemoveComponentCommand<T>
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>移除 ECS 组件命令（Undo=恢复组件）。</summary>
    public sealed class EcsRemoveComponentCommand<T> : IEditorCommand where T : struct, IComponent
    {
        private readonly EntityComponentSystem _ecs;
        private readonly int _entityId;
        private readonly T _before;

        public string Description => $"移除 #{_entityId} 的 {typeof(T).Name}";

        public EcsRemoveComponentCommand(EntityComponentSystem ecs, int entityId)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _entityId = entityId;
            _before = ecs.HasComponent<T>(entityId) ? ecs.GetComponent<T>(entityId) : default;
        }

        public void Execute()
        {
            if (_ecs.HasEntity(_entityId) && _ecs.HasComponent<T>(_entityId))
                _ecs.RemoveComponent<T>(_entityId);
        }

        public void Undo()
        {
            if (_ecs.HasEntity(_entityId) && !_ecs.HasComponent<T>(_entityId))
                _ecs.AddComponent(_entityId, _before);
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsBatchCommand
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 批量 ECS 命令。将多个命令打包为一个原子操作。
    /// </summary>
    public sealed class EcsBatchCommand : IEditorCommand
    {
        private readonly List<IEditorCommand> _commands = new();

        public string Description { get; }

        public EcsBatchCommand(string description, params IEditorCommand[] commands)
        {
            Description = description ?? "批量操作";
            _commands.AddRange(commands);
        }

        public void Add(IEditorCommand command)
        {
            _commands.Add(command);
        }

        public void Execute()
        {
            foreach (var cmd in _commands)
                cmd.Execute();
        }

        public void Undo()
        {
            // 反向撤销
            for (int i = _commands.Count - 1; i >= 0; i--)
                _commands[i].Undo();
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    // ════════════════════════════════════════════════════════════════════════════
    //  EcsCommandStackAdapter — 命令栈快捷调用
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ECS 命令栈适配器。为编辑器面板提供便捷的 Undo/Redo 调用接口。
    /// </summary>
    public sealed class EcsCommandStackAdapter
    {
        private readonly EditorCommandStack _stack;
        private readonly EntityComponentSystem _ecs;

        public bool CanUndo => _stack.CanUndo;
        public bool CanRedo => _stack.CanRedo;
        public string UndoDescription => _stack.UndoDescription;
        public string RedoDescription => _stack.RedoDescription;

        public EcsCommandStackAdapter(EntityComponentSystem ecs, EditorCommandStack stack)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _stack = stack ?? throw new ArgumentNullException(nameof(stack));
        }

        public bool Undo() => _stack.Undo();
        public bool Redo() => _stack.Redo();
        public void Clear() => _stack.Clear();

        public void Execute(IEditorCommand command) => _stack.Execute(command);

        // 快捷方法

        public void RecordNameChange(int entityId, string newName)
            => _stack.Execute(new EcsSetEntityNameCommand(_ecs, entityId, newName));

        public void RecordTransformChange(int entityId, TransformComponent newTransform)
            => _stack.Execute(new EcsSetTransformCommand(_ecs, entityId,
                newTransform.Position, newTransform.Rotation, newTransform.Scale));

        public void RecordComponentChange<T>(int entityId, T newValue, string fieldName = "") where T : struct, IComponent
            => _stack.Execute(new EcsSetComponentCommand<T>(_ecs, entityId, newValue, fieldName));

        public void RecordDelete(int entityId)
            => _stack.Execute(new EcsDeleteEntityCommand(_ecs, entityId));

        public int RecordCreate(EcsEntitySnapshot snapshot)
        {
            var cmd = new EcsCreateEntityCommand(_ecs, snapshot);
            _stack.Execute(cmd);
            return cmd.EntityId;
        }

        public void RecordParentChange(int childId, int newParentId)
            => _stack.Execute(new EcsSetParentCommand(_ecs, childId, newParentId));
    }
}
