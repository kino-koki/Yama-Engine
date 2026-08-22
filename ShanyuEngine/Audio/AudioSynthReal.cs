// ============================================================================
// ShanyuEngine AudioSynthReal.cs
// 山雨引擎 - 真实音频合成器实现
// 包含: 振荡器(Sine/Square/Saw/Triangle/Noise/Pulse/SuperSaw/粉红噪声)、
//       ADSR包络(状态机)、LFO、VCF(Biquad+Moog梯形)、SynthVoice、
//       Synthesizer(声部管理/stealing)、GranularSynth(颗粒合成)。
// ============================================================================

using System;
using System.Collections.Generic;

namespace ShanyuEngine.Audio
{
    // ========================================================================
    // 振荡器
    // ========================================================================

    /// <summary>
    /// 振荡器波形类型
    /// </summary>
    public enum OscillatorType
    {
        Sine,
        Square,
        Saw,
        Triangle,
        Noise,
        Pulse,
        SuperSaw,
        PinkNoise
    }

    /// <summary>
    /// 振荡器 - 生成各种波形。
    /// 真实波形生成:
    /// - Sine: sin(2*pi*phase)
    /// - Square: sign(sin(2*pi*phase))
    /// - Saw: 2*(phase - floor(phase+0.5))
    /// - Triangle: 2*abs(2*(phase-floor(phase+0.5)))-1
    /// - Pulse: 占空比可调方波
    /// - SuperSaw: 多个失谐锯齿波叠加
    /// - Noise: 白噪声(Random)
    /// - PinkNoise: 粉红噪声(Voss-McCartney算法)
    /// </summary>
    public sealed class Oscillator
    {
        private double _sampleRate;
        private double _frequency;
        private double _phase;
        private double _phaseIncrement;
        private OscillatorType _type;
        private double _pulseWidth; // 占空比(0~1)
        private readonly Random _random;
        private double _lastWhiteNoise;

        // SuperSaw: 7个失谐锯齿波
        private readonly double[] _superSawPhases;
        private readonly double[] _superSawDetunes;

        // 粉红噪声: Voss-McCartney算法
        private readonly double[] _pinkNoiseRows;
        private int _pinkNoiseCounter;
        private double _pinkNoiseAccum;

        public OscillatorType Type { get => _type; set => _type = value; }
        public double Frequency
        {
            get => _frequency;
            set
            {
                _frequency = value;
                UpdatePhaseIncrement();
            }
        }
        public double Phase { get => _phase; set => _phase = value; }
        public double PulseWidth { get => _pulseWidth; set => _pulseWidth = Math.Max(0.01, Math.Min(0.99, value)); }
        public double SampleRate { get => _sampleRate; }

        public Oscillator(double sampleRate = 44100.0, OscillatorType type = OscillatorType.Sine,
                          double frequency = 440.0)
        {
            _sampleRate = sampleRate;
            _type = type;
            _frequency = frequency;
            _phase = 0.0;
            _pulseWidth = 0.5;
            _random = new Random();

            // SuperSaw初始化: 7个锯齿波, 中心+左右各3个失谐
            _superSawPhases = new double[7];
            _superSawDetunes = new double[7];
            // 失谐量(cents): 中心0, 左右递增
            double[] detuneCents = { -22, -11, -5, 0, 5, 11, 22 };
            for (int i = 0; i < 7; i++)
            {
                _superSawPhases[i] = _random.NextDouble();
                _superSawDetunes[i] = Math.Pow(2.0, detuneCents[i] / 1200.0);
            }

            // 粉红噪声初始化
            _pinkNoiseRows = new double[16]; // 16行Voss-McCartney
            _pinkNoiseCounter = 0;
            _pinkNoiseAccum = 0;

            UpdatePhaseIncrement();
        }

        private void UpdatePhaseIncrement()
        {
            _phaseIncrement = _frequency / _sampleRate;
            // 限制到Nyquist以内
            if (_phaseIncrement > 0.5) _phaseIncrement = 0.5;
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            UpdatePhaseIncrement();
        }

