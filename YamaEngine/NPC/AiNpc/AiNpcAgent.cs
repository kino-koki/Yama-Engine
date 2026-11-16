using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Animation;
using YamaEngine.Assets;
using Skeleton = YamaEngine.Animation.Skeleton;

namespace YamaEngine.NPC.AiNpc;

public enum AiNpcState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
    Acting,
    Error
}

public sealed class AiNpcAudioChunk
{
    public required byte[] Pcm16 { get; init; }
    public int SampleRate { get; init; } = 16000;
    public int Channels { get; init; } = 1;
    public int Sequence { get; init; }
    public double ReceiveTimeSeconds { get; init; }
}

/// <summary>
/// MOSS-Live 对话/感知 + Kimodo 动作生成 的融合体。
///
/// 融合闭环：
///   玩家语音 → MOSS-Live（全双工，可打断）→ 文本/情绪/viseme
///        ↘ action 语义事件 → Kimodo 生成骨骼动作 → AnimationRuntime 播放 + 根位移
///        ↖ 动作完成 action_result 回传 → MOSS-Live 继续对话
///   NPC 的对话状态、动作状态、口型由同一状态机统一裁决，避免"边说边僵"或"边动边哑"。
/// </summary>
public sealed class AiNpcAgent : IAsyncDisposable
{
    private readonly AiNpcConfig _config;
    private readonly KimodoMotionConverter _converter;
    private readonly IKimodoMotionSource? _motionSource;
    private readonly MossLiveClient? _moss;
    private readonly AnimationRuntime? _animation;
    private readonly SceneObject? _avatar;

    private readonly ConcurrentQueue<ActionCompletion> _completed = new();
    private readonly ConcurrentQueue<ActionFailure> _failures = new();
    private readonly Queue<AiNpcAudioChunk> _audio = new();
    private readonly List<(string Speaker, string Text)> _transcript = new();

    private CancellationTokenSource? _actionCts;
    private int _actionSeq;
    private ActionPlayback? _active;
    private AiNpcState _mossState = AiNpcState.Idle;
    private AiNpcState _stateBeforeAction = AiNpcState.Idle;
    private AiNpcState _state = AiNpcState.Idle;
    private VisemeState? _viseme;
    private double _clock;

    // Unity5 深度姿态后处理（足部 IK / 注视 / 手势层），在播放动作时随目标骨架重建。
    private AiNpcMotionPostProcessor? _postProcessor;
    private KimodoConvertedMotion? _activeMotion;
    private Vector3? _lookAtTargetOverride;
    private float _lastDelta = 1f / 60f;

    public AiNpcAgent(
        AiNpcConfig config,
        NpcCharacter character,
        IKimodoMotionSource? motionSource = null,
        MossLiveClient? moss = null,
        AnimationRuntime? animationRuntime = null,
        SceneObject? avatar = null,
        KimodoMotionConverter? converter = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Character = character ?? throw new ArgumentNullException(nameof(character));
        _motionSource = motionSource;
        _moss = moss;
        _animation = animationRuntime;
        _avatar = avatar;
        _converter = converter ?? new KimodoMotionConverter(_config.Kimodo);
        _state = AiNpcState.Idle;
    }

    public NpcCharacter Character { get; }
    public AiNpcState State => _state;
    public string Emotion { get; private set; } = "neutral";
    public string PartialResponse { get; private set; } = "";
    public string LastResponse { get; private set; } = "";
    public string? LastError { get; private set; }
    public string CurrentActionName => _active?.Name ?? "";
    public IReadOnlyList<(string Speaker, string Text)> Transcript => _transcript;

    public event Action<AiNpcState>? StateChanged;
    public event Action<AiNpcAudioChunk>? AudioChunkReceived;
    public event Action<string, float>? VisemeReceived;
    public event Action<KimodoConvertedMotion>? MotionPlayed;
    public event Action<string, Exception?>? ActionFailed;

