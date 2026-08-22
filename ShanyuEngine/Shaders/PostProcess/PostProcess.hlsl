// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - Post Processing Stack
// 后处理栈: 自动曝光 -> 景深 DoF -> 运动模糊 -> Bloom 提取/模糊/合成 ->
// 镜头畸变与色散 -> 色调映射/颜色分级。
// 每个阶段为独立计算着色器, 通过 ping-pong 纹理串联。
// 与 C# PostProcessingStack.MakeCS 入口点对应。
// Shader Model 6.6 (DXC)
// 入口点: ExposureCS, DoFCS, MotionBlurCS, BloomExtractCS, BloomBlurCS,
//         BloomCombineCS, LensCS, TonemapCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# PostProcessCB / PostProcessBloomCB / PostProcessDoFCB /
//            PostProcessMotionBlurCB / PostProcessTonemapCB 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer PostProcessCB : register(b0)
{
    float g_PP_Time;
    float g_PP_DeltaTime;
    int   g_PP_FrameIndex;
    float g_PP_Pad;
};

cbuffer PostProcessExposureCB : register(b2)
{
    float g_EXP_Key;
    float g_EXP_Compensation;
    float g_EXP_AdaptationSpeed;
    float g_EXP_MinExposure;
    float g_EXP_MaxExposure;
    float g_EXP_ManualExposure;
    int   g_EXP_UseAutoExposure;
    float g_EXP_Pad0;
};

cbuffer PostProcessBloomCB : register(b2)
{
    float g_BLOOM_Threshold;
    float g_BLOOM_Intensity;
    float g_BLOOM_Radius;
    float3 g_BLOOM_Tint;
    float g_BLOOM_Pad;
};

cbuffer PostProcessDoFCB : register(b2)
{
    float4x4 g_DOF_ProjMatrix;
    float g_DOF_FocusDistance;
    float g_DOF_Aperture;
    float g_DOF_MaxBokehRadius;
    float g_DOF_NearFocusStart;
    float g_DOF_FarFocusStart;
    int   g_DOF_BokehShape;
    float g_DOF_Pad0;
    float g_DOF_Pad1;
    float g_DOF_Pad2;
};

cbuffer PostProcessMotionBlurCB : register(b2)
{
    float g_MB_Strength;
    float g_MB_MaxVelocity;
    int   g_MB_SampleCount;
    float g_MB_Pad0;
    float4x4 g_MB_PrevViewProj;
    float4x4 g_MB_InvViewProj;
    float g_MB_Pad1;
    float g_MB_Pad2;
};