        /// <summary>
        /// 生成下一个样本
        /// </summary>
        public double Process()
        {
            double output = 0.0;

            switch (_type)
            {
                case OscillatorType.Sine:
                    output = Math.Sin(2.0 * Math.PI * _phase);
                    break;

                case OscillatorType.Square:
                    // 方波: sign(sin(2*pi*phase)) 等价于 phase < 0.5 ? 1 : -1
                    output = _phase < 0.5 ? 1.0 : -1.0;
                    break;

                case OscillatorType.Saw:
                    // 锯齿波: 2*(phase - floor(phase+0.5))
                    output = 2.0 * (_phase - Math.Floor(_phase + 0.5));
                    break;

                case OscillatorType.Triangle:
                    // 三角波: 2*abs(2*(phase-floor(phase+0.5)))-1
                    {
                        double t = _phase - Math.Floor(_phase + 0.5);
                        output = 2.0 * Math.Abs(2.0 * t) - 1.0;
                    }
                    break;

                case OscillatorType.Pulse:
                    // 脉冲波: 占空比可调的方波
                    output = _phase < _pulseWidth ? 1.0 : -1.0;
                    break;

                case OscillatorType.Noise:
                    // 白噪声
                    output = _random.NextDouble() * 2.0 - 1.0;
                    break;

                case OscillatorType.SuperSaw:
                    // SuperSaw: 7个失谐锯齿波叠加
                    {
                        double sum = 0;
                        for (int i = 0; i < 7; i++)
                        {
                            double detunedFreq = _frequency * _superSawDetunes[i];
                            double inc = detunedFreq / _sampleRate;
                            if (inc > 0.5) inc = 0.5;
                            double saw = 2.0 * (_superSawPhases[i] - Math.Floor(_superSawPhases[i] + 0.5));
                            sum += saw;
                            _superSawPhases[i] += inc;
                            if (_superSawPhases[i] >= 1.0) _superSawPhases[i] -= 1.0;
                        }
                        output = sum / 7.0;
                    }
                    break;

                case OscillatorType.PinkNoise:
                    // 粉红噪声: Voss-McCartney算法
                    // 每个样本更新部分行(基于counter的二进制位)
                    {
                        int updateBits = _pinkNoiseCounter | 0; // counter决定更新哪些行
                        // 找到最低位的0, 更新该行
                        int row = 0;
                        int temp = _pinkNoiseCounter;
                        while ((temp & 1) != 0 && row < _pinkNoiseRows.Length - 1)
                        {
                            temp >>= 1;
                            row++;
                        }
                        double oldVal = _pinkNoiseRows[row];
                        double newVal = _random.NextDouble() * 2.0 - 1.0;
                        _pinkNoiseRows[row] = newVal;
                        _pinkNoiseAccum += newVal - oldVal;
                        _pinkNoiseCounter++;
                        // 归一化(粉红噪声约为白噪声的1/sqrt(N))
                        output = _pinkNoiseAccum / Math.Sqrt(_pinkNoiseRows.Length);
                    }
                    break;
            }

            // 推进相位
            _phase += _phaseIncrement;
            if (_phase >= 1.0)
                _phase -= Math.Floor(_phase); // 保持相位在[0,1)

            return output;
        }

        /// <summary>
        /// 批量生成
        /// </summary>
        public void Process(float[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[i] = (float)Process();
        }

        public void Reset()
        {
            _phase = 0.0;
            for (int i = 0; i < _superSawPhases.Length; i++)
                _superSawPhases[i] = 0.0;
            Array.Clear(_pinkNoiseRows, 0, _pinkNoiseRows.Length);
            _pinkNoiseCounter = 0;
            _pinkNoiseAccum = 0;
        }

        /// <summary>
        /// 设置相位(0~1)
        /// </summary>
        public void SetPhase(double phase)
        {
            _phase = phase - Math.Floor(phase);
        }
    }

    // ========================================================================
    // ADSR 包络
    // ========================================================================

    /// <summary>
    /// ADSR包络状态
    /// </summary>
    public enum EnvelopeState
    {
        Idle,
        Attack,
        Decay,
        Sustain,
        Release
    }

    /// <summary>
    /// ADSR包络 - Attack/Decay/Sustain/Release。
    /// 状态机: Idle→Attack→Decay→Sustain→Release→Idle。
    /// 支持线性和指数曲线。
    /// </summary>
    public sealed class ADSR_Envelope
    {
        private double _sampleRate;
        private double _attackTime;   // 秒
        private double _decayTime;    // 秒
        private double _sustainLevel; // 0~1
        private double _releaseTime;  // 秒
        private EnvelopeState _state;
        private double _currentValue;
        private double _targetValue;
        private bool _linearMode; // true=线性, false=指数
        private double _stateTime;
        private double _stateDuration;
        private double _startValue;

        public double AttackTime { get => _attackTime; set => _attackTime = Math.Max(0.0001, value); }
        public double DecayTime { get => _decayTime; set => _decayTime = Math.Max(0.0001, value); }
        public double SustainLevel { get => _sustainLevel; set => _sustainLevel = Math.Max(0, Math.Min(1, value)); }
        public double ReleaseTime { get => _releaseTime; set => _releaseTime = Math.Max(0.0001, value); }
        public EnvelopeState State => _state;
        public double CurrentValue => _currentValue;
        public bool IsActive => _state != EnvelopeState.Idle;
        public bool IsReleasing => _state == EnvelopeState.Release;
        public bool LinearMode { get => _linearMode; set => _linearMode = value; }

        public ADSR_Envelope(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _attackTime = 0.01;
            _decayTime = 0.1;
            _sustainLevel = 0.7;
            _releaseTime = 0.2;
            _state = EnvelopeState.Idle;
            _currentValue = 0.0;
            _targetValue = 0.0;
            _linearMode = false;
            _stateTime = 0.0;
            _stateDuration = 0.0;
            _startValue = 0.0;
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
        }

        /// <summary>
        /// 触发音符(NoteOn) - 开始Attack阶段
        /// </summary>
        public void NoteOn()
        {
            _state = EnvelopeState.Attack;
            _startValue = _currentValue;
            _targetValue = 1.0;
            _stateTime = 0.0;
            _stateDuration = _attackTime;
        }

