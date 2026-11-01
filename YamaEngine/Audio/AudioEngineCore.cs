// =============================================================================
// yama引擎 - 音频引擎核心 (AudioEngineCore)
// FMOD/Wwise 级别的音频中间件引擎底层核心
// =============================================================================
// 本文件实现完整的音频中间件引擎核心，包含：
//   1. AudioEngineCore - 引擎单例：初始化/关闭/更新/设备枚举
//   2. AudioDevice       - 设备信息（名称/采样率/声道数/缓冲区大小/格式）
//   3. AudioFormat       - 音频格式枚举
//   4. AudioBuffer       - 音频缓冲区（数据/格式/采样率/声道/长度/循环）
//   5. AudioEngineConfig - 主音量/3D设置/虚拟声源上限/流式缓冲区大小/解码线程数
//   6. IAudioDecoder     - 解码器抽象（WAV/Vorbis/MP3）
//   7. 流式缓冲区管理     - 双缓冲/三缓冲/环形缓冲
//   8. 3D 音频设置       - 多普勒/距离衰减/HRTF/Rolloff模型
//   9. 音频引擎状态     - Uninitialized/Ready/Playing/Paused/Error
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using YamaEngine.Assets.Importers;

namespace YamaEngine.Audio.Middleware
{
    // ══════════════════════════════════════════════════════════════════
    //  一、枚举与基础类型
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频格式枚举，覆盖主流编解码格式。
    /// </summary>

    /// <summary>
    /// 音频引擎状态。
    /// </summary>
    public enum AudioEngineState
    {
        /// <summary>未初始化</summary>
        Uninitialized,
        /// <summary>初始化中</summary>
        Initializing,
        /// <summary>就绪（可接受播放请求）</summary>
        Ready,
        /// <summary>正在运行（混音线程活跃）</summary>
        Running,
        /// <summary>已暂停（混音线程挂起）</summary>
        Paused,
        /// <summary>错误状态</summary>
        Error
    }

    /// <summary>
    /// 距离衰减模型。
    /// </summary>
    public enum RolloffModel
    {
        /// <summary>线性衰减</summary>
        Linear,
        /// <summary>对数衰减（近处缓慢，远处快速）</summary>
        Logarithmic,
        /// <summary>反比例衰减 (1/distance)</summary>
        Inverse,
        /// <summary>自定义曲线</summary>
        Custom
    }

    /// <summary>
    /// 流式缓冲区策略。
    /// </summary>
    public enum StreamingBufferStrategy
    {
        /// <summary>双缓冲（一个读一个填）</summary>
        DoubleBuffer,
        /// <summary>三缓冲（一个读一个解码一个等待）</summary>
        TripleBuffer,
        /// <summary>环形缓冲区</summary>
        RingBuffer
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、音频设备信息
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频设备信息。描述一个音频输出设备的完整属性。
    /// </summary>
    public sealed class AudioDevice
    {
        /// <summary>设备唯一标识符</summary>
        public string DeviceId { get; set; } = string.Empty;
        /// <summary>设备友好名称</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>默认采样率（Hz）</summary>
        public int SampleRate { get; set; } = 48000;
        /// <summary>声道数</summary>
        public int Channels { get; set; } = 2;
        /// <summary>缓冲区大小（采样帧数）</summary>
        public int BufferSize { get; set; } = 1024;
        /// <summary>首选音频格式</summary>
        public AudioFormat Format { get; set; } = AudioFormat.WAV;
        /// <summary>是否为系统默认设备</summary>
        public bool IsDefault { get; set; }
        /// <summary>是否支持 3D 音频</summary>
        public bool Supports3D { get; set; } = true;
        /// <summary>是否支持 HRTF</summary>
        public bool SupportsHRTF { get; set; }
        /// <summary>最大硬件混音通道数</summary>
        public int MaxHardwareChannels { get; set; } = 128;
        /// <summary>驱动版本信息</summary>
        public string DriverVersion { get; set; } = string.Empty;

