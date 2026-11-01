// ════════════════════════════════════════════════════════════════════════════
// Yama Engine — Temporal Super Resolution (TSR)
// 时序超分辨率: 运动矢量 + 历史重建累积 + 时序上采样 (色度修正) + 锐化
// 基于 UE5 TSR / TAAU 思路, 输入低分辨率渲染, 输出高分辨率结果
// 目标: Shader Model 6.6 (DXC 编译, wave intrinsics, lerp 历史累积)
// 对应 C#: YamaEngine.Rendering.TemporalSuperResolution
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// TSR 常量 (匹配 C# TSRCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer TSRCB : register(b0)
{
    int2   gTSRInputResolution;      // 低分辨率输入
    int2   gTSROutputResolution;     // 高分辨率输出
    float2 gTSRInvInputResolution;
    float2 gTSRInvOutputResolution;
    float2 gTSRJitter;               // 当前帧抖动
    float2 gTSRPrevJitter;           // 上一帧抖动
    float  gTSRSharpness;
    float  gTSRResetBlendFactor;      // 历史重置混合因子
    uint   gTSRFrameIndex;
    uint   gTSRResetHistory;
    float  gTSRPad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────

// 输入 (低分辨率)
Texture2D<float4> gTSRInputColor : register(t0);   // 当前帧低分辨率颜色
Texture2D<float>  gTSRInputDepth : register(t1);   // 当前帧深度
Texture2D<float>  gTSRPrevDepth  : register(t2);   // 上一帧深度

// 速度缓冲
Texture2D<float2> gTSRInputVelocity  : register(t3); // 几何 Pass 产生的速度
Texture2D<float2> gTSRCameraVelocity : register(t4); // 相机速度 (无几何运动)

// 历史缓冲
Texture2D<float4> gTSRHistoryColor : register(t5);   // 上一帧累积历史
Texture2D<float>  gTSRHistoryDepth : register(t6);   // 上一帧深度 (验证)

// 曝光
Texture2D<float> gTSRExposure : register(t7);

// UAV 输出
RWTexture2D<float4> gTSROutputColor   : register(u0);  // 高分辨率输出
RWTexture2D<float4> gTSRHistoryUAV    : register(u1);  // 更新历史
RWTexture2D<float2> gTSRVelocityUAV   : register(u2);  // 合并速度
RWTexture2D<float>  gTSRFlattenDepthUAV : register(u3); // 深度金字塔底


// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// RGB <-> YCoCg 色彩空间转换 (TSR 在 YCoCg 空间工作以分离亮度/色度)
float3 RGBtoYCoCg(float3 rgb)
{
    float Y  = 0.25 * rgb.r + 0.5 * rgb.g + 0.25 * rgb.b;
    float Co = 0.5 * rgb.r - 0.5 * rgb.b;
    float Cg = -0.25 * rgb.r + 0.5 * rgb.g - 0.25 * rgb.b;
    return float3(Y, Co, Cg);
}

float3 YCoCgtoRGB(float3 ycocg)
{
    float Y = ycocg.x;
    float Co = ycocg.y;
    float Cg = ycocg.z;
    float R = Y + Co - Cg;
    float G = Y + Cg;
    float B = Y - Co - Cg;
    return float3(R, G, B);
}

// AABB 裁剪历史颜色 (邻域最小/最大约束, 防止鬼影)
float3 ClipHistory(float3 history, float3 minColor, float3 maxColor)
{
    float3 center = 0.5 * (minColor + maxColor);
    float3 extents = 0.5 * (maxColor - minColor) + SHANYU_EPSILON;
    float3 offset = history - center;
    float3 ts = extents / max(abs(offset), SHANYU_EPSILON);
    float t = min(min(ts.x, ts.y), ts.z);
    t = saturate_fast(t);
    return center + offset * t;
}

// 计算速度 (几何速度 + 相机速度取最大)
float2 ComputeVelocity(uint2 pixel)
{
    float2 geometryVel = gTSRInputVelocity.Sample(gPointClampSampler,
        (pixel + 0.5) * gTSRInvOutputResolution);
    float2 cameraVel = gTSRCameraVelocity.Sample(gPointClampSampler,
        (pixel + 0.5) * gTSRInvOutputResolution);

    // 取几何速度与相机速度中模长较大者
    float geomLen = dot(geometryVel, geometryVel);
    float camLen = dot(cameraVel, cameraVel);
    return geomLen > camLen ? geometryVel : cameraVel;
}


// ════════════════════════════════════════════════════════════════════════════
// VelocityCS — 运动矢量计算
// 合并几何速度与相机速度, 输出最终运动矢量 (高分辨率)
// 入口点: VelocityCS (匹配 C# RHIShaderBytecode("VelocityCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void VelocityCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gTSROutputResolution.x || dispatchID.y >= (uint)gTSROutputResolution.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gTSRInvOutputResolution;

    // 几何速度 (来自 G-Buffer 速度通道, 已含上帧位置差)
    float2 geometryVel = gTSRInputVelocity.Sample(gPointClampSampler, uv);

    // 相机速度: 从深度重投影
    float depth = gTSRInputDepth.Sample(gPointClampSampler, uv);
    float2 velocity = geometryVel;

    // 若无几何速度 (天空), 用相机重投影速度
    if (dot(geometryVel, geometryVel) < SHANYU_EPSILON && depth < 1.0)
    {
        // 当前帧 NDC (含抖动)
        float2 curNDC = uv * 2.0 - 1.0;
        // 当前帧无抖动 NDC
        float2 curNDCNoJitter = curNDC - gTSRJitter * 2.0;

        // 重建世界位置并投影到上帧 (使用 invViewProj/prevViewProj)
        float3 worldPos = ReconstructWorldPos(uv, depth);
        float4 prevClip = mul(gPrevViewProjMatrix, float4(worldPos, 1.0));
        float2 prevNDC = prevClip.xy / prevClip.w;
        prevNDC += gTSRPrevJitter * 2.0;

        velocity = prevNDC - curNDCNoJitter;
    }

    gTSRVelocityUAV[pixel] = velocity;
}