        /// <summary>
        /// 释放音符(NoteOff) - 开始Release阶段
        /// </summary>
        public void NoteOff()
        {
            _state = EnvelopeState.Release;
            _startValue = _currentValue;
            _targetValue = 0.0;
            _stateTime = 0.0;
            _stateDuration = _releaseTime;
        }

        /// <summary>
        /// 强制停止
        /// </summary>
        public void ForceStop()
        {
            _state = EnvelopeState.Idle;
            _currentValue = 0.0;
        }

        /// <summary>
        /// 处理一个时间步, 返回当前包络值
        /// </summary>
        public double Process(double dt)
        {
            if (_state == EnvelopeState.Idle)
            {
                _currentValue = 0.0;
                return 0.0;
            }

            _stateTime += dt;
            double progress = _stateDuration > 0 ? _stateTime / _stateDuration : 1.0;
            progress = Math.Min(1.0, progress);

            if (_linearMode)
            {
                // 线性插值
                _currentValue = _startValue + (_targetValue - _startValue) * progress;
            }
            else
            {
                // 指数曲线(对数感知, 更自然)
                // 使用指数衰减: y = start + (target-start) * (1 - exp(-3*progress))
                // 或: y = target + (start-target) * exp(-3*progress)
                if (_targetValue > _startValue)
                {
                    // 上升(Attack)
                    _currentValue = _startValue + (_targetValue - _startValue) * (1.0 - Math.Exp(-3.0 * progress));
                }
                else
                {
                    // 下降(Decay/Release)
                    _currentValue = _targetValue + (_startValue - _targetValue) * Math.Exp(-3.0 * progress);
                }
            }

            // 状态转换
            if (progress >= 1.0)
            {
                switch (_state)
                {
                    case EnvelopeState.Attack:
                        _state = EnvelopeState.Decay;
                        _startValue = 1.0;
                        _targetValue = _sustainLevel;
                        _stateTime = 0.0;
                        _stateDuration = _decayTime;
                        break;

                    case EnvelopeState.Decay:
                        _state = EnvelopeState.Sustain;
                        _currentValue = _sustainLevel;
                        break;

                    case EnvelopeState.Release:
                        _state = EnvelopeState.Idle;
                        _currentValue = 0.0;
                        break;
                }
            }

            // Sustain阶段保持
            if (_state == EnvelopeState.Sustain)
                _currentValue = _sustainLevel;

            return _currentValue;
        }

        /// <summary>
        /// 批量处理
        /// </summary>
        public void Process(float[] buffer, int count)
        {
            double dt = 1.0 / _sampleRate;
            for (int i = 0; i < count; i++)
                buffer[i] = (float)Process(dt);
        }

        public void Reset()
        {
            _state = EnvelopeState.Idle;
            _currentValue = 0.0;
            _stateTime = 0.0;
        }
    }

    // ========================================================================
    // LFO (低频振荡器)
    // ========================================================================

    /// <summary>
    /// LFO波形类型
    /// </summary>
    public enum LFOWaveform
    {
        Sine,
        Triangle,
        Square,
        Sawtooth,
        RandomStep
    }

    /// <summary>
    /// LFO - 低频振荡器, 用于调制其他参数。
    /// 支持Sine/Triangle/Square/Sawtooth/RandomStep, 频率/深度/相位偏移/延迟启动。
    /// </summary>
    public sealed class LFO
    {
        private double _sampleRate;
        private double _frequency;   // Hz (通常 < 20Hz)
        private double _depth;       // 调制深度(0~1)
        private double _phase;       // 相位(0~1)
        private double _phaseOffset; // 相位偏移(0~1)
        private double _delayTime;   // 延迟启动(秒)
        private LFOWaveform _waveform;
        private double _elapsedTime;
        private readonly Random _random;
        private double _randomValue;
        private double _randomTarget;
        private double _randomPhase;

        public LFOWaveform Waveform { get => _waveform; set => _waveform = value; }
        public double Frequency { get => _frequency; set => _frequency = value; }
        public double Depth { get => _depth; set => _depth = Math.Max(0, Math.Min(1, value)); }
        public double PhaseOffset { get => _phaseOffset; set => _phaseOffset = value; }
        public double DelayTime { get => _delayTime; set => _delayTime = Math.Max(0, value); }

        public LFO(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _frequency = 5.0; // 5Hz默认
            _depth = 1.0;
            _phase = 0.0;
            _phaseOffset = 0.0;
            _delayTime = 0.0;
            _waveform = LFOWaveform.Sine;
            _elapsedTime = 0.0;
            _random = new Random();
            _randomValue = 0.0;
            _randomTarget = 0.0;
            _randomPhase = 0.0;
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
        }

