// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - GPU Path Tracer
// 路径追踪: BVH 光线求交, GGX BRDF 采样, 下一事件估计 (NEE) + 多重重要性采样 (MIS),
// 俄罗斯轮盘截断, ReSTIR 重采样, SVGF 时空降噪, 累积, 色调映射。
// Shader Model 6.6 (DXC)
// 入口点: PathTraceCS, ReSTIRCS, PTSvgfCS, PTAccumulateCS, PTTonemapCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# PathTracerCB / PathTracerSvgfCB 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer PathTracerCB : register(b0)
{
    float4x4 g_PT_ViewMatrix;
    float4x4 g_PT_ProjMatrix;
    float4x4 g_PT_InvViewMatrix;
    float4x4 g_PT_InvProjMatrix;
    float3   g_PT_CameraPos;
    float    g_PT_RayEpsilon;
    int      g_PT_SamplesPerPixel;
    int      g_PT_MaxBounces;
    int      g_PT_RussianRouletteDepth;
    float    g_PT_RussianRouletteProb;
    int      g_PT_EnableNEE;
    int      g_PT_EnableMIS;
    int      g_PT_EnableReSTIR;
    int      g_PT_ReSTIRCandidateCount;
    int      g_PT_EnableSSS;
    int      g_PT_EnableVolumetric;
    float    g_PT_MaxRayDistance;
    int      g_PT_FrameIndex;
    uint     g_PT_AccumulationFrame;
    float3   g_PT_SkyColor;
    float    g_PT_SkyIntensity;
    float    g_PT_Pad0;
    float    g_PT_Pad1;
};

cbuffer PathTracerSvgfCB : register(b0)
{
    int   g_SVGF_Iteration;
    int   g_SVGF_KernelRadius;
    float g_SVGF_DepthSigma;
    float g_SVGF_NormalSigma;
    float g_SVGF_LuminanceSigma;
    int   g_SVGF_StepWidth;
    float g_SVGF_Pad0;
    float g_SVGF_Pad1;
    float g_SVGF_Pad2;
};

// ──────────────────────────────────────────────────────────────────────────
// 数据结构 (与 C# PathTracerBVHNode / PathTracerReservoir 对应)
// ──────────────────────────────────────────────────────────────────────────
struct BVHNode
{
    float3 BoundsMin;
    uint   LeftOrFirst;
    float3 BoundsMax;
    uint   PrimitiveCount;
};

struct Reservoir
{
    uint  SampleIndex;
    float TargetPdf;
    float WeightSum;
    uint  CandidateCount;
    float W;
    float Pad0;
    float Pad1;
    float Pad2;
};

