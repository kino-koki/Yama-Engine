// =============================================================================
// MeshRenderer.cs - 3D 网格渲染器（索引化 + CPU 端 MVP 变换）
// 支持立方体、球体、平面、圆柱体等基本图元的生成与渲染。
// 使用 passthrough SPIR-V 着色器（vec2 NDC pos + vec3 color），
// 顶点在 CPU 端由 Model * ViewProj 变换到 NDC 裁剪空间。
// 采用画家算法（按距离排序）处理遮挡关系。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.RHI.Vulkan;

namespace ShanyuEditor.Controls
{
    /// <summary>
    /// 3D 网格渲染器 —— 管理基本图元的生成与索引化渲染。
    /// 每帧将所有渲染项的顶点在 CPU 端变换到 NDC，合并为单个顶点/索引缓冲后一次绘制。
    /// </summary>
    public sealed class MeshRenderer : IDisposable
    {
        private readonly VulkanDevice _device;
        private RHIBuffer? _vertexBuffer;
        private RHIBuffer? _indexBuffer;
        private RHIPipelineState? _pipelineState;
        private RHIRootSignature? _rootSignature;
        private bool _disposed;
        private bool _initialized;

        // ── 顶点格式（与 passthrough 着色器一致） ──
        [StructLayout(LayoutKind.Sequential)]
        private struct MeshVertex
        {
            public float PosX, PosY;       // NDC 坐标
            public float ColorR, ColorG, ColorB;
        }

        // ── 本地空间顶点（变换前） ──
        [StructLayout(LayoutKind.Sequential)]
        private struct LocalVertex
        {
            public Vector3 Position;
            public Vector3 Color;
        }

        // ── 网格数据 ──
        private sealed class MeshData
        {
            public LocalVertex[] Vertices = Array.Empty<LocalVertex>();
            public uint[] Indices = Array.Empty<uint>();
        }

        // ── 渲染项 ──
        private readonly struct RenderItem
        {
            public readonly int MeshId;
            public readonly Matrix4x4 Model;
            public readonly Vector3 TintColor;

            public RenderItem(int meshId, Matrix4x4 model, Vector3 tint)
            {
                MeshId = meshId;
                Model = model;
                TintColor = tint;
            }
        }

        // ── 网格注册表 ──
        private readonly List<MeshData> _meshes = new();
        private readonly Dictionary<string, int> _meshNameToId = new();

        // ── 当前帧渲染项 ──
        private readonly List<RenderItem> _renderItems = new();

        // ── 动态缓冲区容量 ──
        private int _maxVertices = 4096;
        private int _maxIndices = 8192;

        // ── 预编译 SPIR-V（passthrough 直通着色器） ──
        private static readonly uint[] VertexShaderSPIRV = {
            0x00020011, 0x00000001, 0x0007000B, 0x00000001, 0x4C534C47,
            0x6474732E, 0x3035342E, 0x00000000, 0x0003000E, 0x00000000,
            0x00000000, 0x0009000F, 0x00000000, 0x00000011, 0x6E69616D,
            0x00000000, 0x0000000B, 0x0000000C, 0x0000000D, 0x0000000E,
            0x00030002, 0x00000004, 0x000001C2, 0x00040005, 0x00000011,
            0x6E69616D, 0x00000000, 0x00030005, 0x0000000B, 0x00736F70,
            0x00040005, 0x0000000C, 0x6F6C6F63, 0x00000072, 0x00050005,
            0x0000000D, 0x67617266, 0x6F6C6F43, 0x00000072, 0x00050005,
            0x0000000E, 0x505F6C67, 0x7469736F, 0x006E6F69, 0x00040047,
            0x0000000B, 0x00000023, 0x00000000, 0x00040047, 0x0000000C,
            0x00000023, 0x00000001, 0x00040047, 0x0000000D, 0x00000023,
            0x00000000, 0x00040047, 0x0000000E, 0x00000025, 0x00000000,
            0x00020013, 0x00000001, 0x00030021, 0x00000002, 0x00000001,
            0x00030016, 0x00000003, 0x00000020, 0x00040017, 0x00000004,
            0x00000003, 0x00000002, 0x00040017, 0x00000005, 0x00000003,
            0x00000003, 0x00040017, 0x00000006, 0x00000003, 0x00000004,
            0x00040027, 0x00000007, 0x00000001, 0x00000004, 0x00040027,
            0x00000008, 0x00000001, 0x00000005, 0x00040027, 0x00000009,
            0x00000003, 0x00000005, 0x00040027, 0x0000000A, 0x00000003,
            0x00000006, 0x0004003B, 0x0000000B, 0x00000007, 0x00000001,
            0x0004003B, 0x0000000C, 0x00000008, 0x00000001, 0x0004003B,
            0x0000000D, 0x00000009, 0x00000003, 0x0004003B, 0x0000000E,
            0x0000000A, 0x00000003, 0x0004002B, 0x0000000F, 0x00000003,
            0x00000000, 0x0004002B, 0x00000010, 0x00000003, 0x3F800000,
            0x00050036, 0x00000011, 0x00000001, 0x00000000, 0x00000002,
            0x000200F7, 0x00000012, 0x0004003D, 0x00000013, 0x00000004,
            0x0000000B, 0x000500B3, 0x00000014, 0x00000003, 0x00000013,
            0x00000000, 0x000500B3, 0x00000015, 0x00000003, 0x00000013,
            0x00000001, 0x000700AF, 0x00000016, 0x00000006, 0x00000014,
            0x00000015, 0x0000000F, 0x00000010, 0x0003003E, 0x0000000E,
            0x00000016, 0x0004003D, 0x00000017, 0x00000005, 0x0000000C,
            0x0003003E, 0x0000000D, 0x00000017, 0x000100FD, 0x00010038
        };

