// ════════════════════════════════════════════════════════════════════════
// yama引擎 - 骨骼蒙皮网格着色器源码
//
// 扩展 BuiltinShaders，提供 GPU 骨骼蒙皮所需的顶点着色器与顶点布局。
// ════════════════════════════════════════════════════════════════════════

using System.Numerics;
using YamaEngine.Modeling;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// 骨骼蒙皮着色器源码与顶点构建辅助。
    /// </summary>
    public static class SkinnedMeshShaders
    {
        // ──────────────────────────────────────────────────────────────
        // 骨骼蒙皮顶点着色器
        // 输入: position(float3), normal(float3), uv(float2), blendweights(float4), blendindices(uint4)
        // 输出: SV_POSITION, world position, world normal, uv
        // ──────────────────────────────────────────────────────────────

        public const string SkinnedMeshVertexShader = @"
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

cbuffer BoneMatrices : register(b2)
{
    float4x4 g_BoneMatrices[256];
};

struct SkinnedVSInput
{
    float3 Position    : POSITION;
    float3 Normal      : NORMAL;
    float2 UV          : TEXCOORD0;
    float4 BoneWeights : BLENDWEIGHTS;
    uint4  BoneIndices : BLENDINDICES;
};

struct VSOutput
{
    float4 Position    : SV_POSITION;
    float3 WorldPos    : WORLDPOS;
    float3 WorldNormal : NORMAL;
    float2 UV          : TEXCOORD0;
};

VSOutput main(SkinnedVSInput input)
{
    VSOutput output;

    // 骨骼蒙皮：加权混合 4 个骨骼的变换
    float4x4 skinMatrix =
        input.BoneWeights.x * g_BoneMatrices[input.BoneIndices.x] +
        input.BoneWeights.y * g_BoneMatrices[input.BoneIndices.y] +
        input.BoneWeights.z * g_BoneMatrices[input.BoneIndices.z] +
        input.BoneWeights.w * g_BoneMatrices[input.BoneIndices.w];

    // 应用蒙皮变换到位置和法线
    float4 skinnedPos = mul(skinMatrix, float4(input.Position, 1.0f));
    float3 skinnedNormal = mul((float3x3)skinMatrix, input.Normal);

    // 世界空间
    float4 worldPos = mul(g_WorldMatrix, skinnedPos);
    output.WorldPos = worldPos.xyz;
    output.Position = mul(g_ViewProjectionMatrix, worldPos);
    output.WorldNormal = normalize(mul((float3x3)g_WorldInverseTranspose, skinnedNormal));
    output.UV = input.UV;

    return output;
}
";

        /// <summary>骨骼蒙皮顶点结构体大小（Position12 + Normal12 + UV8 + BoneWeights16 + BoneIndices4 = 52字节）</summary>
        public const int SkinnedVertexStride = 52;

        // ──────────────────────────────────────────────────────────────
        // 顶点缓冲区构建
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 将带骨骼权重的 EditableMesh 数据转换为骨骼蒙皮顶点缓冲区。
        /// 顶点布局: [Position(12B)][Normal(12B)][UV(8B)][BoneWeights(16B)][BoneIndices(4B)] = 52B/vertex
        /// </summary>
        public static byte[] BuildSkinnedMeshVertexBuffer(
            IReadOnlyList<Vector3> positions,
            IReadOnlyList<Vector3> normals,
            IReadOnlyList<Vector2> uvs,
            IReadOnlyList<MeshBoneWeight> boneWeights)
        {
            int vertexCount = positions.Count;
            byte[] buffer = new byte[vertexCount * SkinnedVertexStride];
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

                // BoneWeights (16 bytes) - 归一化
                var bw = i < boneWeights.Count
                    ? boneWeights[i]
                    : new MeshBoneWeight(0, 0, 0, 0, 1.0f, 0.0f, 0.0f, 0.0f);

                float weightSum = bw.Weight0 + bw.Weight1 + bw.Weight2 + bw.Weight3;
                if (weightSum > 0.0f)
                {
                    WriteFloat(buffer, ref offset, bw.Weight0 / weightSum);
                    WriteFloat(buffer, ref offset, bw.Weight1 / weightSum);
                    WriteFloat(buffer, ref offset, bw.Weight2 / weightSum);
                    WriteFloat(buffer, ref offset, bw.Weight3 / weightSum);
                }
                else
                {
                    WriteFloat(buffer, ref offset, 1.0f);
                    WriteFloat(buffer, ref offset, 0.0f);
                    WriteFloat(buffer, ref offset, 0.0f);
                    WriteFloat(buffer, ref offset, 0.0f);
                }

                // BoneIndices (4 bytes) - 每个索引 1 字节
                buffer[offset++] = (byte)Math.Clamp(bw.Bone0, 0, 255);
                buffer[offset++] = (byte)Math.Clamp(bw.Bone1, 0, 255);
                buffer[offset++] = (byte)Math.Clamp(bw.Bone2, 0, 255);
                buffer[offset++] = (byte)Math.Clamp(bw.Bone3, 0, 255);
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

        private static void WriteFloat(byte[] buf, ref int offset, float value)
        {
            int bytes = System.BitConverter.SingleToInt32Bits(value);
            buf[offset++] = (byte)(bytes & 0xFF);
            buf[offset++] = (byte)((bytes >> 8) & 0xFF);
            buf[offset++] = (byte)((bytes >> 16) & 0xFF);
            buf[offset++] = (byte)((bytes >> 24) & 0xFF);
        }
    }
}