cbuffer PostProcessTonemapCB : register(b2)
{
    float g_TM_Exposure;
    int   g_TM_TonemapOperator;
    int   g_TM_ApplySRGB;
    int   g_TM_EnableColorGrading;
    float g_TM_Saturation;
    float g_TM_Contrast;
    float g_TM_Gain;
    float g_TM_Pad0;
    float3 g_TM_ColorOffset;
    float g_TM_WhiteBalanceTemp;
    float g_TM_WhiteBalanceTint;
    float g_TM_LutIntensity;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
// 通用输入输出 (各 pass 复用相同 SRV/UAV 槽, 由 C# 端绑定不同纹理)
Texture2D<float4>  g_SceneColorSRV    : register(t0); // HDR 场景颜色
Texture2D<float>   g_SceneDepthSRV     : register(t1); // 场景深度
Texture2D<float4>  g_MotionVectorsSRV  : register(t2); // 运动矢量
Texture2D<float4>  g_SourceSRV         : register(t3); // 上一 pass 输出
Texture2D<float4>  g_BloomPyramidSRV   : register(t4); // Bloom 金字塔
Texture2D<float4>  g_LutSRV            : register(t5); // 颜色查找表
Texture2D<float4>  g_LensDirtSRV       : register(t6); // 镜头污渍

RWTexture2D<float4> g_OutputUAV        : register(u0); // 输出
RWTexture2D<float4> g_TempUAV          : register(u1); // 临时纹理
RWTexture2D<float4> g_BloomPyramidUAV  : register(u2); // Bloom 金字塔 (多 mip)
RWStructuredBuffer<float> g_ExposureBuffer : register(u0); // 曝光值 (1 float)

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 计算景深圈 (CoC, Circle of Confusion)
float ComputeCoC(float depth, float focusDistance, float aperture, float maxRadius)
{
    // CoC 与 (深度 - 对焦距离) 成正比, 受光圈控制
    float coc = (depth - focusDistance) * aperture / max(depth, 0.01);
    return clamp(coc, -maxRadius, maxRadius);
}

// 计算 Bokeh 形状权重 (圆形/六边形/八角形)
float BokehWeight(float2 offset, int shape)
{
    float dist = length(offset);
    if (shape == 0) return 1.0; // 圆形
    if (shape == 1) // 六边形
    {
        float angle = atan2(offset.y, offset.x);
        float hexagon = cos(angle * 6.0) * 0.5 + 0.5;
        return lerp(0.3, 1.0, hexagon);
    }
    if (shape == 2) // 八角形
    {
        float angle = atan2(offset.y, offset.x);
        float octagon = cos(angle * 8.0) * 0.5 + 0.5;
        return lerp(0.3, 1.0, octagon);
    }
    return 1.0;
}

// 白平衡 (色温/色调)
float3 WhiteBalance(float3 color, float temp, float tint)
{
    // 色温: 暖 (正) -> 偏黄红, 冷 (负) -> 偏蓝
    float3 warmTint = float3(1.0, 0.95, 0.85);
    float3 coolTint = float3(0.85, 0.95, 1.1);
    float3 tempColor = lerp(coolTint, warmTint, saturate(temp * 0.5 + 0.5));
    color *= tempColor;

    // 色调: 偏绿 (正) / 偏品红 (负)
    color.g *= 1.0 + tint * 0.1;
    color.r *= 1.0 - tint * 0.05;
    color.b *= 1.0 - tint * 0.05;

    return color;
}

// 颜色分级
float3 ColorGrade(float3 color, float saturation, float contrast, float gain,
                  float3 colorOffset, float lutIntensity)
{
    // 饱和度
    float lum = Luminance(color);
    color = lerp(lum.xxx, color, saturation);

    // 对比度 (围绕中灰)
    color = (color - 0.5) * contrast + 0.5;

    // 增益 (亮度)
    color *= gain;

    // 颜色偏移
    color += colorOffset;

    return saturate_fast(color);
}

// ════════════════════════════════════════════════════════════════════════════
// ExposureCS - 自动曝光 (计算场景平均亮度, 更新曝光值)
// 单线程: 对全屏降采样计算平均亮度, 应用自适应
// ════════════════════════════════════════════════════════════════════════════
[numthreads(1, 1, 1)]
void ExposureCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SceneColorSRV.GetDimensions(w, h);

    // 降采样采样: 以步长采样全屏, 计算对数平均亮度
    int stepX = max(1, (int)w / 64);
    int stepY = max(1, (int)h / 64);

    float logSum = 0.0;
    int sampleCount = 0;

    [loop]
    for (int y = 0; y < (int)h; y += stepY)
    {
        [loop]
        for (int x = 0; x < (int)w; x += stepX)
        {
            float3 color = g_SceneColorSRV.Load(int3(x, y, 0)).rgb;
            float lum = max(Luminance(color), 1e-6);
            logSum += log(lum);
            sampleCount++;
        }
    }

    float avgLuminance = sampleCount > 0 ? exp(logSum / max(sampleCount, 1)) : 0.5;

    // 计算目标曝光 (基于关键值)
    float targetExposure = log2(g_EXP_Key / max(avgLuminance, 1e-6)) + g_EXP_Compensation;

    // 读取上一帧曝光 (自适应平滑)
    float prevExposure = g_ExposureBuffer[0];

    float newExposure;
    if (g_EXP_UseAutoExposure != 0)
    {
        // 自适应: 指数趋近目标
        float adaptRate = 1.0 - exp(-g_EXP_AdaptationSpeed * g_PP_DeltaTime);
        newExposure = lerp(prevExposure, targetExposure, adaptRate);
        // 钳制范围
        newExposure = clamp(newExposure, g_EXP_MinExposure, g_EXP_MaxExposure);
    }
    else
    {
        newExposure = g_EXP_ManualExposure;
    }

    g_ExposureBuffer[0] = newExposure;
}

