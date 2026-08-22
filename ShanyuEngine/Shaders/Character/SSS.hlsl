// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - Screen-Space Subsurface Scattering (Separable SSS)
// 分离式 SSS: 提取 SSS 区域 -> 水平高斯模糊 -> 垂直高斯模糊 -> 合成,
// 外加背光透射 (光线穿过薄皮肤如耳廓/鼻翼)。
// 基于可分离核的皮肤散射近似, 深度感知避免边缘渗色。
// Shader Model 6.6 (DXC)
// 入口点: SSSExtractCS, SSSHBlurCS, SSSVBlurCS, SSSCompositeCS, SSSTransmissionCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# SSSConstantsCB / SSSTransmissionCB 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer SSSConstantsCB : register(b0)
{
    float2 g_SSS_InvResolution;
    float  g_SSS_ScatterRadius;
    uint   g_SSS_MaterialIdMask;
    float4x4 g_SSS_ProjMatrix;
    int   g_SSS_KernelSampleCount;
    int   g_SSS_PassIndex;
    int   g_SSS_EnableTransmission;
    int   g_SSS_EnableMultiLayer;
    float g_SSS_Anisotropy;
    float g_SSS_NearZ;
    float g_SSS_FarZ;
    float g_SSS_Strength;
    float g_SSS_Pad0;
    float g_SSS_Pad1;
};

