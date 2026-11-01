// ════════════════════════════════════════════════════════════════════
// IBLShadersGLSL.cs — IBL 环境光真实 GLSL shader (#version 450)
//
// 提供基于图像的环境光照（Image-Based Lighting）GPU pass 源码：
//   1. IBL Irradiance (FS) —— 漫反射辐照度卷积（半球积分 → 辐照度贴图）
//   2. IBL Prefilter (FS)  —— 镜面预滤波（GGX 重要性采样 → 环境 mip 链）
//   3. IBL BRDF LUT (FS)   —— 积分 BRDF 查找表（Scale/Bias）
//   4. IBL Ambient (FS)    —— 环境光注入（漫反射 + 镜面反射 + AO 调制）
//
// 输入约定：
//   uEnvironment (samplerCube)  —— 预卷积环境立方体贴图
//   uBrdfLUT     (sampler2D)    —— 2D BRDF 查找表
//   uNdotV / uRoughness / uAO   —— 表面参数
// ════════════════════════════════════════════════════════════════════

namespace YamaEngine.Rendering;

/// <summary>IBL 环境光真实 GLSL shader 源码。</summary>
public static class IBLShadersGLSL
{
    // ── 1. 漫反射辐照度卷积 ────────────────────────────────────────
    // 对每个法线方向，对上半球均匀采样环境贴图并累加（cos 加权）。
    public const string IrradianceFragmentShader = @"#version 450 core
// IBL Irradiance FS：从环境立方体贴图计算漫反射辐照度
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform samplerCube uEnvironment;

in vec3 vWorldPos;

const float PI = 3.14159265359;

void main()
{
    vec3 N = normalize(vWorldPos);
    vec3 up = abs(N.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 right = normalize(cross(up, N));
    up = cross(N, right);

    vec3 irradiance = vec3(0.0);
    const int SAMPLE_COUNT = 32;
    float sampleDelta = 0.025;
    float nrSamples = 0.0;
    for (int phi = 0; phi < 2 * SAMPLE_COUNT; phi++)
    {
        for (int theta = 0; theta < SAMPLE_COUNT; theta++)
        {
            // 球面采样方向
            vec3 tangentSample = vec3(
                sin(phi * sampleDelta) * cos(theta * sampleDelta),
                sin(phi * sampleDelta) * sin(theta * sampleDelta),
                cos(phi * sampleDelta));
            vec3 sampleVec = tangentSample.x * right + tangentSample.y * up + tangentSample.z * N;
            irradiance += texture(uEnvironment, sampleVec).rgb * cos(theta * sampleDelta) * sin(phi * sampleDelta);
            nrSamples++;
        }
    }
    irradiance = PI * irradiance * (1.0 / nrSamples);
    outColor = vec4(irradiance, 1.0);
}";

    // ── 2. 镜面预滤波 ──────────────────────────────────────────────
    // 用 GGX 分布做重要性采样，对粗糙度分层生成 mip。
    public const string PrefilterFragmentShader = @"#version 450 core
// IBL Prefilter FS：GGX 重要性采样生成镜面预滤波 mip 链
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform samplerCube uEnvironment;
uniform float uRoughness;

in vec3 vWorldPos;

const float PI = 3.14159265359;

float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float denom = (NdotH * NdotH * (a2 - 1.0) + 1.0);
    return a2 / max(PI * denom * denom, 1e-6);
}

vec3 ImportanceSampleGGX(vec2 Xi, vec3 N, float roughness)
{
    float a = roughness * roughness;
    float phi = 2.0 * PI * Xi.x;
    float cosTheta = sqrt((1.0 - Xi.y) / (1.0 + (a * a - 1.0) * Xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    vec3 H = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, N));
    vec3 bitangent = cross(N, tangent);
    return normalize(tangent * H.x + bitangent * H.y + N * H.z);
}

void main()
{
    vec3 N = normalize(vWorldPos);
    vec3 R = N;
    vec3 V = R;

    const int SAMPLE_COUNT = 1024;
    vec3 prefiltered = vec3(0.0);
    float totalWeight = 0.0;
    for (int i = 0; i < SAMPLE_COUNT; i++)
    {
        vec2 Xi = vec2(float((i * 2 + 1) % 1024) / 1024.0, float((i * 2 + 1) / 1024) / 1024.0);
        vec3 H = ImportanceSampleGGX(Xi, N, uRoughness);
        vec3 L = normalize(2.0 * dot(V, H) * H - V);

        float NdotL = dot(N, L);
        if (NdotL > 0.0)
        {
            float D = DistributionGGX(N, H, uRoughness);
            float NdotH = max(dot(N, H), 0.0);
            float HdotV = max(dot(H, V), 0.0);
            float pdf = D * NdotH / (4.0 * HdotV) + 1e-4;
            float saTexel = 4.0 * PI / (6.0 * 1024.0 * 1024.0);
            float saSample = 1.0 / (float(SAMPLE_COUNT) * pdf + 1e-4);
            float mipLevel = uRoughness == 0.0 ? 0.0 : 0.5 * log2(saSample / saTexel);

            prefiltered += textureLod(uEnvironment, L, mipLevel).rgb * NdotL;
            totalWeight += NdotL;
        }
    }
    prefiltered = prefiltered / max(totalWeight, 1e-4);
    outColor = vec4(prefiltered, 1.0);
}";

