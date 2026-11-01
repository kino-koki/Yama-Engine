// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - ProbeField Voxel Scene
// --------------------------------------------------------------------------
// 体素化场景: 将场景几何栅格化到 3D 纹理, 每体素存储反照率 + 法线 + 辐射度。
// 支持锥体追踪 (镜面 GI) 与体素光线步进 (漫反射 GI)。
//
// 该 CS 从屏幕 G-Buffer 重建表面, 将可见表面写入对应 3D 体素,
// 同时清除辐射度体素并为后续光照填充做准备。
//
// 入口点: VoxelizeCS  (CS, [numthreads(8,8,8)])
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 体素化常量缓冲
// ──────────────────────────────────────────────────────────────────────────
cbuffer VoxelConstants : register(b2)
{
    float4x4 ViewProjMatrix;
    float4x4 InvViewProj;
    float3   VoxelOrigin;        // 体素场景原点 (世界)
    float    VoxelExtent;        // 体素场景半范围
    uint     VoxelResolution;    // 每轴体素分辨率
    float    VoxelSize;          // 单体素世界尺寸 = Extent*2/Resolution
    uint     FrameIndex;
    float    Voxelpad0;
    float    Voxelpad1;
    float    Voxelpad2;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4>  gGeometryBufferAlbedo   : register(t0); // 反照率 + 粗糙度
Texture2D<float4>  gGeometryBufferNormal   : register(t1); // 法线 + 金属度
Texture2D<float>   gDepthBuffer     : register(t2); // 场景深度
Texture2D<float4>  gSceneColor      : register(t3); // 场景光照 (用于初始化辐射度)

RWTexture3D<unorm float4> gVoxelAlbedo   : register(u0); // 体素反照率 (RGBA8)
RWTexture3D<unorm float4> gVoxelNormal   : register(u1); // 体素法线 (R10G10B10A2 近似为 RGBA8)
RWTexture3D<float4>       gVoxelRadiance : register(u2); // 体素辐射度 (RGBA16F)

SamplerState gPointClampSampler  : register(s0);
SamplerState gLinearClampSampler : register(s2);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 世界位置 -> 体素坐标 [0, Resolution)
int3 WorldToVoxel(float3 worldPos)
{
    float3 local = (worldPos - VoxelOrigin + VoxelExtent) / (VoxelExtent * 2.0);
    return int3(local * float(VoxelResolution));
}

// 体素坐标 -> 世界位置中心
float3 VoxelToWorld(int3 voxelCoord)
{
    float3 local = (voxelCoord + 0.5) / float(VoxelResolution);
    return VoxelOrigin + local * (VoxelExtent * 2.0) - VoxelExtent;
}

// 将方向法线编码到 RGBA8 (近似)
unorm float4 EncodeNormalRGBA8(float3 n)
{
    return unorm float4(n * 0.5 + 0.5, 1.0);
}

// ─────────────────────────────────══════════════════════════════════════─
// 体素化主着色器
// 每个线程处理一个体素: 清除 + 从屏幕空间回投填充
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 8)]
void VoxelizeCS(uint3 dispatchID : SV_DispatchThreadID)
{
    int3 voxelCoord = int3(dispatchID.xyz);

    // 越界检查
    if (any(voxelCoord >= (int3)VoxelResolution))
        return;

    // ── 1. 清除阶段: 第一帧或周期性清除 ──
    // 使用帧索引交错清除 (每 N 帧完整清除一次, 避免每帧全清开销)
    uint clearPeriod = 4;
    bool shouldClear = (FrameIndex % clearPeriod == 0);

    if (shouldClear)
    {
        gVoxelAlbedo[voxelCoord]   = unorm float4(0, 0, 0, 0);
        gVoxelNormal[voxelCoord]   = unorm float4(0, 0, 0, 0);
        gVoxelRadiance[voxelCoord] = float4(0, 0, 0, 0);
    }

    // ── 2. 从屏幕空间回投填充体素 ──
    // 将体素中心投影到屏幕, 采样 G-Buffer 写入体素
    float3 worldPos = VoxelToWorld(voxelCoord);

    float4 clipPos = mul(ViewProjMatrix, float4(worldPos, 1.0));
    if (clipPos.w <= 0.0)
        return;

    float2 ndc = clipPos.xy / clipPos.w;
    float2 screenUV = ndc * 0.5 + 0.5;

    // 屏幕外则跳过
    if (screenUV.x < 0.0 || screenUV.x > 1.0 || screenUV.y < 0.0 || screenUV.y > 1.0)
        return;

    uint2 screenRes;
    gDepthBuffer.GetDimensions(screenRes.x, screenRes.y);
    uint2 screenPixel = uint2(screenUV * screenRes);

    if (screenPixel.x >= screenRes.x || screenPixel.y >= screenRes.y)
        return;

    // 深度测试: 体素深度与屏幕深度匹配才填充
    float sceneDepth = gDepthBuffer[screenPixel];
    float4 depthClip = float4(ndc, sceneDepth, 1.0);
    depthClip.y = -depthClip.y;
    float4 depthWorld = mul(InvViewProj, depthClip);
    depthWorld.xyz /= depthWorld.w;

    float sceneDistance = length(depthWorld.xyz - VoxelOrigin + VoxelExtent);
    float voxelDistance = length(worldPos - VoxelOrigin + VoxelExtent);

    // 深度一致性: 体素与屏幕像素对应同一表面
    float depthThreshold = VoxelSize * 1.5;
    if (abs(sceneDistance - voxelDistance) > depthThreshold)
        return;

    // 采样 G-Buffer
    float4 albedoRough = gGeometryBufferAlbedo[screenPixel];
    float4 normalMetal = gGeometryBufferNormal[screenPixel];

    if (albedoRough.a <= 0.0)
        return;

    float3 albedo = albedoRough.rgb;
    float  roughness = albedoRough.a;
    float3 normal = DecodeNormalOctahedral(normalMetal.xy);
    float  metallic = normalMetal.z;

    // ── 3. 写入体素属性 ──
    // 反照率 (保留 alpha 作为占用率)
    unorm float4 albedoVoxel = unorm float4(albedo, 1.0);
    gVoxelAlbedo[voxelCoord] = albedoVoxel;

    // 法线 (编码到 RGBA8)
    gVoxelNormal[voxelCoord] = EncodeNormalRGBA8(normal);

    // 辐射度: 从场景光照缓冲采样直接光作为初始辐射度
    float3 sceneLight = gSceneColor.SampleLevel(gPointClampSampler, screenUV, 0).rgb;
    float3 radiance = sceneLight * albedo * (1.0 - metallic);

    // 保留上一帧体素辐射度 (时间累积), 避免完全覆盖
    float4 prevRadiance = gVoxelRadiance[voxelCoord];
    float temporalWeight = 0.8; // 历史权重
    float3 blendedRadiance = lerp(radiance, prevRadiance.rgb, temporalWeight);

    gVoxelRadiance[voxelCoord] = float4(blendedRadiance, 1.0);
}
