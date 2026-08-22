// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - Eye Rendering
// 眼球渲染: 角膜折射 (光线穿过角膜弯曲虹膜), 虹膜程序化纤维纹理与色素沉着,
// 瞳孔收缩, 巩膜血管程序化噪声, 泪液层菲涅尔高光, SSS 皮肤散射近似,
// 眨眼形变, 简化 LOD 回退。
// 与 C# EyeRenderer.EyeShaderConstants 常量布局对应。
// Shader Model 6.6 (DXC)
// 入口点: EyeVS, EyePS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# EyeShaderConstants 对应, Pack=16, b1 对象槽)
// ──────────────────────────────────────────────────────────────────────────
cbuffer EyeConstantsCB : register(b1)
{
    float4x4 g_Eye_WorldMatrix;
    float3   g_Eye_IrisColor;
    float    g_Eye_PupilRadius;
    float    g_Eye_IrisRadius;
    float3   g_Eye_ScleraBaseColor;
    float3   g_Eye_ScleraVeinColor;
    float    g_Eye_ScleraVeinDensity;
    float    g_Eye_SSSCoeff;
    float    g_Eye_TearFilmReflectance;
    float    g_Eye_CorneaEtaRatio;
    float    g_Eye_IrisDepth;
    float    g_Eye_CorneaCurvature;
    float    g_Eye_RefractionStrength;
    float    g_Eye_BlinkAmount;
    float    g_Eye_SimplifiedLOD;
    float    g_Eye_IrisPigmentation;
    float    g_Eye_IrisFiberDetail;
};

// 光照常量 (b2)
cbuffer EyeLightCB : register(b2)
{
    float3 g_Eye_LightDir;
    float  g_Eye_LightIntensity;
    float3 g_Eye_LightColor;
    float  g_Eye_AmbientIntensity;
    float3 g_Eye_CameraPos;
    float  g_Eye_Pad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4> g_IrisTexture   : register(t0); // 虹膜纹理 (可选)
Texture2D<float4> g_ScleraTexture : register(t1); // 巩膜纹理 (可选)
Texture2D<float>  g_EyeSceneDepth : register(t2); // 场景深度 (软遮挡)

// ──────────────────────────────────────────────────────────────────────────
// 顶点/像素结构
// ──────────────────────────────────────────────────────────────────────────
// 眼球顶点: 局部 UV (球面映射), 法线, 切线, 部位标志
//   部位标志: 0=巩膜, 1=角膜, 2=虹膜, 3=瞳孔
struct EyeVSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float2 TexCoord : TEXCOORD0;
    float  PartID   : TEXCOORD1; // 部位标志
};

struct EyeVSOutput
{
    float4 Position : SV_POSITION;
    float3 WorldPos : TEXCOORD0;
    float3 Normal   : TEXCOORD1;
    float2 TexCoord : TEXCOORD2;
    float  PartID   : TEXCOORD3;
    float3 ViewDir  : TEXCOORD4;
};

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 菲涅尔 (Schlick)
float EyeFresnel(float cosTheta, float F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - saturate(cosTheta), 5.0);
}

// 程序化虹膜纤维纹理 (放射状纤维)
// 基于 UV 极坐标, 产生虹膜的纤维状细节
float3 ProceduralIris(float2 uv, float pupilRadius, float irisRadius,
                      float3 baseColor, float pigmentation, float fiberDetail)
{
    // 转极坐标
    float2 centered = uv - 0.5;
    float r = length(centered) * 2.0; // 0..1
    float angle = atan2(centered.y, centered.x);

    // 归一化到虹膜范围
    float radial = (r - pupilRadius) / max(irisRadius - pupilRadius, 0.001);
    radial = saturate(radial);

    // 放射状纤维: 高频噪声沿径向
    float fiber = sin(angle * (64.0 * fiberDetail) + hash1(radial * 100.0) * 6.28) * 0.5 + 0.5;
    fiber *= sin(radial * 30.0) * 0.5 + 0.5;

    // 色素沉着: 随机斑点
    float pigmentationNoise = value_noise(float3(uv * 40.0, 0.0));
    float pigment = smoothstep(0.4, 0.7, pigmentationNoise) * pigmentation;

    // 虹膜颜色: 外圈深, 内圈亮
    float3 darkRing = baseColor * 0.5;
    float3 lightCenter = baseColor * 1.3;
    float3 irisColor = lerp(lightCenter, darkRing, smoothstep(0.0, 0.8, radial));

    // 添加纤维细节
    irisColor *= 0.85 + fiber * 0.3;
    // 添加色素
    irisColor = lerp(irisColor, float3(0.9, 0.7, 0.4), pigment * 0.4);

    // 虹膜外缘暗化 (limbal ring)
    float limbalRing = smoothstep(0.85, 1.0, radial);
    irisColor *= 1.0 - limbalRing * 0.6;

    return irisColor;
}