        /// <summary>
        /// 处理一个样本, 返回LFO输出值(-1~1, 再乘以depth)
        /// </summary>
        public double Process()
        {
            double dt = 1.0 / _sampleRate;
            _elapsedTime += dt;

            // 延迟启动
            if (_elapsedTime < _delayTime)
                return 0.0;

            double output = 0.0;
            double phase = _phase + _phaseOffset;
            phase -= Math.Floor(phase); // 保持[0,1)

            switch (_waveform)
            {
                case LFOWaveform.Sine:
                    output = Math.Sin(2.0 * Math.PI * phase);
                    break;

                case LFOWaveform.Triangle:
                    output = 2.0 * Math.Abs(2.0 * (phase - Math.Floor(phase + 0.5))) - 1.0;
                    break;

                case LFOWaveform.Square:
                    output = phase < 0.5 ? 1.0 : -1.0;
                    break;

                case LFOWaveform.Sawtooth:
                    output = 2.0 * (phase - Math.Floor(phase + 0.5));
                    break;

                case LFOWaveform.RandomStep:
                    // 随机步进: 每个LFO周期切换一个随机值, 平滑过渡
                    _randomPhase += _frequency * dt;
                    if (_randomPhase >= 1.0)
                    {
                        _randomPhase -= 1.0;
                        _randomTarget = _random.NextDouble() * 2.0 - 1.0;
                    }
                    // 平滑插值到目标
                    _randomValue += (_randomTarget - _randomValue) * (1.0 - Math.Exp(-3.0 * dt * _frequency));
                    output = _randomValue;
                    break;
            }

            // 推进相位
            _phase += _frequency * dt;
            if (_phase >= 1.0)
                _phase -= Math.Floor(_phase);

            return output * _depth;
        }

        /// <summary>
        /// 批量处理
        /// </summary>
        public void Process(float[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[i] = (float)Process();
        }

        public void Reset()
        {
            _phase = 0.0;
            _elapsedTime = 0.0;
            _randomValue = 0.0;
            _randomTarget = 0.0;
            _randomPhase = 0.0;
        }
    }

    // ========================================================================
    // VCF (压控滤波器)
    // ========================================================================

    /// <summary>
    /// VCF - 压控滤波器。截止频率可由LFO/包络调制。
    /// 支持Biquad模式(BiquadType LowPass/HighPass)和Moog风格梯形滤波器(简化版)。
    /// </summary>
    public sealed class VCF
    {
        public enum FilterMode { Biquad, MoogLadder }

        private double _sampleRate;
        private double _baseCutoff;     // 基础截止频率(Hz)
        private double _resonance;      // 共振(0~1)
        private double _cutoffModAmount; // 截止频率调制量(0~1, 调制深度)
        private double _envModAmount;    // 包络调制量
        private FilterMode _mode;
        private BiquadFilter _biquad;
        private BiquadType _biquadType;

        // Moog梯形滤波器状态(4级串联一阶低通+全局反馈)
        private double _moogStage1, _moogStage2, _moogStage3, _moogStage4;
        private double _moogDelay1, _moogDelay2, _moogDelay3, _moogDelay4;

        public double BaseCutoff { get => _baseCutoff; set => _baseCutoff = value; }
        public double Resonance { get => _resonance; set => _resonance = Math.Max(0, Math.Min(1, value)); }
        public double CutoffModAmount { get => _cutoffModAmount; set => _cutoffModAmount = Math.Max(0, Math.Min(1, value)); }
        public double EnvModAmount { get => _envModAmount; set => _envModAmount = Math.Max(-1, Math.Min(1, value)); }
        public FilterMode Mode { get => _mode; set => _mode = value; }
        public BiquadType BiquadFilterType { get => _biquadType; set => _biquadType = value; }

        public VCF(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _baseCutoff = 1000.0;
            _resonance = 0.1;
            _cutoffModAmount = 0.0;
            _envModAmount = 0.0;
            _mode = FilterMode.Biquad;
            _biquadType = BiquadType.LowPass;
            _biquad = new BiquadFilter(sampleRate);
            _biquad.SetParameters(BiquadType.LowPass, _baseCutoff, 0.7 + _resonance * 10);
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            _biquad.SetSampleRate(sampleRate);
        }

        /// <summary>
        /// 处理单个样本。
        /// lfoValue: LFO调制值(-1~1)
        /// envValue: 包络调制值(0~1)
        /// </summary>
        public double Process(double input, double lfoValue = 0, double envValue = 0)
        {
            // 计算调制后的截止频率
            double modCutoff = _baseCutoff;
            // LFO调制: 截止频率上下波动
            modCutoff *= Math.Pow(2.0, lfoValue * _cutoffModAmount * 2.0); // ±2 octaves
            // 包络调制: 截止频率随包络变化
            modCutoff *= Math.Pow(2.0, envValue * _envModAmount * 4.0); // 0~4 octaves

            // 限制到有效范围
            double nyquist = _sampleRate * 0.5;
            if (modCutoff > nyquist * 0.99) modCutoff = nyquist * 0.99;
            if (modCutoff < 20.0) modCutoff = 20.0;

            if (_mode == FilterMode.Biquad)
            {
                // 更新Biquad系数
                double q = 0.7 + _resonance * 20.0; // 共振Q
                _biquad.SetParameters(_biquadType, modCutoff, q);
                return _biquad.Process(input);
            }
            else
            {
                return ProcessMoog(input, modCutoff);
            }
        }

