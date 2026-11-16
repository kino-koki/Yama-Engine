// MicroClusterVisibilityTests.cs — MicroCluster 可见性系统 (视锥剔除 + LOD 选择 + 流式加载 + 帧生命周期) 验证套件
// 覆盖:
//   * 视锥剔除: 视锥内聚类可见, 视锥外/相机背后聚类被剔除
//   * LOD 选择: 远距离小子聚类回退父聚类, 近距离选自身
//   * 帧生命周期: BeginFrame 重置上一帧可见性 / 递增 FrameIndex
//   * PopulateCullResult: 填充正确聚类 ID
//   * 流式加载: HandleStreamingRequest 卸载后即使视锥内也不可见
//   * 聚类注册/移除/清空: 自动分配 ID / RemoveCluster / ClearClusters
//   * 端到端剔除管线: 注册多聚类 → 剔除 → 验证可见集 + 剔除计数
// 全部 CPU 级确定性, 不依赖真实 RHI/GPU (空设备容忍)。
using System;
using System.Numerics;
using YamaEngine.MicroCluster;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>MicroCluster 可见性系统验证套件（对齐 UE4.7 Nanite/MicroCluster 可见性缓冲）。</summary>
public sealed class MicroClusterVisibilityTests
{
    // 相机在 (0,0,10) 朝 -Z 看, FOV 90° 透视
    private static readonly Matrix4x4 View =
        Matrix4x4.CreateLookAt(new Vector3(0, 0, 10), new Vector3(0, 0, 0), Vector3.UnitY);
    private static readonly Matrix4x4 Proj =
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 1f, 0.1f, 1000f);

    private static ViewFrustum MakeFrustum() => new()
    {
        Position = new Vector3(0, 0, 10),
        Direction = new Vector3(0, 0, -1),
        Near = 0.1f,
        Far = 1000f,
        VerticalFov = MathF.PI * 0.5f
    };

    private static MicroClusterCluster MakeCluster(int id, Vector3 center, float size)
    {
        var half = new Vector3(size * 0.5f);
        return new MicroClusterCluster
        {
            ClusterId = id,
            BoundsMin = center - half,
            BoundsMax = center + half
        };
    }

    /// <summary>视锥剔除: 视锥内聚类可见, 视锥外/背后聚类被剔除。</summary>
    public static void TestFrustumCullingSelectsVisibleClusters()
    {
        var vis = new MicroClusterVisibility(null);
        vis.RegisterCluster(MakeCluster(1, new Vector3(0, 0, 0), 1f));      // 前方 → 可见
        vis.RegisterCluster(MakeCluster(2, new Vector3(100, 0, 0), 1f));    // 侧面 → 剔除
        vis.RegisterCluster(MakeCluster(3, new Vector3(0, 0, 50), 1f));     // 背后 → 剔除

        vis.UpdateVisibility(View, Proj, MakeFrustum());

        AssertTrue(vis.IsClusterVisible(1), "视锥内聚类应可见。");
        AssertTrue(!vis.IsClusterVisible(2), "视锥外侧聚类应被剔除。");
        AssertTrue(!vis.IsClusterVisible(3), "相机背后聚类应被剔除。");
        AssertEqual(2, vis.FrustumCulledCount, "应剔除 2 个聚类。");
        AssertEqual(1, vis.VisibleClusterCount, "应恰有 1 个可见聚类。");
    }

    /// <summary>LOD 选择: 远距离小子聚类回退父聚类, 近距离选自身。</summary>
    public static void TestLodSelectsParentWhenProjectedSmall()
    {
        var vis = new MicroClusterVisibility(null);

        // 大父聚类 id=1, 小子聚类 id=2 (ParentCluster=parent)
        var parent = MakeCluster(1, new Vector3(0, 0, 0), 20f);
        var child = MakeCluster(2, new Vector3(0, 0, 0), 0.2f);
        child.ParentCluster = parent;
        vis.RegisterCluster(parent);
        vis.RegisterCluster(child);

        // 近距离: 相机在 (0,0,10) → child 投影 ~18px > 4 → 选自身 (id 2)
        vis.UpdateVisibility(View, Proj, MakeFrustum());
        AssertTrue(vis.IsClusterVisible(2), "近距离小子聚类应选自身。");
        AssertTrue(vis.IsClusterVisible(1), "大父聚类也应可见。");

        // 远距离: 相机移到 (0,0,1000) → child 投影 ~0.18px < 4 → 回退父聚类 (id 1)
        var farView = Matrix4x4.CreateLookAt(new Vector3(0, 0, 1000), new Vector3(0, 0, 0), Vector3.UnitY);
        var farFrustum = MakeFrustum();
        farFrustum.Position = new Vector3(0, 0, 1000);
        vis.UpdateVisibility(farView, Proj, farFrustum);

        AssertTrue(vis.IsClusterVisible(1), "远距离父聚类应可见。");
        AssertTrue(!vis.IsClusterVisible(2), "远距离小子聚类应回退父聚类 (自身不入选)。");
    }

    /// <summary>帧生命周期: BeginFrame 清空上一帧可见性并递增 FrameIndex。</summary>
    public static void TestBeginFrameResetsState()
    {
        var vis = new MicroClusterVisibility(null);
        vis.RegisterCluster(MakeCluster(1, new Vector3(0, 0, 0), 1f));
        vis.UpdateVisibility(View, Proj, MakeFrustum());
        AssertEqual(1, vis.VisibleClusterCount, "前置: 应有一帧可见聚类。");

        int frameBefore = vis.FrameIndex;
        vis.BeginFrame();

        AssertEqual(frameBefore + 1, vis.FrameIndex, "BeginFrame 应递增 FrameIndex。");
        AssertEqual(0, vis.VisibleClusterCount, "BeginFrame 应清空上一帧可见性。");
        AssertEqual(0, vis.FrustumCulledCount, "BeginFrame 应清空剔除计数。");
    }

    /// <summary>PopulateCullResult: 填入正确的聚类 ID。</summary>
    public static void TestPopulateCullResultFillsIds()
    {
        var vis = new MicroClusterVisibility(null);
        vis.RegisterCluster(MakeCluster(10, new Vector3(0, 0, 0), 1f));    // 可见
        vis.RegisterCluster(MakeCluster(11, new Vector3(200, 0, 0), 1f));  // 剔除
        vis.UpdateVisibility(View, Proj, MakeFrustum());

        var result = new SceneCullResult();
        vis.PopulateCullResult(result);

        AssertEqual(1, result.Count, "剔除结果应含 1 个图元。");
        AssertEqual(10u, result.VisiblePrimitives[0], "剔除结果应为聚类 id=10。");
    }

    /// <summary>流式加载: HandleStreamingRequest 卸载后即使视锥内也不可见。</summary>
    public static void TestStreamingOutMakesClusterInvisible()
    {
        var vis = new MicroClusterVisibility(null);
        vis.RegisterCluster(MakeCluster(20, new Vector3(0, 0, 0), 1f));
        vis.UpdateVisibility(View, Proj, MakeFrustum());
        AssertTrue(vis.IsClusterVisible(20), "前置: 流式加载聚类应可见。");

        // 卸载聚类 (以 ClusterOffset 寻址聚类 ID)
        vis.HandleStreamingRequest(new SceneStreamingRequest { ClusterOffset = 20, ShouldLoad = false });
        vis.UpdateVisibility(View, Proj, MakeFrustum());

        AssertTrue(!vis.IsClusterVisible(20), "流式卸载的聚类即使视锥内也不可见。");
        AssertEqual(0, vis.VisibleClusterCount, "卸载后应无可见聚类。");

        // 重新加载
        vis.HandleStreamingRequest(new SceneStreamingRequest { ClusterOffset = 20, ShouldLoad = true });
        vis.UpdateVisibility(View, Proj, MakeFrustum());
        AssertTrue(vis.IsClusterVisible(20), "重新加载后应恢复可见。");
    }

    /// <summary>聚类注册/移除/清空: 自动分配 ID / RemoveCluster / ClearClusters。</summary>
    public static void TestRegisterRemoveClearClusters()
    {
        var vis = new MicroClusterVisibility(null);

        // 未分配 ID (0) 的聚类应自动分配唯一 ID
        vis.RegisterCluster(MakeCluster(0, new Vector3(0, 0, 0), 1f));
        AssertEqual(1, vis.TotalClusterCount, "应注册 1 个聚类。");
        var autoCluster = vis.GetCluster(1);
        AssertTrue(autoCluster != null, "自动分配的聚类应可通过 ID 查询。");

        // 移除
        vis.RegisterCluster(MakeCluster(5, new Vector3(5, 0, 0), 1f));
        AssertEqual(2, vis.TotalClusterCount, "应注册 2 个聚类。");
        AssertTrue(vis.RemoveCluster(5), "RemoveCluster 应成功。");
        AssertEqual(1, vis.TotalClusterCount, "移除后应剩 1 个聚类。");
        AssertTrue(!vis.RemoveCluster(5), "再次移除应失败。");

        // 清空
        vis.ClearClusters();
        AssertEqual(0, vis.TotalClusterCount, "清空后应无聚类。");
    }

    /// <summary>端到端剔除管线: 注册多聚类 → 剔除 → 验证可见集 + 剔除计数。</summary>
    public static void TestEndToEndCullPipeline()
    {
        var vis = new MicroClusterVisibility(null);
        vis.RegisterCluster(MakeCluster(1, new Vector3(0, 0, 0), 1f));      // 可见
        vis.RegisterCluster(MakeCluster(2, new Vector3(-1, 0, 0), 1f));     // 可见
        vis.RegisterCluster(MakeCluster(3, new Vector3(0, 20, 0), 1f));     // 上侧 → 剔除 (63° > 45° 半 FOV)
        vis.RegisterCluster(MakeCluster(4, new Vector3(0, 0, -30), 1f));    // 前方远处 → 可见
        vis.RegisterCluster(MakeCluster(5, new Vector3(500, 0, 0), 1f));    // 侧面 → 剔除

        vis.UpdateVisibility(View, Proj, MakeFrustum());

        AssertEqual(3, vis.VisibleClusterCount, "应恰有 3 个可见聚类。");
        AssertEqual(2, vis.FrustumCulledCount, "应剔除 2 个聚类。");
        AssertTrue(vis.IsClusterVisible(1), "聚类 1 应可见。");
        AssertTrue(vis.IsClusterVisible(2), "聚类 2 应可见。");
        AssertTrue(!vis.IsClusterVisible(3), "上侧聚类 3 应剔除。");
        AssertTrue(vis.IsClusterVisible(4), "前方聚类 4 应可见。");
        AssertTrue(!vis.IsClusterVisible(5), "侧面聚类 5 应剔除。");

        // 端到端: 剔除结果可被消费
        var result = new SceneCullResult();
        vis.PopulateCullResult(result);
        AssertEqual(3, result.Count, "剔除结果应含 3 个图元。");
    }
}