    // ── 3. BRDF LUT ────────────────────────────────────────────────
    // 对 (NdotV, roughness) 积分 GGX 的镜面分量，输出 scale/bias。
    public const string BrdfLutFragmentShader = @"#version 450 core
// IBL BRDF LUT FS：积分镜面 BRDF 的 scale/bias 项
layout(location = 0) out vec2 outColor;
in vec2 vUV;

const float PI = 3.14159265359;

float GeometrySchlickGGX(float NdotV, float roughness)
{
    float a = roughness;
    float k = (a * a) / 2.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    return GeometrySchlickGGX(max(dot(N, V), 0.0), roughness)
         * GeometrySchlickGGX(max(dot(N, L), 0.0), roughness);
}

vec3 ImportanceSampleGGX(vec2 Xi, vec3 N, float roughness)
{
    float a = roughness * roughness;
    float phi = 2.0 * PI * Xi.x;
    float cosTheta = sqrt((1.0 - Xi.y) / (1.0 + (a * a - 1.0) * Xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    vec3 H = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, N));
    vec3 bitangent = cross(N, tangent);
    return normalize(tangent * H.x + bitangent * H.y + N * H.z);
}

vec2 IntegrateBRDF(float NdotV, float roughness)
{
    vec3 V = vec3(sqrt(1.0 - NdotV * NdotV), 0.0, NdotV);
    vec3 N = vec3(0.0, 0.0, 1.0);
    float A = 0.0;
    float B = 0.0;
    const int SAMPLE_COUNT = 1024;
    for (int i = 0; i < SAMPLE_COUNT; i++)
    {
        vec2 Xi = vec2(float((i * 2 + 1) % 1024) / 1024.0, float((i * 2 + 1) / 1024) / 1024.0);
        vec3 H = ImportanceSampleGGX(Xi, N, roughness);
        vec3 L = normalize(2.0 * dot(V, H) * H - V);
        float NdotL = max(L.z, 0.0);
        float NdotH = max(H.z, 0.0);
        float VdotH = max(dot(V, H), 0.0);
        if (NdotL > 0.0)
        {
            float G = GeometrySmith(N, V, L, roughness);
            float GVis = (G * VdotH) / (NdotH * NdotV);
            float Fc = pow(1.0 - VdotH, 5.0);
            A += (1.0 - Fc) * GVis;
            B += Fc * GVis;
        }
    }
    return vec2(A, B) / float(SAMPLE_COUNT);
}

void main()
{
    outColor = IntegrateBRDF(vUV.x, vUV.y);
}";

    // ── 4. 环境光注入 ──────────────────────────────────────────────
    // 结合漫反射辐照度 + 镜面预滤波 + BRDF LUT，输出最终环境光贡献。
    public const string AmbientFragmentShader = @"#version 450 core
// IBL Ambient FS：漫反射 + 镜面反射环境光注入（含 AO 调制）
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform samplerCube uIrradianceMap;
layout(set = 0, binding = 1) uniform samplerCube uPrefilterMap;
layout(set = 0, binding = 2) uniform sampler2D uBrdfLUT;

uniform vec3 uAlbedo;
uniform float uMetallic;
uniform float uRoughness;
uniform float uAO;
uniform vec3 uNormal;
uniform vec3 uViewDir;

const float PI = 3.14159265359;

void main()
{
    vec3 N = normalize(uNormal);
    vec3 V = normalize(uViewDir);
    vec3 R = reflect(-V, N);

    vec3 F0 = mix(vec3(0.04), uAlbedo, uMetallic);

    // 漫反射（非金属部分）
    vec3 irradiance = texture(uIrradianceMap, N).rgb;
    vec3 diffuse = irradiance * uAlbedo * (1.0 - uMetallic);

    // 镜面（预滤波 + BRDF LUT）
    vec3 prefiltered = textureLod(uPrefilterMap, R, uRoughness * 4.0).rgb;
    vec2 brdf = texture(uBrdfLUT, vec2(max(dot(N, V), 0.0), uRoughness)).rg;
    vec3 specular = prefiltered * (F0 * brdf.x + brdf.y);

    vec3 ambient = (diffuse + specular) * uAO;
    outColor = vec4(ambient, 1.0);
}";
}
