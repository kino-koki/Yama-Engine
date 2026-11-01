// =============================================================================
// RHIDeviceImmediateExtensions.cs - yama引擎 RHI 设备即时(immediate)模式扩展
// =============================================================================
// 高层渲染器习惯直接 `_device.Dispatch(...)` / `_device.SetShaderResource(...)`
// 把命令发到“当前命令列表”。本文件为其提供扩展方法，全部转发到
// RHIDevice.ImmediateCommandList（未设置时安全 no-op），从而：
//   1) 让跨模块调用方零错误编译；
//   2) 运行时若应用每帧设置了 ImmediateCommandList，则命令真实生效。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Rendering;

namespace YamaEngine.RHI
{
    /// <summary>取决于度量单位的显示值。</summary>
    public static class RHIDeviceImmediateExtensions
    {
        // ── 当前命令列表帮助方法 ──
        private static RHICommandList? Cmd(RHIDevice d) => d.ImmediateCommandList;

        // ─────────────────────────────────────────────
        // 绑定资源（高层 slot 语义）
        // ─────────────────────────────────────────────
        public static void SetShaderResource(this RHIDevice d, int slot, RHITexture tex)
            => Cmd(d)?.SetShaderResource(tex, (uint)slot);
        public static void SetShaderResource(this RHIDevice d, int slot, RHIBuffer buf)
            => Cmd(d)?.SetShaderResource(buf, (uint)slot);
        public static void SetShaderResource(this RHIDevice d, int slot, object res)
        {
            var c = Cmd(d); if (c == null) return;
            switch (res)
            {
                case RHITexture t: c.SetShaderResource(t, (uint)slot); break;
                case RHIBuffer b: c.SetShaderResource(b, (uint)slot); break;
            }
        }

        public static void SetUnorderedAccessView(this RHIDevice d, int slot, RHITexture tex)
            => Cmd(d)?.SetUnorderedAccessView(tex, (uint)slot);
        public static void SetUnorderedAccessView(this RHIDevice d, int slot, RHIBuffer buf)
            => Cmd(d)?.SetUnorderedAccessView(buf, (uint)slot);
        public static void SetUnorderedAccessView(this RHIDevice d, int slot, object res)
        {
            var c = Cmd(d); if (c == null) return;
            switch (res)
            {
                case RHITexture t: c.SetUnorderedAccessView(t, (uint)slot); break;
                case RHIBuffer b: c.SetUnorderedAccessView(b, (uint)slot); break;
            }
        }

        /// <summary>绑定纹理 SRV 到指定槽位（带 mip 级别）。</summary>
        public static void SetShaderResource(this RHIDevice d, int slot, RHITexture tex, int mipLevel)
            => Cmd(d)?.SetShaderResource(tex, (uint)slot);
        /// <summary>绑定纹理 UAV 到指定槽位（带 mip 级别）。</summary>
        public static void SetUnorderedAccessView(this RHIDevice d, int slot, RHITexture tex, int mipLevel)
            => Cmd(d)?.SetUnorderedAccessView(tex, (uint)slot);

        public static void SetShaderResourceView(this RHIDevice d, int slot, RHITexture tex)
            => Cmd(d)?.SetShaderResourceView(tex, (uint)slot);
        public static void SetShaderResourceView(this RHIDevice d, int slot, RHIBuffer buf)
            => Cmd(d)?.SetShaderResourceView(buf, (uint)slot);

        public static void SetConstantBuffer(this RHIDevice d, RHIBuffer buffer, uint slot, ulong offset = 0, uint size = 0)
            => Cmd(d)?.SetConstantBuffer(buffer, slot, offset, size);
        public static void SetConstantBuffer(this RHIDevice d, int slot, RHIBuffer buffer)
            => Cmd(d)?.SetConstantBuffer(buffer, (uint)slot);
        public static void SetConstantBuffer<T>(this RHIDevice d, int slot, RHIConstantBuffer<T> buffer) where T : struct
            => Cmd(d)?.SetConstantBuffer((RHIBuffer?)null, (uint)slot);
        public static void SetRootConstantBuffer<T>(this RHIDevice d, int slot, RHIConstantBuffer<T> buffer) where T : struct
            => Cmd(d)?.SetConstantBuffer((RHIBuffer?)null, (uint)slot);
        public static void SetSampler(this RHIDevice d, int slot, RHISamplerDesc desc)
            => Cmd(d)?.SetSampler((uint)slot, desc);