// ════════════════════════════════════════════════════════════════════════════
// AccumulateCS — 历时累积 (历史重建)
// 在低分辨率下用运动矢量重投影历史, 邻域约束裁剪鬼影, 累积当前帧
// 入口点: AccumulateCS (匹配 C# RHIShaderBytecode("AccumulateCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void AccumulateCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gTSRInputResolution.x || dispatchID.y >= (uint)gTSRInputResolution.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gTSRInvInputResolution;

    // 当前帧颜色 (YCoCg 空间)
    float3 currentColor = RGBtoYCoCg(gTSRInputColor.Sample(gPointClampSampler, uv).rgb);

    // 邻域 3x3 最小/最大 (用于历史裁剪)
    float3 colorMin = currentColor;
    float3 colorMax = currentColor;
    [unroll]
    for (int x = -1; x <= 1; x++)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            if (x == 0 && y == 0) continue;
            float2 sampleUV = uv + float2(x, y) * gTSRInvInputResolution;
            float3 sampleColor = RGBtoYCoCg(gTSRInputColor.Sample(gPointClampSampler, sampleUV).rgb);
            colorMin = min(colorMin, sampleColor);
            colorMax = max(colorMax, sampleColor);
        }
    }

    // 用运动矢量重投影历史 (历史在高分辨率, 下采样到输入分辨率采样)
    float2 velocity = gTSRVelocityUAV[pixel];
    float2 historyUV = uv + velocity;
    historyUV = saturate_fast(historyUV);

    // 采样历史 (双线性)
    float3 historyColor = RGBtoYCoCg(gTSRHistoryColor.Sample(gLinearClampSampler, historyUV).rgb);

    // 邻域约束裁剪 (防止鬼影)
    historyColor = ClipHistory(historyColor, colorMin, colorMax);

    // 自适应混合权重 (速度越大越信任当前帧)
    float velocityMag = length(velocity * gTSROutputResolution);
    float blendFactor = lerp(0.1, 0.8, saturate_fast(velocityMag * 0.1));

    // 历史重置 (场景切换/相机跳跃)
    if (gTSRResetHistory != 0)
        blendFactor = 1.0;
    blendFactor = max(blendFactor, gTSRResetBlendFactor);

    // 累积
    float3 accumulated = lerp(historyColor, currentColor, blendFactor);

    // 更新历史
    gTSRHistoryUAV[pixel] = float4(YCoCgtoRGB(accumulated), 1.0);
}


