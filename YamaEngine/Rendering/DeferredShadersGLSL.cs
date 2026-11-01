// ════════════════════════════════════════════════════════════════════
// DeferredShadersGLSL.cs — Deferred 渲染路径的真实 GLSL shader (#version 450)
//
// 提供 UE4.7 级延迟渲染所需的 GPU pass 源码（Vulkan/SPIR-V 消费）：
//   1. GBuffer (VS+FS, MRT 8 目标)  —— 与 DeferredRenderPipeline.PackGBuffer 打包约定一致
//   2. DeferredLighting (FS)         —— 读取 GBuffer，GGX Cook-Torrance 光照 + AO
//   3. SSAO (FS) + SSAOBlur (FS)     —— 半球核 AO
//   4. SSR (FS)                      —— 屏幕空间反射（深度 ray march）
//
// 打包约定（与 CPU 参考 DeferredRenderPipeline 对齐）：
//   GBuffer[0] = WorldPos (RGBA16F)
//   GBuffer[1] = Albedo  (RGBA8, A 备用)
//   GBuffer[2] = Normal  (RGBA8, Octahedral 编码到 [0,1])
//   GBuffer[3] = PBR 打包 (R=Metal, G=Roughness, B=AO)  (RGBA8)
//   GBuffer[4] = Emissive (RGBA16F)
//   GBuffer[5..7] = 备用（速度/对象ID/贴花）
// ════════════════════════════════════════════════════════════════════

namespace YamaEngine.Rendering;

/// <summary>Deferred 路径真实 GLSL shader 源码。</summary>
public static class DeferredShadersGLSL
{
    // ── 1. GBuffer ──────────────────────────────────────────────────

    public const string GBufferVertexShader = @"#version 450 core
// GBuffer VS：全屏/网格输入，输出世界空间数据到 GBuffer 各 MRT
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUV;
layout(location = 3) in vec3 inTangent;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUV;
layout(location = 3) out vec3 vTangent;

layout(set = 0, binding = 0) uniform FrameUBO {
    mat4 viewProj;
    mat4 model;
    mat4 normalMat;
} frame;

void main() {
    vec4 wp = frame.model * vec4(inPosition, 1.0);
    vWorldPos = wp.xyz;
    vNormal   = normalize(mat3(frame.normalMat) * inNormal);
    vTangent  = normalize(mat3(frame.normalMat) * inTangent);
    vUV       = inUV;
    gl_Position = frame.viewProj * wp;
}";

    public const string GBufferFragmentShader = @"#version 450 core
// GBuffer FS：输出到 8 个 MRT（WorldPos/Albedo/Normal/PBR/Emissive/备用）
layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUV;
layout(location = 3) in vec3 vTangent;

layout(set = 0, binding = 1) uniform sampler2D albedoTex;
layout(set = 0, binding = 2) uniform sampler2D normalTex;
layout(set = 0, binding = 3) uniform sampler2D metallicRoughnessTex;
layout(set = 0, binding = 4) uniform sampler2D emissiveTex;

layout(set = 0, binding = 5) uniform MaterialUBO {
    vec4 baseColor;      // RGB=albedo, A=alpha
    vec4 emissive;       // RGB=emissive, A=强度
    vec4 pbrParams;      // R=metallic, G=roughness, B=AO, A=opacity
} mat;

// Octahedral 法线编码到 [0,1]（与 CPU DeferredRenderPipeline.EncodeNormal 一致）
vec2 octahedralEncode(vec3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * (n.xy >= 0.0 ? vec2(1.0) : vec2(-1.0));
    return n.xy * 0.5 + 0.5;
}

layout(location = 0) out vec4 outWorldPos;
layout(location = 1) out vec4 outAlbedo;
layout(location = 2) out vec4 outNormal;
layout(location = 3) out vec4 outPBR;
layout(location = 4) out vec4 outEmissive;
layout(location = 5) out vec4 outVelocity;
layout(location = 6) out vec4 outObjectId;
layout(location = 7) out vec4 outSpare;

void main() {
    vec3 base   = mat.baseColor.rgb * texture(albedoTex, vUV).rgb;
    vec3 normal = normalize(mat3(1.0) * (texture(normalTex, vUV).rgb * 2.0 - 1.0));
    vec2 mr     = texture(metallicRoughnessTex, vUV).rg;
    float metallic  = clamp(mat.pbrParams.r * mr.r, 0.0, 1.0);
    float roughness = clamp(mat.pbrParams.g * mr.g, 0.05, 1.0);
    float ao        = clamp(mat.pbrParams.b, 0.0, 1.0);
    vec3 emissive   = mat.emissive.rgb * mat.emissive.a * texture(emissiveTex, vUV).rgb;

    outWorldPos = vec4(vWorldPos, 1.0);
    outAlbedo   = vec4(base, 1.0);
    outNormal   = vec4(octahedralEncode(normal), 0.0, 1.0);
    outPBR      = vec4(metallic, roughness, ao, 1.0);
    outEmissive = vec4(emissive, 1.0);
    outVelocity = vec4(0.0);
    outObjectId = vec4(0.0);
    outSpare    = vec4(0.0);
}";

