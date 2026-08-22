using System;
using System.Collections.Generic;

namespace ShanyuEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 音频效果处理系统 (Audio Effect System)
    //
    // 效果基类、均衡器、滤波器、压缩器、限制器、混响、延迟、
    // 合唱、镶边、相位器、失真、变调、效果链 (串联/并联/发送/返回)。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEffect — 效果基类
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频效果基类。</summary>
    public abstract class AudioEffect : IDisposable
    {
        /// <summary>效果名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否旁路 (bypass)。</summary>
        public bool Bypassed { get; set; }

        /// <summary>Wet/Dry 混合比 [0=全干, 1=全湿]。</summary>
        public float WetMix { get; set; } = 1f;

        /// <summary>效果处理 (原地修改 samples)。</summary>
        public abstract void Process(float[] samples, int sampleRate);

        /// <summary>重置效果内部状态。</summary>
        public virtual void Reset() { }

        /// <summary>设置参数 (按名称)。</summary>
        public virtual void SetParameter(string name, float value) { }

        /// <summary>获取参数 (按名称)。</summary>
        public virtual float GetParameter(string name) => 0f;

        public void Dispose()
        {
            OnDispose();
        }

        protected virtual void OnDispose() { }

        /// <summary>混合 Wet/Dry 信号。</summary>
        protected void MixWetDry(float[] dry, float[] wet)
        {
            for (int i = 0; i < dry.Length && i < wet.Length; i++)
                dry[i] = dry[i] * (1f - WetMix) + wet[i] * WetMix;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  滤波器类型
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>滤波器类型。</summary>
    public enum FilterType
    {
        LowPass,
        HighPass,
        BandPass,
        Notch,
        LowShelf,
        HighShelf,
        Peaking,
        AllPass,
        Comb,
        BandReject
    }

    /// <summary>滤波器品质 (Q值) 模式。</summary>
    public enum QMode
    {
        Manual,
        ConstantQ,
        BandwidthInOctaves
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioFilter — 滤波器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>双二阶滤波器 (Biquad Filter)。</summary>
    public sealed class AudioFilter : AudioEffect
    {
        /// <summary>滤波器类型。</summary>
        public FilterType Type { get; set; } = FilterType.LowPass;

        /// <summary>截止频率 (Hz)。</summary>
        public float CutoffFrequency { get; set; } = 1000f;

        /// <summary>品质因数 Q。</summary>
        public float Q { get; set; } = 0.707f;

        /// <summary>增益 (dB, 用于 Shelf/Peak)。</summary>
        public float GainDb { get; set; }

        // 双二阶系数
        private float _b0, _b1, _b2, _a1, _a2;
        // 状态变量
        private float _x1, _x2, _y1, _y2;
        private bool _coefficientsDirty = true;

        public AudioFilter() { Name = "Filter"; }

        public AudioFilter(FilterType type, float cutoff, float q = 0.707f, float gainDb = 0f)
        {
            Name = "Filter";
            Type = type;
            CutoffFrequency = cutoff;
            Q = q;
            GainDb = gainDb;
        }

        private void CalculateCoefficients()
        {
            float w0 = 2f * MathF.PI * CutoffFrequency;
            float cosw0 = MathF.Cos(w0);
            float sinw0 = MathF.Sin(w0);
            float alpha = sinw0 / (2f * Q);

            float A = MathF.Pow(10f, GainDb / 40f);

            switch (Type)
            {
                case FilterType.LowPass:
                    _b0 = (1f - cosw0) / 2f;
                    _b1 = 1f - cosw0;
                    _b2 = (1f - cosw0) / 2f;
                    _a0 = 1f + alpha;
                    _a1 = -2f * cosw0;
                    _a2 = 1f - alpha;
                    break;
                case FilterType.HighPass:
                    _b0 = (1f + cosw0) / 2f;
                    _b1 = -(1f + cosw0);
                    _b2 = (1f + cosw0) / 2f;
                    _a0 = 1f + alpha;
                    _a1 = -2f * cosw0;
                    _a2 = 1f - alpha;
                    break;
                case FilterType.BandPass:
                    _b0 = alpha;
                    _b1 = 0f;
                    _b2 = -alpha;
                    _a0 = 1f + alpha;
                    _a1 = -2f * cosw0;
                    _a2 = 1f - alpha;
                    break;
                case FilterType.Notch:
                    _b0 = 1f;
                    _b1 = -2f * cosw0;
                    _b2 = 1f;
                    _a0 = 1f + alpha;
                    _a1 = -2f * cosw0;
                    _a2 = 1f - alpha;
                    break;
                case FilterType.Peaking:
                    _b0 = 1f + alpha * A;
                    _b1 = -2f * cosw0;
                    _b2 = 1f - alpha * A;
                    _a0 = 1f + alpha / A;
                    _a1 = -2f * cosw0;
                    _a2 = 1f - alpha / A;
                    break;
                default:
                    _b0 = 1f; _b1 = 0f; _b2 = 0f;
                    _a0 = 1f; _a1 = 0f; _a2 = 0f;
                    break;
            }

            // 归一化
            _b0 /= _a0; _b1 /= _a0; _b2 /= _a0;
            _a1 /= _a0; _a2 /= _a0;
            _coefficientsDirty = false;
        }

        private float _a0; // 用于归一化

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;
            if (_coefficientsDirty) CalculateCoefficients();

            for (int i = 0; i < samples.Length; i++)
            {
                float x0 = samples[i];
                float y0 = _b0 * x0 + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x0;
                _y2 = _y1; _y1 = y0;
                samples[i] = y0;
            }
        }

        public override void Reset()
        {
            _x1 = _x2 = _y1 = _y2 = 0f;
            _coefficientsDirty = true;
        }

        public override void SetParameter(string name, float value)
        {
            switch (name.ToLower())
            {
                case "cutoff": case "frequency": CutoffFrequency = value; _coefficientsDirty = true; break;
                case "q": Q = Math.Max(0.001f, value); _coefficientsDirty = true; break;
                case "gain": GainDb = value; _coefficientsDirty = true; break;
            }
        }

        public override float GetParameter(string name) => name.ToLower() switch
        {
            "cutoff" or "frequency" => CutoffFrequency,
            "q" => Q,
            "gain" => GainDb,
            _ => 0f
        };
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEQ — 均衡器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>均衡器频段。</summary>
    public sealed class EQBand
    {
        /// <summary>频段名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>中心频率 (Hz)。</summary>
        public float Frequency { get; set; } = 1000f;

        /// <summary>增益 (dB)。</summary>
        public float GainDb { get; set; }

        /// <summary>Q 值。</summary>
        public float Q { get; set; } = 1f;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>底层滤波器。</summary>
        public AudioFilter Filter { get; } = new();

        public void UpdateFilter()
        {
            Filter.Type = FilterType.Peaking;
            Filter.CutoffFrequency = Frequency;
            Filter.GainDb = GainDb;
            Filter.Q = Q;
            Filter.Reset();
        }
    }

    /// <summary>参数均衡器 (Parametric EQ)。</summary>
    public sealed class AudioEQ : AudioEffect
    {
        /// <summary>均衡器频段列表。</summary>
        public List<EQBand> Bands { get; } = new();

        public AudioEQ()
        {
            Name = "ParametricEQ";
            // 默认 5 频段
            float[] defaultFreqs = { 60f, 250f, 1000f, 4000f, 16000f };
            foreach (var freq in defaultFreqs)
            {
                var band = new EQBand { Name = $"{freq}Hz", Frequency = freq };
                band.UpdateFilter();
                Bands.Add(band);
            }
        }

        public EQBand? GetBand(int index) => index >= 0 && index < Bands.Count ? Bands[index] : null;

        public void AddBand(float freq, float gain = 0f, float q = 1f)
        {
            var band = new EQBand { Frequency = freq, GainDb = gain, Q = q };
            band.UpdateFilter();
            Bands.Add(band);
        }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;
            foreach (var band in Bands)
            {
                if (band.Enabled)
                    band.Filter.Process(samples, sampleRate);
            }
        }

        public override void Reset()
        {
            foreach (var band in Bands) band.Filter.Reset();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioCompressor — 压缩器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>动态范围压缩器。</summary>
    public sealed class AudioCompressor : AudioEffect
    {
        /// <summary>阈值 (dB)。</summary>
        public float ThresholdDb { get; set; } = -20f;

        /// <summary>压缩比 (如 4:1)。</summary>
        public float Ratio { get; set; } = 4f;

        /// <summary>启动时间 (秒)。</summary>
        public float AttackTime { get; set; } = 0.01f;

        /// <summary>释放时间 (秒)。</summary>
        public float ReleaseTime { get; set; } = 0.1f;

        /// <summary>拐点 (dB)。</summary>
        public float KneeDb { get; set; } = 6f;

        /// <summary> makeup 增益 (dB)。</summary>
        public float MakeupGainDb { get; set; }

        /// <summary>侧链输入 (null = 自侧链)。</summary>
        public float[]? SidechainInput { get; set; }

        private float _envelope = 0f;

        public AudioCompressor() { Name = "Compressor"; }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;

            float makeupGain = MathF.Pow(10f, MakeupGainDb / 20f);
            float attackCoeff = (float)Math.Exp(-1f / (Math.Max(0.0001f, AttackTime) * sampleRate));
            float releaseCoeff = (float)Math.Exp(-1f / (Math.Max(0.0001f, ReleaseTime) * sampleRate));
            float threshold = MathF.Pow(10f, ThresholdDb / 20f);

            for (int i = 0; i < samples.Length; i++)
            {
                float input = SidechainInput != null && i < SidechainInput.Length
                    ? SidechainInput[i] : samples[i];
                float absInput = MathF.Abs(input);

                // 包络跟随
                if (absInput > _envelope)
                    _envelope = attackCoeff * (_envelope - absInput) + absInput;
                else
                    _envelope = releaseCoeff * (_envelope - absInput) + absInput;

                // 增益计算
                float gainDb = 0f;
                if (_envelope > threshold)
                {
                    float overDb = 20f * MathF.Log10(_envelope / threshold);
                    gainDb = overDb * (1f - 1f / Ratio);
                }

                // 软拐点
                if (KneeDb > 0f)
                {
                    float halfKnee = KneeDb / 2f;
                    float inputDb = _envelope > 0.0001f ? 20f * MathF.Log10(_envelope) : -96f;
                    float kneeRange = inputDb - (ThresholdDb - halfKnee);
                    if (kneeRange > 0 && kneeRange < KneeDb)
                    {
                        float t = kneeRange / KneeDb;
                        gainDb *= t * t * (3f - 2f * t);
                    }
                }

                float gain = MathF.Pow(10f, (-gainDb + MakeupGainDb) / 20f);
                samples[i] *= gain;
            }
        }

        public override void Reset() { _envelope = 0f; }

        public override void SetParameter(string name, float value)
        {
            switch (name.ToLower())
            {
                case "threshold": ThresholdDb = value; break;
                case "ratio": Ratio = Math.Max(1f, value); break;
                case "attack": AttackTime = Math.Max(0.0001f, value); break;
                case "release": ReleaseTime = Math.Max(0.0001f, value); break;
                case "knee": KneeDb = value; break;
                case "makeupgain": MakeupGainDb = value; break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioLimiter — 限制器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>砖墙限制器。</summary>
    public sealed class AudioLimiter : AudioEffect
    {
        /// <summary>天花板 (dB)。</summary>
        public float CeilingDb { get; set; } = -0.3f;

        /// <summary>释放时间 (秒)。</summary>
        public float ReleaseTime { get; set; } = 0.05f;

        /// <summary>输入增益 (dB)。</summary>
        public float InputGainDb { get; set; }

        private float _envelope = 0f;

        public AudioLimiter() { Name = "Limiter"; }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;

            float ceiling = MathF.Pow(10f, CeilingDb / 20f);
            float inputGain = MathF.Pow(10f, InputGainDb / 20f);
            float releaseCoeff = (float)Math.Exp(-1f / (Math.Max(0.0001f, ReleaseTime) * sampleRate));

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= inputGain;
                float abs = MathF.Abs(samples[i]);

                if (abs > _envelope)
                    _envelope = abs;
                else
                    _envelope = releaseCoeff * (_envelope - abs) + abs;

                if (_envelope > ceiling)
                {
                    float gain = ceiling / _envelope;
                    samples[i] *= gain;
                }
            }
        }

        public override void Reset() { _envelope = 0f; }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioReverb (算法混响) — Schroeder/FDN
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>混响类型。</summary>
    public enum ReverbType
    {
        Schroeder,
        FDN,
        Simple
    }

    /// <summary>算法混响处理器。</summary>
    public sealed class AudioReverb : AudioEffect
    {
        /// <summary>混响类型。</summary>
        public ReverbType ReverbAlgorithm { get; set; } = ReverbType.FDN;

        /// <summary>衰减时间 (秒)。</summary>
        public float DecayTime { get; set; } = 1.5f;

        /// <summary>预延迟 (秒)。</summary>
        public float PreDelay { get; set; } = 0.02f;

        /// <summary>扩散 (0-1)。</summary>
        public float Diffusion { get; set; } = 0.7f;

        /// <summary>密度 (0-1)。</summary>
        public float Density { get; set; } = 0.8f;

        /// <summary>房间大小 (0-1)。</summary>
        public float RoomSize { get; set; } = 0.5f;

        /// <summary>低频增益 (0-1)。</summary>
        public float LowGain { get; set; }

        /// <summary>高频衰减 (0-1)。</summary>
        public float HighCut { get; set; } = 0.8f;

        /// <summary>混响增益 (dB)。</summary>
        public float ReverbGainDb { get; set; } = -6f;

        // 延迟线 (FDN)
        private float[][] _delayLines = Array.Empty<float[]>();
        private int[] _delayPositions = Array.Empty<int>();
        private float[] _feedback = Array.Empty<float>();
        private float[] _filterState = Array.Empty<float>();
        private int _preDelaySamples;
        private float[]? _preDelayBuffer;
        private int _preDelayPos;

        private const int FDN_SIZE = 8;
        private int _lastSampleRate;

        public AudioReverb() { Name = "Reverb"; }

        private void Initialize(int sampleRate)
        {
            if (sampleRate == _lastSampleRate && _delayLines.Length > 0) return;
            _lastSampleRate = sampleRate;

            // FDN 延迟线长度 (素数延迟)
            int[] delayLengths = { 1557, 1617, 1491, 1422, 1277, 1356, 1188, 1301 };
            _delayLines = new float[FDN_SIZE][];
            _delayPositions = new int[FDN_SIZE];
            _feedback = new float[FDN_SIZE];
            _filterState = new float[FDN_SIZE];

            float decayFactor = DecayTime / 2f;
            for (int i = 0; i < FDN_SIZE; i++)
            {
                _delayLines[i] = new float[delayLengths[i]];
                _delayPositions[i] = 0;
                _feedback[i] = MathF.Pow(10f, -3f * delayLengths[i] / (sampleRate * decayFactor));
                _filterState[i] = 0f;
            }

            // 预延迟
            _preDelaySamples = Math.Max(0, (int)(PreDelay * sampleRate));
            _preDelayBuffer = _preDelaySamples > 0 ? new float[_preDelaySamples] : null;
            _preDelayPos = 0;
        }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;
            Initialize(sampleRate);

            float gain = MathF.Pow(10f, ReverbGainDb / 20f);
            float hfDamp = 1f - HighCut * 0.5f;

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                // 预延迟
                if (_preDelayBuffer != null)
                {
                    float pdSample = _preDelayBuffer[_preDelayPos];
                    _preDelayBuffer[_preDelayPos] = input;
                    _preDelayPos = (_preDelayPos + 1) % _preDelaySamples;
                    input = pdSample;
                }

                // FDN 处理
                float output = 0f;
                float inputMix = input;

                for (int j = 0; j < FDN_SIZE; j++)
                {
                    int pos = _delayPositions[j];
                    float delayed = _delayLines[j][pos];

                    // 一阶低通 (高频衰减)
                    _filterState[j] = hfDamp * _filterState[j] + (1f - hfDamp) * delayed;

                    // 输出混合
                    output += delayed;

                    // 延迟线写入 (输入 + 反馈)
                    _delayLines[j][pos] = inputMix + _filterState[j] * _feedback[j];

                    // 扩散矩阵 (Hadamard 简化)
                    _delayPositions[j] = (pos + 1) % _delayLines[j].Length;
                }

                // Wet/Dry 混合
                samples[i] = samples[i] * (1f - WetMix) + output * gain * WetMix / FDN_SIZE;
            }
        }

        public override void Reset()
        {
            _lastSampleRate = 0;
            _delayLines = Array.Empty<float[]>();
            _delayPositions = Array.Empty<int>();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioDelay — 延迟效果
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>延迟效果处理器。</summary>
    public sealed class AudioDelay : AudioEffect
    {
        /// <summary>延迟时间 (秒)。</summary>
        public float DelayTime { get; set; } = 0.3f;

        /// <summary>反馈量 (0-1)。</summary>
        public float Feedback { get; set; } = 0.3f;

        /// <summary>混响量 (发送到混响的比例)。</summary>
        public float ReverbSend { get; set; }

        /// <summary>乒乓模式 (左右交替)。</summary>
        public bool PingPong { get; set; }

        /// <summary>低通滤波截止频率 (0 = 禁用)。</summary>
        public float LowPassFrequency { get; set; }

        private float[] _bufferL = Array.Empty<float>();
        private float[] _bufferR = Array.Empty<float>();
        private int _posL, _posR;
        private float _feedbackFilterL, _feedbackFilterR;
        private int _lastSampleRate;

        public AudioDelay() { Name = "Delay"; }

        private void Initialize(int sampleRate)
        {
            if (sampleRate == _lastSampleRate && _bufferL.Length > 0) return;
            _lastSampleRate = sampleRate;
            int delaySamples = Math.Max(1, (int)(DelayTime * sampleRate));
            _bufferL = new float[delaySamples];
            _bufferR = new float[delaySamples];
            _posL = _posR = 0;
            _feedbackFilterL = _feedbackFilterR = 0f;
        }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;
            Initialize(sampleRate);

            float lpCoeff = LowPassFrequency > 0
                ? (float)Math.Exp(-2f * Math.PI * LowPassFrequency / sampleRate) : 1f;

            for (int i = 0; i < samples.Length; i += 2)
            {
                // 左声道
                float dl = _bufferL[_posL];
                _feedbackFilterL = lpCoeff * _feedbackFilterL + (1f - lpCoeff) * dl;
                _bufferL[_posL] = samples[i] + _feedbackFilterL * Feedback;

                // 右声道
                float dr = PingPong ? _bufferL[_posL] : _bufferR[_posR];
                _feedbackFilterR = lpCoeff * _feedbackFilterR + (1f - lpCoeff) * dr;
                if (!PingPong)
                    _bufferR[_posR] = samples[i + 1] + _feedbackFilterR * Feedback;

                // 输出混合
                if (i < samples.Length)
                    samples[i] = samples[i] * (1f - WetMix) + dl * WetMix;
                if (i + 1 < samples.Length)
                    samples[i + 1] = samples[i + 1] * (1f - WetMix) + dr * WetMix;

                _posL = (_posL + 1) % _bufferL.Length;
                _posR = (_posR + 1) % _bufferR.Length;
            }
        }

        public override void Reset() { _lastSampleRate = 0; _bufferL = Array.Empty<float>(); _bufferR = Array.Empty<float>(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioChorus — 合唱
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>合唱效果。</summary>
    public sealed class AudioChorus : AudioEffect
    {
        /// <summary>基础延迟 (秒)。</summary>
        public float BaseDelay { get; set; } = 0.005f;

        /// <summary>LFO 深度 (延迟调制范围, 秒)。</summary>
        public float Depth { get; set; } = 0.002f;

        /// <summary>LFO 速率 (Hz)。</summary>
        public float Rate { get; set; } = 1.5f;

        /// <summary>声部数。</summary>
        public int Voices { get; set; } = 3;

        private float[][] _buffers = Array.Empty<float[]>();
        private float[] _lfoPhases = Array.Empty<float>();
        private int _bufferSize;
        private int _lastSampleRate;

        public AudioChorus() { Name = "Chorus"; }

        private void Initialize(int sampleRate)
        {
            if (sampleRate == _lastSampleRate && _buffers.Length > 0) return;
            _lastSampleRate = sampleRate;
            _bufferSize = (int)((BaseDelay + Depth) * sampleRate * 2) + 1024;
            _buffers = new float[Voices][];
            _lfoPhases = new float[Voices];
            for (int v = 0; v < Voices; v++)
            {
                _buffers[v] = new float[_bufferSize];
                _lfoPhases[v] = (float)v / Voices * 2f * MathF.PI;
            }
        }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;
            Initialize(sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float output = 0f;
                for (int v = 0; v < Voices; v++)
                {
                    // LFO
                    float phase = _lfoPhases[v];
                    float lfo = MathF.Sin(phase) * 0.5f + 0.5f;
                    _lfoPhases[v] += 2f * MathF.PI * Rate / sampleRate;
                    if (_lfoPhases[v] > 2f * MathF.PI) _lfoPhases[v] -= 2f * MathF.PI;

                    // 可变延迟读取
                    float delaySamples = (BaseDelay + lfo * Depth) * sampleRate;
                    int readPos = (int)(_bufferSize - delaySamples) % _bufferSize;
                    if (readPos < 0) readPos += _bufferSize;

                    // 写入延迟线
                    _buffers[v][i % _bufferSize] = samples[i];
                    output += _buffers[v][readPos];
                }

                samples[i] = samples[i] * (1f - WetMix) + (output / Voices) * WetMix;
            }
        }

        public override void Reset() { _lastSampleRate = 0; _buffers = Array.Empty<float[]>(); _lfoPhases = Array.Empty<float>(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioFlanger — 镶边
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>镶边效果。</summary>
    public sealed class AudioFlanger : AudioEffect
    {
        /// <summary>基础延迟 (秒)。</summary>
        public float BaseDelay { get; set; } = 0.003f;

        /// <summary>LFO 深度 (秒)。</summary>
        public float Depth { get; set; } = 0.003f;

        /// <summary>LFO 速率 (Hz)。</summary>
        public float Rate { get; set; } = 0.5f;

        /// <summary>反馈量 (0-1)。</summary>
        public float Feedback { get; set; } = 0.3f;

        private float[] _buffer = Array.Empty<float>();
        private int _writePos;
        private float _lfoPhase;
        private int _bufferSize;
        private int _lastSampleRate;

        public AudioFlanger() { Name = "Flanger"; }

        private void Initialize(int sampleRate)
        {
            if (sampleRate == _lastSampleRate && _buffer.Length > 0) return;
            _lastSampleRate = sampleRate;
            _bufferSize = (int)((BaseDelay + Depth) * sampleRate) + 1024;
            _buffer = new float[_bufferSize];
            _writePos = 0;
            _lfoPhase = 0f;
        }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;
            Initialize(sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                _lfoPhase += 2f * MathF.PI * Rate / sampleRate;
                if (_lfoPhase > 2f * MathF.PI) _lfoPhase -= 2f * MathF.PI;
                float lfo = MathF.Sin(_lfoPhase) * 0.5f + 0.5f;

                float delaySamples = (BaseDelay + lfo * Depth) * sampleRate;
                int readPos = (_writePos - (int)delaySamples + _bufferSize) % _bufferSize;

                float delayed = _buffer[readPos];
                _buffer[_writePos] = samples[i] + delayed * Feedback;
                _writePos = (_writePos + 1) % _bufferSize;

                samples[i] = samples[i] * (1f - WetMix) + delayed * WetMix;
            }
        }

        public override void Reset() { _lastSampleRate = 0; _buffer = Array.Empty<float>(); _lfoPhase = 0f; }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioPhaser — 相位器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>相位器效果。</summary>
    public sealed class AudioPhaser : AudioEffect
    {
        /// <summary>LFO 速率 (Hz)。</summary>
        public float Rate { get; set; } = 0.5f;

        /// <summary>深度 (0-1)。</summary>
        public float Depth { get; set; } = 0.5f;

        /// <summary>频率 (Hz, 全通滤波中心频率)。</summary>
        public float Frequency { get; set; } = 1000f;

        /// <summary>全通级数。</summary>
        public int Stages { get; set; } = 4;

        private float[] _allPassState = Array.Empty<float>();
        private float _lfoPhase;
        private int _lastSampleRate;

        public AudioPhaser() { Name = "Phaser"; }

        private void Initialize(int sampleRate)
        {
            if (sampleRate == _lastSampleRate && _allPassState.Length > 0) return;
            _lastSampleRate = sampleRate;
            _allPassState = new float[Stages];
            _lfoPhase = 0f;
        }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;
            Initialize(sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                _lfoPhase += 2f * MathF.PI * Rate / sampleRate;
                if (_lfoPhase > 2f * MathF.PI) _lfoPhase -= 2f * MathF.PI;

                float modFreq = Frequency * (1f + Depth * MathF.Sin(_lfoPhase));
                float normFreq = modFreq / sampleRate;

                float input = samples[i];
                float output = input;

                for (int s = 0; s < Stages; s++)
                {
                    float g = MathF.Tan(MathF.PI * normFreq);
                    float apOut = -g * input + _allPassState[s];
                    _allPassState[s] = input + g * apOut;
                    input = apOut;
                }

                samples[i] = samples[i] * (1f - WetMix) + output * WetMix;
            }
        }

        public override void Reset() { _lastSampleRate = 0; _allPassState = Array.Empty<float>(); _lfoPhase = 0f; }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioDistortion — 失真
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>失真类型。</summary>
    public enum DistortionType
    {
        HardClip,
        SoftClip,
        Tube,
        Fuzz,
        BitCrusher,
        Foldback
    }

    /// <summary>失真效果。</summary>
    public sealed class AudioDistortion : AudioEffect
    {
        /// <summary>失真类型。</summary>
        public DistortionType DistType { get; set; } = DistortionType.SoftClip;

        /// <summary>驱动 (增益倍数)。</summary>
        public float Drive { get; set; } = 1f;

        /// <summary>音调偏移 (模拟)。</summary>
        public float Tone { get; set; } = 0f;

        /// <summary>干/湿混合。</summary>
        public float Mix { get; set; } = 0.5f;

        /// <summary>位深度降低 (仅 BitCrusher)。</summary>
        public int BitDepth { get; set; } = 8;

        public AudioDistortion() { Name = "Distortion"; }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled) return;

            for (int i = 0; i < samples.Length; i++)
            {
                float x = samples[i] * Drive;
                float distorted = 0f;

                switch (DistType)
                {
                    case DistortionType.HardClip:
                        distorted = Math.Clamp(x, -1f, 1f);
                        break;
                    case DistortionType.SoftClip:
                        distorted = (float)Math.Tanh(x * 2f) / 2f;
                        break;
                    case DistortionType.Tube:
                        distorted = (float)(1.5f * MathF.Sign(x) * (1f - MathF.Exp(-MathF.Abs(x) * 1.5f)));
                        break;
                    case DistortionType.Fuzz:
                        distorted = MathF.Sign(x) * (1f - (float)Math.Exp(-x * x * 0.5f));
                        break;
                    case DistortionType.BitCrusher:
                        float levels = MathF.Pow(2f, BitDepth);
                        distorted = MathF.Round(x * levels) / levels;
                        break;
                    case DistortionType.Foldback:
                        if (x > 1f) distorted = 2f - x;
                        else if (x < -1f) distorted = -2f - x;
                        else distorted = x;
                        break;
                }

                samples[i] = samples[i] * (1f - WetMix) + distorted * WetMix;
            }
        }

        public override void Reset() { }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioPitchShifter — 变调
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>变调效果 (简化相位声码器)。</summary>
    public sealed class AudioPitchShifter : AudioEffect
    {
        /// <summary>音调偏移 (半音, 正值升调, 负值降调)。</summary>
        public float PitchShiftSemitones { get; set; }

        /// <summary>音调比率 (2.0 = 高一个八度, 0.5 = 低一个八度)。</summary>
        public float PitchRatio
        {
            get => MathF.Pow(2f, PitchShiftSemitones / 12f);
            set => PitchShiftSemitones = 12f * MathF.Log2(Math.Max(0.01f, value));
        }

        private float[] _grainBuffer = Array.Empty<float>();
        private int _grainPos;
        private int _grainSize;
        private int _lastSampleRate;

        public AudioPitchShifter() { Name = "PitchShifter"; }

        public override void Process(float[] samples, int sampleRate)
        {
            if (Bypassed || !Enabled || PitchRatio == 1f) return;

            int grainSize = Math.Max(64, sampleRate / 40); // ~40 grains per second
            if (grainSize != _grainSize || sampleRate != _lastSampleRate)
            {
                _grainSize = grainSize;
                _grainBuffer = new float[grainSize * 2];
                _grainPos = 0;
                _lastSampleRate = sampleRate;
            }

            float ratio = PitchRatio;
            for (int i = 0; i < samples.Length; i++)
            {
                // 简化变调: 通过改变采样率模拟
                float phase = (i * ratio) % samples.Length;
                int idx0 = (int)phase;
                int idx1 = Math.Min(idx0 + 1, samples.Length - 1);
                float frac = phase - idx0;

                _grainBuffer[_grainPos] = samples[idx0] * (1f - frac) + samples[idx1] * frac;
                samples[i] = samples[i] * (1f - WetMix) + _grainBuffer[_grainPos] * WetMix;
                _grainPos = (_grainPos + 1) % _grainBuffer.Length;
            }
        }

        public override void Reset() { _lastSampleRate = 0; _grainBuffer = Array.Empty<float>(); _grainPos = 0; }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEffectChain — 效果链
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>效果链连接模式。</summary>
    public enum EffectChainMode
    {
        /// <summary>串联 (逐个处理)。</summary>
        Series,
        /// <summary>并联 (并行处理后混合)。</summary>
        Parallel,
        /// <summary>发送/返回 (干信号直接通过，湿信号经过效果后混合)。</summary>
        SendReturn
    }

    /// <summary>效果链。</summary>
    public sealed class AudioEffectChain : IDisposable
    {
        /// <summary>链名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>连接模式。</summary>
        public EffectChainMode Mode { get; set; } = EffectChainMode.Series;

        /// <summary>效果列表。</summary>
        public List<AudioEffect> Effects { get; } = new();

        /// <summary>发送量 (仅 SendReturn 模式)。</summary>
        public float SendAmount { get; set; } = 1f;

        /// <summary>返回量 (仅 SendReturn 模式)。</summary>
        public float ReturnAmount { get; set; } = 1f;

        /// <summary>是否启用整个链。</summary>
        public bool Enabled { get; set; } = true;

        public AudioEffectChain() { }

        public AudioEffectChain(string name) { Name = name; }

        /// <summary>添加效果。</summary>
        public void AddEffect(AudioEffect effect)
        {
            Effects.Add(effect);
        }

        /// <summary>移除效果。</summary>
        public bool RemoveEffect(AudioEffect effect)
        {
            return Effects.Remove(effect);
        }

        /// <summary>处理效果链。</summary>
        public void Process(float[] samples, int sampleRate)
        {
            if (!Enabled || Effects.Count == 0) return;

            switch (Mode)
            {
                case EffectChainMode.Series:
                    ProcessSeries(samples, sampleRate);
                    break;

                case EffectChainMode.Parallel:
                    ProcessParallel(samples, sampleRate);
                    break;

                case EffectChainMode.SendReturn:
                    ProcessSendReturn(samples, sampleRate);
                    break;
            }
        }

        private void ProcessSeries(float[] samples, int sampleRate)
        {
            foreach (var effect in Effects)
                effect.Process(samples, sampleRate);
        }

        private void ProcessParallel(float[] samples, int sampleRate)
        {
            float[] dry = (float[])samples.Clone();
            float[] wet = new float[samples.Length];

            foreach (var effect in Effects)
            {
                float[] effectInput = (float[])dry.Clone();
                effect.Process(effectInput, sampleRate);
                for (int i = 0; i < samples.Length; i++)
                    wet[i] += effectInput[i];
            }

            for (int i = 0; i < samples.Length; i++)
                samples[i] = dry[i] * (1f - SendAmount) + wet[i] * SendAmount / Effects.Count;
        }

        private void ProcessSendReturn(float[] samples, int sampleRate)
        {
            float[] sendBuffer = new float[samples.Length];
            Array.Copy(samples, sendBuffer, samples.Length);

            foreach (var effect in Effects)
                effect.Process(sendBuffer, sampleRate);

            for (int i = 0; i < samples.Length; i++)
                samples[i] = samples[i] + sendBuffer[i] * ReturnAmount;
        }

        /// <summary>重置所有效果。</summary>
        public void Reset()
        {
            foreach (var effect in Effects)
                effect.Reset();
        }

        public void Dispose()
        {
            foreach (var effect in Effects)
                effect.Dispose();
            Effects.Clear();
        }
    }
}
