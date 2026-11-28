// =============================================================================
// BloomBlur.frag.hlsl - 高斯模糊 (dual pass: horizontal / vertical)
// 9-tap gaussian, 由 uBloom.z 切换方向 (0=H, 1=V)
// =============================================================================

[[vk::binding(0, 0)]] Texture2D    g_Input         : register(t0);
[[vk::binding(1, 0)]] SamplerState g_LinearSampler : register(s0);

struct BloomPushConstants
{
    float4 uBloom;  // x=threshold, y=softKnee, z=dirX, w=dirY
};

[[vk::push_constant]] BloomPushConstants pc;

// 5-tap linear-sampled gaussian (weights sum normalized)
static const float WEIGHTS[5] = { 0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162 };

float4 PSMain(float4 fragCoord : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
{
    float2 dir = float2(pc.uBloom.z, pc.uBloom.w); // (1/w,0) for H, (0,1/h) for V

    float3 color = g_Input.Sample(g_LinearSampler, uv).rgb * WEIGHTS[0];
    [unroll]
    for (int i = 1; i < 5; ++i)
    {
        float2 off = dir * (float(i) * 1.3846153846); // 1.384615 = lerp factor for linear sampling
        color += g_Input.Sample(g_LinearSampler, uv + off).rgb * WEIGHTS[i];
        color += g_Input.Sample(g_LinearSampler, uv - off).rgb * WEIGHTS[i];
    }
    return float4(color, 1.0);
}
