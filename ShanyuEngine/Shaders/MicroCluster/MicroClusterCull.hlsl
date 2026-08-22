// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine — MicroCluster GPU-Driven Cluster Culling
// 簇剔除计算着色器: 视锥剔除 + HZB 遮挡剔除 + 法线锥背面剔除 + 误差 LOD 选择
// 输出可见簇列表与间接绘制参数 (mesh-shader dispatch args)
// 目标: Shader Model 6.6 (DXC 编译, wave intrinsics, atomic ops)
// 对应 C#: ShanyuEngine.Rendering.MicroClusterRenderer (Cull 方法)
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 剔除常量 (匹配 C# MicroClusterCullCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer MicroClusterCullCB : register(b0)
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
// GPU 数据结构 (匹配 C# MicroClusterCluster / MicroClusterClusterGroup)
// ──────────────────────────────────────────────────────────────────────────

#define NANITE_TRIANGLES_PER_CLUSTER 128
#define NANITE_MAX_VERTICES_PER_CLUSTER 256

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

struct MicroClusterClusterGroup
{
    uint   Id;
    uint   LodLevel;
    uint   FirstChildCluster;
    uint   ChildClusterCount;
    uint   ParentClusterId;
    float3 BoundsCenter;
    float  BoundsRadius;
    float  MaxError;
    float  MinError;
};

// 间接分发参数 (mesh shader dispatch)
struct MicroClusterMeshShaderArgs
{
    uint GroupCountX;
    uint GroupCountY;
    uint GroupCountZ;
    uint Padding;
};

// 可见簇记录 (写入可见簇缓冲)
struct VisibleCluster
{
    uint ClusterIndex;   // 原始簇缓冲中的索引
    uint MeshId;
    uint IsSoftwareRaster; // 0=mesh shader, 1=software raster
    uint Padding;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────

// 簇 / 组元数据 (SRV)
StructuredBuffer<MicroClusterCluster>      gClusters : register(t0);
StructuredBuffer<MicroClusterClusterGroup> gGroups   : register(t1);

// HZB (层级 Z 缓冲, 遮挡剔除)
Texture2D<float> gHZBTexture : register(t2);

// 页表 (检查页面是否驻留)
StructuredBuffer<uint> gPageTable : register(t3);

// UAV 输出
RWStructuredBuffer<VisibleCluster>      gVisibleClusters      : register(u0);
RWStructuredBuffer<MicroClusterMeshShaderArgs> gIndirectArgs         : register(u1);
RWStructuredBuffer<VisibleCluster>      gSoftwareRasterClusters : register(u2);
RWStructuredBuffer<uint>                gVisibleCount         : register(u3);


// ──────────────────────────────────────────────────────────────────────────
// 视锥剔除: 球体 vs 6 个裁剪平面
// ──────────────────────────────────────────────────────────────────────────

// 从 ViewProjMatrix 提取 6 个裁剪平面 (Gribb-Hartmann 方法)
void ExtractFrustumPlanes(float4x4 m, out float4 planes[6])
{
    planes[0] = float4(m._13, m._23, m._33, m._43); // left   = row4 + row1
    planes[1] = float4(m._14 - m._13, m._24 - m._23, m._34 - m._33, m._44 - m._43); // right
    planes[2] = float4(m._12, m._22, m._32, m._42); // bottom
    planes[3] = float4(m._14 - m._12, m._24 - m._22, m._34 - m._32, m._44 - m._42); // top
    planes[4] = float4(m._14, m._24, m._34, m._44); // near
    planes[5] = float4(m._14 - m._14, m._24 - m._24, m._34 - m._34, m._44 - m._44); // far (placeholder)
    // 标准化
    [unroll]
    for (int i = 0; i < 6; i++)
    {
        float len = length(planes[i].xyz);
        planes[i] /= max(len, SHANYU_EPSILON);
    }
}

bool FrustumCull(float3 center, float radius, float4x4 viewProj)
{
    float4 planes[6];
    ExtractFrustumPlanes(viewProj, planes);

    [unroll]
    for (int i = 0; i < 6; i++)
    {
        float dist = dot(planes[i].xyz, center) + planes[i].w;
        if (dist < -radius)
            return true; // 在视锥外
    }
    return false; // 可见
}

// ──────────────────────────────────────────────────────────────────────────
// 法线锥背面剔除: 检查簇所有法线是否背向相机
// ──────────────────────────────────────────────────────────────────────────
bool BackfaceCull(MicroClusterCluster cluster)
{
    // 视线方向 (从相机到簇中心)
    float3 viewDir = normalize(cluster.BoundsCenter - gMicroClusterCameraPos);

    // 若法线锥完全背向视线, 则剔除
    // dot(viewDir, normalConeAxis) > cos(halfAngle) 意味着所有法线都背向
    float cosAngle = dot(viewDir, cluster.NormalConeAxis);
    return cosAngle > cluster.NormalConeCos;
}

// ──────────────────────────────────────────────────────────────────────────
// HZB 遮挡剔除: 将簇包围球投影到屏幕, 采样 HZB 判断是否被遮挡
// ──────────────────────────────────────────────────────────────────────────
bool HZBOcclusionCull(float3 center, float radius)
{
    // 投影包围球 8 个角点到 NDC
    float4 centerClip = mul(gMicroClusterViewProjMatrix, float4(center, 1.0));
    if (centerClip.w <= 0.0) return false; // 在相机后方, 不剔除 (交给视锥)

    float2 ndc = centerClip.xy / centerClip.w;
    float2 uv = ndc * 0.5 + 0.5;

    // 屏幕空间包围球半径
    float screenRadius = radius / max(centerClip.w, SHANYU_EPSILON)
                       * 0.5 * min(gScreenParams.x, gScreenParams.y);

    // 选择 HZB mip 级别 (使一个 texel 覆盖包围球)
    float mipLevel = log2(max(screenRadius, 1.0));

    // 采样 HZB (最远深度)
    float hzbDepth = gHZBTexture.SampleLevel(gPointClampSampler, uv, mipLevel).r;

    // 簇的最近深度 (NDC)
    float clusterDepth = centerClip.z / centerClip.w - radius / max(centerClip.w, SHANYU_EPSILON);

    // 若 HZB 记录的最近表面比簇还远, 则簇被遮挡
    return clusterDepth > hzbDepth;
}

// ──────────────────────────────────────────────────────────────────────────
// LOD 选择: 基于屏幕空间误差
// 若簇几何误差投影后低于阈值, 则使用此簇 (否则应选更粗 LOD 的父簇)
// ──────────────────────────────────────────────────────────────────────────
bool PassErrorTest(MicroClusterCluster cluster)
{
    // 距离相机的距离
    float dist = length(cluster.BoundsCenter - gMicroClusterCameraPos);

    // 屏幕空间误差 = geometricError * (屏幕高度 / 距离)
    // 简化: error_proj = error * screenHeight / (dist * tan(fov/2))
    float screenHeight = gScreenParams.y;
    float screenError = cluster.GeometricError * screenHeight / max(dist, SHANYU_EPSILON);

    // 误差低于阈值则此簇足够细 (或是最粗 LOD)
    return screenError <= gMicroClusterErrorThreshold || cluster.ParentGroup == 0xFFFFFFFF;
}

// ──────────────────────────────────────────────────────────────────────────
// 检查页面是否驻留 (虚拟几何体流式加载)
// ──────────────────────────────────────────────────────────────────────────
bool IsPageResident(MicroClusterCluster cluster)
{
    uint physicalOffset = gPageTable[cluster.PageId];
    return physicalOffset != 0xFFFFFFFF;
}


// ════════════════════════════════════════════════════════════════════════════
// CullCS — MicroCluster 簇剔除计算着色器
// 每线程处理一个簇: 视锥 + HZB 遮挡 + 法线锥背面 + 误差 LOD + 页面驻留
// 通过测试的簇写入可见簇缓冲并原子递增计数器
// 入口点: CullCS (匹配 C# RHIShaderBytecode("CullCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(64, 1, 1)]
void CullCS(uint3 dispatchID : SV_DispatchThreadID, uint groupThreadID : SV_GroupIndex)
{
    uint clusterIdx = dispatchID.x;
    if (clusterIdx >= gMicroClusterTotalClusters)
        return;

    MicroClusterCluster cluster = gClusters[clusterIdx];

    // Pass 1: 视锥剔除
    if (FrustumCull(cluster.BoundsCenter, cluster.BoundsRadius, gMicroClusterViewProjMatrix))
        return;

    // Pass 2: 法线锥背面剔除
    if (BackfaceCull(cluster))
        return;

    // Pass 3: HZB 遮挡剔除
    if (HZBOcclusionCull(cluster.BoundsCenter, cluster.BoundsRadius))
        return;

    // Pass 4: 误差 LOD 测试
    if (!PassErrorTest(cluster))
        return;

    // Pass 5: 页面驻留检查 (虚拟几何体)
    if (!IsPageResident(cluster))
        return;

    // 簇通过所有测试, 判断使用硬件 (mesh shader) 还是软件光栅化
    // 根据屏幕投影面积: 小三角形 (子像素) 使用软件光栅化
    float dist = length(cluster.BoundsCenter - gMicroClusterCameraPos);
    float projectedSize = cluster.BoundsRadius / max(dist, SHANYU_EPSILON) * gScreenParams.y;
    bool useSoftwareRaster = projectedSize < 16.0; // SoftwareRasterPixelThreshold

    // 原子分配可见簇槽位
    uint slot;
    if (useSoftwareRaster)
    {
        InterlockedAdd(gVisibleCount[1], 1, slot); // [1] = 软件光栅计数
        gSoftwareRasterClusters[slot] = (VisibleCluster)0;
        gSoftwareRasterClusters[slot].ClusterIndex = clusterIdx;
        gSoftwareRasterClusters[slot].MeshId = cluster.MeshId;
        gSoftwareRasterClusters[slot].IsSoftwareRaster = 1;
    }
    else
    {
        InterlockedAdd(gVisibleCount[0], 1, slot); // [0] = 硬件光栅计数
        gVisibleClusters[slot] = (VisibleCluster)0;
        gVisibleClusters[slot].ClusterIndex = clusterIdx;
        gVisibleClusters[slot].MeshId = cluster.MeshId;
        gVisibleClusters[slot].IsSoftwareRaster = 0;

        // 累积 mesh-shader 分发参数 (每簇一个线程组)
        // 使用 wave 内归约后跨 wave 原子累加
        uint waveCount = WaveActiveCountBits(true);
        if (WaveIsFirstLane())
        {
            InterlockedAdd(gIndirectArgs[0].GroupCountX, waveCount);
        }
    }
}
