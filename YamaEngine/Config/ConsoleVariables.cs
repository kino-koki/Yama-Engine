using System;
using System.Collections.Generic;
using System.Globalization;

namespace YamaEngine.Config
{
    public enum CVarType
    {
        Float,
        Int,
        Bool,
        String,
        Vector2,
        Vector3,
        Color
    }

    [Flags]
    public enum CVarFlags
    {
        None = 0,
        ReadOnly = 1 << 0,
        Cheat = 1 << 1,
        SaveToConfig = 1 << 2,
        NoEditor = 1 << 3,
        HideInHelp = 1 << 4,
        RuntimeReadOnly = 1 << 5,
        Engine = 1 << 6,
        Renderer = 1 << 7,
        Physics = 1 << 8,
        Audio = 1 << 9,
        Network = 1 << 10,
        Gameplay = 1 << 11,
        UI = 1 << 12
    }

    /// <summary>
    /// 引擎级日志级别。供 Logger / 各子系统统一引用（YamaEngine.Config.LogLevel）。
    /// 数值顺序与严重程度递增一致，便于按阈值过滤。
    /// </summary>
    public enum LogLevel
    {
        None = 0,
        Trace = 1,
        Debug = 2,
        Info = 3,
        Warning = 4,
        Error = 5,
        Fatal = 6,
        Assert = 7
    }

    public class ConsoleVariable
    {
        public string Name;
        public string Description;
        public CVarType Type;
        public CVarFlags Flags;
        public string DefaultValue;
        public string CurrentValue;
        public float MinValue;
        public float MaxValue;
        public List<string> AllowedValues = new();
        public int Order;
        public Action<ConsoleVariable>? OnChanged;