cbuffer SSSTransmissionCB : register(b0)
{
    float3 g_SSS_T_Color;
    float  g_SSS_T_Amount;
    float  g_SSS_T_Distortion;
    float3 g_SSS_T_LightDir;
    float  g_SSS_T_ShadowAttenuation;
    float  g_SSS_T_ThicknessScale;
    float3 g_SSS_T_Pad;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
// G-Buffer 与光照
Texture2D<float4> g_GeometryBuffer0         : register(t0); // Albedo + Roughness
Texture2D<float4> g_GeometryBuffer1         : register(t1); // Normal + Metallic
Texture2D<float4> g_GeometryBuffer3         : register(t2); // MaterialID 等
Texture2D<float>  g_SceneDepth       : register(t3);
Texture2D<float4> g_LightingTex      : register(t4); // 直接光照结果
Texture2D<float>  g_ThicknessTex     : register(t5); // 厚度缓冲
Texture2D<float>  g_ShadowTex        : register(t6); // 阴影衰减

// SSS 中间纹理
RWTexture2D<float4> g_SSS_ExtractUAV : register(u0);  // 提取的 SSS 输入 (颜色+掩码)
RWTexture2D<float4> g_SSS_BlurUAV    : register(u0);  // 模糊输出 (水平/垂直复用)
Texture2D<float4>   g_SSS_BlurInputSRV : register(t0); // 模糊输入
RWTexture2D<float4> g_SSS_CompositeUAV : register(u0); // 最终合成输出
RWTexture2D<float4> g_SSS_TransmissionUAV : register(u0); // 透射输出

// ──────────────────────────────────────────────────────────────────────────
// 可分离高斯核 (皮肤散射近似)
// 6 个高斯核样本, 宽度由散射半径控制
// ──────────────────────────────────────────────────────────────────────────
static const int SSS_KERNEL_SIZE = 6;
static const float SSS_KERNEL_WEIGHTS[SSS_KERNEL_SIZE] =
{
    0.234, 0.188, 0.141, 0.094, 0.047, 0.023
};
static const float SSS_KERNEL_OFFSETS[SSS_KERNEL_SIZE] =
{
    1.0, 2.0, 4.0, 6.0, 8.0, 12.0
};

// 皮肤散射颜色 (红光散射最远, 蓝光最近)
static const float3 SSS_SCATTER_COLOR = float3(1.0, 0.6, 0.4);

// 世界半径转屏幕半径 (基于投影矩阵与深度)
float WorldRadiusToScreenRadius(float worldRadius, float depth)
{
    // 投影矩阵的 (0,0) 与 (1,1) 元素决定缩放
    float projScale = g_SSS_ProjMatrix[1][1];
    return worldRadius * projScale / max(depth, 0.01);
}

// 深度感知权重: 避免深度不连续处渗色
float DepthAwareWeight(float centerDepth, float sampleDepth, float pixelRadius)
{
    float depthDiff = abs(sampleDepth - centerDepth);
    // 深度差大于一定阈值则降权
    float scale = 1.0 / (pixelRadius * 0.02 + 1e-4);
    return exp(-depthDiff * scale * 50.0);
}

// ════════════════════════════════════════════════════════════════════════════
// SSSExtractCS - 提取需要 SSS 的像素 (基于材质 ID 掩码)
// 将皮肤像素的漫反射光照提取到单独纹理
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SSSExtractCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SSS_ExtractUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);

    // 读取材质 ID
    float4 gb3 = g_GeometryBuffer3.SampleLevel(gPointClampSampler, uv, 0);
    uint materialId = uint(gb3.g * 255.0);

    // 检查是否为 SSS 材质
    uint isSSS = (materialId == g_SSS_MaterialIdMask) ? 1 : 0;

    if (isSSS == 0)
    {
        g_SSS_ExtractUAV[dispatchID.xy] = float4(0, 0, 0, 0);
        return;
    }

    // 提取漫反射光照 (排除高光)
    float3 albedo = g_GeometryBuffer0.SampleLevel(gPointClampSampler, uv, 0).rgb;
    float3 lighting = g_LightingTex.SampleLevel(gPointClampSampler, uv, 0).rgb;

    // 漫反射分量 (近似: 总光照减去高光贡献)
    float3 diffuseLight = lighting;

    g_SSS_ExtractUAV[dispatchID.xy] = float4(diffuseLight * albedo, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// SSSHBlurCS - 水平高斯模糊 (可分离核第一步)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SSSHBlurCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SSS_BlurUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float centerDepth = g_SceneDepth.SampleLevel(gPointClampSampler, uv, 0);

    // 屏幕空间半径
    float screenRadius = WorldRadiusToScreenRadius(g_SSS_ScatterRadius, centerDepth);
    float pixelRadius = screenRadius * h;

    float4 centerColor = g_SSS_BlurInputSRV.SampleLevel(gPointClampSampler, uv, 0);
    if (centerColor.a < 0.01)
    {
        g_SSS_BlurUAV[dispatchID.xy] = centerColor;
        return;
    }

    // 中心样本
    float3 sum = centerColor.rgb * 0.5;
    float totalWeight = 0.5;

    // 水平方向采样
    [unroll]
    for (int i = 0; i < SSS_KERNEL_SIZE; i++)
    {
        float offset = SSS_KERNEL_OFFSETS[i] * pixelRadius * g_SSS_InvResolution.x;
        float weight = SSS_KERNEL_WEIGHTS[i];

        // 正负方向
        float2 uvR = uv + float2(offset, 0.0);
        float2 uvL = uv - float2(offset, 0.0);

        float4 sampleR = g_SSS_BlurInputSRV.SampleLevel(gLinearClampSampler, uvR, 0);
        float4 sampleL = g_SSS_BlurInputSRV.SampleLevel(gLinearClampSampler, uvL, 0);

        float depthR = g_SceneDepth.SampleLevel(gPointClampSampler, uvR, 0);
        float depthL = g_SceneDepth.SampleLevel(gPointClampSampler, uvL, 0);

        // 深度感知权重
        float weightR = weight * DepthAwareWeight(centerDepth, depthR, pixelRadius) * sampleR.a;
        float weightL = weight * DepthAwareWeight(centerDepth, depthL, pixelRadius) * sampleL.a;

        // 散射颜色染色 (红光散射更远)
        float3 tintR = lerp(float3(1, 1, 1), SSS_SCATTER_COLOR, SSS_KERNEL_OFFSETS[i] / 12.0);

        sum += sampleR.rgb * tintR * weightR;
        sum += sampleL.rgb * tintR * weightL;
        totalWeight += weightR + weightL;
    }

    g_SSS_BlurUAV[dispatchID.xy] = float4(sum / max(totalWeight, 0.001), centerColor.a);
}

