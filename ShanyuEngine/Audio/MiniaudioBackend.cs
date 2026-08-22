using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Audio
{
    /// <summary>
    /// 基于 Windows waveOut API 的音频后端。
    /// 作为 Miniaudio.Net 的 fallback 实现，提供实际的音频 PCM 输出。
    /// </summary>
    public class MiniaudioBackend : IAudioBackendAdvanced
    {
        public AudioBackendType BackendType => AudioBackendType.WASAPI;
        public bool IsInitialized { get; private set; }

        private IntPtr _hWaveOut;
        private int _sampleRate;
        private int _channels;
        private WaveOutCallback? _callback;
        private readonly ConcurrentQueue<float[]> _pendingBuffers = new();

        private const int MM_WOM_DONE = 0x3BD;
        private const int WAVE_FORMAT_IEEE_FLOAT = 3;
        private const uint CALLBACK_FUNCTION = 0x00030000;
        private const uint WAVE_MAPPER = 0xFFFFFFFF;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private delegate void WaveOutCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        private static extern uint waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, WaveOutCallback dwCallback, IntPtr dwInstance, uint dwFlags);

        [DllImport("winmm.dll")]
        private static extern uint waveOutClose(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern uint waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

        [DllImport("winmm.dll")]
        private static extern uint waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

        [DllImport("winmm.dll")]
        private static extern uint waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

        [DllImport("winmm.dll")]
        private static extern uint waveOutReset(IntPtr hWaveOut);

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
            uint result = waveOutOpen(out _hWaveOut, WAVE_MAPPER, ref format, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
            if (result != 0)
                return false;

            IsInitialized = true;
            return true;
        }

        public void Start() { }
        public void Stop() { }

        public void Shutdown()
        {
            if (!IsInitialized) return;
            waveOutReset(_hWaveOut);
            while (_pendingBuffers.TryDequeue(out _)) { }
            waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
            IsInitialized = false;
        }

        public void SubmitBuffer(float[] buffer)
        {
            if (!IsInitialized || buffer == null || buffer.Length == 0) return;
            // 复制数据，因为上层会立即归还缓冲到池中
            float[] copy = new float[buffer.Length];
            Buffer.BlockCopy(buffer, 0, copy, 0, buffer.Length * sizeof(float));
            _pendingBuffers.Enqueue(copy);
            TryWritePending();
        }

        private void TryWritePending()
        {
            while (_pendingBuffers.TryDequeue(out float[]? data))
            {
                if (data == null) continue;
                int byteLen = data.Length * sizeof(float);
                var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
                var hdr = new WAVEHDR
                {
                    lpData = dataHandle.AddrOfPinnedObject(),
                    dwBufferLength = (uint)byteLen
                };
                var hdrHandle = GCHandle.Alloc(hdr, GCHandleType.Pinned);
                var pbHandle = GCHandle.Alloc(new PendingBuffer
                {
                    DataHandle = dataHandle,
                    HeaderHandle = hdrHandle
                });
                hdr.dwUser = GCHandle.ToIntPtr(pbHandle);
                Marshal.StructureToPtr(hdr, hdrHandle.AddrOfPinnedObject(), false);

                uint r = waveOutPrepareHeader(_hWaveOut, hdrHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WAVEHDR>());
                if (r == 0)
                {
                    r = waveOutWrite(_hWaveOut, hdrHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WAVEHDR>());
                    if (r != 0)
                    {
                        waveOutUnprepareHeader(_hWaveOut, hdrHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WAVEHDR>());
                        FreePendingBuffer(hdrHandle.AddrOfPinnedObject());
                    }
                }
                else
                {
                    FreePendingBuffer(hdrHandle.AddrOfPinnedObject());
                }
            }
        }

        private void OnWaveOutCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg == MM_WOM_DONE)
            {
                IntPtr hdrPtr = dwParam1;
                waveOutUnprepareHeader(hwo, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                FreePendingBuffer(hdrPtr);
            }
        }

        private class PendingBuffer
        {
            public GCHandle DataHandle;
            public GCHandle HeaderHandle;
        }

        private static void FreePendingBuffer(IntPtr hdrPtr)
        {
            var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtr);
            if (hdr.dwUser != IntPtr.Zero)
            {
                var pbHandle = GCHandle.FromIntPtr(hdr.dwUser);
                var pb = (PendingBuffer)pbHandle.Target!;
                pbHandle.Free();
                if (pb.HeaderHandle.IsAllocated) pb.HeaderHandle.Free();
                if (pb.DataHandle.IsAllocated) pb.DataHandle.Free();
            }
        }
    }
}
