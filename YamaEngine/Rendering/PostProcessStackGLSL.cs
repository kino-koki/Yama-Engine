// ════════════════════════════════════════════════════════════════════
// PostProcessStackGLSL.cs — 完整后处理栈的真实 GLSL shader (#version 450)
//
// UE4.7 级后处理所需的 GPU pass 源码（Vulkan/SPIR-V 消费）：
//   Bloom 亮度提取 / Bloom 可分离模糊 / ACES 色调映射 / ColorGrading /
//   DOF(圆盘散射+CoC) / MotionBlur(速度采样) / Vignette / ChromaticAberration /
//   Composite(最终 HDR→LDR 合并链)
// ════════════════════════════════════════════════════════════════════

namespace YamaEngine.Rendering;

/// <summary>后处理栈真实 GLSL shader 源码。</summary>
public static class PostProcessStackGLSL
{
    // ── 1. Bloom 亮度提取 ──────────────────────────────────────────

    public const string BloomExtractFragmentShader = @"#version 450 core
// Bloom 亮度提取：超过阈值的亮度贡献
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform BloomExtractUBO { float threshold; float softKnee; } u;

void main() {
    vec3 c = texture(sceneColor, vUV).rgb;
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    float knee = u.threshold * u.softKnee + 1e-5;
    float soft = clamp(luma - u.threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-5);
    float contrib = max(soft, luma - u.threshold) / max(luma, 1e-5);
    outColor = vec4(c * contrib, 1.0);
}";

    // ── 2. Bloom 可分离模糊 ────────────────────────────────────────

