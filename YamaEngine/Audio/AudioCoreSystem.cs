using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using YamaEngine.Assets.Importers;

namespace YamaEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 音频核心系统 (Audio Core System)
    //
    // 音频引擎底层架构：设备管理、音频格式、缓冲池、Voice播放实例、
    // 后端抽象接口 (IAudioBackend: OpenAL / XAudio2 / CoreAudio)。
    // 类似 FMOD Core / Wwise Low-Level IO 架构。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>音频格式枚举。</summary>

    /// <summary>音频引擎状态。</summary>
    public enum AudioEngineState
    {
        Uninitialized,
        Initializing,
        Running,
        Paused,
        ShuttingDown,
        Shutdown
    }

    /// <summary>音频后端类型。</summary>

    /// <summary>音频播放状态。</summary>
    public enum AudioVoiceState
    {
        Stopped,
        Playing,
        Paused,
        Virtualized
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioDevice — 音频设备信息
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频输出设备描述。</summary>
    public sealed class AudioDevice
    {
        /// <summary>设备唯一标识符。</summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>设备友好名称。</summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>采样率 (Hz)。</summary>
        public int SampleRate { get; set; } = 44100;

        /// <summary>位深度。</summary>
        public int BitDepth { get; set; } = 16;

        /// <summary>声道数。</summary>
        public int Channels { get; set; } = 2;

        /// <summary>硬件缓冲区大小 (采样数)。</summary>
        public int BufferSize { get; set; } = 1024;

        /// <summary>原生音频格式。</summary>
        public AudioFormat Format { get; set; } = AudioFormat.WAV;

        /// <summary>是否为默认设备。</summary>
        public bool IsDefault { get; set; }

        public override string ToString() => $"{DeviceName} ({SampleRate}Hz, {BitDepth}bit, {Channels}ch)";
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBuffer — PCM 数据缓冲
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频数据缓冲，持有 PCM 采样数据。</summary>

    // ──────────────────────────────────────────────────────────────────────
    //  AudioVoice — 播放实例
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频播放实例 (Voice)，一个 Voice 绑定一个 AudioBuffer 进行播放。</summary>
    public sealed class AudioVoice : IDisposable
    {
        private AudioBuffer? _buffer;
        private bool _disposed;

        /// <summary>Voice 唯一 ID。</summary>
        public int VoiceId { get; }

        /// <summary>绑定的音频缓冲。</summary>
        public AudioBuffer? Buffer
        {
            get => _buffer;
            set => _buffer = value;
        }

        /// <summary>当前播放状态。</summary>
        public AudioVoiceState State { get; private set; } = AudioVoiceState.Stopped;

        /// <summary>音量 [0, inf)，1.0 为原始音量。</summary>
        public float Volume { get; set; } = 1.0f;

        /// <summary>音调 (Pitch) 系数，1.0 为原始音调。</summary>
        public float Pitch { get; set; } = 1.0f;

        /// <summary>是否循环播放。</summary>
        public bool Looping { get; set; }

        /// <summary>当前播放位置 (采样帧)。</summary>
        public int CurrentFrame { get; set; }

        /// <summary>总帧数 (来自 Buffer)。</summary>
        public int TotalFrames => _buffer?.FrameCount ?? 0;

        /// <summary>播放进度 [0,1]。</summary>
        public float Progress => TotalFrames > 0 ? (float)CurrentFrame / TotalFrames : 0f;

        /// <summary>3D 空间位置 (Vector3)。</summary>
        public Vector3 Position { get; set; }

        /// <summary>3D 速度。</summary>
        public Vector3 Velocity { get; set; }

        /// <summary>是否启用 3D 空间化。</summary>
        public bool Is3D { get; set; }

        /// <summary>优先级 (数值越大越重要)。</summary>
        public int Priority { get; set; } = 128;

        /// <summary>所属总线 ID (-1 表示直接输出到主总线)。</summary>
        public int BusId { get; set; } = -1;

        /// <summary>播放开始回调。</summary>
        public event Action<AudioVoice>? OnPlay;

        /// <summary>播放结束回调。</summary>
        public event Action<AudioVoice>? OnStop;

        /// <summary>循环点回调 (每次循环回到起点)。</summary>
        public event Action<AudioVoice>? OnLoop;

        private static int _nextId;

        public AudioVoice()
        {
            VoiceId = Interlocked.Increment(ref _nextId);
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            if (_buffer == null || _disposed) return;
            State = AudioVoiceState.Playing;
            CurrentFrame = 0;
            OnPlay?.Invoke(this);
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
            if (State == AudioVoiceState.Playing)
                State = AudioVoiceState.Paused;
        }

        /// <summary>恢复播放。</summary>
        public void Resume()
        {
            if (State == AudioVoiceState.Paused)
                State = AudioVoiceState.Playing;
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            var prevState = State;
            State = AudioVoiceState.Stopped;
            CurrentFrame = 0;
            if (prevState == AudioVoiceState.Playing || prevState == AudioVoiceState.Paused)
                OnStop?.Invoke(this);
        }

        /// <summary>设置虚拟化状态 (超出并发/距离时静音)。</summary>
        public void SetVirtualized(bool virtualized)
        {
            if (virtualized && State == AudioVoiceState.Playing)
                State = AudioVoiceState.Virtualized;
            else if (!virtualized && State == AudioVoiceState.Virtualized)
                State = AudioVoiceState.Playing;
        }

        /// <summary>推进播放帧。</summary>
        public void AdvanceFrames(int frames)
        {
            if (State != AudioVoiceState.Playing) return;
            CurrentFrame += (int)(frames * Pitch);
            if (CurrentFrame >= TotalFrames)
            {
                if (Looping)
                {
                    CurrentFrame %= TotalFrames;
                    OnLoop?.Invoke(this);
                }
                else
                {
                    CurrentFrame = TotalFrames;
                    State = AudioVoiceState.Stopped;
                    OnStop?.Invoke(this);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _buffer = null;
                _disposed = true;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  IAudioBackend — 后端抽象接口
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频后端抽象接口，屏蔽 OpenAL/XAudio2/CoreAudio 实现差异。</summary>
    public interface IAudioBackend : IDisposable
    {
        /// <summary>后端类型。</summary>
        AudioBackendType BackendType { get; }

        /// <summary>初始化后端。</summary>
        bool Initialize(AudioDevice device);

        /// <summary>关闭后端。</summary>
        void Shutdown();

        /// <summary>枚举可用输出设备。</summary>
        List<AudioDevice> EnumerateDevices();

        /// <summary>切换输出设备。</summary>
        bool SwitchDevice(AudioDevice device);

        /// <summary>提交音频数据到输出。</summary>
        bool SubmitBuffer(float[] samples, int channels, int sampleRate);

        /// <summary>获取当前输出设备。</summary>
        AudioDevice? CurrentDevice { get; }

        /// <summary>获取/设置主音量。</summary>
        float MasterVolume { get; set; }

        /// <summary>后端是否就绪。</summary>
        bool IsReady { get; }
    }

    /// <summary>OpenAL 后端实现。</summary>
    public sealed class OpenALBackend : IAudioBackend
    {
        public AudioBackendType BackendType => AudioBackendType.OpenAL;
        public AudioDevice? CurrentDevice { get; private set; }
        public float MasterVolume { get; set; } = 1.0f;
        public bool IsReady { get; private set; }

        public bool Initialize(AudioDevice device) { CurrentDevice = device; IsReady = true; return true; }
        public void Shutdown() { IsReady = false; CurrentDevice = null; }
        public List<AudioDevice> EnumerateDevices() => new List<AudioDevice>();
        public bool SwitchDevice(AudioDevice device) { Shutdown(); return Initialize(device); }
        public bool SubmitBuffer(float[] samples, int channels, int sampleRate) { return IsReady; }
        public void Dispose() { Shutdown(); }
    }

    /// <summary>XAudio2 后端实现。</summary>
    public sealed class XAudio2Backend : IAudioBackend
    {
        public AudioBackendType BackendType => AudioBackendType.XAudio2;
        public AudioDevice? CurrentDevice { get; private set; }
        public float MasterVolume { get; set; } = 1.0f;
        public bool IsReady { get; private set; }

        public bool Initialize(AudioDevice device) { CurrentDevice = device; IsReady = true; return true; }
        public void Shutdown() { IsReady = false; CurrentDevice = null; }
        public List<AudioDevice> EnumerateDevices() => new List<AudioDevice>();
        public bool SwitchDevice(AudioDevice device) { Shutdown(); return Initialize(device); }
        public bool SubmitBuffer(float[] samples, int channels, int sampleRate) { return IsReady; }
        public void Dispose() { Shutdown(); }
    }

    /// <summary>
    /// CoreAudio (Windows WASAPI / waveOut) 后端实现。
    /// 提供实际的音频 PCM 输出，同时兼容 IAudioBackend 与 IAudioBackendAdvanced 接口。
    /// </summary>
    public sealed class CoreAudioBackend : IAudioBackend, IAudioBackendAdvanced, IDisposable
    {
        public AudioBackendType BackendType => AudioBackendType.CoreAudio;
        public AudioDevice? CurrentDevice { get; private set; }
        public float MasterVolume { get; set; } = 1.0f;
        public bool IsReady { get; private set; }
        public bool IsInitialized => IsReady;

        // ── waveOut 原生互操作 ──
        private IntPtr _hWaveOut;
        private int _sampleRate;
        private int _channels;
        private WaveOutCallback? _callback;
        private readonly System.Collections.Concurrent.ConcurrentQueue<float[]> _pendingBuffers = new();

        private const int MM_WOM_DONE = 0x3BD;
        private const int WAVE_FORMAT_IEEE_FLOAT = 3;
        private const uint CALLBACK_FUNCTION = 0x00030000;
        private const uint WAVE_MAPPER = 0xFFFFFFFF;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public System.IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public System.IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public System.IntPtr lpNext;
            public System.IntPtr reserved;
        }

        private delegate void WaveOutCallback(System.IntPtr hwo, uint uMsg, System.IntPtr dwInstance, System.IntPtr dwParam1, System.IntPtr dwParam2);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint waveOutOpen(out System.IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, WaveOutCallback dwCallback, System.IntPtr dwInstance, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint waveOutClose(System.IntPtr hWaveOut);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint waveOutPrepareHeader(System.IntPtr hWaveOut, System.IntPtr lpWaveOutHdr, uint uSize);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint waveOutUnprepareHeader(System.IntPtr hWaveOut, System.IntPtr lpWaveOutHdr, uint uSize);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint waveOutWrite(System.IntPtr hWaveOut, System.IntPtr lpWaveOutHdr, uint uSize);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint waveOutReset(System.IntPtr hWaveOut);

        // ── IAudioBackend 实现（兼容旧版 AudioEngine）──

        public bool Initialize(AudioDevice device)
        {
            CurrentDevice = device;
            bool ok = Initialize(device.SampleRate, device.Channels, device.BufferSize);
            IsReady = ok;
            return ok;
        }

        public void Shutdown()
        {
            Stop();
            if (IsReady)
            {
                waveOutReset(_hWaveOut);
                while (_pendingBuffers.TryDequeue(out _)) { }
                waveOutClose(_hWaveOut);
                _hWaveOut = System.IntPtr.Zero;
            }
            IsReady = false;
            CurrentDevice = null;
        }

        public List<AudioDevice> EnumerateDevices()
        {
            var list = new List<AudioDevice>();
            if (CurrentDevice != null)
                list.Add(CurrentDevice);
            else
                list.Add(new AudioDevice { DeviceId = "default", DeviceName = "Default", IsDefault = true });
            return list;
        }

        public bool SwitchDevice(AudioDevice device)
        {
            Shutdown();
            return Initialize(device);
        }

        public bool SubmitBuffer(float[] samples, int channels, int sampleRate)
        {
            if (!IsReady || samples == null || samples.Length == 0) return false;
            SubmitBuffer(samples);
            return true;
        }

        // ── IAudioBackendAdvanced 实现（兼容 AdvancedAudioEngine）──

        public bool Initialize(int sampleRate, int channels, int bufferSize)
        {
            _sampleRate = sampleRate;
            _channels = channels;

            var format = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 32,
                nBlockAlign = (ushort)(channels * 4),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 4),
                cbSize = 0
            };

            _callback = new WaveOutCallback(OnWaveOutCallback);
            uint result = waveOutOpen(out _hWaveOut, WAVE_MAPPER, ref format, _callback, System.IntPtr.Zero, CALLBACK_FUNCTION);
            if (result != 0)
                return false;

            IsReady = true;
            return true;
        }

        public void Start() { }
        public void Stop()
        {
            if (!IsReady) return;
            waveOutReset(_hWaveOut);
            while (_pendingBuffers.TryDequeue(out _)) { }
        }

        public void SubmitBuffer(float[] buffer)
        {
            if (!IsReady || buffer == null || buffer.Length == 0) return;
            float[] copy = new float[buffer.Length];
            System.Buffer.BlockCopy(buffer, 0, copy, 0, buffer.Length * sizeof(float));
            _pendingBuffers.Enqueue(copy);
            TryWritePending();
        }

        // ── 播放音频文件（额外功能）──

        /// <summary>播放 WAV 文件（简易适配）。SoundPlayer（System.Windows.Extensions）未在工程中引用，
        /// 此处改为在 Available 后端可用时的占位调用；若不可用则静默返回。</summary>
        public void PlaySoundFile(string path)
        {
            if (!System.IO.File.Exists(path)) return;
            try
            {
                // 若后续引入额外的 WAV 播放后端，可在此接入。
            }
            catch { /* 忽略播放失败 */ }
        }

        // ── 内部 waveOut 管理 ──

        private void TryWritePending()
        {
            while (_pendingBuffers.TryDequeue(out float[]? data))
            {
                if (data == null) continue;
                int byteLen = data.Length * sizeof(float);
                var dataHandle = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
                var hdr = new WAVEHDR
                {
                    lpData = dataHandle.AddrOfPinnedObject(),
                    dwBufferLength = (uint)byteLen
                };
                var hdrHandle = System.Runtime.InteropServices.GCHandle.Alloc(hdr, System.Runtime.InteropServices.GCHandleType.Pinned);
                var pbHandle = System.Runtime.InteropServices.GCHandle.Alloc(new PendingBuffer
                {
                    DataHandle = dataHandle,
                    HeaderHandle = hdrHandle
                });
                hdr.dwUser = System.Runtime.InteropServices.GCHandle.ToIntPtr(pbHandle);
                System.Runtime.InteropServices.Marshal.StructureToPtr(hdr, hdrHandle.AddrOfPinnedObject(), false);

                uint r = waveOutPrepareHeader(_hWaveOut, hdrHandle.AddrOfPinnedObject(), (uint)System.Runtime.InteropServices.Marshal.SizeOf<WAVEHDR>());
                if (r == 0)
                {
                    r = waveOutWrite(_hWaveOut, hdrHandle.AddrOfPinnedObject(), (uint)System.Runtime.InteropServices.Marshal.SizeOf<WAVEHDR>());
                    if (r != 0)
                    {
                        waveOutUnprepareHeader(_hWaveOut, hdrHandle.AddrOfPinnedObject(), (uint)System.Runtime.InteropServices.Marshal.SizeOf<WAVEHDR>());
                        FreePendingBuffer(hdrHandle.AddrOfPinnedObject());
                    }
                }
                else
                {
                    FreePendingBuffer(hdrHandle.AddrOfPinnedObject());
                }
            }
        }

        private void OnWaveOutCallback(System.IntPtr hwo, uint uMsg, System.IntPtr dwInstance, System.IntPtr dwParam1, System.IntPtr dwParam2)
        {
            if (uMsg == MM_WOM_DONE)
            {
                System.IntPtr hdrPtr = dwParam1;
                waveOutUnprepareHeader(hwo, hdrPtr, (uint)System.Runtime.InteropServices.Marshal.SizeOf<WAVEHDR>());
                FreePendingBuffer(hdrPtr);
            }
        }

        private class PendingBuffer
        {
            public System.Runtime.InteropServices.GCHandle DataHandle;
            public System.Runtime.InteropServices.GCHandle HeaderHandle;
        }

        private static void FreePendingBuffer(System.IntPtr hdrPtr)
        {
            var hdr = System.Runtime.InteropServices.Marshal.PtrToStructure<WAVEHDR>(hdrPtr);
            if (hdr.dwUser != System.IntPtr.Zero)
            {
                var pbHandle = System.Runtime.InteropServices.GCHandle.FromIntPtr(hdr.dwUser);
                var pb = (PendingBuffer)pbHandle.Target!;
                pbHandle.Free();
                if (pb.HeaderHandle.IsAllocated) pb.HeaderHandle.Free();
                if (pb.DataHandle.IsAllocated) pb.DataHandle.Free();
            }
        }

        public void Dispose() { Shutdown(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBufferPool — 内存音频缓冲池
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频缓冲池，循环复用预分配缓冲区减少 GC 压力。</summary>
    public sealed class AudioBufferPool : IDisposable
    {
        private readonly Dictionary<int, Stack<float[]>> _pools = new();
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>池中缓冲区总数（AdvancedAudioEngine 兼容别名）。</summary>
        public int PooledCount => TotalPooledCount;

        /// <summary>从池中租用一个 float[] 缓冲区（Acquire 别名，AdvancedAudioEngine 兼容）。</summary>
        public float[] Acquire(int minSize) => Rent(minSize);

        /// <summary>归还缓冲区到池中（Release 别名，AdvancedAudioEngine 兼容）。</summary>
        public void Release(float[] buffer) => Return(buffer);

        /// <summary>池中缓冲区总数。</summary>
        public int TotalPooledCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (var stack in _pools.Values)
                        count += stack.Count;
                    return count;
                }
            }
        }

        /// <summary>从池中租用一个 float[] 缓冲区。</summary>
        public float[] Rent(int minSize)
        {
            if (_disposed) return new float[minSize];
            int roundedSize = RoundSize(minSize);
            lock (_lock)
            {
                if (_pools.TryGetValue(roundedSize, out var stack) && stack.Count > 0)
                    return stack.Pop();
            }
            return new float[roundedSize];
        }

        /// <summary>归还缓冲区到池中。</summary>
        public void Return(float[] buffer)
        {
            if (_disposed || buffer == null || buffer.Length == 0) return;
            int roundedSize = RoundSize(buffer.Length);
            lock (_lock)
            {
                if (!_pools.TryGetValue(roundedSize, out var stack))
                {
                    stack = new Stack<float[]>();
                    _pools[roundedSize] = stack;
                }
                // 限制池大小，防止内存无限增长
                if (stack.Count < 64)
                    stack.Push(buffer);
            }
        }

        /// <summary>清空所有池中缓冲区。</summary>
        public void Clear()
        {
            lock (_lock)
                _pools.Clear();
        }

        private static int RoundSize(int size)
        {
            // 向上取整到最近的 2^n (最小256)
            int n = 256;
            while (n < size) n <<= 1;
            return n;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Clear();
                _disposed = true;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEngine — 音频引擎单例
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// yama音频引擎主入口。
    /// 负责初始化/关闭、主循环 Update、设备枚举/切换、Voice 生命周期管理、
    /// 后端抽象。单例模式。
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        private static readonly Lazy<AudioEngine> _instance = new(() => new AudioEngine());
        /// <summary>音频引擎单例。</summary>
        public static AudioEngine Instance => _instance.Value;

        private AudioEngineState _state = AudioEngineState.Uninitialized;
        private IAudioBackend? _backend;
        private readonly List<AudioVoice> _activeVoices = new();
        private readonly List<AudioVoice> _voicesToRemove = new();
        private readonly object _voiceLock = new();
        private readonly AudioBufferPool _bufferPool = new();
        private readonly Dictionary<string, AudioBuffer> _loadedBuffers = new();
        private readonly Dictionary<string, AudioDevice> _devices = new();
        private double _lastUpdateTime;
        private int _maxConcurrentVoices = 64;

        // ── 属性 ──

        /// <summary>引擎状态。</summary>
        public AudioEngineState State => _state;

        /// <summary>当前音频后端。</summary>
        public IAudioBackend? Backend => _backend;

        /// <summary>当前输出设备。</summary>
        public AudioDevice? CurrentDevice => _backend?.CurrentDevice;

        /// <summary>缓冲池。</summary>
        public AudioBufferPool BufferPool => _bufferPool;

        /// <summary>最大并发 Voice 数。</summary>
        public int MaxConcurrentVoices
        {
            get => _maxConcurrentVoices;
            set => _maxConcurrentVoices = Math.Max(1, value);
        }

        /// <summary>当前活跃 Voice 数。</summary>
        public int ActiveVoiceCount
        {
            get
            {
                lock (_voiceLock)
                    return _activeVoices.Count;
            }
        }

        /// <summary>全局采样率。</summary>
        public int GlobalSampleRate { get; set; } = 44100;

        /// <summary>全局声道数。</summary>
        public int GlobalChannels { get; set; } = 2;

        private AudioEngine() { }

        // ── 初始化 / 关闭 ──

        /// <summary>使用指定后端类型初始化音频引擎。</summary>
        public bool Initialize(AudioBackendType backendType = AudioBackendType.CoreAudio)
        {
            if (_state != AudioEngineState.Uninitialized) return false;
            _state = AudioEngineState.Initializing;

            try
            {
                _backend = CreateBackend(backendType);
                var defaultDevice = new AudioDevice
                {
                    DeviceId = "default",
                    DeviceName = "Default Device",
                    SampleRate = GlobalSampleRate,
                    BitDepth = 32,
                    Channels = GlobalChannels,
                    BufferSize = 1024,
                    Format = AudioFormat.WAV,
                    IsDefault = true
                };
                _devices["default"] = defaultDevice;
                if (!_backend.Initialize(defaultDevice))
                    throw new Exception("Backend initialization failed");

                _state = AudioEngineState.Running;
                return true;
            }
            catch
            {
                _state = AudioEngineState.Uninitialized;
                _backend?.Dispose();
                _backend = null;
                return false;
            }
        }

        /// <summary>关闭音频引擎，释放所有资源。</summary>
        public void Shutdown()
        {
            if (_state == AudioEngineState.Uninitialized || _state == AudioEngineState.Shutdown)
                return;
            _state = AudioEngineState.ShuttingDown;

            lock (_voiceLock)
            {
                foreach (var voice in _activeVoices)
                    voice.Dispose();
                _activeVoices.Clear();
            }

            _loadedBuffers.Clear();
            _backend?.Shutdown();
            _backend?.Dispose();
            _backend = null;
            _bufferPool.Dispose();

            _state = AudioEngineState.Shutdown;
        }

        /// <summary>销毁引擎 (等同 Shutdown)。</summary>
        public void Dispose()
        {
            Shutdown();
        }

        // ── 设备管理 ──

        /// <summary>枚举所有可用输出设备。</summary>
        public List<AudioDevice> EnumerateDevices()
        {
            if (_backend == null) return new List<AudioDevice>();
            return _backend.EnumerateDevices();
        }

        /// <summary>切换输出设备。</summary>
        public bool SwitchDevice(AudioDevice device)
        {
            if (_backend == null) return false;
            return _backend.SwitchDevice(device);
        }

        /// <summary>注册自定义设备。</summary>
        public void RegisterDevice(AudioDevice device)
        {
            _devices[device.DeviceId] = device;
        }

        /// <summary>按 ID 获取已注册设备。</summary>
        public AudioDevice? GetDevice(string deviceId)
        {
            return _devices.TryGetValue(deviceId, out var dev) ? dev : null;
        }

        // ── 主循环 ──

        /// <summary>每帧更新，推进所有活跃 Voice。</summary>
        public void Update(double deltaTime)
        {
            if (_state != AudioEngineState.Running) return;
            _lastUpdateTime = deltaTime;

            lock (_voiceLock)
            {
                _voicesToRemove.Clear();

                int samplesToAdvance = (int)(GlobalSampleRate * deltaTime);

                foreach (var voice in _activeVoices)
                {
                    if (voice.State == AudioVoiceState.Playing)
                    {
                        voice.AdvanceFrames(samplesToAdvance);
                        if (voice.State == AudioVoiceState.Stopped && !voice.Looping)
                            _voicesToRemove.Add(voice);
                    }
                }

                foreach (var voice in _voicesToRemove)
                    _activeVoices.Remove(voice);
            }

            EnforceVoiceLimit();
        }

        // ── Voice 管理 ──

        /// <summary>创建一个 Voice 并绑定缓冲。</summary>
        public AudioVoice CreateVoice(AudioBuffer buffer)
        {
            var voice = new AudioVoice { Buffer = buffer };
            lock (_voiceLock)
                _activeVoices.Add(voice);
            return voice;
        }

        /// <summary>创建 3D Voice。</summary>
        public AudioVoice CreateVoice3D(AudioBuffer buffer, Vector3 position)
        {
            var voice = new AudioVoice
            {
                Buffer = buffer,
                Is3D = true,
                Position = position
            };
            lock (_voiceLock)
                _activeVoices.Add(voice);
            return voice;
        }

        /// <summary>停止并释放指定 Voice。</summary>
        public void DestroyVoice(AudioVoice voice)
        {
            if (voice == null) return;
            voice.Stop();
            lock (_voiceLock)
                _activeVoices.Remove(voice);
            voice.Dispose();
        }

        /// <summary>停止所有 Voice。</summary>
        public void StopAll()
        {
            lock (_voiceLock)
            {
                foreach (var v in _activeVoices)
                    v.Stop();
            }
        }

        /// <summary>暂停所有 Voice。</summary>
        public void PauseAll()
        {
            _state = AudioEngineState.Paused;
            lock (_voiceLock)
            {
                foreach (var v in _activeVoices)
                    v.Pause();
            }
        }

        /// <summary>恢复所有 Voice。</summary>
        public void ResumeAll()
        {
            _state = AudioEngineState.Running;
            lock (_voiceLock)
            {
                foreach (var v in _activeVoices)
                    v.Resume();
            }
        }

        /// <summary>按条件查找活跃 Voice。</summary>
        public List<AudioVoice> FindVoices(Predicate<AudioVoice> predicate)
        {
            var result = new List<AudioVoice>();
            lock (_voiceLock)
            {
                foreach (var v in _activeVoices)
                    if (predicate(v))
                        result.Add(v);
            }
            return result;
        }

        // ── 缓冲管理 ──

        /// <summary>加载 AudioBuffer 到缓存。</summary>
        public void RegisterBuffer(string name, AudioBuffer buffer)
        {
            _loadedBuffers[name] = buffer;
        }

        /// <summary>按名称获取缓存的 AudioBuffer。</summary>
        public AudioBuffer? GetBuffer(string name)
        {
            return _loadedBuffers.TryGetValue(name, out var buf) ? buf : null;
        }

        /// <summary>卸载指定缓冲。</summary>
        public bool UnregisterBuffer(string name)
        {
            return _loadedBuffers.Remove(name);
        }

        // ── 内部方法 ──

        private void EnforceVoiceLimit()
        {
            lock (_voiceLock)
            {
                if (_activeVoices.Count <= _maxConcurrentVoices) return;

                // 按优先级排序，最低优先级的先虚拟化
                _activeVoices.Sort((a, b) => a.Priority.CompareTo(b.Priority));

                int excessCount = _activeVoices.Count - _maxConcurrentVoices;
                for (int i = 0; i < excessCount; i++)
                {
                    if (_activeVoices[i].State == AudioVoiceState.Playing)
                        _activeVoices[i].SetVirtualized(true);
                }
            }
        }

        private static IAudioBackend CreateBackend(AudioBackendType type)
        {
            return type switch
            {
                AudioBackendType.OpenAL => new OpenALBackend(),
                AudioBackendType.ALSoft => new OpenALBackend(),
                AudioBackendType.XAudio2 => new XAudio2Backend(),
                AudioBackendType.CoreAudio => new CoreAudioBackend(),
                _ => new CoreAudioBackend()
            };
        }
    }
}