// 三角形顶点 (位置 + 法线 + UV + 颜色)
struct PTVertex
{
    float3 Position;
    float3 Normal;
    float2 TexCoord;
    float3 Albedo;
    float  Roughness;
    float  Metallic;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
StructuredBuffer<BVHNode>   g_BVHNodes      : register(t0);
StructuredBuffer<PTVertex>  g_Vertices      : register(t1);
StructuredBuffer<uint>      g_Indices       : register(t2);
StructuredBuffer<Reservoir> g_PrevReservoirs: register(t3); // 上一帧 ReSTIR 水库

Texture2D<float>   g_PTHistoryDepth  : register(t4);
Texture2D<float4>  g_PTHistoryColor  : register(t5);
Texture2D<float4>  g_PTMotionVectors : register(t6);
Texture2D<float4>  g_PTNormalsInput  : register(t7); // GeometryBuffer 法线 (引导)

RWTexture2D<float4> g_PT_AccumUAV    : register(u0); // 累积缓冲
RWTexture2D<float4> g_PT_DenoisedUAV : register(u1); // 降噪输出
RWTexture2D<float4> g_PT_HistoryUAV  : register(u2); // 历史帧
RWTexture2D<float4> g_PT_NormalsUAV  : register(u3); // 重建法线
RWTexture2D<float4> g_PT_AlbedoUAV   : register(u4); // 重建反照率
RWTexture2D<float>  g_PT_VarianceUAV : register(u5); // 方差
RWTexture2D<float>  g_PT_DepthUAV    : register(u6); // 深度
RWTexture2D<float4> g_PT_OutputUAV   : register(u7); // 最终输出
RWStructuredBuffer<Reservoir> g_CurReservoirs : register(u0); // 当前水库 (ReSTIR)

// ──────────────────────────────────────────────────────────────────────────
// 伪随机数 (PCG / LCG)
// ──────────────────────────────────────────────────────────────────────────
uint PCGHash(uint seed)
{
    uint state = seed * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float RandomFloat(inout uint seed)
{
    seed = PCGHash(seed);
    return seed / 4294967295.0;
}

float2 RandomFloat2(inout uint seed)
{
    return float2(RandomFloat(seed), RandomFloat(seed));
}

float3 RandomFloat3(inout uint seed)
{
    return float3(RandomFloat(seed), RandomFloat(seed), RandomFloat(seed));
}

// ──────────────────────────────────────────────────────────────────────────
// 光线求交
// ──────────────────────────────────────────────────────────────────────────

// 射线-AABB 相交
bool RayAABBIntersect(float3 rayOrigin, float3 rayDir, float3 boxMin, float3 boxMax, out float tNear)
{
    float3 invDir = 1.0 / rayDir;
    float3 t1 = (boxMin - rayOrigin) * invDir;
    float3 t2 = (boxMax - rayOrigin) * invDir;
    float3 tMin = min(t1, t2);
    float3 tMax = max(t1, t2);
    tNear = max(max(tMin.x, tMin.y), tMin.z);
    float tFar = min(min(tMax.x, tMax.y), tMax.z);
    return tFar >= max(tNear, 0.0);
}

// Möller-Trumbore 射线-三角形相交
bool RayTriangleIntersect(float3 rayOrigin, float3 rayDir,
                          float3 v0, float3 v1, float3 v2,
                          out float t, out float2 bary)
{
    float3 e1 = v1 - v0;
    float3 e2 = v2 - v0;
    float3 p = cross(rayDir, e2);
    float det = dot(e1, p);
    if (abs(det) < 1e-8) return false;
    float invDet = 1.0 / det;
    float3 tv = rayOrigin - v0;
    bary.x = dot(tv, p) * invDet;
    if (bary.x < 0.0 || bary.x > 1.0) return false;
    float3 q = cross(tv, e1);
    bary.y = dot(rayDir, q) * invDet;
    if (bary.y < 0.0 || bary.x + bary.y > 1.0) return false;
    t = dot(e2, q) * invDet;
    return t > g_PT_RayEpsilon;
}

// BVH 遍历求交
struct HitInfo
{
    float  T;
    float2 Bary;
    uint   TriIndex;
    bool   Hit;
};

HitInfo TraceBVH(float3 rayOrigin, float3 rayDir)
{
    HitInfo hit;
    hit.T = g_PT_MaxRayDistance;
    hit.Hit = false;
    hit.TriIndex = 0;

    // 栈式 BVH 遍历
    uint stack[32];
    int stackPtr = 0;
    stack[stackPtr++] = 0; // 根节点

    [loop]
    while (stackPtr > 0)
    {
        uint nodeIdx = stack[--stackPtr];
        BVHNode node = g_BVHNodes[nodeIdx];

        float tNear;
        if (!RayAABBIntersect(rayOrigin, rayDir, node.BoundsMin, node.BoundsMax, tNear))
            continue;
        if (tNear > hit.T)
            continue;

        if (node.PrimitiveCount > 0)
        {
            // 叶节点: 遍历三角形
            [loop]
            for (uint i = 0; i < node.PrimitiveCount; i++)
            {
                uint triIdx = g_Indices[node.LeftOrFirst + i];
                uint i0 = g_Indices[triIdx * 3 + 0];
                uint i1 = g_Indices[triIdx * 3 + 1];
                uint i2 = g_Indices[triIdx * 3 + 2];

                float3 v0 = g_Vertices[i0].Position;
                float3 v1 = g_Vertices[i1].Position;
                float3 v2 = g_Vertices[i2].Position;

                float t;
                float2 bary;
                if (RayTriangleIntersect(rayOrigin, rayDir, v0, v1, v2, t, bary) && t < hit.T)
                {
                    hit.T = t;
                    hit.Bary = bary;
                    hit.TriIndex = triIdx;
                    hit.Hit = true;
                }
            }
        }
        else
        {
            // 内部节点: 压入子节点
            if (stackPtr < 31)
            {
                stack[stackPtr++] = node.LeftOrFirst + 1;
                stack[stackPtr++] = node.LeftOrFirst;
            }
        }
    }

    return hit;
}

// 从命中信息插值顶点属性
PTVertex InterpolateVertex(HitInfo hit)
{
    uint i0 = g_Indices[hit.TriIndex * 3 + 0];
    uint i1 = g_Indices[hit.TriIndex * 3 + 1];
    uint i2 = g_Indices[hit.TriIndex * 3 + 2];

    PTVertex v0 = g_Vertices[i0];
    PTVertex v1 = g_Vertices[i1];
    PTVertex v2 = g_Vertices[i2];

    float w0 = 1.0 - hit.Bary.x - hit.Bary.y;
    float w1 = hit.Bary.x;
    float w2 = hit.Bary.y;

    PTVertex result;
    result.Position = v0.Position * w0 + v1.Position * w1 + v2.Position * w2;
    result.Normal = normalize(v0.Normal * w0 + v1.Normal * w1 + v2.Normal * w2);
    result.TexCoord = v0.TexCoord * w0 + v1.TexCoord * w1 + v2.TexCoord * w2;
    result.Albedo = v0.Albedo * w0 + v1.Albedo * w1 + v2.Albedo * w2;
    result.Roughness = v0.Roughness * w0 + v1.Roughness * w1 + v2.Roughness * w2;
    result.Metallic = v0.Metallic * w0 + v1.Metallic * w1 + v2.Metallic * w2;
    return result;
}

// ──────────────────────────────────────────────────────────────────────────
// BRDF 采样
// ──────────────────────────────────────────────────────────────────────────

// 余弦加权半球采样
float3 SampleCosineHemisphere(float2 uniform)
{
    float phi = uniform.x * SHANYU_TWO_PI;
    float cosTheta = sqrt(1.0 - uniform.y);
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    return float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

// GGX 重要度采样
float3 SampleGGX(float2 uniform, float roughness, float3 N)
{
    float a = roughness * roughness;
    float phi = uniform.x * SHANYU_TWO_PI;
    float cosTheta = sqrt((1.0 - uniform.y) / (1.0 + (a * a - 1.0) * uniform.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    float3 H = float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);

    // 构建 TBN
    float3 up = abs(N.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 T = normalize(cross(up, N));
    float3 B = cross(N, T);

    return normalize(T * H.x + B * H.y + N * H.z);
}

// Schlick 菲涅尔
float3 SchlickFresnel(float cosTheta, float3 f0)
{
    return f0 + (1.0 - f0) * pow(1.0 - saturate(cosTheta), 5.0);
}

// 评估 BRDF (GGX + Lambert)
float3 EvaluateBRDF(float3 albedo, float roughness, float metallic,
                    float3 N, float3 V, float3 L)
{
    float3 H = normalize(L + V);
    float NdotL = saturate(dot(N, L));
    float NdotV = saturate(dot(N, V));
    float NdotH = saturate(dot(N, H));
    float VdotH = saturate(dot(V, H));

    float3 f0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);
    float3 F = SchlickFresnel(VdotH, f0);

    // 镜面反射
    float D = D_GGX(NdotH, roughness);
    float Vis = V_SmithGGXCorrelated(NdotV, NdotL, roughness);
    float3 specular = D * Vis * F;

    // 漫反射 (金属无漫反射)
    float3 kd = (1.0 - F) * (1.0 - metallic);
    float3 diffuse = kd * Diffuse_Lambert(albedo);

    return (diffuse + specular) * NdotL;
}

// ──────────────────────────────────────────────────────────────────────────
// 路径追踪核心
// ──────────────────────────────────────────────────────────────────────────
struct PathResult
{
    float3 Radiance;
    float3 Albedo;
    float3 Normal;
    float  Depth;
};

// 单条路径积分
PathResult PathTrace(float3 rayOrigin, float3 rayDir, inout uint seed)
{
    PathResult result;
    result.Radiance = 0.0;
    result.Albedo = 0.0;
    result.Normal = 0.0;
    result.Depth = 0.0;

    float3 throughput = 1.0;
    float3 rayPos = rayOrigin;
    float3 rayDirection = rayDir;

    [loop]
    for (int bounce = 0; bounce < g_PT_MaxBounces; bounce++)
    {
        HitInfo hit = TraceBVH(rayPos, rayDirection);

        if (!hit.Hit)
        {
            // 未命中: 天空光
            float skyFactor = saturate(rayDirection.y * 0.5 + 0.5);
            result.Radiance += throughput * g_PT_SkyColor * g_PT_SkyIntensity * skyFactor;
            break;
        }

        PTVertex vert = InterpolateVertex(hit);
        float3 hitPos = rayPos + rayDirection * hit.T;
        float3 N = vert.Normal;
        float3 V = -rayDirection;

        // 记录首次命中 (用于 GeometryBuffer 引导)
        if (bounce == 0)
        {
            result.Albedo = vert.Albedo;
            result.Normal = N;
            result.Depth = hit.T;
        }

        float3 albedo = vert.Albedo;
        float roughness = vert.Roughness;
        float metallic = vert.Metallic;
        float3 f0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

        // ── 下一事件估计 (NEE): 直接光照采样 ──
        if (g_PT_EnableNEE != 0 && bounce < g_PT_MaxBounces - 1)
        {
            // 简化: 单方向光 (从常量推断, 实际从光源缓冲采样)
            float3 lightDir = normalize(float3(0.5, 0.8, 0.3));
            float3 lightColor = float3(4.0, 3.8, 3.5);

            // 阴影射线
            float3 shadowOrigin = hitPos + N * g_PT_RayEpsilon;
            HitInfo shadowHit = TraceBVH(shadowOrigin, lightDir);
            float shadowDist = 100.0;
            if (!shadowHit.Hit || shadowHit.T >= shadowDist)
            {
                float NdotL = saturate(dot(N, lightDir));
                if (NdotL > 0.0)
                {
                    // 直接光照 BRDF
                    float3 directLight = EvaluateBRDF(albedo, roughness, metallic, N, V, lightDir);
                    result.Radiance += throughput * directLight * lightColor;

                    // MIS: 若启用, 对 BRDF 采样也加权 (此处简化为纯 NEE)
                }
            }
        }

        // ── BRDF 采样: 继续路径 ──
        float2 uniform = RandomFloat2(seed);
        float3 newDir;

        // 金属走 GGX, 非金属混合漫反射与 GGX
        float specProb = lerp(0.5, 1.0, metallic);
        if (RandomFloat(seed) < specProb)
        {
            newDir = SampleGGX(uniform, roughness, N);
        }
        else
        {
            newDir = normalize(N + SampleCosineHemisphere(uniform));
        }

        float3 H = normalize(newDir - rayDirection);
        float NdotL = saturate(dot(N, newDir));
        float VdotH = saturate(dot(V, H));

        float3 F = SchlickFresnel(VdotH, f0);
        float3 brdfValue = EvaluateBRDF(albedo, roughness, metallic, N, V, newDir);
        float pdf = max(NdotL * SHANYU_INV_PI, 1e-6);

        throughput *= brdfValue / pdf;

        // 俄罗斯轮盘截断
        if (bounce >= g_PT_RussianRouletteDepth)
        {
            float p = max(Luminance(throughput), 0.01);
            if (RandomFloat(seed) > p * g_PT_RussianRouletteProb)
                break;
            throughput /= max(p * g_PT_RussianRouletteProb, 1e-6);
        }

        // 推进射线
        rayPos = hitPos + N * g_PT_RayEpsilon;
        rayDirection = newDir;

        // 吞吐量过小则提前终止
        if (max(max(throughput.r, throughput.g), throughput.b) < 0.01)
            break;
    }

    return result;
}

// ════════════════════════════════════════════════════════════════════════════
// PathTraceCS - 主路径追踪计算着色器
// 每像素发射 SPP 条路径, 累积辐照度
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void PathTraceCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_PT_AccumUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    // 初始化随机种子 (基于像素坐标与帧索引)
    uint seed = dispatchID.x * 1973 + dispatchID.y * 9277 + g_PT_FrameIndex * 26699;

    // 生成相机射线
    float2 uv = (dispatchID.xy + 0.5) / float2(w, h);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    // 抗锯齿: 抖动像素
    float2 jitter = (RandomFloat2(seed) - 0.5) / float2(w, h);
    ndc += jitter * 2.0;

    float4 viewDir = mul(g_PT_InvProjMatrix, float4(ndc, 1.0, 1.0));
    viewDir.xyz = mul((float3x3)g_PT_InvViewMatrix, viewDir.xyz);
    float3 rayDir = normalize(viewDir.xyz);
    float3 rayOrigin = g_PT_CameraPos;

    // 多采样累积
    float3 radiance = 0.0;
    float3 albedo = 0.0;
    float3 normal = 0.0;
    float depth = 0.0;

    for (int s = 0; s < g_PT_SamplesPerPixel; s++)
    {
        PathResult result = PathTrace(rayOrigin, rayDir, seed);
        radiance += result.Radiance;
        albedo += result.Albedo;
        normal += result.Normal;
        depth += result.Depth;
    }

    float invSpp = 1.0 / max(g_PT_SamplesPerPixel, 1);
    radiance *= invSpp;
    albedo *= invSpp;
    normal = normalize(normal);
    depth *= invSpp;

    // 写入 GeometryBuffer 引导信息 (法线使用八面edral 编码, 与 GeometryBuffer 一致)
    g_PT_AlbedoUAV[dispatchID.xy] = float4(albedo, 1.0);
    g_PT_NormalsUAV[dispatchID.xy] = float4(EncodeNormalOctahedral(normal), 1.0, 1.0);
    g_PT_DepthUAV[dispatchID.xy] = depth;

    g_PT_AccumUAV[dispatchID.xy] = float4(radiance, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// ReSTIRCS - Resampled Importance Sampling
// 从候选光源样本中重采样, 利用时空复用提高采样效率
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void ReSTIRCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_PT_AccumUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    if (g_PT_EnableReSTIR == 0)
    {
        g_CurReservoirs[dispatchID.y * w + dispatchID.x].W = 1.0;
        return;
    }

    uint seed = dispatchID.x * 91973 + dispatchID.y * 52711 + g_PT_FrameIndex * 12347;

    // 当前像素的表面信息
    float3 albedo = g_PT_AlbedoUAV[dispatchID.xy].rgb;
    float3 normal = DecodeNormalOctahedral(g_PT_NormalsUAV[dispatchID.xy].xy);
    float depth = g_PT_DepthUAV[dispatchID.xy];

    if (depth <= 0.0)
    {
        g_CurReservoirs[dispatchID.y * w + dispatchID.x].W = 0.0;
        return;
    }

    // 生成候选样本 (简化: 假设单光源)
    Reservoir r;
    r.SampleIndex = 0;
    r.TargetPdf = 0.0;
    r.WeightSum = 0.0;
    r.CandidateCount = 0;
    r.W = 0.0;

    float3 lightDir = normalize(float3(0.5, 0.8, 0.3));
    float3 lightColor = float3(4.0, 3.8, 3.5);

    int M = g_PT_ReSTIRCandidateCount;
    for (int i = 0; i < M; i++)
    {
        // 随机候选 (实际为光源列表中的随机光源)
        uint candidateIdx = uint(RandomFloat(seed) * 8);
        float pdf = max(dot(normal, lightDir), 0.0);

        r.CandidateCount++;
        float w_i = pdf / max(pdf, 1e-6);
        r.WeightSum += w_i;

        if (RandomFloat(seed) < w_i / max(r.WeightSum, 1e-6))
        {
            r.SampleIndex = candidateIdx;
            r.TargetPdf = pdf;
        }
    }

    // 归一化
    r.W = (r.TargetPdf > 1e-6) ? (r.WeightSum / max(r.TargetPdf * r.CandidateCount, 1e-6)) : 0.0;

    // 时空复用: 与邻近像素和上一帧的水库融合
    int2 offsets[4] = { int2(1, 0), int2(-1, 0), int2(0, 1), int2(0, -1) };
    for (int n = 0; n < 4; n++)
    {
        int2 neighborPos = dispatchID.xy + offsets[n];
        if (neighborPos.x < 0 || neighborPos.y < 0 || neighborPos.x >= (int)w || neighborPos.y >= (int)h)
            continue;

        // 深度与法线一致性检查
        float neighborDepth = g_PT_DepthUAV[neighborPos];
        if (abs(neighborDepth - depth) > 0.1) continue;

        Reservoir neighbor = g_PrevReservoirs[neighborPos.y * w + neighborPos.x];

        // 合并水库
        uint M_combined = r.CandidateCount + neighbor.CandidateCount;
        float w_neighbor = neighbor.WeightSum;

        if (RandomFloat(seed) < w_neighbor / max(r.WeightSum + w_neighbor, 1e-6))
        {
            r.SampleIndex = neighbor.SampleIndex;
            r.TargetPdf = neighbor.TargetPdf;
        }
        r.WeightSum += w_neighbor;
        r.CandidateCount = M_combined;
    }

    r.W = (r.TargetPdf > 1e-6) ? (r.WeightSum / max(r.TargetPdf * r.CandidateCount, 1e-6)) : 0.0;

    g_CurReservoirs[dispatchID.y * w + dispatchID.x] = r;
}

// ════════════════════════════════════════════════════════════════════════════
// PTSvgfCS - SVGF 时空方差滤波降噪
// 先时间累积, 再空间双边滤波 (基于深度/法线/亮度边缘保持)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void PTSvgfCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_PT_DenoisedUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;
    float3 centerColor = g_PT_AccumUAV[pixel].rgb;
    float centerDepth = g_PT_DepthUAV[pixel];
    float3 centerNormal = DecodeNormalOctahedral(g_PT_NormalsUAV[pixel].xy);

    // ── 时间累积 (用运动矢量重投影) ──
    float4 motion = g_PTMotionVectors.Load(int3(pixel, 0));
    int2 historyPos = pixel + int2(motion.xy * float2(w, h));

    float3 historyColor = centerColor;
    float historyWeight = 0.0;
    if (historyPos.x >= 0 && historyPos.y >= 0 && historyPos.x < (int)w && historyPos.y < (int)h)
    {
        float historyDepth = g_PTHistoryDepth.Load(int3(historyPos, 0));
        float3 historyNormal = DecodeNormalOctahedral(g_PT_NormalsInput.Load(int3(historyPos, 0)).xy);

        // 深度与法线一致性
        float depthDiff = abs(historyDepth - centerDepth) / max(centerDepth, 1e-4);
        float normalDiff = dot(historyNormal, centerNormal);

        if (depthDiff < 0.1 && normalDiff > 0.9)
        {
            historyColor = g_PTHistoryColor.Load(int3(historyPos, 0)).rgb;
            historyWeight = 0.9; // 90% 历史
        }
    }

    // 指数移动平均
    float alpha = 1.0 - historyWeight;
    float3 temporalColor = lerp(historyColor, centerColor, alpha);

    // ── 空间双边滤波 (a-trous 小波) ──
    int radius = g_SVGF_KernelRadius;
    int stepWidth = g_SVGF_StepWidth;

    float3 filteredColor = 0.0;
    float totalWeight = 0.0;

    [loop]
    for (int dy = -radius; dy <= radius; dy++)
    {
        [loop]
        for (int dx = -radius; dx <= radius; dx++)
        {
            int2 samplePos = pixel + int2(dx, dy) * stepWidth;
            if (samplePos.x < 0 || samplePos.y < 0 || samplePos.x >= (int)w || samplePos.y >= (int)h)
                continue;

            float sampleDepth = g_PT_DepthUAV[samplePos];
            float3 sampleNormal = DecodeNormalOctahedral(g_PT_NormalsUAV[samplePos].xy);
            float3 sampleColor = g_PT_AccumUAV[samplePos].rgb;

            // 深度权重 (避免跨越深度边缘)
            float depthDiff = abs(sampleDepth - centerDepth) / max(centerDepth, 1e-4);
            float wDepth = exp(-depthDiff * depthDiff / (g_SVGF_DepthSigma * g_SVGF_DepthSigma));

            // 法线权重
            float wNormal = pow(max(dot(centerNormal, sampleNormal), 0.0), g_SVGF_NormalSigma);

            // 亮度权重 (避免跨越颜色边缘)
            float lDiff = abs(Luminance(sampleColor) - Luminance(temporalColor));
            float wLuminance = exp(-lDiff * lDiff / (g_SVGF_LuminanceSigma * g_SVGF_LuminanceSigma));

            // 空间权重 (高斯)
            float spatialDist2 = float(dx * dx + dy * dy);
            float wSpatial = exp(-spatialDist2 * 0.5);

            float w_combined = wDepth * wNormal * wLuminance * wSpatial;
            filteredColor += sampleColor * w_combined;
            totalWeight += w_combined;
        }
    }

    filteredColor = totalWeight > 0.0 ? filteredColor / totalWeight : temporalColor;

    // 计算方差 (用于自适应)
    float variance = Luminance(abs(filteredColor - temporalColor));
    g_PT_VarianceUAV[pixel] = variance;

    g_PT_DenoisedUAV[pixel] = float4(filteredColor, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// PTAccumulateCS - 累积帧 (多次反弹的渐进收敛)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void PTAccumulateCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_PT_HistoryUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;

    // 当前帧降噪结果
    float3 currentColor = g_PT_DenoisedUAV[pixel].rgb;

    // 历史帧
    float3 historyColor = g_PTHistoryColor.Load(int3(pixel, 0)).rgb;

    // 累积权重 (随帧数收敛)
    float frameCount = float(g_PT_AccumulationFrame);
    float accumWeight = frameCount / max(frameCount + 1.0, 1.0);

    // 检测运动矢量一致性 (避免拖影)
    float4 motion = g_PTMotionVectors.Load(int3(pixel, 0));
    float motionLength = length(motion.xy);
    if (motionLength > 0.01)
    {
        accumWeight *= 0.5; // 运动大时降低历史权重
    }

    // 指数移动平均累积
    float3 accumulated = lerp(currentColor, historyColor, accumWeight);

    // 钳制异常值 (火焰噪声抑制)
    float lum = Luminance(accumulated);
    if (lum > 100.0)
    {
        accumulated = accumulated / lum * 100.0;
    }

    g_PT_HistoryUAV[pixel] = float4(accumulated, 1.0);
}

// ════════════════════════════════════════════════════════════════════════════
// PTTonemapCS - 色调映射 (HDR -> LDR, ACES)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void PTTonemapCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_PT_OutputUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;

    // 读取累积结果
    float3 hdrColor = g_PT_HistoryUAV[pixel].rgb;

    // 应用曝光
    hdrColor = ApplyExposure(hdrColor, gExposure);

    // ACES 色调映射
    float3 tonemapped = ACESFilm(hdrColor);

    // sRGB 编码
    float3 ldrColor = LinearToSRGB(tonemapped);

    g_PT_OutputUAV[pixel] = float4(ldrColor, 1.0);
}
