// ============================================================================
//  ShanyuEngine.AI.PathfindingSystem.cs
//  山雨引擎 -- 寻路系统
//  对齐 Recast Detour / GPPC / 各种 pathfinding 论文
// ----------------------------------------------------------------------------
//  本文件实现 4 种主流寻路算法：
//    1. AStar     - 经典 A*，堆优化 + 启发式
//    2. DStarLite - 动态 A*，边权变化时增量重规划
//    3. JPSPlus   - Jump Point Search Plus，网格地图加速 10x+
//    4. ThetaStar - 任意角度 A*，路径更自然
//
//  A*算法这段得仔细看 不然容易写错，特别是启发函数和 open/closed 集合。
//  所有算法共用一个 IPathGraph 接口，方便切地图表示。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、图接口和数据结构
    // ========================================================================

    /// <summary>
    /// 寻路图抽象。不管是 navmesh / grid / waypoint 都实现这个接口。
    /// 这样上层算法不用关心底层表示。
    /// </summary>
    public interface IPathGraph
    {
        /// <summary>起点 id 到终点 id 的直接边权，无连接返回 float.MaxValue。</summary>
        float GetCost(int from, int to);
        /// <summary>启发函数估值(到 goal 的直线距离之类)。</summary>
        float GetHeuristic(int from, int to);
        /// <summary>枚举某节点的所有邻居。</summary>
        IEnumerable<(int neighbor, float cost)> GetNeighbors(int node);
        /// <summary>节点世界坐标，lineOfSight 用。</summary>
        Vector3 GetNodePosition(int node);
        /// <summary>两点之间是否视线无阻，Theta*/JPS+ 用。</summary>
        bool LineOfSight(int from, int to);
        /// <summary>节点总数(给 D* 预分配用)。</summary>
        int NodeCount { get; }
    }

    /// <summary>寻路请求结果。</summary>
    public enum PathState
    {
        Success,
        PartialPath,
        Failed,
        Invalid
    }

    /// <summary>寻路结果。</summary>
    public sealed class PathResult
    {
        public List<int> Nodes = new();        // 经过的节点 id
        public List<Vector3> Waypoints = new();// 世界坐标路径点
        public float TotalCost;
        public PathState State;
        public int NodesExpanded;
        public int NodesVisited;
        public float TimeMs;

        public bool IsValid => State == PathState.Success || State == PathState.PartialPath;
        public int Length => Waypoints.Count;
    }

    /// <summary>寻路算法枚举。</summary>
    public enum PathfindingAlgorithm
    {
        AStar,
        DStarLite,
        JPSPlus,
        ThetaStar
    }

    /// <summary>寻路选项。</summary>
    public struct PathfindingOptions
    {
        public PathfindingAlgorithm Algorithm;
        public float HeuristicWeight;   // 启发函数权重，>1 贪心更快但次优
        public int MaxNodesExpanded;    // 上限，防卡死
        public bool ReturnPartialPath;  // 找不到终点时返回最接近的路径
        public float PartialPathThreshold; // 离终点多近算"接近"

        public static PathfindingOptions Default => new()
        {
            Algorithm = PathfindingAlgorithm.AStar,
            HeuristicWeight = 1f,
            MaxNodesExpanded = 10000,
            ReturnPartialPath = true,
            PartialPathThreshold = 5f
        };
    }

    // ========================================================================
    //  二、二叉堆(优先队列)
    // ========================================================================

    /// <summary>
    /// 泛型二叉堆。A* 里 open list 用它，Push/Pop 都是 O(log n)。
    /// 用数组实现，索引 0 是根。
    /// </summary>
    public sealed class MinHeap
    {
        private (int node, float priority)[] _data;
        private int _count;
        private readonly int[] _indexMap; // node -> heap index，用于 DecreaseKey

        public int Count => _count;

        public MinHeap(int capacity, int nodeCount)
        {
            _data = new (int, float)[capacity];
            _count = 0;
            _indexMap = new int[nodeCount];
            for (int i = 0; i < nodeCount; i++) _indexMap[i] = -1;
        }

        public void Clear()
        {
            _count = 0;
            for (int i = 0; i < _indexMap.Length; i++) _indexMap[i] = -1;
        }

        public bool Contains(int node) => _indexMap[node] >= 0;

        public void Push(int node, float priority)
        {
            if (_indexMap[node] >= 0)
            {
                // 已经在堆里，做 decrease key
                DecreaseKey(node, priority);
                return;
            }
            if (_count >= _data.Length)
                Array.Resize(ref _data, _data.Length * 2);
            _data[_count] = (node, priority);
            _indexMap[node] = _count;
            SiftUp(_count);
            _count++;
        }

        public int Pop(out float priority)
        {
            if (_count == 0) throw new InvalidOperationException("heap empty");
            var top = _data[0];
            _count--;
            if (_count > 0)
            {
                _data[0] = _data[_count];
                _indexMap[_data[0].node] = 0;
                SiftDown(0);
            }
            _indexMap[top.node] = -1;
            priority = top.priority;
            return top.node;
        }

        public int Peek(out float priority)
        {
            if (_count == 0) throw new InvalidOperationException("heap empty");
            priority = _data[0].priority;
            return _data[0].node;
        }

        private void DecreaseKey(int node, float newPriority)
        {
            int i = _indexMap[node];
            if (newPriority < _data[i].priority)
            {
                _data[i].priority = newPriority;
                SiftUp(i);
            }
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_data[i].priority < _data[parent].priority)
                {
                    Swap(i, parent);
                    i = parent;
                }
                else break;
            }
        }

        private void SiftDown(int i)
        {
            int n = _count;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                if (l < n && _data[l].priority < _data[smallest].priority) smallest = l;
                if (r < n && _data[r].priority < _data[smallest].priority) smallest = r;
                if (smallest == i) break;
                Swap(i, smallest);
                i = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            (_data[a], _data[b]) = (_data[b], _data[a]);
            _indexMap[_data[a].node] = a;
            _indexMap[_data[b].node] = b;
        }
    }

    // ========================================================================
    //  三、A* 实现
    // ========================================================================

    /// <summary>
    /// 经典 A* 算法。f(n) = g(n) + h(n)。
    /// 启发函数必须 admissible(不高估)才能保证最优，
    /// 但游戏里经常稍微超权重(1.0~1.5)换速度，可接受次优。
    /// </summary>
    public sealed class AStarPathfinder
    {
        private readonly IPathGraph _graph;
        private readonly MinHeap _open;
        private readonly float[] _gScore;
        private readonly int[] _cameFrom;
        private readonly bool[] _closed;

        public int MaxNodesExpanded { get; set; } = 10000;
        public float HeuristicWeight { get; set; } = 1f;
        public bool ReturnPartialPath { get; set; } = true;
        public float PartialThreshold { get; set; } = 5f;

        public AStarPathfinder(IPathGraph graph)
        {
            _graph = graph;
            int n = graph.NodeCount;
            _open = new MinHeap(n + 16, n);
            _gScore = new float[n];
            _cameFrom = new int[n];
            _closed = new bool[n];
            for (int i = 0; i < n; i++) { _gScore[i] = float.MaxValue; _cameFrom[i] = -1; }
        }

        /// <summary>执行 A* 寻路。</summary>
        public PathResult FindPath(int start, int goal, PathfindingOptions? opts = null)
        {
            var options = opts ?? PathfindingOptions.Default;
            HeuristicWeight = options.HeuristicWeight;
            MaxNodesExpanded = options.MaxNodesExpanded;
            ReturnPartialPath = options.ReturnPartialPath;
            PartialThreshold = options.PartialPathThreshold;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new PathResult();

            if (start < 0 || goal < 0 || start >= _graph.NodeCount || goal >= _graph.NodeCount)
            {
                result.State = PathState.Invalid;
                sw.Stop();
                result.TimeMs = sw.ElapsedMilliseconds;
                return result;
            }
            if (start == goal)
            {
                result.Nodes.Add(start);
                result.Waypoints.Add(_graph.GetNodePosition(start));
                result.State = PathState.Success;
                sw.Stop();
                result.TimeMs = sw.ElapsedMilliseconds;
                return result;
            }

            Reset();
            _gScore[start] = 0;
            _open.Push(start, _graph.GetHeuristic(start, goal) * HeuristicWeight);

            int expanded = 0;
            int bestNode = start;
            float bestH = _graph.GetHeuristic(start, goal);

            while (_open.Count > 0)
            {
                float fCur;
                int cur = _open.Pop(out fCur);
                if (_closed[cur]) continue;
                _closed[cur] = true;
                expanded++;

                if (cur == goal)
                {
                    ReconstructPath(result, start, goal);
                    result.State = PathState.Success;
                    result.NodesExpanded = expanded;
                    result.TimeMs = sw.ElapsedMilliseconds;
                    return result;
                }

                // 记录最接近 goal 的节点(给 partial path 用)
                float h = _graph.GetHeuristic(cur, goal);
                if (h < bestH) { bestH = h; bestNode = cur; }

                if (expanded >= MaxNodesExpanded)
                    break;

                foreach (var (neighbor, cost) in _graph.GetNeighbors(cur))
                {
                    if (_closed[neighbor]) continue;
                    float tentativeG = _gScore[cur] + cost;
                    if (tentativeG < _gScore[neighbor])
                    {
                        _cameFrom[neighbor] = cur;
                        _gScore[neighbor] = tentativeG;
                        float f = tentativeG + _graph.GetHeuristic(neighbor, goal) * HeuristicWeight;
                        _open.Push(neighbor, f);
                        result.NodesVisited++;
                    }
                }
            }

            // 找不到终点，返回 partial path
            if (ReturnPartialPath && bestNode != start)
            {
                ReconstructPath(result, start, bestNode);
                result.Waypoints.Add(_graph.GetNodePosition(goal));
                result.State = PathState.PartialPath;
                result.NodesExpanded = expanded;
                result.TimeMs = sw.ElapsedMilliseconds;
                return result;
            }

            result.State = PathState.Failed;
            result.NodesExpanded = expanded;
            result.TimeMs = sw.ElapsedMilliseconds;
            return result;
        }

        private void Reset()
        {
            _open.Clear();
            for (int i = 0; i < _gScore.Length; i++)
            {
                _gScore[i] = float.MaxValue;
                _cameFrom[i] = -1;
                _closed[i] = false;
            }
        }

        private void ReconstructPath(PathResult result, int start, int goal)
        {
            var path = new List<int>();
            int cur = goal;
            while (cur != -1 && cur != start)
            {
                path.Add(cur);
                cur = _cameFrom[cur];
            }
            path.Add(start);
            path.Reverse();

            result.Nodes = path;
            result.Waypoints.Clear();
            float totalCost = 0;
            for (int i = 0; i < path.Count; i++)
            {
                var pos = _graph.GetNodePosition(path[i]);
                result.Waypoints.Add(pos);
                if (i > 0) totalCost += _graph.GetCost(path[i - 1], path[i]);
            }
            result.TotalCost = totalCost;
        }
    }

    // ========================================================================
    //  四、Theta* 实现(任意角度 A*)
    // ========================================================================

    /// <summary>
    /// Theta* 是 A* 的变种，允许路径"切角"。
    /// 关键区别：扩展邻居时，如果父节点和邻居之间视线无阻，
    /// 就直接从父节点连过去，跳过当前节点。路径更短更自然。
    /// </summary>
    public sealed class ThetaStarPathfinder
    {
        private readonly IPathGraph _graph;
        private readonly MinHeap _open;
        private readonly float[] _gScore;
        private readonly int[] _cameFrom;
        private readonly bool[] _closed;

        public int MaxNodesExpanded { get; set; } = 10000;
        public float HeuristicWeight { get; set; } = 1f;
        public bool UseStringPulling { get; set; } = true;

        public ThetaStarPathfinder(IPathGraph graph)
        {
            _graph = graph;
            int n = graph.NodeCount;
            _open = new MinHeap(n + 16, n);
            _gScore = new float[n];
            _cameFrom = new int[n];
            _closed = new bool[n];
        }

        public PathResult FindPath(int start, int goal)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new PathResult();
            Reset();

            _gScore[start] = 0;
            _cameFrom[start] = start;
            _open.Push(start, _graph.GetHeuristic(start, goal) * HeuristicWeight);

            int expanded = 0;
            while (_open.Count > 0)
            {
                float fCur;
                int cur = _open.Pop(out fCur);
                if (_closed[cur]) continue;
                _closed[cur] = true;
                expanded++;

                if (cur == goal)
                {
                    ReconstructPath(result, start, goal);
                    result.State = PathState.Success;
                    result.NodesExpanded = expanded;
                    result.TimeMs = sw.ElapsedMilliseconds;
                    return result;
                }

                if (expanded >= MaxNodesExpanded) break;

                foreach (var (neighbor, cost) in _graph.GetNeighbors(cur))
                {
                    if (_closed[neighbor]) continue;

                    int parent = _cameFrom[cur];
                    // 关键：检查 parent -> neighbor 是否 line of sight
                    if (parent != cur && _graph.LineOfSight(parent, neighbor))
                    {
                        float tentativeG = _gScore[parent] + _graph.GetCost(parent, neighbor);
                        if (tentativeG < _gScore[neighbor])
                        {
                            _cameFrom[neighbor] = parent;
                            _gScore[neighbor] = tentativeG;
                            float f = tentativeG + _graph.GetHeuristic(neighbor, goal) * HeuristicWeight;
                            _open.Push(neighbor, f);
                        }
                    }
                    else
                    {
                        // 退化为普通 A*
                        float tentativeG = _gScore[cur] + cost;
                        if (tentativeG < _gScore[neighbor])
                        {
                            _cameFrom[neighbor] = cur;
                            _gScore[neighbor] = tentativeG;
                            float f = tentativeG + _graph.GetHeuristic(neighbor, goal) * HeuristicWeight;
                            _open.Push(neighbor, f);
                        }
                    }
                }
            }

            result.State = PathState.Failed;
            result.NodesExpanded = expanded;
            result.TimeMs = sw.ElapsedMilliseconds;
            return result;
        }

        private void Reset()
        {
            _open.Clear();
            for (int i = 0; i < _gScore.Length; i++)
            {
                _gScore[i] = float.MaxValue;
                _cameFrom[i] = -1;
                _closed[i] = false;
            }
        }

        private void ReconstructPath(PathResult result, int start, int goal)
        {
            var path = new List<int>();
            int cur = goal;
            while (cur != -1 && cur != start)
            {
                path.Add(cur);
                cur = _cameFrom[cur];
            }
            path.Add(start);
            path.Reverse();
            result.Nodes = path;
            result.Waypoints.Clear();
            foreach (var n in path) result.Waypoints.Add(_graph.GetNodePosition(n));
        }
    }

    // ========================================================================
    //  五、D* Lite 实现
    // ========================================================================

    /// <summary>
    /// D* Lite 算法。当边权发生变化时能增量重规划，比每次重新 A* 快得多。
    /// 适合机器人 SLAM / 游戏里动态障碍物场景。
    ///
    /// 核心思想：维护每个节点的 rhs 值(one-step lookahead)，
    /// 当边权变化时只更新受影响节点，用 priority queue 处理 inconsistent 节点。
    ///
    /// 这个算法比较难懂，建议先看 Sven Koenig 的原论文。
    /// </summary>
    public sealed class DStarLitePathfinder
    {
        private readonly IPathGraph _graph;
        private readonly float[] _g;
        private readonly float[] _rhs;
        private readonly bool[] _overConsistent;
        private readonly MinHeap _open;
        private int _start, _goal;
        private float _km; // 启发函数修正量
        private int _lastStart;

        public DStarLitePathfinder(IPathGraph graph)
        {
            _graph = graph;
            int n = graph.NodeCount;
            _g = new float[n];
            _rhs = new float[n];
            _overConsistent = new bool[n];
            _open = new MinHeap(n + 16, n);
        }

        /// <summary>初始化/重置，从 start 到 goal。</summary>
        public void Initialize(int start, int goal)
        {
            _start = start;
            _goal = goal;
            _km = 0;
            _lastStart = start;
            for (int i = 0; i < _g.Length; i++)
            {
                _g[i] = float.MaxValue;
                _rhs[i] = float.MaxValue;
                _overConsistent[i] = false;
            }
            _open.Clear();
            _rhs[goal] = 0;
            _open.Push(goal, CalcKey(goal));
        }

        /// <summary>计算 D* 的 key (优先级)。</summary>
        private float CalcKey(int node)
        {
            float g = _g[node];
            float rhs = _rhs[node];
            float min = MathF.Min(g, rhs);
            float h = _graph.GetHeuristic(_start, node);
            return min + h + _km;
        }

        /// <summary>更新节点 rhs 值，必要时加入/移出 open list。</summary>
        private void UpdateVertex(int u)
        {
            if (u != _goal)
            {
                float minRhs = float.MaxValue;
                foreach (var (succ, cost) in _graph.GetNeighbors(u))
                {
                    float v = _g[succ] + cost;
                    if (v < minRhs) minRhs = v;
                }
                _rhs[u] = minRhs;
            }
            if (_g[u] != _rhs[u])
            {
                _open.Push(u, CalcKey(u));
                _overConsistent[u] = _g[u] > _rhs[u];
            }
        }

        /// <summary>主循环：扩展 inconsistent 节点直到图稳定。</summary>
        private void ComputeShortestPath()
        {
            int safety = _graph.NodeCount * 4 + 1000;
            while (_open.Count > 0 && safety-- > 0)
            {
                float kTop;
                int u = _open.Peek(out kTop);
                float kStart = CalcKey(_start);
                if (kTop >= kStart && MathF.Abs(_rhs[_start] - _g[_start]) < 1e-4f)
                    break;
                int u2 = _open.Pop(out kTop);
                if (_g[u2] > _rhs[u2])
                {
                    _g[u2] = _rhs[u2];
                }
                else
                {
                    _g[u2] = float.MaxValue;
                    UpdateVertex(u2);
                }
                foreach (var (succ, cost) in _graph.GetNeighbors(u2))
                    UpdateVertex(succ);
            }
        }

        /// <summary>初始规划。</summary>
        public PathResult FindPath()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new PathResult();
            ComputeShortestPath();
            if (_g[_start] >= float.MaxValue)
            {
                result.State = PathState.Failed;
                result.TimeMs = sw.ElapsedMilliseconds;
                return result;
            }
            ReconstructPath(result);
            result.State = PathState.Success;
            result.TimeMs = sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// 当某条边的代价变化时调用，增量更新。
        /// 比重新跑一次 A* 快得多。
        /// </summary>
        public PathResult UpdateEdge(int u, int v, float newCost)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // km 修正
            _km += _graph.GetHeuristic(_lastStart, _start);
            _lastStart = _start;

            // 更新 rhs(u) 和 rhs(v)
            UpdateVertex(u);
            UpdateVertex(v);
            ComputeShortestPath();

            var result = new PathResult();
            if (_g[_start] >= float.MaxValue)
            {
                result.State = PathState.Failed;
                result.TimeMs = sw.ElapsedMilliseconds;
                return result;
            }
            ReconstructPath(result);
            result.State = PathState.Success;
            result.TimeMs = sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>更新起点(机器人移动)。</summary>
        public void MoveStart(int newStart)
        {
            _km += _graph.GetHeuristic(newStart, _start);
            _lastStart = _start;
            _start = newStart;
        }

        private void ReconstructPath(PathResult result)
        {
            var path = new List<int>();
            int cur = _start;
            path.Add(cur);
            int safety = _graph.NodeCount + 100;
            while (cur != _goal && safety-- > 0)
            {
                int next = -1;
                float minCost = float.MaxValue;
                foreach (var (succ, cost) in _graph.GetNeighbors(cur))
                {
                    if (succ == cur) continue;
                    float v = cost + _g[succ];
                    if (v < minCost) { minCost = v; next = succ; }
                }
                if (next == -1 || next == cur) break;
                path.Add(next);
                cur = next;
            }
            result.Nodes = path;
            result.Waypoints.Clear();
            foreach (var n in path) result.Waypoints.Add(_graph.GetNodePosition(n));
        }
    }

    // ========================================================================
    //  六、JPS+ (Jump Point Search Plus)
    // ========================================================================

    /// <summary>
    /// JPS+ 算法。仅适用于均匀网格地图，比 A* 快 10 倍以上。
    /// 核心思想：在直线上一直跳到"跳点"(必须转弯的点)才扩展，
    /// 大大减少 open list 操作。
    ///
    /// JPS+ 是 JPS 的预计算增强版，预先算好每个格子每个方向能跳多远。
    /// </summary>
    public sealed class JPSPlusPathfinder
    {
        // 方向编码：0=E,1=W,2=N,3=S,4=NE,5=NW,6=SE,7=SW
        private static readonly int[] DirX = { 1, -1, 0, 0, 1, -1, 1, -1 };
        private static readonly int[] DirZ = { 0, 0, 1, -1, 1, 1, -1, -1 };
        private static readonly bool[] IsDiagonal = { false, false, false, false, true, true, true, true };

        private readonly GridGraph _grid;
        /// <summary>
        /// 预计算的跳跃距离。
        /// 第一维是格子 index，第二维是 8 个方向。
        /// 值 = 在该方向上能跳多远(格子数)，0 表示不能跳。
        /// </summary>
        private readonly int[,] _jumpDistances;

        public int MaxNodesExpanded { get; set; } = 100000;

        public JPSPlusPathfinder(GridGraph grid)
        {
            _grid = grid;
            _jumpDistances = new int[grid.NodeCount, 8];
            PrecomputeJumpPoints();
        }

        /// <summary>预计算每个格子每个方向的跳跃距离。这个比较慢，离线做。</summary>
        private void PrecomputeJumpPoints()
        {
            // 这块逻辑很难写对，建议参考 Harabor & Grastien 2012 论文
            for (int z = 0; z < _grid.Height; z++)
            {
                for (int x = 0; x < _grid.Width; x++)
                {
                    int node = _grid.NodeIndex(x, z);
                    if (!_grid.IsWalkable(x, z)) continue;
                    for (int d = 0; d < 8; d++)
                    {
                        _jumpDistances[node, d] = CalcJumpDistance(x, z, d);
                    }
                }
            }
        }

        private int CalcJumpDistance(int x, int z, int dir)
        {
            int dx = DirX[dir], dz = DirZ[dir];
            int dist = 0;
            int cx = x, cz = z;
            while (true)
            {
                cx += dx; cz += dz; dist++;
                if (!_grid.IsWalkable(cx, cz)) return 0; // 撞墙
                if (cx == _grid.GoalX && cz == _grid.GoalZ) return dist; // 到 goal
                // 检查是否是跳点(有 forced neighbor)
                if (IsForcedNeighbor(cx, cz, dx, dz)) return dist;
                // 对角方向：如果某个分量方向被挡，必须停
                if (IsDiagonal[dir])
                {
                    if (!_grid.IsWalkable(cx - dx, cz) || !_grid.IsWalkable(cx, cz - dz))
                        return dist;
                }
                if (dist > _grid.Width * _grid.Height) return 0; // 安全上限
            }
        }

        private bool IsForcedNeighbor(int x, int z, int dx, int dz)
        {
            // 简化：检查是否有非对称障碍导致 forced turn
            if (dx != 0 && dz != 0) // 对角
            {
                if ((!_grid.IsWalkable(x - dx, z) && _grid.IsWalkable(x - dx, z + dz)) ||
                    (!_grid.IsWalkable(x, z - dz) && _grid.IsWalkable(x + dx, z - dz)))
                    return true;
            }
            else if (dx != 0) // 水平
            {
                if ((!_grid.IsWalkable(x, z - 1) && _grid.IsWalkable(x + dx, z - 1)) ||
                    (!_grid.IsWalkable(x, z + 1) && _grid.IsWalkable(x + dx, z + 1)))
                    return true;
            }
            else // 垂直
            {
                if ((!_grid.IsWalkable(x - 1, z) && _grid.IsWalkable(x - 1, z + dz)) ||
                    (!_grid.IsWalkable(x + 1, z) && _grid.IsWalkable(x + 1, z + dz)))
                    return true;
            }
            return false;
        }

        public PathResult FindPath(int startX, int startZ, int goalX, int goalZ)
        {
            _grid.GoalX = goalX; _grid.GoalZ = goalZ;
            // 因为预计算依赖 goal，得重算
            PrecomputeJumpPoints();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new PathResult();
            var open = new MinHeap(_grid.NodeCount + 16, _grid.NodeCount);
            var gScore = new float[_grid.NodeCount];
            var cameFrom = new int[_grid.NodeCount];
            var closed = new bool[_grid.NodeCount];
            for (int i = 0; i < gScore.Length; i++) { gScore[i] = float.MaxValue; cameFrom[i] = -1; }

            int start = _grid.NodeIndex(startX, startZ);
            int goal = _grid.NodeIndex(goalX, goalZ);
            gScore[start] = 0;
            open.Push(start, _grid.GetHeuristic(start, goal));

            int expanded = 0;
            while (open.Count > 0)
            {
                float f;
                int cur = open.Pop(out f);
                if (closed[cur]) continue;
                closed[cur] = true;
                expanded++;
                if (cur == goal)
                {
                    ReconstructJPSPath(result, start, goal, cameFrom);
                    result.State = PathState.Success;
                    result.NodesExpanded = expanded;
                    result.TimeMs = sw.ElapsedMilliseconds;
                    return result;
                }
                if (expanded >= MaxNodesExpanded) break;

                // 跳跃扩展 8 方向
                for (int d = 0; d < 8; d++)
                {
                    int dist = _jumpDistances[cur, d];
                    if (dist == 0) continue;
                    int nx, nz;
                    _grid.NodeCoord(cur, out int cx, out int cz);
                    nx = cx + DirX[d] * dist;
                    nz = cz + DirZ[d] * dist;
                    int neighbor = _grid.NodeIndex(nx, nz);
                    if (closed[neighbor]) continue;
                    float stepCost = IsDiagonal[d] ? dist * 1.41421356f : dist;
                    float tentativeG = gScore[cur] + stepCost;
                    if (tentativeG < gScore[neighbor])
                    {
                        gScore[neighbor] = tentativeG;
                        cameFrom[neighbor] = cur;
                        open.Push(neighbor, tentativeG + _grid.GetHeuristic(neighbor, goal));
                    }
                }
            }
            result.State = PathState.Failed;
            result.NodesExpanded = expanded;
            result.TimeMs = sw.ElapsedMilliseconds;
            return result;
        }

        private void ReconstructJPSPath(PathResult result, int start, int goal, int[] cameFrom)
        {
            var path = new List<int>();
            int cur = goal;
            while (cur != -1 && cur != start) { path.Add(cur); cur = cameFrom[cur]; }
            path.Add(start);
            path.Reverse();
            result.Nodes = path;
            result.Waypoints.Clear();
            foreach (var n in path)
            {
                _grid.NodeCoord(n, out int x, out int z);
                result.Waypoints.Add(_grid.GetNodePosition(n));
            }
        }
    }

    // ========================================================================
    //  七、网格图实现(给 A*/JPS 用)
    // ========================================================================

    /// <summary>
    /// 网格地图。每个格子 walkable/blocked。
    /// 给 A*/JPS+/Theta* 当 IPathGraph 用。
    /// </summary>
    public sealed class GridGraph : IPathGraph
    {
        public int Width, Height;
        public float CellSize;
        public Vector3 Origin;
        public bool[] Walkable;
        public float[] Costs;
        public int GoalX, GoalZ; // JPS 预计算用

        public int NodeCount => Width * Height;

        public GridGraph(int w, int h, float cellSize, Vector3 origin)
        {
            Width = w; Height = h; CellSize = cellSize; Origin = origin;
            Walkable = new bool[w * h];
            Costs = new float[w * h];
            for (int i = 0; i < Walkable.Length; i++) { Walkable[i] = true; Costs[i] = 1f; }
        }

        public int NodeIndex(int x, int z) => z * Width + x;
        public void NodeCoord(int idx, out int x, out int z) { x = idx % Width; z = idx / Width; }
        public bool IsWalkable(int x, int z) => x >= 0 && x < Width && z >= 0 && z < Height && Walkable[NodeIndex(x, z)];

        public void SetWalkable(int x, int z, bool walkable)
        {
            if (x >= 0 && x < Width && z >= 0 && z < Height)
                Walkable[NodeIndex(x, z)] = walkable;
        }
        public void SetCost(int x, int z, float cost)
        {
            if (x >= 0 && x < Width && z >= 0 && z < Height)
                Costs[NodeIndex(x, z)] = cost;
        }

        public float GetCost(int from, int to)
        {
            NodeCoord(from, out int x1, out int z1);
            NodeCoord(to, out int x2, out int z2);
            int dx = Math.Abs(x2 - x1), dz = Math.Abs(z2 - z1);
            if (dx + dz == 0) return 0;
            if (dx + dz == 1) return (Costs[from] + Costs[to]) * 0.5f;
            if (dx == 1 && dz == 1) return (Costs[from] + Costs[to]) * 0.5f * 1.41421356f;
            return float.MaxValue;
        }

        public float GetHeuristic(int from, int to)
        {
            NodeCoord(from, out int x1, out int z1);
            NodeCoord(to, out int x2, out int z2);
            int dx = Math.Abs(x2 - x1), dz = Math.Abs(z2 - z1);
            // octile distance
            int min = Math.Min(dx, dz), max = Math.Max(dx, dz);
            return (max - min + min * 1.41421356f) * CellSize;
        }

        public IEnumerable<(int neighbor, float cost)> GetNeighbors(int node)
        {
            NodeCoord(node, out int x, out int z);
            // 8 方向
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (!IsWalkable(nx, nz)) continue;
                    // 对角移动要求两个正交邻居都可走(防止穿墙)
                    if (dx != 0 && dz != 0)
                    {
                        if (!IsWalkable(x + dx, z) || !IsWalkable(x, z + dz)) continue;
                    }
                    int ni = NodeIndex(nx, nz);
                    yield return (ni, GetCost(node, ni));
                }
            }
        }

        public Vector3 GetNodePosition(int node)
        {
            NodeCoord(node, out int x, out int z);
            return Origin + new Vector3((x + 0.5f) * CellSize, 0, (z + 0.5f) * CellSize);
        }

        public bool LineOfSight(int from, int to)
        {
            NodeCoord(from, out int x0, out int z0);
            NodeCoord(to, out int x1, out int z1);
            // Bresenham 线段算法
            int dx = Math.Abs(x1 - x0), dz = Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;
            int cx = x0, cz = z0;
            while (true)
            {
                if (!IsWalkable(cx, cz)) return false;
                if (cx == x1 && cz == z1) return true;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; cx += sx; }
                if (e2 < dx) { err += dx; cz += sz; }
            }
        }
    }

    // ========================================================================
    //  八、NavMesh 图适配器
    // ========================================================================

    /// <summary>把 GeneratedNavMesh 适配成 IPathGraph，给 A*/Theta* 用。</summary>
    public sealed class NavMeshGraphAdapter : IPathGraph
    {
        private readonly GeneratedNavMesh _nav;
        private readonly Dictionary<int, List<(int, float)>> _adjacency;

        public int NodeCount => _nav.Polygons.Length;

        public NavMeshGraphAdapter(GeneratedNavMesh nav)
        {
            _nav = nav;
            _adjacency = new Dictionary<int, List<(int, float)>>();
            BuildAdjacency();
        }

        private void BuildAdjacency()
        {
            // 用质心距离当边权，简化处理
            // 完整实现应该用共享边中点距离 + area cost
            for (int i = 0; i < _nav.Polygons.Length; i++)
            {
                _adjacency[i] = new List<(int, float)>();
            }
            // 简化：所有距离 < 阈值的多边形都连边
            float threshold = _nav.CellSize * 4;
            for (int i = 0; i < _nav.Polygons.Length; i++)
            {
                var ci = _nav.PolyCenter(i);
                for (int j = i + 1; j < _nav.Polygons.Length; j++)
                {
                    var cj = _nav.PolyCenter(j);
                    float d = Vector3.Distance(ci, cj);
                    if (d < threshold)
                    {
                        float costI = _nav.AreaCosts.TryGetValue(_nav.Polygons[i].Area, out var c1) ? c1 : 1f;
                        float costJ = _nav.AreaCosts.TryGetValue(_nav.Polygons[j].Area, out var c2) ? c2 : 1f;
                        float cost = d * (costI + costJ) * 0.5f;
                        _adjacency[i].Add((j, cost));
                        _adjacency[j].Add((i, cost));
                    }
                }
            }
        }

        public float GetCost(int from, int to)
        {
            if (_adjacency.TryGetValue(from, out var list))
            {
                foreach (var (n, c) in list)
                    if (n == to) return c;
            }
            return float.MaxValue;
        }

        public float GetHeuristic(int from, int to)
            => Vector3.Distance(_nav.PolyCenter(from), _nav.PolyCenter(to));

        public IEnumerable<(int neighbor, float cost)> GetNeighbors(int node)
            => _adjacency.TryGetValue(node, out var list) ? list : Array.Empty<(int, float)>();

        public Vector3 GetNodePosition(int node) => _nav.PolyCenter(node);

        public bool LineOfSight(int from, int to)
        {
            // 简化：检查中间没有不可走多边形
            var a = _nav.PolyCenter(from);
            var b = _nav.PolyCenter(to);
            // 采样中间点
            int samples = (int)(Vector3.Distance(a, b) / (_nav.CellSize * 2)) + 1;
            for (int i = 1; i < samples; i++)
            {
                float t = (float)i / samples;
                var p = Vector3.Lerp(a, b, t);
                // 找最近多边形检查 area
                // 简化：直接返回 true
            }
            return true;
        }
    }

    // ========================================================================
    //  九、寻路系统外观
    // ========================================================================

    /// <summary>
    /// 寻路系统统一入口。根据算法枚举自动选实现。
    /// </summary>
    public sealed class PathfindingSystem
    {
        private readonly Dictionary<PathfindingAlgorithm, object> _cache = new();
        private IPathGraph? _graph;
        private PathfindingOptions _defaultOpts = PathfindingOptions.Default;

        public PathfindingOptions DefaultOptions
        {
            get => _defaultOpts;
            set => _defaultOpts = value;
        }

        public IPathGraph? Graph => _graph;

        /// <summary>设置当前寻路图。</summary>
        public void SetGraph(IPathGraph graph)
        {
            _graph = graph;
            _cache.Clear();
        }

        /// <summary>执行寻路。</summary>
        public PathResult FindPath(int start, int goal, PathfindingOptions? opts = null)
        {
            if (_graph == null) throw new InvalidOperationException("没设置 graph，先 SetGraph");
            var options = opts ?? _defaultOpts;
            return options.Algorithm switch
            {
                PathfindingAlgorithm.AStar => GetOrCreate<AStarPathfinder>().FindPath(start, goal, options),
                PathfindingAlgorithm.ThetaStar => GetOrCreate<ThetaStarPathfinder>().FindPath(start, goal),
                PathfindingAlgorithm.DStarLite => GetOrCreate<DStarLitePathfinder>().FindPath(),
                PathfindingAlgorithm.JPSPlus when _graph is GridGraph g => FindJPSPath(g, start, goal),
                _ => GetOrCreate<AStarPathfinder>().FindPath(start, goal, options)
            };
        }

        private T GetOrCreate<T>() where T : class
        {
            var alg = typeof(T) == typeof(AStarPathfinder) ? PathfindingAlgorithm.AStar :
                      typeof(T) == typeof(ThetaStarPathfinder) ? PathfindingAlgorithm.ThetaStar :
                      PathfindingAlgorithm.DStarLite;
            if (_cache.TryGetValue(alg, out var obj)) return (T)obj;
            T instance = CreateInstance<T>();
            _cache[alg] = instance;
            return instance;
        }

        private T CreateInstance<T>() where T : class
        {
            if (typeof(T) == typeof(AStarPathfinder)) return (T)(object)new AStarPathfinder(_graph!);
            if (typeof(T) == typeof(ThetaStarPathfinder)) return (T)(object)new ThetaStarPathfinder(_graph!);
            if (typeof(T) == typeof(DStarLitePathfinder)) return (T)(object)new DStarLitePathfinder(_graph!);
            throw new NotSupportedException($"不支持的类型 {typeof(T)}");
        }

        private PathResult FindJPSPath(GridGraph g, int start, int goal)
        {
            // JPS 需要的是坐标不是 id
            g.NodeCoord(start, out int sx, out int sz);
            g.NodeCoord(goal, out int gx, out int gz);
            var jps = new JPSPlusPathfinder(g);
            return jps.FindPath(sx, sz, gx, gz);
        }

        /// <summary>批量寻路(给 crowd 用)。并行可选。</summary>
        public List<PathResult> FindPathsBatch(List<(int start, int goal)> requests, bool parallel = false)
        {
            var results = new List<PathResult>(requests.Count);
            if (parallel)
            {
                // TODO: 多线程并行，注意 graph 共享读取安全
                // 现在先用单线程
            }
            for (int i = 0; i < requests.Count; i++)
            {
                results.Add(FindPath(requests[i].start, requests[i].goal));
            }
            return results;
        }

        /// <summary>找最近的可行走节点(给落点校正用)。</summary>
        public int FindNearestWalkable(Vector3 worldPos, float maxRadius = 50f)
        {
            if (_graph == null) return -1;
            // BFS 找最近的
            // 简化：遍历所有节点找最近的
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _graph.NodeCount; i++)
            {
                var p = _graph.GetNodePosition(i);
                float d = Vector3.Distance(p, worldPos);
                if (d < bestDist && d < maxRadius) { bestDist = d; best = i; }
            }
            return best;
        }

        /// <summary>检查路径是否还有效(边权没变)。</summary>
        public bool IsPathValid(PathResult path)
        {
            if (!path.IsValid) return false;
            for (int i = 1; i < path.Nodes.Count; i++)
            {
                if (_graph!.GetCost(path.Nodes[i - 1], path.Nodes[i]) >= float.MaxValue)
                    return false;
            }
            return true;
        }

        /// <summary>估算路径长度(世界坐标)。</summary>
        public static float ComputePathLength(PathResult path)
        {
            float len = 0;
            for (int i = 1; i < path.Waypoints.Count; i++)
                len += Vector3.Distance(path.Waypoints[i - 1], path.Waypoints[i]);
            return len;
        }
    }

    // ========================================================================
    //  十、路径工具
    // ========================================================================

    /// <summary>路径相关工具方法。</summary>
    public static class PathUtils
    {
        /// <summary>简化路径：去掉共线点。</summary>
        public static List<Vector3> SimplifyCollinear(List<Vector3> path)
        {
            if (path.Count < 3) return new List<Vector3>(path);
            var result = new List<Vector3> { path[0] };
            for (int i = 1; i < path.Count - 1; i++)
            {
                var a = path[i - 1];
                var b = path[i];
                var c = path[i + 1];
                // XZ 平面共线检测
                var d1 = new Vector2(b.X - a.X, b.Z - a.Z);
                var d2 = new Vector2(c.X - b.X, c.Z - b.Z);
                if (MathF.Abs(d1.X * d2.Y - d1.Y * d2.X) > 1e-4f)
                    result.Add(b);
            }
            result.Add(path[path.Count - 1]);
            return result;
        }

        /// <summary>路径插值：在路径上按距离采样。</summary>
        public static List<Vector3> ResampleByDistance(List<Vector3> path, float sampleDist)
        {
            if (path.Count < 2) return new List<Vector3>(path);
            var result = new List<Vector3> { path[0] };
            float acc = 0;
            for (int i = 1; i < path.Count; i++)
            {
                var seg = path[i] - path[i - 1];
                float segLen = seg.Length();
                while (acc + segLen >= sampleDist)
                {
                    float t = (sampleDist - acc) / segLen;
                    result.Add(path[i - 1] + seg * t);
                    seg = path[i] - result[result.Count - 1];
                    segLen = seg.Length();
                    acc = 0;
                }
                acc += segLen;
            }
            if (result[result.Count - 1] != path[path.Count - 1])
                result.Add(path[path.Count - 1]);
            return result;
        }

        /// <summary>路径上 t 处的位置(0=起点, 1=终点)。</summary>
        public static Vector3 SampleAt(List<Vector3> path, float t)
        {
            if (path.Count == 0) return Vector3.Zero;
            if (path.Count == 1) return path[0];
            t = Math.Clamp(t, 0, 1);
            float totalLen = 0;
            for (int i = 1; i < path.Count; i++)
                totalLen += Vector3.Distance(path[i - 1], path[i]);
            float target = t * totalLen;
            float acc = 0;
            for (int i = 1; i < path.Count; i++)
            {
                float segLen = Vector3.Distance(path[i - 1], path[i]);
                if (acc + segLen >= target)
                {
                    float st = (target - acc) / segLen;
                    return Vector3.Lerp(path[i - 1], path[i], st);
                }
                acc += segLen;
            }
            return path[path.Count - 1];
        }

        /// <summary>计算路径总长度。</summary>
        public static float TotalLength(List<Vector3> path)
        {
            float len = 0;
            for (int i = 1; i < path.Count; i++)
                len += Vector3.Distance(path[i - 1], path[i]);
            return len;
        }
    }
}
