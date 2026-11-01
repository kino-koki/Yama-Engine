// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - Ray Tracing Reflections (RayQuery, SM 6.6)
// --------------------------------------------------------------------------
// 硬件光线追踪反射/折射系统 (使用 SM 6.6 inline ray tracing - RayQuery):
//   - 屏幕空间反射 (SSR) + 光线追踪反射 (RTR) 混合
//   - GGX 重要性采样反射方向
//   - 降噪管线: 时序累积 -> 方差估计 -> atrous 波间滤波 -> 自适应
//   - RT 阴影与 RT 环境光遮蔽
//
// 7 个计算入口 (对应 C# RayTracingReflections 系统):
//   RTReflectionCS    - 反射 RayGen (SSR + RTR 混合, RayQuery)
//   RTShadowCS        - RT 阴影
//   RTAOCS            - RT 环境光遮蔽
//   RTTemporalAccumCS - 时序累积
//   RTVarianceCS      - 方差估计
//   RTAtrousCS        - atrous 波间滤波
//   RTAdaptiveCS      - 自适应方差缩放
//
// 入口点: 见上  (CS, [numthreads(8,8,1)])
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// RT 反射主常量 (对应 C# RTReflectionConstants)
// ──────────────────────────────────────────────────────────────────────────
cbuffer RTReflectionConstants : register(b2)
{
    float4x4 ViewMatrix;
    float4x4 ProjMatrix;
    float4x4 InvViewMatrix;
    float4x4 InvProjMatrix;
    float3   CameraPos;
    float    RayEpsilon;
    float2   InvResolution;
    int      MaxBounces;
    float    MaxRayDistance;
    float    RoughnessFallbackThreshold;
    int      EnableRefraction;
    int      FrameIndex;
    int      SamplesPerPixel;
    float2   Jitter;
    float    AORadius;
    int      AOSamples;
    int      ShadowSamples;
    float    RTPad0, RTPad1, RTPad2;
};

// ──────────────────────────────────────────────────────────────────────────
// RT 降噪常量 (对应 C# RTDenoiserConstants)
// ──────────────────────────────────────────────────────────────────────────
cbuffer RTDenoiserConstants : register(b3)
{
    float2 DenoiseInvResolution;
    float  DenoiseTemporalAlpha;
    int    VarianceRadius;
    int    AtrousIterations;
    float  DepthSigma;
    float  NormalSigma;
    float  LuminanceSigma;
    int    DenoiseStepWidth;
    int    DenoiserType;
    float  AdaptiveScale;
    float  DenoisePad0, DenoisePad1;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
// 加速结构 (TLAS) - 用于 RayQuery inline ray tracing
RaytracingAccelerationStructure gSceneTLAS : register(t0, space1);

Texture2D<float4>  gGeometryBufferAlbedo    : register(t0); // 反照率 + 粗糙度
Texture2D<float4>  gGeometryBufferNormal    : register(t1); // 法线 + 金属度
Texture2D<float>   gDepthBuffer      : register(t2); // 场景深度
Texture2D<float4>  gSceneColor       : register(t3); // 场景光照 (SSR 回退)
Texture2D<float4>  gPrevReflection   : register(t4); // 上一帧反射 (时序)
Texture2D<float4>  gHistoryBuffer    : register(t5); // 降噪历史
Texture2D<float2>  gMotionVectors    : register(t6); // 运动向量
Texture2D<float4>  gBlueNoise        : register(t7); // 蓝噪声

RWTexture2D<float4> gReflectionRT   : register(u0); // 反射输出
RWTexture2D<float4> gRefractionRT   : register(u1); // 折射输出
RWTexture2D<float4> gShadowRT       : register(u2); // 阴影输出
RWTexture2D<float4> gAORT           : register(u3); // AO 输出
RWTexture2D<float4> gVarianceBuffer : register(u4); // 方差缓冲
RWTexture2D<float4> gMomentsBuffer  : register(u5); // 矩缓冲

SamplerState gPointClampSampler  : register(s0);
SamplerState gLinearClampSampler : register(s2);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// Halton 序列
float RTHalton(int index, int baseNum)
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

// GGX 重要性采样微表面半程向量
float3 SampleGGXMicrofacet(float2 uniformSample, float3 normal, float roughness)
{
    float a = roughness * roughness;
    float phi = SHANYU_TWO_PI * uniformSample.x;
    float cosTheta = sqrt((1.0 - uniformSample.y) / (1.0 + (a * a - 1.0) * uniformSample.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    float3 h = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

    float3 up = abs(normal.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, normal));
    float3 bitangent = cross(normal, tangent);
    return normalize(tangent * h.x + bitangent * h.y + normal * h.z);
}

// 屏幕空间反射光线步进 (SSR 回退)
struct SSRResult
{
    float3 color;
    float  confidence;
    bool   hit;
};

SSRResult ScreenSpaceReflection(float3 origin, float3 dir, float maxDist)
{
    SSRResult result = (SSRResult)0;
    result.hit = false;

    float3 pos = origin;
    float step = 0.3;

    [loop]
    for (int i = 0; i < 32; i++)
    {
        pos += dir * step;
        float dist = length(pos - origin);
        if (dist > maxDist)
            break;

        float4 clip = mul(ProjMatrix, mul(ViewMatrix, float4(pos, 1.0)));
        if (clip.w <= 0.0)
            continue;

        float2 ndc = clip.xy / clip.w;
        float2 uv = ndc * 0.5 + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0))
            break;

        uint2 res;
        gDepthBuffer.GetDimensions(res.x, res.y);
        float sceneDepth = gDepthBuffer.SampleLevel(gPointClampSampler, uv, 0);

        float4 depthClip = float4(ndc, sceneDepth, 1.0);
        depthClip.y = -depthClip.y;
        float4 hitWorld = mul(mul(InvProjMatrix, InvViewMatrix), depthClip);
        hitWorld.xyz /= hitWorld.w;

        float rayViewZ = mul(ViewMatrix, float4(pos, 1.0)).z;
        float hitViewZ = mul(ViewMatrix, float4(hitWorld.xyz, 1.0)).z;

        if (rayViewZ < hitViewZ && abs(rayViewZ - hitViewZ) < step * 2.0)
        {
            result.color = gSceneColor.SampleLevel(gPointClampSampler, uv, 0).rgb;
            result.confidence = 1.0 - saturate_fast(dist / maxDist);
            result.hit = true;
            return result;
        }
        step *= 1.1;
    }
    return result;
}

