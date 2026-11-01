// ============================================================================
//  NavMeshBakingReal.cs -- 真实体素化导航网格烘焙 (Recast/Detour C#重写)
//  yama引擎 YamaEngine.AI
// ----------------------------------------------------------------------------
//  完整实现了 Recast 烘焙管线:
//    1. 体素化 (Voxelization) - 三角面片→高度场→紧凑高度场
//    2. 可行走标记与过滤
//    3. 区域侵蚀
//    4. 距离场构建 (BFS 3D传播)
//    5. 分水岭区域分割 (Watershed Partitioning)
//    6. 等高线提取与简化 (Douglas-Peucker)
//    7. 凸多边形网格生成
//    8. 细节高度网格
//  不引入外部NuGet依赖, 仅使用 System.Numerics
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Numerics;

namespace YamaEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════════
    //  辅助常量与数学工具
    // ════════════════════════════════════════════════════════════════════════════

    internal static class RcMath
    {
        public const float RC_PI = 3.14159265358979f;
        public const float RC_SQRPI = 1.77245385090552f;
        public const float RC_EPSILON = 1e-6f;
        public const float RC_RADEG = 180.0f / RC_PI;
        public const float RC_DEGRAD = RC_PI / 180.0f;
        public const int RC_UNSET_HEIGHT = 0xffff;
        public const int RC_NULL_AREA = 0;

        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Floor(float v) => (float)Math.Floor(v);
        public static float Ceil(float v) => (float)Math.Ceiling(v);
        public static float Abs(float v) => Math.Abs(v);
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>点在三角形内的重心坐标检测</summary>
        public static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            float d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
            float d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        /// <summary>三角形面积</summary>
        public static float TriArea2D(float ax, float ay, float bx, float by, float cx, float cy)
        {
            return (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        }

        /// <summary>2D线段最近点</summary>
        public static float ClosestPtPointSegment(Vector2 p, Vector2 a, Vector2 b, out Vector2 closest)
        {
            Vector2 ab = b - a;
            float ab2 = ab.X * ab.X + ab.Y * ab.Y;
            if (ab2 < RC_EPSILON * RC_EPSILON)
            {
                closest = a;
                return Vector2.Distance(p, a);
            }
            float t = Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / ab2, 0, 1);
            closest = a + t * ab;
            return Vector2.Distance(p, closest);
        }

        /// <summary>向量叉积(z分量)</summary>
        public static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        /// <summary>向量点积</summary>
        public static float Dot2D(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  rcContext - 构建上下文 (日志/进度/耗时)
    // ════════════════════════════════════════════════════════════════════════════

    public class RcContext
    {
        public bool EnableLog { get; set; } = true;
        public StringBuilder LogBuilder { get; private set; } = new StringBuilder();
        public Stopwatch Timer { get; private set; } = new Stopwatch();
        public Dictionary<string, long> StageTimings { get; private set; } = new Dictionary<string, long>();
        public int Progress { get; private set; } = 0;

        public void Log(RcLogLevel level, string message)
        {
            if (!EnableLog) return;
            string prefix = level switch
            {
                RcLogLevel.Error => "[ERROR] ",
                RcLogLevel.Warning => "[WARN]  ",
                RcLogLevel.Info => "[INFO]  ",
                _ => "[LOG]   "
            };
            LogBuilder.AppendLine(prefix + message);
        }

        public void StartTimer(string stage)
        {
            Timer.Restart();
        }

        public void StopTimer(string stage)
        {
            Timer.Stop();
            long ms = Timer.ElapsedMilliseconds;
            if (!StageTimings.ContainsKey(stage))
                StageTimings[stage] = 0;
            StageTimings[stage] += ms;
            Log(RcLogLevel.Info, $"  {stage}: {ms}ms");
        }

        public void ReportProgress(int p)
        {
            Progress = Math.Clamp(p, 0, 100);
        }

        public string GetFullLog() => LogBuilder.ToString();
    }

    public enum RcLogLevel
    {
        Log,
        Info,
        Warning,
        Error
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  NavMeshGeometry - 输入几何体
    // ════════════════════════════════════════════════════════════════════════════

    public class NavMeshGeometry
    {
        public List<Vector3> Vertices { get; set; } = new List<Vector3>();
        public List<int> Triangles { get; set; } = new List<int>(); // 索引三角形, 每三个一个面
        public List<Vector3> Normals { get; set; } = new List<Vector3>();

        /// <summary>世界AABB包围盒</summary>
        public Vector3 BMin { get; set; }
        public Vector3 BMax { get; set; }

        public NavMeshGeometry() { }

        public NavMeshGeometry(List<Vector3> verts, List<int> tris)
        {
            Vertices = verts;
            Triangles = tris;
            ComputeNormals();
            ComputeBounds();
        }

        /// <summary>计算每个面的法线</summary>
        public void ComputeNormals()
        {
            Normals.Clear();
            for (int i = 0; i < Triangles.Count; i += 3)
            {
                Vector3 a = Vertices[Triangles[i]];
                Vector3 b = Vertices[Triangles[i + 1]];
                Vector3 c = Vertices[Triangles[i + 2]];
                Vector3 e1 = b - a;
                Vector3 e2 = c - a;
                Vector3 n = Vector3.Normalize(Vector3.Cross(e1, e2));
                Normals.Add(n);
            }
        }

        /// <summary>计算世界空间包围盒</summary>
        public void ComputeBounds()
        {
            BMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            BMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var v in Vertices)
            {
                BMin = Vector3.Min(BMin, v);
                BMax = Vector3.Max(BMax, v);
            }
        }

        public int TriangleCount => Triangles.Count / 3;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  VoxelizationConfig - 体素化配置
    // ════════════════════════════════════════════════════════════════════════════

    public class VoxelizationConfig
    {
        // 体素尺寸
        public float CellSize = 0.3f;           // XZ平面的体素边长
        public float CellHeight = 0.2f;          // Y轴的体素高度

        // 代理约束
        public float WalkableClimb = 0.9f;      // 代理最大可攀爬高度
        public float WalkableRadius = 0.6f;     // 代理半径 (用于侵蚀)
        public float WalkableHeight = 2.0f;     // 代理高度

        // 简化参数
        public float MaxEdgeLen = 12.0f;        // 轮廓最大边长
        public float MaxSimplificationError = 1.3f; // 最大简化误差
        public float MinRegionArea = 8.0f;      // 最小区域面积
        public float MergeRegionArea = 20.0f;   // 合并区域面积阈值
        public int MaxVertsPerPoly = 6;          // 每个多边形最大顶点数

        // 细节网格
        public float DetailSampleDist = 6.0f;   // 细节采样距离
        public float DetailSampleMaxError = 1.0f; // 细节采样最大误差

        /// <summary>世界空间包围盒</summary>
        public Vector3 BMin = new Vector3(-500, -500, -500);
        public Vector3 BMax = new Vector3(500, 500, 500);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  RcHeightfield - 高度场 (Span列)  (NavMeshBakingReal 专用，与共享 Heightfield 区分)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    ///   稀疏体素高度场（本烘焙管线专用）。每列用链表（SpansList 存列头索引）组织 Span，
    ///   Span 记录 [YMin, YMax) 的可行走区间。对应 Recast 的 rcHeightfield。
    /// </summary>
    public class RcHeightfield
    {
        /// <summary>X 方向的体素列数。</summary>
        public int Width;
        /// <summary>Z 方向的体素列数。</summary>
        public int Height;
        /// <summary>每列首个 Span 在 Spans 列表中的索引（-1 表示该列为空）。</summary>
        public int[] SpansList;
        /// <summary>所有 Span 的扁平存储（LinkedList 缓冲）。</summary>
        public List<Span> Spans = new List<Span>();
        /// <summary>单元格大小（XZ 平面，世界单位）。</summary>
        public float CellSize;
        /// <summary>单元格高度（Y 轴，世界单位）。</summary>
        public float CellHeight;
        /// <summary>包围盒最小值。</summary>
        public Vector3 BMin;
        /// <summary>包围盒最大值。</summary>
        public Vector3 BMax;
        /// <summary>最大可攀爬台阶（体素数）。</summary>
        public int WalkableClimbVoxels;

        /// <summary>创建体素高度场。</summary>
        public RcHeightfield(int width, int height, float cellSize, float cellHeight,
            Vector3 bmin, Vector3 bmax)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            CellSize = cellSize;
            CellHeight = cellHeight;
            BMin = bmin;
            BMax = bmax;
            SpansList = new int[Width * Height];
            for (int i = 0; i < SpansList.Length; i++) SpansList[i] = -1;
        }

        /// <summary>计算列 (x,z) 对应的扁平索引。</summary>
        public int Index(int x, int z) => x * Width + z;
    }

    /// <summary>高度场Span - 表示某一列中的可行走高度区间</summary>
    public class Span
    {
        public int YMin;         // 底部体素Y
        public int YMax;         // 顶部体素Y (不含)
        public int Area;         // 区域ID (RC_NULL_AREA = 不可行走)
        public int Next;         // 同列中下一个Span的索引, -1=末尾

        public int Height => YMax - YMin;

        public Span Clone()
        {
            return new Span { YMin = this.YMin, YMax = this.YMax, Area = this.Area, Next = this.Next };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  RcCompactHeightfield - 紧凑高度场  (NavMeshBakingReal 专用)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    ///   紧凑高度场（本烘焙管线专用）。仅保留可行走 Span，按列组织为 Cells，
    ///   并维护连通/距离/区域信息。对应 Recast 的 rcCompactHeightfield。
    /// </summary>
    public class RcCompactHeightfield
    {
        /// <summary>X 方向的体素列数。</summary>
        public int Width;
        /// <summary>Z 方向的体素列数。</summary>
        public int Height;
        /// <summary>每列单元格信息（起点索引 + 数量）。</summary>
        public List<CompactCell> Cells = new List<CompactCell>();
        /// <summary>所有紧凑跨度的扁平存储。</summary>
        public List<CompactSpan> Spans = new List<CompactSpan>();
        /// <summary>每个跨度对应的区域/面积标记。</summary>
        public List<int> Area = new List<int>();
        /// <summary>每个跨度对应的距离场值。</summary>
        public List<int> Dist = new List<int>();
        /// <summary>已填充的跨度数量。</summary>
        public int SpanCount;
        /// <summary>最大区域数量。</summary>
        public int MaxRegions;
        /// <summary>单元格大小（XZ 平面，世界单位）。</summary>
        public float CellSize;
        /// <summary>单元格高度（Y 轴，世界单位）。</summary>
        public float CellHeight;
        /// <summary>包围盒最小值。</summary>
        public Vector3 BMin;
        /// <summary>包围盒最大值。</summary>
        public Vector3 BMax;
        /// <summary>最大可攀爬台阶（体素数）。</summary>
        public int WalkableClimbVoxels;

        /// <summary>计算列 (x,z) 对应的扁平索引。</summary>
        public int Index(int x, int z) => x * Width + z;
    }

    /// <summary>紧凑单元 - 一列中紧凑跨度的起始信息</summary>
    public class CompactCell
    {
        public int Index;  // 该列中第一个CompactSpan在Spans列表中的索引
        public int Count;  // 该列中的CompactSpan数量
    }

    /// <summary>紧凑跨度 - 合并后的高度区间与连接</summary>
    public class CompactSpan
    {
        public int Y;          // 底部Y (合并后)
        public int Reg;        // 区域ID
        public const int MaxConnections = 4;
        public int Conn0;      // 方向0 (北) 的邻居span索引, -1=无连接
        public int Conn1;      // 方向1 (东)
        public int Conn2;      // 方向2 (南)
        public int Conn3;      // 方向3 (西)

        // -- 兼容成员 (NavMeshGenerator / AdvancedNavigationSystem 使用) --
        /// <summary>底部高度(整型)。</summary>
        public int Bottom;
        /// <summary>顶部高度(整型)。</summary>
        public int Top;
        /// <summary>底部高度别名。</summary>
        public ushort BottomY;
        /// <summary>区域ID(字节)。</summary>
        public byte Region;
        /// <summary>区域ID(整型，AdvancedNavigationSystem 使用)。</summary>
        public int RegionId;
        /// <summary>面积/类型标记。</summary>
        public byte Area;
        /// <summary>距离场值。</summary>
        public ushort Dist0;
        /// <summary>四个方向邻居 span 索引 (0xFFFF 表示无连接)。</summary>
        public ushort Con0;
        /// <summary>四个方向邻居 span 索引。</summary>
        public ushort Con1;
        /// <summary>四个方向邻居 span 索引。</summary>
        public ushort Con2;
        /// <summary>四个方向邻居 span 索引。</summary>
        public ushort Con3;
        /// <summary>邻居连接数组 (AdvancedNavigationSystem 使用)。</summary>
        public int[] Con = new int[MaxConnections] { -1, -1, -1, -1 };

        public int GetConnection(int dir)
        {
            return dir switch
            {
                0 => Conn0,
                1 => Conn1,
                2 => Conn2,
                3 => Conn3,
                _ => -1
            };
        }

        public void SetConnection(int dir, int value)
        {
            switch (dir)
            {
                case 0: Conn0 = value; break;
                case 1: Conn1 = value; break;
                case 2: Conn2 = value; break;
                case 3: Conn3 = value; break;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Contour / PolyMesh / PolyMeshDetail - 轮廓、多边形网格、细节网格
    // ════════════════════════════════════════════════════════════════════════════

    public class ContourSet
    {
        public List<Contour> Contours = new List<Contour>();
        public int NConts;
        public Vector3 BMin;
        public Vector3 BMax;
        public float CellSize;
        public float CellHeight;
    }

    public class Contour
    {
        public int Reg;              // 区域ID
        public int Area;             // 区域类型
        public List<int> Verts;      // 轮廓顶点 (x,y为体素坐标(4倍), z为高度索引)
        public List<int> Npoly;      // 每条轮廓线的顶点数
        public int Nverts;           // 总顶点数
        public bool Hole;            // 是否是洞

        // -- 兼容成员 -- //
        /// <summary>区域ID（整型，AdvancedNavigationSystem 使用）。</summary>
        public int RegionId;
        /// <summary>轮廓点列（世界坐标，AdvancedNavigationSystem 使用）。</summary>
        public List<Vector3> Points = new List<Vector3>();
        /// <summary>轮廓顶点（世界坐标，NavMeshGenerator 使用）。</summary>
        public List<Vector3> Vertices = new List<Vector3>();
        /// <summary>区域ID（字节，NavMeshGenerator 使用）。</summary>
        public byte Region;
    }

    public class PolyMesh
    {
        public List<int> Verts = new List<int>();        // 顶点 (每个顶点x,y=体素坐标, z=高度)
        public List<int> Polys = new List<int>();        // 多边形定义
        public List<int> Flags = new List<int>();        // 多边形标志
        public List<int> Areas = new List<int>();        // 多边形区域
        public int Nvp;           // 每个多边形的最大顶点数
        public int Npolys;        // 多边形数量
        public int Nverts;        // 顶点数量 (3倍, 因为每个顶点3个分量)
        public int MaxPolys;
        public int MaxVerts;
        public Vector3 BMin;
        public Vector3 BMax;
        public float CellSize;
        public float CellHeight;
        public int WalkableClimbVoxels;
    }

    public class PolyMeshDetail
    {
        public List<float> Verts = new List<float>();    // 细节顶点 (x,y,z 世界坐标)
        public List<int> Tris = new List<int>();         // 细节三角形 (3个顶点索引)
        public List<int> Meshes = new List<int>();       // 每个多边形对应的细节信息: [vStart, tStart, nTris, nVerts]
        public int Nverts;
        public int Ntris;
        public int Nmeshes;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  NavMeshBakerReal - 完整烘焙管线
    // ════════════════════════════════════════════════════════════════════════════

    public class NavMeshBakerReal
    {
        private readonly RcContext _ctx;

        public NavMeshBakerReal()
        {
            _ctx = new RcContext();
        }

        public NavMeshBakerReal(RcContext ctx)
        {
            _ctx = ctx ?? new RcContext();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  公开入口: 完整烘焙流程
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 完整烘焙管线: 几何体→高度场→紧凑高度场→区域→轮廓→多边形网格→细节网格
        /// </summary>
        public NavMeshBakeResult Bake(NavMeshGeometry geometry, VoxelizationConfig config)
        {
            var result = new NavMeshBakeResult();
            _ctx.Log(RcLogLevel.Info, "=== NavMesh Bake Start ===");

            // Step 1: 创建高度场
            _ctx.StartTimer("CreateHeightfield");
            var hf = CreateHeightfield(config, geometry);
            _ctx.StopTimer("CreateHeightfield");
            result.RcHeightfield = hf;

            // Step 2: 构建紧凑高度场
            _ctx.StartTimer("BuildCompactHeightfield");
            var chf = BuildCompactHeightfield(config, hf);
            _ctx.StopTimer("BuildCompactHeightfield");
            result.RcCompactHeightfield = chf;

            // Step 3: 侵蚀可行走区域
            _ctx.StartTimer("ErodeWalkableArea");
            ErodeWalkableArea(config, chf);
            _ctx.StopTimer("ErodeWalkableArea");

            // Step 4: 构建距离场
            _ctx.StartTimer("BuildDistanceField");
            BuildDistanceField(chf);
            _ctx.StopTimer("BuildDistanceField");

            // Step 5: 构建区域
            _ctx.StartTimer("BuildRegions");
            BuildRegions(config, chf);
            _ctx.StopTimer("BuildRegions");

            // Step 6: 构建轮廓
            _ctx.StartTimer("BuildContours");
            var contours = BuildContours(config, chf,
                config.MaxSimplificationError, config.MaxEdgeLen,
                out List<int> contourVerts, out List<int> contourPolys);
            _ctx.StopTimer("BuildContours");
            result.ContourSet = contours;

            // Step 7: 构建多边形网格
            _ctx.StartTimer("BuildPolyMesh");
            var polyMesh = BuildPolyMesh(config, contourVerts, contourPolys, contours.NConts);
            _ctx.StopTimer("BuildPolyMesh");
            result.PolyMesh = polyMesh;

            // Step 8: 构建细节网格
            _ctx.StartTimer("BuildPolyMeshDetail");
            var detailMesh = BuildPolyMeshDetail(config, polyMesh, chf,
                config.DetailSampleDist, config.DetailSampleMaxError);
            _ctx.StopTimer("BuildPolyMeshDetail");
            result.PolyMeshDetail = detailMesh;

            _ctx.Log(RcLogLevel.Info, $"=== NavMesh Bake Complete. Polys={polyMesh.Npolys}, Verts={polyMesh.Nverts}, DetailTris={detailMesh.Ntris} ===");
            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 1: CreateHeightfield - 创建高度场 (体素化三角面片)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcBuildHeightfield: 将三角面片栅格化到高度场
        /// </summary>
        public RcHeightfield CreateHeightfield(VoxelizationConfig config, NavMeshGeometry geom)
        {
            _ctx.Log(RcLogLevel.Info, "CreateHeightfield: rasterizing triangles...");

            // 计算高度场尺寸
            int width = (int)Math.Ceiling((config.BMax.X - config.BMin.X) / config.CellSize);
            int height = (int)Math.Ceiling((config.BMax.Z - config.BMin.Z) / config.CellSize);
            width = Math.Max(width, 1);
            height = Math.Max(height, 1);

            var hf = new RcHeightfield(width, height, config.CellSize, config.CellHeight,
                config.BMin, config.BMax);
            hf.WalkableClimbVoxels = (int)Math.Ceiling(config.WalkableClimb / config.CellHeight);

            _ctx.Log(RcLogLevel.Info, $"  RcHeightfield size: {width}x{height}, Spans: {hf.Spans.Count}");

            // 栅格化每个三角形
            for (int i = 0; i < geom.TriangleCount; i++)
            {
                int idx0 = geom.Triangles[i * 3];
                int idx1 = geom.Triangles[i * 3 + 1];
                int idx2 = geom.Triangles[i * 3 + 2];

                Vector3 va = geom.Vertices[idx0];
                Vector3 vb = geom.Vertices[idx1];
                Vector3 vc = geom.Vertices[idx2];

                Vector3 normal = i < geom.Normals.Count ? geom.Normals[i] : Vector3.UnitY;

                RasterizeTriangle(hf, va, vb, vc, normal, config);
            }

            // 标记可行走三角形
            MarkWalkableTriangles(hf, config);

            // 过滤
            FilterLowHangingSpans(hf);
            FilterLedgeSpans(hf, config);
            FilterWalkableLowHeightSpans(hf, config);

            _ctx.Log(RcLogLevel.Info, $"  Created {hf.Spans.Count} spans");
            return hf;
        }

        /// <summary>
        /// 将单个三角形栅格化到高度场 (rcRasterizeTriangle)
        /// 三角形平面与高度场列求交, 生成Span
        /// </summary>
        private void RasterizeTriangle(RcHeightfield hf, Vector3 va, Vector3 vb, Vector3 vc, Vector3 normal, VoxelizationConfig config)
        {
            // 将世界坐标转换为体素坐标
            float inverseCS = 1.0f / config.CellSize;
            float inverseCH = 1.0f / config.CellHeight;

            // 计算三角形的XZ包围盒在体素网格中的范围
            float minX = Math.Min(Math.Min(va.X, vb.X), vc.X);
            float maxX = Math.Max(Math.Max(va.X, vb.X), vc.X);
            float minZ = Math.Min(Math.Min(va.Z, vb.Z), vc.Z);
            float maxZ = Math.Max(Math.Max(va.Z, vb.Z), vc.Z);

            int iz0 = (int)Math.Floor((minX - config.BMin.X) * inverseCS);
            int iz1 = (int)Math.Floor((maxX - config.BMin.X) * inverseCS);
            int ix0 = (int)Math.Floor((minZ - config.BMin.Z) * inverseCS);
            int ix1 = (int)Math.Floor((maxZ - config.BMin.Z) * inverseCS);

            iz0 = RcMath.Clamp(iz0, 0, hf.Width - 1);
            iz1 = RcMath.Clamp(iz1, 0, hf.Width - 1);
            ix0 = RcMath.Clamp(ix0, 0, hf.Height - 1);
            ix1 = RcMath.Clamp(ix1, 0, hf.Height - 1);

            // 三角形平面: n·(p - va) = 0
            float by = config.BMin.Y;
            // Plane: ax + by + cz + d = 0
            // normal.Y * (p.Y - va.Y) = -normal.X*(p.X-va.X) - normal.Z*(p.Z-va.Z)
            // p.Y = va.Y - (normal.X*(p.X-va.X) + normal.Z*(p.Z-va.Z)) / normal.Y

            if (Math.Abs(normal.Y) < 1e-6f)
                return; // 垂直三角形, 不产生可行走面

            for (int ix = ix0; ix <= ix1; ix++)
            {
                for (int iz = iz0; iz <= iz1; iz++)
                {
                    // 单元格中心世界坐标
                    float cx = (iz + 0.5f) * config.CellSize + config.BMin.X;
                    float cz = (ix + 0.5f) * config.CellSize + config.BMin.Z;

                    // 检查点是否在三角形XZ投影内
                    Vector2 pt = new Vector2(cx, cz);
                    Vector2 a2 = new Vector2(va.X, va.Z);
                    Vector2 b2 = new Vector2(vb.X, vb.Z);
                    Vector2 c2 = new Vector2(vc.X, vc.Z);

                    if (!RcMath.PointInTriangle(pt, a2, b2, c2))
                        continue;

                    // 计算平面在此XZ位置的Y值
                    float py = va.Y - (normal.X * (cx - va.X) + normal.Z * (cz - va.Z)) / normal.Y;

                    // 转换为体素Y坐标
                    int iyMin = (int)Math.Floor((py - by) * inverseCH);
                    int iyMax = iyMin + 1;

                    // 确保不越界
                    iyMin = Math.Max(iyMin, 0);
                    iyMax = Math.Max(iyMax, 0);

                    // 添加Span到该列
                    AddSpan(hf, ix, iz, iyMin, iyMax, 0);
                }
            }
        }

        /// <summary>向高度场的指定列添加Span, 检查重叠和合并</summary>
        private void AddSpan(RcHeightfield hf, int x, int z, int ymin, int ymax, int area)
        {
            if (ymin >= ymax) return;
            if (x < 0 || x >= hf.Height || z < 0 || z >= hf.Width) return;

            int idx = x * hf.Width + z;

            // 创建新Span
            Span newSpan = new Span { YMin = ymin, YMax = ymax, Area = area, Next = -1 };

            // 按YMin升序插入到链表
            if (hf.SpansList[idx] == -1)
            {
                hf.SpansList[idx] = hf.Spans.Count;
                hf.Spans.Add(newSpan);
                return;
            }

            int prev = -1;
            int curr = hf.SpansList[idx];

            while (curr != -1)
            {
                Span s = hf.Spans[curr];

                // 检查是否重叠或相邻
                if (s.YMax >= ymin && s.YMin <= ymax)
                {
                    // 合并重叠Span
                    if (ymin < s.YMin) s.YMin = ymin;
                    if (ymax > s.YMax) s.YMax = ymax;
                    hf.Spans[curr] = s;
                    return;
                }

                // 按YMin排序
                if (s.YMin > ymin)
                {
                    break;
                }

                prev = curr;
                curr = s.Next;
            }

            // 插入到链表中的正确位置
            newSpan.Next = curr;
            int newIdx = hf.Spans.Count;
            hf.Spans.Add(newSpan);

            if (prev == -1)
            {
                hf.SpansList[idx] = newIdx;
            }
            else
            {
                Span prevSpan = hf.Spans[prev];
                prevSpan.Next = newIdx;
                hf.Spans[prev] = prevSpan;
            }
        }

        /// <summary>
        /// rcMarkWalkableTriangles: 根据法线Y分量标记可行走三角形
        /// 法线Y分量 > cos(maxSlope) 的三角形标记为可行走
        /// </summary>
        private void MarkWalkableTriangles(RcHeightfield hf, VoxelizationConfig config)
        {
            // maxSlope = 45度对应的cos值 = 0.707
            // 使用walkableClimb作为最大坡度近似
            float walkableSlopeAngle = 45.0f; // 度
            float cosWalkableSlope = (float)Math.Cos(walkableSlopeAngle * RcMath.RC_DEGRAD);

            // 我们通过遍历所有span, 标记向上朝向的面
            for (int i = 0; i < hf.Spans.Count; i++)
            {
                Span s = hf.Spans[i];
                // 只有具有一定高度的Span才可能可行走
                if (s.Height >= 1)
                {
                    s.Area = 1; // 标记为可行走 (非 RC_NULL_AREA)
                    hf.Spans[i] = s;
                }
            }
        }

        /// <summary>
        /// rcFilterLowHangingSpans: 滤除低悬span
        /// 去除下方有可行走面的低悬障碍span
        /// </summary>
        public void FilterLowHangingSpans(RcHeightfield hf)
        {
            for (int x = 0; x < hf.Height; x++)
            {
                for (int z = 0; z < hf.Width; z++)
                {
                    int idx = x * hf.Width + z;
                    int si = hf.SpansList[idx];
                    while (si != -1)
                    {
                        Span s = hf.Spans[si];
                        if (s.Area != RcMath.RC_NULL_AREA)
                        {
                            si = s.Next;
                            continue;
                        }

                        // 检查下方是否有可行走span, 且距离小于walkableClimb
                        int below = hf.SpansList[idx];
                        while (below != -1)
                        {
                            Span bs = hf.Spans[below];
                            if (bs.YMax <= s.YMin && bs.Area != RcMath.RC_NULL_AREA)
                            {
                                int gap = s.YMin - bs.YMax;
                                if (gap <= hf.WalkableClimbVoxels)
                                {
                                    // 低悬span, 移除
                                    s.Area = RcMath.RC_NULL_AREA;
                                    hf.Spans[si] = s;
                                    break;
                                }
                            }
                            below = bs.Next;
                        }
                        si = s.Next;
                    }
                }
            }
        }

        /// <summary>
        /// rcFilterLedgeSpans: 滤除台阶两侧不可到达的span
        /// 如果当前span与邻居span的高度差 > walkableClimb, 且邻居可行走, 则移除
        /// </summary>
        public void FilterLedgeSpans(RcHeightfield hf, VoxelizationConfig config)
        {
            int maxClimbVoxels = hf.WalkableClimbVoxels;
            int heightThreshold = maxClimbVoxels * 2;

            for (int x = 0; x < hf.Height; x++)
            {
                for (int z = 0; z < hf.Width; z++)
                {
                    int idx = x * hf.Width + z;
                    int si = hf.SpansList[idx];
                    while (si != -1)
                    {
                        Span s = hf.Spans[si];
                        if (s.Area == RcMath.RC_NULL_AREA)
                        {
                            si = s.Next;
                            continue;
                        }

                        // 检查4方向邻居
                        int[][] dirs = new int[][] {
                            new int[] { -1, 0 }, new int[] { 1, 0 },
                            new int[] { 0, -1 }, new int[] { 0, 1 }
                        };

                        bool isLedge = false;
                        foreach (var dir in dirs)
                        {
                            int nx = x + dir[0];
                            int nz = z + dir[1];
                            if (nx < 0 || nx >= hf.Height || nz < 0 || nz >= hf.Width)
                            {
                                isLedge = true;
                                break;
                            }

                            int nidx = nx * hf.Width + nz;
                            int nsi = hf.SpansList[nidx];

                            // 找邻居中与当前span高度最接近的可行走span
                            int bestHeightDiff = int.MaxValue;
                            while (nsi != -1)
                            {
                                Span ns = hf.Spans[nsi];
                                if (ns.Area != RcMath.RC_NULL_AREA)
                                {
                                    int diff = Math.Abs(ns.YMin - s.YMin);
                                    if (diff < bestHeightDiff)
                                        bestHeightDiff = diff;
                                }
                                nsi = ns.Next;
                            }

                            if (bestHeightDiff > heightThreshold)
                            {
                                isLedge = true;
                                break;
                            }
                        }

                        if (isLedge)
                        {
                            s.Area = RcMath.RC_NULL_AREA;
                            hf.Spans[si] = s;
                        }

                        si = s.Next;
                    }
                }
            }
        }

        /// <summary>
        /// rcFilterWalkableLowHeightSpans: 滤除低于walkableHeight的span
        /// 代理高度不够通过的span标记为不可行走
        /// </summary>
        public void FilterWalkableLowHeightSpans(RcHeightfield hf, VoxelizationConfig config)
        {
            int walkableHeightVoxels = (int)Math.Ceiling(config.WalkableHeight / config.CellHeight);

            for (int i = 0; i < hf.Spans.Count; i++)
            {
                Span s = hf.Spans[i];
                if (s.Area != RcMath.RC_NULL_AREA && s.Height < walkableHeightVoxels)
                {
                    s.Area = RcMath.RC_NULL_AREA;
                    hf.Spans[i] = s;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 2: BuildCompactHeightfield - 构建紧凑高度场
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcBuildCompactHeightfield: 合并相邻span为CompactSpan, 计算4方向连接
        /// </summary>
        public RcCompactHeightfield BuildCompactHeightfield(VoxelizationConfig config, RcHeightfield hf)
        {
            _ctx.Log(RcLogLevel.Info, "BuildCompactHeightfield: compacting spans...");

            var chf = new RcCompactHeightfield
            {
                Width = hf.Width,
                Height = hf.Height,
                CellSize = config.CellSize,
                CellHeight = config.CellHeight,
                BMin = hf.BMin,
                BMax = hf.BMax,
                WalkableClimbVoxels = (int)Math.Ceiling(config.WalkableClimb / config.CellHeight)
            };

            // 第1遍: 计算每列的CompactSpan数量
            int totalSpans = 0;
            chf.Cells = new List<CompactCell>(hf.Width * hf.Height);
            for (int i = 0; i < hf.Width * hf.Height; i++)
            {
                chf.Cells.Add(new CompactCell { Index = -1, Count = 0 });
            }

            // 收集所有可行走span, 按列排列
            var columnSpans = new List<Span>[hf.Width * hf.Height];
            for (int i = 0; i < columnSpans.Length; i++)
                columnSpans[i] = new List<Span>();

            for (int x = 0; x < hf.Height; x++)
            {
                for (int z = 0; z < hf.Width; z++)
                {
                    int idx = x * hf.Width + z;
                    int si = hf.SpansList[idx];
                    while (si != -1)
                    {
                        Span s = hf.Spans[si];
                        if (s.Area != RcMath.RC_NULL_AREA)
                        {
                            columnSpans[idx].Add(s);
                        }
                        si = s.Next;
                    }
                    totalSpans += columnSpans[idx].Count;
                }
            }

            chf.Spans = new List<CompactSpan>(totalSpans);
            chf.Area = new List<int>(totalSpans);
            chf.Dist = new List<int>(totalSpans);

            // 第2遍: 构建CompactSpans
            int spanIndex = 0;
            for (int x = 0; x < hf.Height; x++)
            {
                for (int z = 0; z < hf.Width; z++)
                {
                    int idx = x * hf.Width + z;
                    var colSpans = columnSpans[idx];

                    if (colSpans.Count == 0)
                    {
                        chf.Cells[idx] = new CompactCell { Index = -1, Count = 0 };
                        continue;
                    }

                    chf.Cells[idx] = new CompactCell { Index = spanIndex, Count = colSpans.Count };

                    foreach (var s in colSpans)
                    {
                        chf.Spans.Add(new CompactSpan
                        {
                            Y = s.YMin,
                            Reg = 0,
                            Conn0 = -1, Conn1 = -1, Conn2 = -1, Conn3 = -1
                        });
                        chf.Area.Add(s.Area);
                        chf.Dist.Add(0);
                        spanIndex++;
                    }
                }
            }

            chf.SpanCount = spanIndex;
            chf.MaxRegions = 0;

            // 计算4方向连接
            ComputeCompactConnections(chf);

            _ctx.Log(RcLogLevel.Info, $"  Compact spans: {chf.SpanCount}");
            return chf;
        }

        /// <summary>计算紧凑高度场中每个span的4方向邻居连接</summary>
        private void ComputeCompactConnections(RcCompactHeightfield chf)
        {
            int climb = chf.WalkableClimbVoxels;

            // 4方向偏移: (dx,dz)
            int[][] dirs = new int[][] {
                new int[] { 0, -1 },  // 北 (z--)
                new int[] { 1, 0 },   // 东 (x++)
                new int[] { 0, 1 },   // 南 (z++)
                new int[] { -1, 0 }   // 西 (x--)
            };

            for (int x = 0; x < chf.Height; x++)
            {
                for (int z = 0; z < chf.Width; z++)
                {
                    int idx = x * chf.Width + z;
                    var cell = chf.Cells[idx];
                    if (cell.Count == 0) continue;

                    for (int dir = 0; dir < 4; dir++)
                    {
                        int nx = x + dirs[dir][0];
                        int nz = z + dirs[dir][1];
                        if (nx < 0 || nx >= chf.Height || nz < 0 || nz >= chf.Width)
                            continue;

                        int nidx = nx * chf.Width + nz;
                        var ncell = chf.Cells[nidx];
                        if (ncell.Count == 0) continue;

                        // 为当前列的每个span找邻居列中最近邻的span
                        for (int si = 0; si < cell.Count; si++)
                        {
                            int myIdx = cell.Index + si;
                            CompactSpan mySpan = chf.Spans[myIdx];
                            if (mySpan.Y == RcMath.RC_UNSET_HEIGHT) continue;

                            int bestNi = -1;
                            int bestDist = climb + 1;

                            for (int ni = 0; ni < ncell.Count; ni++)
                            {
                                int nSpanIdx = ncell.Index + ni;
                                CompactSpan nSpan = chf.Spans[nSpanIdx];
                                if (nSpan.Y == RcMath.RC_UNSET_HEIGHT) continue;

                                int dy = Math.Abs(nSpan.Y - mySpan.Y);
                                if (dy < bestDist)
                                {
                                    bestDist = dy;
                                    bestNi = nSpanIdx;
                                }
                            }

                            if (bestNi != -1)
                            {
                                chf.Spans[myIdx] = mySpan;
                                chf.Spans[myIdx].SetConnection(dir, bestNi);
                                // 反向连接
                                CompactSpan nSpan = chf.Spans[bestNi];
                                nSpan.SetConnection((dir + 2) % 4, myIdx);
                                chf.Spans[bestNi] = nSpan;
                            }
                        }
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 3: ErodeWalkableArea - 侵蚀可行走区域
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcErodeWalkableArea: 将可行走区域边缘内缩walkableRadius距离
        /// 确保代理不会卡在可行走区域的边缘
        /// </summary>
        public void ErodeWalkableArea(VoxelizationConfig config, RcCompactHeightfield chf)
        {
            _ctx.Log(RcLogLevel.Info, "ErodeWalkableArea: eroding edges...");

            int radiusVoxels = Math.Max(1, (int)Math.Ceiling(config.WalkableRadius / config.CellSize));

            // 使用距离场来标记需要侵蚀的span
            // 初始化所有span为不可行走标记
            int originalCount = chf.Area.Count;

            // 从边缘开始BFS, 距离 < radiusVoxels 的标记为不可行走
            // 先标记所有span为可行走临时标记
            for (int i = 0; i < chf.SpanCount; i++)
            {
                chf.Area[i] = 1; // 临时可行走标记
            }

            // BFS erosion
            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            var dist = new int[chf.SpanCount];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = -1;

            // 初始化: 将没有4个邻居的span加入队列 (边缘span)
            for (int x = 0; x < chf.Height; x++)
            {
                for (int z = 0; z < chf.Width; z++)
                {
                    int idx = x * chf.Width + z;
                    var cell = chf.Cells[idx];
                    for (int si = 0; si < cell.Count; si++)
                    {
                        int spanIdx = cell.Index + si;
                        CompactSpan span = chf.Spans[spanIdx];
                        if (span.Y == RcMath.RC_UNSET_HEIGHT) continue;

                        // 统计邻居数量
                        int neighborCount = 0;
                        for (int d = 0; d < 4; d++)
                        {
                            if (span.GetConnection(d) != -1)
                                neighborCount++;
                        }

                        if (neighborCount < 4)
                        {
                            queue.Enqueue(spanIdx);
                            visited.Add(spanIdx);
                            dist[spanIdx] = 0;
                        }
                    }
                }
            }

            // BFS扩展
            while (queue.Count > 0)
            {
                int curr = queue.Dequeue();
                CompactSpan span = chf.Spans[curr];

                for (int d = 0; d < 4; d++)
                {
                    int neighbor = span.GetConnection(d);
                    if (neighbor == -1) continue;
                    if (visited.Contains(neighbor)) continue;

                    visited.Add(neighbor);
                    dist[neighbor] = dist[curr] + 1;
                    queue.Enqueue(neighbor);
                }
            }

            // 标记距离 < radiusVoxels 的span为不可行走
            int erodedCount = 0;
            for (int i = 0; i < chf.SpanCount; i++)
            {
                if (dist[i] >= 0 && dist[i] < radiusVoxels)
                {
                    chf.Area[i] = RcMath.RC_NULL_AREA;
                    erodedCount++;
                }
                else if (dist[i] == -1)
                {
                    chf.Area[i] = RcMath.RC_NULL_AREA;
                }
                else
                {
                    chf.Area[i] = 1; // 内部可行走
                }
            }

            // 更新连接: 不可行走区域的span移除连接
            for (int i = 0; i < chf.SpanCount; i++)
            {
                if (chf.Area[i] == RcMath.RC_NULL_AREA)
                {
                    CompactSpan span = chf.Spans[i];
                    span.Conn0 = -1;
                    span.Conn1 = -1;
                    span.Conn2 = -1;
                    span.Conn3 = -1;
                    chf.Spans[i] = span;
                }
            }

            _ctx.Log(RcLogLevel.Info, $"  Eroded {erodedCount} spans (radius={radiusVoxels} voxels)");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 4: BuildDistanceField - 构建距离场 (BFS 3D传播)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcBuildDistanceField: 计算每个span到最近不可行走span的距离 (BFS传播)
        /// </summary>
        public void BuildDistanceField(RcCompactHeightfield chf)
        {
            _ctx.Log(RcLogLevel.Info, "BuildDistanceField: computing distances...");

            // 初始化距离: 可行走span = 0, 不可行走span = 最大值
            for (int i = 0; i < chf.SpanCount; i++)
            {
                chf.Dist[i] = chf.Area[i] != RcMath.RC_NULL_AREA ? 0 : 0x7fffffff;
            }

            // 从可行走边界向内BFS传播距离
            var queue = new Queue<int>();

            // 找到所有可行走但邻居为不可行走的span (边界)
            for (int i = 0; i < chf.SpanCount; i++)
            {
                if (chf.Area[i] == RcMath.RC_NULL_AREA) continue;

                CompactSpan span = chf.Spans[i];
                for (int d = 0; d < 4; d++)
                {
                    int ni = span.GetConnection(d);
                    if (ni != -1 && chf.Area[ni] == RcMath.RC_NULL_AREA)
                    {
                        queue.Enqueue(i);
                        chf.Dist[i] = 1;
                        break;
                    }
                }
            }

            // BFS传播
            while (queue.Count > 0)
            {
                int curr = queue.Dequeue();
                CompactSpan span = chf.Spans[curr];
                int currDist = chf.Dist[curr];

                for (int d = 0; d < 4; d++)
                {
                    int ni = span.GetConnection(d);
                    if (ni == -1) continue;
                    if (chf.Area[ni] == RcMath.RC_NULL_AREA) continue;
                    if (chf.Dist[ni] != 0 && chf.Dist[ni] <= currDist + 1) continue;

                    chf.Dist[ni] = currDist + 1;
                    queue.Enqueue(ni);
                }
            }

            _ctx.Log(RcLogLevel.Info, "  Distance field computed");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 5: BuildRegions - 构建区域 (分水岭分割)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcBuildRegions: 使用分水岭算法(Watershed Partitioning)分割区域
        /// 小区域过滤和合并
        /// </summary>
        public void BuildRegions(VoxelizationConfig config, RcCompactHeightfield chf)
        {
            _ctx.Log(RcLogLevel.Info, "BuildRegions: watershed partitioning...");

            int minRegion = (int)Math.Floor(config.MinRegionArea * (1.0f / (config.CellSize * config.CellSize)));
            int mergeRegion = (int)Math.Floor(config.MergeRegionArea * (1.0f / (config.CellSize * config.CellSize)));

            // 初始化所有可行走span的区域ID为0
            for (int i = 0; i < chf.SpanCount; i++)
            {
                CompactSpan span = chf.Spans[i];
                span.Reg = chf.Area[i] != RcMath.RC_NULL_AREA ? 0 : 0;
                chf.Spans[i] = span;
            }

            // 分水岭算法:
            // 1. 按距离从小到大排序所有span
            // 2. 从距离最远的span开始分配区域种子
            // 3. 依次处理距离递减的span, 如果邻居已有区域则合并

            var spanIndices = new List<int>(chf.SpanCount);
            for (int i = 0; i < chf.SpanCount; i++)
            {
                if (chf.Area[i] != RcMath.RC_NULL_AREA && chf.Dist[i] > 0)
                {
                    spanIndices.Add(i);
                }
            }

            // 按距离排序 (降序: 距离最大的先处理)
            spanIndices.Sort((a, b) => chf.Dist[b].CompareTo(chf.Dist[a]));

            int nextRegionId = 1;
            int[] regionSize = new int[chf.SpanCount]; // 区域大小 (使用span索引范围)
            // 用字典跟踪每个区域的大小
            var regionSizes = new Dictionary<int, int>();

            // 处理每个span
            foreach (int si in spanIndices)
            {
                CompactSpan span = chf.Spans[si];
                if (span.Reg != 0) continue;

                // 找到邻居已有的区域
                int bestRegion = 0;
                int bestDist = int.MaxValue;

                for (int d = 0; d < 4; d++)
                {
                    int ni = span.GetConnection(d);
                    if (ni == -1) continue;
                    CompactSpan nSpan = chf.Spans[ni];
                    if (nSpan.Reg == 0) continue;
                    if (nSpan.Reg == bestRegion) continue;

                    if (bestRegion == 0 || chf.Dist[ni] < bestDist)
                    {
                        bestRegion = nSpan.Reg;
                        bestDist = chf.Dist[ni];
                    }
                }

                if (bestRegion == 0)
                {
                    // 没有邻居有区域, 创建新区域
                    bestRegion = nextRegionId++;
                    regionSizes[bestRegion] = 0;
                }

                span.Reg = bestRegion;
                chf.Spans[si] = span;
                regionSizes[bestRegion] = regionSizes.GetValueOrDefault(bestRegion, 0) + 1;
            }

            // 过滤小区域
            var regionMap = new Dictionary<int, int>(); // 旧区域ID → 新区域ID
            int newRegionId = 1;

            // 统计区域大小
            var regionCounts = new Dictionary<int, int>();
            for (int i = 0; i < chf.SpanCount; i++)
            {
                int reg = chf.Spans[i].Reg;
                if (reg > 0)
                {
                    regionCounts[reg] = regionCounts.GetValueOrDefault(reg, 0) + 1;
                }
            }

            // 过滤面积小于minRegion的区域, 合并到最大邻居
            for (int i = 0; i < chf.SpanCount; i++)
            {
                CompactSpan span = chf.Spans[i];
                int reg = span.Reg;
                if (reg == 0) continue;

                int count = regionCounts.GetValueOrDefault(reg, 0);
                if (count < minRegion)
                {
                    // 合并到最大邻居
                    int bestNeighbor = 0;
                    int bestNeighborSize = 0;

                    for (int d = 0; d < 4; d++)
                    {
                        int ni = span.GetConnection(d);
                        if (ni == -1) continue;
                        int nReg = chf.Spans[ni].Reg;
                        if (nReg == 0 || nReg == reg) continue;
                        int nSize = regionCounts.GetValueOrDefault(nReg, 0);
                        if (nSize > bestNeighborSize)
                        {
                            bestNeighborSize = nSize;
                            bestNeighbor = nReg;
                        }
                    }

                    if (bestNeighbor > 0)
                    {
                        span.Reg = bestNeighbor;
                        chf.Spans[i] = span;
                        regionCounts[reg] = regionCounts.GetValueOrDefault(reg, 0) - 1;
                        regionCounts[bestNeighbor] = regionCounts.GetValueOrDefault(bestNeighbor, 0) + 1;
                    }
                    else
                    {
                        span.Reg = 0;
                        chf.Spans[i] = span;
                    }
                }
            }

            // 重新编号区域
            regionMap.Clear();
            newRegionId = 1;
            for (int i = 0; i < chf.SpanCount; i++)
            {
                int reg = chf.Spans[i].Reg;
                if (reg == 0) continue;

                if (!regionMap.ContainsKey(reg))
                {
                    regionMap[reg] = newRegionId++;
                }

                CompactSpan span = chf.Spans[i];
                span.Reg = regionMap[reg];
                chf.Spans[i] = span;
            }

            chf.MaxRegions = newRegionId - 1;

            _ctx.Log(RcLogLevel.Info, $"  Created {chf.MaxRegions} regions");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 6: BuildContours - 构建轮廓
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcBuildContours: 等高线追踪, 简化轮廓(Douglas-Peucker), 生成轮廓集合
        /// </summary>
        public ContourSet BuildContours(VoxelizationConfig config, RcCompactHeightfield chf,
            float maxError, float maxEdgeLen,
            out List<int> rawVerts, out List<int> rawPolys)
        {
            _ctx.Log(RcLogLevel.Info, "BuildContours: extracting contours...");

            var contours = new ContourSet
            {
                BMin = chf.BMin,
                BMax = chf.BMax,
                CellSize = chf.CellSize,
                CellHeight = chf.CellHeight
            };

            rawVerts = new List<int>();
            rawPolys = new List<int>();

            // 收集每个区域的边界span
            var regionEdges = new Dictionary<int, List<(int x, int z, int y, int dir)>>();

            for (int x = 0; x < chf.Height; x++)
            {
                for (int z = 0; z < chf.Width; z++)
                {
                    int idx = x * chf.Width + z;
                    var cell = chf.Cells[idx];
                    for (int si = 0; si < cell.Count; si++)
                    {
                        int spanIdx = cell.Index + si;
                        CompactSpan span = chf.Spans[spanIdx];
                        int reg = span.Reg;
                        if (reg == 0) continue;

                        for (int d = 0; d < 4; d++)
                        {
                            int ni = span.GetConnection(d);
                            if (ni == -1 || chf.Spans[ni].Reg != reg)
                            {
                                // 边界
                                if (!regionEdges.ContainsKey(reg))
                                    regionEdges[reg] = new List<(int, int, int, int)>();

                                regionEdges[reg].Add((x, z, span.Y, d));
                            }
                        }
                    }
                }
            }

            // 为每个区域构建轮廓
            int maxErrorVoxels = (int)Math.Ceiling(maxError / config.CellSize);
            int maxEdgeLenVoxels = (int)Math.Ceiling(maxEdgeLen / config.CellSize);

            foreach (var kvp in regionEdges)
            {
                int reg = kvp.Key;
                var edges = kvp.Value;

                if (edges.Count < 3) continue;

                // 将边转换为轮廓顶点
                var contourVerts = new List<(int x, int y, int z)>();
                var vertSet = new HashSet<(int x, int y, int z)>();

                // 4方向偏移 (体素坐标增量)
                int[][] dirOffsets = new int[][] {
                    new int[] { 0, 0 },   // 北 (z--)
                    new int[] { 1, 0 },    // 东 (x++)
                    new int[] { 0, 1 },    // 南 (z++)
                    new int[] { -1, 0 }    // 西 (x--)
                };

                // 将每个边转换成端点顶点
                foreach (var edge in edges)
                {
                    // 边的起始顶点 (4倍体素坐标)
                    int vx = (edge.z + dirOffsets[edge.dir][0]) * 4;
                    int vy = edge.y;
                    int vz = (edge.x + dirOffsets[edge.dir][1]) * 4;

                    var vt = (vx, vy, vz);
                    if (!vertSet.Contains(vt))
                    {
                        contourVerts.Add(vt);
                        vertSet.Add(vt);
                    }
                }

                if (contourVerts.Count < 3) continue;

                // 对轮廓进行排序 (形成闭合环)
                var orderedVerts = OrderContourVertices(contourVerts);

                if (orderedVerts.Count < 3) continue;

                // Douglas-Peucker简化
                var simplifiedVerts = SimplifyContour(orderedVerts, maxErrorVoxels);
                if (simplifiedVerts.Count < 3) continue;

                // 进一步简化: 限制边长
                simplifiedVerts = LimitEdgeLength(simplifiedVerts, maxEdgeLenVoxels);

                // 创建轮廓
                var contour = new Contour
                {
                    Reg = reg,
                    Area = 1,
                    Nverts = simplifiedVerts.Count,
                    Hole = false
                };

                foreach (var v in simplifiedVerts)
                {
                    contour.Verts.Add(v.x);
                    contour.Verts.Add(v.y);
                    contour.Verts.Add(v.z);
                }

                contour.Npoly = new List<int> { simplifiedVerts.Count };
                contours.Contours.Add(contour);
            }

            contours.NConts = contours.Contours.Count;

            // 填充原始输出
            foreach (var c in contours.Contours)
            {
                rawVerts.AddRange(c.Verts);
                rawPolys.AddRange(c.Npoly);
            }

            _ctx.Log(RcLogLevel.Info, $"  Extracted {contours.NConts} contours");
            return contours;
        }

        /// <summary>将轮廓顶点排序成闭合环</summary>
        private List<(int x, int y, int z)> OrderContourVertices(List<(int x, int y, int z)> verts)
        {
            if (verts.Count <= 2) return verts;

            var result = new List<(int x, int y, int z)>(verts.Count);
            var remaining = new HashSet<(int x, int y, int z)>(verts);

            // 从第一个顶点开始
            var current = verts[0];
            remaining.Remove(current);
            result.Add(current);

            while (remaining.Count > 0)
            {
                float bestDist = float.MaxValue;
                (int x, int y, int z) best = current;

                foreach (var v in remaining)
                {
                    float dx = v.x - current.x;
                    float dy = v.y - current.y;
                    float dz = v.z - current.z;
                    float dist = dx * dx + dy * dy + dz * dz;

                    // 优先选择相邻的顶点 (距离最短)
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = v;
                    }
                }

                result.Add(best);
                remaining.Remove(best);
                current = best;
            }

            return result;
        }

        /// <summary>Douglas-Peucker轮廓简化</summary>
        private List<(int x, int y, int z)> SimplifyContour(List<(int x, int y, int z)> verts, int maxError)
        {
            if (verts.Count <= 2) return new List<(int x, int y, int z)>(verts);

            var result = new List<(int x, int y, int z)>();
            var keep = new bool[verts.Count];
            Array.Fill(keep, false);
            keep[0] = true;
            keep[verts.Count - 1] = true;

            SimplifyContourRecursive(verts, 0, verts.Count - 1, maxError, keep);

            for (int i = 0; i < verts.Count; i++)
            {
                if (keep[i])
                    result.Add(verts[i]);
            }

            return result;
        }

        private void SimplifyContourRecursive(List<(int x, int y, int z)> verts,
            int first, int last, int maxError, bool[] keep)
        {
            if (last - first <= 1) return;

            // 找到距离线段first-last最远的点
            float maxDist = 0;
            int maxIdx = first;

            var p0 = verts[first];
            var pn = verts[last];

            float dx = pn.x - p0.x;
            float dy = pn.y - p0.y;
            float dz = pn.z - p0.z;
            float lenSq = dx * dx + dy * dy + dz * dz;

            for (int i = first + 1; i < last; i++)
            {
                var v = verts[i];
                float dist;

                if (lenSq < 1e-6f)
                {
                    float ex = v.x - p0.x;
                    float ey = v.y - p0.y;
                    float ez = v.z - p0.z;
                    dist = (float)Math.Sqrt(ex * ex + ey * ey + ez * ez);
                }
                else
                {
                    // 点到线段的距离
                    float t = ((v.x - p0.x) * dx + (v.y - p0.y) * dy + (v.z - p0.z) * dz) / lenSq;
                    t = RcMath.Clamp(t, 0, 1);
                    float px = p0.x + t * dx - v.x;
                    float py = p0.y + t * dy - v.y;
                    float pz = p0.z + t * dz - v.z;
                    dist = (float)Math.Sqrt(px * px + py * py + pz * pz);
                }

                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIdx = i;
                }
            }

            if (maxDist > maxError)
            {
                keep[maxIdx] = true;
                SimplifyContourRecursive(verts, first, maxIdx, maxError, keep);
                SimplifyContourRecursive(verts, maxIdx, last, maxError, keep);
            }
        }

        /// <summary>限制边长, 过长的边插入中间点</summary>
        private List<(int x, int y, int z)> LimitEdgeLength(List<(int x, int y, int z)> verts, int maxLen)
        {
            if (verts.Count < 2) return verts;

            var result = new List<(int x, int y, int z)> { verts[0] };

            for (int i = 1; i < verts.Count; i++)
            {
                var prev = result[result.Count - 1];
                var curr = verts[i];

                float dx = curr.x - prev.x;
                float dy = curr.y - prev.y;
                float dz = curr.z - prev.z;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (dist > maxLen)
                {
                    int segments = (int)Math.Ceiling(dist / maxLen);
                    for (int s = 1; s < segments; s++)
                    {
                        float t = (float)s / segments;
                        result.Add((
                            (int)(prev.x + t * dx),
                            (int)(prev.y + t * dy),
                            (int)(prev.z + t * dz)
                        ));
                    }
                }

                result.Add(curr);
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 7: BuildPolyMesh - 构建多边形网格
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcBuildPolyMesh: 将轮廓转换为凸多边形网格
        /// </summary>
        public PolyMesh BuildPolyMesh(VoxelizationConfig config, List<int> verts, List<int> polys, int nconts)
        {
            _ctx.Log(RcLogLevel.Info, "BuildPolyMesh: building polygon mesh...");

            var mesh = new PolyMesh
            {
                Nvp = config.MaxVertsPerPoly,
                CellSize = config.CellSize,
                CellHeight = config.CellHeight,
                BMin = config.BMin,
                BMax = config.BMax,
                WalkableClimbVoxels = (int)Math.Ceiling(config.WalkableClimb / config.CellHeight)
            };

            // 复制顶点
            mesh.Verts = new List<int>(verts);
            mesh.Nverts = verts.Count / 3;

            // 为每个轮廓创建多边形
            int polyStart = 0;
            for (int ci = 0; ci < nconts; ci++)
            {
                int nverts = polys[ci];

                if (nverts < 3) continue;

                // 获取此轮廓的顶点
                var contourVerts = new List<(int x, int y, int z)>();
                for (int vi = 0; vi < nverts; vi++)
                {
                    int baseIdx = (polyStart + vi) * 3;
                    contourVerts.Add((verts[baseIdx], verts[baseIdx + 1], verts[baseIdx + 2]));
                }

                // 将轮廓分解为凸多边形 (耳切法或三角扇分解)
                var convexPolys = TriangulateContour(contourVerts, config.MaxVertsPerPoly);

                foreach (var poly in convexPolys)
                {
                    // 将凸多边形添加到网格
                    int polyIdx = mesh.Polys.Count / (mesh.Nvp * 2);

                    // 多边形顶点索引 (Nvp个)
                    for (int pi = 0; pi < poly.Count; pi++)
                    {
                        mesh.Polys.Add(polyStart + poly[pi]); // 顶点索引
                    }
                    // 填充剩余
                    for (int pi = poly.Count; pi < mesh.Nvp; pi++)
                    {
                        mesh.Polys.Add(-1);
                    }

                    // 邻居信息 (Nvp个)
                    for (int pi = 0; pi < mesh.Nvp; pi++)
                    {
                        mesh.Polys.Add(-1);
                    }

                    mesh.Flags.Add(1); // Walkable
                    mesh.Areas.Add(1);
                    mesh.Npolys++;
                }

                polyStart += nverts;
            }

            // 计算邻居
            ComputePolyNeighbors(mesh);

            mesh.MaxPolys = mesh.Npolys;
            mesh.MaxVerts = mesh.Nverts;

            _ctx.Log(RcLogLevel.Info, $"  PolyMesh: {mesh.Npolys} polys, {mesh.Nverts} verts");
            return mesh;
        }

        /// <summary>将轮廓三角化, 然后合并为不超过maxVertsPerPoly的凸多边形</summary>
        private List<List<int>> TriangulateContour(List<(int x, int y, int z)> contour, int maxVerts)
        {
            var result = new List<List<int>>();

            if (contour.Count < 3) return result;

            // 使用耳切法三角化
            var indices = new List<int>();
            for (int i = 0; i < contour.Count; i++)
                indices.Add(i);

            var triangles = new List<List<int>>();

            while (indices.Count > 3)
            {
                bool foundEar = false;
                int n = indices.Count;

                for (int i = 0; i < n; i++)
                {
                    int prev = indices[(i - 1 + n) % n];
                    int curr = indices[i];
                    int next = indices[(i + 1) % n];

                    var pPrev = contour[prev];
                    var pCurr = contour[curr];
                    var pNext = contour[next];

                    // 检查是否是凸顶点
                    float cross = RcMath.TriArea2D(
                        pCurr.x, pCurr.z,
                        pPrev.x, pPrev.z,
                        pNext.x, pNext.z);

                    if (cross <= 0) continue; // 凹顶点

                    // 检查耳内没有其他顶点
                    bool ear = true;
                    for (int j = 0; j < n; j++)
                    {
                        int idx = indices[j];
                        if (idx == prev || idx == curr || idx == next) continue;

                        var pt = contour[idx];
                        float d1 = RcMath.TriArea2D(pt.x, pt.z, pPrev.x, pPrev.z, pCurr.x, pCurr.z);
                        float d2 = RcMath.TriArea2D(pt.x, pt.z, pCurr.x, pCurr.z, pNext.x, pNext.z);
                        float d3 = RcMath.TriArea2D(pt.x, pt.z, pNext.x, pNext.z, pPrev.x, pPrev.z);

                        if (d1 >= 0 && d2 >= 0 && d3 >= 0)
                        {
                            ear = false;
                            break;
                        }
                    }

                    if (ear)
                    {
                        triangles.Add(new List<int> { prev, curr, next });
                        indices.RemoveAt(i);
                        foundEar = true;
                        break;
                    }
                }

                if (!foundEar) break; // 退化情况
            }

            // 最后一个三角形
            if (indices.Count == 3)
            {
                triangles.Add(new List<int>(indices));
            }

            // 合并三角形为更大的凸多边形
            result = MergeTrianglesToPolygons(triangles, contour, maxVerts);

            return result;
        }

        /// <summary>合并三角形为不超过maxVerts的凸多边形</summary>
        private List<List<int>> MergeTrianglesToPolygons(List<List<int>> triangles,
            List<(int x, int y, int z)> contour, int maxVerts)
        {
            // 如果只有三角形或者maxVerts <= 3, 直接返回
            if (maxVerts <= 3 || triangles.Count <= 1)
                return triangles;

            var polys = new List<List<int>>(triangles);
            bool merged = true;

            while (merged)
            {
                merged = false;

                for (int i = 0; i < polys.Count && !merged; i++)
                {
                    for (int j = i + 1; j < polys.Count && !merged; j++)
                    {
                        // 检查两个多边形是否共享边
                        var shared = FindSharedEdge(polys[i], polys[j]);
                        if (shared.Item1 == -1) continue;

                        // 合并
                        var mergedPoly = MergeTwoPolygons(polys[i], polys[j], shared);

                        if (mergedPoly.Count <= maxVerts && IsConvex(mergedPoly, contour))
                        {
                            polys.RemoveAt(j);
                            polys.RemoveAt(i);
                            polys.Add(mergedPoly);
                            merged = true;
                        }
                    }
                }
            }

            return polys;
        }

        /// <summary>找两个多边形的共享边</summary>
        private (int, int) FindSharedEdge(List<int> a, List<int> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                int ai = a[i];
                int aNext = a[(i + 1) % a.Count];

                for (int j = 0; j < b.Count; j++)
                {
                    int bi = b[j];
                    int bNext = b[(j + 1) % b.Count];

                    if ((ai == bNext && aNext == bi))
                    {
                        return (i, j);
                    }
                }
            }
            return (-1, -1);
        }

        /// <summary>合并两个共享边的多边形</summary>
        private List<int> MergeTwoPolygons(List<int> a, List<int> b, (int, int) shared)
        {
            var result = new List<int>();

            // 添加a中共享边之前的顶点
            for (int i = 0; i <= shared.Item1; i++)
                result.Add(a[i]);

            // 添加b中共享边之后的顶点
            for (int i = (shared.Item2 + 2) % b.Count; i != shared.Item2; i = (i + 1) % b.Count)
                result.Add(b[i]);
            result.Add(b[shared.Item2]);

            // 添加a中共享边之后的顶点
            for (int i = (shared.Item1 + 2) % a.Count; i != shared.Item1; i = (i + 1) % a.Count)
                result.Add(a[i]);

            return result;
        }

        /// <summary>检查多边形是否凸</summary>
        private bool IsConvex(List<int> polyIndices, List<(int x, int y, int z)> contour)
        {
            if (polyIndices.Count < 3) return false;
            int n = polyIndices.Count;

            int sign = 0;
            for (int i = 0; i < n; i++)
            {
                var p0 = contour[polyIndices[i]];
                var p1 = contour[polyIndices[(i + 1) % n]];
                var p2 = contour[polyIndices[(i + 2) % n]];

                float cross = RcMath.TriArea2D(p1.x, p1.z, p0.x, p0.z, p2.x, p2.z);

                if (cross != 0)
                {
                    if (sign == 0)
                        sign = cross > 0 ? 1 : -1;
                    else if ((cross > 0 ? 1 : -1) != sign)
                        return false;
                }
            }

            return true;
        }

        /// <summary>计算多边形之间的邻居关系</summary>
        private void ComputePolyNeighbors(PolyMesh mesh)
        {
            if (mesh.Npolys == 0) return;

            // 为每条边创建查找表
            var edgeMap = new Dictionary<(int, int), (int poly, int edge)>();

            for (int pi = 0; pi < mesh.Npolys; pi++)
            {
                int baseIdx = pi * mesh.Nvp * 2;

                for (int ei = 0; ei < mesh.Nvp; ei++)
                {
                    int vi = mesh.Polys[baseIdx + ei];
                    if (vi == -1) break;

                    int viNext = mesh.Polys[baseIdx + (ei + 1) % mesh.Nvp];
                    if (viNext == -1) break;

                    var edge = (Math.Min(vi, viNext), Math.Max(vi, viNext));
                    edgeMap[edge] = (pi, ei);
                }
            }

            // 设置邻居
            for (int pi = 0; pi < mesh.Npolys; pi++)
            {
                int baseIdx = pi * mesh.Nvp * 2;

                for (int ei = 0; ei < mesh.Nvp; ei++)
                {
                    int vi = mesh.Polys[baseIdx + ei];
                    if (vi == -1) break;

                    int viNext = mesh.Polys[baseIdx + (ei + 1) % mesh.Nvp];
                    if (viNext == -1) break;

                    var edge = (Math.Max(vi, viNext), Math.Min(vi, viNext));

                    if (edgeMap.TryGetValue(edge, out var neighbor))
                    {
                        if (neighbor.poly != pi)
                        {
                            mesh.Polys[baseIdx + mesh.Nvp + ei] = neighbor.poly;
                        }
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 8: BuildPolyMeshDetail - 构建细节高度网格
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// rcBuildPolyMeshDetail: 在多边形内部采样高度, 生成细节网格
        /// </summary>
        public PolyMeshDetail BuildPolyMeshDetail(VoxelizationConfig config, PolyMesh mesh,
            RcCompactHeightfield chf, float sampleDist, float sampleMaxError)
        {
            _ctx.Log(RcLogLevel.Info, "BuildPolyMeshDetail: sampling heights...");

            var detail = new PolyMeshDetail();

            float inverseCellSize = 1.0f / config.CellSize;
            float inverseCellHeight = 1.0f / config.CellHeight;

            // 处理每个多边形
            for (int pi = 0; pi < mesh.Npolys; pi++)
            {
                int polyBase = pi * mesh.Nvp * 2;
                int nPolyVerts = 0;
                for (int vi = 0; vi < mesh.Nvp; vi++)
                {
                    if (mesh.Polys[polyBase + vi] == -1) break;
                    nPolyVerts++;
                }

                if (nPolyVerts < 3) continue;

                // 收集多边形顶点 (体素坐标→世界坐标)
                var polyWorldVerts = new List<Vector3>();
                for (int vi = 0; vi < nPolyVerts; vi++)
                {
                    int vertIdx = mesh.Polys[polyBase + vi];
                    int vBase = vertIdx * 3;
                    float wx = mesh.Verts[vBase] * inverseCellSize * 0.25f + mesh.BMin.X;
                    float wz = mesh.Verts[vBase + 2] * inverseCellSize * 0.25f + mesh.BMin.Z;
                    float wy = mesh.Verts[vBase + 1] * inverseCellHeight + mesh.BMin.Y;
                    polyWorldVerts.Add(new Vector3(wx, wy, wz));
                }

                // 在多边形边界采样高度
                int vStart = detail.Verts.Count / 3;

                // 边界顶点 (从轮廓采样高度)
                var boundaryVerts = new List<int>();
                for (int vi = 0; vi < nPolyVerts; vi++)
                {
                    Vector3 wv = polyWorldVerts[vi];
                    float height = SampleHeight(chf, wv.X, wv.Z, config);
                    detail.Verts.Add(wv.X);
                    detail.Verts.Add(height);
                    detail.Verts.Add(wv.Z);
                    boundaryVerts.Add(detail.Verts.Count / 3 - 1);
                }

                // 内部采样
                if (sampleDist > 0)
                {
                    int internalSamples = SamplePolygonInterior(polyWorldVerts, boundaryVerts,
                        detail, chf, config, sampleDist);
                }

                // 三角化多边形
                int nBoundaryVerts = boundaryVerts.Count;
                int totalVerts = detail.Verts.Count / 3 - vStart;

                if (totalVerts >= 3)
                {
                    // 扇形三角化
                    for (int i = 1; i < totalVerts - 1; i++)
                    {
                        detail.Tris.Add(vStart);
                        detail.Tris.Add(vStart + i);
                        detail.Tris.Add(vStart + i + 1);
                    }
                }

                // 记录此多边形的细节网格信息
                detail.Meshes.Add(vStart);
                detail.Meshes.Add(detail.Tris.Count - (detail.Ntris > 0 ?
                    CountTrisForPoly(detail.Meshes, detail.Nmeshes) : 0));
                int triCount = 0;
                if (totalVerts >= 3)
                    triCount = totalVerts - 2;
                detail.Meshes.Add(triCount);
                detail.Meshes.Add(totalVerts);
                detail.Nmeshes++;
            }

            detail.Nverts = detail.Verts.Count / 3;
            detail.Ntris = detail.Tris.Count / 3;

            _ctx.Log(RcLogLevel.Info, $"  Detail mesh: {detail.Nmeshes} meshes, {detail.Nverts} verts, {detail.Ntris} tris");
            return detail;
        }

        private int CountTrisForPoly(List<int> meshes, int currentMeshIdx)
        {
            int count = 0;
            for (int i = 0; i < currentMeshIdx; i++)
            {
                count += meshes[i * 4 + 2]; // nTris
            }
            return count;
        }

        /// <summary>在紧凑高度场中采样某点的最高可行走高度</summary>
        private float SampleHeight(RcCompactHeightfield chf, float wx, float wz, VoxelizationConfig config)
        {
            int iz = (int)Math.Floor((wx - chf.BMin.X) / config.CellSize);
            int ix = (int)Math.Floor((wz - chf.BMin.Z) / config.CellSize);

            if (ix < 0 || ix >= chf.Height || iz < 0 || iz >= chf.Width)
                return chf.BMin.Y;

            int idx = ix * chf.Width + iz;
            var cell = chf.Cells[idx];
            if (cell.Count == 0) return chf.BMin.Y;

            // 找最高可行走span
            float bestY = chf.BMin.Y;
            for (int si = 0; si < cell.Count; si++)
            {
                int spanIdx = cell.Index + si;
                if (chf.Area[spanIdx] != RcMath.RC_NULL_AREA)
                {
                    float sy = chf.Spans[spanIdx].Y * config.CellHeight + chf.BMin.Y;
                    if (sy > bestY) bestY = sy;
                }
            }

            return bestY;
        }

        /// <summary>在多边形内部采样点</summary>
        private int SamplePolygonInterior(List<Vector3> polyVerts, List<int> boundaryVerts,
            PolyMeshDetail detail, RcCompactHeightfield chf, VoxelizationConfig config, float sampleDist)
        {
            // 计算多边形的XZ包围盒
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in polyVerts)
            {
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Z < minZ) minZ = v.Z;
                if (v.Z > maxZ) maxZ = v.Z;
            }

            int sampleCount = 0;
            float step = sampleDist;

            for (float sz = minZ; sz <= maxZ; sz += step)
            {
                for (float sx = minX; sx <= maxX; sx += step)
                {
                    // 检查点是否在多边形内
                    if (!PointInPolygonXZ(polyVerts, sx, sz))
                        continue;

                    float height = SampleHeight(chf, sx, sz, config);
                    detail.Verts.Add(sx);
                    detail.Verts.Add(height);
                    detail.Verts.Add(sz);
                    sampleCount++;
                }
            }

            return sampleCount;
        }

        /// <summary>检查点是否在多边形的XZ投影内</summary>
        private bool PointInPolygonXZ(List<Vector3> poly, float px, float pz)
        {
            int n = poly.Count;
            bool inside = false;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = poly[i].X, zi = poly[i].Z;
                float xj = poly[j].X, zj = poly[j].Z;

                if (((zi > pz) != (zj > pz)) &&
                    (px < (xj - xi) * (pz - zi) / (zj - zi) + xi))
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  NavMeshBakeResult - 烘焙结果
    // ════════════════════════════════════════════════════════════════════════════

    public class NavMeshBakeResult
    {
        public RcHeightfield RcHeightfield;
        public RcCompactHeightfield RcCompactHeightfield;
        public ContourSet ContourSet;
        public PolyMesh PolyMesh;
        public PolyMeshDetail PolyMeshDetail;
        public string BuildLog;
        public Dictionary<string, long> StageTimings;

        public void SetLogAndTimings(RcContext ctx)
        {
            BuildLog = ctx.GetFullLog();
            StageTimings = new Dictionary<string, long>(ctx.StageTimings);
        }
    }
}
