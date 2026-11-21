// UnityReflectionProbeDepthTests.cs — Unity5 Reflection Probe 深度验证
// 覆盖：ReflectionProbeMode 三态、ProbeBlendMode 混合、Probe 字段默认值、
//       ParallaxCorrection 参数、优先级语义。
using System;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityReflectionProbeDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    // ══════════════════════════════════════════════════════════════════
    //  一、枚举语义
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ReflectionProbeMode 三态：Baked/Realtime/Interval。</summary>
    public static void TestReflectionProbeMode()
    {
        AssertTrue(Enum.IsDefined(typeof(ReflectionProbeMode), ReflectionProbeMode.Baked), "Baked");
        AssertTrue(Enum.IsDefined(typeof(ReflectionProbeMode), ReflectionProbeMode.Realtime), "Realtime");
        AssertTrue(Enum.IsDefined(typeof(ReflectionProbeMode), ReflectionProbeMode.Interval), "Interval");
        AssertTrue((int)ReflectionProbeMode.Baked != (int)ReflectionProbeMode.Realtime, "Baked ≠ Realtime");
    }

    /// <summary>ProbeBlendMode 四态：Nearest/Trilinear/Weighted。</summary>
    public static void TestProbeBlendMode()
    {
        AssertTrue(Enum.IsDefined(typeof(ProbeBlendMode), ProbeBlendMode.Nearest), "Nearest");
        AssertTrue(Enum.IsDefined(typeof(ProbeBlendMode), ProbeBlendMode.Trilinear), "Trilinear");
        AssertTrue(Enum.IsDefined(typeof(ProbeBlendMode), ProbeBlendMode.Weighted), "Weighted");
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、ReflectionProbe 字段默认
    // ══════════════════════════════════════════════════════════════════

    /// <summary>默认字段：Baked 模式、Range 500、Resolution 128、Parallax 开启。</summary>
    public static void TestReflectionProbeDefaults()
    {
        var probe = new ReflectionProbe(null!);
        AssertEqual("ReflectionProbe", probe.Name, "默认名 ReflectionProbe");
        AssertEqual(ReflectionProbeMode.Baked, probe.Mode, "默认 Baked");
        Near(probe.Range, 500f, 0.001f, "默认范围 500");
        AssertEqual(128, probe.Resolution, "默认分辨率 128");
        Near(probe.UpdateInterval, 2f, 0.001f, "默认更新间隔 2s");
        AssertTrue(probe.EnableParallaxCorrection, "默认开启视差校正");
        AssertFalse(probe.Cubemap is not null, "默认未烘焙 Cubemap");
        AssertTrue(probe.Id != Guid.Empty, "自动生成唯一 ID");
    }

    /// <summary>视差校正：ParallaxHalfExtents 定义盒体大小。</summary>
    public static void TestReflectionProbeParallax()
    {
        var probe = new ReflectionProbe(null!)
        {
            EnableParallaxCorrection = true,
            ParallaxHalfExtents = new Vector3(10f, 5f, 10f),
            Position = new Vector3(0, 3, 0)
        };
        AssertTrue(probe.EnableParallaxCorrection, "视差校正开启");
        Near(probe.ParallaxHalfExtents.X, 10f, 0.001f, "盒半宽 X=10");
        Near(probe.ParallaxHalfExtents.Y, 5f, 0.001f, "盒半高 Y=5");
        Near(probe.Position.Y, 3f, 0.001f, "探针 Y=3");
    }

    /// <summary>Realtime 模式 + 短间隔刷新。</summary>
    public static void TestReflectionProbeRealtime()
    {
        var probe = new ReflectionProbe(null!)
        {
            Mode = ReflectionProbeMode.Realtime,
            UpdateInterval = 0.1f,
            Resolution = 256,
            Priority = 10f
        };
        AssertEqual(ReflectionProbeMode.Realtime, probe.Mode, "Realtime 模式");
        Near(probe.UpdateInterval, 0.1f, 0.001f, "0.1s 刷新间隔");
        AssertEqual(256, probe.Resolution, "Realtime 高分辨率 256");
        Near(probe.Priority, 10f, 0.001f, "优先级 10");
    }

    /// <summary>Interval 模式：定时刷新（介于 Baked 和 Realtime）。</summary>
    public static void TestReflectionProbeInterval()
    {
        var probe = new ReflectionProbe(null!)
        {
            Mode = ReflectionProbeMode.Interval,
            UpdateInterval = 5f
        };
        AssertEqual(ReflectionProbeMode.Interval, probe.Mode, "Interval 模式");
        Near(probe.UpdateInterval, 5f, 0.001f, "5s 间隔刷新");
        // Interval 不等于 Baked（Baked 一次不动）也不等于 Realtime（每帧）
        AssertTrue(probe.Mode != ReflectionProbeMode.Baked, "≠ Baked");
        AssertTrue(probe.Mode != ReflectionProbeMode.Realtime, "≠ Realtime");
    }

    /// <summary>多个探针：优先级决定混合权重顺序。</summary>
    public static void TestReflectionProbePriority()
    {
        var p1 = new ReflectionProbe(null!) { Priority = 1f };
        var p2 = new ReflectionProbe(null!) { Priority = 10f };
        var p3 = new ReflectionProbe(null!) { Priority = 5f };

        AssertTrue(p2.Priority > p3.Priority, "p2 > p3");
        AssertTrue(p3.Priority > p1.Priority, "p3 > p1");
        // BlendWeight 默认 1（全权）
        Near(p1.BlendWeight, 1f, 0.001f, "p1 权重 1");
        Near(p2.BlendWeight, 1f, 0.001f, "p2 权重 1");
    }
}