    // ── 2. Deferred Lighting ───────────────────────────────────────

    public const string DeferredLightingFragmentShader = @"#version 450 core
// Deferred Lighting FS：读取 GBuffer，评估方向光/点光/聚光（GGX Cook-Torrance）+ 自发光 + AO
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D gWorldPos;
layout(set = 0, binding = 1) uniform sampler2D gAlbedo;
layout(set = 0, binding = 2) uniform sampler2D gNormal;
layout(set = 0, binding = 3) uniform sampler2D gPBR;
layout(set = 0, binding = 4) uniform sampler2D gEmissive;
layout(set = 0, binding = 5) uniform sampler2D gSSAO;

// Octahedral 解码（与 FS encode 匹配）
vec3 octahedralDecode(vec2 e) {
    e = e * 2.0 - 1.0;
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * (n.xy >= 0.0 ? vec2(1.0) : vec2(-1.0));
    return normalize(n);
}

layout(set = 0, binding = 6) uniform LightingUBO {
    vec4 cameraPos;         // xyz = 位置
    vec4 dirLightDir;       // xyz = 方向, w = 强度
    vec4 dirLightColor;     // rgb = 颜色
    vec4 pointLightPos;     // xyz = 位置
    vec4 pointLightColor;   // rgb = 颜色, w = 范围
    vec4 spotLightPos;      // xyz = 位置, w = 内角
    vec4 spotLightDir;      // xyz = 方向, w = 外角
    vec4 spotLightColor;    // rgb = 颜色, w = 强度
} lights;

// GGX NDF / Smith GGX / Fresnel-Schlick（与 CPU BRDFUtility 对齐）
float D_GGX(float NoH, float a) {
    float a2 = a * a;
    float d = NoH * NoH * (a2 - 1.0) + 1.0;
    return a2 / (3.14159265 * d * d);
}
float G_SmithGGX(float NoV, float NoL, float a) {
    float a2 = a * a;
    float gv = NoV + sqrt(a2 + (1.0 - a2) * NoV * NoV);
    float gl = NoL + sqrt(a2 + (1.0 - a2) * NoL * NoL);
    return 1.0 / (gv * gl + 1e-6);
}
vec3 F_Schlick(float VoH, vec3 f0) {
    return f0 + (1.0 - f0) * pow(clamp(1.0 - VoH, 0.0, 1.0), 5.0);
}

vec3 EvaluateLight(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo, float metallic, float roughness) {
    vec3 H = normalize(V + L);
    float NoL = max(dot(N, L), 0.0);
    if (NoL <= 0.0) return vec3(0.0);
    float NoV = max(dot(N, V), 1e-4);
    float NoH = max(dot(N, H), 0.0);
    float VoH = max(dot(V, H), 0.0);
    float a = roughness * roughness;
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    float D = D_GGX(NoH, a);
    float G = G_SmithGGX(NoV, NoL, a);
    vec3 F = F_Schlick(VoH, f0);
    vec3 spec = (D * G) * F / (4.0 * NoV * NoL + 1e-6);
    vec3 kd = (1.0 - F) * (1.0 - metallic);
    return (kd * albedo / 3.14159265 + spec) * radiance * NoL;
}

void main() {
    vec4 wp    = texture(gWorldPos, vUV);
    vec3 world = wp.xyz;
    vec3 albedo = texture(gAlbedo, vUV).rgb;
    vec3 nrm   = octahedralDecode(texture(gNormal, vUV).rg);
    vec3 pbr   = texture(gPBR, vUV).rgb;
    float metallic  = pbr.r;
    float roughness = pbr.g;
    float ao        = pbr.b * texture(gSSAO, vUV).r;
    vec3 emissive   = texture(gEmissive, vUV).rgb;
    vec3 V = normalize(lights.cameraPos.xyz - world);

    // 方向光
    vec3 Ld = normalize(-lights.dirLightDir.xyz);
    vec3 radiance = lights.dirLightColor.rgb * lights.dirLightDir.w;
    vec3 color = EvaluateLight(nrm, V, Ld, radiance, albedo, metallic, roughness);

    // 点光（距离衰减）
    vec3 toPt = lights.pointLightPos.xyz - world;
    float d = length(toPt);
    float range = max(lights.pointLightColor.w, 0.001);
    float atten = max(0.0, 1.0 - d / range); atten *= atten;
    vec3 Lp = toPt / max(d, 1e-4);
    color += EvaluateLight(nrm, V, Lp, lights.pointLightColor.rgb * atten, albedo, metallic, roughness);

    // 聚光
    vec3 toSp = lights.spotLightPos.xyz - world;
    float ds = max(length(toSp), 1e-4);
    vec3 Ls = toSp / ds;
    vec3 spotDir = normalize(lights.spotLightDir.xyz);
    float cosAng = dot(-Ls, spotDir);
    float cosInner = cos(lights.spotLightPos.w);
    float cosOuter = cos(lights.spotLightDir.w);
    float spot = smoothstep(cosOuter, cosInner, cosAng);
    vec3 radianceSpot = lights.spotLightColor.rgb * lights.spotLightColor.w * spot * max(0.0, 1.0 - ds / 100.0);
    color += EvaluateLight(nrm, V, Ls, radianceSpot, albedo, metallic, roughness);

    outColor = vec4(color * ao + emissive, 1.0);
}";

