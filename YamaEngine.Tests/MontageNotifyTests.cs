// MontageNotifyTests.cs — 蒙太奇通知系统 + 分层闭环验证（UE5.8 深度级 任务D）
// 覆盖：
//   * AnimNotify 瞬时通知在 TriggerTime 触发且仅一次
//   * AnimNotifyState 持续通知进入区间 Begin / 离开 End
//   * MontageNotifyTrack 多轨道并行独立触发
//   * MontageLayerStack 多 Slot 叠加（蒙版覆盖 + 权重合成）
//   * MontageLayerStack 添加/移除/设置权重
//   * AnimNotifyLog 事件日志记录 Fire/Begin/End
// 全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using System.Numerics;
using YamaEngine.Animation;
using YamaEngine.Animation.Montage;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>蒙太奇通知系统 + 分层验证套件。</summary>
public sealed class MontageNotifyTests
{
    /// <summary>瞬时通知在 TriggerTime 触发一次且不重复。</summary>
    public static void TestAnimNotifyFiresOnceAtTime()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Notify");
        var montage = MakeMontage("M1", 2f);
        var instance = new MontageInstance(montage, skeleton);

        var notify = new CountingNotify { Name = "Impact", TriggerTime = 0.5f };
        var track = new MontageNotifyTrack { TrackName = "FX" };
        track.AddNotify(notify);
        var runtime = new MontageNotifyRuntime(instance, new[] { track });

        instance.StartBlendIn(0f);
        // 推进到 1.0s
        for (int i = 0; i < 4; i++) { instance.Update(0.25f); runtime.Tick(0.25f); }
        AssertTrue(notify.FireCount == 1, "通知应在 TriggerTime(0.5s) 触发一次。");

