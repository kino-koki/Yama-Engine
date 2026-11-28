// =============================================================================
// FXAA.frag.hlsl - Fast Approximate Anti-Aliasing (FXAA 3.11 简化版)
// 在 LDR sRGB 颜色上进行边缘检测和模糊
// =============================================================================

[[vk::binding(1, 0)]] Texture2D    g_Input         : register(t0);
[[vk::binding(2, 0)]] SamplerState g_LinearSampler : register(s0);

[[vk::binding(0, 0)]] cbuffer FXAAConstants : register(b0)
{
    float4 uFXAA;  // xy=invResolution, z=fxaaOn, w=pad
};

float3 SampleLuma(float2 uv)
{
    float3 c = g_Input.Sample(g_LinearSampler, uv).rgb;
    // gamma to linear approx for luma calc
    return c * c;
}

float Luma(float3 c)
{
    return dot(c, float3(0.299, 0.587, 0.114));
}

float4 PSMain(float4 fragCoord : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
{
    if (uFXAA.z < 0.5)
        return float4(g_Input.Sample(g_LinearSampler, uv).rgb, 1.0);

    float2 invRes = uFXAA.xy;

    float3 rgbNW = SampleLuma(uv + float2(-1.0, -1.0) * invRes);
    float3 rgbNE = SampleLuma(uv + float2( 1.0, -1.0) * invRes);
    float3 rgbSW = SampleLuma(uv + float2(-1.0,  1.0) * invRes);
    float3 rgbSE = SampleLuma(uv + float2( 1.0,  1.0) * invRes);
    float3 rgbM  = SampleLuma(uv);

    float lumaNW = Luma(rgbNW);
    float lumaNE = Luma(rgbNE);
    float lumaSW = Luma(rgbSW);
    float lumaSE = Luma(rgbSE);
    float lumaM  = Luma(rgbM);

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    // skip if not edge
    if (lumaMax - lumaMin < 0.15 * lumaMax)
        return float4(g_Input.Sample(g_LinearSampler, uv).rgb, 1.0);

    float2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float reduction = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * 0.5, 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduction);
    dir = clamp(dir * rcpDirMin, -8.0, 8.0) * invRes;

    float3 rgbA = 0.5 * (g_Input.Sample(g_LinearSampler, uv + dir * (1.0 / 3.0 - 0.5)).rgb
                       + g_Input.Sample(g_LinearSampler, uv + dir * (2.0 / 3.0 - 0.5)).rgb);
    float3 rgbB = rgbA * 0.5 + 0.25 * (g_Input.Sample(g_LinearSampler, uv + dir * -0.5).rgb
                                     + g_Input.Sample(g_LinearSampler, uv + dir *  0.5).rgb);

    float lumaB = Luma(rgbB);
    if (lumaB < lumaMin || lumaB > lumaMax)
        return float4(rgbA, 1.0);

    return float4(rgbB, 1.0);
}
