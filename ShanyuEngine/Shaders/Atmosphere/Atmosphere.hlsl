// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - Atmospheric Scattering & Volumetric Clouds
// 物理天空: Rayleigh + Mie 单次/多次散射, 预计算透射率与多次散射 LUT,
// 体积云光线步进, 屏幕空间 God Ray, 距离空中透视雾。
// Shader Model 6.6 (DXC)
// 入口点: SkyRenderCS, AerialPerspectiveCS, CloudMarchCS, GodRayCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# SkyPrecomputeCB / SkyRenderCB / CloudMarchCB / GodRayCB 对应)
// ──────────────────────────────────────────────────────────────────────────

// 天空预计算常量 (透射率 / 多次散射 LUT)
cbuffer SkyPrecomputeCB : register(b0)
{
    float  g_PlanetRadius;
    float  g_AtmosphereHeight;
    float  g_RayleighScaleHeight;
    float  g_MieScaleHeight;
    float3 g_RayleighScattering;
    float  g_MieScattering;
    float  g_MieAbsorption;
    float  g_MiePhaseG;
    float  g_PrecomputePad0;
    float3 g_GroundAlbedo;
    float  g_SunIlluminancePre;
    float3 g_SunDirectionPre;
    float  g_PrecomputePad1;
};

// 天空渲染常量
cbuffer SkyRenderCB : register(b0)
{
    float3 g_SkyCameraPos;
    float  g_SunIlluminance;
    float4x4 g_SkyInvViewProj;
    float3 g_SunDirection;
    float  g_SkyPlanetRadius;
    float  g_SkyAtmosphereHeight;
    float2 g_SkyResolution;
};

// 空中透视常量 (与 AerialPerspective 共用 b0 槽)
cbuffer AerialPerspectiveCB : register(b0)
{
    float3 g_AP_CameraPos;
    float  g_AP_FogMaxDistance;
    float4x4 g_AP_InvViewProj;
    float3 g_AP_SunDirection;
    float  g_AP_FogDensity;
    float2 g_AP_Resolution;
    float  g_AP_PlanetRadius;
    float  g_AP_AtmosphereHeight;
};

// 云光线步进常量
cbuffer CloudMarchCB : register(b0)
{
    float3 g_CloudCameraPos;
    int   g_CloudSteps;
    float4x4 g_CloudInvViewProj;
    float3 g_CloudSunDirection;
    float  g_CloudBottom;
    float  g_CloudTop;
    float  g_CloudCoverage;
    float  g_CloudDensity;
    float  g_CloudTime;
    float2 g_CloudWindDir;
    float2 g_CloudResolution;
};

