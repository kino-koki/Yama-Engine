// ════════════════════════════════════════════════════════════════════════════
// Yama Engine — MicroCluster Material Decode Pass
// 全屏 Pass: 读取可见性缓冲, 解码几何属性, 采样材质纹理, 输出最终表面属性
// 完全解耦几何与材质: 可见性缓冲只存簇/三角形/重心, 材质在此 Pass 求值
// 目标: Shader Model 6.6 (DXC 编译)
// 对应 C#: YamaEngine.Rendering.MicroClusterRenderer (材质解码阶段)
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲
// ──────────────────────────────────────────────────────────────────────────
cbuffer MicroClusterMaterialCB : register(b0)
{
    float4x4 gMatViewProjMatrix;
    float4x4 gMatInvViewProjMatrix;
    float3   gMatCameraPos;
    float    gMatPad0;
    float2   gMatInvResolution;
    int      gMatMaterialCount;
    float    gMatPad1;
};

// ──────────────────────────────────────────────────────────────────────────
// GPU 数据结构 (与 MicroClusterRaster.hlsl 一致)
// ──────────────────────────────────────────────────────────────────────────

#define NANITE_POS_QUANT_BITS 13

struct MicroClusterVertex
{
    uint   PackedPosition;
    uint   EncodedNormal;
    uint   EncodedU;
    uint   EncodedV;
    uint   TangentSign;
};

struct MicroClusterTriangle
{
    uint I0, I1, I2;
    uint MaterialSlot_EdgeFlags;
};

struct MicroClusterCluster
{
    uint   Id;
    uint   LodLevel;
    uint   FirstTriangle;
    uint   TriangleCount;
    float3 BoundsCenter;
    float  BoundsRadius;
    float  GeometricError;
    uint   PageId;
    uint   PageOffset;
    uint   ParentGroup;
    float3 NormalConeAxis;
    float  NormalConeCos;
    uint   MeshId;
};

