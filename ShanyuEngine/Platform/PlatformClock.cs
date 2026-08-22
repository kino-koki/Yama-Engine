// ==================================================================================================
//  PlatformClock.cs — 山雨引擎高精度平台时钟
//
//  命名空间: ShanyuEngine.Platform
//  依赖:     System.Diagnostics.Stopwatch
//
//  功能:
//    1. 基于 Stopwatch.GetTimestamp() 的高精度计时（纳秒级分辨率）
//    2. DeltaTime: 上一帧到当前帧的时间间隔（秒）
//    3. TotalTime: 引擎运行总时间（秒）
//    4. TotalFrames: 总渲染帧数
//    5. FPS: 实时帧率统计
//    6. 固定步长模式（FixedTimeStep / Accumulator，用于物理模拟）
// ==================================================================================================

using System;
using System.Diagnostics;

namespace ShanyuEngine.Platform
{
    /// <summary>
    /// 高精度平台时钟。使用 Stopwatch.GetTimestamp() 提供纳秒级分辨率的计时，
    /// 用于驱动引擎主循环的帧间隔计算。
    /// </summary>
    public sealed class PlatformClock
    {
        // ──────────────────────────────────────────────────────────
        // 常量
        // ──────────────────────────────────────────────────────────

        private static readonly double TimestampToSeconds = 1.0 / Stopwatch.Frequency;
        private const double MaxDeltaTime = 0.25; // 防止大跳帧（如调试断点恢复后）
        private const int FpsSampleWindow = 60;  // FPS 统计滑动窗口
        private const double FpsUpdateInterval = 0.5; // FPS 文本更新间隔（秒）

        // ──────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────

        private long _startTimestamp;
        private long _lastFrameTimestamp;
        private long _frameCount;
        private float _deltaTime;
        private float _totalTime;

        // FPS 统计
        private long _fpsFrameAccumulator;
        private long _fpsTimestampAccumulator;
        private double _lastFpsUpdateTimestamp;
        private float _currentFps;
        private float _smoothedFps;

        // 固定步长
        private float _fixedTimeStep;
        private float _accumulator;
        private bool _useFixedTimeStep;
        private int _maxFixedStepsPerFrame;

        // ──────────────────────────────────────────────────────────
        // 属性
        // ──────────────────────────────────────────────────────────

        /// <summary>上一帧到当前帧的时间间隔（秒），已钳制在 [0, MaxDeltaTime] 范围内</summary>
        public float DeltaTime => _deltaTime;

        /// <summary>引擎运行总时间（秒）</summary>
        public float TotalTime => _totalTime;

        /// <summary>总渲染帧数</summary>
        public long TotalFrames => _frameCount;

        /// <summary>当前实时帧率（每秒帧数）</summary>
        public float FPS => _currentFps;

        /// <summary>平滑后的帧率（用于 UI 显示）</summary>
        public float SmoothedFPS => _smoothedFps;

        /// <summary>固定步长时间（秒），仅当 UseFixedTimeStep 为 true 时有效</summary>
        public float FixedTimeStep
        {
            get => _fixedTimeStep;
            set => _fixedTimeStep = value > 0f ? value : 1f / 60f;
        }

        /// <summary>是否启用固定步长模式（用于物理模拟）</summary>
        public bool UseFixedTimeStep
        {
            get => _useFixedTimeStep;
            set => _useFixedTimeStep = value;
        }

        /// <summary>固定步长累积器（秒）</summary>
        public float Accumulator => _accumulator;

        /// <summary>每帧最大固定步进次数（防止死循环）</summary>
        public int MaxFixedStepsPerFrame
        {
            get => _maxFixedStepsPerFrame;
            set => _maxFixedStepsPerFrame = value > 0 ? value : 4;
        }

        // ──────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────

        /// <param name="fixedTimeStep">固定步长时间（秒），默认 1/60</param>
        /// <param name="useFixedTimeStep">是否启用固定步长</param>
        public PlatformClock(float fixedTimeStep = 1f / 60f, bool useFixedTimeStep = false)
        {
            _fixedTimeStep = fixedTimeStep;
            _useFixedTimeStep = useFixedTimeStep;
            _maxFixedStepsPerFrame = 4;
            _smoothedFps = 60f;
        }

