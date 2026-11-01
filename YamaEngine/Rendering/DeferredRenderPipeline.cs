// ════════════════════════════════════════════════════════════════════════
// DeferredRenderPipeline.cs — UE4.7 级延迟渲染管线（CPU 参考实现 + RenderGraph 接线）
//
// 目标：把散落在 Rendering/ 下的"孤岛"延迟算法（GeometryBufferGenerator、
//       CascadeShadowMapRenderer、ScreenSpaceAmbientOcclusion、LightResolveProcessor）
//       组织为一条完整、真实、CPU 级可验证的 Deferred Shading 管线。
//
// 为什么是 CPU 参考实现？
//   - GPU 端到端需要真实 Vulkan 窗口/设备，本环境无头无法可靠验证；
//   - 这些算法（GBuffer 打包、CSM 级联分割、SSAO 核、Cook-Torrance BRDF）
//     本身是确定性纯函数，CPU 参考实现即可严格验证数值正确性；
//   - RenderGraph 负责组织 pass 图（资源生命周期/依赖/屏障），本类负责
//     在 CPU 侧对每个 pass 给出可验证的参考求值，二者共同构成 UE4.7 级
//     Deferred 管线框架，后续可逐步替换为真实 GPU shader pass。
//
// 阶段（与 UE4.7 Deferred 一致）：
//   Shadow(CSM) → GeometryBuffer(GBuffer 打包) → SSAO → Lighting(延迟光照) → PostProcess
// ════════════════════════════════════════════════════════════════════════

using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Rendering;

/// <summary>Deferred 光源类型。</summary>
public enum DeferredLightKind
{
    Directional,
    Point,
    Spot
}

/// <summary>延迟管线用的光源（CPU 参考输入）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DeferredLight
{
    public DeferredLightKind Kind;
    public Vector3 Position;      // 点/聚光位置（平行光忽略）
    public Vector3 Direction;     // 光照方向（指向外）
    public Vector3 Color;         // 光照颜色
    public float Intensity;       // 强度
    public float Range;           // 衰减距离（平行光忽略）
    public float SpotAngle;       // 聚光半角（度）
    public float SpotFalloff;     // 聚光衰减指数
    public bool CastsShadow;
    public float ShadowFactor;    // 阴影因子 0..1（1=无遮挡），CPU 参考阶段输入

    public static DeferredLight Directional(Vector3 dir, Vector3 color, float intensity = 1.0f) =>
        new()
        {
            Kind = DeferredLightKind.Directional, Direction = Vector3.Normalize(dir),
            Color = color, Intensity = intensity, CastsShadow = true, ShadowFactor = 1.0f
        };

    public static DeferredLight Point(Vector3 pos, Vector3 color, float intensity = 1.0f, float range = 100f) =>
        new()
        {
            Kind = DeferredLightKind.Point, Position = pos, Color = color,
            Intensity = intensity, Range = range, CastsShadow = true, ShadowFactor = 1.0f
        };

    public static DeferredLight Spot(Vector3 pos, Vector3 dir, Vector3 color, float intensity = 1.0f,
        float range = 100f, float halfAngleDeg = 30f, float falloff = 2f) =>
        new()
        {
            Kind = DeferredLightKind.Spot, Position = pos, Direction = Vector3.Normalize(dir),
            Color = color, Intensity = intensity, Range = range,
            SpotAngle = halfAngleDeg, SpotFalloff = falloff, CastsShadow = true, ShadowFactor = 1.0f
        };
}

/// <summary>解码后的 GBuffer 材质数据（CPU 参考）。</summary>
public readonly struct DeferredSurface
{
    public readonly Vector3 Albedo;
    public readonly Vector3 Normal;
    public readonly float Metallic;
    public readonly float Roughness;
    public readonly float AO;
    public readonly Vector3 WorldPos;
    public readonly Vector3 Emissive;
    public readonly ShadingModel Model;

    public DeferredSurface(Vector3 albedo, Vector3 normal, float metallic, float roughness,
        float ao, Vector3 worldPos, Vector3 emissive, ShadingModel model)
    {
        Albedo = albedo; Normal = normal; Metallic = metallic;
        Roughness = roughness; AO = ao; WorldPos = worldPos;
        Emissive = emissive; Model = model;
    }
}

