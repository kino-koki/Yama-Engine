// ============================================================================================
//  TimeManager.cs
//  yama引擎 (YamaEngine) 时间管理系统
// ============================================================================================
//  提供统一的时间管理，包括帧计时、时间缩放、游戏暂停/恢复、定时器等。
//  设计参考 Unity Time + Unreal Engine 的 FApp / FPlatformTime。
// ============================================================================================

using System;
using System.Diagnostics;

namespace YamaEngine.Core
{
    /// <summary>
    /// 引擎时间管理器。提供帧率无关的时间访问和全局时间控制。
    /// 用法：
    /// <code>
    /// TimeManager.Instance.Update();
    /// float dt = TimeManager.Instance.DeltaTime;
    /// float gameTime = TimeManager.Instance.GameTime;
    /// </code>
    /// </summary>
    public sealed class TimeManager
    {
        public static TimeManager Instance { get; private set; } = new TimeManager();

        // ====== 计时器 ======
        private readonly Stopwatch _stopwatch;
        private double _lastFrameTime;
        private double _currentFrameTime;

        // ====== 时间缩放 ======
        private float _timeScale = 1.0f;
        private float _fixedTimeScale = 1.0f;

        // ====== 帧计数 ======
        private ulong _frameCount;
        private int _fps;
        private int _fpsAccumulator;
        private double _fpsTimer;

        // ====== 时间累加器 ======
        private double _gameTime;       // 受时间缩放影响
        private double _unscaledTime;   // 不受时间缩放影响
        private double _realTime;       // 真实世界时间
        private double _fixedTime;      // 固定步进时间

        // ====== 暂停 ======
        private bool _paused;
        private double _pauseTime;

        // ====== 最大帧间隔 ======
        /// <summary>为防止长时间断点后的大跳跃，限制最大 DeltaTime（秒）。</summary>
        public float MaxDeltaTime { get; set; } = 0.333f; // ~3 FPS

        /// <summary>固定步进时间步长（秒）。对应 Unity 的 Fixed Timestep。</summary>
        public float FixedDeltaTime { get; set; } = 1.0f / 60.0f;

        // ====== 公开属性 ======

        /// <summary>当前帧与上一帧的时间差（受时间缩放影响）。</summary>
        public float DeltaTime { get; private set; }

        /// <summary>不受时间缩放影响的原始帧时间差。</summary>
        public float UnscaledDeltaTime { get; private set; }

        /// <summary>固定步进的 DeltaTime（受时间缩放影响）。</summary>
        public float FixedDelta => FixedDeltaTime * _fixedTimeScale;

        /// <summary>当前游戏运行总时间（受时间缩放）。</summary>
        public double GameTime => _gameTime;

        /// <summary>当前运行总时间（不受时间缩放）。</summary>
        public double UnscaledTime => _unscaledTime;

        /// <summary>真实世界已流逝时间。</summary>
        public double RealTime => _realTime;

        /// <summary>总帧数。</summary>
        public ulong FrameCount => _frameCount;

        /// <summary>当前 FPS。</summary>
        public int FPS => _fps;

        /// <summary>时间缩放系数（1.0 为正常速度）。</summary>
        public float TimeScale
        {
            get => _timeScale;
            set => _timeScale = Math.Max(0.0f, value);
        }

        /// <summary>固定步进时间缩放系数。</summary>
        public float FixedTimeScale
        {
            get => _fixedTimeScale;
            set => _fixedTimeScale = Math.Max(0.0f, value);
        }

