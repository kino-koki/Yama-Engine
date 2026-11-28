using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 增量动态 AABB 树（Box2D b2DynamicTree 风格的确定性实现）。
    ///
    /// 第七轮的 `YrSweptBroadphase` 每帧从零重建整棵 BVH；这一轮改成真正增量：
    ///   · 每个刚体有稳定 leaf id，`Update(body, box)` 只在 fat AABB 包不住新盒时
    ///     才 Remove+Insert —— 静止/微动物体零开销；
    ///   · 最佳兄弟插入（合并面积增量最小）+ AVL 式旋转，树高保持对数；
    ///   · 自由链表复用节点，不产生每帧 GC 压力。
    ///
    /// 确定性：所有代价比较用严格小于 + 索引 tie-break；QueryPairs 输出按
    /// (A,B) 字典序排序去重；不使用 Dictionary 遍历顺序作为输出顺序。
    ///
    /// 诚实边界：叶盒是 fat AABB，因此候选对是「宽松超集」（可能多报，不漏报）；
    /// 与静态 BVH 用同一 Fat 余量，所以两者候选集可逐项 parity 校验。
    /// 这是 CPU 侧宽相结构，接 GPU 帧循环的压测仍留给真机。
    /// </summary>
    public sealed class YrDynamicAabbTree
    {
        public struct YrDynNode
        {
            public YrAabb Box;
            public int Parent;
            public int Child1;
            public int Child2;
            public int Body;      // 叶：刚体索引；内部节点：-1
            public int Height;    // 叶 = 0
        }

        private YrDynNode[] _nodes;
        private int _nodeCount;
        private int _freeList = -1;
        private int _root = -1;
        private int[] _leafOfBody;
        private int _capacity;

        public YrDynamicAabbTree(int capacity = 64)
        {
            _capacity = Math.Max(8, capacity);
            _nodes = new YrDynNode[2 * _capacity + 8];
            _nodeCount = 0;
            _leafOfBody = new int[_capacity];
            for (int i = 0; i < _leafOfBody.Length; i++) _leafOfBody[i] = -1;
        }

        public int Root => _root;
        public int NodeCount => _nodeCount;
        public int Height => _root < 0 ? 0 : _nodes[_root].Height + 1;
        public YrDynNode[] Nodes => _nodes;
        public int LeafOfBody(int body)
            => body >= 0 && body < _leafOfBody.Length ? _leafOfBody[body] : -1;

        private bool IsLeaf(int i) => _nodes[i].Child1 < 0;

        private static float Area(in YrAabb b)
        {
            float dx = b.Max.X - b.Min.X, dy = b.Max.Y - b.Min.Y, dz = b.Max.Z - b.Min.Z;
            return 2f * (dx * dy + dy * dz + dz * dx);
        }
        private static int Max(int a, int b) => a > b ? a : b;

        private int Allocate()
        {
            if (_freeList >= 0)
            {
                int i = _freeList;
                _freeList = _nodes[i].Child1;
                _nodes[i] = default;
                return i;
            }
            if (_nodeCount >= _nodes.Length)
            {
                var bigger = new YrDynNode[_nodes.Length * 2];
                Array.Copy(_nodes, bigger, _nodes.Length);
                _nodes = bigger;
            }
            return _nodeCount++;
        }

        private void Free(int i)
        {
            _nodes[i] = default;
            _nodes[i].Child1 = _freeList;
            _freeList = i;
        }

        private void EnsureBody(int body)
        {
            if (body < 0) throw new ArgumentOutOfRangeException(nameof(body));
            if (body >= _leafOfBody.Length)
            {
                int n = _leafOfBody.Length;
                while (n <= body) n *= 2;
                var bigger = new int[n];
                Array.Copy(_leafOfBody, bigger, _leafOfBody.Length);
                for (int i = _leafOfBody.Length; i < n; i++) bigger[i] = -1;
                _leafOfBody = bigger;
            }
        }

        /// <summary>插入一个刚体的 fat AABB，返回 leaf id。</summary>
        public int Insert(int body, in YrAabb box)
        {
            EnsureBody(body);
            if (_leafOfBody[body] >= 0) Remove(body);
            // 不在此处额外 Fat：叶盒必须与静态 BVH 的 _boxes 完全一致，
            // 否则 parity 测试会因 fat 余量不同而多报候选对。
            // fat 化由调用方决定（管线的 SweptBox 已含 Fat 余量）。
            YrAabb fat = box;
            int leaf = Allocate();
            _nodes[leaf].Box = fat;
            _nodes[leaf].Parent = -1;
            _nodes[leaf].Child1 = -1;
            _nodes[leaf].Child2 = -1;
            _nodes[leaf].Body = body;
            _nodes[leaf].Height = 0;
            _leafOfBody[body] = leaf;

            if (_root < 0) { _root = leaf; return leaf; }

            // 最佳兄弟：沿合并面积增量最小的分支下探
            int idx = _root;
            while (!IsLeaf(idx))
            {
                int c1 = _nodes[idx].Child1, c2 = _nodes[idx].Child2;
                float cost1 = Area(YrSweptBroadphase.Union(fat, _nodes[c1].Box))
                            - Area(_nodes[c1].Box);
                float cost2 = Area(YrSweptBroadphase.Union(fat, _nodes[c2].Box))
                            - Area(_nodes[c2].Box);
                if (cost1 < cost2 - 1e-9f) idx = c1;
                else if (cost2 < cost1 - 1e-9f) idx = c2;
                else idx = c1 < c2 ? c1 : c2;
            }

            int sibling = idx;
            int oldParent = _nodes[sibling].Parent;
            int newParent = Allocate();
            _nodes[newParent].Box = YrSweptBroadphase.Union(fat, _nodes[sibling].Box);
            _nodes[newParent].Parent = oldParent;
            _nodes[newParent].Child1 = sibling;
            _nodes[newParent].Child2 = leaf;
            _nodes[newParent].Body = -1;
            _nodes[newParent].Height = 1 + Max(_nodes[sibling].Height, 0);
            _nodes[sibling].Parent = newParent;
            _nodes[leaf].Parent = newParent;

            if (oldParent < 0) _root = newParent;
            else if (_nodes[oldParent].Child1 == sibling) _nodes[oldParent].Child1 = newParent;
            else _nodes[oldParent].Child2 = newParent;

            FixUpwards(newParent);
            return leaf;
        }

        /// <summary>移除刚体（幂等）。</summary>
        public void Remove(int body)
        {
            if (body < 0 || body >= _leafOfBody.Length) return;
            int leaf = _leafOfBody[body];
            if (leaf < 0) return;
            _leafOfBody[body] = -1;

            if (leaf == _root) { _root = -1; Free(leaf); return; }
            int parent = _nodes[leaf].Parent;
            int grand = _nodes[parent].Parent;
            int sibling = _nodes[parent].Child1 == leaf
                ? _nodes[parent].Child2 : _nodes[parent].Child1;

            if (grand >= 0)
            {
                if (_nodes[grand].Child1 == parent) _nodes[grand].Child1 = sibling;
                else _nodes[grand].Child2 = sibling;
                _nodes[sibling].Parent = grand;
                Free(parent);
                FixUpwards(grand);
            }
            else
            {
                _root = sibling;
                _nodes[sibling].Parent = -1;
                Free(parent);
            }
            Free(leaf);
        }

        /// <summary>
        /// 增量更新：fat 盒仍包住新盒时零操作；否则 Remove+Insert。
        /// 返回当前 leaf id。
        /// </summary>
        public int Update(int body, in YrAabb box)
        {
            EnsureBody(body);
            int leaf = _leafOfBody[body];
            if (leaf >= 0
                && _nodes[leaf].Box.Contains(box.Min, 0f)
                && _nodes[leaf].Box.Contains(box.Max, 0f))
                return leaf;
            return Insert(body, box);
        }

        private void FixUpwards(int start)
        {
            int idx = start;
            while (idx >= 0)
            {
                idx = Balance(idx);
                int c1 = _nodes[idx].Child1, c2 = _nodes[idx].Child2;
                if (c1 < 0 || c2 < 0) break;   // 旋转后可能变成叶，交给上层
                _nodes[idx].Box = YrSweptBroadphase.Union(_nodes[c1].Box, _nodes[c2].Box);
                _nodes[idx].Height = 1 + Max(_nodes[c1].Height, _nodes[c2].Height);
                idx = _nodes[idx].Parent;
            }
        }

        /// <summary>AVL 式单/双旋，返回旋转后子树根。</summary>
        private int Balance(int a)
        {
            if (a < 0 || IsLeaf(a) || _nodes[a].Height < 2) return a;
            int b = _nodes[a].Child1, c = _nodes[a].Child2;
            int balance = _nodes[c].Height - _nodes[b].Height;

            if (balance > 1)
            {
                int f = _nodes[c].Child1, g = _nodes[c].Child2;
                _nodes[c].Child1 = a;
                _nodes[c].Parent = _nodes[a].Parent;
                _nodes[a].Parent = c;
                if (_nodes[c].Parent >= 0)
                {
                    int p = _nodes[c].Parent;
                    if (_nodes[p].Child1 == a) _nodes[p].Child1 = c;
                    else _nodes[p].Child2 = c;
                }
                else _root = c;

                if (_nodes[f].Height > _nodes[g].Height)
                {
                    _nodes[c].Child2 = f;
                    _nodes[a].Child2 = g;
                    _nodes[g].Parent = a;
                    _nodes[a].Box = YrSweptBroadphase.Union(_nodes[b].Box, _nodes[g].Box);
                    _nodes[c].Box = YrSweptBroadphase.Union(_nodes[a].Box, _nodes[f].Box);
                    _nodes[a].Height = 1 + Max(_nodes[b].Height, _nodes[g].Height);
                    _nodes[c].Height = 1 + Max(_nodes[a].Height, _nodes[f].Height);
                }
                else
                {
                    _nodes[c].Child2 = g;
                    _nodes[a].Child2 = f;
                    _nodes[f].Parent = a;
                    _nodes[a].Box = YrSweptBroadphase.Union(_nodes[b].Box, _nodes[f].Box);
                    _nodes[c].Box = YrSweptBroadphase.Union(_nodes[a].Box, _nodes[g].Box);
                    _nodes[a].Height = 1 + Max(_nodes[b].Height, _nodes[f].Height);
                    _nodes[c].Height = 1 + Max(_nodes[a].Height, _nodes[g].Height);
                }
                return c;
            }

            if (balance < -1)
            {
                int d = _nodes[b].Child1, e = _nodes[b].Child2;
                _nodes[b].Child1 = a;
                _nodes[b].Parent = _nodes[a].Parent;
                _nodes[a].Parent = b;
                if (_nodes[b].Parent >= 0)
                {
                    int p = _nodes[b].Parent;
                    if (_nodes[p].Child1 == a) _nodes[p].Child1 = b;
                    else _nodes[p].Child2 = b;
                }
                else _root = b;

                if (_nodes[d].Height > _nodes[e].Height)
                {
                    _nodes[b].Child2 = d;
                    _nodes[a].Child1 = e;
                    _nodes[e].Parent = a;
                    _nodes[a].Box = YrSweptBroadphase.Union(_nodes[c].Box, _nodes[e].Box);
                    _nodes[b].Box = YrSweptBroadphase.Union(_nodes[a].Box, _nodes[d].Box);
                    _nodes[a].Height = 1 + Max(_nodes[c].Height, _nodes[e].Height);
                    _nodes[b].Height = 1 + Max(_nodes[a].Height, _nodes[e].Height);
                }
                else
                {
                    _nodes[b].Child2 = e;
                    _nodes[a].Child1 = d;
                    _nodes[d].Parent = a;
                    _nodes[a].Box = YrSweptBroadphase.Union(_nodes[c].Box, _nodes[d].Box);
                    _nodes[b].Box = YrSweptBroadphase.Union(_nodes[a].Box, _nodes[e].Box);
                    _nodes[a].Height = 1 + Max(_nodes[c].Height, _nodes[d].Height);
                    _nodes[b].Height = 1 + Max(_nodes[a].Height, _nodes[e].Height);
                }
                return b;
            }
            return a;
        }

        /// <summary>返回所有 fat AABB 重叠的候选对 (A&lt;B)，字典序排序去重。</summary>
        public (int, int)[] QueryPairs()
        {
            var found = new System.Collections.Generic.HashSet<(int, int)>();
            if (_root >= 0) Query(_root, _root, found);
            var list = new System.Collections.Generic.List<(int, int)>(found);
            list.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1)
                                                   : x.Item2.CompareTo(y.Item2));
            return list.ToArray();
        }

        private void Query(int na, int nb,
                           System.Collections.Generic.HashSet<(int, int)> found)
        {
            if (!_nodes[na].Box.Overlaps(_nodes[nb].Box)) return;
            bool la = IsLeaf(na), lb = IsLeaf(nb);
            if (la && lb)
            {
                if (na == nb) return;
                int x = _nodes[na].Body, y = _nodes[nb].Body;
                int p = x < y ? x : y, q = x < y ? y : x;
                found.Add((p, q));
                return;
            }
            // 自比较时跳过对称的 (R,L)，减少一半递归且结果不变
            if (na == nb)
            {
                int l = _nodes[na].Child1, r = _nodes[na].Child2;
                Query(l, l, found);
                Query(l, r, found);
                Query(r, r, found);
                return;
            }
            if (la)
            {
                Query(na, _nodes[nb].Child1, found);
                Query(na, _nodes[nb].Child2, found);
                return;
            }
            if (lb)
            {
                Query(_nodes[na].Child1, nb, found);
                Query(_nodes[na].Child2, nb, found);
                return;
            }
            int a1 = _nodes[na].Child1, a2 = _nodes[na].Child2;
            int b1 = _nodes[nb].Child1, b2 = _nodes[nb].Child2;
            Query(a1, b1, found); Query(a1, b2, found);
            Query(a2, b1, found); Query(a2, b2, found);
        }

        /// <summary>由 AABB 数组按索引顺序构建（供 parity 对照与初始化）。</summary>
        public static YrDynamicAabbTree RebuildFrom(YrAabb[] boxes)
        {
            var tree = new YrDynamicAabbTree(Math.Max(8, boxes.Length));
            for (int i = 0; i < boxes.Length; i++) tree.Insert(i, boxes[i]);
            return tree;
        }
    }
}
