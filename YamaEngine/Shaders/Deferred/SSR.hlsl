// ════════════════════════════════════════════════════════════════════════════
// SSR (Screen Space Reflections) — 屏幕空间反射
// 使用: Hi-Z 光线步进 + 粗糙度重要性采样 + 时序累积
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

Texture2D<float>    gDepthTex   : register(t0);
Texture2D<float4>   gGeometryBuffer0   : register(t1); // Albedo, Roughness
Texture2D<float4>   gGeometryBuffer1   : register(t2); // Normal.xy, Metallic
Texture2D<float4>   gSceneColor : register(t3);
Texture2D<float>    gHZBMip0    : register(t4); // HZB 最粗 mip
Texture2D<float4>   gBlueNoise  : register(t5);

RWTexture2D<float4> gOutput     : register(u0);
RWTexture2D<float2> gHitUV      : register(u1); // 命中 UV + 有效性

cbuffer SSRConstants : register(b2)
{
    uint  gMaxSteps;        // 最大步进数
    float gThickness;       // 厚度测试
    float gRayStepSize;     // 初始步长
    float gMaxDistance;     // 最大反射距离
    float gFadeStart;       // 距离淡出起始
    float gFadeEnd;         // 距离淡出结束
    uint  gPad0;
    uint  gPad1;
};

// Hi-Z 光线步进
bool RayMarchHiZ(float3 rayOriginVS, float3 rayDirVS, out float2 hitUV, out float hitDepth)
{
    hitUV = float2(0, 0);
    hitDepth = 0;

    float4 startPos = mul(gProjMatrix, float4(rayOriginVS, 1.0));
    startPos.xyz /= startPos.w;
    startPos.xy = startPos.xy * 0.5 + 0.5;
    startPos.y = 1.0 - startPos.y;

    float4 endPos = mul(gProjMatrix, float4(rayOriginVS + rayDirVS * gMaxDistance, 1.0));
    endPos.xyz /= endPos.w;
    endPos.xy = endPos.xy * 0.5 + 0.5;
    endPos.y = 1.0 - endPos.y;

    float2 uv = startPos.xy;
    float2 dir = normalize(endPos.xy - startPos.xy);

    // 沿屏幕空间方向步进
    float stepSize = gRayStepSize;
    float currentDepth = startPos.z;

    [loop]
    for (uint i = 0; i < gMaxSteps; i++)
    {
        uv += dir * stepSize;
        if (any(uv < 0.0) || any(uv > 1.0)) return false;

        float sampledDepth = gDepthTex.SampleLevel(gPointClampSampler, uv, 0).x;
        float depthDiff = sampledDepth - currentDepth;

        // 厚度测试
        if (depthDiff > 0.0 && depthDiff < gThickness)
        {
            hitUV = uv;
            hitDepth = sampledDepth;
            return true;
        }

        // 自适应步长: 根据 HZB 调整
        stepSize *= 1.25;
    }

    return false;
}

[numthreads(8, 8, 1)]
void SSRCS(uint3 dtid : SV_DispatchThreadID)
{
    uint2 pixel = dtid.xy;
    if (pixel.x >= (uint)gScreenParams.x || pixel.y >= (uint)gScreenParams.y) return;

    float2 uv = (pixel + 0.5) * gScreenParams.zw;
    float depth = gDepthTex.Load(int3(pixel, 0));
    if (depth >= 1.0) { gOutput[pixel] = float4(0, 0, 0, 0); return; }

    float4 gbuf0 = gGeometryBuffer0.Load(int3(pixel, 0));
    float4 gbuf1 = gGeometryBuffer1.Load(int3(pixel, 0));
    float roughness = gbuf0.a;
    float metallic = gbuf1.z;
    float3 normal = DecodeNormalOctahedral(gbuf1.xy);

    // 非金属或低粗糙度才做反射
    if (roughness > 0.8 || (metallic < 0.01 && roughness > 0.6))
    {
        gOutput[pixel] = float4(0, 0, 0, 0);
        return;
    }

    float3 viewPos = ReconstructViewPos(uv, depth);
    float3 viewDir = normalize(-viewPos);
    float3 reflectDir = reflect(-viewDir, normal);

    // 粗糙度重要性采样: 蓝噪声抖动
    float4 noise = gBlueNoise.Load(int3(pixel % 256, 0));
    float2 jitter = (noise.xy * 2.0 - 1.0) * roughness * 0.1;
    reflectDir.xy += jitter;

    float3 rayOrigin = viewPos + reflectDir * 0.1;

    float2 hitUV;
    float hitDepth;
    bool hit = RayMarchHiZ(rayOrigin, reflectDir, hitUV, hitDepth);

    if (hit)
    {
        float3 reflectionColor = gSceneColor.SampleLevel(gLinearClampSampler, hitUV, 0).rgb;

        // 边缘淡出
        float2 edgeDist = min(hitUV, 1.0 - hitUV);
        float edgeFade = smoothstep_edge(0.0, 0.1, min(edgeDist.x, edgeDist.y));

        // 距离淡出
        float3 hitWorldPos = ReconstructViewPos(hitUV, hitDepth);
        float dist = length(hitWorldPos - viewPos);
        float distFade = 1.0 - smoothstep_edge(gFadeStart, gFadeEnd, dist);

        // 菲涅尔
        float NdotV = saturate_fast(dot(normal, viewDir));
        float3 F0 = lerp(float3(0.04, 0.04, 0.04), gbuf0.rgb, metallic);
        float3 fresnel = F_SchlickRoughness(NdotV, F0, roughness);

        float alpha = edgeFade * distFade;
        gOutput[pixel] = float4(reflectionColor * fresnel, alpha);
        gHitUV[pixel] = hitUV;
    }
    else
    {
        gOutput[pixel] = float4(0, 0, 0, 0);
        gHitUV[pixel] = float2(0, 0);
    }
}
