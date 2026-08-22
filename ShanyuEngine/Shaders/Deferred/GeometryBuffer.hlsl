// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine — Deferred G-Buffer Shaders
//   G-Buffer 编码/解码:
//     · GeometryBufferVS / GeometryBufferPS         —— 传统几何 MRT G-Buffer 编码 (PBR 材质)
//     · GeometryBufferDecodeCS               —— MicroCluster 可见性缓冲 -> G-Buffer 解码 (材质)
//     · GeometryBufferCS                     —— 深度重投影运动矢量 (速度缓冲)
//   目标: Shader Model 6.6  (DXC: -T vs_6_6 / ps_6_6 / cs_6_6)
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

#define GBUF_TRIS_PER_CLUSTER 128u

// ── G-Buffer 解码常量 (镜像 C# GeometryBufferDecodeCB, 绑定 b2) ──
cbuffer GeometryBufferDecodeCB : register(b2)
{
    float4x4 dInvViewProj;
    float4x4 dViewProj;
    float4x4 dPrevViewProj;
    float4   dCameraPos_InvRes;   // xyz = CameraPos, w = unused
    float4   dScreenParams;       // xy = width/height, zw = inv width/height
    float4   dJitter;             // xy = current, zw = previous
};

// ── 资源绑定 ──
// 可见性缓冲 (R32G32_UINT): x = clusterID, y = (triID<<16)|(b0<<8)|b1)
Texture2D<uint2>            VisBuffer            : register(t0);
StructuredBuffer<uint>      VisibleClusterList   : register(t1);   // 可见簇索引列表

// 簇三角形数据 (镜像 C# MicroClusterClusterTriangle)
struct ClusterTriangle
{
    float3 V0; float3 V1; float3 V2;
    float2 UV0; float2 UV1; float2 UV2;
    float3 Normal;
    uint   MaterialIndex;
};
StructuredBuffer<ClusterTriangle> ClusterTriangles : register(t2);
StructuredBuffer<uint>            ClusterTriangleOffsets : register(t3); // 每簇三角形起始偏移

// 材质纹理数组 (slice = MaterialIndex)
Texture2DArray<float4> MatAlbedoArray   : register(t4);
Texture2DArray<float3> MatNormalArray   : register(t5);
Texture2DArray<float4> MatRMArray       : register(t6);   // r=rough, g=metal, b=ao
Texture2DArray<float3> MatEmissiveArray : register(t7);

// 深度 (运动矢量重投影用)
Texture2D<float> PrevDepth : register(t8);

// G-Buffer UAV (计算路径写入)
RWTexture2D<float4> GeometryBufferAlbedoUAV  : register(u0);  // rgb=albedo, a=roughness
RWTexture2D<float4> GeometryBufferNormalUAV  : register(u1);  // xy=oct normal, z=metallic, w=_
RWTexture2D<float4> GeometryBufferEmissiveUAV: register(u2);  // rgb=emissive, a=AO
RWTexture2D<float4> GeometryBufferMaterialUAV: register(u3);  // r=spec, g=matID, b=shading, a=custom
RWTexture2D<float2> VelocityUAV       : register(u4);  // 运动矢量

// ════════════════════════════════════════════════════════════════════════════
// 顶点/像素着色器结构 (传统几何路径, 使用 b0 FrameConstants + b1 ObjectConstants)
// ════════════════════════════════════════════════════════════════════════════
struct GeometryBufferVSInput
{
    float3 Position  : POSITION;
    float3 Normal    : NORMAL;
    float4 Tangent   : TANGENT;
    float2 TexCoord  : TEXCOORD0;
};

struct GeometryBufferVSOutput
{
    float4 Position     : SV_POSITION;
    float3 WorldPos     : WORLDPOS;
    float3 Normal       : NORMAL;
    float4 Tangent      : TANGENT;
    float2 TexCoord     : TEXCOORD0;
    float4 PrevPosition : PREV_POSITION;
};

// ════════════════════════════════════════════════════════════════════════════
// GeometryBufferVS — G-Buffer 顶点着色器
// ════════════════════════════════════════════════════════════════════════════
GeometryBufferVSOutput GeometryBufferVS(GeometryBufferVSInput input)
{
    GeometryBufferVSOutput o;
    float4 worldPos = mul(gWorldMatrix, float4(input.Position, 1.0));

    o.Position     = mul(gViewProjMatrix, worldPos);
    o.WorldPos     = worldPos.xyz;
    o.Normal       = normalize(mul((float3x3)gWorldInvTranspose, input.Normal));
    o.Tangent      = float4(normalize(mul((float3x3)gWorldMatrix, input.Tangent.xyz)), input.Tangent.w);
    o.TexCoord     = input.TexCoord;

    float4 prevWorld = mul(gPrevWorldMatrix, float4(input.Position, 1.0));
    o.PrevPosition   = mul(gPrevViewProjMatrix, prevWorld);

    return o;
}

