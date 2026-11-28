// =============================================================================
// Fullscreen.vert.hlsl - 全屏三角形顶点着色器
// 用于光照 pass、后处理 pass
// =============================================================================

struct VSOutput
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOutput VSMain(uint vertexID : SV_VertexID)
{
    VSOutput o;
    // 全屏三角形：(-1,-1), (3,-1), (-1,3)
    float2 pos = float2(
        (vertexID == 1) ? 3.0 : -1.0,
        (vertexID == 2) ? 3.0 : -1.0);
    o.pos = float4(pos, 0.0, 1.0);
    o.uv  = pos * 0.5 + 0.5;
    o.uv.y = 1.0 - o.uv.y; // Vulkan UV origin top-left
    return o;
}
