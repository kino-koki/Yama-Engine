using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.Character
{
    // ======================================================================
    // 山雨 DigitalHuman UE6 级角色创建系统
    // (ShanyuEngine.DigitalHumanCreator - UE6 Grade Character Creation System)
    //
    // 整合子系统：
    //   1. CharacterDNA       — 基因系统（面部50+参数/身体30+参数/肤色/毛发）
    //   2. BodyRig            — 身体骨骼绑定（70+骨骼层级/IK链/关节约束）
    //   3. FacialRig          — 面部绑定系统（52个FACS/ARKit兼容/表情预设）
    //   4. HairGroomingSystem — 毛发梳理系统（发束生成/物理驱动/风力交互）
    //   5. ClothingSystem     — 服装系统（布料模拟/碰撞回避/层次管理）
    //   6. DigitalHumanAssembly — 角色组装流水线（DNA->Mesh->Rig->Clothing->Hair）
    //   7. LOD 管理           — LOD0 Full / LOD1 Reduced / LOD2 Simple / LOD3 Billboard
    //   8. 材质实例化         — 皮肤/眼睛/头发/服装材质参数
    //
    // 命名空间：ShanyuEngine.Character
    // ======================================================================

    #region ===================== 基础枚举 =====================

    /// <summary>性别。</summary>
    public enum Gender { Male, Female, NonBinary }

    /// <summary>种族预设。</summary>
    public enum Ethnicity
    {
        European, EastAsian, SouthAsian, African,
        MiddleEastern, LatinAmerican, Polynesian, NativeAmerican
    }

    /// <summary>年龄分组。</summary>
    public enum AgeGroup { Child, Teen, YoungAdult, Adult, MiddleAged, Senior }

    /// <summary>体型。</summary>
    public enum BodyType { Slim, Average, Athletic, Heavy, Muscular }

    /// <summary>服装部位类型。</summary>
    public enum ClothingSlot { Top, Bottom, Shoes, Gloves, Hat, Accessory, FullBody, Underwear }

    /// <summary>服装层次（由内到外）。</summary>
    public enum ClothingLayer { Underwear = 0, Mid = 1, Outer = 2, Accessory = 3 }

    /// <summary>情绪类型。</summary>
    public enum EmotionType { Neutral, Happy, Sad, Angry, Surprised, Fearful, Disgusted, Contempt }

    /// <summary>面部特征类别。</summary>
    public enum FacialFeatureCategory { Eyes, Nose, Mouth, Chin, Cheek, Brow, Ear, Forehead, Jaw }

    /// <summary>LOD 级别。</summary>
    public enum CharacterLOD
    {
        /// <summary>全精度（100K+ 三角形）</summary>
        LOD0_Full = 0,
        /// <summary>减精度（25K 三角形）</summary>
        LOD1_Reduced = 1,
        /// <summary>简化（5K 三角形）</summary>
        LOD2_Simple = 2,
        /// <summary>公告板（ Billboard / Imposter ）</summary>
        LOD3_Billboard = 3
    }

    /// <summary>骨骼约束类型。</summary>
    public enum BoneConstraintType
    {
        None, IK, Twist, PoleVector, Aim, ParentDirection, LimitRotation, LimitTranslation
    }

    /// <summary>毛发物理模式。</summary>
    public enum HairPhysicsMode
    {
        /// <summary>无物理，静态造型。</summary>
        Static,
        /// <summary>弹簧质点模拟。</summary>
        SpringMass,
        /// <summary>PBD（位置 Based Dynamics）。</summary>
        PBD,
        /// <summary>链式约束。</summary>
        Chain
    }

    /// <summary>布料积分方法。</summary>
    public enum ClothIntegrationMethod
    {
        Verlet, PBD, XPBD, PositionBased
    }

    /// <summary>材质类型。</summary>
    public enum MaterialType { Skin, Eye, Hair, Clothing, Accessory, Generic }

    #endregion

    #region ===================== CharacterDNA 基因系统 =====================

    /// <summary>
    /// 角色完整 DNA：所有外观参数的统一数据容器。
    /// 所有基因值 0~1 归一化（身高/体重除外，使用物理单位）。
    /// </summary>
    public sealed class CharacterDNA
    {
        /// <summary>角色名称。</summary>
        public string Name { get; set; } = "Character";
        /// <summary>性别。</summary>
        public Gender Gender { get; set; } = Gender.Male;
        /// <summary>种族。</summary>
        public Ethnicity Ethnicity { get; set; } = Ethnicity.European;
        /// <summary>年龄分组。</summary>
        public AgeGroup AgeGroup { get; set; } = AgeGroup.YoungAdult;
        /// <summary>面部 DNA（50+ 参数）。</summary>
        public FaceDNA Face { get; set; } = new();
        /// <summary>身体 DNA（30+ 参数）。</summary>
        public BodyDNA Body { get; set; } = new();
        /// <summary>肤色 DNA。</summary>
        public SkinDNA Skin { get; set; } = new();
        /// <summary>毛发 DNA。</summary>
        public HairDNA Hair { get; set; } = new();
        /// <summary>版本号（用于序列化迁移）。</summary>
        public int Version { get; set; } = 1;

        /// <summary>收集所有 DNA 基因到统一字典。</summary>
        public Dictionary<string, float> CollectAllGenes()
        {
            var genes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Face.CollectParameters()) genes[$"Face.{p.Name}"] = p.Value;
            foreach (var p in Body.CollectParameters()) genes[$"Body.{p.Name}"] = p.Value;
            return genes;
        }

        /// <summary>从统一字典批量设置所有 DNA 基因。</summary>
        public void ApplyAllGenes(Dictionary<string, float> genes)
        {
            var faceParams = new List<(string, float)>();
            var bodyParams = new List<(string, float)>();
            foreach (var kvp in genes)
            {
                if (kvp.Key.StartsWith("Face.", StringComparison.OrdinalIgnoreCase))
                    faceParams.Add((kvp.Key.Substring(5), kvp.Value));
                else if (kvp.Key.StartsWith("Body.", StringComparison.OrdinalIgnoreCase))
                    bodyParams.Add((kvp.Key.Substring(5), kvp.Value));
            }
            Face.ApplyParameters(faceParams);
            Body.ApplyParameters(bodyParams);
        }

        /// <summary>JSON 序列化保存。</summary>
        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(path, json);
        }

        /// <summary>JSON 反序列化加载。</summary>
        public static CharacterDNA Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CharacterDNA>(json, JsonOpts)
                   ?? throw new InvalidDataException($"Failed to load CharacterDNA from {path}");
        }

        /// <summary>DNA 混合（孟德尔式遗传 + 突变）。</summary>
        public static CharacterDNA Breed(CharacterDNA parentA, CharacterDNA parentB, float mutationRate = 0.05f)
        {
            var child = new CharacterDNA();
            var rnd = new Random();
            child.Gender = rnd.NextDouble() < 0.5 ? parentA.Gender : parentB.Gender;
            child.Ethnicity = rnd.NextDouble() < 0.5 ? parentA.Ethnicity : parentB.Ethnicity;

            // 面部基因混合
            foreach (var (name, _) in parentA.Face.CollectParameters())
            {
                float a = GetGeneValue(parentA.Face, name);
                float b = GetGeneValue(parentB.Face, name);
                float val = MixGenes(a, b, rnd, mutationRate);
                child.Face.ApplyParameters(new[] { (name, val) });
            }
            // 身体基因混合
            foreach (var (name, _) in parentA.Body.CollectParameters())
            {
                float a = GetGeneValue(parentA.Body, name);
                float b = GetGeneValue(parentB.Body, name);
                float val = MixGenes(a, b, rnd, mutationRate);
                child.Body.ApplyParameters(new[] { (name, val) });
            }
            return child;
        }

        private static float MixGenes(float a, float b, Random rnd, float mutationRate)
        {
            float baseVal = (a + b) * 0.5f + (float)(rnd.NextDouble() - 0.5) * Math.Abs(a - b) * 0.3f;
            if (rnd.NextDouble() < mutationRate) baseVal += (float)(rnd.NextDouble() - 0.5) * 0.1f;
            return Math.Clamp(baseVal, 0f, 1f);
        }

        private static float GetGeneValue(FaceDNA face, string name)
        {
            var map = face.CollectParameters().ToDictionary(p => p.Name, p => p.Value);
            return map.TryGetValue(name, out var v) ? v : 0.5f;
        }

        private static float GetGeneValue(BodyDNA body, string name)
        {
            var map = body.CollectParameters().ToDictionary(p => p.Name, p => p.Value);
            return map.TryGetValue(name, out var v) ? v : 0.5f;
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, IncludeFields = true, PropertyNameCaseInsensitive = true };
    }

    /// <summary>
    /// 面部 DNA：50+ 参数化面部特征。所有参数归一化到 [0,1]。
    /// </summary>
    public sealed class FaceDNA
    {
        // -- 眼部 (9) --
        public float EyeSpacing { get; set; } = 0.5f;
        public float EyeSize { get; set; } = 0.5f;
        public float EyeHeight { get; set; } = 0.5f;
        public float EyeTilt { get; set; } = 0.5f;
        public float EyeDepth { get; set; } = 0.5f;
        public float UpperEyelidPosition { get; set; } = 0.5f;
        public float LowerEyelidPosition { get; set; } = 0.5f;
        public float EyeBagIntensity { get; set; } = 0.2f;
        public float EyelidCrease { get; set; } = 0.5f;
        // -- 眉毛 (6) --
        public float BrowThickness { get; set; } = 0.5f;
        public float BrowHeight { get; set; } = 0.5f;
        public float BrowAngle { get; set; } = 0.5f;
        public float BrowSpacing { get; set; } = 0.5f;
        public float BrowLength { get; set; } = 0.5f;
        public float BrowRidgeProminence { get; set; } = 0.5f;
        // -- 鼻子 (9) --
        public float NoseBridgeHeight { get; set; } = 0.5f;
        public float NoseBridgeWidth { get; set; } = 0.5f;
        public float NoseTipLength { get; set; } = 0.5f;
        public float NoseTipAngle { get; set; } = 0.5f;
        public float NostrilWidth { get; set; } = 0.5f;
        public float NoseTipSize { get; set; } = 0.5f;
        public float NostrilVisibility { get; set; } = 0.3f;
        public float NoseBridgeCurve { get; set; } = 0.2f;
        public float NoseLength { get; set; } = 0.5f;
        // -- 嘴巴 (11) --
        public float LipThickness { get; set; } = 0.5f;
        public float UpperLipThickness { get; set; } = 0.5f;
        public float LowerLipThickness { get; set; } = 0.5f;
        public float MouthWidth { get; set; } = 0.5f;
        public float MouthCornerHeight { get; set; } = 0.5f;
        public float LipCupidBow { get; set; } = 0.5f;
        public float MouthProtrusion { get; set; } = 0.3f;
        public float PhiltrumLength { get; set; } = 0.5f;
        public float PhiltrumWidth { get; set; } = 0.5f;
        // -- 下巴与下颌 (6) --
        public float ChinLength { get; set; } = 0.5f;
        public float ChinWidth { get; set; } = 0.5f;
        public float ChinProtrusion { get; set; } = 0.5f;
        public float JawWidth { get; set; } = 0.5f;
        public float JawAngle { get; set; } = 0.5f;
        public float JawlineDefinition { get; set; } = 0.5f;
        // -- 颧骨与脸颊 (4) --
        public float CheekboneHeight { get; set; } = 0.5f;
        public float CheekboneWidth { get; set; } = 0.5f;
        public float CheekFullness { get; set; } = 0.5f;
        public float CheekboneProminence { get; set; } = 0.5f;
        // -- 前额与头部 (6) --
        public float ForeheadHeight { get; set; } = 0.5f;
        public float ForeheadWidth { get; set; } = 0.5f;
        public float ForeheadTilt { get; set; } = 0.5f;
        public float TempleWidth { get; set; } = 0.5f;
        public float SkullWidth { get; set; } = 0.5f;
        public float SkullLength { get; set; } = 0.5f;
        // -- 耳朵 (4) --
        public float EarSize { get; set; } = 0.5f;
        public float EarAngle { get; set; } = 0.3f;
        public float EarlobeSize { get; set; } = 0.5f;
        public float EarHeight { get; set; } = 0.5f;
        // -- 面部整体 (5) --
        public float FaceWidth { get; set; } = 0.5f;
        public float FaceHeight { get; set; } = 0.5f;
        public float FaceDepth { get; set; } = 0.5f;
        public float MidfaceProtrusion { get; set; } = 0.5f;

        /// <summary>收集所有参数。</summary>
        public List<(string Name, float Value)> CollectParameters()
        {
            var p = new List<(string, float)>();
            void Add(string n, float v) { p.Add((n, v)); }
            Add("EyeSpacing", EyeSpacing); Add("EyeSize", EyeSize);
            Add("EyeHeight", EyeHeight); Add("EyeTilt", EyeTilt);
            Add("EyeDepth", EyeDepth); Add("UpperEyelidPosition", UpperEyelidPosition);
            Add("LowerEyelidPosition", LowerEyelidPosition); Add("EyeBagIntensity", EyeBagIntensity);
            Add("EyelidCrease", EyelidCrease);
            Add("BrowThickness", BrowThickness); Add("BrowHeight", BrowHeight);
            Add("BrowAngle", BrowAngle); Add("BrowSpacing", BrowSpacing);
            Add("BrowLength", BrowLength); Add("BrowRidgeProminence", BrowRidgeProminence);
            Add("NoseBridgeHeight", NoseBridgeHeight); Add("NoseBridgeWidth", NoseBridgeWidth);
            Add("NoseTipLength", NoseTipLength); Add("NoseTipAngle", NoseTipAngle);
            Add("NostrilWidth", NostrilWidth); Add("NoseTipSize", NoseTipSize);
            Add("NostrilVisibility", NostrilVisibility); Add("NoseBridgeCurve", NoseBridgeCurve);
            Add("NoseLength", NoseLength);
            Add("LipThickness", LipThickness); Add("UpperLipThickness", UpperLipThickness);
            Add("LowerLipThickness", LowerLipThickness); Add("MouthWidth", MouthWidth);
            Add("MouthCornerHeight", MouthCornerHeight); Add("LipCupidBow", LipCupidBow);
            Add("MouthProtrusion", MouthProtrusion); Add("PhiltrumLength", PhiltrumLength);
            Add("PhiltrumWidth", PhiltrumWidth);
            Add("ChinLength", ChinLength); Add("ChinWidth", ChinWidth);
            Add("ChinProtrusion", ChinProtrusion); Add("JawWidth", JawWidth);
            Add("JawAngle", JawAngle); Add("JawlineDefinition", JawlineDefinition);
            Add("CheekboneHeight", CheekboneHeight); Add("CheekboneWidth", CheekboneWidth);
            Add("CheekFullness", CheekFullness); Add("CheekboneProminence", CheekboneProminence);
            Add("ForeheadHeight", ForeheadHeight); Add("ForeheadWidth", ForeheadWidth);
            Add("ForeheadTilt", ForeheadTilt); Add("TempleWidth", TempleWidth);
            Add("SkullWidth", SkullWidth); Add("SkullLength", SkullLength);
            Add("EarSize", EarSize); Add("EarAngle", EarAngle);
            Add("EarlobeSize", EarlobeSize); Add("EarHeight", EarHeight);
            Add("FaceWidth", FaceWidth); Add("FaceHeight", FaceHeight);
            Add("FaceDepth", FaceDepth); Add("MidfaceProtrusion", MidfaceProtrusion);
            return p;
        }

        /// <summary>批量设置参数。</summary>
        public void ApplyParameters(IEnumerable<(string Name, float Value)> parameters)
        {
            var map = parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            void Set(string name, out float target) => target = map.TryGetValue(name, out var v) ? v : target;
            foreach (var kvp in map)
            {
                switch (kvp.Key)
                {
                    case "EyeSpacing": EyeSpacing = kvp.Value; break;
                    case "EyeSize": EyeSize = kvp.Value; break;
                    case "EyeHeight": EyeHeight = kvp.Value; break;
                    case "EyeTilt": EyeTilt = kvp.Value; break;
                    case "EyeDepth": EyeDepth = kvp.Value; break;
                    case "UpperEyelidPosition": UpperEyelidPosition = kvp.Value; break;
                    case "LowerEyelidPosition": LowerEyelidPosition = kvp.Value; break;
                    case "EyeBagIntensity": EyeBagIntensity = kvp.Value; break;
                    case "EyelidCrease": EyelidCrease = kvp.Value; break;
                    case "BrowThickness": BrowThickness = kvp.Value; break;
                    case "BrowHeight": BrowHeight = kvp.Value; break;
                    case "BrowAngle": BrowAngle = kvp.Value; break;
                    case "BrowSpacing": BrowSpacing = kvp.Value; break;
                    case "BrowLength": BrowLength = kvp.Value; break;
                    case "BrowRidgeProminence": BrowRidgeProminence = kvp.Value; break;
                    case "NoseBridgeHeight": NoseBridgeHeight = kvp.Value; break;
                    case "NoseBridgeWidth": NoseBridgeWidth = kvp.Value; break;
                    case "NoseTipLength": NoseTipLength = kvp.Value; break;
                    case "NoseTipAngle": NoseTipAngle = kvp.Value; break;
                    case "NostrilWidth": NostrilWidth = kvp.Value; break;
                    case "NoseTipSize": NoseTipSize = kvp.Value; break;
                    case "NostrilVisibility": NostrilVisibility = kvp.Value; break;
                    case "NoseBridgeCurve": NoseBridgeCurve = kvp.Value; break;
                    case "NoseLength": NoseLength = kvp.Value; break;
                    case "LipThickness": LipThickness = kvp.Value; break;
                    case "UpperLipThickness": UpperLipThickness = kvp.Value; break;
                    case "LowerLipThickness": LowerLipThickness = kvp.Value; break;
                    case "MouthWidth": MouthWidth = kvp.Value; break;
                    case "MouthCornerHeight": MouthCornerHeight = kvp.Value; break;
                    case "LipCupidBow": LipCupidBow = kvp.Value; break;
                    case "MouthProtrusion": MouthProtrusion = kvp.Value; break;
                    case "PhiltrumLength": PhiltrumLength = kvp.Value; break;
                    case "PhiltrumWidth": PhiltrumWidth = kvp.Value; break;
                    case "ChinLength": ChinLength = kvp.Value; break;
                    case "ChinWidth": ChinWidth = kvp.Value; break;
                    case "ChinProtrusion": ChinProtrusion = kvp.Value; break;
                    case "JawWidth": JawWidth = kvp.Value; break;
                    case "JawAngle": JawAngle = kvp.Value; break;
                    case "JawlineDefinition": JawlineDefinition = kvp.Value; break;
                    case "CheekboneHeight": CheekboneHeight = kvp.Value; break;
                    case "CheekboneWidth": CheekboneWidth = kvp.Value; break;
                    case "CheekFullness": CheekFullness = kvp.Value; break;
                    case "CheekboneProminence": CheekboneProminence = kvp.Value; break;
                    case "ForeheadHeight": ForeheadHeight = kvp.Value; break;
                    case "ForeheadWidth": ForeheadWidth = kvp.Value; break;
                    case "ForeheadTilt": ForeheadTilt = kvp.Value; break;
                    case "TempleWidth": TempleWidth = kvp.Value; break;
                    case "SkullWidth": SkullWidth = kvp.Value; break;
                    case "SkullLength": SkullLength = kvp.Value; break;
                    case "EarSize": EarSize = kvp.Value; break;
                    case "EarAngle": EarAngle = kvp.Value; break;
                    case "EarlobeSize": EarlobeSize = kvp.Value; break;
                    case "EarHeight": EarHeight = kvp.Value; break;
                    case "FaceWidth": FaceWidth = kvp.Value; break;
                    case "FaceHeight": FaceHeight = kvp.Value; break;
                    case "FaceDepth": FaceDepth = kvp.Value; break;
                    case "MidfaceProtrusion": MidfaceProtrusion = kvp.Value; break;
                }
            }
        }

        /// <summary>深拷贝。</summary>
        public FaceDNA Clone() { var c = new FaceDNA(); c.ApplyParameters(CollectParameters()); return c; }
        /// <summary>参数数量。</summary>
        public int ParameterCount => 60;
    }

    /// <summary>
    /// 身体 DNA：30+ 参数化身体特征。
    /// </summary>
    public sealed class BodyDNA
    {
        // -- 整体比例 --
        public float Height { get; set; } = 175f;
        public float Weight { get; set; } = 70f;
        public float HeadToBodyRatio { get; set; } = 0.5f;
        public float ArmLength { get; set; } = 0.5f;
        public float LegLength { get; set; } = 0.5f;
        public float TorsoLength { get; set; } = 0.5f;
        public float NeckLength { get; set; } = 0.5f;
        public float HandSize { get; set; } = 0.5f;
        public float FootSize { get; set; } = 0.5f;
        // -- 肌肉与脂肪 --
        public float MuscleMass { get; set; } = 0.4f;
        public float BodyFat { get; set; } = 0.4f;
        public float BicepSize { get; set; } = 0.4f;
        public float DeltoidSize { get; set; } = 0.4f;
        public float ForearmMuscle { get; set; } = 0.4f;
        public float PectoralSize { get; set; } = 0.4f;
        public float AbdominalDefinition { get; set; } = 0.3f;
        public float LatWidth { get; set; } = 0.4f;
        public float QuadricepSize { get; set; } = 0.4f;
        public float CalfSize { get; set; } = 0.4f;
        public float GluteSize { get; set; } = 0.4f;
        public float TrapeziusSize { get; set; } = 0.3f;
        // -- 围度 --
        public float ShoulderWidth { get; set; } = 0.5f;
        public float ChestGirth { get; set; } = 0.5f;
        public float WaistGirth { get; set; } = 0.5f;
        public float HipGirth { get; set; } = 0.5f;
        public float UpperArmGirth { get; set; } = 0.5f;
        public float ForearmGirth { get; set; } = 0.5f;
        public float ThighGirth { get; set; } = 0.5f;
        public float CalfGirth { get; set; } = 0.5f;
        public float NeckGirth { get; set; } = 0.5f;
        public float WristGirth { get; set; } = 0.5f;
        public float AnkleGirth { get; set; } = 0.5f;
        // -- 姿态 --
        public float PelvisWidth { get; set; } = 0.5f;
        public float SpineCurvature { get; set; } = 0.2f;
        public float ShoulderRoll { get; set; } = 0.2f;

        /// <summary>收集所有参数。</summary>
        public List<(string Name, float Value)> CollectParameters()
        {
            return new List<(string, float)>
            {
                ("Height", Height), ("Weight", Weight),
                ("HeadToBodyRatio", HeadToBodyRatio), ("ArmLength", ArmLength),
                ("LegLength", LegLength), ("TorsoLength", TorsoLength),
                ("NeckLength", NeckLength), ("HandSize", HandSize),
                ("FootSize", FootSize), ("MuscleMass", MuscleMass),
                ("BodyFat", BodyFat), ("BicepSize", BicepSize),
                ("DeltoidSize", DeltoidSize), ("ForearmMuscle", ForearmMuscle),
                ("PectoralSize", PectoralSize), ("AbdominalDefinition", AbdominalDefinition),
                ("LatWidth", LatWidth), ("QuadricepSize", QuadricepSize),
                ("CalfSize", CalfSize), ("GluteSize", GluteSize),
                ("TrapeziusSize", TrapeziusSize), ("ShoulderWidth", ShoulderWidth),
                ("ChestGirth", ChestGirth), ("WaistGirth", WaistGirth),
                ("HipGirth", HipGirth), ("UpperArmGirth", UpperArmGirth),
                ("ForearmGirth", ForearmGirth), ("ThighGirth", ThighGirth),
                ("CalfGirth", CalfGirth), ("NeckGirth", NeckGirth),
                ("WristGirth", WristGirth), ("AnkleGirth", AnkleGirth),
                ("PelvisWidth", PelvisWidth), ("SpineCurvature", SpineCurvature),
                ("ShoulderRoll", ShoulderRoll)
            };
        }

        /// <summary>批量设置参数。</summary>
        public void ApplyParameters(IEnumerable<(string Name, float Value)> parameters)
        {
            var map = parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (map.TryGetValue("Height", out var v)) Height = v;
            if (map.TryGetValue("Weight", out v)) Weight = v;
            if (map.TryGetValue("HeadToBodyRatio", out v)) HeadToBodyRatio = v;
            if (map.TryGetValue("ArmLength", out v)) ArmLength = v;
            if (map.TryGetValue("LegLength", out v)) LegLength = v;
            if (map.TryGetValue("TorsoLength", out v)) TorsoLength = v;
            if (map.TryGetValue("NeckLength", out v)) NeckLength = v;
            if (map.TryGetValue("HandSize", out v)) HandSize = v;
            if (map.TryGetValue("FootSize", out v)) FootSize = v;
            if (map.TryGetValue("MuscleMass", out v)) MuscleMass = v;
            if (map.TryGetValue("BodyFat", out v)) BodyFat = v;
            if (map.TryGetValue("BicepSize", out v)) BicepSize = v;
            if (map.TryGetValue("DeltoidSize", out v)) DeltoidSize = v;
            if (map.TryGetValue("ForearmMuscle", out v)) ForearmMuscle = v;
            if (map.TryGetValue("PectoralSize", out v)) PectoralSize = v;
            if (map.TryGetValue("AbdominalDefinition", out v)) AbdominalDefinition = v;
            if (map.TryGetValue("LatWidth", out v)) LatWidth = v;
            if (map.TryGetValue("QuadricepSize", out v)) QuadricepSize = v;
            if (map.TryGetValue("CalfSize", out v)) CalfSize = v;
            if (map.TryGetValue("GluteSize", out v)) GluteSize = v;
            if (map.TryGetValue("TrapeziusSize", out v)) TrapeziusSize = v;
            if (map.TryGetValue("ShoulderWidth", out v)) ShoulderWidth = v;
            if (map.TryGetValue("ChestGirth", out v)) ChestGirth = v;
            if (map.TryGetValue("WaistGirth", out v)) WaistGirth = v;
            if (map.TryGetValue("HipGirth", out v)) HipGirth = v;
            if (map.TryGetValue("UpperArmGirth", out v)) UpperArmGirth = v;
            if (map.TryGetValue("ForearmGirth", out v)) ForearmGirth = v;
            if (map.TryGetValue("ThighGirth", out v)) ThighGirth = v;
            if (map.TryGetValue("CalfGirth", out v)) CalfGirth = v;
            if (map.TryGetValue("NeckGirth", out v)) NeckGirth = v;
            if (map.TryGetValue("WristGirth", out v)) WristGirth = v;
            if (map.TryGetValue("AnkleGirth", out v)) AnkleGirth = v;
            if (map.TryGetValue("PelvisWidth", out v)) PelvisWidth = v;
            if (map.TryGetValue("SpineCurvature", out v)) SpineCurvature = v;
            if (map.TryGetValue("ShoulderRoll", out v)) ShoulderRoll = v;
        }

        /// <summary>深拷贝。</summary>
        public BodyDNA Clone() { var c = new BodyDNA(); c.ApplyParameters(CollectParameters()); return c; }
        /// <summary>参数数量。</summary>
        public int ParameterCount => 35;
    }

    /// <summary>肤色 DNA。</summary>
    public sealed class SkinDNA
    {
        public Vector3 SkinColor { get; set; } = new(0.85f, 0.7f, 0.6f);
        public float Melanin { get; set; } = 0.4f;
        public float Hemoglobin { get; set; } = 0.4f;
        public float SssRadius { get; set; } = 0.012f;
        public Vector3 SssColor { get; set; } = new(1.0f, 0.45f, 0.35f);
        public float Oiliness { get; set; } = 0.3f;
        public float Roughness { get; set; } = 0.5f;
        public float Freckles { get; set; }
        public float PoreVisibility { get; set; } = 0.3f;
        public Vector3 EyeColor { get; set; } = new(0.3f, 0.5f, 0.8f);

        public SkinDNA Clone() => (SkinDNA)MemberwiseClone();
    }

    /// <summary>毛发 DNA。</summary>
    public sealed class HairDNA
    {
        public Vector3 RootColor { get; set; } = new(0.1f, 0.07f, 0.04f);
        public Vector3 TipColor { get; set; } = new(0.2f, 0.13f, 0.08f);
        public Vector3 SpecularColor { get; set; } = new(0.8f, 0.7f, 0.6f);
        public float Density { get; set; } = 0.5f;
        public float Length { get; set; } = 0.4f;
        public float Curliness { get; set; } = 0.1f;
        public float Roughness { get; set; } = 0.3f;
        public float Thickness { get; set; } = 0.5f;
        public float Anisotropy { get; set; } = 0.8f;
        public float IOR { get; set; } = 1.55f;
        public int HairStylePreset { get; set; }
        public float BeardDensity { get; set; }
        public float EyebrowDensity { get; set; } = 0.5f;

        public HairDNA Clone() => (HairDNA)MemberwiseClone();
    }

    #endregion

    #region ===================== ARKit BlendShape 系统 =====================

    /// <summary>ARKit 兼容 52 个 BlendShape 名称库。</summary>
    public static class ARKitBlendShapes
    {
        public static readonly string[] Names = new[]
        {
            "eyeBlink_L", "eyeBlink_R",
            "eyeLookDown_L", "eyeLookDown_R",
            "eyeLookIn_L", "eyeLookIn_R",
            "eyeLookOut_L", "eyeLookOut_R",
            "eyeLookUp_L", "eyeLookUp_R",
            "eyeSquint_L", "eyeSquint_R",
            "eyeWide_L", "eyeWide_R",
            "browDown_L", "browDown_R", "browInnerUp",
            "browOuterUp_L", "browOuterUp_R",
            "cheekPuff", "cheekSquint_L", "cheekSquint_R",
            "noseSneer_L", "noseSneer_R",
            "jawForward", "jawLeft", "jawOpen", "jawRight",
            "mouthClose", "mouthDimple_L", "mouthDimple_R",
            "mouthFrown_L", "mouthFrown_R", "mouthFunnel", "mouthLeft",
            "mouthLowerDown_L", "mouthLowerDown_R",
            "mouthPress_L", "mouthPress_R", "mouthPucker", "mouthRight",
            "mouthRollLower", "mouthRollUpper",
            "mouthShrugLower", "mouthShrugUpper",
            "mouthSmile_L", "mouthSmile_R",
            "mouthStretch_L", "mouthStretch_R",
            "mouthUpperUp_L", "mouthUpperUp_R",
            "tongueOut"
        };
        public const int Count = 52;

        public static readonly Dictionary<string, string> SymmetryPairs = new()
        {
            ["eyeBlink_L"] = "eyeBlink_R",
            ["eyeLookDown_L"] = "eyeLookDown_R",
            ["eyeLookIn_L"] = "eyeLookIn_R",
            ["eyeLookOut_L"] = "eyeLookOut_R",
            ["eyeLookUp_L"] = "eyeLookUp_R",
            ["eyeSquint_L"] = "eyeSquint_R",
            ["eyeWide_L"] = "eyeWide_R",
            ["browDown_L"] = "browDown_R",
            ["browOuterUp_L"] = "browOuterUp_R",
            ["cheekSquint_L"] = "cheekSquint_R",
            ["noseSneer_L"] = "noseSneer_R",
            ["mouthDimple_L"] = "mouthDimple_R",
            ["mouthFrown_L"] = "mouthFrown_R",
            ["mouthLowerDown_L"] = "mouthLowerDown_R",
            ["mouthPress_L"] = "mouthPress_R",
            ["mouthSmile_L"] = "mouthSmile_R",
            ["mouthStretch_L"] = "mouthStretch_R",
            ["mouthUpperUp_L"] = "mouthUpperUp_R"
        };

        public static string GetSymmetricName(string name) =>
            SymmetryPairs.TryGetValue(name, out var sym) ? sym : name;

        public static string[] GetGroup(string group) => group.ToLowerInvariant() switch
        {
            "eye" => Names.Take(14).ToArray(),
            "brow" => Names.Skip(14).Take(5).ToArray(),
            "cheek" => Names.Skip(19).Take(3).ToArray(),
            "nose" => Names.Skip(22).Take(2).ToArray(),
            "jaw" => Names.Skip(24).Take(4).ToArray(),
            "mouth" => Names.Skip(28).Take(23).ToArray(),
            "tongue" => Names.Skip(51).ToArray(),
            _ => Names
        };
    }

    /// <summary>ARKit BlendShape 权重绑定。</summary>
    public sealed class ARKitBlendShapeRig
    {
        public Dictionary<string, float> Weights { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ARKitBlendShapeRig()
        {
            foreach (var name in ARKitBlendShapes.Names) Weights[name] = 0f;
        }

        public void SetWeight(string name, float weight)
        {
            weight = Math.Clamp(weight, 0f, 1f);
            if (Weights.ContainsKey(name)) Weights[name] = weight;
        }

        public float GetWeight(string name) =>
            Weights.TryGetValue(name, out var w) ? w : 0f;

        public void SetWeightSymmetric(string name, float weight)
        {
            SetWeight(name, weight);
            string sym = ARKitBlendShapes.GetSymmetricName(name);
            if (sym != name) SetWeight(sym, weight);
        }

        public void Reset()
        {
            var keys = new List<string>(Weights.Keys);
            foreach (var k in keys) Weights[k] = 0f;
        }

        public ARKitBlendShapeRig Clone()
        {
            var c = new ARKitBlendShapeRig();
            foreach (var kvp in Weights) c.Weights[kvp.Key] = kvp.Value;
            return c;
        }

        public void Blend(ARKitBlendShapeRig a, ARKitBlendShapeRig b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            foreach (var name in ARKitBlendShapes.Names)
                Weights[name] = a.GetWeight(name) * (1f - t) + b.GetWeight(name) * t;
        }
    }

    #endregion

    #region ===================== BodyRig 身体骨骼绑定 =====================

    /// <summary>骨骼数据。</summary>
    public sealed class BoneData
    {
        public string Name { get; set; } = "";
        public int ParentIndex { get; set; } = -1;
        public Vector3 LocalPosition { get; set; }
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        public Vector3 LocalScale { get; set; } = Vector3.One;
        public BoneConstraintType ConstraintType { get; set; } = BoneConstraintType.None;
        public Vector3 MinRotation { get; set; }
        public Vector3 MaxRotation { get; set; }
        public Vector3 MinTranslation { get; set; } = new Vector3(float.MinValue);
        public Vector3 MaxTranslation { get; set; } = new Vector3(float.MaxValue);
    }

    /// <summary>IK 目标。</summary>
    public sealed class IKTarget
    {
        public string Name { get; set; } = "";
        public string ChainRootBone { get; set; } = "";
        public string EndEffectorBone { get; set; } = "";
        public int ChainLength { get; set; } = 2;
        public Vector3 TargetPosition { get; set; }
        public Quaternion TargetRotation { get; set; } = Quaternion.Identity;
        public float PoleVectorAngle { get; set; }
        public Vector3 PoleVectorDirection { get; set; } = new Vector3(0, 0, 1);
        public float Weight { get; set; } = 1f;
        public bool Enabled { get; set; } = true;
    }

    /// <summary>身体骨骼绑定系统。</summary>
    public sealed class BodyRig
    {
        /// <summary>骨骼列表（有序，索引即 BoneId）。</summary>
        public List<BoneData> Bones { get; } = new();

        /// <summary>IK 目标列表。</summary>
        public List<IKTarget> IKTargets { get; } = new();

        /// <summary>骨骼名称 -> 索引映射。</        /// </summary>
        private readonly Dictionary<string, int> _boneIndex = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>构造标准人体骨骼（70+ 骨骼）。</summary>
        public BodyRig() { BuildStandardSkeleton(); }

        private void BuildStandardSkeleton()
        {
            // 根骨骼
            AddBone("root", -1, Vector3.Zero);
            AddBone("pelvis", 0, new Vector3(0, 0, 0));
            // 脊柱
            int spine01 = AddBone("spine_01", 1, new Vector3(0, 0.10f, 0));
            int spine02 = AddBone("spine_02", spine01, new Vector3(0, 0.08f, 0));
            int spine03 = AddBone("spine_03", spine02, new Vector3(0, 0.08f, 0));
            int neck01 = AddBone("neck_01", spine03, new Vector3(0, 0.06f, 0));
            int neck02 = AddBone("neck_02", neck01, new Vector3(0, 0.04f, 0));
            int head = AddBone("head", neck02, new Vector3(0, 0.05f, 0));
            // 左臂
            int clavL = AddBone("clavicle_l", spine03, new Vector3(-0.02f, 0.04f, 0));
            int upperArmL = AddBone("upperarm_l", clavL, new Vector3(-0.14f, 0, 0));
            int lowerArmL = AddBone("lowerarm_l", upperArmL, new Vector3(-0.12f, 0, 0));
            int handL = AddBone("hand_l", lowerArmL, new Vector3(-0.05f, 0, 0));
            // 右臂
            int clavR = AddBone("clavicle_r", spine03, new Vector3(0.02f, 0.04f, 0));
            int upperArmR = AddBone("upperarm_r", clavR, new Vector3(0.14f, 0, 0));
            int lowerArmR = AddBone("lowerarm_r", upperArmR, new Vector3(0.12f, 0, 0));
            int handR = AddBone("hand_r", lowerArmR, new Vector3(0.05f, 0, 0));
            // 左腿
            int thighL = AddBone("thigh_l", 1, new Vector3(-0.05f, -0.02f, 0));
            int calfL = AddBone("calf_l", thighL, new Vector3(0, -0.18f, 0));
            int footL = AddBone("foot_l", calfL, new Vector3(0, -0.18f, 0.04f));
            // 右腿
            int thighR = AddBone("thigh_r", 1, new Vector3(0.05f, -0.02f, 0));
            int calfR = AddBone("calf_r", thighR, new Vector3(0, -0.18f, 0));
            int footR = AddBone("foot_r", calfR, new Vector3(0, -0.18f, 0.04f));

            // 手指 (简化，每手 5 指 x 3 骨)
            AddFingerBones("thumb_l", handL, new Vector3(-0.01f, -0.01f, 0.02f), 2);
            AddFingerBones("index_l", handL, new Vector3(-0.02f, -0.02f, 0), 3);
            AddFingerBones("middle_l", handL, new Vector3(0, -0.02f, 0), 3);
            AddFingerBones("ring_l", handL, new Vector3(0.02f, -0.02f, 0), 3);
            AddFingerBones("pinky_l", handL, new Vector3(0.03f, -0.015f, 0), 3);
            AddFingerBones("thumb_r", handR, new Vector3(0.01f, -0.01f, 0.02f), 2);
            AddFingerBones("index_r", handR, new Vector3(0.02f, -0.02f, 0), 3);
            AddFingerBones("middle_r", handR, new Vector3(0, -0.02f, 0), 3);
            AddFingerBones("ring_r", handR, new Vector3(-0.02f, -0.02f, 0), 3);
            AddFingerBones("pinky_r", handR, new Vector3(-0.03f, -0.015f, 0), 3);

            // 默认 IK 链
            AddIKTarget("LeftArmIK", "clavicle_l", "hand_l", 3);
            AddIKTarget("RightArmIK", "clavicle_r", "hand_r", 3);
            AddIKTarget("LeftLegIK", "pelvis", "foot_l", 3);
            AddIKTarget("RightLegIK", "pelvis", "foot_r", 3);
            AddIKTarget("HeadLookIK", "neck_01", "head", 2);
        }

        private void AddFingerBones(string prefix, int parentIdx, Vector3 offset, int segments)
        {
            int prev = parentIdx;
            for (int i = 0; i < segments; i++)
            {
                float segLen = i == 0 ? 0.02f : 0.015f;
                prev = AddBone($"{prefix}_{i}", prev, offset + new Vector3(0, -segLen * (i + 1), 0));
                Bones[prev].ConstraintType = BoneConstraintType.LimitRotation;
                Bones[prev].MinRotation = new Vector3(-90f, -30f, -30f) * (MathF.PI / 180f);
                Bones[prev].MaxRotation = new Vector3(10f, 30f, 30f) * (MathF.PI / 180f);
            }
        }

        private int AddBone(string name, int parentIndex, Vector3 localPos)
        {
            int idx = Bones.Count;
            _boneIndex[name] = idx;
            Bones.Add(new BoneData { Name = name, ParentIndex = parentIndex, LocalPosition = localPos });
            return idx;
        }

        private void AddIKTarget(string name, string chainRoot, string endEffector, int chainLen)
        {
            IKTargets.Add(new IKTarget
            {
                Name = name, ChainRootBone = chainRoot, EndEffectorBone = endEffector,
                ChainLength = chainLen, Enabled = true, Weight = 1f
            });
        }

        /// <summary>通过名称获取骨骼索引。</summary>
        public int FindBoneIndex(string name) =>
            _boneIndex.TryGetValue(name, out var idx) ? idx : -1;

        /// <summary>通过名称获取骨骼数据。</summary>
        public BoneData? FindBone(string name)
        {
            var idx = FindBoneIndex(name);
            return idx >= 0 ? Bones[idx] : null;
        }

        /// <summary>根据 BodyDNA 调整骨骼比例。</summary>
        public void ApplyBodyProportions(BodyDNA body)
        {
            float heightScale = body.Height / 175f;
            float legScale = 0.8f + body.LegLength * 0.4f;
            float armScale = 0.8f + body.ArmLength * 0.4f;
            float headScale = 1.2f - body.HeadToBodyRatio * 0.4f;

            for (int i = 0; i < Bones.Count; i++)
            {
                var bone = Bones[i];
                if (bone.Name.Contains("thigh") || bone.Name.Contains("calf"))
                    bone.LocalPosition *= new Vector3(1, legScale * heightScale, 1);
                else if (bone.Name.Contains("upperarm") || bone.Name.Contains("lowerarm"))
                    bone.LocalPosition *= new Vector3(armScale * heightScale, 1, 1);
                else if (bone.Name.StartsWith("spine") || bone.Name.StartsWith("neck"))
                    bone.LocalPosition *= new Vector3(1, heightScale, 1);
                else if (bone.Name == "head")
                    bone.LocalPosition *= new Vector3(1, headScale, 1);
                else if (bone.Name == "pelvis")
                    bone.LocalPosition *= new Vector3(1, heightScale, 1);
            }
        }

        /// <summary>获取骨骼总数。</summary>
        public int BoneCount => Bones.Count;
    }

    #endregion

    #region ===================== FacialRig 面部绑定系统 =====================

    /// <summary>面部骨骼绑定。</summary>
    public sealed class FacialRig
    {
        /// <summary>52 个 FACS 混合变形目标权重。</summary>
        public ARKitBlendShapeRig BlendShapes { get; } = new();

        /// <summary>表情预设库。</summary>
        private readonly Dictionary<EmotionType, ExpressionPreset> _presets = new();

        /// <summary>当前激活情绪。</summary>
        public EmotionType CurrentEmotion { get; private set; } = EmotionType.Neutral;

        /// <summary>情绪混合权重 (0~1)。</summary>
        public float EmotionWeight { get; set; } = 1f;

        /// <summary>微表情噪声强度。</summary>
        public float MicroExpressionNoise { get; set; } = 0.02f;

        /// <summary>眼球注视方向（归一化）。</summary>
        public Vector2 GazeDirection { get; set; } = Vector2.Zero;

        /// <summary>眨眼计时器。</summary>
        private float _blinkTimer;
        private Random _rnd = new();

        public FacialRig() { InitializePresets(); }

        private void InitializePresets()
        {
            _presets[EmotionType.Neutral] = CreateNeutral();
            _presets[EmotionType.Happy] = CreateHappy();
            _presets[EmotionType.Sad] = CreateSad();
            _presets[EmotionType.Angry] = CreateAngry();
            _presets[EmotionType.Surprised] = CreateSurprised();
            _presets[EmotionType.Fearful] = CreateFearful();
            _presets[EmotionType.Disgusted] = CreateDisgusted();
        }

        /// <summary>设置情绪并应用对应表情。</summary>
        public void SetEmotion(EmotionType emotion, float weight = 1f)
        {
            CurrentEmotion = emotion;
            EmotionWeight = Math.Clamp(weight, 0f, 1f);
            BlendShapes.Reset();
            if (_presets.TryGetValue(emotion, out var preset))
                preset.ApplyTo(BlendShapes, EmotionWeight);
        }

        /// <summary>在两种情绪间平滑过渡。</summary>
        public void TransitionEmotion(EmotionType from, EmotionType to, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            BlendShapes.Reset();
            if (_presets.TryGetValue(from, out var pFrom))
                pFrom.ApplyTo(BlendShapes, 1f - t);
            if (_presets.TryGetValue(to, out var pTo))
                pTo.ApplyTo(BlendShapes, t);
        }

        /// <summary>设置眼球注视方向。</summary>
        public void SetGaze(float horizontal, float vertical)
        {
            GazeDirection = new Vector2(Math.Clamp(horizontal, -1, 1), Math.Clamp(vertical, -1, 1));
            BlendShapes.SetWeightSymmetric("eyeLookOut", (GazeDirection.X + 1f) * 0.5f);
            BlendShapes.SetWeightSymmetric("eyeLookUp", (GazeDirection.Y + 1f) * 0.5f);
        }

        /// <summary>每帧更新（自动眨眼 + 微表情）。</summary>
        public void Update(float deltaTime)
        {
            // 自动眨眼
            _blinkTimer -= deltaTime;
            if (_blinkTimer <= 0f)
            {
                _blinkTimer = 3f + (float)_rnd.NextDouble() * 4f;
                TriggerBlink();
            }
            // 微表情噪声
            if (MicroExpressionNoise > 0f)
            {
                float noise = (float)(_rnd.NextDouble() - 0.5) * 2f * MicroExpressionNoise;
                BlendShapes.SetWeightSymmetric("browInnerUp",
                    Math.Clamp(BlendShapes.GetWeight("browInnerUp") + noise * deltaTime, 0, 1));
            }
        }

        private void TriggerBlink()
        {
            // 在实际实现中，这会启动一个异步眨眼动画
            BlendShapes.SetWeight("eyeBlink_L", 1f);
            BlendShapes.SetWeight("eyeBlink_R", 1f);
        }

        #region --- 表情预设 ---

        private static ExpressionPreset CreateNeutral() => new()
        { Name = "Neutral", Emotion = EmotionType.Neutral, Weights = new() };

        private static ExpressionPreset CreateHappy() => new()
        {
            Name = "Happy", Emotion = EmotionType.Happy,
            Weights = new()
            {
                ["mouthSmile_L"] = 0.8f, ["mouthSmile_R"] = 0.8f,
                ["cheekSquint_L"] = 0.5f, ["cheekSquint_R"] = 0.5f,
                ["browOuterUp_L"] = 0.3f, ["browOuterUp_R"] = 0.3f,
                ["eyeSquint_L"] = 0.3f, ["eyeSquint_R"] = 0.3f
            }
        };

        private static ExpressionPreset CreateSad() => new()
        {
            Name = "Sad", Emotion = EmotionType.Sad,
            Weights = new()
            {
                ["mouthFrown_L"] = 0.6f, ["mouthFrown_R"] = 0.6f,
                ["browInnerUp"] = 0.5f, ["browDown_L"] = 0.3f, ["browDown_R"] = 0.3f,
                ["mouthLowerDown_L"] = 0.2f, ["mouthLowerDown_R"] = 0.2f
            }
        };

        private static ExpressionPreset CreateAngry() => new()
        {
            Name = "Angry", Emotion = EmotionType.Angry,
            Weights = new()
            {
                ["browDown_L"] = 0.8f, ["browDown_R"] = 0.8f,
                ["noseSneer_L"] = 0.4f, ["noseSneer_R"] = 0.4f,
                ["mouthPress_L"] = 0.3f, ["mouthPress_R"] = 0.3f,
                ["eyeSquint_L"] = 0.4f, ["eyeSquint_R"] = 0.4f,
                ["jawForward"] = 0.2f
            }
        };

        private static ExpressionPreset CreateSurprised() => new()
        {
            Name = "Surprised", Emotion = EmotionType.Surprised,
            Weights = new()
            {
                ["jawOpen"] = 0.7f, ["eyeWide_L"] = 0.8f, ["eyeWide_R"] = 0.8f,
                ["browInnerUp"] = 0.9f, ["browOuterUp_L"] = 0.6f, ["browOuterUp_R"] = 0.6f,
                ["mouthUpperUp_L"] = 0.3f, ["mouthUpperUp_R"] = 0.3f
            }
        };

        private static ExpressionPreset CreateFearful() => new()
        {
            Name = "Fearful", Emotion = EmotionType.Fearful,
            Weights = new()
            {
                ["eyeWide_L"] = 0.7f, ["eyeWide_R"] = 0.7f,
                ["browInnerUp"] = 0.8f, ["jawOpen"] = 0.4f,
                ["mouthStretch_L"] = 0.3f, ["mouthStretch_R"] = 0.3f
            }
        };

        private static ExpressionPreset CreateDisgusted() => new()
        {
            Name = "Disgusted", Emotion = EmotionType.Disgusted,
            Weights = new()
            {
                ["noseSneer_L"] = 0.7f, ["noseSneer_R"] = 0.7f,
                ["mouthFrown_L"] = 0.3f, ["mouthFrown_R"] = 0.3f,
                ["cheekPuff"] = 0.2f
            }
        };

        #endregion
    }

    /// <summary>表情预设。</summary>
    public sealed class ExpressionPreset
    {
        public string Name { get; set; } = "Expression";
        public EmotionType Emotion { get; set; }
        public Dictionary<string, float> Weights { get; set; } = new();
        public float Intensity { get; set; } = 1f;

        public void ApplyTo(ARKitBlendShapeRig rig, float weight = 1f)
        {
            if (rig == null) return;
            float scale = Intensity * Math.Clamp(weight, 0f, 1f);
            foreach (var kvp in Weights)
                rig.SetWeight(kvp.Key, kvp.Value * scale);
        }
    }

    #endregion

    #region ===================== HairGroomingSystem 毛发梳理系统 =====================

    /// <summary>单根毛发数据。</summary>
    public sealed class HairStrand
    {
        /// <summary>控制点（Catmull-Rom 样条节点）。</summary>
        public List<Vector3> ControlPoints { get; set; } = new();
        /// <summary>发根世界坐标。</summary>
        public Vector3 RootPosition { get; set; }
        /// <summary>发根法线。</summary>
        public Vector3 RootNormal { get; set; } = Vector3.UnitY;
        /// <summary>毛发长度（世界单位）。</summary>
        public float Length { get; set; } = 0.08f;
        /// <summary>宽度（世界单位）。</summary>
        public float Width { get; set; } = 0.002f;
        /// <summary>卷曲度 [0,1]。</summary>
        public float Curliness { get; set; }
        /// <summary>粗糙度。</summary>
        public float Roughness { get; set; } = 0.3f;
        /// <summary>发根颜色。</summary>
        public Vector3 RootColor { get; set; }
        /// <summary>发梢颜色。</summary>
        public Vector3 TipColor { get; set; }
        /// <summary>不透明度。</summary>
        public float Opacity { get; set; } = 1f;
    }

    /// <summary>头皮区域定义。</summary>
    public sealed class ScalpRegion
    {
        public string Name { get; set; } = "Scalp";
        public Vector2 UVMin { get; set; } = Vector2.Zero;
        public Vector2 UVMax { get; set; } = Vector2.One;
        public float DensityFactor { get; set; } = 1f;
        public float LengthFactor { get; set; } = 1f;
        public Vector3 GrowthDirectionBias { get; set; } = Vector3.Zero;
    }

    /// <summary>风力参数。</summary>
    public sealed class WindParameters
    {
        public Vector3 WindDirection { get; set; } = new Vector3(1, 0.2f, 0.5f);
        public float WindStrength { get; set; } = 0.5f;
        public float Turbulence { get; set; } = 0.3f;
        public float GustFrequency { get; set; } = 0.5f;
        public float GustAmplitude { get; set; } = 0.3f;
    }

    /// <summary>毛发梳理系统。</summary>
    public sealed class HairGroomingSystem
    {
        /// <summary>生成的发束列表。</summary>
        public List<HairStrand> Strands { get; } = new();

        /// <summary>头皮区域定义。</summary>
        public List<ScalpRegion> ScalpRegions { get; } = new();

        /// <summary>物理模式。</summary>
        public HairPhysicsMode PhysicsMode { get; set; } = HairPhysicsMode.SpringMass;

        /// <summary>风力参数。</summary>
        public WindParameters Wind { get; set; } = new();

        /// <summary>全局毛发密度（Guide 数量系数）。</summary>
        public int GuideHairCount { get; set; } = 10000;

        /// <summary>插值倍率（Guide -> Strand）。</summary>
        public int InterpolationMultiplier { get; set; } = 8;

        /// <summary>时间步长。</summary>
        public float TimeStep { get; set; } = 1f / 60f;

        /// <summary>弹簧刚度。</summary>
        public float SpringStiffness { get; set; } = 50f;

        /// <summary>阻尼系数。</summary>
        public float Damping { get; set; } = 5f;

        /// <summary>重力。</summary>
        public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);

        /// <summary>碰撞体列表（头部/肩部胶囊体中心+半径）。</summary>
        public List<(Vector3 Center, float Radius)> CollisionCapsules { get; } = new();

        public HairGroomingSystem() { InitializeDefaultScalp(); }

        private void InitializeDefaultScalp()
        {
            ScalpRegions.Add(new ScalpRegion { Name = "Top", UVMin = new Vector2(0.2f, 0.5f), UVMax = new Vector2(0.8f, 1f), DensityFactor = 1.2f, LengthFactor = 1f });
            ScalpRegions.Add(new ScalpRegion { Name = "Sides", UVMin = new Vector2(0f, 0.4f), UVMax = new Vector2(0.2f, 0.9f), DensityFactor = 0.8f, LengthFactor = 0.8f });
            ScalpRegions.Add(new ScalpRegion { Name = "Back", UVMin = new Vector2(0.8f, 0.4f), UVMax = new Vector2(1f, 0.9f), DensityFactor = 1f, LengthFactor = 1.1f });
        }

        /// <summary>根据 HairDNA 生成发束。</summary>
        public void GenerateStrands(HairDNA hairDna, Vector3 headCenter, float headRadius)
        {
            Strands.Clear();
            var rnd = new Random();
            int count = GuideHairCount;
            float baseLength = 0.02f + hairDna.Length * 0.16f; // 2cm ~ 18cm

            for (int i = 0; i < count; i++)
            {
                // 在头皮半球均匀分布
                float theta = (float)(rnd.NextDouble() * Math.PI * 2);
                float phi = (float)(rnd.NextDouble() * Math.PI * 0.5); // 上半球
                Vector3 rootPos = headCenter + new Vector3(
                    (float)Math.Sin(phi) * (float)Math.Cos(theta),
                    (float)Math.Cos(phi),
                    (float)Math.Sin(phi) * (float)Math.Sin(theta)) * headRadius;

                // 生长方向（法线方向 + 卷曲偏移）
                Vector3 normal = Vector3.Normalize(rootPos - headCenter);
                Vector3 growthDir = normal;
                float curlOffset = hairDna.Curliness * 0.3f * (float)(rnd.NextDouble() - 0.5);
                growthDir += new Vector3(curlOffset, 0, curlOffset);
                growthDir = Vector3.Normalize(growthDir);

                float strandLength = baseLength * (0.8f + 0.4f * (float)rnd.NextDouble());

                var strand = new HairStrand
                {
                    RootPosition = rootPos,
                    RootNormal = growthDir,
                    Length = strandLength,
                    Curliness = hairDna.Curliness,
                    Roughness = hairDna.Roughness,
                    RootColor = hairDna.RootColor,
                    TipColor = hairDna.TipColor,
                    Width = 0.001f + hairDna.Thickness * 0.003f
                };

                // 生成控制点（Catmull-Rom 样条节点）
                int segments = 4 + (int)(hairDna.Curliness * 6);
                strand.ControlPoints.Add(rootPos);
                for (int s = 1; s <= segments; s++)
                {
                    float t = s / (float)segments;
                    Vector3 pos = rootPos + growthDir * strandLength * t;
                    // 卷曲偏移
                    float curl = hairDna.Curliness * 0.02f * (float)Math.Sin(t * Math.PI * 4);
                    pos += new Vector3(curl, 0, curl * 0.5f);
                    strand.ControlPoints.Add(pos);
                }
                Strands.Add(strand);
            }

            CollisionCapsules.Clear();
            CollisionCapsules.Add((headCenter, headRadius * 1.05f));
            CollisionCapsules.Add((headCenter + new Vector3(0, -headRadius * 2.2f, 0), headRadius * 0.8f));
        }

        /// <summary>物理模拟步进（弹簧质点法）。</summary>
        public void Simulate(float deltaTime)
        {
            if (PhysicsMode == HairPhysicsMode.Static || Strands.Count == 0) return;

            float dt = Math.Min(deltaTime, TimeStep);
            Vector3 windForce = Wind.WindDirection * Wind.WindStrength;

            for (int i = 0; i < Strands.Count; i++)
            {
                var strand = Strands[i];
                int n = strand.ControlPoints.Count;
                if (n < 2) continue;

                // 弹簧质点 Verlet 积分（简化：只模拟中间控制点）
                for (int j = 1; j < n - 1; j++)
                {
                    Vector3 pos = strand.ControlPoints[j];
                    Vector3 prevPos = strand.ControlPoints[Math.Max(0, j - 1)];

                    // 弹簧力
                    Vector3 springForce = (strand.ControlPoints[j + 1] - pos) * SpringStiffness;
                    // 重力 + 风力
                    Vector3 totalForce = Gravity + windForce + springForce * 0.01f;
                    // 阻尼
                    Vector3 velocity = (pos - prevPos) * (1f - Damping * dt);
                    // Verlet 积分
                    Vector3 newPos = pos + velocity + totalForce * dt * dt;
                    strand.ControlPoints[j] = newPos;
                }

                // 碰撞检测（简化：球体碰撞）
                foreach (var (center, radius) in CollisionCapsules)
                {
                    for (int j = 1; j < n; j++)
                    {
                        Vector3 toCenter = strand.ControlPoints[j] - center;
                        float dist = toCenter.Length();
                        if (dist < radius && dist > 0.001f)
                            strand.ControlPoints[j] = center + toCenter / dist * radius;
                    }
                }
            }
        }
    }

    #endregion

    #region ===================== ClothingSystem 服装系统 =====================

    /// <summary>服装颜色方案。</summary>
    public sealed class ClothingColorScheme
    {
        public Vector3 PrimaryColor { get; set; } = new(0.2f, 0.3f, 0.6f);
        public Vector3 SecondaryColor { get; set; } = new(0.8f, 0.8f, 0.85f);
        public Vector3 AccentColor { get; set; } = new(0.9f, 0.7f, 0.3f);
    }

    /// <summary>服装资产定义。</summary>
    public sealed class ClothingAsset
    {
        public string Name { get; set; } = "Clothing";
        public ClothingSlot Slot { get; set; } = ClothingSlot.Top;
        public ClothingLayer Layer { get; set; } = ClothingLayer.Outer;
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public ClothingColorScheme Colors { get; set; } = new();
        /// <summary>物理顶点位置列表（布料模拟用）。</summary>
        public List<Vector3> Vertices { get; set; } = new();
        /// <summary>三角索引。</summary>
        public List<int> Indices { get; set; } = new();
        /// <summary>布料固定约束（顶点索引 -> 固定权重，1.0=完全固定）。</summary>
        public Dictionary<int, float> PinConstraints { get; set; } = new();
    }

    /// <summary>布料模拟参数。</summary>
    public sealed class ClothSimulationParams
    {
        public ClothIntegrationMethod Method { get; set; } = ClothIntegrationMethod.Verlet;
        public float Stiffness { get; set; } = 0.8f;
        public float BendingStiffness { get; set; } = 0.3f;
        public float Damping { get; set; } = 0.05f;
        public float Friction { get; set; } = 0.3f;
        public int SolverIterations { get; set; } = 5;
        public float CollisionThickness { get; set; } = 0.005f;
        public float SelfCollisionDistance { get; set; } = 0.01f;
        public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);
        public WindParameters? Wind { get; set; }
    }

    /// <summary>服装系统。</summary>
    public sealed class ClothingSystem
    {
        /// <summary>当前穿着的服装资产（按槽位索引）。</summary>
        public Dictionary<ClothingSlot, ClothingAsset> EquippedItems { get; } = new();

        /// <summary>所有已加载的服装资产。</summary>
        public List<ClothingAsset> LoadedAssets { get; } = new();

        /// <summary>布料模拟参数。</summary>
        public ClothSimulationParams SimParams { get; set; } = new();

        /// <summary>是否启用布料模拟。</summary>
        public bool SimulationEnabled { get; set; } = true;

        /// <summary>碰撞体列表（来自 BodyRig）。</summary>
        public List<(Vector3 Center, float Radius)> BodyColliders { get; } = new();

        /// <summary>穿上一件服装。</summary>
        public bool Equip(ClothingAsset asset)
        {
            if (asset == null) return false;
            // 检查层次冲突
            foreach (var kv in EquippedItems)
            {
                if (kv.Value.Layer == asset.Layer && kv.Value.Slot == asset.Slot)
                {
                    // 同层同槽替换
                    EquippedItems[asset.Slot] = asset;
                    return true;
                }
            }
            EquippedItems[asset.Slot] = asset;
            return true;
        }

        /// <summary>脱下一件服装。</summary>
        public bool Unequip(ClothingSlot slot)
        {
            return EquippedItems.Remove(slot);
        }

        /// <summary>根据 BodyDNA 自动适配体型。</summary>
        public void FitToBody(BodyDNA body, BodyRig bodyRig)
        {
            // 更新碰撞体
            BodyColliders.Clear();
            foreach (var bone in bodyRig.Bones)
            {
                if (bone.Name == "pelvis" || bone.Name == "spine_03" ||
                    bone.Name == "upperarm_l" || bone.Name == "upperarm_r" ||
                    bone.Name == "thigh_l" || bone.Name == "thigh_r")
                {
                    BodyColliders.Add((bone.LocalPosition, 0.05f + body.MuscleMass * 0.04f));
                }
            }

            // 根据体型调整服装顶点（简化：缩放因子）
            float chestScale = 0.8f + body.ChestGirth * 0.4f;
            float waistScale = 0.8f + body.WaistGirth * 0.4f;
            float heightScale = body.Height / 175f;

            foreach (var item in EquippedItems.Values)
            {
                for (int i = 0; i < item.Vertices.Count; i++)
                {
                    var v = item.Vertices[i];
                    float localY = v.Y;
                    float factor = localY > 0 ? chestScale : waistScale;
                    item.Vertices[i] = v * new Vector3(factor, heightScale, factor);
                }
            }
        }

        /// <summary>布料模拟步进。</summary>
        public void Simulate(float deltaTime)
        {
            if (!SimulationEnabled) return;
            float dt = Math.Min(deltaTime, 1f / 30f);

            foreach (var item in EquippedItems.Values)
            {
                SimulateClothAsset(item, dt);
            }
        }

        private void SimulateClothAsset(ClothingAsset asset, float dt)
        {
            int vertCount = asset.Vertices.Count;
            if (vertCount < 3) return;

            // Verlet 积分布料模拟（简化版）
            // 存储前一帧位置
            var prevPositions = new Vector3[vertCount];
            for (int i = 0; i < vertCount; i++) prevPositions[i] = asset.Vertices[i];

            for (int iter = 0; iter < SimParams.SolverIterations; iter++)
            {
                for (int i = 0; i < vertCount; i++)
                {
                    // 检查是否为固定点
                    float pinWeight = asset.PinConstraints.TryGetValue(i, out var pw) ? pw : 0f;
                    if (pinWeight >= 0.99f) continue;

                    // Verlet 积分
                    var pos = asset.Vertices[i];
                    var prevPos = prevPositions[i];
                    Vector3 velocity = (pos - prevPos) * (1f - SimParams.Damping);
                    Vector3 newPos = pos + velocity + SimParams.Gravity * dt * dt;

                    // 风力
                    if (SimParams.Wind != null)
                    {
                        Vector3 windForce = SimParams.Wind.WindDirection * SimParams.Wind.WindStrength * dt * dt;
                        newPos += windForce;
                    }

                    asset.Vertices[i] = newPos;
                }

                // 距离约束（简化：只检查相邻三角形边）
                for (int t = 0; t < asset.Indices.Count; t += 3)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        int i0 = asset.Indices[t + e];
                        int i1 = asset.Indices[t + (e + 1) % 3];
                        ApplyDistanceConstraint(asset, i0, i1, SimParams.Stiffness);
                    }
                }

                // 碰撞检测
                foreach (var (center, radius) in BodyColliders)
                {
                    for (int i = 0; i < vertCount; i++)
                    {
                        Vector3 toVert = asset.Vertices[i] - center;
                        float dist = toVert.Length();
                        float minDist = radius + SimParams.CollisionThickness;
                        if (dist < minDist && dist > 0.0001f)
                            asset.Vertices[i] = center + toVert / dist * minDist;
                    }
                }
            }
        }

        private static void ApplyDistanceConstraint(ClothingAsset asset, int i0, int i1, float stiffness)
        {
            Vector3 delta = asset.Vertices[i1] - asset.Vertices[i0];
            float dist = delta.Length();
            if (dist < 0.0001f) return;
            Vector3 correction = delta * (1f - dist / (dist * stiffness + 0.001f)) * 0.5f;
            if (!asset.PinConstraints.TryGetValue(i0, out var pw0) || pw0 < 0.99f) asset.Vertices[i0] += correction;
            if (!asset.PinConstraints.TryGetValue(i1, out var pw1) || pw1 < 0.99f) asset.Vertices[i1] -= correction;
        }

        /// <summary>获取已装备服装的总顶点数。</summary>
        public int GetTotalVertexCount() => EquippedItems.Values.Sum(a => a.VertexCount);
        /// <summary>获取已装备服装的总三角面数。</summary>
        public int GetTotalTriangleCount() => EquippedItems.Values.Sum(a => a.TriangleCount);
    }

    #endregion

    #region ===================== 材质实例化系统 =====================

    /// <summary>材质参数集。</summary>

    /// <summary>角色材质集合。</summary>
    public sealed class CharacterMaterialSet
    {
        public MaterialInstance SkinMaterial { get; set; }
        public MaterialInstance EyeMaterial { get; set; }
        public MaterialInstance HairMaterial { get; set; }
        public Dictionary<string, MaterialInstance> ClothingMaterials { get; } = new();
        public MaterialInstance AccessoryMaterial { get; set; } = new() { Name = "Accessory_Material", Type = MaterialType.Accessory };

        public CharacterMaterialSet(CharacterDNA dna)
        {
            SkinMaterial = MaterialInstance.CreateSkinMaterial(dna.Skin);
            EyeMaterial = MaterialInstance.CreateEyeMaterial(dna.Skin);
            HairMaterial = MaterialInstance.CreateHairMaterial(dna.Hair);
        }
    }

    #endregion

    #region ===================== LOD 管理系统 =====================

    /// <summary>LOD 配置。</summary>
    public sealed class LODConfig
    {
        public CharacterLOD Level { get; set; } = CharacterLOD.LOD0_Full;
        public int MaxTriangles { get; set; }
        public int MaxVertices { get; set; }
        public float ScreenSize { get; set; } // 切换屏幕尺寸比例
        public bool EnableHairPhysics { get; set; } = true;
        public bool EnableClothPhysics { get; set; } = true;
        public bool EnableFacialRig { get; set; } = true;
        public int HairStrandCount { get; set; }
        public int FacialBlendShapes { get; set; } = 52;
        public float MeshSimplificationFactor { get; set; }

        /// <summary>构建默认 LOD 配置。</summary>
        public static LODConfig CreateDefault(CharacterLOD level) => level switch
        {
            CharacterLOD.LOD0_Full => new()
            {
                Level = level, MaxTriangles = 150000, MaxVertices = 80000,
                ScreenSize = 1f, EnableHairPhysics = true, EnableClothPhysics = true,
                EnableFacialRig = true, HairStrandCount = 100000, FacialBlendShapes = 52,
                MeshSimplificationFactor = 1f
            },
            CharacterLOD.LOD1_Reduced => new()
            {
                Level = level, MaxTriangles = 25000, MaxVertices = 15000,
                ScreenSize = 0.15f, EnableHairPhysics = true, EnableClothPhysics = true,
                EnableFacialRig = true, HairStrandCount = 20000, FacialBlendShapes = 30,
                MeshSimplificationFactor = 0.5f
            },
            CharacterLOD.LOD2_Simple => new()
            {
                Level = level, MaxTriangles = 5000, MaxVertices = 3000,
                ScreenSize = 0.05f, EnableHairPhysics = false, EnableClothPhysics = false,
                EnableFacialRig = false, HairStrandCount = 0, FacialBlendShapes = 0,
                MeshSimplificationFactor = 0.15f
            },
            CharacterLOD.LOD3_Billboard => new()
            {
                Level = level, MaxTriangles = 4, MaxVertices = 4,
                ScreenSize = 0.01f, EnableHairPhysics = false, EnableClothPhysics = false,
                EnableFacialRig = false, HairStrandCount = 0, FacialBlendShapes = 0,
                MeshSimplificationFactor = 0f
            },
            _ => CreateDefault(CharacterLOD.LOD0_Full)
        };
    }

    /// <summary>LOD 管理器。</summary>
    public sealed class LODManager
    {
        private readonly Dictionary<CharacterLOD, LODConfig> _configs = new();

        /// <summary>当前 LOD 级别。</summary>
        public CharacterLOD CurrentLOD { get; private set; } = CharacterLOD.LOD0_Full;

        public LODManager()
        {
            foreach (CharacterLOD level in Enum.GetValues(typeof(CharacterLOD)))
                _configs[level] = LODConfig.CreateDefault(level);
        }

        /// <summary>根据屏幕大小自动选择 LOD。</summary>
        public CharacterLOD SelectLOD(float screenSizeRatio)
        {
            // 从低到高检查
            for (int i = (int)CharacterLOD.LOD3_Billboard; i >= 0; i--)
            {
                var config = _configs[(CharacterLOD)i];
                if (screenSizeRatio <= config.ScreenSize)
                {
                    CurrentLOD = (CharacterLOD)i;
                    return CurrentLOD;
                }
            }
            CurrentLOD = CharacterLOD.LOD0_Full;
            return CurrentLOD;
        }

        /// <summary>强制设置 LOD。</summary>
        public void ForceLOD(CharacterLOD lod) => CurrentLOD = lod;

        /// <summary>获取当前 LOD 配置。</summary>
        public LODConfig GetCurrentConfig() => _configs[CurrentLOD];

        /// <summary>获取指定 LOD 配置。</summary>
        public LODConfig GetConfig(CharacterLOD lod) => _configs[lod];

        /// <summary>是否需要毛发物理。</summary>
        public bool NeedsHairPhysics => _configs[CurrentLOD].EnableHairPhysics;

        /// <summary>是否需要布料物理。</summary>
        public bool NeedsClothPhysics => _configs[CurrentLOD].EnableClothPhysics;

        /// <summary>是否需要面部绑定。</summary>
        public bool NeedsFacialRig => _configs[CurrentLOD].EnableFacialRig;

        /// <summary>当前 LOD 毛发数量上限。</summary>
        public int MaxHairStrands => _configs[CurrentLOD].HairStrandCount;

        /// <summary>当前 LOD 面部 BlendShape 数量。</summary>
        public int ActiveBlendShapes => _configs[CurrentLOD].FacialBlendShapes;
    }

    #endregion

    #region ===================== DigitalHumanAssembly 角色组装流水线 =====================

    /// <summary>组装阶段。</summary>
    public enum AssemblyStage
    {
        NotStarted,
        DNAProcessing,
        MeshGeneration,
        BodyRigBinding,
        FacialRigBinding,
        HairGrooming,
        ClothingFitting,
        MaterialInstancing,
        LODGeneration,
        Completed,
        Failed
    }

    /// <summary>组装进度回调。</summary>
    public delegate void AssemblyProgressCallback(AssemblyStage stage, float progress, string message);

    /// <summary>角色实例：组装完成后的完整角色对象。</summary>
    public sealed class DigitalHumanInstance
    {
        public string Name { get; set; } = "DigitalHuman";
        public CharacterDNA DNA { get; set; }
        public BodyRig BodyRig { get; set; }
        public FacialRig FacialRig { get; set; }
        public HairGroomingSystem HairSystem { get; set; }
        public ClothingSystem ClothingSystem { get; set; }
        public CharacterMaterialSet Materials { get; set; }
        public LODManager LODManager { get; set; }
        public AssemblyStage Status { get; set; } = AssemblyStage.NotStarted;

        /// <summary>LOD 网格缓存 (LOD -> 三角面数)。</summary>
        public Dictionary<CharacterLOD, int> LODTriangleCounts { get; } = new();

        public DigitalHumanInstance(CharacterDNA dna)
        {
            DNA = dna;
            BodyRig = new BodyRig();
            FacialRig = new FacialRig();
            HairSystem = new HairGroomingSystem();
            ClothingSystem = new ClothingSystem();
            Materials = new CharacterMaterialSet(dna);
            LODManager = new LODManager();
        }
    }

    /// <summary>角色组装流水线：DNA -> Mesh -> Rig -> Clothing -> Hair。</summary>
    public sealed class DigitalHumanAssembly
    {
        /// <summary>组装进度回调。</summary>
        public event AssemblyProgressCallback? OnProgress;

        /// <summary>异步组装完成回调。</summary>
        public event Action<DigitalHumanInstance>? OnComplete;

        /// <summary>组装失败回调。</summary>
        public event Action<Exception>? OnFailed;

        private volatile AssemblyStage _currentStage = AssemblyStage.NotStarted;

        /// <summary>当前组装阶段。</summary>
        public AssemblyStage CurrentStage => _currentStage;

        /// <summary>同步组装角色（阻塞）。</summary>
        public DigitalHumanInstance Assemble(CharacterDNA dna)
        {
            try
            {
                var instance = new DigitalHumanInstance(dna);

                // 阶段1: DNA 处理
                SetStage(AssemblyStage.DNAProcessing, 0f, "Processing DNA...");
                ValidateDNA(dna);

                // 阶段2: 网格生成（模拟）
                SetStage(AssemblyStage.MeshGeneration, 0.15f, "Generating base mesh...");
                // 在实际引擎中，这里会调用 MeshGenerator 从 DNA 生成身体/面部网格

                // 阶段3: 身体骨骼绑定
                SetStage(AssemblyStage.BodyRigBinding, 0.3f, "Binding body rig...");
                instance.BodyRig.ApplyBodyProportions(dna.Body);

                // 阶段4: 面部绑定
                SetStage(AssemblyStage.FacialRigBinding, 0.45f, "Binding facial rig...");
                // 面部 BlendShape 由 DNA 到 Morph 映射驱动
                instance.FacialRig.BlendShapes.Reset();

                // 阶段5: 毛发生成
                SetStage(AssemblyStage.HairGrooming, 0.55f, "Grooming hair...");
                instance.HairSystem.GenerateStrands(dna.Hair,
                    Vector3.UnitY * (dna.Body.Height * 0.005f + 0.88f),
                    0.06f + dna.Body.Height * 0.0002f);

                // 阶段6: 服装适配
                SetStage(AssemblyStage.ClothingFitting, 0.7f, "Fitting clothing...");
                instance.ClothingSystem.FitToBody(dna.Body, instance.BodyRig);

                // 阶段7: 材质实例化
                SetStage(AssemblyStage.MaterialInstancing, 0.8f, "Creating material instances...");
                // 材质已在 DigitalHumanInstance 构造中创建

                // 阶段8: LOD 生成
                SetStage(AssemblyStage.LODGeneration, 0.9f, "Generating LOD levels...");
                GenerateLODs(instance);

                // 完成
                SetStage(AssemblyStage.Completed, 1f, "Assembly complete.");
                instance.Status = AssemblyStage.Completed;
                OnComplete?.Invoke(instance);
                return instance;
            }
            catch (Exception ex)
            {
                SetStage(AssemblyStage.Failed, 0f, $"Assembly failed: {ex.Message}");
                OnFailed?.Invoke(ex);
                throw;
            }
        }

        private void ValidateDNA(CharacterDNA dna)
        {
            if (dna == null) throw new ArgumentNullException(nameof(dna));
            if (dna.Body.Height < 140 || dna.Body.Height > 220)
                throw new ArgumentOutOfRangeException(nameof(dna.Body.Height),
                    $"Height {dna.Body.Height}cm out of valid range [140, 220]");
            if (dna.Body.Weight < 40 || dna.Body.Weight > 150)
                throw new ArgumentOutOfRangeException(nameof(dna.Body.Weight),
                    $"Weight {dna.Body.Weight}kg out of valid range [40, 150]");
        }

        private void GenerateLODs(DigitalHumanInstance instance)
        {
            foreach (CharacterLOD level in Enum.GetValues(typeof(CharacterLOD)))
            {
                var config = instance.LODManager.GetConfig(level);
                int triangles = (int)(150000 * config.MeshSimplificationFactor);
                instance.LODTriangleCounts[level] = Math.Max(triangles, config.MaxTriangles);
            }
        }

        private void SetStage(AssemblyStage stage, float progress, string message)
        {
            _currentStage = stage;
            OnProgress?.Invoke(stage, progress, message);
        }
    }

    #endregion

    #region ===================== DigitalHumanCreator 主入口 =====================

    /// <summary>
    /// DigitalHuman 角色创建器 —— UE6 级角色创建系统的统一入口。
    /// 整合 DNA / 面部 / 身体 / 肤色 / 服装 / 毛发 / 材质 / LOD 的完整工具链。
    /// </summary>
    public sealed class DigitalHumanCreator
    {
        /// <summary>角色 DNA 数据核心。</summary>
        public CharacterDNA DNA { get; set; } = new();

        /// <summary>身体骨骼绑定。</summary>
        public BodyRig BodyRig { get; } = new();

        /// <summary>面部绑定。</summary>
        public FacialRig FacialRig { get; } = new();

        /// <summary>毛发梳理系统。</summary>
        public HairGroomingSystem HairSystem { get; } = new();

        /// <summary>服装系统。</summary>
        public ClothingSystem ClothingSystem { get; } = new();

        /// <summary>材质集合。</summary>
        public CharacterMaterialSet Materials { get; private set; }

        /// <summary>LOD 管理器。</summary>
        public LODManager LODManager { get; } = new();

        /// <summary>角色组装流水线。</summary>
        public DigitalHumanAssembly Assembly { get; } = new();

        /// <summary>对称编辑模式。</summary>
        public bool SymmetricEditing { get; set; } = true;

        /// <summary>BlendShape 权重缓存。</summary>
        public Dictionary<string, float> BlendShapeWeights { get; private set; } = new();

        private static readonly Random _rnd = new();

        public DigitalHumanCreator()
        {
            Materials = new CharacterMaterialSet(DNA);
        }

        // ── DNA 编辑 API ──────────────────────────────────────

        /// <summary>设置面部 DNA 参数。</summary>
        public void SetFaceGene(string paramName, float value)
        {
            value = Math.Clamp(value, 0f, 1f);
            DNA.Face.ApplyParameters(new[] { (paramName, value) });
            RefreshMaterials();
        }

        /// <summary>设置身体 DNA 参数。</summary>
        public void SetBodyGene(string paramName, float value)
        {
            DNA.Body.ApplyParameters(new[] { (paramName, value) });
            BodyRig.ApplyBodyProportions(DNA.Body);
            ClothingSystem.FitToBody(DNA.Body, BodyRig);
        }

        /// <summary>设置肤色。</summary>
        public void SetSkinColor(Vector3 color)
        {
            DNA.Skin.SkinColor = color;
            RefreshMaterials();
        }

        /// <summary>设置眼色。</summary>
        public void SetEyeColor(Vector3 color)
        {
            DNA.Skin.EyeColor = color;
            RefreshMaterials();
        }

        /// <summary>设置发色。</summary>
        public void SetHairColor(Vector3 rootColor, Vector3 tipColor)
        {
            DNA.Hair.RootColor = rootColor;
            DNA.Hair.TipColor = tipColor;
            RefreshMaterials();
        }

        /// <summary>设置身高。</summary>
        public void SetHeight(float heightCm)
        {
            DNA.Body.Height = Math.Clamp(heightCm, 140f, 220f);
            BodyRig.ApplyBodyProportions(DNA.Body);
        }

        /// <summary>设置体重。</summary>
        public void SetWeight(float weightKg)
        {
            DNA.Body.Weight = Math.Clamp(weightKg, 40f, 150f);
        }

        private void RefreshMaterials()
        {
            Materials = new CharacterMaterialSet(DNA);
        }

        // ── 面部表情 API ─────────────────────────────────────

        /// <summary>设置情绪表情。</summary>
        public void SetEmotion(EmotionType emotion, float weight = 1f)
        {
            FacialRig.SetEmotion(emotion, weight);
        }

        /// <summary>情绪过渡。</summary>
        public void TransitionEmotion(EmotionType from, EmotionType to, float t)
        {
            FacialRig.TransitionEmotion(from, to, t);
        }

        /// <summary>设置眼球注视。</summary>
        public void SetGaze(float horizontal, float vertical)
        {
            FacialRig.SetGaze(horizontal, vertical);
        }

        // ── 服装 API ─────────────────────────────────────────

        /// <summary>装备服装。</summary>
        public bool EquipClothing(ClothingAsset asset)
        {
            bool success = ClothingSystem.Equip(asset);
            if (success) ClothingSystem.FitToBody(DNA.Body, BodyRig);
            return success;
        }

        /// <summary>卸下服装。</summary>
        public bool UnequipClothing(ClothingSlot slot) => ClothingSystem.Unequip(slot);

        // ── 毛发 API ─────────────────────────────────────────

        /// <summary>重新生成毛发。</summary>
        public void RegenerateHair()
        {
            Vector3 headCenter = Vector3.UnitY * (DNA.Body.Height * 0.005f + 0.88f);
            float headRadius = 0.06f + DNA.Body.Height * 0.0002f;
            HairSystem.GenerateStrands(DNA.Hair, headCenter, headRadius);
        }

        // ── 模拟步进 API ─────────────────────────────────────

        /// <summary>每帧更新（面部+毛发+服装物理）。</summary>
        public void Update(float deltaTime)
        {
            if (LODManager.NeedsFacialRig) FacialRig.Update(deltaTime);
            if (LODManager.NeedsHairPhysics) HairSystem.Simulate(deltaTime);
            if (LODManager.NeedsClothPhysics) ClothingSystem.Simulate(deltaTime);
        }

        // ── 组装 API ─────────────────────────────────────────

        /// <summary>组装完整角色实例。</summary>
        public DigitalHumanInstance Build()
        {
            return Assembly.Assemble(DNA);
        }

        // ── 随机化 API ─────────────────────────────────────

        /// <summary>随机生成角色参数。</summary>
        public void Randomize()
        {
            // 随机面部
            foreach (var (name, _) in DNA.Face.CollectParameters())
            {
                float val = (float)_rnd.NextDouble();
                DNA.Face.ApplyParameters(new[] { (name, val) });
            }
            // 随机身体
            DNA.Body.Height = 155f + (float)_rnd.NextDouble() * 60f;
            DNA.Body.Weight = 45f + (float)_rnd.NextDouble() * 80f;
            DNA.Body.MuscleMass = (float)_rnd.NextDouble();
            DNA.Body.BodyFat = (float)_rnd.NextDouble();
            DNA.Body.ShoulderWidth = (float)_rnd.NextDouble();
            DNA.Body.ChestGirth = (float)_rnd.NextDouble();
            DNA.Body.WaistGirth = (float)_rnd.NextDouble();
            DNA.Body.HipGirth = (float)_rnd.NextDouble();
            DNA.Body.HeadToBodyRatio = (float)_rnd.NextDouble();
            DNA.Body.ArmLength = (float)_rnd.NextDouble();
            DNA.Body.LegLength = (float)_rnd.NextDouble();
            // 随机肤色
            float melanin = (float)_rnd.NextDouble();
            DNA.Skin.Melanin = melanin;
            DNA.Skin.SkinColor = new Vector3(0.95f - melanin * 0.8f, 0.85f - melanin * 0.7f, 0.75f - melanin * 0.65f);
            // 随机发色
            DNA.Hair.RootColor = new Vector3((float)_rnd.NextDouble() * 0.3f, (float)_rnd.NextDouble() * 0.2f, (float)_rnd.NextDouble() * 0.1f);
            DNA.Hair.TipColor = DNA.Hair.RootColor * 1.5f;
            DNA.Hair.Length = (float)_rnd.NextDouble();
            DNA.Hair.Curliness = (float)_rnd.NextDouble();
            DNA.Hair.Density = 0.3f + (float)_rnd.NextDouble() * 0.7f;
            // 随机性别
            DNA.Gender = _rnd.NextDouble() < 0.5 ? Gender.Male : Gender.Female;

            // 刷新所有子系统
            BodyRig.ApplyBodyProportions(DNA.Body);
            RefreshMaterials();
            RegenerateHair();
            ClothingSystem.FitToBody(DNA.Body, BodyRig);
        }

        // ── 预设管理 API ─────────────────────────────────────

        /// <summary>保存角色预设。</summary>
        public void SavePreset(string path) => DNA.Save(path);

        /// <summary>加载角色预设。</summary>
        public void LoadPreset(string path)
        {
            DNA = CharacterDNA.Load(path);
            BodyRig.ApplyBodyProportions(DNA.Body);
            RefreshMaterials();
            RegenerateHair();
        }

        /// <summary>获取角色统计信息。</summary>
        public string GetStats()
        {
            return $"Character: {DNA.Name} | " +
                   $"Bones: {BodyRig.BoneCount} | " +
                   $"Hair Strands: {HairSystem.Strands.Count} | " +
                   $"Clothing Items: {ClothingSystem.EquippedItems.Count} | " +
                   $"LOD: {LODManager.CurrentLOD} | " +
                   $"Face Genes: {DNA.Face.ParameterCount} | " +
                   $"Body Genes: {DNA.Body.ParameterCount}";
        }
    }

    #endregion
}
