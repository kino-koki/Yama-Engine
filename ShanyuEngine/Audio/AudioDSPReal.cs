// ============================================================================
// ShanyuEngine AudioDSPReal.cs
// 山雨引擎 - 真实音频DSP数字信号处理实现
// 包含: AudioBuffer/AudioFormat, Biquad/OnePole/LinkwitzRiley/Allpass/Comb/
//       MovingAverage滤波器, FFT(Cooley-Tukey)/频谱分析, 压缩器/扩展器/
//       限制器/噪声门/瞬态设计器。全部为真实算法实现。
// ============================================================================

using System;
using System.Collections.Generic;

namespace ShanyuEngine.Audio
{
    // ========================================================================
    // 音频缓冲区与格式
    // ========================================================================

    /// <summary>
    /// 音频样本格式编码
    /// </summary>
    public enum AudioSampleFormat
    {
        Pcm8Bit,
        Pcm16Bit,
        Pcm24Bit,
        Pcm32Bit,
        IeeeFloat32,
        IeeeFloat64
    }

    /// <summary>
    /// 音频格式描述：采样率/位深/声道/编码
    /// </summary>
    public sealed class AudioFormat
    {
        public int SampleRate { get; set; }
        public int BitsPerSample { get; set; }
        public int Channels { get; set; }
        public AudioSampleFormat Encoding { get; set; }

        public AudioFormat(int sampleRate = 44100, int bitsPerSample = 16,
                           int channels = 2, AudioSampleFormat encoding = AudioSampleFormat.Pcm16Bit)
        {
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
            Channels = channels;
            Encoding = encoding;
        }

        public double DurationSeconds(int totalFrames)
        {
            return SampleRate > 0 ? (double)totalFrames / SampleRate : 0.0;
        }

        public int BytesPerSample
        {
            get
            {
                switch (Encoding)
                {
                    case AudioSampleFormat.Pcm8Bit: return 1;
                    case AudioSampleFormat.Pcm16Bit: return 2;
                    case AudioSampleFormat.Pcm24Bit: return 3;
                    case AudioSampleFormat.Pcm32Bit: return 4;
                    case AudioSampleFormat.IeeeFloat32: return 4;
                    case AudioSampleFormat.IeeeFloat64: return 8;
                    default: return 2;
                }
            }
        }

        public AudioFormat Clone()
        {
            return new AudioFormat(SampleRate, BitsPerSample, Channels, Encoding);
        }
    }

    /// <summary>
    /// 音频缓冲区：支持Planar(每通道独立float[])与Interleaved(交叉存储)格式互转。
    /// 样本为float，范围[-1,1]。
    /// </summary>
    public sealed class AudioBuffer
    {
        /// <summary>Planar数据：_channelData[ch][frame]</summary>
        private readonly float[][] _channelData;
        private readonly int _channels;
        private readonly int _frames;
        private int _readFrameIndex;

        public int Channels => _channels;
        public int Frames => _frames;
        public int SampleRate { get; set; }

        public AudioBuffer(int channels, int frames, int sampleRate = 44100)
        {
            if (channels <= 0) throw new ArgumentException("通道数必须大于0", nameof(channels));
            if (frames < 0) throw new ArgumentException("帧数不能为负", nameof(frames));
            _channels = channels;
            _frames = frames;
            SampleRate = sampleRate;
            _channelData = new float[channels][];
            for (int c = 0; c < channels; c++)
                _channelData[c] = new float[frames];
            _readFrameIndex = 0;
        }

        public float[] GetChannel(int channel)
        {
            if (channel < 0 || channel >= _channels)
                throw new ArgumentOutOfRangeException(nameof(channel));
            return _channelData[channel];
        }

        public void SetChannel(int channel, float[] data)
        {
            if (data == null || data.Length < _frames)
                throw new ArgumentException("数据长度不足");
            Array.Copy(data, _channelData[channel], _frames);
        }

        public float GetSample(int channel, int frame)
        {
            return _channelData[channel][frame];
        }

        public void SetSample(int channel, int frame, float value)
        {
            _channelData[channel][frame] = value;
        }

        public void Clear()
        {
            for (int c = 0; c < _channels; c++)
                Array.Clear(_channelData[c], 0, _frames);
            _readFrameIndex = 0;
        }

        public int ReadFrame(float[] outputInterleaved, int maxFrames)
        {
            int remaining = _frames - _readFrameIndex;
            int toRead = Math.Min(maxFrames, remaining);
            for (int f = 0; f < toRead; f++)
            {
                int srcFrame = _readFrameIndex + f;
                for (int c = 0; c < _channels; c++)
                    outputInterleaved[f * _channels + c] = _channelData[c][srcFrame];
            }
            _readFrameIndex += toRead;
            return toRead;
        }

        public int WriteFrame(float[] inputInterleaved, int numFrames)
        {
            int remaining = _frames - _readFrameIndex;
            int toWrite = Math.Min(numFrames, remaining);
            for (int f = 0; f < toWrite; f++)
            {
                for (int c = 0; c < _channels; c++)
                    _channelData[c][_readFrameIndex + f] = inputInterleaved[f * _channels + c];
            }
            _readFrameIndex += toWrite;
            return toWrite;
        }

        /// <summary>Planar float[][] → Interleaved float[]</summary>
        public static float[] ToInterleaved(float[][] planar, int channels, int frames)
        {
            float[] interleaved = new float[channels * frames];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++)
                    interleaved[f * channels + c] = planar[c][f];
            return interleaved;
        }

