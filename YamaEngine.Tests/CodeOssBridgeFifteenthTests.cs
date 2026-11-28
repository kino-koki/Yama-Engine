using System;
using System.Text;
using System.Text.Json;
using YamaEngine.Editor.CodeOss;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十五轮回归（6 项）：自研 Code - OSS 桥接协议面扩展
    /// （LSP didChange/completion/definition、DAP 异常断点/求值、新命令路由）。
    /// </summary>
    public static class CodeOssBridgeFifteenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("CODEOSS_R15_FAIL: " + msg);
        }

        public static void TestR15LspDidChangeAndFrameRoundTrip()
        {
            var lsp = new YamaLspClient();
            string msg = lsp.DidChange("file:///a.cs", 7, "class B { }");
            using (var d = JsonDocument.Parse(msg))
            {
                var r = d.RootElement;
                Require(r.GetProperty("method").GetString() == "textDocument/didChange",
                        "method 应为 didChange");
                var p = r.GetProperty("params");
                Require(p.GetProperty("textDocument").GetProperty("version").GetInt32() == 7,
                        "版本号");
                Require(p.GetProperty("contentChanges")[0].GetProperty("text").GetString()
                        == "class B { }", "全量文本");
            }
            byte[] frame = YamaJsonRpcFraming.Encode(msg);
            Require(YamaJsonRpcFraming.TryDecode(frame, out string back) && back == msg,
                    "didChange 帧应逐字往返");
            string header = Encoding.ASCII.GetString(frame, 0, Math.Min(40, frame.Length));
            Require(header.StartsWith("Content-Length: ", StringComparison.Ordinal),
                    "帧头应合规");
        }

        public static void TestR15LspCompletionAndDefinition()
        {
            var lsp = new YamaLspClient();
            string comp = lsp.Completion("file:///a.cs", 12, 4);
            using (var d = JsonDocument.Parse(comp))
            {
                var r = d.RootElement;
                Require(r.GetProperty("method").GetString() == "textDocument/completion",
                        "completion method");
                var pos = r.GetProperty("params").GetProperty("position");
                Require(pos.GetProperty("line").GetInt32() == 12
                     && pos.GetProperty("character").GetInt32() == 4, "补全位置");
                Require(r.GetProperty("id").GetInt32() == 1, "请求 id");
            }
            string def = lsp.Definition("file:///a.cs", 3, 9);
            using (var d = JsonDocument.Parse(def))
            {
                var r = d.RootElement;
                Require(r.GetProperty("method").GetString() == "textDocument/definition",
                        "definition method");
                var pos = r.GetProperty("params").GetProperty("position");
                Require(pos.GetProperty("line").GetInt32() == 3
                     && pos.GetProperty("character").GetInt32() == 9, "跳转位置");
                Require(r.GetProperty("id").GetInt32() == 2, "id 应单调递增");
            }
            Require(lsp.LastId == 2, "LastId 应跟踪最后一次请求");
        }

        public static void TestR15DapExceptionBreakpoints()
        {
            var dap = new YamaDapClient();
            string req = dap.SetExceptionBreakpoints(new string[] { "all", "userUnhandled" });
            using (var d = JsonDocument.Parse(req))
            {
                var r = d.RootElement;
                Require(r.GetProperty("type").GetString() == "request", "DAP type");
                Require(r.GetProperty("command").GetString() == "setExceptionBreakpoints",
                        "command");
                var f = r.GetProperty("arguments").GetProperty("filters");
                Require(f.GetArrayLength() == 2, "过滤器数量");
                Require(f[0].GetString() == "all" && f[1].GetString() == "userUnhandled",
                        "过滤器内容");
                Require(r.GetProperty("seq").GetInt32() == 1, "seq");
            }
        }

        public static void TestR15DapEvaluateAndSeqMonotonic()
        {
            var dap = new YamaDapClient();
            dap.SetExceptionBreakpoints(new string[] { "all" });
            string ev = dap.Evaluate(3, "YrPhysicsPipeline.Tick");
            using (var d = JsonDocument.Parse(ev))
            {
                var r = d.RootElement;
                Require(r.GetProperty("command").GetString() == "evaluate", "command");
                var a = r.GetProperty("arguments");
                Require(a.GetProperty("expression").GetString() == "YrPhysicsPipeline.Tick",
                        "表达式");
                Require(a.GetProperty("frameId").GetInt32() == 3, "frameId");
                Require(a.GetProperty("context").GetString() == "watch", "context");
                Require(r.GetProperty("seq").GetInt32() == 2, "seq 应单调递增");
            }
            Require(dap.LastSeq == 2, "LastSeq");
        }

        public static void TestR15ManifestHasCoupledMassCommand()
        {
            string m = YamaExtensionManifest.Build("1.148.0");
            Require(YamaExtensionManifest.IsWellFormed(m), "清单应合法");
            Require(YamaExtensionManifest.Commands.Length == 10, "第二十轮应有 10 个命令");
            Require(Array.IndexOf(YamaExtensionManifest.Commands, "yama.coupledMass") >= 0,
                    "应含 coupledMass 命令");
            foreach (string extra in new[] { "yama.codeossMinimal", "yama.invoke",
                                             "yama.capabilities" })
                Require(Array.IndexOf(YamaExtensionManifest.Commands, extra) >= 0,
                        "第二十轮新增命令缺失: " + extra);
            using var d = JsonDocument.Parse(m);
            var cmds = d.RootElement.GetProperty("contributes").GetProperty("commands");
            bool found = false;
            foreach (var c in cmds.EnumerateArray())
                if (c.GetProperty("command").GetString() == "yama.coupledMass") found = true;
            Require(found, "JSON 里应有 coupledMass 贡献点");
        }

        public static void TestR15BridgeRoutesEveryCommandIncludingNewOne()
        {
            string product = "{\"nameShort\":\"Yama Code - OSS\",\"nameLong\":\"x\","
                + "\"licenseName\":\"MIT\",\"version\":\"1.148.0\",\"commit\":\"abc\","
                + "\"enableTelemetry\":false,\"telemetryEndpoint\":\"\","
                + "\"extensionsGallery\":{},\"yama\":{\"bridge\":\"YamaCodeOssBridge\"}}";
            var b = new YamaCodeOssBridge(product, "1.148.0");
            Require(b.IsCleanMitBuild, "桥接应判定干净 MIT 构建");
            foreach (string c in YamaExtensionManifest.Commands)
            {
                var x = b.Dispatch(c);
                Require(x.Handled && x.Intent.Length > 0 && x.Payload.Length > 0,
                        "命令应路由: " + c);
            }
            var cm = b.Dispatch("yama.coupledMass");
            Require(cm.Intent == "audit"
                 && cm.Payload.Contains("UseCoupledMassMatrix", StringComparison.Ordinal)
                 && cm.Payload.Contains("LastCoupledTrace", StringComparison.Ordinal),
                    "新命令应指向耦合开关与 trace");
            Require(!b.Dispatch("unknown").Handled, "未知命令应拒绝");
        }
    }
}
