using System;
using System.Collections.Generic;
using System.Text;

namespace ShanyuEngine.Diagnostics
{
    // ══════════════════════════════════════════════════════════════════
    // 运行时分析覆盖层（ProfilerOverlaySystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现运行时性能覆盖层渲染系统，包含：
    //   1. ProfilerOverlayMode 枚举（None/FPS/Basic/Full/Custom）
    //   2. 覆盖层文本生成（供 ImGui/自绘渲染使用）
    //   3. 帧时间图表（实时波形图数据）
    //   4. FPS 显示（当前/平均/1%/0.1%/颜色编码）
    //   5. 内存显示（当前/峰值/GC 信息）
    //   6. DrawCall / 三角形 / GPU 时间显示
    //   7. 可自定义指标选择
    //   8. 快捷键切换覆盖层模式
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 覆盖层显示模式。控制覆盖层显示的信息详细程度。
    /// </summary>
    public enum ProfilerOverlayMode
    {
        /// <summary>不显示覆盖层</summary>
        None = 0,
        /// <summary>仅显示 FPS</summary>
        FPS = 1,
        /// <summary>基础信息（FPS/帧时间/内存）</summary>
        Basic = 2,
        /// <summary>完整信息（CPU/GPU/内存/网络/音频/DrawCall）</summary>
        Full = 3,
        /// <summary>自定义（由 CustomMetrics 控制）</summary>
        Custom = 4
    }

    /// <summary>
    /// 可选覆盖层指标。
    /// </summary>
    public enum OverlayMetric
    {
        FPS,
        FrameTime,
        CPUFrameTime,
        GPUFrameTime,
        Memory,
        ManagedMemory,
        ProcessMemory,
        GCMemory,
        DrawCalls,
        Triangles,
        Vertices,
        RenderPasses,
        ShaderSwitches,
        TextureMemory,
        BufferMemory,
        ActiveAudioSources,
        AudioCPU,
        NetworkRTT,
        NetworkPacketLoss,
        NetworkBandwidth,
        ActiveAllocations,
        ObjectPoolUtilization,
        ThreadUtilization
    }

    /// <summary>
    /// 帧时间图表数据。提供实时波形图绘制所需的采样数据。
    /// </summary>
    public sealed class FrameTimeChartData
    {
        /// <summary>采样值数组（帧时间，毫秒）</summary>
        public float[] Values { get; } = new float[HistorySize];
        /// <summary>当前写入偏移</summary>
        public int Offset { get; private set; }
        /// <summary>当前有效数据量</summary>
        public int Count { get; private set; }
        /// <summary>最大采样数</summary>
        public const int HistorySize = 240;

        /// <summary>目标帧时间线（毫秒）</summary>
        public double TargetFrameTimeMs { get; set; } = 16.667;

        /// <summary>添加采样值（环形缓冲）</summary>
        public void Push(float value)
        {
            Values[Offset] = value;
            Offset = (Offset + 1) % HistorySize;
            if (Count < HistorySize) Count++;
        }

        /// <summary>获取按时间顺序排列的值（从旧到新）</summary>
        public float[] GetOrderedValues()
        {
            var result = new float[Count];
            for (int i = 0; i < Count; i++)
                result[i] = Values[(Offset - Count + i + HistorySize) % HistorySize];
            return result;
        }

        /// <summary>重置</summary>
        public void Reset()
        {
            Offset = 0;
            Count = 0;
            Array.Clear(Values, 0, Values.Length);
        }
    }

    /// <summary>
    /// 覆盖层指标数据。单个指标的当前值与显示配置。
    /// </summary>
    public sealed class OverlayMetricData
    {
        public OverlayMetric Metric { get; }
        public string Label { get; set; } = string.Empty;
        /// <summary>当前值</summary>
        public double Value { get; set; }
        /// <summary>格式化字符串</summary>
        public string FormattedValue { get; set; } = "--";
        /// <summary>单位</summary>
        public string Unit { get; set; } = string.Empty;
        /// <summary>是否显示</summary>
        public bool Visible { get; set; } = true;
        /// <summary>是否警告（值超出正常范围）</summary>
        public bool IsWarning { get; set; }
        /// <summary>警告阈值</summary>
        public double WarningThreshold { get; set; } = double.MaxValue;
        /// <summary>警告颜色（ARGB）</summary>
        public uint WarningColor { get; set; } = 0xFFFF4444;

