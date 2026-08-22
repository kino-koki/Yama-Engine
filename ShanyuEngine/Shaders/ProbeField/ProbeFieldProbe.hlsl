// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - ProbeField Probe Update (DDGI)
// --------------------------------------------------------------------------
// DDGI 风格辐照度探针更新: 每个探针发射若干射线, 采样命中点的辐射度,
// 累积到球谐 (SH9) 系数 (辐照度) 与深度统计 (距离场, 用于背面投影遮挡)。
// 支持时间累积平滑与软失败 (soft backface) 鲁棒性。
//
// 入口点: ProbeUpdateCS  (CS, [numthreads(32,1,1)])
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 探针更新常量缓冲
// ──────────────────────────────────────────────────────────────────────────
cbuffer ProbeUpdateConstants : register(b2)
{
    float4x4 ViewProjMatrix;
    float4x4 InvViewProj;
    float3   GridOrigin;
    float    GridExtentX;
    float3   GridExtent;          // 探针网格世界范围 (半尺寸)
    float    GridExtentY;
    float3   GridResolution;      // 每轴探针数
    float    GridExtentZ;
    uint     RayCount;            // 每探针射线数
    float    MaxRayDistance;
    float    DeltaTime;
    float    TemporalAlpha;       // 时序累积系数
    uint     FrameIndex;
    float    ProbePad0;
    float    ProbePad1;
};

