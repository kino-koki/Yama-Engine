// ════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 世界渲染着色器集 (WorldShaders)
//
// 静态 HLSL 着色器字符串集合，供 WorldRenderPipeline 编译为 PSO 使用。
// 包含完整的延迟渲染管线所需的所有着色器阶段:
//   1. ZPrepass — 深度预渲染 (VS/PS)
//   2. GeometryBuffer — 几何缓冲输出 (VS/PS, MRT: Normal/Albedo/Specular/MotionVectors)
//   3. LightResolve — 延迟光照 Tile/Cluster 计算 (CS)
//   4. SSAO — 屏幕空间环境光遮蔽 (CS)
//   5. ToneMapping — 色调映射 ACES/Reinhard/Uncharted2 (PS)
//   6. FXAA — 快速近似抗锯齿 (PS)
//   7. TAAResolve — 时域抗锯齿解析 (PS)
//   8. Bloom — 泛光后处理 (PS)
//   9. MotionBlur — 运动模糊 (PS)
//
// 对接系统: WorldRenderPipeline (PSO 编译) / RHI (Shader 编译)
// ════════════════════════════════════════════════════════════════════════

namespace ShanyuEngine.Rendering
{
    /// <summary>
    /// 世界渲染着色器集 — 静态 HLSL 字符串。
    /// 每个着色器可由 RHIDevice.CompileShader() 编译后绑定到 PSO。
    /// </summary>
    public static class WorldShaders
    {
        // ════════════════════════════════════════════════════════
        //  共享 HLSL 头部 (Common)
        // ════════════════════════════════════════════════════════

