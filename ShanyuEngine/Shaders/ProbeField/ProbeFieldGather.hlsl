// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - ProbeField Final Gather
// --------------------------------------------------------------------------
// 最终汇聚: 合并所有 GI 子系统的结果 (SSGI + RT + 探针 + 表面缓存 + 体素
// 锥体追踪 + 天空光), 输出最终间接辐照度到光照缓冲。
//
// 两个入口:
//   GatherCS     - 主汇聚 (合并各层 GI + 天空光 + 环境光)
//   RTGatherCS   - 硬件 RT GI 汇聚 (使用 RayQuery 进行精确多跳光线追踪)
//
// 入口点: GatherCS, RTGatherCS  (CS, [numthreads(8,8,1)])
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 汇聚常量缓冲 (对应 C# ProbeFieldGatherCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer GatherConstants : register(b2)
{
    float4x4 ViewProj;
    float4x4 InvViewProj;
    float3   CameraPos;
    float    IndirectIntensity;
    int      BounceCount;
    float    SkyIntensity;
    float3   SkyColor;
    float    AmbientMul;
    float2   InvResolution;
    int      EnableRT;
    int      EnableVoxel;
};

// ──────────────────────────────────────────────────────────────────────────
// RT 汇聚常量 (RTGatherCS 专用)
// ──────────────────────────────────────────────────────────────────────────
cbuffer RTGatherConstants : register(b3)
{
    float4x4 RTViewProj;
    float4x4 RTInvViewProj;
    float3   RTCameraPos;
    float    RTMaxRayDistance;
    int      RTRaysPerPixel;
    int      RTMaxBounces;
    float    RTTemporalAlpha;
    int      RTFrameIndex;
    float2   RTJitter;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4>  gGeometryBufferAlbedo    : register(t0); // 反照率 + 粗糙度
Texture2D<float4>  gGeometryBufferNormal    : register(t1); // 法线 + 金属度
Texture2D<float>   gDepthBuffer      : register(t2); // 深度
Texture2D<float4>  gSSGIResult       : register(t3); // SSGI 辐照度
Texture2D<float4>  gRTGIResult       : register(t4); // 硬件 RT GI 结果
Texture2D<float4>  gSurfaceCacheRad  : register(t5); // 表面缓存辐射度
Texture2D<float4>  gProbeIrradiance  : register(t6); // 探针辐照度图集
Texture3D<float4>  gVoxelRadiance    : register(t7); // 体素辐射度 (3D)

// RTGatherCS 额外资源
RaytracingAccelerationStructure gTLAS : register(t0, space1);

RWTexture2D<float4> gOutputIrradiance : register(u0); // 最终间接辐照度

SamplerState gPointClampSampler  : register(s0);
SamplerState gLinearClampSampler : register(s2);
SamplerState gLinearWrapSampler  : register(s3);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 体素锥体追踪 (镜面间接光)
float3 ConeTraceVoxels(float3 origin, float3 direction, float tanHalfAngle, float maxDist)
{
    float3 accum = float3(0, 0, 0);
    float weight = 0.0;
    float voxelSize = 0.5;
    float dist = voxelSize * 0.5;

    [loop]
    while (dist < maxDist && weight < 1.0)
    {
        float radius = dist * tanHalfAngle;
        float lod = max(0.0, log2(radius / voxelSize));
        float3 samplePos = (origin + direction * dist) * 0.01;

        if (any(abs(samplePos) > 1.0))
            break;

        float4 voxelSample = gVoxelRadiance.SampleLevel(gLinearClampSampler, samplePos, lod);
        float w = (1.0 - weight) * voxelSample.a;
        accum += voxelSample.rgb * w;
        weight += w;
        dist += radius * 1.5;
    }
    return accum;
}

// 探针辐照度查询
float3 SampleProbeIrradiance(float3 worldPos, float3 normal)
{
    float2 probeUV = worldPos.xz * 0.01 + 0.5;
    probeUV = saturate_fast(probeUV);
    float4 irr = gProbeIrradiance.SampleLevel(gLinearClampSampler, probeUV, 0);
    float nDotUp = max(0.0, normal.y);
    return irr.rgb * (nDotUp * 0.5 + 0.5);
}

// 天空光采样
float3 SampleSkyLight(float3 normal, float3 reflectDir)
{
    float skyFactor = saturate_fast(normal.y * 0.5 + 0.5);
    return SkyColor * SkyIntensity * skyFactor;
}

// 球谐辐照度重建 (用于探针近场)
float3 EvaluateSHIrradiance(float3 normal)
{
    return SampleProbeIrradiance(float3(0, 0, 0), normal);
}

// ─────────────────────────────────══════════════════════════════════════─
// GatherCS - 最终汇聚主着色器
// 合并 SSGI + RT + 探针 + 表面缓存 + 体素 + 天空光
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void GatherCS(uint3 dispatchID : SV_DispatchThreadID)
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
        // 天空像素: 输出天空光
        gOutputIrradiance[pixel] = float4(SkyColor * SkyIntensity, 0.0);
        return;
    }

    float4 albedoRough = gGeometryBufferAlbedo[pixel];
    float4 normalMetal = gGeometryBufferNormal[pixel];
    float3 albedo = albedoRough.rgb;
    float  roughness = albedoRough.a;
    float3 normal = DecodeNormalOctahedral(normalMetal.xy);
    float  metallic = normalMetal.z;

    // 重建世界位置
    float3 worldPos = ReconstructWorldPos(uv, deviceDepth);
    float3 viewDir = normalize(CameraPos - worldPos);

    // ── 1. SSGI 辐照度 (高频近场) ──
    float3 ssgiIrr = gSSGIResult[pixel].rgb;
    float ssgiConfidence = gSSGIResult[pixel].a;

    // ── 2. 硬件 RT GI (精确, 远场) ──
    float3 rtIrr = float3(0, 0, 0);
    float rtWeight = 0.0;
    if (EnableRT != 0)
    {
        rtIrr = gRTGIResult.SampleLevel(gPointClampSampler, uv, 0).rgb;
        rtWeight = gRTGIResult.SampleLevel(gPointClampSampler, uv, 0).a;
    }

    // ── 3. 探针辐照度 (低频漫反射) ──
    float3 probeIrr = SampleProbeIrradiance(worldPos, normal);

    // ── 4. 表面缓存辐射度 (近场多跳) ──
    float3 surfaceIrr = gSurfaceCacheRad.SampleLevel(gPointClampSampler, uv, 0).rgb;

    // ── 5. 体素锥体追踪 (镜面间接光) ──
    float3 voxelIrr = float3(0, 0, 0);
    if (EnableVoxel != 0)
    {
        float3 reflectDir = reflect(-viewDir, normal);
        float coneAngle = tan(roughness * roughness * 0.5 + 0.05);
        voxelIrr = ConeTraceVoxels(worldPos + normal * 0.1, reflectDir, coneAngle, 50.0);
    }

    // ── 6. 天空光 ──
    float3 skyIrr = SampleSkyLight(normal, reflect(-viewDir, normal));

    // ── 合成策略: 按权重混合各层 ──
    // RT 优先 (最精确), 其次 SSGI (高频近场), 再次探针/表面缓存 (低频)
    float3 diffuseIrr = float3(0, 0, 0);
    float totalWeight = 0.0;

    // RT 权重: 有 RT 结果时优先使用
    if (rtWeight > 0.01)
    {
        diffuseIrr += rtIrr * rtWeight;
        totalWeight += rtWeight;
    }

    // SSGI 权重: 基于置信度
    float ssgiWeight = ssgiConfidence * (1.0 - rtWeight);
    diffuseIrr += ssgiIrr * ssgiWeight;
    totalWeight += ssgiWeight;

    // 探针 + 表面缓存: 填充屏幕外/低置信度区域
    float fillWeight = max(0.0, 1.0 - totalWeight);
    diffuseIrr += probeIrr * fillWeight * 0.6;
    diffuseIrr += surfaceIrr * fillWeight * 0.4;
    totalWeight += fillWeight;

    diffuseIrr /= max(totalWeight, 0.001);

    // 天空光 (始终贡献)
    diffuseIrr += skyIrr * AmbientMul;

    // 镜面间接光 (体素追踪)
    float3 specularIrr = voxelIrr;

    // 反照率调制
    float3 diffuseColor = albedo * (1.0 - metallic);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

    // 漫反射间接光
    float3 indirectDiffuse = diffuseIrr * diffuseColor * SHANYU_INV_PI;

    // 镜面间接光 (使用 EnvBRDFApprox)
    float NdotV = max(dot(normal, viewDir), 0.0);
    float3 indirectSpecular = specularIrr * EnvBRDFApprox(F0, roughness, NdotV);

    // 多跳能量缩放
    float bounceScale = 1.0 / float(BounceCount);

    // 最终间接辐照度
    float3 finalIrradiance = (indirectDiffuse + indirectSpecular) * IndirectIntensity * bounceScale;

    gOutputIrradiance[pixel] = float4(finalIrradiance, 1.0);
}

