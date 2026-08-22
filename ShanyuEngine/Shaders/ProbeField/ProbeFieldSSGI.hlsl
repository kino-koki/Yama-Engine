// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - ProbeField SSGI (Screen Space Global Illumination)
// --------------------------------------------------------------------------
// 屏幕空间全局光照: 沿反射/漫反射方向在屏幕空间进行 Hi-Z 引导的光线步进,
// 采样命中点的间接辐照度 (来自上一帧光照缓冲), 输出每像素漫反射间接光。
// 作为 ProbeField 多层 GI 架构的高频细节基础层。
//
// 算法:
//   1. 从 G-Buffer 重建世界位置 / 法线 / 反照率 / 粗糙度
//   2. 基于余弦加权半球采样生成采样方向 (Halton + 蓝噪声抖动)
//   3. 在屏幕空间光线步进, 使用 Hi-Z 深度金字塔加速命中测试
//   4. 命中后采样上一帧光照缓冲获得辐照度, 应用距离 / 深度 / 法线可信度衰减
//   5. 累加多采样结果并归一化, 输出辐照度与射线命中置信度
//
// 入口点: SSGICS  (CS, [numthreads(8,8,1)])
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// SSGI 常量缓冲 (对应 C# ProbeFieldSSGICB, 绑定到 b2 避免与全局 b0 冲突)
// ──────────────────────────────────────────────────────────────────────────
cbuffer SSGIConstants : register(b2)
{
    float4x4 ViewProj;
    float4x4 InvViewProj;
    float4x4 PrevViewProj;
    float3   CameraPos;
    float    RayStep;
    float2   InvResolution;
    float    MaxRayLength;
    int      SampleCount;
    float    TemporalAlpha;
    int      FrameIndex;
    float2   Jitter;
    float    SSGIPad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定 (SRV: t0-t7, UAV: u0-u7, 与 C# 根签名描述表一致)
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4>  gGeometryBufferAlbedo   : register(t0); // 反照率 + 粗糙度
Texture2D<float4>  gGeometryBufferNormal   : register(t1); // 法线(oct) + 金属度
Texture2D<float>   gDepthBuffer     : register(t2); // 场景深度 (NDC)
Texture2D<float>   gHiZDepthMips    : register(t3); // Hi-Z 深度金字塔
Texture2D<float4>  gPrevSceneColor  : register(t4); // 上一帧光照缓冲 (辐照度来源)
Texture2D<float2>  gMotionVectors   : register(t5); // 运动向量 (重投影)
Texture2D<float4>  gPrevSSGI        : register(t6); // 上一帧 SSGI (时序)
Texture2D<float4>  gBlueNoise       : register(t7); // 蓝噪声纹理

RWTexture2D<float4> gSSGIOutput     : register(u0); // 输出: RGB=辐照度, A=置信度
RWTexture2D<float2> gSSGIVelocity   : register(u1); // 输出: 重投影运动向量

SamplerState gLinearClampSampler : register(s2);
SamplerState gPointClampSampler  : register(s0);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 读取 Hi-Z 深度金字塔某一 mip
float SampleHiZDepth(float2 uv, int mipLevel)
{
    uint mipCount = 0;
    uint width, height;
    gHiZDepthMips.GetDimensions(width, height);
    // 计算可用 mip 数 (近似)
    mipCount = (int)log2(max(width, height)) + 1;
    int mip = clamp(mipLevel, 0, max(0, mipCount - 1));
    return gHiZDepthMips.Load(int3(uv * float2(width, height), mip));
}

// Halton 序列 (低差异采样)
float Halton(int index, int baseNum)
{
    float f = 1.0, r = 0.0;
    int i = index;
    while (i > 0)
    {
        f /= float(baseNum);
        r += f * float(i % baseNum);
        i /= baseNum;
    }
    return r;
}

// 余弦加权半球采样 (生成漫反射采样方向)
float3 SampleCosineHemisphere(float2 uniformSample, float3 normal)
{
    float phi = SHANYU_TWO_PI * uniformSample.x;
    float cosTheta = sqrt(1.0 - uniformSample.y);
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    float3 localDir = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

    // 切线空间 -> 世界空间
    float3 up = abs(normal.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, normal));
    float3 bitangent = cross(normal, tangent);

    return normalize(tangent * localDir.x + bitangent * localDir.y + normal * localDir.z);
}

// 将世界位置投影到屏幕 UV
float2 ProjectToScreen(float3 worldPos, float4x4 viewProj)
{
    float4 clip = mul(viewProj, float4(worldPos, 1.0));
    float2 ndc = clip.xy / clip.w;
    return ndc * 0.5 + 0.5;
}

// 屏幕空间光线步进 (Hi-Z 引导)
// 返回命中点的屏幕 UV 与命中距离, hitFound 表示是否命中
struct SSRayHit
{
    float2 hitUV;
    float  hitDepth;
    float  rayDistance;
    bool   hitFound;
    float  confidence;
};

SSRayHit ScreenSpaceRayMarch(float3 rayOrigin, float3 rayDir, float maxDist)
{
    SSRayHit result = (SSRayHit)0;
    result.hitFound = false;

    // 起点投影到屏幕
    float2 startUV = ProjectToScreen(rayOrigin, ViewProj);
    if (any(startUV < 0.0) || any(startUV > 1.0))
        return result;

    float3 currentPos = rayOrigin;
    float3 prevPos = rayOrigin;
    float stepSize = RayStep;

    // 沿射线步进
    [loop]
    for (int i = 0; i < 64; i++)
    {
        prevPos = currentPos;
        currentPos += rayDir * stepSize;

        float traveled = length(currentPos - rayOrigin);
        if (traveled > maxDist)
            break;

        float2 uv = ProjectToScreen(currentPos, ViewProj);

        // 屏幕外则停止
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            break;

        // 采样 Hi-Z 深度 (根据步进距离选择 mip, 距离越远 mip 越大 -> 越粗略)
        int mipLevel = clamp(int(log2(traveled * 0.5 + 1.0)), 0, 6);
        float sampledDepth = SampleHiZDepth(uv, mipLevel);

        // 重建命中点世界位置
        float4 clipPos = float4(uv * 2.0 - 1.0, sampledDepth, 1.0);
        clipPos.y = -clipPos.y;
        float4 hitWorld = mul(InvViewProj, clipPos);
        hitWorld.xyz /= hitWorld.w;

        float rayDepth = currentPos.z;
        float hitZ = hitWorld.z;
        float depthDiff = rayDepth - hitZ;

        // 命中测试: 射线深度大于场景深度 (射线在表面后方), 且差距在合理范围
        if (depthDiff > 0.0 && depthDiff < stepSize * 2.0)
        {
            // 二分精修命中点
            float3 lo = prevPos;
            float3 hi = currentPos;
            [loop]
            for (int r = 0; r < 6; r++)
            {
                float3 mid = (lo + hi) * 0.5;
                float2 midUV = ProjectToScreen(mid, ViewProj);
                if (midUV.x < 0.0 || midUV.x > 1.0 || midUV.y < 0.0 || midUV.y > 1.0)
                    break;
                float midDepth = SampleHiZDepth(midUV, 0);
                float4 mc = float4(midUV * 2.0 - 1.0, midDepth, 1.0);
                mc.y = -mc.y;
                float4 mw = mul(InvViewProj, mc);
                mw.xyz /= mw.w;
                if (mid.z > mw.z)
                    hi = mid;
                else
                    lo = mid;
            }

            float2 finalUV = ProjectToScreen(hi, ViewProj);
            result.hitUV = finalUV;
            result.hitDepth = sampledDepth;
            result.rayDistance = traveled;
            result.hitFound = true;

            // 置信度: 基于距离衰减与深度厚度
            float distFade = 1.0 - saturate_fast(traveled / maxDist);
            float depthConfidence = saturate_fast(1.0 - abs(depthDiff) / (stepSize * 2.0));
            result.confidence = distFade * depthConfidence;
            return result;
        }

        // 自适应步长: 距离越远步长越大
        stepSize *= 1.15;
    }

    return result;
}

// ──────────────────────────────────────────────────────────────────────────
// 主计算着色器
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void SSGICS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gDepthBuffer.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float2 uv = (pixel + 0.5) * InvResolution;

    // 采样 G-Buffer
    float deviceDepth = gDepthBuffer[pixel];
    if (deviceDepth <= 0.0)
    {
        gSSGIOutput[pixel] = float4(0, 0, 0, 0);
        gSSGIVelocity[pixel] = float2(0, 0);
        return;
    }

    float4 albedoRough = gGeometryBufferAlbedo[pixel];
    float3 albedo = albedoRough.rgb;
    float  roughness = albedoRough.a;

    float4 normalMetal = gGeometryBufferNormal[pixel];
    float3 normal = DecodeNormalOctahedral(normalMetal.xy);
    float  metallic = normalMetal.z;

    // 重建世界位置
    float3 worldPos = ReconstructWorldPos(uv, deviceDepth);

    // 视线方向
    float3 viewDir = normalize(CameraPos - worldPos);

    // 蓝噪声种子 (基于像素与帧索引)
    uint noiseW, noiseH;
    gBlueNoise.GetDimensions(noiseW, noiseH);
    float2 blueNoise = gBlueNoise.Load(int3(pixel % int2(noiseW, noiseH), 0)).xy;

    // 多采样累积
    float3 irradianceAccum = float3(0, 0, 0);
    float  weightAccum = 0.0;
    float  totalConfidence = 0.0;

    int actualSamples = max(1, SampleCount);

    [loop]
    for (int s = 0; s < actualSamples; s++)
    {
        // Halton + 蓝噪声抖动生成采样方向
        int sampleIndex = FrameIndex * actualSamples + s;
        float2 halton = float2(Halton(sampleIndex + 1, 2), Halton(sampleIndex + 1, 3));
        float2 uniformSample = frac(halton + blueNoise);

        // 余弦加权半球采样
        float3 rayDir = SampleCosineHemisphere(uniformSample, normal);

        // 起点沿法线偏移避免自相交
        float3 rayOrigin = worldPos + normal * 0.02;

        // 屏幕空间光线步进
        SSRayHit hit = ScreenSpaceRayMarch(rayOrigin, rayDir, MaxRayLength);

        if (hit.hitFound)
        {
            // 采样上一帧光照缓冲 (使用运动向量重投影)
            float2 hitUV = hit.hitUV;
            float2 motion = gMotionVectors.Load(int3(hitUV * resolution, 0));
            float2 historyUV = hitUV - motion;

            float3 hitIrradiance = gPrevSceneColor.SampleLevel(gPointClampSampler, historyUV, 0).rgb;

            // 距离衰减 (近场贡献大, 远场贡献小)
            float distAtten = 1.0 - saturate_fast(hit.rayDistance / MaxRayLength);

            // 深度可信度
            float confidence = hit.confidence * distAtten;

            irradianceAccum += hitIrradiance * confidence;
            weightAccum += confidence;
            totalConfidence += confidence;
        }
        else
        {
            // 未命中: 使用天空光作为环境贡献
            float skyFactor = saturate_fast(rayDir.y * 0.5 + 0.5);
            float3 skyLight = float3(0.4, 0.6, 1.0) * skyFactor * 0.15;
            irradianceAccum += skyLight * 0.1;
            weightAccum += 0.1;
        }
    }

    // 归一化
    float3 irradiance = weightAccum > 0.001 ? irradianceAccum / weightAccum : float3(0, 0, 0);

    // 反照率调制 (漫反射间接光 = 辐照度 * 反照率 / PI)
    float3 diffuseColor = albedo * (1.0 - metallic);
    irradiance *= diffuseColor * SHANYU_INV_PI;

    // 平均置信度
    float avgConfidence = totalConfidence / float(actualSamples);

    // 时序累积: 与上一帧 SSGI 混合
    float2 prevUV = uv;
    // 使用运动向量找到上一帧位置
    float2 currentMotion = gMotionVectors[pixel];
    float2 reprojUV = uv - currentMotion;
    bool validHistory = all(reprojUV > 0.0) && all(reprojUV < 1.0);

    if (validHistory)
    {
        float3 historyIrradiance = gPrevSSGI.SampleLevel(gPointClampSampler, reprojUV, 0).rgb;
        float historyConfidence = gPrevSSGI.SampleLevel(gPointClampSampler, reprojUV, 0).a;
        float alpha = lerp(0.1, TemporalAlpha, avgConfidence);
        irradiance = lerp(irradiance, historyIrradiance, alpha);
        avgConfidence = max(avgConfidence, historyConfidence * TemporalAlpha);
    }

    gSSGIOutput[pixel] = float4(irradiance, avgConfidence);
    gSSGIVelocity[pixel] = currentMotion;
}
