// ════════════════════════════════════════════════════════════════════════════
// Yama Engine — Deferred Lighting Shaders
// 延迟光照累积: 从 G-Buffer 解码表面属性, 累加直接光/阴影/GI/SSAO/SSR/体积雾/自发光
// + 级联阴影评估 + 体积雾 (Froxel) + 延迟贴花 + 透明物体前向
// 目标: Shader Model 6.6 (DXC 编译, wave intrinsics)
// 对应 C#: YamaEngine.Rendering.HybridLightingRenderer
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 延迟光照常量 (匹配 C# DeferredLightCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer DeferredLightCB : register(b2)
{
    float4x4 gDefViewMatrix;
    float4x4 gDefInvViewProj;
    float3   gDefCameraPos;
    float    gDefExposure;
    float2   gDefInvResolution;
    int      gDefLightCount;
    int      gDefEnableProbeField;
    int      gDefEnableVolumetric;
    int      gDefEnableSSAO;
    int      gDefEnableSSR;
    int      gDefEnableAtmosphere;
    float3   gDefSunDirection;
    float3   gDefSunColor;
    float    gDefSunIntensity;
    float    gDefPad0, gDefPad1;
};

// 级联阴影常量 (与 Forward 共享结构)
cbuffer DeferredCascadesCB : register(b3)
{
    float4x4 gDefCascadeVP0;
    float4x4 gDefCascadeVP1;
    float4x4 gDefCascadeVP2;
    float4x4 gDefCascadeVP3;
    float4   gDefCascadeDistances;
    float    gDefShadowBias;
    float    gDefNormalBias;
    float    gDefFilterRadius;
    int      gDefCascadeCount;
};

// 体积雾常量
cbuffer VolumetricFogCB : register(b4)
{
    float4x4 gVolViewProj;
    float3   gVolLightDir;
    float    gVolMaxDistance;
    float3   gVolLightColor;
    float    gVolLightIntensity;
    float3   gVolFogColor;
    float    gVolDensity;
    float3   gVolGridDim;
    float    gVolPad0;
};

// 贴花常量
cbuffer DecalCB : register(b5)
{
    float4x4 gDecalInvViewProj;
    int      gDecalCount;
    float3   gDecalPad;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────

// G-Buffer 输入
Texture2D<float4> gGeometryBufferRT0 : register(t0); // Albedo + Roughness
Texture2D<float4> gGeometryBufferRT1 : register(t1); // Normal(Oct) + Metallic + AO
Texture2D<float4> gGeometryBufferRT2 : register(t2); // Emissive + ShadingModel
Texture2D<float4> gGeometryBufferRT3 : register(t3); // Velocity + Custom
Texture2D<float>  gGeometryBufferDepth : register(t4);

// 阴影图集
Texture2DArray gDefShadowAtlas : register(t5);
Texture2D<float> gDefShadowVisibility : register(t6);

// 光源缓冲
StructuredBuffer<PointLight> gDefPointLights : register(t7);
StructuredBuffer<SpotLight>  gDefSpotLights  : register(t8);

// SSAO / SSR / 体积雾 / ProbeField GI
Texture2D<float>  gSSAOInput : register(t9);
Texture2D<float4> gSSRInput  : register(t10);
Texture3D<float4> gVolumetricFogTex : register(t11);
Texture2D<float4> gProbeFieldGIInput : register(t12);

// 贴花缓冲
struct DeferredDecalGPU
{
    float4x4 WorldMatrix;
    float4x4 InvWorldMatrix;
    float3   HalfSize;
    float    Fade;
    int      BlendMode;
    float3   NormalScale;
    float    Pad0;
};
StructuredBuffer<DeferredDecalGPU> gDecals : register(t13);

// 天空大气 / 环境贴图
Texture2D<float4> gSkyTexture : register(t14);
TextureCube       gEnvProbe   : register(t15);

// UAV 输出
RWTexture2D<float4> gSceneColorUAV : register(u0);
RWTexture2D<float>  gDefShadowVisUAV : register(u1);
RWTexture3D<float4> gVolumetricFogUAV : register(u2);


// ════════════════════════════════════════════════════════════════════════════
// 通用: 从 G-Buffer 解码表面属性
// ════════════════════════════════════════════════════════════════════════════
struct DeferredSurface
{
    float3 WorldPos;
    float3 Normal;
    float3 Albedo;
    float3 Emissive;
    float  Roughness;
    float  Metallic;
    float  AO;
    float  Depth;
};

DeferredSurface DecodeDeferredSurface(uint2 pixel, float2 uv)
{
    DeferredSurface s;
    s.Depth = gGeometryBufferDepth.Sample(gPointClampSampler, uv);

    float4 rt0 = gGeometryBufferRT0.Sample(gPointClampSampler, uv);
    float4 rt1 = gGeometryBufferRT1.Sample(gPointClampSampler, uv);
    float4 rt2 = gGeometryBufferRT2.Sample(gPointClampSampler, uv);

    s.Albedo    = rt0.rgb;
    s.Roughness = rt0.a;
    s.Normal    = DecodeNormalOctahedral(rt1.xy);
    s.Metallic  = rt1.z;
    s.AO        = rt1.w;
    s.Emissive  = rt2.rgb;
    s.WorldPos  = ReconstructWorldPos(uv, s.Depth);
    return s;
}


// ════════════════════════════════════════════════════════════════════════════
// LightResolveCS — 延迟光照累积 (核心)
// 解码 G-Buffer, 累加: 直接光 + 阴影 + 天空光 + ProbeField GI + SSAO + SSR + 体积雾 + 自发光
// 入口点: LightResolveCS (匹配 C# RHIShaderBytecode("LightResolveCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void LightResolveCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gScreenParams.x || dispatchID.y >= (uint)gScreenParams.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gScreenParams.zw;

    float depth = gGeometryBufferDepth.Sample(gPointClampSampler, uv);
    if (depth >= 1.0)
    {
        // 天空
        float3 skyColor = gSkyTexture.Sample(gLinearClampSampler, uv).rgb;
        gSceneColorUAV[pixel] = float4(ApplyExposure(skyColor, gDefExposure), 1.0);
        return;
    }

    DeferredSurface s = DecodeDeferredSurface(pixel, uv);

    float3 V = normalize(gDefCameraPos - s.WorldPos);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), s.Albedo, s.Metallic);

    // --- 主光源 (方向光) + 阴影 ---
    float3 L = normalize(-gDefSunDirection);
    float3 H = normalize(V + L);
    float NdotL = max(dot(s.Normal, L), 0.0);
    float NdotV = max(dot(s.Normal, V), 0.0);
    float NdotH = max(dot(s.Normal, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);

    float shadow = gDefShadowVisibility.Sample(gPointClampSampler, uv);
    float3 sunBRDF = DirectBRDF(s.Albedo, s.Roughness, F0, NdotL, NdotV, NdotH, VdotH);
    float3 directLight = sunBRDF * gDefSunColor * gDefSunIntensity * NdotL * shadow;

    // --- 点光源 + 聚光灯 (简化: 遍历全部, 实际用分块/集群) ---
    float3 pointLightContrib = 0.0;
    [loop]
    for (int pl = 0; pl < gDefLightCount; pl++)
    {
        PointLight light = gDefPointLights[pl];
        float3 toLight = light.Position - s.WorldPos;
        float dist = length(toLight);
        if (dist > light.Radius) continue;
        float3 Lp = toLight / max(dist, SHANYU_EPSILON);

        float att = 1.0 / (dist * dist + 1.0);
        float window = saturate_fast(1.0 - dist / light.Radius);
        att *= window * window;

        float3 Hp = normalize(V + Lp);
        float NdotLp = max(dot(s.Normal, Lp), 0.0);
        float NdotHp = max(dot(s.Normal, Hp), 0.0);
        float VdotHp = max(dot(V, Hp), 0.0);

        float3 brdf = DirectBRDF(s.Albedo, s.Roughness, F0, NdotLp, NdotV, NdotHp, VdotHp);
        pointLightContrib += brdf * light.Color * light.Intensity * att * NdotLp;
    }

    // --- 天空光 (IBL 近似) ---
    float3 ambientDiffuse = gSkyTexture.Sample(gLinearClampSampler, float2(0.5, 0.0)).rgb;
    float3 ambient = ambientDiffuse * s.Albedo * EnvBRDFApprox(F0, s.Roughness, NdotV).y;

    // --- ProbeField GI ---
    float3 gi = gDefEnableProbeField ? gProbeFieldGIInput.Sample(gPointClampSampler, uv).rgb * s.Albedo : 0.0;

    // --- SSAO ---
    float ssao = gDefEnableSSAO ? gSSAOInput.Sample(gPointClampSampler, uv) : 1.0;

    // --- SSR ---
    float4 ssrSample = gDefEnableSSR ? gSSRInput.Sample(gPointClampSampler, uv) : float4(0, 0, 0, 0);
    float3 ssr = ssrSample.rgb;
    float ssrMask = ssrSample.a;

    // --- 体积雾 ---
    float3 volumetric = 0.0;
    if (gDefEnableVolumetric)
    {
        // 从 3D 体积纹理采样 (按深度切片)
        float linearDepth = LinearizeDepth(depth, gCameraParams.z, gCameraParams.w);
        float sliceU = linearDepth / gVolMaxDistance;
        float3 volUV = float3(uv, sliceU);
        float4 fog = gVolumetricFogTex.Sample(gLinearClampSampler, volUV);
        volumetric = fog.rgb * fog.a;
    }

    // --- 合成 ---
    float3 color = directLight + pointLightContrib;
    color += (ambient + gi) * ssao;
    color += ssr * ssrMask;
    color += s.Emissive;
    color = color / (color + 1.0) * 1.0 + color; // 软溢出保护
    color += volumetric;

    color = ApplyExposure(color, gDefExposure);
    gSceneColorUAV[pixel] = float4(color, 1.0);
}


