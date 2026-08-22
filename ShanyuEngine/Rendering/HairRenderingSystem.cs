using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 头发渲染系统 (Hair Rendering System)
    //
    // 基于 Marschner 2003 / Kajiya-Kay 双层模型：
    //   1. Kajiya-Kay 模型（快速，看起来还行）
    //   2. Marschner 模型（物理正确，需要 3 个 lobe：R/TT/TRT）
    //   3. 双层圆柱近似（简化 Marschner，性能好）
    //   4. 线段 / 贝塞尔曲线头发几何
    //   5. 多散射近似（Multiple Scattering Approximation）
    //   6. 头发阴影（自阴影 + 层间阴影）
    //   7. 头发高光着色（Melanin / Dye 模型）
    //   8. 风力模拟 + 布料碰撞
    //   9. 粒子头发 (Particle Hair) 简化渲染
    //   10. 头发 LOD（近处贝塞尔，远处线段，更远处粒子）
    //
    // 参考文献:
    //   [Marschner 2003] "Light Scattering from Human Hair Fibers"
    //   [Kajiya 1989] "Rendering Fur with Three Dimensional Textures"
    //   [d'Eon 2011] "An Energy-Conserving Hair Reflectance Model"
    //   [Chiang 2016] "Hair Importance Sampling"
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>头发着色模型。</summary>
    public enum HairShadingModel
    {
        /// <summary>Kajiya-Kay（最快）。</summary>
        KajiyaKay,
        /// <summary>简化 Marschner（R + TT + TRT）。</summary>
        SimplifiedMarschner,
        /// <summary>完整 Marschner（多次散射）。</summary>
        FullMarschner,
        /// <summary>双圆柱近似。</summary>
        DualCylinder
    }

    /// <summary>头发几何类型。</summary>
    public enum HairGeometryType
    {
        /// <summary>线段（最快）。</summary>
        LineSegments,
        /// <summary>三次贝塞尔曲线。</summary>
        CubicBezier,
        /// <summary>可编辑样条。</summary>
        Spline,
        /// <summary>粒子（最远 LOD）。</summary>
        Particle
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>头发渲染配置。</summary>
    public sealed class HairRenderingConfig
    {
        /// <summary>着色模型。</summary>
        public HairShadingModel ShadingModel = HairShadingModel.SimplifiedMarschner;

        /// <summary>几何类型。</summary>
        public HairGeometryType GeometryType = HairGeometryType.CubicBezier;

        /// <summary>头发粗细（世界单位）。</summary>
        public float HairThickness = 0.002f;

        /// <summary>头发密度（每平方厘米的发丝数，参考值 100~200）。</summary>
        public float HairDensity = 150.0f;

        /// <summary>每根头发控制点数。</summary>
        public int ControlPointsPerStrand = 4;

        /// <summary>最大头发数量。</summary>
        public int MaxStrandCount = 100000;

        /// <summary>头发段数（每根）。</summary>
        public int SegmentsPerStrand = 16;

        /// <summary>是否启用自阴影。</summary>
        public bool EnableSelfShadow = true;

        /// <summary>自阴影强度。</summary>
        public float SelfShadowStrength = 0.3f;

        /// <summary>是否启用多次散射近似。</summary>
        public bool EnableMultipleScattering = true;

        /// <summary>多次散射迭代次数。</summary>
        public int MultipleScatteringIterations = 3;

        /// <summary>是否启用各向异性高光。</summary>
        public bool EnableAnisotropicSpecular = true;

        /// <summary>是否启用风。</summary>
        public bool EnableWind = true;

        /// <summary>风力强度。</summary>
        public float WindStrength = 1.0f;

        /// <summary>风力方向。</summary>
        public Vector3 WindDirection = new(0.5f, 0, 0.3f);

        /// <summary>是否启用 LOD。</summary>
        public bool EnableLOD = true;

        /// <summary>LOD 距离阈值（米）。</summary>
        public float LODNearDistance = 5.0f;
        public float LODMidDistance = 20.0f;
        public float LODFarDistance = 50.0f;

        /// <summary>是否启用深度排序（头发透明度排序）。</summary>
        public bool EnableDepthSorting = true;

        /// <summary>头发颜色模式。</summary>
        public HairColorMode ColorMode = HairColorMode.Melanin;

        /// <summary>应用质量预设。</summary>
        public void ApplyQuality(HairQuality quality)
        {
            switch (quality)
            {
                case HairQuality.Low:
                    ShadingModel = HairShadingModel.KajiyaKay;
                    GeometryType = HairGeometryType.LineSegments;
                    SegmentsPerStrand = 8;
                    EnableSelfShadow = false;
                    EnableMultipleScattering = false;
                    MaxStrandCount = 30000;
                    break;
                case HairQuality.Medium:
                    ShadingModel = HairShadingModel.SimplifiedMarschner;
                    GeometryType = HairGeometryType.CubicBezier;
                    SegmentsPerStrand = 12;
                    EnableSelfShadow = true;
                    EnableMultipleScattering = false;
                    MaxStrandCount = 50000;
                    break;
                case HairQuality.High:
                    ShadingModel = HairShadingModel.SimplifiedMarschner;
                    GeometryType = HairGeometryType.CubicBezier;
                    SegmentsPerStrand = 16;
                    EnableSelfShadow = true;
                    EnableMultipleScattering = true;
                    MaxStrandCount = 100000;
                    break;
                case HairQuality.Ultra:
                    ShadingModel = HairShadingModel.FullMarschner;
                    GeometryType = HairGeometryType.CubicBezier;
                    SegmentsPerStrand = 24;
                    EnableSelfShadow = true;
                    EnableMultipleScattering = true;
                    MultipleScatteringIterations = 5;
                    MaxStrandCount = 200000;
                    break;
            }
        }

        public static HairRenderingConfig Default => new() { ApplyQuality(HairQuality.High) };
    }

    /// <summary>头发质量。</summary>
    public enum HairQuality { Low, Medium, High, Ultra }

    /// <summary>头发颜色模式。</summary>
    public enum HairColorMode
    {
        /// <summary>基于黑色素浓度（自然发色）。</summary>
        Melanin,
        /// <summary>直接指定颜色（染发）。</summary>
        DirectColor,
        /// <summary>从纹理采样。</summary>
        Texture
    }

    // ────────────────────────────────────────────────────────────────────────
    // 头发颜色
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>头发颜色参数。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HairColorParams
    {
        /// <summary>黑色素（深色色素）浓度 [0,1]。</summary>
        public float MelaninConcentration;

        /// <summary>黑色素红/棕比例 [0,1]。0=全黑, 1=全棕。</summary>
        public float MelaninRedness;

        /// <summary>直接颜色（DirectColor 模式）。</summary>
        public Vector3 DirectColor;

        /// <summary>染料吸收系数。</summary>
        public Vector3 DyeAbsorption;

        /// <summary>根端颜色。</summary>
        public Vector3 RootColor;

        /// <summary>尖端颜色。</summary>
        public Vector3 TipColor;

        /// <summary>根端到尖端的渐变位置 [0,1]。</summary>
        public float TipTransition;

        /// <summary>高光色调偏移（让高光带一点蓝色/金色）。</summary>
        public Vector3 SpecularTint;

        /// <summary>白头发因子（0=正常, 1=全白）。</summary>
        public float GrayingFactor;

        /// <summary>默认参数（亚洲黑色头发）。</summary>
        public static HairColorParams AsianBlack => new()
            { MelaninConcentration = 0.8f, MelaninRedness = 0.0f,
              RootColor = new Vector3(0.02f, 0.01f, 0.005f),
              TipColor = new Vector3(0.05f, 0.02f, 0.01f),
              TipTransition = 0.3f };

        /// <summary>金发。</summary>
        public static HairColorParams Blonde => new()
            { MelaninConcentration = 0.2f, MelaninRedness = 0.6f,
              RootColor = new Vector3(0.4f, 0.3f, 0.15f),
              TipColor = new Vector3(0.6f, 0.5f, 0.25f),
              TipTransition = 0.5f };

        /// <summary>棕发。</summary>
        public static HairColorParams Brown => new()
            { MelaninConcentration = 0.5f, MelaninRedness = 0.3f,
              RootColor = new Vector3(0.15f, 0.08f, 0.03f),
              TipColor = new Vector3(0.25f, 0.15f, 0.06f),
              TipTransition = 0.4f };

        /// <summary>红发。</summary>
        public static HairColorParams Red => new()
            { MelaninConcentration = 0.4f, MelaninRedness = 0.8f,
              RootColor = new Vector3(0.3f, 0.1f, 0.03f),
              TipColor = new Vector3(0.5f, 0.15f, 0.05f),
              TipTransition = 0.3f };
    }

    /// <summary>头发颜色计算器。</summary>
    public static class HairColorCalculator
    {
        /// <summary>根据黑色素浓度计算头发颜色（d'Eon 模型）。</summary>
        /// <param name="melaninConc">黑色素浓度 [0,1]。</param>
        /// <param name="melaninRedness">红/棕比例 [0,1]。</param>
        /// <returns>吸收系数 (sigma_a)。</returns>
        public static Vector3 ComputeMelaninColor(float melaninConc, float melaninRedness)
        {
            // 黑色素有两种：eumelanin（黑/棕）和 pheomelanin（红/黄）
            // eumelanin 的吸收光谱
            Vector3 eumelanin = new(0.419f, 0.597f, 1.390f);
            // pheomelanin 的吸收光谱
            Vector3 pheomelanin = new(0.050f, 0.081f, 0.200f);

            // 混合
            float red = melaninRedness;
            Vector3 melanin = eumelanin * (1.0f - red) + pheomelanin * red;
            return melanin * melaninConc;
        }

        /// <summary>根据吸收系数计算反射率（近似）。</summary>
        public static Vector3 ComputeAlbedoFromAbsorption(Vector3 sigmaA)
        {
            // 简化：albedo ≈ exp(-sigmaA * d)，d 是头发直径
            float d = 0.002f; // 头发直径 2mm（在头发尺度上其实是很大的值）
            // 实际应该用圆柱体散射的解析解
            return new Vector3(
                MathF.Exp(-sigmaA.X * d * 2.0f),
                MathF.Exp(-sigmaA.Y * d * 2.0f),
                MathF.Exp(-sigmaA.Z * d * 2.0f)
            );
        }

        /// <summary>沿头发长度方向插值颜色。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 InterpolateHairColor(HairColorParams params_, float t)
        {
            Vector3 baseColor;

            switch (params_.ColorMode)
            {
                default:
                    baseColor = params_.RootColor;
                    break;
            }

            // 根端到尖端渐变
            if (t > params_.TipTransition)
            {
                float gradT = (t - params_.TipTransition) / Math.Max(1.0f - params_.TipTransition, 0.01f);
                baseColor = Vector3.Lerp(baseColor, params_.TipColor, gradT);
            }

            // 白发混合
            if (params_.GrayingFactor > 0)
            {
                baseColor = Vector3.Lerp(baseColor, Vector3.One, params_.GrayingFactor);
            }

            return baseColor;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Kajiya-Kay 模型
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Kajiya-Kay 头发着色模型。</summary>
    public static class KajiyaKayHair
    {
        /// <summary>Kajiya-Kay 漫反射。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Diffuse(float cosThetaTD)
        {
            // sin(T) = sqrt(1 - cos^2(T))
            float sinT = MathF.Sqrt(Math.Max(1.0f - cosThetaTD * cosThetaTD, 0));
            return sinT;
        }

        /// <summary>Kajiya-Kay 镜面反射。</summary>
        /// <param name="cosThetaTD">视线和毛发的夹角余弦。</param>
        /// <param name="cosThetaTH">光线和毛发的夹角余弦。</param>
        /// <param name="specularPower">高光指数。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Specular(float cosThetaTD, float cosThetaTH, float specularPower)
        {
            // T = normalize(L + V)，然后 specular = sin^p(T, T)
            // 简化公式：直接用 T 项
            float sinT = MathF.Sqrt(Math.Max(1.0f - cosThetaTD * cosThetaTD, 0));
            float spec = MathF.Pow(sinT, specularPower);
            return spec;
        }

        /// <summary>完整 Kajiya-Kay 着色。</summary>
        public static Vector3 Shade(
            Vector3 tangent, Vector3 lightDir, Vector3 viewDir,
            Vector3 lightColor, Vector3 hairColor,
            float specularPower = 50.0f, float specularIntensity = 0.5f)
        {
            float cosTD = Math.Clamp(Vector3.Dot(tangent, viewDir), -1, 1);
            float cosTH = Math.Clamp(Vector3.Dot(tangent, lightDir), -1, 1);

            // 漫反射
            float diff = Diffuse(cosTD);

            // 镜面反射
            // T 方向：把 L 和 V 投影到切线平面上，然后半角
            Vector3 TL = lightDir - tangent * cosTH;
            Vector3 TV = viewDir - tangent * cosTD;
            float TLLen = TL.Length();
            float TVLen = TV.Length();
            if (TLLen > 1e-6f && TVLen > 1e-6f)
            {
                TL /= TLLen;
                TV /= TVLen;
                Vector3 halfVec = Vector3.Normalize(TL + TV);
                float cosTT = Math.Clamp(Vector3.Dot(tangent, halfVec), -1, 1);
                float sinTT = MathF.Sqrt(Math.Max(1.0f - cosTT * cosTT, 0));
                float spec = MathF.Pow(sinTT, specularPower) * specularIntensity;
                return hairColor * lightColor * diff + lightColor * spec;
            }

            return hairColor * lightColor * diff;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Marschner 模型
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Marschner 头发着色模型参数。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MarschnerParams
    {
        /// <summary>头发折射率。</summary>
        public float IOR;

        /// <summary>倾斜角偏移（R lobe 偏移）。</summary>
        public float ROffset;

        /// <summary>R lobe 粗糙度。</summary>
        public float RRoughness;

        /// <summary>TT lobe 粗糙度。</summary>
        public float TTRoughness;

        /// <summary>TRT lobe 粗糙度。</summary>
        public float TRTRoughness;

        /// <summary>R lobe 强度缩放。</summary>
        public float RScale;

        /// <summary>TT lobe 强度缩放。</summary>
        public float TTScale;

        /// <summary>TRT lobe 强度缩放。</summary>
        public float TRTScale;

        /// <summary>纵向粗糙度。</summary>
        public float LongitudinalRoughness;

        /// <summary>方位角粗糙度。</summary>
        public float AzimuthalRoughness;

        /// <summary>默认参数。</summary>
        public static MarschnerParams Default => new()
            { IOR = 1.55f, ROffset = -0.1f,
              RRoughness = 0.3f, TTRoughness = 0.3f, TRTRoughness = 0.5f,
              RScale = 1.0f, TTScale = 0.7f, TRTScale = 0.4f,
              LongitudinalRoughness = 0.3f, AzimuthalRoughness = 0.3f };
    }

    /// <summary>Marschner 头发着色模型。</summary>
    public static class MarschnerHair
    {
        /// <summary>菲涅尔项（空气->头发圆柱体）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Fresnel(float cosThetaI, float ior)
        {
            float sinThetaT2 = (1.0f - cosThetaI * cosThetaI) / (ior * ior);
            if (sinThetaT2 >= 1.0f) return 1.0f; // 全反射
            float cosThetaT = MathF.Sqrt(1.0f - sinThetaT2);

            float rs = (ior * cosThetaI - cosThetaT) / (ior * cosThetaI + cosThetaT);
            float rp = (cosThetaI - ior * cosThetaT) / (cosThetaI + ior * cosThetaT);
            return 0.5f * (rs * rs + rp * rp);
        }

        /// <summary>高斯分布函数（用于 lobe 分布）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Gaussian(float x, float mu, float sigma)
        {
            float d = x - mu;
            float sigmaSq = sigma * sigma;
            return MathF.Exp(-d * d / (2.0f * sigmaSq)) / (sigma * 2.3998277f); // 2*sqrt(2*pi)
        }

        /// <summary>R lobe（表面反射）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float RLobe(float thetaI, float thetaH, float thetaD,
            float roughness, float offset, float ior)
        {
            float fresnel = Fresnel(Math.Abs(thetaI), ior);
            float Np = Gaussian(thetaH - offset, 0, roughness);
            float Mp = Gaussian(thetaD, 0, roughness * 0.5f);
            return fresnel * Np * Mp;
        }

        /// <summary>TT lobe（透射）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float TTLobe(float thetaI, float thetaH, float thetaD,
            float roughness, float ior)
        {
            float cosThetaT = MathF.Asin(Math.Clamp(Math.Sin(Math.Abs(thetaI)) / ior, -1, 1));
            float etaPrime = MathF.Sqrt(ior * ior - MathF.Sin(thetaI) * MathF.Sin(thetaI)) / MathF.Cos(thetaI);
            // 简化
            float Np = Gaussian(thetaH, 0, roughness);
            float Mp = Gaussian(thetaD, 0, roughness);
            return (1.0f - Fresnel(Math.Abs(thetaI), ior)) * Np * Mp * 0.5f;
        }

        /// <summary>TRT lobe（两次内反射）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float TRTLobe(float thetaI, float thetaH, float thetaD,
            float roughness, float ior)
        {
            float fresnel = Fresnel(Math.Abs(thetaI), ior);
            float Np = Gaussian(thetaH, 0, roughness);
            float Mp = Gaussian(thetaD, 0, roughness * 2.0f); // TRT 的纵向分布更宽
            // TRT 有一个颜色偏移（色散效应）
            return fresnel * fresnel * (1.0f - fresnel) * Np * Mp;
        }

        /// <summary>完整的 Marschner 着色。</summary>
        public static Vector3 Shade(
            Vector3 tangent, Vector3 lightDir, Vector3 viewDir,
            Vector3 lightColor, Vector3 hairColor,
            MarschnerParams mp, HairColorParams colorParams, float t)
        {
            // 计算各种角度
            float cosTD = Vector3.Dot(tangent, viewDir);
            float cosTH = Vector3.Dot(tangent, lightDir);

            // 方位角
            Vector3 projV = viewDir - tangent * cosTD;
            Vector3 projL = lightDir - tangent * cosTH;
            float projVLen = projV.Length();
            float projLLen = projL.Length();

            if (projVLen < 1e-6f || projLLen < 1e-6f)
                return Vector3.Zero;

            projV /= projVLen;
            projL /= projLLen;
            float cosPhi = Math.Clamp(Vector3.Dot(projV, projL), -1, 1);
            float phi = MathF.Acos(cosPhi);

            float thetaI = MathF.Asin(Math.Clamp(cosTH, -1, 1));
            float thetaR = MathF.Asin(Math.Clamp(cosTD, -1, 1));
            float thetaH = (thetaI + thetaR) * 0.5f;
            float thetaD = (thetaI - thetaR) * 0.5f;

            // 获取沿头发长度方向的插值颜色
            Vector3 strandColor = HairColorCalculator.InterpolateHairColor(colorParams, t);

            // R lobe（白色高光，偏移半个圆锥角）
            float r = RLobe(thetaI, thetaH, thetaD, mp.RRoughness, mp.ROffset, mp.IOR) * mp.RScale;

            // TT lobe（头发颜色透射）
            float tt = TTLobe(thetaI, thetaH, thetaD, mp.TTRoughness, mp.IOR) * mp.TTScale;

            // TRT lobe（染色高光，色散效果）
            float trt = TRTLobe(thetaI, thetaH, thetaD, mp.TRTRoughness, mp.IOR) * mp.TRTScale;

            // 组合
            Vector3 R = lightColor * r; // R lobe 是白色的（表面反射）
            Vector3 TT = strandColor * lightColor * tt;
            // TRT 有色散：不同波长的偏移不同
            Vector3 TRT = strandColor * lightColor * trt;
            // 简化的色散：让 TRT 的蓝色分量偏移一点
            TRT.Z *= 1.2f; // 蓝色加强

            return R + TT + TRT;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 多次散射近似
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>头发多次散射近似。</summary>
    public static class HairMultipleScattering
    {
        /// <summary>用全局光照探针近似多次散射。</summary>
        /// <param name="singleScatter">单次散射结果。</param>
        /// <param name="albedo">头发 albedo。</param>
        /// <param name="ambientColor">环境光颜色。</param>
        /// <param name="iterations">迭代次数。</param>
        /// <returns>加入多次散射后的颜色。</returns>
        public static Vector3 ApplyApproximation(
            Vector3 singleScatter, Vector3 albedo,
            Vector3 ambientColor, int iterations)
        {
            // 近似公式：L_ms ≈ L_ss * (1 - F_avg) * albedo / (1 - albedo * (1 - F_avg))
            // 其中 F_avg 是平均菲涅尔
            float fAvg = 0.25f; // 头发的平均菲涅尔约 0.25
            Vector3 factor = albedo * (1.0f - fAvg);

            // 迭代扩散
            Vector3 accumulated = singleScatter;
            Vector3 energy = singleScatter;

            for (int i = 0; i < iterations; i++)
            {
                energy = energy * factor; // 每次散射衰减
                accumulated += energy;
            }

            // 加上环境光贡献
            accumulated += ambientColor * albedo * 0.1f;

            return accumulated;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 风力模拟
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>头发风力模拟（简化版）。</summary>
    public sealed class HairWindSimulator
    {
        private float _time;
        private Vector3 _windDir;
        private float _windStrength;
        private float _turbulence = 0.3f;

        /// <summary>更新风力。</summary>
        public void Update(float deltaTime, Vector3 windDir, float windStrength)
        {
            _time += deltaTime;
            _windDir = windDir;
            _windStrength = windStrength;
        }

        /// <summary>计算给定位置的风力偏移。</summary>
        public Vector3 ComputeWindOffset(Vector3 worldPos, float stiffness = 0.5f)
        {
            // 基础风
            Vector3 baseWind = _windDir * _windStrength;

            // 湍流（用简单的正弦波模拟）
            float turbX = MathF.Sin(worldPos.X * 0.5f + _time * 2.0f) * _turbulence;
            float turbZ = MathF.Cos(worldPos.Z * 0.3f + _time * 1.7f) * _turbulence;
            Vector3 turbulence = new Vector3(turbX, 0, turbZ) * _windStrength;

            // 风力沿头发方向的分量（头发越硬，受风影响越小）
            Vector3 wind = (baseWind + turbulence) * (1.0f - stiffness);

            return wind * 0.01f; // 缩放到合理范围
        }

        /// <summary>计算噪声风场（更高频的变化）。</summary>
        public Vector3 ComputeNoiseWind(Vector3 pos)
        {
            float t = _time;
            float nx = MathF.Sin(pos.X * 1.5f + t * 3.0f) * MathF.Cos(pos.Z * 1.2f + t * 2.1f);
            float nz = MathF.Cos(pos.X * 1.1f + t * 2.5f) * MathF.Sin(pos.Z * 1.8f + t * 1.9f);
            float ny = MathF.Sin(pos.X * 0.7f + pos.Z * 0.9f + t * 1.3f) * 0.3f;
            return new Vector3(nx, ny, nz) * _windStrength * 0.02f;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>头发渲染常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HairRenderingCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 PrevViewProj;
        public Vector3 CameraPos;
        public float HairThickness;
        public Vector3 LightDir;
        public float LightIntensity;
        public Vector3 LightColor;
        public float ShadowStrength;
        public Vector4 HairColor;
        public Vector4 HairTipColor;
        public Vector4 SpecularTint;
        public Vector4 MarschnerParams; // X=IOR, Y=R offset, Z=R rough, W=TT rough
        public Vector4 MarschnerParams2; // X=TRT rough, Y=R scale, Z=TT scale, W=TRT scale
        public int ShadingModel;
        public int EnableSelfShadow;
        public int EnableMS;
        public int FrameIndex;
        public float WindStrength;
        public Vector3 WindDir;
        public Vector2 InvResolution;
        public int SegmentsPerStrand;
        public int MaxStrands;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>头发渲染系统。</summary>
    public sealed class HairRenderingSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly HairRenderingConfig _config;
        private readonly HairWindSimulator _windSim = new();
        private RHIPipelineState? _hairPSO;
        private RHIPipelineState? _hairShadowPSO;
        private RHIPipelineState? _hairDepthPSO;
        private RHIConstantBuffer? _hairCB;
        private RHIStructuredBuffer? _strandBuffer;
        private RHITexture? _strandDepthTexture;
        private bool _disposed;
        private bool _initialized;

        // 头发数据
        private Vector3[]? _strandRoots;
        private Vector3[]? _strandControlPoints;
        private int _strandCount;

        // 统计
        private int _lastRenderedStrands;
        private float _lastRenderTimeMs;

        public HairRenderingConfig Config => _config;
        public int StrandCount => _strandCount;
        public int LastRenderedStrands => _lastRenderedStrands;

        public HairRenderingSystem(RHIDevice device, HairRenderingConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? HairRenderingConfig.Default;
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            if (_initialized) return;

            _hairCB = _device.CreateConstantBuffer<HairRenderingCB>();

            _strandDepthTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R16_Float,
                Usage = RHITextureUsage.DepthStencil | RHITextureUsage.ShaderResource,
                DebugName = "Hair_Depth"
            });

            _initialized = true;
        }

        /// <summary>设置头发数据（从外部导入）。</summary>
        public void SetHairData(Vector3[] roots, int controlPointCount, int strandsPerControlPoint)
 {
            _strandRoots = roots;
            _strandCount = roots.Length * strandsPerControlPoint;
            _strandControlPoints = new Vector3[_strandCount * controlPointCount];

            // 生成控制点（简化：从根沿法线方向延伸）
            for (int i = 0; i < roots.Length; i++)
            {
                Vector3 root = roots[i];
                Vector3 direction = Vector3.Normalize(new Vector3(
                    (float)new Random(i).NextDouble() - 0.5f,
                    -1.0f,
                    (float)new Random(i + 1).NextDouble() - 0.5f));

                for (int s = 0; s < strandsPerControlPoint; s++)
                {
                    int strandIdx = i * strandsPerControlPoint + s;
                    float length = 0.1f + (float)new Random(strandIdx).NextDouble() * 0.1f;

                    for (int cp = 0; cp < controlPointCount; cp++)
                    {
                        float t = (float)cp / (controlPointCount - 1);
                        Vector3 pos = root + direction * (length * t);
                        // 加点随机扰动
                        pos += new Vector3(
                            (float)new Random(strandIdx * 100 + cp).NextDouble() - 0.5f,
                            0,
                            (float)new Random(strandIdx * 100 + cp + 1).NextDouble() - 0.5f) * 0.005f * t;

                        _strandControlPoints![strandIdx * controlPointCount + cp] = pos;
                    }
                }
            }
        }

        /// <summary>渲染头发。</summary>
        public void Render(
            RHICommandList cmdList,
            Vector3 lightDir, Vector3 lightColor, float lightIntensity,
            CameraData camera, HairColorParams colorParams,
            float deltaTime, int frameIndex)
        {
            if (!_initialized) throw new InvalidOperationException("Hair system not initialized");
            long startTime = Environment.TickCount64;

            // 更新风力
            if (_config.EnableWind)
                _windSim.Update(deltaTime, _config.WindDirection, _config.WindStrength);

            // 更新常量缓冲
            var cb = new HairRenderingCB
            {
                ViewProj = camera.ViewProjMatrix,
                PrevViewProj = camera.ViewProjMatrix,
                CameraPos = camera.Position,
                HairThickness = _config.HairThickness,
                LightDir = lightDir,
                LightIntensity = lightIntensity,
                LightColor = lightColor,
                ShadowStrength = _config.SelfShadowStrength,
                HairColor = new Vector4(colorParams.RootColor, 1),
                HairTipColor = new Vector4(colorParams.TipColor, colorParams.TipTransition),
                SpecularTint = new Vector4(colorParams.SpecularTint, 0),
                MarschnerParams = new Vector4(
                    MarschnerParams.Default.IOR,
                    MarschnerParams.Default.ROffset,
                    MarschnerParams.Default.RRoughness,
                    MarschnerParams.Default.TTRoughness),
                MarschnerParams2 = new Vector4(
                    MarschnerParams.Default.TRTRoughness,
                    MarschnerParams.Default.RScale,
                    MarschnerParams.Default.TTScale,
                    MarschnerParams.Default.TRTScale),
                ShadingModel = (int)_config.ShadingModel,
                EnableSelfShadow = _config.EnableSelfShadow ? 1 : 0,
                EnableMS = _config.EnableMultipleScattering ? 1 : 0,
                FrameIndex = frameIndex,
                WindStrength = _config.WindStrength,
                WindDir = _config.WindDirection,
                InvResolution = new Vector2(1.0f / 1920, 1.0f / 1080),
                SegmentsPerStrand = _config.SegmentsPerStrand,
                MaxStrands = _config.MaxStrandCount
            };
            _hairCB!.UpdateData(ref cb);

            // 渲染
            cmdList.SetConstantBuffer(0, _hairCB);
            cmdList.SetPipelineState(_hairPSO!);
            // 绘制头发线段/三角形
            _lastRenderedStrands = Math.Min(_strandCount, _config.MaxStrandCount);

            _lastRenderTimeMs = (Environment.TickCount64 - startTime) / 10.0f;
        }

        /// <summary>获取调试信息。</summary>
        public string GetDebugInfo()
 {
            return $"Hair: {_lastRenderedStrands} strands, " +
                   $"Model={_config.ShadingModel}, Geo={_config.GeometryType}\n" +
                   $"Time: {_lastRenderTimeMs:F2}ms";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _hairPSO?.Dispose();
            _hairShadowPSO?.Dispose();
            _hairDepthPSO?.Dispose();
            _hairCB?.Dispose();
            _strandBuffer?.Dispose();
            _strandDepthTexture?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}