// 光线追踪反射 (RayQuery)
struct RTRResult
{
    float3 color;
    float  distance;
    bool   hit;
};

RTRResult TraceReflectionRay(float3 origin, float3 dir, float maxDist)
{
    RTRResult result = (RTRResult)0;
    result.hit = false;

    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = dir;
    ray.TMin = RayEpsilon;
    ray.TMax = maxDist;

    // SM 6.6 inline ray tracing
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH |
             RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(gSceneTLAS, RAY_FLAG_NONE, 0xFF, ray);

    while (q.Proceed()) { }

    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
    {
        float3 hitPos = ray.Origin + ray.Direction * q.CommittedRayT();
        result.distance = q.CommittedRayT();
        result.hit = true;

        // 将命中点投影到屏幕采样光照
        float4 clip = mul(ProjMatrix, mul(ViewMatrix, float4(hitPos, 1.0)));
        if (clip.w > 0.0)
        {
            float2 uv = (clip.xy / clip.w) * 0.5 + 0.5;
            if (all(uv > 0.0) && all(uv < 1.0))
            {
                result.color = gSceneColor.SampleLevel(gPointClampSampler, uv, 0).rgb;
            }
            else
            {
                // 屏幕外: 使用天空光
                result.color = float3(0.4, 0.6, 1.0) * saturate_fast(dir.y * 0.5 + 0.5) * 0.3;
            }
        }
    }
    else
    {
        // 未命中: 天空光
        result.color = float3(0.4, 0.6, 1.0) * saturate_fast(dir.y * 0.5 + 0.5) * 0.3;
        result.distance = maxDist;
    }

    return result;
}

