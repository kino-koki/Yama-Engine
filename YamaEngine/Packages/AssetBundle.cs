using System;
using System.Collections.Generic;

namespace YamaEngine.Packages
{
    /// <summary>资源类型（运行时加载类别）。</summary>
    public enum AssetType
    {
        Texture = 0,
        Sprite = 1,
        Mesh = 2,
        Material = 3,
        Shader = 4,
        Audio = 5,
        Scene = 6,
        Prefab = 7,
        Font = 8,
        Data = 9,
        Script = 10,
        Other = 11,
    }

    /// <summary>
    /// 资源引用：一个已加载（或可加载）的独立资源。
    /// 纯数据层：Handle 为不透明句柄（如 RHI 纹理指针 / 资源对象），
    /// 由平台加载器填充，引擎层只负责编目与按名查找。
    /// </summary>
    public sealed class AssetRef
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public AssetType Type { get; set; } = AssetType.Other;

        /// <summary>不透明加载句柄（可为空，表示尚未加载）。</summary>
        public object? Handle { get; set; }

        /// <summary>所属包/集名。</summary>
        public string? SourceBundle { get; set; }

        /// <summary>是否已加载。</summary>
        public bool IsLoaded => Handle != null;
    }

    /// <summary>
    /// 资源包：一组编目的资源，支持按名/按类型加载与查找。
    /// 纯逻辑层；加载由传入的 loader 委托完成，避免直接 IO 耦合。
    /// </summary>
    public sealed class AssetBundle
    {
        /// <summary>包名。</summary>
        public string Name { get; set; } = "Bundle";

        /// <summary>包版本。</summary>
        public string Version { get; set; } = "0.0.0";

        private readonly Dictionary<string, AssetRef> _assets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AssetType, List<string>> _byType = new();

        public int Count => _assets.Count;
        public IReadOnlyCollection<string> AssetNames => _assets.Keys;

        /// <summary>注册一个资源（按 Name 唯一）。</summary>
        public void Register(AssetRef asset)
        {
            if (asset == null || string.IsNullOrEmpty(asset.Name))
                return;
            asset.SourceBundle ??= Name;
            _assets[asset.Name] = asset;
            if (!_byType.TryGetValue(asset.Type, out var list))
                _byType[asset.Type] = list = new List<string>();
            list.Add(asset.Name);
        }

        public bool Contains(string name) => _assets.ContainsKey(name);

        public AssetRef? Get(string name)
            => _assets.TryGetValue(name, out var r) ? r : null;

        /// <summary>加载资源为指定类型。</summary>
        public T? Load<T>(string name) where T : class
        {
            if (!_assets.TryGetValue(name, out var r) || r.Handle is T t)
                return t;
            return null;
        }

        /// <summary>
        /// 加载一个资源：若未加载则调用 loader 生成句柄。
        /// </summary>
        public T? LoadOrCreate<T>(string name, Func<AssetRef, T>? loader = null) where T : class
        {
            if (!_assets.TryGetValue(name, out var r))
                return null;
            if (r.Handle is T t)
                return t;
            if (loader != null)
            {
                var created = loader(r);
                if (created != null)
                {
                    r.Handle = created;
                    return created;
                }
            }
            return null;
        }

        public IEnumerable<AssetRef> LoadAll(AssetType type)
        {
            if (!_byType.TryGetValue(type, out var list))
                yield break;
            foreach (var n in list)
            {
                if (_assets.TryGetValue(n, out var r))
                    yield return r;
            }
        }

        public void Clear()
        {
            _assets.Clear();
            _byType.Clear();
        }
    }

    /// <summary>
    /// 资源包集合：聚合多个 AssetBundle，跨包解析与依赖。
    /// 纯逻辑层。
    /// </summary>
    public sealed class AssetPackage
    {
        public string Id { get; set; } = "package";
        public PackageVersion? Version { get; set; }
        public string Description { get; set; } = string.Empty;

        private readonly Dictionary<string, AssetBundle> _bundles = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> BundleNames => _bundles.Keys;
        public int BundleCount => _bundles.Count;

        public void AddBundle(AssetBundle bundle) => _bundles[bundle.Name] = bundle;

        public AssetBundle? GetBundle(string name)
            => _bundles.TryGetValue(name, out var b) ? b : null;

        /// <summary>
        /// 跨包按名解析资源（按 bundle 名，再全局首匹配）。
        /// </summary>
        public AssetRef? Resolve(string name, string? bundleName = null)
        {
            if (bundleName != null && _bundles.TryGetValue(bundleName, out var b))
                return b.Get(name);
            foreach (var b in _bundles.Values)
            {
                var r = b.Get(name);
                if (r != null)
                    return r;
            }
            return null;
        }

        public IEnumerable<AssetRef> ResolveAll(AssetType type)
        {
            foreach (var b in _bundles.Values)
            {
                foreach (var r in b.LoadAll(type))
                    yield return r;
            }
        }

        /// <summary>清空所有包（释放句柄由调用方负责）。</summary>
        public void Clear() => _bundles.Clear();
    }
}
