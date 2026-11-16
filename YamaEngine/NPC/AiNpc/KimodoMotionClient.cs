using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.NPC.AiNpc;

/// <summary>动作生成源抽象，便于替换 sidecar / 本地推理 / 测试桩。</summary>
public interface IKimodoMotionSource
{
    Task<KimodoHealthResponse> HealthAsync(CancellationToken ct = default);
    Task<KimodoMotionResponse> GenerateAsync(KimodoGenerateRequest request, CancellationToken ct = default);
}

public sealed class KimodoIntegrationException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public KimodoIntegrationException(string message, int? statusCode = null, string? body = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }
}

/// <summary>
/// Kimodo sidecar HTTP 客户端。协议见 integrations/kimodo_service/README.md：
///   POST /v1/motion/generate  {prompt, duration_seconds, fps, model, skeleton, constraints...}
///   GET  /v1/health
/// </summary>
public sealed class KimodoMotionClient : IKimodoMotionSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly KimodoConfig _config;

    public KimodoMotionClient(KimodoConfig config, HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _ownsHttp = httpClient == null;
        _http = httpClient ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _config.TimeoutSeconds));
    }

    public string BaseEndpoint => (_config.Endpoint ?? "http://127.0.0.1:8088").TrimEnd('/');

    public async Task<KimodoHealthResponse> HealthAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{BaseEndpoint}/v1/health", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "health").ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<KimodoHealthResponse>(
            KimodoJson.Options, ct).ConfigureAwait(false);
        return payload ?? throw new KimodoIntegrationException("Kimodo health 返回空响应");
    }

    public async Task<KimodoMotionResponse> GenerateAsync(KimodoGenerateRequest request, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Kimodo 动作提示词不能为空", nameof(request));

        request.Fps = request.Fps <= 0f ? _config.Fps : request.Fps;
        request.Model = string.IsNullOrWhiteSpace(request.Model) ? _config.Model : request.Model;
        request.Skeleton = string.IsNullOrWhiteSpace(request.Skeleton) ? _config.Skeleton : request.Skeleton;
        request.Seed = request.Seed == -1 ? _config.Seed : request.Seed;

        var body = JsonSerializer.Serialize(request, KimodoJson.Options);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{BaseEndpoint}/v1/motion/generate", content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "generate").ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<KimodoMotionResponse>(
            KimodoJson.Options, ct).ConfigureAwait(false);
        if (payload == null) throw new KimodoIntegrationException("Kimodo 返回空动作响应");
        if (!payload.IsUsable)
            throw new KimodoIntegrationException("Kimodo 返回动作不含可用帧或骨架定义");
        return payload;
    }

    /// <summary>便捷入口：文本 + 可选约束 → 动作。</summary>
    public Task<KimodoMotionResponse> GenerateAsync(
        string prompt,
        float durationSeconds,
        IEnumerable<KimodoConstraint>? constraints = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var request = new KimodoGenerateRequest
        {
            Prompt = prompt,
            DurationSeconds = Math.Clamp(durationSeconds, 0.2f, 30f),
            Fps = _config.Fps,
            Model = _config.Model,
            Skeleton = _config.Skeleton,
            Seed = _config.Seed,
            Constraints = constraints == null ? new List<KimodoConstraint>() : new List<KimodoConstraint>(constraints)
        };
        if (metadata != null)
            foreach (var kv in metadata) request.Metadata[kv.Key] = kv.Value;
        return GenerateAsync(request, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        string body = "";
        try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { /* 保留状态码 */ }
        if (body.Length > 512) body = body[..512] + "...";
        throw new KimodoIntegrationException(
            $"Kimodo {operation} 失败: HTTP {response.StatusCode}", (int)response.StatusCode, body);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
