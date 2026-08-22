// ============================================================================================
//  ConfigManager.cs
//  山雨引擎 (ShanyuEngine) 配置管理系统
// ============================================================================================
//  提供分层配置管理：引擎默认值 → 项目配置 → 用户偏好 → 命令行覆盖。
//  支持 JSON 序列化/反序列化，运行时热修改。
//  设计参考 Unreal Engine 的 GConfig + UObject 属性系统。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShanyuEngine.Config
{
    // ====== 配置值的包装（支持类型安全的访问） ======
    public readonly struct ConfigValue
    {
        private readonly object? _value;

        public ConfigValue(object? value) => _value = value;

        public bool AsBool => _value is bool b && b;
        public int AsInt => _value is int i ? i : (_value is long l ? (int)l : 0);
        public float AsFloat => _value is float f ? f : (_value is double d ? (float)d : 0);
        public string AsString => _value?.ToString() ?? string.Empty;

        public T As<T>() where T : class => (_value as T)!;
        public bool IsNull => _value == null;
        public override string ToString() => _value?.ToString() ?? "null";
    }

    // ====== 配置层枚举 ======
    public enum ConfigLayer
    {
        /// <summary>引擎编译时的默认值。不可修改。</summary>
        EngineDefault,

        /// <summary>项目配置（版本控制）。</summary>
        Project,

        /// <summary>用户偏好设置（本地）。</summary>
        User,

        /// <summary>命令行参数覆盖（最高优先级）。</summary>
        CommandLine
    }

    // ====== 配置变更事件 ======
    public class ConfigChangedEventArgs : EventArgs
    {
        public string Key { get; }
        public ConfigValue OldValue { get; }
        public ConfigValue NewValue { get; }
        public ConfigLayer SourceLayer { get; }

        public ConfigChangedEventArgs(string key, ConfigValue oldValue, ConfigValue newValue, ConfigLayer layer)
        {
            Key = key;
            OldValue = oldValue;
            NewValue = newValue;
            SourceLayer = layer;
        }
    }

    // ====== 配置节 ======
    /// <summary>
    /// 配置节将键分组成逻辑单元。每个节可独立序列化/反序列化。
    /// </summary>
    public sealed class ConfigSection
    {
        private readonly Dictionary<string, object?> _values = new();

        public string Name { get; }

        public ConfigSection(string name) => Name = name;

        public object? this[string key]
        {
            get => _values.TryGetValue(key, out var v) ? v : null;
            set
            {
                if (value != null)
                    _values[key] = value;
                else
                    _values.Remove(key);
            }
        }

        public bool HasKey(string key) => _values.ContainsKey(key);
        public IEnumerable<string> Keys => _values.Keys;
        public int Count => _values.Count;

        public void Clear() => _values.Clear();

        public Dictionary<string, object?> ToDictionary()
        {
            var dict = new Dictionary<string, object?>(_values.Count);
            foreach (var kvp in _values)
                dict[kvp.Key] = kvp.Value;
            return dict;
        }

        public void FromDictionary(Dictionary<string, object?> source)
        {
            _values.Clear();
            foreach (var kvp in source)
                _values[kvp.Key] = kvp.Value;
        }
    }

    // ====== 配置管理器 ======
    /// <summary>
    /// 山雨引擎分层配置系统。提供 "引擎默认 → 项目 → 用户 → 命令行" 四层覆盖。
    /// 自动序列化到 JSON 文件，支持热重载。
    /// </summary>
    public sealed class ConfigManager
    {
        // ====== 单例 ======
        public static ConfigManager Instance { get; private set; } = new ConfigManager();

        // ====== 分层存储 ======
        private readonly Dictionary<ConfigLayer, Dictionary<string, Dictionary<string, object?>>> _layers;
        private readonly Dictionary<string, HashSet<ConfigLayer>> _keyLayers;

        // ====== 文件路径 ======
        public string ProjectConfigPath { get; private set; } = string.Empty;
        public string UserConfigPath { get; private set; } = string.Empty;

        // ====== 事件 ======
        public event EventHandler<ConfigChangedEventArgs>? OnConfigChanged;

        // ====== 构造函数 ======
        private ConfigManager()
        {
            _layers = new Dictionary<ConfigLayer, Dictionary<string, Dictionary<string, object?>>>();
            _keyLayers = new Dictionary<string, HashSet<ConfigLayer>>();
            foreach (ConfigLayer layer in Enum.GetValues<ConfigLayer>())
                _layers[layer] = new Dictionary<string, Dictionary<string, object?>>();
        }

        // ====== 初始化 ======
        public void Initialize(string projectPath, string userPath)
        {
            ProjectConfigPath = projectPath;
            UserConfigPath = userPath;

            // 加载项目配置
            LoadFile(ConfigLayer.Project, projectPath);

            // 加载用户配置
            LoadFile(ConfigLayer.User, userPath);

            // 解析命令行
            ParseCommandLine();
        }

        // ====== 读取配置 ======
        public ConfigValue Get(string section, string key)
        {
            // 按优先级查找：命令行 > 用户 > 项目 > 引擎默认
            foreach (var layer in new[] { ConfigLayer.CommandLine, ConfigLayer.User, ConfigLayer.Project, ConfigLayer.EngineDefault })
            {
                if (_layers[layer].TryGetValue(section, out var sec) && sec.TryGetValue(key, out var val))
                    return new ConfigValue(val);
            }
            return new ConfigValue(null);
        }

        /// <summary>获取配置，不存在则返回默认值。</summary>
        public T Get<T>(string section, string key, T defaultValue)
        {
            var val = Get(section, key);
            if (val.IsNull) return defaultValue;

            try
            {
                return (T)Convert.ChangeType(val.AsString, typeof(T))!;
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>获取配置节的所有键值对。</summary>
        public ConfigSection GetSection(string section)
        {
            var result = new ConfigSection(section);
            foreach (var layer in new[] { ConfigLayer.CommandLine, ConfigLayer.User, ConfigLayer.Project, ConfigLayer.EngineDefault })
            {
                if (_layers[layer].TryGetValue(section, out var sec))
                {
                    foreach (var kvp in sec)
                        result[kvp.Key] = kvp.Value;
                }
            }
            return result;
        }

        public IEnumerable<string> GetSectionNames()
        {
            var names = new HashSet<string>();
            foreach (var layer in _layers.Values)
                foreach (var section in layer.Keys)
                    names.Add(section);
            return names;
        }

        // ====== 写入配置 ======
        public void Set(string section, string key, object? value, ConfigLayer layer = ConfigLayer.User)
        {
            var oldVal = Get(section, key);

            if (!_layers[layer].TryGetValue(section, out var sec))
            {
                sec = new Dictionary<string, object?>();
                _layers[layer][section] = sec;
            }

            if (value != null)
            {
                sec[key] = value;
                if (!_keyLayers.TryGetValue($"{section}.{key}", out var ls))
                {
                    ls = new HashSet<ConfigLayer>();
                    _keyLayers[$"{section}.{key}"] = ls;
                }
                ls.Add(layer);
            }
            else
            {
                sec.Remove(key);
            }

            OnConfigChanged?.Invoke(this, new ConfigChangedEventArgs(
                $"{section}.{key}", oldVal, new ConfigValue(value), layer));
        }

        // ====== 批量设置 ======
        public void SetSection(ConfigSection section, ConfigLayer layer = ConfigLayer.User)
        {
            foreach (var key in section.Keys)
                Set(section.Name, key, section[key], layer);
        }

        // ====== 文件 I/O ======
        public void Save(ConfigLayer layer, string? filePath = null)
        {
            string path = filePath ?? (layer == ConfigLayer.User ? UserConfigPath :
                         layer == ConfigLayer.Project ? ProjectConfigPath : string.Empty);

            if (string.IsNullOrEmpty(path)) return;

            var sections = _layers[layer];
            var root = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var (secName, secValues) in sections)
            {
                if (secValues.Count > 0)
                    root[secName] = new Dictionary<string, object?>(secValues);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = true
            };

            string json = JsonSerializer.Serialize(root, options);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        public void Reload(ConfigLayer layer, string? filePath = null)
        {
            string path = filePath ?? (layer == ConfigLayer.User ? UserConfigPath :
                         layer == ConfigLayer.Project ? ProjectConfigPath : string.Empty);
            if (!string.IsNullOrEmpty(path))
                LoadFile(layer, path);
        }

        // ====== CVar 桥接 ======
        /// <summary>注册一个 CVar 类型键（默认为字符串值）。</summary>
        public void RegisterCVar(string section, string key, object? defaultValue)
        {
            // 确保引擎默认层有条目
            var defLayer = _layers[ConfigLayer.EngineDefault];
            if (!defLayer.TryGetValue(section, out var sec))
            {
                sec = new Dictionary<string, object?>();
                defLayer[section] = sec;
            }
            if (!sec.ContainsKey(key))
                sec[key] = defaultValue;
        }

        // ====== 命令行解析 ======
        private void ParseCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            var cmdLayer = _layers[ConfigLayer.CommandLine];

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                // -Section.Key=Value 或 --Section.Key=Value
                if (arg.StartsWith("--")) arg = arg.Substring(2);
                else if (arg.StartsWith("-")) arg = arg.Substring(1);
                else continue;

                int eqPos = arg.IndexOf('=');
                if (eqPos < 0) continue;

                string fullKey = arg.Substring(0, eqPos);
                string value = arg.Substring(eqPos + 1);

                int dotPos = fullKey.LastIndexOf('.');
                if (dotPos < 0) continue;

                string section = fullKey.Substring(0, dotPos);
                string key = fullKey.Substring(dotPos + 1);

                if (!cmdLayer.TryGetValue(section, out var sec))
                {
                    sec = new Dictionary<string, object?>();
                    cmdLayer[section] = sec;
                }

                // 尝试类型推断
                sec[key] = InferValue(value);
            }
        }

        private static object? InferValue(string raw)
        {
            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (int.TryParse(raw, out var i)) return i;
            if (float.TryParse(raw, out var f)) return f;
            return raw;
        }

        private void LoadFile(ConfigLayer layer, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(json);
                if (root == null) return;

                var targetLayer = _layers[layer];
                foreach (var (sectionName, sectionValues) in root)
                {
                    if (!targetLayer.TryGetValue(sectionName, out var sec))
                    {
                        sec = new Dictionary<string, object?>();
                        targetLayer[sectionName] = sec;
                    }

                    foreach (var (key, elem) in sectionValues)
                    {
                        sec[key] = JsonElementToObject(elem);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConfigManager] 加载配置文件失败 ({path}): {ex.Message}");
            }
        }

        private static object? JsonElementToObject(JsonElement elem)
        {
            return elem.ValueKind switch
            {
                JsonValueKind.String => elem.GetString(),
                JsonValueKind.Number => elem.TryGetInt64(out var l) ? (object)l : elem.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => elem.GetRawText(),
                JsonValueKind.Array => elem.GetRawText(),
                _ => elem.GetRawText()
            };
        }

        // ====== 重置 ======
        public void Reset()
        {
            foreach (var layer in _layers.Values)
                layer.Clear();
            _keyLayers.Clear();
        }
    }

    // ====== 预处理宏 / 编译时特性 ======
    /// <summary>
    /// 平台特性检测。引擎预处理器。
    /// </summary>
    public static class PlatformTraits
    {
        public static bool IsWindows => OperatingSystem.IsWindows();
        public static bool IsLinux => OperatingSystem.IsLinux();
        public static bool IsMacOS => OperatingSystem.IsMacOS();
        public static bool Is64Bit => Environment.Is64BitProcess;
        public static bool IsDevelopment =>
#if DEBUG
            true;
#else
            false;
#endif
        public static bool IsEditor =>
#if EDITOR
            true;
#else
            false;
#endif
    }
}
