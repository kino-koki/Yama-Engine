#version 450
// =============================================================================
// passthrough.frag - 通用直通片段着色器
// 输出顶点颜色，alpha = 1.0。
// =============================================================================

layout(location = 0) in vec3 fragColor;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(fragColor, 1.0);
}
