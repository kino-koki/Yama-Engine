using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.RHI.Vulkan;

namespace YamaEditor.Controls
{
    /// <summary>
    /// 简单的三角形渲染器 —— 用于验证 Vulkan 后端到编辑器视口的打通。
    /// 渲染一个彩色三角形，不依赖任何场景系统。
    /// </summary>
    public sealed class TriangleRenderer : IDisposable
    {
        private readonly VulkanDevice _device;
        private RHIBuffer? _vertexBuffer;
        private RHIPipelineState? _pipelineState;
        private RHIRootSignature? _rootSignature;
        private bool _disposed;
        private bool _initialized;

        // ── 顶点数据结构 ──
        // 每个顶点: 位置 (vec2) + 颜色 (vec3) = 20 bytes
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public float PosX, PosY;
            public float ColorR, ColorG, ColorB;
        }

        private static readonly Vertex[] Vertices =
        {
            new Vertex { PosX =  0.0f, PosY =  0.5f, ColorR = 1.0f, ColorG = 0.0f, ColorB = 0.0f }, // 顶部 - 红
            new Vertex { PosX = -0.5f, PosY = -0.5f, ColorR = 0.0f, ColorG = 1.0f, ColorB = 0.0f }, // 左下 - 绿
            new Vertex { PosX =  0.5f, PosY = -0.5f, ColorR = 0.0f, ColorG = 0.0f, ColorB = 1.0f }, // 右下 - 蓝
        };

        public TriangleRenderer(VulkanDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>初始化渲染资源：创建顶点缓冲和 PSO。</summary>
        public void Initialize()
        {
            if (_initialized) return;
            try
            {
                CreateVertexBuffer();
                CreatePipelineState();
                _initialized = true;
                Debug.WriteLine("[TriangleRenderer] 初始化成功");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TriangleRenderer] 初始化失败: {ex.Message}");
            }
        }

        private void CreateVertexBuffer()
        {
            int vertexSize = Marshal.SizeOf<Vertex>();
            ulong bufferSize = (ulong)(Vertices.Length * vertexSize);

            var desc = new RHIBufferDesc
            {
                SizeInBytes = bufferSize,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer | RHIBufferFlags.None,
                Stride = (ulong)vertexSize,
                Format = RHIFormat.Unknown
            };

            _vertexBuffer = _device.CreateBuffer(desc, "TriangleVertexBuffer");

            // 使用命令列表上传顶点数据
            var cmdList = _device.CreateCommandList(RHIQueueType.Graphics, "TriangleUpload");
            cmdList.Open();

            cmdList.UpdateSubresources(_vertexBuffer, 0, Vertices);

            cmdList.Close();

            var queue = _device.GetCommandQueue(RHIQueueType.Graphics);
            queue.ExecuteCommandLists(new[] { cmdList });
            queue.Flush(); // 等待上传完成

            Debug.WriteLine($"[TriangleRenderer] 顶点缓冲创建完成，{Vertices.Length} vertices, {bufferSize} bytes");
        }

        private void CreatePipelineState()
        {
            // 创建根签名（空，不使用 descriptor tables）
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = Array.Empty<RHIRootParameter>(),
                StaticSamplers = Array.Empty<RHISamplerDesc>(),
                AllowInputAssembler = true
            };
            _rootSignature = _device.CreateRootSignature(rootSigDesc, "TriangleRootSig");

            // Vertex/Fragment shader bytecode（DXC 编译的直通着色器）
            var vsShader = EditorShaderCompiler.PassthroughVertex();
            var psShader = EditorShaderCompiler.PassthroughPixel();

            // 输入布局：位置 (vec2) + 颜色 (vec3)
            var inputLayout = new RHIInputElement[]
            {
                new RHIInputElement("POSITION", 0, RHIFormat.R32G32_Float, 0, 0),
                new RHIInputElement("COLOR", 0, RHIFormat.R32G32B32_Float, 0, 8),
            };

            var gfxDesc = new RHIGraphicsPipelineDesc
            {
                Name = "TrianglePSO",
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

            _pipelineState = _device.CreateGraphicsPipelineState(gfxDesc, "TrianglePSO");

            Debug.WriteLine("[TriangleRenderer] PSO 创建完成");
        }

        /// <summary>在当前命令列表中渲染三角形。</summary>
        public void Render(VulkanCommandList cmdList, RHIDescriptorHandle rtv, int viewportWidth, int viewportHeight)
        {
            if (!_initialized || _vertexBuffer == null || _pipelineState == null) return;

            try
            {
                // 设置视口和裁剪矩形
                cmdList.RSSetViewport(0, 0, viewportWidth, viewportHeight, 0, 1);
                cmdList.RSSetScissorRect(0, 0, viewportWidth, viewportHeight);

                // 设置渲染目标
                cmdList.OMSetRenderTargets(new[] { rtv }, null);

                // 绑定 PSO
                cmdList.SetPipelineState(_pipelineState);

                // 设置顶点缓冲 (slot 0)
                int vertexSize = Marshal.SizeOf<Vertex>();
                cmdList.IASetVertexBuffers(0, _vertexBuffer, (ulong)vertexSize, 0);
                cmdList.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

                // 绘制 3 个顶点
                cmdList.DrawInstanced(3, 1, 0, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TriangleRenderer] Render 异常: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _vertexBuffer?.Dispose();
            _pipelineState?.Dispose();
            _rootSignature?.Dispose();
            _initialized = false;

            Debug.WriteLine("[TriangleRenderer] 资源已释放");
        }
    }
}
