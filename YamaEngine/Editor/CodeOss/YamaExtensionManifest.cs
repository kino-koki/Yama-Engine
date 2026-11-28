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
            "yama.rollback", "yama.sotaStatus", "yama.coupledMass"
        };

        public static string Build(string version)
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
            sb.Append("],\"languages\":[{\"id\":\"yama-scene\",\"extensions\":[\".yscene\",\".yprefab\"]}],")
              .Append("\"debuggers\":[{\"type\":\"yama\",\"label\":\"Yama Debugger\"}],")
              .Append("\"configuration\":{\"title\":\"Yama Engine\",\"properties\":{")
              .Append("\"yama.codeoss.cleanMitBuild\":{\"type\":\"boolean\",\"default\":true},")
              .Append("\"yama.cli.smokeFrames\":{\"type\":\"number\",\"default\":60}}}}")
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
                return n == Commands.Length
                    && r.TryGetProperty("license", out var lic)
                    && lic.GetString() == "MIT";
            }
            catch (JsonException) { return false; }
        }
    }
}
