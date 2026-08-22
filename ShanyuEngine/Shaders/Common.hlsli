// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine — Common Shader Library
// 通用着色器库: 全局常量、数学工具、BRDF、打包/解包、颜色空间
// 目标: Shader Model 6.6+ (DXC 编译)
// ════════════════════════════════════════════════════════════════════════════

#ifndef SHANYU_COMMON_HLSLI
#define SHANYU_COMMON_HLSLI

// ──────────────────────────────────────────────────────────────────────────
// 全局编译宏
// ──────────────────────────────────────────────────────────────────────────
#define SHANYU_PI           3.14159265358979323846
#define SHANYU_INV_PI       0.31830988618379067154
#define SHANYU_TWO_PI       6.28318530717958647692
#define SHANYU_INV_TWO_PI   0.15915494309189533577
#define SHANYU_HALF_PI      1.57079632679489661923
#define SHANYU_INV_HALF_PI  0.63661977236758134308
#define SHANYU_LOG2_E       1.44269504088896340736
#define SHANYU_GOLDEN_RATIO 1.61803398874989484820

#define SHANYU_EPSILON      1e-6
#define SHANYU_FLT_MAX      3.402823466e+38
#define SHANYU_FLT_MIN      1.175494351e-38

// ──────────────────────────────────────────────────────────────────────────
// 全局帧常量 (绑定到 b0, 每帧更新)
// ──────────────────────────────────────────────────────────────────────────
cbuffer FrameConstants : register(b0)
{
    // 摄像机
    float4x4 gViewMatrix;
    float4x4 gProjMatrix;
    float4x4 gViewProjMatrix;
    float4x4 gInvViewMatrix;
    float4x4 gInvProjMatrix;
    float4x4 gInvViewProjMatrix;
    float4x4 gPrevViewProjMatrix;
    float4x4 gPrevViewMatrix;
    float4   gCameraPos;        // .w = nearZ
    float4   gCameraForward;    // .w = farZ
    float4   gCameraParams;     // x=fov, y=aspect, z=near, w=far
    float4   gScreenParams;     // x=width, y=height, z=1/width, w=1/height
    float4   gTimeParams;       // x=time, y=deltaTime, z=frames, w=sin(time)
    float4   gJitterParams;     // xy=current jitter, zw=previous jitter

    // 雾和大气
    float4   gFogColor;
    float4   gFogParams;        // x=density, y=start, z=end, w=heightFalloff

    // 渲染标志
    uint     gRenderFlags;      // bit flags
    uint     gFrameCount;
    float    gExposure;
    float    gPadding0;
};

// ──────────────────────────────────────────────────────────────────────────
// 对象常量 (绑定到 b1, 每绘制对象更新)
// ──────────────────────────────────────────────────────────────────────────
cbuffer ObjectConstants : register(b1)
{
    float4x4 gWorldMatrix;
    float4x4 gWorldInvTranspose;
    float4x4 gPrevWorldMatrix;
    float4   gObjectParams;     // x=LOD, y=distance, z=_, w=_
    uint     gObjectID;
    uint     gMaterialID;
    uint     gPadding1;
    uint     gPadding2;
};

// ──────────────────────────────────────────────────────────────────────────
// 数学工具
// ──────────────────────────────────────────────────────────────────────────

// 限制
float saturate_fast(float x) { return max(0.0, min(1.0, x)); }
float2 saturate_fast(float2 x) { return max(0.0, min(1.0, x)); }
float3 saturate_fast(float3 x) { return max(0.0, min(1.0, x)); }
float4 saturate_fast(float4 x) { return max(0.0, min(1.0, x)); }

// 平滑混合
float smoothstep_edge(float edge0, float edge1, float x)
{
    float t = saturate_fast((x - edge0) / (edge1 - edge0));
    return t * t * (3.0 - 2.0 * t);
}

