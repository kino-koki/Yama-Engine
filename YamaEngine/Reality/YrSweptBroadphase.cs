using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 可规模化宽相：扫掠膨胀 AABB + 每帧重建的静态 BVH（中位分割）。
    ///
    /// 第六轮的 `EarliestToiAll` 是 O(n²) 全对扫掠，正确但无法承载大场景。
    /// 这里给每个刚体构造「整帧轨迹」的 AABB：
    ///   swept = 当前 AABB ∪ (当前 AABB + v·dt)，再按角速度做保守半径膨胀。
    /// 因为高速物体**当前分离但整帧轨迹相交**，用轨迹 AABB 才不会漏报；
    /// 这正是第六轮文档里「宽相基于当前 AABB 会直接漏掉穿透」的正面解法。
    ///
    /// BVH 用最长轴中位分割、叶容量 4，按 (中心坐标, 索引) 排序保证逐位确定；
    /// 每帧重建是显式选择（非增量动态树），对 1000+ 体足够且实现可验证。
    /// </summary>
    public sealed class YrSweptBroadphase
    {
        public const int LeafCapacity = 4;
        public const float FatMargin = 0.02f;

        private struct Node
        {
            public YrAabb Box;
            public int Left;
            public int Right;
            public int Start;
            public int Count;
        }

        private Node[] _nodes = Array.Empty<Node>();
        private int _nodeCount;
        private int _root = -1;
        private YrAabb[] _boxes = Array.Empty<YrAabb>();
        private int[] _order = Array.Empty<int>();

        public int NodeCount => _nodeCount;
        public int Height => _root < 0 ? 0 : HeightOf(_root);
        public YrAabb[] Boxes => _boxes;

        private int HeightOf(int idx)
        {
            Node n = _nodes[idx];
            if (n.Left < 0) return 1;
            int a = HeightOf(n.Left), b = HeightOf(n.Right);
            return 1 + (a > b ? a : b);
        }

        public static YrAabb Fat(in YrAabb b)
        {
            var m = new RVec3(FatMargin, FatMargin, FatMargin);
            return new YrAabb(b.Min - m, b.Max + m);
        }

        public static YrAabb Union(in YrAabb a, in YrAabb b)
            => new YrAabb(
                new RVec3(MathF.Min(a.Min.X, b.Min.X), MathF.Min(a.Min.Y, b.Min.Y),
                          MathF.Min(a.Min.Z, b.Min.Z)),
                new RVec3(MathF.Max(a.Max.X, b.Max.X), MathF.Max(a.Max.Y, b.Max.Y),
                          MathF.Max(a.Max.Z, b.Max.Z)));

        public static YrAabb Translate(in YrAabb b, RVec3 d)
            => new YrAabb(b.Min + d, b.Max + d);

        public static YrAabb Inflate(in YrAabb b, float m)
        {
            var e = new RVec3(m, m, m);
            return new YrAabb(b.Min - e, b.Max + e);
        }

        /// <summary>把 OBB 的整帧轨迹（含角速度保守膨胀）压成一个 AABB。</summary>
        public static YrAabb SweptBox(in YrObb o, RVec3 velocity, float omega, float dt)
        {
            YrAabb cur = YrBroadPhase.FromObb(o);
            YrAabb moved = Translate(cur, velocity * dt);
            YrAabb u = Union(cur, moved);
            float inflation = YrSweptObb.AngularInflation(omega, o.HalfExtents.Length(), dt);
            return Fat(Inflate(u, inflation));
        }

        /// <summary>构造整帧轨迹 AABB 数组（确定性，按索引升序）。</summary>
        public static YrAabb[] BuildSweptBoxes(YrObb[] obbs, RigidBody3D[] bodies, float dt)
        {
            if (obbs == null || bodies == null) throw new ArgumentNullException(nameof(obbs));
            int n = Math.Min(obbs.Length, bodies.Length);
            var boxes = new YrAabb[n];
            for (int i = 0; i < n; i++)
            {
                float omega = bodies[i].AngularVelocityWorld().Length();
                boxes[i] = SweptBox(obbs[i], bodies[i].LinearVelocity, omega, dt);
            }
            return boxes;
        }

        /// <summary>用给定 AABB 集合重建 BVH。</summary>
        public void Build(YrAabb[] boxes)
        {
            if (boxes == null) throw new ArgumentNullException(nameof(boxes));
            _boxes = (YrAabb[])boxes.Clone();
            int n = _boxes.Length;
            _order = new int[n];
            for (int i = 0; i < n; i++) _order[i] = i;
            // 容量上界：每个叶最多 1 个内部节点 → 2n
            _nodes = new Node[Math.Max(1, 2 * n + 1)];
            _nodeCount = 0;
            _root = n == 0 ? -1 : BuildRange(0, n);
        }

        private int BuildRange(int start, int count)
        {
            int idx = _nodeCount++;
            Node node = new Node { Left = -1, Right = -1, Start = start, Count = count };
            YrAabb box = _boxes[_order[start]];
            for (int k = 1; k < count; k++) box = Union(box, _boxes[_order[start + k]]);
            node.Box = box;
            if (count <= LeafCapacity)
            {
                _nodes[idx] = node;
                return idx;
            }

            RVec3 size = box.Max - box.Min;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0
                     : (size.Y >= size.Z ? 1 : 2);
            // 按 (中心坐标, 索引) 升序排序，索引 tie-break 保证确定性
            int a = axis;
            YrAabb[] boxesRef = _boxes;
            Array.Sort(_order, start, count, System.Collections.Generic.Comparer<int>.Create((x, y) => {
                float cx = CenterComponent(boxesRef[x], a);
                float cy = CenterComponent(boxesRef[y], a);
                if (cx < cy - 1e-9f) return -1;
                if (cx > cy + 1e-9f) return 1;
                return x.CompareTo(y);
            }));

            int mid = count / 2;
            int left = BuildRange(start, mid);
            int right = BuildRange(start + mid, count - mid);
            node.Left = left; node.Right = right;
            _nodes[idx] = node;
            return idx;
        }

        private static float CenterComponent(in YrAabb b, int axis)
            => axis == 0 ? (b.Min.X + b.Max.X) * 0.5f
             : axis == 1 ? (b.Min.Y + b.Max.Y) * 0.5f
                         : (b.Min.Z + b.Max.Z) * 0.5f;

        /// <summary>返回所有轨迹 AABB 重叠的候选对 (A&lt;B)，按字典序排序去重。</summary>
        public (int, int)[] QueryPairs()
        {
            var found = new System.Collections.Generic.HashSet<(int, int)>();
            if (_root >= 0) Traverse(_root, _root, found);
            var list = new System.Collections.Generic.List<(int, int)>(found);
            list.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1)
                                                   : x.Item2.CompareTo(y.Item2));
            return list.ToArray();
        }

        private void Traverse(int na, int nb,
                              System.Collections.Generic.HashSet<(int, int)> found)
        {
            if (!_nodes[na].Box.Overlaps(_nodes[nb].Box)) return;
            bool la = _nodes[na].Left < 0, lb = _nodes[nb].Left < 0;
            if (la && lb)
            {
                Node A = _nodes[na], B = _nodes[nb];
                for (int i = 0; i < A.Count; i++)
                    for (int j = 0; j < B.Count; j++)
                    {
                        int x = _order[A.Start + i], y = _order[B.Start + j];
                        if (x == y) continue;
                        int p = x < y ? x : y, q = x < y ? y : x;
                        if (_boxes[p].Overlaps(_boxes[q])) found.Add((p, q));
                    }
                return;
            }
            if (la)
            {
                Traverse(na, _nodes[nb].Left, found);
                Traverse(na, _nodes[nb].Right, found);
                return;
            }
            if (lb)
            {
                Traverse(_nodes[na].Left, nb, found);
                Traverse(_nodes[na].Right, nb, found);
                return;
            }
            Traverse(_nodes[na].Left, _nodes[nb].Left, found);
            Traverse(_nodes[na].Left, _nodes[nb].Right, found);
            Traverse(_nodes[na].Right, _nodes[nb].Left, found);
            Traverse(_nodes[na].Right, _nodes[nb].Right, found);
        }

        /// <summary>一步到位：由刚体状态得到整帧 CCD 候选对。</summary>
        public static (int, int)[] SweptPairs(YrObb[] obbs, RigidBody3D[] bodies, float dt)
        {
            var bvh = new YrSweptBroadphase();
            bvh.Build(BuildSweptBoxes(obbs, bodies, dt));
            return bvh.QueryPairs();
        }
    }
}