// ════════════════════════════════════════════════════════════════════════════
// DoFCS - 景深 (散景模糊)
// 基于 CoC 半径进行散景采样, 近处与远处分别模糊
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void DoFCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    float3 centerColor = g_SceneColorSRV.SampleLevel(gPointClampSampler, uv, 0).rgb;
    float depth = g_SceneDepthSRV.SampleLevel(gPointClampSampler, uv, 0);

    // 线性化深度
    float linearDepth = LinearizeDepth(depth, g_DOF_NearFocusStart, g_DOF_FarFocusStart * 4.0);

    // 计算 CoC
    float coc = ComputeCoC(linearDepth, g_DOF_FocusDistance, g_DOF_Aperture, g_DOF_MaxBokehRadius);
    float absCoC = abs(coc);

    if (absCoC < 0.5)
    {
        g_OutputUAV[pixel] = float4(centerColor, 1.0);
        return;
    }

    // 散景采样 (黄金角分布)
    float3 accumulated = 0.0;
    float totalWeight = 0.0;
    int sampleCount = 32;

    [loop]
    for (int i = 0; i < sampleCount; i++)
    {
        // 黄金角螺旋采样
        float goldenAngle = 2.39996323;
        float r = sqrt((i + 0.5) / sampleCount) * absCoC;
        float theta = i * goldenAngle;
        float2 offset = float2(cos(theta), sin(theta)) * r;

        // Bokeh 形状权重
        float shapeWeight = BokehWeight(offset, g_DOF_BokehShape);

        float2 sampleUV = uv + offset / float2(w, h);
        float3 sampleColor = g_SceneColorSRV.SampleLevel(gLinearClampSampler, sampleUV, 0).rgb;
        float sampleDepth = g_SceneDepthSRV.SampleLevel(gPointClampSampler, sampleUV, 0);
        float sampleLinear = LinearizeDepth(sampleDepth, g_DOF_NearFocusStart, g_DOF_FarFocusStart * 4.0);
        float sampleCoC = ComputeCoC(sampleLinear, g_DOF_FocusDistance, g_DOF_Aperture, g_DOF_MaxBokehRadius);

        // 近处物体遮挡远处 (CoC 符号处理)
        float weight = shapeWeight;
        if (coc < 0.0 && sampleCoC > 0.0)
            weight *= saturate(1.0 - absCoC / max(abs(sampleCoC), 0.01));

        accumulated += sampleColor * weight;
        totalWeight += weight;
    }

    float3 result = totalWeight > 0.0 ? accumulated / totalWeight : centerColor;
    g_OutputUAV[pixel] = float4(result, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// MotionBlurCS - 运动模糊 (基于运动矢量)
// 沿运动矢量方向采样累积
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void MotionBlurCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    // 读取运动矢量
    float4 motionVec = g_MotionVectorsSRV.SampleLevel(gPointClampSampler, uv, 0);
    float2 velocity = motionVec.xy * g_MB_Strength;

    // 限制最大速度
    float velLength = length(velocity);
    if (velLength > g_MB_MaxVelocity)
        velocity = velocity / velLength * g_MB_MaxVelocity;

    float3 centerColor = g_SceneColorSRV.SampleLevel(gPointClampSampler, uv, 0).rgb;

    if (velLength < 0.001)
    {
        g_OutputUAV[pixel] = float4(centerColor, 1.0);
        return;
    }

    // 沿运动方向采样
    float3 accumulated = centerColor;
    float totalWeight = 1.0;

    [loop]
    for (int i = 1; i < g_MB_SampleCount; i++)
    {
        float t = (float)i / g_MB_SampleCount;
        float2 offset = velocity * (t - 0.5);
        float2 sampleUV = uv + offset;

        if (all(sampleUV >= 0.0) && all(sampleUV <= 1.0))
        {
            float3 sampleColor = g_SceneColorSRV.SampleLevel(gLinearClampSampler, sampleUV, 0).rgb;
            // 中心权重高
            float weight = 1.0 - abs(t - 0.5) * 2.0;
            accumulated += sampleColor * weight;
            totalWeight += weight;
        }
    }

    g_OutputUAV[pixel] = float4(accumulated / totalWeight, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// BloomExtractCS - 提取高亮区域 (阈值过滤)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void BloomExtractCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_BloomPyramidUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    float3 color = g_SceneColorSRV.SampleLevel(gLinearClampSampler, uv, 0).rgb;

    // 软阈值提取高亮
    float brightness = Luminance(color);
    float softThreshold = g_BLOOM_Threshold * 0.5;
    float contribution = max(brightness - g_BLOOM_Threshold, 0.0) /
                         max(brightness - softThreshold + 1e-6, 0.0);
    contribution = saturate(contribution);

    float3 bloomColor = color * contribution;

    g_BloomPyramidUAV[pixel] = float4(bloomColor, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// BloomBlurCS - Bloom 高斯模糊 (可分离, 水平+垂直)
// 由 C# 端调用两次, 分别在 X/Y 方向模糊
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void BloomBlurCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    // 高斯核 (9 tap)
    static const float weights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };
    static const float offsets[5] = { 0.0, 1.0, 2.0, 3.0, 4.0 };

    // 模糊半径 (基于 BloomRadius)
    float blurScale = g_BLOOM_Radius * 2.0;
    float2 texelSize = 1.0 / float2(w, h);

    // 水平模糊 (C# 通过不同的 UAV 绑定区分方向, 这里同时做两遍近似)
    float3 color = g_SourceSRV.SampleLevel(gPointClampSampler, uv, 0).rgb * weights[0];

    [unroll]
    for (int i = 1; i < 5; i++)
    {
        float2 offset = float2(offsets[i] * blurScale, 0.0) * texelSize;
        color += g_SourceSRV.SampleLevel(gLinearClampSampler, uv + offset, 0).rgb * weights[i];
        color += g_SourceSRV.SampleLevel(gLinearClampSampler, uv - offset, 0).rgb * weights[i];
    }

    // 垂直模糊
    float3 finalColor = color * weights[0];
    [unroll]
    for (int j = 1; j < 5; j++)
    {
        float2 offset = float2(0.0, offsets[j] * blurScale) * texelSize;
        finalColor += g_SourceSRV.SampleLevel(gLinearClampSampler, uv + offset, 0).rgb * weights[j];
        finalColor += g_SourceSRV.SampleLevel(gLinearClampSampler, uv - offset, 0).rgb * weights[j];
    }

    // 应用 Bloom 色调
    finalColor *= g_BLOOM_Tint;

    g_OutputUAV[pixel] = float4(finalColor, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// BloomCombineCS - Bloom 合成 (将模糊后的高亮叠加回场景)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void BloomCombineCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    float3 sceneColor = g_SceneColorSRV.SampleLevel(gPointClampSampler, uv, 0).rgb;
    float3 bloomColor = g_SourceSRV.SampleLevel(gLinearClampSampler, uv, 0).rgb;

    // 软合成: 仅当 Bloom 比场景亮时贡献
    float bloomLum = Luminance(bloomColor);
    float sceneLum = Luminance(sceneColor);
    float softKnee = smoothstep(sceneLum * 0.8, sceneLum * 1.2, bloomLum);

    float3 result = sceneColor + bloomColor * g_BLOOM_Intensity * softKnee;

    g_OutputUAV[pixel] = float4(result, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// LensCS - 镜头特效 (色散/暗角/镜头污渍/胶片颗粒)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void LensCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    // ── 径向色散 (Chromatic Aberration) ──
    float2 centerDir = uv - 0.5;
    float distFromCenter = length(centerDir);
    float caAmount = 0.002 * smoothstep(0.5, 1.0, distFromCenter);
    float2 caDir = normalize(centerDir + 1e-6);

    float r = g_SceneColorSRV.SampleLevel(gLinearClampSampler, uv + caDir * caAmount, 0).r;
    float g = g_SceneColorSRV.SampleLevel(gLinearClampSampler, uv, 0).g;
    float b = g_SceneColorSRV.SampleLevel(gLinearClampSampler, uv - caDir * caAmount, 0).b;
    float3 color = float3(r, g, b);

    // ── 暗角 (Vignette) ──
    float vignette = 1.0 - smoothstep(0.5, 1.0, distFromCenter);
    vignette = saturate(vignette);
    color *= lerp(1.0, vignette, 0.5);

    // ── 镜头污渍 (Lens Dirt) ──
    float3 dirtColor = g_LensDirtSRV.SampleLevel(gLinearWrapSampler, uv, 0).rgb;
    float dirtIntensity = Luminance(dirtColor);
    color = lerp(color, color + dirtColor * 2.0, dirtIntensity * 0.3);

    // ── 胶片颗粒 (Film Grain) ──
    float grain = hash1(dot(uv, float2(12.9898, 78.233)) + g_PP_Time * 100.0);
    grain = (grain - 0.5) * 0.05;
    color += grain;

    g_OutputUAV[pixel] = float4(color, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// TonemapCS - 色调映射 + 颜色分级 (HDR -> LDR)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void TonemapCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    float3 hdrColor = g_SceneColorSRV.SampleLevel(gPointClampSampler, uv, 0).rgb;

    // 应用曝光
    hdrColor = ApplyExposure(hdrColor, g_TM_Exposure);

    // 色调映射
    float3 tonemapped;
    if (g_TM_TonemapOperator == 0)
    {
        // ACES
        tonemapped = ACESFilm(hdrColor);
    }
    else if (g_TM_TonemapOperator == 1)
    {
        // Reinhard
        tonemapped = hdrColor / (hdrColor + 1.0);
    }
    else
    {
        // 线性 (无色调映射)
        tonemapped = saturate_fast(hdrColor);
    }

    // 颜色分级
    if (g_TM_EnableColorGrading != 0)
    {
        // 白平衡
        tonemapped = WhiteBalance(tonemapped, g_TM_WhiteBalanceTemp, g_TM_WhiteBalanceTint);

        // 颜色分级 (饱和度/对比度/增益/偏移)
        tonemapped = ColorGrade(tonemapped, g_TM_Saturation, g_TM_Contrast,
                                g_TM_Gain, g_TM_ColorOffset, g_TM_LutIntensity);

        // LUT 颜色校正 (简化: 用纹理查找)
        if (g_TM_LutIntensity > 0.001)
        {
            float3 lutColor = g_LutSRV.SampleLevel(gLinearClampSampler, tonemapped, 0).rgb;
            tonemapped = lerp(tonemapped, lutColor, g_TM_LutIntensity);
        }
    }

    // sRGB 编码
    float3 ldrColor = tonemapped;
    if (g_TM_ApplySRGB != 0)
    {
        ldrColor = LinearToSRGB(tonemapped);
    }

    g_OutputUAV[pixel] = float4(ldrColor, 1.0);
}
