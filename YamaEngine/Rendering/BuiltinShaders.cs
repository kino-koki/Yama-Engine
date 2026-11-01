// ════════════════════════════════════════════════════════════════════════
// yama引擎 - 内建 HLSL 着色器源码
//
// 这些着色器源码以 C# 字符串形式内嵌，在运行时通过 D3DCompiler 编译。
// 不需要外部 .hlsl 文件，确保引擎开箱即用。
//
// 着色器列表:
//   1. BasicMeshVS/PS    — 基础网格渲染（MVP变换 + 简单光照）
//   2. LineVS/PS         — 线框渲染（网格/Gizmo/调试线）
//   3. GridVS/PS         — 无限网格渲染（屏幕空间投影）
// ════════════════════════════════════════════════════════════════════════

using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// 内建 HLSL 着色器源码集合。
    /// 所有着色器使用 Shader Model 5.0（最大兼容性）。
    /// </summary>
    public static class BuiltinShaders
    {
        // ──────────────────────────────────────────────────────────────
        // 常量缓冲区布局（C# 侧，与 HLSL 侧严格对应）
        // ──────────────────────────────────────────────────────────────

        /// <summary>每帧相机常量（Slot 0, b0）</summary>
        public struct FrameConstants
        {
            public Matrix4x4 ViewMatrix;
            public Matrix4x4 ProjectionMatrix;
            public Matrix4x4 ViewProjectionMatrix;
            public Vector3 CameraPosition;
            public float _pad0;
            public Vector3 LightDirection;
            public float _pad1;
            public Vector4 LightColor;       // RGB + intensity
            public Vector4 AmbientColor;     // RGB + intensity
        }

        /// <summary>每对象常量（Slot 1, b1）</summary>
        public struct ObjectConstants
        {
            public Matrix4x4 WorldMatrix;
            public Matrix4x4 WorldInverseTranspose;
            public Vector4 BaseColor;        // RGB + alpha
            public float Metallic;
            public float Roughness;
            public float UseTexture;         // > 0.5 时使用纹理
            public float _pad1;
        }

        /// <summary>阴影常量（Slot 3, b3）</summary>
        public struct ShadowConstants
        {
            public Matrix4x4 LightViewProj;
            public float ShadowMapSize;
            public float ShadowBias;
            public Vector2 _pad;
        }

        /// <summary>线段渲染常量（Slot 0, b0）</summary>
        public struct LineConstants
        {
            public Matrix4x4 ViewProjectionMatrix;
            public Vector4 Color;
        }

        // ──────────────────────────────────────────────────────────────
        // 基础网格顶点着色器
        // 输入: position(float3), normal(float3), uv(float2)
        // 输出: SV_POSITION, world position, world normal, uv
        // ──────────────────────────────────────────────────────────────

        public const string MeshVertexShader = @"
cbuffer FrameConstants : register(b0)
{
    float4x4 g_ViewMatrix;
    float4x4 g_ProjectionMatrix;
    float4x4 g_ViewProjectionMatrix;
    float3   g_CameraPosition;
    float    g_pad0;
    float3   g_LightDirection;
    float    g_pad1;
    float4   g_LightColor;
    float4   g_AmbientColor;
};

cbuffer ObjectConstants : register(b1)
{
    float4x4 g_WorldMatrix;
    float4x4 g_WorldInverseTranspose;
    float4   g_BaseColor;
    float    g_Metallic;
    float    g_Roughness;
    float    g_pad2;
    float    g_pad3;
};

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float2 UV       : TEXCOORD0;
};

struct VSOutput
{
    float4 Position   : SV_POSITION;
    float3 WorldPos   : WORLDPOS;
    float3 WorldNormal: NORMAL;
    float2 UV         : TEXCOORD0;
};

VSOutput main(VSInput input)
{
    VSOutput output;

    // 世界空间位置
    float4 worldPos = mul(g_WorldMatrix, float4(input.Position, 1.0f));
    output.WorldPos = worldPos.xyz;

    // 裁剪空间位置
    output.Position = mul(g_ViewProjectionMatrix, worldPos);

    // 世界空间法线 (使用世界逆转置矩阵)
    output.WorldNormal = normalize(mul((float3x3)g_WorldInverseTranspose, input.Normal));

    // UV 直接传递
    output.UV = input.UV;

    return output;
}
";

        // ──────────────────────────────────────────────────────────────
        // 基础网格像素着色器
        // 简单 Blinn-Phong 光照模型
        // ──────────────────────────────────────────────────────────────

        public const string MeshPixelShader = @"