// ─────────────────────────────────══════════════════════════════════════─
// 1. RTReflectionCS - 反射 RayGen (SSR + RTR 混合, RayQuery)
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void RTReflectionCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gDepthBuffer.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float2 uv = (pixel + 0.5) * InvResolution;

    float deviceDepth = gDepthBuffer[pixel];
    if (deviceDepth <= 0.0)
    {
        gReflectionRT[pixel] = float4(0, 0, 0, 0);
        return;
    }

    float4 albedoRough = gGeometryBufferAlbedo[pixel];
    float4 normalMetal = gGeometryBufferNormal[pixel];
    float3 albedo = albedoRough.rgb;
    float  roughness = albedoRough.a;
    float3 normal = DecodeNormalOctahedral(normalMetal.xy);
    float  metallic = normalMetal.z;

    float3 worldPos = ReconstructWorldPos(uv, deviceDepth);
    float3 viewDir = normalize(CameraPos - worldPos);

    // 反射方向
    float3 reflectDir = reflect(-viewDir, normal);

    // 蓝噪声种子
    uint noiseW, noiseH;
    gBlueNoise.GetDimensions(noiseW, noiseH);
    float2 blueNoise = gBlueNoise.Load(int3(pixel % int2(noiseW, noiseH), 0)).xy;

    float3 reflectionColor = float3(0, 0, 0);
    float totalWeight = 0.0;

    // 多采样: GGX 重要性采样
    [loop]
    for (int s = 0; s < SamplesPerPixel; s++)
    {
        int sampleIdx = FrameIndex * SamplesPerPixel + s;
        float2 halton = float2(RTHalton(sampleIdx + 1, 2), RTHalton(sampleIdx + 1, 3));
        float2 uniformSample = frac(halton + blueNoise);

        // GGX 重要性采样微表面方向
        float3 halfVec = SampleGGXMicrofacet(uniformSample, normal, roughness);
        float3 sampleDir = reflect(-viewDir, halfVec);

        // 粗糙度回退: 粗糙度高时使用 SSR (性能优化)
        bool useSSR = (roughness > RoughnessFallbackThreshold);

        float3 rayOrigin = worldPos + normal * RayEpsilon;

        if (useSSR)
        {
            // 屏幕空间反射
            SSRResult ssr = ScreenSpaceReflection(rayOrigin, sampleDir, MaxRayDistance);
            if (ssr.hit)
            {
                reflectionColor += ssr.color * ssr.confidence;
                totalWeight += ssr.confidence;
            }
            else
            {
                // SSR 未命中: 天空光
                reflectionColor += float3(0.4, 0.6, 1.0) * saturate_fast(sampleDir.y * 0.5 + 0.5) * 0.2;
                totalWeight += 0.2;
            }
        }
        else
        {
            // 硬件光线追踪反射 (RayQuery)
            RTRResult rtr = TraceReflectionRay(rayOrigin, sampleDir, MaxRayDistance);
            float confidence = rtr.hit ? 1.0 : 0.3;
            reflectionColor += rtr.color * confidence;
            totalWeight += confidence;
        }
    }

    reflectionColor = totalWeight > 0.001 ? reflectionColor / totalWeight : float3(0, 0, 0);

    // 菲涅尔调制
    float NdotV = max(dot(normal, viewDir), 0.0);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);
    float3 fresnel = F_Schlick(NdotV, F0);

    reflectionColor *= fresnel;

    gReflectionRT[pixel] = float4(reflectionColor, 1.0);
}

// ─────────────────────────────────══════════════════════════════════════─
// 2. RTShadowCS - RT 阴影
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void RTShadowCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gDepthBuffer.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float2 uv = (pixel + 0.5) * InvResolution;
    float deviceDepth = gDepthBuffer[pixel];

    if (deviceDepth <= 0.0)
    {
        gShadowRT[pixel] = float4(1, 1, 1, 1); // 天空无阴影
        return;
    }

    float3 normal = DecodeNormalOctahedral(gGeometryBufferNormal[pixel].xy);
    float3 worldPos = ReconstructWorldPos(uv, deviceDepth);

    // 光源方向 (简化: 使用固定方向光)
    float3 lightDir = normalize(float3(-0.5, -0.8, 0.3));

    // 沿法线偏移
    float3 rayOrigin = worldPos + normal * RayEpsilon;

    float shadow = 0.0;
    int samples = max(1, ShadowSamples);

    [loop]
    for (int s = 0; s < samples; s++)
    {
        // 蓝噪声抖动 (软阴影)
        float2 noise = gBlueNoise.Load(int3((pixel + s * 37) & 255, 0)).xy;
        float3 diskOffset = (noise.x - 0.5) * 0.02 * float3(1, 0, 0)
                           + (noise.y - 0.5) * 0.02 * float3(0, 0, 1);
        float3 sampleDir = normalize(-lightDir + diskOffset);

        RayDesc ray;
        ray.Origin = rayOrigin;
        ray.Direction = sampleDir;
        ray.TMin = RayEpsilon;
        ray.TMax = MaxRayDistance;

        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;
        q.TraceRayInline(gSceneTLAS, RAY_FLAG_NONE, 0xFF, ray);
        while (q.Proceed()) { }

        if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
            shadow += 1.0;
    }

    shadow /= float(samples);
    float visibility = 1.0 - shadow;

    gShadowRT[pixel] = float4(visibility, visibility, visibility, 1.0);
}

