// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - Water Surface Rendering
// 水面渲染: FFT 位移顶点, 菲涅尔反射/折射, 屏幕空间焦散, 次表面散射近似,
// 太阳高光, 泡沫; 水下后处理 (深度雾/色散吸收/焦散/扭曲);
// 屏幕空间反射 (SSR) 与水下体积光。
// Shader Model 6.6 (DXC)
// 入口点: WaterVS, WaterPS, FullScreenVS, UnderwaterPS, SSRCS, VolumetricCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# WaterRenderCB / UnderwaterCB 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer WaterRenderCB : register(b0)
{
    float3 g_W_CameraPosition;
    float  g_W_Time;
    float4x4 g_W_ViewProjection;
    float4x4 g_W_InvViewProjection;
    float  g_W_WaterLevel;
    float  g_W_MeshSize;
    float3 g_W_DeepColor;
    float3 g_W_ShallowColor;
    float3 g_W_Absorption;
    float3 g_W_Scattering;
    float  g_W_Fresnel0;
    float3 g_W_SunDirection;
    float3 g_W_SunColor;
    float  g_W_SunSpecularIntensity;
    float  g_W_SunSpecularPower;
    float2 g_W_FlowDirection;
    float  g_W_FlowSpeed;
    float  g_W_CausticsIntensity;
    float  g_W_CausticsTiling;
    float  g_W_FoamIntensity;
    float  g_W_FoamDissipation;
    float  g_W_ReflectionIntensity;
    float  g_W_RefractionIntensity;
    int    g_W_MeshType;
    float2 g_W_ViewportSize;
    float  g_W_Pad0;
};

cbuffer UnderwaterCB : register(b0)
{
    int   g_UW_CameraUnderwater;
    float g_UW_WaterLevel;
    float3 g_UW_FogColor;
    float  g_UW_FogDensity;
    float3 g_UW_Absorption;
    float  g_UW_CausticsIntensity;
    float  g_UW_CausticsTiling;
    float  g_UW_DistortionStrength;
    float  g_UW_DistortionTiling;
    float4x4 g_UW_ViewProjection;
    float4x4 g_UW_InvViewProjection;
    float  g_UW_Time;
    float2 g_UW_ViewportSize;
    float  g_UW_Pad0;
};

cbuffer WaterSSRCB : register(b0)
{
    float4x4 g_SSR_ViewProj;
    float4x4 g_SSR_InvViewProj;
    float3 g_SSR_CameraPos;
    float  g_SSR_RayStep;
    float2 g_SSR_Resolution;
    int    g_SSR_MaxSteps;
    float  g_SSR_Thickness;
};