cbuffer FrameConstants : register(b0)
{
    float4x4 g_ViewMatrix;
    float4x4 g_ProjectionMatrix;
    float4x4 g_ViewProjectionMatrix;
    float3   g_CameraPosition;
    float    g_pad0;
    float3   g_LightDirection;
    float    g_pad1;
    float4   g_LightColor;
    float4   g_AmbientColor;
};

cbuffer ObjectConstants : register(b1)
{
    float4x4 g_WorldMatrix;
    float4x4 g_WorldInverseTranspose;
    float4   g_BaseColor;
    float    g_Metallic;
    float    g_Roughness;
    float    g_UseTexture;
    float    g_pad3;
};

cbuffer ShadowConstants : register(b3)
{
    float4x4 g_LightViewProj;
    float    g_ShadowMapSize;
    float    g_ShadowBias;
    float2   g_ShadowPad;
};

Texture2D    g_AlbedoTexture : register(t0);
SamplerState g_LinearSampler : register(s0);

Texture2D    g_ShadowMap    : register(t1);
SamplerState g_ShadowSampler : register(s1);

struct PSInput
{
    float4 Position   : SV_POSITION;
    float3 WorldPos   : WORLDPOS;
    float3 WorldNormal: NORMAL;
    float2 UV         : TEXCOORD0;
};

float CalculateShadow(float3 worldPos, float3 worldNormal)
{
    // 1. 变换到光源裁剪空间
    float4 lightPos = mul(g_LightViewProj, float4(worldPos, 1.0f));
    lightPos.xyz /= lightPos.w;

    // 2. 变换到 UV 空间 [0,1]
    float2 shadowUV = lightPos.xy * 0.5f + 0.5f;
    shadowUV.y = 1.0f - shadowUV.y;  // DirectX Y翻转

    // 3. 边界检查（超出阴影贴图范围视为无阴影）
    if (shadowUV.x < 0.0f || shadowUV.x > 1.0f ||
        shadowUV.y < 0.0f || shadowUV.y > 1.0f ||
        lightPos.z > 1.0f || lightPos.z < 0.0f)
    {
        return 1.0f;
    }

    // 4. 采样 ShadowMap
    float shadowDepth = g_ShadowMap.Sample(g_ShadowSampler, shadowUV).r;

    // 5. 当前片元在光源空间的深度
    float currentDepth = lightPos.z;

    // 6. 比较（带bias）
    float bias = max(0.05f * (1.0f - dot(worldNormal, -g_LightDirection)), 0.005f);
    bias = max(bias, g_ShadowBias);
    float shadow = currentDepth - bias > shadowDepth ? 0.0f : 1.0f;

    return shadow;
}

float4 main(PSInput input) : SV_TARGET
{
    // 归一化法线
    float3 N = normalize(input.WorldNormal);

    // 光照方向（从表面指向光源）
    float3 L = normalize(-g_LightDirection);

    // 视线方向（从表面指向相机）
    float3 V = normalize(g_CameraPosition - input.WorldPos);

    // 半角向量
    float3 H = normalize(L + V);

    // 阴影
    float shadow = CalculateShadow(input.WorldPos, input.WorldNormal);

    // 漫反射
    float NdotL = max(dot(N, L), 0.0f);
    float3 diffuse = g_LightColor.rgb * g_LightColor.a * NdotL * shadow;

    // 环境光
    float3 ambient = g_AmbientColor.rgb * g_AmbientColor.a;

    // 高光（Blinn-Phong）
    float NdotH = max(dot(N, H), 0.0f);
    float specularPower = lerp(8.0f, 256.0f, 1.0f - g_Roughness);
    float specularIntensity = pow(NdotH, specularPower) * (1.0f - g_Metallic * 0.5f);
    float3 specular = g_LightColor.rgb * g_LightColor.a * specularIntensity * shadow;

    // 纹理颜色：如有真实纹理则采样，否则使用白色（让 BaseColor 常量直接生效）
    float3 textureColor;
    if (g_UseTexture > 0.5f)
    {
        textureColor = g_AlbedoTexture.Sample(g_LinearSampler, input.UV).rgb;
    }
    else
    {
        textureColor = float3(1.0f, 1.0f, 1.0f);
    }

    // 最终颜色
    float3 finalColor = g_BaseColor.rgb * textureColor * (ambient + diffuse) + specular;

    // Gamma 校正
    finalColor = pow(finalColor, 1.0f / 2.2f);

    return float4(finalColor, g_BaseColor.a);
}
";

        // ──────────────────────────────────────────────────────────────
        // 线段顶点着色器
        // 输入: position(float3), color(float4)
        // ──────────────────────────────────────────────────────────────

        public const string LineVertexShader = @"
