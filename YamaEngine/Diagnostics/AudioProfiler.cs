using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace YamaEngine.Diagnostics
{
    // ══════════════════════════════════════════════════════════════════
    // 音频性能分析器（AudioProfiler）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现完整的音频性能分析子系统，包含：
    //   1. 音频源统计（活跃源数/最大源数/混音通道）
    //   2. 音频 CPU（解码/混音/处理耗时）
    //   3. 音频内存（加载的音频数据/缓存/流式缓冲区）
    //   4. 音频事件（播放/停止/暂停时间线）
    //   5. 音频瓶颈检测（源数超限/解码慢/缓冲区下溢）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频源统计。
    /// </summary>
    public sealed class AudioSourceStats
    {
        /// <summary>当前活跃音频源数</summary>
        public int ActiveSources { get; private set; }
        /// <summary>最大音频源数（硬件/驱动限制）</summary>
        public int MaxSources { get; set; } = 32;
        /// <summary>混音通道数</summary>
        public int MixingChannels { get; set; } = 2;
        /// <summary>采样率（Hz）</summary>
        public int SampleRate { get; set; } = 44100;
        /// <summary>比特深度</summary>
        public int BitDepth { get; set; } = 16;
        /// <summary>当前帧播放的源数峰值</summary>
        public int PeakSources { get; private set; }
        /// <summary>虚拟源数（超过最大数被虚拟化的源）</summary>
        public int VirtualSources { get; private set; }

        /// <summary>更新活跃源数</summary>
        public void SetActiveSources(int active, int virtualized)
        {
            ActiveSources = active;
            VirtualSources = virtualized;
            if (active > PeakSources) PeakSources = active;
        }

        /// <summary>源利用率</summary>
        public double SourceUtilization => MaxSources > 0 ? (double)ActiveSources / MaxSources : 0;
    }

    /// <summary>
    /// 音频 CPU 统计。各阶段的 CPU 耗时。
    /// </summary>
    public sealed class AudioCPUStats
    {
        /// <summary>解码耗时（毫秒）</summary>
        public double DecodeMs { get; private set; }
        /// <summary>混音耗时（毫秒）</summary>
        public double MixingMs { get; private set; }
        /// <summary>效果处理耗时（毫秒，DSP/滤波/混响）</summary>
        public double ProcessingMs { get; private set; }
        /// <summary>流式读取耗时（毫秒）</summary>
        public double StreamingMs { get; private set; }
        /// <summary>总音频 CPU 耗时（毫秒）</summary>
        public double TotalMs => DecodeMs + MixingMs + ProcessingMs + StreamingMs;

        // ── 累计统计 ──
        private long _decodeFrames;
        private double _decodeTotalMs;
        private long _mixingFrames;
        private double _mixingTotalMs;
        private long _processingFrames;
        private double _processingTotalMs;
        private long _streamingFrames;
        private double _streamingTotalMs;

        /// <summary>平均解码耗时</summary>
        public double AvgDecodeMs => _decodeFrames > 0 ? _decodeTotalMs / _decodeFrames : 0;
        /// <summary>平均混音耗时</summary>
        public double AvgMixingMs => _mixingFrames > 0 ? _mixingTotalMs / _mixingFrames : 0;
        /// <summary>平均处理耗时</summary>
        public double AvgProcessingMs => _processingFrames > 0 ? _processingTotalMs / _processingFrames : 0;
        /// <summary>平均流式读取耗时</summary>
        public double AvgStreamingMs => _streamingFrames > 0 ? _streamingTotalMs / _streamingFrames : 0;

        /// <summary>记录本帧行的解码耗时</summary>
        public void RecordDecode(double ms)
        {
            DecodeMs = ms;
            _decodeFrames++;
            _decodeTotalMs += ms;
        }

        /// <summary>记录本帧行的混音耗时</summary>
        public void RecordMixing(double ms)
        {
            MixingMs = ms;
            _mixingFrames++;
            _mixingTotalMs += ms;
        }

        /// <summary>记录本帧行的处理耗时</summary>
        public void RecordProcessing(double ms)
        {
            ProcessingMs = ms;
            _processingFrames++;
            _processingTotalMs += ms;
        }

        /// <summary>记录本帧行的流式读取耗时</summary>
        public void RecordStreaming(double ms)
        {
            StreamingMs = ms;
            _streamingFrames++;
            _streamingTotalMs += ms;
        }

        /// <summary>重置本帧数据</summary>
        public void ResetFrame()
        {
            DecodeMs = 0;
            MixingMs = 0;
            ProcessingMs = 0;
            StreamingMs = 0;
        }

        /// <summary>重置所有数据</summary>
        public void ResetAll()
        {
            ResetFrame();
            _decodeFrames = 0; _decodeTotalMs = 0;
            _mixingFrames = 0; _mixingTotalMs = 0;
            _processingFrames = 0; _processingTotalMs = 0;
            _streamingFrames = 0; _streamingTotalMs = 0;
        }
    }

    /// <summary>
    /// 音频内存统计。
    /// </summary>
    public sealed class AudioMemoryStats
    {
        /// <summary>已加载的音频数据（字节）</summary>
        public long LoadedDataBytes { get; private set; }
        /// <summary>解码缓存（字节）</summary>
        public long DecodeCacheBytes { get; private set; }
        /// <summary>流式缓冲区（字节）</summary>
        public long StreamingBufferBytes { get; private set; }
        /// <summary>效果器缓冲区（字节）</summary>
        public long EffectBufferBytes { get; private set; }
        /// <summary>总音频内存（字节）</summary>
        public long TotalBytes => LoadedDataBytes + DecodeCacheBytes + StreamingBufferBytes + EffectBufferBytes;

        /// <summary>记录加载音频数据</summary>
        public void RecordLoad(long bytes) => LoadedDataBytes += bytes;

        /// <summary>记录释放音频数据</summary>
        public void RecordUnload(long bytes) => LoadedDataBytes = Math.Max(0, LoadedDataBytes - bytes);

        /// <summary>设置解码缓存大小</summary>
        public void SetDecodeCache(long bytes) => DecodeCacheBytes = bytes;

        /// <summary>设置流式缓冲区大小</summary>
        public void SetStreamingBuffer(long bytes) => StreamingBufferBytes = bytes;

        /// <summary>设置效果器缓冲区大小</summary>
        public void SetEffectBuffer(long bytes) => EffectBufferBytes = bytes;

        /// <summary>重置</summary>
        public void Reset()
        {
            LoadedDataBytes = 0;
            DecodeCacheBytes = 0;
            StreamingBufferBytes = 0;
            EffectBufferBytes = 0;
        }
    }

    /// <summary>
    /// 音频事件类型。
    /// </summary>
    public enum AudioEventType
    {
        /// <summary>开始播放</summary>
        Play,
        /// <summary>停止</summary>
        Stop,
        /// <summary>暂停</summary>
        Pause,
        /// <summary>恢复</summary>
        Resume,
        /// <summary>加载</summary>
        Load,
        /// <summary>卸载</summary>
        Unload,
        /// <summary>缓冲区下溢</summary>
        BufferUnderrun,
        /// <summary>源数超限</summary>
        SourceLimitReached
    }

    /// <summary>
    /// 音频事件记录。
    /// </summary>
    public sealed class AudioEvent
    {
        public AudioEventType EventType { get; }
        public double TimeMs { get; set; }
        public string AudioName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public AudioEvent(AudioEventType type) { EventType = type; }
    }

    /// <summary>
    /// 音频瓶颈类型。
    /// </summary>
    public enum AudioBottleneckType
    {
        /// <summary>无瓶颈</summary>
        None,
        /// <summary>音频源数超限</summary>
        SourceLimit,
        /// <summary>解码速度不足</summary>
        DecodeSlow,
        /// <summary>缓冲区下溢</summary>
        BufferUnderrun,
        /// <summary>混音 CPU 占用过高</summary>
        MixingCPUHigh
    }

    /// <summary>
    /// 音频瓶颈检测结果。
    /// </summary>
    public sealed class AudioBottleneckResult
    {
        public AudioBottleneckType Type { get; set; } = AudioBottleneckType.None;
        /// <summary>置信度（0~1）</summary>
        public double Confidence { get; set; }
        /// <summary>描述</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>建议</summary>
        public string Recommendation { get; set; } = string.Empty;
    }

    /// <summary>
    /// 音频性能分析器。提供完整的音频性能分析能力，
    /// 包括源统计、CPU 耗时、内存追踪、事件时间线与瓶颈检测。
    /// </summary>
    public sealed class AudioProfiler
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        // ── 源统计 ──
        private readonly AudioSourceStats _sourceStats = new();

        // ── CPU 统计 ──
        private readonly AudioCPUStats _cpuStats = new();

        // ── 内存统计 ──
        private readonly AudioMemoryStats _memoryStats = new();

        // ── 事件时间线 ──
        private readonly List<AudioEvent> _events = new();
        private const int MaxEvents = 2048;

        // ── 瓶颈检测 ──
        private int _bufferUnderrunCount;
        private int _sourceLimitReachedCount;
        private AudioBottleneckResult _lastBottleneck = new();

        // ── 缓冲区下溢历史 ──
        private readonly List<double> _underrunTimes = new();
        private const int MaxUnderrunHistory = 256;

        // ── 音频标签缓存（名称 -> 播放次数） ──
        private readonly Dictionary<string, int> _playCountByAudio = new();
        private readonly object _playCountLock = new();

        /// <summary>事件：瓶颈检测到问题时触发</summary>
        public event Action<AudioBottleneckResult>? OnBottleneckDetected;

        // ═══════════════════ 源统计 ═══════════════════

        /// <summary>获取源统计</summary>
        public AudioSourceStats GetSourceStats() => _sourceStats;

        /// <summary>更新活跃源数</summary>
        public void SetActiveSources(int active, int virtualized = 0)
        {
            _sourceStats.SetActiveSources(active, virtualized);

            if (active >= _sourceStats.MaxSources)
            {
                _sourceLimitReachedCount++;
                RecordEvent(AudioEventType.SourceLimitReached,
                    description: $"活跃源数({active})达到最大限制({_sourceStats.MaxSources})");
            }
        }

        // ═══════════════════ CPU ═══════════════════

        /// <summary>获取 CPU 统计</summary>
        public AudioCPUStats GetCPUStats() => _cpuStats;

        /// <summary>记录解码耗时</summary>
        public void RecordDecode(double ms) => _cpuStats.RecordDecode(ms);
        /// <summary>记录混音耗时</summary>
        public void RecordMixing(double ms) => _cpuStats.RecordMixing(ms);
        /// <summary>记录处理耗时</summary>
        public void RecordProcessing(double ms) => _cpuStats.RecordProcessing(ms);
        /// <summary>记录流式读取耗时</summary>
        public void RecordStreaming(double ms) => _cpuStats.RecordStreaming(ms);

        // ═══════════════════ 内存 ═══════════════════

        /// <summary>获取内存统计</summary>
        public AudioMemoryStats GetMemoryStats() => _memoryStats;

        /// <summary>记录音频加载</summary>
        public void RecordAudioLoad(string name, long bytes)
        {
            _memoryStats.RecordLoad(bytes);
            RecordEvent(AudioEventType.Load, name, $"加载音频: {name} ({FormatBytes(bytes)})");

            lock (_playCountLock)
            {
                if (!_playCountByAudio.ContainsKey(name))
                    _playCountByAudio[name] = 0;
            }
        }

        /// <summary>记录音频卸载</summary>
        public void RecordAudioUnload(string name, long bytes)
        {
            _memoryStats.RecordUnload(bytes);
            RecordEvent(AudioEventType.Unload, name, $"卸载音频: {name}");

            lock (_playCountLock)
                _playCountByAudio.Remove(name);
        }

        // ═══════════════════ 事件 ═══════════════════

        /// <summary>记录播放事件</summary>
        public void RecordPlay(string audioName)
        {
            RecordEvent(AudioEventType.Play, audioName, $"播放: {audioName}");
            lock (_playCountLock)
            {
                if (_playCountByAudio.ContainsKey(audioName))
                    _playCountByAudio[audioName]++;
            }
        }

        /// <summary>记录停止事件</summary>
        public void RecordStop(string audioName) =>
            RecordEvent(AudioEventType.Stop, audioName, $"停止: {audioName}");

        /// <summary>记录暂停事件</summary>
        public void RecordPause(string audioName) =>
            RecordEvent(AudioEventType.Pause, audioName, $"暂停: {audioName}");

        /// <summary>记录恢复事件</summary>
        public void RecordResume(string audioName) =>
            RecordEvent(AudioEventType.Resume, audioName, $"恢复: {audioName}");

        /// <summary>记录缓冲区下溢</summary>
        public void RecordBufferUnderrun(string audioName)
        {
            _bufferUnderrunCount++;
            _underrunTimes.Add(_stopwatch.Elapsed.TotalMilliseconds);
            if (_underrunTimes.Count > MaxUnderrunHistory)
                _underrunTimes.RemoveAt(0);

            RecordEvent(AudioEventType.BufferUnderrun, audioName,
                $"缓冲区下溢: {audioName}");
        }

        /// <summary>记录自定义事件</summary>
        public void RecordEvent(AudioEventType type, string audioName = "", string description = "")
        {
            _events.Add(new AudioEvent(type)
            {
                TimeMs = _stopwatch.Elapsed.TotalMilliseconds,
                AudioName = audioName,
                Description = description
            });

            if (_events.Count > MaxEvents)
                _events.RemoveAt(0);
        }

        /// <summary>获取事件列表</summary>
        public IReadOnlyList<AudioEvent> GetEvents() => _events;

        /// <summary>获取播放次数统计</summary>
        public IReadOnlyDictionary<string, int> GetPlayCounts()
        {
            lock (_playCountLock)
                return new Dictionary<string, int>(_playCountByAudio);
        }

        /// <summary>获取缓冲区下溢次数</summary>
        public int GetBufferUnderrunCount() => _bufferUnderrunCount;

        /// <summary>获取最近 N 分钟的缓冲区下溢次数</summary>
        public int GetRecentUnderrunCount(double minutes = 1.0)
        {
            double cutoff = _stopwatch.Elapsed.TotalMilliseconds - minutes * 60000;
            int count = 0;
            foreach (var t in _underrunTimes)
                if (t >= cutoff) count++;
            return count;
        }

        // ═══════════════════ 瓶颈检测 ═══════════════════

        /// <summary>运行音频瓶颈检测</summary>
        public AudioBottleneckResult DetectBottleneck()
        {
            var result = new AudioBottleneckResult();

            // 检测源数超限
            if (_sourceStats.SourceUtilization > 0.95)
            {
                result.Type = AudioBottleneckType.SourceLimit;
                result.Confidence = _sourceStats.SourceUtilization;
                result.Description = $"音频源接近上限: {_sourceStats.ActiveSources}/{_sourceStats.MaxSources}";
                result.Recommendation = "减少同时播放的音频源数，使用虚拟化或优先级系统";
            }
            // 检测缓冲区下溢
            else if (_bufferUnderrunCount > 0)
            {
                result.Type = AudioBottleneckType.BufferUnderrun;
                result.Confidence = Math.Min(1.0, _bufferUnderrunCount / 10.0);
                result.Description = $"缓冲区下溢 {_bufferUnderrunCount} 次";
                result.Recommendation = "增加流式缓冲区大小，降低音频解码延迟";
            }
            // 检测解码慢
            else if (_cpuStats.AvgDecodeMs > 2.0)
            {
                result.Type = AudioBottleneckType.DecodeSlow;
                result.Confidence = Math.Min(1.0, _cpuStats.AvgDecodeMs / 5.0);
                result.Description = $"平均解码耗时过高: {_cpuStats.AvgDecodeMs:F2}ms";
                result.Recommendation = "使用流式解码替代完整加载，预解码常用音频";
            }
            // 检测混音 CPU 高
            else if (_cpuStats.AvgMixingMs > 1.0)
            {
                result.Type = AudioBottleneckType.MixingCPUHigh;
                result.Confidence = Math.Min(1.0, _cpuStats.AvgMixingMs / 3.0);
                result.Description = $"混音 CPU 占用高: {_cpuStats.AvgMixingMs:F2}ms";
                result.Recommendation = "减少活跃源数，简化混音效果";
            }
            else
            {
                result.Type = AudioBottleneckType.None;
                result.Confidence = 0;
                result.Description = "未检测到音频瓶颈";
                result.Recommendation = string.Empty;
            }

            _lastBottleneck = result;

            if (result.Type != AudioBottleneckType.None)
                OnBottleneckDetected?.Invoke(result);

            return result;
        }

        /// <summary>获取上次瓶颈检测结果</summary>
        public AudioBottleneckResult GetLastBottleneck() => _lastBottleneck;

        // ═══════════════════ 帧更新 ═══════════════════

        /// <summary>帧结束时重置本帧行的数据</summary>
        public void OnFrameEnd()
        {
            _cpuStats.ResetFrame();
        }

        /// <summary>重置所有数据</summary>
        public void Reset()
        {
            _cpuStats.ResetAll();
            _memoryStats.Reset();
            _events.Clear();
            _bufferUnderrunCount = 0;
            _sourceLimitReachedCount = 0;
            _underrunTimes.Clear();
            _lastBottleneck = new AudioBottleneckResult();
            lock (_playCountLock) _playCountByAudio.Clear();
        }

        // ── 工具 ──
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }
    }
}
