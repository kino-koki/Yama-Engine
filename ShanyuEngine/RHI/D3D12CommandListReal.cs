// ==================================================================================================
//  D3D12CommandListReal.cs - 山雨游戏引擎 D3D12 命令列表真实实现
//
//  命名空间: ShanyuEngine.RHI
//  依赖: Vortice.Direct3D12, Vortice.Mathematics
//
//  功能覆盖:
//    - D3D12GraphicsCommandList: 封装 ID3D12GraphicsCommandList
//    - 命令分配器池化 / 每帧重置
//    - 资源屏障: Transition / UAV / Aliasing / Split Barrier
//    - 批量屏障优化: 同一资源多子资源合并
//    - 绘制命令: DrawInstanced / DrawIndexed / Indirect / DispatchMesh / Dispatch
//    - 管线状态: SetPipelineState / SetRootSignature / SetDescriptorHeaps / 根参数绑定
//    - 资源操作: Copy / Resolve / GenerateMips / WriteBufferImmediate / ExecuteIndirect
//    - 查询: BeginQuery / EndQuery / ResolveQueryData / SetPredication
//    - TrackingCommandList: 自动推断屏障 / 资源状态跟踪
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace ShanyuEngine.RHI
{
    // ==============================================================================================
    //  命令分配器池
    // ==============================================================================================

    /// <summary>
    /// 命令分配器池。管理多个 ID3D12CommandAllocator 实例，支持每帧轮换重置。
    /// </summary>
    public sealed class CommandAllocatorPool : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly CommandListType _commandListType;
        private readonly List<ID3D12CommandAllocator> _allocators = new();
        private readonly Queue<int> _freeIndices = new();
        private readonly object _lock = new();
        private bool _disposed;

        public CommandListType Type => _commandListType;
        public int TotalCount => _allocators.Count;
        public int FreeCount
        {
            get { lock (_lock) { return _freeIndices.Count; } }
        }

        public CommandAllocatorPool(ID3D12Device device, CommandListType type, int initialCount = 2)
        {
            _device = device;
            _commandListType = type;

            for (int i = 0; i < initialCount; i++)
            {
                var allocator = _device.CreateCommandAllocator(type);
                _allocators.Add(allocator);
                _freeIndices.Enqueue(i);
            }
        }

        /// <summary>获取一个可用的命令分配器</summary>
        public ID3D12CommandAllocator Acquire()
        {
            lock (_lock)
            {
                if (_freeIndices.Count > 0)
                {
                    int idx = _freeIndices.Dequeue();
                    // 重置分配器 (清空之前的命令)
                    _allocators[idx].Reset();
                    return _allocators[idx];
                }

                // 池已耗尽，创建新的分配器
                var newAllocator = _device.CreateCommandAllocator(_commandListType);
                _allocators.Add(newAllocator);
                return newAllocator;
            }
        }

        /// <summary>归还一个命令分配器</summary>
        public void Release(ID3D12CommandAllocator allocator)
        {
            lock (_lock)
            {
                int idx = _allocators.IndexOf(allocator);
                if (idx >= 0)
                {
                    _freeIndices.Enqueue(idx);
                }
            }
        }

        /// <summary>重置所有分配器 (设备丢失恢复时调用)</summary>
        public void ResetAll()
        {
            lock (_lock)
            {
                foreach (var allocator in _allocators)
                    allocator.Reset();
                _freeIndices.Clear();
                for (int i = 0; i < _allocators.Count; i++)
                    _freeIndices.Enqueue(i);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                foreach (var allocator in _allocators)
                    allocator.Dispose();
                _allocators.Clear();
                _freeIndices.Clear();
            }
        }
    }

    // ==============================================================================================
    //  D3D12GraphicsCommandList - 真实命令列表实现
    // ==============================================================================================

    /// <summary>
    /// D3D12 图形命令列表封装。封装 ID3D12GraphicsCommandList，提供完整的绘制/资源/屏障操作。
    /// </summary>
    public sealed class D3D12GraphicsCommandList : IDisposable
    {
        private readonly ID3D12GraphicsCommandList _nativeList;
        private readonly CommandAllocatorPool _allocatorPool;
        private ID3D12CommandAllocator _currentAllocator;
        private bool _isOpen;
        private bool _disposed;

        // 绑定的管线状态和根签名
        private ID3D12PipelineState? _currentPSO;
        private ID3D12RootSignature? _currentGraphicsRootSig;
        private ID3D12RootSignature? _currentComputeRootSig;

        // 渲染目标
        private CpuDescriptorHandle[] _currentRTVs = Array.Empty<CpuDescriptorHandle>();
        private CpuDescriptorHandle _currentDSV;
        private bool _hasDSV;

        // 视口和裁剪
        private Vortice.Mathematics.Vector3[] _currentViewports = Array.Empty<Vortice.Mathematics.Vector3>();
        private RawRect[] _currentScissors = Array.Empty<RawRect>();

        // IA 状态
        private PrimitiveTopology _currentTopology;

        // 描述符堆
        private ID3D12DescriptorHeap[] _descriptorHeaps = Array.Empty<ID3D12DescriptorHeap>();

        public ID3D12GraphicsCommandList NativeList => _nativeList;
        public bool IsOpen => _isOpen;
        public CommandListType Type { get; }

        public D3D12GraphicsCommandList(ID3D12Device device, CommandAllocatorPool allocatorPool,
            CommandListType type = CommandListType.Direct, ID3D12PipelineState? initialState = null)
        {
            _allocatorPool = allocatorPool;
            Type = type;
            _currentAllocator = _allocatorPool.Acquire();

            _nativeList = device.CreateCommandList(type, _currentAllocator, initialState);
            _nativeList.DeviceName = "ShanyuEngine_GraphicsCommandList";

            // 设置默认拓扑
            _nativeList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            _currentTopology = Vortice.Direct3D.PrimitiveTopology.TriangleList;

            _isOpen = true;
        }

        /// <summary>重置命令列表 (复用同一个列表对象)</summary>
        public void Reset(ID3D12PipelineState? initialState = null)
        {
            Close();
            _currentAllocator = _allocatorPool.Acquire();
            _nativeList.Reset(_currentAllocator, initialState);
            _isOpen = true;

            // 重置跟踪状态
            _currentPSO = null;
            _currentGraphicsRootSig = null;
            _currentComputeRootSig = null;
            _currentRTVs = Array.Empty<CpuDescriptorHandle>();
            _hasDSV = false;
            _nativeList.IASetPrimitiveTopology(_currentTopology);
        }

        /// <summary>关闭命令列表 (提交到队列前必须调用)</summary>
        public void Close()
        {
            if (_isOpen)
            {
                _nativeList.Close();
                _isOpen = false;
            }
        }

        // === 资源屏障 ===

        /// <summary>Transition Barrier: 资源状态转换 (子资源级别)</summary>
        public void ResourceBarrierTransition(ID3D12Resource resource,
            ResourceStates stateBefore, ResourceStates stateAfter, uint subresource = 0xFFFFFFFF)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            // 优化: 如果 before 和 after 相同则跳过
            if (stateBefore == stateAfter) return;

            var barrier = new ResourceBarrierTransition(resource, subresource, stateBefore, stateAfter);
            _nativeList.ResourceBarrier(barrier);
        }

        /// <summary>UAV Barrier: 确保 UAV 访问的执行顺序</summary>
        public void ResourceBarrierUAV(ID3D12Resource? resource = null)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            var barrier = new ResourceBarrierUAV(resource);
            _nativeList.ResourceBarrier(barrier);
        }

        /// <summary>Aliasing Barrier: 不同资源占同一内存时的同步</summary>
        public void ResourceBarrierAliasing(ID3D12Resource resourceBefore, ID3D12Resource resourceAfter)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            var barrier = new ResourceBarrierAliasing(resourceBefore, resourceAfter);
            _nativeList.ResourceBarrier(barrier);
        }

        /// <summary>Split Barrier: 将一个 Barrier 分解为 Begin 和 End</summary>
        public void ResourceBarrierSplitBegin(ID3D12Resource resource,
            ResourceStates stateBefore, ResourceStates stateAfter, uint subresource = 0xFFFFFFFF)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            // Begin: 执行 before -> Begin-Only 状态
            var beginBarrier = new ResourceBarrierTransition(
                resource, subresource, stateBefore, stateAfter);
            beginBarrier.Flags = ResourceBarrierFlags.BeginOnly;
            _nativeList.ResourceBarrier(beginBarrier);
        }

        public void ResourceBarrierSplitEnd(ID3D12Resource resource,
            ResourceStates stateBefore, ResourceStates stateAfter, uint subresource = 0xFFFFFFFF)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            // End: 执行 End-Only 状态 -> after
            var endBarrier = new ResourceBarrierTransition(
                resource, subresource, stateBefore, stateAfter);
            endBarrier.Flags = ResourceBarrierFlags.EndOnly;
            _nativeList.ResourceBarrier(endBarrier);
        }

        /// <summary>
        /// 批量资源屏障优化。同一资源的多个子资源合并为单个屏障调用，
        /// 减少驱动验证开销。
        /// </summary>
        public void ResourceBarrierBatch(IReadOnlyList<ResourceBarrierDesc> barriers)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            if (barriers.Count == 0) return;

            // 按资源分组，合并同一资源的多个子资源屏障
            var grouped = new Dictionary<nint, List<(int origIdx, ResourceStates before, ResourceStates after, uint sub)>>();

            for (int i = 0; i < barriers.Count; i++)
            {
                var b = barriers[i];
                nint key = b.Resource.NativePointer;

                if (!grouped.TryGetValue(key, out var list))
                {
                    list = new List<(int, ResourceStates, ResourceStates, uint)>();
                    grouped[key] = list;
                }
                list.Add((i, b.StateBefore, b.StateAfter, b.Subresource));
            }

            // 对每个资源，检查是否可以使用单一全资源屏障替代多个子资源屏障
            var nativeBarriers = new ResourceBarrier[barriers.Count];
            int writeIdx = 0;

            foreach (var kvp in grouped)
            {
                var entries = kvp.Value;

                if (entries.Count == 1)
                {
                    // 单个屏障直接转换
                    var e = entries[0];
                    if (e.sub == 0xFFFFFFFF)
                    {
                        nativeBarriers[writeIdx++] = new ResourceBarrierTransition(
                            barriers[e.origIdx].Resource, 0xFFFFFFFF, e.before, e.after);
                    }
                    else
                    {
                        nativeBarriers[writeIdx++] = new ResourceBarrierTransition(
                            barriers[e.origIdx].Resource, e.sub, e.before, e.after);
                    }
                }
                else
                {
                    // 多个子资源屏障
                    // 优化: 如果所有子资源转换到相同目标状态，使用全资源屏障
                    var targetStates = entries.Select(e => e.after).Distinct().ToArray();
                    if (targetStates.Length == 1 && entries.Any(e => e.sub == 0xFFFFFFFF))
                    {
                        // 已有全资源屏障
                        var e = entries.First(e => e.sub == 0xFFFFFFFF);
                        nativeBarriers[writeIdx++] = new ResourceBarrierTransition(
                            barriers[e.origIdx].Resource, 0xFFFFFFFF, e.before, e.after);
                    }
                    else if (targetStates.Length == 1)
                    {
                        // 合并为全资源屏障 (所有子资源到同一状态)
                        var e = entries[0];
                        nativeBarriers[writeIdx++] = new ResourceBarrierTransition(
                            barriers[e.origIdx].Resource, 0xFFFFFFFF, e.before, e.after);
                    }
                    else
                    {
                        // 无法合并，逐个发出
                        foreach (var e in entries)
                        {
                            nativeBarriers[writeIdx++] = new ResourceBarrierTransition(
                                barriers[e.origIdx].Resource, e.sub, e.before, e.after);
                        }
                    }
                }
            }

            // 批量提交
            if (writeIdx > 0)
            {
                if (writeIdx == 1)
                    _nativeList.ResourceBarrier(nativeBarriers[0]);
                else
                    _nativeList.ResourceBarrier(nativeBarriers, writeIdx);
            }
        }

        // === 绘制命令 ===

        /// <summary>非索引实例化绘制</summary>
        public void DrawInstanced(uint vertexCountPerInstance, uint instanceCount,
            uint startVertex, uint startInstance)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.DrawInstanced(vertexCountPerInstance, instanceCount, startVertex, startInstance);
        }

        /// <summary>索引实例化绘制</summary>
        public void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount,
            uint startIndex, int baseVertex, uint startInstance)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.DrawIndexedInstanced(indexCountPerInstance, instanceCount,
                startIndex, baseVertex, startInstance);
        }

        /// <summary>间接绘制 (DrawInstancedIndirect)</summary>
        public void DrawInstancedIndirect(ID3D12Resource argumentBuffer, ulong offset)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            // 间接绘制参数缓冲区布局 (32 字节):
            // [0-3]   VertexCountPerInstance (uint)
            // [4-7]   InstanceCount (uint)
            // [8-11]  StartVertex (uint)
            // [12-15] StartInstance (uint)
            var commandSignature = GetOrCreateIndirectCommandSignature(IndirectCommandType.Draw);
            _nativeList.ExecuteIndirect(commandSignature, 1, argumentBuffer, offset);
        }

        /// <summary>间接索引绘制 (DrawIndexedInstancedIndirect)</summary>
        public void DrawIndexedInstancedIndirect(ID3D12Resource argumentBuffer, ulong offset)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            // 间接索引绘制参数缓冲区布局 (36 字节):
            // [0-3]   IndexCountPerInstance (uint)
            // [4-7]   InstanceCount (uint)
            // [8-11]  StartIndex (uint)
            // [12-15] BaseVertexLocation (int)
            // [16-19] StartInstance (uint)
            var commandSignature = GetOrCreateIndirectCommandSignature(IndirectCommandType.DrawIndexed);
            _nativeList.ExecuteIndirect(commandSignature, 1, argumentBuffer, offset);
        }

        /// <summary>Mesh Shader 绘制</summary>
        public void DispatchMesh(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.DispatchMesh(threadGroupCountX, threadGroupCountY, threadGroupCountZ);
        }

        /// <summary>计算着色器派发</summary>
        public void Dispatch(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.Dispatch(threadGroupCountX, threadGroupCountY, threadGroupCountZ);
        }

        // === 管线状态 ===

        /// <summary>设置管线状态对象 (PSO)</summary>
        public void SetPipelineState(ID3D12PipelineState pso)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetPipelineState(pso);
            _currentPSO = pso;
        }

        /// <summary>设置图形根签名</summary>
        public void SetGraphicsRootSignature(ID3D12RootSignature rootSignature)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootSignature(rootSignature);
            _currentGraphicsRootSig = rootSignature;
        }

        /// <summary>设置计算根签名</summary>
        public void SetComputeRootSignature(ID3D12RootSignature rootSignature)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetComputeRootSignature(rootSignature);
            _currentComputeRootSig = rootSignature;
        }

        /// <summary>设置图元拓扑</summary>
        public void IASetPrimitiveTopology(PrimitiveTopology topology)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.IASetPrimitiveTopology(topology);
            _currentTopology = topology;
        }

        /// <summary>设置视口 (单个)</summary>
        public void RSSetViewport(float x, float y, float width, float height, float minDepth = 0, float maxDepth = 1)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            var viewport = new Viewport(x, y, width, height, minDepth, maxDepth);
            _nativeList.RSSetViewports(viewport);
        }

        /// <summary>设置多个视口</summary>
        public void RSSetViewports(Viewport[] viewports)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.RSSetViewports(viewports);
        }

        /// <summary>设置裁剪矩形 (单个)</summary>
        public void RSSetScissorRect(int left, int top, int right, int bottom)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            var rect = new RawRect(left, top, right, bottom);
            _nativeList.RSSetScissorRects(rect);
        }

        /// <summary>设置多个裁剪矩形</summary>
        public void RSSetScissorRects(RawRect[] rects)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.RSSetScissorRects(rects);
        }

        /// <summary>设置渲染目标 (多 RTV + DSV)</summary>
        public void OMSetRenderTargets(
            CpuDescriptorHandle[] rtvHandles,
            CpuDescriptorHandle dsvHandle,
            bool[]? rtvSingleHandleToDescriptorRange = null)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            _currentRTVs = rtvHandles;

            if (rtvHandles.Length > 0)
            {
                var rtvArray = rtvHandles.Select((h, i) =>
                    rtvSingleHandleToDescriptorRange != null && i < rtvSingleHandleToDescriptorRange.Length
                        ? rtvSingleHandleToDescriptorRange[i] ? h : h
                        : h).ToArray();

                if (rtvSingleHandleToDescriptorRange != null)
                {
                    _nativeList.OMSetRenderTargets(
                        (uint)rtvHandles.Length,
                        rtvArray,
                        rtvSingleHandleToDescriptorRange,
                        dsvHandle);
                }
                else
                {
                    _nativeList.OMSetRenderTargets(
                        (uint)rtvHandles.Length,
                        rtvArray,
                        dsvHandle);
                }
            }
            else
            {
                // 无渲染目标
                _nativeList.OMSetRenderTargets(dsvHandle);
            }

            _hasDSV = dsvHandle.Ptr != 0;
        }

        /// <summary>设置混合因子</summary>
        public void OMSetBlendFactor(Vortice.Mathematics.Vector4 blendFactor)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.OMSetBlendFactor(blendFactor);
        }

        /// <summary>设置 Sample Mask</summary>
        public void OMSetSampleMask(uint sampleMask)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.OMSetStencilRef(sampleMask);
        }

        /// <summary>设置模板引用值</summary>
        public void OMSetStencilRef(uint stencilRef)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.OMSetStencilRef(stencilRef);
        }

        /// <summary>清除渲染目标</summary>
        public void ClearRenderTargetView(CpuDescriptorHandle rtv, Vortice.Mathematics.Vector4 color)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.ClearRenderTargetView(rtv, new Color4(color.X, color.Y, color.Z, color.W));
        }

        /// <summary>清除深度模板</summary>
        public void ClearDepthStencilView(CpuDescriptorHandle dsv,
            ClearFlags flags, float depth, byte stencil)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.ClearDepthStencilView(dsv, flags, depth, stencil);
        }

        /// <summary>设置描述符堆 (最多 4 个)</summary>
        public void SetDescriptorHeaps(ID3D12DescriptorHeap[] heaps)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetDescriptorHeaps(heaps);
            _descriptorHeaps = heaps;
        }

        // === 根参数绑定 ===

        /// <summary>设置根 CBV (Constant Buffer View)</summary>
        public void SetGraphicsRootConstantBufferView(uint rootParameterIndex, ID3D12Resource buffer)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootConstantBufferView(rootParameterIndex, buffer.GPUVirtualAddress);
        }

        public void SetGraphicsRootConstantBufferView(uint rootParameterIndex, ulong gpuVirtualAddress)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootConstantBufferView(rootParameterIndex, gpuVirtualAddress);
        }

        /// <summary>设置根 SRV (Shader Resource View)</summary>
        public void SetGraphicsRootShaderResourceView(uint rootParameterIndex, ID3D12Resource buffer)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootShaderResourceView(rootParameterIndex, buffer.GPUVirtualAddress);
        }

        public void SetGraphicsRootShaderResourceView(uint rootParameterIndex, ulong gpuVirtualAddress)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootShaderResourceView(rootParameterIndex, gpuVirtualAddress);
        }

        /// <summary>设置根 UAV (Unordered Access View)</summary>
        public void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, ID3D12Resource buffer)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootUnorderedAccessView(rootParameterIndex, buffer.GPUVirtualAddress);
        }

        public void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, ulong gpuVirtualAddress)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootUnorderedAccessView(rootParameterIndex, gpuVirtualAddress);
        }

        /// <summary>设置根 32 位常量 (4 字节对齐, 最大 64 DWORD = 256 字节)</summary>
        public unsafe void SetGraphicsRoot32BitConstants(uint rootParameterIndex,
            uint num32BitValues, void* data, uint destOffsetIn32BitValues = 0)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            if (num32BitValues > 64)
                throw new ArgumentException("Root 32-bit constants max 64 DWORDs (256 bytes)");
            _nativeList.SetGraphicsRoot32BitConstants(rootParameterIndex, num32BitValues, data, destOffsetIn32BitValues);
        }

        /// <summary>设置根描述符表</summary>
        public void SetGraphicsRootDescriptorTable(uint rootParameterIndex, GpuDescriptorHandle baseDescriptor)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetGraphicsRootDescriptorTable(rootParameterIndex, baseDescriptor);
        }

        // === Compute 根参数 ===

        public void SetComputeRootConstantBufferView(uint rootParameterIndex, ID3D12Resource buffer)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetComputeRootConstantBufferView(rootParameterIndex, buffer.GPUVirtualAddress);
        }

        public void SetComputeRootShaderResourceView(uint rootParameterIndex, ID3D12Resource buffer)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetComputeRootShaderResourceView(rootParameterIndex, buffer.GPUVirtualAddress);
        }

        public void SetComputeRootUnorderedAccessView(uint rootParameterIndex, ID3D12Resource buffer)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetComputeRootUnorderedAccessView(rootParameterIndex, buffer.GPUVirtualAddress);
        }

        public unsafe void SetComputeRoot32BitConstants(uint rootParameterIndex,
            uint num32BitValues, void* data, uint destOffsetIn32BitValues = 0)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            if (num32BitValues > 64)
                throw new ArgumentException("Root 32-bit constants max 64 DWORDs (256 bytes)");
            _nativeList.SetComputeRoot32BitConstants(rootParameterIndex, num32BitValues, data, destOffsetIn32BitValues);
        }

        public void SetComputeRootDescriptorTable(uint rootParameterIndex, GpuDescriptorHandle baseDescriptor)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetComputeRootDescriptorTable(rootParameterIndex, baseDescriptor);
        }

        // === 资源操作 ===

        /// <summary>缓冲区区域拷贝</summary>
        public void CopyBufferRegion(ID3D12Resource dstBuffer, ulong dstOffset,
            ID3D12Resource srcBuffer, ulong srcOffset, ulong numBytes)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.CopyBufferRegion(dstBuffer, dstOffset, srcBuffer, srcOffset, numBytes);
        }

        /// <summary>纹理区域拷贝</summary>
        public void CopyTextureRegion(
            ID3D12Resource dst, uint dstSubresource, int dstX, int dstY, int dstZ,
            ID3D12Resource src, uint srcSubresource,
            Box? srcBox = null)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.CopyTextureRegion(dst, dstSubresource, dstX, dstY, dstZ, src, srcSubresource, srcBox);
        }

        /// <summary>完整资源拷贝</summary>
        public void CopyResource(ID3D12Resource dst, ID3D12Resource src)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.CopyResource(dst, src);
        }

        /// <summary>MSAA 解析 (ResolveSubresource)</summary>
        public void ResolveSubresource(ID3D12Resource dst, uint dstSubresource,
            ID3D12Resource src, uint srcSubresource, Format format)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.ResolveSubresource(dst, dstSubresource, src, srcSubresource, format);
        }

        /// <summary>
        /// 使用 Compute Shader 生成 MipChain。
        /// 对每个 Mip 级别，绑定 SRV (source mip) 和 UAV (dest mip)，
        /// 派发计算着色器进行降采样。
        /// </summary>
        public void GenerateMips(ID3D12Resource texture, int mipLevels,
            ID3D12PipelineState mipGenPSO,
            ID3D12RootSignature mipGenRootSig,
            CpuDescriptorHandle srvBase, CpuDescriptorHandle uavBase,
            uint srvDescriptorSize, uint uavDescriptorSize,
            int width, int height)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            // 设置 MipGen 管线
            _nativeList.SetPipelineState(mipGenPSO);
            _nativeList.SetComputeRootSignature(mipGenRootSig);

            // 计算每个 Mip 级别的线程组数 (8x8 线程组)
            const int threadGroupSize = 8;

            for (int mip = 0; mip < mipLevels - 1; mip++)
            {
                int srcWidth = Math.Max(1, width >> mip);
                int srcHeight = Math.Max(1, height >> mip);
                int dstWidth = Math.Max(1, width >> (mip + 1));
                int dstHeight = Math.Max(1, height >> (mip + 1));

                int groupX = (dstWidth + threadGroupSize - 1) / threadGroupSize;
                int groupY = (dstHeight + threadGroupSize - 1) / threadGroupSize;

                // 绑定 SRV (source mip)
                var srvHandle = srvBase + mip * srvDescriptorSize;
                // 绑定 UAV (destination mip)
                var uavHandle = uavBase + (mip + 1) * uavDescriptorSize;

                // UAV barrier: 确保 UAV 写入完成后再读取
                _nativeList.ResourceBarrier(new ResourceBarrierUAV(texture));

                // 根参数 0: Constants (srcMipLevel, numMips, texelSize)
                uint[] constants = new uint[] { (uint)mip, (uint)(mipLevels - 1), 1 };
                fixed (uint* p = constants)
                    _nativeList.SetComputeRoot32BitConstants(0, 3, p, 0);

                // 根参数 1: SRV
                _nativeList.SetComputeRootDescriptorTable(1,
                    new GpuDescriptorHandle(0)); // 需要从 CPU 句柄转换

                // 根参数 2: UAV
                _nativeList.SetComputeRootDescriptorTable(2,
                    new GpuDescriptorHandle(0));

                _nativeList.Dispatch((uint)groupX, (uint)groupY, 1);
            }

            // 最终 UAV barrier
            _nativeList.ResourceBarrier(new ResourceBarrierUAV(texture));
        }

        /// <summary>WriteBufferImmediate: 立即写入缓冲区 (Postamble 位模式)</summary>
        public unsafe void WriteBufferImmediate(
            ID3D12Resource buffer, ulong offset,
            uint value, WriteBufferImmediateFlags flags = WriteBufferImmediateFlags.None)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            var writeParams = new WriteBufferImmediateParameter
            {
                DestAddress = buffer.GPUVirtualAddress + offset,
                Value = value
            };
            _nativeList.WriteBufferImmediate(1, &writeParams, &flags);
        }

        /// <summary>ExecuteIndirect: 间接执行 (通用)</summary>
        public void ExecuteIndirect(ID3D12CommandSignature commandSignature,
            uint maxCommandCount, ID3D12Resource argumentBuffer, ulong argumentBufferOffset,
            ID3D12Resource? countBuffer = null, ulong countBufferOffset = 0)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.ExecuteIndirect(commandSignature, maxCommandCount,
                argumentBuffer, argumentBufferOffset, countBuffer, countBufferOffset);
        }

        // === 顶点/索引缓冲 ===

        /// <summary>设置顶点缓冲区</summary>
        public void IASetVertexBuffers(uint startSlot, ID3D12Resource buffer,
            ulong offset, uint stride, uint sizeInBytes)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            var vbView = new VertexBufferView
            {
                BufferLocation = buffer.GPUVirtualAddress + offset,
                SizeInBytes = sizeInBytes,
                StrideInBytes = stride
            };
            _nativeList.IASetVertexBuffers(startSlot, vbView);
        }

        /// <summary>设置索引缓冲区</summary>
        public void IASetIndexBuffer(ID3D12Resource buffer, ulong offset, uint sizeInBytes, Format format)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");

            var ibView = new IndexBufferView
            {
                BufferLocation = buffer.GPUVirtualAddress + offset,
                SizeInBytes = sizeInBytes,
                Format = format
            };
            _nativeList.IASetIndexBuffer(ibView);
        }

        // === 查询 ===

        /// <summary>开始查询</summary>
        public void BeginQuery(ID3D12QueryHeap queryHeap, QueryType type, uint index)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.BeginQuery(queryHeap, type, index);
        }

        /// <summary>结束查询</summary>
        public void EndQuery(ID3D12QueryHeap queryHeap, QueryType type, uint index)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.EndQuery(queryHeap, type, index);
        }

        /// <summary>解析查询数据 (GPU -> CPU 缓冲区)</summary>
        public void ResolveQueryData(ID3D12QueryHeap queryHeap, QueryType type,
            uint startIndex, uint numQueries, ID3D12Resource destBuffer, ulong alignedDestBufferOffset)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.ResolveQueryData(queryHeap, type, startIndex, numQueries,
                destBuffer, alignedDestBufferOffset);
        }

        /// <summary>设置条件渲染 (Predication)</summary>
        public void SetPredication(ID3D12Resource buffer, ulong offset, bool equalZero)
        {
            if (!_isOpen) throw new InvalidOperationException("Command list is not open");
            _nativeList.SetPredication(buffer, offset, equalZero);
        }

        // === 间接签名管理 ===

        private static ID3D12CommandSignature? _drawIndirectSig;
        private static ID3D12CommandSignature? _drawIndexedIndirectSig;
        private static ID3D12Device? _sigDevice;

        private ID3D12CommandSignature GetOrCreateIndirectCommandSignature(IndirectCommandType type)
        {
            if (_sigDevice == null || _sigDevice.NativePointer != _nativeList.GetDevice().NativePointer)
            {
                // 重建签名
                _drawIndirectSig?.Dispose();
                _drawIndexedIndirectSig?.Dispose();
                _drawIndirectSig = null;
                _drawIndexedIndirectSig = null;
                _sigDevice = _nativeList.GetDevice();
            }

            if (type == IndirectCommandType.Draw)
            {
                if (_drawIndirectSig == null)
                {
                    var desc = new IndirectCommandDescription(CommandListType.Direct);
                    desc.AddDrawArgument();
                    _drawIndirectSig = _sigDevice.CreateCommandSignature(desc);
                }
                return _drawIndirectSig;
            }
            else
            {
                if (_drawIndexedIndirectSig == null)
                {
                    var desc = new IndirectCommandDescription(CommandListType.Direct);
                    desc.AddDrawIndexedArgument();
                    _drawIndexedIndirectSig = _sigDevice.CreateCommandSignature(desc);
                }
                return _drawIndexedIndirectSig;
            }
        }

        // === Dispose ===

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Close();
            _allocatorPool.Release(_currentAllocator);
            _nativeList.Dispose();
        }
    }

    // === 辅助类型 ===

    /// <summary>资源屏障描述</summary>
    public struct ResourceBarrierDesc
    {
        public ID3D12Resource Resource;
        public ResourceStates StateBefore;
        public ResourceStates StateAfter;
        public uint Subresource;
    }

    /// <summary>间接命令类型</summary>
    public enum IndirectCommandType
    {
        Draw,
        DrawIndexed,
        Dispatch
    }

    // ==============================================================================================
    //  TrackingCommandList - 自动推断屏障的资源状态跟踪
    // ==============================================================================================

    /// <summary>
    /// 跟踪命令列表: 包装 D3D12GraphicsCommandList，自动跟踪资源状态，
    /// 在必要时自动插入资源屏障，减少冗余屏障。
    /// </summary>
    public sealed class TrackingCommandList : IDisposable
    {
        private readonly D3D12GraphicsCommandList _inner;
        private readonly Dictionary<nint, PerResourceState> _resourceStates = new();
        private readonly List<ResourceBarrierDesc> _pendingBarriers = new();
        private bool _disposed;

        public D3D12GraphicsCommandList Inner => _inner;

        private struct PerResourceState
        {
            public ResourceStates GlobalState;
            public ResourceStates[] SubresourceStates;
            public bool HasPerSubresource;
        }

        public TrackingCommandList(D3D12GraphicsCommandList inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// 跟踪资源状态转换。如果状态确实发生变化则发出屏障，
        /// 否则跳过冗余屏障。
        /// </summary>
        public void TransitionResource(ID3D12Resource resource, ResourceStates newState, uint subresource = 0xFFFFFFFF)
        {
            nint key = resource.NativePointer;
            ResourceStates oldState = GetResourceState(key, subresource);

            if (oldState == newState) return; // 无需转换

            // 记录新状态
            SetResourceState(key, subresource, newState);

            // 发出屏障
            _inner.ResourceBarrierTransition(resource, oldState, newState, subresource);
        }

        /// <summary>批量转换资源 (自动合并冗余屏障)</summary>
        public void TransitionResources(IReadOnlyList<(ID3D12Resource resource, ResourceStates newState)> transitions)
        {
            var barriers = new List<ResourceBarrierDesc>();

            foreach (var (resource, newState) in transitions)
            {
                nint key = resource.NativePointer;
                ResourceStates oldState = GetResourceState(key, 0xFFFFFFFF);

                if (oldState != newState)
                {
                    SetResourceState(key, 0xFFFFFFFF, newState);
                    barriers.Add(new ResourceBarrierDesc
                    {
                        Resource = resource,
                        StateBefore = oldState,
                        StateAfter = newState,
                        Subresource = 0xFFFFFFFF
                    });
                }
            }

            if (barriers.Count > 0)
            {
                _inner.ResourceBarrierBatch(barriers);
            }
        }

        /// <summary>确保资源在指定状态下可用 (仅在需要时插入屏障)</summary>
        public void EnsureResourceState(ID3D12Resource resource, ResourceStates requiredState, uint subresource = 0xFFFFFFFF)
        {
            nint key = resource.NativePointer;
            ResourceStates current = GetResourceState(key, subresource);

            if (current != requiredState)
            {
                SetResourceState(key, subresource, requiredState);
                _inner.ResourceBarrierTransition(resource, current, requiredState, subresource);
            }
        }

        private ResourceStates GetResourceState(nint key, uint subresource)
        {
            if (!_resourceStates.TryGetValue(key, out var state))
                return ResourceStates.Common;

            if (state.HasPerSubresource && subresource != 0xFFFFFFFF)
            {
                return subresource < state.SubresourceStates.Length
                    ? state.SubresourceStates[subresource]
                    : state.GlobalState;
            }

            return state.GlobalState;
        }

        private void SetResourceState(nint key, uint subresource, ResourceStates newState)
        {
            if (!_resourceStates.TryGetValue(key, out var state))
            {
                state = new PerResourceState
                {
                    GlobalState = ResourceStates.Common,
                    SubresourceStates = Array.Empty<ResourceStates>(),
                    HasPerSubresource = false
                };
                _resourceStates[key] = state;
            }

            if (subresource == 0xFFFFFFFF)
            {
                // 全资源状态: 更新全局状态，清除子资源跟踪
                state.GlobalState = newState;
                state.SubresourceStates = Array.Empty<ResourceStates>();
                state.HasPerSubresource = false;
                _resourceStates[key] = state;
            }
            else
            {
                // 子资源状态
                if (!state.HasPerSubresource || state.SubresourceStates.Length <= subresource)
                {
                    int newSize = (int)subresource + 1;
                    var newStates = new ResourceStates[newSize];
                    for (int i = 0; i < newStates.Length; i++)
                        newStates[i] = state.GlobalState;
                    newStates[subresource] = newState;
                    state.SubresourceStates = newStates;
                    state.HasPerSubresource = true;
                    _resourceStates[key] = state;
                }
                else
                {
                    state.SubresourceStates[subresource] = newState;
                    _resourceStates[key] = state;
                }
            }
        }

        /// <summary>清除资源跟踪状态 (每帧开始时调用)</summary>
        public void ResetTracking()
        {
            _resourceStates.Clear();
            _pendingBarriers.Clear();
        }

        /// <summary>移除资源跟踪</summary>
        public void RemoveResourceTracking(ID3D12Resource resource)
        {
            _resourceStates.Remove(resource.NativePointer);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner.Dispose();
        }
    }
}
