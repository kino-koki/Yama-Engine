// =============================================================================
//  Navigation2D.cs
//  yama引擎 - 2D 网格寻路（对应 Godot 3 的 Navigation2D + AStar2D）
//
//  对应 Godot 3 语义：
//    * 网格地图（宽×高），每格可启用/禁用（障碍）
//    * set_point_disabled / set_point_solid：标记障碍
//    * id_path：A* 寻路返回格子 id 序列（起点→终点）
//    * get_simple_path：返回世界坐标点序列（格子中心）
//    * 8 方向移动（含对角），对角需两侧正邻都通行（防穿角）
//    * is_in_bounds / is_point_solid 查询
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>2D 网格导航（对应 Godot Navigation2D / AStar2D 的网格模式）。</summary>
    public sealed class Navigation2D
    {
        private readonly int _width;
        private readonly int _height;
        private readonly bool[] _solid;
        private float _defaultJumpHeight; // 预留（对角高差，本 2D 网格不用于代价）

        /// <summary>网格宽度（列数）。</summary>
        public int Width => _width;

        /// <summary>网格高度（行数）。</summary>
        public int Height => _height;

        /// <summary>格子总数。</summary>
        public int CellCount => _width * _height;

        /// <summary>构造 W×H 网格，初始全通行。</summary>
        public Navigation2D(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            _width = width;
            _height = height;
            _solid = new bool[width * height];
        }

        private int Idx(int x, int y) => y * _width + x;

        /// <summary>是否在网格范围内。</summary>
        public bool IsInBounds(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height;

        /// <summary>标记格子为障碍（对应 Godot set_point_disabled）。</summary>
        public void SetPointSolid(int x, int y) => _solid[Idx(x, y)] = true;

        /// <summary>清除格子障碍。</summary>
        public void ClearPointSolid(int x, int y) => _solid[Idx(x, y)] = false;

        /// <summary>格子是否为障碍。</summary>
        public bool IsPointSolid(int x, int y) => IsInBounds(x, y) && _solid[Idx(x, y)];

        /// <summary>批量设置一行障碍（如墙壁）。</summary>
        public void SetRowSolid(int y, int x0, int x1)
        {
            for (int x = x0; x <= x1; x++)
                if (IsInBounds(x, y)) _solid[Idx(x, y)] = true;
        }

        /// <summary>批量设置一列障碍。</summary>
        public void SetColumnSolid(int x, int y0, int y1)
        {
            for (int y = y0; y <= y1; y++)
                if (IsInBounds(x, y)) _solid[Idx(x, y)] = true;
        }

        /// <summary>
        /// A* 寻路：返回格子坐标序列（含起点终点），无路返回空列表。
        /// 8 方向移动，对角移动要求两侧正邻非障碍（防穿角）。
        /// </summary>
        public List<(int X, int Y)> IdPath(int startX, int startY, int endX, int endY)
        {
            var result = new List<(int, int)>();
            if (!IsInBounds(startX, startY) || !IsInBounds(endX, endY)) return result;
            if (IsPointSolid(startX, startY) || IsPointSolid(endX, endY)) return result;

            int start = Idx(startX, startY), end = Idx(endX, endY);
            int n = _solid.Length;
            var gScore = new float[n];
            var fScore = new float[n];
            var cameFrom = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; fScore[i] = float.PositiveInfinity; cameFrom[i] = -1; }
            gScore[start] = 0f;
            fScore[start] = Heuristic(startX, startY, endX, endY);

            // 二叉堆开放集（按 fScore 排序）
            var open = new MinHeap(n);
            open.Push(start, fScore[start]);

            while (open.Count > 0)
            {
                int current = open.Pop();
                if (current == end) return Reconstruct(cameFrom, current, _width, result);
                if (closed[current]) continue;
                closed[current] = true;

                int cx = current % _width, cy = current / _width;
                foreach (var (dx, dy) in Neighbors)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (!IsInBounds(nx, ny)) continue;
                    int ni = Idx(nx, ny);
                    if (IsPointSolid(nx, ny) || closed[ni]) continue;

                    // 对角防穿角：两侧正邻必须都非障碍
                    if (dx != 0 && dy != 0)
                    {
                        if (IsPointSolid(cx + dx, cy) || IsPointSolid(cx, cy + dy)) continue;
                    }

                    float stepCost = (dx != 0 && dy != 0) ? 1.4142f : 1f; // 对角 √2
                    float tentative = gScore[current] + stepCost;
                    if (tentative < gScore[ni])
                    {
                        cameFrom[ni] = current;
                        gScore[ni] = tentative;
                        fScore[ni] = tentative + Heuristic(nx, ny, endX, endY);
                        open.Push(ni, fScore[ni]);
                    }
                }
            }
            return result; // 无路
        }

        /// <summary>
        /// 简化路径（对应 Godot get_simple_path）：返回世界坐标点序列（格子中心）。
        /// 调用方提供格子中心换算（cellSize + offset）。
        /// </summary>
        public List<Vector2> GetSimplePath(
            (int X, int Y) start, (int X, int Y) end,
            float cellSize = 32f, Vector2 offset = default)
        {
            var path = new List<Vector2>();
            foreach (var (x, y) in IdPath(start.X, start.Y, end.X, end.Y))
            {
                path.Add(new Vector2(
                    offset.X + (x + 0.5f) * cellSize,
                    offset.Y + (y + 0.5f) * cellSize));
            }
            return path;
        }

        /// <summary>路径长度（跳数 = id_path.Count - 1）。</summary>
        public int PathLength((int X, int Y) start, (int X, int Y) end)
        {
            var p = IdPath(start.X, start.Y, end.X, end.Y);
            return p.Count > 0 ? p.Count - 1 : -1;
        }

        private static float Heuristic(int x0, int y0, int x1, int y1)
        {
            // 八方向启发：max(对角代价, 直线代价) 的切比雪夫近似（可采纳，2D 网格）
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int straight = Math.Abs(dx - dy);
            int diagonal = Math.Min(dx, dy);
            return straight + diagonal * 1.4142f;
        }

        private static List<(int, int)> Reconstruct(int[] cameFrom, int current, int width, List<(int, int)> result)
        {
            var stack = new List<int>();
            while (current != -1)
            {
                stack.Add(current);
                current = cameFrom[current];
            }
            stack.Reverse();
            foreach (int id in stack)
                result.Add((id % width, id / width));
            return result;
        }

        /// <summary>8 方向偏移（含对角）。</summary>
        private static readonly (int Dx, int Dy)[] Neighbors =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1),
        };

        /// <summary>简易二叉堆（按 key 最小出栈）。</summary>
        private sealed class MinHeap
        {
            private int[] _items;
            private float[] _keys;
            private int _count;

            public int Count => _count;

            public MinHeap(int capacity)
            {
                _items = new int[capacity + 1];
                _keys = new float[capacity + 1];
                _count = 0;
            }

            public void Push(int item, float key)
            {
                if (_count >= _keys.Length - 1)
                {
                    Array.Resize(ref _items, _items.Length * 2);
                    Array.Resize(ref _keys, _keys.Length * 2);
                }
                _count++;
                int i = _count;
                _items[i] = item; _keys[i] = key;
                while (i > 1 && _keys[i] < _keys[i / 2])
                {
                    (_items[i], _items[i / 2]) = (_items[i / 2], _items[i]);
                    (_keys[i], _keys[i / 2]) = (_keys[i / 2], _keys[i]);
                    i /= 2;
                }
            }

            public int Pop()
            {
                int top = _items[1];
                _items[1] = _items[_count]; _keys[1] = _keys[_count];
                _count--;
                int i = 1;
                while (true)
                {
                    int l = i * 2, r = l + 1, smallest = i;
                    if (l <= _count && _keys[l] < _keys[smallest]) smallest = l;
                    if (r <= _count && _keys[r] < _keys[smallest]) smallest = r;
                    if (smallest == i) break;
                    (_items[i], _items[smallest]) = (_items[smallest], _items[i]);
                    (_keys[i], _keys[smallest]) = (_keys[smallest], _keys[i]);
                    i = smallest;
                }
                return top;
            }
        }
    }
}
