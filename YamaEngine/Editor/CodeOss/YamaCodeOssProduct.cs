using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using YamaEngine.Reality;
namespace YamaEngine.Editor.CodeOss
{
    /// <summary>
    /// 自研 Code - OSS 干净 MIT 构建配置解析与校验。
    /// 只解析本仓库 ide/codeoss/product.json 的字段，不引用任何 VS Code 源码。
    /// </summary>
    public sealed class YamaCodeOssProduct
    {
        public string NameShort { get; private set; } = string.Empty;
        public string NameLong { get; private set; } = string.Empty;
        public string LicenseName { get; private set; } = string.Empty;
        public string Version { get; private set; } = string.Empty;
        public string Commit { get; private set; } = string.Empty;
        public bool TelemetryEnabled { get; private set; }
        public string TelemetryEndpoint { get; private set; } = string.Empty;
        public bool HasExtensionsGallery { get; private set; }
        public string Bridge { get; private set; } = string.Empty;

        // 第十九轮 product.json 的 yamaMinimal 段（第二十轮真正解析进引擎侧）。
        // HasMinimalSection 防止「字段缺失」被误判成「已声明未编译」的假绿。
        public bool HasMinimalSection { get; private set; }
        public string MinimalProfile { get; private set; } = string.Empty;
        public string MinimalBuildTarget { get; private set; } = string.Empty;
        public bool MinimalCompiledInThisRepo { get; private set; }
        public bool MinimalNoElectron { get; private set; }
        public bool MinimalNoNativeModules { get; private set; }
        public bool MinimalNoMarketplace { get; private set; }
        public bool MinimalNoTelemetry { get; private set; }
        public bool MinimalNoBranding { get; private set; }
        public bool MinimalNoBuiltinExtensionDownload { get; private set; }
        public bool MinimalNoUpdateService { get; private set; }

        public static YamaCodeOssProduct FromJson(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var p = new YamaCodeOssProduct();
            p.NameShort = Get(r, "nameShort");
            p.NameLong = Get(r, "nameLong");
            p.LicenseName = Get(r, "licenseName");
            p.Version = Get(r, "version");
            p.Commit = Get(r, "commit");
            p.TelemetryEndpoint = Get(r, "telemetryEndpoint");
            p.Bridge = Get(r, "yama", "bridge");
            if (r.TryGetProperty("enableTelemetry", out var t)
                && t.ValueKind == JsonValueKind.True) p.TelemetryEnabled = true;
            if (r.TryGetProperty("extensionsGallery", out var g)
                && g.ValueKind == JsonValueKind.Object)
            {
                var en = g.EnumerateObject();
                p.HasExtensionsGallery = en.MoveNext();
            }
            if (r.TryGetProperty("yamaMinimal", out var ym)
                && ym.ValueKind == JsonValueKind.Object)
            {
                p.HasMinimalSection = true;
                p.MinimalProfile = Get(ym, "profile");
                p.MinimalBuildTarget = Get(ym, "buildTarget");
                p.MinimalCompiledInThisRepo = Bool(ym, "compiledInThisRepo");
                p.MinimalNoElectron = Bool(ym, "noElectron");
                p.MinimalNoNativeModules = Bool(ym, "noNativeModules");
                p.MinimalNoMarketplace = Bool(ym, "noMarketplace");
                p.MinimalNoTelemetry = Bool(ym, "noTelemetry");
                p.MinimalNoBranding = Bool(ym, "noBranding");
                p.MinimalNoBuiltinExtensionDownload = Bool(ym, "noBuiltinExtensionDownload");
                p.MinimalNoUpdateService = Bool(ym, "noUpdateService");
            }
            return p;
        }

        private static string Get(JsonElement e, string key)
        {
            return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? string.Empty) : string.Empty;
        }
        private static string Get(JsonElement e, string parent, string key)
        {
            return e.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object
                ? Get(p, key) : string.Empty;
        }
        private static bool Bool(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>干净 MIT 构建的 6 项硬校验（与 scripts/fetch_codeoss.py 同口径）。</summary>
        public bool IsCleanMitBuild =>
            LicenseName == "MIT"
            && !NameShort.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
            && NameShort.Contains("Code - OSS", StringComparison.Ordinal)
            && !TelemetryEnabled
            && TelemetryEndpoint.Length == 0
            && !HasExtensionsGallery;

        /// <summary>
        /// 最轻量 web-only 构建面的 9 项硬校验。第十四轮的 6 项干净 MIT 校验
        /// 保持独立、不回退；这里只回答「是否真的声明了最轻量且未编译」。
        /// </summary>
        public bool IsMinimalWebBuild =>
            HasMinimalSection
            && MinimalProfile == "web-only-minimal"
            && MinimalBuildTarget == "web"
            && !MinimalCompiledInThisRepo
            && MinimalNoElectron
            && MinimalNoNativeModules
            && MinimalNoMarketplace
            && MinimalNoTelemetry
            && MinimalNoBranding
            && MinimalNoBuiltinExtensionDownload
            && MinimalNoUpdateService;
    }
}