        /// <summary>
        /// Moog梯形滤波器(简化版) - 4级串联一阶低通 + 共振反馈。
        /// 经典模拟合成器滤波器模型。
        /// </summary>
        private double ProcessMoog(double input, double cutoff)
        {
            // 计算滤波器系数
            double fc = cutoff / _sampleRate;
            // 限制
            if (fc > 0.99) fc = 0.99;
            if (fc < 0.01) fc = 0.01;

            // Moog系数 (简化版, 使用tanh饱和限制)
            double x = Math.PI * fc;
            double g = (1.0 - Math.Exp(-x)) ; // 一阶低通增益

            // 共振反馈量 (0~4, 超过4会自激振荡)
            double feedback = _resonance * 4.0;

            // 输入减去共振反馈(通过tanh软限幅)
            double u = input - feedback * _moogStage4;
            u = Math.Tanh(u); // 软饱和防止自激失控

            // 4级串联一阶低通
            _moogStage1 = _moogStage1 + g * (u - _moogStage1);
            _moogStage2 = _moogStage2 + g * (_moogStage1 - _moogStage2);
            _moogStage3 = _moogStage3 + g * (_moogStage2 - _moogStage3);
            _moogStage4 = _moogStage4 + g * (_moogStage3 - _moogStage4);

            return _moogStage4;
        }

        public void ProcessBuffer(float[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            _biquad.Reset();
            _moogStage1 = _moogStage2 = _moogStage3 = _moogStage4 = 0;
            _moogDelay1 = _moogDelay2 = _moogDelay3 = _moogDelay4 = 0;
        }
    }

    // ========================================================================
    // 合成器声部
    // ========================================================================

    /// <summary>
    /// 合成器声部 - Oscillator + ADSR + VCF + LFO。
    /// 处理单个音符的完整信号链。
    /// </summary>
    public sealed class SynthVoice
    {
        public Oscillator Osc { get; set; }
        public ADSR_Envelope Envelope { get; set; }
        public VCF Filter { get; set; }
        public LFO ModLFO { get; set; }
        public int NoteNumber { get; private set; }
        public double Velocity { get; private set; }
        public bool IsActive { get; private set; }
        public double Detune { get; set; } // 失谐(cents)

        private double _sampleRate;

        public SynthVoice(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            Osc = new Oscillator(sampleRate, OscillatorType.Saw, 440.0);
            Envelope = new ADSR_Envelope(sampleRate);
            Filter = new VCF(sampleRate);
            ModLFO = new LFO(sampleRate);
            NoteNumber = -1;
            Velocity = 0;
            IsActive = false;
            Detune = 0;
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            Osc.SetSampleRate(sampleRate);
            Envelope.SetSampleRate(sampleRate);
            Filter.SetSampleRate(sampleRate);
            ModLFO.SetSampleRate(sampleRate);
        }

        /// <summary>
        /// MIDI音符频率: freq = 440 * 2^((note-69)/12)
        /// </summary>
        public static double NoteToFrequency(int noteNumber, double detuneCents = 0)
        {
            return 440.0 * Math.Pow(2.0, (noteNumber - 69 + detuneCents / 100.0) / 12.0);
        }

        /// <summary>
        /// 触发音符
        /// </summary>
        public void NoteOn(int noteNumber, double velocity = 1.0)
        {
            NoteNumber = noteNumber;
            Velocity = velocity;
            IsActive = true;
            Osc.Frequency = NoteToFrequency(noteNumber, Detune);
            Envelope.NoteOn();
        }

        /// <summary>
        /// 释放音符
        /// </summary>
        public void NoteOff()
        {
            Envelope.NoteOff();
        }

        /// <summary>
        /// 处理一个样本
        /// </summary>
        public double Process()
        {
            if (!IsActive) return 0.0;

            double dt = 1.0 / _sampleRate;

            // ADSR包络
            double env = Envelope.Process(dt);
            if (Envelope.State == EnvelopeState.Idle)
            {
                IsActive = false;
                return 0.0;
            }

            // LFO调制
            double lfo = ModLFO.Process();

            // 振荡器
            double oscOut = Osc.Process();

            // VCF滤波 (LFO调制截止频率, 包络调制截止频率)
            double filtered = Filter.Process(oscOut, lfo, env);

            // 应用包络和力度
            return filtered * env * Velocity;
        }

        /// <summary>
        /// 批量处理
        /// </summary>
        public void Process(float[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (!IsActive)
                {
                    buffer[i] = 0;
                    continue;
                }
                buffer[i] = (float)Process();
            }
        }

        public void Reset()
        {
            Osc.Reset();
            Envelope.Reset();
            Filter.Reset();
            ModLFO.Reset();
            NoteNumber = -1;
            IsActive = false;
        }
    }

    // ========================================================================
    // 合成器
    // ========================================================================

    /// <summary>
    /// 音色设置
    /// </summary>
    public sealed class SynthPatch
    {
        public string Name { get; set; }
        public OscillatorType OscType { get; set; }
        public double Attack { get; set; }
        public double Decay { get; set; }
        public double Sustain { get; set; }
        public double Release { get; set; }
        public double Cutoff { get; set; }
        public double Resonance { get; set; }
        public double CutoffModAmount { get; set; }
        public double EnvModAmount { get; set; }
        public double LfoFrequency { get; set; }
        public double LfoDepth { get; set; }
        public double Detune { get; set; }

