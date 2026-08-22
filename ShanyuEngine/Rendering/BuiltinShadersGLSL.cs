// ============================================================================================
//  BuiltinShadersGLSL.cs — 山雨引擎 Vulkan 后端内建 GLSL 着色器源码
//
//  这些着色器源码以 C# 字符串形式内嵌，在运行时通过 shaderc 编译为 SPIR-V。
//  与 BuiltinShaders.cs (HLSL) 对应，但使用 GLSL 语法以适配 Vulkan 后端。
//
//  着色器列表:
//    1. MeshVertexShader / MeshFragmentShader — 基础 PBR 网格渲染
//    2. LineVertexShader / LineFragmentShader — 线框 / Gizmo / 调试线
//    3. GridVertexShader / GridFragmentShader — 无限编辑器网格
//    4. FullScreenVertexShader               — 全屏三角形（后处理用）
//    5. ShadowVertexShader                   — 深度-only 阴影pass
//
//  Uniform Buffer 布局 (std140, 与 C# 侧 BuiltinShaders.FrameConstants / ObjectConstants 对应):
//    set=0, binding=0 : FrameConstants  (每帧更新)
//    set=0, binding=1 : ObjectConstants  (每物体更新)
//    set=0, binding=2 : ShadowConstants  (阴影pass用)
//    set=0, binding=3 : LineConstants    (线段pass用)
//
//  纹理绑定:
//    set=0, binding=4 : sampler2D albedoTexture
// ============================================================================================

using System.Numerics;

namespace ShanyuEngine.Rendering
{
    /// <summary>
    ///   Vulkan 后端内建 GLSL 着色器源码集合。
    ///   所有着色器使用 #version 450 core，通过 shaderc 编译为 SPIR-V。
    /// </summary>
    public static class BuiltinShadersGLSL
    {
        // ──────────────────────────────────────────────────────────────
        // 常量缓冲区布局（C# 侧，与 GLSL std140 对应）
        // ──────────────────────────────────────────────────────────────

        /// <summary>每帧相机常量（binding=0）— 与 BuiltinShaders.FrameConstants 内存布局一致。</summary>
        public struct FrameConstants
        {
            public Matrix4x4 ViewMatrix;
            public Matrix4x4 ProjectionMatrix;
            public Matrix4x4 ViewProjectionMatrix;
            public Vector3 CameraPosition;
            public float _pad0;
            public Vector3 LightDirection;
            public float _pad1;
            public Vector4 LightColor;
            public Vector4 AmbientColor;
        }

        /// <summary>每对象常量（binding=1）— 与 BuiltinShaders.ObjectConstants 内存布局一致。</summary>
        public struct ObjectConstants
        {
            public Matrix4x4 WorldMatrix;
            public Matrix4x4 WorldInverseTranspose;
            public Vector4 BaseColor;
            public float Metallic;
            public float Roughness;
            public float UseTexture;
            public float _pad1;
        }

        /// <summary>阴影常量（binding=2）。</summary>
        public struct ShadowConstants
        {
            public Matrix4x4 LightViewProj;
            public float ShadowMapSize;
            public float ShadowBias;
            public Vector2 _pad;
        }

        /// <summary>线段渲染常量（binding=3）。</summary>
        public struct LineConstants
        {
            public Matrix4x4 ViewProjectionMatrix;
            public Vector4 Color;
        }

        // ──────────────────────────────────────────────────────────────
        // 顶点输入布局 (location 约定)
        //   location=0 : vec3 position
        //   location=1 : vec3 normal
        //   location=2 : vec2 uv
        // ──────────────────────────────────────────────────────────────

        /// <summary>顶点着色器 — 基础 PBR 网格渲染。</summary>
        public const string MeshVertexShader = @"#version 450 core

layout(set = 0, binding = 0, std140) uniform FrameConstants
{
    mat4 g_ViewMatrix;
    mat4 g_ProjectionMatrix;
    mat4 g_ViewProjectionMatrix;
    vec3 g_CameraPosition;
    float _pad0;
    vec3 g_LightDirection;
    float _pad1;
    vec4 g_LightColor;
    vec4 g_AmbientColor;
};

layout(set = 0, binding = 1, std140) uniform ObjectConstants
{
    mat4 g_WorldMatrix;
    mat4 g_WorldInverseTranspose;
    vec4 g_BaseColor;
    float g_Metallic;
    float g_Roughness;
    float g_UseTexture;
    float _pad2;
};

layout(location = 0) in vec3 in_Position;
layout(location = 1) in vec3 in_Normal;
layout(location = 2) in vec2 in_TexCoord;

layout(location = 0) out vec3 v_WorldPosition;
layout(location = 1) out vec3 v_WorldNormal;
layout(location = 2) out vec2 v_TexCoord;

void main()
{
    vec4 worldPos = g_WorldMatrix * vec4(in_Position, 1.0);
    v_WorldPosition = worldPos.xyz;
    v_WorldNormal = normalize((g_WorldInverseTranspose * vec4(in_Normal, 0.0)).xyz);
    v_TexCoord = in_TexCoord;
    gl_Position = g_ViewProjectionMatrix * worldPos;
}
";