// ─────────────────────────────────══════════════════════════════════════─
// 3. RTAOCS - RT 环境光遮蔽
// [numthreads(8,8,1)]
// ─────────────────────────────────────────────────══════════─────────────
[numthreads(8, 8, 1)]
void RTAOCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gDepthBuffer.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float2 uv = (pixel + 0.5) * InvResolution;
    float deviceDepth = gDepthBuffer[pixel];

    if (deviceDepth <= 0.0)
    {
        gAORT[pixel] = float4(1, 1, 1, 1);
        return;
    }

    float3 normal = DecodeNormalOctahedral(gGeometryBufferNormal[pixel].xy);
    float3 worldPos = ReconstructWorldPos(uv, deviceDepth);
    float3 rayOrigin = worldPos + normal * RayEpsilon;

    float occlusion = 0.0;
    int samples = max(1, AOSamples);

    [loop]
    for (int s = 0; s < samples; s++)
    {
        // 余弦加权半球采样
        float2 noise = gBlueNoise.Load(int3((pixel + s * 53) & 255, 0)).xy;
        float2 halton = float2(RTHalton(s + 1, 2), RTHalton(s + 1, 3));
        float2 uniformSample = frac(halton + noise);

        float phi = SHANYU_TWO_PI * uniformSample.x;
        float cosTheta = sqrt(1.0 - uniformSample.y);
        float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
        float3 localDir = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

        float3 up = abs(normal.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
        float3 tangent = normalize(cross(up, normal));
        float3 bitangent = cross(normal, tangent);
        float3 sampleDir = normalize(tangent * localDir.x + bitangent * localDir.y + normal * localDir.z);

        RayDesc ray;
        ray.Origin = rayOrigin;
        ray.Direction = sampleDir;
        ray.TMin = RayEpsilon;
        ray.TMax = AORadius;

        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;
        q.TraceRayInline(gSceneTLAS, RAY_FLAG_NONE, 0xFF, ray);
        while (q.Proceed()) { }

        if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
        {
            // 距离衰减: 近处遮挡贡献大
            float hitDist = q.CommittedRayT();
            float atten = 1.0 - saturate_fast(hitDist / AORadius);
            occlusion += atten;
        }
    }

    occlusion /= float(samples);
    float ao = 1.0 - occlusion;

    gAORT[pixel] = float4(ao, ao, ao, 1.0);
}

// ─────────────────────────────────══════════════════════════════════════─
// 4. RTTemporalAccumCS - 时序累积 (EMA + 重投影)
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void RTTemporalAccumCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gReflectionRT.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float2 uv = (pixel + 0.5) * DenoiseInvResolution;

    float4 current = gReflectionRT[pixel];
    float2 motion = gMotionVectors[pixel];
    float2 historyUV = uv - motion;

    float4 result = current;

    if (all(historyUV > 0.0) && all(historyUV < 1.0))
    {
        float4 history = gHistoryBuffer.SampleLevel(gPointClampSampler, historyUV, 0);

        // 深度一致性检查
        float currentDepth = gDepthBuffer[pixel];
        uint2 histPixel = clamp(uint2(historyUV * resolution), 0, resolution - 1);
        float histDepth = gDepthBuffer[histPixel];

        if (abs(currentDepth - histDepth) < 0.02)
        {
            float alpha = 1.0 - DenoiseTemporalAlpha;
            result = lerp(history, current, alpha);
        }
    }

    gReflectionRT[pixel] = result;
}

