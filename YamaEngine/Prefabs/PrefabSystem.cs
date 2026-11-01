using System.Numerics;
using System.Text.Json;
using YamaEngine.Assets;
using System.Linq;

namespace YamaEngine.Prefabs
{
    public sealed class PrefabAsset
    {
        public int Version { get; set; } = 1;
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Prefab";
        public PrefabObject Root { get; set; } = new();
        /// <summary>父预制体 GUID。若非空, 则此预制体为变体 (Variant), 继承父预制体并叠加自身覆盖。</summary>
        public string? ParentPrefabGuid { get; set; }
        /// <summary>变体覆盖列表 (仅当 ParentPrefabGuid 非空时使用)。</summary>
        public List<PrefabOverride> VariantOverrides { get; set; } = new();
    }

    public sealed class PrefabObject
    {
        public string Name { get; set; } = "GameObject";
        public bool IsVisible { get; set; } = true;
        public int Layer { get; set; }
        public Vector3Record Position { get; set; } = Vector3Record.Zero;
        public QuaternionRecord Rotation { get; set; } = QuaternionRecord.Identity;
        public Vector3Record Scale { get; set; } = Vector3Record.One;
        public string MeshAssetPath { get; set; } = string.Empty;
        public string MaterialAssetPath { get; set; } = string.Empty;
        public Vector3Record BaseColor { get; set; } = Vector3Record.One;
        public float Metallic { get; set; }
        public float Roughness { get; set; } = 0.5f;
        public bool CastShadow { get; set; } = true;
        public bool ReceiveShadow { get; set; } = true;
        /// <summary>子对象列表 (支持嵌套预制体)。</summary>
        public List<PrefabObject> Children { get; set; } = new();
    }

    /// <summary>预制体属性覆盖: 记录被修改的属性路径和值。</summary>
    public sealed class PrefabOverride
    {
        /// <summary>属性路径, 格式 "Root.Child[0].Position"。</summary>
        public string PropertyPath { get; set; } = string.Empty;
        /// <summary>序列化后的属性值 (JSON)。</summary>
        public string SerializedValue { get; set; } = string.Empty;
    }

    /// <summary>预制体实例数据: 记录源预制体 GUID 和实例级覆盖。</summary>
    public sealed class PrefabInstanceData
    {
        public string SourcePrefabGuid { get; set; } = string.Empty;
        public List<PrefabOverride> Overrides { get; set; } = new();
        public System.Guid InstanceId { get; set; } = System.Guid.NewGuid();
    }

    public readonly record struct Vector3Record(float X, float Y, float Z)
    {
        public static Vector3Record Zero => new(0, 0, 0);
        public static Vector3Record One => new(1, 1, 1);
        public static Vector3Record From(Vector3 value) => new(value.X, value.Y, value.Z);
        public Vector3 ToVector3() => new(X, Y, Z);
    }

    public readonly record struct QuaternionRecord(float X, float Y, float Z, float W)
    {
        public static QuaternionRecord Identity => new(0, 0, 0, 1);
        public static QuaternionRecord From(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
        public Quaternion ToQuaternion() => new(X, Y, Z, W == 0 ? 1 : W);
    }

    public sealed class PrefabSystem
    {
        private readonly string _projectRoot;
        private readonly AssetDatabase _assets;
        /// <summary>实例注册表: 源预制体 GUID → 实例数据列表。</summary>
        private readonly Dictionary<string, List<PrefabInstanceData>> _instances = new();
        /// <summary>实例 ID → 场景对象弱引用 (用于传播更新)。</summary>
        private readonly Dictionary<System.Guid, SceneObject> _instanceObjects = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public PrefabSystem(string projectRoot, AssetDatabase assets)
        {
            _projectRoot = projectRoot;
            _assets = assets;
        }

        public PrefabAsset SavePrefab(SceneObject source, string assetPath)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(assetPath)) throw new ArgumentException("Prefab asset path is required.", nameof(assetPath));

            assetPath = NormalizeAssetPath(assetPath);
            var prefabPath = Path.Combine(_projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath)!);

            var prefab = new PrefabAsset
            {
                Name = source.Name,
                Root = FromSceneObject(source)
            };

            File.WriteAllText(prefabPath, JsonSerializer.Serialize(prefab, JsonOptions));
            new AssetMeta
            {
                Guid = prefab.Guid,
                AssetPath = assetPath,
                Type = AssetType.Prefab.ToString(),
                ImportedAtUtc = DateTime.UtcNow,
                SourceHash = prefab.Guid
            }.Write(prefabPath + ".shmeta");
            _assets.RegisterAsset(assetPath, Guid.Parse(prefab.Guid), AssetType.Prefab, prefab.Name);

