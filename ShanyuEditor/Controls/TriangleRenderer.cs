using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.RHI.Vulkan;

namespace ShanyuEditor.Controls
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

        // ── 预编译 SPIR-V shader bytecode ──
        // 由 Python 脚本 gen_spirv.py 生成，v1.0
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

        // 将 uint[] 转换为 byte[]（SPIR-V 要求 4 字节对齐的小端序字）
        private static byte[] UintArrayToBytes(uint[] words)
        {
            byte[] result = new byte[words.Length * 4];
            Buffer.BlockCopy(words, 0, result, 0, result.Length);
            return result;
        }

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

            // Vertex shader bytecode
            var vsShader = new RHIShaderBytecode(
                UintArrayToBytes(VertexShaderSPIRV),
                "main",
                RHIShaderStage.Vertex,
                "TriangleVS");

            // Fragment shader bytecode
            var psShader = new RHIShaderBytecode(
                UintArrayToBytes(FragmentShaderSPIRV),
                "main",
                RHIShaderStage.Pixel,
                "TrianglePS");

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
