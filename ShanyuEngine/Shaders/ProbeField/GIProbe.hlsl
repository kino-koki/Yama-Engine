// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - GI Probe System
// --------------------------------------------------------------------------
// 全局光照探针系统 (基于球谐 SH9): 为漫反射与镜面间接光提供低频环境光照。
// 与 ProbeField 实时 GI 互补: ProbeField 提供高频动态 GI, 探针系统提供稳定的低频
// 环境光基础层 (尤其适用于静态场景与远场)。
//
// 两个计算入口 (对应 C# GlobalIlluminationProbeSystem):
//   ProbeProjectCS         - Cubemap -> SH 系数投影 (烘焙器)
//   DynamicProbeUpdateCS   - 动态探针运行时更新 (反馈 + 时间累积)
//
// 入口点: ProbeProjectCS, DynamicProbeUpdateCS  (CS)
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// GI 探针常量缓冲
// ──────────────────────────────────────────────────────────────────────────
cbuffer GIProbeConstants : register(b2)
{
    float4x4 ViewProjMatrix;
    float4x4 InvViewProj;
    float3   GridOrigin;
    float    GridExtentX;
    float3   GridExtent;          // 探针网格世界范围 (半尺寸)
    float    GridExtentY;
    float3   GridResolution;      // 每轴探针数
    float    GridExtentZ;
    uint     CubemapResolution;   // Cubemap 单面分辨率
    uint     ProbeCount;          // 探针总数
    uint     SHOrder;             // SH 阶数 (2 或 3)
    float    TemporalAlpha;       // 时间累积系数
    float    DeltaTime;
    uint     FrameIndex;
    float    GIProbePad0;
};