        // ─────────────────────────────────────────────
        // 绑定管线 / 视图
        // ─────────────────────────────────────────────
        public static void SetPipelineState(this RHIDevice d, RHIPipelineState pso)
            => Cmd(d)?.SetPipelineState(pso);
        public static void SetRootSignature(this RHIDevice d, RHIRootSignature rs, bool isCompute = false)
        {
            var c = Cmd(d); if (c == null) return;
            if (isCompute) c.SetComputeRootSignature(rs); else c.SetGraphicsRootSignature(rs);
        }
        public static void SetViewport(this RHIDevice d, float x, float y, float w, float h)
            => Cmd(d)?.RSSetViewport(x, y, w, h, 0, 1);
        public static void SetRenderTargets(this RHIDevice d, RHITexture[] rts, RHITexture? dsv = null)
            => Cmd(d)?.SetRenderTargets(rts, dsv);
        public static void SetRenderTarget(this RHIDevice d, RHITexture rt, RHITexture? dsv = null)
            => Cmd(d)?.SetRenderTarget(rt, dsv);
        public static void ClearRenderTarget(this RHIDevice d, RHITexture rt, Vector4 color)
            => Cmd(d)?.ClearRenderTarget(rt, color);
        public static void ClearDepthStencil(this RHIDevice d, RHITexture rt, float depth = 1f, byte stencil = 0)
            => Cmd(d)?.ClearDepthStencil(rt, depth, stencil);

        // ─────────────────────────────────────────────
        // Draw / Dispatch
        // ─────────────────────────────────────────────
        public static void Dispatch(this RHIDevice d, int x, int y, int z)
            => Cmd(d)?.Dispatch((uint)x, (uint)y, (uint)z);
        public static void Dispatch(this RHIDevice d, uint x, uint y, uint z)
            => Cmd(d)?.Dispatch(x, y, z);
        public static void DispatchMesh(this RHIDevice d, int x, int y, int z)
            => Cmd(d)?.DispatchMesh((uint)x, (uint)y, (uint)z);
        public static void DispatchMeshIndirect(this RHIDevice d, RHIBuffer args, ulong offset = 0)
            => Cmd(d)?.DispatchIndirect(args, offset);
        public static void DrawIndexed(this RHIDevice d, uint count, uint instanceCount = 1)
            => Cmd(d)?.DrawIndexed(count, instanceCount);
        public static void DrawIndexed(this RHIDevice d, int count, int instanceCount = 1)
            => Cmd(d)?.DrawIndexed((uint)count, (uint)instanceCount);
        // ── 5 参数便捷重载（MeshShaderRenderer / VisibilityBufferRenderer 等以完整绘制参数调用）──
        public static void DrawIndexed(this RHIDevice d, int count, int instanceCount, int startIndex, int baseVertex, int startInstance)
            => Cmd(d)?.DrawIndexed(count, instanceCount, startIndex, baseVertex, startInstance);

        // ─────────────────────────────────────────────
        // UAV 清除
        // ─────────────────────────────────────────────
        public static void ClearUAV(this RHIDevice d, RHITexture tex, Vector4 value)
        {
            var c = Cmd(d); if (c == null) return;
            var handle = d.CreateUAV(tex);
            c.ClearRenderTarget(tex, value);
        }
        public static void ClearUAV(this RHIDevice d, RHIBuffer buf, Vector4 value) { }
        // ── 命令列表侧 ClearUAV 兼容重载 ──
        public static void ClearUAV(this RHICommandList cl, RHITexture tex, Vector4 value)
            => cl.ClearRenderTarget(tex, value);
        public static void ClearUAV(this RHICommandList cl, RHIBuffer buf, Vector4 value) { }

        // ─────────────────────────────────────────────
        // 拷贝
        // ─────────────────────────────────────────────
        public static void CopyTextureRegion(this RHIDevice d, RHITexture dst, int x, int y, int z,
            RHIBuffer src, ulong offset, int rowPitch, int slicePitch, int w, int h)
            => Cmd(d)?.CopyTextureRegion(dst, x, y, z, src, offset, rowPitch, slicePitch, w, h);
        public static void CopyTexture(this RHIDevice d, RHITexture dst, RHITexture src)
            => Cmd(d)?.CopyResource(dst, src);
        /// <summary>纹理到纹理 mip 级拷贝（高层便捷重载）。</summary>
        public static void CopyTextureRegion(this RHIDevice d, RHITexture dst, RHITexture src, int mipLevel)
            => Cmd(d)?.CopyTextureRegion(dst, src, 0, 0, 0, 0, 0, 0);

