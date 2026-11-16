// EQSTests.cs — 环境查询系统 (Environment Query System, EQS) 验证套件
// 覆盖:
//   * EQSGenerator_ActorsInRadius + EQSTest_Distance 的候选生成与得分排序 (最近者胜)
//   * EQSGenerator_Grid 候选数量 (Rows x Columns)
//   * EQSQuery.MaxResults 截断 (此前声明未生效的 bug 已修复, 此处回归验证)
//   * EQSTest_Trace 依赖 RaycastCheck 回调的遮挡判定
//   * EQSManager 查询缓存 (缓存有效期命中/过期失效)
//   * AIController.RunEQSQuery 端到端 (世界 Actor → 生成 → 测试 → 最佳项)
// 全部 CPU 级确定性, 不依赖 GPU/场景/硬件。
using System;
using System.Numerics;
using YamaEngine.Gameplay;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>环境查询系统验证套件（对齐 UE EQS）。</summary>
public sealed class EQSTests
{
    /// <summary>ActorsInRadius 生成器 + Distance 测试应选出半径内最近的目标为最佳项。</summary>
    public static void TestEQSActorsInRadiusWithDistanceSorts()
    {
        var near = new YamaEngine.GameFramework.Actor { Name = "Near", Position = new Vector3(2f, 0f, 0f) };
        var far = new YamaEngine.GameFramework.Actor { Name = "Far", Position = new Vector3(10f, 0f, 0f) };
        var outside = new YamaEngine.GameFramework.Actor { Name = "Outside", Position = new Vector3(600f, 0f, 0f) };

        var query = new EQSQuery { QueryName = "FindNearestEnemy" };
        query.Generator = new EQSGenerator_ActorsInRadius { Radius = 500f };
        query.AddTest(new EQSTest_Distance { MinDistance = 0f, MaxDistance = 500f, PreferCloser = true });

        var ctx = new EQSContext { QuerierPosition = Vector3.Zero };
        ctx.WorldActors.Add(near);
        ctx.WorldActors.Add(far);
        ctx.WorldActors.Add(outside);

        var result = query.Execute(ctx);
        var best = result.BestItem;

        AssertTrue(result.IsSuccessful, "查询应成功 (半径内有候选)。");
        AssertTrue(best != null, "应返回最佳项。");
        AssertTrue(best!.AssociatedActor == near,
            $"最近目标 (2单位) 应为最佳项, 实际 {best.AssociatedActor?.Name}。");
        AssertTrue(result.Items.Count == 2,
            $"半径 500 内应只有 2 个候选 (排除 600 单位外), 实际 {result.Items.Count}。");

        // 得分单调性: 越近得分越高 (PreferCloser)
        float nearScore = result.Items[0].WeightedScore;
        float farScore = result.Items[1].WeightedScore;
        AssertTrue(nearScore > farScore,
            $"距离越近得分应越高, near={nearScore:F3} far={farScore:F3}。");
    }

    /// <summary>Grid 生成器应产生 Rows x Columns 个候选 (确定性计数)。</summary>
    public static void TestEQSGridGeneratorProducesExpectedCount()
    {
        var query = new EQSQuery { QueryName = "GridQuery" };
        query.Generator = new EQSGenerator_Grid { Rows = 4, Columns = 5, CellSize = 10f, GridSize = 40f };
        query.AddTest(new EQSTest_Distance { MinDistance = 0f, MaxDistance = 100f, PreferCloser = true });

        var ctx = new EQSContext { QuerierPosition = Vector3.Zero };
        var result = query.Execute(ctx);

        AssertEqual(20, result.Items.Count, "4x5 网格应生成 20 个候选。");
        AssertTrue(result.BestItem != null, "网格查询应返回最佳项。");
    }

    /// <summary>MaxResults 应截断结果集 (回归: 此前声明但从未应用)。</summary>
    public static void TestEQSMaxResultsTruncates()
    {
        var query = new EQSQuery { QueryName = "Top3", MaxResults = 3 };
        query.Generator = new EQSGenerator_Grid { Rows = 5, Columns = 5, CellSize = 10f, GridSize = 50f };
        query.AddTest(new EQSTest_Distance { MinDistance = 0f, MaxDistance = 100f, PreferCloser = true });

        var ctx = new EQSContext { QuerierPosition = Vector3.Zero };
        var result = query.Execute(ctx);

        AssertEqual(3, result.Items.Count, "MaxResults=3 应截断为 3 项。");
        AssertTrue(result.GetTopItems(3).Count == 3, "GetTopItems(3) 应返回 3 项。");

        // 全量网格为 25 项, 截断后不得保留更多
        var queryNoLimit = new EQSQuery { QueryName = "All25", MaxResults = 0 };
        queryNoLimit.Generator = new EQSGenerator_Grid { Rows = 5, Columns = 5, CellSize = 10f, GridSize = 50f };
        queryNoLimit.AddTest(new EQSTest_Distance { MinDistance = 0f, MaxDistance = 100f, PreferCloser = true });
        var fullResult = queryNoLimit.Execute(ctx);
        AssertEqual(25, fullResult.Items.Count, "MaxResults=0 (不限) 应保留全部 25 项。");
    }

