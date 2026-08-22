// ==================================================================================================
//  EditorAudioBridge.cs — 山雨引擎编辑器音频桥接
// ==================================================================================================
//  命名空间: ShanyuEngine.Editor
//  依赖:     ShanyuEngine.Audio.AdvancedAudioEngine
//
//  功能:
//    1. 编辑器音频控制（主音量、静音、预览播放）
//    2. 3D 监听器位置同步（绑定到编辑器相机）
//    3. 音频调试统计（活跃音源数、DSP 负载、Bus 电平）
//    4. 编辑器内音效预览（点击播放，不受 PlayMode 影响）
//    5. 场景音频可视化（音源位置、衰减范围 Gizmo 数据）
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShanyuEngine.Audio;

namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════════
    //  AudioSourceGizmoInfo — 音频可视化数据
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频源 Gizmo 可视化所需的数据。由编辑器读取以在场景视图中绘制音源图标和衰减球。
    /// </summary>
    public sealed class AudioSourceGizmoInfo
    {
        /// <summary>音源世界位置。</summary>
        public Vector3 Position { get; set; }

        /// <summary>最小衰减距离。</summary>
        public float MinDistance { get; set; } = 1f;

        /// <summary>最大衰减距离。</summary>
        public float MaxDistance { get; set; } = 100f;

        /// <summary>当前音量（0-1）。</summary>
        public float CurrentVolume { get; set; } = 1f;

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; set; }

        /// <summary>音源名称/标签。</summary>
        public string Name { get; set; } = "AudioSource";

        /// <summary>是否循环。</summary>
        public bool IsLooping { get; set; }

        /// <summary>是否 3D 空间化。</summary>
        public bool Is3D { get; set; } = true;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  EditorAudioBridge
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器音频桥接。为编辑器 UI 提供对 <see cref="AdvancedAudioEngine"/> 的受控访问，
    /// 同时管理编辑器自身的音频预览功能（与 PlayMode 音频隔离）。
    /// </summary>
    public sealed class EditorAudioBridge : IDisposable
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly AdvancedAudioEngine _audioEngine;

        // ── 编辑器预览状态 ────────────────────────────────────────────────────────

        private float _editorMasterVolume = 1f;
        private bool _editorMuted = false;
        private readonly HashSet<Guid> _previewInstanceIds = new();

        // ── 事件 ──────────────────────────────────────────────────────────────────

        /// <summary>音量变化事件。</summary>
        public event Action<float>? OnMasterVolumeChanged;

        /// <summary>静音状态变化事件。</summary>
        public event Action<bool>? OnMuteChanged;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>底层音频引擎。</summary>
        public AdvancedAudioEngine AudioEngine => _audioEngine;

        /// <summary>主音量（0-1）。影响编辑器预览和 PlayMode 音频。</summary>
        public float MasterVolume
        {
            get => _editorMasterVolume;
            set
            {
                float clamped = Math.Clamp(value, 0f, 1f);
                if (Math.Abs(_editorMasterVolume - clamped) < 0.001f) return;
                _editorMasterVolume = clamped;
                ApplyMasterVolume();
                OnMasterVolumeChanged?.Invoke(clamped);
            }
        }

        /// <summary>是否静音。</summary>
        public bool IsMuted
        {
            get => _editorMuted;
            set
            {
                if (_editorMuted == value) return;
                _editorMuted = value;
                ApplyMasterVolume();
                OnMuteChanged?.Invoke(value);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  构造 / 释放
        // ─────────────────────────────────────────────────────────────────────────

        public EditorAudioBridge(AdvancedAudioEngine audioEngine)
        {
            _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        }

        public void Dispose()
        {
            StopAllPreviews();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  预览播放（编辑器内，不受 PlayMode 影响）
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 在编辑器中预览播放一个音效（2D）。
        /// 返回预览实例 ID，可用于后续停止。
        /// </summary>
        public Guid? PlayPreview(string clipName, float volume = 1f)
        {
            var clip = _audioEngine.GetClipByName(clipName);
            if (clip == null) return null;

            var instance = _audioEngine.Play2D(clip, volume * (_editorMuted ? 0f : _editorMasterVolume));
            if (instance == null) return null;

            _previewInstanceIds.Add(instance.Id);
            return instance.Id;
        }

        /// <summary>
        /// 在编辑器中预览播放一个 3D 音效。
        /// </summary>
        public Guid? PlayPreview3D(string clipName, Vector3 position, float volume = 1f)
        {
            var clip = _audioEngine.GetClipByName(clipName);
            if (clip == null) return null;

            var instance = _audioEngine.Play3D(clip, position, volume * (_editorMuted ? 0f : _editorMasterVolume));
            if (instance == null) return null;

            _previewInstanceIds.Add(instance.Id);
            return instance.Id;
        }

        /// <summary>停止指定预览实例。</summary>
        public void StopPreview(Guid instanceId)
        {
            _audioEngine.Stop(instanceId);
            _previewInstanceIds.Remove(instanceId);
        }

        /// <summary>停止所有编辑器预览音效。</summary>
        public void StopAllPreviews()
        {
            foreach (var id in _previewInstanceIds)
                _audioEngine.Stop(id);
            _previewInstanceIds.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  全局控制
        // ─────────────────────────────────────────────────────────────────────────

        private void ApplyMasterVolume()
        {
            float effective = _editorMuted ? 0f : _editorMasterVolume;
            _audioEngine.SetMasterVolume(effective);
        }

        /// <summary>暂停所有音频（包括 PlayMode 和预览）。</summary>
        public void PauseAll()
        {
            _audioEngine.PauseAll();
        }

        /// <summary>恢复所有音频。</summary>
        public void ResumeAll()
        {
            _audioEngine.ResumeAll();
        }

        /// <summary>停止所有音频（包括 PlayMode 和预览）。</summary>
        public void StopAll()
        {
            _audioEngine.StopAll();
            _previewInstanceIds.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  调试统计
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取音频引擎当前统计信息。
        /// </summary>
        public AudioDebugStats GetStats()
        {
            return new AudioDebugStats
            {
                ActiveInstanceCount = _audioEngine.ActiveInstanceCount,
                TotalClipCount = _audioEngine.ClipCount,
                MasterPeakLevel = _audioEngine.MasterPeakLevel,
                MasterVolume = _editorMasterVolume,
                IsMuted = _editorMuted,
            };
        }

        /// <summary>
        /// 获取所有活跃音源的 Gizmo 信息（用于场景视图可视化）。
        /// 基于当前已注册的音频实例构建。
        /// </summary>
        public List<AudioSourceGizmoInfo> GetActiveSourceGizmos()
        {
            var result = new List<AudioSourceGizmoInfo>();
            // AdvancedAudioEngine 不暴露实例枚举器，通过 GetInstance 查询已知 ID
            foreach (var id in _previewInstanceIds)
            {
                var inst = _audioEngine.GetInstance(id);
                if (inst == null) continue;
                result.Add(new AudioSourceGizmoInfo
                {
                    Position = inst.Position,
                    CurrentVolume = inst.Volume,
                    IsPlaying = true,
                    Name = inst.Clip?.Name ?? "Preview",
                    Is3D = true,
                });
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Bus / SoundClass 控制
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>设置指定 SoundClass 的音量。</summary>
        public void SetSoundClassVolume(string soundClassName, float volume)
        {
            _audioEngine.SetSoundClassVolume(soundClassName, Math.Clamp(volume, 0f, 1f));
        }

        /// <summary>获取指定 SoundClass 的音量。</summary>
        public float GetSoundClassVolume(string soundClassName)
        {
            var sc = _audioEngine.SoundClasses.GetClass(soundClassName);
            return sc?.Volume ?? 1f;
        }

        /// <summary>静音指定 SoundClass。</summary>
        public void MuteSoundClass(string soundClassName, bool muted)
        {
            var sc = _audioEngine.SoundClasses.GetClass(soundClassName);
            if (sc != null) sc.Muted = muted;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  AudioDebugStats
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>音频引擎调试统计。</summary>
    public sealed class AudioDebugStats
    {
        /// <summary>活跃播放实例数。</summary>
        public int ActiveInstanceCount { get; set; }

        /// <summary>已注册音频剪辑总数。</summary>
        public int TotalClipCount { get; set; }

        /// <summary>主总线峰值电平（dB）。</summary>
        public float MasterPeakLevel { get; set; }

        /// <summary>当前主音量。</summary>
        public float MasterVolume { get; set; }

        /// <summary>是否静音。</summary>
        public bool IsMuted { get; set; }
    }
}
