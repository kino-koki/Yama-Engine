// ════════════════════════════════════════════════════════════════════════════
// yama引擎 - 世界时间系统 (WorldTimeSystem)
//
// 管理游戏世界的时间。包括：
//   * 实时时间（真实经过的时间）
//   * 游戏时间（可以缩放/暂停）
//   * 固定步长时间（物理模拟用）
//   * 帧时间统计
//   * 时间尺度
//
// 参考 Unity 的 Time 类，但更灵活。
//
// FIXME: 没处理系统时间跳变（如 NTP 同步）
// TODO: 加个时间轴系统，支持回放和慢动作
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace YamaEngine.ECS
{
    /// <summary>
    /// 世界时间系统：管理游戏世界的各种时间。
    /// </summary>
    public sealed class WorldTimeSystem : SystemBaseReal
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly Stopwatch _stopwatch;
        private long _lastTicks;
        private long _startTicks;

        // 时间状态
        private float _realTime;          // 真实经过的时间
        private float _gameTime;          // 游戏时间
        private float _deltaTime;         // 本帧增量时间
        private float _unscaledDeltaTime; // 未缩放的增量时间
        private float _timeScale = 1f;    // 时间缩放
        private float _fixedDeltaTime = 1f / 60f; // 固定步长
        private float _maxDeltaTime = 0.25f;      // 最大增量时间（防止跳帧）

        // 固定步长累积
        private float _fixedAccumulator;
        private int _fixedUpdateCount;

        // 帧统计
        private int _frameCount;
        private float _fps;
        private float _fpsUpdateInterval = 0.5f;
        private float _fpsAccumulator;
        private int _fpsFrameCount;

        // 平滑 delta
        private readonly float[] _deltaHistory;
        private int _deltaHistoryIndex;
        private const int DeltaHistorySize = 60;
        private float _smoothDelta;

        // 暂停
        private bool _paused;
        private int _pauseCount;

        // 时间事件
        private readonly List<TimedEvent> _timedEvents = new();

        // ──────────────────────────────────────────────────────────────────────
        // 公开属性
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>真实经过的时间（秒）</summary>
        public float RealTime => _realTime;

        /// <summary>游戏时间（秒，受时间缩放影响）</summary>
        public float GameTime => _gameTime;

        /// <summary>本帧增量时间（受时间缩放影响）</summary>
        public float DeltaTime => _paused ? 0f : _deltaTime;

        /// <summary>未缩放的增量时间</summary>
        public float UnscaledDeltaTime => _unscaledDeltaTime;

        /// <summary>时间缩放</summary>
        public float TimeScale
        {
            get => _timeScale;
            set => _timeScale = Math.Max(0f, value);
        }

        /// <summary>固定步长（秒）</summary>
        public float FixedDeltaTime
        {
            get => _fixedDeltaTime;
            set => _fixedDeltaTime = value > 0 ? value : _fixedDeltaTime;
        }

        /// <summary>最大增量时间</summary>
        public float MaxDeltaTime
        {
            get => _maxDeltaTime;
            set => _maxDeltaTime = value > 0 ? value : _maxDeltaTime;
        }

        /// <summary>帧数</summary>
        public int FrameCount => _frameCount;

        /// <summary>当前 FPS</summary>
        public float FPS => _fps;

        /// <summary>平滑后的 delta time</summary>
        public float SmoothDeltaTime => _smoothDelta;

        /// <summary>是否暂停</summary>
        public bool IsPaused => _paused;

        /// <summary>本帧固定更新次数</summary>
        public int FixedUpdateCount => _fixedUpdateCount;

        /// <summary>是否需要执行 FixedUpdate</summary>
        public bool ShouldFixedUpdate => _fixedAccumulator >= _fixedDeltaTime && !_paused;

        // ──────────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────────

        public WorldTimeSystem()
        {
            _stopwatch = new Stopwatch();
            _stopwatch.Start();
            _startTicks = _stopwatch.ElapsedTicks;
            _lastTicks = _startTicks;
            _deltaHistory = new float[DeltaHistorySize];
            _smoothDelta = 1f / 60f;
            Name = "WorldTime";
        }

        public override void OnDestroy()
        {
            _stopwatch?.Stop();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 更新
        // ──────────────────────────────────────────────────────────────────────

        public override void OnUpdate(float deltaTime)
        {
            // 计算真实 delta time
            long currentTicks = _stopwatch.ElapsedTicks;
            float rawDelta = (float)(currentTicks - _lastTicks) / Stopwatch.Frequency;
            _lastTicks = currentTicks;

            // 限制最大 delta
            rawDelta = Math.Min(rawDelta, _maxDeltaTime);

            _unscaledDeltaTime = rawDelta;
            _realTime += rawDelta;

            // 计算缩放后的 delta
            _deltaTime = _paused ? 0f : rawDelta * _timeScale;
            _gameTime += _deltaTime;

            // 帧计数
            _frameCount++;

            // 更新平滑 delta
            UpdateSmoothDelta(rawDelta);

            // 更新 FPS
            UpdateFPS(rawDelta);

            // 累积固定步长
            if (!_paused)
            {
                _fixedAccumulator += _deltaTime;
                _fixedUpdateCount = 0;

                // 限制最大固定更新次数
                int maxSteps = (int)(_maxDeltaTime / _fixedDeltaTime) + 1;
                while (_fixedAccumulator >= _fixedDeltaTime && _fixedUpdateCount < maxSteps)
                {
                    _fixedAccumulator -= _fixedDeltaTime;
                    _fixedUpdateCount++;
                }

                // 如果累积太多，丢弃
                if (_fixedAccumulator > _fixedDeltaTime * maxSteps)
                {
                    _fixedAccumulator = 0;
                }
            }
            else
            {
                _fixedUpdateCount = 0;
            }

            // 处理定时事件
            ProcessTimedEvents();
        }

        /// <summary>
        /// 获取固定更新的 delta time（用于物理系统）。
        /// 每次调用返回一个固定步长。
        /// </summary>
        public IEnumerable<float> GetFixedDeltas()
        {
            for (int i = 0; i < _fixedUpdateCount; i++)
            {
                yield return _fixedDeltaTime;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 暂停管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>暂停游戏时间。</summary>
        public void Pause()
        {
            _pauseCount++;
            if (_pauseCount > 0)
                _paused = true;
        }

        /// <summary>恢复游戏时间。</summary>
        public void Resume()
        {
            _pauseCount = Math.Max(0, _pauseCount - 1);
            if (_pauseCount == 0)
                _paused = false;
        }

        /// <summary>强制设置暂停状态。</summary>
        public void SetPaused(bool paused)
        {
            _paused = paused;
            _pauseCount = paused ? 1 : 0;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 时间缩放
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>设置时间缩放（1 = 正常速度）。</summary>
        public void SetTimeScale(float scale)
        {
            _timeScale = Math.Max(0f, scale);
        }

        /// <summary>慢动作（0.5x）。</summary>
        public void SlowMotion(float factor = 0.5f)
        {
            _timeScale = factor;
        }

        /// <summary>恢复正常速度。</summary>
        public void NormalSpeed()
        {
            _timeScale = 1f;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 定时事件
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 注册一个定时事件。在指定时间后触发。
        /// </summary>
        /// <param name="delay">延迟时间（秒）</param>
        /// <param name="callback">回调</param>
        /// <param name="repeat">是否重复</param>
        /// <returns>事件 ID</returns>
        public long Schedule(float delay, Action callback, bool repeat = false)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var evt = new TimedEvent
            {
                Id = _nextEventId++,
                TriggerTime = _gameTime + delay,
                Delay = delay,
                Callback = callback,
                Repeat = repeat,
                Active = true,
            };
            _timedEvents.Add(evt);
            return evt.Id;
        }

        /// <summary>
        /// 取消定时事件。
        /// </summary>
        public bool Cancel(long eventId)
        {
            for (int i = 0; i < _timedEvents.Count; i++)
            {
                if (_timedEvents[i].Id == eventId)
                {
                    _timedEvents[i].Active = false;
                    _timedEvents.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        private static long _nextEventId;

        private void ProcessTimedEvents()
        {
            for (int i = _timedEvents.Count - 1; i >= 0; i--)
            {
                var evt = _timedEvents[i];
                if (!evt.Active) continue;

                if (_gameTime >= evt.TriggerTime)
                {
                    evt.Callback?.Invoke();

                    if (evt.Repeat)
                    {
                        evt.TriggerTime = _gameTime + evt.Delay;
                    }
                    else
                    {
                        _timedEvents.RemoveAt(i);
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // FPS 和平滑 delta
        // ──────────────────────────────────────────────────────────────────────

        private void UpdateFPS(float delta)
        {
            _fpsAccumulator += delta;
            _fpsFrameCount++;

            if (_fpsAccumulator >= _fpsUpdateInterval)
            {
                _fps = _fpsFrameCount / _fpsAccumulator;
                _fpsAccumulator = 0;
                _fpsFrameCount = 0;
            }
        }

        private void UpdateSmoothDelta(float delta)
        {
            _deltaHistory[_deltaHistoryIndex] = delta;
            _deltaHistoryIndex = (_deltaHistoryIndex + 1) % DeltaHistorySize;

            // 计算平均
            float sum = 0;
            for (int i = 0; i < DeltaHistorySize; i++)
                sum += _deltaHistory[i];
            _smoothDelta = sum / DeltaHistorySize;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 时间格式化
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取格式化的游戏时间字符串。
        /// </summary>
        public string GetFormattedTime(bool includeMs = false)
        {
            float t = _gameTime;
            int hours = (int)(t / 3600);
            t -= hours * 3600;
            int minutes = (int)(t / 60);
            t -= minutes * 60;
            int seconds = (int)t;
            t -= seconds;

            if (includeMs)
            {
                int ms = (int)(t * 1000);
                return $"{hours:D2}:{minutes:D2}:{seconds:D2}.{ms:D3}";
            }
            return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
        }

        /// <summary>
        /// 生成诊断报告。
        /// </summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ WorldTimeSystem 诊断报告 ═══");
            sb.AppendLine($"帧数: {FrameCount}");
            sb.AppendLine($"FPS: {FPS:F1}");
            sb.AppendLine($"游戏时间: {GetFormattedTime(true)}");
            sb.AppendLine($"真实时间: {RealTime:F3}s");
            sb.AppendLine($"Delta: {DeltaTime:F6}s (平滑: {SmoothDeltaTime:F6}s)");
            sb.AppendLine($"未缩放 Delta: {UnscaledDeltaTime:F6}s");
            sb.AppendLine($"时间缩放: {TimeScale:F2}x");
            sb.AppendLine($"固定步长: {FixedDeltaTime:F6}s");
            sb.AppendLine($"固定更新次数: {FixedUpdateCount}");
            sb.AppendLine($"暂停: {IsPaused} (暂停计数: {_pauseCount})");
            sb.AppendLine($"定时事件: {_timedEvents.Count}");
            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 内部类型
        // ──────────────────────────────────────────────────────────────────────

        private class TimedEvent
        {
            public long Id;
            public float TriggerTime;
            public float Delay;
            public Action Callback;
            public bool Repeat;
            public bool Active;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 时间快照
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 时间快照：记录某个时刻的时间状态。
    /// 用于回放、网络同步等。
    /// </summary>
    public struct TimeSnapshot
    {
        public float GameTime;
        public float RealTime;
        public float DeltaTime;
        public int FrameCount;
        public float TimeScale;
        public bool IsPaused;

        /// <summary>从 WorldTimeSystem 创建快照</summary>
        public static TimeSnapshot Capture(WorldTimeSystem timeSystem)
        {
            return new TimeSnapshot
            {
                GameTime = timeSystem.GameTime,
                RealTime = timeSystem.RealTime,
                DeltaTime = timeSystem.DeltaTime,
                FrameCount = timeSystem.FrameCount,
                TimeScale = timeSystem.TimeScale,
                IsPaused = timeSystem.IsPaused,
            };
        }

        public override string ToString()
        {
            return $"帧{FrameCount}: game={GameTime:F3}s, delta={DeltaTime:F6}s, " +
                   $"scale={TimeScale:F2}x, paused={IsPaused}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 时间轴（用于过场动画/回放）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 时间轴：独立的时间线，可以暂停、倒退、加速。
    /// 用于过场动画、特效回放等。
    /// </summary>
    public sealed class Timeline : IDisposable
    {
        private float _currentTime;
        private float _duration;
        private float _speed = 1f;
        private bool _playing;
        private bool _loop;
        private float _startOffset;

        /// <summary>当前时间</summary>
        public float CurrentTime => _currentTime;

        /// <summary>总时长</summary>
        public float Duration => _duration;

        /// <summary>播放速度</summary>
        public float Speed
        {
            get => _speed;
            set => _speed = Math.Max(0f, value);
        }

        /// <summary>是否正在播放</summary>
        public bool IsPlaying => _playing;

        /// <summary>是否循环</summary>
        public bool Loop
        {
            get => _loop;
            set => _loop = value;
        }

        /// <summary>进度 (0-1)</summary>
        public float Progress => _duration > 0 ? _currentTime / _duration : 0f;

        /// <summary>是否结束</summary>
        public bool IsFinished => !_loop && _currentTime >= _duration;

        /// <summary>开始偏移</summary>
        public float StartOffset
        {
            get => _startOffset;
            set => _startOffset = Math.Max(0f, value);
        }

        public Timeline(float duration)
        {
            _duration = duration;
            _currentTime = 0f;
        }

        /// <summary>播放</summary>
        public void Play()
        {
            _playing = true;
            if (_currentTime >= _duration)
                _currentTime = _startOffset;
        }

        /// <summary>暂停</summary>
        public void Pause()
        {
            _playing = false;
        }

        /// <summary>停止</summary>
        public void Stop()
        {
            _playing = false;
            _currentTime = _startOffset;
        }

        /// <summary>重置</summary>
        public void Reset()
        {
            _currentTime = _startOffset;
        }

        /// <summary>跳转到指定时间</summary>
        public void Seek(float time)
        {
            _currentTime = Math.Clamp(time, 0f, _duration);
        }

        /// <summary>跳转到指定进度</summary>
        public void SeekProgress(float progress)
        {
            _currentTime = Math.Clamp(progress, 0f, 1f) * _duration;
        }

        /// <summary>更新</summary>
        public void Update(float deltaTime)
        {
            if (!_playing) return;

            _currentTime += deltaTime * _speed;

            if (_currentTime >= _duration)
            {
                if (_loop)
                {
                    _currentTime = _startOffset + (_currentTime - _duration);
                }
                else
                {
                    _currentTime = _duration;
                    _playing = false;
                }
            }
        }

        public void Dispose()
        {
            _playing = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 帧时间分析器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 帧时间分析器：记录和分析帧时间。
    /// </summary>
    public sealed class FrameTimeProfiler : IDisposable
    {
        private readonly float[] _frameTimes;
        private int _index;
        private int _count;
        private float _minFrameTime = float.MaxValue;
        private float _maxFrameTime;
        private float _totalFrameTime;

        /// <summary>采样数</summary>
        public int SampleCount { get; }

        /// <summary>当前样本数</summary>
        public int CurrentSamples => _count;

        /// <summary>最小帧时间</summary>
        public float MinFrameTime => _minFrameTime;

        /// <summary>最大帧时间</summary>
        public float MaxFrameTime => _maxFrameTime;

        /// <summary>平均帧时间</summary>
        public float AvgFrameTime => _count > 0 ? _totalFrameTime / _count : 0f;

        /// <summary>平均 FPS</summary>
        public float AvgFPS => AvgFrameTime > 0 ? 1f / AvgFrameTime : 0f;

        public FrameTimeProfiler(int sampleCount = 120)
        {
            SampleCount = sampleCount;
            _frameTimes = new float[sampleCount];
        }

        /// <summary>记录一帧的时间</summary>
        public void Record(float frameTime)
        {
            _frameTimes[_index] = frameTime;
            _index = (_index + 1) % SampleCount;
            if (_count < SampleCount) _count++;

            // 更新统计
            if (frameTime < _minFrameTime) _minFrameTime = frameTime;
            if (frameTime > _maxFrameTime) _maxFrameTime = frameTime;

            // 重新计算总和（滑动窗口）
            _totalFrameTime = 0;
            for (int i = 0; i < _count; i++)
                _totalFrameTime += _frameTimes[i];
        }

        /// <summary>重置统计</summary>
        public void Reset()
        {
            _index = 0;
            _count = 0;
            _minFrameTime = float.MaxValue;
            _maxFrameTime = 0;
            _totalFrameTime = 0;
        }

        /// <summary>生成报告</summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ 帧时间分析 ═══");
            sb.AppendLine($"样本数: {CurrentSamples}/{SampleCount}");
            sb.AppendLine($"平均: {AvgFrameTime * 1000:F2}ms ({AvgFPS:F1} FPS)");
            sb.AppendLine($"最小: {MinFrameTime * 1000:F2}ms ({1f / MinFrameTime:F1} FPS)");
            sb.AppendLine($"最大: {MaxFrameTime * 1000:F2}ms ({1f / MaxFrameTime:F1} FPS)");

            // 百分位
            if (_count > 0)
            {
                var sorted = new float[_count];
                Array.Copy(_frameTimes, sorted, _count);
                Array.Sort(sorted);

                float p50 = sorted[_count / 2];
                float p90 = sorted[(int)(_count * 0.9f)];
                float p99 = sorted[(int)(_count * 0.99f)];

                sb.AppendLine($"P50: {p50 * 1000:F2}ms");
                sb.AppendLine($"P90: {p90 * 1000:F2}ms");
                sb.AppendLine($"P99: {p99 * 1000:F2}ms");
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            // nothing
        }
    }
}
