// AnimGraphEditorTests.cs — 动画蓝图编辑器面板闭环验证（UE5.8 深度级 任务E）
// 覆盖：
//   * NewBlueprint/OpenAsset 设置资产
//   * BuildPreview 从资产构建 Animator + AnimatorController
//   * SetFloat 驱动状态转移（Speed > 阈值 → Idle→Run）
//   * SetTrigger 驱动转移（Trigger 条件）
//   * TickPreview 推进状态
//   * 面板注册到 MainEditorWindow
// 全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using System.Numerics;
using YamaEngine.Animation;
using YamaEngine.Editor;
using YamaEngine.Editor.Panels;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>动画蓝图编辑器面板验证套件。</summary>
public sealed class AnimGraphEditorTests
{
    /// <summary>新建资产并打开。</summary>
    public static void TestPanelOpenNewAsset()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("ABP");
        var panel = new AnimGraphEditorPanel(skeleton);
        var asset = panel.NewBlueprint("ABP_Test");

        AssertTrue(panel.CurrentAsset == asset, "NewBlueprint 应设置当前资产。");
        AssertEqual("ABP_Test", panel.CurrentAsset!.Name, "资产名应正确。");
        AssertEqual(0, panel.StateCount, "新建资产初始无状态。");
        AssertEqual(0, panel.ParameterCount, "新建资产初始无参数。");
    }

    /// <summary>从资产构建预览运行时。</summary>
    public static void TestPanelBuildPreview()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("ABP");
        var panel = new AnimGraphEditorPanel(skeleton);
        var asset = MakeAsset();
        var clip = MakeClip("IdleClip", 0f);
        panel.RegisterClip(asset.States[0].ClipId, clip);
        panel.RegisterClip(asset.States[1].ClipId, MakeClip("RunClip", 2f));

        panel.OpenAsset(asset);
        panel.BuildPreview();

        AssertTrue(panel.PreviewController != null, "BuildPreview 应构建控制器。");
        AssertTrue(panel.PreviewAnimator != null, "BuildPreview 应构建 Animator。");
        AssertEqual(2, panel.PreviewController!.Layers[0].StateMachine.States.Count, "控制器应有 2 个状态。");
        AssertEqual(2, panel.StateCount, "资产应有 2 个状态。");
        AssertEqual(1, panel.TransitionCount, "资产应有 1 条转移。");
    }

    /// <summary>SetFloat 驱动状态转移（Speed > 阈值 → Idle→Run）。</summary>
    public static void TestPanelParametersDriveState()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("ABP");
        var panel = new AnimGraphEditorPanel(skeleton);
        var asset = MakeAsset();
        panel.RegisterClip(asset.States[0].ClipId, MakeClip("IdleClip", 0f));
        panel.RegisterClip(asset.States[1].ClipId, MakeClip("RunClip", 2f));
        panel.OpenAsset(asset);
        panel.BuildPreview();

        // 默认进入 Idle
        panel.TickPreview(0.1f);
        AssertEqual("Idle", panel.PreviewState, "默认状态应为 Idle。");

        // 提高速度触发转移
        panel.SetFloat("Speed", 3f);
        for (int i = 0; i < 12; i++) panel.TickPreview(0.1f);
        AssertEqual("Run", panel.PreviewState, "Speed>1 应转移至 Run。");
    }

    /// <summary>SetTrigger 驱动转移。</summary>
    public static void TestPanelSetTrigger()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("ABP");
        var panel = new AnimGraphEditorPanel(skeleton);
        var asset = new AnimationBlueprintAsset { Name = "TriggerABP" };
        asset.AddFloat("Speed");
        asset.AddTrigger("Jump");
        var idle = asset.AddClipState("Idle", Guid.NewGuid());
        var air = asset.AddClipState("Air", Guid.NewGuid());
        // Jump 触发 → Air
        asset.AddTransition("Idle", "Air", AnimatorConditionMode.If, "Jump");
        panel.RegisterClip(idle.ClipId, MakeClip("IdleClip", 0f));
        panel.RegisterClip(air.ClipId, MakeClip("AirClip", 3f));
        panel.OpenAsset(asset);
        panel.BuildPreview();

        panel.TickPreview(0.1f);
        AssertEqual("Idle", panel.PreviewState, "默认应为 Idle。");

        panel.SetTrigger("Jump");
        for (int i = 0; i < 12; i++) panel.TickPreview(0.1f);
        AssertEqual("Air", panel.PreviewState, "Jump 触发应转移至 Air。");
    }

    /// <summary>TickPreview 推进状态时间。</summary>
    public static void TestPanelTickPreviewAdvancesState()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("ABP");
        var panel = new AnimGraphEditorPanel(skeleton);
        var asset = MakeAsset();
        panel.RegisterClip(asset.States[0].ClipId, MakeClip("IdleClip", 0f));
        panel.RegisterClip(asset.States[1].ClipId, MakeClip("RunClip", 2f));
        panel.OpenAsset(asset);
        panel.BuildPreview();

        // 保持 Speed 低值（不转移）
        panel.SetFloat("Speed", 0f);
        panel.TickPreview(0.1f);
        string before = panel.PreviewState;
        for (int i = 0; i < 5; i++) panel.TickPreview(0.1f);
        AssertEqual(before, panel.PreviewState, "低 Speed 应保持 Idle。");
        AssertEqual("Idle", panel.PreviewState, "未转移时仍为 Idle。");
    }

    /// <summary>面板注册属性（可注册到 MainEditorWindow 的 EditorPanel）。</summary>
    public static void TestPanelRegisteredInMainWindow()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("ABP");
        var panel = new AnimGraphEditorPanel(skeleton);

        AssertTrue(panel is EditorPanel, "AnimGraphEditorPanel 应为 EditorPanel 子类。");
        AssertEqual("动画蓝图编辑器", panel.Title, "面板标题应为 '动画蓝图编辑器'。");
        AssertEqual("AnimGraphEditor", panel.Id, "面板 ID 应为 'AnimGraphEditor'。");
        AssertTrue(panel.CanClose, "面板应可关闭。");
        AssertTrue(panel.MinSize.X >= 300f, "面板应有合理最小宽度。");
    }

    // ── 辅助 ──────────────────────────────────────────────

    private static AnimationBlueprintAsset MakeAsset()
    {
        var asset = new AnimationBlueprintAsset { Name = "MoveABP", DefaultState = "Idle" };
        asset.AddFloat("Speed", 0f);
        asset.AddClipState("Idle", Guid.NewGuid());
        asset.AddClipState("Run", Guid.NewGuid());
        // Idle → Run 当 Speed > 1
        asset.AddTransition("Idle", "Run", AnimatorConditionMode.Greater, "Speed", 1f, 0.25f);
        return asset;
    }

    private static AnimationClip MakeClip(string name, float z)
    {
        var clip = new AnimationClip { Name = name, LengthSeconds = 1f, Looping = true };
        clip.AddTranslationKey("Hips", 0f, new Vector3(0, 0, z));
        clip.AddTranslationKey("Hips", 1f, new Vector3(0, 0, z));
        return clip;
    }
}
