// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine — MicroCluster Visibility Buffer
// 可见性缓冲写入、属性解码与 GeometryBuffer 生成。
//
// 可见性缓冲 (Visibility Buffer) 是 MicroCluster 延迟渲染的核心:
//   几何 Pass 仅写入最小信息 (instanceId/triangleId/barycentric),
//   材质 Pass 后续从可见性缓冲解码几何属性并执行材质着色。
// 这种几何/材质解耦使得:
//   - 光栅化极快 (无材质开销, 仅写 8 bytes/pixel)
//   - 材质 Pass 按像素执行, 无 overdraw 浪费
//
// 可见性缓冲格式: R32G32_UINT (8 bytes/pixel)
//   .x: instanceId(20bit) | triangleId(12bit)
//   .y: f16(bary.x) | f16(bary.y)
//
// 着色器入口:
//   1. VisibilityWritePS     — 硬件光栅化路径的像素着色器 (写可见性缓冲)
//   2. VisibilityDecodeCS    — 可见性缓冲解码 (Compute, 插值属性 -> GeometryBuffer)
//   3. MaterialPassPS        — 材质 Pass 像素着色器 (从 GeometryBuffer + 可见性缓冲着色)
//
// 对应 C#: ShanyuEngine.Rendering.GPUDrivenPipeline (DecodeVisibilityBuffer)
// 目标: Shader Model 6.6 (DXC 编译, barycentrics)
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量
// ──────────────────────────────────────────────────────────────────────────
#define NANITE_POS_QUANT_BITS 13
#define NANITE_TRIANGLES_PER_CLUSTER 128
#define NANITE_MAX_VERTICES_PER_CLUSTER 256

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (匹配 C# VisibilityGenCB / AttributeDecodeCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer VisibilityGenCB : register(b0)
{
    float4x4 gViewProjMatrix;
    float2   gInvResolution;
    uint     gVisibleClusterCount;
    uint     gFrameIndex;
    uint     gPad0, gPad1, gPad2, gPad3;
};

cbuffer AttributeDecodeCB : register(b1)
{
    float4x4 gInvViewProjMatrix;
    float3   gCameraPos;
    float    gDecodePad0;
    float2   gDecodeInvResolution;
    uint     gMaterialCount;
    uint     gDecodePad1;
    uint     gDecodePad2, gDecodePad3, gDecodePad4;
};

// ──────────────────────────────────────────────────────────────────────────
// GPU 数据结构 (匹配 C# MicroCluster structs)
// ──────────────────────────────────────────────────────────────────────────
struct MicroClusterVertex
{
    uint  PackedPosition;
    uint  EncodedNormal;
    uint  EncodedU;
    uint  EncodedV;
    uint  TangentSign;
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

struct VisibleCluster
{
    uint ClusterIndex;
    uint MeshId;
    uint IsSoftwareRaster;
    uint Padding;
};

struct GPUInstanceData
{
    float4 TransformRow0;
    float4 TransformRow1;
    float4 TransformRow2;
    float3 BoundsCenter;
    float  BoundsRadius;
    uint   MeshIndex;
    uint   MaterialIndexBase;
    uint   LodLevel;
    uint   Flags;
    float3 PrevBoundsCenter;
    uint   InstanceId;
    float2 PrevScreenMin;
    float2 PrevScreenMax;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
StructuredBuffer<MicroClusterCluster>  gClusters       : register(t0);
StructuredBuffer<MicroClusterVertex>    gVertices       : register(t1);
StructuredBuffer<MicroClusterTriangle>  gTriangles      : register(t2);
StructuredBuffer<VisibleCluster>  gVisibleClusters: register(t3);
StructuredBuffer<GPUInstanceData> gInstances      : register(t4);

// 可见性缓冲 (解码 Pass 读取)
Texture2D<uint2>  gVisibilityBufferSRV : register(t5);
// 深度缓冲 (解码 Pass 读取)
Texture2D<float>  gDepthSRV            : register(t6);
// 材质纹理数组
Texture2DArray<float4> gMaterialTextures : register(t7);

// UAV 输出
RWTexture2D<uint2>  gVisibilityBufferUAV : register(u0);
RWTexture2D<float>  gDepthUAV            : register(u1);
RWTexture2D<float4> gGeometryBufferAlbedo       : register(u2);
RWTexture2D<float4> gGeometryBufferNormal       : register(u3);
RWTexture2D<float4> gGeometryBufferEmissive     : register(u4);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 位置解码: 量化打包位置 -> 世界坐标
float3 DecodePosition(uint packed, float3 aabbMin, float aabbRange)
{
    uint mask = (1u << NANITE_POS_QUANT_BITS) - 1u;
    uint qx = packed & mask;
    uint qy = (packed >> NANITE_POS_QUANT_BITS) & mask;
    uint qz = (packed >> (2 * NANITE_POS_QUANT_BITS)) & mask;
    float invMax = 1.0 / (float)((1u << NANITE_POS_QUANT_BITS) - 1u);
    return aabbMin + float3(qx, qy, qz) * invMax * aabbRange;
}

// 八面体解码 (16 位 -> float3)
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

// 打包可见性数据: instanceId(20) | triangleId(12), barycentric(f16x2)
uint2 PackVisibility(uint instanceId, uint triangleId, float2 bary)
{
    uint x = (instanceId & 0xFFFFF) | ((triangleId & 0xFFF) << 20);
    uint y = f32tof16(bary.x) | (f32tof16(bary.y) << 16);
    return uint2(x, y);
}

// 解包可见性数据
void UnpackVisibility(uint2 vis, out uint instanceId, out uint triangleId, out float2 bary)
{
    instanceId = vis.x & 0xFFFFF;
    triangleId = (vis.x >> 20) & 0xFFF;
    bary = float2(f16tof32(vis.y & 0xFFFF), f16tof32(vis.y >> 16));
}

// 实例变换: 局部坐标 -> 世界坐标
float3 TransformInstance(GPUInstanceData inst, float3 localPos)
{
    float3 worldPos;
    worldPos.x = dot(inst.TransformRow0.xyz, localPos) + inst.TransformRow0.w;
    worldPos.y = dot(inst.TransformRow1.xyz, localPos) + inst.TransformRow1.w;
    worldPos.z = dot(inst.TransformRow2.xyz, localPos) + inst.TransformRow2.w;
    return worldPos;
}

// 实例法线变换: 局部法线 -> 世界法线 (假设均匀缩放)
float3 TransformNormalInstance(GPUInstanceData inst, float3 localNormal)
{
    return normalize(float3(
        dot(inst.TransformRow0.xyz, localNormal),
        dot(inst.TransformRow1.xyz, localNormal),
        dot(inst.TransformRow2.xyz, localNormal)));
}


// ════════════════════════════════════════════════════════════════════════════
// VisibilityWritePS — 硬件光栅化路径像素着色器
//
// Mesh Shader 输出三角形后, 硬件光栅化器对每个覆盖像素调用此 PS。
// 仅写入最小信息到可见性缓冲: instanceId + triangleId + barycentric。
// 不执行任何材质计算, 保证几何 Pass 极快。
// ════════════════════════════════════════════════════════════════════════════

struct VisibilityVSOutput
{
    float4 Position   : SV_POSITION;
    uint   InstanceId : TEXCOORD0;
    uint   TriangleId : TEXCOORD1;
    uint   ClusterIdx : TEXCOORD2;
};

struct VisibilityPSOutput
{
    uint2 VisData : SV_Target0;
    float Depth   : SV_Depth;
};

VisibilityPSOutput VisibilityWritePS(
    VisibilityVSOutput input,
    uint primitiveID : SV_PrimitiveID,
    float3 bary : SV_Barycentrics)
{
    VisibilityPSOutput output;
    // 重心坐标 (取前两维, 第三维 = 1 - x - y)
    float2 bary2 = bary.xy;
    // 写入可见性缓冲: instanceId(20) | triangleId(12), barycentric(f16x2)
    output.VisData = PackVisibility(input.InstanceId, input.TriangleId, bary2);
    output.Depth = input.Position.z;
    return output;
}


// ════════════════════════════════════════════════════════════════════════════
// VisibilityDecodeCS — 可见性缓冲解码 (Compute Shader)
//
// 从可见性缓冲读取每像素命中的三角形, 插值顶点属性,
// 输出 GeometryBuffer (Albedo/Normal/Emissive)。
// 几何与材质完全解耦: 此 Pass 仅输出几何属性。
//
// 入口点: VisibilityDecodeCS (匹配 C# GPUDrivenPipeline.DecodeVisibilityBuffer)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void VisibilityDecodeCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    float2 screenRes = 1.0 / gDecodeInvResolution;

    if (pixel.x >= (uint)screenRes.x || pixel.y >= (uint)screenRes.y)
        return;

    // 读取可见性缓冲
    uint2 vis = gVisibilityBufferSRV[pixel];

    // 空像素 (天空/背景)
    if (vis.x == 0 && vis.y == 0)
    {
        gGeometryBufferAlbedo[pixel] = float4(0, 0, 0, 0);
        gGeometryBufferNormal[pixel] = float4(0, 0, 0, 0);
        gGeometryBufferEmissive[pixel] = float4(0, 0, 0, 0);
        return;
    }

    // 解包可见性数据
    uint instanceId, triangleId;
    float2 bary;
    UnpackVisibility(vis, instanceId, triangleId, bary);

    float b0 = 1.0 - bary.x - bary.y;
    float b1 = bary.x;
    float b2 = bary.y;

    // 加载实例数据
    GPUInstanceData inst = gInstances[instanceId];

    // 从可见簇列表获取簇索引
    VisibleCluster visCluster = gVisibleClusters[instanceId];
    MicroClusterCluster cluster = gClusters[visCluster.ClusterIndex];

    // 加载三角形顶点
    MicroClusterTriangle tri = gTriangles[cluster.FirstTriangle + triangleId];
    uint i0 = tri.I0 & 0xFF;
    uint i1 = tri.I1 & 0xFF;
    uint i2 = tri.I2 & 0xFF;

    uint vertBase = cluster.FirstTriangle * 3 + triangleId * 3;
    MicroClusterVertex v0 = gVertices[vertBase + 0];
    MicroClusterVertex v1 = gVertices[vertBase + 1];
    MicroClusterVertex v2 = gVertices[vertBase + 2];

    float3 aabbMin = cluster.BoundsCenter - cluster.BoundsRadius;
    float aabbRange = cluster.BoundsRadius * 2.0;

    // --- 解码并插值顶点位置 ---
    float3 localP0 = DecodePosition(v0.PackedPosition, aabbMin, aabbRange);
    float3 localP1 = DecodePosition(v1.PackedPosition, aabbMin, aabbRange);
    float3 localP2 = DecodePosition(v2.PackedPosition, aabbMin, aabbRange);

    float3 worldP0 = TransformInstance(inst, localP0);
    float3 worldP1 = TransformInstance(inst, localP1);
    float3 worldP2 = TransformInstance(inst, localP2);

    float3 worldPos = worldP0 * b0 + worldP1 * b1 + worldP2 * b2;

    // --- 解码并插值法线 ---
    float3 n0 = DecodeNormalOct16(v0.EncodedNormal);
    float3 n1 = DecodeNormalOct16(v1.EncodedNormal);
    float3 n2 = DecodeNormalOct16(v2.EncodedNormal);

    float3 worldN0 = TransformNormalInstance(inst, n0);
    float3 worldN1 = TransformNormalInstance(inst, n1);
    float3 worldN2 = TransformNormalInstance(inst, n2);

    float3 normal = normalize(worldN0 * b0 + worldN1 * b1 + worldN2 * b2);

    // --- 解码并插值 UV ---
    float2 uv0 = float2(f16tof32(v0.EncodedU), f16tof32(v0.EncodedV));
    float2 uv1 = float2(f16tof32(v1.EncodedU), f16tof32(v1.EncodedV));
    float2 uv2 = float2(f16tof32(v2.EncodedU), f16tof32(v2.EncodedV));
    float2 uv = uv0 * b0 + uv1 * b1 + uv2 * b2;

    // --- 解码切线 ---
    // TangentSign: 低 8 位 = 切线方向索引, 高 8 位 = 手性符号
    float tangentSign0 = (v0.TangentSign & 0x80) ? -1.0 : 1.0;
    float tangentSign1 = (v1.TangentSign & 0x80) ? -1.0 : 1.0;
    float tangentSign2 = (v2.TangentSign & 0x80) ? -1.0 : 1.0;
    float tangentSign = tangentSign0 * b0 + tangentSign1 * b1 + tangentSign2 * b2;

    // 重建切线 (Gram-Schmidt 正交化)
    float3 tangent = normalize(cross(normal, float3(0, 1, 0)) + float3(SHANYU_EPSILON, 0, 0));
    float3 bitangent = cross(normal, tangent) * tangentSign;

    // 材质 ID
    uint materialSlot = tri.MaterialSlot_EdgeFlags & 0xFF;
    uint materialId = inst.MaterialIndexBase + materialSlot;

    // 视线方向
    float3 viewDir = normalize(gCameraPos - worldPos);

    // --- 写入 GeometryBuffer ---
    // RT0: Albedo (占位, 由材质 Pass 填充实际反照率) + Roughness
    gGeometryBufferAlbedo[pixel] = float4(1.0, 1.0, 1.0, 0.5);

    // RT1: Normal (八面体编码) + Metallic + MaterialID
    float2 encodedNormal = EncodeNormalOctahedral(normal);
    gGeometryBufferNormal[pixel] = float4(encodedNormal, 0.0, materialId / 255.0);

    // RT2: Emissive (占位) + AO
    gGeometryBufferEmissive[pixel] = float4(0.0, 0.0, 0.0, 1.0);
}


// ════════════════════════════════════════════════════════════════════════════
// MaterialPassPS — 材质 Pass 像素着色器
//
// 从可见性缓冲 + GeometryBuffer 读取几何信息, 执行材质着色 (采样材质纹理)。
// 按像素执行, 无 overdraw 浪费。
// ════════════════════════════════════════════════════════════════════════════

struct MaterialPassVSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct MaterialPassPSOutput
{
    float4 Albedo    : SV_Target0;
    float4 NormalRM  : SV_Target1;  // Normal(Oct) + Roughness + Metallic
    float4 Emissive  : SV_Target2;
};

MaterialPassPSOutput MaterialPassPS(MaterialPassVSOutput input)
{
    MaterialPassPSOutput output;

    uint2 pixel = uint2(input.Position.xy);

    // 读取可见性缓冲获取材质 ID
    uint2 vis = gVisibilityBufferSRV[pixel];
    uint instanceId = vis.x & 0xFFFFF;
    uint triangleId = (vis.x >> 20) & 0xFFF;

    if (vis.x == 0 && vis.y == 0)
    {
        // 天空像素
        output.Albedo = float4(0.4, 0.6, 0.9, 1.0);
        output.NormalRM = float4(0.5, 0.5, 0.0, 1.0);
        output.Emissive = float4(0.4, 0.6, 0.9, 1.0);
        return output;
    }

    // 加载实例与三角形获取材质
    GPUInstanceData inst = gInstances[instanceId];
    VisibleCluster visCluster = gVisibleClusters[instanceId];
    MicroClusterCluster cluster = gClusters[visCluster.ClusterIndex];
    MicroClusterTriangle tri = gTriangles[cluster.FirstTriangle + triangleId];

    uint materialSlot = tri.MaterialSlot_EdgeFlags & 0xFF;
    uint materialId = inst.MaterialIndexBase + materialSlot;

    // 解包重心坐标
    float2 bary = float2(f16tof32(vis.y & 0xFFFF), f16tof32(vis.y >> 16));

    // 解码 UV
    uint vertBase = cluster.FirstTriangle * 3 + triangleId * 3;
    MicroClusterVertex v0 = gVertices[vertBase + 0];
    MicroClusterVertex v1 = gVertices[vertBase + 1];
    MicroClusterVertex v2 = gVertices[vertBase + 2];

    float2 uv0 = float2(f16tof32(v0.EncodedU), f16tof32(v0.EncodedV));
    float2 uv1 = float2(f16tof32(v1.EncodedU), f16tof32(v1.EncodedV));
    float2 uv2 = float2(f16tof32(v2.EncodedU), f16tof32(v2.EncodedV));

    float b0 = 1.0 - bary.x - bary.y;
    float2 uv = uv0 * b0 + uv1 * bary.x + uv2 * bary.y;

    // 采样材质纹理 (材质纹理数组)
    float4 albedoTex = gMaterialTextures.Sample(gLinearWrapSampler, float3(uv, materialId));

    // 读取 GeometryBuffer 法线
    float4 geometrybufferNormal = gGeometryBufferNormal[pixel];
    float3 normal = DecodeNormalOctahedral(geometrybufferNormal.xy);

    // 输出最终 GeometryBuffer
    output.Albedo = float4(albedoTex.rgb, 0.5); // Roughness 占位
    output.NormalRM = float4(EncodeNormalOctahedral(normal), 0.0, materialId / 255.0);
    output.Emissive = float4(0.0, 0.0, 0.0, 1.0);

    return output;
}