// 伪随机哈希
float hash1(float n) { return frac(sin(n) * 43758.5453123); }
float2 hash2(float2 p)
{
    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
    return frac(sin(p) * 43758.5453123);
}
float3 hash3(float3 p)
{
    p = float3(dot(p, float3(127.1, 311.7, 74.7)),
               dot(p, float3(269.5, 183.3, 246.1)),
               dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p) * 43758.5453123);
}

// 基于格的噪声 (value noise)
float value_noise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float n000 = hash1(dot(i + float3(0,0,0), float3(1.0, 57.0, 113.0)));
    float n100 = hash1(dot(i + float3(1,0,0), float3(1.0, 57.0, 113.0)));
    float n010 = hash1(dot(i + float3(0,1,0), float3(1.0, 57.0, 113.0)));
    float n110 = hash1(dot(i + float3(1,1,0), float3(1.0, 57.0, 113.0)));
    float n001 = hash1(dot(i + float3(0,0,1), float3(1.0, 57.0, 113.0)));
    float n101 = hash1(dot(i + float3(1,0,1), float3(1.0, 57.0, 113.0)));
    float n011 = hash1(dot(i + float3(0,1,1), float3(1.0, 57.0, 113.0)));
    float n111 = hash1(dot(i + float3(1,1,1), float3(1.0, 57.0, 113.0)));

    float n00 = lerp(n000, n100, f.x);
    float n10 = lerp(n010, n110, f.x);
    float n01 = lerp(n001, n101, f.x);
    float n11 = lerp(n011, n111, f.x);
    float n0 = lerp(n00, n10, f.y);
    float n1 = lerp(n01, n11, f.y);
    return lerp(n0, n1, f.z);
}

// 分形布朗运动
float fbm(float3 p, int octaves = 5)
{
    float value = 0.0;
    float amplitude = 0.5;
    float frequency = 1.0;
    for (int i = 0; i < octaves; i++)
    {
        value += amplitude * value_noise(p * frequency);
        frequency *= 2.0;
        amplitude *= 0.5;
    }
    return value;
}

// 重建世界位置从深度
float3 ReconstructWorldPos(float2 uv, float depth)
{
    float4 clipPos = float4(uv * 2.0 - 1.0, depth, 1.0);
    clipPos.y = -clipPos.y; // 翻转Y
    float4 worldPos = mul(gInvViewProjMatrix, clipPos);
    return worldPos.xyz / worldPos.w;
}

// 重建视图位置从深度
float3 ReconstructViewPos(float2 uv, float depth)
{
    float4 clipPos = float4(uv * 2.0 - 1.0, depth, 1.0);
    clipPos.y = -clipPos.y;
    float4 viewPos = mul(gInvProjMatrix, clipPos);
    return viewPos.xyz / viewPos.w;
}

// 线性深度
float LinearizeDepth(float depth, float near, float far)
{
    return (near * far) / (far - depth * (far - near));
}

// 屏幕UV -> 像素坐标
float2 ScreenUVToPixel(float2 uv)
{
    return uv * gScreenParams.xy;
}
float2 PixelToScreenUV(float2 pixel)
{
    return pixel * gScreenParams.zw;
}

// ──────────────────────────────────────────────────────────────────────────
// 打包/解包工具 (G-Buffer 编码)
// ──────────────────────────────────────────────────────────────────────────

// 法线打包: 八面edral 编码 (节省 G-Buffer 空间)
float2 EncodeNormalOctahedral(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    if (n.z < 0.0)
    {
        float2 sign = float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * sign;
    }
    return n.xy * 0.5 + 0.5;
}

float3 DecodeNormalOctahedral(float2 f)
{
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0.0)
    {
        float2 sign = float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * sign;
    }
    return normalize(n);
}

// 颜色打包: RGBM (RGB + Multiplier)
float4 EncodeRGBM(float3 color)
{
    color = max(color, 0.0);
    float maxChannel = max(color.r, max(color.g, color.b));
    float multiplier = ceil(maxChannel * 255.0 / 7.0) * (7.0 / 255.0);
    return float4(color / max(multiplier, SHANYU_EPSILON), multiplier);
}

