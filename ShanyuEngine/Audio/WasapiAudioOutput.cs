// ============================================================================
// ShanyuEngine WasapiAudioOutput.cs
// 山雨引擎 - WASAPI 音频输出驱动
// 使用 Windows Core Audio API (IMMDeviceEnumerator / IAudioClient / IAudioRenderClient)
// 将混音后的浮点采样缓冲区写入系统默认音频渲染设备。
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShanyuEngine.Audio
{
    /// <summary>
    /// WASAPI 音频输出驱动 — 将混音后的音频缓冲区写入系统音频设备。
    /// </summary>
    public class WasapiAudioOutput : IDisposable
    {
        // ====================================================================
        // WASAPI COM 接口定义
        // ====================================================================

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            int GetId(out IntPtr ppstrId);
            int GetState(out int pdwState);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int ShareMode, uint StreamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr AudioSessionGuid);
            int GetBufferSize(out uint pNumBufferFrames);
            int GetStreamLatency(out long phnsLatency);
            int GetCurrentPadding(out uint pNumPaddingFrames);
            int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
            int GetMixFormat(out IntPtr ppDeviceFormat);
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr eventHandle);
            int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioRenderClient
        {
            int GetBuffer(int NumFramesRequested, out IntPtr ppData);
            int ReleaseBuffer(int NumFramesWritten, uint dwFlags);
        }

        // ====================================================================
        // 常量
        // ====================================================================

        [StructLayout(LayoutKind.Sequential)]
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

        private const int EDataFlow_eRender = 0;
        private const int ERole_eConsole = 0;
        private const int CLSCTX_ALL = 23;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const int REFTIMES_PER_SEC = 10000000;

        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        // ====================================================================
        // 字段
        // ====================================================================

        private IAudioClient? _audioClient;
        private IAudioRenderClient? _renderClient;
        private IntPtr _waveFormatPtr;
        private uint _bufferFrameCount;
        private bool _isRunning;
        private Thread? _audioThread;
        private bool _isInitialized;

        /// <summary>音频回调 — 由外部提供混音数据。</summary>
        public delegate void AudioCallback(float[] buffer, int frames, int channels);
        public event AudioCallback? OnAudioCallback;

        public int SampleRate { get; private set; } = 48000;
        public int Channels { get; private set; } = 2;
        public int BufferFrames { get; } = 480; // 10ms @ 48kHz
        public bool IsInitialized => _isInitialized;

        // ====================================================================
        // 初始化
        // ====================================================================

        /// <summary>
        /// 初始化WASAPI音频输出。
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;

            // 1. 创建设备枚举器
            var enumeratorType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType!);
            if (enumerator == null)
                throw new Exception("无法创建 IMMDeviceEnumerator");

            // 2. 获取默认音频渲染设备
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow_eRender, ERole_eConsole, out var device);
            if (hr != 0)
                throw new COMException($"GetDefaultAudioEndpoint 失败 (HR=0x{hr:X8})", hr);

            // 3. 激活 IAudioClient
            hr = device.Activate(ref IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out var audioClientObj);
            if (hr != 0)
                throw new COMException($"Activate IAudioClient 失败 (HR=0x{hr:X8})", hr);
            _audioClient = (IAudioClient)audioClientObj;

            // 4. 获取设备混合格式
            hr = _audioClient.GetMixFormat(out _waveFormatPtr);
            if (hr != 0)
                throw new COMException($"GetMixFormat 失败 (HR=0x{hr:X8})", hr);

            var format = Marshal.PtrToStructure<WAVEFORMATEX>(_waveFormatPtr);
            SampleRate = (int)format.nSamplesPerSec;
            Channels = format.nChannels;

            // 5. 初始化音频客户端 (10ms 缓冲区)
            long bufferDuration = (long)(REFTIMES_PER_SEC * (double)BufferFrames / SampleRate);
            hr = _audioClient.Initialize(
                0, /* AUDCLNT_SHAREMODE_SHARED */
                AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                bufferDuration,
                0,
                _waveFormatPtr,
                IntPtr.Zero);

            if (hr != 0)
            {
                // 降级：不使用 AUTOCONVERTPCM
                hr = _audioClient.Initialize(0, 0, bufferDuration, 0, _waveFormatPtr, IntPtr.Zero);
                if (hr != 0)
                    throw new COMException($"IAudioClient::Initialize 失败 (HR=0x{hr:X8})", hr);
            }

            // 6. 获取缓冲区大小
            hr = _audioClient.GetBufferSize(out _bufferFrameCount);
            if (hr != 0)
                throw new COMException($"GetBufferSize 失败 (HR=0x{hr:X8})", hr);

            // 7. 获取渲染客户端
            hr = _audioClient.GetService(ref IID_IAudioRenderClient, out var renderClientObj);
            if (hr != 0)
                throw new COMException($"GetService(IAudioRenderClient) 失败 (HR=0x{hr:X8})", hr);
            _renderClient = (IAudioRenderClient)renderClientObj;

            _isInitialized = true;
        }

        // ====================================================================
        // 启动 / 停止
        // ====================================================================

        /// <summary>
        /// 启动音频输出线程。
        /// </summary>
        public void Start()
        {
            if (_isRunning || !_isInitialized) return;
            _isRunning = true;

            int hr = _audioClient!.Start();
            if (hr != 0)
                throw new COMException($"IAudioClient::Start 失败 (HR=0x{hr:X8})", hr);

            _audioThread = new Thread(AudioThreadLoop)
            {
                Name = "WasapiAudioThread",
                Priority = ThreadPriority.TimeCritical,
                IsBackground = true
            };
            _audioThread.Start();
        }

        /// <summary>
        /// 停止音频输出。
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _audioThread?.Join(500);
            _audioThread = null;

            try
            {
                _audioClient?.Stop();
            }
            catch { /* 忽略 */ }
        }

        // ====================================================================
        // 音频线程
        // ====================================================================

        private void AudioThreadLoop()
        {
            int bufferSamples = BufferFrames * Channels;
            var buffer = new float[bufferSamples];

            while (_isRunning)
            {
                if (_audioClient == null || _renderClient == null) break;

                try
                {
                    // 1. 获取当前已填充的采样数
                    _audioClient.GetCurrentPadding(out uint padding);
                    int availableFrames = (int)(_bufferFrameCount - padding);
                    if (availableFrames <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    int framesToRender = Math.Min(availableFrames, BufferFrames);
                    int samplesToRender = framesToRender * Channels;

                    // 2. 请求混音数据
                    Array.Clear(buffer, 0, buffer.Length);
                    OnAudioCallback?.Invoke(buffer, framesToRender, Channels);

                    // 3. 写入 WASAPI 缓冲区
                    _renderClient.GetBuffer(framesToRender, out IntPtr dataPtr);
                    Marshal.Copy(buffer, 0, dataPtr, samplesToRender);
                    _renderClient.ReleaseBuffer((uint)framesToRender, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WasapiAudioOutput] 音频线程异常: {ex.Message}");
                    Thread.Sleep(10);
                }
            }
        }

        // ====================================================================
        // 释放
        // ====================================================================

        public void Dispose()
        {
            Stop();

            if (_renderClient != null)
            {
                try { Marshal.ReleaseComObject(_renderClient); } catch { }
                _renderClient = null;
            }

            if (_audioClient != null)
            {
                try { Marshal.ReleaseComObject(_audioClient); } catch { }
                _audioClient = null;
            }

            if (_waveFormatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_waveFormatPtr);
                _waveFormatPtr = IntPtr.Zero;
            }

            _isInitialized = false;
        }
    }
}
