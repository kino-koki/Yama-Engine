// ============================================================================
//  ShanyuEngine.Audio.MetaSounds - 执行机 / 声部 / WAV 导出
//  解释 MetaSoundGraph 数据，按样本精确 (sample-accurate) 拉取求值。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using ShanyuEngine.Audio;

namespace ShanyuEngine.Audio.MetaSounds
{
    /// <summary>图的一个运行期实例：独立状态、可分别设参 / 触发。</summary>
    public sealed class MetaSoundGraphInstance
    {
        public MetaSoundGraph Graph;
        public Dictionary<string, MetaSoundNode> Nodes = new Dictionary<string, MetaSoundNode>();
        public Dictionary<string, MetaSoundNodeState> States = new Dictionary<string, MetaSoundNodeState>();
        public MetaSoundNode OutputNode;
        public double SampleRate = 48000.0;
        public long GlobalSample;
        /// <summary>请求在下一分块起始触发全部包络 (用于手动重触发)。</summary>
        public bool ForceTrigger;

        // 原始图节点 id -> 实例克隆节点 id 的映射
        private Dictionary<string, string> _idMap = new Dictionary<string, string>();

        private Dictionary<(string, int, int), float> _memo = new Dictionary<(string, int, int), float>();

        public MetaSoundGraphInstance(MetaSoundGraph graph, double sampleRate = 48000.0)
        {
            Graph = graph;
            SampleRate = sampleRate;

            var idMap = new Dictionary<string, string>();
            foreach (var n in graph.Nodes)
            {
                var c = n.Clone();
                idMap[n.Id] = c.Id;
                Nodes[c.Id] = c;
                States[c.Id] = new MetaSoundNodeState { RngState = 0x9E3779B9u };
            }
            foreach (var c in Nodes.Values)
            {
                foreach (var p in c.Inputs)
                {
                    if (p.Connection != null && idMap.TryGetValue(p.Connection.NodeId, out var newId))
                        p.Connection.NodeId = newId;
                }
            }
            _idMap = idMap;
            if (graph.OutputNodeId != null && idMap.TryGetValue(graph.OutputNodeId, out var cloneOutId)
                && Nodes.TryGetValue(cloneOutId, out var outNode))
                OutputNode = outNode;
        }

        /// <summary>设置图级参数 (实时)。</summary>
        public void SetParameter(string name, float value)
        {
            foreach (var p in Graph.Parameters)
            {
                if (p.Name != name) continue;
                if (_idMap.TryGetValue(p.TargetNodeId, out var cloneId)
                    && Nodes.TryGetValue(cloneId, out var node)
                    && p.TargetInputIndex < node.Inputs.Count)
                    node.Inputs[p.TargetInputIndex].DefaultValue = value;
                return;
            }
        }

        /// <summary>请求触发 (重触发未接触发输入的包络)。</summary>
        public void Trigger()
        {
            ForceTrigger = true;
            // 将所有包络的 prevTrig 清零，使下一分块产生一次上升沿 (可靠重触发)
            foreach (var node in Nodes.Values)
            {
                if (node.Kind == MetaSoundNodeKind.Envelope
                    && States.TryGetValue(node.Id, out var st))
                    st.PrevTrig = false;
            }
        }

        /// <summary>每个分块开始时清空求值备忘录 (状态保留)。</summary>
        public void BeginBlock() => _memo.Clear();

        public float ReadInput(MetaSoundNode node, int inputIndex, int i)
        {
            var pin = node.Inputs[inputIndex];
            if (pin.Connection != null && Nodes.TryGetValue(pin.Connection.NodeId, out var src))
                return Eval(src, pin.Connection.OutputIndex, i);
            return pin.DefaultValue;
        }

        public float Eval(MetaSoundNode node, int outIdx, int i)
        {
            var key = (node.Id, outIdx, i);
            if (_memo.TryGetValue(key, out var cached)) return cached;
            var state = States[node.Id];
            float v = MetaSoundRuntime.Compute(this, node, state, outIdx, i);
            _memo[key] = v;
            return v;
        }

