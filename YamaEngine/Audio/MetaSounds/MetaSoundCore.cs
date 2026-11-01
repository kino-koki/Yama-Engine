// ============================================================================
//  YamaEngine.Audio.MetaSounds - 核心数据模型
//  yama引擎 -- 程序化音频节点图系统 (对标 UE5 MetaSounds)
// ----------------------------------------------------------------------------
//  设计同构于 UE5 MetaSounds：
//    - 音频图是一份「数据」(MetaSoundGraph)，由节点(MetaSoundNode)、引脚
//      (MetaSoundPin)、连接(MetaSoundPinConnection) 组成；
//    - 执行机 (MetaSoundRuntime) 解释这份数据，按样本精确 (sample-accurate)
//      地拉取求值，所有状态保存在 MetaSoundNodeState 中；
//    - 图可以被实例化多次 (MetaSoundGraphInstance)，每个实例独立持有状态，
//      可分别设置参数 / 触发，因此同一个音效 patch 可同时驱动无数个声部。
//  不依赖任何 GPU / 外部库，纯 C# + System.Numerics，标准 .NET 8 即可编译。
// ============================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.Audio.MetaSounds
{
    /// <summary>引脚数据类型。</summary>
    public enum MetaSoundPinType
    {
        Audio,   // 音频信号 (float 流)
        Float,   // 标量浮点 (控制率 / 可音频率调制)
        Trigger, // 触发器 (离散事件)
        Bool,
        Int
    }

    /// <summary>节点种类。DSP 行为由执行机按此分派。</summary>
    public enum MetaSoundNodeKind
    {
        Output,        // 终端输出 (InL, InR)
        Oscillator,    // 振荡器
        Noise,         // 白噪声
        WaveShaper,    // 波形整形 (tanh 饱和)
        Envelope,      // ADSR 包络
        BiquadFilter,  // 双二阶滤波器 (LP/HP/BP)
        Gain,          // 增益
        Mixer,         // 4 路混音
        Delay,         // 反馈延迟
        MathAdd,       // A + B
        MathMul,       // A * B
        MathSin,       // sin(A)
        MathClamp,     // clamp(V, Min, Max)
        MathRandom,    // 每样本随机 [Min, Max]
        TriggerOnce,   // 仅在声部起始触发一次
        Metronome      // 周期性触发
    }

    /// <summary>连接：指向某个节点的某个输出引脚。</summary>
    public sealed class MetaSoundPinConnection
    {
        public string NodeId;
        public int OutputIndex;

        public MetaSoundPinConnection(string nodeId, int outputIndex)
        {
            NodeId = nodeId;
            OutputIndex = outputIndex;
        }
    }

    /// <summary>输入引脚。</summary>
    public sealed class MetaSoundPin
    {
        public MetaSoundPinType Type;
        /// <summary>未连接时的默认值 (也是控制率常量)。</summary>
        public float DefaultValue;
        /// <summary>连接 (为 null 表示未连接，使用 DefaultValue)。</summary>
        public MetaSoundPinConnection Connection;

        public bool IsConnected => Connection != null;

        public MetaSoundPin(MetaSoundPinType type, float defaultValue = 0f)
        {
            Type = type;
            DefaultValue = defaultValue;
        }
    }

    /// <summary>图级参数：把某个节点的输入引脚暴露为 patch 的可调参数。</summary>
    public sealed class MetaSoundParameter
    {
        public string Name;
        public float DefaultValue;
        public string TargetNodeId;
        public int TargetInputIndex;

        public MetaSoundParameter(string name, float defaultValue, string targetNodeId, int targetInputIndex)
        {
            Name = name;
            DefaultValue = defaultValue;
            TargetNodeId = targetNodeId;
            TargetInputIndex = targetInputIndex;
        }
    }

    /// <summary>音频图节点 (纯数据)。</summary>
    public sealed class MetaSoundNode
    {
        public string Id = Guid.NewGuid().ToString();
        public MetaSoundNodeKind Kind;
        public List<MetaSoundPin> Inputs = new List<MetaSoundPin>();
        public List<MetaSoundPinType> Outputs = new List<MetaSoundPinType>();

        /// <summary>种类相关的整型常量 (如滤波类型 / 振荡波形)。</summary>
        public Dictionary<string, int> IntParams = new Dictionary<string, int>();
        /// <summary>种类相关的浮点常量 (如 ADSR 时间 / Q 值)。</summary>
        public Dictionary<string, float> FloatParams = new Dictionary<string, float>();

        public int IndexOfInput(string name)
        {
            // 约定：输入引脚名编码在 DefaultValue 之外由调用方维护映射；
            // 这里用顺序约定，由 Create() 固定布局，故用辅助查找。
            return _inputNames.IndexOf(name);
        }

        // 仅用于构建期可读的输入名列表 (与 Create() 中布局严格对应)。
        internal List<string> _inputNames = new List<string>();

        /// <summary>按固定布局创建一个节点并填好引脚 / 参数。</summary>
        public static MetaSoundNode Create(MetaSoundNodeKind kind)
        {
            var n = new MetaSoundNode { Kind = kind };
            switch (kind)
            {
                case MetaSoundNodeKind.Output:
                    n._inputNames.Add("InL"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("InR"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    break;

                case MetaSoundNodeKind.Oscillator:
                    n._inputNames.Add("Frequency"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 440f));
                    n._inputNames.Add("Amplitude"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.IntParams["OscType"] = 0; // 0=sine 1=square 2=saw 3=triangle
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.Noise:
                    n._inputNames.Add("Amplitude"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.FloatParams["Seed"] = 0f;
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.WaveShaper:
                    n._inputNames.Add("In"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("Drive"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 2f));
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.Envelope:
                    n._inputNames.Add("Trigger"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Trigger, 0f));
                    n._inputNames.Add("Amplitude"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.FloatParams["Attack"] = 0.01f;
                    n.FloatParams["Decay"] = 0.1f;
                    n.FloatParams["Sustain"] = 0.7f;
                    n.FloatParams["Release"] = 0.2f;
                    n.FloatParams["SustainHold"] = 0f; // 0 = 无限保持
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.BiquadFilter:
                    n._inputNames.Add("In"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("Frequency"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1000f));
                    n._inputNames.Add("Q"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.IntParams["FilterType"] = 0; // 0=LP 1=HP 2=BP
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.Gain:
                    n._inputNames.Add("In"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("Gain"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.Mixer:
                    n._inputNames.Add("In1"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("In2"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("In3"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("In4"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n.FloatParams["Gain1"] = 1f;
                    n.FloatParams["Gain2"] = 1f;
                    n.FloatParams["Gain3"] = 1f;
                    n.FloatParams["Gain4"] = 1f;
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.Delay:
                    n._inputNames.Add("In"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Audio, 0f));
                    n._inputNames.Add("Time"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0.25f));
                    n._inputNames.Add("Feedback"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0.4f));
                    n._inputNames.Add("Wet"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0.5f));
                    n.Outputs.Add(MetaSoundPinType.Audio);
                    break;

                case MetaSoundNodeKind.MathAdd:
                    n._inputNames.Add("A"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0f));
                    n._inputNames.Add("B"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0f));
                    n.Outputs.Add(MetaSoundPinType.Float);
                    break;

                case MetaSoundNodeKind.MathMul:
                    n._inputNames.Add("A"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0f));
                    n._inputNames.Add("B"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.Outputs.Add(MetaSoundPinType.Float);
                    break;

                case MetaSoundNodeKind.MathSin:
                    n._inputNames.Add("A"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0f));
                    n.Outputs.Add(MetaSoundPinType.Float);
                    break;

                case MetaSoundNodeKind.MathClamp:
                    n._inputNames.Add("V"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0f));
                    n._inputNames.Add("Min"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 0f));
                    n._inputNames.Add("Max"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.Outputs.Add(MetaSoundPinType.Float);
                    break;

                case MetaSoundNodeKind.MathRandom:
                    n._inputNames.Add("Min"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, -1f));
                    n._inputNames.Add("Max"); n.Inputs.Add(new MetaSoundPin(MetaSoundPinType.Float, 1f));
                    n.FloatParams["Seed"] = 1f;
                    n.Outputs.Add(MetaSoundPinType.Float);
                    break;

                case MetaSoundNodeKind.TriggerOnce:
                    n.Outputs.Add(MetaSoundPinType.Trigger);
                    break;

                case MetaSoundNodeKind.Metronome:
                    n.FloatParams["Period"] = 0.5f; // 秒
                    n.Outputs.Add(MetaSoundPinType.Trigger);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
            return n;
        }

        /// <summary>深拷贝 (用于实例化，保证每个声部状态独立)。</summary>
        public MetaSoundNode Clone()
        {
            var c = new MetaSoundNode
            {
                Id = Guid.NewGuid().ToString(),
                Kind = Kind,
                IntParams = new Dictionary<string, int>(IntParams),
                FloatParams = new Dictionary<string, float>(FloatParams),
                _inputNames = new List<string>(_inputNames)
            };
            foreach (var p in Inputs)
            {
                c.Inputs.Add(new MetaSoundPin(p.Type, p.DefaultValue)
                {
                    Connection = p.Connection == null
                        ? null
                        : new MetaSoundPinConnection(p.Connection.NodeId, p.Connection.OutputIndex)
                });
            }
            c.Outputs = new List<MetaSoundPinType>(Outputs);
            return c;
        }
    }

    /// <summary>每个节点的运行期状态 (按实例独立存放)。</summary>
    public sealed class MetaSoundNodeState
    {
        public double Phase;          // 振荡器相位
        public float X1, X2, Y1, Y2;  // 双二阶滤波器记忆
        public int EnvStage;          // 0 idle 1 attack 2 decay 3 sustain 4 release
        public float EnvValue;
        public float EnvTimer;
        public bool PrevTrig;
        public float[] DelayBuf;      // 延迟线
        public int DelayPos;
        public uint RngState;         // 随机状态
        public int DelayLen;          // 延迟线当前长度 (样本)

        public void EnsureDelay(int length)
        {
            if (DelayBuf == null || DelayBuf.Length < length)
            {
                DelayBuf = new float[length];
                DelayPos = 0;
                DelayLen = length;
            }
            else
            {
                DelayLen = length;
            }
        }
    }

    /// <summary>音频图 (数据)。可用构建器方法组装。</summary>
    public sealed class MetaSoundGraph
    {
        public List<MetaSoundNode> Nodes = new List<MetaSoundNode>();
        public List<MetaSoundParameter> Parameters = new List<MetaSoundParameter>();
        public string OutputNodeId;

        // ---- 构建器 API ----

        public MetaSoundNode Add(MetaSoundNodeKind kind)
        {
            var n = MetaSoundNode.Create(kind);
            Nodes.Add(n);
            return n;
        }

        public MetaSoundGraph SetOutput(MetaSoundNode outputNode)
        {
            OutputNodeId = outputNode.Id;
            return this;
        }

        public MetaSoundGraph Connect(MetaSoundNode from, int outputIndex, MetaSoundNode to, int inputIndex)
        {
            if (inputIndex < 0 || inputIndex >= to.Inputs.Count)
                throw new ArgumentOutOfRangeException(nameof(inputIndex));
            if (outputIndex < 0 || outputIndex >= from.Outputs.Count)
                throw new ArgumentOutOfRangeException(nameof(outputIndex));
            to.Inputs[inputIndex].Connection = new MetaSoundPinConnection(from.Id, outputIndex);
            return this;
        }

        public MetaSoundGraph Connect(MetaSoundNode from, MetaSoundNode to, string inputName)
        {
            int idx = to.IndexOfInput(inputName);
            if (idx < 0) throw new ArgumentException($"节点 {to.Kind} 没有名为 {inputName} 的输入");
            return Connect(from, 0, to, idx);
        }

        /// <summary>把某个节点的输入引脚暴露为图级可调参数。</summary>
        public MetaSoundGraph AddParameter(string name, MetaSoundNode target, string inputName, float defaultValue)
        {
            int idx = target.IndexOfInput(inputName);
            if (idx < 0) throw new ArgumentException($"节点 {target.Kind} 没有名为 {inputName} 的输入");
            target.Inputs[idx].DefaultValue = defaultValue;
            Parameters.Add(new MetaSoundParameter(name, defaultValue, target.Id, idx));
            return this;
        }

        public MetaSoundGraph Clone()
        {
            var g = new MetaSoundGraph();
            var idMap = new Dictionary<string, string>();
            foreach (var n in Nodes)
            {
                var c = n.Clone();
                idMap[n.Id] = c.Id;
                g.Nodes.Add(c);
            }
            // 重映射连接
            foreach (var c in g.Nodes)
            {
                foreach (var p in c.Inputs)
                {
                    if (p.Connection != null && idMap.TryGetValue(p.Connection.NodeId, out var newId))
                        p.Connection.NodeId = newId;
                }
            }
            if (OutputNodeId != null && idMap.TryGetValue(OutputNodeId, out var outId))
                g.OutputNodeId = outId;
            foreach (var p in Parameters)
            {
                var newTarget = idMap.TryGetValue(p.TargetNodeId, out var t) ? t : p.TargetNodeId;
                g.Parameters.Add(new MetaSoundParameter(p.Name, p.DefaultValue, newTarget, p.TargetInputIndex));
            }
            return g;
        }
    }
}