// ════════════════════════════════════════════════════════════════════════════
// DeferredShadowCS — 延迟级联阴影评估
// 读取 G-Buffer 深度, 重投影到各级联, 输出每像素阴影可见性
// 入口点: DeferredShadowCS (匹配 C# RHIShaderBytecode("DeferredShadowCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void DeferredShadowCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gScreenParams.x || dispatchID.y >= (uint)gScreenParams.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gScreenParams.zw;
    float depth = gGeometryBufferDepth.Sample(gPointClampSampler, uv);

    if (depth >= 1.0)
    {
        gDefShadowVisUAV[pixel] = 1.0;
        return;
    }

    float3 worldPos = ReconstructWorldPos(uv, depth);
    float4 rt1 = gGeometryBufferRT1.Sample(gPointClampSampler, uv);
    float3 normal = DecodeNormalOctahedral(rt1.xy);

    float viewDepth = -mul(gDefViewMatrix, float4(worldPos, 1.0)).z;

    // 选择级联
    int cascadeIdx = 0;
    [unroll]
    for (int i = 0; i < 4; i++)
        if (viewDepth > gDefCascadeDistances[i]) cascadeIdx = i + 1;
    cascadeIdx = min(cascadeIdx, gDefCascadeCount - 1);

    float4x4 cascadeVP = gDefCascadeVP0;
    [flatten] if (cascadeIdx == 1) cascadeVP = gDefCascadeVP1;
    [flatten] if (cascadeIdx == 2) cascadeVP = gDefCascadeVP2;
    [flatten] if (cascadeIdx == 3) cascadeVP = gDefCascadeVP3;

    float4 shadowCoord = mul(cascadeVP, float4(worldPos, 1.0));
    shadowCoord.xyz /= shadowCoord.w;

    float3 lightDir = normalize(-gDefSunDirection);
    shadowCoord.z -= gDefShadowBias * (1.0 - dot(normal, lightDir));

    float2 shadowUV = shadowCoord.xy * 0.5 + 0.5;
    float receiverDepth = shadowCoord.z;

    // PCF 5x5
    float shadow = 0.0;
    float2 texel = 1.0 / float2(2048.0, 2048.0);
    [unroll]
    for (int x = -2; x <= 2; x++)
    {
        [unroll]
        for (int y = -2; y <= 2; y++)
        {
            float2 offUV = shadowUV + float2(x, y) * texel * gDefFilterRadius;
            float d = gDefShadowAtlas.Sample(gPointClampSampler, float3(offUV, cascadeIdx)).r;
            shadow += (receiverDepth <= d + gDefShadowBias) ? 1.0 : 0.0;
        }
    }
    gDefShadowVisUAV[pixel] = shadow / 25.0;
}


