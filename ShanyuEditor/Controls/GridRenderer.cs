// =============================================================================
// GridRenderer.cs - 编辑器无限网格渲染器
// 在 CPU 端生成网格线顶点并变换到裁剪空间，使用 LineList 拓扑渲染。
// 复用与 TriangleRenderer 相同的 SPIR-V 着色器（vec2 pos + vec3 color）。
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
    /// 编辑器无限网格渲染器 —— 在地平面 (Y=0) 绘制自适应网格。
    /// 包含主网格线（每 10 单位）、次网格线（每 1 单位）和坐标轴（X 红、Z 蓝）。
    /// 顶点在 CPU 端预变换到 NDC 裁剪空间。
    /// </summary>
    public sealed class GridRenderer : IDisposable
    {
        private readonly VulkanDevice _device;
        private RHIBuffer? _vertexBuffer;
        private RHIPipelineState? _pipelineState;
        private RHIRootSignature? _rootSignature;
        private bool _disposed;
        private bool _initialized;

        // ── 顶点格式：与 TriangleRenderer 一齐复用 shader ──
        // vec2 position (NDC) + vec3 color = 20 bytes
        [StructLayout(LayoutKind.Sequential)]
        private struct GridVertex
        {
            public float PosX, PosY;   // NDC 坐标
            public float ColorR, ColorG, ColorB;
        }

        // ── 预编译 SPIR-V（与 TriangleRenderer 相同的简单直通着色器） ──
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

        // ── 网格配置 ──
        private const int GridExtent = 50;        // 网格半径（单位数）
        private const int MajorLineInterval = 10;  // 主线间隔
        private static readonly Vector3 MinorColor = new(0.3f, 0.3f, 0.35f);
        private static readonly Vector3 MajorColor = new(0.45f, 0.45f, 0.5f);
        private static readonly Vector3 XAxisColor = new(0.8f, 0.2f, 0.2f);
        private static readonly Vector3 ZAxisColor = new(0.2f, 0.2f, 0.8f);

        private int _lastVertexCount;
        private GridVertex[]? _cachedVertices;

        /// <summary>当前网格顶点数（顶点数 = 线段数 * 2）。</summary>
        public int VertexCount => _lastVertexCount;

        public GridRenderer(VulkanDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>初始化渲染管线（PSO 使用 LineList 拓扑）。</summary>
        public void Initialize()
        {
            if (_initialized) return;
            try
            {
                CreatePipelineState();
                _initialized = true;
                Debug.WriteLine("[GridRenderer] 初始化成功");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GridRenderer] 初始化失败: {ex.Message}");
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
            _rootSignature = _device.CreateRootSignature(rootSigDesc, "GridRootSig");

            var vsShader = new RHIShaderBytecode(
                UintArrayToBytes(VertexShaderSPIRV), "main",
                RHIShaderStage.Vertex, "GridVS");

            var psShader = new RHIShaderBytecode(
                UintArrayToBytes(FragmentShaderSPIRV), "main",
                RHIShaderStage.Pixel, "GridPS");

            var inputLayout = new RHIInputElement[]
            {
                new RHIInputElement("POSITION", 0, RHIFormat.R32G32_Float, 0, 0),
                new RHIInputElement("COLOR", 0, RHIFormat.R32G32B32_Float, 0, 8),
            };

            var gfxDesc = new RHIGraphicsPipelineDesc
            {
                Name = "GridPSO",
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

            _pipelineState = _device.CreateGraphicsPipelineState(gfxDesc, "GridPSO");
            Debug.WriteLine("[GridRenderer] PSO 创建完成 (LineList)");
        }

        // ══════════════════════════════════════════════════════════
        //  网格顶点生成 + CPU 变换
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 生成网格顶点并更新顶点缓冲。
        /// 在 CPU 端将世界坐标变换到 NDC 裁剪空间。
        /// </summary>
        public void UpdateGrid(ViewportCamera3D camera)
        {
            var vertices = GenerateGridVertices(camera);
            _cachedVertices = vertices;
            _lastVertexCount = vertices.Length;

            if (vertices.Length == 0) return;

            // 更新或创建顶点缓冲
            int vertexSize = Marshal.SizeOf<GridVertex>();
            ulong bufferSize = (ulong)(vertices.Length * vertexSize);

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
                _vertexBuffer = _device.CreateBuffer(desc, "GridVertexBuffer");
            }

            // 上传顶点数据
            var cmdList = _device.CreateCommandList(RHIQueueType.Graphics, "GridUpload");
            cmdList.Open();
            cmdList.UpdateSubresources(_vertexBuffer, 0, vertices);
            cmdList.Close();

            var queue = _device.GetCommandQueue(RHIQueueType.Graphics);
            queue.ExecuteCommandLists(new[] { cmdList });
            queue.Flush();
        }

        /// <summary>生成网格线段顶点（已变换到 NDC）。</summary>
        private GridVertex[] GenerateGridVertices(ViewportCamera3D camera)
        {
            var lines = new List<GridVertex>();
            Matrix4x4 viewProj = camera.ViewProjectionMatrix;
            Vector3 camTarget = camera.Target;

            // 网格中心跟随相机目标点（取整数）
            float gridCenterX = MathF.Floor(camTarget.X);
            float gridCenterZ = MathF.Floor(camTarget.Z);

            // 自适应网格范围（根据轨道距离调整）
            int extent = Math.Clamp((int)(camera.OrbitDistance * 1.5f), 10, GridExtent);

            // ── 生成 X 方向线条（沿 Z 轴延伸的线） ──
            for (int i = -extent; i <= extent; i++)
            {
                float x = gridCenterX + i;
                bool isMajor = (i % MajorLineInterval) == 0;
                bool isAxis = Math.Abs(x) < 0.001f;

                Vector3 color = isAxis ? ZAxisColor : (isMajor ? MajorColor : MinorColor);

                var p1 = new Vector3(x, 0, gridCenterZ - extent);
                var p2 = new Vector3(x, 0, gridCenterZ + extent);

                AddLineNdc(lines, p1, p2, color, viewProj);
            }

            // ── 生成 Z 方向线条（沿 X 轴延伸的线） ──
            for (int i = -extent; i <= extent; i++)
            {
                float z = gridCenterZ + i;
                bool isMajor = (i % MajorLineInterval) == 0;
                bool isAxis = Math.Abs(z) < 0.001f;

                Vector3 color = isAxis ? XAxisColor : (isMajor ? MajorColor : MinorColor);

                var p1 = new Vector3(gridCenterX - extent, 0, z);
                var p2 = new Vector3(gridCenterX + extent, 0, z);

                AddLineNdc(lines, p1, p2, color, viewProj);
            }

            return lines.ToArray();
        }

        /// <summary>将一条世界空间线段变换到 NDC 并添加到顶点列表。</summary>
        private static void AddLineNdc(List<GridVertex> lines, Vector3 worldP1, Vector3 worldP2, Vector3 color, Matrix4x4 viewProj)
        {
            Vector4 clip1 = Vector4.Transform(new Vector4(worldP1, 1.0f), viewProj);
            Vector4 clip2 = Vector4.Transform(new Vector4(worldP2, 1.0f), viewProj);

            // 透视除法
            if (Math.Abs(clip1.W) < 1e-6f || Math.Abs(clip2.W) < 1e-6f) return;

            float w1 = 1.0f / clip1.W;
            float w2 = 1.0f / clip2.W;

            float x1 = clip1.X * w1;
            float y1 = clip1.Y * w1;
            float x2 = clip2.X * w2;
            float y2 = clip2.Y * w2;

            // 简单视锥剔除：两端点都在同一侧外面则跳过
            const float margin = 0.1f;
            if ((x1 < -1 - margin && x2 < -1 - margin) || (x1 > 1 + margin && x2 > 1 + margin)) return;
            if ((y1 < -1 - margin && y2 < -1 - margin) || (y1 > 1 + margin && y2 > 1 + margin)) return;

            lines.Add(new GridVertex { PosX = x1, PosY = y1, ColorR = color.X, ColorG = color.Y, ColorB = color.Z });
            lines.Add(new GridVertex { PosX = x2, PosY = y2, ColorR = color.X, ColorG = color.Y, ColorB = color.Z });
        }

        // ══════════════════════════════════════════════════════════
        //  渲染
        // ══════════════════════════════════════════════════════════

        /// <summary>在当前命令列表中渲染网格线。</summary>
        public void Render(VulkanCommandList cmdList, RHIDescriptorHandle rtv, int viewportWidth, int viewportHeight)
        {
            if (!_initialized || _vertexBuffer == null || _pipelineState == null || _lastVertexCount == 0)
                return;

            try
            {
                cmdList.RSSetViewport(0, 0, viewportWidth, viewportHeight, 0, 1);
                cmdList.RSSetScissorRect(0, 0, viewportWidth, viewportHeight);
                cmdList.OMSetRenderTargets(new[] { rtv }, null);
                cmdList.SetPipelineState(_pipelineState);

                int vertexSize = Marshal.SizeOf<GridVertex>();
                cmdList.IASetVertexBuffers(0, _vertexBuffer, (ulong)vertexSize, 0);
                cmdList.IASetPrimitiveTopology(RHIPrimitiveTopology.LineList);

                // 绘制所有线段
                cmdList.DrawInstanced((uint)_lastVertexCount, 1, 0, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GridRenderer] Render 异常: {ex.Message}");
            }
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
            _pipelineState?.Dispose();
            _rootSignature?.Dispose();
            _initialized = false;
            Debug.WriteLine("[GridRenderer] 资源已释放");
        }
    }
}
