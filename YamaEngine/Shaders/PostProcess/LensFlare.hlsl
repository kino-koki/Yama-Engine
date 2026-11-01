// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - Lens Flare System
// 镜头眩光: 鬼影 (Ghost) 沿光轴复制并色散, 光晕 (Halo), 星芒 (Starburst),
// 变形宽屏眩光 (Anamorphic), 镜头污渍合成 (Dirt)。
// 基于屏幕空间光源位置, 采样光斑纹理与场景颜色。
// 与 C# LensFlareSystem.cs 入口点对应。
// Shader Model 6.6 (DXC)
// 入口点: LensFlareCS, AnamorphicFlareCS, LensDirtCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# LensFlarePerLightCB / LensFlareElementCB 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer LensFlarePerLightCB : register(b0)
{
    float3 g_LF_ScreenPos;       // 屏幕空间光源位置 (NDC, -1..1)
    float  g_LF_Intensity;       // 整体强度
    float3 g_LF_LightColor;      // 光源颜色
    float  g_LF_Scale;           // 整体缩放
    float  g_LF_GhostDispersal;  // 鬼影间距
    float  g_LF_HaloWidth;       // 光晕宽度
    int    g_LF_NumGhosts;       // 鬼影数量
    float  g_LF_ChromaticDistortion; // 色散强度
    float  g_LF_Threshold;       // 亮度阈值
    float  g_LF_Pad0;
};

cbuffer LensFlareElementCB : register(b0)
{
    float2 g_LFE_ScreenPos;  // 元素屏幕位置
    float2 g_LFE_Scale;      // 元素缩放
    float4 g_LFE_Color;      // 元素颜色与强度 (RGB + A)
    float  g_LFE_Rotation;   // 旋转角度
    int    g_LFE_Type;        // 元素类型 (0=圆, 1=星芒, 2=光环, 3=多边形)
    float  g_LFE_BlendFactor; // 混合因子
    float  g_LFE_Pad0;
};

