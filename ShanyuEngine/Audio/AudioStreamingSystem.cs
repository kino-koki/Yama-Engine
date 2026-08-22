using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 音频流式系统 (Audio Streaming System)
    //
    // 流式音频源、流式管理、缓冲策略 (双缓冲/三缓冲/环形缓冲)、
    // 运行时解码线程、网络音频流、音乐无缝循环。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  枚举
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>流式缓冲策略。</summary>
    public enum StreamingBufferStrategy
    {
        /// <summary>双缓冲。</summary>
        Double,
        /// <summary>三缓冲。</summary>
        Triple,
        /// <summary>环形缓冲。</summary>
        Ring
    }

    /// <summary>流式源类型。</summary>
    public enum StreamSourceType
    {
        File,
        Network,
        Memory
    }

    /// <summary>流式状态。</summary>
    public enum StreamState
    {
        Closed,
        Opening,
        Buffering,
        Playing,
        Paused,
        Ended,
        Error
    }

    /// <summary>网络流协议。</summary>
    public enum NetworkStreamProtocol
    {
        HTTP,
        RTSP,
        Custom
    }

    // ──────────────────────────────────────────────────────────────────────
    //  StreamingBuffer — 流式缓冲区
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>环形缓冲区实现。</summary>
    public sealed class RingBuffer
    {
        private readonly float[] _buffer;
        private int _readPos;
        private int _writePos;
        private int _count;
        private readonly int _capacity;

        /// <summary>缓冲区容量 (采样数)。</summary>
        public int Capacity => _capacity;

        /// <summary>当前可用数据量。</summary>
        public int AvailableRead => _count;

        /// <summary>剩余可写空间。</summary>
        public int AvailableWrite => _capacity - _count;

        /// <summary>是否为空。</summary>
        public bool IsEmpty => _count == 0;

        /// <summary>是否已满。</summary>
        public bool IsFull => _count == _capacity;

        public RingBuffer(int capacity)
        {
            _capacity = capacity;
            _buffer = new float[capacity];
        }

        /// <summary>写入数据到缓冲区，返回实际写入量。</summary>
        public int Write(float[] data, int offset, int count)
        {
            int toWrite = Math.Min(count, AvailableWrite);
            if (toWrite <= 0) return 0;

            // 分两段写入 (环形)
            int firstPart = Math.Min(toWrite, _capacity - _writePos);
            Array.Copy(data, offset, _buffer, _writePos, firstPart);

            if (firstPart < toWrite)
                Array.Copy(data, offset + firstPart, _buffer, 0, toWrite - firstPart);

            _writePos = (_writePos + toWrite) % _capacity;
            _count += toWrite;
            return toWrite;
        }

        /// <summary>从缓冲区读取数据，返回实际读取量。</summary>
        public int Read(float[] output, int offset, int count)
        {
            int toRead = Math.Min(count, AvailableRead);
            if (toRead <= 0) return 0;

            int firstPart = Math.Min(toRead, _capacity - _readPos);
            Array.Copy(_buffer, _readPos, output, offset, firstPart);

            if (firstPart < toRead)
                Array.Copy(_buffer, 0, output, offset + firstPart, toRead - firstPart);

            _readPos = (_readPos + toRead) % _capacity;
            _count -= toRead;
            return toRead;
        }

        /// <summary>清空缓冲区。</summary>
        public void Clear()
        {
            _readPos = 0;
            _writePos = 0;
            _count = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioStream — 流式音频源
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>流式音频源基类。</summary>
    public abstract class AudioStream : IDisposable
    {
        /// <summary>流名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>源类型。</summary>
        public abstract StreamSourceType SourceType { get; }

        /// <summary>当前状态。</summary>
        public StreamState State { get; protected set; } = StreamState.Closed;

        /// <summary>采样率。</summary>
        public int SampleRate { get; protected set; } = 44100;

        /// <summary>声道数。</summary>
        public int Channels { get; protected set; } = 2;

        /// <summary>音频格式。</summary>
        public AudioFormat Format { get; protected set; } = AudioFormat.Float32;

        /// <summary>总时长 (秒, -1 = 未知/无限)。</summary>
        public float TotalDuration { get; protected set; } = -1f;

        /// <summary>当前播放位置 (秒)。</summary>
        public float CurrentPosition { get; protected set; }

        /// <summary>是否循环。</summary>
        public bool Looping { get; set; }

        /// <summary>读取 PCM 采样数据。</summary>
        /// <returns>实际读取的采样数 (每个声道计一个)。</returns>
        public abstract int ReadSamples(float[] buffer, int offset, int count);

        /// <summary>定位到指定位置 (秒)。</summary>
        public abstract bool Seek(float positionSeconds);

        /// <summary>打开流。</summary>
        public abstract bool Open();

        /// <summary>关闭流。</summary>
        public abstract void Close();

        /// <summary>获取缓冲区填充率 [0,1]。</summary>
        public abstract float GetBufferFillRatio();

        public void Dispose()
        {
            Close();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  FileStreamAudio — 文件流式音频
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>基于文件的流式音频源。</summary>
    public sealed class FileStreamAudio : AudioStream
    {
        public override StreamSourceType SourceType => StreamSourceType.File;

        /// <summary>文件路径。</summary>
        public string FilePath { get; }

        /// <summary>预读取缓冲区大小 (采样数)。</summary>
        public int PreReadBufferSize { get; set; } = 44100 * 2; // ~1 秒

        private RingBuffer? _ringBuffer;
        private Stream? _fileStream;
        private bool _disposed;
        private long _totalSamples;
        private long _currentSample;

        public FileStreamAudio(string filePath)
        {
            FilePath = filePath;
            Name = System.IO.Path.GetFileName(filePath);
        }

        public override bool Open()
        {
            if (State != StreamState.Closed) return false;

            try
            {
                _fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                _ringBuffer = new RingBuffer(PreReadBufferSize * 4);
                _totalSamples = _fileStream.Length / sizeof(float);
                State = StreamState.Buffering;
                return true;
            }
            catch
            {
                State = StreamState.Error;
                return false;
            }
        }

        public override void Close()
        {
            if (_disposed) return;
            State = StreamState.Closed;
            _fileStream?.Close();
            _fileStream = null;
            _ringBuffer = null;
            _disposed = true;
        }

        public override int ReadSamples(float[] buffer, int offset, int count)
        {
            if (_ringBuffer == null || State == StreamState.Closed || State == StreamState.Error)
                return 0;

            int read = _ringBuffer.Read(buffer, offset, count);

            // 如果缓冲区数据不足，尝试从文件补充
            if (read < count && _fileStream != null)
            {
                int needed = count - read;
                byte[] tempBytes = new byte[needed * sizeof(float)];
                int bytesToRead = Math.Min(tempBytes.Length, (int)(_fileStream.Length - _fileStream.Position));
                if (bytesToRead > 0)
                {
                    int bytesRead = _fileStream.Read(tempBytes, 0, bytesToRead);
                    // 简化: 假设文件存储为 float PCM
                    int samplesRead = bytesRead / sizeof(float);
                    for (int i = 0; i < samplesRead; i++)
                        buffer[offset + read + i] = BitConverter.ToSingle(tempBytes, i * sizeof(float));
                    read += samplesRead;
                    _currentSample += samplesRead;
                }
            }

            if (read == 0 && Looping && _fileStream != null)
            {
                _fileStream.Position = 0;
                _currentSample = 0;
                return ReadSamples(buffer, offset, count);
            }

            if (read == 0 && !Looping)
                State = StreamState.Ended;

            CurrentPosition = _totalSamples > 0 ? (float)_currentSample / SampleRate : 0f;

            if (State == StreamState.Buffering && read > 0)
                State = StreamState.Playing;

            return read;
        }

        public override bool Seek(float positionSeconds)
        {
            if (_fileStream == null) return false;
            long samplePos = (long)(positionSeconds * SampleRate * Channels);
            _fileStream.Seek(samplePos * sizeof(float), SeekOrigin.Begin);
            _currentSample = samplePos;
            CurrentPosition = positionSeconds;
            return true;
        }

        public override float GetBufferFillRatio()
        {
            if (_ringBuffer == null) return 0f;
            return (float)_ringBuffer.AvailableRead / _ringBuffer.Capacity;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  NetworkStreamAudio — 网络流式音频
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>网络音频流。</summary>
    public sealed class NetworkStreamAudio : AudioStream
    {
        public override StreamSourceType SourceType => StreamSourceType.Network;

        /// <summary>URL。</summary>
        public string Url { get; }

        /// <summary>协议。</summary>
        public NetworkStreamProtocol Protocol { get; set; } = NetworkStreamProtocol.HTTP;

        /// <summary>缓冲区延迟 (秒, 用于延迟补偿)。</summary>
        public float BufferLatencySeconds { get; set; } = 2f;

        /// <summary>目标缓冲大小 (秒)。</summary>
        public float TargetBufferSeconds { get; set; } = 3f;

        private RingBuffer? _ringBuffer;
        private bool _disposed;

        public NetworkStreamAudio(string url)
        {
            Url = url;
            Name = url;
        }

        public override bool Open()
        {
            if (State != StreamState.Closed) return false;

            int bufferSize = (int)(TargetBufferSeconds * SampleRate * Channels);
            _ringBuffer = new RingBuffer(bufferSize);
            State = StreamState.Buffering;
            return true;
        }

        public override void Close()
        {
            if (_disposed) return;
            State = StreamState.Closed;
            _ringBuffer = null;
            _disposed = true;
        }

        public override int ReadSamples(float[] buffer, int offset, int count)
        {
            if (_ringBuffer == null) return 0;
            int read = _ringBuffer.Read(buffer, offset, count);

            if (read == 0)
            {
                // 网络缓冲区空，用静音填充 (模拟)
                Array.Clear(buffer, offset, count);
                read = count;
            }

            if (State == StreamState.Buffering && _ringBuffer.AvailableRead > _ringBuffer.Capacity * 0.5f)
                State = StreamState.Playing;

            return read;
        }

        /// <summary>向网络流缓冲区注入数据 (由网络接收线程调用)。</summary>
        public void InjectData(float[] data, int offset, int count)
        {
            _ringBuffer?.Write(data, offset, count);
        }

        public override bool Seek(float positionSeconds)
        {
            // 网络流不支持精确 Seek
            return false;
        }

        public override float GetBufferFillRatio()
        {
            if (_ringBuffer == null) return 0f;
            return (float)_ringBuffer.AvailableRead / _ringBuffer.Capacity;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  MemoryStreamAudio — 内存流式音频
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>内存流式音频。</summary>
    public sealed class MemoryStreamAudio : AudioStream
    {
        public override StreamSourceType SourceType => StreamSourceType.Memory;

        private float[]? _data;
        private int _readPosition;

        /// <summary>总采样数。</summary>
        public int TotalSamples => _data?.Length ?? 0;

        public MemoryStreamAudio(string name = "MemoryStream")
        {
            Name = name;
        }

        /// <summary>加载内存数据。</summary>
        public void LoadData(float[] pcmData, int channels, int sampleRate)
        {
            _data = pcmData;
            Channels = channels;
            SampleRate = sampleRate;
            TotalDuration = (float)pcmData.Length / (sampleRate * channels);
        }

        public override bool Open()
        {
            if (_data == null) return false;
            _readPosition = 0;
            State = StreamState.Playing;
            return true;
        }

        public override void Close()
        {
            State = StreamState.Closed;
            _readPosition = 0;
        }

        public override int ReadSamples(float[] buffer, int offset, int count)
        {
            if (_data == null) return 0;

            int available = _data.Length - _readPosition;
            int toRead = Math.Min(count, available);

            if (toRead <= 0)
            {
                if (Looping)
                {
                    _readPosition = 0;
                    return ReadSamples(buffer, offset, count);
                }
                State = StreamState.Ended;
                return 0;
            }

            Array.Copy(_data, _readPosition, buffer, offset, toRead);
            _readPosition += toRead;
            CurrentPosition = (float)_readPosition / (SampleRate * Channels);
            return toRead;
        }

        public override bool Seek(float positionSeconds)
        {
            if (_data == null) return false;
            int samplePos = (int)(positionSeconds * SampleRate * Channels);
            _readPosition = Math.Clamp(samplePos, 0, _data.Length);
            CurrentPosition = positionSeconds;
            return true;
        }

        public override float GetBufferFillRatio()
        {
            if (_data == null) return 0f;
            return 1f - (float)_readPosition / _data.Length;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioStreamingManager — 流式管理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>流式解码任务。</summary>
    public sealed class StreamDecodeTask
    {
        /// <summary>任务 ID。</summary>
        public int TaskId { get; }

        /// <summary>关联的流。</summary>
        public AudioStream? Stream { get; set; }

        /// <summary>优先级 (数值越大越优先)。</summary>
        public int Priority { get; set; }

        /// <summary>是否完成。</summary>
        public bool IsCompleted { get; set; }

        /// <summary>解码状态。</summary>
        public StreamState State { get; set; }
    }

    /// <summary>流式管理器。</summary>
    public sealed class AudioStreamingManager : IDisposable
    {
        /// <summary>最大并发流数。</summary>
        public int MaxConcurrentStreams { get; set; } = 8;

        /// <summary>预读取缓冲大小 (采样数)。</summary>
        public int PreReadBufferSize { get; set; } = 44100 * 2;

        /// <summary>缓冲策略。</summary>
        public StreamingBufferStrategy BufferStrategy { get; set; } = StreamingBufferStrategy.Ring;

        private readonly List<AudioStream> _activeStreams = new();
        private readonly Queue<StreamDecodeTask> _decodeQueue = new();
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>当前活跃流数。</summary>
        public int ActiveStreamCount
        {
            get
            {
                lock (_lock) return _activeStreams.Count;
            }
        }

        /// <summary>添加并打开流。</summary>
        public AudioStream? OpenStream(AudioStream stream)
        {
            lock (_lock)
            {
                if (_activeStreams.Count >= MaxConcurrentStreams)
                {
                    // 虚拟化优先级最低的流
                    _activeStreams.Sort((a, b) =>
                        (b is FileStreamAudio).CompareTo(a is FileStreamAudio));
                }

                if (stream.Open())
                {
                    _activeStreams.Add(stream);
                    return stream;
                }
                return null;
            }
        }

        /// <summary>关闭并移除流。</summary>
        public void CloseStream(AudioStream stream)
        {
            lock (_lock)
            {
                stream.Close();
                _activeStreams.Remove(stream);
            }
        }

        /// <summary>关闭所有流。</summary>
        public void CloseAll()
        {
            lock (_lock)
            {
                foreach (var stream in _activeStreams)
                    stream.Close();
                _activeStreams.Clear();
            }
        }

        /// <summary>从所有活跃流读取混合数据。</summary>
        public int MixReadAll(float[] outputBuffer, int channels, int sampleRate)
        {
            if (_disposed) return 0;

            int maxRead = 0;
            lock (_lock)
            {
                foreach (var stream in _activeStreams)
                {
                    if (stream.State != StreamState.Playing && stream.State != StreamState.Buffering)
                        continue;

                    int read = stream.ReadSamples(outputBuffer, 0, outputBuffer.Length);
                    maxRead = Math.Max(maxRead, read);
                }
            }
            return maxRead;
        }

        /// <summary>每帧更新。</summary>
        public void Update(float deltaTime)
        {
            if (_disposed) return;

            lock (_lock)
            {
                // 检查缓冲区状态
                foreach (var stream in _activeStreams)
                {
                    float fill = stream.GetBufferFillRatio();

                    // 溢出检测
                    if (fill > 0.95f && stream.State == StreamState.Playing)
                    {
                        // 缓冲区几乎满，可以暂停解码
                    }

                    // 下溢检测
                    if (fill < 0.1f && stream.State == StreamState.Playing)
                    {
                        stream.State = StreamState.Buffering;
                    }
                }
            }
        }

        /// <summary>处理解码队列 (在解码线程中调用)。</summary>
        public void ProcessDecodeQueue()
        {
            while (_decodeQueue.Count > 0)
            {
                var task = _decodeQueue.Dequeue();
                if (task.Stream != null && task.Stream.State == StreamState.Buffering)
                {
                    // 预读取数据填充缓冲区
                    float[] preBuffer = new float[PreReadBufferSize];
                    int read = task.Stream.ReadSamples(preBuffer, 0, preBuffer.Length);
                    if (read > 0)
                        task.State = StreamState.Playing;
                    task.IsCompleted = true;
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CloseAll();
                _disposed = true;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  SeamlessLoop — 音乐无缝循环
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>无缝循环播放器 (前后端点交叉淡入淡出)。</summary>
    public sealed class SeamlessLoop : IDisposable
    {
        /// <summary>淡入淡出时长 (秒)。</summary>
        public float CrossfadeDuration { get; set; } = 0.5f;

        /// <summary>淡入淡出采样数。</summary>
        private int _crossfadeSamples;

        /// <summary>源流。</summary>
        private AudioStream? _stream;

        /// <summary>循环起始位置 (秒)。</summary>
        public float LoopStartSeconds { get; set; }

        /// <summary>循环结束位置 (秒)。</summary>
        public float LoopEndSeconds { get; set; }

        /// <summary>淡出缓冲区 (从循环末尾提前读取)。</summary>
        private float[]? _fadeOutBuffer;

        /// <summary>淡入缓冲区 (从循环起始读取)。</summary>
        private float[]? _fadeInBuffer;

        /// <summary>当前是否在交叉淡入淡出中。</summary>
        public bool IsCrossfading { get; private set; }

        /// <summary>当前淡入淡出进度 [0,1]。</summary>
        public float CrossfadeProgress { get; private set; }

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>采样率。</summary>
        private int _sampleRate = 44100;
        private int _channels = 2;

        /// <summary>初始化无缝循环。</summary>
        public void Initialize(AudioStream stream, float loopStart, float loopEnd)
        {
            _stream = stream;
            LoopStartSeconds = loopStart;
            LoopEndSeconds = loopEnd;
            _sampleRate = stream.SampleRate;
            _channels = stream.Channels;
            _crossfadeSamples = (int)(CrossfadeDuration * _sampleRate * _channels);

            // 预读取淡出部分 (循环末尾)
            float preReadStart = LoopEndSeconds - CrossfadeDuration;
            stream.Seek(preReadStart);
            _fadeOutBuffer = new float[_crossfadeSamples];
            stream.ReadSamples(_fadeOutBuffer, 0, _crossfadeSamples);

            // 预读取淡入部分 (循环起始)
            stream.Seek(LoopStartSeconds);
            _fadeInBuffer = new float[_crossfadeSamples];
            stream.ReadSamples(_fadeInBuffer, 0, _crossfadeSamples);

            // 定位到循环起始
            stream.Seek(LoopStartSeconds);
            IsPlaying = true;
        }

        /// <summary>读取无缝循环数据。</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            if (_stream == null || !IsPlaying) return 0;

            int read = _stream.ReadSamples(buffer, offset, count);
            float currentPos = _stream.CurrentPosition;

            // 检查是否接近循环末尾
            float crossfadeStart = LoopEndSeconds - CrossfadeDuration;

            if (currentPos >= crossfadeStart && _fadeOutBuffer != null && _fadeInBuffer != null)
            {
                IsCrossfading = true;
                CrossfadeProgress = (currentPos - crossfadeStart) / CrossfadeDuration;

                // 应用淡出 (衰减输出) + 淡入 (叠加循环起始)
                int fadeOutOffset = (int)((currentPos - crossfadeStart) * _sampleRate * _channels);
                for (int i = 0; i < read; i++)
                {
                    int fadeIdx = fadeOutOffset + i;
                    float fadeOutGain = 1f;
                    float fadeInGain = 0f;

                    if (fadeIdx < _fadeOutBuffer.Length)
                    {
                        fadeOutGain = 1f - CrossfadeProgress;
                        fadeInGain = CrossfadeProgress;
                        if (fadeIdx < _fadeInBuffer.Length)
                            buffer[offset + i] = buffer[offset + i] * fadeOutGain + _fadeInBuffer[fadeIdx] * fadeInGain;
                    }
                }

                if (currentPos >= LoopEndSeconds)
                {
                    _stream.Seek(LoopStartSeconds);
                    IsCrossfading = false;
                    CrossfadeProgress = 0f;
                }
            }

            return read;
        }

        /// <summary>停止。</summary>
        public void Stop()
        {
            IsPlaying = false;
            IsCrossfading = false;
            CrossfadeProgress = 0f;
            _stream?.Seek(LoopStartSeconds);
        }

        public void Dispose()
        {
            Stop();
            _stream = null;
            _fadeOutBuffer = null;
            _fadeInBuffer = null;
        }
    }
}
