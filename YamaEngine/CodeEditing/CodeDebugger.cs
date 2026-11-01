// ==================================================================================================
//  CodeDebugger.cs — yama引擎 C# 脚本调试器
//
//  命名空间: YamaEngine.CodeEditing
//  依赖:     YamaEngine.Scripting (ScriptRuntimeManager / ScriptInstance), System.Reflection
//
//  功能:
//    1. Breakpoint        — 断点 (文件/行/条件/命中次数/启用)
//    2. DebugSession      — 调试会话 (附加/断点管理/继续/单步/暂停/停止/调用栈/局部变量/表达式求值)
//    3. VariableInfo      — 变量信息
//    4. StackFrame        — 栈帧
//    5. WatchWindow       — 监视表达式窗口
//    6. ImmediateWindow   — 即时窗口
//
//  调试模型:
//    宿主在 ScriptRuntimeManager.Tick 循环中, 于每个 ScriptInstance.OnUpdate 前后调用
//    DebugSession.OnBeforeUpdate / OnAfterUpdate 以检查断点。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using YamaEngine.Scripting;

namespace YamaEngine.CodeEditing
{
    // ════════════════════════════════════════════════════════════════════════════
    //  断点与状态
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>断点。</summary>
    public sealed class Breakpoint
    {
        public string File { get; init; } = string.Empty;
        public int Line { get; init; }
        public string Condition { get; set; } = string.Empty;
        public int HitCountTarget { get; set; }
        public int CurrentHitCount { get; internal set; }
        public bool Enabled { get; set; } = true;
        public bool IsConditional => !string.IsNullOrWhiteSpace(Condition);
        public string LastError { get; internal set; } = string.Empty;
        public override string ToString() => $"{System.IO.Path.GetFileName(File)}:{Line}" + (IsConditional ? $" [{Condition}]" : "");
    }

    public enum DebugState { Detached, Running, Paused, Stopped }
    public enum DebugBreakMode { None, Breakpoint, Conditional, HitCount, StepOver, StepInto, StepOut, ManualPause }

