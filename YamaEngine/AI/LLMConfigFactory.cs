// ════════════════════════════════════════════════════════════════════
// LLMConfigFactory.cs — 真实 LLM 端点的一行式配置
//
// 让"LLM 引导行为驱动的 AI NPC"能直接接上真实可用的 LLM 端点：
//   - OpenAI 兼容端点（DeepSeek / 本地 vLLM / OpenAI / 自建网关）
//   - 本地 Ollama（无需 key）
//   - 阿里通义千问（DashScope）
//   - 百度文心（千帆，api_key+secret_key 换 access_token）
//   - 环境变量注入（YAMA_LLM_ENDPOINT / YAMA_LLM_API_KEY / YAMA_LLM_MODEL / YAMA_LLM_PLATFORM）
//
// 用法：
//   var cfg = LLMConfigFactory.DeepSeek("sk-xxx");          // 一行接 DeepSeek
//   var llm  = LLMClient.FromConfig(cfg);                   // 就绪客户端(已注册5平台provider)
//   var reply = await npc.Dialogue.RespondAsync(npc,"玩家",input, llm);
//   var intent = await npcBrain.DecideAsync(context, llm);   // LLM 引导行为
// ════════════════════════════════════════════════════════════════════

using System;

namespace YamaEngine.AI;

/// <summary>真实 LLM 端点配置工厂（一行式 / 环境变量 / 就绪客户端）。</summary>
public static class LLMConfigFactory
{
    /// <summary>OpenAI 兼容端点（DeepSeek / 本地 vLLM / OpenAI / 自建网关等）。</summary>
    public static LLMConfig OpenAiCompatible(string endpoint, string apiKey, string model = "gpt-4o-mini")
        => new(LLMPlatform.OpenAI, apiKey, model) { Endpoint = endpoint };

    /// <summary>DeepSeek（OpenAI 兼容，官方端点）。</summary>
    public static LLMConfig DeepSeek(string apiKey, string model = "deepseek-chat")
        => OpenAiCompatible("https://api.deepseek.com", apiKey, model);

    /// <summary>本地 Ollama（免 key，模型如 llama3 / qwen2.5）。</summary>
    public static LLMConfig LocalOllama(string model = "llama3",
        string endpoint = "http://localhost:11434/api")
        => new(LLMPlatform.Ollama, "", model) { Endpoint = endpoint };

    /// <summary>阿里通义千问（DashScope）。</summary>
    public static LLMConfig Qwen(string apiKey, string model = "qwen-plus")
        => new(LLMPlatform.Qwen, apiKey, model);

    /// <summary>百度文心（千帆：api_key + secret_key 换 access_token）。</summary>
    public static LLMConfig Ernie(string apiKey, string secretKey, string model = "ernie-4.0-8k")
        => new(LLMPlatform.Ernie, apiKey, model) { SecretKey = secretKey };

    /// <summary>
    /// 从环境变量构建配置（供无硬编码 key 的部署）：
    ///   YAMA_LLM_ENDPOINT 必需；YAMA_LLM_API_KEY / YAMA_LLM_MODEL / YAMA_LLM_PLATFORM 可选。
    /// 未设置端点时返回 null（表示未启用真实 LLM）。
    /// </summary>
    public static LLMConfig? FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable("YAMA_LLM_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var apiKey = Environment.GetEnvironmentVariable("YAMA_LLM_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("YAMA_LLM_MODEL") ?? "gpt-4o-mini";
        var platformStr = Environment.GetEnvironmentVariable("YAMA_LLM_PLATFORM");
        var platform = Enum.TryParse<LLMPlatform>(platformStr, true, out var p)
            ? p : LLMPlatform.OpenAI;
        return new LLMConfig(platform, apiKey, model) { Endpoint = endpoint };
    }

    /// <summary>构建就绪的 LLM 客户端（已注册全部真实平台 Provider）。</summary>
    public static LLMClient CreateClient(LLMConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        return new LLMClient(config);
    }
}
