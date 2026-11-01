#version 450
// =============================================================================
// mesh.frag - 网格片段着色器（简单光照版本）
// 基础兰伯特漫反射 + 环境光。
// =============================================================================

layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec3 fragNormal;
layout(location = 2) in vec3 fragWorldPos;

layout(location = 0) out vec4 outColor;

void main()
{
    vec3 lightDir = normalize(vec3(0.5, 0.8, 0.3));
    vec3 normal = normalize(fragNormal);

    float diff = max(dot(normal, lightDir), 0.0);
    float ambient = 0.3;

    vec3 finalColor = fragColor * (ambient + diff * 0.7);
    outColor = vec4(finalColor, 1.0);
}