    public const string BloomBlurFragmentShader = @"#version 450 core
// Bloom 可分离模糊（9-tap 高斯）
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D bloomTex;
layout(set = 0, binding = 1) uniform BloomBlurUBO { vec2 direction; float radius; } u;

const float weights[5] = float[5](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
void main() {
    vec2 texel = u.direction * u.radius / vec2(textureSize(bloomTex, 0));
    vec3 sum = texture(bloomTex, vUV).rgb * weights[0];
    for (int i = 1; i < 5; i++) {
        sum += texture(bloomTex, vUV + texel * float(i)).rgb * weights[i];
        sum += texture(bloomTex, vUV - texel * float(i)).rgb * weights[i];
    }
    outColor = vec4(sum, 1.0);
}";

    // ── 3. ACES 色调映射 ───────────────────────────────────────────

    public const string TonemapFragmentShader = @"#version 450 core
// ACES Filmic 色调映射
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform TonemapUBO { float exposure; float gamma; } u;

vec3 acesTonemap(vec3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}
void main() {
    vec3 c = texture(sceneColor, vUV).rgb * u.exposure;
    c = acesTonemap(c);
    c = pow(c, vec3(1.0 / max(u.gamma, 1e-4)));
    outColor = vec4(c, 1.0);
}";

    // ── 4. Color Grading ───────────────────────────────────────────

    public const string ColorGradingFragmentShader = @"#version 450 core
// Color Grading：饱和度 / 温度色调 / lift-gamma-gain
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform GradeUBO {
    float saturation;
    float temperature;  // 暖(-) 冷(+) 偏移
    float tint;         // 绿(-) 品红(+)
    vec4 lift;          // rgb
    vec4 gamma;         // rgb
    vec4 gain;          // rgb
} u;

void main() {
    vec3 c = texture(sceneColor, vUV).rgb;
    // 温度/色调
    c.r += u.temperature * 0.05;
    c.b -= u.temperature * 0.05;
    c.g += u.tint * 0.05;
    // lift-gamma-gain
    c = c * u.gain.rgb + u.lift.rgb;
    c = pow(max(c, vec3(0.0)), u.gamma.rgb);
    // 饱和度
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = mix(vec3(luma), c, u.saturation);
    outColor = vec4(c, 1.0);
}";

    // ── 5. 景深 DOF ────────────────────────────────────────────────

    public const string DepthOfFieldFragmentShader = @"#version 450 core
// DOF：圆盘散射，按焦外 CoC 半径模糊
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform sampler2D depthTex;
layout(set = 0, binding = 2) uniform DOFUBO {
    vec2 texelSize;
    float focalDistance;
    float focalRegion;
    float maxBlur;
} u;

void main() {
    float d = texture(depthTex, vUV).r;
    float coc = clamp(abs(d - u.focalDistance) / max(u.focalRegion, 1e-3), 0.0, 1.0) * u.maxBlur;
    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    int taps = int(coc * 16.0) + 1;
    for (int i = 0; i < taps; i++) {
        for (int j = 0; j < taps; j++) {
            vec2 off = (vec2(float(i), float(j)) / float(taps) - 0.5) * coc;
            sum += texture(sceneColor, vUV + off * u.texelSize).rgb;
            wsum += 1.0;
        }
    }
    outColor = vec4(sum / max(wsum, 1.0), 1.0);
}";

    // ── 6. 运动模糊 ────────────────────────────────────────────────

    public const string MotionBlurFragmentShader = @"#version 450 core
// Motion Blur：沿速度方向采样
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform sampler2D velocityTex;
layout(set = 0, binding = 2) uniform MotionBlurUBO { vec2 texelSize; float strength; int sampleCount; } u;

void main() {
    vec2 velocity = texture(velocityTex, vUV).xy * u.strength;
    vec3 sum = vec3(0.0);
    int n = max(u.sampleCount, 1);
    for (int i = 0; i < n; i++) {
        float t = float(i) / float(n - 1) - 0.5;
        sum += texture(sceneColor, vUV + velocity * t).rgb;
    }
    outColor = vec4(sum / float(n), 1.0);
}";

    // ── 7. Vignette + ChromaticAberration ──────────────────────────

    public const string VignetteFragmentShader = @"#version 450 core
// Vignette：屏幕暗角
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform VignetteUBO { float intensity; float roundness; } u;

void main() {
    vec2 uv = vUV - 0.5;
    float dist = length(uv * vec2(1.0, u.roundness));
    float v = smoothstep(0.8, 0.2, dist) * u.intensity + (1.0 - u.intensity);
    outColor = vec4(texture(sceneColor, vUV).rgb * v, 1.0);
}";

    public const string ChromaticAberrationFragmentShader = @"#version 450 core
// Chromatic Aberration：边缘 RGB 径向偏移
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform CAUBO { float amount; } u;

void main() {
    vec2 uv = vUV - 0.5;
    vec2 off = uv * u.amount * dot(uv, uv);
    float r = texture(sceneColor, vUV + off).r;
    float g = texture(sceneColor, vUV).g;
    float b = texture(sceneColor, vUV - off).b;
    outColor = vec4(r, g, b, 1.0);
}";

    // ── 8. Composite（最终 HDR→LDR 合并链）─────────────────────────

    public const string CompositeFragmentShader = @"#version 450 core
// Composite：合并 Bloom + 反射 + 色调映射 + 颜色分级 + 暗角/色差 到 LDR 输出
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D sceneColor;
layout(set = 0, binding = 1) uniform sampler2D bloomTex;
layout(set = 0, binding = 2) uniform sampler2D reflectionTex;
layout(set = 0, binding = 3) uniform CompositeUBO {
    float bloomIntensity;
    float reflectionIntensity;
    float exposure;
    float gamma;
    float saturation;
    float vignette;
} u;

vec3 acesTonemap(vec3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}
void main() {
    vec3 base = texture(sceneColor, vUV).rgb;
    vec3 bloom = texture(bloomTex, vUV).rgb * u.bloomIntensity;
    vec3 refl = texture(reflectionTex, vUV).rgb * u.reflectionIntensity;
    vec3 c = base + bloom + refl;
    c = c * u.exposure;
    c = acesTonemap(c);
    c = pow(c, vec3(1.0 / max(u.gamma, 1e-4)));
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = mix(vec3(luma), c, u.saturation);
    vec2 duv = vUV - 0.5;
    float vig = smoothstep(0.8, 0.2, length(duv)) * u.vignette + (1.0 - u.vignette);
    outColor = vec4(c * vig, 1.0);
}";
}