// God Ray 常量
cbuffer GodRayCB : register(b0)
{
    float2 g_GR_SunScreenPos;
    int   g_GR_Samples;
    float g_GR_Intensity;
    float g_GR_Decay;
    float g_GR_Exposure;
    float2 g_GR_Resolution;
    float2 g_GR_InvResolution;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
// SkyRender: t0=透射率LUT, t1=SkyViewLUT, u0=输出
Texture2D<float4> g_TransmittanceLUT : register(t0);
Texture2D<float4> g_SkyViewLUT       : register(t1);
RWTexture2D<float4> g_SkyOutputUAV   : register(u0);
RWTexture2D<float4> g_MultiScatterUAV: register(u1); // 预计算时使用

// AerialPerspective: t0=场景深度, t1=透射率LUT, u0=输出
Texture2D<float>   g_SceneDepthTex   : register(t0);
RWTexture2D<float4> g_APOutputUAV    : register(u0);

// CloudMarch: t0=形状噪声, t1=细节噪声, t2=天气图, u0=颜色, u1=alpha
Texture3D<float>   g_CloudShapeNoise : register(t0);
Texture3D<float>   g_CloudDetailNoise: register(t1);
Texture2D<float>   g_CloudWeatherMap : register(t2);
RWTexture2D<float4> g_CloudColorUAV  : register(u0);
RWTexture2D<float>  g_CloudAlphaUAV  : register(u1);

// GodRay: t0=场景颜色, u0=输出
Texture2D<float4>   g_GodRaySceneTex : register(t0);
RWTexture2D<float4> g_GodRayOutputUAV: register(u0);

// ──────────────────────────────────────────────────────────────────────────
// 大气物理核心函数 (Bruneton & Nakamae 方法)
// ──────────────────────────────────────────────────────────────────────────

// 行星与大气半径
#define ATM_TOP_RADIUS (g_SkyPlanetRadius + g_SkyAtmosphereHeight)

// 射线-球相交: 返回最近正距离 (无相交返回 -1)
float2 RaySphereIntersect(float3 origin, float3 dir, float3 center, float radius)
{
    float3 oc = origin - center;
    float b = dot(oc, dir);
    float c = dot(oc, oc) - radius * radius;
    float disc = b * b - c;
    if (disc < 0.0) return float2(-1.0, -1.0);
    float sq = sqrt(disc);
    return float2(-b - sq, -b + sq);
}

// 到大气层顶部的距离
float DistanceToAtmosphereTop(float3 origin, float3 dir)
{
    float2 t = RaySphereIntersect(origin, dir, float3(0, 0, 0), ATM_TOP_RADIUS);
    return t.y > 0.0 ? t.y : 0.0;
}

// 是否与地面相交
bool IntersectsGround(float3 origin, float3 dir)
{
    float2 t = RaySphereIntersect(origin, dir, float3(0, 0, 0), g_SkyPlanetRadius);
    return t.x > 0.0;
}
float DistanceToGround(float3 origin, float3 dir)
{
    float2 t = RaySphereIntersect(origin, dir, float3(0, 0, 0), g_SkyPlanetRadius);
    return t.x > 0.0 ? t.x : 0.0;
}

// 高度处的瑞利密度 (指数衰减)
float RayleighDensity(float height)
{
    return exp(-max(height, 0.0) / g_RayleighScaleHeight);
}
float MieDensity(float height)
{
    return exp(-max(height, 0.0) / g_MieScaleHeight);
}

// 沿射线的光学深度积分 (Rayleigh + Mie)
// 返回 opticalDepth = (tauR, tauM)
float2 IntegrateOpticalDepth(float3 origin, float3 dir, float distance, int samples)
{
    float2 opticalDepth = 0.0;
    float stepSize = distance / samples;
    for (int i = 0; i < samples; i++)
    {
        float t = (i + 0.5) * stepSize;
        float3 p = origin + dir * t;
        float height = length(p) - g_SkyPlanetRadius;
        opticalDepth.x += RayleighDensity(height) * stepSize;
        opticalDepth.y += MieDensity(height) * stepSize;
    }
    return opticalDepth;
}

// 从光学深度计算透射率 (Beer-Lambert)
float3 TransmittanceFromDepth(float2 opticalDepth)
{
    float3 tauR = g_RayleighScattering * opticalDepth.x;
    float3 tauM = (g_MieScattering + g_MieAbsorption).xxx * opticalDepth.y;
    return exp(-(tauR + tauM));
}

// 瑞利相函数: 3/(16pi) * (1 + cos^2)
float RayleighPhase(float cosTheta)
{
    return 0.0596831 * (1.0 + cosTheta * cosTheta);
}

// Mie 相函数 (Henvey-Greenstein)
float MiePhase(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return 0.1193662 * (1.0 - g2) / (pow(abs(denom), 1.5) + 1e-7) * (1.0 + 0.5); // 归一化常数
}

// 采样透射率 LUT (按高度与天顶角余弦)
float3 SampleTransmittanceLUT(float3 worldPos, float3 dir)
{
    float r = length(worldPos);
    float height = r - g_SkyPlanetRadius;
    float mu = dot(worldPos, dir) / r; // 视线天顶角余弦

    // 将 (height, mu) 映射到 LUT UV
    float u = (mu * 0.5 + 0.5);
    float v = saturate(height / g_SkyAtmosphereHeight);
    return g_TransmittanceLUT.SampleLevel(gLinearClampSampler, float2(u, v), 0).rgb;
}

// ════════════════════════════════════════════════════════════════════════════
// SkyRenderCS - 天空渲染 + LUT 预计算
// 该着色器身兼两职: 当输出 UAV 尺寸为透射率 LUT (256x64) 时执行预计算,
// 否则执行天空像素渲染。
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SkyRenderCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint outW, outH;
    g_SkyOutputUAV.GetDimensions(outW, outH);

    if (dispatchID.x >= outW || dispatchID.y >= outH) return;

    // ── 预计算模式: 透射率 + 多次散射 LUT ──
    // 透射率 LUT 尺寸为 256x64
    if (outW == 256 && outH == 64)
    {
        // 将像素坐标映射到 (mu, height)
        float u = (dispatchID.x + 0.5) / 256.0;
        float v = (dispatchID.y + 0.5) / 64.0;

        // mu 从 -1 到 1, height 从 0 到大气顶
        float mu = u * 2.0 - 1.0;
        float height = v * g_AtmosphereHeight;
        float3 worldPos = float3(0, g_PlanetRadius + height, 0);
        float3 dir = float3(sqrt(1.0 - mu * mu), mu, 0.0);

        float dist = DistanceToAtmosphereTop(worldPos, dir);
        if (IntersectsGround(worldPos, dir))
            dist = DistanceToGround(worldPos, dir);

        float2 od = IntegrateOpticalDepth(worldPos, dir, dist, 32);
        float3 transmittance = TransmittanceFromDepth(od);
        g_SkyOutputUAV[dispatchID.xy] = float4(transmittance, 1.0);

        // 多次散射近似: 在该点对半球积分一次散射, 计算 f_ms 系数
        // Psi = (1 + f_ms)/(1 - f_ms), 简化为基于透射率的近似
        float3 sunDir = normalize(g_SunDirectionPre);
        float3 multiScatter = transmittance * 0.1; // 一阶近似
        // 计算该点直接受太阳照射的散射贡献
        float sunDist = DistanceToAtmosphereTop(worldPos, sunDir);
        if (IntersectsGround(worldPos, sunDir)) sunDist = DistanceToGround(worldPos, sunDir);
        float2 sunOD = IntegrateOpticalDepth(worldPos, sunDir, sunDist, 16);
        float3 sunTrans = TransmittanceFromDepth(sunOD);
        float3 directScatter = g_RayleighScattering * sunOD.x * sunTrans * RayleighPhase(dot(dir, sunDir));
        multiScatter += directScatter * 0.5;

        g_MultiScatterUAV[dispatchID.xy] = float4(multiScatter, 1.0);
        return;
    }

    // ── 天空渲染模式 ──
    float2 uv = (dispatchID.xy + 0.5) / g_SkyResolution;
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    // 重建世界射线方向
    float4 farPoint = mul(g_SkyInvViewProj, float4(ndc, 1.0, 1.0));
    float3 worldDir = normalize(farPoint.xyz / farPoint.w - g_SkyCameraPos);

    float3 origin = g_SkyCameraPos;
    // 将原点转换到行星中心坐标系 (假设行星中心在 -PlanetRadius 的 y 方向)
    float3 planetCenter = float3(0, -g_SkyPlanetRadius, 0);
    origin -= planetCenter;

    float3 radiance = 0.0;
    float3 transmittance = 1.0;

    // 单次散射光线步进
    float dist = DistanceToAtmosphereTop(origin, worldDir);
    bool hitGround = IntersectsGround(origin, worldDir);
    if (hitGround)
        dist = DistanceToGround(origin, worldDir);

    const int SKY_SAMPLES = 32;
    float stepSize = dist / SKY_SAMPLES;

    float3 sunDir = normalize(g_SunDirection);
    float cosTheta = dot(worldDir, sunDir);
    float phaseR = RayleighPhase(cosTheta);
    float phaseM = MiePhase(cosTheta, g_MiePhaseG);

    for (int i = 0; i < SKY_SAMPLES; i++)
    {
        float t = (i + 0.5) * stepSize;
        float3 p = origin + worldDir * t;
        float height = length(p) - g_SkyPlanetRadius;

        float densityR = RayleighDensity(height);
        float densityM = MieDensity(height);

        // 当前点的光学深度
        float2 segOD = float2(densityR, densityM) * stepSize;

        // 太阳方向的透射率 (相机->该点 * 该点->太阳)
        float3 sunTrans = SampleTransmittanceLUT(p, sunDir);

        // 内散射贡献
        float3 scatterR = g_RayleighScattering * phaseR * segOD.x;
        float3 scatterM = g_MieScattering.xxx * phaseM * segOD.y;
        radiance += transmittance * sunTrans * (scatterR + scatterM);

        // 累积透射率
        transmittance *= TransmittanceFromDepth(segOD);
    }

    // 地面反射贡献 (多次散射近似)
    if (hitGround)
    {
        float3 groundPos = origin + worldDir * dist;
        float3 groundNormal = normalize(groundPos);
        float3 groundSunTrans = SampleTransmittanceLUT(groundPos, sunDir);
        float NdotL = saturate(dot(groundNormal, sunDir));
        radiance += transmittance * g_GroundAlbedo * groundSunTrans * NdotL * g_SunIlluminance * SHANYU_INV_PI;
    }

    // 太阳圆盘 (软边缘)
    float sunAngle = acos(saturate(cosTheta));
    float sunDisk = smoothstep(0.0095, 0.0090, sunAngle);
    radiance += transmittance * sunDisk * g_SunIlluminance;

    g_SkyOutputUAV[dispatchID.xy] = float4(radiance, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// AerialPerspectiveCS - 空中透视雾 (基于场景深度的距离衰减大气散射)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void AerialPerspectiveCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_APOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float depth = g_SceneDepthTex.SampleLevel(gPointClampSampler, uv, 0);

    // 重建世界位置
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;
    float4 worldPos = mul(g_AP_InvViewProj, float4(ndc, depth, 1.0));
    worldPos.xyz /= worldPos.w;

    float3 origin = g_AP_CameraPos;
    float3 dir = normalize(worldPos.xyz - origin);
    float dist = length(worldPos.xyz - origin);

    // 距离限制
    dist = min(dist, g_AP_FogMaxDistance);

    // 行星中心坐标
    float3 planetCenter = float3(0, -g_AP_PlanetRadius, 0);
    float3 localOrigin = origin - planetCenter;

    // 沿视线积分大气散射
    float3 transmittance = 1.0;
    float3 inScattering = 0.0;
    const int AP_SAMPLES = 16;
    float stepSize = dist / AP_SAMPLES;

    float3 sunDir = normalize(g_AP_SunDirection);
    float phaseR = RayleighPhase(dot(dir, sunDir));
    float phaseM = MiePhase(dot(dir, sunDir), g_MiePhaseG);

    for (int i = 0; i < AP_SAMPLES; i++)
    {
        float t = (i + 0.5) * stepSize;
        float3 p = localOrigin + dir * t;
        float height = length(p) - g_AP_PlanetRadius;
        height = max(height, 0.0);

        float densityR = exp(-height / g_RayleighScaleHeight) * g_AP_FogDensity;
        float densityM = exp(-height / g_MieScaleHeight) * g_AP_FogDensity;

        float3 sunTrans = SampleTransmittanceLUT(p, sunDir);

        float3 scatterR = g_RayleighScattering * phaseR * densityR * stepSize;
        float3 scatterM = g_MieScattering.xxx * phaseM * densityM * stepSize;
        inScattering += transmittance * sunTrans * (scatterR + scatterM);

        transmittance *= exp(-(g_RayleighScattering * densityR + g_MieScattering.xxx * densityM) * stepSize);
    }

    // 输出: 透射率 (xyz) 与内散射 (w 通道混合权重)
    g_APOutputUAV[dispatchID.xy] = float4(inScattering, 1.0 - Luminance(transmittance));
}

// ════════════════════════════════════════════════════════════════════════════
// CloudMarchCS - 体积云光线步进 (raymarching through cloud layer)
// 使用 fbm 噪声 + 天气图控制覆盖率, 实现啤酒-兰伯特吸收与散射
// ════════════════════════════════════════════════════════════════════════════

// 高度密度曲线: 底部与顶部渐变
float CloudHeightDensity(float heightFraction)
{
    // 双锥形: 中间最密, 上下渐弱
    float h = heightFraction;
    return saturate(smoothstep(0.0, 0.15, h) * smoothstep(1.0, 0.7, h));
}

// 采样云密度
float SampleCloudDensity(float3 pos)
{
    float height = pos.y;
    if (height < g_CloudBottom || height > g_CloudTop) return 0.0;

    float heightFraction = (height - g_CloudBottom) / (g_CloudTop - g_CloudBottom);

    // 风偏移
    float2 windOffset = g_CloudWindDir * g_CloudTime;
    float3 samplePos = float3(pos.xz + windOffset, height * 0.01).xxy;
    samplePos = pos + float3(windOffset.x, 0, windOffset.y) * 0.01;

    // 形状噪声 (fbm)
    float shape = g_CloudShapeNoise.SampleLevel(gLinearWrapSampler, samplePos * 0.002, 0);
    // 细节噪声 (高频)
    float detail = g_CloudDetailNoise.SampleLevel(gLinearWrapSampler, samplePos * 0.008, 0);

    // 天气图覆盖率
    float2 weatherUV = (pos.xz + windOffset) * 0.0001;
    float weather = g_CloudWeatherMap.SampleLevel(gLinearWrapSampler, weatherUV, 0);

    // 覆盖率调制
    float coverage = saturate(weather * g_CloudCoverage);
    float baseDensity = shape - (1.0 - coverage);
    baseDensity -= detail * 0.15 * (1.0 - heightFraction);

    float heightDensity = CloudHeightDensity(heightFraction);
    return max(baseDensity, 0.0) * heightDensity * g_CloudDensity;
}

// 亨尼-格林斯坦相函数 (云)
float CloudPhase(float cosTheta)
{
    float g = 0.2; // 前向偏置
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * SHANYU_PI * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5));
}

