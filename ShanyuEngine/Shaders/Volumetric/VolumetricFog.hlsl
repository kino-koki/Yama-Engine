// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - Volumetric Fog System
// --------------------------------------------------------------------------
// Froxel 化体积雾: 将相机视锥体离散为 3D 网格 (Froxel), 每个 Froxel 存储
// 体积密度与散射光。支持 Mie 散射 (Henyey-Greenstein 相函数)、Beer-Lambert
// 衰减、体积阴影、GI 参与、粒子交互与多级滤波。
//
// 7 个计算入口 (对应 C# VolumetricFogRenderer):
//   FogInjectCS      - 注入密度 (高度雾 + 局部雾体积)
//   FogLightingCS    - 光照积分 (逐光源散射 + 体积阴影)
//   FogGICS          - GI 注入 (探针辐照度散射进雾)
//   FogParticleCS    - 粒子交互 (粒子散射贡献)
//   FogIntegrateCS   - 沿视线积分 (累加散射光 + 透射率)
//   FogTemporalCS    - 时间累积 (历史帧混合)
//   FogBilateralCS   - 双边滤波 (保边降噪)
//
// Froxel 纹理格式: RGBA16F, RGB=散射光, A=密度 (或透射率)
// 调度: [numthreads(8,8,4)] (X/8, Y/8, Z/4)
// Shader Model: 6.6
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// Froxel 常量缓冲 (对应 C# FroxelCB / VolumetricFogRenderCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer FroxelConstants : register(b2)
{
    float3   Resolution;        // Froxel 分辨率 X/Y/Z
    float    NearClip;
    float    FarClip;
    float    DepthExponent;     // 对数深度分布参数
    float4x4 ViewProjection;
    float4x4 InvViewProjection;
    float3   CameraPosition;
    float    Time;
    float    DensityScale;      // 全局密度倍率
    float3   Scattering;        // 散射系数
    float3   Absorption;        // 吸收系数
    float    PhaseG;            // Henyey-Greenstein 不对称参数
    float    TemporalBlend;     // 时间累积混合因子
    float    BilateralRadius;   // 双边滤波半径
    float    FogPad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 光源常量 (对应 C# FogLightCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer FogLightConstants : register(b3)
{
    float3   LightDirectionOrPos;
    int      LightType;         // 0=方向, 1=点, 2=聚光
    float3   LightColor;
    float    LightIntensity;
    float    LightRange;
    float    LightInnerConeCos;
    float    LightOuterConeCos;
    int      ShadowMapIndex;
    float4x4 LightShadowMatrix;
    float    LightPad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2DArray<float>  gShadowMaps     : register(t0); // 阴影图数组
Texture2D<float4>      gSkyViewLUT     : register(t1); // 天空视图 LUT
Texture2D<float4>      gAerialPerspLUT : register(t2); // 空中透视 LUT
Texture3D<float4>      gFogGrid        : register(t3); // Froxel 网格 (当前)
Texture3D<float4>      gHistoryGrid    : register(t4); // 历史网格
Texture2D<float4>      gGIIrradiance   : register(t5); // GI 辐照度
StructuredBuffer<float3> gParticleData : register(t6); // 粒子数据 (位置+密度)
Texture2D<float4>      gGeometryBufferNormal  : register(t7); // G-Buffer 法线 (深度引导)

RWTexture3D<float4> gFogOutput   : register(u0); // Froxel 输出
RWTexture3D<float4> gFilterOutput : register(u1); // 滤波输出

SamplerState gPointClampSampler    : register(s0);
SamplerState gLinearClampSampler   : register(s2);
SamplerComparisonState gShadowSampler : register(s4);

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// Froxel 索引 -> 世界位置 (Froxel 中心)
float3 FroxelToWorld(uint3 froxelCoord)
{
    // 归一化到 [0,1]
    float3 ndc = (froxelCoord + 0.5) / Resolution;

    // 深度: 对数分布 (近处密集, 远处稀疏)
    float z = NearClip * pow(FarClip / NearClip, ndc.z);

    // 屏幕 UV -> NDC
    float2 screenNDC = ndc.xy * 2.0 - 1.0;
    screenNDC.y = -screenNDC.y;

    float4 clipPos = float4(screenNDC, ndc.z * 2.0 - 1.0, 1.0);
    float4 worldPos = mul(InvViewProjection, clipPos);
    worldPos.xyz /= worldPos.w;

    return worldPos.xyz;
}

// 指数高度雾密度
float HeightFogDensity(float worldHeight)
{
    float fogStart = 0.0;
    float heightFalloff = 0.5;
    float baseDensity = 0.02;
    float h = worldHeight - fogStart;
    return baseDensity * exp(-max(0.0, h) / max(0.01, heightFalloff));
}

// 局部雾体积密度 (球体)
float LocalFogDensity(float3 worldPos, float3 center, float radius, float density)
{
    float dist = length(worldPos - center);
    if (dist > radius) return 0.0;
    return density * exp(-(dist * dist) / (radius * radius * 0.25));
}

// Henyey-Greenstein 相函数 (Mie 散射)
float HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 4.0 * SHANYU_PI * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5);
    return (1.0 - g2) / max(1e-6, denom);
}

// Beer-Lambert 透射率
float3 BeerLambert(float3 extinction, float distance)
{
    return exp(-extinction * distance);
}

// 消光系数 = 散射 + 吸收
float3 ExtinctionCoeff()
{
    return Scattering + Absorption;
}

// 光照贡献 (点光/聚光距离衰减)
float LightDistanceAttenuation(float3 worldPos)
{
    if (LightType == 0) return 1.0; // 方向光无衰减

    float dist = length(worldPos - LightDirectionOrPos);
    if (dist > LightRange) return 0.0;

    float atten = 1.0 / (1.0 + 0.1 * dist * dist);

    if (LightType == 2) // 聚光
    {
        float3 dir = normalize(LightDirectionOrPos - worldPos);
        float3 lightDir = normalize(-LightDirectionOrPos); // 简化
        float cosAngle = dot(dir, lightDir);
        float spotAtten = smoothstep_edge(LightOuterConeCos, LightInnerConeCos, cosAngle);
        atten *= spotAtten;
    }

    return atten;
}

// 体积阴影: 从 Froxel 位置向光源步进, 采样阴影图
float VolumetricShadow(float3 worldPos)
{
    if (ShadowMapIndex < 0) return 1.0;

    // 投影到光源空间
    float4 lightSpace = mul(LightShadowMatrix, float4(worldPos, 1.0));
    lightSpace.xyz /= lightSpace.w;

    if (any(abs(lightSpace.xy) > 1.0)) return 1.0;

    float2 shadowUV = lightSpace.xy * 0.5 + 0.5;

    // 沿光线方向步进采样阴影 (体积阴影)
    float shadow = 0.0;
    int samples = 4;
    [loop]
    for (int s = 0; s < samples; s++)
    {
        float2 offset = float2(s, s + 1) * (1.0 / 1024.0) * 0.5;
        float shadowDepth = gShadowMaps.Load(int4(shadowUV * 2048.0 + offset * 2048.0, ShadowMapIndex, 0));
        shadow += (lightSpace.z > shadowDepth + 0.001) ? 0.0 : 1.0;
    }
    shadow /= float(samples);
    return shadow;
}

// ─────────────────────────────────══════════════════════════════════════─
// 1. FogInjectCS - 注入密度 (高度雾 + 局部雾)
// [numthreads(8,8,4)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 4)]
void FogInjectCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint3 froxel = dispatchID.xyz;
    if (any(froxel >= (uint3)Resolution))
        return;

    float3 worldPos = FroxelToWorld(froxel);

    // 高度雾密度
    float density = HeightFogDensity(worldPos.y);

    // 局部雾体积 (示例: 相机附近的雾球)
    float localDensity = LocalFogDensity(worldPos, CameraPosition + float3(0, 0, -10), 8.0, 0.15);
    density = max(density, localDensity);

    // 全局密度倍率
    density *= DensityScale;

    // 散射光初始化 (天空光贡献)
    float3 skyColor = gSkyViewLUT.Load(int3(froxel.xy * 0.5, 0)).rgb;
    float3 initialScattering = skyColor * density * 0.1;

    // 写入 Froxel: RGB=散射光, A=密度
    gFogOutput[froxel] = float4(initialScattering, density);
}

