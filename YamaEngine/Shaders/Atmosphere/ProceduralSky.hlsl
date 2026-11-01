// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - Procedural Sky, Starfield & Celestial Disks
// 程序化天空: fbm 云层, 星空 (哈希星点 + 银河带), 太阳/月亮圆盘与光晕,
// 与大气散射模型 (Preetham/Hosek/Nishita) 混合。
// Shader Model 6.6 (DXC)
// 入口点: StarfieldCS, ProceduralSkyCS, CelestialDiskCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# ProceduralSkyCB / StarfieldCB / CelestialDiskCB 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer ProceduralSkyCB : register(b0)
{
    float3 g_PS_CameraPosition;
    int   g_PS_SkyModel;          // 0=Preetham, 1=Hosek, 2=Nishita
    float3 g_PS_SunDirection;
    float  g_PS_SunIlluminance;
    float3 g_PS_MoonDirection;
    float  g_PS_MoonIlluminance;
    float  g_PS_PlanetRadius;
    float  g_PS_AtmosphereThickness;
    float  g_PS_RayleighScaleHeight;
    float  g_PS_MieScaleHeight;
    float3 g_PS_RayleighScattering;
    float  g_PS_MieScattering;
    float  g_PS_MiePhaseG;
    float  g_PS_Turbidity;
    float  g_PS_MultiScatterStrength;
    float  g_PS_StarIntensity;
    float2 g_PS_ViewportSize;
    float  g_PS_Time;
    float  g_PS_Pad0;
};

cbuffer StarfieldCB : register(b0)
{
    int   g_SF_StarCount;
    int   g_SF_DrawConstellations;
    float g_SF_MilkyWayIntensity;
    float g_SF_TwinkleStrength;
    float4x4 g_SF_ViewRotation;
    float g_SF_Time;
    float g_SF_Pad0;
    float g_SF_Pad1;
    float g_SF_Pad2;
};

cbuffer CelestialDiskCB : register(b0)
{
    float2 g_CD_ScreenPos;       // NDC
    float  g_CD_AngularRadius;
    int    g_CD_IsSun;
    float3 g_CD_Color;
    float  g_CD_GlareIntensity;
    float  g_CD_LensFlareIntensity;
    float2 g_CD_ViewportSize;
    float  g_CD_Pad0;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
RWTexture2D<float4> g_SkyOutputUAV    : register(u0); // 天空颜色
RWTexture2D<float4> g_StarOutputUAV   : register(u0); // 星空输出 (与天空共享 u0, 不同 PSO)
RWTexture2D<float4> g_CelestialOutputUAV : register(u0); // 天体圆盘输出

Texture2D<float4>   g_PrevSkyTex      : register(t0); // 大气散射结果 (用于混合)
Texture2D<float>    g_CloudDepthTex   : register(t1); // 云层 alpha

// ──────────────────────────────────────────────────────────────────────────
// 程序化噪声 (用于云层与银河)
// ──────────────────────────────────────────────────────────────────────────

// 3D 哈希噪声
float3 hash33(float3 p)
{
    p = float3(dot(p, float3(127.1, 311.7, 74.7)),
               dot(p, float3(269.5, 183.3, 246.1)),
               dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p) * 43758.5453123) * 2.0 - 1.0;
}

// 梯度噪声
float gradNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float n000 = dot(hash33(i + float3(0, 0, 0)), f - float3(0, 0, 0));
    float n100 = dot(hash33(i + float3(1, 0, 0)), f - float3(1, 0, 0));
    float n010 = dot(hash33(i + float3(0, 1, 0)), f - float3(0, 1, 0));
    float n110 = dot(hash33(i + float3(1, 1, 0)), f - float3(1, 1, 0));
    float n001 = dot(hash33(i + float3(0, 0, 1)), f - float3(0, 0, 1));
    float n101 = dot(hash33(i + float3(1, 0, 1)), f - float3(1, 0, 1));
    float n011 = dot(hash33(i + float3(0, 1, 1)), f - float3(0, 1, 1));
    float n111 = dot(hash33(i + float3(1, 1, 1)), f - float3(1, 1, 1));

    float n00 = lerp(n000, n100, f.x);
    float n10 = lerp(n010, n110, f.x);
    float n01 = lerp(n001, n101, f.x);
    float n11 = lerp(n011, n111, f.x);
    float n0 = lerp(n00, n10, f.y);
    float n1 = lerp(n01, n11, f.y);
    return lerp(n0, n1, f.z) * 0.5 + 0.5;
}