        public SynthPatch()
        {
            Name = "Default";
            OscType = OscillatorType.Saw;
            Attack = 0.01;
            Decay = 0.1;
            Sustain = 0.7;
            Release = 0.2;
            Cutoff = 2000.0;
            Resonance = 0.1;
            CutoffModAmount = 0.0;
            EnvModAmount = 0.3;
            LfoFrequency = 5.0;
            LfoDepth = 0.0;
            Detune = 0;
        }
    }

    /// <summary>
    /// 合成器 - 声部管理/音符分配/最大声部数/声部stealing/音色库。
    /// </summary>
    public sealed class Synthesizer
    {
        private readonly List<SynthVoice> _voices;
        private readonly int _maxVoices;
        private double _sampleRate;
        private SynthPatch _currentPatch;
        private readonly Dictionary<string, SynthPatch> _patchLibrary;

        public int MaxVoices => _maxVoices;
        public int ActiveVoiceCount
        {
            get
            {
                int count = 0;
                foreach (var v in _voices) if (v.IsActive) count++;
                return count;
            }
        }
        public SynthPatch CurrentPatch { get => _currentPatch; set => ApplyPatch(value); }
        public IReadOnlyDictionary<string, SynthPatch> Patches => _patchLibrary;

        public Synthesizer(int maxVoices = 16, double sampleRate = 44100.0)
        {
            _maxVoices = maxVoices;
            _sampleRate = sampleRate;
            _voices = new List<SynthVoice>();
            _patchLibrary = new Dictionary<string, SynthPatch>();

            // 创建声部池
            for (int i = 0; i < maxVoices; i++)
            {
                _voices.Add(new SynthVoice(sampleRate));
            }

            _currentPatch = new SynthPatch();
            ApplyPatch(_currentPatch);

            // 预设音色
            RegisterPresetPatches();
        }

        private void RegisterPresetPatches()
        {
            _patchLibrary["Lead"] = new SynthPatch
            {
                Name = "Lead",
                OscType = OscillatorType.Saw,
                Attack = 0.01, Decay = 0.1, Sustain = 0.8, Release = 0.15,
                Cutoff = 3000, Resonance = 0.2, EnvModAmount = 0.4
            };
            _patchLibrary["Pad"] = new SynthPatch
            {
                Name = "Pad",
                OscType = OscillatorType.SuperSaw,
                Attack = 0.5, Decay = 0.3, Sustain = 0.7, Release = 1.0,
                Cutoff = 1500, Resonance = 0.05, EnvModAmount = 0.2, LfoDepth = 0.3
            };
            _patchLibrary["Bass"] = new SynthPatch
            {
                Name = "Bass",
                OscType = OscillatorType.Square,
                Attack = 0.005, Decay = 0.2, Sustain = 0.5, Release = 0.1,
                Cutoff = 500, Resonance = 0.3, EnvModAmount = 0.5
            };
            _patchLibrary["Pluck"] = new SynthPatch
            {
                Name = "Pluck",
                OscType = OscillatorType.Triangle,
                Attack = 0.001, Decay = 0.3, Sustain = 0.0, Release = 0.2,
                Cutoff = 2000, Resonance = 0.1, EnvModAmount = 0.6
            };
        }

        /// <summary>
        /// 应用音色到所有声部
        /// </summary>
        public void ApplyPatch(SynthPatch patch)
        {
            _currentPatch = patch;
            foreach (var voice in _voices)
            {
                voice.Osc.Type = patch.OscType;
                voice.Osc.Detune = patch.Detune;
                voice.Envelope.AttackTime = patch.Attack;
                voice.Envelope.DecayTime = patch.Decay;
                voice.Envelope.SustainLevel = patch.Sustain;
                voice.Envelope.ReleaseTime = patch.Release;
                voice.Filter.BaseCutoff = patch.Cutoff;
                voice.Filter.Resonance = patch.Resonance;
                voice.Filter.CutoffModAmount = patch.CutoffModAmount;
                voice.Filter.EnvModAmount = patch.EnvModAmount;
                voice.ModLFO.Frequency = patch.LfoFrequency;
                voice.ModLFO.Depth = patch.LfoDepth;
                voice.Detune = patch.Detune;
            }
        }

        public void LoadPatch(string name)
        {
            if (_patchLibrary.TryGetValue(name, out var patch))
                ApplyPatch(patch);
        }

        /// <summary>
        /// NoteOn - 分配音符到空闲声部, 如果没有空闲声部则steal最老的声部
        /// </summary>
        public void NoteOn(int noteNumber, double velocity = 1.0)
        {
            // 查找空闲声部
            SynthVoice targetVoice = null;

            // 优先查找正在播放相同音符的声部(重触发)
            foreach (var v in _voices)
            {
                if (v.NoteNumber == noteNumber && v.IsActive)
                {
                    targetVoice = v;
                    break;
                }
            }

            // 查找空闲声部
            if (targetVoice == null)
            {
                foreach (var v in _voices)
                {
                    if (!v.IsActive)
                    {
                        targetVoice = v;
                        break;
                    }
                }
            }

            // 声部stealing: 找到最长时间未触发的声部(或处于Release状态的)
            if (targetVoice == null)
            {
                // 优先steal处于Release状态的声部
                foreach (var v in _voices)
                {
                    if (v.Envelope.IsReleasing)
                    {
                        targetVoice = v;
                        break;
                    }
                }

                // 如果没有Release中的, steal第一个声部
                if (targetVoice == null)
                    targetVoice = _voices[0];
            }

            targetVoice?.NoteOn(noteNumber, velocity);
        }