// ─────────────────────────────────══════════════════════════════════════─
// 2. FogLightingCS - 光照积分 (逐光源散射 + 体积阴影)
// [numthreads(8,8,4)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 4)]
void FogLightingCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint3 froxel = dispatchID.xyz;
    if (any(froxel >= (uint3)Resolution))
        return;

    float4 froxelData = gFogGrid[froxel];
    float density = froxelData.a;
    if (density <= 0.0)
    {
        gFogOutput[froxel] = froxelData;
        return;
    }

    float3 worldPos = FroxelToWorld(froxel);
    float3 viewDir = normalize(worldPos - CameraPosition);

    // 光源方向
    float3 lightDir;
    if (LightType == 0)
        lightDir = normalize(LightDirectionOrPos);
    else
        lightDir = normalize(LightDirectionOrPos - worldPos);

    // 相函数 (Mie 散射)
    float cosTheta = dot(viewDir, -lightDir);
    float phase = HenyeyGreenstein(cosTheta, PhaseG);

    // 光照衰减
    float lightAtten = LightDistanceAttenuation(worldPos);

    // 体积阴影
    float shadow = VolumetricShadow(worldPos);

    // 散射光 = 光颜色 * 强度 * 相函数 * 密度 * 散射系数 * 阴影 * 衰减
    float3 scatteredLight = LightColor * LightIntensity * phase * density
                          * Scattering * shadow * lightAtten;

    // 累加到现有散射光
    float3 totalScattering = froxelData.rgb + scatteredLight;

    gFogOutput[froxel] = float4(totalScattering, density);
}

