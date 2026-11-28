// =============================================================================
// DeferredPBR.frag.hlsl - GBuffer 片段着色器 (MRT 5, GPU-Driven 兼容)
// GBuffer[0] = WorldPos (RGBA16F)
// GBuffer[1] = Albedo  (RGBA8, A=metallic)
// GBuffer[2] = Normal  (RGBA16F, xyz)
// GBuffer[3] = PBR     (R=roughness, G=ao, B=emissiveStrength, A=pad)
// GBuffer[4] = Emissive (RGBA16F)
// =============================================================================

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

// 直绘路径 push constant
struct PerObjectPush
{
    float4x4 uWorld;
    float4   uColor;
    float4   uPBRParams;
};
[[vk::push_constant]] PerObjectPush pc;

struct VSOutput
{
    float4 pos      : SV_Position;
    float3 worldPos : TEXCOORD0;
    float3 normal   : TEXCOORD1;
    float2 uv       : TEXCOORD2;
    nointerpolation uint instIdx : TEXCOORD3; // 0=直绘；否则 = itemIndex+1
};

struct PSOutput
{
    float4 worldPos : SV_Target0;
    float4 albedo   : SV_Target1;
    float4 normal   : SV_Target2;
    float4 pbr      : SV_Target3;
    float4 emissive : SV_Target4;
};

PSOutput PSMain(VSOutput i)
{
    PSOutput o;

    float4 color, pbr;
    if (i.instIdx != 0)
    {
        // 间接绘制路径：从 SSBO 读取材质（instIdx = itemIndex + 1）
        RenderItem it = g_RenderItems[i.instIdx - 1];
        color = it.color;
        pbr   = it.pbr;
    }
    else
    {
        // 直绘路径：使用 push constant
        color = pc.uColor;
        pbr   = pc.uPBRParams;
    }

    o.worldPos = float4(i.worldPos, 1.0);
    o.albedo   = float4(color.rgb, color.a);
    o.normal   = float4(normalize(i.normal), 1.0);
    o.pbr      = float4(pbr.x, pbr.y, pbr.z, 1.0);
    o.emissive = float4(color.rgb * pbr.z, 1.0);
    return o;
}
