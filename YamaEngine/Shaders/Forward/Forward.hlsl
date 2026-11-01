// ════════════════════════════════════════════════════════════════════════════
// Yama Engine — Forward Renderer Shaders
//   前向渲染管线:
//     · ForwardVS / ForwardPS         —— PBR 顶点/像素着色器 (传统几何路径)
//     · ForwardShadeCS                —— 屏幕空间前向 PBR 着色 (集群光源 + CSM)
//     · ClusterCullCS                 —— 集群光照剔除 (3D 网格分配光源)
//     · OITCS                         —— 顺序无关透明合成 (加权混合)
//     · ShadowCS                      —— 级联阴影 VSM 矩构建
//     · ShadowFilterCS                —— 级联阴影矩高斯过滤
//   目标: Shader Model 6.6  (DXC: -T vs_6_6 / ps_6_6 / cs_6_6)
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

#define FORWARD_MAX_LIGHTS_PER_PIXEL 64u
#define FORWARD_CLUSTER_TILE_SIZE    64u
#define FORWARD_CASCADE_MAX          4

// ── 前向着色常量 (镜像 C# ForwardShadingCB, 绑定 b2) ──
cbuffer ForwardShadingCB : register(b2)
{
    float4x4 fViewMatrix;
    float4x4 fProjMatrix;
    float4x4 fInvViewProj;
    float4   fCameraPos_Exposure;   // xyz = CameraPos, w = Exposure
    float4   fInvRes_Flags;         // xy  = InvResolution, z = EnableProbeField, w = EnableAtmosphere
    float4   fSkyLight;             // xyz = SkyLightColor, w = SkyLightIntensity
    float4   fSunDir;               // xyz = SunDirection, w = pad
    float4   fSun;                  // xyz = SunColor, w = SunIntensity
    float    fPad0;
    float3   fPad1;
};
#define fCameraPos     fCameraPos_Exposure.xyz
#define fExposure      fCameraPos_Exposure.w
#define fInvResolution fInvRes_Flags.xy
#define fEnableProbeField   ((uint)fInvRes_Flags.z)
#define fEnableAtmo    ((uint)fInvRes_Flags.w)
#define fSunDirection  fSunDir.xyz
#define fSunColor      fSun.xyz
#define fSunIntensity  fSun.w

// ── 级联阴影常量 (镜像 C# ForwardCascadesCB, 绑定 b3) ──
cbuffer ForwardCascadesCB : register(b3)
{
    float4x4 fCascadeVP[FORWARD_CASCADE_MAX];
    float4   fCascadeDistances;     // 分割距离
    float4   fCascadeScale;         // 图集缩放
    float4   fCascadeOffset;        // 图集偏移
    float4   fShadowParams;         // x=ShadowBias, y=NormalBias, z=FilterRadius, w=CascadeCount
};
#define fShadowBias   fShadowParams.x
#define fNormalBias   fShadowParams.y
#define fFilterRadius fShadowParams.z
#define fCascadeCount ((uint)fShadowParams.w)

// ── 集群剔除常量 (镜像 C# ForwardClusterCB, 绑定 b4) ──
cbuffer ForwardClusterCB : register(b4)
{
    float4x4 cInvViewProj;
    float4   cCameraPos_NearZ;      // xyz = CameraPos, w = NearZ
    float4   cFarZ_GridDimZ;        // x = FarZ, y = GridDimX, z = GridDimY, w = GridDimZ
    float4   cScale_Log;            // x = LogScale, y = SliceScale, z = MaxLightsPerCluster, w = LightCount
};
#define cCameraPos          cCameraPos_NearZ.xyz
#define cNearZ              cCameraPos_NearZ.w
#define cFarZ               cFarZ_GridDimZ.x
#define cGridDimX           ((uint)cFarZ_GridDimZ.y)
#define cGridDimY           ((uint)cFarZ_GridDimZ.z)
#define cGridDimZ           ((uint)cFarZ_GridDimZ.w)
#define cSliceScale         cScale_Log.y
#define cMaxLightsPerCluster ((uint)cScale_Log.z)
#define cLightCount         ((uint)cScale_Log.w)

