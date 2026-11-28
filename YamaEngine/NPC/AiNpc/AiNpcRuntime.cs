using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Animation;
using YamaEngine.Assets;

namespace YamaEngine.NPC.AiNpc;

/// <summary>
/// AI NPC 运行时：统一持有 Kimodo 动作源与各 NPC Agent，并在引擎主循环中 Tick。
/// 由 <see cref="YamaEngine.Core.EngineSystems"/> 承载，也支持独立使用。
/// </summary>
public sealed class AiNpcRuntime
{
    private readonly List<AiNpcAgent> _agents = new();
    private readonly IKimodoMotionSource? _sharedMotionSource;
    private bool _shutdown;

    public AiNpcRuntime(AiNpcConfig? config = null,
        AnimationRuntime? animationRuntime = null,
        IKimodoMotionSource? sharedMotionSource = null)
    {
        Config = config ?? AiNpcConfig.CreateDefault();
        Config.Validate();
        Animation = animationRuntime;
        MotionSource = sharedMotionSource
            ?? (Config.Kimodo.Enabled ? new KimodoMotionClient(Config.Kimodo) : null);

        if (Animation != null)
            Animation.PosePostProcess += OnPosePostProcess;
    }

    private void OnPosePostProcess(AnimationInstance instance)
    {
        for (int i = 0; i < _agents.Count; i++)
            _agents[i].ApplyPoseOverrides(instance);
    }

    public AiNpcConfig Config { get; }
    public AnimationRuntime? Animation { get; }
    public IKimodoMotionSource? MotionSource { get; }
    public IReadOnlyList<AiNpcAgent> Agents => _agents;
    public int AgentCount => _agents.Count;

    /// <summary>为角色创建 Agent；avatar 为空时仍可对话/请求动作（动作仅进事件流，不驱动场景）。
    /// 自动为角色创建 <see cref="NpcBrain"/> 心智，使语音/动作 Agent 与 OCEAN 个性/情感/记忆共享同一模型。</summary>
    public AiNpcAgent CreateAgent(NpcCharacter character, SceneObject? avatar = null, bool autoStart = true,
        NpcBrain? brain = null)
    {
        if (character == null) throw new ArgumentNullException(nameof(character));
        var moss = Config.MossLive.Enabled
            ? new MossLiveClient(Config.MossLive)
            : null;
        // 未显式提供大脑时，为每个 Agent 装配独立心智（个性/情感/记忆/关系/日程）。
        brain ??= new NpcBrain(character);
        var agent = new AiNpcAgent(Config, character, MotionSource, moss, Animation, avatar, brain: brain);
        _agents.Add(agent);
        if (autoStart) _ = agent.StartAsync();
        return agent;
    }

    public void Update(float deltaSeconds)
    {
        if (_shutdown) return;
        for (int i = 0; i < _agents.Count; i++)
            _agents[i].Update(deltaSeconds);
    }

    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var agent in _agents)
            await agent.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        if (Animation != null)
            Animation.PosePostProcess -= OnPosePostProcess;
        foreach (var agent in _agents)
            await agent.DisposeAsync().ConfigureAwait(false);
        _agents.Clear();
        if (MotionSource is KimodoMotionClient client) client.Dispose();
    }
}
