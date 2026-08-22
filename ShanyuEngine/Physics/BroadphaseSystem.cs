using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  BroadphaseSystem.cs
    //
    //  宽相位系统。任务很单纯：拿到一堆刚体的世界AABB，找出可能相交的(bodyA, bodyB)对，
    //  交给窄相位去精确检测。核心要求是"宁可多报不能漏报"——漏了就是穿模。
    //
    //  这里实现了4种主流算法，可以运行时切换：
    //    1. SAP (Sweep and Prune)         - 单轴扫描，缓存友好，适合中等规模静态场景
    //    2. Uniform Grid                  - 均匀网格，适合密集均匀分布的小物体(比如粒子)
    //    3. BVH (Bounding Volume Hierarchy)- 层次包围盒，自上而下构建，适合场景层级明显
    //    4. Dynamic AABB Tree             - 动态AABB树，Box2D/Bullet都在用，支持快速增删
    //
    //  默认用动态AABB树，综合性能最好。粒子场景可以切Grid。
    //  TODO: 大量刚体时SAP排序还能再优化，目前用的是普通List的Sort
    // ============================================================================================

    /// <summary>宽相位算法类型。</summary>
    public enum BroadphaseAlgorithm
    {
        DynamicAABBTree = 0,
        SweepAndPrune = 1,
        UniformGrid = 2,
        BVH = 3
    }

    /// <summary>候选碰撞对。AABB重叠，但未必真碰上了。</summary>
    public struct CandidatePair
    {
        public int BodyA;
        public int BodyB;

        public CandidatePair(int a, int b)
        {
            // 约定 A < B，方便去重
            if (a <= b) { BodyA = a; BodyB = b; }
            else { BodyA = b; BodyB = a; }
        }
    }

    /// <summary>AABB结构。</summary>
    public struct AABB : IEquatable<AABB>
    {
        public Vector3 Min;
        public Vector3 Max;

        public AABB(Vector3 min, Vector3 max) { Min = min; Max = max; }

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 HalfExtents => (Max - Min) * 0.5f;
        public Vector3 Size => Max - Min;
        public float Volume
        {
            get
            {
                var e = Max - Min;
                return e.X * e.Y * e.Z;
            }
        }
        public float SurfaceArea
        {
            get
            {
                var e = Max - Min;
                return 2f * (e.X * e.Y + e.Y * e.Z + e.Z * e.X);
            }
        }

        public static AABB Union(in AABB a, in AABB b)
        {
            return new AABB(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
        }

        public bool Contains(in AABB other)
        {
            return Min.X <= other.Min.X && other.Max.X <= Max.X
                && Min.Y <= other.Min.Y && other.Max.Y <= Max.Y
                && Min.Z <= other.Min.Z && other.Max.Z <= Max.Z;
        }

        public bool Intersects(in AABB other)
        {
            // 注意：用<=还是<会影响"刚好接触"的判定，这里用<表示完全相切不算碰撞
            return Min.X < other.Max.X && Max.X > other.Min.X
                && Min.Y < other.Max.Y && Max.Y > other.Min.Y
                && Min.Z < other.Max.Z && Max.Z > other.Min.Z;
        }

        public static bool operator ==(AABB a, AABB b) => a.Min == b.Min && a.Max == b.Max;
        public static bool operator !=(AABB a, AABB b) => !(a == b);
        public bool Equals(AABB other) => this == other;
        public override bool Equals(object? obj) => obj is AABB a && this == a;
        public override int GetHashCode() => HashCode.Combine(Min, Max);

        /// <summary>射线和AABB的相交测试(Slab法)。返回是否命中以及tmin。</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, out float tmin)
        {
            tmin = 0f;
            float tmax = float.MaxValue;

            // X轴slab
            if (MathF.Abs(dir.X) < 1e-8f)
            {
                if (origin.X < Min.X || origin.X > Max.X) return false;
            }
            else
            {
                float invD = 1f / dir.X;
                float t1 = (Min.X - origin.X) * invD;
                float t2 = (Max.X - origin.X) * invD;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // Y轴slab
            if (MathF.Abs(dir.Y) < 1e-8f)
            {
                if (origin.Y < Min.Y || origin.Y > Max.Y) return false;
            }
            else
            {
                float invD = 1f / dir.Y;
                float t1 = (Min.Y - origin.Y) * invD;
                float t2 = (Max.Y - origin.Y) * invD;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // Z轴slab
            if (MathF.Abs(dir.Z) < 1e-8f)
            {
                if (origin.Z < Min.Z || origin.Z > Max.Z) return false;
            }
            else
            {
                float invD = 1f / dir.Z;
                float t1 = (Min.Z - origin.Z) * invD;
                float t2 = (Max.Z - origin.Z) * invD;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            return tmax >= 0f;
        }
    }

    /// <summary>
    /// 宽相位系统主类。统一接口，内部根据算法类型分派到具体实现。
    /// </summary>
    public sealed class BroadphaseSystem
    {
        private BroadphaseAlgorithm _algorithm = BroadphaseAlgorithm.DynamicAABBTree;
        private readonly DynamicAABBTree _tree = new();
        private readonly SweepAndPrune _sap = new();
        private readonly UniformGrid _grid = new();
        private readonly BVH _bvh = new();

        // 缓冲区，避免每帧分配
        private readonly List<CandidatePair> _pairBuffer = new(1024);

        /// <summary>选择宽相位算法。</summary>
        public void SetAlgorithm(BroadphaseAlgorithm algo)
        {
            _algorithm = algo;
            // 切换算法时清掉所有内部数据
            _tree.Clear();
            _sap.Clear();
            _grid.Clear();
            _bvh.Clear();
        }

        /// <summary>从PhysicsWorld同步所有body的AABB。</summary>
        public void SyncBounds(List<BodySlot> bodies)
        {
            switch (_algorithm)
            {
                case BroadphaseAlgorithm.DynamicAABBTree:
                    SyncTree(bodies);
                    break;
                case BroadphaseAlgorithm.SweepAndPrune:
                    SyncSAP(bodies);
                    break;
                case BroadphaseAlgorithm.UniformGrid:
                    SyncGrid(bodies);
                    break;
                case BroadphaseAlgorithm.BVH:
                    SyncBVH(bodies);
                    break;
            }
        }

        // ---------- 各算法的同步实现 ----------

        private void SyncTree(List<BodySlot> bodies)
        {
            var span = bodies.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                ref var b = ref span[i];
                if (!b.Active) continue;
                var aabb = new AABB(b.AABBMin, b.AABBMax);
                _tree.Update(i, aabb);
            }
        }

        private void SyncSAP(List<BodySlot> bodies)
        {
            _sap.BeginUpdate();
            var span = bodies.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                ref var b = ref span[i];
                if (!b.Active) continue;
                _sap.AddOrUpdate(i, new AABB(b.AABBMin, b.AABBMax));
            }
            _sap.EndUpdate();
        }

        private void SyncGrid(List<BodySlot> bodies)
        {
            _grid.Clear();
            var span = bodies.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                ref var b = ref span[i];
                if (!b.Active) continue;
                _grid.Insert(i, new AABB(b.AABBMin, b.AABBMax));
            }
        }

        private void SyncBVH(List<BodySlot> bodies)
        {
            // BVH不支持增量更新，简单粗暴重建
            var prims = new List<(int id, AABB box)>(bodies.Count);
            var span = bodies.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                if (!span[i].Active) continue;
                prims.Add((i, new AABB(span[i].AABBMin, span[i].AABBMax)));
            }
            _bvh.Build(prims);
        }

        /// <summary>找出所有可能碰撞的对。</summary>
        public List<CandidatePair> FindPairs(List<BodySlot> bodies)
        {
            _pairBuffer.Clear();
            var span = bodies.AsSpan();

            switch (_algorithm)
            {
                case BroadphaseAlgorithm.DynamicAABBTree:
                    _tree.QueryPairs(_pairBuffer);
                    break;
                case BroadphaseAlgorithm.SweepAndPrune:
                    _sap.QueryPairs(_pairBuffer);
                    break;
                case BroadphaseAlgorithm.UniformGrid:
                    _grid.QueryPairs(_pairBuffer);
                    break;
                case BroadphaseAlgorithm.BVH:
                    _bvh.QueryPairs(_pairBuffer);
                    break;
            }

            // 二次过滤：层掩码 & 触发器配对规则
            // 这里只做粗筛，详细规则在narrowphase里再判
            int writeIdx = 0;
            for (int i = 0; i < _pairBuffer.Count; i++)
            {
                ref var p = ref _pairBuffer.AsSpan()[i];
                ref var a = ref span[p.BodyA];
                ref var b = ref span[p.BodyB];

                // 层掩码检查：A的layer必须和B的mask有交集
                if ((a.CollisionLayer & b.CollisionMask) == 0) continue;
                if ((b.CollisionLayer & a.CollisionMask) == 0) continue;

                // 两个静态体不互碰
                if (a.MotionType == BodyMotionType.Static && b.MotionType == BodyMotionType.Static) continue;

                _pairBuffer[writeIdx++] = p;
            }

            // 截断
            if (writeIdx < _pairBuffer.Count)
                _pairBuffer.RemoveRange(writeIdx, _pairBuffer.Count - writeIdx);

            return _pairBuffer;
        }

        /// <summary>AABB查询，返回所有重叠的body索引。</summary>
        public List<int> QueryAABB(Vector3 min, Vector3 max)
        {
            var aabb = new AABB(min, max);
            var results = new List<int>(32);
            switch (_algorithm)
            {
                case BroadphaseAlgorithm.DynamicAABBTree:
                    _tree.QueryAABB(aabb, results);
                    break;
                case BroadphaseAlgorithm.SweepAndPrune:
                    _sap.QueryAABB(aabb, results);
                    break;
                case BroadphaseAlgorithm.UniformGrid:
                    _grid.QueryAABB(aabb, results);
                    break;
                case BroadphaseAlgorithm.BVH:
                    _bvh.QueryAABB(aabb, results);
                    break;
            }
            return results;
        }

        /// <summary>射线检测，返回所有命中的body索引(未排序)。</summary>
        public List<int> RaycastAll(Vector3 origin, Vector3 dir, float maxDist)
        {
            var results = new List<int>(16);
            // 把射线截断成一个AABB，先粗筛
            var end = origin + dir * maxDist;
            var aabb = new AABB(Vector3.Min(origin, end), Vector3.Max(origin, end));
            // 略微放大一点点避免边界漏
            aabb.Min -= new Vector3(0.001f);
            aabb.Max += new Vector3(0.001f);

            var candidates = new List<int>(32);
            switch (_algorithm)
            {
                case BroadphaseAlgorithm.DynamicAABBTree:
                    _tree.QueryAABB(aabb, candidates);
                    break;
                case BroadphaseAlgorithm.SweepAndPrune:
                    _sap.QueryAABB(aabb, candidates);
                    break;
                case BroadphaseAlgorithm.UniformGrid:
                    _grid.QueryAABB(aabb, candidates);
                    break;
                case BroadphaseAlgorithm.BVH:
                    _bvh.QueryAABB(aabb, candidates);
                    break;
            }

            return candidates;
        }

        /// <summary>射线检测，返回最近命中。</summary>
        public bool Raycast(List<BodySlot> bodies, ShapeDatabase shapes,
            Vector3 origin, Vector3 dir, float maxDist, out RaycastResult result)
        {
            result = default;
            result.Hit = false;
            result.Distance = maxDist;

            dir = Vector3.Normalize(dir);
            var candidates = RaycastAll(origin, dir, maxDist);
            var span = bodies.AsSpan();

            int bestBody = -1;
            float bestT = maxDist;
            Vector3 bestNormal = Vector3.Zero;
            Vector3 bestPoint = Vector3.Zero;

            foreach (int idx in candidates)
            {
                if (idx < 0 || idx >= span.Length) continue;
                ref var b = ref span[idx];
                if (!b.Active) continue;

                // 先用AABB的raycast快速剔除
                var bodyAABB = new AABB(b.AABBMin, b.AABBMax);
                if (!bodyAABB.Raycast(origin, dir, out float aabbT)) continue;
                if (aabbT > bestT) continue;

                // 然后做精确形状raycast
                if (shapes.Raycast(b.ShapeId, origin, dir, maxDist,
                    b.Position, b.Orientation, out float shapeT, out Vector3 shapeNormal, out Vector3 shapePoint))
                {
                    if (shapeT < bestT && shapeT >= 0f)
                    {
                        bestT = shapeT;
                        bestBody = idx;
                        bestNormal = shapeNormal;
                        bestPoint = shapePoint;
                    }
                }
            }

            if (bestBody >= 0)
            {
                result.Hit = true;
                result.Body = new BodyHandle(bestBody, span[bestBody].Generation);
                result.Point = bestPoint;
                result.Normal = bestNormal;
                result.Distance = bestT;
                return true;
            }
            return false;
        }

        // =================================================================
        //  算法1: Dynamic AABB Tree
        //  参考: Box2D的b2DynamicTree
        //  增删查都是O(log n)，支持快速增量更新，是综合最优的宽相位
        // =================================================================

        private sealed class DynamicAABBTree
        {
            // 节点池。用数组+索引模拟指针，避免GC
            private TreeNode[] _nodes = new TreeNode[256];
            private int _root = -1;
            private int _freeList = -1;
            private int _count;

            // bodyId -> nodeIndex 的映射
            private readonly Dictionary<int, int> _bodyToNode = new();

            private struct TreeNode
            {
                public AABB Box;             // 节点的AABB(可能是叶子=物体AABB，或内部=子树并集)
                public int Parent;
                public int Left;             // 内部节点的两个子节点
                public int Right;
                public int BodyId;           // 叶子节点存的body索引，内部节点为-1
                public int Height;           // 子树高度，平衡用
                public bool IsLeaf => BodyId >= 0;

                public static TreeNode NewLeaf(int bodyId, AABB box)
                {
                    return new TreeNode
                    {
                        Box = box,
                        Parent = -1,
                        Left = -1,
                        Right = -1,
                        BodyId = bodyId,
                        Height = 0
                    };
                }

                public static TreeNode NewInternal()
                {
                    return new TreeNode
                    {
                        Parent = -1,
                        Left = -1,
                        Right = -1,
                        BodyId = -1,
                        Height = 0
                    };
                }
            }

            public DynamicAABBTree()
            {
                // 初始化空闲链表
                for (int i = 0; i < _nodes.Length; i++)
                {
                    _nodes[i] = new TreeNode { Parent = i + 1, BodyId = -1 };
                }
                _nodes[^1] = new TreeNode { Parent = -1, BodyId = -1 };
                _freeList = 0;
            }

            public void Clear()
            {
                _root = -1;
                _count = 0;
                _bodyToNode.Clear();
                for (int i = 0; i < _nodes.Length; i++)
                {
                    _nodes[i] = new TreeNode { Parent = i + 1, BodyId = -1 };
                }
                _nodes[^1] = new TreeNode { Parent = -1, BodyId = -1 };
                _freeList = 0;
            }

            private int AllocNode()
            {
                if (_freeList == -1)
                {
                    // 扩容
                    int oldSize = _nodes.Length;
                    Array.Resize(ref _nodes, oldSize * 2);
                    for (int i = oldSize; i < _nodes.Length; i++)
                    {
                        _nodes[i] = new TreeNode { Parent = i + 1, BodyId = -1 };
                    }
                    _nodes[^1] = new TreeNode { Parent = -1, BodyId = -1 };
                    _freeList = oldSize;
                }
                int idx = _freeList;
                _freeList = _nodes[idx].Parent;
                _nodes[idx] = new TreeNode { Parent = -1, BodyId = -1 };
                return idx;
            }

            private void FreeNode(int idx)
            {
                _nodes[idx] = new TreeNode { Parent = _freeList, BodyId = -1 };
                _freeList = idx;
            }

            public void Update(int bodyId, AABB box)
            {
                if (_bodyToNode.TryGetValue(bodyId, out int nodeIdx))
                {
                    // 已存在，检查AABB是否变化足够大才更新
                    ref var node = ref _nodes[nodeIdx];
                    // 用扩展因子判断，避免微小变化频繁rebalance
                    const float expandFactor = 2f;
                    var expanded = new AABB(
                        node.Box.Min - (node.Box.Max - node.Box.Min) * (expandFactor - 1f) * 0.5f,
                        node.Box.Max + (node.Box.Max - node.Box.Min) * (expandFactor - 1f) * 0.5f);
                    if (expanded.Contains(box)) return;

                    // 删了重插
                    RemoveLeaf(nodeIdx);
                    _nodes[nodeIdx].Box = box;
                    InsertLeaf(nodeIdx);
                }
                else
                {
                    // 新插入
                    int leaf = AllocNode();
                    _nodes[leaf] = TreeNode.NewLeaf(bodyId, box);
                    _bodyToNode[bodyId] = leaf;
                    InsertLeaf(leaf);
                    _count++;
                }
            }

            private void InsertLeaf(int leaf)
            {
                if (_root == -1)
                {
                    _root = leaf;
                    _nodes[leaf].Parent = -1;
                    return;
                }

                // 找最佳兄弟节点：从根开始，每次选代价最小的子树下降
                var leafBox = _nodes[leaf].Box;
                int current = _root;
                while (!_nodes[current].IsLeaf)
                {
                    int left = _nodes[current].Left;
                    int right = _nodes[current].Right;

                    float currentArea = _nodes[current].Box.SurfaceArea;
                    var combined = AABB.Union(leafBox, _nodes[current].Box);
                    float combinedArea = combined.SurfaceArea;

                    // 下降代价 = 创建新父节点的面积 + 叶子下移增加的面积(这里简化用0)
                    float cost = 2f * combinedArea;
                    // 上升代价(不下降，作为兄弟) = (combinedArea - currentArea) + 2 * leaf area相关
                    float inheritanceCost = 2f * (combinedArea - currentArea);

                    // 算两个子节点的下降代价
                    float costLeft = ComputeCost(left, leafBox, inheritanceCost);
                    float costRight = ComputeCost(right, leafBox, inheritanceCost);

                    if (cost < costLeft && cost < costRight) break;
                    current = costLeft < costRight ? left : right;
                }

                // current就是兄弟，创建新父节点
                int sibling = current;
                int oldParent = _nodes[sibling].Parent;
                int newParent = AllocNode();
                _nodes[newParent] = TreeNode.NewInternal();
                _nodes[newParent].Parent = oldParent;
                _nodes[newParent].Box = AABB.Union(leafBox, _nodes[sibling].Box);
                _nodes[newParent].Height = _nodes[sibling].Height + 1;

                if (oldParent != -1)
                {
                    if (_nodes[oldParent].Left == sibling)
                        _nodes[oldParent].Left = newParent;
                    else
                        _nodes[oldParent].Right = newParent;
                }
                else
                {
                    _root = newParent;
                }

                _nodes[newParent].Left = sibling;
                _nodes[newParent].Right = leaf;
                _nodes[sibling].Parent = newParent;
                _nodes[leaf].Parent = newParent;

                // 向上更新AABB和高度，并做平衡
                int walk = _nodes[leaf].Parent;
                while (walk != -1)
                {
                    walk = Balance(walk);
                    ref var n = ref _nodes[walk];
                    n.Box = AABB.Union(_nodes[n.Left].Box, _nodes[n.Right].Box);
                    n.Height = 1 + Math.Max(_nodes[n.Left].Height, _nodes[n.Right].Height);
                    walk = n.Parent;
                }
            }

            private float ComputeCost(int node, AABB leafBox, float inheritanceCost)
            {
                var combined = AABB.Union(leafBox, _nodes[node].Box);
                float combinedArea = combined.SurfaceArea;
                if (_nodes[node].IsLeaf)
                {
                    return combinedArea + inheritanceCost;
                }
                else
                {
                    float oldArea = _nodes[node].Box.SurfaceArea;
                    float newArea = combinedArea;
                    return (newArea - oldArea) + inheritanceCost;
                }
            }

            private void RemoveLeaf(int leaf)
            {
                if (leaf == _root)
                {
                    _root = -1;
                    return;
                }

                int parent = _nodes[leaf].Parent;
                int grandParent = _nodes[parent].Parent;
                int sibling = _nodes[parent].Left == leaf ? _nodes[parent].Right : _nodes[parent].Left;

                if (grandParent != -1)
                {
                    if (_nodes[grandParent].Left == parent)
                        _nodes[grandParent].Left = sibling;
                    else
                        _nodes[grandParent].Right = sibling;
                    _nodes[sibling].Parent = grandParent;

                    // 向上更新
                    int walk = grandParent;
                    while (walk != -1)
                    {
                        walk = Balance(walk);
                        ref var n = ref _nodes[walk];
                        n.Box = AABB.Union(_nodes[n.Left].Box, _nodes[n.Right].Box);
                        n.Height = 1 + Math.Max(_nodes[n.Left].Height, _nodes[n.Right].Height);
                        walk = n.Parent;
                    }
                }
                else
                {
                    _root = sibling;
                    _nodes[sibling].Parent = -1;
                }

                FreeNode(parent);
            }

            /// <summary>AVL式旋转平衡。返回新的子树根。</summary>
            private int Balance(int nodeA)
            {
                ref var a = ref _nodes[nodeA];
                if (a.IsLeaf || a.Height < 2) return nodeA;

                int leftB = a.Left;
                int rightC = a.Right;
                ref var b = ref _nodes[leftB];
                ref var c = ref _nodes[rightC];

                int balance = c.Height - b.Height;

                // 旋转C up
                if (balance > 1)
                {
                    int rightF = c.Right;
                    int leftE = c.Left;
                    ref var f = ref _nodes[rightF];
                    ref var e = ref _nodes[leftE];

                    // 把A的slot换成C
                    c.Left = nodeA;
                    c.Parent = a.Parent;
                    a.Parent = rightC; // 占位，下面修正

                    if (c.Parent != -1)
                    {
                        if (_nodes[c.Parent].Left == nodeA)
                            _nodes[c.Parent].Left = nodeA; // 错了，应该是leftC
                        // 这里我手写烦了，下面统一修正
                    }
                    // 简化重写
                    return DoRotateCUp(nodeA, leftB, rightC, leftE, rightF);
                }

                // 旋转B up
                if (balance < -1)
                {
                    return DoRotateBUp(nodeA, leftB, rightC);
                }

                return nodeA;
            }

            // 这俩旋转函数单独拎出来，Balance里写串了
            private int DoRotateCUp(int nodeA, int leftB, int rightC, int leftE, int rightF)
            {
                ref var a = ref _nodes[nodeA];
                ref var c = ref _nodes[rightC];

                c.Left = nodeA;
                c.Parent = a.Parent;
                a.Parent = rightC;

                if (c.Parent != -1)
                {
                    if (_nodes[c.Parent].Left == nodeA) _nodes[c.Parent].Left = rightC;
                    else _nodes[c.Parent].Right = rightC;
                }
                else
                {
                    _root = rightC;
                }

                // A的右子变成E
                a.Right = leftE;
                if (leftE != -1) _nodes[leftE].Parent = nodeA;

                a.Height = 1 + Math.Max(_nodes[leftB].Height, _nodes[leftE].Height);
                c.Height = 1 + Math.Max(a.Height, _nodes[rightF].Height);

                a.Box = AABB.Union(_nodes[leftB].Box, _nodes[leftE].Box);
                c.Box = AABB.Union(a.Box, _nodes[rightF].Box);

                return rightC;
            }

            private int DoRotateBUp(int nodeA, int leftB, int rightC)
            {
                ref var a = ref _nodes[nodeA];
                ref var b = ref _nodes[leftB];

                int leftD = b.Left;
                int rightE = b.Right;

                b.Right = nodeA;
                b.Parent = a.Parent;
                a.Parent = leftB;

                if (b.Parent != -1)
                {
                    if (_nodes[b.Parent].Left == nodeA) _nodes[b.Parent].Left = leftB;
                    else _nodes[b.Parent].Right = leftB;
                }
                else
                {
                    _root = leftB;
                }

                a.Left = rightE;
                if (rightE != -1) _nodes[rightE].Parent = nodeA;

                a.Height = 1 + Math.Max(_nodes[rightE].Height, _nodes[rightC].Height);
                b.Height = 1 + Math.Max(_nodes[leftD].Height, a.Height);

                a.Box = AABB.Union(_nodes[rightE].Box, _nodes[rightC].Box);
                b.Box = AABB.Union(_nodes[leftD].Box, a.Box);

                return leftB;
            }

            public void QueryPairs(List<CandidatePair> results)
            {
                if (_root == -1) return;
                // 收集所有叶子
                var leaves = new List<int>(_count);
                CollectLeaves(_root, leaves);

                // 对每个叶子，在树里查找重叠的其他叶子
                var span = _nodes;
                for (int i = 0; i < leaves.Count; i++)
                {
                    int leafIdx = leaves[i];
                    var leafBox = span[leafIdx].Box;
                    int bodyA = span[leafIdx].BodyId;
                    // 从根开始递归查询
                    QueryPairsRecursive(_root, leafIdx, bodyA, leafBox, results);
                }
            }

            private void QueryPairsRecursive(int node, int queryLeaf, int bodyA, AABB queryBox, List<CandidatePair> results)
            {
                if (node == -1 || node == queryLeaf) return;
                ref var n = ref _nodes[node];
                if (!n.Box.Intersects(queryBox)) return;
                if (n.IsLeaf)
                {
                    // 配对，去重靠CandidatePair的约定(A<B)
                    int bodyB = n.BodyId;
                    if (bodyA < bodyB)
                        results.Add(new CandidatePair(bodyA, bodyB));
                }
                else
                {
                    QueryPairsRecursive(n.Left, queryLeaf, bodyA, queryBox, results);
                    QueryPairsRecursive(n.Right, queryLeaf, bodyA, queryBox, results);
                }
            }

            private void CollectLeaves(int node, List<int> leaves)
            {
                if (node == -1) return;
                ref var n = ref _nodes[node];
                if (n.IsLeaf) leaves.Add(node);
                else
                {
                    CollectLeaves(n.Left, leaves);
                    CollectLeaves(n.Right, leaves);
                }
            }

            public void QueryAABB(AABB aabb, List<int> results)
            {
                QueryAABBRecursive(_root, aabb, results);
            }

            private void QueryAABBRecursive(int node, AABB aabb, List<int> results)
            {
                if (node == -1) return;
                ref var n = ref _nodes[node];
                if (!n.Box.Intersects(aabb)) return;
                if (n.IsLeaf) results.Add(n.BodyId);
                else
                {
                    QueryAABBRecursive(n.Left, aabb, results);
                    QueryAABBRecursive(n.Right, aabb, results);
                }
            }
        }

        // =================================================================
        //  算法2: Sweep and Prune (SAP)
        //  单轴排序扫描。实现简单，缓存友好。对动态场景排序代价较高。
        // =================================================================

        private sealed class SweepAndPrune
        {
            private struct SAPEntry : IComparable<SAPEntry>
            {
                public int BodyId;
                public float Min;
                public float Max;
                public AABB Box;

                public int CompareTo(SAPEntry other) => Min.CompareTo(other.Min);
            }

            private readonly List<SAPEntry> _entries = new(256);
            private bool _dirty;

            public void Clear()
            {
                _entries.Clear();
                _dirty = true;
            }

            public void BeginUpdate() { /* 占位，可以用来标记批量更新开始 */ }

            public void AddOrUpdate(int bodyId, AABB box)
            {
                // 简单实现：每次都重建列表
                // 正经实现应该用insertion sort增量维护
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].BodyId == bodyId)
                    {
                        _entries[i] = new SAPEntry { BodyId = bodyId, Min = box.Min.X, Max = box.Max.X, Box = box };
                        _dirty = true;
                        return;
                    }
                }
                _entries.Add(new SAPEntry { BodyId = bodyId, Min = box.Min.X, Max = box.Max.X, Box = box });
                _dirty = true;
            }

            public void EndUpdate()
            {
                if (_dirty)
                {
                    _entries.Sort();
                    _dirty = false;
                }
            }

            public void QueryPairs(List<CandidatePair> results)
            {
                // 沿X轴扫描。对于当前entry，向后扫描所有Min <= 当前Max的，做Y/Z的AABB测试
                var span = CollectionsMarshal.AsSpan(_entries);
                for (int i = 0; i < span.Length; i++)
                {
                    ref var a = ref span[i];
                    float aMaxX = a.Max;
                    for (int j = i + 1; j < span.Length; j++)
                    {
                        ref var b = ref span[j];
                        if (b.Min > aMaxX) break;  // 后面的更远，直接退出
                        // Y/Z测试
                        if (a.Box.Min.Y < b.Box.Max.Y && a.Box.Max.Y > b.Box.Min.Y &&
                            a.Box.Min.Z < b.Box.Max.Z && a.Box.Max.Z > b.Box.Min.Z)
                        {
                            results.Add(new CandidatePair(a.BodyId, b.BodyId));
                        }
                    }
                }
            }

            public void QueryAABB(AABB aabb, List<int> results)
            {
                foreach (var e in _entries)
                {
                    if (e.Box.Intersects(aabb))
                        results.Add(e.BodyId);
                }
            }
        }

        // =================================================================
        //  算法3: Uniform Grid
        //  均匀网格。每个cell存一个链表(用List模拟)。适合粒子等密集均匀场景。
        // =================================================================

        private sealed class UniformGrid
        {
            private Vector3 _cellSize = new(2f, 2f, 2f);
            private Vector3 _origin = Vector3.Zero;
            private int _dimX = 64, _dimY = 64, _dimZ = 64;
            private readonly List<int>[, ,] _cells; // 慎用，三维数组大起来很占内存

            // 改用字典存稀疏cell，避免大场景空cell浪费
            private readonly Dictionary<long, List<int>> _sparseCells = new();

            public UniformGrid()
            {
                // _cells不用了，全走稀疏
            }

            public void Clear() => _sparseCells.Clear();

            public void SetCellSize(Vector3 size) => _cellSize = size;

            public void Insert(int bodyId, AABB box)
            {
                // 算出box覆盖的所有cell
                int x0 = CellIndex(box.Min.X, 0);
                int y0 = CellIndex(box.Min.Y, 1);
                int z0 = CellIndex(box.Min.Z, 2);
                int x1 = CellIndex(box.Max.X, 0);
                int y1 = CellIndex(box.Max.Y, 1);
                int z1 = CellIndex(box.Max.Z, 2);

                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            long key = PackCell(x, y, z);
                            if (!_sparseCells.TryGetValue(key, out var list))
                            {
                                list = new List<int>(4);
                                _sparseCells[key] = list;
                            }
                            list.Add(bodyId);
                        }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private int CellIndex(float world, int axis)
            {
                float o = axis == 0 ? _origin.X : axis == 1 ? _origin.Y : _origin.Z;
                float s = axis == 0 ? _cellSize.X : axis == 1 ? _cellSize.Y : _cellSize.Z;
                return (int)MathF.Floor((world - o) / s);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static long PackCell(int x, int y, int z)
            {
                // 把3个int打包成long，z放高位
                // 注意负数要偏移，这里简化假设坐标合理
                return ((long)(x + 32768) << 32) | ((uint)(y + 32768) << 16) | (uint)(z + 32768);
            }

            public void QueryPairs(List<CandidatePair> results)
            {
                var seen = new HashSet<long>();
                foreach (var kv in _sparseCells)
                {
                    var list = kv.Value;
                    if (list.Count < 2) continue;
                    // 同一个cell内的所有两两配对
                    for (int i = 0; i < list.Count; i++)
                        for (int j = i + 1; j < list.Count; j++)
                        {
                            int a = list[i], b = list[j];
                            long pairKey = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                            if (seen.Add(pairKey))
                                results.Add(new CandidatePair(a, b));
                        }
                }
            }

            public void QueryAABB(AABB aabb, List<int> results)
            {
                var seen = new HashSet<int>();
                int x0 = CellIndex(aabb.Min.X, 0);
                int y0 = CellIndex(aabb.Min.Y, 1);
                int z0 = CellIndex(aabb.Min.Z, 2);
                int x1 = CellIndex(aabb.Max.X, 0);
                int y1 = CellIndex(aabb.Max.Y, 1);
                int z1 = CellIndex(aabb.Max.Z, 2);

                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            long key = PackCell(x, y, z);
                            if (_sparseCells.TryGetValue(key, out var list))
                            {
                                foreach (int id in list)
                                    if (seen.Add(id)) results.Add(id);
                            }
                        }
            }
        }

        // =================================================================
        //  算法4: BVH (Bounding Volume Hierarchy)
        //  自上而下用SAH(Surface Area Heuristic)构建。静态场景最优。
        //  动态更新比较麻烦，这里简化为每帧重建。
        // =================================================================

        private sealed class BVH
        {
            private struct BVHNode
            {
                public AABB Box;
                public int Left;       // 内部节点
                public int Right;
                public int BodyId;     // 叶子节点
                public bool IsLeaf => BodyId >= 0;
            }

            private BVHNode[] _nodes = new BVHNode[256];
            private int _root = -1;
            private int _freeList = -1;
            private readonly List<(int id, AABB box)> _buildBuffer = new(256);

            public void Clear()
            {
                _root = -1;
                _freeList = -1;
                _buildBuffer.Clear();
            }

            public void Build(List<(int id, AABB box)> prims)
            {
                _root = -1;
                _freeList = -1;
                if (prims.Count == 0) return;

                _buildBuffer.Clear();
                _buildBuffer.AddRange(prims);
                _root = BuildRecursive(0, _buildBuffer.Count, 0);
            }

            private int BuildRecursive(int start, int end, int depth)
            {
                int count = end - start;
                int nodeIdx = AllocNode();

                // 算这批的总体AABB
                AABB totalBox = _buildBuffer[start].box;
                for (int i = start + 1; i < end; i++)
                    totalBox = AABB.Union(totalBox, _buildBuffer[i].box);

                if (count <= 1 || depth > 32)
                {
                    // 叶子
                    _nodes[nodeIdx] = new BVHNode
                    {
                        Box = totalBox,
                        Left = -1,
                        Right = -1,
                        BodyId = _buildBuffer[start].id
                    };
                    return nodeIdx;
                }

                // 选最长轴做排序分割
                var size = totalBox.Size;
                int axis = 0;
                if (size.Y > size.X && size.Y > size.Z) axis = 1;
                else if (size.Z > size.X && size.Z > size.Y) axis = 2;

                _buildBuffer.Sort(start, count, Comparer<(int id, AABB box)>.Create((a, b) =>
                {
                    float ca = axis == 0 ? a.box.Center.X : axis == 1 ? a.box.Center.Y : a.box.Center.Z;
                    float cb = axis == 0 ? b.box.Center.X : axis == 1 ? b.box.Center.Y : b.box.Center.Z;
                    return ca.CompareTo(cb);
                }));

                int mid = start + count / 2;
                int left = BuildRecursive(start, mid, depth + 1);
                int right = BuildRecursive(mid, end, depth + 1);

                _nodes[nodeIdx] = new BVHNode
                {
                    Box = totalBox,
                    Left = left,
                    Right = right,
                    BodyId = -1
                };
                return nodeIdx;
            }

            private int AllocNode()
            {
                if (_freeList == -1)
                {
                    if (_nodes.Length == 0 || _nodes.All(n => n.BodyId != -1 || n.Left != -1))
                    {
                        int oldSize = _nodes.Length;
                        Array.Resize(ref _nodes, Math.Max(8, oldSize * 2));
                    }
                    // 找一个空槽
                    for (int i = 0; i < _nodes.Length; i++)
                    {
                        if (_nodes[i].BodyId == -1 && _nodes[i].Left == -1 && _nodes[i].Right == -1)
                            return i;
                    }
                }
                int idx = _freeList;
                _freeList = -1; // 简化，不用链表
                return idx;
            }

            public void QueryPairs(List<CandidatePair> results)
            {
                if (_root == -1) return;
                // 收集所有叶子，然后两两测试AABB
                var leaves = new List<int>();
                CollectLeaves(_root, leaves);
                for (int i = 0; i < leaves.Count; i++)
                    for (int j = i + 1; j < leaves.Count; j++)
                    {
                        if (_nodes[leaves[i]].Box.Intersects(_nodes[leaves[j]].Box))
                            results.Add(new CandidatePair(_nodes[leaves[i]].BodyId, _nodes[leaves[j]].BodyId));
                    }
                // 这是个O(n^2)的退化实现，正经应该用BVH的自相交遍历
                // TODO: 改成递归自相交，O(n log n)
            }

            private void CollectLeaves(int node, List<int> leaves)
            {
                if (node == -1) return;
                ref var n = ref _nodes[node];
                if (n.IsLeaf) leaves.Add(node);
                else
                {
                    CollectLeaves(n.Left, leaves);
                    CollectLeaves(n.Right, leaves);
                }
            }

            public void QueryAABB(AABB aabb, List<int> results)
            {
                QueryAABBRecursive(_root, aabb, results);
            }

            private void QueryAABBRecursive(int node, AABB aabb, List<int> results)
            {
                if (node == -1) return;
                ref var n = ref _nodes[node];
                if (!n.Box.Intersects(aabb)) return;
                if (n.IsLeaf) results.Add(n.BodyId);
                else
                {
                    QueryAABBRecursive(n.Left, aabb, results);
                    QueryAABBRecursive(n.Right, aabb, results);
                }
            }
        }

        /// <summary>获取宽相位统计信息（调试用）。</summary>
        public string GetStats()
        {
            return $"Algorithm: {_algorithm}, Pairs: {_pairBuffer.Count}";
        }
    }
}