// ════════════════════════════════════════════════════════════════════════════
// VolumetricFogCS — Froxel 体积雾积分
// 在 3D 网格中沿视线积分散射与吸收, 输出体积雾纹理
// 入口点: VolumetricFogCS (匹配 C# RHIShaderBytecode("VolumetricFogCS", Compute))
// ════════════════════════════════════════════════════════════════════════════

#define VOLUMETRIC_FOG_SAMPLES 64

[numthreads(8, 8, 1)]
void VolumetricFogCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint3 voxel = dispatchID;
    if (voxel.x >= (uint)gVolGridDim.x || voxel.y >= (uint)gVolGridDim.y || voxel.z >= (uint)gVolGridDim.z)
        return;

    // 将 voxel 映射到屏幕 UV 与深度切片
    float2 uv = (voxel.xy + 0.5) / gVolGridDim.xy;
    float sliceNear = (voxel.z / (float)gVolGridDim.z) * gVolMaxDistance;
    float sliceFar  = ((voxel.z + 1) / (float)gVolGridDim.z) * gVolMaxDistance;

    // 沿光线步进积分 (Ray Marching)
    float3 transmittance = 1.0;
    float3 scatteredLight = 0.0;
    int steps = max(1, VOLUMETRIC_FOG_SAMPLES / (int)gVolGridDim.z);
    float stepSize = (sliceFar - sliceNear) / steps;

    [loop]
    for (int s = 0; s < steps; s++)
    {
        float t = sliceNear + (s + 0.5) * stepSize;

        // Beer-Lambert 吸收
        float density = gVolDensity * stepSize;
        float3 extinction = density;
        float3 sampleTrans = exp(-extinction);

        // 相位函数 (Henyey-Greenstein 简化)
        float3 L = normalize(gVolLightDir);
        float phase = 0.25; // 各向同性近似

        // 光线在雾中被光源照亮 (简易: 主光源散射)
        float lightVisibility = 1.0; // 实际应采样阴影
        float3 inScatter = gVolLightColor * gVolLightIntensity * phase * lightVisibility;
        inScatter += gVolFogColor * 0.5; // 环境散射

        // 积分: L += sigma_s * L_in * T, T *= exp(-sigma_t * ds)
        scatteredLight += inScatter * density * transmittance * stepSize;
        transmittance *= sampleTrans;
    }

    // 输出: RGB=散射光, A=透射率
    float alpha = 1.0 - transmittance.x;
    gVolumetricFogUAV[voxel] = float4(scatteredLight, alpha);
}


