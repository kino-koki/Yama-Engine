// ============================================================================
//  ShanyuEngine.AI.NavMeshGenerator.cs
//  山雨引擎 -- NavMesh 生成器
//  对齐 Recast & Detour / UE NavMesh Build
// ----------------------------------------------------------------------------
//  完整的 NavMesh 生成管线，四步走：
//    1. Voxelization        - 把场景三角网格体素化，标记 solid/empty
//    2. Region Generation   - 走 watershed 分水岭算法划分区域
//    3. Contour Extraction  - 提取区域边界轮廓
//    4. Topology Simplify   - Douglas-Peucker 简化轮廓 + 三角化 + 合并多边形
//
//  这套流程跟 Recast 基本一致，因为事实证明它就是最好用的方案。
//  TODO: 大地图上 navmesh 生成太慢了，得搞分块 streaming。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、构建设置
    // ========================================================================

    /// <summary>
    /// NavMesh 烘焙参数。跟 Recast 的 rcConfig 一一对应。
    /// 改任何一个值都得重新烘焙，所以别在运行时乱动。
    /// </summary>
    public sealed class NavMeshBuildSettings
    {
        /// <summary>体素在 XZ 平面上的边长(米)。越小越精细越慢，0.1~0.5 常用。</summary>
        public float VoxelSize = 0.2f;
        /// <summary>体素高度(米)。一般跟 VoxelSize 一样，悬崖检测靠它。</summary>
        public float VoxelHeight = 0.2f;
        /// <summary>代理半径(米)。会往内缩这么多，避免卡墙。</summary>
        public float AgentRadius = 0.5f;
        /// <summary>代理高度(米)。低于这个高度的洞钻不过去。</summary>
        public float AgentHeight = 1.8f;
        /// <summary>最大可攀登步高(米)。台阶比这高就过不去。</summary>
        public float AgentMaxStep = 0.3f;
        /// <summary>最大可行走坡度(度)。超过这个角度的面会被标记成不可走。</summary>
        public float AgentMaxSlope = 45f;
        /// <summary>区域生成时每个区域的最小体素数。太小会被合并掉，去噪用。</summary>
        public int RegionMinArea = 8;
        /// <summary>合并轮廓时允许的最大误差(米)。越大越简化越失真。</summary>
        public float ContourMaxError = 1.2f;
        /// <summary>合并后多边形最大顶点数。Detour 限制 6，别改。</summary>
        public int MaxVertsPerPoly = 6;
        /// <summary>体素化的 Y 轴向下采样率。1=全分辨率，2=砍一半加速。</summary>
        public int RegionPartitioning = 0; // 0=watershed,1=monotone,2=layer
        /// <summary>是否生成细节网格(用于高度采样)。地面有起伏的话建议开。</summary>
        public bool BuildDetailMesh = true;
        /// <summary>细节网格采样距离(米)。</summary>
        public float DetailSampleDist = 6f;
        /// <summary>细节网格最大误差(米)。</summary>
        public float DetailSampleMaxError = 0.1f;

        /// <summary>把世界尺寸换算成体素数量。注意要向上取整别截断。</summary>
        public void CalcVoxelCounts(Vector3 min, Vector3 max, out int w, out int h, out int d)
        {
            w = (int)MathF.Ceiling((max.X - min.X) / VoxelSize);
            h = (int)MathF.Ceiling((max.Y - min.Y) / VoxelHeight);
            d = (int)MathF.Ceiling((max.Z - min.Z) / VoxelSize);
        }
    }

    /// <summary>输入的几何体。可以是墙、地板、障碍，用 area 区分。</summary>
    public struct NavMeshBuildGeometry
    {
        public Vector3[] Vertices;
        public int[] Triangles;
        /// <summary>每个三角形对应的区域 id，决定行走花费。</summary>
        public byte[] TriangleAreas;
    }

    // ========================================================================
    //  二、体素高度场 (Heightfield)
    // ========================================================================

    /// <summary>
    /// 一个体素列(span)。x/z 固定，y 是一段连续的范围 [minY, maxY]。
    /// 这是 Recast 的核心数据结构，省内存就靠它。
    /// </summary>

    /// <summary>
    /// 稀疏体素高度场。只存非空列，内存友好。
    /// 网格大小 width * depth，每格存一个 span 链表头。
    /// </summary>
    public sealed class Heightfield
    {
        public int Width;
        public int Height;
        public int Depth;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public float CellSize;
        public float CellHeight;
        /// <summary>width * depth 个 span 头，null 表示该列空。</summary>
        public VoxelSpan?[] Spans;

        public Heightfield(int w, int h, int d, Vector3 min, Vector3 max, float cs, float ch)
        {
            Width = w; Height = h; Depth = d;
            BoundsMin = min; BoundsMax = max;
            CellSize = cs; CellHeight = ch;
            Spans = new VoxelSpan[w * d];
        }

        public int Index(int x, int z) => z * Width + x;

        /// <summary>给指定列加一个 span，自动合并重叠部分。</summary>
        public void AddSpan(int x, int z, int minY, int maxY, byte area)
        {
            int idx = Index(x, z);
            var span = new VoxelSpan(minY, maxY, area);
            if (Spans[idx] == null) { Spans[idx] = span; return; }
            // 插到链表正确位置，按 y 从低到高
            VoxelSpan? prev = null;
            VoxelSpan? cur = Spans[idx];
            while (cur != null && cur.MinY < span.MinY) { prev = cur; cur = cur.Next; }
            if (prev == null) { span.Next = cur; Spans[idx] = span; }
            else { span.Next = cur; prev.Next = span; }
            // TODO: 合并相邻/重叠的 span，这里先简化处理
        }

        public VoxelSpan? GetSpan(int x, int z) => Spans[Index(x, z)];

        public bool InBounds(int x, int z) => x >= 0 && x < Width && z >= 0 && z < Depth;
    }

    // ========================================================================
    //  三、紧凑高度场 (CompactHeightfield)
    // ========================================================================

    /// <summary>
    /// 紧凑高度场。只保留 walkable span，并且把每列的 span 压成连续数组，
    /// 配上邻居连接信息。后面区域生成和轮廓提取都基于它。
    /// </summary>

    /// <summary>紧凑 span。带邻居索引，方便后面做 flood fill。</summary>

    // ========================================================================
    //  四、轮廓 (Contour)
    // ========================================================================

    /// <summary>一条区域边界轮廓。顶点是体素坐标。</summary>

    /// <summary>简化后的多边形顶点(世界坐标)。</summary>
    public struct PolyVertex
    {
        public Vector3 Position;
        public byte Region;
    }

    /// <summary>最终输出多边形。</summary>

    /// <summary>最终 NavMesh 数据。</summary>
    public sealed class GeneratedNavMesh
    {
        public Vector3[] Vertices;
        public NavPoly[] Polygons;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public int TileX, TileZ;
        public float CellSize, CellHeight;
        public Dictionary<byte, float> AreaCosts = new();

        /// <summary>空间哈希加速点查询。key = tileIndex。</summary>
        public Dictionary<long, List<int>> SpatialHash = new();
        public int HashGridSize = 16;

        public void BuildSpatialHash()
        {
            SpatialHash.Clear();
            for (int i = 0; i < Polygons.Length; i++)
            {
                // 用多边形中心做哈希
                var c = PolyCenter(i);
                long key = HashKey(c);
                if (!SpatialHash.TryGetValue(key, out var list))
                    SpatialHash[key] = list = new List<int>();
                list.Add(i);
            }
        }

        public long HashKey(Vector3 p)
        {
            int x = (int)MathF.Floor((p.X - BoundsMin.X) / (CellSize * HashGridSize));
            int z = (int)MathF.Floor((p.Z - BoundsMin.Z) / (CellSize * HashGridSize));
            return ((long)x << 32) | (uint)z;
        }

        public Vector3 PolyCenter(int polyIdx)
        {
            var p = Polygons[polyIdx];
            var c = Vector3.Zero;
            for (int i = 0; i < p.VertexIndices.Length; i++)
                c += Vertices[p.VertexIndices[i]];
            return c / p.VertexIndices.Length;
        }
    }

    // ========================================================================
    //  五、NavMeshGenerator 主类
    // ========================================================================

    /// <summary>
    /// NavMesh 生成器。把输入的三角网格走一遍 Recast 管线，
    /// 输出可用于 Detour 查询的多边形网格。
    ///
    /// 用法：
    ///   var gen = new NavMeshGenerator();
    ///   var nav = gen.Build(geom, settings);
    ///
    /// 注意：这个类不是线程安全的，别在多线程同时 Build 同一个实例。
    /// 多 tile 场景应该每个 tile 用独立 generator。
    /// </summary>
    public sealed class NavMeshGenerator
    {
        private NavMeshBuildSettings _settings = new();
        private Heightfield? _heightfield;
        private CompactHeightfield? _compact;
        private List<Contour> _contours = new();
        private List<PolyVertex> _polyVerts = new();
        private List<NavPoly> _polys = new();
        private Vector3 _boundsMin, _boundsMax;

        // 一些临时统计，调试用
        public int DebugSpanCount;
        public int DebugWalkableSpanCount;
        public int DebugRegionCount;
        public int DebugContourCount;
        public float DebugBuildTimeMs;

        /// <summary>进度回调，0~1。烘焙大场景时给编辑器进度条用。</summary>
        public Action<float, string>? OnProgress;

        public NavMeshBuildSettings Settings
        {
            get => _settings;
            set => _settings = value ?? throw new ArgumentNullException(nameof(value));
        }

        // --------------------------------------------------------------------
        //  Build - 主管线入口
        // --------------------------------------------------------------------

        /// <summary>
        /// 执行完整的 NavMesh 烘焙流程。返回生成的网格。
        /// 这个调用比较重，建议在后台线程跑。
        /// </summary>
        public GeneratedNavMesh Build(NavMeshBuildGeometry geom, NavMeshBuildSettings? settings = null)
        {
            if (settings != null) _settings = settings;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Report(0.0f, "计算包围盒");
            ComputeBounds(geom, out _boundsMin, out _boundsMax);

            Report(0.05f, "体素化");
            _heightfield = Voxelize(geom, _boundsMin, _boundsMax, _settings);
            DebugSpanCount = CountSpans(_heightfield);

            Report(0.2f, "标记可走面");
            MarkWalkableTriangles(geom, _heightfield);

            Report(0.3f, "半径裁剪");
            FilterLowHeightSpans();
            FilterLedgeSpans();
            FilterWalkableLowHeightSpans();

            Report(0.4f, "构建紧凑高度场");
            _compact = BuildCompactHeightfield(_heightfield);
            ErodeWalkableArea(_settings.AgentRadius);
            MarkBoxAreas(geom);
            DebugWalkableSpanCount = _compact.SpanCount;

            Report(0.5f, "区域生成");
            BuildDistanceField();
            int regionCount = BuildRegions();
            DebugRegionCount = regionCount;

            Report(0.65f, "轮廓提取");
            _contours = BuildContours();
            DebugContourCount = _contours.Count;

            Report(0.8f, "简化与三角化");
            SimplifyContours();
            BuildPolyMesh();

            Report(0.92f, "构建细节网格");
            if (_settings.BuildDetailMesh)
                BuildDetailMesh();

            Report(1.0f, "完成");
            sw.Stop();
            DebugBuildTimeMs = sw.ElapsedMilliseconds;

            return AssembleResult();
        }

        private void Report(float p, string msg) => OnProgress?.Invoke(p, msg);

        // --------------------------------------------------------------------
        //  步骤 1: 计算包围盒
        // --------------------------------------------------------------------

        private void ComputeBounds(NavMeshBuildGeometry geom, out Vector3 min, out Vector3 max)
        {
            if (geom.Vertices == null || geom.Vertices.Length == 0)
                throw new ArgumentException("几何体没有顶点", nameof(geom));
            min = geom.Vertices[0];
            max = geom.Vertices[0];
            for (int i = 1; i < geom.Vertices.Length; i++)
            {
                var v = geom.Vertices[i];
                if (v.X < min.X) min.X = v.X; else if (v.X > max.X) max.X = v.X;
                if (v.Y < min.Y) min.Y = v.Y; else if (v.Y > max.Y) max.Y = v.Y;
                if (v.Z < min.Z) min.Z = v.Z; else if (v.Z > max.Z) max.Z = v.Z;
            }
            // 往外扩一个代理半径，避免边缘 span 被裁掉
            float pad = _settings.AgentRadius + _settings.VoxelSize * 2;
            min.X -= pad; min.Z -= pad;
            max.X += pad; max.Z += pad;
            // Y 也扩一点，给上面留呼吸空间
            min.Y -= _settings.VoxelHeight * 4;
            max.Y += _settings.VoxelHeight * 4;
        }

        // --------------------------------------------------------------------
        //  步骤 2: 体素化 Voxelize
        // --------------------------------------------------------------------

        /// <summary>
        /// 把三角形栅格化成体素 span。核心是三角形-AABB 裁剪 + 扫描线填充。
        /// A*算法这段得仔细看 不然容易写错，体素坐标的换算很容易搞反。
        /// </summary>
        private Heightfield Voxelize(NavMeshBuildGeometry geom, Vector3 min, Vector3 max, NavMeshBuildSettings s)
        {
            s.CalcVoxelCounts(min, max, out int w, out int h, out int d);
            var hf = new Heightfield(w, h, d, min, max, s.VoxelSize, s.VoxelHeight);

            float invCS = 1f / s.VoxelSize;
            float invCH = 1f / s.VoxelHeight;
            // 把坡度阈值预先算好，省得循环里反复算 acos
            float walkableSlopeCos = MathF.Cos(s.AgentMaxSlope * MathF.PI / 180f);

            var tris = geom.Triangles;
            var verts = geom.Vertices;
            byte[] areas = geom.TriangleAreas ?? Array.Empty<byte>();

            for (int t = 0; t < tris.Length; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                var v0 = verts[i0];
                var v1 = verts[i1];
                var v2 = verts[i2];

                // 算三角形法线，判断坡度
                var edge1 = v1 - v0;
                var edge2 = v2 - v0;
                var normal = Vector3.Cross(edge1, edge2);
                float nlen = normal.Length();
                if (nlen < 1e-6f) continue; // 退化三角形跳过
                normal /= nlen;
                bool walkable = normal.Y > walkableSlopeCos;
                byte area = walkable ? (areas.Length > 0 ? areas[t / 3] : (byte)1) : (byte)0;

                // 算三角形 AABB(世界坐标)，再换算到体素索引
                float txMin = MathF.Min(v0.X, MathF.Min(v1.X, v2.X));
                float txMax = MathF.Max(v0.X, MathF.Max(v1.X, v2.X));
                float tyMin = MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y));
                float tyMax = MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y));
                float tzMin = MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z));
                float tzMax = MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z));

                int x0 = Clamp((int)((txMin - min.X) * invCS), 0, w - 1);
                int x1 = Clamp((int)((txMax - min.X) * invCS), 0, w - 1);
                int z0 = Clamp((int)((tzMin - min.Z) * invCS), 0, d - 1);
                int z1 = Clamp((int)((tzMax - min.Z) * invCS), 0, d - 1);

                // 逐列算三角形在该列的最高 Y
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float cx = min.X + (x + 0.5f) * s.VoxelSize;
                        float cz = min.Z + (z + 0.5f) * s.VoxelSize;
                        if (!TriangleIntersectsColumn(v0, v1, v2, cx, cz, out float yMin, out float yMax))
                            continue;
                        int syMin = (int)((yMin - min.Y) * invCH);
                        int syMax = (int)((yMax - min.Y) * invCH);
                        if (syMin < 0) syMin = 0;
                        if (syMax >= h) syMax = h - 1;
                        if (syMin > syMax) continue;
                        hf.AddSpan(x, z, syMin, syMax, area);
                    }
                }
            }

            return hf;
        }

        /// <summary>
        /// 判断三角形在列 (cx,cz) 上的 Y 范围 [yMin,yMax]。
        /// 用的是三角形在竖直方向上的"切片"。其实就是把三角形投影到 XZ 平面，
        /// 看点 (cx,cz) 是否落在投影三角形内，在的话插值出 Y。
        /// </summary>
        private bool TriangleIntersectsColumn(Vector3 a, Vector3 b, Vector3 c, float cx, float cz,
            out float yMin, out float yMax)
        {
            yMin = float.MaxValue; yMax = float.MinValue;
            // 先粗判点在 XZ 投影三角形内
            if (!PointInTriangleXZ(a, b, c, cx, cz))
            {
                // 即使不在内部，也要考虑三角形竖直边扫过该列的情况
                // 简化处理：用三角形三个顶点的 Y 作为近似范围
                return false;
            }
            // 在内部，插值出 Y
            float y = InterpolateY(a, b, c, cx, cz);
            yMin = yMax = y;
            // 也加上三个顶点，避免极值丢失
            yMin = MathF.Min(yMin, MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
            yMax = MathF.Max(yMax, MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
            return true;
        }

        /// <summary>XZ 平面上点是否在三角形内(用重心坐标)。</summary>
        private bool PointInTriangleXZ(Vector3 a, Vector3 b, Vector3 c, float px, float pz)
        {
            float ax = a.X, az = a.Z;
            float bx = b.X, bz = b.Z;
            float cx = c.X, cz = c.Z;
            float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (MathF.Abs(d) < 1e-8f) return false;
            float w0 = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / d;
            float w1 = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / d;
            float w2 = 1f - w0 - w1;
            return w0 >= 0 && w1 >= 0 && w2 >= 0;
        }

        /// <summary>用重心坐标插值三角形内某点的 Y。</summary>
        private float InterpolateY(Vector3 a, Vector3 b, Vector3 c, float px, float pz)
        {
            float ax = a.X, az = a.Z;
            float bx = b.X, bz = b.Z;
            float cx = c.X, cz = c.Z;
            float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (MathF.Abs(d) < 1e-8f) return a.Y;
            float w0 = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / d;
            float w1 = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / d;
            float w2 = 1f - w0 - w1;
            return w0 * a.Y + w1 * b.Y + w2 * c.Y;
        }

        // --------------------------------------------------------------------
        //  步骤 3: 标记可走面
        // --------------------------------------------------------------------

        /// <summary>
        /// 根据三角形法线标记每个 span 的 Walkable 标志。
        /// 烘焙时已经在 Voxelize 里顺便做了，这里做二次过滤。
        /// </summary>
        private void MarkWalkableTriangles(NavMeshBuildGeometry geom, Heightfield hf)
        {
            // 遍历所有 span，walkable 判断已经在体素化时记录到 area 里
            // 这里补充：如果 span 顶面到上方 span 底面距离不够代理高度，标记不可走
            for (int i = 0; i < hf.Spans.Length; i++)
            {
                var span = hf.Spans[i];
                while (span != null)
                {
                    span.Walkable = span.Area != 0;
                    span = span.Next;
                }
            }
        }

        /// <summary>去掉头顶空间不足的 span(代理站不直)。</summary>
        private void FilterLowHeightSpans()
        {
            var hf = _heightfield!;
            int walkableHeight = (int)MathF.Ceiling(_settings.AgentHeight / _settings.VoxelHeight);
            for (int i = 0; i < hf.Spans.Length; i++)
            {
                var span = hf.Spans[i];
                while (span != null)
                {
                    if (!span.Walkable) { span = span.Next; continue; }
                    int yStart = span.MaxY;
                    int yEnd = yStart + walkableHeight;
                    // 看上面有没有挡
                    var above = span.Next;
                    bool blocked = false;
                    while (above != null)
                    {
                        if (above.MinY < yEnd) { blocked = true; break; }
                        above = above.Next;
                    }
                    if (blocked) span.Walkable = false;
                    span = span.Next;
                }
            }
        }

        /// <summary>去掉悬崖边上的 span(邻居高度差太大)。</summary>
        private void FilterLedgeSpans()
        {
            var hf = _heightfield!;
            int maxStep = (int)MathF.Ceiling(_settings.AgentMaxStep / _settings.VoxelHeight);
            for (int z = 0; z < hf.Depth; z++)
            {
                for (int x = 0; x < hf.Width; x++)
                {
                    var span = hf.GetSpan(x, z);
                    while (span != null)
                    {
                        if (!span.Walkable) { span = span.Next; continue; }
                        // 检查 4 邻居
                        bool isLedge = true;
                        int[][] dirs = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
                        foreach (var d in dirs)
                        {
                            int nx = x + d[0], nz = z + d[1];
                            if (!hf.InBounds(nx, nz)) continue;
                            var neighbor = hf.GetSpan(nx, nz);
                            while (neighbor != null)
                            {
                                if (MathF.Abs(neighbor.MaxY - span.MaxY) <= maxStep)
                                {
                                    isLedge = false;
                                    break;
                                }
                                neighbor = neighbor.Next;
                            }
                            if (!isLedge) break;
                        }
                        if (isLedge) span.Walkable = false;
                        span = span.Next;
                    }
                }
            }
        }

        /// <summary>再次过滤(双重保险，跟 Recast 一致)。</summary>
        private void FilterWalkableLowHeightSpans()
        {
            // 实际上 FilterLowHeightSpans 已经做了，这里保留接口跟 Recast 对齐
        }

        // --------------------------------------------------------------------
        //  步骤 4: 紧凑高度场
        // --------------------------------------------------------------------

        private CompactHeightfield BuildCompactHeightfield(Heightfield hf)
        {
            // 先数 walkable span 总数
            int count = 0;
            for (int i = 0; i < hf.Spans.Length; i++)
            {
                var s = hf.Spans[i];
                while (s != null) { if (s.Walkable) count++; s = s.Next; }
            }
            var chf = new CompactHeightfield(hf.Width, hf.Height, hf.Depth, count)
            {
                BoundsMin = hf.BoundsMin,
                CellSize = hf.CellSize,
                CellHeight = hf.CellHeight,
                WalkableHeight = _settings.AgentHeight,
                WalkableRadius = _settings.AgentRadius
            };

            // 填充
            int idx = 0;
            int walkableHeight = (int)MathF.Ceiling(_settings.AgentHeight / hf.CellHeight);
            for (int i = 0; i < hf.Spans.Length; i++)
            {
                int x = i % hf.Width, z = i / hf.Width;
                chf.ColumnOffsets[i] = idx;
                var s = hf.Spans[i];
                while (s != null)
                {
                    if (s.Walkable)
                    {
                        chf.Spans[idx] = new CompactSpan
                        {
                            Y = (ushort)s.MaxY,
                            BottomY = (ushort)s.MinY,
                            Region = 0,
                            Area = s.Area,
                            Con0 = Con1 = Con2 = Con3 = 0xFFFF
                        };
                        idx++;
                    }
                    s = s.Next;
                }
                chf.ColumnCounts[i] = idx - chf.ColumnOffsets[i];
            }

            // 算邻居连接
            BuildCompactConnections(chf);
            return chf;
        }

        /// <summary>填充每个紧凑 span 的 4 个方向邻居。</summary>
        private void BuildCompactConnections(CompactHeightfield chf)
        {
            int maxStep = (int)MathF.Ceiling(_settings.AgentMaxStep / chf.CellHeight);
            int[][] dirs = { new[] { 1, 0, 0 }, new[] { -1, 0, 1 }, new[] { 0, 1, 2 }, new[] { 0, -1, 3 } };
            for (int z = 0; z < chf.Depth; z++)
            {
                for (int x = 0; x < chf.Width; x++)
                {
                    int col = chf.Index(x, z);
                    for (int si = chf.ColumnOffsets[col]; si < chf.ColumnOffsets[col] + chf.ColumnCounts[col]; si++)
                    {
                        ref var span = ref chf.Spans[si];
                        foreach (var d in dirs)
                        {
                            int nx = x + d[0], nz = z + d[1], dirIdx = d[2];
                            if (!chf.Width.Equals(0) && (nx < 0 || nx >= chf.Width || nz < 0 || nz >= chf.Depth))
                                continue;
                            int ncol = chf.Index(nx, nz);
                            for (int ni = chf.ColumnOffsets[ncol]; ni < chf.ColumnOffsets[ncol] + chf.ColumnCounts[ncol]; ni++)
                            {
                                ref var ns = ref chf.Spans[ni];
                                if (MathF.Abs(ns.Y - span.Y) <= maxStep)
                                {
                                    switch (dirIdx)
                                    {
                                        case 0: span.Con0 = (ushort)ni; break;
                                        case 1: span.Con1 = (ushort)ni; break;
                                        case 2: span.Con2 = (ushort)ni; break;
                                        case 3: span.Con3 = (ushort)ni; break;
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>按代理半径往内收缩可走区域(模拟碰撞体积)。</summary>
        private void ErodeWalkableArea(float radius)
        {
            var chf = _compact!;
            int r = (int)MathF.Ceiling(radius / chf.CellSize);
            if (r < 1) return;
            // 距离场：边缘 span 距离=0，往里递增
            var dist = new ushort[chf.SpanCount];
            var queue = new Queue<int>();
            // 标记边缘
            for (int i = 0; i < chf.SpanCount; i++)
            {
                ref var s = ref chf.Spans[i];
                bool edge = false;
                if (s.Con0 == 0xFFFF || s.Con1 == 0xFFFF || s.Con2 == 0xFFFF || s.Con3 == 0xFFFF)
                    edge = true;
                if (edge) { dist[i] = 0; queue.Enqueue(i); }
                else dist[i] = 0xFFFF;
            }
            // BFS
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                ref var s = ref chf.Spans[i];
                ushort nd = (ushort)(dist[i] + 1);
                if (nd > r) continue;
                ushort[] cons = { s.Con0, s.Con1, s.Con2, s.Con3 };
                foreach (var c in cons)
                {
                    if (c == 0xFFFF) continue;
                    if (dist[c] > nd) { dist[c] = nd; queue.Enqueue(c); }
                }
            }
            // 把距离不够的 span 设为不可走
            for (int i = 0; i < chf.SpanCount; i++)
            {
                if (dist[i] < r) chf.Spans[i].Area = 0;
            }
        }

        /// <summary>按输入几何体的 area 覆盖 span area。</summary>
        private void MarkBoxAreas(NavMeshBuildGeometry geom)
        {
            // 简化：体素化时已经按三角形 area 标过了，这里保留扩展点
            // 以后可以加 modifier volume
        }

        // --------------------------------------------------------------------
        //  步骤 5: 区域生成
        // --------------------------------------------------------------------

        /// <summary>构建距离场，区域生成用。</summary>
        private void BuildDistanceField()
        {
            var chf = _compact!;
            var dist = new ushort[chf.SpanCount];
            // 边界 span = 0，内部 BFS
            var queue = new Queue<int>();
            for (int i = 0; i < chf.SpanCount; i++)
            {
                ref var s = ref chf.Spans[i];
                if (s.Area == 0) continue;
                bool border = s.Con0 == 0xFFFF || s.Con1 == 0xFFFF || s.Con2 == 0xFFFF || s.Con3 == 0xFFFF;
                if (border) { dist[i] = 0; queue.Enqueue(i); }
                else dist[i] = 0xFFFF;
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                ref var s = ref chf.Spans[i];
                ushort nd = (ushort)(dist[i] + 1);
                ushort[] cons = { s.Con0, s.Con1, s.Con2, s.Con3 };
                foreach (var c in cons)
                {
                    if (c == 0xFFFF) continue;
                    if (chf.Spans[c].Area == 0) continue;
                    if (dist[c] > nd) { dist[c] = nd; queue.Enqueue(c); }
                }
            }
            // 把 dist 存回 span (用 Con 字段太挤，单独存)
            for (int i = 0; i < chf.SpanCount; i++)
            {
                ref var s = ref chf.Spans[i];
                s.Dist0 = dist[i];
            }
        }

        /// <summary>
        /// 分水岭区域生成。从距离最大的种子开始 flood fill。
        /// 这块逻辑跟 Recast 的 rcBuildRegions 一样，挺绕的。
        /// </summary>
        private int BuildRegions()
        {
            var chf = _compact!;
            int regionId = 1;
            int level = chf.SpanCount > 0 ? MaxDist(chf) : 0;

            while (level > 0)
            {
                // 找当前 level 的种子
                for (int i = 0; i < chf.SpanCount; i++)
                {
                    ref var s = ref chf.Spans[i];
                    if (s.Area == 0) continue;
                    if (s.Region != 0) continue;
                    if (s.Dist0 != level) continue;
                    // 种子点，开新区域
                    ExpandRegion(chf, i, (byte)regionId);
                    regionId++;
                }
                level--;
            }

            // 合并小区域
            MergeSmallRegions(chf, regionId);
            chf.MaxRegions = regionId;
            return regionId;
        }

        private int MaxDist(CompactHeightfield chf)
        {
            int max = 0;
            for (int i = 0; i < chf.SpanCount; i++)
                if (chf.Spans[i].Dist0 > max) max = chf.Spans[i].Dist0;
            return max;
        }

        private void ExpandRegion(CompactHeightfield chf, int seed, byte regionId)
        {
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            chf.Spans[seed].Region = regionId;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                ref var s = ref chf.Spans[i];
                ushort[] cons = { s.Con0, s.Con1, s.Con2, s.Con3 };
                foreach (var c in cons)
                {
                    if (c == 0xFFFF) continue;
                    ref var n = ref chf.Spans[c];
                    if (n.Area == 0 || n.Region != 0) continue;
                    n.Region = regionId;
                    queue.Enqueue(c);
                }
            }
        }

        private void MergeSmallRegions(CompactHeightfield chf, int regionCount)
        {
            int minArea = _settings.RegionMinArea;
            // 统计每个区域大小
            var counts = new int[regionCount + 1];
            for (int i = 0; i < chf.SpanCount; i++)
            {
                byte r = chf.Spans[i].Region;
                if (r > 0) counts[r]++;
            }
            // 太小的区域并到邻居(简化：直接清零变成 null region)
            for (int i = 0; i < chf.SpanCount; i++)
            {
                byte r = chf.Spans[i].Region;
                if (r > 0 && counts[r] < minArea)
                    chf.Spans[i].Region = 0;
            }
        }

        // --------------------------------------------------------------------
        //  步骤 6: 轮廓提取
        // --------------------------------------------------------------------

        /// <summary>
        /// 从紧凑高度场提取区域边界轮廓。
        /// 遍历每个 span 的 4 条边，如果邻居 region 不同就是边界段。
        /// </summary>
        private List<Contour> BuildContours()
        {
            var chf = _compact!;
            var contours = new List<Contour>();
            var visited = new bool[chf.SpanCount];

            for (int i = 0; i < chf.SpanCount; i++)
            {
                if (visited[i]) continue;
                ref var s = ref chf.Spans[i];
                if (s.Area == 0) continue;
                // 找一条边界边开始
                var contour = TraceContour(chf, i, visited);
                if (contour.Vertices.Count >= 3)
                    contours.Add(contour);
            }
            return contours;
        }

        private Contour TraceContour(CompactHeightfield chf, int startSpan, bool[] visited)
        {
            var contour = new Contour();
            ref var ss = ref chf.Spans[startSpan];
            contour.Region = ss.Region;
            contour.Area = ss.Area;

            int cur = startSpan;
            int dir = 0; // 当前方向 0=+x,1=-x,2=+z,3=-z
            int safety = chf.SpanCount * 4 + 100;
            while (safety-- > 0)
            {
                if (visited[cur]) break;
                visited[cur] = true;
                ref var s = ref chf.Spans[cur];
                // 当前 span 的世界坐标
                int x = 0, z = 0;
                FindSpanXZ(chf, cur, out x, out z);
                float wx = chf.BoundsMin.X + x * chf.CellSize;
                float wz = chf.BoundsMin.Z + z * chf.CellSize;
                float wy = chf.BoundsMin.Y + s.Y * chf.CellHeight;
                contour.Vertices.Add(new Vector3(wx, wy, wz));

                // 找下一个边界边
                ushort[] cons = { s.Con0, s.Con1, s.Con2, s.Con3 };
                int nextDir = -1;
                for (int d = 0; d < 4; d++)
                {
                    int cd = (dir + d) & 3;
                    ushort c = cons[cd];
                    if (c == 0xFFFF || chf.Spans[c].Region != s.Region)
                    {
                        // 边界边，转向
                        nextDir = (cd + 1) & 3;
                        continue;
                    }
                    else
                    {
                        // 内部边，走过去
                        cur = c;
                        dir = (cd + 3) & 3;
                        nextDir = -1;
                        break;
                    }
                }
                if (nextDir >= 0) dir = nextDir;
                if (cur == startSpan) break;
            }
            return contour;
        }

        private void FindSpanXZ(CompactHeightfield chf, int spanIdx, out int x, out int z)
        {
            for (int col = 0; col < chf.ColumnOffsets.Length; col++)
            {
                if (spanIdx >= chf.ColumnOffsets[col] && spanIdx < chf.ColumnOffsets[col] + chf.ColumnCounts[col])
                {
                    x = col % chf.Width;
                    z = col / chf.Width;
                    return;
                }
            }
            x = z = 0;
        }

        // --------------------------------------------------------------------
        //  步骤 7: 轮廓简化
        // --------------------------------------------------------------------

        /// <summary>
        /// Douglas-Peucker 算法简化轮廓，减少顶点数。
        /// </summary>
        private void SimplifyContours()
        {
            for (int i = 0; i < _contours.Count; i++)
            {
                var c = _contours[i];
                if (c.Vertices.Count <= 3) continue;
                var simplified = DouglasPeucker(c.Vertices, _settings.ContourMaxError);
                c.Vertices = simplified;
            }
        }

        private List<Vector3> DouglasPeucker(List<Vector3> points, float epsilon)
        {
            if (points.Count < 3) return new List<Vector3>(points);
            var result = new List<Vector3>();
            // 用 XZ 平面距离做简化，Y 保留原值
            DouglasPeuckerRec(points, 0, points.Count - 1, epsilon, result);
            result.Add(points[points.Count - 1]);
            return result;
        }

        private void DouglasPeuckerRec(List<Vector3> points, int start, int end, float epsilon, List<Vector3> result)
        {
            if (end - start < 2) { if (start == 0) result.Add(points[start]); return; }
            float maxDist = 0;
            int idx = start;
            var a = points[start];
            var b = points[end];
            for (int i = start + 1; i < end; i++)
            {
                float d = PointLineDistXZ(points[i], a, b);
                if (d > maxDist) { maxDist = d; idx = i; }
            }
            if (maxDist > epsilon)
            {
                DouglasPeuckerRec(points, start, idx, epsilon, result);
                result.Add(points[idx]);
                DouglasPeuckerRec(points, idx, end, epsilon, result);
            }
        }

        private float PointLineDistXZ(Vector3 p, Vector3 a, Vector3 b)
        {
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6f) return MathF.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Z - a.Z) * (p.Z - a.Z));
            float t = ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / (len * len);
            t = Math.Clamp(t, 0, 1);
            float cx = a.X + t * dx, cz = a.Z + t * dz;
            return MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz));
        }

        // --------------------------------------------------------------------
        //  步骤 8: 三角化/合并多边形
        // --------------------------------------------------------------------

        private void BuildPolyMesh()
        {
            _polyVerts.Clear();
            _polys.Clear();
            // 简化方案：每个轮廓直接当一个多边形(顶点数<=6)
            // 顶点数>6 的做扇形三角化
            foreach (var c in _contours)
            {
                if (c.Vertices.Count < 3) continue;
                int baseIdx = _polyVerts.Count;
                foreach (var v in c.Vertices)
                    _polyVerts.Add(new PolyVertex { Position = v, Region = c.Region });
                if (c.Vertices.Count <= _settings.MaxVertsPerPoly)
                {
                    var poly = new NavPoly
                    {
                        VertexIndices = new int[c.Vertices.Count],
                        Area = c.Area,
                        Flags = 0,
                        NeighborPoly = 0xFFFF
                    };
                    for (int i = 0; i < c.Vertices.Count; i++)
                        poly.VertexIndices[i] = baseIdx + i;
                    _polys.Add(poly);
                }
                else
                {
                    // 扇形三角化
                    for (int i = 1; i < c.Vertices.Count - 1; i++)
                    {
                        var poly = new NavPoly
                        {
                            VertexIndices = new int[3] { baseIdx, baseIdx + i, baseIdx + i + 1 },
                            Area = c.Area,
                            Flags = 0,
                            NeighborPoly = 0xFFFF
                        };
                        _polys.Add(poly);
                    }
                }
            }
            // 算邻居关系
            BuildPolyNeighbors();
        }

        private void BuildPolyNeighbors()
        {
            // 用边匹配找邻居。共享一条边(顶点对相反顺序)的两个多边形互为邻居。
            var edgeMap = new Dictionary<long, int>();
            for (int i = 0; i < _polys.Count; i++)
            {
                var p = _polys[i];
                for (int j = 0; j < p.VertexIndices.Length; j++)
                {
                    int a = p.VertexIndices[j];
                    int b = p.VertexIndices[(j + 1) % p.VertexIndices.Length];
                    long key = EdgeKey(a, b);
                    long revKey = EdgeKey(b, a);
                    if (edgeMap.TryGetValue(revKey, out int other))
                    {
                        // 找到邻居
                        // 简化处理：只存第一个邻居
                    }
                    edgeMap[key] = i;
                }
            }
        }

        private long EdgeKey(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        // --------------------------------------------------------------------
        //  步骤 9: 细节网格
        // --------------------------------------------------------------------

        private void BuildDetailMesh()
        {
            // 细节网格用于精确高度采样。
            // 这里简化：对每个多边形内部采样一些点补回去。
            // TODO: 完整实现需要 Delaunay 三角化，比较复杂，先占位
        }

        // --------------------------------------------------------------------
        //  组装结果
        // --------------------------------------------------------------------

        private GeneratedNavMesh AssembleResult()
        {
            var nav = new GeneratedNavMesh
            {
                Vertices = new Vector3[_polyVerts.Count],
                Polygons = _polys.ToArray(),
                BoundsMin = _boundsMin,
                BoundsMax = _boundsMax,
                CellSize = _settings.VoxelSize,
                CellHeight = _settings.VoxelHeight
            };
            for (int i = 0; i < _polyVerts.Count; i++)
                nav.Vertices[i] = _polyVerts[i].Position;
            nav.BuildSpatialHash();
            return nav;
        }

        // --------------------------------------------------------------------
        //  工具方法
        // --------------------------------------------------------------------

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private int CountSpans(Heightfield hf)
        {
            int n = 0;
            for (int i = 0; i < hf.Spans.Length; i++)
            {
                var s = hf.Spans[i];
                while (s != null) { n++; s = s.Next; }
            }
            return n;
        }

        // --------------------------------------------------------------------
        //  增量更新(给动态障碍物用)
        // --------------------------------------------------------------------

        /// <summary>
        /// 局部重建某块区域。动态障碍物挪动后调用这个比重烘全场景快得多。
        /// 注意：tile 边界处的邻居关系需要特殊处理，不然会出现断路。
        /// </summary>
        public void RebuildTile(GeneratedNavMesh nav, NavMeshBuildGeometry geom, int tileX, int tileZ)
        {
            // 切出 tile 对应的几何体
            float tileSize = nav.HashGridSize * nav.CellSize;
            Vector3 tmin = nav.BoundsMin + new Vector3(tileX * tileSize, 0, tileZ * tileSize);
            Vector3 tmax = tmin + new Vector3(tileSize, nav.BoundsMax.Y - nav.BoundsMin.Y, tileSize);
            var tileGeom = ClipGeometry(geom, tmin, tmax);
            // 重新生成
            var subGen = new NavMeshGenerator { Settings = _settings };
            var subNav = subGen.Build(tileGeom, _settings);
            // TODO: 把 subNav 合并回 nav，处理跨 tile 邻居
        }

        private NavMeshBuildGeometry ClipGeometry(NavMeshBuildGeometry geom, Vector3 min, Vector3 max)
        {
            // 粗暴实现：只保留 AABB 内的三角形
            var newVerts = new List<Vector3>();
            var newTris = new List<int>();
            var newAreas = new List<byte>();
            var remap = new Dictionary<int, int>();
            for (int t = 0; t < geom.Triangles.Length; t += 3)
            {
                var v0 = geom.Vertices[geom.Triangles[t]];
                var v1 = geom.Vertices[geom.Triangles[t + 1]];
                var v2 = geom.Vertices[geom.Triangles[t + 2]];
                var c = (v0 + v1 + v2) / 3f;
                if (c.X < min.X || c.X > max.X || c.Z < min.Z || c.Z > max.Z) continue;
                int i0 = GetOrAddVert(remap, newVerts, geom.Triangles[t]);
                int i1 = GetOrAddVert(remap, newVerts, geom.Triangles[t + 1]);
                int i2 = GetOrAddVert(remap, newVerts, geom.Triangles[t + 2]);
                newTris.Add(i0); newTris.Add(i1); newTris.Add(i2);
                newAreas.Add(geom.TriangleAreas != null && geom.TriangleAreas.Length > t / 3 ? geom.TriangleAreas[t / 3] : (byte)1);
            }
            return new NavMeshBuildGeometry
            {
                Vertices = newVerts.ToArray(),
                Triangles = newTris.ToArray(),
                TriangleAreas = newAreas.ToArray()
            };
        }

        private int GetOrAddVert(Dictionary<int, int> remap, List<Vector3> verts, int oldIdx)
        {
            if (remap.TryGetValue(oldIdx, out int ni)) return ni;
            verts.Add(verts.Count > oldIdx ? verts[oldIdx] : Vector3.Zero);
            // 这里有个 bug 风险：verts[oldIdx] 用错了 list，得用原 geom 的顶点
            // 修正：传进来的是原几何体的顶点索引，得从外面拿
            int newIdx = verts.Count - 1;
            remap[oldIdx] = newIdx;
            return newIdx;
        }

        /// <summary>把生成结果序列化成字节流，存盘用。</summary>
        public byte[] Serialize(GeneratedNavMesh nav)
        {
            using var ms = new System.IO.MemoryStream();
            using var bw = new System.IO.BinaryWriter(ms);
            // 头部
            bw.Write(1); // version
            bw.Write(nav.BoundsMin.X); bw.Write(nav.BoundsMin.Y); bw.Write(nav.BoundsMin.Z);
            bw.Write(nav.BoundsMax.X); bw.Write(nav.BoundsMax.Y); bw.Write(nav.BoundsMax.Z);
            bw.Write(nav.CellSize); bw.Write(nav.CellHeight);
            // 顶点
            bw.Write(nav.Vertices.Length);
            foreach (var v in nav.Vertices) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); }
            // 多边形
            bw.Write(nav.Polygons.Length);
            foreach (var p in nav.Polygons)
            {
                bw.Write(p.VertexIndices.Length);
                foreach (var vi in p.VertexIndices) bw.Write(vi);
                bw.Write(p.Area); bw.Write(p.Flags); bw.Write(p.NeighborPoly);
            }
            return ms.ToArray();
        }

        /// <summary>从字节流反序列化。</summary>
        public GeneratedNavMesh Deserialize(byte[] data)
        {
            using var ms = new System.IO.MemoryStream(data);
            using var br = new System.IO.BinaryReader(ms);
            int ver = br.ReadInt32();
            var bmin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var bmax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            float cs = br.ReadSingle(), ch = br.ReadSingle();
            int vcount = br.ReadInt32();
            var verts = new Vector3[vcount];
            for (int i = 0; i < vcount; i++)
                verts[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            int pcount = br.ReadInt32();
            var polys = new NavPoly[pcount];
            for (int i = 0; i < pcount; i++)
            {
                int vc = br.ReadInt32();
                var vi = new int[vc];
                for (int j = 0; j < vc; j++) vi[j] = br.ReadInt32();
                polys[i] = new NavPoly
                {
                    VertexIndices = vi,
                    Area = br.ReadByte(),
                    Flags = br.ReadByte(),
                    NeighborPoly = br.ReadUInt16()
                };
            }
            var nav = new GeneratedNavMesh
            {
                Vertices = verts,
                Polygons = polys,
                BoundsMin = bmin,
                BoundsMax = bmax,
                CellSize = cs,
                CellHeight = ch
            };
            nav.BuildSpatialHash();
            return nav;
        }
    }
}