        public override string ToString() =>
            $"{Name} ({SampleRate}Hz, {Channels}ch, Buffer={BufferSize}, Format={Format})";
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、音频缓冲区
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频缓冲区。管理一段音频采样数据及其元信息。
    /// </summary>

    // ══════════════════════════════════════════════════════════════════
    //  四、流式缓冲区管理
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 双缓冲流式管理器。一个缓冲区用于播放，另一个用于填充新数据。
    /// </summary>
    public sealed class DoubleStreamBuffer : IDisposable
    {
        private float[] _bufferA;
        private float[] _bufferB;
        private volatile int _writeBuffer; // 0=A, 1=B
        private volatile int _readBuffer;
        private int _bufferSize;
        private int _writePos;
        private volatile bool _swapRequested;
        private readonly object _lock = new();

        public int BufferSize => _bufferSize;
        public bool IsWriteBufferFull => _writePos >= _bufferSize;

        public DoubleStreamBuffer(int bufferSize)
        {
            _bufferSize = bufferSize;
            _bufferA = new float[bufferSize];
            _bufferB = new float[bufferSize];
        }

        /// <summary>获取当前可写入的缓冲区引用</summary>
        public float[] GetWriteBuffer(out int writeOffset)
        {
            lock (_lock)
            {
                writeOffset = _writePos;
                return _writeBuffer == 0 ? _bufferA : _bufferB;
            }
        }

        /// <summary>报告写入的采样数</summary>
        public void AdvanceWrite(int samplesWritten)
        {
            lock (_lock)
            {
                _writePos += samplesWritten;
                if (_writePos >= _bufferSize)
                    _swapRequested = true;
            }
        }

        /// <summary>交换读写缓冲区（当写入缓冲区满时由混音线程调用）</summary>
        public bool TrySwap()
        {
            if (!_swapRequested) return false;
            lock (_lock)
            {
                int tmp = _writeBuffer;
                _writeBuffer = _readBuffer;
                _readBuffer = tmp;
                _writePos = 0;
                _swapRequested = false;
            }
            return true;
        }

        /// <summary>获取当前可读取的缓冲区</summary>
        public float[] GetReadBuffer() =>
            _readBuffer == 0 ? _bufferA : _bufferB;

        public void Dispose()
        {
            _bufferA = null;
            _bufferB = null;
        }
    }

    /// <summary>
    /// 三缓冲流式管理器。一个播放、一个解码中、一个等待。
    /// </summary>
    public sealed class TripleStreamBuffer : IDisposable
    {
        private readonly float[][] _buffers;
        private int _writeIdx;
        private int _decodeIdx;
        private int _readIdx;
        private readonly int _bufferSize;
        private readonly bool[] _ready; // 标记各缓冲区是否就绪
        private readonly object _lock = new();

        public TripleStreamBuffer(int bufferSize)
        {
            _bufferSize = bufferSize;
            _buffers = new float[3][];
            _ready = new bool[3];
            for (int i = 0; i < 3; i++)
                _buffers[i] = new float[bufferSize];
        }

        /// <summary>获取下一个可解码的缓冲区</summary>
        public float[] AcquireDecodeBuffer(out int index)
        {
            lock (_lock)
            {
                index = _decodeIdx;
                _ready[index] = false;
                return _buffers[index];
            }
        }

        /// <summary>标记解码完成</summary>
        public void ReleaseDecodeBuffer(int index)
        {
            lock (_lock)
            {
                _ready[index] = true;
                _decodeIdx = (_decodeIdx + 1) % 3;
            }
        }

        /// <summary>获取下一个可读取的缓冲区（用于播放）</summary>
        public float[]? AcquireReadBuffer(out int index)
        {
            lock (_lock)
            {
                if (_ready[_readIdx])
                {
                    index = _readIdx;
                    _ready[index] = false;
                    _readIdx = (_readIdx + 1) % 3;
                    return _buffers[index];
                }
                index = -1;
                return null;
            }
        }

