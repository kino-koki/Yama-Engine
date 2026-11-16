using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // PathTracerBVH — 路径追踪加速结构深度验证
    // 覆盖: LBVH 构建、叶覆盖唯一性、子节点相邻布局、包围盒层级嵌套、
    //       重复质心退化、大网格深度上界、索引双区布局、
    //       多网格合并索引重映射、CPU 参考遍历 (镜像 HLSL TraceBVH) 命中语义
    // ══════════════════════════════════════════════════════════════
    public static class PathTracerBVHTests
    {
        // ── 单三角形: 1 节点叶, 命中/背面/脱靶 ──
        public static void TestBVHSingleTriangleLeaf()
        {
            var v = new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
            var i = new uint[] { 0, 1, 2 };
            var bvh = PathTracerBVHBuilder.Build(v, i);

            AssertTrue(bvh.TriangleCount == 1, "TriangleCount should be 1.");
            AssertTrue(bvh.Nodes.Length == 1, "Single triangle should produce exactly 1 leaf node.");
            AssertTrue(bvh.Nodes[0].PrimitiveCount == 1, "Root should be a leaf with 1 primitive.");
            AssertTrue(bvh.Indices.Length == 4, "Indices should be 3T + T = 4 entries.");
            AssertTrue(bvh.Indices[3] == 0, "Leaf triangle list should reference triangle 0.");

            var hit = Trace(new Vector3(0.2f, 0.2f, 5), new Vector3(0, 0, -1), v, bvh.Nodes, bvh.Indices);
            AssertTrue(hit.Hit && MathF.Abs(hit.T - 5f) < 1e-4f, "Ray above should hit the triangle at t=5.");

            var back = Trace(new Vector3(0.2f, 0.2f, -5), new Vector3(0, 0, 1), v, bvh.Nodes, bvh.Indices);
            AssertTrue(back.Hit, "Ray from below should hit (two-sided intersection).");

            var miss = Trace(new Vector3(5, 5, 5), new Vector3(0, 0, -1), v, bvh.Nodes, bvh.Indices);
            AssertTrue(!miss.Hit, "Ray outside the triangle should miss.");
        }

        // ── 网格场景: 所有三角形恰好被叶覆盖一次 + 结构不变式 ──
        public static void TestBVHCoversAllTrianglesExactlyOnce()
        {
            var (v, i) = MakeGrid(10, 10, 1f, 0f); // 200 三角形
            var bvh = PathTracerBVHBuilder.Build(v, i, maxLeafSize: 4);
            ValidateStructure(bvh, 200);
        }

        // ── 子节点相邻布局 + 包围盒层级嵌套 ──
        public static void TestBVHChildrenAdjacentAndBoundsNest()
        {
            var (v, i) = MakeGrid(8, 8, 1f, 0f);
            var bvh = PathTracerBVHBuilder.Build(v, i, maxLeafSize: 2);
            var nodes = bvh.Nodes;

            // 除根外, 每个节点恰好被引用一次, 且内部节点的子节点相邻 (右 = 左 + 1)
            var refCount = new int[nodes.Length];
            for (int n = 0; n < nodes.Length; n++)
            {
                if (nodes[n].PrimitiveCount > 0) continue;
                uint left = nodes[n].LeftOrFirst;
                AssertTrue(left + 1 < (uint)nodes.Length, $"Internal node {n}: right child out of range.");
                AssertTrue(left > (uint)n, $"Internal node {n}: children must be allocated after parent (post-order).");
                refCount[left]++;
                refCount[left + 1]++;
            }
            for (int n = 1; n < nodes.Length; n++)
                AssertTrue(refCount[n] == 1, $"Node {n} should be referenced exactly once (found {refCount[n]}).");

            // 父包围盒包含子包围盒
            for (int n = 0; n < nodes.Length; n++)
            {
                if (nodes[n].PrimitiveCount > 0) continue;
                foreach (var c in new uint[] { nodes[n].LeftOrFirst, nodes[n].LeftOrFirst + 1 })
                {
                    AssertTrue(nodes[n].BoundsMin.X <= nodes[c].BoundsMin.X + 1e-4f
                            && nodes[n].BoundsMin.Y <= nodes[c].BoundsMin.Y + 1e-4f
                            && nodes[n].BoundsMin.Z <= nodes[c].BoundsMin.Z + 1e-4f
                            && nodes[n].BoundsMax.X >= nodes[c].BoundsMax.X - 1e-4f
                            && nodes[n].BoundsMax.Y >= nodes[c].BoundsMax.Y - 1e-4f
                            && nodes[n].BoundsMax.Z >= nodes[c].BoundsMax.Z - 1e-4f,
                        $"Node {n} bounds must contain child {c} bounds.");
                }
            }
        }

        // ── 遮挡语义: 近处小三角形遮挡远处地面 (最近命中) ──
        public static void TestBVHRayTraversalClosestHit()
        {
            // 地面 (z=0, 25 顶点) + 抬高的小三角形 (z=2, 顶点索引 25-27, 位于射线正下方)
            var (floorV, floorI) = MakeGrid(4, 4, 1f, 0f);
            var v = new List<Vector3>(floorV) { new(1.4f, 1.4f, 2f), new(1.6f, 1.4f, 2f), new(1.5f, 1.6f, 2f) };
            var i = new List<uint>(floorI) { 25, 26, 27 };
            var bvh = PathTracerBVHBuilder.Build(v.ToArray(), i.ToArray());

            var occluded = Trace(new Vector3(1.5f, 1.5f, 5), new Vector3(0, 0, -1), v.ToArray(), bvh.Nodes, bvh.Indices);
            AssertTrue(occluded.Hit && MathF.Abs(occluded.T - 3f) < 1e-3f,
                $"Ray should hit the elevated triangle first (t=3), got t={occluded.T}.");

            var floor = Trace(new Vector3(0.2f, 0.2f, 5), new Vector3(0, 0, -1), v.ToArray(), bvh.Nodes, bvh.Indices);
            AssertTrue(floor.Hit && MathF.Abs(floor.T - 5f) < 1e-3f,
                $"Ray away from patch should hit the floor (t=5), got t={floor.T}.");
        }

        // ── 退化: 所有质心相同 (叠放三角形) 仍必须分裂且覆盖全部 ──
        public static void TestBVHDuplicateCentroidsStillSplits()
        {
            // 32 个同心叠放三角形 (质心全为原点)
            var v = new List<Vector3>();
            var i = new List<uint>();
            for (int k = 0; k < 32; k++)
            {
                float s = 1f + k * 0.5f;
                v.Add(new Vector3(-s, -s, 0));
                v.Add(new Vector3(2 * s, -s, 0));
                v.Add(new Vector3(0, 2 * s, 0));
                i.Add((uint)(3 * k));
                i.Add((uint)(3 * k + 1));
                i.Add((uint)(3 * k + 2));
            }
            var bvh = PathTracerBVHBuilder.Build(v.ToArray(), i.ToArray(), maxLeafSize: 2);
            ValidateStructure(bvh, 32);

            // 沿 +z 从中心射入, 应命中最小的 k=0 三角形 (最近)
            var hit = Trace(new Vector3(-0.1f, -0.1f, 10), new Vector3(0, 0, -1), v.ToArray(), bvh.Nodes, bvh.Indices);
            AssertTrue(hit.Hit, "Ray through stacked triangles should hit the nearest one.");
        }

        // ── 大网格: 深度上界 + 遍历正确性 ──
        public static void TestBVHLargeMeshDepthAndLayout()
        {
            var (v, i) = MakeGrid(100, 100, 1f, 0f); // 20000 三角形
            var bvh = PathTracerBVHBuilder.Build(v, i, maxLeafSize: 8);
            AssertTrue(bvh.TriangleCount == 20000, "Large grid should report 20000 triangles.");
            AssertTrue(bvh.MaxDepth <= 64, $"LBVH depth should stay bounded (got {bvh.MaxDepth}).");
            AssertTrue(bvh.Nodes.Length == 2 * bvh.LeafCount - 1,
                "Node count must equal 2*leaves-1 for a full binary BVH.");
            ValidateStructure(bvh, 20000);

            // 网格中心向下的射线应命中 z=0 平面
            var hit = Trace(new Vector3(50.5f, 50.5f, 100), new Vector3(0, 0, -1), v, bvh.Nodes, bvh.Indices);
            AssertTrue(hit.Hit && MathF.Abs(hit.T - 100f) < 1e-3f,
                $"Center ray should hit the grid plane at t=100, got t={hit.T}.");
        }

        // ── 索引双区布局: 顶点区原样保留 ──
        public static void TestBVHIndexLayoutPreservesVertexIndices()
        {
            var v = new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0) };
            var i = new uint[] { 2, 1, 0, 1, 3, 2 }; // 故意乱序
            var bvh = PathTracerBVHBuilder.Build(v, i);
            AssertTrue(bvh.Indices.Length == 8, "2 triangles → 3T + T = 8 entries.");
            for (int k = 0; k < 6; k++)
                AssertTrue(bvh.Indices[k] == i[k], $"Vertex-index region must preserve input order at {k}.");
            // 叶区保存三角形 ID (顺序由 Morton 排序决定, 只验证合法且不重复)
            AssertTrue(bvh.Indices[6] != bvh.Indices[7]
                && bvh.Indices[6] < 2 && bvh.Indices[7] < 2,
                "Leaf region should hold two distinct valid triangle IDs.");
        }

        // ── 多网格合并: 索引重映射 + 变换 ──
        public static void TestAccelMergeMeshesRemapsIndices()
        {
            var meshes = new Dictionary<Guid, (Vector3[], uint[])>();
            var meshA = Guid.NewGuid();
            var meshB = Guid.NewGuid();
            meshes[meshA] = (
                new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0) },
                new uint[] { 0, 1, 2, 1, 3, 2 });
            meshes[meshB] = (
                new Vector3[] { new(0, 0, 5), new(1, 0, 5), new(0, 1, 5) },
                new uint[] { 0, 1, 2 });

            var (verts, indices) = PathTracerAccelerationStructure.MergeMeshes(meshes);
            AssertTrue(verts.Length == 7, "Merged vertex count should be 4+3=7.");
            AssertTrue(indices.Length == 9, "Merged index count should be 6+3=9.");
            // 网格 B 的索引应重映射到 +4 偏移
            AssertTrue(indices[6] == 4 && indices[7] == 5 && indices[8] == 6,
                "Mesh B indices must be remapped by vertex offset.");
            // 合并后 BVH 可构建且覆盖 3 个三角形
            var bvh = PathTracerBVHBuilder.Build(verts, indices);
            AssertTrue(bvh.TriangleCount == 3, "Merged BVH should cover 3 triangles.");
            ValidateStructure(bvh, 3);
        }

        // ══════════════════════════════════════════════════════════
        // 辅助: 结构不变式校验 + CPU 参考遍历 (镜像 HLSL TraceBVH)
        // ══════════════════════════════════════════════════════════

        /// <summary>校验: 叶覆盖唯一性 / 叶区偏移合法 / 节点数 = 2*叶-1。</summary>
        private static void ValidateStructure(PathTracerBVHBuildResult bvh, int expectedTris)
        {
            var nodes = bvh.Nodes;
            int triCount = bvh.TriangleCount;
            AssertTrue(triCount == expectedTris, $"TriangleCount should be {expectedTris}.");
            AssertTrue(nodes.Length > 0, "BVH must contain at least the root node.");
            AssertTrue(bvh.Indices.Length == 4 * triCount, "Indices length must be 4T.");

            var seen = new HashSet<uint>();
            int leaves = 0;
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                AssertTrue(n >= 0 && n < nodes.Length, "Node index in range.");
                var node = nodes[n];
                if (node.PrimitiveCount > 0)
                {
                    leaves++;
                    AssertTrue(node.LeftOrFirst >= (uint)(3 * triCount),
                        $"Leaf {n}: LeftOrFirst must point into the trailing triangle-ID region.");
                    AssertTrue(node.LeftOrFirst + node.PrimitiveCount <= (uint)(4 * triCount),
                        $"Leaf {n}: triangle list out of range.");
                    for (uint k = 0; k < node.PrimitiveCount; k++)
                    {
                        uint tri = bvh.Indices[node.LeftOrFirst + k];
                        AssertTrue(tri < (uint)triCount, $"Leaf {n}: triangle id {tri} out of range.");
                        AssertTrue(seen.Add(tri), $"Triangle {tri} covered more than once.");
                    }
                }
                else
                {
                    AssertTrue(node.LeftOrFirst + 1 < (uint)nodes.Length,
                        $"Internal node {n}: children out of range.");
                    stack.Push((int)node.LeftOrFirst);
                    stack.Push((int)node.LeftOrFirst + 1);
                }
            }
            AssertTrue(leaves * 2 - 1 == nodes.Length, "Full binary tree: nodes = 2*leaves - 1.");
            AssertTrue(seen.Count == triCount, $"All {triCount} triangles must be covered exactly once (got {seen.Count}).");
        }

        /// <summary>CPU 参考遍历 — 逐行镜像 PathTracer.hlsl 的 TraceBVH。</summary>
        private static (bool Hit, float T, uint TriIndex) Trace(Vector3 origin, Vector3 dir, Vector3[] vertices,
            PathTracerBVHNode[] nodes, uint[] indices, float maxT = 1e6f)
        {
            float best = maxT;
            uint bestTri = 0;
            bool hit = false;
            var stack = new int[64];
            int sp = 0;
            stack[sp++] = 0;

            while (sp > 0)
            {
                int idx = stack[--sp];
                var node = nodes[idx];

                if (!RayAabb(origin, dir, node.BoundsMin, node.BoundsMax, out float tNear)) continue;
                if (tNear > best) continue;

                if (node.PrimitiveCount > 0)
                {
                    for (uint k = 0; k < node.PrimitiveCount; k++)
                    {
                        uint triIdx = indices[node.LeftOrFirst + k];
                        Vector3 a = vertices[indices[triIdx * 3 + 0]];
                        Vector3 b = vertices[indices[triIdx * 3 + 1]];
                        Vector3 c = vertices[indices[triIdx * 3 + 2]];
                        if (RayTri(origin, dir, a, b, c, out float t) && t < best)
                        {
                            best = t;
                            bestTri = triIdx;
                            hit = true;
                        }
                    }
                }
                else
                {
                    if (sp < 63)
                    {
                        stack[sp++] = (int)(node.LeftOrFirst + 1);
                        stack[sp++] = (int)node.LeftOrFirst;
                    }
                }
            }
            return (hit, best, bestTri);
        }

        private static bool RayAabb(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float tNear)
        {
            tNear = 0f;
            float tmin = 0f, tmax = float.MaxValue;

            if (MathF.Abs(d.X) < 1e-12f) { if (o.X < min.X || o.X > max.X) return false; }
            else { float t1 = (min.X - o.X) / d.X, t2 = (max.X - o.X) / d.X; if (t1 > t2) (t1, t2) = (t2, t1); tmin = MathF.Max(tmin, t1); tmax = MathF.Min(tmax, t2); if (tmin > tmax) return false; }
            if (MathF.Abs(d.Y) < 1e-12f) { if (o.Y < min.Y || o.Y > max.Y) return false; }
            else { float t1 = (min.Y - o.Y) / d.Y, t2 = (max.Y - o.Y) / d.Y; if (t1 > t2) (t1, t2) = (t2, t1); tmin = MathF.Max(tmin, t1); tmax = MathF.Min(tmax, t2); if (tmin > tmax) return false; }
            if (MathF.Abs(d.Z) < 1e-12f) { if (o.Z < min.Z || o.Z > max.Z) return false; }
            else { float t1 = (min.Z - o.Z) / d.Z, t2 = (max.Z - o.Z) / d.Z; if (t1 > t2) (t1, t2) = (t2, t1); tmin = MathF.Max(tmin, t1); tmax = MathF.Min(tmax, t2); if (tmin > tmax) return false; }

            tNear = tmin;
            return true;
        }

        private static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = float.MaxValue;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < -1e-6f || u > 1f + 1e-6f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < -1e-6f || u + v > 1f + 1e-6f) return false;
            float tt = Vector3.Dot(e2, q) * inv;
            if (tt < 1e-9f) return false;
            t = tt;
            return true;
        }

        /// <summary>生成 cellsX×cellsY 网格 (XY 平面, z=z): 顶点 (cx+1)(cy+1), 三角形 cx*cy*2。</summary>
        private static (Vector3[] v, uint[] i) MakeGrid(int cellsX, int cellsY, float cellSize, float z)
        {
            var v = new Vector3[(cellsX + 1) * (cellsY + 1)];
            for (int y = 0; y <= cellsY; y++)
                for (int x = 0; x <= cellsX; x++)
                    v[y * (cellsX + 1) + x] = new Vector3(x * cellSize, y * cellSize, z);

            var i = new uint[cellsX * cellsY * 6];
            int k = 0;
            for (int y = 0; y < cellsY; y++)
            {
                for (int x = 0; x < cellsX; x++)
                {
                    uint a = (uint)(y * (cellsX + 1) + x);
                    uint b = a + 1;
                    uint c = a + (uint)(cellsX + 1);
                    uint d2 = c + 1;
                    i[k++] = a; i[k++] = c; i[k++] = b;
                    i[k++] = b; i[k++] = c; i[k++] = d2;
                }
            }
            return (v, i);
        }
    }
}