// 材质描述 (绑定到材质纹理表)
struct MaterialData
{
    uint   AlbedoIndex;     // 纹理描述符表索引
    uint   NormalIndex;
    uint   RoughMetalIndex;
    uint   EmissiveIndex;
    float3 AlbedoTint;
    float  EmissiveStrength;
    float2 UVScale;
    float  UVOffsetX;
    float  UVOffsetY;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────

// 可见性缓冲 + 深度 (光栅化 Pass 输出)
Texture2D<uint2> gVisBuffer : register(t0);
Texture2D<float> gVisDepth  : register(t1);

// 簇 / 顶点 / 三角形缓冲
StructuredBuffer<MicroClusterCluster>  gMatClusters  : register(t2);
StructuredBuffer<MicroClusterVertex>   gMatVertices  : register(t3);
StructuredBuffer<MicroClusterTriangle> gMatTriangles : register(t4);

// 材质数据
StructuredBuffer<MaterialData> gMaterials : register(t5);

// 材质纹理 (通过 Texture2D 数组 / 描述符表绑定, 此处用绑定槽示例)
Texture2D gMatAlbedoTex   : register(t6);
Texture2D gMatNormalTex   : register(t7);
Texture2D gMatRoughMetalTex : register(t8);
Texture2D gMatEmissiveTex : register(t9);

// 输出 UAV (写入 G-Buffer / 最终属性)
RWTexture2D<float4> gMatAlbedoOut  : register(u0);
RWTexture2D<float4> gMatNormalOut  : register(u1);
RWTexture2D<float4> gMatEmissiveOut : register(u2);


// ──────────────────────────────────────────────────────────────────────────
// 解码工具 (与 MicroClusterRaster.hlsl 共享逻辑)
// ──────────────────────────────────────────────────────────────────────────
float3 DecodePosition(uint packed, float3 aabbMin, float aabbRange)
{
    uint mask = (1u << NANITE_POS_QUANT_BITS) - 1u;
    uint qx = packed & mask;
    uint qy = (packed >> NANITE_POS_QUANT_BITS) & mask;
    uint qz = (packed >> (2 * NANITE_POS_QUANT_BITS)) & mask;
    float invMax = 1.0 / (float)((1u << NANITE_POS_QUANT_BITS) - 1u);
    return aabbMin + float3(qx, qy, qz) * invMax * aabbRange;
}

float3 DecodeNormalOct16(uint encoded)
{
    float x = (float)((encoded >> 8) & 0xFF) / 255.0 * 2.0 - 1.0;
    float y = (float)(encoded & 0xFF) / 255.0 * 2.0 - 1.0;
    float z = 1.0 - abs(x) - abs(y);
    if (z < 0.0)
    {
        float sx = x >= 0.0 ? 1.0 : -1.0;
        float sy = y >= 0.0 ? 1.0 : -1.0;
        x = (1.0 - abs(y)) * sx;
        y = (1.0 - abs(x)) * sy;
    }
    return normalize(float3(x, y, z));
}

float2 DecodeUV(uint encodedU, uint encodedV)
{
    // 16 位半精度解码
    return float2(f16tof32(encodedU & 0xFFFF), f16tof32(encodedV & 0xFFFF));
}

// 解包可见性数据
void UnpackVisibility(uint2 visData, out uint clusterId, out uint triangleId, out float2 bary)
{
    clusterId = visData.x & 0xFFFFFF;
    triangleId = visData.x >> 24;
    bary = float2(f16tof32(visData.y & 0xFFFF), f16tof32(visData.y >> 16));
}


// ════════════════════════════════════════════════════════════════════════════
// MaterialDecodeVS — 全屏三角形顶点着色器
// 生成覆盖屏幕的三角形, 驱动材质解码像素着色器
// 入口点: MaterialDecodeVS
// ════════════════════════════════════════════════════════════════════════════
struct MaterialVSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

MaterialVSOutput MaterialDecodeVS(uint vertexID : SV_VertexID)
{
    MaterialVSOutput output;
    // 全屏三角形 (无顶点缓冲)
    output.TexCoord = float2((vertexID << 1) & 2, vertexID & 2);
    output.Position = float4(output.TexCoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}


// ════════════════════════════════════════════════════════════════════════════
// MaterialDecodePS — 材质解码像素着色器
// 从可见性缓冲解码几何, 插值属性, 采样材质, 输出表面属性 (Albedo/Normal/Emissive)
// 入口点: MaterialDecodePS
// ════════════════════════════════════════════════════════════════════════════
struct MaterialPSOutput
{
    float4 Albedo   : SV_Target0;  // rgb=反照率, a=粗糙度
    float4 Normal   : SV_Target1;  // xy=八面体编码法线, z=金属度, w=AO
    float4 Emissive : SV_Target2;  // rgb=自发光, a=着色模型
};

MaterialPSOutput MaterialDecodePS(MaterialVSOutput input)
{
    MaterialPSOutput output = (MaterialPSOutput)0;

    // 读取可见性缓冲
    uint2 pixel = uint2(input.Position.xy);
    uint2 visData = gVisBuffer[pixel];

    // 天空像素 (无几何命中)
    if (visData.x == 0 && visData.y == 0)
    {
        output.Albedo   = float4(0, 0, 0, 1);
        output.Normal   = float4(0.5, 0.5, 0, 1);
        output.Emissive = float4(0, 0, 0, 0);
        return output;
    }

    // 解包可见性数据
    uint clusterId, triangleId;
    float2 bary;
    UnpackVisibility(visData, clusterId, triangleId, bary);
    float3 weights = float3(1.0 - bary.x - bary.y, bary.x, bary.y);

    // 读取簇元数据
    MicroClusterCluster cluster = gMatClusters[clusterId];
    float3 aabbMin = cluster.BoundsCenter - cluster.BoundsRadius;
    float aabbRange = cluster.BoundsRadius * 2.0;

    // 读取三角形与顶点
    uint triBase = cluster.FirstTriangle + triangleId;
    MicroClusterTriangle tri = gMatTriangles[triBase];
    uint i0 = tri.I0 & 0xFF;
    uint i1 = tri.I1 & 0xFF;
    uint i2 = tri.I2 & 0xFF;
    uint materialSlot = tri.MaterialSlot_EdgeFlags & 0xFF;

    uint vertBase = cluster.FirstTriangle * 3 + triangleId * 3;
    MicroClusterVertex v0 = gMatVertices[vertBase + 0];
    MicroClusterVertex v1 = gMatVertices[vertBase + 1];
    MicroClusterVertex v2 = gMatVertices[vertBase + 2];

    // 解码并插值顶点属性
    float3 p0 = DecodePosition(v0.PackedPosition, aabbMin, aabbRange);
    float3 p1 = DecodePosition(v1.PackedPosition, aabbMin, aabbRange);
    float3 p2 = DecodePosition(v2.PackedPosition, aabbMin, aabbRange);
    float3 worldPos = p0 * weights.x + p1 * weights.y + p2 * weights.z;

    float3 n0 = DecodeNormalOct16(v0.EncodedNormal);
    float3 n1 = DecodeNormalOct16(v1.EncodedNormal);
    float3 n2 = DecodeNormalOct16(v2.EncodedNormal);
    float3 normal = normalize(n0 * weights.x + n1 * weights.y + n2 * weights.z);

    float2 uv0 = DecodeUV(v0.EncodedU, v0.EncodedV);
    float2 uv1 = DecodeUV(v1.EncodedU, v1.EncodedV);
    float2 uv2 = DecodeUV(v2.EncodedU, v2.EncodedV);
    float2 uv = uv0 * weights.x + uv1 * weights.y + uv2 * weights.z;

    // 获取材质数据 (按 materialSlot 索引)
    MaterialData mat = gMaterials[min(materialSlot, (uint)gMatMaterialCount - 1)];
    uv = uv * mat.UVScale + float2(mat.UVOffsetX, mat.UVOffsetY);

    // 采样材质纹理
    float4 albedoSample = gMatAlbedoTex.Sample(gLinearWrapSampler, uv);
    float3 albedo = albedoSample.rgb * mat.AlbedoTint;

    float4 roughMetal = gMatRoughMetalTex.Sample(gLinearWrapSampler, uv);
    float roughness = roughMetal.r;
    float metallic  = roughMetal.g;

    // 法线贴图 (切线空间 -> 世界空间)
    // 构建切线基 (从插值法线 + 屏幕导数近似)
    float3 N = normal;
    float3 dpdx = ddx(worldPos);
    float3 dpdy = ddy(worldPos);
    float2 duvdx = ddx(uv);
    float2 duvdy = ddy(uv);

    float3 T = normalize(dpdx * duvdy.y - dpdy * duvdx.y);
    float3 B = normalize(cross(N, T));
    T = cross(B, N);

    float3 tangentNormal = gMatNormalTex.Sample(gLinearWrapSampler, uv).xyz * 2.0 - 1.0;
    float3x3 TBN = float3x3(T, B, N);
    N = normalize(mul(TBN, tangentNormal));

    // 自发光
    float3 emissive = gMatEmissiveTex.Sample(gLinearWrapSampler, uv).rgb * mat.EmissiveStrength;

    // 输出表面属性 (使用八面体编码法线)
    output.Albedo   = float4(albedo, roughness);
    output.Normal   = float4(EncodeNormalOctahedral(N), metallic, 1.0);
    output.Emissive = float4(emissive, 1.0); // 着色模型 = PBR

    return output;
}