    // ── 3. SSAO ────────────────────────────────────────────────────

    public const string SSAOFragmentShader = @"#version 450 core
// SSAO FS：半球核采样（深度+法线），输出遮蔽值（与 CPU SSAOKernelGenerator 一致）
layout(location = 0) in vec2 vUV;
layout(location = 0) out float outAO;

layout(set = 0, binding = 0) uniform sampler2D gDepth;
layout(set = 0, binding = 1) uniform sampler2D gNormal;
layout(set = 0, binding = 2) uniform sampler2D noiseTex;

layout(set = 0, binding = 3) uniform SSAOUBO {
    mat4 invProj;
    vec2 texelSize;
    float radius;
    float bias;
    float power;
} ssao;

const int KERNEL_SIZE = 32;
const vec3 kernel[32] = vec3[32](
    vec3( 0.100,  0.100, 0.500), vec3(-0.080,  0.120, 0.600),
    vec3( 0.060, -0.120, 0.700), vec3(-0.140, -0.060, 0.450),
    vec3( 0.200,  0.050, 0.400), vec3(-0.050,  0.220, 0.550),
    vec3( 0.120, -0.200, 0.650), vec3(-0.220, -0.140, 0.500),
    vec3( 0.050,  0.050, 0.900), vec3(-0.100, -0.100, 0.850),
    vec3( 0.180,  0.180, 0.800), vec3(-0.180,  0.100, 0.750),
    vec3( 0.100, -0.180, 0.800), vec3(-0.100,  0.200, 0.700),
    vec3( 0.220, -0.050, 0.600), vec3(-0.050, -0.220, 0.600),
    vec3( 0.150,  0.150, 0.300), vec3(-0.150,  0.150, 0.350),
    vec3( 0.150, -0.150, 0.300), vec3(-0.150, -0.150, 0.350),
    vec3( 0.300,  0.000, 0.400), vec3(-0.300,  0.000, 0.450),
    vec3( 0.000,  0.300, 0.400), vec3( 0.000, -0.300, 0.400),
    vec3( 0.250,  0.250, 0.500), vec3(-0.250,  0.250, 0.500),
    vec3( 0.250, -0.250, 0.500), vec3(-0.250, -0.250, 0.500),
    vec3( 0.050,  0.350, 0.300), vec3(-0.050,  0.350, 0.300),
    vec3( 0.350,  0.050, 0.300), vec3(-0.350,  0.050, 0.300)
);

vec3 getPos(vec2 uv) {
    float d = texture(gDepth, uv).r;
    vec4 clip = vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
    vec4 wp = ssao.invProj * clip;
    return wp.xyz / wp.w;
}

void main() {
    vec3 pos = getPos(vUV);
    vec3 nrm = normalize(texture(gNormal, vUV).rgb * 2.0 - 1.0);
    vec3 randVec = normalize(texture(noiseTex, vUV * 64.0).xyz * 2.0 - 1.0);
    vec3 tangent = normalize(randVec - nrm * dot(randVec, nrm));
    vec3 bitangent = cross(nrm, tangent);
    mat3 tbn = mat3(tangent, bitangent, nrm);

    float occlusion = 0.0;
    for (int i = 0; i < KERNEL_SIZE; i++) {
        vec3 samplePos = pos + tbn * kernel[i] * ssao.radius;
        vec3 sPos = getPos(vUV + samplePos.xy * ssao.texelSize);
        float rangeCheck = smoothstep(0.0, 1.0, ssao.radius / max(distance(pos, sPos), 1e-3));
        occlusion += (sPos.z >= samplePos.z + ssao.bias ? 1.0 : 0.0) * rangeCheck;
    }
    outAO = pow(clamp(1.0 - occlusion / float(KERNEL_SIZE), 0.0, 1.0), ssao.power);
}";