[numthreads(8, 8, 1)]
void CloudMarchCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_CloudColorUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    // 重建世界射线
    float4 farPoint = mul(g_CloudInvViewProj, float4(ndc, 1.0, 1.0));
    float3 rayDir = normalize(farPoint.xyz / farPoint.w - g_CloudCameraPos);
    float3 rayOrigin = g_CloudCameraPos;

    // 与云层 (底部/顶部平面) 相交
    float tBottom = (g_CloudBottom - rayOrigin.y) / rayDir.y;
    float tTop = (g_CloudTop - rayOrigin.y) / rayDir.y;
    if (rayDir.y < 0.001 && tBottom < 0.0)
    {
        g_CloudColorUAV[dispatchID.xy] = float4(0, 0, 0, 0);
        g_CloudAlphaUAV[dispatchID.xy] = 0.0;
        return;
    }

    float tStart = min(tBottom, tTop);
    float tEnd = max(tBottom, tTop);
    if (tStart < 0.0) tStart = 0.0;

    float3 sunDir = normalize(g_CloudSunDirection);
    int steps = g_CloudSteps;

    // 主光线步进
    float3 radiance = 0.0;
    float transmittance = 1.0;
    float stepSize = (tEnd - tStart) / steps;

    for (int i = 0; i < steps; i++)
    {
        float t = tStart + (i + 0.5) * stepSize;
        float3 p = rayOrigin + rayDir * t;
        float density = SampleCloudDensity(p);

        if (density > 0.001)
        {
            // 啤酒-兰伯特吸收
            float extinction = density * stepSize;
            float sampleTrans = exp(-extinction);

            // 光线步进到太阳方向的透射率 (少步数近似)
            float lightTransmittance = 1.0;
            const int LIGHT_STEPS = 6;
            float lightStep = stepSize * 2.0;
            for (int j = 0; j < LIGHT_STEPS; j++)
            {
                float3 lp = p + sunDir * ((j + 0.5) * lightStep);
                float ld = SampleCloudDensity(lp);
                lightTransmittance *= exp(-ld * lightStep);
                if (lightTransmittance < 0.01) break;
            }

            // 粉末效应 (小密度时增强散射)
            float powder = 1.0 - exp(-extinction * 2.0);
            float phase = CloudPhase(dot(rayDir, sunDir));

            float3 scatter = g_SunIlluminance * phase * lightTransmittance * (1.0 + powder) * 0.5;
            radiance += transmittance * scatter * (1.0 - sampleTrans) * 0.3;
            transmittance *= sampleTrans;

            if (transmittance < 0.01) break;
        }
    }

    float alpha = 1.0 - transmittance;
    g_CloudColorUAV[dispatchID.xy] = float4(radiance, alpha);
    g_CloudAlphaUAV[dispatchID.xy] = alpha;
}

// ════════════════════════════════════════════════════════════════════════════
// GodRayCS - 屏幕空间径向光柱 (radial blur toward sun position)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void GodRayCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_GodRayOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);

    // 太阳屏幕位置 (UV 空间)
    float2 sunUV = g_GR_SunScreenPos * 0.5 + 0.5;

    // 径向采样方向: 从当前像素朝向太阳
    float2 deltaTex = (sunUV - uv) / g_GR_Samples;
    float2 texCoord = uv;

    float3 color = g_GodRaySceneTex.SampleLevel(gLinearClampSampler, uv, 0).rgb;
    float illuminance = 1.0;
    float3 godRayColor = 0.0;

    for (int i = 0; i < g_GR_Samples; i++)
    {
        texCoord += deltaTex;
        float3 sampleColor = g_GodRaySceneTex.SampleLevel(gLinearClampSampler, texCoord, 0).rgb;
        // 高亮采样贡献
        godRayColor += sampleColor * illuminance * 0.01;
        illuminance *= g_GR_Decay;
    }

    godRayColor *= g_GR_Intensity * g_GR_Exposure;

    // 与原始场景颜色叠加
    float3 result = color + godRayColor;
    g_GodRayOutputUAV[dispatchID.xy] = float4(result, 1.0);
}
