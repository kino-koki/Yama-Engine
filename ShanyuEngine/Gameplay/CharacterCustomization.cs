using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ShanyuEngine.ECS;
using ShanyuEngine.Modeling;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Gameplay
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 角色自定义系统 (Character Customization)
    //
    // 提供运行时角色外观自定义能力：部件（头/躯干/手臂/腿/发型/胡须/饰品/服装）、
    // 形变目标（BlendShape/Morph Target 滑块）、外观配置（肤色/眼睛/毛发/纹身/疤痕/
    // 妆容），以及预设保存/加载、随机生成、DNA 遗传生成后代。
    //
    // 对接关系：
    //   · Skinning：通过 Skeleton/SkeletonPose 计算骨骼插槽（Socket）的世界变换，
    //     把部件挂载到骨骼插槽上；BlendShape 形变作用于 EditableMesh 顶点。
    //   · Modeling：部件网格合并（CharacterMeshMerger）把多个部件烘焙为单一网格。
    //   · ECS：CharacterCustomizationComponent + CharacterCustomizationSystem，
    //     系统每帧应用部件/形变/外观/LOD 同步。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>性别。</summary>
    public enum Gender { Male, Female, NonBinary }

    /// <summary>体型。</summary>
    public enum BodyType { Slim, Average, Athletic, Heavy, Muscular }

    /// <summary>角色部件插槽（部件挂载位置）。</summary>
    public enum CharacterPartSlot
    {
        Head, Hair, Beard, FaceAccessory,
        Torso, Arms, Hands, Legs, Feet,
        ClothingTop, ClothingBottom, Outerwear, BackAccessory
    }

    /// <summary>形变类别。</summary>
    public enum MorphCategory { Face, Eyes, Nose, Mouth, Body, Custom }

    /// <summary>形变预设（一键套用一组形变权重）。</summary>
    public enum MorphPreset { Default, Muscular, Obese, Skinny, Athletic, Aged }

    // ════════════════════════════════════════════════════════════════════════
    // 部件插槽与部件
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 部件插槽（Socket）：定义部件挂载到骨骼上的局部变换。
    /// 挂载时：socketWorld = boneWorldMatrix * LocalMatrix。
    /// </summary>
    public sealed class PartSocket
    {
        /// <summary>插槽名称。</summary>
        public string Name { get; set; } = "Socket";
        /// <summary>绑定的骨骼名称（Skeleton.FindBone）。</summary>
        public string BoneName { get; set; } = "Head";
        /// <summary>相对骨骼的局部位置。</summary>
        public Vector3 LocalPosition { get; set; } = Vector3.Zero;
        /// <summary>相对骨骼的局部旋转。</summary>
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        /// <summary>相对骨骼的局部缩放。</summary>
        public Vector3 LocalScale { get; set; } = Vector3.One;

        /// <summary>局部变换矩阵。</summary>
        public Matrix4x4 LocalMatrix =>
            Matrix4x4.CreateScale(LocalScale) *
            Matrix4x4.CreateFromQuaternion(LocalRotation) *
            Matrix4x4.CreateTranslation(LocalPosition);

        /// <summary>计算插槽在给定骨骼世界矩阵下的世界变换。</summary>
        public Matrix4x4 ComputeWorld(Matrix4x4 boneWorld) => LocalMatrix * boneWorld;
    }

    /// <summary>部件变体：同一部件的多个可选网格（含 LOD 分级）。</summary>
    public sealed class CharacterPartVariant
    {
        /// <summary>变体 Id。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>变体名称。</summary>
        public string Name { get; set; } = "Variant";
        /// <summary>基础网格 Guid（用于 ECS MeshComponent.MeshId）。</summary>
        public Guid MeshGuid { get; set; }
        /// <summary>基础网格文件路径（运行时加载用）。</summary>
        public string MeshPath { get; set; } = string.Empty;
        /// <summary>按 LOD（0=最低/远 .. N=最高/近）分级的网格路径；缺省回退到 MeshPath。</summary>
        public Dictionary<int, string> LodVariants { get; set; } = new();
        /// <summary>材质覆盖（材质参数名 -> 值）。</summary>
        public Dictionary<string, Vector4> MaterialOverride { get; set; } = new();

        /// <summary>获取指定 LOD 的网格路径。</summary>
        public string GetMeshPath(int lod)
        {
            return LodVariants.TryGetValue(lod, out var p) && !string.IsNullOrEmpty(p) ? p : MeshPath;
        }
    }

    /// <summary>
    /// 角色部件：可挂载到骨骼插槽上的网格集合（头/躯干/手臂/腿/发型/胡须/饰品/服装）。
    /// 一个部件包含多个可选变体与材质覆盖。
    /// </summary>
    public sealed class CharacterPart
    {
        /// <summary>部件 Id。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>部件名称。</summary>
        public string Name { get; set; } = "Part";
        /// <summary>所属插槽。</summary>
        public CharacterPartSlot Slot { get; set; }
        /// <summary>挂载插槽。</summary>
        public PartSocket? Socket { get; set; }
        /// <summary>可选变体列表。</summary>
        public List<CharacterPartVariant> Variants { get; set; } = new();
        /// <summary>当前选中的变体索引。</summary>
        public int CurrentVariantIndex { get; set; }
        /// <summary>部件级材质覆盖（覆盖该部件所有变体）。</summary>
        public Dictionary<string, Vector4> MaterialOverride { get; set; } = new();
        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>当前变体。</summary>
        public CharacterPartVariant? CurrentVariant =>
            CurrentVariantIndex >= 0 && CurrentVariantIndex < Variants.Count ? Variants[CurrentVariantIndex] : null;

        /// <summary>获取当前 LOD 下的网格路径。</summary>
        public string? GetCurrentMeshPath(int lod) => CurrentVariant?.GetMeshPath(lod);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 形变目标（BlendShape / Morph Target）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 形变目标（BlendShape）：存储相对基础网格的逐顶点位移增量。
    /// 应用时：vertex = baseVertex + delta * weight。
    /// 这是 Skinning 系统中 BlendShape 的数据载体（Skinning 模块尚未提供 BlendShape，
    /// 此处补全并与 Skeleton/EditableMesh 协同）。
    /// </summary>
    public sealed class BlendShape
    {
        /// <summary>形变名称。</summary>
        public string Name { get; set; } = "BlendShape";
        /// <summary>目标网格文件路径（标识作用于哪个 EditableMesh）。</summary>
        public string TargetMeshPath { get; set; } = string.Empty;
        /// <summary>逐顶点位移增量（顶点索引 -> 位移）。</summary>
        public List<(int Index, Vector3 Delta)> Deltas { get; set; } = new();

        /// <summary>把该形变按权重叠加到顶点缓冲（在已有的顶点基础上累加）。</summary>
        public void Apply(List<Vector3> vertices, float weight)
        {
            if (vertices == null) return;
            foreach (var (index, delta) in Deltas)
            {
                if (index >= 0 && index < vertices.Count)
                    vertices[index] += delta * weight;
            }
        }

        /// <summary>从两个网格的差异构造形变（target - base）。</summary>
        public static BlendShape FromMeshes(string name, EditableMesh baseMesh, EditableMesh targetMesh)
        {
            var bs = new BlendShape { Name = name, TargetMeshPath = baseMesh.Name };
            int count = Math.Min(baseMesh.Vertices.Count, targetMesh.Vertices.Count);
            for (int i = 0; i < count; i++)
            {
                var d = targetMesh.Vertices[i] - baseMesh.Vertices[i];
                if (d.LengthSquared() > 1e-8f)
                    bs.Deltas.Add((i, d));
            }
            return bs;
        }
    }

    /// <summary>
    /// 角色形变：一个 BlendShape 滑块（0~1 权重），归属某类别，可挂预设权重。
    /// 预设形变（脸型/眼型/鼻型/嘴型/体型）与形变预设（肌肉/肥胖/瘦弱/运动型）。
    /// </summary>
    public sealed class CharacterMorph
    {
        /// <summary>形变 Id。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>滑块名称。</summary>
        public string Name { get; set; } = "Morph";
        /// <summary>类别。</summary>
        public MorphCategory Category { get; set; } = MorphCategory.Custom;
        /// <summary>关联的 BlendShape。</summary>
        public BlendShape? BlendShape { get; set; }
        /// <summary>当前权重（0~1）。</summary>
        public float Weight { get; set; }
        /// <summary>最小权重。</summary>
        public float Min { get; set; } = 0.0f;
        /// <summary>最大权重。</summary>
        public float Max { get; set; } = 1.0f;
        /// <summary>预设权重（一键套用）。</summary>
        public Dictionary<MorphPreset, float> PresetWeights { get; set; } = new();

        /// <summary>设置权重并钳制到 [Min, Max]。</summary>
        public void SetWeight(float w) => Weight = Math.Clamp(w, Min, Max);

        /// <summary>套用某预设。</summary>
        public void ApplyPreset(MorphPreset preset)
        {
            if (PresetWeights.TryGetValue(preset, out var w)) Weight = Math.Clamp(w, Min, Max);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 外观配置
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 外观配置：肤色色相/眼睛颜色/毛发颜色/纹身/疤痕/妆容等材质参数。
    /// </summary>
    public sealed class CharacterAppearance
    {
        // —— 肤色 ——
        /// <summary>肤色基础色（线性 RGB）。</summary>
        public Vector3 SkinColor { get; set; } = new(0.85f, 0.7f, 0.6f);
        /// <summary>肤色色相偏移（-1~1）。</summary>
        public float SkinHueShift { get; set; }
        /// <summary>肤色饱和度（0~2）。</summary>
        public float SkinSaturation { get; set; } = 1.0f;
        /// <summary>肤色亮度（0~2）。</summary>
        public float SkinBrightness { get; set; } = 1.0f;

        // —— 眼睛 ——
        /// <summary>眼睛虹膜颜色。</summary>
        public Vector3 EyeColor { get; set; } = new(0.3f, 0.5f, 0.8f);

        // —— 毛发 ——
        /// <summary>毛发颜色。</summary>
        public Vector3 HairColor { get; set; } = new(0.15f, 0.1f, 0.05f);
        /// <summary>毛发发根颜色（用于渐变）。</summary>
        public Vector3 HairRootColor { get; set; } = new(0.1f, 0.07f, 0.04f);

        // —— 妆容 ——
        /// <summary>是否启用妆容。</summary>
        public bool MakeupEnabled { get; set; }
        /// <summary>口红颜色。</summary>
        public Vector3 LipstickColor { get; set; } = new(0.8f, 0.2f, 0.25f);
        /// <summary>口红强度（0~1）。</summary>
        public float LipstickIntensity { get; set; }
        /// <summary>腮红颜色。</summary>
        public Vector3 BlushColor { get; set; } = new(0.9f, 0.4f, 0.4f);
        /// <summary>腮红强度。</summary>
        public float BlushIntensity { get; set; }
        /// <summary>眼影颜色。</summary>
        public Vector3 EyeshadowColor { get; set; } = new(0.4f, 0.3f, 0.5f);
        /// <summary>眼影强度。</summary>
        public float EyeshadowIntensity { get; set; }

        // —— 纹身/疤痕/雀斑 ——
        /// <summary>纹身遮罩纹理路径。</summary>
        public string TattooMaskPath { get; set; } = string.Empty;
        /// <summary>纹身强度（0~1）。</summary>
        public float TattooIntensity { get; set; }
        /// <summary>疤痕遮罩纹理路径。</summary>
        public string ScarMaskPath { get; set; } = string.Empty;
        /// <summary>疤痕强度。</summary>
        public float ScarIntensity { get; set; }
        /// <summary>雀斑强度（0~1）。</summary>
        public float FrecklesIntensity { get; set; }

        /// <summary>导出为材质参数字典（供渲染器读取并写入材质常量）。</summary>
        public Dictionary<string, Vector4> ToMaterialParameters()
        {
            return new Dictionary<string, Vector4>
            {
                ["SkinColor"] = new Vector4(SkinColor, 1.0f),
                ["SkinHueShift"] = new Vector4(SkinHueShift, SkinSaturation, SkinBrightness, 0.0f),
                ["EyeColor"] = new Vector4(EyeColor, 1.0f),
                ["HairColor"] = new Vector4(HairColor, 1.0f),
                ["HairRootColor"] = new Vector4(HairRootColor, 1.0f),
                ["Lipstick"] = new Vector4(LipstickColor, LipstickIntensity),
                ["Blush"] = new Vector4(BlushColor, BlushIntensity),
                ["Eyeshadow"] = new Vector4(EyeshadowColor, EyeshadowIntensity),
                ["Tattoo"] = new Vector4(TattooIntensity, 0, 0, MakeupEnabled ? 1f : 0f),
                ["Scar"] = new Vector4(ScarIntensity, FrecklesIntensity, 0, 0)
            };
        }

        /// <summary>深拷贝。</summary>
        public CharacterAppearance Clone()
        {
            return new CharacterAppearance
            {
                SkinColor = SkinColor, SkinHueShift = SkinHueShift,
                SkinSaturation = SkinSaturation, SkinBrightness = SkinBrightness,
                EyeColor = EyeColor, HairColor = HairColor, HairRootColor = HairRootColor,
                MakeupEnabled = MakeupEnabled, LipstickColor = LipstickColor,
                LipstickIntensity = LipstickIntensity, BlushColor = BlushColor,
                BlushIntensity = BlushIntensity, EyeshadowColor = EyeshadowColor,
                EyeshadowIntensity = EyeshadowIntensity, TattooMaskPath = TattooMaskPath,
                TattooIntensity = TattooIntensity, ScarMaskPath = ScarMaskPath,
                ScarIntensity = ScarIntensity, FrecklesIntensity = FrecklesIntensity
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 角色预设
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 角色预设：体型/性别/肤色/发型/面部特征等可序列化配置，用于保存与加载。
    /// </summary>
    public sealed class CharacterPreset
    {
        /// <summary>预设名称。</summary>
        public string Name { get; set; } = "CharacterPreset";
        /// <summary>性别。</summary>
        public Gender Gender { get; set; } = Gender.Male;
        /// <summary>体型。</summary>
        public BodyType BodyType { get; set; } = BodyType.Average;
        /// <summary>肤色。</summary>
        public Vector3 SkinTone { get; set; } = new(0.85f, 0.7f, 0.6f);
        /// <summary>发型部件 Id。</summary>
        public Guid HairStylePartId { get; set; }
        /// <summary>胡须部件 Id。</summary>
        public Guid BeardPartId { get; set; }
        /// <summary>面部特征形变权重（形变名 -> 权重）。</summary>
        public Dictionary<string, float> FacialFeatureMorphs { get; set; } = new();
        /// <summary>各插槽部件分配（插槽 -> 部件 Id）。</summary>
        public Dictionary<CharacterPartSlot, Guid> PartAssignments { get; set; } = new();
        /// <summary>全部形变权重（形变名 -> 权重）。</summary>
        public Dictionary<string, float> MorphWeights { get; set; } = new();
        /// <summary>外观配置。</summary>
        public CharacterAppearance Appearance { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>深拷贝。</summary>
        public CharacterPreset Clone()
        {
            return new CharacterPreset
            {
                Name = Name + "_Clone",
                Gender = Gender,
                BodyType = BodyType,
                SkinTone = SkinTone,
                HairStylePartId = HairStylePartId,
                BeardPartId = BeardPartId,
                FacialFeatureMorphs = new Dictionary<string, float>(FacialFeatureMorphs),
                PartAssignments = new Dictionary<CharacterPartSlot, Guid>(PartAssignments),
                MorphWeights = new Dictionary<string, float>(MorphWeights),
                Appearance = Appearance.Clone()
            };
        }

        /// <summary>保存到文件（JSON）。</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        /// <summary>从文件加载。</summary>
        public static CharacterPreset Load(string path)
        {
            return JsonSerializer.Deserialize<CharacterPreset>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException($"Invalid character preset: {path}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 角色自定义运行时数据（Config）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 角色自定义运行时数据：持有部件、形变、外观、基础网格与骨骼，以及已挂载的部件实体。
    /// 由 CharacterCustomizationComponent 引用，由 CharacterCustomizationSystem 应用。
    /// </summary>
    public sealed class CharacterConfig
    {
        /// <summary>角色配置 Id。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>角色名称。</summary>
        public string Name { get; set; } = "Character";
        /// <summary>骨架（用于计算骨骼插槽变换；可为 null）。</summary>
        public Skeleton? Skeleton { get; set; }
        /// <summary>骨架绑定姿势。</summary>
        public SkeletonPose? BindPose { get; set; }
        /// <summary>基础身体网格（形变作用对象）。</summary>
        public EditableMesh? BaseMesh { get; set; }
        /// <summary>基础网格的原始顶点位置（形变重算基准）。</summary>
        public List<Vector3> BasePositions { get; set; } = new();
        /// <summary>各插槽的部件（插槽 -> 部件）。</summary>
        public Dictionary<CharacterPartSlot, CharacterPart> Parts { get; set; } = new();
        /// <summary>形变滑块列表。</summary>
        public List<CharacterMorph> Morphs { get; set; } = new();
        /// <summary>外观配置。</summary>
        public CharacterAppearance Appearance { get; set; } = new();
        /// <summary>当前预设。</summary>
        public CharacterPreset Preset { get; set; } = new();
        /// <summary>已挂载的部件实体 Id（插槽 -> 实体 Id）。</summary>
        public Dictionary<CharacterPartSlot, int> AttachedPartEntities { get; set; } = new();
        /// <summary>当前 LOD（0=最低/远 .. N=最高/近）。</summary>
        public int CurrentLod { get; set; } = 3;
        /// <summary>当前生效的材质参数（外观应用后生成，供渲染器读取）。</summary>
        public Dictionary<string, Vector4> MaterialParameters { get; set; } = new();

        /// <summary>初始化基础位置快照（在设置 BaseMesh 后调用一次）。</summary>
        public void CaptureBasePositions()
        {
            BasePositions = BaseMesh != null
                ? new List<Vector3>(BaseMesh.Vertices)
                : new List<Vector3>();
        }

        /// <summary>按名称查找形变。</summary>
        public CharacterMorph? FindMorph(string name)
        {
            foreach (var m in Morphs) if (m.Name == name) return m;
            return null;
        }

        /// <summary>获取某插槽的部件。</summary>
        public CharacterPart? GetPart(CharacterPartSlot slot) =>
            Parts.TryGetValue(slot, out var p) ? p : null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // ECS 组件与系统
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 角色自定义组件（ECS）。挂载到角色实体上，引用 CharacterConfig，
    /// 并用脏标记驱动系统按需应用部件/形变/外观。
    /// </summary>
    public struct CharacterCustomizationComponent : IComponent
    {
        /// <summary>角色自定义运行时数据。</summary>
        public CharacterConfig? Config;

        /// <summary>部件有变更（挂载/卸载/替换）。</summary>
        public bool PartsDirty;

        /// <summary>形变权重有变更。</summary>
        public bool MorphsDirty;

        /// <summary>外观配置有变更。</summary>
        public bool AppearanceDirty;

        /// <summary>LOD 有变更（驱动部件 LOD 同步）。</summary>
        public bool LodDirty;
    }

    /// <summary>
    /// 角色自定义系统（ECS ISystem）。每帧对带有 CharacterCustomizationComponent 的
    /// 角色实体应用：部件挂载/替换、形变目标、外观材质参数、部件 LOD 同步。
    /// </summary>
    public sealed class CharacterCustomizationSystem : ISystem
    {
        private Vector3 _cameraPosition;
        private bool _cameraValid;

        /// <summary>系统名称。</summary>
        public string Name => "CharacterCustomizationSystem";

        /// <summary>外部注入摄像机位置（用于部件 LOD 同步）。</summary>
        public void SetCameraPosition(Vector3 position)
        {
            _cameraPosition = position;
            _cameraValid = true;
        }

        /// <summary>清除外部摄像机位置。</summary>
        public void ClearCameraPosition() => _cameraValid = false;

        /// <summary>每帧更新。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;

            // 摄像机位置（未注入则查询主摄像机）
            Vector3 camPos = _cameraPosition;
            if (!_cameraValid) camPos = ResolveCamera(ecs);

            var entities = ecs.QueryWithComponents<TransformComponent, CharacterCustomizationComponent>();
            foreach (var entityId in entities)
            {
                var comp = ecs.GetComponent<CharacterCustomizationComponent>(entityId);
                if (comp.Config == null) continue;
                var cfg = comp.Config;
                var tf = ecs.GetComponent<TransformComponent>(entityId);

                // LOD 同步：根据距离选择 LOD，变化时标记部件脏
                int lod = ComputeLod(camPos, tf.Position);
                if (lod != cfg.CurrentLod)
                {
                    cfg.CurrentLod = lod;
                    comp.LodDirty = true;
                    comp.PartsDirty = true;
                }

                // 应用部件
                if (comp.PartsDirty)
                {
                    ApplyParts(ecs, entityId, cfg);
                    comp.PartsDirty = false;
                    comp.LodDirty = false;
                }

                // 应用形变
                if (comp.MorphsDirty)
                {
                    ApplyMorphs(cfg);
                    comp.MorphsDirty = false;
                }

                // 应用外观
                if (comp.AppearanceDirty)
                {
                    ApplyAppearance(cfg);
                    comp.AppearanceDirty = false;
                }

                ecs.SetComponent(entityId, comp);
            }
        }

        // ── 应用部件到骨架 ──────────────────────────────────────────────────

        /// <summary>应用部件：为每个插槽创建/更新部件实体，并挂载到骨骼插槽世界位置。</summary>
        private void ApplyParts(EntityComponentSystem ecs, int ownerEntityId, CharacterConfig cfg)
        {
            // 预计算骨骼世界矩阵（若有骨架）
            Matrix4x4[]? boneWorlds = null;
            if (cfg.Skeleton != null)
            {
                boneWorlds = cfg.Skeleton.ComputeWorldMatrices(cfg.BindPose);
            }

            foreach (var (slot, part) in cfg.Parts)
            {
                if (!part.IsEnabled || part.Variants.Count == 0)
                {
                    // 卸载已挂载的部件实体
                    if (cfg.AttachedPartEntities.TryGetValue(slot, out var childId) && ecs.HasEntity(childId))
                        ecs.DestroyEntity(childId);
                    cfg.AttachedPartEntities.Remove(slot);
                    continue;
                }

                var variant = part.CurrentVariant;
                if (variant == null) continue;

                // 计算插槽世界变换
                Matrix4x4 socketWorld = Matrix4x4.Identity;
                if (part.Socket != null && cfg.Skeleton != null)
                {
                    int boneIdx = cfg.Skeleton.FindBone(part.Socket.BoneName);
                    if (boneIdx >= 0 && boneWorlds != null)
                        socketWorld = part.Socket.ComputeWorld(boneWorlds[boneIdx]);
                }

                Vector3 socketPos = new(socketWorld.M41, socketWorld.M42, socketWorld.M43);
                Quaternion socketRot = Quaternion.CreateFromRotationMatrix(socketWorld);

                // 创建或更新部件实体
                if (!cfg.AttachedPartEntities.TryGetValue(slot, out var childId) || !ecs.HasEntity(childId))
                {
                    childId = ecs.CreateEntity(part.Name, socketPos, socketRot);
                    ecs.AddComponent(childId, new MeshComponent
                    {
                        MeshId = variant.MeshGuid,
                        MaterialId = Guid.Empty,
                        CastsShadow = true,
                        ReceivesShadow = true,
                        IsVisible = true,
                        TriangleCount = 0,
                        VertexCount = 0
                    });
                    ecs.AddComponent(childId, new RelationshipComponent
                    {
                        ParentId = ownerEntityId,
                        Children = new List<int>()
                    });
                    cfg.AttachedPartEntities[slot] = childId;
                }
                else
                {
                    // 更新网格（变体或 LOD 变化）
                    var mesh = ecs.GetComponent<MeshComponent>(childId);
                    mesh.MeshId = variant.MeshGuid;
                    ecs.SetComponent(childId, mesh);

                    var ctf = ecs.GetComponent<TransformComponent>(childId);
                    ctf.Position = socketPos;
                    ctf.Rotation = socketRot;
                    ecs.SetComponent(childId, ctf);
                }
            }
        }

        // ── 应用形变目标 ────────────────────────────────────────────────────

        /// <summary>应用形变：从基础位置重算顶点 = base + Σ(delta * weight)。</summary>
        private void ApplyMorphs(CharacterConfig cfg)
        {
            if (cfg.BaseMesh == null || cfg.BasePositions.Count == 0) return;
            var verts = cfg.BaseMesh.Vertices;

            // 重置为基础位置
            for (int i = 0; i < verts.Count && i < cfg.BasePositions.Count; i++)
                verts[i] = cfg.BasePositions[i];

            // 叠加各形变
            foreach (var morph in cfg.Morphs)
            {
                if (morph.BlendShape == null) continue;
                morph.BlendShape.Apply(verts, morph.Weight);
            }

            // 重算法线
            cfg.BaseMesh.RecalculateNormals();
        }

        // ── 应用外观配置 ────────────────────────────────────────────────────

        /// <summary>应用外观：生成材质参数字典（供渲染器写入材质常量）。</summary>
        private void ApplyAppearance(CharacterConfig cfg)
        {
            cfg.MaterialParameters = cfg.Appearance.ToMaterialParameters();
        }

        // ── LOD 计算 ────────────────────────────────────────────────────────

        /// <summary>根据摄像机距离计算 LOD（0=最低/远 .. 3=最高/近）。</summary>
        private static int ComputeLod(Vector3 camera, Vector3 character)
        {
            float dist = Vector3.Distance(camera, character);
            if (dist > 60.0f) return 0;
            if (dist > 30.0f) return 1;
            if (dist > 12.0f) return 2;
            return 3;
        }

        private static Vector3 ResolveCamera(EntityComponentSystem ecs)
        {
            var cams = ecs.QueryWithComponents<TransformComponent, CameraComponent>();
            foreach (var id in cams)
            {
                var cam = ecs.GetComponent<CameraComponent>(id);
                if (cam.IsMain) return ecs.GetComponent<TransformComponent>(id).Position;
            }
            foreach (var id in cams) return ecs.GetComponent<TransformComponent>(id).Position;
            return Vector3.Zero;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 角色自定义器（运行时 API）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 角色自定义器：针对单个角色实体的运行时 API，封装部件/形变/外观/预设/随机生成。
    /// 内部修改 CharacterConfig 并通过脏标记驱动 CharacterCustomizationSystem 应用。
    /// </summary>
    public sealed class CharacterCustomizer
    {
        private readonly EntityComponentSystem _ecs;
        private readonly int _entityId;

        /// <summary>构造：绑定角色实体（须已挂载 CharacterCustomizationComponent）。</summary>
        public CharacterCustomizer(EntityComponentSystem ecs, int entityId)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _entityId = entityId;
            if (!ecs.HasComponent<CharacterCustomizationComponent>(entityId))
                throw new InvalidOperationException("Entity has no CharacterCustomizationComponent.");
        }

        /// <summary>角色配置。</summary>
        public CharacterConfig Config
        {
            get => _ecs.GetComponent<CharacterCustomizationComponent>(_entityId).Config
                   ?? throw new InvalidOperationException("CharacterConfig is null.");
        }

        /// <summary>标记部件脏。</summary>
        private void MarkPartsDirty()
        {
            var c = _ecs.GetComponent<CharacterCustomizationComponent>(_entityId);
            c.PartsDirty = true;
            _ecs.SetComponent(_entityId, c);
        }

        /// <summary>标记形变脏。</summary>
        private void MarkMorphsDirty()
        {
            var c = _ecs.GetComponent<CharacterCustomizationComponent>(_entityId);
            c.MorphsDirty = true;
            _ecs.SetComponent(_entityId, c);
        }

        /// <summary>标记外观脏。</summary>
        private void MarkAppearanceDirty()
        {
            var c = _ecs.GetComponent<CharacterCustomizationComponent>(_entityId);
            c.AppearanceDirty = true;
            _ecs.SetComponent(_entityId, c);
        }

        // —— 部件 API ——

        /// <summary>设置某插槽的部件（按部件 Id 选择；若不存在则移除该插槽部件）。</summary>
        public void SetPart(CharacterPartSlot slot, Guid partId)
        {
            var cfg = Config;
            // 在已注册部件库中查找（这里假设部件已存在于 Config.Parts；外部可先注册）
            if (cfg.Parts.TryGetValue(slot, out var existing) && existing.Id == partId)
            {
                MarkPartsDirty();
                return;
            }
            // 若 partId 为空，则移除插槽
            if (partId == Guid.Empty)
            {
                cfg.Parts.Remove(slot);
                MarkPartsDirty();
                return;
            }
            // 标记现有部件启用
            if (cfg.Parts.TryGetValue(slot, out var part))
            {
                part.IsEnabled = true;
                MarkPartsDirty();
            }
        }

        /// <summary>切换某插槽部件的变体（按变体索引）。</summary>
        public void SetPartVariant(CharacterPartSlot slot, int variantIndex)
        {
            var cfg = Config;
            if (cfg.Parts.TryGetValue(slot, out var part))
            {
                part.CurrentVariantIndex = Math.Clamp(variantIndex, 0, Math.Max(0, part.Variants.Count - 1));
                MarkPartsDirty();
            }
        }

        /// <summary>移除某插槽部件。</summary>
        public void RemovePart(CharacterPartSlot slot)
        {
            if (Config.Parts.Remove(slot)) MarkPartsDirty();
        }

        // —— 形变 API ——

        /// <summary>设置形变权重（按名称）。</summary>
        public void SetMorph(string morphName, float weight)
        {
            var morph = Config.FindMorph(morphName);
            if (morph != null)
            {
                morph.SetWeight(weight);
                MarkMorphsDirty();
            }
        }

        /// <summary>套用形变预设。</summary>
        public void ApplyMorphPreset(MorphPreset preset)
        {
            foreach (var morph in Config.Morphs) morph.ApplyPreset(preset);
            MarkMorphsDirty();
        }

        // —— 外观 API ——

        /// <summary>设置外观参数（浮点类）。</summary>
        public void SetAppearance(string param, float value)
        {
            var app = Config.Appearance;
            switch (param)
            {
                case "SkinHueShift": app.SkinHueShift = value; break;
                case "SkinSaturation": app.SkinSaturation = value; break;
                case "SkinBrightness": app.SkinBrightness = value; break;
                case "LipstickIntensity": app.LipstickIntensity = value; break;
                case "BlushIntensity": app.BlushIntensity = value; break;
                case "EyeshadowIntensity": app.EyeshadowIntensity = value; break;
                case "TattooIntensity": app.TattooIntensity = value; break;
                case "ScarIntensity": app.ScarIntensity = value; break;
                case "FrecklesIntensity": app.FrecklesIntensity = value; break;
                default: return;
            }
            MarkAppearanceDirty();
        }

        /// <summary>设置外观参数（向量类）。</summary>
        public void SetAppearance(string param, Vector3 value)
        {
            var app = Config.Appearance;
            switch (param)
            {
                case "SkinColor": app.SkinColor = value; break;
                case "EyeColor": app.EyeColor = value; break;
                case "HairColor": app.HairColor = value; break;
                case "HairRootColor": app.HairRootColor = value; break;
                case "LipstickColor": app.LipstickColor = value; break;
                case "BlushColor": app.BlushColor = value; break;
                case "EyeshadowColor": app.EyeshadowColor = value; break;
                default: return;
            }
            MarkAppearanceDirty();
        }

        /// <summary>启用/禁用妆容。</summary>
        public void SetMakeupEnabled(bool enabled)
        {
            Config.Appearance.MakeupEnabled = enabled;
            MarkAppearanceDirty();
        }

        // —— 预设 API ——

        /// <summary>从当前配置生成预设并保存到文件。</summary>
        public void SavePreset(string path)
        {
            var cfg = Config;
            var preset = cfg.Preset;
            preset.Appearance = cfg.Appearance.Clone();
            preset.MorphWeights = cfg.Morphs.ToDictionary(m => m.Name, m => m.Weight);
            preset.PartAssignments = cfg.Parts
                .Where(p => p.Value != null)
                .ToDictionary(p => p.Key, p => p.Value.Id);
            preset.Save(path);
        }

        /// <summary>从文件加载预设并应用到当前配置。</summary>
        public void LoadPreset(string path)
        {
            var preset = CharacterPreset.Load(path);
            var cfg = Config;
            cfg.Preset = preset;
            cfg.Appearance = preset.Appearance.Clone();

            // 应用形变权重
            foreach (var kvp in preset.MorphWeights)
            {
                var morph = cfg.FindMorph(kvp.Key);
                if (morph != null) morph.SetWeight(kvp.Value);
            }

            MarkPartsDirty();
            MarkMorphsDirty();
            MarkAppearanceDirty();
        }

        // —— 随机生成 ——

        /// <summary>随机生成外观（性别/体型/肤色/毛发/眼睛/形变/妆容）。</summary>
        public void Randomize(int seed)
        {
            var rnd = new Random(seed);
            var cfg = Config;
            var app = cfg.Appearance;

            // 性别与体型
            cfg.Preset.Gender = (Gender)rnd.Next(0, 3);
            cfg.Preset.BodyType = (BodyType)rnd.Next(0, 5);

            // 肤色（在合理范围内随机）
            float s = 0.4f + (float)rnd.NextDouble() * 0.5f;
            float r = 0.5f + (float)rnd.NextDouble() * 0.45f;
            float g = 0.35f + (float)rnd.NextDouble() * 0.4f;
            float b = 0.25f + (float)rnd.NextDouble() * 0.35f;
            app.SkinColor = new Vector3(r * s, g * s, b * s);
            app.SkinHueShift = ((float)rnd.NextDouble() * 2.0f - 1.0f) * 0.1f;

            // 眼睛颜色
            app.EyeColor = new Vector3(
                (float)rnd.NextDouble(),
                (float)rnd.NextDouble() * 0.6f,
                (float)rnd.NextDouble() * 0.4f);

            // 毛发颜色
            float hv = (float)rnd.NextDouble() * 0.4f;
            app.HairColor = new Vector3(hv, hv * 0.7f, hv * 0.5f);
            app.HairRootColor = app.HairColor * 0.7f;

            // 形变随机
            foreach (var morph in cfg.Morphs)
                morph.SetWeight((float)rnd.NextDouble());

            // 妆容（女性偏好）
            if (cfg.Preset.Gender == Gender.Female && rnd.NextDouble() < 0.6)
            {
                app.MakeupEnabled = true;
                app.LipstickIntensity = (float)rnd.NextDouble() * 0.8f;
                app.BlushIntensity = (float)rnd.NextDouble() * 0.5f;
            }
            else
            {
                app.MakeupEnabled = false;
            }

            // 部件变体随机
            foreach (var part in cfg.Parts.Values)
                if (part.Variants.Count > 0)
                    part.CurrentVariantIndex = rnd.Next(0, part.Variants.Count);

            MarkPartsDirty();
            MarkMorphsDirty();
            MarkAppearanceDirty();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // DNA 遗传系统（基于父母特征生成后代外观）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DNA 遗传系统：基于父母双方预设，按简单孟德尔式随机继承 + 轻微突变，
    /// 生成后代角色预设。
    /// </summary>
    public static class DnaSystem
    {
        /// <summary>由两位父母预设生成后代预设。</summary>
        /// <param name="parentA">父/母 A。</param>
        /// <param name="parentB">父/母 B。</param>
        /// <param name="seed">随机种子（-1 表示时间随机）。</param>
        public static CharacterPreset GenerateOffspring(CharacterPreset parentA, CharacterPreset parentB, int seed = -1)
        {
            if (parentA == null) throw new ArgumentNullException(nameof(parentA));
            if (parentB == null) throw new ArgumentNullException(nameof(parentB));
            var rnd = seed < 0 ? new Random() : new Random(seed);

            var child = new CharacterPreset { Name = "Offspring" };

            // 性别：均等概率
            child.Gender = rnd.NextDouble() < 0.5 ? parentA.Gender : parentB.Gender;

            // 体型：继承一方 + 突变
            child.BodyType = rnd.NextDouble() < 0.5 ? parentA.BodyType : parentB.BodyType;
            if (rnd.NextDouble() < 0.15)
                child.BodyType = (BodyType)rnd.Next(0, 5);

            // 肤色：混合 + 突变
            float t = (float)rnd.NextDouble();
            child.SkinTone = Vector3.Lerp(parentA.SkinTone, parentB.SkinTone, t);
            child.SkinTone += new Vector3(
                ((float)rnd.NextDouble() * 2 - 1) * 0.03f,
                ((float)rnd.NextDouble() * 2 - 1) * 0.03f,
                ((float)rnd.NextDouble() * 2 - 1) * 0.03f);

            // 发型/胡须：继承一方
            child.HairStylePartId = rnd.NextDouble() < 0.5 ? parentA.HairStylePartId : parentB.HairStylePartId;
            child.BeardPartId = rnd.NextDouble() < 0.5 ? parentA.BeardPartId : parentB.BeardPartId;

            // 部件分配：逐插槽继承一方
            var slots = new HashSet<CharacterPartSlot>();
            foreach (var k in parentA.PartAssignments.Keys) slots.Add(k);
            foreach (var k in parentB.PartAssignments.Keys) slots.Add(k);
            foreach (var slot in slots)
            {
                bool hasA = parentA.PartAssignments.TryGetValue(slot, out var a);
                bool hasB = parentB.PartAssignments.TryGetValue(slot, out var b);
                if (hasA && hasB)
                    child.PartAssignments[slot] = rnd.NextDouble() < 0.5 ? a : b;
                else if (hasA)
                    child.PartAssignments[slot] = a;
                else if (hasB)
                    child.PartAssignments[slot] = b;
            }

            // 形变权重：取父母均值 + 突变
            var morphNames = new HashSet<string>();
            foreach (var k in parentA.MorphWeights.Keys) morphNames.Add(k);
            foreach (var k in parentB.MorphWeights.Keys) morphNames.Add(k);
            foreach (var name in morphNames)
            {
                float wa = parentA.MorphWeights.TryGetValue(name, out var va) ? va : 0.0f;
                float wb = parentB.MorphWeights.TryGetValue(name, out var vb) ? vb : 0.0f;
                float avg = (wa + wb) * 0.5f;
                float mutation = ((float)rnd.NextDouble() * 2 - 1) * 0.1f;
                child.MorphWeights[name] = Math.Clamp(avg + mutation, 0.0f, 1.0f);
            }
            child.FacialFeatureMorphs = new Dictionary<string, float>(child.MorphWeights);

            // 外观：肤色/眼睛/毛发混合
            child.Appearance = new CharacterAppearance
            {
                SkinColor = child.SkinTone,
                EyeColor = rnd.NextDouble() < 0.5 ? parentA.Appearance.EyeColor : parentB.Appearance.EyeColor,
                HairColor = rnd.NextDouble() < 0.5 ? parentA.Appearance.HairColor : parentB.Appearance.HairColor,
                HairRootColor = rnd.NextDouble() < 0.5 ? parentA.Appearance.HairRootColor : parentB.Appearance.HairRootColor,
                MakeupEnabled = false
            };

            return child;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 网格合并（与建模系统对接）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 角色网格合并器：把多个部件网格（按各自插槽变换）烘焙合并为单一 EditableMesh。
    /// 用于把角色身体 + 各部件合并为一张可整体蒙皮/绘制网格。
    /// </summary>
    public static class CharacterMeshMerger
    {
        /// <summary>合并多个网格。</summary>
        /// <param name="meshes">待合并网格列表。</param>
        /// <param name="transforms">每个网格的世界变换（顶点先变换再合并）。</param>
        /// <returns>合并后的网格。</returns>
        public static EditableMesh Merge(List<EditableMesh> meshes, List<Matrix4x4> transforms)
        {
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            var result = new EditableMesh { Name = "MergedCharacter" };

            int vertexOffset = 0;
            for (int i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                var xform = i < transforms.Count ? transforms[i] : Matrix4x4.Identity;
                bool hasTransform = xform != Matrix4x4.Identity;

                int baseIndex = result.Vertices.Count;
                // 顶点
                foreach (var v in mesh.Vertices)
                {
                    result.Vertices.Add(hasTransform ? Vector3.Transform(v, xform) : v);
                }
                // 法线
                if (mesh.Normals.Count == mesh.Vertices.Count)
                {
                    foreach (var n in mesh.Normals)
                    {
                        // 法线仅应用变换的旋转+缩放部分（TransformNormal 忽略平移）
                        result.Normals.Add(Vector3.TransformNormal(n, xform));
                    }
                }
                // UV
                if (mesh.UV0.Count == mesh.Vertices.Count)
                    result.UV0.AddRange(mesh.UV0);
                else
                    result.UV0.AddRange(Enumerable.Repeat(Vector2.Zero, mesh.Vertices.Count));

                // 面（索引偏移）
                foreach (var f in mesh.Faces)
                {
                    result.Faces.Add(new MeshFace(f.A + baseIndex, f.B + baseIndex, f.C + baseIndex));
                }

                // 骨骼权重（偏移 BoneIndex 不做，因合并后通常重新蒙皮）
                vertexOffset += mesh.Vertices.Count;
            }

            // 若任一源网格缺法线，则重算
            if (result.Normals.Count != result.Vertices.Count)
            {
                result.Normals.Clear();
                result.RecalculateNormals();
            }
            return result;
        }

        /// <summary>合并一个角色的基础网格与所有已启用部件网格（按插槽变换）。</summary>
        public static EditableMesh MergeCharacter(CharacterConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var meshes = new List<EditableMesh>();
            var transforms = new List<Matrix4x4>();

            // 基础身体
            if (cfg.BaseMesh != null)
            {
                meshes.Add(cfg.BaseMesh);
                transforms.Add(Matrix4x4.Identity);
            }

            // 部件
            Matrix4x4[]? boneWorlds = cfg.Skeleton?.ComputeWorldMatrices(cfg.BindPose);
            foreach (var part in cfg.Parts.Values)
            {
                if (!part.IsEnabled) continue;
                // 这里仅做几何合并示意：实际部件网格需从磁盘加载
                // 读取部件当前 LOD 网格
                var path = part.GetCurrentMeshPath(cfg.CurrentLod);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                try
                {
                    var mesh = EditableMesh.Read(path);
                    Matrix4x4 xform = Matrix4x4.Identity;
                    if (part.Socket != null && cfg.Skeleton != null && boneWorlds != null)
                    {
                        int boneIdx = cfg.Skeleton.FindBone(part.Socket.BoneName);
                        if (boneIdx >= 0) xform = part.Socket.ComputeWorld(boneWorlds[boneIdx]);
                    }
                    meshes.Add(mesh);
                    transforms.Add(xform);
                }
                catch { /* 忽略读取失败的部件 */ }
            }

            return Merge(meshes, transforms);
        }
    }
}