        /// <summary>片段着色器 — 基础 PBR 着色（方向光 + 环境光 + 简单 BRDF）。</summary>
        public const string MeshFragmentShader = @"#version 450 core

layout(set = 0, binding = 0, std140) uniform FrameConstants
{
    mat4 g_ViewMatrix;
    mat4 g_ProjectionMatrix;
    mat4 g_ViewProjectionMatrix;
    vec3 g_CameraPosition;
    float _pad0;
    vec3 g_LightDirection;
    float _pad1;
    vec4 g_LightColor;
    vec4 g_AmbientColor;
};

layout(set = 0, binding = 1, std140) uniform ObjectConstants
{
    mat4 g_WorldMatrix;
    mat4 g_WorldInverseTranspose;
    vec4 g_BaseColor;
    float g_Metallic;
    float g_Roughness;
    float g_UseTexture;
    float _pad2;
};

layout(set = 0, binding = 4) uniform sampler2D g_AlbedoTexture;

layout(location = 0) in vec3 v_WorldPosition;
layout(location = 1) in vec3 v_WorldNormal;
layout(location = 2) in vec2 v_TexCoord;

layout(location = 0) out vec4 out_Color;

const float PI = 3.14159265359;

// GGX 法线分布函数
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;
    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    denom = PI * denom * denom;
    return a2 / max(denom, 0.0001);
}

// Schlick 几何遮蔽 (Smith 方法)
float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggxV = NdotV / (NdotV * (1.0 - k) + k);
    float ggxL = NdotL / (NdotL * (1.0 - k) + k);
    return ggxV * ggxL;
}

// Schlick 菲涅尔近似
vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

void main()
{
    vec3 albedo = g_BaseColor.rgb;
    if (g_UseTexture > 0.5)
        albedo *= texture(g_AlbedoTexture, v_TexCoord).rgb;

    float metallic = g_Metallic;
    float roughness = max(g_Roughness, 0.04);

    vec3 N = normalize(v_WorldNormal);
    vec3 V = normalize(g_CameraPosition - v_WorldPosition);

    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // 方向光
    vec3 L = normalize(-g_LightDirection.xyz);
    vec3 H = normalize(V + L);

    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0) + 0.0001;
    vec3 specular = numerator / denominator;

    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    float NdotL = max(dot(N, L), 0.0);
    vec3 radiance = g_LightColor.rgb * g_LightColor.a;
    vec3 diffuse = kD * albedo / PI;

    vec3 directLighting = (diffuse + specular) * radiance * NdotL;

    // 环境光
    vec3 ambient = g_AmbientColor.rgb * g_AmbientColor.a * albedo;

    vec3 finalColor = ambient + directLighting;

    // 简单 Reinhard 色调映射
    finalColor = finalColor / (finalColor + vec3(1.0));
    // Gamma 校正
    finalColor = pow(finalColor, vec3(1.0 / 2.2));

    out_Color = vec4(finalColor, g_BaseColor.a);
}
";

        /// <summary>顶点着色器 — 线段 / Gizmo / 调试线渲染。</summary>
        public const string LineVertexShader = @"#version 450 core

layout(set = 0, binding = 3, std140) uniform LineConstants
{
    mat4 g_ViewProjectionMatrix;
    vec4 g_Color;
};

layout(location = 0) in vec3 in_Position;

layout(location = 0) out vec4 v_Color;

void main()
{
    gl_Position = g_ViewProjectionMatrix * vec4(in_Position, 1.0);
    v_Color = g_Color;
}
";

        /// <summary>片段着色器 — 线段渲染。</summary>
        public const string LineFragmentShader = @"#version 450 core

layout(location = 0) in vec4 v_Color;
layout(location = 0) out vec4 out_Color;

void main()
{
    out_Color = v_Color;
}
";

        /// <summary>顶点着色器 — 无限编辑器网格 (屏幕空间投影)。</summary>
        public const string GridVertexShader = @"#version 450 core