        public bool IsTriggered(MetaSoundNode node, int outIdx, int i)
            => MetaSoundRuntime.EvaluateTrigger(this, node, outIdx, i);
    }

    /// <summary>执行机：DSP 分派。</summary>
    public static class MetaSoundRuntime
    {
        public static float Compute(MetaSoundGraphInstance inst, MetaSoundNode node, MetaSoundNodeState s, int outIdx, int i)
        {
            double sr = inst.SampleRate;
            switch (node.Kind)
            {
                case MetaSoundNodeKind.Oscillator:
                {
                    float freq = inst.ReadInput(node, 0, i);
                    float amp = inst.ReadInput(node, 1, i);
                    int t = node.IntParams["OscType"];
                    double ph = s.Phase;
                    float sig;
                    switch (t)
                    {
                        case 1: sig = ph < Math.PI ? 1f : -1f; break;
                        case 2: sig = (float)(2.0 * (ph / (2.0 * Math.PI)) - 1.0); break;
                        case 3: sig = (float)(2.0 / Math.PI * Math.Asin(Math.Sin(ph))); break;
                        default: sig = (float)Math.Sin(ph); break;
                    }
                    s.Phase = ph + 2.0 * Math.PI * freq / sr;
                    if (s.Phase > Math.PI * 2.0) s.Phase -= Math.PI * 2.0;
                    return sig * amp;
                }

                case MetaSoundNodeKind.Noise:
                {
                    float amp = inst.ReadInput(node, 0, i);
                    s.RngState ^= s.RngState << 13;
                    s.RngState ^= s.RngState >> 17;
                    s.RngState ^= s.RngState << 5;
                    float r = (s.RngState / (float)uint.MaxValue) * 2f - 1f;
                    return r * amp;
                }

                case MetaSoundNodeKind.WaveShaper:
                {
                    float x = inst.ReadInput(node, 0, i);
                    float drive = inst.ReadInput(node, 1, i);
                    return (float)Math.Tanh(x * drive);
                }

                case MetaSoundNodeKind.Envelope:
                {
                    float amp = inst.ReadInput(node, 1, i);
                    bool trig;
                    var trigPin = node.Inputs[0];
                    if (trigPin.IsConnected)
                        trig = inst.IsTriggered(inst.Nodes[trigPin.Connection.NodeId], trigPin.Connection.OutputIndex, i);
                    else
                        trig = (inst.GlobalSample == 0) || inst.ForceTrigger;

                    bool rising = trig && !s.PrevTrig;
                    s.PrevTrig = trig;

                    float A = node.FloatParams["Attack"];
                    float D = node.FloatParams["Decay"];
                    float S = node.FloatParams["Sustain"];
                    float R = node.FloatParams["Release"];
                    float H = node.FloatParams["SustainHold"];

                    if (rising) { s.EnvStage = 1; s.EnvTimer = 0f; s.EnvValue = 0f; }

                    switch (s.EnvStage)
                    {
                        case 1:
                            s.EnvValue += 1f / (Math.Max(1e-4f, A) * (float)sr);
                            if (s.EnvValue >= 1f) { s.EnvValue = 1f; s.EnvStage = 2; s.EnvTimer = 0f; }
                            break;
                        case 2:
                            s.EnvValue -= (1f - S) / (Math.Max(1e-4f, D) * (float)sr);
                            if (s.EnvValue <= S) { s.EnvValue = S; s.EnvStage = 3; s.EnvTimer = 0f; }
                            break;
                        case 3:
                            s.EnvValue = S;
                            if (H > 0f)
                            {
                                s.EnvTimer += 1f / (float)sr;
                                if (s.EnvTimer >= H) { s.EnvStage = 4; s.EnvTimer = 0f; }
                            }
                            break;
                        case 4:
                            s.EnvValue -= S / (Math.Max(1e-4f, R) * (float)sr);
                            if (s.EnvValue <= 0f) { s.EnvValue = 0f; s.EnvStage = 0; }
                            break;
                        default:
                            s.EnvValue = 0f;
                            break;
                    }
                    return s.EnvValue * amp;
                }

                case MetaSoundNodeKind.BiquadFilter:
                {
                    float x = inst.ReadInput(node, 0, i);
                    float f0 = inst.ReadInput(node, 1, i);
                    float q = inst.ReadInput(node, 2, i);
                    double w0 = 2.0 * Math.PI * f0 / sr;
                    double cw = Math.Cos(w0);
                    double sw = Math.Sin(w0);
                    double alpha = sw / (2.0 * Math.Max(1e-4, q));
                    double b0, b1, b2, a0, a1, a2;
                    int ft = node.IntParams["FilterType"];
                    if (ft == 1) { b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2; }
                    else if (ft == 2) { b0 = alpha; b1 = 0; b2 = -alpha; }
                    else { b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2; }
                    a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                    b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
                    double y = b0 * x + b1 * s.X1 + b2 * s.X2 - a1 * s.Y1 - a2 * s.Y2;
                    s.X2 = s.X1; s.X1 = x; s.Y2 = s.Y1; s.Y1 = (float)y;
                    return (float)y;
                }

                case MetaSoundNodeKind.Gain:
                {
                    float x = inst.ReadInput(node, 0, i);
                    float g = inst.ReadInput(node, 1, i);
                    return x * g;
                }

                case MetaSoundNodeKind.Mixer:
                {
                    float sum = 0f;
                    for (int j = 0; j < 4; j++)
                    {
                        float v = inst.ReadInput(node, j, i);
                        float g = node.FloatParams["Gain" + (j + 1)];
                        sum += v * g;
                    }
                    return sum;
                }

                case MetaSoundNodeKind.Delay:
                {
                    float x = inst.ReadInput(node, 0, i);
                    float time = inst.ReadInput(node, 1, i);
                    float fb = inst.ReadInput(node, 2, i);
                    float wet = inst.ReadInput(node, 3, i);
                    int len = Math.Max(1, (int)(time * sr));
                    s.EnsureDelay(len);
                    int pos = s.DelayPos;
                    float delayed = s.DelayBuf[pos];
                    float outv = x * (1f - wet) + delayed * wet;
                    s.DelayBuf[pos] = x + delayed * fb;
                    s.DelayPos = (pos + 1) % len;
                    return outv;
                }

                case MetaSoundNodeKind.MathAdd:
                    return inst.ReadInput(node, 0, i) + inst.ReadInput(node, 1, i);
                case MetaSoundNodeKind.MathMul:
                    return inst.ReadInput(node, 0, i) * inst.ReadInput(node, 1, i);
                case MetaSoundNodeKind.MathSin:
                    return (float)Math.Sin(inst.ReadInput(node, 0, i));
                case MetaSoundNodeKind.MathClamp:
                {
                    float v = inst.ReadInput(node, 0, i);
                    float mn = inst.ReadInput(node, 1, i);
                    float mx = inst.ReadInput(node, 2, i);
                    return Math.Clamp(v, mn, mx);
                }
                case MetaSoundNodeKind.MathRandom:
                {
                    float mn = inst.ReadInput(node, 0, i);
                    float mx = inst.ReadInput(node, 1, i);
                    s.RngState ^= s.RngState << 13;
                    s.RngState ^= s.RngState >> 17;
                    s.RngState ^= s.RngState << 5;
                    float r = s.RngState / (float)uint.MaxValue;
                    return mn + (mx - mn) * r;
                }

                default:
                    return 0f; // Output / 触发器节点不经由 Eval 产生连续值
            }
        }

