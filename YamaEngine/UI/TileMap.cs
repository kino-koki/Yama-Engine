// =============================================================================
//  TileMap.cs
//  yama引擎 - 2D 瓦片地图（对应 Godot 的 TileMap + TileSet）
//
//  对应 Godot 3 的瓦片地图语义：
//    * TileSet：瓦片资源集合（每个瓦片 id → 纹理区域 + 自定义数据）
//    * TileMap：网格化放置瓦片（set_cell / get_cell）
//    * 网格坐标 ↔ 世界坐标（cell_size 网格尺寸，默认 16×16）
//    * 分层：一个 TileMap 有多个 layer（背景/地形/装饰）
//    * erase_cell：清除格子的瓦片
//    * clear_layer：清空整层
//    * local_to_map / map_to_local：格子↔本地坐标转换
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>单个瓦片定义（对应 Godot TileSet 的一个 tile）。</summary>
    public sealed class Tile
    {
        /// <summary>瓦片 id（在 TileSet 内唯一）。</summary>
        public int Id { get; set; }

        /// <summary>纹理路径。</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>纹理区域（源矩形，本地像素）。</summary>
        public Rect2 Region { get; set; }

        /// <summary>瓦片是否阻挡移动（供导航用，非碰撞）。</summary>
        public bool Solid { get; set; }

        /// <summary>自定义数据键值（对应 Godot tile custom data）。</summary>
        public Dictionary<string, object> CustomData { get; } = new();

        public Tile() { }
        public Tile(int id) { Id = id; }
    }

    /// <summary>瓦片集合（对应 Godot TileSet）。</summary>
    public sealed class TileSet
    {
        private readonly Dictionary<int, Tile> _tiles = new();

        /// <summary>网格单元尺寸（像素，默认 16×16）。</summary>
        public Vector2 CellSize { get; set; } = new(16f, 16f);

        /// <summary>瓦片数量。</summary>
        public int TileCount => _tiles.Count;

        /// <summary>添加或更新瓦片。</summary>
        public void AddTile(Tile tile) => _tiles[tile.Id] = tile;

        /// <summary>按 id 获取瓦片（无则 null）。</summary>
        public Tile? GetTile(int id) => _tiles.TryGetValue(id, out var t) ? t : null;

        /// <summary>移除瓦片。</summary>
        public bool RemoveTile(int id) => _tiles.Remove(id);

        /// <summary>是否存在该 id。</summary>
        public bool HasTile(int id) => _tiles.ContainsKey(id);
    }

    /// <summary>
    /// 瓦片地图（对应 Godot TileMap）。网格放置 + 层 + 坐标转换。
    /// </summary>
    public class TileMap : Node2D
    {
        /// <summary>瓦片集合。</summary>
        public TileSet TileSet { get; set; } = new();

        /// <summary>网格单元尺寸（像素，优先用 TileSet.CellSize）。</summary>
        public Vector2 CellSize => TileSet.CellSize;

        /// <summary>层数（对应 Godot TileMap 的 layer_count）。</summary>
        public int LayerCount { get; private set; }

        // 每层：格子坐标 → 瓦片 id
        private readonly List<Dictionary<Vector2I, int>> _layers = new();

        public TileMap(string name = "TileMap") : base(name)
        {
            AddLayer(); // 默认至少一层
        }

        /// <summary>添加一个新层，返回层索引。</summary>
        public int AddLayer()
        {
            _layers.Add(new Dictionary<Vector2I, int>());
            return LayerCount++;
        }

        /// <summary>设置某层某格的瓦片（对应 Godot set_cell）。</summary>
        public void SetCell(int layer, Vector2I coords, int tileId)
        {
            if (layer < 0 || layer >= LayerCount) return;
            if (tileId == -1)
            {
                _layers[layer].Remove(coords);
            }
            else
            {
                _layers[layer][coords] = tileId;
            }
        }

        /// <summary>获取某层某格的瓦片 id（空格返回 -1，对应 Godot get_cell）。</summary>
        public int GetCell(int layer, Vector2I coords)
        {
            if (layer < 0 || layer >= LayerCount) return -1;
            return _layers[layer].TryGetValue(coords, out var id) ? id : -1;
        }

        /// <summary>擦除某层某格（对应 Godot erase_cell）。</summary>
        public void EraseCell(int layer, Vector2I coords) => SetCell(layer, coords, -1);

        /// <summary>清空整层（对应 Godot clear_layer）。</summary>
        public void ClearLayer(int layer)
        {
            if (layer < 0 || layer >= LayerCount) return;
            _layers[layer].Clear();
        }

        /// <summary>某层使用的格子数。</summary>
        public int GetUsedCellsCount(int layer)
        {
            if (layer < 0 || layer >= LayerCount) return 0;
            return _layers[layer].Count;
        }

        /// <summary>某层是否在给定格子有瓦片。</summary>
        public bool HasCell(int layer, Vector2I coords)
        {
            if (layer < 0 || layer >= LayerCount) return false;
            return _layers[layer].ContainsKey(coords);
        }

        /// <summary>某层所有已用格子坐标。</summary>
        public List<Vector2I> GetUsedCells(int layer)
        {
            var result = new List<Vector2I>();
            if (layer < 0 || layer >= LayerCount) return result;
            result.AddRange(_layers[layer].Keys);
            return result;
        }

        /// <summary>格子坐标 → 本地坐标（格子左上角对应 Godot map_to_local）。</summary>
        public Vector2 MapToLocal(Vector2I coords)
        {
            return new Vector2(coords.X * CellSize.X, coords.Y * CellSize.Y);
        }

        /// <summary>本地坐标 → 格子坐标（向下取整，对应 Godot local_to_map）。</summary>
        public Vector2I LocalToMap(Vector2 localPos)
        {
            int cx = (int)MathF.Floor(localPos.X / CellSize.X);
            int cy = (int)MathF.Floor(localPos.Y / CellSize.Y);
            return new Vector2I(cx, cy);
        }

        /// <summary>世界坐标 → 格子坐标（考虑 TileMap 的全局变换）。</summary>
        public Vector2I WorldToMap(Vector2 worldPos)
        {
            var local = ToLocal(worldPos);
            return LocalToMap(local);
        }

        /// <summary>格子坐标 → 世界坐标（TileMap 无旋转/缩放时 = 全局位置 + 本地）。</summary>
        public Vector2 MapToWorld(Vector2I coords)
        {
            return ToGlobal(MapToLocal(coords));
        }
    }

    /// <summary>整数二维向量（格子坐标）。</summary>
    public readonly struct Vector2I : IEquatable<Vector2I>
    {
        public int X { get; }
        public int Y { get; }

        public Vector2I(int x, int y) { X = x; Y = y; }

        public bool Equals(Vector2I other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Vector2I v && Equals(v);
        public override int GetHashCode() => (X, Y).GetHashCode();
        public override string ToString() => $"({X}, {Y})";

        public static bool operator ==(Vector2I a, Vector2I b) => a.Equals(b);
        public static bool operator !=(Vector2I a, Vector2I b) => !a.Equals(b);

        public static Vector2I operator +(Vector2I a, Vector2I b) => new(a.X + b.X, a.Y + b.Y);
        public static Vector2I operator -(Vector2I a, Vector2I b) => new(a.X - b.X, a.Y - b.Y);
    }
}
