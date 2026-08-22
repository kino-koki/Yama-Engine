using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ShanyuEngine.Core;

namespace ShanyuEngine.Gameplay;

/// <summary>
/// Game save/load system for user progress persistence.
/// Supports multiple save slots, metadata, compression, and versioning.
/// </summary>
public sealed class SaveGameSystem
{
    private readonly string _saveDirectory;
    private readonly string _gameName;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Fired when a save operation completes.</summary>
    public event Action<string, bool>? OnSaveComplete;

    /// <summary>Fired when a load operation completes.</summary>
    public event Action<string, bool>? OnLoadComplete;

    public SaveGameSystem(string gameName, string? customPath = null)
    {
        _gameName = gameName;
        _saveDirectory = customPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            gameName, "Saves");
        Directory.CreateDirectory(_saveDirectory);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true,
        };
    }

    #region Save Slot Management

    /// <summary>Get list of all save slots with metadata.</summary>
    public SaveSlotInfo[] ListSlots()
    {
        if (!Directory.Exists(_saveDirectory))
            return Array.Empty<SaveSlotInfo>();

        return Directory.GetFiles(_saveDirectory, "*.sav")
            .Select(ReadSlotInfo)
            .Where(s => s != null)
            .OrderByDescending(s => s!.LastWriteTime)
            .Select(s => s!)
            .ToArray();
    }

    /// <summary>Check if a save slot exists.</summary>
    public bool SlotExists(string slotName)
    {
        string path = GetSlotPath(slotName);
        return File.Exists(path);
    }

    /// <summary>Delete a save slot.</summary>
    public bool DeleteSlot(string slotName)
    {
        try
        {
            string path = GetSlotPath(slotName);
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Logger.Error($"[SaveGame] Failed to delete slot '{slotName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Get total disk space used by saves in bytes.</summary>
    public long GetTotalDiskUsage()
    {
        if (!Directory.Exists(_saveDirectory)) return 0;
        return Directory.GetFiles(_saveDirectory, "*.sav")
            .Sum(f => new FileInfo(f).Length);
    }

    #endregion

    #region Save/Load

    /// <summary>Save game data to a slot.</summary>
    public bool Save<T>(string slotName, T data, SaveMetadata? metadata = null) where T : class
    {
        try
        {
            var saveData = new SaveFile<T>
            {
                Metadata = metadata ?? new SaveMetadata
                {
                    SlotName = slotName,
                    GameName = _gameName,
                    SaveVersion = 1,
                    Timestamp = DateTime.UtcNow,
                },
                GameData = data,
            };

            string json = JsonSerializer.Serialize(saveData, _jsonOptions);
            byte[] compressed = Compress(Encoding.UTF8.GetBytes(json));
            string path = GetSlotPath(slotName);
            File.WriteAllBytes(path, compressed);

            Diagnostics.Logger.Info($"[SaveGame] Saved '{slotName}': {compressed.Length / 1024}KB");
            OnSaveComplete?.Invoke(slotName, true);
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Logger.Error($"[SaveGame] Save failed for '{slotName}': {ex.Message}");
            OnSaveComplete?.Invoke(slotName, false);
            return false;
        }
    }

    /// <summary>Load game data from a slot.</summary>
    public T? Load<T>(string slotName) where T : class
    {
        try
        {
            string path = GetSlotPath(slotName);
            if (!File.Exists(path))
            {
                Diagnostics.Logger.Warn($"[SaveGame] Slot '{slotName}' not found");
                OnLoadComplete?.Invoke(slotName, false);
                return null;
            }

            byte[] compressed = File.ReadAllBytes(path);
            byte[] jsonBytes = Decompress(compressed);
            string json = Encoding.UTF8.GetString(jsonBytes);

            var saveData = JsonSerializer.Deserialize<SaveFile<T>>(json, _jsonOptions);
            if (saveData == null)
            {
                Diagnostics.Logger.Error($"[SaveGame] Deserialization failed for '{slotName}'");
                OnLoadComplete?.Invoke(slotName, false);
                return null;
            }

            Diagnostics.Logger.Info($"[SaveGame] Loaded '{slotName}': version {saveData.Metadata?.SaveVersion}");
            OnLoadComplete?.Invoke(slotName, true);
            return saveData.GameData;
        }
        catch (Exception ex)
        {
            Diagnostics.Logger.Error($"[SaveGame] Load failed for '{slotName}': {ex.Message}");
            OnLoadComplete?.Invoke(slotName, false);
            return null;
        }
    }

    /// <summary>Async save using thread pool.</summary>
    public System.Threading.Tasks.Task<bool> SaveAsync<T>(string slotName, T data, SaveMetadata? metadata = null) where T : class
    {
        return System.Threading.Tasks.Task.Run(() => Save(slotName, data, metadata));
    }

    /// <summary>Async load using thread pool.</summary>
    public System.Threading.Tasks.Task<T?> LoadAsync<T>(string slotName) where T : class
    {
        return System.Threading.Tasks.Task.Run(() => Load<T>(slotName));
    }

    #endregion

    #region Auto-Save

    private string? _lastAutoSlot;
    private DateTime _lastAutoSave;

    /// <summary>Perform auto-save if enough time has passed since last auto-save.</summary>
    public bool AutoSave<T>(T data, double minIntervalSeconds = 120) where T : class
    {
        if ((DateTime.UtcNow - _lastAutoSave).TotalSeconds < minIntervalSeconds)
            return false;

        string slotName = $"autosave_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        bool result = Save(slotName, data, new SaveMetadata
        {
            SlotName = slotName,
            GameName = _gameName,
            SaveVersion = 1,
            Timestamp = DateTime.UtcNow,
            IsAutoSave = true,
        });

        if (result)
        {
            _lastAutoSave = DateTime.UtcNow;
            _lastAutoSlot = slotName;

            // Keep only last 5 auto-saves
            var autoSaves = ListSlots().Where(s => s.IsAutoSave).ToArray();
            if (autoSaves.Length > 5)
            {
                foreach (var old in autoSaves.SkipLast(5))
                    DeleteSlot(old.SlotName);
            }
        }

        return result;
    }

    /// <summary>Get the most recent auto-save slot name.</summary>
    public string? GetLastAutoSaveSlot() => _lastAutoSlot;

    #endregion

    #region Utilities

    private string GetSlotPath(string slotName)
    {
        // Sanitize slot name
        string safe = string.Concat(slotName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_saveDirectory, $"{safe}.sav");
    }

    private SaveSlotInfo? ReadSlotInfo(string filePath)
    {
        try
        {
            byte[] compressed = File.ReadAllBytes(filePath);
            byte[] jsonBytes = Decompress(compressed);
            string json = Encoding.UTF8.GetString(jsonBytes);

            using var doc = JsonDocument.Parse(json);
            var meta = doc.RootElement.GetProperty("metadata");
            var fi = new FileInfo(filePath);

            return new SaveSlotInfo
            {
                SlotName = meta.GetProperty("slotName").GetString() ?? Path.GetFileNameWithoutExtension(filePath),
                GameName = meta.GetProperty("gameName").GetString() ?? "",
                SaveVersion = meta.GetProperty("saveVersion").GetInt32(),
                Timestamp = DateTime.Parse(meta.GetProperty("timestamp").GetString() ?? "2000-01-01"),
                IsAutoSave = meta.TryGetProperty("isAutoSave", out var auto) && auto.GetBoolean(),
                FileSize = fi.Length,
                LastWriteTime = fi.LastWriteTimeUtc,
            };
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    #endregion
}

#region Data Types

/// <summary>Save slot metadata (readable without loading full save).</summary>
public sealed class SaveMetadata
{
    public string SlotName { get; set; } = "";
    public string GameName { get; set; } = "";
    public int SaveVersion { get; set; } = 1;
    public DateTime Timestamp { get; set; }
    public bool IsAutoSave { get; set; }
    public TimeSpan PlayTime { get; set; }
    public string? ScreenshotPath { get; set; }
    public string? LevelName { get; set; }
    public int PlayerLevel { get; set; }
}

/// <summary>Save slot info (for save selection UI).</summary>
public sealed class SaveSlotInfo
{
    public string SlotName { get; set; } = "";
    public string GameName { get; set; } = "";
    public int SaveVersion { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsAutoSave { get; set; }
    public long FileSize { get; set; }
    public DateTime LastWriteTime { get; set; }

    public string FileSizeFormatted => FileSize switch
    {
        < 1024 => $"{FileSize}B",
        < 1024 * 1024 => $"{FileSize / 1024}KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F1}MB"
    };

    public string TimeAgo
    {
        get
        {
            var span = DateTime.UtcNow - Timestamp;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
            return Timestamp.ToString("yyyy-MM-dd");
        }
    }
}

/// <summary>Internal save file wrapper.</summary>
internal sealed class SaveFile<T> where T : class
{
    public SaveMetadata? Metadata { get; set; }
    public T? GameData { get; set; }
    public string FormatVersion { get; set; } = "1.0";
}

#endregion
