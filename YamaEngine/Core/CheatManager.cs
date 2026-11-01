using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace YamaEngine.Core;

/// <summary>
/// Developer cheat/console command system.
/// Enables runtime command execution for debugging and testing.
/// Commands can be registered by any system and executed from the developer console.
/// </summary>
public static class CheatManager
{
    private static readonly ConcurrentDictionary<string, CheatCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _history = new();
    private const int MaxHistory = 100;

    /// <summary>All registered cheat commands.</summary>
    public static IReadOnlyCollection<CheatCommand> AllCommands => (IReadOnlyCollection<CheatCommand>)_commands.Values;

    /// <summary>Command execution history.</summary>
    public static IReadOnlyList<string> History => _history.AsReadOnly();

    /// <summary>Fired when a command is executed (success or failure).</summary>
    public static event Action<string, bool, string>? OnCommandExecuted;

    #region Registration

    /// <summary>Register a cheat command.</summary>
    public static CheatCommand Register(string name, string description, Func<string[], string> handler,
        string? usage = null, bool hidden = false)
    {
        var cmd = new CheatCommand
        {
            Name = name,
            Description = description,
            Usage = usage ?? name,
            Handler = handler,
            Hidden = hidden,
        };
        _commands[name] = cmd;
        return cmd;
    }

    /// <summary>Register a cheat with no arguments.</summary>
    public static CheatCommand RegisterSimple(string name, string description, Func<string> handler, bool hidden = false)
    {
        return Register(name, description, _ =>
        {
            try { return handler(); }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }, name, hidden);
    }

    /// <summary>Unregister a command.</summary>
    public static bool Unregister(string name) => _commands.TryRemove(name, out _);

    /// <summary>Check if a command is registered.</summary>
    public static bool IsRegistered(string name) => _commands.ContainsKey(name);

    #endregion

    #region Execution

    /// <summary>Execute a command string (e.g. "god", "spawn BP_Enemy 3").</summary>
    public static string Execute(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Add to history
        _history.Add(input);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);

        // Parse: command name + args
        var parts = ParseCommandLine(input);
        if (parts.Length == 0) return string.Empty;

        string cmdName = parts[0];
        string[] args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

        if (!_commands.TryGetValue(cmdName, out var cmd))
        {
            string msg = $"Unknown command: '{cmdName}'. Type 'help' for available commands.";
            OnCommandExecuted?.Invoke(input, false, msg);
            return msg;
        }

        try
        {
            string result = cmd.Handler(args);
            OnCommandExecuted?.Invoke(input, true, result);
            return result;
        }
        catch (Exception ex)
        {
            string msg = $"Command '{cmdName}' failed: {ex.Message}";
            OnCommandExecuted?.Invoke(input, false, msg);
            return msg;
        }
    }

    /// <summary>Execute a command split into name and args.</summary>
    public static string Execute(string name, string[] args)
    {
        return Execute($"{name} {string.Join(" ", args)}");
    }

    /// <summary>Find commands matching a prefix (for autocomplete).</summary>
    public static CheatCommand[] Autocomplete(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return _commands.Values.Where(c => !c.Hidden).ToArray();

        return _commands.Values
            .Where(c => !c.Hidden && c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>Get help text for a specific command.</summary>
    public static string GetHelp(string name)
    {
        if (_commands.TryGetValue(name, out var cmd))
        {
            return $"{cmd.Name} - {cmd.Description}\nUsage: {cmd.Usage}";
        }
        return $"Unknown command: {name}";
    }

    /// <summary>Get full help text listing all commands.</summary>
    public static string GetFullHelp()
    {
        var lines = new List<string> { "Available commands:\n" };
        foreach (var cmd in _commands.Values.Where(c => !c.Hidden).OrderBy(c => c.Name))
        {
            lines.Add($"  {cmd.Name,-25} {cmd.Description}");
            if (!string.IsNullOrEmpty(cmd.Usage) && cmd.Usage != cmd.Name)
                lines.Add($"  {' ',25} Usage: {cmd.Usage}");
        }
        lines.Add($"\nTotal: {_commands.Values.Count(c => !c.Hidden)} commands");
        return string.Join('\n', lines);
    }

    #endregion

    #region Built-in Commands

    static CheatManager()
    {
        Register("help", "Display available commands", args =>
        {
            if (args.Length > 0) return GetHelp(args[0]);
            return GetFullHelp();
        }, "help [command]");

        Register("echo", "Print text to console", args =>
            string.Join(' ', args), "echo <text>");

        Register("clear", "Clear the console", _ =>
        {
            // Console clears itself; this is a placeholder
            return "Console cleared";
        }, hidden: true);

        RegisterSimple("cheats", "List all registered cheat commands", () =>
        {
            int visible = _commands.Count(c => !c.Value.Hidden);
            int hidden = _commands.Count - visible;
            return $"{visible} commands available, {hidden} hidden";
        });

        Register("exec", "Execute commands from a file", args =>
        {
            if (args.Length == 0) return "Usage: exec <filename>";
            string filePath = args[0];
            if (!File.Exists(filePath)) return $"File not found: {filePath}";

            int success = 0, fail = 0;
            foreach (var line in File.ReadAllLines(filePath))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var result = Execute(trimmed);
                if (result.StartsWith("Unknown") || result.StartsWith("Command"))
                    fail++;
                else
                    success++;
                Diagnostics.Logger.Info($">> {trimmed} => {result}");
            }
            return $"Executed {success + fail} commands: {success} OK, {fail} failed";
        }, "exec <filename>");

        RegisterSimple("history", "Show command history", () =>
        {
            var lines = _history.Select((cmd, i) => $"  {i + 1,4}:  {cmd}");
            return string.Join('\n', lines);
        });
    }

    #endregion

    #region Utilities

    private static string[] ParseCommandLine(string input)
    {
        var parts = new List<string>();
        var regex = new Regex(@"(""[^""]*""|[^ ""]+)+");
        foreach (Match m in regex.Matches(input.Trim()))
        {
            string val = m.Value;
            if (val.StartsWith('"') && val.EndsWith('"') && val.Length > 1)
                val = val[1..^1];
            parts.Add(val);
        }
        return parts.ToArray();
    }

    #endregion
}

/// <summary>A registered cheat command.</summary>
public sealed class CheatCommand
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Usage { get; set; } = "";
    public Func<string[], string>? Handler { get; set; }
    public bool Hidden { get; set; }
    public string? Category { get; set; }
}

/// <summary>Attribute to mark methods as cheat commands.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CheatCommandAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }
    public CheatCommandAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}