        public T GetAs<T>()
        {
            try
            {
                if (typeof(T) == typeof(float)) return (T)(object)float.Parse(CurrentValue, CultureInfo.InvariantCulture);
                if (typeof(T) == typeof(int)) return (T)(object)int.Parse(CurrentValue, CultureInfo.InvariantCulture);
                if (typeof(T) == typeof(bool)) return (T)(object)(CurrentValue == "1" || CurrentValue.ToLower() == "true");
                if (typeof(T) == typeof(string)) return (T)(object)CurrentValue;
                if (typeof(T) == typeof(System.Numerics.Vector2))
                {
                    var parts = CurrentValue.Split(',');
                    return (T)(object)new System.Numerics.Vector2(
                        float.Parse(parts[0], CultureInfo.InvariantCulture),
                        parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
                }
                if (typeof(T) == typeof(System.Numerics.Vector3))
                {
                    var parts = CurrentValue.Split(',');
                    return (T)(object)new System.Numerics.Vector3(
                        float.Parse(parts[0], CultureInfo.InvariantCulture),
                        parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 0,
                        parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 0);
                }
            }
            catch { }
            return default;
        }

        public void SetValue(object value)
        {
            if ((Flags & CVarFlags.ReadOnly) != 0 || (Flags & CVarFlags.RuntimeReadOnly) != 0)
                return;

            string newVal = value?.ToString() ?? "";
            if (Type == CVarType.Float)
            {
                if (float.TryParse(newVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    f = Math.Clamp(f, MinValue, MaxValue);
                    newVal = f.ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (Type == CVarType.Int)
            {
                if (int.TryParse(newVal, out int i))
                {
                    i = (int)Math.Clamp(i, MinValue, MaxValue);
                    newVal = i.ToString();
                }
            }

            if (CurrentValue == newVal) return;
            CurrentValue = newVal;
            OnChanged?.Invoke(this);
        }

        public void Reset()
        {
            SetValue(DefaultValue);
        }
    }

    public class ConsoleLogMessage
    {
        public enum LogLevel { Info, Warning, Error, Debug, Assert }
        public string Message;
        public LogLevel Level;
        public DateTime Timestamp;
        public string Category;

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] [{Level}] {Message}";
        }
    }

    public class ConsoleCommand
    {
        public string Name;
        public string Description;
        public string Usage;
        public int ExpectedArgs;
        public CVarFlags Flags;
        public Action<string[]> Action;
        public Func<string[], string>? Func;

        public string Execute(string[] args)
        {
            try
            {
                if (Func != null) return Func(args);
                Action?.Invoke(args);
                return $"Executed: {Name}";
            }
            catch (Exception ex)
            {
                return $"Error in {Name}: {ex.Message}";
            }
        }
    }

    public class ConsoleVariableSystem
    {
        public readonly Dictionary<string, ConsoleVariable> Variables = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ConsoleCommand> Commands = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ConsoleLogMessage> History = new();
        private readonly Queue<ConsoleLogMessage> _messageQueue = new();
        private int _maxHistory = 500;
        private bool _echoToDebug = true;
        private bool _initialized;

        public int VariableCount => Variables.Count;
        public int CommandCount => Commands.Count;
        public int HistoryCount => History.Count;
        public event Action<ConsoleLogMessage>? OnLogMessage;
        /// <summary>变量值变更事件 (name, newValue)。</summary>
        public event Action<string, string>? OnVariableChanged;

        public ConsoleVariableSystem()
        {
            if (!_initialized)
            {
                _initialized = true;
                RegisterDefault();
            }
        }

        private void RegisterDefault()
        {
            // Engine
            Register("engine.paused", "false", "Pauses the engine simulation", CVarFlags.Engine | CVarFlags.SaveToConfig);
            Register("engine.max_fps", "120", "Maximum frames per second", CVarFlags.Engine | CVarFlags.SaveToConfig, 0, 240);
            Register("engine.time_scale", "1.0", "Global time scale multiplier", CVarFlags.Engine | CVarFlags.SaveToConfig, 0f, 10f);
            Register("engine.fixed_dt", "0.0166667", "Fixed timestep for physics (1/60)", CVarFlags.Engine | CVarFlags.Physics, 0.001f, 0.1f);
            Register("engine.max_substeps", "4", "Max physics substeps per frame", CVarFlags.Engine | CVarFlags.Physics, 1, 16);

            // Renderer
            Register("r.vsync", "true", "Enable vertical sync", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.fullscreen", "false", "Enable fullscreen mode", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.resolution_x", "1920", "Render width", CVarFlags.Renderer | CVarFlags.SaveToConfig, 320, 7680);
            Register("r.resolution_y", "1080", "Render height", CVarFlags.Renderer | CVarFlags.SaveToConfig, 240, 4320);
            Register("r.shadow_quality", "high", "Shadow quality (low,medium,high,ultra)", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.texture_quality", "high", "Texture quality (low,medium,high,ultra)", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.aa_mode", "taa", "Anti-aliasing mode (none,msaa,fxaa,taa,smaa)", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.lod_bias", "1.0", "LOD distance bias multiplier", CVarFlags.Renderer | CVarFlags.SaveToConfig, 0.1f, 4f);
            Register("r.view_distance", "2000", "Maximum view distance in meters", CVarFlags.Renderer | CVarFlags.SaveToConfig, 100, 10000);
            Register("r.post_processing", "true", "Enable post-processing effects", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.bloom", "true", "Enable bloom effect", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.bloom_intensity", "0.5", "Bloom effect intensity", CVarFlags.Renderer | CVarFlags.SaveToConfig, 0f, 2f);
            Register("r.ssao", "true", "Enable Screen-Space Ambient Occlusion", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.ssr", "true", "Enable Screen-Space Reflections", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.motion_blur", "true", "Enable motion blur", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.dof", "false", "Enable Depth of Field", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.god_rays", "true", "Enable volumetric lighting", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.hdr", "true", "Enable HDR rendering pipeline", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.color_grading", "true", "Enable color grading LUT", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.fog", "true", "Enable atmospheric fog", CVarFlags.Renderer | CVarFlags.SaveToConfig);
            Register("r.fog_density", "0.002", "Fog density multiplier", CVarFlags.Renderer | CVarFlags.SaveToConfig, 0f, 0.1f);
            Register("r.particles_max", "10000", "Max particles per system", CVarFlags.Renderer | CVarFlags.SaveToConfig, 100, 100000);
            Register("r.draw_calls_debug", "false", "Show draw call count overlay", CVarFlags.Renderer | CVarFlags.Cheat);
            Register("r.wireframe", "false", "Render in wireframe mode", CVarFlags.Renderer | CVarFlags.Cheat);

            // Physics
            Register("p.enabled", "true", "Enable physics simulation", CVarFlags.Physics | CVarFlags.SaveToConfig);
            Register("p.gravity", "-9.81", "Gravity acceleration (Y axis)", CVarFlags.Physics | CVarFlags.SaveToConfig, -50f, 50f);
            Register("p.max_velocity", "500", "Maximum linear velocity", CVarFlags.Physics | CVarFlags.SaveToConfig, 0f, 5000f);
            Register("p.max_angular_velocity", "50", "Maximum angular velocity", CVarFlags.Physics | CVarFlags.SaveToConfig, 0f, 500f);
            Register("p.bounce_threshold", "2.0", "Velocity threshold for bounce", CVarFlags.Physics, 0f, 20f);
            Register("p.sleep_threshold", "0.05", "Energy threshold for sleep", CVarFlags.Physics, 0f, 2f);
            Register("p.contact_offset", "0.02", "Contact generation offset", CVarFlags.Physics, 0f, 0.5f);
            Register("p.debug_draw", "false", "Draw physics debug visualization", CVarFlags.Physics | CVarFlags.Cheat);

            // Audio
            Register("a.master_volume", "1.0", "Master volume level", CVarFlags.Audio | CVarFlags.SaveToConfig, 0f, 2f);
            Register("a.music_volume", "0.8", "Music bus volume", CVarFlags.Audio | CVarFlags.SaveToConfig, 0f, 2f);
            Register("a.sfx_volume", "1.0", "SFX bus volume", CVarFlags.Audio | CVarFlags.SaveToConfig, 0f, 2f);
            Register("a.voice_volume", "1.0", "Voice bus volume", CVarFlags.Audio | CVarFlags.SaveToConfig, 0f, 2f);
            Register("a.ui_volume", "0.7", "UI bus volume", CVarFlags.Audio | CVarFlags.SaveToConfig, 0f, 2f);
            Register("a.ambient_volume", "0.9", "Ambient bus volume", CVarFlags.Audio | CVarFlags.SaveToConfig, 0f, 2f);
            Register("a.hrtf", "true", "Enable HRTF spatial audio", CVarFlags.Audio | CVarFlags.SaveToConfig);
            Register("a.max_instances", "128", "Max concurrent audio instances", CVarFlags.Audio, 16, 1024);
            Register("a.doople_factor", "1.0", "Doppler effect strength", CVarFlags.Audio, 0f, 5f);
            Register("a.muted", "false", "Mute all audio", CVarFlags.Audio | CVarFlags.SaveToConfig);

            // Network
            Register("net.enabled", "true", "Enable networking subsystem", CVarFlags.Network | CVarFlags.SaveToConfig);
            Register("net.tick_rate", "60", "Network simulation tick rate", CVarFlags.Network | CVarFlags.SaveToConfig, 10, 120);
            Register("net.port", "7777", "Default network port", CVarFlags.Network, 1024, 65535);
            Register("net.max_players", "16", "Maximum connected players", CVarFlags.Network, 2, 256);
            Register("net.replication_rate", "30", "Entity replication updates per second", CVarFlags.Network, 1, 120);
            Register("net.lag_compensation", "true", "Enable server-side lag compensation", CVarFlags.Network | CVarFlags.SaveToConfig);
            Register("net.interpolation", "true", "Enable client-side interpolation", CVarFlags.Network | CVarFlags.SaveToConfig);
            Register("net.timeout", "30", "Connection timeout in seconds", CVarFlags.Network, 1, 300);
            Register("net.debug", "false", "Show network debug overlay", CVarFlags.Network | CVarFlags.Cheat);

            // Gameplay
            Register("g.difficulty", "normal", "Game difficulty (easy,normal,hard,insane)", CVarFlags.Gameplay | CVarFlags.SaveToConfig);
            Register("g.player_speed", "5.0", "Player movement speed m/s", CVarFlags.Gameplay | CVarFlags.SaveToConfig, 0.5f, 50f);
            Register("g.jump_height", "1.5", "Jump height in meters", CVarFlags.Gameplay | CVarFlags.SaveToConfig, 0.1f, 10f);
            Register("g.god_mode", "false", "Enable god mode (invincibility)", CVarFlags.Gameplay | CVarFlags.Cheat);
            Register("g.infinite_ammo", "false", "Infinite ammunition cheat", CVarFlags.Gameplay | CVarFlags.Cheat);
            Register("g.noclip", "false", "Enable noclip camera", CVarFlags.Gameplay | CVarFlags.Cheat);
            Register("g.time_of_day", "12.0", "Time of day 0-24", CVarFlags.Gameplay | CVarFlags.SaveToConfig, 0f, 24f);

            // UI
            Register("ui.scale", "1.0", "Global UI scale factor", CVarFlags.UI | CVarFlags.SaveToConfig, 0.5f, 3f);
            Register("ui.hud_enabled", "true", "Show HUD elements", CVarFlags.UI | CVarFlags.SaveToConfig);
            Register("ui.minimap", "true", "Show minimap", CVarFlags.UI | CVarFlags.SaveToConfig);
            Register("ui.crosshair", "true", "Show crosshair", CVarFlags.UI | CVarFlags.SaveToConfig);
            Register("ui.show_fps", "true", "Show FPS counter", CVarFlags.UI | CVarFlags.SaveToConfig);
            Register("ui.theme", "dark", "UI theme (dark, light, blue)", CVarFlags.UI | CVarFlags.SaveToConfig);

            // Commands
            RegisterCommand("help", "Show list of all variables and commands", args =>
            {
                Log(ConsoleLogMessage.LogLevel.Info, "=== Console Variables ===");
                foreach (var kvp in Variables)
                    Log(ConsoleLogMessage.LogLevel.Info, $"  {kvp.Key} = {kvp.Value.CurrentValue}  [{kvp.Value.Type}]  {kvp.Value.Description}");
                Log(ConsoleLogMessage.LogLevel.Info, "=== Console Commands ===");
                foreach (var kvp in Commands)
                    Log(ConsoleLogMessage.LogLevel.Info, $"  {kvp.Key}  {kvp.Value.Description}");
                return $"Found {Variables.Count} variables and {Commands.Count} commands";
            });

            RegisterCommand("set", "Set a variable: set <name> <value>", args =>
            {
                if (args.Length < 2) return "Usage: set <name> <value>";
                var name = args[0];
                var value = string.Join(" ", args.AsSpan(1).ToArray());
                if (!Variables.TryGetValue(name, out var v)) return $"Unknown variable: {name}";
                v.SetValue(value);
                return $"{name} = {v.CurrentValue}";
            });

            RegisterCommand("get", "Get a variable value: get <name>", args =>
            {
                if (args.Length < 1) return "Usage: get <name>";
                return Variables.TryGetValue(args[0], out var v) ? $"{args[0]} = {v.CurrentValue}" : $"Unknown: {args[0]}";
            });

            RegisterCommand("reset", "Reset variable to default: reset <name>", args =>
            {
                if (args.Length < 1) return "Usage: reset <name>";
                if (Variables.TryGetValue(args[0], out var v)) { v.Reset(); return $"{args[0]} reset to {v.DefaultValue}"; }
                return $"Unknown: {args[0]}";
            });

            RegisterCommand("reset_all", "Reset all variables to defaults", args =>
            {
                int count = 0;
                foreach (var v in Variables.Values) { v.Reset(); count++; }
                return $"Reset {count} variables";
            });

            RegisterCommand("echo", "Echo text to console: echo <text>", args =>
            {
                Log(ConsoleLogMessage.LogLevel.Info, string.Join(" ", args));
                return "";
            });

            RegisterCommand("clear", "Clear console history", args =>
            {
                History.Clear();
                return "Console cleared";
            });

            RegisterCommand("list", "List variables by prefix: list <prefix>", args =>
            {
                string prefix = args.Length > 0 ? args[0].ToLower() : "";
                int count = 0;
                foreach (var kvp in Variables)
                {
                    if (kvp.Key.ToLower().StartsWith(prefix))
                    {
                        Log(ConsoleLogMessage.LogLevel.Info, $"  {kvp.Key} = {kvp.Value.CurrentValue}  {kvp.Value.Description}");
                        count++;
                    }
                }
                return $"Found {count} variables";
            });

            RegisterCommand("save_config", "Save variables to config file: save_config <path>", args =>
            {
                string path = args.Length > 0 ? args[0] : "engine.cfg";
                SaveConfig(path);
                return $"Saved config to: {path}";
            });

            RegisterCommand("load_config", "Load variables from config: load_config <path>", args =>
            {
                string path = args.Length > 0 ? args[0] : "engine.cfg";
                LoadConfig(path);
                return $"Loaded config from: {path}";
            });

            RegisterCommand("toggle", "Toggle boolean variable: toggle <name>", args =>
            {
                if (args.Length < 1) return "Usage: toggle <name>";
                if (Variables.TryGetValue(args[0], out var v))
                {
                    bool b = v.CurrentValue == "1" || v.CurrentValue.ToLower() == "true";
                    v.SetValue(!b ? "true" : "false");
                    return $"{args[0]} = {v.CurrentValue}";
                }
                return $"Unknown: {args[0]}";
            });

            RegisterCommand("history", "Show last N log messages: history <count>", args =>
            {
                int count = args.Length > 0 && int.TryParse(args[0], out var c) ? c : 20;
                int start = Math.Max(0, History.Count - count);
                for (int i = start; i < History.Count; i++)
                    Log(ConsoleLogMessage.LogLevel.Info, History[i].ToString());
                return $"Showing {History.Count - start} messages";
            });

            RegisterCommand("exec", "Execute commands from file: exec <path>", args =>
            {
                if (args.Length < 1) return "Usage: exec <path>";
                try
                {
                    int executed = 0;
                    if (System.IO.File.Exists(args[0]))
                    {
                        foreach (var line in System.IO.File.ReadAllLines(args[0]))
                        {
                            var trimmed = line.Trim();
                            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                            ExecuteCommand(trimmed);
                            executed++;
                        }
                    }
                    return $"Executed {executed} commands from {args[0]}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }

        public ConsoleVariable Register(string name, string defaultValue, string description, CVarFlags flags = CVarFlags.SaveToConfig, float min = float.MinValue, float max = float.MaxValue)
        {
            var cvar = new ConsoleVariable
            {
                Name = name,
                Description = description,
                DefaultValue = defaultValue,
                CurrentValue = defaultValue,
                Flags = flags,
                MinValue = min,
                MaxValue = max,
                Type = CVarType.String
            };
            if (float.TryParse(defaultValue, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out float _f)) cvar.Type = CVarType.Float;
            else if (int.TryParse(defaultValue, out int _)) cvar.Type = CVarType.Int;
            else if (defaultValue.Equals("true", StringComparison.OrdinalIgnoreCase) || defaultValue.Equals("false", StringComparison.OrdinalIgnoreCase) || defaultValue == "1" || defaultValue == "0") cvar.Type = CVarType.Bool;
            Variables[name] = cvar;
            return cvar;
        }

        public void RegisterCommand(string name, string description, Action<string[]> action, CVarFlags flags = CVarFlags.None, string usage = "")
        {
            Commands[name] = new ConsoleCommand
            {
                Name = name,
                Description = description,
                Action = action,
                Flags = flags,
                Usage = usage
            };
        }

        public void RegisterCommand(string name, string description, Func<string[], string> func, CVarFlags flags = CVarFlags.None, string usage = "")
        {
            Commands[name] = new ConsoleCommand
            {
                Name = name,
                Description = description,
                Func = func,
                Flags = flags,
                Usage = usage
            };
        }

        public T Get<T>(string name, T defaultValue)
        {
            if (Variables.TryGetValue(name, out var v))
                return v.GetAs<T>();
            return defaultValue;
        }

        public float Get(string name, float defaultValue) => Get<float>(name, defaultValue);
        public int Get(string name, int defaultValue) => Get<int>(name, defaultValue);
        public bool Get(string name, bool defaultValue) => Get<bool>(name, defaultValue);
        public string Get(string name, string defaultValue) => Get<string>(name, defaultValue);

        public bool TrySet(string name, string value)
        {
            if (Variables.TryGetValue(name, out var v))
            {
                v.SetValue(value);
                OnVariableChanged?.Invoke(name, value);
                return true;
            }
            return false;
        }

        public string ExecuteCommand(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return "";
            var parts = commandLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";

            var name = parts[0];
            var args = new string[parts.Length - 1];
            for (int i = 0; i < args.Length; i++) args[i] = parts[i + 1];

            if (Commands.TryGetValue(name, out var cmd))
            {
                var result = cmd.Execute(args);
                if (!string.IsNullOrWhiteSpace(result)) Log(ConsoleLogMessage.LogLevel.Info, result);
                return result;
            }

            // Maybe it's variable = value style
            if (parts.Length >= 3 && parts[1] == "=")
            {
                if (Variables.TryGetValue(name, out var v))
                {
                    v.SetValue(string.Join(" ", args.AsSpan(1).ToArray()));
                    Log(ConsoleLogMessage.LogLevel.Info, $"{name} = {v.CurrentValue}");
                    return v.CurrentValue;
                }
            }

            if (Variables.TryGetValue(name, out var cvar))
            {
                Log(ConsoleLogMessage.LogLevel.Info, $"{name} = {cvar.CurrentValue}  [{cvar.Type}]  {cvar.Description}");
                return cvar.CurrentValue;
            }

            Log(ConsoleLogMessage.LogLevel.Error, $"Unknown command: {name}. Type 'help' for a list.");
            return "";
        }

        public void Log(ConsoleLogMessage.LogLevel level, string message, string category = "Console")
        {
            var msg = new ConsoleLogMessage
            {
                Level = level,
                Message = message,
                Timestamp = DateTime.Now,
                Category = category
            };

            History.Add(msg);
            if (History.Count > _maxHistory)
                History.RemoveRange(0, History.Count - _maxHistory);

            if (_echoToDebug)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine(msg.ToString());
                }
                catch { }
            }
            OnLogMessage?.Invoke(msg);
        }

        public void SaveConfig(string path)
        {
            try
            {
                var lines = new List<string> { "# Engine Configuration", $"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", "" };
                foreach (var kvp in Variables)
                {
                    if ((kvp.Value.Flags & CVarFlags.SaveToConfig) != 0)
                        lines.Add($"{kvp.Value.Name} = {kvp.Value.CurrentValue}");
                }
                System.IO.File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                Log(ConsoleLogMessage.LogLevel.Error, $"Failed to save config: {ex.Message}");
            }
        }

        public void LoadConfig(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Log(ConsoleLogMessage.LogLevel.Warning, $"Config file not found: {path}");
                    return;
                }
                int count = 0;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx < 0) continue;
                    var name = trimmed.Substring(0, eqIdx).Trim();
                    var value = trimmed.Substring(eqIdx + 1).Trim();
                    if (Variables.TryGetValue(name, out var v))
                    {
                        v.SetValue(value);
                        count++;
                    }
                }
                Log(ConsoleLogMessage.LogLevel.Info, $"Loaded {count} variables from {path}");
            }
            catch (Exception ex)
            {
                Log(ConsoleLogMessage.LogLevel.Error, $"Failed to load config: {ex.Message}");
            }
        }

        public List<ConsoleVariable> FindByPrefix(string prefix)
        {
            var result = new List<ConsoleVariable>();
            foreach (var kvp in Variables)
                if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(kvp.Value);
            return result;
        }

        public List<ConsoleVariable> FindByFlags(CVarFlags flags)
        {
            var result = new List<ConsoleVariable>();
            foreach (var kvp in Variables)
                if ((kvp.Value.Flags & flags) != 0)
                    result.Add(kvp.Value);
            return result;
        }

        public void ResetAll()
        {
            foreach (var v in Variables.Values) v.Reset();
        }
    }
}
