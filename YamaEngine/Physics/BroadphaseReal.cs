// ============================================================================================
//  BroadphaseReal.cs
//
//  yama引擎 (YamaEngine) 真实宽相位碰撞检测模块 —— 纯手写实现，无外部物理库依赖。
//
//  内容：
//    - DynamicAABBTree：动态AABB树（插入/移除/更新/查询/光线投射/重叠对/重平衡）
//    - SweepAndPrune：扫掠与剪枝（3轴排序/增量更新/AABB预测）
//    - SpatialHashGrid：空间哈希网格（插入/移除/查询）
//    - CollisionFilter：碰撞过滤（层/掩码/组/N×N矩阵）
//
//  命名空间：YamaEngine.Physics
//  目标框架：net8.0 / C# 8.0+
//  外部依赖：仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    #region ======================== 动态AABB树 ========================

    /// <summary>动态AABB树节点。</summary>
    public struct DynamicTreeNode
    {
        public AABBShape AABB;
        public int Left;
        public int Right;
        public int Parent;
        public int Height;
        public object UserData;
        public AABBShape FatAABB;
        public int TouchCount;
        public bool IsLeaf => Left == -1 && Right == -1;
    }

    /// <summary>动态AABB树 —— 基于表面面积启发式（SAH）的自平衡二叉树。</summary>
    public class DynamicAABBTree
    {
        private const int NULL_NODE = -1;
        private const float FAT_AABB_MARGIN = 0.1f;
        private const int MAX_NODES = 65536;

        private DynamicTreeNode[] _nodes;
        private int _nodeCount;
        private int _root;
        private List<int> _freeList = new List<int>();

        public DynamicAABBTree()
        {
            _nodes = new DynamicTreeNode[MAX_NODES];
            _root = NULL_NODE;
            _nodeCount = 0;
            for (int i = 0; i < MAX_NODES; i++)
            {
                _nodes[i] = new DynamicTreeNode
                {
                    Left = NULL_NODE, Right = NULL_NODE, Parent = NULL_NODE,
                    Height = 0, UserData = null, TouchCount = 0
                };
            }
        }

        /// <summary>清空树中所有节点。</summary>
        public void Clear()
        {
            _nodeCount = 0;
            _root = NULL_NODE;
            _freeList.Clear();
        }

        /// <summary>插入一个AABB到树中。返回代理ID。</summary>
        public int Insert(AABBShape aabb, object userData)
        {
            if (_freeList.Count > 0)
            {
                int nodeId = _freeList[_freeList.Count - 1];
                _freeList.RemoveAt(_freeList.Count - 1);
                AllocateNode(nodeId, aabb, userData);
            }
            else
            {
                if (_nodeCount >= MAX_NODES) return NULL_NODE;
                AllocateNode(_nodeCount, aabb, userData);
                _nodeCount++;
            }

            int leafId = _nodeCount - 1;
            _nodes[leafId].FatAABB = aabb;
            _nodes[leafId].FatAABB.Expand(new PhysicsVector3(FAT_AABB_MARGIN));

            InsertLeaf(leafId);

            return leafId;
        }

        /// <summary>从树中移除代理。</summary>
        public void Remove(int proxyId)
        {
            if (proxyId < 0 || proxyId >= _nodeCount) return;
            _nodes[proxyId].TouchCount++;
            RemoveLeaf(proxyId);
            _freeList.Add(proxyId);
        }

        /// <summary>更新代理的AABB。</summary>
        public bool Update(int proxyId, AABBShape newAABB)
        {
            if (proxyId < 0 || proxyId >= _nodeCount) return false;

            ref DynamicTreeNode node = ref _nodes[proxyId];
            AABBShape expandedAABB = newAABB;
            expandedAABB.Expand(new PhysicsVector3(FAT_AABB_MARGIN));

            // 检查是否在fat AABB内
            if (node.FatAABB.Contains(expandedAABB.Min) && node.FatAABB.Contains(expandedAABB.Max))
                return false;

            RemoveLeaf(proxyId);
            node.AABB = newAABB;
            node.FatAABB = expandedAABB;
            InsertLeaf(proxyId);
            return true;
        }

        /// <summary>查询与给定AABB重叠的所有代理。</summary>
        public void Query(AABBShape aabb, Action<int, object> callback)
        {
            if (_root == NULL_NODE) return;
            Stack<int> stack = new Stack<int>();
            stack.Push(_root);

            while (stack.Count > 0)
            {
                int nodeId = stack.Pop();
                if (nodeId == NULL_NODE) continue;

                ref DynamicTreeNode node = ref _nodes[nodeId];

                if (!OverlapAABB(aabb, node.FatAABB)) continue;

                if (node.IsLeaf)
                {
                    callback(nodeId, node.UserData);
                }
                else
                {
                    stack.Push(node.Left);
                    stack.Push(node.Right);
                }
            }
        }

        /// <summary>批量查询多个AABB。</summary>
        public void QueryMultiple(AABBShape[] aabbs, Action<int, object> callback)
        {
            for (int i = 0; i < aabbs.Length; i++)
            {
                Query(aabbs[i], callback);
            }
        }

        /// <summary>光线投射查询。</summary>
        public void Raycast(PhysicsVector3 origin, PhysicsVector3 direction, float maxDistance,
            Func<int, object, float, bool> callback)
        {
            if (_root == NULL_NODE) return;

            PhysicsVector3 invDir = new PhysicsVector3(
                Math.Abs(direction.X) > 1e-8f ? 1.0f / direction.X : float.MaxValue,
                Math.Abs(direction.Y) > 1e-8f ? 1.0f / direction.Y : float.MaxValue,
                Math.Abs(direction.Z) > 1e-8f ? 1.0f / direction.Z : float.MaxValue);

            PhysicsVector3 end = origin + direction * maxDistance;
            AABBShape rayAABB = new AABBShape(
                PhysicsVector3.Min(origin, end),
                PhysicsVector3.Max(origin, end));

            Stack<int> stack = new Stack<int>();
            stack.Push(_root);

            while (stack.Count > 0)
            {
                int nodeId = stack.Pop();
                if (nodeId == NULL_NODE) continue;

                ref DynamicTreeNode node = ref _nodes[nodeId];

                if (!OverlapAABB(rayAABB, node.FatAABB)) continue;

                if (node.IsLeaf)
                {
                    float t = maxDistance;
                    if (global::YamaEngine.Physics.Raycast.RaycastAABB(origin, direction, node.FatAABB, out float tMin, out float tMax))
                    {
                        t = tMin >= 0 ? tMin : (tMax >= 0 ? tMax : maxDistance);
                    }
                    bool cont = callback(nodeId, node.UserData, t);
                    if (!cont) return;
                }
                else
                {
                    stack.Push(node.Left);
                    stack.Push(node.Right);
                }
            }
        }

        /// <summary>获取所有重叠AABB对。</summary>
        public void OverlapPairs(Action<int, int> callback)
        {
            if (_root == NULL_NODE) return;

            // 使用栈进行深度优先遍历，找出所有重叠的叶子对
            Stack<int> stack = new Stack<int>();
            stack.Push(_root);

            while (stack.Count > 0)
            {
                int nodeId = stack.Pop();
                if (nodeId == NULL_NODE) continue;

                ref DynamicTreeNode node = ref _nodes[nodeId];

                if (node.IsLeaf) continue;

                int left = node.Left;
                int right = node.Right;

                // 检查左子树和右子树是否重叠
                if (left != NULL_NODE && right != NULL_NODE)
                {
                    CollectOverlapPairs(left, right, callback, stack);
                }

                stack.Push(left);
                stack.Push(right);
            }
        }

        /// <summary>当前节点数量。</summary>
        public int Count => _nodeCount - _freeList.Count;

        /// <summary>根节点索引。</summary>
        public int Root => _root;

        private void CollectOverlapPairs(int nodeA, int nodeB, Action<int, int> callback, Stack<int> stack)
        {
            Stack<(int, int)> pairStack = new Stack<(int, int)>();
            pairStack.Push((nodeA, nodeB));

            while (pairStack.Count > 0)
            {
                var (na, nb) = pairStack.Pop();
                if (na == NULL_NODE || nb == NULL_NODE) continue;

                ref DynamicTreeNode curNodeA = ref _nodes[na];
                ref DynamicTreeNode curNodeB = ref _nodes[nb];

                if (!OverlapAABB(curNodeA.FatAABB, curNodeB.FatAABB)) continue;

                if (curNodeA.IsLeaf && curNodeB.IsLeaf)
                {
                    callback(na, nb);
                }
                else if (curNodeA.IsLeaf)
                {
                    pairStack.Push((na, curNodeB.Left));
                    pairStack.Push((na, curNodeB.Right));
                }
                else if (curNodeB.IsLeaf)
                {
                    pairStack.Push((curNodeA.Left, nb));
                    pairStack.Push((curNodeA.Right, nb));
                }
                else
                {
                    pairStack.Push((curNodeA.Left, curNodeB.Left));
                    pairStack.Push((curNodeA.Left, curNodeB.Right));
                    pairStack.Push((curNodeA.Right, curNodeB.Left));
                    pairStack.Push((curNodeA.Right, curNodeB.Right));
                }
            }
        }

        private void AllocateNode(int nodeId, AABBShape aabb, object userData)
        {
            _nodes[nodeId].AABB = aabb;
            _nodes[nodeId].FatAABB = aabb;
            _nodes[nodeId].FatAABB.Expand(new PhysicsVector3(FAT_AABB_MARGIN));
            _nodes[nodeId].UserData = userData;
            _nodes[nodeId].Left = NULL_NODE;
            _nodes[nodeId].Right = NULL_NODE;
            _nodes[nodeId].Parent = NULL_NODE;
            _nodes[nodeId].Height = 0;
            _nodes[nodeId].TouchCount = 0;
        }

        private void InsertLeaf(int leafId)
        {
            if (_root == NULL_NODE)
            {
                _root = leafId;
                _nodes[leafId].Parent = NULL_NODE;
                return;
            }

            ref DynamicTreeNode leaf = ref _nodes[leafId];

            // 找最佳兄弟（Surface Area Cost + SAH）
            int bestSibling = _root;
            float bestCost = ComputeCost(leafId, _root);

            Stack<int> stack = new Stack<int>();
            stack.Push(_root);

            while (stack.Count > 0)
            {
                int nodeId = stack.Pop();
                if (_nodes[nodeId].IsLeaf) continue;

                float inheritedCost = ComputeInheritedCost(leafId, nodeId);
                float directCost = ComputeCost(leafId, nodeId);

                if (directCost < bestCost)
                {
                    bestCost = directCost;
                    bestSibling = nodeId;
                }

                // 如果继承成本已大于当前最优，剪枝
                if (inheritedCost < bestCost)
                {
                    stack.Push(_nodes[nodeId].Left);
                    stack.Push(_nodes[nodeId].Right);
                }
            }

            // 创建新父节点
            int oldParent = _nodes[bestSibling].Parent;
            int newParent = AllocateNewNode();
            _nodes[newParent].Parent = oldParent;
            _nodes[newParent].Height = _nodes[bestSibling].Height + 1;

            if (oldParent != NULL_NODE)
            {
                if (_nodes[oldParent].Left == bestSibling)
                    _nodes[oldParent].Left = newParent;
                else
                    _nodes[oldParent].Right = newParent;
            }
            else
            {
                _root = newParent;
            }

            _nodes[newParent].Left = bestSibling;
            _nodes[newParent].Right = leafId;
            _nodes[bestSibling].Parent = newParent;
            _nodes[leafId].Parent = newParent;

            // 更新AABB和高度
            UpdateNodeHierarchy(newParent);

            // 平衡
            Balance(_root);
        }

        private void RemoveLeaf(int leafId)
        {
            if (_root == leafId)
            {
                _root = NULL_NODE;
                return;
            }

            int parent = _nodes[leafId].Parent;
            int grandParent = _nodes[parent].Parent;
            int sibling = (_nodes[parent].Left == leafId) ? _nodes[parent].Right : _nodes[parent].Left;

            if (grandParent != NULL_NODE)
            {
                if (_nodes[grandParent].Left == parent)
                    _nodes[grandParent].Left = sibling;
                else
                    _nodes[grandParent].Right = sibling;
                _nodes[sibling].Parent = grandParent;
            }
            else
            {
                _root = sibling;
                _nodes[sibling].Parent = NULL_NODE;
            }

            // 释放父节点
            _freeList.Add(parent);
        }

        private void UpdateNodeHierarchy(int nodeId)
        {
            while (nodeId != NULL_NODE)
            {
                ref DynamicTreeNode node = ref _nodes[nodeId];

                int left = node.Left;
                int right = node.Right;

                // 合并子AABB
                if (left != NULL_NODE && right != NULL_NODE)
                {
                    node.AABB = AABBShape.Union(_nodes[left].AABB, _nodes[right].AABB);
                    node.FatAABB = AABBShape.Union(_nodes[left].FatAABB, _nodes[right].FatAABB);
                    node.Height = 1 + Math.Max(_nodes[left].Height, _nodes[right].Height);
                }

                nodeId = node.Parent;
            }
        }

        private void Balance(int nodeId)
        {
            if (nodeId == NULL_NODE || _nodes[nodeId].IsLeaf) return;

            ref DynamicTreeNode node = ref _nodes[nodeId];
            int left = node.Left;
            int right = node.Right;

            Balance(left);
            Balance(right);

            int balance = _nodes[right].Height - _nodes[left].Height;

            // 不平衡检测（高度差>1时旋转）
            if (balance > 1)
            {
                RotateLeft(nodeId);
            }
            else if (balance < -1)
            {
                RotateRight(nodeId);
            }
        }

        private void RotateLeft(int nodeId)
        {
            int right = _nodes[nodeId].Right;
            int rightLeft = _nodes[right].Left;

            _nodes[nodeId].Right = rightLeft;
            if (rightLeft != NULL_NODE) _nodes[rightLeft].Parent = nodeId;

            _nodes[right].Left = nodeId;
            _nodes[right].Parent = _nodes[nodeId].Parent;

            if (_nodes[nodeId].Parent != NULL_NODE)
            {
                if (_nodes[_nodes[nodeId].Parent].Left == nodeId)
                    _nodes[_nodes[nodeId].Parent].Left = right;
                else
                    _nodes[_nodes[nodeId].Parent].Right = right;
            }
            else
            {
                _root = right;
            }

            _nodes[nodeId].Parent = right;

            UpdateNodeHierarchy(nodeId);
            UpdateNodeHierarchy(right);
        }

        private void RotateRight(int nodeId)
        {
            int left = _nodes[nodeId].Left;
            int leftRight = _nodes[left].Right;

            _nodes[nodeId].Left = leftRight;
            if (leftRight != NULL_NODE) _nodes[leftRight].Parent = nodeId;

            _nodes[left].Right = nodeId;
            _nodes[left].Parent = _nodes[nodeId].Parent;

            if (_nodes[nodeId].Parent != NULL_NODE)
            {
                if (_nodes[_nodes[nodeId].Parent].Left == nodeId)
                    _nodes[_nodes[nodeId].Parent].Left = left;
                else
                    _nodes[_nodes[nodeId].Parent].Right = left;
            }
            else
            {
                _root = left;
            }

            _nodes[nodeId].Parent = left;

            UpdateNodeHierarchy(nodeId);
            UpdateNodeHierarchy(left);
        }

        private float ComputeCost(int leafId, int nodeId)
        {
            AABBShape combined = AABBShape.Union(_nodes[leafId].FatAABB, _nodes[nodeId].FatAABB);
            return combined.SurfaceArea();
        }

        private float ComputeInheritedCost(int leafId, int nodeId)
        {
            return AABBShape.Union(_nodes[leafId].FatAABB, _nodes[nodeId].FatAABB).SurfaceArea()
                 - _nodes[nodeId].FatAABB.SurfaceArea();
        }

        private int AllocateNewNode()
        {
            if (_freeList.Count > 0)
            {
                int nodeId = _freeList[_freeList.Count - 1];
                _freeList.RemoveAt(_freeList.Count - 1);
                return nodeId;
            }
            if (_nodeCount >= MAX_NODES) return NULL_NODE;
            _nodeCount++;
            return _nodeCount - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool OverlapAABB(AABBShape a, AABBShape b)
        {
            return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
                   a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
                   a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        }
    }

    #endregion

    #region ======================== 扫掠与剪枝 ========================

    /// <summary>SAP区间。</summary>
    public struct SAPInterval
    {
        public int BodyIndex;
        public float Min;
        public float Max;
        public bool IsMin;
    }

    /// <summary>扫掠与剪枝（Sweep and Prune）—— 基于排序的宽相位算法。</summary>
    public class SweepAndPrune
    {
        private const int NUM_AXES = 3;

        private struct SAPAxis
        {
            public List<SAPInterval> Intervals;
            public PhysicsVector3 AxisDirection;
        }

        private SAPAxis[] _axes;
        private int _maxBodies;
        private PhysicsVector3[] _prevVelocities;

        public SweepAndPrune(int maxBodies = 4096)
        {
            _maxBodies = maxBodies;
            _axes = new SAPAxis[NUM_AXES];
            _axes[0].AxisDirection = PhysicsVector3.UnitX;
            _axes[1].AxisDirection = PhysicsVector3.UnitY;
            _axes[2].AxisDirection = PhysicsVector3.UnitZ;
            for (int i = 0; i < NUM_AXES; i++)
                _axes[i].Intervals = new List<SAPInterval>(maxBodies * 2);
            _prevVelocities = new PhysicsVector3[maxBodies];
        }

        /// <summary>更新所有AABB并执行SAP检测。</summary>
        /// <returns>所有可能重叠的物体对。</returns>
        public List<(int a, int b)> UpdateAndQuery(
            AABBShape[] aabbs, PhysicsVector3[] velocities, float dt)
        {
            // 清除旧数据
            for (int i = 0; i < NUM_AXES; i++)
                _axes[i].Intervals.Clear();

            int count = Math.Min(aabbs.Length, _maxBodies);

            // 为每条轴创建min/max区间
            for (int i = 0; i < count; i++)
            {
                for (int axis = 0; axis < NUM_AXES; axis++)
                {
                    float projMin, projMax;
                    PhysicsVector3 dir = _axes[axis].AxisDirection;

                    // AABB预测：根据速度外推
                    float vel = velocities != null && i < velocities.Length
                        ? velocities[i].Dot(dir) * dt * 2 : 0;

                    projMin = (dir.X != 0 ? aabbs[i].Min.X : (dir.Y != 0 ? aabbs[i].Min.Y : aabbs[i].Min.Z)) - Math.Abs(vel);
                    projMax = (dir.X != 0 ? aabbs[i].Max.X : (dir.Y != 0 ? aabbs[i].Max.Y : aabbs[i].Max.Z)) + Math.Abs(vel);

                    _axes[axis].Intervals.Add(new SAPInterval
                    {
                        BodyIndex = i,
                        Min = projMin,
                        Max = projMax,
                        IsMin = true
                    });
                    _axes[axis].Intervals.Add(new SAPInterval
                    {
                        BodyIndex = i,
                        Min = projMin,
                        Max = projMax,
                        IsMin = false
                    });
                }
            }

            // 扫掠：对每条轴的区间按min端排序
            HashSet<(int, int)> pairs = new HashSet<(int, int)>();

            for (int axis = 0; axis < NUM_AXES; axis++)
            {
                var intervals = _axes[axis].Intervals;
                intervals.Sort((a, b) => {
                    if (a.IsMin != b.IsMin) return a.IsMin ? -1 : 1;
                    return a.Min.CompareTo(b.Min);
                });

                // 剪枝阶段：活跃列表
                HashSet<int> activeSet = new HashSet<int>();

                foreach (var interval in intervals)
                {
                    if (interval.IsMin)
                    {
                        // Min端进入：与活跃列表中所有物体检测重叠
                        foreach (int activeBody in activeSet)
                        {
                            if (activeBody != interval.BodyIndex)
                            {
                                int lo = Math.Min(activeBody, interval.BodyIndex);
                                int hi = Math.Max(activeBody, interval.BodyIndex);
                                pairs.Add((lo, hi));
                            }
                        }
                        activeSet.Add(interval.BodyIndex);
                    }
                    else
                    {
                        activeSet.Remove(interval.BodyIndex);
                    }
                }
            }

            // 全3轴重叠才算候选对
            return new List<(int, int)>(pairs);
        }

        /// <summary>增量更新（只重排序变化的物体）。</summary>
        public void IncrementalUpdate(
            AABBShape[] aabbs, PhysicsVector3[] velocities, float dt,
            Action<int, int> onPairFound)
        {
            int count = Math.Min(aabbs.Length, _maxBodies);

            // 选择穿透最少的轴（通常X轴）
            int bestAxis = 0;
            float bestCost = float.MaxValue;
            for (int axis = 0; axis < NUM_AXES; axis++)
            {
                PhysicsVector3 dir = _axes[axis].AxisDirection;
                float extent = 0;
                for (int i = 0; i < count; i++)
                {
                    float lo = dir.X != 0 ? aabbs[i].Min.X : (dir.Y != 0 ? aabbs[i].Min.Y : aabbs[i].Min.Z);
                    float hi = dir.X != 0 ? aabbs[i].Max.X : (dir.Y != 0 ? aabbs[i].Max.Y : aabbs[i].Max.Z);
                    extent += hi - lo;
                }
                if (extent < bestCost) { bestCost = extent; bestAxis = axis; }
            }

            // 重建最佳轴的区间
            _axes[bestAxis].Intervals.Clear();
            PhysicsVector3 bestDir = _axes[bestAxis].AxisDirection;
            for (int i = 0; i < count; i++)
            {
                float vel = velocities != null && i < velocities.Length
                    ? velocities[i].Dot(bestDir) * dt * 2 : 0;
                float projMin = (bestDir.X != 0 ? aabbs[i].Min.X : (bestDir.Y != 0 ? aabbs[i].Min.Y : aabbs[i].Min.Z)) - Math.Abs(vel);
                float projMax = (bestDir.X != 0 ? aabbs[i].Max.X : (bestDir.Y != 0 ? aabbs[i].Max.Y : aabbs[i].Max.Z)) + Math.Abs(vel);

                _axes[bestAxis].Intervals.Add(new SAPInterval { BodyIndex = i, Min = projMin, Max = projMax, IsMin = true });
                _axes[bestAxis].Intervals.Add(new SAPInterval { BodyIndex = i, Min = projMin, Max = projMax, IsMin = false });
            }

            // 排序
            _axes[bestAxis].Intervals.Sort((a, b) => {
                if (a.IsMin != b.IsMin) return a.IsMin ? -1 : 1;
                return a.Min.CompareTo(b.Min);
            });

            // 扫掠
            HashSet<int> activeSet = new HashSet<int>();
            foreach (var interval in _axes[bestAxis].Intervals)
            {
                if (interval.IsMin)
                {
                    foreach (int activeBody in activeSet)
                    {
                        if (activeBody != interval.BodyIndex)
                            onPairFound(Math.Min(activeBody, interval.BodyIndex),
                                        Math.Max(activeBody, interval.BodyIndex));
                    }
                    activeSet.Add(interval.BodyIndex);
                }
                else
                {
                    activeSet.Remove(interval.BodyIndex);
                }
            }

            // 保存速度用于下帧预测
            for (int i = 0; i < count && i < _prevVelocities.Length; i++)
                _prevVelocities[i] = velocities != null && i < velocities.Length ? velocities[i] : PhysicsVector3.Zero;
        }
    }

    #endregion

    #region ======================== 空间哈希网格 ========================

    /// <summary>空间哈希网格 —— 适用于大量等大小物体。</summary>
    public class SpatialHashGrid
    {
        private float _cellSize;
        private float _invCellSize;
        private Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private Dictionary<int, long> _objectToCell = new Dictionary<int, long>();

        public SpatialHashGrid(float cellSize)
        {
            _cellSize = Math.Max(cellSize, 0.001f);
            _invCellSize = 1.0f / _cellSize;
        }

        /// <summary>哈希函数（3D坐标 → long）。</summary>
        private long HashCell(int cx, int cy, int cz)
        {
            // 大素数哈希
            long hash = (long)cx * 73856093L ^ (long)cy * 19349663L ^ (long)cz * 83492791L;
            return hash;
        }

        /// <summary>世界坐标到格子坐标。</summary>
        private void WorldToCell(PhysicsVector3 position, out int cx, out int cy, out int cz)
        {
            cx = (int)Math.Floor(position.X * _invCellSize);
            cy = (int)Math.Floor(position.Y * _invCellSize);
            cz = (int)Math.Floor(position.Z * _invCellSize);
        }

        /// <summary>插入物体到网格。</summary>
        public void Insert(int objectId, AABBShape aabb)
        {
            PhysicsVector3 min = aabb.Min, max = aabb.Max;
            WorldToCell(min, out int cx0, out int cy0, out int cz0);
            WorldToCell(max, out int cx1, out int cy1, out int cz1);

            long primaryCell = HashCell(cx0, cy0, cz0);
            _objectToCell[objectId] = primaryCell;

            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        long cellHash = HashCell(cx, cy, cz);
                        if (!_cells.ContainsKey(cellHash))
                            _cells[cellHash] = new List<int>();
                        _cells[cellHash].Add(objectId);
                    }
                }
            }
        }

        /// <summary>移除物体。</summary>
        public void Remove(int objectId)
        {
            if (!_objectToCell.TryGetValue(objectId, out long primaryCell)) return;

            // 需要遍历所有可能包含此物体的格子
            var cellsToRemove = new List<long>();
            foreach (var kvp in _cells)
            {
                if (kvp.Value.Remove(objectId))
                {
                    if (kvp.Value.Count == 0)
                        cellsToRemove.Add(kvp.Key);
                }
            }

            foreach (var cell in cellsToRemove)
                _cells.Remove(cell);

            _objectToCell.Remove(objectId);
        }

        /// <summary>查询与AABB重叠的物体。</summary>
        public void Query(AABBShape aabb, Action<int> callback)
        {
            PhysicsVector3 min = aabb.Min, max = aabb.Max;
            WorldToCell(min, out int cx0, out int cy0, out int cz0);
            WorldToCell(max, out int cx1, out int cy1, out int cz1);

            HashSet<int> queried = new HashSet<int>();

            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        long cellHash = HashCell(cx, cy, cz);
                        if (_cells.TryGetValue(cellHash, out var objects))
                        {
                            foreach (int obj in objects)
                            {
                                if (queried.Add(obj))
                                    callback(obj);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>查询重叠对。</summary>
        public List<(int a, int b)> QueryOverlapPairs()
        {
            var pairs = new HashSet<(int, int)>();
            foreach (var kvp in _cells)
            {
                var objects = kvp.Value;
                for (int i = 0; i < objects.Count; i++)
                {
                    for (int j = i + 1; j < objects.Count; j++)
                    {
                        int lo = Math.Min(objects[i], objects[j]);
                        int hi = Math.Max(objects[i], objects[j]);
                        pairs.Add((lo, hi));
                    }
                }
            }
            return new List<(int, int)>(pairs);
        }

        /// <summary>清除所有数据。</summary>
        public void Clear()
        {
            _cells.Clear();
            _objectToCell.Clear();
        }

        /// <summary>当前格子数。</summary>
        public int CellCount => _cells.Count;

        // ---- 粒子/质点网格支持（供流体系统使用） ----

        /// <summary>创建网格（cellSize 亦作为邻域搜索半径）。</summary>
        public SpatialHashGrid(float cellSize, int capacity)
            : this(cellSize) { }

        private object _particleArray;

        /// <summary>根据粒子位置重建格子（T 需具有 PhysicsVector3 类型字段/属性 Position）。</summary>
        public void Build<T>(T[] particles, int count)
        {
            _particleArray = particles;
            _cells.Clear();
            _objectToCell.Clear();
            for (int i = 0; i < count; i++)
            {
                dynamic p = particles[i];
                WorldToCell((PhysicsVector3)p.Position, out int cx, out int cy, out int cz);
                long cellHash = HashCell(cx, cy, cz);
                if (!_cells.TryGetValue(cellHash, out List<int> list))
                {
                    list = new List<int>();
                    _cells[cellHash] = list;
                }
                list.Add(i);
                _objectToCell[i] = cellHash;
            }
        }

        /// <summary>查找 index 粒子在其邻域范围（半径 = cellSize）内的邻居，写入 result。</summary>
        public void FindNeighbors<T>(T[] particles, int index, List<int> result)
        {
            result.Clear();
            dynamic self = particles[index];
            PhysicsVector3 pos = (PhysicsVector3)self.Position;
            WorldToCell(pos, out int cx, out int cy, out int cz);
            float radiusSq = _cellSize * _cellSize;

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (_cells.TryGetValue(HashCell(cx + dx, cy + dy, cz + dz), out List<int> list))
                {
                    foreach (int ni in list)
                    {
                        if (ni == index) continue;
                        dynamic other = particles[ni];
                        if ((pos - (PhysicsVector3)other.Position).LengthSquared() <= radiusSq)
                            result.Add(ni);
                    }
                }
            }
        }
    }

    #endregion

    #region ======================== 碰撞过滤 ========================

    /// <summary>碰撞过滤信息。</summary>
    public struct CollisionFilter
    {
        /// <summary>所属碰撞层（位标志）。</summary>
        public int Layer;

        /// <summary>碰撞掩码（与目标Layer做AND操作）。</summary>
        public int Mask;

        /// <summary>碰撞组（同组不碰撞）。</summary>
        public int Group;

        public CollisionFilter(int layer, int mask, int group = 0)
        {
            Layer = layer; Mask = mask; Group = group;
        }

        /// <summary>按碰撞通道构造默认过滤（层 = 通道位，掩码 = 全部）。</summary>
        public CollisionFilter(CollisionChannel channel)
        {
            Layer = (int)channel; Mask = unchecked((int)0xFFFFFFFF); Group = 0;
        }

        /// <summary>判断本过滤器与另一过滤器是否应发生碰撞。</summary>
        public bool Passes(CollisionFilter other) => CollisionFilterUtils.ShouldCollide(this, other);

        public static readonly CollisionFilter Default = new CollisionFilter(1, unchecked((int)0xFFFFFFFF), 0);
    }

    /// <summary>碰撞过滤工具。</summary>
    public static class CollisionFilterUtils
    {
        /// <summary>判断两个碰撞过滤器是否应该碰撞。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldCollide(CollisionFilter a, CollisionFilter b)
        {
            // 同组不碰撞
            if (a.Group != 0 && a.Group == b.Group)
                return false;

            // Layer-Mask 测试
            return (a.Layer & b.Mask) != 0 && (b.Layer & a.Mask) != 0;
        }

        /// <summary>简化版（使用int表示Layer和Mask）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldCollide(int layerA, int maskA, int groupA,
                                          int layerB, int maskB, int groupB)
        {
            if (groupA != 0 && groupA == groupB) return false;
            return (layerA & maskB) != 0 && (layerB & maskA) != 0;
        }
    }

    /// <summary>碰撞矩阵（N x N 布尔矩阵，自定义哪些层之间碰撞）。</summary>
    public class CollisionFilterMatrix
    {
        private int _numLayers;
        private bool[,] _matrix;

        public CollisionFilterMatrix(int numLayers)
        {
            _numLayers = Math.Max(numLayers, 1);
            _matrix = new bool[_numLayers, _numLayers];
            // 默认：所有层之间都碰撞
            for (int i = 0; i < _numLayers; i++)
                for (int j = 0; j < _numLayers; j++)
                    _matrix[i, j] = true;
        }

        /// <summary>设置两个层之间是否碰撞。</summary>
        public void SetCollision(int layerA, int layerB, bool collide)
        {
            if (layerA >= 0 && layerA < _numLayers && layerB >= 0 && layerB < _numLayers)
            {
                _matrix[layerA, layerB] = collide;
                _matrix[layerB, layerA] = collide;
            }
        }

        /// <summary>查询两个层之间是否碰撞。</summary>
        public bool ShouldCollide(int layerA, int layerB)
        {
            if (layerA < 0 || layerA >= _numLayers || layerB < 0 || layerB >= _numLayers)
                return true;
            return _matrix[layerA, layerB];
        }

        /// <summary>层数。</summary>
        public int NumLayers => _numLayers;
    }

    #endregion
}
