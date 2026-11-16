using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YamaEngine.NPC.AiNpc;

/// <summary>
/// MOSS-Live 全双工会话协议（引擎侧定义，gateway 负责转译到官方端点）。
/// 传输为 WebSocket 文本帧，每帧一个 JSON 对象。
/// </summary>
public static class MossLiveProtocol
{
    public const string Version = "1.0";
}

/// <summary>服务端 → 引擎事件。未知字段忽略，未知 type 由上层记录后丢弃。</summary>
public sealed class MossServerEvent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    // state / emotion
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("intensity")] public float? Intensity { get; set; }

    // text
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("turn_id")] public int? TurnId { get; set; }

    // audio
    [JsonPropertyName("pcm16")] public string? Pcm16 { get; set; }
    [JsonPropertyName("sample_rate")] public int? SampleRate { get; set; }
    [JsonPropertyName("channels")] public int? Channels { get; set; }
    [JsonPropertyName("seq")] public int? Seq { get; set; }

    // viseme
    [JsonPropertyName("viseme")] public string? Viseme { get; set; }
    [JsonPropertyName("weight")] public float? Weight { get; set; }
    [JsonPropertyName("duration_ms")] public int? DurationMs { get; set; }

    // action（融合点：语义动作 → Kimodo 动作生成）
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("duration_seconds")] public float? DurationSeconds { get; set; }
    [JsonPropertyName("wait_for_completion")] public bool? WaitForCompletion { get; set; }
    [JsonPropertyName("constraints")] public List<KimodoConstraint>? Constraints { get; set; }
    [JsonPropertyName("metadata")] public Dictionary<string, string>? Metadata { get; set; }

    // error
    [JsonPropertyName("message")] public string? Message { get; set; }

    public bool IsSpeechEvent => Type is "speech_started" or "speech_ended";
}

/// <summary>引擎 → 服务端消息。</summary>
public sealed class MossClientMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("npc_id")] public string? NpcId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("persona")] public string? Persona { get; set; }
    [JsonPropertyName("voice")] public string? Voice { get; set; }
    [JsonPropertyName("sample_rate")] public int? SampleRate { get; set; }
    [JsonPropertyName("channels")] public int? Channels { get; set; }
    [JsonPropertyName("capabilities")] public string[]? Capabilities { get; set; }
    [JsonPropertyName("pcm16")] public string? Pcm16 { get; set; }
    [JsonPropertyName("seq")] public int? Seq { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("duration_seconds")] public float? DurationSeconds { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
}

/// <summary>会话建立信息。</summary>
public sealed class MossSessionInfo
{
    public string NpcId { get; set; } = "";
    public string Name { get; set; } = "NPC";
    public string Persona { get; set; } = "";
    public string Voice { get; set; } = "default";
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;

    public static readonly string[] DefaultCapabilities =
    {
        "audio.in", "text.in", "text.out", "audio.out", "viseme", "emotion", "action"
    };
}

public enum MossConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}
