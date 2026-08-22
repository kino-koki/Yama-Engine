using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════════
    //  山雨引擎 (ShanyuEngine) —— 音频分析系统 (Audio Analysis System)
    //  文件：AudioAnalyzerSystem.cs
    //  命名空间：ShanyuEngine.Audio
    // ----------------------------------------------------------------------------
    //  本文件对齐 Unreal Engine 5 的音频分析能力（Audio Analyzer / Synesthesia / DSP），
    //  专注“分析侧”，提供实时频谱分析、频带分解、节拍检测、音符起始检测、音高检测、
    //  响度（LUFS）分析、音频特征提取、音频指纹，以及实时分析管线与 ECS 集成。
    //
    //  本文件不与已有 DSP 效果侧冲突：
    //    * 已有 ShanyuEngine.Audio.AudioSystem        —— 播放/总线/监听者（含 AudioInstance/AudioBus/AudioListener）
    //    * 已有 ShanyuEngine.Audio.Effects.AudioDspSystem —— 实时效果链（混响/EQ/压缩/延迟…）
    //    * 本文件仅做“读侧”分析，不修改 AudioSystem.cs，不写入效果链。
    //
    //  与现有系统的对接（签名严格对齐，请勿改动 AudioSystem.cs）：
    //    * ShanyuEngine.Audio.AudioSystem
    //        —— List<AudioBusState> GetBusStates()
    //        —— void Update(float dt)
    //        —— AudioInstance PlayOneShot(AudioClip?, Vector3, float volume=1, float pitch=1)
    //        —— void Stop(Guid instanceId, float fadeOut=0)
    //    * ShanyuEngine.Audio.AudioClip
    //        —— byte[] Data; int SampleRate; int Channels; float Duration;
    //           AudioType Type; string Name; string FilePath; bool Is3D;
    //    * ShanyuEngine.Audio.AudioInstance
    //        —— Guid Id; AudioClip Clip; Vector3 Position; float Volume; float Pitch;
    //           float PlaybackTime; AudioState State; AudioBus Bus;
    //    * ShanyuEngine.Audio.AudioBusState
    //        —— AudioBus Bus; float Volume; bool Muted; bool Solo;
    //           List<AudioInstance> Instances; float PeakLevel; float RMSLevel;
    //    * ShanyuEngine.ECS.ISystem { string Name{get;} void Update(float dt, EntityComponentSystem ecs); }
    //    * ShanyuEngine.ECS.IComponent {}  /  struct TransformComponent : IComponent { Vector3 Position; }
    //
    //  采样数据约定：
    //    AudioClip.Data 视为 16 位有符号 PCM（小端、交错）。分析时下混为单声道，每样本
    //    归一化到 [-1,1]。当 Data 为空或播放实例未提供实时样本时，分析器回落到基于
    //    PlaybackTime 的合成波形，保证分析管线始终产出数据（与 UE5 分析器在无源时仍可
    //    运行的行为一致）。
    //  FFT 实现采用 Cooley-Tukey radix-2 蝶形算法，操作于分离的 float[] 实部/虚部数组
    //  （对齐用户指定签名 Forward(float[] real, float[] imag)），仅要求长度为 2 的幂。
    // ════════════════════════════════════════════════════════════════════════════


    // ─────────────────────────────────────────────────────────────────────────────
    //  一、FFT 频谱分析
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 窗函数类型（对齐 UE5 Audio Analysis 的 FFT Window Type）。
    /// 执行 FFT 前对时域采样施加窗函数以减少频谱泄漏（spectral leakage）。
    /// 不同窗在“主瓣宽度（频率分辨率）”与“旁瓣抑制（幅度精度）”间取舍。
    /// </summary>
    public enum WindowFunction
    {
        /// <summary>矩形窗（不加窗）。频率分辨率最高，旁瓣泄漏最大。</summary>
        Rectangular,
        /// <summary>汉宁窗（Hann）。最常用，旁瓣抑制与主瓣宽度均衡。</summary>
        Hann,
        /// <summary>汉明窗（Hamming）。与 Hann 相近，旁瓣略高、主瓣略窄。</summary>
        Hamming,
        /// <summary>布莱克曼窗（Blackman）。旁瓣抑制最强，主瓣最宽。</summary>
        Blackman
    }

    /// <summary>
    /// 窗函数应用工具（静态类）。
    /// 由于 C# 枚举不能承载静态方法，窗函数的“枚举”与“应用方法”分离为
    /// <see cref="WindowFunction"/>（枚举）与本 <see cref="WindowFunctions"/>（静态应用方法）。
    /// 调用：<c>WindowFunctions.Apply(data, WindowFunction.Hann);</c>
    /// </summary>
    public static class WindowFunctions
    {
        /// <summary>
        /// 对时域采样数组原地施加指定窗函数（直接修改 data）。
        /// 矩形窗等价于不施加（直接返回）。
        /// </summary>
        /// <param name="data">时域采样（将被原地修改）。</param>
        /// <param name="type">窗函数类型。</param>
        public static void Apply(float[] data, WindowFunction type)
        {
            if (data == null || data.Length == 0) return;
            int n = data.Length;
            // 对称窗：w[i] = f(i/(n-1))，i∈[0,n-1]
            switch (type)
            {
                case WindowFunction.Rectangular:
                    // 矩形窗：w[i]=1，无需操作
                    return;

                case WindowFunction.Hann:
                    // Hann: w[i] = 0.5 * (1 - cos(2π i/(n-1)))
                    for (int i = 0; i < n; i++)
                    {
                        double t = 2.0 * Math.PI * i / (n - 1);
                        data[i] *= 0.5f * (1f - (float)Math.Cos(t));
                    }
                    return;

                case WindowFunction.Hamming:
                    // Hamming: w[i] = 0.54 - 0.46*cos(2π i/(n-1))
                    for (int i = 0; i < n; i++)
                    {
                        double t = 2.0 * Math.PI * i / (n - 1);
                        data[i] *= 0.54f - 0.46f * (float)Math.Cos(t);
                    }
                    return;

                case WindowFunction.Blackman:
                    // Blackman: w[i] = 0.42 - 0.5*cos(2π i/(n-1)) + 0.08*cos(4π i/(n-1))
                    for (int i = 0; i < n; i++)
                    {
                        double t1 = 2.0 * Math.PI * i / (n - 1);
                        double t2 = 4.0 * Math.PI * i / (n - 1);
                        data[i] *= 0.42f - 0.5f * (float)Math.Cos(t1) + 0.08f * (float)Math.Cos(t2);
                    }
                    return;
            }
        }

        /// <summary>
        /// 计算指定窗在某索引处的系数（不修改数据，便于按需缩放）。
        /// </summary>
        public static float Coefficient(int i, int n, WindowFunction type)
        {
            if (n <= 1) return 1f;
            switch (type)
            {
                case WindowFunction.Rectangular:
                    return 1f;
                case WindowFunction.Hann:
                    return 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (n - 1)));
                case WindowFunction.Hamming:
                    return 0.54f - 0.46f * (float)Math.Cos(2.0 * Math.PI * i / (n - 1));
                case WindowFunction.Blackman:
                    return 0.42f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / (n - 1))
                                 + 0.08f * (float)Math.Cos(4.0 * Math.PI * i / (n - 1));
                default:
                    return 1f;
            }
        }
    }

    /// <summary>
    /// 快速傅里叶变换工具类（Cooley-Tukey radix-2 蝶形算法）。
    /// 操作于分离的 <c>float[] real</c> 与 <c>float[] imag</c> 数组（原地变换）。
    /// 仅支持长度为 2 的幂的输入（256/512/1024/2048/4096…）。
    ///
    /// 算法分两步：
    ///   1) 位反转重排（bit-reversal permutation）：将输入按二进制位反转顺序重排；
    ///   2) 蝶形运算（butterfly）：自底向上合并，使用旋转因子 W = exp(-2πi/len)。
    /// </summary>

    /// <summary>
    /// PCM 采样工具：从 <see cref="AudioClip.Data"/>（16 位有符号小端交错 PCM）解码为
    /// 单声道 [-1,1] 浮点采样，并提供合成回退与按时间区间读取能力，供分析管线使用。
    /// </summary>
    public static class PcmSampleUtils
    {
        /// <summary>
        /// 将整个 AudioClip 解码为单声道浮点采样（立体声下混为单声道）。
        /// </summary>
        public static float[] ExtractMono(AudioClip clip)
        {
            if (clip?.Data == null || clip.Data.Length < 2) return Array.Empty<float>();
            int channels = Math.Max(1, clip.Channels);
            int frameCount = (clip.Data.Length / 2) / channels;
            float[] mono = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int bi = (i * channels + c) * 2;
                    if (bi + 1 < clip.Data.Length)
                    {
                        short s = (short)(clip.Data[bi] | (clip.Data[bi + 1] << 8));
                        sum += s / 32768f;
                    }
                }
                mono[i] = sum / channels;
            }
            return mono;
        }

        /// <summary>
        /// 从 <paramref name="startFrame"/> 起读取 <paramref name="count"/> 个单声道帧，
        /// 越界部分零填充。用于按播放位置读取“最近”的一段 PCM。
        /// </summary>
        public static float[] DecodeMonoRange(AudioClip clip, int startFrame, int count)
        {
            float[] outBuf = new float[count];
            if (clip?.Data == null || clip.Data.Length < 2) return outBuf;
            int channels = Math.Max(1, clip.Channels);
            for (int i = 0; i < count; i++)
            {
                int frame = startFrame + i;
                if (frame < 0) continue;
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int bi = (frame * channels + c) * 2;
                    if (bi + 1 < clip.Data.Length)
                    {
                        short s = (short)(clip.Data[bi] | (clip.Data[bi + 1] << 8));
                        sum += s / 32768f;
                    }
                }
                outBuf[i] = sum / channels;
            }
            return outBuf;
        }

        /// <summary>
        /// 合成回退波形：当无真实 PCM 时，基于起始时间生成含基频+谐波+周期低频鼓点的
        /// 合成信号，保证分析管线始终产出数据。信号随 <paramref name="startTime"/> 推进。
        /// </summary>
        public static float[] SynthesizeRange(float startTime, float volume, int sampleRate, int count)
        {
            float[] s = new float[count];
            float vol = Math.Clamp(volume, 0f, 1f);
            for (int i = 0; i < count; i++)
            {
                float t = startTime + (float)i / sampleRate;
                // 基频 110Hz(A2) + 谐波
                float v = MathF.Sin(t * 110f * 2f * MathF.PI) * 0.4f;
                v += MathF.Sin(t * 220f * 2f * MathF.PI) * 0.2f;
                v += MathF.Sin(t * 440f * 2f * MathF.PI) * 0.1f;
                // 周期性低频鼓点（~2Hz，模拟 Kick）
                float beatEnv = MathF.Max(0f, MathF.Sin(t * 2f * 2f * MathF.PI));
                v += MathF.Sin(t * 80f * 2f * MathF.PI) * 0.3f * beatEnv;
                s[i] = v * vol * 0.5f;
            }
            return s;
        }
    }

    /// <summary>
    /// 频谱分析器：对一段时域采样施加窗函数→FFT→取幅度谱（前 fftSize/2 个 bin）。
    /// 对齐 UE5 Synesthesia 的 RealTimeSpectrumAnalyzer 取向。
    /// </summary>


    // ─────────────────────────────────────────────────────────────────────────────
    //  二、频带分析
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 标准频带定义（对齐 UE5/Submission 频段划分与常见音频可视化频段）。
    /// Low 与 Bass/SubBass 在频率上有意重叠，分别用于“宽低频”与“细分低频”视图。
    /// </summary>
    public enum FrequencyBand
    {
        /// <summary>次低频 20-60Hz（SubBass，Kick 下限、体感低频）。</summary>
        SubBass,
        /// <summary>低音 60-250Hz（Bass，低音乐器基频区）。</summary>
        Bass,
        /// <summary>低频 20-250Hz（Low，宽低频聚合）。</summary>
        Low,
        /// <summary>中低频 250-500Hz（LowMid，人声厚度、低谐波）。</summary>
        LowMid,
        /// <summary>中频 500-2000Hz（Mid，人声/主奏核心区）。</summary>
        Mid,
        /// <summary>中高频 2000-4000Hz（HighMid，临场感、齿音下限）。</summary>
        HighMid,
        /// <summary>高频 4000-20000Hz（High，空气感、明亮度）。</summary>
        High
    }

    /// <summary>
    /// 频带频率范围映射工具（每个 <see cref="FrequencyBand"/> 对应 [MinHz, MaxHz]）。
    /// </summary>
    public static class FrequencyBandInfo
    {
        /// <summary>获取指定频带的下/上频率边界（Hz）。</summary>
        public static (float MinHz, float MaxHz) GetRange(FrequencyBand band)
        {
            return band switch
            {
                FrequencyBand.SubBass => (20f, 60f),
                FrequencyBand.Bass    => (60f, 250f),
                FrequencyBand.Low     => (20f, 250f),
                FrequencyBand.LowMid  => (250f, 500f),
                FrequencyBand.Mid     => (500f, 2000f),
                FrequencyBand.HighMid => (2000f, 4000f),
                FrequencyBand.High    => (4000f, 20000f),
                _ => (0f, 0f)
            };
        }
    }

    /// <summary>
    /// 频带分析器：将 FFT 幅度谱按频带聚合，输出各频带能量，并对各频带能量做时间平滑。
    /// 平滑采用单极低通：y = y_prev + α·(x - y_prev)，α = 1 - exp(-Δt/τ)，
    /// τ 为 <see cref="TimeConstant"/>（秒），由管线每帧通过 <see cref="DeltaTime"/> 提供 Δt。
    /// </summary>
    public class BandAnalyzer
    {
        /// <summary>时间常数 τ（秒），越大越平滑、响应越慢。</summary>
        public float TimeConstant = 0.12f;

        /// <summary>每帧 Δt（秒），由管线在调用 AnalyzeBands 前设置。</summary>
        public float DeltaTime { get; set; } = 0.016667f;

        // 各频带上一次平滑后的能量
        private readonly Dictionary<FrequencyBand, float> _smoothed = new();

        /// <summary>
        /// 把幅度谱按频带聚合，返回各频带能量（RMS-like：频带内幅度平方均值的平方根）。
        /// 结果同时经过时间平滑。
        /// </summary>
        /// <param name="spectrum">幅度谱（长度 fftSize/2）。</param>
        /// <param name="sampleRate">采样率。</param>
        /// <param name="fftSize">FFT 长度。</param>
        public Dictionary<FrequencyBand, float> AnalyzeBands(float[] spectrum, int sampleRate, int fftSize)
        {
            var result = new Dictionary<FrequencyBand, float>();
            // 平滑系数 α = 1 - exp(-Δt/τ)
            float alpha = 1f - (float)Math.Exp(-DeltaTime / Math.Max(TimeConstant, 1e-4f));

            foreach (FrequencyBand band in Enum.GetValues(typeof(FrequencyBand)))
            {
                var (lo, hi) = FrequencyBandInfo.GetRange(band);
                int binLo = (int)(lo * fftSize / sampleRate);
                int binHi = (int)(hi * fftSize / sampleRate);
                if (binLo < 0) binLo = 0;
                if (binHi >= spectrum.Length) binHi = spectrum.Length - 1;

                double sumSq = 0;
                int count = 0;
                for (int b = binLo; b <= binHi; b++)
                {
                    float m = spectrum[b];
                    sumSq += m * m;
                    count++;
                }
                float energy = count > 0 ? (float)Math.Sqrt(sumSq / count) : 0f;

                // 时间平滑
                _smoothed.TryGetValue(band, out float prev);
                float smoothed = prev + alpha * (energy - prev);
                _smoothed[band] = smoothed;
                result[band] = smoothed;
            }
            return result;
        }

        /// <summary>重置各频带平滑历史（如切换分析源时调用）。</summary>
        public void Reset() => _smoothed.Clear();
    }


    // ─────────────────────────────────────────────────────────────────────────────
    //  三、节拍检测
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节拍检测器：基于低频能量突变 + 局部峰值 + 节拍间隔一致性的节拍检测算法。
    ///
    /// 算法概要：
    ///   1) 用单极低通滤波器从时域采样中分离低频成分（Kick/Bass 区，截止 ~150Hz）；
    ///   2) 计算本帧低频瞬时能量 instEnergy；
    ///   3) 维护历史能量窗口，得局部均值 avg；阈值 = avg * Sensitivity；
    ///   4) 当 instEnergy > 阈值、且为本帧局部峰值、且距上一拍间隔在 [Min,Max] 区间内，
    ///      判定为一拍；
    ///   5) 由节拍间隔历史的中位数估计 BPM；由间隔方差估计置信度；由距上一拍时长/拍长
    ///      估计节拍相位 Phase（0~1）。
    /// </summary>
    public class BeatDetector
    {
        /// <summary>灵敏度（阈值 = 局部均值 × Sensitivity），越大越不敏感。</summary>
        public float Sensitivity = 1.5f;

        /// <summary>最低 BPM（用于过滤过密节拍）。</summary>
        public float MinBPM = 60f;

        /// <summary>最高 BPM。</summary>
        public float MaxBPM = 180f;

        /// <summary>历史能量窗口长度（帧数）。</summary>
        public int HistoryLength = 43;

        /// <summary>低通截止频率（Hz），用于分离低频节拍成分。</summary>
        public float CutoffHz = 150f;

        /// <summary>采样率（由管线设置，默认 44100）。</summary>
        public int SampleRate { get; set; } = 44100;

        /// <summary>当前估计 BPM。</summary>
        public float BPM { get; private set; }

        /// <summary>节拍相位（0~1，0=恰逢一拍）。</summary>
        public float Phase { get; private set; }

        /// <summary>节拍置信度（0~1，间隔越一致越高）。</summary>
        public float Confidence { get; private set; }

        private readonly Queue<float> _energyHistory = new();
        private readonly List<float> _beatTimes = new();      // 最近的节拍时间戳
        private readonly List<float> _intervals = new();      // 最近的节拍间隔
        private float _lowPassState;
        private float _lastEnergy;
        private float _lastBeatTime = -1f;
        private float _currentTime;
        private const int MaxBeatHistory = 24;

        /// <summary>
        /// 处理一帧时域采样，返回本帧是否检测到节拍。
        /// </summary>
        /// <param name="samples">单声道时域采样。</param>
        /// <param name="dt">本帧时长（秒）。</param>
        public bool Process(float[] samples, float dt)
        {
            _currentTime += dt;
            if (samples == null || samples.Length == 0) return false;

            // —— 1) 单极低通分离低频成分 ——
            // 离散 RC 低通：y[n] = y[n-1] + α·(x[n]-y[n-1])，α = dt/(RC+dt)，RC = 1/(2π·fc)
            float rc = 1f / (2f * MathF.PI * CutoffHz);
            float alpha = dt / (rc + dt);
            float lp = _lowPassState;
            double energySum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                lp += alpha * (samples[i] - lp);
                energySum += lp * lp;
            }
            _lowPassState = lp;
            float instEnergy = (float)(energySum / samples.Length);

            // —— 2) 历史均值与阈值 ——
            _energyHistory.Enqueue(instEnergy);
            while (_energyHistory.Count > HistoryLength) _energyHistory.Dequeue();

            double avg = 0;
            foreach (var e in _energyHistory) avg += e;
            avg /= Math.Max(1, _energyHistory.Count);
            float threshold = (float)avg * Sensitivity;

            // 节拍间隔上下限（秒）
            float minInterval = 60f / MaxBPM;
            float maxInterval = 60f / MinBPM;

            bool isBeat = false;
            // —— 3) 局部峰值 + 阈值 + 间隔约束 ——
            if (instEnergy > threshold &&
                instEnergy > _lastEnergy * 1.05f &&
                (_lastBeatTime < 0f || (_currentTime - _lastBeatTime) >= minInterval))
            {
                isBeat = true;
                if (_lastBeatTime >= 0f)
                {
                    float interval = _currentTime - _lastBeatTime;
                    if (interval <= maxInterval)
                    {
                        _intervals.Add(interval);
                        if (_intervals.Count > MaxBeatHistory) _intervals.RemoveAt(0);
                        _beatTimes.Add(_currentTime);
                        if (_beatTimes.Count > MaxBeatHistory) _beatTimes.RemoveAt(0);

                        // BPM 取间隔中位数（更抗噪）
                        float medInterval = Median(_intervals);
                        if (medInterval > 0f)
                            BPM = Math.Clamp(60f / medInterval, MinBPM, MaxBPM);

                        // 置信度：间隔方差越小越高
                        Confidence = ComputeConfidence(_intervals);
                    }
                }
                _lastBeatTime = _currentTime;
            }
            _lastEnergy = instEnergy;

            // —— 4) 节拍相位 ——
            float beatLen = BPM > 0f ? 60f / BPM : maxInterval;
            if (_lastBeatTime >= 0f)
                Phase = ((_currentTime - _lastBeatTime) / beatLen) % 1f;
            else
                Phase = 0f;

            return isBeat;
        }

        /// <summary>重置检测器状态（切换分析源时调用）。</summary>
        public void Reset()
        {
            _energyHistory.Clear();
            _beatTimes.Clear();
            _intervals.Clear();
            _lowPassState = 0f;
            _lastEnergy = 0f;
            _lastBeatTime = -1f;
            _currentTime = 0f;
            BPM = 0f;
            Phase = 0f;
            Confidence = 0f;
        }

        private static float Median(List<float> list)
        {
            if (list.Count == 0) return 0f;
            var sorted = new List<float>(list);
            sorted.Sort();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? 0.5f * (sorted[mid - 1] + sorted[mid]) : sorted[mid];
        }

        // 由间隔的变异系数 CV = σ/μ 计算置信度：CV 越小越一致。
        private static float ComputeConfidence(List<float> intervals)
        {
            if (intervals.Count < 2) return 0f;
            double mean = 0;
            foreach (var v in intervals) mean += v;
            mean /= intervals.Count;
            if (mean <= 0) return 0f;
            double var = 0;
            foreach (var v in intervals) var += (v - mean) * (v - mean);
            var /= intervals.Count;
            double cv = Math.Sqrt(var) / mean;
            return Math.Clamp(1f - (float)cv, 0f, 1f);
        }
    }

    /// <summary>
    /// 音符起始检测器（Onset Detection）：基于频谱通量（Spectral Flux）法。
    ///
    /// 算法：对相邻两帧幅度谱，仅累加正差分（能量增长）作为通量 flux；
    ///       以通量的局部均值 × 阈值作为自适应阈值，flux 超阈值且为局部峰值时判定为 onset。
    /// 对齐 UE5 Synesthesia Onset 的“能量增长”取向。
    /// </summary>
    public class OnsetDetector
    {
        /// <summary>自适应阈值倍数（阈值 = 局部通量均值 × Threshold）。</summary>
        public float Threshold = 1.5f;

        /// <summary>通量历史窗口长度（帧数）。</summary>
        public int HistoryLength = 20;

        /// <summary>本帧频谱通量。</summary>
        public float LastFlux { get; private set; }

        /// <summary>本帧是否检测到 onset。</summary>
        public bool IsOnset { get; private set; }

        private float[] _prevSpectrum;
        private readonly Queue<float> _fluxHistory = new();
        private float _prevFlux;

        /// <summary>
        /// 处理一帧幅度谱，返回频谱通量，并更新 <see cref="IsOnset"/>。
        /// </summary>
        public float Process(float[] spectrum)
        {
            if (spectrum == null || spectrum.Length == 0) { LastFlux = 0f; IsOnset = false; return 0f; }

            if (_prevSpectrum == null || _prevSpectrum.Length != spectrum.Length)
                _prevSpectrum = new float[spectrum.Length];

            // 仅累加正差分（能量增长部分）
            double flux = 0;
            for (int i = 0; i < spectrum.Length; i++)
            {
                float diff = spectrum[i] - _prevSpectrum[i];
                if (diff > 0f) flux += diff;
                _prevSpectrum[i] = spectrum[i];
            }
            float f = (float)(flux / spectrum.Length);
            LastFlux = f;

            // 自适应阈值：局部均值 × Threshold
            _fluxHistory.Enqueue(f);
            while (_fluxHistory.Count > HistoryLength) _fluxHistory.Dequeue();
            double mean = 0;
            foreach (var v in _fluxHistory) mean += v;
            mean /= Math.Max(1, _fluxHistory.Count);

            IsOnset = f > mean * Threshold && f > _prevFlux;
            _prevFlux = f;
            return f;
        }

        /// <summary>重置历史（切换分析源时调用）。</summary>
        public void Reset()
        {
            _prevSpectrum = null;
            _fluxHistory.Clear();
            _prevFlux = 0f;
            LastFlux = 0f;
            IsOnset = false;
        }
    }


    // ─────────────────────────────────────────────────────────────────────────────
    //  四、音高检测
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 音高检测器：提供自相关法与 YIN 算法（改进的差函数），并对结果做中位数滤波平滑。
    ///
    /// YIN 算法步骤：
    ///   1) 差函数 d(τ)=Σ(x[i]-x[i+τ])²；
    ///   2) 累积均值归一化差函数 d'(τ)（d'[0]=1）；
    ///   3) 绝对阈值：找到第一个低于阈值的 τ，再取其局部极小；
    ///   4) 抛物线插值精化 τ，pitch = sampleRate / τ。
    /// 返回 0 表示无声/无法判定。
    /// </summary>
    public class PitchDetector
    {
        /// <summary>可检测最低频率（Hz）。</summary>
        public float MinFrequency = 50f;

        /// <summary>可检测最高频率（Hz）。</summary>
        public float MaxFrequency = 2000f;

        /// <summary>YIN 绝对阈值（典型 0.1~0.15）。</summary>
        public float YinThreshold = 0.12f;

        /// <summary>中位数滤波窗口（奇数，0=不平滑）。</summary>
        public int MedianFilterSize = 5;

        private readonly List<float> _history = new();

        /// <summary>
        /// 检测一帧采样的基频音高（Hz）。0 表示无声/无法判定。
        /// 优先 YIN，失败则回退自相关；最后做中位数平滑。
        /// </summary>
        public float DetectPitch(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length < 4) return 0f;

            // 静音判定：RMS 过低直接返回 0
            double sumSq = 0;
            for (int i = 0; i < samples.Length; i++) sumSq += samples[i] * samples[i];
            float rms = (float)Math.Sqrt(sumSq / samples.Length);
            if (rms < 0.01f)
            {
                _history.Add(0f);
                TrimHistory();
                return Median(_history);
            }

            float pitch = Yin(samples, sampleRate);
            if (pitch <= 0f) pitch = Autocorrelation(samples, sampleRate);

            _history.Add(pitch);
            TrimHistory();
            return MedianFilterSize > 1 ? Median(_history) : pitch;
        }

        private void TrimHistory()
        {
            int size = Math.Max(1, MedianFilterSize);
            while (_history.Count > size) _history.RemoveAt(0);
        }

        /// <summary>YIN 算法实现。</summary>
        private float Yin(float[] buffer, int sampleRate)
        {
            int halfLen = buffer.Length / 2;
            int minTau = Math.Max(1, (int)(sampleRate / MaxFrequency));
            int maxTau = Math.Min(halfLen, (int)(sampleRate / MinFrequency));
            if (maxTau <= minTau) return 0f;

            int W = halfLen; // 差函数窗口长度
            // 1) 差函数 d(τ)
            float[] diff = new float[maxTau + 1];
            for (int tau = minTau; tau <= maxTau; tau++)
            {
                double sum = 0;
                for (int i = 0; i < W; i++)
                {
                    float d = buffer[i] - buffer[i + tau];
                    sum += d * d;
                }
                diff[tau] = (float)sum;
            }
            // 2) 累积均值归一化
            diff[0] = 1f;
            double running = 0;
            for (int tau = 1; tau <= maxTau; tau++)
            {
                running += diff[tau];
                diff[tau] = running > 0 ? (float)(diff[tau] * tau / running) : 1f;
            }
            // 3) 绝对阈值 + 局部极小
            int tauEst = -1;
            for (int tau = minTau; tau <= maxTau; tau++)
            {
                if (diff[tau] < YinThreshold)
                {
                    while (tau + 1 <= maxTau && diff[tau + 1] < diff[tau]) tau++;
                    tauEst = tau;
                    break;
                }
            }
            if (tauEst < 0)
            {
                // 回退：取全局最小（仍受 minTau/maxTau 约束）
                float minVal = float.MaxValue;
                for (int tau = minTau; tau <= maxTau; tau++)
                    if (diff[tau] < minVal) { minVal = diff[tau]; tauEst = tau; }
                if (tauEst < 0 || minVal > 0.5f) return 0f;
            }
            // 4) 抛物线插值精化
            float betterTau = tauEst;
            if (tauEst > 0 && tauEst < maxTau)
            {
                float s0 = diff[tauEst - 1], s1 = diff[tauEst], s2 = diff[tauEst + 1];
                float denom = s0 + s2 - 2f * s1;
                if (Math.Abs(denom) > 1e-6f)
                    betterTau = tauEst + 0.5f * (s0 - s2) / denom;
            }
            return betterTau > 0 ? sampleRate / betterTau : 0f;
        }

        /// <summary>自相关法音高检测（YIN 失败时的回退）。</summary>
        private float Autocorrelation(float[] buffer, int sampleRate)
        {
            int minTau = Math.Max(1, (int)(sampleRate / MaxFrequency));
            int maxTau = Math.Min(buffer.Length / 2, (int)(sampleRate / MinFrequency));
            if (maxTau <= minTau) return 0f;

            float bestCorr = 0f;
            int bestTau = -1;
            for (int tau = minTau; tau <= maxTau; tau++)
            {
                double corr = 0;
                for (int i = 0; i < maxTau; i++)
                    corr += buffer[i] * buffer[i + tau];
                if (corr > bestCorr) { bestCorr = (float)corr; bestTau = tau; }
            }
            return bestTau > 0 ? sampleRate / bestTau : 0f;
        }

        private static float Median(List<float> list)
        {
            if (list.Count == 0) return 0f;
            var sorted = new List<float>(list);
            sorted.Sort();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? 0.5f * (sorted[mid - 1] + sorted[mid]) : sorted[mid];
        }

        /// <summary>重置历史。</summary>
        public void Reset() => _history.Clear();
    }


    // ─────────────────────────────────────────────────────────────────────────────
    //  五、响度分析
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 响度分析器（对齐 ITU-R BS.1770 K-weighted 响度，简化实现）。
    ///
    /// K 计权 = 两级滤波：Stage1 高架（~1.5kHz 提升）+ Stage2 高通/RLB（~38Hz 去除低频）。
    /// 响度（LUFS）= -0.691 + 10·log10( mean_square( K-weighted signal ) )。
    ///
    /// 提供：
    ///   * MomentaryLoudness   —— 瞬时响度（400ms 窗口，无门限）；
    ///   * ShortTermLoudness   —— 短期响度（3s 窗口，无门限）；
    ///   * IntegratedLoudness  —— 积分响度（400ms/75% 重叠块 + 绝对/相对门限）；
    ///   * TruePeak            —— 真峰值（4× 线性过采样峰值，dBFS）。
    ///
    /// 说明：本实现为“简化”版本——K 计权用 RBJ cookbook 系数近似 BS.1770 滤波器，
    /// TruePeak 用线性插值 4× 过采样近似（规范为 4× sinc）。足以驱动可视化与游戏逻辑。
    /// </summary>
    public class LoudnessAnalyzer
    {
        /// <summary>瞬时响度（LUFS，400ms）。</summary>
        public float MomentaryLoudness { get; private set; } = -70f;

        /// <summary>短期响度（LUFS，3s）。</summary>
        public float ShortTermLoudness { get; private set; } = -70f;

        /// <summary>积分响度（LUFS，带门限）。</summary>
        public float IntegratedLoudness { get; private set; } = -70f;

        /// <summary>真峰值（dBFS，4× 过采样）。</summary>
        public float TruePeak { get; private set; } = -100f;

        /// <summary>采样率（由管线设置）。</summary>
        public int SampleRate { get; set; } = 44100;

        // K 计权两级双二阶滤波器
        private readonly Biquad _stage1 = new Biquad(); // 高架
        private readonly Biquad _stage2 = new Biquad(); // 高通（RLB）

        // 环形缓冲：存放近 3s 的 K 计权样本，供滑窗响度计算
        private float[] _ring;
        private int _ringHead;
        private int _ringCount;
        private int _ringCapacity;

        // 积分响度：100ms 步进的 400ms 块功率（mean square）列表
        private readonly List<float> _blockPowers = new();
        private double _blockAccumSq; // 当前块累积平方和
        private int _blockSampleCount;
        private float _blockTimer;    // 当前块计时（秒）

        // TruePeak 4× 过采样状态
        private float _prevWeighted;

        // 常量
        private const float MomentaryWindow = 0.4f;  // 400ms
        private const float ShortTermWindow = 3.0f;  // 3s
        private const float BlockHop = 0.1f;         // 100ms
        private const float BlockWindow = 0.4f;      // 400ms
        private const float AbsGateLufs = -70f;      // 绝对门限
        private const float RelGateDb = -10f;        // 相对门限（LUFS - 10）
        private const float LufsOffset = -0.691f;

        public LoudnessAnalyzer()
        {
            // 初始化为占位容量，首帧按采样率重建
            _ring = new float[1];
            _ringCapacity = 1;
        }

        /// <summary>
        /// 处理一帧 K 计权样本并更新所有响度/真峰值。
        /// </summary>
        public void Process(float[] samples, int sampleRate, float dt)
        {
            if (samples == null || samples.Length == 0) return;
            SampleRate = sampleRate;
            EnsureCapacity(sampleRate);
            ConfigureFilters(sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float x = samples[i];
                // K 计权：两级滤波
                float w = _stage1.Process(x);
                w = _stage2.Process(w);

                // 入环形缓冲
                _ring[_ringHead] = w;
                _ringHead = (_ringHead + 1) % _ringCapacity;
                if (_ringCount < _ringCapacity) _ringCount++;

                // —— TruePeak 4× 线性过采样 ——
                UpdateTruePeak(_prevWeighted, w);
                _prevWeighted = w;

                // —— 积分块累积 ——
                _blockAccumSq += (double)w * w;
                _blockSampleCount++;
            }

            // —— 瞬时 / 短期响度（滑窗 mean square）——
            int momentaryN = (int)(MomentaryWindow * sampleRate);
            int shortTermN = (int)(ShortTermWindow * sampleRate);
            MomentaryLoudness = LufsFromWindow(momentaryN);
            ShortTermLoudness = LufsFromWindow(shortTermN);

            // —— 积分响度：按 100ms 步进提交块 ——
            _blockTimer += dt;
            while (_blockTimer >= BlockHop)
            {
                _blockTimer -= BlockHop;
                if (_blockSampleCount > 0)
                {
                    // 以最近 400ms 滑窗作为该块的 mean square（更贴近规范的重叠块）
                    int blockN = (int)(BlockWindow * sampleRate);
                    float blockMs = MeanSquare(blockN);
                    _blockPowers.Add(blockMs);
                    // 限制块数量上限，避免长时段内存膨胀
                    if (_blockPowers.Count > 60000) _blockPowers.RemoveAt(0);
                }
            }
            IntegratedLoudness = ComputeIntegrated();
        }

        /// <summary>从最近 N 个 K 计权样本计算 LUFS。</summary>
        private float LufsFromWindow(int n)
        {
            float ms = MeanSquare(n);
            return ms > 0 ? LufsOffset + 10f * MathF.Log10(ms) : -70f;
        }

        /// <summary>最近 N 个样本的 mean square（不足时按已有样本计）。</summary>
        private float MeanSquare(int n)
        {
            if (_ringCount == 0 || n <= 0) return 0f;
            int count = Math.Min(n, _ringCount);
            double sum = 0;
            int start = (_ringHead - count + _ringCapacity) % _ringCapacity;
            for (int i = 0; i < count; i++)
            {
                float v = _ring[(start + i) % _ringCapacity];
                sum += (double)v * v;
            }
            return (float)(sum / count);
        }

        /// <summary>带绝对门限 + 相对门限的积分响度。</summary>
        private float ComputeIntegrated()
        {
            if (_blockPowers.Count == 0) return -70f;

            // 1) 绝对门限：丢弃 LUFS < -70 的块
            var kept = new List<float>();
            foreach (var ms in _blockPowers)
            {
                float lufs = ms > 0 ? LufsOffset + 10f * MathF.Log10(ms) : -70f;
                if (lufs >= AbsGateLufs) kept.Add(ms);
            }
            if (kept.Count == 0) return -70f;

            // 2) 相对门限：先算未门限均值（LUFS），相对门限 = 该均值 - 10
            double sumKept = 0;
            foreach (var ms in kept) sumKept += ms;
            double meanMs = sumKept / kept.Count;
            float meanLufs = meanMs > 0 ? LufsOffset + 10f * MathF.Log10((float)meanMs) : -70f;
            float relGateLufs = meanLufs + RelGateDb;

            double gatedSum = 0;
            int gatedCount = 0;
            foreach (var ms in kept)
            {
                float lufs = ms > 0 ? LufsOffset + 10f * MathF.Log10(ms) : -70f;
                if (lufs >= relGateLufs) { gatedSum += ms; gatedCount++; }
            }
            if (gatedCount == 0) return meanLufs;
            double gatedMs = gatedSum / gatedCount;
            return gatedMs > 0 ? LufsOffset + 10f * MathF.Log10((float)gatedMs) : -70f;
        }

        /// <summary>4× 线性过采样更新真峰值。</summary>
        private void UpdateTruePeak(float prev, float cur)
        {
            float maxAbs = Math.Max(Math.Abs(prev), Math.Abs(cur));
            // 3 个内插点
            float i1 = prev + 0.25f * (cur - prev);
            float i2 = prev + 0.5f * (cur - prev);
            float i3 = prev + 0.75f * (cur - prev);
            maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(i1), Math.Max(Math.Abs(i2), Math.Abs(i3))));
            if (maxAbs > _truePeakValue) _truePeakValue = maxAbs;
            TruePeak = _truePeakValue > 0 ? 20f * MathF.Log10(_truePeakValue) : -100f;
        }
        private float _truePeakValue;

        private void EnsureCapacity(int sampleRate)
        {
            int need = (int)(ShortTermWindow * sampleRate) + 8;
            if (_ringCapacity < need)
            {
                var nr = new float[need];
                _ring = nr;
                _ringCapacity = need;
                _ringHead = 0;
                _ringCount = 0;
            }
        }

        private bool _filtersConfigured;
        private int _configuredRate;
        private void ConfigureFilters(int sampleRate)
        {
            if (_filtersConfigured && _configuredRate == sampleRate) return;
            _configuredRate = sampleRate;
            _filtersConfigured = true;
            // Stage1：高架，+4dB @ 1500Hz（近似 BS.1770 第一级）
            _stage1.SetHighShelf(1500f, sampleRate, 4f, 0.707f);
            // Stage2：高通 RLB @ 38Hz（近似 BS.1770 第二级）
            _stage2.SetHighPass(38f, sampleRate, 0.5f);
        }

        /// <summary>重置分析器（切换分析源时调用）。</summary>
        public void Reset()
        {
            _stage1.Reset(); _stage2.Reset();
            _ringHead = 0; _ringCount = 0;
            _blockPowers.Clear();
            _blockAccumSq = 0; _blockSampleCount = 0; _blockTimer = 0;
            _prevWeighted = 0; _truePeakValue = 0;
            MomentaryLoudness = ShortTermLoudness = IntegratedLoudness = -70f;
            TruePeak = -100f;
        }

        /// <summary>
        /// 双二阶（biquad）滤波器，用 RBJ cookbook 系数实现高架/高通等。
        /// 作为 LoudnessAnalyzer 的私有嵌套类型，避免污染命名空间。
        /// </summary>
        private class Biquad
        {
            private float _a1, _a2, _b0, _b1, _b2;
            private float _z1, _z2;

            // 直接型 II 转置（Transpose Direct Form II）
            public float Process(float x)
            {
                float y = _b0 * x + _z1;
                _z1 = _b1 * x - _a1 * y + _z2;
                _z2 = _b2 * x - _a2 * y;
                return y;
            }

            public void Reset() { _z1 = 0; _z2 = 0; }

            /// <summary>高架滤波器（RBJ cookbook）。</summary>
            public void SetHighShelf(float freq, float sampleRate, float gainDb, float q)
            {
                float A = MathF.Pow(10f, gainDb / 40f);
                float w0 = 2f * MathF.PI * freq / sampleRate;
                float cosW = MathF.Cos(w0), sinW = MathF.Sin(w0);
                float alpha = sinW / (2f * q);
                float a0 = (A + 1) - (A - 1) * cosW + 2f * MathF.Sqrt(A) * alpha;
                _b0 = (A * ((A + 1) - (A - 1) * cosW + 2f * MathF.Sqrt(A) * alpha)) / a0;
                _b1 = (2f * A * ((A - 1) - (A + 1) * cosW)) / a0;
                _b2 = (A * ((A + 1) - (A - 1) * cosW - 2f * MathF.Sqrt(A) * alpha)) / a0;
                _a1 = (-2f * ((A - 1) - (A + 1) * cosW)) / a0;
                _a2 = ((A + 1) - (A - 1) * cosW - 2f * MathF.Sqrt(A) * alpha) / a0;
            }

            /// <summary>高通滤波器（RBJ cookbook）。</summary>
            public void SetHighPass(float freq, float sampleRate, float q)
            {
                float w0 = 2f * MathF.PI * freq / sampleRate;
                float cosW = MathF.Cos(w0), sinW = MathF.Sin(w0);
                float alpha = sinW / (2f * q);
                float a0 = 1f + alpha;
                _b0 = ((1f + cosW) / 2f) / a0;
                _b1 = (-(1f + cosW)) / a0;
                _b2 = ((1f + cosW) / 2f) / a0;
                _a1 = (-2f * cosW) / a0;
                _a2 = (1f - alpha) / a0;
            }
        }
    }


    // ─────────────────────────────────────────────────────────────────────────────
    //  六、音频特征
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 音频特征集：频谱质心、频谱滚降、频谱平坦度、零交叉率、RMS、峰值。
    /// 对齐 UE5 Synesthesia 的 Spectral Analysis 特征取向。
    /// </summary>
    public class AudioFeatures
    {
        /// <summary>频谱质心（Hz）：幅度加权的平均频率，表征声音“亮度”。</summary>
        public float SpectralCentroid;

        /// <summary>频谱滚降（Hz）：累积能量达 85% 时的频率，表征频谱分布偏态。</summary>
        public float SpectralRolloff;

        /// <summary>频谱平坦度（0~1）：几何均值/算术均值，越接近 1 越似噪声。</summary>
        public float SpectralFlatness;

        /// <summary>零交叉率（比例）：信号过零次数/样本数，与清音/浊音相关。</summary>
        public float ZeroCrossingRate;

        /// <summary>RMS（均方根，0~1）：时域能量度量。</summary>
        public float RMS;

        /// <summary>峰值（0~1）：时域最大绝对值。</summary>
        public float Peak;

        /// <summary>
        /// 从时域采样与幅度谱提取特征。
        /// </summary>
        /// <param name="samples">单声道时域采样。</param>
        /// <param name="spectrum">幅度谱（长度 fftSize/2）。</param>
        /// <param name="sampleRate">采样率。</param>
        public static AudioFeatures Extract(float[] samples, float[] spectrum, int sampleRate)
        {
            var f = new AudioFeatures();
            if (samples != null && samples.Length > 0)
            {
                double sumSq = 0; float peak = 0; int zc = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    float v = samples[i];
                    sumSq += (double)v * v;
                    float av = Math.Abs(v);
                    if (av > peak) peak = av;
                    if (i > 0 && (samples[i - 1] >= 0) != (v >= 0)) zc++;
                }
                f.RMS = (float)Math.Sqrt(sumSq / samples.Length);
                f.Peak = peak;
                f.ZeroCrossingRate = (float)zc / samples.Length;
            }

            if (spectrum != null && spectrum.Length > 0)
            {
                int fftSize = spectrum.Length * 2;
                double sumMag = 0, sumFreqMag = 0, sumSqMag = 0;
                double logSum = 0; // 几何均值用对数和
                int validForGeo = 0;
                double totalEnergy = 0;
                for (int i = 0; i < spectrum.Length; i++)
                {
                    float m = spectrum[i];
                    double freq = (double)i * sampleRate / fftSize;
                    sumMag += m;
                    sumFreqMag += freq * m;
                    sumSqMag += (double)m * m;
                    totalEnergy += (double)m * m;
                    if (m > 1e-12) { logSum += Math.Log(m); validForGeo++; }
                }
                // 频谱质心
                f.SpectralCentroid = sumMag > 0 ? (float)(sumFreqMag / sumMag) : 0f;
                // 频谱平坦度 = geomean / arithmean
                if (validForGeo > 0 && sumMag > 0)
                {
                    double geo = Math.Exp(logSum / validForGeo);
                    double arith = sumMag / spectrum.Length;
                    f.SpectralFlatness = arith > 0 ? (float)(geo / arith) : 0f;
                }
                // 频谱滚降（85% 能量）
                double threshold = 0.85 * totalEnergy;
                double cum = 0; int rolloffBin = spectrum.Length - 1;
                for (int i = 0; i < spectrum.Length; i++)
                {
                    cum += (double)spectrum[i] * spectrum[i];
                    if (cum >= threshold) { rolloffBin = i; break; }
                }
                f.SpectralRolloff = (float)rolloffBin * sampleRate / fftSize;
            }
            return f;
        }
    }

    /// <summary>
    /// 音频指纹器（简化 Chromaprint 风格）。
    ///
    /// 流程：分帧（加窗 FFT）→ 映射到 12 色度（chroma）能量 → 与上一帧色度比较，
    ///       每个 chroma 位的“增/减”编码为 1 bit，拼成 int 哈希序列。
    /// 用于音频识别/匹配（简化版，非完整 Chromaprint）。
    /// </summary>
    public class AudioFingerprinter
    {
        /// <summary>FFT 长度。</summary>
        public int FftSize = 4096;

        /// <summary>帧移（样本）。</summary>
        public int HopSize = 2048;

        /// <summary>窗函数。</summary>
        public WindowFunction Window = WindowFunction.Hann;

        // C0 = 16.35Hz，用于 freq -> note 转换
        private const float C0 = 16.3515978313f;

        /// <summary>
        /// 计算音频指纹（int 哈希序列，每帧一个 int，低 12 位为色度变化标志）。
        /// </summary>
        public int[] ComputeFingerprint(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length < FftSize) return Array.Empty<int>();
            int frameCount = (samples.Length - FftSize) / HopSize + 1;
            var hashes = new List<int>(frameCount);
            float[] prevChroma = null;
            var analyzer = new SpectrumAnalyzer();
            float[] windowed = new float[FftSize];

            for (int f = 0; f < frameCount; f++)
            {
                int start = f * HopSize;
                Array.Copy(samples, start, windowed, 0, FftSize);
                float[] spectrum = analyzer.Analyze(windowed, sampleRate, FftSize, Window);
                float[] chroma = ComputeChroma(spectrum, sampleRate);

                if (prevChroma != null)
                {
                    int hash = 0;
                    for (int i = 0; i < 12; i++)
                    {
                        // 色度能量上升 -> 该位置 1
                        if (chroma[i] > prevChroma[i]) hash |= (1 << i);
                    }
                    hashes.Add(hash);
                }
                prevChroma = chroma;
            }
            return hashes.ToArray();
        }

        /// <summary>把幅度谱映射为 12 维色度能量（按 pitch class 聚合）。</summary>
        private float[] ComputeChroma(float[] spectrum, int sampleRate)
        {
            float[] chroma = new float[12];
            int fftSize = spectrum.Length * 2;
            for (int i = 1; i < spectrum.Length; i++)
            {
                float freq = (float)i * sampleRate / fftSize;
                if (freq < 32f || freq > 5000f) continue; // 限定有效范围
                double note = 12.0 * Math.Log(freq / C0, 2.0);
                int cls = ((int)Math.Round(note) % 12 + 12) % 12;
                chroma[cls] += spectrum[i] * spectrum[i];
            }
            return chroma;
        }
    }


    // ─────────────────────────────────────────────────────────────────────────────
    //  七、实时分析管线
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 分析快照：聚合某一帧的全部分析结果，供可视化与游戏逻辑读取。
    /// </summary>
    public class AudioAnalysisSnapshot
    {
        /// <summary>时间戳（秒，自管线启动累计）。</summary>
        public double Timestamp;

        /// <summary>幅度谱（长度 fftSize/2）。</summary>
        public float[] Spectrum;

        /// <summary>每个 bin 的中心频率（Hz，与 Spectrum 等长）。</summary>
        public float[] BinFrequencies;

        /// <summary>各频带能量。</summary>
        public Dictionary<FrequencyBand, float> BandEnergies;

        /// <summary>本帧是否检测到节拍。</summary>
        public bool BeatDetected;

        /// <summary>当前 BPM 估计。</summary>
        public float BPM;

        /// <summary>节拍相位（0~1）。</summary>
        public float BeatPhase;

        /// <summary>节拍置信度（0~1）。</summary>
        public float BeatConfidence;

        /// <summary>本帧是否检测到 onset。</summary>
        public bool OnsetDetected;

        /// <summary>频谱通量。</summary>
        public float SpectralFlux;

        /// <summary>检测到的音高（Hz，0=无声）。</summary>
        public float Pitch;

        /// <summary>瞬时响度（LUFS）。</summary>
        public float MomentaryLoudness;

        /// <summary>短期响度（LUFS）。</summary>
        public float ShortTermLoudness;

        /// <summary>积分响度（LUFS）。</summary>
        public float IntegratedLoudness;

        /// <summary>真峰值（dBFS）。</summary>
        public float TruePeak;

        /// <summary>音频特征集。</summary>
        public AudioFeatures Features;
    }

    /// <summary>
    /// 实时分析管线：挂接到一个 <see cref="AudioInstance"/> 或 <see cref="AudioBus"/>，
    /// 每帧拉取最近 PCM（环形缓冲存储最近 N ms），执行 FFT→频带→节拍/音高/响度/特征，
    /// 产出 <see cref="AudioAnalysisSnapshot"/>。
    ///
    /// 对齐 UE5 Audio Analysis 的“实时分析器挂接到 Sound Wave / Submix”的工作流。
    /// </summary>
    public class AudioAnalysisPipeline
    {
        /// <summary>FFT 长度（2 的幂）。</summary>
        public int FftSize = 1024;

        /// <summary>窗函数。</summary>
        public WindowFunction Window = WindowFunction.Hann;

        /// <summary>环形缓冲存储的最近时长（ms）。</summary>
        public int BufferMs = 300;

        /// <summary>是否启用各子分析（可按需关闭以省 CPU）。</summary>
        public bool EnableBeat = true;
        public bool EnableOnset = true;
        public bool EnablePitch = true;
        public bool EnableLoudness = true;
        public bool EnableFeatures = true;

        // 子分析器
        public SpectrumAnalyzer SpectrumAnalyzer { get; } = new();
        public BandAnalyzer BandAnalyzer { get; } = new();
        public BeatDetector BeatDetector { get; } = new();
        public OnsetDetector OnsetDetector { get; } = new();
        public PitchDetector PitchDetector { get; } = new();
        public LoudnessAnalyzer LoudnessAnalyzer { get; } = new();

        /// <summary>最近一次快照。</summary>
        public AudioAnalysisSnapshot LastSnapshot { get; private set; }

        // —— 挂接目标 ——
        private AudioInstance _instance;
        private AudioBus? _bus;
        private AudioSystem _system;
        private bool _attached;

        // —— 环形缓冲（存储最近 N ms 单声道 PCM）——
        private float[] _ring;
        private int _ringHead;
        private int _ringCount;
        private int _ringCapacity;

        // —— 状态 ——
        private int _sampleRate = 44100;
        private float _elapsedTime;
        private float _synthClock; // 合成回退时钟（无真实 PCM 时推进）

        /// <summary>挂接到播放实例（从该实例的 Clip 取 PCM）。</summary>
        public void AttachToInstance(AudioInstance instance, AudioSystem system)
        {
            _instance = instance;
            _bus = null;
            _system = system;
            _attached = instance != null;
            if (instance?.Clip != null && instance.Clip.SampleRate > 0)
                _sampleRate = instance.Clip.SampleRate;
            ResetState();
        }

        /// <summary>挂接到音频总线（混合总线上所有实例的 PCM）。</summary>
        public void AttachToBus(AudioBus bus, AudioSystem system)
        {
            _bus = bus;
            _instance = null;
            _system = system;
            _attached = system != null;
            ResetState();
        }

        /// <summary>停止挂接。</summary>
        public void Detach()
        {
            _attached = false;
            _instance = null;
            _bus = null;
            _system = null;
        }

        private void ResetState()
        {
            _ringHead = 0; _ringCount = 0;
            _elapsedTime = 0f; _synthClock = 0f;
            BeatDetector.Reset(); OnsetDetector.Reset();
            PitchDetector.Reset(); LoudnessAnalyzer.Reset();
            BandAnalyzer.Reset();
            LastSnapshot = null;
        }

        /// <summary>
        /// 每帧更新：拉取本帧新 PCM → 推入环形缓冲 → 取最近 fftSize 窗 → 全维度分析。
        /// </summary>
        public void Update(float dt)
        {
            if (!_attached || dt <= 0f) return;
            _elapsedTime += dt;
            int sr = ResolveSampleRate();
            EnsureCapacity(sr);

            // 本帧新样本数（按真实流逝时间）
            int numNew = (int)Math.Ceiling(dt * sr);
            if (numNew <= 0) numNew = 1;
            // 限制单帧推入量，避免极端 dt 导致溢出
            if (numNew > _ringCapacity) numNew = _ringCapacity;

            float[] newSamples = PullElapsedSamples(numNew, sr, dt);
            for (int i = 0; i < numNew; i++) PushRing(newSamples[i]);

            // 取最近 fftSize 窗口用于分析（不足时已含零填充）
            float[] window = ReadLast(FftSize);

            // 同步子分析器采样率/帧时间
            BeatDetector.SampleRate = sr;
            LoudnessAnalyzer.SampleRate = sr;
            BandAnalyzer.DeltaTime = dt;

            // —— FFT 频谱 ——
            float[] spectrum = SpectrumAnalyzer.Analyze(window, sr, FftSize, Window);

            // —— 频带 ——
            var bands = BandAnalyzer.AnalyzeBands(spectrum, sr, FftSize);

            // —— 节拍 ——
            bool beat = EnableBeat && BeatDetector.Process(window, dt);

            // —— onset（频谱通量）——
            float flux = 0f;
            bool onset = false;
            if (EnableOnset) { flux = OnsetDetector.Process(spectrum); onset = OnsetDetector.IsOnset; }

            // —— 音高 ——
            float pitch = EnablePitch ? PitchDetector.DetectPitch(window, sr) : 0f;

            // —— 响度 ——
            if (EnableLoudness) LoudnessAnalyzer.Process(window, sr, dt);

            // —— 特征 ——
            AudioFeatures features = EnableFeatures
                ? AudioFeatures.Extract(window, spectrum, sr) : new AudioFeatures();

            // —— 组装快照 ——
            LastSnapshot = new AudioAnalysisSnapshot
            {
                Timestamp = _elapsedTime,
                Spectrum = spectrum,
                BinFrequencies = SpectrumAnalyzer.GetCenterFrequencies(FftSize, sr),
                BandEnergies = bands,
                BeatDetected = beat,
                BPM = BeatDetector.BPM,
                BeatPhase = BeatDetector.Phase,
                BeatConfidence = BeatDetector.Confidence,
                OnsetDetected = onset,
                SpectralFlux = flux,
                Pitch = pitch,
                MomentaryLoudness = LoudnessAnalyzer.MomentaryLoudness,
                ShortTermLoudness = LoudnessAnalyzer.ShortTermLoudness,
                IntegratedLoudness = LoudnessAnalyzer.IntegratedLoudness,
                TruePeak = LoudnessAnalyzer.TruePeak,
                Features = features
            };
        }

        /// <summary>获取最近一次快照（若无则返回 null）。</summary>
        public AudioAnalysisSnapshot GetSnapshot() => LastSnapshot;

        // —— PCM 拉取 —— 

        /// <summary>解析当前采样率：优先实例 Clip.SampleRate，否则默认。</summary>
        private int ResolveSampleRate()
        {
            if (_instance?.Clip != null && _instance.Clip.SampleRate > 0)
                return _instance.Clip.SampleRate;
            return _sampleRate > 0 ? _sampleRate : 44100;
        }

        /// <summary>
        /// 拉取本帧新流逝的 numNew 个单声道样本。
        /// 实例模式：从 Clip.Data 按 PlaybackTime 读取最近窗口；
        /// 总线模式：混合总线上各实例的最近窗口；
        /// 无真实 PCM：合成回退（随 _synthClock 推进）。
        /// </summary>
        private float[] PullElapsedSamples(int numNew, int sr, float dt)
        {
            // 实例模式
            if (_instance != null)
            {
                var clip = _instance.Clip;
                if (clip?.Data != null && clip.Data.Length >= 2)
                {
                    int curSample = (int)(_instance.PlaybackTime * sr);
                    return PcmSampleUtils.DecodeMonoRange(clip, curSample - numNew, numNew);
                }
                // 合成回退
                float[] s = PcmSampleUtils.SynthesizeRange(_synthClock, _instance.Volume, sr, numNew);
                _synthClock += dt;
                return s;
            }

            // 总线模式
            if (_bus != null && _system != null)
            {
                var busState = FindBusState(_bus.Value);
                if (busState != null && busState.Instances.Count > 0)
                {
                    float[] mix = new float[numNew];
                    int n = 0;
                    foreach (var inst in busState.Instances)
                    {
                        if (inst?.Clip?.Data == null || inst.Clip.Data.Length < 2) continue;
                        int curSample = (int)(inst.PlaybackTime * sr);
                        float[] s = PcmSampleUtils.DecodeMonoRange(inst.Clip, curSample - numNew, numNew);
                        for (int i = 0; i < numNew; i++) mix[i] += s[i];
                        n++;
                    }
                    if (n > 0)
                    {
                        float inv = 1f / n;
                        for (int i = 0; i < numNew; i++) mix[i] *= inv;
                        return mix;
                    }
                }
                // 总线无可用实例 -> 合成回退
                float[] s2 = PcmSampleUtils.SynthesizeRange(_synthClock, 0.8f, sr, numNew);
                _synthClock += dt;
                return s2;
            }

            return new float[numNew];
        }

        /// <summary>从 AudioSystem 查找指定总线的状态。</summary>
        private AudioBusState FindBusState(AudioBus bus)
        {
            if (_system == null) return null;
            foreach (var st in _system.GetBusStates())
                if (st.Bus == bus) return st;
            return null;
        }

        // —— 环形缓冲操作 —— 

        private void EnsureCapacity(int sr)
        {
            int need = Math.Max(FftSize, (int)(BufferMs / 1000f * sr) + 8);
            if (_ring == null || _ringCapacity < need)
            {
                _ring = new float[need];
                _ringCapacity = need;
                _ringHead = 0; _ringCount = 0;
            }
        }

        private void PushRing(float s)
        {
            _ring[_ringHead] = s;
            _ringHead = (_ringHead + 1) % _ringCapacity;
            if (_ringCount < _ringCapacity) _ringCount++;
        }

        /// <summary>读取最近 n 个样本（按时间顺序，不足部分零填充）。</summary>
        private float[] ReadLast(int n)
        {
            float[] outBuf = new float[n];
            int count = Math.Min(n, _ringCount);
            int start = (_ringHead - count + _ringCapacity) % _ringCapacity;
            int offset = n - count; // 不足部分前置 0
            for (int i = 0; i < count; i++)
                outBuf[offset + i] = _ring[(start + i) % _ringCapacity];
            return outBuf;
        }
    }


    // ─────────────────────────────────────────────────────────────────────────────
    //  八、分析与数据回填（ECS 集成 + 调试）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ECS 组件：将一条 <see cref="AudioAnalysisPipeline"/> 绑定到实体，并记录最近快照。
    /// 必须为 struct : IComponent（ECS 要求 struct 约束）。
    /// 因 Pipeline/Snapshot 为引用类型，struct 内仅持有引用；系统在更新后用
    /// SetComponent 回填最新快照，保证数据持久化。
    /// </summary>
    public struct AudioAnalyzerComponent : IComponent
    {
        /// <summary>绑定的分析管线（由外部创建并 AttachToInstance/Bus 后赋值）。</summary>
        public AudioAnalysisPipeline Pipeline;

        /// <summary>最近一次分析快照（由系统每帧回填）。</summary>
        public AudioAnalysisSnapshot LatestSnapshot;

        /// <summary>是否启用本实体的分析（关闭则系统跳过）。</summary>
        public bool Enabled;

        /// <summary>（可选）绑定的音频实例 ID，便于编辑器检视。</summary>
        public Guid BoundInstanceId;

        /// <summary>（可选）绑定的总线，便于编辑器检视。</summary>
        public AudioBus? BoundBus;
    }

    /// <summary>
    /// ECS 系统：遍历所有持有 <see cref="AudioAnalyzerComponent"/> 的实体，驱动各管线 Update，
    /// 并把最新快照回填到组件。实现 <see cref="ISystem"/>。
    /// </summary>
    public class AudioAnalyzerSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "AudioAnalyzerSystem";

        /// <summary>
        /// 可选：若希望系统在发现未挂接管线但指定了 BoundBus/InstanceId 时自动创建管线，
        /// 可设此引用。默认 null（不自动创建，管线由外部装配）。
        /// </summary>
        public AudioSystem AudioSystem;

        public void Update(float dt, EntityComponentSystem ecs)
        {
            // 查询所有带 AudioAnalyzerComponent 的实体
            List<int> entities = ecs.QueryWithComponent<AudioAnalyzerComponent>();
            foreach (int entityId in entities)
            {
                // struct 以值拷贝取出
                AudioAnalyzerComponent comp = ecs.GetComponent<AudioAnalyzerComponent>(entityId);
                if (!comp.Enabled) continue;
                if (comp.Pipeline == null)
                {
                    // 尝试按需自动挂接（若提供了 AudioSystem 且组件指定了总线/实例）
                    TryAutoAttach(ref comp);
                    if (comp.Pipeline == null) continue;
                }

                // 驱动管线
                comp.Pipeline.Update(dt);
                // 回填最新快照
                comp.LatestSnapshot = comp.Pipeline.GetSnapshot();
                // 写回（因 struct 为值类型，必须 SetComponent 持久化修改）
                ecs.SetComponent(entityId, comp);
            }
        }

        /// <summary>按组件配置尝试自动创建并挂接管线（编辑器/便捷装配场景）。</summary>
        private void TryAutoAttach(ref AudioAnalyzerComponent comp)
        {
            if (AudioSystem == null) return;
            comp.Pipeline = new AudioAnalysisPipeline { EnableBeat = true };
            if (comp.BoundBus.HasValue)
            {
                comp.Pipeline.AttachToBus(comp.BoundBus.Value, AudioSystem);
            }
            else
            {
                // 按 BoundInstanceId 在总线实例中查找
                AudioInstance target = null;
                foreach (var st in AudioSystem.GetBusStates())
                    foreach (var inst in st.Instances)
                        if (inst.Id == comp.BoundInstanceId) { target = inst; break; }
                if (target != null) comp.Pipeline.AttachToInstance(target, AudioSystem);
            }
        }
    }

    /// <summary>
    /// 分析调试器：返回当前频谱/频带数据供编辑器绘制频谱图，并提供快照格式化文本。
    /// 对齐 UE5 Audio Analyzer 的调试视图取向。
    /// </summary>
    public class AudioAnalysisDebugger
    {
        /// <summary>是否在频谱图上叠加频带分隔。</summary>
        public bool ShowBandMarkers = true;

        /// <summary>获取快照的幅度谱（用于绘制频谱图）。</summary>
        public float[] GetSpectrum(AudioAnalysisSnapshot snapshot)
        {
            return snapshot?.Spectrum;
        }

        /// <summary>获取快照的 bin 中心频率数组。</summary>
        public float[] GetFrequencies(AudioAnalysisSnapshot snapshot)
        {
            return snapshot?.BinFrequencies;
        }

        /// <summary>获取快照的频带能量。</summary>
        public Dictionary<FrequencyBand, float> GetBands(AudioAnalysisSnapshot snapshot)
        {
            return snapshot?.BandEnergies;
        }

        /// <summary>
        /// 把幅度谱归一化到 [0,1] 并可选下采样到 maxBins 条，便于编辑器柱状图绘制。
        /// </summary>
        public float[] GetNormalizedSpectrum(AudioAnalysisSnapshot snapshot, int maxBins)
        {
            float[] spec = snapshot?.Spectrum;
            if (spec == null || spec.Length == 0) return Array.Empty<float>();
            float max = 0;
            for (int i = 0; i < spec.Length; i++) if (spec[i] > max) max = spec[i];
            if (max <= 0) max = 1;

            if (maxBins <= 0 || maxBins >= spec.Length)
            {
                float[] norm = new float[spec.Length];
                for (int i = 0; i < spec.Length; i++) norm[i] = spec[i] / max;
                return norm;
            }
            else
            {
                float[] down = new float[maxBins];
                float binPerOut = (float)spec.Length / maxBins;
                for (int i = 0; i < maxBins; i++)
                {
                    int lo = (int)(i * binPerOut);
                    int hi = (int)((i + 1) * binPerOut);
                    if (hi <= lo) hi = lo + 1;
                    float m = 0;
                    for (int b = lo; b < hi && b < spec.Length; b++) if (spec[b] > m) m = spec[b];
                    down[i] = m / max;
                }
                return down;
            }
        }

        /// <summary>把快照格式化为单行调试文本（便于日志/屏显）。</summary>
        public string FormatSnapshot(AudioAnalysisSnapshot s)
        {
            if (s == null) return "[AudioAnalyzer] 无快照";
            return string.Format(
                "[AudioAnalyzer] t={0:F2}s BPM={1:F1}(conf={2:F2}) beat={3} onset={4} " +
                "pitch={5:F1}Hz M={6:F1} S={7:F1} I={8:F1}LUFS TP={9:F1}dBFS " +
                "cent={10:F0}Hz flat={11:F2} rms={12:F3}",
                s.Timestamp, s.BPM, s.BeatConfidence, s.BeatDetected, s.OnsetDetected,
                s.Pitch, s.MomentaryLoudness, s.ShortTermLoudness, s.IntegratedLoudness,
                s.TruePeak,
                s.Features?.SpectralCentroid ?? 0f, s.Features?.SpectralFlatness ?? 0f,
                s.Features?.RMS ?? 0f);
        }

        /// <summary>把频带能量格式化为文本（便于日志/屏显）。</summary>
        public string FormatBands(AudioAnalysisSnapshot s)
        {
            if (s?.BandEnergies == null) return "[Bands] 无数据";
            var sb = new System.Text.StringBuilder("[Bands] ");
            foreach (FrequencyBand b in Enum.GetValues(typeof(FrequencyBand)))
            {
                float e = s.BandEnergies.TryGetValue(b, out var v) ? v : 0f;
                sb.Append(b).Append('=').Append(e.ToString("F3")).Append(' ');
            }
            return sb.ToString().TrimEnd();
        }
    }
}