    // ════════════════════════════════════════════════════════════════════════════
    //  变量与栈帧
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class VariableInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool HasChildren { get; init; }
        public List<VariableInfo> Children { get; } = new();
        public override string ToString() => $"{Name} ({Type}) = {Value}";
    }

    public sealed class StackFrame
    {
        public string Function { get; init; } = string.Empty;
        public string File { get; init; } = string.Empty;
        public int Line { get; init; }
        public List<VariableInfo> Variables { get; init; } = new();
        public override string ToString() => $"{Function}  [{File}:{Line}]";
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  WatchWindow — 监视窗口
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class WatchExpression
    {
        public string Expression { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public bool IsValid { get; set; }
        public string LastError { get; set; } = string.Empty;
    }

    public sealed class WatchWindow
    {
        private readonly List<WatchExpression> _watches = new();
        public IReadOnlyList<WatchExpression> Watches => _watches;
        public void Add(string expression) { if (!string.IsNullOrWhiteSpace(expression) && !_watches.Any(w => w.Expression == expression)) _watches.Add(new WatchExpression { Expression = expression.Trim() }); }
        public bool Remove(string expression) => _watches.RemoveAll(w => w.Expression == expression) > 0;
        public void Clear() => _watches.Clear();
        public void EvaluateAll(object? instance) { foreach (var w in _watches) EvaluateWatch(w, instance); }
        private static void EvaluateWatch(WatchExpression w, object? instance)
        {
            try { var val = DebugEvaluator.GetValue(instance, w.Expression); w.Value = DebugEvaluator.Format(val); w.TypeName = val?.GetType().Name ?? "null"; w.IsValid = true; w.LastError = string.Empty; }
            catch (Exception ex) { w.IsValid = false; w.LastError = ex.Message; w.Value = "<error>"; }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ImmediateWindow — 即时窗口
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class ImmediateWindow
    {
        private readonly List<string> _history = new();
        public IReadOnlyList<string> History => _history;
        public string Execute(string input, object? instance)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            _history.Add(input);
            input = input.Trim();
            int eq = IndexOfAssignment(input);
            if (eq > 0)
            {
                var left = input.Substring(0, eq).Trim();
                var right = input.Substring(eq + 1).Trim();
                if (instance != null && DebugEvaluator.TrySetValue(instance, left, right)) return $"{left} = {right}";
                return $"无法赋值: {left}";
            }
            try { var val = DebugEvaluator.GetValue(instance, input); return DebugEvaluator.Format(val); }
            catch (Exception ex) { return $"错误: {ex.Message}"; }
        }
        public void ClearHistory() => _history.Clear();
        private static int IndexOfAssignment(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '=')
                {
                    char prev = i > 0 ? s[i - 1] : '\0';
                    char next = i + 1 < s.Length ? s[i + 1] : '\0';
                    if (prev is '>' or '<' or '!' or '=') continue;
                    if (next == '=') continue;
                    return i;
                }
            return -1;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  DebugSession — 调试会话
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class DebugSession
    {
        private readonly List<Breakpoint> _breakpoints = new();
        private ScriptInstance? _currentInstance;
        private int _currentLine;
        private DebugBreakMode _stepMode = DebugBreakMode.None;

        public DebugState State { get; private set; } = DebugState.Detached;
        public DebugBreakMode BreakMode { get; private set; } = DebugBreakMode.None;
        public ScriptInstance? CurrentInstance => _currentInstance;
        public int CurrentLine => _currentLine;
        public WatchWindow Watches { get; } = new();
        public ImmediateWindow Immediate { get; } = new();
        public IReadOnlyList<Breakpoint> Breakpoints => _breakpoints;

        public event EventHandler? BreakpointHit;
        public event EventHandler<DebugState>? StateChanged;

        public void Attach()
        {
            _breakpoints.Clear();
            SetState(DebugState.Running);
            _stepMode = DebugBreakMode.None;
        }

        public void Detach()
        {
            _currentInstance = null;
            _stepMode = DebugBreakMode.None;
            SetState(DebugState.Detached);
        }

        public Breakpoint SetBreakpoint(string file, int line, string? condition = null, int hitCountTarget = 0)
        {
            var bp = new Breakpoint { File = file, Line = line, Condition = condition ?? string.Empty, HitCountTarget = hitCountTarget, Enabled = true };
            _breakpoints.Add(bp);
            return bp;
        }

        public bool RemoveBreakpoint(Breakpoint bp) => _breakpoints.Remove(bp);

        public Breakpoint? ToggleBreakpoint(string file, int line)
        {
            var bp = _breakpoints.FirstOrDefault(b => b.File == file && b.Line == line);
            if (bp != null) { _breakpoints.Remove(bp); return null; }
            return SetBreakpoint(file, line);
        }

        public void ClearBreakpoints() => _breakpoints.Clear();
        public IReadOnlyList<Breakpoint> GetBreakpoints(string file) => _breakpoints.Where(b => b.File == file).ToList();

        public void Continue() { if (State == DebugState.Paused) { _stepMode = DebugBreakMode.None; SetState(DebugState.Running); } }
        public void StepOver() { if (State == DebugState.Paused) { _stepMode = DebugBreakMode.StepOver; SetState(DebugState.Running); } }
        public void StepInto() { if (State == DebugState.Paused) { _stepMode = DebugBreakMode.StepInto; SetState(DebugState.Running); } }
        public void StepOut() { if (State == DebugState.Paused) { _stepMode = DebugBreakMode.StepOut; SetState(DebugState.Running); } }
        public void Pause() { if (State == DebugState.Running) { _stepMode = DebugBreakMode.ManualPause; SetState(DebugState.Paused); } }
        public void Stop() { _currentInstance = null; _stepMode = DebugBreakMode.None; SetState(DebugState.Stopped); }

        /// <summary>在 ScriptInstance.OnUpdate 之前调用, 检查是否命中断点。</summary>
        public bool OnBeforeUpdate(ScriptInstance instance, int line)
        {
            if (State != DebugState.Running && State != DebugState.Paused) return false;
            if (State == DebugState.Paused) return true; // 已暂停, 不执行
            _currentLine = line;
            if (CheckBreakpoint(instance, line, out var bp, out var mode))
            {
                _currentInstance = instance;
                BreakMode = mode;
                SetState(DebugState.Paused);
                Watches.EvaluateAll(instance.Instance);
                BreakpointHit?.Invoke(this, EventArgs.Empty);
                return true; // 命中, 阻止执行
            }
            return false; // 未命中, 允许执行
        }

        /// <summary>在 ScriptInstance.OnUpdate 之后调用, 处理单步。</summary>
        public void OnAfterUpdate(ScriptInstance instance, int line)
        {
            if (State != DebugState.Running) return;
            _currentLine = line;
            if (_stepMode == DebugBreakMode.StepOver || _stepMode == DebugBreakMode.StepInto)
            {
                _currentInstance = instance;
                BreakMode = _stepMode;
                _stepMode = DebugBreakMode.None;
                SetState(DebugState.Paused);
                Watches.EvaluateAll(instance.Instance);
                BreakpointHit?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>驱动整个 ScriptRuntimeManager 的调试更新 (替代直接 Tick)。</summary>
        public void DebugTick(ScriptRuntimeManager runtime, float deltaTime)
        {
            if (State == DebugState.Stopped || State == DebugState.Detached || State == DebugState.Paused) return;
            var instances = runtime.GetInstances();
            bool stepOut = _stepMode == DebugBreakMode.StepOut;
            foreach (var inst in instances)
            {
                if (inst.IsDestroyed || !inst.Enabled) continue;
                if (!inst.IsStarted)
                {
                    if (OnBeforeUpdate(inst, 0)) return;
                    inst.OnStart();
                }
                // 简化: 使用 SourcePath 和默认行范围; 实际可基于 PDB 映射
                int line = GuessLine(inst);
                if (OnBeforeUpdate(inst, line)) return;
                inst.OnUpdate(deltaTime);
                OnAfterUpdate(inst, line);
                if (State == DebugState.Paused) return;
            }
            if (stepOut)
            {
                _stepMode = DebugBreakMode.None;
                BreakMode = DebugBreakMode.StepOut;
                SetState(DebugState.Paused);
                Watches.EvaluateAll(_currentInstance?.Instance);
                BreakpointHit?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool CheckBreakpoint(ScriptInstance inst, int line, out Breakpoint? bp, out DebugBreakMode mode)
        {
            bp = null; mode = DebugBreakMode.None;
            if (string.IsNullOrEmpty(inst.SourcePath)) return false;
            var candidates = _breakpoints.Where(b => b.Enabled && b.File == inst.SourcePath && b.Line == line).ToList();
            if (candidates.Count == 0) return false;
            foreach (var b in candidates)
            {
                b.CurrentHitCount++;
                if (b.IsConditional)
                {
                    if (EvaluateCondition(inst.Instance, b.Condition, out bool ok, out string err))
                    { if (ok) { if (b.HitCountTarget > 0 && b.CurrentHitCount < b.HitCountTarget) continue; bp = b; mode = DebugBreakMode.Conditional; return true; } continue; }
                    else { b.LastError = err; bp = b; mode = DebugBreakMode.Conditional; return true; }
                }
                if (b.HitCountTarget > 0 && b.CurrentHitCount < b.HitCountTarget) continue;
                bp = b; mode = DebugBreakMode.Breakpoint; return true;
            }
            return false;
        }

        private static int GuessLine(ScriptInstance inst)
        {
            // 启发式: 若源码可用, 查找 OnUpdate 方法的行号 (简化版)
            if (!string.IsNullOrEmpty(inst.SourcePath) && System.IO.File.Exists(inst.SourcePath))
            {
                var lines = System.IO.File.ReadAllLines(inst.SourcePath);
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains("OnUpdate") && lines[i].Contains("(")) return i + 1;
            }
            return 1;
        }

        public bool EvaluateCondition(object? instance, string condition, out bool result, out string error)
            => DebugEvaluator.EvaluateCondition(instance, condition, out result, out error);

        public IReadOnlyList<StackFrame> GetCallStack()
        {
            var frames = new List<StackFrame>();
            if (_currentInstance == null) return frames;
            frames.Add(new StackFrame { Function = $"{_currentInstance.GetType().Name}.OnUpdate", File = _currentInstance.SourcePath, Line = _currentLine, Variables = GetLocalVariables() });
            return frames;
        }

        public List<VariableInfo> GetLocalVariables()
        {
            if (_currentInstance?.Instance == null) return new List<VariableInfo>();
            return DebugEvaluator.GetVariables(_currentInstance.Instance);
        }

        public VariableInfo? EvaluateExpression(string expression)
        {
            if (_currentInstance?.Instance == null || string.IsNullOrWhiteSpace(expression)) return null;
            try { var val = DebugEvaluator.GetValue(_currentInstance.Instance, expression.Trim()); return new VariableInfo { Name = expression, Type = val?.GetType().Name ?? "null", Value = DebugEvaluator.Format(val), HasChildren = DebugEvaluator.HasChildren(val) }; }
            catch (Exception ex) { return new VariableInfo { Name = expression, Type = "<error>", Value = ex.Message }; }
        }

        private void SetState(DebugState newState)
        {
            if (State == newState) return;
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  DebugEvaluator — 反射求值工具
    // ════════════════════════════════════════════════════════════════════════════

    internal static class DebugEvaluator
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static object? GetValue(object? obj, string path)
        {
            if (obj == null || string.IsNullOrWhiteSpace(path)) return null;
            object? current = obj;
            foreach (var part in path.Split('.'))
            {
                if (current == null) return null;
                var t = current.GetType();
                var f = t.GetField(part, Flags); if (f != null) { current = f.GetValue(current); continue; }
                var p = t.GetProperty(part, Flags); if (p != null && p.GetIndexParameters().Length == 0) { current = p.GetValue(current); continue; }
                return null;
            }
            return current;
        }

        public static bool TrySetValue(object obj, string path, string literal)
        {
            if (obj == null || string.IsNullOrWhiteSpace(path)) return false;
            var parts = path.Split('.');
            object current = obj;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var next = GetValue(current, parts[i]);
                if (next == null) return false;
                current = next;
            }
            var last = parts[^1];
            var type = current.GetType();
            var field = type.GetField(last, Flags);
            if (field != null) { field.SetValue(current, ConvertLiteral(literal, field.FieldType)); return true; }
            var prop = type.GetProperty(last, Flags);
            if (prop != null && prop.CanWrite && prop.GetIndexParameters().Length == 0) { prop.SetValue(current, ConvertLiteral(literal, prop.PropertyType)); return true; }
            return false;
        }

        public static List<VariableInfo> GetVariables(object obj)
        {
            var result = new List<VariableInfo>();
            if (obj == null) return result;
            var t = obj.GetType();
            foreach (var f in t.GetFields(Flags))
            {
                if (f.Name.StartsWith("<")) continue;
                object? val; try { val = f.GetValue(obj); } catch { val = "<exception>"; }
                result.Add(new VariableInfo { Name = f.Name, Type = f.FieldType.Name, Value = Format(val), HasChildren = HasChildren(val) });
            }
            foreach (var p in t.GetProperties(Flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? val; try { val = p.GetValue(obj); } catch { val = "<exception>"; }
                result.Add(new VariableInfo { Name = p.Name, Type = p.PropertyType.Name, Value = Format(val), HasChildren = HasChildren(val) });
            }
            return result;
        }

        public static string Format(object? val)
        {
            if (val == null) return "null";
            if (val is string s) return $"\"{s}\"";
            if (val is char c) return $"'{c}'";
            if (val is bool b) return b ? "true" : "false";
            if (val is Enum) return val.ToString()!;
            var t = val.GetType();
            if (t.IsPrimitive || t == typeof(decimal)) return Convert.ToString(val, CultureInfo.InvariantCulture) ?? val.ToString()!;
            return $"{{{t.Name}}}";
        }

        public static bool HasChildren(object? val)
        {
            if (val == null) return false;
            var t = val.GetType();
            if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t.IsEnum) return false;
            return t.GetFields(Flags).Length > 0 || t.GetProperties(Flags).Length > 0;
        }

        public static bool EvaluateCondition(object? instance, string condition, out bool result, out string error)
        {
            result = false; error = string.Empty;
            if (string.IsNullOrWhiteSpace(condition)) { error = "条件为空"; return false; }
            condition = condition.Trim();
            string[] ops = { "==", "!=", ">=", "<=", ">", "<" };
            string? foundOp = null; int opIndex = -1;
            foreach (var op in ops)
            {
                int idx = condition.IndexOf(op, StringComparison.Ordinal);
                if (idx > 0) { foundOp = op; opIndex = idx; break; }
            }
            if (foundOp == null)
            {
                var val = GetValue(instance, condition);
                if (val is bool bv) { result = bv; return true; }
                error = $"条件 '{condition}' 不可求值为布尔值"; return false;
            }
            string left = condition.Substring(0, opIndex).Trim();
            string right = condition.Substring(opIndex + foundOp.Length).Trim();
            var leftVal = GetValue(instance, left);
            int cmp = CompareLiteral(leftVal, right);
            result = foundOp switch { "==" => cmp == 0, "!=" => cmp != 0, ">" => cmp > 0, "<" => cmp < 0, ">=" => cmp >= 0, "<=" => cmp <= 0, _ => false };
            return true;
        }

        private static int CompareLiteral(object? leftVal, string literal)
        {
            if (literal == "null") return leftVal == null ? 0 : 1;
            if (leftVal == null) return -1;
            if (leftVal is string ls) { var lit = literal.Trim('"', '\''); return string.Compare(ls, lit, StringComparison.Ordinal); }
            if (leftVal is IConvertible && double.TryParse(literal, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            { var lv = Convert.ToDouble(leftVal, CultureInfo.InvariantCulture); return lv.CompareTo(d); }
            if (leftVal is bool lb && bool.TryParse(literal, out var rb)) return lb == rb ? 0 : (lb ? 1 : -1);
            return string.Compare(Format(leftVal), literal, StringComparison.Ordinal);
        }

        private static object? ConvertLiteral(string literal, Type targetType)
        {
            if (literal == "null") return null;
            literal = literal.Trim('"', '\'');
            var tt = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (tt == typeof(string)) return literal;
            if (tt == typeof(char)) return literal.Length > 0 ? literal[0] : '\0';
            return Convert.ChangeType(literal, tt, CultureInfo.InvariantCulture);
        }
    }
}
