using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// SoA 邻域网格。桶按 (dx,dy,dz) 固定顺序遍历、桶内按粒子索引升序插入，
    /// 因此 NeighborsOf(i) 的结果完全确定，可用于回放与快照校验。
    /// </summary>
    public sealed class SpatialHashGrid
    {
        private readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>> _buckets
            = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();
        private float _h = 1f;
        private int[] _cx = Array.Empty<int>();
        private int[] _cy = Array.Empty<int>();
        private int[] _cz = Array.Empty<int>();
        private int _count;

        public int Count => _count;
        public float CellSize => _h;

        private static long Key(int x, int y, int z)
        {
            // 每轴 21 位无符号，范围足够覆盖常规场景；组合无哈希冲突。
            return ((long)(uint)x << 42) | ((long)(uint)y << 21) | (uint)z;
        }

        /// <summary>用 SoA 位置数组重建网格（确定性）。</summary>
        public void Build(float[] px, float[] py, float[] pz, int n, float h)
        {
            if (px == null || py == null || pz == null) throw new ArgumentNullException(nameof(px));
            if (n < 0 || n > px.Length || n > py.Length || n > pz.Length)
                throw new ArgumentOutOfRangeException(nameof(n));
            _h = h > 1e-6f ? h : 1f;
            _count = n;
            _buckets.Clear();
            if (_cx.Length < n) { _cx = new int[n]; _cy = new int[n]; _cz = new int[n]; }
            float inv = 1f / _h;
            for (int i = 0; i < n; i++)
            {
                int x = (int)MathF.Floor(px[i] * inv);
                int y = (int)MathF.Floor(py[i] * inv);
                int z = (int)MathF.Floor(pz[i] * inv);
                _cx[i] = x; _cy[i] = y; _cz[i] = z;
                long k = Key(x, y, z);
                if (!_buckets.TryGetValue(k, out var list))
                {
                    list = new System.Collections.Generic.List<int>();
                    _buckets.Add(k, list);
                }
                list!.Add(i); // i 递增 → 桶内天然有序
            }
        }

        /// <summary>返回 i 的 27 邻桶内所有粒子（不含自身），按索引升序。</summary>
        public int[] NeighborsOf(int i)
        {
            if (i < 0 || i >= _count) return Array.Empty<int>();
            var acc = new System.Collections.Generic.List<int>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (_buckets.TryGetValue(Key(_cx[i] + dx, _cy[i] + dy, _cz[i] + dz), out var list))
                            for (int t = 0; t < list!.Count; t++)
                                if (list[t] != i) acc.Add(list[t]);
                    }
            acc.Sort(); // 消除 27 桶遍历顺序带来的乱序
            return acc.ToArray();
        }
    }
}