cbuffer LineConstants : register(b0)
{
    float4x4 g_ViewProjectionMatrix;
    float4   g_LineColor;
};

struct VSInput
{
    float3 Position : POSITION;
    float4 Color    : COLOR;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR;
};

VSOutput main(VSInput input)
{
    VSOutput output;
    output.Position = mul(g_ViewProjectionMatrix, float4(input.Position, 1.0f));
    output.Color = input.Color * g_LineColor;
    return output;
}
";

        // ──────────────────────────────────────────────────────────────
        // 线段像素着色器
        // ──────────────────────────────────────────────────────────────

        public const string LinePixelShader = @"
struct PSInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR;
};

float4 main(PSInput input) : SV_TARGET
{
    return input.Color;
}
";

        // ──────────────────────────────────────────────────────────────
        // 辅助方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>获取着色器源码</summary>
        public static string GetShaderSource(string name)
        {
            return name switch
            {
                "MeshVS" => MeshVertexShader,
                "MeshPS" => MeshPixelShader,
                "LineVS" => LineVertexShader,
                "LinePS" => LinePixelShader,
                "SkinnedMeshVS" => SkinnedMeshShaders.SkinnedMeshVertexShader,
                _ => string.Empty
            };
        }

        /// <summary>网格顶点结构体大小（Position12 + Normal12 + UV8 = 32字节）</summary>
        public const int MeshVertexStride = 32;

        /// <summary>线段顶点结构体大小（Position12 + Color16 = 28字节，对齐到32）</summary>
        public const int LineVertexStride = 28;

        /// <summary>
        /// 将 EditableMesh 数据转换为紧凑顶点缓冲区（interleaved SoA→AoS）
        /// 顶点布局: [Position(12B)][Normal(12B)][UV(8B)] = 32B per vertex
        /// </summary>
        public static byte[] BuildMeshVertexBuffer(
            IReadOnlyList<Vector3> positions,
            IReadOnlyList<Vector3> normals,
            IReadOnlyList<Vector2> uvs)
        {
            int vertexCount = positions.Count;
            byte[] buffer = new byte[vertexCount * MeshVertexStride];
            int offset = 0;

            for (int i = 0; i < vertexCount; i++)
            {
                // Position (12 bytes)
                WriteVector3(buffer, ref offset, positions[i]);
                // Normal (12 bytes)
                var n = i < normals.Count ? normals[i] : Vector3.UnitY;
                WriteVector3(buffer, ref offset, n);
                // UV (8 bytes)
                var uv = i < uvs.Count ? uvs[i] : Vector2.Zero;
                WriteVector2(buffer, ref offset, uv);
            }

            return buffer;
        }

        /// <summary>
        /// 将面列表转换为索引缓冲区（uint32）
        /// </summary>
        public static byte[] BuildMeshIndexBuffer(IReadOnlyList<(int A, int B, int C)> faces)
        {
            byte[] buffer = new byte[faces.Count * 3 * sizeof(uint)];
            int offset = 0;
            for (int i = 0; i < faces.Count; i++)
            {
                WriteUInt32(buffer, ref offset, (uint)faces[i].A);
                WriteUInt32(buffer, ref offset, (uint)faces[i].B);
                WriteUInt32(buffer, ref offset, (uint)faces[i].C);
            }
            return buffer;
        }

        /// <summary>构建线段顶点缓冲区</summary>
        public static byte[] BuildLineVertexBuffer(IReadOnlyList<(Vector3 Pos, Vector4 Color)> vertices)
        {
            byte[] buffer = new byte[vertices.Count * LineVertexStride];
            int offset = 0;
            for (int i = 0; i < vertices.Count; i++)
            {
                WriteVector3(buffer, ref offset, vertices[i].Pos);
                WriteVector4(buffer, ref offset, vertices[i].Color);
            }
            return buffer;
        }

        // ── 二进制写入辅助 ──

        private static void WriteVector3(byte[] buf, ref int offset, Vector3 v)
        {
            WriteFloat(buf, ref offset, v.X);
            WriteFloat(buf, ref offset, v.Y);
            WriteFloat(buf, ref offset, v.Z);
        }

        private static void WriteVector2(byte[] buf, ref int offset, Vector2 v)
        {
            WriteFloat(buf, ref offset, v.X);
            WriteFloat(buf, ref offset, v.Y);
        }

        private static void WriteVector4(byte[] buf, ref int offset, Vector4 v)
        {
            WriteFloat(buf, ref offset, v.X);
            WriteFloat(buf, ref offset, v.Y);
            WriteFloat(buf, ref offset, v.Z);
            WriteFloat(buf, ref offset, v.W);
        }

        private static void WriteFloat(byte[] buf, ref int offset, float value)
        {
            int bytes = System.BitConverter.SingleToInt32Bits(value);
            buf[offset++] = (byte)(bytes & 0xFF);
            buf[offset++] = (byte)((bytes >> 8) & 0xFF);
            buf[offset++] = (byte)((bytes >> 16) & 0xFF);
            buf[offset++] = (byte)((bytes >> 24) & 0xFF);
        }

        private static void WriteUInt32(byte[] buf, ref int offset, uint value)
        {
            buf[offset++] = (byte)(value & 0xFF);
            buf[offset++] = (byte)((value >> 8) & 0xFF);
            buf[offset++] = (byte)((value >> 16) & 0xFF);
            buf[offset++] = (byte)((value >> 24) & 0xFF);
        }

        // ══════════════════════════════════════════════════════════════
        // PBR 纹理着色器（SimpleSceneRenderer 材质系统使用）
        // ══════════════════════════════════════════════════════════════

        public const string PBRVertexShader = @"
