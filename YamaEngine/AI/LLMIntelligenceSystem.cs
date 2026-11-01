// ============================================================================
//  YamaEngine.AI.LLMIntelligenceSystem.cs
//  yama引擎 -- LLM 智能接入系统 (LLM Intelligence System)
//  对齐 OpenAI API / Anthropic Messages API / Ollama / 通义千问 / 百度文心
// ----------------------------------------------------------------------------
//  本文件实现：
//    1. LLMConfig / LLMPlatform  - 多平台配置 (OpenAI/Anthropic/Ollama/Qwen/Ernie)
//    2. LLMMessage / LLMConversation - 对话历史 + token 计数 + 截断策略
//    3. TokenEstimator           - 近似 token 计数 (按平台启发式)
//    4. SSEParser                - 流式响应逐行解析 (Server-Sent Events)
//    5. ILLMProvider / 各平台 Provider - 异步对话/补全/嵌入/流式
//    6. LLMClient                - 路由 + 速率限制 + 重试 + 超时 + 本地回退
//    7. LLMToBehaviorTree        - LLM 行为描述 -> 行为树节点 (复用 BehaviorTreeV2)
//    8. NPCDialogueSystem        - 基于人格/记忆/情感的动态 NPC 对话
//    9. QuestGenerator           - LLM 生成任务文本 + 目标
//   10. GameMasterAI             - 观察玩家行为 -> 调整难度 -> 生成事件
//   11. LocalRuleInference       - 本地规则推理回退 (无网络时)
// ----------------------------------------------------------------------------
//  全部使用 HttpClient 异步请求，真实 SSE 解析，零 NotImplementedException。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、平台与配置
    // ========================================================================

    /// <summary>支持的 LLM 平台。</summary>
    public enum LLMPlatform
    {
        OpenAI,
        Anthropic,
        Ollama,
        Qwen,
        Ernie,
        Local
    }

    /// <summary>消息角色。</summary>
    public enum LLMRole
    {
        System,
        User,
        Assistant,
        Tool
    }

    /// <summary>LLM 平台连接配置。</summary>
    public sealed class LLMConfig
    {
        public LLMPlatform Platform = LLMPlatform.OpenAI;
        public string ApiKey = "";
        public string SecretKey = "";        // 文心等需要 secret 换 access_token
        public string Endpoint = "";          // 留空则用平台默认
        public string Model = "gpt-4o-mini";
        public float Temperature = 0.7f;
        public int MaxTokens = 1024;
        public float TopP = 1f;
        public int RequestTimeoutMs = 30000;
        public int MaxRetries = 3;
        public float RetryBaseDelayMs = 500f;
        public int RateLimitPerMinute = 60;
        public bool EnableStreaming = false;
        public bool FallbackToLocal = true;

        public LLMConfig() { }
        public LLMConfig(LLMPlatform platform, string apiKey, string model) { Platform = platform; ApiKey = apiKey; Model = model; }
    }

    // ========================================================================
    //  二、消息与对话上下文
    // ========================================================================

    /// <summary>单条对话消息。</summary>
    public sealed class LLMMessage
    {
        public LLMRole Role;
        public string Content = "";
        public string? Name;          // 部分平台支持 name 字段
        public int EstimatedTokens;

        public LLMMessage() { }
        public LLMMessage(LLMRole role, string content) { Role = role; Content = content; }

        public string RoleName => Role switch
        {
            LLMRole.System => "system",
            LLMRole.User => "user",
            LLMRole.Assistant => "assistant",
            LLMRole.Tool => "tool",
            _ => "user"
        };
    }

    /// <summary>LLM 用量统计。</summary>
    public sealed class LLMUsage
    {
        public int PromptTokens;
        public int CompletionTokens;
        public int TotalTokens => PromptTokens + CompletionTokens;
    }

    /// <summary>LLM 响应。</summary>
    public sealed class LLMResponse
    {
        public string Content = "";
        public string FinishReason = "stop";
        public LLMUsage Usage = new();
        public LLMPlatform Platform;
        public bool Success = true;
        public string? Error;
        public float ElapsedMs;

        public override string ToString() => Success ? Content : $"[LLM Error] {Error}";
    }

    /// <summary>
    /// 对话上下文：维护历史消息、token 计数与截断策略。
    /// 截断策略：当 token 超过上限时，保留系统消息 + 最近 N 条，丢弃中间最旧的。
    /// </summary>
    public sealed class LLMConversation
    {
        public string Id = Guid.NewGuid().ToString("N");
        public List<LLMMessage> Messages = new();
        public int MaxContextTokens = 8000;
        public int SystemReservedTokens = 0;

        /// <summary>追加一条消息并刷新 token 估计。</summary>
        public void Append(LLMMessage msg)
        {
            msg.EstimatedTokens = TokenEstimator.Estimate(msg.Content, TokenEstimator.ModelFamily.Auto);
            Messages.Add(msg);
            Truncate();
        }

        /// <summary>系统消息（始终保留在最前）。</summary>
        public void SetSystem(string systemPrompt)
        {
            Messages.RemoveAll(m => m.Role == LLMRole.System);
            if (!string.IsNullOrEmpty(systemPrompt))
                Messages.Insert(0, new LLMMessage(LLMRole.System, systemPrompt));
            SystemReservedTokens = TokenEstimator.Estimate(systemPrompt ?? "", TokenEstimator.ModelFamily.Auto);
        }

        public int TotalTokens
        {
            get { int s = 0; foreach (var m in Messages) s += m.EstimatedTokens + 4; return s; }
        }

        /// <summary>按 token 上限截断：保留所有 System + 末尾若干条，直到不超限。</summary>
        public void Truncate()
        {
            if (Messages.Count == 0) return;
            int budget = MaxContextTokens - SystemReservedTokens;
            if (budget < 200) budget = 200;

            // 从尾部向前累计，超过预算则丢弃中间
            int acc = 0;
            int keepFromIndex = Messages.Count;
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].Role == LLMRole.System) continue; // 系统消息不计入预算
                acc += Messages[i].EstimatedTokens + 4;
                if (acc > budget)
                {
                    keepFromIndex = i + 1;
                    break;
                }
                keepFromIndex = i;
            }

            // 保留 System(开头) + keepFromIndex 之后
            var kept = new List<LLMMessage>();
            foreach (var m in Messages)
                if (m.Role == LLMRole.System) kept.Add(m);
            for (int i = keepFromIndex; i < Messages.Count; i++)
                if (Messages[i].Role != LLMRole.System) kept.Add(Messages[i]);

            Messages = kept;
        }

        public void Clear() { Messages.Clear(); SystemReservedTokens = 0; }
    }

    // ========================================================================
    //  三、Token 估计器 (近似)
    // ========================================================================

    /// <summary>
    /// 近似 token 计数。真实分词器需要 BPE 词表，这里用启发式：
    /// 英文 ~4 字符/token，中文 ~1.5 字符/token，混合按比例估算。
    /// </summary>
    public static class TokenEstimator
    {
        public enum ModelFamily { Auto, OpenAI, Anthropic, Chinese }

        public static int Estimate(string text, ModelFamily family)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int ascii = 0, cjk = 0, other = 0;
            foreach (var ch in text)
            {
                if (ch < 128) ascii++;
                else if (ch >= 0x4E00 && ch <= 0x9FFF) cjk++;
                else other++;
            }
            // 英文 4 字符/token, 中文 1.5 字符/token, 其他 2 字符/token
            int tokens = (ascii + 3) / 4 + (int)Math.Ceiling(cjk / 1.5) + (other + 1) / 2;
            // 平台修正
            switch (family)
            {
                case ModelFamily.Anthropic: tokens = (int)(tokens * 1.05f); break;
                case ModelFamily.Chinese: tokens = (int)(tokens * 0.95f); break;
            }
            return Math.Max(1, tokens);
        }

        public static ModelFamily FamilyFor(LLMPlatform p) => p switch
        {
            LLMPlatform.Anthropic => ModelFamily.Anthropic,
            LLMPlatform.Qwen or LLMPlatform.Ernie => ModelFamily.Chinese,
            _ => ModelFamily.OpenAI
        };
    }

    // ========================================================================
    //  四、SSE 流式解析
    // ========================================================================

    /// <summary>单个 SSE 事件。</summary>
    public readonly struct SSEEvent
    {
        public readonly string Event;   // event: 字段
        public readonly string Data;    // data: 字段
        public SSEEvent(string evt, string data) { Event = evt; Data = data; }
    }

    /// <summary>
    /// SSE 逐行解析器。从 StreamReader 读取 "data: ..." 行，按空行分帧。
    /// </summary>
    public sealed class SSEParser
    {
        /// <summary>从流中异步读取所有 SSE 事件，对每个事件回调。</summary>
        public static async Task ReadAsync(Stream stream, Action<SSEEvent> onEvent, CancellationToken ct = default)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var dataBuf = new StringBuilder();
            string? evtType = null;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0)
                {
                    // 空行 = 一帧结束
                    if (dataBuf.Length > 0)
                    {
                        onEvent(new SSEEvent(evtType ?? "message", dataBuf.ToString()));
                        dataBuf.Clear();
                        evtType = null;
                    }
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (dataBuf.Length > 0) dataBuf.Append('\n');
                    dataBuf.Append(line.Substring(5).TrimStart());
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    evtType = line.Substring(6).Trim();
                }
                // 忽略 id:/retry:/注释行
            }
            // 流结束时刷新剩余
            if (dataBuf.Length > 0)
                onEvent(new SSEEvent(evtType ?? "message", dataBuf.ToString()));
        }
    }

    // ========================================================================
    //  五、Provider 抽象与各平台实现
    // ========================================================================

    /// <summary>LLM 提供者接口。</summary>
    public interface ILLMProvider
    {
        LLMPlatform Platform { get; }
        Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default);
        Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default);
        Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default);
        Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default);
    }

    /// <summary>Provider 基类：共享 HttpClient 与 JSON 工具。</summary>
    public abstract class LLMProviderBase : ILLMProvider
    {
        public abstract LLMPlatform Platform { get; }
        protected static readonly HttpClient Http = new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };

        protected static string J(Dictionary<string, object> o) => JsonSerializer.Serialize(o);
        protected static JsonDocument Parse(string s) => JsonDocument.Parse(s);

        protected static string DefaultEndpoint(LLMPlatform p) => p switch
        {
            LLMPlatform.OpenAI => "https://api.openai.com/v1",
            LLMPlatform.Anthropic => "https://api.anthropic.com/v1",
            LLMPlatform.Ollama => "http://localhost:11434/api",
            LLMPlatform.Qwen => "https://dashscope.aliyuncs.com/api/v1",
            LLMPlatform.Ernie => "https://aip.baidubce.com/rpc/2.0/ai_custom/v1",
            _ => ""
        };

        public abstract Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default);
        public abstract Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default);
        public abstract Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default);
        public abstract Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default);

        protected static List<object> BuildMessages(LLMConversation conv)
        {
            var list = new List<object>();
            foreach (var m in conv.Messages)
            {
                list.Add(new { role = m.RoleName, content = m.Content });
            }
            return list;
        }

        protected static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, LLMConfig cfg, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.RequestTimeoutMs);
            return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>OpenAI 兼容平台 (OpenAI / 兼容端点)。</summary>
    public sealed class OpenAIProvider : LLMProviderBase
    {
        public override LLMPlatform Platform => LLMPlatform.OpenAI;

        public override async Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.OpenAI) : cfg.Endpoint;
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["messages"] = BuildMessages(conv),
                ["temperature"] = cfg.Temperature,
                ["max_tokens"] = cfg.MaxTokens,
                ["top_p"] = cfg.TopP,
                ["stream"] = false
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            return await ReadChatResponseAsync(req, cfg, LLMPlatform.OpenAI, ct).ConfigureAwait(false);
        }

        public override async Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default)
        {
            var conv = new LLMConversation();
            conv.Append(new LLMMessage(LLMRole.User, prompt));
            return await ChatAsync(conv, cfg, ct).ConfigureAwait(false);
        }

        public override async Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.OpenAI) : cfg.Endpoint;
            var body = J(new Dictionary<string, object> { ["model"] = "text-embedding-3-small", ["input"] = text });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/embeddings")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = Parse(json);
            var list = new List<float>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var emb = data[0].GetProperty("embedding");
                foreach (var v in emb.EnumerateArray()) list.Add(v.GetSingle());
            }
            return list.ToArray();
        }

        public override async Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.OpenAI) : cfg.Endpoint;
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["messages"] = BuildMessages(conv),
                ["temperature"] = cfg.Temperature,
                ["max_tokens"] = cfg.MaxTokens,
                ["stream"] = true
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await SSEParser.ReadAsync(stream, evt =>
            {
                if (evt.Data == "[DONE]") return;
                try
                {
                    using var d = Parse(evt.Data);
                    if (d.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                    {
                        var delta = ch[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            onToken(c.GetString() ?? "");
                    }
                }
                catch { /* 忽略单帧解析错误 */ }
            }, ct).ConfigureAwait(false);
        }

        internal static async Task<LLMResponse> ReadChatResponseAsync(HttpRequestMessage req, LLMConfig cfg, LLMPlatform plat, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = new LLMResponse { Platform = plat };
            try
            {
                using var httpResp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
                var json = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds;
                if (!httpResp.IsSuccessStatusCode)
                {
                    resp.Success = false;
                    resp.Error = $"HTTP {(int)httpResp.StatusCode}: {Truncate(json, 300)}";
                    return resp;
                }
                using var doc = Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var msg = choices[0].GetProperty("message").GetProperty("content");
                    resp.Content = msg.ValueKind == JsonValueKind.String ? msg.GetString() ?? "" : msg.GetRawText();
                    resp.FinishReason = choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() ?? "stop" : "stop";
                }
                if (root.TryGetProperty("usage", out var usage))
                {
                    resp.Usage.PromptTokens = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                    resp.Usage.CompletionTokens = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
                }
            }
            catch (Exception ex)
            {
                sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds;
                resp.Success = false; resp.Error = ex.Message;
            }
            return resp;
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
    }

    /// <summary>Anthropic Messages API。</summary>
    public sealed class AnthropicProvider : LLMProviderBase
    {
        public override LLMPlatform Platform => LLMPlatform.Anthropic;

        public override async Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Anthropic) : cfg.Endpoint;
            // Anthropic: system 单独传，messages 只含 user/assistant
            string system = "";
            var msgList = new List<object>();
            foreach (var m in conv.Messages)
            {
                if (m.Role == LLMRole.System) system += (system.Length > 0 ? "\n" : "") + m.Content;
                else msgList.Add(new { role = m.RoleName, content = m.Content });
            }
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["max_tokens"] = cfg.MaxTokens,
                ["temperature"] = cfg.Temperature,
                ["system"] = system,
                ["messages"] = msgList
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/messages")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key", cfg.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = new LLMResponse { Platform = LLMPlatform.Anthropic };
            try
            {
                using var httpResp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
                var json = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds;
                if (!httpResp.IsSuccessStatusCode)
                {
                    resp.Success = false; resp.Error = $"HTTP {(int)httpResp.StatusCode}: {json.Substring(0, Math.Min(json.Length, 300))}";
                    return resp;
                }
                using var doc = Parse(json);
                var content = doc.RootElement.GetProperty("content");
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                resp.Content = sb.ToString();
                if (doc.RootElement.TryGetProperty("stop_reason", out var sr)) resp.FinishReason = sr.GetString() ?? "stop";
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    resp.Usage.PromptTokens = usage.TryGetProperty("input_tokens", out var p) ? p.GetInt32() : 0;
                    resp.Usage.CompletionTokens = usage.TryGetProperty("output_tokens", out var c) ? c.GetInt32() : 0;
                }
            }
            catch (Exception ex) { sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds; resp.Success = false; resp.Error = ex.Message; }
            return resp;
        }

        public override Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default)
        {
            var conv = new LLMConversation(); conv.Append(new LLMMessage(LLMRole.User, prompt));
            return ChatAsync(conv, cfg, ct);
        }

        public override async Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default)
        {
            // Anthropic 暂无官方嵌入端点，回退到本地哈希向量保证可用
            return LocalEmbed(text);
        }

        public override async Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Anthropic) : cfg.Endpoint;
            string system = "";
            var msgList = new List<object>();
            foreach (var m in conv.Messages)
            {
                if (m.Role == LLMRole.System) system += (system.Length > 0 ? "\n" : "") + m.Content;
                else msgList.Add(new { role = m.RoleName, content = m.Content });
            }
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model, ["max_tokens"] = cfg.MaxTokens, ["temperature"] = cfg.Temperature,
                ["system"] = system, ["messages"] = msgList, ["stream"] = true
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/messages")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key", cfg.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await SSEParser.ReadAsync(stream, evt =>
            {
                try
                {
                    using var d = Parse(evt.Data);
                    if (d.RootElement.TryGetProperty("type", out var t) && t.GetString() == "content_block_delta")
                    {
                        var delta = d.RootElement.GetProperty("delta");
                        if (delta.TryGetProperty("text", out var txt)) onToken(txt.GetString() ?? "");
                    }
                }
                catch { }
            }, ct).ConfigureAwait(false);
        }

        internal static float[] LocalEmbed(string text)
        {
            const int Dim = 256;
            var v = new float[Dim];
            int i = 0;
            foreach (var ch in text) { v[i % Dim] += (ch % 17) * 0.01f; i++; }
            float norm = 0; foreach (var x in v) norm += x * x;
            norm = MathF.Sqrt(norm) + 1e-6f;
            for (int k = 0; k < Dim; k++) v[k] /= norm;
            return v;
        }
    }

    /// <summary>Ollama 本地推理平台。</summary>
    public sealed class OllamaProvider : LLMProviderBase
    {
        public override LLMPlatform Platform => LLMPlatform.Ollama;

        public override async Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Ollama) : cfg.Endpoint;
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["messages"] = BuildMessages(conv),
                ["stream"] = false,
                ["options"] = new Dictionary<string, object> { ["temperature"] = cfg.Temperature, ["num_predict"] = cfg.MaxTokens }
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = new LLMResponse { Platform = LLMPlatform.Ollama };
            try
            {
                using var httpResp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
                var json = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds;
                if (!httpResp.IsSuccessStatusCode) { resp.Success = false; resp.Error = $"HTTP {(int)httpResp.StatusCode}"; return resp; }
                using var doc = Parse(json);
                if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    resp.Content = c.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("done_reason", out var dr)) resp.FinishReason = dr.GetString() ?? "stop";
            }
            catch (Exception ex) { sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds; resp.Success = false; resp.Error = ex.Message; }
            return resp;
        }

        public override Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default)
        {
            var conv = new LLMConversation(); conv.Append(new LLMMessage(LLMRole.User, prompt));
            return ChatAsync(conv, cfg, ct);
        }

        public override async Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Ollama) : cfg.Endpoint;
            var body = J(new Dictionary<string, object> { ["model"] = string.IsNullOrEmpty(cfg.Model) ? "nomic-embed-text" : cfg.Model, ["input"] = text });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/embeddings")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = Parse(json);
            var list = new List<float>();
            if (doc.RootElement.TryGetProperty("embedding", out var emb))
                foreach (var v in emb.EnumerateArray()) list.Add(v.GetSingle());
            return list.ToArray();
        }

        public override async Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Ollama) : cfg.Endpoint;
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model, ["messages"] = BuildMessages(conv), ["stream"] = true
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // Ollama 流式为 NDJSON (每行一个 JSON)
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;
                try
                {
                    using var d = Parse(line);
                    if (d.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                        onToken(c.GetString() ?? "");
                }
                catch { }
            }
        }
    }

    /// <summary>通义千问 (DashScope)。</summary>
    public sealed class QwenProvider : LLMProviderBase
    {
        public override LLMPlatform Platform => LLMPlatform.Qwen;

        public override async Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Qwen) : cfg.Endpoint;
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["input"] = new { messages = BuildMessages(conv) },
                ["parameters"] = new { temperature = cfg.Temperature, max_tokens = cfg.MaxTokens, top_p = cfg.TopP, result_format = "message" }
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/services/aigc/text-generation/generation")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = new LLMResponse { Platform = LLMPlatform.Qwen };
            try
            {
                using var httpResp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
                var json = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds;
                if (!httpResp.IsSuccessStatusCode) { resp.Success = false; resp.Error = $"HTTP {(int)httpResp.StatusCode}"; return resp; }
                using var doc = Parse(json);
                if (doc.RootElement.TryGetProperty("output", out var output))
                {
                    if (output.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var m = choices[0].GetProperty("message");
                        resp.Content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    }
                    else if (output.TryGetProperty("text", out var t))
                    {
                        resp.Content = t.GetString() ?? "";
                    }
                    if (output.TryGetProperty("finish_reason", out var fr)) resp.FinishReason = fr.GetString() ?? "stop";
                }
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    resp.Usage.PromptTokens = usage.TryGetProperty("input_tokens", out var p) ? p.GetInt32() : 0;
                    resp.Usage.CompletionTokens = usage.TryGetProperty("output_tokens", out var c) ? c.GetInt32() : 0;
                }
            }
            catch (Exception ex) { sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds; resp.Success = false; resp.Error = ex.Message; }
            return resp;
        }

        public override Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default)
        {
            var conv = new LLMConversation(); conv.Append(new LLMMessage(LLMRole.User, prompt));
            return ChatAsync(conv, cfg, ct);
        }

        public override async Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default)
        {
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Qwen) : cfg.Endpoint;
            var body = J(new Dictionary<string, object> { ["model"] = "text-embedding-v2", ["input"] = new { texts = new[] { text } } });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/services/embeddings/text-embedding/text-embedding")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = Parse(json);
            var list = new List<float>();
            if (doc.RootElement.TryGetProperty("output", out var output) && output.TryGetProperty("embeddings", out var embs) && embs.GetArrayLength() > 0)
                foreach (var v in embs[0].GetProperty("embedding").EnumerateArray()) list.Add(v.GetSingle());
            return list.ToArray();
        }

        public override async Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default)
        {
            // DashScope 流式需 X-DashScope-SSE: enable
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Qwen) : cfg.Endpoint;
            var body = J(new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["input"] = new { messages = BuildMessages(conv) },
                ["parameters"] = new { temperature = cfg.Temperature, max_tokens = cfg.MaxTokens, result_format = "message", incremental_output = true }
            });
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/services/aigc/text-generation/generation")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            req.Headers.Add("X-DashScope-SSE", "enable");
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await SSEParser.ReadAsync(stream, evt =>
            {
                try
                {
                    using var d = Parse(evt.Data);
                    if (d.RootElement.TryGetProperty("output", out var o))
                    {
                        if (o.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                        {
                            var c = ch[0].GetProperty("message").GetProperty("content");
                            if (c.ValueKind == JsonValueKind.String) onToken(c.GetString() ?? "");
                        }
                        else if (o.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            onToken(t.GetString() ?? "");
                    }
                }
                catch { }
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>百度文心 (ERNIE Bot)。</summary>
    public sealed class ErnieProvider : LLMProviderBase
    {
        public override LLMPlatform Platform => LLMPlatform.Ernie;

        private static async Task<string> GetAccessTokenAsync(LLMConfig cfg, CancellationToken ct)
        {
            // 文心需要用 APIKey + SecretKey 换 access_token
            var url = $"https://aip.baidubce.com/oauth/2.0/token?grant_type=client_credentials&client_id={cfg.ApiKey}&client_secret={cfg.SecretKey}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString() ?? "";
        }

        public override async Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(cfg, ct).ConfigureAwait(false);
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Ernie) : cfg.Endpoint;
            var msgList = new List<object>();
            foreach (var m in conv.Messages)
            {
                if (m.Role == LLMRole.System) continue; // 文心 system 通过 system 字段传
                msgList.Add(new { role = m.RoleName, content = m.Content });
            }
            string system = "";
            foreach (var m in conv.Messages) if (m.Role == LLMRole.System) system += m.Content + "\n";
            var payload = new Dictionary<string, object>
            {
                ["messages"] = msgList,
                ["temperature"] = cfg.Temperature,
                ["max_output_tokens"] = cfg.MaxTokens,
                ["top_p"] = cfg.TopP
            };
            if (!string.IsNullOrEmpty(system)) payload["system"] = system;
            var body = J(payload);
            var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/wenxinworkshop/chat/completions?access_token={token}")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = new LLMResponse { Platform = LLMPlatform.Ernie };
            try
            {
                using var httpResp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
                var json = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds;
                if (!httpResp.IsSuccessStatusCode) { resp.Success = false; resp.Error = $"HTTP {(int)httpResp.StatusCode}"; return resp; }
                using var doc = Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var r)) resp.Content = r.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("is_end", out var ie) && ie.GetBoolean()) resp.FinishReason = "stop";
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    resp.Usage.PromptTokens = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                    resp.Usage.CompletionTokens = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
                }
            }
            catch (Exception ex) { sw.Stop(); resp.ElapsedMs = sw.ElapsedMilliseconds; resp.Success = false; resp.Error = ex.Message; }
            return resp;
        }

        public override Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default)
        {
            var conv = new LLMConversation(); conv.Append(new LLMMessage(LLMRole.User, prompt));
            return ChatAsync(conv, cfg, ct);
        }

        public override async Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(cfg, ct).ConfigureAwait(false);
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Ernie) : cfg.Endpoint;
            var body = J(new Dictionary<string, object> { ["input"] = new[] { text } });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/embeddings/embedding-v1?access_token={token}")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = Parse(json);
            var list = new List<float>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                foreach (var v in data[0].GetProperty("embedding").EnumerateArray()) list.Add(v.GetSingle());
            return list.ToArray();
        }

        public override async Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(cfg, ct).ConfigureAwait(false);
            var endpoint = string.IsNullOrEmpty(cfg.Endpoint) ? DefaultEndpoint(LLMPlatform.Ernie) : cfg.Endpoint;
            var msgList = new List<object>();
            foreach (var m in conv.Messages)
                if (m.Role != LLMRole.System) msgList.Add(new { role = m.RoleName, content = m.Content });
            var body = J(new Dictionary<string, object>
            {
                ["messages"] = msgList, ["temperature"] = cfg.Temperature, ["max_output_tokens"] = cfg.MaxTokens, ["stream"] = true
            });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/wenxinworkshop/chat/completions?access_token={token}")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await SendAsync(req, cfg, ct).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await SSEParser.ReadAsync(stream, evt =>
            {
                try
                {
                    using var d = Parse(evt.Data);
                    if (d.RootElement.TryGetProperty("result", out var r)) onToken(r.GetString() ?? "");
                }
                catch { }
            }, ct).ConfigureAwait(false);
        }
    }

    // ========================================================================
    //  六、LLM 客户端 (路由 + 速率限制 + 重试 + 超时 + 本地回退)
    // ========================================================================

    /// <summary>LLM 客户端：管理多个平台 Provider，提供速率限制、重试与本地回退。</summary>
    public sealed class LLMClient
    {
        private readonly Dictionary<LLMPlatform, ILLMProvider> _providers = new();
        private LLMConfig _config;
        private readonly Queue<DateTime> _callTimestamps = new();
        private readonly LocalRuleInference _fallback = new();

        public LLMConfig Config => _config;
        public event Action<string, Exception>? OnError;

        public LLMClient(LLMConfig config)
        {
            _config = config;
            Register(new OpenAIProvider());
            Register(new AnthropicProvider());
            Register(new OllamaProvider());
            Register(new QwenProvider());
            Register(new ErnieProvider());
        }

        public void Register(ILLMProvider provider) => _providers[provider.Platform] = provider;
        public void SetConfig(LLMConfig config) => _config = config;

        private ILLMProvider GetProvider() => _providers.TryGetValue(_config.Platform, out var p) ? p : _providers[LLMPlatform.OpenAI];

        /// <summary>速率限制：每分钟不超过 RateLimitPerMinute 次调用。</summary>
        private bool TryAcquireRate()
        {
            var now = DateTime.UtcNow;
            while (_callTimestamps.Count > 0 && (now - _callTimestamps.Peek()).TotalSeconds > 60)
                _callTimestamps.Dequeue();
            if (_callTimestamps.Count >= _config.RateLimitPerMinute) return false;
            _callTimestamps.Enqueue(now);
            return true;
        }

        /// <summary>带重试的异步执行。指数退避。</summary>
        private async Task<LLMResponse> WithRetryAsync(Func<Task<LLMResponse>> action)
        {
            int attempts = 0;
            while (true)
            {
                attempts++;
                try
                {
                    if (!TryAcquireRate())
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                        continue;
                    }
                    var resp = await action().ConfigureAwait(false);
                    if (resp.Success) return resp;
                    // 4xx 类错误不重试 (除 429)
                    if (resp.Error != null && resp.Error.Contains("HTTP 4") && !resp.Error.Contains("429"))
                        return resp;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"LLM attempt {attempts} failed: {ex.Message}", ex);
                    if (attempts > _config.MaxRetries)
                    {
                        if (_config.FallbackToLocal)
                            return new LLMResponse { Success = true, Platform = LLMPlatform.Local, Content = _fallback.Respond("fallback"), FinishReason = "local_fallback" };
                        return new LLMResponse { Success = false, Error = ex.Message, Platform = _config.Platform };
                    }
                }
                if (attempts > _config.MaxRetries)
                {
                    if (_config.FallbackToLocal)
                        return new LLMResponse { Success = true, Platform = LLMPlatform.Local, Content = _fallback.Respond("fallback"), FinishReason = "local_fallback" };
                    break;
                }
                float delay = _config.RetryBaseDelayMs * MathF.Pow(2, attempts - 1);
                await Task.Delay((int)delay).ConfigureAwait(false);
            }
            return new LLMResponse { Success = false, Error = "exceeded retries", Platform = _config.Platform };
        }

        public Task<LLMResponse> ChatAsync(LLMConversation conv, CancellationToken ct = default)
            => WithRetryAsync(() => GetProvider().ChatAsync(conv, _config, ct));

        public Task<LLMResponse> CompleteAsync(string prompt, CancellationToken ct = default)
            => WithRetryAsync(() => GetProvider().CompleteAsync(prompt, _config, ct));

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            try { return await GetProvider().EmbedAsync(text, _config, ct).ConfigureAwait(false); }
            catch { return AnthropicProvider.LocalEmbed(text); }
        }

        public Task StreamChatAsync(LLMConversation conv, Action<string> onToken, CancellationToken ct = default)
            => GetProvider().StreamChatAsync(conv, _config, onToken, ct);
    }

    // ========================================================================
    //  七、LLM -> 行为树 转换 (复用现有 BehaviorTreeV2 / BTBuilder)
    // ========================================================================

    /// <summary>LLM 生成的行为节点描述 (可序列化)。</summary>
    public sealed class LLMBehaviorNode
    {
        public string Kind = "Task";   // Sequence/Selector/Parallel/Condition/Repeater/Inverter/Task/Wait/MoveTo
        public string Name = "";
        public string Text = "";       // Task 的描述文本 / Condition 的判断文本
        public List<LLMBehaviorNode> Children = new();
    }

    /// <summary>
    /// 将 LLM 生成的行为描述 (JSON) 转换为行为树节点，集成到现有 BehaviorTreeV2 体系。
    /// taskFactory 把任务文本映射为具体的 BTTask 执行逻辑。
    /// </summary>
    public static class LLMToBehaviorTree
    {
        /// <summary>从 JSON 文本解析行为描述。</summary>
        public static LLMBehaviorNode? ParseSpec(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return ParseNode(doc.RootElement);
            }
            catch { return null; }
        }

        private static LLMBehaviorNode ParseNode(JsonElement el)
        {
            var node = new LLMBehaviorNode
            {
                Kind = el.TryGetProperty("kind", out var k) ? k.GetString() ?? "Task" : "Task",
                Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Text = el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""
            };
            if (el.TryGetProperty("children", out var children))
                foreach (var c in children.EnumerateArray()) node.Children.Add(ParseNode(c));
            return node;
        }

        /// <summary>把行为描述构建进现有 BehaviorTreeV2。返回构建好的树。</summary>
        public static BehaviorTreeV2 Build(LLMBehaviorNode spec, Func<string, Func<BTContext, BTNodeStatus>> taskFactory, string treeName = "LLMTree")
        {
            var tree = new BehaviorTreeV2(treeName);
            var root = BuildNode(spec, taskFactory);
            if (root != null) tree.SetRoot(root);
            return tree;
        }

        private static BTNodeV2? BuildNode(LLMBehaviorNode spec, Func<string, Func<BTContext, BTNodeStatus>> taskFactory)
        {
            switch (spec.Kind)
            {
                case "Sequence":
                    var seq = new BTSequence();
                    foreach (var c in spec.Children) { var n = BuildNode(c, taskFactory); if (n != null) seq.Children.Add(n); }
                    seq.Name = string.IsNullOrEmpty(spec.Name) ? "Sequence" : spec.Name;
                    return seq;
                case "Selector":
                    var sel = new BTSelector();
                    foreach (var c in spec.Children) { var n = BuildNode(c, taskFactory); if (n != null) sel.Children.Add(n); }
                    sel.Name = string.IsNullOrEmpty(spec.Name) ? "Selector" : spec.Name;
                    return sel;
                case "Parallel":
                    var par = new BTParallel();
                    foreach (var c in spec.Children) { var n = BuildNode(c, taskFactory); if (n != null) par.Children.Add(n); }
                    par.Name = string.IsNullOrEmpty(spec.Name) ? "Parallel" : spec.Name;
                    return par;
                case "Repeater":
                    {
                        var r = new BTRepeater();
                        if (spec.Children.Count > 0) r.SetChild(BuildNode(spec.Children[0], taskFactory) ?? new BTNoOp());
                        r.Name = string.IsNullOrEmpty(spec.Name) ? "Repeater" : spec.Name;
                        return r;
                    }
                case "Inverter":
                    {
                        var inv = new BTInverter();
                        if (spec.Children.Count > 0) inv.SetChild(BuildNode(spec.Children[0], taskFactory) ?? new BTNoOp());
                        inv.Name = string.IsNullOrEmpty(spec.Name) ? "Inverter" : spec.Name;
                        return inv;
                    }
                case "Condition":
                    {
                        // 条件文本作为运行时断言: 通过 taskFactory 生成条件函数
                        var condFn = taskFactory("cond:" + spec.Text);
                        var cond = new BTCondition(ctx => condFn(ctx) == BTNodeStatus.Success);
                        if (spec.Children.Count > 0) cond.SetChild(BuildNode(spec.Children[0], taskFactory) ?? new BTNoOp());
                        cond.Name = string.IsNullOrEmpty(spec.Name) ? "Condition" : spec.Name;
                        return cond;
                    }
                case "Wait":
                    return float.TryParse(spec.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? new BTWait(sec) { Name = spec.Name } : new BTWait(1f);
                case "MoveTo":
                    return new BTMoveTo(ctx => ctx.Blackboard.GetVector3(spec.Text.Length > 0 ? spec.Text : "MoveTarget")) { Name = string.IsNullOrEmpty(spec.Name) ? "MoveTo" : spec.Name };
                case "Task":
                default:
                    return new BTTask(string.IsNullOrEmpty(spec.Name) ? "Task" : spec.Name, taskFactory(spec.Text));
            }
        }

        /// <summary>提示词模板：让 LLM 输出可解析的行为描述 JSON。</summary>
        public static string BuildBehaviorPrompt(string scenario, string goal)
        {
            return "你是游戏 AI 行为设计助手。请根据场景与目标，输出一个 JSON 行为树。\n" +
                   "JSON 格式: {\"kind\":\"Sequence|Selector|Parallel|Condition|Repeater|Inverter|Task|Wait|MoveTo\",\"name\":\"节点名\",\"text\":\"说明/参数\",\"children\":[...]}\n" +
                   "Condition 的 text 为判断条件描述, Task 的 text 为动作描述, Wait 的 text 为秒数, MoveTo 的 text 为黑板键名。\n" +
                   $"场景: {scenario}\n目标: {goal}\n只输出 JSON，不要其它内容。";
        }
    }

    // ========================================================================
    //  八、NPC 对话系统 (人格 / 记忆 / 情感状态)
    // ========================================================================

    /// <summary>NPC 人格设定。</summary>
    public sealed class NPCPersona
    {
        public string Name = "NPC";
        public string Personality = "友善、健谈";
        public string Background = "村庄商人";
        public string SpeakingStyle = "口语化";
        public string[] KnowledgeTopics = Array.Empty<string>();
    }

    /// <summary>NPC 对单实体的记忆条目。</summary>
    public sealed class NPCMemoryEntry
    {
        public string Subject;       // 玩家ID/实体名
        public string Summary;       // 上次交互摘要
        public int InteractionCount;
        public float LastInteractionTime;
        public float Affinity = 0.5f; // 好感度 0~1
    }

    /// <summary>NPC 情感状态 (PAD 模型简化)。</summary>
    public struct NPCEmotion
    {
        public float Pleasure; // -1~1
        public float Arousal;  // -1~1
        public float Dominance;// -1~1

        public string MoodLabel
        {
            get
            {
                if (Pleasure > 0.3f && Arousal > 0.3f) return "兴奋";
                if (Pleasure > 0.3f && Arousal <= 0.3f) return "满足";
                if (Pleasure <= -0.3f && Arousal > 0.3f) return "愤怒";
                if (Pleasure <= -0.3f && Arousal <= -0.3f) return "悲伤";
                return "平静";
            }
        }

        public void Apply(float dP, float dA, float dD)
        {
            Pleasure = Math.Clamp(Pleasure + dP, -1f, 1f);
            Arousal = Math.Clamp(Arousal + dA, -1f, 1f);
            Dominance = Math.Clamp(Dominance + dD, -1f, 1f);
        }
    }

    /// <summary>基于 LLM 的动态 NPC 对话系统。</summary>
    public sealed class NPCDialogueSystem
    {
        private readonly LLMClient _client;
        public NPCPersona Persona = new();
        public NPCEmotion Emotion;
        public Dictionary<string, NPCMemoryEntry> Memory = new();
        private readonly LLMConversation _conv = new();

        public NPCDialogueSystem(LLMClient client) { _client = client; _conv.MaxContextTokens = 4000; }

        public void InitPersona(NPCPersona persona)
        {
            Persona = persona;
            var sys = $"你扮演 NPC「{persona.Name}」。性格: {persona.Personality}。背景: {persona.Background}。说话风格: {persona.SpeakingStyle}。" +
                      $"当前情绪: {Emotion.MoodLabel}。始终以角色身份回应，保持风格一致。";
            _conv.SetSystem(sys);
        }

        /// <summary>对玩家发言。</summary>
        public async Task<string> RespondAsync(string playerId, string playerSpeech, float currentTime)
        {
            // 更新记忆
            if (!Memory.TryGetValue(playerId, out var mem)) { mem = new NPCMemoryEntry { Subject = playerId, Affinity = 0.5f }; Memory[playerId] = mem; }
            mem.InteractionCount++;
            mem.LastInteractionTime = currentTime;

            var ctxMsg = $"[系统提示] 玩家「{playerId}」对你说: \"{playerSpeech}\"。你与该玩家互动 {mem.InteractionCount} 次，好感度 {mem.Affinity:F2}，当前情绪 {Emotion.MoodLabel}。";
            _conv.Append(new LLMMessage(LLMRole.User, ctxMsg));

            var resp = await _client.ChatAsync(_conv).ConfigureAwait(false);
            var reply = resp.Success ? resp.Content : "...(沉默)";
            _conv.Append(new LLMMessage(LLMRole.Assistant, reply));

            // 情感漂移: 简单关键词触发
            if (playerSpeech.Contains("谢谢") || playerSpeech.Contains("你好")) Emotion.Apply(0.1f, 0.05f, 0.05f);
            if (playerSpeech.Contains("滚") || playerSpeech.Contains("笨")) Emotion.Apply(-0.2f, 0.2f, -0.1f);
            mem.Summary = playerSpeech;

            return reply;
        }

        /// <summary>更新记忆好感度。</summary>
        public void AdjustAffinity(string playerId, float delta)
        {
            if (Memory.TryGetValue(playerId, out var mem)) mem.Affinity = Math.Clamp(mem.Affinity + delta, 0f, 1f);
        }
    }

    // ========================================================================
    //  九、任务生成器 (LLM 生成任务文本 + 目标)
    // ========================================================================

    public sealed class QuestObjective
    {
        public string Description = "";
        public string Type = "kill";   // kill/collect/reach/talk/protect
        public int Required = 1;
        public string TargetTag = "";
    }

    public sealed class QuestSpec
    {
        public string Title = "";
        public string Description = "";
        public string Giver = "";
        public int RewardGold;
        public List<QuestObjective> Objectives = new();
    }

    /// <summary>LLM 驱动的任务生成器。</summary>
    public sealed class QuestGenerator
    {
        private readonly LLMClient _client;
        private readonly LocalRuleInference _ruleFallback = new();

        public QuestGenerator(LLMClient client) { _client = client; }

        /// <summary>根据上下文生成任务。LLM 失败时回退到规则模板。</summary>
        public async Task<QuestSpec> GenerateAsync(string theme, int difficulty, string region)
        {
            var prompt = "你是游戏任务设计师。输出一个任务的 JSON: " +
                         "{\"title\":\"\",\"description\":\"\",\"giver\":\"\",\"rewardGold\":数字,\"objectives\":[{\"description\":\"\",\"type\":\"kill|collect|reach|talk|protect\",\"required\":数字,\"targetTag\":\"\"}]}" +
                         $"\n主题: {theme}\n难度(1-5): {difficulty}\n区域: {region}\n只输出 JSON。";
            var resp = await _client.CompleteAsync(prompt).ConfigureAwait(false);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Content))
                return RuleGenerate(theme, difficulty, region);
            return Parse(resp.Content) ?? RuleGenerate(theme, difficulty, region);
        }

        public static QuestSpec? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var q = new QuestSpec
                {
                    Title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Description = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Giver = r.TryGetProperty("giver", out var g) ? g.GetString() ?? "" : "",
                    RewardGold = r.TryGetProperty("rewardGold", out var rg) ? rg.GetInt32() : 100 * 1
                };
                if (r.TryGetProperty("objectives", out var obs))
                    foreach (var o in obs.EnumerateArray())
                    {
                        q.Objectives.Add(new QuestObjective
                        {
                            Description = o.TryGetProperty("description", out var od) ? od.GetString() ?? "" : "",
                            Type = o.TryGetProperty("type", out var ot) ? ot.GetString() ?? "kill" : "kill",
                            Required = o.TryGetProperty("required", out var ore) ? ore.GetInt32() : 1,
                            TargetTag = o.TryGetProperty("targetTag", out var ott) ? ott.GetString() ?? "" : ""
                        });
                    }
                return q;
            }
            catch { return null; }
        }

        /// <summary>规则回退模板。</summary>
        public QuestSpec RuleGenerate(string theme, int difficulty, string region)
        {
            var q = new QuestSpec();
            string[] verbs = { "剿灭", "收集", "护送", "调查", "夺回" };
            string[] foes = { "哥布林", "亡灵", "盗匪", "野兽", "巨龙" };
            int idx = (difficulty - 1) % foes.Length;
            q.Title = $"{verbs[idx % verbs.Length]}任务: {foes[idx]}之患";
            q.Description = $"在{region}，{foes[idx]}威胁着当地居民。请前去处理。难度: {difficulty}/5。";
            q.Giver = "村长";
            q.RewardGold = 50 * difficulty;
            q.Objectives.Add(new QuestObjective { Description = $"击败 {difficulty} 只{foes[idx]}", Type = "kill", Required = difficulty, TargetTag = foes[idx] });
            if (difficulty >= 3) q.Objectives.Add(new QuestObjective { Description = "收集战利品", Type = "collect", Required = difficulty, TargetTag = "loot" });
            return q;
        }
    }

    // ========================================================================
    //  十、GameMasterAI (观察玩家 -> 调整难度 -> 生成事件)
    // ========================================================================

    /// <summary>玩家行为快照。</summary>
    public sealed class PlayerBehaviorSnapshot
    {
        public float TimeAlive;
        public int Kills;
        public int Deaths;
        public float DamageDealt;
        public float DamageTaken;
        public float AvgReactionMs = 500f;
        public float ExplorationRatio; // 0~1
    }

    /// <summary>游戏事件。</summary>
    public sealed class GameEvent
    {
        public string Name;
        public string Description;
        public float Intensity; // 0~1
    }

    /// <summary>GameMaster AI: 动态难度调整 (DDA) + 事件生成。</summary>
    public sealed class GameMasterAI
    {
        private readonly LLMClient _client;
        public float CurrentDifficulty = 0.5f; // 0~1
        public float TargetDifficulty = 0.5f;
        public float AdaptRate = 0.15f;
        private readonly Queue<PlayerBehaviorSnapshot> _history = new();
        public int HistoryWindow = 10;

        public event Action<GameEvent>? OnEventGenerated;

        public GameMasterAI(LLMClient client) { _client = client; }

        /// <summary>提交玩家行为快照，更新难度。</summary>
        public void Observe(PlayerBehaviorSnapshot snap)
        {
            _history.Enqueue(snap);
            while (_history.Count > HistoryWindow) _history.Dequeue();

            // 性能指标: 击杀/死亡比 + 伤害交换比 + 存活时间
            float kd = snap.Deaths == 0 ? snap.Kills : (float)snap.Kills / snap.Deaths;
            float dmgRatio = snap.DamageTaken == 0 ? 1f : snap.DamageDealt / snap.DamageTaken;
            float perf = Math.Clamp((kd / 3f) * 0.4f + (dmgRatio / 2f) * 0.4f + (snap.TimeAlive / 120f) * 0.2f, 0f, 1f);

            // 玩家表现好 -> 提高难度; 表现差 -> 降低难度
            TargetDifficulty = Math.Clamp(perf, 0.1f, 1f);
            CurrentDifficulty += (TargetDifficulty - CurrentDifficulty) * AdaptRate;
        }

        /// <summary>基于当前难度生成游戏事件。LLM 可选增强叙事。</summary>
        public async Task<GameEvent> GenerateEventAsync(string context)
        {
            // 规则层先决定事件类型
            string evtName; string desc; float intensity = CurrentDifficulty;
            if (CurrentDifficulty > 0.75f) { evtName = "精英突袭"; desc = "强大的敌人增援抵达战场"; }
            else if (CurrentDifficulty < 0.25f) { evtName = "补给空投"; desc = "友军投下补给箱"; }
            else { evtName = "环境变化"; desc = "天气与地形发生改变"; }

            var prompt = $"你是游戏叙事 AI。当前难度 {CurrentDifficulty:F2}，玩家处境: {context}。为事件「{evtName}」生成一句不超过 40 字的生动描述。只输出描述文本。";
            var resp = await _client.CompleteAsync(prompt).ConfigureAwait(false);
            if (resp.Success && !string.IsNullOrWhiteSpace(resp.Content)) desc = resp.Content.Trim();

            var evt = new GameEvent { Name = evtName, Description = desc, Intensity = intensity };
            OnEventGenerated?.Invoke(evt);
            return evt;
        }

        /// <summary>输出当前难度对应的敌人强化系数。</summary>
        public float EnemyStatMultiplier => 0.6f + CurrentDifficulty * 0.8f; // 0.6x ~ 1.4x
    }

    // ========================================================================
    //  十一、本地规则推理回退 (无网络 / API 失败时)
    // ========================================================================

    /// <summary>
    /// 本地规则推理: 无网络时的确定性回退。基于关键词匹配的简单意图响应。
    /// </summary>
    public sealed class LocalRuleInference
    {
        private readonly Dictionary<string, string> _rules = new()
        {
            ["你好"] = "你好，旅人。有什么我能帮忙的吗？",
            ["帮助"] = "我可以提供指引、任务和补给。",
            ["任务"] = "附近似乎有些麻烦需要处理。",
            ["再见"] = "愿你的旅途平安。",
            ["fallback"] = "……(我暂时无法理解，但我会记住这些。)"
        };

        public string Respond(string input)
        {
            if (string.IsNullOrEmpty(input)) return _rules["fallback"];
            foreach (var kv in _rules)
                if (input.Contains(kv.Key)) return kv.Value;
            // 无匹配: 拼接上下文片段
            return _rules["fallback"];
        }

        public void AddRule(string keyword, string response) => _rules[keyword] = response;
    }
}
