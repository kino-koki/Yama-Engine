// ==================================================================================================
//  RenderPrimitives.cs — 真实 D3D12 渲染原语 + 场景渲染器
//
//  命名空间: YamaEngine.Rendering
//  依赖:     RHI.RHIDevice, RHI.RHICore, Assets.EngineContent, BuiltinShaders
//
//  功能:
//    提供完整的 GPU 渲染管线，从顶点上传到最终 DrawIndexed：
//    - RenderVertex: 48 字节顶点格式 (Position+Normal+TexCoord+Color)
//    - WorldConstants: 240 字节常量数据 (WVP矩阵+光照+时间)，通过 Root Constants 传递
//    - MeshRenderResource: GPU 网格资源 (VertexBuffer + IndexBuffer)
//    - ShaderCompileResult: 编译后的着色器字节码封装
//    - PipelineStateCache: RootSignature + PSO 缓存
//    - ConstantBuffer<T>: 通用常量缓冲封装
//    - RenderItem: 可渲染项 (Mesh + WorldMatrix + Material)
//    - SceneRenderer: 场景渲染器 (核心，完整帧渲染流程)
//    - MeshGpuResource: 简化的 GPU 网格资源（直接从 CPU 数组创建）
//    - GpuConstantBuffer: 每帧常量缓冲 (World/View/Proj/光照/时间)
//    - PipelineStateObject: RootSignature + GraphicsPipelineState
//    - SimpleSceneRenderer: 简化版核心渲染器 (使用 CBV 绑定)
//
//  技术要点:
//    - 使用 Root Constants (60 dwords) 而非 Constant Buffer，避免跨队列同步问题
//    - 矩阵上传前 Transpose (System.Numerics 行主序 → HLSL 列主序)
//    - Shader Model 5.1 (d3dcompiler.dll 最高支持)
//    - NoCull 光栅化 (处理平面网格绕序问题)
//    - D32_Float 深度缓冲 + DSV
//    - 帧内同步 (Fence + FramesInFlight=2)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Assets;
using YamaEngine.Modeling;

namespace YamaEngine.Rendering
{
    // ══════════════════════════════════════════════════════════════
    // RenderVertex — 渲染顶点格式 (48 字节)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 渲染顶点结构。布局：Position(12) + Normal(12) + TexCoord(8) + Color(16) = 48 字节。
    /// 与 BuiltinShaders 的 VSInput 语义一一对应。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct RenderVertex
    {
        public Vector3 Position;   // offset 0,  12 bytes, semantic: POSITION
        public Vector3 Normal;     // offset 12, 12 bytes, semantic: NORMAL
        public Vector2 TexCoord;   // offset 24, 8 bytes,  semantic: TEXCOORD
        public Vector4 Color;      // offset 32, 16 bytes, semantic: COLOR

        public static readonly int SizeInBytes = Marshal.SizeOf<RenderVertex>();

        public RenderVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Vector4 color)
        {
            Position = position;
            Normal = normal;
            TexCoord = texCoord;
            Color = color;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // VertexData — 48字节顶点结构（别名，与 RenderVertex 等价）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 顶点数据结构，与 RenderVertex 完全等价。
    /// 提供语义更清晰的别名：Position(float3)/Normal(float3)/TexCoord(float2)/Color(float4)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VertexData
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;
        public Vector4 Color;

        public static readonly int SizeInBytes = Marshal.SizeOf<VertexData>();

        public VertexData(Vector3 position, Vector3 normal, Vector2 texCoord, Vector4 color)
        {
            Position = position;
            Normal = normal;
            TexCoord = texCoord;
            Color = color;
        }

        /// <summary>隐式转换：VertexData → RenderVertex</summary>
        public static implicit operator RenderVertex(VertexData v)
            => new RenderVertex(v.Position, v.Normal, v.TexCoord, v.Color);

        /// <summary>隐式转换：RenderVertex → VertexData</summary>
        public static implicit operator VertexData(RenderVertex v)
            => new VertexData(v.Position, v.Normal, v.TexCoord, v.Color);
    }

    // ══════════════════════════════════════════════════════════════
    // PBRVertex — PBR 渲染顶点格式 (64 字节)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PBR 渲染顶点结构。布局：Position(12) + Normal(12) + TexCoord(8) + Tangent(16) + Color(16) = 64 字节。
    /// 与带纹理的 PBR 着色器输入语义一一对应。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PBRVertex
    {
        public Vector3 Position;    // 12 bytes, semantic: POSITION
        public Vector3 Normal;      // 12 bytes, semantic: NORMAL
        public Vector2 TexCoord;    // 8 bytes,  semantic: TEXCOORD
        public Vector4 Tangent;     // 16 bytes, semantic: TANGENT (xyz=tangent, w=handedness)
        public Vector4 Color;       // 16 bytes, semantic: COLOR

        public static readonly int SizeInBytes = Marshal.SizeOf<PBRVertex>();

        public PBRVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Vector4 tangent, Vector4 color)
        {
            Position = position;
            Normal = normal;
            TexCoord = texCoord;
            Tangent = tangent;
            Color = color;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // WorldConstants — 每帧常量数据 (240 字节 = 60 dwords)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 世界常量数据。通过 Root Constants (b0) 传递给着色器。
    /// 布局必须与 HLSL cbuffer WorldConstants 严格匹配。
    /// 矩阵需在上传前 Transpose（HLSL 使用列主序，System.Numerics 使用行主序）。
    /// 总大小：240 字节 = 60 dwords（D3D12 Root Constants 限制 64 dwords）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WorldConstants
    {
        public Matrix4x4 World;           // offset 0,   64 bytes
        public Matrix4x4 View;            // offset 64,  64 bytes
        public Matrix4x4 Projection;      // offset 128, 64 bytes
        public Vector3   LightDirection;  // offset 192, 12 bytes — 光照方向（从光源指向场景）
        public float     _pad0;           // offset 204, 4 bytes  — 对齐填充
        public Vector3   LightColor;      // offset 208, 12 bytes — 方向光颜色
        public float     _pad1;           // offset 220, 4 bytes  — 对齐填充
        public Vector3   AmbientColor;    // offset 224, 12 bytes — 环境光颜色
        public float     Time;            // offset 236, 4 bytes  — 运行时间

        /// <summary>常量数据大小（dwords），用于 SetGraphicsRoot32BitConstants</summary>
        public const uint Num32BitValues = 60;

        /// <summary>常量数据大小（字节）</summary>
        public static readonly int SizeInBytes = Marshal.SizeOf<WorldConstants>();
    }

    // ══════════════════════════════════════════════════════════════
    // MaterialConstants — 材质常量数据 (64 字节 = 16 dwords)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质常量数据。通过 Root Constants (b1) 传递给着色器。
    /// 布局必须与 HLSL cbuffer MaterialConstants 严格匹配。
    /// 总大小：64 字节 = 16 dwords。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MaterialConstants
    {
        public Vector4 AlbedoColor;      // offset 0,   16 bytes
        public Vector3 EmissiveColor;    // offset 16,  12 bytes
        public float EmissiveIntensity;  // offset 28,  4 bytes
        public float Metallic;           // offset 32,  4 bytes
        public float Roughness;          // offset 36,  4 bytes
        public float NormalScale;        // offset 40,  4 bytes
        public float AlphaCutoff;        // offset 44,  4 bytes
        public uint TextureFlags;        // offset 48,  4 bytes (bit0=HasAlbedo, bit1=HasNormal, bit2=HasMetallic, bit3=HasRoughness, bit4=HasOcclusion, bit5=HasEmissive)
        public uint _pad0;               // offset 52,  4 bytes
        public uint _pad1;               // offset 56,  4 bytes
        public uint _pad2;               // offset 60,  4 bytes

        /// <summary>常量数据大小（dwords），用于 SetGraphicsRoot32BitConstants</summary>
        public const uint Num32BitValues = 16;

        /// <summary>常量数据大小（字节）</summary>
        public static readonly int SizeInBytes = Marshal.SizeOf<MaterialConstants>();
    }

    // ══════════════════════════════════════════════════════════════
    // MeshRenderResource — GPU 网格资源
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 上传到 GPU 的网格资源。包含顶点缓冲、索引缓冲和索引计数。
    /// 通过 RHIDevice.CreateCommittedBuffer 创建，初始数据随创建一并上传。
    /// </summary>
    public sealed class MeshRenderResource : IDisposable
    {
        /// <summary>GPU 顶点缓冲区</summary>
        public RHIBuffer VertexBuffer { get; }

        /// <summary>GPU 索引缓冲区</summary>
        public RHIBuffer IndexBuffer { get; }

        /// <summary>索引数量</summary>
        public uint IndexCount { get; }

        /// <summary>顶点数量</summary>
        public int VertexCount { get; }

        /// <summary>顶点步幅（字节）</summary>
        public uint VertexStride { get; }

        /// <summary>索引缓冲大小（字节）</summary>
        public uint IndexBufferSizeBytes { get; }

        /// <summary>顶点缓冲大小（字节）</summary>
        public uint VertexBufferSizeBytes { get; }

        /// <summary>
        /// 从 CPU 顶点数组和索引数组创建 GPU 网格资源。
        /// </summary>
        /// <param name="device">RHI 设备</param>
        /// <param name="vertices">CPU 顶点数组</param>
        /// <param name="indices">CPU 索引数组（uint32）</param>
        /// <param name="name">资源名称（用于 D3D12 调试）</param>
        public MeshRenderResource(RHIDevice device, RenderVertex[] vertices, uint[] indices, string name = "")
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (vertices == null || vertices.Length == 0) throw new ArgumentException("顶点数组为空", nameof(vertices));
            if (indices == null || indices.Length == 0) throw new ArgumentException("索引数组为空", nameof(indices));

            VertexCount = vertices.Length;
            IndexCount = (uint)indices.Length;
            VertexStride = (uint)RenderVertex.SizeInBytes;
            VertexBufferSizeBytes = (uint)(vertices.Length * RenderVertex.SizeInBytes);
            IndexBufferSizeBytes = (uint)(indices.Length * sizeof(uint));

            // ── 创建顶点缓冲 ──
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = VertexBufferSizeBytes,
                Flags = RHIBufferFlags.VertexBuffer,
                HeapType = RHIResourceHeapType.Default
            };

            var vertexBytes = new byte[VertexBufferSizeBytes];
            Buffer.BlockCopy(vertices, 0, vertexBytes, 0, vertexBytes.Length);
            VertexBuffer = device.CreateCommittedBuffer(vbDesc, vertexBytes, $"{name}_VB");
            VertexBuffer.CurrentState = RHIResourceState.VertexAndConstantBuffer;

            // ── 创建索引缓冲 ──
            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = IndexBufferSizeBytes,
                Flags = RHIBufferFlags.IndexBuffer,
                HeapType = RHIResourceHeapType.Default
            };

