using System.Numerics;
using ShanyuEngine.Assets;

namespace ShanyuEngine.Editor
{

    public sealed class EditorCommandStack
    {
        private readonly Stack<IEditorCommand> _undo = new();
        private readonly Stack<IEditorCommand> _redo = new();

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;
        public string UndoDescription => CanUndo ? _undo.Peek().Description : string.Empty;
        public string RedoDescription => CanRedo ? _redo.Peek().Description : string.Empty;

        public void Execute(IEditorCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            command.Execute();
            _undo.Push(command);
            _redo.Clear();
        }

        public bool Undo()
        {
            if (!CanUndo) return false;
            var command = _undo.Pop();
            command.Undo();
            _redo.Push(command);
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;
            var command = _redo.Pop();
            command.Execute();
            _undo.Push(command);
            return true;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }
    }

    public sealed class CreateObjectCommand : IEditorCommand
    {
        private readonly AssetDatabase _assets;
        private readonly string _name;
        private readonly string _meshAssetPath;
        private readonly Vector3 _position;
        private SceneObject? _created;

        public string Description => $"Create Object '{_name}'";

        public CreateObjectCommand(AssetDatabase assets, string name, string meshAssetPath, Vector3 position)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _name = string.IsNullOrWhiteSpace(name) ? "GameObject" : name;
            _meshAssetPath = string.IsNullOrWhiteSpace(meshAssetPath) ? "Box" : meshAssetPath;
            _position = position;
        }

        public void Execute()
        {
            if (_created == null)
            {
                _created = _assets.CreateObject(_name, _meshAssetPath, _position);
                return;
            }

            if (!_assets.SceneObjects.Any(o => o.Id == _created.Id))
            {
                _assets.AddObject(_created);
            }
        }

