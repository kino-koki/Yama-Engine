using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace ShanyuEngine.VisualScript
{
    // ════════════════════════════════════════════════════════════════════════════
    //  山雨引擎 · 脚本变量系统 (Script Variable System)
    //  ────────────────────────────────────────────────────────────────────────────
    //  提供完整的脚本变量管理系统：
    //    · ScriptVariableScope — 变量作用域枚举
    //    · ScriptVariable      — 脚本变量（名称/类型/作用域/值/访问/回调）
    //    · ScriptVariableTable — 变量表（按作用域分组/查找/创建/删除/序列化）
    //    · VariableWatcher     — 变量监视器（实时值面板/类型转换/范围约束/条件触发）
    //    · VariablePersistence — 变量持久化（Save/Load/Reset）
    //    · NetworkVariableSync — 网络变量同步接口
    //  作者：山雨引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptVariableScope - 变量作用域
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚本变量作用域。
    /// </summary>
    public enum ScriptVariableScope
    {
        /// <summary>局部变量（函数/事件内有效）。</summary>
        Local,
        /// <summary>成员变量（实例级）。</summary>
        Member,
        /// <summary>全局变量（跨所有实例共享）。</summary>
        Global,
        /// <summary>实例变量（每个实例独立）。</summary>
        Instance,
        /// <summary>持久化变量（跨场景/游戏会话保存）。</summary>
        Persisted
    }

    /// <summary>
    /// 变量约束类型。
    /// </summary>
    public enum VariableConstraintType
    {
        /// <summary>无约束。</summary>
        None,
        /// <summary>范围约束（最小值-最大值）。</summary>
        Range,
        /// <summary>枚举约束（只能取预定义值）。</summary>
        Enum,
        /// <summary>正则约束（字符串匹配）。</summary>
        Regex,
        /// <summary>自定义约束。</summary>
        Custom
    }

    /// <summary>
    /// 网络同步模式。
    /// </summary>
    public enum NetworkSyncMode
    {
        /// <summary>不同步。</summary>
        None,
        /// <summary>仅服务器到客户端。</summary>
        ServerToClient,
        /// <summary>仅客户端到服务器。</summary>
        ClientToServer,
        /// <summary>双向同步。</summary>
        Bidirectional,
        /// <summary>多播同步（服务器→所有客户端）。</summary>
        Multicast
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptVariable - 脚本变量
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚本变量：带有作用域、类型检查、访问权限和值变更通知的变量。
    /// </summary>
    [Serializable]
    public class ScriptVariable
    {
        /// <summary>变量唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>变量名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>变量数据类型。</summary>
        public GraphPinDataType DataType { get; set; } = GraphPinDataType.Float;

        /// <summary>变量作用域。</summary>
        public ScriptVariableScope Scope { get; set; } = ScriptVariableScope.Member;

        /// <summary>访问权限。</summary>
        public VariableAccessLevel AccessLevel { get; set; } = VariableAccessLevel.Public;

        /// <summary>变量默认值（JSON 字符串）。</summary>
        public string? DefaultValue { get; set; }

        /// <summary>变量当前值（object 类型）。</summary>
        private object? _value;

        /// <summary>变量当前值。</summary>
        public object? Value
        {
            get => _value;
            set
            {
                var oldValue = _value;
                if (ApplyConstraints(ref value))
                {
                    _value = value;
                    OnValueChanged?.Invoke(this, oldValue, value);
                    LastModifiedTime = DateTime.UtcNow;
                }
            }
        }

        /// <summary>是否为数组。</summary>
        public bool IsArray { get; set; }

        /// <summary>数组元素类型。</summary>
        public GraphPinDataType ArrayElementType { get; set; }

        /// <summary>变量是否可序列化。</summary>
        public bool IsSerializable { get; set; } = true;

        /// <summary>变量描述。</summary>
        public string? Description { get; set; }

        /// <summary>变量分组名称（用于面板显示）。</summary>
        public string? Category { get; set; }

        /// <summary>值变更回调。</summary>
        public Action<ScriptVariable, object?, object?>? OnValueChanged { get; set; }

        /// <summary>变量约束类型。</summary>
        public VariableConstraintType ConstraintType { get; set; } = VariableConstraintType.None;

        /// <summary>约束参数（范围最小值，JSON）。</summary>
        public string? ConstraintMin { get; set; }

        /// <summary>约束参数（范围最大值，JSON）。</summary>
        public string? ConstraintMax { get; set; }

        /// <summary>枚举约束的允许值列表。</summary>
        public List<string> EnumValues { get; set; } = new List<string>();

        /// <summary>自定义约束的回调。</summary>
        public Func<object?, bool>? CustomConstraint { get; set; }

        /// <summary>最后修改时间。</summary>
        public DateTime LastModifiedTime { get; set; } = DateTime.UtcNow;

        /// <summary>网络同步模式。</summary>
        public NetworkSyncMode NetworkSync { get; set; } = NetworkSyncMode.None;

        /// <summary>网络同步频率限制（秒，0=不限制）。</summary>
        public float NetworkSyncInterval { get; set; }

        /// <summary>自定义元数据。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 应用变量约束。
        /// </summary>
        private bool ApplyConstraints(ref object? value)
        {
            switch (ConstraintType)
            {
                case VariableConstraintType.Range:
                    return ApplyRangeConstraint(ref value);

                case VariableConstraintType.Enum:
                    return ApplyEnumConstraint(ref value);

                case VariableConstraintType.Custom:
                    return CustomConstraint == null || CustomConstraint(value);

                default:
                    return true;
            }
        }

        private bool ApplyRangeConstraint(ref object? value)
        {
            if (value == null) return true;

            try
            {
                if (DataType == GraphPinDataType.Float || DataType == GraphPinDataType.Double)
                {
                    var min = !string.IsNullOrEmpty(ConstraintMin) ? float.Parse(ConstraintMin) : float.MinValue;
                    var max = !string.IsNullOrEmpty(ConstraintMax) ? float.Parse(ConstraintMax) : float.MaxValue;
                    var val = Convert.ToSingle(value);
                    if (val < min || val > max)
                    {
                        value = Math.Clamp(val, min, max);
                    }
                }
                else if (DataType == GraphPinDataType.Int)
                {
                    var min = !string.IsNullOrEmpty(ConstraintMin) ? int.Parse(ConstraintMin) : int.MinValue;
                    var max = !string.IsNullOrEmpty(ConstraintMax) ? int.Parse(ConstraintMax) : int.MaxValue;
                    var val = Convert.ToInt32(value);
                    if (val < min || val > max)
                    {
                        value = Math.Clamp(val, min, max);
                    }
                }
            }
            catch
            {
                // 约束应用失败，不阻止赋值
            }

            return true;
        }

        private bool ApplyEnumConstraint(ref object? value)
        {
            if (EnumValues.Count == 0) return true;
            if (value == null) return true;

            var strValue = value.ToString() ?? string.Empty;
            if (EnumValues.Contains(strValue)) return true;

            // 如果值不在枚举列表中，使用默认值
            value = DefaultValue ?? EnumValues.FirstOrDefault() ?? null;
            return true;
        }

        /// <summary>
        /// 获取类型安全的值。
        /// </summary>
        public T? GetValue<T>()
        {
            if (_value == null) return default;
            try { return (T)Convert.ChangeType(_value, typeof(T)); }
            catch { return default; }
        }

        /// <summary>
        /// 初始化变量为默认值。
        /// </summary>
        public void InitializeToDefault()
        {
            _value = GetTypedDefault();
        }

        /// <summary>
        /// 获取类型的默认值。
        /// </summary>
        private object? GetTypedDefault()
        {
            if (!string.IsNullOrEmpty(DefaultValue))
            {
                try
                {
                    return DataType switch
                    {
                        GraphPinDataType.Bool => bool.Parse(DefaultValue),
                        GraphPinDataType.Int => int.Parse(DefaultValue),
                        GraphPinDataType.Float => float.Parse(DefaultValue),
                        GraphPinDataType.Double => double.Parse(DefaultValue),
                        GraphPinDataType.String => DefaultValue,
                        _ => DefaultValue
                    };
                }
                catch
                {
                    // 解析失败，使用类型默认
                }
            }

            return DataType switch
            {
                GraphPinDataType.Bool => false,
                GraphPinDataType.Byte => (byte)0,
                GraphPinDataType.Int => 0,
                GraphPinDataType.Float => 0f,
                GraphPinDataType.Double => 0.0,
                GraphPinDataType.String => string.Empty,
                GraphPinDataType.Vector2 => Vector2.Zero,
                GraphPinDataType.Vector3 => Vector3.Zero,
                GraphPinDataType.Vector4 => Vector4.Zero,
                GraphPinDataType.Quaternion => Quaternion.Identity,
                GraphPinDataType.Color => Vector4.One,
                GraphPinDataType.Object => null,
                _ => null
            };
        }

        /// <summary>
        /// 创建一个新的脚本变量。
        /// </summary>
        public static ScriptVariable Create(string name, GraphPinDataType type, ScriptVariableScope scope = ScriptVariableScope.Member,
            string? defaultValue = null, VariableAccessLevel access = VariableAccessLevel.Public)
        {
            var variable = new ScriptVariable
            {
                Name = name,
                DataType = type,
                Scope = scope,
                DefaultValue = defaultValue,
                AccessLevel = access
            };
            variable.InitializeToDefault();
            return variable;
        }

        /// <summary>
        /// 序列化为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化。
        /// </summary>
        public static ScriptVariable? FromJson(string json)
        {
            return JsonSerializer.Deserialize<ScriptVariable>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptVariableTable - 变量表
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚本变量表：管理所有作用域的脚本变量。
    /// </summary>
    [Serializable]
    public class ScriptVariableTable
    {
        /// <summary>所有变量（ID → 变量）。</summary>
        private readonly Dictionary<Guid, ScriptVariable> _variables = new Dictionary<Guid, ScriptVariable>();

        /// <summary>名称索引（快速查找）。</summary>
        private readonly Dictionary<string, Guid> _nameIndex = new Dictionary<string, Guid>();

        /// <summary>作用域分组索引。</summary>
        private readonly Dictionary<ScriptVariableScope, List<Guid>> _scopeIndex = new Dictionary<ScriptVariableScope, List<Guid>>();

        /// <summary>变量总数。</summary>
        public int Count => _variables.Count;

        /// <summary>
        /// 创建变量。
        /// </summary>
        public ScriptVariable Create(string name, GraphPinDataType type, ScriptVariableScope scope,
            string? defaultValue = null, VariableAccessLevel access = VariableAccessLevel.Public)
        {
            // 检查重名
            if (_nameIndex.ContainsKey(name))
            {
                throw new ArgumentException($"变量 '{name}' 已存在");
            }

            var variable = ScriptVariable.Create(name, type, scope, defaultValue, access);
            _variables[variable.Id] = variable;
            _nameIndex[name] = variable.Id;

            if (!_scopeIndex.ContainsKey(scope))
                _scopeIndex[scope] = new List<Guid>();
            _scopeIndex[scope].Add(variable.Id);

            return variable;
        }

        /// <summary>
        /// 删除变量。
        /// </summary>
        public bool Delete(Guid id)
        {
            if (!_variables.TryGetValue(id, out var variable)) return false;

            _nameIndex.Remove(variable.Name);
            _variables.Remove(id);

            if (_scopeIndex.TryGetValue(variable.Scope, out var list))
                list.Remove(id);

            return true;
        }

        /// <summary>
        /// 按名称获取变量。
        /// </summary>
        public ScriptVariable? Get(string name)
        {
            if (_nameIndex.TryGetValue(name, out var id))
            {
                return _variables.TryGetValue(id, out var variable) ? variable : null;
            }
            return null;
        }

        /// <summary>
        /// 按 ID 获取变量。
        /// </summary>
        public ScriptVariable? Get(Guid id)
        {
            return _variables.TryGetValue(id, out var variable) ? variable : null;
        }

        /// <summary>
        /// 按作用域获取所有变量。
        /// </summary>
        public List<ScriptVariable> GetByScope(ScriptVariableScope scope)
        {
            if (!_scopeIndex.TryGetValue(scope, out var ids)) return new List<ScriptVariable>();
            return ids.Select(id => _variables.TryGetValue(id, out var v) ? v : null)
                     .Where(v => v != null)
                     .ToList()!;
        }

        /// <summary>
        /// 搜索变量（按名称模糊匹配）。
        /// </summary>
        public List<ScriptVariable> Search(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return _variables.Values.ToList();
            var lower = keyword.ToLowerInvariant();
            return _variables.Values.Where(v =>
                v.Name.ToLowerInvariant().Contains(lower) ||
                (v.Category?.ToLowerInvariant().Contains(lower) ?? false)
            ).ToList();
        }

        /// <summary>
        /// 获取变量值（类型安全）。
        /// </summary>
        public T? GetValue<T>(string name)
        {
            var variable = Get(name);
            return variable?.GetValue<T>();
        }

        /// <summary>
        /// 设置变量值。
        /// </summary>
        public void SetValue(string name, object? value)
        {
            var variable = Get(name);
            if (variable != null)
            {
                variable.Value = value;
            }
        }

        /// <summary>
        /// 检查变量是否存在。
        /// </summary>
        public bool Exists(string name)
        {
            return _nameIndex.ContainsKey(name);
        }

        /// <summary>
        /// 序列化所有变量为 JSON。
        /// </summary>
        public string SerializeToJson()
        {
            var data = _variables.Values.ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            return JsonSerializer.Serialize(data, options);
        }

        /// <summary>
        /// 从 JSON 反序列化变量表。
        /// </summary>
        public void DeserializeFromJson(string json)
        {
            _variables.Clear();
            _nameIndex.Clear();
            _scopeIndex.Clear();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };

            var variables = JsonSerializer.Deserialize<List<ScriptVariable>>(json, options);
            if (variables == null) return;

            foreach (var variable in variables)
            {
                _variables[variable.Id] = variable;
                _nameIndex[variable.Name] = variable.Id;

                if (!_scopeIndex.ContainsKey(variable.Scope))
                    _scopeIndex[variable.Scope] = new List<Guid>();
                _scopeIndex[variable.Scope].Add(variable.Id);
            }
        }

        /// <summary>
        /// 获取按作用域分组的变量统计信息。
        /// </summary>
        public string GetStatistics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 变量表统计 ===");
            foreach (ScriptVariableScope scope in Enum.GetValues(typeof(ScriptVariableScope)))
            {
                var count = GetByScope(scope).Count;
                if (count > 0)
                {
                    sb.AppendLine($"  {scope}: {count} 个变量");
                }
            }
            sb.AppendLine($"  总计: {Count} 个变量");
            return sb.ToString();
        }

        /// <summary>清空所有变量。</summary>
        public void Clear()
        {
            _variables.Clear();
            _nameIndex.Clear();
            _scopeIndex.Clear();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  VariableWatcher - 变量监视器
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 变量监视条目。
    /// </summary>
    [Serializable]
    public class WatchEntry
    {
        /// <summary>变量 ID。</summary>
        public Guid VariableId { get; set; }

        /// <summary>变量名称。</summary>
        public string VariableName { get; set; } = string.Empty;

        /// <summary>变量类型。</summary>
        public GraphPinDataType DataType { get; set; }

        /// <summary>当前值（字符串形式）。</summary>
        public string CurrentValue { get; set; } = string.Empty;

        /// <summary>上次值（用于检测变化）。</summary>
        public string PreviousValue { get; set; } = string.Empty;

        /// <summary>是否已变化。</summary>
        public bool HasChanged { get; set; }

        /// <summary>条件表达式（满足时触发回调）。</summary>
        public string? ConditionExpression { get; set; }

        /// <summary>条件回调。</summary>
        public Action<string, object?>? OnConditionMet { get; set; }

        /// <summary>是否启用断点（值变化时暂停调试器）。</summary>
        public bool BreakOnChange { get; set; }
    }

    /// <summary>
    /// 变量监视器：运行时监控变量值、类型转换、范围约束和条件触发。
    /// </summary>
    public class VariableWatcher
    {
        /// <summary>关联的变量表。</summary>
        public ScriptVariableTable VariableTable { get; }

        /// <summary>监视条目列表。</summary>
        public List<WatchEntry> WatchEntries { get; } = new List<WatchEntry>();

        /// <summary>值变化历史（变量名 → 值列表）。</summary>
        private readonly Dictionary<string, List<string>> _valueHistory = new Dictionary<string, List<string>>();

        /// <summary>历史最大记录数。</summary>
        public int MaxHistoryPerVariable { get; set; } = 100;

        /// <summary>是否启用自动更新。</summary>
        public bool AutoUpdate { get; set; } = true;

        /// <summary>条件触发事件。</summary>
        public event Action<string, object?>? OnConditionTriggered;

        /// <summary>
        /// 创建变量监视器。
        /// </summary>
        public VariableWatcher(ScriptVariableTable table)
        {
            VariableTable = table ?? throw new ArgumentNullException(nameof(table));
        }

        /// <summary>
        /// 添加监视变量。
        /// </summary>
        public WatchEntry Watch(string variableName, string? condition = null)
        {
            var variable = VariableTable.Get(variableName);
            if (variable == null)
                throw new ArgumentException($"变量 '{variableName}' 不存在");

            var entry = new WatchEntry
            {
                VariableId = variable.Id,
                VariableName = variableName,
                DataType = variable.DataType,
                CurrentValue = variable.Value?.ToString() ?? "null",
                PreviousValue = variable.Value?.ToString() ?? "null",
                ConditionExpression = condition
            };

            // 注册值变更回调
            variable.OnValueChanged = (v, oldVal, newVal) =>
            {
                UpdateWatchEntry(entry, oldVal, newVal);
            };

            WatchEntries.Add(entry);
            return entry;
        }

        /// <summary>
        /// 移除监视变量。
        /// </summary>
        public bool Unwatch(string variableName)
        {
            return WatchEntries.RemoveAll(e => e.VariableName == variableName) > 0;
        }

        /// <summary>
        /// 更新所有监视条目。
        /// </summary>
        public void UpdateAll()
        {
            foreach (var entry in WatchEntries)
            {
                var variable = VariableTable.Get(entry.VariableId);
                if (variable != null)
                {
                    entry.PreviousValue = entry.CurrentValue;
                    entry.CurrentValue = variable.Value?.ToString() ?? "null";
                    entry.HasChanged = entry.PreviousValue != entry.CurrentValue;

                    if (entry.HasChanged)
                    {
                        RecordHistory(entry.VariableName, entry.CurrentValue);
                        EvaluateCondition(entry, variable.Value);
                    }
                }
            }
        }

        /// <summary>
        /// 获取监视面板数据（用于编辑器显示）。
        /// </summary>
        public List<Dictionary<string, object>> GetWatchPanelData()
        {
            return WatchEntries.Select(e => new Dictionary<string, object>
            {
                ["VariableName"] = e.VariableName,
                ["DataType"] = e.DataType,
                ["CurrentValue"] = e.CurrentValue,
                ["PreviousValue"] = e.PreviousValue,
                ["HasChanged"] = e.HasChanged
            }).ToList();
        }

        /// <summary>
        /// 获取变量值历史。
        /// </summary>
        public List<string> GetValueHistory(string variableName)
        {
            return _valueHistory.TryGetValue(variableName, out var history) ? history : new List<string>();
        }

        private void UpdateWatchEntry(WatchEntry entry, object? oldValue, object? newValue)
        {
            entry.PreviousValue = oldValue?.ToString() ?? "null";
            entry.CurrentValue = newValue?.ToString() ?? "null";
            entry.HasChanged = true;

            RecordHistory(entry.VariableName, entry.CurrentValue);
            EvaluateCondition(entry, newValue);
        }

        private void RecordHistory(string variableName, string value)
        {
            if (!_valueHistory.ContainsKey(variableName))
                _valueHistory[variableName] = new List<string>();

            _valueHistory[variableName].Add(value);

            while (_valueHistory[variableName].Count > MaxHistoryPerVariable)
            {
                _valueHistory[variableName].RemoveAt(0);
            }
        }

        private void EvaluateCondition(WatchEntry entry, object? value)
        {
            if (string.IsNullOrEmpty(entry.ConditionExpression)) return;

            // 简化的条件评估
            try
            {
                bool conditionMet = EvaluateSimpleCondition(entry.ConditionExpression, value);
                if (conditionMet)
                {
                    entry.OnConditionMet?.Invoke(entry.VariableName, value);
                    OnConditionTriggered?.Invoke(entry.VariableName, value);
                }
            }
            catch
            {
                // 条件评估失败，忽略
            }
        }

        private static bool EvaluateSimpleCondition(string expression, object? value)
        {
            var parts = expression.Split(new[] { ' ', '=', '!', '<', '>' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;

            var currentStr = value?.ToString() ?? string.Empty;
            var targetStr = parts.Length > 1 ? parts[1] : parts[0];

            if (expression.Contains(">=")) return CompareValues(currentStr, targetStr) >= 0;
            if (expression.Contains("<=")) return CompareValues(currentStr, targetStr) <= 0;
            if (expression.Contains("!=")) return currentStr != targetStr;
            if (expression.Contains("==")) return currentStr == targetStr;
            if (expression.Contains(">")) return CompareValues(currentStr, targetStr) > 0;
            if (expression.Contains("<")) return CompareValues(currentStr, targetStr) < 0;

            return currentStr == targetStr;
        }

        private static int CompareValues(string a, string b)
        {
            if (float.TryParse(a, out var fa) && float.TryParse(b, out var fb))
                return fa.CompareTo(fb);
            return string.Compare(a, b, StringComparison.Ordinal);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  VariablePersistence - 变量持久化
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 变量持久化：管理变量的保存、加载和重置。
    /// </summary>
    [Serializable]
    public class VariablePersistence
    {
        /// <summary>关联的变量表。</summary>
        public ScriptVariableTable VariableTable { get; }

        /// <summary>保存的变量值（变量 ID → 值 JSON）。</summary>
        private readonly Dictionary<Guid, string> _savedValues = new Dictionary<Guid, string>();

        /// <summary>是否有未保存的变更。</summary>
        public bool HasUnsavedChanges { get; private set; }

        /// <summary>上次保存时间。</summary>
        public DateTime? LastSaveTime { get; private set; }

        /// <summary>值变更事件。</summary>
        public event Action? OnSaved;
        public event Action? OnLoaded;
        public event Action? OnReset;

        /// <summary>
        /// 创建变量持久化管理器。
        /// </summary>
        public VariablePersistence(ScriptVariableTable table)
        {
            VariableTable = table ?? throw new ArgumentNullException(nameof(table));
        }

        /// <summary>
        /// 保存所有可序列化的变量值。
        /// </summary>
        public void Save()
        {
            _savedValues.Clear();

            foreach (var variable in VariableTable.GetByScope(ScriptVariableScope.Persisted))
            {
                if (variable.IsSerializable && variable.Value != null)
                {
                    _savedValues[variable.Id] = JsonSerializer.Serialize(variable.Value);
                }
            }

            // 也保存标记为可序列化的成员变量
            foreach (var variable in VariableTable.GetByScope(ScriptVariableScope.Member))
            {
                if (variable.IsSerializable && variable.Value != null)
                {
                    _savedValues[variable.Id] = JsonSerializer.Serialize(variable.Value);
                }
            }

            HasUnsavedChanges = false;
            LastSaveTime = DateTime.UtcNow;
            OnSaved?.Invoke();
        }

        /// <summary>
        /// 加载已保存的变量值。
        /// </summary>
        public void Load()
        {
            foreach (var kvp in _savedValues)
            {
                var variable = VariableTable.Get(kvp.Key);
                if (variable == null || !variable.IsSerializable) continue;

                try
                {
                    var value = JsonSerializer.Deserialize<object?>(kvp.Value);
                    if (value != null)
                    {
                        variable.Value = ConvertValue(value, variable.DataType);
                    }
                }
                catch
                {
                    // 加载失败，保持当前值
                }
            }

            HasUnsavedChanges = false;
            OnLoaded?.Invoke();
        }

        /// <summary>
        /// 重置所有变量到默认值。
        /// </summary>
        public void Reset()
        {
            foreach (var variable in VariableTable.GetByScope(ScriptVariableScope.Member))
            {
                variable.InitializeToDefault();
            }
            foreach (var variable in VariableTable.GetByScope(ScriptVariableScope.Persisted))
            {
                variable.InitializeToDefault();
            }

            HasUnsavedChanges = true;
            OnReset?.Invoke();
        }

        /// <summary>
        /// 保存到文件。
        /// </summary>
        public void SaveToFile(string filePath)
        {
            Save();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_savedValues, options);
            System.IO.File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// 从文件加载。
        /// </summary>
        public void LoadFromFile(string filePath)
        {
            if (!System.IO.File.Exists(filePath)) return;

            var json = System.IO.File.ReadAllText(filePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _savedValues = JsonSerializer.Deserialize<Dictionary<Guid, string>>(json, options) ?? new Dictionary<Guid, string>();
            Load();
        }

        /// <summary>
        /// 标记有未保存变更。
        /// </summary>
        public void MarkDirty()
        {
            HasUnsavedChanges = true;
        }

        private static object? ConvertValue(object? value, GraphPinDataType targetType)
        {
            if (value == null) return null;

            return targetType switch
            {
                GraphPinDataType.Bool => Convert.ToBoolean(value),
                GraphPinDataType.Int => Convert.ToInt32(value),
                GraphPinDataType.Float => Convert.ToSingle(value),
                GraphPinDataType.Double => Convert.ToDouble(value),
                GraphPinDataType.String => value.ToString(),
                _ => value
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NetworkVariableSync - 网络变量同步接口
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络变量同步状态。
    /// </summary>
    public enum NetworkSyncState
    {
        /// <summary>未同步。</summary>
        Idle,
        /// <summary>等待同步。</summary>
        Pending,
        /// <summary>同步中。</summary>
        Syncing,
        /// <summary>已同步。</summary>
        Synced,
        /// <summary>同步失败。</summary>
        Failed
    }

    /// <summary>
    /// 网络变量同步条目。
    /// </summary>
    [Serializable]
    public class NetworkSyncEntry
    {
        /// <summary>变量 ID。</summary>
        public Guid VariableId { get; set; }

        /// <summary>变量名称。</summary>
        public string VariableName { get; set; } = string.Empty;

        /// <summary>同步模式。</summary>
        public NetworkSyncMode Mode { get; set; }

        /// <summary>当前同步状态。</summary>
        public NetworkSyncState State { get; set; } = NetworkSyncState.Idle;

        /// <summary>上次同步时间。</summary>
        public DateTime LastSyncTime { get; set; }

        /// <summary>上次同步的值。</summary>
        public string? LastSyncedValue { get; set; }

        /// <summary>同步频率限制（秒）。</summary>
        public float SyncInterval { get; set; }

        /// <summary>可靠性模式（可靠/不可靠）。</summary>
        public bool Reliable { get; set; } = true;

        /// <summary>是否压缩传输。</summary>
        public bool Compressed { get; set; }
    }

    /// <summary>
    /// 网络变量同步接口：定义变量在网络环境下的同步行为。
    /// <para>这是一个接口层，实际的网络传输实现由网络系统提供。</para>
    /// </summary>
    public class NetworkVariableSync
    {
        /// <summary>关联的变量表。</summary>
        public ScriptVariableTable VariableTable { get; }

        /// <summary>同步条目列表。</summary>
        public List<NetworkSyncEntry> SyncEntries { get; } = new List<NetworkSyncEntry>();

        /// <summary>是否启用网络同步。</summary>
        public bool IsEnabled { get; set; }

        /// <summary>同步统计。</summary>
        public int TotalSyncs { get; private set; }
        public int TotalSyncErrors { get; private set; }
        public long TotalBytesSent { get; private set; }
        public long TotalBytesReceived { get; private set; }

        /// <summary>变量同步请求事件。</summary>
        public event Action<NetworkSyncEntry, object?>? OnSyncRequested;

        /// <summary>变量同步完成事件。</summary>
        public event Action<NetworkSyncEntry>? OnSyncCompleted;

        /// <summary>
        /// 创建网络变量同步管理器。
        /// </summary>
        public NetworkVariableSync(ScriptVariableTable table)
        {
            VariableTable = table ?? throw new ArgumentNullException(nameof(table));
        }

        /// <summary>
        /// 注册变量进行网络同步。
        /// </summary>
        public NetworkSyncEntry RegisterSync(string variableName, NetworkSyncMode mode,
            float interval = 0, bool reliable = true)
        {
            var variable = VariableTable.Get(variableName);
            if (variable == null)
                throw new ArgumentException($"变量 '{variableName}' 不存在");

            var entry = new NetworkSyncEntry
            {
                VariableId = variable.Id,
                VariableName = variableName,
                Mode = mode,
                SyncInterval = interval,
                Reliable = reliable
            };

            SyncEntries.Add(entry);

            // 注册值变更回调
            variable.OnValueChanged += (v, oldVal, newVal) =>
            {
                if (IsEnabled && (mode == NetworkSyncMode.ClientToServer || mode == NetworkSyncMode.Bidirectional))
                {
                    RequestSync(entry, newVal);
                }
            };

            return entry;
        }

        /// <summary>
        /// 注销变量网络同步。
        /// </summary>
        public bool UnregisterSync(string variableName)
        {
            return SyncEntries.RemoveAll(e => e.VariableName == variableName) > 0;
        }

        /// <summary>
        /// 请求同步变量值。
        /// </summary>
        public void RequestSync(NetworkSyncEntry entry, object? value)
        {
            if (!IsEnabled) return;

            // 检查同步频率限制
            if (entry.SyncInterval > 0)
            {
                var elapsed = (DateTime.UtcNow - entry.LastSyncTime).TotalSeconds;
                if (elapsed < entry.SyncInterval) return;
            }

            entry.State = NetworkSyncState.Syncing;
            entry.LastSyncTime = DateTime.UtcNow;

            // 触发同步请求事件（由网络系统实际处理传输）
            OnSyncRequested?.Invoke(entry, value);

            // 模拟同步完成（实际实现中由网络回调触发）
            entry.State = NetworkSyncState.Synced;
            entry.LastSyncedValue = value?.ToString();
            TotalSyncs++;

            OnSyncCompleted?.Invoke(entry);
        }

        /// <summary>
        /// 处理接收到的同步值。
        /// </summary>
        public void HandleIncomingSync(string variableName, object? value)
        {
            var entry = SyncEntries.FirstOrDefault(e => e.VariableName == variableName);
            if (entry == null) return;

            if (entry.Mode == NetworkSyncMode.ServerToClient || entry.Mode == NetworkSyncMode.Bidirectional)
            {
                VariableTable.SetValue(variableName, value);
                entry.State = NetworkSyncState.Synced;
                entry.LastSyncedValue = value?.ToString();
                TotalBytesReceived += (entry.LastSyncedValue?.Length ?? 0);
            }
        }

        /// <summary>
        /// 获取网络同步面板数据。
        /// </summary>
        public List<Dictionary<string, object>> GetSyncPanelData()
        {
            return SyncEntries.Select(e => new Dictionary<string, object>
            {
                ["VariableName"] = e.VariableName,
                ["Mode"] = e.Mode,
                ["State"] = e.State,
                ["LastSyncTime"] = e.LastSyncTime,
                ["LastSyncedValue"] = e.LastSyncedValue ?? "null",
                ["Reliable"] = e.Reliable
            }).ToList();
        }

        /// <summary>
        /// 获取同步统计信息。
        /// </summary>
        public string GetStatistics()
        {
            return $"网络变量同步: {SyncEntries.Count} 个注册变量 | 总同步次数: {TotalSyncs} | 错误: {TotalSyncErrors} | 发送: {TotalBytesSent} 字节 | 接收: {TotalBytesReceived} 字节";
        }
    }
}
