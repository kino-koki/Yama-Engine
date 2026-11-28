// =============================================================================
// CullDispatch.comp.hlsl - GPU frustum culling, writes indirect draw list
// =============================================================================

struct GPURenderItem
{
    float4x4 world;
    uint     indexCount;
    uint     instanceCount; // 1 or 0 (culled)
    uint     firstIndex;
    int      baseVertex;
    uint     baseInstance;
    uint     _pad0;
    float4   color;
    float4   pbr;
};

[[vk::binding(0, 0)]] StructuredBuffer<GPURenderItem>    g_ItemsIn  : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<GPURenderItem> g_ItemsOut : register(u0);
[[vk::binding(2, 0)]] RWStructuredBuffer<uint>          g_DrawCount : register(u1);

struct CullPushData
{
    float4   uFrustumP0;
    float4   uFrustumP1;
    float4   uFrustumP2;
    float4   uFrustumP3;
    float4   uFrustumP4;
    float4   uFrustumP5;
    uint     uItemCount;
    uint3    uPad;
};

[[vk::push_constant]] CullPushData cullData;

[numthreads(64, 1, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= cullData.uItemCount) return;
    GPURenderItem item = g_ItemsIn[tid.x];

    float3 center = mul(item.world, float4(0, 0, 0, 1)).xyz;
    float radius = 0.75f * max(max(length(item.world[0].xyz), length(item.world[1].xyz)), length(item.world[2].xyz));

    bool visible = true;
    [unroll]
    for (int p = 0; p < 6; ++p)
    {
        float4 plane = (p == 0) ? cullData.uFrustumP0 : (p == 1) ? cullData.uFrustumP1 : (p == 2) ? cullData.uFrustumP2
                     : (p == 3) ? cullData.uFrustumP3 : (p == 4) ? cullData.uFrustumP4 : cullData.uFrustumP5;
        float dist = dot(plane.xyz, center) + plane.w;
        if (dist < -radius) { visible = false; break; }
    }

    item.instanceCount = visible ? 1 : 0;
    g_ItemsOut[tid.x] = item;

    if (visible) InterlockedAdd(g_DrawCount[0], 1);
}
