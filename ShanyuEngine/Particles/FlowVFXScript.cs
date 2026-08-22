using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════
    // 山雨引擎 FlowVFX 风格粒子脚本系统
    // --------------------------------------------------------------------
    // 设计目标：超越 UE7 FlowVFX Script Graph 的可视化粒子行为编辑
    //   - 可视化脚本图：节点 + 连接（Pin）构成有向无环图(DAG)
    //   - 节点类型：数学运算 / 粒子属性采样 / 力施加 / 事件 / 常量 / 曲线采样
    //   - 脚本编译器：将图编译为 HLSL 计算着色器（反向拓扑序表达式展开）
    //   - 参数绑定：图中的可编辑参数编译期绑定到常量缓冲，运行时可热更新
    //   - 与 RHI 对接：编译产物经 RHIDevice.CompileShader → CreateComputePipelineState
    // ════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────
    // 第一部分：引脚(Pin)类型系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚本引脚数据类型。对应 HLSL 中的标量/向量类型。
    /// </summary>
    public enum ScriptPinType
    {
        Float,    // float
        Float2,   // float2
        Float3,   // float3
        Float4,   // float4
        Bool,     // bool
        Exec      // 执行流引脚（事件入口/出口，无数据）
    }

    /// <summary>
    /// 将引脚类型转换为 HLSL 类型字符串。
    /// </summary>
    public static class ScriptPinTypeUtils
    {
        public static string ToHLSL(this ScriptPinType t) => t switch
        {
            ScriptPinType.Float => "float",
            ScriptPinType.Float2 => "float2",
            ScriptPinType.Float3 => "float3",
            ScriptPinType.Float4 => "float4",
            ScriptPinType.Bool => "bool",
            _ => "void"
        };

        /// <summary>两引脚类型是否可隐式转换（窄化允许）。</summary>
        public static bool IsCompatible(ScriptPinType from, ScriptPinType to)
        {
            if (from == to) return true;
            // 标量可扩展为任意向量
            if (from == ScriptPinType.Float) return true;
            // 同维度族可转换
            return (int)from <= (int)to;
        }
    }

    /// <summary>
    /// 脚本引脚。一个节点拥有若干输入/输出引脚，引脚之间通过连接传递数据。
    /// </summary>
    public class ScriptPin
    {
        public string Name;
        public ScriptPinType Type;
        public bool IsInput;
        public ScriptNode? Owner;
        /// <summary>输入引脚连接的源输出引脚（单输入单连接）。</summary>
        public ScriptPin? ConnectedFrom;
        /// <summary>输出引脚连接的目标输入引脚列表（一对多）。</summary>
        public readonly List<ScriptPin> ConnectedTo = new();

        public ScriptPin(string name, ScriptPinType type, bool isInput, ScriptNode owner)
        {
            Name = name; Type = type; IsInput = isInput; Owner = owner;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第二部分：脚本节点基类与节点分类
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚本节点类别。用于编辑器分类与编译器调度。
    /// </summary>
    public enum ScriptNodeCategory
    {
        Event,      // 事件入口（OnUpdate/OnSpawn/OnCollide/OnDeath）
        Input,      // 粒子属性读取
        Output,     // 粒子属性写入
        Math,       // 数学运算
        Constant,   // 常量
        Sampling,   // 采样（曲线/纹理/矢量场）
        Force,      // 力施加
        Utility,    // 工具（Random/Clamp 等）
        Parameter   // 可编辑参数
    }

    /// <summary>
    /// 脚本节点抽象基类。每个节点声明输入/输出引脚，并提供 HLSL 表达式生成方法。
    /// 编译器对每个输出引脚调用 <see cref="EmitExpression"/> 生成对应的 HLSL 代码。
    /// </summary>
    public abstract class ScriptNode
    {
        public uint Id;
        public string Name;
        public ScriptNodeCategory Category;
        public readonly List<ScriptPin> Inputs = new();
        public readonly List<ScriptPin> Outputs = new();

        protected ScriptNode(uint id, string name, ScriptNodeCategory cat)
        {
            Id = id; Name = name; Category = cat;
        }

        protected ScriptPin AddInput(string n, ScriptPinType t) =>
            AddPin(n, t, true);
        protected ScriptPin AddOutput(string n, ScriptPinType t) =>
            AddPin(n, t, false);

        private ScriptPin AddPin(string n, ScriptPinType t, bool isInput)
        {
            var p = new ScriptPin(n, t, isInput, this);
            (isInput ? Inputs : Outputs).Add(p);
            return p;
        }

        /// <summary>
        /// 为指定输出引脚生成 HLSL 表达式字符串。
        /// 编译器负责保证输入引脚的连接表达式已先生成并可用。
        /// </summary>
        /// <param name="outputPin">请求生成的输出引脚</param>
        /// <param name="ctx">编译上下文（含已生成表达式缓存）</param>
        public abstract string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx);

        /// <summary>节点是否有副作用（写入粒子属性/施加力）。副作用节点作为编译入口。</summary>
        public virtual bool HasSideEffect => Category == ScriptNodeCategory.Output
                                          || Category == ScriptNodeCategory.Force;
    }

    // ──────────────────────────────────────────────────────────────────
    // 第三部分：具体节点实现
    // ──────────────────────────────────────────────────────────────────

    // ── 事件节点 ──

    /// <summary>更新事件入口。每帧对每个存活粒子触发。</summary>
    public class OnUpdateNode : ScriptNode
    {
        public OnUpdateNode(uint id) : base(id, "OnUpdate", ScriptNodeCategory.Event)
        {
            AddOutput("Exec", ScriptPinType.Exec);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx) => "";
    }

    /// <summary>生成事件入口。粒子生成时触发。</summary>
    public class OnSpawnNode : ScriptNode
    {
        public OnSpawnNode(uint id) : base(id, "OnSpawn", ScriptNodeCategory.Event)
        {
            AddOutput("Exec", ScriptPinType.Exec);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx) => "";
    }

    /// <summary>碰撞事件入口。粒子发生碰撞时触发。</summary>
    public class OnCollideNode : ScriptNode
    {
        public OnCollideNode(uint id) : base(id, "OnCollide", ScriptNodeCategory.Event)
        {
            AddOutput("Exec", ScriptPinType.Exec);
            AddOutput("Normal", ScriptPinType.Float3);
            AddOutput("ImpactSpeed", ScriptPinType.Float);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            if (outputPin.Name == "Normal") return "gCollisionNormal";
            if (outputPin.Name == "ImpactSpeed") return "gImpactSpeed";
            return "";
        }
    }

    // ── 粒子属性输入节点 ──

    /// <summary>读取粒子位置。</summary>
    public class GetPositionNode : ScriptNode
    {
        public GetPositionNode(uint id) : base(id, "GetPosition", ScriptNodeCategory.Input)
            => AddOutput("Position", ScriptPinType.Float3);
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx) => "Particle.Position";
    }

    /// <summary>读取粒子速度。</summary>
    public class GetVelocityNode : ScriptNode
    {
        public GetVelocityNode(uint id) : base(id, "GetVelocity", ScriptNodeCategory.Input)
            => AddOutput("Velocity", ScriptPinType.Float3);
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx) => "Particle.Velocity";
    }

    /// <summary>读取粒子归一化年龄 [0,1]。</summary>
    public class GetAgeNode : ScriptNode
    {
        public GetAgeNode(uint id) : base(id, "GetNormalizedAge", ScriptNodeCategory.Input)
            => AddOutput("Age", ScriptPinType.Float);
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
            => "saturate(Particle.Age / Particle.Lifetime)";
    }

    /// <summary>读取粒子颜色。</summary>
    public class GetColorNode : ScriptNode
    {
        public GetColorNode(uint id) : base(id, "GetColor", ScriptNodeCategory.Input)
            => AddOutput("Color", ScriptPinType.Float4);
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx) => "Particle.Color";
    }

    /// <summary>读取粒子随机种子。</summary>
    public class GetRandomNode : ScriptNode
    {
        public GetRandomNode(uint id) : base(id, "GetRandomSeed", ScriptNodeCategory.Input)
            => AddOutput("Seed", ScriptPinType.Float);
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx) => "Particle.RandomSeed";
    }

    // ── 粒子属性输出节点（副作用）──

    /// <summary>写入粒子位置。</summary>
    public class SetPositionNode : ScriptNode
    {
        public SetPositionNode(uint id) : base(id, "SetPosition", ScriptNodeCategory.Output)
        {
            AddInput("Exec", ScriptPinType.Exec);
            AddInput("Position", ScriptPinType.Float3);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string v = ctx.GetInputExpression(Inputs[1]);
            return $"    Particle.Position = {v};\n";
        }
        public override bool HasSideEffect => true;
    }

    /// <summary>写入粒子速度。</summary>
    public class SetVelocityNode : ScriptNode
    {
        public SetVelocityNode(uint id) : base(id, "SetVelocity", ScriptNodeCategory.Output)
        {
            AddInput("Exec", ScriptPinType.Exec);
            AddInput("Velocity", ScriptPinType.Float3);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string v = ctx.GetInputExpression(Inputs[1]);
            return $"    Particle.Velocity = {v};\n";
        }
        public override bool HasSideEffect => true;
    }

    /// <summary>写入粒子颜色。</summary>
    public class SetColorNode : ScriptNode
    {
        public SetColorNode(uint id) : base(id, "SetColor", ScriptNodeCategory.Output)
        {
            AddInput("Exec", ScriptPinType.Exec);
            AddInput("Color", ScriptPinType.Float4);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string v = ctx.GetInputExpression(Inputs[1]);
            return $"    Particle.Color = {v};\n";
        }
        public override bool HasSideEffect => true;
    }

    // ── 数学运算节点 ──

    /// <summary>二元数学运算（Add/Sub/Multiply/Divide）。</summary>
    public class MathBinaryNode : ScriptNode
    {
        public enum Op { Add, Subtract, Multiply, Divide }
        public Op Operation = Op.Add;

        public MathBinaryNode(uint id, Op op) : base(id, $"Math{op}", ScriptNodeCategory.Math)
        {
            Operation = op;
            AddInput("A", ScriptPinType.Float3);
            AddInput("B", ScriptPinType.Float3);
            AddOutput("Result", ScriptPinType.Float3);
        }

        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string a = ctx.GetInputExpression(Inputs[0]);
            string b = ctx.GetInputExpression(Inputs[1]);
            return Operation switch
            {
                Op.Add => $"({a} + {b})",
                Op.Subtract => $"({a} - {b})",
                Op.Multiply => $"({a} * {b})",
                Op.Divide => $"({a} / {b})",
                _ => a
            };
        }
    }

    /// <summary>线性插值节点 Lerp(A, B, Alpha)。</summary>
    public class LerpNode : ScriptNode
    {
        public LerpNode(uint id) : base(id, "Lerp", ScriptNodeCategory.Math)
        {
            AddInput("A", ScriptPinType.Float3);
            AddInput("B", ScriptPinType.Float3);
            AddInput("Alpha", ScriptPinType.Float);
            AddOutput("Result", ScriptPinType.Float3);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string a = ctx.GetInputExpression(Inputs[0]);
            string b = ctx.GetInputExpression(Inputs[1]);
            string t = ctx.GetInputExpression(Inputs[2]);
            return $"lerp({a}, {b}, {t})";
        }
    }

    /// <summary>标量数学节点（Sine/Cosine/Abs/Saturate/Length/Normalize）。</summary>
    public class MathUnaryNode : ScriptNode
    {
        public enum Op { Sine, Cosine, Abs, Saturate, Length, Normalize }
        public Op Operation;

        public MathUnaryNode(uint id, Op op) : base(id, $"Math{op}", ScriptNodeCategory.Math)
        {
            Operation = op;
            AddInput("X", ScriptPinType.Float3);
            AddOutput("Result", Operation <= Op.Saturate ? ScriptPinType.Float3 : ScriptPinType.Float);
        }

        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string x = ctx.GetInputExpression(Inputs[0]);
            return Operation switch
            {
                Op.Sine => $"sin({x})",
                Op.Cosine => $"cos({x})",
                Op.Abs => $"abs({x})",
                Op.Saturate => $"saturate({x})",
                Op.Length => $"length({x})",
                Op.Normalize => $"normalize({x})",
                _ => x
            };
        }
    }

    // ── 常量节点 ──

    /// <summary>浮点常量。</summary>
    public class FloatConstantNode : ScriptNode
    {
        public float Value = 1f;
        public FloatConstantNode(uint id, float v = 1f) : base(id, "FloatConst", ScriptNodeCategory.Constant)
        {
            Value = v;
            AddOutput("Value", ScriptPinType.Float);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
            => Value.ToString("R", CultureInfo.InvariantCulture) + "f";
    }

    /// <summary>Vector3 常量。</summary>
    public class Vector3ConstantNode : ScriptNode
    {
        public Vector3 Value = Vector3.One;
        public Vector3ConstantNode(uint id) : base(id, "Vector3Const", ScriptNodeCategory.Constant)
            => AddOutput("Value", ScriptPinType.Float3);
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            NumberFormatInfo f = CultureInfo.InvariantCulture.NumberFormat;
            return $"float3({Value.X.ToString("R", f)}, {Value.Y.ToString("R", f)}, {Value.Z.ToString("R", f)})";
        }
    }

    /// <summary>Vector4 常量。</summary>
    public class Vector4ConstantNode : ScriptNode
    {
        public Vector4 Value = new(1, 1, 1, 1);
        public Vector4ConstantNode(uint id) : base(id, "Vector4Const", ScriptNodeCategory.Constant)
            => AddOutput("Value", ScriptPinType.Float4);
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            NumberFormatInfo f = CultureInfo.InvariantCulture.NumberFormat;
            return $"float4({Value.X.ToString("R", f)}, {Value.Y.ToString("R", f)}, {Value.Z.ToString("R", f)}, {Value.W.ToString("R", f)})";
        }
    }

    // ── 参数节点（可编辑，编译期绑定到常量缓冲）──

    /// <summary>
    /// 参数节点。其值在运行时从常量缓冲读取，可在编辑器中实时调整。
    /// 编译器为每个参数分配一个常量缓冲槽位。
    /// </summary>
    public class ParameterNode : ScriptNode
    {
        public string ParameterName;
        public ScriptPinType ParameterType;
        public Vector4 DefaultValue = Vector4.One;

        public ParameterNode(uint id, string paramName, ScriptPinType type)
            : base(id, $"Param_{paramName}", ScriptNodeCategory.Parameter)
        {
            ParameterName = paramName;
            ParameterType = type;
            AddOutput("Value", type);
        }

        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            // 注册参数到编译上下文（分配常量缓冲偏移）
            int slot = ctx.RegisterParameter(ParameterName, ParameterType, DefaultValue);
            string hlslType = ParameterType.ToHLSL();
            return $"gParameters[{slot}].{ComponentAccess(ParameterType)}";
        }

        private static string ComponentAccess(ScriptPinType t) => t switch
        {
            ScriptPinType.Float => "x",
            ScriptPinType.Float2 => "xy",
            ScriptPinType.Float3 => "xyz",
            ScriptPinType.Float4 => "xyzw",
            _ => "x"
        };
    }

    // ── 采样节点 ──

    /// <summary>曲线采样节点。采样烘焙在缓冲中的参数曲线。</summary>
    public class CurveSampleNode : ScriptNode
    {
        public int CurveIndex;
        public CurveSampleNode(uint id, int curveIdx = 0) : base(id, "CurveSample", ScriptNodeCategory.Sampling)
        {
            CurveIndex = curveIdx;
            AddInput("Time", ScriptPinType.Float);
            AddOutput("Value", ScriptPinType.Float);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string t = ctx.GetInputExpression(Inputs[0]);
            return $"SampleCurve(gCurveBuffer, {CurveIndex}, {t})";
        }
    }

    /// <summary>随机范围节点。在 [Min, Max] 之间取基于种子的随机值。</summary>
    public class RandomRangeNode : ScriptNode
    {
        public RandomRangeNode(uint id) : base(id, "RandomRange", ScriptNodeCategory.Utility)
        {
            AddInput("Min", ScriptPinType.Float);
            AddInput("Max", ScriptPinType.Float);
            AddInput("Seed", ScriptPinType.Float);
            AddOutput("Value", ScriptPinType.Float);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string min = ctx.GetInputExpression(Inputs[0]);
            string max = ctx.GetInputExpression(Inputs[1]);
            string seed = ctx.GetInputExpression(Inputs[2]);
            return $"lerp({min}, {max}, Hash({seed}))";
        }
    }

    // ── 力施加节点 ──

    /// <summary>施加力节点。将力累加到粒子加速度。</summary>
    public class ApplyForceNode : ScriptNode
    {
        public ApplyForceNode(uint id) : base(id, "ApplyForce", ScriptNodeCategory.Force)
        {
            AddInput("Exec", ScriptPinType.Exec);
            AddInput("Force", ScriptPinType.Float3);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string f = ctx.GetInputExpression(Inputs[1]);
            return $"    Particle.Acceleration += {f};\n";
        }
        public override bool HasSideEffect => true;
    }

    /// <summary>施加重力节点。</summary>
    public class ApplyGravityNode : ScriptNode
    {
        public ApplyGravityNode(uint id) : base(id, "ApplyGravity", ScriptNodeCategory.Force)
        {
            AddInput("Exec", ScriptPinType.Exec);
            AddInput("Gravity", ScriptPinType.Float3);
        }
        public override string EmitExpression(ScriptPin outputPin, ScriptCompileContext ctx)
        {
            string g = ctx.GetInputExpression(Inputs[1]);
            return $"    Particle.Acceleration += {g} * gSim.DeltaTime;\n";
        }
        public override bool HasSideEffect => true;
    }

    // ──────────────────────────────────────────────────────────────────
    // 第四部分：编译上下文与参数绑定
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚本参数描述。记录参数在常量缓冲中的布局。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ScriptParameter
    {
        public string Name;
        public ScriptPinType Type;
        public Vector4 DefaultValue;
        public int BufferSlot;   // 在 gParameters[] 数组中的索引
        public int ByteOffset;   // 在常量缓冲中的字节偏移
    }

    /// <summary>
    /// 编译上下文。在编译过程中缓存已生成的表达式与参数布局。
    /// </summary>
    public class ScriptCompileContext
    {
        /// <summary>引脚 → 已生成表达式字符串缓存（避免重复计算）。</summary>
        private readonly Dictionary<ScriptPin, string> _exprCache = new();
        /// <summary>已注册参数列表。</summary>
        public readonly List<ScriptParameter> Parameters = new();
        private readonly Dictionary<string, int> _paramIndex = new();

        /// <summary>注册一个参数，返回其在常量缓冲数组中的槽位。</summary>
        public int RegisterParameter(string name, ScriptPinType type, Vector4 def)
        {
            if (_paramIndex.TryGetValue(name, out int idx)) return idx;
            int slot = Parameters.Count;
            Parameters.Add(new ScriptParameter
            {
                Name = name,
                Type = type,
                DefaultValue = def,
                BufferSlot = slot,
                ByteOffset = slot * 16
            });
            _paramIndex[name] = slot;
            return slot;
        }

        /// <summary>缓存输出引脚表达式。</summary>
        public void SetExpression(ScriptPin pin, string expr) => _exprCache[pin] = expr;

        /// <summary>获取输出引脚的已缓存表达式。</summary>
        public string GetExpression(ScriptPin pin) => _exprCache.TryGetValue(pin, out var e) ? e : "0";

        /// <summary>
        /// 获取输入引脚的表达式。若输入有连接则返回源输出表达式，
        /// 否则返回默认字面量。
        /// </summary>
        public string GetInputExpression(ScriptPin input)
        {
            if (input.ConnectedFrom != null)
                return GetExpression(input.ConnectedFrom);
            // 无连接：返回类型默认值
            return input.Type switch
            {
                ScriptPinType.Float => "0.0",
                ScriptPinType.Float2 => "float2(0,0)",
                ScriptPinType.Float3 => "float3(0,0,0)",
                ScriptPinType.Float4 => "float4(0,0,0,0)",
                _ => "0"
            };
        }
    }

    /// <summary>
    /// 运行时参数绑定。将脚本参数与 CPU 端值绑定，并上传到 GPU 常量缓冲。
    /// </summary>
    public class ScriptParameterBinding : IDisposable
    {
        private readonly List<ScriptParameter> _params;
        private readonly RHIBuffer? _paramCB;
        private readonly RHIDevice _device;
        /// <summary>参数值存储（每个参数一个 Vector4）。</summary>
        public Vector4[] Values;

        public ScriptParameterBinding(RHIDevice device, List<ScriptParameter> parameters)
        {
            _device = device;
            _params = parameters;
            Values = new Vector4[Math.Max(1, parameters.Count)];
            for (int i = 0; i < parameters.Count; i++)
                Values[i] = parameters[i].DefaultValue;

            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)Values.Length * 16u,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = 256
            };
            _paramCB = device.CreateBuffer(desc, "ScriptParamCB");
        }

        /// <summary>按名称设置参数值。</summary>
        public bool Set(string name, Vector4 value)
        {
            for (int i = 0; i < _params.Count; i++)
                if (_params[i].Name == name) { Values[i] = value; return true; }
            return false;
        }

        public bool Set(string name, float x) => Set(name, new Vector4(x, 0, 0, 0));
        public bool Set(string name, Vector3 v) => Set(name, new Vector4(v, 0));

        /// <summary>上传参数到 GPU 常量缓冲。</summary>
        public void Upload()
        {
            if (_paramCB != null && Values.Length > 0)
                _device.UploadToBuffer(_paramCB, Values);
        }

        public ulong GetGpuVirtualAddress() => _paramCB?.GpuVirtualAddress ?? 0;

        public void Dispose() => _paramCB?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────
    // 第五部分：脚本图与编译器
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子脚本图。由节点与引脚连接构成的有向无环图(DAG)。
    /// </summary>
    public class ParticleScriptGraph
    {
        public string Name = "ParticleScript";
        public readonly List<ScriptNode> Nodes = new();
        private uint _nextId = 1;

        /// <summary>添加节点并自动分配 Id。</summary>
        public T AddNode<T>(T node) where T : ScriptNode
        {
            if (node.Id == 0) node.Id = _nextId++;
            Nodes.Add(node);
            return node;
        }

        /// <summary>连接输出引脚到输入引脚。</summary>
        public void Connect(ScriptPin fromOutput, ScriptPin toInput)
        {
            if (!fromOutput.IsInput && toInput.IsInput)
            {
                // 断开输入引脚已有连接（单输入单连接）
                if (toInput.ConnectedFrom != null)
                    toInput.ConnectedFrom.ConnectedTo.Remove(toInput);
                fromOutput.ConnectedTo.Add(toInput);
                toInput.ConnectedFrom = fromOutput;
            }
        }

        /// <summary>获取所有副作用节点（编译入口）。</summary>
        public List<ScriptNode> GetSinkNodes()
        {
            var sinks = new List<ScriptNode>();
            foreach (var n in Nodes)
                if (n.HasSideEffect) sinks.Add(n);
            return sinks;
        }
    }

    /// <summary>
    /// 脚本编译器。将粒子脚本图编译为 HLSL 计算着色器，并创建 RHI 计算管线。
    /// 编译采用反向拓扑序：从副作用（输出/力）节点出发，递归求值每个输入引脚的依赖表达式。
    /// </summary>
    public class ParticleScriptCompiler
    {
        private readonly RHIDevice _device;
        public ScriptCompileContext Context { get; private set; } = new();

        public ParticleScriptCompiler(RHIDevice device) => _device = device;

        /// <summary>
        /// 编译脚本图为可执行的 GPU 计算管线。
        /// </summary>
        public CompiledParticleScript Compile(ParticleScriptGraph graph)
        {
            Context = new ScriptCompileContext();
            string hlsl = GenerateHLSL(graph);

            // 通过 RHI 编译着色器字节码
            var bytecode = _device.CompileShader(
                hlsl, "CSMain", RHIShaderStage.Compute, _device.MaxShaderModel);

            // 创建计算管线状态对象
            var pso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = $"Script_{graph.Name}",
                CS = bytecode
            });

            // 创建参数绑定
            var binding = new ScriptParameterBinding(_device, Context.Parameters);

            return new CompiledParticleScript
            {
                Name = graph.Name,
                PipelineState = pso,
                ParameterBinding = binding,
                GeneratedHLSL = hlsl,
                ParameterCount = Context.Parameters.Count
            };
        }

        /// <summary>生成完整的 HLSL 计算着色器源码。</summary>
        private string GenerateHLSL(ParticleScriptGraph graph)
        {
            var sb = new StringBuilder(4096);

            // 头部：结构体与缓冲声明
            sb.AppendLine("// ═══ 山雨引擎自动生成的粒子脚本着色器 ═══");
            sb.AppendLine("// 脚本名: " + graph.Name);
            sb.AppendLine("#include \"ParticleCommon.hlsli\"");
            sb.AppendLine();
            sb.AppendLine("cbuffer ScriptParamsCB : register(b2)");
            sb.AppendLine("{");
            sb.AppendLine("    float4 gParameters[64]; // 脚本参数数组");
            sb.AppendLine("};");
            sb.AppendLine("StructuredBuffer<float> gCurveBuffer : register(t3);");
            sb.AppendLine();

            // 哈希函数（用于随机）
            sb.AppendLine("float Hash(float n) { return frac(sin(n * 12.9898) * 43758.5453); }");
            sb.AppendLine("float SampleCurve(StructuredBuffer<float> buf, int idx, float t)");
            sb.AppendLine("{");
            sb.AppendLine("    uint n = 64; int i0 = (int)(t * (n-1)); int i1 = min(i0+1, (int)n-1);");
            sb.AppendLine("    float f = t * (n-1) - i0;");
            sb.AppendLine("    return lerp(buf[idx*n + i0], buf[idx*n + i1], f);");
            sb.AppendLine("}");
            sb.AppendLine();

            // 主计算着色器入口
            sb.AppendLine("[numthreads(64,1,1)]");
            sb.AppendLine("void CSMain(uint3 dtid : SV_DispatchThreadID)");
            sb.AppendLine("{");
            sb.AppendLine("    uint gi = dtid.x;");
            sb.AppendLine("    if (gi >= gSim.AliveCount) return;");
            sb.AppendLine("    uint idx = AliveList[gi];");
            sb.AppendLine("    Particle Particle = Particles[idx]; // 按值捕获，避免交叉污染");
            sb.AppendLine("    Particle.Acceleration = float3(0,0,0);");
            sb.AppendLine();

            // 生成副作用节点的语句（反向求值依赖链）
            var sinks = graph.GetSinkNodes();
            var visited = new HashSet<ScriptNode>();
            foreach (var sink in sinks)
            {
                EvaluateNode(sink, sb, visited);
            }

            // 尾部：积分并写回
            sb.AppendLine("    // 积分：速度 += 加速度*dt；位置 += 速度*dt");
            sb.AppendLine("    Particle.Velocity += Particle.Acceleration * gSim.DeltaTime;");
            sb.AppendLine("    Particle.Position += Particle.Velocity * gSim.DeltaTime;");
            sb.AppendLine("    Particle.Age += gSim.DeltaTime;");
            sb.AppendLine("    Particles[idx] = Particle;");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 递归求值节点：先求值其所有输入依赖，再生成自身语句/表达式。
        /// 采用记忆化避免重复生成。
        /// </summary>
        private void EvaluateNode(ScriptNode node, StringBuilder sb, HashSet<ScriptNode> visited)
        {
            if (visited.Contains(node)) return;
            visited.Add(node);

            // 先递归求值输入引脚连接的源节点
            foreach (var input in node.Inputs)
            {
                if (input.ConnectedFrom != null && input.ConnectedFrom.Owner != null)
                    EvaluateNode(input.ConnectedFrom.Owner, sb, visited);
            }

            // 为每个输出引脚生成并缓存表达式
            foreach (var output in node.Outputs)
            {
                if (output.Type == ScriptPinType.Exec) continue;
                string expr = node.EmitExpression(output, Context);
                Context.SetExpression(output, expr);
            }

            // 若节点有副作用，生成其语句
            if (node.HasSideEffect)
            {
                string stmt = node.EmitExpression(node.Outputs.Count > 0 ? node.Outputs[0] : node.Inputs[0], Context);
                if (!string.IsNullOrEmpty(stmt)) sb.Append(stmt);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第六部分：编译产物与运行时执行
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 编译后的粒子脚本。持有着色器管线与参数绑定，可在 GPU 上调度执行。
    /// </summary>
    public class CompiledParticleScript : IDisposable
    {
        public string Name = "";
        /// <summary>计算管线状态对象。</summary>
        public RHIPipelineState? PipelineState;
        /// <summary>运行时参数绑定。</summary>
        public ScriptParameterBinding? ParameterBinding;
        /// <summary>生成的 HLSL 源码（调试用）。</summary>
        public string GeneratedHLSL = "";
        /// <summary>参数数量。</summary>
        public int ParameterCount;
        /// <summary>每帧粒子组数（由粒子总数计算）。</summary>
        public int ThreadGroupSize = 64;

        /// <summary>
        /// 在命令列表上调度执行脚本。
        /// </summary>
        /// <param name="cmd">计算命令列表</param>
        /// <param name="rootSig">粒子根签名（需与脚本缓冲布局一致）</param>
        /// <param name="aliveCount">存活粒子数</param>
        public void Dispatch(RHICommandList cmd, RHIRootSignature rootSig, int aliveCount)
        {
            if (PipelineState == null) return;

            // 上传参数到 GPU
            ParameterBinding?.Upload();

            cmd.SetComputeRootSignature(rootSig);
            cmd.SetPipelineState(PipelineState);

            // 绑定脚本参数常量缓冲到 b2 槽
            if (ParameterBinding != null)
                cmd.SetComputeRootConstantBufferView(2, ParameterBinding.GetGpuVirtualAddress());

            // 计算调度组数
            int groups = Math.Max(1, (aliveCount + ThreadGroupSize - 1) / ThreadGroupSize);
            cmd.Dispatch(groups, 1, 1);
        }

        /// <summary>设置参数值（便捷方法）。</summary>
        public void SetParameter(string name, Vector4 value) => ParameterBinding?.Set(name, value);
        public void SetParameter(string name, float x) => ParameterBinding?.Set(name, x);
        public void SetParameter(string name, Vector3 v) => ParameterBinding?.Set(name, v);

        public void Dispose()
        {
            PipelineState?.Dispose();
            ParameterBinding?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第七部分：脚本图构建器（便捷 API）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚本图构建器。提供链式 API 快速搭建常见粒子行为脚本。
    /// </summary>
    public static class ScriptGraphBuilder
    {
        /// <summary>
        /// 构建一个“重力 + Curl Noise 湍流 + 颜色随年龄渐变”的标准火焰粒子脚本。
        /// </summary>
        public static ParticleScriptGraph BuildFireScript()
        {
            var g = new ParticleScriptGraph { Name = "FireScript" };

            var onUpdate = g.AddNode(new OnUpdateNode(1));
            var getPos = g.AddNode(new GetPositionNode(2));
            var getAge = g.AddNode(new GetAgeNode(3));

            // 重力参数（向上飘升）
            var gravity = g.AddNode(new ParameterNode(4, "UpwardForce", ScriptPinType.Float3));
            gravity.DefaultValue = new Vector4(0, 3f, 0, 0);
            var applyGravity = g.AddNode(new ApplyGravityNode(5));

            // Curl Noise 湍流（基于位置的力）
            var curlStrength = g.AddNode(new ParameterNode(6, "CurlStrength", ScriptPinType.Float));
            curlStrength.DefaultValue = new Vector4(2f, 0, 0, 0);
            var sinPos = g.AddNode(new MathUnaryNode(7, MathUnaryNode.Op.Sine));
            var applyForce = g.AddNode(new ApplyForceNode(8));

            // 颜色随年龄从橙到红渐变
            var colorStart = g.AddNode(new Vector4ConstantNode(9) { Value = new Vector4(1, 0.6f, 0.1f, 1) });
            var colorEnd = g.AddNode(new Vector4ConstantNode(10) { Value = new Vector4(0.8f, 0.1f, 0, 0) });
            var lerpColor = g.AddNode(new LerpNode(11));
            var setColor = g.AddNode(new SetColorNode(12));

            // 连接图
            g.Connect(onUpdate.Outputs[0], applyGravity.Inputs[0]);
            g.Connect(gravity.Outputs[0], applyGravity.Inputs[1]);

            g.Connect(getPos.Outputs[0], sinPos.Inputs[0]);
            g.Connect(sinPos.Outputs[0], applyForce.Inputs[1]);
            g.Connect(curlStrength.Outputs[0], applyForce.Inputs[1]); // 叠加（简化）

            g.Connect(colorStart.Outputs[0], lerpColor.Inputs[0]);
            g.Connect(colorEnd.Outputs[0], lerpColor.Inputs[1]);
            g.Connect(getAge.Outputs[0], lerpColor.Inputs[2]);
            g.Connect(lerpColor.Outputs[0], setColor.Inputs[1]);

            return g;
        }

        /// <summary>构建一个简单重力下落 + 死亡时爆裂子粒子的脚本。</summary>
        public static ParticleScriptGraph BuildGravityScript()
        {
            var g = new ParticleScriptGraph { Name = "GravityScript" };
            var onUpdate = g.AddNode(new OnUpdateNode(1));
            var gravityParam = g.AddNode(new ParameterNode(2, "Gravity", ScriptPinType.Float3));
            gravityParam.DefaultValue = new Vector4(0, -9.81f, 0, 0);
            var applyGravity = g.AddNode(new ApplyGravityNode(3));
            g.Connect(onUpdate.Outputs[0], applyGravity.Inputs[0]);
            g.Connect(gravityParam.Outputs[0], applyGravity.Inputs[1]);
            return g;
        }
    }
}