        // ──────────────────────────────────────────────────────────
        // 控制方法
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 启动时钟。在引擎初始化完成后调用一次。
        /// </summary>
        public void Start()
        {
            _startTimestamp = Stopwatch.GetTimestamp();
            _lastFrameTimestamp = _startTimestamp;
            _lastFpsUpdateTimestamp = _startTimestamp;
            _frameCount = 0;
            _deltaTime = 0f;
            _totalTime = 0f;
            _accumulator = 0f;
            _fpsFrameAccumulator = 0;
            _fpsTimestampAccumulator = 0;
            _currentFps = 0f;
        }

        /// <summary>
        /// 每帧调用。计算 DeltaTime、更新 FPS 统计、管理固定步长累积器。
        /// </summary>
        /// <returns>本帧的 DeltaTime（秒）</returns>
        public float Tick()
        {
            long now = Stopwatch.GetTimestamp();

            // 计算帧间隔
            double rawDt = (now - _lastFrameTimestamp) * TimestampToSeconds;
            _lastFrameTimestamp = now;

            // 钳制大跳帧
            _deltaTime = (float)Math.Min(rawDt, MaxDeltaTime);

            // 累加总时间
            _totalTime += _deltaTime;
            _frameCount++;

            // 固定步长累积器
            if (_useFixedTimeStep && _fixedTimeStep > 0f)
            {
                _accumulator += _deltaTime;
            }

            // FPS 统计（滑动窗口）
            _fpsFrameAccumulator++;
            _fpsTimestampAccumulator += now - _lastFrameTimestamp;

            double elapsedSinceLastUpdate = (now - _lastFpsUpdateTimestamp) * TimestampToSeconds;
            if (elapsedSinceLastUpdate >= FpsUpdateInterval)
            {
                if (_fpsTimestampAccumulator > 0)
                {
                    _currentFps = (float)(_fpsFrameAccumulator / (elapsedSinceLastUpdate));
                }
                // 指数移动平均平滑 FPS
                _smoothedFps = _smoothedFps * 0.9f + _currentFps * 0.1f;

                _fpsFrameAccumulator = 0;
                _fpsTimestampAccumulator = 0;
                _lastFpsUpdateTimestamp = now;
            }

            return _deltaTime;
        }

        // ──────────────────────────────────────────────────────────
        // 固定步长方法
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 消耗固定步长累积器，返回本帧应执行的固定步数。
        /// 调用方应在循环中执行固定步逻辑。
        /// </summary>
        /// <returns>本帧执行的固定步进次数</returns>
        public int ConsumeFixedSteps()
        {
            if (!_useFixedTimeStep || _fixedTimeStep <= 0f || _accumulator <= 0f)
                return 0;

            int steps = 0;
            while (_accumulator >= _fixedTimeStep - 1e-6f && steps < _maxFixedStepsPerFrame)
            {
                _accumulator -= _fixedTimeStep;
                steps++;
            }

            // 防止累积器无限增长（如帧率极低）
            if (steps == _maxFixedStepsPerFrame && _accumulator >= _fixedTimeStep)
            {
                _accumulator %= _fixedTimeStep;
            }

            return steps;
        }

        /// <summary>
        /// 重置累积器（用于暂停恢复后防止时间爆发）。
        /// </summary>
        public void ResetAccumulator()
        {
            _accumulator = 0f;
        }

        // ──────────────────────────────────────────────────────────
        // 查询
        // ──────────────────────────────────────────────────────────

        /// <summary>当前时间戳（Ticks，可通过 Stopwatch.Frequency 转换）</summary>
        public static long NowTimestamp => Stopwatch.GetTimestamp();

        /// <summary>时间戳转秒的常量</summary>
        public static double TimestampToSecondsConstant => TimestampToSeconds;
    }
}