// 分形布朗运动 (fbm)
float fbm3(float3 p, int octaves)
{
    float value = 0.0;
    float amplitude = 0.5;
    float frequency = 1.0;
    for (int i = 0; i < octaves; i++)
    {
        value += amplitude * gradNoise(p * frequency);
        frequency *= 2.0;
        amplitude *= 0.5;
    }
    return value;
}

// ──────────────────────────────────────────────────────────────────────────
// 大气散射近似 (Preetham 模型简化)
// ──────────────────────────────────────────────────────────────────────────
float RayleighPhaseSky(float cosTheta)
{
    return 0.0596831 * (1.0 + cosTheta * cosTheta);
}
float MiePhaseSky(float cosTheta, float g)
{
    float g2 = g * g;
    return 0.1193662 * (1.0 - g2) / pow(1.0 + g2 - 2.0 * g * cosTheta + 1e-7, 1.5);
}

// 简化 Preetham 天空颜色
float3 PreethamSky(float3 dir, float3 sunDir)
{
    float cosTheta = dot(dir, sunDir);
    float cosSunZenith = sunDir.y; // 太阳天顶角余弦

    // 瑞利与米氏散射系数 (随浊度调整)
    float3 betaR = g_PS_RayleighScattering * (1.0 + g_PS_Turbidity * 0.1);
    float3 betaM = g_PS_MieScattering.xxx * (1.0 + g_PS_Turbidity * 0.2);

    // 天顶方向的散射
    float zenith = clamp(cosSunZenith, 0.0, 1.0);
    float3 skyZenith = (betaR + betaM) / (betaR + betaM + 0.0001);

    float phaseR = RayleighPhaseSky(cosTheta);
    float phaseM = MiePhaseSky(cosTheta, g_PS_MiePhaseG);

    // 衰减随天顶角
    float sunFalloff = pow(saturate(cosSunZenith), 0.25);
    float3 skyColor = (betaR * phaseR + betaM * phaseM) * sunFalloff;
    skyColor += skyZenith * 0.3 * g_PS_MultiScatterStrength;

    // 地平线增亮
    float horizonGlow = pow(1.0 - saturate(abs(dir.y)), 8.0);
    skyColor += horizonGlow * float3(0.8, 0.6, 0.4) * 0.3 * sunFalloff;

    return skyColor * g_PS_SunIlluminance * 0.0001;
}

// ──────────────────────────────────────────────────────────────────────────
// 程序化云层
// ──────────────────────────────────────────────────────────────────────────
float3 SampleProceduralClouds(float3 dir, float3 sunDir, float time)
{
    // 仅在上半球且接近水平时绘制云层
    if (dir.y < 0.02) return 0.0;

    // 云层平面投影 (固定高度)
    float cloudHeight = 2000.0;
    float t = cloudHeight / max(dir.y, 0.05);
    float2 cloudUV = dir.xz * t * 0.0005 + time * 0.005;

    // fbm 云密度
    float cloud = fbm3(float3(cloudUV * 2.0, time * 0.02), 5);
    cloud = smoothstep(0.45, 0.75, cloud);

    // 云光照: 朝太阳一侧亮, 背光暗
    float lightDir = dot(normalize(float3(dir.x, 0, dir.z)), normalize(float3(sunDir.x, 0, sunDir.z)));
    float cloudLit = lerp(0.4, 1.2, saturate(lightDir * 0.5 + 0.5));

    float3 cloudColor = float3(1.0, 0.98, 0.95) * cloudLit;
    // 云在太阳附近染金
    float sunGlow = pow(saturate(dot(dir, sunDir)), 8.0);
    cloudColor = lerp(cloudColor, float3(1.0, 0.85, 0.6), sunGlow * 0.5);

    return cloudColor * cloud * 0.8;
}

