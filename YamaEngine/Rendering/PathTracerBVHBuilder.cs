using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 路径追踪 BVH 构建器 (CPU 侧)
    //
    // 为 PathTracer.hlsl 的软件 BVH 遍历 (TraceBVH) 构建 Acceleration Structure:
    //   - LBVH: 质心全局归一化 → 10bit/轴 Morton 码 (30bit) → LSD 基数排序
//   - 按最高不同位分裂; 重复质心回退中位分裂, 保证必然收敛
//   - 子节点相邻布局: 内部节点展开时连续分配两个子节点槽位
//     (左子 = LeftOrFirst, 右子 = LeftOrFirst + 1, 与 HLSL 遍历一致)
    //
    // 索引缓冲布局 (g_Indices, 共 4T 项, T = 三角形数):
    //   [0,     3T)  顶点索引 (与输入顺序一致, triIdx*3 定位)
    //   [3T,    4T)  叶节点三角形 ID 列表 (叶节点 LeftOrFirst = 3T + 区段偏移)
    //
    // 节点布局 (与 HLSL BVHNode / C# PathTracerBVHNode 一致, 32 字节):
    //   BoundsMin float3 | LeftOrFirst uint | BoundsMax float3 | PrimitiveCount uint
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>BVH 构建结果。</summary>
    public sealed class PathTracerBVHBuildResult
    {
        /// <summary>BVH 节点数组 (根节点恒为 0)。</summary>
        public PathTracerBVHNode[] Nodes = Array.Empty<PathTracerBVHNode>();
        /// <summary>索引数组: [0,3T) 顶点索引 + [3T,4T) 叶三角形 ID 列表。</summary>
        public uint[] Indices = Array.Empty<uint>();
        /// <summary>三角形总数。</summary>
        public int TriangleCount;
        /// <summary>叶节点数。</summary>
        public int LeafCount;
        /// <summary>最大树深 (根=0)。</summary>
        public int MaxDepth;
    }

    /// <summary>路径追踪 BVH 构建器 (纯 CPU, 无设备依赖, 可单元验证)。</summary>
    public static class PathTracerBVHBuilder
    {
        /// <summary>
        /// 从三角形网格构建 BVH。
        /// </summary>
        /// <param name="vertices">顶点数组。</param>
        /// <param name="indices">三角形索引 (长度必须为 3 的倍数, 最大值 &lt; 顶点数)。</param>
        /// <param name="maxLeafSize">叶节点最大三角形数 (&gt;= 1)。</param>
        public static PathTracerBVHBuildResult Build(Vector3[] vertices, uint[] indices, int maxLeafSize = 8)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (indices.Length % 3 != 0)
                throw new ArgumentException($"三角形索引长度必须为 3 的倍数: {indices.Length}");
            int triCount = indices.Length / 3;
            maxLeafSize = Math.Max(1, maxLeafSize);

            var result = new PathTracerBVHBuildResult { TriangleCount = triCount };
            if (triCount == 0)
                return result;

            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= (uint)vertices.Length)
                    throw new ArgumentException($"索引越界: indices[{i}]={indices[i]} >= 顶点数 {vertices.Length}");
            }

            // ── 每三角形质心 + AABB + 全局 AABB ──
            var centroids = new Vector3[triCount];
            var triMin = new Vector3[triCount];
            var triMax = new Vector3[triCount];
            var globalMin = new Vector3(float.MaxValue);
            var globalMax = new Vector3(float.MinValue);

            for (int t = 0; t < triCount; t++)
            {
                Vector3 a = vertices[indices[t * 3 + 0]];
                Vector3 b = vertices[indices[t * 3 + 1]];
                Vector3 c = vertices[indices[t * 3 + 2]];
                triMin[t] = Vector3.Min(a, Vector3.Min(b, c));
                triMax[t] = Vector3.Max(a, Vector3.Max(b, c));
                centroids[t] = (triMin[t] + triMax[t]) * 0.5f;
                globalMin = Vector3.Min(globalMin, triMin[t]);
                globalMax = Vector3.Max(globalMax, triMax[t]);
            }

            // ── Morton 码 + LSD 基数排序 (4 × 8bit) ──
            var extent = Vector3.Max(globalMax - globalMin, new Vector3(1e-9f));
            var codes = new uint[triCount];
            for (int t = 0; t < triCount; t++)
            {
                Vector3 n = (centroids[t] - globalMin) / extent;
                codes[t] = Morton3(n.X, n.Y, n.Z);
            }
            var triIds = new int[triCount];
            for (int t = 0; t < triCount; t++) triIds[t] = t;
            RadixSortByCode(codes, triIds);

            // ── 递归构建 (后序线性化: 子节点相邻) ──
            var nodes = new List<PathTracerBVHNode>(2 * triCount);
            var leafTriLists = new List<uint>(triCount);
            int maxDepth = 0;

            // 在 slot 处写入 [begin,end) 子树; 内部节点的两个子节点在节点数组中相邻
            // (左子 = LeftOrFirst, 右子 = LeftOrFirst + 1, 与 HLSL 遍历严格一致)
            void Emit(int slot, int begin, int end, int depth)
            {
                maxDepth = Math.Max(maxDepth, depth);
                int count = end - begin;

                // 区间包围盒
                var bMin = new Vector3(float.MaxValue);
                var bMax = new Vector3(float.MinValue);
                for (int i = begin; i < end; i++)
                {
                    int t = triIds[i];
                    bMin = Vector3.Min(bMin, triMin[t]);
                    bMax = Vector3.Max(bMax, triMax[t]);
                }

                if (count <= maxLeafSize)
                {
                    nodes[slot] = new PathTracerBVHNode
                    {
                        BoundsMin = bMin,
                        BoundsMax = bMax,
                        LeftOrFirst = (uint)(3 * triCount + leafTriLists.Count),
                        PrimitiveCount = (uint)count
                    };
                    for (int i = begin; i < end; i++)
                        leafTriLists.Add((uint)triIds[i]);
                    return;
                }

                // 分裂点: Morton 最高不同位; 退化时回退中位分裂
                int split = FindSplit(codes, begin, end);
                if (split <= begin || split >= end)
                    split = begin + count / 2;

                int leftIdx = nodes.Count; nodes.Add(default);
                int rightIdx = nodes.Count; nodes.Add(default);
                nodes[slot] = new PathTracerBVHNode
                {
                    BoundsMin = bMin,
                    BoundsMax = bMax,
                    LeftOrFirst = (uint)leftIdx,
                    PrimitiveCount = 0
                };
                Emit(leftIdx, begin, split, depth + 1);
                Emit(rightIdx, split, end, depth + 1);
            }

            nodes.Add(default); // 根节点占位 (slot 0)
            Emit(0, 0, triCount, 0);

            // ── 索引缓冲: [顶点索引 3T][叶三角形 ID 列表 T] ──
            var finalIndices = new uint[3 * triCount + leafTriLists.Count];
            for (int i = 0; i < 3 * triCount; i++)
                finalIndices[i] = indices[i];
            for (int i = 0; i < leafTriLists.Count; i++)
                finalIndices[3 * triCount + i] = leafTriLists[i];

            result.Nodes = nodes.ToArray();
            result.Indices = finalIndices;
            result.LeafCount = leafTriLists.Count > 0 || triCount > 0 ? CountLeaves(result.Nodes) : 0;
            result.MaxDepth = maxDepth;
            return result;
        }

        /// <summary>Morton 最高不同位分裂: 返回 [begin,end) 内第一个在该位为 1 的下标; 无有效分裂返回 -1。</summary>
        private static int FindSplit(uint[] codes, int begin, int end)
        {
            uint first = codes[begin], last = codes[end - 1];
            if (first == last) return -1;
            uint xored = first ^ last;
            int bit = 31 - System.Numerics.BitOperations.LeadingZeroCount(xored); // 最高不同位
            int lo = begin, hi = end; // 二分: 第一个该位为 1 的位置
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (((codes[mid] >> bit) & 1u) != 0) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        /// <summary>统计叶节点数。</summary>
        private static int CountLeaves(PathTracerBVHNode[] nodes)
        {
            int leaves = 0;
            var stack = new Stack<int>();
            if (nodes.Length > 0) stack.Push(0);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (nodes[i].PrimitiveCount > 0) leaves++;
                else
                {
                    stack.Push((int)nodes[i].LeftOrFirst);
                    stack.Push((int)nodes[i].LeftOrFirst + 1);
                }
            }
            return leaves;
        }

        /// <summary>3 轴各 10bit 量化 → 30bit Morton 码。</summary>
        private static uint Morton3(float x, float y, float z)
        {
            uint ix = Quantize(x), iy = Quantize(y), iz = Quantize(z);
            return Expand10(ix) | (Expand10(iy) << 1) | (Expand10(iz) << 2);
        }

        private static uint Quantize(float v)
        {
            v = Math.Clamp(v, 0f, 0.9999999f);
            return (uint)(v * 1024f);
        }

        /// <summary>10bit → 30bit 位扩展 (插入 2 个 0)。</summary>
        private static uint Expand10(uint v)
        {
            v = (v | (v << 16)) & 0x30000FFu;
            v = (v | (v << 8)) & 0x300F00Fu;
            v = (v | (v << 4)) & 0x30C30C3u;
            v = (v | (v << 2)) & 0x9249249u;
            return v;
        }

        /// <summary>LSD 基数排序: 按 code 升序稳定排序 triIds (4 × 8bit)。</summary>
        private static void RadixSortByCode(uint[] codes, int[] triIds)
        {
            int n = codes.Length;
            if (n < 2) return;
            var tmpCodes = new uint[n];
            var tmpIds = new int[n];
            for (int shift = 0; shift < 32; shift += 8)
            {
                int[] count = new int[257];
                for (int i = 0; i < n; i++)
                    count[((codes[i] >> shift) & 0xFF) + 1]++;
                for (int b = 0; b < 256; b++)
                    count[b + 1] += count[b];
                for (int i = 0; i < n; i++)
                {
                    int b = (int)((codes[i] >> shift) & 0xFF);
                    tmpCodes[count[b]] = codes[i];
                    tmpIds[count[b]] = triIds[i];
                    count[b]++;
                }
                (codes, tmpCodes) = (tmpCodes, codes);
                (triIds, tmpIds) = (tmpIds, triIds);
            }
            // 若经过 4 次交换后结果落在 tmp 数组 (shift=32 不会执行, 实际 4 趟后 codes/triIds 已是排好序的)
        }
    }
}
