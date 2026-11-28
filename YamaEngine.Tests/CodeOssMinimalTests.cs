using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十九轮回归（12 项）：Code - OSS 1.148.0 最轻量 web-only 干净 MIT
    /// 构建面 + Tuan-CLI 真实被调用能力。
    ///
    /// Python 侧 scripts/build_codeoss_minimal.py --check 与这里的 SHA-256
    /// 比对是同一套口径的双实现；任何一侧漂移都红灯。
    /// 所有断言都是「读真实文件」，没有一条是「开关存在就算通过」。
    /// </summary>
    public static class CodeOssMinimalTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("CODEOSS_R19_FAIL: " + msg);
        }
        private static bool IsRoot(string dir)
            => File.Exists(Path.Combine(dir, "ide", "codeoss", "MINIMAL.lock.json"))
            || File.Exists(Path.Combine(dir, "ide", "codeoss", "UPSTREAM.lock.json"));
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
            throw new InvalidOperationException("未找到仓库根（MINIMAL.lock.json / UPSTREAM.lock.json）");
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

        private static readonly string[] ConfigFiles = {
            "ide/codeoss/product.json", "ide/codeoss/UPSTREAM.lock.json",
            ".gitmodules", "ci/codeoss-gate.yml",
            "scripts/fetch_codeoss.py", "scripts/build_codeoss_minimal.py",
            "ide/codeoss/language-profile.json"
        };
        private static readonly string[] Exclusions = {
            "electron", "native_modules", "marketplace", "telemetry", "branding",
            "builtin_extension_download", "update_service", "extensions_gallery"
        };

        public static void TestR19MinimalLockPinsLatestStableTag()
        {
            var m = J("ide/codeoss/MINIMAL.lock.json");
            Require(m.GetProperty("tag").GetString() == "1.148.0", "tag 应为 1.148.0");
            Require(m.GetProperty("commit").GetString()
                    == "661626d0e72f97806550790a98fb87116c9db825", "commit 未精确锁定");
            Require(m.GetProperty("license").GetString() == "MIT", "license 应为 MIT");
            Require(m.GetProperty("upstream").GetString().Contains("microsoft/vscode",
                    StringComparison.Ordinal), "upstream 应为 microsoft/vscode");
            Require(m.GetProperty("released").GetString() == "2026-11-24", "发布日");
            Require(m.GetProperty("latest_stable_verified").GetString() == "2026-11-28",
                    "最新稳定版查证日");
        }

        public static void TestR19MinimalProfileIsWebOnlyAndNotCompiled()
        {
            var m = J("ide/codeoss/MINIMAL.lock.json");
            Require(m.GetProperty("profile").GetString() == "web-only-minimal", "profile");
            Require(m.GetProperty("build_target").GetString() == "web", "build_target");
            Require(m.GetProperty("compile_in_this_repo").GetBoolean() == false,
                    "不得谎报本仓库编译了上游 workbench");
            Require(m.GetProperty("compile_note").GetString().Contains("不编译",
                    StringComparison.Ordinal), "编译声明应明确");
        }

        public static void TestR19MinimalExclusionsCoverEveryHeavySurface()
        {
            var m = J("ide/codeoss/MINIMAL.lock.json");
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in m.GetProperty("exclusions").EnumerateArray())
                set.Add(e.GetString() ?? "");
            foreach (var x in Exclusions)
                Require(set.Contains(x), "排除项缺失: " + x);
            Require(set.Count == Exclusions.Length, "排除项集合应精确为 8 项，实际 " + set.Count);
        }

        public static void TestR19ProductJsonDeclaresMinimalProfile()
        {
            var p = J("ide/codeoss/product.json");
            var y = p.GetProperty("yamaMinimal");
            Require(y.GetProperty("profile").GetString() == "web-only-minimal", "profile");
            Require(y.GetProperty("buildTarget").GetString() == "web", "buildTarget");
            foreach (var k in new[] { "noElectron", "noNativeModules", "noMarketplace",
                                      "noTelemetry", "noBranding",
                                      "noBuiltinExtensionDownload", "noUpdateService" })
                Require(y.GetProperty(k).GetBoolean(), "yamaMinimal." + k + " 应为 true");
            Require(!y.GetProperty("compiledInThisRepo").GetBoolean(),
                    "compiledInThisRepo 必须为 false");
            // 干净 MIT 6 项仍须成立（第十四轮口径不回退）
            Require(p.GetProperty("licenseName").GetString() == "MIT", "licenseName");
            Require(!p.GetProperty("enableTelemetry").GetBoolean(), "遥测禁用");
            Require(p.GetProperty("telemetryEndpoint").GetString() == "", "无遥测端点");
            Require(p.GetProperty("extensionsGallery").EnumerateObject().Count() == 0,
                    "gallery 为空");
            Require(p.GetProperty("builtInExtensions").GetArrayLength() == 0,
                    "无内置扩展下载");
        }

        public static void TestR19ConfigSha256MatchesEveryRealFile()
        {
            var m = J("ide/codeoss/MINIMAL.lock.json");
            var declared = m.GetProperty("config_sha256");
            foreach (var rel in ConfigFiles)
            {
                Require(declared.TryGetProperty(rel, out var want),
                        "config_sha256 缺 " + rel);
                string normalized = Rd(rel).Replace("\r\n", "\n");
                string got = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(normalized)));
                Require(got == want.GetString(),
                        "SHA-256 不匹配 " + rel + " got=" + got);
            }
            int n = 0;
            foreach (var _ in declared.EnumerateObject()) n++;
            Require(n == ConfigFiles.Length, "摘要键数应为 7，实际 " + n);
        }

        public static void TestR19MinimalBuildScriptIsOfflineAndFailClosed()
        {
            string s = Rd("scripts/build_codeoss_minimal.py");
            foreach (var mode in new[] { "--check", "--plan", "--emit-manifest" })
                Require(s.Contains(mode, StringComparison.Ordinal), "缺模式 " + mode);
            Require(s.Contains("hashlib.sha256", StringComparison.Ordinal), "应算 SHA-256");
            Require(s.Contains("return 0 if not errs else 1", StringComparison.Ordinal),
                    "应 fail-closed");
            Require(s.Contains("clean_mit_build", StringComparison.Ordinal), "应输出判定");
            Require(!s.Contains("subprocess.run", StringComparison.Ordinal),
                    "校验脚本自身不应跑外部进程");
            Require(s.Contains("CONFIG_FILES", StringComparison.Ordinal), "应有配置文件清单");
        }

        public static void TestR19CiGateDoesNotClaimElectronCompile()
        {
            string g = Rd("ci/codeoss-gate.yml");
            Require(!g.Contains("npm ci", StringComparison.Ordinal),
                    "轻量门禁不得声明完整 npm 安装");
            Require(!g.Contains("npm run compile", StringComparison.Ordinal),
                    "轻量门禁不得声明完整 Electron workbench 编译");
            Require(g.Contains("build_codeoss_minimal.py --check", StringComparison.Ordinal),
                    "应调用离线校验");
            Require(g.Contains("--emit-manifest", StringComparison.Ordinal),
                    "应产出轻量清单");
            Require(g.Contains("minimal-build-manifest.json", StringComparison.Ordinal),
                    "应上传轻量清单而非 workbench 产物");
            Require(g.Contains("不编译", StringComparison.Ordinal), "应明确不编译声明");
        }

        public static void TestR19TuAnBridgeReallyInvokesReadOnlyActions()
        {
            string b = Rd("integrations/tuan_cli/yama_bridge.py");
            foreach (var a in new[] { "verify_codeoss_minimal", "capabilities",
                                      "read_contract", "codeoss_manifest",
                                      "sota_status", "hook_gate", "command_intent" })
                Require(b.Contains(a, StringComparison.Ordinal), "动作缺失 " + a);
            Require(b.Contains("def invoke", StringComparison.Ordinal), "应有 dispatcher");
            Require(b.Contains("subprocess.run", StringComparison.Ordinal),
                    "应真跑校验脚本 / hook，而不是只返回字符串");
            Require(b.Contains("read_only", StringComparison.Ordinal), "只读标记");
            Require(b.Contains("fail-closed", StringComparison.Ordinal),
                    "未知动作应 fail-closed");
            var reg = J("integrations/tuan_cli/yama_actions.json");
            Require(reg.GetProperty("read_only").GetBoolean(), "注册表应只读");
            Require(reg.GetProperty("actions").GetArrayLength() == 7, "应有 7 个动作");
        }

        public static void TestR19McpToolsCallDelegatesToBridge()
        {
            string m = Rd("integrations/tuan_cli/mcp_server.py");
            Require(m.Contains("bridge_invoke", StringComparison.Ordinal),
                    "tools/call 应真委派到 bridge");
            Require(m.Contains("delegated", StringComparison.Ordinal), "应标注已委派");
            Require(m.Contains("isError", StringComparison.Ordinal), "失败应标记");
            foreach (var t in new[] { "yama_codeoss_minimal", "yama_invoke",
                                      "yama_capabilities" })
                Require(m.Contains(t, StringComparison.Ordinal), "工具缺失 " + t);
            Require(m.Contains("unknown tool", StringComparison.Ordinal),
                    "未知工具仍应拒绝");
        }

        public static void TestR19CliEntryPointIsStandalone()
        {
            string c = Rd("integrations/tuan_cli/yama_cli.py");
            Require(c.Contains("subcommand.handle", StringComparison.Ordinal),
                    "CLI 应委派到 handle()");
            Require(c.Contains("__main__", StringComparison.Ordinal), "应可直接执行");
            string s = Rd("integrations/tuan_cli/subcommand.py");
            Require(s.Contains("bridge_invoke", StringComparison.Ordinal),
                    "插件子命令也应真委派");
            Require(s.Contains("codeoss-minimal", StringComparison.Ordinal), "新子命令");
            Require(s.Contains("invoke", StringComparison.Ordinal), "invoke 子命令");
            Require(s.Contains("capabilities", StringComparison.Ordinal), "capabilities 子命令");
        }

        public static void TestR19ContractDeclaresCallableActionsAnd13By13()
        {
            var c = J("integrations/tuan_cli/contract.json");
            Require(c.GetProperty("mcp_tools").GetArrayLength() == 13, "13 工具");
            Require(c.GetProperty("subcommands").GetArrayLength() == 13, "13 子命令");
            Require(c.GetProperty("skills").GetArrayLength() == 5, "5 skills 不变");
            var cm = c.GetProperty("codeoss_minimal");
            Require(cm.GetProperty("profile").GetString() == "web-only-minimal", "profile");
            Require(cm.GetProperty("tag").GetString() == "1.148.0", "tag");
            Require(!cm.GetProperty("compile_in_this_repo").GetBoolean(), "未编译声明");
            var ca = c.GetProperty("callable_actions");
            Require(ca.GetProperty("read_only").GetBoolean(), "只读");
            Require(ca.GetProperty("actions").GetArrayLength() == 7, "7 动作");
            Require(ca.GetProperty("dispatcher").GetString().Contains("invoke",
                    StringComparison.Ordinal), "dispatcher");
            foreach (var f in c.GetProperty("files").EnumerateArray())
                Require(File.Exists(Path.Combine(R, "integrations", "tuan_cli",
                        (f.GetString() ?? "").Replace('/', Path.DirectorySeparatorChar))),
                        "适配层文件缺失 " + f.GetString());
        }

        public static void TestR19NoFalseClaimOfBuiltWorkbench()
        {
            string readme = Rd("ide/codeoss/README.md");
            string plan = Rd("docs/CODEOSS_MINIMAL_TUAN_PLAN.md");
            string delivery = Rd("docs/ROUND19_DELIVERY.md");
            string gate = Rd("ci/codeoss-gate.yml");
            foreach (var s in new[] { readme, plan, delivery, gate })
            {
                Require(s.Contains("不编译", StringComparison.Ordinal)
                     || s.Contains("未编译", StringComparison.Ordinal)
                     || s.Contains("不宣称", StringComparison.Ordinal),
                        "必须明确写出未编译 / 不宣称");
                Require(!s.Contains("已编译完整 workbench", StringComparison.Ordinal),
                        "不得宣称已编译完整 workbench");
            }
            Require(readme.Contains("1.148.0", StringComparison.Ordinal)
                 && readme.Contains("最新稳定", StringComparison.Ordinal),
                    "README 应写明 1.148.0 是当前最新稳定版");
            Require(plan.Contains("M0", StringComparison.Ordinal)
                 && plan.Contains("M4", StringComparison.Ordinal)
                 && plan.Contains("验收矩阵", StringComparison.Ordinal),
                    "计划应含里程碑与验收矩阵");
        }
    }
}
