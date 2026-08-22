// ============================================================================
// ShanyuEngine - MicroCluster GPU Compute Shaders
// 山雨引擎 - MicroCluster GPU 计算着色器系统
// ============================================================================
// 描述: 包含 MicroCluster 虚拟化几何体管线所需的全部 HLSL 计算着色器、
//       顶点着色器和像素着色器，以 C# 字符串常量形式内嵌。
//       涵盖 Cluster 视锥剔除、LOD 选择、边折叠简化、
//       替身 Billboard 渲染、可见性缓冲输出等核心功能。
// ============================================================================

using System;
using System.Runtime.InteropServices;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.MicroCluster
{
    /// <summary>
    /// MicroCluster 聚类 GPU 端数据结构，用于与着色器之间的数据传递。
    /// 对应 HLSL 侧的 ClusterData 结构体。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct MicroClusterClusterData
    {
        /// <summary>
        /// 聚类的包围盒中心点（本地空间）。
        /// </summary>
        public Vector3 BoundsCenter;

        /// <summary>
        /// 聚类的包围盒半尺寸（各轴半径）。
        /// </summary>
        public Vector3 BoundsExtent;

        /// <summary>
        /// 该聚类可用的 LOD 层级数量。
        /// 最小值为 1（仅原始分辨率）。
        /// </summary>
        public int LODCount;

        /// <summary>
        /// 父聚类在聚类缓冲中的索引。
        /// 若为 -1 则表示根聚类。
        /// </summary>
        public int ParentClusterIndex;

        /// <summary>
        /// 该聚类在页表缓冲中的偏移量（以三角形为单位）。
        /// 用于从全局三角形缓冲中定位该聚类的几何数据。
        /// </summary>
        public int PageOffset;

        /// <summary>
        /// 该聚类在当前 LOD 层级的三角形数量。
        /// </summary>
        public int TriangleCount;

        /// <summary>
        /// 聚类的屏幕空间误差预算（像素单位）。
        /// 用于 LOD 切换的阈值判断。
        /// </summary>
        public float ScreenSpaceError;

        /// <summary>
        /// 预留对齐填充字段，确保结构体大小为 64 字节。
        /// </summary>
        private float _padding1;
        private float _padding2;

        /// <summary>
        /// 创建一个新的 MicroClusterClusterData 实例。
        /// </summary>
        public MicroClusterClusterData(
            Vector3 boundsCenter,
            Vector3 boundsExtent,
            int lodCount,
            int parentClusterIndex,
            int pageOffset,
            int triangleCount,
            float screenSpaceError)
        {
            BoundsCenter = boundsCenter;
            BoundsExtent = boundsExtent;
            LODCount = lodCount;
            ParentClusterIndex = parentClusterIndex;
            PageOffset = pageOffset;
            TriangleCount = triangleCount;
            ScreenSpaceError = screenSpaceError;
            _padding1 = 0.0f;
            _padding2 = 0.0f;
        }
    }

    /// <summary>
    /// MicroCluster 可见性结果结构体，用于从 GPU 回读剔除与可见性信息。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MicroClusterVisibilityResult
    {
        /// <summary>
        /// 当前帧可见的聚类总数。
        /// </summary>
        public int VisibleClusterCount;

        /// <summary>
        /// 可见性缓冲区的最大容量（最大可见聚类数）。
        /// </summary>
        public int MaxVisibleClusters;

        /// <summary>
        /// 本帧参与剔除的聚类总数（含被剔除的）。
        /// </summary>
        public int TotalClusterCount;

        /// <summary>
        /// LOD 切换发生次数统计。
        /// </summary>
        public int LODSwitchCount;

        /// <summary>
        /// 创建 MicroClusterVisibilityResult 实例。
        /// </summary>
        public MicroClusterVisibilityResult(int visibleClusterCount, int maxVisibleClusters)
        {
            VisibleClusterCount = visibleClusterCount;
            MaxVisibleClusters = maxVisibleClusters;
            TotalClusterCount = 0;
            LODSwitchCount = 0;
        }
    }

    /// <summary>
    /// MicroCluster 计算着色器集合。
    /// 包含所有以 HLSL 字符串常量形式定义的着色器代码，
    /// 运行时通过着色器编译器编译并上传至 GPU。
    /// </summary>
    public static class MicroClusterComputeShaders
    {
        // ====================================================================
        // 公共常量与宏定义（所有着色器共享）
        // ====================================================================

        /// <summary>
        /// 共享的 HLSL 头部代码，包含公共宏、结构体和工具函数。
        /// 被所有着色器字符串通过 #include 逻辑合并。
        /// </summary>
        public const string SharedHLSLHeader = @"
// ============================================================================
// MicroCluster Shared HLSL Definitions
// ============================================================================

#ifndef NANITE_SHARED_HLSL
#define NANITE_SHARED_HLSL

#define NANITE_MAX_LOD_LEVELS        8
#define NANITE_CLUSTER_SIZE           128       // 每个聚类的最大三角形数
#define NANITE_THREADGROUP_SIZE       64        // 计算着色器线程组大小
#define NANITE_MAX_VISIBLE_CLUSTERS   131072    // 最大可见聚类数
#define NANITE_SCREEN_ERROR_THRESHOLD 2.0       // 默认屏幕空间误差阈值（像素）
#define NANITE_IMPOSTOR_DISTANCE      0.015     // 替身渲染触发距离（NDC单位）
#define NANITE_INVALID_CLUSTER_ID     0xFFFFFFFF
#define NANITE_PAGE_TABLE_ENTRY_SIZE  48        // 每个三角形页表条目字节数

// ============================================================================
// 聚类数据结构（对应 C# 侧 MicroClusterClusterData）
// ============================================================================
struct ClusterData
{
    float3 BoundsCenter;       // Offset: 0,  Size: 12
    float3 BoundsExtent;       // Offset: 12, Size: 12
    int    LODCount;           // Offset: 24, Size: 4
    int    ParentClusterIndex; // Offset: 28, Size: 4
    int    PageOffset;         // Offset: 32, Size: 4
    int    TriangleCount;      // Offset: 36, Size: 4
    float  ScreenSpaceError;   // Offset: 40, Size: 4
    float2 Padding;            // Offset: 44, Size: 8
    // Total: 48 bytes -> padded to 64 with alignment
};

// ============================================================================
// 顶点数据结构（量化后）
// ============================================================================
struct QuantizedVertex
{
    uint PositionPacked;  // R16G16B16A16_SNORM -> XYZ (16-bit each) + pad
    uint NormalPacked;    // R16G16_UNORM -> octahedral encoded normal
    uint TexCoordPacked;  // R16G16_UNORM -> UV
};

// ============================================================================
// 三角形数据结构
// ============================================================================
struct ClusterTriangle
{
    uint3 VertexIndices;  // 三个顶点在聚类局部顶点缓冲中的索引
    uint  MaterialIndex;  // 材质索引
};

// ============================================================================
// 可见性条目结构
// ============================================================================
struct VisibleClusterEntry
{
    uint  ClusterIndex;     // 聚类在全局缓冲中的索引
    uint  SelectedLOD;      // 当前选中的LOD层级
    float ScreenSpaceSize;  // 聚类在屏幕空间的估计尺寸
    float SortKey;          // 排序键（用于后续渲染排序）
};

// ============================================================================
// 视锥体结构
// ============================================================================
struct FrustumPlanes
{
    float4 Planes[6]; // left, right, top, bottom, near, far (ax+by+cz+d=0)
};

// ============================================================================
// 边折叠候选结构
// ============================================================================
struct EdgeCollapseCandidate
{
    uint  VertexA;
    uint  VertexB;
    float CollapseCost;
    uint  TriangleCount;
};

// ============================================================================
// 工具函数：平面-球体相交测试
// 返回: > 0 完全在外侧, <= 0 相交或在内侧
// ============================================================================
float PlaneSphereTest(float4 plane, float3 center, float radius)
{
    return dot(plane.xyz, center) + plane.w - radius;
}

// ============================================================================
// 工具函数：AABB-视锥体相交测试
// 返回: true 如果包围盒与视锥体相交
// ============================================================================
bool AABBFrustumTest(ClusterData cluster, FrustumPlanes frustum)
{
    float3 absExtent = abs(cluster.BoundsExtent);
    bool inside = true;

    [unroll]
    for (int i = 0; i < 6; i++)
    {
        float3 absNormal = abs(frustum.Planes[i].xyz);
        float radius = dot(absExtent, absNormal);
        float dist = PlaneSphereTest(frustum.Planes[i], cluster.BoundsCenter, radius);
        if (dist > 0.0f)
        {
            inside = false;
            break;
        }
    }
    return inside;
}

// ============================================================================
// 工具函数：计算AABB的屏幕空间尺寸
// ============================================================================
float ComputeScreenSpaceSize(
    ClusterData cluster,
    float4x4 ViewProjMatrix,
    uint2    ViewportSize)
{
    float3 corners[8];
    float3 ext = cluster.BoundsExtent;
    float3 cen = cluster.BoundsCenter;

    corners[0] = cen + float3(-ext.x, -ext.y, -ext.z);
    corners[1] = cen + float3( ext.x, -ext.y, -ext.z);
    corners[2] = cen + float3( ext.x,  ext.y, -ext.z);
    corners[3] = cen + float3(-ext.x,  ext.y, -ext.z);
    corners[4] = cen + float3(-ext.x, -ext.y,  ext.z);
    corners[5] = cen + float3( ext.x, -ext.y,  ext.z);
    corners[6] = cen + float3( ext.x,  ext.y,  ext.z);
    corners[7] = cen + float3(-ext.x,  ext.y,  ext.z);

    float minX = 1.0f, maxX = -1.0f;
    float minY = 1.0f, maxY = -1.0f;

    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float4 clipPos = mul(ViewProjMatrix, float4(corners[i], 1.0f));
        if (clipPos.w <= 0.0f) continue;
        float2 ndc = clipPos.xy / clipPos.w;
        minX = min(minX, ndc.x);
        maxX = max(maxX, ndc.x);
        minY = min(minY, ndc.y);
        maxY = max(maxY, ndc.y);
    }

    float2 screenSize = float2(
        (maxX - minX) * 0.5f * (float)ViewportSize.x,
        (maxY - minY) * 0.5f * (float)ViewportSize.y
    );

    return max(screenSize.x, screenSize.y);
}

// ============================================================================
// 工具函数：选择最佳LOD层级
// 返回值: 应该使用的LOD层级索引（0=最高精度）
// ============================================================================
int SelectLODLevel(
    ClusterData  cluster,
    float        screenSize,
    float        errorThreshold,
    float3       viewOrigin,
    float3       clusterCenter)
{
    float distance = length(clusterCenter - viewOrigin);
    float adjustedError = cluster.ScreenSpaceError;

    // 根据屏幕尺寸和距离计算所需LOD
    // screenSize越小（物体越远），使用越低的LOD
    int bestLOD = 0;
    float prevError = 0.0f;

    for (int lod = 1; lod < cluster.LODCount; lod++)
    {
        // 每级LOD误差累积增长
        float lodError = adjustedError * (float)lod;
        float pixelError = lodError * screenSize;

        if (pixelError < errorThreshold)
        {
            bestLOD = lod;
        }
        else
        {
            break;
        }
    }

    return bestLOD;
}

// ============================================================================
// 工具函数：八面体法线解码
// ============================================================================
float3 DecodeOctahedralNormal(uint packedNormal)
{
    float2 oct = float2(
        ((packedNormal >> 0) & 0xFFFF) / 65535.0f * 2.0f - 1.0f,
        ((packedNormal >> 16) & 0xFFFF) / 65535.0f * 2.0f - 1.0f
    );

    float3 n = float3(oct.x, oct.y, 1.0f - abs(oct.x) - abs(oct.y));
    float t = max(-n.z, 0.0f);
    n.xy += float2(n.x >= 0.0f ? -t : t, n.y >= 0.0f ? -t : t);
    return normalize(n);
}

// ============================================================================
// 工具函数：量化位置解码
// ============================================================================
float3 DecodeQuantizedPosition(uint packedPos, float3 boundsMin, float3 boundsMax)
{
    float3 quantized = float3(
        ((packedPos >>  0) & 0xFFFF) / 65535.0f,
        ((packedPos >> 16) & 0xFFFF) / 65535.0f,
        (packedPos & 0x80000000) != 0 ? 1.0f : 0.0f // 简化: 用标志位
    );
    return boundsMin + quantized * (boundsMax - boundsMin);
}

// ============================================================================
// 工具函数：量化UV解码
// ============================================================================
float2 DecodeQuantizedUV(uint packedUV)
{
    return float2(
        ((packedUV >>  0) & 0xFFFF) / 65535.0f,
        ((packedUV >> 16) & 0xFFFF) / 65535.0f
    );
}

#endif // NANITE_SHARED_HLSL
";

        // ====================================================================
        // 着色器1: ClusterCullCS — 聚类视锥剔除 + LOD选择 计算着色器
        // ====================================================================

        /// <summary>
        /// ClusterCullCS 计算着色器。
        /// 每个线程处理一个聚类，执行视锥剔除和距离LOD选择，
        /// 将可见聚类写入 UAV 缓冲。
        ///
        /// 资源绑定:
        ///   SRV[0]: StructuredBuffer&lt;ClusterData&gt;   ClusterBuffer
        ///   CBV[0]: cbClusterCull (视锥矩阵、相机参数等)
        ///   UAV[0]: RWStructuredBuffer&lt;VisibleClusterEntry&gt; VisibleClusterBuffer
        ///   UAV[1]: RWByteAddressBuffer                  VisibleClusterCounter (原子计数器)
        /// </summary>
        public const string ClusterCullCS = @"
// ============================================================================
// ClusterCullCS - MicroCluster Cluster Culling Compute Shader
// 每个线程处理一个Cluster: 视锥剔除 + 距离LOD选择 + 可见性输出
// ============================================================================

#include ""MicroClusterShared.hlsli""

// ============================================================================
// 常量缓冲区: 剔除参数
// ============================================================================
cbuffer cbClusterCull : register(b0)
{
    float4x4 ViewProjMatrix;       // 视图投影矩阵
    float4x4 PrevViewProjMatrix;   // 上一帧视图投影矩阵（用于时序 coherence）
    float3   ViewOrigin;           // 相机世界坐标
    float    ErrorThreshold;       // 屏幕空间误差阈值（像素）
    float2   ViewportSize;         // 视口尺寸 (宽, 高)
    float    HZBBias;              // HiZ 贴图偏移（保守度）
    int      ClusterCount;         // 总聚类数
    int      FrameIndex;           // 帧索引（奇偶帧交替）
    float    ImpostorDistance;     // 替身渲染距离阈值
    float    CullSmallClusters;    // 小聚类剔除阈值（像素）
    int      EnableOcclusionCull;  // 是否启用遮挡剔除
    int      Padding0;
    FrustumPlanes Frustum;         // 视锥体6个平面
};

// ============================================================================
// 资源声明
// ============================================================================
StructuredBuffer<ClusterData>            ClusterBuffer       : register(t0);
Texture2D<float>                         HZBTexture          : register(t1);  // HiZ 深度缓冲
SamplerState                             HZBSampler          : register(s0);

RWStructuredBuffer<VisibleClusterEntry>  VisibleClusterBuffer: register(u0);
RWByteAddressBuffer                      VisibleCounter      : register(u1); // 原子计数器

// ============================================================================
// 线程组共享内存
// ============================================================================
groupshared uint gs_VisibleCount;
groupshared uint gs_VisibleIndices[NANITE_THREADGROUP_SIZE];
groupshared uint gs_TotalDispatched;

// ============================================================================
// 入口函数
// ============================================================================
[numthreads(NANITE_THREADGROUP_SIZE, 1, 1)]
void ClusterCullCS(
    uint3 GroupId           : SV_GroupID,
    uint3 GroupThreadId     : SV_GroupThreadId,
    uint3 DispatchThreadId  : SV_DispatchThreadID,
    uint  GroupIndex        : SV_GroupIndex)
{
    // 初始化组共享计数器
    if (GroupIndex == 0)
    {
        gs_VisibleCount = 0;
        gs_TotalDispatched = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    uint globalClusterIndex = DispatchThreadId.x;

    // 边界检查
    if (globalClusterIndex >= (uint)ClusterCount)
        return;

    ClusterData cluster = ClusterBuffer[globalClusterIndex];

    // ----------------------------------------------------------------
    // 第1步: 视锥剔除 (Frustum Culling)
    // ----------------------------------------------------------------
    if (!AABBFrustumTest(cluster, Frustum))
        return; // 聚类在视锥外，剔除

    // ----------------------------------------------------------------
    // 第2步: 背面剔除 (Backface Culling - 粗粒度)
    // 基于聚类中心与相机方向的点积判断
    // ----------------------------------------------------------------
    float3 viewDir = normalize(cluster.BoundsCenter - ViewOrigin);
    float clusterRadius = length(cluster.BoundsExtent);
    float distanceToCluster = length(cluster.BoundsCenter - ViewOrigin);

    // 对于极小的聚类（如粒子级别），直接跳过
    if (distanceToCluster < 0.001f)
        return;

    // ----------------------------------------------------------------
    // 第3步: 遮挡剔除 (Occlusion Culling via Hi-Z)
    // ----------------------------------------------------------------
    if (EnableOcclusionCull != 0)
    {
        // 投影聚类中心到屏幕空间
        float4 clipPos = mul(ViewProjMatrix, float4(cluster.BoundsCenter, 1.0f));
        if (clipPos.w > 0.0f)
        {
            float2 ndcPos = clipPos.xy / clipPos.w;
            float2 uvPos  = ndcPos * 0.5f + 0.5f;
            uvPos.y = 1.0f - uvPos.y; // 翻转Y轴

            // 采样HiZ缓冲，获取该像素的深度值
            float hzbDepth = HZBTexture.SampleLevel(HZBSampler, uvPos, 3).r;
            float clusterDepth = clipPos.w;

            // 如果聚类深度大于HiZ深度（被遮挡），且差距超过偏移量
            if (clusterDepth > hzbDepth + HZBBias)
                return; // 被遮挡，剔除
        }
    }

    // ----------------------------------------------------------------
    // 第4步: 计算屏幕空间尺寸
    // ----------------------------------------------------------------
    float screenSize = ComputeScreenSpaceSize(cluster, ViewProjMatrix, (uint2)ViewportSize);

    // 小聚类剔除：屏幕尺寸过小则跳过
    if (screenSize < CullSmallClusters)
        return;

    // ----------------------------------------------------------------
    // 第5步: LOD层级选择
    // ----------------------------------------------------------------
    int selectedLOD = SelectLODLevel(
        cluster,
        screenSize,
        ErrorThreshold,
        ViewOrigin,
        cluster.BoundsCenter
    );

    // ----------------------------------------------------------------
    // 第6步: 判断是否应使用替身渲染 (Impostor)
    // ----------------------------------------------------------------
    bool useImpostor = false;
    if (screenSize < ImpostorDistance * (float)ViewportSize.x)
    {
        useImpostor = true;
    }

    // ----------------------------------------------------------------
    // 第7步: 写入组共享内存
    // ----------------------------------------------------------------
    uint localIndex;
    InterlockedAdd(gs_VisibleCount, 1, localIndex);
    gs_VisibleIndices[localIndex] = globalClusterIndex;

    GroupMemoryBarrierWithGroupSync();

    // ----------------------------------------------------------------
    // 第8步: 组内线程0负责将可见聚类写入全局UAV
    // ----------------------------------------------------------------
    if (GroupIndex == 0 && gs_VisibleCount > 0)
    {
        // 原子递增全局可见聚类计数器
        uint globalOffset;
        VisibleCounter.InterlockedAdd(0, gs_VisibleCount, globalOffset);

        for (uint i = 0; i < gs_VisibleCount; i++)
        {
            uint ci = gs_VisibleIndices[i];
            ClusterData c = ClusterBuffer[ci];

            VisibleClusterEntry entry;
            entry.ClusterIndex = ci;
            entry.SelectedLOD = (uint)SelectLODLevel(
                c, ComputeScreenSpaceSize(c, ViewProjMatrix, (uint2)ViewportSize),
                ErrorThreshold, ViewOrigin, c.BoundsCenter);
            entry.ScreenSpaceSize = ComputeScreenSpaceSize(c, ViewProjMatrix, (uint2)ViewportSize);
            entry.SortKey = -entry.ScreenSpaceSize; // 从大到小排序（近处优先）

            if (globalOffset + i < NANITE_MAX_VISIBLE_CLUSTERS)
            {
                VisibleClusterBuffer[globalOffset + i] = entry;
            }
        }
    }
}
";

        // ====================================================================
        // 着色器2: ClusterRenderVS — 聚类渲染顶点着色器
        // ====================================================================

        /// <summary>
        /// ClusterRenderVS 顶点着色器。
        /// 从 Cluster Buffer 中读取三角形数据，
        /// 根据 Cluster ID 获取当前 LOD 层级，
        /// 解码量化顶点数据并输出到屏幕空间。
        ///
        /// 资源绑定:
        ///   SRV[0]: StructuredBuffer&lt;ClusterData&gt;        ClusterBuffer
        ///   SRV[1]: ByteAddressBuffer                       TriangleBuffer
        ///   SRV[2]: StructuredBuffer&lt;QuantizedVertex&gt;    VertexBuffer
        ///   CBV[0]: cbClusterRender
        /// </summary>
        public const string ClusterRenderVS = @"
// ============================================================================
// ClusterRenderVS - MicroCluster Cluster Rendering Vertex Shader
// 从Cluster Buffer读取三角形，解码量化顶点，输出屏幕空间位置
// ============================================================================

#include ""MicroClusterShared.hlsli""

// ============================================================================
// 常量缓冲区: 渲染参数
// ============================================================================
cbuffer cbClusterRender : register(b0)
{
    float4x4 ViewMatrix;           // 视图矩阵
    float4x4 ProjMatrix;           // 投影矩阵
    float4x4 ViewProjMatrix;       // 组合视图投影矩阵
    float4x4 WorldMatrix;          // 实例世界矩阵
    float4x4 PrevViewProjMatrix;   // 上一帧视图投影（用于运动向量）
    float3   ClusterBoundsMin;     // 当前mesh的最小边界
    float3   ClusterBoundsMax;     // 当前mesh的最大边界
    float    LODTransitionAlpha;   // LOD过渡混合因子 (0=旧LOD, 1=新LOD)
    int      CurrentLOD;           // 当前LOD层级
    float    Time;                 // 当前时间（用于动画）
    int      MaterialIndexOffset;  // 材质索引偏移
    float3   CameraRight;          // 相机右方向（用于billboard）
    float3   CameraUp;             // 相机上方向
    float    NearPlane;            // 近裁面距离
    float    FarPlane;             // 远裁面距离
};

// ============================================================================
// 资源声明
// ============================================================================
StructuredBuffer<ClusterData>          ClusterBuffer    : register(t0);
ByteAddressBuffer                      TriangleBuffer   : register(t1);
StructuredBuffer<QuantizedVertex>      VertexBuffer     : register(t2);

// ============================================================================
// 顶点着色器输入（来自间接绘制调用）
// ============================================================================
struct VS_INPUT
{
    uint VertexID   : SV_VertexID;   // 顶点全局ID
    uint InstanceID : SV_InstanceID; // 实例ID（每个可见聚类一个实例）
};

// ============================================================================
// 顶点着色器输出
// ============================================================================
struct VS_OUTPUT
{
    float4 Position      : SV_Position;      // 裁剪空间位置
    float3 WorldPos      : TEXCOORD0;        // 世界空间位置
    float3 Normal        : NORMAL;           // 世界空间法线
    float2 TexCoord      : TEXCOORD1;        // 纹理坐标
    float3 PrevClipPos   : TEXCOORD2;        // 上一帧裁剪空间位置（运动向量）
    float  LODBlend      : TEXCOORD3;        // LOD过渡混合因子
    uint   MaterialIndex : TEXCOORD4;        // 材质索引
    float  Depth         : TEXCOORD5;        // 视空间深度
    uint   ClusterIndex  : TEXCOORD6;        // 聚类索引
    nointerpolation uint TriangleID : TEXCOORD7; // 三角形ID（用于像素着色器输出）
};

// ============================================================================
// 入口函数
// ============================================================================
VS_OUTPUT ClusterRenderVS(VS_INPUT input)
{
    VS_OUTPUT output = (VS_OUTPUT)0;

    // ----------------------------------------------------------------
    // 步骤1: 根据InstanceID获取对应的Cluster数据
    // ----------------------------------------------------------------
    // 注意: 在实际管线中，InstanceID映射通过SRV间接查找
    // 这里简化为直接索引
    ClusterData cluster = ClusterBuffer[input.InstanceID];

    // ----------------------------------------------------------------
    // 步骤2: 根据VertexID从TriangleBuffer获取三角形索引
    // ----------------------------------------------------------------
    // 每个ClusterTriangle = 48 bytes (3 * uint vertex + uint material)
    // 但实际使用ByteAddressBuffer需要手动偏移
    uint triIndex = input.VertexID / 3;
    uint vertInTri = input.VertexID % 3;

    // 从三角形缓冲读取：每个三角形16字节 (3个uint索引)
    uint triByteOffset = (cluster.PageOffset + triIndex) * 16;
    uint vertIdx = TriangleBuffer.Load(triByteOffset + vertInTri * 4);
    uint materialIdx = TriangleBuffer.Load(triByteOffset + 12);

    // 边界检查
    [branch]
    if (triIndex >= (uint)cluster.TriangleCount)
    {
        output.Position = float4(0, 0, 0, 1);
        return output;
    }

    // ----------------------------------------------------------------
    // 步骤3: 从VertexBuffer读取量化顶点数据并解码
    // ----------------------------------------------------------------
    QuantizedVertex qv = VertexBuffer[vertIdx];

    // 解码位置
    float3 localPos = DecodeQuantizedPosition(
        qv.PositionPacked, ClusterBoundsMin, ClusterBoundsMax);

    // 应用聚类包围盒偏移
    localPos = cluster.BoundsCenter - cluster.BoundsExtent + localPos * (2.0f * cluster.BoundsExtent);

    // 解码法线
    float3 normal = DecodeOctahedralNormal(qv.NormalPacked);

    // 解码UV
    float2 uv = DecodeQuantizedUV(qv.TexCoordPacked);

    // ----------------------------------------------------------------
    // 步骤4: 变换到世界空间和裁剪空间
    // ----------------------------------------------------------------
    float4 worldPos = mul(WorldMatrix, float4(localPos, 1.0f));
    float4 viewPos = mul(ViewMatrix, worldPos);
    float4 clipPos = mul(ProjMatrix, viewPos);

    // 上一帧裁剪空间位置（用于运动向量计算）
    float4 prevClipPos = mul(PrevViewProjMatrix, worldPos);

    // ----------------------------------------------------------------
    // 步骤5: LOD过渡混合
    // ----------------------------------------------------------------
    // 在LOD切换时，通过Alpha混合平滑过渡
    // 实际实现中需要从两张LOD的顶点缓冲各取一个顶点做lerp
    float lodBlend = LODTransitionAlpha;

    // ----------------------------------------------------------------
    // 步骤6: 填充输出结构
    // ----------------------------------------------------------------
    output.Position = clipPos;
    output.WorldPos = worldPos.xyz;
    output.Normal = normalize(mul((float3x3)WorldMatrix, normal));
    output.TexCoord = uv;
    output.PrevClipPos = prevClipPos.xyz;
    output.LODBlend = lodBlend;
    output.MaterialIndex = materialIdx + (uint)MaterialIndexOffset;
    output.Depth = viewPos.z;
    output.ClusterIndex = input.InstanceID;
    output.TriangleID = triIndex;

    return output;
}
";

        // ====================================================================
        // 着色器3: ClusterRenderPS — 聚类可见性渲染像素着色器
        // ====================================================================

        /// <summary>
        /// ClusterRenderPS 像素着色器。
        /// 执行可见性缓冲渲染，将 Cluster ID 写入 RG32 格式的
        /// 可见性缓冲，不执行实际的光照和材质计算。
        /// 光照在后续的延迟着色 pass 中根据可见性缓冲数据进行。
        ///
        /// 资源绑定:
        ///   UAV[0]: RWTexture2D&lt;uint2&gt;  VisibilityBuffer (RG32)
        ///   SRV[0]: Texture2D               MaterialTextures[]
        /// </summary>
        public const string ClusterRenderPS = @"
// ============================================================================
// ClusterRenderPS - MicroCluster Visibility Buffer Pixel Shader
// 输出Cluster ID到可见性缓冲(RG32格式)，供后续延迟着色使用
// ============================================================================

#include ""MicroClusterShared.hlsli""

// ============================================================================
// 常量缓冲区: 像素着色器参数
// ============================================================================
cbuffer cbClusterPixel : register(b0)
{
    int   FrameIndex;             // 帧索引
    float DepthBias;              // 深度偏移
    int   EnableMotionVectors;    // 是否输出运动向量
    int   OutputMode;             // 0=可见性缓冲, 1=调试颜色, 2=LOD可视化
    float DebugColorScale;        // 调试颜色缩放
    int   Padding0;
    int   Padding1;
    int   Padding2;
};

// ============================================================================
// 资源声明
// ============================================================================
RWTexture2D<uint2>  VisibilityBuffer    : register(u0);  // RG32可见性缓冲
RWTexture2D<float2> MotionVectorBuffer  : register(u1);  // RG16运动向量缓冲
Texture2D          DepthPyramid         : register(t0);  // 深度金字塔

// ============================================================================
// 像素着色器输入（来自VS输出）
// ============================================================================
struct PS_INPUT
{
    float4 Position      : SV_Position;
    float3 WorldPos      : TEXCOORD0;
    float3 Normal        : NORMAL;
    float2 TexCoord      : TEXCOORD1;
    float3 PrevClipPos   : TEXCOORD2;
    float  LODBlend      : TEXCOORD3;
    uint   MaterialIndex : TEXCOORD4;
    float  Depth         : TEXCOORD5;
    uint   ClusterIndex  : TEXCOORD6;
    nointerpolation uint TriangleID : TEXCOORD7;
};

// ============================================================================
// 工具函数：将Cluster ID和Triangle ID打包到RG32
// R通道 = ClusterIndex (低24位) + LOD (高8位)
// G通道 = TriangleIndex (低24位) + MaterialIndex (高8位)
// ============================================================================
uint2 PackVisibilityData(
    uint clusterIndex,
    uint triangleID,
    uint materialIndex,
    uint lodLevel)
{
    uint r = clusterIndex | (lodLevel << 24);
    uint g = triangleID | (materialIndex << 24);
    return uint2(r, g);
}

// ============================================================================
// 工具函数：根据Cluster ID生成调试颜色
// 用于可视化调试，便于观察聚类分布
// ============================================================================
float3 ClusterDebugColor(uint clusterIndex, uint triangleID)
{
    // 使用哈希函数生成确定性伪随机颜色
    uint hash = clusterIndex * 374761393u + triangleID * 668265263u;
    hash = (hash ^ (hash >> 13u)) * 1274126177u;
    hash = hash ^ (hash >> 16u);

    float r = ((hash >> 0u) & 0xFFu) / 255.0f;
    float g = ((hash >> 8u) & 0xFFu) / 255.0f;
    float b = ((hash >> 16u) & 0xFFu) / 255.0f;

    return float3(r, g, b);
}

// ============================================================================
// 工具函数：LOD层级可视化颜色
// ============================================================================
float3 LODDebugColor(float lodBlend, int currentLOD)
{
    // 不同LOD层级使用不同色温
    float3 lodColors[8] = {
        float3(0.0, 0.8, 1.0),  // LOD0 - 蓝色（最高精度）
        float3(0.0, 1.0, 0.5),  // LOD1 - 青色
        float3(0.0, 1.0, 0.0),  // LOD2 - 绿色
        float3(0.5, 1.0, 0.0),  // LOD3 - 黄绿
        float3(1.0, 1.0, 0.0),  // LOD4 - 黄色
        float3(1.0, 0.5, 0.0),  // LOD5 - 橙色
        float3(1.0, 0.0, 0.0),  // LOD6 - 红色
        float3(1.0, 0.0, 1.0),  // LOD7 - 品红（最低精度）
    };

    int lodIdx = min(currentLOD, 7);
    return lodColors[lodIdx] * (0.5f + 0.5f * lodBlend);
}

// ============================================================================
// 入口函数
// ============================================================================
void ClusterRenderPS(PS_INPUT input)
{
    uint2 pixelPos = (uint2)input.Position.xy;

    // ----------------------------------------------------------------
    // 模式0: 正常可见性缓冲输出
    // ----------------------------------------------------------------
    [branch]
    if (OutputMode == 0)
    {
        // 计算当前LOD层级（简化：从LODBlend推算）
        uint lodLevel = (uint)(round(input.LODBlend * 7.0f));
        lodLevel = min(lodLevel, NANITE_MAX_LOD_LEVELS - 1);

        // 打包可见性数据并写入RG32缓冲
        uint2 visData = PackVisibilityData(
            input.ClusterIndex,
            input.TriangleID,
            input.MaterialIndex,
            lodLevel
        );
        VisibilityBuffer[pixelPos] = visData;

        // 输出运动向量（如果启用）
        [branch]
        if (EnableMotionVectors != 0)
        {
            // 当前帧NDC
            float2 ndcCurr = input.Position.xy / input.Position.w * 0.5f + 0.5f;

            // 上一帧NDC
            float2 ndcPrev = input.PrevClipPos.xy / input.PrevClipPos.z * 0.5f + 0.5f;

            // 运动向量 = 当前 - 上一帧
            float2 motionVec = ndcCurr - ndcPrev;

            // 写入运动向量缓冲（半精度浮点）
            MotionVectorBuffer[pixelPos] = motionVec;
        }
    }
    // ----------------------------------------------------------------
    // 模式1: 调试颜色模式 - 每个聚类/三角形不同颜色
    // ----------------------------------------------------------------
    else if (OutputMode == 1)
    {
        float3 debugColor = ClusterDebugColor(
            input.ClusterIndex,
            input.TriangleID
        );
        debugColor *= DebugColorScale;

        // 将颜色编码到可见性缓冲（调试用途）
        uint r = (uint)(debugColor.r * 255.0f) << 16;
        uint g = (uint)(debugColor.g * 255.0f) << 8;
        uint b = (uint)(debugColor.b * 255.0f);
        VisibilityBuffer[pixelPos] = uint2(r | g, b);
    }
    // ----------------------------------------------------------------
    // 模式2: LOD可视化模式 - 不同LOD层级不同颜色
    // ----------------------------------------------------------------
    else if (OutputMode == 2)
    {
        uint lodLevel = (uint)(round(input.LODBlend * 7.0f));
        float3 lodColor = LODDebugColor(input.LODBlend, (int)lodLevel);

        uint r = (uint)(lodColor.r * 255.0f) << 16;
        uint g = (uint)(lodColor.g * 255.0f) << 8;
        uint b = (uint)(lodColor.b * 255.0f);
        VisibilityBuffer[pixelPos] = uint2(r | g, b);
    }
}
";

        // ====================================================================
        // 着色器4: EdgeCollapseCS — 边折叠网格简化计算着色器
        // ====================================================================

        /// <summary>
        /// EdgeCollapseCS 边折叠计算着色器。
        /// 执行边折叠算法简化网格，生成连续的 LOD 层级。
        /// 每个线程处理一条候选边，计算折叠代价并生成简化结果。
        ///
        /// 资源绑定:
        ///   SRV[0]: StructuredBuffer&lt;float3&gt;  VertexPositionBuffer
        ///   SRV[1]: StructuredBuffer&lt;float3&gt;  VertexNormalBuffer
        ///   SRV[2]: StructuredBuffer&lt;uint&gt;    IndexBuffer
        ///   UAV[0]: RWStructuredBuffer&lt;EdgeCollapseCandidate&gt;  CollapseCandidates
        ///   UAV[1]: RWStructuredBuffer&lt;uint&gt;   RemapTable
        /// </summary>
        public const string EdgeCollapseCS = @"
// ============================================================================
// EdgeCollapseCS - MicroCluster Edge Collapse Compute Shader
// 边折叠简化算法，生成LOD层级
// ============================================================================

#include ""MicroClusterShared.hlsli""

// ============================================================================
// 常量缓冲区: 边折叠参数
// ============================================================================
cbuffer cbEdgeCollapse : register(b0)
{
    int    VertexCount;           // 输入顶点数
    int    IndexCount;            // 输入索引数
    int    TriangleCount;         // 输入三角形数
    int    TargetTriangleCount;   // 目标三角形数（该LOD层级的目标）
    float  EdgeLengthWeight;      // 边长度权重
    float  NormalDeviationWeight; // 法线偏差权重
    float  UVSeamWeight;          // UV接缝权重
    float  BoundaryWeight;        // 边界边权重
    float  MaxCollapseError;      // 最大允许折叠误差
    int    CurrentLODLevel;       // 当前正在生成的LOD层级
    int    PreserveHardEdges;     // 是否保留硬边 (1=保留)
    float  HardEdgeAngle;         // 硬边角度阈值（度）
    int    Padding0;
    int    Padding1;
};

// ============================================================================
// 资源声明
// ============================================================================
StructuredBuffer<float3>                 VertexPositionBuffer : register(t0);
StructuredBuffer<float3>                 VertexNormalBuffer   : register(t1);
StructuredBuffer<float2>                 VertexUVBuffer       : register(t2);
StructuredBuffer<uint>                   IndexBuffer          : register(t3);
StructuredBuffer<EdgeCollapseCandidate>  InputEdges           : register(t4);

RWStructuredBuffer<EdgeCollapseCandidate> CollapseCandidates  : register(u0);
RWStructuredBuffer<float3>                OutputVertices      : register(u1);
RWStructuredBuffer<uint>                  OutputIndices       : register(u2);
RWStructuredBuffer<uint>                  VertexRemap         : register(u3);
RWByteAddressBuffer                       CollapseCounter     : register(u4);

// ============================================================================
// 工具函数：计算两个法线之间的角度偏差
// ============================================================================
float ComputeNormalDeviation(float3 n1, float3 n2)
{
    float dotProduct = clamp(dot(normalize(n1), normalize(n2)), -1.0f, 1.0f);
    return acos(dotProduct); // 返回弧度值
}

// ============================================================================
// 工具函数：计算边的UV接缝代价
// 如果边两侧的UV不连续，增加代价以避免在接缝处折叠
// ============================================================================
float ComputeUVSeamCost(
    float2 uv0_a, float2 uv0_b,  // 顶点A的UV（三角形0侧）
    float2 uv1_a, float2 uv1_b,  // 顶点A的UV（三角形1侧）
    float2 uv2_a, float2 uv2_b)  // 共享边的另一端UV
{
    float seamA = length(uv0_a - uv0_b);
    float seamB = length(uv1_a - uv1_b);
    float seamShared = length(uv2_a - uv2_b);
    return (seamA + seamB + seamShared) / 3.0f;
}

// ============================================================================
// 工具函数：计算边折叠代价
// 综合考虑边长度、法线偏差、UV接缝等因素
// ============================================================================
float ComputeCollapseCost(
    float3 posA,
    float3 posB,
    float3 normalA,
    float3 normalB,
    float2 uvA,
    float2 uvB,
    float  boundaryA,  // 0=内部顶点, 1=边界顶点
    float  boundaryB)
{
    // 因子1: 边长度（短边优先折叠）
    float edgeLength = length(posA - posB);
    float lengthCost = edgeLength * EdgeLengthWeight;

    // 因子2: 法线偏差（法线相似的优先折叠）
    float normalDev = ComputeNormalDeviation(normalA, normalB);
    float normalCost = normalDev * NormalDeviationWeight;

    // 因子3: UV接缝（有接缝的边折叠代价更高）
    float uvCost = length(uvA - uvB) * UVSeamWeight;

    // 因子4: 边界边（边界边折叠代价更高）
    float boundaryCost = (boundaryA + boundaryB) * BoundaryWeight;

    // 综合代价
    float totalCost = lengthCost + normalCost + uvCost + boundaryCost;

    // 检查是否超过最大允许误差
    if (totalCost > MaxCollapseError)
        totalCost = FLOAT_MAX;

    return totalCost;
}

// ============================================================================
// 工具函数：判断是否为硬边
// ============================================================================
bool IsHardEdge(float3 normalA, float3 normalB, float threshold)
{
    float angle = ComputeNormalDeviation(normalA, normalB);
    float thresholdRad = threshold * 3.14159265f / 180.0f;
    return angle > thresholdRad;
}

// ============================================================================
// 线程组共享内存 - 用于局部排序
// ============================================================================
groupshared EdgeCollapseCandidate gs_Candidates[NANITE_THREADGROUP_SIZE];

// ============================================================================
// 入口函数
// ============================================================================
[numthreads(NANITE_THREADGROUP_SIZE, 1, 1)]
void EdgeCollapseCS(
    uint3 GroupId           : SV_GroupID,
    uint3 GroupThreadId     : SV_GroupThreadId,
    uint3 DispatchThreadId  : SV_DispatchThreadID,
    uint  GroupIndex        : SV_GroupIndex)
{
    uint edgeIndex = DispatchThreadId.x;

    EdgeCollapseCandidate candidate;
    candidate.VertexA = 0;
    candidate.VertexB = 0;
    candidate.CollapseCost = FLOAT_MAX;
    candidate.TriangleCount = 0;

    // ----------------------------------------------------------------
    // 步骤1: 读取候选边并计算折叠代价
    // ----------------------------------------------------------------
    [branch]
    if (edgeIndex < (uint)TriangleCount)
    {
        // 从索引缓冲读取三角形的三个顶点
        uint i0 = IndexBuffer[edgeIndex * 3 + 0];
        uint i1 = IndexBuffer[edgeIndex * 3 + 1];
        uint i2 = IndexBuffer[edgeIndex * 3 + 2];

        // 对三条边分别计算折叠代价
        float3 pos0 = VertexPositionBuffer[i0];
        float3 pos1 = VertexPositionBuffer[i1];
        float3 pos2 = VertexPositionBuffer[i2];
        float3 nor0 = VertexNormalBuffer[i0];
        float3 nor1 = VertexNormalBuffer[i1];
        float3 nor2 = VertexNormalBuffer[i2];
        float2 uv0  = VertexUVBuffer[i0];
        float2 uv1  = VertexUVBuffer[i1];
        float2 uv2  = VertexUVBuffer[i2];

        // 边 0-1
        [branch]
        if (!IsHardEdge(nor0, nor1, HardEdgeAngle) || PreserveHardEdges == 0)
        {
            float cost = ComputeCollapseCost(
                pos0, pos1, nor0, nor1, uv0, uv1, 0.0f, 0.0f);

            [flatten]
            if (cost < candidate.CollapseCost)
            {
                candidate.VertexA = min(i0, i1);
                candidate.VertexB = max(i0, i1);
                candidate.CollapseCost = cost;
                candidate.TriangleCount = 1;
            }
        }

        // 边 1-2
        [branch]
        if (!IsHardEdge(nor1, nor2, HardEdgeAngle) || PreserveHardEdges == 0)
        {
            float cost = ComputeCollapseCost(
                pos1, pos2, nor1, nor2, uv1, uv2, 0.0f, 0.0f);

            [flatten]
            if (cost < candidate.CollapseCost)
            {
                candidate.VertexA = min(i1, i2);
                candidate.VertexB = max(i1, i2);
                candidate.CollapseCost = cost;
                candidate.TriangleCount = 1;
            }
        }

        // 边 2-0
        [branch]
        if (!IsHardEdge(nor2, nor0, HardEdgeAngle) || PreserveHardEdges == 0)
        {
            float cost = ComputeCollapseCost(
                pos2, pos0, nor2, nor0, uv2, uv0, 0.0f, 0.0f);

            [flatten]
            if (cost < candidate.CollapseCost)
            {
                candidate.VertexA = min(i2, i0);
                candidate.VertexB = max(i2, i0);
                candidate.CollapseCost = cost;
                candidate.TriangleCount = 1;
            }
        }
    }

    // ----------------------------------------------------------------
    // 步骤2: 写入候选列表
    // ----------------------------------------------------------------
    CollapseCandidates[edgeIndex] = candidate;

    // ----------------------------------------------------------------
    // 步骤3: 线程组内局部排序（简化: 每个线程写入，后续排序由另一个pass处理）
    // ----------------------------------------------------------------
    gs_Candidates[GroupIndex] = candidate;
    GroupMemoryBarrierWithGroupSync();

    // ----------------------------------------------------------------
    // 步骤4: 组内线程0执行局部归约，找出最优折叠候选
    // ----------------------------------------------------------------
    if (GroupIndex == 0)
    {
        EdgeCollapseCandidate best = gs_Candidates[0];

        [unroll]
        for (uint i = 1; i < NANITE_THREADGROUP_SIZE; i++)
        {
            if (gs_Candidates[i].CollapseCost < best.CollapseCost)
            {
                best = gs_Candidates[i];
            }
        }

        // 将该线程组的最佳候选写入全局输出
        uint outOffset;
        CollapseCounter.InterlockedAdd(0, 1, outOffset);
        CollapseCandidates[outOffset] = best;
    }
}
";

        // ====================================================================
        // 着色器5: ImpostorRenderVS — 替身渲染顶点着色器
        // ====================================================================

        /// <summary>
        /// ImpostorRenderVS 替身渲染顶点着色器。
        /// 对于远距离的 Cluster，使用 Billboard 替代实际几何体。
        /// 根据相机方向选择最近的预渲染视角，生成 Billboard 四边形。
        ///
        /// 资源绑定:
        ///   SRV[0]: StructuredBuffer&lt;ClusterData&gt;  ClusterBuffer
        ///   CBV[0]: cbImpostorRender
        /// </summary>
        public const string ImpostorRenderVS = @"
// ============================================================================
// ImpostorRenderVS - MicroCluster Impostor/Billboard Vertex Shader
// 远距离Cluster用Billboard替代，根据相机方向选择最佳视角
// ============================================================================

#include ""MicroClusterShared.hlsli""

// ============================================================================
// 常量缓冲区: 替身渲染参数
// ============================================================================
cbuffer cbImpostorRender : register(b0)
{
    float4x4 ViewProjMatrix;       // 视图投影矩阵
    float4x4 WorldMatrix;          // 实例世界矩阵
    float3   ViewOrigin;           // 相机世界坐标
    float    ImpostorSize;         // 替身Billboard大小
    float3   CameraRight;          // 相机右方向向量
    float3   CameraUp;             // 相机上方向向量
    float    FadeInDistance;        // 淡入起始距离
    float    FadeOutDistance;       // 淡出结束距离
    int      ImpostorAtlasSize;     // 替身图集尺寸（每行/列数）
    int      CurrentFrameIndex;     // 当前帧索引
    float    AlphaThreshold;        // Alpha测试阈值
    float    DistanceFadeWidth;     // 距离淡入淡出宽度
};

// ============================================================================
// 资源声明
// ============================================================================
StructuredBuffer<ClusterData> ClusterBuffer     : register(t0);
Texture2D                     ImpostorAtlas      : register(t1);
SamplerState                  ImpostorSampler    : register(s0);

// ============================================================================
// 顶点着色器输入
// ============================================================================
struct IMPOSTOR_VS_INPUT
{
    float3 Position    : POSITION;    // Billboard局部坐标 (-0.5~0.5)
    float2 TexCoord    : TEXCOORD0;   // Billboard UV (0~1)
    uint   InstanceID  : SV_InstanceID;
};

// ============================================================================
// 顶点着色器输出
// ============================================================================
struct IMPOSTOR_VS_OUTPUT
{
    float4 Position         : SV_Position;
    float2 AtlasUV          : TEXCOORD0;   // 图集中的UV坐标
    float3 WorldPos         : TEXCOORD1;   // 世界空间位置
    float  DistanceFade     : TEXCOORD2;   // 距离淡入淡出因子
    float  ViewDotProduct   : TEXCOORD3;   // 视角点积（用于背面判断）
    uint   ClusterIndex     : TEXCOORD4;   // 聚类索引
    uint   ImpostorSlice    : TEXCOORD5;   // 选择的替身切片索引
};

// ============================================================================
// 工具函数：根据相机方向计算最佳替身切片
// 替身图集通常是N x N的网格，每个格子对应一个预渲染角度
// ============================================================================
uint ComputeBestImpostorSlice(float3 viewDir, int atlasSize)
{
    // 将3D方向投影到水平面计算角度
    float angle = atan2(viewDir.x, viewDir.z); // [-PI, PI]

    // 归一化到 [0, 2*PI)
    if (angle < 0.0f) angle += 3.14159265f * 2.0f;

    // 计算总切片数（通常每行=每列=atlasSize）
    int totalSlices = atlasSize * atlasSize;

    // 如果是多行（考虑仰角），这里简化为单行
    // 实际实现中应使用球面坐标
    int sliceCount = atlasSize; // 简化: 只考虑水平旋转
    float sliceAngle = 3.14159265f * 2.0f / (float)sliceCount;
    int slice = (int)(angle / sliceAngle);
    slice = clamp(slice, 0, sliceCount - 1);

    return (uint)slice;
}

// ============================================================================
// 工具函数：计算距离淡入淡出因子
// ============================================================================
float ComputeDistanceFade(float distance, float fadeIn, float fadeOut, float fadeWidth)
{
    // fadeOut < fadeIn: 远处淡出
    if (distance > fadeOut)
        return 1.0f - saturate((distance - fadeOut) / fadeWidth);

    // 距离过近时不使用替身
    if (distance < fadeIn)
        return saturate((distance - fadeIn + fadeWidth) / fadeWidth);

    return 1.0f;
}

// ============================================================================
// 入口函数
// ============================================================================
IMPOSTOR_VS_OUTPUT ImpostorRenderVS(IMPOSTOR_VS_INPUT input)
{
    IMPOSTOR_VS_OUTPUT output = (IMPOSTOR_VS_OUTPUT)0;

    // ----------------------------------------------------------------
    // 步骤1: 获取Cluster数据
    // ----------------------------------------------------------------
    ClusterData cluster = ClusterBuffer[input.InstanceID];

    // ----------------------------------------------------------------
    // 步骤2: 计算Billboard世界空间变换
    // Billboard始终面向相机，使用CameraRight和CameraUp构建局部坐标系
    // ----------------------------------------------------------------
    float3 worldPos = cluster.BoundsCenter;
    float3 right = CameraRight * ImpostorSize;
    float3 up = CameraUp * ImpostorSize;

    // 从Billboard局部坐标变换到世界空间
    worldPos += input.Position.x * right + input.Position.y * up;

    // ----------------------------------------------------------------
    // 步骤3: 计算相机方向和视角选择
    // ----------------------------------------------------------------
    float3 viewDir = normalize(ViewOrigin - cluster.BoundsCenter);
    uint bestSlice = ComputeBestImpostorSlice(viewDir, ImpostorAtlasSize);

    // 视角点积，用于后续背面判断
    float viewDot = dot(viewDir, float3(0, 0, 1)); // 前方向

    // ----------------------------------------------------------------
    // 步骤4: 计算图集UV
    // 将[0,1]的Billboard UV映射到图集中对应的切片区域
    // ----------------------------------------------------------------
    float2 atlasUV;
    float sliceU = (float)(bestSlice % (uint)ImpostorAtlasSize) / (float)ImpostorAtlasSize;
    float sliceV = (float)(bestSlice / (uint)ImpostorAtlasSize) / (float)ImpostorAtlasSize;
    float sliceSize = 1.0f / (float)ImpostorAtlasSize;

    atlasUV.x = sliceU + input.TexCoord.x * sliceSize;
    atlasUV.y = sliceV + input.TexCoord.y * sliceSize;

    // ----------------------------------------------------------------
    // 步骤5: 计算距离淡入淡出
    // ----------------------------------------------------------------
    float distance = length(ViewOrigin - cluster.BoundsCenter);
    float fadeFactor = ComputeDistanceFade(distance, FadeInDistance, FadeOutDistance, DistanceFadeWidth);

    // ----------------------------------------------------------------
    // 步骤6: 变换到裁剪空间并输出
    // ----------------------------------------------------------------
    float4 worldPos4 = mul(WorldMatrix, float4(worldPos, 1.0f));
    float4 clipPos = mul(ViewProjMatrix, worldPos4);

    output.Position = clipPos;
    output.AtlasUV = atlasUV;
    output.WorldPos = worldPos;
    output.DistanceFade = fadeFactor;
    output.ViewDotProduct = viewDot;
    output.ClusterIndex = input.InstanceID;
    output.ImpostorSlice = bestSlice;

    return output;
}
";

        // ====================================================================
        // 着色器6: ImpostorRenderPS — 替身渲染像素着色器
        // ====================================================================

        /// <summary>
        /// ImpostorRenderPS 替身渲染像素着色器。
        /// 从替身图集采样，应用距离淡入淡出和 Alpha 测试，
        /// 输出到可见性缓冲（与 ClusterRenderPS 兼容）。
        ///
        /// 资源绑定:
        ///   SRV[0]: Texture2D  ImpostorAtlas
        ///   UAV[0]: RWTexture2D&lt;uint2&gt;  VisibilityBuffer
        /// </summary>
        public const string ImpostorRenderPS = @"
// ============================================================================
// ImpostorRenderPS - MicroCluster Impostor/Billboard Pixel Shader
// 从替身图集采样，应用淡入淡出和Alpha测试，输出到可见性缓冲
// ============================================================================

#include ""MicroClusterShared.hlsli""

// ============================================================================
// 常量缓冲区
// ============================================================================
cbuffer cbImpostorPixel : register(b0)
{
    float   AlphaThreshold;     // Alpha测试阈值
    int     OutputMode;         // 0=可见性缓冲, 1=调试
    float   BrightnessScale;    // 亮度缩放
    int     EnableDitherFade;   // 是否启用抖动淡出
    uint    ImpostorMarker;     // 替身标记值（写入可见性缓冲的高位）
    int     Padding0;
    int     Padding1;
};

// ============================================================================
// 资源声明
// ============================================================================
Texture2D           ImpostorAtlas      : register(t0);
SamplerState        ImpostorSampler    : register(s0);
RWTexture2D<uint2>  VisibilityBuffer   : register(u0);

// ============================================================================
// 像素着色器输入
// ============================================================================
struct IMPOSTOR_PS_INPUT
{
    float4 Position         : SV_Position;
    float2 AtlasUV          : TEXCOORD0;
    float3 WorldPos         : TEXCOORD1;
    float  DistanceFade     : TEXCOORD2;
    float  ViewDotProduct   : TEXCOORD3;
    uint   ClusterIndex     : TEXCOORD4;
    uint   ImpostorSlice    : TEXCOORD5;
};

// ============================================================================
// 工具函数：4x4 Bayer矩阵抖动
// 用于渐变淡出时避免色带
// ============================================================================
float BayerDither4x4(uint2 pixelPos)
{
    // 4x4 Bayer有序抖动矩阵
    const float4x4 bayerMatrix = {
        { 0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0 },
        {12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0 },
        { 3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0 },
        {15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0 }
    };

    uint2 modPos = pixelPos % 4;
    return bayerMatrix[modPos.y][modPos.x];
}

// ============================================================================
// 入口函数
// ============================================================================
void ImpostorRenderPS(IMPOSTOR_PS_INPUT input)
{
    uint2 pixelPos = (uint2)input.Position.xy;

    // ----------------------------------------------------------------
    // 步骤1: 从替身图集采样颜色
    // ----------------------------------------------------------------
    float4 texColor = ImpostorAtlas.Sample(ImpostorSampler, input.AtlasUV);

    // 应用亮度缩放
    texColor.rgb *= BrightnessScale;

    // ----------------------------------------------------------------
    // 步骤2: 距离淡入淡出
    // ----------------------------------------------------------------
    float finalAlpha = texColor.a * input.DistanceFade;

    // 抖动淡出（避免硬边）
    [branch]
    if (EnableDitherFade != 0)
    {
        float dither = BayerDither4x4(pixelPos);
        finalAlpha = step(dither, finalAlpha);
    }

    // ----------------------------------------------------------------
    // 步骤3: Alpha测试
    // ----------------------------------------------------------------
    [branch]
    if (finalAlpha < AlphaThreshold)
        discard; // 丢弃透明像素

    // ----------------------------------------------------------------
    // 步骤4: 背面剔除（基于视角点积）
    // ----------------------------------------------------------------
    [branch]
    if (input.ViewDotProduct < -0.1f)
        discard; // 背面丢弃

    // ----------------------------------------------------------------
    // 步骤5: 输出到可见性缓冲
    // ----------------------------------------------------------------
    [branch]
    if (OutputMode == 0)
    {
        // 替身标记: 高8位设为ImpostorMarker标识
        // 低24位为ClusterIndex
        uint r = input.ClusterIndex | (ImpostorMarker << 24);

        // G通道: 替身切片索引（低16位）+ Alpha（高16位，8.8定点）
        uint alphaPacked = (uint)(saturate(finalAlpha) * 65535.0f);
        uint g = input.ImpostorSlice | (alphaPacked << 16);

        VisibilityBuffer[pixelPos] = uint2(r, g);
    }
    // ----------------------------------------------------------------
    // 模式1: 调试输出
    // ----------------------------------------------------------------
    else if (OutputMode == 1)
    {
        // 输出图集UV用于调试
        uint u = (uint)(input.AtlasUV.x * 65535.0f);
        uint v = (uint)(input.AtlasUV.y * 65535.0f);
        VisibilityBuffer[pixelPos] = uint2(u, v);
    }
}
";

        // ====================================================================
        // 着色器辅助方法：获取完整的着色器源码（含共享头文件）
        // ====================================================================

        /// <summary>
        /// 获取合并了共享头文件的完整 ClusterCullCS 源码。
        /// </summary>
        public static string GetClusterCullCSSource()
        {
            return SharedHLSLHeader + "\n" + ClusterCullCS;
        }

        /// <summary>
        /// 获取合并了共享头文件的完整 ClusterRenderVS 源码。
        /// </summary>
        public static string GetClusterRenderVSSource()
        {
            return SharedHLSLHeader + "\n" + ClusterRenderVS;
        }

        /// <summary>
        /// 获取合并了共享头文件的完整 ClusterRenderPS 源码。
        /// </summary>
        public static string GetClusterRenderPSSource()
        {
            return SharedHLSLHeader + "\n" + ClusterRenderPS;
        }

        /// <summary>
        /// 获取合并了共享头文件的完整 EdgeCollapseCS 源码。
        /// </summary>
        public static string GetEdgeCollapseCSSource()
        {
            return SharedHLSLHeader + "\n" + EdgeCollapseCS;
        }

        /// <summary>
        /// 获取合并了共享头文件的完整 ImpostorRenderVS 源码。
        /// </summary>
        public static string GetImpostorRenderVSSource()
        {
            return SharedHLSLHeader + "\n" + ImpostorRenderVS;
        }

        /// <summary>
        /// 获取合并了共享头文件的完整 ImpostorRenderPS 源码。
        /// </summary>
        public static string GetImpostorRenderPSSource()
        {
            return SharedHLSLHeader + "\n" + ImpostorRenderPS;
        }

        /// <summary>
        /// 获取所有着色器的名称列表，用于批量编译。
        /// </summary>
        public static readonly string[] ShaderNames = new string[]
        {
            "ClusterCullCS",
            "ClusterRenderVS",
            "ClusterRenderPS",
            "EdgeCollapseCS",
            "ImpostorRenderVS",
            "ImpostorRenderPS"
        };

        /// <summary>
        /// 获取所有着色器的完整源码列表，与 ShaderNames 一一对应。
        /// </summary>
        public static string[] GetAllShaderSources()
        {
            return new string[]
            {
                GetClusterCullCSSource(),
                GetClusterRenderVSSource(),
                GetClusterRenderPSSource(),
                GetEdgeCollapseCSSource(),
                GetImpostorRenderVSSource(),
                GetImpostorRenderPSSource()
            };
        }
    }
}