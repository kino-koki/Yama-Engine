// =============================================================================
//  ResourceSaver.cs
//  yama引擎 - Godot 3 风格资源保存器（自定义资源序列化 + 持久化 round-trip）
//
//  对应 Godot 3 语义：
//    * ResourceSaver.save(resource, path)：把资源序列化写入存储
//    * ResourceFormatSaver：可插拔格式（按扩展名/类型选序列化器）
//    * 自定义资源（.tres/.res）：序列化器按 ResourceType 分派
//    * round-trip：save → load 得回等价资源（复用 GodotResourceLoader 加载）
// =============================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.UI
{
    /// <summary>可写存储后端（在 IResourceBackend 基础上加 Write，对应 Godot 文件系统写）。</summary>
    public interface IResourceStorageBackend : IResourceBackend
    {
        /// <summary>写入（覆盖）指定路径的内容。</summary>
        void Write(string path, string data);
    }

    /// <summary>资源序列化器（对应 Godot ResourceFormatSaver）。按 ResourceType 分派。</summary>
    public interface IResourceSerializer
    {
        /// <summary>本序列化器处理的资源类型名（对应 get_recognized_extensions/类型）。</summary>
        string ResourceType { get; }

        /// <summary>把资源序列化为可存储文本。</summary>
        string Serialize(Resource resource);
    }

    /// <summary>
    /// TextResource 序列化器。文本格式："TextResource|&lt;content&gt;"。
    /// 内容中的换行替换为转义序列，保证单行可解析。
    /// </summary>
    public sealed class TextResourceSerializer : IResourceSerializer
    {
        public string ResourceType => "TextResource";
        public const string Prefix = "TextResource|";

        public string Serialize(Resource resource)
        {
            var t = resource as TextResource
                ?? throw new ArgumentException($"Expected TextResource, got {resource.ResourceType}.");
            string escaped = (t.Text ?? string.Empty)
                .Replace("\r", "\\r").Replace("\n", "\\n");
            return Prefix + escaped;
        }

        /// <summary>从文本反序列化为 TextResource（供 backend.Load 使用）。</summary>
        public static TextResource? Deserialize(string data)
        {
            if (data == null || !data.StartsWith(Prefix, StringComparison.Ordinal))
                return null;
            string content = data[Prefix.Length..]
                .Replace("\\r", "\r").Replace("\\n", "\n");
            return new TextResource { Text = content };
        }
    }

    /// <summary>
    /// Godot 3 风格资源保存器。按 ResourceType 选序列化器写入存储后端。
    /// 与 GodotResourceLoader 共用存储后端即可实现 round-trip。
    /// </summary>
    public sealed class ResourceSaver
    {
        private readonly Dictionary<string, IResourceSerializer> _serializers =
            new(StringComparer.Ordinal);

        /// <summary>已注册序列化器数。</summary>
        public int SerializerCount => _serializers.Count;

        /// <summary>注册序列化器（按 ResourceType 索引，重名覆盖）。</summary>
        public void RegisterSerializer(IResourceSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            _serializers[serializer.ResourceType] = serializer;
        }

        /// <summary>是否已注册某类型的序列化器。</summary>
        public bool HasSerializer(string resourceType) =>
            !string.IsNullOrEmpty(resourceType) && _serializers.ContainsKey(resourceType);

        /// <summary>
        /// 保存资源（对应 Godot ResourceSaver.save）。
        /// 按 resource.ResourceType 选序列化器，序列化后写入 backend。
        /// 未注册类型抛 InvalidOperationException。
        /// </summary>
        public void Save(Resource resource, string path, IResourceStorageBackend backend)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (backend == null) throw new ArgumentNullException(nameof(backend));

            if (!_serializers.TryGetValue(resource.ResourceType, out var serializer))
                throw new InvalidOperationException(
                    $"No serializer registered for resource type '{resource.ResourceType}'.");

            string data = serializer.Serialize(resource);
            backend.Write(path, data);
            resource.ResourcePath = path;
        }
    }
}