// 程序化巩膜血管
float3 ProceduralScleraVeins(float2 uv, float3 baseColor, float3 veinColor, float density)
{
    // 血管方向多沿水平方向 (从内眼角/外眼角向虹膜)
    float2 veinUV = uv * float2(20.0, 8.0);

    // 多层噪声模拟血管分支
    float veins = 0.0;
    veins += smoothstep(0.7, 0.75, fbm(float3(veinUV, 0.0), 3)) * 0.6;
    veins += smoothstep(0.8, 0.85, fbm(float3(veinUV * 2.0, 1.0), 3)) * 0.4;
    veins += smoothstep(0.85, 0.9, fbm(float3(veinUV * 4.0, 2.0), 2)) * 0.3;

    veins *= density;

    // 距离虹膜越远血管越少
    float distToIris = length(uv - 0.5);
    float irisFade = smoothstep(0.15, 0.35, distToIris);
    veins *= irisFade;

    return lerp(baseColor, veinColor, veins);
}

// 角膜折射: 将视线方向折射穿过角膜, 计算实际击中虹膜的位置
// 使用 Snell 定律, 角膜曲率影响折射强度
float2 CorneaRefraction(float2 uv, float3 viewDir, float3 normal,
                        float etaRatio, float curvature, float strength)
{
    // 视线在角膜表面的入射角
    float cosTheta = saturate(dot(-viewDir, normal));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    // Snell 折射
    float sinRefracted = sinTheta * etaRatio;
    float cosRefracted = sqrt(saturate(1.0 - sinRefracted * sinRefracted));

    // 折射方向在切平面上的投影 (影响虹膜采样偏移)
    float3 refractDir = refract(-viewDir, normal, etaRatio);

    // 偏移量与曲率和折射强度相关
    float2 offset = refractDir.xy * curvature * strength * 0.15;

    return uv + offset;
}

// ════════════════════════════════════════════════════════════════════════════
// EyeVS - 眼球顶点着色器
// 应用世界变换, 眨眼形变 (上眼皮压下), 传递视线方向
// ════════════════════════════════════════════════════════════════════════════
EyeVSOutput EyeVS(EyeVSInput input)
{
    EyeVSOutput output;

    float3 pos = input.Position;

    // 眨眼形变: 上半部分顶点向下压 (模拟上眼皮闭合)
    if (g_Eye_BlinkAmount > 0.001 && pos.y > 0.0)
    {
        // 距离中心越远的上部顶点压得越多
        float blinkFactor = smoothstep(0.0, 0.5, pos.y);
        pos.y -= blinkFactor * g_Eye_BlinkAmount * 0.8;
        // Z 方向轻微回缩
        pos.z += blinkFactor * g_Eye_BlinkAmount * 0.1;
    }

    float3 worldPos = mul(g_Eye_WorldMatrix, float4(pos, 1.0)).xyz;
    output.Position = mul(gViewProjMatrix, float4(worldPos, 1.0));
    output.WorldPos = worldPos;
    output.Normal = normalize(mul((float3x3)g_Eye_WorldMatrix, input.Normal));
    output.TexCoord = input.TexCoord;
    output.PartID = input.PartID;
    output.ViewDir = normalize(g_Eye_CameraPos - worldPos);

    return output;
}

