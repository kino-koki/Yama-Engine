// =============================================================================
//  Tween.cs
//  yama引擎 - Tween 属性补间（对应 Godot 的 Tween + create_tween()）
//
//  对应 Godot 3 的 Tween 节点语义：
//    * create_tween() → Tween 播放器；tween_property(node, prop, final, duration)
//    * 四种过渡：Linear / Sine / Quad / Cubic（ease in / out / in-out）
//    * 每帧 Update(dt) 推进进度，把插值写回对象属性（Position/Rotation/Scale/Opacity）
//    * 并行/顺序：append（接在前序后）与 parallel（与前序同时）
//    * 循环：set_loops；完成回调：finished
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>过渡类型（对应 Godot Tween.TRANS_*）。</summary>
    public enum TweenTransition
    {
        Linear,
        Sine,
        Quad,
        Cubic,
    }

    /// <summary>缓动方向（对应 Godot Tween.EASE_*）。</summary>
    public enum TweenEase
    {
        In,
        Out,
        InOut,
    }

    /// <summary>补间的目标属性（对应 Godot tween_property 的 property 路径）。</summary>
    public enum TweenProperty
    {
        PositionX,
        PositionY,
        RotationDegrees,
        ScaleX,
        ScaleY,
        Opacity,
    }

    /// <summary>补间步骤类型（对应 Godot tween_property / tween_interval / tween_callback）。</summary>
    public enum TweenStepKind
    {
        Property,   // 属性补间
        Interval,   // 纯等待（对应 tween_interval）
        Callback,   // 回调（对应 tween_callback）
    }

    /// <summary>一条属性补间步骤。</summary>
    public sealed class TweenStep
    {
        /// <summary>步骤类型。</summary>
        public TweenStepKind Kind { get; set; } = TweenStepKind.Property;

        /// <summary>目标节点（Property 用）。</summary>
        public Node2D? Target { get; set; }

        /// <summary>要补间的属性（Property 用）。</summary>
        public TweenProperty Property { get; set; }

        /// <summary>起始值（Start 时从属性读快照）。</summary>
        public float From { get; internal set; }

        /// <summary>目标值（Property 用）。</summary>
        public float To { get; set; }

        /// <summary>时长（秒）。</summary>
        public float Duration { get; set; }

        /// <summary>过渡类型。</summary>
        public TweenTransition Transition { get; set; } = TweenTransition.Linear;

        /// <summary>缓动方向。</summary>
        public TweenEase Ease { get; set; } = TweenEase.InOut;

        /// <summary>是否与前一步并行（对应 tween.parallel()）。</summary>
        public bool Parallel { get; set; }

        /// <summary>已推进时间。</summary>
        public float Elapsed { get; internal set; }

        /// <summary>是否已完成。</summary>
        public bool Done { get; internal set; }

        /// <summary>回调动作（Callback 用）。</summary>
        public Action? Action { get; set; }
    }

    /// <summary>
    /// 属性补间播放器（对应 Godot 的 Tween）。驱动 Node2D 的位置/旋转/缩放/透明度。
    /// </summary>
    public sealed class Tween
    {
        private readonly List<TweenStep> _steps = new();
        private readonly Dictionary<TweenStep, float> _originalFrom = new();
        private int _activeIndex;
        private int _loopsLeft;
        private float _speedScale = 1f;
        private TweenState _state = TweenState.Idle;
        private int _finishedCount;
        private bool _startedOnce;
        private float _delayElapsed;
        private bool _delayDone;

        /// <summary>补间完成事件（对应 Godot Tween.finished 信号）。</summary>
        public event Action? Finished;

        /// <summary>补间播放器状态。</summary>
        public TweenState State => _state;

        /// <summary>总步骤数。</summary>
        public int StepCount => _steps.Count;

        /// <summary>速度倍数（0=暂停）。</summary>
        public float SpeedScale
        {
            get => _speedScale;
            set => _speedScale = value;
        }

        /// <summary>是否在播放。</summary>
        public bool IsRunning => _state == TweenState.Running;

        /// <summary>完成轮次计数（每完成一轮 +1）。</summary>
        public int FinishedCount => _finishedCount;

        /// <summary>剩余循环次数（-1 = 无限循环）。</summary>
        public int LoopsLeft => _loopsLeft;

        /// <summary>追加一条属性补间步骤（顺序，接在前序完成后）。</summary>
        public Tween TweenPropertyStep(Node2D target, TweenProperty property, float to, float duration)
        {
            _steps.Add(new TweenStep
            {
                Kind = TweenStepKind.Property,
                Target = target,
                Property = property,
                To = to,
                Duration = Math.Max(0f, duration),
            });
            return this;
        }

        /// <summary>追加纯等待步骤（对应 Godot tween_interval）。</summary>
        public Tween TweenInterval(float duration)
        {
            _steps.Add(new TweenStep
            {
                Kind = TweenStepKind.Interval,
                Duration = Math.Max(0f, duration),
            });
            return this;
        }

        /// <summary>追加回调步骤（对应 Godot tween_callback）。</summary>
        public Tween TweenCallback(Action action)
        {
            _steps.Add(new TweenStep
            {
                Kind = TweenStepKind.Callback,
                Duration = 0f,
                Action = action,
            });
            return this;
        }

        /// <summary>把最后一步改为与前一步并行（对应 tween.parallel()）。</summary>
        public Tween Parallel()
        {
            if (_steps.Count > 0)
                _steps[_steps.Count - 1].Parallel = true;
            return this;
        }

        /// <summary>
        /// 强制后续所有步骤与前一步并行（对应 Godot set_parallel(true)）。
        /// 与 Parallel() 区别：Parallel() 只改最后一步，set_parallel(true) 影响之后每次 TweenPropertyStep。
        /// </summary>
        public Tween SetParallel(bool parallel = true)
        {
            _defaultParallel = parallel;
            return this;
        }

        /// <summary>
        /// 链式模式：后续所有步骤顺序执行（对应 Godot chain()），关闭 set_parallel。
        /// </summary>
        public Tween Chain()
        {
            _defaultParallel = false;
            return this;
        }

        private bool _defaultParallel;

        /// <summary>设置最后一步的过渡与缓动。</summary>
        public Tween SetTrans(TweenTransition trans, TweenEase ease)
        {
            if (_steps.Count > 0)
            {
                _steps[_steps.Count - 1].Transition = trans;
                _steps[_steps.Count - 1].Ease = ease;
            }
            return this;
        }

        /// <summary>设置起始延迟（对应 Godot set_delay，Start 后先等 delay 再走步骤）。</summary>
        public Tween SetDelay(float delay)
        {
            _delay = MathF.Max(0f, delay);
            return this;
        }

        private float _delay;

        /// <summary>设置循环次数（0=不循环，-1=无限循环）。</summary>
        public Tween SetLoops(int loops)
        {
            _loopsLeft = loops;
            return this;
        }

        /// <summary>终止补间（对应 Godot kill()），状态置 Done，不再推进。</summary>
        public void Kill()
        {
            _state = TweenState.Done;
        }

        /// <summary>从属性读当前值。</summary>
        private static float ReadProperty(Node2D target, TweenProperty prop)
        {
            return prop switch
            {
                TweenProperty.PositionX => target.Position.X,
                TweenProperty.PositionY => target.Position.Y,
                TweenProperty.RotationDegrees => target.Rotation * (180f / MathF.PI),
                TweenProperty.ScaleX => target.Scale.X,
                TweenProperty.ScaleY => target.Scale.Y,
                TweenProperty.Opacity => target.Modulate.W,
                _ => 0f,
            };
        }

        /// <summary>把插值写回属性。</summary>
        private static void WriteProperty(Node2D target, TweenProperty prop, float value)
        {
            switch (prop)
            {
                case TweenProperty.PositionX:
                    target.Position = new Vector2(value, target.Position.Y);
                    break;
                case TweenProperty.PositionY:
                    target.Position = new Vector2(target.Position.X, value);
                    break;
                case TweenProperty.RotationDegrees:
                    target.Rotation = value * (MathF.PI / 180f);
                    break;
                case TweenProperty.ScaleX:
                    target.Scale = new Vector2(value, target.Scale.Y);
                    break;
                case TweenProperty.ScaleY:
                    target.Scale = new Vector2(target.Scale.X, value);
                    break;
                case TweenProperty.Opacity:
                    target.Modulate = new Vector4(target.Modulate.X, target.Modulate.Y, target.Modulate.Z, value);
                    break;
            }
        }

        /// <summary>缓动插值：返回 [0,1] 进度的过渡曲线值。</summary>
        public static float EaseValue(float t, TweenTransition trans, TweenEase ease)
        {
            t = Math.Clamp(t, 0f, 1f);
            float baseVal = trans switch
            {
                TweenTransition.Linear => t,
                TweenTransition.Sine => ease switch
                {
                    TweenEase.In => 1f - MathF.Cos(t * MathF.PI * 0.5f),
                    TweenEase.Out => MathF.Sin(t * MathF.PI * 0.5f),
                    _ => -(MathF.Cos(MathF.PI * t) - 1f) * 0.5f,
                },
                TweenTransition.Quad => ease switch
                {
                    TweenEase.In => t * t,
                    TweenEase.Out => 1f - (1f - t) * (1f - t),
                    _ => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) * 0.5f,
                },
                TweenTransition.Cubic => ease switch
                {
                    TweenEase.In => t * t * t,
                    TweenEase.Out => 1f - MathF.Pow(1f - t, 3f),
                    _ => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) * 0.5f,
                },
                _ => t,
            };
            return baseVal;
        }

        /// <summary>开始播放（读各步起始值快照，并快照原始起始值供循环复位）。</summary>
        public void Start()
        {
            foreach (var s in _steps)
            {
                if (s.Kind == TweenStepKind.Property)
                {
                    s.From = ReadProperty(s.Target!, s.Property);
                    if (!_startedOnce)
                        _originalFrom[s] = s.From;
                }
                s.Elapsed = 0f;
                s.Done = false;
            }
            _startedOnce = true;
            _activeIndex = 0;
            _delayElapsed = 0f;
            _delayDone = _delay <= 0f;
            _state = TweenState.Running;
            if (_loopsLeft == 0) _loopsLeft = 1; // 一轮
        }

        /// <summary>暂停。</summary>
        public void Pause() { if (_state == TweenState.Running) _state = TweenState.Paused; }

        /// <summary>恢复。</summary>
        public void Resume() { if (_state == TweenState.Paused) _state = TweenState.Running; }

        /// <summary>停止并复位。</summary>
        public void Stop()
        {
            _state = TweenState.Idle;
            _activeIndex = 0;
        }

        /// <summary>
        /// 每帧推进。顺序步骤串行推进；并行步骤与前序同步推进；
        /// 全部步骤完成后计一轮，按 loops 决定是否重播。
        /// </summary>
        public void Update(float dt)
        {
            if (_state != TweenState.Running || _steps.Count == 0) return;

            float scaledDt = dt * _speedScale;

            // 延迟阶段：未过 delay 先扣延迟
            if (!_delayDone)
            {
                _delayElapsed += scaledDt;
                if (_delayElapsed < _delay) return;
                // 本帧剩余时间继续走步骤（用溢出量简化：本帧不再扣，视为延迟结束）
                _delayDone = true;
            }

            // 一帧内连续推进并行组：某组瞬间完成（含 0 时长）则同帧前进到下一组
            while (_activeIndex < _steps.Count)
            {
                // 当前并行组：锚点 + 锚点之后连续 Parallel=true 的步骤
                int groupEnd = _activeIndex + 1;
                while (groupEnd < _steps.Count && _steps[groupEnd].Parallel)
                    groupEnd++;

                for (int i = _activeIndex; i < groupEnd; i++)
                {
                    var s = _steps[i];
                    if (s.Done) continue;

                    switch (s.Kind)
                    {
                        case TweenStepKind.Interval:
                        case TweenStepKind.Property:
                        {
                            s.Elapsed += scaledDt;
                            float dur = s.Duration <= 0f ? 0f : s.Duration;
                            float t = dur <= 0f ? 1f : Math.Min(s.Elapsed / dur, 1f);
                            if (s.Kind == TweenStepKind.Property)
                            {
                                float eased = EaseValue(t, s.Transition, s.Ease);
                                float value = s.From + (s.To - s.From) * eased;
                                WriteProperty(s.Target!, s.Property, value);
                            }
                            if (t >= 1f) s.Done = true;
                            break;
                        }
                        case TweenStepKind.Callback:
                        {
                            s.Action?.Invoke();
                            s.Done = true;
                            break;
                        }
                    }
                }

                // 处理后重判：本组是否全部完成
                bool groupDone = true;
                for (int i = _activeIndex; i < groupEnd; i++)
                {
                    if (!_steps[i].Done) { groupDone = false; break; }
                }

                if (!groupDone) break; // 本组仍在进行，留到下一帧
                _activeIndex = groupEnd; // 本组完成，同帧前进
            }

            // 本帧内若全部步骤完成，立即结算
            if (_activeIndex >= _steps.Count)
                SettleRound();
        }

        /// <summary>结算一轮：计数并按 loops 决定重播或结束（只在全部步骤完成时调用一次）。</summary>
        private void SettleRound()
        {
            _finishedCount++;
            if (_loopsLeft == -1)
            {
                ResetForLoop();
                return;
            }
            _loopsLeft--;
            if (_loopsLeft > 0)
                ResetForLoop();
            else
            {
                _state = TweenState.Done;
                Finished?.Invoke();
            }
        }

        private void ResetForLoop()
        {
            // 从原始起始值快照复位（不用当前已写到目标的值）
            foreach (var s in _steps)
            {
                if (s.Kind == TweenStepKind.Property)
                    s.From = _originalFrom.TryGetValue(s, out var orig) ? orig : ReadProperty(s.Target!, s.Property);
                s.Elapsed = 0f;
                s.Done = false;
            }
            _activeIndex = 0;
            _delayElapsed = 0f;
            _delayDone = _delay <= 0f;
        }
    }

    /// <summary>Tween 播放状态。</summary>
    public enum TweenState { Idle, Running, Paused, Done }

    /// <summary>
    /// Tween 工厂（对应 Godot Node.create_tween()）。持有活动 Tween 集合并统一 Update。
    /// </summary>
    public sealed class TweenFactory
    {
        private readonly List<Tween> _tweens = new();

        /// <summary>创建并注册一个新 Tween。</summary>
        public Tween CreateTween()
        {
            var t = new Tween();
            _tweens.Add(t);
            return t;
        }

        /// <summary>每帧推进所有活动 Tween，清理已完成的。</summary>
        public void Update(float dt)
        {
            for (int i = _tweens.Count - 1; i >= 0; i--)
            {
                var t = _tweens[i];
                t.Update(dt);
                if (t.State == TweenState.Done)
                    _tweens.RemoveAt(i);
            }
        }

        /// <summary>活动 Tween 数（未 Done 的）。</summary>
        public int ActiveCount => _tweens.Count;
    }
}
