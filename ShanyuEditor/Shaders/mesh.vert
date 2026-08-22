#version 450
// =============================================================================
// mesh.vert - 网格顶点着色器（GPU 变换版本）
// 使用 UBO 传递相机 ViewProjection 矩阵，Push Constant 传递 Model 矩阵。
// 用于未来的 GPU 端 3D 变换渲染。
// =============================================================================

layout(set = 0, binding = 0) uniform CameraUBO
{
    mat4 viewProj;
} camera;

layout(push_constant) uniform PushConstants
{
    mat4 model;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec3 inColor;

layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec3 fragNormal;
layout(location = 2) out vec3 fragWorldPos;

void main()
{
    vec4 worldPos = pc.model * vec4(inPosition, 1.0);
    gl_Position = camera.viewProj * worldPos;
    fragColor = inColor;
    fragNormal = mat3(pc.model) * inNormal;
    fragWorldPos = worldPos.xyz;
}