cbuffer WaterVolumetricCB : register(b0)
{
    float3 g_VOL_CameraPos;
    float  g_VOL_Time;
    float3 g_VOL_SunDirection;
    float  g_VOL_SunIntensity;
    float  g_VOL_WaterLevel;
    int    g_VOL_Steps;
    float2 g_VOL_Resolution;
    float4x4 g_VOL_InvViewProj;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
// 水面渲染
Texture2D<float4>   g_DisplacementMap : register(t0); // FFT 位移 (XYZ, J)
Texture2D<float4>   g_NormalJacobMap  : register(t1); // 法线 + 雅可比
Texture2D<float>    g_FoamMap         : register(t2);
Texture2D<float>    g_WetnessMap      : register(t3);
Texture2D<float4>   g_ReflectionTex   : register(t4); // 平面反射 RT
Texture2D<float4>   g_SceneColorTex   : register(t5); // 折射用场景颜色
Texture2D<float>    g_SceneDepthTex   : register(t6);
Texture2D<float4>   g_CausticsTex     : register(t7);
TextureCube<float4> g_SkyCube         : register(t8);

// 水下后处理
Texture2D<float4>   g_UW_SceneColor   : register(t0);
Texture2D<float>    g_UW_SceneDepth   : register(t1);

// SSR
Texture2D<float>    g_SSR_SceneDepth  : register(t0);
Texture2D<float4>   g_SSR_SceneColor  : register(t1);
RWTexture2D<float4> g_SSR_OutputUAV   : register(u0);

// 体积光
RWTexture2D<float4> g_VOL_OutputUAV   : register(u0);

// ──────────────────────────────────────────────────────────────────────────
// 顶点/像素结构
// ──────────────────────────────────────────────────────────────────────────
struct WaterVSInput
{
    float2 TexCoord : TEXCOORD0;
};

struct WaterVSOutput
{
    float4 Position  : SV_POSITION;
    float3 WorldPos  : TEXCOORD0;
    float3 Normal    : TEXCOORD1;
    float2 FlowUV    : TEXCOORD2;
    float  Foam      : TEXCOORD3;
    float  Jacobian  : TEXCOORD4;
};

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 采样位移图 (世界 XZ -> FFT 纹理 UV)
float3 SampleDisplacement(float2 worldXZ)
{
    float2 uv = worldXZ / g_W_MeshSize + 0.5;
    uv = frac(uv);
    return g_DisplacementMap.SampleLevel(gLinearWrapSampler, uv, 0).xyz;
}

float3 SampleNormal(float2 worldXZ)
{
    float2 uv = worldXZ / g_W_MeshSize + 0.5;
    uv = frac(uv);
    return normalize(g_NormalJacobMap.SampleLevel(gLinearWrapSampler, uv, 0).xyz);
}

// 菲涅尔 (Schlick)
float FresnelSchlick(float cosTheta, float F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - saturate(cosTheta), 5.0);
}

// 焦散纹理采样 (基于深度与时间)
float SampleCaustics(float3 worldPos, float depth)
{
    float2 causticsUV = worldPos.xz * g_W_CausticsTiling + g_W_Time * 0.1;
    float c1 = g_CausticsTex.SampleLevel(gLinearWrapSampler, causticsUV, 0).r;
    float2 causticsUV2 = worldPos.xz * g_W_CausticsTiling * 1.3 - g_W_Time * 0.13;
    float c2 = g_CausticsTex.SampleLevel(gLinearWrapSampler, causticsUV2, 0).r;
    float caustics = (c1 + c2) * 0.5;
    // 深度衰减
    caustics *= exp(-depth * 0.02);
    return caustics * g_W_CausticsIntensity;
}

// 水的次表面散射近似 (浅水颜色与深水颜色混合)
float3 WaterSubsurface(float depth, float3 normal, float3 viewDir, float3 sunDir)
{
    float3 shallow = g_W_ShallowColor;
    float3 deep = g_W_DeepColor;
    // 深度驱动颜色
    float depthFactor = saturate(depth * 0.05);
    float3 sssColor = lerp(shallow, deep, depthFactor);
    // 逆散射 (背光时水面发亮, 模拟光穿过波浪)
    float backScatter = saturate(dot(-viewDir, sunDir)) * pow(1.0 - saturate(dot(normal, viewDir)), 2.0);
    sssColor += g_W_SunColor * backScatter * g_W_Scattering * 0.5;
    return sssColor;
}

// ════════════════════════════════════════════════════════════════════════════
// WaterVS - 水面顶点着色器
// 从 FFT 位移图采样, 应用到平面网格顶点
// ════════════════════════════════════════════════════════════════════════════
WaterVSOutput WaterVS(WaterVSInput input)
{
    WaterVSOutput output;

    // 网格 UV -> 世界 XZ (以相机为中心的补丁)
    float2 worldXZ = (input.TexCoord - 0.5) * g_W_MeshSize + g_W_CameraPosition.xz;

    // 采样 FFT 位移
    float3 displacement = SampleDisplacement(worldXZ);
    float2 uv = frac(worldXZ / g_W_MeshSize + 0.5);
    float4 normalJacob = g_NormalJacobMap.SampleLevel(gLinearWrapSampler, uv, 0);

    // 应用位移
    float3 worldPos = float3(worldXZ.x + displacement.x,
                             g_W_WaterLevel + displacement.y,
                             worldXZ.y + displacement.z);

    output.Position = mul(g_W_ViewProjection, float4(worldPos, 1.0));
    output.WorldPos = worldPos;
    output.Normal = normalize(normalJacob.xyz);
    output.FlowUV = worldXZ * 0.1 + g_W_FlowDirection * g_W_FlowSpeed * g_W_Time;
    output.Foam = g_FoamMap.SampleLevel(gLinearWrapSampler, uv, 0);
    output.Jacobian = normalJacob.w;

    return output;
}

