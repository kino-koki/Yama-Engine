using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using YamaEngine.Scenes;
using YamaEngine.Rendering;

namespace YamaEngine.Assets
{
    public enum AssetType
    {
        Unknown,
        Mesh,
        Material,
        Texture2D,
        TextureCube,
        Shader,
        Audio,
        Animation,
        Prefab,
        Scene,
        Script,
        NodeScript,
        ParticleEffect,
        Font,
        Video,
        Skeleton,
        Texture,
        Config,
        Other
    }

    public class AssetMetadata
    {
        public Guid Guid;
        public string AssetPath;
        public string SourceFile;
        public string Name;
        public AssetType Type;
        public long FileSize;
        public DateTime ImportedAt;
        public DateTime LastModified;
        public string Hash;
        public Dictionary<string, object> Properties = new();

        public string GetCachePath()
        {
            return Path.Combine(Path.GetTempPath(), "YamaEngine_Cache",
                Guid.ToString().Substring(0, 2), Guid.ToString() + ".asset");
        }
    }

    public class SceneObject
    {
        public Guid Id = Guid.NewGuid();
        public string Name = "GameObject";
        public bool IsVisible = true;
        public bool IsSelected;
        public int Layer = 0;
        public Vector3 Position = Vector3.Zero;
        public Quaternion Rotation = Quaternion.Identity;
        public Quaternion Orientation { get => Rotation; set => Rotation = value; }
        public Vector3 Scale = Vector3.One;
        public Vector3 LocalPosition = Vector3.Zero;
        public Quaternion LocalRotation = Quaternion.Identity;
        public Vector3 LocalScale = Vector3.One;
        public Matrix4x4 WorldMatrix;
        public SceneObject Parent;
        public List<SceneObject> Children = new();
        public List<IAssetComponent> Components = new();
        public string MeshAssetPath;
        public string MaterialAssetPath;
        /// <summary>材质路径别名 (兼容导出 / 序列化使用)。</summary>
        public string MaterialPath { get => MaterialAssetPath; set => MaterialAssetPath = value; }
        public Vector3 BaseColor = Vector3.One;
        public float Metallic;
        public float Roughness = 0.5f;
        public string? TextureAssetPath { get; set; }
        public bool CastShadow = true;
        public bool ReceiveShadow = true;
        public BoundingBox Bounds;
        public string PrefabSourceGuid = string.Empty;
        /// <summary>父对象 Guid (序列化/层级恢复用)。</summary>
        public string ParentGuid = string.Empty;

        public Matrix4x4 LocalToWorldMatrix =>
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateTranslation(Position);

        public T GetComponent<T>() where T : IAssetComponent
        {
            foreach (var c in Components) if (c is T t) return t;
            return default;
        }

        public T AddComponent<T>() where T : IAssetComponent, new()
        {
            var c = new T();
            Components.Add(c);
            return c;
        }

        /// <summary>灯光组件 (转为 ECS 结构体, 供运行时 PIE 使用)。</summary>
        public YamaEngine.ECS.LightComponent? LightComponent
        {
            get
            {
                var c = GetComponent<LightComponent>();
                if (c == null) return null;
                return new YamaEngine.ECS.LightComponent
                {
                    Type = ParseLightType(c.LightType),
                    Color = c.Color,
                    Intensity = c.Intensity,
                    Range = c.Range,
                    InnerConeAngle = c.InnerAngle,
                    OuterConeAngle = c.OuterAngle,
                    CastsShadow = c.CastsShadows,
                    ShadowBias = c.ShadowBias
                };
            }
        }

        /// <summary>相机组件 (转为 ECS 结构体)。</summary>
        public YamaEngine.ECS.CameraComponent? CameraComponent
        {
            get
            {
                var c = GetComponent<CameraComponent>();
                if (c == null) return null;
                return new YamaEngine.ECS.CameraComponent
                {
                    FieldOfView = c.FieldOfView,
                    AspectRatio = c.AspectRatio,
                    NearClip = c.NearClip,
                    FarClip = c.FarClip,
                    IsMain = c.IsMain
                };
            }
        }