// ════════════════════════════════════════════════════════════════════════════
// GeometryBufferPS — G-Buffer 像素着色器 (MRT 编码, PBR 材质)
// ════════════════════════════════════════════════════════════════════════════
struct GeometryBufferPSOutput
{
    float4 RT0 : SV_Target0;   // albedo, roughness
    float4 RT1 : SV_Target1;   // oct normal, metallic
    float4 RT2 : SV_Target2;   // emissive, AO
    float4 RT3 : SV_Target3;   // spec, matID, shading, custom
    float4 RT4 : SV_Target4;   // velocity (xy), depth(z)
};

GeometryBufferPSOutput GeometryBufferPS(GeometryBufferVSOutput inp)
{
    GeometryBufferPSOutput o;
    uint matID = gMaterialID;

    float4 albedoRGBA = MatAlbedoArray.Sample(gLinearWrapSampler, float3(inp.TexCoord, matID));
    float3 nTS        = normalize(MatNormalArray.Sample(gLinearWrapSampler, float3(inp.TexCoord, matID)) * 2.0 - 1.0);
    float4 rm         = MatRMArray.Sample(gLinearWrapSampler, float3(inp.TexCoord, matID));
    float3 emissive   = MatEmissiveArray.Sample(gLinearWrapSampler, float3(inp.TexCoord, matID));

    // TBN -> 世界空间法线
    float3 N = normalize(inp.Normal);
    float3 T = normalize(inp.Tangent.xyz);
    T = normalize(T - N * dot(T, N));
    float3 B = cross(N, T) * inp.Tangent.w;
    float3 worldNormal = normalize(mul(nTS, float3x3(T, B, N)));

    GeometryBuffer g;
    g.Albedo        = albedoRGBA.rgb;
    g.Roughness     = max(rm.r * rm.r, 0.045);
    g.Metallic      = rm.g;
    g.AO            = rm.b;
    g.Normal        = worldNormal;
    g.Emissive      = emissive;
    g.Specular      = 0.5;
    g.MaterialID    = (float)matID;
    g.ShadingModel  = 0.0;   // 0 = PBR
    g.Custom        = 0.0;

    EncodeGeometryBuffer(g, o.RT0, o.RT1, o.RT2, o.RT3);

    // 运动矢量 (含抖动补偿)
    float2 curUV  = inp.Position.xy * gScreenParams.zw;
    float2 prevUV = (inp.PrevPosition.xy / inp.PrevPosition.w) * 0.5 + 0.5;
    prevUV.y = 1.0 - prevUV.y;
    curUV  -= gJitterParams.xy;
    prevUV -= gJitterParams.zw;
    o.RT4 = float4(curUV - prevUV, inp.Position.z, 1.0);

    return o;
}

// ════════════════════════════════════════════════════════════════════════════
// 可见性缓冲解码工具
// ════════════════════════════════════════════════════════════════════════════
void DecodeVisibility(uint2 vis, out uint clusterID, out uint triID,
                      out float3 bary)
{
    clusterID = vis.x;
    triID     = vis.y >> 16;
    float b0 = (float)((vis.y >> 8) & 0xFF) / 255.0;
    float b1 = (float)(vis.y & 0xFF) / 255.0;
    bary = float3(b0, b1, max(0.0, 1.0 - b0 - b1));
}

// 从 NDC + 深度重建世界坐标
float3 DecodeReconstructWP(float2 uv, float depth)
{
    float4 clip = float4(uv * 2.0 - 1.0, depth, 1.0);
    clip.y = -clip.y;
    float4 wp = mul(dInvViewProj, clip);
    return wp.xyz / max(wp.w, SHANYU_EPSILON);
}