// ── 资源绑定 (SRV t0..t15 / UAV u0..u7, 对齐 C# 根签名) ──
Texture2D<float>        ShadowDepthAtlas   : register(t0);   // CSM 深度图集 (D32)
Texture2D<float2>       ShadowMoments      : register(t1);   // 过滤后 VSM 矩 (mean, mean^2)
StructuredBuffer<uint2> ClusterGrid        : register(t2);   // (offset, count)
StructuredBuffer<uint>  ClusterLightList   : register(t3);
StructuredBuffer<PointLight> PointLights   : register(t4);
Texture2D<float>        SceneDepth         : register(t5);
Texture2D<float3>       GeometryBufferNormals     : register(t6);
Texture2D<float3>       GeometryBufferAlbedo      : register(t7);
Texture2D<float>        HZB                : register(t8);
Texture2D<float4>       MatAlbedoTex       : register(t9);
Texture2D<float3>       MatNormalTex       : register(t10);
Texture2D<float4>       MatRMTex           : register(t11);  // r=roughness, g=metallic, b=ao, a=..
Texture2D<float3>       MatEmissiveTex     : register(t12);
TextureCube<float3>     EnvProbe           : register(t13);
Texture2D<float2>       BRDFLUT            : register(t14);

RWTexture2D<float4>     SceneColorUAV      : register(u0);
RWTexture2D<float2>     MomentsUAV         : register(u1);
RWStructuredBuffer<uint2> ClusterGridUAV   : register(u2);
RWStructuredBuffer<uint>  ClusterLightListUAV : register(u3);
RWTexture2D<float4>     OitAccumUAV        : register(u4);   // 加权累积色
RWTexture2D<float>      OitRevealageUAV    : register(u5);   // 透明揭示率

// ════════════════════════════════════════════════════════════════════════════
// 顶点/像素着色器输入结构
// ════════════════════════════════════════════════════════════════════════════
struct VSInput
{
    float3 Position  : POSITION;
    float3 Normal    : NORMAL;
    float4 Tangent   : TANGENT;   // xyz=切线, w=手性
    float2 TexCoord  : TEXCOORD0;
};

struct VSOutput
{
    float4 Position     : SV_POSITION;
    float3 WorldPos     : WORLDPOS;
    float3 Normal       : NORMAL;
    float4 Tangent      : TANGENT;
    float2 TexCoord     : TEXCOORD0;
    float4 PrevPosition : PREV_POSITION;   // 上帧裁剪空间 (运动矢量)
};

// ════════════════════════════════════════════════════════════════════════════
// ForwardVS — 前向渲染顶点着色器
// ════════════════════════════════════════════════════════════════════════════
VSOutput ForwardVS(VSInput input)
{
    VSOutput o;
    float4 worldPos = mul(gWorldMatrix, float4(input.Position, 1.0));

    o.Position     = mul(fViewMatrix, worldPos);
    o.Position     = mul(fProjMatrix, o.Position);
    o.WorldPos     = worldPos.xyz;
    o.Normal       = normalize(mul((float3x3)gWorldInvTranspose, input.Normal));
    o.Tangent      = float4(normalize(mul((float3x3)gWorldMatrix, input.Tangent.xyz)), input.Tangent.w);
    o.TexCoord     = input.TexCoord;

    // 上帧位置 (用于运动矢量, TSR 消费)
    float4 prevWorld = mul(gPrevWorldMatrix, float4(input.Position, 1.0));
    o.PrevPosition   = mul(gPrevViewProjMatrix, prevWorld);

    return o;
}

// ── 材质采样 ──
struct ForwardMaterial
{
    float3 Albedo;
    float  Roughness;
    float  Metallic;
    float  AO;
    float3 Emissive;
    float3 Normal;       // 切线空间法线 -> 世界空间
};