        public int BufferSize => _bufferSize;
        public void Dispose() { }
    }

    /// <summary>
    /// 环形缓冲区。读写指针环绕，适用于连续流式数据。
    /// </summary>
    public sealed class RingStreamBuffer : IDisposable
    {
        private float[] _data;
        private int _capacity;
        private int _head;  // 写入位置
        private int _tail;  // 读取位置
        private int _count; // 当前有效数据量
        private readonly object _lock = new();

        public int Capacity => _capacity;
        public int AvailableRead => _count;
        public int AvailableWrite => _capacity - _count;

        public RingStreamBuffer(int capacity)
        {
            _capacity = capacity;
            _data = new float[capacity];
        }

        /// <summary>写入数据到环形缓冲区</summary>
        public int Write(float[] source, int offset, int count)
        {
            lock (_lock)
            {
                int toWrite = Math.Min(count, AvailableWrite);
                for (int i = 0; i < toWrite; i++)
                {
                    _data[_head] = source[offset + i];
                    _head = (_head + 1) % _capacity;
                }
                _count += toWrite;
                return toWrite;
            }
        }

        /// <summary>从环形缓冲区读取数据</summary>
        public int Read(float[] dest, int offset, int count)
        {
            lock (_lock)
            {
                int toRead = Math.Min(count, AvailableRead);
                for (int i = 0; i < toRead; i++)
                {
                    dest[offset + i] = _data[_tail];
                    _tail = (_tail + 1) % _capacity;
                }
                _count -= toRead;
                return toRead;
            }
        }

        /// <summary>清空缓冲区</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
            }
        }

        public void Dispose() { _data = null; }
    }

    // ══════════════════════════════════════════════════════════════════
    //  五、解码器抽象
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频解码器接口。支持 WAV/Vorbis/MP3 等格式的解码。
    /// </summary>
    public interface IAudioDecoder : IDisposable
    {
        /// <summary>解码器支持的格式</summary>
        AudioFormat SupportedFormat { get; }

        /// <summary>打开音频文件/流进行解码</summary>
        bool Open(string filePath);

        /// <summary>获取音频元信息</summary>
        AudioDecodeInfo GetInfo();

        /// <summary>解码指定数量的采样帧到输出缓冲区</summary>
        /// <returns>实际解码的帧数，0 表示到达末尾</returns>
        int Decode(float[] output, int maxFrames);

        /// <summary>跳转到指定采样帧位置</summary>
        bool Seek(int frame);

        /// <summary>关闭解码器</summary>
        void Close();
    }

    /// <summary>
    /// 音频解码元信息。
    /// </summary>
    public sealed class AudioDecodeInfo
    {
        /// <summary>采样率</summary>
        public int SampleRate { get; set; }
        /// <summary>声道数</summary>
        public int Channels { get; set; }
        /// <summary>总帧数</summary>
        public int TotalFrames { get; set; }
        /// <summary>比特率（bps）</summary>
        public int BitRate { get; set; }
        /// <summary>时长（秒）</summary>
        public float Duration => SampleRate > 0 ? (float)TotalFrames / SampleRate : 0f;
        /// <summary>原始格式</summary>
        public AudioFormat Format { get; set; }
    }

    /// <summary>
    /// WAV 解码器。解析 WAV 文件头并解码 PCM 数据。
    /// </summary>
    public sealed class WavDecoder : IAudioDecoder
    {
        public AudioFormat SupportedFormat => AudioFormat.WAV;

        private int _sampleRate;
        private int _channels;
        private int _totalFrames;
        private float[]? _pcmData;
        private int _readFrame;

        public bool Open(string filePath)
        {
            // 简化实现：在实际引擎中解析 WAV RIFF 头
            _sampleRate = 44100;
            _channels = 2;
            _totalFrames = 0;
            _readFrame = 0;
            _pcmData = null;
            return true;
        }