cbuffer WorldConstants : register(b0)
{
    float4x4 g_WorldMatrix;
    float4x4 g_ViewMatrix;
    float4x4 g_ProjectionMatrix;
    float3   g_LightDirection;
    float    g_pad0;
    float4   g_LightColor;
    float4   g_AmbientColor;
    float    g_Time;
    float3   g_pad1;
};

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float2 UV       : TEXCOORD;
    float4 Color    : COLOR;
};

struct VSOutput
{
    float4 Position   : SV_POSITION;
    float3 WorldPos   : TEXCOORD0;
    float3 WorldNormal: TEXCOORD1;
    float2 UV         : TEXCOORD2;
    float4 Color      : COLOR;
};

VSOutput main(VSInput input)
{
    VSOutput output;

    float4 worldPos = mul(g_WorldMatrix, float4(input.Position, 1.0f));
    output.WorldPos = worldPos.xyz;
    output.Position = mul(g_ProjectionMatrix, mul(g_ViewMatrix, worldPos));
    output.WorldNormal = normalize(mul((float3x3)g_WorldMatrix, input.Normal));
    output.UV = input.UV;
    output.Color = input.Color;

    return output;
}
";

        public const string PBRPixelShader = @"
cbuffer WorldConstants : register(b0)
{
    float4x4 g_WorldMatrix;
    float4x4 g_ViewMatrix;
    float4x4 g_ProjectionMatrix;
    float3   g_LightDirection;
    float    g_pad0;
    float4   g_LightColor;
    float4   g_AmbientColor;
    float    g_Time;
    float3   g_pad1;
};

cbuffer MaterialConstants : register(b1)
{
    float4   g_AlbedoColor;
    float3   g_EmissiveColor;
    float    g_EmissiveIntensity;
    float    g_Metallic;
    float    g_Roughness;
    float    g_NormalScale;
    float    g_AlphaCutoff;
    uint     g_TextureFlags;
    uint     g_pad2;
    uint     g_pad3;
    uint     g_pad4;
};

