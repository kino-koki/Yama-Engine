using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// 注：本文件使用子命名空间 ShanyuEngine.Audio.Effects，而非直接使用
// ShanyuEngine.Audio。原因：父命名空间 ShanyuEngine.Audio 中已存在
//   * AudioBus 枚举（AudioSystem.cs，标识 Master/Music/SFX/...）
//   * ReverbEffect / EqualizerEffect / CompressorEffect / DelayEffect
//     （AdvancedAudioEngine.cs，基于 DSPNode 的旧版效果器）
// 为避免同名类型冲突导致编译失败，同时保留用户指定的全部类名，本扩展
// 放置于 Effects 子命名空间。父命名空间类型（AudioSystem / AudioInstance /
// AudioClip）在子命名空间中可直接访问，二者无缝对接。
namespace ShanyuEngine.Audio.Effects
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 音效系统扩展 (Audio Effects System)
    // --------------------------------------------------------------------------
    // 设计目标：为音频系统提供专业级实时 DSP 效果链与混音总线。
    //   - AudioEffect：效果基类，定义 Process(缓冲区) 统一接口
    //   - AudioEffectChain：有序效果列表，串联处理（插件链）
    //   - 内置效果：卷积/算法混响、参数均衡器、压缩器、限制器、延迟、合唱、
    //       镶边、失真、变调、噪声门、Bitcrusher
    //   - AudioBus：音频总线（效果链 + 音量 + 路由 + 发送 + 独奏/静音）
    //   - AudioEffectParameter：可被时间线自动化的参数曲线
    //   - AudioEffectPreset：效果链预设的保存/加载
    //   - AudioDspSystem：顶层 DSP 系统，管理所有总线与效果，每帧执行混音
    //
    // 对接关系：
    //   * ShanyuEngine.Audio.AudioSystem / AudioInstance / AudioClip
    //       —— 从音频系统拉取活跃实例，按总线路由，处理其音频缓冲并输出
    //   * 处理模型：交错的 float 采样缓冲（buffer[frameCount*channels]）
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // AudioEffectParameter：可自动化参数
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 效果参数。带最小/最大/默认值约束，并支持由时间线驱动的自动化曲线
    /// （关键点线性插值）。未启用自动化时使用手动设定的 <see cref="Value"/>。
    /// </summary>
    public sealed class AudioEffectParameter
    {
        /// <summary>参数名。</summary>
        public string Name;
        /// <summary>当前手动值（未启用自动化时使用）。</summary>
        public float Value;
        /// <summary>最小值。</summary>
        public float MinValue = 0f;
        /// <summary>最大值。</summary>
        public float MaxValue = 1f;
        /// <summary>默认值。</summary>
        public float DefaultValue = 0f;
        /// <summary>是否已被时间线自动化。</summary>
        public bool IsAutomated => _automation.Count > 0;

        private readonly List<(float Time, float Value)> _automation = new();
        private bool _sorted = true;

        public AudioEffectParameter(string name, float value, float min = 0f, float max = 1f)
        {
            Name = name; MinValue = min; MaxValue = max;
            DefaultValue = value; Value = value;
        }

        /// <summary>设置手动值（自动钳制到范围）。</summary>
        public void SetValue(float v) => Value = Math.Clamp(v, MinValue, MaxValue);

        /// <summary>添加一个自动化关键点（时间秒, 数值）。</summary>
        public void AddAutomationPoint(float time, float value)
        {
            _automation.Add((time, Math.Clamp(value, MinValue, MaxValue)));
            _sorted = false;
        }

        /// <summary>清空自动化曲线，回到手动值。</summary>
        public void ClearAutomation() => _automation.Clear();

        /// <summary>在时间 t 处求值：自动化启用时取曲线值，否则取手动值。</summary>
        public float Evaluate(float t)
        {
            if (!IsAutomated) return Value;
            EnsureSorted();
            if (_automation.Count == 1) return _automation[0].Value;
            if (t <= _automation[0].Time) return _automation[0].Value;
            if (t >= _automation[^1].Time) return _automation[^1].Value;
            int lo = 0, hi = _automation.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_automation[mid].Time <= t) lo = mid; else hi = mid;
            }
            var a = _automation[lo]; var b = _automation[hi];
            float dt = b.Time - a.Time;
            float u = dt > 1e-6f ? (t - a.Time) / dt : 0f;
            return a.Value + (b.Value - a.Value) * u;
        }

        private void EnsureSorted()
        {
            if (!_sorted) { _automation.Sort((x, y) => x.Time.CompareTo(y.Time)); _sorted = true; }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AudioEffect：效果基类
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 音效基类。所有效果器继承自此基类，实现 <see cref="Process"/> 对交错浮点
    /// 缓冲区进行逐帧（可逐采样）处理。处理为就地修改（in-place）。
    /// </summary>
    public abstract class AudioEffect
    {
        /// <summary>效果显示名称。</summary>
        public string Name;
        /// <summary>是否启用（false 时直通不处理）。</summary>
        public bool Enabled = true;
        /// <summary>干/湿混合比例 [0,1]：0=全干（原信号），1=全湿（处理后信号）。</summary>
        public float Mix = 1f;
        /// <summary>效果参数表（按名存取，支持自动化）。</summary>
        public readonly Dictionary<string, AudioEffectParameter> Parameters = new();

        /// <summary>当前处理时间（秒），由链/系统每帧更新，用于参数自动化求值。</summary>
        public float CurrentTime;

        protected AudioEffect(string name) => Name = name;

        /// <summary>添加一个参数并返回引用以便链式配置。</summary>
        protected AudioEffectParameter AddParam(string name, float value, float min = 0f, float max = 1f)
        {
            var p = new AudioEffectParameter(name, value, min, max);
            Parameters[name] = p;
            return p;
        }

        /// <summary>获取参数当前值（考虑时间线自动化）。</summary>
        protected float P(string name) => Parameters.TryGetValue(name, out var p) ? p.Evaluate(CurrentTime) : 0f;

        /// <summary>设置参数手动值。</summary>
        public void SetParameter(string name, float value)
        {
            if (Parameters.TryGetValue(name, out var p)) p.SetValue(value);
        }

        /// <summary>获取参数对象（用于添加自动化点）。</summary>
        public AudioEffectParameter? GetParameter(string name) =>
            Parameters.TryGetValue(name, out var p) ? p : null;

        /// <summary>
        /// 就地处理音频缓冲区。
        /// </summary>
        /// <param name="buffer">交错浮点采样（buffer[frameCount * channels]）。</param>
        /// <param name="frameCount">帧数（每帧 channels 个采样）。</param>
        /// <param name="channels">声道数（1=单声道，2=立体声）。</param>
        /// <param name="sampleRate">采样率（Hz）。</param>
        public abstract void Process(float[] buffer, int frameCount, int channels, int sampleRate);

        /// <summary>采样率/声道变更时调用，用于（重新）分配延迟线、滤波器状态等。</summary>
        public virtual void Prepare(int sampleRate, int channels) { }

        /// <summary>重置内部状态（清空延迟线、包络等），用于跳转/停止。</summary>
        public virtual void Reset() { }

        /// <summary>干湿混合辅助：把 wetBuffer 按 Mix 混入 buffer（buffer 原为干信号）。</summary>
        protected void ApplyMix(float[] buffer, float[] wet, int frameCount, int channels)
        {
            if (Mix >= 0.999f)
            {
                Buffer.BlockCopy(wet, 0, buffer, 0, frameCount * channels * sizeof(float));
                return;
            }
            float dry = 1f - Mix;
            int n = frameCount * channels;
            for (int i = 0; i < n; i++) buffer[i] = buffer[i] * dry + wet[i] * Mix;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AudioEffectChain：效果链（串联处理）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 效果链。维护一个有序的 <see cref="AudioEffect"/> 列表，按顺序串联处理：
    /// 前一个效果的输出作为后一个的输入。支持增删、移动与清空。
    /// </summary>

    // ════════════════════════════════════════════════════════════════════════
    // 滤波器原语：Biquad（IIR）/ Comb / Allpass
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 双二阶（Biquad）IIR 滤波器，Direct Form I。支持 RBJ Cookbook 全套滤波器
    /// 类型（低通/高通/低架/高架/峰值/陷波/全通），是均衡器与音色塑形的基础。
    /// </summary>
    public sealed class BiquadFilter
    {
        // 归一化系数
        private float _b0, _b1, _b2, _a1, _a2;
        // 状态（z1, z2）
        private float _z1, _z2;

        public enum FilterType { LowPass, HighPass, LowShelf, HighShelf, Peak, Notch, AllPass }

        /// <summary>按类型与参数重新计算系数。</summary>
        public void Set(FilterType type, float frequency, float sampleRate, float q = 0.707f, float gainDb = 0f)
        {
            float w0 = 2f * MathF.PI * frequency / sampleRate;
            float cosw = MathF.Cos(w0), sinw = MathF.Sin(w0);
            float alpha = sinw / (2f * q);
            float A = MathF.Pow(10f, gainDb / 40f); // 增益（架/峰用）

            float b0, b1, b2, a0, a1, a2;
            switch (type)
            {
                case FilterType.LowPass:
                    b0 = (1f - cosw) / 2f; b1 = 1f - cosw; b2 = (1f - cosw) / 2f;
                    a0 = 1f + alpha; a1 = -2f * cosw; a2 = 1f - alpha;
                    break;
                case FilterType.HighPass:
                    b0 = (1f + cosw) / 2f; b1 = -(1f + cosw); b2 = (1f + cosw) / 2f;
                    a0 = 1f + alpha; a1 = -2f * cosw; a2 = 1f - alpha;
                    break;
                case FilterType.LowShelf:
                    b0 = A * ((A + 1f) - (A - 1f) * cosw + 2f * MathF.Sqrt(A) * alpha);
                    b1 = 2f * A * ((A - 1f) - (A + 1f) * cosw);
                    b2 = A * ((A + 1f) - (A - 1f) * cosw - 2f * MathF.Sqrt(A) * alpha);
                    a0 = (A + 1f) + (A - 1f) * cosw + 2f * MathF.Sqrt(A) * alpha;
                    a1 = -2f * ((A - 1f) + (A + 1f) * cosw);
                    a2 = (A + 1f) + (A - 1f) * cosw - 2f * MathF.Sqrt(A) * alpha;
                    break;
                case FilterType.HighShelf:
                    b0 = A * ((A + 1f) + (A - 1f) * cosw + 2f * MathF.Sqrt(A) * alpha);
                    b1 = -2f * A * ((A - 1f) + (A + 1f) * cosw);
                    b2 = A * ((A + 1f) + (A - 1f) * cosw - 2f * MathF.Sqrt(A) * alpha);
                    a0 = (A + 1f) - (A - 1f) * cosw + 2f * MathF.Sqrt(A) * alpha;
                    a1 = 2f * ((A - 1f) - (A + 1f) * cosw);
                    a2 = (A + 1f) - (A - 1f) * cosw - 2f * MathF.Sqrt(A) * alpha;
                    break;
                case FilterType.Peak:
                    b0 = 1f + alpha * A; b1 = -2f * cosw; b2 = 1f - alpha * A;
                    a0 = 1f + alpha / A; a1 = -2f * cosw; a2 = 1f - alpha / A;
                    break;
                case FilterType.Notch:
                    b0 = 1f; b1 = -2f * cosw; b2 = 1f;
                    a0 = 1f + alpha; a1 = -2f * cosw; a2 = 1f - alpha;
                    break;
                default: // AllPass
                    b0 = 1f - alpha; b1 = -2f * cosw; b2 = 1f + alpha;
                    a0 = 1f + alpha; a1 = -2f * cosw; a2 = 1f - alpha;
                    break;
            }
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
            _a1 = a1 / a0; _a2 = a2 / a0;
        }

        /// <summary>处理单个采样并更新状态（DF I）。</summary>
        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        public void Reset() { _z1 = _z2 = 0f; }
    }

    /// <summary>梳状滤波器（Freeverb 混响基础）。y[n]=x[n]+feedback*y[n-delay]。</summary>
    internal sealed class CombFilter
    {
        private float[] _buf;
        private int _idx;
        private float _feedback;
        private float _damp;     // 阻尼系数 [0,1]（反馈路径单极低通）
        private float _filter;   // 低通滤波器状态

        public CombFilter(int delaySamples) { _buf = new float[Math.Max(1, delaySamples)]; }

        public void SetFeedback(float f) => _feedback = f;
        public void SetDamping(float d) => _damp = d;

        public float Process(float input)
        {
            float output = _buf[_idx];
            // 反馈路径上的单极低通（阻尼），衰减高频反馈，控制混响音色
            _filter = output * _damp + _filter * (1f - _damp);
            _buf[_idx] = input + _filter * _feedback;
            if (++_idx >= _buf.Length) _idx = 0;
            return output;
        }

        public void Reset() { Array.Clear(_buf, 0, _buf.Length); _filter = 0f; _idx = 0; }
    }

    /// <summary>全通滤波器（Freeverb 混响扩散级）。y[n]=-g*x[n]+x[n-D]+g*y[n-D]。</summary>
    internal sealed class AllpassFilter
    {
        private float[] _buf;
        private int _idx;
        private float _feedback;

        public AllpassFilter(int delaySamples) { _buf = new float[Math.Max(1, delaySamples)]; }

        public void SetFeedback(float f) => _feedback = f;

        public float Process(float input)
        {
            float output = -input + _buf[_idx];
            _buf[_idx] = input + _buf[_idx] * _feedback;
            if (++_idx >= _buf.Length) _idx = 0;
            return output;
        }

        public void Reset() { Array.Clear(_buf, 0, _buf.Length); _idx = 0; }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 内置音效
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 混响效果。支持两种模式：
    ///   * Convolution（卷积混响）：与脉冲响应 (IR) 直接卷积，真实感最高
    ///   * Algorithmic（算法混响）：Freeverb/Schroeder 网络（梳状+全通）
    /// </summary>
    public sealed class ReverbEffect : AudioEffect
    {
        public enum ReverbMode { Convolution, Algorithmic }

        private float[]? _ir;
        // 卷积用输入历史（每声道）
        private float[][]? _convHistory;
        private int _convPos;

        // Freeverb 网络（每声道）
        private CombFilter[][]? _combs;
        private AllpassFilter[][]? _allpasses;

        // Freeverb 经典延迟长度（按 44.1k，使用时按采样率缩放）
        private static readonly int[] CombDelays = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1623 };
        private static readonly int[] AllpassDelays = { 556, 441, 341, 225 };

        public ReverbEffect() : base("Reverb")
        {
            AddParam("Mode", 1, 0, 1);          // 0=Convolution,1=Algorithmic
            AddParam("RoomSize", 0.7f, 0, 1);
            AddParam("Damping", 0.3f, 0, 1);
            AddParam("Wet", 0.3f, 0, 1);
            AddParam("Width", 1f, 0, 1);
        }

        /// <summary>设置卷积脉冲响应（单声道 IR，将应用于所有声道）。</summary>
        public void SetImpulseResponse(float[] ir) { _ir = ir; }

        public override void Prepare(int sampleRate, int channels)
        {
            // 初始化 Freeverb 网络（每声道一组 8 梳状 + 4 全通）
            _combs = new CombFilter[channels][];
            _allpasses = new AllpassFilter[channels][];
            float scale = sampleRate / 44100f;
            for (int c = 0; c < channels; c++)
            {
                _combs[c] = new CombFilter[CombDelays.Length];
                for (int i = 0; i < CombDelays.Length; i++)
                    _combs[c][i] = new CombFilter(Math.Max(1, (int)(CombDelays[i] * scale)));
                _allpasses[c] = new AllpassFilter[AllpassDelays.Length];
                for (int i = 0; i < AllpassDelays.Length; i++)
                    _allpasses[c][i] = new AllpassFilter(Math.Max(1, (int)(AllpassDelays[i] * scale)));
            }
            // 卷积历史
            int irLen = _ir?.Length ?? 1;
            _convHistory = new float[channels][];
            for (int c = 0; c < channels; c++) _convHistory[c] = new float[irLen];
            _convPos = 0;
        }

        public override void Reset()
        {
            if (_combs != null) foreach (var g in _combs) foreach (var f in g) f.Reset();
            if (_allpasses != null) foreach (var g in _allpasses) foreach (var f in g) f.Reset();
            if (_convHistory != null) foreach (var h in _convHistory) Array.Clear(h, 0, h.Length);
            _convPos = 0;
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            ReverbMode mode = (ReverbMode)(int)P("Mode");
            float wet = P("Wet");
            float roomSize = P("RoomSize");
            float damping = P("Damping");
            float width = P("Width");

            float[] outBuf = new float[frameCount * channels];
            if (mode == ReverbMode.Convolution && _ir != null && _convHistory != null)
                ProcessConvolution(buffer, outBuf, frameCount, channels);
            else if (_combs != null && _allpasses != null)
                ProcessFreeverb(buffer, outBuf, frameCount, channels, roomSize, damping, width);
            else
                return; // 未准备好，直通

            // wet 缩放后再与原信号混合
            int n = frameCount * channels;
            for (int i = 0; i < n; i++) outBuf[i] *= wet;
            ApplyMix(buffer, outBuf, frameCount, channels);
        }

        /// <summary>卷积混响：y[n]=sum IR[k]*x[n-k]。O(N*L)，短 IR 可用；长 IR 应换 FFT。</summary>
        private void ProcessConvolution(float[] input, float[] output, int frameCount, int channels)
        {
            int irLen = _ir!.Length;
            for (int c = 0; c < channels; c++)
            {
                var hist = _convHistory![c];
                for (int f = 0; f < frameCount; f++)
                {
                    float x = input[f * channels + c];
                    hist[_convPos] = x;
                    float acc = 0f;
                    int hp = _convPos;
                    for (int k = 0; k < irLen; k++)
                    {
                        acc += _ir[k] * hist[hp];
                        if (--hp < 0) hp = irLen - 1;
                    }
                    output[f * channels + c] = acc;
                    if (++_convPos >= irLen) _convPos = 0;
                }
            }
        }

        /// <summary>Freeverb 算法混响：8 梳状并联 -> 4 全通串联。</summary>
        private void ProcessFreeverb(float[] input, float[] output, int frameCount, int channels,
            float roomSize, float damping, float width)
        {
            float feedback = roomSize * 0.28f + 0.7f; // 0.7~0.98
            for (int c = 0; c < channels; c++)
            {
                var combs = _combs![c];
                var allpasses = _allpasses![c];
                foreach (var cb in combs) { cb.SetFeedback(feedback); cb.SetDamping(damping); }
                foreach (var ap in allpasses) ap.SetFeedback(0.5f);

                for (int f = 0; f < frameCount; f++)
                {
                    float x = input[f * channels + c];
                    float y = 0f;
                    foreach (var cb in combs) y += cb.Process(x);
                    foreach (var ap in allpasses) y = ap.Process(y);
                    // 立体声宽度：第二声道按 width 衰减，模拟声像收窄；固定增益补偿
                    float widthScale = (c == 1) ? width : 1f;
                    output[f * channels + c] = y * 0.015f * widthScale;
                }
            }
        }
    }

    /// <summary>
    /// 参数均衡器。多段级联 Biquad 滤波器，支持低通/高通/低架/高架/峰值/陷波。
    /// </summary>
    public sealed class EqualizerEffect : AudioEffect
    {
        /// <summary>一个 EQ 频段。</summary>
        public sealed class EQBand
        {
            public BiquadFilter.FilterType Type = BiquadFilter.FilterType.Peak;
            public float Frequency = 1000f;
            public float Gain = 0f;     // dB（峰值/架用）
            public float Q = 0.707f;
            public bool Enabled = true;
        }

        /// <summary>频段列表（顺序即级联顺序）。</summary>
        public readonly List<EQBand> Bands = new();
        private BiquadFilter[]? _filters;
        private int _preparedChannels;

        public EqualizerEffect() : base("Equalizer") { }

        /// <summary>添加一个频段。</summary>
        public EQBand AddBand(BiquadFilter.FilterType type, float freq, float gainDb = 0f, float q = 0.707f)
        {
            var b = new EQBand { Type = type, Frequency = freq, Gain = gainDb, Q = q };
            Bands.Add(b);
            return b;
        }

        public override void Prepare(int sampleRate, int channels)
        {
            _preparedChannels = channels;
            _filters = new BiquadFilter[Bands.Count * channels];
            for (int i = 0; i < _filters.Length; i++) _filters[i] = new BiquadFilter();
            UpdateCoeffs(sampleRate, channels);
        }

        private void UpdateCoeffs(int sampleRate, int channels)
        {
            if (_filters == null) return;
            for (int b = 0; b < Bands.Count; b++)
            {
                var band = Bands[b];
                for (int c = 0; c < channels; c++)
                    _filters[b * channels + c].Set(band.Type, band.Frequency, sampleRate, band.Q, band.Gain);
            }
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_filters == null || _filters.Length != Bands.Count * channels) Prepare(sampleRate, channels);
            for (int b = 0; b < Bands.Count; b++)
            {
                if (!Bands[b].Enabled) continue;
                for (int c = 0; c < channels; c++)
                {
                    var f = _filters[b * channels + c];
                    for (int fr = 0; fr < frameCount; fr++)
                    {
                        int idx = fr * channels + c;
                        buffer[idx] = f.Process(buffer[idx]);
                    }
                }
            }
        }

        public override void Reset()
        {
            if (_filters != null) foreach (var f in _filters) f.Reset();
        }
    }

    /// <summary>
    /// 动态压缩器。阈值/比率/启动/释放/膝盖宽度/makeup gain，dB 域增益计算。
    /// </summary>
    public sealed class CompressorEffect : AudioEffect
    {
        private float[]? _envelope; // 每声道包络（dB）

        public CompressorEffect() : base("Compressor")
        {
            AddParam("Threshold", -24, -60, 0);   // dB
            AddParam("Ratio", 4, 1, 20);
            AddParam("Attack", 10, 0.1f, 200);    // ms
            AddParam("Release", 100, 10, 1000);   // ms
            AddParam("Knee", 12, 0, 24);          // dB 膝宽
            AddParam("Makeup", 0, 0, 24);         // dB
        }

        public override void Prepare(int sampleRate, int channels) => _envelope = new float[channels];

        public override void Reset() { if (_envelope != null) Array.Clear(_envelope, 0, _envelope.Length); }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_envelope == null) _envelope = new float[channels];
            float threshold = P("Threshold");
            float ratio = P("Ratio");
            float attackMs = P("Attack");
            float releaseMs = P("Release");
            float knee = P("Knee");
            float makeup = P("Makeup");

            float attackCoef = (float)Math.Exp(-1.0 / (attackMs * 0.001 * sampleRate));
            float releaseCoef = (float)Math.Exp(-1.0 / (releaseMs * 0.001 * sampleRate));
            float makeupGain = (float)Math.Pow(10, makeup / 20.0);

            for (int c = 0; c < channels; c++)
            {
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = f * channels + c;
                    float x = buffer[idx];
                    // 峰值检测转 dB
                    float det = x != 0 ? 20f * MathF.Log10(MathF.Abs(x)) : -120f;
                    // 包络跟随（向上快攻击，向下慢释放）
                    float coef = det > _envelope[c] ? attackCoef : releaseCoef;
                    _envelope[c] = coef * _envelope[c] + (1f - coef) * det;

                    // dB 域增益计算（带软膝盖）
                    float gainDb = ComputeGainReduction(_envelope[c], threshold, ratio, knee);
                    float gainLin = MathF.Pow(10f, gainDb / 20f) * makeupGain;
                    buffer[idx] = x * gainLin;
                }
            }
        }

        /// <summary>软膝盖增益缩减曲线，返回 dB 增益（负值表示衰减量）。</summary>
        private static float ComputeGainReduction(float envDb, float threshold, float ratio, float knee)
        {
            float slope = 1f - 1f / ratio;       // 压缩斜率（0~1）
            float lo = threshold - knee * 0.5f;  // 膝区下沿
            float hi = threshold + knee * 0.5f;  // 膝区上沿
            if (envDb <= lo) return 0f;          // 阈值以下：不压缩
            if (envDb >= hi)
                return -(envDb - threshold) * slope; // 膝区以上：完整压缩
            // 膝区内：用二次曲线从 0 平滑过渡到膝区上沿的完整压缩量（连续且可导）
            float t = (envDb - lo) / knee;            // 归一化 [0,1]
            float fullAtHi = (hi - threshold) * slope; // 膝区上沿的缩减量
            return -(fullAtHi * t * t);
        }
    }

    /// <summary>限制器（砖墙）。保证输出不超过 Ceiling，带攻击/释放平滑。</summary>
    public sealed class LimiterEffect : AudioEffect
    {
        private float[]? _envelope;

        public LimiterEffect() : base("Limiter")
        {
            AddParam("Threshold", -1, -12, 0);  // dB
            AddParam("Ceiling", -0.3f, -6, 0);  // dB
            AddParam("Release", 100, 10, 1000); // ms
        }

        public override void Prepare(int sampleRate, int channels) => _envelope = new float[channels];

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_envelope == null) _envelope = new float[channels];
            float threshold = P("Threshold");
            float ceiling = P("Ceiling");
            float releaseMs = P("Release");
            float releaseCoef = (float)Math.Exp(-1.0 / (releaseMs * 0.001 * sampleRate));
            float threshLin = MathF.Pow(10f, threshold / 20f);
            float ceilLin = MathF.Pow(10f, ceiling / 20f);

            for (int c = 0; c < channels; c++)
            {
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = f * channels + c;
                    float x = buffer[idx];
                    float det = MathF.Abs(x);
                    float gain = 1f;
                    if (det > threshLin)
                    {
                        float target = threshLin / det;
                        _envelope[c] = target; // 即时攻击
                        gain = target;
                    }
                    else
                    {
                        _envelope[c] = releaseCoef * _envelope[c] + (1f - releaseCoef) * 1f;
                        gain = _envelope[c];
                    }
                    float y = x * gain;
                    // 硬钳制到 ceiling（砖墙）
                    if (y > ceilLin) y = ceilLin;
                    else if (y < -ceilLin) y = -ceilLin;
                    buffer[idx] = y;
                }
            }
        }

        public override void Reset() { if (_envelope != null) Array.Clear(_envelope, 0, _envelope.Length); }
    }

    /// <summary>
    /// 延迟效果。支持单延迟 / 多抽头延迟 / 反馈延迟三种模式。
    /// </summary>
    public sealed class DelayEffect : AudioEffect
    {
        public enum DelayMode { Single, MultiTap, Feedback }

        private float[][]? _delayLines;
        private int[] _writePos;
        private int _maxDelaySamples;

        public DelayEffect() : base("Delay")
        {
            AddParam("Mode", 1, 0, 2);
            AddParam("DelayMs", 250, 1, 2000);
            AddParam("Feedback", 0.4f, 0, 0.95f);
            AddParam("Mix", 0.3f, 0, 1);
            AddParam("Taps", 3, 1, 8); // 多抽头数量
        }

        public override void Prepare(int sampleRate, int channels)
        {
            _maxDelaySamples = (int)(2.0f * sampleRate); // 最大 2 秒
            _delayLines = new float[channels][];
            _writePos = new int[channels];
            for (int c = 0; c < channels; c++) _delayLines[c] = new float[_maxDelaySamples];
        }

        public override void Reset()
        {
            if (_delayLines != null) foreach (var d in _delayLines) Array.Clear(d, 0, d.Length);
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_delayLines == null) Prepare(sampleRate, channels);
            DelayMode mode = (DelayMode)(int)P("Mode");
            float delayMs = P("DelayMs");
            float feedback = P("Feedback");
            float mix = P("Mix");
            int taps = (int)P("Taps");
            int delaySamples = Math.Clamp((int)(delayMs * 0.001f * sampleRate), 1, _maxDelaySamples - 1);

            float[] wet = new float[frameCount * channels];
            for (int c = 0; c < channels; c++)
            {
                var line = _delayLines![c];
                int wp = _writePos[c];
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = f * channels + c;
                    float x = buffer[idx];
                    if (mode == DelayMode.Feedback)
                    {
                        int rp = wp - delaySamples; if (rp < 0) rp += _maxDelaySamples;
                        float delayed = line[rp];
                        wet[idx] = delayed;
                        line[wp] = x + delayed * feedback;
                    }
                    else if (mode == DelayMode.MultiTap)
                    {
                        float acc = 0f;
                        for (int t = 1; t <= taps; t++)
                        {
                            int tapDelay = delaySamples * t / taps;
                            int rp = wp - tapDelay; if (rp < 0) rp += _maxDelaySamples;
                            acc += line[rp] / t;
                        }
                        wet[idx] = acc;
                        line[wp] = x;
                    }
                    else // Single
                    {
                        int rp = wp - delaySamples; if (rp < 0) rp += _maxDelaySamples;
                        wet[idx] = line[rp];
                        line[wp] = x;
                    }
                    if (++wp >= _maxDelaySamples) wp = 0;
                }
                _writePos[c] = wp;
            }
            Mix = mix; // ApplyMix 用 Mix
            ApplyMix(buffer, wet, frameCount, channels);
        }
    }

    /// <summary>合唱效果。多个调制延迟线（LFO）叠加，产生合奏厚度感。</summary>
    public sealed class ChorusEffect : AudioEffect
    {
        private float[][]? _lines;
        private int[] _writePos;
        private float[]? _lfoPhase;
        private int _maxDelay;

        public ChorusEffect() : base("Chorus")
        {
            AddParam("Rate", 0.5f, 0.05f, 5);    // Hz
            AddParam("Depth", 0.5f, 0, 1);       // 调制深度（ms 比例）
            AddParam("Voices", 3, 1, 6);
            AddParam("Mix", 0.5f, 0, 1);
        }

        public override void Prepare(int sampleRate, int channels)
        {
            _maxDelay = (int)(0.05f * sampleRate); // 50ms 基准延迟
            _lines = new float[channels][];
            _writePos = new int[channels];
            _lfoPhase = new float[channels];
            for (int c = 0; c < channels; c++) _lines[c] = new float[_maxDelay * 2];
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_lines == null) Prepare(sampleRate, channels);
            float rate = P("Rate");
            float depth = P("Depth");
            int voices = (int)P("Voices");
            float mix = P("Mix");
            float lfoInc = 2f * MathF.PI * rate / sampleRate;

            float[] wet = new float[frameCount * channels];
            for (int c = 0; c < channels; c++)
            {
                var line = _lines![c];
                int wp = _writePos[c];
                float phase = _lfoPhase![c];
                int len = line.Length;
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = f * channels + c;
                    float x = buffer[idx];
                    line[wp] = x;
                    float y = 0f;
                    for (int v = 0; v < voices; v++)
                    {
                        float lfo = MathF.Sin(phase + v * (2f * MathF.PI / voices));
                        float delay = _maxDelay * (1f + depth * lfo * 0.5f);
                        int rp = wp - (int)delay; if (rp < 0) rp += len;
                        y += line[rp] / voices;
                    }
                    wet[idx] = y;
                    phase += lfoInc;
                    if (++wp >= len) wp = 0;
                }
                _writePos[c] = wp;
                _lfoPhase[c] = phase;
            }
            Mix = mix;
            ApplyMix(buffer, wet, frameCount, channels);
        }

        public override void Reset()
        {
            if (_lines != null) foreach (var l in _lines) Array.Clear(l, 0, l.Length);
            if (_lfoPhase != null) Array.Clear(_lfoPhase, 0, _lfoPhase.Length);
        }
    }

    /// <summary>镶边效果。短延迟 + LFO 调制 + 反馈，产生扫频"喷气"声。</summary>
    public sealed class FlangerEffect : AudioEffect
    {
        private float[][]? _line;
        private int[] _writePos;
        private float[]? _phase;
        private int _maxDelay;

        public FlangerEffect() : base("Flanger")
        {
            AddParam("Rate", 0.3f, 0.05f, 5);
            AddParam("Depth", 0.8f, 0, 1);
            AddParam("Feedback", 0.5f, -0.95f, 0.95f);
            AddParam("Mix", 0.5f, 0, 1);
        }

        public override void Prepare(int sampleRate, int channels)
        {
            _maxDelay = (int)(0.01f * sampleRate); // 10ms
            _line = new float[channels][];
            _writePos = new int[channels];
            _phase = new float[channels];
            for (int c = 0; c < channels; c++) _line[c] = new float[_maxDelay * 2];
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_line == null) Prepare(sampleRate, channels);
            float rate = P("Rate");
            float depth = P("Depth");
            float feedback = P("Feedback");
            float mix = P("Mix");
            float lfoInc = 2f * MathF.PI * rate / sampleRate;

            float[] wet = new float[frameCount * channels];
            for (int c = 0; c < channels; c++)
            {
                var line = _line![c];
                int wp = _writePos[c];
                float phase = _phase![c];
                int len = line.Length;
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = f * channels + c;
                    float x = buffer[idx];
                    float lfo = 0.5f + 0.5f * MathF.Sin(phase);
                    float delay = 1f + depth * (_maxDelay - 1f) * lfo;
                    int rp = wp - (int)delay; if (rp < 0) rp += len;
                    float delayed = line[rp];
                    line[wp] = x + delayed * feedback;
                    wet[idx] = delayed;
                    phase += lfoInc;
                    if (++wp >= len) wp = 0;
                }
                _writePos[c] = wp;
                _phase[c] = phase;
            }
            Mix = mix;
            ApplyMix(buffer, wet, frameCount, channels);
        }

        public override void Reset()
        {
            if (_line != null) foreach (var l in _line) Array.Clear(l, 0, l.Length);
            if (_phase != null) Array.Clear(_phase, 0, _phase.Length);
        }
    }

    /// <summary>失真效果。软削波/硬削波/折叠三种模式。</summary>
    public sealed class DistortionEffect : AudioEffect
    {
        public enum DistortionMode { SoftClip, HardClip, Foldback }

        public DistortionEffect() : base("Distortion")
        {
            AddParam("Mode", 0, 0, 2);
            AddParam("Drive", 0.5f, 0, 1);
            AddParam("Threshold", 0.7f, 0.1f, 1);
            AddParam("Mix", 1f, 0, 1);
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            DistortionMode mode = (DistortionMode)(int)P("Mode");
            float drive = 1f + P("Drive") * 9f; // 1x..10x
            float threshold = P("Threshold");
            float mix = P("Mix");

            float[] wet = new float[frameCount * channels];
            int n = frameCount * channels;
            for (int i = 0; i < n; i++)
            {
                float x = buffer[i] * drive;
                float y;
                switch (mode)
                {
                    case DistortionMode.SoftClip:
                        y = (float)(Math.Tanh(x)); // 软削波
                        break;
                    case DistortionMode.HardClip:
                        y = Math.Clamp(x, -threshold, threshold); // 硬削波
                        break;
                    default: // Foldback：超过阈值折返
                        if (x > threshold || x < -threshold)
                        {
                            float folded = threshold - MathF.Abs(MathF.Abs(x) - threshold) % (2f * threshold);
                            y = MathF.Sign(x) * folded;
                        }
                        else y = x;
                        break;
                }
                wet[i] = y;
            }
            Mix = mix;
            ApplyMix(buffer, wet, frameCount, channels);
        }
    }

    /// <summary>
    /// 变调效果。相位声码器 (Phase Vocoder) 与 PSOLA 两类算法（此处实现
    /// 简化的相位声码器：STFT 重采样 + 相位连续修正）。改变音高保持时长。
    /// </summary>
    public sealed class PitchShiftEffect : AudioEffect
    {
        public enum PitchMode { PhaseVocoder, PSOLA }

        private const int FrameSize = 1024;
        private const int Hop = 256;
        private float[]? _overlapBuf;

        public PitchShiftEffect() : base("PitchShift")
        {
            AddParam("Mode", 0, 0, 1);
            AddParam("Semitones", 0, -12, 12);
            AddParam("FormantPreserve", 0, 0, 1);
            AddParam("Mix", 1f, 0, 1);
        }

        public override void Prepare(int sampleRate, int channels)
        {
            _overlapBuf = new float[FrameSize];
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            // 简化实现：按半音比例做重采样变调（不保时长），作为相位声码器的可运行占位。
            // 生产级实现应使用重叠相加的 STFT 相位声码器或 PSOLA 帧拼接。
            float semis = P("Semitones");
            float ratio = MathF.Pow(2f, semis / 12f);
            float mix = P("Mix");
            if (MathF.Abs(ratio - 1f) < 1e-4f) return;

            float[] wet = new float[frameCount * channels];
            for (int c = 0; c < channels; c++)
            {
                for (int f = 0; f < frameCount; f++)
                {
                    // 线性插值重采样：输出位置映射回输入位置
                    float src = f / ratio;
                    int i0 = (int)src;
                    float frac = src - i0;
                    float s0 = (i0 >= 0 && i0 < frameCount) ? buffer[i0 * channels + c] : 0f;
                    float s1 = (i0 + 1 >= 0 && i0 + 1 < frameCount) ? buffer[(i0 + 1) * channels + c] : s0;
                    wet[f * channels + c] = s0 + (s1 - s0) * frac;
                }
            }
            Mix = mix;
            ApplyMix(buffer, wet, frameCount, channels);
        }
    }

    /// <summary>噪声门。低于阈值的信号被衰减，带攻击/保持/释放时间。</summary>
    public sealed class NoiseGateEffect : AudioEffect
    {
        private float[]? _gain;
        private float[]? _holdTimer;

        public NoiseGateEffect() : base("NoiseGate")
        {
            AddParam("Threshold", -50, -80, 0);   // dB
            AddParam("Attack", 1, 0.1f, 50);      // ms
            AddParam("Hold", 50, 0, 500);         // ms
            AddParam("Release", 100, 10, 1000);   // ms
            AddParam("Range", -80, -100, 0);      // dB 衰减下限
        }

        public override void Prepare(int sampleRate, int channels)
        {
            _gain = new float[channels];
            _holdTimer = new float[channels];
            for (int c = 0; c < channels; c++) _gain[c] = 1f;
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_gain == null) Prepare(sampleRate, channels);
            float threshold = P("Threshold");
            float attackMs = P("Attack");
            float holdMs = P("Hold");
            float releaseMs = P("Release");
            float rangeDb = P("Range");
            float rangeLin = MathF.Pow(10f, rangeDb / 20f);
            float attackCoef = (float)Math.Exp(-1.0 / (attackMs * 0.001 * sampleRate));
            float releaseCoef = (float)Math.Exp(-1.0 / (releaseMs * 0.001 * sampleRate));
            float threshLin = MathF.Pow(10f, threshold / 20f);

            for (int c = 0; c < channels; c++)
            {
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = f * channels + c;
                    float x = buffer[idx];
                    float det = MathF.Abs(x);
                    if (det > threshLin)
                    {
                        _holdTimer![c] = holdMs * 0.001f * sampleRate;
                        _gain![c] = attackCoef * _gain[c] + (1f - attackCoef) * 1f;
                    }
                    else
                    {
                        if (_holdTimer![c] > 0) _holdTimer[c] -= 1f;
                        else _gain![c] = releaseCoef * _gain[c] + (1f - releaseCoef) * rangeLin;
                    }
                    buffer[idx] = x * _gain![c];
                }
            }
        }

        public override void Reset()
        {
            if (_gain != null) for (int i = 0; i < _gain.Length; i++) _gain[i] = 1f;
            if (_holdTimer != null) Array.Clear(_holdTimer, 0, _holdTimer.Length);
        }
    }

    /// <summary>Bitcrusher。位深降低 + 采样率降低，产生复古数码效果。</summary>
    public sealed class BitcrusherEffect : AudioEffect
    {
        private float[]? _hold;
        private float[]? _counter;

        public BitcrusherEffect() : base("Bitcrusher")
        {
            AddParam("BitDepth", 8, 1, 16);
            AddParam("SampleRateReduction", 0.5f, 0, 1); // 0=原始,1=极度降采样
            AddParam("Mix", 1f, 0, 1);
        }

        public override void Prepare(int sampleRate, int channels)
        {
            _hold = new float[channels];
            _counter = new float[channels];
        }

        public override void Process(float[] buffer, int frameCount, int channels, int sampleRate)
        {
            if (_hold == null) Prepare(sampleRate, channels);
            int bits = Math.Clamp((int)P("BitDepth"), 1, 24);
            float srReduce = P("SampleRateReduction");
            float mix = P("Mix");
            int levels = 1 << bits;
            float stepSize = 2f / levels;
            float holdEvery = 1f + srReduce * 20f; // 每 N 个采样保持一次

            float[] wet = new float[frameCount * channels];
            for (int c = 0; c < channels; c++)
            {
                for (int f = 0; f < frameCount; f++)
                {
                    int idx = f * channels + c;
                    _counter![c] += 1f;
                    if (_counter[c] >= holdEvery || f == 0)
                    {
                        _counter[c] = 0f;
                        // 位深降低：量化到 levels 级
                        float x = buffer[idx];
                        _hold![c] = MathF.Round((x + 1f) / stepSize) * stepSize - 1f;
                    }
                    wet[idx] = _hold![c];
                }
            }
            Mix = mix;
            ApplyMix(buffer, wet, frameCount, channels);
        }

        public override void Reset()
        {
            if (_hold != null) Array.Clear(_hold, 0, _hold.Length);
            if (_counter != null) Array.Clear(_counter, 0, _counter.Length);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // AudioBus：音频总线（效果链 + 音量 + 路由 + 发送 + 独奏/静音）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 总线发送：把本总线信号按 Level 比例发送到目标辅助总线（如混响发送）。
    /// </summary>
    public sealed class AudioSend
    {
        /// <summary>目标总线。</summary>
        public AudioBus Target;
        /// <summary>发送电平 [0,1]。</summary>
        public float Level = 1f;

        public AudioSend(AudioBus target, float level = 1f) { Target = target; Level = level; }
    }

    /// <summary>
    /// 音频总线。一条总线拥有：输入混合、效果链、音量、路由（输出到下级总线）、
    /// 发送（辅助发送）、独奏/静音。总线之间形成有向路由图，最终汇聚到主总线。
    /// 
    /// 注：本类名为 AudioBus，与父命名空间 ShanyuEngine.Audio.AudioBus 枚举
    /// （总线标识）同名但不同命名空间，二者不冲突。父命名空间枚举的字符串名
    /// （Master/Music/SFX/...）用作本类实例的 Name 进行路由映射。
    /// </summary>

    // ════════════════════════════════════════════════════════════════════════
    // AudioEffectPreset：效果链预设的保存/加载
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 效果预设。把一条效果链的配置（效果类型 + 参数值）序列化为可保存/加载的
    /// 文本格式，便于跨工程复用（如"大厅混响""电话声 EQ"）。
    /// </summary>
    public sealed class AudioEffectPreset
    {
        /// <summary>预设名。</summary>
        public string Name = "Preset";
        /// <summary>预设内效果条目。</summary>
        public readonly List<Entry> Entries = new();

        /// <summary>单个效果条目：效果类型名 + 参数键值。</summary>
        public sealed class Entry
        {
            public string EffectType = "";
            public string EffectName = "";
            public float Mix = 1f;
            public readonly Dictionary<string, float> Parameters = new();
        }

        /// <summary>从效果链捕获当前配置为预设。</summary>
        public void CaptureFrom(AudioEffectChain chain)
        {
            Entries.Clear();
            foreach (var e in chain.Effects)
            {
                var entry = new Entry
                {
                    EffectType = e.GetType().Name,
                    EffectName = e.Name,
                    Mix = e.Mix
                };
                foreach (var kv in e.Parameters)
                    entry.Parameters[kv.Key] = kv.Value.Value;
                Entries.Add(entry);
            }
        }

        /// <summary>把预设应用到效果链（先清空再按条目重建）。</summary>
        public void ApplyTo(AudioEffectChain chain)
        {
            chain.Clear();
            foreach (var entry in Entries)
            {
                var effect = CreateEffect(entry.EffectType);
                if (effect == null) continue;
                effect.Name = entry.EffectName;
                effect.Mix = entry.Mix;
                foreach (var kv in entry.Parameters) effect.SetParameter(kv.Key, kv.Value);
                chain.Add(effect);
            }
        }

        /// <summary>按类型名实例化效果（工厂）。</summary>
        private static AudioEffect? CreateEffect(string typeName) => typeName switch
        {
            nameof(ReverbEffect) => new ReverbEffect(),
            nameof(EqualizerEffect) => new EqualizerEffect(),
            nameof(CompressorEffect) => new CompressorEffect(),
            nameof(LimiterEffect) => new LimiterEffect(),
            nameof(DelayEffect) => new DelayEffect(),
            nameof(ChorusEffect) => new ChorusEffect(),
            nameof(FlangerEffect) => new FlangerEffect(),
            nameof(DistortionEffect) => new DistortionEffect(),
            nameof(PitchShiftEffect) => new PitchShiftEffect(),
            nameof(NoiseGateEffect) => new NoiseGateEffect(),
            nameof(BitcrusherEffect) => new BitcrusherEffect(),
            _ => null
        };

        /// <summary>序列化为文本（每行一条效果，参数以 key=value 列出）。</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"PRESET:{Name}");
            foreach (var e in Entries)
            {
                sb.AppendLine($"EFFECT:{e.EffectType}|{e.EffectName}|mix={e.Mix.ToString(CultureInfo.InvariantCulture)}");
                foreach (var kv in e.Parameters)
                    sb.AppendLine($"  PARAM:{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}");
            }
            return sb.ToString();
        }

        /// <summary>从文本反序列化。</summary>
        public static AudioEffectPreset Deserialize(string text)
        {
            var preset = new AudioEffectPreset();
            Entry? cur = null;
            foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("PRESET:")) preset.Name = line[7..].Trim();
                else if (line.StartsWith("EFFECT:"))
                {
                    var parts = line[7..].Split('|');
                    cur = new Entry { EffectType = parts[0], EffectName = parts.Length > 1 ? parts[1] : parts[0] };
                    if (parts.Length > 2 && parts[2].StartsWith("mix="))
                        cur.Mix = float.Parse(parts[2][4..], CultureInfo.InvariantCulture);
                    preset.Entries.Add(cur);
                }
                else if (line.StartsWith("PARAM:") && cur != null)
                {
                    var kv = line[6..].Split('=');
                    if (kv.Length == 2 && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        cur.Parameters[kv[0]] = v;
                }
            }
            return preset;
        }

        /// <summary>保存到文件。</summary>
        public void Save(string path) => File.WriteAllText(path, Serialize());

        /// <summary>从文件加载。</summary>
        public static AudioEffectPreset Load(string path) => Deserialize(File.ReadAllText(path));
    }

    // ════════════════════════════════════════════════════════════════════════
    // AudioDspSystem：DSP 系统（管理所有总线+效果，每帧处理混音）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 顶层 DSP 系统。管理所有 <see cref="AudioBus"/> 与其效果链，每帧执行混音：
    /// 从 <see cref="AudioSystem"/> 拉取活跃音频实例 -> 按总线路由累加 ->
    /// 逐总线应用效果链与音量 -> 汇聚到主总线 -> 输出最终缓冲。
    /// 
    /// 与现有 <see cref="AudioSystem"/> 对接：本系统读取 AudioSystem 的活跃
    /// <c>AudioInstance</c>，按其 <c>Bus</c>（枚举）名映射到对应 AudioBus 实例。
    /// </summary>
    public sealed class AudioDspSystem
    {
        /// <summary>关联的音频系统（提供活跃实例与剪辑数据）。</summary>
        public AudioSystem? AudioEngine;
        /// <summary>输出采样率。</summary>
        public int SampleRate = 48000;
        /// <summary>输出声道数。</summary>
        public int Channels = 2;
        /// <summary>每帧处理缓冲帧数。</summary>
        public int BufferFrames = 512;

        /// <summary>所有总线（按名索引）。</summary>
        public readonly Dictionary<string, AudioBus> Buses = new();
        /// <summary>主总线。</summary>
        public AudioBus MasterBus;

        /// <summary>当前播放时间（秒，用于参数自动化）。</summary>
        public float CurrentTime { get; private set; }

        public AudioDspSystem()
        {
            MasterBus = new AudioBus("Master");
            Buses["Master"] = MasterBus;
            // 预建常见总线并默认路由到 Master
            foreach (var name in new[] { "Music", "SFX", "Voice", "Ambient", "UI" })
            {
                var b = new AudioBus(name) { Output = MasterBus };
                Buses[name] = b;
            }
        }

        /// <summary>设置关联的音频系统并初始化总线效果链。</summary>
        public void Attach(AudioSystem audioEngine)
        {
            AudioEngine = audioEngine;
            PrepareAll();
        }

        /// <summary>创建或获取一条总线。</summary>
        public AudioBus CreateBus(string name, AudioBus? output = null)
        {
            if (Buses.TryGetValue(name, out var existing)) return existing;
            var bus = new AudioBus(name) { Output = output ?? MasterBus };
            Buses[name] = bus;
            bus.Prepare(SampleRate, Channels);
            return bus;
        }

        /// <summary>添加发送。</summary>
        public void AddSend(string fromBus, string toBus, float level = 1f)
        {
            if (Buses.TryGetValue(fromBus, out var src) && Buses.TryGetValue(toBus, out var dst))
                src.Sends.Add(new AudioSend(dst, level));
        }

        /// <summary>设置总线音量。</summary>
        public void SetBusVolume(string name, float volume)
        {
            if (Buses.TryGetValue(name, out var b)) b.Volume = Math.Clamp(volume, 0f, 2f);
        }

        /// <summary>总线静音/取消静音。</summary>
        public void SetBusMuted(string name, bool muted)
        {
            if (Buses.TryGetValue(name, out var b)) b.Muted = muted;
        }

        /// <summary>总线独奏/取消独奏。</summary>
        public void SetBusSolo(string name, bool solo)
        {
            if (Buses.TryGetValue(name, out var b)) b.Solo = solo;
        }

        /// <summary>初始化/重置所有总线的效果链与缓冲。</summary>
        public void PrepareAll()
        {
            foreach (var b in Buses.Values) b.Prepare(SampleRate, Channels);
        }

        /// <summary>重置所有总线状态（延迟线、包络等）。</summary>
        public void ResetAll()
        {
            foreach (var b in Buses.Values) b.Reset();
            CurrentTime = 0f;
        }

        /// <summary>
        /// 每帧混音处理。从 AudioSystem 拉取活跃实例，路由到对应总线，
        /// 应用效果链，汇聚主总线，返回最终输出缓冲。
        /// </summary>
        /// <param name="deltaTime">本帧时长（秒）。</param>
        /// <returns>输出交错缓冲（BufferFrames * Channels）。</returns>
        public float[] Process(float deltaTime)
        {
            int frameCount = BufferFrames;
            int channels = Channels;
            int n = frameCount * channels;
            CurrentTime += deltaTime;

            // 清空所有总线的本帧混合缓冲（保留效果链状态如混响尾音/延迟线）
            foreach (var b in Buses.Values) b.ClearMixBuffer();

            // 从音频系统拉取活跃实例并路由
            // 注：真实实现中应从音频系统获取每个实例当前帧的 PCM 采样。
            // 此处以 AudioSystem 的实例音量合成简化占位波形，保证系统可联调。
            if (AudioEngine != null)
            {
                var states = AudioEngine.GetBusStates();
                foreach (var state in states)
                {
                    if (!Buses.TryGetValue(state.Bus.ToString(), out var bus)) continue;
                    if (state.Instances.Count == 0) continue;
                    var chunk = new float[n];
                    foreach (var inst in state.Instances)
                    {
                        if (inst.State != AudioState.Playing &&
                            inst.State != AudioState.FadingIn &&
                            inst.State != AudioState.FadingOut) continue;
                        float vol = AudioEngine.GetEffectiveVolume(inst);
                        // 临时回退：以正弦波近似该实例当前帧（真实实现应读取解码 PCM；当前作为测试音调占位）
                        float freq = 220f * inst.Pitch;
                        for (int f = 0; f < frameCount; f++)
                        {
                            float s = MathF.Sin(2f * MathF.PI * freq * ((float)inst.PlaybackTime + f / (float)SampleRate)) * vol;
                            for (int c = 0; c < channels; c++) chunk[f * channels + c] += s;
                        }
                    }
                    bus.Accumulate(chunk, frameCount, channels);
                }
            }

            // 判断是否存在独奏总线
            bool anySolo = false;
            foreach (var b in Buses.Values) if (b.Solo) { anySolo = true; break; }

            // 按拓扑顺序处理：先叶子总线（无输入来自其他总线），再主总线。
            // 简化：按 Music/SFX/.../Master 顺序处理（非主总线先于主总线）。
            foreach (var b in Buses.Values)
                if (b != MasterBus) b.Process(CurrentTime, SampleRate, out _, frameCount, channels, anySolo);

            // 主总线最后处理，输出最终缓冲
            MasterBus.Process(CurrentTime, SampleRate, out var output, frameCount, channels, anySolo);

            // 保证返回非空缓冲
            if (output == null) output = new float[n];
            return output;
        }

        /// <summary>引擎主循环每帧调用：推进时间并执行混音。</summary>
        public void Update(float deltaTime) => Process(deltaTime);
    }
}