        public OverlayMetricData(OverlayMetric metric) { Metric = metric; }
    }

    /// <summary>
    /// 覆盖层快捷键配置。
    /// </summary>
    public sealed class OverlayKeyBindings
    {
        /// <summary>切换覆盖层显示的快捷键</summary>
        public string ToggleOverlay { get; set; } = "F3";
        /// <summary>切换到下一个模式的快捷键</summary>
        public string NextMode { get; set; } = "F4";
        /// <summary>切换到上一个模式的快捷键</summary>
        public string PrevMode { get; set; } = "Shift+F4";
    }

    /// <summary>
    /// 运行时分析覆盖层系统。将性能数据渲染为实时叠加文本，
    /// 显示在视口上方（类似 UE 的 stat 命令、Unity Profiler overlay）。
    /// </summary>
    public sealed class ProfilerOverlaySystem
    {
        /// <summary>当前显示模式</summary>
        public ProfilerOverlayMode Mode { get; set; } = ProfilerOverlayMode.Basic;
        /// <summary>是否显示覆盖层</summary>
        public bool IsVisible => Mode != ProfilerOverlayMode.None;

        /// <summary>快捷键配置</summary>
        public OverlayKeyBindings KeyBindings { get; } = new();

        // ── 帧时间图表 ──
        public FrameTimeChartData FrameTimeChart { get; } = new();
        public FrameTimeChartData CPUTimeChart { get; } = new();
        public FrameTimeChartData GPUTimeChart { get; } = new();

        // ── 峰值追踪 ──
        private double _peakMemoryBytes;
        private int _peakDrawCalls;
        private ulong _peakTriangles;

        // ── 自定义指标 ──
        private readonly Dictionary<OverlayMetric, OverlayMetricData> _customMetrics = new();
        private readonly List<OverlayMetric> _customVisibleMetrics = new();

        // ── FPS 颜色编码 ──
        private const double FPS_GoodThreshold = 55.0;
        private const double FPS_WarningThreshold = 30.0;

        // ── 上次更新时间 ──
        private DateTime _lastUpdate = DateTime.MinValue;

        public ProfilerOverlaySystem()
        {
            // 初始化所有可选指标
            foreach (OverlayMetric metric in Enum.GetValues<OverlayMetric>())
                _customMetrics[metric] = new OverlayMetricData(metric);

            SetupDefaultCustomMetrics();
        }

        private void SetupDefaultCustomMetrics()
        {
            _customMetrics[OverlayMetric.FPS].Label = "FPS";
            _customMetrics[OverlayMetric.FPS].Unit = "fps";
            _customMetrics[OverlayMetric.FPS].WarningThreshold = FPS_WarningThreshold;

            _customMetrics[OverlayMetric.FrameTime].Label = "Frame Time";
            _customMetrics[OverlayMetric.FrameTime].Unit = "ms";
            _customMetrics[OverlayMetric.FrameTime].WarningThreshold = 20.0;

            _customMetrics[OverlayMetric.DrawCalls].Label = "Draw Calls";
            _customMetrics[OverlayMetric.DrawCalls].Unit = "calls";
            _customMetrics[OverlayMetric.DrawCalls].WarningThreshold = 3000;

            _customMetrics[OverlayMetric.Triangles].Label = "Triangles";
            _customMetrics[OverlayMetric.Triangles].Unit = "tris";

            _customMetrics[OverlayMetric.Memory].Label = "Memory";
            _customMetrics[OverlayMetric.Memory].Unit = "MB";
            _customMetrics[OverlayMetric.Memory].WarningThreshold = 1024;

            _customMetrics[OverlayMetric.GPUFrameTime].Label = "GPU Time";
            _customMetrics[OverlayMetric.GPUFrameTime].Unit = "ms";

            // Custom 模式默认显示 FPS/帧时间/内存/DrawCall
            _customVisibleMetrics.AddRange(new[]
            {
                OverlayMetric.FPS, OverlayMetric.FrameTime, OverlayMetric.Memory,
                OverlayMetric.DrawCalls, OverlayMetric.Triangles
            });
        }

        // ═══════════════════ 模式切换 ═══════════════════

        /// <summary>切换到下一个显示模式</summary>
        public void CycleNextMode()
        {
            Mode = (ProfilerOverlayMode)((int)(Mode + 1) % 5);
        }

