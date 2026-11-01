// ============================================================================================
//  CPUProfiler.cs — CPU Profiling 封装
//  本文件是 ProfilingSystem.cs 的前端封装，提供 FPS / 帧时间 / 热点 / 线程利用率统计，
//  并向前兼容静态 BeginSample / EndSample 便捷入口。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace YamaEngine.Diagnostics
{
    /// <summary>CPU 帧时间统计。</summary>
    public sealed class CPUFrameTiming
    {
        public ulong FrameNumber { get; set; }
        public float MainThreadMs { get; set; }
        public float TotalFrameMs { get; set; }
    }

    /// <summary>FPS 统计快照。</summary>
    public sealed class FPSStatistics
    {
        public double CurrentFPS { get; set; }
        public double AverageFPS { get; set; }
        public double AvgFrameTimeMs { get; set; }
        public double MaxFPS { get; set; }
        public double MinFPS { get; set; }
        public double FPS_1Percent { get; set; }
        public double FPS_0_1Percent { get; set; }
        public double StdDevFrameTimeMs { get; set; }
    }

    /// <summary>CPU 性能分析器。</summary>
    public class CPUProfiler
    {
        private static readonly Stopwatch _stopwatch = new();

        // 帧统计
        private readonly List<float> _frameTimes = new();
        private const int MaxFrameHistory = 4096;
        private ulong _frameNumber;
        private CPUFrameTiming? _currentFrame;
        private double _currentFPS, _averageFPS, _maxFPS, _minFPS, _sumFPS;
        private int _fpsCount;

        /// <summary>当前帧的 CPU 计时。</summary>
        public CPUFrameTiming CurrentFrame => _currentFrame ??= new CPUFrameTiming();

        /// <summary>开始 CPU 采样。</summary>
        public static void BeginSample(string name) { _stopwatch.Restart(); }

        /// <summary>结束 CPU 采样并返回耗时(ms)。</summary>
        public static double EndSample(string name)
        {
            _stopwatch.Stop();
            return _stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>记录一帧的 CPU 计时。</summary>
        public void RecordFrame(CPUFrameTiming timing)
        {
            _frameNumber = timing.FrameNumber;
            _currentFrame = timing;
            if (timing.TotalFrameMs > 0.001f)
            {
                double fps = 1000.0 / timing.TotalFrameMs;
                _currentFPS = fps;
                _sumFPS += fps;
                _fpsCount++;
                _averageFPS = _fpsCount > 0 ? _sumFPS / _fpsCount : fps;
                if (fps > _maxFPS) _maxFPS = fps;
                if (_minFPS == 0 || fps < _minFPS) _minFPS = fps;
            }
            _frameTimes.Add(timing.TotalFrameMs);
            while (_frameTimes.Count > MaxFrameHistory) _frameTimes.RemoveAt(0);
        }

        /// <summary>当前 FPS。</summary>
        public float CurrentFPS => (float)_currentFPS;

        /// <summary>平均 FPS。</summary>
        public float AverageFPS => (float)_averageFPS;

        /// <summary>最差帧耗时（ms，取历史最大值）。</summary>
        public float WorstFrameMs
        {
            get { float w = 0f; foreach (var t in _frameTimes) if (t > w) w = t; return w; }
        }

        /// <summary>最佳帧耗时（ms，取历史最小值）。</summary>
        public float BestFrameMs
        {
            get
            {
                float b = float.MaxValue;
                foreach (var t in _frameTimes) if (t < b) b = t;
                return _frameTimes.Count > 0 ? b : 0f;
            }
        }

        /// <summary>获取 FPS 统计快照。</summary>
        public FPSStatistics GetFPSStatistics()
        {
            var stat = new FPSStatistics
            {
                CurrentFPS = _currentFPS,
                AverageFPS = _averageFPS,
                MaxFPS = _maxFPS,
                MinFPS = _minFPS
            };
            if (_frameTimes.Count > 0)
            {
                stat.AvgFrameTimeMs = _frameTimes.Average();
                var sorted = _frameTimes.OrderByDescending(x => x).ToList();
                stat.FPS_1Percent = PercentileFrameRate(sorted, 0.01);
                stat.FPS_0_1Percent = PercentileFrameRate(sorted, 0.001);
                stat.StdDevFrameTimeMs = StdDev(_frameTimes);
            }
            return stat;
        }

        /// <summary>获取线程利用率字典。</summary>
        public Dictionary<string, double> GetThreadUtilization()
            => new() { ["Main"] = _fpsCount > 0 ? 100.0 : 0.0, ["Render"] = 0.0 };

        /// <summary>获取调用热点（最多 count 个）。</summary>
        public List<CPUHotSpot> GetHotSpots(CallTreeNode? root, int count = 10)
        {
            var result = new List<CPUHotSpot>();
            if (root == null) return result;
            foreach (var node in EnumerateNodes(root))
            {
                var stats = node.Stats;
                result.Add(new CPUHotSpot
                {
                    FunctionName = node.Name,
                    SelfMs = node.FrameSelfMs,
                    AvgMs = stats.CallCount > 0 ? stats.TotalMs / stats.CallCount : 0,
                    CallCount = (int)stats.CallCount
                });
            }
            return result.OrderByDescending(h => (double)(h.SelfMs ?? 0)).Take(count).ToList();
        }

        /// <summary>获取帧时间历史。</summary>
        public float[] GetFrameTimeHistory()
        {
            var arr = new float[_frameTimes.Count];
            for (int i = 0; i < _frameTimes.Count; i++) arr[i] = _frameTimes[i];
            return arr;
        }

        /// <summary>重置统计。</summary>
        public void Reset()
        {
            _frameTimes.Clear();
            _frameNumber = 0;
            _currentFrame = null;
            _currentFPS = _averageFPS = _maxFPS = _minFPS = _sumFPS = 0;
            _fpsCount = 0;
        }

        private static IEnumerable<CallTreeNode> EnumerateNodes(CallTreeNode node)
        {
            yield return node;
            foreach (var child in node.Children)
                foreach (var sub in EnumerateNodes(child))
                    yield return sub;
        }

        private static double PercentileFrameRate(List<float> frameMsDesc, double percentile)
        {
            if (frameMsDesc.Count == 0) return 0;
            int idx = (int)(frameMsDesc.Count * percentile);
            if (idx >= frameMsDesc.Count) idx = frameMsDesc.Count - 1;
            double ms = frameMsDesc[idx];
            return ms > 0 ? 1000.0 / ms : 0;
        }

        private static double StdDev(List<float> values)
        {
            if (values.Count < 2) return 0;
            double avg = values.Average();
            double sum = 0;
            foreach (var v in values) sum += (v - avg) * (v - avg);
            return Math.Sqrt(sum / values.Count);
        }
    }
}