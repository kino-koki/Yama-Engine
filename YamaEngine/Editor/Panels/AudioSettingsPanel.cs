using System.Numerics;
using ImGuiNET;
using YamaEngine.Audio;

namespace YamaEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 音频设置面板 (AudioSettingsPanel)
    //
    // 提供编辑器内音频控制:
    //   - 输出后端选择器 (Software / OpenAL / OpenAL-soft / WASAPI / Dummy)
    //   - 后端初始化与状态显示 (当前后端 / 是否已初始化)
    //   - 主音量 + 静音控制
    //   - 各 SoundClass (SFX / Music / UI / Voice / Ambient) 音量控制
    //   - 实时音频统计 (活跃实例 / 剪辑数 / 主总线峰值)
    //
    // 通过 EditorContext.AudioBridge (EditorAudioBridge) 访问底层 AdvancedAudioEngine。
    // 后端选择器能力由 EditorAudioBridge 暴露（InitializeBackend / CurrentBackendType / 等）。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>音频设置面板: 后端选择器 + 音量 + SoundClass + 统计。</summary>
    public sealed class AudioSettingsPanel : EditorPanel
    {
        private static readonly string[] SoundClassNames = { "SFX", "Music", "UI", "Voice", "Ambient" };

        private bool _showBackends = true;
        private bool _showVolumes = true;
        private bool _showSoundClasses = true;
        private bool _showStats = true;

        public AudioSettingsPanel() : base("音频设置", "AudioSettings")
        {
            CanClose = true;
            MinSize = new Vector2(280, 220);
        }

        protected override void OnDraw(EditorContext ctx)
        {
            var bridge = ctx?.AudioBridge;
            if (bridge == null)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.3f, 1f),
                    "音频引擎未初始化（EditorAudioBridge 未注入）。");
                ImGui.TextDisabled("请先创建 AdvancedAudioEngine 并挂接 EditorAudioBridge。");
                return;
            }

            DrawBackendSection(bridge);
            ImGui.Separator();
            DrawVolumeSection(bridge);
            ImGui.Separator();
            DrawSoundClassSection(bridge);
            ImGui.Separator();
            DrawStatsSection(bridge);
        }

        // ─────────────────────────── 后端选择器 ───────────────────────────

        private void DrawBackendSection(EditorAudioBridge bridge)
        {
            ImGui.Checkbox("输出后端", ref _showBackends);
            if (!_showBackends) return;

            var current = bridge.CurrentBackendType;
            bool initialized = bridge.IsBackendInitialized;

            // 当前状态
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "  当前后端:");
            ImGui.SameLine();
            ImGui.Text(current.HasValue
                ? EditorAudioBridge.GetBackendDisplayName(current.Value)
                : "未选择");
            ImGui.SameLine(220);
            ImGui.TextColored(initialized
                ? new Vector4(0.3f, 0.85f, 0.45f, 1f)
                : new Vector4(0.9f, 0.4f, 0.4f, 1f),
                initialized ? "已初始化" : "未初始化");

            // 可选后端按钮
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "  选择并初始化后端:");
            foreach (var backend in EditorAudioBridge.AvailableBackends)
            {
                bool isActive = current == backend;
                string label = EditorAudioBridge.GetBackendDisplayName(backend);
                string btn = label + "##backend_" + (int)backend;

                if (isActive)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.55f, 0.35f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.65f, 0.4f, 1f));
                }

                if (ImGui.Button(btn, new Vector2(-1, 24)))
                {
                    bool ok = bridge.InitializeBackend(backend);
                    ImGui.TextColored(ok
                        ? new Vector4(0.3f, 0.85f, 0.45f, 1f)
                        : new Vector4(0.9f, 0.4f, 0.4f, 1f),
                        ok ? $"  {label} 初始化成功。" : $"  {label} 初始化失败（已回退）。");
                }

                if (isActive)
                {
                    ImGui.PopStyleColor(2);
                }
            }
        }

        // ─────────────────────────── 主音量 ───────────────────────────

        private void DrawVolumeSection(EditorAudioBridge bridge)
        {
            ImGui.Checkbox("主音量", ref _showVolumes);
            if (!_showVolumes) return;

            float volume = bridge.MasterVolume;
            bool muted = bridge.IsMuted;

            // 音量条
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.3f, 0.6f, 0.95f, 1f));
            ImGui.ProgressBar(volume, new Vector2(-1, 16), $"  主音量: {volume * 100:F0}%");
            ImGui.PopStyleColor();

            // 调节按钮
            if (ImGui.Button("− 0.1##vol_down")) bridge.MasterVolume = volume - 0.1f;
            ImGui.SameLine();
            if (ImGui.Button("+ 0.1##vol_up")) bridge.MasterVolume = volume + 0.1f;
            ImGui.SameLine();
            ImGui.Checkbox("静音", ref muted);
            if (muted != bridge.IsMuted) bridge.IsMuted = muted;

            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f),
                $"  生效音量: {(bridge.IsMuted ? 0f : bridge.MasterVolume * 100):F0}%");
        }

        // ─────────────────────────── SoundClass 音量 ───────────────────────────

        private void DrawSoundClassSection(EditorAudioBridge bridge)
        {
            ImGui.Checkbox("SoundClass 音量", ref _showSoundClasses);
            if (!_showSoundClasses) return;

            foreach (var name in SoundClassNames)
            {
                float v = bridge.GetSoundClassVolume(name);
                ImGui.Text($"{name,8}");
                ImGui.SameLine(90);
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.8f, 0.45f, 0.9f, 1f));
                ImGui.ProgressBar(v, new Vector2(-70, 14), "");
                ImGui.PopStyleColor();
                ImGui.SameLine(-58);
                ImGui.Text($"{v * 100:F0}%");
                ImGui.SameLine(-58);
                if (ImGui.Button("-##sc_" + name)) bridge.SetSoundClassVolume(name, v - 0.1f);
                ImGui.SameLine(-58);
                if (ImGui.Button("+##sc_" + name)) bridge.SetSoundClassVolume(name, v + 0.1f);
            }
        }

        // ─────────────────────────── 统计 ───────────────────────────

        private void DrawStatsSection(EditorAudioBridge bridge)
        {
            ImGui.Checkbox("实时统计", ref _showStats);
            if (!_showStats) return;

            var stats = bridge.GetStats();
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "  音频引擎统计");
            ImGui.Text($"    活跃实例:  {stats.ActiveInstanceCount}");
            ImGui.Text($"    已注册剪辑: {stats.TotalClipCount}");
            ImGui.Text($"    主总线峰值: {stats.MasterPeakLevel:F2} dB");
        }
    }
}
