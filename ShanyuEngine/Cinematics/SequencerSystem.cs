using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Cinematics
{
    // ============================================================================================
    //  SequencerSystem.cs
    //
    //  山雨引擎 —— 电影级序列器系统 (Cinematics Sequencer System)
    //
    //  本文件实现面向电影制作的完整非线性编辑序列器，对标 UE6 Level Sequencer。
    //  与 ShanyuEngine.Sequencer（底层基础设施）和 ShanyuEngine.Animation.SequencerSystem
    //  （动画序列器）互补，本系统专注于：
    //    - 电影级别的多轨道非线性时间轴编辑
    //    - 高级关键帧插值（Constant / Linear / Cubic / Hermite / Bezier）
    //    - 多相机切轨系统（Cut / Blend / Fade / Slide）
    //    - 子序列嵌套引用
    //    - 序列事件系统与蓝图事件触发
    //    - 电影导演模式（Director）：全屏播放、HUD 控制、结束后恢复
    //
    //  主要组成：
    //    1. CinematicKeyframe        —— 泛型关键帧（支持多插值模式）
    //    2. CinematicChannel         —— 关键帧通道，提供时间采样求值
    //    3. CinematicSection         —— 轨道片段（起止时间、所属轨道）
    //    4. CinematicTrack           —— 轨道基类 + 6 种具体轨道
    //    5. CinematicSequence        —— 序列（多轨道容器、长度、播放速率、循环）
    //    6. CinematicSubSequence     —— 子序列引用（嵌套序列）
    //    7. SequencerPlayer          —— 播放器（Play/Pause/Stop/Scrub/Loop）
    //    8. SequencerEvaluator       —— 评估器（按时间评估所有轨道、驱动 Actor）
    //    9. CameraCutSystem          —— 相机切轨系统（多相机轨道切换）
    //   10. CinematicEventSystem     —— 序列器事件（自定义事件触发点）
    //   11. CinematicDirector       —— 电影模式导演（全屏/HUD隐藏/恢复）
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>电影序列器关键帧插值模式。</summary>
    public enum CinematicInterpolationMode
    {
        /// <summary>常量（阶跃）：保持前一关键帧值直到下一帧。</summary>
        Constant,
        /// <summary>线性：关键帧之间匀速过渡。</summary>
        Linear,
        /// <summary>三次多项式：自动计算平滑曲线。</summary>
        Cubic,
        /// <summary>Hermite 样条：使用切线控制曲线方向与速率。</summary>
        Hermite,
        /// <summary>Bezier：使用入/出控制点实现完全自定义曲线。</summary>
        Bezier
    }

    /// <summary>电影序列器播放状态。</summary>
    public enum CinematicPlaybackState
    {
        /// <summary>已停止。</summary>
        Stopped,
        /// <summary>正在播放。</summary>
        Playing,
        /// <summary>暂停。</summary>
        Paused,
        /// <summary>梳理/拖拽模式。</summary>
        Scrubbing
    }

    /// <summary>电影序列器循环模式。</summary>
    public enum CinematicLoopMode
    {
        /// <summary>不循环。</summary>
        None,
        /// <summary>循环播放。</summary>
        Loop,
        /// <summary>乒乓循环：正播结束后反向播回起始。</summary>
        PingPong
    }

    /// <summary>电影轨道类型。</summary>
    public enum CinematicTrackType
    {
        /// <summary>Actor 变换轨道：驱动 Actor 的位置、旋转、缩放。</summary>
        Actor,
        /// <summary>相机轨道：相机切换与混合。</summary>
        Camera,
        /// <summary>音频轨道：音频播放控制与音量曲线。</summary>
        Audio,
        /// <summary>事件轨道：在指定时刻触发自定义事件。</summary>
        Event,
        /// <summary>动画轨道：驱动角色骨骼动画播放。</summary>
        Animation,
        /// <summary>材质轨道：驱动材质参数动画。</summary>
        Material,
        /// <summary>粒子轨道：控制粒子发射器触发/停止。</summary>
        Particle,
        /// <summary>子序列轨道：嵌套引用另一个序列。</summary>
        SubSequence
    }

    /// <summary>相机切换混合模式。</summary>
    public enum CameraCutBlendMode
    {
        /// <summary>硬切：瞬间切换。</summary>
        Cut,
        /// <summary>混合淡变：在时长内平滑过渡位置和朝向。</summary>
        Blend,
        /// <summary>淡入淡出：先淡出到黑再淡入新画面。</summary>
        Fade,
        /// <summary>滑动：新画面从指定方向滑入。</summary>
        Slide
    }

    /// <summary>滑动方向（用于 Slide 混合模式）。</summary>
    public enum SlideDirection
    {
        Left,
        Right,
        Up,
        Down
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 泛型关键帧 (CinematicKeyframe)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列器泛型关键帧。携带时间戳、值、插值模式及可选的切线/控制点。
    /// 支持 float / Vector3 / Quaternion 等多种值类型。
    /// </summary>
    public struct CinematicKeyframe<T>
    {
        /// <summary>关键帧时间（秒）。</summary>
        public float Time;

        /// <summary>关键帧值。</summary>
        public T Value;

        /// <summary>插值模式。</summary>
        public CinematicInterpolationMode Interpolation;

        /// <summary>Hermite 入切线（时间方向的变化率）。</summary>
        public T InTangent;

        /// <summary>Hermite 出切线。</summary>
        public T OutTangent;

        /// <summary>Bezier 入控制点（相对于关键帧位置的偏移）。</summary>
        public T BezierIn;

        /// <summary>Bezier 出控制点。</summary>
        public T BezierOut;

        public CinematicKeyframe(
            float time, T value,
            CinematicInterpolationMode interpolation = CinematicInterpolationMode.Linear)
        {
            Time = time;
            Value = value;
            Interpolation = interpolation;
            InTangent = default!;
            OutTangent = default!;
            BezierIn = default!;
            BezierOut = default!;
        }

        public CinematicKeyframe(
            float time, T value,
            T inTangent, T outTangent,
            CinematicInterpolationMode interpolation = CinematicInterpolation.Hermite)
            : this(time, value, interpolation)
        {
            InTangent = inTangent;
            OutTangent = outTangent;
        }

        public CinematicKeyframe(
            float time, T value,
            T bezierIn, T bezierOut)
            : this(time, value, CinematicInterpolationMode.Bezier)
        {
            BezierIn = bezierIn;
            BezierOut = bezierOut;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 通道 (Channel) —— 关键帧序列的采样器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列器泛型通道。管理一组有序关键帧，支持在任意时刻采样求值。
    /// 针对不同值类型提供特化的插值算法。
    /// </summary>
    public class CinematicChannel<T>
    {
        private readonly List<CinematicKeyframe<T>> _keyframes = new();
        private bool _sorted = true;

        /// <summary>关键帧列表（只读）。</summary>
        public IReadOnlyList<CinematicKeyframe<T>> Keyframes => _keyframes;

        /// <summary>关键帧数量。</summary>
        public int KeyframeCount => _keyframes.Count;

        /// <summary>通道是否为空。</summary>
        public bool IsEmpty => _keyframes.Count == 0;

        /// <summary>
        /// 插值函数委托。由外部或子类设置，定义两个值之间如何插值。
        /// 参数：(值A, 值B, 归一化因子t, 插值模式, 入切线, 出切线, Bezier入, Bezier出) => 插值结果
        /// </summary>
        public Func<T, T, float, CinematicInterpolationMode, T, T, T, T, T>? Interpolator;

        /// <summary>添加关键帧。</summary>
        public void AddKeyframe(CinematicKeyframe<T> keyframe)
        {
            _keyframes.Add(keyframe);
            _sorted = false;
        }

        /// <summary>移除指定时间的关键帧。</summary>
        public bool RemoveKeyframeAt(float time, float epsilon = 1e-4f)
        {
            for (int i = 0; i < _keyframes.Count; i++)
            {
                if (MathF.Abs(_keyframes[i].Time - time) < epsilon)
                {
                    _keyframes.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>清除所有关键帧。</summary>
        public void Clear() { _keyframes.Clear(); _sorted = true; }

        private void EnsureSorted()
        {
            if (!_sorted)
            {
                _keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
                _sorted = true;
            }
        }

        /// <summary>
        /// 在指定时刻采样求值。超出首/末帧时间范围则钳制。
        /// </summary>
        /// <param name="time">采样时间（秒）。</param>
        /// <returns>插值后的值。</returns>
        public T EvaluateAtTime(float time)
        {
            EnsureSorted();

            if (_keyframes.Count == 0) return default!;
            if (_keyframes.Count == 1) return _keyframes[0].Value;
            if (time <= _keyframes[0].Time) return _keyframes[0].Value;
            if (time >= _keyframes[^1].Time) return _keyframes[^1].Value;

            // 二分查找定位区间
            int lo = 0, hi = _keyframes.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keyframes[mid].Time <= time) lo = mid;
                else hi = mid;
            }

            var k0 = _keyframes[lo];
            var k1 = _keyframes[hi];
            float dt = k1.Time - k0.Time;

            if (dt <= 1e-6f) return k0.Value;

            float t = (time - k0.Time) / dt; // 归一化因子 [0, 1]

            // 常量模式：直接返回前一帧值
            if (k0.Interpolation == CinematicInterpolationMode.Constant)
                return k0.Value;

            // 使用外部设置的插值器，或使用默认的线性插值
            if (Interpolator != null)
            {
                return Interpolator(k0.Value, k1.Value, t, k0.Interpolation,
                    k0.OutTangent, k1.InTangent, k0.BezierOut, k1.BezierIn);
            }

            // 默认：若 T 是数值类型则做线性插值
            return LerpDefault(k0.Value, k1.Value, t);
        }

        /// <summary>获取通道时间范围。</summary>
        public void GetTimeRange(out float startTime, out float endTime)
        {
            EnsureSorted();
            startTime = _keyframes.Count > 0 ? _keyframes[0].Time : 0f;
            endTime = _keyframes.Count > 0 ? _keyframes[^1].Time : 0f;
        }

        // 默认线性插值（对支持 + 和 * 的数值类型有效）
        private static T LerpDefault(T a, T b, float t)
        {
            // 对于 float
            if (a is float fa && b is float fb)
            {
                return (T)(object)(fa + (fb - fa) * t);
            }
            // 对于 Vector3
            if (a is Vector3 va && b is Vector3 vb)
            {
                return (T)(object)Vector3.Lerp(va, vb, t);
            }
            // 对于 Quaternion
            if (a is Quaternion qa && b is Quaternion qb)
            {
                // 选择最短弧
                if (Quaternion.Dot(qa, qb) < 0f) qb = Quaternion.Negate(qb);
                return (T)(object)Quaternion.Slerp(qa, qb, t);
            }
            // 回退：返回 a
            return a;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 预构建插值器 —— 为 float / Vector3 / Quaternion 提供完整的五种插值实现
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 浮点通道插值器。提供 Constant / Linear / Cubic / Hermite / Bezier 五种插值。
    /// </summary>
    public static class FloatChannelInterpolator
    {
        /// <summary>标准浮点插值函数，可直接赋值给 CinematicChannel{float}.Interpolator。</summary>
        public static float Interpolate(
            float a, float b, float t,
            CinematicInterpolationMode mode,
            float outTangent, float inTangent,
            float bezierOut, float bezierIn)
        {
            return mode switch
            {
                CinematicInterpolationMode.Constant => a,
                CinematicInterpolationMode.Linear => a + (b - a) * t,

                // Hermite 样条：h00*a + h10*outTan*t + h01*b + h11*inTan*t
                CinematicInterpolationMode.Hermite => EvaluateHermite(
                    a, b, t, outTangent, inTangent),

                // 三次自动平滑：Catmull-Rom 式自动切线
                CinematicInterpolationMode.Cubic => a + (b - a) * (t * t * (3f - 2f * t)),

                // Bezier：四控制点三次曲线
                CinematicInterpolationMode.Bezier => EvaluateBezierScalar(
                    a, b, t, bezierOut, bezierIn),

                _ => a + (b - a) * t
            };
        }

        private static float EvaluateHermite(float a, float b, float t, float outTangent, float inTangent)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * a + h10 * outTangent + h01 * b + h11 * inTangent;
        }

        private static float EvaluateBezierScalar(float a, float b, float t, float cpOut, float cpIn)
        {
            float p0 = a;
            float p3 = b;
            float p1 = p0 + cpOut;
            float p2 = p3 + cpIn;
            float mt = 1f - t;
            return mt * mt * mt * p0
                 + 3f * mt * mt * t * p1
                 + 3f * mt * t * t * p2
                 + t * t * t * p3;
        }
    }

    /// <summary>
    /// Vector3 通道插值器。每个分量独立应用浮点插值。
    /// </summary>
    public static class Vector3ChannelInterpolator
    {
        public static Vector3 Interpolate(
            Vector3 a, Vector3 b, float t,
            CinematicInterpolationMode mode,
            Vector3 outTangent, Vector3 inTangent,
            Vector3 bezierOut, Vector3 bezierIn)
        {
            return mode switch
            {
                CinematicInterpolationMode.Constant => a,
                CinematicInterpolationMode.Linear => Vector3.Lerp(a, b, t),
                CinematicInterpolationMode.Cubic => Vector3.Lerp(a, b, t * t * (3f - 2f * t)),
                CinematicInterpolationMode.Hermite => new Vector3(
                    FloatChannelInterpolator.Interpolate(a.X, b.X, t, mode, outTangent.X, inTangent.X, bezierOut.X, bezierIn.X),
                    FloatChannelInterpolator.Interpolate(a.Y, b.Y, t, mode, outTangent.Y, inTangent.Y, bezierOut.Y, bezierIn.Y),
                    FloatChannelInterpolator.Interpolate(a.Z, b.Z, t, mode, outTangent.Z, inTangent.Z, bezierOut.Z, bezierIn.Z)),
                CinematicInterpolationMode.Bezier => new Vector3(
                    FloatChannelInterpolator.Interpolate(a.X, b.X, t, mode, 0, 0, bezierOut.X, bezierIn.X),
                    FloatChannelInterpolator.Interpolate(a.Y, b.Y, t, mode, 0, 0, bezierOut.Y, bezierIn.Y),
                    FloatChannelInterpolator.Interpolate(a.Z, b.Z, t, mode, 0, 0, bezierOut.Z, bezierIn.Z)),
                _ => Vector3.Lerp(a, b, t)
            };
        }
    }

    /// <summary>
    /// Quaternion 通道插值器。Constant 使用前一帧，其余模式使用 SLERP。
    /// </summary>
    public static class QuaternionChannelInterpolator
    {
        public static Quaternion Interpolate(
            Quaternion a, Quaternion b, float t,
            CinematicInterpolationMode mode,
            Quaternion outTangent, Quaternion inTangent,
            Quaternion bezierOut, Quaternion bezierIn)
        {
            if (mode == CinematicInterpolationMode.Constant) return a;

            // 选择最短弧
            if (Quaternion.Dot(a, b) < 0f) b = Quaternion.Negate(b);

            float smoothT = mode switch
            {
                CinematicInterpolationMode.Cubic => t * t * (3f - 2f * t),
                CinematicInterpolationMode.Hermite => t * t * (3f - 2f * t), // 退化为平滑
                _ => t
            };

            return Quaternion.Slerp(a, b, smoothT);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 片段 (Section) —— 轨道上的时间区间
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列器片段。表示轨道上的一个激活区间 [StartTime, StartTime + Duration)。
    /// 每个片段可包含自身的通道数据，也可引用外部资源（如动画剪辑）。
    /// </summary>
    public class CinematicSection
    {
        /// <summary>片段在序列时间轴上的起始时间（秒）。</summary>
        public float StartTime { get; set; }

        /// <summary>片段持续时长（秒）。</summary>
        public float Duration { get; set; } = 1f;

        /// <summary>结束时间（计算属性）。</summary>
        public float EndTime => StartTime + Duration;

        /// <summary>所属轨道引用。</summary>
        public CinematicTrack? OwnerTrack { get; set; }

        /// <summary>片段内部时间缩放。</summary>
        public float TimeScale { get; set; } = 1f;

        /// <summary>片段内部是否循环（用于短动画/音频铺设）。</summary>
        public bool Loop { get; set; }

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>显示名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>用户数据（存储片段特定的元数据）。</summary>
        public object? Tag { get; set; }

        /// <summary>判断给定序列时间是否落在片段的激活区间内。</summary>
        public bool IsTimeInRange(float sequenceTime)
        {
            return sequenceTime >= StartTime && sequenceTime < EndTime;
        }

        /// <summary>
        /// 将序列全局时间映射为片段本地时间。
        /// </summary>
        /// <param name="sequenceTime">序列全局时间。</param>
        /// <param name="localTime">输出本地时间。</param>
        /// <returns>是否在激活区间内。</returns>
        public bool TryMapToLocalTime(float sequenceTime, out float localTime)
        {
            localTime = 0f;
            if (!IsTimeInRange(sequenceTime) || !IsEnabled) return false;

            float raw = (sequenceTime - StartTime) * TimeScale;
            if (Loop && Duration > 1e-5f)
            {
                localTime = raw % Duration;
            }
            else
            {
                localTime = MathF.Min(raw, Duration);
            }
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 轨道 (Track) 抽象基类与具体实现
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列器轨道抽象基类。每条轨道包含多个片段，并在每帧由评估器调用求值。
    /// </summary>
    public abstract class CinematicTrack
    {
        /// <summary>轨道显示名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>轨道类型。</summary>
        public abstract CinematicTrackType TrackType { get; }

        /// <summary>是否启用（静音/取消静音）。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>是否锁定（编辑器中不可编辑）。</summary>
        public bool IsLocked { get; set; }

        /// <summary>轨道排序权重（影响评估顺序）。</summary>
        public int SortOrder { get; set; }

        /// <summary>所属序列引用。</summary>
        public CinematicSequence? OwnerSequence { get; set; }

        /// <summary>该轨道上的片段列表。</summary>
        public readonly List<CinematicSection> Sections = new();

        /// <summary>添加片段到轨道。</summary>
        public CinematicSection AddSection(float startTime, float duration)
        {
            var section = new CinematicSection
            {
                StartTime = startTime,
                Duration = duration,
                OwnerTrack = this
            };
            Sections.Add(section);
            return section;
        }

        /// <summary>移除指定片段。</summary>
        public bool RemoveSection(CinematicSection section)
        {
            section.OwnerTrack = null;
            return Sections.Remove(section);
        }

        /// <summary>
        /// 由评估器在每帧调用。序列时间作为参数传入。
        /// </summary>
        /// <param name="sequenceTime">序列全局时间（秒）。</param>
        /// <param name="deltaTime">与上一帧的时间差。</param>
        public abstract void Evaluate(float sequenceTime, float deltaTime);

        /// <summary>重置轨道的运行时状态。</summary>
        public virtual void Reset() { }

        /// <summary>获取轨道的时间范围（所有片段的并集）。</summary>
        public void GetTimeRange(out float startTime, out float endTime)
        {
            startTime = float.MaxValue;
            endTime = float.MinValue;
            foreach (var sec in Sections)
            {
                if (sec.StartTime < startTime) startTime = sec.StartTime;
                if (sec.EndTime > endTime) endTime = sec.EndTime;
            }
            if (startTime > endTime) { startTime = 0f; endTime = 0f; }
        }
    }

    /// <summary>
    /// Actor 变换轨道。驱动一个 Actor 的位置、旋转、缩放。
    /// 通过独立的 float 通道或 Vector3/Quaternion 通道采样。
    /// </summary>
    public class ActorTrack : CinematicTrack
    {
        public override CinematicTrackType TrackType => CinematicTrackType.Actor;

        /// <summary>目标 Actor 名称/ID。</summary>
        public string ActorBinding { get; set; } = string.Empty;

        /// <summary>位置通道（Vector3）。</summary>
        public CinematicChannel<Vector3> PositionChannel { get; } = new()
        {
            Interpolator = Vector3ChannelInterpolator.Interpolate
        };

        /// <summary>旋转通道（Quaternion）。</summary>
        public CinematicChannel<Quaternion> RotationChannel { get; } = new()
        {
            Interpolator = QuaternionChannelInterpolator.Interpolate
        };

        /// <summary>缩放通道（Vector3）。</summary>
        public CinematicChannel<Vector3> ScaleChannel { get; } = new()
        {
            Interpolator = Vector3ChannelInterpolator.Interpolate
        };

        /// <summary>是否启用位置驱动。</summary>
        public bool EnablePosition { get; set; } = true;

        /// <summary>是否启用旋转驱动。</summary>
        public bool EnableRotation { get; set; } = true;

        /// <summary>是否启用缩放驱动。</summary>
        public bool EnableScale { get; set; } = true;

        // 回调：将求值结果应用到目标 Actor
        /// <summary>设置位置回调。</summary>
        public Action<Vector3>? SetPosition;
        /// <summary>设置旋转回调。</summary>
        public Action<Quaternion>? SetRotation;
        /// <summary>设置缩放回调。</summary>
        public Action<Vector3>? SetScale;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!IsEnabled) return;

            if (EnablePosition && !PositionChannel.IsEmpty)
            {
                Vector3 pos = PositionChannel.EvaluateAtTime(sequenceTime);
                SetPosition?.Invoke(pos);
            }

            if (EnableRotation && !RotationChannel.IsEmpty)
            {
                Quaternion rot = RotationChannel.EvaluateAtTime(sequenceTime);
                SetRotation?.Invoke(rot);
            }

            if (EnableScale && !ScaleChannel.IsEmpty)
            {
                Vector3 scl = ScaleChannel.EvaluateAtTime(sequenceTime);
                SetScale?.Invoke(scl);
            }
        }
    }

    /// <summary>
    /// 相机轨道。包含相机切换点，支持多机位编辑。
    /// 具体混合逻辑由 <see cref="CameraCutSystem"/> 处理。
    /// </summary>
    public class CameraTrack : CinematicTrack
    {
        public override CinematicTrackType TrackType => CinematicTrackType.Camera;

        /// <summary>相机切换点列表。</summary>
        public readonly List<CameraCutPoint> CutPoints = new();

        /// <summary>添加相机切换点。</summary>
        public CameraCutPoint AddCutPoint(float time, string cameraBinding,
            CameraCutBlendMode blendMode = CameraCutBlendMode.Cut, float blendDuration = 0f)
        {
            var cut = new CameraCutPoint
            {
                Time = time,
                CameraBinding = cameraBinding,
                BlendMode = blendMode,
                BlendDuration = blendDuration
            };
            CutPoints.Add(cut);
            return cut;
        }

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!IsEnabled || CutPoints.Count == 0) return;
            // 实际的相机混合由 CameraCutSystem 统一处理
        }

        public override void Reset()
        {
            // 重置运行时混合状态
        }
    }

    /// <summary>
    /// 相机切换点。定义在指定时刻从当前相机切换到目标相机。
    /// </summary>
    public struct CameraCutPoint
    {
        /// <summary>切换时间（秒）。</summary>
        public float Time;

        /// <summary>目标相机绑定名称。</summary>
        public string CameraBinding;

        /// <summary>混合模式。</summary>
        public CameraCutBlendMode BlendMode;

        /// <summary>混合持续时长（秒）。</summary>
        public float BlendDuration;

        /// <summary>滑动方向（仅 Slide 模式有效）。</summary>
        public SlideDirection SlideDir;

        /// <summary>Fade 颜色（仅 Fade 模式有效）。</summary>
        public Vector3 FadeColor;
    }

    /// <summary>
    /// 音频轨道。控制音频片段在序列中的播放。
    /// </summary>
    public class AudioTrack : CinematicTrack
    {
        public override CinematicTrackType TrackType => CinematicTrackType.Audio;

        /// <summary>音频资源路径/名称。</summary>
        public string AudioAsset { get; set; } = string.Empty;

        /// <summary>音量通道（0~1）。</summary>
        public CinematicChannel<float> VolumeChannel { get; } = new()
        {
            Interpolator = FloatChannelInterpolator.Interpolate
        };

        /// <summary>音高通道（倍率）。</summary>
        public CinematicChannel<float> PitchChannel { get; } = new()
        {
            Interpolator = FloatChannelInterpolator.Interpolate
        };

        /// <summary>是否 3D 空间化。</summary>
        public bool IsSpatial { get; set; }

        /// <summary>3D 播放位置。</summary>
        public Vector3 WorldPosition { get; set; }

        /// <summary>音频播放回调。</summary>
        public Action<string, float, float, Vector3, bool>? PlayAudio;
        public Action? StopAudio;

        private bool _isPlaying;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!IsEnabled || Sections.Count == 0) return;

            bool anyActive = false;
            foreach (var section in Sections)
            {
                if (section.TryMapToLocalTime(sequenceTime, out float localTime))
                {
                    anyActive = true;
                    float volume = VolumeChannel.IsEmpty ? 1f : VolumeChannel.EvaluateAtTime(localTime);
                    float pitch = PitchChannel.IsEmpty ? 1f : PitchChannel.EvaluateAtTime(localTime);

                    if (!_isPlaying)
                    {
                        PlayAudio?.Invoke(AudioAsset, volume, pitch, WorldPosition, IsSpatial);
                        _isPlaying = true;
                    }
                    break;
                }
            }

            if (!anyActive && _isPlaying)
            {
                StopAudio?.Invoke();
                _isPlaying = false;
            }
        }

        public override void Reset()
        {
            StopAudio?.Invoke();
            _isPlaying = false;
        }
    }

    /// <summary>
    /// 事件轨道。在指定时刻触发自定义事件，携带多种参数负载。
    /// </summary>
    public class EventTrack : CinematicTrack
    {
        public override CinematicTrackType TrackType => CinematicTrackType.Event;

        /// <summary>事件定义：触发时间 + 名称 + 参数负载。</summary>
        public struct CinematicEvent
        {
            public float Time;
            public string EventName;
            public float FloatParam;
            public int IntParam;
            public string StringParam;
            internal bool Fired;
        }

        /// <summary>事件列表。</summary>
        public readonly List<CinematicEvent> Events = new();

        /// <summary>事件触发回调。</summary>
        public event Action<CinematicEvent>? OnEventTriggered;

        private float _lastEvalTime = -1f;

        /// <summary>添加事件。</summary>
        public void AddEvent(float time, string name,
            float floatParam = 0f, int intParam = 0, string stringParam = "")
        {
            Events.Add(new CinematicEvent
            {
                Time = time,
                EventName = name,
                FloatParam = floatParam,
                IntParam = intParam,
                StringParam = stringParam,
                Fired = false
            });
        }

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!IsEnabled) return;

            // 检测时间回退（跳转/倒退），重置已触发标志
            if (sequenceTime < _lastEvalTime - 0.01f)
            {
                for (int i = 0; i < Events.Count; i++)
                {
                    var ev = Events[i];
                    ev.Fired = false;
                    Events[i] = ev;
                }
            }

            for (int i = 0; i < Events.Count; i++)
            {
                var ev = Events[i];
                bool crossed = _lastEvalTime < 0f
                    ? MathF.Abs(sequenceTime - ev.Time) < MathF.Max(deltaTime, 1f / 60f) + 1e-3f
                    : ev.Time > _lastEvalTime - 1e-3f && ev.Time <= sequenceTime + 1e-3f;

                if (crossed && !ev.Fired)
                {
                    ev.Fired = true;
                    Events[i] = ev;
                    OnEventTriggered?.Invoke(ev);
                }
            }
            _lastEvalTime = sequenceTime;
        }

        public override void Reset()
        {
            for (int i = 0; i < Events.Count; i++)
            {
                var ev = Events[i];
                ev.Fired = false;
                Events[i] = ev;
            }
            _lastEvalTime = -1f;
        }
    }

    /// <summary>
    /// 动画轨道。驱动角色骨骼动画的播放，支持混合。
    /// </summary>
    public class AnimationTrack : CinematicTrack
    {
        public override CinematicTrackType TrackType => CinematicTrackType.Animation;

        /// <summary>动画资源名称。</summary>
        public string AnimationAsset { get; set; } = string.Empty;

        /// <summary>目标角色绑定。</summary>
        public string ActorBinding { get; set; } = string.Empty;

        /// <summary>播放速率通道。</summary>
        public CinematicChannel<float> PlayRateChannel { get; } = new()
        {
            Interpolator = FloatChannelInterpolator.Interpolate
        };

        /// <summary>混合权重通道。</summary>
        public CinematicChannel<float> BlendWeightChannel { get; } = new()
        {
            Interpolator = FloatChannelInterpolator.Interpolate
        };

        /// <summary>动画开始偏移（秒）。</summary>
        public float StartOffset { get; set; }

        /// <summary>动画播放回调。</summary>
        public Action<string, string, float, float, float>? PlayAnimation;
        public Action? StopAnimation;

        private bool _isPlaying;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!IsEnabled || Sections.Count == 0) return;

            foreach (var section in Sections)
            {
                if (section.TryMapToLocalTime(sequenceTime, out float localTime))
                {
                    float playRate = PlayRateChannel.IsEmpty
                        ? 1f : PlayRateChannel.EvaluateAtTime(sequenceTime);
                    float blendWeight = BlendWeightChannel.IsEmpty
                        ? 1f : BlendWeightChannel.EvaluateAtTime(sequenceTime);

                    PlayAnimation?.Invoke(AnimationAsset, ActorBinding,
                        localTime + StartOffset, playRate, blendWeight);
                    _isPlaying = true;
                    return;
                }
            }

            if (_isPlaying)
            {
                StopAnimation?.Invoke();
                _isPlaying = false;
            }
        }

        public override void Reset()
        {
            StopAnimation?.Invoke();
            _isPlaying = false;
        }
    }

    /// <summary>
    /// 材质轨道。驱动材质参数（标量/向量）的动画。
    /// </summary>
    public class MaterialTrack : CinematicTrack
    {
        public override CinematicTrackType TrackType => CinematicTrackType.Material;

        /// <summary>目标材质名称。</summary>
        public string MaterialBinding { get; set; } = string.Empty;

        /// <summary>标量参数通道集合：参数名 => 通道。</summary>
        public readonly Dictionary<string, CinematicChannel<float>> ScalarChannels = new();

        /// <summary>向量参数通道集合：参数名 => 通道。</summary>
        public readonly Dictionary<string, CinematicChannel<Vector3>> VectorChannels = new();

        /// <summary>颜色参数通道集合：参数名 => 通道。</summary>
        public readonly Dictionary<string, CinematicChannel<Vector4>> ColorChannels = new();

        /// <summary>材质参数设置回调。</summary>
        public Action<string, string, float>? SetScalarParam;
        public Action<string, string, Vector3>? SetVectorParam;
        public Action<string, string, Vector4>? SetColorParam;

        /// <summary>添加标量参数通道。</summary>
        public CinematicChannel<float> AddScalarChannel(string paramName)
        {
            var channel = new CinematicChannel<float>
            {
                Interpolator = FloatChannelInterpolator.Interpolate
            };
            ScalarChannels[paramName] = channel;
            return channel;
        }

        /// <summary>添加向量参数通道。</summary>
        public CinematicChannel<Vector3> AddVectorChannel(string paramName)
        {
            var channel = new CinematicChannel<Vector3>
            {
                Interpolator = Vector3ChannelInterpolator.Interpolate
            };
            VectorChannels[paramName] = channel;
            return channel;
        }

        /// <summary>添加颜色参数通道。</summary>
        public CinematicChannel<Vector4> AddColorChannel(string paramName)
        {
            var channel = new CinematicChannel<Vector4>
            {
                Interpolator = null // 使用默认线性插值
            };
            ColorChannels[paramName] = channel;
            return channel;
        }

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!IsEnabled) return;

            foreach (var kvp in ScalarChannels)
            {
                if (!kvp.Value.IsEmpty)
                    SetScalarParam?.Invoke(MaterialBinding, kvp.Key,
                        kvp.Value.EvaluateAtTime(sequenceTime));
            }

            foreach (var kvp in VectorChannels)
            {
                if (!kvp.Value.IsEmpty)
                    SetVectorParam?.Invoke(MaterialBinding, kvp.Key,
                        kvp.Value.EvaluateAtTime(sequenceTime));
            }

            foreach (var kvp in ColorChannels)
            {
                if (!kvp.Value.IsEmpty)
                    SetColorParam?.Invoke(MaterialBinding, kvp.Key,
                        kvp.Value.EvaluateAtTime(sequenceTime));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 子序列 (SubSequence)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 子序列轨道。嵌套引用另一个 CinematicSequence，支持偏移和缩放。
    /// </summary>
    public class SubSequenceTrack : CinematicTrack
    {
        public override CinematicTrackType TrackType => CinematicTrackType.SubSequence;

        /// <summary>被引用的子序列。</summary>
        public CinematicSequence? SubSequence { get; set; }

        /// <summary>子序列在父序列中的起始偏移（秒）。</summary>
        public float Offset { get; set; }

        /// <summary>子序列播放速率。</summary>
        public float TimeScale { get; set; } = 1f;

        /// <summary>子序列是否循环（在父序列时长范围内）。</summary>
        public bool Loop { get; set; }

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!IsEnabled || SubSequence == null) return;

            float subTime = (sequenceTime - Offset) * TimeScale;

            if (Loop)
            {
                subTime = SubSequence.Duration > 1e-5f
                    ? ((subTime % SubSequence.Duration) + SubSequence.Duration) % SubSequence.Duration
                    : 0f;
            }

            // 在子序列的时间范围内才评估
            if (subTime >= 0f && subTime <= SubSequence.Duration)
            {
                SubSequence.Evaluator.Evaluate(subTime, deltaTime);
            }
        }

        public override void Reset()
        {
            SubSequence?.Reset();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 序列 (Sequence)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列。核心容器，包含多个轨道、全局时间参数和元数据。
    /// 一个序列即为一段可播放的"电影胶片"。
    /// </summary>
    public class CinematicSequence
    {
        /// <summary>序列名称。</summary>
        public string Name { get; set; } = "Untitled Sequence";

        /// <summary>序列时长（秒）。自动取所有轨道的最大结束时间。</summary>
        public float Duration
        {
            get
            {
                if (_tracks.Count == 0) return _fixedDuration;
                float maxEnd = _fixedDuration;
                foreach (var track in _tracks)
                {
                    track.GetTimeRange(out float start, out float end);
                    if (end > maxEnd) maxEnd = end;
                }
                return maxEnd;
            }
            set => _fixedDuration = value;
        }
        private float _fixedDuration = 10f;

        /// <summary>播放速率倍率。</summary>
        public float PlaybackRate { get; set; } = 1f;

        /// <summary>循环模式。</summary>
        public CinematicLoopMode LoopMode { get; set; } = CinematicLoopMode.None;

        /// <summary>轨道列表。</summary>
        public IReadOnlyList<CinematicTrack> Tracks => _tracks;

        /// <summary>序列器评估器。</summary>
        public SequencerEvaluator Evaluator { get; }

        /// <summary>序列器播放器。</summary>
        public SequencerPlayer Player { get; }

        /// <summary>相机切轨系统引用。</summary>
        public CameraCutSystem CameraCuts { get; }

        /// <summary>事件系统引用。</summary>
        public CinematicEventSystem EventSystem { get; }

        /// <summary>电影导演引用。</summary>
        public CinematicDirector? Director { get; set; }

        private readonly List<CinematicTrack> _tracks = new();

        public CinematicSequence()
        {
            Evaluator = new SequencerEvaluator(this);
            Player = new SequencerPlayer(this);
            CameraCuts = new CameraCutSystem();
            EventSystem = new CinematicEventSystem();
        }

        /// <summary>添加轨道到序列。</summary>
        public T AddTrack<T>(T track) where T : CinematicTrack
        {
            track.OwnerSequence = this;
            _tracks.Add(track);
            // 按排序权重排序
            _tracks.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            return track;
        }

        /// <summary>按类型查找第一个匹配的轨道。</summary>
        public T? FindTrack<T>(CinematicTrackType type) where T : CinematicTrack
        {
            foreach (var track in _tracks)
            {
                if (track.TrackType == type && track is T typed)
                    return typed;
            }
            return null;
        }

        /// <summary>查找所有指定类型的轨道。</summary>
        public List<T> FindAllTracks<T>(CinematicTrackType type) where T : CinematicTrack
        {
            var result = new List<T>();
            foreach (var track in _tracks)
            {
                if (track.TrackType == type && track is T typed)
                    result.Add(typed);
            }
            return result;
        }

        /// <summary>移除轨道。</summary>
        public bool RemoveTrack(CinematicTrack track)
        {
            track.OwnerSequence = null;
            return _tracks.Remove(track);
        }

        /// <summary>重置序列所有状态。</summary>
        public void Reset()
        {
            Player.Stop();
            foreach (var track in _tracks)
                track.Reset();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 序列器播放器 (Sequencer Player)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列器播放器。控制序列的播放/暂停/停止/跳转/循环逻辑。
    /// </summary>
    public class SequencerPlayer
    {
        private readonly CinematicSequence _sequence;

        /// <summary>当前播放状态。</summary>
        public CinematicPlaybackState State { get; private set; } = CinematicPlaybackState.Stopped;

        /// <summary>当前播放头位置（秒）。</summary>
        public float CurrentTime { get; private set; }

        /// <summary>播放开始时的时间（用于 PingPong 反向判断）。</summary>
        private bool _pingPongForward = true;

        /// <summary>播放状态变更事件。</summary>
        public event Action<CinematicPlaybackState>? OnStateChanged;

        /// <summary>播放完成事件。</summary>
        public event Action? OnPlaybackCompleted;

        /// <summary>播放时间更新事件（每帧触发）。</summary>
        public event Action<float>? OnTimeUpdated;

        public SequencerPlayer(CinematicSequence sequence)
        {
            _sequence = sequence;
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            if (State == CinematicPlaybackState.Playing) return;

            if (State == CinematicPlaybackState.Stopped)
            {
                CurrentTime = 0f;
                _pingPongForward = true;
                // 重置所有轨道
                foreach (var track in _sequence.Tracks)
                    track.Reset();
            }

            State = CinematicPlaybackState.Playing;
            OnStateChanged?.Invoke(State);

            // 触发导演模式
            _sequence.Director?.EnterCinematicMode();
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
            if (State != CinematicPlaybackState.Playing) return;
            State = CinematicPlaybackState.Paused;
            OnStateChanged?.Invoke(State);
        }

        /// <summary>恢复播放。</summary>
        public void Resume()
        {
            if (State != CinematicPlaybackState.Paused) return;
            State = CinematicPlaybackState.Playing;
            OnStateChanged?.Invoke(State);
        }

        /// <summary>停止播放，重置播放头。</summary>
        public void Stop()
        {
            bool wasPlaying = State == CinematicPlaybackState.Playing;
            State = CinematicPlaybackState.Stopped;
            CurrentTime = 0f;
            _pingPongForward = true;
            OnStateChanged?.Invoke(State);

            // 重置所有轨道
            foreach (var track in _sequence.Tracks)
                track.Reset();

            // 退出导演模式
            if (wasPlaying)
                _sequence.Director?.ExitCinematicMode();
        }

        /// <summary>跳转到指定时间（梳理/Scrub）。</summary>
        public void Scrub(float time)
        {
            float prevTime = CurrentTime;
            CurrentTime = Math.Clamp(time, 0f, _sequence.Duration);
            State = CinematicPlaybackState.Scrubbing;
            OnStateChanged?.Invoke(State);

            // 检测是否需要重置事件触发标志
            if (CurrentTime < prevTime - 0.01f)
            {
                foreach (var track in _sequence.Tracks)
                    track.Reset();
            }

            OnTimeUpdated?.Invoke(CurrentTime);
        }

        /// <summary>每帧更新。由外部游戏循环调用。</summary>
        /// <param name="realDeltaTime">实际帧间隔（秒）。</param>
        public void Tick(float realDeltaTime)
        {
            if (State != CinematicPlaybackState.Playing) return;

            float scaledDelta = realDeltaTime * _sequence.PlaybackRate;

            switch (_sequence.LoopMode)
            {
                case CinematicLoopMode.None:
                    CurrentTime += scaledDelta;
                    if (CurrentTime >= _sequence.Duration)
                    {
                        CurrentTime = _sequence.Duration;
                        OnPlaybackCompleted?.Invoke();
                        Stop();
                        return;
                    }
                    break;

                case CinematicLoopMode.Loop:
                    CurrentTime += scaledDelta;
                    if (_sequence.Duration > 1e-5f)
                    {
                        if (CurrentTime >= _sequence.Duration)
                        {
                            CurrentTime -= _sequence.Duration;
                            // 循环时重置事件
                            foreach (var track in _sequence.Tracks)
                                track.Reset();
                        }
                        else if (CurrentTime < 0f)
                        {
                            CurrentTime += _sequence.Duration;
                            foreach (var track in _sequence.Tracks)
                                track.Reset();
                        }
                    }
                    break;

                case CinematicLoopMode.PingPong:
                    if (_pingPongForward)
                    {
                        CurrentTime += scaledDelta;
                        if (CurrentTime >= _sequence.Duration)
                        {
                            CurrentTime = _sequence.Duration;
                            _pingPongForward = false;
                            foreach (var track in _sequence.Tracks)
                                track.Reset();
                        }
                    }
                    else
                    {
                        CurrentTime -= scaledDelta;
                        if (CurrentTime <= 0f)
                        {
                            CurrentTime = 0f;
                            _pingPongForward = true;
                            foreach (var track in _sequence.Tracks)
                                track.Reset();
                        }
                    }
                    break;
            }

            OnTimeUpdated?.Invoke(CurrentTime);
        }

        /// <summary>设置播放速率。</summary>
        public void SetPlaybackRate(float rate)
        {
            _sequence.PlaybackRate = Math.Max(0.01f, rate);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 序列器评估器 (Sequencer Evaluator)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列器评估器。在每帧按当前播放时间评估所有轨道，
    /// 将关键帧曲线的求值结果驱动到目标 Actor 属性上。
    /// </summary>
    public class SequencerEvaluator
    {
        private readonly CinematicSequence _sequence;

        /// <summary>评估开始前的回调。</summary>
        public event Action<float>? OnPreEvaluate;

        /// <summary>评估完成后的回调。</summary>
        public event Action<float>? OnPostEvaluate;

        /// <summary>是否正在评估。</summary>
        public bool IsEvaluating { get; private set; }

        public SequencerEvaluator(CinematicSequence sequence)
        {
            _sequence = sequence;
        }

        /// <summary>
        /// 在指定时间评估整个序列。由播放器每帧调用。
        /// </summary>
        /// <param name="time">当前播放时间（秒）。</param>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        public void Evaluate(float time, float deltaTime)
        {
            IsEvaluating = true;
            OnPreEvaluate?.Invoke(time);

            // 按轨道排序权重依次评估
            foreach (var track in _sequence.Tracks)
            {
                if (!track.IsEnabled) continue;

                // 检查轨道是否有活跃的片段区间
                bool hasActiveSection = track.Sections.Count == 0; // 无片段约束的轨道始终评估
                if (!hasActiveSection)
                {
                    foreach (var section in track.Sections)
                    {
                        if (section.IsEnabled && section.IsTimeInRange(time))
                        {
                            hasActiveSection = true;
                            break;
                        }
                    }
                }

                if (hasActiveSection)
                    track.Evaluate(time, deltaTime);
            }

            // 评估相机切轨系统
            _sequence.CameraCuts.Evaluate(time, deltaTime);

            OnPostEvaluate?.Invoke(time);
            IsEvaluating = false;
        }

        /// <summary>
        /// 仅评估特定轨道。
        /// </summary>
        public void EvaluateTrack(CinematicTrack track, float time, float deltaTime)
        {
            if (!track.IsEnabled) return;
            track.Evaluate(time, deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 相机切轨系统 (Camera Cut System)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 相机切轨系统。管理多条 CameraTrack 之间的切换与混合。
    /// 支持四种混合模式：硬切(Cut)、混合(Blend)、淡入淡出(Fade)、滑动(Slide)。
    /// </summary>
    public class CameraCutSystem
    {
        /// <summary>混合状态信息。</summary>
        public struct BlendState
        {
            /// <summary>是否正在混合中。</summary>
            public bool IsBlending;

            /// <summary>混合进度 [0, 1]。</summary>
            public float Progress;

            /// <summary>源相机绑定。</summary>
            public string FromCamera;

            /// <summary>目标相机绑定。</summary>
            public string ToCamera;

            /// <summary>混合模式。</summary>
            public CameraCutBlendMode BlendMode;

            /// <summary>混合总时长。</summary>
            public float TotalDuration;

            /// <summary>Fade 颜色。</summary>
            public Vector3 FadeColor;

            /// <summary>滑动方向。</summary>
            public SlideDirection SlideDir;
        }

        /// <summary>当前活跃相机绑定名称。</summary>
        public string ActiveCameraBinding { get; private set; } = string.Empty;

        /// <summary>当前混合状态。</summary>
        public BlendState CurrentBlend { get; private set; }

        /// <summary>相机切换回调：通知渲染器切换到指定相机。</summary>
        public event Action<string>? OnCameraSwitch;

        /// <summary>相机混合回调：通知渲染器执行混合。</summary>
        public event Action<BlendState>? OnCameraBlend;

        /// <summary>Fade 进度回调（0=无遮罩, 1=全黑, 中间值=部分遮罩）。</summary>
        public event Action<float, Vector3>? OnFadeProgress;

        /// <summary>Slide 进度回调。</summary>
        public event Action<float, SlideDirection>? OnSlideProgress;

        private int _lastCutIndex = -1;

        /// <summary>
        /// 评估相机切轨。收集所有 CameraTrack 的切换点，确定当前应激活的相机。
        /// </summary>
        public void Evaluate(float sequenceTime, float deltaTime)
        {
            // 收集所有 CameraTrack 的切换点（需要外部提供轨道集合）
            // 此处由序列评估器在 Evaluate 时间接调用
            // 实际使用中通过 UpdateCutPoints 方法注入
        }

        /// <summary>
        /// 从 CameraTrack 切换点列表中评估当前相机状态。
        /// </summary>
        public void EvaluateFromCutPoints(IReadOnlyList<CameraCutPoint> cutPoints,
            float sequenceTime, float deltaTime)
        {
            if (cutPoints.Count == 0) return;

            // 找到当前时间之前最后一个切换点
            int activeIdx = -1;
            for (int i = 0; i < cutPoints.Count; i++)
            {
                if (cutPoints[i].Time <= sequenceTime + 1e-4f)
                    activeIdx = i;
                else
                    break;
            }

            if (activeIdx < 0) return;

            var cut = cutPoints[activeIdx];

            // 检测是否发生了新的切换
            if (activeIdx != _lastCutIndex)
            {
                string prevCam = ActiveCameraBinding;
                ActiveCameraBinding = cut.CameraBinding;

                // 启动混合
                if (cut.BlendMode != CameraCutBlendMode.Cut && cut.BlendDuration > 1e-5f)
                {
                    CurrentBlend = new BlendState
                    {
                        IsBlending = true,
                        Progress = 0f,
                        FromCamera = prevCam,
                        ToCamera = cut.CameraBinding,
                        BlendMode = cut.BlendMode,
                        TotalDuration = cut.BlendDuration,
                        FadeColor = cut.FadeColor,
                        SlideDir = cut.SlideDir
                    };
                }
                else
                {
                    // 硬切
                    CurrentBlend = new BlendState { IsBlending = false };
                    OnCameraSwitch?.Invoke(ActiveCameraBinding);
                }

                _lastCutIndex = activeIdx;
            }

            // 推进混合进度
            if (CurrentBlend.IsBlending)
            {
                CurrentBlend.Progress += deltaTime / CurrentBlend.TotalDuration;
                if (CurrentBlend.Progress >= 1f)
                {
                    CurrentBlend.Progress = 1f;
                    CurrentBlend.IsBlending = false;
                    OnCameraSwitch?.Invoke(ActiveCameraBinding);
                }
                else
                {
                    // 根据混合模式分发回调
                    switch (CurrentBlend.BlendMode)
                    {
                        case CameraCutBlendMode.Blend:
                            OnCameraBlend?.Invoke(CurrentBlend);
                            break;

                        case CameraCutBlendMode.Fade:
                            // Fade 分两阶段：前半段淡出到黑，后半段淡入
                            float fadeT;
                            if (CurrentBlend.Progress < 0.5f)
                                fadeT = CurrentBlend.Progress * 2f;
                            else
                                fadeT = (1f - CurrentBlend.Progress) * 2f;
                            OnFadeProgress?.Invoke(fadeT, CurrentBlend.FadeColor);
                            break;

                        case CameraCutBlendMode.Slide:
                            OnSlideProgress?.Invoke(CurrentBlend.Progress, CurrentBlend.SlideDir);
                            break;
                    }
                }
            }
        }

        /// <summary>重置相机切轨状态。</summary>
        public void Reset()
        {
            ActiveCameraBinding = string.Empty;
            CurrentBlend = new BlendState();
            _lastCutIndex = -1;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 序列器事件系统 (Cinematic Event System)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影序列器事件系统。集中管理所有轨道上的事件触发，
    /// 提供统一的注册、注销和分发机制。
    /// </summary>
    public class CinematicEventSystem
    {
        /// <summary>事件处理器委托。</summary>
        public delegate void CinematicEventHandler(
            string eventName, float floatParam, int intParam, string stringParam);

        /// <summary>已注册的事件处理器：事件名 => 处理器列表。</summary>
        private readonly Dictionary<string, List<CinematicEventHandler>> _handlers = new();

        /// <summary>全局事件处理器（接收所有事件）。</summary>
        public event CinematicEventHandler? OnAnyEvent;

        /// <summary>注册事件处理器。</summary>
        public void RegisterHandler(string eventName, CinematicEventHandler handler)
        {
            if (!_handlers.TryGetValue(eventName, out var list))
            {
                list = new List<CinematicEventHandler>();
                _handlers[eventName] = list;
            }
            list.Add(handler);
        }

        /// <summary>注销事件处理器。</summary>
        public bool UnregisterHandler(string eventName, CinematicEventHandler handler)
        {
            if (_handlers.TryGetValue(eventName, out var list))
                return list.Remove(handler);
            return false;
        }

        /// <summary>触发事件。由 EventTrack 在评估时调用。</summary>
        public void FireEvent(string eventName,
            float floatParam = 0f, int intParam = 0, string stringParam = "")
        {
            // 调用命名处理器
            if (_handlers.TryGetValue(eventName, out var handlers))
            {
                for (int i = 0; i < handlers.Count; i++)
                    handlers[i](eventName, floatParam, intParam, stringParam);
            }

            // 调用全局处理器
            OnAnyEvent?.Invoke(eventName, floatParam, intParam, stringParam);
        }

        /// <summary>清除所有处理器。</summary>
        public void ClearAllHandlers()
        {
            _handlers.Clear();
        }

        /// <summary>将序列中所有 EventTrack 的事件路由到此系统。</summary>
        public void BindToSequence(CinematicSequence sequence)
        {
            var eventTracks = sequence.FindAllTracks<EventTrack>(CinematicTrackType.Event);
            foreach (var track in eventTracks)
            {
                track.OnEventTriggered += (ev) =>
                {
                    FireEvent(ev.EventName, ev.FloatParam, ev.IntParam, ev.StringParam);
                };
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 电影导演模式 (Cinematic Director)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影导演。控制全屏电影播放模式：隐藏 HUD、锁定玩家输入、
    /// 设置宽高比、播放完成后自动恢复。
    /// </summary>
    public class CinematicDirector
    {
        /// <summary>导演模式状态。</summary>
        public bool IsInCinematicMode { get; private set; }

        /// <summary>电影播放时是否隐藏 HUD。</summary>
        public bool HideHUD { get; set; } = true;

        /// <summary>电影播放时是否锁定玩家输入。</summary>
        public bool LockPlayerInput { get; set; } = true;

        /// <summary>电影播放时是否使用全屏宽高比（如 2.39:1）。</summary>
        public bool UseLetterbox { get; set; } = true;

        /// <summary>宽高比（宽度 / 高度）。默认 2.39:1 电影比例。</summary>
        public float LetterboxAspectRatio { get; set; } = 2.39f;

        /// <summary>是否允许玩家跳过过场（按 Esc / 空格）。</summary>
        public bool AllowSkip { get; set; } = true;

        /// <summary>进入电影模式回调。</summary>
        public event Action? OnEnterCinematicMode;

        /// <summary>退出电影模式回调。</summary>
        public event Action? OnExitCinematicMode;

        /// <summary>HUD 可见性切换回调。</summary>
        public event Action<bool>? OnHUDVisibilityChanged;

        /// <summary>玩家输入锁定切换回调。</summary>
        public event Action<bool>? OnPlayerInputLockChanged;

        /// <summary>宽银幕遮幅切换回调。</summary>
        public event Action<bool, float>? OnLetterboxChanged;

        /// <summary>跳过过场回调。</summary>
        public event Action? OnSkipRequested;

        /// <summary>关联的序列。</summary>
        private CinematicSequence? _sequence;

        public CinematicDirector() { }

        public CinematicDirector(CinematicSequence sequence)
        {
            _sequence = sequence;
        }

        /// <summary>关联序列。</summary>
        public void SetSequence(CinematicSequence sequence)
        {
            _sequence = sequence;
            sequence.Director = this;
        }

        /// <summary>进入电影模式。</summary>
        public void EnterCinematicMode()
        {
            if (IsInCinematicMode) return;
            IsInCinematicMode = true;

            OnEnterCinematicMode?.Invoke();

            if (HideHUD)
                OnHUDVisibilityChanged?.Invoke(false);

            if (LockPlayerInput)
                OnPlayerInputLockChanged?.Invoke(true);

            if (UseLetterbox)
                OnLetterboxChanged?.Invoke(true, LetterboxAspectRatio);
        }

        /// <summary>退出电影模式，恢复游戏状态。</summary>
        public void ExitCinematicMode()
        {
            if (!IsInCinematicMode) return;
            IsInCinematicMode = false;

            OnExitCinematicMode?.Invoke();

            if (HideHUD)
                OnHUDVisibilityChanged?.Invoke(true);

            if (LockPlayerInput)
                OnPlayerInputLockChanged?.Invoke(false);

            if (UseLetterbox)
                OnLetterboxChanged?.Invoke(false, 1f);
        }

        /// <summary>请求跳过过场。</summary>
        public void RequestSkip()
        {
            if (!IsInCinematicMode || !AllowSkip || _sequence == null) return;
            OnSkipRequested?.Invoke();
        }

        /// <summary>
        /// 播放过场动画。自动进入/退出电影模式。
        /// </summary>
        public void PlaySequence(CinematicSequence sequence)
        {
            SetSequence(sequence);
            sequence.Player.Play();
        }

        /// <summary>停止并恢复。</summary>
        public void StopSequence()
        {
            if (_sequence != null)
                _sequence.Player.Stop();
            ExitCinematicMode();
        }
    }
}
