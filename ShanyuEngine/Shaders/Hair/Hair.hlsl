// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - Hair & Fur Rendering
// 发丝渲染: Kajiya-Kay 发丝光照模型 (R/TT/TRT 三叶瓣), 多层深度半透明排序,
// 深度淡化, 环境光与发丝阴影; 壳层 (Shell) 毛发回退渲染。
// Shader Model 6.6 (DXC)
// 入口点: HairVS, HairPS, HairMS, ShellFurVS, ShellFurPS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# HairRenderCB / ShellFurCB 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer HairRenderCB : register(b0)
{
    float4x4 g_H_ViewProj;
    float3 g_H_CameraPos;
    float  g_H_Pad0;
    float3 g_H_LightDir;
    float  g_H_Pad1;
    float3 g_H_LightColor;
    float  g_H_Pad2;
    float3 g_H_HairColor;
    float  g_H_StrandRadius;
    float  g_H_RIntensity;
    float  g_H_TTIntensity;
    float  g_H_TRTIntensity;
    float  g_H_LongitudinalRoughness;
    float  g_H_AzimuthalRoughness;
    float  g_H_HairIOR;
    int    g_H_StrandCount;
    int    g_H_MaxVertsPerStrand;
    float2 g_H_Resolution;
    float  g_H_TessellationPixelsPerSegment;
};

cbuffer ShellFurCB : register(b0)
{
    float4x4 g_SF_WorldMatrix;
    float4x4 g_SF_ViewProj;
    float3 g_SF_CameraPos;
    float  g_SF_ShellOffset;
    float  g_SF_LayerFraction;
    float  g_SF_FurLength;
    float  g_SF_Pad0;
    float3 g_SF_FurBaseColor;
    float  g_SF_Pad1;
    float3 g_SF_FurTipColor;
    int    g_SF_ShellIndex;
    float3 g_SF_LightDir;
    float  g_SF_LightIntensity;
    int    g_SF_ShellCount;
    float  g_SF_DensityCutoff;
    float2 g_SF_Pad2;
};