    /// <summary>连接 MOSS-Live 并建立会话。未配置时降级为纯本地动作/文本接口。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_config.MossLive.Enabled || _moss == null) return;
        try
        {
            var session = new MossSessionInfo
            {
                NpcId = Character.Name,
                Name = Character.Name,
                Persona = BuildPersona(),
                Voice = _config.MossLive.Voice,
                SampleRate = _config.MossLive.SampleRate,
                Channels = _config.MossLive.Channels
            };
            await _moss.StartAsync(session, ct).ConfigureAwait(false);
            SetState(AiNpcState.Listening);
        }
        catch (Exception ex)
        {
            LastError = $"MOSS-Live 连接失败: {ex.Message}";
            SetState(AiNpcState.Error);
            ActionFailed?.Invoke("connect", ex);
        }
    }

    /// <summary>每帧调用（建议在 AnimationRuntime.Update 之后，以便口型覆盖生效）。</summary>
    public void Update(float deltaSeconds)
    {
        if (deltaSeconds <= 0f) return;
        _clock += deltaSeconds;
        _lastDelta = deltaSeconds;

        DrainMossEvents();
        DrainActionResults();

        if (_viseme != null)
        {
            _viseme.Remaining -= deltaSeconds;
            if (_viseme.Remaining <= 0f) _viseme = null;
        }

        if (_active != null) AdvanceAction(deltaSeconds);
        RecomposeState();
    }

    /// <summary>
    /// 设置注视目标（世界坐标）。传 null 恢复为自动（面向自身朝向）。
    /// 对应 Unity Animator.SetLookAtPosition，让 NPC 头部跟随玩家/兴趣点。
    /// </summary>
    public void SetLookAtTarget(Vector3? worldPosition) => _lookAtTargetOverride = worldPosition;

    // ───────────────────────── 输入接口 ─────────────────────────

    public Task SendTextAsync(string text, CancellationToken ct = default)
    {
        if (_moss == null) throw new InvalidOperationException("未配置 MOSS-Live 客户端");
        return _moss.SendTextAsync(text, ct);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
    {
        if (_moss == null) throw new InvalidOperationException("未配置 MOSS-Live 客户端");
        return _moss.SendAudioAsync(pcm16, ct);
    }

    /// <summary>玩家打断 NPC（例如抢话/攻击），立即停止语音与动作。</summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        BargeIn();
        if (_moss != null) await _moss.SendInterruptAsync(ct).ConfigureAwait(false);
    }

    /// <summary>直接请求一段 Kimodo 动作（不走 MOSS 语义事件），可用于脚本/任务系统。</summary>
    public void RequestAction(string name, string prompt, float durationSeconds = 3f,
        IReadOnlyList<KimodoConstraint>? constraints = null, bool reportToMoss = false, string? emotion = null)
    {
        var evt = new MossServerEvent
        {
            Type = "action",
            Name = name,
            Prompt = prompt,
            DurationSeconds = durationSeconds,
            Constraints = constraints == null ? null : new List<KimodoConstraint>(constraints),
            WaitForCompletion = reportToMoss,
            Label = emotion
        };
        BeginAction(evt);
    }

    public bool TryDequeueAudio(out AiNpcAudioChunk chunk)
    {
        if (_audio.Count == 0)
        {
            chunk = null!;
            return false;
        }
        chunk = _audio.Dequeue();
        return true;
    }

    // ───────────────────────── MOSS 事件 ─────────────────────────

    private void DrainMossEvents()
    {
        if (_moss == null) return;
        int budget = 32;
        while (budget-- > 0 && _moss.TryDequeueEvent(out var evt))
            HandleMossEvent(evt);
    }

    private void HandleMossEvent(MossServerEvent evt)
    {
        switch (evt.Type)
        {
            case "ready":
                SetState(AiNpcState.Listening);
                break;

            case "state":
                _mossState = ParseState(evt.Value);
                break;

            case "speech_started":
                BargeIn();
                _mossState = AiNpcState.Listening;
                break;

            case "speech_ended":
                _mossState = AiNpcState.Thinking;
                break;

            case "text_delta":
                PartialResponse += evt.Text ?? "";
                break;

            case "text_done":
                LastResponse = string.IsNullOrEmpty(evt.Text) ? PartialResponse : evt.Text!;
                PartialResponse = "";
                _transcript.Add(("npc", LastResponse));
                if (_transcript.Count > 24) _transcript.RemoveAt(0);
                break;

            case "text_in":
                if (!string.IsNullOrEmpty(evt.Text))
                {
                    _transcript.Add(("player", evt.Text!));
                    if (_transcript.Count > 24) _transcript.RemoveAt(0);
                }
                break;

            case "audio":
                EnqueueAudio(evt);
                _mossState = AiNpcState.Speaking;
                break;

            case "viseme":
                if (!string.IsNullOrEmpty(evt.Viseme))
                {
                    float weight = Math.Clamp(evt.Weight ?? 0.5f, 0f, 1f);
                    float duration = Math.Max(0.02f, (evt.DurationMs ?? 60) / 1000f);
                    _viseme = new VisemeState { Name = evt.Viseme!, Weight = weight, Remaining = duration };
                    VisemeReceived?.Invoke(evt.Viseme!, weight);
                }
                break;

            case "emotion":
                Emotion = evt.Label ?? "neutral";
                break;

            case "action":
                BeginAction(evt);
                break;

            case "interrupt":
                BargeIn();
                break;

            case "error":
                LastError = evt.Message ?? "MOSS-Live 返回未知错误";
                SetState(AiNpcState.Error);
                break;

            default:
                break;
        }
    }

    private static AiNpcState ParseState(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "listening" => AiNpcState.Listening,
            "thinking" => AiNpcState.Thinking,
            "speaking" => AiNpcState.Speaking,
            "acting" => AiNpcState.Acting,
            "idle" => AiNpcState.Idle,
            _ => AiNpcState.Idle
        };
    }

    private void EnqueueAudio(MossServerEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Pcm16)) return;
        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(evt.Pcm16);
        }
        catch (FormatException)
        {
            LastError = "MOSS-Live 音频帧 base64 解码失败";
            return;
        }

        var chunk = new AiNpcAudioChunk
        {
            Pcm16 = pcm,
            SampleRate = evt.SampleRate ?? _config.MossLive.SampleRate,
            Channels = evt.Channels ?? _config.MossLive.Channels,
            Sequence = evt.Seq ?? 0,
            ReceiveTimeSeconds = _clock
        };

        while (_audio.Count >= Math.Max(4, _config.MossLive.MaxQueuedAudioChunks))
            _audio.Dequeue();
        _audio.Enqueue(chunk);
        AudioChunkReceived?.Invoke(chunk);
    }

    // ───────────────────────── 动作融合 ─────────────────────────

    private void BeginAction(MossServerEvent evt)
    {
        if (_motionSource == null)
        {
            LastError = "未配置 Kimodo 动作源，无法执行动作";
            ActionFailed?.Invoke(evt.Name ?? "action", null);
            return;
        }

        _actionCts?.Cancel();
        _actionCts = new CancellationTokenSource();
        int seq = ++_actionSeq;
        _ = GenerateActionAsync(evt, seq, _actionCts.Token);
    }

    private async Task GenerateActionAsync(MossServerEvent evt, int seq, CancellationToken ct)
    {
        try
        {
            float duration = Math.Clamp(evt.DurationSeconds ?? 3f, 0.5f, 10f);
            var request = new KimodoGenerateRequest
            {
                Prompt = string.IsNullOrWhiteSpace(evt.Prompt) ? BuildActionPrompt(evt) : evt.Prompt!,
                DurationSeconds = duration,
                Fps = _config.Kimodo.Fps,
                Model = _config.Kimodo.Model,
                Skeleton = _config.Kimodo.Skeleton,
                Seed = _config.Kimodo.Seed,
                Constraints = evt.Constraints ?? new List<KimodoConstraint>(),
                Metadata = evt.Metadata ?? new Dictionary<string, string>()
            };
            request.Metadata["npc"] = Character.Name;
            request.Metadata["emotion"] = Emotion;
            if (evt.Name != null) request.Metadata["action"] = evt.Name;

            var motion = await _motionSource!.GenerateAsync(request, ct).ConfigureAwait(false);

            Skeleton? target = _avatar != null && _animation != null
                ? _animation.GetInstance(_avatar)?.Skeleton
                : null;
            bool embedRoot = _avatar == null;
            var converted = _converter.Convert(motion, target, $"AiNpc_{evt.Name ?? "action"}", embedRootMotion: embedRoot);
            _completed.Enqueue(new ActionCompletion(seq, evt, converted));
        }
        catch (OperationCanceledException)
        {
            // 被新的动作/打断取消，属正常流程。
        }
        catch (Exception ex)
        {
            _failures.Enqueue(new ActionFailure(seq, evt.Name ?? "action", ex));
        }
    }

    private void DrainActionResults()
    {
        while (_completed.TryDequeue(out var done))
        {
            if (done.Sequence != _actionSeq) continue; // 过期结果
            PlayAction(done.Event, done.Motion);
        }
        while (_failures.TryDequeue(out var failure))
        {
            if (failure.Sequence != _actionSeq) continue;
            LastError = $"Kimodo 动作生成失败 [{failure.Name}]: {failure.Exception.Message}";
            ActionFailed?.Invoke(failure.Name, failure.Exception);
            _ = ReportActionResultAsync(failure.Name, "failed", 0f, LastError);
            _mossState = _stateBeforeAction;
        }
    }

    private void PlayAction(MossServerEvent evt, KimodoConvertedMotion motion)
    {
        var name = evt.Name ?? "action";
        if (_avatar != null && _animation != null)
        {
            _animation.PlayAnimation(_avatar, motion.Skeleton, motion.Clip, $"AiNpc:{name}");
        }

        if (!string.IsNullOrEmpty(evt.Label)) Emotion = evt.Label!;

        _active = new ActionPlayback
        {
            Name = name,
            Source = evt,
            Motion = motion,
            Duration = Math.Max(0.1f, motion.Clip.LengthSeconds),
            StartPosition = _avatar?.Position ?? Character.Position,
            StartRotation = _avatar?.Rotation ?? Quaternion.Identity,
            ApplyRootMotion = _avatar != null && _config.Kimodo.UseRootMotion
        };
        _activeMotion = motion;
        // 目标骨架已就绪，按当前骨架重建后处理器（脚/头/手势映射）。
        var targetSkeleton = _avatar != null && _animation != null
            ? _animation.GetInstance(_avatar)?.Skeleton
            : null;
        _postProcessor = targetSkeleton != null
            ? new AiNpcMotionPostProcessor(_config.Rig, targetSkeleton)
            : null;
        _stateBeforeAction = _mossState == AiNpcState.Acting ? AiNpcState.Listening : _mossState;
        _mossState = AiNpcState.Acting;
        MotionPlayed?.Invoke(motion);
    }

    private void AdvanceAction(float dt)
    {
        var action = _active!;
        action.Elapsed += dt;

        if (action.ApplyRootMotion && _avatar != null && action.Motion.RootPositions.Length > 1)
        {
            int index = Math.Clamp((int)(action.Elapsed * action.Motion.Fps), 0, action.Motion.RootPositions.Length - 1);
            var delta = action.Motion.RootPositions[index] - action.Motion.RootPositions[0];
            var rotated = Vector3.Transform(delta, Matrix4x4.CreateFromQuaternion(action.StartRotation));
            _avatar.Position = action.StartPosition + rotated;
            Character.Position = _avatar.Position;
        }

        if (action.Elapsed >= action.Duration)
            FinishAction(action);
    }

    private void FinishAction(ActionPlayback action)
    {
        _active = null;
        _activeMotion = null;
        _postProcessor = null;
        if (_avatar != null && _animation != null)
            _animation.StopAnimation(_avatar);
        _ = ReportActionResultAsync(action.Name, "completed", action.Elapsed,
            action.Source.WaitForCompletion == true ? null : "no-wait");
        _mossState = _stateBeforeAction;
    }

    private async Task ReportActionResultAsync(string name, string status, float duration, string? message = null)
    {
        if (_moss == null || _config.MossLive.Enabled == false) return;
        try
        {
            await _moss.SendActionResultAsync(name, status, duration, message, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // 断线时结果回传失败不能影响本地状态机。
        }
    }

    private void BargeIn()
    {
        _actionSeq++;
        _actionCts?.Cancel();
        _actionCts = null;
        if (_active != null && _avatar != null && _animation != null)
            _animation.StopAnimation(_avatar);
        _active = null;
        _activeMotion = null;
        _postProcessor = null;
        DrainAudio();
        _viseme = null;
        _mossState = AiNpcState.Listening;
    }

    private void DrainAudio()
    {
        _audio.Clear();
    }

    // ───────────────────────── 口型 ─────────────────────────

    /// <summary>
    /// 在 AnimationRuntime 的姿态后处理阶段调用：叠加 Unity5 Mecanim 级别的姿态效果。
    /// 顺序与 Unity OnAnimatorIK 一致：
    ///   1. viseme → 下颌（口型，最高优先级，不被手势覆盖）
    ///   2. 足部 IK → 脚踝贴合地面（由 Kimodo foot_contacts 驱动）
    ///   3. 上半身手势层 → 呼吸/手势微动作（AvatarMask 限制在骨盆以上）
    ///   4. 注视 IK → 头颈转向目标（最后执行，保证头不被身体覆盖）
    /// 必须在 ComputeBoneMatrices 之前执行，才能影响当帧渲染。
    /// </summary>
    internal void ApplyPoseOverrides(AnimationInstance instance)
    {
        if (_avatar == null || instance.Owner != _avatar) return;

        var pose = instance.Animator.CurrentPose;
        if (pose == null) return;

        // ── 1. 口型（viseme）──
        if (_viseme != null && !string.IsNullOrWhiteSpace(_config.LipSync.JawBone))
        {
            int jaw = instance.Skeleton.FindBone(_config.LipSync.JawBone);
            if (jaw >= 0)
            {
                float weight = Math.Clamp(_viseme.Weight, 0f, 1f);
                float angle = weight * _config.LipSync.MaxJawAngleDegrees * (MathF.PI / 180f);
                var baseRotation = pose.GetRotation(jaw);
                pose.SetRotation(jaw, baseRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle));
            }
        }

        // ── 2~4. Unity5 深度：足部 IK + 手势层 + 注视 IK ──
        if (_postProcessor == null || _activeMotion == null || _active == null) return;

        var (leftContact, rightContact) = AiNpcMotionPostProcessor.ExtractFootContacts(
            _activeMotion, (float)_active.Elapsed, _activeMotion.Fps);

        // 说话/思考时手势层激活量升高，聆听时降低，动作时按动作自然表达。
        float gestureAmount = _state switch
        {
            AiNpcState.Speaking => 1f,
            AiNpcState.Thinking => 0.6f,
            AiNpcState.Listening => 0.35f,
            AiNpcState.Acting => 0.5f,
            _ => 0.2f
        };

        _postProcessor.Process(
            instance,
            ResolveLookAtTarget(),
            leftContact,
            rightContact,
            gestureAmount,
            _lastDelta);
    }

    /// <summary>
    /// 解析注视目标：显式 override 优先；否则自动面向玩家（沿自身前向 1.5m）。
    /// 无 avatar 时不注视。
    /// </summary>
    private Vector3? ResolveLookAtTarget()
    {
        if (_lookAtTargetOverride.HasValue) return _lookAtTargetOverride;
        if (_avatar == null) return null;

        // 自动：注视当前对话对象（默认沿自身前向 1.5m 处的假想玩家）。
        var forward = Vector3.Transform(-Vector3.UnitZ, Matrix4x4.CreateFromQuaternion(_avatar.Rotation));
        forward = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));
        return _avatar.Position + forward * 1.5f + Vector3.UnitY * 1.6f;
    }

    // ───────────────────────── 内部 ─────────────────────────

    private void RecomposeState()
    {
        var desired = _active != null ? AiNpcState.Acting : _mossState;
        if (_state == AiNpcState.Error && desired == AiNpcState.Idle) desired = AiNpcState.Idle;
        SetState(desired);
    }

    private void SetState(AiNpcState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(next);
    }

    private string BuildPersona()
    {
        return $"{Character.Name}，{Character.Occupation}，{Character.Faction}；" +
               $"当前情绪={Emotion}。请保持角色一致，用口语短句回应，并在需要肢体表达时输出 action。";
    }

    private string BuildActionPrompt(MossServerEvent evt)
    {
        string action = evt.Name ?? "respond";
        var mood = Emotion == "neutral" ? "" : $"，情绪 {Emotion}";
        return $"NPC 执行动作 {action}{mood}，自然、完整、脚部稳定";
    }

    public async ValueTask DisposeAsync()
    {
        _actionCts?.Cancel();
        _actionCts?.Dispose();
        if (_moss != null) await _moss.DisposeAsync().ConfigureAwait(false);
        if (_active != null && _avatar != null && _animation != null)
            _animation.StopAnimation(_avatar);
    }

    private sealed record ActionCompletion(int Sequence, MossServerEvent Event, KimodoConvertedMotion Motion);
    private sealed record ActionFailure(int Sequence, string Name, Exception Exception);

    private sealed class VisemeState
    {
        public string Name { get; init; } = "";
        public float Weight { get; init; }
        public float Remaining { get; set; }
    }

    private sealed class ActionPlayback
    {
        public required string Name { get; init; }
        public required MossServerEvent Source { get; init; }
        public required KimodoConvertedMotion Motion { get; init; }
        public required float Duration { get; init; }
        public required Vector3 StartPosition { get; init; }
        public required Quaternion StartRotation { get; init; }
        public bool ApplyRootMotion { get; init; }
        public float Elapsed { get; set; }
    }
}