// ════════════════════════════════════════════════════════════════════════════
// GeometryBufferDecodeCS — MicroCluster 可见性缓冲 -> G-Buffer 解码
//   每像素读取可见性缓冲, 解码簇/三角形/重心坐标, 插值几何属性,
//   采样材质, 写入 G-Buffer UAV
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void GeometryBufferDecodeCS(uint3 dtid : SV_DispatchThreadID)
{
    int2 pixel = (int2)dtid.xy;
    if ((uint)pixel.x >= (uint)dScreenParams.x || (uint)pixel.y >= (uint)dScreenParams.y) return;

    uint2 vis = VisBuffer[pixel];
    if (vis.x == 0xFFFFFFFF) // 无几何 (天空)
    {
        GeometryBufferAlbedoUAV[pixel]   = float4(0, 0, 0, 1);
        GeometryBufferNormalUAV[pixel]   = float4(0.5, 0.5, 0, 0);
        GeometryBufferEmissiveUAV[pixel] = float4(0, 0, 0, 1);
        GeometryBufferMaterialUAV[pixel] = float4(0, 0, 0, 0);
        return;
    }

    uint clusterID, triID;
    float3 bary;
    DecodeVisibility(vis, clusterID, triID, bary);

    uint triOffset = ClusterTriangleOffsets[clusterID] + triID;
    ClusterTriangle tri = ClusterTriangles[triOffset];

    // 插值几何属性
    float3 worldPos  = tri.V0 * bary.x + tri.V1 * bary.y + tri.V2 * bary.z;
    float3 worldNorm = normalize(tri.Normal);
    float2 uv        = tri.UV0 * bary.x + tri.UV1 * bary.y + tri.UV2 * bary.z;

    // 采样材质
    uint matID = tri.MaterialIndex;
    float4 albedoRGBA = MatAlbedoArray.Sample(gLinearWrapSampler, float3(uv, matID));
    float3 nTS        = normalize(MatNormalArray.Sample(gLinearWrapSampler, float3(uv, matID)) * 2.0 - 1.0);
    float4 rm         = MatRMArray.Sample(gLinearWrapSampler, float3(uv, matID));
    float3 emissive   = MatEmissiveArray.Sample(gLinearWrapSampler, float3(uv, matID));

    // 几何法线作为切线基 (简化: 无切线时用几何法线)
    float3 N = worldNorm;
    float3 up = abs(N.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 T = normalize(cross(up, N));
    float3 B = cross(N, T);
    float3 worldNormal = normalize(mul(nTS, float3x3(T, B, N)));

    GeometryBuffer g;
    g.Albedo        = albedoRGBA.rgb;
    g.Roughness     = max(rm.r * rm.r, 0.045);
    g.Metallic      = rm.g;
    g.AO            = rm.b;
    g.Normal        = worldNormal;
    g.Emissive      = emissive;
    g.Specular      = 0.5;
    g.MaterialID    = (float)matID;
    g.ShadingModel  = 0.0;
    g.Custom        = 0.0;

    float4 rt0, rt1, rt2, rt3;
    EncodeGeometryBuffer(g, rt0, rt1, rt2, rt3);
    GeometryBufferAlbedoUAV[pixel]   = rt0;
    GeometryBufferNormalUAV[pixel]   = rt1;
    GeometryBufferEmissiveUAV[pixel] = rt2;
    GeometryBufferMaterialUAV[pixel] = rt3;
}

// ════════════════════════════════════════════════════════════════════════════
// GeometryBufferCS — 深度重投影运动矢量
//   当几何路径未内联写出速度时, 由本 CS 从当前/上一帧深度重投影生成.
//   适用于传统几何 (无 prev-position VS) 的场景.
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void GeometryBufferCS(uint3 dtid : SV_DispatchThreadID)
{
    int2 pixel = (int2)dtid.xy;
    if ((uint)pixel.x >= (uint)dScreenParams.x || (uint)pixel.y >= (uint)dScreenParams.y) return;

    float2 uv = (dtid.xy + 0.5) * dScreenParams.zw;
    float2 uvJittered = uv - dJitter.xy;

    // 当前帧世界坐标 (从深度重建) -> 上一帧裁剪空间
    // 此 CS 假定已有当前深度; 上一帧深度用于验证是否同一表面
    float depth = GeometryBufferAlbedoUAV[pixel].a; // 占位: 实际从深度 SRV 读取
    // 注: 此处使用 GeometryBufferAlbedoUAV.a 仅作占位; 工程中绑定 SceneDepth SRV.
    if (depth <= 0.0 || depth >= 1.0)
    {
        VelocityUAV[pixel] = float2(0, 0);
        return;
    }

    float3 worldPos = DecodeReconstructWP(uvJittered, depth);
    float4 prevClip = mul(dPrevViewProj, float4(worldPos, 1.0));
    float2 prevUV = (prevClip.xy / prevClip.w) * 0.5 + 0.5;
    prevUV.y = 1.0 - prevUV.y;
    prevUV -= dJitter.zw;

    VelocityUAV[pixel] = uv - prevUV;
}
