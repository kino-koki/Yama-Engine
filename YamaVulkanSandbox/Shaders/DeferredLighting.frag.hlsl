// =============================================================================
// DeferredLighting.frag.hlsl - Deferred 光照全屏 pass
// 读取 GBuffer + CSM，输出 linear HDR 颜色（未 tonemap）
// GGX Cook-Torrance + Lambert diffuse
// =============================================================================

[[vk::binding(0, 0)]] cbuffer CameraConstants : register(b0)
{
    float4x4 uView;
    float4x4 uProjection;
    float4x4 uViewProjection;
    float4   uCameraPos;
};

[[vk::binding(1, 0)]] cbuffer LightConstants : register(b1)
{
    float4   uLightDir;       // xyz=方向（朝向光源），w=强度
    float4   uLightColor;     // xyz=颜色, w=环境强度
    float4   uCascadeSplits;  // 4 个 cascade 的远平面
    float4   uShadowParams;   // x=texelSize, y=depthBias, z=normalBias, w=pad
    float4x4 uLightVP[4];     // 4 cascade 的 light VP 矩阵
};

[[vk::binding(2, 0)]] cbuffer ResolutionConstants : register(b2)
{
    float4 uResolution;       // xy=width/height, zw=1/width, 1/height
};

[[vk::binding(12, 0)]] SamplerComparisonState g_ShadowSampler : register(s3);
[[vk::binding(13, 0)]] SamplerState           g_LinearSampler : register(s4);

[[vk::binding(3, 0)]]  Texture2D g_GBufferWorldPos : register(t0);
[[vk::binding(4, 0)]]  Texture2D g_GBufferAlbedo   : register(t1);
[[vk::binding(5, 0)]]  Texture2D g_GBufferNormal   : register(t5);
[[vk::binding(6, 0)]]  Texture2D g_GBufferPBR      : register(t6);
[[vk::binding(7, 0)]]  Texture2D g_GBufferEmissive : register(t7);
[[vk::binding(8, 0)]]  Texture2D g_CascadeShadow0  : register(t8);
[[vk::binding(9, 0)]]  Texture2D g_CascadeShadow1  : register(t9);
[[vk::binding(10, 0)]] Texture2D g_CascadeShadow2  : register(t10);
[[vk::binding(11, 0)]] Texture2D g_CascadeShadow3  : register(t11);

// 3x3 PCF
float SampleShadowPCF(Texture2D shadowTex, float3 shadowCoord)
{
    if (shadowCoord.x < 0.0 || shadowCoord.x > 1.0 ||
        shadowCoord.y < 0.0 || shadowCoord.y > 1.0 ||
        shadowCoord.z > 1.0)
        return 1.0;

    float texel = uShadowParams.x;
    float bias  = uShadowParams.y;
    float shadow = 0.0;
    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
        {
            float2 uv = shadowCoord.xy + float2(x, y) * texel;
            shadow += shadowTex.SampleCmpLevelZero(g_ShadowSampler, uv, shadowCoord.z - bias);
        }
    }
    return shadow / 9.0;
}

float3 GGX_CookTorrance(float3 N, float3 V, float3 L, float3 albedo,
                        float metallic, float roughness)
{
    float3  H     = normalize(V + L);
    float   NdotL = max(dot(N, L), 0.0);
    float   NdotV = max(dot(N, V), 1e-4);
    float   NdotH = max(dot(N, H), 0.0);
    float   VdotH = max(dot(V, H), 0.0);

    // GGX distribution
    float a     = roughness * roughness;
    float a2    = a * a;
    float dDen  = NdotH * NdotH * (a2 - 1.0) + 1.0;
    float D     = a2 / (3.14159265 * dDen * dDen);

    // Smith GGX geometry (height-correlated approx)
    float k     = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float G1_NL = NdotL / (NdotL * (1.0 - k) + k);
    float G1_NV = NdotV / (NdotV * (1.0 - k) + k);
    float G     = G1_NL * G1_NV;

    // Schlick fresnel
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);
    float3 F  = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

    float3 specular = D * G * F / (4.0 * NdotV * NdotL + 1e-4);
    float3 kd       = (1.0 - F) * (1.0 - metallic);
    float3 diffuse  = kd * albedo / 3.14159265;

    return (diffuse + specular) * NdotL;
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv  = fragCoord.xy * uResolution.zw;
    int2   pix = int2(fragCoord.xy);

    float4 worldPosW = g_GBufferWorldPos.Load(int3(pix, 0));
    float4 albedoW   = g_GBufferAlbedo.Load(int3(pix, 0));
    float4 normalW   = g_GBufferNormal.Load(int3(pix, 0));
    float4 pbrW      = g_GBufferPBR.Load(int3(pix, 0));
    float4 emissiveW = g_GBufferEmissive.Load(int3(pix, 0));

    float3 albedo      = albedoW.rgb;
    float  metallic    = albedoW.a;
    float  roughness   = pbrW.r;
    float  ao          = pbrW.g;
    float  emissiveStr = pbrW.b;
    float3 N           = normalize(normalW.xyz);

    if (worldPosW.a < 0.5)  // 背景
        return float4(0.05, 0.05, 0.06, 1.0);

    float3 V     = normalize(uCameraPos.xyz - worldPosW.xyz);
    float3 L     = normalize(uLightDir.xyz);
    float3 light = GGX_CookTorrance(N, V, L, albedo, metallic, roughness);

    // 选择 cascade（用 if/else 避免 SPIR-V 禁止的 texture handle store）
    float depthLinear = length(worldPosW.xyz - uCameraPos.xyz);
    int   cascade     = 3;
    [unroll]
    for (int c = 0; c < 4; ++c)
    {
        if (depthLinear < uCascadeSplits[c]) { cascade = c; break; }
    }

    float4 lightSpace = mul(uLightVP[cascade], float4(worldPosW.xyz, 1.0));
    float3 shadowCoord = lightSpace.xyz / lightSpace.w * 0.5 + 0.5;
    shadowCoord.y = 1.0 - shadowCoord.y; // Vulkan depth origin top-left

    float shadow;
    if      (cascade == 0) shadow = SampleShadowPCF(g_CascadeShadow0, shadowCoord);
    else if (cascade == 1) shadow = SampleShadowPCF(g_CascadeShadow1, shadowCoord);
    else if (cascade == 2) shadow = SampleShadowPCF(g_CascadeShadow2, shadowCoord);
    else                   shadow = SampleShadowPCF(g_CascadeShadow3, shadowCoord);

    float3 result = light * uLightColor.rgb * uLightDir.w * shadow;
    result += albedo * uLightColor.w * ao;
    result += emissiveW.rgb * emissiveStr;
    return float4(result, 1.0);
}
