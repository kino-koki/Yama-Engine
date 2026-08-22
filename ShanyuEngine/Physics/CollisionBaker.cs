using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Physics
{
    // ════════════════════════════════════════════════════════════════════════
    //  CollisionBaker.cs — 碰撞烘焙系统
    //
    //  从渲染网格生成碰撞体, 提供以下功能:
    //    1. CollisionShape         —— 碰撞形状数据容器
    //    2. ConvexHullBuilder      —— QuickHull 3D 凸包算法
    //    3. TriangleMeshCollisionBaker —— 三角形网格碰撞体 (BVH + 子网格分解)
    //    4. SimplifiedCollisionGenerator —— 简化碰撞体生成 (采样 + 自动形状选择)
    //    5. HeightFieldBaker       —— 从地形网格生成高度场
    //    6. CollisionCooker        —— 完整烘焙管线 (同步/异步 + 进度回调)
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举 & 配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>碰撞形状类型。</summary>
    public enum CollisionShapeKind
    {
        Box,
        Sphere,
        Capsule,
        ConvexHull,
        TriangleMesh,
        Heightfield,
        Compound
    }

    /// <summary>碰撞烘焙选项。</summary>
    public class CollisionCookingOptions
    {
        /// <summary>目标碰撞形状类型。Auto 表示自动选择最优形状。</summary>
        public CollisionShapeKind TargetShape = CollisionShapeKind.Auto;

        /// <summary>最大简化碰撞体数量 (SimplifiedCollisionGenerator)。</summary>
        public int MaxCollisionShapes = 4;

        /// <summary>随机采样顶点数 (用于从高模生成简化碰撞体)。</summary>
        public int SampleVertexCount = 64;

        /// <summary>凸包最大顶点数。</summary>
        public int MaxConvexHullVertices = 256;

        /// <summary>子网格最大三角形数 (TriangleMesh 子网格分解)。</summary>
        public int MaxTrianglesPerSubMesh = 256;

        /// <summary>高度场分辨率 (每边的采样点数)。</summary>
        public int HeightfieldResolution = 64;

        /// <summary>高度场高度缩放因子。</summary>
        public float HeightfieldScale = 1.0f;

        /// <summary>是否使用共面合并 (减少三角形数量)。</summary>
        public bool MergeCoplanarFaces = true;

        /// <summary>共面合并阈值 (法线点积)。</summary>
        public float CoplanarThreshold = 0.999f;

        /// <summary>是否自动检测退化三角形并修复。</summary>
        public bool FixDegenerateTriangles = true;

        /// <summary>自动选择形状时的阈值参数 (AABB 长宽比容忍度)。</summary>
        public float AspectRatioTolerance = 0.3f;
    }

    // ──────────────────────────────────────────────────────────────
    // 2. CollisionShape — 碰撞形状数据容器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 碰撞形状数据容器, 存储烘焙生成的碰撞几何体。
    /// 可直接传递给 PhysicsWorld 创建物理体。
    /// </summary>
    public class CollisionShape
    {
        /// <summary>形状类型。</summary>
        public CollisionShapeKind Kind { get; set; }

        // ── Box ──
        public Vector3 HalfExtents;

        // ── Sphere ──
        public float SphereRadius;

        // ── Capsule ──
        public float CapsuleRadius;
        public float CapsuleHalfHeight;

        // ── ConvexHull ──
        public Vector3[] ConvexVertices = Array.Empty<Vector3>();
        public int[] ConvexIndices = Array.Empty<int>();

        // ── TriangleMesh ──
        public Vector3[] MeshVertices = Array.Empty<Vector3>();
        public uint[] MeshIndices = Array.Empty<uint>();
        public TriangleMeshSubMesh[] SubMeshes = Array.Empty<TriangleMeshSubMesh>();
        public BVHNode[] BvhNodes = Array.Empty<BVHNode>();

        // ── Heightfield ──
        public float[] HeightfieldHeights = Array.Empty<float>();
        public int HeightfieldWidth;
        public int HeightfieldHeight;
        public Vector3 HeightfieldOrigin;
        public float HeightfieldCellSize;
        public float HeightfieldHeightScale;

        // ── Compound ──
        public CollisionShape[] SubShapes = Array.Empty<CollisionShape>();
        public Vector3[] SubShapeOffsets = Array.Empty<Vector3>();
        public Quaternion[] SubShapeRotations = Array.Empty<Quaternion>();

        /// <summary>创建 Box 碰撞形状。</summary>
        public static CollisionShape CreateBox(Vector3 halfExtents)
        {
            return new CollisionShape
            {
                Kind = CollisionShapeKind.Box,
                HalfExtents = halfExtents
            };
        }

        /// <summary>创建 Sphere 碰撞形状。</summary>
        public static CollisionShape CreateSphere(float radius)
        {
            return new CollisionShape
            {
                Kind = CollisionShapeKind.Sphere,
                SphereRadius = radius
            };
        }

        /// <summary>创建 Capsule 碰撞形状。</summary>
        public static CollisionShape CreateCapsule(float radius, float halfHeight)
        {
            return new CollisionShape
            {
                Kind = CollisionShapeKind.Capsule,
                CapsuleRadius = radius,
                CapsuleHalfHeight = halfHeight
            };
        }

        /// <summary>创建 ConvexHull 碰撞形状。</summary>
        public static CollisionShape CreateConvexHull(Vector3[] vertices, int[] indices)
        {
            return new CollisionShape
            {
                Kind = CollisionShapeKind.ConvexHull,
                ConvexVertices = vertices,
                ConvexIndices = indices
            };
        }

        /// <summary>创建 TriangleMesh 碰撞形状。</summary>
        public static CollisionShape CreateTriangleMesh(
            Vector3[] vertices, uint[] indices,
            TriangleMeshSubMesh[] subMeshes, BVHNode[] bvhNodes)
        {
            return new CollisionShape
            {
                Kind = CollisionShapeKind.TriangleMesh,
                MeshVertices = vertices,
                MeshIndices = indices,
                SubMeshes = subMeshes,
                BvhNodes = bvhNodes
            };
        }

        /// <summary>创建 Heightfield 碰撞形状。</summary>
        public static CollisionShape CreateHeightfield(
            float[] heights, int width, int height,
            Vector3 origin, float cellSize, float heightScale)
        {
            return new CollisionShape
            {
                Kind = CollisionShapeKind.Heightfield,
                HeightfieldHeights = heights,
                HeightfieldWidth = width,
                HeightfieldHeight = height,
                HeightfieldOrigin = origin,
                HeightfieldCellSize = cellSize,
                HeightfieldHeightScale = heightScale
            };
        }

        /// <summary>创建 Compound 碰撞形状。</summary>
        public static CollisionShape CreateCompound(
            CollisionShape[] shapes, Vector3[] offsets, Quaternion[] rotations)
        {
            return new CollisionShape
            {
                Kind = CollisionShapeKind.Compound,
                SubShapes = shapes,
                SubShapeOffsets = offsets,
                SubShapeRotations = rotations
            };
        }

        /// <summary>
        /// 将碰撞形状转换为 PhysicsWorld.BodyDescriptor 可用的参数。
        /// </summary>
        public BodyDescriptor ToBodyDescriptor(int entityId = 0,
            Vector3? position = null, float mass = 1f,
            ushort layer = PhysicsLayers.Default, ushort mask = PhysicsLayers.All)
        {
            var desc = new BodyDescriptor
            {
                EntityId = entityId,
                Position = position ?? Vector3.Zero,
                Mass = mass,
                ObjectLayer = layer,
                CollisionMask = mask
            };

            switch (Kind)
            {
                case CollisionShapeKind.Box:
                    desc.WithBox(HalfExtents);
                    break;
                case CollisionShapeKind.Sphere:
                    desc.WithSphere(SphereRadius);
                    break;
                case CollisionShapeKind.Capsule:
                    desc.WithCapsule(CapsuleRadius, CapsuleHalfHeight * 2f);
                    break;
                case CollisionShapeKind.ConvexHull:
                    // ConvexHull 在 Jolt 中作为 Mesh 形状使用
                    desc.WithMesh(ConvexVertices, ConvexIndices.Select(i => (uint)i).ToArray());
                    break;
                case CollisionShapeKind.TriangleMesh:
                    desc.WithMesh(MeshVertices, MeshIndices);
                    break;
                default:
                    desc.WithBox(HalfExtents);
                    break;
            }

            return desc;
        }
    }

    /// <summary>三角形网格子网格描述。</summary>
    public struct TriangleMeshSubMesh
    {
        /// <summary>起始三角形索引 (索引缓冲区中的偏移, 以3个索引为一个三角形)。</summary>
        public int StartTriangle;

        /// <summary>三角形数量。</summary>
        public int TriangleCount;

        /// <summary>AABB 最小值。</summary>
        public Vector3 BoundsMin;

        /// <summary>AABB 最大值。</summary>
        public Vector3 BoundsMax;
    }

    /// <summary>BVH 节点。</summary>
    public struct BVHNode
    {
        /// <summary>AABB 最小值。</summary>
        public Vector3 Min;

        /// <summary>AABB 最大值。</summary>
        public Vector3 Max;

        /// <summary>左子节点索引 (叶节点时为 -1)。</summary>
        public int LeftChild;

        /// <summary>右子节点索引 (叶节点时为 -1)。</summary>
        public int RightChild;

        /// <summary>叶节点时: 起始三角形索引。</summary>
        public int StartTriangle;

        /// <summary>叶节点时: 三角形数量。</summary>
        public int TriangleCount;

        /// <summary>是否为叶节点。</summary>
        public bool IsLeaf => LeftChild < 0 && RightChild < 0;
    }

    // ──────────────────────────────────────────────────────────────
    // 3. MeshData — 碰撞烘焙用网格数据
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 碰撞烘焙使用的网格数据。与渲染网格解耦, 仅保留几何信息。
    /// </summary>
    public class CollisionMeshData
    {
        /// <summary>顶点位置数组。</summary>
        public Vector3[] Vertices = Array.Empty<Vector3>();

        /// <summary>三角形索引数组 (每3个索引构成一个三角形)。</summary>
        public int[] Indices = Array.Empty<int>();

        /// <summary>从 DCCMeshData 创建 CollisionMeshData。</summary>
        public static CollisionMeshData FromDCCMeshData(DCC.DCCMeshData dccMesh)
        {
            if (dccMesh == null) return new CollisionMeshData();

            var vertices = dccMesh.Vertices
                .Select(v => new Vector3(v.Position.X, v.Position.Y, v.Position.Z))
                .ToArray();

            return new CollisionMeshData
            {
                Vertices = vertices,
                Indices = dccMesh.Indices.ToArray()
            };
        }

        /// <summary>从原始数组创建。</summary>
        public static CollisionMeshData Create(Vector3[] vertices, int[] indices)
        {
            return new CollisionMeshData
            {
                Vertices = vertices ?? Array.Empty<Vector3>(),
                Indices = indices ?? Array.Empty<int>()
            };
        }

        /// <summary>计算 AABB。</summary>
        public Assets.BoundingBox ComputeAABB()
        {
            return Assets.BoundingBox.FromPoints(Vertices.ToList());
        }

        /// <summary>获取三角形数量。</summary>
        public int TriangleCount => Indices.Length / 3;
    }

    // ──────────────────────────────────────────────────────────────
    // 4. ConvexHullBuilder — 3D QuickHull 凸包算法
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 3D QuickHull 凸包生成器。
    /// 算法步骤: 初始四面体 -> 迭代添加最远点 -> 面分裂 -> 邻接面删除 -> 合并共面。
    /// </summary>
    public class ConvexHullBuilder
    {
        private const float Epsilon = 1e-6f;

        // 内部数据结构
        private struct HullFace
        {
            public int A, B, C;          // 顶点索引 (逆时针方向, 从外侧看)
            public Vector3 Normal;       // 外法线
            public float Distance;       // 原点到平面的有符号距离
            public List<int> VisiblePoints; // 面外侧可见的点
        }

        private struct HullEdge
        {
            public int From, To;
        }

        private readonly float _coplanarThreshold;
        private readonly bool _mergeCoplanar;

        /// <summary>
        /// 创建 ConvexHullBuilder。
        /// </summary>
        /// <param name="mergeCoplanar">是否合并共面三角面。</param>
        /// <param name="coplanarThreshold">共面判定阈值 (法线点积)。</param>
        public ConvexHullBuilder(bool mergeCoplanar = true, float coplanarThreshold = 0.999f)
        {
            _mergeCoplanar = mergeCoplanar;
            _coplanarThreshold = coplanarThreshold;
        }

        /// <summary>
        /// 从顶点数组构建凸包。
        /// </summary>
        /// <param name="inputVertices">输入顶点。</param>
        /// <param name="maxVertices">最大输出顶点数 (0 = 不限制)。</param>
        /// <returns>凸包三角形索引数组 (每3个索引为一个三角形)。</returns>
        public int[] Build(Vector3[] inputVertices, int maxVertices = 0)
        {
            if (inputVertices == null || inputVertices.Length < 4)
                return Array.Empty<int>();

            // 1. 去重
            var vertices = RemoveDuplicates(inputVertices);
            if (vertices.Count < 4)
                return Array.Empty<int>();

            // 2. 如果有最大顶点限制, 先采样
            if (maxVertices > 0 && vertices.Count > maxVertices)
            {
                vertices = SampleVertices(vertices, maxVertices);
            }

            // 3. 检测并修复退化情况 (所有点共面或共线)
            if (!DetectNonDegenerate(vertices, out var tetraIndices))
            {
                // 退化: 所有点共面, 返回三角化平面
                return BuildDegenerateHull(vertices);
            }

            // 4. QuickHull 主算法
            var faces = QuickHull(vertices, tetraIndices);

            // 5. 提取三角形索引
            var indices = new List<int>();
            foreach (var face in faces)
            {
                indices.Add(face.A);
                indices.Add(face.B);
                indices.Add(face.C);
            }

            return indices.ToArray();
        }

        /// <summary>构建凸包并返回简化顶点和索引。</summary>
        public void Build(Vector3[] inputVertices, out Vector3[] hullVertices, out int[] hullIndices, int maxVertices = 0)
        {
            hullIndices = Build(inputVertices, maxVertices);
            var vertexSet = new HashSet<int>();
            foreach (var idx in hullIndices)
                vertexSet.Add(idx);

            hullVertices = new Vector3[vertexSet.Count];
            var oldToNew = new Dictionary<int, int>();
            var newIdx = 0;
            foreach (var oldIdx in vertexSet)
            {
                oldToNew[oldIdx] = newIdx;
                hullVertices[newIdx] = inputVertices[oldIdx];
                newIdx++;
            }

            for (int i = 0; i < hullIndices.Length; i++)
            {
                hullIndices[i] = oldToNew[hullIndices[i]];
            }
        }

        // ── 内部方法 ──

        private List<Vector3> RemoveDuplicates(Vector3[] vertices)
        {
            var result = new List<Vector3>();
            var set = new HashSet<string>();
            foreach (var v in vertices)
            {
                var key = $"{v.X:F6},{v.Y:F6},{v.Z:F6}";
                if (set.Add(key))
                    result.Add(v);
            }
            return result;
        }

        private List<Vector3> SampleVertices(List<Vector3> vertices, int count)
        {
            var rng = new Random(42); // 固定种子保证确定性
            var shuffled = vertices.OrderBy(_ => rng.Next()).ToList();
            return shuffled.Take(count).ToList();
        }

        private bool DetectNonDegenerate(List<Vector3> vertices, out int[] tetraIndices)
        {
            tetraIndices = new int[4];

            // 尝试找到4个不共面的点构成初始四面体
            tetraIndices[0] = 0;

            // 找到与第0个点距离最远的点
            int farthest = FindFarthestPoint(vertices, vertices[0], out _);
            tetraIndices[1] = farthest;

            // 找到距离0-1线段最远的点
            int lineFarthest = FindFarthestFromLine(vertices,
                vertices[tetraIndices[0]], vertices[tetraIndices[1]], out _);
            tetraIndices[2] = lineFarthest;

            // 检查这3个点是否共线
            var edge1 = vertices[tetraIndices[1]] - vertices[tetraIndices[0]];
            var edge2 = vertices[tetraIndices[2]] - vertices[tetraIndices[0]];
            var cross = Vector3.Cross(edge1, edge2);
            if (cross.LengthSquared() < Epsilon)
                return false;

            // 找到距离平面0-1-2最远的点 (在两侧)
            var planeNormal = Vector3.Normalize(cross);
            var planePoint = vertices[tetraIndices[0]];
            int bestAbove = -1, bestBelow = -1;
            float maxDistAbove = 0, maxDistBelow = 0;

            for (int i = 0; i < vertices.Count; i++)
            {
                if (i == tetraIndices[0] || i == tetraIndices[1] || i == tetraIndices[2])
                    continue;
                var d = Vector3.Dot(vertices[i] - planePoint, planeNormal);
                if (d > maxDistAbove) { maxDistAbove = d; bestAbove = i; }
                if (d < maxDistBelow) { maxDistBelow = d; bestBelow = i; }
            }

            // 如果两侧都没有足够远的点, 说明所有点共面
            if (maxDistAbove < Epsilon && maxDistBelow > -Epsilon)
                return false;

            tetraIndices[3] = maxDistAbove >= -maxDistBelow ? bestAbove : bestBelow;
            return true;
        }

        private int FindFarthestPoint(List<Vector3> vertices, Vector3 origin, out float maxDist)
        {
            maxDist = 0;
            int idx = 1;
            for (int i = 1; i < vertices.Count; i++)
            {
                var d = (vertices[i] - origin).LengthSquared();
                if (d > maxDist) { maxDist = d; idx = i; }
            }
            return idx;
        }

        private int FindFarthestFromLine(List<Vector3> vertices, Vector3 a, Vector3 b, out float maxDist)
        {
            var ab = b - a;
            float abLenSq = ab.LengthSquared();
            maxDist = 0;
            int idx = 0;
            for (int i = 0; i < vertices.Count; i++)
            {
                if (i == 0) continue; // a
                var ap = vertices[i] - a;
                float t = Vector3.Dot(ap, ab) / Math.Max(abLenSq, Epsilon);
                t = Math.Clamp(t, 0f, 1f);
                var closest = a + ab * t;
                var d = (vertices[i] - closest).LengthSquared();
                if (d > maxDist) { maxDist = d; idx = i; }
            }
            return idx;
        }

        private int[] BuildDegenerateHull(List<Vector3> vertices)
        {
            // 退化情况: 所有点共面 -> 2D 凸包 -> 扇形三角化
            // 计算平均法线
            var centroid = Vector3.Zero;
            foreach (var v in vertices) centroid += v;
            centroid /= vertices.Count;

            // PCA 找到平面法线
            var normal = ComputePlaneNormal(vertices);
            if (normal.LengthSquared() < Epsilon)
                return Array.Empty<int>();

            normal = Vector3.Normalize(normal);

            // 在平面上建立2D坐标系
            var u = Vector3.Normalize(Vector3.Cross(normal, Math.Abs(normal.Y) < 0.9f
                ? Vector3.UnitY : Vector3.UnitX));
            var vAxis = Vector3.Cross(normal, u);

            // 投影到2D
            var points2D = new List<(float x, float y, int idx)>();
            for (int i = 0; i < vertices.Count; i++)
            {
                var delta = vertices[i] - centroid;
                points2D.Add((Vector3.Dot(delta, u), Vector3.Dot(delta, vAxis), i));
            }

            // 2D convex hull (Andrew's monotone chain)
            var hull2D = ConvexHull2D(points2D);

            // 扇形三角化
            var indices = new List<int>();
            if (hull2D.Count >= 3)
            {
                int center = hull2D[0];
                for (int i = 1; i < hull2D.Count - 1; i++)
                {
                    indices.Add(center);
                    indices.Add(hull2D[i]);
                    indices.Add(hull2D[i + 1]);
                }
            }

            return indices.ToArray();
        }

        private Vector3 ComputePlaneNormal(List<Vector3> vertices)
        {
            var centroid = Vector3.Zero;
            foreach (var v in vertices) centroid += v;
            centroid /= vertices.Count;

            // 协方差矩阵的3x3
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var v in vertices)
            {
                var d = v - centroid;
                xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
                yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
            }
            int n = vertices.Count;

            // 最小特征值对应的特征向量 = 平面法线
            // 简化实现: 使用幂迭代找最小特征向量
            var normal = new Vector3(1, 0, 0);
            for (int iter = 0; iter < 32; iter++)
            {
                var m = new Vector3(
                    xx * normal.X + xy * normal.Y + xz * normal.Z,
                    xy * normal.X + yy * normal.Y + yz * normal.Z,
                    xz * normal.X + yz * normal.Y + zz * normal.Z);
                float len = m.Length();
                if (len < Epsilon) break;
                normal = m / len;
            }

            // 反转: 如果 normal 指向最大方差方向, 取反
            float dot = xx * normal.X * normal.X + yy * normal.Y * normal.Y + zz * normal.Z * normal.Z
                       + 2 * xy * normal.X * normal.Y + 2 * xz * normal.X * normal.Z + 2 * yz * normal.Y * normal.Z;
            if (dot > (xx + yy + zz) / (3 * n))
                normal = -normal;

            return normal;
        }

        private List<int> ConvexHull2D(List<(float x, float y, int idx)> points)
        {
            var sorted = points.OrderBy(p => p.x).ThenBy(p => p.y).ToList();
            if (sorted.Count <= 1)
                return sorted.Select(p => p.idx).ToList();

            var lower = new List<int>();
            foreach (var p in sorted)
            {
                while (lower.Count >= 2)
                {
                    var a = sorted[lower[lower.Count - 2]];
                    var b = sorted[lower[lower.Count - 1]];
                    var cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                    if (cross <= 0) lower.RemoveAt(lower.Count - 1);
                    else break;
                }
                lower.Add(sorted.IndexOf(p));
            }

            var upper = new List<int>();
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                var p = sorted[i];
                while (upper.Count >= 2)
                {
                    var a = sorted[upper[upper.Count - 2]];
                    var b = sorted[upper[upper.Count - 1]];
                    var cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                    if (cross <= 0) upper.RemoveAt(upper.Count - 1);
                    else break;
                }
                upper.Add(sorted.IndexOf(p));
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);

            return lower;
        }

        private List<HullFace> QuickHull(List<Vector3> vertices, int[] tetraIndices)
        {
            // 建立初始四面体
            var faces = new List<HullFace>();
            var p0 = vertices[tetraIndices[0]];
            var p1 = vertices[tetraIndices[1]];
            var p2 = vertices[tetraIndices[2]];
            var p3 = vertices[tetraIndices[3]];

            // 确保四面体面的法线朝外
            AddFace(faces, vertices, tetraIndices[0], tetraIndices[1], tetraIndices[2], p3);
            AddFace(faces, vertices, tetraIndices[0], tetraIndices[3], tetraIndices[1], p2);
            AddFace(faces, vertices, tetraIndices[0], tetraIndices[2], tetraIndices[3], p1);
            AddFace(faces, vertices, tetraIndices[1], tetraIndices[3], tetraIndices[2], p0);

            // 将所有点分配到可见面
            var assigned = new HashSet<int>();
            foreach (var idx in tetraIndices) assigned.Add(idx);

            for (int i = 0; i < vertices.Count; i++)
            {
                if (assigned.Contains(i)) continue;
                AssignPointToFace(faces, vertices, i);
            }

            // 迭代处理
            var conflictQueue = new Queue<int>();
            foreach (var face in faces)
            {
                foreach (var pt in face.VisiblePoints)
                    conflictQueue.Enqueue(pt);
                face.VisiblePoints.Clear();
            }

            while (conflictQueue.Count > 0)
            {
                int pointIdx = conflictQueue.Dequeue();
                var point = vertices[pointIdx];

                // 找到最远可见面
                HullFace? farthestFace = null;
                float maxDist = 0;
                foreach (var face in faces)
                {
                    var d = SignedDistance(point, face);
                    if (d > maxDist)
                    {
                        maxDist = d;
                        farthestFace = face;
                    }
                }

                if (farthestFace == null || maxDist < Epsilon)
                    continue;

                // 收集所有可见面
                var visibleFaces = new List<HullFace>();
                var horizonEdges = new List<HullEdge>();

                foreach (var face in faces)
                {
                    if (SignedDistance(point, face) > Epsilon)
                    {
                        visibleFaces.Add(face);
                    }
                }

                // 找到地平线边 (可见面和不可见面的交界)
                foreach (var vFace in visibleFaces)
                {
                    var edges = new[] {
                        (vFace.A, vFace.B),
                        (vFace.B, vFace.C),
                        (vFace.C, vFace.A)
                    };

                    foreach (var (from, to) in edges)
                    {
                        if (!IsEdgeSharedByVisibleFace(faces, visibleFaces, from, to))
                        {
                            horizonEdges.Add(new HullEdge { From = to, To = from }); // 反向
                        }
                    }
                }

                // 删除可见面, 保存其冲突点
                var savedConflictPoints = new List<int>();
                foreach (var vf in visibleFaces)
                {
                    savedConflictPoints.AddRange(vf.VisiblePoints);
                }
                faces.RemoveAll(f => visibleFaces.Contains(f));

                // 从地平线边创建新面
                foreach (var edge in horizonEdges)
                {
                    var newFace = CreateFace(vertices, edge.From, edge.To, pointIdx);
                    faces.Add(newFace);

                    // 重新分配冲突点
                    foreach (var cp in savedConflictPoints)
                    {
                        var d = SignedDistance(vertices[cp], newFace);
                        if (d > Epsilon)
                            newFace.VisiblePoints.Add(cp);
                    }
                }

                // 将冲突点重新入队
                foreach (var face in faces)
                {
                    while (face.VisiblePoints.Count > 0)
                        conflictQueue.Enqueue(face.VisiblePoints[0]);
                    // VisiblePoints 会在下次处理时重新分配
                }
            }

            // 共面合并
            if (_mergeCoplanar)
            {
                faces = MergeCoplanarFaces(faces, vertices);
            }

            return faces;
        }

        private void AddFace(List<HullFace> faces, List<Vector3> vertices, int a, int b, int c, Vector3 opposite)
        {
            var normal = ComputeFaceNormal(vertices[a], vertices[b], vertices[c]);
            var center = (vertices[a] + vertices[b] + vertices[c]) / 3f;
            if (Vector3.Dot(normal, opposite - center) > 0)
            {
                // 法线朝向对面点, 翻转
                (b, c) = (c, b);
                normal = -normal;
            }

            faces.Add(new HullFace
            {
                A = a, B = b, C = c,
                Normal = normal,
                Distance = Vector3.Dot(normal, vertices[a]),
                VisiblePoints = new List<int>()
            });
        }

        private HullFace CreateFace(List<Vector3> vertices, int a, int b, int c)
        {
            var normal = ComputeFaceNormal(vertices[a], vertices[b], vertices[c]);
            return new HullFace
            {
                A = a, B = b, C = c,
                Normal = normal,
                Distance = Vector3.Dot(normal, vertices[a]),
                VisiblePoints = new List<int>()
            };
        }

        private Vector3 ComputeFaceNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var n = Vector3.Cross(ab, ac);
            float len = n.Length();
            return len > Epsilon ? n / len : n;
        }

        private float SignedDistance(Vector3 point, HullFace face)
        {
            return Vector3.Dot(face.Normal, point) - face.Distance;
        }

        private void AssignPointToFace(List<HullFace> faces, List<Vector3> vertices, int pointIdx)
        {
            var point = vertices[pointIdx];
            HullFace? bestFace = null;
            float maxDist = Epsilon;

            foreach (var face in faces)
            {
                var d = SignedDistance(point, face);
                if (d > maxDist)
                {
                    maxDist = d;
                    bestFace = face;
                }
            }

            bestFace?.VisiblePoints.Add(pointIdx);
        }

        private bool IsEdgeSharedByVisibleFace(List<HullFace> allFaces, List<HullFace> visibleFaces, int from, int to)
        {
            foreach (var face in allFaces)
            {
                if (!visibleFaces.Contains(face)) continue;
                if ((face.A == from && face.B == to) ||
                    (face.B == from && face.C == to) ||
                    (face.C == from && face.A == to))
                    return true;
            }
            return false;
        }

        private List<HullFace> MergeCoplanarFaces(List<HullFace> faces, List<Vector3> vertices)
        {
            var result = new List<HullFace>();
            var merged = new bool[faces.Count];

            for (int i = 0; i < faces.Count; i++)
            {
                if (merged[i]) continue;

                var currentFace = faces[i];
                var mergedVertices = new HashSet<int> { currentFace.A, currentFace.B, currentFace.C };

                bool didMerge;
                do
                {
                    didMerge = false;
                    for (int j = i + 1; j < faces.Count; j++)
                    {
                        if (merged[j]) continue;
                        if (Vector3.Dot(currentFace.Normal, faces[j].Normal) >= _coplanarThreshold)
                        {
                            mergedVertices.Add(faces[j].A);
                            mergedVertices.Add(faces[j].B);
                            mergedVertices.Add(faces[j].C);
                            merged[j] = true;
                            didMerge = true;
                        }
                    }
                } while (didMerge);

                // 从合并后的顶点重新三角化 (扇形)
                var verts = mergedVertices.ToList();
                if (verts.Count >= 3)
                {
                    for (int k = 1; k < verts.Count - 1; k++)
                    {
                        result.Add(new HullFace
                        {
                            A = verts[0],
                            B = verts[k],
                            C = verts[k + 1],
                            Normal = currentFace.Normal,
                            Distance = currentFace.Distance,
                            VisiblePoints = new List<int>()
                        });
                    }
                }

                merged[i] = true;
            }

            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. TriangleMeshCollisionBaker — 三角形网格碰撞体
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 三角形网格碰撞体生成器。
    /// 从索引三角形列表创建 BVH 加速结构, 支持分解为子网格。
    /// </summary>
    public class TriangleMeshCollisionBaker
    {
        /// <summary>
        /// 从网格数据创建带 BVH 的三角形网格碰撞形状。
        /// </summary>
        public CollisionShape Bake(CollisionMeshData meshData, int maxTrianglesPerSubMesh = 256)
        {
            if (meshData == null || meshData.TriangleCount == 0)
                throw new ArgumentException("Mesh data is empty or has no triangles.");

            var vertices = meshData.Vertices;
            var indices = meshData.Indices;

            // 修复退化三角形
            var cleanIndices = FixDegenerateTriangles(vertices, indices);

            // 分解为子网格
            var subMeshes = DecomposeIntoSubMeshes(vertices, cleanIndices, maxTrianglesPerSubMesh);

            // 构建 BVH
            var bvhNodes = BuildBVH(vertices, cleanIndices);

            return CollisionShape.CreateTriangleMesh(
                vertices,
                cleanIndices.Select(i => (uint)i).ToArray(),
                subMeshes,
                bvhNodes
            );
        }

        /// <summary>将三角形网格分解为不超过 maxTriangles 个三角形的子网格。</summary>
        private TriangleMeshSubMesh[] DecomposeIntoSubMeshes(
            Vector3[] vertices, int[] indices, int maxTriangles)
        {
            int totalTriangles = indices.Length / 3;
            var subMeshList = new List<TriangleMeshSubMesh>();

            if (totalTriangles <= maxTriangles)
            {
                var aabb = ComputeTriangleAABB(vertices, indices, 0, totalTriangles);
                subMeshList.Add(new TriangleMeshSubMesh
                {
                    StartTriangle = 0,
                    TriangleCount = totalTriangles,
                    BoundsMin = aabb.Min,
                    BoundsMax = aabb.Max
                });
                return subMeshList.ToArray();
            }

            // 使用空间中值分割递归分解
            DecomposeRecursive(vertices, indices, 0, totalTriangles, maxTriangles, subMeshList);
            return subMeshList.ToArray();
        }

        private void DecomposeRecursive(
            Vector3[] vertices, int[] indices,
            int startTriangle, int triangleCount, int maxTriangles,
            List<TriangleMeshSubMesh> result)
        {
            if (triangleCount <= maxTriangles)
            {
                var aabb = ComputeTriangleAABB(vertices, indices, startTriangle, triangleCount);
                result.Add(new TriangleMeshSubMesh
                {
                    StartTriangle = startTriangle,
                    TriangleCount = triangleCount,
                    BoundsMin = aabb.Min,
                    BoundsMax = aabb.Max
                });
                return;
            }

            // 计算 AABB 和分裂轴
            var aabbFull = ComputeTriangleAABB(vertices, indices, startTriangle, triangleCount);
            var extents = aabbFull.Max - aabbFull.Min;

            // 选择最长轴
            int splitAxis = 0;
            if (extents.Y > extents.X) splitAxis = 1;
            if (extents.Z > (splitAxis == 0 ? extents.X : extents.Y)) splitAxis = 2;

            // 按三角形中心沿分裂轴排序
            var triIndices = new List<int>();
            for (int i = startTriangle; i < startTriangle + triangleCount; i++)
                triIndices.Add(i);

            triIndices.Sort((a, b) =>
            {
                var ca = TriangleCentroid(vertices, indices, a);
                var cb = TriangleCentroid(vertices, indices, b);
                return splitAxis switch
                {
                    0 => ca.X.CompareTo(cb.X),
                    1 => ca.Y.CompareTo(cb.Y),
                    _ => ca.Z.CompareTo(cb.Z)
                };
            });

            // 中值分割
            int mid = triIndices.Count / 2;

            // 重新排列 indices 数组中的三角形
            var sortedIndices = new int[indices.Length];
            Array.Copy(indices, sortedIndices, indices.Length);

            // 复制前半部分
            int writePos = startTriangle * 3;
            for (int i = 0; i < mid; i++)
            {
                var src = triIndices[i] * 3;
                sortedIndices[writePos++] = indices[src];
                sortedIndices[writePos++] = indices[src + 1];
                sortedIndices[writePos++] = indices[src + 2];
            }
            // 复制后半部分
            for (int i = mid; i < triIndices.Count; i++)
            {
                var src = triIndices[i] * 3;
                sortedIndices[writePos++] = indices[src];
                sortedIndices[writePos++] = indices[src + 1];
                sortedIndices[writePos++] = indices[src + 2];
            }

            // 写回
            Array.Copy(sortedIndices, indices, indices.Length);

            DecomposeRecursive(vertices, indices, startTriangle, mid, maxTriangles, result);
            DecomposeRecursive(vertices, indices, startTriangle + mid, triangleCount - mid, maxTriangles, result);
        }

        private Vector3 TriangleCentroid(Vector3[] vertices, int[] indices, int triIdx)
        {
            int i0 = indices[triIdx * 3];
            int i1 = indices[triIdx * 3 + 1];
            int i2 = indices[triIdx * 3 + 2];
            return (vertices[i0] + vertices[i1] + vertices[i2]) / 3f;
        }

        private Assets.BoundingBox ComputeTriangleAABB(Vector3[] vertices, int[] indices, int startTri, int count)
        {
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            for (int t = 0; t < count; t++)
            {
                for (int v = 0; v < 3; v++)
                {
                    var vert = vertices[indices[(startTri + t) * 3 + v]];
                    min = Vector3.Min(min, vert);
                    max = Vector3.Max(max, vert);
                }
            }

            return new Assets.BoundingBox { Min = min, Max = max };
        }

        /// <summary>构建 BVH 加速结构。</summary>
        private BVHNode[] BuildBVH(Vector3[] vertices, int[] indices)
        {
            int totalTriangles = indices.Length / 3;
            if (totalTriangles == 0) return Array.Empty<BVHNode>();

            var nodes = new List<BVHNode>();
            var triRanges = new Queue<(int start, int count)>();
            triRanges.Enqueue((0, totalTriangles));

            while (triRanges.Count > 0)
            {
                var (start, count) = triRanges.Dequeue();
                var aabb = ComputeTriangleAABB(vertices, indices, start, count);

                if (count <= 4 || nodes.Count > 1000) // 叶节点
                {
                    nodes.Add(new BVHNode
                    {
                        Min = aabb.Min,
                        Max = aabb.Max,
                        LeftChild = -1,
                        RightChild = -1,
                        StartTriangle = start,
                        TriangleCount = count
                    });
                }
                else
                {
                    int nodeIdx = nodes.Count;
                    nodes.Add(new BVHNode { Min = aabb.Min, Max = aabb.Max }); // 占位

                    var extents = aabb.Max - aabb.Min;
                    int splitAxis = 0;
                    if (extents.Y > extents.X) splitAxis = 1;
                    if (extents.Z > (splitAxis == 0 ? extents.X : extents.Y)) splitAxis = 2;

                    int mid = count / 2;
                    SortTrianglesByAxis(vertices, indices, start, count, splitAxis, mid);

                    int leftIdx = nodes.Count;
                    triRanges.Enqueue((start, mid));
                    int rightIdx = nodes.Count + 1;
                    triRanges.Enqueue((start + mid, count - mid));

                    // 更新占位节点
                    nodes[nodeIdx] = new BVHNode
                    {
                        Min = aabb.Min,
                        Max = aabb.Max,
                        LeftChild = leftIdx,
                        RightChild = rightIdx,
                        StartTriangle = -1,
                        TriangleCount = 0
                    };
                }
            }

            return nodes.ToArray();
        }

        private void SortTrianglesByAxis(Vector3[] vertices, int[] indices, int startTri, int count, int axis, int pivot)
        {
            var triIndices = Enumerable.Range(startTri, count).ToList();
            triIndices.Sort((a, b) =>
            {
                var ca = TriangleCentroid(vertices, indices, a);
                var cb = TriangleCentroid(vertices, indices, b);
                return axis switch
                {
                    0 => ca.X.CompareTo(cb.X),
                    1 => ca.Y.CompareTo(cb.Y),
                    _ => ca.Z.CompareTo(cb.Z)
                };
            });

            var sortedIndices = new int[count * 3];
            for (int i = 0; i < count; i++)
            {
                var src = triIndices[i] * 3;
                sortedIndices[i * 3] = indices[src];
                sortedIndices[i * 3 + 1] = indices[src + 1];
                sortedIndices[i * 3 + 2] = indices[src + 2];
            }

            Array.Copy(sortedIndices, 0, indices, startTri * 3, count * 3);
        }

        /// <summary>检测并修复退化三角形 (面积接近0)。</summary>
        private int[] FixDegenerateTriangles(Vector3[] vertices, int[] indices)
        {
            var result = new List<int>(indices.Length);
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];

                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() > 1e-10f)
                {
                    result.Add(indices[i]);
                    result.Add(indices[i + 1]);
                    result.Add(indices[i + 2]);
                }
                // 退化三角形被丢弃
            }
            return result.ToArray();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. SimplifiedCollisionGenerator — 简化碰撞体生成
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 简化碰撞体生成器。从高模网格生成简化的碰撞体,
    /// 使用随机采样顶点 + ConvexHull, 自动选择最优碰撞形状。
    /// </summary>
    public class SimplifiedCollisionGenerator
    {
        private readonly ConvexHullBuilder _convexBuilder = new();

        /// <summary>
        /// 生成简化碰撞形状 (自动选择最优类型)。
        /// </summary>
        /// <param name="meshData">输入网格数据。</param>
        /// <param name="options">烘焙选项。</param>
        /// <returns>最优碰撞形状。</returns>
        public CollisionShape Generate(CollisionMeshData meshData, CollisionCookingOptions options)
        {
            if (meshData == null || meshData.Vertices.Length == 0)
                throw new ArgumentException("Mesh data is empty.");

            var aabb = meshData.ComputeAABB();
            var center = aabb.Center;
            var extents = aabb.Extents;

            // 基于AABB比例选择最优碰撞形状
            var shapeKind = AutoSelectShape(extents, options.AspectRatioTolerance);

            return shapeKind switch
            {
                CollisionShapeKind.Box => GenerateBox(aabb),
                CollisionShapeKind.Sphere => GenerateSphere(extents),
                CollisionShapeKind.Capsule => GenerateCapsule(extents),
                _ => GenerateConvexHull(meshData, options)
            };
        }

        /// <summary>
        /// 生成多个简化碰撞形状 (Compound)。
        /// </summary>
        public CollisionShape GenerateCompound(CollisionMeshData meshData, CollisionCookingOptions options)
        {
            if (meshData == null || meshData.Vertices.Length == 0)
                throw new ArgumentException("Mesh data is empty.");

            if (options.MaxCollisionShapes <= 1)
                return Generate(meshData, options);

            var aabb = meshData.ComputeAABB();
            var shapes = new List<CollisionShape>();
            var offsets = new List<Vector3>();
            var rotations = new List<Quaternion>();

            // 将AABB空间分割为多个区域
            var subRegions = PartitionSpace(meshData, options.MaxCollisionShapes);

            foreach (var region in subRegions)
            {
                var subShape = GenerateConvexHull(region.MeshData, options);
                shapes.Add(subShape);
                offsets.Add(region.Offset);
                rotations.Add(Quaternion.Identity);
            }

            if (shapes.Count == 1)
                return shapes[0];

            return CollisionShape.CreateCompound(
                shapes.ToArray(), offsets.ToArray(), rotations.ToArray());
        }

        /// <summary>
        /// 自动选择最优碰撞形状类型。
        /// 基于 AABB 三个轴的比例来判断:
        ///   - 三个轴比例接近 → Sphere
        ///   - 两个轴接近, 第三轴较长 → Capsule
        ///   - 三轴比例差异不大 → Box
        ///   - 不规则 → ConvexHull
        /// </summary>
        private CollisionShapeKind AutoSelectShape(Vector3 extents, float tolerance)
        {
            float maxExtent = Math.Max(Math.Max(extents.X, extents.Y), extents.Z);
            if (maxExtent < 1e-6f) return CollisionShapeKind.Box;

            float ratioX = extents.X / maxExtent;
            float ratioY = extents.Y / maxExtent;
            float ratioZ = extents.Z / maxExtent;

            // 接近球体: 所有轴比例 > 1 - tolerance
            if (Math.Min(ratioX, Math.Min(ratioY, ratioZ)) > 1f - tolerance)
                return CollisionShapeKind.Sphere;

            // 胶囊: 两个轴接近, 第三个轴明显不同
            float diffXY = Math.Abs(ratioX - ratioY);
            float diffXZ = Math.Abs(ratioX - ratioZ);
            float diffYZ = Math.Abs(ratioY - ratioZ);

            if (Math.Min(diffXY, Math.Min(diffXZ, diffYZ)) < tolerance)
            {
                // 哪两个轴最接近?
                if (diffXY < diffXZ && diffXY < diffYZ)
                    return CollisionShapeKind.Capsule; // X/Y 接近, Z 是长轴
                if (diffXZ < diffYZ)
                    return CollisionShapeKind.Capsule; // X/Z 接近, Y 是长轴
                return CollisionShapeKind.Capsule; // Y/Z 接近, X 是长轴
            }

            // Box: 三轴差异适中
            if (Math.Max(ratioX, Math.Max(ratioY, ratioZ)) - Math.Min(ratioX, Math.Min(ratioY, ratioZ)) < 0.5f)
                return CollisionShapeKind.Box;

            // 不规则形状
            return CollisionShapeKind.ConvexHull;
        }

        private CollisionShape GenerateBox(Assets.BoundingBox aabb)
        {
            return CollisionShape.CreateBox(aabb.Extents);
        }

        private CollisionShape GenerateSphere(Vector3 extents)
        {
            float radius = Math.Max(Math.Max(extents.X, extents.Y), extents.Z);
            return CollisionShape.CreateSphere(radius);
        }

        private CollisionShape GenerateCapsule(Vector3 extents)
        {
            // 找到最接近的两个轴作为半径
            float[] vals = { extents.X, extents.Y, extents.Z };
            Array.Sort(vals);

            float radius = vals[1]; // 中间值作为半径
            float halfHeight = Math.Max(0, (vals[2] - radius)); // 最长轴减去半径

            return CollisionShape.CreateCapsule(radius, halfHeight);
        }

        private CollisionShape GenerateConvexHull(CollisionMeshData meshData, CollisionCookingOptions options)
        {
            var sampleCount = Math.Min(options.SampleVertexCount, meshData.Vertices.Length);
            var sampledVertices = SampleVertices(meshData.Vertices, sampleCount);

            _convexBuilder.Build(sampledVertices, out var hullVerts, out var hullIndices,
                options.MaxConvexHullVertices);

            return CollisionShape.CreateConvexHull(hullVerts, hullIndices);
        }

        private Vector3[] SampleVertices(Vector3[] vertices, int count)
        {
            if (vertices.Length <= count) return vertices;

            var rng = new Random(42);
            var shuffled = vertices.OrderBy(_ => rng.Next()).ToList();
            return shuffled.Take(count).ToArray();
        }

        private struct SubRegion
        {
            public CollisionMeshData MeshData;
            public Vector3 Offset;
        }

        private List<SubRegion> PartitionSpace(CollisionMeshData meshData, int maxRegions)
        {
            var regions = new List<SubRegion>();
            var aabb = meshData.ComputeAABB();
            var size = aabb.Max - aabb.Min;

            // 简单实现: 沿最长轴均匀分割
            int splitAxis = 0;
            if (size.Y > size.X) splitAxis = 1;
            if (size.Z > size[splitAxis]) splitAxis = 2;

            float regionSize = size[splitAxis] / maxRegions;
            var buckets = new Dictionary<int, List<Vector3>>();

            for (int i = 0; i < maxRegions; i++)
                buckets[i] = new List<Vector3>();

            foreach (var v in meshData.Vertices)
            {
                int bucket = Math.Clamp(
                    (int)((v[splitAxis] - aabb.Min[splitAxis]) / regionSize),
                    0, maxRegions - 1);
                buckets[bucket].Add(v);
            }

            for (int i = 0; i < maxRegions; i++)
            {
                if (buckets[i].Count < 3) continue;

                float offset = aabb.Min[splitAxis] + regionSize * i + regionSize * 0.5f;
                var offsetVec = Vector3.Zero;
                if (splitAxis == 0) offsetVec.X = offset;
                else if (splitAxis == 1) offsetVec.Y = offset;
                else offsetVec.Z = offset;

                regions.Add(new SubRegion
                {
                    MeshData = CollisionMeshData.Create(buckets[i].ToArray(), Array.Empty<int>()),
                    Offset = offsetVec
                });
            }

            return regions;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. HeightFieldBaker — 从地形网格生成高度场
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 高度场碰撞体生成器。从地形网格均匀采样高度值, 生成 HeightField 数据。
    /// </summary>
    public class HeightFieldBaker
    {
        /// <summary>
        /// 从网格数据生成高度场碰撞形状。
        /// </summary>
        /// <param name="meshData">地形网格数据 (应为规则网格)。</param>
        /// <param name="options">烘焙选项。</param>
        /// <returns>Heightfield 碰撞形状。</returns>
        public CollisionShape Bake(CollisionMeshData meshData, CollisionCookingOptions options)
        {
            if (meshData == null || meshData.Vertices.Length == 0)
                throw new ArgumentException("Mesh data is empty.");

            int resolution = Math.Max(2, options.HeightfieldResolution);
            float heightScale = options.HeightfieldScale;

            var aabb = meshData.ComputeAABB();
            var origin = aabb.Min;
            float width = aabb.Max.X - aabb.Min.X;
            float depth = aabb.Max.Z - aabb.Min.Z;
            float cellSize = Math.Max(width, depth) / (resolution - 1);

            // 均匀采样高度值
            var heights = new float[resolution * resolution];

            // 使用双线性插值从网格中采样
            if (meshData.Indices.Length >= 3)
            {
                SampleHeightsFromMesh(meshData, origin, width, depth, resolution, heights);
            }
            else
            {
                // 没有索引, 直接用顶点
                SampleHeightsFromVertices(meshData.Vertices, origin, width, depth, resolution, heights);
            }

            return CollisionShape.CreateHeightfield(
                heights, resolution, resolution, origin, cellSize, heightScale);
        }

        /// <summary>从索引网格中采样高度值。</summary>
        private void SampleHeightsFromMesh(CollisionMeshData meshData,
            Vector3 origin, float width, float depth, int resolution, float[] heights)
        {
            // 构建简单的空间查询: 对每个采样点找到最近三角形并插值高度
            var vertices = meshData.Vertices;
            var indices = meshData.Indices;

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float px = origin.X + (x / (float)(resolution - 1)) * width;
                    float pz = origin.Z + (z / (float)(resolution - 1)) * depth;

                    // 在所有三角形中找最近的点
                    float bestHeight = origin.Y;
                    float bestDistSq = float.MaxValue;

                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        var a = vertices[indices[i]];
                        var b = vertices[indices[i + 1]];
                        var c = vertices[indices[i + 2]];

                        // 点到三角形XZ平面的投影距离
                        var center = (a + b + c) / 3f;
                        float dx = px - center.X;
                        float dz = pz - center.Z;
                        float distSq = dx * dx + dz * dz;

                        if (distSq < bestDistSq)
                        {
                            // 在三角形上双线性插值
                            float h = InterpolateHeightOnTriangle(px, pz, a, b, c);
                            if (!float.IsNaN(h))
                            {
                                bestDistSq = distSq;
                                bestHeight = h;
                            }
                        }
                    }

                    heights[z * resolution + x] = (bestHeight - origin.Y);
                }
            }
        }

        /// <summary>在三角形上通过重心坐标插值高度。</summary>
        private float InterpolateHeightOnTriangle(float px, float pz, Vector3 a, Vector3 b, Vector3 c)
        {
            // 解方程: P = a + u*(b-a) + v*(c-a), 求u,v
            var ab = b - a;
            var ac = c - a;

            float det = ab.X * ac.Z - ab.Z * ac.X;
            if (Math.Abs(det) < 1e-10f) return float.NaN;

            float dx = px - a.X;
            float dz = pz - a.Z;
            float u = (dx * ac.Z - dz * ac.X) / det;
            float v = (ab.X * dz - ab.Z * dx) / det;

            if (u >= -0.01f && v >= -0.01f && u + v <= 1.01f)
            {
                return a.Y + u * ab.Y + v * ac.Y;
            }
            return float.NaN;
        }

        /// <summary>直接从顶点采样 (无索引网格)。</summary>
        private void SampleHeightsFromVertices(Vector3[] vertices,
            Vector3 origin, float width, float depth, int resolution, float[] heights)
        {
            // 简单最近邻采样
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float px = origin.X + (x / (float)(resolution - 1)) * width;
                    float pz = origin.Z + (z / (float)(resolution - 1)) * depth;

                    float bestHeight = origin.Y;
                    float bestDistSq = float.MaxValue;

                    foreach (var v in vertices)
                    {
                        float dx = v.X - px;
                        float dz = v.Z - pz;
                        float distSq = dx * dx + dz * dz;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestHeight = v.Y;
                        }
                    }

                    heights[z * resolution + x] = (bestHeight - origin.Y);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. CollisionCooker — 完整烘焙管线
    // ──────────────────────────────────────────────────────────────

    /// <summary>烘焙进度信息。</summary>
    public class CookingProgressEventArgs : EventArgs
    {
        /// <summary>当前阶段 (0-1)。</summary>
        public float Progress { get; set; }

        /// <summary>阶段描述。</summary>
        public string Stage { get; set; } = string.Empty;

        /// <summary>是否完成。</summary>
        public bool IsComplete { get; set; }
    }

    /// <summary>
    /// 完整碰撞烘焙管线。统一调用各个子系统, 支持同步/异步烘焙和进度回调。
    /// </summary>
    public class CollisionCooker
    {
        private readonly ConvexHullBuilder _convexBuilder = new();
        private readonly TriangleMeshCollisionBaker _meshBaker = new();
        private readonly SimplifiedCollisionGenerator _simplifiedGenerator = new();
        private readonly HeightFieldBaker _heightFieldBaker = new();

        /// <summary>烘焙进度事件。</summary>
        public event EventHandler<CookingProgressEventArgs>? OnProgress;

        /// <summary>
        /// 同步烘焙碰撞体。
        /// </summary>
        /// <param name="meshData">输入网格数据。</param>
        /// <param name="options">烘焙选项。</param>
        /// <returns>烘焙后的碰撞形状。</returns>
        public CollisionShape Cook(CollisionMeshData meshData, CollisionCookingOptions? options = null)
        {
            options ??= new CollisionCookingOptions();

            ReportProgress(0f, "开始碰撞烘焙...");

            var targetShape = options.TargetShape;

            // Auto 模式: 根据网格特征自动选择
            if (targetShape == CollisionShapeKind.Auto)
            {
                targetShape = AutoSelectCookingType(meshData);
            }

            ReportProgress(0.1f, $"选择碰撞类型: {targetShape}");

            CollisionShape result;
            switch (targetShape)
            {
                case CollisionShapeKind.Box:
                case CollisionShapeKind.Sphere:
                case CollisionShapeKind.Capsule:
                case CollisionShapeKind.ConvexHull:
                    ReportProgress(0.2f, "生成简化碰撞体...");
                    result = _simplifiedGenerator.Generate(meshData, options);
                    break;

                case CollisionShapeKind.TriangleMesh:
                    ReportProgress(0.2f, "构建三角形网格碰撞体...");
                    result = _meshBaker.Bake(meshData, options.MaxTrianglesPerSubMesh);
                    ReportProgress(0.8f, "BVH 构建完成");
                    break;

                case CollisionShapeKind.Heightfield:
                    ReportProgress(0.2f, "生成高度场...");
                    result = _heightFieldBaker.Bake(meshData, options);
                    ReportProgress(0.8f, "高度场采样完成");
                    break;

                case CollisionShapeKind.Compound:
                    ReportProgress(0.2f, "生成复合碰撞体...");
                    result = _simplifiedGenerator.GenerateCompound(meshData, options);
                    break;

                default:
                    throw new ArgumentException($"Unsupported collision shape type: {targetShape}");
            }

            ReportProgress(1f, "碰撞烘焙完成");
            return result;
        }

        /// <summary>
        /// 异步烘焙碰撞体。
        /// </summary>
        /// <param name="meshData">输入网格数据。</param>
        /// <param name="options">烘焙选项。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>烘焙后的碰撞形状。</returns>
        public Task<CollisionShape> CookAsync(
            CollisionMeshData meshData,
            CollisionCookingOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                return Cook(meshData, options);
            }, cancellationToken);
        }

        /// <summary>
        /// 从 DCCMeshData 烘焙碰撞体。
        /// </summary>
        public CollisionShape CookFromDCCMesh(DCC.DCCMeshData dccMesh, CollisionCookingOptions? options = null)
        {
            var meshData = CollisionMeshData.FromDCCMeshData(dccMesh);
            return Cook(meshData, options);
        }

        /// <summary>
        /// 批量烘焙多个网格。
        /// </summary>
        public CollisionShape[] CookBatch(
            CollisionMeshData[] meshes,
            CollisionCookingOptions? options = null,
            IProgress<float>? progress = null)
        {
            if (meshes == null || meshes.Length == 0)
                return Array.Empty<CollisionShape>();

            var results = new CollisionShape[meshes.Length];
            for (int i = 0; i < meshes.Length; i++)
            {
                results[i] = Cook(meshes[i], options);
                progress?.Report((float)(i + 1) / meshes.Length);
            }
            return results;
        }

        /// <summary>
        /// 自动选择烘焙类型。
        /// </summary>
        private CollisionShapeKind AutoSelectCookingType(CollisionMeshData meshData)
        {
            int triCount = meshData.TriangleCount;
            int vertCount = meshData.Vertices.Length;

            // 地形检测: 大量三角形, 高度变化相对均匀
            if (triCount > 1000)
            {
                var aabb = meshData.ComputeAABB();
                var extents = aabb.Extents;
                float aspectRatio = Math.Max(extents.X, extents.Z) / Math.Max(extents.Y, 0.001f);

                if (aspectRatio > 5f && triCount > 5000)
                    return CollisionShapeKind.Heightfield;
            }

            // 简单网格: 少量三角形 → 精确三角形网格
            if (triCount < 256)
                return CollisionShapeKind.TriangleMesh;

            // 中等复杂度: 简化碰撞体
            if (triCount < 2000)
                return CollisionShapeKind.ConvexHull;

            // 复杂网格: 复合碰撞体
            return CollisionShapeKind.Compound;
        }

        private void ReportProgress(float progress, string stage)
        {
            OnProgress?.Invoke(this, new CookingProgressEventArgs
            {
                Progress = progress,
                Stage = stage,
                IsComplete = progress >= 1f
            });
        }
    }
}
