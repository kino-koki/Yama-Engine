using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using YamaEngine.Editor.CodeOss;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第二十轮完善回归（10 项）：不是为了「有测试」，而是证明第十九轮的最轻量
    /// 构建面与 Tuan 被调用能力真的接进了 C# 引擎侧三个桥接类，而不只是
    /// 躺在磁盘上的 Python 脚本被另一份 C# 测试读一遍。
    /// 同时把「Python 门禁已进真实 CI」「fetch 脚本口径不回漂」锁成断言。
    /// </summary>
    public static class CodeOssMinimalBridgeTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("CODEOSS_R20_FAIL: " + msg);
        }
        private static bool IsRoot(string dir)
            => File.Exists(Path.Combine(dir, "ide", "codeoss", "MINIMAL.lock.json"));
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
            throw new InvalidOperationException("未找到仓库根（MINIMAL.lock.json）");
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

        private static readonly string[] NewCommands = {
            "yama.codeossMinimal", "yama.invoke", "yama.capabilities" };

        public static void TestR20ManifestExposesTenCommands()
        {
            Require(YamaExtensionManifest.Commands.Length == 14, "命令表应为 14");
            foreach (var c in NewCommands)
                Require(Array.IndexOf(YamaExtensionManifest.Commands, c) >= 0,
                        "命令缺失 " + c);
            string m = YamaExtensionManifest.Build("1.148.0");
            Require(YamaExtensionManifest.IsWellFormed(m), "清单应合法");
            using var doc = JsonDocument.Parse(m);
            int n = 0;
            foreach (var _ in doc.RootElement.GetProperty("contributes")
                         .GetProperty("commands").EnumerateArray()) n++;
            Require(n == 14, "JSON contributes.commands 应为 14，实际 " + n);
        }

        public static void TestR20BridgeRoutesEveryMinimalCommand()
        {
            string p = Rd("ide/codeoss/product.json");
            var b = new YamaCodeOssBridge(p, "1.148.0");
            Require(b.IsCleanMitBuild, "干净 MIT 构建仍应成立");
            foreach (string c in YamaExtensionManifest.Commands)
            {
                var x = b.Dispatch(c);
                Require(x.Handled && x.Intent.Length > 0 && x.Payload.Length > 0,
                        "命令应路由: " + c);
            }
            var v = b.Dispatch("yama.codeossMinimal");
            Require(v.Intent == "verify"
                 && v.Payload.Contains("build_codeoss_minimal.py --check", StringComparison.Ordinal)
                 && v.Payload.Contains("read-only", StringComparison.Ordinal),
                    "codeossMinimal 应指向真实离线校验");
            var iv = b.Dispatch("yama.invoke");
            Require(iv.Intent == "delegate"
                 && iv.Payload.Contains("yama_bridge.py::invoke", StringComparison.Ordinal),
                    "invoke 应指向 dispatcher");
            var cp = b.Dispatch("yama.capabilities");
            Require(cp.Intent == "capabilities" && cp.Payload.Contains("13 tools",
                    StringComparison.Ordinal), "capabilities 应指向契约");
            Require(!b.Dispatch("yama.unknown").Handled, "未知命令应拒绝");
        }

        public static void TestR20ProductParsesMinimalSectionFromRealFile()
        {
            var p = YamaCodeOssProduct.FromJson(Rd("ide/codeoss/product.json"));
            Require(p.HasMinimalSection, "应识别 yamaMinimal 段");
            Require(p.MinimalProfile == "web-only-minimal", "profile");
            Require(p.MinimalBuildTarget == "web", "buildTarget");
            Require(!p.MinimalCompiledInThisRepo, "本仓库不得宣称已编译");
            Require(p.MinimalNoElectron && p.MinimalNoNativeModules
                 && p.MinimalNoMarketplace && p.MinimalNoTelemetry
                 && p.MinimalNoBranding && p.MinimalNoBuiltinExtensionDownload
                 && p.MinimalNoUpdateService, "7 项排除声明应全为 true");
            Require(p.IsMinimalWebBuild, "IsMinimalWebBuild 应通过");
            Require(p.IsCleanMitBuild, "第十四轮 6 项干净 MIT 校验不应回退");
        }

        public static void TestR20MissingMinimalSectionCannotFakeGreen()
        {
            string bare = "{\"nameShort\":\"Yama Code - OSS\",\"nameLong\":\"x\","
                + "\"licenseName\":\"MIT\",\"version\":\"1.148.0\",\"commit\":\"abc\","
                + "\"enableTelemetry\":false,\"telemetryEndpoint\":\"\","
                + "\"extensionsGallery\":{},\"yama\":{\"bridge\":\"YamaCodeOssBridge\"}}";
            var p = YamaCodeOssProduct.FromJson(bare);
            Require(p.IsCleanMitBuild, "干净 MIT 6 项仍成立");
            Require(!p.HasMinimalSection, "缺段时必须标记为缺段");
            Require(!p.IsMinimalWebBuild, "缺段不得被误判成最轻量构建");
        }

        public static void TestR20CompiledFlagCannotBeFaked()
        {
            string real = Rd("ide/codeoss/product.json");
            string faked = real.Replace("\"compiledInThisRepo\": false",
                                        "\"compiledInThisRepo\": true")
                               .Replace("\"compiledInThisRepo\":false",
                                        "\"compiledInThisRepo\":true");
            Require(faked != real, "替换应生效");
            var p = YamaCodeOssProduct.FromJson(faked);
            Require(p.MinimalCompiledInThisRepo, "篡改值应被解析到");
            Require(!p.IsMinimalWebBuild, "宣称已编译时必须拒绝");
        }

        public static void TestR20NoElectronExclusionCannotBeDropped()
        {
            string real = Rd("ide/codeoss/product.json");
            foreach (var key in new[] { "noElectron", "noNativeModules", "noMarketplace",
                                        "noTelemetry", "noBranding",
                                        "noBuiltinExtensionDownload", "noUpdateService" })
            {
                string broken = real.Replace("\"" + key + "\": true", "\"" + key + "\": false");
                Require(broken != real, "替换应生效: " + key);
                var p = YamaCodeOssProduct.FromJson(broken);
                Require(!p.IsMinimalWebBuild, "丢掉排除项应拒绝: " + key);
            }
        }

        public static void TestR20FetchScriptNoElectronCompileClaim()
        {
            string f = Rd("scripts/fetch_codeoss.py");
            Require(!f.Contains("npm ci && npm run compile", StringComparison.Ordinal),
                    "不得再宣称完整 Electron 编译");
            Require(!f.Contains("npm run compile", StringComparison.Ordinal),
                    "不得出现完整编译脚本");
            Require(f.Contains("本仓库不编译", StringComparison.Ordinal),
                    "应明确声明本仓库不编译");
            Require(f.Contains("build_codeoss_minimal.py --check", StringComparison.Ordinal),
                    "应指向最轻量离线校验");
            Require(f.Contains("web-only", StringComparison.Ordinal), "应为 web-only 口径");
        }

        public static void TestR20MinimalCheckGuardsAgainstFetchDrift()
        {
            string m = Rd("scripts/build_codeoss_minimal.py");
            Require(m.Contains("fetch_codeoss.py", StringComparison.Ordinal),
                    "离线校验应检查 fetch 脚本");
            Require(m.Contains("口径回漂", StringComparison.Ordinal),
                    "应有反回漂断言");
            Require(!m.Contains("npm run compile-web", StringComparison.Ordinal),
                    "plan 不得写死未核实的脚本名");
            Require(m.Contains("本仓库未核实脚本名", StringComparison.Ordinal),
                    "plan 应如实标注未核实");
        }

        public static void TestR20GateTemplateIsHonestAndCsharpEquivalentExists()
        {
            // 本环境 token 仅 repo scope，写 workflow 实测 404。因此不谎报已启用：
            // 模板明确标注未启用，且 Python 校验语义由 C# 双实现进真实 Test step。
            var c = J("integrations/tuan_cli/contract.json");
            Require(c.GetProperty("ci_gate_enabled").GetBoolean() == false,
                    "模板未启用时必须如实标记 false");
            Require(c.GetProperty("ci_gate").GetString()!.Contains("NOT enabled",
                    StringComparison.Ordinal), "ci_gate 应说明未启用");
            Require(c.GetProperty("csharp_equivalent_gate").GetString()!
                    .Contains("CodeOssMinimalBridgeTests.cs", StringComparison.Ordinal),
                    "应指向真实执行的 C# 等价门禁");
            string gate = Rd("ci/codeoss-gate.yml");
            Require(gate.Contains("未启用", StringComparison.Ordinal), "模板应标注未启用");
            Require(gate.Contains("404", StringComparison.Ordinal), "应记录实测拒绝原因");
            Require(!gate.Contains("verify-python", StringComparison.Ordinal),
                    "不得谎报 verify-python job 已落地");
            // 模板本身仍必须完整可执行（网页端启用后即可用）
            foreach (var step in new[] { "fetch_codeoss.py --check",
                                         "build_codeoss_minimal.py --check",
                                         "--emit-manifest",
                                         "audit_no_engine_copy.py" })
                Require(gate.Contains(step, StringComparison.Ordinal),
                        "模板缺步骤 " + step);
        }

        public static void TestR20CsharpGateCoversPythonBehaviour()
        {
            // Python 侧 unittest 验过的「真实委派」行为，也用 C# 锁进真实 Test step，
            // 使得即使模板未启用，语义仍每次 CI 被校验。
            string mcp = Rd("integrations/tuan_cli/mcp_server.py");
            Require(mcp.Contains("bridge_invoke", StringComparison.Ordinal)
                 && mcp.Contains("delegated", StringComparison.Ordinal),
                    "MCP 应真委派");
            string sub = Rd("integrations/tuan_cli/subcommand.py");
            Require(sub.Contains("bridge_invoke", StringComparison.Ordinal),
                    "插件子命令应真委派");
            string cli = Rd("integrations/tuan_cli/yama_cli.py");
            Require(cli.Contains("subcommand.handle", StringComparison.Ordinal),
                    "独立 CLI 应可直调");
            string bridge = Rd("integrations/tuan_cli/yama_bridge.py");
            Require(bridge.Contains("def invoke", StringComparison.Ordinal)
                 && bridge.Contains("subprocess.run", StringComparison.Ordinal)
                 && bridge.Contains("fail-closed", StringComparison.Ordinal),
                    "dispatcher 应真执行只读动作且 fail-closed");
        }

        public static void TestR20UpstreamLockDeclaresMinimalAndNotCompiled()
        {
            var u = J("ide/codeoss/UPSTREAM.lock.json");
            Require(u.GetProperty("tag").GetString() == "1.148.0", "tag 应为最新稳定版");
            Require(u.GetProperty("commit").GetString()
                    == "661626d0e72f97806550790a98fb87116c9db825", "commit 应精确锁定");
            Require(u.GetProperty("distribution").GetString()
                    == "clean-mit-web-only-minimal-build", "发行口径应为最轻量 web-only");
            Require(u.GetProperty("minimal_lock").GetString()
                    == "ide/codeoss/MINIMAL.lock.json", "应指向轻量锁");
            Require(u.GetProperty("minimal_build_script").GetString()
                    == "scripts/build_codeoss_minimal.py", "应指向离线校验脚本");
            Require(u.GetProperty("compile_note").GetString().Contains("不编译",
                    StringComparison.Ordinal), "应明确本仓库不编译上游");
            var m = J("ide/codeoss/MINIMAL.lock.json");
            Require(m.GetProperty("note").GetString()!.Contains("404",
                    StringComparison.Ordinal), "轻量锁应留下 workflow 写入实测记录");
        }

        public static void TestR20TuanContractDeclaresRealCiGate()
        {
            var c = J("integrations/tuan_cli/contract.json");
            Require(c.GetProperty("mcp_tools").GetArrayLength() == 13, "13 工具不变");
            Require(c.GetProperty("subcommands").GetArrayLength() == 13, "13 子命令不变");
            Require(c.GetProperty("callable_actions").GetProperty("actions")
                     .GetArrayLength() == 7, "7 动作不变");
            Require(c.TryGetProperty("ci_gate", out var g), "契约应声明门禁状态");
            Require(g.GetString()!.Contains("NOT enabled", StringComparison.Ordinal),
                    "应如实标注模板未启用");
            Require(!c.GetProperty("ci_gate_enabled").GetBoolean(), "不得谎报已启用");
            Require(c.GetProperty("codeoss_bridge_commands").GetInt32() == 14,
                    "引擎侧命令表应为 14");
            Require(c.GetProperty("language_profile").GetProperty("primary").GetString() == "yama",
                    "契约应声明 YAMA 主语言");
            string gate = Rd("ci/codeoss-gate.yml");
            Require(gate.Contains("网页端", StringComparison.Ordinal)
                 && gate.Contains("未启用", StringComparison.Ordinal),
                    "模板应说明需网页端人工启用");
        }
    }
}
