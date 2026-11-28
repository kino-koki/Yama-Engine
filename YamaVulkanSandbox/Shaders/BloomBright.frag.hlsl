// =============================================================================
// BloomBright.frag.hlsl - 亮度提取 (bright pass)
// 从 HDR 颜色提取超过阈值的亮度，输出到半分辨率 RT
// =============================================================================

[[vk::binding(0, 0)]] Texture2D    g_HDRColor     : register(t0);
[[vk::binding(1, 0)]] SamplerState g_LinearSampler: register(s0);

struct BloomPushConstants
{
    float4 uBloom;  // x=threshold, y=softKnee, zw=pad
};

[[vk::push_constant]] BloomPushConstants pc;

float4 PSMain(float4 fragCoord : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
{
    float3 hdr = g_HDRColor.Sample(g_LinearSampler, uv).rgb;

    // Luminance-based threshold (soft knee)
    float threshold = pc.uBloom.x;
    float softKnee  = pc.uBloom.y;

    float lum = dot(hdr, float3(0.2126, 0.7152, 0.0722));
    float knee = threshold * softKnee + 0.0001;
    float soft = lum - threshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 0.0001);
    float contribution = max(soft, lum - threshold) / max(lum, 0.0001);

    float3 bright = hdr * contribution;
    return float4(bright, 1.0);
}