    /// <summary>Trace 测试应依据 RaycastCheck 回调判定可见/遮挡得分。</summary>
    public static void TestEQSTestTraceUsesRaycast()
    {
        var query = new EQSQuery { QueryName = "LineOfSight" };
        query.Generator = new EQSGenerator_CurrentLocation();
        query.AddTest(new EQSTest_Trace { MaxTraceDistance = 100f, VisibilityScore = 1f, BlockedScore = 0f });

        var ctx = new EQSContext { QuerierPosition = Vector3.Zero, RaycastCheck = (a, b) => true };
        var blockedResult = query.Execute(ctx);
        var blockedBest = blockedResult.BestItem;
        AssertTrue(blockedBest != null, "Trace 查询应返回项。");
        AssertTrue(blockedBest!.WeightedScore < 0.5f,
            $"遮挡时应得低分(0), 实际 {blockedBest.WeightedScore:F3}。");

        ctx.RaycastCheck = (a, b) => false;
        var visibleResult = query.Execute(ctx);
        var visibleBest = visibleResult.BestItem;
        AssertTrue(visibleBest != null, "Trace 查询应返回项。");
        AssertTrue(visibleBest!.WeightedScore > 0.9f,
            $"可见时应得满分(1), 实际 {visibleBest.WeightedScore:F3}。");
    }

    /// <summary>EQSManager 缓存应命中有效期内结果, 过期后重新查询。</summary>
    public static void TestEQSManagerCaching()
    {
        var manager = EQSManager.Instance;
        manager.ClearCache();
        manager.CacheDuration = 1f;
        manager.EnableCache = true;

        var query = new EQSQuery { QueryName = "CachedQuery" };
        query.Generator = new EQSGenerator_CurrentLocation();
        query.AddTest(new EQSTest_Distance { MinDistance = 0f, MaxDistance = 500f, PreferCloser = true });

        var ctx = new EQSContext { QuerierPosition = new Vector3(1f, 0f, 0f), CurrentTime = 0f };

        var first = manager.ExecuteQuery(query, "cache-key-1", ctx);
        AssertTrue(first.IsSuccessful, "首次查询应成功。");

        // 同帧内应命中缓存 (对象引用相同)
        var cached = manager.ExecuteQuery(query, "cache-key-1", ctx);
        AssertTrue(ReferenceEquals(first, cached),
            "有效期内应命中缓存 (同一结果对象)。");

        // 越过有效期应重新查询 (新对象)
        var expired = manager.ExecuteQuery(query, "cache-key-1", new EQSContext
        {
            QuerierPosition = new Vector3(1f, 0f, 0f),
            CurrentTime = 5f
        });
        AssertTrue(!ReferenceEquals(first, expired),
            "超过缓存有效期应重新查询 (新结果对象)。");
    }

    // 专用目标类型: 确保 EQS 生成器不把查询者(AIController)自身当候选 (距离0)
    private sealed class EQSEnemy : YamaEngine.GameFramework.Actor { }

    /// <summary>AIController.RunEQSQuery 端到端: 世界 Actor → 生成 → 测试 → 最佳项。</summary>
    public static void TestAIControllerRunEQSQueryEndToEnd()
    {
        var world = new GameplayWorld();
        var pawn = new YamaEngine.GameFramework.Pawn();          // 位置 (0,0,0)
        var ai = new YamaEngine.AI.AIController();
        ai.Possess(pawn);

        var target = new EQSEnemy { Name = "Target", Position = new Vector3(2f, 0f, 0f) };
        var decoy = new EQSEnemy { Name = "Decoy", Position = new Vector3(9f, 0f, 0f) };

        // 仅匹配 EQSEnemy 类型, 排除查询者自身 (UE 惯例: AllActorsOfClass 指定目标类)
        ai.EQSQuery = new EQSQuery { QueryName = "BestTarget", MaxResults = 1 };
        ai.EQSQuery.Generator = new EQSGenerator_AllActorsOfClass { ActorType = typeof(EQSEnemy) };
        ai.EQSQuery.AddTest(new EQSTest_Distance { MinDistance = 0f, MaxDistance = 500f, PreferCloser = true });

        world.RegisterActor(ai);
        world.RegisterActor(target);
        world.RegisterActor(decoy);

        var result = ai.RunEQSQuery();
        var best = result.BestItem;

        AssertTrue(result.IsSuccessful, "RunEQSQuery 应成功。");
        AssertTrue(best != null, "应返回最佳项。");
        AssertTrue(best!.AssociatedActor == target,
            $"最近目标应被 EQS 选出, 实际 {best.AssociatedActor?.Name}。");
        AssertEqual(1, result.Items.Count, "MaxResults=1 应仅保留 1 项。");

        // 未设置模板时应返回空结果 (安全路径)
        var bare = new YamaEngine.AI.AIController();
        bare.Possess(new YamaEngine.GameFramework.Pawn());
        var empty = bare.RunEQSQuery();
        AssertTrue(!empty.IsSuccessful, "无 EQS 模板时应返回空结果。");
    }
}
