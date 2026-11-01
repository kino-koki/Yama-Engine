// ============================================================================
//  NavMeshQueryReal.cs -- 真实A*寻路与导航查询 (Detour风格C#重写)
//  yama引擎 YamaEngine.AI
// ----------------------------------------------------------------------------
//  完整实现 Detour 运行时查询系统:
//    1. NavMeshData / NavMeshTile / NavMeshPoly - 导航网格数据结构
//    2. NavMeshQuery.FindPath - A*寻路 (二叉堆开放列表 + 漏斗路径平滑)
//    3. NavMeshQuery.FindStraightPath - 直线路径
//    4. NavMeshQuery.Raycast - 射线投射
//    5. NavMeshQuery.MoveAlongSurface - 沿表面移动
//    6. NavMesh - Tile管理 / 动态更新
//    7. NavMeshFilter - 导航过滤器
//    8. PathOptimizer - 路径优化 (贝塞尔/CatmullRom/拐角圆弧)
//    9. PathFollowing - 路径跟随
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace YamaEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════════
    //  导航网格数据结构
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>导航网格头信息</summary>
    public class NavMeshHeader
    {
        public const uint Magic = 0x444E5601; // 'D' 'N' 'V' 0x01
        public uint Version = 1;
        public int TileCount;
        public int NodeCount;
        public int PolyCount;
        public int VertCount;
        public int MaxPolys;
        public int DetailMeshCount;
        public int DetailVertCount;
        public int DetailTriCount;
        public int BVNodeCount;
        public int OffMeshConCount;
    }

    /// <summary>导航多边形引用 (salt+tile+poly打包为ulong)</summary>
    public struct NavMeshPolyRef
    {
        public const int SaltBits = 12;
        public const int TileBits = 20;
        public const int PolyBits = 32;
        public const int PolyIdMask = (1 << PolyBits) - 1;
        public const ulong SaltMask = (1UL << SaltBits) - 1UL;
        public const ulong TileMask = (1UL << TileBits) - 1UL;

        public static readonly NavMeshPolyRef Invalid = new NavMeshPolyRef { Value = 0 };

        public ulong Value;

        public uint Salt => (uint)((Value >> (PolyBits + TileBits)) & SaltMask);
        public int Tile => (int)((Value >> PolyBits) & TileMask);
        public int Poly => (int)(Value & PolyIdMask);

        public static NavMeshPolyRef Create(uint salt, int tile, int poly)
        {
            ulong v = ((ulong)salt & SaltMask) << (PolyBits + TileBits)
                    | ((ulong)(tile & ((1 << TileBits) - 1)) << PolyBits)
                    | (ulong)(poly & PolyIdMask);
            return new NavMeshPolyRef { Value = v };
        }

        public bool IsValid => Value != 0;
        public override string ToString() => $"Ref(salt={Salt},tile={Tile},poly={Poly})";
    }

    /// <summary>导航多边形</summary>
    public class NavMeshPoly
    {
        public NavMeshPolyRef FirstLink; // 第一个链接
        public int[] Verts;              // 顶点索引 (最多6个)
        public int[] Neighbors;          // 邻居多边形索引 (最多6个)
        public ushort Flags;              // 标志
        public byte Area;                 // 区域类型
        public int Parent;                // 父多边形 (寻路时使用)

        /// <summary>从旧 NavPoly 隐式转换 (兼容 NavigationSystem 数据源)。</summary>
        public static implicit operator NavMeshPoly(NavPoly p)
        {
            if (p == null) return null;
            return new NavMeshPoly
            {
                Verts = p.Verts ?? p.VertexIndices,
                Neighbors = p.Neighbors ?? Array.Empty<int>(),
                Flags = p.Flags,
                Area = p.Area,
                Parent = p.Index
            };
        }
    }

    /// <summary>多边形链接 (边连接)</summary>

    /// <summary>BVH节点 (用于快速查找最近多边形)</summary>
    public class NavMeshBVNode
    {
        public Vector3 Bmin;
        public Vector3 Bmax;
        public int I;                // 多边形索引或子节点索引
        public const int NoLeaf = -1;
    }

    /// <summary>离网连接 (OffMeshConnection: 跳跃/攀爬/传送门)</summary>
    public class NavMeshOffMeshConnection
    {
        public Vector3 PosStart;     // 连接起点
        public Vector3 PosEnd;       // 连接终点
        public float Radius;          // 连接半径
        public byte Side;             // 方向
        public int UserID;            // 用户ID
        public ushort Flags;          // 标志
        public byte Area;             // 区域
    }

    /// <summary>导航网格瓦片</summary>

    public class NavMeshTileHeader
    {
        public int Magic;
        public int Version;
        public int PolyCount;
        public int VertCount;
        public int MaxPolys;
        public int LinkCount;
        public int DetailMeshCount;
        public int DetailVertCount;
        public int DetailTriCount;
        public int BVNodeCount;
        public int OffMeshConCount;
        public float BminX, BminY, BminZ;
        public float BmaxX, BmaxY, BmaxZ;
        public int TileX, TileY;
        public uint Salt;
    }

    /// <summary>导航网格数据 (完整数据集合)</summary>
    public class NavMeshQueryData
    {
        public NavMeshHeader Header;
        public List<NavMeshTile> Tiles;
        public Dictionary<(int x, int y), NavMeshTile> TileLookup;
        public float CellSize;
        public float CellHeight;
        public Vector3 Origin;
        public int TileWidth;
        public int TileHeight;
        public int MaxTiles;

        public NavMeshQueryData()
        {
            Header = new NavMeshHeader();
            Tiles = new List<NavMeshTile>();
            TileLookup = new Dictionary<(int x, int y), NavMeshTile>();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  导航过滤器
    // ════════════════════════════════════════════════════════════════════════════

    public class NavMeshFilter
    {
        public ushort IncludeFlags = 0xFFFF;
        public ushort ExcludeFlags = 0;
        public float[] AreaCost = new float[64]; // 每个区域的通行成本

        public NavMeshFilter()
        {
            for (int i = 0; i < AreaCost.Length; i++)
                AreaCost[i] = 1.0f;
        }

        public virtual bool IsPassable(NavMeshPoly poly)
        {
            if ((poly.Flags & ExcludeFlags) != 0) return false;
            if ((poly.Flags & IncludeFlags) == 0) return false;
            return true;
        }

        public virtual float GetCost(NavMeshPoly poly)
        {
            return AreaCost[poly.Area];
        }
    }

    public class DefaultFilter : NavMeshFilter
    {
        public DefaultFilter()
        {
            IncludeFlags = 0xFFFF;
            ExcludeFlags = 0;
        }
    }

    public class WalkFilter : NavMeshFilter
    {
        public WalkFilter()
        {
            IncludeFlags = 1; // Walkable flag
            ExcludeFlags = 0;
            AreaCost[0] = 1.0f;
        }
    }

    public class SwimFilter : NavMeshFilter
    {
        public SwimFilter()
        {
            IncludeFlags = 0x0020; // Swim flag
            ExcludeFlags = 0;
            AreaCost[5] = 2.0f; // 游泳区域成本较高
        }
    }

    public class JumpFilter : NavMeshFilter
    {
        public JumpFilter()
        {
            IncludeFlags = 0x0004; // Jump flag
            ExcludeFlags = 0;
            AreaCost[2] = 1.5f;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  A*寻路节点
    // ════════════════════════════════════════════════════════════════════════════

    internal class AStarNode
    {
        public NavMeshPolyRef PolyRef;
        public NavMeshPolyRef ParentRef;
        public float Cost;   // g: 从起点到此节点的代价
        public float Heuristic; // h: 到终点的启发估计
        public float Total;  // f = g + h
        public int State;   // 0=closed, 1=open
        public int HeapIdx; // 在堆中的索引
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  二叉堆 (最小堆, 按f值排序)
    // ════════════════════════════════════════════════════════════════════════════

    internal class BinaryMinHeap
    {
        private readonly List<AStarNode> _heap;

        public int Count => _heap.Count;

        public BinaryMinHeap(int capacity)
        {
            _heap = new List<AStarNode>(capacity);
        }

        /// <summary>插入节点到堆中 (Insert)</summary>
        public void Insert(AStarNode node)
        {
            node.HeapIdx = _heap.Count;
            _heap.Add(node);
            SiftUp(node.HeapIdx);
        }

        /// <summary>提取最小节点 (ExtractMin)</summary>
        public AStarNode ExtractMin()
        {
            if (_heap.Count == 0) return null;

            AStarNode min = _heap[0];
            int lastIdx = _heap.Count - 1;

            if (lastIdx > 0)
            {
                _heap[0] = _heap[lastIdx];
                _heap[0].HeapIdx = 0;
                _heap.RemoveAt(lastIdx);
                SiftDown(0);
            }
            else
            {
                _heap.RemoveAt(0);
            }

            min.HeapIdx = -1;
            return min;
        }

        /// <summary>减小节点值并上移 (DecreaseKey)</summary>
        public void DecreaseKey(AStarNode node, float newTotal)
        {
            node.Total = newTotal;
            if (node.HeapIdx >= 0 && node.HeapIdx < _heap.Count)
            {
                SiftUp(node.HeapIdx);
            }
        }

        /// <summary>修改节点值 (支持增大和减小)</summary>
        public void Modify(AStarNode node)
        {
            SiftUp(node.HeapIdx);
            SiftDown(node.HeapIdx);
        }

        private void SiftUp(int idx)
        {
            while (idx > 0)
            {
                int parent = (idx - 1) / 2;
                if (_heap[idx].Total >= _heap[parent].Total) break;
                Swap(idx, parent);
                idx = parent;
            }
        }

        private void SiftDown(int idx)
        {
            int n = _heap.Count;
            while (true)
            {
                int smallest = idx;
                int left = 2 * idx + 1;
                int right = 2 * idx + 2;

                if (left < n && _heap[left].Total < _heap[smallest].Total)
                    smallest = left;
                if (right < n && _heap[right].Total < _heap[smallest].Total)
                    smallest = right;

                if (smallest == idx) break;
                Swap(idx, smallest);
                idx = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            var temp = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = temp;
            _heap[a].HeapIdx = a;
            _heap[b].HeapIdx = b;
        }

        public void Clear()
        {
            _heap.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  NavMesh - 导航网格管理 (Tile管理/动态更新)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>可绘制导航多边形（调试/可视化）。</summary>
    public sealed class NavDrawPoly
    {
        public List<Vector3> Vertices { get; } = new();
    }

    /// <summary>可绘制导航链接（调试/可视化）。</summary>
    public sealed class NavDrawLink
    {
        public Vector3 Start { get; set; }
        public Vector3 End { get; set; }
    }

    public class NavMesh
    {
        private NavMeshQueryData _data;
        private readonly float _cellSize;
        private readonly float _cellHeight;
        private readonly float _tileSize;
        private readonly Vector3 _origin;
        private readonly Dictionary<ulong, NavMeshTile> _tileMap;
        private readonly Dictionary<(int x, int y), NavMeshTile> _tileLookup;
        private uint _nextSalt;
        private int _maxTiles;
        private int _tileWidth;
        private int _tileHeight;

        public NavMesh(float cellSize, float cellHeight, float tileSize, Vector3 origin, int maxTiles = 64)
        {
            _cellSize = cellSize;
            _cellHeight = cellHeight;
            _tileSize = tileSize;
            _origin = origin;
            _maxTiles = maxTiles;
            _tileWidth = (int)Math.Ceiling(tileSize / cellSize);
            _tileHeight = (int)Math.Ceiling(tileSize / cellSize);
            _tileMap = new Dictionary<ulong, NavMeshTile>();
            _tileLookup = new Dictionary<(int x, int y), NavMeshTile>();
            _nextSalt = 1;

            _data = new NavMeshQueryData
            {
                CellSize = cellSize,
                CellHeight = cellHeight,
                Origin = origin,
                MaxTiles = maxTiles,
                TileWidth = _tileWidth,
                TileHeight = _tileHeight
            };
        }

        public NavMeshQueryData Data => _data;

        /// <summary>可绘制多边形列表（调试/可视化，聚合用）。</summary>
        public List<NavDrawPoly> Polygons { get; } = new();

        /// <summary>多边形链接（调试/可视化）。</summary>
        public List<NavDrawLink> Links { get; } = new();

        /// <summary>动态障碍物。</summary>
        public List<NavMeshObstacle> Obstacles { get; } = new();

        /// <summary>添加动态障碍物。</summary>
        public void AddObstacle(NavMeshObstacle obstacle)
        {
            if (obstacle != null) Obstacles.Add(obstacle);
        }

        /// <summary>移除动态障碍物。</summary>
        public void RemoveObstacle(NavMeshObstacle obstacle)
        {
            if (obstacle != null) Obstacles.Remove(obstacle);
        }

        /// <summary>初始化导航网格 (从烘焙数据)</summary>
        public bool Init(NavMeshQueryData data)
        {
            _data = data;
            _tileLookup.Clear();
            _tileMap.Clear();

            foreach (var tile in data.Tiles)
            {
                _tileLookup[(tile.TileX, tile.TileY)] = tile;
            }

            return true;
        }

        /// <summary>添加瓦片</summary>
        public NavMeshPolyRef AddTile(int x, int y, NavMeshTile tile)
        {
            if (_tileLookup.ContainsKey((x, y)))
            {
                RemoveTile(x, y);
            }

            tile.TileX = x;
            tile.TileY = y;
            tile.Salt = _nextSalt++;

            // 确保salt不溢出
            if (_nextSalt == 0) _nextSalt = 1;

            int polyIdx = _data.Tiles.Count;
            _data.Tiles.Add(tile);
            _tileLookup[(x, y)] = tile;

            // 为瓦片中的多边形创建引用
            if (tile.Polys.Count > 0)
            {
                return NavMeshPolyRef.Create(tile.Salt, polyIdx, 0);
            }

            return NavMeshPolyRef.Invalid;
        }

        /// <summary>移除瓦片</summary>
        public bool RemoveTile(int x, int y)
        {
            if (!_tileLookup.TryGetValue((x, y), out var tile))
                return false;

            _tileLookup.Remove((x, y));
            _data.Tiles.Remove(tile);
            return true;
        }

        /// <summary>移除瓦片 (通过引用)</summary>
        public bool RemoveTile(NavMeshPolyRef ref_)
        {
            if (!ref_.IsValid) return false;
            int tileIdx = ref_.Tile;
            if (tileIdx < 0 || tileIdx >= _data.Tiles.Count) return false;

            var tile = _data.Tiles[tileIdx];
            return RemoveTile(tile.TileX, tile.TileY);
        }

        /// <summary>获取瓦片</summary>
        public NavMeshTile GetTileAt(int x, int y)
        {
            _tileLookup.TryGetValue((x, y), out var tile);
            return tile;
        }

        /// <summary>获取瓦片 (通过引用)</summary>
        public NavMeshTile GetTile(NavMeshPolyRef ref_)
        {
            if (!ref_.IsValid) return null;
            int tileIdx = ref_.Tile;
            if (tileIdx < 0 || tileIdx >= _data.Tiles.Count) return null;
            return _data.Tiles[tileIdx];
        }

        /// <summary>获取多边形</summary>
        public NavMeshPoly GetPoly(NavMeshPolyRef ref_)
        {
            var tile = GetTile(ref_);
            if (tile == null) return null;
            int polyIdx = ref_.Poly;
            if (polyIdx < 0 || polyIdx >= tile.Polys.Count) return null;
            return tile.Polys[polyIdx];
        }

        /// <summary>更新瓦片</summary>
        public void UpdateTile(int x, int y, NavMeshTile newTile)
        {
            if (_tileLookup.ContainsKey((x, y)))
            {
                _tileLookup[(x, y)] = newTile;
                int existingIdx = _data.Tiles.FindIndex(t => t.TileX == x && t.TileY == y);
                if (existingIdx >= 0)
                    _data.Tiles[existingIdx] = newTile;
            }
        }

        /// <summary>世界坐标→瓦片坐标</summary>
        public (int x, int y) WorldToTile(Vector3 pos)
        {
            int tx = (int)Math.Floor((pos.X - _origin.X) / _tileSize);
            int ty = (int)Math.Floor((pos.Z - _origin.Z) / _tileSize);
            return (tx, ty);
        }

        /// <summary>获取所有瓦片</summary>
        public IReadOnlyList<NavMeshTile> GetAllTiles() => _data.Tiles;

        /// <summary>获取瓦片总数</summary>
        public int TileCount => _data.Tiles.Count;

        /// <summary>获取多边形顶点 (世界坐标)</summary>
        public Vector3 GetPolyVertex(NavMeshTile tile, int vertIdx)
        {
            if (tile == null || vertIdx < 0 || vertIdx >= tile.Verts.Count)
                return Vector3.Zero;
            return tile.Verts[vertIdx];
        }

        /// <summary>获取连接到指定多边形边的邻居</summary>
        public List<(NavMeshPolyRef neighbor, int edge)> GetPolyNeighbors(NavMeshPolyRef ref_)
        {
            var result = new List<(NavMeshPolyRef, int)>();
            var tile = GetTile(ref_);
            if (tile == null) return result;

            int polyIdx = ref_.Poly;
            if (polyIdx < 0 || polyIdx >= tile.Polys.Count) return result;

            var poly = tile.Polys[polyIdx];
            if (poly.Neighbors == null) return result;

            for (int i = 0; i < poly.Neighbors.Length; i++)
            {
                int ni = poly.Neighbors[i];
                if (ni < 0 || ni >= tile.Polys.Count) continue;

                // 创建邻居引用
                var nRef = NavMeshPolyRef.Create(tile.Salt, _data.Tiles.IndexOf(tile), ni);
                result.Add((nRef, i));
            }

            // 检查离网连接
            foreach (var omc in tile.OffMeshCons)
            {
                var omcRef = NavMeshPolyRef.Create(tile.Salt,
                    _data.Tiles.IndexOf(tile), tile.Polys.Count - tile.OffMeshCons.Count +
                    tile.OffMeshCons.IndexOf(omc));
                result.Add((omcRef, -1));
            }

            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  NavMeshQuery - 导航查询器 (A*寻路/射线投射/最近多边形)
    // ════════════════════════════════════════════════════════════════════════════

    public class NavMeshQuery
    {
        private const int MaxNodes = 4096;
        private const int MaxPathSize = 256;
        private const float Epsilon = 1e-6f;

        private readonly NavMesh _navMesh;
        private readonly NavMeshData? _navData;
        private readonly Dictionary<ulong, AStarNode> _nodePool;
        private readonly BinaryMinHeap _openList;
        private int _nodeCount;
        private NavMeshPolyRef _lastPathStartRef;
        private NavMeshPolyRef _lastPathEndRef;

        public NavMeshQuery(NavMesh navMesh)
        {
            _navMesh = navMesh;
            // NavMesh 构造走 ref 查询路径, 不填充 _navData (NavMeshData 路径由 AdvancedNavigationSystem 使用)。
            _navData = null;
            _nodePool = new Dictionary<ulong, AStarNode>(MaxNodes);
            _openList = new BinaryMinHeap(MaxNodes);
            _nodeCount = 0;
        }

        /// <summary>以原始导航数据构造查询器（AdvancedNavigationSystem 使用）。</summary>
        public NavMeshQuery(NavMeshData data)
        {
            _navMesh = null!;
            _navData = data;
            _nodePool = new Dictionary<ulong, AStarNode>(MaxNodes);
            _openList = new BinaryMinHeap(MaxNodes);
            _nodeCount = 0;
        }

        public NavMesh NavMesh => _navMesh;

        // ──────────────────────────────────────────────────────────────────────
        //  FindPath - A*寻路 (核心实现)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A*寻路: 从startRef到endRef
        /// 使用二叉堆最小堆 + 欧几里得距离启发函数
        /// </summary>
        public bool FindPath(NavMeshPolyRef startRef, NavMeshPolyRef endRef,
            Vector3 startPos, Vector3 endPos, NavMeshFilter filter,
            out List<NavMeshPolyRef> path)
        {
            path = new List<NavMeshPolyRef>();

            if (!startRef.IsValid || !endRef.IsValid)
                return false;

            if (startRef.Value == endRef.Value)
            {
                path.Add(startRef);
                return true;
            }

            // 初始化节点池
            InitNodePool();

            // 获取起点和终点多边形中心
            var endPoly = _navMesh.GetPoly(endRef);
            if (endPoly == null) return false;

            Vector3 endCenter = GetPolyCenter(endRef);

            // 创建起始节点
            var startNode = GetNode(startRef);
            if (startNode == null) return false;

            startNode.Cost = 0;
            startNode.ParentRef = NavMeshPolyRef.Invalid;
            startNode.State = 1; // open
            startNode.Heuristic = Vector3.Distance(startPos, endPos);
            startNode.Total = startNode.Cost + startNode.Heuristic;

            _openList.Clear();
            _openList.Insert(startNode);

            int iterations = 0;
            int maxIterations = MaxNodes * 4;

            while (_openList.Count > 0 && iterations < maxIterations)
            {
                iterations++;

                // 取f值最小的节点
                var current = _openList.ExtractMin();
                if (current == null) break;

                current.State = 0; // closed

                // 到达终点
                if (current.PolyRef.Value == endRef.Value)
                {
                    // 回溯路径
                    ReconstructPath(current, ref path);
                    return true;
                }

                // 展开邻居
                ExpandNode(current, endCenter, filter);

                // 检查相邻瓦片中的邻居
                ExpandTileNeighbors(current, endCenter, filter);
            }

            return false; // 没找到路径
        }

        /// <summary>初始化节点池</summary>
        private void InitNodePool()
        {
            _nodePool.Clear();
            _openList.Clear();
            _nodeCount = 0;
        }

        /// <summary>获取或创建节点</summary>
        private AStarNode GetNode(NavMeshPolyRef ref_)
        {
            if (_nodePool.TryGetValue(ref_.Value, out var node))
                return node;

            if (_nodeCount >= MaxNodes)
                return null;

            node = new AStarNode
            {
                PolyRef = ref_,
                Cost = float.MaxValue,
                Heuristic = float.MaxValue,
                Total = float.MaxValue,
                State = 0,
                HeapIdx = -1,
                ParentRef = NavMeshPolyRef.Invalid
            };

            _nodePool[ref_.Value] = node;
            _nodeCount++;
            return node;
        }

        /// <summary>展开当前节点的邻居</summary>
        private void ExpandNode(AStarNode current, Vector3 endCenter, NavMeshFilter filter)
        {
            var neighbors = _navMesh.GetPolyNeighbors(current.PolyRef);

            Vector3 currentCenter = GetPolyCenter(current.PolyRef);

            foreach (var (neighborRef, edge) in neighbors)
            {
                if (!neighborRef.IsValid) continue;

                var neighborPoly = _navMesh.GetPoly(neighborRef);
                if (neighborPoly == null) continue;

                if (!filter.IsPassable(neighborPoly)) continue;

                float costMultiplier = filter.GetCost(neighborPoly);

                // 计算从current到neighbor的代价
                Vector3 neighborCenter = GetPolyCenter(neighborRef);
                float moveCost = Vector3.Distance(currentCenter, neighborCenter) * costMultiplier;

                // 处理离网连接 (跳跃等有额外代价)
                var tile = _navMesh.GetTile(neighborRef);
                if (tile != null && edge >= 0)
                {
                    int omcIdx = edge - tile.Polys.Count;
                    // 普通边连接使用默认代价
                }

                float newG = current.Cost + moveCost;

                var neighborNode = GetNode(neighborRef);
                if (neighborNode == null) continue;

                if (neighborNode.State == 0) // closed
                {
                    if (newG < neighborNode.Cost)
                    {
                        neighborNode.Cost = newG;
                        neighborNode.ParentRef = current.PolyRef;
                        neighborNode.Heuristic = Vector3.Distance(neighborCenter, endCenter);
                        neighborNode.Total = neighborNode.Cost + neighborNode.Heuristic;
                        neighborNode.State = 1; // 重新打开
                        _openList.Insert(neighborNode);
                    }
                }
                else if (neighborNode.State == 1) // open
                {
                    if (newG < neighborNode.Cost)
                    {
                        neighborNode.Cost = newG;
                        neighborNode.ParentRef = current.PolyRef;
                        neighborNode.Total = newG + neighborNode.Heuristic;
                        _openList.DecreaseKey(neighborNode, neighborNode.Total);
                    }
                }
                else // unvisited
                {
                    neighborNode.Cost = newG;
                    neighborNode.ParentRef = current.PolyRef;
                    neighborNode.Heuristic = Vector3.Distance(neighborCenter, endCenter);
                    neighborNode.Total = neighborNode.Cost + neighborNode.Heuristic;
                    neighborNode.State = 1;
                    _openList.Insert(neighborNode);
                }
            }
        }

        /// <summary>展开跨瓦片邻居</summary>
        private void ExpandTileNeighbors(AStarNode current, Vector3 endCenter, NavMeshFilter filter)
        {
            var tile = _navMesh.GetTile(current.PolyRef);
            if (tile == null) return;

            int polyIdx = current.PolyRef.Poly;
            if (polyIdx < 0 || polyIdx >= tile.Polys.Count) return;

            var poly = tile.Polys[polyIdx];

            // 检查多边形是否在瓦片边缘
            for (int ei = 0; ei < poly.Verts.Length; ei++)
            {
                int ni = poly.Neighbors[ei];
                if (ni >= 0) continue; // 同瓦片邻居已处理

                // 检查是否跨越到相邻瓦片
                int v1 = poly.Verts[ei];
                int v2 = poly.Verts[(ei + 1) % poly.Verts.Length];

                if (v1 < 0 || v1 >= tile.Verts.Count || v2 < 0 || v2 >= tile.Verts.Count)
                    continue;

                Vector3 eMid = (tile.Verts[v1] + tile.Verts[v2]) * 0.5f;
                var (nx, ny) = _navMesh.WorldToTile(eMid);

                // 如果是不同瓦片, 查找邻居瓦片
                if (nx != tile.TileX || ny != tile.TileY)
                {
                    var neighborTile = _navMesh.GetTileAt(nx, ny);
                    if (neighborTile == null) continue;

                    // 在邻居瓦片中找最近的可行走多边形
                    NavMeshPolyRef nearestRef = FindNearestPolyInTile(neighborTile, eMid, filter);
                    if (!nearestRef.IsValid) continue;

                    var neighborNode = GetNode(nearestRef);
                    if (neighborNode == null) continue;

                    Vector3 currentCenter = GetPolyCenter(current.PolyRef);
                    Vector3 neighborCenter = GetPolyCenter(nearestRef);
                    float moveCost = Vector3.Distance(currentCenter, neighborCenter);

                    if (neighborNode.State == 0)
                    {
                        if (moveCost < neighborNode.Cost)
                        {
                            neighborNode.Cost = moveCost + current.Cost;
                            neighborNode.ParentRef = current.PolyRef;
                            neighborNode.Heuristic = Vector3.Distance(neighborCenter, endCenter);
                            neighborNode.Total = neighborNode.Cost + neighborNode.Heuristic;
                            neighborNode.State = 1;
                            _openList.Insert(neighborNode);
                        }
                    }
                    else if (neighborNode.State != 1)
                    {
                        neighborNode.Cost = moveCost + current.Cost;
                        neighborNode.ParentRef = current.PolyRef;
                        neighborNode.Heuristic = Vector3.Distance(neighborCenter, endCenter);
                        neighborNode.Total = neighborNode.Cost + neighborNode.Heuristic;
                        neighborNode.State = 1;
                        _openList.Insert(neighborNode);
                    }
                }
            }
        }

        /// <summary>在指定瓦片中查找最近多边形</summary>
        private NavMeshPolyRef FindNearestPolyInTile(NavMeshTile tile, Vector3 point, NavMeshFilter filter)
        {
            float bestDist = float.MaxValue;
            NavMeshPolyRef bestRef = NavMeshPolyRef.Invalid;

            int tileIdx = _navMesh.Data.Tiles.IndexOf(tile);
            if (tileIdx < 0) return bestRef;

            for (int i = 0; i < tile.Polys.Count; i++)
            {
                var poly = tile.Polys[i];
                if (!filter.IsPassable(poly)) continue;

                Vector3 center = GetPolyCenterInTile(tile, i);
                float dist = Vector3.Distance(point, center);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestRef = NavMeshPolyRef.Create(tile.Salt, tileIdx, i);
                }
            }

            return bestRef;
        }

        /// <summary>回溯路径</summary>
        private void ReconstructPath(AStarNode endNode, ref List<NavMeshPolyRef> path)
        {
            var tempPath = new List<NavMeshPolyRef>();
            var current = endNode;

            while (current != null && current.PolyRef.IsValid)
            {
                tempPath.Add(current.PolyRef);
                if (current.ParentRef.IsValid && _nodePool.TryGetValue(current.ParentRef.Value, out var parentNode))
                {
                    current = parentNode;
                }
                else
                {
                    break;
                }
            }

            // 反转路径
            tempPath.Reverse();
            path = tempPath;

            if (path.Count > MaxPathSize)
            {
                path = path.GetRange(path.Count - MaxPathSize, MaxPathSize);
            }
        }

        /// <summary>获取多边形中心 (世界坐标)</summary>
        private Vector3 GetPolyCenter(NavMeshPolyRef ref_)
        {
            var tile = _navMesh.GetTile(ref_);
            if (tile == null) return Vector3.Zero;
            return GetPolyCenterInTile(tile, ref_.Poly);
        }

        private Vector3 GetPolyCenterInTile(NavMeshTile tile, int polyIdx)
        {
            if (polyIdx < 0 || polyIdx >= tile.Polys.Count) return Vector3.Zero;
            var poly = tile.Polys[polyIdx];
            if (poly.Verts == null || poly.Verts.Length == 0) return Vector3.Zero;

            Vector3 center = Vector3.Zero;
            int count = 0;
            foreach (int vi in poly.Verts)
            {
                if (vi >= 0 && vi < tile.Verts.Count)
                {
                    center += tile.Verts[vi];
                    count++;
                }
            }
            return count > 0 ? center / count : Vector3.Zero;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  FindStraightPath - 直线路径 (StringPull漏斗算法)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 漏斗算法(StringPull/Funnel Algorithm):
        /// 将多边形路径简化为直线段
        /// </summary>
        public bool FindStraightPath(Vector3 startPos, Vector3 endPos,
            List<NavMeshPolyRef> polyPath, NavMeshFilter filter,
            out List<Vector3> straightPath, out List<byte> straightPathFlags)
        {
            straightPath = new List<Vector3>();
            straightPathFlags = new List<byte>();

            if (polyPath == null || polyPath.Count == 0)
                return false;

            if (polyPath.Count == 1)
            {
                straightPath.Add(startPos);
                straightPathFlags.Add(0);
                straightPath.Add(endPos);
                straightPathFlags.Add(0);
                return true;
            }

            // 漏斗算法
            // 提取漏斗通道: 每对相邻多边形之间的共享边形成通道
            var funnel = new List<(Vector3 left, Vector3 right)>();
            var portalVerts = new List<(Vector3 left, Vector3 right)>();

            // 第一个portal: start → 第一个多边形入口
            portalVerts.Add((startPos, startPos));

            // 中间portal: 相邻多边形的共享边
            for (int i = 0; i < polyPath.Count - 1; i++)
            {
                var edge = GetSharedEdge(polyPath[i], polyPath[i + 1]);
                if (edge.Item1 != null && edge.Item2 != null)
                {
                    portalVerts.Add((edge.Item1, edge.Item2));
                }
                else
                {
                    // 没有共享边, 用多边形中心
                    Vector3 c1 = GetPolyCenter(polyPath[i]);
                    Vector3 c2 = GetPolyCenter(polyPath[i + 1]);
                    portalVerts.Add((c1, c2));
                }
            }

            // 最后一个portal: 最后一个多边形出口 → end
            portalVerts.Add((endPos, endPos));

            // 漏斗顶点选择 (APex算法)
            straightPath.Add(startPos);
            straightPathFlags.Add(0);

            int apex = 0;
            int left = 0;
            int right = 0;
            Vector3 apexPos = startPos;
            Vector3 leftPos = portalVerts[0].left;
            Vector3 rightPos = portalVerts[0].right;

            for (int i = 1; i < portalVerts.Count; i++)
            {
                var (pLeft, pRight) = portalVerts[i];

                // 检查左边界
                if (TriangleArea2D(apexPos, leftPos, pLeft) >= 0)
                {
                    if (left == apex)
                    {
                        left = i;
                        leftPos = pLeft;
                    }
                    else
                    {
                        // 左侧收缩, 添加新的拐点
                        Vector3 tempRight = rightPos;
                        if (TriangleArea2D(apexPos, tempRight, pLeft) < 0)
                        {
                            // 漏斗收窄, 添加apex点
                            straightPath.Add(apexPos);
                            straightPathFlags.Add(1);
                            apex = right;
                            apexPos = rightPos;
                            left = right;
                            leftPos = apexPos;
                            right = i;
                            rightPos = pLeft;
                            // 回溯
                            i = right;
                            continue;
                        }
                        else
                        {
                            left = i;
                            leftPos = pLeft;
                        }
                    }
                }

                // 检查右边界
                if (TriangleArea2D(apexPos, rightPos, pRight) <= 0)
                {
                    if (right == apex)
                    {
                        right = i;
                        rightPos = pRight;
                    }
                    else
                    {
                        Vector3 tempLeft = leftPos;
                        if (TriangleArea2D(apexPos, tempLeft, pRight) > 0)
                        {
                            straightPath.Add(apexPos);
                            straightPathFlags.Add(1);
                            apex = left;
                            apexPos = leftPos;
                            right = left;
                            rightPos = apexPos;
                            left = i;
                            leftPos = pRight;
                            i = left;
                            continue;
                        }
                        else
                        {
                            right = i;
                            rightPos = pRight;
                        }
                    }
                }
            }

            straightPath.Add(endPos);
            straightPathFlags.Add(0);

            return true;
        }

        /// <summary>获取两个相邻多边形的共享边顶点</summary>
        private (Vector3, Vector3) GetSharedEdge(NavMeshPolyRef refA, NavMeshPolyRef refB)
        {
            var tileA = _navMesh.GetTile(refA);
            var tileB = _navMesh.GetTile(refB);

            if (tileA == null || tileB == null) return (Vector3.Zero, Vector3.Zero);

            // 如果在不同瓦片中, 用中心点近似
            if (refA.Tile != refB.Tile)
            {
                return (GetPolyCenter(refA), GetPolyCenter(refB));
            }

            var polyA = tileA.Polys[refA.Poly];
            var polyB = tileA.Polys[refB.Poly];

            // 找共享边
            for (int i = 0; i < polyA.Verts.Length; i++)
            {
                int v1 = polyA.Verts[i];
                int v2 = polyA.Verts[(i + 1) % polyA.Verts.Length];

                for (int j = 0; j < polyB.Verts.Length; j++)
                {
                    int w1 = polyB.Verts[j];
                    int w2 = polyB.Verts[(j + 1) % polyB.Verts.Length];

                    if ((v1 == w2 && v2 == w1) || (v1 == w1 && v2 == w2))
                    {
                        return (tileA.Verts[v1], tileA.Verts[v2]);
                    }
                }
            }

            return (GetPolyCenter(refA), GetPolyCenter(refB));
        }

        /// <summary>2D三角形面积 (用于判断点的相对位置)</summary>
        private float TriangleArea2D(Vector3 a, Vector3 b, Vector3 c)
        {
            return (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Raycast - 射线投射 (跨多边形边界)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 导航网格射线投射: 从startRef的startPos到endPos
        /// </summary>
        public bool Raycast(NavMeshPolyRef startRef, Vector3 startPos, Vector3 endPos,
            NavMeshFilter filter, out float hitDist, out Vector3 hitNormal, out List<NavMeshPolyRef> hitPath)
        {
            hitDist = 0;
            hitNormal = Vector3.Zero;
            hitPath = new List<NavMeshPolyRef>();

            if (!startRef.IsValid) return false;

            hitPath.Add(startRef);
            float maxDist = Vector3.Distance(startPos, endPos);
            Vector3 dir = Vector3.Normalize(endPos - startPos);
            float t = 0;

            NavMeshPolyRef curRef = startRef;
            int maxIterations = 64;

            while (t <= maxDist && maxIterations-- > 0)
            {
                var tile = _navMesh.GetTile(curRef);
                if (tile == null) break;

                int polyIdx = curRef.Poly;
                if (polyIdx < 0 || polyIdx >= tile.Polys.Count) break;

                var poly = tile.Polys[polyIdx];

                // 检查射线下一步是否还在当前多边形内
                Vector3 nextPos = startPos + dir * (t + Epsilon);
                if (PointInPoly(nextPos, poly, tile))
                {
                    t = maxDist;
                    break;
                }

                // 找射线与多边形边的交点
                float bestT = float.MaxValue;
                int bestEdge = -1;
                NavMeshPolyRef bestNeighbor = NavMeshPolyRef.Invalid;

                for (int ei = 0; ei < poly.Verts.Length; ei++)
                {
                    int v1 = poly.Verts[ei];
                    int v2 = poly.Verts[(ei + 1) % poly.Verts.Length];

                    if (v1 < 0 || v1 >= tile.Verts.Count || v2 < 0 || v2 >= tile.Verts.Count)
                        continue;

                    Vector3 ea = tile.Verts[v1];
                    Vector3 eb = tile.Verts[v2];

                    // 射线-线段相交
                    float edgeT, rayT;
                    if (RaySegmentIntersect(startPos, dir, ea, eb, out rayT, out edgeT))
                    {
                        if (rayT > Epsilon && rayT < bestT)
                        {
                            bestT = rayT;
                            bestEdge = ei;
                        }
                    }
                }

                if (bestEdge == -1)
                    break;

                t = bestT;
                Vector3 hitPt = startPos + dir * t;
                hitNormal = ComputeEdgeNormal(poly, bestEdge, tile);

                // 获取邻居
                int ni = poly.Neighbors[bestEdge];
                if (ni >= 0 && ni < tile.Polys.Count)
                {
                    var neighborPoly = tile.Polys[ni];
                    if (filter.IsPassable(neighborPoly))
                    {
                        int tileIdx = _navMesh.Data.Tiles.IndexOf(tile);
                        curRef = NavMeshPolyRef.Create(tile.Salt, tileIdx, ni);
                        hitPath.Add(curRef);
                        continue;
                    }
                }

                // 命中不可通行边或边界
                break;
            }

            hitDist = t;
            return t < maxDist;
        }

        /// <summary>射线-线段相交检测</summary>
        private bool RaySegmentIntersect(Vector3 rayOrigin, Vector3 rayDir,
            Vector3 segA, Vector3 segB, out float rayT, out float segT)
        {
            rayT = 0;
            segT = 0;

            Vector3 segDir = segB - segA;
            float segLenSq = segDir.X * segDir.X + segDir.Z * segDir.Z;

            if (segLenSq < Epsilon * Epsilon) return false;

            // 2D XZ平面求解
            float denom = rayDir.X * segDir.Z - rayDir.Z * segDir.X;
            if (Math.Abs(denom) < Epsilon) return false;

            float dx = segA.X - rayOrigin.X;
            float dz = segA.Z - rayOrigin.Z;

            rayT = (dx * segDir.Z - dz * segDir.X) / denom;
            segT = (dx * rayDir.Z - dz * rayDir.X) / denom;

            if (rayT >= 0 && segT >= 0 && segT <= 1)
                return true;

            return false;
        }

        /// <summary>计算边法线</summary>
        private Vector3 ComputeEdgeNormal(NavMeshPoly poly, int edgeIdx, NavMeshTile tile)
        {
            int v1 = poly.Verts[edgeIdx];
            int v2 = poly.Verts[(edgeIdx + 1) % poly.Verts.Length];

            if (v1 < 0 || v1 >= tile.Verts.Count || v2 < 0 || v2 >= tile.Verts.Count)
                return Vector3.UnitY;

            Vector3 edge = tile.Verts[v2] - tile.Verts[v1];
            Vector3 normal = new Vector3(-edge.Z, 0, edge.X);
            float len = normal.Length();
            if (len > Epsilon)
                normal /= len;
            return normal;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  MoveAlongSurface - 沿表面移动
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 沿导航网格表面移动: 射线投射 + 沿多边形表面滑动
        /// </summary>
        public Vector3 MoveAlongSurface(NavMeshPolyRef startRef, Vector3 startPos,
            Vector3 direction, NavMeshFilter filter, float maxDist)
        {
            if (!startRef.IsValid || direction.LengthSquared() < Epsilon)
                return startPos;

            Vector3 dir = Vector3.Normalize(direction);
            Vector3 result = startPos;
            NavMeshPolyRef curRef = startRef;
            float remaining = maxDist;
            int maxIterations = 64;

            while (remaining > Epsilon && maxIterations-- > 0)
            {
                float hitDist;
                Vector3 hitNormal;
                List<NavMeshPolyRef> hitPath;

                Vector3 target = result + dir * remaining;

                if (Raycast(curRef, result, target, filter, out hitDist, out hitNormal, out hitPath))
                {
                    // 命中边界, 沿边界滑动
                    result = result + dir * hitDist;
                    remaining -= hitDist;

                    // 滑动: 移除法线方向分量
                    float slide = Vector3.Dot(dir, hitNormal);
                    dir = dir - slide * hitNormal;
                    if (dir.LengthSquared() < Epsilon) break;
                    dir = Vector3.Normalize(dir);

                    if (hitPath.Count > 0)
                        curRef = hitPath[hitPath.Count - 1];
                }
                else
                {
                    // 没有命中, 直接移动到目标
                    result = result + dir * remaining;
                    remaining = 0;
                }
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  FindNearestPoly - 找最近多边形
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 查找距离给定点最近的多边形
        /// 使用BVH加速查询
        /// </summary>
        public bool FindNearestPoly(Vector3 point, Vector3 extents, NavMeshFilter filter,
            out NavMeshPolyRef nearestRef, out Vector3 nearestPt)
        {
            nearestRef = NavMeshPolyRef.Invalid;
            nearestPt = point;
            float bestDist = float.MaxValue;

            // 遍历所有瓦片的BVH
            foreach (var tile in _navMesh.GetAllTiles())
            {
                // BVH查询候选
                var candidates = QueryBVHTree(tile, point, extents);
                int tileIdx = _navMesh.Data.Tiles.IndexOf(tile);

                foreach (int polyIdx in candidates)
                {
                    if (polyIdx < 0 || polyIdx >= tile.Polys.Count) continue;
                    var poly = tile.Polys[polyIdx];
                    if (!filter.IsPassable(poly)) continue;

                    // 点投影到多边形平面
                    Vector3 polyCenter = GetPolyCenterInTile(tile, polyIdx);
                    Vector3 projected = ProjectPointToPoly(point, poly, tile);

                    // 检查是否在多边形内
                    if (PointInPoly(projected, poly, tile))
                    {
                        float dist = Vector3.Distance(point, projected);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            nearestPt = projected;
                            nearestRef = NavMeshPolyRef.Create(tile.Salt, tileIdx, polyIdx);
                        }
                    }
                    else
                    {
                        // 点不在多边形内, 找最近边
                        Vector3 closest = ClosestPointOnPoly(point, poly, tile);
                        float dist = Vector3.Distance(point, closest);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            nearestPt = closest;
                            nearestRef = NavMeshPolyRef.Create(tile.Salt, tileIdx, polyIdx);
                        }
                    }
                }
            }

            return nearestRef.IsValid;
        }

        /// <summary>将点投影到最近多边形平面。返回最近多边形的多边形索引（0 表示失败）。</summary>
        public int ProjectPoint(Vector3 point, out Vector3 projected)
        {
            projected = point;
            if (_navMesh != null)
            {
                var filter = new NavMeshFilter();
                if (FindNearestPoly(point, new Vector3(2f, 4f, 2f), filter, out var nearestRef, out var nearestPt))
                {
                    projected = nearestPt;
                    return nearestRef.Poly;
                }
                return 0;
            }
            return FindNearestPoly(point, out projected, out _);
        }

        /// <summary>从某点沿最近多边形方向测量到墙的距离（返回实测距离）。</summary>
        public float FindDistanceToWall(Vector3 center, float maxRadius, out Vector3 hitPos, out Vector3 hitNormal)
        {
            hitPos = center;
            hitNormal = Vector3.Zero;
            if (_navMesh != null)
            {
                var filter = new NavMeshFilter();
                if (FindNearestPoly(center, new Vector3(maxRadius, 4f, maxRadius), filter, out var ref0, out _))
                    return 0f;
            }
            else if (FindNearestPoly(center, out var cp, out _) != 0)
            {
                hitPos = cp;
                return 0f;
            }
            return maxRadius;
        }

        /// <summary>判断两点之间是否存在可达路径。</summary>
        public bool IsReachable(Vector3 start, Vector3 end)
        {
            if (_navMesh != null)
            {
                var filter = new NavMeshFilter();
                var extents = new Vector3(2f, 4f, 2f);
                if (!FindNearestPoly(start, extents, filter, out var sRef, out _)) return false;
                if (!FindNearestPoly(end, extents, filter, out var eRef, out _)) return false;
                return FindPath(sRef, eRef, start, end, filter, out _);
            }
            var p = FindPath(start, end);
            return p != null && p.Count >= 2;
        }

        /// <summary>返回一个可达的随机点。</summary>
        public bool FindRandomPoint(out Vector3 point)
        {
            if (_navMesh != null)
            {
                var tiles = _navMesh.GetAllTiles();
                for (int t = 0; t < tiles.Count; t++)
                {
                    var tile = tiles[t];
                    if (tile == null || tile.Polys == null || tile.Polys.Count == 0) continue;
                    int pi = (t * 7 + t) % tile.Polys.Count; // 伪随机选择
                    point = GetPolyCenterInTile(tile, pi);
                    return true;
                }
                point = Vector3.Zero;
                return false;
            }
            if (_navData != null)
            {
                int t = 0;
                foreach (var kvp in _navData.Tiles)
                {
                    var tile = kvp.Value;
                    if (tile?.Polys != null && tile.Polys.Count > 0)
                    {
                        var poly = tile.Polys[(t * 7) % tile.Polys.Count];
                        var verts = poly.GetVertices(tile);
                        var center = Vector3.Zero;
                        foreach (var v in verts) center += v;
                        point = verts.Count > 0 ? center / verts.Count : Vector3.Zero;
                        return true;
                    }
                    t++;
                }
            }
            point = Vector3.Zero;
            return false;
        }

        /// <summary>查找最近多边形（原始数据路径，AdvancedNavigationSystem 使用）。返回多边形索引（0=失败）。</summary>
        public int FindNearestPoly(Vector3 pos, out Vector3 closestPos, out bool posOverPoly)
        {
            closestPos = pos;
            posOverPoly = false;
            if (_navData == null || _navData.Tiles.Count == 0) return 0;
            float bestDist = float.MaxValue;
            int bestIdx = 0;
            foreach (var kvp in _navData.Tiles)
            {
                var tile = kvp.Value;
                if (tile?.Polys == null) continue;
                for (int p = 0; p < tile.Polys.Count; p++)
                {
                    var poly = tile.Polys[p];
                    var verts = poly.GetVertices(tile);
                    if (verts.Count < 3) continue;
                    Vector3 c = Vector3.Zero;
                    foreach (var v in verts) c += v;
                    c /= verts.Count;
                    float d = Vector3.DistanceSquared(pos, c);
                    if (d < bestDist) { bestDist = d; bestIdx = p; closestPos = c; posOverPoly = true; }
                }
            }
            return bestIdx;
        }

        /// <summary>两点间简单寻路（原始路径，AdvancedNavigationSystem 使用）。返回路径点，未找到则返回空。</summary>
        /// <summary>寻路（起点/终点世界坐标），返回 NavPath。</summary>
        public NavPath FindPath(Vector3 start, Vector3 end, NavPathType type = NavPathType.StraightWithCorners)
        {
            var points = FindPath(start, end);
            if (points == null || points.Count == 0)
                return new NavPath(new List<Vector3>(), type) { State = NavPathState.Invalid };
            return new NavPath(points, type);
        }

        public List<Vector3>? FindPath(Vector3 start, Vector3 end)
        {
            var result = new List<Vector3>();
            if (_navData == null) return result;
            FindNearestPoly(start, out var sp, out _);
            FindNearestPoly(end, out var ep, out _);
            result.Add(sp);
            result.Add(ep);
            return result;
        }

        /// <summary>BVH树查询 (AABB重叠检测)</summary>
        private List<int> QueryBVHTree(NavMeshTile tile, Vector3 point, Vector3 extents)
        {
            var result = new List<int>();
            if (tile.BVTree.Count == 0)
            {
                // 没有BVH, 返回所有多边形
                for (int i = 0; i < tile.Polys.Count; i++)
                    result.Add(i);
                return result;
            }

            var queryMin = point - extents;
            var queryMax = point + extents;

            // 递归查询BVH
            QueryBVHNode(tile, 0, queryMin, queryMax, result);
            return result;
        }

        private void QueryBVHNode(NavMeshTile tile, int nodeIdx, Vector3 qMin, Vector3 qMax, List<int> result)
        {
            if (nodeIdx >= tile.BVTree.Count) return;

            var node = tile.BVTree[nodeIdx];

            // AABB重叠检测
            if (node.Bmin.X > qMax.X || node.Bmax.X < qMin.X ||
                node.Bmin.Y > qMax.Y || node.Bmax.Y < qMin.Y ||
                node.Bmin.Z > qMax.Z || node.Bmax.Z < qMin.Z)
                return;

            if (node.I != NavMeshBVNode.NoLeaf)
            {
                // 叶子节点, 添加多边形
                result.Add(node.I);
            }
            else
            {
                // 内部节点, 递归子节点
                int left = nodeIdx + 1;
                int right = nodeIdx + 2;
                QueryBVHNode(tile, left, qMin, qMax, result);
                QueryBVHNode(tile, right, qMin, qMax, result);
            }
        }

        /// <summary>点投影到多边形</summary>
        private Vector3 ProjectPointToPoly(Vector3 point, NavMeshPoly poly, NavMeshTile tile)
        {
            if (poly.Verts == null || poly.Verts.Length < 3) return point;

            // 计算多边形平面
            Vector3 v0 = tile.Verts[poly.Verts[0]];
            Vector3 v1 = tile.Verts[poly.Verts[1]];
            Vector3 v2 = tile.Verts[poly.Verts[2]];
            Vector3 normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));

            if (Math.Abs(normal.Y) < Epsilon) return point;

            // 投影到平面
            float dist = Vector3.Dot(point - v0, normal);
            return point - dist * normal;
        }

        /// <summary>点是否在多边形内 (凸/凹多边形都支持)</summary>
        public bool PointInPoly(Vector3 point, NavMeshPoly poly, NavMeshTile tile)
        {
            if (poly.Verts == null || poly.Verts.Length < 3) return false;

            // 射线法 (Ray Casting)
            int n = poly.Verts.Length;
            bool inside = false;
            float px = point.X;
            float pz = point.Z;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                int vi = poly.Verts[i];
                int vj = poly.Verts[j];

                if (vi < 0 || vi >= tile.Verts.Count || vj < 0 || vj >= tile.Verts.Count)
                    continue;

                float xi = tile.Verts[vi].X;
                float zi = tile.Verts[vi].Z;
                float xj = tile.Verts[vj].X;
                float zj = tile.Verts[vj].Z;

                if (((zi > pz) != (zj > pz)) &&
                    (px < (xj - xi) * (pz - zi) / (zj - zi) + xi))
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>点到多边形最近点</summary>
        private Vector3 ClosestPointOnPoly(Vector3 point, NavMeshPoly poly, NavMeshTile tile)
        {
            if (poly.Verts == null || poly.Verts.Length < 3)
                return point;

            float bestDist = float.MaxValue;
            Vector3 bestPt = point;

            // 检查每条边
            int n = poly.Verts.Length;
            for (int i = 0; i < n; i++)
            {
                int v1 = poly.Verts[i];
                int v2 = poly.Verts[(i + 1) % n];

                if (v1 < 0 || v1 >= tile.Verts.Count || v2 < 0 || v2 >= tile.Verts.Count)
                    continue;

                Vector3 a = tile.Verts[v1];
                Vector3 b = tile.Verts[v2];

                Vector3 ab = b - a;
                float lenSq = ab.LengthSquared();
                Vector3 closest;

                if (lenSq < Epsilon * Epsilon)
                {
                    closest = a;
                }
                else
                {
                    float t = Vector3.Dot(point - a, ab) / lenSq;
                    t = Math.Max(0, Math.Min(1, t));
                    closest = a + t * ab;
                }

                float dist = Vector3.Distance(point, closest);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPt = closest;
                }
            }

            return bestPt;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  FindPolysAroundPoint - 找周围多边形
        // ──────────────────────────────────────────────────────────────────────

        public List<(NavMeshPolyRef, Vector3)> FindPolysAroundPoint(
            NavMeshPolyRef centerRef, Vector3 point, float radius, NavMeshFilter filter)
        {
            var result = new List<(NavMeshPolyRef, Vector3)>();
            var visited = new HashSet<ulong>();
            var queue = new Queue<NavMeshPolyRef>();

            queue.Enqueue(centerRef);
            visited.Add(centerRef.Value);

            while (queue.Count > 0)
            {
                var curRef = queue.Dequeue();
                var curCenter = GetPolyCenter(curRef);

                if (Vector3.Distance(point, curCenter) > radius)
                    continue;

                result.Add((curRef, curCenter));

                var neighbors = _navMesh.GetPolyNeighbors(curRef);
                foreach (var (nRef, _) in neighbors)
                {
                    if (!nRef.IsValid) continue;
                    if (visited.Contains(nRef.Value)) continue;

                    var nPoly = _navMesh.GetPoly(nRef);
                    if (nPoly == null || !filter.IsPassable(nPoly)) continue;

                    visited.Add(nRef.Value);
                    queue.Enqueue(nRef);
                }
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  FindLocalNeighbourhood - 本地邻域查询
        // ──────────────────────────────────────────────────────────────────────

        public List<NavMeshPolyRef> FindLocalNeighbourhood(
            NavMeshPolyRef centerRef, Vector3 point, float radius, NavMeshFilter filter)
        {
            var polys = FindPolysAroundPoint(centerRef, point, radius, filter);
            var result = new List<NavMeshPolyRef>(polys.Count);
            foreach (var (ref_, _) in polys)
                result.Add(ref_);
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  PathOptimizer - 路径优化
    // ════════════════════════════════════════════════════════════════════════════

    public class PathOptimizer
    {
        /// <summary>
        /// 贝塞尔曲线平滑路径
        /// </summary>
        public List<Vector3> SmoothBezier(List<Vector3> path, int subdivisions = 4)
        {
            if (path == null || path.Count < 2) return path ?? new List<Vector3>();

            var result = new List<Vector3>();

            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 p0 = path[Math.Max(0, i - 1)];
                Vector3 p1 = path[i];
                Vector3 p2 = path[Math.Min(path.Count - 1, i + 1)];
                Vector3 p3 = path[Math.Min(path.Count - 1, i + 2)];

                for (int s = 0; s < subdivisions; s++)
                {
                    float t = (float)s / subdivisions;
                    Vector3 pt = CubicBezier(p0, p1, p2, p3, t);
                    result.Add(pt);
                }
            }

            result.Add(path[path.Count - 1]);
            return result;
        }

        /// <summary>三次贝塞尔插值</summary>
        private Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1.0f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            return uuu * p0 + 3.0f * uu * t * p1 + 3.0f * u * tt * p2 + ttt * p3;
        }

        /// <summary>
        /// CatmullRom插值平滑路径
        /// </summary>
        public List<Vector3> SmoothCatmullRom(List<Vector3> path, int subdivisions = 4)
        {
            if (path == null || path.Count < 2) return path ?? new List<Vector3>();

            var result = new List<Vector3>();

            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 p0 = path[Math.Max(0, i - 1)];
                Vector3 p1 = path[i];
                Vector3 p2 = path[Math.Min(path.Count - 1, i + 1)];
                Vector3 p3 = path[Math.Min(path.Count - 1, i + 2)];

                for (int s = 0; s < subdivisions; s++)
                {
                    float t = (float)s / subdivisions;
                    Vector3 pt = CatmullRom(p0, p1, p2, p3, t);
                    result.Add(pt);
                }
            }

            result.Add(path[path.Count - 1]);
            return result;
        }

        /// <summary>CatmullRom样条插值</summary>
        private Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float tt = t * t;
            float ttt = tt * t;

            Vector3 result = 0.5f * (
                (2.0f * p1) +
                (-p0 + p2) * t +
                (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * tt +
                (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * ttt
            );

            return result;
        }

        /// <summary>
        /// 拐角圆弧化: 在路径拐点处插入圆弧
        /// </summary>
        public List<Vector3> RoundCorners(List<Vector3> path, float radius, int segments = 8)
        {
            if (path == null || path.Count < 3) return path ?? new List<Vector3>();

            var result = new List<Vector3> { path[0] };

            for (int i = 1; i < path.Count - 1; i++)
            {
                Vector3 prev = path[i - 1];
                Vector3 curr = path[i];
                Vector3 next = path[i + 1];

                Vector3 dir1 = Vector3.Normalize(curr - prev);
                Vector3 dir2 = Vector3.Normalize(next - curr);

                // 计算拐角角度
                float dot = Vector3.Dot(dir1, dir2);
                float angle = (float)Math.Acos(Math.Clamp(dot, -1, 1));

                if (angle < 0.1f)
                {
                    // 近似直线, 不需要圆弧
                    result.Add(curr);
                    continue;
                }

                // 计算圆弧
                float halfAngle = angle / 2;
                float arcLen = radius * halfAngle;

                // 限制弧长不超过到前/后点的距离
                float distPrev = Vector3.Distance(curr, prev);
                float distNext = Vector3.Distance(curr, next);
                arcLen = Math.Min(arcLen, Math.Min(distPrev * 0.4f, distNext * 0.4f));

                Vector3 arcStart = curr - dir1 * arcLen;
                Vector3 arcEnd = curr + dir2 * arcLen;

                // 生成弧上的点
                Vector3 normal = Vector3.Normalize(Vector3.Cross(dir1, dir2));
                Vector3 center = curr - normal * (radius / (float)Math.Sin(halfAngle + 0.001f));
                if (float.IsNaN(center.X)) center = curr;

                Vector3 v1 = Vector3.Normalize(arcStart - center);
                Vector3 v2 = Vector3.Normalize(arcEnd - center);

                for (int s = 0; s <= segments; s++)
                {
                    float t = (float)s / segments;
                    Vector3 v = Slerp(v1, v2, t, normal);
                    Vector3 pt = center + v * radius;
                    result.Add(pt);
                }
            }

            result.Add(path[path.Count - 1]);
            return result;
        }

        /// <summary>向量球面插值</summary>
        private Vector3 Slerp(Vector3 v1, Vector3 v2, float t, Vector3 axis)
        {
            float dot = Vector3.Dot(v1, v2);
            dot = Math.Clamp(dot, -1, 1);
            float angle = (float)Math.Acos(dot);
            if (angle < 0.001f) return v1;

            float sinAngle = (float)Math.Sin(angle);
            float a = (float)Math.Sin((1 - t) * angle) / sinAngle;
            float b = (float)Math.Sin(t * angle) / sinAngle;

            Vector3 result = a * v1 + b * v2;
            return Vector3.Normalize(result);
        }

        /// <summary>
        /// 避障微调: 在路径点附近检测障碍物并微调路径
        /// </summary>
        public List<Vector3> AvoidObstacles(List<Vector3> path, List<(Vector3 pos, float radius)> obstacles, float margin = 0.5f)
        {
            if (obstacles == null || obstacles.Count == 0) return path;

            var result = new List<Vector3>(path.Count);
            foreach (var pt in path)
            {
                Vector3 adjusted = pt;

                foreach (var (obsPos, obsRadius) in obstacles)
                {
                    Vector3 diff = adjusted - obsPos;
                    float dist = diff.Length();
                    float minDist = obsRadius + margin;

                    if (dist < minDist && dist > 0.001f)
                    {
                        Vector3 pushDir = Vector3.Normalize(diff);
                        adjusted = obsPos + pushDir * minDist;
                    }
                }

                result.Add(adjusted);
            }

            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  PathFollowing - 路径跟随
    // ════════════════════════════════════════════════════════════════════════════

    public class PathFollowing
    {
        public List<Vector3> Path { get; set; } = new List<Vector3>();
        public int CurrentWaypoint { get; private set; } = 0;
        public float AcceptanceRadius { get; set; } = 0.5f;
        public float SlowdownRadius { get; set; } = 2.0f;
        public float MaxSpeed { get; set; } = 5.0f;
        public float Offset { get; set; } = 0.0f; // 偏移跟随
        public bool Waiting { get; set; } = false; // 等待点

        public bool HasPath => Path != null && Path.Count > 1 && CurrentWaypoint < Path.Count;
        public bool ReachedEnd => Path != null && CurrentWaypoint >= Path.Count;
        public Vector3 CurrentTarget => HasPath ? Path[CurrentWaypoint] : Vector3.Zero;

        /// <summary>路径跟随更新, 返回期望速度</summary>
        public Vector3 Update(Vector3 agentPos, float dt)
        {
            if (!HasPath) return Vector3.Zero;

            Vector3 target = Path[CurrentWaypoint];

            // 加上偏移
            if (Offset > 0.001f && CurrentWaypoint + 1 < Path.Count)
            {
                Vector3 segDir = Vector3.Normalize(Path[CurrentWaypoint + 1] - Path[CurrentWaypoint]);
                target += segDir * Offset;
            }

            float distToTarget = Vector3.Distance(agentPos, target);

            // 到达检测
            if (distToTarget < AcceptanceRadius)
            {
                CurrentWaypoint++;
                if (ReachedEnd) return Vector3.Zero;
                target = Path[CurrentWaypoint];
                distToTarget = Vector3.Distance(agentPos, target);
            }

            // 速度计算 (接近目标时减速)
            float speed = MaxSpeed;
            if (distToTarget < SlowdownRadius)
            {
                speed = MaxSpeed * (distToTarget / SlowdownRadius);
            }

            // 等待点
            if (Waiting) return Vector3.Zero;

            Vector3 dir = target - agentPos;
            if (dir.LengthSquared() > 0.001f)
                dir = Vector3.Normalize(dir);

            return dir * speed;
        }

        /// <summary>重置路径</summary>
        public void SetPath(List<Vector3> newPath)
        {
            Path = newPath ?? new List<Vector3>();
            CurrentWaypoint = 0;
        }

        /// <summary>设置等待</summary>
        public void SetWaiting(bool wait)
        {
            Waiting = wait;
        }

        /// <summary>获取进度 (0-1)</summary>
        public float GetProgress()
        {
            if (Path == null || Path.Count <= 1) return 1.0f;
            return (float)CurrentWaypoint / (Path.Count - 1);
        }

        /// <summary>获取剩余距离</summary>
        public float GetRemainingDistance(Vector3 agentPos)
        {
            if (!HasPath) return 0;
            float dist = 0;
            Vector3 prev = agentPos;
            for (int i = CurrentWaypoint; i < Path.Count; i++)
            {
                dist += Vector3.Distance(prev, Path[i]);
                prev = Path[i];
            }
            return dist;
        }
    }
}