// ════════════════════════════════════════════════════════════════════════════
// WaterPS - 水面像素着色器
// 菲涅尔反射/折射, 焦散, 次表面散射, 太阳高光, 泡沫
// ════════════════════════════════════════════════════════════════════════════
float4 WaterPS(WaterVSOutput input) : SV_TARGET
{
    float3 normal = normalize(input.Normal);
    float3 viewDir = normalize(g_W_CameraPosition - input.WorldPos);
    float3 sunDir = normalize(g_W_SunDirection);

    // ── 深度 (水面到水底) ──
    float2 screenUV = input.Position.xy / g_W_ViewportSize;
    float sceneDepth = g_SceneDepthTex.SampleLevel(gPointClampSampler, screenUV, 0);
    float3 bottomPos = ReconstructWorldPos(screenUV, sceneDepth);
    float waterDepth = max(input.WorldPos.y - bottomPos.y, 0.0);

    // ── 菲涅尔 ──
    float NdotV = saturate(dot(normal, viewDir));
    float fresnel = FresnelSchlick(NdotV, g_W_Fresnel0);

    // ── 反射 (平面反射 RT + 天空立方体回退) ──
    float3 reflectDir = reflect(-viewDir, normal);
    float2 reflUV = screenUV + normal.xz * 0.02; // 法线扰动
    float3 reflection = g_ReflectionTex.SampleLevel(gLinearClampSampler, reflUV, 0).rgb;
    // 天空反射回退
    float3 skyReflect = g_SkyCube.SampleLevel(gLinearWrapSampler, reflectDir, 0).rgb;
    reflection = lerp(skyReflect, reflection, g_W_ReflectionIntensity);

    // ── 折射 (场景颜色 + 位移扰动) ──
    float2 refractOffset = normal.xz * 0.05 * g_W_RefractionIntensity;
    float2 refractUV = screenUV + refractOffset;
    float3 refraction = g_SceneColorTex.SampleLevel(gLinearClampSampler, refractUV, 0).rgb;
    // 深度吸收
    float3 absorption = exp(-g_W_Absorption * waterDepth);
    refraction *= absorption;
    // 深浅水颜色混合
    float3 waterColor = lerp(g_W_ShallowColor, g_W_DeepColor, saturate(waterDepth * 0.1));
    refraction = lerp(waterColor, refraction, g_W_RefractionIntensity);

    // ── 次表面散射近似 ──
    float3 sss = WaterSubsurface(waterDepth, normal, viewDir, sunDir);

    // ── 焦散 (投射到水底) ──
    float caustics = SampleCaustics(bottomPos, waterDepth);
    refraction += caustics * g_W_SunColor;

    // ── 太阳高光 (Blinn-Phong) ──
    float3 halfDir = normalize(sunDir + viewDir);
    float NdotH = saturate(dot(normal, halfDir));
    float sunSpec = pow(NdotH, g_W_SunSpecularPower) * g_W_SunSpecularIntensity;

    // ── 泡沫 (基于雅可比与泡沫图) ──
    float jacobian = input.Jacobian;
    float foamFromJacob = saturate(-jacobian * 2.0); // J < 0 时波浪翻转
    float foam = max(input.Foam, foamFromJacob) * g_W_FoamIntensity;
    foam = saturate(foam);

    // ── 合成 ──
    float3 color = lerp(refraction, reflection, fresnel);
    color += sss * (1.0 - fresnel) * 0.3;
    color += g_W_SunColor * sunSpec;
    // 泡沫覆盖
    color = lerp(color, float3(1.0, 1.0, 1.0), foam);

    return float4(color, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// FullScreenVS - 全屏三角形顶点着色器 (水下后处理用)
// ════════════════════════════════════════════════════════════════════════════
FullScreenTriangleVSOutput FullScreenVS(uint vertexID : SV_VertexID)
{
    return FullScreenTriangleVS(vertexID);
}

// ════════════════════════════════════════════════════════════════════════════
// UnderwaterPS - 水下后处理像素着色器
// 深度雾, 色散吸收, 焦散投影, 屏幕扭曲
// ════════════════════════════════════════════════════════════════════════════
float4 UnderwaterPS(FullScreenTriangleVSOutput input) : SV_TARGET
{
    float2 uv = input.TexCoord;

    // 屏幕扭曲 (水流扰动)
    float2 distortion = float2(
        sin(uv.y * 50.0 + g_UW_Time * 2.0) * 0.001,
        cos(uv.x * 50.0 + g_UW_Time * 1.7) * 0.001
    ) * g_UW_DistortionStrength;
    uv += distortion;

    float3 sceneColor = g_UW_SceneColor.SampleLevel(gLinearClampSampler, uv, 0).rgb;
    float depth = g_UW_SceneDepth.SampleLevel(gPointClampSampler, uv, 0);

    // 重建世界位置
    float3 worldPos = ReconstructWorldPos(uv, depth);

    // 水下深度 (从水面到像素)
    float underwaterDepth = max(g_UW_WaterLevel - worldPos.y, 0.0);

    // 色散吸收 (不同波长吸收率不同, 蓝绿光穿透更深)
    float3 absorption = exp(-g_UW_Absorption * underwaterDepth);

    // 焦散投影 (从水面投射到底部)
    float3 causticsPos = worldPos;
    float2 causticsUV = causticsPos.xz * g_UW_CausticsTiling + g_UW_Time * 0.5;
    float c1 = sin(causticsUV.x * 3.0 + g_UW_Time) * sin(causticsUV.y * 3.0 - g_UW_Time);
    float c2 = sin(causticsUV.x * 2.0 - g_UW_Time * 1.3) * cos(causticsUV.y * 2.3 + g_UW_Time * 0.7);
    float caustics = saturate((c1 + c2) * 0.5 + 0.5);
    caustics = pow(caustics, 3.0) * g_UW_CausticsIntensity;
    // 焦散随深度衰减
    caustics *= exp(-underwaterDepth * 0.05);

    // 深度雾
    float fogFactor = 1.0 - exp(-g_UW_FogDensity * underwaterDepth);

    // 合成
    float3 color = sceneColor * absorption;
    color += caustics * float3(0.6, 0.9, 1.0);
    color = lerp(color, g_UW_FogColor, fogFactor);

    return float4(color, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// SSRCS - 水面屏幕空间反射 (光线步进)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void SSRCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_SSR_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float depth = g_SSR_SceneDepth.SampleLevel(gPointClampSampler, uv, 0);

    // 重建世界位置与法线
    float3 worldPos = ReconstructWorldPos(uv, depth);
    // 仅对水面像素执行 (近似: 在水面高度附近)
    if (abs(worldPos.y - g_VOL_WaterLevel) > 2.0)
    {
        g_SSR_OutputUAV[dispatchID.xy] = float4(0, 0, 0, 0);
        return;
    }

    float3 viewDir = normalize(g_SSR_CameraPos - worldPos);
    // 估算法线 (从深度梯度)
    float depthR = g_SSR_SceneDepth.SampleLevel(gPointClampSampler, uv + float2(1, 0) / w, 0);
    float depthU = g_SSR_SceneDepth.SampleLevel(gPointClampSampler, uv + float2(0, 1) / h, 0);
    float3 posR = ReconstructWorldPos(uv + float2(1, 0) / w, depthR);
    float3 posU = ReconstructWorldPos(uv + float2(0, 1) / h, depthU);
    float3 normal = normalize(cross(posR - worldPos, posU - worldPos));

    // 反射方向
    float3 reflectDir = reflect(-viewDir, normal);

    // 屏幕空间光线步进
    float3 rayPos = worldPos + reflectDir * g_SSR_RayStep;
    float3 lastRayPos = rayPos;
    float2 rayUV = uv;
    float3 color = 0.0;
    float hitWeight = 0.0;

    for (int i = 0; i < g_SSR_MaxSteps; i++)
    {
        // 投影到屏幕
        float4 clipPos = mul(g_SSR_ViewProj, float4(rayPos, 1.0));
        float2 screenUV = clipPos.xy / clipPos.w * 0.5 + 0.5;
        screenUV.y = 1.0 - screenUV.y;

        if (any(screenUV < 0.0) || any(screenUV > 1.0)) break;

        float sampledDepth = g_SSR_SceneDepth.SampleLevel(gPointClampSampler, screenUV, 0);
        float3 sampledPos = ReconstructWorldPos(screenUV, sampledDepth);

        // 深度差检测命中
        float zDiff = rayPos.z - sampledPos.z;
        if (abs(zDiff) < g_SSR_Thickness && rayPos.z < sampledPos.z + 0.5)
        {
            color = g_SSR_SceneColor.SampleLevel(gLinearClampSampler, screenUV, 0).rgb;
            hitWeight = 1.0 - saturate(i / (float)g_SSR_MaxSteps);
            break;
        }

        lastRayPos = rayPos;
        rayPos += reflectDir * g_SSR_RayStep;
    }

    g_SSR_OutputUAV[dispatchID.xy] = float4(color * hitWeight, hitWeight);
}

// ════════════════════════════════════════════════════════════════════════════
// VolumetricCS - 水下体积光 (god rays)
// 从太阳方向沿视线积分散射, 产生光柱效果
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void VolumetricCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_VOL_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    // 重建世界射线
    float4 farPoint = mul(g_VOL_InvViewProj, float4(ndc, 1.0, 1.0));
    float3 rayDir = normalize(farPoint.xyz / farPoint.w - g_VOL_CameraPos);
    float3 rayOrigin = g_VOL_CameraPos;

    float3 sunDir = normalize(g_VOL_SunDirection);

    // 仅水下体积光
    bool cameraUnderwater = rayOrigin.y < g_VOL_WaterLevel;
    if (!cameraUnderwater)
    {
        g_VOL_OutputUAV[dispatchID.xy] = float4(0, 0, 0, 0);
        return;
    }

    // 沿视线积分 (到水面或最大距离)
    float maxDist = 100.0;
    float stepSize = maxDist / g_VOL_Steps;
    float3 radiance = 0.0;
    float transmittance = 1.0;

    for (int i = 0; i < g_VOL_Steps; i++)
    {
        float t = (i + 0.5) * stepSize;
        float3 p = rayOrigin + rayDir * t;

        // 到水面则停止
        if (p.y > g_VOL_WaterLevel) break;

        // 水的散射系数 (简化)
        float density = 0.02;
        float extinction = density * stepSize;
        float sampleTrans = exp(-extinction);

        // 太阳方向的透射率 (从该点到水面)
        float distToSurface = (g_VOL_WaterLevel - p.y) / max(sunDir.y, 0.01);
        float lightTrans = exp(-0.02 * distToSurface);

        // 相函数 (前向散射)
        float phase = 0.1 * (1.0 + dot(rayDir, sunDir) * dot(rayDir, sunDir));

        radiance += transmittance * lightTrans * g_VOL_SunIntensity * phase * (1.0 - sampleTrans);
        transmittance *= sampleTrans;
    }

    g_VOL_OutputUAV[dispatchID.xy] = float4(radiance, 1.0 - transmittance);
}
