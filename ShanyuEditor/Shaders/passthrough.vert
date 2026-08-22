#version 450
// =============================================================================
// passthrough.vert - 通用直通顶点着色器
// 接收 vec2 位置 (NDC) + vec3 颜色，直接输出。
// 用于网格、线框图元等 CPU 预变换的渲染。
// =============================================================================

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec3 inColor;

layout(location = 0) out vec3 fragColor;

void main()
{
    gl_Position = vec4(inPosition, 0.0, 1.0);
    fragColor = inColor;
}