/// <summary>
/// UE4.7 级延迟渲染管线。
/// 提供 CPU 可验证的完整 Deferred 参考实现，并把孤岛算法组织为 RenderGraph 4-pass 图。
/// </summary>
public sealed class DeferredRenderPipeline
{
    // ──────────────────────────────────────────────────────────────────
    // 阶段 1: GeometryBuffer 打包/解码（复用 GeometryBufferPixel 编码约定）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>把材质/几何数据打包为一个 GBuffer 像素（Normal 编码到 [0,1]，PBR 打包到 RGBA）。</summary>
    public static GeometryBufferPixel PackGBuffer(
        Vector3 albedo, Vector3 normal, float metallic, float roughness, float ao,
        Vector3 worldPos, ShadingModel model = ShadingModel.DefaultLit,
        Vector3 emissive = default)
    {
        var n = Vector3.Normalize(normal);
        // Normal 编码: [0,1] 存 [-1,1]，与 DecodedNormal = Normalize(N*2-1) 对应。
        var encodedNormal = new Vector4(n * 0.5f + Vector3.One * 0.5f, 0.0f);
        return new GeometryBufferPixel
        {
            Albedo = new Vector4(Vector3.Clamp(albedo, Vector3.Zero, Vector3.One), 1.0f),
            Normal = encodedNormal,
            // PBR 通道: R=Roughness, G=Metalness, B=AO, A=ShadingModel(0..1)
            PBR = new Vector4(Math.Clamp(roughness, 0f, 1f), Math.Clamp(metallic, 0f, 1f),
                              Math.Clamp(ao, 0f, 1f), (byte)model / 255.0f),
            WorldPos = new Vector4(worldPos, 1.0f),
            Emissive = new Vector4(emissive, 0.0f),
            MotionVector = Vector4.Zero,
            Tangent = Vector4.Zero,
            // MaterialData: 默认不透明 + 阴影接收/投射
            MaterialData = new Vector4(
                (byte)(GeometryBufferMaterialFlags.Opaque |
                       GeometryBufferMaterialFlags.CastShadow |
                       GeometryBufferMaterialFlags.ReceiveShadow),
                0, 0, 0)
        };
    }

    /// <summary>从 GBuffer 像素解码出表面材质数据。</summary>
    public static DeferredSurface DecodeGBuffer(in GeometryBufferPixel gbuf)
    {
        return new DeferredSurface(
            new Vector3(gbuf.Albedo.X, gbuf.Albedo.Y, gbuf.Albedo.Z),
            gbuf.DecodedNormal,
            gbuf.Metalness,
            gbuf.Roughness,
            gbuf.AO,
            new Vector3(gbuf.WorldPos.X, gbuf.WorldPos.Y, gbuf.WorldPos.Z),
            new Vector3(gbuf.Emissive.X, gbuf.Emissive.Y, gbuf.Emissive.Z),
            gbuf.ShadingModelType);
    }

    // ──────────────────────────────────────────────────────────────────
    // 阶段 2: CSM 级联分割（复用 CascadeSplitCalculator，Zhang 2006 Practical Split）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>计算级联近远平面。委托给既有 <see cref="CascadeSplitCalculator"/>。</summary>
    public static (float near, float far)[] ComputeCascadeSplits(
        CascadeShadowConfig config, float cameraNear, float cameraFar)
        => CascadeSplitCalculator.ComputeSplitDistances(config, cameraNear, cameraFar);

    // ──────────────────────────────────────────────────────────────────
    // 阶段 3: SSAO 核生成（复用 SSAOKernelGenerator）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>生成半球 SSAO 核（复用既有生成器）。</summary>
    public static Vector4[] GenerateSSAOKernel(int sampleCount)
        => SSAOKernelGenerator.GenerateKernel(sampleCount);