layout(set = 0, binding = 0, std140) uniform FrameConstants
{
    mat4 g_ViewMatrix;
    mat4 g_ProjectionMatrix;
    mat4 g_ViewProjectionMatrix;
    vec3 g_CameraPosition;
    float _pad0;
    vec3 g_LightDirection;
    float _pad1;
    vec4 g_LightColor;
    vec4 g_AmbientColor;
};

layout(location = 0) in vec3 in_Position;

layout(location = 0) out vec3 v_WorldPos;
layout(location = 1) out float v_ViewDist;

void main()
{
    vec4 worldPos = vec4(in_Position + g_CameraPosition.xzx, 1.0);
    worldPos.y = in_Position.y;
    v_WorldPos = worldPos.xyz;
    vec4 viewPos = g_ViewMatrix * worldPos;
    v_ViewDist = -viewPos.z;
    gl_Position = g_ProjectionMatrix * viewPos;
}
";

        /// <summary>片段着色器 — 无限网格 (基于距离淡出)。</summary>
        public const string GridFragmentShader = @"#version 450 core

layout(location = 0) in vec3 v_WorldPos;
layout(location = 1) in float v_ViewDist;

layout(location = 0) out vec4 out_Color;

float gridFactor(vec2 coord, float scale)
{
    vec2 grid = abs(fract(coord * scale - 0.5) - 0.5) / fwidth(coord * scale);
    float line = min(grid.x, grid.y);
    return 1.0 - min(line, 1.0);
}

void main()
{
    float fade = 1.0 - smoothstep(20.0, 80.0, v_ViewDist);
    float fine = gridFactor(v_WorldPos.xz, 1.0) * 0.3;
    float coarse = gridFactor(v_WorldPos.xz, 0.1) * 0.5;
    float alpha = max(fine, coarse) * fade;
    out_Color = vec4(0.5, 0.5, 0.5, alpha);
}
";

        /// <summary>顶点着色器 — 全屏三角形 (后处理 / Blit 用)。</summary>
        public const string FullScreenVertexShader = @"#version 450 core

layout(location = 0) out vec2 v_TexCoord;

void main()
{
    vec2 positions[3] = vec2[](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0)
    );
    vec2 texcoords[3] = vec2[](
        vec2(0.0, 0.0),
        vec2(2.0, 0.0),
        vec2(0.0, 2.0)
    );
    gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
    v_TexCoord = texcoords[gl_VertexIndex];
}
";

        /// <summary>片段着色器 — 全屏 Blit (纹理直通)。</summary>
        public const string FullScreenBlitFragmentShader = @"#version 450 core

layout(set = 0, binding = 4) uniform sampler2D g_SourceTexture;

layout(location = 0) in vec2 v_TexCoord;
layout(location = 0) out vec4 out_Color;

void main()
{
    out_Color = texture(g_SourceTexture, v_TexCoord);
}
";

        /// <summary>顶点着色器 — 阴影深度 pass (仅写深度, 偏移修正)。</summary>
        public const string ShadowVertexShader = @"#version 450 core

layout(set = 0, binding = 1, std140) uniform ObjectConstants
{
    mat4 g_WorldMatrix;
    mat4 g_WorldInverseTranspose;
    vec4 g_BaseColor;
    float g_Metallic;
    float g_Roughness;
    float g_UseTexture;
    float _pad2;
};

layout(set = 0, binding = 2, std140) uniform ShadowConstants
{
    mat4 g_LightViewProj;
    float g_ShadowMapSize;
    float g_ShadowBias;
    vec2 _pad3;
};

layout(location = 0) in vec3 in_Position;

void main()
{
    vec4 worldPos = g_WorldMatrix * vec4(in_Position, 1.0);
    gl_Position = g_LightViewProj * worldPos;
}
";

        // ──────────────────────────────────────────────────────────────
        // 便利方法：获取着色器源码按阶段
        // ──────────────────────────────────────────────────────────────

        /// <summary>获取基础网格渲染的顶点+片段着色器。</summary>
        public static (string vs, string fs) GetMeshShaders() => (MeshVertexShader, MeshFragmentShader);

        /// <summary>获取线段渲染的顶点+片段着色器。</summary>
        public static (string vs, string fs) GetLineShaders() => (LineVertexShader, LineFragmentShader);

        /// <summary>获取网格渲染的顶点+片段着色器。</summary>
        public static (string vs, string fs) GetGridShaders() => (GridVertexShader, GridFragmentShader);

        /// <summary>获取全屏 Blit 的顶点+片段着色器。</summary>
        public static (string vs, string fs) GetFullScreenBlitShaders() => (FullScreenVertexShader, FullScreenBlitFragmentShader);
    }
}
