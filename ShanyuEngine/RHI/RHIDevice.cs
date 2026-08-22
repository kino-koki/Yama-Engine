using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.RHI
{
    // ──────────────────────────────────────────────────────────────
    // Base resource interface
    // ──────────────────────────────────────────────────────────────

    public abstract class RHIResource : IDisposable
    {
        public virtual ulong GpuVirtualAddress => 0;
        public virtual RHIBufferDesc? BufferDesc => null;
        public virtual RHITextureDesc? TextureDesc => null;
        public abstract RHIResourceState CurrentState { get; set; }
        public abstract string Name { get; set; }
        public abstract ulong AllocatedBytes { get; }
        public abstract void Dispose();
    }

    public abstract class RHIBuffer : RHIResource { }
    public abstract class RHITexture : RHIResource { }

    // ──────────────────────────────────────────────────────────────
    // Descriptor heap and handles
    // ──────────────────────────────────────────────────────────────


    public abstract class RHIDescriptorHeap : IDisposable
    {
        public virtual RHIDescriptorHeapType Type { get; protected set; }
        public virtual int Capacity { get; protected set; }
        public virtual uint DescriptorSize { get; protected set; }
        public virtual int AllocatedCount => 0;
        public virtual bool ShaderVisible => false;
        public virtual string Name { get; set; } = string.Empty;
        public RHIDescriptorHandle StartHandle { get; protected set; }

        public abstract RHIDescriptorHandle Allocate();
        public abstract void Free(RHIDescriptorHandle handle);
        public abstract void Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // Root signature
    // ──────────────────────────────────────────────────────────────

    public abstract class RHIRootSignature : IDisposable
    {
        public virtual int NumParameters => 0;
        public abstract string Name { get; set; }
        public abstract void Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // Pipeline state
    // ──────────────────────────────────────────────────────────────

    public abstract class RHIPipelineState : IDisposable
    {
        public abstract string Name { get; set; }
        public abstract RHIPipelineType PipelineType { get; }
        public abstract void Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // Fence
    // ──────────────────────────────────────────────────────────────

    public abstract class RHIFence : IDisposable
    {
        public virtual string Name { get; set; } = string.Empty;
        public abstract ulong CompletedValue { get; }
        public abstract void Signal(ulong value);
        public virtual bool SetEventOnCompletion(ulong value, IntPtr waitEvent) => false;
        public abstract void Wait(ulong value, uint timeoutMs = uint.MaxValue);
        public abstract void Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // Swap chain
    // ──────────────────────────────────────────────────────────────

    public abstract class RHISwapChain : IDisposable
    {
        public virtual int Width { get; protected set; }
        public virtual int Height { get; protected set; }
        public virtual RHIFormat Format { get; protected set; }
        public virtual int BufferCount { get; protected set; }
        public abstract int CurrentBackBufferIndex { get; }
        public virtual string Name { get; set; } = string.Empty;

        public abstract RHITexture GetBackBuffer(int index);
        public abstract RHIDescriptorHandle GetBackBufferRTV(int index);
        public abstract RHITexture GetCurrentBackBuffer();
        public abstract RHIDescriptorHandle GetCurrentBackBufferRTV();
        public abstract void Present(bool vsync);
        public abstract void Resize(int newWidth, int newHeight);
        public abstract void Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // Command queue
    // ──────────────────────────────────────────────────────────────

    public abstract class RHICommandQueue : IDisposable
    {
        public virtual RHIQueueType QueueType { get; protected set; }
        public virtual string Name { get; set; } = string.Empty;
        public abstract void ExecuteCommandLists(params RHICommandList[] commandLists);
        public abstract void Signal(RHIFence fence, ulong value);
        public abstract void Wait(RHIFence fence, ulong value);
        public abstract void Flush();
        public abstract void Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // Command list / encoder
    // ──────────────────────────────────────────────────────────────

    public abstract class RHICommandList : IDisposable
    {
        public abstract string Name { get; set; }
        public virtual RHIQueueType QueueType { get; protected set; }

        // Recording
        public virtual void Open() { }
        public abstract void Reset(RHIPipelineState? initialState = null);
        public abstract void Close();

        // Resource barriers
        public abstract void ResourceBarrier(RHIResource resource, RHIResourceState stateBefore, RHIResourceState stateAfter, uint subresource = 0xFFFFFFFF);
        public abstract void UAVBarrier(RHIResource? resource);

        // Copy
        public abstract void CopyBufferRegion(RHIBuffer dst, ulong dstOffset, RHIBuffer src, ulong srcOffset, ulong numBytes);
        public abstract void CopyResource(RHIResource dst, RHIResource src);
        public abstract void CopyTextureRegion(RHITexture dst, uint dstSubresource, uint dstX, uint dstY, uint dstZ,
            RHIBuffer src, ulong srcOffset, uint srcRowPitch, uint srcSlicePitch, uint srcWidth, uint srcHeight);

        // Clear
        public abstract void ClearRenderTargetView(RHIDescriptorHandle rtvHandle, in Vector4 clearColor);
        public abstract void ClearDepthStencilView(RHIDescriptorHandle dsvHandle, bool clearDepth, bool clearStencil, float depth, byte stencil);

        // Render pass
        public abstract void OMSetRenderTargets(RHIDescriptorHandle[] rtvHandles, RHIDescriptorHandle? dsvHandle);
        public abstract void RSSetViewport(float x, float y, float width, float height, float minDepth, float maxDepth);
        public abstract void RSSetViewports(RHIViewport[] viewports);
        public abstract void RSSetScissorRect(int left, int top, int right, int bottom);
        public abstract void RSSetScissorRects(RHIRect[] rects);

        // Pipeline binding
        public abstract void SetPipelineState(RHIPipelineState pso);
        public abstract void SetGraphicsRootSignature(RHIRootSignature rootSig);
        public abstract void SetComputeRootSignature(RHIRootSignature rootSig);
        public abstract void SetGraphicsRootDescriptorTable(uint rootParameterIndex, RHIDescriptorHandle handle);
        public abstract void SetComputeRootDescriptorTable(uint rootParameterIndex, RHIDescriptorHandle handle);
        public abstract void SetGraphicsRootConstantBufferView(uint rootParameterIndex, RHIBuffer buffer);
        public abstract void SetComputeRootConstantBufferView(uint rootParameterIndex, RHIBuffer buffer);
        public abstract void SetGraphicsRootShaderResourceView(uint rootParameterIndex, RHIBuffer buffer);
        public abstract void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, RHIBuffer buffer);
        public virtual void SetComputeRootShaderResourceView(uint rootParameterIndex, RHIBuffer buffer) { }
        public virtual void SetComputeRootUnorderedAccessView(uint rootParameterIndex, RHIBuffer buffer) { }
        public abstract unsafe void SetGraphicsRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues, in T data, uint destOffsetIn32BitValues) where T : struct;
        public abstract unsafe void SetComputeRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues, in T data, uint destOffsetIn32BitValues) where T : struct;

        // IA
        public abstract void IASetPrimitiveTopology(RHIPrimitiveTopology topology);
        public abstract void IASetVertexBuffers(uint startSlot, RHIBuffer buffer, ulong offset, uint stride, uint sizeInBytes);
        public abstract void IASetIndexBuffer(RHIBuffer buffer, ulong offset, uint sizeInBytes, RHIFormat format);

        // Draw
        public abstract void DrawInstanced(uint vertexCountPerInstance, uint instanceCount, uint startVertex, uint startInstance);
        public abstract void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount, uint startIndex, int baseVertex, uint startInstance);
        public abstract void DrawIndexedInstancedIndirect(RHIBuffer argsBuffer, ulong argsOffset);
        public abstract void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ);
        public abstract void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);
        public abstract void DispatchIndirect(RHIBuffer argsBuffer, ulong argsOffset);

        // Stencil / Blend
        public abstract void OMSetStencilRef(uint refValue);
        public abstract void OMSetBlendFactor(in Vector4 blendFactor);

        // Resource transition helpers
        public void TransitionBarrier(RHIResource res, RHIResourceState before, RHIResourceState after)
            => ResourceBarrier(res, before, after);

        public void TransitionTo(RHIResource res, RHIResourceState targetState)
        {
            if (res.CurrentState != targetState)
            {
                ResourceBarrier(res, res.CurrentState, targetState);
                res.CurrentState = targetState;
            }
        }

        // ── Backward-compatibility aliases (old API names without RS prefix) ──
        public void SetViewport(float x, float y, float w, float h, float minZ = 0f, float maxZ = 1f)
            => RSSetViewport(x, y, w, h, minZ, maxZ);
        public void SetViewport(RHIViewport vp) => RSSetViewport(vp.TopLeftX, vp.TopLeftY, vp.Width, vp.Height, vp.MinDepth, vp.MaxDepth);
        public void SetViewports(RHIViewport[] vps) => RSSetViewports(vps);
        public void SetScissorRect(int left, int top, int right, int bottom) => RSSetScissorRect(left, top, right, bottom);
        public void SetScissorRects(RHIRect[] rects) => RSSetScissorRects(rects);

        // ── int overloads (for code that passes int instead of uint) ──
        public virtual void CopyTextureRegion(RHITexture dst, int dstSubresource, int dstX, int dstY, int dstZ,
            RHIBuffer src, ulong srcOffset, int srcRowPitch, int srcSlicePitch, int srcWidth, int srcHeight)
            => CopyTextureRegion(dst, (uint)dstSubresource, (uint)dstX, (uint)dstY, (uint)dstZ,
               src, srcOffset, (uint)srcRowPitch, (uint)srcSlicePitch, (uint)srcWidth, (uint)srcHeight);

        public virtual void ClearDepthStencilView(RHIDescriptorHandle dsvHandle, float depth, byte stencil)
            => ClearDepthStencilView(dsvHandle, true, true, depth, stencil);

        public virtual void ClearRenderTargetView(RHIDescriptorHandle rtvHandle, Vector4 clearColor)
            => ClearRenderTargetView(rtvHandle, in clearColor);

        public virtual void SetGraphicsRootDescriptorTable(int rootParameterIndex, RHIDescriptorHandle handle)
            => SetGraphicsRootDescriptorTable((uint)rootParameterIndex, handle);
        public virtual void SetComputeRootDescriptorTable(int rootParameterIndex, RHIDescriptorHandle handle)
            => SetComputeRootDescriptorTable((uint)rootParameterIndex, handle);
        public virtual void SetGraphicsRootConstantBufferView(int rootParameterIndex, RHIBuffer buffer)
            => SetGraphicsRootConstantBufferView((uint)rootParameterIndex, buffer);
        public virtual void SetComputeRootConstantBufferView(int rootParameterIndex, RHIBuffer buffer)
            => SetComputeRootConstantBufferView((uint)rootParameterIndex, buffer);
        public virtual void SetGraphicsRootShaderResourceView(int rootParameterIndex, RHIBuffer buffer)
            => SetGraphicsRootShaderResourceView((uint)rootParameterIndex, buffer);
        public virtual void SetGraphicsRootUnorderedAccessView(int rootParameterIndex, RHIBuffer buffer)
            => SetGraphicsRootUnorderedAccessView((uint)rootParameterIndex, buffer);

        // ulong GPU-address overloads (for legacy code that passes gpuVirtualAddress)
        public virtual void SetGraphicsRootConstantBufferView(uint rootParameterIndex, ulong gpuAddress) { }
        public virtual void SetComputeRootConstantBufferView(uint rootParameterIndex, ulong gpuAddress) { }
        public virtual void SetGraphicsRootShaderResourceView(uint rootParameterIndex, ulong gpuAddress) { }
        public virtual void SetComputeRootShaderResourceView(uint rootParameterIndex, ulong gpuAddress) { }
        public virtual void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, ulong gpuAddress) { }
        public virtual void SetComputeRootUnorderedAccessView(uint rootParameterIndex, ulong gpuAddress) { }

        public virtual void IASetVertexBuffers(int startSlot, RHIBuffer buffer, ulong offset, int stride, int sizeInBytes)
            => IASetVertexBuffers((uint)startSlot, buffer, offset, (uint)stride, (uint)sizeInBytes);
        public virtual void IASetVertexBuffers(int startSlot, RHIBuffer buffer, int stride)
            => IASetVertexBuffers((uint)startSlot, buffer, 0, (uint)stride, (uint)buffer.AllocatedBytes);
        public virtual void IASetIndexBuffer(RHIBuffer buffer, int sizeInBytes, RHIFormat format)
            => IASetIndexBuffer(buffer, 0, (uint)sizeInBytes, format);
        public virtual void IASetIndexBuffer(RHIBuffer buffer, RHIFormat format)
            => IASetIndexBuffer(buffer, 0, (uint)buffer.AllocatedBytes, format);

        public virtual void DrawInstanced(int vertexCountPerInstance, int instanceCount, int startVertex, int startInstance)
            => DrawInstanced((uint)vertexCountPerInstance, (uint)instanceCount, (uint)startVertex, (uint)startInstance);
        public virtual void DrawIndexedInstanced(int indexCountPerInstance, int instanceCount, int startIndex, int baseVertex, int startInstance)
            => DrawIndexedInstanced((uint)indexCountPerInstance, (uint)instanceCount, (uint)startIndex, baseVertex, (uint)startInstance);
        public virtual void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount, uint startIndex, uint baseVertex, uint startInstance)
            => DrawIndexedInstanced(indexCountPerInstance, instanceCount, startIndex, (int)baseVertex, startInstance);
        public virtual void DispatchMesh(int x, int y, int z) => DispatchMesh((uint)x, (uint)y, (uint)z);
        public virtual void Dispatch(int x, int y, int z) => Dispatch((uint)x, (uint)y, (uint)z);

        public virtual void OMSetStencilRef(int refValue) => OMSetStencilRef((uint)refValue);

        // ── Additional compatibility overloads ──

        // SetScissorRect accepting RHIScissorRect
        public void SetScissorRect(RHIScissorRect rect) => RSSetScissorRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        public void RSSetScissorRect(RHIScissorRect rect) => RSSetScissorRect(rect.Left, rect.Top, rect.Right, rect.Bottom);

        // SetScissorRects accepting RHIScissorRect[]
        public void SetScissorRects(RHIScissorRect[] rects)
        {
            var r = new RHIRect[rects.Length];
            for (int i = 0; i < rects.Length; i++)
                r[i] = new RHIRect(rects[i].Left, rects[i].Top, rects[i].Right, rects[i].Bottom);
            RSSetScissorRects(r);
        }
        public void RSSetScissorRects(RHIScissorRect[] rects)
        {
            var r = new RHIRect[rects.Length];
            for (int i = 0; i < rects.Length; i++)
                r[i] = new RHIRect(rects[i].Left, rects[i].Top, rects[i].Right, rects[i].Bottom);
            RSSetScissorRects(r);
        }

        // IASetVertexBuffers with offset (4-param)
        public virtual void IASetVertexBuffers(int startSlot, RHIBuffer buffer, int stride, ulong offset)
            => IASetVertexBuffers((uint)startSlot, buffer, offset, (uint)stride, (uint)buffer.AllocatedBytes);

        // IASetIndexBuffer with format + offset (3-param)
        public virtual void IASetIndexBuffer(RHIBuffer buffer, RHIFormat format, ulong offset)
            => IASetIndexBuffer(buffer, offset, (uint)buffer.AllocatedBytes, format);

        // ClearDepthStencilView with clearFlags (4-param, D3D12 compat)
        public virtual void ClearDepthStencilView(RHIDescriptorHandle dsvHandle, float depth, byte stencil, int clearFlags)
            => ClearDepthStencilView(dsvHandle, true, true, depth, stencil);

        // Compute root view int overloads (for code passing int index)
        public virtual void SetComputeRootShaderResourceView(int rootParameterIndex, RHIBuffer buffer)
            => SetComputeRootShaderResourceView((uint)rootParameterIndex, buffer);
        public virtual void SetComputeRootShaderResourceView(int rootParameterIndex, ulong gpuAddress)
            => SetComputeRootShaderResourceView((uint)rootParameterIndex, gpuAddress);
        public virtual void SetComputeRootUnorderedAccessView(int rootParameterIndex, RHIBuffer buffer)
            => SetComputeRootUnorderedAccessView((uint)rootParameterIndex, buffer);
        public virtual void SetComputeRootUnorderedAccessView(int rootParameterIndex, ulong gpuAddress)
            => SetComputeRootUnorderedAccessView((uint)rootParameterIndex, gpuAddress);

        // UpdateSubresources helper (compatibility)
        public virtual void UpdateSubresources(RHIResource dst, RHIResource src, ulong dstOffset, ulong srcOffset, ulong numBytes)
            => CopyBufferRegion((dst as RHIBuffer)!, dstOffset, (src as RHIBuffer)!, srcOffset, numBytes);

        // UpdateSubresources 3-param (compatibility)
        public virtual void UpdateSubresources(RHIResource dst, RHIResource src, ulong dstOffset)
            => CopyBufferRegion((dst as RHIBuffer)!, dstOffset, (src as RHIBuffer)!, 0, (src as RHIBuffer)!.AllocatedBytes);

        public abstract void Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // Device
    // ──────────────────────────────────────────────────────────────

    public abstract class RHIDevice : IDisposable
    {
        public abstract string AdapterName { get; }
        public abstract ulong DedicatedVideoMemory { get; }
        public abstract ulong DedicatedSystemMemory { get; }
        public abstract ulong SharedSystemMemory { get; }
        public abstract RHIShaderModel MaxShaderModel { get; }
        public abstract bool SupportsMeshShaders { get; }
        public abstract bool SupportsRayTracing { get; }
        public abstract bool SupportsVariableRateShading { get; }
        public abstract bool SupportsSamplerFeedback { get; }
        public abstract string MaxFeatureLevel { get; }
        public virtual string Name { get; set; } = string.Empty;

        // Command queues
        public abstract RHICommandQueue GetCommandQueue(RHIQueueType type);

        // Resource creation
        public abstract RHIBuffer CreateBuffer(in RHIBufferDesc desc, string name = "");
        public abstract RHIBuffer CreateCommittedBuffer(in RHIBufferDesc desc, byte[] initialData, string name = "");
        public abstract RHITexture CreateTexture(in RHITextureDesc desc, string name = "");

        // Upload helpers
        public virtual RHIBuffer CreateUploadBuffer(ulong sizeInBytes, string name = "")
        {
            return default;
        }
        public abstract void UploadToBuffer<T>(RHIBuffer dest, ReadOnlySpan<T> data, ulong destOffset = 0) where T : struct;
        public virtual void UploadToBuffer(RHIBuffer dest, byte[] data, ulong destOffset = 0)
            => UploadToBuffer<byte>(dest, data.AsSpan(), destOffset);
        public virtual void UploadToTexture(RHITexture dest, ReadOnlySpan<byte> data, int width, int height, int mipIndex = 0, int sliceIndex = 0) { }

        // Descriptor heaps
        public abstract RHIDescriptorHeap CreateDescriptorHeap(RHIDescriptorHeapType type, int capacity, bool shaderVisible = true);

        // Descriptor creation
        public abstract RHIDescriptorHandle CreateCBV(RHIBuffer buffer, ulong offset, uint sizeInBytes);
        public abstract RHIDescriptorHandle CreateSRV(RHIBuffer buffer, RHIFormat format, bool isStructured, uint firstElement, uint numElements, uint structureStride);
        public abstract RHIDescriptorHandle CreateUAV(RHIBuffer buffer, RHIFormat format, bool isStructured,
            uint firstElement, uint numElements, uint structureStride, RHIBuffer? counterBuffer = null, ulong counterOffset = 0);
        public abstract RHIDescriptorHandle CreateRTV(RHITexture texture, uint mipSlice = 0, uint arraySlice = 0);
        public abstract RHIDescriptorHandle CreateDSV(RHITexture texture, uint mipSlice = 0, uint arraySlice = 0);
        public abstract RHIDescriptorHandle CreateSampler(RHISamplerDesc desc);
        public virtual RHIDescriptorHandle GetOrCreateSampler(RHISamplerDesc desc) => CreateSampler(desc);

        // ── Texture SRV/UAV convenience overloads ──
        public virtual RHIDescriptorHandle CreateSRV(RHITexture texture, uint mostDetailedMip = 0, uint mipLevels = 0)
            => default;
        public virtual RHIDescriptorHandle CreateUAV(RHITexture texture, uint mipSlice = 0)
            => default;

        // ── Descriptor-based SRV/UAV overloads ──
        public virtual RHIDescriptorHandle CreateSRV(RHITexture texture, RHISRVDesc desc)
            => default;
        public virtual RHIDescriptorHandle CreateUAV(RHITexture texture, RHIUAVDesc desc)
            => default;
        public virtual RHIDescriptorHandle CreateSRV(RHIBuffer buffer, RHISRVDesc desc)
        {
            bool isStructured = buffer.BufferDesc?.IsStructured ?? false;
            return CreateSRV(buffer, desc.Format, isStructured, desc.FirstElement, desc.NumElements, (uint)(buffer.BufferDesc?.Stride ?? 0));
        }
        public virtual RHIDescriptorHandle CreateUAV(RHIBuffer buffer, RHIUAVDesc desc)
        {
            bool isStructured = buffer.BufferDesc?.IsStructured ?? false;
            return CreateUAV(buffer, desc.Format, isStructured, desc.FirstElement, desc.NumElements, (uint)(buffer.BufferDesc?.Stride ?? 0));
        }

        // ── UploadToBuffer Span<T> and T[] overloads ──
        public virtual void UploadToBuffer<T>(RHIBuffer dest, Span<T> data, ulong destOffset = 0) where T : struct
            => UploadToBuffer<T>(dest, (ReadOnlySpan<T>)data, destOffset);
        public virtual void UploadToBuffer<T>(RHIBuffer dest, T[] data, ulong destOffset = 0) where T : struct
            => UploadToBuffer<T>(dest, data.AsSpan(), destOffset);

        // Root signature
        public abstract RHIRootSignature CreateRootSignature(in RHIRootSignatureDesc desc, string name = "");

        // Pipeline state
        public abstract RHIPipelineState CreateGraphicsPipelineState(in RHIGraphicsPipelineDesc desc, string name = "");
        public abstract RHIPipelineState CreateComputePipelineState(in RHIComputePipelineDesc desc, string name = "");
        public abstract RHIPipelineState CreateMeshPipelineState(in RHIMeshPipelineDesc desc, string name = "");

        // Command list
        public abstract RHICommandList CreateCommandList(RHIQueueType type, string name = "");

        // Fence
        public abstract RHIFence CreateFence(ulong initialValue, string name = "");

        // Swap chain
        public abstract RHISwapChain CreateSwapChain(IntPtr hwnd, int width, int height,
            RHIFormat format, int bufferCount, bool vsync, string name = "");
        public virtual RHISwapChain CreateSwapChain(IntPtr hwnd, int width, int height,
            RHIFormat format, int bufferCount, string name = "")
            => CreateSwapChain(hwnd, width, height, format, bufferCount, true, name);

        // Shader compilation
        public abstract RHIShaderBytecode CompileShader(string hlslSource, string entryPoint,
            RHIShaderStage stage, RHIShaderModel shaderModel, string? sourceName = null, bool debug = false);
        public abstract RHIShaderBytecode CompileShaderFromFile(string filePath, string entryPoint,
            RHIShaderStage stage, RHIShaderModel shaderModel, bool debug = false);

        // Disposal
        public abstract void Dispose();

        // Frame sync helpers
        private readonly Queue<(RHIFence fence, ulong value)> _deferredReleases = new();
        private ulong _currentFenceValue;

        public void ReleaseAfterFrame(RHIResource resource, RHIFence fence)
        {
            _currentFenceValue++;
            _deferredReleases.Enqueue((fence, _currentFenceValue));
            resource.Dispose();
        }

        public void ProcessDeferredReleases(ulong completedFenceValue)
        {
            while (_deferredReleases.Count > 0)
            {
                var (fence, value) = _deferredReleases.Peek();
                if (fence.CompletedValue >= value)
                    _deferredReleases.Dequeue();
                else
                    break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Device factory
    // ──────────────────────────────────────────────────────────────

    public enum RHIBackendType { D3D12, Vulkan, Auto }

    public static class RHIDeviceFactory
    {
        public static RHIBackendType PreferredBackend { get; set; } = RHIBackendType.Vulkan;

        public static RHIDevice Create(RHIBackendType? backend = null, bool enableDebug = false, bool enableGpuBasedValidation = false)
        {
            var b = backend ?? PreferredBackend;
            if (b == RHIBackendType.Auto)
            {
                // 默认使用 Vulkan 1.4 后端（跨平台）
                b = RHIBackendType.Vulkan;
            }

            return b switch
            {
                RHIBackendType.Vulkan => new Vulkan.VulkanDevice(enableDebug, enableGpuBasedValidation),
                RHIBackendType.D3D12 => throw new PlatformNotSupportedException(
                    "D3D12 后端已弃用，请使用 Vulkan 1.4 后端。"),
                _ => throw new ArgumentException($"Unknown backend: {b}")
            };
        }
    }
}
