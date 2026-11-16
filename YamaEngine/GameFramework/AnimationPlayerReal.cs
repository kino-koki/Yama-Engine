using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Core;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 动画播放器节点 (AnimationPlayerReal)
    //
    //   对应 Godot 的 AnimationPlayer + Animation 资源：
    //   - Animation：一组属性轨道（translation/rotation/scale/value）的关键帧集合。
    //   - AnimationPlayerReal：持有命名 Animation，Play/Stop/Pause/Resume/Seek，
    //     每帧推进 position，对宿主 TransformReal 应用插值，非循环播完触发
    //     animation_finished 信号。支持 loop、speed_scale、播放队列。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>动画轨道类型（对应 Godot 的 track type）。</summary>
    public enum AnimationTrackType
    {
        /// <summary>位移轨道（驱动 TransformReal.LocalPosition）。</summary>
        Translation,
        /// <summary>旋转轨道（驱动 TransformReal.LocalRotation，球面插值）。</summary>
        Rotation,
        /// <summary>缩放轨道（驱动 TransformReal.LocalScale）。</summary>
        Scale,
        /// <summary>通用值轨道（浮点，通过 value_changed 信号回调）。</summary>
        Value
    }

    /// <summary>通用浮点关键帧。</summary>
    public sealed class FloatKeyframe
    {
        public float Time { get; set; }
        public float Value { get; set; }
        public FloatKeyframe() { }
        public FloatKeyframe(float time, float value) { Time = time; Value = value; }
    }

    /// <summary>
    /// 单条动画轨道：类型 + 关键帧列表（按时间升序）。
    /// Value 轨道额外携带属性名，便于回调识别。
    /// </summary>
    public sealed class AnimationTrack
    {
        public AnimationTrackType Type { get; set; }
        /// <summary>Value 轨道的属性名（其它类型可空）。</summary>
        public string PropertyName { get; set; } = string.Empty;
        public List<FloatKeyframe> Keys { get; } = new();

        public AnimationTrack(AnimationTrackType type, string property = "")
        {
            Type = type;
            PropertyName = property ?? string.Empty;
        }

        public void AddKey(float time, float value)
        {
            Keys.Add(new FloatKeyframe(time, value));
            Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        public float SampleFloat(float time)
        {
            if (Keys.Count == 0) return 0f;
            if (Keys.Count == 1 || time <= Keys[0].Time) return Keys[0].Value;
            if (time >= Keys[^1].Time) return Keys[^1].Value;
            for (int i = 0; i < Keys.Count - 1; i++)
            {
                var a = Keys[i]; var b = Keys[i + 1];
                if (time >= a.Time && time <= b.Time)
                {
                    float span = MathF.Max(1e-6f, b.Time - a.Time);
                    return float.Lerp(a.Value, b.Value, (time - a.Time) / span);
                }
            }
            return Keys[^1].Value;
        }
    }

    /// <summary>向量关键帧（位移/缩放轨道用）。</summary>
    public sealed class VectorKeyframeReal
    {
        public float Time { get; set; }
        public Vector3 Value { get; set; }
        public VectorKeyframeReal() { }
        public VectorKeyframeReal(float time, Vector3 value) { Time = time; Value = value; }
    }

    /// <summary>旋转关键帧（四元数，球面插值）。</summary>
    public sealed class QuatKeyframeReal
    {
        public float Time { get; set; }
        public Quaternion Value { get; set; }
        public QuatKeyframeReal() { }
        public QuatKeyframeReal(float time, Quaternion value) { Time = time; Value = value; }
    }

    /// <summary>
    /// 向量/旋转轨道：与 FloatKeyframe 轨道分离，避免类型混用。
    /// </summary>
    public sealed class VectorAnimationTrack
    {
        public AnimationTrackType Type { get; set; }
        public List<VectorKeyframeReal> Keys { get; } = new();

        public VectorAnimationTrack(AnimationTrackType type) => Type = type;

        public void AddKey(float time, Vector3 value)
        {
            Keys.Add(new VectorKeyframeReal(time, value));
            Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        public Vector3 Sample(float time)
        {
            if (Keys.Count == 0) return Vector3.Zero;
            if (Keys.Count == 1 || time <= Keys[0].Time) return Keys[0].Value;
            if (time >= Keys[^1].Time) return Keys[^1].Value;
            for (int i = 0; i < Keys.Count - 1; i++)
            {
                var a = Keys[i]; var b = Keys[i + 1];
                if (time >= a.Time && time <= b.Time)
                {
                    float span = MathF.Max(1e-6f, b.Time - a.Time);
                    return Vector3.Lerp(a.Value, b.Value, (time - a.Time) / span);
                }
            }
            return Keys[^1].Value;
        }
    }

    public sealed class RotationAnimationTrack
    {
        public List<QuatKeyframeReal> Keys { get; } = new();

        public void AddKey(float time, Quaternion value)
        {
            Keys.Add(new QuatKeyframeReal(time, Quaternion.Normalize(value)));
            Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        public Quaternion Sample(float time)
        {
            if (Keys.Count == 0) return Quaternion.Identity;
            if (Keys.Count == 1 || time <= Keys[0].Time) return Keys[0].Value;
            if (time >= Keys[^1].Time) return Keys[^1].Value;
            for (int i = 0; i < Keys.Count - 1; i++)
            {
                var a = Keys[i]; var b = Keys[i + 1];
                if (time >= a.Time && time <= b.Time)
                {
                    float span = MathF.Max(1e-6f, b.Time - a.Time);
                    return Quaternion.Normalize(Quaternion.Slerp(a.Value, b.Value, (time - a.Time) / span));
                }
            }
            return Keys[^1].Value;
        }
    }

    /// <summary>
    /// 动画资源：命名 + 时长 + 一组轨道。对应 Godot 的 Animation。
    /// </summary>
    public sealed class AnimationResource
    {
        public string Name { get; set; } = "Animation";
        public float Length { get; set; } = 1f;
        public bool Loop { get; set; }

        public List<AnimationTrack> FloatTracks { get; } = new();
        public List<VectorAnimationTrack> VectorTracks { get; } = new();
        public List<RotationAnimationTrack> RotationTracks { get; } = new();

        public AnimationResource() { }
        public AnimationResource(string name, float length, bool loop = false)
        {
            Name = name; Length = MathF.Max(1e-4f, length); Loop = loop;
        }

        public AnimationTrack AddFloatTrack(string property)
        {
            var t = new AnimationTrack(AnimationTrackType.Value, property);
            FloatTracks.Add(t);
            return t;
        }

        public VectorAnimationTrack AddVectorTrack(AnimationTrackType type)
        {
            var t = new VectorAnimationTrack(type);
            VectorTracks.Add(t);
            return t;
        }

        public RotationAnimationTrack AddRotationTrack()
        {
            var t = new RotationAnimationTrack();
            RotationTracks.Add(t);
            return t;
        }

        /// <summary>归一化播放时间（支持循环回绕）。</summary>
        internal float NormalizeTime(float time)
        {
            if (Length <= 0f) return 0f;
            if (!Loop) return Math.Clamp(time, 0f, Length);
            var w = time % Length;
            return w < 0f ? w + Length : w;
        }
    }

    /// <summary>
    /// 动画播放器节点：播放命名 Animation 并驱动宿主 TransformReal。
    /// 用法：
    /// <code>
    /// var player = go.AddComponent<AnimationPlayerReal>();
    /// var anim = new AnimationResource("walk", 1.0f, loop: true);
    /// anim.AddVectorTrack(AnimationTrackType.Translation).AddKey(0f, Vector3.Zero);
    /// anim.AddVectorTrack(AnimationTrackType.Translation).AddKey(1f, new Vector3(0,1,0));
    /// player.AddAnimation(anim);
    /// player.Play("walk");
    /// player.GetSignal("animation_finished").Connect(...);
    /// </code>
    /// </summary>
    public sealed class AnimationPlayerReal : ComponentReal
    {
        private readonly Dictionary<string, AnimationResource> _animations = new();
        private readonly Queue<string> _playQueue = new();

        private AnimationResource _current;
        private float _position;
        private bool _playing;
        private bool _paused;
        private float _speedScale = 1f;
        private int _finishedCount;

        /// <summary>当前动画名（无则空串）。</summary>
        public string CurrentAnimation => _current?.Name ?? string.Empty;

        /// <summary>是否正在播放（暂停时仍算 playing 但不推进）。</summary>
        public bool IsPlaying => _playing;

        /// <summary>是否暂停。</summary>
        public bool IsPaused => _paused;

        /// <summary>当前播放位置（秒）。</summary>
        public float CurrentPosition => _position;

        /// <summary>当前动画时长（秒，无动画为 0）。</summary>
        public float CurrentLength => _current?.Length ?? 0f;

        /// <summary>播放速度倍率。</summary>
        public float SpeedScale
        {
            get => _speedScale;
            set => _speedScale = MathF.Max(0f, value);
        }

        /// <summary>animation_finished 信号累计触发次数（仅非循环动画播完时）。</summary>
        public int FinishedCount => _finishedCount;

        /// <summary>已注册的动画数量。</summary>
        public int AnimationCount => _animations.Count;

        public AnimationPlayerReal() { }

        /// <summary>注册一个动画（按 Name 索引，重名覆盖）。</summary>
        public void AddAnimation(AnimationResource animation)
        {
            if (animation == null) throw new ArgumentNullException(nameof(animation));
            _animations[animation.Name] = animation;
        }

        /// <summary>是否包含指定动画。</summary>
        public bool HasAnimation(string name) => !string.IsNullOrEmpty(name) && _animations.ContainsKey(name);

        /// <summary>取指定动画（不存在抛 KeyNotFound）。</summary>
        public AnimationResource GetAnimation(string name)
        {
            if (!_animations.TryGetValue(name, out var a))
                throw new KeyNotFoundException($"Animation '{name}' not registered.");
            return a;
        }

        /// <summary>开始播放指定动画（从头或指定位置）。</summary>
        public void Play(string name, float fromPosition = 0f)
        {
            if (!_animations.TryGetValue(name, out var a))
                throw new KeyNotFoundException($"Cannot play unregistered animation '{name}'.");
            _current = a;
            _position = Math.Max(0f, fromPosition);
            _playing = true;
            _paused = false;
        }

        /// <summary>停止播放（复位到 0，清除队列）。</summary>
        public void Stop()
        {
            _playing = false;
            _paused = false;
            _position = 0f;
            _current = null;
            _playQueue.Clear();
        }

        /// <summary>暂停（保持 position，IsPlaying 仍 true）。</summary>
        public void Pause()
        {
            if (_playing) _paused = true;
        }

        /// <summary>恢复播放。</summary>
        public void Resume() => _paused = false;

        /// <summary>跳转到指定时间并立即应用该时刻的姿态（不改变播放状态）。</summary>
        public void Seek(float position)
        {
            if (_current == null) return;
            _position = _current.NormalizeTime(position);
            ApplyTracks(_position);
        }

        /// <summary>把动画排入队列：当前播完后自动接播（非循环）。</summary>
        public void QueueAnimation(string name)
        {
            if (string.IsNullOrEmpty(name) || !_animations.ContainsKey(name)) return;
            _playQueue.Enqueue(name);
        }

        /// <summary>
        /// 每帧推进：position += dt * speed_scale。对 TransformReal 应用轨道插值。
        /// 非循环动画到达结尾时触发 animation_finished，并自动接播队列下一个。
        /// </summary>
        public override void Update(float dt)
        {
            if (!_playing || _paused || _current == null) return;

            var prev = _position;
            _position += dt * _speedScale;

            bool finished = false;
            if (_current.Loop)
            {
                _position = _current.NormalizeTime(_position);
            }
            else if (_position >= _current.Length)
            {
                _position = _current.Length;
                finished = true;
            }

            ApplyTracks(_position);

            if (finished)
            {
                _finishedCount++;
                _playing = false;
                GameObject.GetSignal("animation_finished").Emit(_current.Name);

                // 自动接播队列
                if (_playQueue.Count > 0)
                {
                    var next = _playQueue.Dequeue();
                    Play(next, 0f);
                }
            }
        }

        /// <summary>把当前时间的关键帧插值应用到宿主 TransformReal。</summary>
        private void ApplyTracks(float time)
        {
            var tr = GameObject?.Transform;
            if (tr == null) return;

            foreach (var track in _current.VectorTracks)
            {
                var v = track.Sample(time);
                switch (track.Type)
                {
                    case AnimationTrackType.Translation:
                        tr.LocalPosition = v;
                        break;
                    case AnimationTrackType.Scale:
                        tr.LocalScale = v;
                        break;
                }
            }

            foreach (var track in _current.RotationTracks)
            {
                tr.LocalRotation = track.Sample(time);
            }

            // Value 轨道通过信号回调（对应 Godot 的 property path + callable）
            foreach (var track in _current.FloatTracks)
            {
                var v = track.SampleFloat(time);
                GameObject.GetSignal("value_changed").Emit(new ValueChangedArgs(track.PropertyName, v));
            }
        }
    }

    /// <summary>Value 轨道触发时携带的参数。</summary>
    public sealed class ValueChangedArgs
    {
        public string Property { get; }
        public float Value { get; }
        public ValueChangedArgs(string property, float value) { Property = property; Value = value; }
    }
}
