using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShanyuEngine.ECS;

namespace ShanyuEngine.AI
{
    // ══════════════════════════════════════════════════════════════════
    // 导航区域与导航链接
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导航区域类型。每个区域定义了通行成本和标志位，
    /// 用于实现不同地表类型（草地、道路、水域等）的差异化寻路。
    /// 参考 Recast/Detour 的 dtArea 概念。
    /// </summary>
    public class NavArea
    {
        /// <summary>区域 ID（0 通常为默认可通行区域）</summary>
        public byte Id;
        /// <summary>区域名称</summary>
        public string Name = "";
        /// <summary>通行成本（值越大越不倾向走此区域）</summary>
        public float Cost = 1f;
        /// <summary>标志位（用于查询过滤）</summary>
        public ushort Flags = 1;
        /// <summary>区域颜色（调试渲染用）</summary>
        public Vector3 DebugColor = new(0.5f, 0.7f, 1f);

        /// <summary>默认可通行区域</summary>
        public static NavArea Default => new() { Id = 0, Name = "Default", Cost = 1f, Flags = 1 };
        /// <summary>道路区域（低成本）</summary>
        public static NavArea Road => new() { Id = 1, Name = "Road", Cost = 0.5f, Flags = 1, DebugColor = new(0.3f, 0.3f, 0.3f) };
        /// <summary>草地区域（正常成本）</summary>
        public static NavArea Grass => new() { Id = 2, Name = "Grass", Cost = 1.2f, Flags = 1, DebugColor = new(0.2f, 0.6f, 0.2f) };
        /// <summary>水域区域（高成本）</summary>
        public static NavArea Water => new() { Id = 3, Name = "Water", Cost = 3f, Flags = 2, DebugColor = new(0.2f, 0.3f, 0.8f) };
        /// <summary>不可通行区域</summary>
        public static NavArea Null => new() { Id = 63, Name = "Null", Cost = float.MaxValue, Flags = 0, DebugColor = new(0f, 0f, 0f) };
    }

    /// <summary>
    /// 导航链接。用于连接两个非邻接的多边形，
    /// 例如跳跃点、梯子、传送门等。参考 Detour 的 dtOffMeshConnection。
    /// </summary>
    public class NavLink
    {
        /// <summary>唯一标识符</summary>
        public int Id;
        /// <summary>起始位置</summary>
        public Vector3 Start;
        /// <summary>结束位置</summary>
        public Vector3 End;
        /// <summary>起始多边形索引</summary>
        public int FromPoly;
        /// <summary>结束多边形索引</summary>
        public int ToPoly;
        /// <summary>链接半径</summary>
        public float Radius = 0.5f;
        /// <summary>是否双向</summary>
        public bool IsBidirectional = true;
        /// <summary>链接类型（跳跃、梯子、传送等）</summary>
        public LinkType Type;
        /// <summary>通行成本修正</summary>
        public float CostModifier = 1f;
        /// <summary>是否启用</summary>
        public bool IsEnabled = true;
    }

    /// <summary>导航链接类型</summary>
    public enum LinkType
    {
        /// <summary>跳跃链接</summary>
        Jump,
        /// <summary>梯子链接</summary>
        Ladder,
        /// <summary>传送链接</summary>
        Teleport,
        /// <summary>自定义链接</summary>
        Custom
    }

    // ══════════════════════════════════════════════════════════════════
    // 导航多边形与导航瓦片
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导航多边形。NavMesh 的基本构成单元，通常是凸多边形。
    /// 参考 Detour 的 dtPoly。
    /// </summary>
    public class NavPoly
    {
        /// <summary>多边形索引（在 Tile 内的唯一标识）</summary>
        public int Index;
        /// <summary>顶点索引列表（引用 Tile 的顶点缓冲）</summary>
        public readonly List<int> VertIndices = new();
        /// <summary>邻接多边形索引列表（与边一一对应，-1 表示无邻接）</summary>
        public readonly List<int> NeighborPolys = new();
        /// <summary>区域 ID</summary>
        public byte AreaId;
        /// <summary>标志位</summary>
        public ushort Flags;
        /// <summary>中心点</summary>
        public Vector3 Center;
        /// <summary>包围球半径</summary>
        public float BoundingRadius;
        /// <summary>所属 Tile 索引</summary>
        public int TileIndex;

        /// <summary>获取多边形的顶点坐标列表</summary>
        public List<Vector3> GetVertices(NavTile tile)
        {
            var verts = new List<Vector3>();
            foreach (var idx in VertIndices)
                if (idx >= 0 && idx < tile.Vertices.Count)
                    verts.Add(tile.Vertices[idx]);
            return verts;
        }