// ════════════════════════════════════════════════════════════════════════════
// EyePS - 眼球像素着色器
// 分部位渲染: 巩膜 (血管) / 角膜 (折射+泪液高光) / 虹膜 (纤维) / 瞳孔 (吸收)
// ════════════════════════════════════════════════════════════════════════════
float4 EyePS(EyeVSOutput input) : SV_TARGET
{
    float3 normal = normalize(input.Normal);
    float3 viewDir = normalize(input.ViewDir);
    float3 lightDir = normalize(g_Eye_LightDir);
    float2 uv = input.TexCoord;
    float partID = input.PartID;

    float3 color = 0.0;
    float alpha = 1.0;

    // ── 简化 LOD: 直接采样纹理, 跳过程序化细节 ──
    if (g_Eye_SimplifiedLOD > 0.5)
    {
        float3 albedo = g_ScleraTexture.Sample(gLinearWrapSampler, uv).rgb;
        float NdotL = saturate(dot(normal, lightDir));
        color = albedo * g_Eye_LightColor * (NdotL * g_Eye_LightIntensity + g_Eye_AmbientIntensity);
        // 简化泪液高光
        float3 halfDir = normalize(lightDir + viewDir);
        float NdotH = saturate(dot(normal, halfDir));
        color += g_Eye_TearFilmReflectance * pow(NdotH, 64.0) * g_Eye_LightColor;
        return float4(color, alpha);
    }

    // ── 按部位分支渲染 ──
    // 巩膜 (PartID == 0)
    if (partID < 0.5)
    {
        float3 sclera = g_Eye_ScleraBaseColor;
        // 程序化血管
        sclera = ProceduralScleraVeins(uv, sclera, g_Eye_ScleraVeinColor, g_Eye_ScleraVeinDensity);

        // 漫反射 (环绕光, 模拟 SSS)
        float wrap = 0.5;
        float NdotL = saturate((dot(normal, lightDir) + wrap) / (1.0 + wrap));
        float3 diffuse = sclera * g_Eye_LightColor * NdotL * g_Eye_LightIntensity;

        // SSS 近似 (背光散射)
        float backScatter = saturate(-dot(normal, lightDir)) * 0.3;
        diffuse += sclera * g_Eye_LightColor * backScatter * g_Eye_SSSCoeff;

        // 环境光
        float3 ambient = sclera * g_Eye_AmbientIntensity * 0.5;

        // 泪液层高光 (湿润表面)
        float3 halfDir = normalize(lightDir + viewDir);
        float NdotH = saturate(dot(normal, halfDir));
        float fresnel = EyeFresnel(saturate(dot(normal, viewDir)), 0.02);
        float3 specular = g_Eye_TearFilmReflectance * pow(NdotH, 128.0) * g_Eye_LightColor * fresnel;

        color = diffuse + ambient + specular;
    }
    // 角膜 (PartID == 1) - 透明折射层, 显示后方虹膜
    else if (partID < 1.5)
    {
        // 角膜是透明的, 主要贡献是折射偏移与泪液高光
        // 折射采样虹膜位置
        float2 refractedUV = CorneaRefraction(uv, viewDir, normal,
                                              g_Eye_CorneaEtaRatio,
                                              g_Eye_CorneaCurvature,
                                              g_Eye_RefractionStrength);

        // 从折射后的 UV 重建虹膜极坐标
        float2 centered = refractedUV - 0.5;
        float r = length(centered) * 2.0;

        // 判断折射后击中虹膜还是瞳孔
        float3 irisColor = 0.0;
        if (r < g_Eye_PupilRadius)
        {
            // 瞳孔: 几乎全吸收 (黑色)
            irisColor = float3(0.02, 0.02, 0.03);
        }
        else if (r < g_Eye_IrisRadius)
        {
            // 虹膜: 程序化纤维
            irisColor = ProceduralIris(refractedUV, g_Eye_PupilRadius,
                                       g_Eye_IrisRadius, g_Eye_IrisColor,
                                       g_Eye_IrisPigmentation, g_Eye_IrisFiberDetail);
            // 虹膜深度阴影 (近瞳孔处暗)
            float depthShadow = smoothstep(g_Eye_PupilRadius, g_Eye_PupilRadius + 0.05, r);
            irisColor *= 0.7 + depthShadow * 0.3;
        }
        else
        {
            // 折射后落在巩膜上
            irisColor = ProceduralScleraVeins(refractedUV, g_Eye_ScleraBaseColor,
                                              g_Eye_ScleraVeinColor, g_Eye_ScleraVeinDensity);
        }

        // 虹膜光照
        float irisNdotL = saturate(dot(normal, lightDir));
        irisColor *= g_Eye_LightColor * (irisNdotL * 0.5 + 0.6);

        // 泪液层: 强菲涅尔高光 (角膜反射环境光)
        float NdotV = saturate(dot(normal, viewDir));
        float fresnel = EyeFresnel(NdotV, 0.02);
        float3 halfDir = normalize(lightDir + viewDir);
        float NdotH = saturate(dot(normal, halfDir));
        float3 specular = g_Eye_TearFilmReflectance * pow(NdotH, 256.0) * g_Eye_LightColor;
        // 第二组宽高光 (泪液漫反射)
        specular += g_Eye_TearFilmReflectance * 0.3 * pow(NdotH, 32.0) * g_Eye_LightColor;

        // 角膜曲率高光偏移 (高光位置随曲率偏移)
        float3 curvatureOffset = normal * g_Eye_CorneaCurvature * 0.1;

        color = irisColor + specular * fresnel;
        // 角膜略带蓝色调 (透明组织)
        color += float3(0.02, 0.03, 0.05) * (1.0 - NdotV);
    }
    // 虹膜 (PartID == 2) - 直接渲染 (非折射路径)
    else if (partID < 2.5)
    {
        float3 iris = ProceduralIris(uv, g_Eye_PupilRadius, g_Eye_IrisRadius,
                                     g_Eye_IrisColor, g_Eye_IrisPigmentation,
                                     g_Eye_IrisFiberDetail);
        float NdotL = saturate(dot(normal, lightDir));
        iris *= g_Eye_LightColor * (NdotL * 0.6 + 0.5);
        // 虹膜表面也有轻微高光
        float3 halfDir = normalize(lightDir + viewDir);
        float NdotH = saturate(dot(normal, halfDir));
        iris += g_Eye_TearFilmReflectance * 0.2 * pow(NdotH, 64.0) * g_Eye_LightColor;
        color = iris;
    }
    // 瞳孔 (PartID == 3) - 吸收
    else
    {
        color = float3(0.02, 0.02, 0.03);
    }

    // ── 共通: 环境光 ──
    color += g_Eye_AmbientIntensity * 0.15;

    return float4(color, alpha);
}
