// NavMeshQueryTests.cs — UE5.8 导航网格子系统深度验证
// 手工构造 NavTile + NavPoly（凸四边形网格），验证：
//   FindNearestPoly / ProjectPoint / PointInPoly / Raycast（穿多边形/撞墙）/
//   MoveAlongSurface（沿墙滑）/ FindPath A*（可达/不可达）。
// 全部纯 CPU、确定性数值验证（Y-up，查询在 XZ 平面 2D 计算）。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class NavMeshQueryTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        private static void AssertVec(Vector3 got, Vector3 want, float tol, string msg)
        {
            AssertNear(got.X, want.X, tol, $"{msg} X");
            AssertNear(got.Y, want.Y, tol, $"{msg} Y");
            AssertNear(got.Z, want.Z, tol, $"{msg} Z");
        }

        // ── 构造单 tile，两个共享竖直边（X=2, Z∈[-2,2]）的凸四边形：
        //    v0=(0,0,-2) v1=(2,0,-2) v2=(2,0,2) v3=(0,0,2)   → Poly0 中心 (1,0,0)
        //    v4=(4,0,-2) v5=(4,0,2)                            → Poly1 中心 (3,0,0)
        //    Poly0 边: e0=(v0,v1) e1=(v1,v2)[共享] e2=(v2,v3) e3=(v3,v0)
        //    Poly1 边: e0=(v1,v4) e1=(v4,v5) e2=(v5,v2) e3=(v2,v1)[共享]
        private static NavMesh BuildTwoPolyMesh()
        {
            var nav = new NavMesh(cellSize: 0.5f, cellHeight: 0.2f, tileSize: 8f, origin: Vector3.Zero);
            var tile = new NavMeshTile { BaseTile = new NavTile() };
            var baseTile = tile.BaseTile!;

            baseTile.Vertices.AddRange(new List<Vector3>
            {
                new Vector3(0, 0, -2),  // 0
                new Vector3(2, 0, -2),  // 1
                new Vector3(2, 0, 2),   // 2
                new Vector3(0, 0, 2),   // 3
                new Vector3(4, 0, -2),  // 4
                new Vector3(4, 0, 2),   // 5
            });

            var p0 = new NavPoly { Index = 0, Flags = 1, Area = 0 };
            p0.VertIndices.AddRange(new[] { 0, 1, 2, 3 });
            p0.VertexIndices = p0.VertIndices.ToArray();
            p0.Neighbors = new[] { -1, 1, -1, -1 }; // e1=(v1,v2) shared
            p0.Center = new Vector3(1, 0, 0);
            baseTile.Polys.Add(p0);

            var p1 = new NavPoly { Index = 1, Flags = 1, Area = 0 };
            p1.VertIndices.AddRange(new[] { 1, 4, 5, 2 });
            p1.VertexIndices = p1.VertIndices.ToArray();
            p1.Neighbors = new[] { -1, -1, -1, 0 }; // e3=(v2,v1) shared
            p1.Center = new Vector3(3, 0, 0);
            baseTile.Polys.Add(p1);

            tile.BoundsMin = new Vector3(0, -1, -2);
            tile.BoundsMax = new Vector3(4, 1, 2);

            // 填充 BVTree：根=内部节点(I=-1)，叶0→poly0，叶1→poly1
            // 叶子包围盒 = 质心 ± extents(1)
            tile.BVTree.Add(new NavMeshBVNode { Bmin = new Vector3(0, -1, -1), Bmax = new Vector3(4, 1, 1), I = -1 });
            tile.BVTree.Add(new NavMeshBVNode { Bmin = new Vector3(0, -1, -1), Bmax = new Vector3(2, 1, 1), I = 0 });
            tile.BVTree.Add(new NavMeshBVNode { Bmin = new Vector3(2, -1, -1), Bmax = new Vector3(4, 1, 1), I = 1 });

            nav.AddTile(0, 0, tile);
            return nav;
        }

        // ── FindNearestPoly：给定点在 poly0 中心 → 返回 ref 有效，最近点在 poly0 平面 ──
        public static void TestFindNearestPolyHit()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var point = new Vector3(1, 0, -1); // poly0 中心
            bool found = query.FindNearestPoly(point, new Vector3(1, 1, 1), filter,
                out var nearestRef, out var nearestPt);

            AssertTrue(found, "FindNearestPoly must succeed for point on poly0.");
            AssertTrue(nearestRef.IsValid, "nearestRef must be valid.");
            AssertEqual(0, nearestRef.Poly, "nearestRef.Poly must be poly0 (index 0).");
            AssertVec(nearestPt, new Vector3(1, 0, -1), 0.05f, "nearestPt must equal query point on flat poly.");
        }

        // ── FindNearestPoly：点在网格外 → 返回 false（无候选）──
        public static void TestFindNearestPolyMiss()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var point = new Vector3(10, 0, 10); // 明显在网格范围 [0,-2]–[4,2] 之外
            bool found = query.FindNearestPoly(point, new Vector3(1, 1, 1), filter,
                out var nearestRef, out _);

            AssertTrue(!found, "FindNearestPoly must fail for point far from navmesh.");
            AssertTrue(!nearestRef.IsValid, "nearestRef must be Invalid on miss.");
        }

        // ── PointInPoly：内部点 true / 外部点 false ──
        public static void TestPointInPoly()
        {
            var nav = BuildTwoPolyMesh();
            var tile = nav.GetTileAt(0, 0)!;
            var poly = tile.Polys[0];

            bool inside = nav.GetTileAt(0, 0) != null &&
                new NavMeshQuery(nav).PointInPoly(new Vector3(1, 0, -1), poly, tile);
            AssertTrue(inside, "Center point must be inside poly0.");

            bool outside = new NavMeshQuery(nav).PointInPoly(new Vector3(10, 0, -1), poly, tile);
            AssertTrue(!outside, "Point at X=10 must be outside poly0.");
        }

        // ── ProjectPoint：成功投影返回 poly 索引 + 最近点 ──
        public static void TestProjectPoint()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);

            var point = new Vector3(1, 5, -1); // 在 poly0 上方
            int polyIdx = query.ProjectPoint(point, out var projected);

            AssertEqual(0, polyIdx, "ProjectPoint must return poly0 index (0).");
            AssertVec(projected, new Vector3(1, 0, -1), 0.05f,
                "Projected point must land on poly0 plane (Y=0).");
        }

        // ── Raycast：起点在 poly0 中心 → 终点在 poly1 中心，穿越共享边，命中路径包含两 poly ──
        public static void TestRaycastCrossesPoly()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var start = new Vector3(1, 0, -1); // poly0 中心
            var end = new Vector3(3, 0, 1);    // poly1 中心
            var startRef = NavMeshPolyRef.Create(1, 0, 0);

            bool hit = query.Raycast(startRef, start, end, filter,
                out float hitDist, out _, out var hitPath);

            AssertTrue(!hit, "Ray reaching endPos inside poly1 must NOT be blocked (t == maxDist).");
            AssertTrue(hitPath.Count >= 2,
                $"hitPath must contain at least 2 polys crossing shared edge (got {hitPath.Count}).");
            AssertNear(hitDist, Vector3.Distance(start, end), 0.1f,
                "hitDist must equal start→end distance for unblocked ray.");
        }

        // ── Raycast：终点在网格外（X=10），射线越过边界被阻挡 ──
        public static void TestRaycastHitsBoundary()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var start = new Vector3(1, 0, -1); // poly0 中心
            var end = new Vector3(10, 0, -1);  // 远超 poly0 右边界（X=2）
            var startRef = NavMeshPolyRef.Create(1, 0, 0);

            bool hit = query.Raycast(startRef, start, end, filter,
                out float hitDist, out var hitNormal, out var hitPath);

            AssertTrue(hit, "Ray to outside must hit boundary.");
            // 射线穿越共享边（X=2, t=1）进入 poly1，命中 poly1 右边界 X=4 → t=3
            AssertNear(hitDist, 3f, 0.05f, "hitDist to poly1 right boundary (X=4) must be 3.");
            // 命中面法线为 poly1 右边界外法线（Detour 约定：指向多边形外），即 +X
            AssertNear(hitNormal.X, 1f, 0.05f, "hitNormal.X must be outward +X.");
            AssertNear(hitNormal.Z, 0f, 0.05f, "hitNormal.Z must be 0.");
            // 跨越路径应含起始 poly0 + 转入 poly1
            AssertTrue(hitPath.Count == 2, $"hitPath must contain [poly0, poly1] (got {hitPath.Count}).");
        }

        // ── MoveAlongSurface：沿 +X 方向走到网格外，应停在 poly0 右边界（X=2）──
        public static void TestMoveAlongSurfaceStopsAtWall()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var start = new Vector3(1, 0, -1);
            var dir = new Vector3(1, 0, 0); // 沿 +X
            var result = query.MoveAlongSurface(NavMeshPolyRef.Create(1, 0, 0), start, dir, filter, maxDist: 10f);

            // 射线从 poly0 (Z=-1) 沿 +X 穿越 poly0/poly1 共享边（X=2），进入 poly1，停在 poly1 右边界 X=4
            AssertNear(result.X, 4f, 0.05f, "MoveAlongSurface must stop at poly1 right boundary X=4.");
            AssertNear(result.Z, -1f, 0.05f, "MoveAlongSurface must preserve Z during +X motion.");
        }

        // ── MoveAlongSurface：目标在可达 poly1 内 → 直接到达 ──
        public static void TestMoveAlongSurfaceReachesTarget()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var start = new Vector3(1, 0, -1);
            var end = new Vector3(3, 0, 1);
            var dir = end - start;
            var result = query.MoveAlongSurface(NavMeshPolyRef.Create(1, 0, 0), start, dir, filter,
                maxDist: dir.Length());

            AssertVec(result, end, 0.1f, "MoveAlongSurface must reach target inside poly1.");
        }

        // ── FindPath A*：poly0 → poly1 跨共享边可达，路径 [poly0, poly1] ──
        public static void TestFindPathReachable()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var startRef = NavMeshPolyRef.Create(1, 0, 0);
            var endRef = NavMeshPolyRef.Create(1, 0, 1);
            var start = new Vector3(1, 0, -1);
            var end = new Vector3(3, 0, 1);

            bool ok = query.FindPath(startRef, endRef, start, end, filter, out var path);

            AssertTrue(ok, "A* must find path across adjacent polys.");
            AssertTrue(path.Count == 2, $"Path must contain 2 refs (start+end), got {path.Count}.");
            AssertEqual(0, path[0].Poly, "Path[0] must be poly0.");
            AssertEqual(1, path[1].Poly, "Path[1] must be poly1.");
        }

        // ── FindPath：同一 poly 起点=终点 → 单节点路径 ──
        public static void TestFindPathSamePoly()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            var sameRef = NavMeshPolyRef.Create(1, 0, 0);
            bool ok = query.FindPath(sameRef, sameRef, new Vector3(1, 0, -1), new Vector3(1.5f, 0, -1.5f),
                filter, out var path);

            AssertTrue(ok, "FindPath within same poly must succeed.");
            AssertEqual(1, path.Count, "Same-poly path must contain exactly 1 ref.");
        }

        // ── FindPath：无效 startRef → 返回 false ──
        public static void TestFindPathInvalidRef()
        {
            var nav = BuildTwoPolyMesh();
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();

            bool ok = query.FindPath(NavMeshPolyRef.Invalid, NavMeshPolyRef.Create(1, 0, 0),
                new Vector3(1, 0, -1), new Vector3(3, 0, 1), filter, out _);

            AssertTrue(!ok, "FindPath with invalid startRef must return false.");
        }

        // ── NavMeshPolyRef：Salt/Tile/Poly 打包解包 ──
        public static void TestPolyRefPacking()
        {
            var r = NavMeshPolyRef.Create(salt: 5, tile: 3, poly: 7);
            AssertEqual((uint)5, r.Salt, "Salt bits must round-trip.");
            AssertEqual(3, r.Tile, "Tile bits must round-trip.");
            AssertEqual(7, r.Poly, "Poly bits must round-trip.");
            AssertTrue(r.IsValid, "Non-zero ref must be valid.");
            AssertTrue(!NavMeshPolyRef.Invalid.IsValid, "Zero ref must be invalid.");
        }

        // ════════════════════════════════════════════════════════════════
        //  Unity5 NavMesh 深度补充：Filter / Obstacle / ModifierVolume /
        //  Tile Salt / OffMeshConnection / NavMeshLink
        // ════════════════════════════════════════════════════════════════

        private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

        private static void Near(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}）");

        // 构造两多边形网格（指定区域类型）
        private static NavMesh BuildTwoPolyMeshAreas(byte area0, byte area1)
        {
            var nav = new NavMesh(cellSize: 0.5f, cellHeight: 0.2f, tileSize: 8f, origin: Vector3.Zero);
            var tile = new NavMeshTile { BaseTile = new NavTile() };
            var baseTile = tile.BaseTile!;

            baseTile.Vertices.AddRange(new List<Vector3>
            {
                new(0, 0, -2), new(2, 0, -2), new(2, 0, 2),
                new(0, 0, 2), new(4, 0, -2), new(4, 0, 2),
            });

            var p0 = new NavPoly { Index = 0, Flags = 1, Area = area0 };
            p0.VertIndices.AddRange(new[] { 0, 1, 2, 3 });
            p0.VertexIndices = p0.VertIndices.ToArray();
            p0.Neighbors = new[] { -1, 1, -1, -1 };
            p0.Center = new Vector3(1, 0, 0);
            baseTile.Polys.Add(p0);

            var p1 = new NavPoly { Index = 1, Flags = 1, Area = area1 };
            p1.VertIndices.AddRange(new[] { 1, 4, 5, 2 });
            p1.VertexIndices = p1.VertIndices.ToArray();
            p1.Neighbors = new[] { -1, -1, -1, 0 };
            p1.Center = new Vector3(3, 0, 0);
            baseTile.Polys.Add(p1);

            tile.BoundsMin = new Vector3(0, -1, -2);
            tile.BoundsMax = new Vector3(4, 1, 2);
            tile.BVTree.Add(new NavMeshBVNode { Bmin = new Vector3(0, -1, -1), Bmax = new Vector3(4, 1, 1), I = -1 });
            tile.BVTree.Add(new NavMeshBVNode { Bmin = new Vector3(0, -1, -1), Bmax = new Vector3(2, 1, 1), I = 0 });
            tile.BVTree.Add(new NavMeshBVNode { Bmin = new Vector3(2, -1, -1), Bmax = new Vector3(4, 1, 1), I = 1 });
            nav.AddTile(0, 0, tile);
            return nav;
        }

        // ── Filter 默认参数 ──
        public static void TestFilterDefaults()
        {
            var filter = new NavMeshFilter();
            AssertEqual(0xFFFF, filter.IncludeFlags, "默认 IncludeFlags=0xFFFF");
            AssertEqual(0, filter.ExcludeFlags, "默认 ExcludeFlags=0");
            Near(filter.AreaCost[0], 1f, 0.001f, "默认区域 0 代价");
            Near(filter.AreaCost[63], 1f, 0.001f, "默认区域 63 代价");

            var poly = new NavMeshPoly { Area = 5, Flags = 1 };
            AssertTrue(filter.IsPassable(poly), "放行任意 flag 多边形");
            Near(filter.GetCost(poly), 1f, 0.001f, "任意区域默认代价 1");
        }

        // ── Filter 自定义区域代价 ──
        public static void TestFilterCustomAreaCost()
        {
            var filter = new NavMeshFilter();
            filter.AreaCost[0] = 1f;
            filter.AreaCost[1] = 5f;
            filter.AreaCost[2] = 100f;

            Near(filter.GetCost(new NavMeshPoly { Area = 0 }), 1f, 0.001f, "平地代价");
            Near(filter.GetCost(new NavMeshPoly { Area = 1 }), 5f, 0.001f, "草地代价");
            Near(filter.GetCost(new NavMeshPoly { Area = 2 }), 100f, 0.001f, "岩浆代价");
        }

        // ── Filter ExcludeFlags 排除 ──
        public static void TestFilterExcludeFlags()
        {
            var filter = new NavMeshFilter { IncludeFlags = 0x0001, ExcludeFlags = 0x0010 };

            var walkable = new NavMeshPoly { Area = 0, Flags = 0x0001 };
            var both = new NavMeshPoly { Area = 0, Flags = 0x0011 };

            AssertTrue(filter.IsPassable(walkable), "Walkable 放行");
            AssertFalse(filter.IsPassable(both), "含 Exclude 位被排除");
        }

        // ── 内置 WalkFilter ──
        public static void TestWalkFilter()
        {
            var filter = new WalkFilter();
            AssertEqual(1, filter.IncludeFlags, "WalkFilter IncludeFlags=1");
            Near(filter.AreaCost[0], 1f, 0.001f, "WalkFilter 区域 0 代价");
            AssertTrue(filter.IsPassable(new NavMeshPoly { Area = 0, Flags = 0x0001 }), "Walkable 放行");
            AssertFalse(filter.IsPassable(new NavMeshPoly { Area = 0, Flags = 0x0000 }), "非 Walkable 排除");
        }

        // ── 内置 SwimFilter ──
        public static void TestSwimFilter()
        {
            var filter = new SwimFilter();
            AssertEqual(0x0020, filter.IncludeFlags, "SwimFilter IncludeFlags=0x20");
            Near(filter.AreaCost[5], 2f, 0.001f, "游泳区域代价 2");
        }

        // ── 高代价区域仍可达 ──
        public static void TestHighCostAreaStillReachable()
        {
            var nav = BuildTwoPolyMeshAreas(0, 1);
            var query = new NavMeshQuery(nav);
            var filter = new NavMeshFilter();
            filter.AreaCost[0] = 1f;
            filter.AreaCost[1] = 10f;

            AssertTrue(query.FindNearestPoly(new Vector3(1, 0, 0), new Vector3(1, 1, 1), filter, out var startRef, out _), "起点命中");
            AssertTrue(query.FindNearestPoly(new Vector3(3, 0, 0), new Vector3(1, 1, 1), filter, out var endRef, out _), "终点命中");

            bool found = query.FindPath(startRef, endRef, new Vector3(1, 0, 0), new Vector3(3, 0, 0), filter, out var path);
            AssertTrue(found, "跨高代价区域仍可达");
            AssertTrue(path.Count >= 2, $"路径至少 2 个 PolyRef（实际 {path.Count}）");
        }

        // ── 区域代价累积 ──
        public static void TestAreaCostAccumulates()
        {
            var nav = BuildTwoPolyMeshAreas(0, 1);
            var tile = nav.GetTileAt(0, 0)!;
            var filter = new NavMeshFilter();
            filter.AreaCost[0] = 1f;
            filter.AreaCost[1] = 5f;

            float cost0 = filter.GetCost(new NavMeshPoly { Area = tile.BaseTile!.Polys[0].Area });
            float cost1 = filter.GetCost(new NavMeshPoly { Area = tile.BaseTile!.Polys[1].Area });
            Near(cost0 + cost1, 6f, 0.001f, "跨两区域累积代价 1+5=6");
        }

        // ── NavMeshObstacle 圆柱命中 ──
        public static void TestDynamicObstacleContainsPoint()
        {
            var obs = new NavMeshObstacle
            {
                Position = new Vector3(0, 0, 0),
                Radius = 2f,
                Height = 3f,
                IsActive = true
            };

            AssertTrue(obs.ContainsPoint(new Vector3(0, 1, 0)), "圆柱中心命中");
            AssertTrue(obs.ContainsPoint(new Vector3(1.5f, 1f, 0)), "半径内点命中");
            AssertTrue(obs.ContainsPoint(new Vector3(2, 1, 0)), "边界点命中");
            AssertFalse(obs.ContainsPoint(new Vector3(3, 1, 0)), "半径外不命中");
            AssertFalse(obs.ContainsPoint(new Vector3(0, 4, 0)), "高度外不命中");
        }

        // ── NavMeshObstacle 非激活 ──
        public static void TestDynamicObstacleInactive()
        {
            var obs = new NavMeshObstacle { Position = new Vector3(0, 0, 0), Radius = 2f, IsActive = false };
            // ContainsPoint 应检查 IsActive，非激活时返回 false
            AssertFalse(obs.ContainsPoint(new Vector3(0, 1, 0)), "非激活障碍不应命中");
            AssertFalse(obs.IsActive, "IsActive=false 已由 ContainsPoint 内部检查");
        }

        // ── NavMeshObstacle 影响半径 / Carve 标志 ──
        public static void TestDynamicObstacleInfluenceAndCarve()
        {
            var carving = new NavMeshObstacle { Radius = 1f, InfluenceRadius = 3f, CarveNavMesh = true };
            var avoidance = new NavMeshObstacle { CarveNavMesh = false, AvoidanceEnabled = true };

            AssertEqual(1f, carving.Radius, "本体半径");
            AssertEqual(3f, carving.InfluenceRadius, "影响半径");
            AssertTrue(carving.CarveNavMesh, "雕刻型障碍");
            AssertFalse(avoidance.CarveNavMesh, "非雕刻（仅避让）");
        }

        // ── ModifierVolume 禁用导航 ──
        public static void TestModifierVolumeDisable()
        {
            var volume = new NavMeshModifierVolume
            {
                Bounds = new BoundingBox(new Vector3(-5, -1, -5), new Vector3(5, 2, 5)),
                DisableNavigation = true,
                IsActive = true
            };

            AssertTrue(volume.ContainsPoint(new Vector3(0, 0, 0)), "体积内命中");
            AssertFalse(volume.ContainsPoint(new Vector3(10, 0, 10)), "体积外不命中");
            AssertTrue(volume.DisableNavigation, "标记禁用导航");
        }

        // ── ModifierVolume 代价覆盖 ──
        public static void TestModifierVolumeCostOverride()
        {
            var volume = new NavMeshModifierVolume
            {
                Bounds = new BoundingBox(new Vector3(-1, -1, -1), new Vector3(1, 1, 1)),
                CostOverride = 50f,
                IsActive = true
            };

            AssertEqual(50f, volume.CostOverride, "代价覆盖值");
            AssertTrue(volume.ContainsPoint(new Vector3(0, 0, 0)), "覆盖区域内命中");
        }

        // ── ModifierVolume 非激活 ──
        public static void TestModifierVolumeInactive()
        {
            var volume = new NavMeshModifierVolume
            {
                Bounds = new BoundingBox(new Vector3(-5, -1, -5), new Vector3(5, 2, 5)),
                IsActive = false
            };
            AssertFalse(volume.ContainsPoint(new Vector3(0, 0, 0)), "非激活时不命中");
        }

        // ── Tile Salt 失效：移除 Tile 后 GetTileAt 返回 null ──
        public static void TestTileSaltInvalidatesRefs()
        {
            var nav = BuildTwoPolyMeshAreas(0, 0);
            var tile = nav.GetTileAt(0, 0)!;
            var polyRef = NavMeshPolyRef.Create(tile.Salt, 0, 0);

            AssertTrue(polyRef.IsValid, "PolyRef 有效");
            AssertEqual(tile.Salt, polyRef.Salt, "PolyRef Salt 匹配 Tile");

            nav.RemoveTile(0, 0);
            AssertTrue(nav.GetTileAt(0, 0) == null, "移除 Tile 后 GetTileAt 返回 null");
        }

        // ── OffMeshConnection 数据 ──
        public static void TestOffMeshConnectionData()
        {
            var con = new NavMeshOffMeshConnection
            {
                PosStart = new Vector3(0, 0, 0),
                PosEnd = new Vector3(0, 3, 0),
                Radius = 1.5f,
                Flags = 1,
                Area = 0
            };

            Near(con.PosStart.X, 0f, 0.001f, "起点 X");
            Near(con.PosEnd.Y, 3f, 0.001f, "终点 Y（跳跃顶点）");
            AssertEqual(1.5f, con.Radius, "半径");
            AssertEqual(1, con.Flags, "Walkable flag");
        }

        // ── NavMeshLink 类型与转换 ──
        public static void TestNavMeshLinkTypes()
        {
            var jump = new NavMeshLink { Start = new Vector3(0, 0, 0), End = new Vector3(3, 2, 0), Type = NavMeshLinkType.Jump };
            var ladder = new NavMeshLink { Type = NavMeshLinkType.Ladder };
            var teleport = new NavMeshLink { Type = NavMeshLinkType.Teleport };

            AssertEqual(NavMeshLinkType.Jump, jump.Type, "Jump 类型");
            AssertEqual(LinkType.Jump, jump.ToBaseLink().Type, "转换基础 Jump");
            AssertEqual(LinkType.Ladder, ladder.ToBaseLink().Type, "转换基础 Ladder");
            AssertEqual(LinkType.Teleport, teleport.ToBaseLink().Type, "转换基础 Teleport");
            AssertTrue(jump.Bidirectional, "默认双向");
            AssertEqual(1.0f, jump.CostModifier, "默认代价修正 1.0");
        }
    }
}
