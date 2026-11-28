using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using YamaEngine.Editor.CodeOss;
using Xunit;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 仓库契约门禁（xUnit [Fact]，进入现有 build.yml 的 Test step）。
    ///
    /// 背景：本轮探测确认推送 token 的 scope 仅为 `repo`，写入
    /// `.github/workflows/` 返回 404，因此 `ci/godot3-capability-gate.yml`
    /// 无法自动启用。作为替代，这里用纯 C# 把静态审计的关键断言实现一遍，
    /// 让「能力矩阵 / JSON Schema / 模板 / 导出契约 / 差距看板」在每一次
    /// 真实 CI 中都被校验——静态门禁由此真正生效，无需 workflow 权限。
    ///
    /// 与 scripts/capability_audit.py 是同一套口径的双实现（Python 供本地，
    /// C# 供 CI），任何一侧收紧都必须同步另一侧。
    /// </summary>
    public class RepoContractTests
    {
        private static bool IsRepoRoot(string dir)
            => File.Exists(Path.Combine(dir, "docs", "capability_matrix.csv"))
            || File.Exists(Path.Combine(dir, "ide", "codeoss", "UPSTREAM.lock.json"));

        private static string FindRepoRoot()
        {
            // 2026-11-27 稳定性加固：main 首次 CI 出现过一次偶发 Test 红，
            // 同 commit 的 develop / rerun attempt2 均全绿。仓库根定位改为
            // 多锚点 + 双起点（BaseDirectory / CurrentDirectory）+ 环境变量兜底，
            // 避免 testhost 工作目录偶发差异导致单锚点回溯失败。
            string? env = Environment.GetEnvironmentVariable("YAMA_REPO_ROOT");
            if (!string.IsNullOrEmpty(env) && IsRepoRoot(env)) return env;
            var starts = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
            foreach (var start in starts)
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 16 && dir != null; i++, dir = dir.Parent)
                    if (IsRepoRoot(dir.FullName)) return dir.FullName;
            }
            throw new InvalidOperationException(
                "未找到仓库根（docs/capability_matrix.csv 或 ide/codeoss/UPSTREAM.lock.json）");
        }

        private static string Root => _root ??= FindRepoRoot();
        private static string? _root;

        [Fact]
        public void TestCapabilityMatrixIsWellFormed()
        {
            var lines = File.ReadAllLines(Path.Combine(Root, "docs", "capability_matrix.csv"));
            Assert.True(lines.Length >= 82, $"能力矩阵应有表头 + 81 项，实际 {lines.Length} 行");
            Assert.StartsWith("id,category,godot35_capability", lines[0]);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int data = 0;
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var id = line.Split(',')[0];
                Assert.Matches(@"^G3-\d{3}$", id);
                Assert.True(ids.Add(id), "能力 ID 重复: " + id);
                data++;
            }
            Assert.Equal(81, data);
        }

        [Fact]
        public void TestAllJsonSchemasAreValid()
        {
            var dir = Path.Combine(Root, "schemas");
            var files = Directory.GetFiles(dir, "*.json").OrderBy(x => x).ToArray();
            // 第二十二轮新增 schemas/yama-script.schema.json，精确同步为 5。
            Assert.Equal(5, files.Length);
            bool sawScriptBindingSchema = false;
            foreach (var f in files)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("$schema", out _), f);
                Assert.True(root.TryGetProperty("type", out _), f);
                if (root.TryGetProperty("$id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && (id.GetString() ?? "").Contains("yama-script.schema.json",
                        StringComparison.Ordinal))
                {
                    sawScriptBindingSchema = true;
                    // 新 schema 必须真的声明带 kind 白名单的 TypedValue，不能是空壳。
                    var defs = root.GetProperty("$defs");
                    Assert.True(defs.TryGetProperty("TypedValue", out var tv), f);
                    Assert.Equal(JsonValueKind.Array,
                        tv.GetProperty("properties").GetProperty("kind")
                          .GetProperty("enum").ValueKind);
                }
            }
            Assert.True(sawScriptBindingSchema, "行为层声明 schema 缺失");
        }

        [Fact]
        public void TestTemplatesAndExportContractsAreValid()
        {
            var troot = Path.Combine(Root, "Templates");
            foreach (var f in Directory.GetFiles(troot, "*.json", SearchOption.AllDirectories))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            }
            // 第二十二轮：Templates 下出现两类 .yama —— 场景文件与脚本绑定声明。
            // 按 schema 字段分流，两类都施加真实门禁，不跳过任何文件、不吞异常。
            foreach (var f in Directory.GetFiles(troot, "*.yama", SearchOption.AllDirectories))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                if (root.TryGetProperty("schema", out var sc)
                    && sc.ValueKind == JsonValueKind.String
                    && sc.GetString() == "yama.script-binding.v1")
                {
                    Assert.True(root.TryGetProperty("typeName", out var tn), f);
                    Assert.False(string.IsNullOrWhiteSpace(tn.GetString()), f);
                    Assert.Equal(JsonValueKind.Object,
                        root.GetProperty("properties").ValueKind);
                    Assert.True(root.TryGetProperty("lifecycle", out _), f);
                    continue;
                }
                Assert.Equal(1, root.GetProperty("Version").GetInt32());
                Assert.Equal(JsonValueKind.Array, root.GetProperty("Objects").ValueKind);
            }
            foreach (var f in Directory.GetFiles(troot, "*.tileset.json", SearchOption.AllDirectories))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                Assert.Equal(1, doc.RootElement.GetProperty("formatVersion").GetInt32());
            }
            var exports = Directory.GetFiles(Path.Combine(troot, "Export"), "template.json", SearchOption.AllDirectories);
            Assert.Equal(6, exports.Length);
            foreach (var f in exports)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                Assert.True(doc.RootElement.TryGetProperty("platform", out _), f);
                Assert.True(doc.RootElement.TryGetProperty("runtimeBinary", out _), f);
            }
        }

        [Fact]
        public void TestRequiredDocumentationExists()
        {
            string[] required = {
                "docs/GODOT3_CAPABILITY_MATRIX.md", "docs/E2E_ACCEPTANCE.md",
                "docs/BUILD_DECISIONS.md", "docs/EDITOR_WIRING.md",
                "docs/SCRIPTING_ABI_SPEC.md", "docs/PLATFORM_MATRIX.md",
                "docs/DEVELOPMENT_ROADMAP.md", "docs/ECOLOGY_ROADMAP.md",
                "docs/PLACEHOLDER_TRIAGE.md", "docs/AUDIO_DECODER_GAPS.md",
                "docs/CI_RED_TO_GREEN_REPORT.md", "docs/DELIVERY_OVERVIEW.md",
                "scripts/capability_audit.py", "scripts/assembly_boundary_check.py"
            };
            foreach (var r in required)
                Assert.True(File.Exists(Path.Combine(Root, r)), "缺少交付物: " + r);
        }

        [Fact]
        public void TestScriptAbiIsCompiledIntoSameAssembly()
        {
            // 证明 L2 绑定 ABI 不只是磁盘上的 .cs，而是真的编进了测试引用的程序集。
            var asm = typeof(YamaEngine.Scripting.Abi.ScriptBinding).Assembly;
            Assert.Contains("YamaEngine.Scripting.Abi.PropertyBag", asm.GetTypes().Select(t => t.FullName ?? string.Empty));
            Assert.Contains("YamaEngine.Scripting.Abi.SignalBus", asm.GetTypes().Select(t => t.FullName ?? string.Empty));
        }

        [Fact]
        public void TestSelfResearchBoundarySpdxCoversEveryPackageReference()
        {
            var csproj = File.ReadAllText(Path.Combine(Root, "YamaEngine", "YamaEngine.csproj"));
            var spdx = File.ReadAllText(Path.Combine(Root, "docs", "THIRD_PARTY_SPX.md"));
            const string marker = "<PackageReference Include=\"";
            int idx = 0, count = 0;
            while ((idx = csproj.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
            {
                int s = idx + marker.Length;
                int e = csproj.IndexOf('"', s);
                var name = csproj.Substring(s, e - s);
                Assert.Contains(name, spdx);
                count++;
                idx = e + 1;
            }
            Assert.Equal(13, count);
            Assert.Contains("Apache-2.0", spdx);
            Assert.Contains("MIT", spdx);
            Assert.True(File.Exists(Path.Combine(Root, "docs", "SELF_RESEARCH_BOUNDARY.md")));
            Assert.True(File.Exists(Path.Combine(Root, "scripts", "audit_no_engine_copy.py")));
        }

        [Fact]
        public void TestCodeOssUpstreamLockPinnedAndCleanProduct()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "ide", "codeoss", "UPSTREAM.lock.json")));
            var r = doc.RootElement;
            Assert.Equal("1.148.0", r.GetProperty("tag").GetString());
            Assert.Equal("661626d0e72f97806550790a98fb87116c9db825",
                         r.GetProperty("commit").GetString());
            Assert.Equal("MIT", r.GetProperty("license").GetString());
            using var pd = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "ide", "codeoss", "product.json")));
            var p = pd.RootElement;
            Assert.Equal("MIT", p.GetProperty("licenseName").GetString());
            Assert.False(p.GetProperty("enableTelemetry").GetBoolean());
            Assert.Equal(string.Empty, p.GetProperty("telemetryEndpoint").GetString());
            Assert.Equal(JsonValueKind.Object, p.GetProperty("extensionsGallery").ValueKind);
            int galleryEntries = 0;
            foreach (var _ in p.GetProperty("extensionsGallery").EnumerateObject())
                galleryEntries++;
            Assert.Equal(0, galleryEntries);
            Assert.DoesNotContain("Visual Studio Code", p.GetProperty("nameShort").GetString()!);
            Assert.Contains("microsoft/vscode", File.ReadAllText(Path.Combine(Root, ".gitmodules")));
        }

        [Fact]
        public void TestTuanCliIntegrationContract()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "integrations", "tuan_cli", "contract.json")));
            var r = doc.RootElement;
            Assert.Equal("1.148.0", r.GetProperty("codeoss_version").GetString());
            Assert.Equal("yama-side-only", r.GetProperty("integration_mode").GetString());
            Assert.Contains("TUAN.md", r.GetProperty("write_policy").GetString()!);
            foreach (var f in r.GetProperty("files").EnumerateArray())
                Assert.True(File.Exists(Path.Combine(Root, "integrations", "tuan_cli",
                    (f.GetString() ?? "").Replace('/', Path.DirectorySeparatorChar))),
                    "适配层文件缺失: " + f.GetString());
            int skills = 0;
            foreach (var _ in r.GetProperty("skills").EnumerateArray()) skills++;
            Assert.Equal(5, skills);
            int tools = 0;
            foreach (var _ in r.GetProperty("mcp_tools").EnumerateArray()) tools++;
            Assert.Equal(13, tools);
            int sub = 0;
            foreach (var _ in r.GetProperty("subcommands").EnumerateArray()) sub++;
            Assert.Equal(13, sub);
        }

        [Fact]
        public void TestNoForeignEngineCopyrightHeadersInSources()
        {
            var forbidden = new[]
            {
                "Godot Engine contributors", "Copyright Epic Games",
                "Copyright Unity Technologies", "Copyright NVIDIA PhysX",
                "#include \"godot/", "#include \"PhysX/",
                "namespace godot", "namespace physx"
            };
            int files = 0;
            foreach (var f in Directory.GetFiles(Path.Combine(Root, "YamaEngine"),
                                                 "*.cs", SearchOption.AllDirectories))
            {
                files++;
                var head = new System.Text.StringBuilder();
                using var sr = new StreamReader(f);
                for (int i = 0; i < 40 && !sr.EndOfStream; i++) head.AppendLine(sr.ReadLine());
                string h = head.ToString();
                foreach (var bad in forbidden)
                    Assert.DoesNotContain(bad, h);
            }
            Assert.True(files > 700, $"扫描源文件过少: {files}");
        }


        [Fact]
        public void TestThirdPartyInventoryMatchesCsprojAndSubmodule()
        {
            using var inv = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "docs", "THIRD_PARTY_INVENTORY.json")));
            var r = inv.RootElement;
            Assert.Equal("docs/SELF_RESEARCH_BOUNDARY.md", r.GetProperty("policy").GetString());
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in r.GetProperty("package_references").EnumerateArray())
            {
                string n = p.GetProperty("name").GetString()!;
                Assert.True(names.Add(n), "清单重复: " + n);
                Assert.False(string.IsNullOrEmpty(p.GetProperty("spdx").GetString()));
                Assert.False(string.IsNullOrEmpty(p.GetProperty("version").GetString()));
            }
            // 与 YamaEngine.csproj 的 PackageReference 集合逐项一致
            var csproj = File.ReadAllText(Path.Combine(Root, "YamaEngine", "YamaEngine.csproj"));
            const string marker = "<PackageReference Include=\"";
            int idx = 0, count = 0;
            while ((idx = csproj.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
            {
                int a = idx + marker.Length, b = csproj.IndexOf('"', a);
                string n = csproj.Substring(a, b - a);
                Assert.True(names.Contains(n), "csproj 包未进清单: " + n);
                count++; idx = b + 1;
            }
            Assert.Equal(13, count);
            Assert.Equal(13, names.Count);
            string sub = File.ReadAllText(Path.Combine(Root, ".gitmodules"));
            Assert.Contains("microsoft/vscode", sub);
            var sm = r.GetProperty("submodules")[0];
            Assert.Equal("1.148.0", sm.GetProperty("tag").GetString());
            Assert.Equal("661626d0e72f97806550790a98fb87116c9db825",
                         sm.GetProperty("commit").GetString());
            Assert.Equal("MIT", sm.GetProperty("spdx").GetString());
            Assert.True(File.Exists(Path.Combine(Root, "ide", "codeoss", "UPSTREAM.lock.json")));
        }

        [Fact]
        public void TestCodeOssMinimalProfileIsPinnedAndTuanIsCallable()
        {
            using var m = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "ide", "codeoss", "MINIMAL.lock.json")));
            var r = m.RootElement;
            Assert.Equal("1.148.0", r.GetProperty("tag").GetString());
            Assert.Equal("661626d0e72f97806550790a98fb87116c9db825",
                         r.GetProperty("commit").GetString());
            Assert.Equal("web-only-minimal", r.GetProperty("profile").GetString());
            Assert.Equal("web", r.GetProperty("build_target").GetString());
            Assert.False(r.GetProperty("compile_in_this_repo").GetBoolean());
            int ex = 0;
            foreach (var _ in r.GetProperty("exclusions").EnumerateArray()) ex++;
            Assert.Equal(8, ex);
            // 6 个轻量配置文件 SHA-256 与 Python --check 同口径
            foreach (var kv in r.GetProperty("config_sha256").EnumerateObject())
            {
                string rel = kv.Name;
                string normalized = File.ReadAllText(Path.Combine(Root,
                    rel.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");
                string got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(normalized)));
                Assert.Equal(kv.Value.GetString(), got);
            }
            using var p = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "ide", "codeoss", "product.json")));
            Assert.Equal("web-only-minimal",
                p.RootElement.GetProperty("yamaMinimal").GetProperty("profile").GetString());
            Assert.False(p.RootElement.GetProperty("yamaMinimal")
                .GetProperty("compiledInThisRepo").GetBoolean());
            // Tuan 三入口与动作注册表
            using var c = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "integrations", "tuan_cli", "contract.json")));
            Assert.Equal(13, c.RootElement.GetProperty("mcp_tools").GetArrayLength());
            Assert.Equal(13, c.RootElement.GetProperty("subcommands").GetArrayLength());
            Assert.Equal(7, c.RootElement.GetProperty("callable_actions")
                .GetProperty("actions").GetArrayLength());
            Assert.True(File.Exists(Path.Combine(Root, "integrations", "tuan_cli",
                "yama_bridge.py")));
            Assert.True(File.Exists(Path.Combine(Root, "integrations", "tuan_cli",
                "yama_cli.py")));
            Assert.True(File.Exists(Path.Combine(Root, "scripts",
                "build_codeoss_minimal.py")));
        }

        [Fact]
        public void TestRound20EngineSideBridgeExposesMinimalBuild()
        {
            // 证明最轻量能力真的编进了被引用的程序集，不只是磁盘上的脚本。
            Assert.Equal(14, YamaExtensionManifest.Commands.Length);
            Assert.Contains("yama.languageProfile", YamaExtensionManifest.Commands);
            Assert.Contains("yama.newGameProject", YamaExtensionManifest.Commands);
            Assert.Contains("yama.codeossMinimal", YamaExtensionManifest.Commands);
            Assert.Contains("yama.invoke", YamaExtensionManifest.Commands);
            Assert.Contains("yama.capabilities", YamaExtensionManifest.Commands);
            string manifest = YamaExtensionManifest.Build("1.148.0");
            Assert.True(YamaExtensionManifest.IsWellFormed(manifest));
            using var product = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Root, "ide", "codeoss", "product.json")));
            var parsed = YamaCodeOssProduct.FromJson(product.RootElement.GetRawText());
            Assert.True(parsed.HasMinimalSection);
            Assert.Equal("web-only-minimal", parsed.MinimalProfile);
            Assert.Equal("web", parsed.MinimalBuildTarget);
            Assert.False(parsed.MinimalCompiledInThisRepo);
            Assert.True(parsed.IsMinimalWebBuild);
            Assert.True(parsed.IsCleanMitBuild);
            var bridge = new YamaCodeOssBridge(product.RootElement.GetRawText(), "1.148.0");
            Assert.True(bridge.IsCleanMitBuild);
            Assert.True(bridge.Dispatch("yama.codeossMinimal").Handled);
            Assert.True(bridge.Dispatch("yama.invoke").Handled);
            Assert.True(bridge.Dispatch("yama.capabilities").Handled);
            // Python 门禁无法写进 workflow（token 仅 repo，实测 404），
            // 故如实锁成「模板未启用 + C# 等价门禁真实存在」，不谎报已启用。
            using var ct = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,
                "integrations", "tuan_cli", "contract.json")));
            Assert.False(ct.RootElement.GetProperty("ci_gate_enabled").GetBoolean());
            Assert.Contains("NOT enabled", ct.RootElement.GetProperty("ci_gate").GetString()!);
            Assert.True(File.Exists(Path.Combine(Root, "ci", "codeoss-gate.yml")));
            Assert.True(File.Exists(Path.Combine(Root, "YamaEngine.Tests",
                "CodeOssMinimalBridgeTests.cs")));
            // fetch 脚本口径不回漂
            string fetch = File.ReadAllText(Path.Combine(Root, "scripts", "fetch_codeoss.py"));
            Assert.DoesNotContain("npm ci && npm run compile", fetch);
            Assert.Contains("本仓库不编译", fetch);
        }

        [Fact]
        public void TestRound21YamaFirstLanguageProfileIsEnforced()
        {
            // 语言面收口是「可判定事实」：主语言强制、可选模块零内置、模板全过契约。
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,
                "ide", "codeoss", "language-profile.json")));
            var r = doc.RootElement;
            Assert.Equal("yama", r.GetProperty("primary").GetProperty("id").GetString());
            Assert.True(r.GetProperty("primary").GetProperty("enforced").GetBoolean());
            Assert.True(r.GetProperty("enforcement").GetProperty("strictYamaProject").GetBoolean());
            foreach (var m in r.GetProperty("optional_modules").EnumerateArray())
            {
                Assert.False(m.GetProperty("installed").GetBoolean());
                Assert.True(m.GetProperty("removable").GetBoolean());
            }
            var profile = YamaEngine.Editor.CodeOss.YamaLanguageProfile.FromJson(
                r.GetRawText());
            Assert.True(profile.IsYamaFirst);
            var product = YamaEngine.Editor.CodeOss.YamaCodeOssProduct.FromJson(
                File.ReadAllText(Path.Combine(Root, "ide", "codeoss", "product.json")));
            Assert.True(product.IsYamaFirstLanguage);
            Assert.True(product.IsMinimalWebBuild);
            foreach (var t in new[] { "Basic2D", "Basic3D", "NpcRealityDemo" })
            {
                using var p = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,
                    "Templates", t, "yama.project.json")));
                Assert.Equal("yama", p.RootElement.GetProperty("primaryLanguage").GetString());
                Assert.EndsWith(".yama",
                    p.RootElement.GetProperty("startupScene").GetString()!);
            }
            Assert.True(File.Exists(Path.Combine(Root, "scripts", "check_yama_project.py")));
        }

        [Fact]
        public void TestRound22YamaScriptDeclarationRestoresBindingAbi()
        {
            // 行为层声明真的编进被测程序集，且能还原成 L2 绑定 ABI 对象。
            string json = File.ReadAllText(Path.Combine(Root, "Templates", "Basic2D",
                "Content", "Scripts", "PlayerController.yama"));
            Assert.True(YamaEngine.Scripting.Abi.YamaScriptDeclaration.TryParse(
                json, out var decl, out var error), error);
            Assert.NotNull(decl);
            Assert.Equal("PlayerController", decl!.TypeName);
            Assert.Equal(5, decl.Properties.Count);
            var handles = new YamaEngine.Scripting.Abi.NodeHandleTable();
            var registry = new YamaEngine.Scripting.Abi.ScriptRegistry();
            registry.Register("PlayerController", b => new object());
            var target = new object();
            var binding = decl.CreateBinding(handles, registry, target);
            Assert.True(binding.Node.IsValid);
            Assert.True(binding.Properties.Get("speed",
                YamaEngine.Scripting.Abi.ScriptVariant.Null).AsDouble() > 6.24);
            Assert.Contains("yama.scriptBinding",
                YamaEngine.Editor.CodeOss.YamaExtensionManifest.Commands);
            Assert.Contains("yama.newScript",
                YamaEngine.Editor.CodeOss.YamaExtensionManifest.Commands);
            string spec = File.ReadAllText(Path.Combine(Root, "docs", "SCRIPTING_ABI_SPEC.md"));
            Assert.DoesNotContain("规格文档，不是实现", spec);
            Assert.Contains("L2 已实现", spec);
        }

        [Fact]
        public void TestNoForeignEngineSourceUrlsInWholeSources()
        {
            // 第十六轮深化：不只扫文件头，全文都不得出现其他引擎的源码 URL /
            // 仓库标识。README/文档里的「对标 UE5」等描述性文字不在扫描范围。
            var bad = new[] {
                "github.com/godotengine/godot", "github.com/EpicGames/UnrealEngine",
                "github.com/Unity-Technologies", "github.com/NVIDIAGameWorks/PhysX",
                "github.com/bulletphysics", "github.com/erincatto/box2d",
                "github.com/jrouwe/JoltPhysics"
            };
            int scanned = 0;
            foreach (var f in Directory.GetFiles(Path.Combine(Root, "YamaEngine"),
                                                 "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                string all = File.ReadAllText(f);
                foreach (var b in bad)
                    Assert.DoesNotContain(b, all);
            }
            Assert.True(scanned > 700, $"扫描源文件过少: {scanned}");
        }

    }
}