Texture2D g_AlbedoTex    : register(t0);
Texture2D g_NormalTex    : register(t1);
Texture2D g_MetallicTex  : register(t2);
Texture2D g_RoughnessTex : register(t3);
Texture2D g_OcclusionTex : register(t4);
Texture2D g_EmissiveTex  : register(t5);
SamplerState g_Sampler   : register(s0);

struct PSInput
{
    float4 Position   : SV_POSITION;
    float3 WorldPos   : TEXCOORD0;
    float3 WorldNormal: TEXCOORD1;
    float2 UV         : TEXCOORD2;
    float4 Color      : COLOR;
};

float4 main(PSInput input) : SV_TARGET
{
    float2 uv = input.UV;

    // Albedo
    float4 albedo = g_AlbedoColor;
    if (g_TextureFlags & 0x01)
    {
        albedo = g_AlbedoTex.Sample(g_Sampler, uv);
        albedo.rgb = pow(albedo.rgb, 2.2f); // sRGB -> Linear
    }
    albedo *= input.Color;

    // Alpha cutoff
    if (albedo.a < g_AlphaCutoff)
        discard;

    // Normal (simplified: no TANGENT semantic, use world normal directly)
    float3 N = normalize(input.WorldNormal);
    if (g_TextureFlags & 0x02)
    {
        float3 normalSample = g_NormalTex.Sample(g_Sampler, uv).rgb * 2.0f - 1.0f;
        normalSample.xy *= g_NormalScale;
        // Without tangent frame, we approximate by perturbing the world normal
        N = normalize(N + normalSample.x * float3(1,0,0) + normalSample.y * float3(0,1,0));
    }

    // Metallic & Roughness
    float metallic = g_Metallic;
    float roughness = g_Roughness;
    if (g_TextureFlags & 0x04)
        metallic = g_MetallicTex.Sample(g_Sampler, uv).r;
    if (g_TextureFlags & 0x08)
        roughness = g_RoughnessTex.Sample(g_Sampler, uv).r;

    // Occlusion
    float occlusion = 1.0f;
    if (g_TextureFlags & 0x10)
        occlusion = g_OcclusionTex.Sample(g_Sampler, uv).r;

    // Emissive
    float3 emissive = g_EmissiveColor * g_EmissiveIntensity;
    if (g_TextureFlags & 0x20)
        emissive = g_EmissiveTex.Sample(g_Sampler, uv).rgb * g_EmissiveIntensity;

    // Lighting
    float3 L = normalize(-g_LightDirection);
    float3 V = normalize(-mul(g_ViewMatrix, float4(0,0,0,1)).xyz - input.WorldPos);
    float3 H = normalize(L + V);

    float NdotL = max(dot(N, L), 0.0f);
    float NdotH = max(dot(N, H), 0.0f);

    float specularPower = lerp(8.0f, 256.0f, 1.0f - roughness);
    float specularIntensity = pow(NdotH, specularPower) * (1.0f - metallic * 0.5f);

    float3 ambient = g_AmbientColor.rgb * g_AmbientColor.a * occlusion;
    float3 diffuse = g_LightColor.rgb * g_LightColor.a * NdotL * (1.0f - metallic);
    float3 specular = g_LightColor.rgb * g_LightColor.a * specularIntensity;

    float3 litColor = albedo.rgb * (ambient + diffuse) + specular + emissive;

    // Gamma correction
    litColor = pow(litColor, 1.0f / 2.2f);

    return float4(litColor, albedo.a);
}
";

        // ══════════════════════════════════════════════════════════════
        // 后处理着色器
        // ══════════════════════════════════════════════════════════════

        /// <summary>后处理全屏四边形顶点着色器</summary>
        public const string PostProcessVertexShader = @"
struct VSInput
{
    float2 Position : POSITION;
    float2 UV       : TEXCOORD;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 UV       : TEXCOORD0;
};

VSOutput main(VSInput input)
{
    VSOutput output;
    output.Position = float4(input.Position, 0.0f, 1.0f);
    output.UV = input.UV;
    return output;
}
";

        /// <summary>Bloom 亮度提取像素着色器</summary>
        public const string BloomExtractPixelShader = @"
cbuffer PostProcessConstants : register(b0)
{
    float2 g_BlurDirection;
    float2 g_TexelSize;
    float  g_BloomThreshold;
    float  g_BloomIntensity;
    float  g_Exposure;
    float  g_Pad0;
};

