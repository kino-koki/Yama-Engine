using System.Collections.Concurrent;
using System.Text.Json;
using ShanyuEngine.Core;

namespace ShanyuEngine.Gameplay;

/// <summary>
/// Achievement/trophy tracking system.
/// Supports progress-based achievements, hidden achievements,
/// platform integration hooks, and persistent storage.
/// </summary>
public sealed class AchievementSystem
{
    private readonly string _savePath;
    private readonly Dictionary<string, AchievementDef> _definitions = new();
    private readonly Dictionary<string, AchievementState> _states = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>Fired when an achievement is unlocked.</summary>
    public event Action<AchievementDef>? OnAchievementUnlocked;

    /// <summary>Fired when achievement progress changes.</summary>
    public event Action<AchievementDef, double>? OnProgressChanged;

    /// <summary>All defined achievements.</summary>
    public IReadOnlyCollection<AchievementDef> Definitions => _definitions.Values;

    /// <summary>Number of unlocked achievements.</summary>
    public int UnlockedCount => _states.Count(s => s.Value.Unlocked);

    /// <summary>Total achievements defined.</summary>
    public int TotalCount => _definitions.Count;

    /// <summary>Completion percentage (0.0 - 1.0).</summary>
    public double CompletionPercent => TotalCount > 0 ? (double)UnlockedCount / TotalCount : 0;

    public AchievementSystem(string? savePath = null)
    {
        _savePath = savePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShanyuEngine", "Achievements", "achievements.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
    }

    #region Definition Registration

    /// <summary>Register an achievement definition.</summary>
    public AchievementDef Register(string id, string displayName, string description,
        string? iconPath = null, bool hidden = false, double maxProgress = 1.0)
    {
        var def = new AchievementDef
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            IconPath = iconPath ?? "",
            Hidden = hidden,
            MaxProgress = maxProgress,
        };
        _definitions[id] = def;
        _states.TryAdd(id, new AchievementState { Id = id });
        return def;
    }

    /// <summary>Register multiple achievements at once.</summary>
    public void RegisterRange(params (string id, string name, string desc, bool hidden)[] achievements)
    {
        foreach (var (id, name, desc, hidden) in achievements)
            Register(id, name, desc, hidden: hidden);
    }

    /// <summary>Check if an achievement is registered.</summary>
    public bool IsRegistered(string id) => _definitions.ContainsKey(id);

    #endregion

    #region Progress & Unlock

    /// <summary>Unlock an achievement immediately.</summary>
    public bool Unlock(string id)
    {
        if (!_definitions.TryGetValue(id, out var def)) return false;

        if (_states.TryGetValue(id, out var state) && state.Unlocked)
            return true; // Already unlocked

        state ??= new AchievementState { Id = id };
        state.Unlocked = true;
        state.UnlockTime = DateTime.UtcNow;
        state.Progress = def.MaxProgress;
        _states[id] = state;

        Save();
        OnAchievementUnlocked?.Invoke(def);

        Diagnostics.Logger.Info($"[Achievement] Unlocked: {def.DisplayName}");
        return true;
    }

    /// <summary>Set progress for a progress-based achievement.</summary>
    public bool SetProgress(string id, double progress)
    {
        if (!_definitions.TryGetValue(id, out var def)) return false;
        if (progress < 0) progress = 0;

        if (_states.TryGetValue(id, out var state) && state.Unlocked)
            return true;

        state ??= new AchievementState { Id = id };
        double clamped = Math.Min(progress, def.MaxProgress);
        state.Progress = clamped;
        _states[id] = state;

        OnProgressChanged?.Invoke(def, clamped);

        // Auto-unlock if progress is complete
        if (clamped >= def.MaxProgress)
            return Unlock(id);

        Save();
        return true;
    }

    /// <summary>Increment progress by a delta.</summary>
    public bool AddProgress(string id, double delta)
    {
        double current = GetProgress(id);
        return SetProgress(id, current + delta);
    }

    /// <summary>Get current progress for an achievement.</summary>
    public double GetProgress(string id)
    {
        return _states.TryGetValue(id, out var state) ? state.Progress : 0;
    }

    /// <summary>Check if an achievement is unlocked.</summary>
    public bool IsUnlocked(string id)
    {
        return _states.TryGetValue(id, out var state) && state.Unlocked;
    }

    /// <summary>Get unlock time for an achievement.</summary>
    public DateTime? GetUnlockTime(string id)
    {
        return _states.TryGetValue(id, out var state) && state.Unlocked
            ? state.UnlockTime : null;
    }

    #endregion

    #region Bulk Operations

    /// <summary>Unlock multiple achievements at once.</summary>
    public int UnlockAll(params string[] ids)
    {
        int count = 0;
        foreach (var id in ids)
            if (Unlock(id)) count++;
        return count;
    }

    /// <summary>Reset a specific achievement.</summary>
    public bool Reset(string id)
    {
        if (!_definitions.ContainsKey(id)) return false;
        _states[id] = new AchievementState { Id = id };
        Save();
        return true;
    }

    /// <summary>Reset all achievements.</summary>
    public void ResetAll()
    {
        foreach (var kvp in _definitions)
            _states[kvp.Key] = new AchievementState { Id = kvp.Key };
        Save();
    }

    /// <summary>Get recently unlocked achievements (newest first).</summary>
    public AchievementDef[] GetRecentUnlocks(int count = 5)
    {
        return _states
            .Where(s => s.Value.Unlocked)
            .OrderByDescending(s => s.Value.UnlockTime)
            .Take(count)
            .Select(s => _definitions.TryGetValue(s.Key, out var d) ? d : null)
            .Where(d => d != null)
            .Select(d => d!)
            .ToArray();
    }

    #endregion

    #region Persistence

    /// <summary>Save achievement state to disk.</summary>
    public void Save()
    {
        try
        {
            var data = new AchievementSaveData
            {
                States = _states.Values.ToList(),
                LastUpdated = DateTime.UtcNow,
            };
            string json = JsonSerializer.Serialize(data, _json);
            File.WriteAllText(_savePath, json);
        }
        catch (Exception ex)
        {
            Diagnostics.Logger.Error($"[Achievement] Save failed: {ex.Message}");
        }
    }

    /// <summary>Load achievement state from disk.</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_savePath)) return;
            string json = File.ReadAllText(_savePath);
            var data = JsonSerializer.Deserialize<AchievementSaveData>(json, _json);
            if (data?.States == null) return;

            foreach (var state in data.States)
                _states[state.Id] = state;

            Diagnostics.Logger.Info($"[Achievement] Loaded {UnlockedCount}/{TotalCount} achievements");
        }
        catch (Exception ex)
        {
            Diagnostics.Logger.Error($"[Achievement] Load failed: {ex.Message}");
        }
    }

    #endregion
}

#region Data Types

/// <summary>An achievement definition (registered once, immutable at runtime).</summary>
public sealed class AchievementDef
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconPath { get; set; } = "";
    public bool Hidden { get; set; }
    public double MaxProgress { get; set; } = 1.0;
}

/// <summary>Runtime state of an achievement (persisted).</summary>
internal sealed class AchievementState
{
    public string Id { get; set; } = "";
    public bool Unlocked { get; set; }
    public DateTime UnlockTime { get; set; }
    public double Progress { get; set; }
}

internal sealed class AchievementSaveData
{
    public List<AchievementState> States { get; set; } = new();
    public DateTime LastUpdated { get; set; }
}

#endregion
