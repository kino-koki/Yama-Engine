using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 14 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docs", "capability_matrix.csv")))
                    return dir.FullName;
            }
            throw new InvalidOperationException("未找到仓库根（docs/capability_matrix.csv）");
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
            Assert.Equal(4, files.Length);
            foreach (var f in files)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                Assert.True(doc.RootElement.TryGetProperty("$schema", out _), f);
                Assert.True(doc.RootElement.TryGetProperty("type", out _), f);
            }
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
            foreach (var f in Directory.GetFiles(troot, "*.yama", SearchOption.AllDirectories))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                Assert.Equal(1, doc.RootElement.GetProperty("Version").GetInt32());
                Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("Objects").ValueKind);
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
    }
}
