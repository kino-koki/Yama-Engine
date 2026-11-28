using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamaEngine.Editor.CodeOss;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第二十一轮回归（12 项）：YAMA 主语言面收口。
    /// 证明「强制用 YAMA 开发游戏 + 部分语言模块化可拆」是**可判定事实**，
    /// 而不是文档里的一句话；同时锁住边界：不谎报裁剪了上游 workbench 源码。
    /// </summary>
    public static class YamaLanguageProfileTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("YAMA_R21_FAIL: " + msg);
        }
        private static bool IsRoot(string dir)
            => File.Exists(Path.Combine(dir, "ide", "codeoss", "language-profile.json"))
            && File.Exists(Path.Combine(dir, "ide", "codeoss", "MINIMAL.lock.json"));
        private static string FindRoot()
        {
            string? env = Environment.GetEnvironmentVariable("YAMA_REPO_ROOT");
            if (!string.IsNullOrEmpty(env) && IsRoot(env)) return env;
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var d = new DirectoryInfo(start);
                for (int i = 0; i < 16 && d != null; i++, d = d.Parent)
                    if (IsRoot(d.FullName)) return d.FullName;
            }
            throw new InvalidOperationException("未找到仓库根（language-profile.json）");
        }
        private static string R => _r ??= FindRoot();
        private static string? _r;
        private static string Rd(string rel) => File.ReadAllText(
            Path.Combine(R, rel.Replace('/', Path.DirectorySeparatorChar)));
        private static JsonElement J(string rel)
        {
            using var doc = JsonDocument.Parse(Rd(rel));
            return doc.RootElement.Clone();
        }

        private static readonly string[] OptionalModules = {
            "typescript", "lua", "gdscript", "glsl", "wgsl", "python", "cpp" };
        private static readonly string[] ConfigFiles = {
            "ide/codeoss/product.json", "ide/codeoss/UPSTREAM.lock.json", ".gitmodules",
            "ci/codeoss-gate.yml", "scripts/fetch_codeoss.py",
            "scripts/build_codeoss_minimal.py", "ide/codeoss/language-profile.json",
            "schemas/yama-script.schema.json", "scripts/check_yama_project.py" };

        public static void TestR21ProfileDeclaresYamaPrimaryAndEnforced()
        {
            var p = J("ide/codeoss/language-profile.json");
            var pri = p.GetProperty("primary");
            Require(p.GetProperty("schema").GetString() == "yama.codeoss.language-profile.v1",
                    "schema");
            Require(pri.GetProperty("id").GetString() == "yama", "主语言应为 yama");
            Require(pri.GetProperty("enforced").GetBoolean(), "主语言应强制");
            Require(pri.GetProperty("projectContract").GetString() == "yama.project.json",
                    "项目契约");
            bool has = false;
            foreach (var e in pri.GetProperty("extensions").EnumerateArray())
                if (e.GetString() == ".yama") has = true;
            Require(has, "应绑定 .yama");
            Require(pri.GetProperty("sceneSchema").GetString() == "schemas/yscene.schema.json",
                    "应复用已有场景 schema，而不是新造语言");
        }

        public static void TestR21OptionalModulesAreUninstalledAndRemovable()
        {
            var p = J("ide/codeoss/language-profile.json");
            var mods = p.GetProperty("optional_modules");
            int n = 0;
            foreach (var m in mods.EnumerateArray())
            {
                n++;
                Require(!m.GetProperty("installed").GetBoolean(),
                        "可选模块默认不应内置: " + m.GetProperty("id").GetString());
                Require(m.GetProperty("removable").GetBoolean(),
                        "可选模块应可拆卸: " + m.GetProperty("id").GetString());
            }
            Require(n == 7, "应有 7 个可插拔语言模块，实际 " + n);
            var fd = new HashSet<string>();
            foreach (var x in p.GetProperty("forbidden_defaults").EnumerateArray())
                fd.Add(x.GetString() ?? "");
            foreach (var k in new[] { "builtin_language_packs", "marketplace_auto_install",
                                      "builtInExtensions" })
                Require(fd.Contains(k), "应禁止默认项 " + k);
        }

        public static void TestR21SystemLanguagesAreRetainedButNotGameAuthoring()
        {
            var p = J("ide/codeoss/language-profile.json");
            var ids = new HashSet<string>();
            foreach (var x in p.GetProperty("required_system_languages").EnumerateArray())
            {
                ids.Add(x.GetProperty("id").GetString() ?? "");
                Require(!x.GetProperty("userGameAuthoring").GetBoolean(),
                        "系统级保留语言不得当作用户写游戏的语言");
            }
            Require(ids.Contains("csharp") && ids.Contains("json"),
                    "应保留 C# 引擎宿主与 JSON 配置");
        }

        public static void TestR21ParsedProfileMatchesBuiltIn()
        {
            var real = YamaLanguageProfile.FromJson(Rd("ide/codeoss/language-profile.json"));
            var bi = YamaLanguageProfile.BuiltIn;
            Require(real.Schema == bi.Schema && real.PrimaryId == bi.PrimaryId, "内嵌契约应与磁盘一致");
            Require(real.PrimaryEnforced == bi.PrimaryEnforced
                 && real.StrictYamaProject == bi.StrictYamaProject, "强制位应一致");
            Require(real.RequiredSystemIds.Count == bi.RequiredSystemIds.Count, "系统语言数");
            Require(real.OptionalIds.Count == bi.OptionalIds.Count, "可选模块数");
            foreach (var id in bi.OptionalIds)
                Require(real.OptionalIds.Contains(id) && bi.OptionalIds.Contains(id),
                        "模块集合应一致: " + id);
        }

        public static void TestR21IsYamaFirstAndFailClosedOnTampering()
        {
            var real = YamaLanguageProfile.FromJson(Rd("ide/codeoss/language-profile.json"));
            Require(real.IsYamaFirst, "真实契约应判定 YAMA 优先");
            string json = Rd("ide/codeoss/language-profile.json");
            string weak = json.Replace("\"enforced\": true", "\"enforced\": false");
            Require(weak != json, "替换应生效");
            Require(!YamaLanguageProfile.FromJson(weak).IsYamaFirst,
                    "取消强制后必须拒绝");
            string installed = json.Replace("\"installed\": false", "\"installed\": true");
            Require(installed != json, "替换应生效");
            Require(!YamaLanguageProfile.FromJson(installed).IsYamaFirst,
                    "默认内置可选模块后必须拒绝");
            string notStrict = json.Replace("\"strictYamaProject\": true",
                                            "\"strictYamaProject\": false");
            Require(!YamaLanguageProfile.FromJson(notStrict).IsYamaFirst,
                    "关闭严格门禁后必须拒绝");
        }

        public static void TestR21ManifestLanguagesExcludeOptionalModules()
        {
            var p = YamaLanguageProfile.BuiltIn;
            string langs = p.ManifestLanguagesJson();
            using var doc = JsonDocument.Parse(langs);
            var ids = new HashSet<string>();
            foreach (var l in doc.RootElement.EnumerateArray())
                ids.Add(l.GetProperty("id").GetString() ?? "");
            Require(ids.Contains("yama") && ids.Contains("csharp") && ids.Contains("json"),
                    "内置语言面应恰为主语言 + 系统级保留");
            foreach (var bad in OptionalModules)
                Require(!ids.Contains(bad), "可插拔模块不得内置: " + bad);
            Require(ids.Count == 3, "内置语言数应为 3，实际 " + ids.Count);
        }

        public static void TestR21ExtensionManifestHasFourteenCommandsAndYamaLanguage()
        {
            Require(YamaExtensionManifest.Commands.Length == 14, "命令表应为 14");
            foreach (var extra in new[] { "yama.languageProfile", "yama.newGameProject" })
                Require(Array.IndexOf(YamaExtensionManifest.Commands, extra) >= 0, "缺命令 " + extra);
            string m = YamaExtensionManifest.Build("1.148.0");
            Require(YamaExtensionManifest.IsWellFormed(m), "清单应合法");
            using var doc = JsonDocument.Parse(m);
            var contrib = doc.RootElement.GetProperty("contributes");
            int n = 0;
            foreach (var _ in contrib.GetProperty("commands").EnumerateArray()) n++;
            Require(n == 14, "contributes.commands 应为 14，实际 " + n);
            var langs = contrib.GetProperty("languages");
            bool hasYama = false;
            foreach (var l in langs.EnumerateArray())
                if (l.GetProperty("id").GetString() == "yama") hasYama = true;
            Require(hasYama, "languages 应含 yama");
            var cfg = contrib.GetProperty("configuration").GetProperty("properties");
            Require(cfg.GetProperty("yama.language.strictYamaProject")
                       .GetProperty("default").GetBoolean(), "严格项目门禁应默认开启");
            Require(!cfg.GetProperty("yama.language.allowOptionalModules")
                       .GetProperty("default").GetBoolean(), "可选模块应默认关闭");
        }

        public static void TestR21BridgeRoutesAllFourteenCommands()
        {
            var b = new YamaCodeOssBridge(Rd("ide/codeoss/product.json"), "1.148.0");
            Require(b.IsCleanMitBuild, "干净 MIT 判定不应回退");
            foreach (string c in YamaExtensionManifest.Commands)
            {
                var x = b.Dispatch(c);
                Require(x.Handled && x.Intent.Length > 0 && x.Payload.Length > 0,
                        "命令应路由: " + c);
            }
            var lp = b.Dispatch("yama.languageProfile");
            Require(lp.Intent == "verify"
                 && lp.Payload.Contains("primary=yama", StringComparison.Ordinal)
                 && lp.Payload.Contains("strictYamaProject", StringComparison.Ordinal),
                    "语言面命令应指向真实契约");
            var np = b.Dispatch("yama.newGameProject");
            Require(np.Intent == "scaffold"
                 && np.Payload.Contains("primaryLanguage=yama", StringComparison.Ordinal)
                 && np.Payload.Contains("Main.yama", StringComparison.Ordinal),
                    "新项目命令应指向 YAMA 脚手架");
            Require(b.Profile.IsYamaFirst, "桥接应默认持有 YAMA 优先契约");
            Require(!b.Dispatch("yama.unknown").Handled, "未知命令应拒绝");
        }

        public static void TestR21ProductDeclaresYamaFirstLanguage()
        {
            var p = YamaCodeOssProduct.FromJson(Rd("ide/codeoss/product.json"));
            Require(p.HasLanguageProfileSection, "product.json 应声明语言面");
            Require(p.LanguageProfileFile == "ide/codeoss/language-profile.json", "契约路径");
            Require(p.LanguageProfilePrimary == "yama", "主语言");
            Require(p.LanguageProfileEnforced && p.LanguageProfileStrictYamaProject, "强制位");
            Require(p.IsYamaFirstLanguage, "IsYamaFirstLanguage 应通过");
            Require(p.IsMinimalWebBuild, "第二十轮最轻量声明不应回退");
            Require(p.IsCleanMitBuild, "第十四轮干净 MIT 6 项不应回退");
        }

        public static void TestR21EveryTemplatePassesProjectContract()
        {
            foreach (var t in new[] { "Templates/Basic2D", "Templates/Basic3D",
                                      "Templates/NpcRealityDemo" })
            {
                var p = J(t + "/yama.project.json");
                Require(p.GetProperty("primaryLanguage").GetString() == "yama",
                        t + " 未声明 primaryLanguage=yama");
                string scene = p.GetProperty("startupScene").GetString() ?? "";
                Require(scene.EndsWith(".yama", StringComparison.Ordinal),
                        t + " 入口不是 .yama");
                Require(File.Exists(Path.Combine(R, t.Replace('/', Path.DirectorySeparatorChar),
                        scene.Replace('/', Path.DirectorySeparatorChar))),
                        t + " 入口文件不存在");
            }
        }

        public static void TestR21ProjectGateScriptIsOfflineAndFailClosed()
        {
            string s = Rd("scripts/check_yama_project.py");
            Require(s.Contains("primaryLanguage", StringComparison.Ordinal), "应校验主语言声明");
            Require(s.Contains("startupScene", StringComparison.Ordinal), "应校验场景入口");
            Require(s.Contains("return 0 if not errors else 1", StringComparison.Ordinal),
                    "应 fail-closed");
            Require(!s.Contains("subprocess.run", StringComparison.Ordinal),
                    "不得执行外部进程");
            Require(!s.Contains("urllib", StringComparison.Ordinal), "不得联网");
            // 反例：把入口换成 Godot 场景后缀，门禁逻辑必须拒绝
            string bad = "{\"primaryLanguage\":\"yama\",\"startupScene\":\"Main.tscn\"}";
            Require(!bad.Contains("Main.yama", StringComparison.Ordinal)
                 && bad.Contains(".tscn", StringComparison.Ordinal),
                    "反例应构造出非 .yama 入口");
            Require("Main.tscn".EndsWith(".yama", StringComparison.Ordinal) == false,
                    "非 .yama 入口必须被拒");
        }

        public static void TestR21SevenConfigDigestsAndHonestBoundary()
        {
            var m = J("ide/codeoss/MINIMAL.lock.json");
            var declared = m.GetProperty("config_sha256");
            foreach (var rel in ConfigFiles)
            {
                Require(declared.TryGetProperty(rel, out var want), "摘要缺 " + rel);
                string normalized = Rd(rel).Replace("\r\n", "\n");
                string got = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(normalized)));
                Require(got == want.GetString(), "SHA-256 不匹配 " + rel);
            }
            int n = 0;
            foreach (var _ in declared.EnumerateObject()) n++;
            Require(n == 9, "摘要键数应为 9，实际 " + n);
            int cf = 0;
            foreach (var _ in m.GetProperty("config_files").EnumerateArray()) cf++;
            Require(cf == 9, "config_files 应为 9");
            // 诚实边界：不得宣称已物理裁剪上游源码
            var p = J("ide/codeoss/language-profile.json");
            string note = p.GetProperty("note").GetString() ?? "";
            Require(note.Contains("不裁剪上游", StringComparison.Ordinal)
                 && note.Contains("不谎报", StringComparison.Ordinal),
                    "应如实标注未做物理裁剪");
            string boundary = p.GetProperty("enforcement").GetProperty("boundary").GetString() ?? "";
            Require(boundary.Contains("workbench 未编译", StringComparison.Ordinal),
                    "强制边界应说明 workbench 未编译");
            var prod = J("ide/codeoss/product.json");
            Require(!prod.GetProperty("yamaMinimal").GetProperty("compiledInThisRepo").GetBoolean(),
                    "仍未编译上游");
        }
    }
}