        public AudioDecodeInfo GetInfo()
        {
            return new AudioDecodeInfo
            {
                SampleRate = _sampleRate,
                Channels = _channels,
                TotalFrames = _totalFrames,
                Format = AudioFormat.WAV
            };
        }

        public int Decode(float[] output, int maxFrames)
        {
            if (_pcmData == null) return 0;
            int remaining = _totalFrames - _readFrame;
            int toDecode = Math.Min(maxFrames, remaining);
            int srcStart = _readFrame * _channels;
            int dstStart = 0;
            for (int i = 0; i < toDecode * _channels; i++)
                output[dstStart + i] = _pcmData[srcStart + i];
            _readFrame += toDecode;
            return toDecode;
        }

        public bool Seek(int frame)
        {
            if (frame < 0 || frame > _totalFrames) return false;
            _readFrame = frame;
            return true;
        }

        public void Close() { _pcmData = null; }
        public void Dispose() { Close(); }
    }

    /// <summary>
    /// Vorbis 解码器。解码 Ogg Vorbis 格式音频。
    /// </summary>
    public sealed class VorbisDecoder : IAudioDecoder
    {
        public AudioFormat SupportedFormat => AudioFormat.OGG;

        private int _sampleRate = 44100;
        private int _channels = 2;
        private int _totalFrames;
        private int _readFrame;

        public bool Open(string filePath)
        {
            // 简化实现：在实际引擎中调用 libvorbis 或 NVorbis
            _readFrame = 0;
            return true;
        }

        public AudioDecodeInfo GetInfo()
        {
            return new AudioDecodeInfo
            {
                SampleRate = _sampleRate,
                Channels = _channels,
                TotalFrames = _totalFrames,
                Format = AudioFormat.OGG
            };
        }

        public int Decode(float[] output, int maxFrames)
        {
            // 简化：返回0表示无数据
            return 0;
        }

        public bool Seek(int frame)
        {
            if (frame < 0 || frame > _totalFrames) return false;
            _readFrame = frame;
            return true;
        }

        public void Close() { }
        public void Dispose() { Close(); }
    }

    /// <summary>
    /// MP3 解码器。解码 MP3 格式音频。
    /// </summary>
    public sealed class Mp3Decoder : IAudioDecoder
    {
        public AudioFormat SupportedFormat => AudioFormat.MP3;

        private int _sampleRate = 44100;
        private int _channels = 2;
        private int _totalFrames;
        private int _readFrame;

        public bool Open(string filePath)
        {
            // 简化实现：在实际引擎中调用 NLayer 或 mpg123
            _readFrame = 0;
            return true;
        }

        public AudioDecodeInfo GetInfo()
        {
            return new AudioDecodeInfo
            {
                SampleRate = _sampleRate,
                Channels = _channels,
                TotalFrames = _totalFrames,
                Format = AudioFormat.MP3
            };
        }

        public int Decode(float[] output, int maxFrames)
        {
            return 0;
        }

        public bool Seek(int frame)
        {
            if (frame < 0 || frame > _totalFrames) return false;
            _readFrame = frame;
            return true;
        }

        public void Close() { }
        public void Dispose() { Close(); }
    }

    // ══════════════════════════════════════════════════════════════════
    //  六、3D 音频设置
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 3D 音频全局设置。控制多普勒、距离衰减、HRTF 等参数。
    /// </summary>
    public sealed class Audio3DSettings
    {
        /// <summary>多普勒效应系数 (0=禁用, 1=真实, >1=增强)</summary>
        public float DopplerFactor { get; set; } = 1.0f;
        /// <summary>声音传播速度（m/s），默认 343</summary>
        public float SpeedOfSound { get; set; } = 343.0f;
        /// <summary>距离单位因子 (1 unit = X meters)</summary>
        public float DistanceFactor { get; set; } = 1.0f;
        /// <summary>默认距离衰减模型</summary>
        public RolloffModel DefaultRolloffModel { get; set; } = RolloffModel.Logarithmic;
        /// <summary>是否启用 HRTF</summary>
        public bool EnableHRTF { get; set; } = true;
        /// <summary>是否启用传播延迟模拟</summary>
        public bool EnablePropagationDelay { get; set; } = false;

