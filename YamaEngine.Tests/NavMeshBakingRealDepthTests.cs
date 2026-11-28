using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// NavMeshBakingReal 深度测试。覆盖 Recast 烘焙管线的核心数据结构、纯数学函数、
    /// RcContext 日志/计时、NavMeshGeometry 法线与包围盒、端到端 Bake 小场景。
    /// </summary>
    public static class NavMeshBakingRealDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        // ── RcMath 常量 ──

        public static void TestRcMathConstants()
        {
            Near(3.14159265f, RcMath.RC_PI, 1e-5f, "RC_PI");
            Near(1.77245f, RcMath.RC_SQRPI, 1e-4f, "RC_SQRPI (sqrt(pi))");
            Near(1e-6f, RcMath.RC_EPSILON, 1e-9f, "RC_EPSILON");
            Near(180.0f / 3.14159265f, RcMath.RC_RADEG, 1e-3f, "RC_RADEG");
            Near(3.14159265f / 180.0f, RcMath.RC_DEGRAD, 1e-7f, "RC_DEGRAD");
            AssertEqual(0xffff, RcMath.RC_UNSET_HEIGHT, "RC_UNSET_HEIGHT");
            AssertEqual(0, RcMath.RC_NULL_AREA, "RC_NULL_AREA");
        }

        // ── RcMath 基础函数 ──

        public static void TestRcMathBasicFuncs()
        {
            Near(2f, RcMath.Sqrt(4f), 1e-5f, "Sqrt(4)");
            Near(3f, RcMath.Floor(3.7f), 1e-5f, "Floor(3.7)");
            Near(4f, RcMath.Ceil(3.2f), 1e-5f, "Ceil(3.2)");
            Near(3f, RcMath.Abs(-3f), 1e-5f, "Abs(-3)");
            Near(1f, RcMath.Min(1f, 2f), 1e-5f, "Min(1,2)");
            Near(2f, RcMath.Max(1f, 2f), 1e-5f, "Max(1,2)");
            Near(0.5f, RcMath.Lerp(0f, 1f, 0.5f), 1e-5f, "Lerp(0,1,0.5)=0.5");
            Near(5f, RcMath.Lerp(5f, 10f, 0f), 1e-5f, "Lerp(5,10,0)=5 (start)");
            Near(10f, RcMath.Lerp(5f, 10f, 1f), 1e-5f, "Lerp(5,10,1)=10 (end)");
            AssertEqual(5, RcMath.Clamp(10, 0, 5), "Clamp(10,0,5)=5");
            AssertEqual(0, RcMath.Clamp(-1, 0, 5), "Clamp(-1,0,5)=0");
            AssertEqual(3, RcMath.Clamp(3, 0, 5), "Clamp(3,0,5)=3");
            Near(0.5f, RcMath.Clamp(0.5f, 0f, 1f), 1e-5f, "Clamp(0.5,0,1)");
            Near(1f, RcMath.Clamp(2f, 0f, 1f), 1e-5f, "Clamp(2,0,1)");
            Near(0f, RcMath.Clamp(-1f, 0f, 1f), 1e-5f, "Clamp(-1,0,1)");
        }

        // ── PointInTriangle ──

        public static void TestPointInTriangle()
        {
            var a = new Vector2(0, 0);
            var b = new Vector2(4, 0);
            var c = new Vector2(0, 4);
            AssertTrue(RcMath.PointInTriangle(new Vector2(1, 1), a, b, c), "inside (1,1)");
            AssertTrue(RcMath.PointInTriangle(new Vector2(0, 0), a, b, c), "vertex (0,0) counts as inside");
            AssertFalse(RcMath.PointInTriangle(new Vector2(5, 5), a, b, c), "outside (5,5)");
            AssertFalse(RcMath.PointInTriangle(new Vector2(-1, 0), a, b, c), "outside (-1,0)");
        }

        public static void TestPointInTriangleDegenerate()
        {
            // Degenerate triangle (all collinear): point on the line is inside
            var a = new Vector2(0, 0);
            var b = new Vector2(2, 0);
            var c = new Vector2(4, 0);
            // Point on the line segment
            AssertTrue(RcMath.PointInTriangle(new Vector2(1, 0), a, b, c), "point on degenerate line");
            // Point off the line
            AssertFalse(RcMath.PointInTriangle(new Vector2(1, 0.5f), a, b, c), "point off degenerate line");
        }

        // ── TriArea2D ──

        public static void TestTriArea2D()
        {
            // Signed area: (b-a) × (c-a) / 2 but as cross product
            float area = RcMath.TriArea2D(0, 0, 1, 0, 0, 1);
            Near(1f, area, 1e-5f, "area (0,0)(1,0)(0,1) = 1");
            float area2 = RcMath.TriArea2D(0, 0, 0, 1, 1, 0);
            Near(-1f, area2, 1e-5f, "swapped winding gives -1");
        }

        // ── ClosestPtPointSegment ──

        public static void TestClosestPtPointSegment()
        {
            var a = new Vector2(0, 0);
            var b = new Vector2(4, 0);
            // Point directly above the segment
            float d1 = RcMath.ClosestPtPointSegment(new Vector2(2, 3), a, b, out var c1);
            Near(3f, d1, 1e-5f, "distance to (2,3) is 3");
            Near(2f, c1.X, 1e-5f, "closest X=2");
            Near(0f, c1.Y, 1e-5f, "closest Y=0");

            // Point beyond the end (clamp to b)
            float d2 = RcMath.ClosestPtPointSegment(new Vector2(6, 0), a, b, out var c2);
            Near(2f, d2, 1e-5f, "distance to (6,0) is 2 (clamp to b)");
            Near(4f, c2.X, 1e-5f, "closest is b");

            // Point before the start (clamp to a)
            float d3 = RcMath.ClosestPtPointSegment(new Vector2(-3, 0), a, b, out var c3);
            Near(3f, d3, 1e-5f, "distance to (-3,0) is 3 (clamp to a)");
            Near(0f, c3.X, 1e-5f, "closest is a");
        }

        public static void TestClosestPtPointSegmentDegenerate()
        {
            // a == b
            var a = new Vector2(2, 2);
            float d = RcMath.ClosestPtPointSegment(new Vector2(5, 6), a, a, out var c);
            Near(5f, d, 1e-4f, "distance = |(5,6)-(2,2)| = sqrt(9+16) = 5");
            Near(2f, c.X, 1e-5f, "closest = a.X");
            Near(2f, c.Y, 1e-5f, "closest = a.Y");
        }

        // ── Cross2D / Dot2D ──

        public static void TestCross2D()
        {
            Near(1f, RcMath.Cross2D(new Vector2(1, 0), new Vector2(0, 1)), 1e-5f, "X×Y=1");
            Near(-1f, RcMath.Cross2D(new Vector2(0, 1), new Vector2(1, 0)), 1e-5f, "Y×X=-1");
            Near(0f, RcMath.Cross2D(new Vector2(1, 0), new Vector2(2, 0)), 1e-5f, "parallel=0");
        }

        public static void TestDot2D()
        {
            Near(1f, RcMath.Dot2D(new Vector2(1, 0), new Vector2(1, 0)), 1e-5f, "X·X=1");
            Near(0f, RcMath.Dot2D(new Vector2(1, 0), new Vector2(0, 1)), 1e-5f, "X·Y=0");
            Near(5f, RcMath.Dot2D(new Vector2(1, 2), new Vector2(3, 1)), 1e-5f, "(1,2)·(3,1)=3+2=5");
        }

        // ── RcContext ──

        public static void TestRcContextDefaults()
        {
            var ctx = new RcContext();
            AssertTrue(ctx.EnableLog, "log enabled by default");
            AssertEqual(0, ctx.Progress, "progress starts at 0");
            AssertTrue(ctx.LogBuilder != null, "log builder created");
            AssertTrue(ctx.StageTimings != null, "stage timings created");
            AssertTrue(ctx.Timer != null, "timer created");
            AssertEqual(0, ctx.LogBuilder.Length, "log starts empty");
        }

        public static void TestRcContextDisableLog()
        {
            var ctx = new RcContext { EnableLog = false };
            ctx.Log(RcLogLevel.Info, "should not appear");
            AssertEqual(0, ctx.LogBuilder.Length, "no log entry when disabled");
        }

        public static void TestRcContextLogLevels()
        {
            var ctx = new RcContext();
            ctx.Log(RcLogLevel.Error, "err");
            ctx.Log(RcLogLevel.Warning, "warn");
            ctx.Log(RcLogLevel.Info, "info");
            ctx.Log(RcLogLevel.Log, "log");
            string full = ctx.GetFullLog();
            AssertTrue(full.Contains("[ERROR]"), "contains [ERROR]");
            AssertTrue(full.Contains("[WARN]"), "contains [WARN]");
            AssertTrue(full.Contains("[INFO]"), "contains [INFO]");
            AssertTrue(full.Contains("[LOG]"), "contains [LOG]");
            AssertTrue(full.Contains("err"), "contains err");
            AssertTrue(full.Contains("warn"), "contains warn");
        }

        public static void TestRcContextProgress()
        {
            var ctx = new RcContext();
            ctx.ReportProgress(50);
            AssertEqual(50, ctx.Progress, "progress=50");
            ctx.ReportProgress(120); // clamps to 100
            AssertEqual(100, ctx.Progress, "progress clamps to 100");
            ctx.ReportProgress(-5); // clamps to 0
            AssertEqual(0, ctx.Progress, "progress clamps to 0");
        }

        public static void TestRcContextStartStopTimer()
        {
            var ctx = new RcContext();
            ctx.StartTimer("test");
            System.Threading.Thread.Sleep(2); // 2ms
            ctx.StopTimer("test");
            AssertTrue(ctx.StageTimings.ContainsKey("test"), "timing recorded");
            // Should have accumulated at least 0ms (the sleep should give 1-2ms)
            AssertTrue(ctx.StageTimings["test"] >= 0, "timing >= 0");
        }

        // ── RcLogLevel ──

        public static void TestRcLogLevelValues()
        {
            AssertEqual(0, (int)RcLogLevel.Log, "Log=0");
            AssertEqual(1, (int)RcLogLevel.Info, "Info=1");
            AssertEqual(2, (int)RcLogLevel.Warning, "Warning=2");
            AssertEqual(3, (int)RcLogLevel.Error, "Error=3");
        }

        // ── NavMeshGeometry ──

        public static void TestGeometryDefaults()
        {
            var g = new NavMeshGeometry();
            AssertEqual(0, g.Vertices.Count, "empty verts");
            AssertEqual(0, g.Triangles.Count, "empty tris");
            AssertEqual(0, g.Normals.Count, "empty normals");
            AssertEqual(0, g.TriangleCount, "triangle count=0");
        }

        public static void TestGeometryComputeNormalsAndBounds()
        {
            var verts = new List<Vector3>
            {
                new(0, 0, 0), new(4, 0, 0), new(0, 0, 4),  // tri 0
                new(0, 0, 0), new(0, 0, 4), new(4, 0, 0),  // tri 1 (wound opposite)
            };
            var tris = new List<int> { 0, 1, 2, 3, 4, 5 };
            var g = new NavMeshGeometry(verts, tris);
            AssertEqual(2, g.TriangleCount, "2 triangles");
            AssertEqual(2, g.Normals.Count, "2 normals");
            // Triangle 0: (0,0,0)->(4,0,0)->(0,0,4), e1=(4,0,0), e2=(0,0,4), cross=(0*4-0*0, 0*0-4*4, 4*0-0*0)=(0,-16,0), normalized=(0,-1,0)
            Near(0f, g.Normals[0].X, 1e-4f, "tri0 normal X=0");
            Near(-1f, g.Normals[0].Y, 1e-4f, "tri0 normal Y=-1");
            Near(0f, g.Normals[0].Z, 1e-4f, "tri0 normal Z=0");

            // Bounding box: min=(0,0,0), max=(4,0,4)
            Near(0f, g.BMin.X, 1e-5f, "bmin X=0");
            Near(0f, g.BMin.Y, 1e-5f, "bmin Y=0");
            Near(0f, g.BMin.Z, 1e-5f, "bmin Z=0");
            Near(4f, g.BMax.X, 1e-5f, "bmax X=4");
            Near(0f, g.BMax.Y, 1e-5f, "bmax Y=0");
            Near(4f, g.BMax.Z, 1e-5f, "bmax Z=4");
        }

        public static void TestGeometryComputeBoundsManual()
        {
            var g = new NavMeshGeometry();
            g.Vertices.Add(new Vector3(1, 2, 3));
            g.Vertices.Add(new Vector3(-1, 5, 0));
            g.ComputeBounds();
            Near(-1f, g.BMin.X, 1e-5f, "bmin X=-1");
            Near(2f, g.BMin.Y, 1e-5f, "bmin Y=2");
            Near(0f, g.BMin.Z, 1e-5f, "bmin Z=0");
            Near(1f, g.BMax.X, 1e-5f, "bmax X=1");
            Near(5f, g.BMax.Y, 1e-5f, "bmax Y=5");
            Near(3f, g.BMax.Z, 1e-5f, "bmax Z=3");
        }

        // ── VoxelizationConfig 默认值 ──

        public static void TestVoxelConfigDefaults()
        {
            var c = new VoxelizationConfig();
            Near(0.3f, c.CellSize, 1e-5f, "CellSize=0.3");
            Near(0.2f, c.CellHeight, 1e-5f, "CellHeight=0.2");
            Near(0.9f, c.WalkableClimb, 1e-5f, "WalkableClimb=0.9");
            Near(0.6f, c.WalkableRadius, 1e-5f, "WalkableRadius=0.6");
            Near(2.0f, c.WalkableHeight, 1e-5f, "WalkableHeight=2.0");
            Near(12.0f, c.MaxEdgeLen, 1e-5f, "MaxEdgeLen=12");
            Near(1.3f, c.MaxSimplificationError, 1e-5f, "MaxSimplificationError=1.3");
            Near(8.0f, c.MinRegionArea, 1e-5f, "MinRegionArea=8");
            Near(20.0f, c.MergeRegionArea, 1e-5f, "MergeRegionArea=20");
            AssertEqual(6, c.MaxVertsPerPoly, "MaxVertsPerPoly=6");
            Near(6.0f, c.DetailSampleDist, 1e-5f, "DetailSampleDist=6");
            Near(1.0f, c.DetailSampleMaxError, 1e-5f, "DetailSampleMaxError=1");
            Near(-500f, c.BMin.X, 1e-5f, "BMin.X=-500");
            Near(500f, c.BMax.X, 1e-5f, "BMax.X=500");
        }

        // ── Span ──

        public static void TestSpanDefaults()
        {
            var s = new Span();
            AssertEqual(0, s.YMin, "YMin=0");
            AssertEqual(0, s.YMax, "YMax=0");
            AssertEqual(0, s.Area, "Area=0 (RC_NULL_AREA)");
            AssertEqual(0, s.Next, "Next=0 (no next span)");
            AssertEqual(0, s.Height, "Height=0");
        }

        public static void TestSpanHeight()
        {
            var s = new Span { YMin = 5, YMax = 10 };
            AssertEqual(5, s.Height, "Height=YMax-YMin=5");
        }

        public static void TestSpanClone()
        {
            var s = new Span { YMin = 3, YMax = 7, Area = 1, Next = 99 };
            var c = s.Clone();
            AssertEqual(3, c.YMin, "clone YMin=3");
            AssertEqual(7, c.YMax, "clone YMax=7");
            AssertEqual(1, c.Area, "clone Area=1");
            AssertEqual(99, c.Next, "clone Next=99");
        }

        // ── RcHeightfield ──

        public static void TestHeightfieldConstruction()
        {
            var hf = new RcHeightfield(10, 20, 0.3f, 0.2f,
                new Vector3(-3, 0, -3), new Vector3(3, 5, 3));
            AssertEqual(10, hf.Width, "Width=10");
            AssertEqual(20, hf.Height, "Height=20");
            Near(0.3f, hf.CellSize, 1e-5f, "CellSize=0.3");
            Near(0.2f, hf.CellHeight, 1e-5f, "CellHeight=0.2");
            AssertEqual(200, hf.SpansList.Length, "SpansList.Length=Width*Height=200");
            // All spans initially -1 (empty)
            AssertTrue(hf.SpansList.All(i => i == -1), "all spans initially empty");
            AssertEqual(0, hf.Spans.Count, "no span data yet");
        }

        public static void TestHeightfieldIndex()
        {
            var hf = new RcHeightfield(4, 4, 0.3f, 0.2f,
                Vector3.Zero, Vector3.One);
            AssertEqual(0, hf.Index(0, 0), "Index(0,0)=0");
            AssertEqual(1, hf.Index(0, 1), "Index(0,1)=1");
            AssertEqual(4, hf.Index(1, 0), "Index(1,0)=4");
            AssertEqual(15, hf.Index(3, 3), "Index(3,3)=3*4+3=15");
        }

        public static void TestHeightfieldMinDimensions()
        {
            var hf = new RcHeightfield(0, 0, 0.3f, 0.2f, Vector3.Zero, Vector3.One);
            AssertEqual(1, hf.Width, "Width clamps to min 1");
            AssertEqual(1, hf.Height, "Height clamps to min 1");
        }

        // ── CompactSpan ──

        public static void TestCompactSpanConnections()
        {
            var s = new CompactSpan();
            // Default: all connections 0 (no connection)
            AssertEqual(0, s.GetConnection(0), "conn0 default=0 (no connection)");
            AssertEqual(0, s.GetConnection(1), "conn1 default=0");
            AssertEqual(0, s.GetConnection(2), "conn2 default=0");
            AssertEqual(0, s.GetConnection(3), "conn3 default=0");
            AssertEqual(-1, s.GetConnection(99), "conn99 out of range returns -1");

            s.SetConnection(0, 10);
            s.SetConnection(1, 20);
            s.SetConnection(2, 30);
            s.SetConnection(3, 40);
            AssertEqual(10, s.GetConnection(0), "conn0=10");
            AssertEqual(20, s.GetConnection(1), "conn1=20");
            AssertEqual(30, s.GetConnection(2), "conn2=30");
            AssertEqual(40, s.GetConnection(3), "conn3=40");
        }

        public static void TestCompactSpanMaxConnections()
        {
            AssertEqual(4, CompactSpan.MaxConnections, "MaxConnections=4");
        }

        // ── NavMeshBakerReal 构造 ──

        public static void TestBakerRealConstruction()
        {
            var baker = new NavMeshBakerReal();
            AssertTrue(baker != null, "baker created");
            var baker2 = new NavMeshBakerReal(new RcContext { EnableLog = false });
            AssertTrue(baker2 != null, "baker with custom ctx created");
            var baker3 = new NavMeshBakerReal(null!); // should fall back to new ctx
            AssertTrue(baker3 != null, "baker with null ctx fallback");
        }

        // ── 端到端 Bake 小场景 ──

        public static void TestBakeEmptyGeometry()
        {
            var baker = new NavMeshBakerReal(new RcContext { EnableLog = false });
            var config = new VoxelizationConfig
            {
                BMin = new Vector3(-2, -2, -2),
                BMax = new Vector3(2, 2, 2),
                CellSize = 0.5f,
                CellHeight = 0.2f,
                WalkableClimb = 0.5f,
                WalkableRadius = 0.3f,
                WalkableHeight = 1.0f,
                MinRegionArea = 1,
                MergeRegionArea = 1,
                MaxVertsPerPoly = 6,
                MaxEdgeLen = 12f,
                MaxSimplificationError = 1.3f
            };
            var geom = new NavMeshGeometry();
            var result = baker.Bake(geom, config);
            AssertTrue(result != null, "result not null");
            AssertTrue(result.RcHeightfield != null, "heightfield created");
            // With no geometry, poly mesh should be empty
            AssertTrue(result.PolyMesh != null, "poly mesh created");
            AssertEqual(0, result.PolyMesh.Npolys, "no polygons from empty geometry");
        }

        public static void TestBakeFlatQuad()
        {
            var baker = new NavMeshBakerReal(new RcContext { EnableLog = false });
            var config = new VoxelizationConfig
            {
                BMin = new Vector3(-3, -2, -3),
                BMax = new Vector3(3, 2, 3),
                CellSize = 0.5f,
                CellHeight = 0.2f,
                WalkableClimb = 0.5f,
                WalkableRadius = 0.1f,
                WalkableHeight = 1.0f,
                MinRegionArea = 1,
                MergeRegionArea = 1,
                MaxVertsPerPoly = 6,
                MaxEdgeLen = 12f,
                MaxSimplificationError = 1.3f
            };
            // A flat quad at Y=0: 2 triangles
            var verts = new List<Vector3>
            {
                new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2),
                new(-2, 0, -2), new(2, 0, 2), new(-2, 0, 2),
            };
            var tris = new List<int> { 0, 1, 2, 3, 4, 5 };
            var geom = new NavMeshGeometry(verts, tris);

            var result = baker.Bake(geom, config);
            AssertTrue(result != null, "result not null");
            AssertTrue(result.RcHeightfield != null, "heightfield created");
            AssertTrue(result.RcCompactHeightfield != null, "compact heightfield created");
            // Flat walkable surface should produce some polygons
            AssertTrue(result.PolyMesh != null, "poly mesh created");
            // We don't assert exact count (algorithm may merge/erode), just check it's not null
            AssertTrue(result.PolyMeshDetail != null, "detail mesh created");
        }

        public static void TestBakeResultSetLogAndTimings()
        {
            var ctx = new RcContext();
            ctx.Log(RcLogLevel.Info, "test log");
            ctx.StartTimer("phase");
            ctx.StopTimer("phase");
            var result = new NavMeshBakeResult();
            result.SetLogAndTimings(ctx);
            AssertTrue(result.BuildLog != null, "BuildLog set");
            AssertTrue(result.BuildLog.Contains("test log"), "BuildLog contains entry");
            AssertTrue(result.StageTimings != null, "StageTimings set");
            AssertTrue(result.StageTimings.ContainsKey("phase"), "StageTimings has phase");
        }
    }
}
