// =============================================================================
//  TileMapAutotile.cs
//  yama引擎 - 2D 瓦片地图自动瓦片/地形过渡（对应 Godot 3 的 autotile / terrain）
//
//  对应 Godot 3 的自动瓦片语义：
//    * 每个瓦片可声明 peering bits（8 邻居连接位）——模板掩码
//    * 填充时计算实际邻居掩码，挑选匹配度最高的候选瓦片
//    * TerrainSet：地形集合（命名地形，用于按谓词批量填充）
//    * GetAutoTileMask：8 邻居位掩码（N/NE/E/SE/S/SW/W/NW）
//    * PickTileForMask：贪心最优匹配（声明位 ⊆ 实际位，匹配位数最多）
//    * AutoFillLayer：按地形谓词批量计算并设置瓦片 id
// =============================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.UI
{
    /// <summary>8 邻居方向位（对应 Godot autotile 的 peering bits）。</summary>
    [Flags]
    public enum TileNeighbor : int
    {
        None = 0,
        N = 1 << 0,   // 上 (0,-1)
        NE = 1 << 1,  // 右上 (1,-1)
        E = 1 << 2,   // 右 (1,0)
        SE = 1 << 3,  // 右下 (1,1)
        S = 1 << 4,   // 下 (0,1)
        SW = 1 << 5,  // 左下 (-1,1)
        W = 1 << 6,   // 左 (-1,0)
        NW = 1 << 7,  // 左上 (-1,-1)
    }

    /// <summary>8 个方向（位掩码 → 坐标偏移）。顺序与 TileNeighbor 位一致。</summary>
    public static class TileNeighborDir
    {
        /// <summary>所有方向及其位值 + 偏移（供掩码计算枚举）。</summary>
        public static readonly (TileNeighbor Bit, int Dx, int Dy)[] All =
        {
            (TileNeighbor.N,  0, -1),
            (TileNeighbor.NE, 1, -1),
            (TileNeighbor.E,  1,  0),
            (TileNeighbor.SE, 1,  1),
            (TileNeighbor.S,  0,  1),
            (TileNeighbor.SW, -1, 1),
            (TileNeighbor.W, -1,  0),
            (TileNeighbor.NW, -1, -1),
        };
    }

    /// <summary>
    /// 自动瓦片候选（对应 Godot TileSet 里一个 tile 的 peering bits 模板）。
    /// TemplateMask 声明该瓦片"期望"哪些方向有同类邻居；实际掩码 ⊇ 模板即匹配。
    /// </summary>
    public sealed class AutoTileCandidate
    {
        /// <summary>目标瓦片 id（填入格子的瓦片）。</summary>
        public int TileId { get; set; }

        /// <summary>模板掩码（声明期望的邻居方向）。</summary>
        public TileNeighbor TemplateMask { get; set; }

        public AutoTileCandidate() { }
        public AutoTileCandidate(int tileId, TileNeighbor templateMask)
        {
            TileId = tileId;
            TemplateMask = templateMask;
        }
    }

    /// <summary>
    /// 地形自动瓦片规则集（对应 Godot TileSet 的 autotile 集合）。
    /// 一个地形规则 = 一组候选瓦片 + 匹配算法。
    /// </summary>
    public sealed class AutoTileRules
    {
        private readonly List<AutoTileCandidate> _candidates = new();

        /// <summary>候选瓦片数量。</summary>
        public int CandidateCount => _candidates.Count;

        /// <summary>添加候选模板瓦片。</summary>
        public void AddCandidate(int tileId, TileNeighbor templateMask)
            => _candidates.Add(new AutoTileCandidate(tileId, templateMask));

        /// <summary>取所有候选（只读）。</summary>
        public IReadOnlyList<AutoTileCandidate> Candidates => _candidates;

        /// <summary>
        /// 为给定实际掩码挑选最佳候选瓦片。
        /// 规则：模板位必须被实际掩码包含（template &amp; ~actual == 0），
        /// 在满足者中选拥有最多模板位的（最特异，对应 Godot 优先精确匹配）。
        /// 无满足者返回 -1（对应 Godot 找不到匹配时的空）。
        /// </summary>
        public int PickTileForMask(TileNeighbor actualMask)
        {
            int best = -1;
            int bestBits = -1;
            foreach (var c in _candidates)
            {
                // 模板的所有位都必须在实际掩码中
                if ((c.TemplateMask & ~actualMask) != 0)
                    continue;
                int bits = PopCount(c.TemplateMask);
                if (bits > bestBits)
                {
                    bestBits = bits;
                    best = c.TileId;
                }
            }
            return best;
        }

        /// <summary>数二进制 1 的个数（模板特异度）。</summary>
        private static int PopCount(TileNeighbor v)
        {
            int x = (int)v, n = 0;
            while (x != 0) { x &= x - 1; n++; }
            return n;
        }
    }

    /// <summary>
    /// TileMap 自动瓦片扩展（对应 Godot autotile 的运行时填充能力）。
    /// 用扩展方法 + 外部字典存规则，不侵入 TileMap 基类。
    /// </summary>
    public static class TileMapAutotileExtensions
    {
        private static readonly Dictionary<TileMap, AutoTileRules> _rules = new();

        /// <summary>设置某 TileMap 的自动瓦片规则集。</summary>
        public static void SetAutoTileRules(this TileMap map, AutoTileRules rules)
            => _rules[map] = rules;

        /// <summary>取自动瓦片规则集（无则 null）。</summary>
        public static AutoTileRules? GetAutoTileRules(this TileMap map)
            => _rules.TryGetValue(map, out var r) ? r : null;

        /// <summary>清除规则绑定。</summary>
        public static void ClearAutoTileRules(this TileMap map)
            => _rules.Remove(map);

        /// <summary>
        /// 计算某层某格的 8 邻居位掩码。
        /// 对每个方向，若该邻居格满足 isTerrain（返回 true）则置对应位。
        /// 空格/越界/非同类格视为"非地形"，对应方向位清零。
        /// </summary>
        public static TileNeighbor GetAutoTileMask(
            this TileMap map, int layer, Vector2I coords, Func<int, Vector2I, bool> isTerrain)
        {
            var mask = TileNeighbor.None;
            foreach (var (bit, dx, dy) in TileNeighborDir.All)
            {
                var n = new Vector2I(coords.X + dx, coords.Y + dy);
                if (isTerrain(layer, n))
                    mask |= bit;
            }
            return mask;
        }

        /// <summary>
        /// 批量自动填充一层：遍历所有 isTerrain 为 true 的格子，
        /// 计算掩码 → 挑候选瓦片 → SetCell。
        /// 仅对"区域内的格子"计算（bounds 为空则扫描已用格 + 掩码邻居）。
        /// 返回填充的格子数。
        /// </summary>
        public static int AutoFillLayer(
            this TileMap map, int layer,
            Func<int, Vector2I, bool> isTerrain,
            IEnumerable<Vector2I>? region = null)
        {
            var rules = map.GetAutoTileRules();
            if (rules == null) return 0;

            var cells = new List<Vector2I>();
            if (region != null)
            {
                cells.AddRange(region);
            }
            else
            {
                // 扫描所有 isTerrain 为 true 的格子
                foreach (var c in map.GetUsedCells(layer))
                {
                    if (isTerrain(layer, c)) cells.Add(c);
                }
            }

            int filled = 0;
            // 两遍填充：先对所有格子计算掩码并挑瓦片（快照），再统一写入。
            // 否则边填边算会让"已填成候选 id 的邻居"不再被 isTerrain 判为同类，
            // 污染后续格子的掩码（对应 Godot autotile 的整体重算语义）。
            var picks = new List<(Vector2I Cell, int TileId)>();
            foreach (var c in cells)
            {
                if (!isTerrain(layer, c)) continue;
                var mask = map.GetAutoTileMask(layer, c, isTerrain);
                int tileId = rules.PickTileForMask(mask);
                if (tileId != -1)
                {
                    picks.Add((c, tileId));
                    filled++;
                }
            }
            foreach (var (cell, tileId) in picks)
                map.SetCell(layer, cell, tileId);
            return filled;
        }

        /// <summary>取某格当前自动瓦片规则对应的瓦片 id（不写入，仅查询）。</summary>
        public static int PickAutoTile(this TileMap map, int layer, Vector2I coords,
            Func<int, Vector2I, bool> isTerrain)
        {
            var rules = map.GetAutoTileRules();
            if (rules == null) return -1;
            var mask = map.GetAutoTileMask(layer, coords, isTerrain);
            return rules.PickTileForMask(mask);
        }
    }
}
