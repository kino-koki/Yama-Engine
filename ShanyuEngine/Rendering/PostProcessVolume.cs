using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 后处理体积系统 (Post Process Volume)
    //
    // 在已有的 PostProcessingStack（电影级后处理管线）之上，提供“体积化”的后处理
    // 配置能力：设计师在世界中放置若干后处理体积（球体/盒体/胶囊/凸包），当摄像机
    // 进入体积时，该体积的后处理参数会按权重混入最终栈；多个体积可重叠并按优先级
    // 与混合距离平滑过渡。这是 UE/Unity 中“Post Process Volume”的等价实现。
    //
    // 设计要点：
    //   1. PostProcessSettings 为“可覆盖”结构体：每个特效块带 Override 标志，
    //      Override=false 表示“使用上一级”（即更低优先级 / 引擎默认）。
    //   2. 体积权重基于摄像机到体积边界的距离（Blend Distance）做 smoothstep 过渡，
    //      支持无限范围（全局后处理）。
    //   3. VolumeBlendSystem 为 ECS 系统：每帧收集激活体积 -> 按优先级排序 ->
    //      按权重混合 -> 写回 PostProcessingStack.Settings。
    //   4. 与 ECS（IComponent / ISystem）和 Rendering.PostProcessingStack 双向对接。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>后处理体积的形状类型。</summary>
    public enum VolumeShape
    {
        /// <summary>球体：由 Center + Radius 描述。</summary>
        Sphere,
        /// <summary>轴对齐/定向盒体：由 Center + HalfExtents + Orientation 描述。</summary>
        Box,
        /// <summary>胶囊体：由 Center + Radius + Height + Orientation 描述。</summary>
        Capsule,
        /// <summary>凸包：由一组平面（法线 + 距离）描述，用于复杂形状的近似。</summary>
        Convex
    }

    /// <summary>体积混合模式：决定该体积如何与其它体积叠加（预留扩展，当前按优先级+权重混合）。</summary>
    public enum VolumeBlendMode
    {
        /// <summary>按权重叠加混合（默认）。</summary>
        Blend,
        /// <summary>覆盖：该体积权重&gt;0 时完全覆盖低优先级同名参数。</summary>
        Override,
        /// <summary>取最大值（用于强度类参数）。</summary>
        Maximum,
        /// <summary>取最小值。</summary>
        Minimum
    }

    /// <summary>SSAO（屏幕空间环境光遮蔽）质量档位。</summary>
    public enum SsaoQuality { Off, Low, Medium, High, Ultra }

    /// <summary>SSR（屏幕空间反射）质量档位。</summary>
    public enum SsrQuality { Off, Low, Medium, High, Ultra }

    // ────────────────────────────────────────────────────────────────────────
    // 可覆盖特效块：每个块带 Override 标志。Override=false => 不覆盖（继承上级）。
    // 注意：结构体字段不能有初始化器，default(块) 即 Override=false、其余为零值。
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>自动曝光覆盖块。</summary>
    public struct ExposureOverride
    {
        public bool Override;
        public bool Enabled;
        public float Key;
        public float Compensation;
        public float AdaptationSpeed;
        public float MinExposure;
        public float MaxExposure;
        public float ManualExposure;
    }

    /// <summary>景深（DoF）覆盖块。</summary>
    public struct DepthOfFieldOverride
    {
        public bool Override;
        public bool Enabled;
        public float FocusDistance;
        public float Aperture;
        public int BokehShape;          // 对应 BokehShape 枚举值
        public float MaxBokehRadius;
        public float NearFocusStart;
        public float FarFocusStart;
    }

    /// <summary>运动模糊覆盖块。</summary>
    public struct MotionBlurOverride
    {
        public bool Override;
        public bool Enabled;
        public float Strength;
        public int Samples;
    }

    /// <summary>泛光（Bloom）覆盖块。</summary>
    public struct BloomOverride
    {
        public bool Override;
        public bool Enabled;
        public float Threshold;
        public float Intensity;
        public float Radius;
        public Vector3 Tint;
    }

    /// <summary>镜头光晕（Lens Flare）覆盖块。</summary>
    public struct LensFlareOverride
    {
        public bool Override;
        public bool Enabled;
        public float Intensity;
        public int RayCount;
    }

    /// <summary>色差（Chromatic Aberration）覆盖块。</summary>
    public struct ChromaticAberrationOverride
    {
        public bool Override;
        public bool Enabled;
        public float Amount;
        public float RadialStart;
    }

    /// <summary>暗角（Vignette）覆盖块。</summary>
    public struct VignetteOverride
    {
        public bool Override;
        public bool Enabled;
        public float Intensity;
        public float Radius;
        public float Softness;
        public Vector3 Color;
    }

    /// <summary>胶片颗粒（Film Grain）覆盖块。</summary>
    public struct FilmGrainOverride
    {
        public bool Override;
        public bool Enabled;
        public float Intensity;
        public float Size;
        public float Speed;
    }

    /// <summary>色调映射覆盖块。</summary>
    public struct TonemappingOverride
    {
        public bool Override;
        public int Operator;           // 对应 TonemapOperator 枚举值
        public bool ApplySRGB;
    }

    /// <summary>颜色分级覆盖块。</summary>
    public struct ColorGradingOverride
    {
        public bool Override;
        public bool Enabled;
        public float WhiteBalanceTemp;
        public float WhiteBalanceTint;
        public float Saturation;
        public float Contrast;
        public float Gain;
        public Vector3 ColorOffset;
        public float LutIntensity;
        public bool HasLut;
        public RHITexture? LutTexture;
    }

    /// <summary>SSAO 覆盖块（供 SSAO Pass 消费，PostProcessingStack 暂不直接使用）。</summary>
    public struct SsaoOverride
    {
        public bool Override;
        public bool Enabled;
        public int Quality;            // 对应 SsaoQuality 枚举值
        public float Radius;
        public float Intensity;
        public float Power;
    }

    /// <summary>SSR 覆盖块（供 SSR Pass 消费，PostProcessingStack 暂不直接使用）。</summary>
    public struct SsrOverride
    {
        public bool Override;
        public bool Enabled;
        public int Quality;            // 对应 SsrQuality 枚举值
        public float MaxRoughness;
        public float Intensity;
    }

    /// <summary>输出缩放覆盖块。</summary>
    public struct OutputScaleOverride
    {
        public bool Override;
        public float Scale;
    }

    /// <summary>
    /// 后处理设置结构（体积版）。每个特效块可独立覆盖；Override=false 表示
    /// “使用上一级”（更低优先级体积或引擎默认）。default(PostProcessSettings)
    /// 表示“什么都不覆盖”的空配置。
    /// </summary>
    public struct PostProcessSettings
    {
        public ExposureOverride Exposure;
        public DepthOfFieldOverride DoF;
        public MotionBlurOverride MotionBlur;
        public BloomOverride Bloom;
        public LensFlareOverride LensFlare;
        public ChromaticAberrationOverride ChromaticAberration;
        public VignetteOverride Vignette;
        public FilmGrainOverride FilmGrain;
        public TonemappingOverride Tonemapping;
        public ColorGradingOverride ColorGrading;
        public SsaoOverride Ssao;
        public SsrOverride Ssr;
        public OutputScaleOverride OutputScale;

        /// <summary>引擎默认设置（所有块 Override=true，值为引擎缺省值），用作混合的最终回退。</summary>
        public static PostProcessSettings EngineDefaults
        {
            get
            {
                return new PostProcessSettings
                {
                    Exposure = new ExposureOverride
                    {
                        Override = true, Enabled = true,
                        Key = 0.18f, Compensation = 0.0f, AdaptationSpeed = 1.5f,
                        MinExposure = -8.0f, MaxExposure = 8.0f, ManualExposure = 0.0f
                    },
                    DoF = new DepthOfFieldOverride
                    {
                        Override = true, Enabled = true,
                        FocusDistance = 10.0f, Aperture = 2.8f, BokehShape = 0,
                        MaxBokehRadius = 24.0f, NearFocusStart = 2.0f, FarFocusStart = 20.0f
                    },
                    MotionBlur = new MotionBlurOverride
                    {
                        Override = true, Enabled = true, Strength = 16.0f, Samples = 16
                    },
                    Bloom = new BloomOverride
                    {
                        Override = true, Enabled = true, Threshold = 1.0f,
                        Intensity = 0.8f, Radius = 0.85f, Tint = new Vector3(1.0f, 0.95f, 0.9f)
                    },
                    LensFlare = new LensFlareOverride
                    {
                        Override = true, Enabled = true, Intensity = 0.5f, RayCount = 6
                    },
                    ChromaticAberration = new ChromaticAberrationOverride
                    {
                        Override = true, Enabled = true, Amount = 0.002f, RadialStart = 0.5f
                    },
                    Vignette = new VignetteOverride
                    {
                        Override = true, Enabled = true, Intensity = 0.5f,
                        Radius = 0.75f, Softness = 0.3f, Color = Vector3.Zero
                    },
                    FilmGrain = new FilmGrainOverride
                    {
                        Override = true, Enabled = true, Intensity = 0.05f, Size = 1.5f, Speed = 1.0f
                    },
                    Tonemapping = new TonemappingOverride
                    {
                        Override = true, Operator = 0, ApplySRGB = true
                    },
                    ColorGrading = new ColorGradingOverride
                    {
                        Override = true, Enabled = true, WhiteBalanceTemp = 0.0f,
                        WhiteBalanceTint = 0.0f, Saturation = 1.0f, Contrast = 1.0f,
                        Gain = 1.0f, ColorOffset = Vector3.Zero, LutIntensity = 1.0f,
                        HasLut = false, LutTexture = null
                    },
                    Ssao = new SsaoOverride
                    {
                        Override = true, Enabled = true, Quality = (int)SsaoQuality.Medium,
                        Radius = 0.5f, Intensity = 1.0f, Power = 2.0f
                    },
                    Ssr = new SsrOverride
                    {
                        Override = true, Enabled = true, Quality = (int)SsrQuality.Medium,
                        MaxRoughness = 0.6f, Intensity = 1.0f
                    },
                    OutputScale = new OutputScaleOverride { Override = true, Scale = 1.0f }
                };
            }
        }

        // ── 混合辅助 ────────────────────────────────────────────────────────

        /// <summary>单个体积对最终结果的贡献（设置 + 权重 + 优先级）。</summary>
        public readonly struct Contribution
        {
            public readonly PostProcessSettings Settings;
            public readonly float Weight;
            public readonly int Priority;
            public Contribution(PostProcessSettings settings, float weight, int priority)
            {
                Settings = settings; Weight = weight; Priority = priority;
            }
        }

        /// <summary>
        /// 标量混合：按优先级从高到低遍历覆盖该字段的贡献者，逐个“吃掉”剩余权重；
        /// 引擎默认作为最低优先级、权重 1 的兜底贡献者追加在末尾，从而实现
        /// “Override=false => 使用上一级”的语义。
        /// </summary>
        private static float BlendScalar(Contribution[] ordered,
            Func<PostProcessSettings, bool> overridden,
            Func<PostProcessSettings, float> getter)
        {
            float remaining = 1.0f;
            float result = 0.0f;
            for (int i = 0; i < ordered.Length; i++)
            {
                if (!overridden(ordered[i].Settings)) continue;
                float take = Math.Min(ordered[i].Weight, remaining);
                result += take * getter(ordered[i].Settings);
                remaining -= take;
                if (remaining <= 1e-5f) break;
            }
            return result;
        }

        /// <summary>三维向量混合（按分量调用 BlendScalar）。</summary>
        private static Vector3 BlendVector3(Contribution[] ordered,
            Func<PostProcessSettings, bool> overridden,
            Func<PostProcessSettings, Vector3> getter)
        {
            return new Vector3(
                BlendScalar(ordered, overridden, s => getter(s).X),
                BlendScalar(ordered, overridden, s => getter(s).Y),
                BlendScalar(ordered, overridden, s => getter(s).Z));
        }

        /// <summary>布尔混合（按 0/1 标量混合后以 0.5 为阈值）。</summary>
        private static bool BlendBool(Contribution[] ordered,
            Func<PostProcessSettings, bool> overridden,
            Func<PostProcessSettings, bool> getter)
        {
            return BlendScalar(ordered, overridden, s => getter(s) ? 1.0f : 0.0f) >= 0.5f;
        }

        /// <summary>枚举混合：取最高优先级且权重&gt;0.5 的覆盖者，否则取首个覆盖者。</summary>
        private static int BlendEnum(Contribution[] ordered,
            Func<PostProcessSettings, bool> overridden,
            Func<PostProcessSettings, int> getter)
        {
            for (int i = 0; i < ordered.Length; i++)
            {
                if (overridden(ordered[i].Settings) && ordered[i].Weight > 0.5f)
                    return getter(ordered[i].Settings);
            }
            for (int i = 0; i < ordered.Length; i++)
            {
                if (overridden(ordered[i].Settings))
                    return getter(ordered[i].Settings);
            }
            return 0;
        }

        /// <summary>引用类型混合（如 LUT 纹理）：取最高优先级且权重&gt;0.5 的覆盖者。</summary>
        private static T? BlendRef<T>(Contribution[] ordered,
            Func<PostProcessSettings, bool> overridden,
            Func<PostProcessSettings, T?> getter) where T : class
        {
            for (int i = 0; i < ordered.Length; i++)
            {
                if (overridden(ordered[i].Settings) && ordered[i].Weight > 0.5f)
                    return getter(ordered[i].Settings);
            }
            for (int i = 0; i < ordered.Length; i++)
            {
                if (overridden(ordered[i].Settings))
                    return getter(ordered[i].Settings);
            }
            return null;
        }

        /// <summary>
        /// 将一组激活体积的设置混合为最终的、完全具体的 PostProcessSettings
        /// （所有块 Override=true）。引擎默认作为兜底贡献者。
        /// </summary>
        public static PostProcessSettings Blend(List<Contribution> activeVolumes)
        {
            // 1. 按优先级降序排列激活体积
            var sorted = new List<Contribution>(activeVolumes);
            sorted.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            // 2. 末尾追加引擎默认（最低优先级、权重 1）作为兜底
            sorted.Add(new Contribution(EngineDefaults, 1.0f, int.MinValue));
            Contribution[] ordered = sorted.ToArray();

            var result = new PostProcessSettings();

            // —— 自动曝光 ——
            result.Exposure = new ExposureOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.Exposure.Override, s => s.Exposure.Enabled),
                Key = BlendScalar(ordered, s => s.Exposure.Override, s => s.Exposure.Key),
                Compensation = BlendScalar(ordered, s => s.Exposure.Override, s => s.Exposure.Compensation),
                AdaptationSpeed = BlendScalar(ordered, s => s.Exposure.Override, s => s.Exposure.AdaptationSpeed),
                MinExposure = BlendScalar(ordered, s => s.Exposure.Override, s => s.Exposure.MinExposure),
                MaxExposure = BlendScalar(ordered, s => s.Exposure.Override, s => s.Exposure.MaxExposure),
                ManualExposure = BlendScalar(ordered, s => s.Exposure.Override, s => s.Exposure.ManualExposure)
            };

            // —— 景深 ——
            result.DoF = new DepthOfFieldOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.DoF.Override, s => s.DoF.Enabled),
                FocusDistance = BlendScalar(ordered, s => s.DoF.Override, s => s.DoF.FocusDistance),
                Aperture = BlendScalar(ordered, s => s.DoF.Override, s => s.DoF.Aperture),
                BokehShape = BlendEnum(ordered, s => s.DoF.Override, s => s.DoF.BokehShape),
                MaxBokehRadius = BlendScalar(ordered, s => s.DoF.Override, s => s.DoF.MaxBokehRadius),
                NearFocusStart = BlendScalar(ordered, s => s.DoF.Override, s => s.DoF.NearFocusStart),
                FarFocusStart = BlendScalar(ordered, s => s.DoF.Override, s => s.DoF.FarFocusStart)
            };

            // —— 运动模糊 ——
            result.MotionBlur = new MotionBlurOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.MotionBlur.Override, s => s.MotionBlur.Enabled),
                Strength = BlendScalar(ordered, s => s.MotionBlur.Override, s => s.MotionBlur.Strength),
                Samples = (int)Math.Round(BlendScalar(ordered, s => s.MotionBlur.Override, s => s.MotionBlur.Samples))
            };

            // —— 泛光 ——
            result.Bloom = new BloomOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.Bloom.Override, s => s.Bloom.Enabled),
                Threshold = BlendScalar(ordered, s => s.Bloom.Override, s => s.Bloom.Threshold),
                Intensity = BlendScalar(ordered, s => s.Bloom.Override, s => s.Bloom.Intensity),
                Radius = BlendScalar(ordered, s => s.Bloom.Override, s => s.Bloom.Radius),
                Tint = BlendVector3(ordered, s => s.Bloom.Override, s => s.Bloom.Tint)
            };

            // —— 镜头光晕 ——
            result.LensFlare = new LensFlareOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.LensFlare.Override, s => s.LensFlare.Enabled),
                Intensity = BlendScalar(ordered, s => s.LensFlare.Override, s => s.LensFlare.Intensity),
                RayCount = (int)Math.Round(BlendScalar(ordered, s => s.LensFlare.Override, s => s.LensFlare.RayCount))
            };

            // —— 色差 ——
            result.ChromaticAberration = new ChromaticAberrationOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.ChromaticAberration.Override, s => s.ChromaticAberration.Enabled),
                Amount = BlendScalar(ordered, s => s.ChromaticAberration.Override, s => s.ChromaticAberration.Amount),
                RadialStart = BlendScalar(ordered, s => s.ChromaticAberration.Override, s => s.ChromaticAberration.RadialStart)
            };

            // —— 暗角 ——
            result.Vignette = new VignetteOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.Vignette.Override, s => s.Vignette.Enabled),
                Intensity = BlendScalar(ordered, s => s.Vignette.Override, s => s.Vignette.Intensity),
                Radius = BlendScalar(ordered, s => s.Vignette.Override, s => s.Vignette.Radius),
                Softness = BlendScalar(ordered, s => s.Vignette.Override, s => s.Vignette.Softness),
                Color = BlendVector3(ordered, s => s.Vignette.Override, s => s.Vignette.Color)
            };

            // —— 胶片颗粒 ——
            result.FilmGrain = new FilmGrainOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.FilmGrain.Override, s => s.FilmGrain.Enabled),
                Intensity = BlendScalar(ordered, s => s.FilmGrain.Override, s => s.FilmGrain.Intensity),
                Size = BlendScalar(ordered, s => s.FilmGrain.Override, s => s.FilmGrain.Size),
                Speed = BlendScalar(ordered, s => s.FilmGrain.Override, s => s.FilmGrain.Speed)
            };

            // —— 色调映射 ——
            result.Tonemapping = new TonemappingOverride
            {
                Override = true,
                Operator = BlendEnum(ordered, s => s.Tonemapping.Override, s => s.Tonemapping.Operator),
                ApplySRGB = BlendBool(ordered, s => s.Tonemapping.Override, s => s.Tonemapping.ApplySRGB)
            };

            // —— 颜色分级 ——
            result.ColorGrading = new ColorGradingOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.Enabled),
                WhiteBalanceTemp = BlendScalar(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.WhiteBalanceTemp),
                WhiteBalanceTint = BlendScalar(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.WhiteBalanceTint),
                Saturation = BlendScalar(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.Saturation),
                Contrast = BlendScalar(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.Contrast),
                Gain = BlendScalar(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.Gain),
                ColorOffset = BlendVector3(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.ColorOffset),
                LutIntensity = BlendScalar(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.LutIntensity),
                HasLut = BlendBool(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.HasLut),
                LutTexture = BlendRef(ordered, s => s.ColorGrading.Override, s => s.ColorGrading.LutTexture)
            };

            // —— SSAO ——
            result.Ssao = new SsaoOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.Ssao.Override, s => s.Ssao.Enabled),
                Quality = BlendEnum(ordered, s => s.Ssao.Override, s => s.Ssao.Quality),
                Radius = BlendScalar(ordered, s => s.Ssao.Override, s => s.Ssao.Radius),
                Intensity = BlendScalar(ordered, s => s.Ssao.Override, s => s.Ssao.Intensity),
                Power = BlendScalar(ordered, s => s.Ssao.Override, s => s.Ssao.Power)
            };

            // —— SSR ——
            result.Ssr = new SsrOverride
            {
                Override = true,
                Enabled = BlendBool(ordered, s => s.Ssr.Override, s => s.Ssr.Enabled),
                Quality = BlendEnum(ordered, s => s.Ssr.Override, s => s.Ssr.Quality),
                MaxRoughness = BlendScalar(ordered, s => s.Ssr.Override, s => s.Ssr.MaxRoughness),
                Intensity = BlendScalar(ordered, s => s.Ssr.Override, s => s.Ssr.Intensity)
            };

            // —— 输出缩放 ——
            result.OutputScale = new OutputScaleOverride
            {
                Override = true,
                Scale = BlendScalar(ordered, s => s.OutputScale.Override, s => s.OutputScale.Scale)
            };

            return result;
        }

        /// <summary>
        /// 将混合后的具体设置写回 PostProcessingStack.Settings（已有的后处理栈配置类）。
        /// SSAO/SSR 块不在 PostProcessingStack 中直接消费，留给对应的 SSAO/SSR Pass。
        /// </summary>
        public void ApplyTo(PostProcessingStack stack)
        {
            if (stack == null) return;
            var s = stack.Settings;

            // 自动曝光
            s.EnableAutoExposure = Exposure.Enabled;
            s.ExposureKey = Exposure.Key;
            s.ExposureCompensation = Exposure.Compensation;
            s.ExposureAdaptationSpeed = Exposure.AdaptationSpeed;
            s.MinExposure = Exposure.MinExposure;
            s.MaxExposure = Exposure.MaxExposure;
            s.ManualExposure = Exposure.ManualExposure;

            // 景深
            s.EnableDoF = DoF.Enabled;
            s.FocusDistance = DoF.FocusDistance;
            s.Aperture = DoF.Aperture;
            s.BokehShape = (BokehShape)DoF.BokehShape;
            s.MaxBokehRadius = DoF.MaxBokehRadius;
            s.NearFocusStart = DoF.NearFocusStart;
            s.FarFocusStart = DoF.FarFocusStart;

            // 运动模糊
            s.EnableMotionBlur = MotionBlur.Enabled;
            s.MotionBlurStrength = MotionBlur.Strength;
            s.MotionBlurSamples = MotionBlur.Samples;

            // 泛光
            s.EnableBloom = Bloom.Enabled;
            s.BloomThreshold = Bloom.Threshold;
            s.BloomIntensity = Bloom.Intensity;
            s.BloomRadius = Bloom.Radius;
            s.BloomTint = Bloom.Tint;

            // 镜头光晕
            s.EnableLensFlare = LensFlare.Enabled;
            s.LensFlareIntensity = LensFlare.Intensity;
            s.LensFlareRayCount = LensFlare.RayCount;

            // 色差
            s.EnableChromaticAberration = ChromaticAberration.Enabled;
            s.ChromaticAberrationAmount = ChromaticAberration.Amount;
            s.ChromaticAberrationRadialStart = ChromaticAberration.RadialStart;

            // 暗角
            s.EnableVignette = Vignette.Enabled;
            s.VignetteIntensity = Vignette.Intensity;
            s.VignetteRadius = Vignette.Radius;
            s.VignetteSoftness = Vignette.Softness;
            s.VignetteColor = Vignette.Color;

            // 胶片颗粒
            s.EnableFilmGrain = FilmGrain.Enabled;
            s.FilmGrainIntensity = FilmGrain.Intensity;
            s.FilmGrainSize = FilmGrain.Size;
            s.FilmGrainSpeed = FilmGrain.Speed;

            // 色调映射
            s.TonemapOperator = (TonemapOperator)Tonemapping.Operator;
            s.ApplySRGBCurve = Tonemapping.ApplySRGB;

            // 颜色分级
            s.EnableColorGrading = ColorGrading.Enabled;
            s.WhiteBalanceTemp = ColorGrading.WhiteBalanceTemp;
            s.WhiteBalanceTint = ColorGrading.WhiteBalanceTint;
            s.Saturation = ColorGrading.Saturation;
            s.Contrast = ColorGrading.Contrast;
            s.Gain = ColorGrading.Gain;
            s.ColorOffset = ColorGrading.ColorOffset;
            s.LutIntensity = ColorGrading.LutIntensity;
            s.LutTexture = ColorGrading.HasLut ? ColorGrading.LutTexture : null;

            // 输出缩放
            s.OutputScale = OutputScale.Scale;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 后处理体积
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 后处理体积：在世界空间中描述一个区域，摄像机进入该区域时按权重混入其
    /// 后处理设置。支持球体/盒体/胶囊/凸包四种形状，以及无限范围（全局后处理）。
    /// </summary>
    public sealed class PostProcessVolume
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>体积名称（便于调试）。</summary>
        public string Name { get; set; } = "PostProcessVolume";

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>体积形状。</summary>
        public VolumeShape Shape { get; set; } = VolumeShape.Box;

        /// <summary>无限范围：为 true 时该体积全局生效（权重恒为 BlendWeight），忽略形状。</summary>
        public bool IsInfiniteRange { get; set; } = false;

        /// <summary>世界空间中心。</summary>
        public Vector3 Center { get; set; } = Vector3.Zero;

        /// <summary>朝向（盒体/胶囊用）。</summary>
        public Quaternion Orientation { get; set; } = Quaternion.Identity;

        /// <summary>球体/胶囊半径。</summary>
        public float Radius { get; set; } = 5.0f;

        /// <summary>盒体半尺寸。</summary>
        public Vector3 HalfExtents { get; set; } = new(5.0f, 5.0f, 5.0f);

        /// <summary>胶囊高度（含两端半球）。</summary>
        public float Height { get; set; } = 10.0f;

        /// <summary>凸包平面列表（法线.xyz + 距离.w，平面方程 dot(N,P) + w &gt;= 0 为内侧）。</summary>
        public List<Vector4> ConvexPlanes { get; set; } = new();

        /// <summary>混合距离：从边界向内/向外过渡的距离，权重在此范围内 smoothstep。</summary>
        public float BlendDistance { get; set; } = 1.5f;

        /// <summary>该体积的最大影响权重（0~1）。</summary>
        public float BlendWeight { get; set; } = 1.0f;

        /// <summary>优先级：数值越大越优先覆盖。</summary>
        public int Priority { get; set; } = 0;

        /// <summary>混合模式（预留扩展）。</summary>
        public VolumeBlendMode BlendMode { get; set; } = VolumeBlendMode.Blend;

        /// <summary>该体积的后处理设置（仅 Override=true 的块生效）。</summary>
        public PostProcessSettings Settings { get; set; } = new();

        /// <summary>把点变换到体积局部空间（用朝向的逆）。</summary>
        private Vector3 WorldToLocal(Vector3 p)
        {
            Vector3 d = p - Center;
            Quaternion inv = Quaternion.Inverse(Quaternion.Normalize(Orientation));
            return Vector3.Transform(d, inv);
        }

        /// <summary>smoothstep 平滑过渡函数。</summary>
        private static float Smoothstep(float t)
        {
            t = Math.Clamp(t, 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        /// <summary>
        /// 计算给定世界空间点在该体积中的影响权重（0~BlendWeight）。
        /// 权重基于点到体积边界的距离在 BlendDistance 内做 smoothstep 过渡。
        /// </summary>
        public float ComputeWeight(Vector3 point)
        {
            if (!IsEnabled) return 0.0f;

            // 无限范围：全局生效
            if (IsInfiniteRange) return Math.Clamp(BlendWeight, 0.0f, 1.0f);

            float bd = Math.Max(BlendDistance, 1e-5f);
            float depth; // 点到边界的“内侧深度”：>0 在内部、&lt;0 在外部

            switch (Shape)
            {
                case VolumeShape.Sphere:
                {
                    float d = Vector3.Distance(point, Center);
                    depth = Radius - d; // 在球内为正
                    break;
                }
                case VolumeShape.Box:
                {
                    Vector3 lp = WorldToLocal(point);
                    Vector3 q = Vector3.Abs(lp) - HalfExtents;
                    bool outside = q.X > 0 || q.Y > 0 || q.Z > 0;
                    if (outside)
                    {
                        Vector3 outsideClamped = Vector3.Max(q, Vector3.Zero);
                        depth = -outsideClamped.Length(); // 外侧距离取负
                    }
                    else
                    {
                        // 内侧：到最近面的距离 = min(HalfExtents - |lp|)
                        Vector3 inner = HalfExtents - Vector3.Abs(lp);
                        depth = Math.Min(Math.Min(inner.X, inner.Y), inner.Z);
                    }
                    break;
                }
                case VolumeShape.Capsule:
                {
                    // 胶囊：沿局部 Y 轴的线段，长度 = Height - 2*Radius
                    Vector3 lp = WorldToLocal(point);
                    float halfLen = Math.Max(0.0f, (Height - 2.0f * Radius) * 0.5f);
                    float cy = Math.Clamp(lp.Y, -halfLen, halfLen);
                    Vector3 closest = new(0.0f, cy, 0.0f);
                    float d = Vector3.Distance(lp, closest);
                    depth = Radius - d; // 内侧为正
                    break;
                }
                case VolumeShape.Convex:
                {
                    // 凸包：到所有平面内侧的最小有符号距离
                    if (ConvexPlanes == null || ConvexPlanes.Count == 0) return 0.0f;
                    Vector3 lp = WorldToLocal(point);
                    float minDist = float.MaxValue;
                    bool inside = true;
                    foreach (var plane in ConvexPlanes)
                    {
                        var n = new Vector3(plane.X, plane.Y, plane.Z);
                        float signed = Vector3.Dot(n, lp) + plane.W; // >=0 内侧
                        if (signed < minDist) minDist = signed;
                        if (signed < 0.0f) inside = false;
                    }
                    depth = inside ? minDist : -Math.Abs(minDist);
                    break;
                }
                default:
                    return 0.0f;
            }

            // depth >= BlendDistance => 权重满；depth <= 0 => 权重 0
            float t = depth / bd;
            return Smoothstep(t) * Math.Clamp(BlendWeight, 0.0f, 1.0f);
        }

        /// <summary>点是否在体积内部（无限范围视为内部）。</summary>
        public bool Contains(Vector3 point)
        {
            if (IsInfiniteRange) return true;
            return ComputeWeight(point) > 0.0f;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 体积栈：管理当前帧所有激活体积，并执行混合
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 体积栈：每帧由 VolumeBlendSystem 填充当前帧所有激活体积（设置+权重+优先级），
    /// 并提供混合接口输出最终后处理参数。
    /// </summary>
    public sealed class VolumeStack
    {
        private readonly List<PostProcessSettings.Contribution> _active = new();

        /// <summary>当前帧激活体积数量。</summary>
        public int ActiveCount => _active.Count;

        /// <summary>清空当前帧激活列表（每帧开始时调用）。</summary>
        public void Clear() => _active.Clear();

        /// <summary>注册一个激活体积的贡献。</summary>
        public void Add(PostProcessVolume volume, float weight)
        {
            if (volume == null || weight <= 0.0f) return;
            _active.Add(new PostProcessSettings.Contribution(
                volume.Settings, Math.Clamp(weight, 0.0f, 1.0f), volume.Priority));
        }

        /// <summary>注册一个裸贡献（用于代码直接注入）。</summary>
        public void Add(PostProcessSettings settings, float weight, int priority)
        {
            if (weight <= 0.0f) return;
            _active.Add(new PostProcessSettings.Contribution(settings, Math.Clamp(weight, 0.0f, 1.0f), priority));
        }

        /// <summary>获取当前帧激活贡献的只读快照。</summary>
        public IReadOnlyList<PostProcessSettings.Contribution> Active => _active;

        /// <summary>混合当前帧所有激活体积，输出最终后处理设置。</summary>
        public PostProcessSettings Blend() => PostProcessSettings.Blend(_active);

        /// <summary>混合并直接写回 PostProcessingStack。</summary>
        public void ApplyTo(PostProcessingStack stack) => Blend().ApplyTo(stack);
    }

    // ════════════════════════════════════════════════════════════════════════
    // ECS 组件与系统
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 后处理体积组件（ECS）。挂载到实体上即可让该实体成为一个后处理体积；
    /// 实体的 TransformComponent 提供体积的中心与朝向（可由 SyncFromTransform 同步）。
    /// </summary>
    public struct PostProcessVolumeComponent : IComponent
    {
        /// <summary>该组件引用的后处理体积实例。</summary>
        public PostProcessVolume? Volume;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>权重缩放（乘到计算所得权重上）。</summary>
        public float WeightScale;

        /// <summary>是否每帧从实体 TransformComponent 同步体积的中心与朝向。</summary>
        public bool SyncFromTransform;
    }

    /// <summary>
    /// 体积混合系统（ECS ISystem）。每帧：
    ///   1. 确定摄像机位置（外部注入或查询主摄像机实体）；
    ///   2. 收集所有激活体积（实体挂载的 + 代码注册的独立体积）；
    ///   3. 计算每个体积在摄像机处的权重；
    ///   4. 按优先级与权重混合，写回 PostProcessingStack.Settings。
    /// </summary>
    public sealed class VolumeBlendSystem : ISystem
    {
        private readonly PostProcessingStack _stack;
        private readonly VolumeStack _frameStack = new();
        private readonly List<PostProcessVolume> _standaloneVolumes = new();

        private Vector3 _cameraPosition;
        private bool _cameraPositionValid;

        /// <summary>系统名称。</summary>
        public string Name => "VolumeBlendSystem";

        /// <summary>当前帧使用的体积栈（调试/可视化用）。</summary>
        public VolumeStack FrameStack => _frameStack;

        /// <summary>构造：绑定要驱动的后处理栈。</summary>
        public VolumeBlendSystem(PostProcessingStack stack)
        {
            _stack = stack ?? throw new ArgumentNullException(nameof(stack));
        }

        /// <summary>添加一个不依赖实体的独立体积（代码驱动）。</summary>
        public void AddStandaloneVolume(PostProcessVolume volume)
        {
            if (volume != null && !_standaloneVolumes.Contains(volume))
                _standaloneVolumes.Add(volume);
        }

        /// <summary>移除独立体积。</summary>
        public bool RemoveStandaloneVolume(PostProcessVolume volume) => _standaloneVolumes.Remove(volume);

        /// <summary>外部注入当前摄像机世界位置（优先于自动查询）。</summary>
        public void SetCameraPosition(Vector3 position)
        {
            _cameraPosition = position;
            _cameraPositionValid = true;
        }

        /// <summary>清除外部注入的摄像机位置，改用自动查询。</summary>
        public void ClearCameraPosition() => _cameraPositionValid = false;

        /// <summary>每帧更新：收集、混合、写回。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;

            // 1. 确定摄像机位置
            Vector3 camPos = _cameraPosition;
            if (!_cameraPositionValid)
            {
                camPos = ResolveMainCameraPosition(ecs);
            }

            // 2. 清空并重建当前帧激活列表
            _frameStack.Clear();

            // 2a. 独立体积
            foreach (var vol in _standaloneVolumes)
            {
                if (vol == null || !vol.IsEnabled) continue;
                float w = vol.ComputeWeight(camPos);
                if (w > 0.0f) _frameStack.Add(vol, w);
            }

            // 2b. 实体挂载的体积
            var entities = ecs.QueryWithComponents<TransformComponent, PostProcessVolumeComponent>();
            foreach (var entityId in entities)
            {
                var comp = ecs.GetComponent<PostProcessVolumeComponent>(entityId);
                if (!comp.Enabled || comp.Volume == null) continue;
                var vol = comp.Volume;
                if (!vol.IsEnabled) continue;

                // 从实体变换同步中心/朝向
                if (comp.SyncFromTransform)
                {
                    var tf = ecs.GetComponent<TransformComponent>(entityId);
                    vol.Center = tf.Position;
                    vol.Orientation = tf.Rotation;
                    // 可选：用缩放放大盒体半尺寸（仅当形状为 Box 时）
                    if (vol.Shape == VolumeShape.Box)
                    {
                        vol.HalfExtents = new Vector3(
                            Math.Abs(tf.Scale.X) * vol.HalfExtents.X,
                            Math.Abs(tf.Scale.Y) * vol.HalfExtents.Y,
                            Math.Abs(tf.Scale.Z) * vol.HalfExtents.Z);
                    }
                }

                float w = vol.ComputeWeight(camPos) * Math.Clamp(comp.WeightScale, 0.0f, 1.0f);
                if (w > 0.0f) _frameStack.Add(vol, w);
            }

            // 3. 混合并写回后处理栈
            _frameStack.ApplyTo(_stack);
        }

        /// <summary>查询主摄像机实体的世界位置。</summary>
        private static Vector3 ResolveMainCameraPosition(EntityComponentSystem ecs)
        {
            var cams = ecs.QueryWithComponents<TransformComponent, CameraComponent>();
            foreach (var id in cams)
            {
                var cam = ecs.GetComponent<CameraComponent>(id);
                if (cam.IsMain)
                {
                    return ecs.GetComponent<TransformComponent>(id).Position;
                }
            }
            // 没有主摄像机：取第一个摄像机，否则原点
            foreach (var id in cams)
            {
                return ecs.GetComponent<TransformComponent>(id).Position;
            }
            return Vector3.Zero;
        }
    }
}
