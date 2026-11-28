using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using YamaEngine.Editor.CodeOss;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十四轮回归（12 项）：Code - OSS 干净 MIT 构建 + 自研 LSP/DAP 桥接
    /// + Tuan-CLI 适配层契约。反射发现，进入现有 build.yml 的 Test step。
    /// </summary>
    public static class CodeOssBridgeTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("CODEOSS_R14_FAIL: " + msg);
        }
        private static bool IsRoot(string dir)
            => File.Exists(Path.Combine(dir, "docs", "capability_matrix.csv"))
            || File.Exists(Path.Combine(dir, "ide", "codeoss", "UPSTREAM.lock.json"));

        private static string FindRoot()
        {
            string? env = Environment.GetEnvironmentVariable("YAMA_REPO_ROOT");
            if (!string.IsNullOrEmpty(env) && IsRoot(env)) return env;
            var starts = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
            foreach (var start in starts)
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 16 && dir != null; i++, dir = dir.Parent)
                    if (IsRoot(dir.FullName)) return dir.FullName;
            }
            throw new InvalidOperationException("未找到仓库根（多锚点均失败）");
        }
        private static string R => _r ??= FindRoot();
        private static string? _r;
        private static string Read(string rel) => File.ReadAllText(Path.Combine(R, rel.Replace('/', Path.DirectorySeparatorChar)));

        public static void TestR14ProductJsonIsCleanMitBuild()
        {
            var p = YamaCodeOssProduct.FromJson(Read("ide/codeoss/product.json"));
            Require(p.LicenseName == "MIT", "licenseName 应为 MIT");
            Require(p.NameShort.Contains("Code - OSS", StringComparison.Ordinal),
                    "nameShort 应保留 Code - OSS 标识");
            Require(!p.NameShort.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase),
                    "不得含微软官方品牌");
            Require(!p.TelemetryEnabled && p.TelemetryEndpoint.Length == 0,
                    "遥测必须禁用且无端点");
            Require(!p.HasExtensionsGallery, "不得配置 Marketplace gallery");
            Require(p.Version == "1.148.0" && p.Commit.Length == 40,
                    "版本/commit 应锁定");
            Require(p.IsCleanMitBuild, "IsCleanMitBuild 应通过");
            Require(p.Bridge.Contains("YamaCodeOssBridge", StringComparison.Ordinal),
                    "应指向自研桥接类");
        }

        public static void TestR14UpstreamLockPinsExactCommit()
        {
            using var doc = JsonDocument.Parse(Read("ide/codeoss/UPSTREAM.lock.json"));
            var r = doc.RootElement;
            Require(r.GetProperty("tag").GetString() == "1.148.0", "tag 应为 1.148.0");
            Require(r.GetProperty("commit").GetString()
                    == "661626d0e72f97806550790a98fb87116c9db825", "commit 未精确锁定");
            Require(r.GetProperty("license").GetString() == "MIT", "license 应为 MIT");
            Require(r.GetProperty("upstream").GetString().Contains("microsoft/vscode",
                    StringComparison.Ordinal), "upstream 应为 microsoft/vscode");
        }

        public static void TestR14GitmodulesPinsCodeOssTag()
        {
            string g = Read(".gitmodules");
            Require(g.Contains("microsoft/vscode", StringComparison.Ordinal), "缺 upstream URL");
            Require(g.Contains("1.148.0", StringComparison.Ordinal), "未锁定 tag");
            Require(g.Contains("ide/codeoss/upstream", StringComparison.Ordinal), "缺 path");
            Require(Read("docs/THIRD_PARTY_SPX.md").Contains("microsoft/vscode",
                    StringComparison.Ordinal), "SPDX 清单缺 Code - OSS 署名");
        }

        public static void TestR14LspContentLengthFrameRoundTrip()
        {
            var lsp = new YamaLspClient();
            string req = lsp.Initialize("file:///repo");
            byte[] frame = YamaJsonRpcFraming.Encode(req);
            string head = Encoding.ASCII.GetString(frame, 0, 40);
            Require(head.StartsWith("Content-Length: ", StringComparison.Ordinal),
                    "帧头应为 Content-Length");
            Require(head.Contains("\r\n\r\n", StringComparison.Ordinal), "应有 CRLF 分隔");
            Require(YamaJsonRpcFraming.TryDecode(frame, out string back), "解码应成功");
            Require(back == req, "往返 JSON 应逐字相同");
            Require(!YamaJsonRpcFraming.TryDecode(Encoding.UTF8.GetBytes("no header"), out _),
                    "非法帧头应拒绝");
        }

        public static void TestR14JsonRpcQuoteEscapesAndBadFrames()
        {
            string q = YamaJsonRpcFraming.Quote("a\"b\\c\nd");
            Require(q.StartsWith("\"", StringComparison.Ordinal)
                 && q.EndsWith("\"", StringComparison.Ordinal), "应加引号");
            Require(q.Contains("\\\"", StringComparison.Ordinal), "双引号应转义");
            Require(q.Contains("\\\\", StringComparison.Ordinal), "反斜杠应转义");
            Require(q.Contains("\\n", StringComparison.Ordinal), "换行应转义");
            string rpc = YamaJsonRpcFraming.JsonRpc("initialize", 3, "{}");
            Require(rpc.Contains("\"id\":3", StringComparison.Ordinal), "id 应写入");
            Require(rpc.Contains("\"method\":\"initialize\"", StringComparison.Ordinal),
                    "method 应写入");
            byte[] bad = YamaJsonRpcFraming.Encode("{\"a\":1}");
            bad[bad.Length - 1] = (byte)' ';
            string header = System.Text.Encoding.ASCII.GetString(
                bad, 0, Math.Min(30, bad.Length));
            Require(header.Contains("Content-Length: 7", StringComparison.Ordinal),
                    "帧头长度声明应正确");
            byte[] truncated = new byte[bad.Length - 3];
            System.Array.Copy(bad, truncated, truncated.Length);
            Require(!YamaJsonRpcFraming.TryDecode(truncated, out _),
                    "声明长度超过实际字节应拒绝");
        }

        public static void TestR14LspRequestsAndResponseParsing()
        {
            var lsp = new YamaLspClient();
            string init = lsp.Initialize("file:///yama");
            using (var d = JsonDocument.Parse(init))
            {
                var r = d.RootElement;
                Require(r.GetProperty("jsonrpc").GetString() == "2.0", "jsonrpc 版本");
                Require(r.GetProperty("method").GetString() == "initialize", "method");
                Require(r.GetProperty("params").GetProperty("rootUri").GetString()
                        == "file:///yama", "rootUri");
            }
            string opened = lsp.DidOpen("file:///a.cs", "csharp", 3, "class A { }");
            Require(opened.Contains("textDocument/didOpen", StringComparison.Ordinal), "didOpen");
            Require(opened.Contains("class A { }", StringComparison.Ordinal), "文本转义");
            string shut = lsp.Shutdown();
            Require(shut.Contains("shutdown", StringComparison.Ordinal), "shutdown");
            Require(lsp.LastId == 2, "id 应单调递增");

            string ok = "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"capabilities\":{}}}";
            Require(YamaLspClient.TryParseResponse(ok, out int id, out bool err, out string res),
                    "合法响应应解析");
            Require(id == 7 && !err && res.Contains("capabilities", StringComparison.Ordinal),
                    "响应字段");
            string bad = "{\"jsonrpc\":\"2.0\",\"id\":8,\"error\":{\"code\":-32601}}";
            Require(YamaLspClient.TryParseResponse(bad, out _, out bool err2, out _),
                    "错误响应也应解析");
            Require(err2, "错误标志");
        }

        public static void TestR14DapBreakpointsAndStackTrace()
        {
            var dap = new YamaDapClient();
            string bp = dap.SetBreakpoints("D:/yama/a.cs", new int[] { 12, 40 });
            using (var d = JsonDocument.Parse(bp))
            {
                var r = d.RootElement;
                Require(r.GetProperty("type").GetString() == "request", "type");
                Require(r.GetProperty("command").GetString() == "setBreakpoints", "command");
                Require(r.GetProperty("arguments").GetProperty("breakpoints").GetArrayLength() == 2,
                        "断点数");
            }
            string cont = dap.Continue(1);
            Require(cont.Contains("\"command\":\"continue\"", StringComparison.Ordinal), "continue");
            Require(dap.LastSeq == 2, "seq 应单调递增");

            string resp = "{\"seq\":9,\"type\":\"response\",\"command\":\"stackTrace\","
                + "\"body\":{\"stackFrames\":[{\"id\":0,\"name\":\"Main\",\"line\":42},"
                + "{\"id\":1,\"name\":\"Step\",\"line\":7}]}}";
            var frames = YamaDapClient.ParseStackTrace(resp);
            Require(frames.Count == 2, "应解析 2 帧");
            Require(frames[0].FrameId == 0 && frames[0].Name == "Main"
                 && frames[0].Line == 42, "首帧内容");
            Require(YamaDapClient.ParseStackTrace("{}").Count == 0, "无 body 应返回空");
        }

        public static void TestR14ExtensionManifestIsWellFormed()
        {
            string m = YamaExtensionManifest.Build("1.148.0");
            Require(YamaExtensionManifest.IsWellFormed(m), "清单应合法");
            using var d = JsonDocument.Parse(m);
            var r = d.RootElement;
            Require(r.GetProperty("engines").GetProperty("vscode").GetString() == "^1.148.0",
                    "engine 版本");
            var cmds = r.GetProperty("contributes").GetProperty("commands");
            int n = 0;
            foreach (var _ in cmds.EnumerateArray()) n++;
            Require(n == YamaExtensionManifest.Commands.Length, "命令数");
            Require(r.GetProperty("license").GetString() == "MIT", "license");
            Require(r.GetProperty("contributes").GetProperty("debuggers").GetArrayLength() == 1,
                    "调试器贡献点");
        }

        public static void TestR14BridgeCleanBuildAndCommandRouting()
        {
            var b = new YamaCodeOssBridge(Read("ide/codeoss/product.json"), "1.148.0");
            Require(b.IsCleanMitBuild, "桥接应判定为干净 MIT 构建");
            Require(b.Diagnostics.Count == 0, "不应有诊断");
            var routed = new HashSet<string>();
            foreach (string c in YamaExtensionManifest.Commands)
            {
                var x = b.Dispatch(c);
                Require(x.Handled && x.Intent.Length > 0 && x.Payload.Length > 0,
                        "命令应被路由: " + c);
                Require(routed.Add(x.Intent + "|" + x.Payload), "路由重复: " + c);
            }
            var unknown = b.Dispatch("nope");
            Require(!unknown.Handled, "未知命令应拒绝");
            Require(b.InitializeFrame("file:///yama") > 100,
                    "LSP initialize 帧应非空");
        }

        public static void TestR14SelfResearchBoundarySpdxCoversAllPackages()
        {
            string csproj = Read("YamaEngine/YamaEngine.csproj");
            string spdx = Read("docs/THIRD_PARTY_SPX.md");
            var names = new List<string>();
            const string marker = "<PackageReference Include=\"";
            int idx = 0;
            while ((idx = csproj.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
            {
                int s = idx + marker.Length;
                int e = csproj.IndexOf('"', s);
                names.Add(csproj.Substring(s, e - s));
                idx = e + 1;
            }
            Require(names.Count == 13, "应有 13 个 PackageReference，实际 " + names.Count);
            foreach (string n in names)
                Require(spdx.Contains(n, StringComparison.Ordinal),
                        "SPDX 清单未署名: " + n);
            Require(spdx.Contains("Apache-2.0", StringComparison.Ordinal)
                 && spdx.Contains("MIT", StringComparison.Ordinal), "许可标注");
            Require(Read("docs/SELF_RESEARCH_BOUNDARY.md").Contains("不借鉴",
                    StringComparison.Ordinal), "自研边界定义存在");
        }

        public static void TestR14NoForeignEngineCopyrightHeadersInSources()
        {
            // C# 侧同口径实现（Python 版见 scripts/audit_no_engine_copy.py）
            string[] forbidden = new string[] {
                "Copyright (c) 2007-2024 Juan Linietsky", "Godot Engine contributors",
                "Copyright Epic Games", "Copyright Unity Technologies",
                "Copyright NVIDIA PhysX", "#include \"godot/",
                "#include \"PhysX/", "namespace godot", "namespace physx"
            };
            int files = 0;
            foreach (string f in Directory.GetFiles(Path.Combine(R, "YamaEngine"), "*.cs",
                                                   SearchOption.AllDirectories))
            {
                files++;
                string head = "";
                using (var sr = new StreamReader(f))
                {
                    for (int i = 0; i < 40 && !sr.EndOfStream; i++)
                        head += sr.ReadLine() + "\n";
                }
                foreach (string bad in forbidden)
                    Require(!head.Contains(bad, StringComparison.OrdinalIgnoreCase),
                            "疑似其他引擎源码搬运: " + f + " / " + bad);
            }
            Require(files > 700, "应扫描到足够多自研源文件，实际 " + files);
        }

        public static void TestR14TuanIntegrationContractIsComplete()
        {
            using var doc = JsonDocument.Parse(Read("integrations/tuan_cli/contract.json"));
            var r = doc.RootElement;
            Require(r.GetProperty("codeoss_version").GetString() == "1.148.0", "版本");
            Require(r.GetProperty("integration_mode").GetString() == "yama-side-only",
                    "本轮只改 Yama 侧");
            Require(r.GetProperty("write_policy").GetString().Contains("TUAN.md",
                    StringComparison.Ordinal), "应说明 TUAN.md 授权边界");
            foreach (var f in r.GetProperty("files").EnumerateArray())
            {
                string rel = f.GetString() ?? "";
                Require(File.Exists(Path.Combine(R, "integrations/tuan_cli",
                        rel.Replace('/', Path.DirectorySeparatorChar))),
                        "适配层文件缺失: " + rel);
            }
            int skills = 0;
            foreach (var s in r.GetProperty("skills").EnumerateArray()) skills++;
            Require(skills == 5, "应有 5 个 skills");
            foreach (string s in new string[] { "yama-build", "yama-test",
                                               "yama-physics-gate", "yama-sota-audit",
                                               "yama-coupled-mass" })
            {
                string txt = Read("integrations/tuan_cli/skills/" + s + "/SKILL.md");
                // Windows runner 的 checkout 会把 .md 转成 CRLF（Git for Windows
                // 默认 core.autocrlf=true），本地 Linux 是 LF。两种都算合法 frontmatter。
                bool fm = txt.StartsWith("---\n", StringComparison.Ordinal)
                       || txt.StartsWith("---\r\n", StringComparison.Ordinal);
                Require(fm, "frontmatter: " + s);
                Require(txt.Contains("priority:", StringComparison.Ordinal), "priority: " + s);
                Require(txt.Contains("1. ", StringComparison.Ordinal)
                     && txt.Contains("2. ", StringComparison.Ordinal)
                     && txt.Contains("3. ", StringComparison.Ordinal),
                        "至少 3 步: " + s);
            }
            string hooks = Read("integrations/tuan_cli/hooks/yama_hooks.yaml");
            Require(hooks.Contains("PostToolUse", StringComparison.Ordinal)
                 && hooks.Contains("Stop", StringComparison.Ordinal), "Tuan hook 事件名");
            string mcp = Read("integrations/tuan_cli/mcp_server.py");
            Require(mcp.Contains("initialize", StringComparison.Ordinal)
                 && mcp.Contains("tools/list", StringComparison.Ordinal)
                 && mcp.Contains("tools/call", StringComparison.Ordinal), "MCP 协议最小集");
        }
    }
}
