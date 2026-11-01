// ════════════════════════════════════════════════════════════════════
// AdditionalEffectShadersGLSL.cs — 附加效果真实 GLSL shader (#version 450)
//
// 提供 UE4.7+ 级附加效果 GPU pass 源码：
//   1. SSGI (FS)          —— 屏幕空间全局光照（HDR 反弹，ray march）
//   2. ContactShadows(FS) —— 接触阴影（短距离 ray march 柔和边缘）
//   3. TAA (FS)           —— 时间抗锯齿（运动向量重投影 + 历史钳制）
//   4. LensFlare (FS)     —— 镜头光晕（径向条纹 + 光环叠加）
//   5. Vignette (FS)      —— 暗角
//   6. ChromaticAberration(FS) —— 色差（径向 RGB 偏移）
// ════════════════════════════════════════════════════════════════════

namespace YamaEngine.Rendering;

/// <summary>附加效果真实 GLSL shader 源码。</summary>
public static class AdditionalEffectShadersGLSL
{
    // ── 1. SSGI 屏幕空间全局光照 ──────────────────────────────────
    // 从屏幕空间 HDR 缓冲 ray march，积累间接光照。
    public const string SsgiFragmentShader = @"#version 450 core
// SSGI FS：屏幕空间全局光照（HDR 反弹）
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D uScene;    // HDR
layout(set = 0, binding = 1) uniform sampler2D uDepth;    // 场景深度
layout(set = 0, binding = 2) uniform sampler2D uNormal;   // 场景法线
layout(set = 0, binding = 3) uniform sampler2D uRadiance; // 上一次 GI（可选回馈）

uniform vec2 uTexelSize;
uniform mat4 uViewProjInverse;
uniform mat4 uPrevViewProj;

in vec2 vUV;

const float PI = 3.14159265359;

// 屏幕空间 ray march：返回命中点间接光照
vec3 RayMarch(vec3 origin, vec3 dir)
{
    vec3 result = vec3(0.0);
    const int STEPS = 24;
    float stepSize = 0.06;
    float t = 0.0;
    vec3 prevPos = origin;
    for (int i = 0; i < STEPS; i++)
    {
        t += stepSize;
        vec3 p = origin + dir * t;
        vec4 clip = uViewProjInverse * vec4(p, 1.0);
        vec3 ndc = clip.xyz / clip.w;
        vec2 uv = ndc.xy * 0.5 + 0.5;
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;
        float sceneDepth = texture(uDepth, uv).r;
        float rayDepth = ndc.z;
        if (rayDepth > sceneDepth && rayDepth - sceneDepth < 0.02)
        {
            // 命中：读取 HDR 反弹光照
            vec3 bounce = texture(uScene, uv).rgb;
            float atten = 1.0 - float(i) / float(STEPS);
            result += bounce * atten;
            break;
        }
        prevPos = p;
    }
    return result * 0.05; // 反弹强度
}

void main()
{
    vec3 origin = texture(uScene, vUV).rgb;
    vec3 N = normalize(texture(uNormal, vUV).rgb * 2.0 - 1.0);
    // 取若干反弹方向（半球分布）
    vec3 indirect = vec3(0.0);
    vec3 tangent = normalize(cross(N, vec3(0.0, 1.0, 0.0)) + vec3(1e-4));
    vec3 bitangent = cross(N, tangent);
    for (int i = 0; i < 4; i++)
    {
        float a = float(i) * 1.5708;
        vec3 dir = normalize(tangent * cos(a) + bitangent * sin(a) + N * 0.6);
        indirect += RayMarch(origin, dir);
    }
    outColor = vec4(indirect * 0.25, 1.0);
}";

    // ── 2. 接触阴影 ───────────────────────────────────────────────
    // 短距离 ray march，输出柔和接触阴影。
    public const string ContactShadowsFragmentShader = @"#version 450 core
// Contact Shadows FS：短距离 ray march 柔和阴影
layout(location = 0) out float outShadow;
layout(set = 0, binding = 0) uniform sampler2D uDepth;
layout(set = 0, binding = 1) uniform sampler2D uNormal;
uniform vec3 uLightDir;
uniform vec2 uTexelSize;

in vec2 vUV;

void main()
{
    vec3 N = normalize(texture(uNormal, vUV).rgb * 2.0 - 1.0);
    float occ = 1.0;
    const int STEPS = 16;
    float rayLen = 0.12;
    float stepSize = rayLen / float(STEPS);
    vec2 uv = vUV;
    for (int i = 1; i <= STEPS; i++)
    {
        uv -= uTexelSize * uLightDir.xy * stepSize * 16.0;
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;
        float d = texture(uDepth, uv).r;
        // 深度阻挡近似
        float blocker = d - texture(uDepth, vUV).r;
        if (blocker > 0.0 && blocker < 0.1)
        {
            occ -= (1.0 - float(i) / float(STEPS)) * 0.5;
            break;
        }
    }
    outShadow = clamp(occ, 0.0, 1.0);
}";

