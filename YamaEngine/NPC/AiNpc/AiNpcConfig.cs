using System;
using System.IO;
using System.Text.Json;

namespace YamaEngine.NPC.AiNpc;

/// <summary>
/// AI NPC 正式接入配置：Kimodo 动作生成服务 + MOSS-Live 全双工交互服务。
/// 默认指向本机 sidecar（integrations/ 目录），可通过 JSON 覆盖。
/// </summary>
public sealed class AiNpcConfig
{
    public KimodoConfig Kimodo { get; set; } = new();
    public MossLiveConfig MossLive { get; set; } = new();
    public LipSyncConfig LipSync { get; set; } = new();
    /// <summary>Unity5 Mecanim 深度：足部 IK / 头部注视 / 上半身手势层。</summary>
    public AiNpcRigConfig Rig { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AiNpcConfig CreateDefault() => new();

    public static AiNpcConfig Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("配置路径不能为空", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"AI NPC 配置不存在: {path}", path);

        var config = JsonSerializer.Deserialize<AiNpcConfig>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidOperationException($"AI NPC 配置解析失败: {path}");
        config.Validate();
        return config;
    }

    public void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>校验并规范化端点/数值，尽早暴露配置错误。</summary>
    public void Validate()
    {
        Kimodo ??= new KimodoConfig();
        MossLive ??= new MossLiveConfig();
        LipSync ??= new LipSyncConfig();

        Kimodo.Endpoint = Normalize(Kimodo.Endpoint, "http://127.0.0.1:8088");
        Kimodo.Fps = Math.Clamp(Kimodo.Fps, 1f, 120f);
        Kimodo.TimeoutSeconds = Math.Max(1, Kimodo.TimeoutSeconds);
        Kimodo.ScaleToEngine = MathF.Max(0.0001f, Kimodo.ScaleToEngine);

        MossLive.Endpoint = Normalize(MossLive.Endpoint, "ws://127.0.0.1:8091/v1/duplex");
        MossLive.SampleRate = Math.Clamp(MossLive.SampleRate, 8000, 48000);
        MossLive.Channels = Math.Clamp(MossLive.Channels, 1, 2);

        LipSync.MaxJawAngleDegrees = Math.Clamp(LipSync.MaxJawAngleDegrees, 0f, 60f);

        Rig ??= new AiNpcRigConfig();
        Rig.FootIkWeight = Math.Clamp(Rig.FootIkWeight, 0f, 1f);
        Rig.LookAtWeight = Math.Clamp(Rig.LookAtWeight, 0f, 1f);
        Rig.GestureLayerWeight = Math.Clamp(Rig.GestureLayerWeight, 0f, 1f);
        Rig.LookAtMaxAngleDegrees = Math.Clamp(Rig.LookAtMaxAngleDegrees, 5f, 120f);
        Rig.FootOffset = MathF.Max(0f, Rig.FootOffset);
    }

    private static string Normalize(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? fallback : trimmed;
    }
}

public sealed class KimodoConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Kimodo sidecar 基地址（由 integrations/kimodo_service 提供）。</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:8088";
    /// <summary>Kimodo 模型变体，例如 kimodo-soma-rp / kimodo-g1。</summary>
    public string Model { get; set; } = "kimodo-soma-rp";
    /// <summary>输出骨架，SOMA 用 somaskel77。</summary>
    public string Skeleton { get; set; } = "somaskel77";
    public float Fps { get; set; } = 30f;
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Kimodo 米制坐标到引擎单位的缩放（引擎若以 1 单位 = 1 米则保持 1）。</summary>
    public float ScaleToEngine { get; set; } = 1f;
    /// <summary>是否把根位移写入 Hips 轨道（关闭后只播放原地动作）。</summary>
    public bool UseRootMotion { get; set; } = true;
    public int Seed { get; set; } = -1;
    public bool SaveGeneratedClips { get; set; }
    public string ClipOutputDirectory { get; set; } = "Generated/Animations";
}

public sealed class MossLiveConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>MOSS-Live 全双工 WebSocket 端点（官方端点或本地 gateway）。</summary>
    public string Endpoint { get; set; } = "ws://127.0.0.1:8091/v1/duplex";
    public string Voice { get; set; } = "default";
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int ConnectTimeoutMs { get; set; } = 8000;
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectDelayMs { get; set; } = 3000;
    public int MaxQueuedAudioChunks { get; set; } = 64;
}

public sealed class LipSyncConfig
{
    /// <summary>驱动 viseme 的下颌骨名；留空则由调用方接管口型。</summary>
    public string JawBone { get; set; } = "";
    public float MaxJawAngleDegrees { get; set; } = 12f;
}

/// <summary>
/// Unity5 Mecanim 级别的姿态后处理配置。
/// 与 Unity 的 Animator.IKGoal / OnAnimatorIK / AvatarMask 层权重一一对应。
/// </summary>
public sealed class AiNpcRigConfig
{
    // ── 足部 IK（foot planting，对应 Unity foot IK + Subtle Foot IK）──
    public bool EnableFootIk { get; set; } = true;
    /// <summary>全局足部 IK 权重 [0,1]。</summary>
    public float FootIkWeight { get; set; } = 1f;
    /// <summary>脚底离地偏移（米），避免脚面插入地面。</summary>
    public float FootOffset { get; set; } = 0.03f;
    /// <summary>左脚骨骼名（脚踝）。</summary>
    public string LeftFootBone { get; set; } = "LeftFoot";
    /// <summary>右脚骨骼名（脚踝）。</summary>
    public string RightFootBone { get; set; } = "RightFoot";
    /// <summary>是否启用髋部下沉补偿（两条腿都伸不到地面时）。</summary>
    public bool EnableHipAdjustment { get; set; } = true;

    // ── 头部注视 IK（look-at，对应 Unity LookAt + Head Turn）──
    public bool EnableLookAt { get; set; } = true;
    /// <summary>全局注视权重 [0,1]。</summary>
    public float LookAtWeight { get; set; } = 1f;
    /// <summary>头骨名（注视链末端）。</summary>
    public string HeadBone { get; set; } = "Head";
    /// <summary>注视链中间骨（颈/胸），从下到上。</summary>
    public string[] LookAtChainBones { get; set; } = Array.Empty<string>();
    /// <summary>单骨最大注视旋转角（度）。</summary>
    public float LookAtMaxAngleDegrees { get; set; } = 55f;
    /// <summary>注视目标超出该角度（水平 FOV 半角，度）时减弱权重。</summary>
    public float LookAtFovDegrees { get; set; } = 120f;

    // ── 上半身手势层（AvatarMask 分层叠加）──
    /// <summary>全局手势层权重 [0,1]；0 时说话不做手势。</summary>
    public float GestureLayerWeight { get; set; } = 0.6f;
    /// <summary>骨盆骨名，用于构建上半身 AvatarMask。</summary>
    public string PelvisBone { get; set; } = "Hips";
}
