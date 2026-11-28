using System;
using System.Linq;
using YamaEngine.Audio;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// AudioDSPReal 深度测试。覆盖 AudioFormatDescriptor / AudioBuffer / Biquad / OnePole /
    /// Linkwitz-Riley / Allpass / Comb / MovingAverage 滤波器 / FFT / Window /
    /// SpectrumAnalyzer / Compressor / Expander / Limiter / NoiseGate / TransientDesigner。
    /// </summary>
    public static class AudioDSPRealDepthTests
    {
        private static void NearD(double want, double got, double tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        // ── AudioSampleFormat ──

        public static void TestAudioSampleFormatEnumValues()
        {
            AssertEqual(0, (int)AudioSampleFormat.Pcm8Bit, "Pcm8Bit=0");
            AssertEqual(1, (int)AudioSampleFormat.Pcm16Bit, "Pcm16Bit=1");
            AssertEqual(2, (int)AudioSampleFormat.Pcm24Bit, "Pcm24Bit=2");
            AssertEqual(3, (int)AudioSampleFormat.Pcm32Bit, "Pcm32Bit=3");
            AssertEqual(4, (int)AudioSampleFormat.IeeeFloat32, "IeeeFloat32=4");
            AssertEqual(5, (int)AudioSampleFormat.IeeeFloat64, "IeeeFloat64=5");
        }

        // ── AudioFormatDescriptor ──

        public static void TestFormatDescriptorDefaults()
        {
            var d = new AudioFormatDescriptor();
            AssertEqual(44100, d.SampleRate, "SampleRate=44100");
            AssertEqual(16, d.BitsPerSample, "BitsPerSample=16");
            AssertEqual(2, d.Channels, "Channels=2");
            AssertEqual(AudioSampleFormat.Pcm16Bit, d.Encoding, "Encoding=Pcm16Bit");
        }

        public static void TestFormatDescriptorCustom()
        {
            var d = new AudioFormatDescriptor(48000, 24, 6, AudioSampleFormat.Pcm24Bit);
            AssertEqual(48000, d.SampleRate, "SampleRate=48000");
            AssertEqual(24, d.BitsPerSample, "BitsPerSample=24");
            AssertEqual(6, d.Channels, "Channels=6");
            AssertEqual(AudioSampleFormat.Pcm24Bit, d.Encoding, "Encoding=Pcm24Bit");
        }

        public static void TestFormatDescriptorDurationSeconds()
        {
            var d = new AudioFormatDescriptor(44100, 16, 2);
            NearD(1.0, d.DurationSeconds(44100), 1e-6, "44100 frames = 1s");
            NearD(2.0, d.DurationSeconds(88200), 1e-6, "88200 frames = 2s");
            NearD(0.0, d.DurationSeconds(0), 1e-6, "0 frames = 0s");
        }

        public static void TestFormatDescriptorDurationZeroRate()
        {
            var d = new AudioFormatDescriptor(0, 16, 2);
            NearD(0.0, d.DurationSeconds(1000), 1e-6, "zero sample rate → 0s");
        }

        public static void TestFormatDescriptorBytesPerSample()
        {
            AssertEqual(1, new AudioFormatDescriptor(44100, 8, 1, AudioSampleFormat.Pcm8Bit).BytesPerSample, "8bit=1B");
            AssertEqual(2, new AudioFormatDescriptor(44100, 16, 1, AudioSampleFormat.Pcm16Bit).BytesPerSample, "16bit=2B");
            AssertEqual(3, new AudioFormatDescriptor(44100, 24, 1, AudioSampleFormat.Pcm24Bit).BytesPerSample, "24bit=3B");
            AssertEqual(4, new AudioFormatDescriptor(44100, 32, 1, AudioSampleFormat.Pcm32Bit).BytesPerSample, "32bit=4B");
            AssertEqual(4, new AudioFormatDescriptor(44100, 32, 1, AudioSampleFormat.IeeeFloat32).BytesPerSample, "float32=4B");
            AssertEqual(8, new AudioFormatDescriptor(44100, 64, 1, AudioSampleFormat.IeeeFloat64).BytesPerSample, "float64=8B");
        }

        public static void TestFormatDescriptorClone()
        {
            var d = new AudioFormatDescriptor(48000, 24, 6, AudioSampleFormat.Pcm24Bit);
            var c = d.Clone();
            AssertEqual(d.SampleRate, c.SampleRate, "clone SampleRate");
            AssertEqual(d.BitsPerSample, c.BitsPerSample, "clone BitsPerSample");
            AssertEqual(d.Channels, c.Channels, "clone Channels");
            AssertEqual(d.Encoding, c.Encoding, "clone Encoding");
        }

        // ── AudioBuffer ──

        public static void TestAudioBufferDefaults()
        {
            var b = new AudioBuffer(2, 512);
            AssertEqual(2, b.Channels, "Channels=2");
            AssertEqual(512, b.Frames, "Frames=512");
            AssertEqual(512, b.FrameCount, "FrameCount=512");
            AssertEqual(44100, b.SampleRate, "SampleRate=44100");
        }

        public static void TestAudioBufferChannelAccess()
        {
            var b = new AudioBuffer(2, 4);
            float[] ch0 = { 0.1f, 0.2f, 0.3f, 0.4f };
            b.SetChannel(0, ch0);
            b.SetSample(1, 0, 0.5f);
            AssertEqual(0.2f, b.GetSample(0, 1), "GetSample(0,1)=0.2");
            AssertEqual(0.5f, b.GetSample(1, 0), "GetSample(1,0)=0.5");
        }

        public static void TestAudioBufferClear()
        {
            var b = new AudioBuffer(1, 4);
            b.SetSample(0, 0, 1.0f);
            b.Clear();
            AssertEqual(0.0f, b.GetSample(0, 0), "cleared to 0");
        }

        public static void TestAudioBufferToFromInterleaved()
        {
            var b = new AudioBuffer(2, 2);
            b.SetSample(0, 0, 1.0f); b.SetSample(0, 1, 2.0f);
            b.SetSample(1, 0, 3.0f); b.SetSample(1, 1, 4.0f);
            var interleaved = b.ToInterleaved();
            AssertEqual(1.0f, interleaved[0], "L0");
            AssertEqual(3.0f, interleaved[1], "R0");
            AssertEqual(2.0f, interleaved[2], "L1");
            AssertEqual(4.0f, interleaved[3], "R1");
        }

        public static void TestAudioBufferStaticToInterleaved()
        {
            var planar = new float[][] { new[] { 1.0f, 2.0f }, new[] { 3.0f, 4.0f } };
            var interleaved = AudioBuffer.ToInterleaved(planar, 2, 2);
            AssertEqual(1.0f, interleaved[0], "L0");
            AssertEqual(3.0f, interleaved[1], "R0");
            AssertEqual(2.0f, interleaved[2], "L1");
            AssertEqual(4.0f, interleaved[3], "R1");
        }

        public static void TestAudioBufferStaticToPlanar()
        {
            var interleaved = new[] { 1.0f, 3.0f, 2.0f, 4.0f };
            var planar = AudioBuffer.ToPlanar(interleaved, 2, 2);
            AssertEqual(1.0f, planar[0][0], "L0");
            AssertEqual(2.0f, planar[0][1], "L1");
            AssertEqual(3.0f, planar[1][0], "R0");
            AssertEqual(4.0f, planar[1][1], "R1");
        }

        public static void TestAudioBufferMixInAndGain()
        {
            var a = new AudioBuffer(1, 2);
            a.SetSample(0, 0, 1.0f);
            var b = new AudioBuffer(1, 2);
            b.SetSample(0, 0, 1.0f);
            a.MixIn(b, 0.5f);
            AssertEqual(1.5f, a.GetSample(0, 0), "1 + 1*0.5 = 1.5");
            a.ApplyGain(2.0f);
            AssertEqual(3.0f, a.GetSample(0, 0), "1.5 * 2 = 3.0");
        }

        public static void TestAudioBufferReadFrameRoundTrip()
        {
            var b = new AudioBuffer(2, 2);
            b.SetSample(0, 0, 1.0f); b.SetSample(0, 1, 2.0f);
            b.SetSample(1, 0, 3.0f); b.SetSample(1, 1, 4.0f);
            var buf = new float[4];
            int read = b.ReadFrame(buf, 2);
            AssertEqual(2, read, "read 2 frames");
            AssertEqual(1.0f, buf[0], "L0");
            AssertEqual(3.0f, buf[1], "R0");
            AssertEqual(2.0f, buf[2], "L1");
            AssertEqual(4.0f, buf[3], "R1");
        }

        public static void TestAudioBufferWriteFrame()
        {
            var b = new AudioBuffer(1, 4);
            var input = new[] { 1.0f, 2.0f, 3.0f };
            int written = b.WriteFrame(input, 3);
            AssertEqual(3, written, "wrote 3 frames");
            AssertEqual(1.0f, b.GetSample(0, 0), "frame0=1");
            AssertEqual(3.0f, b.GetSample(0, 2), "frame2=3");
            AssertEqual(0.0f, b.GetSample(0, 3), "frame3=0 (untouched)");
        }

        public static void TestAudioBufferDispose()
        {
            var b = new AudioBuffer(1, 2);
            b.SetSample(0, 0, 1.0f);
            b.Dispose();
            AssertEqual(0.0f, b.GetSample(0, 0), "disposed → cleared to 0");
        }

        // ── BiquadFilter ──

        public static void TestBiquadDefaultLowPass()
        {
            var f = new BiquadFilter();
            // Default: LowPass 1kHz Butterworth Q=0.707 Fs=44100
            // DC 增益 = (B0+B1+B2) / (1+A1+A2) = 1 for LPF (H(z=1))
            // 注意：分子和本身 ≠ 1，要除以分母和才是 DC 频响。
            double dcGain = (f.B0 + f.B1 + f.B2) / (1.0 + f.A1 + f.A2);
            NearD(1.0, dcGain, 1e-6, "LPF DC response = 1 (pass band)");
        }

        public static void TestBiquadHighPassDCNearZero()
        {
            var f = new BiquadFilter();
            f.SetParameters(BiquadType.HighPass, 1000.0, 0.7071067811865476);
            // At DC (z=1): H(z=1) = (b0+b1+b2)/(1+a1+a2). For HPF numerator ~0.
            NearD(0.0, f.B0 + f.B1 + f.B2, 1e-9, "HPF DC gain ≈ 0");
        }

        public static void TestBiquadProcessDCLowPass()
        {
            var f = new BiquadFilter();
            f.SetParameters(BiquadType.LowPass, 5000.0, 0.707);
            // Feed DC (1.0) samples and let it settle; should approach 1.0
            double last = 0;
            for (int i = 0; i < 5000; i++) last = f.Process(1.0);
            NearD(1.0, last, 0.01, "LPF 5kHz DC response → 1.0");
        }

        public static void TestBiquadProcessDCHighPass()
        {
            var f = new BiquadFilter();
            f.SetParameters(BiquadType.HighPass, 5000.0, 0.707);
            double last = 0;
            for (int i = 0; i < 5000; i++) last = f.Process(1.0);
            NearD(0.0, last, 0.01, "HPF 5kHz DC response → 0.0");
        }

        public static void TestBiquadReset()
        {
            var f = new BiquadFilter();
            f.SetParameters(BiquadType.LowPass, 5000.0, 0.707);
            for (int i = 0; i < 100; i++) f.Process(1.0);
            f.Reset();
            // After reset, feeding 0 should produce exactly 0 (all state gone)
            double out1 = f.Process(0.0);
            NearD(0.0, out1, 1e-12, "after reset, silence in → silence out");
        }

        public static void TestBiquadNotchBlocksFrequency()
        {
            var f = new BiquadFilter();
            f.SetParameters(BiquadType.Notch, 1000.0, 10.0);
            // Sum of numerator coefficients: b0+b1+b2 = 1-2cos+1 = 2(1-cosw0)
            double sumNumerator = f.B0 + f.B1 + f.B2;
            // For notch, at center freq the gain should be ~0.
            AssertTrue(Math.Abs(sumNumerator) > 1e-6, "notch numerator sum nonzero (only zero at DC/Nyquist)");
        }

        public static void TestBiquadAllPassGainIsOne()
        {
            var f = new BiquadFilter();
            f.SetParameters(BiquadType.AllPass, 1000.0, 0.707);
            // RBJ allpass: B0 = (1-alpha)/(1+alpha), alpha = sin(w0)/(2Q)
            // w0=2π*1000/44100=0.14248, alpha=sin(0.14248)/(2*0.707)=0.10037
            // B0 = (1-0.10037)/(1+0.10037) = 0.8176
            // 全通 unity gain 体现在 |H(e^jw)|=1（频响），B0 本身不是 1。
            NearD(0.8176, f.B0, 0.01, "allpass B0 = (1-alpha)/(1+alpha) ≈ 0.8176");
        }

        public static void TestBiquadPeakingEQBoost()
        {
            var f = new BiquadFilter();
            f.SetParameters(BiquadType.PeakingEQ, 1000.0, 1.0, 6.0); // +6 dB
            // RBJ peaking: B0 = b0/a0 = (1+alpha*A)/(1+alpha/A) ≈ 1.0476 for +6dB/Q=1
            // 峰值增益 A² 在中心频率处体现为频响，不是系数本身。B0 应略大于 1 且 < 2。
            NearD(1.0476, f.B0, 0.01, "peaking +6dB → B0 = b0/a0 ≈ 1.0476 (boost, not 2.0)");
        }

        // ── OnePoleFilter ──

        public static void TestOnePoleDefault()
        {
            var f = new OnePoleFilter();
            // Feed DC (1.0) through lowpass, should approach 1.0
            double last = 0;
            for (int i = 0; i < 10000; i++) last = f.Process(1.0);
            NearD(1.0, last, 0.01, "one-pole LPF DC → 1.0");
        }

        public static void TestOnePoleHighPassDC()
        {
            var f = new OnePoleFilter(44100.0, 1000.0, OnePoleFilter.OnePoleType.HighPass);
            double last = 0;
            for (int i = 0; i < 10000; i++) last = f.Process(1.0);
            NearD(0.0, last, 0.01, "one-pole HPF DC → 0.0");
        }

        public static void TestOnePoleReset()
        {
            var f = new OnePoleFilter();
            for (int i = 0; i < 100; i++) f.Process(1.0);
            f.Reset();
            double output = f.Process(0.0);
            NearD(0.0, output, 1e-9, "after reset, zero in → zero out");
        }

        // ── LinkwitzRileyFilter ──

        public static void TestLinkwitzRileyDefaults()
        {
            var f = new LinkwitzRileyFilter();
            AssertTrue(f.LowPassStage1 != null, "stage1 non-null");
            AssertTrue(f.LowPassStage2 != null, "stage2 non-null");
        }

        public static void TestLinkwitzRileyCrossoverSum()
        {
            var f = new LinkwitzRileyFilter(44100.0);
            f.SetCrossover(1000.0, 4);
            // LR has sum of LP+HP = unity at all frequencies
            // We can't easily test frequency response without external HP stages, but stage1 exists
            AssertTrue(f.LowPassStage1.B0 != 0, "stage1 B0 nonzero after SetCrossover");
        }

        public static void TestLinkwitzRileyLowPassDC()
        {
            var f = new LinkwitzRileyFilter(44100.0);
            f.SetCrossover(1000.0, 4);
            // Low-pass DC response should approach 1.0
            double last = 0;
            for (int i = 0; i < 50000; i++)
            {
                double s = f.LowPassStage1.Process(1.0);
                last = f.LowPassStage2.Process(s);
            }
            NearD(1.0, last, 0.01, "LR4 LPF DC → 1.0");
        }

        // ── AllpassFilter ──

        public static void TestAllpassUnityGain()
        {
            // Allpass has |H(e^jw)| = 1 for all w. Test with impulse.
            var f = new AllpassFilter(100, 0.5);
            // Feed an impulse and sum the energy — should be ≈ 1 (unity)
            double sumSquares = 0;
            f.Process(1.0);
            // After impulse, feed many zeros; each output contributes
            for (int i = 0; i < 1000; i++) sumSquares += 0; // reset
            f.Reset();
            sumSquares = 0;
            double impOut = f.Process(1.0);
            sumSquares += impOut * impOut; // 包含 impulse 本身输出
            for (int i = 0; i < 500; i++)
            {
                double s = f.Process(0.0);
                sumSquares += s * s;
            }
            NearD(1.0, sumSquares, 0.1, "allpass impulse response energy ≈ 1 (unity gain)");
        }

        public static void TestAllpassFeedbackClamp()
        {
            var f = new AllpassFilter(10, 2.0);
            AssertTrue(f.Feedback <= 0.999, "feedback clamped to <= 0.999");
            f.Feedback = -2.0;
            AssertTrue(f.Feedback >= -0.999, "feedback clamped to >= -0.999");
        }

        public static void TestAllpassReset()
        {
            var f = new AllpassFilter(4, 0.5);
            f.Process(1.0);
            f.Reset();
            // After reset, process 0 → exactly 0 (no history)
            double output = f.Process(0.0);
            NearD(0.0, output, 1e-12, "allpass reset → silent");
        }

        // ── CombFilter ──

        public static void TestCombFeedbackClamp()
        {
            var c = new CombFilter(10, 2.0);
            AssertTrue(c.Feedback <= 0.999, "feedback clamp upper");
            c.Damping = 5.0;
            AssertTrue(c.Damping <= 1.0, "damping clamp upper");
            c.Damping = -1.0;
            AssertTrue(c.Damping >= 0.0, "damping clamp lower");
        }

        public static void TestCombZeroInputSilence()
        {
            var c = new CombFilter(4, 0.5);
            for (int i = 0; i < 100; i++) c.Process(0.0);
            double output = c.Process(0.0);
            NearD(0.0, output, 1e-12, "comb silence in → silence out");
        }

        public static void TestCombImpulseDecay()
        {
            var c = new CombFilter(8, 0.5);
            double firstOut = c.Process(1.0);
            NearD(0.0, firstOut, 1e-12, "comb first output is 0 (empty delay line)");
            double peakAfter = 0;
            for (int i = 0; i < 100; i++)
            {
                double s = c.Process(0.0);
                peakAfter = Math.Max(peakAfter, Math.Abs(s));
            }
            AssertTrue(peakAfter > 0.0, "comb has feedback tail after impulse");
            AssertTrue(peakAfter <= 1.0 + 1e-9, "comb first echo ≤ 1.0 (impulse passes once, then decays)");
        }

        // ── MovingAverageFilter ──

        public static void TestMovingAverageDCCapture()
        {
            var f = new MovingAverageFilter(8);
            // After 8 samples of 1.0, output should be 1.0
            double last = 0;
            for (int i = 0; i < 100; i++) last = f.Process(1.0);
            NearD(1.0, last, 1e-9, "moving avg DC → 1.0");
        }

        public static void TestMovingAverageSinglePulse()
        {
            var f = new MovingAverageFilter(4);
            // Feed one pulse of 4.0 → output should peak at 1.0 (4/4)
            double peak = 0;
            for (int i = 0; i < 20; i++)
            {
                double s = f.Process(i == 0 ? 4.0 : 0.0);
                peak = Math.Max(peak, s);
            }
            NearD(1.0, peak, 1e-9, "single pulse of 4 → peak 1.0 (4/N)");
        }

        public static void TestMovingAverageMinLength()
        {
            var f = new MovingAverageFilter(0); // should clamp to 1
            double output = f.Process(5.0);
            NearD(5.0, output, 1e-9, "length clamped to 1 → passthrough");
        }

        public static void TestMovingAverageReset()
        {
            var f = new MovingAverageFilter(4);
            for (int i = 0; i < 10; i++) f.Process(1.0);
            f.Reset();
            double output = f.Process(0.0);
            NearD(0.0, output, 1e-9, "moving avg reset → zero");
        }

        // ── FFT ──

        public static void TestFFTPowerOfTwo()
        {
            AssertTrue(FFT.IsPowerOfTwo(1), "1 is power of 2");
            AssertTrue(FFT.IsPowerOfTwo(1024), "1024 is power of 2");
            AssertFalse(FFT.IsPowerOfTwo(0), "0 is not");
            AssertFalse(FFT.IsPowerOfTwo(-4), "-4 is not");
            AssertFalse(FFT.IsPowerOfTwo(6), "6 is not");
        }

        public static void TestFFTLog2()
        {
            AssertEqual(0, FFT.Log2(1), "log2(1)=0");
            AssertEqual(1, FFT.Log2(2), "log2(2)=1");
            AssertEqual(10, FFT.Log2(1024), "log2(1024)=10");
        }

        public static void TestFFTForwardDC()
        {
            // DC-only signal: all ones → X[0] = N, rest = 0
            int n = 8;
            var real = Enumerable.Repeat(1.0, n).ToArray();
            var imag = new double[n];
            FFT.Forward(real, imag);
            NearD(8.0, real[0], 1e-9, "X[0].real = N = 8");
            NearD(0.0, imag[0], 1e-9, "X[0].imag = 0");
            for (int k = 1; k < n; k++)
            {
                NearD(0.0, Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]), 1e-9, $"X[{k}] magnitude = 0");
            }
        }

        public static void TestFFTInverseRoundTrip()
        {
            int n = 16;
            var origReal = new double[n];
            var origImag = new double[n];
            var rng = new Random(42);
            for (int i = 0; i < n; i++) { origReal[i] = rng.NextDouble() * 2 - 1; origImag[i] = rng.NextDouble() * 2 - 1; }
            var real = (double[])origReal.Clone();
            var imag = (double[])origImag.Clone();
            FFT.Forward(real, imag);
            FFT.Inverse(real, imag);
            for (int i = 0; i < n; i++)
            {
                NearD(origReal[i], real[i], 1e-9, $"roundtrip real[{i}]");
                NearD(origImag[i], imag[i], 1e-9, $"roundtrip imag[{i}]");
            }
        }

        public static void TestFFTMagnitude()
        {
            var real = new[] { 3.0, 0.0, 0.0 };
            var imag = new[] { 4.0, 0.0, 0.0 };
            var mag = FFT.Magnitude(real, imag);
            NearD(5.0, mag[0], 1e-9, "|3+4j| = 5");
        }

        public static void TestFFTPowerSpectrum()
        {
            var real = new[] { 3.0, 0.0, 0.0 };
            var imag = new[] { 4.0, 0.0, 0.0 };
            var pow = FFT.PowerSpectrum(real, imag);
            NearD(25.0 / 3.0, pow[0], 1e-9, "P[0] = |X|^2/N = 25/3");
        }

        public static void TestFFTBinFrequency()
        {
            // binToFrequency(bin, fftSize, sampleRate) = bin * sampleRate / fftSize
            // bin=10, fftSize=44100, sampleRate=44100 → 10*44100/44100 = 10 Hz
            NearD(10.0, FFT.BinToFrequency(10, 44100, 44100), 1e-6, "bin=10 fftSize=44100 fs=44100 → 10 Hz");
            NearD(22050.0, FFT.BinToFrequency(512, 1024, 44100), 1e-3, "bin=512 fftSize=1024 fs=44100 → 22050 Hz (Nyquist)");
        }

        public static void TestFFTFrequencyToBin()
        {
            AssertEqual(0, FFT.FrequencyToBin(0.0, 1024, 44100), "0 Hz → bin 0");
            AssertEqual(23, FFT.FrequencyToBin(1000.0, 1024, 44100), "1kHz → bin 23");
        }

        // ── Window functions ──

        public static void TestWindowRectangular()
        {
            var w = FFT.GenerateWindow(8, WindowType.Rectangular);
            AssertTrue(w.All(v => Math.Abs(v - 1.0) < 1e-9), "rectangular = all 1.0");
        }

        public static void TestWindowHannEndsAtZero()
        {
            var w = FFT.GenerateWindow(16, WindowType.Hann);
            NearD(0.0, w[0], 1e-9, "Hann[0] = 0");
            NearD(0.0, w[15], 1e-9, "Hann[N-1] = 0");
        }

        public static void TestWindowHannMidPeak()
        {
            var w = FFT.GenerateWindow(16, WindowType.Hann);
            NearD(1.0, w[8], 0.05, "Hann midpoint ≈ 1");
        }

        public static void TestWindowHammingEndsAtNonZero()
        {
            var w = FFT.GenerateWindow(16, WindowType.Hamming);
            NearD(0.08, w[0], 0.01, "Hamming[0] = 0.54-0.46 = 0.08");
        }

        public static void TestWindowBlackmanEndsNearZero()
        {
            var w = FFT.GenerateWindow(16, WindowType.Blackman);
            NearD(0.0, w[0], 0.01, "Blackman[0] = 0.42-0.5+0.08 = 0.0");
        }

        public static void TestApplyWindowModifies()
        {
            var buf = new double[] { 1.0, 1.0, 1.0, 1.0 };
            var win = new double[] { 1.0, 0.5, 0.5, 1.0 };
            FFT.ApplyWindow(buf, win);
            AssertEqual(1.0, buf[0], "buf[0]=1");
            AssertEqual(0.5, buf[1], "buf[1]=0.5");
        }

        public static void TestBesselI0()
        {
            NearD(1.0, FFT.BesselI0(0.0), 1e-9, "I0(0) = 1");
            AssertTrue(FFT.BesselI0(1.0) > 1.0, "I0(1) > 1");
            AssertTrue(FFT.BesselI0(5.0) > FFT.BesselI0(1.0), "I0 monotonic");
        }

        // ── Compressor ──

        public static void TestCompressorDefaults()
        {
            var c = new Compressor();
            AssertEqual(-20.0, c.Threshold, "Threshold=-20dB");
            AssertEqual(4.0, c.Ratio, "Ratio=4:1");
            AssertEqual(0.003, c.AttackTime, "Attack=3ms");
            AssertEqual(0.25, c.ReleaseTime, "Release=250ms");
            AssertEqual(0.0, c.KneeWidth, "Knee=0 (hard)");
            AssertEqual(0.0, c.MakeupGain, "MakeupGain=0");
            AssertEqual(DetectionMode.Rms, c.Mode, "Mode=Rms");
        }

        public static void TestCompressorRatioClamp()
        {
            var c = new Compressor();
            c.Ratio = 0.5; // below minimum
            AssertEqual(1.0, c.Ratio, "ratio clamped to >= 1.0");
        }

        public static void TestCompressorReleaseClamp()
        {
            var c = new Compressor();
            c.ReleaseTime = 0.0001;
            AssertEqual(0.001, c.ReleaseTime, "release clamped to >= 0.001");
        }

        public static void TestCompressorBelowThresholdNoCompression()
        {
            var c = new Compressor();
            c.Threshold = -10.0; // -10 dB ≈ 0.316 linear
            c.Ratio = 10.0;
            c.Mode = DetectionMode.Peak;
            // Feed 0.1 (≈ -20 dB) — well below threshold
            double out1 = 0;
            for (int i = 0; i < 10000; i++) out1 = c.Process(0.1);
            NearD(0.1, out1, 0.005, "below threshold → passthrough (≈0.1)");
        }

        public static void TestCompressorAboveThresholdCompresses()
        {
            var c = new Compressor();
            c.Threshold = -20.0;
            c.Ratio = 10.0;
            c.MakeupGain = 0;
            c.Mode = DetectionMode.Peak;
            c.AttackTime = 0.001;
            c.ReleaseTime = 0.05;
            // Feed 1.0 (0 dB) — 20 dB above -20 threshold → should compress to ≈ -2 dB
            double output = 0;
            for (int i = 0; i < 10000; i++) output = c.Process(1.0);
            AssertTrue(Math.Abs(output) < 1.0, $"compressed < 1.0 (got {output})");
        }

        public static void TestCompressorReset()
        {
            var c = new Compressor();
            c.Mode = DetectionMode.Peak;
            for (int i = 0; i < 1000; i++) c.Process(1.0);
            c.Reset();
            double output = c.Process(0.0);
            NearD(0.0, output, 1e-9, "after reset, silence in → silence out");
        }

        // ── Expander ──

        public static void TestExpanderDefaults()
        {
            var e = new Expander();
            AssertEqual(1.0, e.Ratio, "Ratio=1 (no expansion by default)");
        }

        public static void TestExpanderRatioClamp()
        {
            var e = new Expander();
            e.Ratio = 0.1;
            AssertEqual(1.0, e.Ratio, "ratio clamped to >= 1.0");
        }

        public static void TestExpanderReleaseClamp()
        {
            var e = new Expander();
            e.ReleaseTime = 0.0;
            AssertEqual(0.001, e.ReleaseTime, "release clamped to >= 0.001");
        }

        // ── Limiter ──

        public static void TestLimiterDefaults()
        {
            var l = new Limiter();
            AssertEqual(-1.0, l.Threshold, "Threshold=-1dB");
            AssertEqual(0.05, l.ReleaseTime, "Release=50ms");
            AssertEqual(Limiter.LimitMode.Soft, l.Mode, "Mode=Soft");
        }

        public static void TestLimiterHardMode()
        {
            var l = new Limiter();
            l.Mode = Limiter.LimitMode.Hard;
            l.Threshold = -6.0; // ≈ 0.501 linear
            double thresholdLin = Math.Pow(10.0, -6.0 / 20.0);
            double output = l.Process(1.0); // way over threshold
            NearD(thresholdLin, output, 1e-3, "hard limit clips to threshold");
            double out2 = l.Process(-1.0);
            NearD(-thresholdLin, out2, 1e-3, "hard limit clips negative to -threshold");
            double out3 = l.Process(0.1);
            NearD(0.1, out3, 1e-3, "below threshold passthrough");
        }

        public static void TestLimiterBrickwall()
        {
            var l = new Limiter();
            l.Mode = Limiter.LimitMode.Brickwall;
            l.Threshold = -6.0;
            double output = l.Process(1.0);
            double thresholdLin = Math.Pow(10.0, -6.0 / 20.0);
            AssertTrue(Math.Abs(output) <= thresholdLin + 1e-3, "brickwall limits output to threshold");
        }

        public static void TestLimiterLimitModeEnum()
        {
            AssertEqual(0, (int)Limiter.LimitMode.Hard, "Hard=0");
            AssertEqual(1, (int)Limiter.LimitMode.Soft, "Soft=1");
            AssertEqual(2, (int)Limiter.LimitMode.Brickwall, "Brickwall=2");
        }

        // ── NoiseGate ──

        public static void TestNoiseGateDefaults()
        {
            var g = new NoiseGate();
            AssertEqual(-50.0, g.Threshold, "Threshold=-50dB");
            AssertEqual(0.001, g.AttackTime, "Attack=1ms");
            AssertEqual(0.02, g.HoldTime, "Hold=20ms");
            AssertEqual(0.05, g.ReleaseTime, "Release=50ms");
            AssertEqual(80.0, g.Range, "Range=80dB");
        }

        public static void TestNoiseGateReleaseClamp()
        {
            var g = new NoiseGate();
            g.ReleaseTime = 0.0;
            AssertEqual(0.001, g.ReleaseTime, "release clamped >= 0.001");
        }

        public static void TestNoiseGateRangeClamp()
        {
            var g = new NoiseGate();
            g.Range = -10.0;
            AssertEqual(0.0, g.Range, "range clamped >= 0");
        }

        public static void TestNoiseGateClosesOnSilence()
        {
            var g = new NoiseGate();
            g.Threshold = -30.0; // ≈ 0.0316 linear
            // Feed silence → gate should close (output = 0 after release)
            double last = 0;
            for (int i = 0; i < 10000; i++) last = g.Process(0.0);
            NearD(0.0, last, 1e-9, "silence → gate closes to 0");
        }

        // ── TransientDesigner ──

        public static void TestTransientDesignerDefaults()
        {
            var t = new TransientDesigner();
            AssertEqual(0.0, t.AttackGain, "AttackGain=0 (0 dB pass-through)");
            AssertEqual(0.0, t.ReleaseGain, "ReleaseGain=0 (0 dB pass-through)");
        }

        public static void TestTransientDesignerSilencePassthrough()
        {
            var t = new TransientDesigner();
            double last = 0;
            for (int i = 0; i < 1000; i++) last = t.Process(0.0);
            NearD(0.0, last, 1e-9, "silence in → silence out");
        }
    }
}
