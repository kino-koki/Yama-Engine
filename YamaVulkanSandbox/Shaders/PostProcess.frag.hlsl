// =============================================================================
// PostProcess.frag.hlsl - ACES Filmic Tonemap + Bloom 合成 + Gamma Correction
// 读取 HDR 颜色 + Bloom 纹理，输出 display-ready LDR 颜色
// =============================================================================

[[vk::binding(1, 0)]] Texture2D    g_HDRColor    : register(t0);
[[vk::binding(3, 0)]] Texture2D    g_BloomColor  : register(t1);
[[vk::binding(2, 0)]] SamplerState g_LinearSampler : register(s0);

[[vk::binding(0, 0)]] cbuffer ExposureConstants : register(b0)
{
    float4 uExposure;  // x=exposure, y=gamma, z=bloomStrength, w=pad
};

// ACES Filmic Tonemap (Narkowicz 2015 approximation)
float3 ACESFilmic(float3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

float4 PSMain(float4 fragCoord : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
{
    float3 hdr = g_HDRColor.Sample(g_LinearSampler, uv).rgb;
    float3 bloom = g_BloomColor.Sample(g_LinearSampler, uv).rgb;

    hdr += bloom * uExposure.z; // bloom 合成
    hdr *= uExposure.x;

    // ACES tonemap
    float3 mapped = ACESFilmic(hdr);

    // Gamma correction (linear -> sRGB)
    float gamma = uExposure.y;
    mapped = pow(mapped, float3(1.0 / gamma, 1.0 / gamma, 1.0 / gamma));

    return float4(mapped, 1.0);
}
