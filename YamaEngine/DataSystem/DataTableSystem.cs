using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamaEngine.DataSystem
{
    // ====================================================================
    //  YamaEngine Data System (DataTable + CurveTable + Config + SmartObject)
    // ====================================================================
    //  Comprehensive data table system, curve tables, hierarchical config
    //  management, and smart object interaction system.
    // ====================================================================

    // ─────────────────────────────────────────────────────────────────────
    //  Part 1: DataTable Row Base and Validation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Base class for all data table rows.</summary>
    public class DataTableRow
    {
        public string Name { get; set; } = string.Empty;
        public int RowIndex { get; set; } = -1;
        public virtual void FromDictionary(Dictionary<string, string> fields) { }
        public virtual Dictionary<string, string> ToDictionary() => new();
    }

    /// <summary>Validation result container.</summary>
    public sealed class DataTableValidationResult
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool IsValid => Errors.Count == 0;
        public void AddError(string error) => Errors.Add(error);
        public void AddWarning(string warning) => Warnings.Add(warning);
        public override string ToString()
        {
            var sb = new StringBuilder();
            if (IsValid) sb.AppendLine("Validation passed.");
            foreach (var e in Errors) sb.AppendLine($"  ERROR: {e}");
            foreach (var w in Warnings) sb.AppendLine($"  WARNING: {w}");
            return sb.ToString();
        }
    }

    /// <summary>Editor integration interface for table editing support.</summary>
    public interface IDataTableEditorIntegration
    {
        List<string> GetEditableColumns();
        Type GetColumnType(string columnName);
        string GetColumnDisplayName(string columnName);
        List<string>? GetColumnAllowedValues(string columnName);
        bool IsColumnReadOnly(string columnName);
        void OnCellValueChanged(string rowName, string columnName, object? newValue);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 2: DataTable<TRow> — Generic Data Table
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Generic data table with row storage, lookup, sort, filter, and import/export.</summary>
    public class DataTable<TRow> where TRow : DataTableRow, new()
    {
        private readonly Dictionary<string, TRow> _rowsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TRow> _rows = new();
        private readonly List<string> _columnNames = new();
        private string _tableName = "UnnamedTable";
        private IDataTableEditorIntegration? _editorIntegration;

        public string TableName { get => _tableName; set => _tableName = value ?? "UnnamedTable"; }
        public int RowCount => _rows.Count;
        public IReadOnlyList<string> ColumnNames => _columnNames;
        public IReadOnlyList<TRow> Rows => _rows;
        public IDataTableEditorIntegration? EditorIntegration { get => _editorIntegration; set => _editorIntegration = value; }

        public event Action<TRow>? OnRowAdded;
        public event Action<string>? OnRowRemoved;
        public event Action<TRow>? OnRowModified;
        public event Action? OnTableCleared;

        public List<string> GetRowNames() => _rowsByName.Keys.ToList();
        public TRow? FindRow(string name) => _rowsByName.TryGetValue(name, out var row) ? row : null;
        public TRow? FindRowByPredicate(Func<TRow, bool> predicate) => _rows.FirstOrDefault(predicate);
        public List<TRow> FindAllRows(Func<TRow, bool> predicate) => _rows.Where(predicate).ToList();
        public TRow? GetRowAt(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;
        public bool ContainsRow(string name) => _rowsByName.ContainsKey(name);

        public bool AddRow(TRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (string.IsNullOrEmpty(row.Name)) throw new ArgumentException("Row name cannot be empty.");
            if (_rowsByName.ContainsKey(row.Name)) throw new InvalidOperationException($"Duplicate row name: {row.Name}");
            row.RowIndex = _rows.Count; _rows.Add(row); _rowsByName[row.Name] = row;
            foreach (var col in row.ToDictionary().Keys) if (!_columnNames.Contains(col)) _columnNames.Add(col);
            OnRowAdded?.Invoke(row); return true;
        }

        public bool AddOrReplaceRow(TRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (string.IsNullOrEmpty(row.Name)) throw new ArgumentException("Row name cannot be empty.");
            if (_rowsByName.TryGetValue(row.Name, out var existing)) { row.RowIndex = existing.RowIndex; _rows[existing.RowIndex] = row; _rowsByName[row.Name] = row; OnRowModified?.Invoke(row); return true; }
            return AddRow(row);
        }

        public bool RemoveRow(string name)
        {
            if (!_rowsByName.TryGetValue(name, out var row)) return false;
            _rowsByName.Remove(name); _rows.Remove(row);
            for (int i = 0; i < _rows.Count; i++) _rows[i].RowIndex = i;
            OnRowRemoved?.Invoke(name); return true;
        }

        public bool ModifyRow(string name, Action<TRow> modifier)
        {
            if (!_rowsByName.TryGetValue(name, out var row)) return false;
            modifier(row); OnRowModified?.Invoke(row); return true;
        }

        public void Merge(DataTable<TRow> other) { if (other == null) return; foreach (var row in other._rows) AddOrReplaceRow(row); }

        public DataTable<TRow> Copy()
        {
            var copy = new DataTable<TRow> { TableName = TableName };
            foreach (var row in _rows) { var newRow = new TRow { Name = row.Name, RowIndex = row.RowIndex }; newRow.FromDictionary(row.ToDictionary()); copy.AddOrReplaceRow(newRow); }
            return copy;
        }

        public void Clear() { _rows.Clear(); _rowsByName.Clear(); _columnNames.Clear(); OnTableCleared?.Invoke(); }
        public void Sort(Comparison<TRow> comparison) => _rows.Sort(comparison);
        public void SortByName() => Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        public DataTable<TRow> Filter(Func<TRow, bool> predicate)
        {
            var result = new DataTable<TRow> { TableName = $"{TableName}_Filtered" };
            foreach (var row in _rows) { if (predicate(row)) { var newRow = new TRow { Name = row.Name }; newRow.FromDictionary(row.ToDictionary()); result.AddRow(newRow); } }
            return result;
        }

        public DataTable<TRow> Paginate(int page, int pageSize) { return _rows.Skip(page * pageSize).Take(pageSize).Aggregate(new DataTable<TRow> { TableName = $"{TableName}_P{page}" }, (t, r) => { var nr = new TRow { Name = r.Name }; nr.FromDictionary(r.ToDictionary()); t.AddOrReplaceRow(nr); return t; }); }
        public int PageCount(int pageSize) => (int)Math.Ceiling((double)_rows.Count / Math.Max(1, pageSize));

        // ── CSV Import/Export ──

        public int ImportCsv(string csv) { var lines = csv.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None); return ImportCsvLines(lines); }
        public int ImportCsvFile(string filePath) { if (!File.Exists(filePath)) return 0; return ImportCsvLines(File.ReadAllLines(filePath, Encoding.UTF8)); }

        private int ImportCsvLines(string[] lines)
        {
            Clear(); if (lines.Length < 2) return 0;
            var headers = ParseCsvLine(lines[0]); _columnNames.Clear();
            foreach (var h in headers) { var col = h.Trim(); if (!string.IsNullOrEmpty(col)) _columnNames.Add(col); }
            int imported = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim(); if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var values = ParseCsvLine(line);
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int j = 0; j < Math.Min(values.Length, headers.Length); j++) fields[headers[j].Trim()] = values[j].Trim();
                var row = new TRow(); row.FromDictionary(fields);
                if (string.IsNullOrEmpty(row.Name) && fields.TryGetValue("Name", out var name)) row.Name = name;
                if (!string.IsNullOrEmpty(row.Name)) { try { AddOrReplaceRow(row); imported++; } catch { } }
            }
            return imported;
        }

        public string ExportCsv()
        {
            if (_rows.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            var allColumns = new List<string> { "Name" };
            foreach (var col in _columnNames) if (!col.Equals("Name", StringComparison.OrdinalIgnoreCase)) allColumns.Add(col);
            sb.AppendLine(string.Join(",", allColumns));
            foreach (var row in _rows) { var fields = row.ToDictionary(); var values = new List<string>(); foreach (var col in allColumns) { fields.TryGetValue(col, out var val); values.Add(EscapeCsvValue(val ?? "")); } sb.AppendLine(string.Join(",", values)); }
            return sb.ToString();
        }

        public bool ExportCsvFile(string filePath) { try { var dir = Path.GetDirectoryName(filePath); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.WriteAllText(filePath, ExportCsv(), Encoding.UTF8); return true; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to export CSV: {ex.Message}"); return false; } }

        // ── JSON Import/Export ──

        public int ImportJson(string json)
        {
            Clear();
            try
            {
                var doc = JsonDocument.Parse(json); if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
                int imported = 0;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in element.EnumerateObject()) fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "") : prop.Value.GetRawText();
                    var row = new TRow(); row.FromDictionary(fields);
                    if (!string.IsNullOrEmpty(row.Name)) { try { AddOrReplaceRow(row); imported++; } catch { } }
                }
                return imported;
            } catch { return 0; }
        }

        public int ImportJsonFile(string filePath) { if (!File.Exists(filePath)) return 0; return ImportJson(File.ReadAllText(filePath, Encoding.UTF8)); }

        public string ExportJson() { var list = new List<Dictionary<string, string>>(); foreach (var row in _rows) { var dict = row.ToDictionary(); dict["Name"] = row.Name; list.Add(dict); } return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }); }
        public bool ExportJsonFile(string filePath) { try { var dir = Path.GetDirectoryName(filePath); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.WriteAllText(filePath, ExportJson(), Encoding.UTF8); return true; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to export JSON: {ex.Message}"); return false; } }

        // ── Binary Import/Export ──

        public byte[] ExportBinary()
        {
            using var ms = new MemoryStream(); using var writer = new BinaryWriter(ms, Encoding.UTF8);
            writer.Write(_tableName); writer.Write(_rows.Count); writer.Write(_columnNames.Count);
            foreach (var col in _columnNames) writer.Write(col);
            foreach (var row in _rows) { writer.Write(row.Name); var fields = row.ToDictionary(); writer.Write(fields.Count); foreach (var kvp in fields) { writer.Write(kvp.Key); writer.Write(kvp.Value); } }
            return ms.ToArray();
        }

        public bool ImportBinary(byte[] data)
        {
            Clear();
            try
            {
                using var ms = new MemoryStream(data); using var reader = new BinaryReader(ms, Encoding.UTF8);
                _tableName = reader.ReadString(); int rowCount = reader.ReadInt32(); int colCount = reader.ReadInt32(); _columnNames.Clear();
                for (int i = 0; i < colCount; i++) _columnNames.Add(reader.ReadString());
                for (int i = 0; i < rowCount; i++)
                {
                    var name = reader.ReadString(); int fieldCount = reader.ReadInt32();
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int j = 0; j < fieldCount; j++) fields[reader.ReadString()] = reader.ReadString();
                    var row = new TRow(); row.FromDictionary(fields); row.Name = name;
                    try { AddOrReplaceRow(row); } catch { }
                }
                return true;
            } catch { return false; }
        }

        public DataTableValidationResult Validate()
        {
            var result = new DataTableValidationResult();
            if (_rows.Count == 0) result.AddWarning("Table is empty.");
            var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                if (string.IsNullOrEmpty(row.Name)) result.AddError($"Row at index {row.RowIndex} has empty name.");
                else if (!nameSet.Add(row.Name)) result.AddError($"Duplicate row name: {row.Name}");
            }
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].RowIndex != i) { result.AddWarning($"Row '{_rows[i].Name}' has inconsistent index {_rows[i].RowIndex}, expected {i}."); _rows[i].RowIndex = i; }
            return result;
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>(); var sb = new StringBuilder(); bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && !inQuotes) { inQuotes = true; continue; }
                if (c == '"' && inQuotes) { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQuotes = false; continue; }
                if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            result.Add(sb.ToString()); return result.ToArray();
        }

        private static string EscapeCsvValue(string value) => (value.Contains(',') || value.Contains('"') || value.Contains('\n')) ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        public IEnumerator<TRow> GetEnumerator() => _rows.GetEnumerator();
        public IEnumerable<string> RowNames => _rowsByName.Keys;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 3: DataTableImporter/Exporter (Standalone)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Standalone data table importer supporting CSV, JSON, XML, and binary formats.</summary>
    public static class DataTableImporter
    {
        public static DataTable<TRow> ImportFromCsv<TRow>(string filePath) where TRow : DataTableRow, new()
        {
            var table = new DataTable<TRow> { TableName = Path.GetFileNameWithoutExtension(filePath) };
            table.ImportCsvFile(filePath);
            return table;
        }

        public static DataTable<TRow> ImportFromJson<TRow>(string filePath) where TRow : DataTableRow, new()
        {
            var table = new DataTable<TRow> { TableName = Path.GetFileNameWithoutExtension(filePath) };
            table.ImportJsonFile(filePath);
            return table;
        }

        public static DataTable<TRow> ImportFromBinary<TRow>(string filePath) where TRow : DataTableRow, new()
        {
            var table = new DataTable<TRow> { TableName = Path.GetFileNameWithoutExtension(filePath) };
            table.ImportBinary(File.ReadAllBytes(filePath));
            return table;
        }

        public static DataTable<TRow> ImportFromDirectory<TRow>(string directory, string searchPattern = "*.csv") where TRow : DataTableRow, new()
        {
            var combined = new DataTable<TRow> { TableName = "Combined" };
            foreach (var file in Directory.GetFiles(directory, searchPattern, SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var sub = new DataTable<TRow> { TableName = Path.GetFileNameWithoutExtension(file) };
                if (ext == ".csv") sub.ImportCsvFile(file);
                else if (ext == ".json") sub.ImportJsonFile(file);
                combined.Merge(sub);
            }
            return combined;
        }

        /// <summary>Import with automatic format detection based on file extension.</summary>
        public static DataTable<TRow> Import<TRow>(string filePath) where TRow : DataTableRow, new()
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch { ".csv" => ImportFromCsv<TRow>(filePath), ".json" => ImportFromJson<TRow>(filePath), ".bin" or ".dat" => ImportFromBinary<TRow>(filePath), _ => throw new NotSupportedException($"Unsupported file format: {ext}") };
        }
    }

    /// <summary>Standalone data table exporter supporting multiple formats.</summary>
    public static class DataTableExporter
    {
        public static bool ExportToCsv<TRow>(DataTable<TRow> table, string filePath) where TRow : DataTableRow, new() => table.ExportCsvFile(filePath);
        public static bool ExportToJson<TRow>(DataTable<TRow> table, string filePath) where TRow : DataTableRow, new() => table.ExportJsonFile(filePath);
        public static bool ExportToBinary<TRow>(DataTable<TRow> table, string filePath) where TRow : DataTableRow, new() { try { var dir = Path.GetDirectoryName(filePath); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.WriteAllBytes(filePath, table.ExportBinary()); return true; } catch { return false; } }

        public static bool Export<TRow>(DataTable<TRow> table, string filePath) where TRow : DataTableRow, new()
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch { ".csv" => ExportToCsv(table, filePath), ".json" => ExportToJson(table, filePath), ".bin" or ".dat" => ExportToBinary(table, filePath), _ => throw new NotSupportedException($"Unsupported format: {ext}") };
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 4: CurveTable (FloatCurve / VectorCurve / SimpleCurve)
    // ─────────────────────────────────────────────────────────────────────


    public enum CurveType { FloatCurve, VectorCurve, SimpleCurve }

    /// <summary>Float curve data with keyframes and interpolation.</summary>

    /// <summary>Vector curve data: 3 float curves for X/Y/Z.</summary>
    public sealed class VectorCurveData
    {
        public CurveData X { get; } = new(); public CurveData Y { get; } = new(); public CurveData Z { get; } = new();
        public float Length => Math.Max(Math.Max(X.Length, Y.Length), Z.Length);
        public void AddKey(float time, Vector3 value, CurveInterpMode mode = CurveInterpMode.Linear) { X.AddKey(time, value.X, mode); Y.AddKey(time, value.Y, mode); Z.AddKey(time, value.Z, mode); }
        public Vector3 Evaluate(float time) => new Vector3(X.Evaluate(time), Y.Evaluate(time), Z.Evaluate(time));
        public Vector3 EvaluateDerivative(float time) => new Vector3(X.EvaluateDerivative(time), Y.EvaluateDerivative(time), Z.EvaluateDerivative(time));
    }

    /// <summary>Simple curve: single keyframe pair (start, end) with interpolation.</summary>
    public sealed class SimpleCurveData
    {
        public float StartTime { get; set; } = 0f;
        public float EndTime { get; set; } = 1f;
        public float StartValue { get; set; } = 0f;
        public float EndValue { get; set; } = 1f;
        public CurveInterpMode InterpMode { get; set; } = CurveInterpMode.Linear;
        public float Duration => EndTime - StartTime;
        public float Evaluate(float time) { float t = Math.Clamp((time - StartTime) / Math.Max(1e-8f, Duration), 0f, 1f); return InterpMode == CurveInterpMode.Constant ? StartValue : StartValue + (EndValue - StartValue) * t; }
    }

    /// <summary>Curve table: named collection of curves. Like UE UCurveTable.</summary>

    // ─────────────────────────────────────────────────────────────────────
    //  Part 5: ConfigManager (Hierarchical Override: Default/Project/Platform/CommandLine)
    // ─────────────────────────────────────────────────────────────────────

    public enum ConfigLayer { Default = 0, Platform = 1, Project = 2, User = 3, CommandLine = 4 }


    /// <summary>Game config base: hot-reloadable runtime config container.</summary>

    /// <summary>Config manager with hierarchical override layers: Default -> Platform -> Project -> User -> CommandLine.</summary>
    public sealed class ConfigManager
    {
        private readonly Dictionary<ConfigLayer, GameConfig> _layers = new();
        private readonly Dictionary<string, ConfigValidationRule> _rules = new(StringComparer.OrdinalIgnoreCase);
        public event Action<string, object?, object?>? OnConfigChanged;

        public ConfigManager()
        {
            foreach (ConfigLayer layer in Enum.GetValues(typeof(ConfigLayer)))
                _layers[layer] = new GameConfig { ConfigName = $"Config_{layer}" };
        }

        /// <summary>Get config value with layer priority (CommandLine > User > Project > Platform > Default).</summary>
        public T? Get<T>(string key, T? defaultValue = default)
        {
            for (int i = (int)ConfigLayer.CommandLine; i >= 0; i--)
            {
                var layer = (ConfigLayer)i;
                if (_layers.TryGetValue(layer, out var config) && config.Has(key))
                    return config.Get<T>(key);
            }
            return defaultValue;
        }

        /// <summary>Set config value at a specific layer.</summary>
        public bool Set(string key, object? value, ConfigLayer layer = ConfigLayer.Project)
        {
            if (_rules.TryGetValue(key, out var rule)) { var (isValid, error) = rule.Validate(value); if (!isValid) { System.Diagnostics.Debug.WriteLine($"Config validation failed: {error}"); return false; } }
            return _layers[layer].Set(key, value);
        }

        public bool Has(string key) { for (int i = (int)ConfigLayer.CommandLine; i >= 0; i--) if (_layers.TryGetValue((ConfigLayer)i, out var c) && c.Has(key)) return true; return false; }
        public bool Remove(string key, ConfigLayer layer = ConfigLayer.Project) => _layers[layer].Remove(key);
        public IEnumerable<string> GetAllKeys() { var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var layer in _layers.Values) foreach (var k in layer.GetAllKeys()) keys.Add(k); return keys; }

        public void AddValidationRule(ConfigValidationRule rule) => _rules[rule.Key] = rule;
        public GameConfig GetLayer(ConfigLayer layer) => _layers[layer];

        /// <summary>Load config from file into specified layer.</summary>
        public void LoadLayer(ConfigLayer layer, string filePath) { _layers[layer].ImportFile(filePath); }

        /// <summary>Load command-line arguments (key=value or /key:value format).</summary>
        public void LoadCommandLine(string[] args)
        {
            var config = _layers[ConfigLayer.CommandLine];
            foreach (var arg in args)
            {
                var trimmed = arg.TrimStart('-', '/');
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx >= 0) config.Set(trimmed.Substring(0, eqIdx).Trim(), trimmed.Substring(eqIdx + 1).Trim());
                else if (trimmed.Contains(':')) { var parts = trimmed.Split(':', 2); config.Set(parts[0].Trim(), parts[1].Trim()); }
            }
        }

        /// <summary>Get all values merged into a single dictionary (layer override applied).</summary>
        public Dictionary<string, object?> GetAllMerged()
        {
            var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i <= (int)ConfigLayer.CommandLine; i++)
            {
                var layer = (ConfigLayer)i;
                foreach (var key in _layers[layer].GetAllKeys())
                    merged[key] = _layers[layer].Get<object?>(key);
            }
            return merged;
        }

        public void ReloadAll() { foreach (var layer in _layers.Values) layer.NotifyConfigReloaded(); }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Part 6: Smart Object Interaction System
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Smart object interaction type.</summary>
    public enum SmartObjectInteractionType { Use, Examine, PickUp, Drop, Talk, Interact, Custom }

    /// <summary>Smart object interaction slot definition.</summary>
    public sealed class SmartObjectSlot
    {
        public string SlotName { get; set; } = string.Empty;
        public SmartObjectInteractionType InteractionType { get; set; }
        public string? RequiredTag { get; set; } // E.g., "Key", "Weapon"
        public float InteractionDuration { get; set; } // 0 = instant
        public int MaxUses { get; set; } = -1; // -1 = unlimited
        public int CurrentUses { get; set; }
        public bool IsAvailable => MaxUses < 0 || CurrentUses < MaxUses;
        public bool RequiresApproach { get; set; } = true;
        public float ApproachDistance { get; set; } = 2f;
        public Dictionary<string, object?> Parameters { get; set; } = new();
    }

    /// <summary>Smart object definition: an interactable game world object with multiple interaction slots.</summary>
    public sealed class SmartObjectDefinition
    {
        public string ObjectId { get; set; } = Guid.NewGuid().ToString();
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<string> Tags { get; set; } = new(); // E.g., "Door", "Container", "NPC"
        public Dictionary<string, SmartObjectSlot> Slots { get; set; } = new();
        public Vector3 Position { get; set; }
        public float InteractionRadius { get; set; } = 3f;
        public bool IsEnabled { get; set; } = true;
        public string? AssociatedAssetPath { get; set; }

        public SmartObjectSlot? FindSlot(string slotName) => Slots.TryGetValue(slotName, out var s) ? s : null;
        public List<SmartObjectSlot> GetAvailableSlots() => Slots.Values.Where(s => s.IsAvailable).ToList();
        public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        public void AddSlot(SmartObjectSlot slot) => Slots[slot.SlotName] = slot;
        public bool RemoveSlot(string slotName) => Slots.Remove(slotName);
    }

    /// <summary>Smart object query parameters for finding matching objects.</summary>
    public sealed class SmartObjectQuery
    {
        public List<string>? RequiredTags { get; set; }
        public SmartObjectInteractionType? InteractionType { get; set; }
        public float? MaxDistance { get; set; }
        public Vector3 QueryOrigin { get; set; }
        public bool EnabledOnly { get; set; } = true;
    }

    /// <summary>Smart object system: manages and queries interactable objects in the game world.</summary>
    public sealed class SmartObjectSystem
    {
        private readonly Dictionary<string, SmartObjectDefinition> _objects = new(StringComparer.OrdinalIgnoreCase);
        public event Action<string, string, int>? OnInteraction; // objectId, slotName, entityId
        public event Action<string>? OnObjectAdded;
        public event Action<string>? OnObjectRemoved;

        public int Count => _objects.Count;

        public void Register(SmartObjectDefinition obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            _objects[obj.ObjectId] = obj;
            OnObjectAdded?.Invoke(obj.ObjectId);
        }

        public bool Unregister(string objectId) { OnObjectRemoved?.Invoke(objectId); return _objects.Remove(objectId); }
        public SmartObjectDefinition? Find(string objectId) => _objects.TryGetValue(objectId, out var o) ? o : null;
        public bool Contains(string objectId) => _objects.ContainsKey(objectId);
        public IEnumerable<SmartObjectDefinition> GetAll() => _objects.Values;
        public void Clear() { _objects.Clear(); }

        /// <summary>Query smart objects matching criteria.</summary>
        public List<SmartObjectDefinition> Query(SmartObjectQuery query)
        {
            var results = _objects.Values.AsEnumerable();
            if (query.RequiredTags != null && query.RequiredTags.Count > 0)
                results = results.Where(o => query.RequiredTags.All(t => o.HasTag(t)));
            if (query.InteractionType.HasValue)
                results = results.Where(o => o.Slots.Values.Any(s => s.InteractionType == query.InteractionType.Value && s.IsAvailable));
            if (query.MaxDistance.HasValue)
                results = results.Where(o => Vector3.Distance(o.Position, query.QueryOrigin) <= query.MaxDistance.Value + o.InteractionRadius);
            if (query.EnabledOnly)
                results = results.Where(o => o.IsEnabled);
            return results.ToList();
        }

        /// <summary>Find the nearest smart object to a position.</summary>
        public SmartObjectDefinition? FindNearest(Vector3 position, float maxDistance = float.MaxValue, List<string>? requiredTags = null)
        {
            SmartObjectDefinition? nearest = null; float nearestDist = maxDistance;
            foreach (var obj in _objects.Values)
            {
                if (!obj.IsEnabled) continue;
                if (requiredTags != null && !requiredTags.All(t => obj.HasTag(t))) continue;
                float dist = Vector3.Distance(obj.Position, position) - obj.InteractionRadius;
                if (dist < nearestDist) { nearestDist = dist; nearest = obj; }
            }
            return nearest;
        }

        /// <summary>Try to interact with a smart object's slot.</summary>
        public bool TryInteract(string objectId, string slotName, int entityId, Vector3 entityPosition)
        {
            var obj = Find(objectId); if (obj == null || !obj.IsEnabled) return false;
            var slot = obj.FindSlot(slotName); if (slot == null || !slot.IsAvailable) return false;
            if (slot.RequiresApproach && Vector3.Distance(obj.Position, entityPosition) > slot.ApproachDistance) return false;
            slot.CurrentUses++;
            OnInteraction?.Invoke(objectId, slotName, entityId);
            return true;
        }

        /// <summary>Reset all slot use counts (for level reset, etc.).</summary>
        public void ResetAllUses() { foreach (var obj in _objects.Values) foreach (var slot in obj.Slots.Values) slot.CurrentUses = 0; }
    }
}