        /// <summary>共享常量缓冲区与工具函数定义。</summary>
        public const string CommonHeader = @"
// ── Shanyu Engine Common HLSL Header ──

#ifndef SHANYU_COMMON_HLSL
#define SHANYU_COMMON_HLSL

// 常量缓冲区布局
cbuffer FrameConstants : register(b0)
{
    float4x4 ViewMatrix;
    float4x4 ProjectionMatrix;
    float4x4 ViewProjectionMatrix;
    float4x4 InvViewProjectionMatrix;
    float4x4 PrevViewProjectionMatrix;
    float3   CameraPosition;
    float    DeltaTime;
    float2   RenderTargetSize;
    float2   InvRenderTargetSize;
    float2   JitterOffset;
    float2   MotionVectorScale;
    int      FrameIndex;
    int      Pad0;
    float2   Pad1;
};

cbuffer ObjectConstants : register(b1)
{
    float4x4 WorldMatrix;
    float4x4 PrevWorldMatrix;
    float4   ObjectParams; // x: roughness, y: metallic, z: ao, w: unused
};

// GeometryBuffer 输出结构
struct GeometryBufferOutput
{
    float4 Albedo          : SV_Target0; // RGB: base color, A: ao
    float4 Normal          : SV_Target1; // RGB: world normal (encoded), A: unused
    float4 Specular        : SV_Target2; // R: metallic, G: roughness, B: specular, A: shading model id
    float4 MotionVectors   : SV_Target3; // RG: velocity in clip space, BA: unused
};

// 顶点输入
struct VertexInput
{
    float3 Position    : POSITION;
    float3 Normal      : NORMAL;
    float4 Tangent     : TANGENT;
    float2 TexCoord    : TEXCOORD0;
};

// 顶点输出 (Z-Prepass)
struct VSOutputDepth
{
    float4 ClipPos     : SV_POSITION;
};

// 顶点输出 (GeometryBuffer)
struct VSOutputGeometryBuffer
{
    float4 ClipPos         : SV_POSITION;
    float3 WorldPos        : TEXCOORD0;
    float3 WorldNormal     : TEXCOORD1;
    float4 WorldTangent    : TEXCOORD2;
    float2 TexCoord        : TEXCOORD3;
    float4 PrevClipPos     : TEXCOORD4;
};

// 全屏三角形顶点输出
struct VSOutputFullscreen
{
    float4 ClipPos : SV_POSITION;
    float2 UV      : TEXCOORD0;
};

// 编码/解码法线 (Octahedral encoding, 2-channel)
float2 EncodeNormalOctahedron(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 enc = (n.xy >= 0.0) ? n.xy : (n.xy * 2.0 - 1.0);
    return enc * 0.5 + 0.5;
}

float3 DecodeNormalOctahedron(float2 enc)
{
    float2 n = enc * 2.0 - 1.0;
    float3 v = float3(n.x, n.y, 1.0 - abs(n.x) - abs(n.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * (v.xy >= 0.0 ? 1.0 : -1.0);
    return normalize(v);
}

// 线性深度 -> 非线性深度
float LinearToDepth(float linearDepth, float near, float far)
{
    return (far / (far - near)) * (1.0 - near / linearDepth);
}

// 非线性深度 -> 线性深度
float DepthToLinear(float depth, float near, float far)
{
    return near * far / (far - depth * (far - near));
}

#endif // SHANYU_COMMON_HLSL
";

        // ════════════════════════════════════════════════════════
        //  Z-Prepass — 深度预渲染
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// Z-Prepass 顶点着色器。
        /// 仅变换顶点到裁剪空间，不输出颜色。
        /// 配合 DepthWrite=true, ColorWrite=false 使用。
        /// </summary>
        public const string ZPrepassVS = CommonHeader + @"
// ── Z-Prepass Vertex Shader ──

VSOutputDepth VS_ZPrepass(VertexInput input)
{
    VSOutputDepth output;
    float4 worldPos = mul(float4(input.Position, 1.0), WorldMatrix);
    output.ClipPos = mul(worldPos, ViewProjectionMatrix);
    return output;
}
";

        /// <summary>
        /// Z-Prepass 像素着色器 (空实现)。
        /// 深度由光栅化器自动写入，PS 不需要任何输出。
        /// </summary>
        public const string ZPrepassPS = @"
// ── Z-Prepass Pixel Shader (空, 深度由光栅化器写入) ──

void PS_ZPrepass()
{
    // 不输出任何颜色，仅更新深度
    discard; // 确保不写入 RT
}
";

        // ════════════════════════════════════════════════════════
        //  GeometryBuffer — 几何缓冲
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// GeometryBuffer 顶点着色器。
        /// 输出世界空间位置、法线、切线、UV 和上一帧裁剪坐标(运动矢量)。
        /// </summary>
        public const string GeometryBufferVS = CommonHeader + @"
// ── GeometryBuffer Vertex Shader ──

VSOutputGeometryBuffer VS_GeometryBuffer(VertexInput input)
{
    VSOutputGeometryBuffer output;

    float4 worldPos = mul(float4(input.Position, 1.0), WorldMatrix);
    output.ClipPos = mul(worldPos, ViewProjectionMatrix);

    // 世界空间位置 (用于后续 Pass)
    output.WorldPos = worldPos.xyz;

    // 世界空间法线
    output.WorldNormal = normalize(mul(input.Normal, (float3x3)WorldMatrix));

    // 世界空间切线
    output.WorldTangent = float4(
        normalize(mul(input.Tangent.xyz, (float3x3)WorldMatrix)),
        input.Tangent.w
    );

    output.TexCoord = input.TexCoord;

    // 上一帧裁剪坐标 (用于运动矢量计算)
    float4 prevWorldPos = mul(float4(input.Position, 1.0), PrevWorldMatrix);
    output.PrevClipPos = mul(prevWorldPos, PrevViewProjectionMatrix);

    return output;
}
";

        /// <summary>
        /// GeometryBuffer 像素着色器。
        /// 输出 4 个 MRT: Albedo (RGB+AO), Normal (Octahedral), Specular (Metal/Rough/Spec), MotionVectors。
        /// </summary>
        public const string GeometryBufferPS = CommonHeader + @"
// ── GeometryBuffer Pixel Shader ──

// 简单的纹理采样器
Texture2D AlbedoMap    : register(t0);
Texture2D NormalMap    : register(t1);
SamplerState LinearClamp : register(s0);

GeometryBufferOutput PS_GeometryBuffer(VSOutputGeometryBuffer input)
{
    GeometryBufferOutput output;

    // Albedo + AO (使用纹理或纯色)
    float3 albedo = AlbedoMap.Sample(LinearClamp, input.TexCoord).rgb;
    float ao = ObjectParams.z;
    output.Albedo = float4(albedo, ao);

    // 法线 (法线贴图 + TBN 空间变换, 或直接使用顶点法线)
    float3 normal = normalize(input.WorldNormal);
    float3 tangent = normalize(input.WorldTangent.xyz);
    float bitangent = cross(normal, tangent) * input.WorldTangent.w;
    float3x3 tbn = float3x3(tangent, bitangent, normal);

    float3 sampledNormal = NormalMap.Sample(LinearClamp, input.TexCoord).rgb;
    sampledNormal = sampledNormal * 2.0 - 1.0;
    float3 worldNormal = normalize(mul(sampledNormal, tbn));

    // Octahedral 编码法线到 2 通道 (节省 GeometryBuffer 带宽)
    float2 encNormal = EncodeNormalOctahedron(worldNormal);
    output.Normal = float4(encNormal, 0.0, 1.0);

    // PBR 材质参数
    float metallic = ObjectParams.x;
    float roughness = ObjectParams.y;
    float specular = 0.5; // F0 默认值
    output.Specular = float4(metallic, roughness, specular, 0.0); // shading model: 0=lit

    // 运动矢量 (当前帧 NDC - 上一帧 NDC)
    float2 ndcCurrent = input.ClipPos.xy / input.ClipPos.w;
    float2 ndcPrev = input.PrevClipPos.xy / input.PrevClipPos.w;
    float2 motion = (ndcCurrent - ndcPrev) * MotionVectorScale;
    // 丢弃超出屏幕范围的极端值
    motion = clamp(motion, -1.0, 1.0);
    output.MotionVectors = float4(motion, 0.0, 1.0);

    return output;
}
";

        // ════════════════════════════════════════════════════════
        //  LightResolve — 延迟光照计算
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 延迟光照计算着色器 (Compute Shader)。
        /// 基于 Tile/Cluster 分解的光照计算，从 GeometryBuffer 读取几何信息，
        /// 遍历影响当前 Tile 的光源，计算直接光照。
        /// </summary>
        public const string LightResolveCS = CommonHeader + @"
// ── Deferred Lighting Compute Shader (Tile Based) ──

#define TILE_SIZE 16
#define MAX_LIGHTS_PER_TILE 64

// GeometryBuffer 纹理
Texture2D GeometryBuffer_Albedo       : register(t0);
Texture2D GeometryBuffer_Normal       : register(t1);
Texture2D GeometryBuffer_Specular     : register(t2);
Texture2D GeometryBuffer_Depth        : register(t3);
Texture2D SSAO_Texture         : register(t4);
Texture2D ShadowAtlas          : register(t5);
RWTexture2D<float4> OutputColor : register(u0);

// 光源缓冲区
StructuredBuffer<LightData> LightBuffer : register(t6);
Buffer<uint> TileLightList             : register(t7);

// Tile 分配缓冲区
cbuffer TileInfo : register(b2)
{
    uint2 TileCount;
    uint  NumTiles;
    uint  NumLights;
};

// 光源数据结构

// PBR 直接光照计算
float3 PBR_Direct_Lighting(float3 albedo, float metallic, float roughness,
    float3 N, float3 V, float3 L, float3 lightColor, float lightIntensity)
{
    float3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);

    // Fresnel (Schlick)
    float F0 = lerp(0.04, albedo, metallic);
    float3 F = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

    // Distribution (GGX/Smith)
    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    float D = a2 / (3.14159 * denom * denom + 1e-7);

    // Geometry (Smith GGX)
    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float G1V = NdotV / (NdotV * (1.0 - k) + k);
    float G1L = NdotL / (NdotL * (1.0 - k) + k);
    float G = G1V * G1L;

    // Cook-Torrance BRDF
    float3 specular = (D * G * F) / (4.0 * NdotV * NdotL + 1e-7);
    float3 kd = lerp(1.0 - F, 0.0, metallic);
    float3 diffuse = kd * albedo / 3.14159;

    return (diffuse + specular) * lightColor * lightIntensity * NdotL;
}

// 环境光 (简化)
float3 Ambient(float3 albedo, float ao, float3 N)
{
    float3 ambientColor = float3(0.03, 0.03, 0.05);
    float NdotUp = N.y * 0.5 + 0.5;
    float3 skyColor = lerp(float3(0.1, 0.15, 0.2), float3(0.3, 0.5, 0.7), NdotUp);
    float3 groundColor = float3(0.1, 0.08, 0.06) * (1.0 - NdotUp);
    return (skyColor + groundColor) * albedo * ao;
}

// 世界空间位置重建
float3 ReconstructWorldPos(float2 uv, float depth)
{
    float4 clipPos = float4(uv * 2.0 - 1.0, depth, 1.0);
    clipPos.y = -clipPos.y; // Vulkan/DX12 UV 翻转
    float4 worldPos = mul(clipPos, InvViewProjectionMatrix);
    return worldPos.xyz / worldPos.w;
}

[numthreads(TILE_SIZE, TILE_SIZE, 1)]
void CS_LightResolve(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pixelCoord = dispatchThreadID.xy;
    if (pixelCoord.x >= (uint)RenderTargetSize.x || pixelCoord.y >= (uint)RenderTargetSize.y)
        return;

    float2 uv = (pixelCoord + 0.5) * InvRenderTargetSize;

    // 读取 GeometryBuffer
    float4 albedo_ao   = GeometryBuffer_Albedo.SampleLevel(sampler_LinearClamp, uv, 0);
    float4 encNormal   = GeometryBuffer_Normal.SampleLevel(sampler_LinearClamp, uv, 0);
    float4 specData    = GeometryBuffer_Specular.SampleLevel(sampler_LinearClamp, uv, 0);
    float  depth       = GeometryBuffer_Depth.SampleLevel(sampler_LinearClamp, uv, 0).r;

    if (depth >= 1.0)
    {
        OutputColor[pixelCoord] = float4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    float3 albedo    = albedo_ao.rgb;
    float  ao        = albedo_ao.a;
    float  metallic  = specData.r;
    float  roughness = specData.g;
    float3 N         = DecodeNormalOctahedron(encNormal.xy);

    float3 worldPos = ReconstructWorldPos(uv, depth);
    float3 V = normalize(CameraPosition - worldPos);

    // SSAO
    float ssao = SSAO_Texture.SampleLevel(sampler_LinearClamp, uv, 0).r;

    // 环境光
    float3 color = Ambient(albedo, ao * ssao, N);

    // 直接光照 (遍历 Tile 中的光源)
    uint2 tileCoord = pixelCoord / TILE_SIZE;
    uint tileIndex = tileCoord.y * TileCount.x + tileCoord.x;

    // 获取此 Tile 的光源列表
    uint lightCount = min(MAX_LIGHTS_PER_TILE, NumLights);
    for (uint i = 0; i < lightCount; i++)
    {
        LightData light = LightBuffer[i];

        float3 L;
        float attenuation = 1.0;

        if (light.Type < 0.5) // Point Light
        {
            float3 toLight = light.Position - worldPos;
            float dist = length(toLight);
            L = toLight / dist;
            attenuation = 1.0 / (1.0 + 0.09 * dist + 0.032 * dist * dist);
            attenuation *= saturate(1.0 - dist / light.Range);
        }
        else if (light.Type < 1.5) // Spot Light
        {
            float3 toLight = light.Position - worldPos;
            float dist = length(toLight);
            L = toLight / dist;
            float cosAngle = dot(-L, light.Direction);
            float spotFactor = smoothstep(light.SpotAngles.y, light.SpotAngles.x, cosAngle);
            attenuation = spotFactor / (1.0 + 0.09 * dist + 0.032 * dist * dist);
        }
        else // Directional Light
        {
            L = normalize(-light.Direction);
            attenuation = 1.0;
        }

        if (attenuation > 0.001)
        {
            color += PBR_Direct_Lighting(albedo, metallic, roughness, N, V, L,
                light.Color, light.Intensity * attenuation);
        }
    }

    OutputColor[pixelCoord] = float4(color, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  SSAO — 屏幕空间环境光遮蔽
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// SSAO 计算着色器 (Compute Shader)。
        /// 在屏幕空间采样深度/法线，估计环境光遮蔽率。
        /// 使用半球采样 + 降噪模糊。
        /// </summary>
        public const string SSAOCS = CommonHeader + @"
// ── SSAO Compute Shader ──

#define SSAO_KERNEL_SIZE 16
#define SSAO_RADIUS 0.5
#define SSAO_BIAS 0.025

Texture2D DepthTexture     : register(t0);
Texture2D NormalTexture    : register(t1);
RWTexture2D<float> OutputAO : register(u0);

cbuffer SSAOParams : register(b2)
{
    float Radius;
    float Bias;
    float Intensity;
    float Power;
    float2 NoiseScale;
    int    KernelSize;
    float  Pad0;
    float  Pad1;
};

// 重建世界空间位置
float3 ReconstructPosSSAO(float2 uv, float depth)
{
    float4 clip = float4(uv * 2.0 - 1.0, depth, 1.0);
    clip.y = -clip.y;
    float4 world = mul(clip, InvViewProjectionMatrix);
    return world.xyz / world.w;
}

// 伪随机旋转 (避免带状伪影)
float2 SSAORandom(float2 uv)
{
    return float2(
        frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453),
        frac(sin(dot(uv, float2(39.346, 11.135))) * 43758.5453)
    );
}

[numthreads(8, 8, 1)]
void CS_SSAO(uint3 id : SV_DispatchThreadID)
{
    uint2 pixel = id.xy;
    if (pixel.x >= (uint)RenderTargetSize.x || pixel.y >= (uint)RenderTargetSize.y)
        return;

    float2 uv = (pixel + 0.5) * InvRenderTargetSize;
    float depth = DepthTexture.SampleLevel(sampler_LinearClamp, uv, 0).r;
    float3 pos = ReconstructPosSSAO(uv, depth);

    float4 encN = NormalTexture.SampleLevel(sampler_LinearClamp, uv, 0);
    float3 N = DecodeNormalOctahedron(encN.xy);

    // 伪随机切线空间
    float2 rand = SSAORandom(uv + FrameIndex * 0.01) * 2.0 - 1.0;
    float3 tangent = normalize(rand.x * N + rand.y * cross(N, float3(0, 1, 0.001)));
    float3 bitangent = cross(N, tangent);
    float3x3 TBN = float3x3(tangent, bitangent, N);

    // 半球采样
    float occlusion = 0.0;
    int kernelSize = SSAO_KERNEL_SIZE;

    for (int i = 0; i < kernelSize; i++)
    {
        // 基于 i 的半球方向 (Hammersley 序列简化版)
        float phi = 2.0 * 3.14159 * frac(i * 0.6180339887);
        float cosTheta = sqrt(1.0 - (i + 0.5) / kernelSize);
        float sinTheta = sqrt((i + 0.5) / kernelSize);

        float3 sampleDir = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
        float3 samplePos = pos + mul(sampleDir, TBN) * Radius;

        // 投影采样点到屏幕空间
        float4 projSample = mul(float4(samplePos, 1.0), ViewProjectionMatrix);
        projSample.xyz /= projSample.w;
        float2 sampleUV = projSample.xy * 0.5 + 0.5;

        if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1)
            continue;

        float sampleDepth = DepthTexture.SampleLevel(sampler_LinearClamp, sampleUV, 0).r;
        float3 realPos = ReconstructPosSSAO(sampleUV, sampleDepth);

        // 深度差 = 遮蔽量
        float rangeCheck = smoothstep(0.0, 1.0, Radius / abs(pos.z - realPos.z));
        occlusion += (realPos.z >= samplePos.z + Bias ? 1.0 : 0.0) * rangeCheck;
    }

    occlusion = 1.0 - (occlusion / kernelSize) * Intensity;
    occlusion = pow(occlusion, Power);

    OutputAO[pixel] = saturate(occlusion);
}
";

        // ════════════════════════════════════════════════════════
        //  ToneMapping — 色调映射
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 色调映射像素着色器。
        /// 支持 ACES Filmic / Reinhard / Uncharted2 三种曲线。
        /// </summary>
        public const string ToneMappingPS = CommonHeader + @"
// ── Tone Mapping Pixel Shader ──

Texture2D HDRColor : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer ToneMappingParams : register(b2)
{
    int   Method;      // 0=ACES, 1=Reinhard, 2=Uncharted2
    float Exposure;
    float Gamma;
    float Pad0;
};

// ACES Filmic 色调映射 (Stephen Hill 优化版)
float3 ACESFilmic(float3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

// Reinhard 色调映射
float3 Reinhard(float3 color)
{
    return color / (1.0 + color);
}

// Uncharted2 (Hable) 色调映射
float3 Uncharted2Partial(float3 x)
{
    float A = 0.15; float B = 0.50; float C = 0.10;
    float D = 0.20; float E = 0.02; float F = 0.30;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 Uncharted2(float3 color)
{
    float exposureBias = 2.0;
    float3 curr = Uncharted2Partial(color * exposureBias);
    float3 whiteScale = 1.0 / Uncharted2Partial(float3(11.2, 11.2, 11.2));
    return curr * whiteScale;
}

float4 PS_ToneMapping(VSOutputFullscreen input) : SV_Target
{
    float3 hdr = HDRColor.Sample(LinearClamp, input.UV).rgb;
    hdr *= Exposure;

    float3 mapped;
    switch (Method)
    {
        case 1:  mapped = Reinhard(hdr); break;
        case 2:  mapped = Uncharted2(hdr); break;
        default: mapped = ACESFilmic(hdr); break;
    }

    // Gamma 校正
    mapped = pow(mapped, float3(1.0 / Gamma, 1.0 / Gamma, 1.0 / Gamma));
    return float4(mapped, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  FXAA — 快速近似抗锯齿
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// FXAA 3.11 像素着色器。
        /// 基于 Timothy Lottes 的 FXAA 算法，检测并对边缘进行抗锯齿处理。
        /// </summary>
        public const string FXAAPS = CommonHeader + @"
// ── FXAA 3.11 Pixel Shader ──

Texture2D ColorTex : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer FXAAParams : register(b2)
{
    float2 RcpFrame;      // 1.0 / resolution
    float  SubpixQuality; // 0.75 default
    float  EdgeThreshold;
    float  EdgeThresholdMin;
    float  Pad0;
    float  Pad1;
    float  Pad2;
};

#define FXAA_EDGE_STEPS 10
#define FXAA_EDGE_GUESS 8.0

float Luma(float3 rgb)
{
    return dot(rgb, float3(0.299, 0.587, 0.114));
}

float4 PS_FXAA(VSOutputFullscreen input) : SV_Target
{
    float2 pos = input.UV;
    float3 rgbM = ColorTex.Sample(LinearClamp, pos).rgb;
    float lumaM = Luma(rgbM);
    float lumaN = Luma(ColorTex.Sample(LinearClamp, pos + float2(0, -1) * RcpFrame).rgb);
    float lumaS = Luma(ColorTex.Sample(LinearClamp, pos + float2(0,  1) * RcpFrame).rgb);
    float lumaW = Luma(ColorTex.Sample(LinearClamp, pos + float2(-1, 0) * RcpFrame).rgb);
    float lumaE = Luma(ColorTex.Sample(LinearClamp, pos + float2( 1, 0) * RcpFrame).rgb);

    // 找到最大/最小亮度
    float lumaMin = min(lumaM, min(min(lumaN, lumaS), min(lumaW, lumaE)));
    float lumaMax = max(lumaM, max(max(lumaN, lumaS), max(lumaW, lumaE)));
    float lumaRange = lumaMax - lumaMin;

    // 低于阈值则跳过
    if (lumaRange < max(EdgeThresholdMin, lumaMax * EdgeThreshold))
        return float4(rgbM, 1.0);

    // 计算亚像素混合因子
    float lumaNW = Luma(ColorTex.Sample(LinearClamp, pos + float2(-1, -1) * RcpFrame).rgb);
    float lumaNE = Luma(ColorTex.Sample(LinearClamp, pos + float2( 1, -1) * RcpFrame).rgb);
    float lumaSW = Luma(ColorTex.Sample(LinearClamp, pos + float2(-1,  1) * RcpFrame).rgb);
    float lumaSE = Luma(ColorTex.Sample(LinearClamp, pos + float2( 1,  1) * RcpFrame).rgb);

    float lumaAvg = (lumaN + lumaS + lumaW + lumaE) * 0.25;
    float subpixOffset = saturate(abs(lumaAvg - lumaM) / lumaRange);
    subpixOffset = smoothstep(0, 1, subpixOffset);
    subpixOffset = subpixOffset * subpixOffset * SubpixQuality;

    // 边缘方向
    float edgeH = abs(-2.0 * lumaW + lumaNW + lumaSW) + abs(-2.0 * lumaM + lumaN + lumaS) * 2.0
                + abs(-2.0 * lumaE + lumaNE + lumaSE);
    float edgeV = abs(-2.0 * lumaN + lumaNW + lumaNE) + abs(-2.0 * lumaM + lumaW + lumaE) * 2.0
                + abs(-2.0 * lumaS + lumaSW + lumaSE);

    bool isHorizontal = (edgeH >= edgeV);

    // 沿垂直/水平方向步进，寻找边缘端点
    float stepLength = isHorizontal ? RcpFrame.y : RcpFrame.x;
    float luma1 = isHorizontal ? lumaN : lumaW;
    float luma2 = isHorizontal ? lumaS : lumaE;
    float grad1 = luma1 - lumaM;
    float grad2 = luma2 - lumaM;
    bool is1Steepest = abs(grad1) >= abs(grad2);
    float gradientScaled = 0.25 * max(abs(grad1), abs(grad2));

    float lumaLocalAverage;
    if (is1Steepest)
        lumaLocalAverage = 0.5 * (luma1 + lumaM);
    else
        lumaLocalAverage = 0.5 * (luma2 + lumaM);

    // 边缘端点搜索 (简化版)
    float2 offset = isHorizontal ? float2(RcpFrame.x, 0) : float2(0, RcpFrame.y);
    float2 pos1 = pos - offset;
    float2 pos2 = pos + offset;

    for (int j = 0; j < FXAA_EDGE_STEPS; j++)
    {
        float t = float(j) / float(FXAA_EDGE_STEPS);
        float step = stepLength * (1.0 + t);
        if (!is1Steepest) step = -step;

        float2 p1 = pos1 - offset * step;
        float2 p2 = pos2 + offset * step;
        float l1 = Luma(ColorTex.Sample(LinearClamp, p1).rgb);
        float l2 = Luma(ColorTex.Sample(LinearClamp, p2).rgb);

        bool done1 = abs(l1 - lumaLocalAverage) >= gradientScaled;
        bool done2 = abs(l2 - lumaLocalAverage) >= gradientScaled;
        if (done1 && done2) break;
    }

    // 混合
    float pixelBlend = max(subpixOffset, 1.0);
    float3 result = lerp(rgbM, ColorTex.Sample(LinearClamp, pos + offset * 0.5).rgb, pixelBlend * 0.25);

    return float4(result, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  TAAResolve — 时域抗锯齿
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// TAA 时域抗锯齿解析着色器。
        /// 利用运动矢量将当前帧与历史帧混合，减少锯齿和闪烁。
        /// 包含邻域钳制 (Neighborhood Clamping) 防止重影。
        /// </summary>
        public const string TAAResolvePS = CommonHeader + @"
// ── TAA Resolve Pixel Shader ──

Texture2D CurrentColor     : register(t0);
Texture2D HistoryColor     : register(t1);
Texture2D MotionVectors    : register(t2);
SamplerState LinearClamp   : register(s0);

cbuffer TAAParams : register(b2)
{
    float   BlendFactor;   // 0.1 (更多历史) ~ 0.3 (更多当前)
    float   Gamma;
    float2  Pad0;
};

float3 ClampToNeighborhood(float3 color, float2 uv, float2 texelSize)
{
    float3 minC = color;
    float3 maxC = color;

    [unroll]
    for (int x = -1; x <= 1; x++)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            float3 s = CurrentColor.Sample(LinearClamp, uv + float2(x, y) * texelSize).rgb;
            minC = min(minC, s);
            maxC = max(maxC, s);
        }
    }

    return clamp(color, minC, maxC);
}

float4 PS_TAAResolve(VSOutputFullscreen input) : SV_Target
{
    float2 uv = input.UV;
    float2 texelSize = InvRenderTargetSize;

    // 读取运动矢量，获取上一帧 UV
    float2 motion = MotionVectors.Sample(LinearClamp, uv).rg;
    float2 historyUV = uv - motion;

    // 边界检查
    if (historyUV.x < 0 || historyUV.x > 1 || historyUV.y < 0 || historyUV.y > 1)
        return float4(CurrentColor.Sample(LinearClamp, uv).rgb, 1.0);

    // 采样当前帧和历史帧
    float3 current = CurrentColor.Sample(LinearClamp, uv).rgb;
    float3 history = HistoryColor.Sample(LinearClamp, historyUV).rgb;

    // 邻域钳制: 防止历史帧引入重影
    history = ClampToNeighborhood(history, uv, texelSize);

    // 混合
    float3 result = lerp(history, current, BlendFactor);

    return float4(result, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  Bloom — 泛光后处理
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 泛光后处理像素着色器。
        /// 先提取亮区 (阈值以上)，经高斯模糊后叠加回原图。
        /// 使用 13-tap 可分离高斯核。
        /// </summary>
        public const string BloomPS = CommonHeader + @"
// ── Bloom Post-Process Pixel Shader ──

Texture2D SceneColor     : register(t0);
Texture2D BloomTex       : register(t1);
SamplerState LinearClamp : register(s0);

cbuffer BloomParams : register(b2)
{
    float  Threshold;    // 亮度阈值 (HDR 空间)
    float  Intensity;    // 泛光强度
    float  Scatter;      // 散射系数
    int    MipLevel;     // 当前处理的 Mip 级别
    float3 Pad0;
};

// 13-tap 高斯权重 (sigma=2.5 近似)
static const float BloomWeights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

// 提取亮区
float4 PS_BloomExtract(VSOutputFullscreen input) : SV_Target
{
    float3 color = SceneColor.Sample(LinearClamp, input.UV).rgb;
    float brightness = Luma(color);
    float contribution = max(0.0, brightness - Threshold) / max(brightness, 1e-5);
    return float4(color * contribution, 1.0);
}

// 水平高斯模糊
float4 PS_BloomHBlur(VSOutputFullscreen input) : SV_Target
{
    float2 texelSize = InvRenderTargetSize;
    if (MipLevel > 0) texelSize *= 2.0;

    float3 result = SceneColor.Sample(LinearClamp, input.UV).rgb * BloomWeights[0];

    [unroll]
    for (int i = 1; i < 5; i++)
    {
        float2 offset = float2(float(i) * texelSize.x, 0.0);
        result += SceneColor.Sample(LinearClamp, input.UV + offset).rgb * BloomWeights[i];
        result += SceneColor.Sample(LinearClamp, input.UV - offset).rgb * BloomWeights[i];
    }

    return float4(result, 1.0);
}

// 垂直高斯模糊
float4 PS_BloomVBlur(VSOutputFullscreen input) : SV_Target
{
    float2 texelSize = InvRenderTargetSize;
    if (MipLevel > 0) texelSize *= 2.0;

    float3 result = SceneColor.Sample(LinearClamp, input.UV).rgb * BloomWeights[0];

    [unroll]
    for (int i = 1; i < 5; i++)
    {
        float2 offset = float2(0.0, float(i) * texelSize.y);
        result += SceneColor.Sample(LinearClamp, input.UV + offset).rgb * BloomWeights[i];
        result += SceneColor.Sample(LinearClamp, input.UV - offset).rgb * BloomWeights[i];
    }

    return float4(result, 1.0);
}

// 叠加泛光到场景
float4 PS_BloomComposite(VSOutputFullscreen input) : SV_Target
{
    float3 scene = SceneColor.Sample(LinearClamp, input.UV).rgb;
    float3 bloom = BloomTex.Sample(LinearClamp, input.UV).rgb;
    float3 result = scene + bloom * Intensity;
    return float4(result, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  MotionBlur — 运动模糊
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 运动模糊像素着色器。
        /// 基于运动矢量进行方向性模糊，模拟相机/物体运动产生的拖尾效果。
        /// 支持强度控制和最大模糊半径限制。
        /// </summary>
        public const string MotionBlurPS = CommonHeader + @"
// ── Motion Blur Pixel Shader ──

Texture2D SceneColor     : register(t0);
Texture2D MotionVectors  : register(t1);
SamplerState LinearClamp : register(s0);

cbuffer MotionBlurParams : register(b2)
{
    float  Intensity;      // 模糊强度 (0.0 ~ 1.0)
    float  MaxBlurRadius;  // 最大模糊半径 (像素)
    int    SampleCount;    // 采样数量 (8/16/32)
    float3 Pad0;
};

float4 PS_MotionBlur(VSOutputFullscreen input) : SV_Target
{
    float2 uv = input.UV;

    // 读取运动矢量
    float2 motion = MotionVectors.Sample(LinearClamp, uv).rg;
    float motionLength = length(motion);

    // 运动量过小则跳过
    if (motionLength < 0.001)
        return float4(SceneColor.Sample(LinearClamp, uv).rgb, 1.0);

    // 限制最大模糊半径
    motion = motion / max(motionLength, 1e-5) * min(motionLength, MaxBlurRadius) * Intensity;

    // 沿运动方向采样
    float3 color = float3(0, 0, 0);
    float totalWeight = 0.0;

    for (int i = 0; i < 16; i++)
    {
        float t = float(i) / 15.0;
        float2 sampleUV = uv + motion * (t - 0.5);
        float weight = 1.0 - abs(t - 0.5) * 2.0; // 三角权重
        color += SceneColor.Sample(LinearClamp, sampleUV).rgb * weight;
        totalWeight += weight;
    }

    color /= max(totalWeight, 1e-5);

    return float4(color, 1.0);
}
";
    }
}