        private static readonly uint[] FragmentShaderSPIRV = {
            0x00020011, 0x00000001, 0x0007000B, 0x00000001, 0x4C534C47,
            0x6474732E, 0x3035342E, 0x00000000, 0x0003000E, 0x00000000,
            0x00000000, 0x0007000F, 0x00000004, 0x0000000B, 0x6E69616D,
            0x00000000, 0x00000008, 0x00000009, 0x00030002, 0x00000004,
            0x000001C2, 0x00050005, 0x00000008, 0x67617266, 0x6F6C6F43,
            0x00000072, 0x00050005, 0x00000009, 0x4374756F, 0x726F6C6F,
            0x00000000, 0x00040005, 0x0000000B, 0x6E69616D, 0x00000000,
            0x00040047, 0x00000008, 0x00000023, 0x00000000, 0x00040047,
            0x00000009, 0x00000023, 0x00000000, 0x00020013, 0x00000001,
            0x00030021, 0x00000002, 0x00000001, 0x00030016, 0x00000003,
            0x00000020, 0x00040017, 0x00000004, 0x00000003, 0x00000003,
            0x00040017, 0x00000005, 0x00000003, 0x00000004, 0x00040027,
            0x00000006, 0x00000001, 0x00000004, 0x00040027, 0x00000007,
            0x00000003, 0x00000005, 0x0004003B, 0x00000008, 0x00000006,
            0x00000001, 0x0004003B, 0x00000009, 0x00000007, 0x00000003,
            0x0004002B, 0x0000000A, 0x00000003, 0x3F800000, 0x00050036,
            0x0000000B, 0x00000001, 0x00000000, 0x00000002, 0x000200F7,
            0x0000000C, 0x0004003D, 0x0000000D, 0x00000004, 0x00000008,
            0x000500AF, 0x0000000E, 0x00000005, 0x0000000D, 0x0000000A,
            0x0003003E, 0x00000009, 0x0000000E, 0x000100FD, 0x00010038
        };

        // ══════════════════════════════════════════════════════════
        //  属性
        // ══════════════════════════════════════════════════════════

        /// <summary>当前帧渲染项数量。</summary>
        public int RenderItemCount => _renderItems.Count;

        /// <summary>已注册的网格数量。</summary>
        public int MeshCount => _meshes.Count;

        // ══════════════════════════════════════════════════════════
        //  构造与初始化
        // ══════════════════════════════════════════════════════════

        public MeshRenderer(VulkanDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public void Initialize()
        {
            if (_initialized) return;
            try
            {
                CreatePipelineState();
                CreateDynamicBuffers();
                RegisterBuiltinMeshes();
                _initialized = true;
                Debug.WriteLine($"[MeshRenderer] 初始化完成 - 注册了 {_meshes.Count} 个基本图元");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MeshRenderer] 初始化失败: {ex.Message}");
            }
        }

