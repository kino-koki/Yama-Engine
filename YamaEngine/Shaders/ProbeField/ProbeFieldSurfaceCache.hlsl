// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - ProbeField Surface Cache
// --------------------------------------------------------------------------
// 表面缓存系统: 将场景网格表面参数化到 2D 图集 (Atlas), 缓存每个表面片的
// 反照率、法线、深度, 并在辐照度 pass 中计算直接光 + 间接光辐照度。
// 用于近场多跳 GI 与动态物体支持。
//
// 两个计算入口:
//   SurfaceRasterCS      - 将网格体投影到图集, 写入反照率/法线/深度图集
//   SurfaceIrradianceCS  - 对图集每图素计算直接+间接辐照度 (多跳累积)
//
// 入口点: SurfaceRasterCS, SurfaceIrradianceCS  (CS)
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 表面缓存常量缓冲
// ──────────────────────────────────────────────────────────────────────────
cbuffer SurfaceCacheConstants : register(b2)
{
    float4x4 ViewMatrix;
    float4x4 ProjMatrix;
    float4x4 InvViewProj;
    float3   CameraPos;
    float    AtlasResolution;     // 图集分辨率
    int      MaxPages;            // 最大页数
    int      BounceIndex;         // 当前多跳索引
    float    DeltaTime;
    float3   SkyLightColor;
    float    SkyIntensity;
    float3   GridOrigin;
    float    GridExtent;          // 体素场景范围 (用于查询体素辐照度)
    float    IndirectIntensity;
    int      PageTileSize;        // 每页图素大小 (如 16)
    float    SCpad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 表面缓存页结构 (对应 C# 32 字节页描述)
// ──────────────────────────────────────────────────────────────────────────
struct SurfaceCachePage
{
    float4x4 WorldToAtlas;        // 世界 -> 图集 UV 变换 (4x4 = 64 字节, 实际打包)
};

// 简化页结构 (32 字节: 4 个 float)
struct PageDesc
{
    float3 pageWorldPos;          // 页代表的世界位置
    float  pageSize;              // 页覆盖范围
    float2 atlasOffset;           // 图集 UV 偏移 (像素)
    float2 atlasScale;            // 图集 UV 缩放
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
StructuredBuffer<PageDesc>  gPageBuffer       : register(t0); // 页分配表
Texture2D<float4>           gGeometryBufferAlbedo    : register(t1); // 屏幕空间 G-Buffer 反照率
Texture2D<float4>           gGeometryBufferNormal    : register(t2); // 屏幕空间 G-Buffer 法线
Texture2D<float>            gDepthBuffer      : register(t3); // 屏幕深度
Texture2D<float4>           gSceneColor       : register(t4); // 直接光照结果
Texture2D<float4>           gDirectLighting   : register(t5); // 直接光缓冲
Texture3D<float4>           gVoxelRadiance    : register(t6); // 体素辐射度 (锥体追踪)
Texture2D<float4>           gProbeIrradiance  : register(t7); // 探针辐照度图集

// 历史 radiosity 通过 root table 重新绑定到相同图集资源 (乒乓),
// 多跳采样复用 gRadiosityAtlas 上一帧内容

RWTexture2D<float4> gAlbedoAtlas     : register(u0); // 反照率图集
RWTexture2D<float4> gNormalAtlas     : register(u1); // 法线图集
RWTexture2D<float>  gDepthAtlas      : register(u2); // 深度图集
RWTexture2D<float4> gRadiosityAtlas  : register(u3); // 辐照度图集 (输出)

SamplerState gLinearClampSampler : register(s2);
SamplerState gPointClampSampler  : register(s0);
SamplerState gLinearWrapSampler  : register(s3);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 体素锥体追踪: 沿方向步进采样体素辐射度 (用于间接光)
float3 ConeTraceVoxels(float3 origin, float3 direction, float tanHalfAngle, float maxDist)
{
    float3 accum = float3(0, 0, 0);
    float weight = 0.0;
    float voxelSize = (GridExtent * 2.0) / 128.0;
    float dist = voxelSize * 0.5;

    [loop]
    while (dist < maxDist && weight < 1.0)
    {
        float radius = dist * tanHalfAngle;
        float lod = max(0.0, log2(radius / voxelSize));
        float3 samplePos = (origin + direction * dist - GridOrigin + GridExtent) / (GridExtent * 2.0);

        // 范围检查
        if (any(samplePos < 0.0) || any(samplePos > 1.0))
            break;

        float4 voxelSample = gVoxelRadiance.SampleLevel(gLinearClampSampler, samplePos, lod);
        float3 radiance = voxelSample.rgb;
        float alpha = voxelSample.a;

        // 前向累积
        float w = (1.0 - weight) * alpha;
        accum += radiance * w;
        weight += w;

        dist += radius * 1.5;
    }

    return accum;
}

// 从屏幕 G-Buffer 采样表面属性
struct SurfaceData
{
    float3 albedo;
    float  roughness;
    float3 normal;
    float  metallic;
    float3 worldPos;
    bool   valid;
};

SurfaceData SampleScreenSurface(uint2 pixel)
{
    SurfaceData s = (SurfaceData)0;
    s.valid = false;

    float depth = gDepthBuffer[pixel];
    if (depth <= 0.0)
        return s;

    uint2 res;
    gDepthBuffer.GetDimensions(res.x, res.y);
    float2 uv = (pixel + 0.5) / float2(res);

    float4 albedoRough = gGeometryBufferAlbedo[pixel];
    float4 normalMetal = gGeometryBufferNormal[pixel];

    s.albedo = albedoRough.rgb;
    s.roughness = albedoRough.a;
    s.normal = DecodeNormalOctahedral(normalMetal.xy);
    s.metallic = normalMetal.z;
    s.worldPos = ReconstructWorldPos(uv, depth);
    s.valid = true;
    return s;
}

// 探针辐照度查询 (简化: 直接采样探针图集)
float3 SampleProbeIrradiance(float3 worldPos, float3 normal)
{
    // 将世界位置映射到探针图集 UV (简化映射)
    float2 probeUV = worldPos.xz * 0.01 + 0.5;
    probeUV = saturate_fast(probeUV);
    float4 irr = gProbeIrradiance.SampleLevel(gLinearClampSampler, probeUV, 0);
    // 法线调制
    float nDotUp = max(0.0, normal.y);
    return irr.rgb * (nDotUp * 0.5 + 0.5);
}

// ─────────────────────────────────══════════════════════════════════════─
// Pass 1: SurfaceRasterCS - 将网格体投影到图集
// 把屏幕可见表面光栅化到表面缓存图集, 捕获反照率/法线/深度
// [numthreads(8,8,1)] - 每线程处理一个图集图素
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void SurfaceRasterCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 atlasPixel = dispatchID.xy;
    uint atlasRes = (uint)AtlasResolution;

    if (atlasPixel.x >= atlasRes || atlasPixel.y >= atlasRes)
        return;

    // 计算当前图素属于哪个页
    uint tileShift = (uint)log2((float)PageTileSize);
    uint2 tileCoord = atlasPixel >> tileShift;
    uint tilesPerAxis = atlasRes >> tileShift;
    uint pageIndex = tileCoord.x + tileCoord.y * tilesPerAxis;

    if (pageIndex >= (uint)MaxPages)
        return;

    PageDesc page = gPageBuffer[pageIndex];

    // 将图集图素映射到屏幕空间 (查找覆盖该页代表位置的屏幕像素)
    // 简化: 直接从世界位置投影
    float3 pageWorldPos = page.pageWorldPos;
    float4 clipPos = mul(ProjMatrix, mul(ViewMatrix, float4(pageWorldPos, 1.0)));
    if (clipPos.w <= 0.0)
    {
        gAlbedoAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gNormalAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gDepthAtlas[atlasPixel] = 0.0;
        return;
    }

    float2 screenUV = clipPos.xy / clipPos.w;
    screenUV = screenUV * 0.5 + 0.5;

    uint2 screenRes;
    gDepthBuffer.GetDimensions(screenRes.x, screenRes.y);
    uint2 screenPixel = uint2(screenUV * screenRes);

    if (screenPixel.x >= screenRes.x || screenPixel.y >= screenRes.y)
    {
        gAlbedoAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gNormalAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gDepthAtlas[atlasPixel] = 0.0;
        return;
    }

    // 采样屏幕 G-Buffer
    SurfaceData surface = SampleScreenSurface(screenPixel);
    if (!surface.valid)
    {
        gAlbedoAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gNormalAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gDepthAtlas[atlasPixel] = 0.0;
        return;
    }

    // 深度一致性检查: 确保屏幕像素对应的是页代表的表面
    float pageDepth = length(pageWorldPos - CameraPos);
    float surfaceDepth = length(surface.worldPos - CameraPos);
    if (abs(pageDepth - surfaceDepth) > page.pageSize * 2.0)
    {
        gAlbedoAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gNormalAtlas[atlasPixel] = float4(0, 0, 0, 0);
        gDepthAtlas[atlasPixel] = 0.0;
        return;
    }

    // 写入图集
    gAlbedoAtlas[atlasPixel] = float4(surface.albedo, surface.roughness);
    gNormalAtlas[atlasPixel] = float4(EncodeNormalOctahedral(surface.normal), surface.metallic, 1.0);
    gDepthAtlas[atlasPixel] = surfaceDepth;
}

// ─────────────────────────────────────────────────═════════════════════════
// Pass 2: SurfaceIrradianceCS - 计算直接 + 间接辐照度
// 对每个图集图素查询直接光、探针辐照度、体素锥体追踪, 累积多跳间接光
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void SurfaceIrradianceCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 atlasPixel = dispatchID.xy;
    uint atlasRes = (uint)AtlasResolution;

    if (atlasPixel.x >= atlasRes || atlasPixel.y >= atlasRes)
        return;

    // 读取图集表面属性
    float4 albedoRough = gAlbedoAtlas[atlasPixel];
    float4 normalMetal = gNormalAtlas[atlasPixel];
    float  surfaceDepth = gDepthAtlas[atlasPixel];

    // 无效图素 (未光栅化)
    if (surfaceDepth <= 0.0 || albedoRough.a <= 0.0)
    {
        gRadiosityAtlas[atlasPixel] = float4(0, 0, 0, 0);
        return;
    }

    float3 albedo = albedoRough.rgb;
    float  roughness = albedoRough.a;
    float3 normal = DecodeNormalOctahedral(normalMetal.xy);
    float  metallic = normalMetal.z;

    // 重建世界位置 (从页信息)
    uint tileShift = (uint)log2((float)PageTileSize);
    uint2 tileCoord = atlasPixel >> tileShift;
    uint tilesPerAxis = atlasRes >> tileShift;
    uint pageIndex = tileCoord.x + tileCoord.y * tilesPerAxis;
    if (pageIndex >= (uint)MaxPages)
    {
        gRadiosityAtlas[atlasPixel] = float4(0, 0, 0, 0);
        return;
    }
    PageDesc page = gPageBuffer[pageIndex];
    float3 worldPos = page.pageWorldPos;

    // ── 1. 直接光照 (从屏幕空间直接光缓冲采样) ──
    float4 clipPos = mul(ProjMatrix, mul(ViewMatrix, float4(worldPos, 1.0)));
    float2 screenUV = (clipPos.xy / clipPos.w) * 0.5 + 0.5;
    screenUV = saturate_fast(screenUV);

    float3 directLighting = float3(0, 0, 0);
    if (all(screenUV > 0.0) && all(screenUV < 1.0))
    {
        directLighting = gDirectLighting.SampleLevel(gPointClampSampler, screenUV, 0).rgb;
    }

    // ── 2. 间接光照 ──
    float3 indirectLighting = float3(0, 0, 0);

    // 2a. 探针辐照度 (漫反射低频)
    float3 probeIrr = SampleProbeIrradiance(worldPos, normal);
    indirectLighting += probeIrr;

    // 2b. 体素锥体追踪 (镜面/中频间接光)
    float3 viewDir = normalize(CameraPos - worldPos);
    float3 reflectDir = reflect(-viewDir, normal);
    float coneAngle = tan(roughness * roughness * 0.5 + 0.05);
    float3 voxelIrr = ConeTraceVoxels(worldPos + normal * 0.1, reflectDir, coneAngle, GridExtent);
    indirectLighting += voxelIrr * (1.0 - metallic * 0.5);

    // 2c. 多跳: 从历史 radiosity 采样上一跳的辐照度
    if (BounceIndex > 0)
    {
        // 历史缓冲已通过 root table 绑定 (此处简化采样自身)
        float3 bounceIrr = gRadiosityAtlas[atlasPixel].rgb;
        indirectLighting += bounceIrr * 0.5;
    }

    // ── 3. 天空光 ──
    float skyAccess = saturate_fast(normal.y * 0.5 + 0.5);
    float3 skyLight = SkyLightColor * SkyIntensity * skyAccess * 0.3;
    indirectLighting += skyLight;

    // ── 4. 合成辐照度 ──
    float3 diffuseColor = albedo * (1.0 - metallic);
    float3 totalIrradiance = (directLighting + indirectLighting * IndirectIntensity) * diffuseColor * SHANYU_INV_PI;

    // 时序累积 (多跳平滑)
    float3 prevRadiosity = gRadiosityAtlas[atlasPixel].rgb;
    float blendFactor = saturate_fast(DeltaTime * 8.0);
    totalIrradiance = lerp(prevRadiosity, totalIrradiance, blendFactor);

    gRadiosityAtlas[atlasPixel] = float4(totalIrradiance, 1.0);
}
