// =============================================================================
// DeferredPBR.vert.hlsl - GBuffer 顶点着色器 (GPU-Driven 兼容)
// 双路径：push-constant 直绘路径 + SSBO 间接绘制路径
// baseInstance==0  -> 直绘：使用 push constant pc.uWorld
// baseInstance!=0  -> 间接：使用 g_RenderItems[baseInstance-1]，
//                     并把 baseInstance 原样传给 FS（FS 内 -1 得 item 索引）
// =============================================================================

cbuffer CameraConstants : register(b0)
{
    float4x4 uView;
    float4x4 uProjection;
    float4x4 uViewProjection;
    float4   uCameraPos;
};

// 与 C# RenderItem 布局严格一致（112 字节，与 FrustumCull.comp.hlsl 相同）
struct RenderItem
{
    float4x4 world;      // offset 0
    float4   color;      // offset 64  (xyz=albedo, w=metallic)
    float4   pbr;        // offset 80  (x=roughness, y=ao, z=emissiveStrength, w=pad)
    uint     indexCount; // offset 96
    uint     firstIndex; // offset 100
    uint     baseVertex; // offset 104
    uint     _pad;       // offset 108
};

[[vk::binding(0, 1)]] StructuredBuffer<RenderItem> g_RenderItems : register(t0);

// 直绘路径的 push constant（间接路径不使用）
struct PerObjectPush
{
    float4x4 uWorld;
    float4   uColor;
    float4   uPBRParams;
};
[[vk::push_constant]] PerObjectPush pc;

struct VSInput
{
    [[vk::location(0)]] float3 inPos    : POSITION;
    [[vk::location(1)]] float3 inNormal : NORMAL;
    [[vk::location(2)]] float2 inUV     : TEXCOORD0;
};

struct VSOutput
{
    float4 pos      : SV_Position;
    float3 worldPos : TEXCOORD0;
    float3 normal   : TEXCOORD1;
    float2 uv       : TEXCOORD2;
    nointerpolation uint instIdx : TEXCOORD3; // 0=直绘；否则 = itemIndex+1
};

VSOutput VSMain(VSInput i, uint baseInstance : SV_InstanceID)
{
    VSOutput o;
    float4x4 world;

    if (baseInstance != 0)
    {
        // 间接绘制路径：baseInstance = itemIndex + 1
        RenderItem it = g_RenderItems[baseInstance - 1];
        world     = it.world;
        o.instIdx = baseInstance;
    }
    else
    {
        // 直绘路径：使用 push constant
        world     = pc.uWorld;
        o.instIdx = 0;
    }

    float4 wpos = mul(world, float4(i.inPos, 1.0));
    o.worldPos = wpos.xyz;
    o.pos      = mul(uViewProjection, wpos);
    o.normal   = normalize(mul((float3x3)world, i.inNormal));
    o.uv       = i.inUV;
    return o;
}