        private void CreatePipelineState()
        {
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = Array.Empty<RHIRootParameter>(),
                StaticSamplers = Array.Empty<RHISamplerDesc>(),
                AllowInputAssembler = true
            };
            _rootSignature = _device.CreateRootSignature(rootSigDesc, "MeshRootSig");

            var vsShader = new RHIShaderBytecode(
                UintArrayToBytes(VertexShaderSPIRV), "main",
                RHIShaderStage.Vertex, "MeshVS");

            var psShader = new RHIShaderBytecode(
                UintArrayToBytes(FragmentShaderSPIRV), "main",
                RHIShaderStage.Pixel, "MeshPS");

            var inputLayout = new RHIInputElement[]
            {
                new RHIInputElement("POSITION", 0, RHIFormat.R32G32_Float, 0, 0),
                new RHIInputElement("COLOR", 0, RHIFormat.R32G32B32_Float, 0, 8),
            };

            var gfxDesc = new RHIGraphicsPipelineDesc
            {
                Name = "MeshPSO",
                VS = vsShader,
                PS = psShader,
                InputLayout = inputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.B8G8R8A8_UNorm },
                DSVFormat = RHIFormat.Unknown,
                SampleCount = 1,
                RootSignature = _rootSignature
            };

            _pipelineState = _device.CreateGraphicsPipelineState(gfxDesc, "MeshPSO");
            Debug.WriteLine("[MeshRenderer] PSO 创建完成 (TriangleList + Indexed)");
        }

        private void CreateDynamicBuffers()
        {
            int vertexSize = Marshal.SizeOf<MeshVertex>();
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_maxVertices * vertexSize),
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = (ulong)vertexSize,
                Format = RHIFormat.Unknown
            };
            _vertexBuffer = _device.CreateBuffer(vbDesc, "MeshDynamicVB");

            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_maxIndices * sizeof(uint)),
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer,
                Stride = (ulong)sizeof(uint),
                Format = RHIFormat.R32_UInt
            };
            _indexBuffer = _device.CreateBuffer(ibDesc, "MeshDynamicIB");
        }

        // ══════════════════════════════════════════════════════════
        //  内置图元注册
        // ══════════════════════════════════════════════════════════

        private void RegisterBuiltinMeshes()
        {
            RegisterCube("Cube", Vector3.One);
            RegisterCube("Cube_Small", new Vector3(0.5f));
            RegisterCube("Cube_Large", new Vector3(2.0f));
            RegisterSphere("Sphere", 0.5f, 16);
            RegisterPlane("Plane", new Vector2(5.0f, 5.0f));
            RegisterCylinder("Cylinder", 0.5f, 0.5f, 1.0f, 16);
        }

        /// <summary>注册一个立方体网格，返回网格 ID。</summary>
        public int RegisterCube(string name, Vector3 size)
        {
            var mesh = new MeshData();
            float hx = size.X * 0.5f, hy = size.Y * 0.5f, hz = size.Z * 0.5f;

            // 6 面 × 4 顶点 = 24 顶点
            mesh.Vertices = new LocalVertex[24];
            mesh.Indices = new uint[36];

            // 面定义：法线、4 个顶点（局部坐标）
            var faces = new (Vector3 normal, Vector3[] corners)[]
            {
                // +X
                (Vector3.UnitX, new[] { new Vector3(hx,-hy,-hz), new Vector3(hx,hy,-hz), new Vector3(hx,hy,hz), new Vector3(hx,-hy,hz) }),
                // -X
                (-Vector3.UnitX, new[] { new Vector3(-hx,-hy,hz), new Vector3(-hx,hy,hz), new Vector3(-hx,hy,-hz), new Vector3(-hx,-hy,-hz) }),
                // +Y
                (Vector3.UnitY, new[] { new Vector3(-hx,hy,-hz), new Vector3(hx,hy,-hz), new Vector3(hx,hy,hz), new Vector3(-hx,hy,hz) }),
                // -Y
                (-Vector3.UnitY, new[] { new Vector3(-hx,-hy,hz), new Vector3(hx,-hy,hz), new Vector3(hx,-hy,-hz), new Vector3(-hx,-hy,-hz) }),
                // +Z
                (Vector3.UnitZ, new[] { new Vector3(hx,-hy,hz), new Vector3(hx,hy,hz), new Vector3(-hx,hy,hz), new Vector3(-hx,-hy,hz) }),
                // -Z
                (-Vector3.UnitZ, new[] { new Vector3(-hx,-hy,-hz), new Vector3(-hx,hy,-hz), new Vector3(hx,hy,-hz), new Vector3(hx,-hy,-hz) }),
            };

            // 面颜色（各面略微不同色温，方便辨识）
            var faceColors = new Vector3[]
            {
                new(0.8f, 0.5f, 0.3f),  // +X 橙
                new(0.3f, 0.5f, 0.8f),  // -X 蓝
                new(0.8f, 0.8f, 0.3f),  // +Y 黄
                new(0.5f, 0.3f, 0.3f),  // -Y 暗红
                new(0.3f, 0.8f, 0.5f),  // +Z 绿
                new(0.5f, 0.3f, 0.8f),  // -Z 紫
            };

            for (int f = 0; f < 6; f++)
            {
                int baseIdx = f * 4;
                var color = faceColors[f];
                for (int v = 0; v < 4; v++)
                {
                    mesh.Vertices[baseIdx + v] = new LocalVertex
                    {
                        Position = faces[f].corners[v],
                        Color = color
                    };
                }

                // 两个三角形
                int idxBase = f * 6;
                mesh.Indices[idxBase + 0] = (uint)(baseIdx + 0);
                mesh.Indices[idxBase + 1] = (uint)(baseIdx + 1);
                mesh.Indices[idxBase + 2] = (uint)(baseIdx + 2);
                mesh.Indices[idxBase + 3] = (uint)(baseIdx + 0);
                mesh.Indices[idxBase + 4] = (uint)(baseIdx + 2);
                mesh.Indices[idxBase + 5] = (uint)(baseIdx + 3);
            }

            return RegisterMesh(name, mesh);
        }

        /// <summary>注册一个球体网格。</summary>
        public int RegisterSphere(string name, float radius, int segments)
        {
            var mesh = new MeshData();
            var verts = new List<LocalVertex>();
            var indices = new List<uint>();

            int rings = segments / 2;
            if (rings < 3) rings = 3;

            for (int ring = 0; ring <= rings; ring++)
            {
                float phi = MathF.PI * ring / rings;  // 0 → PI
                float y = MathF.Cos(phi) * radius;
                float r = MathF.Sin(phi) * radius;

                for (int seg = 0; seg <= segments; seg++)
                {
                    float theta = 2.0f * MathF.PI * seg / segments;
                    float x = r * MathF.Cos(theta);
                    float z = r * MathF.Sin(theta);

                    // 基于纬度的颜色渐变
                    float t = (float)ring / rings;
                    var color = new Vector3(
                        0.3f + 0.5f * t,
                        0.5f + 0.3f * (1 - t),
                        0.7f + 0.2f * MathF.Sin(theta));

                    verts.Add(new LocalVertex { Position = new Vector3(x, y, z), Color = color });
                }
            }

            // 生成索引
            for (int ring = 0; ring < rings; ring++)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    uint a = (uint)(ring * (segments + 1) + seg);
                    uint b = (uint)(a + 1);
                    uint c = (uint)(a + segments + 1);
                    uint d = (uint)(c + 1);

                    indices.Add(a); indices.Add(c); indices.Add(b);
                    indices.Add(b); indices.Add(c); indices.Add(d);
                }
            }

            mesh.Vertices = verts.ToArray();
            mesh.Indices = indices.ToArray();
            return RegisterMesh(name, mesh);
        }

        /// <summary>注册一个平面网格。</summary>
        public int RegisterPlane(string name, Vector2 size)
        {
            var mesh = new MeshData();
            float hx = size.X * 0.5f, hz = size.Y * 0.5f;

            mesh.Vertices = new LocalVertex[]
            {
                new() { Position = new Vector3(-hx, 0, -hz), Color = new Vector3(0.4f, 0.5f, 0.4f) },
                new() { Position = new Vector3( hx, 0, -hz), Color = new Vector3(0.4f, 0.5f, 0.4f) },
                new() { Position = new Vector3( hx, 0,  hz), Color = new Vector3(0.4f, 0.5f, 0.4f) },
                new() { Position = new Vector3(-hx, 0,  hz), Color = new Vector3(0.4f, 0.5f, 0.4f) },
            };

            mesh.Indices = new uint[] { 0, 2, 1, 0, 3, 2 };
            return RegisterMesh(name, mesh);
        }

        /// <summary>注册一个圆柱体网格。</summary>
        public int RegisterCylinder(string name, float radiusTop, float radiusBottom, float height, int segments)
        {
            var mesh = new MeshData();
            var verts = new List<LocalVertex>();
            var indices = new List<uint>();
            float hy = height * 0.5f;

            for (int seg = 0; seg <= segments; seg++)
            {
                float theta = 2.0f * MathF.PI * seg / segments;
                float ct = MathF.Cos(theta), st = MathF.Sin(theta);

                // 底部环
                verts.Add(new LocalVertex
                {
                    Position = new Vector3(radiusBottom * ct, -hy, radiusBottom * st),
                    Color = new Vector3(0.5f, 0.4f, 0.6f)
                });
                // 顶部环
                verts.Add(new LocalVertex
                {
                    Position = new Vector3(radiusTop * ct, hy, radiusTop * st),
                    Color = new Vector3(0.6f, 0.5f, 0.7f)
                });
            }

            // 侧面索引
            for (int seg = 0; seg < segments; seg++)
            {
                int i0 = seg * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;
                indices.Add((uint)i0); indices.Add((uint)i2); indices.Add((uint)i1);
                indices.Add((uint)i1); indices.Add((uint)i2); indices.Add((uint)i3);
            }

            // 底面中心点
            int bottomCenter = verts.Count;
            verts.Add(new LocalVertex { Position = new Vector3(0, -hy, 0), Color = new Vector3(0.4f, 0.3f, 0.5f) });
            for (int seg = 0; seg < segments; seg++)
            {
                indices.Add((uint)bottomCenter);
                indices.Add((uint)(seg * 2 + 2));
                indices.Add((uint)(seg * 2));
            }

            // 顶面中心点
            int topCenter = verts.Count;
            verts.Add(new LocalVertex { Position = new Vector3(0, hy, 0), Color = new Vector3(0.5f, 0.4f, 0.6f) });
            for (int seg = 0; seg < segments; seg++)
            {
                indices.Add((uint)topCenter);
                indices.Add((uint)(seg * 2 + 1));
                indices.Add((uint)(seg * 2 + 3));
            }

            mesh.Vertices = verts.ToArray();
            mesh.Indices = indices.ToArray();
            return RegisterMesh(name, mesh);
        }

        /// <summary>注册自定义网格，返回网格 ID。</summary>
        public int RegisterMesh(string name, MeshData mesh)
        {
            int id = _meshes.Count;
            _meshes.Add(mesh);
            _meshNameToId[name] = id;
            return id;
        }

        /// <summary>按名称获取网格 ID。</summary>
        public int GetMeshId(string name)
        {
            return _meshNameToId.TryGetValue(name, out int id) ? id : -1;
        }

        // ══════════════════════════════════════════════════════════
        //  渲染项管理
        // ══════════════════════════════════════════════════════════

        /// <summary>清空当前帧的渲染项。</summary>
        public void ClearRenderItems()
        {
            _renderItems.Clear();
        }

        /// <summary>添加一个渲染项。</summary>
        public void AddRenderItem(int meshId, Matrix4x4 model, Vector3 tint)
        {
            if (meshId < 0 || meshId >= _meshes.Count) return;
            _renderItems.Add(new RenderItem(meshId, model, tint));
        }

        /// <summary>按网格名称添加渲染项。</summary>
        public void AddRenderItem(string meshName, Matrix4x4 model, Vector3 tint)
        {
            int id = GetMeshId(meshName);
            if (id >= 0) AddRenderItem(id, model, tint);
        }

        // ══════════════════════════════════════════════════════════
        //  渲染
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 渲染所有网格项。在 CPU 端将顶点变换到 NDC，
        /// 合并到单个顶点/索引缓冲后一次 DrawIndexedInstanced 绘制。
        /// </summary>
        public void Render(VulkanCommandList cmdList, RHIDescriptorHandle rtv,
            ViewportCamera3D camera, int viewportWidth, int viewportHeight)
        {
            if (!_initialized || _pipelineState == null || _renderItems.Count == 0)
                return;

            try
            {
                Matrix4x4 viewProj = camera.ViewProjectionMatrix;
                Vector3 camPos = camera.Position;

                // 1. 按距离排序（画家算法：远 → 近）
                var sorted = new List<(RenderItem item, float dist)>(_renderItems.Count);
                foreach (var item in _renderItems)
                {
                    // 提取 Model 矩阵的平移分量作为距离参考
                    Vector3 worldPos = new(item.Model.M41, item.Model.M42, item.Model.M43);
                    float dist = Vector3.DistanceSquared(camPos, worldPos);
                    sorted.Add((item, dist));
                }
                sorted.Sort((a, b) => b.dist.CompareTo(a.dist));  // 远→近

                // 2. 合并所有顶点和索引
                var allVerts = new List<MeshVertex>();
                var allIndices = new List<uint>();

                foreach (var (item, _) in sorted)
                {
                    var mesh = _meshes[item.MeshId];
                    Matrix4x4 mvp = item.Model * viewProj;
                    int vertexOffset = allVerts.Count;

                    // 变换顶点到 NDC
                    for (int i = 0; i < mesh.Vertices.Length; i++)
                    {
                        var lv = mesh.Vertices[i];
                        Vector4 clip = Vector4.Transform(new Vector4(lv.Position, 1.0f), mvp);

                        // 透视除法
                        if (Math.Abs(clip.W) < 1e-6f) clip.W = 1e-6f;
                        float invW = 1.0f / clip.W;

                        // 应用色调
                        Vector3 color = lv.Color * item.TintColor;
                        // 确保颜色在 [0,1] 范围
                        color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);

                        allVerts.Add(new MeshVertex
                        {
                            PosX = clip.X * invW,
                            PosY = clip.Y * invW,
                            ColorR = color.X,
                            ColorG = color.Y,
                            ColorB = color.Z
                        });
                    }

                    // 偏移索引
                    for (int i = 0; i < mesh.Indices.Length; i++)
                    {
                        allIndices.Add((uint)(mesh.Indices[i] + vertexOffset));
                    }
                }

                // 3. 检查容量
                if (allVerts.Count > _maxVertices || allIndices.Count > _maxIndices)
                {
                    // 扩容
                    _maxVertices = Math.Max(_maxVertices, allVerts.Count);
                    _maxIndices = Math.Max(_maxIndices, allIndices.Count);
                    RecreateBuffers();
                }

                // 4. 上传到 GPU
                int vertexSize = Marshal.SizeOf<MeshVertex>();
                var uploadCmd = _device.CreateCommandList(RHIQueueType.Graphics, "MeshUpload");
                uploadCmd.Open();
                uploadCmd.UpdateSubresources(_vertexBuffer!, 0, allVerts.ToArray());
                uploadCmd.UpdateSubresources(_indexBuffer!, 0, allIndices.ToArray());
                uploadCmd.Close();

                var queue = _device.GetCommandQueue(RHIQueueType.Graphics);
                queue.ExecuteCommandLists(new[] { uploadCmd });
                queue.Flush();

                // 5. 绘制
                cmdList.RSSetViewport(0, 0, viewportWidth, viewportHeight, 0, 1);
                cmdList.RSSetScissorRect(0, 0, viewportWidth, viewportHeight);
                cmdList.OMSetRenderTargets(new[] { rtv }, null);
                cmdList.SetPipelineState(_pipelineState);
                cmdList.IASetVertexBuffers(0, _vertexBuffer!, (ulong)vertexSize, 0);
                cmdList.IASetIndexBuffer(_indexBuffer!, RHIFormat.R32_UInt, 0);
                cmdList.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
                cmdList.DrawIndexedInstanced((uint)allIndices.Count, 1, 0, 0, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MeshRenderer] Render 异常: {ex.Message}");
            }
        }

        private void RecreateBuffers()
        {
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            CreateDynamicBuffers();
            Debug.WriteLine($"[MeshRenderer] 缓冲区扩容: VB={_maxVertices} verts, IB={_maxIndices} indices");
        }

        // ══════════════════════════════════════════════════════════
        //  工具
        // ══════════════════════════════════════════════════════════

        private static byte[] UintArrayToBytes(uint[] words)
        {
            byte[] result = new byte[words.Length * 4];
            Buffer.BlockCopy(words, 0, result, 0, result.Length);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _pipelineState?.Dispose();
            _rootSignature?.Dispose();
            _initialized = false;
            Debug.WriteLine("[MeshRenderer] 资源已释放");
        }
    }
}
