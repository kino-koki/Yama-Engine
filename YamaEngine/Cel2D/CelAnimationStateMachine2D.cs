using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>
    /// 2D 动画状态机状态：持有动画片段与可选循环。
    /// </summary>
    public sealed class CelAnimState2D
    {
        public string Name { get; set; } = "State";
        public CelAnimationClip Clip { get; set; } = new();
        public bool Loop { get; set; } = true;
        public float Speed { get; set; } = 1.0f;

        public CelAnimState2D() { }

        public CelAnimState2D(string name, CelAnimationClip clip, bool loop = true)
        {
            Name = name;
            Clip = clip;
            Loop = loop;
        }
    }

    /// <summary>
    /// 2D 动画状态转换：由条件决定是否切换。
    /// </summary>
    public sealed class CelAnimTransition2D
    {
        public string FromState { get; set; } = string.Empty;
        public string ToState { get; set; } = string.Empty;

        /// <summary>条件（返回 true 则触发转换）。</summary>
        public Func<CelAnimationStateMachine2D, bool>? Condition { get; set; }

        /// <summary>转换后是否从新状态开头播放。</summary>
        public bool RestartOnEnter { get; set; } = true;

        /// <summary>触发该转换（无条件，供外部调用）。</summary>
        public string? TriggerKey { get; set; }
    }

    /// <summary>
    /// 2D 动画状态机：驱动 <see cref="CelAnimationPlayer"/> 在多个片段间切换。
    /// 用于赛璐璐二次元角色（待机/行走/攻击/受击…）。
    /// 纯逻辑层。
    /// </summary>
    public sealed class CelAnimationStateMachine2D
    {
        private readonly Dictionary<string, CelAnimState2D> _states = new(StringComparer.Ordinal);
        private readonly List<CelAnimTransition2D> _transitions = new();

        public CelAnimationPlayer Player { get; } = new();

        /// <summary>当前状态名。</summary>
        public string CurrentState { get; private set; } = string.Empty;

        /// <summary>当前状态索引（用于外部判断）。</summary>
        public int CurrentStateIndex { get; private set; } = -1;

        /// <summary>是否已初始化（进入首个状态）。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>绑定精灵（播放器驱动其 SourceRect）。</summary>
        public CelSpriteData? BoundSprite { get; private set; }

        public void AddState(CelAnimState2D state) => _states[state.Name] = state;

        public void AddTransition(CelAnimTransition2D transition) => _transitions.Add(transition);

        /// <summary>绑定精灵：状态机播放时更新其 SourceRect。</summary>
        public void BindSprite(CelSpriteData sprite)
        {
            BoundSprite = sprite;
            if (sprite != null)
                sprite.Player = Player;
        }

        /// <summary>进入指定状态。</summary>
        public bool Enter(string stateName)
        {
            if (!_states.TryGetValue(stateName, out var state))
                return false;
            CurrentState = stateName;
            CurrentStateIndex = FindIndex(stateName);
            state.Clip.PlaybackSpeed = state.Speed;
            Player.Play(state.Clip);
            IsInitialized = true;
            return true;
        }

        /// <summary>推进状态机（评估转换 + 动画）。</summary>
        public void Update(float deltaTime)
        {
            if (!IsInitialized)
                return;

            // 先评估转换。
            foreach (var t in _transitions)
            {
                if (string.Equals(t.FromState, CurrentState, StringComparison.Ordinal))
                {
                    if (t.TriggerKey != null)
                        continue; // 触发器转换由外部 Fire 处理
                    if (t.Condition != null && t.Condition(this))
                    {
                        Enter(t.ToState);
                        return;
                    }
                }
            }

            // 推进动画并同步精灵源矩形。
            Vector4 rect = Player.Update(deltaTime);
            if (BoundSprite != null)
                BoundSprite.SourceRect = rect;
        }

        /// <summary>触发带 TriggerKey 的转换。</summary>
        public bool Fire(string triggerKey)
        {
            foreach (var t in _transitions)
            {
                if (string.Equals(t.TriggerKey, triggerKey, StringComparison.Ordinal) &&
                    string.Equals(t.FromState, CurrentState, StringComparison.Ordinal))
                {
                    return Enter(t.ToState);
                }
            }
            return false;
        }

        /// <summary>检查是否处于某状态。</summary>
        public bool IsIn(string stateName) => string.Equals(CurrentState, stateName, StringComparison.Ordinal);

        /// <summary>当前状态是否已播放完毕（非循环片段结束时）。</summary>
        public bool IsFinished =>
            Player.State != CelPlaybackState.Playing && Player.State != CelPlaybackState.Paused;

        private int FindIndex(string stateName)
        {
            int i = 0;
            foreach (var kv in _states)
            {
                if (string.Equals(kv.Key, stateName, StringComparison.Ordinal))
                    return i;
                i++;
            }
            return -1;
        }
    }
}
