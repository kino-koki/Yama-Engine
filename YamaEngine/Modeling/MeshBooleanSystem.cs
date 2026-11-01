// =====================================================================================
//  YamaEngine.Modeling.MeshBooleanSystem
//  网格布尔运算系统 (Boolean Mesh Operations)
// -------------------------------------------------------------------------------------
//  本文件对齐 Unreal Engine 5 Modeling Mode 的 "Boolean" / "Mesh Boolean Operations" 工具。
//  UE5 Modeling Mode 提供对两个三角网格执行 并集 / 交集 / 差集 等布尔运算的能力,
//  广泛用于程序化建模与编辑器内的实体组合 (CSG)。
//
//  实现思路 (与 UE5 Modeling Boolean 概念对齐):
//    1. 三角形相交检测 —— Möller 1997 三角形-三角形相交算法, 配合 AABB 快速排除。
//    2. 相交裁剪 (Intersection Clipping) —— 沿相交线把三角形切分为子三角形, 使两个网格
//       的三角形在相交线上严格对齐 (no triangle crosses the other mesh's surface)。
//    3. 分类 (Classification) —— 用质心点做 PointInMesh 测试 (射线法), 判断每个子三角形
//       在另一个网格的内部 / 外部。
//    4. 按运算类型保留 (Keep by Operation) —— Union / Intersection / Difference /
//       SymmetricDifference / Cutout 分别保留对应区域的三角形, 差集/对称差集对腔体面翻转法线。
//    5. 合并与清理 (Merge & Cleanup) —— 合并结果顶点、重算法线、合并重复顶点。
//
//  对接的已有系统 API (YamaEngine.Modeling.EditableMesh):
//    - List<Vector3> Vertices / Normals / UV0
//    - List<MeshFace> Faces (MeshFace 为 readonly record struct, 含 int A,B,C)
//    - void RecalculateNormals()
//    - int  MergeDuplicateVertices(float threshold = 0.0001f)
//    - static EditableMesh CreateCube(string name, float size)
//
//  注意: 本系统假设输入网格为封闭 (watertight) 流形, 否则点在网格内测试 (射线法) 结果
//  不可靠 —— 这与 UE5 Modeling Boolean 对输入网格的要求一致。
// =====================================================================================

using System.Numerics;
using YamaEngine.Assets;

namespace YamaEngine.Modeling
{
    // =================================================================================
    //  布尔运算类型枚举
    // =================================================================================

    /// <summary>
    /// 布尔运算类型 —— 对齐 UE5 Modeling Mode Boolean Operations 的运算模式。
    /// 描述两个网格 (A 与 B) 之间的实体布尔组合方式。
    /// </summary>
    public enum BooleanOperation
    {
        /// <summary>并集 A∪B: 保留 A 在 B 外部的三角形 + B 在 A 外部的三角形。</summary>
        Union = 0,

        /// <summary>交集 A∩B: 保留 A 在 B 内部的三角形 + B 在 A 内部的三角形。</summary>
        Intersection = 1,

        /// <summary>差集 A-B: 保留 A 在 B 外部的三角形 + B 在 A 内部的三角形 (腔体面法线翻转)。</summary>
        Difference = 2,

        /// <summary>对称差集 A△B = (A-B)∪(B-A): 保留 A、B 互相在外部的三角形, 以及互相在内部(腔体)的三角形(翻转)。</summary>
        SymmetricDifference = 3,

        /// <summary>切口 Cutout: 从 A 中切出 B 的形状 (实现等同于 Difference, 用于在表面开槽/挖洞)。</summary>
        Cutout = 4
    }

    // =================================================================================
    //  布尔运算设置
    // =================================================================================

    /// <summary>
    /// 布尔运算设置 —— 控制运算后处理流程。
    /// 对应 UE5 Modeling Boolean 工具面板中的 "Recalculate Normals" / "Weld Vertices" 等选项。
    /// </summary>
    public class MeshBooleanSettings
    {
        /// <summary>运算后是否重新计算顶点法线 (按面法线加权平滑)。</summary>
        public bool bRecalculateNormals = true;

        /// <summary>运算后是否合并重复顶点 (焊接)。</summary>
        public bool bMergeVertices = true;

        /// <summary>是否翻转背面 (将绕序与法线一致的三角形统一朝外)。</summary>
        public bool bFlipBackfaces = false;

        /// <summary>顶点合并阈值 (距离小于该值的顶点视为同一顶点)。</summary>
        public float WeldThreshold = 0.0001f;

        /// <summary>几何 epsilon, 用于判定点是否在平面上 / 距离是否为零。</summary>
        public float GeometryEpsilon = 1e-6f;

        /// <summary>默认设置。</summary>
        public static MeshBooleanSettings Default => new();
    }

    // =================================================================================
    //  辅助几何类型: Ray / Triangle
    // =================================================================================

    /// <summary>射线 (起点 + 单位方向)。</summary>
    public readonly struct Ray
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;