        /// <summary>刚体组件 (转为 ECS 结构体)。</summary>
        public YamaEngine.ECS.RigidbodyComponent? RigidbodyComponent
        {
            get
            {
                var c = GetComponent<RigidbodyComponent>();
                if (c == null) return null;
                return new YamaEngine.ECS.RigidbodyComponent
                {
                    ColliderType = c.ColliderType,
                    ColliderSize = c.ColliderSize,
                    IsKinematic = c.IsKinematic,
                    Mass = c.Mass
                };
            }
        }

        /// <summary>将字符串灯光类型解析为 ECS 枚举。</summary>
        private static YamaEngine.ECS.LightComponent.LightType ParseLightType(string s) => s switch
        {
            "Point" => YamaEngine.ECS.LightComponent.LightType.Point,
            "Spot" => YamaEngine.ECS.LightComponent.LightType.Spot,
            "Area" => YamaEngine.ECS.LightComponent.LightType.Area,
            _ => YamaEngine.ECS.LightComponent.LightType.Directional
        };

        public void AttachChild(SceneObject child)
        {
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
            child.LocalPosition = child.Position - Position;
        }

        public void DetachFromParent()
        {
            Parent?.Children.Remove(this);
            Parent = null;
            LocalPosition = Position;
            LocalRotation = Rotation;
            LocalScale = Scale;
        }
    }

    public interface IAssetComponent
    {
        string Name { get; }
        void Update(float deltaTime, SceneObject owner);
    }

    public class CameraComponent : IAssetComponent
    {
        public string Name => "Camera";
        public float FieldOfView = 60f;
        public float NearClip = 0.1f;
        public float FarClip = 1000f;
        public float AspectRatio = 1.777f;
        public bool IsMain;

        /// <summary>最近一次 Update 缓存的视图矩阵 (世界 -> 相机空间), 供渲染/剔除复用。</summary>
        public Matrix4x4 ViewMatrix;

        public Matrix4x4 GetProjectionMatrix()
        {
            float fovRad = FieldOfView * (float)(Math.PI / 180.0);
            return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, AspectRatio, NearClip, FarClip);
        }

        /// <summary>组合视图投影矩阵 (View * Projection)。</summary>
        public Matrix4x4 GetViewProjectionMatrix() => ViewMatrix * GetProjectionMatrix();

