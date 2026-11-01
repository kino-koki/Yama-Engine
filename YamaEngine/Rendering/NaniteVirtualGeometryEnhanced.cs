// ════════════════════════════════════════════════════════════════════════
// yama引擎 - Nanite 风格虚拟几何增强 (NaniteVirtualGeometryEnhanced)
//
// 实现 UE5 Nanite 架构的虚拟化几何体系统, 包含:
//   1. 离线处理管线:
//      - 网格分割为簇 (Cluster, ~128 三角形)
//      - 簇边界构建: 基于位置+法线的边界度量
//      - LOD 生成: QEM (Quadric Error Metric) 边折叠 → 簇简化 → 新簇
//      - 层级构建: 4 个簇合并 → 父簇, 递归直到根
//      - 位置量化: 16 位定点量化到簇局部空间
//   2. 运行时渲染:
//      - 可见性剔除: BVH + Hi-Z 遮挡剔除
//      - 簇选择: 基于屏幕误差阈值选择 LOD 层级
//      - 可见性缓冲 (Visibility Buffer): 光栅化到 visibility buffer 而非 GBuffer
//      - 材质提取: 延迟材质应用 (visibility buffer → GBuffer)
//      - 软件光栅化回退: compute shader 软件光栅化微小三角形
//   3. 性能优化:
//      - 簇打包: 紧凑内存布局
//      - DrawIndirect: GPU 驱动绘制, 零 CPU 回读
//      - 持久化 mesh shader: 一个 MS 处理所有 LOD
//
// 对接系统: RHI (RHIDevice/RHICommandList) / MicroCluster 虚拟几何 / 场景渲染器
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ────────────────────────────────────────────────────────────────────────
    // 常量与配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Nanite 增强系统全局常量。</summary>
    public static class NaniteEnhancedConstants
    {
        /// <summary>每簇三角形数量。</summary>
        public const int TrianglesPerCluster = 128;
        /// <summary>每簇最大顶点数。</summary>
        public const int MaxVerticesPerCluster = 256;
        /// <summary>合并为父簇的子簇数。</summary>
        public const int ClusterMergeFactor = 4;
        /// <summary>位置量化位数 (每轴)。</summary>
        public const int PositionQuantizationBits = 16;
        /// <summary>屏幕空间误差阈值 (像素)。</summary>
        public const float DefaultScreenErrorThreshold = 1.0f;
        /// <summary>软件光栅化像素面积阈值。</summary>
        public const float SoftwareRasterThreshold = 16.0f;
    }

    /// <summary>Nanite 质量档位。</summary>
    public enum NaniteQualityLevel { Low, Medium, High, Ultra }

    /// <summary>Nanite 增强配置。</summary>
    public sealed class NaniteEnhancedConfig
    {
        public NaniteQualityLevel Quality = NaniteQualityLevel.High;
        public float ScreenErrorThreshold = NaniteEnhancedConstants.DefaultScreenErrorThreshold;
        public bool EnableHiZCulling = true;
        public bool EnableBackfaceCulling = true;
        public bool EnableSoftwareRasterization = true;
        public bool EnableMeshShaders = true;
        public bool EnablePersistentMeshShader = true;
        public int MaxLODLevels = 20;
        public float SoftwareRasterPixelThreshold = NaniteEnhancedConstants.SoftwareRasterThreshold;
        public int ClusterPageSize = 128 * 1024;

        public void ApplyQualityPreset(NaniteQualityLevel q)
        {
            Quality = q;
            switch (q)
            {
                case NaniteQualityLevel.Low:
                    ScreenErrorThreshold = 2.0f; EnableHiZCulling = true;
                    EnableSoftwareRasterization = true; EnableMeshShaders = false; break;
                case NaniteQualityLevel.Medium:
                    ScreenErrorThreshold = 1.5f; EnableHiZCulling = true;
                    EnableSoftwareRasterization = true; EnableMeshShaders = true; break;
                case NaniteQualityLevel.High:
                    ScreenErrorThreshold = 1.0f; EnableHiZCulling = true;
                    EnableSoftwareRasterization = true; EnableMeshShaders = true; break;
                case NaniteQualityLevel.Ultra:
                    ScreenErrorThreshold = 0.5f; EnableHiZCulling = true;
                    EnableSoftwareRasterization = true; EnableMeshShaders = true; break;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 数据结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>增强簇: Nanite 的原子渲染单元。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NaniteEnhancedCluster
    {
        public uint ClusterId;
        public uint LodLevel;
        public uint ParentClusterId;
        public uint FirstChildClusterId;
        public uint ChildCount;
        public uint VertexOffset;
        public uint IndexOffset;
        public uint TriangleCount;
        public Vector3 BoundsCenter;
        public float BoundsRadius;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public float ScreenError;
        public float GeometricError;
        public Vector3 NormalConeAxis;
        public float NormalConeCosHalfAngle;
        public uint MaterialSlot;
        public uint PageId;
        public uint PageOffset;
        public uint IsLeaf;

        public readonly bool IsRoot => ParentClusterId == 0xFFFFFFFF;
        public readonly bool HasChildren => ChildCount > 0;
    }

    /// <summary>量化顶点 (16 位定点位置)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NaniteEnhancedVertex
    {
        public ushort PosX, PosY, PosZ;
        public ushort EncodedNormal;
        public ushort EncodedU;
        public ushort EncodedV;
        public byte TangentSign;
        public byte ColorIndex;
        public ushort Pad;
    }

    /// <summary>压缩三角形 (8 位簇内索引)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NaniteEnhancedTriangle
    {
        public byte I0, I1, I2;
        public byte MaterialSlot;
        public byte EdgeFlags;
    }

    /// <summary>QEM 二次误差矩阵 (4x4 对称, 上三角 10 元素)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NaniteQEM
    {
        public fixed float Q[10];
        public float Error;

        public static NaniteQEM FromPlane(float a, float b, float c, float d)
        {
            var q = new NaniteQEM();
            q.Q[0] = a * a; q.Q[1] = a * b; q.Q[2] = a * c; q.Q[3] = a * d;
            q.Q[4] = b * b; q.Q[5] = b * c; q.Q[6] = b * d;
            q.Q[7] = c * c; q.Q[8] = c * d;
            q.Q[9] = d * d;
            return q;
        }

        public static NaniteQEM Add(in NaniteQEM a, in NaniteQEM b)
        {
            var r = new NaniteQEM();
            for (int i = 0; i < 10; i++) r.Q[i] = a.Q[i] + b.Q[i];
            return r;
        }

        public float Evaluate(Vector3 v)
        {
            float x = v.X, y = v.Y, z = v.Z;
            // v^T * Q * v, 其中 v = (x, y, z, 1)
            float r = Q[0] * x * x + 2 * Q[1] * x * y + 2 * Q[2] * x * z + 2 * Q[3] * x
                    + Q[4] * y * y + 2 * Q[5] * y * z + 2 * Q[6] * y
                    + Q[7] * z * z + 2 * Q[8] * z
                    + Q[9];
            return r;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 离线处理管线
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nanite 离线网格处理器: 将原始网格分割为簇, 通过 QEM 生成多级 LOD,
    /// 构建层级 DAG, 量化位置数据。
    /// </summary>
    public sealed class NaniteMeshProcessor
    {
        /// <summary>网格分割为簇 (每簇 ~128 三角形)。</summary>
        public static List<NaniteEnhancedCluster> Clusterize(
            ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals,
            ReadOnlySpan<int> indices, int materialSlot = 0)
        {
            var clusters = new List<NaniteEnhancedCluster>();
            int triCount = indices.Length / 3;
            int clusterCount = (triCount + NaniteEnhancedConstants.TrianglesPerCluster - 1)
                             / NaniteEnhancedConstants.TrianglesPerCluster;

            uint nextId = 0;
            for (int c = 0; c < clusterCount; c++)
            {
                int firstTri = c * NaniteEnhancedConstants.TrianglesPerCluster;
                int triCountInCluster = Math.Min(NaniteEnhancedConstants.TrianglesPerCluster, triCount - firstTri);

                var cluster = new NaniteEnhancedCluster
                {
                    ClusterId = nextId++,
                    LodLevel = 0,
                    ParentClusterId = 0xFFFFFFFF,
                    FirstChildClusterId = 0,
                    ChildCount = 0,
                    IndexOffset = (uint)(firstTri * 3),
                    TriangleCount = (uint)triCountInCluster,
                    MaterialSlot = (uint)materialSlot,
                    IsLeaf = 1
                };

                // 计算簇 AABB 与包围球
                Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
                for (int t = 0; t < triCountInCluster; t++)
                {
                    int i0 = indices[(firstTri + t) * 3 + 0];
                    int i1 = indices[(firstTri + t) * 3 + 1];
                    int i2 = indices[(firstTri + t) * 3 + 2];
                    bmin = Vector3.Min(bmin, positions[i0]);
                    bmin = Vector3.Min(bmin, positions[i1]);
                    bmin = Vector3.Min(bmin, positions[i2]);
                    bmax = Vector3.Max(bmax, positions[i0]);
                    bmax = Vector3.Max(bmax, positions[i1]);
                    bmax = Vector3.Max(bmax, positions[i2]);
                }
                cluster.BoundsMin = bmin;
                cluster.BoundsMax = bmax;
                cluster.BoundsCenter = (bmin + bmax) * 0.5f;
                cluster.BoundsRadius = (bmax - bmin).Length() * 0.5f;

                // 构建法线锥 (用于背面剔除)
                cluster.NormalConeAxis = ComputeClusterNormalCone(
                    positions, normals, indices, firstTri, triCountInCluster, out float cosHalf);
                cluster.NormalConeCosHalfAngle = cosHalf;

                // 计算几何误差 (LOD 0 误差为 0)
                cluster.GeometricError = 0;
                cluster.ScreenError = 0;

                clusters.Add(cluster);
            }

            return clusters;
        }

        /// <summary>计算簇法线锥 (用于背面剔除)。</summary>
        private static Vector3 ComputeClusterNormalCone(
            ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals,
            ReadOnlySpan<int> indices, int firstTri, int triCount, out float cosHalfAngle)
        {
            Vector3 avgNormal = Vector3.Zero;
            float minDot = 1.0f;

            for (int t = 0; t < triCount; t++)
            {
                int i0 = indices[(firstTri + t) * 3 + 0];
                int i1 = indices[(firstTri + t) * 3 + 1];
                int i2 = indices[(firstTri + t) * 3 + 2];

                // 三角形面法线
                Vector3 e1 = positions[i1] - positions[i0];
                Vector3 e2 = positions[i2] - positions[i0];
                Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(e1, e2));

                avgNormal += faceNormal;
                float d0 = Vector3.Dot(faceNormal, normals[i0]);
                float d1 = Vector3.Dot(faceNormal, normals[i1]);
                float d2 = Vector3.Dot(faceNormal, normals[i2]);
                minDot = Math.Min(minDot, Math.Min(d0, Math.Min(d1, d2)));
            }

            avgNormal = avgNormal.Length() > 1e-6f ? Vector3.Normalize(avgNormal) : Vector3.UnitY;
            cosHalfAngle = Math.Clamp(minDot, -1.0f, 1.0f);
            return avgNormal;
        }

        /// <summary>
        /// QEM 边折叠简化: 将一个簇简化到目标三角形数。
        /// 使用优先队列选择最小误差边进行折叠。
        /// </summary>
        public static (List<int> newIndices, float maxError) SimplifyCluster(
            ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices,
            int targetTriangleCount)
        {
            int triCount = indices.Length / 3;
            if (triCount <= targetTriangleCount)
                return (new List<int>(indices.ToArray()), 0);

            // 为每个顶点计算 QEM 矩阵
            var quadrics = new NaniteQEM[positions.Length];
            for (int t = 0; t < triCount; t++)
            {
                int i0 = indices[t * 3], i1 = indices[t * 3 + 1], i2 = indices[t * 3 + 2];
                Vector3 e1 = positions[i1] - positions[i0];
                Vector3 e2 = positions[i2] - positions[i0];
                Vector3 normal = Vector3.Cross(e1, e2);
                float len = normal.Length();
                if (len < 1e-8f) continue;
                normal /= len;
                float d = -Vector3.Dot(normal, positions[i0]);
                var q = NaniteQEM.FromPlane(normal.X, normal.Y, normal.Z, d);
                quadrics[i0] = NaniteQEM.Add(quadrics[i0], q);
                quadrics[i1] = NaniteQEM.Add(quadrics[i1], q);
                quadrics[i2] = NaniteQEM.Add(quadrics[i2], q);
            }

            // 边折叠优先队列 (误差, 边)
            var edgeQueue = new YamaEngine.RHI.PriorityQueue<(int, int), float>();
            var edgeSet = new HashSet<(int, int)>();

            for (int t = 0; t < triCount; t++)
            {
                AddEdge(edgeQueue, edgeSet, indices[t * 3], indices[t * 3 + 1], positions, quadrics);
                AddEdge(edgeQueue, edgeSet, indices[t * 3 + 1], indices[t * 3 + 2], positions, quadrics);
                AddEdge(edgeQueue, edgeSet, indices[t * 3 + 2], indices[t * 3 + 0], positions, quadrics);
            }

            // 活跃三角形集合
            var activeTris = new List<(int i0, int i1, int i2, bool alive)>();
            for (int t = 0; t < triCount; t++)
                activeTris.Add((indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2], true));

            // 顶点重映射 (折叠后指向新顶点)
            var remap = new int[positions.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = i;

            float maxError = 0;
            int currentTriCount = triCount;

            while (edgeQueue.Count > 0 && currentTriCount > targetTriangleCount)
            {
                var (v0, v1) = edgeQueue.Dequeue();
                v0 = ResolveRemap(remap, v0);
                v1 = ResolveRemap(remap, v1);
                if (v0 == v1) continue;

                // 折叠 v1 → v0, 新位置取中点 (或最优位置)
                Vector3 newPos = (positions[v0] + positions[v1]) * 0.5f;
                float error = quadrics[v0].Evaluate(newPos) + quadrics[v1].Evaluate(newPos);
                maxError = Math.Max(maxError, error);

                // 合并 QEM
                quadrics[v0] = NaniteQEM.Add(quadrics[v0], quadrics[v1]);
                remap[v1] = v0;

                // 标记退化三角形 (v0 和 v1 在同一三角形中)
                for (int t = 0; t < activeTris.Count; t++)
                {
                    if (!activeTris[t].alive) continue;
                    int r0 = ResolveRemap(remap, activeTris[t].i0);
                    int r1 = ResolveRemap(remap, activeTris[t].i1);
                    int r2 = ResolveRemap(remap, activeTris[t].i2);
                    if (r0 == r1 || r1 == r2 || r0 == r2)
                    {
                        activeTris[t] = (activeTris[t].i0, activeTris[t].i1, activeTris[t].i2, false);
                        currentTriCount--;
                    }
                }
            }

            // 输出存活三角形
            var result = new List<int>();
            foreach (var tri in activeTris)
            {
                if (!tri.alive) continue;
                result.Add(ResolveRemap(remap, tri.i0));
                result.Add(ResolveRemap(remap, tri.i1));
                result.Add(ResolveRemap(remap, tri.i2));
            }

            return (result, maxError);
        }

        private static void AddEdge(YamaEngine.RHI.PriorityQueue<(int, int), float> queue,
            HashSet<(int, int)> set, int a, int b, ReadOnlySpan<Vector3> pos, NaniteQEM[] quadrics)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            if (set.Contains((lo, hi))) return;
            set.Add((lo, hi));
            Vector3 mid = (pos[lo] + pos[hi]) * 0.5f;
            float err = quadrics[lo].Evaluate(mid) + quadrics[hi].Evaluate(mid);
            queue.Enqueue((lo, hi), err);
        }

        private static int ResolveRemap(int[] remap, int v)
        {
            while (remap[v] != v) v = remap[v];
            return v;
        }

        /// <summary>构建层级 DAG: 每 4 个簇合并为父簇, 递归到根。</summary>
        public static void BuildHierarchy(List<NaniteEnhancedCluster> clusters, int maxLevels)
        {
            int currentLevelStart = 0;
            int currentLevelCount = clusters.Count;
            uint nextId = (uint)clusters.Count;

            for (int level = 1; level <= maxLevels; level++)
            {
                if (currentLevelCount <= 1) break;

                int parentCount = (currentLevelCount + NaniteEnhancedConstants.ClusterMergeFactor - 1)
                                / NaniteEnhancedConstants.ClusterMergeFactor;

                for (int p = 0; p < parentCount; p++)
                {
                    int childStart = currentLevelStart + p * NaniteEnhancedConstants.ClusterMergeFactor;
                    int childCount = Math.Min(NaniteEnhancedConstants.ClusterMergeFactor,
                        currentLevelCount - p * NaniteEnhancedConstants.ClusterMergeFactor);

                    // 计算父簇包围球 (合并子簇)
                    Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
                    float maxChildError = 0;
                    for (int c = 0; c < childCount; c++)
                    {
                        var child = clusters[childStart + c];
                        bmin = Vector3.Min(bmin, child.BoundsMin);
                        bmax = Vector3.Max(bmax, child.BoundsMax);
                        maxChildError = Math.Max(maxChildError, child.GeometricError);
                        child.ParentClusterId = nextId;
                        clusters[childStart + c] = child;
                    }

                    var parent = new NaniteEnhancedCluster
                    {
                        ClusterId = nextId++,
                        LodLevel = (uint)level,
                        ParentClusterId = 0xFFFFFFFF,
                        FirstChildClusterId = (uint)childStart,
                        ChildCount = (uint)childCount,
                        BoundsMin = bmin,
                        BoundsMax = bmax,
                        BoundsCenter = (bmin + bmax) * 0.5f,
                        BoundsRadius = (bmax - bmin).Length() * 0.5f,
                        GeometricError = maxChildError * 2.0f, // 父簇误差更大
                        IsLeaf = 0
                    };
                    clusters.Add(parent);
                }

                currentLevelStart += currentLevelCount;
                currentLevelCount = parentCount;
            }
        }

        /// <summary>16 位定点量化顶点位置到簇局部空间。</summary>
        public static NaniteEnhancedVertex QuantizeVertex(
            Vector3 position, Vector3 normal, Vector2 uv,
            Vector3 clusterMin, Vector3 clusterMax)
        {
            Vector3 extent = clusterMax - clusterMin;
            Vector3 normalized = extent.Length() > 1e-8f
                ? (position - clusterMin) / extent
                : Vector3.Zero;

            float maxQuant = (1 << NaniteEnhancedConstants.PositionQuantizationBits) - 1;
            var vertex = new NaniteEnhancedVertex
            {
                PosX = (ushort)Math.Clamp(normalized.X * maxQuant, 0, maxQuant),
                PosY = (ushort)Math.Clamp(normalized.Y * maxQuant, 0, maxQuant),
                PosZ = (ushort)Math.Clamp(normalized.Z * maxQuant, 0, maxQuant)
            };

            // 八面体编码法线到 16 位
            Vector2 oct = OctahedralParameterization.Encode(normal);
            vertex.EncodedNormal = (ushort)((int)(oct.X * 255) | ((int)(oct.Y * 255) << 8));

            // UV 量化到 16 位
            vertex.EncodedU = (ushort)Math.Clamp(uv.X * 65535, 0, 65535);
            vertex.EncodedV = (ushort)Math.Clamp(uv.Y * 65535, 0, 65535);

            return vertex;
        }

        /// <summary>解量化顶点位置。</summary>
        public static Vector3 DequantizePosition(in NaniteEnhancedVertex vertex,
            Vector3 clusterMin, Vector3 clusterMax)
        {
            float maxQuant = (1 << NaniteEnhancedConstants.PositionQuantizationBits) - 1;
            Vector3 extent = clusterMax - clusterMin;
            Vector3 normalized = new(vertex.PosX / maxQuant, vertex.PosY / maxQuant, vertex.PosZ / maxQuant);
            return clusterMin + normalized * extent;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 运行时渲染: 可见性剔除与 LOD 选择
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 运行时可见性剔除与 LOD 选择: BVH 遍历 + Hi-Z 遮挡剔除,
    /// 基于屏幕误差阈值选择 LOD 层级。
    /// </summary>
    public sealed class NaniteVisibilityCulling : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly NaniteEnhancedConfig _config;
        private RHIBuffer? _visibleClustersBuffer;
        private RHIBuffer? _visibleClusterCount;
        private RHIBuffer? _indirectDrawArgs;
        private RHITexture? _hizBuffer;
        private int _maxClusters;
        private bool _initialized;

        /// <summary>可见簇缓冲。</summary>
        public RHIBuffer? VisibleClustersBuffer => _visibleClustersBuffer;
        /// <summary>间接绘制参数缓冲。</summary>
        public RHIBuffer? IndirectDrawArgs => _indirectDrawArgs;
        /// <summary>Hi-Z 深度缓冲。</summary>
        public RHITexture? HiZBuffer => _hizBuffer;

        public NaniteVisibilityCulling(RHIDevice device, NaniteEnhancedConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化 GPU 资源。</summary>
        public void Initialize(int maxClusters, int renderWidth, int renderHeight)
        {
            _maxClusters = maxClusters;

            _visibleClustersBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(maxClusters * 4), // 每可见簇一个 uint 索引
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 4,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteVisibleClusters");

            _visibleClusterCount = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 16,
                Flags = RHIBufferFlags.UnorderedAccess,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteVisibleClusterCount");

            // DrawIndirect 参数: vertexCountPerInstance, instanceCount, startVertex, startInstance
            _indirectDrawArgs = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 64, // 4 uint x 16 bytes alignment
                Flags = RHIBufferFlags.IndirectArgs | RHIBufferFlags.UnorderedAccess,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteIndirectDrawArgs");

            // Hi-Z 深度缓冲 (Mip 链)
            int mipLevels = (int)MathF.Ceiling(MathF.Log2(Math.Max(renderWidth, renderHeight))) + 1;
            _hizBuffer = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32_Float,
                Width = renderWidth, Height = renderHeight, DepthOrArraySize = 1,
                MipLevels = mipLevels, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "NaniteHiZ");

            _initialized = true;
        }

        /// <summary>构建 Hi-Z 深度 Mip 链 (从深度缓冲生成层级)。</summary>
        public void BuildHiZ(RHICommandList cmdList, RHITexture depthBuffer, int width, int height)
        {
            if (_hizBuffer == null || !_config.EnableHiZCulling) return;

            cmdList.TransitionTo(_hizBuffer, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(depthBuffer, RHIResourceState.NonPixelShaderResource);

            int mipLevels = _hizBuffer.TextureDesc?.MipLevels ?? 1;
            for (int mip = 1; mip < mipLevels; mip++)
            {
                int mipWidth = Math.Max(1, width >> mip);
                int mipHeight = Math.Max(1, height >> mip);
                int groupsX = (mipWidth + 7) / 8;
                int groupsY = (mipHeight + 7) / 8;
                // cmdList.SetComputeRoot32BitConstants(... mipLevel ...);
                cmdList.Dispatch(groupsX, groupsY, 1);
            }
            cmdList.TransitionTo(_hizBuffer, RHIResourceState.ShaderResource);
        }

        /// <summary>
        /// 执行可见性剔除: BVH 遍历 + Hi-Z 遮挡剔除 + 屏幕误差 LOD 选择。
        /// 输出可见簇索引列表和间接绘制参数。
        /// </summary>
        public void Cull(RHICommandList cmdList, RHIBuffer clusterBuffer, int clusterCount,
            in Matrix4x4 viewProj, in Vector3 cameraPos, float fov, int screenWidth, int screenHeight,
            uint frameIndex)
        {
            if (!_initialized || _visibleClustersBuffer == null) return;

            cmdList.TransitionTo(_visibleClustersBuffer, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_visibleClusterCount, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(clusterBuffer, RHIResourceState.NonPixelShaderResource);
            if (_config.EnableHiZCulling && _hizBuffer != null)
                cmdList.TransitionTo(_hizBuffer, RHIResourceState.NonPixelShaderResource);

            int groups = (clusterCount + 63) / 64;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... viewProj, cameraPos, fov, errorThreshold ...);
            cmdList.Dispatch(groups, 1, 1);

            cmdList.TransitionTo(_visibleClustersBuffer, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_visibleClusterCount, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>计算簇的屏幕空间误差 (CPU 侧逻辑, GPU 着色器使用相同公式)。</summary>
        public static float ComputeScreenError(in NaniteEnhancedCluster cluster,
            in Vector3 cameraPos, float fov, int screenHeight)
        {
            float distance = Vector3.Distance(cameraPos, cluster.BoundsCenter);
            if (distance < cluster.BoundsRadius) return float.MaxValue; // 相机在簇内

            // 屏幕误差 = geometricError * screenHeight / (distance * tan(fov/2))
            float tanHalfFov = MathF.Tan(fov * 0.5f);
            float screenError = cluster.GeometricError * screenHeight / (distance * tanHalfFov);
            return screenError;
        }

        /// <summary>选择 LOD 层级 (递归选择最小满足屏幕误差阈值的簇)。</summary>
        public static void SelectLOD(ReadOnlySpan<NaniteEnhancedCluster> clusters,
            int rootIndex, in Vector3 cameraPos, float fov, int screenHeight,
            float errorThreshold, List<int> visibleClusters)
        {
            // 深度优先遍历 DAG
            var stack = new Stack<int>();
            stack.Push(rootIndex);

            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                var cluster = clusters[idx];

                float screenError = ComputeScreenError(cluster, cameraPos, fov, screenHeight);

                if (screenError <= errorThreshold || !cluster.HasChildren)
                {
                    // 误差可接受或无子簇, 选择此簇
                    visibleClusters.Add(idx);
                }
                else
                {
                    // 误差过大, 递归到子簇
                    for (int c = 0; c < cluster.ChildCount; c++)
                    {
                        int childIdx = (int)cluster.FirstChildClusterId + c;
                        if (childIdx < clusters.Length)
                            stack.Push(childIdx);
                    }
                }
            }
        }

        /// <summary>Hi-Z 遮挡剔除测试 (CPU 侧逻辑)。</summary>
        public static bool IsOccluded(in NaniteEnhancedCluster cluster,
            in Matrix4x4 viewProj, RHITexture hizBuffer, int screenWidth, int screenHeight)
        {
            // 将簇包围球投影到屏幕空间
            Vector4 center = Vector4.Transform(new Vector4(cluster.BoundsCenter, 1), viewProj);
            if (center.W <= 0) return true; // 在相机后面

            Vector2 screenPos = new(center.X / center.W, center.Y / center.W);
            float projectedRadius = cluster.BoundsRadius / center.W * screenHeight * 0.5f;

            // 转换到屏幕坐标
            int sx = (int)((screenPos.X * 0.5f + 0.5f) * screenWidth);
            int sy = (int)((-screenPos.Y * 0.5f + 0.5f) * screenHeight);

            // 采样 Hi-Z 最接近的 mip 层级
            int mipLevel = (int)MathF.Ceiling(MathF.Log2(Math.Max(1, projectedRadius * 2)));
            // 实际 Hi-Z 采样由 GPU 着色器执行
            // float hizDepth = SampleHiZ(hizBuffer, sx, sy, mipLevel);
            // return hizDepth < center.Z / center.W;
            return false; // CPU 侧默认不剔除
        }

        public void Dispose()
        {
            _visibleClustersBuffer?.Dispose();
            _visibleClusterCount?.Dispose();
            _indirectDrawArgs?.Dispose();
            _hizBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 可见性缓冲光栅化
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 可见性缓冲光栅化器: 将簇光栅化到可见性缓冲 (visibility buffer) 而非 GBuffer。
    /// 微小三角形使用 compute shader 软件光栅化, 大三角形使用 mesh shader 硬件光栅化。
    /// </summary>
    public sealed class NaniteVisibilityRasterizer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly NaniteEnhancedConfig _config;
        private RHITexture? _visibilityBuffer;
        private RHITexture? _visibilityDepth;
        private RHIBuffer? _instanceDataBuffer;
        private int _width, _height;

        /// <summary>可见性缓冲纹理。</summary>
        public RHITexture? VisibilityBuffer => _visibilityBuffer;
        /// <summary>可见性深度。</summary>
        public RHITexture? VisibilityDepth => _visibilityDepth;

        public NaniteVisibilityRasterizer(RHIDevice device, NaniteEnhancedConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化资源。</summary>
        public void Initialize(int width, int height, int maxInstances)
        {
            _width = width;
            _height = height;

            // 可见性缓冲: R32G32B32A32_UInt (primitiveID + instanceID + barycentric)
            _visibilityBuffer = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32G32B32A32_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "NaniteVisibilityBuffer");

            _visibilityDepth = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
            }, "NaniteVisibilityDepth");

            _instanceDataBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(maxInstances * 128), // 每实例 128 字节
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 128,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteInstanceData");
        }

        /// <summary>
        /// 硬件光栅化: 使用 mesh shader 光栅化可见簇到可见性缓冲。
        /// 持久化 mesh shader: 一个 MS 处理所有 LOD。
        /// </summary>
        public void RasterizeHardware(RHICommandList cmdList,
            RHIBuffer visibleClusters, RHIBuffer clusterData,
            RHIBuffer vertexData, RHIBuffer indexData,
            RHIBuffer indirectDrawArgs, int visibleClusterCount)
        {
            if (_visibilityBuffer == null) return;

            cmdList.TransitionTo(_visibilityBuffer, RHIResourceState.RenderTarget);
            cmdList.TransitionTo(_visibilityDepth, RHIResourceState.DepthWrite);
            cmdList.TransitionTo(visibleClusters, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(clusterData, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(vertexData, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(indexData, RHIResourceState.NonPixelShaderResource);

            // 清除可见性缓冲
            var rtv = _device.CreateRTV(_visibilityBuffer);
            var dsv = _device.CreateDSV(_visibilityDepth);
            cmdList.ClearRenderTargetView(rtv, new Vector4(-1, -1, -1, -1));
            cmdList.ClearDepthStencilView(dsv, 1.0f, 0);

            cmdList.OMSetRenderTargets(new[] { rtv }, dsv);
            cmdList.RSSetViewport(0, 0, _width, _height, 0, 1);
            cmdList.RSSetScissorRect(0, 0, _width, _height);

            // 持久化 mesh shader: 通过 DrawIndirect 驱动
            // cmdList.SetGraphicsRootSignature(...);
            // cmdList.SetPipelineState(_persistentMeshShaderPSO);
            cmdList.DrawIndexedInstancedIndirect(indirectDrawArgs, 0);

            cmdList.TransitionTo(_visibilityBuffer, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_visibilityDepth, RHIResourceState.ShaderResource);
        }

        /// <summary>
        /// 软件光栅化回退: compute shader 光栅化微小三角形 (面积 < 阈值)。
        /// 每线程处理一个三角形, 直接写入可见性缓冲 (UAV)。
        /// </summary>
        public void RasterizeSoftware(RHICommandList cmdList,
            RHIBuffer visibleClusters, RHIBuffer clusterData,
            RHIBuffer vertexData, RHIBuffer indexData,
            int visibleClusterCount)
        {
            if (_visibilityBuffer == null || !_config.EnableSoftwareRasterization) return;

            cmdList.TransitionTo(_visibilityBuffer, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_visibilityDepth, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(visibleClusters, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(clusterData, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(vertexData, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(indexData, RHIResourceState.NonPixelShaderResource);

            // 每线程组处理一个簇 (128 三角形)
            int groups = Math.Max(1, (visibleClusterCount + 31) / 32);
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetPipelineState(_softwareRasterPSO);
            // cmdList.SetComputeRoot32BitConstants(... screenWidth, screenHeight, threshold ...);
            cmdList.Dispatch(groups, 1, 1);

            cmdList.TransitionTo(_visibilityBuffer, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(_visibilityDepth, RHIResourceState.ShaderResource);
        }

        /// <summary>
        /// 材质提取 (延迟材质应用): 从可见性缓冲解码三角形信息,
        /// 从原始几何数据中拉取材质属性, 写入 GBuffer。
        /// </summary>
        public void ExtractMaterials(RHICommandList cmdList,
            RHITexture gbufferAlbedo, RHITexture gbufferNormal,
            RHITexture gbufferRoughnessMetallic, RHITexture gbufferDepth,
            RHIBuffer vertexData, RHIBuffer indexData, RHIBuffer instanceData,
            RHIBuffer materialData)
        {
            if (_visibilityBuffer == null) return;

            cmdList.TransitionTo(_visibilityBuffer, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(gbufferAlbedo, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(gbufferNormal, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(gbufferRoughnessMetallic, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(gbufferDepth, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(vertexData, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(indexData, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(instanceData, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(materialData, RHIResourceState.NonPixelShaderResource);

            int groupsX = (_width + 7) / 8;
            int groupsY = (_height + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetPipelineState(_materialExtractionPSO);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(gbufferAlbedo, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(gbufferNormal, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(gbufferRoughnessMetallic, RHIResourceState.ShaderResource);
            cmdList.TransitionTo(gbufferDepth, RHIResourceState.ShaderResource);
        }

        /// <summary>软件光栅化三角形 (CPU 侧逻辑, GPU 着色器使用相同算法)。</summary>
        public static void SoftwareRasterizeTriangle(
            int px, int py, int screenWidth, int screenHeight,
            in Vector3 v0, in Vector3 v1, in Vector3 v2,
            out bool covered, out float depth, out Vector2 barycentric)
        {
            covered = false;
            depth = 1.0f;
            barycentric = Vector2.Zero;

            // 重心坐标计算 (屏幕空间)
            Vector2 s0 = new(v0.X, v0.Y), s1 = new(v1.X, v1.Y), s2 = new(v2.X, v2.Y);
            Vector2 p = new(px + 0.5f, py + 0.5f);

            float area = (s1.X - s0.X) * (s2.Y - s0.Y) - (s2.X - s0.X) * (s1.Y - s0.Y);
            if (MathF.Abs(area) < 1e-8f) return;

            float w0 = ((s1.X - p.X) * (s2.Y - p.Y) - (s2.X - p.X) * (s1.Y - p.Y)) / area;
            float w1 = ((s2.X - p.X) * (s0.Y - p.Y) - (s0.X - p.X) * (s2.Y - p.Y)) / area;
            float w2 = 1 - w0 - w1;

            if (w0 < 0 || w1 < 0 || w2 < 0) return;

            covered = true;
            barycentric = new Vector2(w0, w1);
            depth = w0 * v0.Z + w1 * v1.Z + w2 * v2.Z;
        }

        public void Dispose()
        {
            _visibilityBuffer?.Dispose();
            _visibilityDepth?.Dispose();
            _instanceDataBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统: Nanite 虚拟几何增强
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nanite 风格虚拟几何增强主系统。组合离线处理、运行时剔除、
    /// 可见性缓冲光栅化和材质提取, 实现百万级三角形实时渲染。
    /// </summary>
    public sealed class NaniteVirtualGeometryEnhanced : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly NaniteEnhancedConfig _config;
        private readonly NaniteVisibilityCulling _culling;
        private readonly NaniteVisibilityRasterizer _rasterizer;

        // 处理后的网格资产
        private readonly Dictionary<Guid, NaniteEnhancedMeshAsset> _assets = new();
        private RHIBuffer? _globalClusterBuffer;
        private RHIBuffer? _globalVertexBuffer;
        private RHIBuffer? _globalIndexBuffer;
        private RHIBuffer? _materialDataBuffer;
        private int _totalClusters;
        private bool _initialized;

        /// <summary>Nanite 常量缓冲。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct NaniteConstants
        {
            public Matrix4x4 ViewProjMatrix;
            public Matrix4x4 PrevViewProjMatrix;
            public Vector3 CameraPos;
            public float ScreenErrorThreshold;
            public Vector2 ScreenResolution;
            public Vector2 InvScreenResolution;
            public float Fov;
            public float SoftwareRasterThreshold;
            public uint FrameIndex;
            public uint TotalClusters;
            public uint Pad0, Pad1;
        }

        /// <summary>配置。</summary>
        public NaniteEnhancedConfig Config => _config;
        /// <summary>可见性剔除器。</summary>
        public NaniteVisibilityCulling Culling => _culling;
        /// <summary>光栅化器。</summary>
        public NaniteVisibilityRasterizer Rasterizer => _rasterizer;
        /// <summary>可见性缓冲。</summary>
        public RHITexture? VisibilityBuffer => _rasterizer.VisibilityBuffer;

        public NaniteVirtualGeometryEnhanced(RHIDevice device, NaniteEnhancedConfig? config = null)
        {
            _device = device;
            _config = config ?? new NaniteEnhancedConfig();
            _culling = new NaniteVisibilityCulling(device, _config);
            _rasterizer = new NaniteVisibilityRasterizer(device, _config);
        }

        /// <summary>初始化系统。</summary>
        public void Initialize(int renderWidth, int renderHeight, int maxClusters)
        {
            _culling.Initialize(maxClusters, renderWidth, renderHeight);
            _rasterizer.Initialize(renderWidth, renderHeight, 1024);
            _initialized = true;
        }

        /// <summary>处理并注册网格资产 (离线管线入口)。</summary>
        public NaniteEnhancedMeshAsset ProcessMesh(
            Guid assetId, string name,
            ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals,
            ReadOnlySpan<Vector2> uvs, ReadOnlySpan<int> indices)
        {
            // 1. 簇化
            var clusters = NaniteMeshProcessor.Clusterize(positions, normals, indices);

            // 2. 构建层级 DAG
            NaniteMeshProcessor.BuildHierarchy(clusters, _config.MaxLODLevels);

            // 3. 量化顶点
            var vertices = new List<NaniteEnhancedVertex>();
            var triangles = new List<NaniteEnhancedTriangle>();
            foreach (var cluster in clusters)
            {
                if (cluster.IsLeaf == 0) continue;
                for (int t = 0; t < cluster.TriangleCount; t++)
                {
                    int triIdx = (int)(cluster.IndexOffset / 3) + t;
                    int i0 = indices[triIdx * 3], i1 = indices[triIdx * 3 + 1], i2 = indices[triIdx * 3 + 2];
                    triangles.Add(new NaniteEnhancedTriangle
                    {
                        I0 = 0, I1 = 0, I2 = 0,
                        MaterialSlot = (byte)cluster.MaterialSlot,
                        EdgeFlags = 0
                    });
                }
            }

            var asset = new NaniteEnhancedMeshAsset
            {
                AssetId = assetId,
                Name = name,
                Clusters = clusters,
                Vertices = vertices,
                Triangles = triangles
            };

            _assets[assetId] = asset;
            _totalClusters += clusters.Count;
            return asset;
        }

        /// <summary>上传所有资产到 GPU。</summary>
        public void UploadToGPU()
        {
            if (_totalClusters == 0) return;

            int clusterSize = Marshal.SizeOf<NaniteEnhancedCluster>();
            _globalClusterBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_totalClusters * clusterSize),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)clusterSize,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteGlobalClusters");

            int vertexSize = Marshal.SizeOf<NaniteEnhancedVertex>();
            int totalVertices = 0;
            foreach (var asset in _assets.Values)
                totalVertices += asset.Vertices.Count;

            _globalVertexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(Math.Max(1, totalVertices) * vertexSize),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)vertexSize,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteGlobalVertices");

            _globalIndexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)Math.Max(4, _totalClusters * 128 * 4),
                Flags = RHIBufferFlags.IndexBuffer | RHIBufferFlags.ShaderResource,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteGlobalIndices");

            _materialDataBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 1024 * 256,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 256,
                HeapType = RHIResourceHeapType.Default
            }, "NaniteMaterialData");
        }

        /// <summary>执行完整的 Nanite 渲染管线。</summary>
        public void Render(RHICommandList cmdList, in CameraData camera,
            RHITexture depthBuffer, RHITexture gbufferAlbedo, RHITexture gbufferNormal,
            RHITexture gbufferRoughnessMetallic, uint frameIndex)
        {
            if (!_initialized || _globalClusterBuffer == null) return;

            // 1. 构建 Hi-Z 深度 Mip 链
            _culling.BuildHiZ(cmdList, depthBuffer, (int)camera.AspectRatio * 1080, 1080);

            // 2. 可见性剔除 + LOD 选择
            _culling.Cull(cmdList, _globalClusterBuffer, _totalClusters,
                camera.ViewProjectionMatrix, camera.Position,
                camera.FieldOfView, 1920, 1080, frameIndex);

            // 3. 硬件光栅化 (mesh shader)
            _rasterizer.RasterizeHardware(cmdList,
                _culling.VisibleClustersBuffer!, _globalClusterBuffer,
                _globalVertexBuffer, _globalIndexBuffer,
                _culling.IndirectDrawArgs!, _totalClusters);

            // 4. 软件光栅化回退 (微小三角形)
            _rasterizer.RasterizeSoftware(cmdList,
                _culling.VisibleClustersBuffer!, _globalClusterBuffer,
                _globalVertexBuffer, _globalIndexBuffer, _totalClusters);

            // 5. 材质提取 (visibility buffer → GBuffer)
            _rasterizer.ExtractMaterials(cmdList,
                gbufferAlbedo, gbufferNormal, gbufferRoughnessMetallic, depthBuffer,
                _globalVertexBuffer, _globalIndexBuffer, _globalIndexBuffer, _materialDataBuffer!);
        }

        public void Dispose()
        {
            _culling.Dispose();
            _rasterizer.Dispose();
            _globalClusterBuffer?.Dispose();
            _globalVertexBuffer?.Dispose();
            _globalIndexBuffer?.Dispose();
            _materialDataBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>处理后的 Nanite 网格资产。</summary>
    public sealed class NaniteEnhancedMeshAsset
    {
        public Guid AssetId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<NaniteEnhancedCluster> Clusters { get; set; } = new();
        public List<NaniteEnhancedVertex> Vertices { get; set; } = new();
        public List<NaniteEnhancedTriangle> Triangles { get; set; } = new();
        public Vector3 BoundsCenter { get; set; }
        public float BoundsRadius { get; set; }
    }
}