// ─────────────────────────────────══════════════════════════════════════─
// 3. FogGICS - GI 注入 (探针辐照度散射进雾)
// [numthreads(8,8,4)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 4)]
void FogGICS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint3 froxel = dispatchID.xyz;
    if (any(froxel >= (uint3)Resolution))
        return;

    float4 froxelData = gFogGrid[froxel];
    float density = froxelData.a;
    if (density <= 0.0)
    {
        gFogOutput[froxel] = froxelData;
        return;
    }

    float3 worldPos = FroxelToWorld(froxel);

    // 采样 GI 辐照度 (从屏幕空间或探针)
    float2 giUV = worldPos.xz * 0.01 + 0.5;
    giUV = saturate_fast(giUV);
    float3 giIrradiance = gGIIrradiance.SampleLevel(gLinearClampSampler, giUV, 0).rgb;

    // 多散射近似: GI 辐照度 * 反照率 * 密度
    float3 albedo = float3(0.9, 0.9, 0.9);
    float3 giScattering = giIrradiance * albedo * density * Scattering * 0.5;

    // 累加
    float3 totalScattering = froxelData.rgb + giScattering;

    gFogOutput[froxel] = float4(totalScattering, density);
}

// ─────────────────────────────────══════════════════════════════════════─
// 4. FogParticleCS - 粒子交互 (粒子散射贡献)
// [numthreads(8,8,4)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 4)]
void FogParticleCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint3 froxel = dispatchID.xyz;
    if (any(froxel >= (uint3)Resolution))
        return;

    float4 froxelData = gFogGrid[froxel];
    float3 worldPos = FroxelToWorld(froxel);

    // 粒子散射: 查找 Froxel 附近的粒子并累加散射
    // 简化: 假设粒子数据为位置数组, 遍历少量粒子
    float3 particleScattering = float3(0, 0, 0);
    float extraDensity = 0.0;

    // 注: 实际粒子数量由系统传入, 此处使用固定循环示意
    uint particleCount = 16;
    [loop]
    for (uint i = 0; i < particleCount; i++)
    {
        float3 particlePos = gParticleData[i];
        float dist = length(worldPos - particlePos);
        if (dist < 2.0)
        {
            float particleContribution = exp(-dist * dist * 0.5);
            extraDensity += particleContribution * 0.05;
            particleScattering += LightColor * particleContribution * 0.01;
        }
    }

    float density = froxelData.a + extraDensity;
    float3 scattering = froxelData.rgb + particleScattering;

    gFogOutput[froxel] = float4(scattering, density);
}