float3 DecodeRGBM(float4 rgbm)
{
    return rgbm.rgb * rgbm.a;
}

// 打包两个 float 到一个 float (16位精度)
float PackFloat2(float2 v)
{
    return floor(v.x * 255.0) + v.y;
}
float2 UnpackFloat2(float f)
{
    return float2(frac(f / 256.0), frac(f));
}

// ──────────────────────────────────────────────────────────────────────────
// BRDF 函数 (PBR 光照模型)
// ──────────────────────────────────────────────────────────────────────────

// GGX/Trowbridge-Reitz 法线分布函数
float D_GGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / max(SHANYU_EPSILON, denom * denom);
}

// Smith 几何遮蔽 (联合可见性, GGX)
float V_SmithGGXCorrelated(float NdotV, float NdotL, float roughness)
{
    float a2 = roughness * roughness;
    float GGXV = NdotL * sqrt(NdotV * NdotV * (1.0 - a2) + a2);
    float GGXL = NdotV * sqrt(NdotL * NdotL * (1.0 - a2) + a2);
    return 0.5 / max(SHANYU_EPSILON, GGXV + GGXL);
}

// Schlick 菲涅尔
float3 F_Schlick(float VdotH, float3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);
}

// Schlick 菲涅尔 (带粗糙度优化)
float3 F_SchlickRoughness(float VdotH, float3 F0, float roughness)
{
    float3 Fmin = max(F0, float3(0.04, 0.04, 0.04)) * (1.0 - roughness);
    return Fmin + (1.0 - Fmin) * pow(1.0 - VdotH, 5.0);
}

// Lambert 漫反射
float3 Diffuse_Lambert(float3 albedo)
{
    return albedo * SHANYU_INV_PI;
}

// Disney 漫反射 (更精确的次表面近似)
float3 Diffuse_Disney(float NdotV, float NdotL, float VdotH, float roughness, float3 albedo)
{
    float fd90 = 0.5 + 2.0 * roughness * VdotH * VdotH;
    float lightScatter = 1.0 + (fd90 - 1.0) * pow(1.0 - NdotL, 5.0);
    float viewScatter = 1.0 + (fd90 - 1.0) * pow(1.0 - NdotV, 5.0);
    return albedo * SHANYU_INV_PI * lightScatter * viewScatter;
}

// 直接光照 BRDF (GGX + Lambert)
float3 DirectBRDF(float3 albedo, float roughness, float3 F0,
                   float NdotL, float NdotV, float NdotH, float VdotH)
{
    float D = D_GGX(NdotH, roughness);
    float V = V_SmithGGXCorrelated(NdotV, NdotL, roughness);
    float3 F = F_Schlick(VdotH, F0);

    float3 specular = D * V * F;
    float3 diffuse = Diffuse_Lambert(albedo) * (1.0 - F);
    return diffuse + specular;
}

// 环境光照 (近似 IBL)
float3 EnvBRDFApprox(float3 F0, float roughness, float NdotV)
{
    const float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
    const float4 c1 = float4(1.0, 0.0425, 1.04, -0.04);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y;
    float2 AB = float2(-1.04, 1.04) * a004 + r.zw;
    return F0 * AB.x + AB.y;
}

// ──────────────────────────────────────────────────────────────────────────
// 颜色空间转换
// ──────────────────────────────────────────────────────────────────────────

// sRGB <-> Linear
float3 SRGBToLinear(float3 c)
{
    return select(c <= 0.04045, c / 12.92, pow((c + 0.055) / 1.055, 2.4));
}
float3 LinearToSRGB(float3 c)
{
    return select(c <= 0.0031308, c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055);
}

// ACES 色调映射
float3 ACESFilm(float3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate_fast((x * (a * x + b)) / (x * (c * x + d) + e));
}

// 曝光
float3 ApplyExposure(float3 color, float exposure)
{
    return color * exp2(exposure);
}