            var indexBytes = new byte[IndexBufferSizeBytes];
            Buffer.BlockCopy(indices, 0, indexBytes, 0, indexBytes.Length);
            IndexBuffer = device.CreateCommittedBuffer(ibDesc, indexBytes, $"{name}_IB");
            IndexBuffer.CurrentState = RHIResourceState.IndexBuffer;
        }

        /// <summary>
        /// 从 ProceduralMesh 创建 GPU 网格资源，自动转换顶点格式并赋予统一颜色。
        /// </summary>
        /// <param name="device">RHI 设备</param>
        /// <param name="mesh">程序化网格（ProceduralVertex 格式）</param>
        /// <param name="color">顶点颜色</param>
        /// <param name="name">资源名称</param>
        public static MeshRenderResource FromProceduralMesh(
            RHIDevice device, ProceduralMesh mesh, Vector4 color, string name = "")
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var vertices = new RenderVertex[mesh.Vertices.Length];
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                ref var src = ref mesh.Vertices[i];
                vertices[i] = new RenderVertex(src.Position, src.Normal, src.TexCoord, color);
            }

            return new MeshRenderResource(device, vertices, mesh.Indices, name);
        }

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════
    // MeshGpuResource — 简化版 GPU 网格资源
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 简化版 GPU 网格资源。
    /// 从 CPU 顶点/索引数组创建 GPU 缓冲，调用 RHIDevice.CreateCommittedBuffer + UploadToBuffer。
    /// 与 MeshRenderResource 功能等价，提供更简洁的接口。
    /// </summary>
    public sealed class MeshGpuResource : IDisposable
    {
        /// <summary>GPU 顶点缓冲</summary>
        public RHIBuffer VertexBuffer { get; private set; }

        /// <summary>GPU 索引缓冲</summary>
        public RHIBuffer IndexBuffer { get; private set; }

        /// <summary>索引数量</summary>
        public uint IndexCount { get; private set; }

        /// <summary>顶点步幅（字节）</summary>
        public uint VertexStride { get; private set; }

        /// <summary>顶点缓冲大小（字节）</summary>
        public uint VertexBufferSize { get; private set; }

        /// <summary>索引缓冲大小（字节）</summary>
        public uint IndexBufferSize { get; private set; }

        /// <summary>顶点数量</summary>
        public int VertexCount { get; private set; }

        /// <summary>资源名称</summary>
        public string Name { get; }

        private bool _disposed;

        /// <summary>
        /// 从 RenderVertex[] 和 uint[] 创建 GPU 网格资源。
        /// 通过 CreateCommittedBuffer 创建 Default 堆缓冲，初始数据随创建一并上传。
        /// </summary>
        public MeshGpuResource(RHIDevice device, RenderVertex[] vertices, uint[] indices, string name = "")
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            Name = name;

            VertexCount = vertices?.Length ?? 0;
            IndexCount = (uint)(indices?.Length ?? 0);
            VertexStride = (uint)RenderVertex.SizeInBytes;
            VertexBufferSize = (uint)(vertices?.Length ?? 0) * VertexStride;
            IndexBufferSize = (uint)(indices?.Length ?? 0) * sizeof(uint);

            // 创建顶点缓冲（Default 堆 + 初始数据）
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = VertexBufferSize,
                Flags = RHIBufferFlags.VertexBuffer,
                HeapType = RHIResourceHeapType.Default
            };

            var vbData = new byte[VertexBufferSize];
            Buffer.BlockCopy(vertices, 0, vbData, 0, vbData.Length);
            VertexBuffer = device.CreateCommittedBuffer(vbDesc, vbData, $"{name}_VB");
            VertexBuffer.CurrentState = RHIResourceState.VertexAndConstantBuffer;

            // 创建索引缓冲（Default 堆 + 初始数据）
            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = IndexBufferSize,
                Flags = RHIBufferFlags.IndexBuffer,
                HeapType = RHIResourceHeapType.Default
            };

            var ibData = new byte[IndexBufferSize];
            Buffer.BlockCopy(indices, 0, ibData, 0, ibData.Length);
            IndexBuffer = device.CreateCommittedBuffer(ibDesc, ibData, $"{name}_IB");
            IndexBuffer.CurrentState = RHIResourceState.IndexBuffer;
        }

        /// <summary>
        /// 从 VertexData[] 和 uint[] 创建 GPU 网格资源（VertexData 与 RenderVertex 内存布局一致）。
        /// </summary>
        public MeshGpuResource(RHIDevice device, VertexData[] vertices, uint[] indices, string name = "")
            : this(device, ConvertVertexData(vertices), indices, name)
        {
        }

        /// <summary>
        /// 从 ProceduralMesh 创建 GPU 网格资源，自动转换顶点格式。
        /// </summary>
        public static MeshGpuResource FromProceduralMesh(
            RHIDevice device, ProceduralMesh mesh, Vector4 color, string name = "")
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var vertices = new RenderVertex[mesh.Vertices.Length];
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                ref var src = ref mesh.Vertices[i];
                vertices[i] = new RenderVertex(src.Position, src.Normal, src.TexCoord, color);
            }

            return new MeshGpuResource(device, vertices, mesh.Indices, name);
        }

        private static RenderVertex[] ConvertVertexData(VertexData[] vd)
        {
            var rv = new RenderVertex[vd.Length];
            for (int i = 0; i < vd.Length; i++)
            {
                rv[i] = new RenderVertex(vd[i].Position, vd[i].Normal, vd[i].TexCoord, vd[i].Color);
            }
            return rv;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            VertexBuffer = null!;
            IndexBuffer = null!;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // ShaderCompileResult — 编译后的着色器字节码
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 编译后的着色器字节码封装。
    /// 持有顶点着色器和像素着色器的 RHIShaderBytecode。
    /// </summary>
    public sealed class ShaderCompileResult
    {
        /// <summary>顶点着色器字节码</summary>
        public RHIShaderBytecode VertexShader { get; }

        /// <summary>像素着色器字节码</summary>
        public RHIShaderBytecode PixelShader { get; }

        /// <summary>顶点着色器是否有效</summary>
        public bool HasVertexShader => VertexShader.IsValid;

        /// <summary>像素着色器是否有效</summary>
        public bool HasPixelShader => PixelShader.IsValid;

        /// <summary>是否所有着色器均有效</summary>
        public bool IsValid => HasVertexShader && HasPixelShader;

        public ShaderCompileResult(RHIShaderBytecode vs, RHIShaderBytecode ps)
        {
            VertexShader = vs;
            PixelShader = ps;
        }

        /// <summary>
        /// 使用 BuiltinShaders 编译基础着色器对。
        /// </summary>
        public static ShaderCompileResult CompileBuiltin(RHIDevice device, bool debug = false)
        {
            var vs = BuiltinShaders.CompileBasicVertexShader(device, debug);
            var ps = BuiltinShaders.CompileBasicPixelShader(device, debug);
            return new ShaderCompileResult(vs, ps);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // PipelineStateCache — RootSignature + PSO 缓存
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 管线状态对象缓存。持有 RootSignature 和 PipelineState。
    /// 输入布局：Position/Normal/TexCoord/Color（48 字节步幅）。
    /// 混合：无混合（Opaque）。深度：启用写入+Less比较。光栅：NoCull。
    /// </summary>
    public sealed class PipelineStateCache : IDisposable
    {
        /// <summary>根签名（Root Constants: 60 dwords at b0, AllowInputAssembler）</summary>
        public RHIRootSignature RootSignature { get; }

        /// <summary>图形管线状态对象</summary>
        public RHIPipelineState PipelineState { get; }

        /// <summary>
        /// 创建基础光照管线状态。
        /// Root Signature: 1 个 Constants 参数 (60 dwords, b0, All visibility) + AllowInputAssembler
        /// PSO: VS+PS, NoCull, DepthEnable+Write, Opaque blend, R8G8B8A8_UNorm RTV, D32_Float DSV
        /// </summary>
        public PipelineStateCache(RHIDevice device, ShaderCompileResult shaders)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (shaders == null || !shaders.IsValid)
                throw new ArgumentException("着色器无效", nameof(shaders));

            // ── 1. 创建 Root Signature ──
            // 单个 Constants 参数：60 dwords → b0, 所有着色器可见
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.Constants,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Constants = new RHIRootConstants
                        {
                            Num32BitValues = (int)WorldConstants.Num32BitValues, // 60
                            ShaderRegister = 0,
                            RegisterSpace = 0
                        }
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };

            RootSignature = device.CreateRootSignature(rootSigDesc, "BasicLightingRootSig");

            // ── 2. 创建 PSO ──
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = "BasicLightingPSO",
                VS = shaders.VertexShader,
                PS = shaders.PixelShader,
                InputLayout = new RHIInputElement[]
                {
                    new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float,  0, 0),   // offset 0
                    new RHIInputElement("NORMAL",   RHIFormat.R32G32B32_Float,  0, 12),  // offset 12
                    new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float,     0, 24),  // offset 24
                    new RHIInputElement("COLOR",    RHIFormat.R32G32B32A32_Float, 0, 32) // offset 32
                },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,           // 不透明，无混合
                DepthStencilState = RHIDepthDesc.Default,    // 深度测试+写入，Less 比较
                RasterizerState = RHIRasterizerDesc.NoCull,  // 不剔除（处理平面绕序）
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = RHIFormat.D32_Float,
                SampleCount = 1,
                RootSignature = RootSignature
            };

            PipelineState = device.CreateGraphicsPipelineState(psoDesc, "BasicLightingPSO");
        }

        public void Dispose()
        {
            PipelineState?.Dispose();
            RootSignature?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════
    // GpuConstantBuffer — 每帧常量缓冲 (World/View/Proj/光照/时间)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// GPU 常量缓冲：承载 WorldConstants 数据（World/View/Proj 矩阵 + 光照 + 时间）。
    /// 在 Upload 堆上分配，CPU 可直接写入，GPU 读取。
    /// 大小 256 字节对齐（D3D12 CBV 要求）。
    /// 提供 Set() 方法一次性更新所有常量，以及独立的矩阵/光照更新方法。
    /// </summary>
    public sealed class GpuConstantBuffer : IDisposable
    {
        /// <summary>GPU 缓冲区（Upload 堆）</summary>
        public RHIBuffer Buffer { get; private set; }

        /// <summary>实际数据大小（字节）</summary>
        public uint DataSize { get; }

        /// <summary>缓冲区大小（字节，256 字节对齐）</summary>
        public uint BufferSize { get; }

        /// <summary>当前常量数据（CPU 侧副本）</summary>
        public WorldConstants Constants { get; private set; }

        private readonly RHIDevice _device;
        private bool _disposed;

        /// <summary>
        /// 创建 GPU 常量缓冲。大小按 256 字节对齐。
        /// </summary>
        public GpuConstantBuffer(RHIDevice device, string name = "GpuConstantBuffer")
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            DataSize = (uint)WorldConstants.SizeInBytes;
            // D3D12 CBV 要求 256 字节对齐
            BufferSize = (DataSize + 255u) & ~255u;

            Constants = new WorldConstants();

            var desc = new RHIBufferDesc
            {
                SizeInBytes = BufferSize,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            };

            Buffer = device.CreateBuffer(desc, name);
            Buffer.CurrentState = RHIResourceState.VertexAndConstantBuffer;

            // 初始化为零
            Upload();
        }

        /// <summary>更新全部常量并上传到 GPU</summary>
        public void Set(in WorldConstants constants)
        {
            Constants = constants;
            Upload();
        }

        /// <summary>更新 World 矩阵并上传</summary>
        public void SetWorldMatrix(Matrix4x4 world)
        {
            var c = Constants;
            c.World = world;
            Set(c);
        }

        /// <summary>更新 View + Projection 矩阵并上传</summary>
        public void SetViewProjection(Matrix4x4 view, Matrix4x4 projection)
        {
            var c = Constants;
            c.View = view;
            c.Projection = projection;
            Set(c);
        }

        /// <summary>更新光照参数并上传</summary>
        public void SetLighting(Vector3 lightDir, Vector3 lightColor, Vector3 ambient)
        {
            var c = Constants;
            c.LightDirection = lightDir;
            c.LightColor = lightColor;
            c.AmbientColor = ambient;
            Set(c);
        }

        /// <summary>更新时间并上传</summary>
        public void SetTime(float time)
        {
            var c = Constants;
            c.Time = time;
            Set(c);
        }

        /// <summary>创建 CBV 描述符（用于 Root Descriptor Table 绑定）</summary>
        public RHIDescriptorHandle CreateCBV()
        {
            return _device.CreateCBV(Buffer, 0, BufferSize);
        }

        /// <summary>上传当前 Constants 到 GPU 缓冲</summary>
        private void Upload()
        {
            _device.UploadToBuffer(Buffer, new[] { Constants });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Buffer?.Dispose();
            Buffer = null!;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // PipelineStateObject — RootSignature + GraphicsPipelineState
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 管线状态对象封装。持有 RootSignature 和 GraphicsPipelineState。
    /// 提供标准输入布局（Position/Normal/TexCoord/Color，48 字节步幅），
    /// 支持自定义光栅化状态（NoCull/Wireframe）和深度状态。
    /// 可选择使用 Root Constants（默认）或 CBV 绑定模式。
    /// </summary>
    public sealed class PipelineStateObject : IDisposable
    {
        /// <summary>根签名</summary>
        public RHIRootSignature RootSignature { get; }

        /// <summary>图形管线状态对象</summary>
        public RHIPipelineState PipelineState { get; }

        /// <summary>输入布局（Position/Normal/TexCoord/Color，48 字节步幅）</summary>
        public static readonly RHIInputElement[] DefaultInputLayout = new RHIInputElement[]
        {
            new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float,  0, 0),   // offset 0
            new RHIInputElement("NORMAL",   RHIFormat.R32G32B32_Float,  0, 12),  // offset 12
            new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float,     0, 24),  // offset 24
            new RHIInputElement("COLOR",    RHIFormat.R32G32B32A32_Float, 0, 32) // offset 32
        };

        /// <summary>PBR 输入布局（Position/Normal/TexCoord/Tangent/Color，64 字节步幅）</summary>
        public static readonly RHIInputElement[] PBRInputLayout = new RHIInputElement[]
        {
            new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float,  0, 0),   // offset 0
            new RHIInputElement("NORMAL",   RHIFormat.R32G32B32_Float,  0, 12),  // offset 12
            new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float,     0, 24),  // offset 24
            new RHIInputElement("TANGENT",  RHIFormat.R32G32B32A32_Float, 0, 32), // offset 32
            new RHIInputElement("COLOR",    RHIFormat.R32G32B32A32_Float, 0, 48) // offset 48
        };

        /// <summary>
        /// 创建使用 Root Constants (b0, 60 dwords) 的管线状态。
        /// </summary>
        public PipelineStateObject(
            RHIDevice device,
            RHIShaderBytecode vertexShader,
            RHIShaderBytecode pixelShader,
            string name = "DefaultPSO",
            RHIRasterizerDesc? rasterizer = null,
            RHIFormat dsvFormat = RHIFormat.D32_Float,
            RHIFormat[]? rtvFormats = null)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (!vertexShader.IsValid) throw new ArgumentException("顶点着色器无效", nameof(vertexShader));
            if (!pixelShader.IsValid) throw new ArgumentException("像素着色器无效", nameof(pixelShader));

            // ── Root Signature: 1 个 Constants 参数 (60 dwords, b0) ──
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.Constants,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Constants = new RHIRootConstants
                        {
                            Num32BitValues = (int)WorldConstants.Num32BitValues,
                            ShaderRegister = 0,
                            RegisterSpace = 0
                        }
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };

            RootSignature = device.CreateRootSignature(rootSigDesc, $"{name}_RootSig");

            // ── PSO ──
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = name,
                VS = vertexShader,
                PS = pixelShader,
                InputLayout = DefaultInputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = RHIDepthDesc.Default,
                RasterizerState = rasterizer ?? RHIRasterizerDesc.NoCull,
                RTVFormats = rtvFormats ?? new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = dsvFormat,
                SampleCount = 1,
                RootSignature = RootSignature
            };

            PipelineState = device.CreateGraphicsPipelineState(psoDesc, name);
        }

        /// <summary>
        /// 从 ShaderCompileResult 创建管线状态。
        /// </summary>
        public PipelineStateObject(RHIDevice device, ShaderCompileResult shaders, string name = "DefaultPSO")
            : this(device, shaders.VertexShader, shaders.PixelShader, name)
        {
        }

        /// <summary>
        /// 创建线框渲染管线状态（Wireframe 光栅化 + 线框像素着色器）。
        /// </summary>
        public static PipelineStateObject CreateWireframe(
            RHIDevice device,
            RHIShaderBytecode vertexShader,
            RHIShaderBytecode wireframePixelShader,
            string name = "WireframePSO")
        {
            var wireframeRasterizer = new RHIRasterizerDesc
            {
                CullMode = RHICullMode.None,
                FillMode = RHIFillMode.Wireframe
            };

            return new PipelineStateObject(
                device, vertexShader, wireframePixelShader, name,
                wireframeRasterizer, RHIFormat.D32_Float);
        }

        /// <summary>
        /// 创建 PBR 管线状态（支持材质常量 b1 + 纹理 SRV + 静态采样器）。
        /// 根签名布局：
        ///   [0] Root Constants  : b0, 60 dwords (WorldConstants)
        ///   [1] Root Constants  : b1, 16 dwords (MaterialConstants)
        ///   [2] DescriptorTable : SRV t0-t5 (Albedo/Normal/Metallic/Roughness/Occlusion/Emissive)
        ///   [3] Static Sampler  : s0
        /// </summary>
        public static PipelineStateObject CreatePBR(
            RHIDevice device,
            RHIShaderBytecode vertexShader,
            RHIShaderBytecode pixelShader,
            string name = "PBR_PSO",
            RHIRasterizerDesc? rasterizer = null,
            RHIFormat dsvFormat = RHIFormat.D32_Float,
            RHIFormat[]? rtvFormats = null)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (!vertexShader.IsValid) throw new ArgumentException("顶点着色器无效", nameof(vertexShader));
            if (!pixelShader.IsValid) throw new ArgumentException("像素着色器无效", nameof(pixelShader));

            // ── Root Signature: WorldConstants(b0) + MaterialConstants(b1) + 6x SRV Table + StaticSampler ──
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.Constants,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Constants = new RHIRootConstants
                        {
                            Num32BitValues = (int)WorldConstants.Num32BitValues,
                            ShaderRegister = 0,
                            RegisterSpace = 0
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.Constants,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        Constants = new RHIRootConstants
                        {
                            Num32BitValues = (int)MaterialConstants.Num32BitValues,
                            ShaderRegister = 1,
                            RegisterSpace = 0
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 1,
                                    BaseShaderRegister = 0,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 1,
                                    BaseShaderRegister = 1,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 1,
                                    BaseShaderRegister = 2,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 1,
                                    BaseShaderRegister = 3,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 1,
                                    BaseShaderRegister = 4,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 1,
                                    BaseShaderRegister = 5,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    }
                },
                StaticSamplers = new[]
                {
                    new RHISamplerDesc
                    {
                        Filter = RHIFilter.Anisotropic,
                        AddressU = RHITextureAddressMode.Wrap,
                        AddressV = RHITextureAddressMode.Wrap,
                        AddressW = RHITextureAddressMode.Wrap,
                        MaxAnisotropy = 8,
                        ComparisonFunc = RHIComparison.Never,
                        MinLOD = 0,
                        MaxLOD = float.MaxValue,
                        ShaderRegister = 0,
                        RegisterSpace = 0,
                        ShaderVisibility = RHIShaderVisibility.Pixel
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };

            var rootSignature = device.CreateRootSignature(rootSigDesc, $"{name}_RootSig");

            // ── PSO ──
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = name,
                VS = vertexShader,
                PS = pixelShader,
                InputLayout = PBRInputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = RHIDepthDesc.Default,
                RasterizerState = rasterizer ?? RHIRasterizerDesc.NoCull,
                RTVFormats = rtvFormats ?? new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = dsvFormat,
                SampleCount = 1,
                RootSignature = rootSignature
            };

            var pipelineState = device.CreateGraphicsPipelineState(psoDesc, name);

            // 使用反射构造返回（RootSignature/PipelineState 为 readonly，通过此方式绕开）
            return new PipelineStateObject(rootSignature, pipelineState);
        }

        /// <summary>内部构造：从已创建的根签名和 PSO 构建（用于 CreatePBR 工厂方法）。</summary>
        private PipelineStateObject(RHIRootSignature rootSignature, RHIPipelineState pipelineState)
        {
            RootSignature = rootSignature;
            PipelineState = pipelineState;
        }

        public void Dispose()
        {
            PipelineState?.Dispose();
            RootSignature?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════
    // ConstantBuffer<T> — 通用常量缓冲封装
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 通用常量缓冲封装。创建 Upload 堆缓冲区并支持更新数据。
    /// 注意：SceneRenderer 使用 Root Constants 而非此常量缓冲，
    /// 此类作为基础设施保留，供后续需要 CBV 绑定的着色器使用。
    /// </summary>
    /// <typeparam name="T">常量数据结构类型（必须是 blittable 结构体）</typeparam>
    public sealed class ConstantBuffer<T> : IDisposable where T : struct
    {
        /// <summary>GPU 缓冲区</summary>
        public RHIBuffer Buffer { get; private set; }

        /// <summary>数据大小（字节）</summary>
        public uint DataSize { get; }

        /// <summary>缓冲区大小（字节，256 字节对齐，D3D12 CBV 要求）</summary>
        public uint BufferSize { get; }

        private readonly RHIDevice _device;

        /// <summary>
        /// 创建常量缓冲。在 Upload 堆上分配，大小按 256 字节对齐。
        /// </summary>
        public ConstantBuffer(RHIDevice device, string name = "")
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            DataSize = (uint)Marshal.SizeOf<T>();
            // D3D12 常量缓冲视图要求大小 256 字节对齐
            BufferSize = (DataSize + 255u) & ~255u;

            var desc = new RHIBufferDesc
            {
                SizeInBytes = BufferSize,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            };

            Buffer = device.CreateBuffer(desc, $"{name}_CB");
            Buffer.CurrentState = RHIResourceState.VertexAndConstantBuffer;
        }

        /// <summary>
        /// 更新常量缓冲数据（CPU 写入 → GPU 读取）。
        /// </summary>
        public void Update(in T data)
        {
            _device.UploadToBuffer(Buffer, new[] { data });
        }

        /// <summary>
        /// 创建 CBV 描述符。
        /// </summary>
        public RHIDescriptorHandle CreateCBV()
        {
            return _device.CreateCBV(Buffer, 0, BufferSize);
        }

        public void Dispose()
        {
            Buffer?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════
    // RenderItem — 可渲染项
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 可渲染项。包含网格资源、世界变换矩阵和材质颜色。
    /// </summary>
    public sealed class RenderItem
    {
        /// <summary>网格资源（顶点+索引缓冲）</summary>
        public MeshRenderResource Mesh { get; set; }

        /// <summary>世界变换矩阵</summary>
        public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;

        /// <summary>材质基础颜色（乘到顶点颜色上）</summary>
        public Vector4 MaterialColor { get; set; } = Vector4.One;

        /// <summary>材质资产（带纹理的 PBR 材质，可选）</summary>
        public MaterialAsset? Material { get; set; }

        /// <summary>是否可见</summary>
        public bool Visible { get; set; } = true;

        /// <summary>调试名称</summary>
        public string Name { get; set; } = "";

        public RenderItem(MeshRenderResource mesh, string name = "")
        {
            Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
            Name = name;
        }

        /// <summary>
        /// 创建一个带有位置和缩放的 RenderItem。
        /// </summary>
        public RenderItem(MeshRenderResource mesh, Vector3 position, Vector3 scale, Vector4 color, string name = "")
            : this(mesh, name)
        {
            WorldMatrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position);
            MaterialColor = color;
        }

        /// <summary>
        /// 创建使用 MeshGpuResource 的 RenderItem（自动转换）。
        /// </summary>
        public RenderItem(MeshGpuResource mesh, Vector3 position, Vector3 scale, Vector4 color, string name = "")
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            Name = name;
            MeshGpu = mesh;
            WorldMatrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position);
            MaterialColor = color;
        }

        /// <summary>
        /// 创建使用 MeshGpuResource 和 PBR 材质的 RenderItem。
        /// </summary>
        public RenderItem(MeshGpuResource mesh, Vector3 position, Vector3 scale, MaterialAsset material, string name = "")
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (material == null) throw new ArgumentNullException(nameof(material));
            Name = name;
            MeshGpu = mesh;
            Material = material;
            WorldMatrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position);
            MaterialColor = material.Properties.AlbedoColor;
        }

        /// <summary>可选：MeshGpuResource 引用（用于 SimpleSceneRenderer）</summary>
        internal MeshGpuResource? MeshGpu { get; }
    }

    // ══════════════════════════════════════════════════════════════
    // SceneRenderer — 场景渲染器（核心，使用 Root Constants）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 场景渲染器。管理深度缓冲、PSO、根签名和渲染项列表，
    /// 执行完整的帧渲染流程：BeginFrame → Clear → RenderScene → EndFrame → Present。
    ///
    /// 渲染流程：
    ///   1. 等待 Fence（限制 FramesInFlight 帧在飞）
    ///   2. 重置命令列表
    ///   3. 过渡后缓冲 → RenderTarget
    ///   4. 清屏 + 清深度
    ///   5. 设置 RTV + DSV + 视口 + 裁剪
    ///   6. 设置 RootSignature + PSO + 拓扑
    ///   7. 遍历 RenderItem：设置 Root Constants + 绑定 VB/IB + DrawIndexed
    ///   8. 过渡后缓冲 → Present
    ///   9. 关闭 + 执行 + 信号 + Present
    /// </summary>
    public sealed class SceneRenderer : IDisposable
    {
        // ── 依赖（外部创建，不拥有） ──
        private readonly RHIDevice _device;
        private readonly RHISwapChain _swapChain;
        private readonly RHICommandQueue _graphicsQueue;
        private readonly RHIFence _frameFence;
        private readonly RHICommandList _commandList;

        // ── 渲染资源（内部创建，拥有） ──
        private RHITexture? _depthTexture;
        private RHIDescriptorHandle _dsvHandle;
        private PipelineStateCache? _pipeline;

        // ── 渲染项列表 ──
        private readonly List<RenderItem> _renderItems = new();

        // ── 光照参数 ──
        /// <summary>方向光方向（从光源指向场景）</summary>
        public Vector3 LightDirection { get; set; } = new Vector3(-0.5f, -1.0f, -0.5f);

        /// <summary>方向光颜色</summary>
        public Vector3 LightColor { get; set; } = new Vector3(0.8f, 0.8f, 0.75f);

        /// <summary>环境光颜色</summary>
        public Vector3 AmbientColor { get; set; } = new Vector3(0.25f, 0.25f, 0.3f);

        // ── 清屏颜色 ──
        /// <summary>背景清屏颜色</summary>
        public Vector4 ClearColor { get; set; } = new Vector4(0.1f, 0.1f, 0.12f, 1.0f);

        // ── 帧同步 ──
        private ulong _fenceValue;
        private const int FramesInFlight = 2;

        // ── 状态 ──
        private bool _initialized;
        private bool _disposed;

        /// <summary>是否已初始化</summary>
        public bool IsInitialized => _initialized;

        /// <summary>渲染项数量</summary>
        public int RenderItemCount => _renderItems.Count;

        // ──────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 创建场景渲染器。
        /// 所有 RHI 资源由外部创建并传入（EngineApplication 拥有它们的生命周期）。
        /// </summary>
        public SceneRenderer(
            RHIDevice device,
            RHISwapChain swapChain,
            RHICommandQueue graphicsQueue,
            RHIFence frameFence,
            RHICommandList commandList)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _swapChain = swapChain ?? throw new ArgumentNullException(nameof(swapChain));
            _graphicsQueue = graphicsQueue ?? throw new ArgumentNullException(nameof(graphicsQueue));
            _frameFence = frameFence ?? throw new ArgumentNullException(nameof(frameFence));
            _commandList = commandList ?? throw new ArgumentNullException(nameof(commandList));
        }

        // ──────────────────────────────────────────────────────────
        // 初始化
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化渲染器：编译着色器、创建 Root Signature + PSO、创建深度缓冲。
        /// </summary>
        /// <param name="debugShaders">是否启用着色器调试信息</param>
        public void Initialize(bool debugShaders = false)
        {
            if (_initialized) return;

            // ── 1. 编译着色器 ──
            Console.WriteLine("[SceneRenderer] 编译内置着色器...");
            var shaders = ShaderCompileResult.CompileBuiltin(_device, debugShaders);
            if (!shaders.IsValid)
                throw new InvalidOperationException("内置着色器编译失败");
            Console.WriteLine("[SceneRenderer] 着色器编译成功 (SM_5_1)");

            // ── 2. 创建管线状态（Root Signature + PSO） ──
            Console.WriteLine("[SceneRenderer] 创建管线状态...");
            _pipeline = new PipelineStateCache(_device, shaders);
            Console.WriteLine("[SceneRenderer] PSO 创建成功");

            // ── 3. 创建深度缓冲 ──
            CreateDepthBuffer(_swapChain.Width, _swapChain.Height);

            _initialized = true;
            Console.WriteLine($"[SceneRenderer] 初始化完成 (Depth: {_swapChain.Width}x{_swapChain.Height})");
        }

        // ──────────────────────────────────────────────────────────
        // 深度缓冲管理
        // ──────────────────────────────────────────────────────────

        private void CreateDepthBuffer(int width, int height)
        {
            // 释放旧的深度缓冲
            _depthTexture?.Dispose();

            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.DepthStencil,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };

            _depthTexture = _device.CreateTexture(depthDesc, "DepthBuffer");
            _depthTexture.CurrentState = RHIResourceState.DepthWrite;

            // 创建 DSV 描述符
            _dsvHandle = _device.CreateDSV(_depthTexture);
        }

        /// <summary>
        /// 窗口尺寸变化时重建深度缓冲。
        /// </summary>
        public void OnResize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            // 等待 GPU 空闲
            _graphicsQueue.Flush();

            // 重建深度缓冲
            CreateDepthBuffer(width, height);
            Console.WriteLine($"[SceneRenderer] 深度缓冲已重建: {width}x{height}");
        }

        // ──────────────────────────────────────────────────────────
        // 渲染项管理
        // ──────────────────────────────────────────────────────────

        /// <summary>添加渲染项</summary>
        public void AddRenderItem(RenderItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            _renderItems.Add(item);
        }

        /// <summary>清空渲染项列表</summary>
        public void ClearRenderItems()
        {
            _renderItems.Clear();
        }

        /// <summary>按名称移除渲染项（匹配首个）。</summary>
        public bool RemoveRenderItemByName(string name)
        {
            for (int i = 0; i < _renderItems.Count; i++)
            {
                if (_renderItems[i].Name == name)
                {
                    _renderItems.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 清除所有非默认的渲染项，保留默认测试网格（TestCube、GroundPlane）。
        /// 被移除的 MeshGpuResource 将自动释放。
        /// </summary>
        public void ClearRenderItemsExceptDefault()
        {
            for (int i = _renderItems.Count - 1; i >= 0; i--)
            {
                var name = _renderItems[i].Name;
                if (name != "TestCube" && name != "GroundPlane")
                {
                    _renderItems[i].MeshGpu?.Dispose();
                    _renderItems.RemoveAt(i);
                }
            }
        }

        /// <summary>获取渲染项列表（只读）</summary>
        public IReadOnlyList<RenderItem> GetRenderItems() => _renderItems;

        // ──────────────────────────────────────────────────────────
        // 帧渲染（核心）
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 渲染一帧。完整流程：等待 → 重置 → 清屏 → 渲染 → 关闭 → 执行 → 信号 → Present。
        /// </summary>
        /// <param name="view">相机视图矩阵（行主序，内部会 Transpose）</param>
        /// <param name="projection">投影矩阵（行主序，内部会 Transpose）</param>
        /// <param name="time">运行时间（秒）</param>
        /// <param name="vsync">是否启用垂直同步</param>
        public void RenderFrame(in Matrix4x4 view, in Matrix4x4 projection, float time, bool vsync)
        {
            if (!_initialized || _pipeline == null || _depthTexture == null)
                return;

            try
            {
                // ── 1. 帧同步：限制 FramesInFlight 帧在飞 ──
                _fenceValue++;
                ulong waitValue = _fenceValue > FramesInFlight ? _fenceValue - FramesInFlight : 0;
                if (waitValue > 0)
                {
                    _frameFence.Wait(waitValue);
                }

                // ── 2. 获取后缓冲 ──
                int bbIndex = _swapChain.CurrentBackBufferIndex;
                var backBuffer = _swapChain.GetBackBuffer(bbIndex);
                var rtvHandle = _swapChain.GetBackBufferRTV(bbIndex);

                // ── 3. 重置命令列表 ──
                _commandList.Reset();

                // ── 4. 过渡后缓冲 → RenderTarget ──
                _commandList.TransitionTo(backBuffer, RHIResourceState.RenderTarget);

                // ── 5. 清屏 + 清深度 ──
                _commandList.ClearRenderTargetView(rtvHandle, ClearColor);
                _commandList.ClearDepthStencilView(_dsvHandle, 1.0f, 0);

                // ── 6. 设置渲染目标 + 视口 + 裁剪 ──
                _commandList.OMSetRenderTargets(new[] { rtvHandle }, _dsvHandle);
                _commandList.RSSetViewport(0, 0, _swapChain.Width, _swapChain.Height, 0, 1);
                _commandList.RSSetScissorRect(0, 0, _swapChain.Width, _swapChain.Height);

                // ── 7. 设置管线状态 ──
                _commandList.SetGraphicsRootSignature(_pipeline.RootSignature);
                _commandList.SetPipelineState(_pipeline.PipelineState);
                _commandList.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

                // ── 8. 预构建常量数据（共享部分） ──
                // 矩阵 Transpose：System.Numerics 行主序 → HLSL 列主序
                var constants = new WorldConstants
                {
                    View = Matrix4x4.Transpose(view),
                    Projection = Matrix4x4.Transpose(projection),
                    LightDirection = LightDirection,
                    LightColor = LightColor,
                    AmbientColor = AmbientColor,
                    Time = time
                };

                // ── 9. 遍历渲染项，逐个绘制 ──
                foreach (var item in _renderItems)
                {
                    if (!item.Visible || item.Mesh == null) continue;

                    // 设置该项的 World 矩阵（Transpose 后传入）
                    constants.World = Matrix4x4.Transpose(item.WorldMatrix);

                    // 通过 Root Constants 上传常量（60 dwords → b0）
                    _commandList.SetGraphicsRoot32BitConstants(
                        0,                              // rootParameterIndex
                        WorldConstants.Num32BitValues,  // num32BitValues = 60
                        in constants,                   // data
                        0);                             // destOffsetIn32BitValues

                    // 绑定顶点缓冲
                    _commandList.IASetVertexBuffers(
                        0,                                  // startSlot
                        item.Mesh.VertexBuffer,             // buffer
                        0,                                  // offset
                        item.Mesh.VertexStride,             // stride (48 bytes)
                        item.Mesh.VertexBufferSizeBytes);   // sizeInBytes

                    // 绑定索引缓冲
                    _commandList.IASetIndexBuffer(
                        item.Mesh.IndexBuffer,              // buffer
                        0,                                  // offset
                        item.Mesh.IndexBufferSizeBytes,     // sizeInBytes
                        RHIFormat.R32_UInt);                // format (uint32 indices)

                    // 绘制
                    _commandList.DrawIndexedInstanced(
                        item.Mesh.IndexCount,   // indexCountPerInstance
                        1,                      // instanceCount
                        0,                      // startIndex
                        0,                      // baseVertex
                        0);                     // startInstance
                }

                // ── 10. 过渡后缓冲 → Present ──
                _commandList.TransitionTo(backBuffer, RHIResourceState.Present);

                // ── 11. 关闭命令列表 ──
                _commandList.Close();

                // ── 12. 执行命令列表 ──
                _graphicsQueue.ExecuteCommandLists(_commandList);

                // ── 13. 信号 Fence ──
                _graphicsQueue.Signal(_frameFence, _fenceValue);

                // ── 14. Present ──
                _swapChain.Present(vsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SceneRenderer] 渲染帧失败: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────
        // 释放
        // ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 等待 GPU 完成
            try { _graphicsQueue.Flush(); } catch { }

            // 释放内部资源（不释放外部传入的资源）
            _depthTexture?.Dispose();
            _pipeline?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════
    // SimpleSceneRenderer — 简化版核心渲染器
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 简化版核心渲染器。
    /// 提供完整的 D3D12 渲染管线：编译着色器 → 创建PSO → 创建深度模板 → 上传网格 → 渲染。
    /// 使用 Root Constants (60 dwords, b0) 传递 WorldConstants。
    ///
    /// 渲染流程：
    ///   1. Init: 编译着色器 → 创建 PipelineStateObject → 创建深度模板
    ///   2. AddMesh: 上传网格到 GPU（使用 MeshGpuResource）
    ///   3. Render:
    ///      a. ResourceBarrier（后缓冲 → RenderTarget）
    ///      b. ClearRTV + ClearDSV
    ///      c. SetPipelineState + SetRootSignature
    ///      d. SetGraphicsRoot32BitConstants
    ///      e. IASetVertexBuffers + IASetIndexBuffer
    ///      f. DrawIndexedInstanced（每个 RenderItem）
    ///      g. ResourceBarrier（RenderTarget → Present）
    ///      h. Execute + Present
    /// </summary>
    public sealed class SimpleSceneRenderer : IDisposable
    {
        // ── 依赖（外部创建，不拥有） ──
        private readonly RHIDevice _device;
        private readonly RHISwapChain _swapChain;
        private readonly RHICommandQueue _graphicsQueue;
        private readonly RHIFence _frameFence;
        private readonly RHICommandList _commandList;

        // ── 渲染资源（内部创建，拥有） ──
        private RHITexture? _depthStencil;
        private RHIDescriptorHandle _dsvHandle;
        private PipelineStateObject? _pso;        // 基础 PSO（无材质）
        private PipelineStateObject? _pbrPso;     // PBR PSO（带纹理材质）

        // ── 网格缓存（按路径缓存的 GPU 网格资源） ──
        private readonly Dictionary<string, MeshGpuResource> _meshes = new();

        // ── 渲染项列表 ──
        private readonly List<RenderItem> _renderItems = new();

        // ── 光照参数 ──
        /// <summary>方向光方向（从光源指向场景）</summary>
        public Vector3 LightDirection { get; set; } = new Vector3(-0.5f, -1.0f, -0.5f);

        /// <summary>方向光颜色</summary>
        public Vector3 LightColor { get; set; } = new Vector3(0.9f, 0.85f, 0.7f);

        /// <summary>环境光颜色</summary>
        public Vector3 AmbientColor { get; set; } = new Vector3(0.2f, 0.2f, 0.25f);

        /// <summary>清屏颜色</summary>
        public Vector4 ClearColor { get; set; } = new Vector4(0.08f, 0.08f, 0.12f, 1.0f);

        // ── 帧同步 ──
        private ulong _fenceValue;
        private const int FramesInFlight = 2;

        // ── 状态 ──
        private bool _initialized;
        private bool _disposed;

        /// <summary>是否已初始化</summary>
        public bool IsInitialized => _initialized;

        /// <summary>渲染项数量</summary>
        public int RenderItemCount => _renderItems.Count;

        /// <summary>获取当前渲染项列表（只读）。</summary>
        public IReadOnlyList<RenderItem> GetRenderItems() => _renderItems;

        /// <summary>清除所有非默认的渲染项，保留默认测试网格（TestCube、GroundPlane）。</summary>
        public void ClearRenderItemsExceptDefault()
        {
            for (int i = _renderItems.Count - 1; i >= 0; i--)
            {
                var name = _renderItems[i].Name;
                if (name != "TestCube" && name != "GroundPlane")
                {
                    _renderItems[i].MeshGpu?.Dispose();
                    _renderItems.RemoveAt(i);
                }
            }
        }

        // ──────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────

        public SimpleSceneRenderer(
            RHIDevice device,
            RHISwapChain swapChain,
            RHICommandQueue graphicsQueue,
            RHIFence frameFence,
            RHICommandList commandList)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _swapChain = swapChain ?? throw new ArgumentNullException(nameof(swapChain));
            _graphicsQueue = graphicsQueue ?? throw new ArgumentNullException(nameof(graphicsQueue));
            _frameFence = frameFence ?? throw new ArgumentNullException(nameof(frameFence));
            _commandList = commandList ?? throw new ArgumentNullException(nameof(commandList));
        }

        // ──────────────────────────────────────────────────────────
        // Init — 编译着色器 → 创建 PSO → 创建深度模板
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化渲染器：编译 PBR 着色器 → 创建 PipelineStateObject → 创建深度模板缓冲。
        /// 优先尝试 ShaderCompiler（Vortice.D3DCompiler），失败则 fallback 到 BuiltinShaders。
        /// </summary>
        /// <param name="debugShaders">是否启用着色器调试信息</param>
        /// <param name="rtvFormats">自定义 RTV 格式（默认 R8G8B8A8_UNorm）</param>
        public void Init(bool debugShaders = false, RHIFormat[]? rtvFormats = null)
        {
            if (_initialized) return;

            try
            {
                // ── 1. 编译基础着色器 ──
                Console.WriteLine("[SimpleSceneRenderer] 编译基础着色器...");
                RHIShaderBytecode basicVs = BuiltinShaders.CompileBasicVertexShader(_device, debugShaders);
                RHIShaderBytecode basicPs = BuiltinShaders.CompileBasicPixelShader(_device, debugShaders);
                if (!basicVs.IsValid || !basicPs.IsValid)
                    throw new InvalidOperationException("基础着色器编译失败");

                // ── 2. 编译 PBR 着色器 ──
                Console.WriteLine("[SimpleSceneRenderer] 编译 PBR 着色器...");
                RHIShaderBytecode pbrVs = BuiltinShaders.CompilePBRVertexShader(_device, debugShaders);
                RHIShaderBytecode pbrPs = BuiltinShaders.CompilePBRPixelShader(_device, debugShaders);
                if (!pbrVs.IsValid || !pbrPs.IsValid)
                    throw new InvalidOperationException("PBR 着色器编译失败");

                // ── 3. 创建基础 PSO ──
                Console.WriteLine("[SimpleSceneRenderer] 创建基础 PSO...");
                _pso = new PipelineStateObject(_device, basicVs, basicPs, "SimpleSceneBasic_PSO", rtvFormats: rtvFormats);
                Console.WriteLine("[SimpleSceneRenderer] 基础 PSO 创建成功");

                // ── 4. 创建 PBR PSO ──
                Console.WriteLine("[SimpleSceneRenderer] 创建 PBR PSO...");
                _pbrPso = PipelineStateObject.CreatePBR(_device, pbrVs, pbrPs, "SimpleScenePBR_PSO", rtvFormats: rtvFormats);
                Console.WriteLine("[SimpleSceneRenderer] PBR PSO 创建成功");

                // ── 5. 创建深度模板 ──
                CreateDepthStencil(_swapChain.Width, _swapChain.Height);

                _initialized = true;
                Console.WriteLine($"[SimpleSceneRenderer] 初始化完成 (深度模板: {_swapChain.Width}x{_swapChain.Height})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimpleSceneRenderer] 初始化失败: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                throw;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 深度模板管理
        // ──────────────────────────────────────────────────────────

        private void CreateDepthStencil(int width, int height)
        {
            _depthStencil?.Dispose();

            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.DepthStencil,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };

            _depthStencil = _device.CreateTexture(depthDesc, "SimpleSceneDepth");
            _depthStencil.CurrentState = RHIResourceState.DepthWrite;

            _dsvHandle = _device.CreateDSV(_depthStencil);
        }

        /// <summary>窗口 resize 时重建深度模板</summary>
        public void OnResize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            _graphicsQueue.Flush();
            CreateDepthStencil(width, height);
            Console.WriteLine($"[SimpleSceneRenderer] 深度模板已重建: {width}x{height}");
        }

        // ──────────────────────────────────────────────────────────
        // AddMesh — 上传网格到 GPU
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 添加网格渲染项。将 CPU 网格数据上传到 GPU，创建 MeshGpuResource。
        /// </summary>
        /// <param name="mesh">程序化网格数据</param>
        /// <param name="position">世界空间位置</param>
        /// <param name="scale">缩放</param>
        /// <param name="color">顶点颜色</param>
        /// <param name="name">调试名称</param>
        public void AddMesh(ProceduralMesh mesh, Vector3 position, Vector3 scale, Vector4 color, string name = "")
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var gpuMesh = MeshGpuResource.FromProceduralMesh(_device, mesh, color, name);
            var item = new RenderItem(gpuMesh, position, scale, color, name);
            _renderItems.Add(item);
        }

        /// <summary>添加自定义 MeshGpuResource 渲染项</summary>
        public void AddMesh(MeshGpuResource gpuMesh, Vector3 position, Vector3 scale, Vector4 color, string name = "")
        {
            if (gpuMesh == null) throw new ArgumentNullException(nameof(gpuMesh));
            var item = new RenderItem(gpuMesh, position, scale, color, name);
            _renderItems.Add(item);
        }

        /// <summary>添加 MeshRenderResource 渲染项</summary>
        public void AddMesh(MeshRenderResource mesh, Vector3 position, Vector3 scale, Vector4 color, string name = "")
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var item = new RenderItem(mesh, position, scale, color, name);
            _renderItems.Add(item);
        }

        /// <summary>添加 RenderItem</summary>
        public void AddMesh(RenderItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            _renderItems.Add(item);
        }

        // ──────────────────────────────────────────────────────────
        // UploadMesh — 将 EditableMesh 上传到 GPU 并缓存
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 将 <see cref="EditableMesh"/> 上传到 GPU，创建并缓存 <see cref="MeshGpuResource"/>。
        /// 返回的 GPU 资源可直接用于 <see cref="AddMesh(MeshGpuResource, Vector3, Vector3, Vector4, string)"/>。
        /// </summary>
        /// <param name="meshPath">网格路径（作为缓存键）</param>
        /// <param name="mesh">可编辑网格数据</param>
        /// <returns>上传后的 GPU 网格资源</returns>
        public MeshGpuResource UploadMesh(string meshPath, EditableMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (string.IsNullOrEmpty(meshPath)) throw new ArgumentException("meshPath 不能为空", nameof(meshPath));

            // 如果已存在，先释放旧资源
            if (_meshes.TryGetValue(meshPath, out var existing))
            {
                existing.Dispose();
                _meshes.Remove(meshPath);
            }

            // 转换顶点格式
            int vertexCount = mesh.Vertices.Count;
            var vertices = new RenderVertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var pos = mesh.Vertices[i];
                var nrm = i < mesh.Normals.Count ? mesh.Normals[i] : Vector3.UnitY;
                var uv  = i < mesh.UV0.Count ? mesh.UV0[i] : Vector2.Zero;
                var col = i < mesh.Colors.Count ? mesh.Colors[i] : Vector4.One;
                vertices[i] = new RenderVertex(pos, nrm, uv, col);
            }

            // 转换面为索引（只处理三角形；四边形拆分为两个三角形）
            var indices = new List<uint>();
            foreach (var face in mesh.Faces)
            {
                if (face.Indices.Count == 3)
                {
                    indices.Add((uint)face.Indices[0]);
                    indices.Add((uint)face.Indices[1]);
                    indices.Add((uint)face.Indices[2]);
                }
                else if (face.Indices.Count == 4)
                {
                    indices.Add((uint)face.Indices[0]);
                    indices.Add((uint)face.Indices[1]);
                    indices.Add((uint)face.Indices[2]);
                    indices.Add((uint)face.Indices[0]);
                    indices.Add((uint)face.Indices[2]);
                    indices.Add((uint)face.Indices[3]);
                }
                else if (face.Indices.Count > 4)
                {
                    // 简单扇形三角化（凸多边形假设）
                    for (int i = 2; i < face.Indices.Count; i++)
                    {
                        indices.Add((uint)face.Indices[0]);
                        indices.Add((uint)face.Indices[i - 1]);
                        indices.Add((uint)face.Indices[i]);
                    }
                }
            }

            var gpuMesh = new MeshGpuResource(_device, vertices, indices.ToArray(), meshPath);
            _meshes[meshPath] = gpuMesh;

            Console.WriteLine($"[SimpleSceneRenderer] 已上传网格 '{meshPath}': {vertexCount} 顶点, {indices.Count / 3} 三角形");
            return gpuMesh;
        }

        // ──────────────────────────────────────────────────────────
        // RenderToTexture — 渲染到指定目标（供后处理流程使用）
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 渲染场景到指定的颜色和深度目标。
        /// 不做 Present / Fence 同步，调用者负责管理 commandList 生命周期。
        /// </summary>
        public void RenderToTexture(
            RHIDescriptorHandle colorRtv,
            RHIDescriptorHandle depthDsv,
            int width,
            int height,
            in Matrix4x4 view,
            in Matrix4x4 projection,
            float time,
            RHICommandList? commandList = null)
        {
            if (!_initialized || _pso == null) return;

            var cmd = commandList ?? _commandList;

            // 清屏 + 清深度
            cmd.ClearRenderTargetView(colorRtv, ClearColor);
            cmd.ClearDepthStencilView(depthDsv, 1.0f, 0);

            // 设置渲染目标 + 视口 + 裁剪
            cmd.OMSetRenderTargets(new[] { colorRtv }, depthDsv);
            cmd.RSSetViewport(0, 0, width, height, 0, 1);
            cmd.RSSetScissorRect(0, 0, width, height);

            // 预构建共享常量数据
            var constants = new WorldConstants
            {
                View = Matrix4x4.Transpose(view),
                Projection = Matrix4x4.Transpose(projection),
                LightDirection = LightDirection,
                LightColor = LightColor,
                AmbientColor = AmbientColor,
                Time = time
            };

            // 当前激活的 PSO 引用
            PipelineStateObject? currentPso = null;

            // 遍历 RenderItem，DrawIndexedInstanced
            foreach (var item in _renderItems)
            {
                if (!item.Visible) continue;

                bool usePbr = item.Material != null && _pbrPso != null;
                var activePso = usePbr ? _pbrPso! : _pso!;

                if (currentPso != activePso)
                {
                    cmd.SetGraphicsRootSignature(activePso.RootSignature);
                    cmd.SetPipelineState(activePso.PipelineState);
                    cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
                    currentPso = activePso;
                }

                constants.World = Matrix4x4.Transpose(item.WorldMatrix);
                cmd.SetGraphicsRoot32BitConstants(0, WorldConstants.Num32BitValues, in constants, 0);

                if (usePbr)
                {
                    var mat = item.Material!;
                    var matConstants = new MaterialConstants
                    {
                        AlbedoColor = mat.Properties.AlbedoColor,
                        EmissiveColor = mat.Properties.EmissiveColor,
                        EmissiveIntensity = mat.Properties.EmissiveIntensity,
                        Metallic = mat.Properties.Metallic,
                        Roughness = mat.Properties.Roughness,
                        NormalScale = mat.Properties.NormalScale,
                        AlphaCutoff = mat.Properties.AlphaCutoff,
                        TextureFlags = mat.ComputeTextureFlags()
                    };
                    cmd.SetGraphicsRoot32BitConstants(1, MaterialConstants.Num32BitValues, in matConstants, 0);

                    BindMaterialTexture(cmd, 2, mat, MaterialTextureType.Albedo);
                    BindMaterialTexture(cmd, 3, mat, MaterialTextureType.Normal);
                    BindMaterialTexture(cmd, 4, mat, MaterialTextureType.Metallic);
                    BindMaterialTexture(cmd, 5, mat, MaterialTextureType.Roughness);
                    BindMaterialTexture(cmd, 6, mat, MaterialTextureType.Occlusion);
                    BindMaterialTexture(cmd, 7, mat, MaterialTextureType.Emissive);
                }

                if (item.MeshGpu != null)
                {
                    var m = item.MeshGpu;
                    cmd.IASetVertexBuffers(0, m.VertexBuffer, 0, m.VertexStride, m.VertexBufferSize);
                    cmd.IASetIndexBuffer(m.IndexBuffer, 0, m.IndexBufferSize, RHIFormat.R32_UInt);
                    cmd.DrawIndexedInstanced(m.IndexCount, 1, 0, 0, 0);
                }
                else if (item.Mesh != null)
                {
                    var m = item.Mesh;
                    cmd.IASetVertexBuffers(0, m.VertexBuffer, 0, m.VertexStride, m.VertexBufferSizeBytes);
                    cmd.IASetIndexBuffer(m.IndexBuffer, 0, m.IndexBufferSizeBytes, RHIFormat.R32_UInt);
                    cmd.DrawIndexedInstanced(m.IndexCount, 1, 0, 0, 0);
                }
            }
        }

        // ──────────────────────────────────────────────────────────
        // Render — 完整帧渲染流程
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 渲染一帧。
        /// 流程：ResourceBarrier → ClearRTV → ClearDSV → SetPipelineState → SetRootSignature →
        ///       SetGraphicsRoot32BitConstants → IASetVertexBuffers → IASetIndexBuffer →
        ///       DrawIndexedInstanced（每 item） → ResourceBarrier(Present) → Execute → Present
        /// </summary>
        /// <param name="view">相机视图矩阵</param>
        /// <param name="projection">投影矩阵</param>
        /// <param name="time">运行时间</param>
        /// <param name="vsync">是否垂直同步</param>
        public void Render(in Matrix4x4 view, in Matrix4x4 projection, float time, bool vsync)
        {
            if (!_initialized || _pso == null || _depthStencil == null)
                return;

            try
            {
                // ── 帧同步 ──
                _fenceValue++;
                ulong waitValue = _fenceValue > FramesInFlight ? _fenceValue - FramesInFlight : 0;
                if (waitValue > 0)
                    _frameFence.Wait(waitValue);

                // ── 获取后缓冲 ──
                int bbIndex = _swapChain.CurrentBackBufferIndex;
                var backBuffer = _swapChain.GetBackBuffer(bbIndex);
                var rtvHandle = _swapChain.GetBackBufferRTV(bbIndex);

                // ── 重置命令列表 ──
                _commandList.Reset();

                // ── ResourceBarrier: 后缓冲 → RenderTarget ──
                _commandList.TransitionTo(backBuffer, RHIResourceState.RenderTarget);

                // ── ClearRTV + ClearDSV ──
                _commandList.ClearRenderTargetView(rtvHandle, ClearColor);
                _commandList.ClearDepthStencilView(_dsvHandle, 1.0f, 0);

                // ── 设置渲染目标 + 视口 + 裁剪 ──
                _commandList.OMSetRenderTargets(new[] { rtvHandle }, _dsvHandle);
                _commandList.RSSetViewport(0, 0, _swapChain.Width, _swapChain.Height, 0, 1);
                _commandList.RSSetScissorRect(0, 0, _swapChain.Width, _swapChain.Height);

                // ── 预构建共享常量数据 ──
                var constants = new WorldConstants
                {
                    View = Matrix4x4.Transpose(view),
                    Projection = Matrix4x4.Transpose(projection),
                    LightDirection = LightDirection,
                    LightColor = LightColor,
                    AmbientColor = AmbientColor,
                    Time = time
                };

                // ── 当前激活的 PSO 引用（用于避免重复绑定） ──
                PipelineStateObject? currentPso = null;

                // ── 遍历 RenderItem，DrawIndexedInstanced ──
                foreach (var item in _renderItems)
                {
                    if (!item.Visible) continue;

                    // 根据是否有材质选择 PSO
                    bool usePbr = item.Material != null && _pbrPso != null;
                    var activePso = usePbr ? _pbrPso! : _pso!;

                    // 切换 PSO / RootSignature（仅在变化时）
                    if (currentPso != activePso)
                    {
                        _commandList.SetGraphicsRootSignature(activePso.RootSignature);
                        _commandList.SetPipelineState(activePso.PipelineState);
                        _commandList.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
                        currentPso = activePso;
                    }

                    // 更新 World 矩阵
                    constants.World = Matrix4x4.Transpose(item.WorldMatrix);
                    _commandList.SetGraphicsRoot32BitConstants(
                        0, WorldConstants.Num32BitValues, in constants, 0);

                    // ── 材质绑定（PBR 路径） ──
                    if (usePbr)
                    {
                        var mat = item.Material!;
                        var matConstants = new MaterialConstants
                        {
                            AlbedoColor = mat.Properties.AlbedoColor,
                            EmissiveColor = mat.Properties.EmissiveColor,
                            EmissiveIntensity = mat.Properties.EmissiveIntensity,
                            Metallic = mat.Properties.Metallic,
                            Roughness = mat.Properties.Roughness,
                            NormalScale = mat.Properties.NormalScale,
                            AlphaCutoff = mat.Properties.AlphaCutoff,
                            TextureFlags = mat.ComputeTextureFlags()
                        };
                        _commandList.SetGraphicsRoot32BitConstants(
                            1, MaterialConstants.Num32BitValues, in matConstants, 0);

                        // 绑定纹理 SRV（每个纹理独立 Descriptor Table）
                        BindMaterialTexture(_commandList, 2, mat, MaterialTextureType.Albedo);
                        BindMaterialTexture(_commandList, 3, mat, MaterialTextureType.Normal);
                        BindMaterialTexture(_commandList, 4, mat, MaterialTextureType.Metallic);
                        BindMaterialTexture(_commandList, 5, mat, MaterialTextureType.Roughness);
                        BindMaterialTexture(_commandList, 6, mat, MaterialTextureType.Occlusion);
                        BindMaterialTexture(_commandList, 7, mat, MaterialTextureType.Emissive);
                    }

                    // 根据网格资源类型绑定 VB/IB 并绘制
                    if (item.MeshGpu != null)
                    {
                        var m = item.MeshGpu;
                        _commandList.IASetVertexBuffers(0, m.VertexBuffer, 0, m.VertexStride, m.VertexBufferSize);
                        _commandList.IASetIndexBuffer(m.IndexBuffer, 0, m.IndexBufferSize, RHIFormat.R32_UInt);
                        _commandList.DrawIndexedInstanced(m.IndexCount, 1, 0, 0, 0);
                    }
                    else if (item.Mesh != null)
                    {
                        var m = item.Mesh;
                        _commandList.IASetVertexBuffers(0, m.VertexBuffer, 0, m.VertexStride, m.VertexBufferSizeBytes);
                        _commandList.IASetIndexBuffer(m.IndexBuffer, 0, m.IndexBufferSizeBytes, RHIFormat.R32_UInt);
                        _commandList.DrawIndexedInstanced(m.IndexCount, 1, 0, 0, 0);
                    }
                }

                // ── ResourceBarrier: RenderTarget → Present ──
                _commandList.TransitionTo(backBuffer, RHIResourceState.Present);

                // ── Close → Execute → Signal → Present ──
                _commandList.Close();
                _graphicsQueue.ExecuteCommandLists(_commandList);
                _graphicsQueue.Signal(_frameFence, _fenceValue);
                _swapChain.Present(vsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimpleSceneRenderer] 渲染失败: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────
        // 释放
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 辅助方法：绑定材质纹理到指定 Root Parameter。
        /// </summary>
        private static void BindMaterialTexture(RHICommandList cmd, uint rootParamIndex, MaterialAsset mat, MaterialTextureType type)
        {
            if (mat.Textures.TryGetValue(type, out var slot) && slot.SrvHandle.HasValue)
            {
                cmd.SetGraphicsRootDescriptorTable(rootParamIndex, slot.SrvHandle.Value);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _graphicsQueue.Flush(); } catch { }

            _depthStencil?.Dispose();
            _pso?.Dispose();
            _pbrPso?.Dispose();
        }
    }
}