// ════════════════════════════════════════════════════════════════════════════
// SSSVBlurCS - 垂直高斯模糊 (可分离核第二步)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SSSVBlurCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SSS_BlurUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float centerDepth = g_SceneDepth.SampleLevel(gPointClampSampler, uv, 0);

    float screenRadius = WorldRadiusToScreenRadius(g_SSS_ScatterRadius, centerDepth);
    float pixelRadius = screenRadius * h;

    float4 centerColor = g_SSS_BlurInputSRV.SampleLevel(gPointClampSampler, uv, 0);
    if (centerColor.a < 0.01)
    {
        g_SSS_BlurUAV[dispatchID.xy] = centerColor;
        return;
    }

    float3 sum = centerColor.rgb * 0.5;
    float totalWeight = 0.5;

    // 垂直方向采样
    [unroll]
    for (int i = 0; i < SSS_KERNEL_SIZE; i++)
    {
        float offset = SSS_KERNEL_OFFSETS[i] * pixelRadius * g_SSS_InvResolution.y;
        float weight = SSS_KERNEL_WEIGHTS[i];

        float2 uvU = uv + float2(0.0, offset);
        float2 uvD = uv - float2(0.0, offset);

        float4 sampleU = g_SSS_BlurInputSRV.SampleLevel(gLinearClampSampler, uvU, 0);
        float4 sampleD = g_SSS_BlurInputSRV.SampleLevel(gLinearClampSampler, uvD, 0);

        float depthU = g_SceneDepth.SampleLevel(gPointClampSampler, uvU, 0);
        float depthD = g_SceneDepth.SampleLevel(gPointClampSampler, uvD, 0);

        float weightU = weight * DepthAwareWeight(centerDepth, depthU, pixelRadius) * sampleU.a;
        float weightD = weight * DepthAwareWeight(centerDepth, depthD, pixelRadius) * sampleD.a;

        float3 tint = lerp(float3(1, 1, 1), SSS_SCATTER_COLOR, SSS_KERNEL_OFFSETS[i] / 12.0);

        sum += sampleU.rgb * tint * weightU;
        sum += sampleD.rgb * tint * weightD;
        totalWeight += weightU + weightD;
    }

    g_SSS_BlurUAV[dispatchID.xy] = float4(sum / max(totalWeight, 0.001), centerColor.a);
}

// ════════════════════════════════════════════════════════════════════════════
// SSSCompositeCS - 将 SSS 散射结果合成回场景
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SSSCompositeCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SSS_CompositeUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);

    // 原始场景光照
    float3 sceneColor = g_LightingTex.SampleLevel(gPointClampSampler, uv, 0).rgb;

    // SSS 散射结果
    float4 sssColor = g_SSS_BlurInputSRV.SampleLevel(gPointClampSampler, uv, 0);

    // 材质掩码
    float4 gb3 = g_GeometryBuffer3.SampleLevel(gPointClampSampler, uv, 0);
    uint materialId = uint(gb3.g * 255.0);
    float sssMask = (materialId == g_SSS_MaterialIdMask) ? 1.0 : 0.0;

    // 合成: 用 SSS 结果替换皮肤的漫反射分量
    // 保留高光, 用散射后的漫反射
    float3 albedo = g_GeometryBuffer0.SampleLevel(gPointClampSampler, uv, 0).rgb;
    float metallic = g_GeometryBuffer1.SampleLevel(gPointClampSampler, uv, 0).a;
    float3 specular = sceneColor * metallic; // 简化的高光保留

    float3 sssResult = sssColor.rgb * g_SSS_Strength;
    float3 finalColor = lerp(sceneColor, sssResult + specular, sssMask * sssColor.a);

    g_SSS_CompositeUAV[dispatchID.xy] = float4(finalColor, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// SSSTransmissionCS - 背光透射 (光线穿过薄皮肤)
// 当光源在物体背面时, 光穿过薄壁区域产生红色透射光
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SSSTransmissionCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SSS_TransmissionUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);

    // 材质掩码
    float4 gb3 = g_GeometryBuffer3.SampleLevel(gPointClampSampler, uv, 0);
    uint materialId = uint(gb3.g * 255.0);
    if (materialId != g_SSS_MaterialIdMask)
    {
        g_SSS_TransmissionUAV[dispatchID.xy] = float4(0, 0, 0, 0);
        return;
    }

    // 法线与厚度
    float4 gb1 = g_GeometryBuffer1.SampleLevel(gPointClampSampler, uv, 0);
    float3 normal = DecodeNormalOctahedral(gb1.xy);
    float thickness = g_ThicknessTex.SampleLevel(gPointClampSampler, uv, 0) * g_SSS_T_ThicknessScale;

    // 光线方向 (向光)
    float3 lightDir = normalize(g_SSS_T_LightDir);

    // 透射: 光从背面穿过, NdotL 为负
    float backNdotL = saturate(-dot(normal, lightDir));

    // 厚度衰减 (越厚透射越少)
    float thicknessAttenuation = exp(-thickness * 3.0);

    // 光线扭曲 (沿法线偏移采样位置, 模拟光在皮肤内的弯曲)
    float2 distortion = normal.xy * g_SSS_T_Distortion * g_SSS_InvResolution * 10.0;
    float shadow = g_ShadowTex.SampleLevel(gPointClampSampler, uv + distortion, 0);
    shadow *= g_SSS_T_ShadowAttenuation;

    // 透射颜色 (穿过皮肤的红色调)
    float3 transmission = g_SSS_T_Color * backNdotL * thicknessAttenuation * shadow * g_SSS_T_Amount;

    // 软化边缘
    float softEdge = pow(backNdotL, 2.0);

    g_SSS_TransmissionUAV[dispatchID.xy] = float4(transmission * softEdge, softEdge);
}
