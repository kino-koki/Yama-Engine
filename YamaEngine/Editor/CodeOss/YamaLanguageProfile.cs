using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
namespace YamaEngine.Editor.CodeOss
{
    /// <summary>
    /// 自研 YAMA 语言面契约：主语言强制 + 系统级保留语言 + 可插拔语言模块。
    /// 不引用任何 VS Code 源码，不裁剪上游二进制；只定义本仓库声明的能力面。
    /// 物理裁剪需带 Node 24 的机器，本环境做不到，不谎报已做。
    /// </summary>
    public sealed class YamaLanguageProfile
    {
        public string Schema { get; private set; } = string.Empty;
        public string PrimaryId { get; private set; } = string.Empty;
        public bool PrimaryEnforced { get; private set; }
        public string[] PrimaryExtensions { get; private set; } = Array.Empty<string>();
        public string ProjectContract { get; private set; } = string.Empty;
        public bool StrictYamaProject { get; private set; }
        public List<string> RequiredSystemIds { get; } = new List<string>();
        public List<string> OptionalIds { get; } = new List<string>();
        public List<string> InstalledOptionalIds { get; } = new List<string>();
        public List<string> ForbiddenDefaults { get; } = new List<string>();

        // 第二十二轮：行为层声明（language-profile.json 的 scriptBinding 段）
        public bool HasScriptBindingSection { get; private set; }
        public string ScriptBindingSchema { get; private set; } = string.Empty;
        public string ScriptBindingAbi { get; private set; } = string.Empty;
        public bool VmImplemented { get; private set; }

        public static YamaLanguageProfile FromJson(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var p = new YamaLanguageProfile();
            p.Schema = Str(r, "schema");
            if (r.TryGetProperty("primary", out var pri)
                && pri.ValueKind == JsonValueKind.Object)
            {
                p.PrimaryId = Str(pri, "id");
                p.PrimaryEnforced = Bool(pri, "enforced");
                p.ProjectContract = Str(pri, "projectContract");
                var exts = new List<string>();
                if (pri.TryGetProperty("extensions", out var ex)
                    && ex.ValueKind == JsonValueKind.Array)
                    foreach (var e in ex.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String) exts.Add(e.GetString() ?? "");
                p.PrimaryExtensions = exts.ToArray();
            }
            if (r.TryGetProperty("required_system_languages", out var req)
                && req.ValueKind == JsonValueKind.Array)
                foreach (var x in req.EnumerateArray())
                    p.RequiredSystemIds.Add(Str(x, "id"));
            if (r.TryGetProperty("optional_modules", out var mods)
                && mods.ValueKind == JsonValueKind.Array)
                foreach (var x in mods.EnumerateArray())
                {
                    string id = Str(x, "id");
                    p.OptionalIds.Add(id);
                    if (Bool(x, "installed")) p.InstalledOptionalIds.Add(id);
                }
            if (r.TryGetProperty("forbidden_defaults", out var fd)
                && fd.ValueKind == JsonValueKind.Array)
                foreach (var x in fd.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) p.ForbiddenDefaults.Add(x.GetString() ?? "");
            if (r.TryGetProperty("enforcement", out var enf)
                && enf.ValueKind == JsonValueKind.Object)
                p.StrictYamaProject = Bool(enf, "strictYamaProject");
            if (r.TryGetProperty("scriptBinding", out var sb)
                && sb.ValueKind == JsonValueKind.Object)
            {
                p.HasScriptBindingSection = true;
                p.ScriptBindingSchema = Str(sb, "schema");
                p.ScriptBindingAbi = Str(sb, "abi");
                p.VmImplemented = Bool(sb, "vmImplemented");
            }
            return p;
        }

        /// <summary>内嵌最小契约：Build() 不读磁盘也可生成扩展清单。</summary>
        public static YamaLanguageProfile BuiltIn
        {
            get
            {
                var p = new YamaLanguageProfile();
                p.Schema = "yama.codeoss.language-profile.v1";
                p.PrimaryId = "yama";
                p.PrimaryEnforced = true;
                p.PrimaryExtensions = new[] { ".yama", ".yscene", ".yprefab" };
                p.ProjectContract = "yama.project.json";
                p.StrictYamaProject = true;
                p.RequiredSystemIds.Add("csharp");
                p.RequiredSystemIds.Add("json");
                foreach (var id in new[] { "typescript", "lua", "gdscript", "glsl",
                                           "wgsl", "python", "cpp" })
                    p.OptionalIds.Add(id);
                foreach (var f in new[] { "builtin_language_packs",
                                          "marketplace_auto_install", "builtInExtensions" })
                    p.ForbiddenDefaults.Add(f);
                p.HasScriptBindingSection = true;
                p.ScriptBindingSchema = "schemas/yama-script.schema.json";
                p.ScriptBindingAbi = "YamaEngine/Scripting/Abi/";
                p.VmImplemented = false;
                return p;
            }
        }

        private static string Str(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? string.Empty) : string.Empty;
        private static bool Bool(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>YAMA 优先：主语言强制 + .yama 绑定 + 严格项目门禁 + 可选模块零内置。</summary>
        public bool IsYamaFirst =>
            PrimaryId == "yama"
            && PrimaryEnforced
            && Array.IndexOf(PrimaryExtensions, ".yama") >= 0
            && ProjectContract == "yama.project.json"
            && StrictYamaProject
            && InstalledOptionalIds.Count == 0
            && OptionalIds.Count > 0;

        /// <summary>行为层声明必须是「声明式绑定 / 未实现 VM」，不得谎报。</summary>
        public bool HasHonestDeclaredScriptBinding =>
            HasScriptBindingSection
            && ScriptBindingSchema == "schemas/yama-script.schema.json"
            && !VmImplemented;

        public bool IsBuiltIn(string id)
            => id == PrimaryId || RequiredSystemIds.Contains(id);

        /// <summary>生成 contributes.languages：只含主语言 + 系统级保留语言。</summary>
        public string ManifestLanguagesJson()
        {
            var sb = new StringBuilder(512);
            sb.Append("[{\"id\":").Append(YamaJsonRpcFraming.Quote(PrimaryId))
              .Append(",\"aliases\":[\"YAMA\",\"Yama Scene\"],\"extensions\":[");
            for (int i = 0; i < PrimaryExtensions.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(YamaJsonRpcFraming.Quote(PrimaryExtensions[i]));
            }
            sb.Append("]}");
            foreach (var id in RequiredSystemIds)
            {
                sb.Append(",{\"id\":").Append(YamaJsonRpcFraming.Quote(id))
                  .Append(",\"extensions\":[")
                  .Append(YamaJsonRpcFraming.Quote(id == "csharp" ? ".cs" : ".json"))
                  .Append("]}");
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