        // 继续推进到 2.0s，不应重复
        for (int i = 0; i < 4; i++) { instance.Update(0.25f); runtime.Tick(0.25f); }
        AssertTrue(notify.FireCount == 1, "通知不应重复触发。");
    }

    /// <summary>持续通知进入区间 Begin、离开 End。</summary>
    public static void TestAnimNotifyStateBeginEnd()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Notify");
        var montage = MakeMontage("M2", 2f);
        var instance = new MontageInstance(montage, skeleton);

        var state = new CountingState { Name = "Stagger", StartTime = 0.3f, EndTime = 1.0f };
        var track = new MontageNotifyTrack { TrackName = "State" };
        track.AddState(state);
        var runtime = new MontageNotifyRuntime(instance, new[] { track });

        instance.StartBlendIn(0f);
        // 推进到 0.5s（进入区间）
        for (int i = 0; i < 2; i++) { instance.Update(0.25f); runtime.Tick(0.25f); }
        AssertTrue(state.BeginCount == 1, "进入 [0.3,1.0) 应触发 Begin。");
        AssertTrue(state.EndCount == 0, "仍在区间内不应触发 End。");

        // 推进到 2.0s（离开区间）
        for (int i = 0; i < 6; i++) { instance.Update(0.25f); runtime.Tick(0.25f); }
        AssertTrue(state.EndCount == 1, "离开区间应触发 End。");
        AssertTrue(state.BeginCount == 1, "Begin 不应重复。");
    }

    /// <summary>多轨道并行，各自通知独立触发。</summary>
    public static void TestMontageNotifyTrackMultipleTracks()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Notify");
        var montage = MakeMontage("M3", 2f);
        var instance = new MontageInstance(montage, skeleton);

        var nA = new CountingNotify { Name = "A", TriggerTime = 0.5f };
        var nB = new CountingNotify { Name = "B", TriggerTime = 0.75f };
        var tA = new MontageNotifyTrack { TrackName = "TrackA" };
        tA.AddNotify(nA);
        var tB = new MontageNotifyTrack { TrackName = "TrackB" };
        tB.AddNotify(nB);
        var runtime = new MontageNotifyRuntime(instance, new[] { tA, tB });

        instance.StartBlendIn(0f);
        for (int i = 0; i < 4; i++) { instance.Update(0.25f); runtime.Tick(0.25f); }
        AssertTrue(nA.FireCount == 1 && nB.FireCount == 1,
            "两条轨道各自的通知应在各自 TriggerTime 触发。");
    }

    /// <summary>蒙太奇分层：上层蒙版覆盖 + 权重合成。</summary>
    public static void TestMontageLayerBlendWeights()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Layer");
        var clip0 = MakeClip("Low", 0f);
        var clip1 = MakeClip("High", 2f);
        var montage0 = MakeMontageWithClip("M0", 2f, clip0);
        var montage1 = MakeMontageWithClip("M1", 2f, clip1);
        var inst0 = new MontageInstance(montage0, skeleton);
        var inst1 = new MontageInstance(montage1, skeleton);
        inst0.StartBlendIn(0f);
        inst1.StartBlendIn(0f);
        inst0.Update(0.1f);
        inst1.Update(0.1f);

        var stack = new MontageLayerStack(skeleton);
        // 底层 Body：clip0，无蒙版，权重 1.0
        var body = stack.AddLayer("Body", inst0, 1f);
        // 上层 Upper：clip1，蒙版仅 Hips，权重 1.0 → 覆盖 Hips
        var upper = stack.AddLayer("Upper", inst1, 1f);
        upper.Mask = new HashSet<string> { "Hips" };

        var pose = stack.SampleBlendedPose();
        int hips = skeleton.FindBone("Hips");
        AssertTrue(MathF.Abs(pose.Translations[hips].Z - 2f) < 0.05f,
            "上层蒙版含 Hips 时应覆盖为 clip1 的 Hips 平移 (Z=2)。");

        // 上层蒙版不含 Hips（改为 Head）→ Hips 保持底层 clip0 (Z=0)
        upper.Mask = new HashSet<string> { "Head" };
        var pose2 = stack.SampleBlendedPose();
        AssertTrue(MathF.Abs(pose2.Translations[hips].Z - 0f) < 0.05f,
            "上层蒙版不含 Hips 时，Hips 应保持底层 clip0 (Z=0)。");
    }

    /// <summary>蒙太奇分层：添加/移除/设置权重/获取层。</summary>
    public static void TestMontageLayerAddRemove()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Layer");
        var montage = MakeMontage("M5", 2f);
        var inst = new MontageInstance(montage, skeleton);
        var stack = new MontageLayerStack(skeleton);

        var layer = stack.AddLayer("SlotA", inst, 0.5f);
        AssertTrue(stack.GetLayer("SlotA") == layer, "添加后可获取层。");
        AssertEqual(1, stack.Layers.Count, "应有 1 层。");

        stack.SetLayerWeight("SlotA", 0.8f);
        AssertTrue(MathF.Abs(stack.GetLayer("SlotA")!.Weight - 0.8f) < 0.001f, "设置权重应生效。");

        AssertTrue(stack.RemoveLayer("SlotA"), "移除应返回 true。");
        AssertEqual(0, stack.Layers.Count, "移除后应为 0 层。");
        AssertTrue(stack.GetLayer("SlotA") == null, "移除后获取应为 null。");
    }

    /// <summary>通知事件日志记录 Fire/Begin/End。</summary>
    public static void TestNotifyLogCapturesEvents()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Notify");
        var montage = MakeMontage("M6", 2f);
        var instance = new MontageInstance(montage, skeleton);

        var notify = new CountingNotify { Name = "Impact", TriggerTime = 0.5f };
        var state = new CountingState { Name = "Stagger", StartTime = 0.3f, EndTime = 1.0f };
        var track = new MontageNotifyTrack { TrackName = "FX" };
        track.AddNotify(notify);
        track.AddState(state);
        var runtime = new MontageNotifyRuntime(instance, new[] { track });

        instance.StartBlendIn(0f);
        for (int i = 0; i < 8; i++) { instance.Update(0.25f); runtime.Tick(0.25f); }

        bool hasNotify = runtime.Log.Events.Contains("Notify:FX:Impact");
        bool hasBegin = runtime.Log.Events.Contains("StateBegin:FX:Stagger");
        bool hasEnd = runtime.Log.Events.Contains("StateEnd:FX:Stagger");
        AssertTrue(hasNotify, "日志应记录瞬时通知 Fire。");
        AssertTrue(hasBegin, "日志应记录状态通知 Begin。");
        AssertTrue(hasEnd, "日志应记录状态通知 End。");
    }

    // ── 辅助 ──────────────────────────────────────────────

    private sealed class CountingNotify : YamaEngine.Animation.Montage.AnimNotify
    {
        public int FireCount;
        public override void Fire(YamaEngine.Animation.Montage.AnimNotifyContext ctx) { FireCount++; }
    }

    private sealed class CountingState : YamaEngine.Animation.Montage.AnimNotifyState
    {
        public int BeginCount;
        public int EndCount;
        public override void Begin(YamaEngine.Animation.Montage.AnimNotifyContext ctx) { BeginCount++; }
        public override void End(YamaEngine.Animation.Montage.AnimNotifyContext ctx) { EndCount++; }
    }

    private static YamaEngine.Animation.AnimMontage MakeMontage(string name, float length)
    {
        var montage = new YamaEngine.Animation.AnimMontage { Name = name, LengthSeconds = length };
        var section = montage.AddSection("Main", length);
        var clip = new YamaEngine.Animation.AnimationClip { Name = name + "_clip", LengthSeconds = length, Looping = true };
        clip.AddTranslationKey("Hips", 0f, Vector3.Zero);
        clip.AddTranslationKey("Hips", length, Vector3.Zero);
        var seg = montage.AddSegment(clip, 0f, length, "DefaultSlot");
        section.SegmentIndices.Add(montage.Segments.Count - 1);
        return montage;
    }

    private static YamaEngine.Animation.AnimMontage MakeMontageWithClip(string name, float length, YamaEngine.Animation.AnimationClip clip)
    {
        var montage = new YamaEngine.Animation.AnimMontage { Name = name, LengthSeconds = length };
        var section = montage.AddSection("Main", length);
        var seg = montage.AddSegment(clip, 0f, length, "DefaultSlot");
        section.SegmentIndices.Add(montage.Segments.Count - 1);
        return montage;
    }

    private static YamaEngine.Animation.AnimationClip MakeClip(string name, float z)
    {
        var clip = new YamaEngine.Animation.AnimationClip { Name = name, LengthSeconds = 2f, Looping = true };
        clip.AddTranslationKey("Hips", 0f, new Vector3(0, 0, z));
        clip.AddTranslationKey("Hips", 2f, new Vector3(0, 0, z));
        return clip;
    }
}
