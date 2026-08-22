// =============================================================================
// RHIDeviceExtensions.cs - 山雨引擎 RHI 设备扩展方法
// =============================================================================
// 这个文件给 RHIDevice 加一堆扩展方法，把常用操作封装成简洁 API。
// 设计思路参考了 bgfx 和 DiligentEngine，让上层不用直接面对底层创建信息。
//
// 每个方法都做了几个事：
//   1. 填充 CreateInfo 结构
//   2. 调用 device 创建
//   3. 失败抛异常 (带详细错误信息)
//   4. 成功返回资源句柄
//
// FIXME: 还没处理多 backend (D3D12/Metal/Vulkan) 的差异
// TODO: 加资源池化，避免频繁创建销毁
//
// 作者: 山雨引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.RHI
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: Buffer 创建扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Buffer 扩展方法。</summary>
    public static class RHIBufferExtensions
    {
        /// <summary>创建顶点缓冲。</summary>
        public static RHIBuffer CreateVertexBuffer(this RHIDevice device, ulong size,
                                                   bool cpuWritable = false,
                                                   string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.VertexBuffer | RHIBufferUsage.CopyDst,
                CpuAccess = cpuWritable ? RHICpuAccess.Write : RHICpuAccess.None,
                Name = name ?? "VertexBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建索引缓冲。</summary>
        public static RHIBuffer CreateIndexBuffer(this RHIDevice device, ulong size,
                                                  bool cpuWritable = false,
                                                  string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.IndexBuffer | RHIBufferUsage.CopyDst,
                CpuAccess = cpuWritable ? RHICpuAccess.Write : RHICpuAccess.None,
                Name = name ?? "IndexBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建 uniform/constant buffer。</summary>
        public static RHIBuffer CreateUniformBuffer(this RHIDevice device, ulong size,
                                                    bool dynamic = true,
                                                    string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.UniformBuffer,
                CpuAccess = dynamic ? RHICpuAccess.Write : RHICpuAccess.None,
                Name = name ?? "UniformBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建 storage buffer (SSBO/UAV)。</summary>
        public static RHIBuffer CreateStorageBuffer(this RHIDevice device, ulong size,
                                                    RHICpuAccess cpuAccess = RHICpuAccess.None,
                                                    string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.StorageBuffer | RHIBufferUsage.CopyDst | RHIBufferUsage.CopySrc,
                CpuAccess = cpuAccess,
                Name = name ?? "StorageBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建 staging buffer (CPU 写 GPU 读)。</summary>
        public static RHIBuffer CreateStagingBuffer(this RHIDevice device, ulong size,
                                                    string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.CopySrc,
                CpuAccess = RHICpuAccess.Write,
                Name = name ?? "StagingBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建 readback buffer (GPU 写 CPU 读)。</summary>
        public static RHIBuffer CreateReadbackBuffer(this RHIDevice device, ulong size,
                                                     string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.CopyDst,
                CpuAccess = RHICpuAccess.Read,
                Name = name ?? "ReadbackBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建 structured buffer (带 stride)。</summary>
        public static RHIBuffer CreateStructuredBuffer(this RHIDevice device, uint elementCount,
                                                       uint elementStride,
                                                       RHICpuAccess cpuAccess = RHICpuAccess.None,
                                                       string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = (ulong)elementCount * elementStride,
                Usage = RHIBufferUsage.StorageBuffer | RHIBufferUsage.CopyDst,
                CpuAccess = cpuAccess,
                Stride = elementStride,
                Name = name ?? $"Structured_{elementCount}x{elementStride}",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建 texel buffer。</summary>
        public static RHIBuffer CreateTexelBuffer(this RHIDevice device, ulong size,
                                                  RHIFormat format,
                                                  string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.UniformTexelBuffer | RHIBufferUsage.StorageTexelBuffer,
                Format = format,
                Name = name ?? "TexelBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建 indirect argument buffer。</summary>
        public static RHIBuffer CreateIndirectBuffer(this RHIDevice device, ulong size,
                                                     string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIBufferUsage.IndirectBuffer | RHIBufferUsage.CopyDst,
                Name = name ?? "IndirectBuffer",
            };
            return device.CreateBuffer(desc);
        }

        /// <summary>创建带初始数据的 buffer。</summary>
        public static RHIBuffer CreateBufferWithData(this RHIDevice device, byte[] data,
                                                      RHIBufferUsage usage,
                                                      RHICpuAccess cpuAccess = RHICpuAccess.None,
                                                      string name = null)
        {
            var desc = new RHIBufferDesc
            {
                Size = (ulong)data.Length,
                Usage = usage,
                CpuAccess = cpuAccess,
                Name = name ?? "BufferWithData",
            };
            var buf = device.CreateBuffer(desc);
            device.UpdateBuffer(buf, 0, data);
            return buf;
        }

        /// <summary>更新 buffer 数据 (CPU 端)。</summary>
        public static void UpdateBuffer(this RHIDevice device, RHIBuffer buffer,
                                        ulong offset, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            device.UpdateBufferData(buffer, offset, data, 0, data.Length);
        }

        /// <summary>更新 buffer 数据 (带 source offset)。</summary>
        public static void UpdateBufferData(this RHIDevice device, RHIBuffer buffer,
                                            ulong dstOffset, byte[] srcData,
                                            int srcOffset, int srcLength)
        {
            if (srcData == null || srcLength == 0) return;
            device.UpdateBufferRange(buffer, dstOffset, srcData, srcOffset, srcLength);
        }

        /// <summary>映射 buffer 内存到 CPU。</summary>
        public static IntPtr MapBuffer(this RHIDevice device, RHIBuffer buffer,
                                       RHIMapMode mode = RHIMapMode.Write)
        {
            return device.MapBufferRange(buffer, 0, buffer.Desc.Size, mode);
        }

        /// <summary>解映射。</summary>
        public static void UnmapBuffer(this RHIDevice device, RHIBuffer buffer)
        {
            device.UnmapBufferRange(buffer);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: Texture 创建扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Texture 扩展方法。</summary>
    public static class RHITextureExtensions
    {
        /// <summary>创建 2D 贴图。</summary>
        public static RHITexture CreateTexture2D(this RHIDevice device, uint width, uint height,
                                                 RHIFormat format, uint mipLevels = 1,
                                                 RHITextureUsage usage = RHITextureUsage.ShaderResource,
                                                 string name = null)
        {
            var desc = new RHITextureDesc
            {
                Type = RHITextureType.Texture2D,
                Width = width,
                Height = height,
                Depth = 1,
                Format = format,
                MipLevels = mipLevels,
                ArraySize = 1,
                SampleCount = 1,
                Usage = usage,
                Name = name ?? $"Tex2D_{width}x{height}_{format}",
            };
            return device.CreateTexture(desc);
        }

        /// <summary>创建 3D 贴图。</summary>
        public static RHITexture CreateTexture3D(this RHIDevice device, uint width, uint height, uint depth,
                                                 RHIFormat format, uint mipLevels = 1,
                                                 RHITextureUsage usage = RHITextureUsage.ShaderResource,
                                                 string name = null)
        {
            var desc = new RHITextureDesc
            {
                Type = RHITextureType.Texture3D,
                Width = width,
                Height = height,
                Depth = depth,
                Format = format,
                MipLevels = mipLevels,
                ArraySize = 1,
                SampleCount = 1,
                Usage = usage,
                Name = name ?? $"Tex3D_{width}x{height}x{depth}_{format}",
            };
            return device.CreateTexture(desc);
        }

        /// <summary>创建 cube map。</summary>
        public static RHITexture CreateTextureCube(this RHIDevice device, uint size,
                                                   RHIFormat format, uint mipLevels = 1,
                                                   RHITextureUsage usage = RHITextureUsage.ShaderResource,
                                                   string name = null)
        {
            var desc = new RHITextureDesc
            {
                Type = RHITextureType.TextureCube,
                Width = size,
                Height = size,
                Depth = 1,
                Format = format,
                MipLevels = mipLevels,
                ArraySize = 6,
                SampleCount = 1,
                Usage = usage,
                Name = name ?? $"TexCube_{size}_{format}",
            };
            return device.CreateTexture(desc);
        }

        /// <summary>创建 2D array。</summary>
        public static RHITexture CreateTexture2DArray(this RHIDevice device, uint width, uint height,
                                                     uint arraySize, RHIFormat format,
                                                     uint mipLevels = 1,
                                                     RHITextureUsage usage = RHITextureUsage.ShaderResource,
                                                     string name = null)
        {
            var desc = new RHITextureDesc
            {
                Type = RHITextureType.Texture2DArray,
                Width = width,
                Height = height,
                Depth = 1,
                Format = format,
                MipLevels = mipLevels,
                ArraySize = arraySize,
                SampleCount = 1,
                Usage = usage,
                Name = name ?? $"Tex2DArray_{width}x{height}x{arraySize}_{format}",
            };
            return device.CreateTexture(desc);
        }

        /// <summary>创建 render target。</summary>
        public static RHITexture CreateRenderTarget(this RHIDevice device, uint width, uint height,
                                                    RHIFormat format, uint sampleCount = 1,
                                                    string name = null)
        {
            var desc = new RHITextureDesc
            {
                Type = RHITextureType.Texture2D,
                Width = width,
                Height = height,
                Depth = 1,
                Format = format,
                MipLevels = 1,
                ArraySize = 1,
                SampleCount = sampleCount,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource,
                Name = name ?? $"RT_{width}x{height}_{format}",
            };
            return device.CreateTexture(desc);
        }

        /// <summary>创建 depth stencil。</summary>
        public static RHITexture CreateDepthStencil(this RHIDevice device, uint width, uint height,
                                                    RHIFormat format, uint sampleCount = 1,
                                                    string name = null)
        {
            var desc = new RHITextureDesc
            {
                Type = RHITextureType.Texture2D,
                Width = width,
                Height = height,
                Depth = 1,
                Format = format,
                MipLevels = 1,
                ArraySize = 1,
                SampleCount = sampleCount,
                Usage = RHITextureUsage.DepthStencil | RHITextureUsage.ShaderResource,
                Name = name ?? $"DS_{width}x{height}_{format}",
            };
            return device.CreateTexture(desc);
        }

        /// <summary>创建 UAV/storage image。</summary>
        public static RHITexture CreateStorageImage(this RHIDevice device, uint width, uint height,
                                                    RHIFormat format, uint mipLevels = 1,
                                                    string name = null)
        {
            var desc = new RHITextureDesc
            {
                Type = RHITextureType.Texture2D,
                Width = width,
                Height = height,
                Depth = 1,
                Format = format,
                MipLevels = mipLevels,
                ArraySize = 1,
                SampleCount = 1,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                Name = name ?? $"Storage_{width}x{height}_{format}",
            };
            return device.CreateTexture(desc);
        }

        /// <summary>创建带初始数据的贴图。</summary>
        public static RHITexture CreateTextureWithData(this RHIDevice device,
                                                        RHITextureDesc desc, byte[] data)
        {
            var tex = device.CreateTexture(desc);
            device.UpdateTexture(tex, 0, 0, 0, desc.Width, desc.Height, 1, 0, data);
            return tex;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: Sampler 创建扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Sampler 扩展方法。</summary>
    public static class RHISamplerExtensions
    {
        /// <summary>创建默认 linear sampler。</summary>
        public static RHISampler CreateLinearSampler(this RHIDevice device, string name = null)
        {
            var desc = new RHISamplerDesc
            {
                Filter = RHIFilter.Linear,
                AddressU = RHIAddressMode.Clamp,
                AddressV = RHIAddressMode.Clamp,
                AddressW = RHIAddressMode.Clamp,
                MinLod = 0,
                MaxLod = float.MaxValue,
                Name = name ?? "LinearSampler",
            };
            return device.CreateSampler(desc);
        }

        /// <summary>创建 point sampler。</summary>
        public static RHISampler CreatePointSampler(this RHIDevice device, string name = null)
        {
            var desc = new RHISamplerDesc
            {
                Filter = RHIFilter.Point,
                AddressU = RHIAddressMode.Clamp,
                AddressV = RHIAddressMode.Clamp,
                AddressW = RHIAddressMode.Clamp,
                Name = name ?? "PointSampler",
            };
            return device.CreateSampler(desc);
        }

        /// <summary>创建 wrap sampler (重复)。</summary>
        public static RHISampler CreateWrapSampler(this RHIDevice device, string name = null)
        {
            var desc = new RHISamplerDesc
            {
                Filter = RHIFilter.Linear,
                AddressU = RHIAddressMode.Wrap,
                AddressV = RHIAddressMode.Wrap,
                AddressW = RHIAddressMode.Wrap,
                Name = name ?? "WrapSampler",
            };
            return device.CreateSampler(desc);
        }

        /// <summary>创建 mirror sampler。</summary>
        public static RHISampler CreateMirrorSampler(this RHIDevice device, string name = null)
        {
            var desc = new RHISamplerDesc
            {
                Filter = RHIFilter.Linear,
                AddressU = RHIAddressMode.Mirror,
                AddressV = RHIAddressMode.Mirror,
                AddressW = RHIAddressMode.Mirror,
                Name = name ?? "MirrorSampler",
            };
            return device.CreateSampler(desc);
        }

        /// <summary>创建 shadow map sampler (depth compare)。</summary>
        public static RHISampler CreateShadowSampler(this RHIDevice device, string name = null)
        {
            var desc = new RHISamplerDesc
            {
                Filter = RHIFilter.Linear,
                AddressU = RHIAddressMode.Clamp,
                AddressV = RHIAddressMode.Clamp,
                AddressW = RHIAddressMode.Clamp,
                CompareFunction = RHICompareFunction.LessEqual,
                MinLod = 0,
                MaxLod = float.MaxValue,
                Name = name ?? "ShadowSampler",
            };
            return device.CreateSampler(desc);
        }

        /// <summary>创建 anisotropic sampler。</summary>
        public static RHISampler CreateAnisotropicSampler(this RHIDevice device, uint maxAniso = 16,
                                                          string name = null)
        {
            var desc = new RHISamplerDesc
            {
                Filter = RHIFilter.Anisotropic,
                AddressU = RHIAddressMode.Wrap,
                AddressV = RHIAddressMode.Wrap,
                AddressW = RHIAddressMode.Wrap,
                MaxAnisotropy = maxAniso,
                Name = name ?? "AnisoSampler",
            };
            return device.CreateSampler(desc);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: Shader 创建扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Shader 扩展方法。</summary>
    public static class RHIShaderExtensions
    {
        /// <summary>从字节码创建 shader。</summary>
        public static RHIShader CreateShaderFromBytecode(this RHIDevice device,
                                                        RHIShaderStage stage,
                                                        byte[] bytecode,
                                                        string entryPoint = "main",
                                                        string name = null)
        {
            var desc = new RHIShaderDesc
            {
                Stage = stage,
                Bytecode = bytecode,
                EntryPoint = entryPoint,
                Name = name ?? $"Shader_{stage}",
            };
            return device.CreateShader(desc);
        }

        /// <summary>从源码创建 shader (运行时编译)。</summary>
        public static RHIShader CreateShaderFromSource(this RHIDevice device,
                                                       RHIShaderStage stage,
                                                       string source,
                                                       string entryPoint = "main",
                                                       string name = null)
        {
            var desc = new RHIShaderDesc
            {
                Stage = stage,
                Source = source,
                EntryPoint = entryPoint,
                Name = name ?? $"Shader_{stage}",
            };
            return device.CreateShader(desc);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: Pipeline 创建扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Pipeline 扩展方法。</summary>
    public static class RHIPipelineExtensions
    {
        /// <summary>创建简单的 graphics pipeline。</summary>
        public static RHIPipeline CreateSimpleGraphicsPipeline(this RHIDevice device,
                                                               RHIShader vertexShader,
                                                               RHIShader pixelShader,
                                                               RHIRenderPass renderPass,
                                                               RHIFormat[] rtvFormats,
                                                               RHIFormat dsvFormat = RHIFormat.Unknown,
                                                               string name = null)
        {
            var desc = new RHIGraphicsPipelineDesc
            {
                VertexShader = vertexShader,
                PixelShader = pixelShader,
                RenderPass = renderPass,
                RTVFormats = rtvFormats,
                DSVFormat = dsvFormat,
                Name = name ?? "GraphicsPipeline",
            };
            return device.CreateGraphicsPipeline(desc);
        }

        /// <summary>创建 compute pipeline。</summary>
        public static RHIPipeline CreateComputePipeline(this RHIDevice device,
                                                        RHIShader computeShader,
                                                        string name = null)
        {
            var desc = new RHIComputePipelineDesc
            {
                ComputeShader = computeShader,
                Name = name ?? "ComputePipeline",
            };
            return device.CreateComputePipeline(desc);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 资源生命周期管理扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>资源生命周期扩展。</summary>
    public static class RHIResourceLifetimeExtensions
    {
        /// <summary>带引用计数的资源持有。</summary>
        public static IDisposable HoldResource(this RHIDevice device, RHIResource resource)
        {
            return new ResourceHolder(device, resource);
        }

        private sealed class ResourceHolder : IDisposable
        {
            private readonly RHIDevice _device;
            private readonly RHIResource _resource;
            private bool _disposed;

            public ResourceHolder(RHIDevice device, RHIResource res)
            {
                _device = device;
                _resource = res;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _device.DestroyResource(_resource);
                _disposed = true;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 查询扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>查询扩展方法。</summary>
    public static class RHIDeviceQueryExtensions
    {
        /// <summary>查询设备能力。</summary>
        public static RHIDeviceCapabilities GetCapabilities(this RHIDevice device)
        {
            return new RHIDeviceCapabilities
            {
                MaxTextureSize2D = device.GetMaxTextureSize2D(),
                MaxTextureSize3D = device.GetMaxTextureSize3D(),
                MaxTextureSizeCube = device.GetMaxTextureSizeCube(),
                MaxTextureArrayLayers = device.GetMaxTextureArrayLayers(),
                MaxBufferBindings = device.GetMaxBufferBindings(),
                MaxSamplerBindings = device.GetMaxSamplerBindings(),
                MaxUAVBindings = device.GetMaxUAVBindings(),
                MaxUniformBufferSize = device.GetMaxUniformBufferSize(),
                MaxStorageBufferSize = device.GetMaxStorageBufferSize(),
                UniformBufferAlignment = device.GetUniformBufferAlignment(),
                StorageBufferAlignment = device.GetStorageBufferAlignment(),
                TexelBufferAlignment = device.GetTexelBufferAlignment(),
                MaxComputeWorkgroupX = device.GetMaxComputeWorkgroupX(),
                MaxComputeWorkgroupY = device.GetMaxComputeWorkgroupY(),
                MaxComputeWorkgroupZ = device.GetMaxComputeWorkgroupZ(),
                MaxComputeWorkgroupSize = device.GetMaxComputeWorkgroupSize(),
                SupportsTessellation = device.SupportsTessellation(),
                SupportsGeometryShader = device.SupportsGeometryShader(),
                SupportsMeshShader = device.SupportsMeshShader(),
                SupportsRayTracing = device.SupportsRayTracing(),
                SupportsVariableRateShading = device.SupportsVariableRateShading(),
                SupportsBindless = device.SupportsBindless(),
            };
        }

        /// <summary>查询支持的格式特性。</summary>
        public static RHIFormatProperties GetFormatProperties(this RHIDevice device, RHIFormat format)
        {
            return device.QueryFormatProperties(format);
        }

        /// <summary>判断格式是否支持某种用途。</summary>
        public static bool SupportsFormatUsage(this RHIDevice device, RHIFormat format,
                                               RHIFormatUsage usage)
        {
            var props = device.QueryFormatProperties(format);
            return usage switch
            {
                RHIFormatUsage.RenderTarget => props.SupportsRenderTarget,
                RHIFormatUsage.DepthStencil => props.SupportsDepthStencil,
                RHIFormatUsage.ShaderResource => props.SupportsShaderResource,
                RHIFormatUsage.UnorderedAccess => props.SupportsUnorderedAccess,
                RHIFormatUsage.Blendable => props.SupportsBlendable,
                RHIFormatUsage.Multisampling => props.SupportsMultisampling,
                _ => false,
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: Fence/Sync 扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>同步扩展。</summary>
    public static class RHISyncExtensions
    {
        /// <summary>创建 signaled fence。</summary>
        public static RHIFence CreateSignaledFence(this RHIDevice device, ulong initialValue = 1)
        {
            var desc = new RHIFenceDesc
            {
                InitialValue = initialValue,
                Name = "Fence",
            };
            return device.CreateFence(desc);
        }

        /// <summary>等待并重置 fence。</summary>
        public static bool WaitAndReset(this RHIDevice device, RHIFence fence,
                                        ulong timeoutMs = 1000)
        {
            if (!device.WaitForFence(fence, timeoutMs)) return false;
            device.ResetFence(fence);
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 设备信息扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>设备信息扩展。</summary>
    public static class RHIDeviceInfoExtensions
    {
        /// <summary>获取设备描述字符串。</summary>
        public static string GetDeviceInfoString(this RHIDevice device)
        {
            var info = device.GetDeviceInfo();
            return $"{info.DeviceName} ({info.VendorName}) - " +
                   $"Backend: {info.Backend}, " +
                   $"VRAM: {info.DedicatedVideoMemory / 1024 / 1024}MB, " +
                   $"Driver: {info.DriverVersion}";
        }

        /// <summary>获取 backend 类型字符串。</summary>
        public static string GetBackendName(this RHIDevice device)
        {
            return device.GetBackend().ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: 便捷创建容器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>资源容器。一组相关资源，统一销毁。</summary>
    public sealed class RHIResourceSet : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly List<RHIResource> _resources = new();
        private readonly object _lock = new();
        private bool _disposed;

        public RHIResourceSet(RHIDevice device) { _device = device; }

        public T Add<T>(T resource) where T : RHIResource
        {
            lock (_lock)
            {
                if (!_disposed) _resources.Add(resource);
            }
            return resource;
        }

        public void Remove(RHIResource resource)
        {
            lock (_lock) _resources.Remove(resource);
        }

        public int Count
        {
            get { lock (_lock) return _resources.Count; }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                foreach (var r in _resources)
                    _device.DestroyResource(r);
                _resources.Clear();
                _disposed = true;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: 调试扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>调试扩展。</summary>
    public static class RHIDebugExtensions
    {
        /// <summary>设置对象名字。</summary>
        public static void SetName(this RHIDevice device, RHIResource resource, string name)
        {
            device.SetResourceName(resource, name);
        }

        /// <summary>开始 debug group。</summary>
        public static void BeginEvent(this RHICommandList cmd, string name)
        {
            cmd.BeginDebugEvent(name, 0, 0, 0, 1);
        }

        /// <summary>结束 debug group。</summary>
        public static void EndEvent(this RHICommandList cmd)
        {
            cmd.EndDebugEvent();
        }

        /// <summary>using 块包装的 debug event。</summary>
        public static RHIEventScope EventScope(this RHICommandList cmd, string name)
        {
            return new RHIEventScope(cmd, name);
        }
    }

    /// <summary>using 块包装的 event。</summary>
    public readonly struct RHIEventScope : IDisposable
    {
        private readonly RHICommandList _cmd;
        public RHIEventScope(RHICommandList cmd, string name)
        {
            _cmd = cmd;
            cmd.BeginEvent(name);
        }
        public void Dispose() => _cmd?.EndEvent();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 12: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class RHIDeviceExtensionsVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine RHI Device Extensions";
    }
}

// 文件结束 - RHIDeviceExtensions.cs
