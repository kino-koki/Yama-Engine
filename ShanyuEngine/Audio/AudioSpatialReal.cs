// ============================================================================
// ShanyuEngine AudioSpatialReal.cs
// 山雨引擎 - 真实3D空间音频实现
// 包含: HRTF卷积、距离衰减、多普勒效应、空气吸收、遮挡/遮蔽、
//       卷积混响(分区卷积)、算法混响(Schroeder-Moorer/Freeverb)、声道映射。
// ============================================================================

using System;
using System.Collections.Generic;

namespace ShanyuEngine.Audio
{
    // ========================================================================
    // HRTF - 头相关传输函数
    // ========================================================================

    /// <summary>
    /// HRTF数据 - 某个方位角/仰角的双耳脉冲响应。
    /// </summary>

    /// <summary>
    /// HRTF卷积器 - 使用分区卷积(Overlap-Save)进行FFT加速的双耳卷积。
    /// output[n] = sum(h[k] * input[n-k])
    /// </summary>
    public sealed class HRTFConvolver
    {
        private readonly int _blockSize;
        private readonly int _fftSize;
        private HRTFData _hrtf;

        // 分区卷积状态 - 左耳
        private readonly List<double[]> _leftFFTPartitions;
        private readonly List<double[]> _rightFFTPartitions;
        private readonly Queue<double[]> _inputHistory; // 存储历史输入块的FFT
        private readonly double[] _overlapBuffer;
        private readonly double[] _inputBlock;
        private int _partitionCount;

        public HRTFData CurrentHRTF => _hrtf;

        public HRTFConvolver(int blockSize = 256, int sampleRate = 44100)
        {
            _blockSize = blockSize;
            _fftSize = blockSize * 2;
            _leftFFTPartitions = new List<double[]>();
            _rightFFTPartitions = new List<double[]>();
            _inputHistory = new Queue<double[]>();
            _overlapBuffer = new double[_blockSize];
            _inputBlock = new double[_fftSize];

            // 默认HRTF - 简单的直达声(单位脉冲+微小延迟模拟双耳时间差)
            float[] leftIR = new float[] { 1.0f };
            float[] rightIR = new float[] { 0.0f, 0.0f, 0.0f, 0.8f }; // 右耳延迟3样本(时间差)
            SetHRTF(new HRTFData(0, 0, leftIR, rightIR, sampleRate));
        }

        /// <summary>
        /// 设置HRTF并预计算频域分区。
        /// </summary>
        public void SetHRTF(HRTFData hrtf)
        {
            _hrtf = hrtf;
            _leftFFTPartitions.Clear();
            _rightFFTPartitions.Clear();
            _inputHistory.Clear();
            _partitionCount = 0;

            int irLen = Math.Max(hrtf.LeftIR?.Length ?? 1, hrtf.RightIR?.Length ?? 1);
            // 将IR分为_blockSize大小的段
            int numPartitions = (irLen + _blockSize - 1) / _blockSize;
            _partitionCount = numPartitions;

            for (int p = 0; p < numPartitions; p++)
            {
                double[] leftSeg = new double[_fftSize];
                double[] leftSegImag = new double[_fftSize];
                double[] rightSeg = new double[_fftSize];
                double[] rightSegImag = new double[_fftSize];

                for (int i = 0; i < _blockSize; i++)
                {
                    int idx = p * _blockSize + i;
                    if (hrtf.LeftIR != null && idx < hrtf.LeftIR.Length)
                        leftSeg[i] = hrtf.LeftIR[idx];
                    if (hrtf.RightIR != null && idx < hrtf.RightIR.Length)
                        rightSeg[i] = hrtf.RightIR[idx];
                }

                // 预计算FFT
                FFT.Forward(leftSeg, leftSegImag);
                FFT.Forward(rightSeg, rightSegImag);

                // 合并实部虚部到一个数组(交错存储)
                double[] leftFFT = new double[_fftSize * 2];
                double[] rightFFT = new double[_fftSize * 2];
                for (int i = 0; i < _fftSize; i++)
                {
                    leftFFT[i * 2] = leftSeg[i];
                    leftFFT[i * 2 + 1] = leftSegImag[i];
                    rightFFT[i * 2] = rightSeg[i];
                    rightFFT[i * 2 + 1] = rightSegImag[i];
                }
                _leftFFTPartitions.Add(leftFFT);
                _rightFFTPartitions.Add(rightFFT);
            }

            // 预填充输入历史为0
            for (int i = 0; i < numPartitions; i++)
            {
                double[] emptyFFT = new double[_fftSize * 2];
                _inputHistory.Enqueue(emptyFFT);
            }

            Array.Clear(_overlapBuffer, 0, _blockSize);
        }

