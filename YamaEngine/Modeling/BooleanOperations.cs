// =====================================================================================
//  YamaEngine.Modeling.BooleanOperations
//  网格布尔运算系统 (BVH 加速) (Mesh Boolean Operations with BVH Acceleration)
// -------------------------------------------------------------------------------------
//  与已有 MeshBooleanSystem.cs 共存, 提供 BVH 加速版本的布尔运算实现。
//
//  核心管线:
//    1. BuildBVH:          为两个网格构建层次包围盒 (BVH) 加速空间查询
//    2. FindIntersections:  BVH 宽相排除 + Möller-Trumbore 边-三角形精确检测, 找到所有相交三角形对
//    3. SplitTriangles:    沿相交平面分割三角形, 使子三角形不再跨越对方表面
//    4. ClassifyTriangles: 用质心做 BVH 加速射线点内/外测试, 分类子三角形 (Inside/Outside)
//    5. MergeResult:       按运算类型 (Union/Intersect/Subtract) 合并结果
//    6. FixTopology:       合并重复顶点 / 重算法线 / 修复拓扑
//
//  类型命名使用 MeshBooleanOp / BooleanProcessor / BoolBVH 等,
//  避免与 MeshBooleanSystem.cs 中的 BooleanOperation / MeshBoolean 等已有类型冲突。
//
//  对接的已有系统 API (YamaEngine.Modeling.EditableMesh):
//    - List<Vector3> Vertices / Normals / UV0
//    - List<MeshFace> Faces (MeshFace 为 readonly record struct, 含 int A,B,C)
//    - void RecalculateNormals()
//    - int  MergeDuplicateVertices(float threshold = 0.0001f)
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.Modeling
{
    // =================================================================================
    //  枚举: MeshBooleanOp / TriClassification
    // =================================================================================

    /// <summary>
    /// 布尔运算类型 —— 与 MeshBooleanSystem.cs 中的 BooleanOperation 区别命名。
    /// 描述两个网格 (A 与 B) 之间的实体布尔组合方式。
    /// </summary>
    public enum MeshBooleanOp
    {
        /// <summary>并集 A∪B: 保留 A 在 B 外部的三角形 + B 在 A 外部的三角形。</summary>
        Union = 0,

        /// <summary>交集 A∩B: 保留 A 在 B 内部的三角形 + B 在 A 内部的三角形。</summary>
        Intersect = 1,

        /// <summary>差集 A-B: 保留 A 在 B 外部的三角形 + B 在 A 内部的三角形 (翻转法线)。</summary>
        Subtract = 2
    }

    /// <summary>
    /// 三角形分类结果 —— 子三角形相对于另一个网格的位置关系。
    /// </summary>
    public enum TriClassification
    {
        /// <summary>在另一个网格内部。</summary>
        Inside = 0,

        /// <summary>在另一个网格外部。</summary>
        Outside = 1,

        /// <summary>在边界上 (无法确定, 默认保留)。</summary>
        Boundary = 2
    }

    // =================================================================================
    //  工作顶点 / 工作三角形: BoolVert / BoolTri
    //  与 MeshBooleanSystem.cs 中的 internal BoolVertex / BoolTriangle 区别命名。
    // =================================================================================

    /// <summary>
    /// 布尔运算工作顶点 —— 在切分过程中携带位置 / 法线 / UV,
    /// 切分产生的新顶点通过线性插值继承原始属性。
    /// </summary>
    public struct BoolVert
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;

        public BoolVert(Vector3 pos, Vector3 normal, Vector2 uv)
        {
            Position = pos;
            Normal = normal;
            UV = uv;
        }

        /// <summary>在两个顶点之间线性插值 (用于切分时生成交点顶点)。</summary>
        public static BoolVert Lerp(BoolVert a, BoolVert b, float t)
        {
            var n = a.Normal + t * (b.Normal - a.Normal);
            if (n.LengthSquared() > 1e-12f)
                n = Vector3.Normalize(n);
            else
                n = a.Normal;
            return new BoolVert
            {
                Position = a.Position + t * (b.Position - a.Position),
                Normal = n,
                UV = a.UV + t * (b.UV - a.UV)
            };
        }
    }

    /// <summary>
    /// 布尔运算工作三角形 —— 三个 BoolVert, 携带完整属性。
    /// </summary>
    public struct BoolTri
    {
        public BoolVert A, B, C;

        /// <summary>三角形质心位置 (用于点在网格内分类)。</summary>
        public Vector3 Centroid => (A.Position + B.Position + C.Position) / 3f;

        /// <summary>三角形面法线 (按叉积计算并归一化)。</summary>
        public Vector3 FaceNormal
        {
            get
            {
                var n = Vector3.Cross(B.Position - A.Position, C.Position - A.Position);
                var len = n.Length();
                return len > 1e-12f ? n / len : Vector3.UnitY;
            }
        }

        public BoolTri(BoolVert a, BoolVert b, BoolVert c)
        {
            A = a; B = b; C = c;
        }
    }

    /// <summary>
    /// 三角形预计算数据 —— 用于 BVH 构建和查询, 缓存平面方程和 AABB。
    /// </summary>
    public struct BoolTriData
    {
        /// <summary>三角形在原始列表中的索引。</summary>
        public int SourceIndex;

        /// <summary>三个顶点位置。</summary>
        public Vector3 V0, V1, V2;

        /// <summary>平面方程 ax+by+cz+d=0, (a,b,c) 为单位法线。</summary>
        public Vector4 Plane;

        /// <summary>AABB 最小/最大角。</summary>
        public Vector3 Min, Max;

        /// <summary>质心。</summary>
        public Vector3 Centroid;

        /// <summary>从三个顶点构建预计算数据。</summary>
        public static BoolTriData Create(int sourceIndex, Vector3 v0, Vector3 v1, Vector3 v2)
        {
            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            var len = normal.Length();
            var plane = len < BoolMathUtil.Epsilon
                ? Vector4.Zero
                : new Vector4(normal / len, -Vector3.Dot(normal / len, v0));
            return new BoolTriData
            {
                SourceIndex = sourceIndex,
                V0 = v0, V1 = v1, V2 = v2,
                Plane = plane,
                Min = Vector3.Min(Vector3.Min(v0, v1), v2),
                Max = Vector3.Max(Vector3.Max(v0, v1), v2),
                Centroid = (v0 + v1 + v2) / 3f
            };
        }
    }

    // =================================================================================
    //  BoolMathUtil — 布尔运算底层几何数学工具
    //  与 MeshBooleanSystem.cs 中的 BooleanMath 区别命名。
    // =================================================================================

    /// <summary>
    /// 布尔运算所需的底层几何数学工具集合。
    /// </summary>
    public static class BoolMathUtil
    {
        /// <summary>全局几何 epsilon。</summary>
        public const float Epsilon = 1e-6f;

        /// <summary>
        /// 从三角形三个顶点构建平面方程 ax+by+cz+d=0。
        /// 返回 Vector4(a,b,c,d), 其中 (a,b,c) 为单位法线。
        /// 退化三角形返回 Vector4.Zero。
        /// </summary>
        public static Vector4 PlaneFromTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Cross(b - a, c - a);
            var len = normal.Length();
            if (len < Epsilon) return Vector4.Zero;
            normal /= len;
            return new Vector4(normal, -Vector3.Dot(normal, a));
        }

        /// <summary>点到平面的有符号距离。</summary>
        public static float SignedDistanceToPlane(Vector3 p, Vector4 plane)
        {
            return plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W;
        }

        /// <summary>
        /// 射线-三角形相交检测 (Möller-Trumbore 算法)。
        /// 返回是否相交, 以及相交参数 t (命中点 = origin + t*dir)。
        /// 仅当 t > Epsilon 时视为有效命中。
        /// </summary>
        public static bool RayTriangleIntersect(Vector3 origin, Vector3 dir,
                                                 Vector3 v0, Vector3 v1, Vector3 v2,
                                                 out float t)
        {
            t = 0f;
            var edge1 = v1 - v0;
            var edge2 = v2 - v0;
            var h = Vector3.Cross(dir, edge2);
            float det = Vector3.Dot(edge1, h);
            if (det > -Epsilon && det < Epsilon) return false; // 平行
            float invDet = 1f / det;
            var s = origin - v0;
            float u = invDet * Vector3.Dot(s, h);
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, edge1);
            float v = invDet * Vector3.Dot(dir, q);
            if (v < 0f || u + v > 1f) return false;
            t = invDet * Vector3.Dot(edge2, q);
            return t > Epsilon;
        }

        /// <summary>
        /// 线段-三角形相交检测 (Möller-Trumbore 变体, 限制 t 在 [0,1] 范围内)。
        /// segStart + t*(segEnd-segStart) 为命中点, t∈[0,1] 表示在线段内。
        /// </summary>
        public static bool SegmentTriangleIntersect(Vector3 segStart, Vector3 segEnd,
                                                     Vector3 v0, Vector3 v1, Vector3 v2,
                                                     out float t, out Vector3 hitPoint)
        {
            var dir = segEnd - segStart;
            t = 0f;
            hitPoint = Vector3.Zero;
            if (RayTriangleIntersect(segStart, dir, v0, v1, v2, out t) && t >= 0f && t <= 1f)
            {
                hitPoint = segStart + t * dir;
                return true;
            }
            return false;
        }

        /// <summary>计算三角形的 AABB。</summary>
        public static void TriangleAABB(Vector3 a, Vector3 b, Vector3 c, out Vector3 min, out Vector3 max)
        {
            min = Vector3.Min(Vector3.Min(a, b), c);
            max = Vector3.Max(Vector3.Max(a, b), c);
        }

        /// <summary>两个 AABB 是否重叠。</summary>
        public static bool AABBOverlap(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB, float eps = 0f)
        {
            return minA.X <= maxB.X + eps && maxA.X >= minB.X - eps &&
                   minA.Y <= maxB.Y + eps && maxA.Y >= minB.Y - eps &&
                   minA.Z <= maxB.Z + eps && maxA.Z >= minB.Z - eps;
        }

        /// <summary>
        /// 射线-AABB 相交检测 (Slab 方法)。
        /// 返回是否相交以及相交区间 [tmin, tmax]。
        /// </summary>
        public static bool RayAABBIntersect(Vector3 origin, Vector3 invDir,
                                             Vector3 min, Vector3 max,
                                             out float tmin, out float tmax)
        {
            tmin = 0f;
            tmax = float.MaxValue;

            // X 轴 slab
            float t1 = (min.X - origin.X) * invDir.X;
            float t2 = (max.X - origin.X) * invDir.X;
            tmin = Math.Max(tmin, Math.Min(t1, t2));
            tmax = Math.Min(tmax, Math.Max(t1, t2));

            // Y 轴 slab
            t1 = (min.Y - origin.Y) * invDir.Y;
            t2 = (max.Y - origin.Y) * invDir.Y;
            tmin = Math.Max(tmin, Math.Min(t1, t2));
            tmax = Math.Min(tmax, Math.Max(t1, t2));

            // Z 轴 slab
            t1 = (min.Z - origin.Z) * invDir.Z;
            t2 = (max.Z - origin.Z) * invDir.Z;
            tmin = Math.Max(tmin, Math.Min(t1, t2));
            tmax = Math.Min(tmax, Math.Max(t1, t2));

            return tmax >= tmin && tmax >= 0f;
        }

        /// <summary>取 Vector3 第 i 个分量 (i=0,1,2)。</summary>
        public static float GetComponent(Vector3 v, int i)
        {
            return i == 0 ? v.X : (i == 1 ? v.Y : v.Z);
        }

        /// <summary>将点投影到平面上。</summary>
        public static Vector3 ProjectToPlane(Vector3 p, Vector4 plane)
        {
            float d = SignedDistanceToPlane(p, plane);
            var n = new Vector3(plane.X, plane.Y, plane.Z);
            return p - n * d;
        }

        /// <summary>对点列表按距离去重。</summary>
        public static void DedupPoints(List<Vector3> pts, float eps)
        {
            float sq = eps * eps;
            for (int i = pts.Count - 1; i >= 1; i--)
            {
                for (int j = 0; j < i; j++)
                {
                    if (Vector3.DistanceSquared(pts[i], pts[j]) < sq)
                    {
                        pts.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }

    // =================================================================================
    //  BoolTriIntersection — 三角形-三角形相交检测 (Möller-Trumbore 边变体)
    //  与 MeshBooleanSystem.cs 中的 TriangleTriangleIntersection 区别命名。
    // -------------------------------------------------------------------------------------
    //  算法: 对三角形 A 的每条边 (作为线段), 用 Möller-Trumbore 检测与三角形 B 的相交;
    //        对三角形 B 的每条边同理检测与三角形 A 的相交。收集所有交点,
    //        去重后若得到 2 个点则构成交线段。
    // =================================================================================

    /// <summary>
    /// 三角形-三角形相交检测 —— 基于 Möller-Trumbore 的边-三角形测试变体。
    /// 将每个三角形的 3 条边作为线段, 与另一个三角形做精确相交检测,
    /// 收集所有交点后去重, 得到交线段。
    /// </summary>
    public static class BoolTriIntersection
    {
        /// <summary>
        /// 检测两个三角形是否相交。
        /// </summary>
        public static bool Intersect(Vector3 a0, Vector3 a1, Vector3 a2,
                                     Vector3 b0, Vector3 b1, Vector3 b2)
        {
            return FindIntersection(a0, a1, a2, b0, b1, b2, out _, out _);
        }

        /// <summary>
        /// 计算两个三角形的交线段。
        /// 通过测试一个三角形的每条边与另一个三角形的相交, 收集交点。
        /// 若得到 2 个或更多交点, 取最远的两个作为交线段端点。
        /// </summary>
        /// <param name="segStart">交线段起点 (仅当返回 true 时有效)。</param>
        /// <param name="segEnd">交线段终点 (仅当返回 true 时有效)。</param>
        /// <returns>是否相交。</returns>
        public static bool FindIntersection(Vector3 a0, Vector3 a1, Vector3 a2,
                                            Vector3 b0, Vector3 b1, Vector3 b2,
                                            out Vector3 segStart, out Vector3 segEnd)
        {
            segStart = Vector3.Zero;
            segEnd = Vector3.Zero;
            float eps = BoolMathUtil.Epsilon;

            // 先用平面同侧测试快速排除
            var planeB = BoolMathUtil.PlaneFromTriangle(b0, b1, b2);
            if (planeB == Vector4.Zero) return false;
            float da0 = BoolMathUtil.SignedDistanceToPlane(a0, planeB);
            float da1 = BoolMathUtil.SignedDistanceToPlane(a1, planeB);
            float da2 = BoolMathUtil.SignedDistanceToPlane(a2, planeB);
            if (da0 > eps && da1 > eps && da2 > eps) return false;
            if (da0 < -eps && da1 < -eps && da2 < -eps) return false;

            var planeA = BoolMathUtil.PlaneFromTriangle(a0, a1, a2);
            if (planeA == Vector4.Zero) return false;
            float db0 = BoolMathUtil.SignedDistanceToPlane(b0, planeA);
            float db1 = BoolMathUtil.SignedDistanceToPlane(b1, planeA);
            float db2 = BoolMathUtil.SignedDistanceToPlane(b2, planeA);
            if (db0 > eps && db1 > eps && db2 > eps) return false;
            if (db0 < -eps && db1 < -eps && db2 < -eps) return false;

            // 收集交点: A 的 3 条边 vs B, B 的 3 条边 vs A
            var points = new List<Vector3>();
            CollectEdgeTriHits(a0, a1, b0, b1, b2, points);
            CollectEdgeTriHits(a1, a2, b0, b1, b2, points);
            CollectEdgeTriHits(a2, a0, b0, b1, b2, points);
            CollectEdgeTriHits(b0, b1, a0, a1, a2, points);
            CollectEdgeTriHits(b1, b2, a0, a1, a2, points);
            CollectEdgeTriHits(b2, b0, a0, a1, a2, points);

            BoolMathUtil.DedupPoints(points, eps * 10f);
            if (points.Count < 2) return points.Count > 0; // 顶点接触

            // 取距离最远的两个点作为交线段端点
            float maxDistSq = 0f;
            int i0 = 0, i1 = 1;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    float d = Vector3.DistanceSquared(points[i], points[j]);
                    if (d > maxDistSq)
                    {
                        maxDistSq = d;
                        i0 = i;
                        i1 = j;
                    }
                }
            }

            segStart = points[i0];
            segEnd = points[i1];
            return true;
        }

        /// <summary>
        /// 测试线段 (e0→e1) 与三角形 (t0,t1,t2) 的相交, 若相交则将交点加入 points 列表。
        /// 使用 Möller-Trumbore 射线-三角形算法, 限制参数 t 在 [0,1] 范围内。
        /// </summary>
        private static void CollectEdgeTriHits(Vector3 e0, Vector3 e1,
                                                Vector3 t0, Vector3 t1, Vector3 t2,
                                                List<Vector3> points)
        {
            if (BoolMathUtil.SegmentTriangleIntersect(e0, e1, t0, t1, t2, out _, out var hit))
            {
                points.Add(hit);
            }
        }
    }

    // =================================================================================
    //  BoolBVH — 层次包围盒 (Bounding Volume Hierarchy)
    //  用于加速三角形相交检测 (宽相排除) 和射线查询 (点在网格内测试)。
    // -------------------------------------------------------------------------------------
    //  构建: 自顶向下递归, 按最长轴的中位数分割。
    //  查询: AABB 重叠查询 (找相交三角形对) + 射线查询 (点在网格内测试)。
    // =================================================================================

    /// <summary>
    /// BVH 节点 —— 包含 AABB 包围盒, 叶子节点持有三角形索引, 内部节点持有左右子节点。
    /// </summary>
    public class BoolBVHNode
    {
        /// <summary>AABB 最小角。</summary>
        public Vector3 Min = new(float.MaxValue);

        /// <summary>AABB 最大角。</summary>
        public Vector3 Max = new(float.MinValue);

        /// <summary>叶子节点的三角形索引 (-1 表示内部节点)。</summary>
        public int TriangleIndex = -1;

        /// <summary>左子节点。</summary>
        public BoolBVHNode? Left;

        /// <summary>右子节点。</summary>
        public BoolBVHNode? Right;

        /// <summary>是否为叶子节点。</summary>
        public bool IsLeaf => Left == null && Right == null;
    }

    /// <summary>
    /// 层次包围盒 (BVH) —— 加速三角形空间查询。
    /// 构建: 自顶向下递归, 每次选择最长轴, 按质心中位数分割。
    /// 查询: AABB 重叠 (找相交三角形对) + 射线投射 (点在网格内测试)。
    /// </summary>
    public class BoolBVH
    {
        private BoolBVHNode? _root;
        private readonly List<BoolTriData> _triangles;

        /// <summary>BVH 中的三角形数据列表 (只读访问)。</summary>
        public IReadOnlyList<BoolTriData> Triangles => _triangles;

        /// <summary>根节点。</summary>
        public BoolBVHNode? Root => _root;

        /// <summary>
        /// 从三角形数据列表构建 BVH。
        /// </summary>
        public BoolBVH(List<BoolTriData> triangles)
        {
            _triangles = triangles ?? new List<BoolTriData>();
            if (_triangles.Count > 0)
                _root = BuildRecursive(_triangles, 0, _triangles.Count, 0);
        }

        /// <summary>
        /// 递归构建 BVH。
        /// 选择当前三角形集合 AABB 的最长轴, 按质心中位数分割为左右子集。
        /// 当子集大小 <= 1 时创建叶子节点。
        /// </summary>
        private BoolBVHNode BuildRecursive(List<BoolTriData> tris, int start, int count, int depth)
        {
            var node = new BoolBVHNode();

            // 计算 AABB
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                min = Vector3.Min(min, tris[i].Min);
                max = Vector3.Max(max, tris[i].Max);
            }
            node.Min = min;
            node.Max = max;

            // 叶子节点
            if (count <= 1)
            {
                node.TriangleIndex = tris[start].SourceIndex;
                return node;
            }

            // 选择最长轴
            var extent = max - min;
            int axis = 0;
            if (extent.Y > extent.X) axis = 1;
            if (extent.Z > GetComponent(extent, axis)) axis = 2;

            // 按质心沿最长轴排序 (只排序当前子范围)
            var subset = tris.GetRange(start, count);
            subset.Sort((a, b) =>
                GetComponent(a.Centroid, axis).CompareTo(GetComponent(b.Centroid, axis)));

            // 写回排序后的子集
            for (int i = 0; i < count; i++)
                tris[start + i] = subset[i];

            // 中位数分割
            int mid = count / 2;
            node.Left = BuildRecursive(tris, start, mid, depth + 1);
            node.Right = BuildRecursive(tris, start + mid, count - mid, depth + 1);
            return node;
        }

        private static float GetComponent(Vector3 v, int i)
        {
            return i == 0 ? v.X : (i == 1 ? v.Y : v.Z);
        }

        /// <summary>
        /// 查询与给定 AABB 重叠的所有三角形索引。
        /// 递归遍历 BVH, 仅访问 AABB 重叠的节点。
        /// </summary>
        public List<int> QueryOverlap(Vector3 queryMin, Vector3 queryMax)
        {
            var results = new List<int>();
            if (_root != null)
                QueryOverlapRecursive(_root, queryMin, queryMax, results);
            return results;
        }

        private void QueryOverlapRecursive(BoolBVHNode node, Vector3 qMin, Vector3 qMax, List<int> results)
        {
            if (!BoolMathUtil.AABBOverlap(node.Min, node.Max, qMin, qMax))
                return;

            if (node.IsLeaf)
            {
                results.Add(node.TriangleIndex);
                return;
            }

            if (node.Left != null) QueryOverlapRecursive(node.Left, qMin, qMax, results);
            if (node.Right != null) QueryOverlapRecursive(node.Right, qMin, qMax, results);
        }

        /// <summary>
        /// 射线投射 —— 返回射线与 BVH 中所有三角形的命中列表 (三角形索引, 命中参数 t)。
        /// 用于点在网格内测试 (统计命中数, 奇数=内部)。
        /// </summary>
        public List<(int TriIndex, float T)> RayCast(Vector3 origin, Vector3 dir)
        {
            var hits = new List<(int, float)>();
            if (_root == null) return hits;

            var invDir = new Vector3(
                Math.Abs(dir.X) > 1e-20f ? 1f / dir.X : 1e20f,
                Math.Abs(dir.Y) > 1e-20f ? 1f / dir.Y : 1e20f,
                Math.Abs(dir.Z) > 1e-20f ? 1f / dir.Z : 1e20f);

            RayCastRecursive(_root, origin, dir, invDir, hits);
            return hits;
        }

        private void RayCastRecursive(BoolBVHNode node, Vector3 origin, Vector3 dir, Vector3 invDir,
                                       List<(int, float)> hits)
        {
            if (!BoolMathUtil.RayAABBIntersect(origin, invDir, node.Min, node.Max, out _, out _))
                return;

            if (node.IsLeaf)
            {
                // 查找该三角形的预计算数据
                var triData = FindTriData(node.TriangleIndex);
                if (triData.HasValue &&
                    BoolMathUtil.RayTriangleIntersect(origin, dir,
                        triData.Value.V0, triData.Value.V1, triData.Value.V2, out float t))
                {
                    hits.Add((node.TriangleIndex, t));
                }
                return;
            }

            if (node.Left != null) RayCastRecursive(node.Left, origin, dir, invDir, hits);
            if (node.Right != null) RayCastRecursive(node.Right, origin, dir, invDir, hits);
        }

        private BoolTriData? FindTriData(int sourceIndex)
        {
            foreach (var td in _triangles)
                if (td.SourceIndex == sourceIndex) return td;
            return null;
        }

        /// <summary>获取整个 BVH 的包围盒。</summary>
        public (Vector3 Min, Vector3 Max) GetBounds()
        {
            if (_root == null) return (Vector3.Zero, Vector3.Zero);
            return (_root.Min, _root.Max);
        }
    }

    // =================================================================================
    //  BoolTriSplitter — 三角形切分器
    //  与 MeshBooleanSystem.cs 中的 MeshSplitter 区别命名。
    // -------------------------------------------------------------------------------------
    //  沿平面集合递归切分三角形, 使每个子三角形不再跨越任何平面。
    //  使用 Sutherland-Hodgman 变体: 每个平面将多边形分为正侧和负侧, 两侧都保留。
    // =================================================================================

    /// <summary>
    /// 三角形切分器 —— 沿平面集合递归切分三角形, 使每个子三角形不再跨越任何平面。
    /// 算法: 对每个平面, 用 Sutherland-Hodgman 将多边形分为正侧和负侧两部分,
    /// 两部分继续用后续平面切分, 最终三角化所有子多边形。
    /// </summary>
    public static class BoolTriSplitter
    {
        /// <summary>
        /// 用平面集合切分一个三角形, 返回切分后的子三角形列表。
        /// 若无需切分 (planes 为空), 返回包含原始三角形的单元素列表。
        /// </summary>
        public static List<BoolTri> SplitTriangle(BoolTri tri, List<Vector4> planes, float eps)
        {
            if (planes == null || planes.Count == 0)
                return new List<BoolTri> { tri };

            // 构建初始多边形 (3 个顶点)
            var initialPoly = new List<BoolVert> { tri.A, tri.B, tri.C };

            // 递归切分
            var subPolys = new List<List<BoolVert>>();
            SplitRecursive(initialPoly, planes, 0, eps, subPolys);

            // 三角化每个子多边形
            var result = new List<BoolTri>();
            foreach (var poly in subPolys)
            {
                if (poly.Count < 3) continue;
                result.AddRange(Triangulate(poly));
            }
            return result;
        }

        /// <summary>
        /// 递归地用平面集合切分多边形。
        /// 每个平面将多边形分为正侧和负侧, 两部分继续用后续平面切分。
        /// </summary>
        private static void SplitRecursive(List<BoolVert> polygon, List<Vector4> planes,
                                            int planeIndex, float eps,
                                            List<List<BoolVert>> results)
        {
            if (polygon.Count < 3) return;
            if (planeIndex >= planes.Count)
            {
                results.Add(polygon);
                return;
            }

            var plane = planes[planeIndex];
            var (posPoly, negPoly) = SplitByPlane(polygon, plane, eps);

            if (posPoly.Count >= 3)
                SplitRecursive(posPoly, planes, planeIndex + 1, eps, results);
            if (negPoly.Count >= 3)
                SplitRecursive(negPoly, planes, planeIndex + 1, eps, results);
        }

        /// <summary>
        /// 用单个平面将多边形切分为正侧 (pos) 和负侧 (neg) 两部分。
        /// 跨越平面的边在交点处插入新顶点 (通过插值继承属性)。
        /// 平面上的顶点同时属于两侧。
        /// </summary>
        private static (List<BoolVert> pos, List<BoolVert> neg) SplitByPlane(
            List<BoolVert> polygon, Vector4 plane, float eps)
        {
            var pos = new List<BoolVert>();
            var neg = new List<BoolVert>();
            int n = polygon.Count;

            for (int i = 0; i < n; i++)
            {
                var cur = polygon[i];
                var prev = polygon[(i - 1 + n) % n];
                float dCur = BoolMathUtil.SignedDistanceToPlane(cur.Position, plane);
                float dPrev = BoolMathUtil.SignedDistanceToPlane(prev.Position, plane);

                bool prevPos = dPrev > eps;
                bool prevNeg = dPrev < -eps;
                bool curPos = dCur > eps;
                bool curNeg = dCur < -eps;
                bool curOn = !curPos && !curNeg;

                // 跨越平面 (严格异侧) -> 计算交点, 加入两侧
                if ((prevPos && curNeg) || (prevNeg && curPos))
                {
                    float t = dPrev / (dPrev - dCur);
                    var mid = BoolVert.Lerp(prev, cur, t);
                    pos.Add(mid);
                    neg.Add(mid);
                }

                // 当前顶点归属
                if (curPos)
                {
                    pos.Add(cur);
                }
                else if (curNeg)
                {
                    neg.Add(cur);
                }
                else // 顶点在平面上 -> 两侧都加
                {
                    pos.Add(cur);
                    neg.Add(cur);
                }
            }

            return (pos, neg);
        }

        /// <summary>扇形三角化凸多边形 (切分产生的子多边形均为凸)。</summary>
        private static List<BoolTri> Triangulate(List<BoolVert> poly)
        {
            var tris = new List<BoolTri>(poly.Count - 2);
            for (int i = 1; i < poly.Count - 1; i++)
            {
                tris.Add(new BoolTri(poly[0], poly[i], poly[i + 1]));
            }
            return tris;
        }
    }

    // =================================================================================
    //  BoolRaycaster — BVH 加速的射线点内/外测试
    //  与 MeshBooleanSystem.cs 中的 PointInMeshTest 区别命名。
    // -------------------------------------------------------------------------------------
    //  从测试点发射射线, 用 BVH 加速统计与网格三角形的命中次数。
    //  奇数次命中 = 在网格内部, 偶数次 = 在外部。
    //  为提升鲁棒性, 尝试多个方向并跳过退化命中。
    // =================================================================================

    /// <summary>
    /// BVH 加速的射线点内/外测试 —— 用于布尔运算中的三角形分类。
    /// </summary>
    public static class BoolRaycaster
    {
        // 不易与三角形平面平行的测试方向
        private static readonly Vector3[] TestDirections =
        {
            Vector3.Normalize(new Vector3(1f, 2f, 3f)),
            Vector3.Normalize(new Vector3(3f, 1f, 2f)),
            Vector3.Normalize(new Vector3(2f, 3f, 1f)),
            Vector3.Normalize(new Vector3(1f, 1f, 1f)),
            Vector3.Normalize(new Vector3(1f, -1f, 2f)),
            Vector3.Normalize(new Vector3(-2f, 1f, 3f)),
            Vector3.Normalize(new Vector3(3f, -2f, 1f)),
        };

        /// <summary>
        /// 测试点 point 是否在网格内部 (使用 BVH 加速射线投射)。
        /// 从测试点发射射线, 用 BVH 统计与三角形的命中次数, 奇数次 = 内部。
        /// 尝试多个方向以避免退化命中。
        /// </summary>
        public static bool IsInside(Vector3 point, BoolBVH bvh)
        {
            if (bvh == null || bvh.Triangles.Count == 0) return false;

            foreach (var dir in TestDirections)
            {
                var hits = bvh.RayCast(point, dir);

                // 检查退化 (命中参数过小, 可能恰好在表面上)
                bool degenerate = false;
                int count = 0;
                foreach (var (_, t) in hits)
                {
                    if (t < 1e-4f)
                    {
                        degenerate = true;
                        break;
                    }
                    count++;
                }

                if (degenerate) continue;

                // 奇数次命中 = 在内部
                return (count & 1) == 1;
            }

            return false;
        }

        /// <summary>
        /// 批量分类: 测试一组三角形质心是否在另一个网格内部。
        /// 返回每个三角形的分类结果 (Inside/Outside)。
        /// </summary>
        public static TriClassification[] ClassifyTriangles(List<BoolTri> triangles, BoolBVH otherBVH)
        {
            var result = new TriClassification[triangles.Count];
            for (int i = 0; i < triangles.Count; i++)
            {
                var centroid = triangles[i].Centroid;
                bool inside = IsInside(centroid, otherBVH);
                result[i] = inside ? TriClassification.Inside : TriClassification.Outside;
            }
            return result;
        }
    }

    // =================================================================================
    //  BooleanConfig — 布尔运算配置
    // =================================================================================

    /// <summary>
    /// 布尔运算配置 —— 控制 epsilon / 顶点焊接 / 法线重算等后处理选项。
    /// </summary>
    public class BooleanConfig
    {
        /// <summary>几何 epsilon, 用于判定点是否在平面上 / 距离是否为零。</summary>
        public float Epsilon = 1e-6f;

        /// <summary>顶点合并阈值 (距离小于该值的顶点视为同一顶点)。</summary>
        public float WeldThreshold = 1e-4f;

        /// <summary>运算后是否重新计算顶点法线。</summary>
        public bool RecalculateNormals = true;

        /// <summary>运算后是否合并重复顶点 (焊接)。</summary>
        public bool MergeVertices = true;

        /// <summary>BVH 最大递归深度 (防止栈溢出)。</summary>
        public int MaxBVHDepth = 64;

        /// <summary>默认配置。</summary>
        public static BooleanConfig Default => new();
    }

    // =================================================================================
    //  BooleanResult — 布尔运算结果
    // =================================================================================

    /// <summary>
    /// 布尔运算结果 —— 包含输出网格和运算过程的统计信息。
    /// </summary>
    public class BooleanResult
    {
        /// <summary>输出网格。</summary>
        public EditableMesh Mesh = new();

        /// <summary>网格 A 的原始三角形数。</summary>
        public int OriginalTrianglesA;

        /// <summary>网格 B 的原始三角形数。</summary>
        public int OriginalTrianglesB;

        /// <summary>网格 A 切分后的三角形数。</summary>
        public int SplitTrianglesA;

        /// <summary>网格 B 切分后的三角形数。</summary>
        public int SplitTrianglesB;

        /// <summary>相交三角形对数。</summary>
        public int IntersectionPairs;

        /// <summary>结果三角形数。</summary>
        public int ResultTriangles;

        /// <summary>结果顶点数。</summary>
        public int ResultVertices;

        /// <summary>运算是否成功。</summary>
        public bool Success = true;

        /// <summary>警告信息列表。</summary>
        public List<string> Warnings = new();

        /// <summary>运算类型。</summary>
        public MeshBooleanOp Operation;
    }

    // =================================================================================
    //  BooleanProcessor — 布尔运算主处理器
    //  与 MeshBooleanSystem.cs 中的 MeshBoolean 区别命名。
    // -------------------------------------------------------------------------------------
    //  完整管线:
    //    1. ConvertToTriangles:   EditableMesh → List<BoolTri>
    //    2. BuildTriangleData:    预计算平面/AABB/质心
    //    3. BuildBVH:             构建层次包围盒
    //    4. FindIntersections:    BVH 宽相 + Möller-Trumbore 精确检测
    //    5. SplitTriangles:       沿相交平面切分三角形
    //    6. ClassifyTriangles:    BVH 加速射线点内/外分类
    //    7. MergeResult:          按运算类型合并结果
    //    8. FixTopology:          合并重复顶点 / 重算法线
    // =================================================================================

    /// <summary>
    /// 布尔运算主处理器 —— 对两个封闭三角网格执行 Union / Intersect / Subtract 运算。
    /// 使用 BVH 加速相交检测和点在网格内测试, 显著提升大规模网格的运算性能。
    /// </summary>
    public class BooleanProcessor
    {
        private readonly BooleanConfig _config;
        private readonly float _eps;

        /// <summary>使用默认配置创建处理器。</summary>
        public BooleanProcessor() : this(BooleanConfig.Default) { }

        /// <summary>使用自定义配置创建处理器。</summary>
        public BooleanProcessor(BooleanConfig config)
        {
            _config = config ?? BooleanConfig.Default;
            _eps = _config.Epsilon;
        }

        // =====================================================================
        //  公开接口
        // =====================================================================

        /// <summary>使用默认配置执行布尔运算。</summary>
        public BooleanResult Execute(EditableMesh meshA, EditableMesh meshB, MeshBooleanOp op)
        {
            return Execute(meshA, meshB, op, _config);
        }

        /// <summary>
        /// 执行完整的布尔运算管线。
        /// </summary>
        public BooleanResult Execute(EditableMesh meshA, EditableMesh meshB,
                                     MeshBooleanOp op, BooleanConfig config)
        {
            var result = new BooleanResult { Operation = op };
            _ = config; // 使用实例 _config

            if (meshA == null) throw new ArgumentNullException(nameof(meshA));
            if (meshB == null) throw new ArgumentNullException(nameof(meshB));

            result.Mesh.Name = $"Boolean_{op}";

            // ---- 退化处理: 空网格 ----
            if (meshA.Faces.Count == 0 && meshB.Faces.Count == 0)
            {
                result.Warnings.Add("两个网格均为空。");
                return result;
            }
            if (meshA.Faces.Count == 0)
            {
                if (op == MeshBooleanOp.Union)
                    CopyMesh(meshB, result.Mesh);
                result.Warnings.Add("网格 A 为空。");
                result.ResultTriangles = result.Mesh.Faces.Count;
                result.ResultVertices = result.Mesh.Vertices.Count;
                return result;
            }
            if (meshB.Faces.Count == 0)
            {
                if (op == MeshBooleanOp.Union || op == MeshBooleanOp.Subtract)
                    CopyMesh(meshA, result.Mesh);
                result.Warnings.Add("网格 B 为空。");
                result.ResultTriangles = result.Mesh.Faces.Count;
                result.ResultVertices = result.Mesh.Vertices.Count;
                return result;
            }

            // ---- 步骤 1: 转换为工作三角形 ----
            var trisA = ConvertToTriangles(meshA);
            var trisB = ConvertToTriangles(meshB);
            result.OriginalTrianglesA = trisA.Count;
            result.OriginalTrianglesB = trisB.Count;

            // ---- 步骤 2: 预计算三角形数据 (平面/AABB/质心) ----
            var dataA = BuildTriangleData(trisA);
            var dataB = BuildTriangleData(trisB);

            // ---- 步骤 3: 构建 BVH ----
            var bvhA = new BoolBVH(dataA);
            var bvhB = new BoolBVH(dataB);

            // ---- 步骤 4: 找到相交三角形对 ----
            var intersectPairs = FindIntersections(dataA, bvhB, dataB, bvhA);
            result.IntersectionPairs = intersectPairs.Count;

            // ---- 步骤 5: 切分三角形 ----
            // 对 A 的每个三角形, 找到与之相交的 B 三角形的平面, 用这些平面切分
            var splitA = SplitTrianglesByBVH(trisA, dataA, bvhB, dataB);
            var splitB = SplitTrianglesByBVH(trisB, dataB, bvhA, dataA);
            result.SplitTrianglesA = splitA.Count;
            result.SplitTrianglesB = splitB.Count;

            // ---- 步骤 6: 分类 ----
            // 对切分后的每个子三角形, 用质心做点在另一个 (原始) 网格内的测试
            var classA = BoolRaycaster.ClassifyTriangles(splitA, bvhB);
            var classB = BoolRaycaster.ClassifyTriangles(splitB, bvhA);

            // ---- 步骤 7: 按运算类型合并结果 ----
            MergeResult(result.Mesh, splitA, classA, splitB, classB, op);
            result.ResultTriangles = result.Mesh.Faces.Count;

            // ---- 步骤 8: 修复拓扑 ----
            FixTopology(result.Mesh, _config);
            result.ResultVertices = result.Mesh.Vertices.Count;

            return result;
        }

        // =====================================================================
        //  步骤 1: ConvertToTriangles — EditableMesh → List<BoolTri>
        // =====================================================================

        /// <summary>将 EditableMesh 转换为工作三角形列表 (携带法线/UV)。</summary>
        private List<BoolTri> ConvertToTriangles(EditableMesh mesh)
        {
            var tris = new List<BoolTri>(mesh.Faces.Count);
            foreach (var f in mesh.Faces)
            {
                if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                tris.Add(new BoolTri(
                    MakeVert(mesh, f.A),
                    MakeVert(mesh, f.B),
                    MakeVert(mesh, f.C)
                ));
            }
            return tris;
        }

        private static BoolVert MakeVert(EditableMesh mesh, int index)
        {
            return new BoolVert(
                mesh.Vertices[index],
                mesh.Normals.Count > index ? mesh.Normals[index] : Vector3.UnitY,
                mesh.UV0.Count > index ? mesh.UV0[index] : Vector2.Zero
            );
        }

        // =====================================================================
        //  步骤 2: BuildTriangleData — 预计算平面/AABB/质心
        // =====================================================================

        /// <summary>为每个三角形预计算平面方程、AABB、质心。</summary>
        private List<BoolTriData> BuildTriangleData(List<BoolTri> tris)
        {
            var data = new List<BoolTriData>(tris.Count);
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                data.Add(BoolTriData.Create(i, t.A.Position, t.B.Position, t.C.Position));
            }
            return data;
        }

        // =====================================================================
        //  步骤 4: FindIntersections — BVH 宽相 + Möller-Trumbore 精确检测
        // =====================================================================

        /// <summary>
        /// 找到两个网格中所有相交的三角形对。
        /// 宽相: 用 BVH 的 AABB 重叠查询快速排除不相交的三角形对。
        /// 窄相: 用 Möller-Trumbore 边-三角形精确检测确认相交。
        /// </summary>
        private List<(int idxA, int idxB)> FindIntersections(
            List<BoolTriData> dataA, BoolBVH bvhB,
            List<BoolTriData> dataB, BoolBVH bvhA)
        {
            var pairs = new List<(int, int)>();
            var tested = new HashSet<(int, int)>();

            // 对 A 的每个三角形, 查询 B 的 BVH 找到 AABB 重叠的候选, 再做精确检测
            for (int i = 0; i < dataA.Count; i++)
            {
                var da = dataA[i];
                if (da.Plane == Vector4.Zero) continue;

                // 宽相: BVH AABB 查询
                var candidates = bvhB.QueryOverlap(da.Min, da.Max);

                foreach (var bTriIdx in candidates)
                {
                    // 找到 B 中的三角形数据
                    var db = FindTriDataBySourceIndex(dataB, bTriIdx);
                    if (db == null) continue;

                    var key = (i, db.Value.SourceIndex);
                    if (tested.Contains(key)) continue;
                    tested.Add(key);

                    // 窄相: Möller-Trumbore 精确检测
                    if (BoolTriIntersection.Intersect(
                            da.V0, da.V1, da.V2,
                            db.Value.V0, db.Value.V1, db.Value.V2))
                    {
                        pairs.Add((i, db.Value.SourceIndex));
                    }
                }
            }

            return pairs;
        }

        private static BoolTriData? FindTriDataBySourceIndex(List<BoolTriData> data, int sourceIndex)
        {
            foreach (var d in data)
                if (d.SourceIndex == sourceIndex) return d;
            return null;
        }

        // =====================================================================
        //  步骤 5: SplitTriangles — 沿相交平面切分三角形
        // =====================================================================

        /// <summary>
        /// 对网格的每个三角形, 找到与之相交的另一个网格的三角形平面,
        /// 用这些平面切分该三角形, 使子三角形不再跨越对方表面。
        /// </summary>
        private List<BoolTri> SplitTrianglesByBVH(
            List<BoolTri> tris, List<BoolTriData> ownData,
            BoolBVH otherBVH, List<BoolTriData> otherData)
        {
            var result = new List<BoolTri>();

            for (int i = 0; i < tris.Count; i++)
            {
                var tri = tris[i];
                var td = ownData[i];
                if (td.Plane == Vector4.Zero)
                {
                    result.Add(tri);
                    continue;
                }

                // 用 BVH 查询与该三角形 AABB 重叠的另一个网格的三角形
                var candidates = otherBVH.QueryOverlap(td.Min, td.Max);

                // 收集需要用来切分的平面 (确实与该三角形相交的另一个网格的三角形平面)
                var splitPlanes = new List<Vector4>();
                foreach (var otherIdx in candidates)
                {
                    var otherTD = FindTriDataBySourceIndex(otherData, otherIdx);
                    if (otherTD == null) continue;
                    if (otherTD.Value.Plane == Vector4.Zero) continue;

                    // AABB 快速排除
                    if (!BoolMathUtil.AABBOverlap(td.Min, td.Max,
                            otherTD.Value.Min, otherTD.Value.Max, _eps))
                        continue;

                    // Möller-Trumbore 精确检测
                    if (!BoolTriIntersection.Intersect(
                            td.V0, td.V1, td.V2,
                            otherTD.Value.V0, otherTD.Value.V1, otherTD.Value.V2))
                        continue;

                    // 仅当三角形确实跨越该平面时才需要切分
                    float d0 = BoolMathUtil.SignedDistanceToPlane(td.V0, otherTD.Value.Plane);
                    float d1 = BoolMathUtil.SignedDistanceToPlane(td.V1, otherTD.Value.Plane);
                    float d2 = BoolMathUtil.SignedDistanceToPlane(td.V2, otherTD.Value.Plane);
                    bool crosses = (d0 > _eps || d1 > _eps || d2 > _eps) &&
                                   (d0 < -_eps || d1 < -_eps || d2 < -_eps);
                    if (!crosses) continue;

                    splitPlanes.Add(otherTD.Value.Plane);
                }

                // 切分三角形
                if (splitPlanes.Count == 0)
                {
                    result.Add(tri);
                }
                else
                {
                    var subTris = BoolTriSplitter.SplitTriangle(tri, splitPlanes, _eps);
                    result.AddRange(subTris);
                }
            }

            return result;
        }

        // =====================================================================
        //  步骤 7: MergeResult — 按运算类型合并结果
        // =====================================================================

        /// <summary>
        /// 按运算类型合并两个网格的分类后三角形。
        ///   Union:     A.Outside + B.Outside
        ///   Intersect: A.Inside  + B.Inside
        ///   Subtract:  A.Outside + B.Inside (翻转法线和绕序)
        /// </summary>
        private void MergeResult(EditableMesh output,
                                  List<BoolTri> trisA, TriClassification[] classA,
                                  List<BoolTri> trisB, TriClassification[] classB,
                                  MeshBooleanOp op)
        {
            // 处理 A 的三角形
            for (int i = 0; i < trisA.Count; i++)
            {
                bool keep = op switch
                {
                    MeshBooleanOp.Union => classA[i] == TriClassification.Outside,
                    MeshBooleanOp.Intersect => classA[i] == TriClassification.Inside,
                    MeshBooleanOp.Subtract => classA[i] == TriClassification.Outside,
                    _ => false
                };
                if (keep)
                    AppendTri(output, trisA[i], flip: false);
            }

            // 处理 B 的三角形
            for (int i = 0; i < trisB.Count; i++)
            {
                bool keep = op switch
                {
                    MeshBooleanOp.Union => classB[i] == TriClassification.Outside,
                    MeshBooleanOp.Intersect => classB[i] == TriClassification.Inside,
                    MeshBooleanOp.Subtract => classB[i] == TriClassification.Inside,
                    _ => false
                };
                // Subtract 时 B 的内部面需要翻转法线 (形成腔体)
                bool flip = op == MeshBooleanOp.Subtract && classB[i] == TriClassification.Inside;
                if (keep)
                    AppendTri(output, trisB[i], flip);
            }
        }

        /// <summary>将工作三角形追加到 EditableMesh (顶点不合并, 后续统一焊接)。</summary>
        private static void AppendTri(EditableMesh mesh, BoolTri tri, bool flip)
        {
            int baseIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(tri.A.Position);
            mesh.Vertices.Add(tri.B.Position);
            mesh.Vertices.Add(tri.C.Position);
            mesh.Normals.Add(flip ? -tri.A.Normal : tri.A.Normal);
            mesh.Normals.Add(flip ? -tri.B.Normal : tri.B.Normal);
            mesh.Normals.Add(flip ? -tri.C.Normal : tri.C.Normal);
            mesh.UV0.Add(tri.A.UV);
            mesh.UV0.Add(tri.B.UV);
            mesh.UV0.Add(tri.C.UV);
            // flip=true 时反转绕序: (0,1,2) -> (0,2,1)
            mesh.Faces.Add(flip
                ? new MeshFace(baseIdx, baseIdx + 2, baseIdx + 1)
                : new MeshFace(baseIdx, baseIdx + 1, baseIdx + 2));
        }

        // =====================================================================
        //  步骤 8: FixTopology — 合并重复顶点 / 重算法线 / 修复拓扑
        // =====================================================================

        /// <summary>
        /// 修复结果网格的拓扑:
        ///   1. 合并重复顶点 (空间哈希加速)
        ///   2. 删除退化三角形 (面积为零)
        ///   3. 重新计算法线
        /// </summary>
        private void FixTopology(EditableMesh mesh, BooleanConfig config)
        {
            // 删除退化三角形 (面积为零 / 有重复顶点)
            RemoveDegenerateFaces(mesh);

            // 合并重复顶点
            if (config.MergeVertices)
            {
                MergeVerticesSpatial(mesh, config.WeldThreshold);
            }

            // 重新计算法线
            if (config.RecalculateNormals)
            {
                mesh.RecalculateNormals();
            }
        }

        /// <summary>删除退化三角形 (面积为零或顶点重复)。</summary>
        private void RemoveDegenerateFaces(EditableMesh mesh)
        {
            var validFaces = new List<MeshFace>(mesh.Faces.Count);
            foreach (var f in mesh.Faces)
            {
                if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                if (f.A == f.B || f.B == f.C || f.A == f.C) continue;

                var a = mesh.Vertices[f.A];
                var b = mesh.Vertices[f.B];
                var c = mesh.Vertices[f.C];
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() < _eps * _eps) continue; // 零面积

                validFaces.Add(f);
            }
            mesh.Faces = validFaces;
        }

        /// <summary>
        /// 使用空间哈希网格加速的顶点合并。
        /// 将距离小于 threshold 的顶点合并为同一个, 重映射面索引。
        /// </summary>
        private void MergeVerticesSpatial(EditableMesh mesh, float threshold)
        {
            int n = mesh.Vertices.Count;
            if (n == 0) return;

            var remap = new int[n];
            for (int i = 0; i < n; i++) remap[i] = i;

            float cellSize = Math.Max(threshold * 2f, 1e-5f);
            var grid = new Dictionary<(int, int, int), List<int>>();

            float invCell = 1f / cellSize;

            for (int i = 0; i < n; i++)
            {
                if (remap[i] != i) continue;
                var p = mesh.Vertices[i];
                var key = (
                    (int)Math.Floor(p.X * invCell),
                    (int)Math.Floor(p.Y * invCell),
                    (int)Math.Floor(p.Z * invCell)
                );

                // 检查当前 cell 和 26 邻域 cell
                bool merged = false;
                for (int dx = -1; dx <= 1 && !merged; dx++)
                {
                    for (int dy = -1; dy <= 1 && !merged; dy++)
                    {
                        for (int dz = -1; dz <= 1 && !merged; dz++)
                        {
                            var nkey = (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz);
                            if (!grid.TryGetValue(nkey, out var cell)) continue;

                            foreach (var j in cell)
                            {
                                if (Vector3.DistanceSquared(mesh.Vertices[i], mesh.Vertices[j]) < threshold * threshold)
                                {
                                    remap[i] = j;
                                    merged = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (!merged)
                {
                    if (!grid.TryGetValue(key, out var cell))
                    {
                        cell = new List<int>();
                        grid[key] = cell;
                    }
                    cell.Add(i);
                }
            }

            // 构建新顶点列表
            var newVertices = new List<Vector3>();
            var oldToNew = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (remap[i] == i)
                {
                    oldToNew[i] = newVertices.Count;
                    newVertices.Add(mesh.Vertices[i]);
                }
                else
                {
                    oldToNew[i] = oldToNew[remap[i]];
                }
            }

            int removed = n - newVertices.Count;
            if (removed == 0) return; // 无需合并

            mesh.Vertices = newVertices;

            // 重映射法线和 UV
            if (mesh.Normals.Count == n)
            {
                var newNormals = new List<Vector3>(newVertices.Count);
                for (int i = 0; i < n; i++)
                    if (remap[i] == i) newNormals.Add(mesh.Normals[i]);
                mesh.Normals = newNormals;
            }
            if (mesh.UV0.Count == n)
            {
                var newUVs = new List<Vector2>(newVertices.Count);
                for (int i = 0; i < n; i++)
                    if (remap[i] == i) newUVs.Add(mesh.UV0[i]);
                mesh.UV0 = newUVs;
            }

            // 重映射面索引
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                var f = mesh.Faces[i];
                mesh.Faces[i] = new MeshFace(oldToNew[f.A], oldToNew[f.B], oldToNew[f.C]);
            }
        }

        // =====================================================================
        //  辅助方法
        // =====================================================================

        /// <summary>浅拷贝源网格到目标。</summary>
        private static void CopyMesh(EditableMesh src, EditableMesh dst)
        {
            dst.Vertices.AddRange(src.Vertices);
            dst.Normals.AddRange(src.Normals);
            dst.UV0.AddRange(src.UV0);
            dst.Faces.AddRange(src.Faces);
        }
    }

    // =================================================================================
    //  BoolMeshConverter — EditableMesh 与 BoolTri 互转的便捷工具
    // =================================================================================

    /// <summary>
    /// EditableMesh 与布尔运算工作类型之间的转换工具。
    /// </summary>
    public static class BoolMeshConverter
    {
        /// <summary>将 EditableMesh 转换为工作三角形列表。</summary>
        public static List<BoolTri> ToBoolTris(EditableMesh mesh)
        {
            var tris = new List<BoolTri>(mesh.Faces.Count);
            foreach (var f in mesh.Faces)
            {
                if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                tris.Add(new BoolTri(
                    new BoolVert(
                        mesh.Vertices[f.A],
                        mesh.Normals.Count > f.A ? mesh.Normals[f.A] : Vector3.UnitY,
                        mesh.UV0.Count > f.A ? mesh.UV0[f.A] : Vector2.Zero),
                    new BoolVert(
                        mesh.Vertices[f.B],
                        mesh.Normals.Count > f.B ? mesh.Normals[f.B] : Vector3.UnitY,
                        mesh.UV0.Count > f.B ? mesh.UV0[f.B] : Vector2.Zero),
                    new BoolVert(
                        mesh.Vertices[f.C],
                        mesh.Normals.Count > f.C ? mesh.Normals[f.C] : Vector3.UnitY,
                        mesh.UV0.Count > f.C ? mesh.UV0[f.C] : Vector2.Zero)
                ));
            }
            return tris;
        }

        /// <summary>将工作三角形列表转换回 EditableMesh (每个三角形独立 3 顶点, 不合并)。</summary>
        public static EditableMesh ToEditableMesh(List<BoolTri> tris, string name = "BoolMesh")
        {
            var mesh = new EditableMesh { Name = name };
            foreach (var tri in tris)
            {
                int baseIdx = mesh.Vertices.Count;
                mesh.Vertices.Add(tri.A.Position);
                mesh.Vertices.Add(tri.B.Position);
                mesh.Vertices.Add(tri.C.Position);
                mesh.Normals.Add(tri.A.Normal);
                mesh.Normals.Add(tri.B.Normal);
                mesh.Normals.Add(tri.C.Normal);
                mesh.UV0.Add(tri.A.UV);
                mesh.UV0.Add(tri.B.UV);
                mesh.UV0.Add(tri.C.UV);
                mesh.Faces.Add(new MeshFace(baseIdx, baseIdx + 1, baseIdx + 2));
            }
            return mesh;
        }

        /// <summary>从工作三角形列表构建 BVH。</summary>
        public static BoolBVH BuildBVHFromTris(List<BoolTri> tris)
        {
            var data = new List<BoolTriData>(tris.Count);
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                data.Add(BoolTriData.Create(i, t.A.Position, t.B.Position, t.C.Position));
            }
            return new BoolBVH(data);
        }
    }
}
