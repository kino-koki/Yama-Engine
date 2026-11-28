// ════════════════════════════════════════════════════════════════════
// AtmosphereShadersGLSL.cs — 大气散射真实 GLSL shader (#version 450)
//
// 提供大气散射（瑞利 + 米氏）GPU pass 源码：
//   1. Sky Atmosphere (FS) —— 单次散射近似：瑞利（蓝移）+ 米氏（前向峰）
//   2. In-scatter 注入 (FS) —— 对场景表面累加大气内散射贡献（体积雾配合）
//
// 物理模型（UE4/Unreal 风格简化单次散射）：
//   Rayleigh:  beta_r * (1/λ^4)，蓝光散射最强 → 天空蓝
//   Mie:       beta_m * Henyey-Greenstein 相位函数，前向散射峰 → 太阳光环
//   View 方向沿射线积分，密度按海拔指数衰减（uHeightFalloff）
// ════════════════════════════════════════════════════════════════════

namespace YamaEngine.Rendering;

/// <summary>大气散射真实 GLSL shader 源码。</summary>
public static class AtmosphereShadersGLSL
{
    // ── 1. 天空大气 pass ───────────────────────────────────────────
    // 对天空像素做射线-大气积分，输出散射颜色。
    public const string SkyFragmentShader = @"#version 450 core
// Sky Atmosphere FS：单次散射（瑞利+米氏）天空 pass
layout(location = 0) out vec4 outColor;

// SPIR-V 要求 non-opaque uniform 显式 layout(location)。
layout(location = 0) uniform vec3 uCameraPos;
layout(location = 1) uniform vec3 uSunDirection;   // 指向太阳的单位向量
layout(location = 2) uniform vec3 uSunColor;
layout(location = 3) uniform float uSunIntensity;
layout(location = 4) uniform float uPlanetRadius;  // 地表半径（场景单位）
layout(location = 5) uniform float uAtmosphereHeight;
layout(location = 6) uniform float uHeightFalloff; // 密度指数衰减系数

// 全屏 VS（BuiltinShadersGLSL.FullScreenVertexShader）输出的输入接口。
layout(location = 1) in vec3 vWorldPos;

const float PI = 3.14159265359;
const vec3 BETA_R = vec3(5.8e-6, 13.5e-6, 33.1e-6); // 瑞利散射系数 (1/λ^4)
const vec3 BETA_M = vec3(21e-6, 21e-6, 21e-6);       // 米氏散射系数

// Henyey-Greenstein 相位函数（前向散射峰）
float HGP(float cosTheta, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5));
}

// 沿射线求大气球壳交点（进/出）
float IntersectSphere(vec3 origin, vec3 dir, float radius)
{
    float b = dot(origin, dir);
    float c = dot(origin, origin) - radius * radius;
    float disc = b * b - c;
    if (disc < 0.0) return -1.0;
    float s = sqrt(disc);
    float t = -b - s;
    if (t < 0.0) t = -b + s;
    return t;
}

// 数值积分单次散射
vec3 IntegrateScattering(vec3 origin, vec3 dir, vec3 sunDir, float tMax)
{
    const int STEPS = 32;
    float stepSize = tMax / float(STEPS);
    vec3 sum = vec3(0.0);
    float t = 0.0;
    for (int i = 0; i < STEPS; i++)
    {
        vec3 pos = origin + dir * (t + 0.5 * stepSize);
        float height = max(length(pos) - uPlanetRadius, 0.0);
        // 指数密度（地表最大、随高度衰减）
        float density = exp(-height * uHeightFalloff);
        float cosTheta = dot(dir, sunDir);
        float phaseR = 3.0 / (16.0 * PI) * (1.0 + cosTheta * cosTheta);
        float phaseM = HGP(cosTheta, 0.76);
        // 到太阳方向的光学深度（简化：沿视线近似）
        float sunOptical = exp(-height * uHeightFalloff);
        vec3 scat = (BETA_R * phaseR + BETA_M * phaseM) * density;
        sum += scat * sunOptical * stepSize;
        t += stepSize;
    }
    return sum;
}

void main()
{
    vec3 origin = uCameraPos;
    vec3 dir = normalize(vWorldPos - uCameraPos);

    float tTop = IntersectSphere(origin, dir, uPlanetRadius + uAtmosphereHeight);
    if (tTop < 0.0) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }

    vec3 scatter = IntegrateScattering(origin, dir, uSunDirection, tTop);
    // 太阳直射项（米氏前向峰产生太阳光晕）
    float sunDisc = pow(max(dot(dir, uSunDirection), 0.0), 800.0);
    vec3 color = scatter * uSunColor * uSunIntensity + uSunColor * sunDisc * uSunIntensity;

    outColor = vec4(color, 1.0);
}";

    // ── 2. 场景内散射注入 ─────────────────────────────────────────
    // 对场景表面像素累加沿视线的大气贡献（体积散射近似）。
    public const string InscatterFragmentShader = @"#version 450 core
// Atmosphere In-scatter FS：向场景表面注入大气散射贡献
layout(location = 0) out vec4 outColor;

layout(location = 0) uniform vec3 uCameraPos;
layout(location = 1) uniform vec3 uSunDirection;
layout(location = 2) uniform vec3 uSunColor;
layout(location = 3) uniform float uSunIntensity;
layout(location = 4) uniform float uHeightFalloff;

layout(location = 1) in vec3 vWorldPos;
layout(location = 2) in float vViewDepth;

const float PI = 3.14159265359;
const vec3 BETA_R = vec3(5.8e-6, 13.5e-6, 33.1e-6);
const vec3 BETA_M = vec3(21e-6, 21e-6, 21e-6);

float HGP(float cosTheta, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5));
}

void main()
{
    vec3 dir = normalize(vWorldPos - uCameraPos);
    float height = max(length(vWorldPos) - 6360.0, 0.0);
    float density = exp(-height * uHeightFalloff);
    float cosTheta = dot(dir, uSunDirection);
    float phaseR = 3.0 / (16.0 * PI) * (1.0 + cosTheta * cosTheta);
    float phaseM = HGP(cosTheta, 0.76);
    vec3 inscatter = (BETA_R * phaseR + BETA_M * phaseM) * density * vViewDepth;
    outColor = vec4(inscatter * uSunColor * uSunIntensity, 1.0);
}";
}