        public void Update(float deltaTime, SceneObject owner)
        {
            if (owner == null) return;

            // 视图矩阵 = 相机世界变换矩阵的逆 (行向量约定 v*M: world = S*R*T)
            Matrix4x4 world = Matrix4x4.CreateScale(owner.Scale) *
                              Matrix4x4.CreateFromQuaternion(owner.Rotation) *
                              Matrix4x4.CreateTranslation(owner.Position);
            if (Matrix4x4.Invert(world, out Matrix4x4 view))
                ViewMatrix = view;

            // 维护裁剪面有效性, 避免透视投影退化
            NearClip = Math.Max(0.001f, NearClip);
            FarClip = Math.Max(NearClip + 0.001f, FarClip);
            AspectRatio = Math.Max(0.0001f, AspectRatio);
        }
    }

    public class LightComponent : IAssetComponent
    {
        public string Name => "Light";
        public string LightType = "Directional";
        public Vector3 Color = Vector3.One;
        public float Intensity = 1.0f;
        public float Range = 10f;
        public float InnerAngle = 30f;
        public float OuterAngle = 45f;
        public bool CastsShadows = true;
        public float ShadowBias = 0.005f;

        public void Update(float deltaTime, SceneObject owner)
        {
            // 保持光照参数处于有效区间, 避免渲染/阴影伪影
            Intensity = Math.Max(0f, Intensity);
            Range = Math.Max(0.001f, Range);
            InnerAngle = Math.Clamp(InnerAngle, 0f, 180f);
            // 内锥角不应大于外锥角 (聚光灯), 否则产生错误的本影/半影
            OuterAngle = Math.Clamp(OuterAngle, InnerAngle, 180f);
            ShadowBias = Math.Max(0f, ShadowBias);
            // 颜色分量归一化到 [0,1], 防止过曝输入
            Color = Vector3.Clamp(Color, Vector3.Zero, Vector3.One);
        }
    }

    /// <summary>场景对象上的刚体组件 (PIE 物理导出用)。</summary>
    public class RigidbodyComponent : IAssetComponent
    {
        public string Name => "Rigidbody";
        public YamaEngine.Physics.ColliderType ColliderType = YamaEngine.Physics.ColliderType.Box;
        public Vector3 ColliderSize = Vector3.One;
        public bool IsKinematic;
        public float Mass = 1f;

        public void Update(float deltaTime, SceneObject owner) { }
    }


    public class AssetDatabase
    {
        private readonly Dictionary<Guid, AssetMetadata> _assetsByGuid = new();
        private readonly Dictionary<string, AssetMetadata> _assetsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Guid> _pathToGuid = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SceneObject> _sceneObjects = new();
        private readonly Dictionary<string, DCC.DCCMeshData> _meshCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _rootPath;
        private readonly string _libraryPath;

        public event Action<AssetMetadata> OnAssetImported;
        public event Action<AssetMetadata> OnAssetUpdated;
        public event Action<string> OnAssetDeleted;
        public event Action<SceneObject> OnObjectCreated;
        public event Action<SceneObject> OnObjectSelected;
        public event Action OnSceneChanged;

        public IReadOnlyList<SceneObject> SceneObjects => _sceneObjects;
        public int AssetCount => _assetsByGuid.Count;
        public int ObjectCount => _sceneObjects.Count;

        /// <summary>当前打开场景的名称 (PIE 导出 / 标题栏使用)。</summary>
        public string CurrentSceneName { get; set; } = "Untitled";

        public string RootPath => _rootPath;
        public string LibraryPath => _libraryPath;

        public AssetDatabase(string rootPath)
        {
            _rootPath = rootPath;
            _libraryPath = Path.Combine(rootPath, "Library");
            try { Directory.CreateDirectory(_libraryPath); } catch { }
        }

        /// <summary>
        /// 释放资产数据库资源: 清空所有缓存 (网格、元数据、场景对象) 并解除事件订阅,
        /// 避免数据库重建/卸载时持有引用导致内存泄漏。
        /// </summary>
        public void Dispose()
        {
            _meshCache.Clear();
            _assetsByGuid.Clear();
            _assetsByPath.Clear();
            _pathToGuid.Clear();
            _sceneObjects.Clear();

            // 解除所有事件订阅, 防止外部委托持有数据库实例造成泄漏
            OnAssetImported = null;
            OnAssetUpdated = null;
            OnAssetDeleted = null;
            OnObjectCreated = null;
            OnObjectSelected = null;
            OnSceneChanged = null;
        }

        public string GenerateHash(string input)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public AssetMetadata ImportAsset(string sourceFile, AssetType type = AssetType.Unknown)
        {
            if (!File.Exists(sourceFile))
            {
                System.Diagnostics.Debug.WriteLine($"File not found: {sourceFile}");
                return null;
            }

            var fileName = Path.GetFileName(sourceFile);
            var ext = Path.GetExtension(sourceFile).ToLower();
            if (type == AssetType.Unknown)
            {
                type = ext switch
                {
                    ".obj" or ".fbx" or ".dae" or ".ply" or ".stl" or ".blend" => AssetType.Mesh,
                    ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds" => AssetType.Texture2D,
                    ".wav" or ".mp3" or ".ogg" or ".flac" => AssetType.Audio,
                    ".yamaasset" => AssetType.Scene,
                    _ => AssetType.Unknown
                };
            }

            string relativePath = $"Assets/{Guid.NewGuid():N}_{fileName}";
            string importedPath = Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(importedPath)!);
            File.Copy(sourceFile, importedPath, true);

            var fi = new FileInfo(sourceFile);
            var meta = new AssetMetadata
            {
                Guid = Guid.NewGuid(),
                Name = Path.GetFileNameWithoutExtension(fileName),
                AssetPath = relativePath,
                SourceFile = sourceFile,
                Type = type,
                FileSize = fi.Length,
                ImportedAt = DateTime.Now,
                LastModified = fi.LastWriteTime,
                Hash = GenerateHash(sourceFile + fi.Length + fi.LastWriteTime.Ticks)
            };
            var metaPath = importedPath + ".shmeta";
            AssetMeta.FromMetadata(meta).Write(metaPath);

            _assetsByGuid[meta.Guid] = meta;
            _assetsByPath[relativePath] = meta;
            _pathToGuid[sourceFile] = meta.Guid;

            if (type == AssetType.Mesh)
            {
                var bridge = new DCC.DCCBridge();
                var sceneData = bridge.ImportScene(sourceFile);
                if (sceneData != null)
                {
                    meta.Properties["MeshCount"] = sceneData.Meshes.Count;
                    meta.Properties["TotalTriangles"] = sceneData.TotalTriangles;
                    meta.Properties["TotalVertices"] = sceneData.TotalVertices;

                    int idx = 0;
                    foreach (var mesh in sceneData.Meshes)
                    {
                        string cacheKey = $"{meta.Guid}_{idx}";
                        _meshCache[cacheKey] = mesh;
                        idx++;
                    }
                }
            }

            OnAssetImported?.Invoke(meta);
            System.Diagnostics.Debug.WriteLine($"Imported asset: {meta.Name} ({meta.Type})");
            return meta;
        }

        public bool RemoveAsset(Guid guid)
        {
            if (!_assetsByGuid.TryGetValue(guid, out var meta)) return false;
            _assetsByGuid.Remove(guid);
            _assetsByPath.Remove(meta.AssetPath);
            _pathToGuid.Remove(meta.SourceFile);
            var keysToRemove = new List<string>();
            foreach (var kvp in _meshCache) if (kvp.Key.StartsWith(guid.ToString())) keysToRemove.Add(kvp.Key);
            foreach (var k in keysToRemove) _meshCache.Remove(k);
            OnAssetDeleted?.Invoke(meta.AssetPath);
            return true;
        }

        public AssetMetadata GetAsset(Guid guid)
        {
            return _assetsByGuid.TryGetValue(guid, out var m) ? m : null;
        }

        /// <summary>
        /// 按 Guid 获取并加载指定类型的资产对象。
        /// 当前通用加载管线尚未接入完整反序列化，暂返回 null（调用方需自行回退）。
        /// </summary>
        public T? GetAsset<T>(Guid guid) where T : class
        {
            return null;
        }

        public AssetMetadata GetAssetByGuid(string guid)
        {
            if (!Guid.TryParse(guid, out var parsed)) return null;
            return GetAsset(parsed);
        }

        public AssetMetadata RegisterAsset(string assetPath, Guid guid, AssetType type, string name)
        {
            var fullPath = Path.Combine(_rootPath, assetPath.Replace('/', Path.DirectorySeparatorChar));
            var fileInfo = File.Exists(fullPath) ? new FileInfo(fullPath) : null;
            var meta = new AssetMetadata
            {
                Guid = guid,
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(assetPath) : name,
                AssetPath = assetPath.Replace('\\', '/'),
                SourceFile = fullPath,
                Type = type,
                FileSize = fileInfo?.Length ?? 0,
                ImportedAt = DateTime.Now,
                LastModified = fileInfo?.LastWriteTime ?? DateTime.Now,
                Hash = fileInfo == null ? guid.ToString("N") : GenerateHash(fullPath + fileInfo.Length + fileInfo.LastWriteTime.Ticks)
            };
            _assetsByGuid[meta.Guid] = meta;
            _assetsByPath[meta.AssetPath] = meta;
            _pathToGuid[meta.AssetPath] = meta.Guid;
            OnAssetImported?.Invoke(meta);
            return meta;
        }

        public AssetMetadata GetAsset(string path)
        {
            return _assetsByPath.TryGetValue(path, out var m) ? m :
                   _pathToGuid.TryGetValue(path, out var guid) ? _assetsByGuid[guid] : null;
        }

        public List<AssetMetadata> GetAllAssets(AssetType? filter = null)
        {
            var list = new List<AssetMetadata>();
            foreach (var a in _assetsByGuid.Values)
            {
                if (filter == null || a.Type == filter.Value) list.Add(a);
            }
            return list;
        }

        public DCC.DCCMeshData GetMeshData(string cacheKey)
        {
            return _meshCache.TryGetValue(cacheKey, out var m) ? m : null;
        }

        public SceneObject CreateObject(string name = null, string meshType = "Box", Vector3? position = null)
        {
            var obj = new SceneObject
            {
                Name = name ?? $"Object_{_sceneObjects.Count + 1}",
                Position = position ?? Vector3.Zero,
                MeshAssetPath = meshType
            };

            switch (meshType)
            {
                case "Sphere": obj.BaseColor = new Vector3(0.9f, 0.85f, 0.35f); break;
                case "Plane": obj.BaseColor = new Vector3(0.55f, 0.75f, 0.45f); break;
                case "Cylinder": obj.BaseColor = new Vector3(0.45f, 0.65f, 0.85f); break;
                case "Torus": obj.BaseColor = new Vector3(0.9f, 0.4f, 0.4f); break;
                default: obj.BaseColor = new Vector3(0.9f, 0.9f, 0.95f); break;
            }

            obj.Bounds = meshType switch
            {
                "Sphere" => new BoundingBox { Min = new Vector3(-0.5f, -0.5f, -0.5f), Max = new Vector3(0.5f, 0.5f, 0.5f) },
                "Plane" => new BoundingBox { Min = new Vector3(-5f, 0, -5f), Max = new Vector3(5f, 0.01f, 5f) },
                "Cylinder" => new BoundingBox { Min = new Vector3(-0.5f, -1f, -0.5f), Max = new Vector3(0.5f, 1f, 0.5f) },
                "Torus" => new BoundingBox { Min = new Vector3(-1.3f, -0.3f, -1.3f), Max = new Vector3(1.3f, 0.3f, 1.3f) },
                _ => new BoundingBox { Min = new Vector3(-0.5f, -0.5f, -0.5f), Max = new Vector3(0.5f, 0.5f, 0.5f) }
            };

            _sceneObjects.Add(obj);
            OnObjectCreated?.Invoke(obj);
            OnSceneChanged?.Invoke();
            return obj;
        }

        public SceneObject AddObject(SceneObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            _sceneObjects.Add(obj);
            OnObjectCreated?.Invoke(obj);
            OnSceneChanged?.Invoke();
            return obj;
        }

        public SceneObject CreateLight(string type = "Directional", Vector3? position = null, Vector3? color = null)
        {
            var obj = new SceneObject
            {
                Name = $"{type} Light",
                Position = position ?? new Vector3(5, 10, 5),
                MeshAssetPath = "Light_" + type
            };
            var light = obj.AddComponent<LightComponent>();
            light.LightType = type;
            light.Color = color ?? new Vector3(1f, 0.97f, 0.92f);
            light.Intensity = type == "Directional" ? 1.5f : 1.0f;
            if (type == "Point") light.Range = 8f;
            obj.BaseColor = new Vector3(1f, 0.97f, 0.92f);
            obj.CastShadow = true;
            _sceneObjects.Add(obj);
            OnObjectCreated?.Invoke(obj);
            OnSceneChanged?.Invoke();
            return obj;
        }

        public SceneObject CreateCamera(Vector3? position = null, bool isMain = true)
        {
            var obj = new SceneObject
            {
                Name = "Camera",
                Position = position ?? new Vector3(0, 2, 8),
                MeshAssetPath = "Camera_Gizmo"
            };
            var cam = obj.AddComponent<CameraComponent>();
            cam.IsMain = isMain;
            obj.BaseColor = new Vector3(0.4f, 0.9f, 0.95f);
            _sceneObjects.Add(obj);
            OnObjectCreated?.Invoke(obj);
            OnSceneChanged?.Invoke();
            return obj;
        }

        public bool RemoveObject(SceneObject obj)
        {
            if (obj == null) return false;
            obj.Parent?.Children.Remove(obj);
            foreach (var child in obj.Children) child.Parent = null;
            if (_sceneObjects.Remove(obj))
            {
                OnSceneChanged?.Invoke();
                return true;
            }
            return false;
        }

        public SceneObject FindObjectByName(string name)
        {
            foreach (var obj in _sceneObjects)
                if (obj.Name == name) return obj;
            return null;
        }

        public void SelectObject(SceneObject obj, bool additive = false)
        {
            if (!additive)
            {
                foreach (var o in _sceneObjects) o.IsSelected = false;
            }
            if (obj != null)
            {
                obj.IsSelected = true;
                OnObjectSelected?.Invoke(obj);
            }
        }

        public void SelectObjects(List<SceneObject> objects)
        {
            foreach (var o in _sceneObjects) o.IsSelected = false;
            if (objects != null)
            {
                foreach (var o in objects) o.IsSelected = true;
                if (objects.Count > 0) OnObjectSelected?.Invoke(objects[0]);
            }
        }

        public List<SceneObject> GetSelectedObjects()
        {
            var list = new List<SceneObject>();
            foreach (var o in _sceneObjects) if (o.IsSelected) list.Add(o);
            return list;
        }

        public SceneObject DuplicateObject(SceneObject source)
        {
            if (source == null) return null;
            var obj = new SceneObject
            {
                Name = source.Name + "_Copy",
                Position = source.Position + new Vector3(1, 0, 1),
                Rotation = source.Rotation,
                Scale = source.Scale,
                MeshAssetPath = source.MeshAssetPath,
                MaterialAssetPath = source.MaterialAssetPath,
                TextureAssetPath = source.TextureAssetPath,
                BaseColor = source.BaseColor,
                Metallic = source.Metallic,
                Roughness = source.Roughness,
                CastShadow = source.CastShadow,
                ReceiveShadow = source.ReceiveShadow,
                Bounds = source.Bounds
            };
            _sceneObjects.Add(obj);
            OnObjectCreated?.Invoke(obj);
            OnSceneChanged?.Invoke();
            return obj;
        }

        public void ClearScene()
        {
            _sceneObjects.Clear();
            OnSceneChanged?.Invoke();
        }

        public bool SaveScene(string filePath)
        {
            return SaveScene(filePath, "Main");
        }

        public bool SaveScene(string filePath, string sceneName)
        {
            try
            {
                SceneSerializer.Save(filePath, _sceneObjects, sceneName);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save scene failed: {ex.Message}");
                return false;
            }
        }

        public bool LoadScene(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                if (!SceneSerializer.TryLoad(filePath, out var doc, out var error) || doc == null)
                {
                    System.Diagnostics.Debug.WriteLine($"Load scene failed: {error}");
                    return false;
                }

                _sceneObjects.Clear();
                foreach (var obj in SceneSerializer.Materialize(doc))
                {
                    _sceneObjects.Add(obj);
                    OnObjectCreated?.Invoke(obj);
                }

                foreach (var obj in _sceneObjects) obj.IsSelected = false;
                OnSceneChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Load scene failed: {ex.Message}");
                return false;
            }
        }

        public BoundingBox CalculateSceneBounds()
        {
            if (_sceneObjects.Count == 0)
                return new BoundingBox { Min = new Vector3(-1, -1, -1), Max = new Vector3(1, 1, 1) };
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            foreach (var obj in _sceneObjects)
            {
                var transformed = obj.Bounds.Transform(obj.LocalToWorldMatrix);
                min = Vector3.Min(min, transformed.Min);
                max = Vector3.Max(max, transformed.Max);
            }
            return new BoundingBox { Min = min, Max = max };
        }
    }
}
