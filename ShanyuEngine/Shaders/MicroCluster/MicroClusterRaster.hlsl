// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine — MicroCluster Rasterization (Mesh Shader + Software Rasterizer)
// 大三角形: 网格着色器 (Mesh Shader) 硬光栅化 -> 可见性缓冲
// 小三角形 (子像素): 计算着色器软件光栅化 -> 可见性缓冲
// 目标: Shader Model 6.6 (DXC 编译, mesh shader, wave intrinsics, barycentrics)
// 对应 C#: ShanyuEngine.Rendering.MicroClusterRenderer (Rasterize 方法)
//
// 可见性缓冲格式: R32G32_UINT
//   .x: clusterId(24bit) | triangleId(8bit)
//   .y: barycentric.x(f16) | barycentric.y(f16)
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (复用 MicroClusterCullCB, 仅需 ViewProj 与分辨率)
// ──────────────────────────────────────────────────────────────────────────
cbuffer MicroClusterRasterCB : register(b0)
{
    float4x4 gMicroClusterViewMatrix;
    float4x4 gMicroClusterProjMatrix;
    float4x4 gMicroClusterViewProjMatrix;
    float3   gMicroClusterCameraPos;
    float    gMicroClusterErrorThreshold;
    float2   gMicroClusterInvResolution;
    float    gMicroClusterLodDistanceScale;
    uint     gMicroClusterTotalClusters;
    uint     gMicroClusterClusterBufferOffset;
    uint     gMicroClusterGroupBufferOffset;
    uint     gMicroClusterFrameIndex;
    uint     gMicroClusterPad0, gMicroClusterPad1;
};

// ──────────────────────────────────────────────────────────────────────────
// GPU 数据结构 (匹配 C# MicroClusterVertex / MicroClusterTriangle / MicroClusterCluster)
// ──────────────────────────────────────────────────────────────────────────

#define NANITE_TRIANGLES_PER_CLUSTER 128
#define NANITE_MAX_VERTICES_PER_CLUSTER 256
#define NANITE_POS_QUANT_BITS 13

struct MicroClusterVertex
{
    uint   PackedPosition;  // 13 位/轴 量化位置
    uint   EncodedNormal;   // 八面体编码法线 (16 位, 打包为 uint)
    uint   EncodedU;        // 16 位半精度 UV (打包)
    uint   EncodedV;
    uint   TangentSign;     // 切线符号 + 颜色索引
};