        /// <summary>切换到上一个显示模式</summary>
        public void CyclePrevMode()
        {
            int current = (int)Mode;
            Mode = (ProfilerOverlayMode)(current <= 0 ? 4 : current - 1);
        }

        /// <summary>设置显示模式</summary>
        public void SetMode(ProfilerOverlayMode mode) => Mode = mode;

        /// <summary>处理快捷键输入（由输入系统调用）</summary>
        /// <param name="key">按下的键名</param>
        /// <param name="ctrl">是否按下 Ctrl</param>
        /// <param name="shift">是否按下 Shift</param>
        public void HandleKeyDown(string key, bool ctrl = false, bool shift = false)
        {
            if (key == KeyBindings.ToggleOverlay)
            {
                Mode = Mode == ProfilerOverlayMode.None ? ProfilerOverlayMode.Basic : ProfilerOverlayMode.None;
            }
            else if (key == KeyBindings.NextMode && !shift)
            {
                CycleNextMode();
            }
            else if (key == KeyBindings.PrevMode || (key == KeyBindings.NextMode && shift))
            {
                CyclePrevMode();
            }
        }

        // ═══════════════════ 数据更新 ═══════════════════

        /// <summary>每帧更新覆盖层数据</summary>
        public void Update(
            double fps = 0,
            double frameTimeMs = 0,
            double cpuTimeMs = 0,
            double gpuTimeMs = 0,
            long memoryBytes = 0,
            long managedMemoryBytes = 0,
            long processMemoryBytes = 0,
            int drawCalls = 0,
            ulong triangles = 0,
            ulong vertices = 0,
            int renderPasses = 0,
            int shaderSwitches = 0,
            double textureMemoryMB = 0,
            double bufferMemoryMB = 0,
            int activeAudioSources = 0,
            double audioCpuMs = 0,
            double networkRTT = 0,
            double networkPacketLoss = 0,
            double networkBandwidth = 0,
            int activeAllocations = 0)
        {
            // 更新帧时间图表
            FrameTimeChart.Push((float)frameTimeMs);
            CPUTimeChart.Push((float)cpuTimeMs);
            GPUTimeChart.Push((float)gpuTimeMs);

            // 更新自定义指标值
            SetMetricValue(OverlayMetric.FPS, fps);
            SetMetricValue(OverlayMetric.FrameTime, frameTimeMs);
            SetMetricValue(OverlayMetric.CPUFrameTime, cpuTimeMs);
            SetMetricValue(OverlayMetric.GPUFrameTime, gpuTimeMs);
            SetMetricValue(OverlayMetric.Memory, memoryBytes / (1024.0 * 1024.0));
            SetMetricValue(OverlayMetric.ManagedMemory, managedMemoryBytes / (1024.0 * 1024.0));
            SetMetricValue(OverlayMetric.ProcessMemory, processMemoryBytes / (1024.0 * 1024.0));
            SetMetricValue(OverlayMetric.DrawCalls, drawCalls);
            SetMetricValue(OverlayMetric.Triangles, triangles);
            SetMetricValue(OverlayMetric.Vertices, vertices);
            SetMetricValue(OverlayMetric.RenderPasses, renderPasses);
            SetMetricValue(OverlayMetric.ShaderSwitches, shaderSwitches);
            SetMetricValue(OverlayMetric.TextureMemory, textureMemoryMB);
            SetMetricValue(OverlayMetric.BufferMemory, bufferMemoryMB);
            SetMetricValue(OverlayMetric.ActiveAudioSources, activeAudioSources);
            SetMetricValue(OverlayMetric.AudioCPU, audioCpuMs);
            SetMetricValue(OverlayMetric.NetworkRTT, networkRTT);
            SetMetricValue(OverlayMetric.NetworkPacketLoss, networkPacketLoss);
            SetMetricValue(OverlayMetric.NetworkBandwidth, networkBandwidth);
            SetMetricValue(OverlayMetric.ActiveAllocations, activeAllocations);

            // 峰值
            if (memoryBytes > _peakMemoryBytes) _peakMemoryBytes = memoryBytes;
            if (drawCalls > _peakDrawCalls) _peakDrawCalls = drawCalls;
            if (triangles > _peakTriangles) _peakTriangles = triangles;

            // 自动格式化
            FormatMetricValues();

            _lastUpdate = DateTime.UtcNow;
        }

