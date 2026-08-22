// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - ProbeField Temporal Accumulation
// --------------------------------------------------------------------------
// 时序累积 (TAA 重投影): 将当前帧去噪后的 GI 结果与历史帧累积,
// 使用运动向量重投影历史位置, 应用深度/法线一致性检查避免拖影,
// 并使用邻域裁剪 (neighborhood clamping) 抑制鬼影。
//
// 入口点: TemporalCS  (CS, [numthreads(8,8,1)])
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 时序累积常量缓冲
// ──────────────────────────────────────────────────────────────────────────
cbuffer TemporalConstants : register(b2)
{
    float4x4 ViewProjMatrix;
    float4x4 InvViewProj;
    float4x4 PrevViewProjMatrix;
    float3   CameraPos;
    float    TemporalAlpha;       // 历史权重 (越大越平滑)
    float2   InvResolution;
    int      FrameIndex;
    float2   Jitter;
    float    TemporalPad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4> gCurrentColor   : register(t0); // 当前帧去噪后 GI
Texture2D<float4> gHistoryBuffer  : register(t1); // 历史帧累积结果
Texture2D<float2> gMotionVectors  : register(t2); // 运动向量
Texture2D<float>  gDepthBuffer    : register(t3); // 当前深度
Texture2D<float>  gPrevDepth      : register(t4); // 上一帧深度
Texture2D<float4>  gGeometryBufferNormal : register(t5); // 法线
Texture2D<float4>  gGeometryBufferAlbedo : register(t6); // 反照率
Texture2D<float4>  gBlueNoise     : register(t7); // 蓝噪声

RWTexture2D<float4> gOutputColor   : register(u0); // 累积结果
RWTexture2D<float2> gOutputVelocity : register(u1); // 输出运动向量

SamplerState gPointClampSampler  : register(s0);
SamplerState gLinearClampSampler : register(s2);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 3x3 邻域最小/最大值 (用于邻域裁剪)
void NeighborhoodMinMax(float2 uv, out float3 colorMin, out float3 colorMax)
{
    colorMin = float3(SHANYU_FLT_MAX, SHANYU_FLT_MAX, SHANYU_FLT_MAX);
    colorMax = float3(-SHANYU_FLT_MAX, -SHANYU_FLT_MAX, -SHANYU_FLT_MAX);

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 offset = float2(x, y) * InvResolution;
            float3 sampleColor = gCurrentColor.SampleLevel(gPointClampSampler, uv + offset, 0).rgb;
            colorMin = min(colorMin, sampleColor);
            colorMax = max(colorMax, sampleColor);
        }
    }
}

// RGB <-> YCoCg 色彩空间 (邻域裁剪在亮度色度空间更稳定)
float3 RGBToYCoCg(float3 rgb)
{
    float y = 0.25 * rgb.r + 0.5 * rgb.g + 0.25 * rgb.b;
    float co = 0.5 * rgb.r - 0.5 * rgb.b;
    float cg = -0.25 * rgb.r + 0.5 * rgb.g - 0.25 * rgb.b;
    return float3(y, co, cg);
}

float3 YCoCgToRGB(float3 ycocg)
{
    float r = ycocg.x + ycocg.y - ycocg.z;
    float g = ycocg.x + ycocg.z;
    float b = ycocg.x - ycocg.y - ycocg.z;
    return float3(r, g, b);
}

// ─────────────────────────────────────────────────═════════════════════════
// 时序累积主着色器
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void TemporalCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchID.xy;
    uint2 resolution;
    gCurrentColor.GetDimensions(resolution.x, resolution.y);

    if (pixel.x >= resolution.x || pixel.y >= resolution.y)
        return;

    float2 uv = (pixel + 0.5) * InvResolution;

    // 当前帧颜色
    float4 currentColor = gCurrentColor[pixel];
    float  currentDepth = gDepthBuffer[pixel];
    float3 currentNormal = DecodeNormalOctahedral(gGeometryBufferNormal[pixel].xy);

    // 运动向量重投影: 找到当前像素在上一帧的位置
    float2 motion = gMotionVectors[pixel];
    float2 historyUV = uv - motion;

    // 默认输出当前帧 (无有效历史时)
    float3 result = currentColor.rgb;
    float resultAlpha = currentColor.a;

    // 检查历史是否有效
    bool validHistory = all(historyUV >= 0.0) && all(historyUV <= 1.0);

    if (validHistory && currentDepth > 0.0)
    {
        // 采样历史帧
        float3 historyColor = gHistoryBuffer.SampleLevel(gLinearClampSampler, historyUV, 0).rgb;
        float  historyAlpha = gHistoryBuffer.SampleLevel(gLinearClampSampler, historyUV, 0).a;

        // 深度一致性检查
        uint2 histPixel = uint2(historyUV * resolution);
        histPixel = clamp(histPixel, 0, resolution - 1);
        float historyDepth = gPrevDepth[histPixel];
        float depthDiff = abs(currentDepth - historyDepth);
        float depthThreshold = 0.02;
        bool depthConsistent = depthDiff < depthThreshold;

        // 法线一致性检查
        float3 historyNormal = DecodeNormalOctahedral(gGeometryBufferNormal[histPixel].xy);
        float normalDot = dot(currentNormal, historyNormal);
        bool normalConsistent = normalDot > 0.9;

        if (depthConsistent && normalConsistent)
        {
            // 邻域裁剪 (YCoCg 空间): 将历史颜色裁剪到当前邻域的包围盒
            float3 colorMin, colorMax;
            NeighborhoodMinMax(uv, colorMin, colorMax);

            // 扩展裁剪盒 (容忍度)
            float3 colorCenter = (colorMin + colorMax) * 0.5;
            float3 colorExtent = (colorMax - colorMin) * 0.5;
            float clipExtent = 1.0; // 扩展系数
            colorMin = colorCenter - colorExtent * clipExtent;
            colorMax = colorCenter + colorExtent * clipExtent;

            // 转换到 YCoCg 裁剪
            float3 histYCoCg = RGBToYCoCg(historyColor);
            float3 minYCoCg = RGBToYCoCg(colorMin);
            float3 maxYCoCg = RGBToYCoCg(colorMax);

            histYCoCg = clamp(histYCoCg, minYCoCg, maxYCoCg);
            historyColor = YCoCgToRGB(histYCoCg);

            // 自适应混合因子: 当前帧置信度低时多信任历史
            float confidence = currentColor.a;
            float alpha = lerp(0.05, TemporalAlpha, confidence);

            // 指数移动平均累积
            result = lerp(currentColor.rgb, historyColor, alpha);
            resultAlpha = lerp(confidence, historyAlpha, alpha);
        }
        else
        {
            // 不一致: 完全使用当前帧 (避免拖影/鬼影)
            result = currentColor.rgb;
            resultAlpha = currentColor.a;
        }
    }
    else
    {
        // 无有效历史或天空像素
        result = currentColor.rgb;
        resultAlpha = currentColor.a;
    }

    gOutputColor[pixel] = float4(result, resultAlpha);
    gOutputVelocity[pixel] = motion;
}