    // ── 3. TAA 时间抗锯齿 ─────────────────────────────────────────
    // 运动向量重投影 + 邻域颜色钳制，稳定时间抗锯齿。
    public const string TaaFragmentShader = @"#version 450 core
// TAA FS：时间抗锯齿（运动向量重投影 + 历史钳制）
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D uCurrent;   // 当前帧
layout(set = 0, binding = 1) uniform sampler2D uHistory;   // 历史帧
layout(set = 0, binding = 2) uniform sampler2D uMotion;    // 运动向量
uniform vec2 uTexelSize;

in vec2 vUV;

vec3 SampleClamp(sampler2D tex, vec2 uv)
{
    // 邻域 AABB 钳制，避免鬼影
    vec3 center = texture(tex, uv).rgb;
    vec3 c0 = texture(tex, uv + vec2(-1, -1) * uTexelSize).rgb;
    vec3 c1 = texture(tex, uv + vec2(1, -1) * uTexelSize).rgb;
    vec3 c2 = texture(tex, uv + vec2(-1, 1) * uTexelSize).rgb;
    vec3 c3 = texture(tex, uv + vec2(1, 1) * uTexelSize).rgb;
    vec3 mn = min(min(min(c0, c1), min(c2, c3)), center);
    vec3 mx = max(max(max(c0, c1), max(c2, c3)), center);
    return clamp(center, mn, mx);
}

void main()
{
    vec2 motion = texture(uMotion, vUV).xy;
    vec2 historyUV = vUV - motion;
    vec3 history = texture(uHistory, historyUV).rgb;
    vec3 current = texture(uCurrent, vUV).rgb;
    // 钳制历史到当前邻域，抑制鬼影
    vec3 clampedHistory = clamp(history,
        texture(uCurrent, vUV).rgb - 0.15,
        texture(uCurrent, vUV).rgb + 0.15);
    float blend = 0.1; // 历史权重
    outColor = vec4(mix(current, clampedHistory, blend), 1.0);
}";

    // ── 4. 镜头光晕 ───────────────────────────────────────────────
    // 径向条纹 + 光环叠加，随亮度阈值触发。
    public const string LensFlareFragmentShader = @"#version 450 core
// Lens Flare FS：径向条纹 + 光环
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D uScene;
uniform vec2 uFlareCenter;   // 光源屏幕位置
uniform float uThreshold;

in vec2 vUV;

void main()
{
    vec3 color = texture(uScene, vUV).rgb;
    float lum = dot(color, vec3(0.2126, 0.7152, 0.0722));
    float bright = smoothstep(uThreshold, uThreshold + 0.5, lum);

    vec2 toCenter = uFlareCenter - vUV;
    float dist = length(toCenter);
    vec2 dir = dist > 1e-4 ? toCenter / dist : vec2(0.0);

    // 径向条纹
    float radial = sin(dist * 60.0 + atan(dir.y, dir.x) * 4.0) * 0.5 + 0.5;
    radial = pow(radial, 6.0);

    // 光环
    float halo = smoothstep(0.15, 0.0, abs(dist - 0.35)) * 0.4;

    // 幽灵（反向光斑）
    vec2 ghostUV = vUV - toCenter * 0.6;
    float ghost = bright * texture(uScene, ghostUV).r * 0.3;

    vec3 flare = (radial * 0.4 + halo + ghost) * bright * vec3(0.6, 0.75, 1.0);
    outColor = vec4(color + flare, 1.0);
}";

    // ── 5. Vignette 暗角 ──────────────────────────────────────────
    public const string VignetteFragmentShader = @"#version 450 core
// Vignette FS：径向暗角
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D uScene;
uniform float uIntensity;   // 0..1
uniform float uRadius;      // 衰减半径

in vec2 vUV;

void main()
{
    vec3 color = texture(uScene, vUV).rgb;
    vec2 centered = vUV - 0.5;
    float dist = length(centered) / uRadius;
    float vig = smoothstep(1.0, uRadius * 0.4, dist);
    color *= mix(1.0, vig, uIntensity);
    outColor = vec4(color, 1.0);
}";

    // ── 6. Chromatic Aberration 色差 ──────────────────────────────
    public const string ChromaticAberrationFragmentShader = @"#version 450 core
// Chromatic Aberration FS：径向 RGB 偏移
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D uScene;
uniform float uAmount; // 偏移量（像素）

in vec2 vUV;

void main()
{
    vec2 centered = vUV - 0.5;
    float dist = length(centered);
    vec2 radial = dist > 1e-4 ? centered / dist : vec2(0.0);
    vec2 offset = radial * uAmount * 0.001 * dist;

    float r = texture(uScene, vUV + offset).r;
    float g = texture(uScene, vUV).g;
    float b = texture(uScene, vUV - offset).b;
    outColor = vec4(r, g, b, 1.0);
}";
}