        /// <summary>
        /// NoteOff - 释放指定音符
        /// </summary>
        public void NoteOff(int noteNumber)
        {
            foreach (var v in _voices)
            {
                if (v.NoteNumber == noteNumber && v.IsActive && !v.Envelope.IsReleasing)
                {
                    v.NoteOff();
                }
            }
        }

        /// <summary>
        /// 全部NoteOff
        /// </summary>
        public void AllNotesOff()
        {
            foreach (var v in _voices)
                if (v.IsActive) v.NoteOff();
        }

        /// <summary>
        /// 处理所有声部, 混合输出
        /// </summary>
        public double Process()
        {
            double sum = 0.0;
            foreach (var voice in _voices)
            {
                if (voice.IsActive)
                    sum += voice.Process();
            }

            // 软限幅防止削波
            return Math.Tanh(sum * 0.5);
        }

        /// <summary>
        /// 批量处理(立体声)
        /// </summary>
        public void Process(float[] outputLeft, float[] outputRight, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double sample = Process();
                outputLeft[i] = (float)sample;
                outputRight[i] = (float)sample;
            }
        }

        public void Process(float[] output, int count)
        {
            for (int i = 0; i < count; i++)
                output[i] = (float)Process();
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            foreach (var v in _voices)
                v.SetSampleRate(sampleRate);
        }

        public void Reset()
        {
            foreach (var v in _voices)
                v.Reset();
        }
    }

    // ========================================================================
    // 颗粒合成
    // ========================================================================

    /// <summary>
    /// 颗粒 - 一个短音频片段
    /// </summary>
    public sealed class Grain
    {
        public float[] Samples { get; set; }
        public int Position { get; set; }      // 在源音频中的起始位置
        public int Length { get; set; }        // 颗粒长度(样本数)
        public double Pitch { get; set; }      // 音高倍率(1.0=原始)
        public double Pan { get; set; }        // 声像(-1~1)
        public double Volume { get; set; }     // 音量(0~1)
        public double[] Window { get; set; }   // 窗函数
        public int ReadPosition { get; set; }  // 当前读取位置(定点)
        public bool IsActive { get; set; }

        public Grain()
        {
            Volume = 1.0;
            Pitch = 1.0;
            Pan = 0.0;
            IsActive = false;
        }
    }

    /// <summary>
    /// 颗粒合成 - 将音频分解为颗粒, 通过参数化重叠/窗函数/位置随机化/速度调制生成新音频。
    /// </summary>
    public sealed class GranularSynth
    {
        private float[] _sourceAudio;
        private int _sourceSampleRate;
        private int _sourceChannels;
        private readonly List<Grain> _activeGrains;
        private readonly int _maxGrains;

        // 颗粒参数
        private double _grainSizeMs;       // 颗粒大小(毫秒)
        private double _grainRateHz;      // 颗粒生成频率(Hz)
        private double _pitch;            // 音高倍率
        private double _pitchRandom;      // 音高随机量
        private double _positionRandom;   // 位置随机量(0~1, 相对于颗粒大小)
        private double _panRandom;        // 声像随机量(0~1)
        private double _overlap;          // 重叠度(0~1)
        private WindowType _windowType;

        private double _grainPhase;       // 颗粒生成相位
        private double _playbackPosition; // 播放位置(样本)
        private double _playbackSpeed;    // 播放速度倍率

        public double GrainSizeMs { get => _grainSizeMs; set => _grainSizeMs = Math.Max(1, value); }
        public double GrainRateHz { get => _grainRateHz; set => _grainRateHz = Math.Max(0.1, value); }
        public double Pitch { get => _pitch; set => _pitch = value; }
        public double PitchRandom { get => _pitchRandom; set => _pitchRandom = Math.Max(0, Math.Min(1, value)); }
        public double PositionRandom { get => _positionRandom; set => _positionRandom = Math.Max(0, Math.Min(1, value)); }
        public double PanRandom { get => _panRandom; set => _panRandom = Math.Max(0, Math.Min(1, value)); }
        public double Overlap { get => _overlap; set => _overlap = Math.Max(0, Math.Min(1, value)); }
        public WindowType GrainWindowType { get => _windowType; set => _windowType = value; }
        public double PlaybackSpeed { get => _playbackSpeed; set => _playbackSpeed = value; }
        public int MaxGrains => _maxGrains;
        public int ActiveGrainCount => _activeGrains.Count;

        public GranularSynth(int maxGrains = 64, int sampleRate = 44100)
        {
            _maxGrains = maxGrains;
            _activeGrains = new List<Grain>();
            _grainSizeMs = 50.0;
            _grainRateHz = 20.0;
            _pitch = 1.0;
            _pitchRandom = 0.0;
            _positionRandom = 0.0;
            _panRandom = 0.0;
            _overlap = 0.5;
            _windowType = WindowType.Hann;
            _grainPhase = 0.0;
            _playbackPosition = 0.0;
            _playbackSpeed = 1.0;
            _sourceSampleRate = sampleRate;
            _sourceChannels = 1;
        }

