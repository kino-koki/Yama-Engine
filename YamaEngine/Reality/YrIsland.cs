using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 约束岛（并查集按约束图连通分量）+ 休眠。
    /// 大场景里不分裂岛会让「一个远处物体在动」唤醒整世界，既慢又不正确；
    /// 休眠还要求按岛判定，否则静止堆叠会持续抖动。
    /// Union 一律取较小根，保证岛标签逐位确定、可回放。
    /// </summary>
    public sealed class YrIslandSolver
    {
        private int[] _parent = Array.Empty<int>();
        private int[] _labels = Array.Empty<int>();
        private int[] _particleToIsland = Array.Empty<int>();
        private int[] _quietFrames = Array.Empty<int>();
        private bool[] _sleeping = Array.Empty<bool>();
        private int _islandCount;
        private int _n;

        public int ParticleCount => _n;
        public int IslandCount => _islandCount;
        public int[] Labels => _labels;

        private int Find(int x)
        {
            int r = x;
            while (_parent[r] != r) r = _parent[r];
            while (_parent[x] != x) { int p = _parent[x]; _parent[x] = r; x = p; }
            return r;
        }
        private void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (ra < rb) _parent[rb] = ra; else _parent[ra] = rb;
        }

        public void Build(int n, (int A, int B)[] edges)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
            _n = n;
            _parent = new int[n];
            for (int i = 0; i < n; i++) _parent[i] = i;
            if (edges != null)
                for (int k = 0; k < edges.Length; k++)
                {
                    int a = edges[k].A, b = edges[k].B;
                    if (a < 0 || b < 0 || a >= n || b >= n) continue;
                    Union(a, b);
                }
            var roots = new System.Collections.Generic.SortedSet<int>();
            _particleToIsland = new int[n];
            for (int i = 0; i < n; i++) { int r = Find(i); roots.Add(r); _particleToIsland[i] = r; }
            _labels = new int[roots.Count];
            var map = new System.Collections.Generic.Dictionary<int, int>();
            int idx = 0;
            foreach (int r in roots) { _labels[idx] = r; map[r] = idx; idx++; }
            for (int i = 0; i < n; i++) _particleToIsland[i] = map[_particleToIsland[i]];
            _islandCount = roots.Count;
            _quietFrames = new int[_islandCount];
            _sleeping = new bool[_islandCount];
        }

        public int IslandOf(int particle)
            => particle >= 0 && particle < _n ? _particleToIsland[particle] : -1;

        /// <summary>按岛内最大速度推进休眠状态机。</summary>
        public void Update(float[] vx, float[] vy, float[] vz,
                           float speedThreshold, int framesToSleep)
        {
            if (vx == null) throw new ArgumentNullException(nameof(vx));
            var maxSpeed = new float[_islandCount];
            int n = Math.Min(_n, Math.Min(vx.Length, Math.Min(vy.Length, vz.Length)));
            for (int i = 0; i < n; i++)
            {
                int isl = _particleToIsland[i];
                if (isl < 0) continue;
                float s2 = vx[i] * vx[i] + vy[i] * vy[i] + vz[i] * vz[i];
                if (s2 > maxSpeed[isl]) maxSpeed[isl] = s2;
            }
            float thr2 = speedThreshold * speedThreshold;
            for (int k = 0; k < _islandCount; k++)
            {
                if (maxSpeed[k] < thr2) _quietFrames[k]++;
                else _quietFrames[k] = 0;
                if (_quietFrames[k] >= framesToSleep) _sleeping[k] = true;
            }
        }

        public void Wake(int particle)
        {
            int isl = IslandOf(particle);
            if (isl < 0) return;
            _quietFrames[isl] = 0;
            _sleeping[isl] = false;
        }
        public bool IsSleeping(int particle)
        {
            int isl = IslandOf(particle);
            return isl >= 0 && _sleeping[isl];
        }
    }
}