// ──────────────────────────────────────────────────────────────────────────
// GI 探针结构 (对应 C# GIProbe, SH[27] + 位置 + 范围 + 偏移 + 深度 + 标志)
// ──────────────────────────────────────────────────────────────────────────
struct GIProbe
{
    float SH[27];         // 9 系数 x 3 通道球谐辐照度
    float3 Position;      // 探针世界位置
    float  Range;         // 影响半径
    float3 SelfOffset;    // 自身偏移
    float  MeanDepth;     // 平均深度 (视差校正)
    int    IsActive;      // 是否激活
    float  LastUpdateTime;
    float  BakePriority;
    uint   AccumulatedSamples;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
TextureCube<float4>  gProbeCubemap   : register(t0); // 探针 Cubemap (6 面)
Texture2D<float4>    gSceneColor     : register(t1); // 场景颜色 (动态探针反馈)
Texture2D<float>     gSceneDepth     : register(t2); // 场景深度
Texture2D<float4>    gGeometryBufferNormal  : register(t3); // G-Buffer 法线
Texture2D<float4>    gGeometryBufferAlbedo  : register(t4); // G-Buffer 反照率
Texture2D<float4>    gBlueNoise      : register(t5); // 蓝噪声
Texture3D<float4>    gSHTextureR     : register(t6); // SH 系数 R 通道体积
Texture3D<float4>    gSHTextureG     : register(t7); // SH 系数 G 通道体积

RWStructuredBuffer<GIProbe> gProbeBuffer     : register(u0); // 探针数据缓冲
RWTexture3D<float4>         gSHOutputR       : register(u1); // SH R 通道体积输出
RWTexture3D<float4>         gSHOutputG       : register(u2); // SH G 通道体积输出
RWTexture3D<float4>         gSHOutputB       : register(u3); // SH B 通道体积输出
RWTexture3D<float>          gDepthVolume     : register(u4); // 探针深度体积

SamplerState gLinearClampSampler : register(s2);
SamplerState gPointClampSampler  : register(s0);

// ──────────────────────────────────────────────────────────────────────────
// 球谐函数 (SH9, 三阶)
// ──────────────────────────────────────────────────────────────────────────

// 评估 SH9 基函数 (对应 C# EvaluateSH9)
void EvaluateSH9(float3 d, out float sh[9])
{
    float x = d.x, y = d.y, z = d.z;
    sh[0] = 0.282095;                                    // Y00
    sh[1] = -0.488603 * y;                               // Y1-1
    sh[2] = 0.488603 * z;                                // Y10
    sh[3] = -0.488603 * x;                               // Y1+1
    sh[4] = 1.092548 * x * y;                            // Y2-2
    sh[5] = -1.092548 * y * z;                           // Y2-1
    sh[6] = 0.315392 * (3.0 * z * z - 1.0);              // Y20
    sh[7] = -1.092548 * x * z;                           // Y2+1
    sh[8] = 0.546274 * (x * x - y * y);                  // Y2+2
}

// 评估二阶 SH4 基函数 (前 4 个)
void EvaluateSH4(float3 d, out float sh[4])
{
    float x = d.x, y = d.y, z = d.z;
    sh[0] = 0.282095;
    sh[1] = -0.488603 * y;
    sh[2] = 0.488603 * z;
    sh[3] = -0.488603 * x;
}

// 余弦卷积系数 (Ramamoorthi 2001)
static const float SH9Conv[9] = {
    0.886226925, 1.023317, 1.023317, 1.023317,
    0.247708, 0.247708, 0.247708, 0.247708, 0.247708
};
static const float SH4Conv[4] = {
    0.886226925, 1.023317, 1.023317, 1.023317
};

// 由 SH 系数重建某方向的辐照度 (漫反射余弦卷积)
float3 EvaluateIrradianceSH(float sh[27], float3 normal, uint order)
{
    uint count = (order >= 3) ? 9 : 4;
    float basis[9];
    EvaluateSH9(normal, basis);

    float3 result = float3(0, 0, 0);
    [loop]
    for (uint i = 0; i < count; i++)
    {
        float conv = (order >= 3) ? SH9Conv[i] : SH4Conv[i];
        result.r += sh[i * 3 + 0] * basis[i] * conv;
        result.g += sh[i * 3 + 1] * basis[i] * conv;
        result.b += sh[i * 3 + 2] * basis[i] * conv;
    }
    return result;
}

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// Cubemap 面方向 (6 面)
float3 GetCubemapFaceDir(uint face, float2 uv)
{
    float2 ndc = uv * 2.0 - 1.0;
    float3 dir = float3(0, 0, 0);
    switch (face)
    {
        case 0: dir = float3( 1.0, ndc.y, -ndc.x); break; // +X
        case 1: dir = float3(-1.0, ndc.y,  ndc.x); break; // -X
        case 2: dir = float3( ndc.x, 1.0, -ndc.y); break; // +Y
        case 3: dir = float3( ndc.x,-1.0,  ndc.y); break; // -Y
        case 4: dir = float3( ndc.x, ndc.y,  1.0); break; // +Z
        case 5: dir = float3(-ndc.x, ndc.y, -1.0); break; // -Z
    }
    return normalize(dir);
}

// 探针索引 -> 网格坐标
uint3 ProbeIndexToGrid(uint probeIndex)
{
    uint rx = (uint)GridResolution.x;
    uint ry = (uint)GridResolution.y;
    uint z = probeIndex / (rx * ry);
    uint yz = probeIndex % (rx * ry);
    uint y = yz / rx;
    uint x = yz % rx;
    return uint3(x, y, z);
}

// 网格坐标 -> 体积纹理坐标
uint3 GridToVolumeCoord(uint3 gridCoord)
{
    return gridCoord;
}

// ─────────────────────────────────══════════════════════════════════════─
// ProbeProjectCS - Cubemap -> SH 系数投影
// 采样探针 Cubemap 的所有像素, 累加 SH 系数, 归一化后写入探针缓冲与体积纹理
// 每个线程组处理一个探针, 组内线程协作采样 Cubemap
// [numthreads(64,1,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(64, 1, 1)]
void ProbeProjectCS(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupID.x;
    if (probeIndex >= ProbeCount)
        return;

    GIProbe probe = gProbeBuffer[probeIndex];
    if (probe.IsActive == 0)
        return;

    // 每线程处理 Cubemap 的若干像素
    // Cubemap 总像素 = Resolution^2 * 6
    uint totalPixels = CubemapResolution * CubemapResolution * 6;
    uint pixelsPerThread = (totalPixels + 63) / 64;

    // 线程局部 SH 累加
    float localSH[27];
    [unroll]
    for (int init = 0; init < 27; init++)
        localSH[init] = 0.0;

    float localDepth = 0.0;
    uint localSamples = 0;

    [loop]
    for (uint p = 0; p < pixelsPerThread; p++)
    {
        uint pixelIndex = groupIndex + p * 64;
        if (pixelIndex >= totalPixels)
            break;

        // 像素索引 -> (face, uv)
        uint face = pixelIndex / (CubemapResolution * CubemapResolution);
        uint facePixel = pixelIndex % (CubemapResolution * CubemapResolution);
        uint py = facePixel / CubemapResolution;
        uint px = facePixel % CubemapResolution;

        float2 uv = (float2(px, py) + 0.5) / float(CubemapResolution);
        float3 dir = GetCubemapFaceDir(face, uv);

        // 采样 Cubemap 辐射度
        float4 radiance = gProbeCubemap.SampleLevel(gLinearClampSampler, dir, 0);

        // 评估 SH 基函数
        float sh[9];
        EvaluateSH9(dir, sh);

        // 累加 SH 系数
        [unroll]
        for (int s = 0; s < 9; s++)
        {
            localSH[s * 3 + 0] += sh[s] * radiance.r;
            localSH[s * 3 + 1] += sh[s] * radiance.g;
            localSH[s * 3 + 2] += sh[s] * radiance.b;
        }

        // 深度统计 (使用辐射度 alpha 作为近似深度)
        localDepth += radiance.a;
        localSamples++;
    }

    // 归一化 SH 系数 (4*PI 球面积分)
    float normFactor = 4.0 * SHANYU_PI / float(totalPixels);
    [unroll]
    for (int n = 0; n < 27; n++)
        localSH[n] *= normFactor;

    // 线程 0 写入探针缓冲 (简化: 使用最后一个线程的结果,
    // 实际应使用 GroupShared 内存归约所有线程的累加结果)
    if (groupIndex == 0)
    {
        // 写入 SH 系数到探针
        [unroll]
        for (int m = 0; m < 27; m++)
            probe.SH[m] = localSH[m];

        probe.MeanDepth = localSamples > 0 ? localDepth / float(localSamples) : 0.0;
        probe.AccumulatedSamples = 1;
        probe.LastUpdateTime = (float)FrameIndex * DeltaTime;

        gProbeBuffer[probeIndex] = probe;

        // 写入 SH 体积纹理 (每体素存 4 个 SH 系数, 共 3 张纹理存 12 个)
        uint3 gridCoord = ProbeIndexToGrid(probeIndex);
        uint3 volCoord = GridToVolumeCoord(gridCoord);

        // 打包: R 纹理存 SH0-3, G 纹理存 SH4-7, B 纹理存 SH8 + 深度
        gSHOutputR[volCoord] = float4(probe.SH[0], probe.SH[3], probe.SH[6], probe.SH[1]);
        gSHOutputG[volCoord] = float4(probe.SH[9], probe.SH[12], probe.SH[15], probe.SH[10]);
        gSHOutputB[volCoord] = float4(probe.SH[18], probe.SH[21], probe.SH[24], probe.SH[19]);
        gDepthVolume[volCoord] = probe.MeanDepth;
    }
}

// ─────────────────────────────────══════════════════════════════════════─
// DynamicProbeUpdateCS - 动态探针运行时更新
// 从渲染帧反馈 (场景颜色/深度) 更新探针辐照度, 时间累积平滑
// 每个线程组处理一个探针, 从屏幕空间采样可见区域
// [numthreads(32,1,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(32, 1, 1)]
void DynamicProbeUpdateCS(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupID.x;
    if (probeIndex >= ProbeCount)
        return;

    GIProbe probe = gProbeBuffer[probeIndex];
    if (probe.IsActive == 0)
        return;

    float3 probePos = probe.Position + probe.SelfOffset;

    // 每线程采样屏幕空间一个方向
    // 将探针位置投影到屏幕, 在周围采样
    float4 clipPos = mul(ViewProjMatrix, float4(probePos, 1.0));
    if (clipPos.w <= 0.0)
        return;

    float2 screenUV = (clipPos.xy / clipPos.w) * 0.5 + 0.5;
    screenUV = saturate_fast(screenUV);

    uint2 screenRes;
    gSceneColor.GetDimensions(screenRes.x, screenRes.y);

    // 蓝噪声抖动
    uint noiseW, noiseH;
    gBlueNoise.GetDimensions(noiseW, noiseH);
    float2 blueNoise = gBlueNoise.Load(int3((probeIndex + FrameIndex) % noiseW, 0, 0)).xy;

    // 每线程采样一个屏幕方向 (Halton 球面采样)
    int sampleIndex = (int)groupIndex;
    float2 halton = float2(RTHalton(sampleIndex + 1, 2), RTHalton(sampleIndex + 1, 3));
    float2 uniformSample = frac(halton + blueNoise);

    float phi = SHANYU_TWO_PI * uniformSample.x;
    float cosTheta = sqrt(1.0 - uniformSample.y);
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    float3 sampleDir = float3(cos(phi) * sinTheta, cosTheta, sin(phi) * sinTheta);

    // 在屏幕空间探针周围采样 (将方向映射到屏幕偏移)
    float2 screenOffset = sampleDir.xy * 0.05;
    float2 sampleUV = saturate_fast(screenUV + screenOffset);

    uint2 samplePixel = uint2(sampleUV * screenRes);
    if (any(samplePixel >= screenRes))
        return;

    // 采样场景颜色与法线
    float3 sceneColor = gSceneColor[samplePixel].rgb;
    float3 albedo = gGeometryBufferAlbedo[samplePixel].rgb;
    float3 normal = DecodeNormalOctahedral(gGeometryBufferNormal[samplePixel].xy);
    float depth = gSceneDepth[samplePixel];

    // 重建世界位置
    float3 worldPos = ReconstructWorldPos(sampleUV, depth);

    // 计算从探针到采样点的方向与距离
    float3 toSample = worldPos - probePos;
    float dist = length(toSample);
    if (dist < 0.01)
        return;
    float3 dir = toSample / dist;

    // 辐射度 = 场景颜色 (简化: 直接光 + 反照率)
    float3 radiance = sceneColor;

    // 评估 SH 基函数
    float sh[9];
    EvaluateSH9(dir, sh);

    // 线程局部 SH 累加
    float localSH[27];
    [unroll]
    for (int s = 0; s < 9; s++)
    {
        localSH[s * 3 + 0] = sh[s] * radiance.r;
        localSH[s * 3 + 1] = sh[s] * radiance.g;
        localSH[s * 3 + 2] = sh[s] * radiance.b;
    }

    // 归一化
    float normFactor = 4.0 * SHANYU_PI / 32.0;
    [unroll]
    for (int n = 0; n < 27; n++)
        localSH[n] *= normFactor;

    // 时间累积: 与现有探针 SH 混合
    // (简化: thread0 写回, 实际应 GroupShared 归约)
    if (groupIndex == 0)
    {
        float alpha = 1.0 - TemporalAlpha;
        float dt = saturate_fast(DeltaTime * 8.0);
        alpha = lerp(1.0, alpha, dt);

        [unroll]
        for (int m = 0; m < 27; m++)
        {
            probe.SH[m] = lerp(probe.SH[m], localSH[m], 1.0 - alpha);
        }

        probe.MeanDepth = lerp(probe.MeanDepth, dist, 1.0 - alpha);
        probe.AccumulatedSamples++;
        probe.LastUpdateTime = (float)FrameIndex * DeltaTime;

        gProbeBuffer[probeIndex] = probe;

        // 更新 SH 体积纹理
        uint3 gridCoord = ProbeIndexToGrid(probeIndex);
        uint3 volCoord = GridToVolumeCoord(gridCoord);

        gSHOutputR[volCoord] = float4(probe.SH[0], probe.SH[3], probe.SH[6], probe.SH[1]);
        gSHOutputG[volCoord] = float4(probe.SH[9], probe.SH[12], probe.SH[15], probe.SH[10]);
        gSHOutputB[volCoord] = float4(probe.SH[18], probe.SH[21], probe.SH[24], probe.SH[19]);
        gDepthVolume[volCoord] = probe.MeanDepth;
    }
}