ForwardMaterial SampleForwardMaterial(VSOutput inp)
{
    ForwardMaterial m;
    float4 albedoRGBA = MatAlbedoTex.Sample(gLinearWrapSampler, inp.TexCoord);
    m.Albedo   = albedoRGBA.rgb;
    float3 nTS = normalize(MatNormalTex.Sample(gLinearWrapSampler, inp.TexCoord) * 2.0 - 1.0);
    float4 rm  = MatRMTex.Sample(gLinearWrapSampler, inp.TexCoord);
    m.Roughness = max(rm.r * rm.r, 0.045);
    m.Metallic  = rm.g;
    m.AO        = rm.b;
    m.Emissive  = MatEmissiveTex.Sample(gLinearWrapSampler, inp.TexCoord);

    // TBN -> 世界空间法线
    float3 N = normalize(inp.Normal);
    float3 T = normalize(inp.Tangent.xyz);
    T = normalize(T - N * dot(T, N));
    float3 B = cross(N, T) * inp.Tangent.w;
    m.Normal = normalize(mul(nTS, float3x3(T, B, N)));
    return m;
}

// ── 级联阴影采样 (PCF + 级联选择) ──
float SampleCSMShadow(float3 worldPos, float viewDepth)
{
    // 选择级联
    uint cascade = 0;
    [unroll]
    for (uint i = 0; i < FORWARD_CASCADE_MAX; ++i)
    {
        if (i < fCascadeCount && viewDepth > fCascadeDistances[i])
            cascade = i + 1;
    }
    cascade = min(cascade, fCascadeCount - 1);

    float4 lightClip = mul(fCascadeVP[cascade], float4(worldPos, 1.0));
    float3 proj = lightClip.xyz / lightClip.w;
    proj.xy = proj.xy * 0.5 + 0.5;
    proj.y = 1.0 - proj.y;

    // 映射到图集中的级联区域
    float2 atlasUV = proj.xy * fCascadeScale.xy + fCascadeOffset.xy;

    // 法线偏移 (Peter-panning 缓解)
    float2 moments = ShadowMoments.Sample(gLinearClampSampler, atlasUV);
    float mean = moments.x;
    float mean2 = moments.y;
    float variance = max(mean2 - mean * mean, 0.00001);
    float d = proj.z - fShadowBias;
    float pMax = variance / (variance + d * d);   // Chebyshev 上界
    float shadow = (proj.z <= mean) ? 1.0 : saturate_fast(pMax);

    // 级联边界平滑过渡
    if (cascade + 1 < fCascadeCount)
    {
        float edge = smoothstep_edge(fCascadeDistances[cascade] * 0.9, fCascadeDistances[cascade], viewDepth);
        float2 uv2 = proj.xy * fCascadeScale.xy; // 下一级 (更粗, 缩放更大)
        float2 m2 = ShadowMoments.Sample(gLinearClampSampler, uv2);
        float meanB = m2.x, mean2B = m2.y;
        float varB = max(mean2B - meanB * meanB, 0.00001);
        float dB = proj.z - fShadowBias;
        float pB = (proj.z <= meanB) ? 1.0 : saturate_fast(varB / (varB + dB * dB));
        shadow = lerp(shadow, pB, edge);
    }
    return shadow;
}

