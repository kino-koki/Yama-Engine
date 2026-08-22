using System.Diagnostics;

namespace ShanyuEngine.Core;

/// <summary>
/// Game timer with time scaling, pausing, frame rate capping, and delta time smoothing.
/// All game systems should use this as their single source of time, not System.DateTime.
/// </summary>
public sealed class GameTimer
{
    private readonly Stopwatch _stopwatch = new();
    private double _lastFrameTime;
    private double _unscaledDelta;
    private double _smoothedDelta;
    private double _frameTimeBudget = 1.0 / 60.0; // 60 FPS default
    private double _timeScale = 1.0;
    private double _maxDelta = 0.1; // 100ms cap to avoid spiral of death
    private double _totalTime;
    private double _totalUnscaledTime;
    private int _frameCount;
    private int _fps;
    private double _fpsTimer;
    private bool _paused;
    private bool _limitFrameRate;

    /// <summary>Current delta time in seconds (scaled, smoothed).</summary>
    public double DeltaTime => _smoothedDelta;

    /// <summary>Unscaled delta time in seconds (raw real time).</summary>
    public double UnscaledDeltaTime => _unscaledDelta;

    /// <summary>Total elapsed time in seconds (scaled).</summary>
    public double TotalTime => _totalTime;

    /// <summary>Total elapsed unscaled time in seconds.</summary>
    public double TotalUnscaledTime => _totalUnscaledTime;

    /// <summary>Current frames per second.</summary>
    public int FPS => _fps;

    /// <summary>Total frames elapsed.</summary>
    public int FrameCount => _frameCount;

    /// <summary>Whether the timer is paused.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Time scale factor (1.0 = normal, 0.5 = half speed, 2.0 = double speed).</summary>
    public double TimeScale
    {
        get => _timeScale;
        set => _timeScale = Math.Max(0.0, value);
    }

    /// <summary>Target frame rate (0 = uncapped).</summary>
    public double TargetFrameRate
    {
        get => _frameTimeBudget > 0 ? 1.0 / _frameTimeBudget : 0;
        set
        {
            _frameTimeBudget = value > 0 ? 1.0 / value : 0;
            _limitFrameRate = value > 0;
        }
    }

    /// <summary>Maximum delta time to prevent spiral of death (default 100ms).</summary>
    public double MaxDelta
    {
        get => _maxDelta;
        set => _maxDelta = Math.Max(value, 0.001);
    }

    /// <summary>FPS changed event (fires once per second).</summary>
    public event Action<int>? OnFPSChanged;

    public GameTimer()
    {
        _stopwatch.Start();
        _lastFrameTime = _stopwatch.Elapsed.TotalSeconds;
    }

    /// <summary>Call once per frame to advance the timer.</summary>
    public void Tick()
    {
        double now = _stopwatch.Elapsed.TotalSeconds;
        _unscaledDelta = now - _lastFrameTime;
        _lastFrameTime = now;

        // Clamp delta to prevent spiral of death
        if (_unscaledDelta > _maxDelta)
            _unscaledDelta = _maxDelta;

        // Smooth delta with EMA filter to reduce jitter
        const double smoothing = 0.9;
        _smoothedDelta = _smoothedDelta == 0
            ? _unscaledDelta
            : smoothing * _smoothedDelta + (1.0 - smoothing) * _unscaledDelta;

        // Frame rate limiting
        if (_limitFrameRate && _smoothedDelta < _frameTimeBudget)
        {
            double sleepMs = (_frameTimeBudget - _smoothedDelta) * 1000.0;
            if (sleepMs > 1.0)
            {
                int sleepInt = (int)(sleepMs - 0.5);
                if (sleepInt > 0)
                    Thread.Sleep(sleepInt);
            }
            now = _stopwatch.Elapsed.TotalSeconds;
            _unscaledDelta = now - _lastFrameTime;
            _lastFrameTime = now;
        }

        _totalUnscaledTime += _unscaledDelta;
        _frameCount++;

        // Update scaled time
        if (!_paused)
        {
            double scaled = _unscaledDelta * _timeScale;
            _totalTime += scaled;
        }

        // FPS counter (update once per second)
        _fpsTimer += _unscaledDelta;
        if (_fpsTimer >= 1.0)
        {
            _fps = (int)(_frameCount / _fpsTimer);
            _fpsTimer = 0;
            _frameCount = 0;
            OnFPSChanged?.Invoke(_fps);
        }
    }

    /// <summary>Reset the timer to zero.</summary>
    public void Reset()
    {
        _stopwatch.Restart();
        _lastFrameTime = 0;
        _totalTime = 0;
        _totalUnscaledTime = 0;
        _frameCount = 0;
        _fps = 0;
        _fpsTimer = 0;
        _smoothedDelta = 0;
    }

    /// <summary>Create a scoped timer for measuring operation duration.</summary>
    public static ScopeTimer Scope(string name) => new(name);

    /// <summary>Measures and logs the duration of a block of code.</summary>
    public struct ScopeTimer : IDisposable
    {
        private readonly string _name;
        private readonly double _start;

        internal ScopeTimer(string name)
        {
            _name = name;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            double elapsed = (Stopwatch.GetTimestamp() - _start) / Stopwatch.Frequency;
            if (elapsed > 0.001)
                Debug.WriteLine($"[Timer] {_name}: {elapsed * 1000:F1}ms");
        }
    }
}

/// <summary>
/// High-precision stopwatch for microbenchmarks and profiling.
/// </summary>
public sealed class MicroTimer
{
    private readonly long _start = Stopwatch.GetTimestamp();

    /// <summary>Elapsed time in microseconds.</summary>
    public double Microseconds =>
        (Stopwatch.GetTimestamp() - _start) / (Stopwatch.Frequency / 1_000_000.0);

    /// <summary>Elapsed time in milliseconds.</summary>
    public double Milliseconds => Microseconds / 1000.0;

    /// <summary>Reset the timer.</summary>
    public void Restart() => _start = Stopwatch.GetTimestamp();
}
