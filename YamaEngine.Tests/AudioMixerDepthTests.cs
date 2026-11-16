// AudioMixerDepthTests.cs — Unity5 AudioMixer 深度验证（snapshots / ducking / spatializer）
// 覆盖：
//   * AudioBus 级联有效音量（GetEffectiveVolume 含父链）
//   * MasterBus 最终输出音量（Device/System/Volume 三者相乘）
//   * MixerSnapshot Save/Load 瞬间切换总线参数
//   * Snapshot Linear 过渡（t 中间态总线音量插值）
//   * AudioDucker Attacking/Sustaining/Releasing 状态机
//   * MWAudio3DSpatializer 距离衰减 / 立体声 pan / 多普勒 / 传播延迟
// 全部 CPU 级确定性，不依赖设备播放。
using System;
using System.Numerics;
using YamaEngine.Audio;
using YamaEngine.Audio.Middleware;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class AudioMixerDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}）");

    // ───────────── AudioBus 级联音量 ─────────────
    /// <summary>父子总线级联 GetEffectiveVolume 相乘；Mute 置零。</summary>
    public static void TestBusEffectiveVolumeCascade()
    {
        var master = new AudioBus { Name = "M", Volume = 0.8f };
        var music = master.AddChildBus("Music");
        music.Volume = 0.5f;

        Near(music.GetEffectiveVolume(), 0.4f, 0.0001f, "子总线有效音量 = 0.5 * 0.8");

        master.Muted = true;
        Near(music.GetEffectiveVolume(), 0f, 0.0001f, "父总线 Mute → 子总线有效音量为 0");
    }

    /// <summary>MasterBus 最终输出 = Device * System * 级联（默认 1*1*1）。</summary>
    public static void TestMasterBusFinalOutputVolume()
    {
        var master = new MasterBus();
        master.DeviceVolume = 0.5f;
        master.SystemVolume = 0.5f;
        master.Volume = 1f;
        Near(master.GetFinalOutputVolume(), 0.25f, 0.0001f, "最终输出 = 0.5*0.5*1");
    }

    // ───────────── Snapshot 瞬间切换 ─────────────
    /// <summary>SaveSnapshot 记录总线音量；LoadSnapshot 瞬间恢复。</summary>
    public static void TestSnapshotSaveLoadInstant()
    {
        var mixer = new AudioMixer();
        var music = mixer.RootBus.AddChildBus("Music");
        music.Volume = 1f;
        mixer.SaveSnapshot("Normal");

        music.Volume = 0.3f;
        AssertTrue(mixer.LoadSnapshot("Normal"), "LoadSnapshot 应返回 true。");
        Near(music.Volume, 1f, 0.0001f, "LoadSnapshot 瞬间恢复到保存时值");
    }

    /// <summary>LoadSnapshot 不存在的名字返回 false。</summary>
    public static void TestSnapshotLoadUnknownReturnsFalse()
    {
        var mixer = new AudioMixer();
        AssertTrue(!mixer.LoadSnapshot("nope"), "未知快照名应返回 false");
    }

    // ───────────── Snapshot 线性过渡 ─────────────
    /// <summary>TransitionToSnapshot(Linear) 中间态总线音量线性插值；结束后到达目标值。</summary>
    public static void TestSnapshotLinearTransition()
    {
        var mixer = new AudioMixer();
        var music = mixer.RootBus.AddChildBus("Music");
        music.Volume = 0f;

        // 把 music 音量改为 1 并保存为 "Loud"
        music.Volume = 1f;
        mixer.SaveSnapshot("Loud");

        // 回到 0，开始向 "Loud" 过渡 1 秒
        music.Volume = 0f;
        AssertTrue(mixer.TransitionToSnapshot("Loud", 1f, SnapshotTransitionType.Linear), "TransitionToSnapshot 应返回 true");

        // t=0.5 → 0 + (1-0)*0.5 = 0.5
        mixer.Update(0.5f);
        Near(music.Volume, 0.5f, 0.01f, "Linear 过渡 t=0.5 时音量应约 0.5");

        // t=1 → 到达目标值 1
        mixer.Update(0.5f);
        Near(music.Volume, 1f, 0.01f, "Linear 过渡 t=1 时音量应到达 1");
    }

    // ───────────── AudioDucking ─────────────
    /// <summary>音频避让状态机：Idle → Attacking → Sustaining → Releasing → Idle。</summary>
    public static void TestDuckingStateMachine()
    {
        var mixer = new AudioMixer();
        var music = mixer.RootBus.AddChildBus("Music");
        var voice = mixer.RootBus.AddChildBus("Voice");
        music.Volume = 1f;

        var ducker = mixer.AddDucker("Duck", music, voice,
            targetVolume: 0.2f, attack: 0.1f, hold: 0.1f, release: 0.1f);

        // Idle：无 voice → 不触发
        ducker.Update(0.016f);
        AssertTrue(!ducker.IsDucking, "无触发时应保持 Idle");

        // Voice 活跃 → Attacking
        voice.ActiveVoiceCount = 2;
        ducker.Update(0.016f);
        AssertTrue(ducker.State == DuckState.Attacking, "Voice 活跃后应进入 Attacking");

        // 攻击 0.1s 后 → Sustaining
        for (int i = 0; i < 10; i++) ducker.Update(0.01f);
        AssertTrue(ducker.State == DuckState.Sustaining, "攻击结束后应进入 Sustaining");
        Near(music.Volume, 0.2f, 0.05f, "Sustaining 时音乐音量应降到约 TargetVolume");

        // Hold 0.1s 结束后 → Releasing（多迭代 2 次覆盖浮点累积误差）
        for (int i = 0; i < 12; i++) ducker.Update(0.01f);
        AssertTrue(ducker.State == DuckState.Releasing, "Hold 结束后应进入 Releasing");

        // Release 前停掉 voice，避免 Idle 后立刻重新触发 Attacking
        voice.ActiveVoiceCount = 0;

        // Release 0.1s 后 → Idle，音量恢复（用稍大 dt 一次跨过浮点过零误差）
        for (int i = 0; i < 5; i++) ducker.Update(0.05f);
        AssertTrue(!ducker.IsDucking, $"释放结束后应回 Idle（实际 State={ducker.State}, Progress={ducker.CurrentProgress}, MusicVol={music.Volume}）");
        Near(music.Volume, 1f, 0.05f, "释放后音乐音量应恢复到原值 1");
    }

    // ───────────── 3D Spatializer ─────────────
    /// <summary>距离衰减：源在 MinDistance 内不衰减，超出 MaxDistance 归零。</summary>
    public static void TestSpatializerDistanceAttenuation()
    {
        var listener = new MWAudioListener { Position = Vector3.Zero };
        var near = new MWAudio3DSource { Position = new Vector3(0.5f, 0, 0), MinDistance = 1f, MaxDistance = 100f };
        var far = new MWAudio3DSource { Position = new Vector3(200f, 0, 0), MinDistance = 1f, MaxDistance = 100f };
        var sp = new MWAudio3DSpatializer();

        var rn = sp.Process(near, listener, 0.016f);
        Near(rn.FinalVolume, 1f, 0.001f, "MinDistance 内不衰减，FinalVolume=1");

        var rf = sp.Process(far, listener, 0.016f);
        Near(rf.FinalVolume, 0f, 0.001f, "超出 MaxDistance FinalVolume=0");
    }

    /// <summary>立体声 pan：源在正右方 → 右耳增益 > 左耳；正左方反之。</summary>
    public static void TestSpatializerStereoPan()
    {
        var listener = new MWAudioListener { Position = Vector3.Zero };
        var right = new MWAudio3DSource { Position = new Vector3(10f, 0, 0), MinDistance = 1f, MaxDistance = 100f };
        var sp = new MWAudio3DSpatializer();
        var rr = sp.Process(right, listener, 0.016f);
        AssertTrue(rr.RightGain > rr.LeftGain, "源在正右方时右耳增益应大于左耳");
    }

    /// <summary>多普勒：声源朝向听者移动 → 音调升高 (>1)；远离 → 降低 (<1)。</summary>
    public static void TestSpatializerDoppler()
    {
        var listener = new MWAudioListener { Position = Vector3.Zero };
        var src = new MWAudio3DSource
        {
            Position = new Vector3(10f, 0, 0),
            Velocity = new Vector3(-10f, 0, 0), // 朝向听者
            MinDistance = 1f, MaxDistance = 100f
        };
        var sp = new MWAudio3DSpatializer();
        var r1 = sp.Process(src, listener, 0.016f);
        AssertTrue(r1.DopplerPitch > 1f, "声源朝向听者 DopplerPitch 应 > 1");

        src.Velocity = new Vector3(10f, 0, 0); // 远离
        var r2 = sp.Process(src, listener, 0.016f);
        AssertTrue(r2.DopplerPitch < 1f, "声源远离 DopplerPitch 应 < 1");
    }

    /// <summary>传播延迟 = distance / SpeedOfSound。</summary>
    public static void TestSpatializerPropagationDelay()
    {
        var listener = new MWAudioListener { Position = Vector3.Zero };
        var src = new MWAudio3DSource { Position = new Vector3(343f, 0, 0), MinDistance = 1f, MaxDistance = 1000f };
        var sp = new MWAudio3DSpatializer { EnablePropagationDelay = true, SpeedOfSound = 343f };
        var r = sp.Process(src, listener, 0.016f);
        Near(r.PropagationDelay, 1f, 0.01f, "343m / 343(m/s) = 1s");
    }
}