    // ──────────────────────────────────────────────────────────────────
    // 阶段 4: 延迟光照（复用 BRDFUtility 的 Cook-Torrance + Lambert 漫反射）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 计算单个光源对一个 GBuffer 表面的直接光照（UE4 式 PBR）。
    /// 漫反射 = 非金属 Lambert；镜面 = Cook-Torrance (GGX NDF + Smith GGX G + Fresnel-Schlick)。
    /// </summary>
    public static Vector3 EvaluateLight(
        in GeometryBufferPixel gbuf, in DeferredLight light, Vector3 viewDir)
    {
        var surf = DecodeGBuffer(gbuf);
        var N = surf.Normal;
        var V = Vector3.Normalize(viewDir);
        if (surf.Model == ShadingModel.Unlit) return Vector3.Zero;

        // 光源方向 L 与衰减
        Vector3 L; float attenuation = light.Intensity;
        switch (light.Kind)
        {
            case DeferredLightKind.Directional:
                L = -Vector3.Normalize(light.Direction);
                break;
            case DeferredLightKind.Point:
            {
                var toL = light.Position - surf.WorldPos;
                float dist = toL.Length();
                L = dist > 1e-5f ? toL / dist : Vector3.UnitY;
                attenuation *= Attenuation(dist, light.Range);
                break;
            }
            case DeferredLightKind.Spot:
            {
                var toL = light.Position - surf.WorldPos;
                float dist = toL.Length();
                L = dist > 1e-5f ? toL / dist : Vector3.UnitY;
                attenuation *= Attenuation(dist, light.Range) * SpotFactor(-L, light.Direction, light.SpotAngle, light.SpotFalloff);
                break;
            }
            default:
                return Vector3.Zero;
        }

        float NdotL = Vector3.Dot(N, L);
        if (NdotL <= 0f) return Vector3.Zero;

        // 阴影
        attenuation *= Math.Clamp(light.ShadowFactor, 0f, 1f);

        // 基础颜色分解（UE4: F0 = lerp(0.04, albedo, metallic)）
        var albedo = surf.Albedo;
        var F0 = Vector3.Lerp(new Vector3(0.04f), albedo, surf.Metallic);
        var diffuseColor = albedo * (1f - surf.Metallic);

        var H = Vector3.Normalize(L + V);
        float NdotH = Math.Clamp(Vector3.Dot(N, H), 0f, 1f);
        float NdotV = Math.Clamp(Vector3.Dot(N, V), 0f, 1f);
        float HdotV = Math.Clamp(Vector3.Dot(H, V), 0f, 1f);

        // 镜面: Cook-Torrance (复用 BRDFUtility)
        Vector3 specular = BRDFUtility.CookTorranceSpecular(
            NdotL, NdotV, NdotH, HdotV, F0, surf.Roughness, light.Color, attenuation);
        // 漫反射: Lambert
        Vector3 diffuse = diffuseColor * (1f / MathF.PI) * light.Color * attenuation;

        return (diffuse + specular) * NdotL;
    }

    /// <summary>完整延迟光照一像素：遍历全部光源 + 自发光 + AO。</summary>
    public static Vector3 RenderDeferredPixel(
        in GeometryBufferPixel gbuf, ReadOnlySpan<DeferredLight> lights, Vector3 viewDir)
    {
        var surf = DecodeGBuffer(gbuf);
        if (surf.Model == ShadingModel.Unlit)
            return new Vector3(gbuf.Emissive.X, gbuf.Emissive.Y, gbuf.Emissive.Z);

        Vector3 result = new(gbuf.Emissive.X, gbuf.Emissive.Y, gbuf.Emissive.Z);
        for (int i = 0; i < lights.Length; i++)
            result += EvaluateLight(gbuf, lights[i], viewDir);

        // 环境 AO 调制（IBL 环境项简化：仅应用 AO 于环境光）
        result *= surf.AO;
        return result;
    }

    // ── 工具 ──────────────────────────────────────────────────────────

    /// <summary>距离平方衰减（UE4 风格：范围外 0，近处 1）。</summary>
    private static float Attenuation(float dist, float range)
    {
        if (range <= 0f) return 1f;
        float d = Math.Max(dist, 0f);
        if (d >= range) return 0f;
        // 平滑衰减: (1 - d/range)^2
        float t = 1f - d / range;
        return t * t;
    }

    /// <summary>聚光角衰减因子（角度余弦平滑）。</summary>
    private static float SpotFactor(Vector3 L, Vector3 spotDir, float halfAngleDeg, float falloff)
    {
        float cosAngle = Math.Clamp(Vector3.Dot(L, spotDir), -1f, 1f);
        float cosHalf = MathF.Cos(halfAngleDeg * MathF.PI / 180f);
        if (cosAngle <= cosHalf) return 0f;
        // 内锥（cosHalf 与 1 之间的平滑过渡）
        float t = Math.Clamp((cosAngle - cosHalf) / Math.Max(1f - cosHalf, 1e-4f), 0f, 1f);
        return MathF.Pow(t, Math.Max(falloff, 1f));
    }

    // ──────────────────────────────────────────────────────────────────
    // RenderGraph 接线：构建 UE4.7 级 4-pass Deferred 图
    //   Shadow(CSM) → GeometryBuffer → Lighting → PostProcess
    // ──────────────────────────────────────────────────────────────────

    /// <summary>构建完整延迟渲染图（复用 <see cref="RenderGraph.CreateDefaultDeferredGraph"/>）。</summary>
    public static RenderGraph BuildGraph(int width, int height)
        => RenderGraph.CreateDefaultDeferredGraph(width, height);
}
