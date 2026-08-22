// =============================================================================
// ViewportSceneRenderer.cs - 编辑器视口场景渲染协调器
// 统一管理相机、网格、三角形验证渲染器和线框图元渲染器。
// 提供单一 Render 入口，按正确顺序渲染所有视口元素。
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
    /// 视口场景渲染器 —— 协调编辑器视口中所有可渲染元素的绘制。
    /// 渲染顺序：清除 → 三角形（验证） → 线框图元 → 网格 → 叠加层。
    /// </summary>
    public sealed class ViewportSceneRenderer : IDisposable
    {
        // ── 渲染组件 ──
        private readonly VulkanDevice _device;
        private ViewportCamera3D _camera;
        private TriangleRenderer? _triangleRenderer;
        private GridRenderer? _gridRenderer;
        private WireframePrimitiveRenderer? _wireframeRenderer;

        // ── 状态 ──
        private bool _initialized;
        private bool _disposed;
        private int _viewportWidth = 800;
        private int _viewportHeight = 600;
        private bool _gridNeedsUpdate = true;
        private Vector3 _lastCameraTarget = Vector3.Zero;
        private float _lastCameraDistance;

        // ── 统计 ──
        public int DrawCalls { get; private set; }
        public int LineCount { get; private set; }
        public int TriangleCount { get; private set; }

        /// <summary>视口相机。</summary>
        public ViewportCamera3D Camera => _camera;

        /// <summary>是否显示网格。</summary>
        public bool ShowGrid { get; set; } = true;

        /// <summary>是否显示验证三角形。</summary>
        public bool ShowTestTriangle { get; set; } = true;

        /// <summary>是否显示测试立方体。</summary>
        public bool ShowTestCube { get; set; } = true;

        /// <summary>是否显示 Gizmo（预留接口，当前未实现渲染）。</summary>
        public bool ShowGizmos { get; set; } = true;

        /// <summary>构造函数。</summary>
        public ViewportSceneRenderer(VulkanDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _camera = new ViewportCamera3D();
            _camera.SetViewportSize(_viewportWidth, _viewportHeight);
        }

        /// <summary>初始化所有渲染组件。</summary>
        public void Initialize()
        {
            if (_initialized) return;
            try
            {
                // 三角形渲染器（验证 Vulkan 管线）
                _triangleRenderer = new TriangleRenderer(_device);
                _triangleRenderer.Initialize();

                // 网格渲染器
                _gridRenderer = new GridRenderer(_device);
                _gridRenderer.Initialize();

                // 线框图元渲染器
                _wireframeRenderer = new WireframePrimitiveRenderer(_device);
                _wireframeRenderer.Initialize();

                // 添加测试立方体
                _wireframeRenderer.AddCube(Vector3.Zero, Vector3.One, new Vector3(0.8f, 0.6f, 0.2f));
                _wireframeRenderer.AddCube(new Vector3(3, 0, 0), new Vector3(0.5f), new Vector3(0.2f, 0.8f, 0.4f));
                _wireframeRenderer.AddCube(new Vector3(-3, 0, 0), new Vector3(0.5f), new Vector3(0.2f, 0.4f, 0.8f));

                _initialized = true;
                Debug.WriteLine("[ViewportSceneRenderer] 初始化完成 - 相机+网格+图元就绪");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewportSceneRenderer] 初始化失败: {ex.Message}");
            }
        }

        /// <summary>更新视口尺寸。</summary>
        public void SetViewportSize(int width, int height)
        {
            _viewportWidth = Math.Max(1, width);
            _viewportHeight = Math.Max(1, height);
            _camera.SetViewportSize(width, height);
            _gridNeedsUpdate = true;
        }

        /// <summary>更新场景（每帧调用，在渲染前）。</summary>
        public void Update(float deltaTime)
        {
            // 检查相机是否移动，决定是否需要重建网格
            float targetDist = Vector3.Distance(_camera.Target, _lastCameraTarget);
            if (targetDist > 0.5f || Math.Abs(_camera.OrbitDistance - _lastCameraDistance) > 0.5f)
            {
                _gridNeedsUpdate = true;
                _lastCameraTarget = _camera.Target;
                _lastCameraDistance = _camera.OrbitDistance;
            }

            // 更新网格顶点（CPU 变换）
            if (_gridNeedsUpdate && _gridRenderer != null && ShowGrid)
            {
                _gridRenderer.UpdateGrid(_camera);
                _wireframeRenderer?.UpdateVertices(_camera);
                _gridNeedsUpdate = false;
            }
        }

        /// <summary>
        /// 渲染一帧。调用者需先 AcquireNextImage 并打开命令列表。
        /// </summary>
        public void Render(VulkanCommandList cmdList, RHIDescriptorHandle rtv)
        {
            DrawCalls = 0;
            LineCount = 0;
            TriangleCount = 0;

            if (!_initialized) return;

            try
            {
                // 1. 清除渲染目标（深色背景）
                cmdList.ClearRenderTargetView(rtv, new Vector4(0.06f, 0.06f, 0.10f, 1.0f));

                // 2. 渲染验证三角形（NDC 空间，无相机变换）
                if (ShowTestTriangle && _triangleRenderer != null)
                {
                    _triangleRenderer.Render(cmdList, rtv, _viewportWidth, _viewportHeight);
                    DrawCalls++;
                    TriangleCount += 1;
                }

                // 3. 渲染线框图元（CPU 预变换到 NDC）
                if (ShowTestCube && _wireframeRenderer != null)
                {
                    _wireframeRenderer.Render(cmdList, rtv, _viewportWidth, _viewportHeight);
                    DrawCalls++;
                    LineCount += _wireframeRenderer.VertexCount / 2;
                }

                // 4. 渲染编辑器网格（最上层）
                if (ShowGrid && _gridRenderer != null)
                {
                    _gridRenderer.Render(cmdList, rtv, _viewportWidth, _viewportHeight);
                    DrawCalls++;
                    LineCount += _gridRenderer.VertexCount / 2;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewportSceneRenderer] Render 异常: {ex.Message}");
            }
        }

        /// <summary>获取统计信息字符串。</summary>
        public string GetStatsString()
        {
            return $"Draw:{DrawCalls} | Tri:{TriangleCount} | Lines:{LineCount} | Grid:{_gridRenderer?.VertexCount / 2 ?? 0}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _triangleRenderer?.Dispose();
            _gridRenderer?.Dispose();
            _wireframeRenderer?.Dispose();
            _initialized = false;
            Debug.WriteLine("[ViewportSceneRenderer] 资源已释放");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // WireframePrimitiveRenderer - 线框图元渲染器
    // 渲染线框立方体等基本图元，用于场景可视化。
    // 同样使用 CPU 预变换 + LineList 拓扑 + 复用 SPIR-V 着色器。
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class WireframePrimitiveRenderer : IDisposable
    {
        private readonly VulkanDevice _device;
        private RHIBuffer? _vertexBuffer;
        private RHIPipelineState? _pipelineState;
        private RHIRootSignature? _rootSignature;
        private bool _disposed;
        private bool _initialized;

        // ── 顶点格式 ──
        [StructLayout(LayoutKind.Sequential)]
        private struct WireVertex
        {
            public float PosX, PosY;   // NDC
            public float ColorR, ColorG, ColorB;
        }

        // ── 预编译 SPIR-V（与 TriangleRenderer 相同） ──
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

        // ── 图元定义 ──
        private readonly List<(Vector3 center, Vector3 size, Vector3 color)> _cubes = new();
        private WireVertex[]? _vertices;
        private int _vertexCount;

        public int VertexCount => _vertexCount;

        public WireframePrimitiveRenderer(VulkanDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public void Initialize()
        {
            if (_initialized) return;
            CreatePipelineState();
            _initialized = true;
            Debug.WriteLine("[WireframePrimitiveRenderer] 初始化成功");
        }

        private void CreatePipelineState()
        {
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = Array.Empty<RHIRootParameter>(),
                StaticSamplers = Array.Empty<RHISamplerDesc>(),
                AllowInputAssembler = true
            };
            _rootSignature = _device.CreateRootSignature(rootSigDesc, "WireRootSig");

            var vsShader = new RHIShaderBytecode(
                UintArrayToBytes(VertexShaderSPIRV), "main",
                RHIShaderStage.Vertex, "WireVS");

            var psShader = new RHIShaderBytecode(
                UintArrayToBytes(FragmentShaderSPIRV), "main",
                RHIShaderStage.Pixel, "WirePS");

            var inputLayout = new RHIInputElement[]
            {
                new RHIInputElement("POSITION", 0, RHIFormat.R32G32_Float, 0, 0),
                new RHIInputElement("COLOR", 0, RHIFormat.R32G32B32_Float, 0, 8),
            };

            var gfxDesc = new RHIGraphicsPipelineDesc
            {
                Name = "WirePSO",
                VS = vsShader,
                PS = psShader,
                InputLayout = inputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.LineList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.B8G8R8A8_UNorm },
                DSVFormat = RHIFormat.Unknown,
                SampleCount = 1,
                RootSignature = _rootSignature
            };

            _pipelineState = _device.CreateGraphicsPipelineState(gfxDesc, "WirePSO");
        }

        /// <summary>添加一个线框立方体。</summary>
        public void AddCube(Vector3 center, Vector3 size, Vector3 color)
        {
            _cubes.Add((center, size, color));
        }

        /// <summary>清空所有图元。</summary>
        public void Clear()
        {
            _cubes.Clear();
            _vertexCount = 0;
        }

        /// <summary>更新顶点缓冲（CPU 变换到 NDC）。</summary>
        public void UpdateVertices(ViewportCamera3D camera)
        {
            if (_cubes.Count == 0)
            {
                _vertexCount = 0;
                return;
            }

            var verts = new List<WireVertex>();
            Matrix4x4 viewProj = camera.ViewProjectionMatrix;

            foreach (var (center, size, color) in _cubes)
            {
                float hx = size.X * 0.5f;
                float hy = size.Y * 0.5f;
                float hz = size.Z * 0.5f;

                // 立方体 8 个顶点
                Vector3[] corners = new Vector3[8];
                corners[0] = center + new Vector3(-hx, -hy, -hz);
                corners[1] = center + new Vector3(+hx, -hy, -hz);
                corners[2] = center + new Vector3(+hx, -hy, +hz);
                corners[3] = center + new Vector3(-hx, -hy, +hz);
                corners[4] = center + new Vector3(-hx, +hy, -hz);
                corners[5] = center + new Vector3(+hx, +hy, -hz);
                corners[6] = center + new Vector3(+hx, +hy, +hz);
                corners[7] = center + new Vector3(-hx, +hy, +hz);

                // 12 条边
                int[][] edges = new int[][]
                {
                    new[] {0,1}, new[] {1,2}, new[] {2,3}, new[] {3,0}, // 底面
                    new[] {4,5}, new[] {5,6}, new[] {6,7}, new[] {7,4}, // 顶面
                    new[] {0,4}, new[] {1,5}, new[] {2,6}, new[] {3,7}, // 竖边
                };

                foreach (var edge in edges)
                {
                    AddLineNdc(verts, corners[edge[0]], corners[edge[1]], color, viewProj);
                }
            }

            _vertices = verts.ToArray();
            _vertexCount = verts.Count;

            if (_vertexCount == 0) return;

            // 更新顶点缓冲
            int vertexSize = Marshal.SizeOf<WireVertex>();
            ulong bufferSize = (ulong)(_vertexCount * vertexSize);

            if (_vertexBuffer == null)
            {
                var desc = new RHIBufferDesc
                {
                    SizeInBytes = bufferSize,
                    HeapType = RHIResourceHeapType.Default,
                    Flags = RHIBufferFlags.VertexBuffer,
                    Stride = (ulong)vertexSize,
                    Format = RHIFormat.Unknown
                };
                _vertexBuffer = _device.CreateBuffer(desc, "WireVertexBuffer");
            }

            var cmdList = _device.CreateCommandList(RHIQueueType.Graphics, "WireUpload");
            cmdList.Open();
            cmdList.UpdateSubresources(_vertexBuffer, 0, _vertices);
            cmdList.Close();

            var queue = _device.GetCommandQueue(RHIQueueType.Graphics);
            queue.ExecuteCommandLists(new[] { cmdList });
            queue.Flush();
        }

        private static void AddLineNdc(List<WireVertex> lines, Vector3 worldP1, Vector3 worldP2, Vector3 color, Matrix4x4 viewProj)
        {
            Vector4 clip1 = Vector4.Transform(new Vector4(worldP1, 1.0f), viewProj);
            Vector4 clip2 = Vector4.Transform(new Vector4(worldP2, 1.0f), viewProj);

            if (Math.Abs(clip1.W) < 1e-6f || Math.Abs(clip2.W) < 1e-6f) return;

            float w1 = 1.0f / clip1.W;
            float w2 = 1.0f / clip2.W;

            lines.Add(new WireVertex { PosX = clip1.X * w1, PosY = clip1.Y * w1, ColorR = color.X, ColorG = color.Y, ColorB = color.Z });
            lines.Add(new WireVertex { PosX = clip2.X * w2, PosY = clip2.Y * w2, ColorR = color.X, ColorG = color.Y, ColorB = color.Z });
        }

        public void Render(VulkanCommandList cmdList, RHIDescriptorHandle rtv, int viewportWidth, int viewportHeight)
        {
            if (!_initialized || _vertexBuffer == null || _pipelineState == null || _vertexCount == 0)
                return;

            try
            {
                cmdList.RSSetViewport(0, 0, viewportWidth, viewportHeight, 0, 1);
                cmdList.RSSetScissorRect(0, 0, viewportWidth, viewportHeight);
                cmdList.OMSetRenderTargets(new[] { rtv }, null);
                cmdList.SetPipelineState(_pipelineState);

                int vertexSize = Marshal.SizeOf<WireVertex>();
                cmdList.IASetVertexBuffers(0, _vertexBuffer, (ulong)vertexSize, 0);
                cmdList.IASetPrimitiveTopology(RHIPrimitiveTopology.LineList);

                cmdList.DrawInstanced((uint)_vertexCount, 1, 0, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WireframePrimitiveRenderer] Render 异常: {ex.Message}");
            }
        }

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
            _pipelineState?.Dispose();
            _rootSignature?.Dispose();
        }
    }
}
