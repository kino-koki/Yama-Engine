// =============================================================================
// CSMShadow.vert.hlsl - Cascade Shadow Map 深度 pass
// 输出裁剪空间位置，仅写深度（depth-only pass）
// =============================================================================

struct ShadowPushConstants
{
    float4x4 uLightViewProj;  // offset 0
    float4x4 uWorldShadow;    // offset 64
};

[[vk::push_constant]] ShadowPushConstants pc;

struct VSInput
{
    [[vk::location(0)]] float3 inPos : POSITION;
};

float4 VSMain(VSInput i) : SV_Position
{
    float4 world = mul(pc.uWorldShadow, float4(i.inPos, 1.0));
    return mul(pc.uLightViewProj, world);
}
