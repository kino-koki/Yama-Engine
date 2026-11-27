using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using YamaEngine.Reality;
namespace YamaEngine.Editor.CodeOss
{
    /// <summary>
    /// 自研 LSP 3.17 客户端（消息层）。构造 initialize / didOpen / shutdown 请求，
    /// 解析响应。传输层由调用方接入 stdio；此处不启动进程、不引用第三方 SDK。
    /// </summary>
    public sealed class YamaLspClient
    {
        private int _nextId = 1;
        public int LastId { get; private set; }

        public string Initialize(string rootUri)
        {
            string p = "{\"processId\":null,\"rootUri\":"
                + YamaJsonRpcFraming.Quote(rootUri)
                + ",\"capabilities\":{\"workspace\":{\"configuration\":true},"
                + "\"textDocument\":{\"synchronization\":{\"dynamicRegistration\":false}}}}";
            LastId = _nextId++;
            return YamaJsonRpcFraming.JsonRpc("initialize", LastId, p);
        }

        public string DidOpen(string uri, string languageId, int version, string text)
        {
            string p = "{\"textDocument\":{\"uri\":" + YamaJsonRpcFraming.Quote(uri)
                + ",\"languageId\":" + YamaJsonRpcFraming.Quote(languageId)
                + ",\"version\":" + version.ToString()
                + ",\"text\":" + YamaJsonRpcFraming.Quote(text) + "}}";
            return "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":" + p + "}";
        }

        public string Shutdown()
        {
            LastId = _nextId++;
            return YamaJsonRpcFraming.JsonRpc("shutdown", LastId, "null");
        }

        public byte[] FrameInitialize(string rootUri)
            => YamaJsonRpcFraming.Encode(Initialize(rootUri));

        /// <summary>解析 JSON-RPC 响应：返回 (id, hasError, resultJson)。</summary>
        public static bool TryParseResponse(string json, out int id,
                                            out bool hasError, out string resultJson)
        {
            id = 0; hasError = false; resultJson = string.Empty;
            if (json == null) return false;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("jsonrpc", out var v)
                || v.GetString() != "2.0") return false;
            if (r.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                id = idEl.GetInt32();
            hasError = r.TryGetProperty("error", out _);
            if (r.TryGetProperty("result", out var res)) resultJson = res.GetRawText();
            return true;
        }
    }
}
