using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace YamaEngine.DataSystem
{
    // ====================================================================
    // 配置系统（ConfigSystem）
    // ====================================================================
    // 类似 UE Config / Unity ScriptableObject 配置。
    // 支持分层配置（默认→平台→项目→用户→命令行）、热重载、
    // CVar 系统、配置变更监听和验证。
    // ====================================================================

    /// <summary>
    /// 配置类型枚举。标识配置所属的子系统。
    /// </summary>
    public enum ConfigType
    {
        Engine,
        Game,
        Input,
        Graphics,
        Audio,
        Network,
        Physics,
        UI
    }

    /// <summary>
    /// 配置格式枚举。支持多种配置文件格式。
    /// </summary>
    public enum ConfigFormat
    {
        Ini,
        Json,
        Toml
    }

    /// <summary>
    /// 配置分层优先级。数值越大优先级越高。
    /// </summary>

    /// <summary>
    /// 配置验证规则。用于检查配置值的合法性。
    /// </summary>
    public sealed class ConfigValidationRule
    {
        public string Key { get; }
        public float? MinValue { get; set; }
        public float? MaxValue { get; set; }
        public List<string>? AllowedValues { get; set; }
        public string? DependsOn { get; set; }
        public Func<object?, bool>? CustomValidator { get; set; }
        public bool IsReadOnly { get; set; }

        public ConfigValidationRule(string key)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
        }

        public (bool isValid, string error) Validate(object? value)
        {
            if (IsReadOnly)
                return (false, $"Key '{Key}' is read-only.");

            if (value == null)
                return (true, "");

            // 数值范围检查
            if (value is IConvertible conv)
            {
                try
                {
                    double num = conv.ToDouble(CultureInfo.InvariantCulture);
                    if (MinValue.HasValue && num < MinValue.Value)
                        return (false, $"'{Key}' value {num} is below minimum {MinValue.Value}.");
                    if (MaxValue.HasValue && num > MaxValue.Value)
                        return (false, $"'{Key}' value {num} is above maximum {MaxValue.Value}.");
                }
                catch { }
            }

            // 允许值检查
            if (AllowedValues != null && AllowedValues.Count > 0)
            {
                var strValue = value.ToString();
                if (!AllowedValues.Any(v => string.Equals(v, strValue, StringComparison.OrdinalIgnoreCase)))
                    return (false, $"'{Key}' value '{strValue}' is not in allowed values: [{string.Join(", ", AllowedValues)}].");
            }

            // 自定义验证
            if (CustomValidator != null && !CustomValidator(value))
                return (false, $"'{Key}' failed custom validation.");

            return (true, "");
        }
    }

    /// <summary>
    /// 游戏配置基类。可热重载的运行时配置容器。
    /// 类似 UE Config + Unity ScriptableObject，提供分层配置和变更通知。
    /// </summary>
    public class GameConfig
    {
        /// <summary>配置类型</summary>
        public ConfigType ConfigType { get; protected set; } = ConfigType.Engine;

        /// <summary>配置名称</summary>
        public string ConfigName { get; set; } = "DefaultConfig";

        /// <summary>配置变更事件（参数：配置键、旧值、新值）</summary>
        public event Action<string, object?, object?>? OnConfigChanged;

        /// <summary>配置重载事件</summary>
        public event Action? OnConfigReloaded;

        /// <summary>触发配置重载事件（供外部 ConfigManager 调用）。</summary>
        public void NotifyConfigReloaded() => OnConfigReloaded?.Invoke();

        protected readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
        protected readonly Dictionary<string, ConfigValidationRule> _rules = new(StringComparer.OrdinalIgnoreCase);

        // ── 基础读写 ──

        /// <summary>获取配置值</summary>
        public T? Get<T>(string key, T? defaultValue = default)
        {
            if (_values.TryGetValue(key, out var val))
            {
                if (val is T typed) return typed;
                try { return (T)Convert.ChangeType(val, typeof(T), CultureInfo.InvariantCulture); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        /// <summary>设置配置值（带验证和事件）</summary>
        public bool Set(string key, object? value)
        {
            // 验证
            if (_rules.TryGetValue(key, out var rule))
            {
                var (isValid, error) = rule.Validate(value);
                if (!isValid)
                {
                    System.Diagnostics.Debug.WriteLine($"Config validation failed: {error}");
                    return false;
                }
            }

            object? oldValue = _values.TryGetValue(key, out var v) ? v : null;
            _values[key] = value;
            OnConfigChanged?.Invoke(key, oldValue, value);
            return true;
        }

        /// <summary>判断是否存在指定键</summary>
        public bool Has(string key) => _values.ContainsKey(key);

        /// <summary>移除配置键</summary>
        public bool Remove(string key)
        {
            object? oldValue = _values.TryGetValue(key, out var v) ? v : null;
            if (_values.Remove(key))
            {
                OnConfigChanged?.Invoke(key, oldValue, null);
                return true;
            }
            return false;
        }

        /// <summary>获取所有配置键</summary>
        public IEnumerable<string> GetAllKeys() => _values.Keys;

        /// <summary>清空所有配置</summary>
        public void Clear() => _values.Clear();

        /// <summary>添加验证规则</summary>
        public void AddValidationRule(ConfigValidationRule rule) =>
            _rules[rule.Key] = rule;

        // ── 便捷方法 ──

        public int GetInt(string key, int defaultValue = 0) => Get(key, defaultValue);
        public float GetFloat(string key, float defaultValue = 0) => Get(key, defaultValue);
        public bool GetBool(string key, bool defaultValue = false) => Get(key, defaultValue);
        public string GetString(string key, string defaultValue = "") => Get(key, defaultValue);
        public Vector3 GetVector3(string key, Vector3 defaultValue = default) => Get(key, defaultValue);

        // ── 导入导出 ──

        /// <summary>从 INI 格式导入</summary>
        public void ImportIni(string ini)
        {
            foreach (var line in ini.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("["))
                    continue;
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0) continue;
                var key = trimmed.Substring(0, eqIdx).Trim();
                var value = trimmed.Substring(eqIdx + 1).Trim();
                _values[key] = value;
            }
        }

        /// <summary>从 JSON 格式导入</summary>
        public void ImportJson(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        _values[prop.Name] = ConvertJsonElement(prop.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to import JSON config: {ex.Message}");
            }
        }

        /// <summary>从文件导入（自动检测格式）</summary>
        public void ImportFile(string filePath)
        {
            if (!File.Exists(filePath)) return;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var content = File.ReadAllText(filePath, Encoding.UTF8);

            if (ext == ".json") ImportJson(content);
            else ImportIni(content); // 默认 INI
        }

        /// <summary>导出为 INI 格式</summary>
        public string ExportIni()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {ConfigName} ({ConfigType})");
            sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            foreach (var kvp in _values)
                sb.AppendLine($"{kvp.Key} = {kvp.Value}");
            return sb.ToString();
        }

        /// <summary>导出为 JSON 格式</summary>
        public string ExportJson()
        {
            return JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>保存到文件</summary>
        public bool SaveToFile(string filePath, ConfigFormat format = ConfigFormat.Ini)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var content = format == ConfigFormat.Json ? ExportJson() : ExportIni();
                File.WriteAllText(filePath, content, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
                return false;
            }
        }

        /// <summary>重载配置</summary>
        public virtual void Reload(string? filePath = null)
        {
            OnConfigReloaded?.Invoke();
        }

        private static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l
                    : element.GetDouble(),
                JsonValueKind.String => element.GetString(),
                _ => element.GetRawText()
            };
        }
    }

    // ====================================================================
    // CVar 系统（控制台变量）
    // ====================================================================

    /// <summary>
    /// CVar 类型枚举。控制台变量的数据类型。
    /// </summary>
    public enum CVarDataType
    {
        Int,
        Float,
        Bool,
        String,
        Vector3,
        Color
    }

    /// <summary>
    /// 控制台变量（CVar）。运行时可通过控制台修改的配置变量。
    /// 持久化到用户配置文件。
    /// </summary>
    public sealed class CVar
    {
        public string Name { get; }
        public string Description { get; set; } = "";
        public CVarDataType DataType { get; }
        public object DefaultValue { get; }
        public object CurrentValue { get; private set; }
        public float? MinValue { get; set; }
        public float? MaxValue { get; set; }
        public bool IsReadOnly { get; set; }
        public bool PersistToUserConfig { get; set; } = true;
        public string Category { get; set; } = "General";

        public event Action<CVar>? OnChanged;

        internal CVar(string name, CVarDataType dataType, object defaultValue)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DataType = dataType;
            DefaultValue = defaultValue;
            CurrentValue = defaultValue;
        }

        /// <summary>获取值（类型安全）</summary>
        public T GetValue<T>() => CurrentValue is T t ? t : default;

        /// <summary>设置值（带验证）</summary>
        public bool SetValue(object value)
        {
            if (IsReadOnly) return false;

            // 数值范围检查
            if (value is IConvertible conv && (MinValue.HasValue || MaxValue.HasValue))
            {
                try
                {
                    double num = conv.ToDouble(CultureInfo.InvariantCulture);
                    if (MinValue.HasValue && num < MinValue.Value) return false;
                    if (MaxValue.HasValue && num > MaxValue.Value) return false;
                }
                catch { }
            }

            var oldValue = CurrentValue;
            CurrentValue = value;
            OnChanged?.Invoke(this);
            return true;
        }

        /// <summary>重置为默认值</summary>
        public void Reset() => SetValue(DefaultValue);

        /// <summary>转换为字符串</summary>
        public string ValueAsString() => CurrentValue?.ToString() ?? "";
    }

    /// <summary>
    /// CVar 管理器。管理所有控制台变量的注册、查找、修改和持久化。
    /// </summary>
    public sealed class CVarManager
    {
        private static CVarManager? _instance;
        public static CVarManager Instance => _instance ??= new CVarManager();

        private readonly Dictionary<string, CVar> _cvars = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CVar> _cvarList = new();

        /// <summary>CVar 数量</summary>
        public int Count => _cvarList.Count;

        /// <summary>CVar 注册事件</summary>
        public event Action<CVar>? OnCVarRegistered;
        /// <summary>CVar 变更事件</summary>
        public event Action<CVar, object?, object?>? OnCVarChanged;

        private CVarManager() { }

        /// <summary>注册一个 CVar</summary>
        public CVar Register(string name, CVarDataType type, object defaultValue, string description = "")
        {
            if (_cvars.ContainsKey(name))
                return _cvars[name];

            var cvar = new CVar(name, type, defaultValue) { Description = description };
            _cvars[name] = cvar;
            _cvarList.Add(cvar);
            cvar.OnChanged += OnCVarChangedHandler;
            OnCVarRegistered?.Invoke(cvar);
            return cvar;
        }

        /// <summary>注册 Int CVar</summary>
        public CVar RegisterInt(string name, int defaultValue, string description = "",
            int? min = null, int? max = null)
        {
            var cvar = Register(name, CVarDataType.Int, defaultValue, description);
            if (min.HasValue) cvar.MinValue = min.Value;
            if (max.HasValue) cvar.MaxValue = max.Value;
            return cvar;
        }

        /// <summary>注册 Float CVar</summary>
        public CVar RegisterFloat(string name, float defaultValue, string description = "",
            float? min = null, float? max = null)
        {
            var cvar = Register(name, CVarDataType.Float, defaultValue, description);
            if (min.HasValue) cvar.MinValue = min.Value;
            if (max.HasValue) cvar.MaxValue = max.Value;
            return cvar;
        }

        /// <summary>注册 Bool CVar</summary>
        public CVar RegisterBool(string name, bool defaultValue, string description = "")
        {
            return Register(name, CVarDataType.Bool, defaultValue, description);
        }

        /// <summary>注册 String CVar</summary>
        public CVar RegisterString(string name, string defaultValue, string description = "")
        {
            return Register(name, CVarDataType.String, defaultValue, description);
        }

        /// <summary>注册 Vector3 CVar</summary>
        public CVar RegisterVector3(string name, Vector3 defaultValue, string description = "")
        {
            return Register(name, CVarDataType.Vector3, defaultValue, description);
        }

        /// <summary>注册 Color CVar</summary>
        public CVar RegisterColor(string name, Vector4 defaultValue, string description = "")
        {
            return Register(name, CVarDataType.Color, defaultValue, description);
        }

        /// <summary>获取 CVar</summary>
        public CVar? GetCVar(string name) =>
            _cvars.TryGetValue(name, out var cvar) ? cvar : null;

        /// <summary>获取 CVar 值</summary>
        public T GetCVarValue<T>(string name, T defaultValue = default)
        {
            if (!_cvars.TryGetValue(name, out var cvar)) return defaultValue;
            return cvar.GetValue<T>();
        }

        /// <summary>设置 CVar 值</summary>
        public bool SetCVarValue(string name, object value)
        {
            if (!_cvars.TryGetValue(name, out var cvar)) return false;
            return cvar.SetValue(value);
        }

        /// <summary>按分类查找 CVar</summary>
        public List<CVar> GetByCategory(string category) =>
            _cvarList.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>按前缀查找 CVar</summary>
        public List<CVar> GetByPrefix(string prefix) =>
            _cvarList.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>获取所有 CVar</summary>
        public IReadOnlyList<CVar> GetAll() => _cvarList;

        /// <summary>重置所有 CVar 到默认值</summary>
        public void ResetAll()
        {
            foreach (var cvar in _cvarList) cvar.Reset();
        }

        /// <summary>持久化所有标记了 PersistToUserConfig 的 CVar 到文件</summary>
        public bool PersistToFile(string filePath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Yama Engine User Configuration");
                sb.AppendLine($"# Saved: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                foreach (var cvar in _cvarList.Where(c => c.PersistToUserConfig))
                    sb.AppendLine($"{cvar.Name} = {cvar.ValueAsString()}");

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to persist CVars: {ex.Message}");
                return false;
            }
        }

        /// <summary>从文件加载 CVar 值（不覆盖不存在的变量）</summary>
        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx < 0) continue;
                    var name = trimmed.Substring(0, eqIdx).Trim();
                    var value = trimmed.Substring(eqIdx + 1).Trim();
                    if (_cvars.TryGetValue(name, out var cvar))
                        cvar.SetValue(ParseCVarValue(cvar.DataType, value));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load CVars: {ex.Message}");
            }
        }

        private void OnCVarChangedHandler(CVar cvar)
        {
            OnCVarChanged?.Invoke(cvar, cvar.DefaultValue, cvar.CurrentValue);
        }

        private static object ParseCVarValue(CVarDataType type, string value)
        {
            return type switch
            {
                CVarDataType.Int => int.TryParse(value, out int i) ? i : 0,
                CVarDataType.Float => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f,
                CVarDataType.Bool => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1",
                CVarDataType.Vector3 => ParseVec3(value),
                CVarDataType.Color => ParseVec4(value),
                _ => value
            };
        }

        private static Vector3 ParseVec3(string s)
        {
            var p = s.Split(',');
            return new Vector3(
                p.Length > 0 ? float.Parse(p[0], CultureInfo.InvariantCulture) : 0,
                p.Length > 1 ? float.Parse(p[1], CultureInfo.InvariantCulture) : 0,
                p.Length > 2 ? float.Parse(p[2], CultureInfo.InvariantCulture) : 0);
        }

        private static Vector4 ParseVec4(string s)
        {
            var p = s.Split(',');
            return new Vector4(
                p.Length > 0 ? float.Parse(p[0], CultureInfo.InvariantCulture) : 0,
                p.Length > 1 ? float.Parse(p[1], CultureInfo.InvariantCulture) : 0,
                p.Length > 2 ? float.Parse(p[2], CultureInfo.InvariantCulture) : 0,
                p.Length > 3 ? float.Parse(p[3], CultureInfo.InvariantCulture) : 1);
        }
    }

    // ====================================================================
    // 分层配置管理器（LayeredConfigManager）
    // ====================================================================

    /// <summary>
    /// 分层配置管理器。按优先级合并多层配置：
    /// 默认 → 平台 → 项目 → 用户 → 命令行（高优先级覆盖低优先级）。
    /// </summary>
    public sealed class LayeredConfigManager
    {
        private readonly Dictionary<ConfigLayer, GameConfig> _layers = new();
        private GameConfig _merged = new() { ConfigName = "MergedConfig" };

        /// <summary>合并后的配置（最高优先级）</summary>
        public GameConfig MergedConfig => _merged;

        /// <summary>配置变更事件</summary>
        public event Action<string, object?, object?>? OnConfigChanged;

        public LayeredConfigManager()
        {
            foreach (ConfigLayer layer in Enum.GetValues(typeof(ConfigLayer)))
                _layers[layer] = new GameConfig { ConfigName = $"Layer_{layer}" };
        }

        /// <summary>设置指定层的配置值</summary>
        public void Set(ConfigLayer layer, string key, object? value)
        {
            if (!_layers.TryGetValue(layer, out var config)) return;
            config.Set(key, value);
            RebuildMerged();
        }

        /// <summary>获取合并后的配置值</summary>
        public T? Get<T>(string key, T? defaultValue = default) => _merged.Get(key, defaultValue);

        /// <summary>获取指定层的配置</summary>
        public GameConfig? GetLayer(ConfigLayer layer) =>
            _layers.TryGetValue(layer, out var config) ? config : null;

        /// <summary>导入配置到指定层</summary>
        public void ImportLayer(ConfigLayer layer, string filePath)
        {
            if (!_layers.TryGetValue(layer, out var config)) return;
            config.ImportFile(filePath);
            RebuildMerged();
        }

        /// <summary>从命令行参数导入</summary>
        public void ImportCommandLine(string[] args)
        {
            var config = _layers[ConfigLayer.CommandLine];
            foreach (var arg in args)
            {
                if (arg.StartsWith("--") || arg.StartsWith("-"))
                {
                    var trimmed = arg.TrimStart('-', '-');
                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = trimmed.Substring(0, eqIdx);
                        var value = trimmed.Substring(eqIdx + 1);
                        config.Set(key, value);
                    }
                    else
                    {
                        config.Set(trimmed, "true");
                    }
                }
            }
            RebuildMerged();
        }

        /// <summary>重建合并配置</summary>
        private void RebuildMerged()
        {
            _merged.Clear();

            // 按优先级从低到高合并
            var orderedLayers = _layers
                .OrderBy(kvp => (int)kvp.Key)
                .Select(kvp => kvp.Value);

            foreach (var layer in orderedLayers)
            {
                foreach (var key in layer.GetAllKeys())
                {
                    _merged.Set(key, layer.Get<object>(key));
                }
            }
        }

        /// <summary>保存用户层配置到文件</summary>
        public bool SaveUserConfig(string filePath)
        {
            return _layers[ConfigLayer.User].SaveToFile(filePath, ConfigFormat.Ini);
        }

        /// <summary>加载用户层配置从文件</summary>
        public void LoadUserConfig(string filePath)
        {
            ImportLayer(ConfigLayer.User, filePath);
        }
    }
}
