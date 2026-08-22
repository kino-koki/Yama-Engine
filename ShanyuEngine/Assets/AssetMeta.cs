using System.Text.Json;

namespace ShanyuEngine.Assets
{
    public sealed class AssetMeta
    {
        public string Guid { get; set; } = string.Empty;
        public string AssetPath { get; set; } = string.Empty;
        public string Type { get; set; } = AssetType.Unknown.ToString();
        public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
        public string SourceHash { get; set; } = string.Empty;
        public Dictionary<string, string> ImportSettings { get; set; } = new();
        public List<string> Dependencies { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static AssetMeta FromMetadata(AssetMetadata metadata)
        {
            return new AssetMeta
            {
                Guid = metadata.Guid.ToString("N"),
                AssetPath = metadata.AssetPath,
                Type = metadata.Type.ToString(),
                ImportedAtUtc = metadata.ImportedAt.ToUniversalTime(),
                SourceHash = metadata.Hash,
                ImportSettings = new Dictionary<string, string>
                {
                    ["scale"] = "1",
                    ["readWrite"] = "true",
                    ["generateColliders"] = "false"
                }
            };
        }

        public static AssetMeta Read(string path)
        {
            return JsonSerializer.Deserialize<AssetMeta>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidOperationException($"Invalid asset meta: {path}");
        }

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }
}
