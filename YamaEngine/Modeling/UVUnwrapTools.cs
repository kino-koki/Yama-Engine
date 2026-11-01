// =====================================================================================
//  YamaEngine.Modeling.UVUnwrapTools
//  UV 展开工具系统 (UV Unwrapping Tools)
// -------------------------------------------------------------------------------------
//  类似 Blender Smart UV Project 的自动 UV 展开:
//    - UVIsland: UV 岛 (连通的 UV 面组)
//    - UVUnwrapProcessor: 自动检测切割边 / 构建 UV 岛 / LSCM 展开 / MaxRects 装箱
//    - UVTools: UV 松弛 / 缝合 / 钉住 / 投影 (平面/球面/圆柱)
//    - UVQualityMetrics: 拉伸度量 / 扭曲度量
//
//  与已有 UVUnwrapper.cs 中的 UVUnwrapper / MeshChart / UvProjectionAxis 共存,
//  使用 UVUnwrapProcessor / UVIsland / UVProjectionType 等名称。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.Modeling
{
    // =================================================================================
    //  UVIsland — UV 岛
    // =================================================================================

    /// <summary>
    /// UV 岛 —— 一个连通的 UV 面组, 展开后作为一个整体进行装箱排列。
    /// </summary>
    public class UVIsland
    {
        /// <summary>岛中包含的面索引列表。</summary>
        public List<int> FaceIndices = new();

        /// <summary>岛中包含的顶点索引集合。</summary>
        public HashSet<int> Vertices = new();

        /// <summary>岛中所有面的 UV 坐标 (展开后)。</summary>
        public List<Vector2> UVs = new();

        /// <summary>顶点索引到 UV 列表中的索引映射。</summary>
        public Dictionary<int, int> VertexToUVIndex = new();

        /// <summary>岛在 UV 空间的包围盒。</summary>
        public Vector2 Min = new(float.MaxValue);
        public Vector2 Max = new(float.MinValue);

        /// <summary>是否旋转 90 度 (装箱时使用)。</summary>
        public bool Rotated = false;

        /// <summary>添加顶点到岛中。</summary>
        public void AddVertex(int vertexIndex)
        {
            if (Vertices.Add(vertexIndex))
            {
                VertexToUVIndex[vertexIndex] = UVs.Count;
                UVs.Add(Vector2.Zero);
            }
        }

        /// <summary>设置顶点的 UV 坐标。</summary>
        public void SetUV(int vertexIndex, Vector2 uv)
        {
            if (!VertexToUVIndex.TryGetValue(vertexIndex, out int uvIdx))
            {
                AddVertex(vertexIndex);
                uvIdx = UVs.Count - 1;
            }
            UVs[uvIdx] = uv;
            Min = Vector2.Min(Min, uv);
            Max = Vector2.Max(Max, uv);
        }

        /// <summary>获取顶点的 UV 坐标。</summary>
        public Vector2 GetUV(int vertexIndex)
        {
            return VertexToUVIndex.TryGetValue(vertexIndex, out int uvIdx)
                ? UVs[uvIdx]
                : Vector2.Zero;
        }

        /// <summary>岛在 UV 空间的宽。</summary>
        public float Width => Max.X - Min.X;

        /// <summary>岛在 UV 空间的高。</summary>
        public float Height => Max.Y - Min.Y;

        /// <summary>岛在 UV 空间的面积。</summary>
        public float Area => Width * Height;

        /// <summary>将岛的 UV 平移, 使 Min 为 (0,0)。</summary>
        public void NormalizeToLocal()
        {
            for (int i = 0; i < UVs.Count; i++)
                UVs[i] -= Min;
            Max -= Min;
            Min = Vector2.Zero;
        }

        /// <summary>将岛的 UV 平移到指定位置。</summary>
        public void Translate(Vector2 offset)
        {
            for (int i = 0; i < UVs.Count; i++)
                UVs[i] += offset;
            Min += offset;
            Max += offset;
        }

        /// <summary>旋转 UV 90 度。</summary>
        public void Rotate90()
        {
            for (int i = 0; i < UVs.Count; i++)
            {
                var uv = UVs[i] - Min;
                UVs[i] = new Vector2(Height - uv.Y, uv.X) + Min;
            }
            var oldMin = Min;
            Min = new Vector2(oldMin.X, oldMin.Y);
            Max = Min + new Vector2(Height, Width);
            Rotated = !Rotated;
        }
    }

    // =================================================================================
    //  UVProjectionType — UV 投影类型
    // =================================================================================

    /// <summary>UV 投影类型。</summary>
    public enum UVProjectionType
    {
        Planar,
        Spherical,
        Cylindrical
    }

    // =================================================================================
    //  UVUnwrapProcessor — UV 展开处理器
    // =================================================================================

    /// <summary>
    /// UV 展开处理器 —— 自动 UV 展开管线。
    /// 1. DetectSeams: 检测切割边 (锐角边 + 边界边)
    /// 2. BuildIslands: 从切割边构建 UV 岛 (FloodFill 连通分量)
    /// 3. FlattenIsland: LSCM 展开单个 UV 岛到 2D
    /// 4. PackIslands: MaxRects 装箱排列 UV 岛
    /// 5. Normalize: 归一化 UV 到 [0,1] 范围
    /// </summary>
    public class UVUnwrapProcessor
    {
        /// <summary>切割边的角度阈值 (度)。</summary>
        public float AngleThreshold = 60f;

        /// <summary>装箱时的 UV 间距。</summary>
        public float PackingMargin = 0.01f;

        /// <summary>允许 90 度旋转。</summary>
        public bool AllowRotation = true;

        // ──────────────────────────────────────────────────────────────
        //  DetectSeams — 检测切割边
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 检测切割边: 锐角边 (面间夹角 > threshold) + 边界边。
        /// 返回切割边的顶点对集合。
        /// </summary>
        public HashSet<(int, int)> DetectSeams(List<Vector3> vertices, List<(int A, int B, int C)> triangles)
        {
            var seams = new HashSet<(int, int)>();
            var edgeFaces = BuildEdgeFaceMap(triangles);

            foreach (var kvp in edgeFaces)
            {
                var (v0, v1) = kvp.Key;
                var faces = kvp.Value;

                if (faces.Count == 1)
                {
                    // 边界边 — 切割
                    seams.Add((v0, v1));
                }
                else if (faces.Count == 2)
                {
                    // 检查面间夹角
                    float angle = ComputeDihedralAngle(vertices, triangles, faces[0], faces[1], v0, v1);
                    if (angle > AngleThreshold * MathF.PI / 180f)
                        seams.Add((v0, v1));
                }
                else
                {
                    // 非流形边 — 切割
                    seams.Add((v0, v1));
                }
            }

            return seams;
        }

        // ──────────────────────────────────────────────────────────────
        //  BuildIslands — 构建 UV 岛
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 从切割边构建 UV 岛 (FloodFill 连通分量)。
        /// 不跨越切割边的连通面组成一个 UV 岛。
        /// </summary>
        public List<UVIsland> BuildIslands(List<Vector3> vertices, List<(int A, int B, int C)> triangles, HashSet<(int, int)> seams)
        {
            var islands = new List<UVIsland>();
            var visited = new bool[triangles.Count];

            // 构建面邻接表 (不跨越切割边)
            var faceAdjacency = BuildFaceAdjacency(triangles, seams);

            for (int startFace = 0; startFace < triangles.Count; startFace++)
            {
                if (visited[startFace]) continue;

                var island = new UVIsland();
                var queue = new Queue<int>();
                queue.Enqueue(startFace);
                visited[startFace] = true;

                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue();
                    var (a, b, c) = triangles[fi];
                    island.FaceIndices.Add(fi);
                    island.AddVertex(a);
                    island.AddVertex(b);
                    island.AddVertex(c);

                    if (faceAdjacency.TryGetValue(fi, out var neighbors))
                    {
                        foreach (var nb in neighbors)
                        {
                            if (!visited[nb])
                            {
                                visited[nb] = true;
                                queue.Enqueue(nb);
                            }
                        }
                    }
                }

                if (island.FaceIndices.Count > 0)
                    islands.Add(island);
            }

            return islands;
        }

        // ──────────────────────────────────────────────────────────────
        //  FlattenIsland — LSCM 展开单个 UV 岛
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 使用 LSCM (Least Squares Conformal Maps) 算法展开单个 UV 岛到 2D。
        /// 1. 固定两个锚点顶点 (岛上距离最远的两个顶点)
        /// 2. 构建矩阵 A 和 b, 最小二乘求解 UV 坐标
        /// 3. 对每个三角形计算局部 2D 坐标
        /// 4. 求解 A^T * A * x = A^T * b (正则方程)
        /// </summary>
        public void FlattenIsland(UVIsland island, List<Vector3> vertices, List<(int A, int B, int C)> triangles)
        {
            if (island.FaceIndices.Count == 0 || island.Vertices.Count < 3) return;

            // 收集岛中所有顶点
            var vertList = island.Vertices.ToList();
            int nVerts = vertList.Count;
            var vertIndexMap = new Dictionary<int, int>();
            for (int i = 0; i < nVerts; i++)
                vertIndexMap[vertList[i]] = i;

            // 收集岛中所有三角形
            var islandTris = new List<(int, int, int)>();
            foreach (var fi in island.FaceIndices)
            {
                var (a, b, c) = triangles[fi];
                islandTris.Add((vertIndexMap[a], vertIndexMap[b], vertIndexMap[c]));
            }
            int nTris = islandTris.Count;

            if (nVerts < 3 || nTris == 0) return;

            // 1. 为每个三角形计算局部 2D 坐标
            var localCoords = new List<(float x0, float y0, float x1, float y1, float x2, float y2)>();
            foreach (var (i, j, k) in islandTris)
            {
                var lp0 = vertices[vertList[i]];
                var lp1 = vertices[vertList[j]];
                var lp2 = vertices[vertList[k]];

                // 以 lp0 为原点, lp0->lp1 为 x 轴
                var e1 = lp1 - lp0;
                var e2 = lp2 - lp0;
                float len1 = e1.Length();
                if (len1 < 1e-8f) len1 = 1e-8f;

                var e1n = e1 / len1;
                float x1 = len1;
                float y1 = 0f;
                float x2 = Vector3.Dot(e2, e1n);
                var cross = Vector3.Cross(e1, e2);
                float y2 = cross.Length() / len1;

                // 确保 y2 > 0 (保持方向一致)
                if (Vector3.Dot(cross, Vector3.Cross(e1, Vector3.UnitY)) < 0 && y2 > 0)
                    y2 = -y2;

                localCoords.Add((0f, 0f, x1, y1, x2, y2));
            }

            // 2. 选择两个锚点 (距离最远的顶点对)
            int anchor0 = 0, anchor1 = 1;
            float maxDist = 0f;
            for (int i = 0; i < nVerts; i++)
            {
                for (int j = i + 1; j < nVerts; j++)
                {
                    float dist = Vector3.DistanceSquared(vertices[vertList[i]], vertices[vertList[j]]);
                    if (dist > maxDist)
                    {
                        maxDist = dist;
                        anchor0 = i;
                        anchor1 = j;
                    }
                }
            }

            // 锚点 UV 固定值
            var p0 = vertices[vertList[anchor0]];
            var p1 = vertices[vertList[anchor1]];
            float anchorDist = Vector3.Distance(p0, p1);
            if (anchorDist < 1e-8f) anchorDist = 1f;
            Vector2 anchorUV0 = new(0f, 0f);
            Vector2 anchorUV1 = new(anchorDist, 0f);

            // 3. 构建 LSCM 矩阵系统
            // 对每个三角形 t, 顶点 (i, j, k):
            //   W_i = x_j - x_k, W_j = x_k - x_i, W_k = x_i - x_j
            //   S_i = y_j - y_k, S_j = y_k - y_i, S_k = y_i - y_j
            //   约束: W_i*u_i + W_j*u_j + W_k*u_k + S_i*v_i + S_j*v_j + S_k*v_k = 0
            //          W_i*v_i + W_j*v_j + W_k*v_k - S_i*u_i - S_j*u_j - S_k*u_k = 0

            // 自由顶点 (非锚点)
            var freeVerts = new List<int>();
            var freeVertIndex = new int[nVerts];
            for (int i = 0; i < nVerts; i++)
            {
                if (i == anchor0 || i == anchor1)
                    freeVertIndex[i] = -1;
                else
                {
                    freeVertIndex[i] = freeVerts.Count;
                    freeVerts.Add(i);
                }
            }
            int nFree = freeVerts.Count;

            if (nFree == 0) return;

            // 构建 A^T * A 和 A^T * b (正则方程)
            // 未知量: [u_0, u_1, ..., u_{nFree-1}, v_0, v_1, ..., v_{nFree-1}]
            // 矩阵大小: 2*nFree x 2*nFree
            int matSize = 2 * nFree;
            var AtA = new float[matSize, matSize];
            var Atb = new float[matSize];

            foreach (var tri in islandTris)
            {
                var (ti, tj, tk) = tri;
                var (x0, y0, x1, y1, x2, y2) = localCoords[islandTris.IndexOf(tri)];

                float Wi = x1 - x2, Wj = x2 - x0, Wk = x0 - x1;
                float Si = y1 - y2, Sj = y2 - y0, Sk = y0 - y1;

                int[] triVerts = { ti, tj, tk };
                float[] wVals = { Wi, Wj, Wk };
                float[] sVals = { Si, Sj, Sk };

                for (int row = 0; row < 2; row++)
                {
                    for (int vi = 0; vi < 3; vi++)
                    {
                        int globalVert = triVerts[vi];
                        int freeIdx = freeVertIndex[globalVert];
                        if (freeIdx < 0) continue;

                        float wCoef = wVals[vi];
                        float sCoef = sVals[vi];

                        // Row 0: W*u + S*v
                        // Row 1: -S*u + W*v
                        float aCoef = row == 0 ? wCoef : -sCoef;
                        float bCoef = row == 0 ? sCoef : wCoef;

                        // A^T * A
                        for (int vj = 0; vj < 3; vj++)
                        {
                            int globalVertJ = triVerts[vj];
                            int freeIdxJ = freeVertIndex[globalVertJ];
                            if (freeIdxJ < 0) continue;

                            float aCoefJ = row == 0 ? wVals[vj] : -sVals[vj];
                            float bCoefJ = row == 0 ? sVals[vj] : wVals[vj];

                            AtA[freeIdx, freeIdxJ] += aCoef * aCoefJ;
                            AtA[freeIdx, freeIdxJ + nFree] += aCoef * bCoefJ;
                            AtA[freeIdx + nFree, freeIdxJ] += bCoef * aCoefJ;
                            AtA[freeIdx + nFree, freeIdxJ + nFree] += bCoef * bCoefJ;
                        }

                        // A^T * b (来自锚点贡献)
                        for (int vj = 0; vj < 3; vj++)
                        {
                            int globalVertJ = triVerts[vj];
                            if (globalVertJ == anchor0)
                            {
                                float aCoefJ = row == 0 ? wVals[vj] : -sVals[vj];
                                float bCoefJ = row == 0 ? sVals[vj] : wVals[vj];
                                Atb[freeIdx] -= aCoefJ * anchorUV0.X;
                                Atb[freeIdx + nFree] -= bCoefJ * anchorUV0.Y;
                            }
                            else if (globalVertJ == anchor1)
                            {
                                float aCoefJ = row == 0 ? wVals[vj] : -sVals[vj];
                                float bCoefJ = row == 0 ? sVals[vj] : wVals[vj];
                                Atb[freeIdx] -= aCoefJ * anchorUV1.X;
                                Atb[freeIdx + nFree] -= bCoefJ * anchorUV1.Y;
                            }
                        }
                    }
                }
            }

            // 4. 求解 AtA * x = Atb (高斯消元)
            var solution = SolveLinearSystem(AtA, Atb);
            if (solution == null) return;

            // 5. 写回 UV 坐标
            island.SetUV(vertList[anchor0], anchorUV0);
            island.SetUV(vertList[anchor1], anchorUV1);
            for (int i = 0; i < nFree; i++)
            {
                var uv = new Vector2(solution[i], solution[i + nFree]);
                island.SetUV(vertList[freeVerts[i]], uv);
            }

            // 6. 归一化 UV 到 [0, 1]
            NormalizeIslandUVs(island);
        }

        // ──────────────────────────────────────────────────────────────
        //  PackIslands — MaxRects 装箱排列
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 使用 MaxRects 算法将多个 UV 岛装箱排列到 UV 空间。
        /// 支持 90 度旋转优化。
        /// </summary>
        public void PackIslands(List<UVIsland> islands, float uvArea = 1f)
        {
            // 按面积从大到小排序
            var sorted = islands.OrderByDescending(i => i.Area).ToList();

            // 初始化 MaxRects 装箱器
            var packer = new MaxRectsPacker(uvArea, uvArea, PackingMargin);

            foreach (var island in sorted)
            {
                island.NormalizeToLocal();

                float w = island.Width + PackingMargin;
                float h = island.Height + PackingMargin;

                // 尝试不旋转
                var pos = packer.Insert(w, h);
                bool rotated = false;

                // 尝试旋转 (如果允许且更好)
                if (pos == null && AllowRotation)
                {
                    pos = packer.Insert(h, w);
                    rotated = true;
                }

                if (pos == null)
                {
                    // 装不下, 扩展空间 (或跳过)
                    island.Translate(new Vector2(PackingMargin, PackingMargin));
                    continue;
                }

                if (rotated)
                    island.Rotate90();

                island.Translate(pos.Value + new Vector2(PackingMargin * 0.5f, PackingMargin * 0.5f));
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Normalize — 归一化 UV
        // ──────────────────────────────────────────────────────────────

        /// <summary>归一化单个岛的 UV 到 [0, 1] 范围。</summary>
        public void NormalizeIslandUVs(UVIsland island)
        {
            if (island.UVs.Count == 0) return;

            island.Min = new Vector2(float.MaxValue);
            island.Max = new Vector2(float.MinValue);
            foreach (var uv in island.UVs)
            {
                island.Min = Vector2.Min(island.Min, uv);
                island.Max = Vector2.Max(island.Max, uv);
            }

            var size = island.Max - island.Min;
            float maxDim = Math.Max(size.X, size.Y);
            if (maxDim < 1e-8f) maxDim = 1f;

            for (int i = 0; i < island.UVs.Count; i++)
                island.UVs[i] = (island.UVs[i] - island.Min) / maxDim;

            island.Min = Vector2.Zero;
            island.Max = new Vector2(size.X / maxDim, size.Y / maxDim);
        }

        /// <summary>归一化所有岛的 UV 到 [0, 1] 范围。</summary>
        public void NormalizeAllIslands(List<UVIsland> islands)
        {
            foreach (var island in islands)
                NormalizeIslandUVs(island);
        }

        // ──────────────────────────────────────────────────────────────
        //  完整展开管线
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 完整自动 UV 展开管线: 检测切割边 → 构建岛 → LSCM 展开 → 装箱 → 归一化。
        /// 返回展开后的 UV 坐标列表 (与 vertices 一一对应)。
        /// </summary>
        public List<Vector2> Unwrap(List<Vector3> vertices, List<(int A, int B, int C)> triangles)
        {
            var seams = DetectSeams(vertices, triangles);
            var islands = BuildIslands(vertices, triangles, seams);

            foreach (var island in islands)
                FlattenIsland(island, vertices, triangles);

            PackIslands(islands);

            // 将岛 UV 写回全局 UV 数组
            var uvs = new List<Vector2>(vertices.Count);
            for (int i = 0; i < vertices.Count; i++)
                uvs.Add(Vector2.Zero);

            foreach (var island in islands)
            {
                foreach (var kvp in island.VertexToUVIndex)
                {
                    int vertIdx = kvp.Key;
                    int uvIdx = kvp.Value;
                    if (vertIdx >= 0 && vertIdx < uvs.Count)
                        uvs[vertIdx] = island.UVs[uvIdx];
                }
            }

            return uvs;
        }

        // ──────────────────────────────────────────────────────────────
        //  辅助方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>构建边-面邻接表。</summary>
        private static Dictionary<(int, int), List<int>> BuildEdgeFaceMap(List<(int A, int B, int C)> triangles)
        {
            var map = new Dictionary<(int, int), List<int>>();
            for (int fi = 0; fi < triangles.Count; fi++)
            {
                var (a, b, c) = triangles[fi];
                var edges = new[] { (a, b), (b, c), (c, a) };
                foreach (var (v0, v1) in edges)
                {
                    var key = v0 < v1 ? (v0, v1) : (v1, v0);
                    if (!map.ContainsKey(key)) map[key] = new List<int>();
                    map[key].Add(fi);
                }
            }
            return map;
        }

        /// <summary>计算两个面之间的二面角 (弧度)。</summary>
        private static float ComputeDihedralAngle(List<Vector3> vertices, List<(int A, int B, int C)> triangles, int f0, int f1, int v0, int v1)
        {
            var n0 = ComputeTriangleNormal(vertices, triangles[f0]);
            var n1 = ComputeTriangleNormal(vertices, triangles[f1]);
            float dot = Math.Clamp(Vector3.Dot(n0, n1), -1f, 1f);
            return MathF.Acos(dot);
        }

        private static Vector3 ComputeTriangleNormal(List<Vector3> vertices, (int A, int B, int C) tri)
        {
            var (a, b, c) = tri;
            var normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            return normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
        }

        /// <summary>构建面邻接表 (不跨越切割边)。</summary>
        private static Dictionary<int, List<int>> BuildFaceAdjacency(List<(int A, int B, int C)> triangles, HashSet<(int, int)> seams)
        {
            var edgeFaces = BuildEdgeFaceMap(triangles);
            var adjacency = new Dictionary<int, List<int>>();

            foreach (var kvp in edgeFaces)
            {
                var (v0, v1) = kvp.Key;
                var key = v0 < v1 ? (v0, v1) : (v1, v0);
                if (seams.Contains(key)) continue; // 跨越切割边, 不连接

                var faces = kvp.Value;
                if (faces.Count == 2)
                {
                    if (!adjacency.ContainsKey(faces[0])) adjacency[faces[0]] = new List<int>();
                    if (!adjacency.ContainsKey(faces[1])) adjacency[faces[1]] = new List<int>();
                    adjacency[faces[0]].Add(faces[1]);
                    adjacency[faces[1]].Add(faces[0]);
                }
            }

            return adjacency;
        }

        /// <summary>使用高斯消元法求解线性方程组 Ax = b。</summary>
        private static float[] SolveLinearSystem(float[,] A, float[] b)
        {
            int n = b.Length;
            if (n == 0) return Array.Empty<float>();

            // 创建增广矩阵 [A|b]
            var aug = new float[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                    aug[i, j] = A[i, j];
                aug[i, n] = b[i];
            }

            // 前向消元 (带部分主元选择)
            for (int col = 0; col < n; col++)
            {
                // 找到当前列的最大主元
                int maxRow = col;
                float maxVal = Math.Abs(aug[col, col]);
                for (int row = col + 1; row < n; row++)
                {
                    if (Math.Abs(aug[row, col]) > maxVal)
                    {
                        maxVal = Math.Abs(aug[row, col]);
                        maxRow = row;
                    }
                }

                if (maxVal < 1e-12f) continue; // 奇异矩阵, 跳过

                // 交换行
                if (maxRow != col)
                {
                    for (int j = 0; j <= n; j++)
                        (aug[col, j], aug[maxRow, j]) = (aug[maxRow, j], aug[col, j]);
                }

                // 消元
                for (int row = 0; row < n; row++)
                {
                    if (row == col) continue;
                    float factor = aug[row, col] / aug[col, col];
                    for (int j = col; j <= n; j++)
                        aug[row, j] -= factor * aug[col, j];
                }
            }

            // 回代
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(aug[i, i]) > 1e-12f)
                    x[i] = aug[i, n] / aug[i, i];
                else
                    x[i] = 0f;
            }

            return x;
        }
    }

    // =================================================================================
    //  MaxRectsPacker — MaxRects 装箱算法
    // =================================================================================

    /// <summary>
    /// MaxRects 装箱算法 —— 用于 UV 岛的矩形装箱排列。
    /// 维护一组 "最大矩形" (自由空间), 每次插入时选择最佳位置并分割剩余空间。
    /// </summary>
    public class MaxRectsPacker
    {
        private readonly float _binWidth;
        private readonly float _binHeight;
        private readonly float _margin;
        private readonly List<Rect> _freeRects = new();

        public MaxRectsPacker(float width, float height, float margin = 0f)
        {
            _binWidth = width;
            _binHeight = height;
            _margin = margin;
            _freeRects.Add(new Rect(margin, margin, width - margin, height - margin));
        }

        /// <summary>尝试插入一个矩形, 返回左下角位置 (成功) 或 null (失败)。</summary>
        public Vector2? Insert(float width, float height)
        {
            Rect? bestRect = null;
            float bestScore = float.MaxValue;
            Vector2 bestPos = Vector2.Zero;

            foreach (var free in _freeRects)
            {
                if (free.Width >= width && free.Height >= height)
                {
                    // BSSF (Best Short Side Fit) 启发式
                    float leftover = Math.Min(free.Width - width, free.Height - height);
                    if (leftover < bestScore)
                    {
                        bestScore = leftover;
                        bestRect = free;
                        bestPos = new Vector2(free.X0, free.Y0);
                    }
                }
            }

            if (bestRect == null) return null;

            // 放置矩形并分割剩余空间
            PlaceRect(bestPos, width, height);
            return bestPos;
        }

        private void PlaceRect(Vector2 pos, float width, float height)
        {
            var placed = new Rect(pos.X, pos.Y, pos.X + width, pos.Y + height);
            var newFreeRects = new List<Rect>();

            foreach (var free in _freeRects)
            {
                if (!free.Intersects(placed))
                {
                    newFreeRects.Add(free);
                    continue;
                }

                // 分割自由矩形
                if (placed.X0 > free.X0)
                    newFreeRects.Add(new Rect(free.X0, free.Y0, placed.X0, free.Y1));
                if (placed.X1 < free.X1)
                    newFreeRects.Add(new Rect(placed.X1, free.Y0, free.X1, free.Y1));
                if (placed.Y0 > free.Y0)
                    newFreeRects.Add(new Rect(free.X0, free.Y0, free.X1, placed.Y0));
                if (placed.Y1 < free.Y1)
                    newFreeRects.Add(new Rect(free.X0, placed.Y1, free.X1, free.Y1));
            }

            // 去除被包含的矩形
            _freeRects.Clear();
            for (int i = 0; i < newFreeRects.Count; i++)
            {
                bool contained = false;
                for (int j = 0; j < newFreeRects.Count; j++)
                {
                    if (i == j) continue;
                    if (newFreeRects[j].Contains(newFreeRects[i]))
                    {
                        contained = true;
                        break;
                    }
                }
                if (!contained)
                    _freeRects.Add(newFreeRects[i]);
            }
        }

        private struct Rect
        {
            public float X0, Y0, X1, Y1;
            public float Width => X1 - X0;
            public float Height => Y1 - Y0;

            public Rect(float x0, float y0, float x1, float y1)
            {
                X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
            }

            public bool Intersects(in Rect other)
            {
                return !(X1 <= other.X0 || X0 >= other.X1 || Y1 <= other.Y0 || Y0 >= other.Y1);
            }

            public bool Contains(in Rect other)
            {
                return X0 <= other.X0 && X1 >= other.X1 && Y0 <= other.Y0 && Y1 >= other.Y1;
            }
        }
    }

    // =================================================================================
    //  UVTools — UV 工具
    // =================================================================================

    /// <summary>
    /// UV 工具集 —— UV 松弛 / 缝合 / 钉住 / 投影。
    /// </summary>
    public static class UVTools
    {
        // ──────────────────────────────────────────────────────────────
        //  RelaxUV — 松弛 UV
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 松弛 UV: 迭代减少 UV 扭曲 (Laplacian 平滑在 UV 空间)。
        /// pinned 顶点不参与松弛。
        /// </summary>
        public static void RelaxUV(List<Vector2> uvs, List<(int A, int B, int C)> triangles,
            HashSet<int> pinned, int iterations = 10, float lambda = 0.5f)
        {
            // 构建顶点邻接
            var adjacency = new HashSet<int>[uvs.Count];
            for (int i = 0; i < uvs.Count; i++) adjacency[i] = new HashSet<int>();
            foreach (var (a, b, c) in triangles)
            {
                adjacency[a].Add(b); adjacency[b].Add(a);
                adjacency[b].Add(c); adjacency[c].Add(b);
                adjacency[c].Add(a); adjacency[a].Add(c);
            }

            for (int iter = 0; iter < iterations; iter++)
            {
                var newUVs = new Vector2[uvs.Count];
                for (int i = 0; i < uvs.Count; i++)
                {
                    if (pinned.Contains(i) || adjacency[i].Count == 0)
                    {
                        newUVs[i] = uvs[i];
                        continue;
                    }

                    Vector2 sum = Vector2.Zero;
                    foreach (var nb in adjacency[i])
                        sum += uvs[nb];
                    var avg = sum / adjacency[i].Count;
                    newUVs[i] = Vector3.Lerp(
                        new Vector3(uvs[i], 0),
                        new Vector3(avg, 0),
                        lambda).ToVector2();
                }

                for (int i = 0; i < uvs.Count; i++)
                    uvs[i] = newUVs[i];
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  SewUV — 缝合 UV 边
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 缝合选中 UV 边: 将两条 UV 边的顶点合并到中点。
        /// edges: 要缝合的 UV 边列表 (每条边由两个顶点索引对组成)。
        /// </summary>
        public static void SewUV(List<Vector2> uvs, List<((int V0, int V1), (int V0, int V1))> edges)
        {
            foreach (var (edge1, edge2) in edges)
            {
                // 将 edge2 的顶点 UV 移到 edge1 的中点
                var mid1 = (uvs[edge1.V0] + uvs[edge1.V1]) * 0.5f;
                uvs[edge2.V0] = mid1;
                uvs[edge2.V1] = mid1;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  PinUV — 钉住 UV 顶点
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 标记顶点为钉住状态 (用于 RelaxUV 时保持不动)。
        /// </summary>
        public static HashSet<int> PinUV(HashSet<int> existing, IEnumerable<int> toPin)
        {
            var result = new HashSet<int>(existing);
            foreach (var v in toPin)
                result.Add(v);
            return result;
        }

        /// <summary>取消钉住顶点。</summary>
        public static HashSet<int> UnpinUV(HashSet<int> existing, IEnumerable<int> toUnpin)
        {
            var result = new HashSet<int>(existing);
            foreach (var v in toUnpin)
                result.Remove(v);
            return result;
        }

        // ──────────────────────────────────────────────────────────────
        //  ProjectUV — UV 投影
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// UV 投影: 将 3D 顶点投影到平面 / 球面 / 圆柱面生成 UV。
        /// </summary>
        public static List<Vector2> ProjectUV(List<Vector3> positions, List<(int A, int B, int C)> triangles,
            UVProjectionType type, Vector3 projectionAxis = default)
        {
            if (projectionAxis == default) projectionAxis = Vector3.UnitY;

            var uvs = new List<Vector2>(positions.Count);
            if (positions.Count == 0) return uvs;

            switch (type)
            {
                case UVProjectionType.Planar:
                    uvs = ProjectPlanar(positions, projectionAxis);
                    break;
                case UVProjectionType.Spherical:
                    uvs = ProjectSpherical(positions);
                    break;
                case UVProjectionType.Cylindrical:
                    uvs = ProjectCylindrical(positions, projectionAxis);
                    break;
            }

            return uvs;
        }

        /// <summary>平面投影: 将顶点投影到垂直于给定轴的平面。</summary>
        private static List<Vector2> ProjectPlanar(List<Vector3> positions, Vector3 axis)
        {
            var uvs = new List<Vector2>(positions.Count);
            axis = Vector3.Normalize(axis);

            // 选择两个垂直于 axis 的方向
            var up = Math.Abs(axis.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var right = Vector3.Normalize(Vector3.Cross(up, axis));
            up = Vector3.Cross(axis, right);

            foreach (var p in positions)
                uvs.Add(new Vector2(Vector3.Dot(p, right), Vector3.Dot(p, up)));

            return NormalizeUVs(uvs);
        }

        /// <summary>球面投影: 使用球面坐标 (theta, phi) 作为 UV。</summary>
        private static List<Vector2> ProjectSpherical(List<Vector3> positions)
        {
            var uvs = new List<Vector2>(positions.Count);

            // 计算中心点
            var center = Vector3.Zero;
            foreach (var p in positions) center += p;
            center /= positions.Count;

            foreach (var p in positions)
            {
                var d = p - center;
                float r = d.Length();
                if (r < 1e-8f) { uvs.Add(Vector2.Zero); continue; }

                float theta = MathF.Atan2(d.Z, d.X) / (2f * MathF.PI) + 0.5f;
                float phi = MathF.Acos(Math.Clamp(d.Y / r, -1f, 1f)) / MathF.PI;
                uvs.Add(new Vector2(theta, phi));
            }

            return uvs;
        }

        /// <summary>圆柱投影: 使用圆柱坐标 (角度, 高度) 作为 UV。</summary>
        private static List<Vector2> ProjectCylindrical(List<Vector3> positions, Vector3 axis)
        {
            var uvs = new List<Vector2>(positions.Count);
            axis = Vector3.Normalize(axis);

            // 计算高度范围
            float minHeight = float.MaxValue, maxHeight = float.MinValue;
            var projections = new List<(float angle, float height)>(positions.Count);

            foreach (var p in positions)
            {
                float height = Vector3.Dot(p, axis);
                var proj = p - axis * height;
                float angle = MathF.Atan2(proj.Z, proj.X);
                projections.Add((angle, height));
                minHeight = Math.Min(minHeight, height);
                maxHeight = Math.Max(maxHeight, height);
            }

            float heightRange = maxHeight - minHeight;
            if (heightRange < 1e-8f) heightRange = 1f;

            foreach (var (angle, height) in projections)
            {
                float u = angle / (2f * MathF.PI) + 0.5f;
                float v = (height - minHeight) / heightRange;
                uvs.Add(new Vector2(u, v));
            }

            return uvs;
        }

        /// <summary>归一化 UV 到 [0, 1] 范围。</summary>
        private static List<Vector2> NormalizeUVs(List<Vector2> uvs)
        {
            if (uvs.Count == 0) return uvs;

            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var uv in uvs)
            {
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }

            var size = max - min;
            float maxDim = Math.Max(size.X, size.Y);
            if (maxDim < 1e-8f) maxDim = 1f;

            for (int i = 0; i < uvs.Count; i++)
                uvs[i] = (uvs[i] - min) / maxDim;

            return uvs;
        }
    }

    // =================================================================================
    //  UVQualityMetrics — UV 质量度量
    // =================================================================================

    /// <summary>
    /// UV 质量度量 —— 衡量 UV 展开的质量。
    /// </summary>
    public struct UVQualityMetrics
    {
        /// <summary>拉伸度量: 3D 面积 / UV 面积 的比值 (越接近 1 越好)。</summary>
        public float Stretch;

        /// <summary>最大拉伸。</summary>
        public float MaxStretch;

        /// <summary>扭曲度量: 3D 角度 / UV 角度 的比值 (越接近 1 越好)。</summary>
        public float Distortion;

        /// <summary>最大扭曲。</summary>
        public float MaxDistortion;

        /// <summary>平均 3D 三角形面积。</summary>
        public float Avg3DArea;

        /// <summary>平均 UV 三角形面积。</summary>
        public float AvgUVArea;

        public override string ToString()
        {
            return $"Stretch: {Stretch:F3} (max: {MaxStretch:F3}), " +
                   $"Distortion: {Distortion:F3} (max: {MaxDistortion:F3})";
        }

        /// <summary>
        /// 计算 UV 质量度量。
        /// </summary>
        public static UVQualityMetrics Compute(List<Vector3> positions, List<Vector2> uvs, List<(int A, int B, int C)> triangles)
        {
            var metrics = new UVQualityMetrics();
            if (triangles.Count == 0) return metrics;

            float totalStretch = 0f;
            float maxStretch = 0f;
            float totalDistortion = 0f;
            float maxDistortion = 0f;
            float total3DArea = 0f;
            float totalUVArea = 0f;
            int validCount = 0;

            foreach (var (a, b, c) in triangles)
            {
                // 3D 面积
                var area3D = 0.5f * Vector3.Cross(
                    positions[b] - positions[a],
                    positions[c] - positions[a]).Length();

                // UV 面积
                var uvArea = 0.5f * Math.Abs(
                    (uvs[b].X - uvs[a].X) * (uvs[c].Y - uvs[a].Y) -
                    (uvs[c].X - uvs[a].X) * (uvs[b].Y - uvs[a].Y));

                if (area3D < 1e-8f || uvArea < 1e-8f) continue;

                // 拉伸: 面积比
                float ratio = area3D / uvArea;
                float stretch = Math.Abs(MathF.Log(ratio)); // 对数拉伸
                totalStretch += stretch;
                maxStretch = Math.Max(maxStretch, stretch);

                // 扭曲: 角度比
                float angle3D_a = ComputeAngle(positions[a], positions[b], positions[c]);
                float angleUV_a = ComputeAngle(uvs[a], uvs[b], uvs[c]);
                float angle3D_b = ComputeAngle(positions[b], positions[c], positions[a]);
                float angleUV_b = ComputeAngle(uvs[b], uvs[c], uvs[a]);
                float angle3D_c = ComputeAngle(positions[c], positions[a], positions[b]);
                float angleUV_c = ComputeAngle(uvs[c], uvs[a], uvs[b]);

                float angleDiff = Math.Abs(angle3D_a - angleUV_a) +
                                  Math.Abs(angle3D_b - angleUV_b) +
                                  Math.Abs(angle3D_c - angleUV_c);
                float distortion = angleDiff / MathF.PI;
                totalDistortion += distortion;
                maxDistortion = Math.Max(maxDistortion, distortion);

                total3DArea += area3D;
                totalUVArea += uvArea;
                validCount++;
            }

            if (validCount > 0)
            {
                metrics.Stretch = totalStretch / validCount;
                metrics.MaxStretch = maxStretch;
                metrics.Distortion = totalDistortion / validCount;
                metrics.MaxDistortion = maxDistortion;
                metrics.Avg3DArea = total3DArea / validCount;
                metrics.AvgUVArea = totalUVArea / validCount;
            }

            return metrics;
        }

        private static float ComputeAngle(Vector3 a, Vector3 b, Vector3 c)
        {
            var v1 = b - a;
            var v2 = c - a;
            float dot = Math.Clamp(Vector3.Dot(v1, v2) / (v1.Length() * v2.Length() + 1e-8f), -1f, 1f);
            return MathF.Acos(dot);
        }

        private static float ComputeAngle(Vector2 a, Vector2 b, Vector2 c)
        {
            var v1 = b - a;
            var v2 = c - a;
            float dot = Math.Clamp(Vector2.Dot(v1, v2) / (v1.Length() * v2.Length() + 1e-8f), -1f, 1f);
            return MathF.Acos(dot);
        }
    }
}
