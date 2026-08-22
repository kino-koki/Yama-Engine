using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShanyuEngine.Assets;
using ShanyuEngine.ECS;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.Scenes
{
    /// <summary>
    /// 统一读写 Shanyu 场景文件（.shanyu）。
    /// JSON 布局、格式版本和资产引用都集中在这里，AssetDatabase 只负责索引和场景对象管理。
    /// </summary>
    public static class SceneSerializer
    {
        public const int CurrentFormatVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static SceneDocument Save(string filePath, IEnumerable<SceneObject> objects, string sceneName = "Main")
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Scene file path is required.", nameof(filePath));
            if (objects == null) throw new ArgumentNullException(nameof(objects));

            var doc = new SceneDocument
            {
                FormatVersion = CurrentFormatVersion,
                Name = string.IsNullOrWhiteSpace(sceneName) ? "Main" : sceneName,
                SavedAtUtc = DateTime.UtcNow,
                Objects = objects.Where(o => o != null).Select(SceneObjectRecord.FromSceneObject).ToList()
            };

            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(filePath, JsonSerializer.Serialize(doc, JsonOptions), Encoding.UTF8);
            return doc;
        }

        public static SceneDocument Load(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException($"Scene file not found: {filePath}", filePath);

            SceneDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<SceneDocument>(File.ReadAllText(filePath, Encoding.UTF8), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Invalid scene JSON: {filePath}. {ex.Message}", ex);
            }

            if (doc == null) throw new InvalidDataException($"Empty scene document: {filePath}");
            if (doc.FormatVersion <= 0) doc.FormatVersion = CurrentFormatVersion;
            doc.Objects ??= new List<SceneObjectRecord>();
            return doc;
        }

        public static bool TryLoad(string filePath, out SceneDocument? document, out string? error)
        {
            try
            {
                document = Load(filePath);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                document = null;
                error = ex.Message;
                return false;
            }
        }

        public static List<SceneObject> Materialize(SceneDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var result = new List<SceneObject>(document.Objects?.Count ?? 0);
            if (document.Objects != null)
            {
                foreach (var record in document.Objects)
                {
                    result.Add(record.ToSceneObject());
                }
            }
            return result;
        }
    }

    public sealed class SceneDocument
    {
        public int FormatVersion { get; set; } = SceneSerializer.CurrentFormatVersion;
        public string Engine { get; set; } = "Shanyu Engine";
        public string Name { get; set; } = "Main";
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
        public List<SceneObjectRecord> Objects { get; set; } = new();
    }

    public sealed class SceneObjectRecord
    {
        public Guid Id { get; set; }
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
        public string PrefabSourceGuid { get; set; } = string.Empty;
        public List<ComponentRecord> Components { get; set; } = new();

        public static SceneObjectRecord FromSceneObject(SceneObject obj)
        {
            return new SceneObjectRecord
            {
                Id = obj.Id,
                Name = obj.Name,
                IsVisible = obj.IsVisible,
                Layer = obj.Layer,
                Position = Vector3Record.From(obj.Position),
                Rotation = QuaternionRecord.From(obj.Rotation),
                Scale = Vector3Record.From(obj.Scale),
                MeshAssetPath = obj.MeshAssetPath ?? string.Empty,
                MaterialAssetPath = obj.MaterialAssetPath ?? string.Empty,
                BaseColor = Vector3Record.From(obj.BaseColor),
                Metallic = obj.Metallic,
                Roughness = obj.Roughness,
                CastShadow = obj.CastShadow,
                ReceiveShadow = obj.ReceiveShadow,
                PrefabSourceGuid = obj.PrefabSourceGuid ?? string.Empty,
                Components = obj.Components
                    .Select(ComponentRecord.FromComponent)
                    .Where(c => c != null)
                    .Cast<ComponentRecord>()
                    .ToList()
            };
        }

        public SceneObject ToSceneObject()
        {
            var obj = new SceneObject
            {
                Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
                Name = string.IsNullOrWhiteSpace(Name) ? "GameObject" : Name,
                IsVisible = IsVisible,
                Layer = Layer,
                Position = Position.ToVector3(),
                Rotation = Rotation.ToQuaternion(),
                Scale = Scale.ToVector3(),
                LocalPosition = Position.ToVector3(),
                LocalRotation = Rotation.ToQuaternion(),
                LocalScale = Scale.ToVector3(),
                MeshAssetPath = MeshAssetPath ?? string.Empty,
                MaterialAssetPath = MaterialAssetPath ?? string.Empty,
                BaseColor = BaseColor.ToVector3(),
                Metallic = Metallic,
                Roughness = Roughness,
                CastShadow = CastShadow,
                ReceiveShadow = ReceiveShadow,
                PrefabSourceGuid = PrefabSourceGuid ?? string.Empty,
                Bounds = BoundsForMesh(MeshAssetPath)
            };

            if (Components != null)
            {
                foreach (var component in Components)
                {
                    component?.Apply(obj);
                }
            }

            return obj;
        }

        private static BoundingBox BoundsForMesh(string? meshType)
        {
            return meshType switch
            {
                "Sphere" => new BoundingBox { Min = new Vector3(-0.5f, -0.5f, -0.5f), Max = new Vector3(0.5f, 0.5f, 0.5f) },
                "Plane" => new BoundingBox { Min = new Vector3(-5f, 0, -5f), Max = new Vector3(5f, 0.01f, 5f) },
                "Cylinder" => new BoundingBox { Min = new Vector3(-0.5f, -1f, -0.5f), Max = new Vector3(0.5f, 1f, 0.5f) },
                "Torus" => new BoundingBox { Min = new Vector3(-1.3f, -0.3f, -1.3f), Max = new Vector3(1.3f, 0.3f, 1.3f) },
                _ => new BoundingBox { Min = new Vector3(-0.5f, -0.5f, -0.5f), Max = new Vector3(0.5f, 0.5f, 0.5f) }
            };
        }
    }

    public sealed class ComponentRecord
    {
        public string Type { get; set; } = string.Empty;
        public bool IsMain { get; set; }
        public float FieldOfView { get; set; } = 60f;
        public float NearClip { get; set; } = 0.1f;
        public float FarClip { get; set; } = 1000f;
        public string LightType { get; set; } = "Directional";
        public Vector3Record Color { get; set; } = Vector3Record.One;
        public float Intensity { get; set; } = 1f;
        public float Range { get; set; } = 10f;
        public bool CastsShadows { get; set; } = true;

        public static ComponentRecord? FromComponent(IComponent component)
        {
            return component switch
            {
                CameraComponent camera => new ComponentRecord
                {
                    Type = "Camera",
                    IsMain = camera.IsMain,
                    FieldOfView = camera.FieldOfView,
                    NearClip = camera.NearClip,
                    FarClip = camera.FarClip
                },
                LightComponent light => new ComponentRecord
                {
                    Type = "Light",
                    LightType = light.LightType,
                    Color = Vector3Record.From(light.Color),
                    Intensity = light.Intensity,
                    Range = light.Range,
                    CastsShadows = light.CastsShadows
                },
                _ => null
            };
        }

        public void Apply(SceneObject target)
        {
            switch (Type)
            {
                case "Camera":
                    var camera = target.AddComponent<CameraComponent>();
                    camera.IsMain = IsMain;
                    camera.FieldOfView = FieldOfView;
                    camera.NearClip = NearClip;
                    camera.FarClip = FarClip;
                    break;
                case "Light":
                    var light = target.AddComponent<LightComponent>();
                    light.LightType = string.IsNullOrWhiteSpace(LightType) ? "Directional" : LightType;
                    light.Color = Color.ToVector3();
                    light.Intensity = Intensity;
                    light.Range = Range;
                    light.CastsShadows = CastsShadows;
                    break;
            }
        }
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

    // ====================================================================
    // ECS 场景序列化系统（SceneData / EntityData / ComponentData）
    // ====================================================================
    // 面向 EntityComponentSystem 的完整 JSON 序列化/反序列化。
    // 实体通过 GUID 在场景文件内持久化引用，运行时 int 实体 ID 不被序列化。
    // ====================================================================

    /// <summary>场景数据结构（ECS 版），包含名称、版本与全部实体。</summary>
    public sealed class SceneData
    {
        /// <summary>场景名称。</summary>
        public string Name { get; set; } = "Scene";

        /// <summary>序列化格式版本。</summary>
        public int Version { get; set; } = SceneJsonSerializer.CurrentVersion;

        /// <summary>场景内的全部实体数据。</summary>
        public List<EntityData> Entities { get; set; } = new();
    }

    /// <summary>实体数据（ECS 版），描述单个实体的标识、变换、父子关系与组件。</summary>
    public sealed class EntityData
    {
        /// <summary>实体在场景文件内的稳定标识（运行时 int ID 不被序列化）。</summary>
        public Guid GUID { get; set; }

        /// <summary>实体名称。</summary>
        public string Name { get; set; } = "Entity";

        /// <summary>父实体 GUID；Guid.Empty 表示根实体。</summary>
        public Guid ParentGUID { get; set; } = Guid.Empty;

        /// <summary>实体是否激活。</summary>
        public bool Active { get; set; } = true;

        /// <summary>实体变换（Position / Rotation / Scale）。</summary>
        public TransformData Transform { get; set; } = new();

        /// <summary>实体携带的组件列表（不含 Transform，Transform 单独存储）。</summary>
        public List<ComponentData> Components { get; set; } = new();

        /// <summary>实体附加的脚本列表（由 ScriptRegistry 管理，非 ECS 组件）。</summary>
        public List<ScriptData> Scripts { get; set; } = new();
    }

    /// <summary>脚本数据，描述附加到实体的脚本类型、启用状态和序列化字段。</summary>
    public sealed class ScriptData
    {
        /// <summary>脚本类型全名（命名空间.类名）。</summary>
        public string TypeName { get; set; } = string.Empty;

        /// <summary>脚本源文件路径（用于热重载和编辑器定位）。</summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>序列化的公共字段值 [字段名 → 值]。</summary>
        public Dictionary<string, object?> Fields { get; set; } = new();
    }

    /// <summary>变换数据，以 float 数组存储以避免向量类型的序列化差异。</summary>
    public sealed class TransformData
    {
        public float[] Position { get; set; } = new float[] { 0f, 0f, 0f };
        public float[] Rotation { get; set; } = new float[] { 0f, 0f, 0f, 1f }; // XYZW
        public float[] Scale { get; set; } = new float[] { 1f, 1f, 1f };

        public static TransformData From(Vector3 pos, Quaternion rot, Vector3 scale)
            => new()
            {
                Position = new float[] { pos.X, pos.Y, pos.Z },
                Rotation = new float[] { rot.X, rot.Y, rot.Z, rot.W },
                Scale = new float[] { scale.X, scale.Y, scale.Z }
            };

        public Vector3 ToPosition() => new(Position[0], Position[1], Position[2]);
        public Quaternion ToRotation() => new(Rotation[0], Rotation[1], Rotation[2], Rotation[3]);
        public Vector3 ToScale() => new(Scale[0], Scale[1], Scale[2]);
    }

    /// <summary>组件数据，以类型名 + 属性字典存储，支持任意组件的通用序列化。</summary>
    public sealed class ComponentData
    {
        /// <summary>组件类型名（如 "MeshComponent"、"LightComponent"）。</summary>
        public string TypeName { get; set; } = string.Empty;

        /// <summary>组件属性键值对，值为标量/数组/嵌套字典。</summary>
        public Dictionary<string, object?> Properties { get; set; } = new();
    }

    /// <summary>
    /// ECS 场景 JSON 序列化器。提供 SceneData 与 JSON 字符串、文件之间的转换，
    /// 以及与 EntityComponentSystem 的双向构建。
    /// </summary>
    public static class SceneJsonSerializer
    {
        /// <summary>当前序列化格式版本。</summary>
        public const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(), new ObjectJsonConverter() }
        };

        // ── JSON 字符串 / 对象转换 ──

        /// <summary>将 SceneData 序列化为 JSON 字符串。</summary>
        public static string Serialize(SceneData scene)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            return JsonSerializer.Serialize(scene, Options);
        }

        /// <summary>从 JSON 字符串反序列化为 SceneData。</summary>
        public static SceneData Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("JSON string is empty.", nameof(json));
            SceneData? data = JsonSerializer.Deserialize<SceneData>(json, Options);
            if (data == null) throw new InvalidDataException("Failed to deserialize SceneData.");
            data.Entities ??= new List<EntityData>();
            return data;
        }

        // ── 文件 IO ──

        /// <summary>将 SceneData 保存到文件。</summary>
        public static void SaveToFile(SceneData scene, string filePath)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path is required.", nameof(filePath));

            string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, Serialize(scene), Encoding.UTF8);
        }

        /// <summary>从文件加载 SceneData。</summary>
        public static SceneData LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException($"Scene file not found: {filePath}", filePath);
            return Deserialize(File.ReadAllText(filePath, Encoding.UTF8));
        }

        /// <summary>尝试从文件加载 SceneData，返回是否成功。</summary>
        public static bool TryLoadFromFile(string filePath, out SceneData? scene, out string? error)
        {
            try
            {
                scene = LoadFromFile(filePath);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                scene = null;
                error = ex.Message;
                return false;
            }
        }

        // ── 从 EntityComponentSystem 构建 SceneData ──

        /// <summary>从 ECS 收集全部实体与组件，构建 SceneData。</summary>
        /// <param name="ecs">实体组件系统。</param>
        /// <param name="sceneName">场景名称。</param>
        /// <param name="scriptRegistry">可选的脚本注册表，用于序列化脚本实例。</param>
        public static SceneData BuildFromECS(EntityComponentSystem ecs, string sceneName = "Scene", ShanyuEngine.Scripting.ScriptRegistry? scriptRegistry = null)
        {
            if (ecs == null) throw new ArgumentNullException(nameof(ecs));

            var data = new SceneData { Name = sceneName, Version = CurrentVersion };

            // 为每个实体分配 GUID，建立 int ID → GUID 映射
            var idToGuid = new Dictionary<int, Guid>();
            var entityIds = ecs.AllEntities.ToList();
            foreach (int id in entityIds)
                idToGuid[id] = Guid.NewGuid();

            foreach (int id in entityIds)
            {
                if (!ecs.HasEntity(id)) continue;

                var entityData = new EntityData
                {
                    GUID = idToGuid[id],
                    Name = ecs.GetEntityName(id) ?? "Entity",
                    Active = true
                };

                // 变换
                if (ecs.HasComponent<TransformComponent>(id))
                {
                    var t = ecs.GetComponent<TransformComponent>(id);
                    entityData.Transform = TransformData.From(t.Position, t.Rotation, t.Scale);
                }

                // 父子关系
                if (ecs.HasComponent<RelationshipComponent>(id))
                {
                    var rel = ecs.GetComponent<RelationshipComponent>(id);
                    if (rel.ParentId != 0 && idToGuid.TryGetValue(rel.ParentId, out var parentGuid))
                        entityData.ParentGUID = parentGuid;
                }

                // 收集已知组件
                AddComponentData(entityData, ecs, id);

                // 收集脚本
                if (scriptRegistry != null)
                    AddScriptData(entityData, scriptRegistry, id);

                data.Entities.Add(entityData);
            }

            return data;
        }

        private static void AddComponentData(EntityData entityData, EntityComponentSystem ecs, int id)
        {
            // MeshComponent
            if (ecs.HasComponent<MeshComponent>(id))
            {
                var m = ecs.GetComponent<MeshComponent>(id);
                entityData.Components.Add(new ComponentData
                {
                    TypeName = nameof(MeshComponent),
                    Properties = new()
                    {
                        ["MeshId"] = m.MeshId.ToString(),
                        ["MaterialId"] = m.MaterialId.ToString(),
                        ["CastsShadow"] = m.CastsShadow,
                        ["ReceivesShadow"] = m.ReceivesShadow,
                        ["IsVisible"] = m.IsVisible,
                        ["TriangleCount"] = m.TriangleCount,
                        ["VertexCount"] = m.VertexCount
                    }
                });
            }

            // LightComponent
            if (ecs.HasComponent<LightComponent>(id))
            {
                var l = ecs.GetComponent<LightComponent>(id);
                entityData.Components.Add(new ComponentData
                {
                    TypeName = nameof(LightComponent),
                    Properties = new()
                    {
                        ["Type"] = l.Type.ToString(),
                        ["Color"] = new float[] { l.Color.X, l.Color.Y, l.Color.Z },
                        ["Intensity"] = l.Intensity,
                        ["Range"] = l.Range,
                        ["InnerConeAngle"] = l.InnerConeAngle,
                        ["OuterConeAngle"] = l.OuterConeAngle,
                        ["CastsShadow"] = l.CastsShadow,
                        ["ShadowBias"] = l.ShadowBias
                    }
                });
            }

            // CameraComponent
            if (ecs.HasComponent<CameraComponent>(id))
            {
                var c = ecs.GetComponent<CameraComponent>(id);
                entityData.Components.Add(new ComponentData
                {
                    TypeName = nameof(CameraComponent),
                    Properties = new()
                    {
                        ["FieldOfView"] = c.FieldOfView,
                        ["AspectRatio"] = c.AspectRatio,
                        ["NearClip"] = c.NearClip,
                        ["FarClip"] = c.FarClip,
                        ["IsMain"] = c.IsMain,
                        ["IsOrthographic"] = c.IsOrthographic,
                        ["OrthographicSize"] = c.OrthographicSize
                    }
                });
            }

            // TagComponent
            if (ecs.HasComponent<TagComponent>(id))
            {
                var tg = ecs.GetComponent<TagComponent>(id);
                entityData.Components.Add(new ComponentData
                {
                    TypeName = nameof(TagComponent),
                    Properties = new()
                    {
                        ["Tag"] = tg.Tag ?? string.Empty,
                        ["Layer"] = tg.Layer,
                        ["LayerMask"] = tg.LayerMask,
                        ["IsStatic"] = tg.IsStatic
                    }
                });
            }
        }

        // ── 脚本序列化辅助 ──

        private static void AddScriptData(EntityData entityData, ShanyuEngine.Scripting.ScriptRegistry scriptRegistry, int entityId)
        {
            var scripts = scriptRegistry.GetScripts(entityId);
            foreach (var script in scripts)
            {
                if (script == null) continue;

                var scriptData = new ScriptData
                {
                    TypeName = script.TypeName,
                    Enabled = script.Enabled,
                };

                // 序列化公共字段（跳过 EntityId、ECS、Engine、Registry 等内部字段）
                var fields = script.GetType().GetFields(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);
                foreach (var field in fields)
                {
                    if (field.IsInitOnly) continue;
                    if (field.Name == "EntityId") continue;
                    if (field.Name == "ECS") continue;
                    if (field.Name == "Engine") continue;
                    if (field.Name == "Registry") continue;
                    if (field.Name == "TypeName") continue;
                    if (field.Name == "Enabled") continue;
                    if (field.Name == "HasAwoken") continue;
                    if (field.Name == "HasStarted") continue;
                    if (field.Name == "ErrorCount") continue;
                    if (field.Name == "IsDestroyed") continue;
                    if (field.Name == "Position") continue;
                    if (field.Name == "Rotation") continue;
                    if (field.Name == "Scale") continue;

                    try
                    {
                        var value = field.GetValue(script);
                        if (value == null) continue;
                        // 只序列化基础类型、字符串、数组、字典
                        if (IsSerializableFieldType(value.GetType()))
                            scriptData.Fields[field.Name] = value;
                    }
                    catch { /* 跳过无法读取的字段 */ }
                }

                entityData.Scripts.Add(scriptData);
            }
        }

        private static bool IsSerializableFieldType(Type type)
        {
            if (type == null) return false;
            if (type.IsPrimitive) return true;
            if (type == typeof(string)) return true;
            if (type == typeof(decimal)) return true;
            if (type == typeof(DateTime)) return true;
            if (type == typeof(Guid)) return true;
            if (type.IsEnum) return true;
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                return elementType != null && IsSerializableFieldType(elementType);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                return IsSerializableFieldType(elementType);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var args = type.GetGenericArguments();
                return args[0] == typeof(string) && IsSerializableFieldType(args[1]);
            }
            // Vector3 / Quaternion / Matrix4x4 等常用类型
            if (type == typeof(System.Numerics.Vector2)) return true;
            if (type == typeof(System.Numerics.Vector3)) return true;
            if (type == typeof(System.Numerics.Vector4)) return true;
            if (type == typeof(System.Numerics.Quaternion)) return true;
            if (type == typeof(System.Numerics.Matrix4x4)) return true;
            return false;
        }

        // ── 从 SceneData 恢复 EntityComponentSystem ──

        /// <summary>
        /// 将 SceneData 中的实体恢复到 ECS，返回 GUID → 实体 int ID 映射。
        /// </summary>
        /// <param name="scene">场景数据。</param>
        /// <param name="ecs">实体组件系统。</param>
        /// <param name="scriptRegistry">可选的脚本注册表，用于恢复脚本实例。</param>
        public static Dictionary<Guid, int> RestoreToECS(SceneData scene, EntityComponentSystem ecs, ShanyuEngine.Scripting.ScriptRegistry? scriptRegistry = null)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (ecs == null) throw new ArgumentNullException(nameof(ecs));

            var guidToId = new Dictionary<Guid, int>();

            // 第一遍：创建所有实体（含变换与组件），暂不处理父子关系
            foreach (var ed in scene.Entities)
            {
                if (ed == null) continue;

                int id = ecs.CreateEntity(
                    name: ed.Name,
                    position: ed.Transform.ToPosition(),
                    rotation: ed.Transform.ToRotation(),
                    scale: ed.Transform.ToScale());

                guidToId[ed.GUID] = id;

                // 恢复组件
                if (ed.Components != null)
                {
                    foreach (var cd in ed.Components)
                        ApplyComponent(ecs, id, cd);
                }

                // 恢复脚本
                if (scriptRegistry != null && ed.Scripts != null)
                {
                    foreach (var sd in ed.Scripts)
                        ApplyScriptData(scriptRegistry, id, sd);
                }
            }

            // 第二遍：恢复父子关系
            foreach (var ed in scene.Entities)
            {
                if (ed == null || ed.ParentGUID == Guid.Empty) continue;
                if (!guidToId.TryGetValue(ed.GUID, out int childId)) continue;
                if (!guidToId.TryGetValue(ed.ParentGUID, out int parentId)) continue;

                // 设置 RelationshipComponent
                var rel = ecs.HasComponent<RelationshipComponent>(childId)
                    ? ecs.GetComponent<RelationshipComponent>(childId)
                    : new RelationshipComponent();
                rel.ParentId = parentId;
                rel.Children ??= new List<int>();
                ecs.SetComponent(childId, rel);

                // 同步父实体的 Children 列表
                if (ecs.HasComponent<RelationshipComponent>(parentId))
                {
                    var parentRel = ecs.GetComponent<RelationshipComponent>(parentId);
                    parentRel.Children ??= new List<int>();
                    if (!parentRel.Children.Contains(childId)) parentRel.Children.Add(childId);
                    ecs.SetComponent(parentId, parentRel);
                }
                else
                {
                    ecs.AddComponent(parentId, new RelationshipComponent
                    {
                        ParentId = 0,
                        Children = new List<int> { childId }
                    });
                }
            }

            return guidToId;
        }

        private static void ApplyScriptData(ShanyuEngine.Scripting.ScriptRegistry scriptRegistry, int entityId, ScriptData? sd)
        {
            if (sd == null || string.IsNullOrEmpty(sd.TypeName)) return;

            try
            {
                // 通过反射查找脚本类型
                var scriptType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.FullName == sd.TypeName && typeof(ShanyuEngine.Scripting.ScriptComponent).IsAssignableFrom(t));

                if (scriptType == null)
                {
                    // 类型未找到，记录警告并跳过
                    System.Diagnostics.Debug.WriteLine($"[SceneSerializer] Script type not found: {sd.TypeName}");
                    return;
                }

                // 创建实例并注册到 ScriptRegistry
                var instance = (ShanyuEngine.Scripting.ScriptComponent?)Activator.CreateInstance(scriptType);
                if (instance == null) return;

                instance.TypeName = sd.TypeName;
                instance.Enabled = sd.Enabled;

                // 恢复字段值
                if (sd.Fields != null)
                {
                    foreach (var fieldKv in sd.Fields)
                    {
                        var field = scriptType.GetField(fieldKv.Key,
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance);
                        if (field == null || field.IsInitOnly) continue;
                        try
                        {
                            var value = ConvertJsonValue(fieldKv.Value, field.FieldType);
                            if (value != null)
                                field.SetValue(instance, value);
                        }
                        catch { /* 跳过无法恢复的字段 */ }
                    }
                }

                scriptRegistry.AddScript(entityId, instance);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SceneSerializer] Failed to restore script {sd.TypeName}: {ex.Message}");
            }
        }

        /// <summary>将 JSON 反序列化的值转换为目标类型。</summary>
        private static object? ConvertJsonValue(object? value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            if (value is JsonElement je)
            {
                if (targetType == typeof(int)) return je.GetInt32();
                if (targetType == typeof(float)) return je.GetSingle();
                if (targetType == typeof(double)) return je.GetDouble();
                if (targetType == typeof(bool)) return je.GetBoolean();
                if (targetType == typeof(string)) return je.GetString();
                if (targetType == typeof(Guid)) return je.GetGuid();
                if (targetType == typeof(System.Numerics.Vector3) && je.ValueKind == JsonValueKind.Array)
                {
                    var arr = je.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    if (arr.Length >= 3) return new System.Numerics.Vector3(arr[0], arr[1], arr[2]);
                }
                if (targetType == typeof(System.Numerics.Quaternion) && je.ValueKind == JsonValueKind.Array)
                {
                    var arr = je.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    if (arr.Length >= 4) return new System.Numerics.Quaternion(arr[0], arr[1], arr[2], arr[3]);
                }
                if (targetType.IsEnum && je.ValueKind == JsonValueKind.String)
                    return Enum.Parse(targetType, je.GetString() ?? "0");
                if (targetType.IsEnum && je.ValueKind == JsonValueKind.Number)
                    return Enum.ToObject(targetType, je.GetInt32());
            }

            try { return System.Convert.ChangeType(value, targetType); }
            catch { return null; }
        }

        internal static void ApplyComponent(EntityComponentSystem ecs, int entityId, ComponentData? cd)
        {
            if (cd == null || string.IsNullOrEmpty(cd.TypeName)) return;
            var p = cd.Properties;

            switch (cd.TypeName)
            {
                case nameof(MeshComponent):
                    ecs.AddComponent(entityId, new MeshComponent
                    {
                        MeshId = Guid.TryParse(GetString(p, "MeshId"), out var mid) ? mid : Guid.Empty,
                        MaterialId = Guid.TryParse(GetString(p, "MaterialId"), out var matid) ? matid : Guid.Empty,
                        CastsShadow = GetBool(p, "CastsShadow", true),
                        ReceivesShadow = GetBool(p, "ReceivesShadow", true),
                        IsVisible = GetBool(p, "IsVisible", true),
                        TriangleCount = GetInt(p, "TriangleCount", 0),
                        VertexCount = GetInt(p, "VertexCount", 0)
                    });
                    break;

                case nameof(LightComponent):
                    ecs.AddComponent(entityId, new LightComponent
                    {
                        Type = Enum.TryParse<LightComponent.LightType>(GetString(p, "Type"), out var lt) ? lt : LightComponent.LightType.Directional,
                        Color = GetVector3(p, "Color", new Vector3(1f, 1f, 1f)),
                        Intensity = GetFloat(p, "Intensity", 1f),
                        Range = GetFloat(p, "Range", 10f),
                        InnerConeAngle = GetFloat(p, "InnerConeAngle", 0f),
                        OuterConeAngle = GetFloat(p, "OuterConeAngle", 0f),
                        CastsShadow = GetBool(p, "CastsShadow", true),
                        ShadowBias = GetFloat(p, "ShadowBias", 0f)
                    });
                    break;

                case nameof(CameraComponent):
                    ecs.AddComponent(entityId, new CameraComponent
                    {
                        FieldOfView = GetFloat(p, "FieldOfView", 60f),
                        AspectRatio = GetFloat(p, "AspectRatio", 16f / 9f),
                        NearClip = GetFloat(p, "NearClip", 0.1f),
                        FarClip = GetFloat(p, "FarClip", 1000f),
                        IsMain = GetBool(p, "IsMain", false),
                        IsOrthographic = GetBool(p, "IsOrthographic", false),
                        OrthographicSize = GetFloat(p, "OrthographicSize", 5f)
                    });
                    break;

                case nameof(TagComponent):
                    ecs.AddComponent(entityId, new TagComponent
                    {
                        Tag = GetString(p, "Tag"),
                        Layer = GetInt(p, "Layer", 0),
                        LayerMask = GetUInt(p, "LayerMask", 0xFFFFFFFF),
                        IsStatic = GetBool(p, "IsStatic", false)
                    });
                    break;
            }
        }

        // ── 属性读取辅助（兼容 JsonElement / 原始类型）──

        internal static string GetString(Dictionary<string, object?> p, string key, string def = "")
        {
            if (p.TryGetValue(key, out var v) && v != null)
            {
                if (v is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() ?? def : je.ToString();
                return v.ToString() ?? def;
            }
            return def;
        }

        internal static float GetFloat(Dictionary<string, object?> p, string key, float def)
        {
            if (p.TryGetValue(key, out var v) && v != null)
            {
                if (v is JsonElement je) return je.ValueKind == JsonValueKind.Number ? je.GetSingle() : def;
                if (v is float f) return f;
                if (v is double d) return (float)d;
                if (v is int i) return i;
                if (float.TryParse(v.ToString(), out float r)) return r;
            }
            return def;
        }

        internal static int GetInt(Dictionary<string, object?> p, string key, int def)
        {
            if (p.TryGetValue(key, out var v) && v != null)
            {
                if (v is JsonElement je) return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : def;
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (int.TryParse(v.ToString(), out int r)) return r;
            }
            return def;
        }

        internal static uint GetUInt(Dictionary<string, object?> p, string key, uint def)
        {
            if (p.TryGetValue(key, out var v) && v != null)
            {
                if (v is JsonElement je) return je.ValueKind == JsonValueKind.Number ? je.GetUInt32() : def;
                if (v is uint u) return u;
                if (v is long l) return (uint)l;
                if (uint.TryParse(v.ToString(), out uint r)) return r;
            }
            return def;
        }

        internal static bool GetBool(Dictionary<string, object?> p, string key, bool def)
        {
            if (p.TryGetValue(key, out var v) && v != null)
            {
                if (v is JsonElement je) return je.ValueKind == JsonValueKind.True || (je.ValueKind == JsonValueKind.False && je.GetBoolean());
                if (v is bool b) return b;
                if (bool.TryParse(v.ToString(), out bool r)) return r;
            }
            return def;
        }

        internal static Vector3 GetVector3(Dictionary<string, object?> p, string key, Vector3 def)
        {
            if (p.TryGetValue(key, out var v) && v != null)
            {
                float[]? arr = ToFloatArray(v);
                if (arr != null && arr.Length >= 3) return new Vector3(arr[0], arr[1], arr[2]);
            }
            return def;
        }

        internal static float[]? ToFloatArray(object? v)
        {
            if (v == null) return null;
            if (v is float[] fa) return fa;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var list = new List<float>();
                foreach (var item in je.EnumerateArray())
                    list.Add(item.ValueKind == JsonValueKind.Number ? item.GetSingle() : 0f);
                return list.ToArray();
            }
            if (v is System.Collections.IEnumerable enumerable)
            {
                var list = new List<float>();
                foreach (var item in enumerable)
                {
                    if (item is JsonElement je2 && je2.ValueKind == JsonValueKind.Number) list.Add(je2.GetSingle());
                    else if (item is float f) list.Add(f);
                    else if (item is double d) list.Add((float)d);
                    else if (item is int i) list.Add(i);
                }
                return list.ToArray();
            }
            return null;
        }
    }

    // ====================================================================
    // PrefabSerializer — 预制体序列化
    // ====================================================================

    /// <summary>
    /// 预制体序列化器。将单个 EntityData 序列化为 .prefab 文件，并可实例化到 ECS。
    /// </summary>
    public static class PrefabSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(), new ObjectJsonConverter() }
        };

        /// <summary>将实体数据保存为预制体文件。</summary>
        public static void SavePrefab(EntityData entity, string path)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Prefab path is required.", nameof(path));

            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(entity, Options), Encoding.UTF8);
        }

        /// <summary>从文件加载预制体实体数据。</summary>
        public static EntityData LoadPrefab(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"Prefab file not found: {path}", path);
            EntityData? data = JsonSerializer.Deserialize<EntityData>(File.ReadAllText(path, Encoding.UTF8), Options);
            if (data == null) throw new InvalidDataException($"Failed to deserialize prefab: {path}");
            return data;
        }

        /// <summary>
        /// 将预制体实体数据实例化到 ECS 中，返回新实体的 int ID。
        /// 实例化时会生成新的 GUID 以避免与现有实体冲突。
        /// </summary>
        public static int InstantiatePrefab(EntityData prefab, EntityComponentSystem ecs)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            if (ecs == null) throw new ArgumentNullException(nameof(ecs));

            return InstantiateInternal(prefab, ecs, Guid.Empty, new Dictionary<Guid, int>());
        }

        /// <summary>递归实例化预制体（支持嵌套子实体，若 EntityData.Components 中携带子实体信息）。</summary>
        private static int InstantiateInternal(EntityData prefab, EntityComponentSystem ecs, Guid parentGuid, Dictionary<Guid, int> guidMap)
        {
            // 生成新 GUID
            Guid newGuid = Guid.NewGuid();

            int id = ecs.CreateEntity(
                name: prefab.Name,
                position: prefab.Transform.ToPosition(),
                rotation: prefab.Transform.ToRotation(),
                scale: prefab.Transform.ToScale());

            guidMap[newGuid] = id;

            // 恢复组件
            if (prefab.Components != null)
            {
                foreach (var cd in prefab.Components)
                    SceneJsonSerializer.ApplyComponent(ecs, id, cd);
            }

            // 若有父实体，建立关系
            if (parentGuid != Guid.Empty && guidMap.TryGetValue(parentGuid, out int parentId))
            {
                ecs.AddComponent(id, new RelationshipComponent
                {
                    ParentId = parentId,
                    Children = new List<int>()
                });

                if (ecs.HasComponent<RelationshipComponent>(parentId))
                {
                    var parentRel = ecs.GetComponent<RelationshipComponent>(parentId);
                    parentRel.Children ??= new List<int>();
                    if (!parentRel.Children.Contains(id)) parentRel.Children.Add(id);
                    ecs.SetComponent(parentId, parentRel);
                }
            }

            return id;
        }
    }

    // ====================================================================
    // ObjectJsonConverter — 通用 object 值的 JSON 转换
    // ====================================================================

    /// <summary>
    /// 处理 Dictionary&lt;string, object?&gt; 中 object 值的多态 JSON 序列化/反序列化，
    /// 将 JSON 标量/数组/对象还原为 CLR 基础类型。
    /// </summary>
    public sealed class ObjectJsonConverter : JsonConverter<object?>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Null => null,
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out long l) ? (object)l : reader.GetDouble(),
                JsonTokenType.StartArray => ReadArray(ref reader, options),
                JsonTokenType.StartObject => ReadObject(ref reader, options),
                _ => JsonDocument.ParseValue(ref reader).RootElement.Clone()
            };
        }

        public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }

        private static object ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            var list = new List<object?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                list.Add(Read(ref reader, typeof(object), options));
            }
            // 数值数组优先返回 float[] 以便向量读取
            if (list.Count > 0 && list.All(e => e is long || e is double))
            {
                float[] arr = new float[list.Count];
                for (int i = 0; i < list.Count; i++)
                    arr[i] = list[i] is long li ? li : (float)(double)list[i]!;
                return arr;
            }
            return list;
        }

        private static object ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            var dict = new Dictionary<string, object?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                string key = reader.GetString() ?? string.Empty;
                reader.Read();
                dict[key] = Read(ref reader, typeof(object), options);
            }
            return dict;
        }
    }
}