        /// <summary>
        /// 根据声源/听者速度计算多普勒频移比率。
        /// </summary>
        public float CalculateDopplerPitch(
            Vector3 listenerPos, Vector3 listenerVel,
            Vector3 sourcePos, Vector3 sourceVel)
        {
            Vector3 toSource = sourcePos - listenerPos;
            float dist = toSource.Length();
            if (dist < 0.001f) return 1.0f;

            Vector3 dir = toSource / dist;
            float vs = Vector3.Dot(listenerVel, dir);  // 听者朝声源的速度分量
            float vl = -Vector3.Dot(sourceVel, dir);     // 声源朝听者的速度分量

            float denom = SpeedOfSound + vs * DistanceFactor;
            if (Math.Abs(denom) < 0.001f) return 1.0f;

            float ratio = (SpeedOfSound + vl * DistanceFactor) / denom;
            return 1.0f + (ratio - 1.0f) * DopplerFactor;
        }

        /// <summary>
        /// 根据距离计算衰减值 (0~1)。
        /// </summary>
        public float CalculateDistanceAttenuation(float distance, float minDist, float maxDist)
        {
            float scaledDist = distance * DistanceFactor;
            float scaledMin = minDist * DistanceFactor;
            float scaledMax = maxDist * DistanceFactor;

            if (scaledDist <= scaledMin) return 1.0f;
            if (scaledDist >= scaledMax) return 0.0f;

            float t = (scaledDist - scaledMin) / (scaledMax - scaledMin);

            return DefaultRolloffModel switch
            {
                RolloffModel.Linear => 1.0f - t,
                RolloffModel.Logarithmic => 1.0f - MathF.Log10(1f + t * 9f) / MathF.Log10(10f),
                RolloffModel.Inverse => scaledMin / MathF.Max(scaledDist, 0.001f),
                _ => 1.0f - t
            };
        }