// 亮度
float Luminance(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

// ──────────────────────────────────────────────────────────────────────────
// G-Buffer 结构 (延迟渲染)
// ──────────────────────────────────────────────────────────────────────────

struct GBuffer
{
    float3 Albedo;         // GBuffer0.rgb
    float   Roughness;     // GBuffer0.a
    float3  Normal;        // GBuffer1.rgb
    float   Metallic;      // GBuffer1.a
    float3  Emissive;      // GBuffer2.rgb
    float   AO;            // GBuffer2.a
    float   Specular;      // GBuffer3.r
    float   MaterialID;    // GBuffer3.g
    float   ShadingModel;  // GBuffer3.b
    float   Custom;        // GBuffer3.a
};

// 编码 G-Buffer 到 4 个 RGBA8/RGBA16f target
void EncodeGBuffer(GBuffer g, out float4 rt0, out float4 rt1, out float4 rt2, out float4 rt3)
{
    rt0 = float4(g.Albedo, g.Roughness);
    rt1 = float4(EncodeNormalOctahedral(g.Normal), g.Metallic, 0.0);
    rt2 = float4(g.Emissive, g.AO);
    rt3 = float4(g.Specular, g.MaterialID / 255.0, g.ShadingModel / 255.0, g.Custom);
}

// 解码 G-Buffer
GBuffer DecodeGBuffer(float4 rt0, float4 rt1, float4 rt2, float4 rt3)
{
    GBuffer g;
    g.Albedo = rt0.rgb;
    g.Roughness = rt0.a;
    g.Normal = DecodeNormalOctahedral(rt1.xy);
    g.Metallic = rt1.z;
    g.Emissive = rt2.rgb;
    g.AO = rt2.a;
    g.Specular = rt3.r;
    g.MaterialID = rt3.g * 255.0;
    g.ShadingModel = rt3.b * 255.0;
    g.Custom = rt3.a;
    return g;
}

// ──────────────────────────────────────────────────────────────────────────
// 光源结构
// ──────────────────────────────────────────────────────────────────────────

struct DirectionalLight
{
    float3 Direction;
    float  Intensity;
    float3 Color;
    float  CastShadow;
    float4x4 ShadowMatrix;
    float4 ShadowCascadeSplits;
    float4 ShadowBiasParams; // x=depth, y=normal, z=slope, w=padding
};

struct PointLight
{
    float3 Position;
    float  Radius;
    float3 Color;
    float  Intensity;
    float  Falloff;
    float  CastShadow;
    uint   LightID;
    float  Padding;
};

struct SpotLight
{
    float3 Position;
    float  Range;
    float3 Direction;
    float  CosInnerAngle;
    float3 Color;
    float  CosOuterAngle;
    float  Intensity;
    float  CastShadow;
    float2 Padding;
};

// ──────────────────────────────────────────────────────────────────────────
// 全屏三角形 (用于后处理/全屏 pass)
// ──────────────────────────────────────────────────────────────────────────
struct FullScreenTriangleVSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
    float2 ScreenPos : TEXCOORD1;
};

// 生成全屏三角形 (无顶点缓冲)
FullScreenTriangleVSOutput FullScreenTriangleVS(uint vertexID : SV_VertexID)
{
    FullScreenTriangleVSOutput output;
    // 生成覆盖整个屏幕的三角形
    output.TexCoord = float2((vertexID << 1) & 2, vertexID & 2);
    output.Position = float4(output.TexCoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.ScreenPos = output.Position.xy;
    return output;
}

// ──────────────────────────────────────────────────────────────────────────
// 采样器绑定 (全局共享)
// ──────────────────────────────────────────────────────────────────────────
SamplerState gPointClampSampler  : register(s0);
SamplerState gPointWrapSampler   : register(s1);
SamplerState gLinearClampSampler : register(s2);
SamplerState gLinearWrapSampler  : register(s3);
SamplerComparisonState gShadowSampler : register(s4);

// ──────────────────────────────────────────────────────────────────────────
// 通用纹理绑定 (绑定到 t0-t15, 由各 pass 自行定义)
// ──────────────────────────────────────────────────────────────────────────

#endif // SHANYU_COMMON_HLSLI