        /// <summary>
        /// 处理一个输入块，输出双耳(左/右)结果。
        /// 使用Overlap-Save分区卷积。
        /// </summary>
        public void Process(float[] input, float[] leftOutput, float[] rightOutput)
        {
            int n = Math.Min(input.Length, _blockSize);

            // 构建FFT输入: [overlap | new input]
            for (int i = 0; i < _blockSize; i++)
                _inputBlock[i] = _overlapBuffer[i];
            for (int i = 0; i < n; i++)
                _inputBlock[_blockSize + i] = input[i];

            // 保存overlap用于下一块
            for (int i = 0; i < _blockSize; i++)
                _overlapBuffer[i] = n > 0 && _blockSize + i < _blockSize + n ? input[n - _blockSize + i >= 0 ? n - _blockSize + i : 0] : 0;
            // 更简单的overlap：保存最后_blockSize个输入样本
            for (int i = 0; i < _blockSize; i++)
            {
                int srcIdx = input.Length - _blockSize + i;
                _overlapBuffer[i] = srcIdx >= 0 && srcIdx < input.Length ? input[srcIdx] : 0;
            }

            // 计算输入FFT
            double[] inputReal = new double[_fftSize];
            double[] inputImag = new double[_fftSize];
            Array.Copy(_inputBlock, inputReal, _fftSize);
            FFT.Forward(inputReal, inputImag);

            // 存储到历史
            double[] inputFFT = new double[_fftSize * 2];
            for (int i = 0; i < _fftSize; i++)
            {
                inputFFT[i * 2] = inputReal[i];
                inputFFT[i * 2 + 1] = inputImag[i];
            }
            _inputHistory.Enqueue(inputFFT);
            if (_inputHistory.Count > _partitionCount)
                _inputHistory.Dequeue();

            // 频域乘法+累加
            double[] accLeftReal = new double[_fftSize];
            double[] accLeftImag = new double[_fftSize];
            double[] accRightReal = new double[_fftSize];
            double[] accRightImag = new double[_fftSize];

            var historyArray = _inputHistory.ToArray();
            for (int p = 0; p < _partitionCount && p < historyArray.Length; p++)
            {
                double[] irLeft = _leftFFTPartitions[p];
                double[] irRight = _rightFFTPartitions[p];
                double[] inFFT = historyArray[historyArray.Length - 1 - p];

                for (int i = 0; i < _fftSize; i++)
                {
                    // 复数乘法: (a+bi)(c+di) = (ac-bd) + (ad+bc)i
                    double inR = inFFT[i * 2];
                    double inI = inFFT[i * 2 + 1];

                    double lr = irLeft[i * 2], li = irLeft[i * 2 + 1];
                    accLeftReal[i] += inR * lr - inI * li;
                    accLeftImag[i] += inR * li + inI * lr;

                    double rr = irRight[i * 2], ri = irRight[i * 2 + 1];
                    accRightReal[i] += inR * rr - inI * ri;
                    accRightImag[i] += inR * ri + inI * rr;
                }
            }

            // IFFT
            FFT.Inverse(accLeftReal, accLeftImag);
            FFT.Inverse(accRightReal, accRightImag);

            // 取后半部分(Overlap-Save丢弃前半)
            for (int i = 0; i < _blockSize && i < leftOutput.Length; i++)
            {
                leftOutput[i] = (float)accLeftReal[_blockSize + i];
                rightOutput[i] = (float)accRightReal[_blockSize + i];
            }
        }

        public void Reset()
        {
            _inputHistory.Clear();
            for (int i = 0; i < _partitionCount; i++)
                _inputHistory.Enqueue(new double[_fftSize * 2]);
            Array.Clear(_overlapBuffer, 0, _blockSize);
        }
    }

    // ========================================================================
    // 距离衰减
    // ========================================================================

    /// <summary>
    /// 距离衰减模型类型
    /// </summary>

    /// <summary>
    /// 距离衰减 - 根据声源距离计算增益。
    /// </summary>
    public sealed class DistanceAttenuation
    {
        public AttenuationModel Model { get; set; } = AttenuationModel.Inverse;
        public double ReferenceDistance { get; set; } = 1.0;  // 参考距离(米)
        public double MaxDistance { get; set; } = 100.0;      // 最大距离
        public double RolloffFactor { get; set; } = 1.0;      // 衰减系数

        /// <summary>自定义衰减曲线: key=距离, value=增益(0~1)</summary>
        public List<(double distance, double gain)> CustomCurve { get; set; }

        public DistanceAttenuation()
        {
            CustomCurve = new List<(double, double)>();
        }

        /// <summary>
        /// 计算距离衰减增益
        /// </summary>
        public double ComputeGain(double distance)
        {
            if (distance <= ReferenceDistance) return 1.0;
            if (distance >= MaxDistance) return 0.0;

            switch (Model)
            {
                case AttenuationModel.Inverse:
                    // gain = refDist / (refDist + rolloff * (dist - refDist))
                    return ReferenceDistance / (ReferenceDistance + RolloffFactor * (distance - ReferenceDistance));

                case AttenuationModel.Linear:
                    // gain = 1 - (dist - refDist) / (maxDist - refDist)
                    return 1.0 - (distance - ReferenceDistance) / (MaxDistance - ReferenceDistance);

                case AttenuationModel.Exponential:
                    // gain = (dist / refDist) ^ (-rolloff)
                    return Math.Pow(distance / ReferenceDistance, -RolloffFactor);

                case AttenuationModel.Custom:
                    return EvaluateCustomCurve(distance);

                default:
                    return 1.0;
            }
        }

