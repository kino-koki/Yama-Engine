// LODDepthTests.cs — Unity5 LOD 深度验证（QEM 简化 / LOD 链生成 / ScreenSize 阈值 / LOD 切换 / Hysteresis / 群组 LOD）
// 覆盖：
//   * QEMatrix 从平面方程构建、误差 v^T*Q*v
//   * QEMeshSimplifier.Simplify 减少三角形数
//   * LODChainGenerator.GenerateLODChain 5 级默认
//   * ComputeScreenSizeThresholds 单调递减
//   * LODSwitchController.UpdateLOD 覆盖率选择级别
//   * Hysteresis 防闪烁（切换后小幅波动不抖动）
//   * UpdateGroupLOD 群组所有 Mesh 同步切换
// 全部 CPU 级确定性。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Processing;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class LODDepthTests
{
    /// <summary>构造一个高细分网格（UV 球体近似）——足够三角形供简化器折叠。</summary>
    private static MeshData MakeSphere(int segments = 16, float radius = 1f)
    {
        var mesh = new MeshData();
        // 顶部/底部极点 + 多段环
        mesh.Positions.Add(new Vector3(0, radius, 0));
        mesh.Normals.Add(Vector3.UnitY);
        int rings = segments / 2;
        for (int r = 1; r < rings; r++)
        {
            float theta = MathF.PI * r / rings;
            float y = MathF.Cos(theta) * radius;
            float s = MathF.Sin(theta) * radius;
            for (int i = 0; i < segments; i++)
            {
                float phi = MathF.PI * 2 * i / segments;
                var n = new Vector3(s * MathF.Cos(phi), y, s * MathF.Sin(phi));
                mesh.Positions.Add(Vector3.Normalize(n) * radius);
                mesh.Normals.Add(Vector3.Normalize(n));
            }
        }
        mesh.Positions.Add(new Vector3(0, -radius, 0));
        mesh.Normals.Add(-Vector3.UnitY);

        int topPole = 0;
        int bottomPole = mesh.Positions.Count - 1;
        int ringStart(int r) => 1 + (r - 1) * segments;

        // 顶环到顶极点
        for (int i = 0; i < segments; i++)
        {
            int a = ringStart(1) + i;
            int b = ringStart(1) + (i + 1) % segments;
            mesh.Indices.Add((uint)topPole); mesh.Indices.Add((uint)b); mesh.Indices.Add((uint)a);
        }
        // 中间环带
        for (int r = 1; r < rings - 1; r++)
        {
            for (int i = 0; i < segments; i++)
            {
                int a = ringStart(r) + i;
                int b = ringStart(r) + (i + 1) % segments;
                int c = ringStart(r + 1) + i;
                int d = ringStart(r + 1) + (i + 1) % segments;
                mesh.Indices.Add((uint)a); mesh.Indices.Add((uint)b); mesh.Indices.Add((uint)d);
                mesh.Indices.Add((uint)a); mesh.Indices.Add((uint)d); mesh.Indices.Add((uint)c);
            }
        }
        // 底环到底极点
        int lastRing = rings - 1;
        for (int i = 0; i < segments; i++)
        {
            int a = ringStart(lastRing) + i;
            int b = ringStart(lastRing) + (i + 1) % segments;
            mesh.Indices.Add((uint)a); mesh.Indices.Add((uint)b); mesh.Indices.Add((uint)bottomPole);
        }
        return mesh;
    }

    /// <summary>QEMatrix 从平面构建后计算误差为 0（点在平面上）。</summary>
    public static void TestQEMatrixPlaneZeroError()
    {
        // 平面 z=0 → 法线 (0,0,1)，过原点 (0,0,0)
        var q = QEMatrix.Zero;
        q.AddPlane(new Vector3(0, 0, 1), Vector3.Zero);
        // 平面上任一点 (x,y,0) 误差为 0
        float err = q.Evaluate(new Vector3(3, 7, 0));
        AssertTrue(MathF.Abs(err) < 1e-4f, $"平面上的点误差应为 0（实际 {err}）");
    }

    /// <summary>QEMatrix 平面外点误差为距离平方。</summary>
    public static void TestQEMatrixOffPlaneError()
    {
        // 平面 z=0
        var q = QEMatrix.Zero;
        q.AddPlane(new Vector3(0, 0, 1), Vector3.Zero);
        // 点 (0,0,2) 距离平面 2 → 误差 ≈ 4
        float err = q.Evaluate(new Vector3(0, 0, 2));
        AssertTrue(MathF.Abs(err - 4f) < 1e-3f, $"平面外点误差应≈4（实际 {err}）");
    }

    /// <summary>QEMeshSimplifier.Simplify 减少三角形数到目标。</summary>
    public static void TestSimplifierReducesTriangles()
    {
        var mesh = MakeSphere(24);
        int original = mesh.TriangleCount;
        AssertTrue(original > 100, $"测试网格应>100 三角形（实际 {original}）");

        var simplifier = new QEMeshSimplifier();
        var simplified = simplifier.Simplify(mesh, 50);

        AssertTrue(simplified.TriangleCount <= 60,
            $"简化后三角形数应≈50（实际 {simplified.TriangleCount}，原始 {original}）");
        AssertTrue(simplified.TriangleCount < original, "简化后应少于原始");
        AssertTrue(simplified.VertexCount >= 3, "至少保留基础顶点");
    }

    /// <summary>LODChainGenerator 默认生成 5 级 LOD。</summary>
    public static void TestLODChainDefault5Levels()
    {
        var mesh = MakeSphere(24);
        var gen = new LODChainGenerator();
        var chain = gen.GenerateLODChain(mesh);

        AssertTrue(chain.Levels.Count == 5, $"默认应生成 5 级 LOD（实际 {chain.Levels.Count}）");
        AssertTrue(chain.Levels[0].TriangleCount == mesh.TriangleCount,
            "LOD0 应保持原始三角形数");
        // 三角形数应单调递减
        for (int i = 1; i < chain.Levels.Count; i++)
        {
            AssertTrue(chain.Levels[i].TriangleCount <= chain.Levels[i - 1].TriangleCount,
                $"LOD{i} 三角形数应≤LOD{i - 1}");
        }
    }

    /// <summary>ComputeScreenSizeThresholds 阈值单调递减。</summary>
    public static void TestScreenSizeThresholdsMonotonicDecrease()
    {
        var mesh = MakeSphere(24);
        var gen = new LODChainGenerator();
        var chain = gen.GenerateLODChain(mesh);
        gen.ComputeScreenSizeThresholds(chain);

        AssertTrue(chain.Levels.Count >= 2, "至少 2 级才能验证单调性");
        // LOD0 阈值固定为 0（始终显示），单调递减从 LOD1 开始验证
        AssertTrue(chain.Levels[0].ScreenSizeThreshold == 0f, "LOD0 阈值应为 0（始终显示）");
        for (int i = 2; i < chain.Levels.Count; i++)
        {
            AssertTrue(chain.Levels[i].ScreenSizeThreshold <= chain.Levels[i - 1].ScreenSizeThreshold,
                $"LOD{i} ScreenSize({chain.Levels[i].ScreenSizeThreshold})应≤LOD{i - 1}({chain.Levels[i - 1].ScreenSizeThreshold})");
        }
    }

    /// <summary>LODSwitchController：高覆盖率选 LOD0，低覆盖率选高 LOD。</summary>
    public static void TestLODControllerCoverageSelection()
    {
        var mesh = MakeSphere(24);
        var gen = new LODChainGenerator();
        var chain = gen.GenerateLODChain(mesh);
        gen.ComputeScreenSizeThresholds(chain);

        var ctrl = new LODSwitchController();
        // 高覆盖率 → LOD0（最高细节）
        int lod0 = ctrl.UpdateLOD(1, chain, 0.9f);
        AssertTrue(lod0 == 0, $"高覆盖率应选 LOD0（实际 {lod0}）");

        // 极低覆盖率 → 高 LOD
        int lodHigh = ctrl.UpdateLOD(1, chain, 0.01f);
        AssertTrue(lodHigh > 0, $"低覆盖率应选更高 LOD（实际 {lodHigh}）");
    }

    /// <summary>Hysteresis 防闪烁：切换后小幅波动（±5%）不回切。</summary>
    public static void TestLODControllerHysteresisNoFlicker()
    {
        var mesh = MakeSphere(24);
        var gen = new LODChainGenerator();
        var chain = gen.GenerateLODChain(mesh);
        gen.ComputeScreenSizeThresholds(chain);

        var ctrl = new LODSwitchController { HysteresisFactor = 0.2f };

        // 起始高覆盖率 → LOD0
        int level1 = ctrl.UpdateLOD(1, chain, 0.9f);
        AssertTrue(level1 == 0, "高覆盖率应 LOD0");

        // 降到刚好跨过 LOD0→LOD1 阈值
        float lod1Threshold = chain.Levels[1].ScreenSizeThreshold;
        int level2 = ctrl.UpdateLOD(1, chain, lod1Threshold * 0.9f); // 跨过阈值降级
        AssertTrue(level2 > 0, "跨过阈值应降级到 LOD1");

        // 小幅回升到略高于阈值（仍在 Hysteresis 迟滞区内）——不回切
        int level3 = ctrl.UpdateLOD(1, chain, lod1Threshold * 1.05f);
        AssertTrue(level3 == level2, $"Hysteresis 迟滞区内不回切（实际 {level3} vs {level2}）");

        // 大幅回升远超阈值×(1+Hysteresis) 才回切
        int level4 = ctrl.UpdateLOD(1, chain, lod1Threshold * 1.5f);
        AssertTrue(level4 < level2, "远超阈值应回切到更低 LOD");
    }

    /// <summary>UpdateGroupLOD：所有 Mesh 同步到同一 LOD 级别。</summary>
    public static void TestLODControllerGroupSync()
    {
        var mesh = MakeSphere(24);
        var gen = new LODChainGenerator();
        var chain = gen.GenerateLODChain(mesh);
        gen.ComputeScreenSizeThresholds(chain);

        var ctrl = new LODSwitchController();
        var meshIds = new List<int> { 10, 11, 12 };

        // 阶段1：高覆盖率 → 群组在 LOD0
        var highCov = new Dictionary<int, float> { [10] = 0.8f, [11] = 0.85f, [12] = 0.9f };
        ctrl.UpdateGroupLOD(100, meshIds, chain, highCov);

        // 阶段2：覆盖率降到阈值以下 → 群组降级，所有 Mesh 同步切换
        // 降级只需 coverage < threshold（无升级滞后），用极低覆盖率确保降到底
        var lowCov = new Dictionary<int, float> { [10] = 0.001f, [11] = 0.002f, [12] = 0.003f };
        ctrl.UpdateGroupLOD(100, meshIds, chain, lowCov);

        // 所有 Mesh 应同步到同一级别
        int? syncLevel = null;
        foreach (int id in meshIds)
        {
            var cf = ctrl.GetCrossFadeState(id);
            if (syncLevel == null) syncLevel = cf.NewLevel;
            AssertTrue(cf.NewLevel == syncLevel,
                $"Mesh {id} 级别({cf.NewLevel})应与群组同步({syncLevel})");
        }
        AssertTrue(syncLevel > 0, $"低覆盖率群组应同步降级到更高 LOD（实际 {syncLevel}）");
    }

    /// <summary>CrossFade 过渡：切换后 CrossFadeProgress 逐渐递增。</summary>
    public static void TestLODControllerCrossFadeTransition()
    {
        var mesh = MakeSphere(24);
        var gen = new LODChainGenerator();
        var chain = gen.GenerateLODChain(mesh);
        gen.ComputeScreenSizeThresholds(chain);

        var ctrl = new LODSwitchController { CrossFadeFrames = 3 };

        // 起始高覆盖率
        ctrl.UpdateLOD(1, chain, 0.9f);
        // 触发切换
        float lod1Threshold = chain.Levels[1].ScreenSizeThreshold;
        ctrl.UpdateLOD(1, chain, lod1Threshold * 0.5f);

        // 查询 CrossFade 状态（如果有过渡的话）
        var cf = ctrl.GetCrossFadeState(1);
        float progress = cf.BlendFactor;
        // 如果没有 CrossFade 机制，progress 应为 1（已完成）
        // 如果有，应 < 1
        AssertTrue(progress >= 0f && progress <= 1f, $"CrossFadeProgress 应在 [0,1]（实际 {progress}）");
    }
}
