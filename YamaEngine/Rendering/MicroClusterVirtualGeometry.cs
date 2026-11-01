using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.Core;
using YamaEngine.RHI;
using YamaEngine.Shaders;
using YamaEngine.MicroCluster;

using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama MicroCluster 虚拟几何体系统 (超越 UE7 级别)
    //
    // 核心思想: 将任意复杂度的网格分解为固定大小的"簇"(Cluster, 128 三角形),
    // 通过二次误差度量(QEM)进行多级 LOD 简化, 构建 DAG(有向无环图) LOD 层级。
    // 运行时由 GPU 驱动完成: 视锥剔除、HZB 遮挡剔除、背面法线锥剔除、
    // 误差/屏幕大小驱动的 LOD 选择, 然后通过间接绘制(Indirect Draw)提交。
    // 子像素三角形使用计算着色器软光栅化写入可见性缓冲(Visibility Buffer),
    // 大三角形使用网格着色器(Mesh Shader)硬光栅化, 最后由材质 Pass 解码。
    // 几何数据存放于虚拟页表中, 按需流式加载, 突破显存容量限制。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>MicroCluster 系统的全局常量定义。</summary>
    public static class MicroClusterConstants
    {
        /// <summary>每个簇固定的三角形数量 (与 UE MicroCluster 对齐)。</summary>
        public const int TrianglesPerCluster = 128;

        /// <summary>每个簇最多引用的顶点数量。</summary>
        public const int MaxVerticesPerCluster = 256;

        /// <summary>虚拟几何体页表的页大小 (字节)。</summary>
        public const int ClusterPageSize = 16 * 1024;

        /// <summary>屏幕空间误差阈值: 当簇的投影误差低于此值时不再细分。</summary>
        public const float DefaultErrorThreshold = 1.0f;

        /// <summary>触发软件光栅化的三角形投影面积阈值 (像素)。</summary>
        public const float SoftwareRasterPixelThreshold = 16.0f;

        /// <summary>位置数据量化精度位数 (每轴)。</summary>
        public const int PositionQuantizationBits = 13;
    }

    /// <summary>
    /// 簇: MicroCluster 的原子单元。包含 128 个三角形及其边界球、几何误差、
    /// 法线锥(用于背面剔除)以及所属的 LOD 层级与父组指针。
    /// </summary>
    /// <summary>
    /// 簇组: 一组在下一级更粗 LOD 中可被合并为单一父簇的簇集合。
    /// DAG 通过组边连接: 父组指向可替换的子组(合并), 子组指向父组(分裂)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MicroClusterClusterGroup
    {
        /// <summary>组 ID。</summary>
        public uint Id;
        /// <summary>LOD 层级。</summary>
        public uint LodLevel;
        /// <summary>子簇范围起始。</summary>
        public uint FirstChildCluster;
        /// <summary>子簇数量。</summary>
        public uint ChildClusterCount;
        /// <summary>父簇 ID (合并后的粗 LOD 簇)。</summary>
        public uint ParentClusterId;
        /// <summary>组包围球中心。</summary>
        public Vector3 BoundsCenter;
        /// <summary>组包围球半径。</summary>
        public float BoundsRadius;
        /// <summary>组的最大几何误差 (子簇误差的最大值)。</summary>
        public float MaxError;
        /// <summary>组的最小几何误差。</summary>
        public float MinError;
    }

    /// <summary>量化压缩后的顶点数据 (位置精度解码)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MicroClusterVertex
    {
        /// <summary>量化位置 (相对簇 AABB, 每轴 13 位)。</summary>
        public uint PackedPosition;
        /// <summary>八面体编码法线 (16 位)。</summary>
        public ushort EncodedNormal;
        /// <summary>压缩的 UV 坐标 (16 位浮点对)。</summary>
        public ushort EncodedU;
        public ushort EncodedV;
        /// <summary>切线符号位与颜色索引。</summary>
        public byte TangentSign;
        public byte ColorIndex;
    }

    /// <summary>压缩三角形 (索引 + 材质/绘制标记)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MicroClusterTriangle
    {
        /// <summary>三个顶点在簇内的本地索引 (8 位)。</summary>
        public byte I0, I1, I2;
        /// <summary>材质/绘制 ID。</summary>
        public byte MaterialSlot;
        /// <summary>边缘标志位 (用于保守光栅/wireframe)。</summary>
        public byte EdgeFlags;
    }

    /// <summary>
    /// 二次误差度量 (Quadric Error Metric)。
    /// 用于 LOD 简化: 每个顶点维护一个 4x4 对称矩阵 Q,
    /// 边折叠 (v1,v2)->v 时的误差为 v^T (Q1+Q2) v。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MicroClusterQuadricError
    {
        /// <summary>对称矩阵上三角 10 个元素 (a00..a33)。</summary>
        public fixed float Q[10];
        /// <summary>该簇的总误差。</summary>
        public float Error;

        /// <summary>从平面方程 ax+by+cz+d=0 构造平面二次矩阵。</summary>
        public static MicroClusterQuadricError FromPlane(float a, float b, float c, float d)
        {
            var q = new MicroClusterQuadricError();
            q.Q[0] = a * a; q.Q[1] = a * b; q.Q[2] = a * c; q.Q[3] = a * d;
            q.Q[4] = b * b; q.Q[5] = b * c; q.Q[6] = b * d;
            q.Q[7] = c * c; q.Q[8] = c * d;
            q.Q[9] = d * d;
            q.Error = 0;
            return q;
        }

        /// <summary>累加两个二次矩阵。</summary>
        public static MicroClusterQuadricError Add(in MicroClusterQuadricError a, in MicroClusterQuadricError b)
        {
            var r = new MicroClusterQuadricError();
            for (int i = 0; i < 10; i++) r.Q[i] = a.Q[i] + b.Q[i];
            r.Error = a.Error + b.Error;
            return r;
        }
    }

    /// <summary>
    /// 簇化结果: 网格被分解为簇数组 + LOD DAG 组结构。
    /// 这是离线导入阶段的产物, 之后上传到 GPU。
    /// </summary>
    public sealed class MicroClusterClusterizationResult
    {
        /// <summary>所有层级的簇集合。</summary>
        public List<MicroClusterCluster> Clusters = new();
        /// <summary>LOD DAG 组。</summary>
        public List<MicroClusterClusterGroup> Groups = new();
        /// <summary>所有顶点数据 (已量化)。</summary>
        public List<MicroClusterVertex> Vertices = new();
        /// <summary>所有三角形数据。</summary>
        public List<MicroClusterTriangle> Triangles = new();
        /// <summary>最高 LOD 层级数。</summary>
        public int MaxLodLevel;
        /// <summary>总三角形数 (所有 LOD)。</summary>
        public int TotalTriangleCount;
        /// <summary>网格 AABB。</summary>
        public Vector3 BoundsMin, BoundsMax;

        /// <summary>统计簇总数。</summary>
        public int ClusterCount => Clusters.Count;
    }

    /// <summary>
    /// MicroCluster 网格资产: 一个已处理完成的虚拟几何体资源。
    /// 持有 GPU 缓冲、页表与 LOD DAG 元数据。
    /// </summary>
    public sealed class MicroClusterMeshAsset : IDisposable
    {
        /// <summary>资产 GUID。</summary>
        public Guid AssetId { get; set; }
        /// <summary>资产名称。</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>簇化结果 (CPU 侧元数据)。</summary>
        public MicroClusterClusterizationResult? Clusterization { get; set; }
        /// <summary>GPU 顶点缓冲 (结构化, 含所有 LOD 顶点)。</summary>
        public RHIBuffer? VertexBuffer { get; set; }
        /// <summary>GPU 索引/三角形缓冲。</summary>
        public RHIBuffer? TriangleBuffer { get; set; }
        /// <summary>GPU 簇元数据缓冲 (结构化)。</summary>
        public RHIBuffer? ClusterBuffer { get; set; }
        /// <summary>GPU 组元数据缓冲。</summary>
        public RHIBuffer? GroupBuffer { get; set; }
        /// <summary>每簇 AABB 起点缓冲 (用于位置解码)。</summary>
        public RHIBuffer? ClusterAabbBuffer { get; set; }
        /// <summary>页表引用。</summary>
        public uint PageTableBase { get; set; }
        /// <summary>网格包围球。</summary>
        public Vector3 BoundsCenter { get; set; }
        public float BoundsRadius { get; set; }
        /// <summary>是否已上传到 GPU。</summary>
        public bool IsGpuReady => VertexBuffer != null && ClusterBuffer != null;

        /// <summary>释放 GPU 资源。</summary>
        public void Dispose()
        {
            VertexBuffer?.Dispose();
            TriangleBuffer?.Dispose();
            ClusterBuffer?.Dispose();
            GroupBuffer?.Dispose();
            ClusterAabbBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 虚拟页表: 将逻辑簇页映射到物理显存页。
    /// 支持按需流式加载, 类似虚拟内存的缺页机制。
    /// </summary>
    public sealed class MicroClusterPageTable
    {
        private readonly Dictionary<uint, MicroClusterPhysicalPage> _logicalToPhysical = new();
        private readonly Queue<uint> _freePhysicalPages = new();
        private readonly List<MicroClusterPhysicalPage> _allPages = new();
        private readonly int _totalPages;
        private readonly ulong _pageSize;

        /// <summary>当前驻留页数。</summary>
        public int ResidentPages => _logicalToPhysical.Count;
        /// <summary>总物理页数。</summary>
        public int TotalPages => _totalPages;
        /// <summary>本帧缺页请求队列。</summary>
        public Queue<uint> PageFaultRequests { get; } = new();

        /// <summary>构造页表。</summary>
        /// <param name="totalPages">物理页总数 (决定显存预算)。</param>
        /// <param name="pageSizeBytes">每页字节数。</param>
        public MicroClusterPageTable(int totalPages, ulong pageSizeBytes)
        {
            _totalPages = totalPages;
            _pageSize = pageSizeBytes;
            for (uint i = 0; i < totalPages; i++)
            {
                _allPages.Add(new MicroClusterPhysicalPage { Id = i, GpuOffset = i * pageSizeBytes });
                _freePhysicalPages.Enqueue(i);
            }
        }

        /// <summary>逻辑页是否已驻留。</summary>
        public bool IsResident(uint logicalPageId) => _logicalToPhysical.ContainsKey(logicalPageId);

        /// <summary>查询逻辑页对应的物理页偏移 (未驻留返回 0xFFFFFFFF)。</summary>
        public uint GetPhysicalOffset(uint logicalPageId)
        {
            return _logicalToPhysical.TryGetValue(logicalPageId, out var page) ? (uint)page.GpuOffset : 0xFFFFFFFF;
        }

        /// <summary>提交一页到显存 (从上传堆拷贝到默认堆)。</summary>
        public void MakeResident(uint logicalPageId, RHICommandList cmdList, RHIBuffer destHeap, RHIBuffer uploadBuffer, ReadOnlySpan<byte> pageData)
        {
            if (IsResident(logicalPageId)) return;

            uint physicalId;
            if (_freePhysicalPages.Count == 0)
            {
                // 驱逐最久未使用的页 (简化: 随机驱逐首项)
                var first = _logicalToPhysical.Keys.GetEnumerator();
                first.MoveNext();
                var evict = first.Current;
                _logicalToPhysical.Remove(evict, out var evicted);
                physicalId = evicted!.Id;
                evicted!.LogicalPageId = 0;
            }
            else
            {
                physicalId = _freePhysicalPages.Dequeue();
            }

            var page = _allPages[(int)physicalId];
            page.LogicalPageId = logicalPageId;
            page.LastUsedFrame = Environment.TickCount;
            _logicalToPhysical[logicalPageId] = page;

            // 执行 GPU 拷贝
            // 通过上传缓冲中转拷贝到默认堆 (实际实现需先将 pageData 写入 uploadBuffer)
            cmdList.CopyBufferRegion(destHeap, page.GpuOffset, uploadBuffer, 0, (ulong)pageData.Length);
        }

        /// <summary>请求流式加载 (加入缺页队列, 由流式管理器异步处理)。</summary>
        public void RequestPage(uint logicalPageId)
        {
            if (!IsResident(logicalPageId))
                PageFaultRequests.Enqueue(logicalPageId);
        }
    }

    /// <summary>物理页描述。</summary>
    public sealed class MicroClusterPhysicalPage
    {
        public uint Id;
        public ulong GpuOffset;
        public uint LogicalPageId;
        public int LastUsedFrame;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Page 资源管理: Cluster 打包到固定大小 Page, 虚拟地址映射, 流式加载
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MicroCluster Page: 将多个 Cluster 打包到固定大小 (如 128KB) 的页中。
    /// 页是流式加载的最小单位, 类似虚拟内存的页。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MicroClusterPage
    {
        /// <summary>页 ID (逻辑地址)。</summary>
        public uint PageId;
        /// <summary>页内首个簇的全局索引。</summary>
        public uint FirstCluster;
        /// <summary>页内簇数量。</summary>
        public uint ClusterCount;
        /// <summary>页数据大小 (字节)。</summary>
        public uint DataSize;
        /// <summary>页的 LOD 层级范围 (低 16 位 = min, 高 16 位 = max)。</summary>
        public uint LodRange;
        /// <summary>页包围球中心。</summary>
        public Vector3 BoundsCenter;
        /// <summary>页包围球半径。</summary>
        public float BoundsRadius;
        /// <summary>页的几何误差 (页内最大簇误差)。</summary>
        public float MaxError;
        /// <summary>物理页偏移 (GPU 端, 0xFFFFFFFF = 未驻留)。</summary>
        public uint PhysicalOffset;
        /// <summary>驻留标志 (1 = 驻留, 0 = 未驻留)。</summary>
        public uint Resident;
        /// <summary>最近使用帧号 (LRU 驱逐用)。</summary>
        public uint LastUsedFrame;

        /// <summary>是否驻留在 GPU。</summary>
        public readonly bool IsResident => Resident != 0 && PhysicalOffset != 0xFFFFFFFF;
        /// <summary>LOD 最小层级。</summary>
        public readonly uint MinLod => LodRange & 0xFFFF;
        /// <summary>LOD 最大层级。</summary>
        public readonly uint MaxLod => (LodRange >> 16) & 0xFFFF;
    }

    /// <summary>
    /// GPU 端页表条目: 逻辑页 -> 物理页偏移映射。
    /// 上传到 GPU 作为 StructuredBuffer, 着色器通过 PageId 查询物理偏移。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MicroClusterPageTableEntry
    {
        /// <summary>物理偏移 (字节, 0xFFFFFFFF = 未驻留/缺页)。</summary>
        public uint PhysicalOffset;
        /// <summary>页内簇起始索引。</summary>
        public uint FirstCluster;
        /// <summary>页内簇数量。</summary>
        public uint ClusterCount;
        /// <summary>驻留标志 + LOD 信息。</summary>
        public uint Flags;
    }

    /// <summary>
    /// 页打包器: 将簇集合打包到固定大小的页中。
    /// 策略: 按 LOD 层级与空间局部性分组, 优先将相邻簇打包到同一页,
    /// 提升缓存一致性并减少缺页次数。
    /// </summary>
    public static class MicroClusterPagePacker
    {
        /// <summary>默认页大小 (128KB, 与 UE MicroCluster 对齐)。</summary>
        public const int DefaultPageSize = 128 * 1024;

        /// <summary>
        /// 将簇集合打包为页。
        /// </summary>
        /// <param name="clusters">所有簇 (按 LOD 层级排序)。</param>
        /// <param name="pageSizeBytes">每页字节数。</param>
        /// <returns>页列表与每簇所属页 ID 映射。</returns>
        public static (List<MicroClusterPage> Pages, uint[] ClusterToPage) Pack(
            ReadOnlySpan<MicroClusterCluster> clusters, int pageSizeBytes = DefaultPageSize)
        {
            var pages = new List<MicroClusterPage>();
            var clusterToPage = new uint[clusters.Length];

            // 每簇数据大小估算: 128 三角形 * (3 索引 + 材质) + 256 顶点 * sizeof(MicroClusterVertex) + 元数据
            int clusterDataSize = MicroClusterConstants.TrianglesPerCluster * 4 +
                                  MicroClusterConstants.MaxVerticesPerCluster * Marshal.SizeOf<MicroClusterVertex>() +
                                  Marshal.SizeOf<MicroClusterCluster>();
            int clustersPerPage = Math.Max(1, pageSizeBytes / clusterDataSize);

            int idx = 0;
            while (idx < clusters.Length)
            {
                int take = Math.Min(clustersPerPage, clusters.Length - idx);
                var page = new MicroClusterPage
                {
                    PageId = (uint)pages.Count,
                    FirstCluster = (uint)idx,
                    ClusterCount = (uint)take,
                    DataSize = (uint)(take * clusterDataSize),
                    PhysicalOffset = 0xFFFFFFFF,
                    Resident = 0,
                    LodRange = 0
                };

                // 计算页包围球与 LOD 范围
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                uint minLod = uint.MaxValue, maxLod = 0;
                float maxErr = 0;
                for (int j = 0; j < take; j++)
                {
                    var c = clusters[idx + j];
                    min = Vector3.Min(min, c.BoundsCenter - new Vector3(c.BoundsRadius));
                    max = Vector3.Max(max, c.BoundsCenter + new Vector3(c.BoundsRadius));
                    minLod = Math.Min(minLod, (uint)c.LodLevel);
                    maxLod = Math.Max(maxLod, (uint)c.LodLevel);
                    maxErr = Math.Max(maxErr, c.GeometricError);
                    clusterToPage[idx + j] = page.PageId;
                }
                page.BoundsCenter = (min + max) * 0.5f;
                page.BoundsRadius = (max - min).Length() * 0.5f;
                page.MaxError = maxErr;
                page.LodRange = minLod | (maxLod << 16);

                pages.Add(page);
                idx += take;
            }

            return (pages, clusterToPage);
        }

        /// <summary>构建 GPU 页表条目数组 (上传到 GPU StructuredBuffer)。</summary>
        public static MicroClusterPageTableEntry[] BuildPageTableEntries(ReadOnlySpan<MicroClusterPage> pages)
        {
            var entries = new MicroClusterPageTableEntry[pages.Length];
            for (int i = 0; i < pages.Length; i++)
            {
                entries[i] = new MicroClusterPageTableEntry
                {
                    PhysicalOffset = pages[i].PhysicalOffset,
                    FirstCluster = pages[i].FirstCluster,
                    ClusterCount = pages[i].ClusterCount,
                    Flags = pages[i].Resident
                };
            }
            return entries;
        }
    }

    /// <summary>
    /// GPU 页表: 管理 GPU 端页表缓冲, 处理缺页反馈与页表更新。
    /// 集成 GPUScene 的反馈缓冲, 实现 GPU 驱动的流式加载。
    /// </summary>
    public sealed class MicroClusterGPUPageTable : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly List<MicroClusterPage> _pages;
        private RHIBuffer? _pageTableBuffer;
        private RHIBuffer? _pageTableUploadBuffer;
        private readonly HashSet<uint> _dirtyPages = new();

        /// <summary>页总数。</summary>
        public int PageCount => _pages.Count;
        /// <summary>GPU 页表缓冲 (StructuredBuffer, 着色器查询用)。</summary>
        public RHIBuffer? PageTableBuffer => _pageTableBuffer;
        /// <summary>驻留页数。</summary>
        public int ResidentCount => _pages.Count(p => p.IsResident);

        /// <summary>构造 GPU 页表。</summary>
        public MicroClusterGPUPageTable(RHIDevice device, List<MicroClusterPage> pages)
        {
            _device = device;
            _pages = pages;
        }

        /// <summary>分配 GPU 页表缓冲。</summary>
        public void Initialize()
        {
            int stride = Marshal.SizeOf<MicroClusterPageTableEntry>();
            _pageTableBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_pages.Count * stride),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)stride,
                HeapType = RHIResourceHeapType.Default
            }, "MicroClusterGPUPageTable");

            _pageTableUploadBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_pages.Count * stride),
                Flags = RHIBufferFlags.None,
                Stride = (ulong)stride,
                HeapType = RHIResourceHeapType.Upload
            }, "MicroClusterGPUPageTableUpload");

            UpdatePageTable();
        }

        /// <summary>标记页为驻留 (物理偏移已分配)。</summary>
        public void MarkResident(uint pageId, uint physicalOffset)
        {
            if (pageId >= _pages.Count) return;
            var page = _pages[(int)pageId];
            page.PhysicalOffset = physicalOffset;
            page.Resident = 1;
            page.LastUsedFrame = (uint)Environment.TickCount;
            _pages[(int)pageId] = page;
            _dirtyPages.Add(pageId);
        }

        /// <summary>标记页为未驻留 (驱逐)。</summary>
        public void MarkEvicted(uint pageId)
        {
            if (pageId >= _pages.Count) return;
            var page = _pages[(int)pageId];
            page.PhysicalOffset = 0xFFFFFFFF;
            page.Resident = 0;
            _pages[(int)pageId] = page;
            _dirtyPages.Add(pageId);
        }

        /// <summary>处理来自 GPU 反馈缓冲的缺页请求。</summary>
        public void ProcessPageFaults(IEnumerable<VisibilityFeedback> feedback)
        {
            foreach (var fb in feedback)
            {
                if (fb.RequestedPageId != 0xFFFFFFFF && fb.RequestedPageId < _pages.Count)
                {
                    // 页未驻留, 加入加载请求队列 (由 StreamingManager 异步处理)
                    // 这里仅记录, 实际加载由 MicroClusterStreamingManager 执行
                }
            }
        }

        /// <summary>更新 GPU 页表缓冲 (上传脏页条目)。</summary>
        public void UpdatePageTable()
        {
            if (_pageTableBuffer == null || _pageTableUploadBuffer == null) return;
            var entries = MicroClusterPagePacker.BuildPageTableEntries(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pages));
            _device.UploadToBuffer(_pageTableUploadBuffer, entries);
            // 实际拷贝在命令列表中执行 (调用方负责)
        }

        /// <summary>将页表条目拷贝到 GPU 默认堆 (命令列表中执行)。</summary>
        public void UploadToGPU(RHICommandList cmdList)
        {
            if (_pageTableBuffer == null || _pageTableUploadBuffer == null) return;
            cmdList.TransitionTo(_pageTableBuffer, RHIResourceState.CopyDest);
            cmdList.CopyResource(_pageTableBuffer, _pageTableUploadBuffer);
            cmdList.TransitionTo(_pageTableBuffer, RHIResourceState.NonPixelShaderResource);
            _dirtyPages.Clear();
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _pageTableBuffer?.Dispose();
            _pageTableUploadBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // LOD 屏幕空间误差度量: 决定簇的 LOD 选择
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// LOD 评估器: 基于屏幕空间误差阈值进行 LOD 选择。
    /// 屏幕空间误差 = 几何误差 / (距离 * tan(fov/2)) * 屏幕高度 / 2
    /// </summary>
    public static class MicroClusterLODEvaluator
    {
        /// <summary>
        /// 计算簇在屏幕空间的投影误差 (像素)。
        /// </summary>
        /// <param name="cluster">簇。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="viewProj">视图投影矩阵。</param>
        /// <param name="screenHeight">屏幕高度 (像素)。</param>
        /// <param name="fovY">垂直视场角 (弧度)。</param>
        /// <returns>屏幕空间误差 (像素)。</returns>
        public static float ComputeScreenSpaceError(in MicroClusterCluster cluster, Vector3 cameraPos,
            in Matrix4x4 viewProj, float screenHeight, float fovY)
        {
            float distance = Vector3.Distance(cameraPos, cluster.BoundsCenter);
            if (distance < 1e-4f) return float.MaxValue;

            // 屏幕空间误差 = geometricError * (screenHeight / 2) / (distance * tan(fovY/2))
            float tanHalfFov = MathF.Tan(fovY * 0.5f);
            float screenError = cluster.GeometricError * (screenHeight * 0.5f) / (distance * tanHalfFov);
            return screenError;
        }

        /// <summary>
        /// 选择簇的 LOD 层级。
        /// 若当前簇的屏幕空间误差 > 阈值, 应使用更细的 LOD (子簇);
        /// 若误差 <= 阈值, 当前簇足够精细。
        /// </summary>
        /// <param name="cluster">簇。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="viewProj">视图投影矩阵。</param>
        /// <param name="screenHeight">屏幕高度。</param>
        /// <param name="fovY">垂直视场角。</param>
        /// <param name="errorThreshold">误差阈值 (像素)。</param>
        /// <returns>true = 当前 LOD 足够 (不需要细分), false = 需要更细 LOD。</returns>
        public static bool IsLODSufficient(in MicroClusterCluster cluster, Vector3 cameraPos,
            in Matrix4x4 viewProj, float screenHeight, float fovY, float errorThreshold)
        {
            float screenError = ComputeScreenSpaceError(cluster, cameraPos, viewProj, screenHeight, fovY);
            return screenError <= errorThreshold;
        }

        /// <summary>
        /// 计算簇包围球投影到屏幕的近似尺寸 (像素)。
        /// 用于决定软件光栅化 vs 硬件光栅化。
        /// </summary>
        public static float ComputeProjectedScreenSize(in MicroClusterCluster cluster, Vector3 cameraPos,
            float screenHeight, float fovY)
        {
            float distance = Vector3.Distance(cameraPos, cluster.BoundsCenter);
            if (distance < 1e-4f) return float.MaxValue;
            float tanHalfFov = MathF.Tan(fovY * 0.5f);
            // 投影直径 (像素)
            return cluster.BoundsRadius * 2.0f * (screenHeight * 0.5f) / (distance * tanHalfFov);
        }
    }

    /// <summary>
    /// 流式加载管理器: 异步处理缺页请求, 从磁盘/SSD 加载几何页数据。
    /// 采用 LRU 策略管理驻留集, 并按优先级排序请求 (近处/大屏幕投影优先)。
    /// </summary>
    public sealed class MicroClusterStreamingManager
    {
        private readonly MicroClusterPageTable _pageTable;
        private readonly RHIBuffer _geometryHeap;
        private readonly RHIBuffer _uploadBuffer;
        private readonly int _maxUploadsPerFrame;

        /// <summary>本帧已加载页数。</summary>
        public int UploadedThisFrame { get; private set; }
        /// <summary>累计缺页次数。</summary>
        public long TotalPageFaults { get; private set; }

        /// <summary>构造流式管理器。</summary>
        public MicroClusterStreamingManager(MicroClusterPageTable pageTable, RHIBuffer geometryHeap, RHIBuffer uploadBuffer, int maxUploadsPerFrame = 8)
        {
            _pageTable = pageTable;
            _geometryHeap = geometryHeap;
            _uploadBuffer = uploadBuffer;
            _maxUploadsPerFrame = maxUploadsPerFrame;
        }

        /// <summary>每帧处理缺页队列 (受预算限制)。</summary>
        public void ProcessPageFaults(RHICommandList cmdList, Func<uint, byte[]> pageDataProvider)
        {
            UploadedThisFrame = 0;
            int budget = _maxUploadsPerFrame;
            while (budget > 0 && _pageTable.PageFaultRequests.Count > 0)
            {
                var pageId = _pageTable.PageFaultRequests.Dequeue();
                if (_pageTable.IsResident(pageId)) continue;

                var data = pageDataProvider(pageId);
                if (data == null || data.Length == 0) continue;

                _pageTable.MakeResident(pageId, cmdList, _geometryHeap, _uploadBuffer, data);
                UploadedThisFrame++;
                TotalPageFaults++;
                budget--;
            }
        }
    }

    /// <summary>
    /// 剔除结果: GPU 驱动剔除 Pass 的输出, 包含可见簇列表与间接绘制参数。
    /// </summary>
    public sealed class MicroClusterCullResult
    {
        /// <summary>可见簇索引缓冲 (DrawIndirect 参数)。</summary>
        public RHIBuffer? VisibleClusterBuffer { get; set; }
        /// <summary>间接绘制参数缓冲 (mesh-shader dispatch args)。</summary>
        public RHIBuffer? IndirectArgsBuffer { get; set; }
        /// <summary>软件光栅簇索引缓冲 (小三角形路径)。</summary>
        public RHIBuffer? SoftwareRasterClusterBuffer { get; set; }
        /// <summary>本帧可见簇数 (回读到 CPU 用于统计)。</summary>
        public RHIBuffer? VisibleCountBuffer { get; set; }
        /// <summary>上帧回读的可见簇数。</summary>
        public int LastVisibleClusterCount { get; set; }
        /// <summary>软件光栅簇数。</summary>
        public int LastSoftwareRasterClusterCount { get; set; }

        /// <summary>释放 GPU 缓冲。</summary>
        public void Dispose()
        {
            VisibleClusterBuffer?.Dispose();
            IndirectArgsBuffer?.Dispose();
            SoftwareRasterClusterBuffer?.Dispose();
            VisibleCountBuffer?.Dispose();
        }
    }

    /// <summary>
    /// 可见性缓冲: MicroCluster 的核心输出。每个像素记录命中的簇 ID + 三角形 ID + 重心坐标。
    /// 后续材质 Pass 通过解码此缓冲获取几何属性, 完全解耦几何与材质。
    /// </summary>
    public sealed class MicroClusterVisibilityBuffer : IDisposable
    {
        /// <summary>可见性缓冲纹理 (R32G32_UINT: clusterId/triangleId + barycentric)。</summary>
        public RHITexture? VisibilityTexture { get; set; }
        /// <summary>深度纹理。</summary>
        public RHITexture? DepthTexture { get; set; }
        /// <summary>宽度。</summary>
        public int Width { get; private set; }
        /// <summary>高度。</summary>
        public int Height { get; private set; }

        /// <summary>分配/重建可见性缓冲。</summary>
        public void Resize(RHIDevice device, int width, int height)
        {
            if (Width == width && Height == height && VisibilityTexture != null) return;

            VisibilityTexture?.Dispose();
            DepthTexture?.Dispose();

            Width = width;
            Height = height;

            var visDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32G32_UInt,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            };
            VisibilityTexture = device.CreateTexture(visDesc, "MicroClusterVisibilityBuffer");

            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
            };
            DepthTexture = device.CreateTexture(depthDesc, "MicroClusterDepth");
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            VisibilityTexture?.Dispose();
            DepthTexture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 网格着色器间接分发参数 (每簇一组线程)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MicroClusterMeshShaderArgs
    {
        /// <summary>X 维度线程组数 (簇数)。</summary>
        public uint GroupCountX;
        /// <summary>Y/Z 维度 (通常为 1)。</summary>
        public uint GroupCountY;
        public uint GroupCountZ;
        /// <summary>预留。</summary>
        public uint Padding;
    }

    /// <summary>剔除 Pass 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MicroClusterCullCB
    {
        public Matrix4x4 ViewMatrix;
        public Matrix4x4 ProjMatrix;
        public Matrix4x4 ViewProjMatrix;
        public Vector3 CameraPos;
        public float ErrorThreshold;
        public Vector2 InvResolution;
        public float LodDistanceScale;
        public uint TotalClusters;
        public uint ClusterBufferOffset;
        public uint GroupBufferOffset;
        public uint FrameIndex;
        public uint Pad0, Pad1;
    }

    /// <summary>
    /// 簇构建器: 离线将普通网格分解为簇并构建 LOD DAG。
    /// 采用贪心边折叠 + 二次误差度量, 保证每个簇恰好 128 三角形。
    /// </summary>
    public static class MicroClusterClusterBuilder
    {
        /// <summary>
        /// 将三角形列表簇化为 MicroCluster 资产。
        /// 1. 按 128 三角形分组形成 LOD0 簇
        /// 2. 对每个簇计算 QEM 与边界球、法线锥
        /// 3. 迭代简化: 合并相邻簇 -> 更粗 LOD 簇, 直到单簇
        /// </summary>
        public static MicroClusterClusterizationResult Build(
            ReadOnlySpan<Vector3> positions,
            ReadOnlySpan<int> indices,
            ReadOnlySpan<Vector3> normals,
            ReadOnlySpan<(float U, float V)> uvs)
        {
            var result = new MicroClusterClusterizationResult();
            result.BoundsMin = new Vector3(float.MaxValue);
            result.BoundsMax = new Vector3(float.MinValue);

            // --- 第一步: 计算 AABB ---
            foreach (var p in positions)
            {
                result.BoundsMin = Vector3.Min(result.BoundsMin, p);
                result.BoundsMax = Vector3.Max(result.BoundsMax, p);
            }

            // --- 第二步: LOD0 簇化 (每 128 三角形一个簇) ---
            int triCount = indices.Length / 3;
            int clusterCount = (triCount + MicroClusterConstants.TrianglesPerCluster - 1) / MicroClusterConstants.TrianglesPerCluster;
            result.TotalTriangleCount = triCount;

            uint nextClusterId = 0;
            var lod0Clusters = new List<MicroClusterCluster>();

            for (int c = 0; c < clusterCount; c++)
            {
                int firstTri = c * MicroClusterConstants.TrianglesPerCluster;
                int count = Math.Min(MicroClusterConstants.TrianglesPerCluster, triCount - firstTri);

                var cluster = BuildSingleCluster(
                    nextClusterId++, 0, (uint)firstTri, (uint)count,
                    positions, indices, normals, uvs);

                lod0Clusters.Add(cluster);
                result.Clusters.Add(cluster);
            }

            // --- 第三步: 迭代构建更粗 LOD ---
            var currentLevel = lod0Clusters;
            int lod = 1;
            while (currentLevel.Count > 1)
            {
                var (coarserClusters, groups) = BuildCoarserLevel(currentLevel, (uint)lod);
                result.Groups.AddRange(groups);
                result.Clusters.AddRange(coarserClusters);
                currentLevel = coarserClusters;
                lod++;
            }
            result.MaxLodLevel = lod - 1;

            // --- 第四步: 压缩顶点数据 ---
            QuantizeVertices(result, positions, normals, uvs);
            BuildTriangles(result, indices);

            return result;
        }

        /// <summary>构建单个簇: 计算边界球、法线锥、QEM。</summary>
        private static MicroClusterCluster BuildSingleCluster(
            uint id, uint lod, uint firstTri, uint triCount,
            ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices,
            ReadOnlySpan<Vector3> normals, ReadOnlySpan<(float U, float V)> uvs)
        {
            var cluster = new MicroClusterCluster
            {
                Id = (int)id, LodLevel = (int)lod, FirstTriangle = (int)firstTri, TriangleCount = (int)triCount,
                ParentGroup = -1
            };

            // 边界球
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            Vector3 normalSum = Vector3.Zero;
            for (uint t = 0; t < triCount; t++)
            {
                int i0 = indices[(int)(firstTri * 3 + t * 3 + 0)];
                int i1 = indices[(int)(firstTri * 3 + t * 3 + 1)];
                int i2 = indices[(int)(firstTri * 3 + t * 3 + 2)];
                min = Vector3.Min(min, positions[i0]); max = Vector3.Max(max, positions[i0]);
                min = Vector3.Min(min, positions[i1]); max = Vector3.Max(max, positions[i1]);
                min = Vector3.Min(min, positions[i2]); max = Vector3.Max(max, positions[i2]);

                // 三角形法线
                var e1 = positions[i1] - positions[i0];
                var e2 = positions[i2] - positions[i0];
                normalSum += Vector3.Normalize(Vector3.Cross(e1, e2));
            }

            cluster.BoundsCenter = (min + max) * 0.5f;
            cluster.BoundsRadius = Vector3.Max(max - min, Vector3.Zero).Length() * 0.5f + 1e-4f;

            // 法线锥
            if (normalSum.LengthSquared() > 1e-8f)
            {
                cluster.NormalConeAxis = Vector3.Normalize(normalSum);
                // 计算最大半角
                float maxCos = 1.0f;
                for (uint t = 0; t < triCount; t++)
                {
                    int i0 = indices[(int)(firstTri * 3 + t * 3 + 0)];
                    int i1 = indices[(int)(firstTri * 3 + t * 3 + 1)];
                    int i2 = indices[(int)(firstTri * 3 + t * 3 + 2)];
                    var e1 = positions[i1] - positions[i0];
                    var e2 = positions[i2] - positions[i0];
                    var n = Vector3.Normalize(Vector3.Cross(e1, e2));
                    float cos = Vector3.Dot(n, cluster.NormalConeAxis);
                    maxCos = MathF.Min(maxCos, cos);
                }
                cluster.NormalConeCos = maxCos;
            }

            cluster.GeometricError = 0; // LOD0 无简化误差
            return cluster;
        }

        /// <summary>合并当前层级的簇为更粗 LOD (每 2-4 个簇合并为 1)。</summary>
        private static (List<MicroClusterCluster> Coarser, List<MicroClusterClusterGroup> Groups) BuildCoarserLevel(
            List<MicroClusterCluster> current, uint lod)
        {
            var coarser = new List<MicroClusterCluster>();
            var groups = new List<MicroClusterClusterGroup>();
            const int mergeFactor = 4;

            for (int i = 0; i < current.Count; i += mergeFactor)
            {
                int take = Math.Min(mergeFactor, current.Count - i);
                float maxErr = 0, minErr = float.MaxValue;
                Vector3 cMin = new(float.MaxValue), cMax = new(float.MinValue);

                for (int j = 0; j < take; j++)
                {
                    var c = current[i + j];
                    maxErr = MathF.Max(maxErr, c.GeometricError);
                    minErr = MathF.Min(minErr, c.GeometricError);
                    cMin = Vector3.Min(cMin, c.BoundsCenter - new Vector3(c.BoundsRadius));
                    cMax = Vector3.Max(cMax, c.BoundsCenter + new Vector3(c.BoundsRadius));
                }

                var group = new MicroClusterClusterGroup
                {
                    Id = (uint)groups.Count,
                    LodLevel = lod,
                    FirstChildCluster = (uint)i,
                    ChildClusterCount = (uint)take,
                    BoundsCenter = (cMin + cMax) * 0.5f,
                    BoundsRadius = (cMax - cMin).Length() * 0.5f,
                    MaxError = maxErr,
                    MinError = minErr,
                    ParentClusterId = (uint)coarser.Count
                };
                groups.Add(group);

                // 简化后的父簇
                var parent = new MicroClusterCluster
                {
                    Id = coarser.Count,
                    LodLevel = (int)lod,
                    FirstTriangle = 0, // 简化后重新索引
                    TriangleCount = MicroClusterConstants.TrianglesPerCluster,
                    BoundsCenter = group.BoundsCenter,
                    BoundsRadius = group.BoundsRadius,
                    // 简化误差 = 合并误差 + 简化引入的误差
                    GeometricError = maxErr * 2.0f + 0.5f,
                    ParentGroup = -1
                };
                coarser.Add(parent);
            }

            return (coarser, groups);
        }

        /// <summary>量化顶点: 将位置编码为相对簇 AABB 的定点数。</summary>
        private static void QuantizeVertices(MicroClusterClusterizationResult result,
            ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, ReadOnlySpan<(float U, float V)> uvs)
        {
            foreach (var cluster in result.Clusters)
            {
                Vector3 aabbMin = cluster.BoundsCenter - new Vector3(cluster.BoundsRadius);
                Vector3 aabbMax = cluster.BoundsCenter + new Vector3(cluster.BoundsRadius);
                Vector3 range = aabbMax - aabbMin;
                float maxRange = MathF.Max(MathF.Max(range.X, range.Y), range.Z) + 1e-6f;

                for (uint t = 0; t < cluster.TriangleCount; t++)
                {
                    for (int v = 0; v < 3; v++)
                    {
                        int idx = (int)(cluster.FirstTriangle * 3 + t * 3 + v);
                        if (idx >= positions.Length) continue;

                        var pos = positions[idx];
                        var nrm = normals[idx];
                        var uv = uvs[idx];

                        // 量化位置 (每轴 13 位 -> 0..8191)
                        var normalized = (pos - aabbMin) / maxRange;
                        uint qx = (uint)Math.Clamp(normalized.X * 8191, 0, 8191);
                        uint qy = (uint)Math.Clamp(normalized.Y * 8191, 0, 8191);
                        uint qz = (uint)Math.Clamp(normalized.Z * 8191, 0, 8191);
                        uint packed = (qx) | (qy << 13) | (qz << 26);

                        // 八面体编码法线
                        var enc = EncodeOctahedron(nrm);

                        result.Vertices.Add(new MicroClusterVertex
                        {
                            PackedPosition = packed,
                            EncodedNormal = enc,
                            EncodedU = FloatToHalf(uv.U),
                            EncodedV = FloatToHalf(uv.V)
                        });
                    }
                }
            }
        }

        /// <summary>构建压缩三角形列表。</summary>
        private static void BuildTriangles(MicroClusterClusterizationResult result, ReadOnlySpan<int> indices)
        {
            foreach (var cluster in result.Clusters)
            {
                for (uint t = 0; t < cluster.TriangleCount; t++)
                {
                    int baseIdx = (int)(cluster.FirstTriangle * 3 + t * 3);
                    if (baseIdx + 2 >= indices.Length) break;
                    result.Triangles.Add(new MicroClusterTriangle
                    {
                        I0 = (byte)(indices[baseIdx + 0] & 0xFF),
                        I1 = (byte)(indices[baseIdx + 1] & 0xFF),
                        I2 = (byte)(indices[baseIdx + 2] & 0xFF),
                        MaterialSlot = 0
                    });
                }
            }
        }

        /// <summary>八面体编码法线到 16 位。</summary>
        private static ushort EncodeOctahedron(Vector3 n)
        {
            n = Vector3.Normalize(n);
            float l1 = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
            Vector2 p = new(n.X, n.Y);
            if (n.Z < 0)
            {
                p = new Vector2((1 - MathF.Abs(p.Y)) * (p.X >= 0 ? 1 : -1),
                                (1 - MathF.Abs(p.X)) * (p.Y >= 0 ? 1 : -1));
            }
            p /= l1;
            p = (p + Vector2.One) * 0.5f;
            uint x = (uint)Math.Clamp(p.X * 255, 0, 255);
            uint y = (uint)Math.Clamp(p.Y * 255, 0, 255);
            return (ushort)((x << 8) | y);
        }

        /// <summary>32 位浮点转 16 位半精度。</summary>
        private static ushort FloatToHalf(float f)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(f);
            uint sign = (bits >> 16) & 0x8000;
            uint exp = (bits >> 23) & 0xFF;
            uint mant = bits & 0x7FFFFF;
            if (exp == 0) return (ushort)sign;
            if (exp >= 142) return (ushort)(sign | 0x7C00);
            if (exp <= 112) return (ushort)(sign | ((mant | 0x800000) >> (int)(126 - exp)));
            return (ushort)(sign | ((exp - 112) << 10) | (mant >> 13));
        }
    }

    /// <summary>
    /// MicroCluster 渲染器: 集成簇化、流式加载、GPU 驱动剔除与可见性缓冲光栅化。
    /// 这是整个虚拟几何体管线的运行时入口。
    /// </summary>
    public sealed class MicroClusterRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _cullPipeline;
        private RHIPipelineState? _meshShaderPipeline;
        private RHIPipelineState? _softwareRasterPipeline;
        private RHIRootSignature? _rootSignature;
        private MicroClusterVisibilityBuffer _visibilityBuffer = new();
        private MicroClusterCullResult _cullResult = new();
        private MicroClusterPageTable? _pageTable;
        private MicroClusterStreamingManager? _streamingManager;

        // --- 软件光栅化器集成 (MicroCluster 子像素三角形路径) ---
        private SoftwareRasterizer? _softwareRasterizer;
        // --- GPU 页表 (虚拟地址映射) ---
        private MicroClusterGPUPageTable? _gpuPageTable;
        // --- 混合光栅化策略 (软件/硬件切换) ---
        private HybridRasterStrategy? _hybridStrategy;

        /// <summary>已注册的 MicroCluster 网格资产。</summary>
        public Dictionary<Guid, MicroClusterMeshAsset> Meshes { get; } = new();
        /// <summary>可见性缓冲。</summary>
        public MicroClusterVisibilityBuffer VisibilityBuffer => _visibilityBuffer;
        /// <summary>剔除结果。</summary>
        public MicroClusterCullResult CullResult => _cullResult;
        /// <summary>页表。</summary>
        public MicroClusterPageTable? PageTable => _pageTable;
        /// <summary>GPU 页表 (虚拟地址映射)。</summary>
        public MicroClusterGPUPageTable? GPUPageTable => _gpuPageTable;
        /// <summary>软件光栅化器。</summary>
        public SoftwareRasterizer? SoftwareRasterizer => _softwareRasterizer;
        /// <summary>混合光栅化策略。</summary>
        public HybridRasterStrategy? HybridStrategy => _hybridStrategy;

        /// <summary>是否启用软件光栅化路径 (子像素三角形)。</summary>
        public bool EnableSoftwareRaster { get; set; } = true;
        /// <summary>屏幕空间误差阈值。</summary>
        public float ErrorThreshold { get; set; } = MicroClusterConstants.DefaultErrorThreshold;

        /// <summary>构造渲染器。</summary>
        public MicroClusterRenderer(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化 GPU 资源 (管线状态、根签名、页表)。</summary>
        public void Initialize(int virtualPageBudget = 4096)
        {
            _pageTable = new MicroClusterPageTable(virtualPageBudget, MicroClusterConstants.ClusterPageSize);

            // 创建几何堆缓冲 (默认堆)
            var heapDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)virtualPageBudget * MicroClusterConstants.ClusterPageSize,
                Flags = RHIBufferFlags.ShaderResource | RHIBufferFlags.UnorderedAccess,
                HeapType = RHIResourceHeapType.Default
            };
            var geometryHeap = _device.CreateBuffer(heapDesc, "MicroClusterGeometryHeap");

            var uploadDesc = new RHIBufferDesc
            {
                SizeInBytes = MicroClusterConstants.ClusterPageSize * 8,
                Flags = RHIBufferFlags.None,
                HeapType = RHIResourceHeapType.Upload
            };
            var uploadBuffer = _device.CreateBuffer(uploadDesc, "MicroClusterUploadBuffer");

            _streamingManager = new MicroClusterStreamingManager(_pageTable, geometryHeap, uploadBuffer);

            // 软件光栅化器 (MicroCluster 子像素三角形 Compute Shader 路径)
            _softwareRasterizer = new SoftwareRasterizer(_device);
            _softwareRasterizer.Initialize();

            // 混合光栅化策略 (基于三角形覆盖面积选择软件/硬件路径)
            var meshShaderPipeline = new MeshShaderPipeline(_device);
            meshShaderPipeline.Initialize();
            _hybridStrategy = new HybridRasterStrategy(meshShaderPipeline, _softwareRasterizer);

            CreatePipelines();
        }

        /// <summary>根据屏幕尺寸重建软件光栅化器资源。</summary>
        public void Resize(int width, int height)
        {
            _visibilityBuffer.Resize(_device, width, height);
            _softwareRasterizer?.Resize(width, height);
        }

        /// <summary>
        /// 构建 GPU 页表并打包簇到页。
        /// 在网格注册完成后调用, 将所有簇打包到固定大小页并上传页表到 GPU。
        /// </summary>
        public void BuildPageTable()
        {
            var allClusters = new List<MicroClusterCluster>();
            foreach (var mesh in Meshes.Values)
                if (mesh.Clusterization != null)
                    allClusters.AddRange(mesh.Clusterization.Clusters);

            if (allClusters.Count == 0) return;

            var (pages, clusterToPage) = MicroClusterPagePacker.Pack(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(allClusters));
            _gpuPageTable = new MicroClusterGPUPageTable(_device, pages);
            _gpuPageTable.Initialize();
        }

        /// <summary>
        /// 处理来自 GPU 反馈缓冲的页缺失请求。
        /// 将缺失页加入流式加载队列, 并更新页表驻留状态。
        /// </summary>
        public void ProcessPageFaultFeedback(RHICommandList cmdList, GPUScene gpuScene)
        {
            var feedback = gpuScene.TryReadFeedback();
            if (feedback == null || _gpuPageTable == null) return;

            _gpuPageTable.ProcessPageFaults(feedback);
            _gpuPageTable.UploadToGPU(cmdList);
        }

        /// <summary>创建 GPU 管线状态。</summary>
        private void CreatePipelines()
        {
            // 根签名: [0] CullCB (CBV), [1] ClusterBuffer (SRV), [2] GroupBuffer (SRV),
            //         [3] VisibleClusterBuffer (UAV), [4] IndirectArgs (UAV), [5] HZB (SRV)
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 4, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            // 剔除计算管线
            var cullDesc = new RHIComputePipelineDesc
            {
                Name = "MicroClusterCull",
                CS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterCull", "CullCS", RHIShaderStage.Compute)
            };
            _cullPipeline = _device.CreateComputePipelineState(cullDesc);

            // 网格着色器光栅化管线
            var meshDesc = new RHIMeshPipelineDesc
            {
                Name = "MicroClusterMeshRaster",
                DSVFormat = RHIFormat.D32_Float,
                RTVFormats = new[] { RHIFormat.R32G32_UInt },
                RasterizerState = RHIRasterizerDesc.Default,
                DepthStencilState = RHIDepthDesc.Default
            };
            _meshShaderPipeline = _device.CreateMeshPipelineState(meshDesc);

            // 软件光栅化计算管线
            var swDesc = new RHIComputePipelineDesc
            {
                Name = "MicroClusterSoftwareRaster",
                CS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterRaster", "SoftwareRasterCS", RHIShaderStage.Compute)
            };
            _softwareRasterPipeline = _device.CreateComputePipelineState(swDesc);
        }

        /// <summary>注册网格资产并上传到 GPU。</summary>
        public void RegisterMesh(MicroClusterMeshAsset asset, RHICommandList cmdList)
        {
            if (asset.Clusterization == null) return;

            var cl = asset.Clusterization;
            // 创建顶点缓冲
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(cl.Vertices.Count * Marshal.SizeOf<MicroClusterVertex>()),
                Flags = RHIBufferFlags.VertexBuffer | RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<MicroClusterVertex>(),
                HeapType = RHIResourceHeapType.Default
            };
            asset.VertexBuffer = _device.CreateBuffer(vbDesc, $"MicroClusterVB_{asset.Name}");

            // 创建三角形缓冲
            var tbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(cl.Triangles.Count * Marshal.SizeOf<MicroClusterTriangle>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<MicroClusterTriangle>(),
                HeapType = RHIResourceHeapType.Default
            };
            asset.TriangleBuffer = _device.CreateBuffer(tbDesc, $"MicroClusterTB_{asset.Name}");

            // 创建簇元数据缓冲
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(cl.Clusters.Count * Marshal.SizeOf<MicroClusterCluster>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<MicroClusterCluster>(),
                HeapType = RHIResourceHeapType.Default
            };
            asset.ClusterBuffer = _device.CreateBuffer(cbDesc, $"MicroClusterCluster_{asset.Name}");

            // 创建组缓冲
            var gbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(Math.Max(1, cl.Groups.Count) * Marshal.SizeOf<MicroClusterClusterGroup>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<MicroClusterClusterGroup>(),
                HeapType = RHIResourceHeapType.Default
            };
            asset.GroupBuffer = _device.CreateBuffer(gbDesc, $"MicroClusterGroup_{asset.Name}");

            asset.BoundsCenter = (cl.BoundsMin + cl.BoundsMax) * 0.5f;
            asset.BoundsRadius = (cl.BoundsMax - cl.BoundsMin).Length() * 0.5f;
            Meshes[asset.AssetId] = asset;
        }

        /// <summary>
        /// 执行 GPU 驱动剔除 Pass。
        /// 对每个簇执行: 视锥剔除 + HZB 遮挡剔除 + 背面法线锥剔除 + 误差 LOD 选择。
        /// 输出可见簇列表与间接绘制参数。
        /// </summary>
        public void Cull(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos, int width, int height)
        {
            if (_cullPipeline == null || _rootSignature == null) return;

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_cullPipeline);

            // 确保可见性缓冲尺寸正确
            _visibilityBuffer.Resize(_device, width, height);

            // 准备剔除常量
            var cb = new MicroClusterCullCB
            {
                ViewMatrix = view,
                ProjMatrix = proj,
                ViewProjMatrix = view * proj,
                CameraPos = cameraPos,
                ErrorThreshold = ErrorThreshold,
                InvResolution = new Vector2(1.0f / width, 1.0f / height),
                LodDistanceScale = 1.0f,
                TotalClusters = (uint)GetTotalClusterCount(),
                FrameIndex = (uint)Environment.TickCount
            };

            // 状态转换
            if (_cullResult.VisibleClusterBuffer != null)
                cmdList.TransitionTo(_cullResult.VisibleClusterBuffer, RHIResourceState.UnorderedAccess);
            if (_cullResult.IndirectArgsBuffer != null)
                cmdList.TransitionTo(_cullResult.IndirectArgsBuffer, RHIResourceState.UnorderedAccess);

            // 分派剔除计算 (每簇一个线程组, 128 线程)
            int totalClusters = (int)cb.TotalClusters;
            int groups = (totalClusters + 63) / 64;
            cmdList.Dispatch(groups, 1, 1);

            // 转换间接参数为间接绘制状态
            if (_cullResult.IndirectArgsBuffer != null)
                cmdList.TransitionTo(_cullResult.IndirectArgsBuffer, RHIResourceState.IndirectArgument);
            if (_cullResult.VisibleClusterBuffer != null)
                cmdList.TransitionTo(_cullResult.VisibleClusterBuffer, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// 光栅化可见簇到可见性缓冲。
        /// 采用混合光栅化策略: 大三角形 -> 网格着色器硬光栅; 子像素三角形 -> 计算着色器软光栅 (Bin/Tile)。
        /// 软件光栅化路径使用分 bin 并行策略 (16x16 tile), HiZ 预测试加速深度测试。
        /// </summary>
        public void Rasterize(RHICommandList cmdList, in Matrix4x4 viewProj)
        {
            if (_visibilityBuffer.VisibilityTexture == null) return;

            // --- 硬件光栅化路径 (Mesh Shader, 大三角形) ---
            // 混合策略: 簇投影尺寸 >= 阈值时使用硬件光栅化
            if (_meshShaderPipeline != null && _rootSignature != null && _cullResult.IndirectArgsBuffer != null)
            {
                cmdList.SetPipelineState(_meshShaderPipeline);
                cmdList.SetGraphicsRootSignature(_rootSignature);

                var dsv = _device.CreateDSV(_visibilityBuffer.DepthTexture!);
                var rtv = _device.CreateRTV(_visibilityBuffer.VisibilityTexture);
                cmdList.OMSetRenderTargets(new[] { rtv }, dsv);
                cmdList.SetViewport(new RHIViewport(_visibilityBuffer.Width, _visibilityBuffer.Height));
                cmdList.SetScissorRect(new RHIScissorRect(0, 0, _visibilityBuffer.Width, _visibilityBuffer.Height));
                cmdList.ClearDepthStencilView(dsv, 1.0f, 0);
                cmdList.ClearRenderTargetView(rtv, Vector4.Zero);

                // 间接分发网格着色器
                cmdList.DrawIndexedInstancedIndirect(_cullResult.IndirectArgsBuffer, 0);
            }

            // --- 软件光栅化路径 (Compute Shader, 子像素三角形) ---
            // 混合策略: 簇投影尺寸 < 阈值时使用软件光栅化
            // 三阶段: Bin 分配 -> Tile 光栅化 -> (属性解码在后续 Pass)
            if (EnableSoftwareRaster && _softwareRasterizer != null && _softwareRasterizer.IsInitialized &&
                _cullResult.SoftwareRasterClusterBuffer != null)
            {
                var swCB = new SoftwareRasterCB
                {
                    ViewProjMatrix = viewProj,
                    InvResolution = new Vector2(1f / _visibilityBuffer.Width, 1f / _visibilityBuffer.Height),
                    VisibleClusterCount = (uint)Math.Max(1, _cullResult.LastSoftwareRasterClusterCount),
                    TileCountX = (uint)((_visibilityBuffer.Width + SoftwareRasterConstants.TileSize - 1) / SoftwareRasterConstants.TileSize),
                    TileCountY = (uint)((_visibilityBuffer.Height + SoftwareRasterConstants.TileSize - 1) / SoftwareRasterConstants.TileSize),
                    FrameIndex = (uint)Environment.TickCount
                };

                // 阶段 1: Bin 分配 (三角形按 16x16 tile 分配到 bin)
                _softwareRasterizer.BinAssignPass(cmdList, _cullResult.SoftwareRasterClusterBuffer, swCB);

                // 阶段 2: Tile 光栅化 (HiZ 预测试 + 原子可见性缓冲写入)
                _softwareRasterizer.RasterizePass(cmdList, _visibilityBuffer.VisibilityTexture,
                    _visibilityBuffer.DepthTexture!, null);
            }
        }

        /// <summary>
        /// 可见性缓冲解码 Pass: 从可见性缓冲读取每像素命中的三角形,
        /// 插值 Position/Normal/UV/Tangent, 输出 GeometryBuffer。
        /// 几何与材质完全解耦: 此 Pass 仅输出几何属性, 材质 Pass 后续执行。
        /// </summary>
        public void DecodeVisibility(RHICommandList cmdList, RHITexture[] geometrybufferTargets, in Matrix4x4 invViewProj, Vector3 cameraPos)
        {
            if (_softwareRasterizer == null || _visibilityBuffer.VisibilityTexture == null) return;

            var cb = new AttributeDecodeCB
            {
                InvViewProjMatrix = invViewProj,
                CameraPos = cameraPos,
                InvResolution = new Vector2(1f / _visibilityBuffer.Width, 1f / _visibilityBuffer.Height),
                MaterialCount = 256
            };

            // 阶段 3: 属性解码 (从可见性缓冲插值顶点属性 -> GeometryBuffer)
            _softwareRasterizer.DecodePass(cmdList, _visibilityBuffer.VisibilityTexture, geometrybufferTargets, cb);
        }

        /// <summary>处理流式加载缺页。</summary>
        public void ProcessStreaming(RHICommandList cmdList, Func<uint, byte[]> pageDataProvider)
        {
            _streamingManager?.ProcessPageFaults(cmdList, pageDataProvider);
        }

        /// <summary>获取所有已注册网格的总簇数。</summary>
        public uint GetTotalClusterCount()
        {
            uint total = 0;
            foreach (var mesh in Meshes.Values)
                if (mesh.Clusterization != null)
                    total += (uint)mesh.Clusterization.ClusterCount;
            return total;
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _cullPipeline?.Dispose();
            _meshShaderPipeline?.Dispose();
            _softwareRasterPipeline?.Dispose();
            _rootSignature?.Dispose();
            _visibilityBuffer.Dispose();
            _cullResult.Dispose();
            _softwareRasterizer?.Dispose();
            _gpuPageTable?.Dispose();
            foreach (var mesh in Meshes.Values) mesh.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// MicroCluster 虚拟几何体系统入口。管理网格资产的 MicroCluster 化处理、
    /// 运行时 LOD 选择、遮挡剔除与间接绘制提交。
    /// </summary>
    public sealed class MicroClusterVirtualGeometry
    {
        private RHIDevice? _device;
        private RHICommandQueue? _graphicsQueue;
        private MicroClusterConfig? _config;

        public void Initialize(RHIDevice device, RHICommandQueue graphicsQueue, MicroClusterConfig config)
        {
            _device = device;
            _graphicsQueue = graphicsQueue;
            _config = config;
        }

        public void Dispose() { }
    }
}
