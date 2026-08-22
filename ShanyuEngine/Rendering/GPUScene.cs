using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 GPU Scene 管理 (GPU 驱动数据结构)
    //
    // 核心思想: 将整个场景的实例数据、裁剪结果、HZB 与反馈数据全部驻留 GPU,
    // CPU 端仅通过脏标记 + 增量上传更新变化部分, 实现零回读的 GPU 驱动渲染。
    //
    //  1. 实例数据缓冲: GPU 端实例数据 (Transform/Bounds/MeshIndex/MaterialIndex/LOD)
    //     采用脏标记 + 增量上传策略, 避免每帧全量上传。
    //  2. 裁剪结果缓冲: 可见实例列表 / 可见 Cluster 列表 / Draw 间接参数缓冲
    //     全部由 Compute Shader 写入, 供 DrawIndexedInstancedIndirect / DispatchMeshIndirect 消费。
    //  3. HZB (层次 Z 缓冲): Compute Shader mipmap 生成, 遮挡剔除, 增量更新。
    //  4. 反馈缓冲: Page 加载请求 / Probe 更新请求 / LOD 反馈, GPU 写入后由 CPU 异步回读处理。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>GPU 实例数据: 单个渲染实例的 GPU 端表示 (与 HLSL struct 对齐)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUInstanceData
    {
        /// <summary>世界变换矩阵的行 0 (X 基向量 + 平移 x)。</summary>
        public Vector4 TransformRow0;
        /// <summary>世界变换矩阵的行 1 (Y 基向量 + 平移 y)。</summary>
        public Vector4 TransformRow1;
        /// <summary>世界变换矩阵的行 2 (Z 基向量 + 平移 z)。</summary>
        public Vector4 TransformRow2;
        /// <summary>实例包围球中心 (世界空间)。</summary>
        public Vector3 BoundsCenter;
        /// <summary>实例包围球半径。</summary>
        public float BoundsRadius;
        /// <summary>所属网格资产索引 (GPU 端 mesh table 索引)。</summary>
        public uint MeshIndex;
        /// <summary>材质索引数组起始 (每个 mesh slot 一个)。</summary>
        public uint MaterialIndexBase;
        /// <summary>当前 LOD 层级 (GPU 运行时更新)。</summary>
        public uint LodLevel;
        /// <summary>实例自定义数据 (位掩码: cast shadow / visible / etc)。</summary>
        public uint Flags;
        /// <summary>上一帧世界位置 (用于运动向量/时序重投影)。</summary>
        public Vector3 PrevBoundsCenter;
        /// <summary>实例唯一 ID (用于 picking / 反馈)。</summary>
        public uint InstanceId;
        /// <summary>上一帧屏幕包围盒 minXY (用于 HZB 增量更新区域)。</summary>
        public Vector2 PrevScreenMin;
        /// <summary>上一帧屏幕包围盒 maxXY。</summary>
        public Vector2 PrevScreenMax;

        /// <summary>实例是否可见 (Flags bit 0)。</summary>
        public bool IsVisible => (Flags & 1u) != 0;
        /// <summary>是否投射阴影 (Flags bit 1)。</summary>
        public bool CastShadow => (Flags & 2u) != 0;

        /// <summary>从位置/旋转/缩放构建三行变换矩阵。</summary>
        public static GPUInstanceData FromTransform(Vector3 position, Quaternion rotation, Vector3 scale,
            uint meshIndex, uint materialIndexBase, uint instanceId)
        {
            var m = Matrix4x4.CreateFromQuaternion(rotation);
            m.Translation = position;
            // 列缩放
            m.M11 *= scale.X; m.M12 *= scale.X; m.M13 *= scale.X;
            m.M21 *= scale.Y; m.M22 *= scale.Y; m.M23 *= scale.Y;
            m.M31 *= scale.Z; m.M32 *= scale.Z; m.M33 *= scale.Z;

            return new GPUInstanceData
            {
                TransformRow0 = new Vector4(m.M11, m.M12, m.M13, m.M41),
                TransformRow1 = new Vector4(m.M21, m.M22, m.M23, m.M42),
                TransformRow2 = new Vector4(m.M31, m.M32, m.M33, m.M43),
                MeshIndex = meshIndex,
                MaterialIndexBase = materialIndexBase,
                InstanceId = instanceId,
                Flags = 1u, // 默认可见
                PrevBoundsCenter = position
            };
        }
    }

    /// <summary>间接绘制参数 (匹配 D3D12 DrawIndexedInstanced 间接参数布局)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DrawIndexedIndirectArgs
    {
        public uint IndexCountPerInstance;
        public uint InstanceCount;
        public uint StartIndexLocation;
        public int BaseVertexLocation;
        public uint StartInstanceLocation;
    }

    /// <summary>Mesh Shader 间接分发参数 (匹配 D3D12 DispatchMesh 间接参数)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DispatchMeshIndirectArgs
    {
        public uint GroupCountX;
        public uint GroupCountY;
        public uint GroupCountZ;
    }

    /// <summary>Cluster 间接绘制参数 (每簇记录, 用于软件光栅化分派)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusterDrawArgs
    {
        public uint ClusterIndex;
        public uint MeshId;
        public uint TriangleCount;
        public uint Pad;
    }

    /// <summary>可见性反馈记录: GPU 写入、CPU 回读的反馈条目。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VisibilityFeedback
    {
        /// <summary>请求加载的 Page ID。</summary>
        public uint RequestedPageId;
        /// <summary>请求更新探针的线性索引。</summary>
        public uint RequestedProbeId;
        /// <summary>实例的运行时 LOD 选择结果。</summary>
        public uint LodFeedbackInstanceId;
        /// <summary>该实例实际使用的 LOD 层级。</summary>
        public uint LodFeedbackLevel;
    }

    /// <summary>HZB (层次 Z 缓冲) 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HZBConstants
    {
        public Vector2 InvResolution;
        public uint MipCount;
        public uint Pad0;
        public Vector2 InputInvResolution;
        public float Pad1;
        public float Pad2;
    }

    /// <summary>
    /// GPU Scene: 管理全部 GPU 驱动数据结构的容器。
    /// 负责实例缓冲的增量上传、裁剪结果缓冲分配、HZB 生成与反馈缓冲回读。
    /// </summary>
    public sealed class GPUScene : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly int _maxInstances;
        private readonly int _maxVisibleInstances;
        private readonly int _maxVisibleClusters;

        // --- 实例数据缓冲 ---
        private RHIBuffer? _instanceBuffer;
        private RHIBuffer? _instanceUploadBuffer;
        private RHIBuffer? _instanceCounterBuffer;
        private readonly HashSet<uint> _dirtyInstances = new();
        private GPUInstanceData[] _cpuInstances;
        private uint _activeInstanceCount;

        // --- 裁剪结果缓冲 ---
        private RHIBuffer? _visibleInstanceBuffer;
        private RHIBuffer? _visibleClusterBuffer;
        private RHIBuffer? _meshShaderIndirectArgs;
        private RHIBuffer? _drawIndexedIndirectArgs;
        private RHIBuffer? _cullCounterBuffer;

        // --- HZB ---
        private RHITexture? _hzbTexture;
        private RHITexture? _depthSource;
        private RHIPipelineState? _hzbBuildPipeline;
        private RHIPipelineState? _hzbDownsamplePipeline;
        private int _hzbMipCount;
        private int _hzbWidth;
        private int _hzbHeight;

        // --- 反馈缓冲 ---
        private RHIBuffer? _feedbackBuffer;
        private RHIBuffer? _feedbackReadbackBuffer;
        private RHIFence? _feedbackFence;
        private ulong _feedbackFenceValue;
        private readonly Queue<VisibilityFeedback[]> _pendingFeedback = new();

        /// <summary>最大实例数。</summary>
        public int MaxInstances => _maxInstances;
        /// <summary>当前活跃实例数。</summary>
        public uint ActiveInstanceCount => _activeInstanceCount;
        /// <summary>实例数据缓冲 (StructuredBuffer, GPU 端读取)。</summary>
        public RHIBuffer? InstanceBuffer => _instanceBuffer;
        /// <summary>可见实例列表 (UAV, 剔除 CS 写入)。</summary>
        public RHIBuffer? VisibleInstanceBuffer => _visibleInstanceBuffer;
        /// <summary>可见 Cluster 列表 (UAV)。</summary>
        public RHIBuffer? VisibleClusterBuffer => _visibleClusterBuffer;
        /// <summary>Mesh Shader 间接分发参数缓冲。</summary>
        public RHIBuffer? MeshShaderIndirectArgs => _meshShaderIndirectArgs;
        /// <summary>DrawIndexedIndirect 参数缓冲。</summary>
        public RHIBuffer? DrawIndexedIndirectArgs => _drawIndexedIndirectArgs;
        /// <summary>裁剪计数缓冲 (可见实例/簇数量, 原子累加)。</summary>
        public RHIBuffer? CullCounterBuffer => _cullCounterBuffer;
        /// <summary>HZB 纹理 (遮挡剔除用)。</summary>
        public RHITexture? HZBTexture => _hzbTexture;
        /// <summary>HZB mip 层数。</summary>
        public int HZBMipCount => _hzbMipCount;
        /// <summary>反馈缓冲 (UAV, GPU 写入)。</summary>
        public RHIBuffer? FeedbackBuffer => _feedbackBuffer;

        /// <summary>构造 GPU Scene。</summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="maxInstances">最大实例数。</param>
        /// <param name="maxVisibleInstances">最大可见实例数 (裁剪结果容量)。</param>
        /// <param name="maxVisibleClusters">最大可见簇数。</param>
        public GPUScene(RHIDevice device, int maxInstances = 65536,
            int maxVisibleInstances = 32768, int maxVisibleClusters = 262144)
        {
            _device = device;
            _maxInstances = maxInstances;
            _maxVisibleInstances = maxVisibleInstances;
            _maxVisibleClusters = maxVisibleClusters;
            _cpuInstances = new GPUInstanceData[maxInstances];
        }

        /// <summary>分配所有 GPU 缓冲资源。</summary>
        public void Initialize()
        {
            int instanceStride = Marshal.SizeOf<GPUInstanceData>();

            // 实例数据缓冲 (默认堆, SRV)
            _instanceBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_maxInstances * instanceStride),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)instanceStride,
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_InstanceBuffer");

            // 实例上传缓冲 (Upload 堆, 用于增量上传)
            _instanceUploadBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_maxInstances * instanceStride),
                Flags = RHIBufferFlags.Structured,
                Stride = (ulong)instanceStride,
                HeapType = RHIResourceHeapType.Upload
            }, "GPUScene_InstanceUpload");

            // 实例计数缓冲 (活跃实例数, 供 CS 使用)
            _instanceCounterBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 16,
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.Structured,
                Stride = 4,
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_InstanceCounter");

            // 裁剪结果缓冲
            _visibleInstanceBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_maxVisibleInstances * 16),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 16,
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_VisibleInstances");

            _visibleClusterBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_maxVisibleClusters * (ulong)Marshal.SizeOf<ClusterDrawArgs>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<ClusterDrawArgs>(),
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_VisibleClusters");

            _meshShaderIndirectArgs = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(Marshal.SizeOf<DispatchMeshIndirectArgs>() * 4),
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.IndirectArgs,
                Stride = (ulong)Marshal.SizeOf<DispatchMeshIndirectArgs>(),
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_MeshShaderIndirectArgs");

            _drawIndexedIndirectArgs = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(Marshal.SizeOf<DrawIndexedIndirectArgs>() * 16),
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.IndirectArgs,
                Stride = (ulong)Marshal.SizeOf<DrawIndexedIndirectArgs>(),
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_DrawIndexedIndirectArgs");

            _cullCounterBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 64, // 多个 32 位计数器
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.Structured,
                Stride = 4,
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_CullCounters");

            // 反馈缓冲
            int feedbackStride = Marshal.SizeOf<VisibilityFeedback>();
            _feedbackBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(4096 * feedbackStride),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess,
                Stride = (ulong)feedbackStride,
                HeapType = RHIResourceHeapType.Default
            }, "GPUScene_Feedback");

            _feedbackReadbackBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(4096 * feedbackStride),
                Flags = RHIBufferFlags.None,
                Stride = (ulong)feedbackStride,
                HeapType = RHIResourceHeapType.Readback
            }, "GPUScene_FeedbackReadback");

            _feedbackFence = _device.CreateFence(0, "GPUScene_FeedbackFence");

            CreateHZBPipelines();
        }

        /// <summary>创建 HZB 生成管线 (Compute Shader mipmap)。</summary>
        private void CreateHZBPipelines()
        {
            _hzbBuildPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "HZBBuild",
                CS = ShaderManager.Instance.GetBytecode("Deferred/HZBBuild", "HZBBuildCS", RHIShaderStage.Compute)
            });
        }

        // ──────────────────────────────────────────────────────────────
        // 实例管理 (CPU 侧, 脏标记增量上传)
        // ──────────────────────────────────────────────────────────────

        /// <summary>添加或更新一个实例 (标记为脏, 下次 Upload 时上传)。</summary>
        /// <returns>实例索引。</returns>
        public uint AddOrUpdateInstance(in GPUInstanceData instance)
        {
            // 查找已有实例 (按 InstanceId)
            for (int i = 0; i < _activeInstanceCount; i++)
            {
                if (_cpuInstances[i].InstanceId == instance.InstanceId)
                {
                    _cpuInstances[i] = instance;
                    _dirtyInstances.Add((uint)i);
                    return (uint)i;
                }
            }

            if (_activeInstanceCount >= _maxInstances)
                throw new InvalidOperationException("GPU Scene 实例数已达上限。");

            uint idx = _activeInstanceCount++;
            _cpuInstances[idx] = instance;
            _dirtyInstances.Add(idx);
            return idx;
        }

        /// <summary>标记实例为脏 (下次 Upload 重新上传)。</summary>
        public void MarkDirty(uint instanceIndex) => _dirtyInstances.Add(instanceIndex);

        /// <summary>移除实例 (标记为不可见, 不立即压缩数组)。</summary>
        public void RemoveInstance(uint instanceId)
        {
            for (int i = 0; i < _activeInstanceCount; i++)
            {
                if (_cpuInstances[i].InstanceId == instanceId)
                {
                    var inst = _cpuInstances[i];
                    inst.Flags = 0; // 清除可见标志
                    _cpuInstances[i] = inst;
                    _dirtyInstances.Add((uint)i);
                    return;
                }
            }
        }

        /// <summary>
        /// 将脏实例增量上传到 GPU。
        /// 采用脏标记策略: 仅上传变化的实例, 避免每帧全量上传。
        /// </summary>
        public void UploadDirtyInstances(RHICommandList cmdList)
        {
            if (_instanceBuffer == null || _instanceUploadBuffer == null || _dirtyInstances.Count == 0)
                return;

            int stride = Marshal.SizeOf<GPUInstanceData>();
            cmdList.TransitionTo(_instanceBuffer, RHIResourceState.CopyDest);

            // 全量上传 (首帧或脏实例过多时)
            if (_dirtyInstances.Count > _activeInstanceCount * 0.5)
            {
                // 将 CPU 数据写入上传缓冲, 再拷贝到默认堆
                _device.UploadToBuffer(_instanceUploadBuffer, _cpuInstances.AsSpan(0, (int)_activeInstanceCount));
                cmdList.CopyBufferRegion(_instanceBuffer, 0, _instanceUploadBuffer, 0,
                    (ulong)(_activeInstanceCount * stride));
            }
            else
            {
                // 增量上传: 仅上传脏实例
                foreach (uint idx in _dirtyInstances)
                {
                    if (idx >= _activeInstanceCount) continue;
                    _device.UploadToBuffer(_instanceUploadBuffer, _cpuInstances.AsSpan((int)idx, 1), (ulong)(idx * stride));
                    cmdList.CopyBufferRegion(_instanceBuffer, (ulong)(idx * stride),
                        _instanceUploadBuffer, 0, (ulong)stride);
                }
            }

            cmdList.TransitionTo(_instanceBuffer, RHIResourceState.NonPixelShaderResource);
            _dirtyInstances.Clear();
        }

        /// <summary>重置裁剪计数缓冲 (每帧剔除前调用)。</summary>
        public void ResetCullCounters(RHICommandList cmdList)
        {
            if (_cullCounterBuffer == null) return;
            cmdList.TransitionTo(_cullCounterBuffer, RHIResourceState.UnorderedAccess);
            cmdList.UAVBarrier(_cullCounterBuffer);
        }

        // ──────────────────────────────────────────────────────────────
        // HZB (层次 Z 缓冲)
        // ──────────────────────────────────────────────────────────────

        /// <summary>根据深度缓冲分辨率重建 HZB 纹理。</summary>
        public void ResizeHZB(int width, int height)
        {
            _hzbWidth = Math.Max(8, width);
            _hzbHeight = Math.Max(8, height);
            // HZB mip 数 = ceil(log2(max(w,h))) (每级减半, 取最近深度)
            int maxDim = Math.Max(_hzbWidth, _hzbHeight);
            _hzbMipCount = 1;
            int dim = maxDim;
            while (dim > 8)
            {
                dim >>= 1;
                _hzbMipCount++;
            }
            _hzbMipCount = Math.Min(_hzbMipCount, 14);

            _hzbTexture?.Dispose();
            _hzbTexture = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32_Float,
                Width = _hzbWidth, Height = _hzbHeight, DepthOrArraySize = 1,
                MipLevels = _hzbMipCount, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "GPUScene_HZB");
        }

        /// <summary>
        /// 从场景深度缓冲生成 HZB (层次 Z 缓冲)。
        /// 逐级降采样, 每级取 2x2 块的最远深度 (用于保守遮挡剔除)。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="depthSource">场景深度纹理 (D32_Float)。</param>
        public void BuildHZB(RHICommandList cmdList, RHITexture depthSource)
        {
            if (_hzbTexture == null || _hzbBuildPipeline == null) return;

            _depthSource = depthSource;
            int srcW = depthSource.TextureDesc!.Value.Width;
            int srcH = depthSource.TextureDesc!.Value.Height;
            if (_hzbTexture.TextureDesc!.Value.Width != srcW || _hzbTexture.TextureDesc!.Value.Height != srcH)
                ResizeHZB(srcW, srcH);

            cmdList.SetComputeRootSignature(null!);
            cmdList.SetPipelineState(_hzbBuildPipeline);

            // 第一级: 从深度缓冲降采样到 HZB mip 0
            cmdList.TransitionTo(depthSource, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_hzbTexture, RHIResourceState.UnorderedAccess);

            // 分派 HZB 构建 (每 mip 一级)
            int w = _hzbWidth, h = _hzbHeight;
            for (int mip = 0; mip < _hzbMipCount; mip++)
            {
                int groupsX = (w + 7) / 8;
                int groupsY = (h + 7) / 8;
                cmdList.Dispatch(Math.Max(1, groupsX), Math.Max(1, groupsY), 1);
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
            }

            cmdList.TransitionTo(_hzbTexture, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depthSource, RHIResourceState.DepthRead);
        }

        // ──────────────────────────────────────────────────────────────
        // 反馈缓冲 (GPU 写入 -> CPU 回读)
        // ──────────────────────────────────────────────────────────────

        /// <summary>请求将反馈缓冲复制到回读堆 (异步, 下一帧可读取)。</summary>
        public void RequestFeedbackReadback(RHICommandList cmdList, RHICommandQueue queue)
        {
            if (_feedbackBuffer == null || _feedbackReadbackBuffer == null || _feedbackFence == null) return;

            cmdList.TransitionTo(_feedbackBuffer, RHIResourceState.CopySource);
            cmdList.CopyResource(_feedbackReadbackBuffer, _feedbackBuffer);
            cmdList.TransitionTo(_feedbackBuffer, RHIResourceState.UnorderedAccess);

            _feedbackFenceValue++;
            queue.Signal(_feedbackFence, _feedbackFenceValue);
        }

        /// <summary>
        /// 尝试读取上一帧的反馈数据。
        /// 若 GPU 尚未完成, 返回 null。
        /// </summary>
        public VisibilityFeedback[]? TryReadFeedback()
        {
            if (_feedbackFence == null || _feedbackFence.CompletedValue < _feedbackFenceValue)
                return null;

            // 从回读堆映射读取 (简化: 返回空数组占位, 实际需 Map readback buffer)
            // 实际实现需通过 D3D12 Readback heap 的 Map 获取数据
            return Array.Empty<VisibilityFeedback>();
        }

        /// <summary>处理反馈: 解析 Page 加载请求与 Probe 更新请求。</summary>
        public void ProcessFeedback(
            Action<uint>? onPageRequest,
            Action<uint>? onProbeRequest,
            Action<uint, uint>? onLodFeedback)
        {
            while (_pendingFeedback.TryDequeue(out var feedback))
            {
                foreach (var fb in feedback)
                {
                    if (fb.RequestedPageId != 0xFFFFFFFF) onPageRequest?.Invoke(fb.RequestedPageId);
                    if (fb.RequestedProbeId != 0xFFFFFFFF) onProbeRequest?.Invoke(fb.RequestedProbeId);
                    if (fb.LodFeedbackInstanceId != 0xFFFFFFFF) onLodFeedback?.Invoke(fb.LodFeedbackInstanceId, fb.LodFeedbackLevel);
                }
            }
        }

        /// <summary>获取实例数据的 CPU 侧副本 (用于查询)。</summary>
        public ReadOnlySpan<GPUInstanceData> GetCpuInstances() => _cpuInstances.AsSpan(0, (int)_activeInstanceCount);

        /// <summary>释放所有 GPU 资源。</summary>
        public void Dispose()
        {
            _instanceBuffer?.Dispose();
            _instanceUploadBuffer?.Dispose();
            _instanceCounterBuffer?.Dispose();
            _visibleInstanceBuffer?.Dispose();
            _visibleClusterBuffer?.Dispose();
            _meshShaderIndirectArgs?.Dispose();
            _drawIndexedIndirectArgs?.Dispose();
            _cullCounterBuffer?.Dispose();
            _hzbTexture?.Dispose();
            _hzbBuildPipeline?.Dispose();
            _hzbDownsamplePipeline?.Dispose();
            _feedbackBuffer?.Dispose();
            _feedbackReadbackBuffer?.Dispose();
            _feedbackFence?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
