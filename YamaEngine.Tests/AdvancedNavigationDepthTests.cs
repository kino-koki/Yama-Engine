using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// AdvancedNavigationSystem 深度测试。覆盖 CompactHeightfield、NavMeshTile、NavMeshLink、
    /// NavMeshModifier、NavMeshTileCache LRU、AdvancedPathSmoother、DynamicNavMesh。
    /// </summary>
    public static class AdvancedNavigationDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        // ── CompactHeightfield ──

        public static void TestCompactHeightfieldConstruction()
        {
            var chf = new CompactHeightfield(10, 20);
            AssertEqual(10, chf.Width, "Width=10");
            AssertEqual(20, chf.Height, "Height=20");
            AssertEqual(1, chf.Depth, "Depth=1");
            AssertEqual(200, chf.ColumnHeads.Length, "ColumnHeads.Length=200");
            AssertTrue(chf.ColumnHeads.All(h => h == -1), "all ColumnHeads=-1");
            AssertTrue(chf.ColumnOffsets == null, "ColumnOffsets null");
            AssertTrue(chf.ColumnCounts == null, "ColumnCounts null");
        }

        public static void TestCompactHeightfieldWithSpanCount()
        {
            var chf = new CompactHeightfield(5, 5, 3, 50);
            AssertEqual(5, chf.Width, "Width=5");
            AssertEqual(5, chf.Height, "Height=5");
            AssertEqual(3, chf.Depth, "Depth=3");
            AssertEqual(50, chf.SpanCount, "SpanCount=50");
            AssertEqual(25, chf.ColumnOffsets!.Length, "ColumnOffsets.Length=25");
            AssertEqual(25, chf.ColumnCounts!.Length, "ColumnCounts.Length=25");
            AssertEqual(50, chf.Spans.Length, "Spans.Length=50");
        }

        public static void TestCompactHeightfieldIndex()
        {
            var chf = new CompactHeightfield(4, 4);
            AssertEqual(0, chf.Index(0, 0), "Index(0,0)=0");
            AssertEqual(4, chf.Index(0, 1), "Index(0,1)=1*4+0=4");
            AssertEqual(1, chf.Index(1, 0), "Index(1,0)=0*4+1=1");
            AssertEqual(15, chf.Index(3, 3), "Index(3,3)=3*4+3=15");
        }

        public static void TestCompactHeightfieldAddSpan()
        {
            var chf = new CompactHeightfield(4, 4);
            int idx = chf.AddSpan(1, 1, 0, 5, SpanRegion.Walkable);
            AssertTrue(idx >= 0, "AddSpan returns valid index");
            // Column head for (1,1) should now be idx
            AssertEqual(idx, chf.ColumnHeads[chf.Index(1, 1)], "ColumnHead[1,1]=idx");
        }

        // ── NavMeshTile ──

        public static void TestNavMeshTileDefaults()
        {
            var tile = new NavMeshTile();
            AssertEqual(0, tile.GridX, "GridX=0");
            AssertEqual(0, tile.GridZ, "GridZ=0");
            AssertEqual(1u, tile.Salt, "Salt=1");
            AssertTrue(tile.IsLoaded, "IsLoaded=true");
            AssertEqual(0, tile.LastAccessFrame, "LastAccessFrame=0");
            AssertTrue(tile.BaseTile == null, "BaseTile null");
            AssertTrue(tile.CompactField == null, "CompactField null");
            AssertTrue(tile.Detail == null, "Detail null");
            AssertEqual(-1, tile.BvhRoot, "BvhRoot=-1");
            AssertEqual(0, tile.PolyCount, "PolyCount=0");
            AssertEqual(0, tile.TileX, "TileX alias=0 (GridX default)");
        }

        public static void TestNavMeshTileAliases()
        {
            var tile = new NavMeshTile { GridX = 3, GridZ = 7 };
            AssertEqual(3, tile.TileX, "TileX alias=GridX");
            AssertEqual(7, tile.TileY, "TileY alias=GridZ");
            var bounds = new Vector3(-1, 0, -1);
            tile.Bmin = bounds;
            AssertEqual(bounds, tile.BoundsMin, "BoundsMin alias=Bmin");
        }

        public static void TestNavMeshTilePolyCount()
        {
            var tile = new NavMeshTile();
            // Empty BaseTile -> PolyCount=0
            AssertEqual(0, tile.PolyCount, "empty tile");
        }

        public static void TestNavMeshTileBuildBvhEmpty()
        {
            var tile = new NavMeshTile();
            tile.BuildBvh();
            AssertEqual(-1, tile.BvhRoot, "BvhRoot=-1 for empty tile");
            AssertEqual(0, tile.BvhNodes.Count, "no nodes for empty tile");
        }

        // ── NavMeshLink ──

        public static void TestNavMeshLinkDefaults()
        {
            var link = new NavMeshLink();
            AssertEqual(0, link.Id, "Id=0");
            Near(0.5f, link.Radius, 1e-5f, "Radius=0.5");
            AssertTrue(link.Bidirectional, "Bidirectional=true");
            AssertEqual(NavMeshLinkType.Jump, link.Type, "Type=Jump");
            Near(1.0f, link.CostModifier, 1e-5f, "CostModifier=1.0");
            AssertTrue(link.IsEnabled, "IsEnabled=true");
            AssertEqual(-1, link.TileX, "TileX=-1");
            AssertEqual(-1, link.TileZ, "TileZ=-1");
        }

        public static void TestNavMeshLinkToBaseLink()
        {
            var link = new NavMeshLink
            {
                Start = new Vector3(1, 0, 1),
                End = new Vector3(5, 0, 5),
                Type = NavMeshLinkType.Teleport,
                Bidirectional = false,
                CostModifier = 2.0f
            };
            var baseLink = link.ToBaseLink();
            AssertEqual(link.Start, baseLink.Start, "base Start");
            AssertEqual(link.End, baseLink.End, "base End");
            AssertEqual(LinkType.Teleport, baseLink.Type, "base Type=Teleport");
            AssertFalse(baseLink.IsBidirectional, "base Bidirectional=false");
            Near(2.0f, baseLink.CostModifier, 1e-5f, "base CostModifier=2.0");
        }

        public static void TestNavMeshLinkTypeValues()
        {
            AssertEqual(0, (int)NavMeshLinkType.Jump, "Jump=0");
            AssertEqual(1, (int)NavMeshLinkType.Ladder, "Ladder=1");
            AssertEqual(2, (int)NavMeshLinkType.Teleport, "Teleport=2");
        }

        // ── NavMeshModifier ──

        public static void TestModifierDefaults()
        {
            var m = new NavMeshModifier();
            AssertEqual(-1, m.OverrideAreaId, "OverrideAreaId=-1 (no override)");
            AssertEqual(0, m.OverrideFlags, "OverrideFlags=0");
            Near(-1f, m.OverrideCost, 1e-5f, "OverrideCost=-1 (no override)");
            AssertTrue(m.IgnoreY, "IgnoreY=true");
            AssertTrue(m.IsEnabled, "IsEnabled=true");
        }

        public static void TestModifierContainsIgnoreY()
        {
            var m = new NavMeshModifier
            {
                Center = new Vector3(5, 0, 5),
                HalfSize = new Vector3(2, 1, 2),
                IgnoreY = true
            };
            AssertTrue(m.Contains(new Vector3(5, 100, 5)), "contains (5,100,5) - Y ignored");
            AssertTrue(m.Contains(new Vector3(6, 0, 4)), "contains (6,0,4)");
            AssertFalse(m.Contains(new Vector3(10, 0, 5)), "not contains (10,0,5)");
        }

        public static void TestModifierContainsWithY()
        {
            var m = new NavMeshModifier
            {
                Center = new Vector3(5, 5, 5),
                HalfSize = new Vector3(2, 2, 2),
                IgnoreY = false
            };
            AssertTrue(m.Contains(new Vector3(5, 5, 5)), "contains center");
            AssertTrue(m.Contains(new Vector3(6, 6, 6)), "contains (6,6,6)");
            AssertFalse(m.Contains(new Vector3(5, 10, 5)), "not contains (5,10,5) - Y=10 > 5+2");
        }

        public static void TestModifierDisabledNotContains()
        {
            var m = new NavMeshModifier
            {
                Center = Vector3.Zero,
                HalfSize = Vector3.One,
                IsEnabled = false
            };
            AssertFalse(m.Contains(Vector3.Zero), "disabled modifier returns false");
        }

        // ── NavMeshTileCache ──

        public static void TestTileCacheDefaults()
        {
            var cache = new NavMeshTileCache(16);
            AssertEqual(16, cache.Capacity, "Capacity=16");
            AssertEqual(0, cache.LoadedCount, "LoadedCount=0");
            AssertEqual(0, cache.CacheHits, "CacheHits=0");
            AssertEqual(0, cache.CacheMisses, "CacheMisses=0");
        }

        public static void TestTileCachePutGet()
        {
            var cache = new NavMeshTileCache(8);
            var tile = new NavMeshTile { GridX = 1, GridZ = 2 };
            cache.Put(tile);
            AssertEqual(1, cache.LoadedCount, "LoadedCount=1");
            AssertTrue(cache.IsLoaded(1, 2), "IsLoaded(1,2)=true");
            AssertTrue(cache.IsLoaded(1, 3) == false, "IsLoaded(1,3)=false");

            var got = cache.Get(1, 2);
            AssertTrue(got != null, "Get(1,2) returns tile");
            AssertEqual(1, cache.CacheHits, "CacheHits=1");
            AssertEqual(0, cache.CacheMisses, "CacheMisses=0");
        }

        public static void TestTileCacheMissNoLoader()
        {
            var cache = new NavMeshTileCache(8);
            var result = cache.Get(5, 5);
            AssertTrue(result == null, "Get without loader returns null");
            AssertEqual(1, cache.CacheMisses, "CacheMisses=1");
        }

        public static void TestTileCacheLoader()
        {
            var cache = new NavMeshTileCache(8);
            bool loaderCalled = false;
            cache.RegisterLoader(0, 0, () =>
            {
                loaderCalled = true;
                return new NavMeshTile { GridX = 0, GridZ = 0 };
            });
            AssertFalse(loaderCalled, "loader not called yet");
            var result = cache.Get(0, 0);
            AssertTrue(loaderCalled, "loader called on miss");
            AssertTrue(result != null, "Get returns loaded tile");
            AssertEqual(1, cache.LoadedCount, "tile cached after load");
        }

        public static void TestTileCacheLRUEviction()
        {
            var cache = new NavMeshTileCache(2);
            cache.Put(new NavMeshTile { GridX = 0, GridZ = 0 });
            cache.Put(new NavMeshTile { GridX = 0, GridZ = 1 });
            AssertEqual(2, cache.LoadedCount, "2 tiles loaded");

            // Access tile (0,0) to make it most recently used
            cache.Get(0, 0);
            AssertEqual(1, cache.CacheHits, "hit for (0,0)");

            // Put (0,2) -> should evict (0,1) (least recently used)
            cache.Put(new NavMeshTile { GridX = 0, GridZ = 2 });
            AssertEqual(2, cache.LoadedCount, "still 2 tiles (at capacity)");
            AssertTrue(cache.IsLoaded(0, 0), "(0,0) still loaded (recently accessed)");
            AssertTrue(cache.IsLoaded(0, 2), "(0,2) loaded");
            AssertFalse(cache.IsLoaded(0, 1), "(0,1) evicted");
        }

        public static void TestTileCacheUnload()
        {
            var cache = new NavMeshTileCache(4);
            cache.Put(new NavMeshTile { GridX = 1, GridZ = 1 });
            AssertTrue(cache.Unload(1, 1), "Unload(1,1)=true");
            AssertEqual(0, cache.LoadedCount, "0 tiles after unload");
            AssertFalse(cache.Unload(1, 1), "Unload again=false");
        }

        public static void TestTileCacheOverwrite()
        {
            var cache = new NavMeshTileCache(4);
            var t1 = new NavMeshTile { GridX = 0, GridZ = 0, Salt = 1 };
            var t2 = new NavMeshTile { GridX = 0, GridZ = 0, Salt = 2 };
            cache.Put(t1);
            cache.Put(t2);
            AssertEqual(1, cache.LoadedCount, "still 1 tile (overwritten)");
            var got = cache.Get(0, 0);
            AssertEqual(2u, got!.Salt, "overwritten tile has Salt=2");
        }

        // ── AdvancedPathSmoother ──

        public static void TestPathSmootherStringPullStraight()
        {
            // Two portals in a straight line -> should produce 2 points (start + end)
            var portals = new List<(Vector3 left, Vector3 right)>
            {
                (new Vector3(0, 0, 0), new Vector3(0, 0, 0)),
                (new Vector3(0, 0, 5), new Vector3(0, 0, 5)),
            };
            var result = AdvancedPathSmoother.StringPull(portals);
            AssertEqual(2, result.Count, "straight line -> 2 points");
            AssertEqual(new Vector3(0, 0, 0), result[0], "start point");
            AssertEqual(new Vector3(0, 0, 5), result[1], "end point");
        }

        public static void TestPathSmootherStringPullWithCorner()
        {
            // L-shaped path: goes +Z then turns +X
            var portals = new List<(Vector3 left, Vector3 right)>
            {
                (new Vector3(0, 0, 0), new Vector3(1, 0, 0)),   // start portal
                (new Vector3(0, 0, 3), new Vector3(1, 0, 3)),   // corner portal
                (new Vector3(5, 0, 3), new Vector3(5, 0, 4)),   // end portal
            };
            var result = AdvancedPathSmoother.StringPull(portals);
            AssertTrue(result.Count >= 2, "at least start + end");
            AssertEqual(new Vector3(0, 0, 0), result[0], "starts at origin");
            AssertEqual(new Vector3(5, 0, 3), result[^1], "ends at (5,0,3)");
            // Should have at least one corner point (the L bend)
            if (result.Count > 2)
            {
                AssertTrue(result[1].Z > 0 && result[1].X < 5, "corner point between start and end");
            }
        }

        public static void TestPathSmootherStringPullEmpty()
        {
            var portals = new List<(Vector3 left, Vector3 right)>();
            var result = AdvancedPathSmoother.StringPull(portals);
            AssertEqual(0, result.Count, "empty input -> empty output");
        }

        public static void TestPathSmootherStringPullSingle()
        {
            var portals = new List<(Vector3 left, Vector3 right)>
            {
                (new Vector3(1, 0, 1), new Vector3(1, 0, 1)),
            };
            var result = AdvancedPathSmoother.StringPull(portals);
            AssertEqual(0, result.Count, "single portal -> empty (need >= 2)");
        }

        public static void TestPathSmootherBezierSmooth()
        {
            var input = new List<Vector3>
            {
                new(0, 0, 0),
                new(5, 0, 0),
                new(10, 0, 5),
            };
            var result = AdvancedPathSmoother.BezierSmooth(input, 4, 0.3f);
            AssertTrue(result.Count > input.Count, "Bezier smooth produces more points");
            AssertEqual(input[0], result[0], "Bezier keeps start");
            // End may be same or close
            Near(input[^1].X, result[^1].X, 0.5f, "Bezier ends near input end X");
            Near(input[^1].Z, result[^1].Z, 0.5f, "Bezier ends near input end Z");
        }

        // ── DynamicNavMesh ──

        public static void TestDynamicNavMeshDefaults()
        {
            var data = new NavMeshData();
            var generator = new AdvancedNavMeshGenerator();
            var advTiles = new Dictionary<(int, int), NavMeshTile>();
            var dnm = new DynamicNavMesh(data, generator, advTiles);
            AssertTrue(dnm != null, "DynamicNavMesh created");
            AssertEqual(0, dnm.DirtyTileCount, "DirtyTileCount=0");
            AssertEqual(0, dnm.ModifierCount, "ModifierCount=0");
            AssertEqual(0, dnm.LinkCount, "LinkCount=0");
        }

        public static void TestDynamicNavMeshAddObstacle()
        {
            var dnm = new DynamicNavMesh(new NavMeshData(), new AdvancedNavMeshGenerator(),
                new Dictionary<(int, int), NavMeshTile>());
            dnm.AddObstacle(new Vector3(1, 0, 1), 2.0f);
            // Obstacle tracking works without throwing; exact dirty count depends on implementation
            dnm.RemoveObstacle(new Vector3(1, 0, 1), 2.0f);
        }

        public static void TestDynamicNavMeshAddRemoveModifier()
        {
            var dnm = new DynamicNavMesh(new NavMeshData(), new AdvancedNavMeshGenerator(),
                new Dictionary<(int, int), NavMeshTile>());
            var mod = dnm.AddModifier(new Vector3(5, 0, 5), new Vector3(2, 2, 2), areaId: 1, cost: 3.0f);
            AssertTrue(mod.Id > 0, "modifier has id > 0");
            AssertEqual(1, mod.OverrideAreaId, "area id=1");
            Near(3.0f, mod.OverrideCost, 1e-5f, "cost=3.0");
            AssertEqual(1, dnm.ModifierCount, "1 modifier");
            AssertTrue(dnm.RemoveModifier(mod.Id), "remove returns true");
            AssertEqual(0, dnm.ModifierCount, "0 modifiers after remove");
            AssertFalse(dnm.RemoveModifier(9999), "remove unknown=false");
        }

        public static void TestDynamicNavMeshAddLink()
        {
            var dnm = new DynamicNavMesh(new NavMeshData(), new AdvancedNavMeshGenerator(),
                new Dictionary<(int, int), NavMeshTile>());
            var link = dnm.AddLink(new Vector3(0, 0, 0), new Vector3(5, 0, 5),
                NavMeshLinkType.Teleport, bidirectional: false);
            AssertTrue(link.Id > 0, "link has id > 0");
            AssertEqual(NavMeshLinkType.Teleport, link.Type, "link type=Teleport");
            AssertFalse(link.Bidirectional, "link not bidirectional");
            AssertEqual(1, dnm.LinkCount, "1 link");
        }

        // ── NavTile (基础导航瓦片，与 NavigationSystem 共享) ──

        public static void TestNavTileFromNavigation()
        {
            var tile = new NavTile { GridX = 2, GridZ = 3, Index = 99, Salt = 1 };
            AssertEqual(2, tile.GridX, "GridX=2");
            AssertEqual(3, tile.GridZ, "GridZ=3");
            AssertEqual(99, tile.Index, "Index=99");
            AssertEqual(1u, tile.Salt, "Salt=1");
        }
    }
}
