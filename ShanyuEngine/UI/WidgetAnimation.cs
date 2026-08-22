// ============================================================================================
//  WidgetAnimation.cs
//
//  山雨引擎 (ShanyuEngine) UI Widget 动画系统
//
//  提供 Widget 属性（位置/缩放/旋转/不透明度/颜色）的关键帧动画与插值播放。
//  原文件被截断损坏，此为基于 WidgetSystem/WidgetBase 重新编写的最小可用实现。
//
//  命名空间：ShanyuEngine.UI
//  目标框架：net8.0 / C# 8.0+
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.UI
{
    // ════════════════════════════════════════════════════════════════════════
    // 缓动函数
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>缓动函数类型。</summary>
    public enum EasingType
    {
        Linear,
        QuadraticEaseIn,
        QuadraticEaseOut,
        QuadraticEaseInOut,
        CubicEaseIn,
        CubicEaseOut,
        CubicEaseInOut,
        SineEaseIn,
        SineEaseOut,
        SineEaseInOut,
        ExponentialEaseIn,
        ExponentialEaseOut,
        ExponentialEaseInOut,
        BounceEaseOut,
        BackEaseOut,
    }

    /// <summary>缓动函数工具。</summary>
    public static class WidgetEasing
    {
        public static float Evaluate(EasingType type, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return type switch
            {
                EasingType.Linear => t,
                EasingType.QuadraticEaseIn => t * t,
                EasingType.QuadraticEaseOut => 1f - (1f - t) * (1f - t),
                EasingType.QuadraticEaseInOut => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
                EasingType.CubicEaseIn => t * t * t,
                EasingType.CubicEaseOut => 1f - (1f - t) * (1f - t) * (1f - t),
                EasingType.CubicEaseInOut => t < 0.5f ? 4f * t * t * t : 1f - 4f * (1f - t) * (1f - t) * (1f - t),
                EasingType.SineEaseIn => 1f - MathF.Cos(t * MathF.PI * 0.5f),
                EasingType.SineEaseOut => MathF.Sin(t * MathF.PI * 0.5f),
                EasingType.SineEaseInOut => -(MathF.Cos(MathF.PI * t) - 1f) * 0.5f,
                EasingType.ExponentialEaseIn => t == 0f ? 0f : MathF.Pow(2f, 10f * (t - 1f)),
                EasingType.ExponentialEaseOut => t == 1f ? 1f : 1f - MathF.Pow(2f, -10f * t),
                EasingType.ExponentialEaseInOut => t switch
                {
                    0f => 0f,
                    1f => 1f,
                    _ when t < 0.5f => MathF.Pow(2f, 20f * t - 10f) * 0.5f,
                    _ => (2f - MathF.Pow(2f, -20f * t + 10f)) * 0.5f
                },
                EasingType.BounceEaseOut => BounceEaseOut(t),
                EasingType.BackEaseOut => 1f + 2.70158f * (t - 1f) * (t - 1f) * (t - 1f) + 1.70158f * (t - 1f) * (t - 1f),
                _ => t
            };
        }

        private static float BounceEaseOut(float t)
        {
            if (t < 1f / 2.75f) return 7.5625f * t * t;
            if (t < 2f / 2.75f) { t -= 1.5f / 2.75f; return 7.5625f * t * t + 0.75f; }
            if (t < 2.5f / 2.75f) { t -= 2.25f / 2.75f; return 7.5625f * t * t + 0.9375f; }
            t -= 2.625f / 2.75f;
            return 7.5625f * t * t + 0.984375f;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 动画轨道
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>动画轨道类型 — Widget 的哪个属性被动画化。</summary>
    public enum WidgetTrackType
    {
        Position,
        Scale,
        Rotation,
        Opacity,
        Color,
    }

    /// <summary>单个关键帧（时间-值对）。</summary>
    public struct WidgetKeyframe
    {
        public float Time;
        public Vector4 Value;       // Position/Scale 用 XYZ，Opacity 用 X，Color 用 RGBA
        public EasingType Easing;

        public WidgetKeyframe(float time, Vector4 value, EasingType easing = EasingType.Linear)
        {
            Time = time;
            Value = value;
            Easing = easing;
        }
    }

    /// <summary>动画轨道：一条属性的时间线。</summary>
    public class WidgetAnimationTrack
    {
        public WidgetTrackType TrackType { get; set; }
        public List<WidgetKeyframe> Keyframes { get; } = new();

        public float Duration
        {
            get
            {
                if (Keyframes.Count == 0) return 0f;
                return Keyframes[^1].Time;
            }
        }

        public Vector4 Sample(float time)
        {
            if (Keyframes.Count == 0) return Vector4.Zero;
            if (Keyframes.Count == 1 || time <= Keyframes[0].Time) return Keyframes[0].Value;
            if (time >= Keyframes[^1].Time) return Keyframes[^1].Value;

            for (int i = 0; i < Keyframes.Count - 1; i++)
            {
                var a = Keyframes[i];
                var b = Keyframes[i + 1];
                if (time >= a.Time && time <= b.Time)
                {
                    float t = (time - a.Time) / (b.Time - a.Time);
                    t = WidgetEasing.Evaluate(a.Easing, t);
                    return Vector4.Lerp(a.Value, b.Value, t);
                }
            }
            return Keyframes[^1].Value;
        }

        public void AddKeyframe(float time, Vector4 value, EasingType easing = EasingType.Linear)
        {
            Keyframes.Add(new WidgetKeyframe(time, value, easing));
            Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Widget 动画
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Widget 动画 — 由多条轨道组成，可播放/暂停/停止。</summary>

    /// <summary>动画播放状态。</summary>
    public enum WidgetAnimationPlaybackState
    {
        Stopped,
        Playing,
        Paused,
    }

    /// <summary>Widget 动画播放器 — 管理动画的播放/暂停/停止/循环。</summary>
    public class WidgetAnimationPlayer
    {
        private readonly Dictionary<string, WidgetAnimation> _animations = new();
        private WidgetAnimation? _current;
        private float _currentTime;
        private WidgetAnimationPlaybackState _state = WidgetAnimationPlaybackState.Stopped;
        private bool _loop;
        private float _playbackSpeed = 1f;

        public WidgetAnimationPlaybackState State => _state;
        public float CurrentTime => _currentTime;
        public float Duration => _current?.Duration ?? 0f;
        public float NormalizedTime => Duration > 0f ? _currentTime / Duration : 0f;

        public void Register(string name, WidgetAnimation anim)
        {
            _animations[name] = anim;
        }

        public void Play(string name, bool loop = false)
        {
            if (!_animations.TryGetValue(name, out var anim)) return;
            _current = anim;
            _currentTime = 0f;
            _state = WidgetAnimationPlaybackState.Playing;
            _loop = loop;
        }

        public void Pause() => _state = WidgetAnimationPlaybackState.Paused;
        public void Resume() => _state = WidgetAnimationPlaybackState.Playing;
        public void Stop()
        {
            _state = WidgetAnimationPlaybackState.Stopped;
            _currentTime = 0f;
        }

        public void SetPlaybackSpeed(float speed) => _playbackSpeed = Math.Max(0f, speed);

        /// <summary>每帧更新动画时间，返回当前帧所有轨道的采样结果。</summary>
        public bool Tick(float deltaTime, out IReadOnlyList<WidgetAnimationTrack> activeTracks)
        {
            activeTracks = Array.Empty<WidgetAnimationTrack>();
            if (_state != WidgetAnimationPlaybackState.Playing || _current == null)
                return false;

            _currentTime += deltaTime * _playbackSpeed;
            var dur = _current.Duration;
            if (dur <= 0f) return false;

            if (_currentTime >= dur)
            {
                if (_loop)
                {
                    _currentTime %= dur;
                }
                else
                {
                    _currentTime = dur;
                    _state = WidgetAnimationPlaybackState.Stopped;
                }
            }

            activeTracks = _current.Tracks;
            return true;
        }
    }
}
