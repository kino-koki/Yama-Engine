using System;
using System.Collections.Generic;

namespace YamaEngine.Scripting.Abi
{
    public sealed class ScriptDescriptor
    {
        public string TypeName { get; }
        public Func<ScriptBinding, object> Factory { get; }
        public ScriptDescriptor(string typeName, Func<ScriptBinding, object> factory)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }
    }

    /// <summary>类型名 → 工厂，供场景反序列化时按名还原脚本实例。</summary>
    public sealed class ScriptRegistry
    {
        private readonly Dictionary<string, ScriptDescriptor> _map = new Dictionary<string, ScriptDescriptor>(StringComparer.Ordinal);

        public bool Register(string typeName, Func<ScriptBinding, object> factory)
        {
            if (string.IsNullOrEmpty(typeName) || factory == null) return false;
            _map[typeName] = new ScriptDescriptor(typeName, factory);
            return true;
        }

        public ScriptDescriptor? Find(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            return _map.TryGetValue(typeName, out var d) ? d : null;
        }

        public object Create(string typeName, ScriptBinding binding)
        {
            var descriptor = Find(typeName);
            if (descriptor == null) throw new KeyNotFoundException("Script type is not registered: " + typeName);
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            return descriptor.Factory(binding);
        }

        public IReadOnlyList<string> TypeNames => new List<string>(_map.Keys).AsReadOnly();
        public int Count => _map.Count;
    }
}
