// ============================================================================
//  YamaEngine.Audio.MetaSounds - 内置示例 Patch (同时作为集成用法范例)
//  对标 UE5 MetaSounds 的「音效 Patch」：用节点图描述一段可合成的声音。
// ============================================================================

namespace YamaEngine.Audio.MetaSounds
{
    public static class MetaSoundPresets
    {
        /// <summary>持续低音 pad：正弦 + 慢起音包络。</summary>
        public static MetaSoundGraph SubBass()
        {
            var g = new MetaSoundGraph();
            var osc = g.Add(MetaSoundNodeKind.Oscillator);
            osc.IntParams["OscType"] = 0; // sine
            var env = g.Add(MetaSoundNodeKind.Envelope);
            env.FloatParams["Attack"] = 0.4f;
            env.FloatParams["Decay"] = 0.3f;
            env.FloatParams["Sustain"] = 0.8f;
            env.FloatParams["Release"] = 1.2f;
            env.FloatParams["SustainHold"] = 0f; // 无限保持 (pad)
            var gain = g.Add(MetaSoundNodeKind.Gain);
            gain.Inputs[1].DefaultValue = 0.5f;
            var outp = g.Add(MetaSoundNodeKind.Output);

            g.AddParameter("Freq", osc, "Frequency", 55f);
            g.Connect(osc, 0, env, 1);       // osc -> env.Amplitude
            g.Connect(env, 0, gain, 0);      // env -> gain.In
            g.Connect(gain, 0, outp, 0);     // gain -> out.InL
            g.Connect(gain, 0, outp, 1);     // gain -> out.InR
            return g.SetOutput(outp);
        }

        /// <summary>拨弦音：三角波 + 极快起音、中速衰减的一次性包络，由节拍器周期重触发。</summary>
        public static MetaSoundGraph Pluck()
        {
            var g = new MetaSoundGraph();
            var osc = g.Add(MetaSoundNodeKind.Oscillator);
            osc.IntParams["OscType"] = 3; // triangle
            var metro = g.Add(MetaSoundNodeKind.Metronome);
            metro.FloatParams["Period"] = 0.5f;
            var env = g.Add(MetaSoundNodeKind.Envelope);
            env.FloatParams["Attack"] = 0.005f;
            env.FloatParams["Decay"] = 0.25f;
            env.FloatParams["Sustain"] = 0f;   // 衰减到 0
            env.FloatParams["Release"] = 0.08f;
            env.FloatParams["SustainHold"] = 0f;
            var gain = g.Add(MetaSoundNodeKind.Gain);
            gain.Inputs[1].DefaultValue = 0.6f;
            var outp = g.Add(MetaSoundNodeKind.Output);

            g.AddParameter("Freq", osc, "Frequency", 220f);
            g.Connect(metro, 0, env, 0);   // metro -> env.Trigger
            g.Connect(osc, 0, env, 1);     // osc -> env.Amplitude
            g.Connect(env, 0, gain, 0);
            g.Connect(gain, 0, outp, 0);
            g.Connect(gain, 0, outp, 1);
            return g.SetOutput(outp);
        }

        /// <summary>噪声打击：白噪声 + 极快起音的一次性包络 (单次触发)。</summary>
        public static MetaSoundGraph NoisePerc()
        {
            var g = new MetaSoundGraph();
            var noise = g.Add(MetaSoundNodeKind.Noise);
            var env = g.Add(MetaSoundNodeKind.Envelope);
            env.FloatParams["Attack"] = 0.001f;
            env.FloatParams["Decay"] = 0.15f;
            env.FloatParams["Sustain"] = 0f;
            env.FloatParams["Release"] = 0.05f;
            env.FloatParams["SustainHold"] = 0f;
            var gain = g.Add(MetaSoundNodeKind.Gain);
            gain.Inputs[1].DefaultValue = 0.7f;
            var outp = g.Add(MetaSoundNodeKind.Output);

            g.Connect(noise, 0, env, 1); // 触发输入留空 -> 起始自动触发一次
            g.Connect(env, 0, gain, 0);
            g.Connect(gain, 0, outp, 0);
            g.Connect(gain, 0, outp, 1);
            return g.SetOutput(outp);
        }

        /// <summary>滤波扫频：锯齿波经低通，截止频率由低频 LFO 实时调制 (展示音频率调制)。</summary>
        public static MetaSoundGraph FilterSweep()
        {
            var g = new MetaSoundGraph();
            var osc = g.Add(MetaSoundNodeKind.Oscillator);
            osc.IntParams["OscType"] = 2; // saw
            osc.Inputs[1].DefaultValue = 0.5f;

            var lfo = g.Add(MetaSoundNodeKind.Oscillator);
            lfo.IntParams["OscType"] = 0; // sine
            lfo.Inputs[0].DefaultValue = 0.5f; // 0.5 Hz

            var mul = g.Add(MetaSoundNodeKind.MathMul); // LFO * 800
            mul.Inputs[1].DefaultValue = 800f;
            var add = g.Add(MetaSoundNodeKind.MathAdd); // + 1000 基准
            add.Inputs[1].DefaultValue = 1000f;

            var filter = g.Add(MetaSoundNodeKind.BiquadFilter);
            filter.IntParams["FilterType"] = 0; // LP
            filter.Inputs[2].DefaultValue = 6f; // Q

            var gain = g.Add(MetaSoundNodeKind.Gain);
            gain.Inputs[1].DefaultValue = 0.5f;
            var outp = g.Add(MetaSoundNodeKind.Output);

            g.Connect(lfo, 0, mul, 0);     // LFO -> mul.A
            g.Connect(mul, 0, add, 0);     // mul -> add.A
            g.Connect(add, 0, filter, 1);  // add -> filter.Frequency
            g.Connect(osc, 0, filter, 0);  // osc -> filter.In
            g.Connect(filter, 0, gain, 0);
            g.Connect(gain, 0, outp, 0);
            g.Connect(gain, 0, outp, 1);
            return g.SetOutput(outp);
        }
    }
}