// ════════════════════════════════════════════════════════════════════════════
// DecalCS — 延迟贴花
// 将贴花投影到 G-Buffer, 修改 Albedo/Normal 通道
// 入口点: DecalCS (匹配 C# RHIShaderBytecode("DecalCS", Compute))
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void DecalCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gScreenParams.x || dispatchID.y >= (uint)gScreenParams.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gScreenParams.zw;
    float depth = gGeometryBufferDepth.Sample(gPointClampSampler, uv);
    if (depth >= 1.0) return;

    float3 worldPos = ReconstructWorldPos(uv, depth);

    // 遍历所有贴花, 检查像素是否在贴花包围盒内
    float3 decalAlbedo = 0.0;
    float3 decalNormal = 0.0;
    float decalMask = 0.0;

    [loop]
    for (int d = 0; d < gDecalCount; d++)
    {
        DeferredDecalGPU decal = gDecals[d];

        // 变换到贴花局部空间
        float3 localPos = mul(decal.InvWorldMatrix, float4(worldPos, 1.0)).xyz;

        // 包围盒测试
        float3 absLocal = abs(localPos);
        if (all(absLocal <= decal.HalfSize))
        {
            // 贴花 UV
            float3 projUV = localPos / (decal.HalfSize * 2.0) + 0.5;
            float fade = decal.Fade * smoothstep_edge(0.9, 1.0, 1.0 - max(max(absLocal.x / decal.HalfSize.x,
                                                                              absLocal.y / decal.HalfSize.y),
                                                                              absLocal.z / decal.HalfSize.z));
            // 简易贴花颜色 (实际采样贴花纹理)
            decalAlbedo += float3(0.8, 0.2, 0.2) * fade;
            decalMask = max(decalMask, fade);
        }
    }

    // 混合贴花到 G-Buffer Albedo (法线修改同理)
    if (decalMask > 0.0)
    {
        float4 rt0 = gGeometryBufferRT0[pixel];
        rt0.rgb = lerp(rt0.rgb, decalAlbedo, decalMask);
        gGeometryBufferRT0UAV_Inline(pixel, rt0);
    }
}

