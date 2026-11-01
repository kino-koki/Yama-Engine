using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.RHI
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
        public virtual ulong Size => 0;
        public abstract void Dispose();
    }

    public abstract class RHIBuffer : RHIResource
    {
        public override ulong Size => BufferDesc?.Size ?? 0;
        public virtual uint Stride => (uint)(BufferDesc?.Stride ?? 0);
        /// <summary>描述符别名（兼容 <c>RHIDeviceExtensions</c> 中的 <c>Desc</c> 访问）。</summary>
        public virtual RHIBufferDesc? Desc => BufferDesc;
    }

    public abstract class RHITexture : RHIResource
    {
        public virtual int Width => TextureDesc?.Width ?? 0;
        public virtual int Height => TextureDesc?.Height ?? 0;
        public virtual int Depth => TextureDesc?.DepthOrArraySize ?? 1;
        public virtual int ArraySize => TextureDesc?.DepthOrArraySize ?? 1;
        public virtual RHIFormat Format => TextureDesc?.Format ?? default;
        public virtual int MipLevels => TextureDesc?.MipLevels ?? 1;
        public virtual int SampleCount => TextureDesc?.SampleCount ?? 1;

        /// <summary>调试名称。</summary>
        public virtual string DebugName { get; set; } = string.Empty;
    }

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

        // ── 高层便利命令（默认空实现，后端可覆盖；承接渲染管线/子系统期望的高层 API）──
        public virtual void SetRenderTargets(RHITexture[] renderTargets, RHITexture? depthStencil = null) { }
        public virtual void SetRenderTarget(RHITexture? target, RHITexture? depthStencil = null)
            => SetRenderTargets(target != null ? new[] { target } : Array.Empty<RHITexture>(), depthStencil);
        public virtual void ClearRenderTarget(RHITexture target, Vector4 color) { }
        public virtual void ClearDepthStencil(RHITexture target, float depth = 1.0f, byte stencil = 0) { }
        public virtual void SetVertexBuffer(uint slot, RHIBuffer buffer, uint stride, ulong offset = 0) { }
        public virtual void SetIndexBuffer(RHIBuffer buffer, RHIFormat format, ulong offset = 0) { }
        public virtual void SetConstantBuffer(RHIBuffer buffer, uint slot, ulong offset = 0, uint size = 0) { }
        public virtual void SetShaderResource(RHIBuffer buffer, uint slot) { }
        public virtual void SetShaderResource(RHITexture texture, uint slot) { }
        public virtual void SetShaderResourceView(RHIBuffer buffer, uint slot) { }
        public virtual void SetShaderResourceView(RHITexture texture, uint slot) { }
        public virtual void SetUnorderedAccessView(RHIBuffer buffer, uint slot) { }
        public virtual void SetUnorderedAccessView(RHITexture texture, uint slot) { }
        // ── 带 ShaderStage + slot 的重载（渲染管线以 ShaderStage.Pixel/Compute 调用）──
        public virtual void SetShaderResourceView(ShaderStage stage, int slot, RHITexture texture)
            => SetShaderResourceView(texture, (uint)slot);
        public virtual void SetShaderResourceView(ShaderStage stage, int slot, RHIBuffer buffer)
            => SetShaderResourceView(buffer, (uint)slot);
        public virtual void SetUnorderedAccessView(ShaderStage stage, int slot, RHITexture texture)
            => SetUnorderedAccessView(texture, (uint)slot);
        public virtual void SetUnorderedAccessView(ShaderStage stage, int slot, RHIBuffer buffer)
            => SetUnorderedAccessView(buffer, (uint)slot);
        public virtual void BeginDebugEvent(string name, uint p0 = 0, uint p1 = 0, uint p2 = 0, uint p3 = 0) { }
        public virtual void EndDebugEvent() { }
        public virtual void SetSampler(uint slot, RHISamplerDesc desc) { }
        public virtual void Draw(uint vertexCount, uint instanceCount = 1) { }
        public virtual void DrawIndexed(uint indexCount, uint instanceCount = 1, uint indexOffset = 0, int baseVertex = 0) { }
        // ── 5 参数 DrawIndexed 重载（快捷映射到 DrawIndexedInstanced）──
        public virtual void DrawIndexed(int indexCount, int instanceCount, int startIndex, int baseVertex, int startInstance)
            => DrawIndexedInstanced((uint)indexCount, (uint)instanceCount, (uint)startIndex, baseVertex, (uint)startInstance);
        public virtual void SetComputeRootUnorderedAccessView(uint rootParameterIndex, RHIBuffer buffer) { }

        // ── 更多渲染层期望的高层命令（默认空实现，后端可覆盖）──
        /// <summary>绑定深度模板视图。</summary>
        public virtual void SetDepthStencilView(RHIDescriptorHandle dsv) { }
        /// <summary>提交并执行当前命令列表的一组命令。</summary>
        public virtual void Execute() { }
        /// <summary>读取纹理到回读缓冲（调试用，可能造成 GPU stall）。</summary>
        public virtual void ReadTexture(RHITexture texture, RHIBuffer readBackBuffer, int offset) { }
        /// <summary>为纹理生成 mip 链。</summary>
        public virtual void GenerateMips(RHITexture texture) { }

        // ── 更多高层别名（渲染管线/子系统期望的命名）──
        /// <summary>绑定图形管线。</summary>
        public virtual void SetGraphicsPipeline(RHIPipelineState pso) => SetPipelineState(pso);
        /// <summary>绑定纹理到指定槽位。</summary>
        public virtual void SetTexture(uint slot, RHITexture texture) => SetShaderResource(texture, slot);
        /// <summary>用 uint 值清除 UAV 缓冲。</summary>
        public virtual void ClearUAVUint(RHIBuffer buffer, uint value) { }
        /// <summary>绑定图形根常量缓冲。</summary>
        public virtual void SetGraphicsRootConstantBuffer(uint rootParameterIndex, RHIBuffer buffer)
            => SetGraphicsRootConstantBufferView(rootParameterIndex, buffer);
        /// <summary>绑定计算根常量缓冲。</summary>
        public virtual void SetComputeRootConstantBuffer(uint rootParameterIndex, RHIBuffer buffer)
            => SetComputeRootConstantBufferView(rootParameterIndex, buffer);
        /// <summary>绑定加速结构（光追，object 以兼容多种 AS 描述类型）。</summary>
        public virtual void SetAccelerationStructure(uint rootParameterIndex, object accel) { }
        /// <summary>间接执行（参数缓冲 + 偏移）。</summary>
        public virtual void ExecuteIndirect(RHIBuffer argsBuffer, ulong argsOffset) { }
        public abstract unsafe void SetGraphicsRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues, in T data, uint destOffsetIn32BitValues) where T : struct;
        public abstract unsafe void SetComputeRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues, in T data, uint destOffsetIn32BitValues) where T : struct;
        // ── 2 参数便捷重载（渲染管线以 SetGraphicsRoot32BitConstants(slot, ref data) 调用）──
        public virtual void SetGraphicsRoot32BitConstants<T>(uint rootParameterIndex, ref T data, uint destOffsetIn32BitValues = 0) where T : struct
            => SetGraphicsRoot32BitConstants(rootParameterIndex, (uint)(System.Runtime.InteropServices.Marshal.SizeOf<T>() / sizeof(uint)), in data, destOffsetIn32BitValues);
        public virtual void SetComputeRoot32BitConstants<T>(uint rootParameterIndex, ref T data, uint destOffsetIn32BitValues = 0) where T : struct
            => SetComputeRoot32BitConstants(rootParameterIndex, (uint)(System.Runtime.InteropServices.Marshal.SizeOf<T>() / sizeof(uint)), in data, destOffsetIn32BitValues);

        // IA
        public abstract void IASetPrimitiveTopology(RHIPrimitiveTopology topology);
        public abstract void IASetVertexBuffers(uint startSlot, RHIBuffer buffer, ulong offset, uint stride, uint sizeInBytes);
        public abstract void IASetIndexBuffer(RHIBuffer buffer, ulong offset, uint sizeInBytes, RHIFormat format);

        // Draw
        public abstract void DrawInstanced(uint vertexCountPerInstance, uint instanceCount, uint startVertex, uint startInstance);
        public abstract void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount, uint startIndex, int baseVertex, uint startInstance);
        public abstract void DrawIndexedInstancedIndirect(RHIBuffer argsBuffer, ulong argsOffset);
        public abstract void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ);
        public virtual void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
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

        /// <summary>无 dstSubresource 的便捷重载（默认复制到子资源 0；承接 RHIDeviceImmediateExtensions 等 10 参调用）。</summary>
        public virtual void CopyTextureRegion(RHITexture dst, int x, int y, int z,
            RHIBuffer src, ulong offset, int rowPitch, int slicePitch, int w, int h)
            => CopyTextureRegion(dst, 0, (uint)x, (uint)y, (uint)z, src, offset,
               (uint)rowPitch, (uint)slicePitch, (uint)w, (uint)h);

        /// <summary>
        /// 纹理到纹理区域拷贝（高层便捷重载，供烘焙/拷贝引擎以 dst/src 两个纹理调用）。
        /// 默认空实现，后端可覆盖；用于从源纹理复制一块区域到目标纹理（两者同格式同子资源 0）。
        /// </summary>
        public virtual void CopyTextureRegion(RHITexture dst, RHITexture src, int dstX, int dstY, int srcX, int srcY, int w, int h)
        {
        }

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

        // Friendly texture-based clear overloads (HybridLightingRenderer 等使用)
        public virtual void ClearRenderTargetView(RHITexture texture, RHIResourceState state, Vector4 clearColor) { }
        public virtual void ClearDepthStencilView(RHITexture texture, RHIResourceState state, bool clearDepth, float depth) { }

        // Friendly pipeline overloads (GeometryBufferGenerator 等使用 RHIPipeline)
        public virtual void SetGraphicsPipeline(RHIPipeline pipeline) { }
        // Friendly ReadTexture byte[] overload (CPU 回读)
        public virtual void ReadTexture(RHITexture texture, byte[] readBackBuffer, int offset) { }
        // Friendly SetVertexBuffer int-stride overload
        public virtual void SetVertexBuffer(uint slot, RHIBuffer buffer, int stride, ulong offset = 0) { }

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
        /// <summary>是否为支持 Mesh Shader（单数命名别名，等价 <see cref="SupportsMeshShaders"/>）。</summary>
        public virtual bool SupportsMeshShader => SupportsMeshShaders;
        public abstract bool SupportsRayTracing { get; }
        public abstract bool SupportsVariableRateShading { get; }
        public abstract bool SupportsSamplerFeedback { get; }
        public abstract string MaxFeatureLevel { get; }
        public virtual string Name { get; set; } = string.Empty;

        /// <summary>总显存大小（等价 DedicatedVideoMemory 的便捷别名）。</summary>
        public virtual ulong TotalVRAM => DedicatedVideoMemory;

        /// <summary>当前可用显存 (字节)。默认实现返回总显存, 具体后端可重写。</summary>
        public virtual ulong AvailableVRAM => DedicatedVideoMemory;

        /// <summary>设备能力集合。</summary>
        public virtual RHIDeviceCapabilities Capabilities => new RHIDeviceCapabilities
        {
            SupportsTessellation = MaxShaderModel >= RHIShaderModel.SM6_0,
            SupportsGeometryShader = MaxShaderModel >= RHIShaderModel.SM5_0,
            SupportsMeshShader = SupportsMeshShaders,
            SupportsRayTracing = SupportsRayTracing,
            SupportsVariableRateShading = SupportsVariableRateShading,
        };

        // Command queues
        public abstract RHICommandQueue GetCommandQueue(RHIQueueType type);

        // Resource creation
        public abstract RHIBuffer CreateBuffer(in RHIBufferDesc desc, string name = "");
        public abstract RHIBuffer CreateCommittedBuffer(in RHIBufferDesc desc, byte[] initialData, string name = "");
        public abstract RHITexture CreateTexture(in RHITextureDesc desc, string name = "");

        // ── 高层便利创建/映射（虚拟实现，允许后端覆盖）──
        public virtual RHIBuffer CreateBuffer(byte[] data, RHIBufferUsage usage, string name = "")
        {
            var d = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(data?.Length ?? 0),
                Usage = usage,
                Name = name,
            };
            var b = CreateBuffer(in d, name);
            if (data != null && data.Length > 0) UploadToBuffer(b, data);
            return b;
        }

        public virtual RHIBuffer CreateBuffer(uint[] data, RHIBufferUsage usage, string name = "")
        {
            var d = new RHIBufferDesc
            {
                SizeInBytes = (ulong)((data?.Length ?? 0) * sizeof(uint)),
                Usage = usage,
                Name = name,
            };
            var b = CreateBuffer(in d, name);
            if (data != null && data.Length > 0) { var bytes = new byte[data.Length * sizeof(uint)]; Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length); UploadToBuffer(b, bytes); }
            return b;
        }

        // ── 按大小 + 用途的便捷创建重载（ClusteredForward/GeometryBuffer 等以 (ulong 大小, RHIBufferUsage) 调用）──
        public virtual RHIBuffer CreateBuffer(ulong sizeInBytes, RHIBufferUsage usage, string name = "")
            => CreateBuffer(new RHIBufferDesc { SizeInBytes = sizeInBytes, Size = sizeInBytes, Usage = usage, Name = name }, name);

        // size + stride overload (RenderGraphV2 等使用)
        public virtual RHIBuffer CreateBuffer(ulong sizeInBytes, ulong stride, RHIBufferUsage usage, string name = "")
            => CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = sizeInBytes,
                Size = sizeInBytes,
                Stride = stride,
                Usage = usage,
                Name = name
            }, name);

        public virtual IntPtr MapBufferRange(RHIBuffer buffer, ulong offset, ulong size, RHIMapMode mode = RHIMapMode.Write)
            => IntPtr.Zero;
        public virtual void UnmapBufferRange(RHIBuffer buffer) { }
        public virtual void UpdateBufferRange(RHIBuffer buffer, ulong dstOffset, byte[] srcData, int srcOffset, int srcLength)
        {
            if (srcData == null || srcLength <= 0) return;
            var span = new ReadOnlySpan<byte>(srcData, srcOffset, srcLength);
            UploadToBuffer(buffer, span, dstOffset);
        }

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

        // ── 即时（immediate）模式命令上下文 ──
        // 高层渲染器通过 _device.Dispatch(...) 等直接把命令发送到“当前命令列表”。
        // 后端/应用在每帧显式赋予当前命令列表，未设置时延后操作为 no-op（保证编译零错误）。
        public virtual RHICommandList? ImmediateCommandList { get; set; }
        public virtual void SetCurrentCommandList(RHICommandList cmd) => ImmediateCommandList = cmd;

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

        // ──────────────────────────────────────────────────────────────
        // RHIDeviceExtensions / 上层调用所需的设备便捷 API（虚拟默认实现）
        // ──────────────────────────────────────────────────────────────

        /// <summary>按描述符创建 Fence（高层便利重载）。</summary>
        public virtual RHIFence CreateFence(RHIFenceDesc desc)
            => CreateFence(desc.InitialValue, desc.Name ?? "Fence");

        /// <summary>等待 Fence 完成。</summary>
        public virtual bool WaitForFence(RHIFence fence, ulong timeoutMs = 1000)
        {
            if (fence == null) return true;
            fence.Wait(fence.CompletedValue, (uint)timeoutMs);
            return true;
        }

        /// <summary>重置 Fence 信号值。</summary>
        public virtual void ResetFence(RHIFence fence) { }

        /// <summary>销毁资源。</summary>
        public virtual void DestroyResource(RHIResource resource) => resource?.Dispose();

        /// <summary>设置资源调试名。</summary>
        public virtual void SetResourceName(RHIResource resource, string name)
        {
            if (resource != null) resource.Name = name;
        }

        /// <summary>获取设备信息。</summary>
        public virtual RHIDeviceInfo GetDeviceInfo() => new RHIDeviceInfo
        {
            DeviceName = AdapterName,
            VendorName = string.Empty,
            Backend = GetBackend(),
            DedicatedVideoMemory = DedicatedVideoMemory,
            DedicatedSystemMemory = DedicatedSystemMemory,
            SharedSystemMemory = SharedSystemMemory,
            DriverVersion = string.Empty,
            ShaderModel = MaxFeatureLevel,
        };

        /// <summary>获取渲染后端类型。</summary>
        public virtual RHIBackendType GetBackend() => RHIBackendType.Vulkan;

        // ── 设备能力上限查询（默认值，后端可覆盖）──
        public virtual int GetMaxTextureSize2D() => 16384;
        public virtual int GetMaxTextureSize3D() => 2048;
        public virtual int GetMaxTextureSizeCube() => 16384;
        public virtual int GetMaxTextureArrayLayers() => 2048;
        public virtual int GetMaxBufferBindings() => 64;
        public virtual int GetMaxSamplerBindings() => 16;
        public virtual int GetMaxUAVBindings() => 64;
        public virtual long GetMaxUniformBufferSize() => 64 * 1024;
        public virtual long GetMaxStorageBufferSize() => 1L * 1024 * 1024 * 1024;
        public virtual int GetUniformBufferAlignment() => 256;
        public virtual int GetStorageBufferAlignment() => 16;
        public virtual int GetTexelBufferAlignment() => 16;
        public virtual int GetMaxComputeWorkgroupX() => 1024;
        public virtual int GetMaxComputeWorkgroupY() => 1024;
        public virtual int GetMaxComputeWorkgroupZ() => 64;
        public virtual int GetMaxComputeWorkgroupSize() => 1024;

        /// <summary>查询格式特性（默认全部支持，后端可覆盖）。</summary>
        public virtual RHIFormatProperties QueryFormatProperties(RHIFormat format) => new RHIFormatProperties
        {
            SupportsShaderResource = true,
            SupportsRenderTarget = true,
            SupportsDepthStencil = true,
            SupportsUnorderedAccess = true,
            SupportsBlendable = true,
            SupportsMultisampling = true,
        };

        // ── 能力判别（方法与属性区分命名，供 GetCapabilities 方法式调用）──
        public virtual bool SupportsTessellation() => false;
        public virtual bool SupportsGeometryShader() => false;
        public virtual bool SupportsBindless() => false;

        // ── 高层创建重载（供 RHIShader/RHIPipelineExtensions 便捷 API）──
        public virtual RHIShader CreateShader(RHIShaderDesc desc) => null;
        public virtual RHIPipeline CreateGraphicsPipeline(RHIGraphicsPipelineDesc desc) => null;
        public virtual RHIPipeline CreateComputePipeline(RHIComputePipelineDesc desc) => null;

        /// <summary>上传数据到纹理指定区域（高层面包）。</summary>
        public virtual void UpdateTexture(RHITexture texture, int dstX, int dstY, int dstZ,
            int srcWidth, int srcHeight, int srcDepth, int srcMip, byte[] data) { }

        /// <summary>更新数据到缓冲指定偏移。</summary>
        public virtual void UpdateBuffer(RHIBuffer buffer, ulong dstOffset, byte[] srcData, int srcOffset, int srcLength)
            => UpdateBufferRange(buffer, dstOffset, srcData, srcOffset, srcLength);

        // ── 设备创建（静态入口，路由到工厂）──
        /// <summary>根据设备描述创建 RHI 设备。</summary>
        public static RHIDevice Create(RHIDeviceDescriptor desc)
        {
            RHIBackendType backend = desc?.API == YamaEngine.Core.GraphicsAPI.Vulkan ? RHIBackendType.Vulkan : RHIBackendType.D3D12;
            var device = RHIDeviceFactory.Create(backend, desc?.EnableDebugLayer ?? false, desc?.GPUValidationLevel > 0);
            if (device != null && desc != null && !string.IsNullOrEmpty(desc.ApplicationName))
            {
                device.Name = desc.ApplicationName;
            }
            return device;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Device descriptor
    // ──────────────────────────────────────────────────────────────

    /// <summary>RHI 设备创建描述（用于 <see cref="RHIDevice.Create"/>）。</summary>
    public class RHIDeviceDescriptor
    {
        /// <summary>图形 API 后端选择。</summary>
        public YamaEngine.Core.GraphicsAPI API;
        /// <summary>是否启用调试层。</summary>
        public bool EnableDebugLayer;
        /// <summary>GPU 验证级别（&gt;0 表示启用 GPU 校验）。</summary>
        public int GPUValidationLevel;
        /// <summary>应用名称。</summary>
        public string ApplicationName = string.Empty;
        /// <summary>最大纹理内存预算（字节）。</summary>
        public ulong MaxTextureMemory;
        /// <summary>最大缓冲内存预算（字节）。</summary>
        public ulong MaxBufferMemory;

        /// <summary>构造。</summary>
        public RHIDeviceDescriptor() { }
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
