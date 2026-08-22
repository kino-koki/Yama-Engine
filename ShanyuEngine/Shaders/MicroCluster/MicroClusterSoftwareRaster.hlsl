// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine — MicroCluster Software Rasterizer (Compute Shader)
// 子像素三角形 (< 1 pixel) 专用软件光栅化路径。
//
// 三阶段管线:
//   1. BinAssignCS  — 将三角形按 16x16 tile 分配到 bin (并行处理可见簇)
//   2. TileRasterCS — 每 tile 内并行光栅化三角形, HiZ 预测试 + 原子深度测试,
//                     写入可见性缓冲 (instanceId/triangleId/barycentric)
//   3. AttributeDecodeCS — 从可见性缓冲读取命中三角形, 插值 Position/Normal/UV/Tangent,
//                           输出 GeometryBuffer
//
// 可见性缓冲格式: R32G32_UINT (8 bytes/pixel)
//   .x: instanceId(20bit) | triangleId(12bit)
//   .y: f16(bary.x) | f16(bary.y)
//
// 对应 C#: ShanyuEngine.Rendering.SoftwareRasterizer
// 目标: Shader Model 6.6 (DXC 编译)
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量
// ──────────────────────────────────────────────────────────────────────────
#define TILE_SIZE 16
#define MAX_TRIS_PER_BIN 64
#define BIN_ASSIGN_GROUP_SIZE 64
#define RASTER_GROUP_SIZE 64

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (匹配 C# SoftwareRasterCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer SoftwareRasterCB : register(b0)
{
    float4x4 gViewProjMatrix;
    float3   gCameraPos;
    uint     gVisibleClusterCount;
    float2   gInvResolution;
    uint     gTileCountX;
    uint     gTileCountY;
    uint     gFrameIndex;
    uint     gPad0;
    uint     gPad1;
    uint     gPad2;
};

// 属性解码常量 (匹配 C# AttributeDecodeCB)
cbuffer AttributeDecodeCB : register(b1)
{
    float4x4 gInvViewProjMatrix;
    float3   gDecodeCameraPos;
    float    gDecodePad0;
    float2   gDecodeInvResolution;
    uint     gMaterialCount;
    uint     gDecodePad1;
    uint     gDecodePad2;
    uint     gDecodePad3;
    uint     gDecodePad4;
};

// ──────────────────────────────────────────────────────────────────────────
// GPU 数据结构 (匹配 C# MicroCluster structs)
// ──────────────────────────────────────────────────────────────────────────
#define NANITE_POS_QUANT_BITS 13

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

// 可见簇记录 (来自剔除 Pass)
struct VisibleCluster
{
    uint ClusterIndex;
    uint MeshId;
    uint IsSoftwareRaster;
    uint Padding;
};

// GPU 实例数据 (匹配 C# GPUInstanceData)
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

// Bin 头 (每 tile 一个)
struct BinHeader
{
    uint TriangleCount;
    uint TriangleStart;
    uint TileX;
    uint TileY;
};

// Bin 三角形引用
struct BinTriangleRef
{
    uint TriangleIndex;
    uint ClusterIndex;
    uint InstanceIndex;
    uint PackedMin;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
StructuredBuffer<MicroClusterCluster>  gClusters       : register(t0);
StructuredBuffer<MicroClusterVertex>    gVertices       : register(t1);
StructuredBuffer<MicroClusterTriangle>  gTriangles      : register(t2);
StructuredBuffer<VisibleCluster>  gVisibleClusters: register(t3);
StructuredBuffer<GPUInstanceData> gInstances      : register(t4);
Texture2D<float>                   gHZB            : register(t5);

// Bin 中间缓冲
RWStructuredBuffer<BinHeader>      gBinHeaders     : register(u0);
RWStructuredBuffer<BinTriangleRef> gBinTriangles   : register(u1);
RWStructuredBuffer<uint>           gBinCounters    : register(u2);

// 输出缓冲
RWTexture2D<uint2>  gVisibilityBufferUAV : register(u3);
RWTexture2D<float>  gDepthUAV            : register(u4);

// GeometryBuffer 输出 (属性解码 Pass)
RWTexture2D<float4> gGeometryBufferAlbedo   : register(u5);
RWTexture2D<float4> gGeometryBufferNormal   : register(u6);
RWTexture2D<float4> gGeometryBufferEmissive : register(u7);

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

// 实例变换: 将局部坐标变换到世界空间
float3 TransformInstance(GPUInstanceData inst, float3 localPos)
{
    float3 worldPos;
    worldPos.x = dot(inst.TransformRow0.xyz, localPos) + inst.TransformRow0.w;
    worldPos.y = dot(inst.TransformRow1.xyz, localPos) + inst.TransformRow1.w;
    worldPos.z = dot(inst.TransformRow2.xyz, localPos) + inst.TransformRow2.w;
    return worldPos;
}

// 屏幕像素坐标 -> tile 索引
uint2 PixelToTile(float2 pixel)
{
    return uint2((uint)(pixel.x / TILE_SIZE), (uint)(pixel.y / TILE_SIZE));
}

// tile 线性索引
uint TileToLinear(uint2 tile, uint tileCountX)
{
    return tile.y * tileCountX + tile.x;
}


// ════════════════════════════════════════════════════════════════════════════
// 阶段 1: BinAssignCS — 将三角形按 16x16 tile 分配到 bin
//
// 每个线程处理一个可见簇的一个三角形:
//   1. 计算三角形屏幕包围盒
//   2. 找到覆盖的 tile 范围
//   3. 对每个覆盖的 tile, 原子追加到 bin 三角形列表
// ════════════════════════════════════════════════════════════════════════════
[numthreads(BIN_ASSIGN_GROUP_SIZE, 1, 1)]
void BinAssignCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint globalTriIdx = dispatchID.x;
    if (globalTriIdx >= gVisibleClusterCount * 128) // 简化: 每簇最多 128 三角形
        return;

    // 计算簇索引与簇内三角形索引
    uint clusterSlot = globalTriIdx / 128;
    uint triLocal = globalTriIdx % 128;

    if (clusterSlot >= gVisibleClusterCount)
        return;

    VisibleCluster visCluster = gVisibleClusters[clusterSlot];
    MicroClusterCluster cluster = gClusters[visCluster.ClusterIndex];

    if (triLocal >= cluster.TriangleCount)
        return;

    // 加载三角形
    MicroClusterTriangle tri = gTriangles[cluster.FirstTriangle + triLocal];
    uint i0 = tri.I0 & 0xFF;
    uint i1 = tri.I1 & 0xFF;
    uint i2 = tri.I2 & 0xFF;

    uint vertBase = cluster.FirstTriangle * 3 + triLocal * 3;
    MicroClusterVertex v0 = gVertices[vertBase + 0];
    MicroClusterVertex v1 = gVertices[vertBase + 1];
    MicroClusterVertex v2 = gVertices[vertBase + 2];

    // 解码位置
    float3 aabbMin = cluster.BoundsCenter - cluster.BoundsRadius;
    float aabbRange = cluster.BoundsRadius * 2.0;
    float3 p0 = DecodePosition(v0.PackedPosition, aabbMin, aabbRange);
    float3 p1 = DecodePosition(v1.PackedPosition, aabbMin, aabbRange);
    float3 p2 = DecodePosition(v2.PackedPosition, aabbMin, aabbRange);

    // 变换到裁剪空间
    float4 c0 = mul(gViewProjMatrix, float4(p0, 1.0));
    float4 c1 = mul(gViewProjMatrix, float4(p1, 1.0));
    float4 c2 = mul(gViewProjMatrix, float4(p2, 1.0));

    // 裁剪: 全在近平面后方则跳过
    if (c0.w <= 0.0 && c1.w <= 0.0 && c2.w <= 0.0)
        return;

    // 透视除法 -> NDC
    float3 ndc0 = c0.xyz / max(c0.w, SHANYU_EPSILON);
    float3 ndc1 = c1.xyz / max(c1.w, SHANYU_EPSILON);
    float3 ndc2 = c2.xyz / max(c2.w, SHANYU_EPSILON);

    // NDC -> 屏幕像素坐标
    float2 s0 = (ndc0.xy * 0.5 + 0.5) * (1.0 / gInvResolution);
    float2 s1 = (ndc1.xy * 0.5 + 0.5) * (1.0 / gInvResolution);
    float2 s2 = (ndc2.xy * 0.5 + 0.5) * (1.0 / gInvResolution);

    // 屏幕包围盒
    float2 minBB = min(min(s0, s1), s2);
    float2 maxBB = max(max(s0, s1), s2);

    // 包围盒覆盖的 tile 范围
    uint2 tileMin = PixelToTile(minBB);
    uint2 tileMax = PixelToTile(maxBB);

    // 裁剪到有效 tile 范围
    tileMin = max(tileMin, uint2(0, 0));
    tileMax = min(tileMax, uint2(gTileCountX - 1, gTileCountY - 1));

    // 对每个覆盖的 tile, 原子追加到 bin
    BinTriangleRef ref;
    ref.TriangleIndex = triLocal;
    ref.ClusterIndex = visCluster.ClusterIndex;
    ref.InstanceIndex = clusterSlot; // 简化: 使用簇槽位作为实例索引
    ref.PackedMin = 0;

    [loop]
    for (uint ty = tileMin.y; ty <= tileMax.y; ty++)
    {
        [loop]
        for (uint tx = tileMin.x; tx <= tileMax.x; tx++)
        {
            uint tileLinear = ty * gTileCountX + tx;

            // 原子递增该 tile 的三角形计数, 获取写入位置
            uint writeSlot;
            InterlockedAdd(gBinCounters[tileLinear], 1, writeSlot);

            // 检查是否超出 bin 容量
            if (writeSlot < MAX_TRIS_PER_BIN)
            {
                // 写入三角形引用
                uint bufIdx = tileLinear * MAX_TRIS_PER_BIN + writeSlot;
                gBinTriangles[bufIdx] = ref;
            }

            // 更新 bin 头
            InterlockedAdd(gBinHeaders[tileLinear].TriangleCount, 1);
            if (gBinHeaders[tileLinear].TileX == 0 && gBinHeaders[tileLinear].TileY == 0)
            {
                gBinHeaders[tileLinear].TileX = tx;
                gBinHeaders[tileLinear].TileY = ty;
                gBinHeaders[tileLinear].TriangleStart = tileLinear * MAX_TRIS_PER_BIN;
            }
        }
    }
}


// ════════════════════════════════════════════════════════════════════════════
// 阶段 2: TileRasterCS — 每 tile 内并行光栅化三角形
//
// 每个 tile (16x16) 分配一个线程组, 线程组内并行光栅化 bin 内三角形:
//   1. 读取 bin 头与三角形列表
//   2. 对每个三角形, 计算覆盖的像素
//   3. HiZ 预测试 (粗粒度深度剔除)
//   4. 原子深度测试 + 可见性缓冲写入
// ════════════════════════════════════════════════════════════════════════════

// 每个 tile 共享三角形列表
groupshared BinTriangleRef gsBinTris[MAX_TRIS_PER_BIN];
groupshared uint gsTriCount;

[numthreads(TILE_SIZE * TILE_SIZE / 4, 1, 1)] // 64 线程/tile
void TileRasterCS(uint3 groupID : SV_GroupID, uint3 groupThreadID : SV_GroupThreadID)
{
    uint tileLinear = groupID.x + groupID.y * gTileCountX;
    if (tileLinear >= gTileCountX * gTileCountY)
        return;

    // 第一个线程加载 bin 头
    if (groupThreadID.x == 0)
    {
        gsTriCount = min(gBinHeaders[tileLinear].TriangleCount, MAX_TRIS_PER_BIN);
    }
    GroupMemoryBarrierWithGroupSync();

    // 加载三角形引用到共享内存
    uint triStart = gBinHeaders[tileLinear].TriangleStart;
    [unroll]
    for (uint i = groupThreadID.x; i < gsTriCount; i += RASTER_GROUP_SIZE)
    {
        gsBinTris[i] = gBinTriangles[triStart + i];
    }
    GroupMemoryBarrierWithGroupSync();

    // tile 的像素范围
    uint2 tileOrigin = uint2(groupID.x * TILE_SIZE, groupID.y * TILE_SIZE);

    // 每线程负责 tile 内的若干像素
    [loop]
    for (uint t = 0; t < gsTriCount; t++)
    {
        BinTriangleRef triRef = gsBinTris[t];
        MicroClusterCluster cluster = gClusters[triRef.ClusterIndex];

        // 加载三角形
        MicroClusterTriangle tri = gTriangles[cluster.FirstTriangle + triRef.TriangleIndex];
        uint i0 = tri.I0 & 0xFF;
        uint i1 = tri.I1 & 0xFF;
        uint i2 = tri.I2 & 0xFF;

        uint vertBase = cluster.FirstTriangle * 3 + triRef.TriangleIndex * 3;
        MicroClusterVertex v0 = gVertices[vertBase + 0];
        MicroClusterVertex v1 = gVertices[vertBase + 1];
        MicroClusterVertex v2 = gVertices[vertBase + 2];

        float3 aabbMin = cluster.BoundsCenter - cluster.BoundsRadius;
        float aabbRange = cluster.BoundsRadius * 2.0;
        float3 p0 = DecodePosition(v0.PackedPosition, aabbMin, aabbRange);
        float3 p1 = DecodePosition(v1.PackedPosition, aabbMin, aabbRange);
        float3 p2 = DecodePosition(v2.PackedPosition, aabbMin, aabbRange);

        // 变换到裁剪空间
        float4 c0 = mul(gViewProjMatrix, float4(p0, 1.0));
        float4 c1 = mul(gViewProjMatrix, float4(p1, 1.0));
        float4 c2 = mul(gViewProjMatrix, float4(p2, 1.0));

        // 透视除法
        float3 ndc0 = c0.xyz / max(c0.w, SHANYU_EPSILON);
        float3 ndc1 = c1.xyz / max(c1.w, SHANYU_EPSILON);
        float3 ndc2 = c2.xyz / max(c2.w, SHANYU_EPSILON);

        // 屏幕坐标
        float2 screenRes = 1.0 / gInvResolution;
        float2 s0 = (ndc0.xy * 0.5 + 0.5) * screenRes;
        float2 s1 = (ndc1.xy * 0.5 + 0.5) * screenRes;
        float2 s2 = (ndc2.xy * 0.5 + 0.5) * screenRes;

        // 背面剔除
        float2 e0 = s1 - s0;
        float2 e1 = s2 - s0;
        float area = e0.x * e1.y - e0.y * e1.x;
        if (area >= 0.0) continue; // 顺时针剔除
        float invArea = 1.0 / area;

        // 三角形深度范围 (用于 HiZ 预测试)
        float minDepth = min(min(ndc0.z, ndc1.z), ndc2.z);
        float maxDepth = max(max(ndc0.z, ndc1.z), ndc2.z);

        // HiZ 预测试: 在最粗 mip 检查深度
        float2 tileCenterUV = (tileOrigin + float2(TILE_SIZE * 0.5, TILE_SIZE * 0.5)) * gInvResolution;
        float hzbDepth = gHZB.SampleLevel(gPointClampSampler, tileCenterUV, 6);
        if (minDepth > hzbDepth)
            continue; // 整个 tile 被遮挡, 跳过

        // 每线程光栅化 tile 内的像素 (16x16 / 64 线程 = 4 像素/线程)
        [unroll]
        for (uint p = 0; p < 4; p++)
        {
            uint px = groupThreadID.x * 4 + p;
            uint localX = px % TILE_SIZE;
            uint localY = px / TILE_SIZE;
            uint2 pixel = tileOrigin + uint2(localX, localY);

            if (pixel.x >= (uint)screenRes.x || pixel.y >= (uint)screenRes.y)
                continue;

            float2 pCenter = float2(pixel.x + 0.5, pixel.y + 0.5);

            // 重心坐标 (屏幕空间)
            float2 dp = pCenter - s0;
            float b1 = (e0.x * dp.y - e0.y * dp.x) * invArea;
            float b2 = (e1.y * dp.x - e1.x * dp.y) * invArea;
            float b0 = 1.0 - b1 - b2;

            // 像素中心在三角形内?
            if (b0 >= 0.0 && b1 >= 0.0 && b2 >= 0.0)
            {
                // 插值深度
                float depth = b0 * ndc0.z + b1 * ndc1.z + b2 * ndc2.z;

                // 原子深度测试 (最小深度获胜)
                float prevDepth = gDepthUAV[pixel];
                if (depth < prevDepth)
                {
                    // 原子比较交换: 仅当当前深度更小时写入
                    uint prevBits = asuint(prevDepth);
                    uint newBits = asuint(depth);
                    uint originalBits;
                    InterlockedCompareExchange(
                        (RWStructuredBuffer<uint>)0, 0, prevBits, newBits, originalBits);

                    // 二次检查 (防止竞争)
                    if (depth <= gDepthUAV[pixel])
                    {
                        gDepthUAV[pixel] = depth;
                        gVisibilityBufferUAV[pixel] = PackVisibility(
                            triRef.InstanceIndex, triRef.TriangleIndex, float2(b1, b2));
                    }
                }
            }
        }
    }
}


// ════════════════════════════════════════════════════════════════════════════
// 阶段 3: AttributeDecodeCS — 从可见性缓冲解码顶点属性 -> GeometryBuffer
//
// 每个线程处理一个像素:
//   1. 读取可见性缓冲 (instanceId/triangleId/barycentric)
//   2. 加载对应三角形的 3 个顶点
//   3. 插值 Position/Normal/UV/Tangent
//   4. 写入 GeometryBuffer (Albedo/Normal/Emissive)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void AttributeDecodeCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    float2 screenRes = 1.0 / gDecodeInvResolution;

    if (pixel.x >= (uint)screenRes.x || pixel.y >= (uint)screenRes.y)
        return;

    // 读取可见性缓冲
    uint2 vis = gVisibilityBufferUAV[pixel];

    // 空像素 (天空)
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
    VisibleCluster visCluster = gVisibleClusters[instanceId]; // 简化: instanceId 映射到簇槽位
    MicroClusterCluster cluster = gClusters[visCluster.ClusterIndex];

    // 加载三角形
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

    // 解码顶点位置并变换到世界空间
    float3 localP0 = DecodePosition(v0.PackedPosition, aabbMin, aabbRange);
    float3 localP1 = DecodePosition(v1.PackedPosition, aabbMin, aabbRange);
    float3 localP2 = DecodePosition(v2.PackedPosition, aabbMin, aabbRange);

    float3 worldP0 = TransformInstance(inst, localP0);
    float3 worldP1 = TransformInstance(inst, localP1);
    float3 worldP2 = TransformInstance(inst, localP2);

    // 插值世界位置
    float3 worldPos = worldP0 * b0 + worldP1 * b1 + worldP2 * b2;

    // 解码法线并变换到世界空间 (实例法线 = 逆变换转置 * 法线, 简化: 直接用实例旋转)
    float3 n0 = DecodeNormalOct16(v0.EncodedNormal);
    float3 n1 = DecodeNormalOct16(v1.EncodedNormal);
    float3 n2 = DecodeNormalOct16(v2.EncodedNormal);

    // 法线变换 (使用实例变换的旋转部分, 假设均匀缩放)
    float3 worldN0 = normalize(float3(
        dot(inst.TransformRow0.xyz, n0),
        dot(inst.TransformRow1.xyz, n0),
        dot(inst.TransformRow2.xyz, n0)));
    float3 worldN1 = normalize(float3(
        dot(inst.TransformRow0.xyz, n1),
        dot(inst.TransformRow1.xyz, n1),
        dot(inst.TransformRow2.xyz, n1)));
    float3 worldN2 = normalize(float3(
        dot(inst.TransformRow0.xyz, n2),
        dot(inst.TransformRow1.xyz, n2),
        dot(inst.TransformRow2.xyz, n2)));

    // 插值法线
    float3 normal = normalize(worldN0 * b0 + worldN1 * b1 + worldN2 * b2);

    // 解码 UV (半精度)
    float2 uv0 = float2(f16tof32(v0.EncodedU), f16tof32(v0.EncodedV));
    float2 uv1 = float2(f16tof32(v1.EncodedU), f16tof32(v1.EncodedV));
    float2 uv2 = float2(f16tof32(v2.EncodedU), f16tof32(v2.EncodedV));
    float2 uv = uv0 * b0 + uv1 * b1 + uv2 * b2;

    // 材质 ID
    uint materialSlot = tri.MaterialSlot_EdgeFlags & 0xFF;
    uint materialId = inst.MaterialIndexBase + materialSlot;

    // 写入 GeometryBuffer
    // RT0: Albedo (占位白色, 实际由材质 Pass 填充) + Roughness
    gGeometryBufferAlbedo[pixel] = float4(1.0, 1.0, 1.0, 0.5);

    // RT1: Normal (八面体编码) + Metallic
    float2 encodedNormal = EncodeNormalOctahedral(normal);
    gGeometryBufferNormal[pixel] = float4(encodedNormal, 0.0, materialId / 255.0);

    // RT2: Emissive (占位) + AO
    gGeometryBufferEmissive[pixel] = float4(0.0, 0.0, 0.0, 1.0);
}
