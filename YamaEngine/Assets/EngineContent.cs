using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Assets
{
    // ====================================================================
    // EngineContent — 引擎内置内容
    // ====================================================================
    // 提供引擎运行时需要的内置资源：
    // - 默认着色器（内嵌 HLSL 源码字符串）
    // - 默认纹理（1x1 白色像素、棋盘格等程序化纹理）
    // - 默认材质
    // - 程序化几何体（立方体、球体、平面）
    // ====================================================================

    // ──────────────────────────────────────────────────────────────
    // EngineBuiltinShaders — 内置着色器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 引擎内置着色器集合。
    /// 所有 HLSL 代码以字符串形式内嵌，无需外部文件即可运行。
    /// </summary>
    public static class EngineBuiltinShaders
    {
        /// <summary>
        /// 默认顶点着色器。
        /// 简单的 MVP 变换，支持纹理坐标和法线传递。
        /// </summary>
        public const string DefaultVertexShader = @"
// YamaEngine Default Vertex Shader
cbuffer TransformBuffer : register(b0)
{
    matrix World;
    matrix View;
    matrix Projection;
};

struct VSInput
{
    float3 Position  : POSITION;
    float3 Normal    : NORMAL;
    float2 TexCoord   : TEXCOORD0;
    float4 Color      : COLOR0;
};

struct VSOutput
{
    float4 PositionCS  : SV_POSITION;
    float3 WorldPos    : WORLD_POS;
    float3 WorldNormal : WORLD_NORMAL;
    float2 TexCoord    : TEXCOORD0;
    float4 Color       : COLOR0;
};

VSOutput main(VSInput input)
{
    VSOutput output;

    float4 worldPos = mul(float4(input.Position, 1.0), World);
    output.WorldPos = worldPos.xyz;
    output.WorldNormal = normalize(mul(input.Normal, (float3x3)World));

    float4 viewPos = mul(worldPos, View);
    output.PositionCS = mul(viewPos, Projection);

    output.TexCoord = input.TexCoord;
    output.Color = input.Color;

    return output;
}
";

        /// <summary>
        /// 默认像素着色器。
        /// 简单纹理采样 + 顶点颜色乘法 + 基础漫反射光照。
        /// </summary>
        public const string DefaultPixelShader = @"
// YamaEngine Default Pixel Shader
Texture2D    DiffuseTexture : register(t0);
SamplerState DiffuseSampler : register(s0);

cbuffer MaterialBuffer : register(b1)
{
    float4 BaseColor;
    float  Metallic;
    float  Roughness;
    float  Emissive;
    float  Opacity;
    float3 _pad;
};

struct PSInput
{
    float4 PositionCS  : SV_POSITION;
    float3 WorldPos    : WORLD_POS;
    float3 WorldNormal : WORLD_NORMAL;
    float2 TexCoord    : TEXCOORD0;
    float4 Color       : COLOR0;
};

float4 main(PSInput input) : SV_TARGET
{
    float4 texColor = DiffuseTexture.Sample(DiffuseSampler, input.TexCoord);
    float4 baseColor = BaseColor * texColor * input.Color;

    // 简单漫反射光照
    float3 lightDir = normalize(float3(0.5, 1.0, 0.5));
    float3 normal = normalize(input.WorldNormal);
    float NdotL = max(dot(normal, lightDir), 0.0);
    float3 diffuse = baseColor.rgb * (0.3 + 0.7 * NdotL);

    return float4(diffuse, baseColor.a * Opacity);
}
";

        /// <summary>
        /// 线框模式顶点着色器（调试用）。
        /// </summary>
        public const string WireframeVertexShader = @"
cbuffer TransformBuffer : register(b0)
{
    matrix World;
    matrix View;
    matrix Projection;
};

struct VSInput
{
    float3 Position : POSITION;
};

struct VSOutput
{
    float4 PositionCS : SV_POSITION;
};

VSOutput main(VSInput input)
{
    VSOutput output;
    float4 worldPos = mul(float4(input.Position, 1.0), World);
    float4 viewPos = mul(worldPos, View);
    output.PositionCS = mul(viewPos, Projection);
    return output;
}
";

        /// <summary>
        /// 线框模式像素着色器（调试用）。
        /// </summary>
        public const string WireframePixelShader = @"
float4 main(float4 position : SV_POSITION) : SV_TARGET
{
    return float4(0.0, 1.0, 0.0, 1.0); // 绿色线框
}
";

        /// <summary>
        /// 纯色着色器（用于 UI 覆盖、清除等场景）。
        /// </summary>
        public const string SolidColorPixelShader = @"
cbuffer ColorBuffer : register(b0)
{
    float4 Color;
};

float4 main(float4 position : SV_POSITION) : SV_TARGET
{
    return Color;
}
";
    }

    // ──────────────────────────────────────────────────────────────
    // EngineBuiltinTextures — 内置纹理
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 引擎内置纹理生成器。
    /// 所有纹理程序化生成，无需外部文件。
    /// </summary>
    public static class EngineBuiltinTextures
    {
        /// <summary>
        /// 生成 1x1 白色像素纹理数据（R8G8B8A8，每通道 255）。
        /// </summary>
        public static byte[] GenerateWhitePixel()
        {
            return new byte[] { 255, 255, 255, 255 };
        }

        /// <summary>
        /// 生成 1x1 黑色像素纹理数据。
        /// </summary>
        public static byte[] GenerateBlackPixel()
        {
            return new byte[] { 0, 0, 0, 255 };
        }

        /// <summary>
        /// 生成 1x1 透明黑色像素纹理数据。
        /// </summary>
        public static byte[] GenerateTransparentPixel()
        {
            return new byte[] { 0, 0, 0, 0 };
        }

        /// <summary>
        /// 生成棋盘格纹理数据。
        /// </summary>
        /// <param name="gridSize">棋盘格单元的像素数（每个单元 gridSize x gridSize 像素）</param>
        /// <param name="gridCount">棋盘格的行列数（总尺寸 = gridSize * gridCount）</param>
        /// <param name="colorA">浅色格的颜色</param>
        /// <param name="colorB">深色格的颜色</param>
        public static byte[] GenerateCheckerboardTexture(
            int gridSize = 32, int gridCount = 4,
            Vector4? colorA = null, Vector4? colorB = null)
        {
            colorA ??= new Vector4(0.8f, 0.8f, 0.8f, 1.0f);
            colorB ??= new Vector4(0.3f, 0.3f, 0.3f, 1.0f);

            int totalSize = gridSize * gridCount;
            var pixels = new byte[totalSize * totalSize * 4];

            for (int y = 0; y < totalSize; y++)
            {
                for (int x = 0; x < totalSize; x++)
                {
                    int gridX = x / gridSize;
                    int gridY = y / gridSize;
                    bool isWhite = (gridX + gridY) % 2 == 0;

                    Vector4 color = isWhite ? colorA.Value : colorB.Value;
                    int idx = (y * totalSize + x) * 4;

                    pixels[idx + 0] = (byte)Math.Clamp(color.X * 255, 0, 255);
                    pixels[idx + 1] = (byte)Math.Clamp(color.Y * 255, 0, 255);
                    pixels[idx + 2] = (byte)Math.Clamp(color.Z * 255, 0, 255);
                    pixels[idx + 3] = (byte)Math.Clamp(color.W * 255, 0, 255);
                }
            }

            return pixels;
        }

        /// <summary>
        /// 生成渐变纹理数据（水平渐变，从 leftColor 到 rightColor）。
        /// </summary>
        public static byte[] GenerateGradientTexture(
            int width = 256, int height = 1,
            Vector4? leftColor = null, Vector4? rightColor = null)
        {
            leftColor ??= Vector4.Zero;
            rightColor ??= Vector4.One;

            var pixels = new byte[width * height * 4];

            for (int x = 0; x < width; x++)
            {
                float t = width > 1 ? (float)x / (width - 1) : 0;
                Vector4 color = Vector4.Lerp(leftColor.Value, rightColor.Value, t);

                for (int y = 0; y < height; y++)
                {
                    int idx = (y * width + x) * 4;
                    pixels[idx + 0] = (byte)Math.Clamp(color.X * 255, 0, 255);
                    pixels[idx + 1] = (byte)Math.Clamp(color.Y * 255, 0, 255);
                    pixels[idx + 2] = (byte)Math.Clamp(color.Z * 255, 0, 255);
                    pixels[idx + 3] = (byte)Math.Clamp(color.W * 255, 0, 255);
                }
            }

            return pixels;
        }

        /// <summary>
        /// 生成法线贴图纹理数据（扁平法线，全部朝 Z+）。
        /// </summary>
        public static byte[] GenerateFlatNormalMap(int width = 1, int height = 1)
        {
            // 扁平法线: RGB = (128, 128, 255)，对应 normal (0, 0, 1)
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int idx = i * 4;
                pixels[idx + 0] = 128; // R  = X = 0
                pixels[idx + 1] = 128; // G  = Y = 0
                pixels[idx + 2] = 255; // B  = Z = 1
                pixels[idx + 3] = 255; // A  = 1
            }
            return pixels;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // DefaultMaterial — 默认材质
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 默认材质描述。
    /// 使用内置白色纹理和默认着色器。
    /// </summary>
    public class DefaultMaterial
    {
        /// <summary>材质名称</summary>
        public string Name { get; set; } = "DefaultMaterial";

        /// <summary>基础颜色（线性空间）</summary>
        public Vector4 BaseColor { get; set; } = new Vector4(1, 1, 1, 1);

        /// <summary>金属度 [0, 1]</summary>
        public float Metallic { get; set; }

        /// <summary>粗糙度 [0, 1]</summary>
        public float Roughness { get; set; } = 0.5f;

        /// <summary>自发光强度</summary>
        public float Emissive { get; set; }

        /// <summary>不透明度 [0, 1]</summary>
        public float Opacity { get; set; } = 1.0f;

        /// <summary>漫反射纹理路径（null 表示使用 BaseColor）</summary>
        public string? DiffuseTexturePath { get; set; }

        /// <summary>法线贴图路径</summary>
        public string? NormalTexturePath { get; set; }

        /// <summary>顶点着色器路径（null 表示使用默认）</summary>
        public string? VertexShaderPath { get; set; }

        /// <summary>像素着色器路径（null 表示使用默认）</summary>
        public string? PixelShaderPath { get; set; }

        /// <summary>是否使用线框模式</summary>
        public bool Wireframe { get; set; }

        /// <summary>是否双面渲染</summary>
        public bool DoubleSided { get; set; }

        /// <summary>额外属性</summary>
        public Dictionary<string, object> Properties { get; } = new();

        /// <summary>
        /// 创建使用内置着色器的默认材质。
        /// </summary>
        public static DefaultMaterial CreateBuiltin()
        {
            return new DefaultMaterial
            {
                Name = "__BuiltinDefaultMaterial__",
                VertexShaderPath = "__builtin://vs_default",
                PixelShaderPath = "__builtin://ps_default"
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // ProceduralGeometry — 程序化几何体
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 顶点结构（程序化几何体使用的标准顶点格式）。
    /// 布局：Position(float3), Normal(float3), TexCoord(float2), Tangent(float3) = 44 bytes。
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct ProceduralVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;
        public Vector3 Tangent;

        public static readonly int SizeInBytes = System.Runtime.InteropServices.Marshal.SizeOf<ProceduralVertex>();

        public ProceduralVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Vector3 tangent)
        {
            Position = position;
            Normal = normal;
            TexCoord = texCoord;
            Tangent = tangent;
        }
    }

    /// <summary>
    /// 程序化几何体生成结果。
    /// </summary>
    public class ProceduralMesh : IDisposable
    {
        /// <summary>顶点数据</summary>
        public ProceduralVertex[] Vertices { get; }

        /// <summary>索引数据</summary>
        public uint[] Indices { get; }

        /// <summary>GPU 顶点缓冲区（延迟上传）</summary>
        public RHIBuffer? GpuVertexBuffer { get; set; }

        /// <summary>GPU 索引缓冲区（延迟上传）</summary>
        public RHIBuffer? GpuIndexBuffer { get; set; }

        public ProceduralMesh(ProceduralVertex[] vertices, uint[] indices)
        {
            Vertices = vertices;
            Indices = indices;
        }

        /// <summary>
        /// 将顶点/索引数据上传到 GPU。
        /// </summary>
        public void UploadToGpu(RHIDevice device, string name = "")
        {
            if (device == null) throw new ArgumentNullException(nameof(device));

            // 创建顶点缓冲
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(Vertices.Length * ProceduralVertex.SizeInBytes),
                Flags = RHIBufferFlags.VertexBuffer,
                HeapType = RHIResourceHeapType.Default
            };

            var vertexBytes = new byte[Vertices.Length * ProceduralVertex.SizeInBytes];
            System.Buffer.BlockCopy(Vertices, 0, vertexBytes, 0, vertexBytes.Length);
            GpuVertexBuffer = device.CreateCommittedBuffer(vbDesc, vertexBytes, name: $"{name}_VB");
            GpuVertexBuffer.CurrentState = RHIResourceState.VertexAndConstantBuffer;

            // 创建索引缓冲
            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(Indices.Length * sizeof(uint)),
                Flags = RHIBufferFlags.IndexBuffer,
                HeapType = RHIResourceHeapType.Default
            };

            var indexBytes = new byte[Indices.Length * sizeof(uint)];
            System.Buffer.BlockCopy(Indices, 0, indexBytes, 0, indexBytes.Length);
            GpuIndexBuffer = device.CreateCommittedBuffer(ibDesc, indexBytes, name: $"{name}_IB");
            GpuIndexBuffer.CurrentState = RHIResourceState.IndexBuffer;
        }

        public void Dispose()
        {
            GpuVertexBuffer?.Dispose();
            GpuIndexBuffer?.Dispose();
        }
    }

    /// <summary>
    /// 引擎内置几何体生成器。
    /// </summary>
    public static class EngineBuiltinGeometry
    {
        // ══════════════════════════════════════════════════════════
        // 单位立方体
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 生成单位立方体（边长 1，中心在原点）。
        /// 24 个顶点（每面 4 个），36 个索引（12 个三角形）。
        /// </summary>
        public static ProceduralMesh GenerateCube(float size = 1.0f)
        {
            float half = size * 0.5f;

            var vertices = new ProceduralVertex[24];
            var indices = new uint[36];

            // 6 个面的定义：法线方向、切线方向
            Vector3[] normals = {
                new Vector3(0, 0, 1), new Vector3(0, 0, -1),  // +Z, -Z
                new Vector3(0, 1, 0), new Vector3(0, -1, 0),  // +Y, -Y
                new Vector3(1, 0, 0), new Vector3(-1, 0, 0)   // +X, -X
            };

            Vector3[] tangents = {
                new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
                new Vector3(1, 0, 0), new Vector3(1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(0, 1, 0)
            };

            // 每面 4 个顶点位置（绕面中心逆时针排列）
            Vector3[][] facePositions = {
                // +Z 面
                new[] { new Vector3(-half, -half, half), new Vector3(half, -half, half),
                        new Vector3(half, half, half), new Vector3(-half, half, half) },
                // -Z 面
                new[] { new Vector3(half, -half, -half), new Vector3(-half, -half, -half),
                        new Vector3(-half, half, -half), new Vector3(half, half, -half) },
                // +Y 面
                new[] { new Vector3(-half, half, half), new Vector3(half, half, half),
                        new Vector3(half, half, -half), new Vector3(-half, half, -half) },
                // -Y 面
                new[] { new Vector3(-half, -half, -half), new Vector3(half, -half, -half),
                        new Vector3(half, -half, half), new Vector3(-half, -half, half) },
                // +X 面
                new[] { new Vector3(half, -half, half), new Vector3(half, -half, -half),
                        new Vector3(half, half, -half), new Vector3(half, half, half) },
                // -X 面
                new[] { new Vector3(-half, -half, -half), new Vector3(-half, -half, half),
                        new Vector3(-half, half, half), new Vector3(-half, half, -half) }
            };

            Vector2[] texCoords = {
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(1, 0), new Vector2(0, 0)
            };

            for (int face = 0; face < 6; face++)
            {
                for (int v = 0; v < 4; v++)
                {
                    vertices[face * 4 + v] = new ProceduralVertex(
                        facePositions[face][v],
                        normals[face],
                        texCoords[v],
                        tangents[face]
                    );
                }

                // 两个三角形
                uint baseIdx = (uint)(face * 4);
                indices[face * 6 + 0] = baseIdx + 0;
                indices[face * 6 + 1] = baseIdx + 1;
                indices[face * 6 + 2] = baseIdx + 2;
                indices[face * 6 + 3] = baseIdx + 0;
                indices[face * 6 + 4] = baseIdx + 2;
                indices[face * 6 + 5] = baseIdx + 3;
            }

            return new ProceduralMesh(vertices, indices);
        }

        // ══════════════════════════════════════════════════════════
        // 单位球体（UV 球）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 生成 UV 球体。
        /// </summary>
        /// <param name="radius">半径</param>
        /// <param name="segments">经线分段数</param>
        /// <param name="rings">纬线分段数</param>
        public static ProceduralMesh GenerateSphere(float radius = 0.5f, int segments = 32, int rings = 16)
        {
            var vertexList = new List<ProceduralVertex>();
            var indexList = new List<uint>();

            for (int ring = 0; ring <= rings; ring++)
            {
                float phi = MathF.PI * ring / rings; // 从北极到南极 [0, PI]
                float sinPhi = MathF.Sin(phi);
                float cosPhi = MathF.Cos(phi);

                for (int seg = 0; seg <= segments; seg++)
                {
                    float theta = 2.0f * MathF.PI * seg / segments; // 经度 [0, 2PI]
                    float sinTheta = MathF.Sin(theta);
                    float cosTheta = MathF.Cos(theta);

                    Vector3 position = new Vector3(
                        radius * sinPhi * cosTheta,
                        radius * cosPhi,
                        radius * sinPhi * sinTheta
                    );

                    Vector3 normal = Vector3.Normalize(position);

                    Vector2 texCoord = new Vector2(
                        (float)seg / segments,
                        1.0f - (float)ring / rings
                    );

                    Vector3 tangent = new Vector3(-sinTheta, 0, cosTheta);

                    vertexList.Add(new ProceduralVertex(position, normal, texCoord, tangent));
                }
            }

            for (int ring = 0; ring < rings; ring++)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    uint a = (uint)(ring * (segments + 1) + seg);
                    uint b = (uint)(a + segments + 1);

                    indexList.Add(a);
                    indexList.Add(b);
                    indexList.Add(a + 1);

                    indexList.Add(a + 1);
                    indexList.Add(b);
                    indexList.Add(b + 1);
                }
            }

            return new ProceduralMesh(vertexList.ToArray(), indexList.ToArray());
        }

        // ══════════════════════════════════════════════════════════
        // 单位平面
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 生成 XZ 平面上的平面网格。
        /// 中心在原点，面朝 Y+。
        /// </summary>
        /// <param name="width">宽度（X 方向）</param>
        /// <param name="depth">深度（Z 方向）</param>
        /// <param name="widthSegments">宽度分段数</param>
        /// <param name="depthSegments">深度分段数</param>
        public static ProceduralMesh GeneratePlane(
            float width = 1.0f, float depth = 1.0f,
            int widthSegments = 1, int depthSegments = 1)
        {
            var vertexList = new List<ProceduralVertex>();
            var indexList = new List<uint>();

            float halfW = width * 0.5f;
            float halfD = depth * 0.5f;
            Vector3 normal = Vector3.UnitY;
            Vector3 tangent = Vector3.UnitX;

            for (int z = 0; z <= depthSegments; z++)
            {
                for (int x = 0; x <= widthSegments; x++)
                {
                    float u = (float)x / widthSegments;
                    float v = (float)z / depthSegments;

                    Vector3 position = new Vector3(
                        -halfW + u * width,
                        0,
                        -halfD + v * depth
                    );

                    Vector2 texCoord = new Vector2(u, v);

                    vertexList.Add(new ProceduralVertex(position, normal, texCoord, tangent));
                }
            }

            for (int z = 0; z < depthSegments; z++)
            {
                for (int x = 0; x < widthSegments; x++)
                {
                    uint a = (uint)(z * (widthSegments + 1) + x);
                    uint b = (uint)(a + widthSegments + 1);

                    indexList.Add(a);
                    indexList.Add(b);
                    indexList.Add(a + 1);

                    indexList.Add(a + 1);
                    indexList.Add(b);
                    indexList.Add(b + 1);
                }
            }

            return new ProceduralMesh(vertexList.ToArray(), indexList.ToArray());
        }

        // ══════════════════════════════════════════════════════════
        // 单位圆盘（用于粒子等）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 生成 XZ 平面上的圆盘（中心在原点，面朝 Y+）。
        /// </summary>
        /// <param name="radius">半径</param>
        /// <param name="segments">圆周分段数</param>
        public static ProceduralMesh GenerateDisc(float radius = 0.5f, int segments = 32)
        {
            var vertexList = new List<ProceduralVertex>();
            var indexList = new List<uint>();

            Vector3 normal = Vector3.UnitY;
            Vector3 tangent = Vector3.UnitX;

            // 中心顶点
            vertexList.Add(new ProceduralVertex(
                Vector3.Zero, normal, new Vector2(0.5f, 0.5f), tangent));

            // 周围顶点
            for (int i = 0; i < segments; i++)
            {
                float angle = 2.0f * MathF.PI * i / segments;
                float x = MathF.Cos(angle) * radius;
                float z = MathF.Sin(angle) * radius;

                // UV: 将圆心映射到 (0.5, 0.5)，边缘映射到圆
                float u = 0.5f + 0.5f * MathF.Cos(angle);
                float v = 0.5f + 0.5f * MathF.Sin(angle);

                vertexList.Add(new ProceduralVertex(
                    new Vector3(x, 0, z), normal, new Vector2(u, v), tangent));
            }

            // 三角形扇
            for (uint i = 0; i < (uint)segments; i++)
            {
                indexList.Add(0);
                indexList.Add(1 + i);
                indexList.Add(1 + (i + 1) % (uint)segments);
            }

            return new ProceduralMesh(vertexList.ToArray(), indexList.ToArray());
        }
    }

    // ──────────────────────────────────────────────────────────────
    // EngineContent — 内置内容管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 引擎内置内容管理器。
    /// 统一管理所有内置着色器、纹理、材质和几何体资源。
    /// 在引擎初始化时调用 EnsureBuiltinContentLoaded() 确保所有资源就绪。
    /// </summary>
    public sealed class EngineContent : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<string, LoadedTexture> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LoadedShader> _shaders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProceduralMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private bool _initialized;

        /// <summary>默认 1x1 白色纹理</summary>
        public LoadedTexture? WhiteTexture { get; private set; }

        /// <summary>默认棋盘格纹理</summary>
        public LoadedTexture? CheckerboardTexture { get; private set; }

        /// <summary>默认立方体网格</summary>
        public ProceduralMesh? DefaultCube { get; private set; }

        /// <summary>默认球体网格</summary>
        public ProceduralMesh? DefaultSphere { get; private set; }

        /// <summary>默认平面网格</summary>
        public ProceduralMesh? DefaultPlane { get; private set; }

        /// <summary>默认材质</summary>
        public DefaultMaterial DefaultMaterial { get; } = DefaultMaterial.CreateBuiltin();

        public EngineContent(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>
        /// 确保所有内置资源已加载并上传到 GPU。
        /// </summary>
        public void EnsureBuiltinContentLoaded()
        {
            if (_initialized) return;
            _initialized = true;

            // 1. 创建白色纹理
            var whitePixels = EngineBuiltinTextures.GenerateWhitePixel();
            WhiteTexture = CreateGpuTexture("__builtin_white_1x1", whitePixels, 1, 1);
            _textures["__builtin_white"] = WhiteTexture;

            // 2. 创建棋盘格纹理
            var checkerPixels = EngineBuiltinTextures.GenerateCheckerboardTexture(
                gridSize: 32, gridCount: 4);
            int checkerSize = 32 * 4; // 128
            CheckerboardTexture = CreateGpuTexture("__builtin_checkerboard",
                checkerPixels, checkerSize, checkerSize);
            _textures["__builtin_checkerboard"] = CheckerboardTexture;

            // 3. 创建内置着色器
            _shaders["__builtin_vs_default"] = new LoadedShader(
                EngineBuiltinShaders.DefaultVertexShader, "main", "vs_6_0", "__builtin_vs_default");
            _shaders["__builtin_ps_default"] = new LoadedShader(
                EngineBuiltinShaders.DefaultPixelShader, "main", "ps_6_0", "__builtin_ps_default");
            _shaders["__builtin_vs_wireframe"] = new LoadedShader(
                EngineBuiltinShaders.WireframeVertexShader, "main", "vs_6_0", "__builtin_vs_wireframe");
            _shaders["__builtin_ps_wireframe"] = new LoadedShader(
                EngineBuiltinShaders.WireframePixelShader, "main", "ps_6_0", "__builtin_ps_wireframe");

            // 4. 创建内置几何体（仅 CPU 数据，不自动上传 GPU）
            DefaultCube = EngineBuiltinGeometry.GenerateCube(1.0f);
            _meshes["__builtin_cube"] = DefaultCube;

            DefaultSphere = EngineBuiltinGeometry.GenerateSphere(0.5f, 32, 16);
            _meshes["__builtin_sphere"] = DefaultSphere;

            DefaultPlane = EngineBuiltinGeometry.GeneratePlane(1.0f, 1.0f, 1, 1);
            _meshes["__builtin_plane"] = DefaultPlane;
        }

        /// <summary>
        /// 获取内置纹理。
        /// </summary>
        public LoadedTexture? GetTexture(string name)
        {
            _textures.TryGetValue(name, out var tex);
            return tex;
        }

        /// <summary>
        /// 获取内置着色器。
        /// </summary>
        public LoadedShader? GetShader(string name)
        {
            _shaders.TryGetValue(name, out var shader);
            return shader;
        }

        /// <summary>
        /// 获取内置几何体。
        /// </summary>
        public ProceduralMesh? GetMesh(string name)
        {
            _meshes.TryGetValue(name, out var mesh);
            return mesh;
        }

        private LoadedTexture CreateGpuTexture(string name, byte[] pixelData, int width, int height)
        {
            var texDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.None,
                OptimizedClearValue = null
            };

            RHITexture? gpuTexture = null;
            try
            {
                gpuTexture = _device.CreateTexture(texDesc, name);
                _device.UploadToTexture(gpuTexture, pixelData, width, height, mipIndex: 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"EngineContent: GPU 纹理创建失败 '{name}': {ex.Message}");
                gpuTexture?.Dispose();
                gpuTexture = null;
            }

            return new LoadedTexture(gpuTexture, width, height, 1,
                RHIFormat.R8G8B8A8_UNorm, pixelData, name);
        }

        public void Dispose()
        {
            foreach (var tex in _textures.Values) tex.Dispose();
            _textures.Clear();

            foreach (var mesh in _meshes.Values) mesh.Dispose();
            _meshes.Clear();

            WhiteTexture = null;
            CheckerboardTexture = null;
            DefaultCube = null;
            DefaultSphere = null;
            DefaultPlane = null;
        }
    }
}
