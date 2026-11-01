// ════════════════════════════════════════════════════════════════════════════
// SSAO (Screen Space Ambient Occlusion) — 屏幕空间环境光遮蔽
// 使用: 半球采样核 + 深度重建 + 双边滤波
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

Texture2D<float>  gDepthTex  : register(t0);
Texture2D<float3> gNormalTex : register(t1);
Texture2D<float4> gBlueNoise : register(t2);

RWTexture2D<float4> gOutput : register(u0);

cbuffer SSAOConstants : register(b2)
{
    float  gRadius;          // 采样半径 (世界空间)
    float  gBias;            // 偏移避免自阴影
    float  gPower;           // 强度幂
    uint   gSampleCount;     // 采样数
    float4 gKernel[64];      // 采样核 (球面分布)
};

float3 ReconstructViewPosFromDepth(float2 uv, float depth)
{
    return ReconstructViewPos(uv, depth);
}

[numthreads(8, 8, 1)]
void SSAOCS(uint3 dtid : SV_DispatchThreadID)
{
    uint2 pixel = dtid.xy;
    if (pixel.x >= (uint)gScreenParams.x || pixel.y >= (uint)gScreenParams.y) return;

    float2 uv = (pixel + 0.5) * gScreenParams.zw;
    float depth = gDepthTex.Load(int3(pixel, 0));
    if (depth >= 1.0) { gOutput[pixel] = float4(1, 1, 1, 1); return; }

    float3 viewPos = ReconstructViewPosFromDepth(uv, depth);
    float3 normal = DecodeNormalOctahedral(gNormalTex.Load(int3(pixel, 0)).xy);

    // 视空间切线基
    float3 up = abs(normal.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, normal));
    float3 bitangent = cross(normal, tangent);
    float3x3 TBN = float3x3(tangent, bitangent, normal);

    // 蓝噪声旋转
    float4 noise = gBlueNoise.Load(int3(pixel % 256, 0));
    float angle = noise.x * SHANYU_TWO_PI;
    float cosA = cos(angle), sinA = sin(angle);
    float2x2 rotMat = float2x2(cosA, -sinA, sinA, cosA);

    float occlusion = 0.0;
    float radiusScale = gRadius / -viewPos.z; // 透视投影调整

    [loop]
    for (uint i = 0; i < gSampleCount; i++)
    {
        // 采样方向: 从半球核中取
        float3 sampleDir = mul(gKernel[i].xyz, TBN);
        sampleDir = normalize(sampleDir);

        // 旋转采样方向
        sampleDir.xy = mul(rotMat, sampleDir.xy);

        float3 samplePos = viewPos + sampleDir * gRadius;

        // 投影到屏幕空间
        float4 projSample = mul(gProjMatrix, float4(samplePos, 1.0));
        projSample.xyz /= projSample.w;
        float2 sampleUV = projSample.xy * 0.5 + 0.5;
        sampleUV.y = 1.0 - sampleUV.y;

        if (any(sampleUV < 0.0) || any(sampleUV > 1.0)) continue;

        float sampleDepth = gDepthTex.SampleLevel(gPointClampSampler, sampleUV, 0).x;
        float3 sampleViewPos = ReconstructViewPosFromDepth(sampleUV, sampleDepth);

        // 距离检查避免远距离误判
        float rangeCheck = smoothstep_edge(1.0, 0.0, abs(samplePos.z - sampleViewPos.z) / gRadius);

        // 遮蔽判定
        if (sampleViewPos.z >= samplePos.z + gBias)
        {
            occlusion += rangeCheck;
        }
    }

    occlusion = 1.0 - saturate_fast(occlusion / float(gSampleCount));
    occlusion = pow(occlusion, gPower);

    gOutput[pixel] = float4(occlusion.xxx, 1.0);
}
