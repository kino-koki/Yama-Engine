// ============================================================================
//  NavMeshDebugReal.cs -- 导航网格调试可视化
//  山雨引擎 ShanyuEngine.AI
// ----------------------------------------------------------------------------
//  提供导航网格的调试绘制和ASCII文本输出:
//    1. NavMeshDebugDraw - 调试绘制器 (多边形边/链接/BVH/OffMeshConnection/路径)
//    2. ASCII调试输出 (导航网格信息/寻路统计/高度场可视化)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════════
    //  DebugDraw回调接口 (可由外部渲染系统实现)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 调试绘制回调接口。外部渲染器实现此接口以接收绘制命令。
    /// </summary>
    public interface IDebugDrawCallback
    {
        void DrawLine(Vector3 from, Vector3 to, uint color, float lineWidth = 1.0f);
        void DrawTriangle(Vector3 a, Vector3 b, Vector3 c, uint color, float alpha = 0.5f);
        void DrawCircle(Vector3 center, float radius, uint color, float lineWidth = 1.0f);
        void DrawArrow(Vector3 from, Vector3 to, uint color, float headSize = 0.2f);
        void DrawText3D(Vector3 pos, string text, uint color, float size = 1.0f);
        void DrawBox(Vector3 center, Vector3 halfExtents, uint color, float alpha = 0.3f);
        void BeginBatch(DebugDrawMode mode);
        void EndBatch();
    }

    public enum DebugDrawMode
    {
        Polys,
        Links,
        BVTree,
        OffMeshConnections,
        Path,
        OpenList,
        ClosedList,
        Portals,
        DistanceField,
        Regions
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  DebugDrawColors - 调试颜色
    // ════════════════════════════════════════════════════════════════════════════

    public static class DebugDrawColors
    {
        // ARGB 格式
        public const uint NavMeshPolyFill = 0x400000FF;       // 半透明蓝色
        public const uint NavMeshPolyWire = 0xFF00FFFF;        // 青色线框
        public const uint NavMeshPolyBorder = 0xFFFFFFFF;      // 白色边框
        public const uint NavLinkNormal = 0xFF00FF00;         // 绿色链接
        public const uint NavLinkOffMesh = 0xFFFF8800;        // 橙色离网连接
        public const uint PathLine = 0xFFFF0000;              // 红色路径
        public const uint PathWaypoint = 0xFFFFFF00;           // 黄色路径点
        public const uint PathStart = 0xFF00FF00;              // 绿色起点
        public const uint PathEnd = 0xFFFF0000;                // 红色终点
        public const uint BVTreeNode = 0xFF808080;            // 灰色BVH
        public const uint BVTreeLeaf = 0xFF00FF00;            // 绿色BVH叶子
        public const uint OpenListNode = 0xFFFFFF00;           // 黄色开放列表
        public const uint ClosedListNode = 0xFF808000;        // 深黄色封闭列表
        public const uint ObstacleFill = 0x60FF0000;           // 半透明红色障碍物
        public const uint ObstacleBorder = 0xFFFF4444;       // 浅红色障碍物边框
        public const uint AgentRadius = 0x80FFFF00;            // 半透明黄色代理半径
        public const uint AgentVelocity = 0xFF00FF00;          // 绿色速度向量
        public const uint AgentDesiredVelocity = 0xFFFFFF00;   // 黄色期望速度
        public const uint ORCAPlane = 0x8000FFFF;              // 半透明青色ORCA平面
        public const uint FunnelLeft = 0xFFFF00FF;             // 紫色漏斗左
        public const uint FunnelRight = 0xFFFFFF00;           // 黄色漏斗右
        public const uint FunnelApex = 0xFF00FFFF;             // 青色漏斗顶点

        public static uint MakeColor(byte a, byte r, byte g, byte b)
        {
            return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  NavMeshDebugDraw - 调试绘制器
    // ════════════════════════════════════════════════════════════════════════════

    public class NavMeshDebugDraw
    {
        private readonly IDebugDrawCallback _callback;
        private bool _enabled = true;

        public NavMeshDebugDraw(IDebugDrawCallback callback)
        {
            _callback = callback;
        }

        public bool Enabled { get => _enabled; set => _enabled = value; }

        // ──────────────────────────────────────────────────────────────────────
        //  绘制导航网格多边形
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>绘制导航网格的所有多边形</summary>
        public void DrawNavMeshPolys(NavMesh navMesh)
        {
            if (!_enabled || _callback == null || navMesh == null) return;

            _callback.BeginBatch(DebugDrawMode.Polys);

            foreach (var tile in navMesh.GetAllTiles())
            {
                foreach (var poly in tile.Polys)
                {
                    if (poly.Verts == null || poly.Verts.Length < 3) continue;

                    // 填充多边形
                    Vector3 v0 = navMesh.GetPolyVertex(tile, poly.Verts[0]);
                    Vector3 v1 = navMesh.GetPolyVertex(tile, poly.Verts[1]);
                    Vector3 v2 = navMesh.GetPolyVertex(tile, poly.Verts[2]);

                    _callback.DrawTriangle(v0, v1, v2, DebugDrawColors.NavMeshPolyFill);

                    // 多边形边线
                    uint wireColor = DebugDrawColors.NavMeshPolyWire;
                    // 根据区域类型着色
                    wireColor = GetColorForArea(poly.Area, wireColor);

                    for (int i = 0; i < poly.Verts.Length; i++)
                    {
                        int vi1 = poly.Verts[i];
                        int vi2 = poly.Verts[(i + 1) % poly.Verts.Length];
                        Vector3 a = navMesh.GetPolyVertex(tile, vi1);
                        Vector3 b = navMesh.GetPolyVertex(tile, vi2);
                        _callback.DrawLine(a, b, wireColor);
                    }

                    // 绘制边框
                    for (int i = 0; i < poly.Verts.Length; i++)
                    {
                        int ni = poly.Neighbors[i];
                        if (ni < 0)
                        {
                            // 边界边
                            int vi1 = poly.Verts[i];
                            int vi2 = poly.Verts[(i + 1) % poly.Verts.Length];
                            Vector3 a = navMesh.GetPolyVertex(tile, vi1);
                            Vector3 b = navMesh.GetPolyVertex(tile, vi2);
                            _callback.DrawLine(a, b, DebugDrawColors.NavMeshPolyBorder, 2.0f);
                        }
                    }
                }
            }

            _callback.EndBatch();
        }

        /// <summary>根据区域类型返回不同颜色</summary>
        private uint GetColorForArea(int area, uint defaultColor)
        {
            return area switch
            {
                0 => 0xFF00FFFF, // 水域 - 青色
                1 => 0xFF00FF00, // 可行走 - 绿色
                2 => 0xFFFF8800, // 跳跃 - 橙色
                3 => 0xFFFF00FF, // 禁止 - 粉色
                _ => defaultColor
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        //  绘制导航链接
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>绘制多边形之间的导航链接</summary>
        public void DrawNavMeshLinks(NavMesh navMesh)
        {
            if (!_enabled || _callback == null || navMesh == null) return;

            _callback.BeginBatch(DebugDrawMode.Links);

            foreach (var tile in navMesh.GetAllTiles())
            {
                for (int pi = 0; pi < tile.Polys.Count; pi++)
                {
                    var poly = tile.Polys[pi];
                    if (poly.Verts == null) continue;

                    for (int ei = 0; ei < poly.Verts.Length; ei++)
                    {
                        int ni = poly.Neighbors[ei];
                        if (ni < 0 || ni >= tile.Polys.Count) continue;

                        // 绘制共享边的中心点标记
                        int v1 = poly.Verts[ei];
                        int v2 = poly.Verts[(ei + 1) % poly.Verts.Length];

                        if (v1 < 0 || v1 >= tile.Verts.Count || v2 < 0 || v2 >= tile.Verts.Count)
                            continue;

                        Vector3 mid = (tile.Verts[v1] + tile.Verts[v2]) * 0.5f;
                        _callback.DrawCircle(mid, 0.1f, DebugDrawColors.NavLinkNormal);
                    }
                }
            }

            _callback.EndBatch();
        }

        /// <summary>绘制离网连接 (OffMeshConnection)</summary>
        public void DrawOffMeshConnections(NavMesh navMesh)
        {
            if (!_enabled || _callback == null || navMesh == null) return;

            _callback.BeginBatch(DebugDrawMode.OffMeshConnections);

            foreach (var tile in navMesh.GetAllTiles())
            {
                foreach (var omc in tile.OffMeshCons)
                {
                    // 绘制连接线
                    _callback.DrawLine(omc.PosStart, omc.PosEnd, DebugDrawColors.NavLinkOffMesh, 2.0f);

                    // 起点标记
                    _callback.DrawCircle(omc.PosStart, omc.Radius, 0xFF00FF00);

                    // 终点标记
                    _callback.DrawCircle(omc.PosEnd, omc.Radius, 0xFFFF0000);

                    // 方向箭头
                    Vector3 dir = Vector3.Normalize(omc.PosEnd - omc.PosStart);
                    Vector3 arrowEnd = omc.PosStart + dir * Vector3.Distance(omc.PosStart, omc.PosEnd) * 0.5f;
                    _callback.DrawArrow(omc.PosStart, arrowEnd, DebugDrawColors.NavLinkOffMesh);
                }
            }

            _callback.EndBatch();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  绘制BVH树
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>绘制BVH包围盒层级</summary>
        public void DrawBVH(NavMesh navMesh, int maxDepth = 3)
        {
            if (!_enabled || _callback == null || navMesh == null) return;

            _callback.BeginBatch(DebugDrawMode.BVTree);

            foreach (var tile in navMesh.GetAllTiles())
            {
                DrawBVHNodeRecursive(tile, 0, maxDepth);
            }

            _callback.EndBatch();
        }

        private void DrawBVHNodeRecursive(NavMeshTile tile, int nodeIdx, int maxDepth)
        {
            if (nodeIdx >= tile.BVTree.Count || maxDepth <= 0) return;

            var node = tile.BVTree[nodeIdx];

            Vector3 center = (node.Bmin + node.Bmax) * 0.5f;
            Vector3 halfExtents = (node.Bmax - node.Bmin) * 0.5f;

            uint color = node.I != NavMeshBVNode.NoLeaf ?
                DebugDrawColors.BVTreeLeaf : DebugDrawColors.BVTreeNode;

            _callback.DrawBox(center, halfExtents, color, 0.1f);

            if (node.I == NavMeshBVNode.NoLeaf)
            {
                DrawBVHNodeRecursive(tile, nodeIdx + 1, maxDepth - 1);
                DrawBVHNodeRecursive(tile, nodeIdx + 2, maxDepth - 1);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  绘制寻路路径
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>绘制A*寻路结果</summary>
        public void DrawPath(List<Vector3> path, List<NavMeshPolyRef> polyPath = null)
        {
            if (!_enabled || _callback == null || path == null || path.Count < 2) return;

            _callback.BeginBatch(DebugDrawMode.Path);

            // 绘制路径线
            for (int i = 0; i < path.Count - 1; i++)
            {
                _callback.DrawLine(path[i], path[i + 1], DebugDrawColors.PathLine, 2.0f);
            }

            // 绘制路径点
            for (int i = 0; i < path.Count; i++)
            {
                uint color = i == 0 ? DebugDrawColors.PathStart :
                             i == path.Count - 1 ? DebugDrawColors.PathEnd :
                             DebugDrawColors.PathWaypoint;
                _callback.DrawCircle(path[i], 0.15f, color);
            }

            // 起点/终点特殊标记
            if (path.Count > 0)
            {
                _callback.DrawCircle(path[0], 0.3f, DebugDrawColors.PathStart);
                _callback.DrawText3D(path[0], "START", DebugDrawColors.PathStart);
            }
            if (path.Count > 1)
            {
                _callback.DrawCircle(path[path.Count - 1], 0.3f, DebugDrawColors.PathEnd);
                _callback.DrawText3D(path[path.Count - 1], "END", DebugDrawColors.PathEnd);
            }

            _callback.EndBatch();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  绘制A*开放/封闭列表
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>绘制A*算法的开放和封闭列表</summary>
        public void DrawAStarLists(NavMesh navMesh,
            HashSet<ulong> openList, HashSet<ulong> closedList)
        {
            if (!_enabled || _callback == null || navMesh == null) return;

            _callback.BeginBatch(DebugDrawMode.OpenList);

            // 绘制开放列表
            foreach (var refValue in openList)
            {
                var polyRef = new NavMeshPolyRef { Value = refValue };
                var tile = navMesh.GetTile(polyRef);
                if (tile == null || polyRef.Poly >= tile.Polys.Count) continue;

                var poly = tile.Polys[polyRef.Poly];
                Vector3 center = GetPolyCenter(tile, polyRef.Poly);
                _callback.DrawCircle(center, 0.2f, DebugDrawColors.OpenListNode);
                _callback.DrawText3D(center + new Vector3(0, 0.5f, 0), "O", DebugDrawColors.OpenListNode);
            }

            _callback.EndBatch();

            _callback.BeginBatch(DebugDrawMode.ClosedList);

            // 绘制封闭列表
            foreach (var refValue in closedList)
            {
                var polyRef = new NavMeshPolyRef { Value = refValue };
                var tile = navMesh.GetTile(polyRef);
                if (tile == null || polyRef.Poly >= tile.Polys.Count) continue;

                var poly = tile.Polys[polyRef.Poly];
                Vector3 center = GetPolyCenter(tile, polyRef.Poly);
                _callback.DrawCircle(center, 0.15f, DebugDrawColors.ClosedListNode);
                _callback.DrawText3D(center + new Vector3(0, 0.5f, 0), "C", DebugDrawColors.ClosedListNode);
            }

            _callback.EndBatch();
        }

        /// <summary>获取多边形中心</summary>
        private Vector3 GetPolyCenter(NavMeshTile tile, int polyIdx)
        {
            if (tile == null || polyIdx < 0 || polyIdx >= tile.Polys.Count)
                return Vector3.Zero;

            var poly = tile.Polys[polyIdx];
            if (poly.Verts == null || poly.Verts.Length == 0)
                return Vector3.Zero;

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
        //  绘制漏斗通道
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>绘制StringPull漏斗通道</summary>
        public void DrawFunnel(List<(Vector3 left, Vector3 right)> portals)
        {
            if (!_enabled || _callback == null || portals == null) return;

            _callback.BeginBatch(DebugDrawMode.Portals);

            for (int i = 0; i < portals.Count; i++)
            {
                var (left, right) = portals[i];
                _callback.DrawLine(left, right, DebugDrawColors.FunnelLeft, 1.0f);

                if (i > 0)
                {
                    var (prevLeft, prevRight) = portals[i - 1];
                    _callback.DrawLine(prevLeft, left, DebugDrawColors.FunnelLeft, 0.5f);
                    _callback.DrawLine(prevRight, right, DebugDrawColors.FunnelRight, 0.5f);
                }
            }

            _callback.EndBatch();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  绘制代理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>绘制导航代理 (位置/半径/速度)</summary>
        public void DrawAgent(Vector3 position, float radius, Vector3 velocity,
            Vector3 desiredVelocity, float drawHeight = 0.0f)
        {
            if (!_enabled || _callback == null) return;

            Vector3 pos = position + new Vector3(0, drawHeight, 0);

            // 代理半径
            _callback.DrawCircle(pos, radius, DebugDrawColors.AgentRadius);

            // 当前速度向量
            if (velocity.LengthSquared() > 0.001f)
            {
                Vector3 velEnd = pos + velocity;
                _callback.DrawArrow(pos, velEnd, DebugDrawColors.AgentVelocity);
            }

            // 期望速度向量
            if (desiredVelocity.LengthSquared() > 0.001f)
            {
                Vector3 desEnd = pos + desiredVelocity;
                _callback.DrawArrow(pos, desEnd, DebugDrawColors.AgentDesiredVelocity, 0.15f);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  NavMeshDebugText - ASCII调试输出
    // ════════════════════════════════════════════════════════════════════════════

    public class NavMeshDebugText
    {
        /// <summary>打印导航网格信息摘要</summary>
        public static string PrintNavMeshInfo(NavMesh navMesh)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== NavMesh Debug Info ===");
            sb.AppendLine($"Tiles: {navMesh.TileCount}");

            int totalPolys = 0;
            int totalVerts = 0;
            int totalLinks = 0;
            int totalOMC = 0;

            foreach (var tile in navMesh.GetAllTiles())
            {
                sb.AppendLine($"  Tile({tile.TileX},{tile.TileY}): " +
                    $"Polys={tile.Polys.Count}, Verts={tile.Verts.Count}, " +
                    $"Links={tile.Links.Count}, OMC={tile.OffMeshCons.Count}, " +
                    $"BVNodes={tile.BVTree.Count}, " +
                    $"Bounds=[({tile.Bmin.X:F1},{tile.Bmin.Y:F1},{tile.Bmin.Z:F1})-" +
                    $"({tile.Bmax.X:F1},{tile.Bmax.Y:F1},{tile.Bmax.Z:F1})], " +
                    $"Salt={tile.Salt}");

                totalPolys += tile.Polys.Count;
                totalVerts += tile.Verts.Count;
                totalLinks += tile.Links.Count;
                totalOMC += tile.OffMeshCons.Count;
            }

            sb.AppendLine($"Totals: Polys={totalPolys}, Verts={totalVerts}, Links={totalLinks}, OMC={totalOMC}");
            sb.AppendLine("=============================");

            return sb.ToString();
        }

        /// <summary>打印多边形路径信息</summary>
        public static string PrintPathInfo(List<NavMeshPolyRef> polyPath, List<Vector3> straightPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Path Debug Info ===");

            if (polyPath != null)
            {
                sb.AppendLine($"Poly Path: {polyPath.Count} polys");
                for (int i = 0; i < polyPath.Count; i++)
                {
                    sb.AppendLine($"  [{i}] {polyPath[i]}");
                }
            }

            if (straightPath != null)
            {
                sb.AppendLine($"Straight Path: {straightPath.Count} points");
                float totalLen = 0;
                for (int i = 0; i < straightPath.Count; i++)
                {
                    sb.AppendLine($"  [{i}] ({straightPath[i].X:F2}, {straightPath[i].Y:F2}, {straightPath[i].Z:F2})");
                    if (i > 0)
                        totalLen += Vector3.Distance(straightPath[i], straightPath[i - 1]);
                }
                sb.AppendLine($"Total Length: {totalLen:F2}");
            }

            sb.AppendLine("=========================");
            return sb.ToString();
        }

        /// <summary>打印寻路统计信息</summary>
        public static string PrintPathfindingStats(long elapsedMs, int nodesExplored,
            int pathLength, bool found)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Pathfinding Stats ===");
            sb.AppendLine($"Status: {(found ? "FOUND" : "NOT FOUND")}");
            sb.AppendLine($"Time: {elapsedMs}ms");
            sb.AppendLine($"Nodes Explored: {nodesExplored}");
            sb.AppendLine($"Path Length: {pathLength} polys");
            sb.AppendLine("==========================");
            return sb.ToString();
        }

        /// <summary>打印高度场ASCII图 (俯视图)</summary>
        public static string PrintHeightfieldASCII(Heightfield hf)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Heightfield {hf.Width}x{hf.Height} ===");
            sb.Append("   ");
            for (int z = 0; z < hf.Width && z < 80; z++)
                sb.Append(z % 10);
            sb.AppendLine();

            for (int x = 0; x < hf.Height; x++)
            {
                sb.Append($"{x,3}");
                for (int z = 0; z < hf.Width && z < 80; z++)
                {
                    int idx = x * hf.Width + z;
                    int si = hf.SpansList[idx];
                    if (si == -1)
                    {
                        sb.Append(' ');
                    }
                    else
                    {
                        int count = 0;
                        while (si != -1)
                        {
                            count++;
                            si = hf.Spans[si].Next;
                        }
                        sb.Append(count > 9 ? '#' : (char)('0' + count));
                    }
                }
                sb.AppendLine();
            }
            sb.AppendLine("=============================");
            return sb.ToString();
        }

        /// <summary>打印紧凑高度场的区域分布</summary>
        public static string PrintCompactHeightfieldRegions(CompactHeightfield chf)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Compact Heightfield Regions {chf.Width}x{chf.Height} ===");
            sb.Append("   ");
            for (int z = 0; z < chf.Width && z < 80; z++)
                sb.Append(z % 10);
            sb.AppendLine();

            for (int x = 0; x < chf.Height; x++)
            {
                sb.Append($"{x,3}");
                for (int z = 0; z < chf.Width && z < 80; z++)
                {
                    int idx = x * chf.Width + z;
                    var cell = chf.Cells[idx];

                    if (cell.Count == 0)
                    {
                        sb.Append(' ');
                    }
                    else
                    {
                        // 取第一个span的区域ID
                        int reg = chf.Spans[cell.Index].Reg;
                        if (reg == 0)
                            sb.Append('.');
                        else
                            sb.Append(reg % 10);
                    }
                }
                sb.AppendLine();
            }
            sb.AppendLine($"MaxRegions: {chf.MaxRegions}, SpanCount: {chf.SpanCount}");
            sb.AppendLine("===================================");
            return sb.ToString();
        }

        /// <summary>打印烘焙结果摘要</summary>
        public static string PrintBakeResult(NavMeshBakeResult result, RcContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Bake Result Summary ===");

            if (result.Heightfield != null)
            {
                sb.AppendLine($"Heightfield: {result.Heightfield.Width}x{result.Heightfield.Height}, " +
                    $"Spans: {result.Heightfield.Spans.Count}");
            }

            if (result.CompactHeightfield != null)
            {
                sb.AppendLine($"Compact HF: {result.CompactHeightfield.Width}x{result.CompactHeightfield.Height}, " +
                    $"Spans: {result.CompactHeightfield.SpanCount}, Regions: {result.CompactHeightfield.MaxRegions}");
            }

            if (result.ContourSet != null)
            {
                sb.AppendLine($"Contours: {result.ContourSet.NConts}");
            }

            if (result.PolyMesh != null)
            {
                sb.AppendLine($"PolyMesh: {result.PolyMesh.Npolys} polys, {result.PolyMesh.Nverts} verts");
            }

            if (result.PolyMeshDetail != null)
            {
                sb.AppendLine($"Detail Mesh: {result.PolyMeshDetail.Nmeshes} meshes, " +
                    $"{result.PolyMeshDetail.Nverts} verts, {result.PolyMeshDetail.Ntris} tris");
            }

            if (ctx != null)
            {
                sb.AppendLine();
                sb.AppendLine("--- Stage Timings ---");
                foreach (var kvp in ctx.StageTimings)
                {
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value}ms");
                }
            }

            sb.AppendLine("===========================");
            return sb.ToString();
        }

        /// <summary>打印多边形网格详细信息</summary>
        public static string PrintPolyMeshDetail(PolyMesh mesh)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== PolyMesh Detail ({mesh.Npolys} polys, {mesh.Nvp} verts/poly) ===");

            for (int pi = 0; pi < mesh.Npolys; pi++)
            {
                int baseIdx = pi * mesh.Nvp * 2;
                sb.Append($"Poly[{pi}]: verts=[");

                for (int vi = 0; vi < mesh.Nvp; vi++)
                {
                    int vertIdx = mesh.Polys[baseIdx + vi];
                    if (vertIdx == -1) break;
                    if (vi > 0) sb.Append(", ");

                    int vBase = vertIdx * 3;
                    float x = mesh.Verts[vBase] / 4.0f * mesh.CellSize + mesh.BMin.X;
                    float y = mesh.Verts[vBase + 1] * mesh.CellHeight + mesh.BMin.Y;
                    float z = mesh.Verts[vBase + 2] / 4.0f * mesh.CellSize + mesh.BMin.Z;
                    sb.Append($"({x:F1},{y:F1},{z:F1})");
                }

                sb.Append("] neighbors=[");

                for (int ni = 0; ni < mesh.Nvp; ni++)
                {
                    int neighbor = mesh.Polys[baseIdx + mesh.Nvp + ni];
                    if (neighbor == -1) break;
                    if (ni > 0) sb.Append(", ");
                    sb.Append(neighbor);
                }

                sb.Append($"] flags={mesh.Flags[pi]} area={mesh.Areas[pi]}");
                sb.AppendLine();
            }

            sb.AppendLine("============================");
            return sb.ToString();
        }
    }
}