            return prefab;
        }

        public SceneObject InstantiatePrefab(string guid, Vector3 position)
        {
            var prefabPath = FindPrefabByGuid(guid)
                             ?? throw new FileNotFoundException($"Prefab GUID not found: {guid}");
            var prefab = JsonSerializer.Deserialize<PrefabAsset>(File.ReadAllText(prefabPath), JsonOptions)
                         ?? throw new InvalidOperationException($"Invalid prefab file: {prefabPath}");

            var instance = ToSceneObject(prefab.Root);
            instance.Id = Guid.NewGuid();
            instance.Position = position;
            instance.LocalPosition = position;
            instance.PrefabSourceGuid = prefab.Guid;
            return instance;
        }

        // ──────────────────────────────────────────────────────────────
        // 覆盖传播 (Override Propagation)
        // ──────────────────────────────────────────────────────────────

        /// <summary>实例化预制体并注册到实例表, 支持后续覆盖传播。</summary>
        public SceneObject InstantiatePrefabTracked(string guid, Vector3 position)
        {
            var instance = InstantiatePrefab(guid, position);

            var data = new PrefabInstanceData { SourcePrefabGuid = guid };
            if (!_instances.ContainsKey(guid))
                _instances[guid] = new List<PrefabInstanceData>();
            _instances[guid].Add(data);
            _instanceObjects[instance.Id] = instance;

            return instance;
        }

        /// <summary>记录实例上的属性覆盖 (当用户修改实例属性时调用)。</summary>
        public void RecordOverride(System.Guid instanceId, string propertyPath, object value)
        {
            if (!_instanceObjects.TryGetValue(instanceId, out var obj)) return;
            var prefabGuid = obj.PrefabSourceGuid;
            if (string.IsNullOrEmpty(prefabGuid)) return;

            if (!_instances.TryGetValue(prefabGuid, out var list)) return;
            var data = list.FirstOrDefault(d => d.InstanceId == instanceId);
            if (data == null) return;

            // 移除同路径的旧覆盖
            data.Overrides.RemoveAll(o => o.PropertyPath == propertyPath);
            data.Overrides.Add(new PrefabOverride
            {
                PropertyPath = propertyPath,
                SerializedValue = JsonSerializer.Serialize(value, JsonOptions)
            });
        }

        /// <summary>撤销某个属性的覆盖, 恢复为源预制体的值。</summary>
        public void RevertOverride(System.Guid instanceId, string propertyPath)
        {
            if (!_instanceObjects.TryGetValue(instanceId, out var obj)) return;
            var prefabGuid = obj.PrefabSourceGuid;
            if (string.IsNullOrEmpty(prefabGuid) || !_instances.TryGetValue(prefabGuid, out var list)) return;

            var data = list.FirstOrDefault(d => d.InstanceId == instanceId);
            if (data == null) return;

            data.Overrides.RemoveAll(o => o.PropertyPath == propertyPath);

            // 重新从源预制体加载该属性
            var prefab = LoadPrefab(prefabGuid);
            if (prefab == null) return;
            var fresh = ToSceneObject(prefab.Root);
            ApplyPropertyToSceneObject(obj, propertyPath, GetPropertyFromSceneObject(fresh, propertyPath));
        }

        /// <summary>
        /// 当源预制体被修改后, 将变更传播到所有已注册的实例。
        /// 实例上的覆盖会被保留并叠加到新的预制体数据之上。
        /// </summary>
        public int PropagatePrefabChanges(string prefabGuid)
        {
            if (!_instances.TryGetValue(prefabGuid, out var list)) return 0;
            var prefab = LoadPrefab(prefabGuid);
            if (prefab == null) return 0;

            int updated = 0;
            foreach (var data in list)
            {
                if (!_instanceObjects.TryGetValue(data.InstanceId, out var obj)) continue;

                // 重新从源预制体构建对象 (获取最新数据)
                var fresh = ToSceneObject(prefab.Root);
                // 保留实例的 ID 和位置
                fresh.Id = obj.Id;
                fresh.PrefabSourceGuid = obj.PrefabSourceGuid;

                // 应用实例覆盖
                foreach (var ov in data.Overrides)
                {
                    var value = JsonSerializer.Deserialize<object>(ov.SerializedValue, JsonOptions);
                    ApplyPropertyToSceneObject(fresh, ov.PropertyPath, value);
                }

                // 将更新后的数据拷贝回实例对象
                CopySceneObjectData(fresh, obj);
                updated++;
            }
            return updated;
        }

        /// <summary>获取预制体的所有已注册实例。</summary>
        public IReadOnlyList<PrefabInstanceData> GetInstances(string prefabGuid)
        {
            if (_instances.TryGetValue(prefabGuid, out var list))
                return list.AsReadOnly();
            return Array.Empty<PrefabInstanceData>();
        }

        // ──────────────────────────────────────────────────────────────
        // 嵌套预制体 (Nested Prefabs)
        // ──────────────────────────────────────────────────────────────

        /// <summary>将一个预制体作为子对象嵌套到另一个场景对象中。</summary>
        public SceneObject AddNestedPrefab(SceneObject parent, string childPrefabGuid, Vector3 localPosition)
        {
            var child = InstantiatePrefab(childPrefabGuid, localPosition);
            child.LocalPosition = localPosition;
            parent.Children.Add(child);
            return child;
        }

        // ──────────────────────────────────────────────────────────────
        // 变体 (Variants)
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 基于父预制体创建变体。变体继承父预制体的所有属性, 并叠加自身的覆盖。
        /// </summary>
        public PrefabAsset CreateVariant(string parentPrefabGuid, string variantAssetPath, string variantName)
        {
            var parentPrefab = LoadPrefab(parentPrefabGuid)
                ?? throw new FileNotFoundException($"Parent prefab not found: {parentPrefabGuid}");

            var variant = new PrefabAsset
            {
                Name = variantName,
                ParentPrefabGuid = parentPrefabGuid,
                Root = DeepCopy(parentPrefab.Root)
            };

            variantAssetPath = NormalizeAssetPath(variantAssetPath);
            var variantPath = Path.Combine(_projectRoot, variantAssetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(variantPath)!);
            File.WriteAllText(variantPath, JsonSerializer.Serialize(variant, JsonOptions));

            new AssetMeta
            {
                Guid = variant.Guid,
                AssetPath = variantAssetPath,
                Type = AssetType.Prefab.ToString(),
                ImportedAtUtc = DateTime.UtcNow,
                SourceHash = variant.Guid
            }.Write(variantPath + ".shmeta");
            _assets.RegisterAsset(variantAssetPath, Guid.Parse(variant.Guid), AssetType.Prefab, variant.Name);

            return variant;
        }

        /// <summary>向变体添加一条覆盖。</summary>
        public void AddVariantOverride(string variantGuid, string propertyPath, object value)
        {
            var variant = LoadPrefab(variantGuid);
            if (variant == null) return;
            if (string.IsNullOrEmpty(variant.ParentPrefabGuid))
                throw new InvalidOperationException("Can only add variant overrides to a variant prefab.");

            variant.VariantOverrides.RemoveAll(o => o.PropertyPath == propertyPath);
            variant.VariantOverrides.Add(new PrefabOverride
            {
                PropertyPath = propertyPath,
                SerializedValue = JsonSerializer.Serialize(value, JsonOptions)
            });

            // 保存更新后的变体
            var variantPath = FindPrefabByGuid(variantGuid);
            if (variantPath != null)
                File.WriteAllText(variantPath, JsonSerializer.Serialize(variant, JsonOptions));
        }

        /// <summary>
        /// 解析预制体: 若为变体, 则递归加载父链并应用变体覆盖, 返回最终合并后的预制体。
        /// </summary>
        public PrefabAsset? ResolvePrefab(string guid)
        {
            var prefab = LoadPrefab(guid);
            if (prefab == null) return null;

            // 若无父预制体, 直接返回
            if (string.IsNullOrEmpty(prefab.ParentPrefabGuid))
                return prefab;

            // 递归解析父预制体
            var parent = ResolvePrefab(prefab.ParentPrefabGuid);
            if (parent == null) return prefab;

            // 深拷贝父预制体, 然后应用变体覆盖
            var resolved = new PrefabAsset
            {
                Guid = prefab.Guid,
                Name = prefab.Name,
                ParentPrefabGuid = prefab.ParentPrefabGuid,
                Root = DeepCopy(parent.Root)
            };

            // 应用变体自身的 Root 属性 (覆盖父)
            ApplyPrefabOverrides(resolved.Root, prefab.Root);
            // 应用属性路径覆盖
            foreach (var ov in prefab.VariantOverrides)
            {
                var value = JsonSerializer.Deserialize<object>(ov.SerializedValue, JsonOptions);
                ApplyPropertyToPrefabObject(resolved.Root, ov.PropertyPath, value);
            }

            return resolved;
        }

        // ──────────────────────────────────────────────────────────────
        // 内部辅助方法
        // ──────────────────────────────────────────────────────────────

        private PrefabAsset? LoadPrefab(string guid)
        {
            var path = FindPrefabByGuid(guid);
            if (path == null) return null;
            return JsonSerializer.Deserialize<PrefabAsset>(File.ReadAllText(path), JsonOptions);
        }

        private static PrefabObject DeepCopy(PrefabObject source)
        {
            var json = JsonSerializer.Serialize(source, JsonOptions);
            return JsonSerializer.Deserialize<PrefabObject>(json, JsonOptions) ?? new PrefabObject();
        }

        /// <summary>将 source 的属性覆盖到 target (非默认值覆盖)。</summary>
        private static void ApplyPrefabOverrides(PrefabObject target, PrefabObject source)
        {
            target.Name = source.Name;
            target.IsVisible = source.IsVisible;
            target.Layer = source.Layer;
            target.Position = source.Position;
            target.Rotation = source.Rotation;
            target.Scale = source.Scale;
            target.MeshAssetPath = source.MeshAssetPath;
            target.MaterialAssetPath = source.MaterialAssetPath;
            target.BaseColor = source.BaseColor;
            target.Metallic = source.Metallic;
            target.Roughness = source.Roughness;
            target.CastShadow = source.CastShadow;
            target.ReceiveShadow = source.ReceiveShadow;
            // 子对象: 替换为 source 的子树
            if (source.Children.Count > 0)
            {
                target.Children.Clear();
                foreach (var child in source.Children)
                    target.Children.Add(DeepCopy(child));
            }
        }

        /// <summary>按属性路径设置 PrefabObject 的属性值。</summary>
        private static void ApplyPropertyToPrefabObject(PrefabObject obj, string path, object? value)
        {
            if (value == null) return;
            switch (path)
            {
                case "Name": obj.Name = value.ToString() ?? obj.Name; break;
                case "IsVisible": obj.IsVisible = (bool)value; break;
                case "Layer": obj.Layer = (int)value; break;
                case "MeshAssetPath": obj.MeshAssetPath = value.ToString() ?? string.Empty; break;
                case "MaterialAssetPath": obj.MaterialAssetPath = value.ToString() ?? string.Empty; break;
                case "Metallic": obj.Metallic = (float)Convert.ToDouble(value); break;
                case "Roughness": obj.Roughness = (float)Convert.ToDouble(value); break;
                case "CastShadow": obj.CastShadow = (bool)value; break;
                case "ReceiveShadow": obj.ReceiveShadow = (bool)value; break;
            }
        }

        /// <summary>按属性路径设置 SceneObject 的属性值。</summary>
        private static void ApplyPropertyToSceneObject(SceneObject obj, string path, object? value)
        {
            if (value == null) return;
            switch (path)
            {
                case "Name": obj.Name = value.ToString() ?? obj.Name; break;
                case "IsVisible": obj.IsVisible = (bool)value; break;
                case "Layer": obj.Layer = (int)value; break;
                case "Position": obj.Position = ToVector3(value); obj.LocalPosition = obj.Position; break;
                case "Rotation": obj.Rotation = ToQuaternion(value); obj.LocalRotation = obj.Rotation; break;
                case "Scale": obj.Scale = ToVector3(value); obj.LocalScale = obj.Scale; break;
                case "MeshAssetPath": obj.MeshAssetPath = value.ToString() ?? string.Empty; break;
                case "MaterialAssetPath": obj.MaterialAssetPath = value.ToString() ?? string.Empty; break;
                case "BaseColor": obj.BaseColor = ToVector3(value); break;
                case "Metallic": obj.Metallic = (float)Convert.ToDouble(value); break;
                case "Roughness": obj.Roughness = (float)Convert.ToDouble(value); break;
                case "CastShadow": obj.CastShadow = (bool)value; break;
                case "ReceiveShadow": obj.ReceiveShadow = (bool)value; break;
            }
        }

        /// <summary>按属性路径获取 SceneObject 的属性值。</summary>
        private static object? GetPropertyFromSceneObject(SceneObject obj, string path)
        {
            return path switch
            {
                "Name" => obj.Name,
                "IsVisible" => obj.IsVisible,
                "Layer" => obj.Layer,
                "Position" => Vector3Record.From(obj.Position),
                "Rotation" => QuaternionRecord.From(obj.Rotation),
                "Scale" => Vector3Record.From(obj.Scale),
                "MeshAssetPath" => obj.MeshAssetPath,
                "MaterialAssetPath" => obj.MaterialAssetPath,
                "BaseColor" => Vector3Record.From(obj.BaseColor),
                "Metallic" => obj.Metallic,
                "Roughness" => obj.Roughness,
                "CastShadow" => obj.CastShadow,
                "ReceiveShadow" => obj.ReceiveShadow,
                _ => null
            };
        }

        /// <summary>将源对象的属性拷贝到目标对象 (保持目标 ID 和 PrefabSourceGuid)。</summary>
        private static void CopySceneObjectData(SceneObject source, SceneObject target)
        {
            target.Name = source.Name;
            target.IsVisible = source.IsVisible;
            target.Layer = source.Layer;
            target.Position = source.Position;
            target.Rotation = source.Rotation;
            target.Scale = source.Scale;
            target.LocalPosition = source.LocalPosition;
            target.LocalRotation = source.LocalRotation;
            target.LocalScale = source.LocalScale;
            target.MeshAssetPath = source.MeshAssetPath;
            target.MaterialAssetPath = source.MaterialAssetPath;
            target.BaseColor = source.BaseColor;
            target.Metallic = source.Metallic;
            target.Roughness = source.Roughness;
            target.CastShadow = source.CastShadow;
            target.ReceiveShadow = source.ReceiveShadow;
        }

        private static Vector3 ToVector3(object value)
        {
            if (value is JsonElement je)
            {
                return new Vector3(je.GetProperty("X").GetSingle(), je.GetProperty("Y").GetSingle(), je.GetProperty("Z").GetSingle());
            }
            if (value is Vector3Record r) return r.ToVector3();
            return Vector3.Zero;
        }

        private static Quaternion ToQuaternion(object value)
        {
            if (value is JsonElement je)
            {
                return new Quaternion(je.GetProperty("X").GetSingle(), je.GetProperty("Y").GetSingle(),
                    je.GetProperty("Z").GetSingle(), je.GetProperty("W").GetSingle());
            }
            if (value is QuaternionRecord r) return r.ToQuaternion();
            return Quaternion.Identity;
        }

        private string? FindPrefabByGuid(string guid)
        {
            foreach (var metaPath in Directory.EnumerateFiles(_projectRoot, "*.shprefab.shmeta", SearchOption.AllDirectories))
            {
                var meta = AssetMeta.Read(metaPath);
                if (string.Equals(meta.Guid, guid, StringComparison.OrdinalIgnoreCase))
                {
                    var prefabPath = metaPath[..^".shmeta".Length];
                    if (File.Exists(prefabPath)) return prefabPath;
                }
            }
            return null;
        }

        private static PrefabObject FromSceneObject(SceneObject source)
        {
            var obj = new PrefabObject
            {
                Name = source.Name,
                IsVisible = source.IsVisible,
                Layer = source.Layer,
                Position = Vector3Record.From(source.Position),
                Rotation = QuaternionRecord.From(source.Rotation),
                Scale = Vector3Record.From(source.Scale),
                MeshAssetPath = source.MeshAssetPath ?? string.Empty,
                MaterialAssetPath = source.MaterialAssetPath ?? string.Empty,
                BaseColor = Vector3Record.From(source.BaseColor),
                Metallic = source.Metallic,
                Roughness = source.Roughness,
                CastShadow = source.CastShadow,
                ReceiveShadow = source.ReceiveShadow
            };
            // 递归处理子对象 (嵌套预制体)
            if (source.Children != null)
            {
                foreach (var child in source.Children)
                    obj.Children.Add(FromSceneObject(child));
            }
            return obj;
        }

        private static SceneObject ToSceneObject(PrefabObject source)
        {
            var obj = new SceneObject
            {
                Name = source.Name,
                IsVisible = source.IsVisible,
                Layer = source.Layer,
                Position = source.Position.ToVector3(),
                Rotation = source.Rotation.ToQuaternion(),
                Scale = source.Scale.ToVector3(),
                LocalPosition = source.Position.ToVector3(),
                LocalRotation = source.Rotation.ToQuaternion(),
                LocalScale = source.Scale.ToVector3(),
                MeshAssetPath = source.MeshAssetPath,
                MaterialAssetPath = source.MaterialAssetPath,
                BaseColor = source.BaseColor.ToVector3(),
                Metallic = source.Metallic,
                Roughness = source.Roughness,
                CastShadow = source.CastShadow,
                ReceiveShadow = source.ReceiveShadow
            };
            // 递归处理子对象 (嵌套预制体)
            foreach (var child in source.Children)
                obj.Children.Add(ToSceneObject(child));
            return obj;
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            return assetPath.Replace('\\', '/').TrimStart('/');
        }
    }
}
