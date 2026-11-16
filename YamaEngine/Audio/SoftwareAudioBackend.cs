// ============================================================================
//  YamaEngine.Audio.SoftwareAudioBackend.cs
//  yama引擎 -- 软件/离线音频后端 (无原生依赖)
// ----------------------------------------------------------------------------
//  闭合"非 Windows 后端"缺口的可测试部分:
//    * 纯托管实现, 不依赖 WASAPI/waveOut/OpenAL 原生库, 全平台可运行。
//    * 捕获提交的 PCM 缓冲, 供无音频设备环境 (CI/无头服务器/离线渲染) 使用。
//    * 支持离线渲染: 提交的浮点 PCM 累积到渲染缓冲, 可回读验证。
//  与 UE 的整合: 类比 UE AudioDevice 的 NULL/离线后端 (AudioDevice 无输出设备时)。
// ============================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.Audio
{
    /// <summary>
    /// 软件/离线音频后端。捕获所有提交的 PCM 样本, 不产生硬件输出。
    /// 用于无音频设备环境、自动化测试与离线渲染 (如 WAV 导出)。
    /// </summary>
    public sealed class SoftwareAudioBackend : IAudioBackendAdvanced
    {
        private readonly object _lock = new();
        private readonly List<float> _renderedSamples = new();
        private int _sampleRate;
        private int _channels;
        private long _submittedBuffers;
        private long _totalSamples;

        public AudioBackendType BackendType => AudioBackendType.Software;
        public bool IsInitialized { get; private set; }

        /// <summary>采样率。</summary>
        public int SampleRate => _sampleRate;

        /// <summary>声道数。</summary>
        public int Channels => _channels;

        /// <summary>已提交的缓冲数。</summary>
        public long SubmittedBufferCount => _submittedBuffers;

        /// <summary>已累积的样本总数 (帧 * 声道)。</summary>
        public long TotalRenderedSamples => _totalSamples;

        public bool Initialize(int sampleRate, int channels, int bufferSize)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _channels = channels > 0 ? channels : 2;
            IsInitialized = true;
            return true;
        }

        public void Start() { }

        public void Stop() { }

        /// <summary>提交 PCM 缓冲: 累积到渲染缓冲 (交错帧: [L,R,L,R,...])。</summary>
        public void SubmitBuffer(float[] buffer)
        {
            if (!IsInitialized || buffer == null || buffer.Length == 0) return;
            lock (_lock)
            {
                _renderedSamples.AddRange(buffer);
                _totalSamples += buffer.Length;
            }
            _submittedBuffers++;
        }

        /// <summary>清空已累积的渲染样本。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _renderedSamples.Clear();
                _submittedBuffers = 0;
                _totalSamples = 0;
            }
        }

        /// <summary>获取已渲染的交错 PCM 快照 (副本)。</summary>
        public float[] GetRenderedSamples()
        {
            lock (_lock)
                return _renderedSamples.ToArray();
        }

        /// <summary>获取已渲染的样本数 (帧数 = samples / channels)。</summary>
        public int GetRenderedFrameCount() => _channels > 0 ? (int)(_totalSamples / _channels) : 0;

        /// <summary>
        /// 导出已渲染 PCM 为 16-bit WAV 字节流 (离线渲染输出)。
        /// </summary>
        public byte[] ExportWav16Bit()
        {
            float[] samples;
            lock (_lock)
                samples = _renderedSamples.ToArray();

            int sampleRate = _sampleRate, channels = _channels;
            int dataSize = samples.Length * 2; // 16-bit = 2 bytes/sample
            int byteRate = sampleRate * channels * 2;
            int blockAlign = channels * 2;

            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms);

            // RIFF 头
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataSize);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            // fmt 块
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);            // PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write((short)blockAlign);
            w.Write((short)16);           // bits per sample
            // data 块
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataSize);
            foreach (var s in samples)
            {
                short v = (short)(Math.Clamp(s, -1f, 1f) * 32767f);
                w.Write(v);
            }

            w.Flush();
            return ms.ToArray();
        }

        public void Shutdown()
        {
            lock (_lock)
                _renderedSamples.Clear();
            IsInitialized = false;
        }
    }
}
