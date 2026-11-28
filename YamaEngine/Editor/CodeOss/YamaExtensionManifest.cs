using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using YamaEngine.Reality;
namespace YamaEngine.Editor.CodeOss
{
    /// <summary>
    /// 自研 Code - OSS 扩展清单生成（VS Code extension package.json 的 contributes 面）。
    /// 不引用任何扩展 SDK；产出纯 JSON，供 workbench 加载。
    /// </summary>
    public static class YamaExtensionManifest
    {
        public static readonly string[] Commands = new string[]
        {
            "yama.build", "yama.test", "yama.smoke", "yama.snapshot",
            "yama.rollback", "yama.sotaStatus", "yama.coupledMass",
            "yama.codeossMinimal", "yama.invoke", "yama.capabilities",
            "yama.languageProfile", "yama.newGameProject",
            "yama.scriptBinding", "yama.newScript"
        };

        /// <summary>不得内置的语言模块 id（可插拔模块默认不安装）。</summary>
        public static readonly string[] ForbiddenBuiltinLanguages = new string[]
        {
            "typescript", "lua", "gdscript", "glsl", "wgsl", "python", "cpp"
        };

        public static string Build(string version)
            => Build(version, YamaLanguageProfile.BuiltIn);

        public static string Build(string version, YamaLanguageProfile profile)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"name\":\"yama-engine\",\"displayName\":\"Yama Engine\",")
              .Append("\"publisher\":\"kino-koki\",\"version\":")
              .Append(YamaJsonRpcFraming.Quote(version))
              .Append(",\"engines\":{\"vscode\":\"^1.148.0\"},")
              .Append("\"main\":\"./out/extension.js\",\"contributes\":{\"commands\":[");
            for (int i = 0; i < Commands.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"command\":").Append(YamaJsonRpcFraming.Quote(Commands[i]))
                  .Append(",\"title\":").Append(YamaJsonRpcFraming.Quote("Yama: " + Commands[i].Substring(5)))
                  .Append('}');
            }
            sb.Append("],\"languages\":").Append(profile.ManifestLanguagesJson()).Append(',')
              .Append("\"debuggers\":[{\"type\":\"yama\",\"label\":\"Yama Debugger\"}],")
              .Append("\"configuration\":{\"title\":\"Yama Engine\",\"properties\":{")
              .Append("\"yama.codeoss.cleanMitBuild\":{\"type\":\"boolean\",\"default\":true},")
              .Append("\"yama.cli.smokeFrames\":{\"type\":\"number\",\"default\":60},")
              .Append("\"yama.language.strictYamaProject\":{\"type\":\"boolean\",\"default\":true},")
              .Append("\"yama.language.allowOptionalModules\":{\"type\":\"boolean\",\"default\":false}}}}")
              .Append(",\"activationEvents\":[\"onStartupFinished\"],")
              .Append("\"license\":\"MIT\"}");
            return sb.ToString();
        }

        public static bool IsWellFormed(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (!r.TryGetProperty("contributes", out var c)) return false;
                if (!c.TryGetProperty("commands", out var cmds)) return false;
                int n = 0;
                foreach (var _ in cmds.EnumerateArray()) n++;
                if (n != Commands.Length) return false;
                if (!r.TryGetProperty("license", out var lic) || lic.GetString() != "MIT")
                    return false;
                // 语言面收口：必须内置 yama，且不得内置任何可插拔模块。
                if (!c.TryGetProperty("languages", out var langs)) return false;
                bool hasYama = false;
                foreach (var l in langs.EnumerateArray())
                {
                    string id = l.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String
                        ? (v.GetString() ?? "") : "";
                    if (id == "yama") hasYama = true;
                    foreach (var bad in ForbiddenBuiltinLanguages)
                        if (id == bad) return false;
                }
                return hasYama;
            }
            catch (JsonException) { return false; }
        }
    }
}