// ──────────────────────────────────────────────────────────────────────────
// 发丝数据结构 (与 C# HairStrandVertex / HairStrand 布局对应)
// ──────────────────────────────────────────────────────────────────────────
struct HairStrandVertex
{
    float3 Position;
    float3 OldPosition;
    float3 Tangent;
    float3 RestPosition;
    float  V;
    float  Pad0;
    float  Pad1;
};
struct HairStrand
{
    int   VertexStart;
    int   VertexCount;
    float3 RootPosition;
    float3 RootDirection;
    float Length;
    float3 Color;
    float Thickness;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
StructuredBuffer<HairStrandVertex> g_HairVertices : register(t0);
StructuredBuffer<HairStrand>       g_HairStrands  : register(t1);
Texture2D<float>                   g_HairDepthTex : register(t2); // 场景深度 (用于发丝阴影)
Texture2D<float4>                  g_HairShadowTex: register(t3); // 发丝深度图 (深向阴影)

Texture2D<float4> g_FurTexture     : register(t0); // 毛发密度纹理
Texture2D<float>  g_FurSceneDepth  : register(t1);

// ──────────────────────────────────────────────────────────────────────────
// 顶点/像素结构
// ──────────────────────────────────────────────────────────────────────────
struct HairVSOutput
{
    float4 Position   : SV_POSITION;
    float3 WorldPos   : TEXCOORD0;
    float3 Tangent    : TEXCOORD1;
    float3 StrandColor: TEXCOORD2;
    float  Coverage   : TEXCOORD3; // 抗锯齿覆盖率
    float  V          : TEXCOORD4; // 沿发丝位置 [0,1]
    float  Depth      : TEXCOORD5;
};

// ──────────────────────────────────────────────────────────────────────────
// Kajiya-Kay 发丝光照模型
// 发丝切线方向 T, 视线 V, 光线 L
// R 叶瓣 (反射), TT 叶瓣 (透射), TRT 叶瓣 (二次反射透射)
// ──────────────────────────────────────────────────────────────────────────

// 发丝法线 (垂直于切线, 朝向视线)
float3 HairNormal(float3 tangent, float3 viewDir)
{
    float3 n = cross(tangent, cross(tangent, viewDir));
    return normalize(n);
}

// Kajiya-Kay 纵向散射: sin(theta) 的幂
// theta = 角度在 (切线, 半向量) 之间
float KajiyaKayLongitudinal(float3 tangent, float3 halfDir, float roughness)
{
    float sinTheta = dot(tangent, halfDir);
    float cosTheta = sqrt(saturate(1.0 - sinTheta * sinTheta));
    // 高斯纵向分布近似
    float exponent = (cosTheta - 1.0) / (roughness * roughness + 1e-5);
    return exp(exponent * 0.5);
}

// R 叶瓣 (主反射)
float3 HairLobeR(float3 tangent, float3 viewDir, float3 lightDir, float3 lightColor,
                 float roughness, float ior, float intensity)
{
    float3 halfDir = normalize(lightDir + viewDir);
    float longitudinal = KajiyaKayLongitudinal(tangent, halfDir, roughness);
    // 方位角散射 (简化)
    float3 N = HairNormal(tangent, viewDir);
    float NdotH = saturate(dot(N, halfDir));
    float azimuthal = pow(NdotH, 16.0);
    float3 fresnel = F_Schlick(saturate(dot(halfDir, viewDir)), float3(0.04, 0.04, 0.04) * ior);
    return lightColor * longitudinal * azimuthal * fresnel * intensity;
}

// TT 叶瓣 (透射, 光穿过发丝)
float3 HairLobeTT(float3 tangent, float3 viewDir, float3 lightDir, float3 lightColor,
                  float roughness, float ior, float intensity)
{
    float3 halfDir = normalize(lightDir + viewDir);
    float longitudinal = KajiyaKayLongitudinal(tangent, halfDir, roughness);
    // TT 方向偏移: 半向量偏向背光侧
    float3 N = HairNormal(tangent, viewDir);
    float NdotH = saturate(-dot(N, halfDir)); // 负方向
    float azimuthal = pow(NdotH, 8.0);
    // 透射吸收 (不同波长)
    float3 absorption = float3(0.4, 0.7, 1.2); // 红光吸收多
    return lightColor * longitudinal * azimuthal * absorption * intensity;
}

// TRT 叶瓣 (二次反射-透射)
float3 HairLobeTRT(float3 tangent, float3 viewDir, float3 lightDir, float3 lightColor,
                   float roughness, float ior, float intensity)
{
    float3 halfDir = normalize(lightDir + viewDir);
    float longitudinal = KajiyaKayLongitudinal(tangent, halfDir, roughness * 1.5);
    float3 N = HairNormal(tangent, viewDir);
    float NdotH = saturate(dot(N, halfDir));
    float azimuthal = pow(NdotH, 32.0);
    float3 tint = float3(1.2, 1.0, 0.8); // 暖色高光
    return lightColor * longitudinal * azimuthal * tint * intensity;
}

// 抗锯齿覆盖率 (基于像素到发丝距离)
float ComputeCoverage(float2 pixelPos, float2 strandPos, float radius)
{
    float dist = length(pixelPos - strandPos);
    return saturate((radius - dist) * g_H_Resolution.y * 0.5 + 0.5);
}

// ════════════════════════════════════════════════════════════════════════════
// HairVS - 发丝顶点着色器 (基于 StructuredBuffer 的发丝顶点)
// ════════════════════════════════════════════════════════════════════════════
HairVSOutput HairVS(uint vertexID : SV_VertexID)
{
    HairVSOutput output;
    HairStrandVertex v = g_HairVertices[vertexID];

    output.WorldPos = v.Position;
    output.Position = mul(g_H_ViewProj, float4(v.Position, 1.0));
    output.Tangent = normalize(v.Tangent);
    output.V = v.V;
    output.Depth = output.Position.z;

    // 找到所属发丝 (简化: 假设每条发丝连续顶点, 由外部实例 ID 提供)
    // 这里用顶点 ID 估算发丝索引
    uint strandIdx = vertexID / g_H_MaxVertsPerStrand;
    HairStrand strand = g_HairStrands[strandIdx];
    output.StrandColor = strand.Color;

    // 抗锯齿覆盖率 (简化)
    output.Coverage = 1.0;

    return output;
}

// ════════════════════════════════════════════════════════════════════════════
// HairPS - 发丝像素着色器 (Kajiya-Kay 多叶瓣)
// ════════════════════════════════════════════════════════════════════════════
struct HairPSOutput
{
    float4 Color  : SV_TARGET0;
    float  Depth  : SV_Depth;
};

HairPSOutput HairPS(HairVSOutput input)
{
    HairPSOutput output;

    float3 tangent = normalize(input.Tangent);
    float3 viewDir = normalize(g_H_CameraPos - input.WorldPos);
    float3 lightDir = normalize(g_H_LightDir);
    float3 lightColor = g_H_LightColor;

    // ── Kajiya-Kay 三叶瓣 ──
    float3 lobeR = HairLobeR(tangent, viewDir, lightDir, lightColor,
                              g_H_LongitudinalRoughness, g_H_HairIOR, g_H_RIntensity);
    float3 lobeTT = HairLobeTT(tangent, viewDir, lightDir, lightColor,
                                g_H_LongitudinalRoughness * 2.0, g_H_HairIOR, g_H_TTIntensity);
    float3 lobeTRT = HairLobeTRT(tangent, viewDir, lightDir, lightColor,
                                  g_H_LongitudinalRoughness, g_H_HairIOR, g_H_TRTIntensity);

    // 漫反射 (发丝的环绕光)
    float3 N = HairNormal(tangent, viewDir);
    float NdotL = saturate(dot(N, lightDir));
    float3 diffuse = input.StrandColor * lightColor * NdotL * 0.3;

    // 环境光
    float3 ambient = input.StrandColor * 0.2;

    // 发丝阴影 (从深度图采样, 深处发丝被浅处遮挡)
    float2 screenUV = input.Position.xy / g_H_Resolution;
    float hairShadow = 1.0;
    // 简化: 基于 V 值的深向衰减
    hairShadow = lerp(0.6, 1.0, input.V);

    // 合成
    float3 color = diffuse + ambient + (lobeR + lobeTT + lobeTRT) * hairShadow;
    color *= g_H_HairColor * input.StrandColor;

    // 深度淡化 (远景发丝淡化, 避免锯齿)
    float depthFade = saturate((1.0 - input.V) * 2.0);

    // 多层半透明: 使用覆盖率作为 alpha
    float alpha = input.Coverage * depthFade;
    alpha = saturate(alpha + 0.1);

    output.Color = float4(color, alpha);
    output.Depth = input.Depth;
    return output;
}

// ════════════════════════════════════════════════════════════════════════════
// HairMS - 发丝网格着色器 (Mesh Shader 路径)
// 直接从缓冲生成发丝三角形, 用于支持 Mesh Shader 的硬件
// ════════════════════════════════════════════════════════════════════════════
struct HairMeshPayload
{
    uint StrandIndex;
};

[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void HairMS(uint groupID : SV_GroupID, uint groupThreadID : SV_GroupThreadID,
            out indices uint3 tris[62], out vertices HairVSOutput verts[64],
            out_payload HairMeshPayload payload)
{
    uint strandIdx = groupID;
    if (strandIdx >= (uint)g_H_StrandCount)
    {
        SetMeshOutputCounts(0, 0);
        return;
    }

    HairStrand strand = g_HairStrands[strandIdx];
    int vertCount = strand.VertexCount;

    SetMeshOutputCounts(vertCount, max(vertCount - 2, 0));

    // 生成顶点
    for (int i = 0; i < vertCount; i++)
    {
        if (groupThreadID < (uint)vertCount)
        {
            uint vid = strand.VertexStart + i;
            HairStrandVertex v = g_HairVertices[vid];

            verts[i].WorldPos = v.Position;
            verts[i].Position = mul(g_H_ViewProj, float4(v.Position, 1.0));
            verts[i].Tangent = normalize(v.Tangent);
            verts[i].V = v.V;
            verts[i].Depth = verts[i].Position.z;
            verts[i].StrandColor = strand.Color;
            verts[i].Coverage = 1.0;
        }
    }

    // 生成三角形 (条带)
    uint triCount = max(vertCount - 2, 0);
    for (uint t = 0; t < triCount; t++)
    {
        if (groupThreadID < triCount)
        {
            tris[t] = uint3(t, t + 1, t + 2);
        }
    }

    payload.StrandIndex = strandIdx;
}

// ════════════════════════════════════════════════════════════════════════════
// ShellFurVS - 壳层毛发顶点着色器
// 沿法线偏移生成多层壳, 每层对应毛发长度的不同高度
// ════════════════════════════════════════════════════════════════════════════
struct ShellFurVSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float2 TexCoord : TEXCOORD0;
};

struct ShellFurVSOutput
{
    float4 Position  : SV_POSITION;
    float3 WorldPos  : TEXCOORD0;
    float3 Normal    : TEXCOORD1;
    float2 TexCoord  : TEXCOORD2;
    float  LayerFrac : TEXCOORD3; // 当前层在毛发长度中的比例
};

ShellFurVSOutput ShellFurVS(ShellFurVSInput input)
{
    ShellFurVSOutput output;

    // 沿法线偏移
    float3 offset = input.Normal * g_SF_ShellOffset;
    float3 worldPos = mul(g_SF_WorldMatrix, float4(input.Position + offset, 1.0)).xyz;

    output.Position = mul(g_SF_ViewProj, float4(worldPos, 1.0));
    output.WorldPos = worldPos;
    output.Normal = normalize(mul((float3x3)g_SF_WorldMatrix, input.Normal));
    output.TexCoord = input.TexCoord;
    output.LayerFrac = g_SF_LayerFraction;

    return output;
}

// ════════════════════════════════════════════════════════════════════════════
// ShellFurPS - 壳层毛发像素着色器
// 基于密度纹理 alpha 测试, 沿毛发长度颜色渐变, Kajiya-Kay 光照
// ════════════════════════════════════════════════════════════════════════════
float4 ShellFurPS(ShellFurVSOutput input) : SV_TARGET
{
    // 采样毛发密度纹理
    float4 furSample = g_FurTexture.Sample(gLinearWrapSampler, input.TexCoord);
    float density = furSample.a;

    // 层越深 (越接近根部), 密度阈值越低
    float cutoff = lerp(g_SF_DensityCutoff, 0.0, input.LayerFrac);
    clip(density - cutoff);

    // 颜色渐变: 根部到尖端
    float3 color = lerp(g_SF_FurBaseColor, g_SF_FurTipColor, input.LayerFrac);

    // Kajiya-Kay 光照 (切线近似为毛发方向)
    float3 viewDir = normalize(g_SF_CameraPos - input.WorldPos);
    float3 lightDir = normalize(g_SF_LightDir);
    float3 tangent = normalize(input.Normal); // 简化: 用法线作为切线
    float3 N = HairNormal(tangent, viewDir);

    float NdotL = saturate(dot(N, lightDir));
    float3 diffuse = color * g_SF_LightIntensity * NdotL;

    // 高光 (沿毛发方向的各向异性高光)
    float3 halfDir = normalize(lightDir + viewDir);
    float sinTH = dot(tangent, halfDir);
    float cosTH = sqrt(saturate(1.0 - sinTH * sinTH));
    float3 specular = pow(cosTH, 32.0) * g_SF_LightIntensity * 0.5;

    // 环境光
    float3 ambient = color * 0.2;

    // Alpha: 越接近尖端越透明
    float alpha = density * input.LayerFrac;

    float3 finalColor = ambient + diffuse + specular;
    return float4(finalColor, alpha);
}