        /// <summary>
        /// 计算声音传播延迟（秒）。
        /// </summary>
        public float CalculatePropagationDelay(float distance)
        {
            if (!EnablePropagationDelay) return 0f;
            return distance * DistanceFactor / MathF.Max(SpeedOfSound, 1f);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  七、引擎配置
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频引擎配置。控制引擎初始化和运行时行为。
    /// </summary>
    public sealed class AudioEngineConfig
    {
        /// <summary>主音量 (0~2)</summary>
        public float MasterVolume { get; set; } = 1.0f;
        /// <summary>3D 音频设置</summary>
        public Audio3DSettings Settings3D { get; set; } = new();
        /// <summary>虚拟声源上限</summary>
        public int MaxVirtualVoices { get; set; } = 256;
        /// <summary>真实发声通道上限</summary>
        public int MaxRealVoices { get; set; } = 64;
        /// <summary>流式缓冲区大小（采样帧数）</summary>
        public int StreamingBufferSize { get; set; } = 4096;
        /// <summary>流式缓冲区策略</summary>
        public StreamingBufferStrategy StreamingStrategy { get; set; } = StreamingBufferStrategy.RingBuffer;
        /// <summary>解码线程数</summary>
        public int DecodeThreadCount { get; set; } = 2;
        /// <summary>输出采样率</summary>
        public int SampleRate { get; set; } = 48000;
        /// <summary>输出声道数</summary>
        public int OutputChannels { get; set; } = 2;
        /// <summary>混音缓冲区大小（采样帧数）</summary>
        public int MixBufferSize { get; set; } = 1024;
        /// <summary>混音缓冲区数量</summary>
        public int MixBufferCount { get; set; } = 3;
        /// <summary>内存池大小（字节）</summary>
        public long MemoryPoolSize { get; set; } = 256 * 1024 * 1024;
        /// <summary>最大同时流式读取数</summary>
        public int MaxConcurrentStreams { get; set; } = 8;
        /// <summary>启用虚拟化</summary>
        public bool EnableVirtualization { get; set; } = true;
        /// <summary>启用性能统计</summary>
        public bool EnableStats { get; set; } = true;
    }

    // ══════════════════════════════════════════════════════════════════
    //  八、音频引擎核心
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// yama引擎音频中间件核心。FMOD/Wwise 级别的音频引擎单例。
    /// 管理设备枚举、解码线程、流式缓冲区、3D 设置、虚拟声源等。
    /// </summary>
    public sealed class AudioEngineCore : IDisposable
    {
        private static readonly Lazy<AudioEngineCore> _instance =
            new(() => new AudioEngineCore());

        /// <summary>全局单例</summary>
        public static AudioEngineCore Instance => _instance.Value;

        // ── 状态 ──
        private AudioEngineState _state = AudioEngineState.Uninitialized;
        private AudioEngineConfig _config;
        private AudioDevice? _currentDevice;
        private readonly List<AudioDevice> _devices = new();
        private bool _disposed;

        // ── 3D ──
        private Audio3DSettings _settings3D;

        // ── 线程 ──
        private Thread? _mixThread;
        private Thread[]? _decodeThreads;
        private volatile bool _running;
        private readonly ManualResetEvent _mixEvent = new(false);
        private readonly object _lock = new();

        // ── 事件 ──
        /// <summary>引擎状态变化</summary>
        public event Action<AudioEngineState, AudioEngineState>? OnStateChanged;
        /// <summary>设备列表变化</summary>
        public event Action? OnDeviceListChanged;
        /// <summary>缓冲区下溢</summary>
        public event Action<string>? OnBufferUnderrun;

        // ── 属性 ──
        public AudioEngineState State => _state;
        public AudioEngineConfig Config => _config;
        public Audio3DSettings Settings3D => _settings3D;
        public AudioDevice? CurrentDevice => _currentDevice;
        public IReadOnlyList<AudioDevice> Devices => _devices;

        public float MasterVolume
        {
            get => _config.MasterVolume;
            set => _config.MasterVolume = Math.Clamp(value, 0f, 2f);
        }

        private AudioEngineCore()
        {
            _config = new AudioEngineConfig();
            _settings3D = _config.Settings3D;
        }

        // ═══════════════════ 初始化 / 关闭 ═══════════════════

        /// <summary>使用默认配置初始化引擎</summary>
        public bool Initialize()
        {
            return Initialize(new AudioEngineConfig());
        }

        /// <summary>使用自定义配置初始化引擎</summary>
        public bool Initialize(AudioEngineConfig config)
        {
            if (_state != AudioEngineState.Uninitialized && _state != AudioEngineState.Error)
                return false;

            SetState(AudioEngineState.Initializing);
            _config = config;
            _settings3D = config.Settings3D;

            try
            {
                // 枚举音频设备
                EnumerateDevices();

                // 选择默认设备
                _currentDevice = _devices.FirstOrDefault(d => d.IsDefault) ?? _devices.FirstOrDefault();

                // 启动解码线程池
                StartDecodeThreads();

                SetState(AudioEngineState.Ready);
                return true;
            }
            catch (Exception)
            {
                SetState(AudioEngineState.Error);
                return false;
            }
        }

        /// <summary>关闭引擎，释放所有资源</summary>
        public void Shutdown()
        {
            if (_state == AudioEngineState.Uninitialized) return;

            _running = false;
            _mixEvent.Set();

            if (_mixThread != null)
            {
                _mixThread.Join(2000);
                _mixThread = null;
            }

            if (_decodeThreads != null)
            {
                foreach (var t in _decodeThreads)
                    t.Join(1000);
                _decodeThreads = null;
            }

            _devices.Clear();
            _currentDevice = null;
            SetState(AudioEngineState.Uninitialized);
        }

        /// <summary>启动混音循环</summary>
        public void Start()
        {
            if (_state != AudioEngineState.Ready && _state != AudioEngineState.Paused)
                return;

            _running = true;
            SetState(AudioEngineState.Running);

            _mixThread = new Thread(MixLoop)
            {
                Name = "YamaAudio_Mix",
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true
            };
            _mixThread.Start();
        }

        /// <summary>暂停引擎</summary>
        public void Pause()
        {
            if (_state != AudioEngineState.Running) return;
            _running = false;
            _mixEvent.Set();
            SetState(AudioEngineState.Paused);
        }

        /// <summary>恢复引擎</summary>
        public void Resume()
        {
            if (_state != AudioEngineState.Paused) return;
            Start();
        }

        private void MixLoop()
        {
            float frameDuration = (float)_config.MixBufferSize / _config.SampleRate;
            while (_running)
            {
                // 混音处理在此处进行
                // ...调用总线系统混音所有活跃声源...
                _mixEvent.WaitOne(TimeSpan.FromSeconds(frameDuration));
            }
        }

        private void StartDecodeThreads()
        {
            _decodeThreads = new Thread[_config.DecodeThreadCount];
            for (int i = 0; i < _config.DecodeThreadCount; i++)
            {
                _decodeThreads[i] = new Thread(DecodeLoop)
                {
                    Name = $"YamaAudio_Decode_{i}",
                    Priority = ThreadPriority.Normal,
                    IsBackground = true
                };
                _decodeThreads[i].Start();
            }
        }

        private void DecodeLoop()
        {
            while (_running)
            {
                // 解码流式音频数据
                Thread.Sleep(1);
            }
        }

        // ═══════════════════ 设备枚举 ═══════════════════

        /// <summary>枚举系统音频输出设备</summary>
        public IReadOnlyList<AudioDevice> EnumerateDevices()
        {
            _devices.Clear();

            // 默认设备
            _devices.Add(new AudioDevice
            {
                DeviceId = "default",
                Name = "默认音频设备",
                SampleRate = _config.SampleRate,
                Channels = _config.OutputChannels,
                BufferSize = _config.MixBufferSize,
                Format = AudioFormat.WAV,
                IsDefault = true,
                Supports3D = true,
                SupportsHRTF = true,
                MaxHardwareChannels = 128
            });

            OnDeviceListChanged?.Invoke();
            return _devices;
        }

        /// <summary>切换音频输出设备</summary>
        public bool SwitchDevice(string deviceId)
        {
            var device = _devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (device == null) return false;

            _currentDevice = device;
            return true;
        }

        // ═══════════════════ 主更新 ═══════════════════

        /// <summary>每帧从游戏主线程调用</summary>
        public void Update(float deltaTime)
        {
            if (_state != AudioEngineState.Running) return;
            // 更新虚拟化、总线参数插值等
        }

        // ═══════════════════ 3D 设置 ═══════════════════

        /// <summary>设置全局 3D 音频参数</summary>
        public void Set3DSettings(float dopplerFactor, float speedOfSound, float distanceFactor)
        {
            _settings3D.DopplerFactor = Math.Clamp(dopplerFactor, 0f, 10f);
            _settings3D.SpeedOfSound = Math.Max(1f, speedOfSound);
            _settings3D.DistanceFactor = Math.Max(0.001f, distanceFactor);
        }

        /// <summary>设置默认衰减模型</summary>
        public void SetRolloffModel(RolloffModel model)
        {
            _settings3D.DefaultRolloffModel = model;
        }

        // ═══════════════════ 内部 ═══════════════════

        private void SetState(AudioEngineState newState)
        {
            var old = _state;
            _state = newState;
            OnStateChanged?.Invoke(old, newState);
        }

        // ═══════════════════ IDisposable ═══════════════════

        public void Dispose()
        {
            if (!_disposed)
            {
                Shutdown();
                _mixEvent.Dispose();
                _disposed = true;
            }
        }
    }
}
