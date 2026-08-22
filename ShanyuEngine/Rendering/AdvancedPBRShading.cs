using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 高级 PBR 着色模型 (超越 UE6 材质系统)
    //
    // 完整的物理材质着色系统，包含:
    //  1. Disney Principled BRDF - Hanrahan-Krueger漫反射 + GGX高光 + 多层Lobe
    //  2. Subsurface Scattering - Burley归一化漫反射（含SSS近似项）
    //  3. Anisotropic BRDF - 方向相关GGX NDF + Anisotropic Smith G
    //  4. Cloth Shading - Charlie sheen + Diffuse wrap + 纤维高光
    //  5. Eye Shading - 虹膜折射 + 焦散近似 + Limbal Darkening
    //  6. Material Layering - Top Coat / Base Coat 叠加 + 能量守恒
    //  7. Iridescence - 薄膜干涉（基于厚度的F0偏移 + 虹彩色移）
    //
    // 参考文献:
    //  [Burley+2012] "Physically Based Shading at Disney"
    //  [Karis+2013] "Real Shading in Unreal Engine 4"
    //  [Heitz+2014] "Understanding the Masking-Shadowing Function"
    //  [Kulla+2017] "Revisiting Physically Based Shading at Imageworks"
    //  [Estevez+2017] "Production Micro-Facet Hair Shading Models"
    //  [Gulbrandsen+2014] "Artist-Friendly Metallic Fresnel"
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────── 数学工具 ────────────────────────

    /// <summary>PBR 着色常用数学工具。</summary>
    public static class PBRMath
    {
        /// <summary>Clamp to [eps, 1-eps]。</summary>
        public static float ClampEps(float x, float eps = 1e-5f) => Math.Clamp(x, eps, 1.0f - eps);

        /// <summary>Schlick Fresnel 近似。</summary>
        public static Vector3 SchlickFresnel(float cosTheta, Vector3 f0)
        {
            float t = Math.Max(0.0f, 1.0f - cosTheta);
            float t5 = t * t * t * t * t;
            return f0 + (Vector3.One - f0) * t5;
        }

        /// <summary>Schlick Fresnel (标量F0)。</summary>
        public static float SchlickFresnel(float cosTheta, float f0)
        {
            float t = Math.Max(0.0f, 1.0f - cosTheta);
            float t5 = t * t * t * t * t;
            return f0 + (1.0f - f0) * t5;
        }

        /// <summary>粗糙度重映射: perceptualRoughness -> alpha。</summary>
        public static float PerceptualRoughnessToAlpha(float perceptualRoughness)
        {
            return perceptualRoughness * perceptualRoughness;
        }

        /// <summary>Alpha -> perceptualRoughness。</summary>
        public static float AlphaToPerceptualRoughness(float alpha)
        {
            return MathF.Sqrt(Math.Max(alpha, 0.0f));
        }

        /// <summary>球面坐标（theta, phi）到方向向量。</summary>
        public static Vector3 SphericalToDirection(float cosTheta, float sinTheta, float phi)
        {
            return new Vector3(sinTheta * MathF.Cos(phi), cosTheta, sinTheta * MathF.Sin(phi));
        }

        /// <summary>各向异性方向。</summary>
        public static void GetAnisotropicDirections(
            Vector3 normal, Vector3 tangent, Vector3 bitangent,
            float anisotropy, out Vector3 anisoTangent, out Vector3 anisoBitangent)
        {
            // 各向异性拉伸切线方向
            anisoTangent = Vector3.Normalize(Vector3.Lerp(normal, tangent, anisotropy));
            anisoBitangent = Vector3.Normalize(Vector3.Cross(normal, anisoTangent));
        }
    }

    // ──────────────────────── 材质参数 ────────────────────────

    /// <summary>高级PBR材质参数（完整迪士尼原则化材质）。</summary>
    public struct AdvancedMaterialParameters
    {
        /// <summary>基础色（反照率, linear sRGB）。</summary>
        public Vector3 BaseColor;
        /// <summary>感知粗糙度 [0,1]。</summary>
        public float PerceptualRoughness;
        /// <summary>金属度 [0,1]。</summary>
        public float Metallic;
        /// <summary>次表面散射权重 (subsurface, Disney diffuse中的SSS项)。</summary>
        public float Subsurface;
        /// <summary>次表面颜色。</summary>
        public Vector3 SubsurfaceColor;
        /// <summary>光泽层强度 (Sheen)。</summary>
        public float Sheen;
        /// <summary>光泽层颜色。</summary>
        public Vector3 SheenColor;
        /// <summary>Clearcoat强度。</summary>
        public float Clearcoat;
        /// <summary>Clearcoat粗糙度。</summary>
        public float ClearcoatGloss;
        /// <summary>各向异性强度 [-1, 1]（负值=沿Bitangent拉伸）。</summary>
        public float Anisotropy;
        /// <summary>各向异性旋转角度（弧度）。</summary>
        public float AnisotropyRotation;
        /// <summary>薄膜干涉厚度 (nm)。</summary>
        public float ThinFilmThickness;
        /// <summary>透射率 (Transmission, 用于玻璃/水等材质)。</summary>
        public float Transmission;
        /// <summary>IOR (折射率)。</summary>
        public float IOR;
        /// <summary>厚度 (用于次表面散射的传输近似)。</summary>
        public float Thickness;
        /// <summary>散射系数 (Scatter)。</summary>
        public Vector3 ScatterColor;
        /// <summary>衰减距离。</summary>
        public float AttenuationDistance;
        /// <summary>材质类型 (0=Standard, 1=Cloth, 2=Eye, 3=Hair)。</summary>
        public int MaterialType;
        /// <summary>布料颜色绕射角度（wrap, 用于Cloth）。</summary>
        public float ClothWrap;
        /// <summary>虹膜参数（用于Eye shading）。</summary>
        public Vector3 IrisColor;
        /// <summary>虹膜深度偏移。</summary>
        public float IrisDepth;
        /// <summary>Limbal Ring 强度。</summary>
        public float LimbalRingIntensity;
        /// <summary>焦散强度。</summary>
        public float CausticIntensity;

        /// <summary>创建默认标准材质。</summary>
        public static AdvancedMaterialParameters CreateDefault()
        {
            return new AdvancedMaterialParameters
            {
                BaseColor = new Vector3(0.8f, 0.8f, 0.8f),
                PerceptualRoughness = 0.5f,
                Metallic = 0.0f,
                Subsurface = 0.0f,
                SubsurfaceColor = Vector3.Zero,
                Sheen = 0.0f,
                SheenColor = new Vector3(1.0f, 1.0f, 1.0f),
                Clearcoat = 0.0f,
                ClearcoatGloss = 0.0f,
                Anisotropy = 0.0f,
                AnisotropyRotation = 0.0f,
                ThinFilmThickness = 0.0f,
                Transmission = 0.0f,
                IOR = 1.5f,
                Thickness = 0.5f,
                ScatterColor = Vector3.One,
                AttenuationDistance = 1.0f,
                MaterialType = 0,
                ClothWrap = 0.5f,
                IrisColor = new Vector3(0.2f, 0.5f, 0.3f),
                IrisDepth = 0.1f,
                LimbalRingIntensity = 0.3f,
                CausticIntensity = 0.0f
            };
        }
    }

    // ──────────────────────── GGX 分布函数 ────────────────────────

    /// <summary>GGX (Trowbridge-Reitz) 法线分布函数 (NDF)。</summary>
    public static class GGXNDF
    {
        /// <summary>标准 GGX NDF: D(h) = alpha^2 / (pi * (NdotH^2 * (alpha^2 - 1) + 1)^2)。</summary>
        public static float Evaluate(float alpha, float NdotH)
        {
            float a2 = alpha * alpha;
            float NdotH2 = NdotH * NdotH;
            float denom = NdotH2 * (a2 - 1.0f) + 1.0f;
            return a2 / (MathF.PI * denom * denom);
        }

        /// <summary>Smith G 函数 (联合遮蔽-阴影项)。</summary>
        public static float SmithG(float alpha, float NdotV, float NdotL)
        {
            float gV = SmithG1(alpha, NdotV);
            float gL = SmithG1(alpha, NdotL);
            return gV * gL;
        }

        /// <summary>Smith G1 (单方向遮蔽函数)。</summary>
        /// <remarks>
        /// G1(v) = 2 * NdotV / (NdotV + sqrt(alpha^2 + (1-alpha^2) * NdotV^2))
        /// </remarks>
        public static float SmithG1(float alpha, float NdotV)
        {
            float a2 = alpha * alpha;
            float denom = NdotV + MathF.Sqrt(a2 + (1.0f - a2) * NdotV * NdotV);
            return 2.0f * NdotV / Math.Max(denom, 1e-7f);
        }

        /// <summary>Smith G1 高度相关版本（更精确）。</summary>
        public static float SmithG1HeightCorrelated(float alpha, float NdotV, float NdotL)
        {
            float a2 = alpha * alpha;
            float v = NdotV * MathF.Sqrt(NdotL * NdotL * (1.0f - a2) + a2);
            float l = NdotL * MathF.Sqrt(NdotV * NdotV * (1.0f - a2) + a2);
            return 0.5f / Math.Max(v + l, 1e-7f);
        }

        /// <summary>各向异性 GGX NDF。</summary>
        /// <param name="alphaT">沿切线方向的粗糙度。</param>
        /// <param name="alphaB">沿副切线方向的粗糙度。</param>
        /// <param name="NdotH">法线与半程向量的点乘。</param>
        /// <param name="TdotH">切线与半程向量的点乘。</param>
        /// <param name="BdotH">副切线与半程向量的点乘。</param>
        public static float AnisotropicEvaluate(float alphaT, float alphaB, float NdotH, float TdotH, float BdotH)
        {
            float d = TdotH * TdotH / Math.Max(alphaT * alphaT, 1e-7f) +
                       BdotH * BdotH / Math.Max(alphaB * alphaB, 1e-7f) +
                       NdotH * NdotH;
            float denom = d * d;
            return 1.0f / (MathF.PI * Math.Max(alphaT * alphaB, 1e-7f) * Math.Max(denom, 1e-7f));
        }

        /// <summary>各向异性 Smith G1。</summary>
        public static float AnisotropicG1(float alphaT, float alphaB, Vector3 direction,
            Vector3 normal, Vector3 tangent, Vector3 bitangent)
        {
            float NdotV = Math.Max(0.0f, Vector3.Dot(normal, direction));
            float TdotV = Vector3.Dot(tangent, direction);
            float BdotV = Vector3.Dot(bitangent, direction);

            float v2 = TdotV * TdotV * alphaB * alphaB +
                       BdotV * BdotV * alphaT * alphaT;
            float denom = NdotV * NdotV * (alphaT * alphaT * alphaB * alphaB) + v2;
            return NdotV * 2.0f / MathF.Sqrt(Math.Max(denom, 1e-7f));
        }
    }

    // ──────────────────────── Disney 原则化 BRDF ────────────────────────

    /// <summary>迪士尼原则化 BRDF 完整实现。</summary>
    public static class DisneyPrincipledBRDF
    {
        /// <summary>完整迪士尼 BRDF 求值。</summary>
        /// <param name="mat">材质参数。</param>
        /// <param name="normal">表面法线。</param>
        /// <param name="viewDir">视线方向。</param>
        /// <param name="lightDir">光线方向。</param>
        /// <param name="halfVector">半程向量 (H = normalize(V+L))。</param>
        /// <param name="NdotL">N.L。</param>
        /// <param name="NdotV">N.V。</param>
        /// <param name="NdotH">N.H。</param>
        /// <param name="LdotH">L.H。</param>
        /// <param name="tangent">切线。</param>
        /// <param name="bitangent">副切线。</param>
        /// <returns>BRDF 值。</returns>
        public static Vector3 Evaluate(
            ref AdvancedMaterialParameters mat,
            Vector3 normal, Vector3 viewDir, Vector3 lightDir,
            Vector3 halfVector, float NdotL, float NdotV, float NdotH, float LdotH,
            Vector3 tangent, Vector3 bitangent)
        {
            Vector3 f0 = ComputeF0(mat);

            // 根据材质类型选择着色模型
            switch (mat.MaterialType)
            {
                case 1: return ClothShading.Evaluate(ref mat, normal, viewDir, lightDir,
                            halfVector, NdotL, NdotV, NdotH, LdotH);
                case 2: return EyeShading.Evaluate(ref mat, normal, viewDir, lightDir,
                            halfVector, NdotL, NdotV, NdotH, LdotH);
                default: break;
            }

            // ── Diffuse Lobe: Hanrahan-Krueger (含次表面近似) ──
            Vector3 diffuseLobe = BurleyDiffuse.Evaluate(
                mat.BaseColor, mat.Subsurface, mat.SubsurfaceColor,
                NdotL, NdotV, LdotH);

            // ── Specular Lobe: GGX NDF + Smith G + Fresnel ──
            float alpha = PBRMath.PerceptualRoughnessToAlpha(mat.PerceptualRoughness);
            float D = GGXNDF.Evaluate(alpha, NdotH);
            float G = GGXNDF.SmithG(alpha, NdotV, NdotL);

            Vector3 F = PBRMath.SchlickFresnel(LdotH, f0);

            // 薄膜干涉修改Fresnel
            if (mat.ThinFilmThickness > 0.0f)
            {
                Vector3 thinFilmF0 = ComputeThinFilmF0(mat.ThinFilmThickness, mat.IOR);
                F = PBRMath.SchlickFresnel(LdotH, thinFilmF0);
                F = Vector3.Max(F, thinFilmF0 * 0.5f);
            }

            Vector3 specularLobe = D * G * F / Math.Max(4.0f * NdotV * NdotL, 1e-7f);

            // ── Sheen Lobe: Charlie光泽 ──
            Vector3 sheenLobe = Vector3.Zero;
            if (mat.Sheen > 0.0f)
            {
                sheenLobe = CharlieSheen.Evaluate(mat.Sheen, mat.SheenColor, NdotH, NdotV, NdotL);
            }

            // ── Clearcoat Lobe ──
            Vector3 clearcoatLobe = Vector3.Zero;
            if (mat.Clearcoat > 0.0f)
            {
                clearcoatLobe = EvaluateClearcoat(mat.Clearcoat, mat.ClearcoatGloss,
                    NdotH, NdotV, NdotL, LdotH);
            }

            // ── 各向异性高光修正 ──
            if (Math.Abs(mat.Anisotropy) > 0.01f)
            {
                specularLobe = EvaluateAnisotropicSpecular(
                    ref mat, normal, viewDir, lightDir, halfVector,
                    NdotL, NdotV, NdotH, tangent, bitangent);
            }

            // ── 能量守恒: 各Lobe按权重叠加 ──
            // diffuse 能量需要减去被specular占用的部分
            float fresnelDiffuse = 1.0f - (F.X + F.Y + F.Z) * 0.333f; // 近似
            float fresnelSheen = 1.0f - Math.Max(mat.Sheen, 0.0f);
            Vector3 result = diffuseLobe * fresnelDiffuse * fresnelSheen +
                              specularLobe +
                              sheenLobe +
                              clearcoatLobe;

            return result;
        }

        /// <summary>计算 F0 (基础反射率)。</summary>
        private static Vector3 ComputeF0(AdvancedMaterialParameters mat)
        {
            // 非金属: F0 = ((ior-1)/(ior+1))^2 * 0.08 + 0.04
            // 金属: F0 = baseColor
            Vector3 dielectricF0 = Vector3.One * 0.04f;
            Vector3 metallicF0 = mat.BaseColor;
            return Vector3.Lerp(dielectricF0, metallicF0, mat.Metallic);
        }

        /// <summary>Clearcoat Lobe: 额外光滑层。</summary>
        private static Vector3 EvaluateClearcoat(
            float clearcoat, float clearcoatGloss,
            float NdotH, float NdotV, float NdotL, float LdotH)
        {
            // Clearcoat 使用更光滑的GGX (alpha = lerp(0.1, 0.001, clearcoatGloss))
            float ccAlpha = PBRMath.PerceptualRoughnessToAlpha(
                MathF.Lerp(0.1f, 0.001f, clearcoatGloss));
            float ccD = GGXNDF.Evaluate(ccAlpha, NdotH);
            float ccG = GGXNDF.SmithG(ccAlpha, NdotV, NdotL);
            float ccF = PBRMath.SchlickFresnel(LdotH, 0.04f);

            return new Vector3(clearcoat * ccD * ccG * ccF /
                                Math.Max(4.0f * NdotV * NdotL, 1e-7f));
        }

        /// <summary>各向异性高光计算。</summary>
        private static Vector3 EvaluateAnisotropicSpecular(
            ref AdvancedMaterialParameters mat,
            Vector3 normal, Vector3 viewDir, Vector3 lightDir, Vector3 halfVector,
            float NdotL, float NdotV, float NdotH,
            Vector3 tangent, Vector3 bitangent)
        {
            float alpha = PBRMath.PerceptualRoughnessToAlpha(mat.PerceptualRoughness);
            float aspect = MathF.Sqrt(Math.Max(1.0f - mat.Anisotropy * 0.9f, 0.1f));

            // 旋转各向异性方向
            float rotAngle = mat.AnisotropyRotation;
            Vector3 rotTangent = tangent * MathF.Cos(rotAngle) + bitangent * MathF.Sin(rotAngle);
            Vector3 rotBitangent = -tangent * MathF.Sin(rotAngle) + bitangent * MathF.Cos(rotAngle);

            float alphaT = Math.Max(alpha / aspect, 1e-5f);
            float alphaB = alpha * aspect;

            float TdotH = Vector3.Dot(rotTangent, halfVector);
            float BdotH = Vector3.Dot(rotBitangent, halfVector);
            float TdotV = Vector3.Dot(rotTangent, viewDir);
            float BdotV = Vector3.Dot(rotBitangent, viewDir);
            float TdotL = Vector3.Dot(rotTangent, lightDir);
            float BdotL = Vector3.Dot(rotBitangent, lightDir);

            float D = GGXNDF.AnisotropicEvaluate(alphaT, alphaB, NdotH, TdotH, BdotH);
            float Gv = GGXNDF.AnisotropicG1(alphaT, alphaB, viewDir, normal, rotTangent, rotBitangent);
            float Gl = GGXNDF.AnisotropicG1(alphaT, alphaB, lightDir, normal, rotTangent, rotBitangent);

            Vector3 f0 = ComputeF0(mat);
            Vector3 F = PBRMath.SchlickFresnel(NdotV, f0);

            return new Vector3(D * Gv * Gl * F.X /
                                Math.Max(4.0f * NdotV * NdotL, 1e-7f),
                                D * Gv * Gl * F.Y /
                                Math.Max(4.0f * NdotV * NdotL, 1e-7f),
                                D * Gv * Gl * F.Z /
                                Math.Max(4.0f * NdotV * NdotL, 1e-7f));
        }

        // ──────────────── 薄膜干涉 ────────────────

        /// <summary>计算薄膜干涉的F0偏移。</summary>
        /// <param name="thickness">薄膜厚度 (nm)。</param>
        /// <param name="ior">薄膜折射率。</param>
        public static Vector3 ComputeThinFilmF0(float thickness, float ior)
        {
            // 薄膜干涉: 基于光程差计算相位差，产生虹彩色移
            // delta = 4 * pi * n * d / lambda
            // 其中 n=ior, d=thickness, lambda=可见光波长
            // 简化: 对RGB三通道使用不同波长计算

            float deltaR = 4.0f * MathF.PI * ior * thickness / 650.0f; // 红光波长
            float deltaG = 4.0f * MathF.PI * ior * thickness / 530.0f; // 绿光波长
            float deltaB = 4.0f * MathF.PI * ior * thickness / 460.0f; // 蓝光波长

            // 干涉强度: I = 2 * R * (1 - cos(delta))
            float baseReflectance = ((ior - 1.0f) / (ior + 1.0f));
            float R = baseReflectance * baseReflectance;

            float iridR = 2.0f * R * (1.0f - MathF.Cos(deltaR));
            float iridG = 2.0f * R * (1.0f - MathF.Cos(deltaG));
            float iridB = 2.0f * R * (1.0f - MathF.Cos(deltaB));

            // 基础F0 + 干涉色移
            return new Vector3(
                Math.Clamp(0.04f + iridR, 0.0f, 1.0f),
                Math.Clamp(0.04f + iridG, 0.0f, 1.0f),
                Math.Clamp(0.04f + iridB, 0.0f, 1.0f));
        }
    }

    // ──────────────────────── Burley 漫反射（次表面近似） ────────────────────────

    /// <summary>Burley Normalized Diffuse（原迪士尼漫反射，含次表面散射近似项）。</summary>
    public static class BurleyDiffuse
    {
        /// <summary>Disney/Burley 漫反射项。</summary>
        /// <remarks>
        /// f_diffuse = baseColor/pi * (1 + (subsurface-1)/pi * (1+cos(theta_i)) * (1-cos(theta_o)^5/2) / (1-cos(theta_i)) )
        /// 其中 subsurface 参数控制漫反射到次表面的过渡:
        ///   subsurface=0 -> 标准Lambert
        ///   subsurface=1 -> 完全次表面散射外观
        /// </remarks>
        public static Vector3 Evaluate(
            Vector3 baseColor, float subsurface, Vector3 subsurfaceColor,
            float NdotL, float NdotV, float LdotH)
        {
            // 标准Lambert项
            Vector3 lambert = baseColor / MathF.PI;

            // Burley 修正项 (subsurface 近似)
            float FL = DisneyDiffuseFresnel(NdotL);
            float FV = DisneyDiffuseFresnel(NdotV);
            float Fd90 = 0.5f + 2.0f * LdotH * LdotH;
            float Fd = 1.0f + (Fd90 - 1.0f) * FL;
            Fd *= 1.0f + (Fd90 - 1.0f) * FV;

            // 次表面散射项
            Vector3 sssColor = Vector3.Lerp(baseColor, subsurfaceColor, subsurface);
            float sssMix = (subsurface * 0.5f) * (FL * MathF.Pow(1.0f - NdotL, 5.0f) +
                                                     FV * MathF.Pow(1.0f - NdotV, 5.0f));

            return lambert * Fd + sssColor * sssMix / MathF.PI;
        }

        /// <summary>Disney漫反射Fresnel因子。</summary>
        private static float DisneyDiffuseFresnel(float cosTheta)
        {
            float bias = 0.5f;
            float scale = 1.0f;
            return scale * MathF.Pow(1.0f - cosTheta, 5.0f) + bias;
        }
    }

    // ──────────────────────── 次表面散射传输近似 ────────────────────────

    /// <summary>次表面散射传输剖面近似（基于薄板模型）。</summary>
    public static class SubsurfaceScattering
    {
        /// <summary>传输剖面计算: 基于Disney的translucency参数。</summary>
        /// <remarks>
        /// 传输近似: T(d) = exp(-sigma_t * d)
        /// 其中 sigma_t = scatter / attenuation_distance
        /// </remarks>
        public static Vector3 EvaluateTransmission(
            Vector3 scatterColor, float attenuationDistance, float thickness,
            Vector3 normal, Vector3 lightDir, Vector3 viewDir)
        {
            float NdotL = Math.Max(0.0f, Vector3.Dot(normal, lightDir));
            float NdotV = Math.Max(0.0f, Vector3.Dot(normal, viewDir));

            // 计算光线穿过材质的等效路径长度
            float wrap = 0.5f; // 绕射因子
            float wrappedNdotL = Math.Max(0.0f, (NdotL + wrap) / (1.0f + wrap));

            // Beer-Lambert 衰减
            float opticalDepth = thickness / Math.Max(attenuationDistance, 1e-5f);
            Vector3 transmittance = new Vector3(
                MathF.Exp(-scatterColor.X * opticalDepth),
                MathF.Exp(-scatterColor.Y * opticalDepth),
                MathF.Exp(-scatterColor.Z * opticalDepth));

            // 传输近似辐照度
            return transmittance * wrappedNdotL * thickness / MathF.PI;
        }

        /// <summary>屏幕空间次表面散射近似 (Screen-Space Subsurface)。</summary>
        /// <remarks>
        /// 使用屏幕空间模糊来模拟次表面散射的光线扩散效果。
        /// 在着色点周围按高斯核采样深度/法线相似的像素颜色，模拟光线在皮肤内部的散射。
        /// </remarks>
        public static Vector3 ScreenSpaceSubsurfaceApprox(
            Vector3[] colorBuffer, float[] depthBuffer, Vector3[] normalBuffer,
            int x, int y, int width, int height,
            Vector3 scatterColor, float scatterRadius)
        {
            Vector3 centerColor = colorBuffer[y * width + x];
            float centerDepth = depthBuffer[y * width + x];
            Vector3 centerNormal = normalBuffer[y * width + x];

            // 采样核: 7x7 高斯核
            int kernelRadius = 3;
            float[] gaussianKernel = {
                0.003f, 0.013f, 0.022f, 0.026f, 0.022f, 0.013f, 0.003f,
                0.013f, 0.059f, 0.097f, 0.112f, 0.097f, 0.059f, 0.013f,
                0.022f, 0.097f, 0.159f, 0.183f, 0.159f, 0.097f, 0.022f,
                0.026f, 0.112f, 0.183f, 0.211f, 0.183f, 0.112f, 0.026f,
                0.022f, 0.097f, 0.159f, 0.183f, 0.159f, 0.097f, 0.022f,
                0.013f, 0.059f, 0.097f, 0.112f, 0.097f, 0.059f, 0.013f,
                0.003f, 0.013f, 0.022f, 0.026f, 0.022f, 0.013f, 0.003f
            };

            Vector3 blurredColor = Vector3.Zero;
            float totalWeight = 0.0f;

            for (int ky = -kernelRadius; ky <= kernelRadius; ky++)
            {
                for (int kx = -kernelRadius; kx <= kernelRadius; kx++)
                {
                    int sx = x + kx;
                    int sy = y + ky;
                    if (sx < 0 || sx >= width || sy < 0 || sy >= height) continue;

                    int sIdx = sy * width + sx;
                    float sampleDepth = depthBuffer[sIdx];
                    Vector3 sampleNormal = normalBuffer[sIdx];

                    // 深度/法线相似性权重
                    float depthDiff = Math.Abs(centerDepth - sampleDepth);
                    float depthWeight = MathF.Exp(-depthDiff * depthDiff / (0.02f * 0.02f));
                    float normalDot = Vector3.Dot(centerNormal, sampleNormal);
                    float normalWeight = MathF.Pow(Math.Max(0.0f, normalDot), 8.0f);

                    float kernelWeight = gaussianKernel[(ky + kernelRadius) * 7 + (kx + kernelRadius)];
                    float weight = kernelWeight * depthWeight * normalWeight;

                    blurredColor += colorBuffer[sIdx] * weight;
                    totalWeight += weight;
                }
            }

            blurredColor = totalWeight > 0.0f ? blurredColor / totalWeight : centerColor;

            // 散射颜色调制: 减去直接光照，添加散射颜色
            Vector3 sss = (blurredColor - centerColor) * scatterColor * scatterRadius;

            return Vector3.Max(sss, Vector3.Zero);
        }
    }

    // ──────────────────────── Charlie Sheen ────────────────────────

    /// <summary>Charlie Sheen 光泽层 NDF。</summary>
    public static class CharlieSheen
    {
        /// <summary>Charlie NDF (用于布料光泽的高光分布)。</summary>
        /// <remarks>
        /// D_Charlie(x) = (2 + 1/roughness) * pow(1 - cos(theta_h), 1 + 1/roughness) / (2 * pi)
        /// 比 GGX 更适合布料光泽的分布特征。
        /// </remarks>
        public static float Evaluate(float sheen, Vector3 sheenColor, float NdotH, float NdotV, float NdotL)
        {
            float sheenRoughness = 0.5f; // 固定Charlie粗糙度
            float invRoughness = 1.0f / Math.Max(sheenRoughness, 1e-5f);

            float D = (2.0f + invRoughness) *
                      MathF.Pow(Math.Max(0.0f, 1.0f - NdotH), invRoughness + 1.0f) /
                      (2.0f * MathF.PI);

            // 能量补偿的G项
            float G = 1.0f / Math.Max(4.0f * NdotV * NdotL, 1e-7f);

            // Fresnel (简化)
            float sheenFresnel = PBRMath.SchlickFresnel(NdotV, 0.04f);

            return sheen * D * G * sheenFresnel;
        }
    }

    // ──────────────────────── 布料着色 ────────────────────────

    /// <summary>布料着色模型 (Cloth Shading)。</summary>
    public static class ClothShading
    {
        /// <summary>布料着色完整求值。</summary>
        /// <remarks>
        /// 布料材质特点:
        ///  - 使用 Diffuse Wrap 漫反射（布料的漫反射比Lambert更宽）
        ///  - Charlie Sheen 光泽（布料特有的光泽层）
        ///  - 无金属感（metallic=0）
        ///  - 纤维方向高光（各向异性高光）
        /// </remarks>
        public static Vector3 Evaluate(
            ref AdvancedMaterialParameters mat,
            Vector3 normal, Vector3 viewDir, Vector3 lightDir,
            Vector3 halfVector, float NdotL, float NdotV, float NdotH, float LdotH)
        {
            // Diffuse Wrap: cos(theta) * (1 + (1-cos(theta)) * wrap)^2 / (2*(1+wrap))
            // 比标准Lambert更宽的漫反射分布
            float wrap = mat.ClothWrap; // typically 0.3-0.7
            float wrappedNdotL = Math.Max(0.0f, (NdotL + wrap) / (1.0f + wrap));
            wrappedNdotL = wrappedNdotL * wrappedNdotL;
            Vector3 diffuseColor = mat.BaseColor * wrappedNdotL / MathF.PI;

            // Charlie Sheen
            Vector3 sheen = Vector3.Zero;
            if (mat.Sheen > 0.0f)
            {
                float sheenValue = CharlieSheen.Evaluate(mat.Sheen, mat.SheenColor, NdotH, NdotV, NdotL);
                sheen = mat.SheenColor * sheenValue;
            }

            // 纤维方向高光 (简化各向异性GGX)
            Vector3 specular = Vector3.Zero;
            if (mat.PerceptualRoughness < 0.5f)
            {
                float alpha = PBRMath.PerceptualRoughnessToAlpha(mat.PerceptualRoughness);
                float D = GGXNDF.Evaluate(alpha * 0.5f, NdotH); // 布料用更平滑的NDF
                float G = GGXNDF.SmithG(alpha, NdotV, NdotL);
                float F = PBRMath.SchlickFresnel(LdotH, new Vector3(0.04f));
                specular = new Vector3(D * G * F.X, D * G * F.Y, D * G * F.Z) /
                           Math.Max(4.0f * NdotV * NdotL, 1e-7f);
            }

            return diffuseColor + sheen + specular;
        }
    }

    // ──────────────────────── 眼睛着色 ────────────────────────

    /// <summary>眼睛着色模型 (Eye Shading)。</summary>
    public static class EyeShading
    {
        /// <summary>眼睛着色完整求值。</summary>
        /// <remarks>
        /// 眼睛着色需要特殊处理:
        ///  1. 虹膜折射 - 光线穿过角膜折射进入虹膜
        ///  2. 焦散近似 - 虹膜背后的焦散光效
        ///  3. Limbal Darkening - 虹膜边缘变暗
        /// </remarks>
        public static Vector3 Evaluate(
            ref AdvancedMaterialParameters mat,
            Vector3 normal, Vector3 viewDir, Vector3 lightDir,
            Vector3 halfVector, float NdotL, float NdotV, float NdotH, float LdotH)
        {
            // 虹膜折射近似: 调整光线方向模拟角膜折射
            // 简化: 在虹膜区域使用更深的NdotL
            float iorCornea = 1.376f; // 角膜折射率
            float refractionOffset = mat.IrisDepth;
            Vector3 refractedLightDir = lightDir; // 简化（完整版应使用Snell定律）

            // 虹膜颜色采样（基于角度和到中心距离）
            float radialAngle = MathF.Atan2(
                MathF.Sqrt(1.0f - NdotV * NdotV), NdotV);

            // Limbal Darkening: 虹膜边缘变暗
            float limbalDarken = 1.0f - mat.LimbalRingIntensity * radialAngle * radialAngle;
            Vector3 irisDiffuse = mat.IrisColor * NdotL * limbalDarken / MathF.PI;

            // 焦散近似: 虹膜背后由光聚焦产生的亮点
            // 使用角度相关的锐利高光模拟
            float causticAngle = 0.05f; // 焦散的锥角
            float causticTerm = MathF.Pow(Math.Max(0.0f, NdotH - (1.0f - causticAngle)),
                64.0f) * mat.CausticIntensity;
            Vector3 causticColor = mat.IrisColor * causticTerm;

            // 角膜高光: 非常光滑的GGX高光
            float corneaAlpha = 0.001f;
            float corneaD = GGXNDF.Evaluate(corneaAlpha, NdotH);
            float corneaG = GGXNDF.SmithG(corneaAlpha, NdotV, NdotL);
            float corneaF = PBRMath.SchlickFresnel(LdotH, 0.04f);
            Vector3 corneaSpecular = new Vector3(
                corneaD * corneaG * corneaF / Math.Max(4.0f * NdotV * NdotL, 1e-7f));

            // 虹膜漫反射高光（更粗糙）
            float irisAlpha = 0.3f;
            float irisD = GGXNDF.Evaluate(irisAlpha, NdotH);
            float irisG = GGXNDF.SmithG(irisAlpha, NdotV, NdotL);
            float irisF = PBRMath.SchlickFresnel(LdotH, 0.04f);
            Vector3 irisSpecular = mat.IrisColor * irisD * irisG * irisF /
                                   Math.Max(4.0f * NdotV * NdotL, 1e-7f);

            // 融合
            return irisDiffuse + causticColor + corneaSpecular + irisSpecular;
        }

        /// <summary>虹膜折射的Snell定律近似。</summary>
        public static Vector3 RefractApprox(Vector3 incident, Vector3 normal, float ior)
        {
            float eta = 1.0f / ior;
            float cosI = Math.Max(0.0f, -Vector3.Dot(incident, normal));
            float sinT2 = eta * eta * (1.0f - cosI * cosI);

            if (sinT2 > 1.0f) return incident; // 全反射

            float cosT = MathF.Sqrt(Math.Max(0.0f, 1.0f - sinT2));
            return eta * incident + (eta * cosI - cosT) * normal;
        }
    }

    // ──────────────────────── 材质分层 ────────────────────────

    /// <summary>材质分层系统: Top Coat / Base Coat 叠加。</summary>
    /// <remarks>
    /// 实现多层材质的能量守恒叠加:
    ///  - Bottom Layer (Base Coat): 标准迪士尼BRDF
    ///  - Top Layer (Top Coat): 透明涂层（如清漆、蜡）
    ///  - Fresnel 分离: Top Coat 的反射部分从 Bottom Layer 扣除
    /// </remarks>
    public static class MaterialLayering
    {
        /// <summary>材质层描述。</summary>
        public struct MaterialLayer
        {
            /// <summary>材质参数。</summary>
            public AdvancedMaterialParameters Material;
            /// <summary>层厚度 (0=无厚度/透明, 1=完全不透明)。</summary>
            public float Thickness;
            /// <summary>层IOR (影响Fresnel和折射)。</summary>
            public float LayerIOR;
        }

        /// <summary>计算两层材质的能量守恒叠加。</summary>
        /// <param name="topCoat">顶层涂层。</param>
        /// <param name="baseCoat">底层材质。</param>
        /// <param name="normal">法线。</param>
        /// <param name="viewDir">视线方向。</param>
        /// <param name="lightDir">光线方向。</param>
        /// <param name="tangent">切线。</param>
        /// <param name="bitangent">副切线。</param>
        /// <returns>最终颜色。</returns>
        public static Vector3 EvaluateLayered(
            ref MaterialLayer topCoat, ref MaterialLayer baseCoat,
            Vector3 normal, Vector3 viewDir, Vector3 lightDir,
            Vector3 tangent, Vector3 bitangent)
        {
            float NdotV = Math.Max(0.0f, Vector3.Dot(normal, viewDir));
            float NdotL = Math.Max(0.0f, Vector3.Dot(normal, lightDir));
            Vector3 halfVec = Vector3.Normalize(viewDir + lightDir);
            float NdotH = Math.Max(0.0f, Vector3.Dot(normal, halfVec));
            float LdotH = Math.Max(0.0f, Vector3.Dot(lightDir, halfVec));

            // ── 计算顶层Fresnel ──
            float topF0 = ((topCoat.LayerIOR - 1.0f) / (topCoat.LayerIOR + 1.0f));
            topF0 = topF0 * topF0;
            float topFresnel = PBRMath.SchlickFresnel(Math.Max(NdotV, NdotL), topF0);

            // ── 计算底层BRDF ──
            Vector3 baseBRDF = DisneyPrincipledBRDF.Evaluate(
                ref baseCoat.Material, normal, viewDir, lightDir,
                halfVec, NdotL, NdotV, NdotH, LdotH, tangent, bitangent);

            // ── 计算顶层BRDF ──
            Vector3 topBRDF = DisneyPrincipledBRDF.Evaluate(
                ref topCoat.Material, normal, viewDir, lightDir,
                halfVec, NdotL, NdotV, NdotH, LdotH, tangent, bitangent);

            // ── 能量守恒: 顶层反射的部分从底层扣除 ──
            // 底层接收到的光 = (1 - topFresnel)
            // 顶层直接反射 = topBRDF
            // 底层透射 = (1 - topFresnel)^2 * baseBRDF (进入再出来)
            float transmittance = 1.0f - topFresnel;
            float transmittance2 = transmittance * transmittance;

            // 厚度衰减 (Beer-Lambert): T = exp(-absorption * thickness)
            Vector3 topAbsorption = Vector3.Lerp(Vector3.One,
                new Vector3(0.5f, 0.3f, 0.1f), topCoat.Thickness);
            Vector3 thicknessAttenuation = new Vector3(
                MathF.Exp(-topAbsorption.X * topCoat.Thickness),
                MathF.Exp(-topAbsorption.Y * topCoat.Thickness),
                MathF.Exp(-topAbsorption.Z * topCoat.Thickness));

            // 最终结果 = 顶层反射 + 厚度衰减后的底层透射
            Vector3 result = topBRDF + baseBRDF * transmittance2 * thicknessAttenuation;

            return result;
        }

        /// <summary>计算多层（超过2层）材质叠加。</summary>
        /// <param name="layers">材质层列表（从顶到底）。</param>
        /// <param name="normal">法线。</param>
        /// <param name="viewDir">视线方向。</param>
        /// <param name="lightDir">光线方向。</param>
        /// <param name="tangent">切线。</param>
        /// <param name="bitangent">副切线。</param>
        /// <returns>最终颜色。</returns>
        public static Vector3 EvaluateMultiLayer(
            MaterialLayer[] layers, int layerCount,
            Vector3 normal, Vector3 viewDir, Vector3 lightDir,
            Vector3 tangent, Vector3 bitangent)
        {
            if (layerCount <= 0) return Vector3.Zero;
            if (layerCount == 1)
            {
                return DisneyPrincipledBRDF.Evaluate(
                    ref layers[0].Material, normal, viewDir, lightDir,
                    Vector3.Normalize(viewDir + lightDir),
                    Math.Max(0.0f, Vector3.Dot(normal, lightDir)),
                    Math.Max(0.0f, Vector3.Dot(normal, viewDir)),
                    Math.Max(0.0f, Vector3.Dot(normal, Vector3.Normalize(viewDir + lightDir))),
                    Math.Max(0.0f, Vector3.Dot(lightDir, Vector3.Normalize(viewDir + lightDir))),
                    tangent, bitangent);
            }

            // 自底向上逐层叠加
            Vector3 accumulatedColor = Vector3.Zero;
            float totalTransmittance = 1.0f;

            for (int i = 0; i < layerCount; i++)
            {
                float NdotV = Math.Max(0.0f, Vector3.Dot(normal, viewDir));
                float NdotL = Math.Max(0.0f, Vector3.Dot(normal, lightDir));
                Vector3 H = Vector3.Normalize(viewDir + lightDir);
                float NdotH = Math.Max(0.0f, Vector3.Dot(normal, H));
                float LdotH = Math.Max(0.0f, Vector3.Dot(lightDir, H));

                Vector3 layerBRDF = DisneyPrincipledBRDF.Evaluate(
                    ref layers[i].Material, normal, viewDir, lightDir,
                    H, NdotL, NdotV, NdotH, LdotH, tangent, bitangent);

                // 该层Fresnel
                float layerF0 = ((layers[i].LayerIOR - 1.0f) / (layers[i].LayerIOR + 1.0f));
                layerF0 = layerF0 * layerF0;
                float layerFresnel = PBRMath.SchlickFresnel(Math.Max(NdotV, NdotL), layerF0);

                // 该层反射贡献
                accumulatedColor += layerBRDF * totalTransmittance;

                // 累积透射率
                totalTransmittance *= (1.0f - layerFresnel);

                // Beer-Lambert衰减
                Vector3 absorption = Vector3.Lerp(Vector3.One, layers[i].Material.BaseColor, layers[i].Thickness);
                totalTransmittance *= Math.Min(
                    Math.Min(
                        MathF.Exp(-absorption.X * layers[i].Thickness),
                        MathF.Exp(-absorption.Y * layers[i].Thickness)),
                    MathF.Exp(-absorption.Z * layers[i].Thickness));
            }

            return accumulatedColor;
        }
    }

    // ──────────────────────── GPU 常量缓冲 ────────────────────────

    /// <summary>高级PBR材质GPU常量缓冲（与着色器布局严格一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AdvancedPBRCB
    {
        public Vector3 BaseColor;
        public float PerceptualRoughness;
        public float Metallic;
        public float Subsurface;
        public float Sheen;
        public float Clearcoat;
        public Vector3 SubsurfaceColor;
        public float ClearcoatGloss;
        public Vector3 SheenColor;
        public float Anisotropy;
        public float AnisotropyRotation;
        public float ThinFilmThickness;
        public float Transmission;
        public float IOR;
        public float Thickness;
        public float AttenuationDistance;
        public Vector3 ScatterColor;
        public int MaterialType;
        public float ClothWrap;
        public Vector3 IrisColor;
        public float IrisDepth;
        public float LimbalRingIntensity;
        public float CausticIntensity;
        public float Padding1;
        public float Padding2;
    }
}