        private void SetMetricValue(OverlayMetric metric, double value)
        {
            if (_customMetrics.TryGetValue(metric, out var data))
            {
                data.Value = value;
                data.IsWarning = data.WarningThreshold != double.MaxValue && value > data.WarningThreshold;
            }
        }

        private void FormatMetricValues()
        {
            if (_customMetrics.TryGetValue(OverlayMetric.FPS, out var fps))
                fps.FormattedValue = $"{fps.Value:F1} {fps.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.FrameTime, out var ft))
                ft.FormattedValue = $"{ft.Value:F2} {ft.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.CPUFrameTime, out var cpu))
                cpu.FormattedValue = $"{cpu.Value:F2} {cpu.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.GPUFrameTime, out var gpu))
                gpu.FormattedValue = $"{gpu.Value:F2} {gpu.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.Memory, out var mem))
                mem.FormattedValue = $"{mem.Value:F1} {mem.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.ManagedMemory, out var mMem))
                mMem.FormattedValue = $"{mMem.Value:F1} {mMem.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.DrawCalls, out var dc))
                dc.FormattedValue = $"{(int)dc.Value:N0} {dc.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.Triangles, out var tri))
                tri.FormattedValue = $"{tri.Value:N0} {tri.Unit}";
            if (_customMetrics.TryGetValue(OverlayMetric.Vertices, out var vtx))
                vtx.FormattedValue = $"{vtx.Value:N0} {vtx.Unit}";
        }

        // ═══════════════════ 文本生成 ═══════════════════

        /// <summary>生成覆盖层文本（供 ImGui/自绘渲染使用）</summary>
        public string GenerateOverlayText()
        {
            if (!IsVisible) return string.Empty;

            var sb = new StringBuilder(2048);

            switch (Mode)
            {
                case ProfilerOverlayMode.FPS:
                    GenerateFPSText(sb);
                    break;
                case ProfilerOverlayMode.Basic:
                    GenerateFPSText(sb);
                    GenerateBasicText(sb);
                    break;
                case ProfilerOverlayMode.Full:
                    GenerateFPSText(sb);
                    GenerateBasicText(sb);
                    GenerateGPUText(sb);
                    GenerateMemoryText(sb);
                    GenerateAudioText(sb);
                    GenerateNetworkText(sb);
                    break;
                case ProfilerOverlayMode.Custom:
                    GenerateCustomText(sb);
                    break;
            }

            return sb.ToString();
        }

        /// <summary>获取 FPS 颜色（ARGB）</summary>
        public uint GetFPSColor()
        {
            if (_customMetrics.TryGetValue(OverlayMetric.FPS, out var fps))
            {
                if (fps.Value >= FPS_GoodThreshold) return 0xFF4ECCA3;    // 绿
                if (fps.Value >= FPS_WarningThreshold) return 0xFFF0A500; // 黄
                return 0xFFFF4444; // 红
            }
            return 0xFFFFFFFF;
        }

