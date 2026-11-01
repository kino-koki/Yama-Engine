using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace YamaEngine.Character
{
    // ======================================================================
    // yama 角色 DNA 扩展系统 (CharacterDNA Extensions)
    //
    // 本文件提供 CharacterDNA 的扩展功能，不重复定义基础 DNA 类型
    // （FaceDNA / BodyDNA / SkinDNA / HairDNA / CharacterDNA 已在
    //  DigitalHumanCreator.cs 中定义）。
    //
    // 扩展功能：
    //   1. DnaMorphMap          — DNA -> BlendShape 映射表
    //   2. CharacterDNA 扩展方法 — 高级混合、预设库、序列化增强
    //   3. DNA 预设库           — 内置角色预设（全 DNA 参数快照）
    //   4. DNA 随机化增强       — 种族感知随机化、参数约束
    // ======================================================================

    // ════════════════════════════════════════════════════════════════════════
    // DNA -> Morph 映射
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DNA 到 Morph 映射条目：一个 DNA 参数映射到一个或多个 BlendShape 权重。
    /// </summary>
    public sealed class DnaMorphMapping
    {
        /// <summary>DNA 参数名称。</summary>
        public string DnaParameter { get; set; } = string.Empty;
        /// <summary>目标 BlendShape 名称。</summary>
        public string BlendShapeName { get; set; } = string.Empty;
        /// <summary>是否反向（true 时 weight = 1 - dnaValue）。</summary>
        public bool Invert { get; set; }
        /// <summary>权重缩放系数。</summary>
        public float Scale { get; set; } = 1.0f;
        /// <summary>权重偏移。</summary>
        public float Offset { get; set; }
    }

    /// <summary>
    /// DNA -> Morph 映射表：将 CharacterDNA 参数转换为 BlendShape 权重字典。
    /// 提供默认映射规则，并支持自定义扩展。
    /// </summary>
    public static class DnaMorphMap
    {
        private static readonly List<DnaMorphMapping> DefaultMappings = BuildDefaultMappings();

        private static List<DnaMorphMapping> BuildDefaultMappings()
        {
            var list = new List<DnaMorphMapping>();

            // 面部 DNA -> ARKit BlendShape 映射
            Add(list, "EyeSpacing", "eyeWide", false);
            Add(list, "EyeSize", "eyeWide_L", false, 0.8f);
            Add(list, "EyeSize", "eyeWide_R", false, 0.8f);
            Add(list, "EyeTilt", "eyeLookUp_L", false, 0.5f);
            Add(list, "BrowHeight", "browInnerUp", false, 0.7f);
            Add(list, "BrowHeight", "browOuterUp_L", false, 0.4f);
            Add(list, "BrowHeight", "browOuterUp_R", false, 0.4f);
            Add(list, "BrowThickness", "browDown_L", false, 0.5f);
            Add(list, "BrowThickness", "browDown_R", false, 0.5f);
            Add(list, "BrowAngle", "browInnerUp", false, 0.6f);
            Add(list, "NoseBridgeHeight", "noseSneer_L", false, 0.3f);
            Add(list, "NoseBridgeHeight", "noseSneer_R", false, 0.3f);
            Add(list, "NoseTipAngle", "noseSneer_L", true, 0.4f);
            Add(list, "NostrilWidth", "noseSneer_L", false, 0.4f);
            Add(list, "LipThickness", "mouthFunnel", false, 0.3f);
            Add(list, "UpperLipThickness", "mouthUpperUp_L", false, 0.5f);
            Add(list, "UpperLipThickness", "mouthUpperUp_R", false, 0.5f);
            Add(list, "LowerLipThickness", "mouthLowerDown_L", false, 0.3f);
            Add(list, "LowerLipThickness", "mouthLowerDown_R", false, 0.3f);
            Add(list, "MouthWidth", "mouthStretch_L", false, 0.4f);
            Add(list, "MouthWidth", "mouthStretch_R", false, 0.4f);
            Add(list, "MouthCornerHeight", "mouthSmile_L", false, 0.6f);
            Add(list, "MouthCornerHeight", "mouthSmile_R", false, 0.6f);
            Add(list, "ChinLength", "jawForward", false, 0.4f);
            Add(list, "JawWidth", "cheekPuff", false, 0.3f);
            Add(list, "CheekFullness", "cheekPuff", false, 0.5f);
            Add(list, "CheekboneProminence", "cheekSquint_L", false, 0.2f);
            Add(list, "CheekboneProminence", "cheekSquint_R", false, 0.2f);
            Add(list, "EyeDepth", "eyeSquint_L", false, 0.3f);
            Add(list, "EyeDepth", "eyeSquint_R", false, 0.3f);

            // 身体 DNA -> BlendShape 映射
            AddBody(list, "MuscleMass", "Muscular", false, 1.0f);
            AddBody(list, "BodyFat", "Obese", false, 0.8f);
            AddBody(list, "BicepSize", "Muscular", false, 0.5f);
            AddBody(list, "PectoralSize", "Athletic", false, 0.7f);
            AddBody(list, "AbdominalDefinition", "Athletic", false, 0.8f);
            AddBody(list, "ShoulderWidth", "BroadShoulders", false, 0.6f);

            return list;
        }

        private static void Add(List<DnaMorphMapping> list, string dna, string bs,
                                  bool invert, float scale = 1.0f)
        {
            list.Add(new DnaMorphMapping
            {
                DnaParameter = dna, BlendShapeName = bs,
                Invert = invert, Scale = scale
            });
        }

        private static void AddBody(List<DnaMorphMapping> list, string dna, string bs,
                                      bool invert, float scale = 1.0f)
        {
            list.Add(new DnaMorphMapping
            {
                DnaParameter = "Body." + dna, BlendShapeName = bs,
                Invert = invert, Scale = scale
            });
        }

        /// <summary>获取默认映射规则（只读）。</summary>
        public static IReadOnlyList<DnaMorphMapping> GetDefaultMappings() => DefaultMappings;

        /// <summary>
        /// 将 CharacterDNA 转换为 BlendShape 权重字典。
        /// </summary>
        public static Dictionary<string, float> ToBlendShapeWeights(CharacterDNA dna)
        {
            if (dna == null) throw new ArgumentNullException(nameof(dna));
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            var allParams = dna.CollectAllGenes();

            foreach (var map in DefaultMappings)
            {
                if (!allParams.TryGetValue(map.DnaParameter, out var dnaValue)) continue;
                float weight = (map.Invert ? (1f - dnaValue) : dnaValue) * map.Scale + map.Offset;
                weight = Math.Clamp(weight, 0f, 1f);
                if (result.TryGetValue(map.BlendShapeName, out var existing))
                    result[map.BlendShapeName] = Math.Max(existing, weight);
                else
                    result[map.BlendShapeName] = weight;
            }
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CharacterDNA 扩展方法（partial 语义通过静态方法实现）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CharacterDNA 扩展工具类：提供高级 DNA 操作。
    /// </summary>
    public static class CharacterDNAExtensions
    {
        /// <summary>
        /// 高级 DNA 混合（孟德尔式遗传 + 轻微突变）。
        /// </summary>
        public static CharacterDNA BreedAdvanced(CharacterDNA parentA, CharacterDNA parentB,
                                                  int seed = -1, float mutationRate = 0.08f)
        {
            if (parentA == null) throw new ArgumentNullException(nameof(parentA));
            if (parentB == null) throw new ArgumentNullException(nameof(parentB));
            var rnd = seed < 0 ? new Random() : new Random(seed);

            var child = new CharacterDNA
            {
                Name = "Offspring",
                Gender = rnd.NextDouble() < 0.5 ? parentA.Gender : parentB.Gender,
                Ethnicity = rnd.NextDouble() < 0.5 ? parentA.Ethnicity : parentB.Ethnicity,
                AgeGroup = AgeGroup.Child
            };

            // 面部混合
            child.Face = BlendDNA(parentA.Face, parentB.Face, rnd, mutationRate);
            // 身体混合
            child.Body = BlendBodyDNA(parentA.Body, parentB.Body, rnd, mutationRate);
            // 肤色混合
            child.Skin = BlendSkinDNA(parentA.Skin, parentB.Skin, rnd, mutationRate);
            // 毛发混合
            child.Hair = BlendHairDNA(parentA.Hair, parentB.Hair, rnd, mutationRate);

            return child;
        }

        private static float InheritFloat(float a, float b, Random rnd, float mr)
        {
            float baseVal = rnd.NextDouble() < 0.5 ? a : b;
            float avg = (a + b) * 0.5f;
            float blend = rnd.NextDouble() < 0.3 ? avg : baseVal;
            float mutation = (float)(rnd.NextDouble() * 2.0 - 1.0) * mr;
            return Math.Clamp(blend + mutation, 0f, 1f);
        }

        private static FaceDNA BlendDNA(FaceDNA a, FaceDNA b, Random rnd, float mr)
        {
            var result = new FaceDNA();
            var pairsA = a.CollectParameters();
            var pairsB = b.CollectParameters();
            var blended = new List<(string, float)>();
            for (int i = 0; i < pairsA.Count; i++)
            {
                string name = pairsA[i].Name;
                float va = pairsA[i].Value;
                float vb = i < pairsB.Count ? pairsB[i].Value : va;
                blended.Add((name, InheritFloat(va, vb, rnd, mr)));
            }
            result.ApplyParameters(blended);
            return result;
        }

        private static BodyDNA BlendBodyDNA(BodyDNA a, BodyDNA b, Random rnd, float mr)
        {
            var result = new BodyDNA();
            var pairsA = a.CollectParameters();
            var pairsB = b.CollectParameters();
            var blended = new List<(string, float)>();
            for (int i = 0; i < pairsA.Count; i++)
            {
                string name = pairsA[i].Name;
                float va = pairsA[i].Value;
                float vb = i < pairsB.Count ? pairsB[i].Value : va;
                if (name == "Height" || name == "Weight")
                {
                    float avg = (va + vb) * 0.5f;
                    float mutation = (float)(rnd.NextDouble() * 2.0 - 1.0) * mr * 5f;
                    blended.Add((name, avg + mutation));
                }
                else
                {
                    blended.Add((name, InheritFloat(va, vb, rnd, mr)));
                }
            }
            result.ApplyParameters(blended);
            return result;
        }

        private static SkinDNA BlendSkinDNA(SkinDNA a, SkinDNA b, Random rnd, float mr)
        {
            float t = (float)rnd.NextDouble();
            return new SkinDNA
            {
                SkinColor = Vector3.Lerp(a.SkinColor, b.SkinColor, t),
                Melanin = InheritFloat(a.Melanin, b.Melanin, rnd, mr),
                Hemoglobin = InheritFloat(a.Hemoglobin, b.Hemoglobin, rnd, mr),
                SssRadius = (a.SssRadius + b.SssRadius) * 0.5f,
                SssColor = Vector3.Lerp(a.SssColor, b.SssColor, t),
                Roughness = InheritFloat(a.Roughness, b.Roughness, rnd, mr),
                EyeColor = rnd.NextDouble() < 0.5 ? a.EyeColor : b.EyeColor
            };
        }

        private static HairDNA BlendHairDNA(HairDNA a, HairDNA b, Random rnd, float mr)
        {
            float t = (float)rnd.NextDouble();
            return new HairDNA
            {
                RootColor = Vector3.Lerp(a.RootColor, b.RootColor, t),
                TipColor = Vector3.Lerp(a.TipColor, b.TipColor, t),
                SpecularColor = Vector3.Lerp(a.SpecularColor, b.SpecularColor, t),
                Density = InheritFloat(a.Density, b.Density, rnd, mr),
                Length = InheritFloat(a.Length, b.Length, rnd, mr),
                Curliness = InheritFloat(a.Curliness, b.Curliness, rnd, mr),
                Roughness = InheritFloat(a.Roughness, b.Roughness, rnd, mr),
                Anisotropy = InheritFloat(a.Anisotropy, b.Anisotropy, rnd, mr),
                IOR = (a.IOR + b.IOR) * 0.5f
            };
        }

        /// <summary>
        /// 种族感知的 DNA 随机化。
        /// </summary>
        public static void RandomizeAdvanced(CharacterDNA dna, int seed = -1)
        {
            var rnd = seed < 0 ? new Random() : new Random(seed);
            dna.Gender = (Gender)rnd.Next(0, 3);
            dna.Ethnicity = (Ethnicity)rnd.Next(0, 8);
            dna.AgeGroup = AgeGroup.YoungAdult;

            // 面部随机
            var faceParams = new List<(string, float)>();
            foreach (var (name, _) in dna.Face.CollectParameters())
                faceParams.Add((name, (float)rnd.NextDouble()));
            dna.Face.ApplyParameters(faceParams);

            // 身体随机
            dna.Body.Height = 150f + (float)rnd.NextDouble() * 60f;
            dna.Body.Weight = 50f + (float)rnd.NextDouble() * 60f;
            var bodyParams = new List<(string, float)>();
            foreach (var (name, _) in dna.Body.CollectParameters())
            {
                if (name == "Height" || name == "Weight") continue;
                bodyParams.Add((name, (float)rnd.NextDouble()));
            }
            dna.Body.ApplyParameters(bodyParams);

            // 肤色随机（种族感知）
            float melaninBase = dna.Ethnicity switch
            {
                Ethnicity.African => 0.7f + (float)rnd.NextDouble() * 0.3f,
                Ethnicity.SouthAsian => 0.5f + (float)rnd.NextDouble() * 0.3f,
                Ethnicity.MiddleEastern => 0.4f + (float)rnd.NextDouble() * 0.3f,
                Ethnicity.LatinAmerican => 0.3f + (float)rnd.NextDouble() * 0.3f,
                Ethnicity.EastAsian => 0.2f + (float)rnd.NextDouble() * 0.2f,
                Ethnicity.Polynesian => 0.4f + (float)rnd.NextDouble() * 0.3f,
                Ethnicity.NativeAmerican => 0.3f + (float)rnd.NextDouble() * 0.3f,
                _ => 0.1f + (float)rnd.NextDouble() * 0.3f
            };
            dna.Skin.Melanin = melaninBase;
            float m = melaninBase;
            dna.Skin.SkinColor = new Vector3(0.95f - m * 0.7f, 0.82f - m * 0.6f, 0.72f - m * 0.5f);
            dna.Skin.EyeColor = new Vector3((float)rnd.NextDouble(), (float)rnd.NextDouble() * 0.6f, (float)rnd.NextDouble() * 0.4f);

            // 毛发随机
            float hv = (float)rnd.NextDouble() * 0.5f;
            dna.Hair.RootColor = new Vector3(hv, hv * 0.7f, hv * 0.5f);
            dna.Hair.TipColor = dna.Hair.RootColor * 1.5f;
            dna.Hair.Density = 0.3f + (float)rnd.NextDouble() * 0.6f;
            dna.Hair.Length = (float)rnd.NextDouble();
            dna.Hair.Curliness = (float)rnd.NextDouble() * 0.6f;
        }

        /// <summary>
        /// DNA 差异度计算（0=完全相同, 1=完全不同）。
        /// </summary>
        public static float ComputeDifference(CharacterDNA a, CharacterDNA b)
        {
            float totalDiff = 0;
            int paramCount = 0;

            var paramsA = a.Face.CollectParameters();
            var paramsB = b.Face.CollectParameters();
            for (int i = 0; i < Math.Min(paramsA.Count, paramsB.Count); i++)
            {
                totalDiff += Math.Abs(paramsA[i].Value - paramsB[i].Value);
                paramCount++;
            }

            var bodyA = a.Body.CollectParameters();
            var bodyB = b.Body.CollectParameters();
            for (int i = 0; i < Math.Min(bodyA.Count, bodyB.Count); i++)
            {
                float diff = Math.Abs(bodyA[i].Value - bodyB[i].Value);
                // 身高/体重归一化到 0~1 范围
                if (bodyA[i].Name == "Height") diff = Math.Abs(bodyA[i].Value - bodyB[i].Value) / 80f;
                else if (bodyA[i].Name == "Weight") diff = Math.Abs(bodyA[i].Value - bodyB[i].Value) / 110f;
                totalDiff += diff;
                paramCount++;
            }

            return paramCount > 0 ? totalDiff / paramCount : 0;
        }

        /// <summary>
        /// 深拷贝 CharacterDNA。
        /// </summary>
        public static CharacterDNA DeepClone(this CharacterDNA dna)
        {
            var clone = new CharacterDNA
            {
                Name = dna.Name, Gender = dna.Gender,
                Ethnicity = dna.Ethnicity, AgeGroup = dna.AgeGroup,
                Version = dna.Version
            };
            clone.Face.ApplyParameters(dna.Face.CollectParameters());
            clone.Body.ApplyParameters(dna.Body.CollectParameters());
            clone.Skin = dna.Skin.Clone();
            clone.Hair = dna.Hair.Clone();
            return clone;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // DNA 预设库
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>DNA 预设：完整的角色 DNA 快照。</summary>
    public sealed class DnaPreset
    {
        public string Name { get; set; } = "Preset";
        public string Description { get; set; } = "";
        public CharacterDNA DNA { get; set; } = new();

        /// <summary>从预设创建 CharacterDNA 副本。</summary>
        public CharacterDNA Instantiate()
        {
            var dna = DNA.DeepClone();
            dna.Name = Name;
            return dna;
        }
    }

    /// <summary>内置 DNA 预设库。</summary>
    public static class DnaPresetLibrary
    {
        private static readonly List<DnaPreset> _builtInPresets = BuildPresets();

        /// <summary>获取所有内置预设。</summary>
        public static IReadOnlyList<DnaPreset> GetAllPresets() => _builtInPresets;

        /// <summary>按名称查找预设。</summary>
        public static DnaPreset? Find(string name) =>
            _builtInPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>从预设初始化 CharacterDNA。</summary>
        public static bool ApplyPreset(CharacterDNA dna, string presetName)
        {
            var preset = Find(presetName);
            if (preset == null) return false;
            var instantiated = preset.Instantiate();
            dna.Face.ApplyParameters(instantiated.Face.CollectParameters());
            dna.Body.ApplyParameters(instantiated.Body.CollectParameters());
            dna.Skin = instantiated.Skin.Clone();
            dna.Hair = instantiated.Hair.Clone();
            dna.Gender = instantiated.Gender;
            dna.Ethnicity = instantiated.Ethnicity;
            dna.AgeGroup = instantiated.AgeGroup;
            return true;
        }

        private static List<DnaPreset> BuildPresets()
        {
            var presets = new List<DnaPreset>();

            // 欧洲男性 - 英俊
            presets.Add(new DnaPreset
            {
                Name = "European_Handsome_Male",
                Description = "欧洲英俊男性预设",
                DNA = CreatePreset(Gender.Male, Ethnicity.European,
                    face: new Dictionary<string, float>
                    {
                        ["EyeSize"] = 0.6f, ["EyeDepth"] = 0.4f, ["BrowHeight"] = 0.55f,
                        ["NoseBridgeHeight"] = 0.7f, ["NoseTipSize"] = 0.4f,
                        ["JawWidth"] = 0.6f, ["JawlineDefinition"] = 0.7f,
                        ["ChinProtrusion"] = 0.6f, ["LipThickness"] = 0.45f
                    },
                    body: new Dictionary<string, float>
                    {
                        ["Height"] = 182f, ["Weight"] = 78f,
                        ["MuscleMass"] = 0.55f, ["BodyFat"] = 0.2f,
                        ["ShoulderWidth"] = 0.6f
                    })
            });

            // 东亚女性 - 甜美
            presets.Add(new DnaPreset
            {
                Name = "EastAsian_Cute_Female",
                Description = "东亚甜美女性预设",
                DNA = CreatePreset(Gender.Female, Ethnicity.EastAsian,
                    face: new Dictionary<string, float>
                    {
                        ["EyeSize"] = 0.55f, ["EyeHeight"] = 0.6f, ["EyeSpacing"] = 0.55f,
                        ["EyelidCrease"] = 0.2f, ["BrowThickness"] = 0.35f,
                        ["NoseBridgeHeight"] = 0.35f, ["NoseTipSize"] = 0.4f,
                        ["CheekFullness"] = 0.6f, ["FaceWidth"] = 0.55f,
                        ["LipThickness"] = 0.55f, ["MouthWidth"] = 0.45f
                    },
                    body: new Dictionary<string, float>
                    {
                        ["Height"] = 162f, ["Weight"] = 52f,
                        ["MuscleMass"] = 0.2f, ["BodyFat"] = 0.3f,
                        ["ShoulderWidth"] = 0.35f
                    })
            });

            // 非洲男性 - 运动
            presets.Add(new DnaPreset
            {
                Name = "African_Athletic_Male",
                Description = "非洲运动型男性预设",
                DNA = CreatePreset(Gender.Male, Ethnicity.African,
                    face: new Dictionary<string, float>
                    {
                        ["EyeSize"] = 0.5f, ["NoseBridgeHeight"] = 0.3f,
                        ["NoseBridgeWidth"] = 0.7f, ["NoseTipSize"] = 0.6f,
                        ["LipThickness"] = 0.65f, ["JawWidth"] = 0.65f,
                        ["JawlineDefinition"] = 0.6f, ["CheekboneProminence"] = 0.6f
                    },
                    body: new Dictionary<string, float>
                    {
                        ["Height"] = 185f, ["Weight"] = 85f,
                        ["MuscleMass"] = 0.75f, ["BodyFat"] = 0.15f,
                        ["ShoulderWidth"] = 0.7f, ["AbdominalDefinition"] = 0.7f
                    })
            });

            // 拉丁女性 - 优雅
            presets.Add(new DnaPreset
            {
                Name = "Latin_Elegant_Female",
                Description = "拉丁优雅女性预设",
                DNA = CreatePreset(Gender.Female, Ethnicity.LatinAmerican,
                    face: new Dictionary<string, float>
                    {
                        ["EyeSize"] = 0.55f, ["EyeTilt"] = 0.6f,
                        ["BrowHeight"] = 0.5f, ["BrowAngle"] = 0.55f,
                        ["NoseBridgeHeight"] = 0.45f, ["LipThickness"] = 0.7f,
                        ["MouthWidth"] = 0.55f, ["CheekboneHeight"] = 0.55f,
                        ["JawlineDefinition"] = 0.5f
                    },
                    body: new Dictionary<string, float>
                    {
                        ["Height"] = 165f, ["Weight"] = 58f,
                        ["MuscleMass"] = 0.3f, ["BodyFat"] = 0.35f,
                        ["HipGirth"] = 0.55f, ["ChestGirth"] = 0.5f
                    })
            });

            // 中年欧洲男性
            presets.Add(new DnaPreset
            {
                Name = "European_Middleaged_Male",
                Description = "欧洲中年男性预设",
                DNA = CreatePreset(Gender.Male, Ethnicity.European,
                    face: new Dictionary<string, float>
                    {
                        ["EyeBagIntensity"] = 0.5f, ["BrowHeight"] = 0.4f,
                        ["ForeheadHeight"] = 0.55f, ["JawlineDefinition"] = 0.4f,
                        ["CheekFullness"] = 0.4f, ["LipThickness"] = 0.4f,
                        ["EarlobeSize"] = 0.6f
                    },
                    body: new Dictionary<string, float>
                    {
                        ["Height"] = 178f, ["Weight"] = 85f,
                        ["MuscleMass"] = 0.3f, ["BodyFat"] = 0.5f,
                        ["ShoulderRoll"] = 0.35f
                    })
            });

            return presets;
        }

        private static CharacterDNA CreatePreset(Gender gender, Ethnicity ethnicity,
                                                   Dictionary<string, float> face,
                                                   Dictionary<string, float> body)
        {
            var dna = new CharacterDNA { Gender = gender, Ethnicity = ethnicity };
            dna.Face.ApplyParameters(face.Select(kvp => (kvp.Key, kvp.Value)));
            dna.Body.ApplyParameters(body.Select(kvp => (kvp.Key, kvp.Value)));

            // 根据种族设置默认肤色
            float melanin = ethnicity switch
            {
                Ethnicity.African => 0.85f,
                Ethnicity.SouthAsian => 0.6f,
                Ethnicity.MiddleEastern => 0.5f,
                Ethnicity.LatinAmerican => 0.4f,
                Ethnicity.EastAsian => 0.25f,
                Ethnicity.Polynesian => 0.45f,
                Ethnicity.NativeAmerican => 0.35f,
                _ => 0.15f
            };
            dna.Skin.Melanin = melanin;
            dna.Skin.SkinColor = new Vector3(0.95f - melanin * 0.7f, 0.82f - melanin * 0.6f, 0.72f - melanin * 0.5f);

            return dna;
        }
    }
}
