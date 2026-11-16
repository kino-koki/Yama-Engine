using System;
using System.Numerics;
using System.Linq;
using YamaEngine.Audio;
using YamaEngine.Network;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // AudioDepth / NetworkDepth — UE5.8 级确定性深度验证
    // ══════════════════════════════════════════════════════════════

    public static class AudioNetworkTests
    {
        private static void AssertNearValue(float got, float want, float tol, string msg)
        {
            AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");
        }

        // 音频 DSP：正弦波生成、RMS/Peak 统计量、低通滤波器高频衰减
        public static void TestAudioDspSineRmsAndLowPass()
        {
            const float sampleRate = 48000f;
            // 440Hz 正弦波，1秒，幅度 0.5
            var sine = AudioDSP.GenerateSineWave(440f, sampleRate, 1.0f, 0.5f);
            AssertTrue(sine.Length == (int)sampleRate, "Sine wave should generate sampleRate*duration samples.");
            // 正弦波峰值 = 振幅
            AssertNearValue(AudioDSP.Peak(sine), 0.5f, 0.01f, "Sine wave peak should equal amplitude.");
            // 正弦波 RMS = amp/√2
            AssertNearValue(AudioDSP.RMS(sine), 0.5f / MathF.Sqrt(2f), 0.01f,
                "Sine wave RMS should equal amp/sqrt(2).");
            // 低通滤波器（一阶 IIR）：对高频信号衰减，输出峰值应低于输入峰值
            var highFreq = AudioDSP.GenerateSineWave(10000f, sampleRate, 0.1f, 1.0f);
            var lp = AudioDSP.ApplyLowPass(highFreq, cutoff: 500f, sampleRate: sampleRate);
            float inPeak = AudioDSP.Peak(highFreq);
            float outPeak = AudioDSP.Peak(lp);
            AssertTrue(outPeak < inPeak * 0.9f,
                $"Low-pass at 500Hz should attenuate 10kHz signal (in={inPeak:F3}, out={outPeak:F3}).");
            // 直流信号（0Hz）通过低通应基本保持（alpha≈1 时透传）
            var dc = new float[1000];
            Array.Fill(dc, 1.0f);
            var dcLp = AudioDSP.ApplyLowPass(dc, cutoff: 1000f, sampleRate: sampleRate);
            AssertNearValue(dcLp[^1], 1.0f, 0.01f, "DC signal should pass through low-pass unattenuated.");
        }

        // 网络序列化 Writer/Reader round-trip：整数、浮点、字符串、向量逐字段还原
        public static void TestNetworkSerializationRoundTrip()
        {
            var writer = new NetworkSerializationWriter();
            writer.WriteInt32(42);
            writer.WriteInt32(-12345);
            writer.WriteFloat(3.14159f);
            writer.WriteFloat(-0.5f);
            writer.WriteString("YamaEngine");
            writer.WriteString(""); // 空字符串
            writer.WriteVector3(new Vector3(1f, 2f, 3f));
            var bytes = writer.ToArray();
            AssertTrue(bytes.Length > 0, "Writer should produce non-empty byte array.");

            var reader = new NetworkReaderReal(bytes);
            AssertTrue(reader.ReadInt32() == 42, "ReadInt32 should round-trip positive integer.");
            AssertTrue(reader.ReadInt32() == -12345, "ReadInt32 should round-trip negative integer.");
            AssertNearValue(reader.ReadFloat(), 3.14159f, 0.0001f, "ReadFloat should round-trip.");
            AssertNearValue(reader.ReadFloat(), -0.5f, 0.0001f, "ReadFloat should round-trip negative.");
            AssertEqual("YamaEngine", reader.ReadString(), "ReadString should round-trip length-prefixed string.");
            AssertEqual("", reader.ReadString(), "ReadString should round-trip empty string.");
            var v = reader.ReadVector3();
            AssertNearValue(v.X, 1f, 0.0001f, "ReadVector3.X should round-trip.");
            AssertNearValue(v.Y, 2f, 0.0001f, "ReadVector3.Y should round-trip.");
            AssertNearValue(v.Z, 3f, 0.0001f, "ReadVector3.Z should round-trip.");
        }
    }
}