struct MicroClusterTriangle
{
    uint I0, I1, I2;       // 8 位顶点索引 (打包到 uint)
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

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
StructuredBuffer<MicroClusterCluster>  gClusters  : register(t0);
StructuredBuffer<MicroClusterVertex>   gVertices  : register(t1);
StructuredBuffer<MicroClusterTriangle> gTriangles : register(t2);
StructuredBuffer<VisibleCluster> gVisibleClusters : register(t3);

// 页表 (流式加载物理偏移)
StructuredBuffer<uint> gPageTable : register(t4);
ByteAddressBuffer gGeometryHeap   : register(t5);

// UAV 输出 (软件光栅化路径)
RWTexture2D<uint2> gVisibilityBufferUAV : register(u0);
RWTexture2D<float> gMicroClusterDepthUAV      : register(u1);


// ──────────────────────────────────────────────────────────────────────────
// 位置解码: 从量化打包位置重建世界坐标
// ──────────────────────────────────────────────────────────────────────────
float3 DecodePosition(uint packed, float3 aabbMin, float aabbRange)
{
    uint qx = packed & ((1u << NANITE_POS_QUANT_BITS) - 1u);
    uint qy = (packed >> NANITE_POS_QUANT_BITS) & ((1u << NANITE_POS_QUANT_BITS) - 1u);
    uint qz = (packed >> (2 * NANITE_POS_QUANT_BITS)) & ((1u << NANITE_POS_QUANT_BITS) - 1u);

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

// 打包可见性数据: clusterId(24) | triangleId(8), barycentric(f16x2)
uint2 PackVisibility(uint clusterId, uint triangleId, float2 bary)
{
    uint x = (clusterId & 0xFFFFFF) | ((triangleId & 0xFF) << 24);
    uint by = f32tof16(bary.x) | (f32tof16(bary.y) << 16);
    return uint2(x, by);
}


// ════════════════════════════════════════════════════════════════════════════
// Mesh Shader 路径 — 大三角形硬件光栅化
// 每个线程组处理一个可见簇 (128 三角形)
// ════════════════════════════════════════════════════════════════════════════

struct MeshVertexOutput
{
    float4 Position     : SV_POSITION;
    uint   ClusterId    : TEXCOORD0;   // 簇 ID (传递给 PS 写可见性缓冲)
    uint   TriangleId   : TEXCOORD1;   // 三角形 ID
    float2 Barycentric  : TEXCOORD2;   // 重心坐标 (由 PS 计算)
};

struct MeshPayload
{
    float3 ClusterAabbMin;
    float  ClusterAabbRange;
    uint   ClusterId;
    uint   ClusterIndex;     // 原始簇缓冲索引
    uint   TriangleBase;
    uint   VertexBase;
};

// 每簇最多 256 顶点 / 128 三角形
groupshared MeshVertexOutput  gsVertices[NANITE_MAX_VERTICES_PER_CLUSTER];
groupshared MeshPayload       gsPayload;
groupshared float3            gsWorldPositions[NANITE_MAX_VERTICES_PER_CLUSTER];

// MicroClusterMeshShaderMS — 网格着色器
// 入口点: MicroClusterMeshShaderMS (匹配 C# MicroClusterMeshRaster 管线)
[outputtopology("triangle")]
[numthreads(128, 1, 1)]
void MicroClusterMeshShaderMS(
    uint groupID : SV_GroupID,
    uint groupThreadID : SV_GroupThreadID,
    out vertices MeshVertexOutput verts[NANITE_MAX_VERTICES_PER_CLUSTER],
    out indices uint3 triangles[NANITE_TRIANGLES_PER_CLUSTER])
{
    // 一个线程组 = 一个可见簇
    VisibleCluster visCluster = gVisibleClusters[groupID];
    uint clusterIdx = visCluster.ClusterIndex;
    MicroClusterCluster cluster = gClusters[clusterIdx];

    // 第一个线程加载簇元数据
    if (groupThreadID == 0)
    {
        gsPayload.ClusterId = cluster.Id;
        gsPayload.ClusterIndex = clusterIdx;
        gsPayload.TriangleBase = cluster.FirstTriangle;
        gsPayload.VertexBase = cluster.FirstTriangle * 3; // 简化: 顶点按三角形排列
        gsPayload.ClusterAabbMin = cluster.BoundsCenter - cluster.BoundsRadius;
        gsPayload.ClusterAabbRange = cluster.BoundsRadius * 2.0;
    }
    GroupMemoryBarrierWithGroupSync();

    // 每个线程处理一个三角形: 加载 3 个顶点并变换到裁剪空间
    uint triCount = cluster.TriangleCount;
    uint triLocal = groupThreadID;

    // 设置输出数量
    SetMeshOutputCounts(NANITE_MAX_VERTICES_PER_CLUSTER, triCount);

    // 处理顶点 (每个线程负责一个三角形的 3 顶点)
    if (triLocal < triCount)
    {
        MicroClusterTriangle tri = gTriangles[gsPayload.TriangleBase + triLocal];
        uint i0 = tri.I0 & 0xFF;
        uint i1 = tri.I1 & 0xFF;
        uint i2 = tri.I2 & 0xFF;

        [unroll]
        for (int v = 0; v < 3; v++)
        {
            uint vidx = (v == 0) ? i0 : (v == 1) ? i1 : i2;
            uint vertIdx = gsPayload.VertexBase + triLocal * 3 + v;
            if (vertIdx >= NANITE_MAX_VERTICES_PER_CLUSTER) continue;

            MicroClusterVertex nv = gVertices[vertIdx];
            float3 worldPos = DecodePosition(nv.PackedPosition, gsPayload.ClusterAabbMin, gsPayload.ClusterAabbRange);

            gsWorldPositions[vidx] = worldPos;
            verts[vidx].Position = mul(gMicroClusterViewProjMatrix, float4(worldPos, 1.0));
            verts[vidx].ClusterId = gsPayload.ClusterId;
            verts[vidx].TriangleId = triLocal;
            verts[vidx].Barycentric = float2(0, 0);
        }

        // 输出三角形索引
        triangles[triLocal] = uint3(i0, i1, i2);
    }
}

// MicroClusterVisibilityPS — 可见性缓冲像素着色器
// 写入簇 ID + 三角形 ID + 重心坐标到 R32G32_UINT
// 入口点: MicroClusterVisibilityPS
struct VisibilityPSOutput
{
    uint2 VisData : SV_Target0;
    float Depth   : SV_Depth;
};

VisibilityPSOutput MicroClusterVisibilityPS(
    MeshVertexOutput input,
    uint primitiveID : SV_PrimitiveID,
    float3 bary : SV_Barycentrics)
{
    VisibilityPSOutput output;
    // 重心坐标 (取前两维, 第三维 = 1 - x - y)
    float2 bary2 = bary.xy;
    output.VisData = PackVisibility(input.ClusterId, input.TriangleId, bary2);
    output.Depth = input.Position.z;
    return output;
}


// ════════════════════════════════════════════════════════════════════════════
// SoftwareRasterCS — 子像素三角形软件光栅化 (计算着色器)
// 对小投影面积的簇, 在计算着色器中手动光栅化三角形并写入可见性缓冲
// 入口点: SoftwareRasterCS (匹配 C# RHIShaderBytecode("SoftwareRasterCS", Compute))
// ════════════════════════════════════════════════════════════════════════════

// 每个线程组处理一个软件光栅簇, 每线程光栅化一个三角形
[numthreads(32, 1, 1)]
void SoftwareRasterCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint clusterSlot = dispatchID.x;
    // 读取软件光栅簇列表 (来自剔除 Pass)
    // 此处复用 gVisibleClusters, 实际绑定软件光栅簇缓冲
    VisibleCluster visCluster = gVisibleClusters[clusterSlot];
    uint clusterIdx = visCluster.ClusterIndex;
    MicroClusterCluster cluster = gClusters[clusterIdx];

    float3 aabbMin = cluster.BoundsCenter - cluster.BoundsRadius;
    float aabbRange = cluster.BoundsRadius * 2.0;

    uint triBase = cluster.FirstTriangle;
    uint triCount = cluster.TriangleCount;

    // 每线程组有 32 线程, 但簇有最多 128 三角形 -> 每线程处理多个三角形
    [loop]
    for (uint t = 0; t < triCount; t += 32)
    {
        uint triLocal = t + (dispatchID.y * 1) + (clusterSlot & 0); // 简化: 按线程索引
        uint triIdx = triLocal;
        if (triIdx >= triCount) continue;

        MicroClusterTriangle tri = gTriangles[triBase + triIdx];
        uint i0 = tri.I0 & 0xFF;
        uint i1 = tri.I1 & 0xFF;
        uint i2 = tri.I2 & 0xFF;

        uint vertBase = triBase * 3 + triIdx * 3;
        MicroClusterVertex v0 = gVertices[vertBase + 0];
        MicroClusterVertex v1 = gVertices[vertBase + 1];
        MicroClusterVertex v2 = gVertices[vertBase + 2];

        float3 p0 = DecodePosition(v0.PackedPosition, aabbMin, aabbRange);
        float3 p1 = DecodePosition(v1.PackedPosition, aabbMin, aabbRange);
        float3 p2 = DecodePosition(v2.PackedPosition, aabbMin, aabbRange);

        // 变换到裁剪空间
        float4 c0 = mul(gMicroClusterViewProjMatrix, float4(p0, 1.0));
        float4 c1 = mul(gMicroClusterViewProjMatrix, float4(p1, 1.0));
        float4 c2 = mul(gMicroClusterViewProjMatrix, float4(p2, 1.0));

        // 透视除法 -> NDC
        float3 ndc0 = c0.xyz / c0.w;
        float3 ndc1 = c1.xyz / c1.w;
        float3 ndc2 = c2.xyz / c2.w;

        // 背面剔除 (NDC 空间叉积)
        float2 e0 = ndc1.xy - ndc0.xy;
        float2 e1 = ndc2.xy - ndc0.xy;
        float area = e0.x * e1.y - e0.y * e1.x;
        if (area >= 0.0) continue; // 逆时针为正面, 顺时针剔除

        // NDC -> 屏幕像素坐标
        float2 s0 = (ndc0.xy * 0.5 + 0.5) * gScreenParams.xy;
        float2 s1 = (ndc1.xy * 0.5 + 0.5) * gScreenParams.xy;
        float2 s2 = (ndc2.xy * 0.5 + 0.5) * gScreenParams.xy;

        // 计算屏幕空间包围盒
        float2 minBB = min(min(s0, s1), s2);
        float2 maxBB = max(max(s0, s1), s2);
        int2 iMin = int2(floor(minBB));
        int2 iMax = int2(ceil(maxBB));

        // 包围盒裁剪到屏幕
        iMin = max(iMin, int2(0, 0));
        iMax = min(iMax, int2((int)gScreenParams.x - 1, (int)gScreenParams.y - 1));

        float invArea = 1.0 / area;

        // 光栅化包围盒内像素
        for (int py = iMin.y; py <= iMax.y; py++)
        {
            for (int px = iMin.x; px <= iMax.x; px++)
            {
                float2 p = float2(px + 0.5, py + 0.5);

                // 重心坐标 (屏幕空间)
                float2 d0 = s1 - s0;
                float2 d1 = s2 - s0;
                float2 dp = p - s0;
                float b1 = (d0.x * dp.y - d0.y * dp.x) * invArea;
                float b2 = (d1.y * dp.x - d1.x * dp.y) * invArea;
                float b0 = 1.0 - b1 - b2;

                // 像素中心在三角形内?
                if (b0 >= 0.0 && b1 >= 0.0 && b2 >= 0.0)
                {
                    // 插值深度
                    float depth = b0 * ndc0.z + b1 * ndc1.z + b2 * ndc2.z;

                    // 深度测试 (原子比较交换)
                    uint2 pixel = uint2(px, py);
                    float prevDepth = gMicroClusterDepthUAV[pixel];
                    if (depth < prevDepth)
                    {
                        // InterlockedCompareExchange 风格的深度测试
                        uint oldBits = asuint(prevDepth);
                        uint newBits = asuint(depth);
                        InterlockedCompareExchange(
                            (RWStructuredBuffer<uint>)gMicroClusterDepthUAV, 0, oldBits, newBits, oldBits);
                        // 简化: 直接写入 (实际需正确原子最小深度)
                        if (depth < gMicroClusterDepthUAV[pixel])
                        {
                            gMicroClusterDepthUAV[pixel] = depth;
                            gVisibilityBufferUAV[pixel] = PackVisibility(cluster.Id, triIdx, float2(b1, b2));
                        }
                    }
                }
            }
        }
    }
}