        private double EvaluateCustomCurve(double distance)
        {
            if (CustomCurve == null || CustomCurve.Count == 0) return 1.0;
            if (CustomCurve.Count == 1) return CustomCurve[0].gain;

            // 按距离排序
            var sorted = new List<(double distance, double gain)>(CustomCurve);
            sorted.Sort((a, b) => a.distance.CompareTo(b.distance));

            if (distance <= sorted[0].distance) return sorted[0].gain;
            if (distance >= sorted[sorted.Count - 1].distance) return sorted[sorted.Count - 1].gain;

            // 线性插值
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (distance >= sorted[i].distance && distance <= sorted[i + 1].distance)
                {
                    double t = (distance - sorted[i].distance) / (sorted[i + 1].distance - sorted[i].distance);
                    return sorted[i].gain * (1.0 - t) + sorted[i + 1].gain * t;
                }
            }
            return 1.0;
        }
    }

    // ========================================================================
    // 多普勒效应
    // ========================================================================

    /// <summary>
    /// 多普勒效应 - 声源与听众相对运动导致音高变化。
    /// pitch = soundSpeed / (soundSpeed + relativeVelocity)
    /// </summary>
    public sealed class DopplerEffect
    {
        public const double SoundSpeed = 343.2; // m/s (20°C空气中)

        public double DopplerFactor { get; set; } = 1.0; // 效果强度倍数
        public double MaxPitchShift { get; set; } = 4.0; // 最大变调倍数

        private double _currentPitch;
        private double _lastPitch;
        private double _smoothingCoeff;

        /// <summary>当前位置(用于计算距离变化率)</summary>
        private double _lastDistance;

        public DopplerEffect()
        {
            _currentPitch = 1.0;
            _lastPitch = 1.0;
            _smoothingCoeff = 0.01;
            _lastDistance = 0;
        }

        /// <summary>
        /// 计算多普勒音高比。
        /// listenerVelocity: 听众速度向量
        /// sourceVelocity: 声源速度向量
        /// sourcePos, listenerPos: 位置
        /// </summary>
        public double ComputePitchRatio(
            double sourcePosX, double sourcePosY, double sourcePosZ,
            double listenerPosX, double listenerPosY, double listenerPosZ,
            double sourceVelX, double sourceVelY, double sourceVelZ,
            double listenerVelX, double listenerVelY, double listenerVelZ,
            double dt)
        {
            // 声源到听众的方向向量
            double dx = listenerPosX - sourcePosX;
            double dy = listenerPosY - sourcePosY;
            double dz = listenerPosZ - sourcePosZ;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist < 0.0001) dist = 0.0001;

            // 单位方向向量(声源→听众)
            double nx = dx / dist;
            double ny = dy / dist;
            double nz = dz / dist;

            // 相对速度投影到声源→听众方向
            double relVelX = listenerVelX - sourceVelX;
            double relVelY = listenerVelY - sourceVelY;
            double relVelZ = listenerVelZ - sourceVelZ;

            // 听众朝声源方向运动的速度分量(正=靠近)
            double listenerRadialVel = relVelX * nx + relVelY * ny + relVelZ * nz;

            // 多普勒公式: pitch = c / (c + v_relative * dopplerFactor)
            // v_relative > 0 (靠近) → pitch > 1 (音高升高)
            // v_relative < 0 (远离) → pitch < 1 (音高降低)
            double effectiveVel = listenerRadialVel * DopplerFactor;
            double pitch = SoundSpeed / (SoundSpeed + effectiveVel);

            // 限制范围
            pitch = Math.Max(1.0 / MaxPitchShift, Math.Min(MaxPitchShift, pitch));

            // 平滑过渡(避免速度突变导致爆音)
            double smoothCoef = 1.0 - Math.Exp(-dt / _smoothingCoeff);
            _currentPitch += (pitch - _currentPitch) * smoothCoef;

            _lastDistance = dist;
            _lastPitch = pitch;
            return _currentPitch;
        }

        public double GetCurrentPitch() => _currentPitch;

        public void Reset()
        {
            _currentPitch = 1.0;
            _lastPitch = 1.0;
            _lastDistance = 0;
        }
    }

    // ========================================================================
    // 空气吸收
    // ========================================================================

    /// <summary>
    /// 空气吸收 - 高频随距离衰减，受温度和湿度影响。
    /// 使用随距离调整截止频率的低通滤波器模拟。
    /// </summary>
    public sealed class AirAbsorption
    {
        public double Temperature { get; set; } = 20.0;  // 摄氏度
        public double Humidity { get; set; } = 50.0;     // 相对湿度%
        public double MaxDistance { get; set; } = 100.0;

        private BiquadFilter _lowpass;
        private double _currentDistance;
        private double _sampleRate;

        public AirAbsorption(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _lowpass = new BiquadFilter(sampleRate);
            _lowpass.SetParameters(BiquadType.LowPass, 20000.0, 0.707);
            _currentDistance = 1.0;
        }

        /// <summary>
        /// 根据距离设置吸收量。距离越远，截止频率越低。
        /// 真实空气吸收约为 0.5-5 dB/100m @ 1kHz, 更高频率衰减更快。
        /// </summary>
        public void SetDistance(double distance)
        {
            _currentDistance = Math.Max(0.1, distance);

            // 截止频率随距离衰减 - 简化模型
            // 近距离: ~20kHz, 远距离: ~2kHz
            double distRatio = Math.Min(1.0, _currentDistance / MaxDistance);
            double cutoff = 20000.0 * Math.Pow(0.1, distRatio); // 指数衰减
            cutoff = Math.Max(500.0, cutoff);

            // 温度和湿度修正
            // 高湿度增加吸收(尤其在2-5kHz), 高温降低密度也影响
            double tempFactor = 1.0 + (Temperature - 20.0) * 0.005;
            double humidFactor = 1.0 - (Humidity - 50.0) * 0.002;
            cutoff *= tempFactor * humidFactor;
            cutoff = Math.Max(200.0, Math.Min(_sampleRate * 0.49, cutoff));

            _lowpass.SetParameters(BiquadType.LowPass, cutoff, 0.707);
        }

        public double Process(double sample)
        {
            return _lowpass.Process(sample);
        }

        public void ProcessBuffer(float[] buffer)
        {
            _lowpass.ProcessBuffer(buffer);
        }

        public void Reset()
        {
            _lowpass.Reset();
        }
    }

    // ========================================================================
    // 遮挡/遮蔽
    // ========================================================================

    /// <summary>
    /// 音频遮挡 - 检测声源到听众之间是否有障碍物，应用低通滤波。
    /// 遮蔽(部分遮挡)考虑衍射和边缘绕射。
    /// </summary>
    public sealed class AudioOcclusion
    {
        public double OcclusionFactor { get; set; }   // 0=无遮挡, 1=完全遮挡
        public double MinCutoff { get; set; } = 200.0; // 完全遮挡时截止频率
        public double MaxCutoff { get; set; } = 20000.0; // 无遮挡时截止频率

        private BiquadFilter _occlusionFilter;
        private BiquadFilter _diffractionFilter;
        private double _sampleRate;

        public AudioOcclusion(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            _occlusionFilter = new BiquadFilter(sampleRate);
            _diffractionFilter = new BiquadFilter(sampleRate);
            _occlusionFilter.SetParameters(BiquadType.LowPass, MaxCutoff, 0.707);
            _diffractionFilter.SetParameters(BiquadType.LowPass, MaxCutoff, 0.707);
            OcclusionFactor = 0.0;
        }

        /// <summary>
        /// 设置遮挡系数(0=无遮挡, 1=完全遮挡)
        /// </summary>
        public void SetOcclusion(double factor)
        {
            OcclusionFactor = Math.Max(0.0, Math.Min(1.0, factor));

            // 遮挡截止频率: 线性插值
            double occlusionCutoff = MaxCutoff * (1.0 - OcclusionFactor) + MinCutoff * OcclusionFactor;
            _occlusionFilter.SetParameters(BiquadType.LowPass, occlusionCutoff, 0.707);

            // 衍射(遮蔽): 截止频率略高, 用于部分遮挡的绕射效果
            double diffractionCutoff = MaxCutoff * (1.0 - OcclusionFactor * 0.5) + MinCutoff * 2.0 * OcclusionFactor * 0.5;
            _diffractionFilter.SetParameters(BiquadType.LowPass, diffractionCutoff, 0.707);
        }

        public double Process(double sample)
        {
            double occluded = _occlusionFilter.Process(sample);
            double diffracted = _diffractionFilter.Process(sample);
            // 混合: 高遮挡时以低通(遮挡)为主, 部分遮挡时衍射分量更多
            double diffractionAmount = (1.0 - OcclusionFactor) * 0.3 + OcclusionFactor * 0.1;
            return occluded * (1.0 - diffractionAmount) + diffracted * diffractionAmount;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            _occlusionFilter.Reset();
            _diffractionFilter.Reset();
        }
    }

    // ========================================================================
    // 卷积混响
    // ========================================================================

    /// <summary>
    /// 卷积混响 - 使用分区卷积(Overlap-Save)进行FFT加速。
    /// 每段N点FFT→频域乘法→IFFT→重叠相加。
    /// </summary>
    public sealed class ConvolutionReverb
    {
        private readonly int _blockSize;
        private readonly int _fftSize;
        private float[] _impulseResponse;
        private readonly List<double[]> _irPartitions; // 预计算的IR分区FFT
        private readonly Queue<double[]> _inputHistory;
        private readonly double[] _overlapBuffer;
        private readonly double[] _inputBlock;
        private int _partitionCount;
        private double _wet;
        private double _dry;
        private double _sampleRate;

        public double Wet { get => _wet; set => _wet = Math.Max(0, Math.Min(1, value)); }
        public double Dry { get => _dry; set => _dry = Math.Max(0, Math.Min(1, value)); }

        public ConvolutionReverb(int blockSize = 512, double sampleRate = 44100.0)
        {
            _blockSize = blockSize;
            _fftSize = blockSize * 2;
            _sampleRate = sampleRate;
            _irPartitions = new List<double[]>();
            _inputHistory = new Queue<double[]>();
            _overlapBuffer = new double[_blockSize];
            _inputBlock = new double[_fftSize];
            _wet = 0.3;
            _dry = 1.0;

            // 默认IR - 简短的指数衰减脉冲
            float[] defaultIR = new float[blockSize * 4];
            for (int i = 0; i < defaultIR.Length; i++)
                defaultIR[i] = (float)(Math.Exp(-i * 0.005) * (Math.Sin(i * 0.3) * 0.5 + 0.5));
            LoadImpulseResponse(defaultIR);
        }

        /// <summary>
        /// 加载脉冲响应并预计算频域分区
        /// </summary>
        public void LoadImpulseResponse(float[] ir)
        {
            _impulseResponse = ir;
            _irPartitions.Clear();
            _inputHistory.Clear();

            int irLen = ir.Length;
            _partitionCount = (irLen + _blockSize - 1) / _blockSize;
            if (_partitionCount == 0) _partitionCount = 1;

            for (int p = 0; p < _partitionCount; p++)
            {
                double[] irSeg = new double[_fftSize];
                double[] irSegImag = new double[_fftSize];

                for (int i = 0; i < _blockSize; i++)
                {
                    int idx = p * _blockSize + i;
                    if (idx < irLen)
                        irSeg[i] = ir[idx];
                }

                FFT.Forward(irSeg, irSegImag);

                double[] irFFT = new double[_fftSize * 2];
                for (int i = 0; i < _fftSize; i++)
                {
                    irFFT[i * 2] = irSeg[i];
                    irFFT[i * 2 + 1] = irSegImag[i];
                }
                _irPartitions.Add(irFFT);
            }

            // 预填充输入历史为0
            for (int i = 0; i < _partitionCount; i++)
                _inputHistory.Enqueue(new double[_fftSize * 2]);

            Array.Clear(_overlapBuffer, 0, _blockSize);
        }

        /// <summary>
        /// 处理输入块, 输出混响结果
        /// </summary>
        public void Process(float[] input, float[] output)
        {
            int n = Math.Min(input.Length, _blockSize);

            // 构建FFT输入
            for (int i = 0; i < _blockSize; i++)
                _inputBlock[i] = _overlapBuffer[i];
            for (int i = 0; i < n; i++)
                _inputBlock[_blockSize + i] = input[i];

            // 更新overlap
            for (int i = 0; i < _blockSize; i++)
            {
                int srcIdx = input.Length - _blockSize + i;
                _overlapBuffer[i] = srcIdx >= 0 && srcIdx < input.Length ? input[srcIdx] : 0;
            }

            // 计算输入FFT
            double[] inputReal = new double[_fftSize];
            double[] inputImag = new double[_fftSize];
            Array.Copy(_inputBlock, inputReal, _fftSize);
            FFT.Forward(inputReal, inputImag);

            double[] inputFFT = new double[_fftSize * 2];
            for (int i = 0; i < _fftSize; i++)
            {
                inputFFT[i * 2] = inputReal[i];
                inputFFT[i * 2 + 1] = inputImag[i];
            }
            _inputHistory.Enqueue(inputFFT);
            if (_inputHistory.Count > _partitionCount)
                _inputHistory.Dequeue();

            // 频域乘法累加
            double[] accReal = new double[_fftSize];
            double[] accImag = new double[_fftSize];

            var historyArray = _inputHistory.ToArray();
            for (int p = 0; p < _partitionCount && p < historyArray.Length; p++)
            {
                double[] irFFT = _irPartitions[p];
                double[] inFFT = historyArray[historyArray.Length - 1 - p];

                for (int i = 0; i < _fftSize; i++)
                {
                    double inR = inFFT[i * 2];
                    double inI = inFFT[i * 2 + 1];
                    double irR = irFFT[i * 2];
                    double irI = irFFT[i * 2 + 1];
                    accReal[i] += inR * irR - inI * irI;
                    accImag[i] += inR * irI + inI * irR;
                }
            }

            // IFFT
            FFT.Inverse(accReal, accImag);

            // 取后半部分(Overlap-Save)
            for (int i = 0; i < _blockSize && i < output.Length; i++)
            {
                double wet = accReal[_blockSize + i];
                double dry = i < input.Length ? input[i] : 0;
                output[i] = (float)(dry * _dry + wet * _wet);
            }
        }

        public void Reset()
        {
            _inputHistory.Clear();
            for (int i = 0; i < _partitionCount; i++)
                _inputHistory.Enqueue(new double[_fftSize * 2]);
            Array.Clear(_overlapBuffer, 0, _blockSize);
        }
    }

    // ========================================================================
    // 算法混响 (Schroeder-Moorer / Freeverb风格)
    // ========================================================================

    /// <summary>
    /// 算法混响 - Schroeder-Moorer风格。
    /// Comb滤波器并联(延迟+反馈+低通) + Allpass串联(扩散)。
    /// </summary>
    public sealed class AlgorithmicReverb
    {
        // Freeverb风格的Comb延迟时间(采样数@44100Hz)
        private static readonly int[] CombDelayTimes = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        // Allpass延迟时间
        private static readonly int[] AllpassDelayTimes = { 556, 441, 341, 225 };

        private readonly CombFilter[] _combs;
        private readonly AllpassFilter[] _allpasses;
        private readonly int _numCombs;
        private readonly int _numAllpasses;

        private double _roomSize;    // 房间大小 (0~1)
        private double _damping;     // 高频阻尼 (0~1)
        private double _wetLevel;    // 湿信号
        private double _dryLevel;    // 干信号
        private double _width;       // 立体声宽度
        private double _preDelay;    // 预延迟(采样数)
        private readonly double[] _preDelayBuffer;
        private int _preDelayIndex;
        private double _sampleRate;

        public double RoomSize { get => _roomSize; set => _roomSize = Math.Max(0, Math.Min(1, value)); }
        public double Damping { get => _damping; set => _damping = Math.Max(0, Math.Min(1, value)); }
        public double WetLevel { get => _wetLevel; set => _wetLevel = Math.Max(0, Math.Min(1, value)); }
        public double DryLevel { get => _dryLevel; set => _dryLevel = Math.Max(0, Math.Min(1, value)); }
        public double Width { get => _width; set => _width = Math.Max(0, Math.Min(1, value)); }

        public AlgorithmicReverb(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            double srScale = sampleRate / 44100.0;

            _numCombs = CombDelayTimes.Length;
            _combs = new CombFilter[_numCombs];
            for (int i = 0; i < _numCombs; i++)
            {
                int delay = (int)(CombDelayTimes[i] * srScale);
                _combs[i] = new CombFilter(delay, 0.5, 0.0);
            }

            _numAllpasses = AllpassDelayTimes.Length;
            _allpasses = new AllpassFilter[_numAllpasses];
            for (int i = 0; i < _numAllpasses; i++)
            {
                int delay = (int)(AllpassDelayTimes[i] * srScale);
                _allpasses[i] = new AllpassFilter(delay, 0.5);
            }

            _roomSize = 0.5;
            _damping = 0.5;
            _wetLevel = 0.3;
            _dryLevel = 1.0;
            _width = 1.0;
            _preDelay = 0;
            _preDelayBuffer = new double[44100]; // 1秒预延迟最大
            _preDelayIndex = 0;

            UpdateParameters();
        }

        private void UpdateParameters()
        {
            // Freeverb反馈系数: feedback = roomSize * 0.28 + 0.7
            // 限制在稳定范围内
            double feedback = _roomSize * 0.28 + 0.7;
            feedback = Math.Min(0.98, feedback);

            for (int i = 0; i < _numCombs; i++)
            {
                _combs[i].Feedback = feedback;
                _combs[i].Damping = _damping;
            }

            // Allpass反馈固定为0.5
            for (int i = 0; i < _numAllpasses; i++)
                _allpasses[i].Feedback = 0.5;
        }

        public void SetPreDelay(double seconds)
        {
            _preDelay = seconds * _sampleRate;
            if (_preDelay >= _preDelayBuffer.Length) _preDelay = _preDelayBuffer.Length - 1;
        }

        /// <summary>
        /// 处理单声道输入, 输出混响单声道
        /// </summary>
        public double Process(double input)
        {
            UpdateParameters();

            // 预延迟
            double delayed = _preDelayBuffer[_preDelayIndex];
            _preDelayBuffer[_preDelayIndex] = input;
            _preDelayIndex = (int)((_preDelayIndex + 1) % Math.Max(1, (int)_preDelay));

            // Comb滤波器并联
            double combSum = 0;
            for (int i = 0; i < _numCombs; i++)
                combSum += _combs[i].Process(delayed);

            // Allpass串联(扩散)
            double output = combSum;
            for (int i = 0; i < _numAllpasses; i++)
                output = _allpasses[i].Process(output);

            // 湿/干混合
            return input * _dryLevel + output * _wetLevel;
        }

        /// <summary>
        /// 处理立体声输入
        /// </summary>
        public void ProcessStereo(float[] inputLeft, float[] inputRight,
                                   float[] outputLeft, float[] outputRight)
        {
            int n = Math.Min(Math.Min(inputLeft.Length, inputRight.Length),
                             Math.Min(outputLeft.Length, outputRight.Length));
            for (int i = 0; i < n; i++)
            {
                // 左右声道分别处理,宽度控制交叉混合
                double wetL = ProcessChannelSide(inputLeft[i], true);
                double wetR = ProcessChannelSide(inputRight[i], false);

                double dryL = inputLeft[i] * _dryLevel;
                double dryR = inputRight[i] * _dryLevel;

                // 立体声宽度: width=0全单声道混合, width=1完全分离
                double wetMixL = wetL * (1.0 - _width * 0.5) + wetR * (_width * 0.5);
                double wetMixR = wetR * (1.0 - _width * 0.5) + wetL * (_width * 0.5);

                outputLeft[i] = (float)(dryL + wetMixL * _wetLevel);
                outputRight[i] = (float)(dryR + wetMixR * _wetLevel);
            }
        }

        private double ProcessChannelSide(double input, bool isLeft)
        {
            UpdateParameters();
            // 预延迟
            double delayed = _preDelayBuffer[_preDelayIndex];
            _preDelayBuffer[_preDelayIndex] = input;
            _preDelayIndex = (_preDelayIndex + 1) % Math.Max(1, (int)_preDelay);

            double combSum = 0;
            for (int i = 0; i < _numCombs; i++)
                combSum += _combs[i].Process(delayed);

            double output = combSum;
            for (int i = 0; i < _numAllpasses; i++)
                output = _allpasses[i].Process(output);

            return output;
        }

        public void ProcessBuffer(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)Process(buffer[i]);
        }

        public void Reset()
        {
            for (int i = 0; i < _numCombs; i++) _combs[i].Reset();
            for (int i = 0; i < _numAllpasses; i++) _allpasses[i].Reset();
            Array.Clear(_preDelayBuffer, 0, _preDelayBuffer.Length);
            _preDelayIndex = 0;
        }
    }

    // ========================================================================
    // 声道映射
    // ========================================================================

    /// <summary>
    /// 声道配置
    /// </summary>
    public enum ChannelConfiguration
    {
        Mono,
        Stereo,
        Quad,
        Surround51,
        Surround71,
        Ambisonic
    }

    /// <summary>
    /// 声道映射器 - 单声道→立体声, 立体声→5.1, 5.1→立体声下混, 7.1, Ambisonics。
    /// </summary>
    public static class ChannelMapper
    {
        // 声道索引常量
        public const int CH_FRONT_LEFT = 0;
        public const int CH_FRONT_RIGHT = 1;
        public const int CH_FRONT_CENTER = 2;
        public const int CH_LOW_FREQ = 3;   // LFE
        public const int CH_BACK_LEFT = 4;
        public const int CH_BACK_RIGHT = 5;
        public const int CH_SIDE_LEFT = 6;
        public const int CH_SIDE_RIGHT = 7;

        // ITU标准下混系数
        private const double SQRT_HALF = 0.7071067811865476;

        /// <summary>
        /// 单声道→立体声
        /// </summary>
        public static void MonoToStereo(float[] mono, float[] stereoLeft, float[] stereoRight)
        {
            int n = Math.Min(mono.Length, Math.Min(stereoLeft.Length, stereoRight.Length));
            for (int i = 0; i < n; i++)
            {
                stereoLeft[i] = mono[i];
                stereoRight[i] = mono[i];
            }
        }

        /// <summary>
        /// 立体声→单声道
        /// </summary>
        public static void StereoToMono(float[] left, float[] right, float[] mono)
        {
            int n = Math.Min(Math.Min(left.Length, right.Length), mono.Length);
            for (int i = 0; i < n; i++)
                mono[i] = (left[i] + right[i]) * 0.5f;
        }

        /// <summary>
        /// 立体声→5.1环绕
        /// L/R直通, C=(L+R)/2, LFE=0(或低通提取), Ls/Rs=0
        /// </summary>
        public static void StereoToSurround51(float[] left, float[] right,
            float[] fl, float[] fr, float[] fc, float[] lfe, float[] bl, float[] br)
        {
            int n = Math.Min(Math.Min(left.Length, right.Length), fl.Length);
            for (int i = 0; i < n; i++)
            {
                fl[i] = left[i];
                fr[i] = right[i];
                fc[i] = (left[i] + right[i]) * 0.5f;
                lfe[i] = 0f;
                bl[i] = 0f;
                br[i] = 0f;
            }
        }

        /// <summary>
        /// 5.1→立体声下混 (ITU-R BS.775标准)
        /// stereoL = FL + 0.707*FC + 0.707*BL
        /// stereoR = FR + 0.707*FC + 0.707*BR
        /// </summary>
        public static void Surround51ToStereo(float[] fl, float[] fr, float[] fc, float[] lfe,
            float[] bl, float[] br, float[] stereoLeft, float[] stereoRight)
        {
            int n = Math.Min(fl.Length, stereoLeft.Length);
            for (int i = 0; i < n; i++)
            {
                double center = fc != null && i < fc.Length ? fc[i] * SQRT_HALF : 0;
                double lfeVal = lfe != null && i < lfe.Length ? lfe[i] * 0.0 : 0; // LFE通常不下混
                double blVal = bl != null && i < bl.Length ? bl[i] * SQRT_HALF : 0;
                double brVal = br != null && i < br.Length ? br[i] * SQRT_HALF : 0;

                stereoLeft[i] = (float)(fl[i] + center + blVal + lfeVal);
                stereoRight[i] = (float)(fr[i] + center + brVal + lfeVal);
            }
        }

        /// <summary>
        /// 7.1→立体声下混
        /// stereoL = FL + 0.707*FC + 0.5*BL + 0.5*SL
        /// stereoR = FR + 0.707*FC + 0.5*BR + 0.5*SR
        /// </summary>
        public static void Surround71ToStereo(float[] fl, float[] fr, float[] fc, float[] lfe,
            float[] bl, float[] br, float[] sl, float[] sr, float[] stereoLeft, float[] stereoRight)
        {
            int n = Math.Min(fl.Length, stereoLeft.Length);
            for (int i = 0; i < n; i++)
            {
                double center = fc != null && i < fc.Length ? fc[i] * SQRT_HALF : 0;
                double blVal = bl != null && i < bl.Length ? bl[i] * 0.5 : 0;
                double brVal = br != null && i < br.Length ? br[i] * 0.5 : 0;
                double slVal = sl != null && i < sl.Length ? sl[i] * 0.5 : 0;
                double srVal = sr != null && i < sr.Length ? sr[i] * 0.5 : 0;

                stereoLeft[i] = (float)(fl[i] + center + blVal + slVal);
                stereoRight[i] = (float)(fr[i] + center + brVal + srVal);
            }
        }

        /// <summary>
        /// 通用声道重映射 - 将输入多声道映射到输出多声道
        /// </summary>
        public static void Remap(float[][] input, int inputChannels,
                                  float[][] output, int outputChannels)
        {
            int frames = input.Length > 0 ? input[0].Length : 0;

            for (int ch = 0; ch < outputChannels; ch++)
            {
                if (output[ch] == null) continue;
                for (int f = 0; f < frames; f++)
                {
                    if (ch < inputChannels && input[ch] != null)
                        output[ch][f] = input[ch][f];
                    else
                        output[ch][f] = 0f;
                }
            }
        }
    }

    // ========================================================================
    // 空间音频处理器 - 整合所有空间效果
    // ========================================================================

    /// <summary>
    /// 空间音频处理器 - 整合HRTF、距离衰减、多普勒、空气吸收、遮挡和混响。
    /// </summary>
    public sealed class SpatialAudioProcessor
    {
        public HRTFConvolver HRTF { get; set; }
        public DistanceAttenuation DistanceAtten { get; set; }
        public DopplerEffect Doppler { get; set; }
        public AirAbsorption AirAbsorb { get; set; }
        public AudioOcclusion Occlusion { get; set; }
        public AlgorithmicReverb Reverb { get; set; }

        // 声源状态
        private double _srcX, _srcY, _srcZ;
        private double _srcVelX, _srcVelY, _srcVelZ;
        private double _listenerX, _listenerY, _listenerZ;
        private double _listenerVelX, _listenerVelY, _listenerVelZ;
        private double _lastTime;
        private double _sampleRate;

        // 多普勒变调延迟线
        private readonly double[] _pitchBuffer;
        private double _pitchReadPos;
        private int _pitchWritePos;
        private const int PITCH_BUFFER_SIZE = 16384;

        public SpatialAudioProcessor(double sampleRate = 44100.0)
        {
            _sampleRate = sampleRate;
            HRTF = new HRTFConvolver(256, sampleRate);
            DistanceAtten = new DistanceAttenuation();
            Doppler = new DopplerEffect();
            AirAbsorb = new AirAbsorption(sampleRate);
            Occlusion = new AudioOcclusion(sampleRate);
            Reverb = new AlgorithmicReverb(sampleRate);
            _pitchBuffer = new double[PITCH_BUFFER_SIZE];
            _pitchReadPos = 0;
            _pitchWritePos = 0;
            _lastTime = 0;
        }

        public void SetSourcePosition(double x, double y, double z)
        {
            _srcX = x; _srcY = y; _srcZ = z;
        }

        public void SetSourceVelocity(double vx, double vy, double vz)
        {
            _srcVelX = vx; _srcVelY = vy; _srcVelZ = vz;
        }

        public void SetListenerPosition(double x, double y, double z)
        {
            _listenerX = x; _listenerY = y; _listenerZ = z;
        }

        public void SetListenerVelocity(double vx, double vy, double vz)
        {
            _listenerVelX = vx; _listenerVelY = vy; _listenerVelZ = vz;
        }

        /// <summary>
        /// 处理一段输入音频, 输出双耳立体声
        /// </summary>
        public void Process(float[] input, float[] outputLeft, float[] outputRight, double dt)
        {
            int n = Math.Min(input.Length, Math.Min(outputLeft.Length, outputRight.Length));

            // 计算距离
            double dx = _listenerX - _srcX;
            double dy = _listenerY - _srcY;
            double dz = _listenerZ - _srcZ;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance < 0.01) distance = 0.01;

            // 距离衰减
            double distGain = DistanceAtten.ComputeGain(distance);

            // 空气吸收
            AirAbsorb.SetDistance(distance);

            // 遮挡(外部设置)
            // Occlusion.SetOcclusion(...);

            // 多普勒
            double pitch = Doppler.ComputePitchRatio(
                _srcX, _srcY, _srcZ, _listenerX, _listenerY, _listenerZ,
                _srcVelX, _srcVelY, _srcVelZ, _listenerVelX, _listenerVelY, _listenerVelZ, dt);

            // 处理每个样本
            for (int i = 0; i < n; i++)
            {
                // 写入变调缓冲区
                _pitchBuffer[_pitchWritePos] = input[i] * distGain;
                _pitchWritePos = (_pitchWritePos + 1) % PITCH_BUFFER_SIZE;

                // 变调读取(变速不变调→线性插值变调)
                int readIdx = (int)_pitchReadPos;
                double frac = _pitchReadPos - readIdx;
                int nextIdx = (readIdx + 1) % PITCH_BUFFER_SIZE;
                double pitched = _pitchBuffer[readIdx] * (1.0 - frac) + _pitchBuffer[nextIdx] * frac;
                _pitchReadPos += pitch;
                if (_pitchReadPos >= _pitchWritePos)
                    _pitchReadPos = _pitchWritePos - 1;
                if (_pitchReadPos < 0) _pitchReadPos = 0;

                // 空气吸收
                double absorbed = AirAbsorb.Process(pitched);

                // 遮挡
                double occluded = Occlusion.Process(absorbed);

                // 混响(湿信号)
                double reverbed = Reverb.Process(occluded * 0.3);

                // 总信号
                double final = occluded + reverbed;

                // 写入临时缓冲供HRTF处理
                input[i] = (float)final;
            }

            // HRTF双耳处理
            int blockSize = 256;
            float[] tempInput = new float[blockSize];
            float[] tempLeft = new float[blockSize];
            float[] tempRight = new float[blockSize];

            for (int offset = 0; offset < n; offset += blockSize)
            {
                int toProcess = Math.Min(blockSize, n - offset);
                Array.Copy(input, offset, tempInput, 0, toProcess);
                for (int i = toProcess; i < blockSize; i++) tempInput[i] = 0;

                HRTF.Process(tempInput, tempLeft, tempRight);

                int copyLen = Math.Min(blockSize, n - offset);
                Array.Copy(tempLeft, 0, outputLeft, offset, copyLen);
                Array.Copy(tempRight, 0, outputRight, offset, copyLen);
            }

            _lastTime += dt;
        }

        public void Reset()
        {
            HRTF.Reset();
            Doppler.Reset();
            AirAbsorb.Reset();
            Occlusion.Reset();
            Reverb.Reset();
            Array.Clear(_pitchBuffer, 0, PITCH_BUFFER_SIZE);
            _pitchReadPos = 0;
            _pitchWritePos = 0;
        }
    }
}