        /// <summary>Interleaved float[] → Planar float[][]</summary>
        public static float[][] ToPlanar(float[] interleaved, int channels, int frames)
        {
            float[][] planar = new float[channels][];
            for (int c = 0; c < channels; c++)
                planar[c] = new float[frames];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++)
                    planar[c][f] = interleaved[f * channels + c];
            return planar;
        }

        public float[] ToInterleaved()
        {
            return ToInterleaved(_channelData, _channels, _frames);
        }

        public void FromInterleaved(float[] interleaved)
        {
            int frames = interleaved.Length / _channels;
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < _channels; c++)
                    _channelData[c][f] = interleaved[f * _channels + c];
        }

        public void MixIn(AudioBuffer other, float gain)
        {
            int minFrames = Math.Min(_frames, other._frames);
            int minCh = Math.Min(_channels, other._channels);
            for (int c = 0; c < minCh; c++)
            {
                float[] dst = _channelData[c];
                float[] src = other._channelData[c];
                for (int f = 0; f < minFrames; f++)
                    dst[f] += src[f] * gain;
            }
        }

        public void ApplyGain(float gain)
        {
            for (int c = 0; c < _channels; c++)
            {
                float[] ch = _channelData[c];
                for (int f = 0; f < _frames; f++)
                    ch[f] *= gain;
            }
        }
    }

    // ========================================================================
    // 滤波器
    // ========================================================================

    /// <summary>
    /// 双二阶滤波器类型（RBJ Audio EQ Cookbook）
    /// </summary>
    public enum BiquadType
    {
        LowPass,
        HighPass,
        BandPass,
        Notch,
        AllPass,
        LowShelf,
        HighShelf,
        PeakingEQ
    }

    /// <summary>
    /// 双二阶滤波器(Biquad Filter) - 基于RBJ Audio EQ Cookbook系数公式。
    /// 传递函数: H(z) = (b0 + b1*z^-1 + b2*z^-2) / (a0 + a1*z^-1 + a2*z^-2)
    /// 使用 Transposed Direct Form II 实现。
    /// </summary>
    public class BiquadFilter
    {
        // 归一化系数（已除以a0）
        private double _b0, _b1, _b2;
        private double _a1, _a2;
        // 状态变量 (Transposed Direct Form II)
        private double _z1, _z2;

        private double _sampleRate;
        private double _frequency;
        private double _q;
        private double _gainDb;
        private BiquadType _type;

        public double B0 => _b0;
        public double B1 => _b1;
        public double B2 => _b2;
        public double A1 => _a1;
        public double A2 => _a2;

        public BiquadFilter(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _frequency = 1000.0;
            _q = 0.7071067811865476; // 1/sqrt(2) - Butterworth
            _gainDb = 0.0;
            _type = BiquadType.LowPass;
            _b0 = 1.0; _b1 = 0.0; _b2 = 0.0;
            _a1 = 0.0; _a2 = 0.0;
            _z1 = 0.0; _z2 = 0.0;
            RecalculateCoefficients();
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            RecalculateCoefficients();
        }

        public void SetParameters(BiquadType type, double frequency, double q, double gainDb = 0.0)
        {
            _type = type;
            _frequency = frequency;
            _q = q;
            _gainDb = gainDb;
            RecalculateCoefficients();
        }

        /// <summary>
        /// 根据RBJ Audio EQ Cookbook公式计算系数。
        /// w0 = 2*pi*f0/Fs, alpha = sin(w0)/(2*Q)
        /// </summary>
        public void RecalculateCoefficients()
        {
            double f0 = _frequency;
            double Fs = _sampleRate;
            double Q = _q;

            // 限制频率到Nyquist以内
            double nyquist = Fs * 0.5;
            if (f0 > nyquist * 0.999) f0 = nyquist * 0.999;
            if (f0 < 1.0) f0 = 1.0;
            if (Q < 0.0001) Q = 0.0001;

            double w0 = 2.0 * Math.PI * f0 / Fs;
            double cosw0 = Math.Cos(w0);
            double sinw0 = Math.Sin(w0);
            double alpha = sinw0 / (2.0 * Q);

            // Shelf/EQ用到的量
            double A = Math.Pow(10.0, _gainDb / 40.0); // gainDb -> 线性增益(振幅)
            // shelf的beta参数
            double beta = Math.Sqrt(A) / Q;

            double b0 = 1.0, b1 = 0.0, b2 = 0.0;
            double a0 = 1.0, a1 = 0.0, a2 = 0.0;

            switch (_type)
            {
                case BiquadType.LowPass:
                    b0 = (1.0 - cosw0) / 2.0;
                    b1 = 1.0 - cosw0;
                    b2 = (1.0 - cosw0) / 2.0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosw0;
                    a2 = 1.0 - alpha;
                    break;

                case BiquadType.HighPass:
                    b0 = (1.0 + cosw0) / 2.0;
                    b1 = -(1.0 + cosw0);
                    b2 = (1.0 + cosw0) / 2.0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosw0;
                    a2 = 1.0 - alpha;
                    break;

                case BiquadType.BandPass: // constant 0 dB peak gain
                    b0 = alpha;
                    b1 = 0.0;
                    b2 = -alpha;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosw0;
                    a2 = 1.0 - alpha;
                    break;

                case BiquadType.Notch:
                    b0 = 1.0;
                    b1 = -2.0 * cosw0;
                    b2 = 1.0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosw0;
                    a2 = 1.0 - alpha;
                    break;

                case BiquadType.AllPass:
                    b0 = 1.0 - alpha;
                    b1 = -2.0 * cosw0;
                    b2 = 1.0 + alpha;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosw0;
                    a2 = 1.0 - alpha;
                    break;

                case BiquadType.PeakingEQ:
                    b0 = 1.0 + alpha * A;
                    b1 = -2.0 * cosw0;
                    b2 = 1.0 - alpha * A;
                    a0 = 1.0 + alpha / A;
                    a1 = -2.0 * cosw0;
                    a2 = 1.0 - alpha / A;
                    break;

                case BiquadType.LowShelf:
                    b0 = A * ((A + 1.0) - (A - 1.0) * cosw0 + 2.0 * Math.Sqrt(A) * alpha);
                    b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cosw0);
                    b2 = A * ((A + 1.0) - (A - 1.0) * cosw0 - 2.0 * Math.Sqrt(A) * alpha);
                    a0 = (A + 1.0) + (A - 1.0) * cosw0 + 2.0 * Math.Sqrt(A) * alpha;
                    a1 = -2.0 * ((A - 1.0) + (A + 1.0) * cosw0);
                    a2 = (A + 1.0) + (A - 1.0) * cosw0 - 2.0 * Math.Sqrt(A) * alpha;
                    break;

                case BiquadType.HighShelf:
                    b0 = A * ((A + 1.0) + (A - 1.0) * cosw0 + 2.0 * Math.Sqrt(A) * alpha);
                    b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cosw0);
                    b2 = A * ((A + 1.0) + (A - 1.0) * cosw0 - 2.0 * Math.Sqrt(A) * alpha);
                    a0 = (A + 1.0) - (A - 1.0) * cosw0 + 2.0 * Math.Sqrt(A) * alpha;
                    a1 = 2.0 * ((A - 1.0) - (A + 1.0) * cosw0);
                    a2 = (A + 1.0) - (A - 1.0) * cosw0 - 2.0 * Math.Sqrt(A) * alpha;
                    break;
            }

            // 归一化：除以a0
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        /// <summary>
        /// 处理单个样本 - Transposed Direct Form II
        /// y[n] = b0*x[n] + z1
        /// z1 = b1*x[n] - a1*y[n] + z2
        /// z2 = b2*x[n] - a2*y[n]
        /// </summary>
        public double Process(double sample)
        {
            double output = _b0 * sample + _z1;
            _z1 = _b1 * sample - _a1 * output + _z2;
            _z2 = _b2 * sample - _a2 * output;
            return output;
        }

        /// <summary>批量处理缓冲区</summary>
        public void ProcessBuffer(float[] buffer, int start, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[start + i] = (float)Process(buffer[start + i]);
        }

        public void ProcessBuffer(float[] buffer)
        {
            ProcessBuffer(buffer, 0, buffer.Length);
        }

        public void ProcessBuffer(double[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = Process(buffer[i]);
        }

        public void Reset()
        {
            _z1 = 0.0;
            _z2 = 0.0;
        }
    }

    /// <summary>
    /// 一阶滤波器 - 低通或高通，简化版。
    /// y[n] = b0*x[n] + b1*x[n-1] - a1*y[n-1]
    /// </summary>
    public class OnePoleFilter
    {
        public enum OnePoleType { LowPass, HighPass }

        private double _a1;   // 反馈系数
        private double _b0, _b1;
        private double _prevX, _prevY;
        private OnePoleType _type;
        private double _sampleRate;
        private double _cutoff;

        public OnePoleFilter(double sampleRate = 44100.0, double cutoff = 1000.0, OnePoleType type = OnePoleType.LowPass)
        {
            _sampleRate = sampleRate;
            _cutoff = cutoff;
            _type = type;
            _prevX = 0; _prevY = 0;
            Recalculate();
        }

        public void SetParameters(double cutoff, OnePoleType type)
        {
            _cutoff = cutoff;
            _type = type;
            Recalculate();
        }

        private void Recalculate()
        {
            double nyquist = _sampleRate * 0.5;
            double f = _cutoff;
            if (f > nyquist * 0.999) f = nyquist * 0.999;
            if (f < 1.0) f = 1.0;
            // 一阶RC低通: a = exp(-2*pi*fc/Fs)
            _a1 = Math.Exp(-2.0 * Math.PI * f / _sampleRate);
            if (_type == OnePoleType.LowPass)
            {
                _b0 = 1.0 - _a1;
                _b1 = 0.0;
            }
            else // HighPass
            {
                _b0 = (1.0 + _a1) / 2.0;
                _b1 = -(1.0 + _a1) / 2.0;
            }
        }

        public double Process(double sample)
        {
            double output = _b0 * sample + _b1 * _prevX - _a1 * _prevY;
            _prevX = sample;
            _prevY = output;
            return output;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            _prevX = 0; _prevY = 0;
        }
    }

    /// <summary>
    /// Linkwitz-Riley 分频滤波器 - 两个Biquad级联，产生-6dB在分频点的平坦响应。
    /// 常用于多分频扬声器系统，LR4 = 4阶(两个二阶)。
    /// </summary>
    public class LinkwitzRileyFilter
    {
        private readonly BiquadFilter _stage1;
        private readonly BiquadFilter _stage2;
        private double _sampleRate;

        public BiquadFilter LowPassStage1 => _stage1;
        public BiquadFilter LowPassStage2 => _stage2;

        public LinkwitzRileyFilter(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _stage1 = new BiquadFilter(sampleRate);
            _stage2 = new BiquadFilter(sampleRate);
            SetCrossover(1000.0, 4); // 默认LR4
        }

        /// <summary>
        /// 设置分频频率。order=2(LR2)或4(LR4)。
        /// LR使用Q=0.5的两个级联butterworth低通。
        /// </summary>
        public void SetCrossover(double frequency, int order = 4)
        {
            double q = 0.5; // Butterworth Q for each stage in LR
            // LR2: 单个biquad Q=0.5; LR4: 两个Q=0.5 biquad级联
            if (order == 2)
            {
                _stage1.SetParameters(BiquadType.LowPass, frequency, q);
                _stage2.SetParameters(BiquadType.LowPass, frequency, 1e9); // 直通
                // 直通：设置b0=1,其他=0
                _stage2.SetParameters(BiquadType.AllPass, frequency, 0.7);
            }
            else // LR4
            {
                _stage1.SetParameters(BiquadType.LowPass, frequency, q);
                _stage2.SetParameters(BiquadType.LowPass, frequency, q);
            }
        }

        public double ProcessLowPass(double sample)
        {
            return _stage2.Process(_stage1.Process(sample));
        }

        /// <summary>获取高通分支(互补)</summary>
        public double ProcessHighPass(double sample, BiquadFilter highpass1, BiquadFilter highpass2)
        {
            return highpass2.Process(highpass1.Process(sample));
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            _stage1.SetSampleRate(sampleRate);
            _stage2.SetSampleRate(sampleRate);
        }

        public void Reset()
        {
            _stage1.Reset();
            _stage2.Reset();
        }
    }

    /// <summary>
    /// 全通滤波器 - 改变相位但不改变幅度。用于混响和相位效果。
    /// y[n] = -g*x[n] + x[n-D] + g*y[n-D]
    /// </summary>
    public class AllpassFilter
    {
        private readonly double[] _delayBuffer;
        private int _delayIndex;
        private readonly int _delaySamples;
        private double _feedback;

        public double Feedback
        {
            get => _feedback;
            set => _feedback = Math.Max(-0.999, Math.Min(0.999, value));
        }

        public AllpassFilter(int delaySamples, double feedback = 0.5)
        {
            _delaySamples = Math.Max(1, delaySamples);
            _delayBuffer = new double[_delaySamples];
            _delayIndex = 0;
            _feedback = feedback;
        }

        public double Process(double sample)
        {
            double delayed = _delayBuffer[_delayIndex];
            // Allpass: y[n] = -g*x + d + g*y_delayed (Schroeder form)
            // Equivalent: output = delayed - g * (sample + g*delayed) ... standard Schroeder allpass
            double output = delayed + _feedback * sample;
            _delayBuffer[_delayIndex] = sample - _feedback * delayed;
            _delayIndex = (_delayIndex + 1) % _delaySamples;
            return output;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delaySamples);
            _delayIndex = 0;
        }
    }

    /// <summary>
    /// 梳状滤波器 - 延迟线+反馈，用于混响。
    /// y[n] = x[n] + feedback * y[n-D]
    /// 可选低通在反馈回路中模拟高频衰减。
    /// </summary>
    public class CombFilter
    {
        private readonly double[] _delayBuffer;
        private int _delayIndex;
        private readonly int _delaySamples;
        private double _feedback;
        private double _lowpassState;
        private double _damping; // 反馈回路低通截止(0=全通,1=最大衰减)

        public double Feedback
        {
            get => _feedback;
            set => _feedback = Math.Max(-0.999, Math.Min(0.999, value));
        }

        public double Damping
        {
            get => _damping;
            set => _damping = Math.Max(0.0, Math.Min(1.0, value));
        }

        public CombFilter(int delaySamples, double feedback = 0.5, double damping = 0.0)
        {
            _delaySamples = Math.Max(1, delaySamples);
            _delayBuffer = new double[_delaySamples];
            _delayIndex = 0;
            _feedback = feedback;
            _damping = damping;
            _lowpassState = 0.0;
        }

        public double Process(double sample)
        {
            double output = _delayBuffer[_delayIndex];
            // 反馈回路中的一阶低通(阻尼)
            _lowpassState = output * (1.0 - _damping) + _lowpassState * _damping;
            _delayBuffer[_delayIndex] = sample + _feedback * _lowpassState;
            _delayIndex = (_delayIndex + 1) % _delaySamples;
            return output;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delaySamples);
            _lowpassState = 0.0;
            _delayIndex = 0;
        }
    }

    /// <summary>
    /// 移动平均滤波器 - N点滑动平均，简单低通。
    /// y[n] = (1/N) * sum(x[n-k]), k=0..N-1
    /// </summary>
    public class MovingAverageFilter
    {
        private readonly double[] _buffer;
        private int _index;
        private readonly int _length;
        private double _sum;

        public MovingAverageFilter(int length = 8)
        {
            _length = Math.Max(1, length);
            _buffer = new double[_length];
            _index = 0;
            _sum = 0.0;
        }

        public double Process(double sample)
        {
            _sum -= _buffer[_index];
            _buffer[_index] = sample;
            _sum += sample;
            _index = (_index + 1) % _length;
            return _sum / _length;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            Array.Clear(_buffer, 0, _length);
            _sum = 0.0;
            _index = 0;
        }
    }

    // ========================================================================
    // FFT / 频谱分析
    // ========================================================================

    /// <summary>
    /// 窗函数类型
    /// </summary>
    public enum WindowType
    {
        Rectangular,
        Hann,
        Hamming,
        Blackman,
        Kaiser
    }

    /// <summary>
    /// 快速傅里叶变换 - Cooley-Tukey radix-2算法。
    /// 支持2的幂次长度，包含位反转排序与蝶形运算。
    /// </summary>
    public static class FFT
    {
        /// <summary>
        /// 判断n是否为2的幂
        /// </summary>
        public static bool IsPowerOfTwo(int n)
        {
            return n > 0 && (n & (n - 1)) == 0;
        }

        /// <summary>
        /// 计算log2(n)（n为2的幂）
        /// </summary>
        public static int Log2(int n)
        {
            int r = 0;
            while ((n >>= 1) > 0) r++;
            return r;
        }

        /// <summary>
        /// 位反转排序 - 将索引的二进制位反转
        /// </summary>
        private static void BitReverse(double[] real, double[] imag)
        {
            int n = real.Length;
            int bits = Log2(n);
            for (int i = 1; i < n; i++)
            {
                int j = 0;
                int x = i;
                for (int b = 0; b < bits; b++)
                {
                    j = (j << 1) | (x & 1);
                    x >>= 1;
                }
                if (j > i)
                {
                    double tr = real[i]; real[i] = real[j]; real[j] = tr;
                    double ti = imag[i]; imag[i] = imag[j]; imag[j] = ti;
                }
            }
        }

        /// <summary>
        /// 正向FFT（时域→频域）- 迭代Cooley-Tukey蝶形运算
        /// 输入长度必须为2的幂。
        /// </summary>
        public static void Forward(double[] real, double[] imag)
        {
            int n = real.Length;
            if (n <= 1) return;
            if (!IsPowerOfTwo(n))
                throw new ArgumentException("FFT长度必须是2的幂", nameof(real));

            BitReverse(real, imag);

            // 蝶形运算：size=2,4,8,...,n
            for (int size = 2; size <= n; size *= 2)
            {
                int halfSize = size / 2;
                double angle = -2.0 * Math.PI / size;
                double wReal = Math.Cos(angle);
                double wImag = Math.Sin(angle);

                for (int i = 0; i < n; i += size)
                {
                    double curWReal = 1.0;
                    double curWImag = 0.0;
                    for (int j = i; j < i + halfSize; j++)
                    {
                        int k = j + halfSize;
                        double tReal = curWReal * real[k] - curWImag * imag[k];
                        double tImag = curWReal * imag[k] + curWImag * real[k];

                        real[k] = real[j] - tReal;
                        imag[k] = imag[j] - tImag;
                        real[j] += tReal;
                        imag[j] += tImag;

                        // 旋转因子递推: w *= wStep
                        double newWReal = curWReal * wReal - curWImag * wImag;
                        double newWImag = curWReal * wImag + curWImag * wReal;
                        curWReal = newWReal;
                        curWImag = newWImag;
                    }
                }
            }
        }

        /// <summary>
        /// 逆FFT（频域→时域）- 共轭→正向FFT→共轭→归一化
        /// </summary>
        public static void Inverse(double[] real, double[] imag)
        {
            int n = real.Length;
            if (n <= 1) return;
            if (!IsPowerOfTwo(n))
                throw new ArgumentException("FFT长度必须是2的幂", nameof(real));

            // 共轭
            for (int i = 0; i < n; i++)
                imag[i] = -imag[i];

            Forward(real, imag);

            // 共轭并归一化
            double invN = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                real[i] = real[i] * invN;
                imag[i] = -imag[i] * invN;
            }
        }

        /// <summary>
        /// 计算幅度谱 |X[k]| = sqrt(real^2 + imag^2)
        /// </summary>
        public static double[] Magnitude(double[] real, double[] imag)
        {
            int n = real.Length;
            double[] mag = new double[n];
            for (int i = 0; i < n; i++)
                mag[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
            return mag;
        }

        /// <summary>
        /// 计算功率谱 P[k] = |X[k]|^2 / N
        /// </summary>
        public static double[] PowerSpectrum(double[] real, double[] imag)
        {
            int n = real.Length;
            double[] power = new double[n];
            double invN = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                double magSq = real[i] * real[i] + imag[i] * imag[i];
                power[i] = magSq * invN;
            }
            return power;
        }

        /// <summary>
        /// 生成窗函数系数
        /// </summary>
        public static double[] GenerateWindow(int length, WindowType type, double beta = 8.6)
        {
            double[] window = new double[length];
            if (length <= 1)
            {
                window[0] = 1.0;
                return window;
            }
            int N = length - 1;
            for (int i = 0; i < length; i++)
            {
                switch (type)
                {
                    case WindowType.Rectangular:
                        window[i] = 1.0;
                        break;
                    case WindowType.Hann:
                        window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / N));
                        break;
                    case WindowType.Hamming:
                        window[i] = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / N);
                        break;
                    case WindowType.Blackman:
                        window[i] = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * i / N)
                                  + 0.08 * Math.Cos(4.0 * Math.PI * i / N);
                        break;
                    case WindowType.Kaiser:
                        // 修正贝塞尔函数I0的数值近似
                        window[i] = BesselI0(beta * Math.Sqrt(1.0 - Math.Pow(2.0 * i / N - 1.0, 2)))
                                  / BesselI0(beta);
                        break;
                }
            }
            return window;
        }

        /// <summary>
        /// 对缓冲区应用窗函数（逐点相乘）
        /// </summary>
        public static void ApplyWindow(double[] buffer, double[] window)
        {
            int n = Math.Min(buffer.Length, window.Length);
            for (int i = 0; i < n; i++)
                buffer[i] *= window[i];
        }

        public static void ApplyWindow(float[] buffer, double[] window)
        {
            int n = Math.Min(buffer.Length, window.Length);
            for (int i = 0; i < n; i++)
                buffer[i] = (float)(buffer[i] * window[i]);
        }

        /// <summary>
        /// 零阶修正贝塞尔函数 I0(x) 的级数展开
        /// </summary>
        public static double BesselI0(double x)
        {
            double result = 1.0;
            double term = 1.0;
            double xHalfSq = (x * 0.5) * (x * 0.5);
            for (int k = 1; k <= 50; k++)
            {
                term *= xHalfSq / (k * k);
                result += term;
                if (term < 1e-20 * result) break;
            }
            return result;
        }

        /// <summary>
        /// 频率bin转Hz: freq = bin * sampleRate / N
        /// </summary>
        public static double BinToFrequency(int bin, int fftSize, double sampleRate)
        {
            return (double)bin * sampleRate / fftSize;
        }

        /// <summary>
        /// Hz转频率bin: bin = freq * N / sampleRate
        /// </summary>
        public static int FrequencyToBin(double frequency, int fftSize, double sampleRate)
        {
            return (int)Math.Round(frequency * fftSize / sampleRate);
        }
    }

    /// <summary>
    /// 频谱分析器：FFT→幅度→峰值检测→频谱质心/平坦度/滚降点
    /// </summary>
    public class SpectrumAnalyzer
    {
        private readonly double[] _window;
        private readonly WindowType _windowType;
        private readonly int _fftSize;
        private readonly double _sampleRate;

        public int FFTSize => _fftSize;
        public double SampleRate => _sampleRate;

        // 上次分析结果
        public double[] LastMagnitude { get; private set; }
        public double SpectralCentroid { get; private set; }
        public double SpectralFlatness { get; private set; }
        public double SpectralRolloff { get; private set; }
        public List<(double freq, double mag)> Peaks { get; private set; }

        public SpectrumAnalyzer(int fftSize = 1024, double sampleRate = 44100.0,
                                 WindowType windowType = WindowType.Hann)
        {
            if (!FFT.IsPowerOfTwo(fftSize))
                throw new ArgumentException("FFT大小必须是2的幂", nameof(fftSize));
            _fftSize = fftSize;
            _sampleRate = sampleRate;
            _windowType = windowType;
            _window = FFT.GenerateWindow(fftSize, windowType);
            LastMagnitude = new double[fftSize / 2];
            Peaks = new List<(double, double)>();
        }

        /// <summary>
        /// 分析一段音频样本，返回幅度谱
        /// </summary>
        public double[] Analyze(float[] samples, int startOffset = 0)
        {
            double[] real = new double[_fftSize];
            double[] imag = new double[_fftSize];

            int count = Math.Min(_fftSize, samples.Length - startOffset);
            for (int i = 0; i < count; i++)
                real[i] = samples[startOffset + i];
            // 剩余部分补零

            FFT.ApplyWindow(real, _window);
            FFT.Forward(real, imag);

            // 只取前半部分（共轭对称）
            int half = _fftSize / 2;
            double[] mag = new double[half];
            for (int i = 0; i < half; i++)
                mag[i] = 2.0 * Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]) / _fftSize;

            LastMagnitude = mag;
            ComputeDescriptors(mag);
            return mag;
        }

        /// <summary>
        /// 计算频谱描述符：质心、平坦度、滚降点、峰值
        /// </summary>
        private void ComputeDescriptors(double[] mag)
        {
            int n = mag.Length;
            double sumMagFreq = 0.0;
            double sumMag = 0.0;
            double sumLogMag = 0.0;
            double sumPower = 0.0;
            int nonZeroCount = 0;

            for (int i = 0; i < n; i++)
            {
                double freq = FFT.BinToFrequency(i, _fftSize, _sampleRate);
                double m = mag[i];
                double power = m * m;
                sumMagFreq += freq * m;
                sumMag += m;
                sumPower += power;
                if (m > 1e-10)
                {
                    sumLogMag += Math.Log(m);
                    nonZeroCount++;
                }
            }

            // 频谱质心: sum(f*mag)/sum(mag)
            SpectralCentroid = sumMag > 1e-10 ? sumMagFreq / sumMag : 0.0;

            // 频谱平坦度: exp(mean(log(mag))) / mean(mag) = geometric_mean / arithmetic_mean
            if (nonZeroCount > 0 && sumMag > 1e-10)
            {
                double geoMean = Math.Exp(sumLogMag / nonZeroCount);
                double arithMean = sumMag / n;
                SpectralFlatness = arithMean > 1e-10 ? geoMean / arithMean : 0.0;
            }
            else
            {
                SpectralFlatness = 0.0;
            }

            // 频谱滚降点: 85%能量所在的频率
            double threshold = 0.85 * sumPower;
            double cumPower = 0.0;
            SpectralRolloff = 0.0;
            for (int i = 0; i < n; i++)
            {
                cumPower += mag[i] * mag[i];
                if (cumPower >= threshold)
                {
                    SpectralRolloff = FFT.BinToFrequency(i, _fftSize, _sampleRate);
                    break;
                }
            }

            // 峰值检测
            Peaks.Clear();
            for (int i = 2; i < n - 2; i++)
            {
                if (mag[i] > mag[i - 1] && mag[i] > mag[i + 1] && mag[i] > mag[i - 2] && mag[i] > mag[i + 2])
                {
                    double freq = FFT.BinToFrequency(i, _fftSize, _sampleRate);
                    Peaks.Add((freq, mag[i]));
                }
            }
            // 按幅度降序排序，保留前10个峰值
            Peaks.Sort((a, b) => b.mag.CompareTo(a.mag));
            if (Peaks.Count > 10)
                Peaks.RemoveRange(10, Peaks.Count - 10);
        }
    }

    // ========================================================================
    // 动态范围处理
    // ========================================================================

    /// <summary>
    /// 检测模式：峰值或RMS
    /// </summary>
    public enum DetectionMode
    {
        Peak,
        Rms
    }

    /// <summary>
    /// 压缩器 - 阈值/比率/攻击/释放/增益补偿/Knee。
    /// 真实实现：包络跟随器(一阶低通)→增益计算→平滑→应用。
    /// </summary>
    public class Compressor
    {
        private double _threshold;   // dB
        private double _ratio;       // 压缩比 (e.g. 4:1)
        private double _attackTime;  // 秒
        private double _releaseTime; // 秒
        private double _kneeWidth;   // dB (soft knee)
        private double _makeupGain;  // dB
        private DetectionMode _mode;
        private double _sampleRate;

        // 包络跟随器状态
        private double _envelope;
        private double _rmsWindow;
        private readonly double[] _rmsBuffer;
        private int _rmsIndex;
        private readonly int _rmsWindowSize;

        // 增益平滑状态
        private double _currentGainReduction;

        public double Threshold { get => _threshold; set => _threshold = value; }
        public double Ratio { get => _ratio; set => _ratio = Math.Max(1.0, value); }
        public double AttackTime { get => _attackTime; set => _attackTime = Math.Max(0, value); }
        public double ReleaseTime { get => _releaseTime; set => _releaseTime = Math.Max(0.001, value); }
        public double KneeWidth { get => _kneeWidth; set => _kneeWidth = Math.Max(0, value); }
        public double MakeupGain { get => _makeupGain; set => _makeupGain = value; }
        public DetectionMode Mode { get => _mode; set => _mode = value; }
        public double CurrentGainReduction => _currentGainReduction;

        public Compressor(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _threshold = -20.0;
            _ratio = 4.0;
            _attackTime = 0.003;
            _releaseTime = 0.25;
            _kneeWidth = 0.0;
            _makeupGain = 0.0;
            _mode = DetectionMode.Rms;
            _envelope = 0.0;
            _currentGainReduction = 0.0;
            _rmsWindowSize = 64;
            _rmsBuffer = new double[_rmsWindowSize];
            _rmsIndex = 0;
            _rmsWindow = 0.0;
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
        }

        /// <summary>
        /// 根据输入电平计算目标增益衰减(dB)
        /// 使用soft-knee公式
        /// </summary>
        private double ComputeGainReduction(double inputLevelDb)
        {
            double threshold = _threshold;
            double knee = _kneeWidth;
            double ratio = _ratio;

            if (knee > 0 && inputLevelDb > threshold - knee * 0.5 && inputLevelDb < threshold + knee * 0.5)
            {
                // Soft knee区域 - 二次插值
                double x = inputLevelDb - threshold + knee * 0.5;
                double gainReduction = x * x / (2.0 * knee) * (1.0 - 1.0 / ratio);
                return gainReduction;
            }
            else if (inputLevelDb > threshold + knee * 0.5)
            {
                // Hard compression above knee
                double aboveThreshold = inputLevelDb - threshold;
                double gainReduction = aboveThreshold * (1.0 - 1.0 / ratio);
                return gainReduction;
            }
            return 0.0; // Below threshold, no compression
        }

        /// <summary>
        /// 处理单个样本
        /// </summary>
        public double Process(double sample)
        {
            double absSample = Math.Abs(sample);

            // 检测：Peak或RMS
            double detectionLevel;
            if (_mode == DetectionMode.Peak)
            {
                // 峰值包络跟随器
                if (absSample > _envelope)
                    _envelope += (absSample - _envelope) * GetAttackCoefficient();
                else
                    _envelope += (absSample - _envelope) * GetReleaseCoefficient();
                detectionLevel = _envelope;
            }
            else
            {
                // RMS检测 - 滑动窗口
                _rmsWindow -= _rmsBuffer[_rmsIndex];
                double sq = absSample * absSample;
                _rmsBuffer[_rmsIndex] = sq;
                _rmsWindow += sq;
                _rmsIndex = (_rmsIndex + 1) % _rmsWindowSize;
                detectionLevel = Math.Sqrt(_rmsWindow / _rmsWindowSize);
            }

            // 转dB
            double levelDb = detectionLevel > 1e-10 ? 20.0 * Math.Log10(detectionLevel) : -200.0;

            // 计算目标增益衰减
            double targetGainReduction = ComputeGainReduction(levelDb);

            // 平滑增益变化（攻击/释放）
            if (targetGainReduction > _currentGainReduction)
                _currentGainReduction += (targetGainReduction - _currentGainReduction) * GetAttackCoefficient();
            else
                _currentGainReduction += (targetGainReduction - _currentGainReduction) * GetReleaseCoefficient();

            // 应用增益衰减(dB→线性) + 增益补偿
            double totalGainDb = -_currentGainReduction + _makeupGain;
            double gainLin = Math.Pow(10.0, totalGainDb / 20.0);

            return sample * gainLin;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        private double GetAttackCoefficient()
        {
            return 1.0 - Math.Exp(-1.0 / (_attackTime * _sampleRate + 1e-10));
        }

        private double GetReleaseCoefficient()
        {
            return 1.0 - Math.Exp(-1.0 / (_releaseTime * _sampleRate + 1e-10));
        }

        public void Reset()
        {
            _envelope = 0.0;
            _currentGainReduction = 0.0;
            _rmsWindow = 0.0;
            Array.Clear(_rmsBuffer, 0, _rmsWindowSize);
            _rmsIndex = 0;
        }
    }

    /// <summary>
    /// 扩展器 - 与压缩器对称，阈值以下信号被衰减。
    /// 用于去除背景噪声或增加动态范围。
    /// </summary>
    public class Expander
    {
        private double _threshold;
        private double _ratio;       // 扩展比 (e.g. 2:1 means below threshold, output = input/ratio)
        private double _attackTime;
        private double _releaseTime;
        private double _range;       // 最大衰减量(dB)
        private double _sampleRate;
        private double _envelope;
        private double _currentGain;

        public double Threshold { get => _threshold; set => _threshold = value; }
        public double Ratio { get => _ratio; set => _ratio = Math.Max(1.0, value); }
        public double AttackTime { get => _attackTime; set => _attackTime = Math.Max(0, value); }
        public double ReleaseTime { get => _releaseTime; set => _releaseTime = Math.Max(0.001, value); }
        public double Range { get => _range; set => _range = value; }

        public Expander(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _threshold = -40.0;
            _ratio = 2.0;
            _attackTime = 0.01;
            _releaseTime = 0.1;
            _range = 80.0;
            _envelope = 0.0;
            _currentGain = 1.0;
        }

        private double ComputeTargetGain(double inputLevelDb)
        {
            if (inputLevelDb < _threshold)
            {
                // Below threshold: expand
                double belowThreshold = _threshold - inputLevelDb;
                double expansion = belowThreshold * (_ratio - 1.0);
                double gainReduction = Math.Min(expansion, _range);
                return -gainReduction; // dB
            }
            return 0.0;
        }

        public double Process(double sample)
        {
            double absSample = Math.Abs(sample);

            // 峰值包络跟随器
            if (absSample > _envelope)
                _envelope += (absSample - _envelope) * (1.0 - Math.Exp(-1.0 / (_attackTime * _sampleRate + 1e-10)));
            else
                _envelope += (absSample - _envelope) * (1.0 - Math.Exp(-1.0 / (_releaseTime * _sampleRate + 1e-10)));

            double levelDb = _envelope > 1e-10 ? 20.0 * Math.Log10(_envelope) : -200.0;
            double targetGainDb = ComputeTargetGain(levelDb);
            double targetGainLin = Math.Pow(10.0, targetGainDb / 20.0);

            // 平滑
            double coef = targetGainLin < _currentGain
                ? (1.0 - Math.Exp(-1.0 / (_releaseTime * _sampleRate + 1e-10)))
                : (1.0 - Math.Exp(-1.0 / (_attackTime * _sampleRate + 1e-10)));
            _currentGain += (targetGainLin - _currentGain) * coef;

            return sample * _currentGain;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            _envelope = 0.0;
            _currentGain = 1.0;
        }
    }

    /// <summary>
    /// 限制器 - 压缩比无穷大的压缩器，防止信号超过阈值。
    /// 支持硬限制(clip)和软限制(soft clip)以及brickwall。
    /// </summary>
    public class Limiter
    {
        private double _threshold;   // dB
        private double _releaseTime;
        private double _sampleRate;
        private double _envelope;
        private double _currentGain;

        public enum LimitMode { Hard, Soft, Brickwall }

        public double Threshold { get => _threshold; set => _threshold = value; }
        public double ReleaseTime { get => _releaseTime; set => _releaseTime = Math.Max(0.001, value); }
        public LimitMode Mode { get; set; }

        public Limiter(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _threshold = -1.0; // -1dB
            _releaseTime = 0.05;
            _envelope = 0.0;
            _currentGain = 1.0;
            Mode = LimitMode.Soft;
        }

        public double Process(double sample)
        {
            double absSample = Math.Abs(sample);
            double thresholdLin = Math.Pow(10.0, _threshold / 20.0);

            switch (Mode)
            {
                case LimitMode.Hard:
                    // 硬限制 - 直接削波
                    if (sample > thresholdLin) return (float)thresholdLin;
                    if (sample < -thresholdLin) return (float)-thresholdLin;
                    return sample;

                case LimitMode.Brickwall:
                    // Brickwall: 超过阈值的瞬时增益归零
                    if (absSample > thresholdLin)
                    {
                        double gain = thresholdLin / absSample;
                        _currentGain = gain;
                        return sample * gain;
                    }
                    // 平滑恢复
                    double releaseCoef = 1.0 - Math.Exp(-1.0 / (_releaseTime * _sampleRate + 1e-10));
                    _currentGain += (1.0 - _currentGain) * releaseCoef;
                    return sample * _currentGain;

                case LimitMode.Soft:
                default:
                    // 软限制: 峰值检测→增益衰减→平滑
                    if (absSample > _envelope)
                        _envelope = absSample; // 瞬时攻击
                    else
                        _envelope += (absSample - _envelope) * (1.0 - Math.Exp(-1.0 / (_releaseTime * _sampleRate + 1e-10)));

                    double targetGain = 1.0;
                    if (_envelope > thresholdLin)
                        targetGain = thresholdLin / _envelope;

                    // 平滑增益（只往下走快，往上走慢）
                    if (targetGain < _currentGain)
                        _currentGain = targetGain; // 快速衰减
                    else
                        _currentGain += (targetGain - _currentGain) * (1.0 - Math.Exp(-1.0 / (_releaseTime * _sampleRate + 1e-10)));

                    double output = sample * _currentGain;
                    // 软削波(saturation): tanh曲线
                    double knee = 1.0 + _threshold / 20.0; // 软拐点
                    if (knee < 0.1) knee = 0.1;
                    output = thresholdLin * Math.Tanh(output / thresholdLin);
                    return output;
            }
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            _envelope = 0.0;
            _currentGain = 1.0;
        }
    }

    /// <summary>
    /// 噪声门 - 阈值以下信号静音，带攻击/释放/Hold时间。
    /// </summary>
    public class NoiseGate
    {
        private double _threshold;
        private double _attackTime;
        private double _holdTime;
        private double _releaseTime;
        private double _range; // 最大衰减(dB)
        private double _sampleRate;
        private double _envelope;
        private double _currentGain;
        private int _holdCounter;

        public double Threshold { get => _threshold; set => _threshold = value; }
        public double AttackTime { get => _attackTime; set => _attackTime = Math.Max(0, value); }
        public double HoldTime { get => _holdTime; set => _holdTime = Math.Max(0, value); }
        public double ReleaseTime { get => _releaseTime; set => _releaseTime = Math.Max(0.001, value); }
        public double Range { get => _range; set => _range = Math.Max(0, value); }

        public NoiseGate(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _threshold = -50.0;
            _attackTime = 0.001;
            _holdTime = 0.02;
            _releaseTime = 0.05;
            _range = 80.0;
            _envelope = 0.0;
            _currentGain = 0.0;
            _holdCounter = 0;
        }

        public double Process(double sample)
        {
            double absSample = Math.Abs(sample);

            // 包络跟随器（快速攻击）
            if (absSample > _envelope)
                _envelope = absSample;
            else
                _envelope += (absSample - _envelope) * (1.0 - Math.Exp(-1.0 / (0.01 * _sampleRate + 1e-10)));

            double levelDb = _envelope > 1e-10 ? 20.0 * Math.Log10(_envelope) : -200.0;
            double thresholdLin = Math.Pow(10.0, _threshold / 20.0);
            double minGain = Math.Pow(10.0, -_range / 20.0);

            double targetGain;
            if (_envelope >= thresholdLin)
            {
                // 信号超过阈值 - 打开门
                targetGain = 1.0;
                _holdCounter = (int)(_holdTime * _sampleRate);
            }
            else if (_holdCounter > 0)
            {
                // Hold期间保持开启
                targetGain = 1.0;
                _holdCounter--;
            }
            else
            {
                // 关门
                targetGain = minGain;
            }

            // 平滑增益
            double coef = targetGain > _currentGain
                ? (1.0 - Math.Exp(-1.0 / (_attackTime * _sampleRate + 1e-10)))
                : (1.0 - Math.Exp(-1.0 / (_releaseTime * _sampleRate + 1e-10)));
            _currentGain += (targetGain - _currentGain) * coef;

            return sample * _currentGain;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            _envelope = 0.0;
            _currentGain = 0.0;
            _holdCounter = 0;
        }
    }

    /// <summary>
    /// 瞬态设计器 - 独立于阈值，直接调整攻击和释放特性。
    /// 基于快慢两个包络跟随器的差值来检测瞬态。
    /// </summary>
    public class TransientDesigner
    {
        private double _sampleRate;
        private double _attackGain;  // 攻击增强量 (dB, 正=增强, 负=减弱)
        private double _releaseGain; // 释放增强量 (dB)
        private double _fastEnvelope;
        private double _slowEnvelope;
        private double _currentGain;

        // 快慢包络时间常数
        private readonly double _fastTimeConstant;
        private readonly double _slowTimeConstant;

        public double AttackGain { get => _attackGain; set => _attackGain = value; }
        public double ReleaseGain { get => _releaseGain; set => _releaseGain = value; }

        public TransientDesigner(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _attackGain = 3.0;  // +3dB attack
            _releaseGain = 0.0;
            _fastEnvelope = 0.0;
            _slowEnvelope = 0.0;
            _currentGain = 1.0;
            _fastTimeConstant = 0.003;  // 3ms 快速包络
            _slowTimeConstant = 0.05;   // 50ms 慢速包络
        }

        public double Process(double sample)
        {
            double absSample = Math.Abs(sample);

            // 快速包络跟随器（攻击包络）
            double fastCoef = 1.0 - Math.Exp(-1.0 / (_fastTimeConstant * _sampleRate + 1e-10));
            if (absSample > _fastEnvelope)
                _fastEnvelope += (absSample - _fastEnvelope) * fastCoef;
            else
                _fastEnvelope += (absSample - _fastEnvelope) * fastCoef;

            // 慢速包络跟随器（持续包络）
            double slowCoef = 1.0 - Math.Exp(-1.0 / (_slowTimeConstant * _sampleRate + 1e-10));
            _slowEnvelope += (absSample - _slowEnvelope) * slowCoef;

            // 瞬态检测：快包络 > 慢包络 = 攻击阶段
            double transientRatio = 1.0;
            if (_slowEnvelope > 1e-10)
            {
                transientRatio = _fastEnvelope / _slowEnvelope;
            }

            // 计算目标增益
            double targetGainDb;
            if (transientRatio > 1.0)
            {
                // 攻击阶段
                double excess = 20.0 * Math.Log10(transientRatio);
                targetGainDb = excess * (_attackGain / 6.0); // 归一化
            }
            else
            {
                // 释放阶段
                double deficit = 20.0 * Math.Log10(transientRatio);
                targetGainDb = deficit * (_releaseGain / 6.0);
            }

            double targetGainLin = Math.Pow(10.0, targetGainDb / 20.0);

            // 平滑增益
            double smoothCoef = 1.0 - Math.Exp(-1.0 / (_fastTimeConstant * _sampleRate + 1e-10));
            _currentGain += (targetGainLin - _currentGain) * smoothCoef;

            return sample * _currentGain;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            _fastEnvelope = 0.0;
            _slowEnvelope = 0.0;
            _currentGain = 1.0;
        }
    }
}
