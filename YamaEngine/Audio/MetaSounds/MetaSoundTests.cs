// ============================================================================
//  YamaEngine.Audio.MetaSounds - 自检测试 (无需外部测试框架)
//  用途：在没有 GPU / 完整引擎的情况下，用纯逻辑断言验证 DSP 正确性，
//        作为「生产级可靠性」的可复现证据。可在任意 .NET 8 环境运行：
//        dotnet run / 或挂到 YamaEngine.Tests 下执行 MetaSoundTests.RunAll()
// ============================================================================

using System;
using System.IO;

namespace YamaEngine.Audio.MetaSounds
{
    public static class MetaSoundTests
    {
        private static int _fail;

        private static void Check(string name, bool cond, string detail = "")
        {
            if (cond) Console.WriteLine($"  PASS  {name}");
            else { _fail++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
        }

        private static int CountZeroCrossings(float[] s)
        {
            int n = 0;
            for (int i = 1; i < s.Length; i++)
            {
                if ((s[i - 1] <= 0f && s[i] > 0f) || (s[i - 1] >= 0f && s[i] < 0f)) n++;
            }
            return n;
        }

        private static float MaxAbs(float[] s, int from, int to)
        {
            float m = 0f;
            to = Math.Min(to, s.Length);
            for (int i = from; i < to; i++) m = Math.Max(m, Math.Abs(s[i]));
            return m;
        }

        private static float Rms(float[] s)
        {
            double sum = 0;
            foreach (var v in s) sum += v * v;
            return (float)Math.Sqrt(sum / Math.Max(1, s.Length));
        }

        /// <summary>振荡器音高正确 (440Hz 正弦)。</summary>
        public static void TestSinePitch()
        {
            var g = new MetaSoundGraph();
            var osc = g.Add(MetaSoundNodeKind.Oscillator);
            osc.IntParams["OscType"] = 0;
            osc.Inputs[0].DefaultValue = 440f;
            var gain = g.Add(MetaSoundNodeKind.Gain);
            gain.Inputs[1].DefaultValue = 0.8f;
            var outp = g.Add(MetaSoundNodeKind.Output);
            g.Connect(osc, 0, gain, 0);
            g.Connect(gain, 0, outp, 0);
            g.SetOutput(outp);

            var voice = new MetaSoundVoice(g, 48000.0);
            var clip = voice.RenderToClip(0.1f, 1);
            int zc = CountZeroCrossings(clip.Samples);
            int expected = (int)(2 * 440 * 0.1);
            Check("SinePitch.ZeroCrossings", Math.Abs(zc - expected) <= 8,
                $"got {zc}, expected ~{expected}");
            Check("SinePitch.Amplitude", MaxAbs(clip.Samples, 0, clip.Samples.Length) > 0.6f,
                $"max {MaxAbs(clip.Samples, 0, clip.Samples.Length)}");
        }

        /// <summary>ADSR 包络：起音后达到高峰，随后衰减至接近 0。</summary>
        public static void TestEnvelopeShape()
        {
            var g = new MetaSoundGraph();
            var osc = g.Add(MetaSoundNodeKind.Oscillator);
            osc.IntParams["OscType"] = 0;
            osc.Inputs[0].DefaultValue = 440f;
            var env = g.Add(MetaSoundNodeKind.Envelope);
            env.FloatParams["Attack"] = 0.01f;
            env.FloatParams["Decay"] = 0.1f;
            env.FloatParams["Sustain"] = 0f;
            env.FloatParams["Release"] = 0.05f;
            env.FloatParams["SustainHold"] = 0f;
            var gain = g.Add(MetaSoundNodeKind.Gain);
            gain.Inputs[1].DefaultValue = 0.8f;
            var outp = g.Add(MetaSoundNodeKind.Output);
            g.Connect(osc, 0, env, 1); // 触发留空 -> 起始自动触发
            g.Connect(env, 0, gain, 0);
            g.Connect(gain, 0, outp, 0);
            g.SetOutput(outp);

            var voice = new MetaSoundVoice(g, 48000.0);
            var clip = voice.RenderToClip(0.3f, 1);
            int sr = clip.SampleRate;
            float peak = MaxAbs(clip.Samples, 0, (int)(0.05f * sr));
            float tail = MaxAbs(clip.Samples, clip.Samples.Length - (int)(0.05f * sr), clip.Samples.Length);
            Check("Envelope.PeakReached", peak > 0.5f, $"peak {peak}");
            Check("Envelope.DecayedToNearZero", tail < 0.2f, $"tail {tail}");
        }

        /// <summary>低通滤波器确实衰减能量。</summary>
        public static void TestFilterAttenuates()
        {
            MetaSoundGraph Build(bool withFilter)
            {
                var g = new MetaSoundGraph();
                var osc = g.Add(MetaSoundNodeKind.Oscillator);
                osc.IntParams["OscType"] = 2; // saw (富含高频)
                osc.Inputs[0].DefaultValue = 200f;
                osc.Inputs[1].DefaultValue = 0.8f;
                MetaSoundNode last = osc;
                if (withFilter)
                {
                    var f = g.Add(MetaSoundNodeKind.BiquadFilter);
                    f.IntParams["FilterType"] = 0; // LP
                    f.Inputs[1].DefaultValue = 300f;
                    f.Inputs[2].DefaultValue = 1f;
                    g.Connect(osc, 0, f, 0);
                    last = f;
                }
                var outp = g.Add(MetaSoundNodeKind.Output);
                g.Connect(last, 0, outp, 0);
                return g.SetOutput(outp);
            }

            var raw = new MetaSoundVoice(Build(false), 48000.0).RenderToClip(0.2f, 1);
            var filt = new MetaSoundVoice(Build(true), 48000.0).RenderToClip(0.2f, 1);
            float rmsRaw = Rms(raw.Samples);
            float rmsFilt = Rms(filt.Samples);
            Check("Filter.ReducesEnergy", rmsFilt < rmsRaw * 0.95f,
                $"raw {rmsRaw:F4} filtered {rmsFilt:F4}");
        }

        /// <summary>实例隔离：同一图的两个独立声部首块输出完全一致 (确定性 + 状态独立)。</summary>
        public static void TestInstanceIsolation()
        {
            var g = MetaSoundPresets.SubBass();
            var a = new MetaSoundVoice(g, 48000.0).RenderToClip(0.05f, 1);
            var b = new MetaSoundVoice(g, 48000.0).RenderToClip(0.05f, 1);
            bool identical = a.Samples.Length == b.Samples.Length;
            for (int i = 0; i < a.Samples.Length && identical; i++)
                if (Math.Abs(a.Samples[i] - b.Samples[i]) > 1e-6f) identical = false;
            Check("Instance.IsolationDeterministic", identical);
        }

        /// <summary>WAV 写出格式正确。</summary>
        public static void TestWavWrite(string outDir)
        {
            if (string.IsNullOrEmpty(outDir))
            {
                Console.WriteLine("  SKIP  WavWrite (no outDir)");
                return;
            }
            Directory.CreateDirectory(outDir);
            var clip = new MetaSoundVoice(MetaSoundPresets.Pluck(), 48000.0).RenderToClip(0.5f, 1);
            string path = Path.Combine(outDir, "metasound_pluck.wav");
            MetaSoundWav.Write(path, clip.Samples, clip.SampleRate, clip.Channels);
            bool ok = File.Exists(path) && new FileInfo(path).Length > 44;
            Check("WavWrite.ValidFile", ok, path);
        }

        public static bool RunAll(string outDir = null)
        {
            _fail = 0;
            Console.WriteLine("[MetaSound] 自检开始");
            TestSinePitch();
            TestEnvelopeShape();
            TestFilterAttenuates();
            TestInstanceIsolation();
            TestWavWrite(outDir);
            Console.WriteLine(_fail == 0
                ? "[MetaSound] 全部通过 ✅"
                : $"[MetaSound] {_fail} 项失败 ❌");
            return _fail == 0;
        }
    }
}
