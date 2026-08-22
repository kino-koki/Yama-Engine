// =============================================================================
//  Shanyu Engine - ParticleShaders.cs
//  山雨引擎 - 粒子着色器源码与顶点构建工具
//
//  提供 GPU 粒子渲染所需的 HLSL 源码（顶点/像素），以及 CPU 侧公告板
//  四边形顶点缓冲区的构建辅助。
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Rendering
{
    /// <summary>
    /// 粒子着色器源码与缓冲区构建工具。
    /// </summary>
    public static class ParticleShaders
    {
        // ──────────────────────────────────────────────────────────────────────
        // 常量缓冲区布局（C# 侧，与 HLSL 侧严格 16 字节对齐）
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>每帧相机常量（Slot 0, b0）</summary>
        public struct ParticleFrameConstants
        {
            public Matrix4x4 ViewProjectionMatrix;
            public Vector3 CameraPosition;
            public float _pad0;
        }

        /// <summary>发射器常量（Slot 1, b1）</summary>
        public struct ParticleEmitterConstants
        {
            public Matrix4x4 EmitterWorldMatrix;
            public Vector4 ParticleColorTint;
            public float GlobalParticleSize;
            public float _pad0;
            public float _pad1;
            public float _pad2;
        }

        // ──────────────────────────────────────────────────────────────────────
        // HLSL 源码
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 粒子顶点着色器。
        /// 输入为 CPU 已展开的公告板四边形顶点（Position + UV + Color）。
        /// </summary>
        public const string ParticleVertexShader = @"
cbuffer FrameConstants : register(b0)
{
    float4x4 g_ViewProjectionMatrix;
    float3   g_CameraPosition;
    float    g_Pad0;
};

cbuffer EmitterConstants : register(b1)
{
    float4x4 g_EmitterWorldMatrix;
    float4   g_ParticleColorTint;
    float    g_GlobalParticleSize;
    float3   g_Pad1;
};

struct VSInput
{
    float3 Position : POSITION;
    float2 UV       : TEXCOORD0;
    float4 Color    : COLOR;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 UV       : TEXCOORD0;
    float4 Color    : COLOR;
};

VSOutput main(VSInput input)
{
    VSOutput output;
    float3 worldPos = mul(g_EmitterWorldMatrix, float4(input.Position, 1.0f)).xyz;
    output.Position = mul(g_ViewProjectionMatrix, float4(worldPos, 1.0f));
    output.UV = input.UV;
    output.Color = input.Color * g_ParticleColorTint;
    return output;
}
";

        /// <summary>
        /// 粒子像素着色器 — 软圆形粒子遮罩。
        /// </summary>
        public const string ParticlePixelShader = @"
struct PSInput
{
    float4 Position : SV_POSITION;
    float2 UV       : TEXCOORD0;
    float4 Color    : COLOR;
};

float4 main(PSInput input) : SV_TARGET
{
    // 从 UV 中心计算圆形遮罩
    float2 center = input.UV - 0.5f;
    float dist = length(center) * 2.0f;
    float alpha = saturate(1.0f - dist);

    // 软边缘
    alpha = smoothstep(0.0f, 0.2f, alpha);

    float4 color = input.Color;
    color.a *= alpha;
    return color;
}
";

        // ──────────────────────────────────────────────────────────────────────
        // 顶点布局
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>单个粒子四边形顶点的字节大小：Position(12) + UV(8) + Color(16) = 36</summary>
        public const int ParticleVertexStride = 36;

        /// <summary>
        /// 将已展开的粒子四边形数据打包为字节数组（供上传 GPU 顶点缓冲区）。
        /// </summary>
        /// <param name="quadPositions">每个粒子的 4 个角点世界坐标（长度 = 粒子数 × 4）。</param>
        /// <param name="uvs">每个顶点的 UV（长度 = 粒子数 × 4）。</param>
        /// <param name="colors">每个顶点的颜色（长度 = 粒子数 × 4）。</param>
        public static byte[] BuildParticleQuadBuffer(
            IReadOnlyList<Vector3> quadPositions,
            IReadOnlyList<Vector2> uvs,
            IReadOnlyList<Vector4> colors)
        {
            int vertexCount = quadPositions.Count;
            if (uvs.Count != vertexCount || colors.Count != vertexCount)
                throw new ArgumentException("quadPositions、uvs、colors 长度必须相等（均为 粒子数×4）。");

            byte[] buffer = new byte[vertexCount * ParticleVertexStride];
            Span<byte> span = buffer;

            for (int i = 0; i < vertexCount; i++)
            {
                int offset = i * ParticleVertexStride;
                WriteVector3(span.Slice(offset), quadPositions[i]);
                WriteVector2(span.Slice(offset + 12), uvs[i]);
                WriteVector4(span.Slice(offset + 20), colors[i]);
            }

            return buffer;
        }

        /// <summary>
        /// 为每个粒子生成公告板四边形角点，并打包为顶点字节数组。
        /// </summary>
        /// <param name="particles">粒子数据列表。</param>
        /// <param name="cameraRight">相机右向量（已归一化）。</param>
        /// <param name="cameraUp">相机上向量（已归一化）。</param>
        public static byte[] BuildParticleQuadBuffer(
            IReadOnlyList<ParticleData> particles,
            Vector3 cameraRight,
            Vector3 cameraUp)
        {
            int vertexCount = particles.Count * 4;
            byte[] buffer = new byte[vertexCount * ParticleVertexStride];
            Span<byte> span = buffer;

            // 标准四边形 UV（左下、右下、右上、左上）
            Vector2[] quadUVs = new[]
            {
                new Vector2(0.0f, 1.0f),
                new Vector2(1.0f, 1.0f),
                new Vector2(1.0f, 0.0f),
                new Vector2(0.0f, 0.0f)
            };

            for (int i = 0; i < particles.Count; i++)
            {
                var p = particles[i];
                float halfSize = p.Size * 0.5f;

                // 应用旋转
                float cosR = MathF.Cos(p.Rotation);
                float sinR = MathF.Sin(p.Rotation);
                Vector3 right = cameraRight * cosR + cameraUp * sinR;
                Vector3 up    = -cameraRight * sinR + cameraUp * cosR;

                Vector3 pos = p.Position;
                Vector4 color = p.Color;

                // 4 个角点：左下、右下、右上、左上
                Vector3[] corners = new[]
                {
                    pos - right * halfSize - up * halfSize,
                    pos + right * halfSize - up * halfSize,
                    pos + right * halfSize + up * halfSize,
                    pos - right * halfSize + up * halfSize
                };

                for (int j = 0; j < 4; j++)
                {
                    int offset = (i * 4 + j) * ParticleVertexStride;
                    WriteVector3(span.Slice(offset), corners[j]);
                    WriteVector2(span.Slice(offset + 12), quadUVs[j]);
                    WriteVector4(span.Slice(offset + 20), color);
                }
            }

            return buffer;
        }

        /// <summary>
        /// 构建粒子索引缓冲区（每个粒子 6 个索引，uint16）。
        /// </summary>
        public static byte[] BuildParticleIndexBuffer(int particleCount)
        {
            int indexCount = particleCount * 6;
            byte[] buffer = new byte[indexCount * sizeof(ushort)];
            Span<byte> span = buffer;

            for (int i = 0; i < particleCount; i++)
            {
                int baseVertex = i * 4;
                int offset = i * 6 * sizeof(ushort);

                WriteUInt16(span.Slice(offset), (ushort)(baseVertex + 0));
                WriteUInt16(span.Slice(offset + 2), (ushort)(baseVertex + 1));
                WriteUInt16(span.Slice(offset + 4), (ushort)(baseVertex + 2));
                WriteUInt16(span.Slice(offset + 6), (ushort)(baseVertex + 0));
                WriteUInt16(span.Slice(offset + 8), (ushort)(baseVertex + 2));
                WriteUInt16(span.Slice(offset + 10), (ushort)(baseVertex + 3));
            }

            return buffer;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 底层写入辅助
        // ──────────────────────────────────────────────────────────────────────

        private static void WriteVector3(Span<byte> dst, Vector3 v)
        {
            MemoryMarshal.Write(dst.Slice(0, 4), ref v.X);
            MemoryMarshal.Write(dst.Slice(4, 4), ref v.Y);
            MemoryMarshal.Write(dst.Slice(8, 4), ref v.Z);
        }

        private static void WriteVector2(Span<byte> dst, Vector2 v)
        {
            MemoryMarshal.Write(dst.Slice(0, 4), ref v.X);
            MemoryMarshal.Write(dst.Slice(4, 4), ref v.Y);
        }

        private static void WriteVector4(Span<byte> dst, Vector4 v)
        {
            MemoryMarshal.Write(dst.Slice(0, 4), ref v.X);
            MemoryMarshal.Write(dst.Slice(4, 4), ref v.Y);
            MemoryMarshal.Write(dst.Slice(8, 4), ref v.Z);
            MemoryMarshal.Write(dst.Slice(12, 4), ref v.W);
        }

        private static void WriteUInt16(Span<byte> dst, ushort value)
        {
            MemoryMarshal.Write(dst.Slice(0, 2), ref value);
        }
    }
}