        public void Undo()
        {
            if (_created != null)
            {
                _assets.RemoveObject(_created);
            }
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    public sealed class DeleteObjectCommand : IEditorCommand
    {
        private readonly AssetDatabase _assets;
        private readonly SceneObject _target;

        public string Description => $"Delete Object '{_target.Name}'";

        public DeleteObjectCommand(AssetDatabase assets, SceneObject target)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        public void Execute()
        {
            _assets.RemoveObject(_target);
        }

        public void Undo()
        {
            if (!_assets.SceneObjects.Any(o => o.Id == _target.Id))
            {
                _assets.AddObject(_target);
            }
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    public sealed class MoveObjectCommand : IEditorCommand
    {
        private readonly SceneObject _target;
        private readonly Vector3 _before;
        private readonly Vector3 _after;

        public string Description => $"Move Object '{_target.Name}'";

        public MoveObjectCommand(SceneObject target, Vector3 after)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _before = target.Position;
            _after = after;
        }

        public void Execute()
        {
            _target.Position = _after;
            _target.LocalPosition = _after;
        }

        public void Undo()
        {
            _target.Position = _before;
            _target.LocalPosition = _before;
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    public sealed class SetObjectPropertyCommand<T> : IEditorCommand
    {
        private readonly SceneObject _target;
        private readonly string _propertyName;
        private readonly Func<SceneObject, T> _getter;
        private readonly Action<SceneObject, T> _setter;
        private readonly T _before;
        private readonly T _after;

        public string Description => $"Set {_propertyName} on '{_target.Name}'";

        public SetObjectPropertyCommand(
            SceneObject target,
            string propertyName,
            Func<SceneObject, T> getter,
            Action<SceneObject, T> setter,
            T after)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _propertyName = string.IsNullOrWhiteSpace(propertyName) ? "Property" : propertyName;
            _getter = getter ?? throw new ArgumentNullException(nameof(getter));
            _setter = setter ?? throw new ArgumentNullException(nameof(setter));
            _before = _getter(_target);
            _after = after;
        }

        public void Execute()
        {
            _setter(_target, _after);
        }

        public void Undo()
        {
            _setter(_target, _before);
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    /// <summary>旋转对象命令: 记录旋转前后的四元数, 支持撤销/重做。</summary>
    public sealed class RotateObjectCommand : IEditorCommand
    {
        private readonly SceneObject _target;
        private readonly Quaternion _before;
        private readonly Quaternion _after;

        public string Description => $"Rotate Object '{_target.Name}'";

        public RotateObjectCommand(SceneObject target, Quaternion after)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _before = target.Rotation;
            _after = after;
        }

        public void Execute()
        {
            _target.Rotation = _after;
            _target.LocalRotation = _after;
        }

        public void Undo()
        {
            _target.Rotation = _before;
            _target.LocalRotation = _before;
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    /// <summary>缩放对象命令: 记录缩放前后的向量, 支持撤销/重做。</summary>
    public sealed class ScaleObjectCommand : IEditorCommand
    {
        private readonly SceneObject _target;
        private readonly Vector3 _before;
        private readonly Vector3 _after;

        public string Description => $"Scale Object '{_target.Name}'";

        public ScaleObjectCommand(SceneObject target, Vector3 after)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _before = target.Scale;
            _after = after;
        }

        public void Execute()
        {
            _target.Scale = _after;
            _target.LocalScale = _after;
        }

        public void Undo()
        {
            _target.Scale = _before;
            _target.LocalScale = _before;
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    /// <summary>复制对象命令: 深拷贝一个场景对象, 撤销时移除复制的对象。</summary>
    public sealed class DuplicateObjectCommand : IEditorCommand
    {
        private readonly AssetDatabase _assets;
        private readonly SceneObject _source;
        private readonly Vector3 _offset;
        private SceneObject? _duplicate;

        public string Description => $"Duplicate Object '{_source.Name}'";

        public DuplicateObjectCommand(AssetDatabase assets, SceneObject source, Vector3 offset = default)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _offset = offset;
        }

        public void Execute()
        {
            if (_duplicate == null)
            {
                _duplicate = DeepClone(_source);
                _duplicate.Id = Guid.NewGuid();
                _duplicate.Position = _source.Position + _offset;
                _duplicate.LocalPosition = _duplicate.Position;
                _duplicate.Name = _source.Name + " (Copy)";
            }

            if (!_assets.SceneObjects.Any(o => o.Id == _duplicate.Id))
            {
                _assets.AddObject(_duplicate);
            }
        }

        public void Undo()
        {
            if (_duplicate != null)
            {
                _assets.RemoveObject(_duplicate);
            }
        }

        /// <summary>深拷贝场景对象 (包括子对象树)。</summary>
        private static SceneObject DeepClone(SceneObject source)
        {
            var clone = new SceneObject
            {
                Name = source.Name,
                IsVisible = source.IsVisible,
                IsSelected = source.IsSelected,
                Layer = source.Layer,
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
                PrefabSourceGuid = source.PrefabSourceGuid
            };
            foreach (var child in source.Children)
                clone.Children.Add(DeepClone(child));
            return clone;
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}

    /// <summary>批量变换命令: 对多个对象执行移动/旋转/缩放, 作为一个原子操作。</summary>
    public sealed class BatchTransformCommand : IEditorCommand
    {
        private readonly SceneObject[] _targets;
        private readonly Vector3[] _beforePositions;
        private readonly Quaternion[] _beforeRotations;
        private readonly Vector3[] _beforeScales;
        private readonly Vector3 _translation;
        private readonly Quaternion _rotationDelta;
        private readonly Vector3 _scaleFactors;
        private readonly Vector3 _pivot;

        public string Description => $"Transform {_targets.Length} Object(s)";

        public BatchTransformCommand(
            IEnumerable<SceneObject> targets,
            Vector3 translation,
            Quaternion rotationDelta,
            Vector3 scaleFactors,
            Vector3? pivot = null)
        {
            _targets = targets?.ToArray() ?? throw new ArgumentNullException(nameof(targets));
            if (_targets.Length == 0) throw new ArgumentException("At least one target is required.", nameof(targets));

            _beforePositions = new Vector3[_targets.Length];
            _beforeRotations = new Quaternion[_targets.Length];
            _beforeScales = new Vector3[_targets.Length];
            for (int i = 0; i < _targets.Length; i++)
            {
                _beforePositions[i] = _targets[i].Position;
                _beforeRotations[i] = _targets[i].Rotation;
                _beforeScales[i] = _targets[i].Scale;
            }

            _translation = translation;
            _rotationDelta = rotationDelta;
            _scaleFactors = scaleFactors;

            // 计算中心点作为变换枢轴
            if (pivot.HasValue)
            {
                _pivot = pivot.Value;
            }
            else
            {
                Vector3 sum = Vector3.Zero;
                foreach (var t in _targets) sum += t.Position;
                _pivot = sum / _targets.Length;
            }
        }

        public void Execute()
        {
            for (int i = 0; i < _targets.Length; i++)
            {
                var obj = _targets[i];
                var offset = obj.Position - _pivot;
                obj.Position = _pivot + Vector3.Transform(offset, _rotationDelta) * _scaleFactors + _translation;
                obj.Rotation = _rotationDelta * obj.Rotation;
                obj.Scale *= _scaleFactors;
                obj.LocalPosition = obj.Position;
                obj.LocalRotation = obj.Rotation;
                obj.LocalScale = obj.Scale;
            }
        }

        public void Undo()
        {
            for (int i = 0; i < _targets.Length; i++)
            {
                var obj = _targets[i];
                obj.Position = _beforePositions[i];
                obj.Rotation = _beforeRotations[i];
                obj.Scale = _beforeScales[i];
                obj.LocalPosition = obj.Position;
                obj.LocalRotation = obj.Rotation;
                obj.LocalScale = obj.Scale;
            }
        }
    
        public string Name => Description;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void Execute(EditorWorkspace workspace) => Execute();
        public void Undo(EditorWorkspace workspace) => Undo();
}
}