// ─────────────────────────────────══════════════════════════════════════─
// 5. FogIntegrateCS - 沿视线积分 (累加散射光 + 透射率)
// 输出: RGB=最终雾颜色, A=透射率 (1=无雾, 0=完全不透明)
// [numthreads(8,8,1)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 1)]
void FogIntegrateCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint2 screenFroxel = dispatchID.xy;
    if (any(screenFroxel >= (uint2)Resolution.xy))
        return;

    float3 accumColor = float3(0, 0, 0);
    float transmittance = 1.0;
    float3 extinction = ExtinctionCoeff();

    // 沿深度方向 (Z 轴) 累积积分
    [loop]
    for (uint z = 0; z < (uint)Resolution.z; z++)
    {
        uint3 froxel = uint3(screenFroxel, z);
        float4 froxelData = gFogGrid[froxel];

        float density = froxelData.a;
        float3 scattering = froxelData.rgb;

        // Froxel 世界位置 (用于深度步长)
        float3 worldPos = FroxelToWorld(froxel);
        float3 prevWorldPos = (z > 0) ? FroxelToWorld(uint3(screenFroxel, z - 1)) : worldPos;
        float stepLength = length(worldPos - prevWorldPos);
        stepLength = max(stepLength, 0.1);

        // 消光 (标量近似)
        float3 ext = extinction * density * stepLength;

        // 透射率 (Beer-Lambert)
        float3 stepTransmittance = BeerLambert(ext, 1.0);
        float avgTransmittance = (stepTransmittance.r + stepTransmittance.g + stepTransmittance.b) / 3.0;

        // 累积散射光 (前向积分): C += T * L_scat * (1 - T_step) / ext
        float3 scatterContrib = scattering * stepLength;
        float3 integratedScatter;
        if (all(ext > 1e-6))
            integratedScatter = scatterContrib * (1.0 - stepTransmittance) / max(ext, 1e-6);
        else
            integratedScatter = scatterContrib;

        accumColor += transmittance * integratedScatter;
        transmittance *= avgTransmittance;

        // 透射率过低则提前终止
        if (transmittance < 0.01)
            break;
    }

    // 大气空中透视 LUT 叠加
    float3 aerialPersp = gAerialPerspLUT.Load(int3(screenFroxel, 0)).rgb;
    accumColor += aerialPersp * (1.0 - transmittance) * 0.3;

    gFogOutput[uint3(screenFroxel, 0)] = float4(accumColor, transmittance);
}

// ─────────────────────────────────══════════════════════════─────────────
// 6. FogTemporalCS - 时间累积 (历史帧混合)
// [numthreads(8,8,4)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 4)]
void FogTemporalCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint3 froxel = dispatchID.xyz;
    if (any(froxel >= (uint3)Resolution))
        return;

    float4 current = gFogGrid[froxel];
    float4 history = gHistoryGrid[froxel];

    // 深度引导: 相邻帧 Froxel 内容应相似
    // 简化: 直接指数移动平均
    float blend = TemporalBlend;
    float4 result = lerp(history, current, blend);

    gFogOutput[froxel] = result;
}

// ─────────────────────────────────══════════════════════════════════════─
// 7. FogBilateralCS - 双边滤波 (保边降噪)
// [numthreads(8,8,4)]
// ──────────────────────────────────────────────────────────────────────────
[numthreads(8, 8, 4)]
void FogBilateralCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint3 froxel = dispatchID.xyz;
    if (any(froxel >= (uint3)Resolution))
        return;

    float4 center = gFogGrid[froxel];
    float centerDensity = center.a;

    // 深度引导: 使用 Froxel 深度索引作为边缘判据
    float centerDepth = (froxel.z + 0.5) / Resolution.z;

    float4 colorAccum = float4(0, 0, 0, 0);
    float weightAccum = 0.0;

    int radius = (int)BilateralRadius;

    // 3D 双边滤波 (X, Y, Z 邻域)
    [loop]
    for (int dz = -radius; dz <= radius; dz++)
    {
        [loop]
        for (int dy = -radius; dy <= radius; dy++)
        {
            [loop]
            for (int dx = -radius; dx <= radius; dx++)
            {
                int3 sampleCoord = int3(froxel) + int3(dx, dy, dz);
                if (any(sampleCoord < 0) || any(sampleCoord >= (int3)Resolution))
                    continue;

                float4 sampleColor = gFogGrid[uint3(sampleCoord)];
                float sampleDensity = sampleColor.a;
                float sampleDepth = (sampleCoord.z + 0.5) / Resolution.z;

                // 空间权重 (高斯)
                float spatialDist = length(float3(dx, dy, dz));
                float spatialWeight = exp(-spatialDist * spatialDist / (2.0 * BilateralRadius * BilateralRadius));

                // 深度权重 (边缘停止)
                float depthDiff = abs(centerDepth - sampleDepth);
                float depthWeight = exp(-depthDiff * depthDiff / (2.0 * 0.1 * 0.1));

                // 密度权重
                float densityDiff = abs(centerDensity - sampleDensity);
                float densityWeight = exp(-densityDiff * densityDiff / (2.0 * 0.05 * 0.05));

                float weight = spatialWeight * depthWeight * densityWeight;

                colorAccum += sampleColor * weight;
                weightAccum += weight;
            }
        }
    }

    float4 filtered = colorAccum / max(weightAccum, SHANYU_EPSILON);
    gFilterOutput[froxel] = filtered;
}