        public static bool EvaluateTrigger(MetaSoundGraphInstance inst, MetaSoundNode node, int outIdx, int i)
        {
            double sr = inst.SampleRate;
            switch (node.Kind)
            {
                case MetaSoundNodeKind.TriggerOnce:
                    return inst.GlobalSample == 0;
                case MetaSoundNodeKind.Metronome:
                    int p = (int)(node.FloatParams["Period"] * sr);
                    return p > 0 && (inst.GlobalSample % p) == 0;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 声部：包装一个图实例，提供与 ShanyuEngine.Audio.AudioSource.ReadSamples
    /// 完全一致的调用约定，因此可以直接接入现有混音器 / 输出链路。
    /// </summary>
    public sealed class MetaSoundVoice
    {
        public MetaSoundGraphInstance Instance;
        public float SampleRate;
        public bool Loop = true;

        public MetaSoundVoice(MetaSoundGraph graph, double sampleRate = 48000.0)
        {
            SampleRate = (float)sampleRate;
            Instance = new MetaSoundGraphInstance(graph, sampleRate);
        }

        /// <summary>
        /// 渲染若干帧到输出缓冲 (累加，与 AudioSource 行为一致，便于多声部混音)。
        /// 返回 true 表示已结束 (Loop=false 且超过设定时长)。
        /// </summary>
        public bool ReadSamples(float[] outputBuffer, int frames, int channels)
            => RenderInto(outputBuffer, 0, frames, channels);

        public bool RenderInto(float[] outputBuffer, int frameOffset, int frames, int channels)
        {
            int idxL = Instance.OutputNode.IndexOfInput("InL");
            int idxR = Instance.OutputNode.IndexOfInput("InR");
            int block = 256;
            int remaining = frames;
            int outPos = frameOffset * channels;

            while (remaining > 0)
            {
                int n = Math.Min(block, remaining);
                Instance.BeginBlock();
                for (int i = 0; i < n; i++)
                {
                    float l = Instance.ReadInput(Instance.OutputNode, idxL, i);
                    float r = Instance.ReadInput(Instance.OutputNode, idxR, i);
                    int bi = outPos + i * channels;
                    outputBuffer[bi] += l;
                    if (channels >= 2)
                        outputBuffer[bi + 1] += (Math.Abs(r) > 1e-9f) ? r : l;
                }
                outPos += n * channels;
                remaining -= n;
                Instance.GlobalSample += n;
                Instance.ForceTrigger = false;
            }
            return false;
        }

        /// <summary>离线渲染为音频片段，可赋值给现有 AudioSource.Clip 播放。</summary>
        public AudioSampleClip RenderToClip(float durationSec, int channels = 1)
        {
            int totalFrames = (int)(durationSec * SampleRate);
            var buf = new float[totalFrames * channels];
            int done = 0;
            int block = 256;
            while (done < totalFrames)
            {
                int n = Math.Min(block, totalFrames - done);
                for (int k = 0; k < n * channels; k++) buf[done * channels + k] = 0f;
                RenderInto(buf, done, n, channels);
                done += n;
            }
            return new AudioSampleClip
            {
                Name = "MetaSound",
                Samples = buf,
                SampleRate = (int)SampleRate,
                Channels = channels
            };
        }
    }

    /// <summary>16-bit PCM WAV 写出 (用于离线验证 / 试听)。</summary>
    public static class MetaSoundWav
    {
        private static void WriteTag(BinaryWriter bw, string tag)
        {
            foreach (char c in tag) bw.Write((byte)c);
        }

        public static void Write(string path, float[] samples, int sampleRate, int channels = 1)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            int dataLen = samples.Length * 2;
            WriteTag(bw, "RIFF");
            bw.Write(36 + dataLen);
            WriteTag(bw, "WAVE");
            WriteTag(bw, "fmt ");
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * channels * 2);
            bw.Write((short)(channels * 2));
            bw.Write((short)16);
            WriteTag(bw, "data");
            bw.Write(dataLen);
            foreach (var s in samples)
            {
                short v = (short)(Math.Clamp(s, -1f, 1f) * 32767f);
                bw.Write(v);
            }
        }
    }
}
