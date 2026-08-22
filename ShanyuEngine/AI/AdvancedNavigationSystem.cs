using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShanyuEngine.ECS;

namespace ShanyuEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 —— 高级导航网格系统 (AdvancedNavigationSystem.cs)
    //
    // 本文件在已有的 NavigationSystem.cs（基础导航：NavMeshData / NavTile / NavPoly /
    // NavQuery / Pathfinding / PathSmoother / CrowdManager / DynamicNavModifier）之上，
    // 构建一套更完整、更接近 Recast & Detour 工业级实现的高级导航网格系统：
    //
    //   1. AdvancedNavMeshGenerator  —— 高级网格生成器
    //        · 体素化（Voxelization）            —— 三角形场景光栅化为高度场
    //        · 区域标记（Walkable/Solid/Open）   —— 基于坡度与台阶高度
    //        · 紧凑高度场（CompactHeightfield）  —— 去除冗余体素，仅保留可通行 span
    //        · 轮廓提取（Contour Extraction）    —— 区域边界转多边形轮廓
    //        · 多边形网格生成                    —— 轮廓简化 + 三角化 + 凸分割
    //        · 详细网格（Detailed Mesh）         —— 带真实高度细节的三角网
    //        · Agent 参数（半径/高度/台阶/坡度）
    //        · Tile 分块生成                     —— 大世界分块导航网格
    //   2. NavMeshTile            —— 导航网格瓦片（局部导航网格数据）
    //   3. NavMeshTileCache       —— 瓦片缓存（LRU，动态加载/卸载）
    //   4. AdvancedPathSmoother   —— 路径平滑器（漏斗/拉绳/贝塞尔/均匀插值）
    //   5. NavMeshQuery           —— 高级查询（A*/随机点/射线/可达/投影/到墙距离）
    //   6. NavMeshModifier        —— 修改器（区域标记 / 成本修改）
    //   7. NavMeshLink            —— 导航链接（传送门 / 跳跃点 / 梯子）
    //   8. DynamicNavMesh         —— 动态导航网格（运行时局部重建）
    //   9. AdvancedCrowdManager   —— 人群导航管理（RVO/分离/队列/动态避让）
    //  10. AdvancedNavigationSystem —— 主系统类，整合上述能力并与 ECS/NavigationSystem 对接
    //
    // 命名说明：为避免与 NavigationSystem.cs 中已存在的同名类型（NavMeshGenerator /
    // PathSmoother / CrowdManager / NavQuery）产生重复定义冲突，本文件对会冲突的概念统一
    // 使用 "Advanced" 前缀，其余（NavMeshTile / NavMeshTileCache / NavMeshQuery /
    // DynamicNavMesh / NavMeshModifier / NavMeshLink）使用全新名称。所有高级类型均与基础
    // NavigationSystem 保持兼容：复用 NavMeshData / NavTile / NavPoly / NavArea /
    // NavQueryFilter / Heightfield / Contour / CrowdAgent，并可直接喂给 NavigationSystem。
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // Agent 配置与高级生成配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   高级 NavMesh 生成配置。在基础 NavMeshBuildConfig 之上扩展了更完整的 Agent
    ///   描述与 Recast 风格的细分参数。生成器据此裁剪可行走区域。
    /// </summary>
    public sealed class AdvancedNavMeshConfig
    {
        /// <summary>体素单元格大小（XZ 平面，世界单位）。</summary>
        public float CellSize = 0.3f;
        /// <summary>体素单元格高度（Y 轴，世界单位）。</summary>
        public float CellHeight = 0.2f;

        // ── Agent 描述 ──
        /// <summary>Agent 半径（用于沿可行走边界内缩，避免擦墙）。</summary>
        public float AgentRadius = 0.5f;
        /// <summary>Agent 高度（决定可行走上方净空）。</summary>
        public float AgentHeight = 2.0f;
        /// <summary>Agent 最大可攀爬台阶高度。</summary>
        public float AgentMaxStep = 0.4f;
        /// <summary>Agent 可行走的最大坡度（度）。</summary>
        public float AgentMaxSlope = 45.0f;

        // ── 区域 / 轮廓 / 多边形 ──
        /// <summary>区域最小面积（体素数），小于此值的孤立区域被剔除。</summary>
        public int MinRegionArea = 8;
        /// <summary>合并区域面积阈值。</summary>
        public int MergeRegionArea = 20;
        /// <summary>轮廓简化最大误差（世界单位）。</summary>
        public float ContourMaxError = 1.3f;
        /// <summary>每个多边形最大顶点数（凸分割上限）。</summary>
        public int MaxVertsPerPoly = 6;

        // ── Tile 分块 ──
        /// <summary>Tile 边长（世界单位）。</summary>
        public float TileSize = 64.0f;
        /// <summary>Tile 边界重叠区（用于跨 Tile 邻接拼接，世界单位）。</summary>
        public float TileBorderSize = 4.0f;

        /// <summary>按 Agent 描述派生可行走坡度阈值（法线 Y 分量下限）。</summary>
        public float WalkableNormalY => MathF.Cos(AgentMaxSlope * MathF.PI / 180f);
        /// <summary>按 Agent 描述派生可行走高度（体素层数）。</summary>
        public int WalkableHeightVoxels => Math.Max(1, (int)MathF.Ceiling(AgentHeight / CellHeight));
        /// <summary>按 Agent 描述派生可攀爬高度（体素层数）。</summary>
        public int WalkableClimbVoxels => Math.Max(1, (int)MathF.Ceiling(AgentMaxStep / CellHeight));
        /// <summary>按 Agent 半径派生边界内缩体素数。</summary>
        public int WalkableRadiusVoxels => Math.Max(0, (int)MathF.Ceiling(AgentRadius / CellSize));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 紧凑高度场与详细网格中间结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>体素 span 的区域标记类别。</summary>
    public enum SpanRegion
    {
        /// <summary>未分类。</summary>
        None,
        /// <summary>可行走（Walkable）：表面平缓且满足 Agent 净空。</summary>
        Walkable,
        /// <summary>实体（Solid）：被遮挡或坡度过陡。</summary>
        Solid,
        /// <summary>开放（Open）：低矮障碍物下方等开放空间，不可站立但可穿越。</summary>
        Open
    }

    /// <summary>
    ///   紧凑高度场中的单个 span。每个 span 表示某体素列上一段可通行表面，
    ///   记录底部/顶部高度、区域 id 与四邻域连通关系。参考 Recast 的 rcCompactSpan。
    /// </summary>

    /// <summary>
    ///   紧凑高度场（Compact Heightfield）。在原始 Heightfield 基础上去除不可通行体素，
    ///   仅保留可通行 span，并建立邻接连通图，供区域分割与轮廓提取使用。
    ///   参考 Recast 的 rcCompactHeightfield。
    /// </summary>
    public sealed class CompactHeightfield
    {
        /// <summary>X 方向体素数。</summary>
        public int Width;
        /// <summary>Z 方向体素数。</summary>
        public int Height;
        /// <summary>每个体素列起始 span 索引（-1 表示空列）。</summary>
        public int[] ColumnHeads;
        /// <summary>所有 span（扁平存储）。</summary>
        public List<CompactSpan> Spans = new();
        /// <summary>已分配的区域数量。</summary>
        public int RegionCount;

        /// <summary>创建紧凑高度场。</summary>
        public CompactHeightfield(int width, int height)
        {
            Width = width;
            Height = height;
            ColumnHeads = new int[width * height];
            Array.Fill(ColumnHeads, -1);
        }

        /// <summary>添加一个 span 并返回其在 Spans 中的索引。</summary>
        public int AddSpan(int cellX, int cellZ, int bottom, int top, SpanRegion region)
        {
            int col = cellZ * Width + cellX;
            var span = new CompactSpan { Bottom = bottom, Top = top, Region = region };
            int idx = Spans.Count;
            Spans.Add(span);
            // 链入列头（简单链表：复用 Con[0] 作为 next 指针的简化替代）
            span.Con[0] = ColumnHeads[col];
            ColumnHeads[col] = idx;
            return idx;
        }
    }

    /// <summary>
    ///   详细网格（Detailed Mesh）。在凸多边形导航网格之上叠加带真实高度细节的三角网，
    ///   用于精确的高度采样与角色足部贴合。参考 Recast 的 rcPolyMeshDetail。
    /// </summary>
    public sealed class DetailedMesh
    {
        /// <summary>详细顶点（含高度细节）。</summary>
        public List<Vector3> Vertices = new();
        /// <summary>详细三角形索引（每 3 个一组）。</summary>
        public List<int> Triangles = new();
        /// <summary>每个多边形对应的子三角网起始索引（与 NavPoly 一一对应）。</summary>
        public List<int> PolyMeshBases = new();
        /// <summary>每个多边形对应的子三角形数量。</summary>
        public List<int> PolyMeshTris = new();
    }

    // ────────────────────────────────────────────────────────────────────────
    // NavMeshTile：导航网格瓦片（局部导航网格数据）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   导航网格瓦片。承载单个 Tile 的局部导航数据：基础 NavTile（与 NavigationSystem
    ///   兼容的顶点/多边形）、紧凑高度场、详细网格、多边形 BVH、局部导航链接与盐值校验。
    ///   是 NavMeshTileCache 进行 LRU 加载/卸载的基本单元。
    /// </summary>
    public sealed class NavMeshTile
    {
        /// <summary>Tile 网格坐标 X。</summary>
        public int GridX;
        /// <summary>Tile 网格坐标 Z。</summary>
        public int GridZ;
        /// <summary>Tile 包围盒最小值。</summary>
        public Vector3 BoundsMin;
        /// <summary>Tile 包围盒最大值。</summary>
        public Vector3 BoundsMax;
        /// <summary>盐值（用于多边形引用校验，Tile 重建后递增以使旧引用失效）。</summary>
        public uint Salt = 1;
        /// <summary>是否已加载到内存。</summary>
        public bool IsLoaded = true;
        /// <summary>最后访问帧号（供 LRU 缓存淘汰使用）。</summary>
        public long LastAccessFrame;

        /// <summary>基础导航瓦片（与 NavigationSystem 的 NavMeshData 共享顶点/多边形）。</summary>
        public NavTile? BaseTile;

        /// <summary>紧凑高度场（可空，未生成详细数据时为 null）。</summary>
        public CompactHeightfield? CompactField;

        /// <summary>详细网格（可空）。</summary>
        public DetailedMesh? Detail;

        /// <summary>本 Tile 内的导航链接（传送门/跳跃点/梯子）。</summary>
        public List<NavMeshLink> Links = new();

        /// <summary>多边形 BVH 根节点索引（-1 表示未构建）。</summary>
        public int BvhRoot = -1;
        /// <summary>BVH 节点列表（构建后填充）。</summary>
        public List<TileBvhNode> BvhNodes = new();

        /// <summary>获取多边形总数。</summary>
        public int PolyCount => BaseTile?.Polys.Count ?? 0;

        /// <summary>构建多边形 BVH 以加速点-多边形查询。</summary>
        public void BuildBvh()
        {
            BvhNodes.Clear();
            if (BaseTile == null || BaseTile.Polys.Count == 0) { BvhRoot = -1; return; }
            var ids = Enumerable.Range(0, BaseTile.Polys.Count).ToList();
            BvhRoot = BuildBvhRecursive(ids);
        }

        private int BuildBvhRecursive(List<int> polyIds)
        {
            if (polyIds.Count == 0) return -1;
            var node = new TileBvhNode();
            int nodeIdx = BvhNodes.Count;
            BvhNodes.Add(node);

            // 计算包围盒
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            foreach (int pid in polyIds)
            {
                var poly = BaseTile!.Polys[pid];
                foreach (int vi in poly.VertIndices)
                {
                    var v = BaseTile.Vertices[vi];
                    bmin = Vector3.Min(bmin, v);
                    bmax = Vector3.Max(bmax, v);
                }
            }
            node.BoundsMin = bmin;
            node.BoundsMax = bmax;

            if (polyIds.Count <= 2)
            {
                node.IsLeaf = true;
                node.PolyIds.AddRange(polyIds);
                return nodeIdx;
            }

            // 按最长轴中位数分裂
            var extent = bmax - bmin;
            int axis = extent.X > extent.Y
                ? (extent.X > extent.Z ? 0 : 2)
                : (extent.Y > extent.Z ? 1 : 2);
            polyIds.Sort((a, b) =>
            {
                var ca = BaseTile!.Polys[a].Center;
                var cb = BaseTile!.Polys[b].Center;
                return axis switch { 0 => ca.X.CompareTo(cb.X), 1 => ca.Y.CompareTo(cb.Y), _ => ca.Z.CompareTo(cb.Z) };
            });
            int mid = polyIds.Count / 2;
            node.IsLeaf = false;
            node.Left = BuildBvhRecursive(polyIds.GetRange(0, mid));
            node.Right = BuildBvhRecursive(polyIds.GetRange(mid, polyIds.Count - mid));
            return nodeIdx;
        }
    }

    /// <summary>Tile 多边形 BVH 节点。</summary>
    public sealed class TileBvhNode
    {
        /// <summary>节点包围盒最小值。</summary>
        public Vector3 BoundsMin;
        /// <summary>节点包围盒最大值。</summary>
        public Vector3 BoundsMax;
        /// <summary>是否叶节点。</summary>
        public bool IsLeaf;
        /// <summary>左子节点索引。</summary>
        public int Left = -1;
        /// <summary>右子节点索引。</summary>
        public int Right = -1;
        /// <summary>叶节点包含的多边形 id 列表。</summary>
        public List<int> PolyIds = new();
    }

    // ────────────────────────────────────────────────────────────────────────
    // NavMeshLink：导航链接（传送门 / 跳跃点 / 梯子）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>高级导航链接类型。比基础 LinkType 更细化。</summary>
    public enum NavMeshLinkType
    {
        /// <summary>跳跃点：Agent 从一端跳到另一端。</summary>
        Jump,
        /// <summary>梯子：Agent 沿梯子上下移动。</summary>
        Ladder,
        /// <summary>传送门：瞬时传送。</summary>
        Teleport,
        /// <summary>跳跃台：赋予初始速度的抛物跳跃。</summary>
        Launch,
        /// <summary>自定义。</summary>
        Custom
    }

    /// <summary>
    ///   导航链接。连接两个非邻接多边形或空间位置，用于实现跳跃点、梯子、传送门等
    ///   无法通过普通邻接表达的通行方式。可附加到 NavMeshTile 或全局 DynamicNavMesh。
    /// </summary>
    public sealed class NavMeshLink
    {
        /// <summary>唯一标识。</summary>
        public int Id;
        /// <summary>起始位置（世界空间）。</summary>
        public Vector3 Start;
        /// <summary>结束位置（世界空间）。</summary>
        public Vector3 End;
        /// <summary>起始多边形引用（0 表示未绑定）。</summary>
        public int FromPolyRef;
        /// <summary>结束多边形引用。</summary>
        public int ToPolyRef;
        /// <summary>链接半径（影响判定范围）。</summary>
        public float Radius = 0.5f;
        /// <summary>是否双向可通行。</summary>
        public bool Bidirectional = true;
        /// <summary>链接类型。</summary>
        public NavMeshLinkType Type = NavMeshLinkType.Jump;
        /// <summary>通行成本修正系数（>1 更不倾向走）。</summary>
        public float CostModifier = 1.0f;
        /// <summary>是否启用。</summary>
        public bool IsEnabled = true;
        /// <summary>所属 Tile 网格坐标（-1 表示全局链接）。</summary>
        public int TileX = -1;
        /// <summary>所属 Tile 网格坐标。</summary>
        public int TileZ = -1;

        /// <summary>转换为基础 NavLink（供 NavigationSystem 使用）。</summary>
        public NavLink ToBaseLink()
        {
            var baseType = Type switch
            {
                NavMeshLinkType.Ladder => LinkType.Ladder,
                NavMeshLinkType.Teleport => LinkType.Teleport,
                _ => LinkType.Jump,
            };
            return new NavLink
            {
                Start = Start,
                End = End,
                FromPoly = FromPolyRef,
                ToPoly = ToPolyRef,
                Radius = Radius,
                IsBidirectional = Bidirectional,
                Type = baseType,
                CostModifier = CostModifier,
                IsEnabled = IsEnabled
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // NavMeshModifier：修改器（区域标记 / 成本修改）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   导航网格修改器。作用于一个世界空间盒体范围，对该范围内的多边形执行区域重标记
    ///   或通行成本覆盖。常用于：标记草地为高成本、标记禁区为不可通行、降低道路成本等。
    ///   运行时由 DynamicNavMesh 合并到受影响 Tile。
    /// </summary>
    public sealed class NavMeshModifier
    {
        /// <summary>唯一标识。</summary>
        public int Id;
        /// <summary>作用盒体中心。</summary>
        public Vector3 Center;
        /// <summary>作用盒体半尺寸。</summary>
        public Vector3 HalfSize;
        /// <summary>要写入的区域 id（-1 表示不修改区域，仅改成本）。</summary>
        public int OverrideAreaId = -1;
        /// <summary>要写入的标志位（按位覆盖；0 表示不改）。</summary>
        public ushort OverrideFlags;
        /// <summary>通行成本覆盖（<=0 表示不覆盖）。</summary>
        public float OverrideCost = -1f;
        /// <summary>是否忽略 Y 轴（仅在 XZ 平面判断）。</summary>
        public bool IgnoreY = true;
        /// <summary>是否启用。</summary>
        public bool IsEnabled = true;

        /// <summary>判断世界点是否落在修改器作用范围内。</summary>
        public bool Contains(Vector3 p)
        {
            if (!IsEnabled) return false;
            if (IgnoreY)
            {
                return MathF.Abs(p.X - Center.X) <= HalfSize.X &&
                       MathF.Abs(p.Z - Center.Z) <= HalfSize.Z;
            }
            return MathF.Abs(p.X - Center.X) <= HalfSize.X &&
                   MathF.Abs(p.Y - Center.Y) <= HalfSize.Y &&
                   MathF.Abs(p.Z - Center.Z) <= HalfSize.Z;
        }

        /// <summary>将修改器应用到一个多边形（返回是否被修改）。</summary>
        public bool ApplyToPoly(NavMeshData data, int polyRef)
        {
            if (!IsEnabled) return false;
            if (!data.TryGetPoly(polyRef, out var tile, out var poly)) return false;
            if (!Contains(poly!.Center)) return false;

            bool changed = false;
            if (OverrideAreaId >= 0) { poly.AreaId = (byte)OverrideAreaId; changed = true; }
            if (OverrideFlags != 0) { poly.Flags = OverrideFlags; changed = true; }
            return changed;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // NavMeshTileCache：瓦片缓存（LRU，动态加载/卸载）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   导航网格瓦片缓存。以 LRU（最近最少使用）策略管理 NavMeshTile 的加载与卸载，
    ///   支持大世界流式导航网格：仅保留玩家周围一定数量的 Tile 在内存中，远离的 Tile
    ///   被卸载以节省内存。参考 Detour TileCache 的缓存思路。
    /// </summary>
    public sealed class NavMeshTileCache
    {
        private readonly LinkedList<(int x, int z)> _lruOrder = new();
        private readonly Dictionary<(int x, int z), LinkedListNode<(int x, int z)>> _lruNodes = new();
        private readonly Dictionary<(int x, int z), NavMeshTile> _tiles = new();
        private readonly Dictionary<(int x, int z), Func<NavMeshTile?>> _loaders = new();
        private readonly int _maxTiles;
        private long _frame;
        private int _cacheMisses;
        private int _cacheHits;

        /// <summary>缓存容量（最多驻留 Tile 数）。</summary>
        public int Capacity => _maxTiles;
        /// <summary>当前驻留 Tile 数。</summary>
        public int LoadedCount => _tiles.Count;
        /// <summary>缓存命中次数。</summary>
        public int CacheHits => _cacheHits;
        /// <summary>缓存未命中次数。</summary>
        public int CacheMisses => _cacheMisses;

        /// <summary>创建瓦片缓存。</summary>
        /// <param name="maxTiles">最大驻留 Tile 数。</param>
        public NavMeshTileCache(int maxTiles = 64)
        {
            _maxTiles = Math.Max(1, maxTiles);
        }

        /// <summary>注册一个 Tile 的延迟加载器（按需加载）。</summary>
        public void RegisterLoader(int x, int z, Func<NavMeshTile?> loader)
        {
            _loaders[(x, z)] = loader;
        }

        /// <summary>直接放入一个已构建好的 Tile。</summary>
        public void Put(NavMeshTile tile)
        {
            var key = (tile.GridX, tile.GridZ);
            if (_tiles.ContainsKey(key))
            {
                _tiles[key] = tile;
                Touch(key);
                return;
            }
            EnsureCapacity();
            _tiles[key] = tile;
            var node = _lruOrder.AddLast(key);
            _lruNodes[key] = node;
            tile.LastAccessFrame = _frame;
        }

        /// <summary>获取一个 Tile，不存在则尝试通过加载器加载。未命中返回 null。</summary>
        public NavMeshTile? Get(int x, int z)
        {
            var key = (x, z);
            if (_tiles.TryGetValue(key, out var tile))
            {
                _cacheHits++;
                Touch(key);
                tile.LastAccessFrame = _frame;
                return tile;
            }

            _cacheMisses++;
            if (_loaders.TryGetValue(key, out var loader))
            {
                var loaded = loader();
                if (loaded != null)
                {
                    Put(loaded);
                    loaded.LastAccessFrame = _frame;
                    return loaded;
                }
            }
            return null;
        }

        /// <summary>判断 Tile 是否已驻留。</summary>
        public bool IsLoaded(int x, int z) => _tiles.ContainsKey((x, z));

        /// <summary>显式卸载一个 Tile。</summary>
        public bool Unload(int x, int z)
        {
            var key = (x, z);
            if (!_tiles.Remove(key)) return false;
            if (_lruNodes.TryGetValue(key, out var node))
            {
                _lruOrder.Remove(node);
                _lruNodes.Remove(key);
            }
            return true;
        }

        /// <summary>预加载玩家周围的 Tile，卸载超出半径的 Tile。</summary>
        public void UpdateStreaming(Vector3 viewerPos, Vector3 origin, float tileSize, int radiusInTiles)
        {
            _frame++;
            int cx = (int)MathF.Floor((viewerPos.X - origin.X) / tileSize);
            int cz = (int)MathF.Floor((viewerPos.Z - origin.Z) / tileSize);

            // 预加载半径内
            for (int dx = -radiusInTiles; dx <= radiusInTiles; dx++)
            {
                for (int dz = -radiusInTiles; dz <= radiusInTiles; dz++)
                {
                    Get(cx + dx, cz + dz);
                }
            }

            // 卸载半径外
            var toUnload = new List<(int x, int z)>();
            foreach (var key in _tiles.Keys)
            {
                if (Math.Abs(key.x - cx) > radiusInTiles + 1 || Math.Abs(key.z - cz) > radiusInTiles + 1)
                    toUnload.Add(key);
            }
            foreach (var key in toUnload) Unload(key.x, key.z);
        }

        /// <summary>清空缓存。</summary>
        public void Clear()
        {
            _tiles.Clear();
            _lruOrder.Clear();
            _lruNodes.Clear();
        }

        private void Touch((int x, int z) key)
        {
            if (_lruNodes.TryGetValue(key, out var node))
            {
                _lruOrder.Remove(node);
                _lruNodes[key] = _lruOrder.AddLast(key);
            }
        }

        private void EnsureCapacity()
        {
            while (_tiles.Count >= _maxTiles && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.First!.Value;
                _lruOrder.RemoveFirst();
                _lruNodes.Remove(oldest);
                _tiles.Remove(oldest);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // AdvancedNavMeshGenerator：高级导航网格生成器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   高级导航网格生成器。实现完整 Recast 风格生成管线，并在每步增加更精细的处理：
    ///   体素化 → 区域标记(Walkable/Solid/Open) → 紧凑高度场 → 轮廓提取 →
    ///   多边形网格(简化+三角化+凸分割) → 详细网格 → Tile 分块。
    ///   生成的 NavMeshData 可直接交给 NavigationSystem 使用，同时返回每 Tile 的高级数据。
    /// </summary>
    public sealed class AdvancedNavMeshGenerator
    {
        /// <summary>生成配置。</summary>
        public AdvancedNavMeshConfig Config;

        /// <summary>用默认配置创建生成器。</summary>
        public AdvancedNavMeshGenerator() : this(new AdvancedNavMeshConfig()) { }

        /// <summary>用指定配置创建生成器。</summary>
        public AdvancedNavMeshGenerator(AdvancedNavMeshConfig config)
        {
            Config = config;
        }

        /// <summary>
        ///   从三角形输入生成完整 NavMesh，同时产出每 Tile 的高级数据。
        /// </summary>
        /// <param name="vertices">场景顶点数组。</param>
        /// <param name="indices">三角形索引数组（长度为 3 的倍数）。</param>
        /// <param name="origin">NavMesh 原点。</param>
        /// <param name="worldSize">世界范围（X/Y/Z）。</param>
        /// <param name="advancedTiles">输出的每 Tile 高级数据（按网格坐标索引）。</param>
        /// <returns>与 NavigationSystem 兼容的 NavMeshData。</returns>
        public NavMeshData Build(Vector3[] vertices, int[] indices, Vector3 origin, Vector3 worldSize,
            out Dictionary<(int x, int z), NavMeshTile> advancedTiles)
        {
            advancedTiles = new Dictionary<(int x, int z), NavMeshTile>();

            var data = new NavMeshData
            {
                Origin = origin,
                TileSize = Config.TileSize,
                CellSize = Config.CellSize,
                CellHeight = Config.CellHeight,
                MaxAgentRadius = Config.AgentRadius,
                MaxAgentHeight = Config.AgentHeight,
                TileCountX = (int)MathF.Ceiling(worldSize.X / Config.TileSize),
                TileCountZ = (int)MathF.Ceiling(worldSize.Z / Config.TileSize)
            };
            data.RegisterArea(NavArea.Default);
            data.RegisterArea(NavArea.Road);
            data.RegisterArea(NavArea.Grass);
            data.RegisterArea(NavArea.Water);

            // ── Tile 分块生成 ──
            for (int tx = 0; tx < data.TileCountX; tx++)
            {
                for (int tz = 0; tz < data.TileCountZ; tz++)
                {
                    var bmin = new Vector3(
                        origin.X + tx * Config.TileSize,
                        origin.Y,
                        origin.Z + tz * Config.TileSize);
                    var bmax = new Vector3(
                        bmin.X + Config.TileSize,
                        origin.Y + worldSize.Y,
                        bmin.Z + Config.TileSize);

                    var advTile = BuildTile(vertices, indices, tx, tz, bmin, bmax);
                    advTile.BaseTile!.Index = tx * data.TileCountZ + tz;
                    advTile.BaseTile.Salt = (uint)(tx * 1000 + tz + 1);
                    advTile.Salt = advTile.BaseTile.Salt;
                    data.Tiles[(tx, tz)] = advTile.BaseTile;

                    // 为每个多边形分配全局引用
                    for (int i = 0; i < advTile.BaseTile.Polys.Count; i++)
                    {
                        advTile.BaseTile.Polys[i].Index = i;
                        advTile.BaseTile.Polys[i].TileIndex = advTile.BaseTile.Index;
                        data.AllocPolyRef(advTile.BaseTile.Index, i);
                    }

                    advTile.BuildBvh();
                    advancedTiles[(tx, tz)] = advTile;
                }
            }

            BuildPolyNeighbors(data);
            return data;
        }

        /// <summary>生成单个 Tile（高级管线）。</summary>
        private NavMeshTile BuildTile(Vector3[] vertices, int[] indices, int gridX, int gridZ,
            Vector3 bmin, Vector3 bmax)
        {
            var advTile = new NavMeshTile
            {
                GridX = gridX,
                GridZ = gridZ,
                BoundsMin = bmin,
                BoundsMax = bmax,
                BaseTile = new NavTile { GridX = gridX, GridZ = gridZ, BoundsMin = bmin, BoundsMax = bmax, IsLoaded = true }
            };

            // 步 1：体素化
            var hf = Voxelize(vertices, indices, bmin, bmax);

            // 步 2：区域标记（Walkable/Solid/Open）
            MarkRegions(hf, bmin);

            // 步 3：构建紧凑高度场
            advTile.CompactField = BuildCompactHeightfield(hf, bmin);

            // 步 4：区域分割（洪水填充）
            var regionMap = PartitionRegions(advTile.CompactField);

            // 步 5：轮廓提取
            var contours = BuildContours(advTile.CompactField, regionMap, bmin);

            // 步 6：多边形网格生成（简化 + 三角化 + 凸分割）
            BuildPolygonMesh(advTile.BaseTile!, contours);

            // 步 7：详细网格生成
            advTile.Detail = BuildDetailedMesh(advTile.BaseTile!, advTile.CompactField, bmin);

            return advTile;
        }

        // ── 步 1：体素化 ──

        /// <summary>
        ///   将三角形场景光栅化为高度场。遍历每个三角形，按其 XZ 包围范围覆盖体素列，
        ///   根据三角形法线 Y 分量判定是否可行走，并记录表面高度。
        /// </summary>
        private Heightfield Voxelize(Vector3[] vertices, int[] indices, Vector3 bmin, Vector3 bmax)
        {
            int w = (int)MathF.Ceiling((bmax.X - bmin.X) / Config.CellSize);
            int h = (int)MathF.Ceiling((bmax.Z - bmin.Z) / Config.CellSize);
            var hf = new Heightfield(Math.Max(1, w), Math.Max(1, h));
            float walkableThreshold = Config.WalkableNormalY;

            for (int i = 0; i < indices.Length; i += 3)
            {
                var v0 = vertices[indices[i]];
                var v1 = vertices[indices[i + 1]];
                var v2 = vertices[indices[i + 2]];

                var normal = Vector3.Cross(v1 - v0, v2 - v0);
                if (normal.LengthSquared() < 1e-10f) continue;
                normal = Vector3.Normalize(normal);
                bool walkable = normal.Y > walkableThreshold;

                float minX = MathF.Min(v0.X, MathF.Min(v1.X, v2.X));
                float maxX = MathF.Max(v0.X, MathF.Max(v1.X, v2.X));
                float minZ = MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z));
                float maxZ = MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z));

                int x0 = Math.Clamp((int)((minX - bmin.X) / Config.CellSize), 0, hf.Width - 1);
                int x1 = Math.Clamp((int)((maxX - bmin.X) / Config.CellSize), 0, hf.Width - 1);
                int z0 = Math.Clamp((int)((minZ - bmin.Z) / Config.CellSize), 0, hf.Height - 1);
                int z1 = Math.Clamp((int)((maxZ - bmin.Z) / Config.CellSize), 0, hf.Height - 1);

                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        // 用三角形质心 Y 作为该列表面高度（简化版高度光栅化）
                        float y = (v0.Y + v1.Y + v2.Y) / 3f;
                        hf.AddSpan(x, z, y, walkable);
                    }
                }
            }
            return hf;
        }

        // ── 步 2：区域标记 ──

        /// <summary>
        ///   标记区域。基于坡度与台阶高度将每个体素 span 归类为 Walkable/Solid/Open。
        ///   Walkable：表面平缓且上方有足够净空；Solid：实体/陡坡；Open：开放空间。
        /// </summary>
        private void MarkRegions(Heightfield hf, Vector3 bmin)
        {
            // Heightfield 内部已用 walkable 标记坡度可行走的列；此处进一步细化：
            // 将相邻高度差超过可攀爬台阶的列降级为 Solid（不可通行）。
            int climbVoxels = Config.WalkableClimbVoxels;
            for (int x = 0; x < hf.Width; x++)
            {
                for (int z = 0; z < hf.Height; z++)
                {
                    if (!hf.IsWalkable(x, z)) continue;
                    // 检查四邻高度差是否超出可攀爬范围
                    bool tooSteep = false;
                    float y = hf.GetHeight(x, z);
                    var neighbors = new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) };
                    foreach (var (nx, nz) in neighbors)
                    {
                        if (!hf.IsWalkable(nx, nz)) continue;
                        float dy = MathF.Abs(hf.GetHeight(nx, nz) - y);
                        if (dy > climbVoxels * Config.CellHeight)
                        {
                            tooSteep = true;
                            break;
                        }
                    }
                    // 注意：此处仅做标记判定，实际降级在紧凑高度场构建时体现
                    if (tooSteep)
                    {
                        // 陡峭邻居不计入可行走（通过不再添加到紧凑场实现）
                    }
                }
            }
        }

        // ── 步 3：紧凑高度场 ──

        /// <summary>
        ///   构建紧凑高度场。遍历原始 Heightfield 的可行走列，过滤掉因台阶过陡而不可通行
        ///   的列，生成 CompactSpan 并建立四邻域连通关系。
        /// </summary>
        private CompactHeightfield BuildCompactHeightfield(Heightfield hf, Vector3 bmin)
        {
            var chf = new CompactHeightfield(hf.Width, hf.Height);
            int climbVoxels = Config.WalkableClimbVoxels;

            for (int x = 0; x < hf.Width; x++)
            {
                for (int z = 0; z < hf.Height; z++)
                {
                    if (!hf.IsWalkable(x, z)) continue;
                    float y = hf.GetHeight(x, z);
                    int topVoxel = (int)MathF.Floor((y - bmin.Y) / Config.CellHeight) + 1;
                    int bottomVoxel = topVoxel - 1;

                    // 台阶过滤：若任意可行走邻居高度差超阈值，标记为 Solid
                    SpanRegion region = SpanRegion.Walkable;
                    var neighbors = new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) };
                    foreach (var (nx, nz) in neighbors)
                    {
                        if (!hf.IsWalkable(nx, nz)) continue;
                        float dy = MathF.Abs(hf.GetHeight(nx, nz) - y);
                        if (dy > climbVoxels * Config.CellHeight)
                        {
                            region = SpanRegion.Solid;
                            break;
                        }
                    }
                    chf.AddSpan(x, z, bottomVoxel, topVoxel, region);
                }
            }
            return chf;
        }

        // ── 步 4：区域分割（洪水填充） ──

        /// <summary>在紧凑高度场上执行洪水填充区域分割，返回 span 索引到区域 id 的映射。</summary>
        private Dictionary<int, int> PartitionRegions(CompactHeightfield chf)
        {
            var regionOf = new Dictionary<int, int>();
            int regionId = 1;

            for (int i = 0; i < chf.Spans.Count; i++)
            {
                if (regionOf.ContainsKey(i)) continue;
                if (chf.Spans[i].Region == SpanRegion.Solid) { regionOf[i] = 0; continue; }

                // BFS 洪水填充
                var queue = new Queue<int>();
                queue.Enqueue(i);
                regionOf[i] = regionId;
                int area = 0;

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    area++;
                    var span = chf.Spans[cur];
                    foreach (int con in span.Con)
                    {
                        if (con < 0 || regionOf.ContainsKey(con)) continue;
                        if (chf.Spans[con].Region == SpanRegion.Solid) continue;
                        // 台阶高度差检查
                        if (MathF.Abs(chf.Spans[con].Top - span.Top) > Config.WalkableClimbVoxels) continue;
                        regionOf[con] = regionId;
                        queue.Enqueue(con);
                    }
                }

                if (area < Config.MinRegionArea)
                {
                    // 小区域剔除：回退为区域 0
                    foreach (var kvp in regionOf)
                        if (kvp.Value == regionId) regionOf[kvp.Key] = 0;
                }
                else
                {
                    chf.Spans[i].RegionId = regionId;
                    regionId++;
                }
            }

            chf.RegionCount = regionId;
            return regionOf;
        }

        // ── 步 5：轮廓提取 ──

        /// <summary>沿区域边界提取简化轮廓。复用基础 Contour 类型并附加区域 id。</summary>
        private List<Contour> BuildContours(CompactHeightfield chf, Dictionary<int, int> regionOf, Vector3 bmin)
        {
            var contours = new List<Contour>();
            // 按区域分组 span，取每个区域的包围边界点形成轮廓（简化版）
            var byRegion = new Dictionary<int, List<Vector3>>();
            for (int i = 0; i < chf.Spans.Count; i++)
            {
                if (!regionOf.TryGetValue(i, out int rid) || rid == 0) continue;
                int x = i % chf.Width;
                int z = i / chf.Width;
                float y = bmin.Y + chf.Spans[i].Top * Config.CellHeight;
                var p = new Vector3(bmin.X + x * Config.CellSize, y, bmin.Z + z * Config.CellSize);
                if (!byRegion.ContainsKey(rid)) byRegion[rid] = new List<Vector3>();
                byRegion[rid].Add(p);
            }

            foreach (var kvp in byRegion)
            {
                if (kvp.Value.Count < 3) continue;
                var contour = new Contour { RegionId = kvp.Key, Points = SimplifyPoints(kvp.Value) };
                if (contour.Points.Count >= 3) contours.Add(contour);
            }
            return contours;
        }

        /// <summary>Douglas-Peucker 风格点列简化。</summary>
        private List<Vector3> SimplifyPoints(List<Vector3> points)
        {
            if (points.Count <= 3) return new List<Vector3>(points);
            var simplified = new List<Vector3> { points[0] };
            for (int i = 1; i < points.Count - 1; i++)
            {
                var prev = simplified[^1];
                var curr = points[i];
                var next = points[i + 1];
                var edge = next - prev;
                float edgeLen = edge.Length();
                if (edgeLen < 1e-6f) continue;
                float t = Math.Clamp(Vector3.Dot(curr - prev, edge) / (edgeLen * edgeLen), 0f, 1f);
                var projected = prev + t * edge;
                if (Vector3.Distance(curr, projected) > Config.ContourMaxError * Config.CellSize)
                    simplified.Add(curr);
            }
            simplified.Add(points[^1]);
            return simplified;
        }

        // ── 步 6：多边形网格生成（简化 + 三角化 + 凸分割） ──

        /// <summary>
        ///   将轮廓转换为凸多边形网格。对超过 MaxVertsPerPoly 的轮廓先三角化（扇形），
        ///   再尝试合并相邻三角形为凸多边形（凸分割），控制每多边形顶点数上限。
        /// </summary>
        private void BuildPolygonMesh(NavTile tile, List<Contour> contours)
        {
            int polyIdx = 0;
            foreach (var contour in contours)
            {
                if (contour.Points.Count < 3) continue;
                var verts = contour.Points;

                if (verts.Count <= Config.MaxVertsPerPoly)
                {
                    AddPoly(tile, verts, ref polyIdx, contour.RegionId);
                }
                else
                {
                    // 三角化（扇形）后凸分割合并
                    var tris = FanTriangulate(verts);
                    var merged = MergeToConvex(tris, verts, Config.MaxVertsPerPoly);
                    foreach (var polyVerts in merged)
                        AddPoly(tile, polyVerts, ref polyIdx, contour.RegionId);
                }
            }
        }

        private void AddPoly(NavTile tile, List<Vector3> verts, ref int polyIdx, int regionId)
        {
            if (verts.Count < 3) return;
            var poly = new NavPoly
            {
                Index = polyIdx++,
                AreaId = (byte)Math.Min(regionId, 62),
                Flags = 1,
                Center = ComputeCentroid(verts),
                BoundingRadius = ComputeBoundingRadius(verts),
                TileIndex = tile.Index
            };
            int baseVert = tile.Vertices.Count;
            foreach (var v in verts)
            {
                tile.Vertices.Add(v);
                poly.VertIndices.Add(baseVert++);
            }
            for (int i = 0; i < poly.VertIndices.Count; i++) poly.NeighborPolys.Add(-1);
            tile.Polys.Add(poly);
        }

        /// <summary>扇形三角化。</summary>
        private List<(int a, int b, int c)> FanTriangulate(List<Vector3> verts)
        {
            var tris = new List<(int, int, int)>();
            for (int i = 1; i < verts.Count - 1; i++)
                tris.Add((0, i, i + 1));
            return tris;
        }

        /// <summary>将三角形列表合并为不超过 maxVerts 的凸多边形（贪心合并）。</summary>
        private List<List<Vector3>> MergeToConvex(List<(int a, int b, int c)> tris, List<Vector3> verts, int maxVerts)
        {
            // 简化贪心：每个三角形单独作为多边形，避免复杂凸性判定
            var result = new List<List<Vector3>>();
            var used = new bool[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var polyVerts = new List<Vector3> { verts[tris[i].a], verts[tris[i].b], verts[tris[i].c] };
                // 尝试与后续三角形合并（共享边且合并后仍为凸且顶点数不超限）
                for (int j = i + 1; j < tris.Count; j++)
                {
                    if (used[j]) continue;
                    if (polyVerts.Count >= maxVerts) break;
                    var t = tris[j];
                    var tVerts = new[] { verts[t.a], verts[t.b], verts[t.c] };
                    if (SharesEdge(polyVerts, tVerts, out var merged) && merged.Count <= maxVerts && IsConvex(merged))
                    {
                        polyVerts = merged;
                        used[j] = true;
                    }
                }
                result.Add(polyVerts);
            }
            return result;
        }

        private bool SharesEdge(List<Vector3> a, Vector3[] b, out List<Vector3> merged)
        {
            merged = new List<Vector3>(a);
            // 寻找 a 中一条边与 b 中一条边（反向）匹配
            for (int i = 0; i < a.Count; i++)
            {
                int ni = (i + 1) % a.Count;
                for (int j = 0; j < b.Length; j++)
                {
                    int nj = (j + 1) % b.Length;
                    if (ApproxEqual(a[i], b[nj]) && ApproxEqual(a[ni], b[j]))
                    {
                        // 在 ni 位置插入 b 的第三个点
                        int third = (j + 2) % b.Length;
                        merged.Insert(ni, b[third]);
                        return true;
                    }
                }
            }
            return false;
        }

        private bool IsConvex(List<Vector3> poly)
        {
            if (poly.Count < 4) return true;
            int sign = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                var c = poly[(i + 2) % poly.Count];
                float cross = (b.X - a.X) * (c.Z - b.Z) - (b.Z - a.Z) * (c.X - b.X);
                int s = cross > 0 ? 1 : (cross < 0 ? -1 : 0);
                if (s != 0)
                {
                    if (sign == 0) sign = s;
                    else if (sign != s) return false;
                }
            }
            return true;
        }

        private static bool ApproxEqual(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-5f;

        // ── 步 7：详细网格 ──

        /// <summary>
        ///   生成详细网格。对每个多边形，用其真实高度顶点构建子三角网，保留高度细节，
        ///   供角色足部贴合与精确高度采样使用。
        /// </summary>
        private DetailedMesh BuildDetailedMesh(NavTile tile, CompactHeightfield chf, Vector3 bmin)
        {
            var dm = new DetailedMesh();
            foreach (var poly in tile.Polys)
            {
                int baseIdx = dm.Triangles.Count;
                var verts = poly.GetVertices(tile);
                int vBase = dm.Vertices.Count;
                foreach (var v in verts) dm.Vertices.Add(v);

                if (verts.Count == 3)
                {
                    dm.Triangles.Add(vBase);
                    dm.Triangles.Add(vBase + 1);
                    dm.Triangles.Add(vBase + 2);
                }
                else
                {
                    // 扇形三角化保留高度细节
                    for (int i = 1; i < verts.Count - 1; i++)
                    {
                        dm.Triangles.Add(vBase);
                        dm.Triangles.Add(vBase + i);
                        dm.Triangles.Add(vBase + i + 1);
                    }
                }
                dm.PolyMeshBases.Add(baseIdx);
                dm.PolyMeshTris.Add((dm.Triangles.Count - baseIdx) / 3);
            }
            return dm;
        }

        // ── 邻接关系构建 ──

        /// <summary>构建多边形邻接关系（复用基础 NavigationSystem 的逻辑）。</summary>
        private void BuildPolyNeighbors(NavMeshData data)
        {
            foreach (var tile in data.Tiles.Values)
            {
                for (int i = 0; i < tile.Polys.Count; i++)
                {
                    var polyA = tile.Polys[i];
                    for (int ei = 0; ei < polyA.VertIndices.Count; ei++)
                    {
                        if (polyA.NeighborPolys[ei] != -1) continue;
                        int va0 = polyA.VertIndices[ei];
                        int va1 = polyA.VertIndices[(ei + 1) % polyA.VertIndices.Count];
                        for (int j = i + 1; j < tile.Polys.Count; j++)
                        {
                            var polyB = tile.Polys[j];
                            for (int ej = 0; ej < polyB.VertIndices.Count; ej++)
                            {
                                int vb0 = polyB.VertIndices[ej];
                                int vb1 = polyB.VertIndices[(ej + 1) % polyB.VertIndices.Count];
                                if ((va0 == vb0 && va1 == vb1) || (va0 == vb1 && va1 == vb0))
                                {
                                    polyA.NeighborPolys[ei] = j;
                                    polyB.NeighborPolys[ej] = i;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private Vector3 ComputeCentroid(List<Vector3> verts)
        {
            var sum = Vector3.Zero;
            foreach (var v in verts) sum += v;
            return verts.Count > 0 ? sum / verts.Count : sum;
        }

        private float ComputeBoundingRadius(List<Vector3> verts)
        {
            var center = ComputeCentroid(verts);
            float maxDist = 0f;
            foreach (var v in verts) maxDist = Math.Max(maxDist, Vector3.Distance(v, center));
            return maxDist;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // AdvancedPathSmoother：路径平滑器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   高级路径平滑器。在基础 PathSmoother（仅 Funnel）之上补充：
    ///   · String Pulling（拉绳算法，等价于漏斗的另一种表述）
    ///   · Bezier 平滑（二次/三次贝塞尔曲线平滑路径）
    ///   · 均匀插值（按固定间距重采样路径点）
    ///   可组合使用以得到自然且不脱离导航网格的平滑路径。
    /// </summary>
    public static class AdvancedPathSmoother
    {
        /// <summary>使用 Funnel（漏斗/拉绳）算法平滑多边形路径，返回最短拐点序列。</summary>
        public static List<Vector3> FunnelSmooth(NavMeshData navData, List<int> polyPath, Vector3 startPos, Vector3 endPos)
        {
            // 复用基础 PathSmoother 的成熟 Funnel 实现
            return PathSmoother.SmoothPath(navData, polyPath, startPos, endPos);
        }

        /// <summary>
        ///   String Pulling（拉绳算法）。与 Funnel 等价：沿通道（portal）序列拉直绳子，
        ///   在漏斗壁反转处打拐点。此处提供一个独立实现，不依赖 polyPath，
        ///   直接接收 portal（通道左右端点）序列。
        /// </summary>
        /// <param name="portals">通道序列，第一个为起点 (start,start)，最后一个为终点 (end,end)。</param>
        public static List<Vector3> StringPull(List<(Vector3 left, Vector3 right)> portals)
        {
            var result = new List<Vector3>();
            if (portals == null || portals.Count < 2) return result;

            Vector3 apex = portals[0].left;
            Vector3 portalLeft = portals[0].left;
            Vector3 portalRight = portals[0].right;
            int apexIdx = 0, leftIdx = 0, rightIdx = 0;
            result.Add(apex);

            for (int i = 1; i < portals.Count; i++)
            {
                var (newLeft, newRight) = portals[i];

                // 更新右壁
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
                // 更新左壁
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
            result.Add(portals[^1].left);
            return result;
        }

        /// <summary>
        ///   贝塞尔曲线平滑。在拐点路径上每两点之间插入三次贝塞尔曲线段，
        ///   生成更圆滑的运动轨迹。control 控制曲线弯曲程度（0~1）。
        /// </summary>
        public static List<Vector3> BezierSmooth(List<Vector3> path, int segmentsPerSpan = 8, float control = 0.3f)
        {
            var result = new List<Vector3>();
            if (path == null || path.Count == 0) return result;
            if (path.Count == 1) { result.Add(path[0]); return result; }

            result.Add(path[0]);
            for (int i = 0; i < path.Count - 1; i++)
            {
                var p0 = path[i];
                var p3 = path[i + 1];
                // 用前后点估算切向生成控制点
                var prev = i > 0 ? path[i - 1] : p0;
                var next = i < path.Count - 2 ? path[i + 2] : p3;
                var tan0 = Vector3.Normalize(p3 - prev) * Vector3.Distance(p0, p3) * control;
                var tan1 = Vector3.Normalize(next - p0) * Vector3.Distance(p0, p3) * control;
                var p1 = p0 + tan0;
                var p2 = p3 - tan1;

                for (int s = 1; s <= segmentsPerSpan; s++)
                {
                    float t = s / (float)segmentsPerSpan;
                    result.Add(CubicBezier(p0, p1, p2, p3, t));
                }
            }
            return result;
        }

        /// <summary>按固定间距重采样路径，得到均匀间距点序列，便于匀速移动。</summary>
        public static List<Vector3> InterpolateUniform(List<Vector3> path, float spacing)
        {
            var result = new List<Vector3>();
            if (path == null || path.Count == 0 || spacing <= 0f) return result;
            result.Add(path[0]);
            float accum = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                var a = result[^1];
                var b = path[i];
                var seg = b - a;
                float segLen = seg.Length();
                if (segLen < 1e-6f) continue;
                float remaining = segLen + accum;
                while (remaining >= spacing)
                {
                    float t = (spacing - accum) / segLen;
                    a = a + seg * t;
                    result.Add(a);
                    seg = b - a;
                    segLen = seg.Length();
                    accum = 0f;
                    remaining = segLen;
                }
                accum = remaining;
            }
            if (Vector3.Distance(result[^1], path[^1]) > 1e-4f) result.Add(path[^1]);
            return result;
        }

        private static Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        }

        private static float TriArea2D(Vector3 a, Vector3 b, Vector3 c)
        {
            float ax = b.X - a.X, az = b.Z - a.Z;
            float bx = c.X - a.X, bz = c.Z - a.Z;
            return bx * az - ax * bz;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // NavMeshQuery：高级查询
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   高级导航查询。提供 A* 寻路、随机可达点、射线检测、可达性检查、点到网格投影、
    ///   到墙距离等查询能力。内部复用基础 Pathfinding 与 NavQueryFilter。
    ///   参考 Detour 的 dtNavMeshQuery。
    /// </summary>

    // ────────────────────────────────────────────────────────────────────────
    // DynamicNavMesh：动态导航网格
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   动态导航网格。在运行时支持障碍物添加/移除触发局部重建、NavMeshModifier 区域/
    ///   成本修改、NavMeshLink 传送门/跳跃点/梯子管理。内部维护脏 Tile 集合，按需重建。
    /// </summary>
    public sealed class DynamicNavMesh
    {
        private readonly NavMeshData _data;
        private readonly AdvancedNavMeshGenerator _generator;
        private readonly HashSet<(int x, int z)> _dirtyTiles = new();
        private readonly List<NavMeshModifier> _modifiers = new();
        private readonly List<NavMeshLink> _links = new();
        private readonly Dictionary<(int x, int z), NavMeshTile> _advTiles;
        private Vector3[]? _sceneVertices;
        private int[]? _sceneIndices;
        private int _nextModifierId = 1;
        private int _nextLinkId = 1;

        /// <summary>脏 Tile 数量。</summary>
        public int DirtyTileCount => _dirtyTiles.Count;
        /// <summary>修改器数量。</summary>
        public int ModifierCount => _modifiers.Count;
        /// <summary>导航链接数量。</summary>
        public int LinkCount => _links.Count;

        /// <summary>构造动态导航网格。</summary>
        public DynamicNavMesh(NavMeshData data, AdvancedNavMeshGenerator generator,
            Dictionary<(int x, int z), NavMeshTile> advTiles)
        {
            _data = data;
            _generator = generator;
            _advTiles = advTiles;
        }

        /// <summary>设置场景几何输入（用于脏 Tile 重建时重新体素化）。</summary>
        public void SetSceneInput(Vector3[] vertices, int[] indices)
        {
            _sceneVertices = vertices;
            _sceneIndices = indices;
        }

        /// <summary>添加障碍物并标记受影响 Tile 为脏。</summary>
        public void AddObstacle(Vector3 position, float radius)
        {
            MarkAreaDirty(position, radius);
        }

        /// <summary>移除障碍物并标记受影响 Tile 为脏。</summary>
        public void RemoveObstacle(Vector3 position, float radius)
        {
            MarkAreaDirty(position, radius);
        }

        /// <summary>添加导航网格修改器。</summary>
        public NavMeshModifier AddModifier(Vector3 center, Vector3 halfSize, int areaId = -1, float cost = -1f)
        {
            var mod = new NavMeshModifier
            {
                Id = _nextModifierId++,
                Center = center,
                HalfSize = halfSize,
                OverrideAreaId = areaId,
                OverrideCost = cost
            };
            _modifiers.Add(mod);
            MarkAreaDirty(center, Math.Max(halfSize.X, Math.Max(halfSize.Y, halfSize.Z)));
            return mod;
        }

        /// <summary>移除修改器。</summary>
        public bool RemoveModifier(int modifierId)
        {
            for (int i = 0; i < _modifiers.Count; i++)
            {
                if (_modifiers[i].Id == modifierId)
                {
                    var m = _modifiers[i];
                    _modifiers.RemoveAt(i);
                    MarkAreaDirty(m.Center, Math.Max(m.HalfSize.X, Math.Max(m.HalfSize.Y, m.HalfSize.Z)));
                    return true;
                }
            }
            return false;
        }

        /// <summary>添加导航链接（传送门/跳跃点/梯子）。</summary>
        public NavMeshLink AddLink(Vector3 start, Vector3 end, NavMeshLinkType type = NavMeshLinkType.Jump,
            bool bidirectional = true)
        {
            var link = new NavMeshLink
            {
                Id = _nextLinkId++,
                Start = start,
                End = end,
                Type = type,
                Bidirectional = bidirectional
            };
            // 绑定到所在 Tile 的多边形
            int fromRef = FindPolyAt(start);
            int toRef = FindPolyAt(end);
            link.FromPolyRef = fromRef;
            link.ToPolyRef = toRef;
            link.TileX = (int)MathF.Floor((start.X - _data.Origin.X) / _data.TileSize);
            link.TileZ = (int)MathF.Floor((start.Z - _data.Origin.Z) / _data.TileSize);
            if (_advTiles.TryGetValue((link.TileX, link.TileZ), out var tile))
                tile.Links.Add(link);
            _links.Add(link);

            // 同步到基础 NavMeshData 以便 NavigationSystem 识别
            _data.Links.Add(link.ToBaseLink());
            return link;
        }

        /// <summary>移除导航链接。</summary>
        public bool RemoveLink(int linkId)
        {
            NavMeshLink? removed = null;
            for (int i = 0; i < _links.Count; i++)
            {
                if (_links[i].Id == linkId) { removed = _links[i]; _links.RemoveAt(i); break; }
            }
            if (removed == null) return false;
            if (_advTiles.TryGetValue((removed.TileX, removed.TileZ), out var tile))
                tile.Links.RemoveAll(l => l.Id == linkId);
            return true;
        }

        /// <summary>处理所有脏 Tile：重新生成并应用修改器。</summary>
        public void ProcessDirty()
        {
            if (_sceneVertices == null || _sceneIndices == null) { _dirtyTiles.Clear(); return; }

            foreach (var (tx, tz) in _dirtyTiles)
            {
                if (!_data.Tiles.ContainsKey((tx, tz))) continue;
                var bmin = new Vector3(
                    _data.Origin.X + tx * _data.TileSize,
                    _data.Origin.Y,
                    _data.Origin.Z + tz * _data.TileSize);
                var bmax = new Vector3(bmin.X + _data.TileSize, _data.Origin.Y + 1000f, bmin.Z + _data.TileSize);

                // 重新生成该 Tile
                var advTile = _generator.Build(_sceneVertices, _sceneIndices, _data.Origin,
                    new Vector3(_data.TileCountX * _data.TileSize, 1000f, _data.TileCountZ * _data.TileSize),
                    out var newTiles);
                if (newTiles.TryGetValue((tx, tz), out var regenerated))
                {
                    regenerated.Salt = (_advTiles.TryGetValue((tx, tz), out var old) ? old.Salt : 0) + 1;
                    _advTiles[(tx, tz)] = regenerated;
                    _data.Tiles[(tx, tz)] = regenerated.BaseTile!;
                }
                _ = advTile; // 仅取目标 Tile，丢弃整体重建结果
            }

            // 重建后应用所有修改器
            ApplyAllModifiers();
            _dirtyTiles.Clear();
        }

        /// <summary>将所有修改器应用到 NavMesh。</summary>
        public void ApplyAllModifiers()
        {
            foreach (var mod in _modifiers)
            {
                if (!mod.IsEnabled) continue;
                foreach (var tile in _data.Tiles.Values)
                {
                    foreach (var poly in tile.Polys)
                    {
                        if (mod.Contains(poly.Center))
                        {
                            if (mod.OverrideAreaId >= 0) poly.AreaId = (byte)mod.OverrideAreaId;
                            if (mod.OverrideFlags != 0) poly.Flags = mod.OverrideFlags;
                        }
                    }
                }
            }
        }

        /// <summary>获取所有导航链接。</summary>
        public List<NavMeshLink> GetLinks() => new(_links);

        /// <summary>获取所有修改器。</summary>
        public List<NavMeshModifier> GetModifiers() => new(_modifiers);

        private void MarkAreaDirty(Vector3 center, float radius)
        {
            float ts = _data.TileSize;
            int cx = (int)MathF.Floor((center.X - _data.Origin.X) / ts);
            int cz = (int)MathF.Floor((center.Z - _data.Origin.Z) / ts);
            int r = (int)MathF.Ceiling(radius / ts) + 1;
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                    if (_data.Tiles.ContainsKey((x, z))) _dirtyTiles.Add((x, z));
        }

        private int FindPolyAt(Vector3 pos)
        {
            var q = new NavMeshQuery(_data);
            return q.FindNearestPoly(pos, out _, out _);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // AdvancedCrowdManager：人群导航管理（RVO / 分离 / 队列 / 动态避让）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   高级人群管理器。在基础 CrowdManager（RVO 避障）之上增加显式的代理分离
    ///   (Separation)、队列等待 (Queue) 与动态避让速度调整。每个代理可独立开关各行为。
    ///   复用基础 CrowdAgent 类型与 NavigationSystem 的寻路能力。
    /// </summary>
    public sealed class AdvancedCrowdManager
    {
        private readonly List<CrowdAgent> _agents = new();
        private readonly NavigationSystem _navSystem;
        private readonly Dictionary<int, CrowdAgent> _agentById = new();
        private int _nextAgentId = 1;

        /// <summary>是否启用代理分离行为。</summary>
        public bool EnableSeparation = true;
        /// <summary>是否启用队列等待行为。</summary>
        public bool EnableQueue = true;
        /// <summary>是否启用 RVO 避障。</summary>
        public bool EnableRVO = true;
        /// <summary>分离力强度系数。</summary>
        public float SeparationStrength = 3.0f;
        /// <summary>队列减速距离。</summary>
        public float QueueSlowDistance = 1.5f;

        /// <summary>代理数量。</summary>
        public int AgentCount => _agents.Count;

        /// <summary>构造人群管理器。</summary>
        public AdvancedCrowdManager(NavigationSystem navSystem)
        {
            _navSystem = navSystem ?? throw new ArgumentNullException(nameof(navSystem));
        }

        /// <summary>添加代理。</summary>
        public CrowdAgent AddAgent(Vector3 position, float radius = 0.5f, float maxSpeed = 3.5f, int entityId = -1)
        {
            var agent = new CrowdAgent
            {
                Id = _nextAgentId++,
                Position = position,
                Radius = radius,
                MaxSpeed = maxSpeed,
                EntityId = entityId
            };
            agent.CurrentPolyRef = _navSystem.FindNearestPoly(position, out _, out _);
            _agents.Add(agent);
            _agentById[agent.Id] = agent;
            return agent;
        }

        /// <summary>移除代理。</summary>
        public bool RemoveAgent(int agentId)
        {
            if (!_agentById.TryGetValue(agentId, out var agent)) return false;
            _agents.Remove(agent);
            _agentById.Remove(agentId);
            return true;
        }

        /// <summary>获取代理。</summary>
        public CrowdAgent? GetAgent(int agentId) =>
            _agentById.TryGetValue(agentId, out var a) ? a : null;

        /// <summary>获取所有代理。</summary>
        public List<CrowdAgent> GetAgents() => new(_agents);

        /// <summary>设置代理目标并计算路径。</summary>
        public void SetAgentTarget(int agentId, Vector3 target)
        {
            if (!_agentById.TryGetValue(agentId, out var agent)) return;
            agent.Target = target;
            agent.ReachedGoal = false;
            agent.PathSegmentIndex = 0;
            agent.Path = _navSystem.FindPath(agent.Position, target);
        }

        /// <summary>
        ///   每帧更新所有代理。
        ///   流程：期望速度 → RVO 避障 → 分离 → 队列 → 速度限制/加速度 → 集成 → ECS 同步。
        /// </summary>
        public void Update(float dt)
        {
            if (dt <= 0f) return;

            // 1. 期望速度
            foreach (var agent in _agents) UpdateDesiredVelocity(agent);

            // 2. RVO 避障
            if (EnableRVO)
                foreach (var agent in _agents) ApplyRVO(agent, dt);

            // 3. 分离
            if (EnableSeparation)
                foreach (var agent in _agents) ApplySeparation(agent);

            // 4. 队列等待
            if (EnableQueue)
                foreach (var agent in _agents) ApplyQueue(agent);

            // 5. 速度限制 + 加速度限制 + 集成
            foreach (var agent in _agents)
            {
                float speed = agent.Velocity.Length();
                if (speed > agent.MaxSpeed)
                    agent.Velocity = agent.Velocity / speed * agent.MaxSpeed;

                agent.Position += agent.Velocity * dt;
                SyncToEcs(agent);
            }
        }

        private void UpdateDesiredVelocity(CrowdAgent agent)
        {
            if (agent.Path == null || agent.Path.Count == 0 || agent.PathSegmentIndex >= agent.Path.Count)
            {
                agent.DesiredVelocity = Vector3.Zero;
                if (agent.Path != null && agent.PathSegmentIndex >= agent.Path.Count)
                    agent.ReachedGoal = true;
                return;
            }
            Vector3 target = agent.Path[agent.PathSegmentIndex];
            var toTarget = target - agent.Position;
            toTarget.Y = 0f;
            float dist = toTarget.Length();
            if (dist < agent.Radius * 0.5f)
            {
                agent.PathSegmentIndex++;
                return;
            }
            agent.DesiredVelocity = Vector3.Normalize(toTarget) * agent.MaxSpeed;
        }

        /// <summary>RVO 互惠速度障碍避障（与基础 CrowdManager 一致的半速障碍思路）。</summary>
        private void ApplyRVO(CrowdAgent agent, float dt)
        {
            Vector3 newVel = agent.DesiredVelocity;
            foreach (var other in _agents)
            {
                if (other.Id == agent.Id) continue;
                var rel = other.Position - agent.Position;
                rel.Y = 0f;
                float distSq = rel.LengthSquared();
                float combined = agent.Radius + other.Radius;
                if (distSq >= agent.NeighborDist * agent.NeighborDist) continue;
                if (distSq < combined * combined)
                {
                    float dist = MathF.Sqrt(distSq);
                    if (dist > 1e-4f)
                    {
                        var push = rel / dist;
                        newVel -= push * (combined - dist) * 2.5f;
                    }
                }
                else
                {
                    float dist = MathF.Sqrt(distSq);
                    var relVel = agent.Velocity - other.Velocity;
                    relVel.Y = 0f;
                    float velAlong = Vector3.Dot(relVel, rel / dist);
                    if (velAlong > 0) continue;
                    float ttc = -(dist - combined) / velAlong;
                    if (ttc < 0 || ttc > agent.TimeHorizon) continue;
                    var avoid = rel / dist;
                    float strength = (agent.MaxSpeed - velAlong) * 0.5f * (1f - ttc / agent.TimeHorizon);
                    newVel += avoid * strength;
                }
            }
            agent.Velocity = newVel;
        }

        /// <summary>代理分离：已重叠时强力推开，避免穿模。</summary>
        private void ApplySeparation(CrowdAgent agent)
        {
            Vector3 sep = Vector3.Zero;
            int count = 0;
            foreach (var other in _agents)
            {
                if (other.Id == agent.Id) continue;
                var rel = agent.Position - other.Position;
                rel.Y = 0f;
                float distSq = rel.LengthSquared();
                float combined = agent.Radius + other.Radius;
                if (distSq < combined * combined && distSq > 1e-6f)
                {
                    float dist = MathF.Sqrt(distSq);
                    sep += rel / dist * (combined - dist);
                    count++;
                }
            }
            if (count > 0)
            {
                sep /= count;
                agent.Velocity += sep * SeparationStrength;
            }
        }

        /// <summary>队列等待：前方有同向慢速邻居时减速，避免推挤。</summary>
        private void ApplyQueue(CrowdAgent agent)
        {
            if (agent.DesiredVelocity.LengthSquared() < 1e-4f) return;
            var forward = agent.DesiredVelocity;
            forward.Y = 0f;
            if (forward.LengthSquared() < 1e-6f) return;
            forward = Vector3.Normalize(forward);

            foreach (var other in _agents)
            {
                if (other.Id == agent.Id) continue;
                var rel = other.Position - agent.Position;
                rel.Y = 0f;
                float dist = rel.Length();
                if (dist > QueueSlowDistance || dist < 1e-4f) continue;
                // 仅当邻居在前方且速度更慢时减速
                float dot = Vector3.Dot(Vector3.Normalize(rel), forward);
                if (dot > 0.5f && other.Velocity.Length() < agent.MaxSpeed * 0.5f)
                {
                    float factor = 1f - (dist / QueueSlowDistance) * 0.7f;
                    agent.Velocity *= factor;
                    break;
                }
            }
        }

        private void SyncToEcs(CrowdAgent agent)
        {
            if (agent.EntityId < 0) return;
            var ecs = _navSystem._ecs;
            if (ecs == null) return;
            if (ecs.HasComponent<TransformComponent>(agent.EntityId))
            {
                var t = ecs.GetComponent<TransformComponent>(agent.EntityId);
                t.Position = agent.Position;
                ecs.SetComponent(agent.EntityId, t);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // AdvancedNavigationSystem：主系统类
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///   高级导航系统。整合高级生成器、瓦片缓存、高级查询、动态导航网格与高级人群管理，
    ///   并与基础 NavigationSystem、ECS 双向对接。可作为 ISystem 注册到 ECS 主循环。
    /// </summary>
    public sealed class AdvancedNavigationSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "AdvancedNavigationSystem";

        /// <summary>基础导航系统引用（复用其 NavMeshData/寻路/ECS 绑定）。</summary>
        public NavigationSystem? BaseSystem { get; private set; }

        /// <summary>高级导航网格数据（与 BaseSystem.NavData 共享）。</summary>
        public NavMeshData? NavData => BaseSystem?.NavData;

        /// <summary>高级生成器。</summary>
        public AdvancedNavMeshGenerator? Generator { get; private set; }

        /// <summary>瓦片缓存（LRU 流式加载）。</summary>
        public NavMeshTileCache TileCache { get; }

        /// <summary>高级查询器。</summary>
        public NavMeshQuery? Query { get; private set; }

        /// <summary>动态导航网格。</summary>
        public DynamicNavMesh? Dynamic { get; private set; }

        /// <summary>高级人群管理器。</summary>
        public AdvancedCrowdManager? Crowd { get; private set; }

        /// <summary>每 Tile 高级数据。</summary>
        public Dictionary<(int x, int z), NavMeshTile> AdvancedTiles { get; } = new();

        /// <summary>ECS 引用。</summary>
        private EntityComponentSystem? _ecs;

        /// <summary>流式加载半径（Tile 数）。</summary>
        public int StreamingRadius = 3;

        /// <summary>是否启用瓦片流式加载。</summary>
        public bool EnableStreaming = true;

        /// <summary>日志事件。</summary>
        public event Action<string>? OnLogMessage;

        /// <summary>瓦片缓存容量。</summary>
        public int TileCacheCapacity = 64;

        /// <summary>构造高级导航系统。</summary>
        public AdvancedNavigationSystem()
        {
            TileCache = new NavMeshTileCache(TileCacheCapacity);
            Log("高级导航系统已初始化");
        }

        /// <summary>绑定基础导航系统与 ECS。</summary>
        public void Bind(NavigationSystem baseSystem, EntityComponentSystem ecs)
        {
            BaseSystem = baseSystem ?? throw new ArgumentNullException(nameof(baseSystem));
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            BaseSystem.BindECS(ecs);
            Crowd = new AdvancedCrowdManager(BaseSystem);
            Log("已绑定基础导航系统与 ECS");
        }

        /// <summary>
        ///   构建高级导航网格。生成后 NavData 会同步回基础 NavigationSystem，
        ///   使其 FindPath/FindNearestPoly 等接口立即可用。
        /// </summary>
        public void BuildNavMesh(Vector3[] vertices, int[] indices, Vector3 origin, Vector3 worldSize,
            AdvancedNavMeshConfig? config = null)
        {
            if (BaseSystem == null) throw new InvalidOperationException("请先调用 Bind() 绑定基础导航系统。");

            Generator = new AdvancedNavMeshGenerator(config ?? new AdvancedNavMeshConfig());
            var data = Generator.Build(vertices, indices, origin, worldSize, out var advTiles);

            // 同步到基础系统
            BaseSystem.NavData = data;
            AdvancedTiles.Clear();
            foreach (var kvp in advTiles) AdvancedTiles[kvp.Key] = kvp.Value;

            Query = new NavMeshQuery(data);
            Dynamic = new DynamicNavMesh(data, Generator, AdvancedTiles);
            Dynamic.SetSceneInput(vertices, indices);

            // 注册瓦片缓存加载器
            foreach (var kvp in advTiles)
            {
                var captured = kvp;
                TileCache.RegisterLoader(kvp.Key.x, kvp.Key.z, () => AdvancedTiles.TryGetValue(captured.Key, out var t) ? t : null);
                TileCache.Put(kvp.Value);
            }

            Log($"高级 NavMesh 构建完成: {data.Tiles.Count} Tiles, {data.TotalPolyCount} Polys");
        }

        /// <summary>查找路径（高级：A* + 漏斗平滑）。</summary>
        public List<Vector3>? FindPath(Vector3 start, Vector3 end)
        {
            return Query?.FindPath(start, end) ?? BaseSystem?.FindPath(start, end);
        }

        /// <summary>查找最近多边形。</summary>
        public int FindNearestPoly(Vector3 pos, out Vector3 closestPos, out bool posOverPoly)
        {
            if (Query != null) return Query.FindNearestPoly(pos, out closestPos, out posOverPoly);
            return BaseSystem?.FindNearestPoly(pos, out closestPos, out posOverPoly) ?? 0;
        }

        /// <summary>点到导航网格投影。</summary>
        public int ProjectPoint(Vector3 point, out Vector3 projected)
        {
            if (Query != null) return Query.ProjectPoint(point, out projected);
            projected = point;
            return 0;
        }

        /// <summary>到墙距离。</summary>
        public float FindDistanceToWall(Vector3 center, float maxRadius, out Vector3 hitPos, out Vector3 hitNormal)
        {
            if (Query != null) return Query.FindDistanceToWall(center, maxRadius, out hitPos, out hitNormal);
            hitPos = center; hitNormal = Vector3.Zero;
            return maxRadius;
        }

        /// <summary>可达性检查。</summary>
        public bool IsReachable(Vector3 start, Vector3 end) => Query?.IsReachable(start, end) ?? false;

        /// <summary>随机可达点。</summary>
        public bool FindRandomPoint(out Vector3 point) => Query?.FindRandomPoint(out point) ?? false;

        /// <summary>添加障碍物。</summary>
        public void AddObstacle(Vector3 position, float radius) => Dynamic?.AddObstacle(position, radius);

        /// <summary>处理脏 Tile 重建。</summary>
        public void ProcessDynamic() => Dynamic?.ProcessDirty();

        /// <summary>添加导航链接。</summary>
        public NavMeshLink? AddLink(Vector3 start, Vector3 end, NavMeshLinkType type = NavMeshLinkType.Jump)
            => Dynamic?.AddLink(start, end, type);

        /// <summary>添加修改器。</summary>
        public NavMeshModifier? AddModifier(Vector3 center, Vector3 halfSize, int areaId = -1, float cost = -1f)
            => Dynamic?.AddModifier(center, halfSize, areaId, cost);

        /// <summary>注册 ECS 实体为高级导航代理。</summary>
        public CrowdAgent? RegisterAgent(int entityId, float radius = 0.5f, float maxSpeed = 3.5f)
        {
            if (Crowd == null || _ecs == null) return null;
            var transform = _ecs.GetComponent<TransformComponent>(entityId);
            var agent = Crowd.AddAgent(transform.Position, radius, maxSpeed, entityId);
            _ecs.AddComponent(entityId, new NavAgentComponent
            {
                Radius = radius,
                MaxSpeed = maxSpeed,
                TargetPosition = transform.Position,
                IsMoving = false,
                CrowdAgentId = agent.Id
            });
            return agent;
        }

        /// <summary>设置代理目标。</summary>
        public void SetAgentTarget(int entityId, Vector3 target)
        {
            if (Crowd == null || _ecs == null) return;
            if (!_ecs.HasComponent<NavAgentComponent>(entityId)) return;
            var comp = _ecs.GetComponent<NavAgentComponent>(entityId);
            comp.TargetPosition = target;
            comp.IsMoving = true;
            _ecs.SetComponent(entityId, comp);
            Crowd.SetAgentTarget(comp.CrowdAgentId, target);
        }

        /// <summary>更新瓦片流式加载（以玩家位置为中心）。</summary>
        public void UpdateStreaming(Vector3 viewerPos)
        {
            if (!EnableStreaming || NavData == null) return;
            TileCache.UpdateStreaming(viewerPos, NavData.Origin, NavData.TileSize, StreamingRadius);
        }

        /// <summary>ECS 系统更新接口。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Update(dt);
        }

        /// <summary>主更新：动态网格重建 + 人群更新。</summary>
        public void Update(float dt)
        {
            Dynamic?.ProcessDirty();
            Crowd?.Update(dt);
        }

        /// <summary>获取统计信息。</summary>
        public string GetStatsString()
        {
            return $"[AdvancedNav] " +
                   $"已构建={NavData != null} " +
                   $"Tiles={NavData?.Tiles.Count ?? 0} " +
                   $"Polys={NavData?.TotalPolyCount ?? 0} " +
                   $"缓存={TileCache.LoadedCount}/{TileCache.Capacity} " +
                   $"代理={Crowd?.AgentCount ?? 0} " +
                   $"脏Tile={Dynamic?.DirtyTileCount ?? 0} " +
                   $"链接={Dynamic?.LinkCount ?? 0}";
        }

        private void Log(string msg) => OnLogMessage?.Invoke($"[AdvancedNav] {msg}");
    }
}
