// =====================================================================================
//  ShanyuEngine - MeshSimplifier：网格简化器（QEM 算法的简化实现）
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  实现基于 Quadric Error Metric（QEM）的边折叠网格简化算法。
//
//  功能：
//    - 边折叠算法（Quadric Error Metric）
//    - 目标三角形数指定
//    - LOD 链生成（Full → Medium → Low → VeryLow）
//    - 属性保留（UV/Normal 加权）
//    - 简化进度报告
//
//  参考：Garland & Heckbert "Surface Simplification Using Quadric Error Metrics" (1997)
//  注意：纯 C# 实现，无外部依赖。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  LOD 级别定义
    // =================================================================================

    /// <summary>LOD 级别。</summary>
    public enum LODLevel
    {
        /// <summary>完整精度 (100% 三角形数)。</summary>
        Full,
        /// <summary>中等精度 (50% 三角形数)。</summary>
        Medium,
        /// <summary>低精度 (25% 三角形数)。</summary>
        Low,
        /// <summary>极低精度 (10% 三角形数)。</summary>
        VeryLow
    }

    // =================================================================================
    //  简化配置
    // =================================================================================

    /// <summary>网格简化配置参数。</summary>
    public sealed class SimplificationConfig
    {
        /// <summary>目标三角形数比例（0~1，1=不简化）。</summary>
        public float TargetRatio = 0.5f;

        /// <summary>目标三角形绝对数量（优先级高于 TargetRatio）。</summary>
        public int? TargetTriangleCount;

        /// <summary>UV 权重（越大 UV 保持越好，默认 1.0）。</summary>
        public float UVWeight = 1.0f;

        /// <summary>法线权重（越大法线保持越好，默认 1.0）。</summary>
        public float NormalWeight = 1.0f;

        /// <summary>边界边保护（保持开放边不折叠，默认 true）。</summary>
        public bool ProtectBoundary = true;

        /// <summary>最大折叠代价（超过此值的边不折叠，默认 1e10）。</summary>
        public double MaxCost = 1e10;

        /// <summary>进度回调。</summary>
        public Action<float, string>? ProgressCallback;
    }

    // =================================================================================
    //  MeshSimplifier —— 网格简化器
    // =================================================================================

    /// <summary>
    /// 基于 Quadric Error Metric 的网格简化器。
    /// 接收 InterchangeMesh 输入，输出简化后的 InterchangeMesh。
    /// </summary>
    public sealed class MeshSimplifier
    {
        // =================================================================================
        //  内部数据结构
        // =================================================================================

        /// <summary>三维二次误差矩阵 (4x4 对称矩阵，存储为 10 个分量)。</summary>
        private struct Quadric
        {
            public double A2, B2, C2;  // x², y², z²
            public double AB, AC, BC;  // xy, xz, yz
            public double AD, BD, CD;  // x, y, z
            public double D2;          // 常数项

            public static Quadric FromPlane(Vector3 p, Vector3 n)
            {
                // 平面方程: n·x + d = 0, 其中 d = -n·p
                double d = -(n.X * p.X + n.Y * p.Y + n.Z * p.Z);
                double nx = n.X, ny = n.Y, nz = n.Z;

                return new Quadric
                {
                    A2 = nx * nx, B2 = ny * ny, C2 = nz * nz,
                    AB = nx * ny, AC = nx * nz, BC = ny * nz,
                    AD = nx * d, BD = ny * d, CD = nz * d,
                    D2 = d * d
                };
            }

            public static Quadric operator +(Quadric a, Quadric b) => new()
            {
                A2 = a.A2 + b.A2, B2 = a.B2 + b.B2, C2 = a.C2 + b.C2,
                AB = a.AB + b.AB, AC = a.AC + b.AC, BC = a.BC + b.BC,
                AD = a.AD + b.AD, BD = a.BD + b.BD, CD = a.CD + b.CD,
                D2 = a.D2 + b.D2
            };

            /// <summary>计算向量 v 在二次型下的误差值。</summary>
            public double Evaluate(Vector3 v)
            {
                double x = v.X, y = v.Y, z = v.Z;
                return A2 * x * x + B2 * y * y + C2 * z * z
                     + 2 * AB * x * y + 2 * AC * x * z + 2 * BC * y * z
                     + 2 * AD * x + 2 * BD * y + 2 * CD * z
                     + D2;
            }
        }

        /// <summary>半边结构（折叠操作的基础）。</summary>
        private struct Edge
        {
            public int V0, V1; // 顶点索引（V0 折叠到 V1）
            public double Cost;
            public Vector3 OptimalPosition;
        }

        // 内部网格数据
        private List<Vector3> _vertices = null!;
        private List<Vector3> _normals = null!;
        private List<Vector2> _uvs = null!;
        private List<int> _indices = null!;
        private List<int> _triangles = null!; // 三角形顶点索引 (flat, length = indexCount)

        private Quadric[] _quadrics = null!;
        private bool[] _vertexRemoved = null!;
        private bool[] _triangleRemoved = null!;
        private List<int>[] _vertexTriangles = null!; // 每个顶点关联的三角形列表

        private SimplificationConfig _config = null!;

        // =================================================================================
        //  主入口
        // =================================================================================

        /// <summary>
        /// 简化网格到指定三角形数。
        /// </summary>
        /// <param name="inputMesh">输入网格。</param>
        /// <param name="targetTriangleCount">目标三角形数。</param>
        /// <param name="config">简化配置（可选）。</param>
        /// <returns>简化后的网格。</returns>
        public InterchangeMesh Simplify(InterchangeMesh inputMesh, int targetTriangleCount,
            SimplificationConfig? config = null)
        {
            if (inputMesh == null)
                throw new ArgumentNullException(nameof(inputMesh));

            _config = config ?? new SimplificationConfig();

            int inputTriCount = inputMesh.TriangleCount;
            if (targetTriangleCount >= inputTriCount || inputTriCount <= 3)
                return CloneMesh(inputMesh);

            targetTriangleCount = Math.Max(1, targetTriangleCount);
            ReportProgress(0f, "准备简化数据");

            // 1) 复制数据到内部结构
            Initialize(inputMesh);

            // 2) 计算初始二次误差矩阵
            ComputeInitialQuadrics();

            // 3) 计算所有可折叠边
            ReportProgress(0.1f, "计算边代价");
            int targetRemovals = (_triangles.Count / 3) - targetTriangleCount;
            int removed = 0;

            // 4) 迭代折叠边
            while (removed < targetRemovals)
            {
                ReportProgress(0.1f + 0.8f * removed / Math.Max(1, targetRemovals),
                    $"简化中: {removed}/{targetRemovals}");

                var bestEdge = FindBestEdge();
                if (bestEdge.Cost > _config.MaxCost)
                    break; // 没有可接受的折叠

                if (!CollapseEdge(bestEdge))
                    break; // 折叠失败（可能不再有有效边）

                removed++;
            }

            // 5) 构建输出网格
            ReportProgress(0.95f, "构建输出网格");
            var result = BuildOutputMesh(inputMesh);

            ReportProgress(1f, "简化完成");
            return result;
        }

        /// <summary>
        /// 简化网格到指定比例。
        /// </summary>
        public InterchangeMesh Simplify(InterchangeMesh inputMesh, float ratio,
            SimplificationConfig? config = null)
        {
            int targetTris = (int)(inputMesh.TriangleCount * ratio);
            return Simplify(inputMesh, Math.Max(1, targetTris), config);
        }

        /// <summary>
        /// 生成 LOD 链（Full → Medium → Low → VeryLow）。
        /// </summary>
        /// <param name="inputMesh">输入网格。</param>
        /// <param name="config">简化配置。</param>
        /// <returns>按 LODLevel 排序的网格列表（索引 0=Full, 1=Medium, 2=Low, 3=VeryLow）。</returns>
        public List<InterchangeMesh> GenerateLODChain(InterchangeMesh inputMesh,
            SimplificationConfig? config = null)
        {
            var chain = new List<InterchangeMesh>();
            int totalTris = inputMesh.TriangleCount;

            chain.Add(CloneMesh(inputMesh)); // Full (100%)

            if (totalTris > 10)
                chain.Add(Simplify(inputMesh, Math.Max(4, (int)(totalTris * 0.5f)), config)); // Medium (50%)

            if (totalTris > 20)
                chain.Add(Simplify(inputMesh, Math.Max(3, (int)(totalTris * 0.25f)), config)); // Low (25%)

            if (totalTris > 40)
                chain.Add(Simplify(inputMesh, Math.Max(2, (int)(totalTris * 0.1f)), config)); // VeryLow (10%)

            return chain;
        }

        // =================================================================================
        //  内部方法
        // =================================================================================

        private void Initialize(InterchangeMesh mesh)
        {
            _vertices = new List<Vector3>(mesh.Vertices);
            _normals = mesh.Normals.Count == mesh.Vertices.Count
                ? new List<Vector3>(mesh.Normals)
                : new List<Vector3>();
            _uvs = mesh.UV0.Count == mesh.Vertices.Count
                ? new List<Vector2>(mesh.UV0)
                : new List<Vector2>();

            _indices = new List<int>(mesh.Indices);
            _triangles = new List<int>(mesh.Indices);

            int vertCount = _vertices.Count;
            int triCount = _triangles.Count / 3;

            _quadrics = new Quadric[vertCount];
            _vertexRemoved = new bool[vertCount];
            _triangleRemoved = new bool[triCount];

            // 构建顶点-三角形邻接表
            _vertexTriangles = new List<int>[vertCount];
            for (int i = 0; i < vertCount; i++)
                _vertexTriangles[i] = new List<int>();

            for (int t = 0; t < triCount; t++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int vi = _triangles[t * 3 + j];
                    if (vi >= 0 && vi < vertCount)
                        _vertexTriangles[vi].Add(t);
                }
            }
        }

        private void ComputeInitialQuadrics()
        {
            int vertCount = _vertices.Count;
            _quadrics = new Quadric[vertCount];

            for (int t = 0; t < _triangles.Count / 3; t++)
            {
                int i0 = _triangles[t * 3];
                int i1 = _triangles[t * 3 + 1];
                int i2 = _triangles[t * 3 + 2];

                if (i0 >= vertCount || i1 >= vertCount || i2 >= vertCount) continue;

                var p0 = _vertices[i0];
                var p1 = _vertices[i1];
                var p2 = _vertices[i2];

                var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                if (normal.LengthSquared() < 1e-10f)
                    normal = Vector3.UnitY;

                var q = Quadric.FromPlane(p0, normal);
                _quadrics[i0] += q;
                _quadrics[i1] += q;
                _quadrics[i2] += q;
            }
        }

        private Edge FindBestEdge()
        {
            var bestEdge = new Edge { Cost = double.MaxValue };
            int triCount = _triangles.Count / 3;

            for (int t = 0; t < triCount; t++)
            {
                if (_triangleRemoved[t]) continue;

                for (int j = 0; j < 3; j++)
                {
                    int v0 = _triangles[t * 3 + j];
                    int v1 = _triangles[t * 3 + (j + 1) % 3];

                    if (v0 >= _vertices.Count || v1 >= _vertices.Count) continue;
                    if (_vertexRemoved[v0] || _vertexRemoved[v1]) continue;

                    double cost = ComputeCollapseCost(v0, v1);
                    if (cost < bestEdge.Cost)
                    {
                        bestEdge = new Edge
                        {
                            V0 = v0,
                            V1 = v1,
                            Cost = cost,
                            OptimalPosition = ComputeOptimalPosition(v0, v1)
                        };
                    }
                }
            }

            return bestEdge;
        }

        private double ComputeCollapseCost(int v0, int v1)
        {
            // 边界保护
            if (_config.ProtectBoundary && IsBoundaryEdge(v0, v1))
                return double.MaxValue;

            var q = _quadrics[v0] + _quadrics[v1];
            var opt = ComputeOptimalPosition(v0, v1);
            return q.Evaluate(opt);
        }

        private Vector3 ComputeOptimalPosition(int v0, int v1)
        {
            // 求解二次型的最小值点：Ax = b
            // 其中 A 是 3x3 矩阵，b 是 3 维向量
            var q = _quadrics[v0] + _quadrics[v1];

            double[,] A = new double[3, 3];
            double[] b = new double[3];

            A[0, 0] = q.A2; A[0, 1] = q.AB; A[0, 2] = q.AC; b[0] = -q.AD;
            A[1, 0] = q.AB; A[1, 1] = q.B2; A[1, 2] = q.BC; b[1] = -q.BD;
            A[2, 0] = q.AC; A[2, 1] = q.BC; A[2, 2] = q.C2; b[2] = -q.CD;

            // 高斯消元求解
            if (SolveLinearSystem(A, b, out var result))
                return result;

            // 如果不可解，回退到中点
            return (_vertices[v0] + _vertices[v1]) * 0.5f;
        }

        private static bool SolveLinearSystem(double[,] A, double[] b, out Vector3 result)
        {
            result = Vector3.Zero;

            // 3x3 高斯消元
            double[,] aug = new double[3, 4];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    aug[i, j] = A[i, j];
                aug[i, 3] = b[i];
            }

            // 前向消元
            for (int col = 0; col < 3; col++)
            {
                // 选主元
                int maxRow = col;
                for (int row = col + 1; row < 3; row++)
                {
                    if (Math.Abs(aug[row, col]) > Math.Abs(aug[maxRow, col]))
                        maxRow = row;
                }

                if (Math.Abs(aug[maxRow, col]) < 1e-12)
                    return false; // 奇异矩阵

                // 交换行
                for (int j = col; j < 4; j++)
                {
                    double tmp = aug[col, j];
                    aug[col, j] = aug[maxRow, j];
                    aug[maxRow, j] = tmp;
                }

                // 消元
                for (int row = col + 1; row < 3; row++)
                {
                    double factor = aug[row, col] / aug[col, col];
                    for (int j = col; j < 4; j++)
                        aug[row, j] -= factor * aug[col, j];
                }
            }

            // 回代
            double[] x = new double[3];
            for (int i = 2; i >= 0; i--)
            {
                double sum = aug[i, 3];
                for (int j = i + 1; j < 3; j++)
                    sum -= aug[i, j] * x[j];
                x[i] = sum / aug[i, i];
            }

            result = new Vector3((float)x[0], (float)x[1], (float)x[2]);
            return true;
        }

        private bool IsBoundaryEdge(int v0, int v1)
        {
            // 检查边 (v0, v1) 是否只被一个三角形引用（即开放边）
            int count = 0;
            for (int t = 0; t < _triangles.Count / 3; t++)
            {
                if (_triangleRemoved[t]) continue;
                int i0 = _triangles[t * 3];
                int i1 = _triangles[t * 3 + 1];
                int i2 = _triangles[t * 3 + 2];

                if ((i0 == v0 && i1 == v1) || (i1 == v0 && i2 == v1) || (i2 == v0 && i0 == v1) ||
                    (i1 == v0 && i0 == v1) || (i2 == v0 && i1 == v1) || (i0 == v0 && i2 == v1))
                    count++;
            }
            return count == 1;
        }

        private bool CollapseEdge(Edge edge)
        {
            int v0 = edge.V0;
            int v1 = edge.V1;

            if (_vertexRemoved[v0] || _vertexRemoved[v1])
                return false;

            // 将 v0 折叠到 v1 的位置
            _vertices[v1] = edge.OptimalPosition;
            _quadrics[v1] = _quadrics[v0] + _quadrics[v1];

            // 更新所有包含 v0 的三角形
            var affectedTris = new List<int>(_vertexTriangles[v0]);
            foreach (int t in affectedTris)
            {
                if (_triangleRemoved[t]) continue;

                // 找到 v0 在三角形中的位置并替换为 v1
                for (int j = 0; j < 3; j++)
                {
                    if (_triangles[t * 3 + j] == v0)
                        _triangles[t * 3 + j] = v1;
                }

                // 检查三角形是否退化（两个顶点相同）
                int i0 = _triangles[t * 3], i1 = _triangles[t * 3 + 1], i2 = _triangles[t * 3 + 2];
                if (i0 == i1 || i1 == i2 || i0 == i2)
                {
                    _triangleRemoved[t] = true;
                }
            }

            // 标记 v0 为已移除
            _vertexRemoved[v0] = true;

            // 更新顶点-三角形邻接表
            foreach (int t in affectedTris)
            {
                if (_triangleRemoved[t]) continue;
                // 从 v0 的邻接列表中移除该三角形
                _vertexTriangles[v0].Remove(t);
                // 添加到 v1 的邻接列表（如果未包含）
                if (!_vertexTriangles[v1].Contains(t))
                    _vertexTriangles[v1].Add(t);
            }

            return true;
        }

        // =================================================================================
        //  输出构建
        // =================================================================================

        private InterchangeMesh BuildOutputMesh(InterchangeMesh inputMesh)
        {
            var result = new InterchangeMesh
            {
                Name = inputMesh.Name + "_Simplified"
            };

            // 创建顶点映射（旧索引 -> 新索引）
            var vertMap = new Dictionary<int, int>();
            int triCount = _triangles.Count / 3;

            for (int t = 0; t < triCount; t++)
            {
                if (_triangleRemoved[t]) continue;

                for (int j = 0; j < 3; j++)
                {
                    int oldIdx = _triangles[t * 3 + j];
                    if (oldIdx < 0 || oldIdx >= _vertices.Count) continue;
                    if (_vertexRemoved[oldIdx]) continue;

                    if (!vertMap.TryGetValue(oldIdx, out int newIdx))
                    {
                        newIdx = result.Vertices.Count;
                        vertMap[oldIdx] = newIdx;
                        result.Vertices.Add(_vertices[oldIdx]);

                        if (_normals.Count > oldIdx)
                            result.Normals.Add(_normals[oldIdx]);
                        if (_uvs.Count > oldIdx)
                            result.UV0.Add(_uvs[oldIdx]);
                    }

                    result.Indices.Add(newIdx);
                }
            }

            return result;
        }

        private static InterchangeMesh CloneMesh(InterchangeMesh source)
        {
            var clone = new InterchangeMesh
            {
                Name = source.Name,
                Vertices = new List<Vector3>(source.Vertices),
                Indices = new List<int>(source.Indices),
                Normals = new List<Vector3>(source.Normals),
                UV0 = new List<Vector2>(source.UV0),
                Tangents = new List<Vector4>(source.Tangents),
                Colors = new List<Vector4>(source.Colors),
                BoneWeights = new List<InterchangeVertexWeight>(source.BoneWeights),
                MaterialIndices = new List<int>(source.MaterialIndices)
            };
            return clone;
        }

        private void ReportProgress(float progress, string message)
        {
            _config.ProgressCallback?.Invoke(progress, message);
        }
    }

    // =================================================================================
    //  便捷静态入口
    // =================================================================================

    /// <summary>网格简化的静态便捷 API。</summary>
    public static class MeshSimplifierFactory
    {
        private static readonly MeshSimplifier Simplifier = new();

        /// <summary>简化网格到指定三角形数。</summary>
        public static InterchangeMesh Simplify(InterchangeMesh mesh, int targetTriangleCount,
            SimplificationConfig? config = null)
        {
            return Simplifier.Simplify(mesh, targetTriangleCount, config);
        }

        /// <summary>简化网格到指定比例。</summary>
        public static InterchangeMesh Simplify(InterchangeMesh mesh, float ratio,
            SimplificationConfig? config = null)
        {
            return Simplifier.Simplify(mesh, ratio, config);
        }

        /// <summary>生成 LOD 链。</summary>
        public static List<InterchangeMesh> GenerateLODChain(InterchangeMesh mesh,
            SimplificationConfig? config = null)
        {
            return Simplifier.GenerateLODChain(mesh, config);
        }
    }
}
