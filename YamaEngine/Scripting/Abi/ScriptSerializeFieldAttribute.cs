using System;

namespace YamaEngine.Scripting.Abi
{
    /// <summary>标记热重载前后必须迁移的字段/属性（对齐 Godot 的导出字段语义）。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class ScriptSerializeFieldAttribute : Attribute
    {
        public string Key { get; }
        public ScriptSerializeFieldAttribute(string? key = null) { Key = key ?? string.Empty; }
    }
}