        /// <summary>是否暂停。</summary>
        public bool IsPaused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                if (_paused) _pauseTime = _realTime;
            }
        }

        /// <summary>是否正在游戏中（不同于编辑器暂停）。</summary>
        public bool IsGamePaused { get; set; }

        /// <summary>自暂停以来的时间。</summary>
        public double TimeSincePaused => _paused ? _realTime - _pauseTime : 0;

        // ====== 构造 ======
        private TimeManager()
        {
            _stopwatch = Stopwatch.StartNew();
            _lastFrameTime = 0;
            _currentFrameTime = 0;
            _gameTime = 0;
            _unscaledTime = 0;
            _realTime = 0;
            _frameCount = 0;
        }

        // ====== 重置 ======
        public void Reset()
        {
            _stopwatch.Restart();
            _lastFrameTime = 0;
            _currentFrameTime = 0;
            _gameTime = 0;
            _unscaledTime = 0;
            _realTime = 0;
            _frameCount = 0;
            _fps = 0;
            _fpsAccumulator = 0;
            _fpsTimer = 0;
            _paused = false;
        }

        // ====== 每帧更新 ======
        /// <summary>在每帧开始前调用。</summary>
        public void Update()
        {
            // 读取当前时间
            _currentFrameTime = _stopwatch.Elapsed.TotalSeconds;
            _realTime = _currentFrameTime;

            if (_lastFrameTime <= 0)
            {
                // 第一帧
                _lastFrameTime = _currentFrameTime;
                DeltaTime = 0;
                UnscaledDeltaTime = 0;
                return;
            }

            // 计算原始 DeltaTime
            UnscaledDeltaTime = (float)(_currentFrameTime - _lastFrameTime);

            // 限制最大 DeltaTime 防止调试断点后的跳跃
            if (UnscaledDeltaTime > MaxDeltaTime)
                UnscaledDeltaTime = MaxDeltaTime;

            // 应用时间缩放
            DeltaTime = UnscaledDeltaTime * _timeScale;

            // 更新累加器
            if (!_paused)
            {
                _gameTime += DeltaTime;
                _unscaledTime += UnscaledDeltaTime;
            }

            // 更新帧统计
            _frameCount++;
            _fpsAccumulator++;
            _fpsTimer += UnscaledDeltaTime;
            if (_fpsTimer >= 1.0)
            {
                _fps = _fpsAccumulator;
                _fpsAccumulator = 0;
                _fpsTimer -= 1.0;
            }

            // 更新定时器
            UpdateTimers();

            _lastFrameTime = _currentFrameTime;
        }

        // ====== 固定步进 ======
        /// <summary>
        /// 获取当前固定步进迭代次数。物理等子系统使用此方法进行固定步进。
        /// 返回需要执行的步进次数。
        /// </summary>
        public int GetFixedStepCount(ref float accumulator)
        {
            if (_paused) return 0;

            float step = FixedDelta;
            if (step <= 0) return 0;

            accumulator += UnscaledDeltaTime;
            int steps = 0;

            while (accumulator >= step && steps < 8) // 上限8步防止螺旋
            {
                accumulator -= step;
                steps++;
                _fixedTime += step;
            }

            // 如果累积太大，丢弃防止螺旋
            if (accumulator > step * 4)
                accumulator = 0;

            return steps;
        }

        // ====== 定时器 ======
        /// <summary>创建一次性定时器。</summary>
        public TimerHandle SetTimeout(Action callback, float delaySeconds)
        {
            var handle = new TimerHandle { Id = TimerHandle.NextId++ };
            _timers.Add(new TimerEntry
            {
                Handle = handle,
                Callback = callback,
                RemainingTime = delaySeconds,
                IsRepeating = false
            });
            return handle;
        }

        /// <summary>创建重复定时器。</summary>
        public TimerHandle SetInterval(Action callback, float intervalSeconds)
        {
            var handle = new TimerHandle { Id = TimerHandle.NextId++ };
            _timers.Add(new TimerEntry
            {
                Handle = handle,
                Callback = callback,
                RemainingTime = intervalSeconds,
                Interval = intervalSeconds,
                IsRepeating = true
            });
            return handle;
        }

        /// <summary>取消定时器。</summary>
        public void ClearTimer(TimerHandle handle)
        {
            _timers.RemoveAll(t => t.Handle.Id == handle.Id);
        }

        private readonly List<TimerEntry> _timers = new();
        private readonly List<TimerEntry> _pendingTimers = new();

        private void UpdateTimers()
        {
            _pendingTimers.Clear();
            foreach (var timer in _timers)
            {
                var t = timer;
                t.RemainingTime -= DeltaTime;
                if (t.RemainingTime <= 0)
                {
                    t.Callback?.Invoke();
                    if (t.IsRepeating)
                    {
                        t.RemainingTime += t.Interval;
                        _pendingTimers.Add(t);
                    }
                    // 非重复定时器不重新添加
                }
                else
                {
                    _pendingTimers.Add(t);
                }
            }
            _timers.Clear();
            _timers.AddRange(_pendingTimers);
        }

        // ====== 编辑器集成 ======
        /// <summary>编辑器中使用，时间按需步进。</summary>
        public void EditorStep(float deltaTime)
        {
            DeltaTime = deltaTime;
            UnscaledDeltaTime = deltaTime;
            _frameCount++;
            _gameTime += deltaTime;
            _unscaledTime += deltaTime;
        }

        internal struct TimerEntry
        {
            public TimerHandle Handle;
            public Action? Callback;
            public float RemainingTime;
            public float Interval;
            public bool IsRepeating;
        }
    }

    /// <summary>
    /// 定时器句柄。
    /// </summary>
    public struct TimerHandle : IEquatable<TimerHandle>
    {
        internal static int NextId = 1;
        internal int Id;

        public bool IsValid => Id > 0;
        public bool Equals(TimerHandle other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is TimerHandle h && Equals(h);
        public override int GetHashCode() => Id;
        public static bool operator ==(TimerHandle a, TimerHandle b) => a.Id == b.Id;
        public static bool operator !=(TimerHandle a, TimerHandle b) => a.Id != b.Id;
    }
}
