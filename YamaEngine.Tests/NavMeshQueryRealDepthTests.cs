using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// NavMeshQueryReal 深度测试。覆盖 NavMeshHeader / NavMeshPolyRef 位编码 /
    /// NavMeshPoly / NavMeshFilter 派生类 / NavMesh Tile CRUD / 二叉堆 / A* 寻路 /
    /// 最近多边形查询 / 射线投射。
    /// </summary>
    public static class NavMeshQueryRealDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        // ── 辅助: 构造一个含 n 个单位方块多边形的 NavMesh ──────────────────
        // 每个方块 (i,0) 顶点： (i,0,0),(i+1,0,0),(i+1,0,1),(i,0,1)
        private static NavMesh BuildLinearMesh(int n)
        {
            var nm = new NavMesh(0.3f, 0.2f, 10f, Vector3.Zero, 64);
            // 所有方块共享一个 tile，简单起见 n 个多边形全在 tile (0,0)
            var tile = new NavMeshTile();
            var verts = new List<Vector3>();
            var polys = new List<NavPoly>();
            for (int i = 0; i < n; i++)
            {
                int vBase = verts.Count;
                verts.Add(new Vector3(i, 0, 0));
                verts.Add(new Vector3(i + 1, 0, 0));
                verts.Add(new Vector3(i + 1, 0, 1));
                verts.Add(new Vector3(i, 0, 1));
                var p = new NavPoly { Index = i, AreaId = 0, Flags = 1 };
                p.VertIndices.AddRange(new[] { vBase, vBase + 1, vBase + 2, vBase + 3 });
                // 邻居: 左 / 右 (4 条边，其中左 (i-1) 右 (i+1)，前后 -1)
                p.NeighborPolys.AddRange(new[] { i > 0 ? i - 1 : -1, i + 1 < n ? i + 1 : -1, -1, -1 });
                p.VertexIndices = p.VertIndices.ToArray();
                p.Neighbors = p.NeighborPolys.ToArray();
                p.Area = 0;
                p.Center = new Vector3(i + 0.5f, 0, 0.5f);
                polys.Add(p);
            }
            tile.BaseTile = new NavTile { GridX = 0, GridZ = 0 };
            tile.BaseTile.Vertices.AddRange(verts);
            tile.BaseTile.Polys.AddRange(polys);
            // 同步 Verts/Polys 别名到 NavMeshTile.Verts (List<Vector3>) 与 Polys
            // 通过 BaseTile 间接访问，NavMesh.AddTile 需要 tile.Verts / tile.Polys 非空
            // NavMeshTile.Verts / Polys 是只读转发，NavMesh 内部直接读 tile.Polys!.Count
            // 这里 BaseTile.Polys.Count > 0 已足够
            nm.AddTile(0, 0, tile);
            return nm;
        }

        private static NavMeshPolyRef MakeRef(NavMesh nm, int tileIdx, int polyIdx)
        {
            var tile = nm.GetAllTiles()[tileIdx];
            return NavMeshPolyRef.Create(tile.Salt, tileIdx, polyIdx);
        }

        // ── NavMeshHeader ──

        public static void TestHeaderMagicAndDefaults()
        {
            AssertEqual(0x444E5601u, NavMeshHeader.Magic, "Magic='DNV'+1");
            var h = new NavMeshHeader();
            AssertEqual(1u, h.Version, "Version=1");
            AssertEqual(0, h.TileCount, "TileCount=0");
            AssertEqual(0, h.PolyCount, "PolyCount=0");
            AssertEqual(0, h.VertCount, "VertCount=0");
            AssertEqual(0, h.BVNodeCount, "BVNodeCount=0");
            AssertEqual(0, h.OffMeshConCount, "OffMeshConCount=0");
        }

        // ── NavMeshPolyRef ──

        public static void TestPolyRefBitPacking()
        {
            var r = NavMeshPolyRef.Create(7, 42, 123);
            AssertEqual(7u, r.Salt, "Salt=7");
            AssertEqual(42, r.Tile, "Tile=42");
            AssertEqual(123, r.Poly, "Poly=123");
            AssertTrue(r.IsValid, "IsValid=true");
        }

        public static void TestPolyRefInvalid()
        {
            AssertFalse(NavMeshPolyRef.Invalid.IsValid, "Invalid.IsValid=false");
            AssertEqual(0ul, NavMeshPolyRef.Invalid.Value, "Invalid.Value=0");
            AssertEqual(0u, NavMeshPolyRef.Invalid.Salt, "Invalid.Salt=0");
            AssertEqual(0, NavMeshPolyRef.Invalid.Tile, "Invalid.Tile=0");
            AssertEqual(0, NavMeshPolyRef.Invalid.Poly, "Invalid.Poly=0");
        }

        public static void TestPolyRefSaltMask()
        {
            // Salt 12 bits (max 4095), Tile 20 bits (max ~1M), Poly 32 bits
            var r = NavMeshPolyRef.Create(4095, 1048575, int.MaxValue);
            AssertEqual(4095u, r.Salt, "Salt max=4095");
            AssertEqual(1048575, r.Tile, "Tile max=2^20-1");
        }

        public static void TestPolyRefToString()
        {
            var r = NavMeshPolyRef.Create(3, 5, 9);
            string s = r.ToString();
            AssertTrue(s.Contains("salt=3"), $"ToString contains salt=3: {s}");
            AssertTrue(s.Contains("tile=5"), $"ToString contains tile=5: {s}");
            AssertTrue(s.Contains("poly=9"), $"ToString contains poly=9: {s}");
        }

        // ── NavMeshPoly ──

        public static void TestNavMeshPolyDefaults()
        {
            var p = new NavMeshPoly();
            AssertEqual(0, p.Verts.Length, "Verts empty");
            AssertEqual(0, p.Neighbors.Length, "Neighbors empty");
            AssertEqual(0, p.Flags, "Flags=0");
            AssertEqual(0, p.Area, "Area=0");
            AssertEqual(0, p.Parent, "Parent=0");
        }

        public static void TestNavMeshPolyImplicitConversion()
        {
            var np = new NavPoly { Index = 7, Flags = 3, Area = 2 };
            np.VertexIndices = new[] { 0, 1, 2 };
            np.Neighbors = new[] { -1, 1, -1 };
            NavMeshPoly? p = (NavMeshPoly?)np;
            AssertTrue(p != null, "conversion non-null");
            AssertEqual(3, p!.Verts.Length, "Verts length=3");
            AssertEqual(3, p.Neighbors.Length, "Neighbors length=3");
            AssertEqual(3, p.Flags, "Flags copied=3");
            AssertEqual(2, p.Area, "Area copied=2");
            AssertEqual(7, p.Parent, "Parent=Index=7");
        }

        // ── NavMeshFilter ──

        public static void TestFilterDefaults()
        {
            var f = new NavMeshFilter();
            AssertEqual(0xFFFF, f.IncludeFlags, "IncludeFlags=0xFFFF");
            AssertEqual(0, f.ExcludeFlags, "ExcludeFlags=0");
            AssertEqual(64, f.AreaCost.Length, "AreaCost.Length=64");
            AssertTrue(f.AreaCost.All(c => Math.Abs(c - 1.0f) < 1e-6f), "all AreaCost=1.0");
        }

        public static void TestFilterIsPassableInclude()
        {
            var f = new NavMeshFilter { IncludeFlags = 0x0001, ExcludeFlags = 0 };
            var poly = new NavMeshPoly { Flags = 0x0001 };
            AssertTrue(f.IsPassable(poly), "flag set → passable");
            var poly2 = new NavMeshPoly { Flags = 0x0002 };
            AssertFalse(f.IsPassable(poly2), "flag not in IncludeFlags → not passable");
        }

        public static void TestFilterIsPassableExclude()
        {
            var f = new NavMeshFilter { IncludeFlags = 0xFFFF, ExcludeFlags = 0x0004 };
            var ok = new NavMeshPoly { Flags = 0x0001 };
            AssertTrue(f.IsPassable(ok), "non-excluded flag passable");
            var bad = new NavMeshPoly { Flags = 0x0004 };
            AssertFalse(f.IsPassable(bad), "excluded flag not passable");
        }

        public static void TestFilterGetCost()
        {
            var f = new NavMeshFilter();
            f.AreaCost[0] = 1.0f;
            f.AreaCost[2] = 3.5f;
            var p0 = new NavMeshPoly { Area = 0 };
            var p2 = new NavMeshPoly { Area = 2 };
            Near(1.0f, f.GetCost(p0), 1e-5f, "cost area0=1.0");
            Near(3.5f, f.GetCost(p2), 1e-5f, "cost area2=3.5");
        }

        public static void TestWalkFilter()
        {
            var f = new WalkFilter();
            AssertEqual(1, f.IncludeFlags, "WalkFilter IncludeFlags=1");
            AssertEqual(0, f.ExcludeFlags, "WalkFilter ExcludeFlags=0");
            Near(1.0f, f.AreaCost[0], 1e-5f, "WalkFilter AreaCost[0]=1.0");
            AssertTrue(f.IsPassable(new NavMeshPoly { Flags = 1 }), "walkable poly passable");
            AssertFalse(f.IsPassable(new NavMeshPoly { Flags = 2 }), "non-walkable poly blocked");
        }

        public static void TestSwimFilter()
        {
            var f = new SwimFilter();
            AssertEqual(0x0020, f.IncludeFlags, "SwimFilter IncludeFlags=0x20");
            Near(2.0f, f.AreaCost[5], 1e-5f, "SwimFilter AreaCost[5]=2.0");
        }

        public static void TestJumpFilter()
        {
            var f = new JumpFilter();
            AssertEqual(0x0004, f.IncludeFlags, "JumpFilter IncludeFlags=0x4");
            Near(1.5f, f.AreaCost[2], 1e-5f, "JumpFilter AreaCost[2]=1.5");
        }

        public static void TestDefaultFilter()
        {
            var f = new DefaultFilter();
            AssertEqual(0xFFFF, f.IncludeFlags, "DefaultFilter IncludeFlags=0xFFFF");
            AssertEqual(0, f.ExcludeFlags, "DefaultFilter ExcludeFlags=0");
            AssertTrue(f.IsPassable(new NavMeshPoly { Flags = 0xFFFF }), "any flag passable");
        }

        // ── NavMesh Tile 管理 ──

        public static void TestNavMeshConstruction()
        {
            var nm = new NavMesh(0.3f, 0.2f, 10f, new Vector3(1, 2, 3), 32);
            AssertEqual(0, nm.TileCount, "empty at construction");
            AssertTrue(nm.GetAllTiles().Count == 0, "no tiles");
            AssertEqual(0, nm.Polygons.Count, "no draw polys");
            AssertEqual(0, nm.Links.Count, "no draw links");
            AssertEqual(0, nm.Obstacles.Count, "no obstacles");
        }

        public static void TestNavMeshAddTileReturnsRef()
        {
            var nm = BuildLinearMesh(3);
            AssertEqual(1, nm.TileCount, "1 tile");
            var tile = nm.GetAllTiles()[0];
            AssertEqual(0, tile.TileX, "TileX=0");
            AssertEqual(0, tile.TileY, "TileY=0");
            AssertEqual(1u, tile.Salt, "Salt=1 (first tile)");
        }

        public static void TestNavMeshGetTileAt()
        {
            var nm = BuildLinearMesh(2);
            AssertTrue(nm.GetTileAt(0, 0) != null, "GetTileAt(0,0) hit");
            AssertTrue(nm.GetTileAt(5, 5) == null, "GetTileAt(5,5) miss");
        }

        public static void TestNavMeshRemoveTile()
        {
            var nm = BuildLinearMesh(2);
            AssertTrue(nm.RemoveTile(0, 0), "RemoveTile returns true");
            AssertEqual(0, nm.TileCount, "TileCount=0 after remove");
            AssertFalse(nm.RemoveTile(0, 0), "RemoveTile again returns false");
        }

        public static void TestNavMeshWorldToTile()
        {
            var nm = new NavMesh(0.3f, 0.2f, 10f, Vector3.Zero, 64);
            var (tx, ty) = nm.WorldToTile(new Vector3(0, 0, 0));
            AssertEqual(0, tx, "origin tile x=0");
            AssertEqual(0, ty, "origin tile y=0");
            var (tx2, ty2) = nm.WorldToTile(new Vector3(15, 0, 15));
            AssertEqual(1, tx2, "pos (15,*,15) → tile x=1");
            AssertEqual(1, ty2, "pos (15,*,15) → tile y=1");
            var (tx3, _) = nm.WorldToTile(new Vector3(-1, 0, 0));
            AssertEqual(-1, tx3, "negative → tile x=-1");
        }

        public static void TestNavMeshGetPoly()
        {
            var nm = BuildLinearMesh(3);
            var r0 = MakeRef(nm, 0, 0);
            var p = nm.GetPoly(r0);
            AssertTrue(p != null, "GetPoly(poly 0) hit");
            AssertEqual(4, p!.Verts.Length, "poly 0 has 4 verts");

            var rBad = NavMeshPolyRef.Create(999, 99, 99);
            AssertTrue(nm.GetPoly(rBad) == null, "GetPoly(bad ref) null");
        }

        public static void TestNavMeshGetTileByRef()
        {
            var nm = BuildLinearMesh(2);
            var r = MakeRef(nm, 0, 0);
            var t = nm.GetTile(r);
            AssertTrue(t != null, "GetTile(ref) hit");
            AssertEqual(0, t!.TileX, "tile.TileX=0");
        }

        public static void TestNavMeshObstacles()
        {
            var nm = new NavMesh(0.3f, 0.2f, 10f, Vector3.Zero);
            AssertEqual(0, nm.Obstacles.Count, "no obstacles");
            var ob = new NavMeshObstacle { Position = new Vector3(5, 0, 5), Radius = 1.0f };
            nm.AddObstacle(ob);
            AssertEqual(1, nm.Obstacles.Count, "1 obstacle added");
            nm.RemoveObstacle(ob);
            AssertEqual(0, nm.Obstacles.Count, "obstacle removed");
        }

        public static void TestNavMeshUpdateTile()
        {
            var nm = BuildLinearMesh(1);
            var tile = nm.GetAllTiles()[0];
            AssertEqual(1u, tile.Salt, "salt=1 before update");
            var newTile = new NavMeshTile { Salt = 99 };
            nm.UpdateTile(0, 0, newTile);
            var fetched = nm.GetTileAt(0, 0);
            AssertTrue(fetched != null, "GetTileAt still hits");
            AssertEqual(99u, fetched!.Salt, "salt updated to 99");
        }

        // ── 二叉最小堆 (通过反射或间接测试，堆是 internal 的，只能通过寻路行为验证) ──

        public static void TestFindPathSameRef()
        {
            var nm = BuildLinearMesh(4);
            var q = new NavMeshQuery(nm);
            var r = MakeRef(nm, 0, 1);
            var ok = q.FindPath(r, r, new Vector3(1.5f, 0, 0.5f), new Vector3(1.5f, 0, 0.5f),
                new DefaultFilter(), out var path);
            AssertTrue(ok, "same-ref path returns true");
            AssertEqual(1, path.Count, "path length=1");
        }

        public static void TestFindPathInvalidRef()
        {
            var nm = BuildLinearMesh(2);
            var q = new NavMeshQuery(nm);
            var ok = q.FindPath(NavMeshPolyRef.Invalid, NavMeshPolyRef.Invalid,
                Vector3.Zero, Vector3.Zero, new DefaultFilter(), out var path);
            AssertFalse(ok, "invalid refs → returns false");
            AssertEqual(0, path.Count, "empty path");
        }

        public static void TestFindPathLinear()
        {
            var nm = BuildLinearMesh(5);
            var q = new NavMeshQuery(nm);
            var start = MakeRef(nm, 0, 0);
            var end = MakeRef(nm, 0, 4);
            var ok = q.FindPath(start, end,
                new Vector3(0.5f, 0, 0.5f), new Vector3(4.5f, 0, 0.5f),
                new DefaultFilter(), out var path);
            AssertTrue(ok, "linear path found");
            AssertEqual(5, path.Count, "5 polys in path");
            AssertEqual(start.Value, path[0].Value, "path starts at start");
            AssertEqual(end.Value, path[path.Count - 1].Value, "path ends at end");
        }

        public static void TestFindPathFiltered()
        {
            var nm = BuildLinearMesh(3);
            // 把中间 poly 的 flags 设成不可通行 → 路径中断
            var tile = nm.GetTileAt(0, 0)!;
            tile.BaseTile!.Polys[1].Flags = 0;
            var q = new NavMeshQuery(nm);
            var start = MakeRef(nm, 0, 0);
            var end = MakeRef(nm, 0, 2);
            // 用只允许 flags=1 的 WalkFilter
            var ok = q.FindPath(start, end,
                new Vector3(0.5f, 0, 0.5f), new Vector3(2.5f, 0, 0.5f),
                new WalkFilter(), out var path);
            AssertFalse(ok, "blocked middle poly → no path with WalkFilter");
        }

        // ── 最近多边形 / 射线 / 沿面移动 ──

        public static void TestFindNearestPolyHit()
        {
            var nm = BuildLinearMesh(3);
            var q = new NavMeshQuery(nm);
            var filter = new DefaultFilter();
            var r = q.FindNearestPoly(new Vector3(0.5f, 0, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), filter, out var nearestRef, out var nearestPt);
            AssertTrue(r, "FindNearestPoly returns true");
            AssertTrue(nearestRef.IsValid, "nearest poly valid");
            AssertEqual(0, nearestRef.Poly, "nearest poly index=0");
        }

        public static void TestPointInPoly()
        {
            var nm = BuildLinearMesh(2);
            var tile = nm.GetTileAt(0, 0)!;
            var p = tile.BaseTile!.Polys[0];
            AssertTrue(q_Contains(tile, p, new Vector3(0.5f, 0, 0.5f)), "point (0.5,0,0.5) inside poly 0");
            AssertFalse(q_Contains(tile, p, new Vector3(5.0f, 0, 0.5f)), "point (5,0,0.5) outside poly 0");
        }

        private static bool q_Contains(NavMeshTile tile, NavPoly poly, Vector3 point)
        {
            var verts = new List<Vector3>();
            foreach (var vi in poly.VertIndices)
                verts.Add(tile.BaseTile!.Vertices[vi]);
            int n = verts.Count;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = verts[i].X, zi = verts[i].Z;
                float xj = verts[j].X, zj = verts[j].Z;
                bool intersect = ((zi > point.Z) != (zj > point.Z)) &&
                    (point.X < (xj - xi) * (point.Z - zi) / (zj - zi + 1e-10f) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        // ── NavPath (NavigationMeshSystem) ──

        public static void TestNavPathConstruction()
        {
            var path = new NavPath(new List<Vector3>
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0)
            });
            AssertEqual(3, path.Points.Count, "3 points");
            AssertEqual(NavPathState.Complete, path.State, "state=Complete");
            Near(2.0f, path.Length, 1e-4f, "length=2");
        }

        public static void TestNavPathEmpty()
        {
            var path = new NavPath(new List<Vector3>());
            AssertEqual(0, path.Points.Count, "empty points");
            AssertEqual(NavPathState.Invalid, path.State, "state=Invalid");
        }

        public static void TestNavPathSinglePoint()
        {
            var path = new NavPath(new List<Vector3> { Vector3.Zero });
            AssertEqual(1, path.Points.Count, "1 point");
            AssertEqual(NavPathState.Invalid, path.State, "single point → Invalid");
        }

        public static void TestNavPathSetPoints()
        {
            var path = new NavPath();
            AssertEqual(NavPathState.Invalid, path.State, "default Invalid");
            path.SetPoints(new List<Vector3>
            {
                new Vector3(0, 0, 0), new Vector3(3, 0, 4)
            });
            AssertEqual(2, path.Points.Count, "2 points");
            AssertEqual(NavPathState.Complete, path.State, "state=Complete");
            Near(5.0f, path.Length, 1e-4f, "length=5 (3-4-5 triangle)");
        }

        public static void TestNavPathTypeDefaults()
        {
            var path = new NavPath(new List<Vector3> { Vector3.Zero, Vector3.One });
            AssertEqual(NavPathType.StraightWithCorners, path.PathType, "default path type=StraightWithCorners");
            AssertEqual(0, path.CurrentCornerIndex, "CurrentCornerIndex=0");
        }

        public static void TestNavMeshObstacleContains()
        {
            var ob = new NavMeshObstacle
            {
                Position = new Vector3(5, 0, 5),
                Radius = 2.0f,
                Height = 3.0f
            };
            AssertTrue(ob.ContainsPoint(new Vector3(6, 1, 5)), "point inside cylinder");
            AssertTrue(ob.ContainsPoint(new Vector3(5, 3, 5)), "point at top edge");
            AssertFalse(ob.ContainsPoint(new Vector3(5, 4, 5)), "above height → outside");
            AssertFalse(ob.ContainsPoint(new Vector3(10, 1, 5)), "outside radius");
            // Disabled → always false
            ob.IsActive = false;
            AssertFalse(ob.ContainsPoint(new Vector3(5, 0, 5)), "inactive obstacle contains nothing");
        }

        public static void TestNavMeshObstacleDefaults()
        {
            var ob = new NavMeshObstacle();
            Near(1.0f, ob.Radius, 1e-5f, "Radius=1");
            Near(2.0f, ob.Height, 1e-5f, "Height=2");
            AssertTrue(ob.AvoidanceEnabled, "AvoidanceEnabled=true");
            Near(2.0f, ob.InfluenceRadius, 1e-5f, "InfluenceRadius=2");
            AssertTrue(ob.CarveNavMesh, "CarveNavMesh=true");
            AssertTrue(ob.IsActive, "IsActive=true");
        }

        public static void TestNavPathTypeAndStateEnums()
        {
            AssertEqual(0, (int)NavPathType.Straight, "Straight=0");
            AssertEqual(1, (int)NavPathType.StraightWithCorners, "StraightWithCorners=1");
            AssertEqual(2, (int)NavPathType.Spline, "Spline=2");
            AssertEqual(3, (int)NavPathType.SplineWithCorners, "SplineWithCorners=3");
            AssertEqual(0, (int)NavPathState.Invalid, "Invalid=0");
            AssertEqual(1, (int)NavPathState.Partial, "Partial=1");
            AssertEqual(2, (int)NavPathState.Complete, "Complete=2");
        }

        public static void TestNavMeshAreaDefaults()
        {
            var a = NavMeshArea.DefaultWalkable;
            AssertEqual(0, a.Id, "DefaultWalkable.Id=0");
            AssertEqual("Walkable", a.Name, "DefaultWalkable.Name");
            Near(1.0f, a.Cost, 1e-5f, "cost=1");
            AssertTrue(a.CanGenerate, "CanGenerate=true");
            AssertTrue(a.AutoLink, "AutoLink=true");

            var road = NavMeshArea.Road;
            AssertEqual(1, road.Id, "Road.Id=1");
            Near(0.5f, road.Cost, 1e-5f, "Road cost=0.5");

            var water = NavMeshArea.Water;
            AssertEqual(3, water.Id, "Water.Id=3");
            Near(3.0f, water.Cost, 1e-5f, "Water cost=3");

            var notWalkable = NavMeshArea.NotWalkable;
            AssertEqual(63, notWalkable.Id, "NotWalkable.Id=63");
            AssertFalse(notWalkable.CanGenerate, "NotWalkable.CanGenerate=false");
        }
    }
}