Texture2D    g_InputTexture  : register(t0);
SamplerState g_LinearSampler : register(s0);

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 UV       : TEXCOORD0;
};

float4 main(PSInput input) : SV_TARGET
{
    float3 color = g_InputTexture.Sample(g_LinearSampler, input.UV).rgb;
    float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
    float3 bloom = color * max(luminance - g_BloomThreshold, 0.0f);
    return float4(bloom, 1.0f);
}
";

        /// <summary>Bloom 高斯模糊像素着色器（水平/垂直共用）</summary>
        public const string BloomBlurPixelShader = @"
cbuffer PostProcessConstants : register(b0)
{
    float2 g_BlurDirection;
    float2 g_TexelSize;
    float  g_BloomThreshold;
    float  g_BloomIntensity;
    float  g_Exposure;
    float  g_Pad0;
};

Texture2D    g_InputTexture  : register(t0);
SamplerState g_LinearSampler : register(s0);

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 UV       : TEXCOORD0;
};

float4 main(PSInput input) : SV_TARGET
{
    float2 offset = g_BlurDirection * g_TexelSize;
    float4 result = 0.0f;
    float weights[5] = { 0.227f, 0.194f, 0.121f, 0.054f, 0.016f };
    for (int i = -4; i <= 4; i++)
    {
        float w = weights[abs(i)];
        result += g_InputTexture.Sample(g_LinearSampler, input.UV + offset * i) * w;
    }
    return result;
}
";

        /// <summary>色调映射 + Bloom 合并像素着色器</summary>
        public const string TonemapPixelShader = @"
cbuffer PostProcessConstants : register(b0)
{
    float2 g_BlurDirection;
    float2 g_TexelSize;
    float  g_BloomThreshold;
    float  g_BloomIntensity;
    float  g_Exposure;
    float  g_Pad0;
};

Texture2D    g_SceneColor    : register(t0);
Texture2D    g_BloomTexture  : register(t1);
SamplerState g_LinearSampler : register(s0);

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 UV       : TEXCOORD0;
};

float3 ACESFilm(float3 x)
{
    float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

float4 main(PSInput input) : SV_TARGET
{
    float3 scene = g_SceneColor.Sample(g_LinearSampler, input.UV).rgb;
    float3 bloom = g_BloomTexture.Sample(g_LinearSampler, input.UV).rgb * g_BloomIntensity;
    float3 color = scene + bloom;

    // 色调映射
    color = ACESFilm(color * g_Exposure);

    // Gamma 校正
    color = pow(color, 1.0f / 2.2f);

    return float4(color, 1.0f);
}
";

        // ══════════════════════════════════════════════════════════════
        // 编译辅助方法
        // ══════════════════════════════════════════════════════════════

        /// <summary>编译基础网格顶点着色器</summary>
        public static RHIShaderBytecode CompileBasicVertexShader(RHIDevice device, bool debug = false)
        {
            return device.CompileShader(MeshVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_0, "BuiltinMeshVS", debug);
        }

        /// <summary>编译基础网格像素着色器</summary>
        public static RHIShaderBytecode CompileBasicPixelShader(RHIDevice device, bool debug = false)
        {
            return device.CompileShader(MeshPixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_0, "BuiltinMeshPS", debug);
        }

        /// <summary>编译 PBR 顶点着色器</summary>
        public static RHIShaderBytecode CompilePBRVertexShader(RHIDevice device, bool debug = false)
        {
            return device.CompileShader(PBRVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_0, "BuiltinPBRVS", debug);
        }

        /// <summary>编译 PBR 像素着色器</summary>
        public static RHIShaderBytecode CompilePBRPixelShader(RHIDevice device, bool debug = false)
        {
            return device.CompileShader(PBRPixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_0, "BuiltinPBRPS", debug);
        }

        /// <summary>编译骨骼蒙皮顶点着色器</summary>
        public static RHIShaderBytecode CompileSkinnedVertexShader(RHIDevice device, bool debug = false)
        {
            return device.CompileShader(SkinnedMeshShaders.SkinnedMeshVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_0, "BuiltinSkinnedMeshVS", debug);
        }
    }
}