cbuffer LensFlareAnamorphicCB : register(b2)
{
    float2 g_AF_Direction;      // 拉伸方向 (通常水平)
    float  g_AF_StretchAmount;  // 拉伸量
    float  g_AF_Intensity;
    float3 g_AF_Tint;           // 颜色色调
    float  g_AF_Threshold;      // 亮度阈值
    int    g_AF_SampleCount;    // 采样数
    float  g_AF_Pad0;
    float  g_AF_Pad1;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4> g_SceneColorSRV    : register(t0); // 场景颜色 (用于阈值)
Texture2D<float4> g_FlareTextureSRV  : register(t1); // 光斑纹理
Texture2D<float4> g_StarburstSRV     : register(t2); // 星芒纹理
Texture2D<float4> g_LensDirtSRV      : register(t3); // 镜头污渍掩码
Texture2D<float>  g_SceneDepthSRV    : register(t4); // 场景深度 (遮挡测试)

RWTexture2D<float4> g_FlareOutputUAV : register(u0); // 眩光输出
RWTexture2D<float4> g_TempUAV        : register(u1); // 临时缓冲

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 从屏幕 UV 计算到光源中心的矢量
float2 ToLightCenter(float2 uv, float2 lightScreenPos)
{
    return uv - lightScreenPos;
}

// 沿光轴方向的鬼影位置 (相对中心反向偏移)
// ghostId: 第几个鬼影, dispersal: 间距
float2 GhostUV(float2 uv, float2 lightPos, int ghostId, float dispersal)
{
    float2 dir = uv - lightPos;
    // 鬼影沿光轴反方向排列, 每个鬼影距离不同
    return lightPos - dir * (1.0 + ghostId * dispersal);
}

// 色散采样: R/G/B 通道分别从略微偏移的位置采样
float3 SampleChromatic(Texture2D<float4> tex, SamplerState samp,
                       float2 uv, float2 centerDir, float distortion)
{
    float2 dir = normalize(centerDir + 1e-6);
    float scale = length(centerDir) * distortion;
    float r = tex.SampleLevel(samp, uv + dir * scale, 0).r;
    float g = tex.SampleLevel(samp, uv, 0).g;
    float b = tex.SampleLevel(samp, uv - dir * scale, 0).b;
    return float3(r, g, b);
}

// 径向衰减: 离屏幕中心越远越暗
float RadialFalloff(float2 uv, float power)
{
    float2 centered = uv - 0.5;
    float dist = length(centered) * 2.0;
    return pow(saturate(1.0 - dist), power);
}

// 星芒生成 (基于衍射, 程序化)
float3 Starburst(float2 uv, float2 lightPos, float intensity)
{
    float2 dir = uv - lightPos;
    float dist = length(dir);
    float angle = atan2(dir.y, dir.x);

    // 衍射条纹 (十字星芒 + 次级条纹)
    float star = 0.0;
    // 主十字
    star += pow(abs(cos(angle * 2.0)), 32.0);
    // 次级 6 角星芒
    star += pow(abs(cos(angle * 6.0)), 64.0) * 0.5;

    // 距离衰减
    star *= 1.0 / (dist * dist + 0.01);

    return star * intensity;
}

// ════════════════════════════════════════════════════════════════════════════
// LensFlareCS - 主镜头眩光 (鬼影 + 光晕 + 星芒)
// 在屏幕空间沿光轴生成一系列鬼影, 加上中心光晕与星芒
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void LensFlareCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_FlareOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    // 光源屏幕位置 (NDC -1..1 转 UV 0..1)
    float2 lightPos = g_LF_ScreenPos.xy * 0.5 + 0.5;

    // 遮挡测试: 检查光源位置是否被场景几何遮挡
    float lightDepth = g_SceneDepthSRV.Load(int3(lightPos * float2(w, h), 0));
    // 简化: 若光源位置深度被遮挡则减弱 (实际需比较光源深度)
    float occlusion = 1.0; // 默认不遮挡

    float3 result = 0.0;

    // ── 鬼影 (Ghosts) ──
    // 沿光源到屏幕中心的轴生成多个鬼影, 每个鬼影有色散
    [loop]
    for (int i = 0; i < g_LF_NumGhosts; i++)
    {
        float2 ghostUV = GhostUV(uv, lightPos, i, g_LF_GhostDispersal);

        // 鬼影缩放 (越远的鬼影越小)
        float ghostScale = 1.0 / (1.0 + i * g_LF_GhostDispersal * 2.0);

        // 采样光斑纹理 (带色散)
        float2 centerDir = ghostUV - lightPos;
        float3 ghostColor = SampleChromatic(g_FlareTextureSRV, gLinearClampSampler,
                                            ghostUV, centerDir, g_LF_ChromaticDistortion);

        // 距离衰减
        float distToGhost = length(ghostUV - lightPos);
        float falloff = 1.0 / (1.0 + distToGhost * distToGhost * 4.0);

        // 鬼影缩放 (纹理采样)
        float2 scaledUV = (ghostUV - lightPos) * ghostScale + lightPos;
        float ghostMask = g_FlareTextureSRV.SampleLevel(gLinearClampSampler, scaledUV, 0).a;

        result += ghostColor * ghostMask * falloff * g_LF_Intensity;
    }

    // ── 光晕 (Halo) ──
    // 围绕光源的环形光晕
    {
        float2 dir = uv - lightPos;
        float dist = length(dir);
        // 光晕在 HaloWidth 距离处最强 (环形)
        float halo = exp(-pow(dist - g_LF_HaloWidth, 2.0) * 8.0);
        // 色散光环
        float2 haloDir = normalize(dir + 1e-6);
        float3 haloColor = g_LF_LightColor;
        haloColor.r *= g_FlareTextureSRV.SampleLevel(gLinearClampSampler,
            lightPos + haloDir * (g_LF_HaloWidth + g_LF_ChromaticDistortion), 0).r;
        haloColor.b *= g_FlareTextureSRV.SampleLevel(gLinearClampSampler,
            lightPos + haloDir * (g_LF_HaloWidth - g_LF_ChromaticDistortion), 0).b;

        result += haloColor * halo * g_LF_Intensity * 0.5;
    }

    // ── 星芒 (Starburst) ──
    {
        float3 star = Starburst(uv, lightPos, g_LF_Intensity);
        // 星芒色散
        star *= g_LF_LightColor;
        result += star * 0.3;
    }

    // 应用整体颜色与强度
    result *= g_LF_LightColor * occlusion;

    // 径向衰减 (屏幕边缘减弱)
    result *= RadialFalloff(uv, 1.0);

    g_FlareOutputUAV[pixel] = float4(result, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// AnamorphicFlareCS - 变形宽屏镜头眩光
// 水平拉伸的条状眩光, 模拟变形宽银幕镜头的横向光斑
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void AnamorphicFlareCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_FlareOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    // 光源屏幕位置
    float2 lightPos = g_LF_ScreenPos.xy * 0.5 + 0.5;

    // 计算到光源的矢量
    float2 dir = uv - lightPos;
    float dist = length(dir);

    // 仅水平方向拉伸 (垂直方向窄, 水平方向宽)
    float2 anamorphicDir = float2(dir.x, dir.y * 8.0); // 垂直方向压缩
    float anamorphicDist = length(anamorphicDir);

    // 拉伸后的衰减
    float falloff = exp(-anamorphicDist * anamorphicDist * 8.0);

    // 沿水平方向采样累积 (模拟拉伸)
    float3 color = 0.0;
    float totalWeight = 0.0;

    [loop]
    for (int i = 0; i < g_AF_SampleCount; i++)
    {
        float t = (float)i / g_AF_SampleCount - 0.5;
        float2 sampleUV = lightPos + float2(t * g_AF_StretchAmount, 0.0);
        sampleUV = saturate(sampleUV);

        float3 sampleColor = g_SceneColorSRV.SampleLevel(gLinearClampSampler, sampleUV, 0).rgb;
        float brightness = Luminance(sampleColor);

        // 阈值过滤
        float contribution = max(brightness - g_AF_Threshold, 0.0);
        contribution = pow(contribution, 2.0);

        // 距光源越近权重越高
        float weight = exp(-t * t * 8.0);
        color += sampleColor * contribution * weight;
        totalWeight += weight;
    }

    color = totalWeight > 0.0 ? color / totalWeight : 0.0;

    // 应用色调与强度
    color *= g_AF_Tint * g_AF_Intensity * falloff;

    // 蓝色偏移 (变形镜头通常偏蓝)
    color.b *= 1.3;
    color.r *= 0.8;

    g_FlareOutputUAV[pixel] = float4(color, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// LensDirtCS - 镜头污渍合成
// 将眩光与镜头污渍掩码相乘, 模拟污渍上的光斑反射
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void LensDirtCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_FlareOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) / float2(w, h);

    // 读取眩光 (来自上一 pass)
    float3 flareColor = g_FlareTextureSRV.SampleLevel(gLinearClampSampler, uv, 0).rgb;

    // 读取镜头污渍掩码
    float3 dirtColor = g_LensDirtSRV.SampleLevel(gLinearWrapSampler, uv, 0).rgb;
    float dirtIntensity = Luminance(dirtColor);

    // 眩光与污渍相乘: 仅在污渍位置显示眩光
    // 全屏眩光 = 原始眩光 * (污渍掩码 + 0.1)  (0.1 为基础透过率)
    float3 dirtedFlare = flareColor * (dirtColor + 0.1);

    // 添加场景颜色 (眩光叠加到场景上)
    float3 sceneColor = g_SceneColorSRV.SampleLevel(gPointClampSampler, uv, 0).rgb;

    // 加法混合
    float3 result = sceneColor + dirtedFlare;

    g_FlareOutputUAV[pixel] = float4(result, 1.0);
}
