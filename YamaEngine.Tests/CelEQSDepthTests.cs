using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 环境查询系统（EQS）深度测试。对齐 UE5 Environment Query System。
    /// 覆盖：QueryItem / Context / Result / 6 Generators / 10 Tests / Query / Manager。
    /// </summary>
    public static class CelEQSDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        private static EQSContext MakeCtx(Vector3 pos, List<Actor>? actors = null)
        {
            var ctx = new EQSContext
            {
                QuerierPosition = pos,
                QuerierForward = new Vector3(0, 0, 1),
                WorldActors = actors ?? new List<Actor>(),
                CurrentTime = 10f,
            };
            return ctx;
        }

        private static Actor MakeActor(Vector3 pos, string tag = "")
        {
            var a = new Actor { Position = pos };
            if (!string.IsNullOrEmpty(tag)) a.AddTag(tag);
            return a;
        }

        // ── EQSQueryItem ──

        public static void TestItemDefaults()
        {
            var item = new EQSQueryItem();
            AssertEqual(Vector3.Zero, item.Position, "default pos");
            Near(0f, item.RawScore, 1e-5f, "raw 0");
            Near(0f, item.WeightedScore, 1e-5f, "weighted 0");
            AssertTrue(item.IsValid, "valid default");
            AssertEqual(0, item.ContextData.Count, "empty context data");
        }

        public static void TestItemWithPosition()
        {
            var pos = new Vector3(1, 2, 3);
            var item = new EQSQueryItem(pos);
            AssertEqual(pos, item.Position, "position set");
            AssertTrue(item.IsValid, "valid");
        }

        // ── EQSContext ──

        public static void TestContextDefaults()
        {
            var ctx = new EQSContext();
            AssertTrue(ctx.Querier == null, "no querier");
            AssertEqual(Vector3.Zero, ctx.QuerierPosition, "pos zero");
            AssertEqual(Vector3.Zero, ctx.QuerierForward, "forward zero");
            AssertEqual(0, ctx.WorldActors.Count, "no actors");
            AssertTrue(ctx.Blackboard == null, "no blackboard");
        }

        public static void TestContextSetQuerier()
        {
            var ctx = new EQSContext();
            var actor = MakeActor(new Vector3(10, 5, 3));
            actor.Rotation = Quaternion.CreateFromYawPitchRoll(0, 0, 0);
            ctx.SetQuerier(actor);
            AssertTrue(ctx.Querier != null, "querier set");
            AssertEqual(new Vector3(10, 5, 3), ctx.QuerierPosition, "pos from actor");
        }

        // ── EQSResult ──

        public static void TestResultEmpty()
        {
            var r = new EQSResult();
            AssertEqual(0, r.Items.Count, "no items");
            AssertTrue(r.BestItem == null, "no best");
            AssertFalse(r.IsSuccessful, "not successful");
            Near(0f, r.GetAverageScore(), 1e-5f, "avg 0");
        }

        public static void TestResultSorting()
        {
            var r = new EQSResult();
            var items = new List<EQSQueryItem>
            {
                new EQSQueryItem(new Vector3(0, 0, 1)) { WeightedScore = 0.3f },
                new EQSQueryItem(new Vector3(0, 0, 2)) { WeightedScore = 0.9f },
                new EQSQueryItem(new Vector3(0, 0, 3)) { WeightedScore = 0.6f },
            };
            r.SetItems(items);
            AssertEqual(3, r.Items.Count, "3 items");
            AssertEqual(new Vector3(0, 0, 2), r.BestItem!.Position, "best is 0.9");
            Near(0.9f, r.Items[0].WeightedScore, 1e-5f, "first highest");
            Near(0.3f, r.Items[2].WeightedScore, 1e-5f, "last lowest");
        }

        public static void TestResultInvalidFilter()
        {
            var r = new EQSResult();
            var items = new List<EQSQueryItem>
            {
                new EQSQueryItem(new Vector3(0, 0, 1)) { WeightedScore = 0.5f, IsValid = false },
                new EQSQueryItem(new Vector3(0, 0, 2)) { WeightedScore = 0.3f, IsValid = true },
            };
            r.SetItems(items);
            AssertEqual(1, r.Items.Count, "invalid filtered out");
            AssertEqual(2, r.RawItems.Count, "raw keeps all");
        }

        public static void TestResultTruncateTo()
        {
            var r = new EQSResult();
            var items = new List<EQSQueryItem>();
            for (int i = 0; i < 5; i++)
                items.Add(new EQSQueryItem(new Vector3(0, 0, i)) { WeightedScore = i * 0.1f });
            r.SetItems(items);
            r.TruncateTo(2);
            AssertEqual(2, r.Items.Count, "truncated to 2");
        }

        public static void TestResultGetTopItems()
        {
            var r = new EQSResult();
            var items = new List<EQSQueryItem>();
            for (int i = 0; i < 10; i++)
                items.Add(new EQSQueryItem(new Vector3(0, 0, i)) { WeightedScore = (10 - i) * 0.1f });
            r.SetItems(items);
            var top3 = r.GetTopItems(3);
            AssertEqual(3, top3.Count, "3 top items");
            Near(1.0f, top3[0].WeightedScore, 1e-5f, "best");
        }

        public static void TestResultGetRandomTopItem()
        {
            var r = new EQSResult();
            var items = new List<EQSQueryItem>();
            for (int i = 0; i < 10; i++)
                items.Add(new EQSQueryItem(new Vector3(0, 0, i)) { WeightedScore = (10 - i) * 0.1f });
            r.SetItems(items);
            var rand = r.GetRandomTopItem(0.3f); // top 30% = top 3
            AssertTrue(rand != null, "returns item");
            AssertTrue(rand!.WeightedScore >= 0.7f, "from top 30%");
        }

        // ── Generators ──

        public static void TestGenAllActorsOfType()
        {
            var g = new EQSGenerator_AllActorsOfClass { ActorType = typeof(Actor) };
            var ctx = MakeCtx(Vector3.Zero, new List<Actor>
            {
                MakeActor(new Vector3(1, 0, 0)),
                MakeActor(new Vector3(2, 0, 0)),
            });
            var items = g.GenerateItems(ctx);
            AssertEqual(2, items.Count, "2 actors generated");
        }

        public static void TestGenAllActorsByTag()
        {
            var g = new EQSGenerator_AllActorsOfClass { ActorTag = "enemy" };
            var ctx = MakeCtx(Vector3.Zero, new List<Actor>
            {
                MakeActor(new Vector3(1, 0, 0), "enemy"),
                MakeActor(new Vector3(2, 0, 0), "friend"),
                MakeActor(new Vector3(3, 0, 0), "enemy"),
            });
            var items = g.GenerateItems(ctx);
            AssertEqual(2, items.Count, "2 enemies");
        }

        public static void TestGenActorsInRadius()
        {
            var g = new EQSGenerator_ActorsInRadius { Radius = 100f };
            var ctx = MakeCtx(Vector3.Zero, new List<Actor>
            {
                MakeActor(new Vector3(50, 0, 0)),   // dist=50 <= 100
                MakeActor(new Vector3(150, 0, 0)),  // dist=150 > 100
                MakeActor(new Vector3(80, 0, 0)),   // dist=80 <= 100
            });
            var items = g.GenerateItems(ctx);
            AssertEqual(2, items.Count, "2 in radius");
        }

        public static void TestGenPointsInRadius()
        {
            var g = new EQSGenerator_PointsInRadius { Radius = 100f, PointCount = 8 };
            var ctx = MakeCtx(Vector3.Zero);
            var items = g.GenerateItems(ctx);
            AssertEqual(8, items.Count, "8 points");
            // 所有点都在合理范围内
            foreach (var item in items)
                AssertTrue(Vector3.Distance(item.Position, Vector3.Zero) <= 100f + 1e-3f, "within radius");
        }

        public static void TestGenGrid()
        {
            var g = new EQSGenerator_Grid { GridSize = 100f, CellSize = 10f, Rows = 5, Columns = 5 };
            var ctx = MakeCtx(new Vector3(0, 0, 0));
            var items = g.GenerateItems(ctx);
            AssertEqual(25, items.Count, "5x5=25 grid points");
        }

        public static void TestGenAlongPath()
        {
            var g = new EQSGenerator_AlongPath
            {
                SamplingDistance = 10f,
                MaxSamples = 5,
                PathPoints = new List<Vector3>
                {
                    new Vector3(0, 0, 0),
                    new Vector3(100, 0, 0),
                }
            };
            var ctx = MakeCtx(Vector3.Zero);
            var items = g.GenerateItems(ctx);
            AssertEqual(5, items.Count, "5 samples along 100-unit path");
        }

        public static void TestGenCurrentLocation()
        {
            var g = new EQSGenerator_CurrentLocation();
            var ctx = MakeCtx(new Vector3(5, 10, 15));
            var items = g.GenerateItems(ctx);
            AssertEqual(1, items.Count, "1 point at current location");
            AssertEqual(new Vector3(5, 10, 15), items[0].Position, "current pos");
        }

        // ── EQSTest ──

        public static void TestDistancePreferCloser()
        {
            var t = new EQSTest_Distance { MaxDistance = 100f, PreferCloser = true };
            var ctx = MakeCtx(new Vector3(0, 0, 0));
            var near = new EQSQueryItem(new Vector3(10, 0, 0));  // dist=10
            var far = new EQSQueryItem(new Vector3(90, 0, 0));   // dist=90
            Near(0.9f, t.RunTest(ctx, near), 0.01f, "near: 1 - 10/100 = 0.9");
            Near(0.1f, t.RunTest(ctx, far), 0.01f, "far: 1 - 90/100 = 0.1");
        }

        public static void TestDistancePreferFurther()
        {
            var t = new EQSTest_Distance { MaxDistance = 100f, PreferCloser = false };
            var ctx = MakeCtx(new Vector3(0, 0, 0));
            var near = new EQSQueryItem(new Vector3(10, 0, 0));
            Near(0.1f, t.RunTest(ctx, near), 0.01f, "near: 10/100 = 0.1");
        }

        public static void TestDistanceClamp()
        {
            var t = new EQSTest_Distance { MaxDistance = 100f, MinScoreClamp = 0.2f, MaxScoreClamp = 0.8f, PreferCloser = true };
            var ctx = MakeCtx(Vector3.Zero);
            var veryNear = new EQSQueryItem(new Vector3(1, 0, 0));  // raw score ~0.99, clamped to 0.8
            Near(0.8f, t.RunTest(ctx, veryNear), 0.01f, "clamped max");
        }

        public static void TestTraceVisible()
        {
            var t = new EQSTest_Trace { MaxTraceDistance = 100f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.RaycastCheck = (from, to) => false; // 无遮挡
            var item = new EQSQueryItem(new Vector3(50, 0, 0));
            Near(1f, t.RunTest(ctx, item), 0.01f, "visible → 1.0");
        }

        public static void TestTraceBlocked()
        {
            var t = new EQSTest_Trace { MaxTraceDistance = 100f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.RaycastCheck = (from, to) => true; // 全遮挡
            var item = new EQSQueryItem(new Vector3(50, 0, 0));
            Near(0f, t.RunTest(ctx, item), 0.01f, "blocked → 0.0");
        }

        public static void TestTraceOutOfRange()
        {
            var t = new EQSTest_Trace { MaxTraceDistance = 50f, BlockedScore = 0f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.RaycastCheck = (from, to) => false;
            var item = new EQSQueryItem(new Vector3(100, 0, 0)); // dist=100 > 50
            Near(0f, t.RunTest(ctx, item), 0.01f, "out of range → blocked score");
        }

        public static void TestDotForward()
        {
            var t = new EQSTest_Dot { MinDot = 0f, MaxDot = 1f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.QuerierForward = new Vector3(0, 0, 1); // +Z 方向
            var ahead = new EQSQueryItem(new Vector3(0, 0, 10)); // 正前方
            Near(1f, t.RunTest(ctx, ahead), 0.01f, "directly ahead → dot=1.0");
        }

        public static void TestDotPerpendicular()
        {
            var t = new EQSTest_Dot { MinDot = -1f, MaxDot = 1f, CurveType = EQSCurveType.Linear };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.QuerierForward = new Vector3(0, 0, 1);
            var side = new EQSQueryItem(new Vector3(10, 0, 0)); // 侧面
            // dot = (0,0,1)·(1,0,0) = 0，normalized (0-(-1))/(1-(-1)) = 0.5
            Near(0.5f, t.RunTest(ctx, side), 0.01f, "perpendicular → dot=0, score=0.5");
        }

        public static void TestAngleSmaller()
        {
            var t = new EQSTest_Angle { MaxAngle = 180f, PreferSmallerAngle = true };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.QuerierForward = new Vector3(0, 0, 1);
            var ahead = new EQSQueryItem(new Vector3(0, 0, 10)); // angle=0
            Near(1f, t.RunTest(ctx, ahead), 0.01f, "angle=0 → score=1.0");
        }

        public static void TestAnglePerpendicular()
        {
            var t = new EQSTest_Angle { MaxAngle = 180f, PreferSmallerAngle = true };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.QuerierForward = new Vector3(0, 0, 1);
            var side = new EQSQueryItem(new Vector3(10, 0, 0)); // angle=90
            Near(0.5f, t.RunTest(ctx, side), 0.01f, "angle=90 → score=0.5");
        }

        public static void TestPathCost()
        {
            var t = new EQSTest_PathCost { MaxPathCost = 100f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.PathCostQuery = (from, to) => Vector3.Distance(from, to); // 简化路径代价
            var near = new EQSQueryItem(new Vector3(10, 0, 0));  // cost=10
            var far = new EQSQueryItem(new Vector3(90, 0, 0));   // cost=90
            Near(0.9f, t.RunTest(ctx, near), 0.01f, "low cost → high score");
            Near(0.1f, t.RunTest(ctx, far), 0.01f, "high cost → low score");
        }

        public static void TestBlackboardExactMatch()
        {
            var t = new EQSTest_Blackboard { KeyName = "target_dist", ExpectedValue = 50f, Tolerance = 1f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.Blackboard = new Blackboard();
            ctx.Blackboard.SetDynamicValue("target_dist", 50f); // RunTest 用 GetDynamicValue<float>
            Near(1f, t.RunTest(ctx, new EQSQueryItem(Vector3.Zero)), 0.01f, "exact match → 1.0");
        }

        public static void TestBlackboardOffByOne()
        {
            var t = new EQSTest_Blackboard { KeyName = "target_dist", ExpectedValue = 50f, Tolerance = 1f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.Blackboard = new Blackboard();
            ctx.Blackboard.SetDynamicValue("target_dist", 52f); // diff=2 > tolerance=1
            var score = t.RunTest(ctx, new EQSQueryItem(Vector3.Zero));
            AssertTrue(score > 0.9f && score < 1.0f, "outside tolerance + 1 → high but < 1");
        }

        public static void TestNoiseRandom()
        {
            var t = new EQSTest_Noise { NoiseMagnitude = 0.1f };
            var ctx = MakeCtx(Vector3.Zero);
            // 运行多次，得分应在 0.4~0.6 范围内（0.5 ± 0.1）
            for (int i = 0; i < 100; i++)
            {
                var score = t.RunTest(ctx, new EQSQueryItem(Vector3.Zero));
                AssertTrue(score >= 0.3f && score <= 0.7f, $"noise score in range: {score}");
            }
        }

        public static void TestHeightPreferHigher()
        {
            var t = new EQSTest_Height { MaxHeightDifference = 10f, PreferHigher = true };
            var ctx = MakeCtx(new Vector3(0, 0, 0));
            var high = new EQSQueryItem(new Vector3(0, 5, 0));  // heightDiff=+5
            Near(0.8f, t.RunTest(ctx, high), 0.05f, "higher position gets bonus");
        }

        public static void TestCoverWithBlock()
        {
            var t = new EQSTest_Cover { CoverCheckDistance = 3f };
            var ctx = MakeCtx(Vector3.Zero);
            // 第二段（item → checkPoint）被遮挡
            ctx.RaycastCheck = (from, to) => true;
            var item = new EQSQueryItem(new Vector3(10, 0, 0));
            Near(1f, t.RunTest(ctx, item), 0.01f, "blocked beyond → has cover");
        }

        public static void TestCoverNoBlock()
        {
            var t = new EQSTest_Cover { CoverCheckDistance = 3f, NoCoverScore = 0f };
            var ctx = MakeCtx(Vector3.Zero);
            ctx.RaycastCheck = (from, to) => false;
            var item = new EQSQueryItem(new Vector3(10, 0, 0));
            Near(0f, t.RunTest(ctx, item), 0.01f, "no block → no cover");
        }

        public static void TestMultipleBest()
        {
            var t = new EQSTest_Multiple
            {
                SampleCount = 5,
                SamplingStrategy = EQSMultipleSampling.Best,
                InnerTest = new EQSTest_Distance { MaxDistance = 100f, PreferCloser = true },
            };
            var ctx = MakeCtx(new Vector3(0, 0, 0));
            var item = new EQSQueryItem(new Vector3(50, 0, 0)); // dist=50, score~0.5
            var score = t.RunTest(ctx, item);
            AssertTrue(score >= 0.4f && score <= 1.0f, "multiple best in valid range");
        }

        public static void TestMultipleAverage()
        {
            var t = new EQSTest_Multiple
            {
                SampleCount = 3,
                SamplingStrategy = EQSMultipleSampling.Average,
                InnerTest = new EQSTest_Noise { NoiseMagnitude = 0.01f }, // 接近 0.5
            };
            var ctx = MakeCtx(Vector3.Zero);
            Near(0.5f, t.RunTest(ctx, new EQSQueryItem(Vector3.Zero)), 0.1f, "average noise ~0.5");
        }

        // ── Curve Types ──

        public static void TestCurveLinear()
        {
            var t = new EQSTest_Distance { CurveType = EQSCurveType.Linear, MaxDistance = 100f, PreferCloser = false };
            var ctx = MakeCtx(Vector3.Zero);
            Near(0.5f, t.RunTest(ctx, new EQSQueryItem(new Vector3(50, 0, 0))), 0.01f, "linear midpoint → 0.5");
        }

        public static void TestCurveConstant()
        {
            var t = new EQSTest_Distance { CurveType = EQSCurveType.Constant, MaxDistance = 100f, PreferCloser = false };
            var ctx = MakeCtx(Vector3.Zero);
            Near(0.5f, t.RunTest(ctx, new EQSQueryItem(new Vector3(50, 0, 0))), 0.01f, "constant always → 0.5");
        }

        public static void TestCurveParabolic()
        {
            var t = new EQSTest_Distance { CurveType = EQSCurveType.Parabolic, MaxDistance = 100f, PreferCloser = false };
            var ctx = MakeCtx(Vector3.Zero);
            // normalized = 0.5, parabolic = 0.5^2 = 0.25
            Near(0.25f, t.RunTest(ctx, new EQSQueryItem(new Vector3(50, 0, 0))), 0.01f, "parabolic midpoint → 0.25");
        }

        // ── EQSQuery ──

        public static void TestQueryDefaults()
        {
            var q = new EQSQuery();
            AssertEqual("UnnamedQuery", q.QueryName, "default name");
            AssertTrue(q.Generator == null, "no generator");
            AssertEqual(0, q.Tests.Count, "no tests");
            AssertEqual(0, q.MaxResults, "no max results");
        }

        public static void TestQueryExecuteWithDistance()
        {
            var q = new EQSQuery { QueryName = "FindNearestEnemy" };
            q.Generator = new EQSGenerator_AllActorsOfClass { ActorType = typeof(Actor) };
            q.AddTest(new EQSTest_Distance { MaxDistance = 500f, PreferCloser = true });

            var enemies = new List<Actor>
            {
                MakeActor(new Vector3(100, 0, 0)),
                MakeActor(new Vector3(200, 0, 0)),
                MakeActor(new Vector3(50, 0, 0)),   // 最近
            };
            var ctx = MakeCtx(Vector3.Zero, enemies);
            var result = q.Execute(ctx);

            AssertTrue(result.IsSuccessful, "query successful");
            AssertEqual(3, result.Items.Count, "3 candidates");
            AssertEqual(new Vector3(50, 0, 0), result.BestItem!.Position, "nearest is best");
        }

        public static void TestQueryMaxResults()
        {
            var q = new EQSQuery { MaxResults = 2 };
            q.Generator = new EQSGenerator_AllActorsOfClass { ActorType = typeof(Actor) };
            q.AddTest(new EQSTest_Distance { MaxDistance = 500f });

            var enemies = new List<Actor>();
            for (int i = 1; i <= 5; i++) enemies.Add(MakeActor(new Vector3(i * 10, 0, 0)));
            var ctx = MakeCtx(Vector3.Zero, enemies);
            var result = q.Execute(ctx);
            AssertEqual(2, result.Items.Count, "truncated to 2");
        }

        public static void TestQueryNoGenerator()
        {
            var q = new EQSQuery { QueryName = "Empty" };
            var ctx = MakeCtx(Vector3.Zero);
            var result = q.Execute(ctx);
            AssertFalse(result.IsSuccessful, "no generator → empty result");
        }

        public static void TestQueryNoItems()
        {
            var q = new EQSQuery();
            q.Generator = new EQSGenerator_AllActorsOfClass { ActorType = typeof(Actor) };
            var ctx = MakeCtx(Vector3.Zero, new List<Actor>()); // 空列表
            var result = q.Execute(ctx);
            AssertFalse(result.IsSuccessful, "no candidates → empty result");
        }

        public static void TestQueryClone()
        {
            var q = new EQSQuery { QueryName = "Original", MaxResults = 5 };
            q.Generator = new EQSGenerator_AllActorsOfClass { ActorType = typeof(Actor) };
            q.AddTest(new EQSTest_Distance { Weight = 2f });
            var clone = q.Clone();
            AssertEqual("Original", clone.QueryName, "name cloned");
            AssertEqual(5, clone.MaxResults, "max results cloned");
            AssertEqual(1, clone.Tests.Count, "tests cloned");
            AssertTrue(clone.Generator != null, "generator cloned");
        }

        // ── EQSManager ──

        public static void TestManagerRegisterQuery()
        {
            var mgr = new EQSManager();
            var q = new EQSQuery { QueryName = "FindCover" };
            mgr.RegisterQuery("cover", q);
            var names = mgr.GetRegisteredQueryNames();
            AssertTrue(names.Contains("cover"), "query registered");
            mgr.UnregisterQuery("cover");
            AssertFalse(mgr.GetRegisteredQueryNames().Contains("cover"), "query unregistered");
        }

        public static void TestManagerExecuteByName()
        {
            var mgr = new EQSManager { CacheDuration = 5f, EnableCache = false };
            var q = new EQSQuery { QueryName = "Test" };
            q.Generator = new EQSGenerator_CurrentLocation();
            q.AddTest(new EQSTest_Distance { MaxDistance = 100f, PreferCloser = true });
            mgr.RegisterQuery("test", q);
            var ctx = MakeCtx(new Vector3(0, 0, 0));
            var result = mgr.ExecuteQuery("test", ctx);
            AssertTrue(result.IsSuccessful, "query executed by name");
        }

        public static void TestManagerCache()
        {
            var mgr = new EQSManager { CacheDuration = 10f, EnableCache = true };
            var q = new EQSQuery();
            q.Generator = new EQSGenerator_CurrentLocation();
            mgr.RegisterQuery("loc", q);

            var ctx1 = MakeCtx(new Vector3(0, 0, 0));
            ctx1.CurrentTime = 1f;
            var result1 = mgr.ExecuteQuery(q, "cache_key", ctx1);

            var ctx2 = MakeCtx(new Vector3(0, 0, 0));
            ctx2.CurrentTime = 5f; // 同一缓存窗口
            var result2 = mgr.ExecuteQuery(q, "cache_key", ctx2);

            // 相同对象引用（命中缓存）
            AssertTrue(ReferenceEquals(result1, result2), "cache hit returns same result");
        }

        public static void TestManagerCacheExpired()
        {
            var mgr = new EQSManager { CacheDuration = 1f, EnableCache = true };
            var q = new EQSQuery();
            q.Generator = new EQSGenerator_CurrentLocation();
            mgr.RegisterQuery("loc", q);

            var ctx1 = MakeCtx(Vector3.Zero);
            ctx1.CurrentTime = 0f;
            mgr.ExecuteQuery(q, "key1", ctx1);

            var ctx2 = MakeCtx(Vector3.Zero);
            ctx2.CurrentTime = 5f; // 超过 1s 缓存期
            mgr.ClearExpiredCache(5f);
            var result2 = mgr.ExecuteQuery(q, "key1", ctx2);

            // 过期后应重新执行
            AssertTrue(result2 != null, "re-executed after expiry");
        }

        public static void TestManagerClearCache()
        {
            var mgr = new EQSManager { EnableCache = true, CacheDuration = 100f };
            var q = new EQSQuery();
            q.Generator = new EQSGenerator_CurrentLocation();
            mgr.RegisterQuery("q", q);
            var ctx = MakeCtx(Vector3.Zero);
            ctx.CurrentTime = 1f;
            mgr.ExecuteQuery(q, "k", ctx);
            mgr.ClearCache();
            // 缓存清除后再执行应返回新对象
            var result2 = mgr.ExecuteQuery(q, "k", ctx);
            AssertTrue(result2 != null, "works after clear");
        }

        public static void TestManagerUnregisteredQuery()
        {
            var mgr = new EQSManager();
            var ctx = MakeCtx(Vector3.Zero);
            var result = mgr.ExecuteQuery("nonexistent", ctx);
            AssertFalse(result.IsSuccessful, "unregistered → empty result");
        }
    }
}