// ─────────────────────────────────══════════════════════════════════════─
// RTGatherCS - 硬件 RT GI 汇聚 (使用 RayQuery SM 6.6 inline ray tracing)
// 每像素发射射线, 多跳累积间接光, 精确处理遮挡与多跳能量
// ──────────────────────────────────────────────────────────────────────────
struct RTPayload
{
    float3 radiance;
    float3 throughput;
    uint   bounce;
};

// Halton 序列
float RTGatherHalton(int index, int baseNum)
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

// GGX 重要性采样方向
float3 SampleGGXDirection(float2 uniformSample, float3 normal, float roughness, float3 viewDir)
{
    float a = roughness * roughness;
    float phi = SHANYU_TWO_PI * uniformSample.x;
    float cosTheta = sqrt((1.0 - uniformSample.y) / (1.0 + (a * a - 1.0) * uniformSample.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    float3 h = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

    float3 up = abs(normal.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, normal));
    float3 bitangent = cross(normal, tangent);
    float3 halfVec = normalize(tangent * h.x + bitangent * h.y + normal * h.z);

    return reflect(-viewDir, halfVec);
}

[numthreads(8, 8, 1)]
void RTGatherCS(uint3 dispatchID : SV_DispatchThreadID)
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
        gOutputIrradiance[pixel] = float4(SkyColor * SkyIntensity, 0.0);
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

    // 射线起点 (沿法线偏移避免自相交)
    float3 rayOrigin = worldPos + normal * 0.01;

    float3 radiance = float3(0, 0, 0);
    float3 throughput = float3(1, 1, 1);

    // 多跳光线追踪
    [loop]
    for (int bounce = 0; bounce < RTMaxBounces; bounce++)
    {
        // GGX 重要性采样方向
        int sampleIdx = RTFrameIndex * RTRaysPerPixel + bounce;
        float2 halton = float2(RTGatherHalton(sampleIdx + 1, 2),
                               RTGatherHalton(sampleIdx + 1, 3));
        halton = frac(halton + RTJitter);

        float3 rayDir = SampleGGXDirection(halton, normal, roughness, viewDir);

        // 使用 RayQuery 进行 inline 光线追踪 (SM 6.6)
        RayDesc ray;
        ray.Origin = rayOrigin;
        ray.Direction = rayDir;
        ray.TMin = 0.01;
        ray.TMax = RTMaxRayDistance;

        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH |
                 RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
        q.TraceRayInline(gTLAS, RAY_FLAG_NONE, 0xFF, ray);

        // 评估射线
        while (q.Proceed())
        {
            // 可在此处理 any-hit (alpha 测试等)
        }

        if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
        {
            // 命中几何体: 采样命中点辐射度
            float3 hitPos = ray.Origin + ray.Direction * q.CommittedRayT();
            float3 hitNormal = normalize(q.CommittedTriangleNormal());

            // 将命中点投影到屏幕采样辐照度 (简化: 使用 SSGI/表面缓存)
            float4 clipPos = mul(RTViewProj, float4(hitPos, 1.0));
            if (clipPos.w > 0.0)
            {
                float2 hitUV = (clipPos.xy / clipPos.w) * 0.5 + 0.5;
                if (all(hitUV > 0.0) && all(hitUV < 1.0))
                {
                    float3 hitIrr = gSSGIResult.SampleLevel(gPointClampSampler, hitUV, 0).rgb;
                    float3 hitAlbedo = gGeometryBufferAlbedo.SampleLevel(gPointClampSampler, hitUV, 0).rgb;

                    // BRDF 调制
                    float NdotL = max(dot(normal, rayDir), 0.0);
                    radiance += throughput * hitIrr * hitAlbedo * NdotL * SHANYU_INV_PI;
                }
            }

            // 准备下一跳
            float3 diffuseColor = albedo * (1.0 - metallic);
            throughput *= diffuseColor * 0.5; // 能量衰减
            throughput *= max(dot(hitNormal, -rayDir), 0.0);

            rayOrigin = hitPos + hitNormal * 0.01;
            viewDir = -rayDir;
            normal = hitNormal;
        }
        else
        {
            // 未命中: 天空光
            float skyFactor = saturate_fast(rayDir.y * 0.5 + 0.5);
            radiance += throughput * SkyColor * SkyIntensity * skyFactor;
            break;
        }

        // 俄罗斯轮盘: 提前终止低贡献路径
        if (bounce > 1 && Luminance(throughput) < 0.05)
            break;
    }

    float diffuseConfidence = 1.0;
    gOutputIrradiance[pixel] = float4(radiance * IndirectIntensity, diffuseConfidence);
}