        /// <summary>
        /// 加载源音频
        /// </summary>
        public void LoadSource(float[] audio, int sampleRate, int channels = 1)
        {
            _sourceAudio = audio;
            _sourceSampleRate = sampleRate;
            _sourceChannels = channels;
            _playbackPosition = 0.0;
        }

        /// <summary>
        /// 生成一个新颗粒
        /// </summary>
        private void SpawnGrain()
        {
            if (_sourceAudio == null || _sourceAudio.Length == 0) return;
            if (_activeGrains.Count >= _maxGrains) return;

            var grain = new Grain();
            int grainLength = (int)(_grainSizeMs * 0.001 * _sourceSampleRate);
            if (grainLength < 4) grainLength = 4;

            // 播放位置(带速度调制)
            int basePosition = (int)_playbackPosition;

            // 位置随机化
            int positionJitter = (int)(grainLength * _positionRandom * (new Random().NextDouble() * 2.0 - 1.0));
            grain.Position = Math.Max(0, Math.Min(_sourceAudio.Length - grainLength - 1,
                                                    basePosition + positionJitter));
            grain.Length = grainLength;

            // 音高(带随机)
            double pitch = _pitch;
            if (_pitchRandom > 0)
            {
                pitch *= Math.Pow(2.0, (new Random().NextDouble() * 2.0 - 1.0) * _pitchRandom);
            }
            grain.Pitch = pitch;
            grain.ReadPosition = 0;

            // 声像(带随机)
            grain.Pan = _panRandom > 0 ? (new Random().NextDouble() * 2.0 - 1.0) * _panRandom : 0;
            grain.Volume = 1.0;

            // 窗函数
            grain.Window = FFT.GenerateWindow(grainLength, _windowType);

            grain.IsActive = true;
            _activeGrains.Add(grain);
        }

        /// <summary>
        /// 处理一个样本, 输出立体声
        /// </summary>
        public (double left, double right) Process()
        {
            double dt = 1.0 / _sourceSampleRate;

            // 颗粒生成调度
            _grainPhase += _grainRateHz * dt;
            while (_grainPhase >= 1.0)
            {
                _grainPhase -= 1.0;
                SpawnGrain();
            }

            // 推进播放位置
            _playbackPosition += _playbackSpeed;
            if (_playbackPosition >= _sourceAudio.Length)
                _playbackPosition -= _sourceAudio.Length;

            // 混合所有活跃颗粒
            double leftSum = 0.0;
            double rightSum = 0.0;

            for (int i = _activeGrains.Count - 1; i >= 0; i--)
            {
                var grain = _activeGrains[i];
                if (!grain.IsActive)
                {
                    _activeGrains.RemoveAt(i);
                    continue;
                }

                // 读取颗粒样本(带音高变速)
                double readPos = grain.ReadPosition * grain.Pitch;
                int readIdx = (int)readPos;
                double frac = readPos - readIdx;

                int srcIdx = grain.Position + readIdx;
                if (srcIdx >= _sourceAudio.Length - 1)
                {
                    grain.IsActive = false;
                    _activeGrains.RemoveAt(i);
                    continue;
                }

                // 线性插值读取
                double sample = _sourceAudio[srcIdx] * (1.0 - frac) + _sourceAudio[srcIdx + 1] * frac;

                // 应用窗函数
                int windowIdx = (int)(grain.ReadPosition);
                if (windowIdx >= grain.Length)
                {
                    grain.IsActive = false;
                    _activeGrains.RemoveAt(i);
                    continue;
                }
                double window = grain.Window[windowIdx];
                sample *= window * grain.Volume;

                // 声像(等功率)
                double panAngle = (grain.Pan + 1.0) * Math.PI * 0.25;
                double leftGain = Math.Cos(panAngle);
                double rightGain = Math.Sin(panAngle);

                leftSum += sample * leftGain;
                rightSum += sample * rightGain;

                // 推进颗粒读取位置
                grain.ReadPosition++;
            }

            // 归一化(根据颗粒数)
            double norm = _activeGrains.Count > 0 ? 1.0 / Math.Sqrt(_activeGrains.Count) : 1.0;
            return (leftSum * norm, rightSum * norm);
        }

        /// <summary>
        /// 批量处理(立体声输出)
        /// </summary>
        public void Process(float[] outputLeft, float[] outputRight, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var (left, right) = Process();
                outputLeft[i] = (float)Math.Tanh(left);
                outputRight[i] = (float)Math.Tanh(right);
            }
        }

        public void Process(float[] output, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var (left, right) = Process();
                output[i] = (float)Math.Tanh((left + right) * 0.5);
            }
        }

        /// <summary>
        /// 设置播放位置(0~1)
        /// </summary>
        public void SetPlaybackPosition(double position)
        {
            if (_sourceAudio != null)
                _playbackPosition = position * _sourceAudio.Length;
        }

        public void Reset()
        {
            _activeGrains.Clear();
            _grainPhase = 0.0;
            _playbackPosition = 0.0;
        }
    }
}