// ════════════════════════════════════════════════════════════════════════════
// UpscaleCS — 时序上采样 + 色度修正
// 将低分辨率累积结果上采样到高分辨率, 修正色度失真 (YCoCg 色度锁定)
// 入口点: UpscaleCS (匹配 C# RHIShaderBytecode("UpscaleCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void UpscaleCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gTSROutputResolution.x || dispatchID.y >= (uint)gTSROutputResolution.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gTSRInvOutputResolution;

    // 减去抖动偏移 (抖动在低分辨率, 上采样后需移除)
    float2 uvUnjittered = uv - gTSRJitter * gTSRInvInputResolution;

    // 双线性上采样累积历史 (低 -> 高)
    float3 upscaledColor = gTSRHistoryColor.Sample(gLinearClampSampler, uvUnjittered).rgb;

    // --- 色度修正 (Chroma Correction) ---
    // 在 YCoCg 空间, 用高分辨率当前帧的色度修正上采样的色度模糊
    float3 currentYCoCg = RGBtoYCoCg(gTSRInputColor.Sample(gLinearClampSampler, uvUnjittered).rgb);
    float3 upscaledYCoCg = RGBtoYCoCg(upscaledColor);

    // 锁定色度: 取上采样的亮度 (Y), 用当前帧高频色度修正 (Co, Cg)
    float luma = upscaledYCoCg.x;
    float2 chroma = upscaledYCoCg.yz;

    // 当前帧色度 (低分辨率, 作为参考)
    float2 currentChroma = currentYCoCg.yz;

    // 色度细节增强: 当邻域色度方差大时, 信任当前帧色度
    float2 chromaMin = currentChroma;
    float2 chromaMax = currentChroma;
    [unroll]
    for (int x = -1; x <= 1; x++)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            float2 sampleUV = uvUnjittered + float2(x, y) * gTSRInvInputResolution;
            float2 sampleChroma = RGBtoYCoCg(gTSRInputColor.Sample(gLinearClampSampler, sampleUV).rgb).yz;
            chromaMin = min(chromaMin, sampleChroma);
            chromaMax = max(chromaMax, sampleChroma);
        }
    }
    float2 chromaRange = chromaMax - chromaMin;
    float chromaVariance = dot(chromaRange, chromaRange);

    // 高色度方差区域: 用当前帧色度替换 (恢复边缘色彩)
    float chromaBlend = saturate_fast(chromaVariance * 4.0);
    chroma = lerp(chroma, currentChroma, chromaBlend * 0.5);

    float3 correctedYCoCg = float3(luma, chroma);
    float3 outputColor = YCoCgtoRGB(correctedYCoCg);

    gTSROutputColor[pixel] = float4(outputColor, 1.0);
}


// ════════════════════════════════════════════════════════════════════════════
// SharpenCS — 锐化 Pass
// 对上采样结果应用锐化滤镜 (增强细节, 补偿时序模糊)
// 入口点: SharpenCS (匹配 C# RHIShaderBytecode("SharpenCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SharpenCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gTSROutputResolution.x || dispatchID.y >= (uint)gTSROutputResolution.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gTSRInvOutputResolution;

    // 中心像素
    float3 center = gTSROutputColor[pixel].rgb;

    // 4 邻域采样 (拉普拉斯锐化)
    float3 sum = 0.0;
    sum += gTSROutputColor.Sample(gPointClampSampler, uv + float2(1, 0) * gTSRInvOutputResolution).rgb;
    sum += gTSROutputColor.Sample(gPointClampSampler, uv + float2(-1, 0) * gTSRInvOutputResolution).rgb;
    sum += gTSROutputColor.Sample(gPointClampSampler, uv + float2(0, 1) * gTSRInvOutputResolution).rgb;
    sum += gTSROutputColor.Sample(gPointClampSampler, uv + float2(0, -1) * gTSRInvOutputResolution).rgb;

    // 锐化: center + sharpness * (center - average(neighbor))
    float3 average = sum * 0.25;
    float3 sharpened = center + gTSRSharpness * (center - average);

    // 防止负值 (HDR 下允许, 但保护极端值)
    sharpened = max(sharpened, 0.0);

    gTSROutputColor[pixel] = float4(sharpened, 1.0);
}