// ════════════════════════════════════════════════════════════════════════════
// StarfieldCS - 程序化星空
// 使用哈希函数在球面上分布星点, 叠加银河带与闪烁
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void StarfieldCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_StarOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    // 重建视图方向 (假设 FOV 90 度的简化)
    float3 viewDir = normalize(float3(ndc.x, ndc.y, 1.0));
    viewDir = mul((float3x3)g_SF_ViewRotation, viewDir);

    // ── 银河带 ──
    // 银河沿一条倾斜带分布, 用噪声调制
    float3 galacticPlane = normalize(float3(0.3, 0.4, 1.0));
    float3 galacticNormal = normalize(cross(galacticPlane, float3(0, 1, 0)));
    float bandDist = abs(dot(viewDir, galacticNormal));
    float milkyWay = smoothstep(0.25, 0.0, bandDist);
    milkyWay *= fbm3(viewDir * 8.0, 4); // 噪声调制密度
    milkyWay *= g_SF_MilkyWayIntensity;

    float3 milkyColor = lerp(float3(0.4, 0.5, 0.8), float3(0.9, 0.85, 0.7), fbm3(viewDir * 16.0, 3));
    float3 starColor = milkyColor * milkyWay * 0.3;

    // ── 星点 (基于网格哈希) ──
    // 在球面上以多分辨率网格采样星点
    float starBrightness = 0.0;
    float3 starTint = 0.0;

    [unroll]
    for (int layer = 0; layer < 3; layer++)
    {
        float scale = 40.0 * (1 << layer);
        float3 gridPos = viewDir * scale;
        float3 cell = floor(gridPos);
        float3 cellCenter = cell + 0.5;

        // 在 3x3x3 邻域内查找最近星点
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        [unroll]
        for (int dy = -1; dy <= 1; dy++)
        [unroll]
        for (int dz = -1; dz <= 1; dz++)
        {
            float3 neighbor = cell + float3(dx, dy, dz);
            float3 h = hash33(neighbor + layer * 17.0);
            // 阈值控制星点密度
            float starProb = 0.08 / (1 << layer);
            if (length(h) < starProb * 6.0)
            {
                float3 starPos = neighbor + h * 0.5;
                float dist = length(gridPos - starPos);
                // 星点大小
                float starSize = (0.15 + abs(h.x) * 0.2) / (1 << layer);
                float intensity = exp(-dist * dist / (starSize * starSize));

                // 闪烁
                float twinkle = 1.0 + sin(g_SF_Time * 3.0 + h.x * 100.0) * g_SF_TwinkleStrength;
                intensity *= twinkle;

                // 星色温 (基于哈希)
                float temp = 0.5 + h.y * 0.5;
                float3 tint = lerp(float3(1.0, 0.7, 0.5), float3(0.6, 0.8, 1.0), temp);

                starBrightness += intensity;
                starTint += tint * intensity;
            }
        }
    }

    // 仅在背向太阳时显示星空
    float sunFade = 1.0 - saturate(dot(viewDir, g_PS_SunDirection) * 2.0 + 0.3);
    sunFade = saturate(sunFade);

    starBrightness *= g_PS_StarIntensity * sunFade;
    starTint = starBrightness > 0.001 ? starTint / max(starBrightness, 0.001) : 0.0;

    float3 finalColor = starColor + starTint * starBrightness;
    g_StarOutputUAV[dispatchID.xy] = float4(finalColor, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// ProceduralSkyCS - 程序化天空 (大气散射 + 程序化云层混合)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void ProceduralSkyCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SkyOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    // 视图方向 (简化, 假设 FOV)
    float3 viewDir = normalize(float3(ndc.x * 1.6, ndc.y * 1.6, 1.0));

    float3 sunDir = normalize(g_PS_SunDirection);
    float3 moonDir = normalize(g_PS_MoonDirection);

    // ── 大气散射背景 ──
    float3 skyColor = 0.0;
    if (g_PS_SkyModel == 0)
        skyColor = PreethamSky(viewDir, sunDir);
    else
        skyColor = PreethamSky(viewDir, sunDir); // 其他模型复用简化版

    // 夜晚时天空变暗
    float dayFactor = saturate(sunDir.y * 2.0 + 0.2);
    skyColor *= lerp(0.02, 1.0, dayFactor);

    // ── 月亮散射贡献 (夜晚) ──
    if (g_PS_MoonIlluminance > 0.0)
    {
        float moonCos = dot(viewDir, moonDir);
        float moonPhase = RayleighPhaseSky(moonCos);
        float3 moonScatter = g_PS_RayleighScattering * moonPhase * g_PS_MoonIlluminance * 0.0001;
        skyColor += moonScatter * (1.0 - dayFactor);
    }

    // ── 程序化云层 ──
    float3 cloudColor = SampleProceduralClouds(viewDir, sunDir, g_PS_Time);

    // 云层 alpha (基于密度)
    float cloudAlpha = saturate(Luminance(cloudColor) * 1.5);
    cloudAlpha *= saturate(dayFactor * 2.0); // 夜晚云层暗

    // 混合云层与天空
    float3 finalColor = lerp(skyColor, cloudColor, cloudAlpha);

    // ── 太阳辉光 (大气光晕) ──
    float sunAngle = dot(viewDir, sunDir);
    float sunGlow = pow(saturate(sunAngle), 256.0) * 0.5;
    sunGlow += pow(saturate(sunAngle), 32.0) * 0.1;
    finalColor += sunGlow * float3(1.0, 0.9, 0.7) * dayFactor;

    g_SkyOutputUAV[dispatchID.xy] = float4(finalColor, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// CelestialDiskCS - 太阳/月亮圆盘与光晕渲染
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void CelestialDiskCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_CelestialOutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    // 像素到 NDC 的方向, 考虑宽高比
    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    // 宽高比修正
    float aspect = g_CD_ViewportSize.x / max(g_CD_ViewportSize.y, 1.0);
    float2 toCenter = ndc - g_CD_ScreenPos;
    toCenter.x *= aspect;
    float dist = length(toCenter);

    // 圆盘半径 (NDC 空间)
    float diskRadius = g_CD_AngularRadius * 4.0;

    // ── 圆盘本体 (软边缘) ──
    float disk = smoothstep(diskRadius + 0.002, diskRadius - 0.002, dist);
    // 太阳表面有微妙的渐变 (中心更亮)
    float surfaceGradient = 1.0 - smoothstep(0.0, diskRadius, dist) * 0.2;
    float3 diskColor = g_CD_Color * surfaceGradient;

    // 月亮表面纹理 (环形山阴影)
    if (g_CD_IsSun == 0)
    {
        float2 moonUV = (ndc - g_CD_ScreenPos) / (diskRadius * 2.0) + 0.5;
        float craters = fbm3(float3(moonUV * 8.0, 0.0), 4);
        diskColor *= 0.5 + craters * 0.7;
    }

    // ── 光晕 (glare) ──
    // 多层径向衰减
    float glare1 = pow(saturate(1.0 - dist / (diskRadius * 4.0)), 4.0);
    float glare2 = pow(saturate(1.0 - dist / (diskRadius * 8.0)), 8.0);
    float glare = (glare1 * 0.6 + glare2 * 0.4) * g_CD_GlareIntensity;
    float3 glareColor = g_CD_Color * glare;

    // 十字光芒 (star spike)
    float2 spikeDir = normalize(toCenter + 1e-6);
    float spike = pow(abs(spikeDir.x), 64.0) + pow(abs(spikeDir.y), 64.0);
    spike *= exp(-dist * 8.0) * g_CD_GlareIntensity * 0.5;
    float3 spikeColor = g_CD_Color * spike;

    // 镜头眩光环 (光环)
    float ringDist = abs(dist - diskRadius * 3.0);
    float ring = exp(-ringDist * 200.0) * g_CD_LensFlareIntensity * 0.3;
    float3 ringColor = g_CD_Color * ring;

    // 合成
    float3 result = glareColor + spikeColor + ringColor;
    result = lerp(result, diskColor, disk);

    float alpha = max(disk, max(glare * 0.5, max(spike, ring)));
    g_CelestialOutputUAV[dispatchID.xy] = float4(result, alpha);
}
