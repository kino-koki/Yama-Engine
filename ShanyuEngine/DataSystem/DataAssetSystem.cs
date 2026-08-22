using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ShanyuEngine.DataSystem
{
    // ====================================================================
    // 数据资产系统（DataAssetSystem）
    // ====================================================================
    // 类似 UE UDataAsset，提供可序列化的数据容器、类型安全的数据资产、
    // 全局注册表、弱引用、加载管理、以及编辑器标记。
    // ====================================================================

    /// <summary>
    /// 数据资产分类标记。附加到 DataAsset 子类上，用于编辑器分类和组织。
    /// 用法：[DataAssetCategory("Gameplay/Weapons")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class DataAssetCategoryAttribute : Attribute
    {
        public string Category { get; }
        public string? Description { get; set; }

        public DataAssetCategoryAttribute(string category)
        {
            Category = category ?? throw new ArgumentNullException(nameof(category));
        }
    }

    /// <summary>
    /// 数据资产显示名称标记。在编辑器中替代类名显示。
    /// 用法：[DataAssetDisplayName("Weapon Data")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class DataAssetDisplayNameAttribute : Attribute
    {
        public string DisplayName { get; }

        public DataAssetDisplayNameAttribute(string displayName)
        {
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        }
    }

    /// <summary>
    /// 数据资产基类。所有可通过编辑器和运行时访问的数据容器均继承此类。
    /// 类似 UE UDataAsset，支持序列化、分类、标签、蓝图访问。
    /// </summary>
    public class DataAsset
    {
        /// <summary>资产唯一标识符</summary>
        public Guid AssetId { get; set; } = Guid.NewGuid();

        /// <summary>资产名称</summary>
        public string AssetName { get; set; } = "UnnamedDataAsset";

        /// <summary>资产文件路径（编辑器内使用）</summary>
        public string AssetPath { get; set; } = string.Empty;

        /// <summary>资产分类路径（如 "Gameplay/Weapons"）</summary>
        public string Category { get; set; } = "Misc";

        /// <summary>资产标签集合（用于编辑器搜索和过滤）</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>资产是否已加载到内存</summary>
        public bool IsLoaded { get; protected set; }

        /// <summary>资产最后修改时间</summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>加载状态变更事件</summary>
        public event Action<DataAsset>? OnLoaded;
        public event Action<DataAsset>? OnUnloaded;

        /// <summary>
        /// 获取此资产在编辑器中显示的名称。
        /// 优先使用 [DataAssetDisplayName] 标记，否则返回类名。
        /// </summary>
        public virtual string GetDisplayName()
        {
            var attr = GetType().GetCustomAttributes(typeof(DataAssetDisplayNameAttribute), false)
                .FirstOrDefault() as DataAssetDisplayNameAttribute;
            return attr?.DisplayName ?? GetType().Name;
        }

        /// <summary>
        /// 获取此资产的所有分类标记。
        /// </summary>
        public string[] GetCategories()
        {
            var attrs = GetType().GetCustomAttributes(typeof(DataAssetCategoryAttribute), false)
                .Cast<DataAssetCategoryAttribute>();
            if (!attrs.Any()) return new[] { Category };
            return attrs.Select(a => a.Category).ToArray();
        }

        /// <summary>加载资产（子类可重写以执行资源加载）</summary>
        public virtual void Load()
        {
            IsLoaded = true;
            OnLoaded?.Invoke(this);
        }

        /// <summary>卸载资产（释放内存中的资源）</summary>
        public virtual void Unload()
        {
            IsLoaded = false;
            OnUnloaded?.Invoke(this);
        }

        /// <summary>重置资产到默认状态</summary>
        public virtual void ResetToDefault() { }
    }

    /// <summary>
    /// 类型安全的数据资产泛型基类。
    /// 提供强类型访问和编辑器类型约束。
    /// 用法：public class WeaponData : DataAsset&lt;WeaponData&gt; { ... }
    /// </summary>
    public class DataAsset<T> : DataAsset where T : DataAsset<T>
    {
        /// <summary>深度复制此资产（创建一个全新的独立副本）</summary>
        public T DeepCopy()
        {
            var json = JsonSerializer.Serialize((object)this);
            return JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}");
        }
    }

    /// <summary>
    /// 数据资产弱引用。在编辑器中通过拖拽指定目标资产，
    /// 运行时按需懒加载。避免循环引用和内存浪费。
    /// </summary>
    public class DataAssetReference<T> where T : DataAsset
    {
        /// <summary>目标资产的 AssetId（序列化时保存此值）</summary>
        public Guid TargetAssetId { get; set; }

        /// <summary>运行时缓存的目标资产实例（懒加载后持有）</summary>
        private T? _cachedAsset;

        /// <summary>是否已加载目标资产</summary>
        public bool IsLoaded => _cachedAsset != null && _cachedAsset.IsLoaded;

        /// <summary>编辑器中显示的路径（用于拖拽时的视觉反馈）</summary>
        public string? EditorHintPath { get; set; }

        /// <summary>
        /// 获取目标资产。首次访问时通过 DataAssetManager 懒加载。
        /// </summary>
        public T? Resolve()
        {
            if (_cachedAsset != null) return _cachedAsset;
            if (TargetAssetId == Guid.Empty) return null;
            _cachedAsset = DataAssetManager.Instance?.GetAsset<T>(TargetAssetId);
            return _cachedAsset;
        }

        /// <summary>重置缓存（用于热重载时刷新引用）</summary>
        public void ResetCache()
        {
            _cachedAsset = null;
        }

        /// <summary>是否设置了有效引用</summary>
        public bool IsValid => TargetAssetId != Guid.Empty;

        public static implicit operator bool(DataAssetReference<T> reference) => reference.IsValid;
    }

    /// <summary>
    /// 数据资产全局注册表。按类型、按名称、按标签查找已注册的数据资产。
    /// 管理所有 DataAsset 实例的生命周期。
    /// </summary>
    public class DataAssetRegistry
    {
        private readonly Dictionary<Guid, DataAsset> _byId = new();
        private readonly Dictionary<Type, List<DataAsset>> _byType = new();
        private readonly Dictionary<string, DataAsset> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DataAsset>> _byTag = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DataAsset>> _byCategory = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>已注册的资产总数</summary>
        public int Count => _byId.Count;

        /// <summary>注册一个数据资产到全局注册表</summary>
        public void Register(DataAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (_byId.ContainsKey(asset.AssetId))
                throw new InvalidOperationException($"Duplicate DataAsset id: {asset.AssetId}");

            _byId[asset.AssetId] = asset;

            if (!_byType.TryGetValue(asset.GetType(), out var typeList))
            {
                typeList = new List<DataAsset>();
                _byType[asset.GetType()] = typeList;
            }
            typeList.Add(asset);

            _byName[asset.AssetName] = asset;

            foreach (var tag in asset.Tags)
            {
                if (!_byTag.TryGetValue(tag, out var tagList))
                {
                    tagList = new List<DataAsset>();
                    _byTag[tag] = tagList;
                }
                tagList.Add(asset);
            }

            foreach (var cat in asset.GetCategories())
            {
                if (!_byCategory.TryGetValue(cat, out var catList))
                {
                    catList = new List<DataAsset>();
                    _byCategory[cat] = catList;
                }
                catList.Add(asset);
            }
        }

        /// <summary>取消注册一个数据资产</summary>
        public bool Unregister(DataAsset asset)
        {
            if (asset == null) return false;
            if (!_byId.Remove(asset.AssetId)) return false;

            if (_byType.TryGetValue(asset.GetType(), out var typeList))
                typeList.Remove(asset);

            _byName.Remove(asset.AssetName);

            foreach (var tag in asset.Tags)
            {
                if (_byTag.TryGetValue(tag, out var tagList))
                    tagList.Remove(asset);
            }

            foreach (var cat in asset.GetCategories())
            {
                if (_byCategory.TryGetValue(cat, out var catList))
                    catList.Remove(asset);
            }

            return true;
        }

        /// <summary>按 ID 查找资产</summary>
        public DataAsset? GetById(Guid id) =>
            _byId.TryGetValue(id, out var asset) ? asset : null;

        /// <summary>按 ID 查找类型安全的资产</summary>
        public T? GetById<T>(Guid id) where T : DataAsset =>
            GetById(id) as T;

        /// <summary>按名称查找资产</summary>
        public DataAsset? GetByName(string name) =>
            _byName.TryGetValue(name, out var asset) ? asset : null;

        /// <summary>按名称查找类型安全的资产</summary>
        public T? GetByName<T>(string name) where T : DataAsset =>
            GetByName(name) as T;

        /// <summary>获取指定类型的所有资产</summary>
        public List<T> GetAllOfType<T>() where T : DataAsset
        {
            var result = new List<T>();
            foreach (var kvp in _byType)
            {
                if (typeof(T).IsAssignableFrom(kvp.Key))
                {
                    foreach (var asset in kvp.Value)
                        if (asset is T typed) result.Add(typed);
                }
            }
            return result;
        }

        /// <summary>获取指定标签下的所有资产</summary>
        public List<DataAsset> GetByTag(string tag)
        {
            if (_byTag.TryGetValue(tag, out var list)) return new List<DataAsset>(list);
            return new List<DataAsset>();
        }

        /// <summary>获取指定分类下的所有资产</summary>
        public List<DataAsset> GetByCategory(string category)
        {
            if (_byCategory.TryGetValue(category, out var list)) return new List<DataAsset>(list);
            return new List<DataAsset>();
        }

        /// <summary>获取所有已注册资产</summary>
        public IReadOnlyList<DataAsset> GetAll() =>
            _byId.Values.ToList();

        /// <summary>清空注册表</summary>
        public void Clear()
        {
            _byId.Clear();
            _byType.Clear();
            _byName.Clear();
            _byTag.Clear();
            _byCategory.Clear();
        }
    }

    /// <summary>
    /// 数据资产管理器。单例模式，管理数据资产的加载、卸载、热重载和引用追踪。
    /// </summary>
    public class DataAssetManager
    {
        private static DataAssetManager? _instance;
        /// <summary>全局单例</summary>
        public static DataAssetManager? Instance => _instance;

        private readonly DataAssetRegistry _registry = new();
        private readonly Dictionary<Guid, int> _refCounts = new();
        private readonly Dictionary<Guid, string> _assetFilePaths = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>注册表（提供查找能力）</summary>
        public DataAssetRegistry Registry => _registry;

        /// <summary>资产数量</summary>
        public int AssetCount => _registry.Count;

        /// <summary>资产加载事件</summary>
        public event Action<DataAsset>? OnAssetLoaded;
        /// <summary>资产卸载事件</summary>
        public event Action<DataAsset>? OnAssetUnloaded;
        /// <summary>资产热重载事件</summary>
        public event Action<DataAsset>? OnAssetReloaded;

        public DataAssetManager()
        {
            _instance = this;
        }

        /// <summary>初始化管理器</summary>
        public static void Initialize()
        {
            if (_instance == null) _instance = new DataAssetManager();
        }

        /// <summary>关闭管理器，卸载所有资产</summary>
        public void Shutdown()
        {
            UnloadAll();
            _registry.Clear();
            _refCounts.Clear();
            _assetFilePaths.Clear();
            _instance = null;
        }

        /// <summary>注册一个数据资产</summary>
        public void RegisterAsset(DataAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            _registry.Register(asset);
            asset.OnLoaded += OnAssetLoadedHandler;
        }

        /// <summary>取消注册一个数据资产</summary>
        public bool UnregisterAsset(DataAsset asset)
        {
            if (asset == null) return false;
            asset.OnLoaded -= OnAssetLoadedHandler;
            return _registry.Unregister(asset);
        }

        /// <summary>按 ID 获取资产（类型安全）</summary>
        public T? GetAsset<T>(Guid id) where T : DataAsset =>
            _registry.GetById<T>(id);

        /// <summary>按名称获取资产（类型安全）</summary>
        public T? GetAsset<T>(string name) where T : DataAsset =>
            _registry.GetByName<T>(name);

        /// <summary>获取资产（非泛型）</summary>
        public DataAsset? GetAsset(Guid id) =>
            _registry.GetById(id);

        /// <summary>增加引用计数</summary>
        public void AddReference(Guid assetId)
        {
            _refCounts.TryGetValue(assetId, out int count);
            _refCounts[assetId] = count + 1;
        }

        /// <summary>减少引用计数。当计数归零时自动卸载资产。</summary>
        public void ReleaseReference(Guid assetId)
        {
            if (!_refCounts.TryGetValue(assetId, out int count)) return;
            count--;
            if (count <= 0)
            {
                _refCounts.Remove(assetId);
                var asset = _registry.GetById(assetId);
                if (asset != null)
                {
                    asset.Unload();
                    OnAssetUnloaded?.Invoke(asset);
                }
            }
            else
            {
                _refCounts[assetId] = count;
            }
        }

        /// <summary>获取引用计数</summary>
        public int GetReferenceCount(Guid assetId) =>
            _refCounts.TryGetValue(assetId, out int count) ? count : 0;

        /// <summary>加载资产（若尚未加载）</summary>
        public void LoadAsset(DataAsset asset)
        {
            if (asset == null) return;
            if (!asset.IsLoaded)
            {
                asset.Load();
                AddReference(asset.AssetId);
            }
        }

        /// <summary>从 JSON 文件加载并注册数据资产</summary>
        public T? LoadFromFile<T>(string filePath) where T : DataAsset, new()
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                var json = File.ReadAllText(filePath);
                var asset = JsonSerializer.Deserialize<T>(json, _jsonOptions);
                if (asset == null) return null;
                asset.AssetPath = filePath;
                asset.LastModified = File.GetLastWriteTimeUtc(filePath);
                RegisterAsset(asset);
                asset.Load();
                _assetFilePaths[asset.AssetId] = filePath;
                return asset;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load DataAsset from {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>保存数据资产到 JSON 文件</summary>
        public bool SaveToFile(DataAsset asset, string filePath)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            try
            {
                var json = JsonSerializer.Serialize(asset, asset.GetType(), _jsonOptions);
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, json);
                asset.AssetPath = filePath;
                asset.LastModified = DateTime.UtcNow;
                _assetFilePaths[asset.AssetId] = filePath;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save DataAsset to {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>热重载资产（从磁盘重新读取）</summary>
        public bool ReloadAsset(Guid assetId)
        {
            if (!_assetFilePaths.TryGetValue(assetId, out var filePath)) return false;
            if (!File.Exists(filePath)) return false;

            var asset = _registry.GetById(assetId);
            if (asset == null) return false;

            try
            {
                var json = File.ReadAllText(filePath);
                var type = asset.GetType();
                var reloaded = JsonSerializer.Deserialize(json, type, _jsonOptions) as DataAsset;
                if (reloaded == null) return false;

                // 保留原始 ID
                reloaded.AssetId = assetId;
                reloaded.AssetPath = filePath;
                reloaded.LastModified = File.GetLastWriteTimeUtc(filePath);

                // 先卸载旧资产
                asset.Unload();
                _registry.Unregister(asset);

                // 注册新资产
                _registry.Register(reloaded);
                reloaded.Load();
                OnAssetReloaded?.Invoke(reloaded);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to reload DataAsset {assetId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>卸载所有资产</summary>
        public void UnloadAll()
        {
            foreach (var asset in _registry.GetAll())
            {
                asset.Unload();
            }
            _refCounts.Clear();
        }

        private void OnAssetLoadedHandler(DataAsset asset) => OnAssetLoaded?.Invoke(asset);
    }
}