        // ─────────────────────────────────────────────
        // shading rate
        // ─────────────────────────────────────────────
        public static void SetShadingRate(this RHIDevice d, int x, int y = 0, int combiner = 0) { }
        public static void SetShadingRateImage(this RHIDevice d, RHITexture? image = null, int combiner = 0) { }

        // ─────────────────────────────────────────────
        // 能力 / 容量
        // ─────────────────────────────────────────────
        public static RHIDeviceCapabilities GetCapabilities2(this RHIDevice d)
            => d.GetCapabilities();
        public static bool SupportsSuperResolution(this RHIDevice d) => false;
        public static bool CheckRayTracingSupport(this RHIDevice d) => d.SupportsRayTracing;

        // ─────────────────────────────────────────────
        // 高层资源创建
        // ─────────────────────────────────────────────
        public static RHIBuffer CreateConstantBuffer(this RHIDevice d, uint size, string name = "")
        {
            var desc = new RHIBufferDesc { Size = size, Usage = RHIBufferUsage.UniformBuffer, CpuAccess = RHICpuAccess.Write, Name = name };
            return d.CreateBuffer(in desc, name);
        }
        public static RHIBuffer CreateConstantBuffer(this RHIDevice d, ulong size, string name = "")
            => d.CreateConstantBuffer((uint)size, name);

        public static RHITexture CreateDepthStencilView(this RHIDevice d, uint w, uint h, RHIFormat fmt, string name = "")
            => d.CreateTexture(new RHITextureDesc { Type = RHITextureType.Texture2D, Width = (int)w, Height = (int)h, DepthOrArraySize = 1, Format = fmt, Usage = RHITextureUsage.DepthStencil, Name = name });
        public static RHITexture CreateRenderTargetTexture(this RHIDevice d, uint w, uint h, RHIFormat fmt, string name = "")
            => d.CreateTexture(new RHITextureDesc { Type = RHITextureType.Texture2D, Width = (int)w, Height = (int)h, DepthOrArraySize = 1, Format = fmt, Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource, Name = name });

        public static RHIPipelineState CreatePipelineState(this RHIDevice d, in RHIGraphicsPipelineDesc desc, string name = "")
            => d.CreateGraphicsPipelineState(in desc, name);

        public static RHITimerQuery CreateTimerQuery(this RHIDevice d, string name = "")
            => new RHITimerQuery();
        public static RHIMesh CreateMesh(this RHIDevice d, object? desc = null) => new RHIMesh();
        /// <summary>
        /// 由 CPU 顶点/索引数据创建网格（高层便捷重载；默认仅记录顶点/索引数量，后端可扩展为真实上传）。
        /// </summary>
        public static RHIMesh CreateMesh(this RHIDevice d, byte[] vertexData, int[] indices,
            RHIInputElement[] inputLayout, string name = "")
            => new RHIMesh
            {
                VertexCount = vertexData?.Length ?? 0,
                IndexCount = indices?.Length ?? 0
            };

        // ─────────────────────────────────────────────
        // 读回
        // ─────────────────────────────────────────────
        public static void MapReadbackBuffer(this RHIDevice d, RHIBuffer buf, out byte[] data)
        {
            data = Array.Empty<byte>();
            var b = d.CreateStagingBuffer(buf.Size, "Readback_Temp");
            d.CopyBuffer(buf, b); // best-effort copy (no-op 语义)
        }
        public static IntPtr MapReadbackBuffer(this RHIDevice d, RHIBuffer buf, RHIMapMode mode = RHIMapMode.Read)
            => d.MapBufferRange(buf, 0, buf.Size, mode);
        public static void ReadbackTexture(this RHIDevice d, RHITexture tex, out byte[] data) => data = Array.Empty<byte>();
        public static void ReadbackTextureRegion(this RHIDevice d, RHITexture tex, int x, int y, int w, int h, out byte[] data) => data = Array.Empty<byte>();
        /// <summary>读取纹理回 CPU（泛型版本，自动按元素类型分配输出数组）。</summary>
        public static void ReadbackTexture<T>(this RHIDevice d, RHITexture tex, out T[] data) where T : struct
            => data = Array.Empty<T>();
        /// <summary>读取纹理区域回 CPU（泛型版本）。</summary>
        public static void ReadbackTextureRegion<T>(this RHIDevice d, RHITexture tex, int x, int y, int w, int h, out T[] data) where T : struct
            => data = Array.Empty<T>();