        public Ray(Vector3 origin, Vector3 direction)
        {
            Origin = origin;
            var len = direction.Length();
            Direction = len > 0f ? direction / len : Vector3.UnitX;
        }
    }

    /// <summary>三角形 (三个顶点位置)。</summary>
    public readonly struct Triangle
    {
        public readonly Vector3 A;
        public readonly Vector3 B;
        public readonly Vector3 C;

        public Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            A = a; B = b; C = c;
        }
    }

    // =================================================================================
    //  数学工具 BooleanMath
    //  提供: 平面构建 / 有符号距离 / 射线-三角形相交 / 三角形最近点 / AABB 等。
    // =================================================================================

    /// <summary>
    /// 布尔运算所需的底层几何数学工具集合。
    /// </summary>
    public static class BooleanMath
    {
        /// <summary>全局几何 epsilon。</summary>
        public const float Epsilon = 1e-6f;

        /// <summary>
        /// 从三角形三个顶点构建平面方程 ax + by + cz + d = 0。
        /// 返回 Vector4(a, b, c, d), 其中 (a,b,c) 为单位法线。
        /// 退化三角形 (零面积) 返回 Vector4.Zero。
        /// </summary>
        public static Vector4 PlaneFromTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Cross(b - a, c - a);
            var len = normal.Length();
            if (len < Epsilon) return Vector4.Zero; // 退化三角形, 无法定义平面
            normal /= len;
            float d = -Vector3.Dot(normal, a);
            return new Vector4(normal, d);
        }

        /// <summary>
        /// 点到平面的有符号距离。
        /// plane 为 Vector4(a,b,c,d) 且 (a,b,c) 为单位法线时, 返回值即为带符号距离。
        /// 正值表示点在法线正侧, 负值表示在反侧, 0 表示在平面上。
        /// </summary>
        public static float SignedDistanceToPlane(Vector3 p, Vector4 plane)
        {
            return plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W;
        }

        /// <summary>
        /// 射线-三角形相交检测 (Möller-Trumbore 算法)。
        /// 返回是否相交, 以及相交参数 t (命中点 = rayOrigin + t * rayDir)。
        /// 仅当 t &gt; Epsilon 时视为有效命中 (排除射线起点所在的三角形)。
        /// </summary>
        public static bool RayTriangleIntersect(Vector3 rayOrigin, Vector3 rayDir,
                                                Vector3 a, Vector3 b, Vector3 c,
                                                out float t)
        {
            t = 0f;
            var edge1 = b - a;
            var edge2 = c - a;
            var h = Vector3.Cross(rayDir, edge2);
            float det = Vector3.Dot(edge1, h);
            // 射线与三角形平面平行 (或退化) -> 无交点
            if (det > -Epsilon && det < Epsilon) return false;
            float invDet = 1f / det;
            var s = rayOrigin - a;
            float u = invDet * Vector3.Dot(s, h);
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, edge1);
            float v = invDet * Vector3.Dot(rayDir, q);
            if (v < 0f || u + v > 1f) return false;
            t = invDet * Vector3.Dot(edge2, q);
            return t > Epsilon;
        }

        /// <summary>
        /// 计算点 p 到三角形 (a,b,c) 的最近点。
        /// 采用 Ericson《Real-Time Collision Detection》中基于重心坐标的区域分解算法,
        /// 依次判断 Voronoi 区域 (顶点 / 边 / 面) 并返回最近点。
        /// </summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a; // 顶点 A 区域

            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b; // 顶点 B 区域

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return a + v * ab; // 边 AB 区域
            }

            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c; // 顶点 C 区域

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return a + w * ac; // 边 AC 区域
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + w * (c - b); // 边 BC 区域
            }

            // 面内区域: 用重心坐标组合
            float denom = 1f / (va + vb + vc);
            float vBary = vb * denom;
            float wBary = vc * denom;
            return a + ab * vBary + ac * wBary;
        }

        /// <summary>计算三角形的轴对齐包围盒 (AABB)。</summary>
        public static void TriangleAABB(Vector3 a, Vector3 b, Vector3 c, out Vector3 min, out Vector3 max)
        {
            min = Vector3.Min(Vector3.Min(a, b), c);
            max = Vector3.Max(Vector3.Max(a, b), c);
        }

        /// <summary>两个 AABB 是否重叠 (可附加容差 eps 用于相交判定)。</summary>
        public static bool AABBOverlap(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB, float eps = 0f)
        {
            return minA.X <= maxB.X + eps && maxA.X >= minB.X - eps &&
                   minA.Y <= maxB.Y + eps && maxA.Y >= minB.Y - eps &&
                   minA.Z <= maxB.Z + eps && maxA.Z >= minB.Z - eps;
        }

        /// <summary>取 Vector3 的第 i 个分量 (i=0,1,2 对应 X,Y,Z)。</summary>
        public static float Component(Vector3 v, int i)
        {
            return i == 0 ? v.X : (i == 1 ? v.Y : v.Z);
        }

        /// <summary>沿平面法线方向把点投影到平面上 (用于把交点贴合到平面, 减小浮点误差)。</summary>
        public static Vector3 ProjectToPlane(Vector3 p, Vector4 plane)
        {
            float d = SignedDistanceToPlane(p, plane);
            var n = new Vector3(plane.X, plane.Y, plane.Z);
            return p - n * d;
        }
    }

    // =================================================================================
    //  三角形-三角形相交检测 (TriangleTriangleIntersection)
    //  实现 Möller 1997 "A Fast Triangle-Triangle Intersection Test" 算法。
    // =================================================================================

    /// <summary>
    /// 三角形-三角形相交检测结果。
    /// </summary>
    public readonly struct IntersectionResult
    {
        /// <summary>两个三角形是否相交。</summary>
        public readonly bool Intersects;
        /// <summary>交线段端点 1 (仅当 IsSegment 为真时有意义)。</summary>
        public readonly Vector3 Point1;
        /// <summary>交线段端点 2 (仅当 IsSegment 为真时有意义)。</summary>
        public readonly Vector3 Point2;
        /// <summary>是否为有效线段 (共面重叠 / 顶点接触时为 false)。</summary>
        public readonly bool IsSegment;

        public IntersectionResult(bool intersects, Vector3 p1, Vector3 p2, bool isSegment)
        {
            Intersects = intersects;
            Point1 = p1;
            Point2 = p2;
            IsSegment = isSegment;
        }
    }

    /// <summary>
    /// 三角形-三角形相交检测 —— Möller 1997 算法。
    /// 算法流程:
    ///   1) 计算三角形 B 所在平面, 检验 A 的三个顶点是否全在平面同侧 (同侧则不相交)。
    ///   2) 计算三角形 A 所在平面, 检验 B 的三个顶点是否全在平面同侧 (同侧则不相交)。
    ///   3) 两个平面的交线方向 D = Na × Nb, 取 D 的主轴作为一维参数坐标。
    ///   4) 分别求 A 与平面 B 的交点段 [ca1,ca2], B 与平面 A 的交点段 [cb1,cb2]。
    ///   5) 两个区间在主轴上重叠 -> 相交, 重叠区间即为交线段; 否则不相交。
    ///   6) 共面情况单独处理 (2D 三角形重叠测试)。
    /// </summary>
    public static class TriangleTriangleIntersection
    {
        /// <summary>
        /// 判断两个三角形是否相交。
        /// </summary>
        public static bool Intersect(Vector3 a0, Vector3 a1, Vector3 a2,
                                     Vector3 b0, Vector3 b1, Vector3 b2)
        {
            return ComputeIntersection(a0, a1, a2, b0, b1, b2).Intersects;
        }

        /// <summary>
        /// 计算两个三角形的交线段。
        /// 返回 <see cref="IntersectionResult"/>; 若不相交则 Intersects=false。
        /// 共面重叠或顶点接触时 IsSegment=false (无有效线段)。
        /// </summary>
        public static IntersectionResult ComputeIntersection(Vector3 a0, Vector3 a1, Vector3 a2,
                                                             Vector3 b0, Vector3 b1, Vector3 b2)
        {
            float eps = BooleanMath.Epsilon;

            // ---- 步骤 1: 三角形 B 的平面, 检验 A 顶点是否同侧 ----
            var planeB = BooleanMath.PlaneFromTriangle(b0, b1, b2);
            if (planeB == Vector4.Zero) return default; // B 退化
            float da0 = BooleanMath.SignedDistanceToPlane(a0, planeB);
            float da1 = BooleanMath.SignedDistanceToPlane(a1, planeB);
            float da2 = BooleanMath.SignedDistanceToPlane(a2, planeB);
            if (da0 > eps && da1 > eps && da2 > eps) return default; // A 全在 B 平面正侧
            if (da0 < -eps && da1 < -eps && da2 < -eps) return default; // A 全在 B 平面负侧

            // ---- 步骤 2: 三角形 A 的平面, 检验 B 顶点是否同侧 ----
            var planeA = BooleanMath.PlaneFromTriangle(a0, a1, a2);
            if (planeA == Vector4.Zero) return default; // A 退化
            float db0 = BooleanMath.SignedDistanceToPlane(b0, planeA);
            float db1 = BooleanMath.SignedDistanceToPlane(b1, planeA);
            float db2 = BooleanMath.SignedDistanceToPlane(b2, planeA);
            if (db0 > eps && db1 > eps && db2 > eps) return default; // B 全在 A 平面正侧
            if (db0 < -eps && db1 < -eps && db2 < -eps) return default; // B 全在 A 平面负侧

            // ---- 步骤 3: 共面情况 (A 三个顶点都在 B 平面上) ----
            bool aCoplanar = Math.Abs(da0) <= eps && Math.Abs(da1) <= eps && Math.Abs(da2) <= eps;
            if (aCoplanar)
            {
                bool overlap = CoplanarOverlap(a0, a1, a2, b0, b1, b2, planeA);
                // 共面重叠无交线段, 返回 IsSegment=false
                return new IntersectionResult(overlap, Vector3.Zero, Vector3.Zero, false);
            }

            // ---- 步骤 4: 交线方向 D = Na × Nb ----
            var nA = new Vector3(planeA.X, planeA.Y, planeA.Z);
            var nB = new Vector3(planeB.X, planeB.Y, planeB.Z);
            var D = Vector3.Cross(nA, nB);
            if (D.LengthSquared() < eps * eps)
            {
                // 平面平行 (非共面, 已被同侧测试排除) -> 不相交
                return default;
            }

            // 取 D 的主轴作为一维参数轴 (减小投影误差)
            float ax = Math.Abs(D.X), ay = Math.Abs(D.Y), az = Math.Abs(D.Z);
            int axis = 0;
            float maxComp = ax;
            if (ay > maxComp) { maxComp = ay; axis = 1; }
            if (az > maxComp) { maxComp = az; axis = 2; }

            // ---- 步骤 5: 计算 A 与 B 平面的交点段, B 与 A 平面的交点段 ----
            var ptsA = new List<Vector3>(2);
            ComputeInterval(a0, a1, a2, da0, da1, da2, ptsA, eps);
            var ptsB = new List<Vector3>(2);
            ComputeInterval(b0, b1, b2, db0, db1, db2, ptsB, eps);

            if (ptsA.Count < 2 || ptsB.Count < 2) return default; // 顶点接触, 退化

            // 把交点贴合到对方平面, 减小浮点误差
            ptsA[0] = BooleanMath.ProjectToPlane(ptsA[0], planeB);
            ptsA[1] = BooleanMath.ProjectToPlane(ptsA[1], planeB);
            ptsB[0] = BooleanMath.ProjectToPlane(ptsB[0], planeA);
            ptsB[1] = BooleanMath.ProjectToPlane(ptsB[1], planeA);

            // ---- 步骤 6: 投影到主轴做区间重叠测试 ----
            float ca1 = BooleanMath.Component(ptsA[0], axis);
            float ca2 = BooleanMath.Component(ptsA[1], axis);
            Vector3 paLo = ptsA[0], paHi = ptsA[1];
            if (ca1 > ca2) { (ca1, ca2) = (ca2, ca1); (paLo, paHi) = (paHi, paLo); }

            float cb1 = BooleanMath.Component(ptsB[0], axis);
            float cb2 = BooleanMath.Component(ptsB[1], axis);
            Vector3 pbLo = ptsB[0], pbHi = ptsB[1];
            if (cb1 > cb2) { (cb1, cb2) = (cb2, cb1); (pbLo, pbHi) = (pbHi, pbLo); }

            // 区间不重叠 -> 不相交
            if (ca1 > cb2 + eps || cb1 > ca2 + eps) return default;

            // 重叠段端点: 取两个区间交集的端点
            Vector3 segStart = ca1 >= cb1 ? paLo : pbLo; // max(下界)
            Vector3 segEnd = ca2 <= cb2 ? paHi : pbHi;   // min(上界)
            return new IntersectionResult(true, segStart, segEnd, true);
        }

        /// <summary>
        /// 计算三角形 (v0,v1,v2) 与另一平面的交点段。
        /// 通过遍历三条边, 收集跨越平面 (或端点在平面上) 的交点, 去重后得到 2 个端点。
        /// </summary>
        private static void ComputeInterval(Vector3 v0, Vector3 v1, Vector3 v2,
                                            float d0, float d1, float d2,
                                            List<Vector3> outPoints, float eps)
        {
            CollectEdgeCross(v0, v1, d0, d1, outPoints, eps);
            CollectEdgeCross(v1, v2, d1, d2, outPoints, eps);
            CollectEdgeCross(v2, v0, d2, d0, outPoints, eps);
            DedupPoints(outPoints, eps);
        }

        /// <summary>
        /// 对一条边 (a,b), 若其跨越平面则在 outPoints 添加交点; 若端点在平面上则添加该端点。
        /// </summary>
        private static void CollectEdgeCross(Vector3 a, Vector3 b, float da, float db,
                                             List<Vector3> pts, float eps)
        {
            bool aPos = da > eps, aNeg = da < -eps;
            bool bPos = db > eps, bNeg = db < -eps;

            // 同侧 (都正或都负) -> 无交点
            if (aPos && bPos) return;
            if (aNeg && bNeg) return;

            // 一正一负 -> 边跨越平面, 线性插值求交点
            if ((aPos && bNeg) || (aNeg && bPos))
            {
                float t = da / (da - db);
                pts.Add(a + t * (b - a));
                return;
            }

            // 端点在平面上 (|d|<=eps)
            if (!aPos && !aNeg) pts.Add(a);
            if (!bPos && !bNeg) pts.Add(b);
        }

        /// <summary>对点列表按距离去重 (距离平方 &lt; eps² 视为同一点)。</summary>
        private static void DedupPoints(List<Vector3> pts, float eps)
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

        /// <summary>
        /// 共面三角形的 2D 重叠测试。
        /// 把两个三角形投影到主平面 (丢弃法线最大分量对应的轴), 在 2D 下测试:
        ///   - 任一 B 顶点在 A 内 (或反之);
        ///   - 任一边相交。
        /// 任一条件成立即视为重叠。
        /// </summary>
        private static bool CoplanarOverlap(Vector3 a0, Vector3 a1, Vector3 a2,
                                            Vector3 b0, Vector3 b1, Vector3 b2,
                                            Vector4 plane)
        {
            var n = new Vector3(plane.X, plane.Y, plane.Z);
            float ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
            int drop = 0;
            if (ay >= ax && ay >= az) drop = 1;
            else if (az >= ax && az >= ay) drop = 2;

            var va0 = To2D(a0, drop); var va1 = To2D(a1, drop); var va2 = To2D(a2, drop);
            var vb0 = To2D(b0, drop); var vb1 = To2D(b1, drop); var vb2 = To2D(b2, drop);

            // B 的任一顶点在 A 内
            if (PointInTri2D(vb0, va0, va1, va2)) return true;
            if (PointInTri2D(vb1, va0, va1, va2)) return true;
            if (PointInTri2D(vb2, va0, va1, va2)) return true;
            // A 的任一顶点在 B 内
            if (PointInTri2D(va0, vb0, vb1, vb2)) return true;
            if (PointInTri2D(va1, vb0, vb1, vb2)) return true;
            if (PointInTri2D(va2, vb0, vb1, vb2)) return true;

            // 边相交
            var aEdges = new[] { (va0, va1), (va1, va2), (va2, va0) };
            var bEdges = new[] { (vb0, vb1), (vb1, vb2), (vb2, vb0) };
            foreach (var ea in aEdges)
                foreach (var eb in bEdges)
                    if (SegSegIntersect2D(ea.Item1, ea.Item2, eb.Item1, eb.Item2))
                        return true;

            return false;
        }

        private static Vector2 To2D(Vector3 v, int drop)
        {
            return drop == 0 ? new Vector2(v.Y, v.Z) : (drop == 1 ? new Vector2(v.X, v.Z) : new Vector2(v.X, v.Y));
        }

        /// <summary>2D 点在三角形内测试 (使用三条边的同号判定)。</summary>
        private static bool PointInTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign2D(p, a, b);
            float d2 = Sign2D(p, b, c);
            float d3 = Sign2D(p, c, a);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos); // 全非负或全非正 -> 在内 (含边界)
        }

        private static float Sign2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return (a.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (a.Y - c.Y);
        }

        /// <summary>2D 线段-线段相交测试。</summary>
        private static bool SegSegIntersect2D(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float d1 = Sign2D(p3, p4, p1);
            float d2 = Sign2D(p3, p4, p2);
            float d3 = Sign2D(p1, p2, p3);
            float d4 = Sign2D(p1, p2, p4);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;
            return false;
        }
    }

    // =================================================================================
    //  三角形裁剪器 TriangleClipper (Sutherland-Hodgman 平面裁剪)
    // =================================================================================

    /// <summary>
    /// 三角形裁剪器 —— 用平面裁剪三角形, 风格参照 Sutherland-Hodgman 多边形裁剪算法。
    /// 裁剪保留平面正侧 (SignedDistance &gt;= 0) 的部分, 可生成 0/1/2 个三角形的多边形。
    /// </summary>
    public static class TriangleClipper
    {
        /// <summary>
        /// 用平面裁剪三角形 (v0,v1,v2), 返回裁剪后 (正侧) 顶点列表。
        /// 顶点数可能为 0 (完全在负侧)、3 (完全在正侧)、4 (跨越平面, 切出四边形)。
        /// </summary>
        public static List<Vector3> ClipTriangleAgainstPlane(Vector3 v0, Vector3 v1, Vector3 v2, Vector4 plane)
        {
            var polygon = new List<Vector3> { v0, v1, v2 };
            return ClipPolygonAgainstPlane(polygon, plane);
        }

        /// <summary>
        /// 用平面裁剪多边形 (Sutherland-Hodgman), 返回正侧多边形顶点列表 (保持绕序)。
        /// </summary>
        public static List<Vector3> ClipPolygonAgainstPlane(List<Vector3> polygon, Vector4 plane)
        {
            if (polygon == null || polygon.Count == 0) return new List<Vector3>();
            var output = new List<Vector3>();
            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                var cur = polygon[i];
                var prev = polygon[(i - 1 + n) % n];
                float dCur = BooleanMath.SignedDistanceToPlane(cur, plane);
                float dPrev = BooleanMath.SignedDistanceToPlane(prev, plane);
                bool curInside = dCur >= 0f;
                bool prevInside = dPrev >= 0f;

                if (curInside)
                {
                    if (!prevInside)
                    {
                        // 由外进入: 先添加边与平面的交点
                        float t = dPrev / (dPrev - dCur);
                        output.Add(prev + t * (cur - prev));
                    }
                    output.Add(cur);
                }
                else if (prevInside)
                {
                    // 由内离开: 添加边与平面的交点
                    float t = dPrev / (dPrev - dCur);
                    output.Add(prev + t * (cur - prev));
                }
            }
            return output;
        }
    }

    // =================================================================================
    //  点在网格内测试 PointInMeshTest (射线法)
    // =================================================================================

    /// <summary>
    /// 点在网格内测试 —— 射线法 (Ray Casting)。
    /// 从测试点出发沿某方向发射射线, 统计与网格三角形相交次数, 奇数次=在内部, 偶数次=在外部。
    /// 为提升鲁棒性, 会尝试多个方向并跳过退化命中 (命中参数过小)。
    /// 注意: 输入网格须为封闭流形, 否则结果不可靠。
    /// </summary>
    public static class PointInMeshTest
    {
        /// <summary>测试点 point 是否在网格 mesh 内部。</summary>
        public static bool Contains(EditableMesh mesh, Vector3 point)
        {
            if (mesh == null || mesh.Faces.Count == 0) return false;

            // 一组不易与三角形平行的测试方向 (含对角方向, 避免退化的轴对齐)
            var dirs = new[]
            {
                Vector3.Normalize(new Vector3(1f, 2f, 3f)),
                Vector3.Normalize(new Vector3(3f, 1f, 2f)),
                Vector3.Normalize(new Vector3(2f, 3f, 1f)),
                Vector3.Normalize(new Vector3(1f, 1f, 1f)),
                Vector3.Normalize(new Vector3(1f, -1f, 2f)),
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ
            };

            foreach (var dir in dirs)
            {
                int count = 0;
                bool degenerate = false;
                foreach (var f in mesh.Faces)
                {
                    if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                    var a = mesh.Vertices[f.A];
                    var b = mesh.Vertices[f.B];
                    var c = mesh.Vertices[f.C];
                    if (BooleanMath.RayTriangleIntersect(point, dir, a, b, c, out float t))
                    {
                        // 命中点过近 -> 可能恰好在表面上, 视为退化, 换方向重试
                        if (t < 1e-4f) { degenerate = true; break; }
                        count++;
                    }
                }
                if (degenerate) continue;
                // 奇数次命中 = 在内部
                return (count & 1) == 1;
            }
            return false;
        }

        /// <summary>
        /// 射线-三角形相交 (封装版), 返回 (是否命中, 命中参数 t)。
        /// 对应 UE5 中常用的 ray-triangle 查询接口。
        /// </summary>
        public static (bool hit, float t) RayTriangleIntersect(Ray ray, Triangle tri)
        {
            bool hit = BooleanMath.RayTriangleIntersect(ray.Origin, ray.Direction,
                                                       tri.A, tri.B, tri.C, out float t);
            return (hit, t);
        }
    }

    // =================================================================================
    //  内部工作类型: BoolVertex / BoolTriangle (切分时携带法线/UV 插值)
    // =================================================================================

    /// <summary>
    /// 布尔运算内部工作顶点 —— 在三角形切分过程中携带位置 / 法线 / UV,
    /// 以便切分产生的新顶点能通过线性插值继承原始属性。
    /// </summary>
    internal struct BoolVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;

        public static BoolVertex Interpolate(BoolVertex a, BoolVertex b, float t)
        {
            var n = a.Normal + t * (b.Normal - a.Normal);
            if (n.LengthSquared() < 1e-12f) n = a.Normal;
            else n = Vector3.Normalize(n);
            return new BoolVertex
            {
                Position = a.Position + t * (b.Position - a.Position),
                Normal = n,
                UV = a.UV + t * (b.UV - a.UV)
            };
        }
    }

    /// <summary>内部工作三角形 (三个 BoolVertex)。</summary>
    internal struct BoolTriangle
    {
        public BoolVertex A, B, C;

        /// <summary>三角形质心位置 (用于点在网格内分类)。</summary>
        public Vector3 Centroid => (A.Position + B.Position + C.Position) / 3f;
    }

    // =================================================================================
    //  网格切分器 MeshSplitter
    //  沿另一个网格 (cutter) 的三角形平面切分目标网格的三角形,
    //  使切分后每个子三角形不再跨越 cutter 表面 (从而可被分类为完全内部或完全外部)。
    // =================================================================================

    /// <summary>
    /// 网格切分器 —— 对齐 UE5 Modeling 中 "把相交三角形沿交线对齐切分" 的步骤。
    /// </summary>
    public static class MeshSplitter
    {
        /// <summary>
        /// 用 cutter 的三角形平面切分 target 的三角形, 返回切分后的新网格。
        /// 切分保持 target 的拓扑属性 (法线/UV 通过插值继承), 但每个三角形被切到与 cutter 表面对齐。
        /// </summary>
        public static EditableMesh SplitByMesh(EditableMesh target, EditableMesh cutter)
        {
            var result = new EditableMesh { Name = (target?.Name ?? "Mesh") + "_Split" };
            if (target == null || cutter == null) return result;
            if (target.Vertices.Count == 0 || target.Faces.Count == 0) return result;
            if (cutter.Vertices.Count == 0 || cutter.Faces.Count == 0)
            {
                // cutter 为空, 直接拷贝 target
                CopyMeshInto(target, result);
                return result;
            }

            float eps = BooleanMath.Epsilon;

            // 预计算 cutter 三角形数据 (顶点 / 平面 / AABB)
            var cutterTris = new List<CutterTriangle>();
            foreach (var f in cutter.Faces)
            {
                if (!f.IsValidFor(cutter.Vertices.Count)) continue;
                var v0 = cutter.Vertices[f.A];
                var v1 = cutter.Vertices[f.B];
                var v2 = cutter.Vertices[f.C];
                var plane = BooleanMath.PlaneFromTriangle(v0, v1, v2);
                if (plane == Vector4.Zero) continue; // 跳过退化三角形
                BooleanMath.TriangleAABB(v0, v1, v2, out var min, out var max);
                cutterTris.Add(new CutterTriangle { V0 = v0, V1 = v1, V2 = v2, Plane = plane, Min = min, Max = max });
            }

            // 对 target 每个三角形做切分
            foreach (var f in target.Faces)
            {
                if (!f.IsValidFor(target.Vertices.Count)) continue;
                var v0 = target.Vertices[f.A];
                var v1 = target.Vertices[f.B];
                var v2 = target.Vertices[f.C];
                BooleanMath.TriangleAABB(v0, v1, v2, out var tmin, out var tmax);

                // 收集与该 target 三角形相交的 cutter 三角形的平面
                var splitPlanes = new List<Vector4>();
                foreach (var ct in cutterTris)
                {
                    // AABB 快速排除
                    if (!BooleanMath.AABBOverlap(tmin, tmax, ct.Min, ct.Max, eps)) continue;
                    // Möller 三角形-三角形相交检测
                    if (!TriangleTriangleIntersection.Intersect(v0, v1, v2, ct.V0, ct.V1, ct.V2)) continue;
                    // 仅当 target 三角形确实跨越该平面时才需要切分 (共面相交无需切分)
                    float d0 = BooleanMath.SignedDistanceToPlane(v0, ct.Plane);
                    float d1 = BooleanMath.SignedDistanceToPlane(v1, ct.Plane);
                    float d2 = BooleanMath.SignedDistanceToPlane(v2, ct.Plane);
                    bool crosses = (d0 > eps || d1 > eps || d2 > eps) &&
                                   (d0 < -eps || d1 < -eps || d2 < -eps);
                    if (!crosses) continue;
                    splitPlanes.Add(ct.Plane);
                }

                // 构建工作三角形 (携带法线/UV)
                var poly = new List<BoolVertex>
                {
                    MakeBoolVertex(target, f.A),
                    MakeBoolVertex(target, f.B),
                    MakeBoolVertex(target, f.C)
                };

                // 递归用所有相交平面切分 (保留正负两侧), 得到子多边形集合
                List<List<BoolVertex>> subPolys;
                if (splitPlanes.Count == 0)
                {
                    subPolys = new List<List<BoolVertex>> { poly };
                }
                else
                {
                    subPolys = SplitPolygonByPlanes(poly, splitPlanes, 0, eps);
                }

                // 三角化并写入结果
                foreach (var sp in subPolys)
                {
                    if (sp.Count < 3) continue;
                    foreach (var bt in TriangulatePolygon(sp))
                        AppendBoolTriangle(result, bt);
                }
            }

            return result;
        }

        /// <summary>cutter 三角形缓存。</summary>
        private struct CutterTriangle
        {
            public Vector3 V0, V1, V2;
            public Vector4 Plane;
            public Vector3 Min, Max;
        }

        /// <summary>从 EditableMesh 读取一个工作顶点 (带法线/UV 兜底)。</summary>
        private static BoolVertex MakeBoolVertex(EditableMesh mesh, int index)
        {
            return new BoolVertex
            {
                Position = mesh.Vertices[index],
                Normal = mesh.Normals.Count > index ? mesh.Normals[index] : Vector3.UnitY,
                UV = mesh.UV0.Count > index ? mesh.UV0[index] : Vector2.Zero
            };
        }

        /// <summary>
        /// 递归地用平面集合切分多边形 —— 每个平面把多边形分成正侧与负侧两部分,
        /// 两部分都继续用后续平面切分, 最终得到所有"不再跨越任何平面"的子多边形。
        /// 这保证了每个子多边形完全位于所有 cutter 平面的某一侧 (即不跨越 cutter 表面)。
        /// </summary>
        private static List<List<BoolVertex>> SplitPolygonByPlanes(List<BoolVertex> polygon,
                                                                    List<Vector4> planes,
                                                                    int planeIndex, float eps)
        {
            if (polygon.Count < 3) return new List<List<BoolVertex>>();
            if (planeIndex >= planes.Count)
                return new List<List<BoolVertex>> { polygon };

            var plane = planes[planeIndex];
            var (posPoly, negPoly) = SplitPolygonByPlane(polygon, plane, eps);

            var result = new List<List<BoolVertex>>();
            if (posPoly.Count >= 3)
                result.AddRange(SplitPolygonByPlanes(posPoly, planes, planeIndex + 1, eps));
            if (negPoly.Count >= 3)
                result.AddRange(SplitPolygonByPlanes(negPoly, planes, planeIndex + 1, eps));
            return result;
        }

        /// <summary>
        /// 用单个平面把多边形切分为正侧 (pos) 与负侧 (neg) 两个多边形。
        /// 平面上的顶点同时归属两侧 (作为共享边界), 跨越平面的边在交点处插入新顶点 (插值属性)。
        /// </summary>
        private static (List<BoolVertex> pos, List<BoolVertex> neg) SplitPolygonByPlane(
            List<BoolVertex> polygon, Vector4 plane, float eps)
        {
            var pos = new List<BoolVertex>();
            var neg = new List<BoolVertex>();
            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                var cur = polygon[i];
                var prev = polygon[(i - 1 + n) % n];
                float dCur = BooleanMath.SignedDistanceToPlane(cur.Position, plane);
                float dPrev = BooleanMath.SignedDistanceToPlane(prev.Position, plane);

                bool prevPos = dPrev > eps;
                bool prevNeg = dPrev < -eps;
                bool curPos = dCur > eps;
                bool curNeg = dCur < -eps;
                bool curOn = !curPos && !curNeg;

                // 跨越平面 (严格异侧) -> 计算交点并加入两侧
                if ((prevPos && curNeg) || (prevNeg && curPos))
                {
                    float t = dPrev / (dPrev - dCur);
                    var mid = BoolVertex.Interpolate(prev, cur, t);
                    pos.Add(mid);
                    neg.Add(mid);
                }

                // 当前顶点归属: 正侧 / 负侧 / 平面上(两侧都加)
                if (curPos)
                {
                    pos.Add(cur);
                }
                else if (curNeg)
                {
                    neg.Add(cur);
                }
                else // curOn: 顶点在平面上
                {
                    pos.Add(cur);
                    neg.Add(cur);
                }
            }
            return (pos, neg);
        }

        /// <summary>扇形三角化凸多边形 (切分产生的子多边形均为凸)。</summary>
        private static List<BoolTriangle> TriangulatePolygon(List<BoolVertex> poly)
        {
            var tris = new List<BoolTriangle>(poly.Count - 2);
            for (int i = 1; i < poly.Count - 1; i++)
            {
                tris.Add(new BoolTriangle { A = poly[0], B = poly[i], C = poly[i + 1] });
            }
            return tris;
        }

        /// <summary>把工作三角形追加到 EditableMesh (顶点不合并, 后续统一焊接)。</summary>
        private static void AppendBoolTriangle(EditableMesh mesh, BoolTriangle bt)
        {
            int baseIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(bt.A.Position);
            mesh.Vertices.Add(bt.B.Position);
            mesh.Vertices.Add(bt.C.Position);
            mesh.Normals.Add(bt.A.Normal);
            mesh.Normals.Add(bt.B.Normal);
            mesh.Normals.Add(bt.C.Normal);
            mesh.UV0.Add(bt.A.UV);
            mesh.UV0.Add(bt.B.UV);
            mesh.UV0.Add(bt.C.UV);
            mesh.Faces.Add(new MeshFace(baseIdx, baseIdx + 1, baseIdx + 2));
        }

        /// <summary>把源网格拷贝到目标网格 (顶点/法线/UV/面)。</summary>
        private static void CopyMeshInto(EditableMesh src, EditableMesh dst)
        {
            dst.Vertices.AddRange(src.Vertices);
            dst.Normals.AddRange(src.Normals);
            dst.UV0.AddRange(src.UV0);
            dst.Faces.AddRange(src.Faces);
        }
    }

    // =================================================================================
    //  网格布尔运算核心 MeshBoolean
    // =================================================================================

    /// <summary>
    /// 网格布尔运算核心 —— 对齐 UE5 Modeling Mode "Boolean" 工具。
    /// 对两个封闭网格执行 并集 / 交集 / 差集 / 对称差集 / 切口 运算, 生成新网格。
    ///
    /// 算法管线:
    ///   1) SplitByMesh: 把 A 沿 B 表面切分, 把 B 沿 A 表面切分, 使两侧三角形在交线上对齐。
    ///   2) Classify:    对每个子三角形用质心做 PointInMesh 测试, 判定在另一网格的内部/外部。
    ///   3) Keep/Flip:   按运算类型保留对应区域三角形, 差集/对称差集的腔体面翻转法线与绕序。
    ///   4) Cleanup:     合并顶点、重算法线、(可选)翻转背面。
    /// </summary>
    public static class MeshBoolean
    {
        /// <summary>使用默认设置执行布尔运算。</summary>
        public static EditableMesh Execute(EditableMesh meshA, EditableMesh meshB, BooleanOperation op)
        {
            return Execute(meshA, meshB, op, MeshBooleanSettings.Default);
        }

        /// <summary>使用自定义设置执行布尔运算。</summary>
        public static EditableMesh Execute(EditableMesh meshA, EditableMesh meshB,
                                           BooleanOperation op, MeshBooleanSettings settings)
        {
            if (meshA == null) throw new ArgumentNullException(nameof(meshA));
            if (meshB == null) throw new ArgumentNullException(nameof(meshB));
            settings ??= MeshBooleanSettings.Default;

            var result = new EditableMesh { Name = $"Boolean_{op}" };

            // 输入为空的退化处理
            if (meshA.Faces.Count == 0 && meshB.Faces.Count == 0) return result;
            if (meshA.Faces.Count == 0)
            {
                // 仅 B 存在: Union/SymmetricDifference 返回 B, 其它返回空
                if (op == BooleanOperation.Union || op == BooleanOperation.SymmetricDifference)
                    CopyMesh(meshB, result);
                return result;
            }
            if (meshB.Faces.Count == 0)
            {
                if (op == BooleanOperation.Union || op == BooleanOperation.SymmetricDifference)
                    CopyMesh(meshA, result);
                return result;
            }

            // ---- 步骤 1: 相交切分, 使两个网格的三角形在交线上对齐 ----
            var splitA = MeshSplitter.SplitByMesh(meshA, meshB);
            var splitB = MeshSplitter.SplitByMesh(meshB, meshA);

            // ---- 步骤 2: 分类 —— 每个子三角形是否在另一(原始)网格内部 ----
            var aTris = CollectTriangles(splitA);
            var bTris = CollectTriangles(splitB);

            var aInB = new bool[aTris.Count]; // A 的子三角形是否在 B 内
            for (int i = 0; i < aTris.Count; i++)
                aInB[i] = PointInMeshTest.Contains(meshB, aTris[i].Centroid);

            var bInA = new bool[bTris.Count]; // B 的子三角形是否在 A 内
            for (int i = 0; i < bTris.Count; i++)
                bInA[i] = PointInMeshTest.Contains(meshA, bTris[i].Centroid);

            // ---- 步骤 3: 按运算类型保留 (并按需翻转腔体面) ----
            for (int i = 0; i < aTris.Count; i++)
            {
                if (KeepA(op, aInB[i]))
                    AppendTriangle(result, aTris[i], FlipA(op, aInB[i]));
            }
            for (int i = 0; i < bTris.Count; i++)
            {
                if (KeepB(op, bInA[i]))
                    AppendTriangle(result, bTris[i], FlipB(op, bInA[i]));
            }

            // ---- 步骤 4: 后处理 ----
            if (settings.bFlipBackfaces) UnifyBackfaces(result);
            if (settings.bMergeVertices) result.MergeDuplicateVertices(settings.WeldThreshold);
            if (settings.bRecalculateNormals) result.RecalculateNormals();

            return result;
        }

        /// <summary>从 EditableMesh 收集所有工作三角形 (读取法线/UV 带兜底)。</summary>
        private static List<BoolTriangle> CollectTriangles(EditableMesh mesh)
        {
            var list = new List<BoolTriangle>(mesh.Faces.Count);
            foreach (var f in mesh.Faces)
            {
                if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                list.Add(new BoolTriangle
                {
                    A = ToBoolVertex(mesh, f.A),
                    B = ToBoolVertex(mesh, f.B),
                    C = ToBoolVertex(mesh, f.C)
                });
            }
            return list;
        }

        private static BoolVertex ToBoolVertex(EditableMesh mesh, int index)
        {
            return new BoolVertex
            {
                Position = mesh.Vertices[index],
                Normal = mesh.Normals.Count > index ? mesh.Normals[index] : Vector3.UnitY,
                UV = mesh.UV0.Count > index ? mesh.UV0[index] : Vector2.Zero
            };
        }

        /// <summary>把工作三角形追加到结果 (flip=true 时反转绕序并翻转法线, 用于腔体面)。</summary>
        private static void AppendTriangle(EditableMesh mesh, BoolTriangle bt, bool flip)
        {
            int baseIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(bt.A.Position);
            mesh.Vertices.Add(bt.B.Position);
            mesh.Vertices.Add(bt.C.Position);
            mesh.Normals.Add(flip ? -bt.A.Normal : bt.A.Normal);
            mesh.Normals.Add(flip ? -bt.B.Normal : bt.B.Normal);
            mesh.Normals.Add(flip ? -bt.C.Normal : bt.C.Normal);
            mesh.UV0.Add(bt.A.UV);
            mesh.UV0.Add(bt.B.UV);
            mesh.UV0.Add(bt.C.UV);
            // 反转绕序: (0,1,2) -> (0,2,1)
            mesh.Faces.Add(flip
                ? new MeshFace(baseIdx, baseIdx + 2, baseIdx + 1)
                : new MeshFace(baseIdx, baseIdx + 1, baseIdx + 2));
        }

        /// <summary>浅拷贝源网格到目标 (顶点/法线/UV/面)。</summary>
        private static void CopyMesh(EditableMesh src, EditableMesh dst)
        {
            dst.Vertices.AddRange(src.Vertices);
            dst.Normals.AddRange(src.Normals);
            dst.UV0.AddRange(src.UV0);
            dst.Faces.AddRange(src.Faces);
        }

        // ---- 保留/翻转策略表 (inB=true 表示 A 的三角形在 B 内; inA=true 表示 B 的三角形在 A 内) ----
        //
        // | 运算            | A-outside-B | A-inside-B | B-outside-A | B-inside-A |
        // |----------------|-------------|------------|-------------|------------|
        // | Union          | 保留 不翻   | 丢弃       | 保留 不翻   | 丢弃       |
        // | Intersection   | 丢弃        | 保留 不翻  | 丢弃        | 保留 不翻  |
        // | Difference(A-B)| 保留 不翻   | 丢弃       | 丢弃        | 保留 翻转  |
        // | SymmetricDiff  | 保留 不翻   | 保留 翻转  | 保留 不翻   | 保留 翻转  |
        // | Cutout(=A-B)   | 保留 不翻   | 丢弃       | 丢弃        | 保留 翻转  |
        // -------------------------------------------------------------------------------------

        /// <summary>A 的子三角形是否保留。</summary>
        private static bool KeepA(BooleanOperation op, bool inB)
        {
            return op switch
            {
                BooleanOperation.Union => !inB,              // A 在 B 外
                BooleanOperation.Intersection => inB,        // A 在 B 内
                BooleanOperation.Difference => !inB,         // A 在 B 外
                BooleanOperation.SymmetricDifference => true,// A 在 B 外(不翻) + A 在 B 内(翻转) 全保留
                BooleanOperation.Cutout => !inB,            // 同 Difference
                _ => false
            };
        }

        /// <summary>A 的子三角形是否翻转法线/绕序。</summary>
        private static bool FlipA(BooleanOperation op, bool inB)
        {
            // 仅对称差集的 "A 在 B 内" 部分 (A\B 的腔体面) 需要翻转
            return op == BooleanOperation.SymmetricDifference && inB;
        }

        /// <summary>B 的子三角形是否保留。</summary>
        private static bool KeepB(BooleanOperation op, bool inA)
        {
            return op switch
            {
                BooleanOperation.Union => !inA,              // B 在 A 外
                BooleanOperation.Intersection => inA,        // B 在 A 内
                BooleanOperation.Difference => inA,          // B 在 A 内 (腔体面)
                BooleanOperation.SymmetricDifference => true,// 全保留 (B 在 A 内部分翻转)
                BooleanOperation.Cutout => inA,             // 同 Difference
                _ => false
            };
        }

        /// <summary>B 的子三角形是否翻转法线/绕序。</summary>
        private static bool FlipB(BooleanOperation op, bool inA)
        {
            return (op == BooleanOperation.Difference && inA)
                || (op == BooleanOperation.SymmetricDifference && inA)
                || (op == BooleanOperation.Cutout && inA);
        }

        /// <summary>
        /// 统一背面: 对每个三角形, 若其面法线与顶点平均法线方向相反, 则翻转绕序。
        /// 这是一个保守的朝向统一启发式 (适用于凸体结果)。
        /// </summary>
        private static void UnifyBackfaces(EditableMesh mesh)
        {
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                var f = mesh.Faces[i];
                if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                var a = mesh.Vertices[f.A];
                var b = mesh.Vertices[f.B];
                var c = mesh.Vertices[f.C];
                var faceNormal = Vector3.Cross(b - a, c - a);
                if (faceNormal.LengthSquared() < 1e-12f) continue;
                faceNormal = Vector3.Normalize(faceNormal);

                var avgNormal = (mesh.Normals[f.A] + mesh.Normals[f.B] + mesh.Normals[f.C]) / 3f;
                if (avgNormal.LengthSquared() < 1e-12f) continue;
                avgNormal = Vector3.Normalize(avgNormal);

                // 面法线与平均顶点法线反向 -> 翻转绕序
                if (Vector3.Dot(faceNormal, avgNormal) < 0f)
                {
                    mesh.Faces[i] = new MeshFace(f.A, f.C, f.B);
                }
            }
        }
    }

    // =================================================================================
    //  笔刷布尔 BrushOperation (连续布尔操作, 建模工具用)
    // =================================================================================

    /// <summary>
    /// 笔刷布尔 —— 对齐 UE5 Modeling 中 "连续对一个主网格应用多个布尔笔刷" 的工作流。
    /// 例如: 对一个主网格依次应用多次差集, 挖出多个洞。
    /// </summary>
    public static class BrushOperation
    {
        /// <summary>
        /// 对主网格 target 依次应用一组布尔运算, 返回最终结果。
        /// operations 为 (笔刷网格, 运算类型) 列表, 按顺序累积应用。
        /// </summary>
        public static EditableMesh Apply(EditableMesh target,
                                         List<(EditableMesh mesh, BooleanOperation op)> operations)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (operations == null || operations.Count == 0) return target;

            EditableMesh current = target;
            foreach (var (mesh, op) in operations)
            {
                if (mesh == null) continue;
                current = MeshBoolean.Execute(current, mesh, op);
            }
            return current;
        }

        /// <summary>便捷重载: 单次笔刷运算。</summary>
        public static EditableMesh Apply(EditableMesh target, EditableMesh brush, BooleanOperation op)
        {
            return MeshBoolean.Execute(target, brush, op);
        }
    }

    // =================================================================================
    //  CSG 树节点 CSGNode (构造实体几何, 二叉树表达布尔组合)
    // =================================================================================

    /// <summary>
    /// CSG (Constructive Solid Geometry) 树节点 —— 对齐 UE5 中以二叉树表达布尔组合的方式。
    /// 叶子节点持有单个网格; 内部节点持有左右子树与一个布尔运算。
    /// Evaluate() 自底向上递归求值整棵树, 得到最终合成网格。
    /// </summary>
    public class CSGNode
    {
        /// <summary>叶子节点的网格 (内部节点为 null)。</summary>
        public EditableMesh? LeafMesh;

        /// <summary>内部节点的左子树。</summary>
        public CSGNode? Left;

        /// <summary>内部节点的右子树。</summary>
        public CSGNode? Right;

        /// <summary>内部节点的布尔运算类型。</summary>
        public BooleanOperation Operation;

        /// <summary>是否为叶子节点。</summary>
        public bool IsLeaf => LeafMesh != null && Left == null && Right == null;

        /// <summary>构造叶子节点。</summary>
        public CSGNode(EditableMesh mesh)
        {
            LeafMesh = mesh;
            Operation = BooleanOperation.Union;
        }

        /// <summary>构造内部节点 (left OP right)。</summary>
        public CSGNode(CSGNode left, CSGNode right, BooleanOperation op)
        {
            Left = left;
            Right = right;
            Operation = op;
        }

        /// <summary>
        /// 递归求值整棵 CSG 树, 返回最终合成网格。
        /// 叶子直接返回其网格; 内部节点对左右子树求值结果执行布尔运算。
        /// </summary>
        public EditableMesh Evaluate()
        {
            if (IsLeaf) return LeafMesh!;

            if (Left == null && Right == null)
                return LeafMesh ?? new EditableMesh();

            var a = Left?.Evaluate() ?? new EditableMesh();
            var b = Right?.Evaluate() ?? new EditableMesh();
            return MeshBoolean.Execute(a, b, Operation);
        }

        /// <summary>便捷工厂: 叶子节点。</summary>
        public static CSGNode Leaf(EditableMesh mesh) => new(mesh);

        /// <summary>便捷工厂: 内部节点。</summary>
        public static CSGNode Combine(CSGNode left, CSGNode right, BooleanOperation op) => new(left, right, op);
    }

    // =================================================================================
    //  布尔运算调试器 MeshBooleanDebugger (返回相交线/切分三角形供编辑器可视化)
    // =================================================================================

    /// <summary>
    /// 布尔运算调试器 —— 对齐 UE5 Modeling 编辑器中对布尔运算中间结果的可视化需求。
    /// 提供相交线段端点集合与切分后的三角形集合, 供编辑器视口绘制线框/高亮。
    /// </summary>
    public class MeshBooleanDebugger
    {
        /// <summary>所有相交线段的端点集合 (每段 2 个点)。</summary>
        public List<Vector3> IntersectionPoints { get; } = new();

        /// <summary>所有相交线段 (起点, 终点)。</summary>
        public List<(Vector3 Start, Vector3 End)> IntersectionSegments { get; } = new();

        /// <summary>相交的三角形对索引 (A 面索引, B 面索引)。</summary>
        public List<(int FaceA, int FaceB)> IntersectingPairs { get; } = new();

        /// <summary>切分后 (A 沿 B 切分) 的三角形顶点列表 (每 3 个顶点为一个三角形, 供可视化)。</summary>
        public List<Vector3> SplitTrianglesA { get; } = new();

        /// <summary>切分后 (B 沿 A 切分) 的三角形顶点列表。</summary>
        public List<Vector3> SplitTrianglesB { get; } = new();

        /// <summary>分析两个网格的相交关系并填充上述可视化数据。</summary>
        public void Analyze(EditableMesh meshA, EditableMesh meshB)
        {
            IntersectionPoints.Clear();
            IntersectionSegments.Clear();
            IntersectingPairs.Clear();
            SplitTrianglesA.Clear();
            SplitTrianglesB.Clear();

            if (meshA == null || meshB == null) return;
            float eps = BooleanMath.Epsilon;

            // 预计算 B 三角形 AABB
            var bBoxes = new List<(Vector3 min, Vector3 max)>();
            foreach (var f in meshB.Faces)
            {
                if (!f.IsValidFor(meshB.Vertices.Count)) { bBoxes.Add((Vector3.Zero, Vector3.Zero)); continue; }
                BooleanMath.TriangleAABB(meshB.Vertices[f.A], meshB.Vertices[f.B], meshB.Vertices[f.C], out var mn, out var mx);
                bBoxes.Add((mn, mx));
            }

            // 遍历所有三角形对, 记录相交线段
            for (int ia = 0; ia < meshA.Faces.Count; ia++)
            {
                var fa = meshA.Faces[ia];
                if (!fa.IsValidFor(meshA.Vertices.Count)) continue;
                var a0 = meshA.Vertices[fa.A];
                var a1 = meshA.Vertices[fa.B];
                var a2 = meshA.Vertices[fa.C];
                BooleanMath.TriangleAABB(a0, a1, a2, out var amin, out var amax);

                for (int ib = 0; ib < meshB.Faces.Count; ib++)
                {
                    var (bmin, bmax) = bBoxes[ib];
                    if (!BooleanMath.AABBOverlap(amin, amax, bmin, bmax, eps)) continue;
                    var fb = meshB.Faces[ib];
                    if (!fb.IsValidFor(meshB.Vertices.Count)) continue;
                    var b0 = meshB.Vertices[fb.A];
                    var b1 = meshB.Vertices[fb.B];
                    var b2 = meshB.Vertices[fb.C];

                    var res = TriangleTriangleIntersection.ComputeIntersection(a0, a1, a2, b0, b1, b2);
                    if (!res.Intersects) continue;
                    IntersectingPairs.Add((ia, ib));
                    if (res.IsSegment)
                    {
                        IntersectionSegments.Add((res.Point1, res.Point2));
                        IntersectionPoints.Add(res.Point1);
                        IntersectionPoints.Add(res.Point2);
                    }
                }
            }

            // 切分三角形 (供编辑器可视化切分结果)
            var splitA = MeshSplitter.SplitByMesh(meshA, meshB);
            foreach (var f in splitA.Faces)
            {
                if (!f.IsValidFor(splitA.Vertices.Count)) continue;
                SplitTrianglesA.Add(splitA.Vertices[f.A]);
                SplitTrianglesA.Add(splitA.Vertices[f.B]);
                SplitTrianglesA.Add(splitA.Vertices[f.C]);
            }
            var splitB = MeshSplitter.SplitByMesh(meshB, meshA);
            foreach (var f in splitB.Faces)
            {
                if (!f.IsValidFor(splitB.Vertices.Count)) continue;
                SplitTrianglesB.Add(splitB.Vertices[f.A]);
                SplitTrianglesB.Add(splitB.Vertices[f.B]);
                SplitTrianglesB.Add(splitB.Vertices[f.C]);
            }
        }

        /// <summary>获取所有相交线段的中点 (便于编辑器以点云形式高亮相交区域)。</summary>
        public List<Vector3> GetSegmentMidpoints()
        {
            var mids = new List<Vector3>(IntersectionSegments.Count);
            foreach (var seg in IntersectionSegments)
                mids.Add((seg.Start + seg.End) * 0.5f);
            return mids;
        }
    }
}
