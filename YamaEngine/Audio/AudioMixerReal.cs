// ============================================================================
// YamaEngine AudioMixerReal.cs
// yama引擎 - 真实音频混音器实现
// 包含: 混音通道(音量/声像/静音/独奏/效果链/峰值表/RMS表/限制器)、
//       混音总线(Master/Group/Aux/PreFader/PostFader)、
//       混音器(路由/混音处理)、效果链、发送/返回、LUFS响度测量(EBU R128)。
// ============================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.Audio
{
    // ========================================================================
    // 效果器接口与链
    // ========================================================================

    /// <summary>
    /// 音频效果器接口 - 所有效果器实现此接口
    /// </summary>
    public interface IAudioEffect
    {
        void Process(float[] buffer, int channels);
        void Reset();
        string Name { get; }
        bool Enabled { get; set; }
    }

    /// <summary>
    /// 效果链 - 有序效果器列表, 串联处理。
    /// </summary>

    // ========================================================================
    // 发送/返回
    // ========================================================================

    /// <summary>
    /// 发送类型: Pre-Fader(推子前发送)或Post-Fader(推子后发送)
    /// </summary>
    public enum SendType
    {
        PreFader,
        PostFader
    }

    /// <summary>
    /// 发送/返回 - 辅助总线发送量与返回量。
    /// </summary>
    public sealed class AudioSendReturn
    {
        public string TargetBusName { get; set; }
        public double SendLevel { get; set; }    // 发送量(0~1, 线性)
        public double ReturnLevel { get; set; }  // 返回量(0~1, 线性)
        public SendType Type { get; set; }

        public AudioSendReturn(string targetBusName, double sendLevel = 0.0,
                               double returnLevel = 1.0, SendType type = SendType.PostFader)
        {
            TargetBusName = targetBusName;
            SendLevel = sendLevel;
            ReturnLevel = returnLevel;
            Type = type;
        }
    }

    // ========================================================================
    // 电平表 (峰值/RMS/LUFS)
    // ========================================================================

    /// <summary>
    /// 电平表 - 峰值表、RMS表、LUFS表(EBU R128响度测量)。
    /// </summary>
    public sealed class LevelMeter
    {
        private double _sampleRate;
        private double _peakLevel;       // 峰值(线性)
        private double _rmsLevel;        // RMS(线性)
        private double _lufsLevel;       // LUFS(响度, dB)
        private double _peakHoldLevel;   // 峰值保持
        private double _peakHoldTimer;

        // RMS窗口
        private readonly double[] _rmsBuffer;
        private int _rmsIndex;
        private double _rmsSum;
        private readonly int _rmsWindowSize;

        // EBU R128 K-weighting滤波器 (预滤波 + RLBF)
        private BiquadFilter _kWeightingStage1; // 高通shelf (pre-filter)
        private BiquadFilter _kWeightingStage2; // 高通 (RLBF)
        private readonly double[] _kWeightBuffer;
        private double _lufsBlockSum;
        private int _lufsBlockCount;
        private const double LUFS_REF = -0.691; // 参考偏移

        // 门限阈值(EBU R128: -70 LUFS绝对门限, -10 LUFS相对门限)
        private const double ABS_GATE = -70.0;
        private double _relativeGate = -10.0;
        private readonly List<double> _lufsBlockHistory;

        public double PeakLevel => _peakLevel;
        public double PeakLevelDb => LinearToDb(_peakLevel);
        public double RmsLevel => _rmsLevel;
        public double RmsLevelDb => LinearToDb(_rmsLevel);
        public double LufsLevel => _lufsLevel;
        public double PeakHoldDb => LinearToDb(_peakHoldLevel);

        public LevelMeter(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _peakLevel = 0;
            _rmsLevel = 0;
            _lufsLevel = -70.0;
            _peakHoldLevel = 0;
            _peakHoldTimer = 0;

            _rmsWindowSize = (int)(0.3 * sampleRate); // 300ms RMS窗口
            _rmsBuffer = new double[_rmsWindowSize];
            _rmsIndex = 0;
            _rmsSum = 0;

            // EBU R128 K-weighting: Stage1=high-shelf -4dB @ ~1500Hz, Stage2=highpass @ ~38Hz
            _kWeightingStage1 = new BiquadFilter(sampleRate);
            _kWeightingStage1.SetParameters(BiquadType.HighShelf, 1500.0, 0.5, -4.0);
            _kWeightingStage2 = new BiquadFilter(sampleRate);
            _kWeightingStage2.SetParameters(BiquadType.HighPass, 38.0, 0.5);

            _kWeightBuffer = new double[(int)(0.4 * sampleRate)]; // 400ms block
            _lufsBlockSum = 0;
            _lufsBlockCount = 0;
            _lufsBlockHistory = new List<double>();
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            _kWeightingStage1.SetSampleRate(sampleRate);
            _kWeightingStage2.SetSampleRate(sampleRate);
        }

        /// <summary>
        /// 处理一个样本块(interleaved)
        /// </summary>
        public void Process(float[] buffer, int channels)
        {
            int n = buffer.Length / channels;
            double blockPeak = 0;
            double blockSumSq = 0;

            for (int i = 0; i < n; i++)
            {
                // 取所有声道的绝对值最大作为峰值
                double sampleMax = 0;
                double sampleSumSq = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    double s = buffer[i * channels + ch];
                    double abs = Math.Abs(s);
                    if (abs > sampleMax) sampleMax = abs;
                    sampleSumSq += s * s;
                }

                // 峰值
                if (sampleMax > _peakLevel)
                    _peakLevel = sampleMax;
                if (sampleMax > blockPeak)
                    blockPeak = sampleMax;

                // RMS (滑动窗口)
                double meanSq = sampleSumSq / channels;
                _rmsSum -= _rmsBuffer[_rmsIndex];
                _rmsBuffer[_rmsIndex] = meanSq;
                _rmsSum += meanSq;
                _rmsIndex = (_rmsIndex + 1) % _rmsWindowSize;
                _rmsLevel = Math.Sqrt(_rmsSum / _rmsWindowSize);

                blockSumSq += meanSq;

                // LUFS: K-weighting后计算
                double weighted = _kWeightingStage2.Process(_kWeightingStage1.Process(sampleMax));
                _lufsBlockSum += weighted * weighted;
                _lufsBlockCount++;
            }

            // 峰值保持衰减
            _peakHoldTimer += n / _sampleRate;
            if (_peakHoldTimer > 1.5) // 1.5秒后衰减
            {
                _peakHoldLevel *= 0.99;
                _peakHoldTimer = 0;
            }
            if (blockPeak > _peakHoldLevel)
                _peakHoldLevel = blockPeak;

            // LUFS块计算(400ms块)
            if (_lufsBlockCount >= (int)(0.4 * _sampleRate))
            {
                double meanSq = _lufsBlockSum / _lufsBlockCount;
                double blockLoudness = meanSq > 0
                    ? LUFS_REF + 10 * Math.Log10(meanSq)
                    : ABS_GATE;

                if (blockLoudness > ABS_GATE)
                    _lufsBlockHistory.Add(blockLoudness);

                // 计算gated LUFS
                if (_lufsBlockHistory.Count > 0)
                {
                    // 绝对门限过滤
                    double sumAbove = 0;
                    int countAbove = 0;
                    foreach (var l in _lufsBlockHistory)
                    {
                        if (l > ABS_GATE)
                        {
                            sumAbove += Math.Pow(10, (l - LUFS_REF) / 10.0);
                            countAbove++;
                        }
                    }

                    if (countAbove > 0)
                    {
                        double ungatedLoudness = LUFS_REF + 10 * Math.Log10(sumAbove / countAbove);
                        _relativeGate = ungatedLoudness - 10.0;

                        // 相对门限过滤
                        double sumRel = 0;
                        int countRel = 0;
                        foreach (var l in _lufsBlockHistory)
                        {
                            if (l > _relativeGate)
                            {
                                sumRel += Math.Pow(10, (l - LUFS_REF) / 10.0);
                                countRel++;
                            }
                        }
                        if (countRel > 0)
                            _lufsLevel = LUFS_REF + 10 * Math.Log10(sumRel / countRel);
                    }
                }

                _lufsBlockSum = 0;
                _lufsBlockCount = 0;
            }
        }

        public void Reset()
        {
            _peakLevel = 0;
            _rmsLevel = 0;
            _lufsLevel = -70;
            _peakHoldLevel = 0;
            _peakHoldTimer = 0;
            _rmsSum = 0;
            Array.Clear(_rmsBuffer, 0, _rmsWindowSize);
            _rmsIndex = 0;
            _kWeightingStage1.Reset();
            _kWeightingStage2.Reset();
            _lufsBlockSum = 0;
            _lufsBlockCount = 0;
            _lufsBlockHistory.Clear();
        }

        public static double LinearToDb(double linear)
        {
            if (linear < 1e-10) return -200.0;
            return 20.0 * Math.Log10(linear);
        }

        public static double DbToLinear(double db)
        {
            return Math.Pow(10.0, db / 20.0);
        }
    }

    // ========================================================================
    // 混音总线
    // ========================================================================

    /// <summary>
    /// 总线类型
    /// </summary>
    public enum BusType
    {
        Master,
        Group,    // Ambient/Music/Dialogue/SFX/UI
        Aux       // Send/Return
    }

    /// <summary>
    /// 混音总线 - 接收多个通道的输入, 混合后应用总线效果, 路由到下一级。
    /// </summary>
    public sealed class AudioMixerBus
    {
        public string Name { get; set; }
        public BusType Type { get; set; }
        public double Volume { get; set; }      // 音量(0~1, 线性)
        public double VolumeDb { get => LevelMeter.LinearToDb(Volume); set => Volume = LevelMeter.DbToLinear(value); }
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public string OutputBusName { get; set; } // 路由目标
        public AudioEffectChain EffectChain { get; set; }
        public LevelMeter Meter { get; set; }
        public Limiter BusLimiter { get; set; }

        // 输入缓冲(累积所有通道的输入)
        private float[] _inputBuffer;
        private int _channels;

        public AudioMixerBus(string name, BusType type, int channels = 2, double sampleRate = 44100.0)
        {
            Name = name;
            Type = type;
            Volume = 1.0;
            Mute = false;
            Solo = false;
            OutputBusName = "Master";
            EffectChain = new AudioEffectChain();
            Meter = new LevelMeter(sampleRate);
            BusLimiter = new Limiter(sampleRate);
            BusLimiter.Threshold = -0.3;
            _channels = channels;
            _inputBuffer = new float[4096 * channels];
        }

        public void ClearInput()
        {
            Array.Clear(_inputBuffer, 0, _inputBuffer.Length);
        }

        /// <summary>
        /// 将数据混入总线输入缓冲
        /// </summary>
        public void MixIn(float[] source, int numFrames, double gain)
        {
            int samples = numFrames * _channels;
            if (samples > _inputBuffer.Length)
            {
                // 扩展缓冲
                Array.Resize(ref _inputBuffer, samples * 2);
            }
            for (int i = 0; i < samples; i++)
                _inputBuffer[i] += source[i] * (float)gain;
        }

        /// <summary>
        /// 处理总线: 应用效果链 → 限制器 → 音量 → 电平表
        /// </summary>
        public void Process(int numFrames, float[] outputBuffer)
        {
            if (Mute) return;

            int samples = numFrames * _channels;

            // 应用总线效果链
            EffectChain.Process(_inputBuffer, _channels);

            // 应用音量
            double vol = Volume;
            for (int i = 0; i < samples; i++)
                _inputBuffer[i] = (float)(_inputBuffer[i] * vol);

            // 总线限制器
            if (BusLimiter != null)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    for (int f = 0; f < numFrames; f++)
                    {
                        int idx = f * _channels + ch;
                        _inputBuffer[idx] = (float)BusLimiter.Process(_inputBuffer[idx]);
                    }
                }
            }

            // 电平表
            Meter.Process(_inputBuffer, _channels);

            // 输出到目标缓冲
            if (outputBuffer != null)
            {
                int copyLen = Math.Min(samples, outputBuffer.Length);
                Array.Copy(_inputBuffer, outputBuffer, copyLen);
            }

            // 清零输入缓冲供下一帧使用
            Array.Clear(_inputBuffer, 0, samples);
        }

        /// <summary>获取总线输出(不处理, 直接返回输入缓冲引用)</summary>
        public float[] GetOutput(int numFrames)
        {
            return _inputBuffer;
        }

        public void Reset()
        {
            EffectChain.Reset();
            Meter.Reset();
            BusLimiter?.Reset();
            ClearInput();
        }
    }

    // ========================================================================
    // 混音通道
    // ========================================================================

    /// <summary>
    /// 混音通道 - 单个音频源的混音通道。
    /// 包含音量/声像/静音/独奏/效果链/输入源/输出目标/峰值表/RMS表/限制器。
    /// </summary>
    public sealed class AudioMixerChannel
    {
        public string Name { get; set; }
        public double Volume { get; set; }        // 音量(0~1, 线性)
        public double VolumeDb { get => LevelMeter.LinearToDb(Volume); set => Volume = LevelMeter.DbToLinear(value); }
        public double Pan { get; set; }           // 声像(-1=左, 0=中, 1=右)
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public string OutputBusName { get; set; } // 输出总线名
        public AudioEffectChain EffectChain { get; set; }
        public LevelMeter Meter { get; set; }
        public List<AudioSendReturn> Sends { get; set; }

        // 输入源回调
        public Func<int, int, float[]> InputSource { get; set; }
        public int InputChannels { get; set; }

        // 通道限制器
        public Limiter ChannelLimiter { get; set; }

        // 声像模式
        public enum PanLaw { Linear, EqualPower }
        public PanLaw PanMode { get; set; }

        private double _sampleRate;

        public AudioMixerChannel(string name, int inputChannels = 2, double sampleRate = 44100.0)
        {
            Name = name;
            Volume = 1.0;
            Pan = 0.0;
            Mute = false;
            Solo = false;
            OutputBusName = "Master";
            EffectChain = new AudioEffectChain();
            Meter = new LevelMeter(sampleRate);
            Sends = new List<AudioSendReturn>();
            InputChannels = inputChannels;
            ChannelLimiter = null;
            PanMode = PanLaw.EqualPower;
            _sampleRate = sampleRate;
        }

        /// <summary>
        /// 计算声像增益(左/右声道)
        /// </summary>
        public (double leftGain, double rightGain) GetPanGains()
        {
            double pan = Math.Max(-1.0, Math.Min(1.0, Pan));
            if (PanMode == PanLaw.EqualPower)
            {
                // 等功率声像: L = cos((pan+1)*pi/4), R = sin((pan+1)*pi/4)
                double angle = (pan + 1.0) * Math.PI * 0.25;
                return (Math.Cos(angle), Math.Sin(angle));
            }
            else
            {
                // 线性声像
                double leftGain = pan <= 0 ? 1.0 : (1.0 - pan);
                double rightGain = pan >= 0 ? 1.0 : (1.0 + pan);
                return (leftGain, rightGain);
            }
        }

        /// <summary>
        /// 处理通道: 读取输入 → 应用音量/声像 → 效果链 → 电平表 → 输出
        /// </summary>
        public void Process(float[] outputBuffer, int numFrames, int outputChannels)
        {
            if (Mute) return;

            // 从输入源读取
            if (InputSource == null) return;
            float[] input = InputSource(numFrames, InputChannels);
            if (input == null) return;

            int inputCh = InputChannels;
            int samples = numFrames * inputCh;

            // 应用音量
            double vol = Volume;
            for (int i = 0; i < samples; i++)
                input[i] = (float)(input[i] * vol);

            // 声像处理(输入为立体声或单声道时)
            if (inputCh == 1 && outputChannels >= 2)
            {
                // 单声道→立体声声像
                var (leftGain, rightGain) = GetPanGains();
                for (int f = 0; f < numFrames; f++)
                {
                    double mono = input[f];
                    outputBuffer[f * outputChannels] = (float)(mono * leftGain);
                    outputBuffer[f * outputChannels + 1] = (float)(mono * rightGain);
                    // 填充其他声道为0
                    for (int ch = 2; ch < outputChannels; ch++)
                        outputBuffer[f * outputChannels + ch] = 0;
                }
            }
            else if (inputCh >= 2 && outputChannels >= 2)
            {
                // 立体声声像
                var (leftGain, rightGain) = GetPanGains();
                for (int f = 0; f < numFrames; f++)
                {
                    outputBuffer[f * outputChannels] = (float)(input[f * inputCh] * leftGain);
                    outputBuffer[f * outputChannels + 1] = (float)(input[f * inputCh + 1] * rightGain);
                    for (int ch = 2; ch < outputChannels && ch < inputCh; ch++)
                        outputBuffer[f * outputChannels + ch] = input[f * inputCh + ch];
                }
            }
            else
            {
                // 直接复制
                Array.Copy(input, outputBuffer, Math.Min(samples, outputBuffer.Length));
            }

            // 应用效果链
            EffectChain.Process(outputBuffer, outputChannels);

            // 通道限制器
            if (ChannelLimiter != null)
            {
                for (int ch = 0; ch < outputChannels; ch++)
                {
                    for (int f = 0; f < numFrames; f++)
                    {
                        int idx = f * outputChannels + ch;
                        outputBuffer[idx] = (float)ChannelLimiter.Process(outputBuffer[idx]);
                    }
                }
            }

            // 电平表
            Meter.Process(outputBuffer, outputChannels);
        }

        public void Reset()
        {
            EffectChain.Reset();
            Meter.Reset();
            ChannelLimiter?.Reset();
        }
    }

    // ========================================================================
    // 混音器
    // ========================================================================

    /// <summary>
    /// 真实音频混音器 - 通道管理/总线管理/路由/混音处理。
    /// </summary>
    public sealed class AudioMixerReal
    {
        private readonly Dictionary<string, AudioMixerChannel> _channels;
        private readonly Dictionary<string, AudioMixerBus> _buses;
        private readonly int _outputChannels;
        private double _sampleRate;

        // 处理缓冲
        private float[] _channelBuffer;
        private float[] _busBuffer;
        private float[] _masterBuffer;

        public AudioMixerBus MasterBus { get; private set; }
        public IReadOnlyDictionary<string, AudioMixerChannel> Channels => _channels;
        public IReadOnlyDictionary<string, AudioMixerBus> Buses => _buses;
        public int OutputChannels => _outputChannels;
        public double SampleRate => _sampleRate;

        public AudioMixerReal(int outputChannels = 2, double sampleRate = 44100.0)
        {
            _outputChannels = outputChannels;
            _sampleRate = sampleRate;
            _channels = new Dictionary<string, AudioMixerChannel>();
            _buses = new Dictionary<string, AudioMixerBus>();

            _channelBuffer = new float[4096 * outputChannels];
            _busBuffer = new float[4096 * outputChannels];
            _masterBuffer = new float[4096 * outputChannels];

            // 创建Master总线
            MasterBus = new AudioMixerBus("Master", BusType.Master, outputChannels, sampleRate);
            MasterBus.OutputBusName = null; // Master不路由到其他总线
            _buses["Master"] = MasterBus;

            // 创建标准分组总线
            CreateBus("Ambient", BusType.Group);
            CreateBus("Music", BusType.Group);
            CreateBus("Dialogue", BusType.Group);
            CreateBus("SFX", BusType.Group);
            CreateBus("UI", BusType.Group);

            // 创建Aux总线
            CreateBus("ReverbAux", BusType.Aux);
            CreateBus("DelayAux", BusType.Aux);
        }

        public AudioMixerBus CreateBus(string name, BusType type)
        {
            var bus = new AudioMixerBus(name, type, _outputChannels, _sampleRate);
            _buses[name] = bus;
            return bus;
        }

        public AudioMixerChannel CreateChannel(string name, int inputChannels = 2)
        {
            var channel = new AudioMixerChannel(name, inputChannels, _sampleRate);
            _channels[name] = channel;
            return channel;
        }

        public AudioMixerChannel GetChannel(string name)
        {
            return _channels.TryGetValue(name, out var ch) ? ch : null;
        }

        public AudioMixerBus GetBus(string name)
        {
            return _buses.TryGetValue(name, out var bus) ? bus : null;
        }

        public void RemoveChannel(string name)
        {
            _channels.Remove(name);
        }

        public void RemoveBus(string name)
        {
            if (name != "Master")
                _buses.Remove(name);
        }

        /// <summary>
        /// 检查是否有任何通道处于Solo状态
        /// </summary>
        private bool HasSoloChannel()
        {
            foreach (var ch in _channels.Values)
                if (ch.Solo) return true;
            return false;
        }

        /// <summary>
        /// 混音处理主循环
        /// 1. 清零所有总线输入
        /// 2. 对每个活跃通道: 读取→音量/声像→效果链→路由到目标总线
        /// 3. 对每个总线: 混合→总线效果→路由到下一级
        /// 4. Master总线输出
        /// </summary>
        public void Process(float[] outputBuffer, int numFrames)
        {
            int samples = numFrames * _outputChannels;

            // 确保缓冲区足够大
            if (_channelBuffer.Length < samples)
            {
                _channelBuffer = new float[samples * 2];
                _busBuffer = new float[samples * 2];
                _masterBuffer = new float[samples * 2];
            }

            // 1. 清零所有总线输入
            foreach (var bus in _buses.Values)
                bus.ClearInput();

            // 2. 检查Solo状态
            bool anySolo = HasSoloChannel();

            // 3. 处理每个活跃通道
            foreach (var channel in _channels.Values)
            {
                // Solo逻辑: 如果有通道Solo, 则只有Solo的通道发声
                if (anySolo && !channel.Solo) continue;
                if (channel.Mute) continue;

                // 清零通道缓冲
                Array.Clear(_channelBuffer, 0, samples);

                // 处理通道(读取输入→音量→声像→效果链→电平表)
                channel.Process(_channelBuffer, numFrames, _outputChannels);

                // 路由到目标总线(Post-Fader)
                string targetBus = channel.OutputBusName;
                if (!string.IsNullOrEmpty(targetBus) && _buses.ContainsKey(targetBus))
                {
                    _buses[targetBus].MixIn(_channelBuffer, numFrames, 1.0);
                }

                // 处理发送(Pre/Post Fader)
                foreach (var send in channel.Sends)
                {
                    if (send.SendLevel <= 0) continue;
                    if (!_buses.ContainsKey(send.TargetBusName)) continue;

                    if (send.Type == SendType.PostFader)
                    {
                        // Post-Fader: 发送通道处理后的信号
                        _buses[send.TargetBusName].MixIn(_channelBuffer, numFrames, send.SendLevel);
                    }
                    else
                    {
                        // Pre-Fader: 发送通道处理前(只有音量前)的信号
                        // 简化: 使用通道缓冲但乘以发送量(不含通道音量)
                        // 实际实现需要Pre-Fader tap, 这里用通道输出近似
                        _buses[send.TargetBusName].MixIn(_channelBuffer, numFrames, send.SendLevel / Math.Max(0.0001, channel.Volume));
                    }
                }
            }

            // 4. 处理总线(按依赖顺序: Aux → Group → Master)
            ProcessBusChain("ReverbAux", numFrames);
            ProcessBusChain("DelayAux", numFrames);
            ProcessBusChain("Ambient", numFrames);
            ProcessBusChain("Music", numFrames);
            ProcessBusChain("Dialogue", numFrames);
            ProcessBusChain("SFX", numFrames);
            ProcessBusChain("UI", numFrames);

            // 5. Master总线处理
            MasterBus.Process(numFrames, _masterBuffer);

            // 6. 输出
            int copyLen = Math.Min(samples, outputBuffer.Length);
            Array.Copy(_masterBuffer, outputBuffer, copyLen);
        }

        /// <summary>
        /// 处理单条总线并路由到下一级
        /// </summary>
        private void ProcessBusChain(string busName, int numFrames)
        {
            if (!_buses.TryGetValue(busName, out var bus)) return;
            if (bus.Mute) return;

            // 处理总线
            bus.Process(numFrames, _busBuffer);

            // 路由到下一级总线
            if (!string.IsNullOrEmpty(bus.OutputBusName) && _buses.ContainsKey(bus.OutputBusName))
            {
                _buses[bus.OutputBusName].MixIn(_busBuffer, numFrames, 1.0);
            }
        }

        /// <summary>
        /// 获取Master总线峰值(dB)
        /// </summary>
        public double GetMasterPeakDb()
        {
            return MasterBus.Meter.PeakLevelDb;
        }

        /// <summary>
        /// 获取Master总线LUFS响度
        /// </summary>
        public double GetMasterLufs()
        {
            return MasterBus.Meter.LufsLevel;
        }

        /// <summary>
        /// 获取指定总线的RMS电平(dB)
        /// </summary>
        public double GetBusRmsDb(string busName)
        {
            if (_buses.TryGetValue(busName, out var bus))
                return bus.Meter.RmsLevelDb;
            return -200.0;
        }

        public void Reset()
        {
            foreach (var ch in _channels.Values)
                ch.Reset();
            foreach (var bus in _buses.Values)
                bus.Reset();
        }

        public void SetSampleRate(double sampleRate)
        {
            _sampleRate = sampleRate;
            foreach (var ch in _channels.Values)
                ch.Meter.SetSampleRate(sampleRate);
            foreach (var bus in _buses.Values)
                bus.Meter.SetSampleRate(sampleRate);
        }
    }

    // ========================================================================
    // 内置效果器适配器 - 将DSP效果包装为IAudioEffect
    // ========================================================================

    /// <summary>
    /// Biquad滤波器效果适配器
    /// </summary>
    public sealed class BiquadEffectAdapter : IAudioEffect
    {
        private readonly BiquadFilter[] _filters; // 每声道一个
        public string Name => "BiquadFilter";
        public bool Enabled { get; set; } = true;

        public BiquadEffectAdapter(BiquadType type, double frequency, double q, double gainDb, int channels, double sampleRate)
        {
            _filters = new BiquadFilter[channels];
            for (int i = 0; i < channels; i++)
            {
                _filters[i] = new BiquadFilter(sampleRate);
                _filters[i].SetParameters(type, frequency, q, gainDb);
            }
        }

        public void Process(float[] buffer, int channels)
        {
            int n = buffer.Length / channels;
            for (int ch = 0; ch < channels && ch < _filters.Length; ch++)
            {
                for (int i = 0; i < n; i++)
                {
                    int idx = i * channels + ch;
                    buffer[idx] = (float)_filters[ch].Process(buffer[idx]);
                }
            }
        }

        public void Reset()
        {
            foreach (var f in _filters) f.Reset();
        }
    }

    /// <summary>
    /// 压缩器效果适配器
    /// </summary>
    public sealed class CompressorEffectAdapter : IAudioEffect
    {
        private readonly Compressor[] _compressors;
        public string Name => "Compressor";
        public bool Enabled { get; set; } = true;

        public CompressorEffectAdapter(double threshold, double ratio, double attack, double release,
                                       double makeupGain, int channels, double sampleRate)
        {
            _compressors = new Compressor[channels];
            for (int i = 0; i < channels; i++)
            {
                _compressors[i] = new Compressor(sampleRate);
                _compressors[i].Threshold = threshold;
                _compressors[i].Ratio = ratio;
                _compressors[i].AttackTime = attack;
                _compressors[i].ReleaseTime = release;
                _compressors[i].MakeupGain = makeupGain;
            }
        }

        public void Process(float[] buffer, int channels)
        {
            int n = buffer.Length / channels;
            for (int ch = 0; ch < channels && ch < _compressors.Length; ch++)
            {
                for (int i = 0; i < n; i++)
                {
                    int idx = i * channels + ch;
                    buffer[idx] = (float)_compressors[ch].Process(buffer[idx]);
                }
            }
        }

        public void Reset()
        {
            foreach (var c in _compressors) c.Reset();
        }
    }

    /// <summary>
    /// 混响效果适配器
    /// </summary>
    public sealed class ReverbEffectAdapter : IAudioEffect
    {
        private readonly AlgorithmicReverb _reverb;
        private float[] _tempLeft;
        private float[] _tempRight;
        public string Name => "Reverb";
        public bool Enabled { get; set; } = true;

        public ReverbEffectAdapter(double roomSize, double damping, double wet, double dry,
                                    double width, int channels, double sampleRate)
        {
            _reverb = new AlgorithmicReverb(sampleRate);
            _reverb.RoomSize = roomSize;
            _reverb.Damping = damping;
            _reverb.WetLevel = wet;
            _reverb.DryLevel = dry;
            _reverb.Width = width;
            _tempLeft = new float[4096];
            _tempRight = new float[4096];
        }

        public void Process(float[] buffer, int channels)
        {
            if (channels < 2)
            {
                _reverb.ProcessBuffer(buffer);
                return;
            }
            int n = buffer.Length / channels;
            if (_tempLeft.Length < n) Array.Resize(ref _tempLeft, n);
            if (_tempRight.Length < n) Array.Resize(ref _tempRight, n);

            // 提取左右声道
            for (int i = 0; i < n; i++)
            {
                _tempLeft[i] = buffer[i * channels];
                _tempRight[i] = buffer[i * channels + 1];
            }

            // 处理混响
            float[] outL = new float[n];
            float[] outR = new float[n];
            _reverb.ProcessStereo(_tempLeft, _tempRight, outL, outR);

            // 写回
            for (int i = 0; i < n; i++)
            {
                buffer[i * channels] = outL[i];
                buffer[i * channels + 1] = outR[i];
            }
        }

        public void Reset()
        {
            _reverb.Reset();
        }
    }
}