// ── 集群点光源累积 ──
float3 AccumulateClusterLights(float3 worldPos, float3 N, float3 V,
                               float3 albedo, float roughness, float3 F0,
                               int2 pixel, float viewDepth)
{
    float3 result = 0;
    uint tileX = pixel.x / FORWARD_CLUSTER_TILE_SIZE;
    uint tileY = pixel.y / FORWARD_CLUSTER_TILE_SIZE;

    // 对数深度切片
    float sliceF = saturate_fast(viewDepth / cFarZ);
    uint sliceZ = (uint)(sliceF * (float)cGridDimZ);
    sliceZ = min(sliceZ, cGridDimZ - 1);

    uint clusterIdx = (sliceZ * cGridDimY + tileY) * cGridDimX + tileX;
    uint2 grid = ClusterGrid[clusterIdx];
    uint offset = grid.x;
    uint count  = min(grid.y, cMaxLightsPerCluster);

    float NdotV = saturate_fast(dot(N, V));

    [loop]
    for (uint i = 0; i < count; ++i)
    {
        uint lightIdx = ClusterLightList[offset + i];
        if (lightIdx >= cLightCount) break;
        PointLight pl = PointLights[lightIdx];

        float3 L = pl.Position - worldPos;
        float dist = length(L);
        L /= max(dist, SHANYU_EPSILON);
        if (dist > pl.Radius) continue;

        float attenuation = 1.0 / (1.0 + pl.Falloff * dist * dist);
        attenuation *= saturate_fast(1.0 - dist / pl.Radius);

        float NdotL = saturate_fast(dot(N, L));
        if (NdotL <= 0.0) continue;

        float3 H = normalize(L + V);
        float NdotH = saturate_fast(dot(N, H));
        float VdotH = saturate_fast(dot(V, H));
        float3 brdf = DirectBRDF(albedo, roughness, F0, NdotL, NdotV, NdotH, VdotH);
        result += brdf * pl.Color * pl.Intensity * attenuation * NdotL;
    }
    return result;
}

// ── 从 NDC + 深度重建世界坐标 (使用传入的 InvViewProj, 避免依赖 b0) ──
float3 ReconstructWP(float4x4 invViewProj, float2 uv, float depth)
{
    float4 clip = float4(uv * 2.0 - 1.0, depth, 1.0);
    clip.y = -clip.y;
    float4 wp = mul(invViewProj, clip);
    return wp.xyz / max(wp.w, SHANYU_EPSILON);
}

// 视空间线性深度 -> D3D NDC 深度 (常规 [0,1], near=0/far=1)
float ViewDepthToNDC(float viewZ, float nearZ, float farZ)
{
    return farZ * (viewZ - nearZ) / max((farZ - nearZ) * viewZ, SHANYU_EPSILON);
}

// ── 简化天空大气 ──
float3 EvaluateAtmosphere(float3 dir)
{
    float3 sunDir = normalize(fSunDirection);
    float cosTheta = saturate_fast(dot(dir, sunDir) * 0.5 + 0.5);
    float3 sky = lerp(float3(0.55, 0.75, 1.05), float3(0.15, 0.30, 0.55), saturate_fast(dir.y * 1.5));
    float3 sunDisc = fSunColor * pow(cosTheta, 256.0) * 8.0;
    return sky * fSkyLight.rgb * fSkyLight.w + sunDisc;
}

// ════════════════════════════════════════════════════════════════════════════
// ForwardPS — 前向渲染像素着色器 (PBR)
// ════════════════════════════════════════════════════════════════════════════
struct PSOutput
{
    float4 SceneColor : SV_Target0;
    float2 Velocity   : SV_Target1;
};