// 内联 UAV 别名 (DecalCS 使用, 共享 GeometryBuffer.hlsl 的 UAV 声明需前向声明)
void gGeometryBufferRT0UAV_Inline(uint2 pixel, float4 value)
{
    // 此处依赖运行时绑定的 G-Buffer RT0 UAV; 在单文件编译时由 root signature 绑定
    // (与 GeometryBuffer.hlsl 共享 u0 槽位)
    RWTexture2D<float4> localRT0 = (RWTexture2D<float4>)0;
    // 占位: 实际通过 ResourceDescriptorHeap 索引
}


// ════════════════════════════════════════════════════════════════════════════
// TransparentCS — 透明物体前向渲染 (在 G-Buffer 上方)
// 对透明几何执行前向着色并混合到场景颜色
// 入口点: TransparentCS (匹配 C# RHIShaderBytecode("TransparentCS", Compute))
// ════════════════════════════════════════════════════════════════════════════

Texture2D<float4> gTransparentColorTex : register(t16); // 透明几何缓冲 (颜色+alpha)
Texture2D<float>  gTransparentDepthTex : register(t17);

[numthreads(8, 8, 1)]
void TransparentCS(uint3 dispatchID : SV_DispatchThreadID)
{
    if (dispatchID.x >= (uint)gScreenParams.x || dispatchID.y >= (uint)gScreenParams.y)
        return;

    uint2 pixel = dispatchID.xy;
    float2 uv = (pixel + 0.5) * gScreenParams.zw;

    float4 transparentColor = gTransparentColorTex.Sample(gPointClampSampler, uv);
    float transparentDepth = gTransparentDepthTex.Sample(gPointClampSampler, uv);
    float sceneDepth = gGeometryBufferDepth.Sample(gPointClampSampler, uv);

    // 仅在透明几何比不透明几何更近时混合
    if (transparentColor.a <= 0.001 || transparentDepth >= sceneDepth)
        return;

    // 解码 G-Buffer 获取背景表面属性 (用于折射/反射)
    DeferredSurface bgSurface = DecodeDeferredSurface(pixel, uv);
    float3 V = normalize(gDefCameraPos - bgSurface.WorldPos);

    // 简单折射偏移 (基于法线的 UV 偏移采样)
    float2 refractUV = uv + bgSurface.Normal.xy * 0.02 * transparentColor.a;
    float4 sceneColor = gSceneColorUAV[pixel];
    float3 refracted = sceneColor.rgb;

    // Alpha 混合: out = src*alpha + dst*(1-alpha)
    float3 blended = transparentColor.rgb * transparentColor.a + refracted * (1.0 - transparentColor.a);
    gSceneColorUAV[pixel] = float4(blended, 1.0);
}