// ─────────────────────────────────══════────────────────═════════════════
// 5. RTVarianceCS - 方差估计 (从邻域计算亮度方差)
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void RTVarianceCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gReflectionRT.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float2 uv = (pixel + 0.5) * DenoiseInvResolution;
    float centerLum = Luminance(gReflectionRT[pixel].rgb);

    float mean = 0.0;
    float meanSq = 0.0;
    int count = 0;

    int radius = max(1, VarianceRadius);
    [loop]
    for (int dy = -radius; dy <= radius; dy++)
    {
        [loop]
        for (int dx = -radius; dx <= radius; dx++)
        {
            uint2 samplePixel = clamp(pixel + uint2(dx, dy), 0, resolution - 1);
            float lum = Luminance(gReflectionRT[samplePixel].rgb);
            mean += lum;
            meanSq += lum * lum;
            count++;
        }
    }

    mean /= float(count);
    meanSq /= float(count);
    float variance = max(0.0, meanSq - mean * mean);

    gVarianceBuffer[pixel] = float4(variance, variance, variance, 1.0);
}

// ─────────────────────────────────══════────────────────═════════════════
// 6. RTAtrousCS - atrous 波间滤波 (5 抽头十字, 边缘感知)
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void RTAtrousCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gReflectionRT.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float4 centerColor = gReflectionRT[pixel];
    float  centerDepth = gDepthBuffer[pixel];
    float3 centerNormal = DecodeNormalOctahedral(gGeometryBufferNormal[pixel].xy);
    float  centerLum = Luminance(centerColor.rgb);
    float  centerVar = gVarianceBuffer[pixel].r;

    float stepWorld = float(DenoiseStepWidth);

    float3 colorAccum = centerColor.rgb * 1.0;
    float weightAccum = 1.0;

    static const float2 offsets[4] = {
        float2( 1,  0), float2(-1,  0),
        float2( 0,  1), float2( 0, -1)
    };

    [unroll]
    for (int i = 0; i < 4; i++)
    {
        uint2 samplePixel = clamp(pixel + uint2(offsets[i] * stepWorld), 0, resolution - 1);

        float4 sampleColor = gReflectionRT[samplePixel];
        float  sampleDepth = gDepthBuffer[samplePixel];
        float3 sampleNormal = DecodeNormalOctahedral(gGeometryBufferNormal[samplePixel].xy);
        float  sampleLum = Luminance(sampleColor.rgb);
        float  sampleVar = gVarianceBuffer[samplePixel].r;

        // 边缘停止权重
        float wDepth = exp(-abs(centerDepth - sampleDepth) / max(DepthSigma, SHANYU_EPSILON));
        float wNormal = pow(max(0.0, dot(centerNormal, sampleNormal)), NormalSigma);
        float sigma = LuminanceSigma * sqrt(max(centerVar, sampleVar)) + 0.001;
        float wLum = exp(-abs(centerLum - sampleLum) / sigma);

        float weight = 0.5 * wDepth * wNormal * wLum;
        colorAccum += sampleColor.rgb * weight;
        weightAccum += weight;
    }

    float3 filtered = colorAccum / max(weightAccum, SHANYU_EPSILON);
    gReflectionRT[pixel] = float4(filtered, centerColor.a);
}

// ─────────────────────────────────══════════════════════════════════════─
// 7. RTAdaptiveCS - 自适应方差缩放 (A-SVGF)
// 高方差区域增大滤波强度, 低方差区域保留细节
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void RTAdaptiveCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gReflectionRT.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float4 centerColor = gReflectionRT[pixel];
    float variance = gVarianceBuffer[pixel].r;

    // 自适应缩放: 方差越高, 滤波半径越大
    float adaptiveRadius = AdaptiveScale * sqrt(variance);
    int adaptiveSteps = clamp(int(adaptiveRadius), 0, 3);

    float3 result = centerColor.rgb;

    [loop]
    for (int step = 1; step <= adaptiveSteps; step++)
    {
        int stepWidth = 1 << step;
        float3 blur = float3(0, 0, 0);
        float weight = 0.0;

        static const float2 offsets[4] = {
            float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1)
        };

        [unroll]
        for (int i = 0; i < 4; i++)
        {
            uint2 samplePixel = clamp(pixel + uint2(offsets[i] * stepWidth), 0, resolution - 1);
            blur += gReflectionRT[samplePixel].rgb;
            weight += 1.0;
        }

        blur /= max(weight, 1.0);

        // 距离引导混合: 与中心差异大时少混
        float diff = Luminance(abs(result - blur));
        float blend = exp(-diff * 4.0) * 0.5;
        result = lerp(result, blur, blend);
    }

    gReflectionRT[pixel] = float4(result, centerColor.a);
}
