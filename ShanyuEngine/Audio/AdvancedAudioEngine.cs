// ==============================================================================
//  山雨引擎 ShanyuEngine — 高级音频引擎 (Advanced Audio Engine)
//  文件：AdvancedAudioEngine.cs
//  命名空间：ShanyuEngine.Audio
//  声明：using ShanyuEngine.ECS 仅用于 ECS 集成（实体音频源同步）
// -----------------------------------------------------------------------------
//  本文件覆盖以下核心模块：
//
//    一、HRTF 双耳渲染  —— 头相关传输函数数据（方位角 x 仰角 x IR 采样）
//    二、DSP 效果器基类  —— Process/ProcessBuffer/Reset 统一接口
//    三、内置 DSP 效果器 —— Reverb / Equalizer / Compressor / Delay / Chorus
//    四、DSP 效果链       —— 有序节点列表，串联处理音频缓冲
//    五、AudioBus 体系   —— MasterBus -> SubMixBus -> AuxBus 树形路由
//    六、AudioBusGraph   —— 总线图拓扑管理，拓扑序混音
//    七、AudioBufferPool —— 音频缓冲池，零分配复用
//    八、SoundClass 体系  —— Master / SFX / Music / UI / Voice / Ambient 独立控制
//    九、AudioMixer      —— 多声道混音、3D 空间化、距离衰减
//    十、音频遮挡与反射  —— 射线遮挡计算 + 反射模拟
//   十一、StreamingAudio —— 流式音频源（大文件 / 背景音乐）
//   十二、AudioCrossfader —— 交叉淡变器（线性 / 等功率 / Smoothstep）
//   十三、AudioBackend   —— 后端抽象 + Dummy 后端
//   十四、AdvancedAudioEngine —— 顶层引擎：实例管理、混音渲染、ECS 同步
//   十五、并发播放限制   —— 按总线 / 类型限流，超限按优先级裁剪
//
//  对接关系：
//    * ShanyuEngine.Audio.AudioSystem / AudioInstance / AudioClip —— 基础类型
//    * ShanyuEngine.ECS.EntityComponentSystem / IComponent / TransformComponent
//    * ShanyuEngine.Core.AudioConfig —— 引擎配置
//
//  设计原则：
//    * 所有公共类型均在此命名空间下，不依赖外部音频库
//    * AudioBus (枚举, AudioSystem.cs) 与 AdvancedAudioBus (类, 本文件) 并存
//    * DSP 效果器在本文件中提供完整算法实现（Schroeder 混响 / Biquad EQ / ...）
// ==============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Audio
{
    // ==============================================================================
    //  一、HRTF 双耳渲染数据
    // ==============================================================================

    /// <summary>
    /// HRTF（Head-Related Transfer Function，头相关传输函数）数据。
    /// 用于实现 3D 双耳渲染：根据声源相对于听者的方位角和仰角，
    /// 应用对应的脉冲响应（IR）来模拟声音到达左右耳的延迟和频率差异。
    /// 数据通常来自 MIT KEMAR 或 SADIE 等测量数据库。
    /// 本实现使用简化的合成模型（球头模型 ITD/ILD 模拟）。
    /// </summary>
    public class HRTFData
    {
        /// <summary>方位角数量（通常 72 = 每 5 度一个采样）。</summary>
        public int AzimuthCount = 72;
        /// <summary>仰角数量（通常 14，从 -45 到 +90 度）。</summary>
        public int ElevationCount = 14;
        /// <summary>脉冲响应长度（采样数，通常 128）。</summary>
        public int IRLength = 128;
        /// <summary>左耳脉冲响应表 [方位角索引, 仰角索引, 采样]。</summary>
        public float[,,] LeftIR;
        /// <summary>右耳脉冲响应表 [方位角索引, 仰角索引, 采样]。</summary>
        public float[,,] RightIR;
        /// <summary>采样率。</summary>
        public int SampleRate = 48000;
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => LeftIR != null && RightIR != null;

        /// <summary>
        /// 初始化 HRTF 数据。使用简化的合成模型（基于球头模型的基本 ITD/ILD 模拟）。
        /// 实际项目中应从 KEMAR 等测量数据文件加载。
        /// </summary>
        public void Initialize()
        {
            LeftIR = new float[AzimuthCount, ElevationCount, IRLength];
            RightIR = new float[AzimuthCount, ElevationCount, IRLength];

            for (int az = 0; az < AzimuthCount; az++)
            {
                float azimuth = az * (360f / AzimuthCount) * MathF.PI / 180f;
                for (int el = 0; el < ElevationCount; el++)
                {
                    float elevation = (el - ElevationCount / 2f) * 15f * MathF.PI / 180f;

                    // ITD（双耳时间差）：最大约 0.6ms，与方位角余弦相关
                    float itdSamples = (SampleRate * 0.0006f) * MathF.Cos(elevation) * MathF.Cos(azimuth);
                    // ILD（双耳声级差）：高频侧衰减，与方位角相关
                    float leftGain = 1f - 0.3f * (1f + MathF.Cos(azimuth)) * 0.5f;
                    float rightGain = 1f - 0.3f * (1f - MathF.Cos(azimuth)) * 0.5f;

                    for (int i = 0; i < IRLength; i++)
                    {
                        float leftDelay = MathF.Max(0, i + itdSamples);
                        if (leftDelay < IRLength)
                        {
                            float envelope = MathF.Exp(-leftDelay * 0.05f) * leftGain;
                            LeftIR[az, el, i] = envelope * MathF.Sin(leftDelay * 0.3f);
                        }

                        float rightDelay = MathF.Max(0, i - itdSamples);
                        if (rightDelay < IRLength)
                        {
                            float envelope = MathF.Exp(-rightDelay * 0.05f) * rightGain;
                            RightIR[az, el, i] = envelope * MathF.Sin(rightDelay * 0.3f);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 根据方位角和仰角获取对应的左右耳脉冲响应。
        /// </summary>
        /// <param name="azimuthDeg">方位角（度，0=正前方）。</param>
        /// <param name="elevationDeg">仰角（度，0=水平面）。</param>
        /// <returns>左右耳脉冲响应数组。</returns>
        public (float[] leftIR, float[] rightIR) GetIR(float azimuthDeg, float elevationDeg)
        {
            if (!IsInitialized) Initialize();

            int azIdx = (int)(azimuthDeg / 360f * AzimuthCount) % AzimuthCount;
            if (azIdx < 0) azIdx += AzimuthCount;

            int elIdx = (int)((elevationDeg + 90f) / 180f * ElevationCount);
            elIdx = Math.Clamp(elIdx, 0, ElevationCount - 1);

            var leftIR = new float[IRLength];
            var rightIR = new float[IRLength];
            for (int i = 0; i < IRLength; i++)
            {
                leftIR[i] = LeftIR[azIdx, elIdx, i];
                rightIR[i] = RightIR[azIdx, elIdx, i];
            }
            return (leftIR, rightIR);
        }

        /// <summary>重置 HRTF 数据。</summary>
        public void Reset()
        {
            LeftIR = null;
            RightIR = null;
        }
    }

    // ==============================================================================
    //  二、DSP 效果器基类
    // ==============================================================================

    /// <summary>
    /// DSP 效果器基类。所有音频效果器都继承自此基类，
    /// 实现 Process（逐采样）和 ProcessBuffer（批量）方法。
    /// </summary>
    public abstract class DSPNode
    {
        /// <summary>效果器名称。</summary>
        public string Name = "";
        /// <summary>是否启用。</summary>
        public bool IsEnabled = true;
        /// <summary>干湿混合比例 [0, 1]（0=全干声，1=全效果声）。</summary>
        public float WetMix = 1f;
        /// <summary>采样率。</summary>
        protected int SampleRate = 48000;

        /// <summary>设置采样率。</summary>
        public virtual void SetSampleRate(int sampleRate) => SampleRate = sampleRate;

        /// <summary>处理单个采样。输入一个采样值，返回处理后的值。</summary>
        public abstract float Process(float input);

        /// <summary>
        /// 批量处理采样数组。默认实现逐采样调用 Process。
        /// 子类可重写以优化性能（如使用 SIMD）。
        /// </summary>
        public virtual void ProcessBuffer(float[] buffer, int channels)
        {
            if (!IsEnabled) return;
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = Process(buffer[i]);
        }

        /// <summary>重置效果器内部状态（清除延迟线、缓冲等）。</summary>
        public virtual void Reset() { }
    }

    // ==============================================================================
    //  三、内置 DSP 效果器
    // ==============================================================================

    /// <summary>
    /// 混响效果器。Schroeder 混响结构：8 并联梳状滤波器 + 4 串联全通滤波器。
    /// </summary>
    public class ReverbEffect : DSPNode
    {
        /// <summary>房间大小 [0, 1]。</summary>
        public float RoomSize = 0.5f;
        /// <summary>阻尼 [0, 1]（高频衰减程度）。</summary>
        public float Damping = 0.5f;
        /// <summary>混响衰减时间（秒）。</summary>
        public float DecayTime = 2f;
        /// <summary>预延迟（毫秒）。</summary>
        public float PreDelayMs = 20f;

        // Schroeder 混响延迟线（采样数 @ 44100Hz，按实际采样率缩放）
        private readonly float[] _combDelays = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private float[] _combFeedback = new float[8];
        private float[][] _combBuffers;
        private int[] _combIndex;
        private readonly float[] _allpassDelays = { 556, 441, 341, 225 };
        private float[][] _allpassBuffers;
        private int[] _allpassIndex;
        private float[] _preDelayBuffer;
        private int _preDelayIndex;
        private int _preDelaySamples;

        public ReverbEffect()
        {
            Name = "Reverb";
            int n = _combDelays.Length;
            _combBuffers = new float[n][];
            _combIndex = new int[n];
            _combFeedback = new float[n];
            for (int i = 0; i < n; i++)
            {
                _combBuffers[i] = new float[_combDelays[i]];
                _combFeedback[i] = 0.84f;
            }
            int m = _allpassDelays.Length;
            _allpassBuffers = new float[m][];
            _allpassIndex = new int[m];
            for (int i = 0; i < m; i++)
                _allpassBuffers[i] = new float[_allpassDelays[i]];
        }

        public override void SetSampleRate(int sampleRate)
        {
            base.SetSampleRate(sampleRate);
            UpdateParameters();
        }

        private void UpdateParameters()
        {
            float scale = SampleRate / 44100f;
            float decayFactor = 0.7f + RoomSize * 0.28f;
            for (int i = 0; i < _combFeedback.Length; i++)
                _combFeedback[i] = Math.Clamp(decayFactor - Damping * 0.1f, 0f, 0.98f);

            _preDelaySamples = Math.Max(1, (int)(PreDelayMs * 0.001f * SampleRate));
            if (_preDelayBuffer == null || _preDelayBuffer.Length < _preDelaySamples)
            {
                _preDelayBuffer = new float[_preDelaySamples];
                _preDelayIndex = 0;
            }
        }

        public override float Process(float input)
        {
            if (!IsEnabled) return input;

            // 预延迟
            float delayed = _preDelayBuffer[_preDelayIndex];
            _preDelayBuffer[_preDelayIndex] = input;
            _preDelayIndex = (_preDelayIndex + 1) % _preDelaySamples;

            // 并联梳状滤波器
            float wet = 0f;
            for (int i = 0; i < _combBuffers.Length; i++)
            {
                float output = _combBuffers[i][_combIndex[i]];
                float newSample = delayed + output * _combFeedback[i];
                _combBuffers[i][_combIndex[i]] = newSample;
                _combIndex[i] = (_combIndex[i] + 1) % _combBuffers[i].Length;
                wet += output;
            }
            wet /= _combBuffers.Length;

            // 串联全通滤波器
            for (int i = 0; i < _allpassBuffers.Length; i++)
            {
                float apDelayed = _allpassBuffers[i][_allpassIndex[i]];
                float output = -wet + apDelayed;
                _allpassBuffers[i][_allpassIndex[i]] = wet + apDelayed * 0.5f;
                _allpassIndex[i] = (_allpassIndex[i] + 1) % _allpassBuffers[i].Length;
                wet = output;
            }

            return input * (1f - WetMix) + wet * WetMix;
        }

        public override void Reset()
        {
            foreach (var buf in _combBuffers) Array.Clear(buf, 0, buf.Length);
            Array.Clear(_combIndex, 0, _combIndex.Length);
            foreach (var buf in _allpassBuffers) Array.Clear(buf, 0, buf.Length);
            Array.Clear(_allpassIndex, 0, _allpassIndex.Length);
            if (_preDelayBuffer != null) Array.Clear(_preDelayBuffer, 0, _preDelayBuffer.Length);
            _preDelayIndex = 0;
        }
    }

    /// <summary>
    /// 均衡器效果器。多频段参数均衡，使用双二阶（Biquad）滤波器实现各频段。
    /// </summary>
    public class EqualizerEffect : DSPNode
    {
        public class EQBand
        {
            public float Frequency;
            public float Gain = 0f;
            public float Q = 1f;
            public EQFilterType Type;
            // Biquad 系数
            public float A0, A1, A2, B1, B2;
            public float Z1, Z2;
        }

        public enum EQFilterType { LowShelf, Peak, HighShelf, LowPass, HighPass }

        /// <summary>频段列表。</summary>
        public readonly List<EQBand> Bands = new();

        public EqualizerEffect()
        {
            Name = "EQ";
            float[] freqs = { 60, 120, 250, 500, 1000, 2000, 4000, 8000, 12000, 16000 };
            foreach (var f in freqs)
                Bands.Add(new EQBand { Frequency = f, Type = EQFilterType.Peak, Q = 1.41f });
        }

        /// <summary>更新指定频段的 Biquad 系数。</summary>
        public void UpdateBandCoefficients(int index)
        {
            if (index < 0 || index >= Bands.Count) return;
            var band = Bands[index];
            float w0 = 2f * MathF.PI * band.Frequency / SampleRate;
            float sinW0 = MathF.Sin(w0);
            float cosW0 = MathF.Cos(w0);
            float A = MathF.Pow(10f, band.Gain / 40f);
            float alpha = sinW0 / (2f * Math.Max(0.001f, band.Q));

            switch (band.Type)
            {
                case EQFilterType.Peak:
                    {
                        float alphaQ = sinW0 / (2f * Math.Max(0.001f, band.Q));
                        float a0 = 1f + alphaQ / A;
                        band.A0 = 1f / a0;
                        band.A1 = -2f * cosW0 / a0;
                        band.A2 = (1f - alphaQ / A) / a0;
                        band.B1 = 2f * cosW0 * band.A0;
                        band.B2 = (1f - alphaQ * A) * band.A0;
                        break;
                    }
                case EQFilterType.LowShelf:
                    {
                        float sq = 2f * MathF.Sqrt(A) * alpha;
                        float a0 = (A + 1f) - (A - 1f) * cosW0 + sq;
                        band.A0 = A * ((A + 1f) + (A - 1f) * cosW0 + sq) / a0;
                        band.A1 = 2f * A * ((A - 1f) - (A + 1f) * cosW0) / a0;
                        band.A2 = A * ((A + 1f) + (A - 1f) * cosW0 - sq) / a0;
                        band.B1 = 2f * ((A - 1f) + (A + 1f) * cosW0) / a0;
                        band.B2 = ((A + 1f) - (A - 1f) * cosW0 - sq) / a0;
                        break;
                    }
                case EQFilterType.HighShelf:
                    {
                        float sq = 2f * MathF.Sqrt(A) * alpha;
                        float a0 = (A + 1f) + (A - 1f) * cosW0 + sq;
                        band.A0 = A * ((A + 1f) - (A - 1f) * cosW0 + sq) / a0;
                        band.A1 = -2f * A * ((A - 1f) - (A + 1f) * cosW0) / a0;
                        band.A2 = A * ((A + 1f) - (A - 1f) * cosW0 - sq) / a0;
                        band.B1 = 2f * ((A - 1f) + (A + 1f) * cosW0) / a0;
                        band.B2 = ((A + 1f) + (A - 1f) * cosW0 - sq) / a0;
                        break;
                    }
                case EQFilterType.LowPass:
                    {
                        float a0 = 1f + alpha;
                        band.A0 = (1f - cosW0) / (2f * a0);
                        band.A1 = (1f - cosW0) / a0;
                        band.A2 = (1f - cosW0) / (2f * a0);
                        band.B1 = 2f * cosW0 / a0;
                        band.B2 = (1f - alpha) / a0;
                        break;
                    }
                case EQFilterType.HighPass:
                    {
                        float a0 = 1f + alpha;
                        band.A0 = (1f + cosW0) / (2f * a0);
                        band.A1 = -(1f + cosW0) / a0;
                        band.A2 = (1f + cosW0) / (2f * a0);
                        band.B1 = 2f * cosW0 / a0;
                        band.B2 = (1f - alpha) / a0;
                        break;
                    }
            }
        }

        /// <summary>更新所有频段的 Biquad 系数。</summary>
        public void UpdateAllBands()
        {
            for (int i = 0; i < Bands.Count; i++)
                UpdateBandCoefficients(i);
        }

        public override float Process(float input)
        {
            if (!IsEnabled) return input;
            float output = input;
            foreach (var band in Bands)
            {
                float y = band.A0 * output + band.A1 * band.Z1 + band.A2 * band.Z2
                        - band.B1 * output - band.B2 * band.Z1;
                band.Z2 = band.Z1;
                band.Z1 = output;
                output = y;
            }
            return output;
        }

        public override void Reset()
        {
            foreach (var band in Bands) { band.Z1 = 0; band.Z2 = 0; }
        }
    }

    /// <summary>
    /// 动态压缩器。基于 RMS 包络检测的动态范围压缩。
    /// </summary>
    public class CompressorEffect : DSPNode
    {
        /// <summary>阈值（dB）。</summary>
        public float Threshold = -20f;
        /// <summary>压缩比 [1, 20]。</summary>
        public float Ratio = 4f;
        /// <summary>启动时间（秒）。</summary>
        public float Attack = 0.01f;
        /// <summary>释放时间（秒）。</summary>
        public float Release = 0.1f;
        /// <summary>补偿增益（dB）。</summary>
        public float MakeupGain = 0f;
        /// <summary>软拐点半径（dB），0 = 硬拐点。</summary>
        public float KneeWidth = 6f;

        private float _envelope = 0f;
        private float _gainDb = 0f;

        public CompressorEffect() { Name = "Compressor"; }

        public override float Process(float input)
        {
            if (!IsEnabled) return input;

            float absIn = MathF.Abs(input);
            // RMS 包络跟随
            float coeff = absIn > _envelope
                ? 1f - MathF.Exp(-1f / (Math.Max(0.0001f, Attack) * SampleRate))
                : 1f - MathF.Exp(-1f / (Math.Max(0.0001f, Release) * SampleRate));
            _envelope += (absIn - _envelope) * coeff;

            float envDb = 20f * MathF.Log10(Math.Max(1e-6f, _envelope));
            float thresholdDb = Threshold;
            float kneeHalf = KneeWidth * 0.5f;

            // 计算增益衰减
            float gainDb;
            if (envDb < thresholdDb - kneeHalf)
                gainDb = 0f;
            else if (envDb > thresholdDb + kneeHalf)
                gainDb = -(envDb - thresholdDb) * (1f - 1f / Math.Max(1f, Ratio));
            else
            {
                // 软拐点二次插值
                float x = envDb - thresholdDb + kneeHalf;
                gainDb = -x * x / Math.Max(0.01f, 2f * kneeHalf) * (1f - 1f / Math.Max(1f, Ratio));
            }

            _gainDb += (gainDb - _gainDb) * coeff;
            float gainLinear = MathF.Pow(10f, (_gainDb + MakeupGain) / 20f);
            return input * gainLinear;
        }

        public override void Reset()
        {
            _envelope = 0f;
            _gainDb = 0f;
        }
    }

    /// <summary>
    /// 延迟效果器。带反馈的简单延迟线。
    /// </summary>
    public class DelayEffect : DSPNode
    {
        /// <summary>延迟时间（秒）。</summary>
        public float DelayTime = 0.3f;
        /// <summary>反馈量 [0, 0.95]。</summary>
        public float Feedback = 0.4f;
        /// <summary>低通截止频率（Hz），用于反馈路径音色控制。</summary>
        public float LpCutoff = 3000f;

        private float[] _delayBuffer;
        private int _delayIndex;
        private int _delaySamples;
        private float _lpState;

        public DelayEffect() { Name = "Delay"; }

        private void EnsureBuffer()
        {
            _delaySamples = Math.Max(1, (int)(DelayTime * SampleRate));
            if (_delayBuffer == null || _delayBuffer.Length < _delaySamples)
            {
                var newBuf = new float[_delaySamples];
                if (_delayBuffer != null)
                    Array.Copy(_delayBuffer, newBuf, Math.Min(_delayBuffer.Length, _delaySamples));
                _delayBuffer = newBuf;
                _delayIndex = 0;
            }
        }

        public override float Process(float input)
        {
            if (!IsEnabled) return input;
            EnsureBuffer();

            float delayed = _delayBuffer[_delayIndex];
            // 低通滤波反馈
            float lpCoeff = 2f * MathF.PI * LpCutoff / SampleRate;
            lpCoeff = Math.Clamp(lpCoeff, 0f, 1f);
            _lpState = delayed * lpCoeff + _lpState * (1f - lpCoeff);

            float output = input * (1f - WetMix) + delayed * WetMix;
            _delayBuffer[_delayIndex] = input + _lpState * Feedback;
            _delayIndex = (_delayIndex + 1) % _delaySamples;
            return output;
        }

        public override void Reset()
        {
            if (_delayBuffer != null) Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _delayIndex = 0;
            _lpState = 0f;
        }
    }

    /// <summary>
    /// 合唱效果器。使用多个略去调的延迟线产生合唱/镶边效果。
    /// </summary>
    public class ChorusEffect : DSPNode
    {
        /// <summary>调制速率（Hz）。</summary>
        public float Rate = 1.5f;
        /// <summary>调制深度（采样数偏移量）。</summary>
        public float Depth = 5f;
        /// <summary>反馈量 [0, 0.9]。</summary>
        public float Feedback = 0.3f;
        /// <summary>声音数量（并行延迟线）。</summary>
        public int Voices = 3;

        private float[][] _buffers;
        private int[] _indices;
        private int _bufferSize;
        private float _phase;

        public ChorusEffect()
        {
            Name = "Chorus";
            _buffers = new float[0][];
            _indices = new int[0];
        }

        private void EnsureBuffers()
        {
            _bufferSize = Math.Max(1, (int)(0.05f * SampleRate)); // 50ms 缓冲
            if (_buffers.Length != Voices)
            {
                _buffers = new float[Voices][];
                _indices = new int[Voices];
                for (int v = 0; v < Voices; v++)
                {
                    _buffers[v] = new float[_bufferSize];
                    _indices[v] = 0;
                }
            }
        }

        public override float Process(float input)
        {
            if (!IsEnabled) return input;
            EnsureBuffers();

            float output = 0f;
            float phaseInc = Rate / SampleRate * 2f * MathF.PI;
            _phase += phaseInc;

            for (int v = 0; v < Voices; v++)
            {
                float mod = MathF.Sin(_phase + v * 2f * MathF.PI / Voices) * Depth;
                int readPos = (_indices[v] - (int)mod + _bufferSize) % _bufferSize;
                float delayed = _buffers[v][readPos];

                float voiceOut = (input + delayed * Feedback) * 0.5f;
                _buffers[v][_indices[v]] = input;
                _indices[v] = (_indices[v] + 1) % _bufferSize;
                output += voiceOut;
            }

            output /= Voices;
            return input * (1f - WetMix) + output * WetMix;
        }

        public override void Reset()
        {
            for (int v = 0; v < _buffers.Length; v++)
            {
                Array.Clear(_buffers[v], 0, _buffers[v].Length);
                _indices[v] = 0;
            }
            _phase = 0f;
        }
    }

    // ==============================================================================
    //  四、DSP 效果链
    // ==============================================================================

    /// <summary>
    /// DSP 效果链。有序节点列表，串联处理音频缓冲。
    /// 支持动态添加/移除效果器节点。
    /// </summary>
    public class DSPChain
    {
        /// <summary>效果器节点列表。</summary>
        public readonly List<DSPNode> Nodes = new();
        /// <summary>是否启用旁路（旁路时所有效果器都不处理）。</summary>
        public bool Bypassed;

        /// <summary>添加效果器节点。</summary>
        public void AddNode(DSPNode node) => Nodes.Add(node);

        /// <summary>移除效果器节点。</summary>
        public bool RemoveNode(DSPNode node) => Nodes.Remove(node);

        /// <summary>插入效果器到指定位置。</summary>
        public void InsertNode(int index, DSPNode node) => Nodes.Insert(index, node);

        /// <summary>清空所有节点。</summary>
        public void Clear() => Nodes.Clear();

        /// <summary>设置所有节点的采样率。</summary>
        public void SetSampleRate(int sampleRate)
        {
            foreach (var node in Nodes)
                node.SetSampleRate(sampleRate);
        }

        /// <summary>重置所有节点。</summary>
        public void Reset()
        {
            foreach (var node in Nodes)
                node.Reset();
        }

        /// <summary>处理缓冲（串联所有节点）。</summary>
        public void ProcessBuffer(float[] buffer, int channels)
        {
            if (Bypassed) return;
            foreach (var node in Nodes)
                node.ProcessBuffer(buffer, channels);
        }
    }

    // ==============================================================================
    //  五、AudioBus 体系 —— MasterBus -> SubMixBus -> AuxBus
    // ==============================================================================

    /// <summary>
    /// 高级音频总线。支持音量/静音/独奏/声像/DSP 效果链/多输出路由。
    /// </summary>
    public class AdvancedAudioBus
    {
        /// <summary>总线名称。</summary>
        public string Name;
        /// <summary>总线 ID。</summary>
        public Guid Id = Guid.NewGuid();
        /// <summary>音量 [0, 2]。</summary>
        public float Volume = 1f;
        /// <summary>是否静音。</summary>
        public bool Muted;
        /// <summary>是否独奏。</summary>
        public bool Solo;
        /// <summary>声像 [-1, 1]（仅对 2D 音频有效）。</summary>
        public float Pan;
        /// <summary>DSP 效果链。</summary>
        public readonly DSPChain EffectChain = new();
        /// <summary>路由目标总线 ID 列表。</summary>
        public readonly List<Guid> OutputBuses = new();
        /// <summary>子总线 ID 列表（树形路由用）。</summary>
        public readonly List<Guid> ChildBuses = new();
        /// <summary>当前峰值电平。</summary>
        public float PeakLevel;
        /// <summary>当前 RMS 电平。</summary>
        /// <summary>活跃实例数。</summary>
        public int ActiveInstanceCount;
        /// <summary>总线类型标签。</summary>
        public string BusType = "SubMix";

        private float _rmsLevel;

        public float RMSLevel => _rmsLevel;

        public AdvancedAudioBus(string name, string busType = "SubMix")
        {
            Name = name;
            BusType = busType;
        }

        /// <summary>处理总线上的音频数据：音量 + 声像 + 效果链 + 电平表。</summary>
        public void ProcessBuffer(float[] buffer, int channels)
        {
            if (Muted)
            {
                Array.Clear(buffer, 0, buffer.Length);
                PeakLevel = 0f;
                _rmsLevel = 0f;
                return;
            }

            // 应用音量和声像
            float leftVol = Volume * (1f - MathF.Max(0, Pan));
            float rightVol = Volume * (1f + MathF.Min(0, Pan));

            for (int i = 0; i < buffer.Length; i += channels)
            {
                if (channels >= 2)
                {
                    buffer[i] *= leftVol;
                    buffer[i + 1] *= rightVol;
                }
                else
                {
                    buffer[i] *= Volume;
                }
            }

            // 应用效果链
            EffectChain.ProcessBuffer(buffer, channels);

            // 更新电平表
            float peak = 0f;
            float sumSq = 0f;
            int samples = buffer.Length / Math.Max(1, channels);
            for (int i = 0; i < buffer.Length; i++)
            {
                float abs = MathF.Abs(buffer[i]);
                if (abs > peak) peak = abs;
                sumSq += buffer[i] * buffer[i];
            }
            PeakLevel = peak;
            _rmsLevel = samples > 0 ? MathF.Sqrt(sumSq / samples) : 0f;
        }

        /// <summary>添加 Aux 发送到目标总线（带发送量）。</summary>
        public void AddAuxSend(Guid targetBusId, float sendAmount)
        {
            if (!OutputBuses.Contains(targetBusId))
                OutputBuses.Add(targetBusId);
        }

        /// <summary>移除 Aux 发送。</summary>
        public bool RemoveAuxSend(Guid targetBusId) => OutputBuses.Remove(targetBusId);
    }

    // ==============================================================================
    //  六、AudioBusGraph —— 总线图拓扑管理
    // ==============================================================================

    /// <summary>
    /// 音频总线图。管理所有总线之间的树形路由关系。
    /// MasterBus -> SubMixBus -> AuxBus，按拓扑序处理。
    /// </summary>
    public class AudioBusGraph
    {
        /// <summary>所有总线字典。</summary>
        public readonly Dictionary<Guid, AdvancedAudioBus> Buses = new();
        /// <summary>主输出总线。</summary>
        public AdvancedAudioBus MasterBus;

        public AudioBusGraph()
        {
            MasterBus = CreateBus("Master", "Master");
        }

        /// <summary>创建并注册总线。</summary>
        public AdvancedAudioBus CreateBus(string name, string busType = "SubMix")
        {
            var bus = new AdvancedAudioBus(name, busType);
            Buses[bus.Id] = bus;
            return bus;
        }

        /// <summary>创建子总线并连接到父总线（树形路由）。</summary>
        public AdvancedAudioBus CreateChildBus(string name, Guid parentId, string busType = "SubMix")
        {
            var bus = CreateBus(name, busType);
            if (Buses.TryGetValue(parentId, out var parent))
            {
                parent.ChildBuses.Add(bus.Id);
                bus.OutputBuses.Add(parentId); // 子 -> 父 路由
            }
            return bus;
        }

        /// <summary>创建 Aux 发送总线（发送到目标总线）。</summary>
        public AdvancedAudioBus CreateAuxBus(string name, Guid targetId)
        {
            var bus = CreateBus(name, "Aux");
            bus.OutputBuses.Add(targetId);
            return bus;
        }

        /// <summary>连接总线输出（任意两点路由）。</summary>
        public void Connect(Guid fromId, Guid toId)
        {
            if (Buses.ContainsKey(fromId) && Buses.ContainsKey(toId))
                if (!Buses[fromId].OutputBuses.Contains(toId))
                    Buses[fromId].OutputBuses.Add(toId);
        }

        /// <summary>断开总线连接。</summary>
        public void Disconnect(Guid fromId, Guid toId)
        {
            if (Buses.TryGetValue(fromId, out var bus))
                bus.OutputBuses.Remove(toId);
        }

        /// <summary>移除总线。</summary>
        public bool RemoveBus(Guid busId)
        {
            if (busId == MasterBus.Id) return false;
            if (!Buses.TryGetValue(busId, out var bus)) return false;

            // 清理所有引用
            foreach (var b in Buses.Values)
            {
                b.ChildBuses.Remove(busId);
                b.OutputBuses.Remove(busId);
            }
            Buses.Remove(busId);
            return true;
        }

        /// <summary>按拓扑序获取所有总线（叶子节点先处理）。</summary>
        private List<AdvancedAudioBus> GetTopologicalOrder()
        {
            var visited = new HashSet<Guid>();
            var result = new List<AdvancedAudioBus>();

            void Visit(Guid id)
            {
                if (visited.Contains(id)) return;
                visited.Add(id);
                if (Buses.TryGetValue(id, out var bus))
                {
                    // 先处理子节点
                    foreach (var childId in bus.ChildBuses)
                        Visit(childId);
                    // 再处理自身
                    result.Add(bus);
                }
            }

            Visit(MasterBus.Id);
            return result;
        }

        /// <summary>
        /// 处理整条总线图。按拓扑顺序将子总线混入父总线。
        /// </summary>
        public void ProcessGraph(float[] outputBuffer, int channels)
        {
            Array.Clear(outputBuffer, 0, outputBuffer.Length);

            // 检查独奏状态
            bool anySolo = Buses.Values.Any(b => b.Solo);

            // 按拓扑序处理（叶子先）
            var order = GetTopologicalOrder();

            // 每个总线的混合缓冲
            var busBuffers = new Dictionary<Guid, float[]>();
            foreach (var bus in order)
            {
                busBuffers[bus.Id] = new float[outputBuffer.Length];
            }

            // 从叶子到根，将子总线输出混入父总线
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var bus = order[i];

                // 收集所有子总线的输出并混入当前总线
                foreach (var childId in bus.ChildBuses)
                {
                    if (busBuffers.TryGetValue(childId, out var childBuf))
                    {
                        for (int s = 0; s < outputBuffer.Length; s++)
                            busBuffers[bus.Id][s] += childBuf[s];
                    }
                }

                // 跳过 MasterBus 本身不混合（Master 是最终输出）
                if (bus.Id == MasterBus.Id)
                {
                    // MasterBus 直接处理混入 outputBuffer
                    for (int s = 0; s < outputBuffer.Length; s++)
                        outputBuffer[s] = busBuffers[bus.Id][s];
                    MasterBus.ProcessBuffer(outputBuffer, channels);
                }
                else
                {
                    // 独奏检查
                    if (anySolo && !bus.Solo)
                    {
                        Array.Clear(busBuffers[bus.Id], 0, busBuffers[bus.Id].Length);
                        continue;
                    }

                    // 处理子总线缓冲
                    bus.ProcessBuffer(busBuffers[bus.Id], channels);
                }
            }

            // 处理 Aux 发送（非树形连接）
            foreach (var bus in Buses.Values)
            {
                if (bus.BusType == "Aux" || (bus.OutputBuses.Count > 0 && bus.BusType != "SubMix"))
                {
                    continue; // Aux 总线已在主流程中处理
                }
            }
        }

        /// <summary>设置所有总线采样率。</summary>
        public void SetSampleRate(int sampleRate)
        {
            foreach (var bus in Buses.Values)
                bus.EffectChain.SetSampleRate(sampleRate);
        }

        /// <summary>重置所有总线效果链。</summary>
        public void Reset()
        {
            foreach (var bus in Buses.Values)
                bus.EffectChain.Reset();
        }
    }

    // ==============================================================================
    //  七、AudioBufferPool —— 音频缓冲池
    // ==============================================================================

    /// <summary>
    /// 音频缓冲池。零分配复用，避免混音过程中的 GC 压力。
    /// </summary>

    // ==============================================================================
    //  八、SoundClass 体系 —— 声音类层级
    // ==============================================================================

    /// <summary>
    /// 声音类（Sound Class）。层级化音量/音调/暂停控制。
    /// 支持 Master / SFX / Music / UI / Voice / Ambient 六大类。
    /// </summary>
    public class SoundClassEntry
    {
        /// <summary>类名。</summary>
        public string Name;
        /// <summary>音量 [0, 2]。</summary>
        public float Volume = 1f;
        /// <summary>音调倍率。</summary>
        public float Pitch = 1f;
        /// <summary>是否暂停（暂停时该类所有实例静音）。</summary>
        public bool Paused;
        /// <summary>是否静音。</summary>
        public bool Muted;
        /// <summary>父类引用（null 表示根类）。</summary>
        public SoundClassEntry Parent;
        /// <summary>最大并发数（0 = 无限制）。</summary>
        public int MaxConcurrency = 0;
        /// <summary>优先级（用于并发裁剪，数值越大越重要）。</summary>
        public int DefaultPriority = 100;

        /// <summary>获取有效音量（沿父链累乘）。</summary>
        public float GetEffectiveVolume()
        {
            float vol = Volume;
            if (Muted) return 0f;
            var p = Parent;
            while (p != null)
            {
                if (p.Muted) return 0f;
                vol *= p.Volume;
                p = p.Parent;
            }
            return vol;
        }

        /// <summary>获取有效音调（沿父链累乘）。</summary>
        public float GetEffectivePitch()
        {
            float pitch = Pitch;
            var p = Parent;
            while (p != null)
            {
                pitch *= p.Pitch;
                p = p.Parent;
            }
            return pitch;
        }

        /// <summary>获取有效暂停状态（任一祖先暂停则暂停）。</summary>
        public bool GetEffectivePaused()
        {
            if (Paused) return true;
            var p = Parent;
            while (p != null)
            {
                if (p.Paused) return true;
                p = p.Parent;
            }
            return false;
        }
    }

    /// <summary>
    /// 声音类管理器。管理 Master/SFX/Music/UI/Voice/Ambient 六大类的层级树。
    /// </summary>
    public class SoundClassManager
    {
        /// <summary>所有声音类。</summary>
        public readonly Dictionary<string, SoundClassEntry> Classes = new();

        /// <summary>根 Master 类。</summary>
        public SoundClassEntry MasterClass;

        /// <summary>预定义类快捷引用。</summary>
        public SoundClassEntry SFXClass;
        public SoundClassEntry MusicClass;
        public SoundClassEntry UIClass;
        public SoundClassEntry VoiceClass;
        public SoundClassEntry AmbientClass;

        public SoundClassManager()
        {
            MasterClass = CreateClass("Master", null);
            SFXClass = CreateClass("SFX", MasterClass);
            MusicClass = CreateClass("Music", MasterClass);
            UIClass = CreateClass("UI", MasterClass);
            VoiceClass = CreateClass("Voice", MasterClass);
            AmbientClass = CreateClass("Ambient", MasterClass);

            // 默认优先级
            VoiceClass.DefaultPriority = 200;
            SFXClass.DefaultPriority = 100;
            UIClass.DefaultPriority = 150;
            MusicClass.DefaultPriority = 80;
            AmbientClass.DefaultPriority = 50;

            // 默认最大并发
            SFXClass.MaxConcurrency = 32;
            UIClass.MaxConcurrency = 8;
        }

        /// <summary>创建声音类。</summary>
        public SoundClassEntry CreateClass(string name, SoundClassEntry parent)
        {
            var sc = new SoundClassEntry { Name = name, Parent = parent };
            Classes[name] = sc;
            if (parent != null)
                parent.Children ??= new List<SoundClassEntry>();
            return sc;
        }

        /// <summary>按名称获取声音类。</summary>
        public SoundClassEntry GetClass(string name)
        {
            return Classes.TryGetValue(name, out var sc) ? sc : MasterClass;
        }

        /// <summary>将 AudioType 映射到 SoundClass。</summary>
        public SoundClassEntry GetClassByAudioType(AudioType type)
        {
            return type switch
            {
                AudioType.SFX => SFXClass,
                AudioType.Music => MusicClass,
                AudioType.UI => UIClass,
                AudioType.Voice => VoiceClass,
                AudioType.Ambient => AmbientClass,
                _ => SFXClass
            };
        }
    }

    // ==============================================================================
    //  九、AudioMixer —— 多声道混音器
    // ==============================================================================

    /// <summary>
    /// 多声道混音器。将多个音频实例的信号混入输出缓冲，
    /// 支持 3D 空间化、距离衰减、声像、并发限制。
    /// </summary>
    public class AdvancedAudioMixer
    {
        /// <summary>声道数。</summary>
        public int Channels = 2;
        /// <summary>采样率。</summary>
        public int SampleRate = 48000;

        /// <summary>
        /// 将一个音频实例混入输出缓冲。
        /// </summary>
        /// <param name="output">输出缓冲。</param>
        /// <param name="instance">音频实例。</param>
        /// <param name="spatialGain">空间化增益（距离衰减 * 遮挡）。</param>
        /// <param name="pan">声像 [-1, 1]。</param>
        public void MixInstance(float[] output, AdvancedAudioInstance instance, float spatialGain, float pan)
        {
            if (instance.Clip == null) return;
            float gain = instance.Volume * spatialGain;

            if (Channels >= 2)
            {
                float leftGain = gain * (1f - MathF.Max(0, pan));
                float rightGain = gain * (1f + MathF.Min(0, pan));
                for (int i = 0; i < output.Length; i += Channels)
                {
                    float sample = GenerateSample(instance);
                    output[i] += sample * leftGain;
                    output[i + 1] += sample * rightGain;
                }
            }
            else
            {
                for (int i = 0; i < output.Length; i++)
                {
                    output[i] += GenerateSample(instance) * gain;
                }
            }
        }

        /// <summary>
        /// 生成简化音频信号（占位：正弦波）。
        /// 实际实现应从 AudioClip.Data 读取 PCM 数据。
        /// </summary>
        private float GenerateSample(AdvancedAudioInstance instance)
        {
            float freq = 440f * instance.Pitch;
            return MathF.Sin(instance.PlaybackTime * freq * MathF.PI * 2f) * 0.1f;
        }

        /// <summary>限制缓冲范围到 [-1, 1]（软限幅）。</summary>
        public void SoftClamp(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            }
        }
    }

    // ==============================================================================
    //  十、音频遮挡与反射
    // ==============================================================================

    /// <summary>
    /// 音频遮挡计算器。通过射线检测判断声源是否被几何体遮挡。
    /// </summary>
    public class AudioOcclusionCalculator
    {
        /// <summary>最大遮挡衰减量（dB）。</summary>
        public float MaxAttenuation = 30f;
        /// <summary>每次射线命中的衰减量（dB）。</summary>
        public float AttenuationPerHit = 6f;
        /// <summary>射线检测回调（由物理系统提供）。</summary>
        public Func<Vector3, Vector3, int>? RaycastCallback;

        /// <summary>计算从声源到听者的遮挡衰减量。</summary>
        public float CalculateOcclusion(Vector3 sourcePos, Vector3 listenerPos)
        {
            if (RaycastCallback == null) return 0f;
            int hitCount = RaycastCallback(sourcePos, listenerPos);
            float attenuation = Math.Min(hitCount * AttenuationPerHit, MaxAttenuation);
            return attenuation;
        }

        /// <summary>将 dB 衰减量转换为线性增益系数。</summary>
        public static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

        /// <summary>将线性增益转换为 dB。</summary>
        public static float LinearToDb(float linear) => 20f * MathF.Log10(Math.Max(1e-6f, linear));
    }

    /// <summary>
    /// 音频反射模拟器。简化的一次反射模型。
    /// </summary>
    public class AudioReflectionSimulator
    {
        public struct ReflectionSurface
        {
            public Vector3 Position;
            public Vector3 Normal;
            public float Size;
            public float Absorption; // 0=全反射, 1=全吸收
        }

        private readonly List<ReflectionSurface> _surfaces = new();
        private const int MaxReflections = 4;

        public void AddSurface(Vector3 position, Vector3 normal, float size, float absorption = 0.3f)
        {
            _surfaces.Add(new ReflectionSurface
            {
                Position = position,
                Normal = Vector3.Normalize(normal),
                Size = size,
                Absorption = absorption
            });
        }

        public void ClearSurfaces() => _surfaces.Clear();

        /// <summary>计算声源到听者的反射路径。</summary>
        public List<(Vector3 hitPoint, float delay, float gain)> CalculateReflections(
            Vector3 sourcePos, Vector3 listenerPos)
        {
            var results = new List<(Vector3, float, float)>();

            for (int i = 0; i < Math.Min(_surfaces.Count, MaxReflections); i++)
            {
                var surface = _surfaces[i];
                float dist = Vector3.Distance(sourcePos, listenerPos);
                float reflDist = Vector3.Distance(sourcePos, surface.Position)
                               + Vector3.Distance(surface.Position, listenerPos);
                float delay = (reflDist - dist) / 343f; // 声速差
                float gain = (1f - surface.Absorption) * surface.Size / Math.Max(1f, reflDist);
                results.Add((surface.Position, delay, gain));
            }

            return results;
        }
    }

    // ==============================================================================
    //  十一、StreamingAudio —— 流式音频源
    // ==============================================================================

    /// <summary>
    /// 流式音频源。用于长音频文件（背景音乐、过场动画语音等）。
    /// 使用环形缓冲区实现按需解码。
    /// </summary>
    public class StreamingAudioSource
    {
        public string FilePath;
        public float Duration;
        public int SampleRate = 48000;
        public int Channels = 2;
        public bool IsLooping;
        public float PlaybackTime;

        private float[] _ringBuffer;
        private int _ringHead;
        private int _ringTail;
        private int _ringSize;
        private bool _isLoaded;

        public bool IsLoaded => _isLoaded;

        public StreamingAudioSource(string filePath, float duration)
        {
            FilePath = filePath;
            Duration = duration;
            _ringSize = (int)(SampleRate * Channels * Math.Min(duration, 10f));
            _ringBuffer = new float[_ringSize];
        }

        /// <summary>加载音频数据（占位：实际实现应解码文件）。</summary>
        public void Load()
        {
            _isLoaded = true;
        }

        /// <summary>读取下一帧数据。</summary>
        public int Read(float[] output, int count)
        {
            if (!_isLoaded) return 0;
            int available = (_ringHead - _ringTail + _ringSize) % _ringSize;
            int toRead = Math.Min(count, available);
            for (int i = 0; i < toRead; i++)
            {
                output[i] = _ringBuffer[_ringTail];
                _ringTail = (_ringTail + 1) % _ringSize;
            }
            return toRead;
        }

        /// <summary>写入解码数据到环形缓冲。</summary>
        public int Write(float[] input, int count)
        {
            int space = (_ringTail - _ringHead - 1 + _ringSize) % _ringSize;
            int toWrite = Math.Min(count, space);
            for (int i = 0; i < toWrite; i++)
            {
                _ringBuffer[_ringHead] = input[i];
                _ringHead = (_ringHead + 1) % _ringSize;
            }
            return toWrite;
        }

        /// <summary>重置播放位置。</summary>
        public void Reset()
        {
            PlaybackTime = 0f;
            _ringHead = 0;
            _ringTail = 0;
        }
    }

    // ==============================================================================
    //  十二、AudioCrossfader —— 交叉淡变器
    // ==============================================================================

    /// <summary>
    /// 音频交叉淡变器。支持线性、等功率、Smoothstep 三种曲线。
    /// </summary>
    public class AudioCrossfader
    {
        public enum FadeCurve { Linear, EqualPower, Smoothstep }

        public float Duration = 2f;
        public FadeCurve Curve = FadeCurve.EqualPower;
        public bool IsActive;
        public float Progress;

        public void Start(float duration, FadeCurve curve)
        {
            Duration = Math.Max(0.01f, duration);
            Curve = curve;
            Progress = 0f;
            IsActive = true;
        }

        public void Update(float dt)
        {
            if (!IsActive) return;
            Progress += dt / Duration;
            if (Progress >= 1f)
            {
                Progress = 1f;
                IsActive = false;
            }
        }

        /// <summary>获取当前淡入增益。</summary>
        public float GetFadeInGain()
        {
            return Curve switch
            {
                FadeCurve.Linear => Progress,
                FadeCurve.EqualPower => MathF.Sin(Progress * MathF.PI * 0.5f),
                FadeCurve.Smoothstep => Progress * Progress * (3f - 2f * Progress),
                _ => Progress
            };
        }

        /// <summary>获取当前淡出增益。</summary>
        public float GetFadeOutGain()
        {
            return Curve switch
            {
                FadeCurve.Linear => 1f - Progress,
                FadeCurve.EqualPower => MathF.Cos(Progress * MathF.PI * 0.5f),
                FadeCurve.Smoothstep => 1f - Progress * Progress * (3f - 2f * Progress),
                _ => 1f - Progress
            };
        }

        public void Reset()
        {
            IsActive = false;
            Progress = 0f;
        }
    }

    // ==============================================================================
    //  十三、AudioBackend —— 后端抽象
    // ==============================================================================

    /// <summary>音频后端类型。</summary>
    public enum AudioBackendType
    {
        WASAPI,
        XAudio2,
        OpenAL,
        Dummy,
        CoreAudio
    }

    /// <summary>音频后端接口（高级）。</summary>
    public interface IAudioBackendAdvanced
    {
        AudioBackendType BackendType { get; }
        bool IsInitialized { get; }
        bool Initialize(int sampleRate, int channels, int bufferSize);
        void Start();
        void Stop();
        void Shutdown();
        void SubmitBuffer(float[] buffer);
    }

    /// <summary>Dummy 音频后端（无音频设备环境占位）。</summary>
    public class DummyAudioBackend : IAudioBackendAdvanced
    {
        public AudioBackendType BackendType => AudioBackendType.Dummy;
        public bool IsInitialized { get; private set; }

        public bool Initialize(int sampleRate, int channels, int bufferSize)
        {
            IsInitialized = true;
            return true;
        }

        public void Start() { }
        public void Stop() { }
        public void Shutdown() { IsInitialized = false; }
        public void SubmitBuffer(float[] buffer) { /* 丢弃 */ }
    }

    // ==============================================================================
    //  十四、ECS 组件
    // ==============================================================================

    /// <summary>ECS 组件：高级音频源（绑定到实体上）。</summary>
    public struct AdvancedAudioSourceComponent : IComponent
    {
        public AdvancedAudioSourceComponent() { }
        /// <summary>音频剪辑 ID。</summary>
        public Guid ClipId;
        /// <summary>音量。</summary>
        public float Volume = 1f;
        /// <summary>音调。</summary>
        public float Pitch = 1f;
        /// <summary>是否循环。</summary>
        public bool Loop;
        /// <summary>是否自动播放。</summary>
        public bool AutoPlay;
        /// <summary>最小距离。</summary>
        public float MinDistance = 1f;
        /// <summary>最大距离。</summary>
        public float MaxDistance = 500f;
        /// <summary>是否启用空间化。</summary>
        public bool Spatialize = true;
        /// <summary>关联的实例 ID（播放后由引擎回填）。</summary>
        public Guid InstanceId;
        /// <summary>声音类名称。</summary>
        public string SoundClassName = "SFX";
        /// <summary>优先级。</summary>
        public int Priority = 100;
    }

    // ==============================================================================
    //  十五、AdvancedAudioEngine —— 顶层音频引擎
    // ==============================================================================

    /// <summary>
    /// 高级音频引擎。山雨引擎音频子系统的核心管理器。
    /// 
    /// 职责：
    ///   - AudioBus 树形路由：MasterBus -> SubMixBus -> AuxBus
    ///   - 音频混音：多声道混音、3D 空间化、距离衰减
    ///   - SoundClass 体系：Master/SFX/Music/UI/Voice/Ambient 独立控制
    ///   - 音频源管理：播放/停止/暂停/淡入淡出
    ///   - 缓冲池管理：零分配复用
    ///   - 实时 DSP：EQ、Compressor、Reverb、Delay、Chorus
    ///   - 并发播放限制：按声音类限流，超限按优先级裁剪
    ///   - ECS 集成：实体音频源自动同步
    /// </summary>
    public class AdvancedAudioEngine : ISystem
    {
        // ── ISystem ──
        public string Name => "AdvancedAudioEngine";

        // ── 配置 ──
        /// <summary>采样率。</summary>
        public int SampleRate = 48000;
        /// <summary>声道数。</summary>
        public int Channels = 2;
        /// <summary>输出缓冲大小。</summary>
        public int OutputBufferSize = 2048;
        /// <summary>最大同时播放实例数。</summary>
        public int MaxInstances = 256;

        // ── 核心子系统 ──
        /// <summary>HRTF 双耳渲染数据。</summary>
        public HRTFData HRTF = new();
        /// <summary>音频总线图（树形路由）。</summary>
        public AudioBusGraph BusGraph;
        /// <summary>声音类管理器。</summary>
        public SoundClassManager SoundClasses;
        /// <summary>缓冲池。</summary>
        public AudioBufferPool BufferPool;
        /// <summary>混音器。</summary>
        public AdvancedAudioMixer Mixer;
        /// <summary>遮挡计算器。</summary>
        public AudioOcclusionCalculator Occlusion;
        /// <summary>反射模拟器。</summary>
        public AudioReflectionSimulator Reflections;
        /// <summary>交叉淡变器。</summary>
        public AudioCrossfader Crossfader;
        /// <summary>音频后端。</summary>
        private IAudioBackendAdvanced? _backend;

        // ── WASAPI 输出（新增）──
        /// <summary>WASAPI 音频输出驱动。</summary>
        private WasapiAudioOutput? _wasapiOutput;
        /// <summary>简化活跃音频源（用于 PlayOneShot）。</summary>
        private readonly List<AudioSource> _simpleSources = new();
        private readonly object _simpleSourceLock = new();
        /// <summary>主音量。</summary>
        private float _masterVolume = 1.0f;

        // ── 音频实例管理 ──
        /// <summary>所有活跃音频实例。</summary>
        private readonly Dictionary<Guid, AdvancedAudioInstance> _instances = new();
        /// <summary>音频剪辑库。</summary>
        private readonly Dictionary<Guid, AudioClip> _clips = new();
        /// <summary>按名称索引的剪辑库。</summary>
        private readonly Dictionary<string, AudioClip> _clipRegistry = new();
        /// <summary>流式音频源字典。</summary>
        private readonly Dictionary<Guid, StreamingAudioSource> _streamingSources = new();
        /// <summary>听者。</summary>
        private AudioListener _listener = new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity
        };

        // ── 并发管理 ──
        /// <summary>按声音类统计的活跃实例数。</summary>
        private readonly Dictionary<string, List<Guid>> _classInstances = new();

        // ── ECS 引用 ──
        private EntityComponentSystem? _ecs;

        // ── 主总线快捷引用 ──
        public AdvancedAudioBus? SFXBus;
        public AdvancedAudioBus? MusicBus;
        public AdvancedAudioBus? VoiceBus;
        public AdvancedAudioBus? AmbientBus;
        public AdvancedAudioBus? UIBus;

        // ── 统计 ──
        public int ActiveInstanceCount => _instances.Count;
        public int ClipCount => _clips.Count;
        public int StreamingSourceCount => _streamingSources.Count;
        public int BusCount => BusGraph.Buses.Count;
        public float MasterPeakLevel => BusGraph.MasterBus.PeakLevel;

        // ── 事件 ──
        public event Action<AdvancedAudioInstance>? OnPlay;
        public event Action<AdvancedAudioInstance>? OnStop;
        public event Action<string>? OnLogMessage;

        // ══════════════════════════════════════════════════════════════
        // 构造与初始化
        // ══════════════════════════════════════════════════════════════

        public AdvancedAudioEngine()
        {
            SoundClasses = new SoundClassManager();
            BufferPool = new AudioBufferPool();
            Mixer = new AdvancedAudioMixer();
            BusGraph = new AudioBusGraph();
            Crossfader = new AudioCrossfader();
            SetupDefaultBuses();
            Log("高级音频引擎已初始化");
        }

        /// <summary>
        /// 按引擎配置初始化音频引擎。应用采样率/声道/缓冲大小,
        /// 初始化 HRTF 双耳渲染数据, 创建音频后端并同步效果链采样率。
        /// 如果后端初始化失败，自动回退到 DummyAudioBackend 保证音频系统可用。
        /// </summary>
        public void Initialize(ShanyuEngine.Core.AudioConfig config)
        {
            try
            {
                if (config != null)
                {
                    SampleRate = config.SampleRate;
                    Channels = config.Channels;
                    OutputBufferSize = config.BufferSize;
                }

                // HRTF
                if (config == null || config.EnableHRTF)
                {
                    HRTF.SampleRate = SampleRate;
                    HRTF.Initialize();
                }

                // 混音器
                Mixer.SampleRate = SampleRate;
                Mixer.Channels = Channels;

                // 后端 — 尝试 CoreAudioBackend (WASAPI), 失败自动回退到 Dummy
                InitializeBackend();

                // 同步所有效果链
                BusGraph.SetSampleRate(SampleRate);
            }
            catch (Exception ex)
            {
                Log($"音频引擎初始化失败: {ex.Message}，回退到 DummyAudioBackend");
                if (_backend == null || !_backend.IsInitialized)
                {
                    _backend = new DummyAudioBackend();
                    _backend.Initialize(SampleRate, Channels, OutputBufferSize);
                    _backend.Start();
                }
            }

            Log($"高级音频引擎已配置: {SampleRate}Hz, {Channels}ch, 缓冲={OutputBufferSize}, " +
                $"HRTF={(HRTF.IsInitialized ? "启用" : "关闭")}, 总线数={BusCount}");
        }

        /// <summary>设置默认总线结构：Master -> SFX/Music/Voice/Ambient/UI。</summary>
        private void SetupDefaultBuses()
        {
            SFXBus = BusGraph.CreateChildBus("SFX", BusGraph.MasterBus.Id, "SubMix");
            MusicBus = BusGraph.CreateChildBus("Music", BusGraph.MasterBus.Id, "SubMix");
            VoiceBus = BusGraph.CreateChildBus("Voice", BusGraph.MasterBus.Id, "SubMix");
            AmbientBus = BusGraph.CreateChildBus("Ambient", BusGraph.MasterBus.Id, "SubMix");
            UIBus = BusGraph.CreateChildBus("UI", BusGraph.MasterBus.Id, "SubMix");

            // 创建 Reverb Aux 总线
            var reverbAux = BusGraph.CreateAuxBus("ReverbAux", BusGraph.MasterBus.Id);
            reverbAux.EffectChain.AddNode(new ReverbEffect
            {
                RoomSize = 0.7f,
                DecayTime = 2.5f,
                Damping = 0.4f,
                WetMix = 0.8f
            });

            // 为各总线配置默认效果链
            AmbientBus.EffectChain.AddNode(new ReverbEffect
            {
                RoomSize = 0.8f, DecayTime = 3f, WetMix = 0.4f
            });
            MusicBus.EffectChain.AddNode(new CompressorEffect
            {
                Threshold = -15f, Ratio = 2f, MakeupGain = 3f
            });
            SFXBus.EffectChain.AddNode(new EqualizerEffect());
        }

        // ══════════════════════════════════════════════════════════════
        // 后端管理
        // ══════════════════════════════════════════════════════════════

        public bool InitializeBackend(AudioBackendType backendType = AudioBackendType.WASAPI)
        {
            // 优先尝试 WASAPI 真实音频输出
            if (backendType == AudioBackendType.WASAPI)
            {
                try
                {
                    _wasapiOutput = new WasapiAudioOutput();
                    _wasapiOutput.Initialize();
                    _wasapiOutput.OnAudioCallback += OnWasapiAudioCallback;
                    _wasapiOutput.Start();
                    SampleRate = _wasapiOutput.SampleRate;
                    Channels = _wasapiOutput.Channels;
                    BusGraph.SetSampleRate(SampleRate);
                    Log($"WASAPI 音频输出已初始化: {SampleRate}Hz, {Channels}ch");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"WASAPI 初始化失败: {ex.Message}，回退到 CoreAudioBackend");
                    _wasapiOutput?.Dispose();
                    _wasapiOutput = null;
                }
            }

            // 回退到 CoreAudioBackend（waveOut）
            _backend = new CoreAudioBackend();
            bool success = _backend.Initialize(SampleRate, Channels, OutputBufferSize);
            if (success)
            {
                BusGraph.SetSampleRate(SampleRate);
                _backend.Start();
                Log($"音频后端已初始化: {backendType}, {SampleRate}Hz, {Channels}ch");
            }
            else
            {
                Log("音频后端初始化失败，回退到 DummyAudioBackend");
                _backend = new DummyAudioBackend();
                _backend.Initialize(SampleRate, Channels, OutputBufferSize);
                _backend.Start();
            }
            return success;
        }

        /// <summary>绑定 ECS 系统。</summary>
        public void BindECS(EntityComponentSystem ecs)
        {
            _ecs = ecs;
            Log("已绑定 ECS 系统");
        }

        /// <summary>设置遮挡射线检测回调。</summary>
        public void SetOcclusionRaycastCallback(Func<Vector3, Vector3, int> callback)
        {
            Occlusion.RaycastCallback = callback;
        }

        // ══════════════════════════════════════════════════════════════
        // 剪辑管理
        // ══════════════════════════════════════════════════════════════

        public AudioClip RegisterClip(string name, string path, float duration, AudioType type = AudioType.SFX)
        {
            var clip = new AudioClip { Name = name, FilePath = path, Duration = duration, Type = type };
            _clips[clip.Id] = clip;
            _clipRegistry[name] = clip;
            return clip;
        }

        public AudioClip? GetClip(Guid id) => _clips.TryGetValue(id, out var clip) ? clip : null;
        public AudioClip? GetClipByName(string name) =>
            _clipRegistry.TryGetValue(name, out var clip) ? clip : null;

        // ══════════════════════════════════════════════════════════════
        // 3D 空间音频计算
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 计算 3D 音频的空间化参数。
        /// </summary>
        public SpatialAudioParams CalculateSpatialParams(
            Vector3 sourcePos, Vector3 listenerPos, Quaternion listenerRot,
            float minDist, float maxDist, float rolloff, bool enableOcclusion)
        {
            Vector3 toSource = sourcePos - listenerPos;
            float distance = toSource.Length();

            // 距离衰减（对数模型）
            float distanceGain;
            if (distance < minDist)
                distanceGain = 1f;
            else if (distance > maxDist)
                distanceGain = 0f;
            else
            {
                float t = (distance - minDist) / Math.Max(0.001f, maxDist - minDist);
                distanceGain = MathF.Pow(1f - t, rolloff);
            }

            // 方位角和仰角
            Vector3 listenerForward = Vector3.Transform(new Vector3(0, 0, -1), listenerRot);
            Vector3 listenerRight = Vector3.Transform(new Vector3(1, 0, 0), listenerRot);
            Vector3 dir = distance > 1e-4f ? toSource / distance : Vector3.UnitZ;

            float azimuth = MathF.Atan2(Vector3.Dot(dir, listenerRight), Vector3.Dot(dir, -listenerForward));
            float elevation = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));

            // 遮挡衰减
            float occlusionGain = 1f;
            if (enableOcclusion && Occlusion.RaycastCallback != null)
            {
                float occlusionDb = Occlusion.CalculateOcclusion(sourcePos, listenerPos);
                occlusionGain = AudioOcclusionCalculator.DbToLinear(-occlusionDb);
            }

            return new SpatialAudioParams
            {
                Distance = distance,
                DistanceGain = distanceGain,
                Azimuth = azimuth * 180f / MathF.PI,
                Elevation = elevation * 180f / MathF.PI,
                OcclusionGain = occlusionGain,
                TotalGain = distanceGain * occlusionGain
            };
        }

        // ══════════════════════════════════════════════════════════════
        // 播放控制
        // ══════════════════════════════════════════════════════════════

        /// <summary>播放 3D 音频。</summary>
        public AdvancedAudioInstance? Play3D(AudioClip clip, Vector3 position, float volume = 1f,
            float pitch = 1f, bool loop = false, AdvancedAudioBus? bus = null,
            string soundClassName = "SFX", int priority = 100)
        {
            if (clip == null) return null;

            // 并发检查
            var soundClass = SoundClasses.GetClass(soundClassName);
            if (soundClass.MaxConcurrency > 0)
            {
                EnforceConcurrency(soundClassName, soundClass.MaxConcurrency, priority);
            }

            // 实例数检查
            if (_instances.Count >= MaxInstances)
            {
                CleanupStoppedInstances();
                if (_instances.Count >= MaxInstances)
                {
                    Log($"警告：达到最大实例数限制 ({MaxInstances})，无法播放 {clip.Name}");
                    return null;
                }
            }

            var instance = new AdvancedAudioInstance
            {
                Clip = clip,
                Position = position,
                Volume = volume,
                Pitch = pitch,
                Is3D = true,
                Loop = loop,
                State = AudioState.Playing,
                Bus = bus ?? GetBusForSoundClass(soundClassName),
                MinDistance = 1f,
                MaxDistance = 500f,
                SoundClassName = soundClassName,
                Priority = priority
            };

            _instances[instance.Id] = instance;
            TrackClassInstance(soundClassName, instance.Id);
            OnPlay?.Invoke(instance);
            return instance;
        }

        /// <summary>播放 2D 音频（UI 音效等）。</summary>
        public AdvancedAudioInstance? Play2D(AudioClip clip, float volume = 1f, float pitch = 1f,
            bool loop = false, AdvancedAudioBus? bus = null,
            string soundClassName = "UI", int priority = 100)
        {
            if (clip == null) return null;

            var soundClass = SoundClasses.GetClass(soundClassName);
            if (soundClass.MaxConcurrency > 0)
                EnforceConcurrency(soundClassName, soundClass.MaxConcurrency, priority);

            if (_instances.Count >= MaxInstances)
            {
                CleanupStoppedInstances();
                if (_instances.Count >= MaxInstances)
                {
                    Log($"警告：达到最大实例数限制 ({MaxInstances})");
                    return null;
                }
            }

            var instance = new AdvancedAudioInstance
            {
                Clip = clip,
                Volume = volume,
                Pitch = pitch,
                Is3D = false,
                Loop = loop,
                State = AudioState.Playing,
                Bus = bus ?? GetBusForSoundClass(soundClassName),
                SoundClassName = soundClassName,
                Priority = priority
            };

            _instances[instance.Id] = instance;
            TrackClassInstance(soundClassName, instance.Id);
            OnPlay?.Invoke(instance);
            return instance;
        }

        /// <summary>播放带淡入的音频。</summary>
        public AdvancedAudioInstance? PlayWithFadeIn(AudioClip clip, Vector3 position,
            float volume, float fadeInDuration, float pitch = 1f, bool loop = false,
            AdvancedAudioBus? bus = null, string soundClassName = "SFX")
        {
            var inst = Play3D(clip, position, 0f, pitch, loop, bus, soundClassName);
            if (inst != null)
            {
                inst.State = AudioState.FadingIn;
                inst.FadeDuration = fadeInDuration;
                inst.FadeTarget = volume;
            }
            return inst;
        }

        /// <summary>停止实例（可选淡出）。</summary>
        public void Stop(Guid instanceId, float fadeOut = 0f)
        {
            if (!_instances.TryGetValue(instanceId, out var inst)) return;
            if (fadeOut > 0f)
            {
                inst.State = AudioState.FadingOut;
                inst.FadeDuration = fadeOut;
                inst.FadeTarget = 0f;
            }
            else
            {
                inst.State = AudioState.Stopped;
                UntrackClassInstance(inst.SoundClassName, instanceId);
                OnStop?.Invoke(inst);
            }
        }

        /// <summary>暂停实例。</summary>
        public void Pause(Guid instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var inst)
                && (inst.State == AudioState.Playing || inst.State == AudioState.FadingIn))
            {
                inst.State = AudioState.Paused;
            }
        }

        /// <summary>恢复暂停的实例。</summary>
        public void Resume(Guid instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var inst) && inst.State == AudioState.Paused)
                inst.State = AudioState.Playing;
        }

        /// <summary>停止所有实例。</summary>
        public void StopAll()
        {
            foreach (var inst in _instances.Values)
            {
                inst.State = AudioState.Stopped;
                OnStop?.Invoke(inst);
            }
            _classInstances.Clear();
        }

        /// <summary>按声音类停止所有实例。</summary>
        public void StopAllByClass(string soundClassName)
        {
            var toStop = new List<Guid>();
            if (_classInstances.TryGetValue(soundClassName, out var list))
                toStop.AddRange(list);
            foreach (var id in toStop)
                Stop(id);
        }

        /// <summary>暂停所有实例。</summary>
        public void PauseAll()
        {
            foreach (var inst in _instances.Values)
                if (inst.State == AudioState.Playing)
                    inst.State = AudioState.Paused;
        }

        /// <summary>暂停指定声音类。</summary>
        public void PauseAllByClass(string soundClassName)
        {
            var sc = SoundClasses.GetClass(soundClassName);
            sc.Paused = true;
            if (_classInstances.TryGetValue(soundClassName, out var list))
            {
                foreach (var id in list)
                {
                    if (_instances.TryGetValue(id, out var inst) && inst.State == AudioState.Playing)
                        inst.State = AudioState.Paused;
                }
            }
        }

        /// <summary>恢复所有实例。</summary>
        public void ResumeAll()
        {
            foreach (var inst in _instances.Values)
                if (inst.State == AudioState.Paused)
                    inst.State = AudioState.Playing;
        }

        /// <summary>恢复指定声音类。</summary>
        public void ResumeAllByClass(string soundClassName)
        {
            var sc = SoundClasses.GetClass(soundClassName);
            sc.Paused = false;
            if (_classInstances.TryGetValue(soundClassName, out var list))
            {
                foreach (var id in list)
                {
                    if (_instances.TryGetValue(id, out var inst) && inst.State == AudioState.Paused)
                        inst.State = AudioState.Playing;
                }
            }
        }

        /// <summary>更新实例位置。</summary>
        public void UpdateInstancePosition(Guid instanceId, Vector3 position)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
                inst.Position = position;
        }

        /// <summary>更新实例速度（用于多普勒计算）。</summary>
        public void UpdateInstanceVelocity(Guid instanceId, Vector3 velocity)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
                inst.Velocity = velocity;
        }

        /// <summary>设置实例音量。</summary>
        public void SetInstanceVolume(Guid instanceId, float volume)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
                inst.Volume = Math.Clamp(volume, 0f, 2f);
        }

        /// <summary>设置实例音调。</summary>
        public void SetInstancePitch(Guid instanceId, float pitch)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
                inst.Pitch = Math.Clamp(pitch, 0.1f, 4f);
        }

        /// <summary>获取实例。</summary>
        public AdvancedAudioInstance? GetInstance(Guid id)
        {
            return _instances.TryGetValue(id, out var inst) ? inst : null;
        }

        // ══════════════════════════════════════════════════════════════
        // 并发管理
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 强制并发限制。超限时按优先级（低优先级先裁剪）停止最旧的实例。
        /// </summary>
        private void EnforceConcurrency(string soundClassName, int maxCount, int newPriority)
        {
            if (!_classInstances.TryGetValue(soundClassName, out var list)) return;
            while (list.Count >= maxCount)
            {
                // 找到优先级最低且最早的实例
                Guid? oldest = null;
                int lowestPriority = int.MaxValue;
                DateTime oldestTime = DateTime.MaxValue;
                foreach (var id in list)
                {
                    if (_instances.TryGetValue(id, out var inst))
                    {
                        if (inst.Priority < lowestPriority
                            || (inst.Priority == lowestPriority && inst.StartTime < oldestTime))
                        {
                            lowestPriority = inst.Priority;
                            oldestTime = inst.StartTime;
                            oldest = id;
                        }
                    }
                }
                if (oldest.HasValue)
                    Stop(oldest.Value);
                else
                    break;
            }
        }

        private void TrackClassInstance(string soundClassName, Guid instanceId)
        {
            if (!_classInstances.TryGetValue(soundClassName, out var list))
            {
                list = new List<Guid>();
                _classInstances[soundClassName] = list;
            }
            list.Add(instanceId);
        }

        private void UntrackClassInstance(string soundClassName, Guid instanceId)
        {
            if (_classInstances.TryGetValue(soundClassName, out var list))
                list.Remove(instanceId);
        }

        // ══════════════════════════════════════════════════════════════
        // 总线管理
        // ══════════════════════════════════════════════════════════════

        public void SetMasterVolume(float volume)
        {
            BusGraph.MasterBus.Volume = Math.Clamp(volume, 0f, 2f);
            Log($"主音量设置为 {BusGraph.MasterBus.Volume:F2}");
        }

        public void SetBusVolume(AdvancedAudioBus bus, float volume)
            => bus.Volume = Math.Clamp(volume, 0f, 2f);

        public void SetBusMuted(AdvancedAudioBus bus, bool muted) => bus.Muted = muted;
        public void SetBusSolo(AdvancedAudioBus bus, bool solo) => bus.Solo = solo;

        public AdvancedAudioBus? GetBusForSoundClass(string soundClassName)
        {
            return soundClassName switch
            {
                "SFX" => SFXBus,
                "Music" => MusicBus,
                "Voice" => VoiceBus,
                "Ambient" => AmbientBus,
                "UI" => UIBus,
                _ => SFXBus
            };
        }

        // ══════════════════════════════════════════════════════════════
        // SoundClass 管理
        // ══════════════════════════════════════════════════════════════

        /// <summary>设置声音类音量。</summary>
        public void SetSoundClassVolume(string soundClassName, float volume)
        {
            var sc = SoundClasses.GetClass(soundClassName);
            sc.Volume = Math.Clamp(volume, 0f, 2f);
        }

        /// <summary>设置声音类静音。</summary>
        public void SetSoundClassMuted(string soundClassName, bool muted)
        {
            SoundClasses.GetClass(soundClassName).Muted = muted;
        }

        /// <summary>设置声音类暂停。</summary>
        public void SetSoundClassPaused(string soundClassName, bool paused)
        {
            var sc = SoundClasses.GetClass(soundClassName);
            sc.Paused = paused;
            if (paused)
                PauseAllByClass(soundClassName);
            else
                ResumeAllByClass(soundClassName);
        }

        /// <summary>获取声音类有效音量。</summary>
        public float GetSoundClassEffectiveVolume(string soundClassName)
        {
            return SoundClasses.GetClass(soundClassName).GetEffectiveVolume();
        }

        // ══════════════════════════════════════════════════════════════
        // 流式播放
        // ══════════════════════════════════════════════════════════════

        public StreamingAudioSource CreateStreamingSource(string filePath, float duration)
        {
            var source = new StreamingAudioSource(filePath, duration);
            _streamingSources[source.GetHashCode()] = source;
            return source;
        }

        public void RemoveStreamingSource(Guid id) => _streamingSources.Remove(id);

        // ══════════════════════════════════════════════════════════════
        // 听者管理
        // ══════════════════════════════════════════════════════════════

        public void SetListener(Vector3 position, Quaternion rotation)
        {
            _listener.Position = position;
            _listener.Rotation = rotation;
        }

        public void SetListenerVelocity(Vector3 velocity)
        {
            _listener.Velocity = velocity;
        }

        public AudioListener GetListener() => _listener;

        // ══════════════════════════════════════════════════════════════
        // 交叉淡变
        // ══════════════════════════════════════════════════════════════

        public void StartMusicCrossfade(float duration = 2f,
            AudioCrossfader.FadeCurve curve = AudioCrossfader.FadeCurve.EqualPower)
        {
            Crossfader.Start(duration, curve);
            Log($"开始音乐交叉淡变，时长 {duration}s");
        }

        // ══════════════════════════════════════════════════════════════
        // 反射表面管理
        // ══════════════════════════════════════════════════════════════

        public void AddReflectionSurface(Vector3 position, Vector3 normal, float size,
            float absorption = 0.3f)
        {
            Reflections.AddSurface(position, normal, size, absorption);
        }

        // ══════════════════════════════════════════════════════════════
        // 主更新
        // ══════════════════════════════════════════════════════════════

        /// <summary>ECS 系统更新接口。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Update(dt);
        }

        /// <summary>
        /// 主更新方法。每帧执行：
        /// 1. 更新交叉淡变器
        /// 2. 更新所有音频实例状态
        /// 3. 处理声音类暂停
        /// 4. 计算 3D 空间化参数
        /// 5. 混音并输出到后端
        /// 6. 同步 ECS 实体
        /// </summary>
        public void Update(float dt)
        {
            // 步骤 1：更新交叉淡变
            Crossfader.Update(dt);

            // 步骤 2：更新音频实例
            var toRemove = new List<Guid>();
            foreach (var kvp in _instances)
            {
                var inst = kvp.Value;

                // 声音类暂停检查
                var soundClass = SoundClasses.GetClass(inst.SoundClassName);
                if (soundClass.GetEffectivePaused())
                {
                    if (inst.State == AudioState.Playing || inst.State == AudioState.FadingIn)
                        inst.State = AudioState.Paused;
                }
                else if (inst.State == AudioState.Paused)
                {
                    inst.State = AudioState.Playing;
                }

                // 声音类音量/音调叠加
                inst.EffectiveClassVolume = soundClass.GetEffectiveVolume();
                inst.EffectiveClassPitch = soundClass.GetEffectivePitch();

                UpdateInstance(inst, dt);
                if (inst.State == AudioState.Stopped)
                {
                    toRemove.Add(kvp.Key);
                    UntrackClassInstance(inst.SoundClassName, kvp.Key);
                }
            }
            foreach (var id in toRemove)
                _instances.Remove(id);

            // 步骤 3：混音并输出
            RenderAudio(dt);

            // 步骤 4：同步 ECS
            if (_ecs != null)
                SyncECSAudioSources();
        }

        /// <summary>更新单个音频实例。</summary>
        private void UpdateInstance(AdvancedAudioInstance inst, float dt)
        {
            if (inst.Clip == null) return;

            float effectivePitch = inst.Pitch * inst.EffectiveClassPitch;

            switch (inst.State)
            {
                case AudioState.Playing:
                    if (_wasapiOutput == null)
                        inst.PlaybackTime += dt * effectivePitch;
                    if (!inst.Loop && inst.PlaybackTime >= inst.Clip.Duration)
                    {
                        inst.State = AudioState.Stopped;
                        OnStop?.Invoke(inst);
                    }
                    break;

                case AudioState.FadingIn:
                    if (_wasapiOutput == null)
                        inst.PlaybackTime += dt * effectivePitch;
                    inst.Volume = MathF.Min(
                        inst.Volume + dt / MathF.Max(0.001f, inst.FadeDuration) * inst.FadeTarget,
                        inst.FadeTarget);
                    if (inst.Volume >= inst.FadeTarget - 0.001f)
                        inst.State = AudioState.Playing;
                    break;

                case AudioState.FadingOut:
                    inst.Volume = MathF.Max(0f, inst.Volume - dt / MathF.Max(0.001f, inst.FadeDuration));
                    if (inst.Volume <= 0.001f)
                    {
                        inst.State = AudioState.Stopped;
                        OnStop?.Invoke(inst);
                    }
                    break;
            }

            // 3D 空间化
            if (inst.Is3D)
            {
                var spatial = CalculateSpatialParams(
                    inst.Position, _listener.Position, _listener.Rotation,
                    inst.MinDistance, inst.MaxDistance, inst.RolloffFactor, inst.EnableOcclusion);
                inst.SpatialGain = spatial.TotalGain;
                inst.SpatialAzimuth = spatial.Azimuth;
                inst.SpatialElevation = spatial.Elevation;
                inst.SpatialDistance = spatial.Distance;

                // 声像（基于方位角）
                inst.SpatialPan = Math.Clamp(spatial.Azimuth / 90f, -1f, 1f);

                // 反射
                if (inst.EnableReflections)
                {
                    var paths = Reflections.CalculateReflections(inst.Position, _listener.Position);
                    inst.ReflectionCount = paths.Count;
                }
            }
            else
            {
                inst.SpatialGain = 1f;
                inst.SpatialPan = 0f;
            }
        }

        /// <summary>混音并输出到后端。</summary>
        private void RenderAudio(float dt)
        {
            // WASAPI 活跃时由音频回调负责混音与输出
            if (_wasapiOutput != null && _wasapiOutput.IsInitialized) return;
            if (_backend == null || !_backend.IsInitialized) return;

            int samplesPerFrame = (int)(SampleRate * Channels * dt);
            if (samplesPerFrame <= 0) samplesPerFrame = OutputBufferSize;

            // 从缓冲池借用输出缓冲
            float[] outputBuffer = BufferPool.Acquire(samplesPerFrame);
            try
            {
                Array.Clear(outputBuffer, 0, outputBuffer.Length);

                // 将所有活跃实例混入输出缓冲
                foreach (var inst in _instances.Values)
                {
                    if (inst.State == AudioState.Stopped || inst.State == AudioState.Paused) continue;

                    float gain = inst.Volume * inst.SpatialGain
                               * inst.EffectiveClassVolume
                               * (inst.Bus?.Volume ?? 1f);

                    // 交叉淡变影响
                    if (inst.Bus == MusicBus && Crossfader.IsActive)
                        gain *= Crossfader.GetFadeInGain();

                    // 声像
                    float pan = inst.Is3D ? inst.SpatialPan : (inst.Bus?.Pan ?? 0f);

                    // 混入
                    if (Channels >= 2)
                    {
                        float leftGain = gain * (1f - MathF.Max(0, pan));
                        float rightGain = gain * (1f + MathF.Min(0, pan));
                        for (int i = 0; i < samplesPerFrame; i += Channels)
                        {
                            float sample = GenerateSample(inst);
                            outputBuffer[i] += sample * leftGain;
                            outputBuffer[i + 1] += sample * rightGain;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < samplesPerFrame; i++)
                            outputBuffer[i] += GenerateSample(inst) * gain;
                    }
                }

                // 软限幅
                for (int i = 0; i < outputBuffer.Length; i++)
                    outputBuffer[i] = Math.Clamp(outputBuffer[i], -1f, 1f);

                // 通过总线图处理
                BusGraph.ProcessGraph(outputBuffer, Channels);

                // 提交到后端
                _backend.SubmitBuffer(outputBuffer);
            }
            finally
            {
                // 归还缓冲到池
                BufferPool.Release(outputBuffer);
            }
        }

        /// <summary>生成简化音频信号（占位）。</summary>
        private float GenerateSample(AdvancedAudioInstance inst)
        {
            return GenerateSampleAtTime(inst, inst.PlaybackTime);
        }

        /// <summary>在指定时间生成简化音频信号（占位）。</summary>
        private float GenerateSampleAtTime(AdvancedAudioInstance inst, float time)
        {
            float freq = 440f * inst.Pitch * inst.EffectiveClassPitch;
            return MathF.Sin(time * freq * MathF.PI * 2f) * 0.1f;
        }

        /// <summary>
        /// 播放一次性音效（简化接口）。
        /// </summary>
        public void PlayOneShot(AudioSampleClip clip, Vector3 position, float volume = 1.0f)
        {
            lock (_simpleSourceLock)
            {
                _simpleSources.Add(new AudioSource
                {
                    Clip = clip,
                    Position = position,
                    Volume = volume,
                    Is3D = true,
                    PlayTime = 0
                });
            }
        }

        /// <summary>
        /// WASAPI 音频回调 — 每 10ms 调用一次，混音所有活跃音源。
        /// </summary>
        private void OnWasapiAudioCallback(float[] buffer, int frames, int channels)
        {
            Array.Clear(buffer, 0, buffer.Length);
            int samples = frames * channels;
            float sampleDt = 1.0f / SampleRate;

            // 混合高级音频实例（_instances）
            var instanceSnapshot = _instances.Values.ToArray();
            foreach (var inst in instanceSnapshot)
            {
                if (inst.State == AudioState.Stopped || inst.State == AudioState.Paused) continue;
                if (inst.Clip == null) continue;

                float effectivePitch = inst.Pitch * inst.EffectiveClassPitch;
                float gain = inst.Volume * inst.SpatialGain
                           * inst.EffectiveClassVolume
                           * (inst.Bus?.Volume ?? 1f);

                if (inst.Bus == MusicBus && Crossfader.IsActive)
                    gain *= Crossfader.GetFadeInGain();

                float pan = inst.Is3D ? inst.SpatialPan : (inst.Bus?.Pan ?? 0f);

                if (channels >= 2)
                {
                    float leftGain = gain * (1f - MathF.Max(0, pan));
                    float rightGain = gain * (1f + MathF.Min(0, pan));
                    for (int i = 0; i < samples; i += channels)
                    {
                        float time = inst.PlaybackTime + (i / channels) * sampleDt * effectivePitch;
                        float sample = GenerateSampleAtTime(inst, time);
                        buffer[i] += sample * leftGain;
                        buffer[i + 1] += sample * rightGain;
                    }
                }
                else
                {
                    for (int i = 0; i < samples; i++)
                    {
                        float time = inst.PlaybackTime + i * sampleDt * effectivePitch;
                        buffer[i] += GenerateSampleAtTime(inst, time) * gain;
                    }
                }

                // 在音频线程推进播放时间
                inst.PlaybackTime += frames * sampleDt * effectivePitch;
                if (!inst.Loop && inst.PlaybackTime >= inst.Clip.Duration)
                {
                    inst.State = AudioState.Stopped;
                    OnStop?.Invoke(inst);
                }
            }

            // 混合简化音频源（PlayOneShot）
            lock (_simpleSourceLock)
            {
                for (int i = _simpleSources.Count - 1; i >= 0; i--)
                {
                    var source = _simpleSources[i];
                    bool finished = source.ReadSamples(buffer, frames, channels);
                    if (finished)
                        _simpleSources.RemoveAt(i);
                }
            }

            // 应用主音量与软限幅
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = Math.Clamp(buffer[i] * _masterVolume, -1f, 1f);
            }
        }

        /// <summary>同步 ECS 实体上的音频源组件。</summary>
        private void SyncECSAudioSources()
        {
            if (_ecs == null) return;

            var entities = _ecs.QueryWithComponent<AdvancedAudioSourceComponent>();
            foreach (var entityId in entities)
            {
                var audioSrc = _ecs.GetComponent<AdvancedAudioSourceComponent>(entityId);
                var transform = _ecs.GetComponent<TransformComponent>(entityId);

                // 自动播放
                if (audioSrc.AutoPlay && audioSrc.InstanceId == Guid.Empty)
                {
                    var clip = GetClip(audioSrc.ClipId);
                    if (clip != null)
                    {
                        var inst = Play3D(clip, transform.Position, audioSrc.Volume, audioSrc.Pitch,
                            audioSrc.Loop, GetBusForSoundClass(audioSrc.SoundClassName),
                            audioSrc.SoundClassName, audioSrc.Priority);
                        if (inst != null)
                        {
                            audioSrc.InstanceId = inst.Id;
                            _ecs.SetComponent(entityId, audioSrc);
                        }
                    }
                }

                // 更新实例位置
                if (audioSrc.InstanceId != Guid.Empty)
                    UpdateInstancePosition(audioSrc.InstanceId, transform.Position);
            }
        }

        /// <summary>清理已停止的实例。</summary>
        private void CleanupStoppedInstances()
        {
            var stopped = _instances
                .Where(kvp => kvp.Value.State == AudioState.Stopped)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var id in stopped)
            {
                if (_instances.TryGetValue(id, out var inst))
                    UntrackClassInstance(inst.SoundClassName, id);
                _instances.Remove(id);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 关闭与统计
        // ══════════════════════════════════════════════════════════════

        /// <summary>释放音频引擎资源。</summary>
        public void Dispose()
        {
            StopAll();
            _instances.Clear();
            _classInstances.Clear();
            _streamingSources.Clear();
            _clips.Clear();
            _clipRegistry.Clear();

            if (_wasapiOutput != null)
            {
                _wasapiOutput.Dispose();
                _wasapiOutput = null;
            }

            if (_backend != null)
            {
                _backend.Stop();
                _backend.Shutdown();
                _backend = null;
            }

            BusGraph.Reset();
            BufferPool.Clear();
            GC.SuppressFinalize(this);
            Log("高级音频引擎已释放");
        }

        /// <summary>关闭音频引擎。</summary>
        public void Shutdown()
        {
            StopAll();
            _wasapiOutput?.Stop();
            _wasapiOutput?.Dispose();
            _wasapiOutput = null;
            _backend?.Stop();
            _backend?.Shutdown();
            _instances.Clear();
            _clips.Clear();
            _streamingSources.Clear();
            Log("高级音频引擎已关闭");
        }

        /// <summary>获取统计信息。</summary>
        public string GetStatsString()
        {
            string backendName = _wasapiOutput != null ? "WASAPI" : _backend?.BackendType.ToString() ?? "None";
            return $"[AdvancedAudio] " +
                   $"实例={ActiveInstanceCount}/{MaxInstances} " +
                   $"剪辑={ClipCount} " +
                   $"总线={BusCount} " +
                   $"流式源={StreamingSourceCount} " +
                   $"峰值={MasterPeakLevel:F2} " +
                   $"后端={backendName} " +
                   $"缓冲池={BufferPool.PooledCount}";
        }

        private void Log(string msg) => OnLogMessage?.Invoke($"[AdvancedAudio] {msg}");
    }

    // ==============================================================================
    //  十六、辅助数据结构
    // ==============================================================================

    /// <summary>空间音频参数（3D 计算结果）。</summary>
    public struct SpatialAudioParams
    {
        public float Distance;
        public float DistanceGain;
        public float Azimuth;
        public float Elevation;
        public float OcclusionGain;
        public float TotalGain;
    }

    /// <summary>高级音频实例。</summary>
    public class AdvancedAudioInstance
    {
        public Guid Id = Guid.NewGuid();
        public AudioClip? Clip;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Volume = 1f;
        public float Pitch = 1f;
        public float PlaybackTime;
        public AudioState State;
        public bool Is3D;
        public bool Loop;
        public float MinDistance = 1f;
        public float MaxDistance = 500f;
        public float RolloffFactor = 1f;
        public AdvancedAudioBus? Bus;
        public float FadeDuration;
        public float FadeTarget;
        public bool EnableOcclusion = true;
        public bool EnableReflections;
        public float SpatialGain = 1f;
        public float SpatialAzimuth;
        public float SpatialElevation;
        public float SpatialDistance;
        public float SpatialPan;
        public int ReflectionCount;
        public string SoundClassName = "SFX";
        public int Priority = 100;
        public DateTime StartTime = DateTime.Now;
        public float EffectiveClassVolume = 1f;
        public float EffectiveClassPitch = 1f;
    }
}
