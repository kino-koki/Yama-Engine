// =============================================================================
// FrustumCull.comp.hlsl - 视锥剔除 compute shader (GPU-Driven 管线)
// 每个线程处理一个 RenderItem，测试 6 个视锥平面，剔除的 instanceCount 置 0，
// 保留的直接把 GpuIndirectCommand 写入 g_IndirectCommands（保持顺序）。
// =============================================================================

// Push constant block: 6 planes + itemCount + pad = 112 bytes (matches C# CullPush)
struct CullPushData
{
    float4 uFrustumPlanes[6];  // 6 个视锥平面 (A,B,C,D)，指向视锥外为正
    uint   uItemCount;         // RenderItem 总数
    uint   uPad0;
    uint   uPad1;
    uint   uPad2;
};

[[vk::push_constant]] CullPushData cullData;

struct RenderItem
{
    float4x4 world;     // offset 0
    float4   color;     // xyz=albedo, w=metallic
    float4   pbr;       // x=roughness, y=ao, z=emissiveStrength, w=pad
    uint     indexCount;// offset 96
    uint     firstIndex;// offset 100
    uint     baseVertex;// offset 104
    uint     pad1;      // offset 108 (total 112 bytes)
};

[[vk::binding(1, 0)]] StructuredBuffer<RenderItem> g_Items : register(t0);

// 每个可见 item 写一条 indirect command
struct GpuIndirectCommand
{
    uint vertexCount;    // IndexCount
    uint instanceCount;  // 1 (可见) 或 0 (剔除)
    uint firstVertex;    // firstIndex
    int  baseVertex;     // baseVertex
    uint baseInstance;   // drawIndex + 1 (用于 VS 读 SSBO)
};

[[vk::binding(2, 0)]] RWStructuredBuffer<GpuIndirectCommand> g_OutCommands : register(u0);

[[vk::binding(3, 0)]] RWStructuredBuffer<uint> g_VisibleCount : register(u1);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= cullData.uItemCount)
        return;

    RenderItem item = g_Items[id.x];

    // 包围球：以模型原点为球心，半径 = world X 列长度 (假设均匀缩放)
    float scale = length(float3(item.world._11, item.world._12, item.world._13));
    float4 centerWS = mul(item.world, float4(0.0, 0.0, 0.0, 1.0));
    float radius = scale * 1.0; // 单位模型半径 1

    bool visible = true;
    [unroll]
    for (int i = 0; i < 6; ++i)
    {
        float4 p = cullData.uFrustumPlanes[i];
        float dist = dot(p.xyz, centerWS.xyz) + p.w;
        if (dist < -radius)
        {
            visible = false;
            break;
        }
    }

    g_OutCommands[id.x].vertexCount   = item.indexCount;
    g_OutCommands[id.x].instanceCount = visible ? 1u : 0u;
    g_OutCommands[id.x].firstVertex   = item.firstIndex;
    g_OutCommands[id.x].baseVertex    = (int)item.baseVertex;
    g_OutCommands[id.x].baseInstance  = id.x + 1; // VS 用这个读 SSBO

    if (visible)
        InterlockedAdd(g_VisibleCount[0], 1u);
}