    public const string SSAOBlurFragmentShader = @"#version 450 core
// SSAO Blur FS：可分离 5x5 模糊
layout(location = 0) in vec2 vUV;
layout(location = 0) out float outAO;
layout(set = 0, binding = 0) uniform sampler2D ssaoTex;
layout(set = 0, binding = 1) uniform BlurUBO { vec2 texelSize; } blur;

void main() {
    float sum = 0.0;
    float wsum = 0.0;
    for (int x = -2; x <= 2; x++) {
        for (int y = -2; y <= 2; y++) {
            vec2 uv = vUV + vec2(float(x), float(y)) * blur.texelSize;
            float w = exp(-float(x * x + y * y) * 0.25);
            sum += texture(ssaoTex, uv).r * w;
            wsum += w;
        }
    }
    outAO = sum / wsum;
}";

    // ── 4. SSR ─────────────────────────────────────────────────────

    public const string SSRFragmentShader = @"#version 450 core
// SSR FS：屏幕空间反射（深度 ray march），输出反射颜色
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outReflection;

layout(set = 0, binding = 0) uniform sampler2D gDepth;
layout(set = 0, binding = 1) uniform sampler2D gNormal;
layout(set = 0, binding = 2) uniform sampler2D sceneColor;

layout(set = 0, binding = 3) uniform SSRUBO {
    mat4 viewProj;
    mat4 invProj;
    vec4 cameraPos;
    float stepSize;
    float thickness;
    float maxDistance;
    float intensity;
} ssr;

vec3 getPos(vec2 uv) {
    float d = texture(gDepth, uv).r;
    vec4 clip = vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
    vec4 wp = ssr.invProj * clip;
    return wp.xyz / wp.w;
}

void main() {
    vec3 pos = getPos(vUV);
    vec3 nrm = normalize(texture(gNormal, vUV).rgb * 2.0 - 1.0);
    vec3 V = normalize(pos - ssr.cameraPos.xyz);
    vec3 R = reflect(V, nrm);

    vec3 rayPos = pos;
    vec2 uv = vUV;
    float traveled = 0.0;
    for (int i = 0; i < 64; i++) {
        rayPos += R * ssr.stepSize;
        traveled += ssr.stepSize;
        if (traveled > ssr.maxDistance) break;
        vec4 clip = ssr.viewProj * vec4(rayPos, 1.0);
        if (clip.w <= 0.0) break;
        vec2 nuv = clip.xy / clip.w * 0.5 + 0.5;
        if (nuv.x < 0.0 || nuv.x > 1.0 || nuv.y < 0.0 || nuv.y > 1.0) break;
        float sceneDepth = texture(gDepth, nuv).r;
        float rayDepth = clip.z / clip.w;
        if (abs(sceneDepth - rayDepth) < ssr.thickness) {
            outReflection = vec4(texture(sceneColor, nuv).rgb * ssr.intensity, 1.0);
            return;
        }
    }
    outReflection = vec4(0.0);
}";
}
