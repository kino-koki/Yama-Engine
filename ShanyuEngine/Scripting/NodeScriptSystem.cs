using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Scripting
{
    // ====================================================================
    //  ShanyuEngine NodeScript Visual Scripting System (UE6-Level)
    //  ====================================================================
    //  Complete node-based visual scripting system aligned with Unreal Engine NodeScript:
    //    - Pin (ExecPin/DataPin) -> Node -> Edge -> Graph
    //    - Execution flow push (Exec pins) + Data flow pull (Pull-based pure data nodes)
    //    - NodeScriptCompiler: type checking / constant folding / dead code elimination / bytecode generation
    //    - NodeScriptBytecodeCompiler: compiles node graph to instruction sequences
    //    - NodeScriptVM: interprets bytecode instructions
    //    - NodeScriptInterpreter: direct node graph interpretation for rapid iteration
    //    - NodeScriptComponent / NodeScriptExecutionSystem: ECS integration
    //    - NodeScriptRegistry: naming / lookup / JSON persistence
    //  Author: ShanyuEngine Team
    // ====================================================================

    // ─────────────────────────────────────────────────────────────────────
    //  Part 1: Enums, Logging, Type Utilities
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>NodeScript pin data types. Exec = execution flow pin (white).</summary>
    public enum NodeScriptDataType
    {
        Exec, Bool, Int, Float, String, Vector3, Quaternion, Entity, Component, Object,
        Enum, Struct, Name
    }

    /// <summary>Pin direction: Input (left) or Output (right).</summary>
    public enum PinDirection { Input, Output }

    /// <summary>Node category for editor grouping and color coding.</summary>
    public enum NodeCategory
    {
        Event, FlowControl, Function, Variable, Math, Logic, Vector,
        Cast, Entity, Debug, Comment, Timeline, Delay
    }

    /// <summary>NodeScript log hub. All debug output routes through here.</summary>
    public static class NodeScriptLog
    {
        public static Action<string>? OnLog;
        public static void Log(string message) { OnLog?.Invoke(message); Console.WriteLine("[NodeScript] " + message); }
    }

    /// <summary>Type utilities shared by compiler and interpreter.</summary>
    public static class NodeScriptTypeUtils
    {
        public static object? GetDefault(NodeScriptDataType type) => type switch
        {
            NodeScriptDataType.Exec => null,
            NodeScriptDataType.Bool => false,
            NodeScriptDataType.Int => 0,
            NodeScriptDataType.Float => 0f,
            NodeScriptDataType.String => string.Empty,
            NodeScriptDataType.Vector3 => Vector3.Zero,
            NodeScriptDataType.Quaternion => Quaternion.Identity,
            NodeScriptDataType.Entity => 0,
            NodeScriptDataType.Component => null,
            NodeScriptDataType.Object => null,
            NodeScriptDataType.Enum => 0,
            NodeScriptDataType.Struct => null,
            NodeScriptDataType.Name => string.Empty,
            _ => null
        };

        public static Type GetClrType(NodeScriptDataType type) => type switch
        {
            NodeScriptDataType.Exec => typeof(void),
            NodeScriptDataType.Bool => typeof(bool),
            NodeScriptDataType.Int => typeof(int),
            NodeScriptDataType.Float => typeof(float),
            NodeScriptDataType.String => typeof(string),
            NodeScriptDataType.Vector3 => typeof(Vector3),
            NodeScriptDataType.Quaternion => typeof(Quaternion),
            NodeScriptDataType.Entity => typeof(int),
            NodeScriptDataType.Component => typeof(object),
            NodeScriptDataType.Object => typeof(object),
            NodeScriptDataType.Enum => typeof(int),
            NodeScriptDataType.Struct => typeof(object),
            NodeScriptDataType.Name => typeof(string),
            _ => typeof(object)
        };

        public static bool AreCompatible(NodeScriptDataType from, NodeScriptDataType to)
        {
            if (from == to) return true;
            if (from == NodeScriptDataType.Exec || to == NodeScriptDataType.Exec) return false;
            if (to == NodeScriptDataType.Object || from == NodeScriptDataType.Object) return true;
            if (from == NodeScriptDataType.Component || to == NodeScriptDataType.Component) return true;
            if (to == NodeScriptDataType.Name && from == NodeScriptDataType.String) return true;
            if (from == NodeScriptDataType.Name && to == NodeScriptDataType.String) return true;
            bool IsNumeric(NodeScriptDataType t) =>
                t == NodeScriptDataType.Int || t == NodeScriptDataType.Float ||
                t == NodeScriptDataType.Bool || t == NodeScriptDataType.Entity || t == NodeScriptDataType.Enum;
            if (IsNumeric(from) && IsNumeric(to)) return true;
            return false;
        }

        public static bool IsNarrowing(NodeScriptDataType from, NodeScriptDataType to)
        {
            if (from == to) return false;
            if (from == NodeScriptDataType.Float && to == NodeScriptDataType.Int) return true;
            if (from == NodeScriptDataType.Float && to == NodeScriptDataType.Bool) return true;
            if (from == NodeScriptDataType.Int && to == NodeScriptDataType.Bool) return true;
            if (from == NodeScriptDataType.Entity && to == NodeScriptDataType.Bool) return true;
            return false;
        }

        public static object? Convert(object? value, NodeScriptDataType target)
        {
            if (value == null) return GetDefault(target);
            if (target == NodeScriptDataType.Object) return value;
            if (target == NodeScriptDataType.Name) return Convert(value, NodeScriptDataType.String);
            try
            {
                return target switch
                {
                    NodeScriptDataType.Bool => value switch { bool b => b, int i => i != 0, float f => f != 0f, string s => bool.TryParse(s, out var bv) && bv, _ => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture) },
                    NodeScriptDataType.Int => value switch { int i => i, float f => (int)f, bool b => b ? 1 : 0, string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv) ? iv : 0, _ => System.Convert.ToInt32(value, CultureInfo.InvariantCulture) },
                    NodeScriptDataType.Float => value switch { float f => f, int i => (float)i, bool b => b ? 1f : 0f, string s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv) ? fv : 0f, _ => System.Convert.ToSingle(value, CultureInfo.InvariantCulture) },
                    NodeScriptDataType.String => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    NodeScriptDataType.Vector3 => value switch { Vector3 v => v, string s => ParseVector3(s), _ => Vector3.Zero },
                    NodeScriptDataType.Quaternion => value switch { Quaternion q => q, _ => Quaternion.Identity },
                    NodeScriptDataType.Entity => value switch { int i => i, float f => (int)f, _ => System.Convert.ToInt32(value, CultureInfo.InvariantCulture) },
                    NodeScriptDataType.Component => value is IComponent ? value : null,
                    NodeScriptDataType.Enum => value switch { int i => i, float f => (int)f, string s => int.TryParse(s, out var ev) ? ev : 0, _ => 0 },
                    _ => value
                };
            }
            catch { return GetDefault(target); }
        }

        public static object? Convert(object? value, Type targetType)
        {
            if (value == null) return null;
            if (targetType == typeof(bool)) return Convert(value, NodeScriptDataType.Bool);
            if (targetType == typeof(int)) return Convert(value, NodeScriptDataType.Int);
            if (targetType == typeof(float)) return Convert(value, NodeScriptDataType.Float);
            if (targetType == typeof(string)) return Convert(value, NodeScriptDataType.String);
            if (targetType == typeof(Vector3)) return Convert(value, NodeScriptDataType.Vector3);
            if (targetType == typeof(Quaternion)) return Convert(value, NodeScriptDataType.Quaternion);
            return value;
        }

        public static Vector3 ParseVector3(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Vector3.Zero;
            var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return Vector3.Zero;
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x);
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y);
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z);
            return new Vector3(x, y, z);
        }

        public static string FormatValue(object? value) => value switch
        {
            null => "null", bool b => b ? "true" : "false",
            Vector3 v => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})",
            Quaternion q => $"({q.X:0.###}, {q.Y:0.###}, {q.Z:0.###}, {q.W:0.###})",
            float f => f.ToString("0.######", CultureInfo.InvariantCulture),
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        public static Type? ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var t = Type.GetType(typeName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(typeName);
                if (t != null) return t;
                foreach (var ty in SafeGetTypes(asm))
                    if (ty.Name == typeName || ty.FullName == typeName) return ty;
            }
            return null;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
            catch { return Array.Empty<Type>(); }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 2: Pins and Edges
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>NodeScript pin (connection point on a node).</summary>
    public sealed class NodeScriptPin
    {
        public Guid Id = Guid.NewGuid();
        public string Name = string.Empty;
        public PinDirection Direction;
        public NodeScriptDataType DataType;
        public Guid OwnerNodeId;
        public object? DefaultValue;
        public string? Tooltip;
        public string? SubType; // For Enum/Struct: type name
        public bool IsExec => DataType == NodeScriptDataType.Exec;
        public NodeScriptPin Clone() => new() { Id = Guid.NewGuid(), Name = Name, Direction = Direction, DataType = DataType, OwnerNodeId = OwnerNodeId, DefaultValue = DefaultValue, Tooltip = Tooltip, SubType = SubType };
    }

    /// <summary>NodeScript edge (connection between two pins).</summary>
    public sealed class NodeScriptEdge
    {
        public Guid Id = Guid.NewGuid();
        public Guid FromPinId;
        public Guid ToPinId;
        public NodeScriptDataType ResolvedType = NodeScriptDataType.Object;
        public bool IsExec => ResolvedType == NodeScriptDataType.Exec;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 3: NodeScript Variables and Functions
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>NodeScript variable descriptor. Named storage slot with type and default value.</summary>
    public sealed class NodeScriptVariable
    {
        public Guid Id = Guid.NewGuid();
        public string Name = string.Empty;
        public NodeScriptDataType DataType = NodeScriptDataType.Float;
        public string? DefaultValueText;
        public bool IsEditable = true;
        public bool IsInstanced = true;
        public string Category = string.Empty;
        public string? Description;
        public bool IsArray; // Array variable support

        public object? GetDefaultValue()
        {
            if (string.IsNullOrEmpty(DefaultValueText))
                return NodeScriptTypeUtils.GetDefault(DataType);
            return DataType switch
            {
                NodeScriptDataType.Bool => bool.TryParse(DefaultValueText, out var b) ? b : false,
                NodeScriptDataType.Int => int.TryParse(DefaultValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0,
                NodeScriptDataType.Float => float.TryParse(DefaultValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f,
                NodeScriptDataType.String => DefaultValueText,
                NodeScriptDataType.Vector3 => NodeScriptTypeUtils.ParseVector3(DefaultValueText),
                NodeScriptDataType.Quaternion => Quaternion.Identity,
                NodeScriptDataType.Entity => int.TryParse(DefaultValueText, out var e) ? e : 0,
                NodeScriptDataType.Enum => int.TryParse(DefaultValueText, out var ev) ? ev : 0,
                _ => NodeScriptTypeUtils.GetDefault(DataType)
            };
        }

        public NodeScriptVariable Clone() => new() { Id = Guid.NewGuid(), Name = Name, DataType = DataType, DefaultValueText = DefaultValueText, IsEditable = IsEditable, IsInstanced = IsInstanced, Category = Category, Description = Description, IsArray = IsArray };
    }

    /// <summary>NodeScript function: named callable unit with parameters, outputs, locals, and a graph.</summary>
    public sealed class NodeScriptFunction
    {
        public string Name = string.Empty;
        public List<NodeScriptVariable> Parameters = new();
        public List<NodeScriptVariable> Outputs = new();
        public List<NodeScriptVariable> LocalVariables = new();
        public NodeScriptGraph Graph = new();
        public string? Description;
        public bool IsPure;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 4: Execution Context
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Execution context for one event trigger. Holds all runtime state.</summary>
    public sealed class NodeScriptExecutionContext
    {
        public NodeScriptGraph Graph = null!;
        public NodeScriptInterpreter Interpreter = null!;
        public EntityComponentSystem Ecs = null!;
        public int SelfEntity;
        public float DeltaTime;
        public Dictionary<string, object?> Variables = new();
        public Dictionary<int, Dictionary<string, object?>> Attributes = new();
        public Dictionary<Guid, object?> PinCache = new();
        public HashSet<Guid> Evaluating = new();
        public Dictionary<string, object?> EventArguments = new();
        public int StepsUsed;
        public int MaxExecutionSteps = 1_000_000;
        public bool ShouldBreak;
        public bool IsStepLimitExceeded => StepsUsed >= MaxExecutionSteps;

        // Global variables (cross-instance shared)
        public Dictionary<string, object?>? GlobalVariables;

        public void ClearPureCaches()
        {
            if (PinCache.Count == 0) return;
            var stale = new List<Guid>();
            foreach (var pinId in PinCache.Keys)
            {
                var node = Graph.FindNodeByPin(pinId);
                if (node == null || node.IsPure) stale.Add(pinId);
            }
            foreach (var k in stale) PinCache.Remove(k);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 5: Node Base Class and Concrete Nodes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>NodeScript node base class. All nodes have input/output pins, category, position, metadata.</summary>
    public abstract class NodeScriptNode
    {
        public Guid Id = Guid.NewGuid();
        public string Title = string.Empty;
        public NodeCategory Category = NodeCategory.Function;
        public float PositionX;
        public float PositionY;
        public List<NodeScriptPin> InputPins = new();
        public List<NodeScriptPin> OutputPins = new();
        public Dictionary<string, string> Metadata = new();
        public virtual bool HasExecPins => InputPins.Any(p => p.IsExec) || OutputPins.Any(p => p.IsExec);
        public virtual bool IsPure => !HasExecPins && Category != NodeCategory.Event;

        protected NodeScriptPin AddInput(string name, NodeScriptDataType dt, object? defaultValue = null)
        {
            var pin = new NodeScriptPin { Name = name, Direction = PinDirection.Input, DataType = dt, OwnerNodeId = Id, DefaultValue = defaultValue ?? NodeScriptTypeUtils.GetDefault(dt) };
            InputPins.Add(pin);
            return pin;
        }

        protected NodeScriptPin AddOutput(string name, NodeScriptDataType dt, object? defaultValue = null)
        {
            var pin = new NodeScriptPin { Name = name, Direction = PinDirection.Output, DataType = dt, OwnerNodeId = Id, DefaultValue = defaultValue ?? NodeScriptTypeUtils.GetDefault(dt) };
            OutputPins.Add(pin);
            return pin;
        }

        public NodeScriptPin? FindInput(string name) => InputPins.FirstOrDefault(p => p.Name == name);
        public NodeScriptPin? FindOutput(string name) => OutputPins.FirstOrDefault(p => p.Name == name);
        public NodeScriptPin? FindPin(Guid pinId) => InputPins.FirstOrDefault(p => p.Id == pinId) ?? OutputPins.FirstOrDefault(p => p.Id == pinId);

        /// <summary>Execution flow nodes override to produce side effects and return next exec pin indices.</summary>
        public virtual List<int> Execute(NodeScriptExecutionContext ctx) => new();

        /// <summary>Pure data nodes override to compute outputs from inputs.</summary>
        public virtual void EvaluateOutputs(NodeScriptExecutionContext ctx) { }

        protected object? ReadInput(NodeScriptExecutionContext ctx, string name)
        {
            var pin = FindInput(name);
            if (pin == null) return null;
            return ctx.Interpreter.ReadInputPin(ctx, this, pin);
        }

        protected T ReadInputAs<T>(NodeScriptExecutionContext ctx, string name)
        {
            var v = ReadInput(ctx, name);
            if (v == null) return default!;
            if (v is T t) return t;
            return (T)NodeScriptTypeUtils.Convert(v, typeof(T))!;
        }

        protected void WriteOutput(NodeScriptExecutionContext ctx, string name, object? value)
        {
            var pin = FindOutput(name);
            if (pin != null) ctx.PinCache[pin.Id] = value;
        }

        protected int ExecOutIndex(string name)
        {
            for (int i = 0; i < OutputPins.Count; i++)
                if (OutputPins[i].IsExec && OutputPins[i].Name == name) return i;
            return -1;
        }

        public abstract NodeScriptNode Clone();
        protected void CloneInto(NodeScriptNode target)
        {
            target.Id = Guid.NewGuid();
            target.Title = Title; target.Category = Category;
            target.PositionX = PositionX; target.PositionY = PositionY;
            target.Metadata = new Dictionary<string, string>(Metadata);
            target.InputPins = InputPins.Select(p => p.Clone()).ToList();
            target.OutputPins = OutputPins.Select(p => p.Clone()).ToList();
            foreach (var p in target.InputPins) p.OwnerNodeId = target.Id;
            foreach (var p in target.OutputPins) p.OwnerNodeId = target.Id;
        }
    }

    // ── 5.1 Variable/Attribute Nodes ──────────────────────────────────

    public sealed class GetVariableNode : NodeScriptNode
    {
        public GetVariableNode() { Title = "Get Variable"; Category = NodeCategory.Variable; AddOutput("Value", NodeScriptDataType.Float); }
        public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { var name = Metadata.GetValueOrDefault("VariableName", string.Empty); if (ctx.GlobalVariables != null && ctx.GlobalVariables.TryGetValue(name, out var gv)) WriteOutput(ctx, "Value", gv); else ctx.Variables.TryGetValue(name, out var v); WriteOutput(ctx, "Value", v ?? ctx.Variables.GetValueOrDefault(name)); }
        public override NodeScriptNode Clone() { var n = new GetVariableNode(); CloneInto(n); return n; }
    }

    public sealed class SetVariableNode : NodeScriptNode
    {
        public SetVariableNode() { Title = "Set Variable"; Category = NodeCategory.Variable; AddInput("In", NodeScriptDataType.Exec); AddInput("Value", NodeScriptDataType.Float); AddOutput("Out", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { var name = Metadata.GetValueOrDefault("VariableName", string.Empty); ctx.Variables[name] = ReadInput(ctx, "Value"); return new List<int> { ExecOutIndex("Out") }; }
        public override NodeScriptNode Clone() { var n = new SetVariableNode(); CloneInto(n); return n; }
    }

    public sealed class GetGlobalVariableNode : NodeScriptNode
    {
        public GetGlobalVariableNode() { Title = "Get Global Variable"; Category = NodeCategory.Variable; AddOutput("Value", NodeScriptDataType.Object); }
        public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { var name = Metadata.GetValueOrDefault("VariableName", string.Empty); WriteOutput(ctx, "Value", ctx.GlobalVariables?.GetValueOrDefault(name)); }
        public override NodeScriptNode Clone() { var n = new GetGlobalVariableNode(); CloneInto(n); return n; }
    }

    public sealed class SetGlobalVariableNode : NodeScriptNode
    {
        public SetGlobalVariableNode() { Title = "Set Global Variable"; Category = NodeCategory.Variable; AddInput("In", NodeScriptDataType.Exec); AddInput("Value", NodeScriptDataType.Object); AddOutput("Out", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { var name = Metadata.GetValueOrDefault("VariableName", string.Empty); if (ctx.GlobalVariables != null) ctx.GlobalVariables[name] = ReadInput(ctx, "Value"); return new List<int> { ExecOutIndex("Out") }; }
        public override NodeScriptNode Clone() { var n = new SetGlobalVariableNode(); CloneInto(n); return n; }
    }

    public sealed class GetAttributeNode : NodeScriptNode
    {
        public GetAttributeNode() { Title = "Get Attribute"; Category = NodeCategory.Variable; AddInput("Entity", NodeScriptDataType.Entity); AddOutput("Value", NodeScriptDataType.Object); }
        public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { int entity = ReadInputAs<int>(ctx, "Entity"); var attr = Metadata.GetValueOrDefault("AttributeName", string.Empty); object? v = null; if (ctx.Attributes.TryGetValue(entity, out var map) && map.TryGetValue(attr, out var obj)) v = obj; WriteOutput(ctx, "Value", v); }
        public override NodeScriptNode Clone() { var n = new GetAttributeNode(); CloneInto(n); return n; }
    }

    public sealed class SetAttributeNode : NodeScriptNode
    {
        public SetAttributeNode() { Title = "Set Attribute"; Category = NodeCategory.Variable; AddInput("In", NodeScriptDataType.Exec); AddInput("Entity", NodeScriptDataType.Entity); AddInput("Value", NodeScriptDataType.Object); AddOutput("Out", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { int entity = ReadInputAs<int>(ctx, "Entity"); var attr = Metadata.GetValueOrDefault("AttributeName", string.Empty); var value = ReadInput(ctx, "Value"); if (!ctx.Attributes.TryGetValue(entity, out var map)) ctx.Attributes[entity] = map = new Dictionary<string, object?>(); map[attr] = value; return new List<int> { ExecOutIndex("Out") }; }
        public override NodeScriptNode Clone() { var n = new SetAttributeNode(); CloneInto(n); return n; }
    }

    // ── 5.2 Flow Control Nodes ──────────────────────────────────────

    public sealed class SequenceNode : NodeScriptNode
    {
        public SequenceNode() : this(3) { }
        public SequenceNode(int outputs = 3) { Title = "Sequence"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); for (int i = 0; i < outputs; i++) AddOutput("Then" + i, NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { var list = new List<int>(); for (int i = 0; i < OutputPins.Count; i++) if (OutputPins[i].IsExec) list.Add(i); return list; }
        public override NodeScriptNode Clone() { var n = new SequenceNode(OutputPins.Count(p => p.IsExec)); CloneInto(n); return n; }
    }

    public sealed class IfNode : NodeScriptNode
    {
        public IfNode() { Title = "If (Branch)"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); AddInput("Condition", NodeScriptDataType.Bool); AddOutput("True", NodeScriptDataType.Exec); AddOutput("False", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { bool cond = ReadInputAs<bool>(ctx, "Condition"); return new List<int> { cond ? ExecOutIndex("True") : ExecOutIndex("False") }; }
        public override NodeScriptNode Clone() { var n = new IfNode(); CloneInto(n); return n; }
    }

    public sealed class WhileNode : NodeScriptNode
    {
        public WhileNode() { Title = "While Loop"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); AddInput("Condition", NodeScriptDataType.Bool); AddOutput("LoopBody", NodeScriptDataType.Exec); AddOutput("Completed", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { int guard = 0; int bodyIdx = ExecOutIndex("LoopBody"); while (ReadInputAs<bool>(ctx, "Condition") && guard++ < 1_000_000) { ctx.ClearPureCaches(); ctx.Interpreter.RunOutputChain(ctx, this, bodyIdx); if (ctx.IsStepLimitExceeded) break; } return new List<int> { ExecOutIndex("Completed") }; }
        public override NodeScriptNode Clone() { var n = new WhileNode(); CloneInto(n); return n; }
    }

    public sealed class ForNode : NodeScriptNode
    {
        public ForNode() { Title = "For Loop"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); AddInput("Start", NodeScriptDataType.Int, 0); AddInput("End", NodeScriptDataType.Int, 10); AddOutput("Index", NodeScriptDataType.Int); AddOutput("LoopBody", NodeScriptDataType.Exec); AddOutput("Completed", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { int start = ReadInputAs<int>(ctx, "Start"); int end = ReadInputAs<int>(ctx, "End"); int bodyIdx = ExecOutIndex("LoopBody"); for (int i = start; i < end; i++) { WriteOutput(ctx, "Index", i); ctx.ClearPureCaches(); ctx.Interpreter.RunOutputChain(ctx, this, bodyIdx); if (ctx.IsStepLimitExceeded) break; } return new List<int> { ExecOutIndex("Completed") }; }
        public override NodeScriptNode Clone() { var n = new ForNode(); CloneInto(n); return n; }
    }

    public sealed class ForEachNode : NodeScriptNode
    {
        public ForEachNode() { Title = "For Each Loop"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); AddInput("Array", NodeScriptDataType.String); AddOutput("Item", NodeScriptDataType.String); AddOutput("Index", NodeScriptDataType.Int); AddOutput("LoopBody", NodeScriptDataType.Exec); AddOutput("Completed", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { var arr = ReadInputAs<string>(ctx, "Array") ?? string.Empty; int bodyIdx = ExecOutIndex("LoopBody"); int idx = 0; foreach (var item in arr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { WriteOutput(ctx, "Item", item); WriteOutput(ctx, "Index", idx++); ctx.ClearPureCaches(); ctx.Interpreter.RunOutputChain(ctx, this, bodyIdx); if (ctx.IsStepLimitExceeded) break; } return new List<int> { ExecOutIndex("Completed") }; }
        public override NodeScriptNode Clone() { var n = new ForEachNode(); CloneInto(n); return n; }
    }

    public sealed class SwitchNode : NodeScriptNode
    {
        public SwitchNode() : this(3) { }
        public SwitchNode(int cases = 3) { Title = "Switch"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); AddInput("Value", NodeScriptDataType.Int); for (int i = 0; i < cases; i++) AddOutput("Case" + i, NodeScriptDataType.Exec); AddOutput("Default", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { int value = ReadInputAs<int>(ctx, "Value"); int target = -1; for (int i = 0; i < OutputPins.Count; i++) { if (!OutputPins[i].IsExec) continue; if (OutputPins[i].Name == "Case" + value) { target = i; break; } } return new List<int> { target >= 0 ? target : ExecOutIndex("Default") }; }
        public override NodeScriptNode Clone() { var n = new SwitchNode(OutputPins.Count(p => p.IsExec && p.Name != "Default")); CloneInto(n); return n; }
    }

    public sealed class DoOnceNode : NodeScriptNode
    {
        private bool _executed;
        public DoOnceNode() { Title = "Do Once"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); AddInput("Reset", NodeScriptDataType.Exec); AddOutput("Out", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { if (_executed) return new(); _executed = true; return new List<int> { ExecOutIndex("Out") }; }
        public override NodeScriptNode Clone() { var n = new DoOnceNode(); CloneInto(n); return n; }
    }

    public sealed class FlipFlopNode : NodeScriptNode
    {
        private bool _isA = true;
        public FlipFlopNode() { Title = "Flip Flop"; Category = NodeCategory.FlowControl; AddInput("In", NodeScriptDataType.Exec); AddOutput("A", NodeScriptDataType.Exec); AddOutput("B", NodeScriptDataType.Exec); AddOutput("IsA", NodeScriptDataType.Bool); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { _isA = !_isA; WriteOutput(ctx, "IsA", _isA); return new List<int> { _isA ? ExecOutIndex("A") : ExecOutIndex("B") }; }
        public override NodeScriptNode Clone() { var n = new FlipFlopNode(); CloneInto(n); return n; }
    }

    // ── 5.3 Event Nodes (Execution Entrypoints) ──────────────────────

    public abstract class EventNodeBase : NodeScriptNode
    {
        public override bool IsPure => false;
        public EventNodeBase() { Category = NodeCategory.Event; AddOutput("Then", NodeScriptDataType.Exec); }
        public string EventName { get => Metadata.GetValueOrDefault("EventName", string.Empty); set => Metadata["EventName"] = value; }
        public override List<int> Execute(NodeScriptExecutionContext ctx) => new() { ExecOutIndex("Then") };
    }

    public sealed class OnBeginNode : EventNodeBase { public OnBeginNode() { Title = "BeginPlay"; EventName = "OnBegin"; } public override NodeScriptNode Clone() { var n = new OnBeginNode(); CloneInto(n); return n; } }
    public sealed class OnTickNode : EventNodeBase { public OnTickNode() { Title = "Tick"; EventName = "OnTick"; AddOutput("Delta Time", NodeScriptDataType.Float); } public override List<int> Execute(NodeScriptExecutionContext ctx) { WriteOutput(ctx, "Delta Time", ctx.DeltaTime); return base.Execute(ctx); } public override NodeScriptNode Clone() { var n = new OnTickNode(); CloneInto(n); return n; } }
    public sealed class OnCollisionNode : EventNodeBase { public OnCollisionNode() { Title = "On Collision"; EventName = "OnCollision"; AddOutput("Other", NodeScriptDataType.Entity); } public override List<int> Execute(NodeScriptExecutionContext ctx) { ctx.EventArguments.TryGetValue("Other", out var other); WriteOutput(ctx, "Other", other is int i ? i : 0); return base.Execute(ctx); } public override NodeScriptNode Clone() { var n = new OnCollisionNode(); CloneInto(n); return n; } }
    public sealed class OnTriggerNode : EventNodeBase { public OnTriggerNode() { Title = "On Trigger"; EventName = "OnTrigger"; AddOutput("Other", NodeScriptDataType.Entity); } public override List<int> Execute(NodeScriptExecutionContext ctx) { ctx.EventArguments.TryGetValue("Other", out var other); WriteOutput(ctx, "Other", other is int i ? i : 0); return base.Execute(ctx); } public override NodeScriptNode Clone() { var n = new OnTriggerNode(); CloneInto(n); return n; } }
    public sealed class OnSpawnNode : EventNodeBase { public OnSpawnNode() { Title = "On Spawn"; EventName = "OnSpawn"; } public override NodeScriptNode Clone() { var n = new OnSpawnNode(); CloneInto(n); return n; } }
    public sealed class OnDestroyNode : EventNodeBase { public OnDestroyNode() { Title = "On Destroy"; EventName = "OnDestroy"; } public override NodeScriptNode Clone() { var n = new OnDestroyNode(); CloneInto(n); return n; } }
    public sealed class CustomEventNode : EventNodeBase { public CustomEventNode() : this("CustomEvent") { } public CustomEventNode(string eventName = "CustomEvent") { Title = "Custom Event"; EventName = eventName; AddOutput("Arg0", NodeScriptDataType.Object); } public override List<int> Execute(NodeScriptExecutionContext ctx) { ctx.EventArguments.TryGetValue("Arg0", out var arg); WriteOutput(ctx, "Arg0", arg); return base.Execute(ctx); } public override NodeScriptNode Clone() { var n = new CustomEventNode(EventName); CloneInto(n); return n; } }

    // ── 5.4 Function Call Nodes ───────────────────────────────────────

    public sealed class CallFunctionNode : NodeScriptNode
    {
        public CallFunctionNode() { Title = "Call Function"; Category = NodeCategory.Function; AddInput("In", NodeScriptDataType.Exec); AddInput("Arg0", NodeScriptDataType.Object); AddOutput("Out", NodeScriptDataType.Exec); AddOutput("Result", NodeScriptDataType.Object); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { var fnName = Metadata.GetValueOrDefault("FunctionName", string.Empty); var fn = ctx.Graph.Functions.FirstOrDefault(f => f.Name == fnName); if (fn == null) { NodeScriptLog.Log($"CallFunction: function '{fnName}' not found"); return new() { ExecOutIndex("Out") }; } var sub = ctx.Interpreter.RunFunction(fn, ctx, ReadInput(ctx, "Arg0")); WriteOutput(ctx, "Result", sub); return new() { ExecOutIndex("Out") }; }
        public override NodeScriptNode Clone() { var n = new CallFunctionNode(); CloneInto(n); return n; }
    }

    public sealed class CallMethodNode : NodeScriptNode
    {
        public CallMethodNode() { Title = "Call Method"; Category = NodeCategory.Function; AddInput("In", NodeScriptDataType.Exec); AddInput("Target", NodeScriptDataType.Object); AddInput("Arg0", NodeScriptDataType.Object); AddOutput("Out", NodeScriptDataType.Exec); AddOutput("Return", NodeScriptDataType.Object); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { var typeName = Metadata.GetValueOrDefault("TypeName", string.Empty); var methodName = Metadata.GetValueOrDefault("MethodName", string.Empty); var target = ReadInput(ctx, "Target"); var arg = ReadInput(ctx, "Arg0"); var type = NodeScriptTypeUtils.ResolveType(typeName); object? result = null; try { var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static; var mi = type?.GetMethod(methodName, flags); if (mi != null) result = mi.Invoke(target is null || mi.IsStatic ? null : target, arg == null ? null : new[] { arg }); else NodeScriptLog.Log($"CallMethod: method {typeName}.{methodName} not found"); } catch (Exception ex) { NodeScriptLog.Log($"CallMethod exception: {ex.Message}"); } WriteOutput(ctx, "Return", result); return new() { ExecOutIndex("Out") }; }
        public override NodeScriptNode Clone() { var n = new CallMethodNode(); CloneInto(n); return n; }
    }

    // ── 5.5 Math Nodes ───────────────────────────────────────────────

    public abstract class BinaryMathNode : NodeScriptNode
    {
        public BinaryMathNode(string title) { Title = title; Category = NodeCategory.Math; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Float); }
        protected abstract float Op(float a, float b);
        public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Op(ReadInputAs<float>(ctx, "A"), ReadInputAs<float>(ctx, "B")));
    }
    public sealed class AddNode : BinaryMathNode { public AddNode() : base("Add (+)") { } protected override float Op(float a, float b) => a + b; public override NodeScriptNode Clone() { var n = new AddNode(); CloneInto(n); return n; } }
    public sealed class SubNode : BinaryMathNode { public SubNode() : base("Subtract (-)") { } protected override float Op(float a, float b) => a - b; public override NodeScriptNode Clone() { var n = new SubNode(); CloneInto(n); return n; } }
    public sealed class MulNode : BinaryMathNode { public MulNode() : base("Multiply (*)") { } protected override float Op(float a, float b) => a * b; public override NodeScriptNode Clone() { var n = new MulNode(); CloneInto(n); return n; } }
    public sealed class DivNode : BinaryMathNode { public DivNode() : base("Divide (/)") { } protected override float Op(float a, float b) => b == 0 ? 0 : a / b; public override NodeScriptNode Clone() { var n = new DivNode(); CloneInto(n); return n; } }
    public sealed class MinNode : BinaryMathNode { public MinNode() : base("Min") { } protected override float Op(float a, float b) => Math.Min(a, b); public override NodeScriptNode Clone() { var n = new MinNode(); CloneInto(n); return n; } }
    public sealed class MaxNode : BinaryMathNode { public MaxNode() : base("Max") { } protected override float Op(float a, float b) => Math.Max(a, b); public override NodeScriptNode Clone() { var n = new MaxNode(); CloneInto(n); return n; } }
    public sealed class ModNode : BinaryMathNode { public ModNode() : base("Modulo (%)") { } protected override float Op(float a, float b) => b == 0 ? 0 : a % b; public override NodeScriptNode Clone() { var n = new ModNode(); CloneInto(n); return n; } }
    public sealed class AbsNode : BinaryMathNode { public AbsNode() : base("Abs") { } protected override float Op(float a, float b) => Math.Abs(a); public override NodeScriptNode Clone() { var n = new AbsNode(); CloneInto(n); return n; } }

    public abstract class UnaryMathNode : NodeScriptNode
    {
        public UnaryMathNode(string title) { Title = title; Category = NodeCategory.Math; AddInput("A", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Float); }
        protected abstract float Op(float a);
        public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Op(ReadInputAs<float>(ctx, "A")));
    }
    public sealed class SqrtNode : UnaryMathNode { public SqrtNode() : base("Sqrt") { } protected override float Op(float a) => (float)Math.Sqrt(Math.Max(0, a)); public override NodeScriptNode Clone() { var n = new SqrtNode(); CloneInto(n); return n; } }
    public sealed class SinNode : UnaryMathNode { public SinNode() : base("Sin") { } protected override float Op(float a) => (float)Math.Sin(a); public override NodeScriptNode Clone() { var n = new SinNode(); CloneInto(n); return n; } }
    public sealed class CosNode : UnaryMathNode { public CosNode() : base("Cos") { } protected override float Op(float a) => (float)Math.Cos(a); public override NodeScriptNode Clone() { var n = new CosNode(); CloneInto(n); return n; } }
    public sealed class TanNode : UnaryMathNode { public TanNode() : base("Tan") { } protected override float Op(float a) => (float)Math.Tan(a); public override NodeScriptNode Clone() { var n = new TanNode(); CloneInto(n); return n; } }
    public sealed class CeilNode : UnaryMathNode { public CeilNode() : base("Ceil") { } protected override float Op(float a) => (float)Math.Ceiling(a); public override NodeScriptNode Clone() { var n = new CeilNode(); CloneInto(n); return n; } }
    public sealed class FloorNode : UnaryMathNode { public FloorNode() : base("Floor") { } protected override float Op(float a) => (float)Math.Floor(a); public override NodeScriptNode Clone() { var n = new FloorNode(); CloneInto(n); return n; } }
    public sealed class NegateNode : UnaryMathNode { public NegateNode() : base("Negate (-)") { } protected override float Op(float a) => -a; public override NodeScriptNode Clone() { var n = new NegateNode(); CloneInto(n); return n; } }

    public sealed class PowNode : NodeScriptNode { public PowNode() { Title = "Power"; Category = NodeCategory.Math; AddInput("Base", NodeScriptDataType.Float); AddInput("Exp", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", (float)Math.Pow(ReadInputAs<float>(ctx, "Base"), ReadInputAs<float>(ctx, "Exp"))); public override NodeScriptNode Clone() { var n = new PowNode(); CloneInto(n); return n; } }
    public sealed class DotNode : NodeScriptNode { public DotNode() { Title = "Dot"; Category = NodeCategory.Math; AddInput("A", NodeScriptDataType.Vector3); AddInput("B", NodeScriptDataType.Vector3); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Vector3.Dot(ReadInputAs<Vector3>(ctx, "A"), ReadInputAs<Vector3>(ctx, "B"))); public override NodeScriptNode Clone() { var n = new DotNode(); CloneInto(n); return n; } }
    public sealed class CrossNode : NodeScriptNode { public CrossNode() { Title = "Cross"; Category = NodeCategory.Math; AddInput("A", NodeScriptDataType.Vector3); AddInput("B", NodeScriptDataType.Vector3); AddOutput("Result", NodeScriptDataType.Vector3); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Vector3.Cross(ReadInputAs<Vector3>(ctx, "A"), ReadInputAs<Vector3>(ctx, "B"))); public override NodeScriptNode Clone() { var n = new CrossNode(); CloneInto(n); return n; } }
    public sealed class LerpNode : NodeScriptNode { public LerpNode() { Title = "Lerp"; Category = NodeCategory.Math; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddInput("Alpha", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { float a = ReadInputAs<float>(ctx, "A"), b = ReadInputAs<float>(ctx, "B"), t = ReadInputAs<float>(ctx, "Alpha"); WriteOutput(ctx, "Result", a + (b - a) * Math.Clamp(t, 0f, 1f)); } public override NodeScriptNode Clone() { var n = new LerpNode(); CloneInto(n); return n; } }
    public sealed class ClampNode : NodeScriptNode { public ClampNode() { Title = "Clamp"; Category = NodeCategory.Math; AddInput("Value", NodeScriptDataType.Float); AddInput("Min", NodeScriptDataType.Float); AddInput("Max", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { float v = ReadInputAs<float>(ctx, "Value"), mn = ReadInputAs<float>(ctx, "Min"), mx = ReadInputAs<float>(ctx, "Max"); WriteOutput(ctx, "Result", Math.Clamp(v, mn, mx)); } public override NodeScriptNode Clone() { var n = new ClampNode(); CloneInto(n); return n; } }
    public sealed class RandomNode : NodeScriptNode { public RandomNode() { Title = "Random"; Category = NodeCategory.Math; AddInput("Min", NodeScriptDataType.Float, 0f); AddInput("Max", NodeScriptDataType.Float, 1f); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { float mn = ReadInputAs<float>(ctx, "Min"), mx = ReadInputAs<float>(ctx, "Max"); WriteOutput(ctx, "Result", new Random().NextSingle() * (mx - mn) + mn); } public override NodeScriptNode Clone() { var n = new RandomNode(); CloneInto(n); return n; } }
    public sealed class LerpVectorNode : NodeScriptNode { public LerpVectorNode() { Title = "Lerp Vector"; Category = NodeCategory.Vector; AddInput("A", NodeScriptDataType.Vector3); AddInput("B", NodeScriptDataType.Vector3); AddInput("Alpha", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Vector3); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { var a = ReadInputAs<Vector3>(ctx, "A"); var b = ReadInputAs<Vector3>(ctx, "B"); float t = Math.Clamp(ReadInputAs<float>(ctx, "Alpha"), 0f, 1f); WriteOutput(ctx, "Result", Vector3.Lerp(a, b, t)); } public override NodeScriptNode Clone() { var n = new LerpVectorNode(); CloneInto(n); return n; } }

    // ── 5.6 Logic Nodes ───────────────────────────────────────────────

    public sealed class AndNode : NodeScriptNode { public AndNode() { Title = "And"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Bool); AddInput("B", NodeScriptDataType.Bool); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<bool>(ctx, "A") && ReadInputAs<bool>(ctx, "B")); public override NodeScriptNode Clone() { var n = new AndNode(); CloneInto(n); return n; } }
    public sealed class OrNode : NodeScriptNode { public OrNode() { Title = "Or"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Bool); AddInput("B", NodeScriptDataType.Bool); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<bool>(ctx, "A") || ReadInputAs<bool>(ctx, "B")); public override NodeScriptNode Clone() { var n = new OrNode(); CloneInto(n); return n; } }
    public sealed class NotNode : NodeScriptNode { public NotNode() { Title = "Not"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Bool); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", !ReadInputAs<bool>(ctx, "A")); public override NodeScriptNode Clone() { var n = new NotNode(); CloneInto(n); return n; } }
    public sealed class XorNode : NodeScriptNode { public XorNode() { Title = "Xor"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Bool); AddInput("B", NodeScriptDataType.Bool); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<bool>(ctx, "A") ^ ReadInputAs<bool>(ctx, "B")); public override NodeScriptNode Clone() { var n = new XorNode(); CloneInto(n); return n; } }
    public sealed class EqualNode : NodeScriptNode { public EqualNode() { Title = "Equal (==)"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Math.Abs(ReadInputAs<float>(ctx, "A") - ReadInputAs<float>(ctx, "B")) < 1e-5f); public override NodeScriptNode Clone() { var n = new EqualNode(); CloneInto(n); return n; } }
    public sealed class NotEqualNode : NodeScriptNode { public NotEqualNode() { Title = "Not Equal (!=)"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Math.Abs(ReadInputAs<float>(ctx, "A") - ReadInputAs<float>(ctx, "B")) >= 1e-5f); public override NodeScriptNode Clone() { var n = new NotEqualNode(); CloneInto(n); return n; } }
    public sealed class GreaterNode : NodeScriptNode { public GreaterNode() { Title = "Greater (>)"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<float>(ctx, "A") > ReadInputAs<float>(ctx, "B")); public override NodeScriptNode Clone() { var n = new GreaterNode(); CloneInto(n); return n; } }
    public sealed class LessNode : NodeScriptNode { public LessNode() { Title = "Less (<)"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<float>(ctx, "A") < ReadInputAs<float>(ctx, "B")); public override NodeScriptNode Clone() { var n = new LessNode(); CloneInto(n); return n; } }
    public sealed class GreaterEqualNode : NodeScriptNode { public GreaterEqualNode() { Title = "Greater Equal (>=)"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<float>(ctx, "A") >= ReadInputAs<float>(ctx, "B")); public override NodeScriptNode Clone() { var n = new GreaterEqualNode(); CloneInto(n); return n; } }
    public sealed class LessEqualNode : NodeScriptNode { public LessEqualNode() { Title = "Less Equal (<=)"; Category = NodeCategory.Logic; AddInput("A", NodeScriptDataType.Float); AddInput("B", NodeScriptDataType.Float); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<float>(ctx, "A") <= ReadInputAs<float>(ctx, "B")); public override NodeScriptNode Clone() { var n = new LessEqualNode(); CloneInto(n); return n; } }

    // ── 5.7 Vector Nodes ───────────────────────────────────────────────

    public sealed class MakeVectorNode : NodeScriptNode { public MakeVectorNode() { Title = "Make Vector"; Category = NodeCategory.Vector; AddInput("X", NodeScriptDataType.Float); AddInput("Y", NodeScriptDataType.Float); AddInput("Z", NodeScriptDataType.Float); AddOutput("Vector", NodeScriptDataType.Vector3); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Vector", new Vector3(ReadInputAs<float>(ctx, "X"), ReadInputAs<float>(ctx, "Y"), ReadInputAs<float>(ctx, "Z"))); public override NodeScriptNode Clone() { var n = new MakeVectorNode(); CloneInto(n); return n; } }
    public sealed class BreakVectorNode : NodeScriptNode { public BreakVectorNode() { Title = "Break Vector"; Category = NodeCategory.Vector; AddInput("Vector", NodeScriptDataType.Vector3); AddOutput("X", NodeScriptDataType.Float); AddOutput("Y", NodeScriptDataType.Float); AddOutput("Z", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { var v = ReadInputAs<Vector3>(ctx, "Vector"); WriteOutput(ctx, "X", v.X); WriteOutput(ctx, "Y", v.Y); WriteOutput(ctx, "Z", v.Z); } public override NodeScriptNode Clone() { var n = new BreakVectorNode(); CloneInto(n); return n; } }
    public sealed class NormalizeNode : NodeScriptNode { public NormalizeNode() { Title = "Normalize"; Category = NodeCategory.Vector; AddInput("Vector", NodeScriptDataType.Vector3); AddOutput("Result", NodeScriptDataType.Vector3); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Vector3.Normalize(ReadInputAs<Vector3>(ctx, "Vector"))); public override NodeScriptNode Clone() { var n = new NormalizeNode(); CloneInto(n); return n; } }
    public sealed class LengthNode : NodeScriptNode { public LengthNode() { Title = "Length"; Category = NodeCategory.Vector; AddInput("Vector", NodeScriptDataType.Vector3); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", ReadInputAs<Vector3>(ctx, "Vector").Length()); public override NodeScriptNode Clone() { var n = new LengthNode(); CloneInto(n); return n; } }
    public sealed class DistanceNode : NodeScriptNode { public DistanceNode() { Title = "Distance"; Category = NodeCategory.Vector; AddInput("A", NodeScriptDataType.Vector3); AddInput("B", NodeScriptDataType.Vector3); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Vector3.Distance(ReadInputAs<Vector3>(ctx, "A"), ReadInputAs<Vector3>(ctx, "B"))); public override NodeScriptNode Clone() { var n = new DistanceNode(); CloneInto(n); return n; } }
    public sealed class ReflectNode : NodeScriptNode { public ReflectNode() { Title = "Reflect"; Category = NodeCategory.Vector; AddInput("Incident", NodeScriptDataType.Vector3); AddInput("Normal", NodeScriptDataType.Vector3); AddOutput("Result", NodeScriptDataType.Vector3); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", Vector3.Reflect(ReadInputAs<Vector3>(ctx, "Incident"), ReadInputAs<Vector3>(ctx, "Normal"))); public override NodeScriptNode Clone() { var n = new ReflectNode(); CloneInto(n); return n; } }

    // ── 5.8 Cast Nodes ────────────────────────────────────────────────

    public sealed class CastNode : NodeScriptNode { public CastNode() { Title = "Cast"; Category = NodeCategory.Cast; AddInput("In", NodeScriptDataType.Object); AddOutput("Out", NodeScriptDataType.Object); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { var v = ReadInput(ctx, "In"); var tn = Metadata.GetValueOrDefault("TargetType", "Object"); if (Enum.TryParse<NodeScriptDataType>(tn, out var dt)) WriteOutput(ctx, "Out", NodeScriptTypeUtils.Convert(v, dt)); else WriteOutput(ctx, "Out", v); } public override NodeScriptNode Clone() { var n = new CastNode(); CloneInto(n); return n; } }
    public sealed class ToStringNode : NodeScriptNode { public ToStringNode() { Title = "To String"; Category = NodeCategory.Cast; AddInput("In", NodeScriptDataType.Object); AddOutput("String", NodeScriptDataType.String); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "String", NodeScriptTypeUtils.FormatValue(ReadInput(ctx, "In"))); public override NodeScriptNode Clone() { var n = new ToStringNode(); CloneInto(n); return n; } }
    public sealed class ToFloatNode : NodeScriptNode { public ToFloatNode() { Title = "To Float"; Category = NodeCategory.Cast; AddInput("In", NodeScriptDataType.Object); AddOutput("Result", NodeScriptDataType.Float); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", NodeScriptTypeUtils.Convert(ReadInput(ctx, "In"), NodeScriptDataType.Float)); public override NodeScriptNode Clone() { var n = new ToFloatNode(); CloneInto(n); return n; } }
    public sealed class ToIntNode : NodeScriptNode { public ToIntNode() { Title = "To Int"; Category = NodeCategory.Cast; AddInput("In", NodeScriptDataType.Object); AddOutput("Result", NodeScriptDataType.Int); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", NodeScriptTypeUtils.Convert(ReadInput(ctx, "In"), NodeScriptDataType.Int)); public override NodeScriptNode Clone() { var n = new ToIntNode(); CloneInto(n); return n; } }
    public sealed class ToBoolNode : NodeScriptNode { public ToBoolNode() { Title = "To Bool"; Category = NodeCategory.Cast; AddInput("In", NodeScriptDataType.Object); AddOutput("Result", NodeScriptDataType.Bool); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Result", NodeScriptTypeUtils.Convert(ReadInput(ctx, "In"), NodeScriptDataType.Bool)); public override NodeScriptNode Clone() { var n = new ToBoolNode(); CloneInto(n); return n; } }

    // ── 5.9 Entity Nodes ──────────────────────────────────────────────

    public sealed class SpawnEntityNode : NodeScriptNode { public SpawnEntityNode() { Title = "Spawn Entity"; Category = NodeCategory.Entity; AddInput("In", NodeScriptDataType.Exec); AddInput("Name", NodeScriptDataType.String, "Entity"); AddInput("Position", NodeScriptDataType.Vector3); AddInput("Rotation", NodeScriptDataType.Quaternion); AddInput("Scale", NodeScriptDataType.Vector3, new Vector3(1, 1, 1)); AddOutput("Out", NodeScriptDataType.Exec); AddOutput("Entity", NodeScriptDataType.Entity); } public override List<int> Execute(NodeScriptExecutionContext ctx) { var name = ReadInputAs<string>(ctx, "Name") ?? "Entity"; var pos = ReadInputAs<Vector3>(ctx, "Position"); var rot = ReadInputAs<Quaternion>(ctx, "Rotation"); var scl = ReadInputAs<Vector3>(ctx, "Scale"); int id = ctx.Ecs.CreateEntity(name, pos, rot, scl); WriteOutput(ctx, "Entity", id); return new() { ExecOutIndex("Out") }; } public override NodeScriptNode Clone() { var n = new SpawnEntityNode(); CloneInto(n); return n; } }
    public sealed class DestroyEntityNode : NodeScriptNode { public DestroyEntityNode() { Title = "Destroy Entity"; Category = NodeCategory.Entity; AddInput("In", NodeScriptDataType.Exec); AddInput("Entity", NodeScriptDataType.Entity); AddOutput("Out", NodeScriptDataType.Exec); } public override List<int> Execute(NodeScriptExecutionContext ctx) { int id = ReadInputAs<int>(ctx, "Entity"); ctx.Ecs.DestroyEntity(id); return new() { ExecOutIndex("Out") }; } public override NodeScriptNode Clone() { var n = new DestroyEntityNode(); CloneInto(n); return n; } }
    public sealed class GetEntityNode : NodeScriptNode { public GetEntityNode() { Title = "Get Entity (Self)"; Category = NodeCategory.Entity; AddOutput("Entity", NodeScriptDataType.Entity); Metadata["Mode"] = "Self"; } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) => WriteOutput(ctx, "Entity", ctx.SelfEntity); public override NodeScriptNode Clone() { var n = new GetEntityNode(); CloneInto(n); return n; } }
    public sealed class GetComponentNode : NodeScriptNode { public GetComponentNode() { Title = "Get Component"; Category = NodeCategory.Entity; AddInput("Entity", NodeScriptDataType.Entity); AddOutput("Component", NodeScriptDataType.Component); } public override void EvaluateOutputs(NodeScriptExecutionContext ctx) { int entity = ReadInputAs<int>(ctx, "Entity"); var typeName = Metadata.GetValueOrDefault("ComponentType", string.Empty); var type = NodeScriptTypeUtils.ResolveType(typeName); if (type == null) { WriteOutput(ctx, "Component", null); return; } try { var method = typeof(EntityComponentSystem).GetMethod(nameof(EntityComponentSystem.GetComponent))!.MakeGenericMethod(type); var comp = method.Invoke(ctx.Ecs, new object[] { entity }); WriteOutput(ctx, "Component", comp); } catch { WriteOutput(ctx, "Component", null); } } public override NodeScriptNode Clone() { var n = new GetComponentNode(); CloneInto(n); return n; } }
    public sealed class SetComponentNode : NodeScriptNode { public SetComponentNode() { Title = "Set Component"; Category = NodeCategory.Entity; AddInput("In", NodeScriptDataType.Exec); AddInput("Entity", NodeScriptDataType.Entity); AddInput("Component", NodeScriptDataType.Component); AddOutput("Out", NodeScriptDataType.Exec); } public override List<int> Execute(NodeScriptExecutionContext ctx) { int entity = ReadInputAs<int>(ctx, "Entity"); var comp = ReadInput(ctx, "Component"); var typeName = Metadata.GetValueOrDefault("ComponentType", string.Empty); var type = NodeScriptTypeUtils.ResolveType(typeName); if (type != null && comp != null) { try { bool has = (bool)typeof(EntityComponentSystem).GetMethod(nameof(EntityComponentSystem.HasComponent))!.MakeGenericMethod(type).Invoke(ctx.Ecs, new object[] { entity })!; if (has) typeof(EntityComponentSystem).GetMethod(nameof(EntityComponentSystem.SetComponent))!.MakeGenericMethod(type).Invoke(ctx.Ecs, new[] { (object)entity, comp }); else typeof(EntityComponentSystem).GetMethod(nameof(EntityComponentSystem.AddComponent))!.MakeGenericMethod(type).Invoke(ctx.Ecs, new[] { (object)entity, comp }); } catch (Exception ex) { NodeScriptLog.Log($"SetComponent exception: {ex.Message}"); } } return new() { ExecOutIndex("Out") }; } public override NodeScriptNode Clone() { var n = new SetComponentNode(); CloneInto(n); return n; } }

    // ── 5.10 Debug Nodes ──────────────────────────────────────────────

    public sealed class PrintNode : NodeScriptNode { public PrintNode() { Title = "Print"; Category = NodeCategory.Debug; AddInput("In", NodeScriptDataType.Exec); AddInput("Message", NodeScriptDataType.String, "Hello"); AddOutput("Out", NodeScriptDataType.Exec); } public override List<int> Execute(NodeScriptExecutionContext ctx) { NodeScriptLog.Log(NodeScriptTypeUtils.FormatValue(ReadInput(ctx, "Message"))); return new() { ExecOutIndex("Out") }; } public override NodeScriptNode Clone() { var n = new PrintNode(); CloneInto(n); return n; } }
    public sealed class LogNode : NodeScriptNode { public LogNode() { Title = "Log"; Category = NodeCategory.Debug; AddInput("In", NodeScriptDataType.Exec); AddInput("Message", NodeScriptDataType.String); AddOutput("Out", NodeScriptDataType.Exec); Metadata["Level"] = "Info"; } public override List<int> Execute(NodeScriptExecutionContext ctx) { var level = Metadata.GetValueOrDefault("Level", "Info"); var msg = NodeScriptTypeUtils.FormatValue(ReadInput(ctx, "Message")); NodeScriptLog.Log($"[{level}] {msg}"); return new() { ExecOutIndex("Out") }; } public override NodeScriptNode Clone() { var n = new LogNode(); CloneInto(n); return n; } }
    public sealed class BreakpointNode : NodeScriptNode { public BreakpointNode() { Title = "Breakpoint"; Category = NodeCategory.Debug; AddInput("In", NodeScriptDataType.Exec); AddOutput("Out", NodeScriptDataType.Exec); } public override List<int> Execute(NodeScriptExecutionContext ctx) { ctx.ShouldBreak = true; NodeScriptLog.Log($"Breakpoint hit @ entity {ctx.SelfEntity}"); return new() { ExecOutIndex("Out") }; } public override NodeScriptNode Clone() { var n = new BreakpointNode(); CloneInto(n); return n; } }

    // ── 5.11 Delay/Timeline Nodes (NEW) ──────────────────────────────

    /// <summary>Delay node: pauses execution for a duration, then resumes.</summary>
    public sealed class DelayNode : NodeScriptNode
    {
        private float _remainingTime;
        private bool _active;
        public DelayNode() { Title = "Delay"; Category = NodeCategory.Delay; AddInput("In", NodeScriptDataType.Exec); AddInput("Duration", NodeScriptDataType.Float, 1f); AddOutput("Completed", NodeScriptDataType.Exec); }
        public override List<int> Execute(NodeScriptExecutionContext ctx) { _active = true; _remainingTime = ReadInputAs<float>(ctx, "Duration"); return new(); } // Does not immediately continue
        public void Tick(float dt, NodeScriptExecutionContext ctx) { if (!_active) return; _remainingTime -= dt; if (_remainingTime <= 0f) { _active = false; ctx.Interpreter.RunOutputChain(ctx, this, ExecOutIndex("Completed")); } }
        public override NodeScriptNode Clone() { var n = new DelayNode(); CloneInto(n); return n; }
    }

    /// <summary>Timeline node: evaluates a curve over time, outputs current value each tick.</summary>
    public sealed class TimelineNode : NodeScriptNode
    {
        public float CurrentTime;
        public float Duration = 5f;
        public bool IsPlaying;
        public bool Loop;
        private List<(float t, float v)> _keys = new() { (0f, 0f), (1f, 1f) }; // Normalized keys 0-1
        public TimelineNode() { Title = "Timeline"; Category = NodeCategory.Timeline; AddOutput("Update", NodeScriptDataType.Exec); AddOutput("Finished", NodeScriptDataType.Exec); AddOutput("Time", NodeScriptDataType.Float); AddOutput("Value", NodeScriptDataType.Float); AddOutput("Alpha", NodeScriptDataType.Float); }
        public override List<int> Execute(NodeScriptExecutionContext ctx)
        {
            CurrentTime = 0f; IsPlaying = true;
            return new();
        }
        public void Tick(float dt, NodeScriptExecutionContext ctx)
        {
            if (!IsPlaying) return;
            CurrentTime += dt;
            float alpha = Math.Clamp(CurrentTime / Duration, 0f, 1f);
            float value = EvaluateCurve(alpha);
            WriteOutput(ctx, "Time", CurrentTime);
            WriteOutput(ctx, "Value", value);
            WriteOutput(ctx, "Alpha", alpha);
            ctx.Interpreter.RunOutputChain(ctx, this, ExecOutIndex("Update"));
            if (CurrentTime >= Duration)
            {
                if (Loop) CurrentTime = 0f;
                else { IsPlaying = false; ctx.Interpreter.RunOutputChain(ctx, this, ExecOutIndex("Finished")); }
            }
        }
        public void SetKeys(List<(float t, float v)> keys) { _keys = keys; }
        private float EvaluateCurve(float alpha)
        {
            if (_keys.Count == 0) return 0f;
            if (_keys.Count == 1) return _keys[0].v;
            for (int i = 0; i < _keys.Count - 1; i++)
                if (alpha >= _keys[i].t && alpha <= _keys[i + 1].t)
                { float range = _keys[i + 1].t - _keys[i].t; float localT = range < 1e-8f ? 0f : (alpha - _keys[i].t) / range; return _keys[i].v + (_keys[i + 1].v - _keys[i].v) * localT; }
            return _keys[_keys.Count - 1].v;
        }
        public override NodeScriptNode Clone() { var n = new TimelineNode() { Duration = Duration, Loop = Loop, _keys = new List<(float, float)>(_keys) }; CloneInto(n); return n; }
    }

    /// <summary>Set Timer by Event: triggers a named timer that fires a custom event after duration.</summary>
    public sealed class SetTimerNode : NodeScriptNode
    {
        public SetTimerNode() { Title = "Set Timer"; Category = NodeCategory.Delay; AddInput("In", NodeScriptDataType.Exec); AddInput("EventName", NodeScriptDataType.String); AddInput("Duration", NodeScriptDataType.Float, 1f); AddInput("Looping", NodeScriptDataType.Bool); AddOutput("Out", NodeScriptDataType.Exec); AddOutput("TimerHandle", NodeScriptDataType.Int); }
        public override List<int> Execute(NodeScriptExecutionContext ctx)
        {
            var evt = ReadInputAs<string>(ctx, "EventName") ?? "TimerEvent";
            float dur = ReadInputAs<float>(ctx, "Duration");
            bool loop = ReadInputAs<bool>(ctx, "Looping");
            int handle = evt.GetHashCode();
            // Store timer info in metadata for the execution system to pick up
            Metadata["TimerEvent"] = evt;
            Metadata["TimerDuration"] = dur.ToString(CultureInfo.InvariantCulture);
            Metadata["TimerLooping"] = loop.ToString();
            WriteOutput(ctx, "TimerHandle", handle);
            return new() { ExecOutIndex("Out") };
        }
        public override NodeScriptNode Clone() { var n = new SetTimerNode(); CloneInto(n); return n; }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 6: Node Factory Catalog
    // ─────────────────────────────────────────────────────────────────────

    public static class NodeScriptNodeCatalog
    {
        private static readonly Dictionary<string, Func<NodeScriptNode>> _ctors = new();
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            Register<GetVariableNode>("GetVariable"); Register<SetVariableNode>("SetVariable");
            Register<GetGlobalVariableNode>("GetGlobalVariable"); Register<SetGlobalVariableNode>("SetGlobalVariable");
            Register<GetAttributeNode>("GetAttribute"); Register<SetAttributeNode>("SetAttribute");
            Register<SequenceNode>("Sequence"); Register<IfNode>("If");
            Register<WhileNode>("While"); Register<ForNode>("For"); Register<ForEachNode>("ForEach");
            Register<SwitchNode>("Switch"); Register<DoOnceNode>("DoOnce"); Register<FlipFlopNode>("FlipFlop");
            Register<OnBeginNode>("OnBegin"); Register<OnTickNode>("OnTick");
            Register<OnCollisionNode>("OnCollision"); Register<OnTriggerNode>("OnTrigger");
            Register<OnSpawnNode>("OnSpawn"); Register<OnDestroyNode>("OnDestroy");
            Register<CustomEventNode>("CustomEvent");
            Register<CallFunctionNode>("CallFunction"); Register<CallMethodNode>("CallMethod");
            Register<AddNode>("Add"); Register<SubNode>("Sub"); Register<MulNode>("Mul");
            Register<DivNode>("Div"); Register<MinNode>("Min"); Register<MaxNode>("Max");
            Register<ModNode>("Mod"); Register<AbsNode>("Abs");
            Register<SqrtNode>("Sqrt"); Register<SinNode>("Sin"); Register<CosNode>("Cos");
            Register<TanNode>("Tan"); Register<CeilNode>("Ceil"); Register<FloorNode>("Floor");
            Register<NegateNode>("Negate");
            Register<PowNode>("Pow"); Register<DotNode>("Dot"); Register<CrossNode>("Cross");
            Register<LerpNode>("Lerp"); Register<ClampNode>("Clamp"); Register<RandomNode>("Random");
            Register<LerpVectorNode>("LerpVector");
            Register<AndNode>("And"); Register<OrNode>("Or"); Register<NotNode>("Not"); Register<XorNode>("Xor");
            Register<EqualNode>("Equal"); Register<NotEqualNode>("NotEqual");
            Register<GreaterNode>("Greater"); Register<LessNode>("Less");
            Register<GreaterEqualNode>("GreaterEqual"); Register<LessEqualNode>("LessEqual");
            Register<MakeVectorNode>("MakeVector"); Register<BreakVectorNode>("BreakVector");
            Register<NormalizeNode>("Normalize"); Register<LengthNode>("Length");
            Register<DistanceNode>("Distance"); Register<ReflectNode>("Reflect");
            Register<CastNode>("Cast"); Register<ToStringNode>("ToString");
            Register<ToFloatNode>("ToFloat"); Register<ToIntNode>("ToInt"); Register<ToBoolNode>("ToBool");
            Register<SpawnEntityNode>("SpawnEntity"); Register<DestroyEntityNode>("DestroyEntity");
            Register<GetEntityNode>("GetEntity"); Register<GetComponentNode>("GetComponent");
            Register<SetComponentNode>("SetComponent");
            Register<PrintNode>("Print"); Register<LogNode>("Log"); Register<BreakpointNode>("Breakpoint");
            Register<DelayNode>("Delay"); Register<TimelineNode>("Timeline"); Register<SetTimerNode>("SetTimer");
        }

        public static void Register<T>(string key) where T : NodeScriptNode, new() => _ctors[key] = () => new T();
        public static NodeScriptNode? Create(string key) { Initialize(); return _ctors.TryGetValue(key, out var ctor) ? ctor() : null; }
        public static IEnumerable<string> GetAllKeys() { Initialize(); return _ctors.Keys; }
        public static string GetKey(NodeScriptNode node) => node.GetType().Name.Replace("Node", string.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 7: NodeScript Graph
    // ─────────────────────────────────────────────────────────────────────

    public sealed class NodeScriptGraph
    {
        public string Name = string.Empty;
        public string? Description;
        public string? ParentNodeScript;
        public Dictionary<Guid, NodeScriptNode> Nodes = new();
        public List<NodeScriptEdge> Edges = new();
        public List<NodeScriptVariable> Variables = new();
        public List<NodeScriptFunction> Functions = new();
        private Dictionary<Guid, NodeScriptNode>? _pinIndex;
        private bool _indexDirty = true;

        public void AddNode(NodeScriptNode node) { Nodes[node.Id] = node; _indexDirty = true; }
        public bool RemoveNode(Guid nodeId)
        {
            if (!Nodes.TryGetValue(nodeId, out var node)) return false;
            var pinIds = new HashSet<Guid>();
            foreach (var p in node.InputPins) pinIds.Add(p.Id);
            foreach (var p in node.OutputPins) pinIds.Add(p.Id);
            Edges.RemoveAll(e => pinIds.Contains(e.FromPinId) || pinIds.Contains(e.ToPinId));
            Nodes.Remove(nodeId); _indexDirty = true; return true;
        }
        public void RemoveNodeWithEdges(Guid nodeId) => RemoveNode(nodeId);
        public NodeScriptNode? FindNode(Guid nodeId) => Nodes.TryGetValue(nodeId, out var n) ? n : null;
        public NodeScriptNode? FindNodeByPin(Guid pinId) { if (_indexDirty) RebuildPinIndex(); return _pinIndex != null && _pinIndex.TryGetValue(pinId, out var n) ? n : null; }
        public NodeScriptEdge? FindEdgeTo(Guid inputPinId) => Edges.FirstOrDefault(e => e.ToPinId == inputPinId);
        public List<NodeScriptEdge> FindEdgesFrom(Guid outputPinId) => Edges.Where(e => e.FromPinId == outputPinId).ToList();

        public NodeScriptEdge? Connect(Guid fromPinId, Guid toPinId)
        {
            var fromNode = FindNodeByPin(fromPinId); var toNode = FindNodeByPin(toPinId);
            if (fromNode == null || toNode == null) return null;
            var fromPin = fromNode.FindPin(fromPinId); var toPin = toNode.FindPin(toPinId);
            if (fromPin == null || toPin == null) return null;
            if (fromPin.Direction != PinDirection.Output || toPin.Direction != PinDirection.Input) return null;
            if (fromPin.IsExec != toPin.IsExec) return null;
            if (!fromPin.IsExec && !NodeScriptTypeUtils.AreCompatible(fromPin.DataType, toPin.DataType)) return null;
            if (!toPin.IsExec) Edges.RemoveAll(e => e.ToPinId == toPinId);
            var edge = new NodeScriptEdge { FromPinId = fromPinId, ToPinId = toPinId, ResolvedType = fromPin.IsExec ? NodeScriptDataType.Exec : fromPin.DataType };
            Edges.Add(edge); return edge;
        }

        public bool Disconnect(Guid edgeId) { int removed = Edges.RemoveAll(e => e.Id == edgeId); return removed > 0; }
        public List<EventNodeBase> GetEventEntries() => Nodes.Values.OfType<EventNodeBase>().ToList();
        public List<EventNodeBase> GetEventEntries(string eventName) => Nodes.Values.OfType<EventNodeBase>().Where(n => n.EventName == eventName).ToList();
        public void RebuildPinIndex() { _pinIndex = new Dictionary<Guid, NodeScriptNode>(); foreach (var node in Nodes.Values) { foreach (var p in node.InputPins) _pinIndex[p.Id] = node; foreach (var p in node.OutputPins) _pinIndex[p.Id] = node; } _indexDirty = false; }
        public void AddVariable(NodeScriptVariable variable) => Variables.Add(variable);
        public NodeScriptVariable? FindVariable(string name) => Variables.FirstOrDefault(v => v.Name == name);
        public List<string> ValidateTopology() { var errors = new List<string>(); RebuildPinIndex(); foreach (var edge in Edges) { if (FindNodeByPin(edge.FromPinId) == null) errors.Add($"Edge {edge.Id} source pin missing."); if (FindNodeByPin(edge.ToPinId) == null) errors.Add($"Edge {edge.Id} target pin missing."); } return errors; }

        /// <summary>Topological sort of exec-reachable nodes from event entries.</summary>
        public List<NodeScriptNode> TopologicalSort()
        {
            var result = new List<NodeScriptNode>();
            var visited = new HashSet<Guid>();
            var inDegree = new Dictionary<Guid, int>();
            foreach (var node in Nodes.Values) inDegree[node.Id] = 0;
            foreach (var edge in Edges.Where(e => e.IsExec))
                if (Nodes.Values.Any(n => n.OutputPins.Any(p => p.Id == edge.FromPinId)))
                { var target = FindNodeByPin(edge.ToPinId); if (target != null) inDegree[target.Id]++; }
            var queue = new Queue<NodeScriptNode>();
            foreach (var entry in GetEventEntries()) { if (inDegree[entry.Id] == 0) { queue.Enqueue(entry); visited.Add(entry.Id); } }
            while (queue.Count > 0) { var node = queue.Dequeue(); result.Add(node); foreach (var op in node.OutputPins.Where(p => p.IsExec)) foreach (var edge in FindEdgesFrom(op.Id)) { var next = FindNodeByPin(edge.ToPinId); if (next != null && visited.Add(next.Id)) { inDegree[next.Id]--; if (inDegree[next.Id] <= 0) queue.Enqueue(next); } } }
            return result;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 8: NodeScript Interpreter
    // ─────────────────────────────────────────────────────────────────────

    public sealed class NodeScriptInterpreter
    {
        public Dictionary<int, Dictionary<string, object?>> GlobalAttributes = new();
        public Dictionary<string, object?> GlobalVariables = new();

        public void RunEvent(NodeScriptGraph graph, Dictionary<string, object?> instanceVars,
            EntityComponentSystem ecs, int entity, string eventName, float dt, Dictionary<string, object?>? eventArgs = null)
        {
            var entries = graph.GetEventEntries(eventName);
            if (entries.Count == 0) return;
            var ctx = NewContext(graph, ecs, entity, dt, instanceVars, eventArgs);
            foreach (var entry in entries)
            {
                ctx.StepsUsed = 0; FollowExec(ctx, entry);
                if (ctx.ShouldBreak) { NodeScriptLog.Log($"Breakpoint paused: {graph.Name}.{eventName}"); break; }
            }
        }

        public object? RunFunction(NodeScriptFunction fn, NodeScriptExecutionContext callerCtx, object? arg0)
        {
            var ctx = NewContext(fn.Graph, callerCtx.Ecs, callerCtx.SelfEntity, callerCtx.DeltaTime, new Dictionary<string, object?>(), null);
            if (fn.Parameters.Count > 0) ctx.Variables[fn.Parameters[0].Name] = arg0;
            foreach (var lv in fn.LocalVariables) ctx.Variables[lv.Name] = lv.GetDefaultValue();
            var entry = fn.Graph.GetEventEntries().FirstOrDefault();
            if (entry != null) FollowExec(ctx, entry);
            return fn.Outputs.Count > 0 ? ctx.Variables.GetValueOrDefault(fn.Outputs[0].Name) : null;
        }

        private NodeScriptExecutionContext NewContext(NodeScriptGraph graph, EntityComponentSystem ecs, int entity, float dt, Dictionary<string, object?> vars, Dictionary<string, object?>? eventArgs)
        {
            graph.RebuildPinIndex();
            return new NodeScriptExecutionContext { Graph = graph, Interpreter = this, Ecs = ecs, SelfEntity = entity, DeltaTime = dt, Variables = vars, Attributes = GlobalAttributes, EventArguments = eventArgs ?? new Dictionary<string, object?>(), GlobalVariables = GlobalVariables };
        }

        public void FollowExec(NodeScriptExecutionContext ctx, NodeScriptNode node)
        {
            if (ctx.IsStepLimitExceeded) { NodeScriptLog.Log("Execution step limit reached, aborting."); return; }
            ctx.StepsUsed++;
            List<int> nextIndices;
            try { nextIndices = node.Execute(ctx); }
            catch (Exception ex) { NodeScriptLog.Log($"Node execution error [{node.Title}]: {ex.Message}"); return; }
            foreach (var idx in nextIndices)
            {
                if (idx < 0 || idx >= node.OutputPins.Count) continue;
                var outPin = node.OutputPins[idx];
                if (!outPin.IsExec) continue;
                foreach (var edge in ctx.Graph.FindEdgesFrom(outPin.Id)) { var nextNode = ctx.Graph.FindNodeByPin(edge.ToPinId); if (nextNode != null) FollowExec(ctx, nextNode); if (ctx.IsStepLimitExceeded || ctx.ShouldBreak) return; }
            }
        }

        public void RunOutputChain(NodeScriptExecutionContext ctx, NodeScriptNode node, int outputIndex)
        {
            if (outputIndex < 0 || outputIndex >= node.OutputPins.Count) return;
            var outPin = node.OutputPins[outputIndex]; if (!outPin.IsExec) return;
            foreach (var edge in ctx.Graph.FindEdgesFrom(outPin.Id)) { var nextNode = ctx.Graph.FindNodeByPin(edge.ToPinId); if (nextNode != null) FollowExec(ctx, nextNode); if (ctx.IsStepLimitExceeded || ctx.ShouldBreak) return; }
        }

        public object? ReadInputPin(NodeScriptExecutionContext ctx, NodeScriptNode node, NodeScriptPin inputPin)
        {
            var edge = ctx.Graph.FindEdgeTo(inputPin.Id);
            if (edge == null) return inputPin.DefaultValue;
            var srcNode = ctx.Graph.FindNodeByPin(edge.FromPinId); if (srcNode == null) return inputPin.DefaultValue;
            var srcOut = srcNode.FindPin(edge.FromPinId); if (srcOut == null) return inputPin.DefaultValue;
            if (!ctx.PinCache.TryGetValue(srcOut.Id, out var val))
            {
                if (srcNode.IsPure) { if (ctx.Evaluating.Contains(srcNode.Id)) return NodeScriptTypeUtils.GetDefault(inputPin.DataType); ctx.Evaluating.Add(srcNode.Id); try { srcNode.EvaluateOutputs(ctx); } finally { ctx.Evaluating.Remove(srcNode.Id); } }
                ctx.PinCache.TryGetValue(srcOut.Id, out val);
            }
            if (val == null) return NodeScriptTypeUtils.GetDefault(inputPin.DataType);
            if (!NodeScriptTypeUtils.AreCompatible(srcOut.DataType, inputPin.DataType)) return val;
            return NodeScriptTypeUtils.Convert(val, inputPin.DataType);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 9: Bytecode Compiler & VM (NEW)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Bytecode opcodes for the nodescript virtual machine.</summary>
    public enum NodeScriptOpCode
    {
        Nop, LoadConst, LoadVar, StoreVar, LoadGlobal, StoreGlobal,
        LoadPin, StorePin, CallExec, CallPure, Branch, Jump, LoopStart, LoopEnd,
        PushFrame, PopFrame, Add, Sub, Mul, Div, Mod, Negate,
        And, Or, Not, Xor, Equal, NotEqual, Greater, Less, GreaterEq, LessEq,
        Lerp, Clamp, Sqrt, Sin, Cos, Tan, Pow, Abs, Floor, Ceil, Random,
        MakeVec, BreakVec, Normalize, VecLen, VecDist, Dot, Cross, Reflect,
        Cast, ToStringOp, ToFloat, ToInt, ToBool,
        SpawnEntity, DestroyEntity, GetEntity, GetComponent, SetComponent,
        Print, Log, Breakpoint, Delay, Return, Halt
    }

    /// <summary>A single bytecode instruction.</summary>
    public struct NodeScriptInstruction
    {
        public NodeScriptOpCode OpCode;
        public NodeScriptDataType OperandType;
        public int OperandA;    // Usually a pin Id hash or variable index
        public int OperandB;    // Secondary operand
        public float FloatOperand; // For constant values
        public string StringOperand; // For variable names, metadata
        public NodeScriptInstruction(NodeScriptOpCode op) { OpCode = op; OperandType = NodeScriptDataType.Exec; OperandA = 0; OperandB = 0; FloatOperand = 0f; StringOperand = string.Empty; }
        public override string ToString() => $"{OpCode}({OperandA},{OperandB},F={FloatOperand},S={StringOperand})";
    }

    /// <summary>Compiled bytecode for a nodescript graph.</summary>
    public sealed class NodeScriptBytecode
    {
        public List<NodeScriptInstruction> Instructions = new();
        public Dictionary<string, int> VariableIndices = new();
        public Dictionary<string, object?> Constants = new();
        public int InstructionCount => Instructions.Count;

        public void Emit(NodeScriptInstruction inst) => Instructions.Add(inst);
        public NodeScriptInstruction this[int index] => index >= 0 && index < Instructions.Count ? Instructions[index] : new NodeScriptInstruction(NodeScriptOpCode.Nop);
    }

    /// <summary>Compiles a NodeScriptGraph into bytecode instructions.</summary>
    public sealed class NodeScriptBytecodeCompiler
    {
        private NodeScriptBytecode _bytecode = new();
        private NodeScriptGraph _graph = null!;
        private Dictionary<Guid, int> _nodeIndices = new();
        private int _nodeCounter;

        public NodeScriptBytecode Compile(NodeScriptGraph graph)
        {
            _bytecode = new NodeScriptBytecode();
            _graph = graph;
            _nodeIndices.Clear();
            _nodeCounter = 0;

            graph.RebuildPinIndex();

            // Register variables
            for (int i = 0; i < graph.Variables.Count; i++)
                _bytecode.VariableIndices[graph.Variables[i].Name] = i;

            // Emit event entry points
            foreach (var entry in graph.GetEventEntries())
                CompileEventChain(entry);

            return _bytecode;
        }

        private void CompileEventChain(EventNodeBase entry)
        {
            int entryIdx = GetNodeIndex(entry);
            _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.PushFrame) { OperandA = entryIdx });
            _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.LoadConst) { FloatOperand = 0f, StringOperand = entry.EventName });
            CompileExecChain(entry, "Then");
            _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.PopFrame));
        }

        private void CompileExecChain(NodeScriptNode node, string outputPinName)
        {
            // Emit node-specific instructions
            EmitNodeInstructions(node);

            // Follow exec output
            int outIdx = -1;
            for (int i = 0; i < node.OutputPins.Count; i++)
                if (node.OutputPins[i].IsExec && node.OutputPins[i].Name == outputPinName) { outIdx = i; break; }
            if (outIdx < 0) return;

            var outPin = node.OutputPins[outIdx];
            foreach (var edge in _graph.FindEdgesFrom(outPin.Id))
            {
                var next = _graph.FindNodeByPin(edge.ToPinId);
                if (next != null) CompileExecChain(next, "Then");
            }
        }

        private void EmitNodeInstructions(NodeScriptNode node)
        {
            int idx = GetNodeIndex(node);
            switch (node)
            {
                case PrintNode pn:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Print) { OperandA = idx }); break;
                case LogNode ln:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Log) { OperandA = idx, StringOperand = ln.Metadata.GetValueOrDefault("Level", "Info") }); break;
                case BreakpointNode:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Breakpoint) { OperandA = idx }); break;
                case SetVariableNode svn:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.StoreVar) { OperandA = idx, StringOperand = svn.Metadata.GetValueOrDefault("VariableName", "") }); break;
                case GetVariableNode gvn:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.LoadVar) { OperandA = idx, StringOperand = gvn.Metadata.GetValueOrDefault("VariableName", "") }); break;
                case SetGlobalVariableNode sgvn:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.StoreGlobal) { OperandA = idx, StringOperand = sgvn.Metadata.GetValueOrDefault("VariableName", "") }); break;
                case GetGlobalVariableNode ggvn:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.LoadGlobal) { OperandA = idx, StringOperand = ggvn.Metadata.GetValueOrDefault("VariableName", "") }); break;
                case IfNode ifn:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Branch) { OperandA = idx }); break;
                case AddNode: _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Add) { OperandA = idx }); break;
                case SubNode: _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Sub) { OperandA = idx }); break;
                case MulNode: _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Mul) { OperandA = idx }); break;
                case DivNode: _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.Div) { OperandA = idx }); break;
                case SpawnEntityNode: _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.SpawnEntity) { OperandA = idx }); break;
                case DestroyEntityNode: _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.DestroyEntity) { OperandA = idx }); break;
                default:
                    _bytecode.Emit(new NodeScriptInstruction(NodeScriptOpCode.CallExec) { OperandA = idx }); break;
            }
        }

        private int GetNodeIndex(NodeScriptNode node)
        {
            if (!_nodeIndices.TryGetValue(node.Id, out var idx))
            { idx = _nodeCounter++; _nodeIndices[node.Id] = idx; }
            return idx;
        }
    }

    /// <summary>NodeScript Virtual Machine: interprets compiled bytecode.</summary>
    public sealed class NodeScriptVM
    {
        private NodeScriptBytecode _bytecode = null!;
        private NodeScriptGraph _graph = null!;
        private readonly Stack<object?> _stack = new();
        private readonly Dictionary<string, object?> _variables = new();
        private readonly Dictionary<string, object?> _globalVariables;
        private EntityComponentSystem _ecs = null!;
        private int _selfEntity;
        private float _deltaTime;
        private int _ip; // Instruction pointer

        public Dictionary<string, object?> GlobalVariables => _globalVariables;
        public int InstructionPointer => _ip;

        public NodeScriptVM(Dictionary<string, object?>? globalVars = null)
        {
            _globalVariables = globalVars ?? new Dictionary<string, object?>();
        }

        public void Load(NodeScriptBytecode bytecode, NodeScriptGraph graph)
        {
            _bytecode = bytecode; _graph = graph;
        }

        public void RunEvent(string eventName, EntityComponentSystem ecs, int entity, float dt, Dictionary<string, object?>? instanceVars = null)
        {
            _ecs = ecs; _selfEntity = entity; _deltaTime = dt;
            if (instanceVars != null) foreach (var kv in instanceVars) _variables[kv.Key] = kv.Value;
            // Initialize variables to defaults
            foreach (var v in _graph.Variables)
                if (!_variables.ContainsKey(v.Name)) _variables[v.Name] = v.GetDefaultValue();
            _ip = 0;
            Execute();
        }

        private void Execute()
        {
            while (_ip < _bytecode.InstructionCount)
            {
                var inst = _bytecode[_ip];
                _ip++;
                switch (inst.OpCode)
                {
                    case NodeScriptOpCode.Nop: break;
                    case NodeScriptOpCode.Halt: return;
                    case NodeScriptOpCode.PushFrame: break;
                    case NodeScriptOpCode.PopFrame: return;
                    case NodeScriptOpCode.LoadConst: _stack.Push(_bytecode.Constants.GetValueOrDefault(inst.StringOperand)); break;
                    case NodeScriptOpCode.LoadVar: _stack.Push(_variables.GetValueOrDefault(inst.StringOperand)); break;
                    case NodeScriptOpCode.StoreVar: _variables[inst.StringOperand] = _stack.Count > 0 ? _stack.Pop() : null; break;
                    case NodeScriptOpCode.LoadGlobal: _stack.Push(_globalVariables.GetValueOrDefault(inst.StringOperand)); break;
                    case NodeScriptOpCode.StoreGlobal: _globalVariables[inst.StringOperand] = _stack.Count > 0 ? _stack.Pop() : null; break;
                    case NodeScriptOpCode.Print: NodeScriptLog.Log(NodeScriptTypeUtils.FormatValue(_stack.Count > 0 ? _stack.Pop() : null)); break;
                    case NodeScriptOpCode.Log: NodeScriptLog.Log($"[{inst.StringOperand}] {NodeScriptTypeUtils.FormatValue(_stack.Count > 0 ? _stack.Pop() : null)}"); break;
                    case NodeScriptOpCode.Breakpoint: NodeScriptLog.Log($"Breakpoint hit @ entity {_selfEntity}"); break;
                    case NodeScriptOpCode.Branch: break; // Handled by the graph interpreter for control flow
                    case NodeScriptOpCode.CallExec: break;
                    case NodeScriptOpCode.Add: { var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(NodeScriptTypeUtils.Convert(a, NodeScriptDataType.Float) is float af && NodeScriptTypeUtils.Convert(b, NodeScriptDataType.Float) is float bf ? af + bf : 0f); break; }
                    case NodeScriptOpCode.Sub: { var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(NodeScriptTypeUtils.Convert(a, NodeScriptDataType.Float) is float af && NodeScriptTypeUtils.Convert(b, NodeScriptDataType.Float) is float bf ? af - bf : 0f); break; }
                    case NodeScriptOpCode.Mul: { var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(NodeScriptTypeUtils.Convert(a, NodeScriptDataType.Float) is float af && NodeScriptTypeUtils.Convert(b, NodeScriptDataType.Float) is float bf ? af * bf : 0f); break; }
                    case NodeScriptOpCode.Div: { var b = _stack.Pop(); var a = _stack.Pop(); if (NodeScriptTypeUtils.Convert(a, NodeScriptDataType.Float) is float af && NodeScriptTypeUtils.Convert(b, NodeScriptDataType.Float) is float bf) _stack.Push(bf == 0 ? 0f : af / bf); else _stack.Push(0f); break; }
                    case NodeScriptOpCode.SpawnEntity: { string name = NodeScriptTypeUtils.Convert(_stack.Pop(), NodeScriptDataType.String) as string ?? "Entity"; _stack.Push(_ecs.CreateEntity(name, Vector3.Zero, Quaternion.Identity, Vector3.One)); break; }
                    case NodeScriptOpCode.DestroyEntity: { int e = NodeScriptTypeUtils.Convert(_stack.Pop(), NodeScriptDataType.Int) as int? ?? 0; _ecs.DestroyEntity(e); break; }
                    case NodeScriptOpCode.Return: return;
                    default: break;
                }
                if (_ip > 1_000_000) { NodeScriptLog.Log("VM: Step limit exceeded."); return; }
            }
        }

        public object? GetVariable(string name) => _variables.GetValueOrDefault(name);
        public void SetVariable(string name, object? value) => _variables[name] = value;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 10: NodeScript Compiler (Optimizer)
    // ─────────────────────────────────────────────────────────────────────

    public sealed class NodeScriptDiagnostic { public string Severity = "Info"; public string Message = string.Empty; public Guid? NodeId; public override string ToString() => $"[{Severity}] {Message}"; }
    public sealed class CompiledNodeScript { public bool Success; public List<NodeScriptDiagnostic> Diagnostics = new(); public NodeScriptGraph OptimizedGraph = new(); public string? GeneratedCSharp; public NodeScriptBytecode? Bytecode; public TimeSpan CompileTime; }
    public interface ICompiledNodeScript { void Execute(EntityComponentSystem ecs, int entity, float dt, string eventName); }

    public sealed class NodeScriptCompiler
    {
        public bool EnableConstantFolding = true;
        public bool EnableDeadCodeElimination = true;
        public bool GenerateCSharp = false;
        public bool GenerateBytecode = true;

        public CompiledNodeScript Compile(NodeScriptGraph graph)
        {
            var sw = Stopwatch.StartNew();
            var result = new CompiledNodeScript();
            graph.RebuildPinIndex();
            TypeCheck(graph, result.Diagnostics);
            var working = CloneGraph(graph);
            working.RebuildPinIndex();
            if (EnableConstantFolding) ConstantFolding(working, result.Diagnostics);
            if (EnableDeadCodeElimination) DeadCodeElimination(working, result.Diagnostics);
            working.RebuildPinIndex();
            result.OptimizedGraph = working;
            if (GenerateCSharp) result.GeneratedCSharp = GenerateRoslynCSharp(working);
            if (GenerateBytecode) result.Bytecode = new NodeScriptBytecodeCompiler().Compile(working);
            result.Success = result.Diagnostics.All(d => d.Severity != "Error");
            sw.Stop(); result.CompileTime = sw.Elapsed;
            return result;
        }

        private void TypeCheck(NodeScriptGraph graph, List<NodeScriptDiagnostic> diags)
        {
            foreach (var edge in graph.Edges)
            {
                var fromNode = graph.FindNodeByPin(edge.FromPinId); var toNode = graph.FindNodeByPin(edge.ToPinId);
                if (fromNode == null || toNode == null) { diags.Add(new NodeScriptDiagnostic { Severity = "Error", Message = "Edge references non-existent pin." }); continue; }
                var fromPin = fromNode.FindPin(edge.FromPinId)!; var toPin = toNode.FindPin(edge.ToPinId)!;
                if (fromPin.IsExec != toPin.IsExec) { diags.Add(new NodeScriptDiagnostic { Severity = "Error", Message = $"Exec pin connected to data pin ({fromNode.Title} -> {toNode.Title}).", NodeId = toNode.Id }); continue; }
                if (!fromPin.IsExec && !NodeScriptTypeUtils.AreCompatible(fromPin.DataType, toPin.DataType)) diags.Add(new NodeScriptDiagnostic { Severity = "Error", Message = $"Type mismatch: {fromPin.DataType} -> {toPin.DataType} ({fromNode.Title} -> {toNode.Title}).", NodeId = toNode.Id });
                else if (!fromPin.IsExec && NodeScriptTypeUtils.IsNarrowing(fromPin.DataType, toPin.DataType)) diags.Add(new NodeScriptDiagnostic { Severity = "Warning", Message = $"Narrowing conversion: {fromPin.DataType} -> {toPin.DataType}.", NodeId = toNode.Id });
            }
            var groups = graph.GetEventEntries().GroupBy(e => e.EventName).Where(g => g.Count() > 1);
            foreach (var g in groups) diags.Add(new NodeScriptDiagnostic { Severity = "Warning", Message = $"Event '{g.Key}' has {g.Count()} entry nodes." });
        }

        private void ConstantFolding(NodeScriptGraph graph, List<NodeScriptDiagnostic> diags)
        {
            var interpreter = new NodeScriptInterpreter();
            var ctx = new NodeScriptExecutionContext { Graph = graph, Interpreter = interpreter };
            graph.RebuildPinIndex();
            bool changed = true; int passes = 0;
            while (changed && passes++ < 8) { changed = false; foreach (var node in graph.Nodes.Values.ToList()) { if (!node.IsPure || node.OutputPins.Count == 0) continue; bool allLiteral = node.InputPins.All(p => p.IsExec || graph.FindEdgeTo(p.Id) == null); if (!allLiteral) continue; try { ctx.PinCache.Clear(); node.EvaluateOutputs(ctx); foreach (var op in node.OutputPins) if (ctx.PinCache.TryGetValue(op.Id, out var val)) op.DefaultValue = val; graph.Edges.RemoveAll(e => node.InputPins.Any(p => p.Id == e.ToPinId)); diags.Add(new NodeScriptDiagnostic { Severity = "Info", Message = $"Constant folded: {node.Title}.", NodeId = node.Id }); changed = true; } catch { } } }
        }

        private void DeadCodeElimination(NodeScriptGraph graph, List<NodeScriptDiagnostic> diags)
        {
            var reachable = new HashSet<Guid>(); var stack = new Stack<NodeScriptNode>();
            foreach (var entry in graph.GetEventEntries()) { reachable.Add(entry.Id); stack.Push(entry); }
            while (stack.Count > 0) { var node = stack.Pop(); foreach (var op in node.OutputPins.Where(p => p.IsExec)) foreach (var edge in graph.FindEdgesFrom(op.Id)) { var next = graph.FindNodeByPin(edge.ToPinId); if (next != null && reachable.Add(next.Id)) stack.Push(next); } }
            bool changed = true;
            while (changed) { changed = false; foreach (var nodeId in reachable.ToList()) { var node = graph.Nodes[nodeId]; foreach (var ip in node.InputPins.Where(p => !p.IsExec)) { var edge = graph.FindEdgeTo(ip.Id); if (edge == null) continue; var src = graph.FindNodeByPin(edge.FromPinId); if (src != null && reachable.Add(src.Id)) changed = true; } } }
            var toRemove = graph.Nodes.Keys.Where(id => !reachable.Contains(id)).ToList();
            foreach (var id in toRemove) { graph.RemoveNodeWithEdges(id); diags.Add(new NodeScriptDiagnostic { Severity = "Info", Message = "Dead code eliminated: unreachable node removed." }); }
        }

        public string GenerateRoslynCSharp(NodeScriptGraph graph)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated by ShanyuEngine NodeScriptCompiler");
            sb.AppendLine("using System; using System.Numerics; using ShanyuEngine.ECS; using ShanyuEngine.Scripting;");
            sb.AppendLine($"namespace ShanyuEngine.Scripting.Generated {{ public sealed class bp_{Sanitize(graph.Name)} : ICompiledNodeScript {{");
            foreach (var v in graph.Variables) sb.AppendLine($"    // var {v.Name} : {v.DataType}");
            sb.AppendLine("    public void Execute(EntityComponentSystem ecs, int entity, float dt, string eventName) { switch(eventName) {");
            foreach (var grp in graph.GetEventEntries().GroupBy(e => e.EventName)) { sb.AppendLine($"    case \"{grp.Key}\":"); foreach (var entry in grp) EmitChain(sb, graph, entry, 3); sb.AppendLine("    break;"); }
            sb.AppendLine("    } } } }");
            return sb.ToString();
        }

        private void EmitChain(StringBuilder sb, NodeScriptGraph graph, NodeScriptNode node, int depth, HashSet<Guid>? visited = null)
        {
            visited ??= new HashSet<Guid>();
            if (!visited.Add(node.Id)) { sb.Append(' ', depth * 4).AppendLine($"// (cycle detected) {node.Title}"); return; }
            sb.Append(' ', depth * 4).AppendLine(EmitStatement(graph, node));
            var execOut = node.OutputPins.FirstOrDefault(p => p.IsExec);
            if (execOut != null) foreach (var edge in graph.FindEdgesFrom(execOut.Id)) { var next = graph.FindNodeByPin(edge.ToPinId); if (next != null) EmitChain(sb, graph, next, depth, visited); }
        }

        private string EmitStatement(NodeScriptGraph graph, NodeScriptNode node) => node switch
        {
            PrintNode => $"Console.WriteLine(\"{{0}}\", {EmitInput(graph, node, "Message")});",
            SetVariableNode => $"__var_{node.Metadata.GetValueOrDefault("VariableName")} = {EmitInput(graph, node, "Value")};",
            IfNode => $"if ({EmitInput(graph, node, "Condition")}) {{ /* True */ }} else {{ /* False */ }}",
            _ => $"/* {node.Title} */;"
        };

        private string EmitInput(NodeScriptGraph graph, NodeScriptNode node, string pinName)
        {
            var pin = node.FindInput(pinName); if (pin == null) return "default";
            var edge = graph.FindEdgeTo(pin.Id); if (edge == null) return EmitLiteral(pin.DefaultValue, pin.DataType);
            var src = graph.FindNodeByPin(edge.FromPinId); if (src == null) return "default";
            return src switch { GetVariableNode gv => $"__var_{gv.Metadata.GetValueOrDefault("VariableName")}", AddNode => $"({EmitInput(graph, src, "A")} + {EmitInput(graph, src, "B")})", SubNode => $"({EmitInput(graph, src, "A")} - {EmitInput(graph, src, "B")})", _ => "/*expr*/" };
        }

        private string EmitLiteral(object? value, NodeScriptDataType dt) => dt switch { NodeScriptDataType.Bool => (value is bool b && b) ? "true" : "false", NodeScriptDataType.Float => $"{Convert.ToSingle(value, CultureInfo.InvariantCulture):R}f", NodeScriptDataType.String => $"\"{value}\"", _ => "default" };
        private static string Sanitize(string name) => string.IsNullOrEmpty(name) ? "Untitled" : new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        private NodeScriptGraph CloneGraph(NodeScriptGraph src)
        {
            var g = new NodeScriptGraph { Name = src.Name, Description = src.Description, ParentNodeScript = src.ParentNodeScript };
            var idMap = new Dictionary<Guid, Guid>();
            foreach (var node in src.Nodes.Values) { var copy = node.Clone(); idMap[node.Id] = copy.Id; g.AddNode(copy); }
            src.RebuildPinIndex(); g.RebuildPinIndex();
            foreach (var edge in src.Edges)
            {
                var srcNode = src.FindNodeByPin(edge.FromPinId); var dstNode = src.FindNodeByPin(edge.ToPinId);
                if (srcNode == null || dstNode == null) continue;
                if (!idMap.TryGetValue(srcNode.Id, out var newSrcId) || !idMap.TryGetValue(dstNode.Id, out var newDstId)) continue;
                var newSrcNode = g.FindNode(newSrcId)!; var newDstNode = g.FindNode(newDstId)!;
                var srcPinName = srcNode.FindPin(edge.FromPinId)?.Name; var dstPinName = dstNode.FindPin(edge.ToPinId)?.Name;
                var newFrom = srcPinName != null ? newSrcNode.FindOutput(srcPinName)?.Id : Guid.Empty;
                var newTo = dstPinName != null ? newDstNode.FindInput(dstPinName)?.Id : Guid.Empty;
                if (newFrom != Guid.Empty && newTo != Guid.Empty) g.Connect(newFrom, newTo);
            }
            foreach (var v in src.Variables) g.Variables.Add(v.Clone());
            return g;
        }

        public CompileResult? CompileWithScriptEngine(NodeScriptGraph graph, ScriptEngine engine) { var src = GenerateRoslynCSharp(graph); return engine.CompileSource(src, "bp_" + Sanitize(graph.Name), "GeneratedNodeScript.cs"); }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 11: ECS Component and Execution System
    // ─────────────────────────────────────────────────────────────────────

    public struct NodeScriptComponent : IComponent
    {
        public string NodeScriptName;
        public Dictionary<string, object?> InstanceVariables;
        public bool IsEnabled;
        public bool HasBegun;
    }

    public sealed class NodeScriptExecutionSystem : ISystem
    {
        public string Name => "NodeScriptExecutionSystem";
        private readonly NodeScriptInterpreter _interpreter;
        private readonly NodeScriptRegistry _registry;
        private readonly HashSet<int> _begun = new();
        private readonly List<(DelayNode node, NodeScriptExecutionContext ctx)> _activeDelays = new();
        private readonly List<(TimelineNode node, NodeScriptExecutionContext ctx)> _activeTimelines = new();

        public NodeScriptExecutionSystem(NodeScriptInterpreter interpreter, NodeScriptRegistry registry)
        {
            _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public void Update(float dt, EntityComponentSystem ecs)
        {
            var entities = ecs.QueryWithComponent<NodeScriptComponent>();
            foreach (var entity in entities)
            {
                var comp = ecs.GetComponent<NodeScriptComponent>(entity);
                if (!comp.IsEnabled) continue;
                var graph = _registry.Find(comp.NodeScriptName);
                if (graph == null) continue;
                comp.InstanceVariables ??= CreateInstanceVariables(graph);
                ecs.SetComponent(entity, comp);

                if (!_begun.Contains(entity))
                {
                    _begun.Add(entity);
                    _interpreter.RunEvent(graph, comp.InstanceVariables, ecs, entity, "OnBegin", dt);
                    _interpreter.RunEvent(graph, comp.InstanceVariables, ecs, entity, "OnSpawn", dt);
                }
                _interpreter.RunEvent(graph, comp.InstanceVariables, ecs, entity, "OnTick", dt);

                // Tick active delay and timeline nodes
                TickDelayNodes(graph, comp.InstanceVariables, ecs, entity, dt);
                TickTimelineNodes(graph, comp.InstanceVariables, ecs, entity, dt);
            }
        }

        private void TickDelayNodes(NodeScriptGraph graph, Dictionary<string, object?> vars, EntityComponentSystem ecs, int entity, float dt)
        {
            foreach (var node in graph.Nodes.Values.OfType<DelayNode>())
            {
                var ctx = new NodeScriptExecutionContext { Graph = graph, Interpreter = _interpreter, Ecs = ecs, SelfEntity = entity, DeltaTime = dt, Variables = vars, Attributes = _interpreter.GlobalAttributes, GlobalVariables = _interpreter.GlobalVariables };
                node.Tick(dt, ctx);
            }
        }

        private void TickTimelineNodes(NodeScriptGraph graph, Dictionary<string, object?> vars, EntityComponentSystem ecs, int entity, float dt)
        {
            foreach (var node in graph.Nodes.Values.OfType<TimelineNode>())
            {
                var ctx = new NodeScriptExecutionContext { Graph = graph, Interpreter = _interpreter, Ecs = ecs, SelfEntity = entity, DeltaTime = dt, Variables = vars, Attributes = _interpreter.GlobalAttributes, GlobalVariables = _interpreter.GlobalVariables };
                node.Tick(dt, ctx);
            }
        }

        public void TriggerEvent(EntityComponentSystem ecs, int entity, string eventName, Dictionary<string, object?>? args = null, float dt = 0f)
        {
            if (!ecs.HasComponent<NodeScriptComponent>(entity)) return;
            var comp = ecs.GetComponent<NodeScriptComponent>(entity);
            if (!comp.IsEnabled) return;
            var graph = _registry.Find(comp.NodeScriptName);
            if (graph == null) return;
            comp.InstanceVariables ??= CreateInstanceVariables(graph);
            _interpreter.RunEvent(graph, comp.InstanceVariables, ecs, entity, eventName, dt, args);
        }

        public void NotifyDestroyed(EntityComponentSystem ecs, int entity) { TriggerEvent(ecs, entity, "OnDestroy"); _begun.Remove(entity); }

        public static Dictionary<string, object?> CreateInstanceVariables(NodeScriptGraph graph) { var dict = new Dictionary<string, object?>(); foreach (var v in graph.Variables) dict[v.Name] = v.GetDefaultValue(); return dict; }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 12: NodeScript Registry (JSON Persistence)
    // ─────────────────────────────────────────────────────────────────────

    public sealed class NodeScriptRegistry
    {
        private readonly Dictionary<string, NodeScriptGraph> _nodescripts = new(StringComparer.Ordinal);
        public int Count => _nodescripts.Count;

        public void Register(NodeScriptGraph graph) { if (graph == null) throw new ArgumentNullException(nameof(graph)); if (string.IsNullOrEmpty(graph.Name)) throw new ArgumentException("NodeScript name cannot be empty."); _nodescripts[graph.Name] = graph; }
        public bool Unregister(string name) => _nodescripts.Remove(name);
        public NodeScriptGraph? Find(string name) => _nodescripts.TryGetValue(name, out var g) ? g : null;
        public bool Contains(string name) => _nodescripts.ContainsKey(name);
        public IEnumerable<string> GetAllNames() => _nodescripts.Keys;
        public IEnumerable<NodeScriptGraph> GetAll() => _nodescripts.Values;
        public void Clear() => _nodescripts.Clear();

        public string SaveToJson(string name) { var graph = Find(name) ?? throw new KeyNotFoundException($"NodeScript '{name}' not found."); return SerializeGraph(graph).ToString(); }
        public string LoadFromJson(string json) { var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Invalid nodescript JSON."); var graph = DeserializeGraph(node.AsObject()); Register(graph); return graph.Name; }
        public string SaveAllToJson() { var arr = new JsonArray(); foreach (var g in _nodescripts.Values) arr.Add(SerializeGraph(g)); return arr.ToString(); }
        public int LoadAllFromJson(string json) { var arr = JsonNode.Parse(json)?.AsArray() ?? throw new InvalidOperationException("Invalid nodescript collection JSON."); int n = 0; foreach (var item in arr) { if (item == null) continue; var graph = DeserializeGraph(item.AsObject()); Register(graph); n++; } return n; }

        public bool SaveToJsonFile(string name, string filePath) { try { var dir = Path.GetDirectoryName(filePath); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.WriteAllText(filePath, SaveToJson(name), Encoding.UTF8); return true; } catch { return false; } }
        public string LoadFromJsonFile(string filePath) { if (!File.Exists(filePath)) throw new FileNotFoundException($"NodeScript file not found: {filePath}"); return LoadFromJson(File.ReadAllText(filePath, Encoding.UTF8)); }

        private static JsonObject SerializeGraph(NodeScriptGraph graph)
        {
            var obj = new JsonObject { ["name"] = graph.Name, ["description"] = graph.Description, ["parent"] = graph.ParentNodeScript };
            var vars = new JsonArray();
            foreach (var v in graph.Variables) vars.Add(new JsonObject { ["id"] = v.Id.ToString(), ["name"] = v.Name, ["dataType"] = v.DataType.ToString(), ["default"] = v.DefaultValueText, ["editable"] = v.IsEditable, ["instanced"] = v.IsInstanced, ["category"] = v.Category, ["description"] = v.Description, ["isArray"] = v.IsArray });
            obj["variables"] = vars;
            var nodes = new JsonArray();
            foreach (var n in graph.Nodes.Values)
            {
                var pins = new JsonArray();
                foreach (var p in n.InputPins.Concat(n.OutputPins)) pins.Add(new JsonObject { ["id"] = p.Id.ToString(), ["name"] = p.Name, ["dir"] = p.Direction.ToString(), ["type"] = p.DataType.ToString(), ["owner"] = p.OwnerNodeId.ToString(), ["subType"] = p.SubType });
                var meta = new JsonObject(); foreach (var kv in n.Metadata) meta[kv.Key] = kv.Value;
                nodes.Add(new JsonObject { ["id"] = n.Id.ToString(), ["type"] = NodeScriptNodeCatalog.GetKey(n), ["title"] = n.Title, ["category"] = n.Category.ToString(), ["x"] = n.PositionX, ["y"] = n.PositionY, ["pins"] = pins, ["metadata"] = meta });
            }
            obj["nodes"] = nodes;
            var edges = new JsonArray();
            foreach (var e in graph.Edges) edges.Add(new JsonObject { ["id"] = e.Id.ToString(), ["from"] = e.FromPinId.ToString(), ["to"] = e.ToPinId.ToString(), ["type"] = e.ResolvedType.ToString() });
            obj["edges"] = edges;
            return obj;
        }

        private static NodeScriptGraph DeserializeGraph(JsonObject obj)
        {
            NodeScriptNodeCatalog.Initialize();
            var graph = new NodeScriptGraph { Name = obj["name"]?.GetValue<string>() ?? "Untitled", Description = obj["description"]?.GetValue<string>(), ParentNodeScript = obj["parent"]?.GetValue<string>() };
            if (obj["variables"] is JsonArray vars) foreach (var v in vars) { var vo = v!.AsObject(); graph.Variables.Add(new NodeScriptVariable { Id = Guid.Parse(vo["id"]!.GetValue<string>()), Name = vo["name"]!.GetValue<string>(), DataType = Enum.Parse<NodeScriptDataType>(vo["dataType"]!.GetValue<string>()), DefaultValueText = vo["default"]?.GetValue<string>(), IsEditable = vo["editable"]?.GetValue<bool>() ?? true, IsInstanced = vo["instanced"]?.GetValue<bool>() ?? true, Category = vo["category"]?.GetValue<string>() ?? string.Empty, Description = vo["description"]?.GetValue<string>(), IsArray = vo["isArray"]?.GetValue<bool>() ?? false }); }
            if (obj["nodes"] is JsonArray nodes) foreach (var n in nodes) { var no = n!.AsObject(); var key = no["type"]!.GetValue<string>(); var node = NodeScriptNodeCatalog.Create(key); if (node == null) continue; node.Id = Guid.Parse(no["id"]!.GetValue<string>()); node.Title = no["title"]?.GetValue<string>() ?? node.Title; node.PositionX = no["x"]?.GetValue<float>() ?? 0; node.PositionY = no["y"]?.GetValue<float>() ?? 0; if (no["metadata"] is JsonObject meta) foreach (var kv in meta) if (kv.Value != null) node.Metadata[kv.Key] = kv.Value.GetValue<string>(); if (no["pins"] is JsonArray pins) { foreach (var p in pins) { var po = p!.AsObject(); var pinName = po["name"]?.GetValue<string>(); if (pinName == null) continue; var pin = node.InputPins.FirstOrDefault(pp => pp.Name == pinName) ?? node.OutputPins.FirstOrDefault(pp => pp.Name == pinName); if (pin != null) pin.SubType = po["subType"]?.GetValue<string>(); } } graph.AddNode(node); }
            if (obj["edges"] is JsonArray edges) foreach (var e in edges) { var eo = e!.AsObject(); var from = Guid.Parse(eo["from"]!.GetValue<string>()); var to = Guid.Parse(eo["to"]!.GetValue<string>()); var edge = graph.Connect(from, to); if (edge != null && Enum.TryParse<NodeScriptDataType>(eo["type"]?.GetValue<string>(), out var rt)) edge.ResolvedType = rt; }
            graph.RebuildPinIndex();
            return graph;
        }
    }
}