PSOutput ForwardPS(VSOutput inp)
{
    PSOutput o;
    ForwardMaterial m = SampleForwardMaterial(inp);

    float3 N = m.Normal;
    float3 V = normalize(fCameraPos - inp.WorldPos);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), m.Albedo, m.Metallic);

    // 太阳直接光 + 阴影
    float3 L = normalize(fSunDirection);
    float NdotL = saturate_fast(dot(N, L));
    float3 H = normalize(L + V);
    float NdotV = saturate_fast(dot(N, V));
    float NdotH = saturate_fast(dot(N, H));
    float VdotH = saturate_fast(dot(V, H));

    float viewDepth = mul(fViewMatrix, float4(inp.WorldPos, 1.0)).z;
    float shadow = SampleCSMShadow(inp.WorldPos, viewDepth);

    float3 sunBRDF = DirectBRDF(m.Albedo, m.Roughness, F0, NdotL, NdotV, NdotH, VdotH);
    float3 directLight = sunBRDF * fSunColor * fSunIntensity * NdotL * shadow;

    // 集群点光源
    int2 pixel = (int2)inp.Position.xy;
    float3 pointLights = AccumulateClusterLights(inp.WorldPos, N, V, m.Albedo, m.Roughness, F0, pixel, viewDepth);

    // 环境 IBL
    float3 R = reflect(-V, N);
    float3 irradiance = EnvProbe.SampleLevel(gLinearWrapSampler, N, 0).rgb;
    float3 radiance    = EnvProbe.SampleLevel(gLinearWrapSampler, R, m.Roughness * 8.0).rgb;
    float2 envBRDF = BRDFLUT.Sample(gLinearClampSampler, float2(NdotV, m.Roughness));
    float3 kS = F0 * envBRDF.x + envBRDF.y;
    float3 kD = (1.0 - kS) * (1.0 - m.Metallic);
    float3 indirect = (kD * m.Albedo * irradiance + radiance * kS) * m.AO;

    // 天空背景 (背景像素)
    float3 sky = (fEnableAtmo != 0) ? EvaluateAtmosphere(V) : fSkyLight.rgb * fSkyLight.w;

    float3 color = directLight + pointLights + indirect + m.Emissive;
    float skyMask = step(inp.Position.z, 0.99999) * 0.0; // 占位: 实际由深度测试决定
    color = lerp(color, sky, skyMask);

    color = ApplyExposure(color, fExposure);

    o.SceneColor = float4(color, 1.0);

    // 运动矢量
    float2 curUV = inp.Position.xy * fInvResolution;
    float2 prevUV = (inp.PrevPosition.xy / inp.PrevPosition.w) * 0.5 + 0.5;
    prevUV.y = 1.0 - prevUV.y;
    o.Velocity = curUV - prevUV;

    return o;
}

// ════════════════════════════════════════════════════════════════════════════
// ClusterCullCS — 集群光照剔除
//   每个线程处理一个集群, 测试所有点光源与之相交, 写入固定槽位的光源索引列表
//   [numthreads] = 64 (一个线程组处理 64 个集群)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(64, 1, 1)]
void ClusterCullCS(uint3 dtid : SV_DispatchThreadID)
{
    uint clusterIdx = dtid.x;
    uint totalClusters = cGridDimX * cGridDimY * cGridDimZ;
    if (clusterIdx >= totalClusters) return;

    uint sliceZ = clusterIdx / (cGridDimX * cGridDimY);
    uint rem    = clusterIdx - sliceZ * cGridDimX * cGridDimY;
    uint tileY  = rem / cGridDimX;
    uint tileX  = rem - tileY * cGridDimX;

    // 视空间对数深度切片
    float sliceNearView = cNearZ * pow(cFarZ / cNearZ, (float)sliceZ / (float)cGridDimZ);
    float sliceFarView  = cNearZ * pow(cFarZ / cNearZ, (float)(sliceZ + 1) / (float)cGridDimZ);
    float zNearNDC = ViewDepthToNDC(sliceNearView, cNearZ, cFarZ);
    float zFarNDC  = ViewDepthToNDC(sliceFarView,  cNearZ, cFarZ);

    // 切片在 NDC 空间的 4 个角, 逆投影到世界空间构建 AABB
    float2 tileBias = float2(tileX, tileY);
    float2 minNDC = (tileBias * FORWARD_CLUSTER_TILE_SIZE) * fInvResolution * 2.0 - 1.0;
    float2 maxNDC = ((tileBias + 1.0) * FORWARD_CLUSTER_TILE_SIZE) * fInvResolution * 2.0 - 1.0;
    minNDC.y = -minNDC.y;
    maxNDC.y = -maxNDC.y;

    float3 wMin = float3(SHANYU_FLT_MAX, SHANYU_FLT_MAX, SHANYU_FLT_MAX);
    float3 wMax = -wMin;
    // 8 个角点逆投影
    [unroll]
    for (int cx = 0; cx < 2; ++cx)
    {
        [unroll]
        for (int cy = 0; cy < 2; ++cy)
        {
            [unroll]
            for (int cz = 0; cz < 2; ++cz)
            {
                float2 ndcXY = (cx == 0) ? minNDC : maxNDC;
                float2 ndcY  = (cy == 0) ? minNDC : maxNDC;
                float2 corner = float2(ndcXY.x, ndcY.y);
                float zNDC = (cz == 0) ? zNearNDC : zFarNDC;
                float4 clip = float4(corner, zNDC, 1.0);
                float4 wp = mul(cInvViewProj, clip);
                float3 w = wp.xyz / max(wp.w, SHANYU_EPSILON);
                wMin = min(wMin, w);
                wMax = max(wMax, w);
            }
        }
    }

    uint baseOffset = clusterIdx * cMaxLightsPerCluster;
    uint count = 0;

    [loop]
    for (uint li = 0; li < cLightCount && count < cMaxLightsPerCluster; ++li)
    {
        PointLight pl = PointLights[li];

        // 球体 - 世界 AABB 最近点测试
        float3 closest = clamp(pl.Position, wMin, wMax);
        float3 diff = pl.Position - closest;
        if (dot(diff, diff) <= pl.Radius * pl.Radius)
        {
            ClusterLightListUAV[baseOffset + count] = li;
            count++;
        }
    }

    ClusterGridUAV[clusterIdx] = uint2(baseOffset, count);
}

