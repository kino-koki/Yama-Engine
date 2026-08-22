using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.Editor
{
    // 编辑器这块不搞花活，重点是把“选中 -> 修改 -> 撤销 -> 刷新视图”这条链路跑顺。
    public readonly record struct EditorObjectId(Guid Value)
    {
        public static EditorObjectId NewId() => new(Guid.NewGuid());
        public override string ToString() => Value.ToString("N");
    }

    public sealed record EditorSelectionChanged(IReadOnlyList<EditorObjectId> OldSelection, IReadOnlyList<EditorObjectId> NewSelection, DateTime Time);

    public sealed class EditorSelectionSet
    {
        private readonly List<EditorObjectId> _ids = new();
        private readonly List<EditorSelectionChanged> _history = new();
        public IReadOnlyList<EditorObjectId> Items => _ids;
        public IReadOnlyList<EditorSelectionChanged> History => _history;

        public EditorSelectionChanged Replace(IEnumerable<EditorObjectId> ids)
        {
            var old = _ids.ToArray();
            _ids.Clear();
            foreach (var id in ids.Distinct())
                _ids.Add(id);
            var evt = new EditorSelectionChanged(old, _ids.ToArray(), DateTime.Now);
            _history.Add(evt);
            if (_history.Count > 256) _history.RemoveAt(0);
            return evt;
        }

        public bool Toggle(EditorObjectId id)
        {
            if (_ids.Remove(id)) return false;
            _ids.Add(id);
            return true;
        }
    }

    public interface IEditorCommand
    {
        string Name { get; }
        bool CanMergeWith(IEditorCommand next);
        void Execute(EditorWorkspace workspace);
        void Undo(EditorWorkspace workspace);
    }

    public sealed class EditorUndoStack
    {
        private readonly Stack<IEditorCommand> _undo = new();
        private readonly Stack<IEditorCommand> _redo = new();
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;

        public void Execute(EditorWorkspace workspace, IEditorCommand command)
        {
            command.Execute(workspace);
            if (_undo.TryPeek(out var top) && top.CanMergeWith(command))
            {
                _undo.Pop();
                _undo.Push(command);
            }
            else
            {
                _undo.Push(command);
            }
            _redo.Clear();
        }

        public bool Undo(EditorWorkspace workspace)
        {
            if (!_undo.TryPop(out var command)) return false;
            command.Undo(workspace);
            _redo.Push(command);
            return true;
        }

        public bool Redo(EditorWorkspace workspace)
        {
            if (!_redo.TryPop(out var command)) return false;
            command.Execute(workspace);
            _undo.Push(command);
            return true;
        }
    }

    public sealed class SceneHierarchyNode
    {
        private readonly List<SceneHierarchyNode> _children = new();
        public EditorObjectId Id { get; }
        public string Name { get; private set; }
        public EditorObjectId? ParentId { get; private set; }
        public Vector3 LocalPosition { get; set; }
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        public Vector3 LocalScale { get; set; } = Vector3.One;
        public IReadOnlyList<SceneHierarchyNode> Children => _children;

        public SceneHierarchyNode(EditorObjectId id, string name)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name.Trim();
        }

        public void Rename(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("节点名不能为空", nameof(name));
            Name = name.Trim();
        }

        public void Attach(SceneHierarchyNode child)
        {
            if (child.Id.Equals(Id)) throw new InvalidOperationException("节点不能挂到自己下面");
            if (_children.Any(c => c.Id.Equals(child.Id))) return;
            child.ParentId = Id;
            _children.Add(child);
        }

        public bool Detach(EditorObjectId childId)
        {
            var index = _children.FindIndex(c => c.Id.Equals(childId));
            if (index < 0) return false;
            _children[index].ParentId = null;
            _children.RemoveAt(index);
            return true;
        }

        public IEnumerable<SceneHierarchyNode> WalkDepthFirst()
        {
            yield return this;
            foreach (var child in _children)
            foreach (var node in child.WalkDepthFirst())
                yield return node;
        }
    }

    public sealed class SceneHierarchyModel
    {
        private readonly Dictionary<EditorObjectId, SceneHierarchyNode> _nodes = new();
        private readonly List<EditorObjectId> _roots = new();
        public IReadOnlyCollection<SceneHierarchyNode> Nodes => _nodes.Values;

        public SceneHierarchyNode CreateRoot(string name)
        {
            var node = new SceneHierarchyNode(EditorObjectId.NewId(), name);
            _nodes[node.Id] = node;
            _roots.Add(node.Id);
            return node;
        }

        public bool TryGet(EditorObjectId id, out SceneHierarchyNode node)
        {
            if (_nodes.TryGetValue(id, out var found))
            {
                node = found;
                return true;
            }

            node = null!;
            return false;
        }

        public void Parent(EditorObjectId childId, EditorObjectId parentId)
        {
            if (!_nodes.TryGetValue(childId, out var child)) throw new KeyNotFoundException("找不到子节点");
            if (!_nodes.TryGetValue(parentId, out var parent)) throw new KeyNotFoundException("找不到父节点");
            if (child.WalkDepthFirst().Any(n => n.Id.Equals(parentId))) throw new InvalidOperationException("不能形成循环层级");
            foreach (var node in _nodes.Values) node.Detach(childId);
            _roots.Remove(childId);
            parent.Attach(child);
        }

        public IReadOnlyList<SceneHierarchyNode> Roots()
        {
            var list = new List<SceneHierarchyNode>();
            foreach (var id in _roots)
                if (_nodes.TryGetValue(id, out var node))
                    list.Add(node);
            return list;
        }

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            var seen = new HashSet<EditorObjectId>();
            foreach (var root in Roots())
            {
                foreach (var node in root.WalkDepthFirst())
                {
                    if (!seen.Add(node.Id)) errors.Add($"节点重复出现在层级中：{node.Name}");
                    if (string.IsNullOrWhiteSpace(node.Name)) errors.Add($"节点 {node.Id} 名称为空");
                }
            }
            foreach (var node in _nodes.Values)
                if (!seen.Contains(node.Id)) errors.Add($"节点游离在层级外：{node.Name}");
            return errors;
        }
    }

    public sealed class InspectorProperty
    {
        public string Path { get; }
        public Type ValueType { get; }
        public object? Value { get; private set; }
        public bool IsDirty { get; private set; }

        public InspectorProperty(string path, Type valueType, object? value)
        {
            Path = path;
            ValueType = valueType;
            Value = value;
        }

        public bool TrySet(object? value, out string error)
        {
            error = "";
            if (value != null && !ValueType.IsInstanceOfType(value))
            {
                error = $"属性 {Path} 需要 {ValueType.Name}，收到 {value.GetType().Name}";
                return false;
            }
            if (Equals(Value, value)) return true;
            Value = value;
            IsDirty = true;
            return true;
        }

        public void Accept() => IsDirty = false;
    }

    public sealed class InspectorSession
    {
        private readonly Dictionary<string, InspectorProperty> _properties = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, InspectorProperty> Properties => _properties;

        public InspectorProperty Bind(string path, Type type, object? value)
        {
            var property = new InspectorProperty(path, type, value);
            _properties[path] = property;
            return property;
        }

        public IReadOnlyList<string> Apply(Dictionary<string, object?> patch)
        {
            var errors = new List<string>();
            foreach (var (path, value) in patch)
            {
                if (!_properties.TryGetValue(path, out var prop))
                {
                    errors.Add($"Inspector 没有绑定属性：{path}");
                    continue;
                }
                if (!prop.TrySet(value, out var error))
                    errors.Add(error);
            }
            return errors;
        }
    }

    public sealed class EditorWorkspace
    {
        public SceneHierarchyModel Hierarchy { get; } = new();
        public EditorSelectionSet Selection { get; } = new();
        public InspectorSession Inspector { get; } = new();
        public EditorUndoStack Undo { get; } = new();
        public Dictionary<string, object> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

        public T GetOrCreateService<T>(Func<T> factory) where T : notnull
        {
            var key = typeof(T).FullName ?? typeof(T).Name;
            if (Services.TryGetValue(key, out var value) && value is T typed) return typed;
            var created = factory();
            Services[key] = created;
            return created;
        }
    }

    public sealed class RenameNodeCommand : IEditorCommand
    {
        private readonly EditorObjectId _id;
        private readonly string _newName;
        private string _oldName = "";
        public string Name => "重命名节点";

        public RenameNodeCommand(EditorObjectId id, string newName)
        {
            _id = id;
            _newName = newName;
        }

        public bool CanMergeWith(IEditorCommand next) => next is RenameNodeCommand r && r._id.Equals(_id);

        public void Execute(EditorWorkspace workspace)
        {
            if (!workspace.Hierarchy.TryGet(_id, out var node)) throw new KeyNotFoundException("节点不存在");
            _oldName = node.Name;
            node.Rename(_newName);
        }

        public void Undo(EditorWorkspace workspace)
        {
            if (workspace.Hierarchy.TryGet(_id, out var node)) node.Rename(_oldName);
        }
    }

    public sealed class EditorCommandRouter
    {
        private readonly Dictionary<string, Action<EditorWorkspace, string[]>> _commands = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string name, Action<EditorWorkspace, string[]> handler)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("命令名不能为空");
            _commands[name.Trim()] = handler;
        }

        public bool Dispatch(EditorWorkspace workspace, string commandLine)
        {
            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            if (!_commands.TryGetValue(parts[0], out var handler)) return false;
            handler(workspace, parts.Skip(1).ToArray());
            return true;
        }

        public static EditorCommandRouter CreateDefault()
        {
            var router = new EditorCommandRouter();
            router.Register("select", (ws, args) =>
            {
                var ids = args.Select(a => Guid.TryParse(a, out var g) ? new EditorObjectId(g) : default)
                              .Where(id => id.Value != Guid.Empty);
                ws.Selection.Replace(ids);
            });
            router.Register("create", (ws, args) =>
            {
                var name = args.Length == 0 ? "GameObject" : string.Join(' ', args);
                ws.Hierarchy.CreateRoot(name);
            });
            return router;
        }
    }
}
