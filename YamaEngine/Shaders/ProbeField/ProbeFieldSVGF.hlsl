// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - ProbeField SVGF (Spatiotemporal Variance-Guided Filtering)
// --------------------------------------------------------------------------
// SVGF 风格时空去噪: 对低采样的 GI/RT 结果进行保边滤波。
//   - 波间 (atrous) 边缘感知滤波: 多次迭代, 每次步宽翻倍, 5 抽头十字核
//   - 边缘停止权重: 基于深度、法线、亮度的差异衰减权重, 保留几何边缘
//   - 方差引导: 从时序历史估计噪声方差, 高方差区域增大滤波半径
//
// 入口点: SvgfCS  (CS, [numthreads(8,8,1)])
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// SVGF 常量缓冲 (对应 C# ProbeFieldSvgfCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer SvgfConstants : register(b2)
{
    float2 InvResolution;
    int   Iteration;       // 当前 atrous 迭代索引
    int   KernelRadius;    // 核半径
    float DepthSigma;      // 深度边缘停止阈值
    float NormalSigma;     // 法线边缘停止阈值
    float LuminanceSigma;  // 亮度边缘停止阈值
    float TemporalAlpha;   // 时序累积系数
    int   StepWidth;       // 当前 atrous 步宽 (像素)
    float Pad0, Pad1;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4> gInputColor    : register(t0); // 输入辐照度 (待去噪)
Texture2D<float4> gGeometryBufferNormal : register(t1); // 法线
Texture2D<float>  gDepthBuffer   : register(t2); // 深度
Texture2D<float4> gGeometryBufferAlbedo : register(t3); // 反照率 (用于亮度引导)
Texture2D<float>  gVariance      : register(t4); // 方差图 (时序估计)
Texture2D<float4> gHistoryColor  : register(t5); // 历史帧 (时序累积)
Texture2D<float2> gMotionVectors : register(t6); // 运动向量
Texture2D<float>  gHiZDepth      : register(t7); // Hi-Z 深度

RWTexture2D<float4> gOutputColor   : register(u0); // 去噪后辐照度
RWTexture2D<float>  gOutputVariance : register(u1); // 更新后方差

SamplerState gPointClampSampler  : register(s0);
SamplerState gLinearClampSampler : register(s2);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 线性深度
float LinearDepth(float depth)
{
    float near = gCameraPos.w;
    float far = gCameraForward.w;
    return (near * far) / (far - depth * (far - near));
}

// 边缘停止权重: 深度
float EdgeWeightDepth(float centerDepth, float sampleDepth, float stepWorld)
{
    float depthDiff = abs(centerDepth - sampleDepth);
    float expectedDiff = abs(centerDepth) * DepthSigma * 0.01;
    return exp(-depthDiff / max(expectedDiff, SHANYU_EPSILON));
}

// 边缘停止权重: 法线
float EdgeWeightNormal(float3 centerNormal, float3 sampleNormal)
{
    float dotN = dot(centerNormal, sampleNormal);
    return pow(max(0.0, dotN), NormalSigma);
}

// 边缘停止权重: 亮度
float EdgeWeightLuminance(float centerLum, float sampleLum, float variance)
{
    float lumDiff = abs(centerLum - sampleLum);
    float sigma = LuminanceSigma * sqrt(max(variance, 0.0)) + 0.001;
    return exp(-lumDiff / sigma);
}

// ─────────────────────────────────══════════════════════════════════════─
// SVGF atrous 波间滤波
// 5 抽头十字核 (中心 + 4 邻域), 边缘感知权重
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void SvgfCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gInputColor.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    // 中心样本
    float4 centerColor = gInputColor[pixel];
    float  centerDepth = gDepthBuffer[pixel];
    float3 centerNormal = DecodeNormalOctahedral(gGeometryBufferNormal[pixel].xy);
    float  centerLum = Luminance(centerColor.rgb);
    float  variance = gVariance[pixel];

    // 第一迭代: 时序累积 (将历史帧与当前帧混合, 估计方差)
    if (Iteration == 0)
    {
        float2 motion = gMotionVectors[pixel];
        float2 historyUV = (pixel + 0.5) * InvResolution - motion;

        bool validHistory = all(historyUV > 0.0) && all(historyUV < 1.0);

        if (validHistory)
        {
            float4 historyColor = gHistoryColor.SampleLevel(gPointClampSampler, historyUV, 0);

            // 深度一致性检查 (避免拖影)
            uint2 histPixel = uint2(historyUV * resolution);
            float histDepth = gDepthBuffer.Load(int3(clamp(histPixel, 0, resolution - 1), 0));
            float depthDiff = abs(centerDepth - histDepth);
            float depthThreshold = 0.01;
            bool depthConsistent = depthDiff < depthThreshold;

            if (depthConsistent)
            {
                // 指数移动平均 (EMA) 时序累积
                float alpha = 1.0 - TemporalAlpha;
                centerColor.rgb = lerp(historyColor.rgb, centerColor.rgb, alpha);

                // 更新方差 (递归估计)
                float3 diff = centerColor.rgb - historyColor.rgb;
                float newVar = Luminance(diff * diff);
                variance = lerp(newVar, variance * 0.9, alpha);
            }
            else
            {
                // 深度不一致: 重置方差
                variance = 1.0;
            }
        }
        else
        {
            variance = 1.0;
        }

        gOutputColor[pixel] = centerColor;
        gOutputVariance[pixel] = variance;
        return;
    }

    // atrous 波间滤波 (迭代 1+)
    // 5 抽头: 中心权重 1.0, 4 邻域各 0.5, 边缘停止权重调制
    float stepWorld = float(StepWidth);
    float2 step = float2(stepWorld, stepWorld) * InvResolution;

    float3 colorAccum = centerColor.rgb * 1.0;
    float weightAccum = 1.0;

    // 4 个邻域方向
    static const float2 offsets[4] = {
        float2( 1,  0), float2(-1,  0),
        float2( 0,  1), float2( 0, -1)
    };

    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float2 sampleUV = (pixel + 0.5) * InvResolution + offsets[i] * step;
        sampleUV = saturate_fast(sampleUV);

        uint2 samplePixel = uint2(sampleUV * resolution);
        if (any(samplePixel >= resolution))
            continue;

        float4 sampleColor = gInputColor[samplePixel];
        float  sampleDepth = gDepthBuffer[samplePixel];
        float3 sampleNormal = DecodeNormalOctahedral(gGeometryBufferNormal[samplePixel].xy);
        float  sampleLum = Luminance(sampleColor.rgb);
        float  sampleVar = gVariance[samplePixel];

        // 边缘停止权重
        float wDepth = EdgeWeightDepth(centerDepth, sampleDepth, stepWorld);
        float wNormal = EdgeWeightNormal(centerNormal, sampleNormal);
        float wLum = EdgeWeightLuminance(centerLum, sampleLum, max(variance, sampleVar));

        // 综合权重 (邻域基础权重 0.5)
        float weight = 0.5 * wDepth * wNormal * wLum;

        colorAccum += sampleColor.rgb * weight;
        weightAccum += weight;
    }

    float3 filtered = colorAccum / max(weightAccum, SHANYU_EPSILON);

    // 方差缩减 (滤波后方差降低)
    float varianceReduction = 0.5;
    float filteredVariance = variance * varianceReduction;

    gOutputColor[pixel] = float4(filtered, centerColor.a);
    gOutputVariance[pixel] = filteredVariance;
}