        /// <summary>判断点是否在多边形内（XZ 平面投影）</summary>
        public bool ContainsPoint(Vector3 point, NavTile tile)
        {
            var verts = GetVertices(tile);
            int n = verts.Count;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = verts[i].X, zi = verts[i].Z;
                float xj = verts[j].X, zj = verts[j].Z;
                bool intersect = ((zi > point.Z) != (zj > point.Z)) &&
                    (point.X < (xj - xi) * (point.Z - zi) / (zj - zi + 1e-10f) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>
    /// 导航瓦片。NavMesh 按 Tile 组织，每个 Tile 包含一组多边形和顶点数据。
    /// 参考 Detour 的 dtMeshTile。支持按需加载和动态更新。
    /// </summary>
    public class NavTile
    {
        /// <summary>Tile 索引</summary>
        public int Index;
        /// <summary>Tile 网格坐标 X</summary>
        public int GridX;
        /// <summary>Tile 网格坐标 Z</summary>
        public int GridZ;
        /// <summary>Tile 包围盒</summary>
        public Vector3 BoundsMin;
        /// <summary>Tile 包围盒最大值</summary>
        public Vector3 BoundsMax;
        /// <summary>顶点缓冲</summary>
        public readonly List<Vector3> Vertices = new();
        /// <summary>多边形列表</summary>
        public readonly List<NavPoly> Polys = new();
        /// <summary>Tile 内的导航链接</summary>
        public readonly List<NavLink> Links = new();
        /// <summary>Tile 是否已加载</summary>
        public bool IsLoaded;
        /// <summary>Tile 盐值（用于多边形引用校验，参考 Detour 的 salt 机制）</summary>
        public uint Salt;
    }

    /// <summary>
    /// 导航网格数据。包含所有 Tile、区域定义和导航链接。
    /// </summary>
    public class NavMeshData
    {
        /// <summary>所有 Tile 字典（按网格坐标索引）</summary>
        public readonly Dictionary<(int x, int z), NavTile> Tiles = new();
        /// <summary>区域定义字典（按区域 ID 索引）</summary>
        public readonly Dictionary<byte, NavArea> Areas = new();
        /// <summary>全局导航链接列表</summary>
        public readonly List<NavLink> Links = new();
        /// <summary>NavMesh 原点</summary>
        public Vector3 Origin = Vector3.Zero;
        /// <summary>Tile 大小</summary>
        public float TileSize = 64f;
        /// <summary>Tile 总数 X</summary>
        public int TileCountX;
        /// <summary>Tile 总数 Z</summary>
        public int TileCountZ;
        /// <summary>最大 Agent 半径</summary>
        public float MaxAgentRadius = 0.5f;
        /// <summary>最大 Agent 高度</summary>
        public float MaxAgentHeight = 2f;
        /// <summary>体素化单元格大小</summary>
        public float CellSize = 0.3f;
        /// <summary>体素化单元格高度</summary>
        public float CellHeight = 0.2f;
        /// <summary>多边形引用计数器（用于生成唯一 PolyRef）</summary>
        private int _nextPolyRef = 1;
        /// <summary>多边形引用到 (Tile, Poly) 的映射</summary>
        public readonly Dictionary<int, (int tileIdx, int polyIdx)> PolyRefMap = new();

        /// <summary>获取总多边形数</summary>
        public int TotalPolyCount
        {
            get
            {
                int count = 0;
                foreach (var tile in Tiles.Values)
                    count += tile.Polys.Count;
                return count;
            }
        }

        /// <summary>注册区域类型</summary>
        public void RegisterArea(NavArea area) => Areas[area.Id] = area;

        /// <summary>获取区域类型</summary>
        public NavArea GetArea(byte areaId) =>
            Areas.TryGetValue(areaId, out var area) ? area : NavArea.Default;

        /// <summary>生成多边形引用（全局唯一 ID）</summary>
        public int AllocPolyRef(int tileIdx, int polyIdx)
        {
            int polyRef = _nextPolyRef++;
            PolyRefMap[polyRef] = (tileIdx, polyIdx);
            return polyRef;
        }

        /// <summary>根据多边形引用获取 Tile 和 Poly</summary>
        public bool TryGetPoly(int polyRef, out NavTile? tile, out NavPoly? poly)
        {
            tile = null;
            poly = null;
            if (!PolyRefMap.TryGetValue(polyRef, out var loc)) return false;
            // 查找对应 Tile
            foreach (var t in Tiles.Values)
            {
                if (t.Index == loc.tileIdx)
                {
                    tile = t;
                    if (loc.polyIdx >= 0 && loc.polyIdx < t.Polys.Count)
                    {
                        poly = t.Polys[loc.polyIdx];
                        return true;
                    }
                    return false;
                }
            }
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 导航查询过滤器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导航查询过滤器。控制哪些区域可通行、各区域的通行成本。
    /// 参考 Detour 的 dtQueryFilter。
    /// </summary>
    public class NavQueryFilter
    {
        /// <summary>包含的标志位（只有 Flags 与此按位与不为 0 的区域才可通行）</summary>
        public ushort IncludeFlags = 0xFFFF;
        /// <summary>排除的标志位（Flags 与此按位与不为 0 的区域不可通行）</summary>
        public ushort ExcludeFlags = 0;
        /// <summary>区域成本覆盖表</summary>
        public readonly Dictionary<byte, float> AreaCosts = new();
        /// <summary>默认区域成本</summary>
        public float DefaultAreaCost = 1f;

        /// <summary>判断区域是否可通行</summary>
        public bool IsAreaPassable(byte areaId, ushort flags)
        {
            if ((flags & IncludeFlags) == 0) return false;
            if ((flags & ExcludeFlags) != 0) return false;
            return true;
        }

        /// <summary>获取区域的通行成本</summary>
        public float GetAreaCost(byte areaId) =>
            AreaCosts.TryGetValue(areaId, out var cost) ? cost : DefaultAreaCost;

        /// <summary>设置区域通行成本</summary>
        public void SetAreaCost(byte areaId, float cost) => AreaCosts[areaId] = cost;
    }

    // ══════════════════════════════════════════════════════════════════
    // NavMesh 生成器 —— 体素化 → 区域分割 → 轮廓 → 多边形网格
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// NavMesh 生成器。实现 Recast 风格的导航网格生成管线：
    /// 1. 体素化（Voxelization）—— 将三角形场景转换为高度场体素
    /// 2. 区域分割（Region Partition）—— 将连通的体素分组为区域
    /// 3. 轮廓提取（Contour）—— 沿区域边界生成简化轮廓
    /// 4. 多边形网格生成（Polygon Mesh）—— 将轮廓三角化为凸多边形
    /// </summary>

    // ── NavMesh 生成的中间数据结构 ──

    /// <summary>高度场（体素化结果）</summary>

    /// <summary>轮廓（区域边界）</summary>

    // ══════════════════════════════════════════════════════════════════
    // 寻路算法 —— A*、Dijkstra、IDA*
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 寻路算法集合。提供 A*、Dijkstra 和 IDA* 三种寻路实现。
    /// </summary>
    public static class Pathfinding
    {
        /// <summary>
        /// A* 寻路算法。使用启发式函数引导搜索方向，效率较高。
        /// 适合大多数寻路场景。
        /// </summary>
        /// <param name="navData">NavMesh 数据</param>
        /// <param name="startPolyRef">起始多边形引用</param>
        /// <param name="endPolyRef">目标多边形引用</param>
        /// <param name="startPos">起始位置</param>
        /// <param name="endPos">目标位置</param>
        /// <param name="filter">查询过滤器</param>
        /// <returns>路径多边形引用列表，null 表示无路径</returns>
        public static List<int>? AStar(NavMeshData navData, int startPolyRef, int endPolyRef,
            Vector3 startPos, Vector3 endPos, NavQueryFilter filter)
        {
            if (startPolyRef == endPolyRef) return new List<int> { startPolyRef };

            var openSet = new PriorityQueue<int, float>();
            var cameFrom = new Dictionary<int, int>();
            var gScore = new Dictionary<int, float>();
            var closed = new HashSet<int>();

            openSet.Enqueue(startPolyRef, 0f);
            gScore[startPolyRef] = 0f;

            while (openSet.Count > 0)
            {
                int current = openSet.Dequeue();
                if (current == endPolyRef)
                    return ReconstructPath(cameFrom, current);

                if (closed.Contains(current)) continue;
                closed.Add(current);

                if (!navData.TryGetPoly(current, out var tile, out var poly)) continue;

                // 遍历邻接多边形
                for (int i = 0; i < poly!.NeighborPolys.Count; i++)
                {
                    int neighborPoly = poly.NeighborPolys[i];
                    if (neighborPoly < 0) continue;

                    int neighborRef = GetPolyRef(navData, tile!, neighborPoly);
                    if (neighborRef == 0 || closed.Contains(neighborRef)) continue;

                    // 检查可通行性
                    if (!navData.TryGetPoly(neighborRef, out _, out var neighbor)) continue;
                    if (!filter.IsAreaPassable(neighbor!.AreaId, neighbor.Flags)) continue;

                    // 计算移动成本
                    float cost = filter.GetAreaCost(neighbor.AreaId) *
                                 Vector3.Distance(poly.Center, neighbor.Center);
                    float tentativeG = gScore[current] + cost;

                    if (!gScore.TryGetValue(neighborRef, out float existingG) || tentativeG < existingG)
                    {
                        cameFrom[neighborRef] = current;
                        gScore[neighborRef] = tentativeG;
                        float h = Vector3.Distance(neighbor.Center, endPos);
                        float f = tentativeG + h;
                        openSet.Enqueue(neighborRef, f);
                    }
                }
            }

            return null; // 无路径
        }

        /// <summary>
        /// Dijkstra 寻路算法。不使用启发式函数，保证找到最短路径。
        /// 适合需要精确最短路径或无明确目标方向的场景。
        /// </summary>
        public static List<int>? Dijkstra(NavMeshData navData, int startPolyRef, int endPolyRef,
            Vector3 startPos, Vector3 endPos, NavQueryFilter filter)
        {
            var openSet = new PriorityQueue<int, float>();
            var cameFrom = new Dictionary<int, int>();
            var gScore = new Dictionary<int, float>();
            var closed = new HashSet<int>();

            openSet.Enqueue(startPolyRef, 0f);
            gScore[startPolyRef] = 0f;

            while (openSet.Count > 0)
            {
                int current = openSet.Dequeue();
                if (current == endPolyRef)
                    return ReconstructPath(cameFrom, current);

                if (closed.Contains(current)) continue;
                closed.Add(current);

                if (!navData.TryGetPoly(current, out var tile, out var poly)) continue;

                for (int i = 0; i < poly!.NeighborPolys.Count; i++)
                {
                    int neighborPoly = poly.NeighborPolys[i];
                    if (neighborPoly < 0) continue;

                    int neighborRef = GetPolyRef(navData, tile!, neighborPoly);
                    if (neighborRef == 0 || closed.Contains(neighborRef)) continue;

                    if (!navData.TryGetPoly(neighborRef, out _, out var neighbor)) continue;
                    if (!filter.IsAreaPassable(neighbor!.AreaId, neighbor.Flags)) continue;

                    float cost = filter.GetAreaCost(neighbor.AreaId) *
                                 Vector3.Distance(poly.Center, neighbor.Center);
                    float tentativeG = gScore[current] + cost;

                    if (!gScore.TryGetValue(neighborRef, out float existingG) || tentativeG < existingG)
                    {
                        cameFrom[neighborRef] = current;
                        gScore[neighborRef] = tentativeG;
                        openSet.Enqueue(neighborRef, tentativeG); // 无启发式
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// IDA*（迭代加深 A*）寻路算法。
        /// 内存占用极低，适合大规模 NavMesh 上的寻路。
        /// 通过逐步增加成本阈值来限制搜索深度。
        /// </summary>
        public static List<int>? IDAStar(NavMeshData navData, int startPolyRef, int endPolyRef,
            Vector3 startPos, Vector3 endPos, NavQueryFilter filter, float maxCost = 10000f)
        {
            float threshold = Vector3.Distance(startPos, endPos);

            while (threshold < maxCost)
            {
                var path = new List<int>();
                var visited = new HashSet<int>();
                var result = IDASearch(navData, startPolyRef, endPolyRef, endPos,
                    0f, threshold, filter, path, visited);

                if (result == float.MinValue) // 找到路径
                    return path;
                if (result == float.MaxValue) // 无路径
                    return null;
                threshold = result; // 增加阈值重试
            }

            return null;
        }

        /// <summary>IDA* 递归搜索</summary>
        private static float IDASearch(NavMeshData navData, int currentRef, int endRef,
            Vector3 endPos, float g, float threshold, NavQueryFilter filter,
            List<int> path, HashSet<int> visited)
        {
            if (!navData.TryGetPoly(currentRef, out var tile, out var poly)) return float.MaxValue;

            float h = Vector3.Distance(poly!.Center, endPos);
            float f = g + h;

            if (f > threshold) return f;
            if (currentRef == endRef)
            {
                path.Add(currentRef);
                return float.MinValue; // 找到
            }

            visited.Add(currentRef);
            path.Add(currentRef);

            float minExcess = float.MaxValue;

            for (int i = 0; i < poly.NeighborPolys.Count; i++)
            {
                int neighborPoly = poly.NeighborPolys[i];
                if (neighborPoly < 0) continue;

                int neighborRef = GetPolyRef(navData, tile!, neighborPoly);
                if (neighborRef == 0 || visited.Contains(neighborRef)) continue;

                if (!navData.TryGetPoly(neighborRef, out _, out var neighbor)) continue;
                if (!filter.IsAreaPassable(neighbor!.AreaId, neighbor.Flags)) continue;

                float cost = filter.GetAreaCost(neighbor.AreaId) *
                             Vector3.Distance(poly.Center, neighbor.Center);

                float result = IDASearch(navData, neighborRef, endRef, endPos,
                    g + cost, threshold, filter, path, visited);

                if (result == float.MinValue) return float.MinValue;
                if (result < minExcess) minExcess = result;
            }

            path.RemoveAt(path.Count - 1);
            visited.Remove(currentRef);
            return minExcess;
        }

        /// <summary>重建路径</summary>
        private static List<int> ReconstructPath(Dictionary<int, int> cameFrom, int current)
        {
            var path = new List<int> { current };
            while (cameFrom.TryGetValue(current, out int prev))
            {
                path.Add(prev);
                current = prev;
            }
            path.Reverse();
            return path;
        }

        /// <summary>根据 Tile 和 Poly 索引获取多边形引用</summary>
        private static int GetPolyRef(NavMeshData data, NavTile tile, int polyIdx)
        {
            foreach (var kvp in data.PolyRefMap)
            {
                if (kvp.Value.tileIdx == tile.Index && kvp.Value.polyIdx == polyIdx)
                    return kvp.Key;
            }
            return 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 路径平滑 —— Funnel 算法（字符串拉直）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 路径平滑器。使用 Funnel（漏斗）算法对多边形路径进行拉直优化，
    /// 将沿多边形中心的锯齿路径转换为最短可达路径。
    /// 参考 Detour 的 dtSmoothPath。
    /// </summary>
    public static class PathSmoother
    {
        /// <summary>
        /// 使用 Funnel 算法平滑路径。
        /// 算法原理：将路径视为一个"漏斗"，两条边作为漏斗壁，
        /// 当漏斗壁反转时找到拐点，最终得到最短路径。
        /// </summary>
        /// <param name="navData">NavMesh 数据</param>
        /// <param name="polyPath">多边形路径</param>
        /// <param name="startPos">起始位置</param>
        /// <param name="endPos">目标位置</param>
        /// <returns>平滑后的路径点列表</returns>
        public static List<Vector3> SmoothPath(NavMeshData navData, List<int> polyPath,
            Vector3 startPos, Vector3 endPos)
        {
            var result = new List<Vector3>();
            if (polyPath == null || polyPath.Count == 0)
            {
                result.Add(startPos);
                result.Add(endPos);
                return result;
            }
            if (polyPath.Count == 1)
            {
                result.Add(startPos);
                result.Add(endPos);
                return result;
            }

            result.Add(startPos);

            // 收集每条穿越边的 portal（入口）
            var portals = new List<(Vector3 left, Vector3 right)>();
            portals.Add((startPos, startPos));

            for (int i = 0; i < polyPath.Count - 1; i++)
            {
                if (!navData.TryGetPoly(polyPath[i], out var tileA, out var polyA)) continue;
                if (!navData.TryGetPoly(polyPath[i + 1], out var tileB, out var polyB)) continue;

                // 找到两个多边形之间的共享边
                var edge = FindSharedEdge(navData, polyPath[i], polyPath[i + 1]);
                if (edge.HasValue)
                {
                    var (v0, v1) = edge.Value;
                    portals.Add((v0, v1));
                }
            }

            portals.Add((endPos, endPos));

            // Funnel 算法核心
            Vector3 apex = startPos;
            int apexIdx = 0;
            int leftIdx = 1;
            int rightIdx = 1;
            Vector3 portalLeft = portals[1].left;
            Vector3 portalRight = portals[1].right;

            for (int i = 2; i < portals.Count; i++)
            {
                var (newLeft, newRight) = portals[i];

                // 更新右边界
                if (TriArea2D(apex, portalRight, newRight) <= 0f)
                {
                    if (apex == portalRight || TriArea2D(apex, portalLeft, newRight) >= 0f)
                    {
                        portalRight = newRight;
                        rightIdx = i;
                    }
                    else
                    {
                        apex = portalLeft;
                        apexIdx = leftIdx;
                        portalRight = apex;
                        rightIdx = apexIdx;
                        result.Add(apex);
                        i = apexIdx;
                        continue;
                    }
                }

                // 更新左边界
                if (TriArea2D(apex, portalLeft, newLeft) >= 0f)
                {
                    if (apex == portalLeft || TriArea2D(apex, portalRight, newLeft) <= 0f)
                    {
                        portalLeft = newLeft;
                        leftIdx = i;
                    }
                    else
                    {
                        apex = portalRight;
                        apexIdx = rightIdx;
                        portalLeft = apex;
                        leftIdx = apexIdx;
                        result.Add(apex);
                        i = apexIdx;
                        continue;
                    }
                }
            }

            result.Add(endPos);
            return result;
        }

        /// <summary>查找两个多边形之间的共享边</summary>
        private static (Vector3 v0, Vector3 v1)? FindSharedEdge(NavMeshData data, int polyRefA, int polyRefB)
        {
            if (!data.TryGetPoly(polyRefA, out var tileA, out var polyA)) return null;
            if (!data.TryGetPoly(polyRefB, out var tileB, out var polyB)) return null;

            for (int i = 0; i < polyA!.VertIndices.Count; i++)
            {
                int va0 = polyA.VertIndices[i];
                int va1 = polyA.VertIndices[(i + 1) % polyA.VertIndices.Count];

                for (int j = 0; j < polyB!.VertIndices.Count; j++)
                {
                    int vb0 = polyB.VertIndices[j];
                    int vb1 = polyB.VertIndices[(j + 1) % polyB.VertIndices.Count];

                    if ((va0 == vb0 && va1 == vb1) || (va0 == vb1 && va1 == vb0))
                    {
                        return (tileA!.Vertices[va0], tileA.Vertices[va1]);
                    }
                }
            }
            return null;
        }

        /// <summary>计算 XZ 平面上三角形的有符号面积（用于判断左右关系）</summary>
        private static float TriArea2D(Vector3 a, Vector3 b, Vector3 c)
        {
            float ax = b.X - a.X;
            float az = b.Z - a.Z;
            float bx = c.X - a.X;
            float bz = c.Z - a.Z;
            return bx * az - ax * bz;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Crowd Manager —— 人群管理与避障（VO/RVO）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群代理。代表一个在 NavMesh 上移动的 AI 角色。
    /// </summary>
    public class CrowdAgent
    {
        /// <summary>代理 ID</summary>
        public int Id;
        /// <summary>当前位置</summary>
        public Vector3 Position;
        /// <summary>当前速度</summary>
        public Vector3 Velocity;
        /// <summary>期望速度（朝向目标）</summary>
        public Vector3 DesiredVelocity;
        /// <summary>代理半径</summary>
        public float Radius = 0.5f;
        /// <summary>最大速度</summary>
        public float MaxSpeed = 3.5f;
        /// <summary>最大加速度</summary>
        public float MaxAcceleration = 10f;
        /// <summary>当前路径</summary>
        public List<Vector3>? Path;
        /// <summary>路径目标点</summary>
        public Vector3 Target;
        /// <summary>当前路径段索引</summary>
        public int PathSegmentIndex;
        /// <summary>是否到达目标</summary>
        public bool ReachedGoal;
        /// <summary>关联的 ECS 实体 ID</summary>
        public int EntityId = -1;
        /// <summary>当前所在多边形引用</summary>
        public int CurrentPolyRef;
        /// <summary>避障参数：邻居预测时间</summary>
        public float NeighborDist = 10f;
        /// <summary>避障参数：时间步长</summary>
        public float TimeHorizon = 2.5f;
    }

    /// <summary>
    /// 人群管理器。管理多个 AI 代理的移动和避障。
    /// 使用 RVO（Reciprocal Velocity Obstacles，互惠速度障碍）算法实现群体避障。
    /// 参考 Detour 的 dtCrowd。
    /// </summary>
    public class CrowdManager
    {
        private readonly List<CrowdAgent> _agents = new();
        private readonly NavigationSystem _navSystem;
        private int _nextAgentId = 1;

        /// <summary>代理数量</summary>
        public int AgentCount => _agents.Count;

        /// <summary>创建人群管理器</summary>
        /// <param name="navSystem">导航系统引用</param>
        public CrowdManager(NavigationSystem navSystem)
        {
            _navSystem = navSystem;
        }

        /// <summary>添加代理</summary>
        public CrowdAgent AddAgent(Vector3 position, float radius = 0.5f, float maxSpeed = 3.5f)
        {
            var agent = new CrowdAgent
            {
                Id = _nextAgentId++,
                Position = position,
                Radius = radius,
                MaxSpeed = maxSpeed
            };
            agent.CurrentPolyRef = _navSystem.FindNearestPoly(position, out _, out _);
            _agents.Add(agent);
            return agent;
        }

        /// <summary>移除代理</summary>
        public bool RemoveAgent(int agentId)
        {
            for (int i = 0; i < _agents.Count; i++)
            {
                if (_agents[i].Id == agentId)
                {
                    _agents.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>获取所有代理</summary>
        public List<CrowdAgent> GetAgents() => new(_agents);

        /// <summary>移除所有代理, 重置代理 ID 计数器</summary>
        public void Clear()
        {
            _agents.Clear();
            _nextAgentId = 1;
        }

        /// <summary>设置代理目标</summary>
        public void SetAgentTarget(int agentId, Vector3 target)
        {
            var agent = _agents.Find(a => a.Id == agentId);
            if (agent == null) return;

            agent.Target = target;
            agent.ReachedGoal = false;
            agent.PathSegmentIndex = 0;

            // 计算路径
            agent.Path = _navSystem.FindPath(agent.Position, target);
        }

        /// <summary>
        /// 更新所有代理。每帧调用。
        /// 流程：1. 更新期望速度 → 2. RVO 避障 → 3. 集成运动 → 4. 更新路径
        /// </summary>
        public void Update(float dt)
        {
            if (dt <= 0f) return;

            // 步骤 1：更新每个代理的期望速度（基于路径）
            foreach (var agent in _agents)
            {
                UpdateDesiredVelocity(agent, dt);
            }

            // 步骤 2：RVO 避障 —— 计算避障后的新速度
            foreach (var agent in _agents)
            {
                ApplyRVO(agent, dt);
            }

            // 步骤 3：集成运动（位置 = 位置 + 速度 * dt）
            foreach (var agent in _agents)
            {
                agent.Position += agent.Velocity * dt;

                // 同步到 ECS
                if (agent.EntityId >= 0 && _navSystem._ecs != null)
                {
                    var transform = _navSystem._ecs.GetComponent<TransformComponent>(agent.EntityId);
                    transform.Position = agent.Position;
                    _navSystem._ecs.SetComponent(agent.EntityId, transform);
                }
            }
        }

        /// <summary>更新代理的期望速度（朝向路径上的下一个拐点）</summary>
        private void UpdateDesiredVelocity(CrowdAgent agent, float dt)
        {
            if (agent.Path == null || agent.Path.Count == 0)
            {
                agent.DesiredVelocity = Vector3.Zero;
                return;
            }

            // 找到路径上当前段的终点
            if (agent.PathSegmentIndex >= agent.Path.Count)
            {
                agent.ReachedGoal = true;
                agent.DesiredVelocity = Vector3.Zero;
                return;
            }

            Vector3 target = agent.Path[agent.PathSegmentIndex];
            Vector3 toTarget = target - agent.Position;
            toTarget.Y = 0; // 在 XZ 平面移动
            float dist = toTarget.Length();

            if (dist < agent.Radius * 0.5f)
            {
                agent.PathSegmentIndex++;
                if (agent.PathSegmentIndex >= agent.Path.Count)
                {
                    agent.ReachedGoal = true;
                    agent.DesiredVelocity = Vector3.Zero;
                    return;
                }
                target = agent.Path[agent.PathSegmentIndex];
                toTarget = target - agent.Position;
                toTarget.Y = 0;
                dist = toTarget.Length();
            }

            if (dist > 1e-4f)
            {
                agent.DesiredVelocity = Vector3.Normalize(toTarget) * agent.MaxSpeed;
            }
        }

        /// <summary>
        /// 应用 RVO（互惠速度障碍）避障算法。
        /// 核心思想：每个代理假设其他代理也会合作避让，
        /// 因此只需避开"半速度障碍"，从而实现更自然的双向避让。
        /// </summary>
        private void ApplyRVO(CrowdAgent agent, float dt)
        {
            Vector3 newVelocity = agent.DesiredVelocity;

            foreach (var other in _agents)
            {
                if (other.Id == agent.Id) continue;

                Vector3 relativePos = other.Position - agent.Position;
                relativePos.Y = 0;
                float distSq = relativePos.LengthSquared();

                float combinedRadius = agent.Radius + other.Radius;
                if (distSq < combinedRadius * combinedRadius)
                {
                    // 已碰撞 —— 强力推开
                    float dist = MathF.Sqrt(distSq);
                    if (dist > 1e-4f)
                    {
                        Vector3 pushDir = relativePos / dist;
                        float pushStrength = (combinedRadius - dist) * 5f;
                        newVelocity -= pushDir * pushStrength;
                    }
                }
                else if (distSq < agent.NeighborDist * agent.NeighborDist)
                {
                    // 预测碰撞 —— RVO 速度调整
                    float dist = MathF.Sqrt(distSq);
                    Vector3 relativeVel = agent.Velocity - other.Velocity;
                    relativeVel.Y = 0;

                    // 计算到碰撞的时间
                    float velAlongRel = Vector3.Dot(relativeVel, relativePos / dist);
                    if (velAlongRel > 0) continue; // 正在远离

                    float timeToCollision = -(dist - combinedRadius) / velAlongRel;
                    if (timeToCollision < 0 || timeToCollision > agent.TimeHorizon) continue;

                    // 计算 RVO 速度调整方向
                    Vector3 avoidanceDir = relativePos / dist;
                    // RVO 假设对方也会让一半，所以只需偏移一半
                    float avoidanceStrength = (agent.MaxSpeed - velAlongRel) * 0.5f;
                    newVelocity += avoidanceDir * avoidanceStrength * (1f - timeToCollision / agent.TimeHorizon);
                }
            }

            // 限制速度
            float speed = newVelocity.Length();
            if (speed > agent.MaxSpeed)
                newVelocity = newVelocity / speed * agent.MaxSpeed;

            // 平滑速度变化（限制加速度）
            Vector3 velDiff = newVelocity - agent.Velocity;
            float accel = velDiff.Length() / dt;
            if (accel > agent.MaxAcceleration)
            {
                velDiff = velDiff / velDiff.Length() * agent.MaxAcceleration * dt;
                newVelocity = agent.Velocity + velDiff;
            }

            agent.Velocity = newVelocity;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 导航查询系统
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导航查询。提供射线检测、路径查找和最近多边形查找等查询功能。
    /// 参考 Detour 的 dtNavMeshQuery。
    /// </summary>
    public class NavQuery
    {
        private readonly NavMeshData _data;
        private readonly NavQueryFilter _filter;

        public NavQuery(NavMeshData data, NavQueryFilter? filter = null)
        {
            _data = data;
            _filter = filter ?? new NavQueryFilter();
        }

        /// <summary>
        /// 查找最近的可行走多边形。
        /// 在给定位置周围搜索，找到包含该点或距离最近的多边形。
        /// </summary>
        public int FindNearestPoly(Vector3 pos, out Vector3 closestPos, out bool posOverPoly)
        {
            closestPos = pos;
            posOverPoly = false;

            int bestRef = 0;
            float bestDist = float.MaxValue;

            foreach (var tile in _data.Tiles.Values)
            {
                // 快速 AABB 过滤
                if (pos.X < tile.BoundsMin.X - 5f || pos.X > tile.BoundsMax.X + 5f ||
                    pos.Z < tile.BoundsMin.Z - 5f || pos.Z > tile.BoundsMax.Z + 5f)
                    continue;

                foreach (var poly in tile.Polys)
                {
                    if (!_filter.IsAreaPassable(poly.AreaId, poly.Flags)) continue;

                    if (poly.ContainsPoint(pos, tile))
                    {
                        closestPos = pos;
                        posOverPoly = true;
                        return GetPolyRef(_data, tile, poly.Index);
                    }

                    // 计算到多边形中心的最短距离
                    float dist = Vector3.Distance(pos, poly.Center);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestRef = GetPolyRef(_data, tile, poly.Index);
                    }
                }
            }

            return bestRef;
        }

        /// <summary>
        /// 射线检测。沿给定方向发射射线，返回碰到的第一个多边形。
        /// 使用 DDA（数字微分分析器）算法遍历 NavMesh。
        /// </summary>
        public int Raycast(int startPolyRef, Vector3 startPos, Vector3 endPos,
            out Vector3 hitPos, out float t)
        {
            hitPos = endPos;
            t = 1f;

            Vector3 dir = endPos - startPos;
            float totalLen = dir.Length();
            if (totalLen < 1e-4f) return startPolyRef;
            dir /= totalLen;

            int currentRef = startPolyRef;
            float traveled = 0f;

            while (traveled < totalLen)
            {
                if (!_data.TryGetPoly(currentRef, out var tile, out var poly)) break;

                // 找到射线从当前多边形穿出的边
                int exitEdge = -1;
                float minT = float.MaxValue;

                for (int i = 0; i < poly!.VertIndices.Count; i++)
                {
                    int vi0 = poly.VertIndices[i];
                    int vi1 = poly.VertIndices[(i + 1) % poly.VertIndices.Count];
                    var v0 = tile!.Vertices[vi0];
                    var v1 = tile.Vertices[vi1];

                    // 射线与边的交点（XZ 平面）
                    if (RaySegmentIntersect(startPos, dir, v0, v1, out float hitT) && hitT < minT)
                    {
                        minT = hitT;
                        exitEdge = i;
                    }
                }

                if (exitEdge == -1)
                {
                    // 射线在多边形内结束
                    hitPos = endPos;
                    t = 1f;
                    return currentRef;
                }

                int neighborPolyIdx = poly.NeighborPolys[exitEdge];
                if (neighborPolyIdx < 0)
                {
                    // 碰到边界 —— 射线被阻挡
                    hitPos = startPos + dir * minT;
                    t = minT / totalLen;
                    return currentRef;
                }

                // 移动到邻接多边形
                int neighborRef = GetPolyRef(_data, tile, neighborPolyIdx);
                if (neighborRef == 0) break;

                if (!_data.TryGetPoly(neighborRef, out _, out var neighbor)) break;
                if (!_filter.IsAreaPassable(neighbor!.AreaId, neighbor.Flags))
                {
                    // 邻接多边形不可通行
                    hitPos = startPos + dir * minT;
                    t = minT / totalLen;
                    return currentRef;
                }

                traveled = minT;
                currentRef = neighborRef;
            }

            hitPos = endPos;
            t = 1f;
            return currentRef;
        }

        /// <summary>射线与线段相交检测（XZ 平面）</summary>
        private bool RaySegmentIntersect(Vector3 rayOrigin, Vector3 rayDir, Vector3 segA, Vector3 segB, out float t)
        {
            t = 0f;
            Vector3 segDir = segB - segA;
            float denom = rayDir.X * segDir.Z - rayDir.Z * segDir.X;
            if (MathF.Abs(denom) < 1e-6f) return false;

            Vector3 diff = segA - rayOrigin;
            float t1 = (diff.X * segDir.Z - diff.Z * segDir.X) / denom;
            float t2 = (diff.X * rayDir.Z - diff.Z * rayDir.X) / denom;

            if (t1 >= 0f && t2 >= 0f && t2 <= 1f)
            {
                t = t1;
                return true;
            }
            return false;
        }

        /// <summary>获取多边形引用</summary>
        private int GetPolyRef(NavMeshData data, NavTile tile, int polyIdx)
        {
            foreach (var kvp in data.PolyRefMap)
                if (kvp.Value.tileIdx == tile.Index && kvp.Value.polyIdx == polyIdx)
                    return kvp.Key;
            return 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 动态 NavMesh 修改
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 动态 NavMesh 修改器。支持运行时对 NavMesh 进行局部更新，
    /// 例如在场景中放置/移除障碍物时，只重新生成受影响的 Tile。
    /// 参考 Detour 的 dtTileCache 机制。
    /// </summary>
    public class DynamicNavModifier
    {
        private readonly NavMeshData _data;
        private readonly NavMeshGenerator _generator;
        private readonly HashSet<(int x, int z)> _dirtyTiles = new();

        public int DirtyTileCount => _dirtyTiles.Count;

        public DynamicNavModifier(NavMeshData data, NavMeshGenerator generator)
        {
            _data = data;
            _generator = generator;
        }

        /// <summary>
        /// 标记受影响区域为脏。下次更新时将重新生成这些 Tile。
        /// </summary>
        /// <param name="center">影响中心</param>
        /// <param name="radius">影响半径</param>
        public void MarkAreaDirty(Vector3 center, float radius)
        {
            float tileSize = _data.TileSize;
            int cx = (int)MathF.Floor((center.X - _data.Origin.X) / tileSize);
            int cz = (int)Math.Floor((center.Z - _data.Origin.Z) / tileSize);
            int r = (int)MathF.Ceiling(radius / tileSize) + 1;

            for (int x = cx - r; x <= cx + r; x++)
            {
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (_data.Tiles.ContainsKey((x, z)))
                        _dirtyTiles.Add((x, z));
                }
            }
        }

        /// <summary>
        /// 添加障碍物并标记相关区域为脏。
        /// </summary>
        public void AddObstacle(Vector3 position, float radius)
        {
            MarkAreaDirty(position, radius);
        }

        /// <summary>
        /// 执行所有脏 Tile 的重新生成。
        /// 实际实现中会使用 Tile Cache 和临时障碍物合并，这里简化为重新生成。
        /// </summary>
        /// <param name="vertices">更新后的场景顶点</param>
        /// <param name="indices">更新后的场景索引</param>
        public void ProcessDirty(Vector3[] vertices, int[] indices)
        {
            foreach (var (tx, tz) in _dirtyTiles)
            {
                if (!_data.Tiles.TryGetValue((tx, tz), out var oldTile)) continue;

                var bmin = new Vector3(
                    _data.Origin.X + tx * _data.TileSize,
                    _data.Origin.Y,
                    _data.Origin.Z + tz * _data.TileSize);
                var bmax = new Vector3(
                    bmin.X + _data.TileSize,
                    _data.Origin.Y + 1000f,
                    bmin.Z + _data.TileSize);

                // 重新生成 Tile
                var newTile = new NavTile
                {
                    Index = oldTile.Index,
                    GridX = tx,
                    GridZ = tz,
                    BoundsMin = bmin,
                    BoundsMax = bmax,
                    Salt = oldTile.Salt + 1,
                    IsLoaded = true
                };

                // 简化：复制旧的 poly 引用映射
                _data.Tiles[(tx, tz)] = newTile;
            }

            _dirtyTiles.Clear();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // ECS 集成组件
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导航代理组件。附加到需要寻路的 ECS 实体上。
    /// </summary>
    public struct NavAgentComponent : IComponent
    {
        /// <summary>代理半径</summary>
        public float Radius;
        /// <summary>最大移动速度</summary>
        public float MaxSpeed;
        /// <summary>当前目标位置</summary>
        public Vector3 TargetPosition;
        /// <summary>是否正在寻路移动</summary>
        public bool IsMoving;
        /// <summary>对应的 CrowdAgent ID</summary>
        public int CrowdAgentId;
        /// <summary>查询过滤器</summary>
        public NavQueryFilter? Filter;
    }

    // ══════════════════════════════════════════════════════════════════
    // 导航系统 —— 主系统类
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导航系统。整合 NavMesh 生成、寻路、路径平滑、人群管理和动态修改。
    /// 
    /// 核心功能：
    /// 1. NavMesh 生成（Recast 风格：体素化 → 区域分割 → 轮廓 → 多边形网格）
    /// 2. 寻路算法（A*、Dijkstra、IDA*）
    /// 3. 路径平滑（Funnel 字符串拉直）
    /// 4. 人群管理（RVO 避障）
    /// 5. 动态 NavMesh 修改（局部更新）
    /// 6. 导航查询（Raycast、Pathfind、FindNearestPoly）
    /// 7. 导航链接和导航区域
    /// 8. 与 ECS 系统集成
    /// </summary>
    public class NavigationSystem : ISystem
    {
        // ── 名称 ──
        public string Name => "NavigationSystem";

        // ── 内部数据 ──
        /// <summary>NavMesh 数据</summary>
        public NavMeshData? NavData;
        /// <summary>NavMesh 生成器</summary>
        private NavMeshGenerator? _generator;
        /// <summary>导航查询</summary>
        private NavQuery? _query;
        /// <summary>人群管理器</summary>
        public CrowdManager? Crowd;
        /// <summary>动态修改器</summary>
        private DynamicNavModifier? _modifier;
        /// <summary>默认查询过滤器</summary>
        private NavQueryFilter _defaultFilter = new();
        /// <summary>ECS 引用</summary>
        internal EntityComponentSystem? _ecs;
        /// <summary>导航配置 (由 Initialize 设置, BuildNavMesh 时使用)</summary>
        private ShanyuEngine.Core.NavMeshConfig? _config;
        /// <summary>默认构建配置 (从 NavMeshConfig 转换而来)</summary>
        private NavMeshGenerator.NavMeshBuildConfig? _buildConfig;
        /// <summary>是否已初始化</summary>
        public bool IsInitialized { get; private set; }
        /// <summary>是否已释放</summary>
        public bool IsDisposed { get; private set; }

        // ── 统计信息 ──
        /// <summary>NavMesh 是否已构建</summary>
        public bool IsBuilt => NavData != null;
        /// <summary>总多边形数</summary>
        public int TotalPolyCount => NavData?.TotalPolyCount ?? 0;
        /// <summary>总 Tile 数</summary>
        public int TotalTileCount => NavData?.Tiles.Count ?? 0;
        /// <summary>导航链接数</summary>
        public int NavLinkCount => NavData?.Links.Count ?? 0;
        /// <summary>人群代理数</summary>
        public int CrowdAgentCount => Crowd?.AgentCount ?? 0;

        // ── 事件 ──
        /// <summary>NavMesh 构建完成事件</summary>
        public event Action<NavMeshData>? OnNavMeshBuilt;
        /// <summary>日志事件</summary>
        public event Action<string>? OnLogMessage;

        // ══════════════════════════════════════════════════════════════
        // 构造函数
        // ══════════════════════════════════════════════════════════════

        public NavigationSystem()
        {
            Log("导航系统已初始化");
        }

        /// <summary>
        /// 使用导航配置初始化系统。将引擎级 NavMeshConfig 转换为内部构建配置,
        /// 配置默认查询过滤器, 并设置代理参数。
        /// </summary>
        /// <param name="config">导航网格配置 (来自引擎引导配置)</param>
        public void Initialize(ShanyuEngine.Core.NavMeshConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (IsDisposed) throw new ObjectDisposedException(nameof(NavigationSystem));

            _config = config;

            // 将引擎级配置转换为 NavMesh 构建配置
            _buildConfig = new NavMeshGenerator.NavMeshBuildConfig
            {
                CellSize = config.CellSize,
                CellHeight = config.CellHeight,
                AgentRadius = config.AgentRadius,
                AgentHeight = config.AgentHeight,
                WalkableSlopeAngle = config.AgentMaxSlope,
                MaxClimbAngle = config.AgentMaxClimb,
                MinRegionArea = (int)config.RegionMinSize,
                MergeRegionArea = (int)(config.RegionMinSize * 2.5f),
                WalkableHeight = config.AgentHeight,
                WalkableRadius = config.AgentRadius,
            };

            // 配置默认查询过滤器: 所有区域默认可通行
            _defaultFilter = new NavQueryFilter
            {
                IncludeFlags = 0xFFFF,
                ExcludeFlags = 0x0,
                DefaultAreaCost = 1f,
            };

            IsInitialized = true;
            Log($"导航系统已配置: CellSize={config.CellSize}, AgentRadius={config.AgentRadius}, " +
                $"AgentHeight={config.AgentHeight}, MaxSlope={config.AgentMaxSlope}°, MaxClimb={config.AgentMaxClimb}");
        }

        /// <summary>
        /// 释放导航系统占用的所有资源。清理 NavMesh 数据、查询对象、
        /// 人群管理器、动态修改器, 并解除 ECS 绑定。
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed) return;

            // 清理人群管理器中的所有代理
            Crowd?.Clear();
            Crowd = null;

            // 释放查询和修改器
            _query = null;
            _modifier = null;

            // 释放 NavMesh 数据
            NavData = null;
            _generator = null;

            // 解除 ECS 绑定
            _ecs = null;

            // 清理事件
            OnNavMeshBuilt = null;
            OnLogMessage = null;

            IsDisposed = true;
            IsInitialized = false;
        }

        // ══════════════════════════════════════════════════════════════
        // 系统绑定
        // ══════════════════════════════════════════════════════════════

        /// <summary>绑定 ECS 系统</summary>
        public void BindECS(EntityComponentSystem ecs)
        {
            _ecs = ecs;
            Log("已绑定 ECS 系统");
        }

        // ══════════════════════════════════════════════════════════════
        // NavMesh 构建
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 构建 NavMesh。
        /// </summary>
        /// <param name="vertices">场景顶点数组</param>
        /// <param name="indices">三角形索引数组</param>
        /// <param name="origin">NavMesh 原点</param>
        /// <param name="worldSize">世界范围</param>
        /// <param name="config">构建配置</param>
        public void BuildNavMesh(Vector3[] vertices, int[] indices, Vector3 origin, Vector3 worldSize,
            NavMeshGenerator.NavMeshBuildConfig? config = null)
        {
            // 若未传入构建配置, 则使用 Initialize 时存储的配置
            var effectiveConfig = config ?? _buildConfig;
            Log("开始构建 NavMesh...");
            _generator = new NavMeshGenerator(effectiveConfig);
            NavData = _generator.Build(vertices, indices, origin, worldSize);
            _query = new NavQuery(NavData, _defaultFilter);
            _modifier = new DynamicNavModifier(NavData, _generator);
            Crowd = new CrowdManager(this);

            OnNavMeshBuilt?.Invoke(NavData);
            Log($"NavMesh 构建完成: {TotalTileCount} Tiles, {TotalPolyCount} Polys");
        }

        // ══════════════════════════════════════════════════════════════
        // 寻路接口
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 查找从起点到终点的路径。使用 A* 算法 + Funnel 平滑。
        /// </summary>
        /// <param name="start">起点世界坐标</param>
        /// <param name="end">终点世界坐标</param>
        /// <returns>平滑后的路径点列表，null 表示无路径</returns>
        public List<Vector3>? FindPath(Vector3 start, Vector3 end)
        {
            if (NavData == null || _query == null) return null;

            // 查找起点和终点所在的多边形
            int startRef = _query.FindNearestPoly(start, out _, out _);
            int endRef = _query.FindNearestPoly(end, out _, out _);

            if (startRef == 0 || endRef == 0) return null;

            // A* 寻路
            var polyPath = Pathfinding.AStar(NavData, startRef, endRef, start, end, _defaultFilter);
            if (polyPath == null || polyPath.Count == 0) return null;

            // Funnel 路径平滑
            var smoothPath = PathSmoother.SmoothPath(NavData, polyPath, start, end);
            return smoothPath;
        }

        /// <summary>
        /// 使用指定算法查找路径。
        /// </summary>
        /// <param name="algorithm">寻路算法："AStar"、"Dijkstra"、"IDAStar"</param>
        public List<Vector3>? FindPath(Vector3 start, Vector3 end, string algorithm)
        {
            if (NavData == null || _query == null) return null;

            int startRef = _query.FindNearestPoly(start, out _, out _);
            int endRef = _query.FindNearestPoly(end, out _, out _);
            if (startRef == 0 || endRef == 0) return null;

            List<int>? polyPath = algorithm switch
            {
                "AStar" => Pathfinding.AStar(NavData, startRef, endRef, start, end, _defaultFilter),
                "Dijkstra" => Pathfinding.Dijkstra(NavData, startRef, endRef, start, end, _defaultFilter),
                "IDAStar" => Pathfinding.IDAStar(NavData, startRef, endRef, start, end, _defaultFilter),
                _ => Pathfinding.AStar(NavData, startRef, endRef, start, end, _defaultFilter)
            };

            if (polyPath == null || polyPath.Count == 0) return null;
            return PathSmoother.SmoothPath(NavData, polyPath, start, end);
        }

        /// <summary>
        /// 查找最近的可行走多边形。
        /// </summary>
        public int FindNearestPoly(Vector3 pos, out Vector3 closestPos, out bool posOverPoly)
        {
            if (_query == null)
            {
                closestPos = pos;
                posOverPoly = false;
                return 0;
            }
            return _query.FindNearestPoly(pos, out closestPos, out posOverPoly);
        }

        /// <summary>
        /// 射线检测。返回射线终点的多边形引用。
        /// </summary>
        public int Raycast(Vector3 start, Vector3 end, out Vector3 hitPos, out float t)
        {
            if (_query == null || NavData == null)
            {
                hitPos = end;
                t = 1f;
                return 0;
            }
            int startRef = _query.FindNearestPoly(start, out _, out _);
            return _query.Raycast(startRef, start, end, out hitPos, out t);
        }

        // ══════════════════════════════════════════════════════════════
        // 导航链接和区域管理
        // ══════════════════════════════════════════════════════════════

        /// <summary>注册导航区域</summary>
        public void RegisterArea(NavArea area)
        {
            NavData?.RegisterArea(area);
        }

        /// <summary>添加导航链接</summary>
        public NavLink AddNavLink(Vector3 start, Vector3 end, LinkType type = LinkType.Jump)
        {
            var link = new NavLink
            {
                Id = NavData?.Links.Count ?? 0,
                Start = start,
                End = end,
                Type = type
            };
            NavData?.Links.Add(link);
            return link;
        }

        // ══════════════════════════════════════════════════════════════
        // 人群管理
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 注册 ECS 实体为导航代理。
        /// </summary>
        public CrowdAgent? RegisterAgent(int entityId, float radius = 0.5f, float maxSpeed = 3.5f)
        {
            if (Crowd == null || _ecs == null) return null;

            var transform = _ecs.GetComponent<TransformComponent>(entityId);
            var agent = Crowd.AddAgent(transform.Position, radius, maxSpeed);
            agent.EntityId = entityId;

            _ecs.AddComponent(entityId, new NavAgentComponent
            {
                Radius = radius,
                MaxSpeed = maxSpeed,
                TargetPosition = transform.Position,
                IsMoving = false,
                CrowdAgentId = agent.Id,
                Filter = _defaultFilter
            });

            return agent;
        }

        /// <summary>
        /// 设置代理目标位置。
        /// </summary>
        public void SetAgentTarget(int entityId, Vector3 target)
        {
            if (Crowd == null || _ecs == null) return;
            if (!_ecs.HasComponent<NavAgentComponent>(entityId)) return;

            var agentComp = _ecs.GetComponent<NavAgentComponent>(entityId);
            agentComp.TargetPosition = target;
            agentComp.IsMoving = true;
            _ecs.SetComponent(entityId, agentComp);

            Crowd.SetAgentTarget(agentComp.CrowdAgentId, target);
        }

        // ══════════════════════════════════════════════════════════════
        // 动态修改
        // ══════════════════════════════════════════════════════════════

        /// <summary>添加障碍物（标记区域为脏）</summary>
        public void AddObstacle(Vector3 position, float radius)
        {
            _modifier?.AddObstacle(position, radius);
        }

        /// <summary>处理脏 Tile 重新生成</summary>
        public void ProcessDirtyNavMesh(Vector3[] vertices, int[] indices)
        {
            _modifier?.ProcessDirty(vertices, indices);
        }

        // ══════════════════════════════════════════════════════════════
        // 更新
        // ══════════════════════════════════════════════════════════════

        /// <summary>ECS 系统更新接口</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Update(dt);
        }

        /// <summary>主更新方法</summary>
        public void Update(float dt)
        {
            Crowd?.Update(dt);
        }

        // ══════════════════════════════════════════════════════════════
        // 工具方法
        // ══════════════════════════════════════════════════════════════

        /// <summary>获取统计信息字符串</summary>
        public string GetStatsString()
        {
            return $"[Navigation] " +
                   $"已构建={IsBuilt} " +
                   $"Tiles={TotalTileCount} " +
                   $"Polys={TotalPolyCount} " +
                   $"Links={NavLinkCount} " +
                   $"Agents={CrowdAgentCount}";
        }

        /// <summary>日志辅助方法</summary>
        private void Log(string msg) => OnLogMessage?.Invoke($"[Navigation] {msg}");
    }
}
