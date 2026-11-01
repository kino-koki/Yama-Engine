// ============================================================================================
//  GPUProfiler.cs — GPU Profiling 封装
//  本文件是 ProfilingSystem.cs 的前端封装，提供 GPU 帧时间 / Pass 统计 / 瓶颈检测，
//  并向前兼容静态 BeginSample / EndSample 便捷入口。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace YamaEngine.Diagnostics
{
    /// <summary>渲染 Pass 统计。</summary>
    public sealed class RenderPassStats
    {
        public string Name { get; set; } = "";
        public double DurationMs { get; set; }
        public int DrawCalls { get; set; }
        public long Triangles { get; set; }
    }

    /// <summary>GPU 叠加显示数据。</summary>
    public sealed class GPUOverlayData
    {
        public int DrawCalls { get; set; }
        public long Triangles { get; set; }
        public long Vertices { get; set; }
        public double TotalGpuMs { get; set; }
        public int ShaderSwitches { get; set; }
        public double TextureMemoryMB { get; set; }
        public double BufferMemoryMB { get; set; }
        public int RenderPassCount { get; set; }
    }

    /// <summary>GPU 瓶颈检测结果。</summary>
    public sealed class GPUDetectedBottleneck
    {
        public string Description { get; set; } = "无";
    }

    /// <summary>GPU 性能分析器。</summary>
    public class GPUProfiler
    {
        private readonly List<RenderPassStats> _renderPasses = new();
        private readonly Dictionary<GPUCounter, long> _counters = new();
        private readonly List<float> _gpuHistory = new();
        private const int MaxHistory = 4096;
        private double _averageGpuMs, _peakGpuMs, _currentFrameGpuMs;

        /// <summary>平均 GPU 耗时 (ms)。</summary>
        public double AverageGpuMs => _averageGpuMs;

        /// <summary>峰值 GPU 耗时 (ms)。</summary>
        public double PeakGpuMs => _peakGpuMs;

        /// <summary>检测到的 GPU 瓶颈类型。</summary>
        public GPUBottleneckType DetectedBottleneck { get; private set; } = GPUBottleneckType.None;

        /// <summary>当前帧的 Pass 列表。</summary>
        public List<RenderPassStats> RenderPasses => _renderPasses;

        /// <summary>当前帧的 GPU 计数器。</summary>
        public Dictionary<GPUCounter, long> Counters => _counters;

        /// <summary>当前帧 GPU 耗时 (ms)。</summary>
        public float CurrentFrameGpuMs => (float)_currentFrameGpuMs;

        /// <summary>开始 GPU 采样（需 RHI TimestampQuery 支持）。</summary>
        public static void BeginSample(string name) { }

        /// <summary>结束 GPU 采样。</summary>
        public static double EndSample(string name) => 0;

        /// <summary>帧开始（重置帧内统计）。</summary>
        public void BeginFrame()
        {
            _renderPasses.Clear();
            _counters.Clear();
            _currentFrameGpuMs = 0;
        }

        /// <summary>记录一个渲染 Pass。</summary>
        public void RecordPass(RenderPassStats pass)
        {
            _renderPasses.Add(pass);
            _currentFrameGpuMs += pass.DurationMs;
            _gpuHistory.Add((float)pass.DurationMs);
            while (_gpuHistory.Count > MaxHistory) _gpuHistory.RemoveAt(0);
            if (_currentFrameGpuMs > _peakGpuMs) _peakGpuMs = _currentFrameGpuMs;
            _averageGpuMs = _gpuHistory.Count > 0 ? _gpuHistory.Average() : _currentFrameGpuMs;
        }

        /// <summary>设置 GPU 计数器。</summary>
        public void SetCounter(GPUCounter counter, long value) => _counters[counter] = value;

        /// <summary>获取 GPU 叠加数据。</summary>
        public GPUOverlayData GetOverlay()
        {
            return new GPUOverlayData
            {
                DrawCalls = (int)_counters.GetValueOrDefault(GPUCounter.DrawCall),
                Triangles = _counters.GetValueOrDefault(GPUCounter.TriangleCount),
                Vertices = _counters.GetValueOrDefault(GPUCounter.VertexCount),
                TotalGpuMs = _currentFrameGpuMs,
                ShaderSwitches = (int)_counters.GetValueOrDefault(GPUCounter.ShaderChange),
                TextureMemoryMB = _counters.GetValueOrDefault(GPUCounter.TextureMemory) / (1024.0 * 1024.0),
                BufferMemoryMB = _counters.GetValueOrDefault(GPUCounter.BufferMemory) / (1024.0 * 1024.0),
                RenderPassCount = _renderPasses.Count
            };
        }

        /// <summary>检测 GPU 瓶颈。</summary>
        public GPUDetectedBottleneck DetectBottleneck()
        {
            var result = new GPUDetectedBottleneck
            {
                Description = DetectedBottleneck.ToString()
            };
            if (_currentFrameGpuMs > 0)
            {
                _averageGpuMs = _gpuHistory.Count > 0 ? _gpuHistory.Average() : _currentFrameGpuMs;
                if (_currentFrameGpuMs > 30.0) DetectedBottleneck = GPUBottleneckType.ComputeBound;
            }
            return result;
        }

        /// <summary>获取 GPU 耗时历史。</summary>
        public float[] GetGpuTimeHistory()
        {
            var arr = new float[_gpuHistory.Count];
            for (int i = 0; i < _gpuHistory.Count; i++) arr[i] = _gpuHistory[i];
            return arr;
        }

        /// <summary>重置统计。</summary>
        public void Reset()
        {
            _renderPasses.Clear();
            _counters.Clear();
            _gpuHistory.Clear();
            _averageGpuMs = _peakGpuMs = _currentFrameGpuMs = 0;
            DetectedBottleneck = GPUBottleneckType.None;
        }
    }
}