        /// <summary>获取 FPS 颜色字符串（用于 ImGui）</summary>
        public string GetFPSColorHex()
        {
            uint color = GetFPSColor();
            byte r = (byte)((color >> 16) & 0xFF);
            byte g = (byte)((color >> 8) & 0xFF);
            byte b = (byte)(color & 0xFF);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private void GenerateFPSText(StringBuilder sb)
        {
            if (_customMetrics.TryGetValue(OverlayMetric.FPS, out var fps))
                sb.AppendLine($"  {fps.FormattedValue}");
        }

        private void GenerateBasicText(StringBuilder sb)
        {
            sb.AppendLine($"  Frame: {_customMetrics[OverlayMetric.FrameTime].FormattedValue}");
            sb.AppendLine($"  CPU:   {_customMetrics[OverlayMetric.CPUFrameTime].FormattedValue}");
            sb.AppendLine($"  GPU:   {_customMetrics[OverlayMetric.GPUFrameTime].FormattedValue}");
            sb.AppendLine($"  Mem:   {_customMetrics[OverlayMetric.Memory].FormattedValue}");
            sb.AppendLine($"  Peak:  {FormatBytes(_peakMemoryBytes)}");
        }

        private void GenerateGPUText(StringBuilder sb)
        {
            sb.AppendLine("  -- GPU --");
            sb.AppendLine($"  DrawCalls: {_customMetrics[OverlayMetric.DrawCalls].FormattedValue}");
            sb.AppendLine($"  Triangles: {_customMetrics[OverlayMetric.Triangles].FormattedValue}");
            sb.AppendLine($"  Vertices:  {_customMetrics[OverlayMetric.Vertices].FormattedValue}");
            sb.AppendLine($"  Passes:    {_customMetrics[OverlayMetric.RenderPasses].FormattedValue}");
            sb.AppendLine($"  Shader Sw: {_customMetrics[OverlayMetric.ShaderSwitches].FormattedValue}");
            sb.AppendLine($"  Tex Mem:   {_customMetrics[OverlayMetric.TextureMemory].FormattedValue} MB");
            sb.AppendLine($"  Buf Mem:   {_customMetrics[OverlayMetric.BufferMemory].FormattedValue} MB");
        }

        private void GenerateMemoryText(StringBuilder sb)
        {
            sb.AppendLine("  -- Memory --");
            sb.AppendLine($"  Total:     {_customMetrics[OverlayMetric.Memory].FormattedValue}");
            sb.AppendLine($"  Managed:   {_customMetrics[OverlayMetric.ManagedMemory].FormattedValue}");
            sb.AppendLine($"  Process:   {_customMetrics[OverlayMetric.ProcessMemory].FormattedValue}");
            sb.AppendLine($"  Peak:      {FormatBytes(_peakMemoryBytes)}");
            sb.AppendLine($"  Allocs:    {(int)_customMetrics[OverlayMetric.ActiveAllocations].Value}");
        }

        private void GenerateAudioText(StringBuilder sb)
        {
            sb.AppendLine("  -- Audio --");
            sb.AppendLine($"  Sources:  {(int)_customMetrics[OverlayMetric.ActiveAudioSources].Value}");
            sb.AppendLine($"  CPU:      {_customMetrics[OverlayMetric.AudioCPU].FormattedValue}");
        }

        private void GenerateNetworkText(StringBuilder sb)
        {
            sb.AppendLine("  -- Network --");
            sb.AppendLine($"  RTT:      {_customMetrics[OverlayMetric.NetworkRTT].FormattedValue} ms");
            sb.AppendLine($"  Loss:     {_customMetrics[OverlayMetric.NetworkPacketLoss].Value * 100:F1}%");
        }

        private void GenerateCustomText(StringBuilder sb)
        {
            foreach (var metric in _customVisibleMetrics)
            {
                if (_customMetrics.TryGetValue(metric, out var data) && data.Visible)
                {
                    string prefix = data.IsWarning ? "!! " : "   ";
                    sb.AppendLine($"{prefix}{data.Label}: {data.FormattedValue}");
                }
            }
        }

        // ═══════════════════ 自定义指标管理 ═══════════════════

        /// <summary>设置自定义指标可见性</summary>
        public void SetCustomMetricVisible(OverlayMetric metric, bool visible)
        {
            if (_customMetrics.TryGetValue(metric, out var data))
                data.Visible = visible;
        }

        /// <summary>添加自定义显示指标</summary>
        public void AddCustomVisibleMetric(OverlayMetric metric)
        {
            if (!_customVisibleMetrics.Contains(metric))
                _customVisibleMetrics.Add(metric);
        }

        /// <summary>移除自定义显示指标</summary>
        public void RemoveCustomVisibleMetric(OverlayMetric metric)
        {
            _customVisibleMetrics.Remove(metric);
        }

        /// <summary>获取自定义指标数据</summary>
        public OverlayMetricData? GetMetricData(OverlayMetric metric)
        {
            return _customMetrics.TryGetValue(metric, out var data) ? data : null;
        }

        /// <summary>获取所有自定义指标</summary>
        public IReadOnlyDictionary<OverlayMetric, OverlayMetricData> GetAllMetrics() => _customMetrics;

        /// <summary>重置峰值</summary>
        public void ResetPeaks()
        {
            _peakMemoryBytes = 0;
            _peakDrawCalls = 0;
            _peakTriangles = 0;
        }

        /// <summary>重置所有数据</summary>
        public void Reset()
        {
            FrameTimeChart.Reset();
            CPUTimeChart.Reset();
            GPUTimeChart.Reset();
            ResetPeaks();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