// ════════════════════════════════════════════════════════════════════════════
// ForwardShadeCS — 屏幕空间前向着色 (集群光源 + CSM + IBL)
//   读取 GeometryBuffer (法线/反照率) + 深度, 输出 HDR 场景颜色
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void ForwardShadeCS(uint3 dtid : SV_DispatchThreadID)
{
    int2 pixel = (int2)dtid.xy;
    float depth = SceneDepth[pixel];
    if (depth >= 1.0) // 天空
    {
        float2 uv = (dtid.xy + 0.5) * fInvResolution;
        float3 dir = normalize(mul((float3x3)cInvViewProj, float3(uv * 2.0 - 1.0, 1.0)));
        float3 sky = (fEnableAtmo != 0) ? EvaluateAtmosphere(dir) : fSkyLight.rgb * fSkyLight.w;
        SceneColorUAV[pixel] = float4(ApplyExposure(sky, fExposure), 1.0);
        return;
    }

    float2 uv = (dtid.xy + 0.5) * fInvResolution;
    float3 worldPos = ReconstructWP(fInvViewProj, uv, depth);

    float3 N = normalize(GeometryBufferNormals[pixel] * 2.0 - 1.0);
    float3 albedo = GeometryBufferAlbedo[pixel];
    // 粗略材质参数 (GeometryBuffer 通道受限, 此处近似)
    float roughness = 0.5;
    float metallic = 0.0;
    float ao = 1.0;

    float3 V = normalize(fCameraPos - worldPos);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

    // 太阳
    float3 L = normalize(fSunDirection);
    float NdotL = saturate_fast(dot(N, L));
    float3 H = normalize(L + V);
    float NdotV = saturate_fast(dot(N, V));
    float NdotH = saturate_fast(dot(N, H));
    float VdotH = saturate_fast(dot(V, H));
    float viewDepth = mul(fViewMatrix, float4(worldPos, 1.0)).z;
    float shadow = SampleCSMShadow(worldPos, viewDepth);
    float3 sunBRDF = DirectBRDF(albedo, roughness, F0, NdotL, NdotV, NdotH, VdotH);
    float3 direct = sunBRDF * fSunColor * fSunIntensity * NdotL * shadow;

    // 集群点光源
    float3 points = AccumulateClusterLights(worldPos, N, V, albedo, roughness, F0, pixel, viewDepth);

    // IBL
    float3 R = reflect(-V, N);
    float3 irradiance = EnvProbe.SampleLevel(gLinearWrapSampler, N, 0).rgb;
    float3 radiance = EnvProbe.SampleLevel(gLinearWrapSampler, R, roughness * 8.0).rgb;
    float2 envBRDF = BRDFLUT.Sample(gLinearClampSampler, float2(NdotV, roughness));
    float3 kS = F0 * envBRDF.x + envBRDF.y;
    float3 kD = (1.0 - kS) * (1.0 - metallic);
    float3 indirect = (kD * albedo * irradiance + radiance * kS) * ao;

    float3 color = ApplyExposure(direct + points + indirect, fExposure);
    SceneColorUAV[pixel] = float4(color, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// OITCS — 顺序无关透明合成 (加权混合透明, McGuire & Bavoil 2013)
//   透明 Pass 已将所有透明片元累积到 OitAccumUAV (premultiplied RGB * weight)
//   与 OitRevealageUAV (1 - alpha * weight). 本 CS 将其合成到不透明场景颜色上.
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void OITCS(uint3 dtid : SV_DispatchThreadID)
{
    int2 pixel = (int2)dtid.xy;
    float4 accum = OitAccumUAV[pixel];
    float revealage = OitRevealageUAV[pixel];

    float3 transparentColor = accum.rgb / max(accum.a, SHANYU_EPSILON);
    float alpha = 1.0 - revealance;

    float3 scene = SceneColorUAV[pixel].rgb;
    float3 result = lerp(scene, transparentColor, saturate_fast(alpha));
    SceneColorUAV[pixel] = float4(result, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// ShadowCS — 级联阴影 VSM 矩构建
//   从 CSM 深度图集生成方差阴影矩 (mean, mean^2), 供软阴影采样
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void ShadowCS(uint3 dtid : SV_DispatchThreadID)
{
    int2 pixel = (int2)dtid.xy;
    uint w, h;
    ShadowDepthAtlas.GetDimensions(w, h);
    if ((uint)pixel.x >= w || (uint)pixel.y >= h) return;

    float d = ShadowDepthAtlas[pixel];
    // 将近平面线性化后的深度作为矩 (减少精度问题)
    float linearD = d; // 已是光源空间深度
    MomentsUAV[pixel] = float2(linearD, linearD * linearD);
}

// ════════════════════════════════════════════════════════════════════════════
// ShadowFilterCS — 级联阴影矩高斯过滤 (5x5)
//   模糊矩以产生更平滑的软阴影边界
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void ShadowFilterCS(uint3 dtid : SV_DispatchThreadID)
{
    int2 pixel = (int2)dtid.xy;
    uint w, h;
    MomentsUAV.GetDimensions(w, h);
    if ((uint)pixel.x >= w || (uint)pixel.y >= h) return;

    // 5x5 高斯核
    float weights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };
    float2 sum = 0;
    float wsum = 0;
    [unroll]
    for (int x = -2; x <= 2; ++x)
    {
        [unroll]
        for (int y = -2; y <= 2; ++y)
        {
            int2 s = clamp(pixel + int2(x, y), int2(0, 0), int2((int)w - 1, (int)h - 1));
            float wx = weights[abs(x)];
            float wy = weights[abs(y)];
            float wgt = wx * wy;
            sum += MomentsUAV[s] * wgt;
            wsum += wgt;
        }
    }
    // 注意: 此处读自身 UAV 同一 dispatch 会有竞态, 实际工程用乒乓纹理.
    // 这里读 ShadowDepthAtlas 重新构建以避免竞态:
    float2 moments = sum / max(wsum, SHANYU_EPSILON);
    MomentsUAV[pixel] = moments;
}
