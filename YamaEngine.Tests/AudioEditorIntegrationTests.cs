// AudioEditorIntegrationTests.cs — 音频编辑器集成验证套件
// 覆盖:
//   * EditorAudioBridge 后端选择器：InitializeBackend(Software/Dummy) → 成功 + CurrentBackendType 正确 + IsBackendInitialized
//   * AvailableBackends 完整列表（Software/OpenAL/ALSoft/WASAPI/Dummy）
//   * GetBackendDisplayName 各类型可读名称
//   * EditorAudioBridge 音量控制：MasterVolume 夹取 [0,1]、IsMuted、SoundClass 音量设置/读取
//   * AudioSettingsPanel 面板注册（EditorPanel 子类、标题"音频设置"）
// 全部 CPU 级确定性：Software/Dummy 后端无原生依赖，不依赖真实音频设备/GPU。
using YamaEngine.Audio;
using YamaEngine.Editor;
using YamaEngine.Editor.Panels;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>音频编辑器集成（后端选择器 UI 能力）验证套件。</summary>
public sealed class AudioEditorIntegrationTests
{
    /// <summary>EditorAudioBridge 后端选择器：Software 后端（纯 C# 离线）应初始化成功且类型正确。</summary>
    public static void TestAudioBridgeBackendSelectorSoftware()
    {
        var engine = new AdvancedAudioEngine();
        var bridge = new EditorAudioBridge(engine);

        bool ok = bridge.InitializeBackend(AudioBackendType.Software);

        AssertTrue(ok, "Software 后端应初始化成功。");
        AssertTrue(bridge.IsBackendInitialized, "桥应报告后端已初始化。");
        AssertEqual(AudioBackendType.Software, bridge.CurrentBackendType,
            "当前后端应为 Software。");
    }

    /// <summary>EditorAudioBridge 后端选择器：Dummy 后端（无输出）应初始化成功。</summary>
    public static void TestAudioBridgeBackendSelectorDummy()
    {
        var engine = new AdvancedAudioEngine();
        var bridge = new EditorAudioBridge(engine);

        bool ok = bridge.InitializeBackend(AudioBackendType.Dummy);

        AssertTrue(ok, "Dummy 后端应初始化成功。");
        AssertEqual(AudioBackendType.Dummy, bridge.CurrentBackendType,
            "当前后端应为 Dummy。");
    }

    /// <summary>AvailableBackends 应包含全部 5 种可选后端。</summary>
    public static void TestAudioBridgeAvailableBackends()
    {
        AssertEqual(5, EditorAudioBridge.AvailableBackends.Count,
            "应提供 5 种可选后端。");

        foreach (var t in new[]
        {
            AudioBackendType.Software, AudioBackendType.OpenAL, AudioBackendType.ALSoft,
            AudioBackendType.WASAPI, AudioBackendType.Dummy
        })
        {
            AssertTrue(EditorAudioBridge.AvailableBackends.Contains(t),
                $"AvailableBackends 应包含 {t}。");
        }
    }

    /// <summary>GetBackendDisplayName 应为每种后端提供可读名称。</summary>
    public static void TestAudioBridgeBackendDisplayName()
    {
        foreach (var t in EditorAudioBridge.AvailableBackends)
        {
            string name = EditorAudioBridge.GetBackendDisplayName(t);
            AssertTrue(!string.IsNullOrWhiteSpace(name),
                $"后端 {t} 应提供非空显示名。");
        }
        // 代表性名称包含关键词
        AssertTrue(EditorAudioBridge.GetBackendDisplayName(AudioBackendType.Software).Contains("Software"),
            "Software 显示名应含 'Software'。");
        AssertTrue(EditorAudioBridge.GetBackendDisplayName(AudioBackendType.Dummy).Contains("Dummy"),
            "Dummy 显示名应含 'Dummy'。");
    }

    /// <summary>音量控制：MasterVolume 夹取、IsMuted、SoundClass 音量设置/读取。</summary>
    public static void TestAudioBridgeVolumeControls()
    {
        var engine = new AdvancedAudioEngine();
        var bridge = new EditorAudioBridge(engine);

        // MasterVolume 夹取 [0,1]
        bridge.MasterVolume = 1.5f;
        AssertTrue(bridge.MasterVolume <= 1.0f, "MasterVolume 应被夹取到 ≤1。");
        bridge.MasterVolume = -0.5f;
        AssertTrue(bridge.MasterVolume >= 0.0f, "MasterVolume 应被夹取到 ≥0。");

        // IsMuted
        bridge.IsMuted = true;
        AssertTrue(bridge.IsMuted, "静音应生效。");
        bridge.IsMuted = false;
        AssertTrue(!bridge.IsMuted, "取消静音应生效。");

        // SoundClass 音量设置/读取
        bridge.SetSoundClassVolume("SFX", 0.6f);
        AssertTrue(Math.Abs(bridge.GetSoundClassVolume("SFX") - 0.6f) < 0.001f,
            "SFX 音量应可设置并读取。");
        bridge.SetSoundClassVolume("Music", 0.35f);
        AssertTrue(Math.Abs(bridge.GetSoundClassVolume("Music") - 0.35f) < 0.001f,
            "Music 音量应可设置并读取。");
    }

    /// <summary>AudioSettingsPanel 应注册为 EditorPanel 子类且标题为"音频设置"。</summary>
    public static void TestAudioSettingsPanelRegistered()
    {
        var panel = new AudioSettingsPanel();

        AssertTrue(panel is EditorPanel, "AudioSettingsPanel 应为 EditorPanel 子类。");
        AssertEqual("音频设置", panel.Title, "面板标题应为 '音频设置'。");
        AssertEqual("AudioSettings", panel.Id, "面板 ID 应为 'AudioSettings'。");
        AssertTrue(panel.CanClose, "面板应可关闭。");
    }
}