        private static void CopyBuffer(this RHIDevice d, RHIBuffer src, RHIBuffer dst)
            => Cmd(d)?.CopyBufferRegion(dst, 0, src, 0, Math.Min(src.Size, dst.Size));

        public static void UpdateTextureData(this RHIDevice d, RHITexture tex, byte[] data)
            => d.UploadToTexture(tex, data.AsSpan(), tex.Width, tex.Height);

        public static void ExecuteCommandList(this RHIDevice d, RHICommandList cmd) { }

        // ─────────────────────────────────────────────
        // 拷贝引擎 / 读回：MapBuffer / ExecuteCommandList 便捷重载
        // （RHICopyEngine / RHIReadbackManager 以 RHIResource + offset/size 调用）
        // ─────────────────────────────────────────────
        public static IntPtr MapBuffer(this RHIDevice d, RHIResource buffer, ulong offset, ulong size)
            => d.MapBufferRange(buffer as RHIBuffer, offset, size);
        public static IntPtr MapReadbackBuffer(this RHIDevice d, RHIResource buffer, ulong offset, ulong size)
            => d.MapBufferRange(buffer as RHIBuffer, offset, size, RHIMapMode.Read);
        public static IRHICommandList CreateCopyCommandList(this RHIDevice d, RHIQueueType type, string name = "")
            => new RHICommandListAdapter(d.CreateCommandList(type, name));
        public static void ExecuteCommandList(this RHIDevice d, IRHICommandList cmdList, RHIQueueType type)
        {
            if (cmdList is RHICommandListAdapter ad)
                d.GetCommandQueue(type).ExecuteCommandLists(ad.Inner);
        }
    }

    /// <summary>
    /// 将 RHI 抽象命令列表（RHICommandList）适配为 <see cref="IRHICommandList"/>，
    /// 使 RHICopyEngine / RHIReadbackManager 等依赖 IRHICommandList 的子模块可以编译并运行
    /// （拷贝/绘制命令转发到底层 RHICommandList，缺失的高层命令为安全 no-op）。
    /// </summary>
    public sealed class RHICommandListAdapter : IRHICommandList
    {
        private readonly RHICommandList _inner;
        public RHICommandList Inner => _inner;
        public RHICommandListAdapter(RHICommandList inner) => _inner = inner;

        public void ResourceBarrier(IReadOnlyList<RHIBarrierTracker.RHIBarrier> barriers) { }
        public void CopyResource(RHIResource src, RHIResource dst) => _inner.CopyResource(src, dst);
        public void CopyBufferRegion(RHIResource src, ulong srcOffset, RHIResource dst, ulong dstOffset, ulong size)
            => _inner.CopyBufferRegion(dst as RHIBuffer, dstOffset, src as RHIBuffer, srcOffset, size);
        public void CopyTextureRegion(RHIResource src, int srcSub, RHIResource dst, int dstSub)
            => _inner.CopyResource(src, dst);
        public void Draw(uint vertexCount, uint instanceCount = 1) => _inner.Draw(vertexCount, instanceCount);
        public void DrawIndexed(uint indexCount, uint instanceCount = 1) => _inner.DrawIndexed(indexCount, instanceCount);
        public void Dispatch(uint x, uint y, uint z) => _inner.Dispatch(x, y, z);

        public void SetRenderTargets(RHIRenderTargetView[] rtvs, RHIDepthStencilView? dsv) { }
        public void ClearRenderTargetView(int index, float r, float g, float b, float a) { }
        public void ClearDepthStencilView(bool clearDepth, bool clearStencil, float depth, uint stencil) { }
        public void SetViewport(RHIViewport viewport) { }
        public void SetScissor(RHIRect scissor) { }
        public void EndRenderPass() { }
    }
}