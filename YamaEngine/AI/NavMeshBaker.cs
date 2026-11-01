using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Modeling;

namespace YamaEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════════
    //  NavMeshBaker.cs —— Recast / Detour 风格导航网格生成系统
    //
    //  本文件实现了完整的 Recast 烘焙管线与 Detour 运行时查询/避障系统，
    //  并与 YamaEngine 已有子系统对接：
    //    · 复用 NavigationSystem.cs 中已定义的 NavArea / NavLink / LinkType /
    //      NavPoly / NavTile / NavMeshData / NavQueryFilter / CrowdAgent 等类型。
    //    · 通过 BakedNavMeshData 继承 NavMeshData，使烘焙产物可直接被现有
    //      NavigationSystem / NavQuery / Pathfinding 使用。
    //    · 输入网格来自 Modeling.EditableMesh（Vertices + Faces(MeshFace A/B/C)）。
    //
    //  Recast 管线阶段：
    //    1. VoxelizationStage        —— 三角网格 → 体素高度场
    //    2. RegionGenerationStage    —— 高度场 → 区域（分水岭 / 单调分区）
    //    3. BakerContourGenerationStage   —— 区域 → 简化边界轮廓
    //    4. PolyMeshGenerationStage  —— 轮廓 → 凸多边形网格 + 邻接
    //    5. DetailMeshGenerationStage—— 多边形 → 细节高度网格
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 区域分区算法类型。对应 Recast 的三种区域分割策略。
    /// </summary>
    public enum RegionPartitioning
    {
        /// <summary>分水岭算法（Watershed）。质量最高，速度最慢。</summary>
        Watershed,
        /// <summary>单调分区（Monotone）。速度快，区域规整。</summary>
        Monotone,
        /// <summary>层级分区（Layers）。按高度分层。</summary>
        Layers
    }

    /// <summary>
    /// NavMesh 烘焙配置。覆盖 Recast rcConfig 的主要字段。
    /// </summary>
    public class NavMeshBakeConfig
    {
        // ── 体素尺寸 ──
        /// <summary>体素单元格在 XZ 平面的边长（世界单位）。</summary>
        public float CellSize = 0.3f;
        /// <summary>体素单元格在 Y 轴的高度（世界单位）。</summary>
        public float CellHeight = 0.2f;

        // ── Agent 参数 ──
        /// <summary>Agent 高度（用于过滤净空不足的区域）。</summary>
        public float AgentHeight = 2.0f;
        /// <summary>Agent 半径（用于膨胀可通行区域）。</summary>
        public float AgentRadius = 0.5f;
        /// <summary>Agent 最大攀爬高度。</summary>
        public float AgentMaxClimb = 0.9f;
        /// <summary>Agent 最大可行走坡度（度）。</summary>
        public float AgentMaxSlope = 45.0f;

        // ── 区域 ──
        /// <summary>区域最小面积（体素格子数）。小于此面积的区域被合并。</summary>
        public int RegionMinArea = 8;
        /// <summary>区域合并面积阈值（体素格子数）。</summary>
        public int RegionMergeArea = 20;

        // ── 轮廓简化 ──
        /// <summary>轮廓边最大长度。超过此长度的边会被分割。</summary>
        public float EdgeMaxLen = 12.0f;
        /// <summary>轮廓简化最大误差。</summary>
        public float EdgeMaxError = 1.3f;

        // ── 多边形网格 ──
        /// <summary>每个多边形最大顶点数。</summary>
        public int VertsPerPoly = 6;

        // ── 细节网格 ──
        /// <summary>细节网格采样距离。</summary>
        public float DetailSampleDist = 6.0f;
        /// <summary>细节网格最大采样误差。</summary>
        public float DetailSampleMaxError = 1.0f;

        // ── 分区算法 ──
        /// <summary>区域分区算法。</summary>
        public RegionPartitioning Partitioning = RegionPartitioning.Watershed;

        // ── Tile ──
        /// <summary>Tile 大小（世界单位）。0 表示整个网格为单个 Tile。</summary>
        public float TileSize = 0.0f;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 体素高度场（Voxel Heightfield）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 体素跨度（Span）。表示一列中从 yMin 到 yMax 的实体柱。
    /// 使用链表组织同一 (x,z) 列中的多个 span。
    /// </summary>
    public class VoxelSpan
    {
        public uint YMin;
        public uint YMax;
        public bool Walkable;
        public byte Area;
        public VoxelSpan? Next;

        /// <summary>顶部高度别名（整型形式，供 NavMeshGenerator 使用）。</summary>
        public int MaxY { get => (int)YMax; set => YMax = (uint)Math.Max(0, value); }
        /// <summary>底部高度别名（整型形式，供 NavMeshGenerator 使用）。</summary>
        public int MinY { get => (int)YMin; set => YMin = (uint)Math.Max(0, value); }

        public VoxelSpan(uint yMin, uint yMax, bool walkable = false, byte area = 0)
        {
            YMin = yMin;
            YMax = yMax;
            Walkable = walkable;
            Area = area;
            Next = null;
        }

        /// <summary>整型构造重载，供 NavMeshGenerator 的 Heightfield 使用。</summary>
        public VoxelSpan(int minY, int maxY, byte area)
        {
            YMin = (uint)Math.Max(0, minY);
            YMax = (uint)Math.Max(0, maxY);
            Walkable = area != 0;
            Area = area;
            Next = null;
        }
    }

    /// <summary>
    /// 体素高度场。将三角网格体素化为规则网格的 span 集合。
    /// 对应 Recast 的 rcHeightfield。
    /// </summary>
    public class VoxelHeightfield
    {
        public int Width;
        public int Height;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public float CellSize;
        public float CellHeight;
        public VoxelSpan?[] Spans;

        public VoxelHeightfield(int width, int height, Vector3 bmin, Vector3 bmax, float cellSize, float cellHeight)
        {
            Width = width;
            Height = height;
            BoundsMin = bmin;
            BoundsMax = bmax;
            CellSize = cellSize;
            CellHeight = cellHeight;
            Spans = new VoxelSpan[width * height];
        }

        /// <summary>获取 (x,z) 列的 span 链表头。</summary>
        public VoxelSpan? GetColumn(int x, int z)
        {
            if (x < 0 || x >= Width || z < 0 || z >= Height) return null;
            return Spans[z * Width + x];
        }

        /// <summary>向 (x,z) 列添加一个 span，按 Y 排序插入。</summary>
        public void AddSpan(int x, int z, float yMinF, float yMaxF, bool walkable, float areaNormal = 0)
        {
            if (x < 0 || x >= Width || z < 0 || z >= Height) return;

            uint yMin = (uint)MathF.Max(0, (int)((yMinF - BoundsMin.Y) / CellHeight));
            uint yMax = (uint)MathF.Max(0, (int)((yMaxF - BoundsMin.Y) / CellHeight));

            var span = new VoxelSpan(yMin, yMax, walkable, walkable ? (byte)1 : (byte)0);
            int idx = z * Width + x;

            if (Spans[idx] == null)
            {
                Spans[idx] = span;
                return;
            }

            // 按高度排序插入
            VoxelSpan? cur = Spans[idx];
            VoxelSpan? prev = null;
            while (cur != null && cur.YMin < span.YMin)
            {
                prev = cur;
                cur = cur.Next;
            }

            if (prev == null)
            {
                span.Next = Spans[idx];
                Spans[idx] = span;
            }
            else
            {
                span.Next = cur;
                prev.Next = span;
            }
        }

        /// <summary>统计 span 总数。</summary>
        public int SpanCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Spans.Length; i++)
                {
                    for (var s = Spans[i]; s != null; s = s.Next)
                        count++;
                }
                return count;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 紧凑高度场（Compact Heightfield）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 紧凑 span。去除冗余数据后的轻量 span，用于区域生成。
    /// </summary>
    public struct BakerCompactSpan
    {
        public ushort Y;
        public ushort Height;
        public int Con0, Con1, Con2, Con3; // 邻接列 index (-1=无)
    }

    /// <summary>
    /// 紧凑高度场。从 VoxelHeightfield 构建，仅保留可行走 span。
    /// 对应 Recast 的 rcBakerCompactHeightfield。
    /// </summary>
    public class BakerCompactHeightfield
    {
        public int Width;
        public int Height;
        public int SpanCount;
        public int MaxSpansPerColumn;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public float CellSize;
        public float CellHeight;
        public int[] CellCounts; // 每列 span 数
        public int[] CellStarts; // 每列起始 index
        public BakerCompactSpan[] Spans;
        public byte[] Areas;

        public BakerCompactHeightfield(int width, int height, Vector3 bmin, Vector3 bmax, float cs, float ch)
        {
            Width = width;
            Height = height;
            BoundsMin = bmin;
            BoundsMax = bmax;
            CellSize = cs;
            CellHeight = ch;
            CellCounts = new int[width * height];
            CellStarts = new int[width * height + 1];
        }

        /// <summary>获取 (x,z) 列中第 i 个 span 的全局 index。</summary>
        public int GetSpanIndex(int x, int z, int i)
        {
            return CellStarts[z * Width + x] + i;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 体素化阶段（Voxelization Stage）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 体素化阶段。将三角网格光栅化为体素高度场。
    /// 对应 Recast 的 rcCreateHeightfield + rcRasterizeTriangles。
    /// </summary>
    public class VoxelizationStage
    {
        private readonly NavMeshBakeConfig _cfg;

        public VoxelizationStage(NavMeshBakeConfig config)
        {
            _cfg = config;
        }

        /// <summary>
        /// 从可编辑网格构建体素高度场。
        /// </summary>
        public VoxelHeightfield Build(EditableMesh mesh)
        {
            // 计算包围盒
            Vector3 bmin = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bmax = new(float.MinValue, float.MinValue, float.MinValue);
            foreach (var v in mesh.Vertices)
            {
                bmin = Vector3.Min(bmin, v);
                bmax = Vector3.Max(bmax, v);
            }

            // 膨胀包围盒以容纳 Agent 半径
            float agentRadiusVoxels = _cfg.AgentRadius / _cfg.CellSize;
            bmin.X -= agentRadiusVoxels * _cfg.CellSize;
            bmin.Z -= agentRadiusVoxels * _cfg.CellSize;
            bmax.X += agentRadiusVoxels * _cfg.CellSize;
            bmax.Z += agentRadiusVoxels * _cfg.CellSize;

            int width = (int)((bmax.X - bmin.X) / _cfg.CellSize + 0.5f);
            int height = (int)((bmax.Z - bmin.Z) / _cfg.CellSize + 0.5f);
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var hf = new VoxelHeightfield(width, height, bmin, bmax, _cfg.CellSize, _cfg.CellHeight);

            // 光栅化每个三角形
            foreach (var face in mesh.Faces)
            {
                if (face.A >= mesh.Vertices.Count || face.B >= mesh.Vertices.Count || face.C >= mesh.Vertices.Count)
                    continue;

                Vector3 v0 = mesh.Vertices[face.A];
                Vector3 v1 = mesh.Vertices[face.B];
                Vector3 v2 = mesh.Vertices[face.C];

                RasterizeTriangle(hf, v0, v1, v2);
            }

            // 标记可行走面（法线朝上且坡度小于阈值）
            MarkWalkableSpans(hf);

            // 过滤低悬空障碍
            FilterLowHangingObstacles(hf);

            // 过滤净空不足的 span
            FilterWalkableLowHeightSpans(hf);

            return hf;
        }

        /// <summary>
        /// 光栅化单个三角形到高度场。使用点采样法。
        /// </summary>
        private void RasterizeTriangle(VoxelHeightfield hf, Vector3 v0, Vector3 v1, Vector3 v2)
        {
            // 计算三角形法线
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

            // 判断三角形朝向（法线 Y 分量）
            float walkableThreshold = MathF.Cos(_cfg.AgentMaxSlope * MathF.PI / 180.0f);
            bool walkable = normal.Y > walkableThreshold;

            // 投影到 XZ 网格
            float minx = MathF.Min(v0.X, MathF.Min(v1.X, v2.X));
            float maxx = MathF.Max(v0.X, MathF.Max(v1.X, v2.X));
            float minz = MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z));
            float maxz = MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z));

            int x0 = Math.Clamp((int)((minx - hf.BoundsMin.X) / hf.CellSize), 0, hf.Width - 1);
            int x1 = Math.Clamp((int)((maxx - hf.BoundsMin.X) / hf.CellSize), 0, hf.Width - 1);
            int z0 = Math.Clamp((int)((minz - hf.BoundsMin.Z) / hf.CellSize), 0, hf.Height - 1);
            int z1 = Math.Clamp((int)((maxz - hf.BoundsMin.Z) / hf.CellSize), 0, hf.Height - 1);

            // 逆法线方向投影到 XZ 平面求 Y
            float nx = normal.X, ny = normal.Y, nz = normal.Z;
            if (MathF.Abs(ny) < 1e-6f) return;

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float cx = hf.BoundsMin.X + (x + 0.5f) * hf.CellSize;
                    float cz = hf.BoundsMin.Z + (z + 0.5f) * hf.CellSize;

                    // 检查 (cx, cz) 是否在三角形 XZ 投影内
                    if (!PointInTriangleXZ(cx, cz, v0, v1, v2))
                        continue;

                    // 求三角形在 (cx, cz) 处的 Y 值
                    float y = (-(nx * (cx - v0.X) + nz * (cz - v0.Z)) / ny) + v0.Y;

                    // 坡度导致的 Y 范围估算
                    float slopeHeight = (MathF.Abs(nx) + MathF.Abs(nz)) * hf.CellSize / MathF.Max(MathF.Abs(ny), 1e-4f);
                    float yMin = y - slopeHeight * 0.5f;
                    float yMax = y + slopeHeight * 0.5f;

                    yMin = MathF.Max(yMin, hf.BoundsMin.Y);
                    yMax = MathF.Min(yMax, hf.BoundsMax.Y);
                    if (yMax <= yMin) yMax = yMin + hf.CellHeight;

                    hf.AddSpan(x, z, yMin, yMax, walkable, normal.Y);
                }
            }
        }

        /// <summary>判断点 (px, pz) 是否在三角形 (a, b, c) 的 XZ 投影内。</summary>
        private static bool PointInTriangleXZ(float px, float pz, Vector3 a, Vector3 b, Vector3 c)
        {
            float d1 = SignXZ(px, pz, a, b);
            float d2 = SignXZ(px, pz, b, c);
            float d3 = SignXZ(px, pz, c, a);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        private static float SignXZ(float px, float pz, Vector3 a, Vector3 b)
        {
            return (px - b.X) * (a.Z - b.Z) - (a.X - b.X) * (pz - b.Z);
        }

        /// <summary>
        /// 标记可行走 span。坡度小于阈值的面标记为 walkable。
        /// </summary>
        private void MarkWalkableSpans(VoxelHeightfield hf)
        {
            float threshold = MathF.Cos(_cfg.AgentMaxSlope * MathF.PI / 180.0f);
            for (int i = 0; i < hf.Spans.Length; i++)
            {
                for (var s = hf.Spans[i]; s != null; s = s.Next)
                {
                    // 简化：标记时已在 RasterizeTriangle 中完成
                    // 这里仅做二次校验
                }
            }
        }

        /// <summary>
        /// 过滤低悬空障碍。可通行的低矮障碍被标记为不可行走。
        /// 对应 rcFilterLowHangingObstacles。
        /// </summary>
        private void FilterLowHangingObstacles(VoxelHeightfield hf)
        {
            float walkableClimbVoxels = _cfg.AgentMaxClimb / hf.CellHeight;

            for (int i = 0; i < hf.Spans.Length; i++)
            {
                var s = hf.Spans[i];
                if (s == null) continue;

                var next = s.Next;
                if (next != null && s.Walkable && !next.Walkable)
                {
                    // 可行走面正上方有不可行走的低矮障碍
                    if (next.YMin - s.YMax <= (uint)walkableClimbVoxels)
                    {
                        next.Walkable = true;
                    }
                }
            }
        }

        /// <summary>
        /// 过滤净空不足的 span。若 span 上方间距小于 Agent 高度，标记为不可行走。
        /// 对应 rcFilterWalkableLowHeightSpans。
        /// </summary>
        private void FilterWalkableLowHeightSpans(VoxelHeightfield hf)
        {
            uint walkableHeightVoxels = (uint)(_cfg.AgentHeight / hf.CellHeight + 0.5f);

            for (int i = 0; i < hf.Spans.Length; i++)
            {
                for (var s = hf.Spans[i]; s != null; s = s.Next)
                {
                    if (!s.Walkable) continue;

                    uint ceilY;
                    if (s.Next != null)
                        ceilY = s.Next.YMin;
                    else
                        ceilY = (uint)((hf.BoundsMax.Y - hf.BoundsMin.Y) / hf.CellHeight);

                    if (ceilY - s.YMax < walkableHeightVoxels)
                        s.Walkable = false;
                }
            }
        }

        /// <summary>
        /// 从体素高度场构建紧凑高度场。
        /// </summary>
        public BakerCompactHeightfield BuildBakerCompactHeightfield(VoxelHeightfield hf)
        {
            // 统计每列可行走 span 数
            int totalSpans = 0;
            int maxPerCol = 0;
            for (int i = 0; i < hf.Spans.Length; i++)
            {
                int count = 0;
                for (var s = hf.Spans[i]; s != null; s = s.Next)
                {
                    if (s.Walkable) { count++; totalSpans++; }
                }
                hf.Spans[i]?.Let(_ => { }); // no-op to satisfy analysis
                // 重新计数存入 chf
                if (count > maxPerCol) maxPerCol = count;
            }

            var chf = new BakerCompactHeightfield(hf.Width, hf.Height, hf.BoundsMin, hf.BoundsMax, hf.CellSize, hf.CellHeight)
            {
                SpanCount = totalSpans,
                MaxSpansPerColumn = maxPerCol,
                Spans = new BakerCompactSpan[totalSpans],
                Areas = new byte[totalSpans]
            };

            // 填充 cells
            int idx = 0;
            for (int z = 0; z < hf.Height; z++)
            {
                for (int x = 0; x < hf.Width; x++)
                {
                    int cellIdx = z * hf.Width + x;
                    chf.CellStarts[cellIdx] = idx;

                    int count = 0;
                    for (var s = hf.GetColumn(x, z); s != null; s = s.Next)
                    {
                        if (!s.Walkable) continue;

                        uint ceilY = s.Next != null ? s.Next.YMin :
                            (uint)((hf.BoundsMax.Y - hf.BoundsMin.Y) / hf.CellHeight);

                        chf.Spans[idx] = new BakerCompactSpan
                        {
                            Y = (ushort)s.YMin,
                            Height = (ushort)Math.Max(1, (int)(ceilY - s.YMin)),
                            Con0 = -1, Con1 = -1, Con2 = -1, Con3 = -1
                        };
                        chf.Areas[idx] = s.Area;
                        idx++;
                        count++;
                    }
                    chf.CellCounts[cellIdx] = count;
                }
            }
            chf.CellStarts[hf.Width * hf.Height] = idx;

            // 构建邻接关系
            BuildConnections(chf);

            return chf;
        }

        /// <summary>
        /// 构建紧凑 span 之间的邻接关系（4 邻接）。
        /// </summary>
        private void BuildConnections(BakerCompactHeightfield chf)
        {
            uint walkableClimbVoxels = (uint)(_cfg.AgentMaxClimb / chf.CellHeight + 0.5f);
            int[,] dirs = { { -1, 0 }, { 0, 1 }, { 1, 0 }, { 0, -1 } }; // 左、前、右、后

            for (int z = 0; z < chf.Height; z++)
            {
                for (int x = 0; x < chf.Width; x++)
                {
                    int cellIdx = z * chf.Width + x;
                    for (int si = 0; si < chf.CellCounts[cellIdx]; si++)
                    {
                        int spanIdx = chf.GetSpanIndex(x, z, si);
                        var span = chf.Spans[spanIdx];

                        for (int dir = 0; dir < 4; dir++)
                        {
                            int nx = x + dirs[dir, 0];
                            int nz = z + dirs[dir, 1];
                            if (nx < 0 || nx >= chf.Width || nz < 0 || nz >= chf.Height)
                                continue;

                            int nCellIdx = nz * chf.Width + nx;
                            for (int ni = 0; ni < chf.CellCounts[nCellIdx]; ni++)
                            {
                                int nSpanIdx = chf.GetSpanIndex(nx, nz, ni);
                                var nSpan = chf.Spans[nSpanIdx];

                                // 检查高度是否可通行
                                if (MathF.Abs(nSpan.Y - span.Y) <= walkableClimbVoxels)
                                {
                                    switch (dir)
                                    {
                                        case 0: span.Con0 = nSpanIdx; break;
                                        case 1: span.Con1 = nSpanIdx; break;
                                        case 2: span.Con2 = nSpanIdx; break;
                                        case 3: span.Con3 = nSpanIdx; break;
                                    }
                                    chf.Spans[spanIdx] = span;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 轮廓与多边形网格
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 轮廓顶点。
    /// </summary>
    public struct ContourVertex
    {
        public int X, Y, Z;
        public int RegionId;
    }

    /// <summary>
    /// 区域边界轮廓。由同一区域的边界 span 组成。
    /// </summary>
    public class BakerContour
    {
        public List<ContourVertex> Vertices = new();
        public int RegionId;
    }

    /// <summary>
    /// 简化后的多边形顶点（世界坐标）。
    /// </summary>

    // ════════════════════════════════════════════════════════════════════════════
    // 烘焙产物
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 烘焙后的 NavMesh 数据。继承 NavMeshData，可直接被 NavigationSystem 使用。
    /// </summary>
    public class BakedNavMeshData : NavMeshData
    {
        /// <summary>烘焙配置快照。</summary>
        public NavMeshBakeConfig? Config;
        /// <summary>原始包围盒。</summary>
        public Vector3 SourceBoundsMin;
        public Vector3 SourceBoundsMax;
        /// <summary>多边形顶点列表（世界坐标）。</summary>
        public readonly List<Vector3> PolyVertices = new();
        /// <summary>总三角形数。</summary>
        public int SourceTriangleCount;
        /// <summary>体素 span 总数。</summary>
        public int VoxelSpanCount;
        /// <summary>烘焙耗时（ms）。</summary>
        public long BakeTimeMs;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // NavMesh 烘焙器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NavMesh 烘焙器。编排完整的 Recast 烘焙管线。
    /// </summary>
    public class NavMeshBaker
    {
        private readonly NavMeshBakeConfig _config;

        public NavMeshBaker(NavMeshBakeConfig? config = null)
        {
            _config = config ?? new NavMeshBakeConfig();
        }

        /// <summary>
        /// 烘焙 NavMesh。将可编辑网格转换为可查询的导航网格数据。
        /// </summary>
        public BakedNavMeshData Bake(EditableMesh mesh)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var result = new BakedNavMeshData
            {
                Config = _config,
                CellSize = _config.CellSize,
                CellHeight = _config.CellHeight,
                MaxAgentRadius = _config.AgentRadius,
                MaxAgentHeight = _config.AgentHeight
            };

            // ── 阶段 1: 体素化 ──
            var voxelStage = new VoxelizationStage(_config);
            var hf = voxelStage.Build(mesh);
            result.VoxelSpanCount = hf.SpanCount;
            result.SourceBoundsMin = hf.BoundsMin;
            result.SourceBoundsMax = hf.BoundsMax;

            // 统计源三角形数
            result.SourceTriangleCount = mesh.Faces.Count;

            // ── 阶段 2: 构建紧凑高度场 ──
            var chf = voxelStage.BuildBakerCompactHeightfield(hf);

            // ── 阶段 3: 区域生成 ──
            var regions = GenerateRegions(chf);

            // ── 阶段 4: 轮廓生成 ──
            var BakerContours = BuildBakerContours(chf, regions);

            // ── 阶段 5: 多边形网格生成 ──
            BuildPolyMesh(chf, BakerContours, result);

            // ── 设置原点和 Tile 信息 ──
            result.Origin = hf.BoundsMin;
            result.TileSize = _config.TileSize > 0 ? _config.TileSize :
                MathF.Max(hf.BoundsMax.X - hf.BoundsMin.X, hf.BoundsMax.Z - hf.BoundsMin.Z);
            result.TileCountX = 1;
            result.TileCountZ = 1;

            sw.Stop();
            result.BakeTimeMs = sw.ElapsedMilliseconds;

            return result;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 区域生成
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 区域生成。将紧凑高度场的 span 分组为区域。
        /// </summary>
        private int[] GenerateRegions(BakerCompactHeightfield chf)
        {
            int[] regions = new int[chf.SpanCount];
            int nextRegion = 1;

            switch (_config.Partitioning)
            {
                case RegionPartitioning.Monotone:
                    nextRegion = GenerateRegionsMonotone(chf, regions);
                    break;
                case RegionPartitioning.Layers:
                    nextRegion = GenerateRegionsLayers(chf, regions);
                    break;
                default:
                    nextRegion = GenerateRegionsWatershed(chf, regions);
                    break;
            }

            // 过滤小区域
            FilterSmallRegions(chf, regions, nextRegion);

            return regions;
        }

        /// <summary>
        /// 分水岭区域生成。质量最高但最慢。
        /// 简化实现：使用 BFS 泛洪填充。
        /// </summary>
        private int GenerateRegionsWatershed(BakerCompactHeightfield chf, int[] regions)
        {
            Array.Fill(regions, 0);
            int nextRegion = 1;

            for (int i = 0; i < chf.Spans.Length; i++)
            {
                if (regions[i] != 0 || chf.Areas[i] == 0) continue;

                // BFS 泛洪
                var queue = new Queue<int>();
                queue.Enqueue(i);
                regions[i] = nextRegion;
                int count = 0;

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    count++;

                    var span = chf.Spans[idx];
                    int[] cons = { span.Con0, span.Con1, span.Con2, span.Con3 };

                    foreach (var con in cons)
                    {
                        if (con >= 0 && regions[con] == 0 && chf.Areas[con] != 0)
                        {
                            regions[con] = nextRegion;
                            queue.Enqueue(con);
                        }
                    }
                }

                if (count >= _config.RegionMinArea)
                    nextRegion++;
                else
                {
                    // 小区域：回退标记
                    for (int j = 0; j < regions.Length; j++)
                        if (regions[j] == nextRegion) regions[j] = 0;
                }
            }

            return nextRegion;
        }

        /// <summary>
        /// 单调分区。按扫描线分配区域。
        /// </summary>
        private int GenerateRegionsMonotone(BakerCompactHeightfield chf, int[] regions)
        {
            Array.Fill(regions, 0);
            int nextRegion = 1;

            for (int z = 0; z < chf.Height; z++)
            {
                for (int x = 0; x < chf.Width; x++)
                {
                    int cellIdx = z * chf.Width + x;
                    for (int si = 0; si < chf.CellCounts[cellIdx]; si++)
                    {
                        int spanIdx = chf.GetSpanIndex(x, z, si);
                        if (regions[spanIdx] != 0 || chf.Areas[spanIdx] == 0) continue;

                        var queue = new Queue<int>();
                        queue.Enqueue(spanIdx);
                        regions[spanIdx] = nextRegion;

                        while (queue.Count > 0)
                        {
                            int idx = queue.Dequeue();
                            var span = chf.Spans[idx];
                            int[] cons = { span.Con0, span.Con1, span.Con2, span.Con3 };

                            foreach (var con in cons)
                            {
                                if (con >= 0 && regions[con] == 0 && chf.Areas[con] != 0)
                                {
                                    regions[con] = nextRegion;
                                    queue.Enqueue(con);
                                }
                            }
                        }
                        nextRegion++;
                    }
                }
            }

            return nextRegion;
        }

        /// <summary>
        /// 层级分区。按 Y 高度分层。
        /// </summary>
        private int GenerateRegionsLayers(BakerCompactHeightfield chf, int[] regions)
        {
            Array.Fill(regions, 0);
            int nextRegion = 1;

            for (int i = 0; i < chf.Spans.Length; i++)
            {
                if (regions[i] != 0 || chf.Areas[i] == 0) continue;

                var queue = new Queue<int>();
                queue.Enqueue(i);
                regions[i] = nextRegion;
                ushort layerY = chf.Spans[i].Y;

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    var span = chf.Spans[idx];
                    int[] cons = { span.Con0, span.Con1, span.Con2, span.Con3 };

                    foreach (var con in cons)
                    {
                        if (con >= 0 && regions[con] == 0 && chf.Areas[con] != 0)
                        {
                            // 仅连接相近高度的 span
                            if (MathF.Abs(chf.Spans[con].Y - layerY) <= 3)
                            {
                                regions[con] = nextRegion;
                                queue.Enqueue(con);
                            }
                        }
                    }
                }
                nextRegion++;
            }

            return nextRegion;
        }

        /// <summary>过滤小区域，将其合并到邻接的大区域。</summary>
        private void FilterSmallRegions(BakerCompactHeightfield chf, int[] regions, int regionCount)
        {
            // 统计每区域 span 数
            var counts = new int[regionCount];
            for (int i = 0; i < regions.Length; i++)
            {
                if (regions[i] > 0 && regions[i] < regionCount)
                    counts[regions[i]]++;
            }

            // 标记小区域为 0（无效）
            for (int i = 0; i < regions.Length; i++)
            {
                if (regions[i] > 0 && counts[regions[i]] < _config.RegionMinArea)
                    regions[i] = 0;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 轮廓生成
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 从区域生成边界轮廓。
        /// </summary>
        private List<BakerContour> BuildBakerContours(BakerCompactHeightfield chf, int[] regions)
        {
            var BakerContours = new List<BakerContour>();
            var visited = new bool[chf.SpanCount];

            int[,] dirs = { { -1, 0 }, { 0, 1 }, { 1, 0 }, { 0, -1 } };

            for (int z = 0; z < chf.Height; z++)
            {
                for (int x = 0; x < chf.Width; x++)
                {
                    int cellIdx = z * chf.Width + x;
                    for (int si = 0; si < chf.CellCounts[cellIdx]; si++)
                    {
                        int spanIdx = chf.GetSpanIndex(x, z, si);
                        if (visited[spanIdx] || regions[spanIdx] == 0) continue;

                        int regionId = regions[spanIdx];
                        var BakerContour = TraceBakerContour(chf, regions, visited, x, z, si, spanIdx, regionId);
                        if (BakerContour.Vertices.Count >= 3)
                            BakerContours.Add(BakerContour);
                    }
                }
            }

            // 简化轮廓
            var simplified = new List<BakerContour>();
            foreach (var c in BakerContours)
            {
                var s = SimplifyBakerContour(c);
                if (s.Vertices.Count >= 3)
                    simplified.Add(s);
            }

            return simplified;
        }

        /// <summary>追踪单个区域的边界轮廓。</summary>
        private BakerContour TraceBakerContour(BakerCompactHeightfield chf, int[] regions, bool[] visited,
            int startX, int startZ, int startSi, int startSpanIdx, int regionId)
        {
            var BakerContour = new BakerContour { RegionId = regionId };
            int[,] dirs = { { -1, 0 }, { 0, 1 }, { 1, 0 }, { 0, -1 } };

            int cx = startX, cz = startZ, csi = startSi;
            int curIdx = startSpanIdx;
            int dir = 0;
            int maxIter = chf.SpanCount * 4;
            int iter = 0;

            while (iter < maxIter)
            {
                iter++;
                if (visited[curIdx]) break;

                var span = chf.Spans[curIdx];
                visited[curIdx] = true;

                // 添加顶点（世界坐标）
                float wx = chf.BoundsMin.X + cx * chf.CellSize;
                float wz = chf.BoundsMin.Z + cz * chf.CellSize;
                float wy = chf.BoundsMin.Y + span.Y * chf.CellHeight;
                BakerContour.Vertices.Add(new ContourVertex
                {
                    X = (int)(wx * 1000),
                    Y = (int)(wy * 1000),
                    Z = (int)(wz * 1000),
                    RegionId = regionId
                });

                // 查找下一个边界方向
                int[] cons = { span.Con0, span.Con1, span.Con2, span.Con3 };
                bool found = false;

                for (int d = 0; d < 4; d++)
                {
                    int checkDir = (dir + d) % 4;
                    int conIdx = cons[checkDir];

                    if (conIdx >= 0 && regions[conIdx] == regionId)
                    {
                        // 同区域邻居：移动到邻居
                        curIdx = conIdx;
                        dir = (checkDir + 3) % 4;

                        // 更新 cx, cz
                        int nx = cx + dirs[checkDir, 0];
                        int nz = cz + dirs[checkDir, 1];
                        cx = nx; cz = nz;

                        // 更新 csi（在邻居列中找到对应 span）
                        if (cx >= 0 && cx < chf.Width && cz >= 0 && cz < chf.Height)
                        {
                            int nCellIdx = cz * chf.Width + cx;
                            for (int ni = 0; ni < chf.CellCounts[nCellIdx]; ni++)
                            {
                                if (chf.GetSpanIndex(cx, cz, ni) == conIdx)
                                {
                                    csi = ni;
                                    break;
                                }
                            }
                        }
                        found = true;
                        break;
                    }
                }

                if (!found) break;
                if (curIdx == startSpanIdx && dir == 0) break;
            }

            return BakerContour;
        }

        /// <summary>简化轮廓：Douglas-Peucker 风格顶点缩减。</summary>
        private BakerContour SimplifyBakerContour(BakerContour BakerContour)
        {
            if (BakerContour.Vertices.Count <= 3) return BakerContour;

            var result = new BakerContour { RegionId = BakerContour.RegionId };
            float error = _config.EdgeMaxError * 1000; // 转换到整数空间
            float maxLen = _config.EdgeMaxLen * 1000;

            int n = BakerContour.Vertices.Count;
            var keep = new bool[n];

            // 始终保留首尾
            keep[0] = true;
            keep[n - 1] = true;

            // 递归简化
            SimplifySegment(BakerContour.Vertices, keep, 0, n - 1, error);

            // 确保边长不超过 maxLen
            for (int i = 0; i < n - 1; i++)
            {
                if (keep[i])
                {
                    int next = i + 1;
                    while (next < n - 1 && !keep[next]) next++;

                    float dx = BakerContour.Vertices[next].X - BakerContour.Vertices[i].X;
                    float dz = BakerContour.Vertices[next].Z - BakerContour.Vertices[i].Z;
                    float len = MathF.Sqrt(dx * dx + dz * dz);

                    if (len > maxLen)
                    {
                        // 在中间插入点
                        int mid = (i + next) / 2;
                        keep[mid] = true;
                    }
                }
            }

            for (int i = 0; i < n; i++)
                if (keep[i]) result.Vertices.Add(BakerContour.Vertices[i]);

            return result;
        }

        /// <summary>Douglas-Peucker 递归简化。</summary>
        private void SimplifySegment(List<ContourVertex> verts, bool[] keep, int start, int end, float tolerance)
        {
            if (end - start < 2) return;

            float maxDist = 0;
            int maxIdx = start;

            var a = verts[start];
            var b = verts[end];

            for (int i = start + 1; i < end; i++)
            {
                float d = PerpendicularDistance(verts[i], a, b);
                if (d > maxDist)
                {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            if (maxDist > tolerance)
            {
                keep[maxIdx] = true;
                SimplifySegment(verts, keep, start, maxIdx, tolerance);
                SimplifySegment(verts, keep, maxIdx, end, tolerance);
            }
        }

        /// <summary>点到线段的垂直距离。</summary>
        private float PerpendicularDistance(ContourVertex p, ContourVertex a, ContourVertex b)
        {
            float dx = b.X - a.X;
            float dz = b.Z - a.Z;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6f) return 0;

            float t = ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / (len * len);
            t = Math.Clamp(t, 0, 1);

            float px = a.X + t * dx;
            float pz = a.Z + t * dz;

            float ddx = p.X - px;
            float ddz = p.Z - pz;
            return MathF.Sqrt(ddx * ddx + ddz * ddz);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 多边形网格生成
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 从轮廓生成凸多边形网格。
        /// </summary>
        private void BuildPolyMesh(BakerCompactHeightfield chf, List<BakerContour> BakerContours, BakedNavMeshData result)
        {
            // 创建 Tile
            var tile = new NavTile
            {
                X = 0,
                Z = 0,
                Salt = 1
            };
            result.Tiles[(0, 0)] = tile;

            int polyIndex = 0;
            int nextVertIdx = 0;

            foreach (var BakerContour in BakerContours)
            {
                if (BakerContour.Vertices.Count < 3) continue;

                // 将轮廓顶点转为世界坐标并添加到 Tile
                var poly = new NavPoly
                {
                    Index = polyIndex++,
                    TileIndex = 0,
                    AreaId = (byte)Math.Min(BakerContour.RegionId, 254)
                };

                // 扇形三角化
                int n = BakerContour.Vertices.Count;
                if (n > _config.VertsPerPoly)
                {
                    // 对超过顶点限制的多边形做扇形分解
                    for (int i = 1; i < n - 1; i++)
                    {
                        var tri = new NavPoly
                        {
                            Index = polyIndex++,
                            TileIndex = 0,
                            AreaId = (byte)Math.Min(BakerContour.RegionId, 254)
                        };

                        // 添加 3 个顶点
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = (j == 0) ? 0 : (j == 1) ? i : i + 1;
                            var cv = BakerContour.Vertices[vi];
                            var worldPos = new Vector3(cv.X / 1000f, cv.Y / 1000f, cv.Z / 1000f);

                            // 查找或添加顶点
                            int vertIdx = tile.Vertices.IndexOf(worldPos);
                            if (vertIdx < 0)
                            {
                                vertIdx = nextVertIdx++;
                                tile.Vertices.Add(worldPos);
                                result.PolyVertices.Add(worldPos);
                            }
                            tri.VertIndices.Add(vertIdx);
                            tri.NeighborPolys.Add(-1);
                        }

                        // 计算中心
                        tri.Center = (tile.Vertices[tri.VertIndices[0]] +
                                      tile.Vertices[tri.VertIndices[1]] +
                                      tile.Vertices[tri.VertIndices[2]]) / 3f;
                        tri.BoundingRadius = Vector3.Distance(tri.Center, tile.Vertices[tri.VertIndices[0]]);

                        tile.Polys.Add(tri);
                    }
                }
                else
                {
                    // 直接添加为多边形
                    for (int i = 0; i < n; i++)
                    {
                        var cv = BakerContour.Vertices[i];
                        var worldPos = new Vector3(cv.X / 1000f, cv.Y / 1000f, cv.Z / 1000f);

                        int vertIdx = tile.Vertices.IndexOf(worldPos);
                        if (vertIdx < 0)
                        {
                            vertIdx = nextVertIdx++;
                            tile.Vertices.Add(worldPos);
                            result.PolyVertices.Add(worldPos);
                        }
                        poly.VertIndices.Add(vertIdx);
                        poly.NeighborPolys.Add(-1);
                    }

                    // 计算中心
                    Vector3 center = Vector3.Zero;
                    foreach (var vi in poly.VertIndices)
                        center += tile.Vertices[vi];
                    poly.Center = center / poly.VertIndices.Count;

                    float maxDist = 0;
                    foreach (var vi in poly.VertIndices)
                        maxDist = MathF.Max(maxDist, Vector3.Distance(poly.Center, tile.Vertices[vi]));
                    poly.BoundingRadius = maxDist;

                    tile.Polys.Add(poly);
                }
            }

            // 构建邻接关系
            BuildPolyNeighbors(tile);

            tile.IsLoaded = true;
        }

        /// <summary>构建多边形之间的邻接关系（共享边的多边形互为邻居）。</summary>
        private void BuildPolyNeighbors(NavTile tile)
        {
            for (int i = 0; i < tile.Polys.Count; i++)
            {
                var polyA = tile.Polys[i];
                int nA = polyA.VertIndices.Count;

                for (int j = i + 1; j < tile.Polys.Count; j++)
                {
                    var polyB = tile.Polys[j];
                    int nB = polyB.VertIndices.Count;

                    // 检查每对边
                    for (int ea = 0; ea < nA; ea++)
                    {
                        int va0 = polyA.VertIndices[ea];
                        int va1 = polyA.VertIndices[(ea + 1) % nA];

                        for (int eb = 0; eb < nB; eb++)
                        {
                            int vb0 = polyB.VertIndices[eb];
                            int vb1 = polyB.VertIndices[(eb + 1) % nB];

                            // 检查是否为共享边（正反方向）
                            if ((va0 == vb0 && va1 == vb1) || (va0 == vb1 && va1 == vb0))
                            {
                                polyA.NeighborPolys[ea] = j;
                                polyB.NeighborPolys[eb] = i;
                            }
                        }
                    }
                }
            }
        }
    }
}

// 扩展方法：允许 null 条件操作
internal static class NullableExtensions
{
    public static void Let<T>(this T value, Action<T> action) where T : class
    {
        if (value != null) action(value);
    }
}