// ──────────────────────────────────────────────────────────────────────────
// 探针结构 (对应 C# ProbeFieldProbe, 27 SH + 4 统计 = 31 float, 打包对齐)
// ──────────────────────────────────────────────────────────────────────────
struct ProbeFieldProbe
{
    float SH[27];     // 9 系数 x 3 通道球谐辐照度
    float MeanDepth;  // 平均深度
    float DepthVariance; // 深度方差
    uint  SampleCount;   // 累计采样数
    uint  IsActive;      // 是否激活
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float>    gDepthBuffer    : register(t0); // 屏幕深度
Texture2D<float4>   gGeometryBufferAlbedo  : register(t1); // G-Buffer 反照率
Texture2D<float4>   gGeometryBufferNormal  : register(t2); // G-Buffer 法线
Texture2D<float4>   gSceneColor     : register(t3); // 场景光照 (辐射度来源)
Texture3D<float4>   gVoxelRadiance  : register(t4); // 体素辐射度 (屏幕外回退)
Texture2D<float4>   gBlueNoise      : register(t5); // 蓝噪声 (射线方向抖动)
Texture2D<float4>   gPrevIrradiance : register(t6); // 上一帧探针辐照度图集

RWStructuredBuffer<ProbeFieldProbe> gProbeBuffer       : register(u0); // 探针数据
RWTexture2D<float4>            gProbeIrradiance   : register(u1); // 探针辐照度图集
RWTexture2D<float2>            gProbeDepth        : register(u2); // 探针深度图集

SamplerState gPointClampSampler  : register(s0);
SamplerState gLinearClampSampler : register(s2);

// ──────────────────────────────────────────────────────────────────────────
// 球谐函数 (SH9, 二阶)
// ──────────────────────────────────────────────────────────────────────────

// 评估 SH9 基函数 (对应 C# EvaluateSh2)
void EvaluateSH9(float3 d, out float sh[9])
{
    float x = d.x, y = d.y, z = d.z;
    sh[0] = 0.282095;
    sh[1] = -0.488603 * y;
    sh[2] = 0.488603 * z;
    sh[3] = -0.488603 * x;
    sh[4] = 1.092548 * x * y;
    sh[5] = -1.092548 * y * z;
    sh[6] = 0.315392 * (3.0 * z * z - 1.0);
    sh[7] = -1.092548 * x * z;
    sh[8] = 0.546274 * (x * x - y * y);
}

// 余弦卷积系数 (Ramamoorthi 2001)
static const float SH9Conv[9] = {
    0.886226925, 1.023317, 1.023317, 1.023317,
    0.247708, 0.247708, 0.247708, 0.247708, 0.247708
};

// 由 SH 系数重建某方向的辐照度
float3 EvaluateIrradianceSH(float sh[27], float3 normal)
{
    float basis[9];
    EvaluateSH9(normal, basis);

    float3 result = float3(0, 0, 0);
    [unroll]
    for (int i = 0; i < 9; i++)
    {
        result.r += sh[i * 3 + 0] * basis[i] * SH9Conv[i];
        result.g += sh[i * 3 + 1] * basis[i] * SH9Conv[i];
        result.b += sh[i * 3 + 2] * basis[i] * SH9Conv[i];
    }
    return result;
}

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 探针索引 -> 世界位置
float3 ProbeIndexToWorldPos(uint probeIndex)
{
    uint rx = (uint)GridResolution.x;
    uint ry = (uint)GridResolution.y;
    uint rz = (uint)GridResolution.z;

    uint z = probeIndex / (rx * ry);
    uint yz = probeIndex % (rx * ry);
    uint y = yz / rx;
    uint x = yz % rx;

    float3 cellSize = (GridExtent * 2.0) / (GridResolution - 1.0);
    return GridOrigin - GridExtent
         + float3(x, y, z) * cellSize;
}

// 球面均匀采样方向 (Fibonacci 球面分布)
float3 FibonacciSphereDirection(uint sampleIndex, uint sampleCount, float2 jitter)
{
    float golden = 2.39996323; // 黄金角
    float theta = golden * float(sampleIndex) + jitter.x * SHANYU_TWO_PI;
    float phi = acos(1.0 - 2.0 * (float(sampleIndex) + 0.5 + jitter.y) / float(sampleCount));
    return float3(
        sin(phi) * cos(theta),
        cos(phi),
        sin(phi) * sin(theta));
}

// 屏幕空间光线步进 (探针射线)
struct ProbeRayHit
{
    float3 radiance;
    float  distance;
    bool   hit;
};

ProbeRayHit TraceProbeRay(float3 origin, float3 dir, float maxDist)
{
    ProbeRayHit hit = (ProbeRayHit)0;
    hit.hit = false;
    hit.distance = maxDist;

    float3 pos = origin;
    float step = 0.5;

    [loop]
    for (int i = 0; i < 48; i++)
    {
        pos += dir * step;
        float dist = length(pos - origin);
        if (dist > maxDist)
        {
            hit.distance = maxDist;
            break;
        }

        // 投影到屏幕
        float4 clip = mul(ViewProjMatrix, float4(pos, 1.0));
        if (clip.w <= 0.0)
            continue;

        float2 ndc = clip.xy / clip.w;
        float2 uv = ndc * 0.5 + 0.5;
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            continue;

        uint2 screenRes;
        gDepthBuffer.GetDimensions(screenRes.x, screenRes.y);
        float sceneDepth = gDepthBuffer.SampleLevel(gPointClampSampler, uv, 0);

        // 重建命中点世界位置
        float4 depthClip = float4(ndc, sceneDepth, 1.0);
        depthClip.y = -depthClip.y;
        float4 hitWorld = mul(InvViewProj, depthClip);
        hitWorld.xyz /= hitWorld.w;

        // 深度测试: 射线是否在表面后方
        float rayViewDepth = mul(ViewProjMatrix, float4(pos, 1.0)).z;
        float sceneViewDepth = mul(ViewProjMatrix, float4(hitWorld.xyz, 1.0)).z;

        if (rayViewDepth > sceneViewDepth && abs(rayViewDepth - sceneViewDepth) < step * 2.0)
        {
            // 命中
            hit.hit = true;
            hit.distance = dist;

            // 采样命中点辐射度
            uint2 pixel = uint2(uv * screenRes);
            float3 albedo = gGeometryBufferAlbedo[pixel].rgb;
            float3 sceneLight = gSceneColor.SampleLevel(gPointClampSampler, uv, 0).rgb;
            hit.radiance = sceneLight * albedo;
            return hit;
        }

        step *= 1.1;
    }

    // 未命中: 天空光
    float skyFactor = saturate_fast(dir.y * 0.5 + 0.5);
    hit.radiance = float3(0.4, 0.6, 1.0) * skyFactor * 0.3;
    return hit;
}

// ─────────────────────────────────────────────────═════════════════════════
// 探针更新主着色器
// 每线程组处理一个探针, 组内线程协作发射射线
// ──────────────────────────────────────────────────────────────────────────
[numthreads(32, 1, 1)]
void ProbeUpdateCS(uint3 groupID : SV_GroupID, uint3 dispatchID : SV_DispatchThreadID,
                   uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupID.x;
    if (probeIndex == 0)
        return; // 占位防零

    ProbeFieldProbe probe = gProbeBuffer[probeIndex];
    if (probe.IsActive == 0)
        return;

    float3 probePos = ProbeIndexToWorldPos(probeIndex);

    // 蓝噪声抖动
    uint noiseW, noiseH;
    gBlueNoise.GetDimensions(noiseW, noiseH);
    uint noiseIndex = (FrameIndex + probeIndex) & 255;
    float2 jitter = gBlueNoise.Load(int3(noiseIndex % noiseW, (noiseIndex / noiseW) % noiseH, 0)).xy;

    // 每线程发射一条射线 (32 线程, 循环覆盖 RayCount)
    float3 newSH[27];
    [unroll]
    for (int init = 0; init < 27; init++)
        newSH[init] = 0.0;

    float depthAccum = 0.0;
    float depthSqAccum = 0.0;
    uint validRays = 0;

    // 每线程处理多条射线 (RayCount 可能 > 32)
    uint raysPerThread = max(1, (RayCount + 31) / 32);

    [loop]
    for (uint r = 0; r < raysPerThread; r++)
    {
        uint rayIndex = groupIndex + r * 32;
        if (rayIndex >= RayCount)
            break;

        // 球面方向
        float3 dir = FibonacciSphereDirection(rayIndex, RayCount, jitter);

        // 射线追踪
        ProbeRayHit hit = TraceProbeRay(probePos, dir, MaxRayDistance);

        // 累加 SH 系数
        float sh[9];
        EvaluateSH9(dir, sh);

        [unroll]
        for (int s = 0; s < 9; s++)
        {
            newSH[s * 3 + 0] += sh[s] * hit.radiance.r;
            newSH[s * 3 + 1] += sh[s] * hit.radiance.g;
            newSH[s * 3 + 2] += sh[s] * hit.radiance.b;
        }

        // 深度统计 (仅命中几何的射线)
        if (hit.hit)
        {
            depthAccum += hit.distance;
            depthSqAccum += hit.distance * hit.distance;
            validRays++;
        }
    }

    // 归一化 SH 系数 (4*PI 球面积分)
    float normFactor = 4.0 * SHANYU_PI / float(RayCount);
    [unroll]
    for (int n = 0; n < 27; n++)
        newSH[n] *= normFactor;

    // 深度统计
    float meanDepth = validRays > 0 ? depthAccum / float(validRays) : MaxRayDistance;
    float depthVariance = validRays > 0
        ? max(0.0, depthSqAccum / float(validRays) - meanDepth * meanDepth)
        : 0.0;

    // 时序累积: 与上一帧探针数据混合
    float alpha = saturate_fast(DeltaTime * 10.0);
    alpha = lerp(1.0 - TemporalAlpha, 1.0, alpha); // 历史权重

    [unroll]
    for (int m = 0; m < 27; m++)
    {
        probe.SH[m] = lerp(probe.SH[m], newSH[m], 1.0 - alpha);
    }

    probe.MeanDepth = lerp(probe.MeanDepth, meanDepth, 1.0 - alpha);
    probe.DepthVariance = lerp(probe.DepthVariance, depthVariance, 1.0 - alpha);
    probe.SampleCount += RayCount;

    // 写回探针缓冲
    gProbeBuffer[probeIndex] = probe;

    // 写入辐照度图集 (每探针 6x6 块, thread0 写代表图素)
    if (groupIndex == 0)
    {
        uint rx = (uint)GridResolution.x;
        uint ry = (uint)GridResolution.y;
        uint rz = (uint)GridResolution.z;
        uint z = probeIndex / (rx * ry);
        uint yz = probeIndex % (rx * ry);
        uint y = yz / rx;
        uint x = yz % rx;

        uint2 atlasPos = uint2(x, y + z * ry) * 6;
        float3 upNormal = float3(0, 1, 0);

        // 用 SH0 还原一个代表辐照度写入图集
        float3 irradiance = EvaluateIrradianceSH(probe.SH, upNormal);

        // 写 6x6 块
        [loop]
        for (int dy = 0; dy < 6; dy++)
        {
            [loop]
            for (int dx = 0; dx < 6; dx++)
            {
                // 根据图素在块内的位置计算方向 (简化)
                float2 localUV = (float2(dx, dy) + 0.5) / 6.0;
                float3 dir = normalize(float3(localUV.x * 2 - 1, localUV.y * 2 - 1, 0.5));
                float3 irr = EvaluateIrradianceSH(probe.SH, dir);
                gProbeIrradiance[atlasPos + uint2(dx, dy)] = float4(irr, 1.0);
            }
        }

        gProbeDepth[atlasPos] = float2(meanDepth, depthVariance);
    }
}
