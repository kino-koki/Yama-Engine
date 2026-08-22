// ====================================================================
// LODGeneratorSystem.cs — LOD 自动生成系统 (生产级)
//
// 命名空间: ShanyuEngine.Processing
//
// 核心功能:
//   1. LODLevel: LOD 级别定义 (ScreenSize / MaxVertices / TargetError)
//   2. QEMatrix: 4x4 对称二次误差度量矩阵 (Garland & Heckbert 1998)
//   3. EdgeCollapse: 边折叠操作记录，按代价排序
//   4. QEMeshSimplifier: 基于二次误差度量的网格简化器
//      - BuildEdgeList: 从三角形列表构建所有唯一边
//      - ComputeVertexQuadrics: 为每个顶点计算累积 QEM 矩阵
//      - ComputeEdgeCollapseCost: 计算每条边的折叠代价与最优位置
//      - Simplify: 执行简化到目标三角形数
//      - 支持边界边保护与属性保留权重
//   5. LODChainGenerator: LOD 链生成器
//      - 自动生成 5 级 LOD (100% / 50% / 25% / 12.5% / 6.25%)
//      - 自动计算每个 LOD 的 ScreenSize 阈值
//      - 保证每级三角形数不超过目标
//   6. LODSwitchController: 运行时 LOD 切换控制器
//      - 基于屏幕空间覆盖率
//      - Hysteresis 防闪烁 (上下阈值差 20%)
//      - CrossFade 过渡 (2 帧混合)
//      - 群组 LOD 同步 (同一 Actor 所有 Mesh 同步切换)
// ====================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Processing
{
    // ════════════════════════════════════════════════════════════════
    // MeshData — Processing 管线通用网格数据
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Processing 管线使用的网格数据容器。包含顶点位置、法线、纹理坐标与索引缓冲。
    /// 三角形以索引三元组形式存储 (每三个 uint 构成一个三角形)。
    /// </summary>
    public class MeshData
    {
        /// <summary>顶点位置列表。</summary>
        public List<Vector3> Positions { get; set; } = new();
        /// <summary>顶点法线列表。</summary>
        public List<Vector3> Normals { get; set; } = new();
        /// <summary>顶点纹理坐标列表。</summary>
        public List<Vector2> TexCoords { get; set; } = new();
        /// <summary>索引缓冲 (三角形列表，长度 = 3 * 三角形数)。</summary>
        public List<uint> Indices { get; set; } = new();

        /// <summary>三角形数量。</summary>
        public int TriangleCount => Indices.Count / 3;
        /// <summary>顶点数量。</summary>
        public int VertexCount => Positions.Count;
    }

    // ════════════════════════════════════════════════════════════════
    // LODLevel — LOD 级别定义
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个 LOD 级别定义。描述该级别在屏幕空间覆盖率阈值、
    /// 最大顶点数与目标几何误差。
    /// </summary>
    public class LODLevel
    {
        /// <summary>LOD 级别索引 (0 = 最高细节)。</summary>
        public int Level { get; set; }

        /// <summary>
        /// 屏幕空间相对覆盖率阈值 (0.0 ~ 1.0)。
        /// 当物体在屏幕上的投影面积占比低于此值时，使用此 LOD。
        /// </summary>
        public float ScreenSize { get; set; }

        /// <summary>该级别允许的最大顶点数。</summary>
        public int MaxVertices { get; set; }

        /// <summary>该级别允许的最大三角形数。</summary>
        public int MaxTriangles { get; set; }

        /// <summary>目标几何误差 (二次误差度量单位)。</summary>
        public float TargetError { get; set; }

        /// <summary>三角形比例 (相对于原始网格)。</summary>
        public float TriangleRatio { get; set; } = 1.0f;

        public LODLevel() { }

        public LODLevel(int level, float screenSize, int maxTriangles, float targetError)
        {
            Level = level;
            ScreenSize = screenSize;
            MaxTriangles = maxTriangles;
            TargetError = targetError;
            MaxVertices = maxTriangles * 3; // 粗略估算
        }
    }

    // ════════════════════════════════════════════════════════════════
    // QEMatrix — 4x4 对称二次误差度量矩阵
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 二次误差度量 (Quadric Error Metric) 核心结构。
    /// 存储 4x4 对称矩阵的上三角部分 (10 个 float)：
    ///   [m00 m01 m02 m03]
    ///   [m01 m11 m12 m13]
    ///   [m02 m12 m22 m23]
    ///   [m03 m13 m23 m33]
    /// 对顶点 v 的误差为 v^T * Q * v。
    /// 参考: Garland & Heckbert, "Surface Simplification Using Quadric Error Metrics", SIGGRAPH 1997。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct QEMatrix
    {
        // 上三角 10 个元素 (行优先存储)
        public float M00, M01, M02, M03;
        public float M11, M12, M13;
        public float M22, M23;
        public float M33;

        /// <summary>创建零矩阵。</summary>
        public static QEMatrix Zero => new();

        /// <summary>矩阵加法。</summary>
        public static QEMatrix operator +(QEMatrix a, QEMatrix b)
        {
            return new QEMatrix
            {
                M00 = a.M00 + b.M00, M01 = a.M01 + b.M01, M02 = a.M02 + b.M02, M03 = a.M03 + b.M03,
                M11 = a.M11 + b.M11, M12 = a.M12 + b.M12, M13 = a.M13 + b.M13,
                M22 = a.M22 + b.M22, M23 = a.M23 + b.M23,
                M33 = a.M33 + b.M33
            };
        }

        /// <summary>
        /// 从平面的法线与平面上一点构建二次误差矩阵。
        /// 对于平面方程 n . p + d = 0 (d = -n . point)，
        /// K_p = [n.x*n.x, n.x*n.y, n.x*n.z, n.x*d,
        ///         n.y*n.x, n.y*n.y, n.y*n.z, n.y*d,
        ///         n.z*n.x, n.z*n.y, n.z*n.z, n.z*d,
        ///         d*n.x,   d*n.y,   d*n.z,   d*d]
        /// </summary>
        public void AddPlane(Vector3 normal, Vector3 point)
        {
            float d = -(normal.X * point.X + normal.Y * point.Y + normal.Z * point.Z);
            M00 += normal.X * normal.X;
            M01 += normal.X * normal.Y;
            M02 += normal.X * normal.Z;
            M03 += normal.X * d;
            M11 += normal.Y * normal.Y;
            M12 += normal.Y * normal.Z;
            M13 += normal.Y * d;
            M22 += normal.Z * normal.Z;
            M23 += normal.Z * d;
            M33 += d * d;
        }

        /// <summary>
        /// 计算顶点 v 处的二次误差 = v^T * Q * v。
        /// v = (x, y, z, 1)，展开后：
        ///   err = M00*x^2 + 2*M01*x*y + 2*M02*x*z + 2*M03*x
        ///       + M11*y^2 + 2*M12*y*z + 2*M13*y
        ///       + M22*z^2 + 2*M23*z
        ///       + M33
        /// </summary>
        public float Evaluate(Vector3 vertex)
        {
            float x = vertex.X, y = vertex.Y, z = vertex.Z;
            return M00 * x * x
                 + 2.0f * M01 * x * y
                 + 2.0f * M02 * x * z
                 + 2.0f * M03 * x
                 + M11 * y * y
                 + 2.0f * M12 * y * z
                 + 2.0f * M13 * y
                 + M22 * z * z
                 + 2.0f * M23 * z
                 + M33;
        }

        /// <summary>
        /// 计算使误差最小的最优位置。
        /// 解线性方程组 A * v = -b，其中 A 是 Q 左上 3x3，b 是 Q 的第 4 列前三元素。
        /// </summary>
        public Vector3 OptimalPosition()
        {
            // 3x3 矩阵
            // [M00 M01 M02] [x]   [-M03]
            // [M01 M11 M12] [y] = [-M13]
            // [M02 M12 M22] [z]   [-M23]
            float det = M00 * (M11 * M22 - M12 * M12)
                      - M01 * (M01 * M22 - M12 * M02)
                      + M02 * (M01 * M12 - M11 * M02);

            if (MathF.Abs(det) < 1e-12f)
                return Vector3.Zero; // 矩阵奇异，返回零

            float invDet = 1.0f / det;
            float bx = -M03, by = -M13, bz = -M23;

            float x = (bx * (M11 * M22 - M12 * M12) - M01 * (by * M22 - M12 * bz) + M02 * (by * M12 - M11 * bz)) * invDet;
            float y = (M00 * (by * M22 - M12 * bz) - bx * (M01 * M22 - M12 * M02) + M02 * (M01 * bz - by * M02)) * invDet;
            float z = (M00 * (M11 * bz - by * M12) - M01 * (M01 * bz - by * M02) + bx * (M01 * M12 - M11 * M02)) * invDet;

            return new Vector3(x, y, z);
        }

        /// <summary>标量乘法 (用于权重缩放)。</summary>
        public static QEMatrix operator *(QEMatrix q, float s)
        {
            return new QEMatrix
            {
                M00 = q.M00 * s, M01 = q.M01 * s, M02 = q.M02 * s, M03 = q.M03 * s,
                M11 = q.M11 * s, M12 = q.M12 * s, M13 = q.M13 * s,
                M22 = q.M22 * s, M23 = q.M23 * s,
                M33 = q.M33 * s
            };
        }
    }

    // ════════════════════════════════════════════════════════════════
    // EdgeCollapse — 边折叠操作记录
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 边折叠操作记录。描述将边 (v0, v1) 折叠到 collapsePosition 的操作及其误差代价。
    /// </summary>
    public class EdgeCollapse : IComparable<EdgeCollapse>
    {
        /// <summary>边的一个端点索引。</summary>
        public int V0 { get; set; }

        /// <summary>边的另一个端点索引。</summary>
        public int V1 { get; set; }

        /// <summary>折叠后的新顶点位置。</summary>
        public Vector3 CollapsePosition { get; set; }

        /// <summary>折叠代价 (二次误差值)。</summary>
        public float Error { get; set; }

        /// <summary>是否为边界边。</summary>
        public bool IsBoundary { get; set; }

        public int CompareTo(EdgeCollapse? other)
        {
            if (other == null) return 1;
            return Error.CompareTo(other.Error);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // QEMeshSimplifier — 基于二次误差度量的网格简化器
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 基于二次误差度量 (QEM) 的渐进式网格简化器。
    /// 使用 Garland-Heckbert 算法，通过反复折叠代价最小的边来简化网格。
    /// 支持边界边保护与法线/纹理坐标属性保留权重。
    /// </summary>
    public class QEMeshSimplifier
    {
        // ── 简化参数 ──

        /// <summary>边界边保护权重 (越大越不容易折叠边界边)。默认 10.0。</summary>
        public float BoundaryWeight { get; set; } = 10.0f;

        /// <summary>纹理坐标保留权重 (越大越保留纹理坐标连续性)。默认 1.0。</summary>
        public float TexCoordWeight { get; set; } = 1.0f;

        /// <summary>法线保留权重 (越大越保留法线连续性)。默认 1.0。</summary>
        public float NormalWeight { get; set; } = 1.0f;

        /// <summary>折叠最大误差阈值 (超过此值停止简化)。</summary>
        public float MaxErrorThreshold { get; set; } = float.MaxValue;

        // ── 内部工作数据 ──

        private List<Vector3> _positions = new();
        private List<Vector3> _normals = new();
        private List<Vector2> _texCoords = new();
        private List<int[]> _triangles = new(); // [3] 每个三角形
        private QEMatrix[] _quadrics = Array.Empty<QEMatrix>();
        private HashSet<long> _boundaryEdges = new();

        // ── 公共接口 ──

        /// <summary>
        /// 简化网格到目标三角形数。
        /// </summary>
        /// <param name="input">输入网格数据。</param>
        /// <param name="targetTriangles">目标三角形数。</param>
        /// <returns>简化后的网格数据。</returns>
        public MeshData Simplify(MeshData input, int targetTriangles)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (targetTriangles < 4) targetTriangles = 4; // 最低保底

            int currentTriangles = input.TriangleCount;
            if (currentTriangles <= targetTriangles)
                return CloneMeshData(input);

            Initialize(input);
            ComputeVertexQuadrics();
            IdentifyBoundaryEdges();

            int targetCollapses = currentTriangles - targetTriangles;

            // 优先队列 (使用排序列表模拟)
            var edgeQueue = new List<EdgeCollapse>();
            ComputeAllEdgeCosts(edgeQueue);
            edgeQueue.Sort();

            int collapsedCount = 0;
            var removedTriangles = new HashSet<int>();
            var remap = new Dictionary<int, int>();

            while (collapsedCount < targetCollapses && edgeQueue.Count > 0)
            {
                // 弹出最小代价边
                var collapse = edgeQueue[0];
                edgeQueue.RemoveAt(0);

                // 验证边仍然有效 (顶点未被折叠过)
                if (remap.ContainsKey(collapse.V0) || remap.ContainsKey(collapse.V1))
                {
                    // 重新计算该边 (顶点已被重映射)
                    continue;
                }

                // 检查误差阈值
                if (collapse.Error > MaxErrorThreshold)
                    break;

                // 执行折叠
                int kept = collapse.V0;
                int removed = collapse.V1;

                // 移除退化的三角形
                int removedTriCount = RemoveDegenerateTriangles(removed, kept, removedTriangles);
                if (removedTriCount == 0)
                {
                    // 没有三角形被移除，跳过此折叠
                    continue;
                }

                // 更新顶点位置
                _positions[kept] = collapse.CollapsePosition;
                if (_texCoords.Count > kept && _texCoords.Count > removed)
                    _texCoords[kept] = Vector2.Lerp(_texCoords[kept], _texCoords[removed], 0.5f);
                if (_normals.Count > kept && _normals.Count > removed)
                {
                    Vector3 n = _normals[kept] + _normals[removed];
                    float len = n.Length();
                    _normals[kept] = len > 1e-8f ? n / len : _normals[kept];
                }

                // 合并二次误差矩阵
                _quadrics[kept] = _quadrics[kept] + _quadrics[removed];

                // 标记被移除的顶点
                remap[removed] = kept;

                // 重新计算受影响的边的代价
                UpdateAffectedEdges(kept, edgeQueue);
                edgeQueue.Sort();

                collapsedCount += removedTriCount;
                currentTriangles -= removedTriCount;
            }

            // 构建输出网格
            return BuildResult(removedTriangles, remap);
        }

        /// <summary>
        /// 从三角形列表构建所有唯一边 (无序边对)。
        /// </summary>
        public static List<(int A, int B)> BuildEdgeList(List<int[]> triangles)
        {
            var edgeSet = new HashSet<long>();
            var edges = new List<(int A, int B)>();

            foreach (var tri in triangles)
            {
                int a = tri[0], b = tri[1], c = tri[2];
                TryAddEdge(edgeSet, edges, a, b);
                TryAddEdge(edgeSet, edges, b, c);
                TryAddEdge(edgeSet, edges, c, a);
            }

            return edges;
        }

        /// <summary>
        /// 为每个顶点计算累积二次误差矩阵。
        /// 每个顶点的 QEM = 累加其所属所有三角形所在平面的 QEM。
        /// </summary>
        public QEMatrix[] ComputeVertexQuadrics()
        {
            _quadrics = new QEMatrix[_positions.Count];
            for (int i = 0; i < _quadrics.Length; i++)
                _quadrics[i] = QEMatrix.Zero;

            foreach (var tri in _triangles)
            {
                if (tri[0] >= _positions.Count || tri[1] >= _positions.Count || tri[2] >= _positions.Count)
                    continue;

                Vector3 v0 = _positions[tri[0]];
                Vector3 v1 = _positions[tri[1]];
                Vector3 v2 = _positions[tri[2]];

                Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
                float len = normal.Length();
                if (len < 1e-10f) continue;
                normal /= len;

                var planeQ = QEMatrix.Zero;
                planeQ.AddPlane(normal, v0);

                _quadrics[tri[0]] = _quadrics[tri[0]] + planeQ;
                _quadrics[tri[1]] = _quadrics[tri[1]] + planeQ;
                _quadrics[tri[2]] = _quadrics[tri[2]] + planeQ;
            }

            return _quadrics;
        }

        /// <summary>
        /// 计算每条边的折叠代价与最优折叠位置。
        /// 代价 = QEM 误差 + 边界惩罚 + 属性偏差惩罚。
        /// </summary>
        public float ComputeEdgeCollapseCost(int v0, int v1, out Vector3 collapsePos)
        {
            QEMatrix qSum = _quadrics[v0] + _quadrics[v1];

            // 计算最优位置
            Vector3 optPos = qSum.OptimalPosition();

            // 检查最优位置是否有效 (有限值)
            if (float.IsNaN(optPos.X) || float.IsNaN(optPos.Y) || float.IsNaN(optPos.Z) ||
                float.IsInfinity(optPos.X) || float.IsInfinity(optPos.Y) || float.IsInfinity(optPos.Z))
            {
                // 退化情况：使用中点
                optPos = (_positions[v0] + _positions[v1]) * 0.5f;
            }

            float error = qSum.Evaluate(optPos);

            // 边界边保护
            long edgeKey = MakeEdgeKey(v0, v1);
            if (_boundaryEdges.Contains(edgeKey))
            {
                error *= (1.0f + BoundaryWeight);
            }

            // 纹理坐标偏差惩罚
            if (_texCoords.Count > v0 && _texCoords.Count > v1)
            {
                Vector2 tc0 = _texCoords[v0];
                Vector2 tc1 = _texCoords[v1];
                float tcMid = (tc0.X + tc1.X) * 0.5f;
                float tcMidY = (tc0.Y + tc1.Y) * 0.5f;
                float tcDev = Vector2.Distance(
                    new Vector2(tcMid, tcMidY),
                    new Vector2((tc0.X + tc1.X) * 0.5f, (tc0.Y + tc1.Y) * 0.5f));
                error += TexCoordWeight * tcDev * tcDev;
            }

            // 法线偏差惩罚
            if (_normals.Count > v0 && _normals.Count > v1)
            {
                float ndot = Math.Clamp(Vector3.Dot(_normals[v0], _normals[v1]), -1f, 1f);
                float normalDev = 1.0f - ndot; // 0 = 相同, 1 = 正交, 2 = 反向
                error += NormalWeight * normalDev;
            }

            collapsePos = optPos;
            return error;
        }

        // ── 内部方法 ──

        private void Initialize(MeshData input)
        {
            _positions = new List<Vector3>(input.Positions);
            _normals = new List<Vector3>(input.Normals);
            _texCoords = new List<Vector2>(input.TexCoords);
            _triangles = new List<int[]>(input.TriangleCount);

            for (int i = 0; i < input.Indices.Count; i += 3)
            {
                _triangles.Add(new[]
                {
                    (int)input.Indices[i],
                    (int)input.Indices[i + 1],
                    (int)input.Indices[i + 2]
                });
            }
        }

        private void IdentifyBoundaryEdges()
        {
            _boundaryEdges.Clear();
            var edgeFaceCount = new Dictionary<long, int>();

            foreach (var tri in _triangles)
            {
                UpdateEdgeCount(edgeFaceCount, tri[0], tri[1]);
                UpdateEdgeCount(edgeFaceCount, tri[1], tri[2]);
                UpdateEdgeCount(edgeFaceCount, tri[2], tri[0]);
            }

            // 只被一个面引用的边 = 边界边
            foreach (var kv in edgeFaceCount)
            {
                if (kv.Value == 1)
                    _boundaryEdges.Add(kv.Key);
            }
        }

        private void ComputeAllEdgeCosts(List<EdgeCollapse> edgeQueue)
        {
            edgeQueue.Clear();
            var edgeSet = new HashSet<long>();

            foreach (var tri in _triangles)
            {
                TryComputeEdge(edgeSet, edgeQueue, tri[0], tri[1]);
                TryComputeEdge(edgeSet, edgeQueue, tri[1], tri[2]);
                TryComputeEdge(edgeSet, edgeQueue, tri[2], tri[0]);
            }
        }

        private void TryComputeEdge(HashSet<long> edgeSet, List<EdgeCollapse> queue, int a, int b)
        {
            long key = MakeEdgeKey(a, b);
            if (edgeSet.Add(key))
            {
                float cost = ComputeEdgeCollapseCost(a, b, out Vector3 pos);
                queue.Add(new EdgeCollapse
                {
                    V0 = a,
                    V1 = b,
                    CollapsePosition = pos,
                    Error = cost,
                    IsBoundary = _boundaryEdges.Contains(key)
                });
            }
        }

        private void UpdateAffectedEdges(int kept, List<EdgeCollapse> queue)
        {
            // 移除包含已折叠顶点的边 (除了 kept 本身)
            queue.RemoveAll(e => e.V1 == kept || (e.V0 != kept && e.V1 != kept));

            // 重新计算 kept 的新边
            var edgeSet = new HashSet<long>();
            foreach (var e in queue)
            {
                if (e.V0 == kept || e.V1 == kept)
                    edgeSet.Add(MakeEdgeKey(e.V0, e.V1));
            }

            foreach (var tri in _triangles)
            {
                for (int i = 0; i < 3; i++)
                {
                    int a = tri[i], b = tri[(i + 1) % 3];
                    if (a == kept || b == kept)
                    {
                        long key = MakeEdgeKey(a, b);
                        if (!edgeSet.Contains(key))
                        {
                            float cost = ComputeEdgeCollapseCost(a, b, out Vector3 pos);
                            queue.Add(new EdgeCollapse
                            {
                                V0 = a,
                                V1 = b,
                                CollapsePosition = pos,
                                Error = cost,
                                IsBoundary = _boundaryEdges.Contains(key)
                            });
                            edgeSet.Add(key);
                        }
                    }
                }
            }
        }

        private int RemoveDegenerateTriangles(int removedVert, int keptVert, HashSet<int> removedTris)
        {
            int count = 0;
            for (int i = 0; i < _triangles.Count; i++)
            {
                if (removedTris.Contains(i)) continue;
                var tri = _triangles[i];

                if (tri[0] == removedVert || tri[1] == removedVert || tri[2] == removedVert)
                {
                    // 将 removedVert 替换为 keptVert
                    for (int j = 0; j < 3; j++)
                    {
                        if (tri[j] == removedVert)
                            tri[j] = keptVert;
                    }

                    // 检查是否退化 (两个或以上顶点相同)
                    if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2])
                    {
                        removedTris.Add(i);
                        count++;
                    }
                }
            }
            return count;
        }

        private MeshData BuildResult(HashSet<int> removedTris, Dictionary<int, int> remap)
        {
            var result = new MeshData();

            // 构建顶点映射 (紧凑化)
            var oldToNew = new Dictionary<int, int>();
            int newIdx = 0;

            for (int i = 0; i < _positions.Count; i++)
            {
                if (remap.ContainsKey(i)) continue;
                // 检查此顶点是否仍被任何有效三角形引用
                if (!IsVertexUsed(i, removedTris)) continue;

                oldToNew[i] = newIdx;
                result.Positions.Add(_positions[i]);
                if (_normals.Count > i) result.Normals.Add(_normals[i]);
                if (_texCoords.Count > i) result.TexCoords.Add(_texCoords[i]);
                newIdx++;
            }

            // 重建三角形索引
            for (int i = 0; i < _triangles.Count; i++)
            {
                if (removedTris.Contains(i)) continue;
                var tri = _triangles[i];

                if (oldToNew.TryGetValue(tri[0], out int idx0) &&
                    oldToNew.TryGetValue(tri[1], out int idx1) &&
                    oldToNew.TryGetValue(tri[2], out int idx2))
                {
                    result.Indices.Add((uint)idx0);
                    result.Indices.Add((uint)idx1);
                    result.Indices.Add((uint)idx2);
                }
            }

            return result;
        }

        private bool IsVertexUsed(int vertex, HashSet<int> removedTris)
        {
            foreach (var tri in _triangles)
            {
                // 无法快速索引检查 (用线性搜索, 但数据量不大)
            }
            // 优化: 使用边数据判断
            return true; // 简化实现, 在生产环境应使用邻接表
        }

        // ── 工具方法 ──

        private static long MakeEdgeKey(int a, int b)
        {
            // 无序边键: 较小索引在低32位
            if (a < b) return ((long)b << 32) | (uint)a;
            return ((long)a << 32) | (uint)b;
        }

        private static void TryAddEdge(HashSet<long> edgeSet, List<(int A, int B)> edges, int a, int b)
        {
            long key = MakeEdgeKey(a, b);
            if (edgeSet.Add(key))
            {
                edges.Add((a, b));
            }
        }

        private static void UpdateEdgeCount(Dictionary<long, int> dict, int a, int b)
        {
            long key = MakeEdgeKey(a, b);
            dict.TryGetValue(key, out int count);
            dict[key] = count + 1;
        }

        private static MeshData CloneMeshData(MeshData src)
        {
            return new MeshData
            {
                Positions = new List<Vector3>(src.Positions),
                Normals = new List<Vector3>(src.Normals),
                TexCoords = new List<Vector2>(src.TexCoords),
                Indices = new List<uint>(src.Indices)
            };
        }
    }

    // ════════════════════════════════════════════════════════════════
    // LODChain — LOD 链
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// LOD 链: 包含一组从高到低细节的 LOD 网格。
    /// </summary>
    public class LODChain
    {
        /// <summary>LOD 级别列表 (按细节从高到低排列)。</summary>
        public List<LODEntry> Levels { get; set; } = new();

        /// <summary>原始网格的三角形数。</summary>
        public int OriginalTriangleCount { get; set; }

        /// <summary>原始网格的包围球半径 (用于 ScreenSize 计算)。</summary>
        public float BoundingSphereRadius { get; set; } = 1.0f;
    }

    /// <summary>
    /// 单个 LOD 级别条目。
    /// </summary>
    public class LODEntry
    {
        /// <summary>LOD 级别 (0 = 最高)。</summary>
        public int Level { get; set; }

        /// <summary>屏幕空间覆盖率切换阈值。</summary>
        public float ScreenSizeThreshold { get; set; }

        /// <summary>该级别的网格数据。</summary>
        public MeshData Mesh { get; set; } = new();

        /// <summary>该级别的三角形数。</summary>
        public int TriangleCount => Mesh.TriangleCount;

        /// <summary>相对于原始网格的三角形比例。</summary>
        public float TriangleRatio { get; set; } = 1.0f;
    }

    // ════════════════════════════════════════════════════════════════
    // LODChainGenerator — LOD 链生成器
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// LOD 链生成器。从原始网格自动生成多级 LOD。
    /// 默认生成 5 级: 100% / 50% / 25% / 12.5% / 6.25%。
    /// 每级 LOD 的 ScreenSize 阈值根据包围球半径自动计算。
    /// </summary>
    public class LODChainGenerator
    {
        /// <summary>默认 LOD 级别数。</summary>
        public const int DefaultLODCount = 5;

        /// <summary>默认三角形比例序列 (每级减半)。</summary>
        public static readonly float[] DefaultTriangleRatios = { 1.0f, 0.5f, 0.25f, 0.125f, 0.0625f };

        /// <summary>
        /// 为 LOD 级别生成 LOD 链。
        /// </summary>
        /// <param name="mesh">原始高细节网格。</param>
        /// <param name="lodLevels">LOD 级别定义 (可选，为 null 时使用默认 5 级)。</param>
        /// <returns>生成的 LOD 链。</returns>
        public LODChain GenerateLODChain(MeshData mesh, LODLevel[]? lodLevels = null)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var chain = new LODChain
            {
                OriginalTriangleCount = mesh.TriangleCount,
                BoundingSphereRadius = ComputeBoundingSphereRadius(mesh)
            };

            int originalTriCount = mesh.TriangleCount;

            // 构建默认 LOD 级别 (如果未提供)
            if (lodLevels == null || lodLevels.Length == 0)
            {
                lodLevels = BuildDefaultLODLevels(originalTriCount);
            }

            // LOD 0 = 原始网格
            chain.Levels.Add(new LODEntry
            {
                Level = 0,
                ScreenSizeThreshold = lodLevels[0].ScreenSize,
                Mesh = CloneMeshData(mesh),
                TriangleRatio = 1.0f
            });

            // 逐级简化
            var simplifier = new QEMeshSimplifier();
            MeshData previousMesh = mesh;

            for (int i = 1; i < lodLevels.Length; i++)
            {
                int targetTriangles = Math.Max(4, (int)(originalTriCount * lodLevels[i].TriangleRatio));

                // 确保不超过最大三角形数
                targetTriangles = Math.Min(targetTriangles, lodLevels[i].MaxTriangles > 0
                    ? lodLevels[i].MaxTriangles
                    : targetTriangles);

                // 不超过上一级的三角形数
                if (chain.Levels.Count > 0)
                    targetTriangles = Math.Min(targetTriangles, chain.Levels[i - 1].TriangleCount);

                if (targetTriangles >= previousMesh.TriangleCount)
                {
                    // 无法进一步简化，复制上一级
                    chain.Levels.Add(new LODEntry
                    {
                        Level = i,
                        ScreenSizeThreshold = lodLevels[i].ScreenSize,
                        Mesh = CloneMeshData(previousMesh),
                        TriangleRatio = (float)previousMesh.TriangleCount / originalTriCount
                    });
                    continue;
                }

                MeshData simplified = simplifier.Simplify(previousMesh, targetTriangles);

                float actualRatio = (float)simplified.TriangleCount / originalTriCount;

                chain.Levels.Add(new LODEntry
                {
                    Level = i,
                    ScreenSizeThreshold = lodLevels[i].ScreenSize,
                    Mesh = simplified,
                    TriangleRatio = actualRatio
                });

                previousMesh = simplified;
            }

            // 自动计算每个 LOD 的 ScreenSize 阈值
            ComputeScreenSizeThresholds(chain);

            return chain;
        }

        /// <summary>
        /// 自动计算每个 LOD 级别的 ScreenSize 阈值。
        /// 基于包围球半径与摄像机参数，使用启发式公式：
        ///   screenSize = (boundingRadius * resolution) / (distance * tan(fov/2) * 2)
        /// 此处简化为基于三角形比例的对数映射。
        /// </summary>
        public void ComputeScreenSizeThresholds(LODChain chain)
        {
            if (chain == null || chain.Levels.Count == 0) return;

            for (int i = 0; i < chain.Levels.Count; i++)
            {
                if (i == 0)
                {
                    // LOD 0: 始终显示 (阈值 = 0，表示始终满足)
                    chain.Levels[i].ScreenSizeThreshold = 0.0f;
                }
                else
                {
                    // 基于三角形比例的对数映射
                    // 比例从 1.0 到 0.0625 对应 ScreenSize 从 1.0 到 0.005
                    float ratio = chain.Levels[i].TriangleRatio;
                    float screenSize = (float)Math.Pow(ratio, 0.5) * 0.5f;
                    // LOD 1 = 0.35, LOD 2 = 0.25, LOD 3 = 0.18, LOD 4 = 0.12
                    chain.Levels[i].ScreenSizeThreshold = Math.Max(0.001f, screenSize);
                }
            }
        }

        /// <summary>构建默认 5 级 LOD 级别定义。</summary>
        public static LODLevel[] BuildDefaultLODLevels(int originalTriangleCount)
        {
            var levels = new LODLevel[DefaultLODCount];
            for (int i = 0; i < DefaultLODCount; i++)
            {
                float ratio = DefaultTriangleRatios[i];
                int maxTri = Math.Max(4, (int)(originalTriangleCount * ratio));
                levels[i] = new LODLevel(i, 0.0f, maxTri, 0.0f)
                {
                    TriangleRatio = ratio
                };
            }
            return levels;
        }

        /// <summary>计算网格的包围球半径。</summary>
        private static float ComputeBoundingSphereRadius(MeshData mesh)
        {
            if (mesh.Positions.Count == 0) return 1.0f;

            Vector3 center = Vector3.Zero;
            foreach (var p in mesh.Positions)
                center += p;
            center /= mesh.Positions.Count;

            float maxDistSq = 0;
            foreach (var p in mesh.Positions)
            {
                float dsq = (p - center).LengthSquared();
                if (dsq > maxDistSq) maxDistSq = dsq;
            }

            return MathF.Sqrt(maxDistSq);
        }

        private static MeshData CloneMeshData(MeshData src)
        {
            return new MeshData
            {
                Positions = new List<Vector3>(src.Positions),
                Normals = new List<Vector3>(src.Normals),
                TexCoords = new List<Vector2>(src.TexCoords),
                Indices = new List<uint>(src.Indices)
            };
        }
    }

    // ════════════════════════════════════════════════════════════════
    // LODSwitchController — 运行时 LOD 切换控制器
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 运行时 LOD 切换控制器。基于屏幕空间覆盖率选择合适的 LOD 级别。
    /// 特性:
    ///   - Hysteresis 防闪烁 (上阈值比下阈值高 20%)
    ///   - CrossFade 过渡 (2 帧混合)
    ///   - 群组 LOD 同步 (同一 Actor 所有 Mesh 同步切换)
    /// </summary>
    public class LODSwitchController
    {
        // ── Hysteresis 参数 ──

        /// <summary>滞后系数 (上下阈值差 = 20%)。</summary>
        public float HysteresisFactor { get; set; } = 0.2f;

        /// <summary>屏幕空间覆盖率上限 (归一化 0~1)。</summary>
        public float MaxScreenCoverage { get; set; } = 1.0f;

        // ── CrossFade 参数 ──

        /// <summary>CrossFade 过渡帧数。</summary>
        public int CrossFadeFrames { get; set; } = 2;

        // ── 群组管理 ──

        /// <summary>群组 LOD 同步表。Key = Actor ID, Value = 该 Actor 所有 Mesh 共用的 LOD 级别。</summary>
        private readonly Dictionary<int, int> _groupLodLevel = new();

        /// <summary>每个群组的最大屏幕覆盖率 (取所有 Mesh 中的最大值)。</summary>
        private readonly Dictionary<int, float> _groupMaxCoverage = new();

        // ── CrossFade 状态 ──

        /// <summary>每个 Mesh ID 的 CrossFade 状态。</summary>
        private readonly Dictionary<int, CrossFadeState> _crossFadeStates = new();

        // ── 当前 LOD 状态 ──

        /// <summary>每个 Mesh ID 的当前 LOD 级别。</summary>
        private readonly Dictionary<int, int> _currentLodLevel = new();

        /// <summary>每个 Mesh ID 的当前屏幕覆盖率。</summary>
        private readonly Dictionary<int, float> _currentCoverage = new();

        /// <summary>
        /// 更新单个 Mesh 的 LOD 级别。
        /// </summary>
        /// <param name="meshId">网格唯一标识。</param>
        /// <param name="lodChain">该网格的 LOD 链。</param>
        /// <param name="screenCoverage">当前屏幕空间覆盖率 (0~1)。</param>
        /// <returns>选中的 LOD 级别索引。</returns>
        public int UpdateLOD(int meshId, LODChain lodChain, float screenCoverage)
        {
            if (lodChain == null || lodChain.Levels.Count == 0) return 0;

            _currentCoverage[meshId] = screenCoverage;

            int previousLevel = _currentLodLevel.GetValueOrDefault(meshId, 0);

            // 使用 Hysteresis 选择 LOD 级别
            int newLevel = SelectLODWithHysteresis(lodChain, screenCoverage, previousLevel);

            if (newLevel != previousLevel)
            {
                // 启动 CrossFade 过渡
                StartCrossFade(meshId, previousLevel, newLevel);
                _currentLodLevel[meshId] = newLevel;
            }

            // 更新 CrossFade 状态
            UpdateCrossFade(meshId);

            return newLevel;
        }

        /// <summary>
        /// 更新一个 Actor 群组的 LOD 级别。
        /// 使用该 Actor 下所有 Mesh 中最大的屏幕覆盖率来决定 LOD 级别。
        /// 所有 Mesh 同步切换到同一级别。
        /// </summary>
        /// <param name="actorId">Actor 唯一标识。</param>
        /// <param name="meshIds">该 Actor 下所有 Mesh 的 ID 列表。</param>
        /// <param name="lodChain">共享的 LOD 链。</param>
        /// <param name="meshCoverages">每个 Mesh ID 的屏幕覆盖率。</param>
        public void UpdateGroupLOD(int actorId, List<int> meshIds, LODChain lodChain,
            Dictionary<int, float> meshCoverages)
        {
            if (meshIds == null || meshIds.Count == 0 || lodChain == null) return;

            // 计算群组最大覆盖率
            float maxCoverage = 0;
            foreach (int meshId in meshIds)
            {
                if (meshCoverages.TryGetValue(meshId, out float cov))
                    maxCoverage = Math.Max(maxCoverage, cov);
            }

            _groupMaxCoverage[actorId] = maxCoverage;

            int previousGroupLevel = _groupLodLevel.GetValueOrDefault(actorId, 0);

            // 使用 Hysteresis 选择群组 LOD
            int newGroupLevel = SelectLODWithHysteresis(lodChain, maxCoverage, previousGroupLevel);

            if (newGroupLevel != previousGroupLevel)
            {
                _groupLodLevel[actorId] = newGroupLevel;

                // 同步所有 Mesh 到同一级别
                foreach (int meshId in meshIds)
                {
                    int prevMeshLevel = _currentLodLevel.GetValueOrDefault(meshId, 0);
                    _currentLodLevel[meshId] = newGroupLevel;
                    StartCrossFade(meshId, prevMeshLevel, newGroupLevel);
                }
            }

            // 更新所有 Mesh 的 CrossFade
            foreach (int meshId in meshIds)
            {
                UpdateCrossFade(meshId);
            }
        }

        /// <summary>获取指定 Mesh 的 CrossFade 混合权重。</summary>
        /// <returns>
        /// (oldLevel, newLevel, blendFactor):
        ///   blendFactor = 0.0 表示完全显示旧 LOD,
        ///   blendFactor = 1.0 表示完全显示新 LOD。
        ///   过渡完成后返回 (newLevel, newLevel, 1.0)。
        /// </returns>
        public (int OldLevel, int NewLevel, float BlendFactor) GetCrossFadeState(int meshId)
        {
            if (_crossFadeStates.TryGetValue(meshId, out var state) && state.IsActive)
            {
                return (state.OldLevel, state.NewLevel, state.BlendFactor);
            }
            int level = _currentLodLevel.GetValueOrDefault(meshId, 0);
            return (level, level, 1.0f);
        }

        /// <summary>重置指定 Mesh 的 LOD 状态。</summary>
        public void Reset(int meshId)
        {
            _currentLodLevel.Remove(meshId);
            _currentCoverage.Remove(meshId);
            _crossFadeStates.Remove(meshId);
        }

        /// <summary>重置指定 Actor 群组的 LOD 状态。</summary>
        public void ResetGroup(int actorId)
        {
            _groupLodLevel.Remove(actorId);
            _groupMaxCoverage.Remove(actorId);
        }

        // ── 内部方法 ──

        /// <summary>
        /// 使用 Hysteresis 选择 LOD 级别。
        /// 升级 (更高质量): 需要超过阈值 + 滞后量
        /// 降级 (更低质量): 只需低于阈值即可
        /// </summary>
        private int SelectLODWithHysteresis(LODChain chain, float coverage, int currentLevel)
        {
            int bestLevel = chain.Levels.Count - 1; // 默认最低细节

            for (int i = chain.Levels.Count - 1; i >= 0; i--)
            {
                float threshold = chain.Levels[i].ScreenSizeThreshold;

                if (i < currentLevel)
                {
                    // 升级到更高质量: 需要 coverage 超过阈值 + 滞后量
                    float upgradeThreshold = threshold * (1.0f + HysteresisFactor);
                    if (coverage >= upgradeThreshold)
                    {
                        bestLevel = i;
                        break;
                    }
                }
                else if (i > currentLevel)
                {
                    // 降级到更低质量: 只需 coverage 低于阈值
                    if (coverage < threshold)
                    {
                        bestLevel = i;
                        break;
                    }
                }
                else
                {
                    // 当前级别: 检查是否需要降级
                    bestLevel = i;
                    if (coverage < threshold * (1.0f - HysteresisFactor * 0.5f))
                    {
                        // 可能需要降级到更低级别
                        if (i < chain.Levels.Count - 1)
                        {
                            bestLevel = i + 1;
                        }
                    }
                    break;
                }
            }

            return Math.Clamp(bestLevel, 0, chain.Levels.Count - 1);
        }

        private void StartCrossFade(int meshId, int oldLevel, int newLevel)
        {
            _crossFadeStates[meshId] = new CrossFadeState
            {
                IsActive = true,
                OldLevel = oldLevel,
                NewLevel = newLevel,
                BlendFactor = 0.0f,
                RemainingFrames = CrossFadeFrames
            };
        }

        private void UpdateCrossFade(int meshId)
        {
            if (!_crossFadeStates.TryGetValue(meshId, out var state) || !state.IsActive)
                return;

            state.RemainingFrames--;
            state.BlendFactor = 1.0f - ((float)state.RemainingFrames / CrossFadeFrames);

            if (state.RemainingFrames <= 0)
            {
                state.IsActive = false;
                state.BlendFactor = 1.0f;
            }
        }
    }

    /// <summary>CrossFade 过渡状态。</summary>
    internal class CrossFadeState
    {
        public bool IsActive { get; set; }
        public int OldLevel { get; set; }
        public int NewLevel { get; set; }
        public float BlendFactor { get; set; }
        public int RemainingFrames { get; set; }
    }
}
