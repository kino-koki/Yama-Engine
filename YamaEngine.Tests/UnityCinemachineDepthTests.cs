// UnityCinemachineDepthTests.cs — Unity5 Cinemachine 虚拟相机系统深度验证
// 覆盖：枚举（VirtualCameraState/BlendStyle/GroupComposeMode/PathEndBehavior/CameraNoiseType）、
//       CinemachineBlend（HardCut/Linear/EaseInOut/CustomCurve + Progress/Reset/IsComplete）、
//       VirtualCamera（默认字段/SetDirectState/SetState）、CameraShake（Start/Stop/Duration/Damping）、
//       CameraNoise（Evaluate/多层叠加）、ImpulseListener（ReceiveImpulse/Update）、
//       CinemachineBrain（AddCamera 优先级排序/RemoveCamera/Update 优先级切换/BlendTransition/Fallback/UserOverride/TriggerImpulse）、
//       FollowCamera/LookAtCamera/FreeLookRig/PathCamera/StateDrivenCamera/ClearShotCamera 子类型。
using System;
using System.Numerics;
using YamaEngine.Sequencer;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityCinemachineDepthTests
{
    private static void Near(float want, float got, float tol, string msg)
        => AssertTrue(MathF.Abs(want - got) <= tol, $"{msg}: want {want}, got {got}");

    // ════════════════════════════════════════════════════════════════
    //  一、枚举
    // ════════════════════════════════════════════════════════════════

    public static void TestEnumValues()
    {
        AssertEqual(4, Enum.GetValues<VirtualCameraState>().Length, "4 vcam states");
        AssertEqual(VirtualCameraState.Idle, (VirtualCameraState)0, "Idle=0");
        AssertEqual(VirtualCameraState.Live, (VirtualCameraState)1, "Live=1");
        AssertEqual(VirtualCameraState.Blending, (VirtualCameraState)2, "Blending=2");
        AssertEqual(VirtualCameraState.UserOverride, (VirtualCameraState)3, "UserOverride=3");

        AssertEqual(4, Enum.GetValues<CinemachineBlendStyle>().Length, "4 blend styles");
        AssertEqual(CinemachineBlendStyle.HardCut, (CinemachineBlendStyle)0, "HardCut=0");
        AssertEqual(CinemachineBlendStyle.CustomCurve, (CinemachineBlendStyle)3, "CustomCurve=3");

        AssertEqual(4, Enum.GetValues<GroupComposeMode>().Length, "4 compose modes");
        AssertEqual(GroupComposeMode.Group, (GroupComposeMode)0, "Group=0");

        AssertEqual(3, Enum.GetValues<PathEndBehavior>().Length, "3 path end behaviors");
        AssertEqual(PathEndBehavior.Stop, (PathEndBehavior)0, "Stop=0");
        AssertEqual(PathEndBehavior.Loop, (PathEndBehavior)1, "Loop=1");
        AssertEqual(PathEndBehavior.PingPong, (PathEndBehavior)2, "PingPong=2");

        AssertEqual(2, Enum.GetValues<CameraNoiseType>().Length, "2 noise types");
        AssertEqual(CameraNoiseType.Basic, (CameraNoiseType)0, "Basic=0");
        AssertEqual(CameraNoiseType.Perlin, (CameraNoiseType)1, "Perlin=1");
    }

    // ════════════════════════════════════════════════════════════════
    //  二、CinemachineBlend
    // ════════════════════════════════════════════════════════════════

    public static void TestBlendDefaults()
    {
        var b = new CinemachineBlend();
        AssertEqual(0.5, b.Duration, "默认 Duration=0.5");
        AssertEqual(CinemachineBlendStyle.EaseInOut, b.Style, "默认 Style=EaseInOut");
        AssertEqual(0f, b.Progress, "初始 Progress=0");
        AssertTrue(!b.IsComplete, "初始未完成");
    }

    public static void TestBlendLinearUpdate()
    {
        var b = new CinemachineBlend { Duration = 1.0, Style = CinemachineBlendStyle.Linear };
        b.Update(0.25);
        Near(0.25f, b.Progress, 1e-4f, "0.25s → Progress=0.25");
        Near(0.25f, b.GetBlendWeight(), 1e-4f, "Linear weight=Progress");
        b.Update(0.75);
        Near(1f, b.Progress, 1e-4f, "1.0s → Progress=1");
        AssertTrue(b.IsComplete, "完成后 IsComplete=true");
    }

    public static void TestBlendHardCut()
    {
        var b = new CinemachineBlend { Duration = 1.0, Style = CinemachineBlendStyle.HardCut };
        b.Update(0.3);
        Near(0f, b.GetBlendWeight(), 1e-5f, "HardCut <0.5 → weight=0");
        b.Reset();
        b.Update(0.7);
        Near(1f, b.GetBlendWeight(), 1e-5f, "HardCut >=0.5 → weight=1");
    }

    public static void TestBlendEaseInOut()
    {
        var b = new CinemachineBlend { Duration = 1.0, Style = CinemachineBlendStyle.EaseInOut };
        b.Update(0.5);
        // SmoothStep(0.5)=0.5*0.5*(3-2*0.5)=0.5
        Near(0.5f, b.GetBlendWeight(), 1e-4f, "EaseInOut(0.5)=0.5");
    }

    public static void TestBlendCustomCurve()
    {
        var b = new CinemachineBlend
        {
            Duration = 1.0,
            Style = CinemachineBlendStyle.CustomCurve,
            CustomCurve = t => t * t // 自定义二次曲线
        };
        b.Update(0.5);
        Near(0.25f, b.GetBlendWeight(), 1e-4f, "CustomCurve(t)=t²");
    }

    public static void TestBlendZeroDurationInstantComplete()
    {
        var b = new CinemachineBlend { Duration = 0.0 };
        b.Update(0.016);
        Near(1f, b.Progress, 1e-5f, "Duration=0 → 立即完成");
        AssertTrue(b.IsComplete, "IsComplete=true");
    }

    public static void TestBlendResetRestores()
    {
        var b = new CinemachineBlend { Duration = 1.0 };
        b.Update(0.5);
        b.Reset();
        AssertEqual(0f, b.Progress, "Reset 后 Progress=0");
        AssertTrue(!b.IsComplete, "Reset 后未完成");
    }

    // ════════════════════════════════════════════════════════════════
    //  三、VirtualCamera 基类
    // ════════════════════════════════════════════════════════════════

    public static void TestVirtualCameraDefaults()
    {
        var c = new VirtualCamera();
        AssertEqual("VirtualCamera", c.Name, "默认 Name");
        AssertEqual(10, c.Priority, "默认 Priority=10");
        AssertTrue(c.IsActive, "默认 IsActive=true");
        AssertEqual(0.5, c.BlendTime, "默认 BlendTime=0.5");
        AssertEqual(CinemachineBlendStyle.EaseInOut, c.BlendStyle, "默认 BlendStyle");
        AssertEqual(VirtualCameraState.Idle, c.State, "默认 State=Idle");
        AssertEqual(60f, c.CurrentFOV, "默认 FOV=60");
    }

    public static void TestVirtualCameraSetDirectState()
    {
        var c = new VirtualCamera("Test");
        var pos = new Vector3(1, 2, 3);
        var rot = Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f);
        c.SetDirectState(pos, rot, 75f);
        var (p, r, f) = c.GetState();
        AssertEqual(pos, p, "位置设置");
        AssertEqual(rot, r, "旋转设置");
        AssertEqual(75f, f, "FOV 设置");
    }

    public static void TestVirtualCameraNamePriorityCtor()
    {
        var c = new VirtualCamera("Cutscene", 20);
        AssertEqual("Cutscene", c.Name, "Name=构造参数");
        AssertEqual(20, c.Priority, "Priority=构造参数");
    }

    // ════════════════════════════════════════════════════════════════
    //  四、CameraShake / CameraNoise
    // ════════════════════════════════════════════════════════════════

    public static void TestCameraShakeDefaults()
    {
        var s = new CameraShake();
        AssertEqual("Shake", s.Name, "默认 Name");
        AssertEqual(1f, s.Intensity, "默认 Intensity=1");
        AssertEqual(10f, s.Frequency, "默认 Frequency=10");
        AssertEqual(1f, s.Damping, "默认 Damping=1");
        AssertTrue(!s.IsActive, "初始非活跃");
        AssertEqual(0f, s.ElapsedTime, "初始 ElapsedTime=0");
    }

    public static void TestCameraShakeLifecycle()
    {
        var s = new CameraShake { Duration = 0.5f };
        s.Start();
        AssertTrue(s.IsActive, "Start 后活跃");
        var (pos, rot) = s.Evaluate(0.1f);
        // 活跃且有幅度，位置偏移非零（随机，但 decay>0）
        AssertTrue(pos.LengthSquared() > 0, "活跃时产生位置偏移");
        s.Evaluate(0.5f); // 超过 Duration
        AssertTrue(!s.IsActive, "超过 Duration 自动停止");
    }

    public static void TestCameraShakeStopManual()
    {
        var s = new CameraShake();
        s.Start();
        s.Stop();
        AssertTrue(!s.IsActive, "Stop 后非活跃");
        var (pos, _) = s.Evaluate(0.1f);
        AssertEqual(Vector3.Zero, pos, "非活跃 Evaluate 返回零");
    }

    public static void TestCameraShakeReset()
    {
        var s = new CameraShake();
        s.Start();
        s.Evaluate(0.2f);
        AssertTrue(s.ElapsedTime > 0, "Evaluate 后 ElapsedTime>0");
        s.Reset();
        AssertEqual(0f, s.ElapsedTime, "Reset 后 ElapsedTime=0");
        AssertTrue(!s.IsActive, "Reset 后非活跃");
    }

    public static void TestCameraNoiseEvaluate()
    {
        var n = new CameraNoise();
        var (pos, rot) = n.Evaluate(0.016f);
        // 噪声始终产生非零偏移（Perlin 在 [0,1] 区间）
        AssertTrue(pos.LengthSquared() > 0, "噪声位置偏移非零");
    }

    public static void TestCameraNoiseLayers()
    {
        var n = new CameraNoise { PositionIntensity = 0f, RotationIntensity = 0f };
        n.Layers.Add(new NoiseLayer { PositionIntensity = 0.1f, RotationIntensity = 1f });
        var (pos, rot) = n.Evaluate(0.016f);
        // 单层叠加后仍有偏移
        AssertTrue(pos.LengthSquared() > 0, "叠加层后仍有位置偏移");
    }

    // ════════════════════════════════════════════════════════════════
    //  五、ImpulseListener
    // ════════════════════════════════════════════════════════════════

    public static void TestImpulseListenerDefaults()
    {
        var l = new ImpulseListener();
        AssertEqual("", l.Channel, "默认 Channel 空");
        AssertTrue(l.IsEnabled, "默认启用");
        AssertEqual(1f, l.Gain, "默认 Gain=1");
        AssertTrue(l.UseDecay, "默认 UseDecay=true");
        AssertEqual(Vector3.Zero, l.CurrentPositionOffset, "初始偏移零");
    }

    public static void TestImpulseListenerDisabledIgnores()
    {
        var l = new ImpulseListener { IsEnabled = false };
        var s = new CameraShake { Duration = 0.5f };
        l.ReceiveImpulse(s);
        AssertTrue(!s.IsActive, "禁用时不启动 shake");
    }

    public static void TestImpulseListenerChannelFilter()
    {
        var l = new ImpulseListener { Channel = "Camera" };
        var s = new CameraShake { Duration = 0.5f };
        l.ReceiveImpulse(s, "Gameplay"); // 频道不匹配
        AssertTrue(!s.IsActive, "频道不匹配忽略");

        l.ReceiveImpulse(s, "Camera"); // 频道匹配
        AssertTrue(s.IsActive, "频道匹配启动 shake");
    }

    public static void TestImpulseListenerReceiveStarts()
    {
        var l = new ImpulseListener();
        var s = new CameraShake { Duration = 0.5f };
        l.ReceiveImpulse(s);
        AssertTrue(s.IsActive, "ReceiveImpulse 启动 shake");
        l.Update(0.1f);
        AssertTrue(l.CurrentPositionOffset.LengthSquared() > 0, "Update 后产生偏移");
    }

    // ════════════════════════════════════════════════════════════════
    //  六、CinemachineBrain
    // ════════════════════════════════════════════════════════════════

    public static void TestBrainInitialState()
    {
        var b = new CinemachineBrain();
        AssertTrue(b.ActiveCamera is null, "初始 ActiveCamera=null");
        AssertTrue(b.FallbackCamera is null, "初始 FallbackCamera=null");
        AssertEqual(0, b.Cameras.Count, "初始 0 相机");
        AssertTrue(!b.IsUserOverride, "初始非 UserOverride");
        AssertEqual(1f, b.UserOverrideRecoveryDelay, "默认恢复延迟=1");
    }

    public static void TestBrainAddCameraPrioritySort()
    {
        var b = new CinemachineBrain();
        var low = new VirtualCamera("Low", 5);
        var high = new VirtualCamera("High", 20);
        var mid = new VirtualCamera("Mid", 10);
        b.AddCamera(low);
        b.AddCamera(high);
        b.AddCamera(mid);
        // 内部排序（高优先在前）
        AssertEqual(3, b.Cameras.Count, "3 相机");
        AssertEqual("High", b.Cameras[0].Name, "排序后首位=High");
        AssertEqual("Low", b.Cameras[^1].Name, "排序后末位=Low");
    }

    public static void TestBrainAddNullThrows()
    {
        var b = new CinemachineBrain();
        bool threw = false;
        try { b.AddCamera(null!); }
        catch (ArgumentNullException) { threw = true; }
        AssertTrue(threw, "AddCamera(null) 抛 ArgumentNullException");
    }

    public static void TestBrainRemoveCamera()
    {
        var b = new CinemachineBrain();
        var c = new VirtualCamera("Test");
        b.AddCamera(c);
        AssertTrue(b.RemoveCamera(c), "RemoveCamera 返回 true");
        AssertEqual(0, b.Cameras.Count, "移除后 0 相机");
        AssertTrue(!b.RemoveCamera(c), "二次移除返回 false");
    }

    public static void TestBrainUpdateActivatesHighestPriority()
    {
        var b = new CinemachineBrain();
        var low = new VirtualCamera("Low", 5) { BlendTime = 0 };
        var high = new VirtualCamera("High", 20) { BlendTime = 0 };
        b.AddCamera(low);
        b.AddCamera(high);
        b.Update(0.016f);
        AssertEqual(high, b.ActiveCamera, "Update 后激活最高优先级");
    }

    public static void TestBrainUpdateInactiveSkipped()
    {
        var b = new CinemachineBrain();
        var c1 = new VirtualCamera("Active", 10) { IsActive = true, BlendTime = 0 };
        var c2 = new VirtualCamera("Inactive", 20) { IsActive = false, BlendTime = 0 };
        b.AddCamera(c1);
        b.AddCamera(c2);
        b.Update(0.016f);
        // c2 虽高优先但 IsActive=false，跳过 → c1 激活
        AssertEqual(c1, b.ActiveCamera, "非活跃相机被跳过");
    }

    public static void TestBrainFallbackWhenNoneActive()
    {
        var b = new CinemachineBrain();
        var fallback = new VirtualCamera("Fallback") { BlendTime = 0 };
        b.FallbackCamera = fallback;
        b.Update(0.016f);
        AssertEqual(fallback, b.ActiveCamera, "无活跃相机时回退到 Fallback");
    }

    public static void TestBrainPrioritySwitchWithBlend()
    {
        var b = new CinemachineBrain();
        var c1 = new VirtualCamera("First", 10) { BlendTime = 0.5, BlendStyle = CinemachineBlendStyle.Linear };
        var c2 = new VirtualCamera("Second", 20) { BlendTime = 0.5, BlendStyle = CinemachineBlendStyle.Linear };
        b.AddCamera(c1);
        b.AddCamera(c2);
        b.Update(0.016f); // 激活 c2（高优先）
        AssertEqual(c2, b.ActiveCamera, "高优先激活");

        c1.Priority = 30; // 提升低优先相机
        b.Update(0.016f); // 触发切换 + 启动混合
        AssertEqual(c1, b.ActiveCamera, "切换到新高优先");
        // 混合进行中
        b.Update(0.25f);
        b.Update(0.25f); // 累计 0.5s → 混合完成
        AssertEqual(c1, b.ActiveCamera, "混合完成后 ActiveCamera=目标");
    }

    public static void TestBrainUserOverride()
    {
        var b = new CinemachineBrain();
        var c = new VirtualCamera("Auto") { BlendTime = 0 };
        b.AddCamera(c);
        b.Update(0.016f);
        AssertEqual(c, b.ActiveCamera, "正常激活");

        b.BeginUserOverride();
        AssertTrue(b.IsUserOverride, "BeginUserOverride 标记");
        b.EndUserOverride();
        AssertTrue(!b.IsUserOverride, "EndUserOverride 清除");
    }

    public static void TestBrainUserOverrideBlocksUpdate()
    {
        var b = new CinemachineBrain();
        var c = new VirtualCamera("Auto") { BlendTime = 0 };
        b.AddCamera(c);
        // UserOverride 时不更新相机（Update 直接 return）
        b.BeginUserOverride();
        b.Update(0.016f);
        AssertTrue(b.IsUserOverride, "UserOverride 期间保持标记");
        b.EndUserOverride();
    }

    public static void TestBrainTriggerImpulse()
    {
        var b = new CinemachineBrain();
        var s = new CameraShake { Duration = 0.5f };
        b.TriggerImpulse(s, "Test");
        AssertTrue(s.IsActive, "TriggerImpulse 启动 shake");
    }

    // ════════════════════════════════════════════════════════════════
    //  七、专用相机子类型
    // ════════════════════════════════════════════════════════════════

    public static void TestFollowCameraDefaults()
    {
        var c = new FollowCamera();
        AssertEqual(new Vector3(0, 5, -10), c.FollowOffset, "默认 FollowOffset");
        AssertEqual(0.5f, c.PositionDamping, "默认 PositionDamping=0.5");
        AssertEqual(float.MaxValue, c.HardLimitDistance, "默认 HardLimit=Max");
    }

    public static void TestLookAtCameraDefaults()
    {
        var c = new LookAtCamera();
        AssertEqual(0.5f, c.RotationDamping, "默认 RotationDamping=0.5");
        AssertTrue(c.MaintainLookAt, "默认 MaintainLookAt=true");
        AssertEqual(Vector3.Zero, c.LookAtOffset, "默认 LookAtOffset=0");
    }

    public static void TestFreeLookRigPosition()
    {
        var rig = new FreeLookRig { Radius = 10f, Height = 2f };
        var pos = rig.GetRigPosition(null, 0f, Vector3.UnitY);
        // angle=0 → x=cos(0)*10=10, z=sin(0)*10=0
        Near(10f, pos.X, 1e-4f, "angle=0 → x=Radius");
        Near(0f, pos.Z, 1e-4f, "angle=0 → z=0");
        Near(2f, pos.Y, 1e-4f, "y=Height");
    }

    public static void TestPathCameraRequiresTwoPoints()
    {
        var c = new PathCamera { Speed = 5f };
        c.PathPoints.Add(new Vector3(0, 0, 0));
        // 单点路径 UpdateState 不移动
        c.UpdateState(0.1f);
        AssertEqual(Vector3.Zero, c.CurrentPosition, "单点路径不移动");
        AssertEqual(0f, c.PathProgress, "PathProgress 保持 0");
    }

    public static void TestPathCameraMovesAlong()
    {
        var c = new PathCamera { Speed = 10f };
        c.PathPoints.Add(new Vector3(0, 0, 0));
        c.PathPoints.Add(new Vector3(0, 0, 10));
        c.PathPoints.Add(new Vector3(0, 0, 20));
        c.UpdateState(0.1f);
        AssertTrue(c.CurrentPosition.Z > 0, "路径相机沿 Z 前进");
        AssertTrue(c.PathProgress > 0, "PathProgress>0");
    }

    public static void TestStateDrivenCameraSwitch()
    {
        var cam = new StateDrivenCamera("SDC");
        var child1 = new VirtualCamera("State1");
        var child2 = new VirtualCamera("State2");
        var defaultCam = new VirtualCamera("Default");
        cam.StateCameraMap["Idle"] = child1;
        cam.StateCameraMap["Combat"] = child2;
        cam.DefaultCamera = defaultCam;

        cam.SetState("Idle");
        AssertEqual(child1, cam.ActiveChildCamera, "Idle → child1");
        cam.SetState("Combat");
        AssertEqual(child2, cam.ActiveChildCamera, "Combat → child2");
        cam.SetState("Unknown");
        AssertEqual(defaultCam, cam.ActiveChildCamera, "未知状态 → Default");
    }

    public static void TestStateDrivenCameraOnStateChanged()
    {
        var cam = new StateDrivenCamera("SDC");
        string? lastState = null;
        cam.OnStateChanged = s => lastState = s;
        cam.SetState("Run");
        AssertEqual("Run", lastState, "OnStateChanged 回调触发");
    }

    public static void TestClearShotCameraDefaults()
    {
        var c = new ClearShotCamera();
        AssertEqual(0, c.CandidateCameras.Count, "初始 0 候选");
        AssertEqual(0.1f, c.SwitchThreshold, "默认 SwitchThreshold=0.1");
        AssertEqual(0.5, c.ScoreRefreshInterval, "默认 ScoreRefreshInterval=0.5");
        AssertTrue(c.BestCamera is null, "初始 BestCamera=null");
    }

    public static void TestClearShotCameraEvaluatesBest()
    {
        var c = new ClearShotCamera { ScoreRefreshInterval = 0.0 };
        var camA = new VirtualCamera("A");
        var camB = new VirtualCamera("B");
        c.CandidateCameras.Add(camA);
        c.CandidateCameras.Add(camB);
        c.ScoreCamera = cam => cam == camB ? 0.9f : 0.3f;
        c.UpdateState(0.016f);
        AssertEqual(camB, c.BestCamera, "评分后选择最高分 camB");
    }
}
