using System;
using System.Collections.Generic;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 补间动画 (TweenNode)
    //
    //   对应 Godot 3.1+ 的 SceneTreeTween：按时间插值属性值。
    //   支持：多步骤顺序、步骤内并行、回调、缓动曲线、循环、进度控制。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>缓动曲线类型（对应 Godot 的 TransitionType）。</summary>
    public enum TweenEase
    {
        Linear,
        QuadIn, QuadOut, QuadInOut,
        CubicIn, CubicOut, CubicInOut,
        SineIn, SineOut, SineInOut,
        ExpoIn, ExpoOut, ExpoInOut,
    }

    /// <summary>
    /// 补间动画节点。链式构建，Update 驱动推进。
    /// 用法：
    /// <code>
    /// var tween = go.AddComponent<TweenNode>();
    /// tween.TweenFloat(v => position.X = v, 0f, 10f, 1.0f, TweenEase.QuadOut);
    ///     .Then().TweenFloat(v => position.Y = v, 0f, 5f, 0.5f);
    /// tween.Start();
    /// </code>
    /// </summary>
    public sealed class TweenNode : ComponentReal
    {
        private readonly List<TweenStep> _steps = new();
        private int _currentStep;
        private float _stepElapsed;
        private bool _running;
        private int _loopsSet;
        private int _loopsRemaining; // -1 = 无限循环
        private int _completedCycles;
        private Action? _onComplete;

        /// <summary>补间是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>是否已完成所有步骤（非循环模式）。</summary>
        public bool IsFinished => !_running && _completedCycles > 0;

        /// <summary>总步骤数。</summary>
        public int StepCount => _steps.Count;

        /// <summary>循环已完成的完整周期数。</summary>
        public int CompletedCycles => _completedCycles;

        /// <summary>当前执行到的步骤索引。</summary>
        public int CurrentStepIndex => _currentStep;

        // ── 构建 API ──

        /// <summary>添加一个 float 插值步骤。</summary>
        public TweenNode TweenFloat(Action<float> setter, float from, float to, float duration,
            TweenEase ease = TweenEase.Linear)
        {
            _steps.Add(TweenStep.Float(setter, from, to, duration, ease));
            return this;
        }

        /// <summary>添加一个纯回调步骤（duration=0 立即触发；duration>0 等待后触发）。</summary>
        public TweenNode TweenCallback(Action callback, float waitDuration = 0f)
        {
            _steps.Add(TweenStep.Callback(callback, waitDuration));
            return this;
        }

        /// <summary>向最后一个步骤追加一个并行操作（延迟 delay 秒后触发一次）。</summary>
        public TweenNode Parallel(Action callback, float delay = 0f)
        {
            if (_steps.Count == 0)
                throw new InvalidOperationException("Parallel requires a preceding step.");
            _steps[^1].ParallelActions.Add(new ParallelAction(callback, delay));
            return this;
        }

        /// <summary>顺序分隔符（链式 API 的视觉标记；步骤本身按顺序执行）。</summary>
        public TweenNode Then() => this;

        /// <summary>设置循环次数（0 = 不循环，-1 = 无限循环）。</summary>
        public TweenNode SetLoops(int loops)
        {
            _loopsSet = loops;
            return this;
        }

        /// <summary>每个完整周期结束时的回调。</summary>
        public TweenNode OnComplete(Action callback)
        {
            _onComplete = callback;
            return this;
        }

        // ── 控制流 ──

        /// <summary>开始播放（从第一步开始，重置所有步骤状态）。</summary>
        public void Start()
        {
            if (_steps.Count == 0) return;
            foreach (var s in _steps) s.Reset();
            _running = true;
            _currentStep = 0;
            _stepElapsed = 0f;
            _loopsRemaining = _loopsSet;
            _completedCycles = 0;
            _steps[0].Enter();
        }

        /// <summary>暂停补间。</summary>
        public void Pause() => _running = false;

        /// <summary>恢复补间。</summary>
        public void Resume() => _running = true;

        /// <summary>停止并重置到初始状态。</summary>
        public void Stop()
        {
            _running = false;
            foreach (var s in _steps) s.Reset();
            _currentStep = 0;
            _stepElapsed = 0f;
            _loopsRemaining = _loopsSet;
            _completedCycles = 0;
        }

        /// <summary>立即完成当前步骤（跳到下一步）。</summary>
        public void SkipStep() => _stepElapsed = _steps[_currentStep].Duration;

        /// <summary>
        /// 每帧推进补间。步骤内并行操作在到达各自延迟时触发一次。
        /// 步骤完成推进到下一步；全部完成触发 on_complete 并按循环设置重启。
        /// </summary>
        public override void Update(float deltaTime)
        {
            if (!_running || _steps.Count == 0) return;

            var step = _steps[_currentStep];
            _stepElapsed += deltaTime;

            float t = step.Duration <= 0f ? 1f : Math.Min(1f, _stepElapsed / step.Duration);
            float eased = ApplyEase(t, step.Ease);
            step.Apply(eased);

            // 步骤内的并行操作：到延迟时触发一次
            foreach (var pa in step.ParallelActions)
            {
                if (!pa.Fired && _stepElapsed >= pa.Delay)
                {
                    pa.Action();
                    pa.Fired = true;
                }
            }

            if (_stepElapsed >= step.Duration)
            {
                _currentStep++;
                _stepElapsed = 0f;
                if (_currentStep >= _steps.Count)
                {
                    _completedCycles++;
                    _onComplete?.Invoke();
                    if (_loopsRemaining == -1)
                    {
                        ResetStepsAndRestart();
                    }
                    else if (_loopsRemaining > 1)
                    {
                        _loopsRemaining--;
                        ResetStepsAndRestart();
                    }
                    else
                    {
                        _running = false;
                    }
                }
                else
                {
                    _steps[_currentStep].Enter();
                }
            }
        }

        private void ResetStepsAndRestart()
        {
            foreach (var s in _steps) s.Reset();
            _currentStep = 0;
            _stepElapsed = 0f;
            _steps[0].Enter();
        }

        // ── 缓动函数 ──
        internal static float ApplyEase(float t, TweenEase ease)
        {
            t = Math.Clamp(t, 0f, 1f);
            return ease switch
            {
                TweenEase.Linear => t,
                TweenEase.QuadIn => t * t,
                TweenEase.QuadOut => 1f - (1f - t) * (1f - t),
                TweenEase.QuadInOut => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
                TweenEase.CubicIn => t * t * t,
                TweenEase.CubicOut => 1f - (1f - t) * (1f - t) * (1f - t),
                TweenEase.CubicInOut => t < 0.5f ? 4f * t * t * t : 1f - 4f * (1f - t) * (1f - t) * (1f - t),
                TweenEase.SineIn => 1f - MathF.Cos(t * MathF.PI * 0.5f),
                TweenEase.SineOut => MathF.Sin(t * MathF.PI * 0.5f),
                TweenEase.SineInOut => 0.5f * (1f - MathF.Cos(t * MathF.PI)),
                TweenEase.ExpoIn => t <= 0f ? 0f : MathF.Pow(2f, 10f * (t - 1f)),
                TweenEase.ExpoOut => t >= 1f ? 1f : 1f - MathF.Pow(2f, -10f * t),
                TweenEase.ExpoInOut => t <= 0f ? 0f : t >= 1f ? 1f :
                    t < 0.5f ? 0.5f * MathF.Pow(2f, 20f * t - 10f) : 0.5f * (2f - MathF.Pow(2f, -20f * t + 10f)),
                _ => t,
            };
        }

        // ── 步骤内部结构 ──
        private sealed class TweenStep
        {
            public float Duration;
            public TweenEase Ease;
            public Action<float> Apply = _ => { };
            private readonly Action? _onEnter;
            public List<ParallelAction> ParallelActions = new();
            private bool _entered;

            private TweenStep(float duration, TweenEase ease, Action<float> apply, Action? onEnter)
            {
                Duration = duration;
                Ease = ease;
                Apply = apply;
                _onEnter = onEnter;
            }

            public void Enter()
            {
                if (_entered) return;
                _entered = true;
                _onEnter?.Invoke();
            }

            public void Reset()
            {
                _entered = false;
                foreach (var pa in ParallelActions)
                    pa.Reset();
            }

            public static TweenStep Float(Action<float> setter, float from, float to, float dur, TweenEase ease)
                => new TweenStep(dur, ease, eased => setter(from + (to - from) * eased), null);

            public static TweenStep Callback(Action callback, float waitDuration)
                => new TweenStep(waitDuration, TweenEase.Linear, _ => { }, callback);
        }

        /// <summary>并行操作包装（可重置触发状态）。</summary>
        private sealed class ParallelAction
        {
            public Action Action;
            public float Delay;
            public bool Fired;

            public ParallelAction(Action action, float delay)
            {
                Action = action;
                Delay = delay;
                Fired = false;
            }

            public void Reset() => Fired = false;
        }
    }
}
