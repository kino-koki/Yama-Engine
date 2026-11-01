using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.ECS;

namespace YamaEngine.Gameplay.Effects
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 游戏效果系统 (Gameplay Effects System, 类 GAS)
    //
    // 本文件实现一套数据驱动的玩法效果框架, 对齐 Unreal Engine 的 Gameplay Ability
    // System 思路, 涵盖: 层级标签、属性集合与修改器、即时/持续/无限/周期效果、
    // 堆叠规则、自定义执行、级别缩放与概率应用、技能生命周期/任务/连招/输入绑定、
    // 游戏提示 (GameplayCue) 以及与 ECS 的对接。
    //
    // 命名空间说明:
    //   本类型置于 YamaEngine.Gameplay.Effects 子命名空间, 以避免与同引擎内
    //   YamaEngine.Gameplay.GameplayAbilitySystem.cs 中已存在的同名类型
    //   (GameplayTag / GameplayEffect / GameplayAbility / AbilitySystemComponent 等)
    //   产生编译期重复定义 (CS0101)。如需在玩家/角色代码中使用本系统, 添加:
    //     using YamaEngine.Gameplay.Effects;
    //
    // 对接系统:
    //   * ECS: YamaEngine.ECS.IComponent / ISystem / EntityComponentSystem
    //          —— 通过 AbilitySystemComponentData (struct) 挂载, 由 AbilitySystem (ISystem) 每帧 Tick。
    //   * 物理/动画/音频: 通过 GameplayCue 的回调委托间接对接, 保持本系统纯净。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. GameplayTag —— 层级标签系统
    //    标签以 '.' 分隔层级, 例如 "Status.Burning.Degree1"。
    //    父标签匹配: 子标签 Matches(父标签) 为 true。
    //    标签必须先经 GameplayTagManager 注册, 才能被 Parse 创建。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 游戏标签 —— 对已注册层级标签节点的轻量只读引用。
    /// 标签字符串以 '.' 分隔, 不可为空、不可含空格、不可连续 '.'。
    /// </summary>

    /// <summary>
    /// 标签树节点 —— 维护父子关系, 用于在编辑器中可视化与查询。
    /// </summary>

    /// <summary>
    /// 标签管理器 —— 维护全局标签树, 负责注册/查询。
    /// </summary>
    public static class GameplayTagManager
    {
        private static readonly Dictionary<string, GameplayTagNode> _nodes = new(StringComparer.Ordinal);
        private static readonly GameplayTagNode _root = new("", "", null);

        /// <summary>根节点。</summary>
        public static GameplayTagNode Root => _root;

        /// <summary>已注册标签数。</summary>
        public static int Count => _nodes.Count;

        /// <summary>标签是否已注册。</summary>
        public static bool IsRegistered(string fullName)
            => !string.IsNullOrEmpty(fullName) && _nodes.ContainsKey(fullName);

        /// <summary>注册一个原生标签 (引擎/代码侧)。</summary>
        public static GameplayTag RegisterNative(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return GameplayTag.None;
            string trimmed = fullName.Trim();
            if (_nodes.TryGetValue(trimmed, out var existing)) return new GameplayTag(existing.FullName);
            // 逐段创建, 保证父节点存在
            string[] parts = trimmed.Split('.');
            string acc = "";
            GameplayTagNode? parent = _root;
            for (int i = 0; i < parts.Length; i++)
            {
                acc = i == 0 ? parts[i] : acc + "." + parts[i];
                if (!_nodes.TryGetValue(acc, out var node))
                {
                    node = new GameplayTagNode(acc, parts[i], parent == _root ? null : parent);
                    _nodes[acc] = node;
                    (parent ?? _root).Children.Add(node.FullName, node);
                }
                parent = node;
            }
            return new GameplayTag(trimmed);
        }

        /// <summary>按完整名查找节点。</summary>
        public static GameplayTagNode? FindNode(string fullName)
            => _nodes.TryGetValue(fullName, out var n) ? n : null;
    }

    /// <summary>
    /// 标签容器 —— 持有一组 GameplayTag, 支持 HasTag / HasAny / HasAll / Matches 查询。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 2. GameplayAttribute —— 游戏属性
    //    属性 = 基础值 + 修改器列表 -> 当前值, 支持钳制与变化事件。
    // ──────────────────────────────────────────────────────────────

    /// <summary>属性修改器运算类型。</summary>
    public enum AttributeModifierOp : byte
    {
        /// <summary>加法: current += magnitude</summary>
        Add = 0,
        /// <summary>乘法: current *= magnitude</summary>
        Multiply = 1,
        /// <summary>除法: current /= magnitude</summary>
        Divide = 2,
        /// <summary>覆盖: current = magnitude (最后生效, 多个取最后一个)</summary>
        Override = 3
    }

    /// <summary>
    /// 属性修改器 —— 由 GameplayEffect 施加到属性上的单项修正。
    /// </summary>

    /// <summary>
    /// 游戏属性 —— 持有基础值、当前值与修改器列表, 提供重算/钳制/事件。
    /// 计算公式:
    ///   v = BaseValue
    ///   v += Σ(Add)
    ///   v *= Π(Multiply)
    ///   v /= Π(Divide)
    ///   if any(Override): v = 最后一个 Override 值
    ///   v = clamp(v, Min, Max)
    /// </summary>
    public class GameplayAttribute
    {
        /// <summary>属性名。</summary>
        public string Name { get; set; }
        /// <summary>基础值。</summary>
        public float BaseValue { get; set; }
        /// <summary>当前值 (重算后)。</summary>
        public float CurrentValue { get; set; }
        /// <summary>最小钳制值。</summary>
        public float Min { get; set; } = float.MinValue;
        /// <summary>最大钳制值。</summary>
        public float Max { get; set; } = float.MaxValue;

        private readonly List<AttributeModifier> _modifiers = new();

        /// <summary>属性值变化事件。参数: (属性名, 旧值, 新值)。</summary>
        public event Action<string, float, float>? OnValueChanged;

        /// <summary>创建属性。</summary>
        public GameplayAttribute() : this("", 0f) { }

        /// <summary>创建属性。</summary>
        public GameplayAttribute(string name, float baseValue = 0f, float min = float.MinValue, float max = float.MaxValue)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            BaseValue = baseValue;
            Min = min;
            Max = max;
            CurrentValue = baseValue;
        }

        /// <summary>添加修改器。</summary>
        public void AddModifier(AttributeModifier mod) => _modifiers.Add(mod);

        /// <summary>移除指定来源句柄的全部修改器。</summary>
        public void RemoveModifiersFrom(int sourceHandle)
        {
            _modifiers.RemoveAll(m => m.SourceEffectHandle == sourceHandle);
        }

        /// <summary>移除全部修改器。</summary>
        public void ClearModifiers() => _modifiers.Clear();

        /// <summary>修改器数量。</summary>
        public int ModifierCount => _modifiers.Count;

        /// <summary>
        /// 根据当前基础值与修改器列表重新计算当前值。
        /// 计算顺序: Add -> Multiply -> Divide -> Override -> Clamp。
        /// </summary>
        public void Recalculate()
        {
            float v = BaseValue;
            float addSum = 0f, mulProd = 1f, divProd = 1f;
            float? overrideVal = null;

            foreach (var mod in _modifiers)
            {
                switch (mod.Op)
                {
                    case YamaEngine.Gameplay.AttributeModifierOp.Add: addSum += mod.Magnitude; break;
                    case YamaEngine.Gameplay.AttributeModifierOp.Multiply: mulProd *= mod.Magnitude; break;
                    case YamaEngine.Gameplay.AttributeModifierOp.Divide: divProd *= mod.Magnitude; break;
                    case YamaEngine.Gameplay.AttributeModifierOp.Override: overrideVal = mod.Magnitude; break;
                }
            }

            v += addSum;
            if (mulProd != 1f) v *= mulProd;
            if (divProd != 0f && divProd != 1f) v /= divProd;
            if (overrideVal.HasValue) v = overrideVal.Value;
            v = Math.Clamp(v, Min, Max);

            float old = CurrentValue;
            CurrentValue = v;
            if (Math.Abs(old - v) > 1e-6f)
                OnValueChanged?.Invoke(Name, old, v);
        }

        /// <summary>直接设置基础值并重算。</summary>
        public void SetBaseValue(float value)
        {
            BaseValue = value;
            Recalculate();
        }

        /// <summary>快照当前修改器量值汇总 (调试用)。</summary>
        public override string ToString()
            => $"{Name}: {CurrentValue:F2} (base={BaseValue:F2}, mods={_modifiers.Count})";
    }

    // ──────────────────────────────────────────────────────────────
    // 3. AttributeSet —— 属性集合
    //    管理一组命名属性, 提供按名查找/批量重算。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 属性集合 —— 持有一组 GameplayAttribute, 提供按名查找与批量重算。
    /// 通常挂载在 ASC (AbilitySystemComponent) 上。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 4. GameplayEffect —— 游戏效果
    //    定义对属性的修改 (即时/持续/无限/周期), 标签授予, 堆叠规则。
    // ──────────────────────────────────────────────────────────────

    /// <summary>效果持续时间策略。</summary>
    public enum EEffectDurationPolicy : byte
    {
        /// <summary>即时: 立即修改基础值, 不留存。</summary>
        Instant,
        /// <summary>持续: 限定时间内保持修改器, 到期移除。</summary>
        Duration,
        /// <summary>无限: 持续到手动移除。</summary>
        Infinite
    }

    /// <summary>效果堆叠策略。</summary>
    public enum EStackingPolicy : byte
    {
        /// <summary>不叠加: 每次施加都是独立实例。</summary>
        NoStack,
        /// <summary>叠加层数: 同类效果增加层数, 刷新持续时间。</summary>
        StackByCount,
        /// <summary>叠加量值: 同类效果合并量值。</summary>
        StackByMagnitude
    }

    /// <summary>效果修改器定义 (在效果定义中, 运行时转化为 AttributeModifier)。</summary>
    public struct EffectModifierDef
    {
        /// <summary>目标属性名。</summary>
        public string AttributeName;
        /// <summary>运算类型。</summary>
        public AttributeModifierOp Op;
        /// <summary>量值。</summary>
        public float Magnitude;
    }

    /// <summary>
    /// 游戏效果定义 —— 描述对属性的修改方式与生命周期。
    /// </summary>
    public class GameplayEffect
    {
        /// <summary>效果名。</summary>
        public string Name { get; set; } = "Effect";
        /// <summary>持续时间策略。</summary>
        public EEffectDurationPolicy DurationPolicy { get; set; } = EEffectDurationPolicy.Instant;
        /// <summary>持续时间 (秒), Duration 策略时使用。</summary>
        public float Duration { get; set; } = 0f;
        /// <summary>周期 (秒), >0 时按周期重复施加即时修改器。</summary>
        public float Period { get; set; } = 0f;
        /// <summary>堆叠策略。</summary>
        public EStackingPolicy StackingPolicy { get; set; } = EStackingPolicy.NoStack;
        /// <summary>最大堆叠层数。</summary>
        public int MaxStacks { get; set; } = 1;
        /// <summary>修改器定义列表。</summary>
        public List<EffectModifierDef> Modifiers { get; set; } = new();
        /// <summary>效果授予的标签 (施加时添加, 移除时撤销)。</summary>
        public GameplayTagContainer GrantedTags { get; set; } = new();
        /// <summary>施加时触发的 GameplayCue 标签。</summary>
        public GameplayTagContainer CueTagsOnApply { get; set; } = new();
        /// <summary>移除时触发的 GameplayCue 标签。</summary>
        public GameplayTagContainer CueTagsOnRemove { get; set; } = new();
        /// <summary>应用概率 (0~1), 低于 1 时随机决定是否施加。</summary>
        public float ChanceToApply { get; set; } = 1f;
    }

    /// <summary>
    /// 效果运行时实例 —— 一个 GameplayEffect 在特定 ASC 上的实例化。
    /// </summary>
    public class ActiveEffect
    {
        private static int _nextHandle = 1;

        /// <summary>唯一句柄。</summary>
        public int Handle { get; } = _nextHandle++;
        /// <summary>效果定义。</summary>
        public GameplayEffect Def { get; }
        /// <summary>当前层数。</summary>
        public int Stacks { get; set; } = 1;
        /// <summary>已持续时间 (秒)。</summary>
        public float ElapsedTime { get; set; }
        /// <summary>下次周期执行时间 (秒)。</summary>
        public float NextPeriodTime { get; set; }
        /// <summary>授予的标签快照 (用于移除时撤销)。</summary>
        public GameplayTagContainer GrantedTags { get; } = new();

        /// <summary>是否已过期 (Duration 策略)。</summary>
        public bool IsExpired =>
            Def.DurationPolicy == EEffectDurationPolicy.Duration && ElapsedTime >= Def.Duration;

        // ── 兼容成员（AbilitySystem 等使用 Spec / RemainingTime / PeriodTimer / SourceLevel / Paused 的模型）──
        public GameplayEffectSpec? Spec { get; set; }
        public float RemainingTime { get; set; }
        public float PeriodTimer { get; set; }
        public int SourceLevel { get; set; }
        public bool Paused { get; set; }

        /// <summary>创建实例。</summary>
        public ActiveEffect(GameplayEffect def)
        {
            Def = def ?? throw new ArgumentNullException(nameof(def));
        }

        public ActiveEffect() { }

        /// <summary>
        /// 以指定句柄与规格创建 (兼容 AbilitySystem 旧模型)。
        /// </summary>
        public ActiveEffect(int handle, YamaEngine.Gameplay.GameplayEffectSpec spec)
        {
            Handle = handle;
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            Def = null!;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. GameplayCue —— 游戏提示 (粒子/音效/相机震动/时间膨胀)
    //    数据驱动的视觉/听觉/镜头反馈, 由 GameplayCueManager 执行。
    // ──────────────────────────────────────────────────────────────

    /// <summary>GameplayCue 触发类型。</summary>
    public enum ECueEventType : byte
    {
        /// <summary>效果施加时触发。</summary>
        OnApply,
        /// <summary>效果移除时触发。</summary>
        OnRemove,
        /// <summary>周期执行时触发。</summary>
        OnPeriod,
        /// <summary>持续激活 (每帧执行, 如持续粒子)。</summary>
        WhileActive
    }

    /// <summary>
    /// 粒子特效参数 —— 定义触发哪种粒子, 在何处, 以什么参数播放。
    /// </summary>
    public struct ParticleCueParams
    {
        /// <summary>粒子系统资源名。</summary>
        public string SystemName;
        /// <summary>是否跟随拥有者 (否则在固定位置播放)。</summary>
        public bool AttachToOwner;
        /// <summary>附着骨骼名 (AttachToOwner 为 true 时使用)。</summary>
        public string AttachBoneName;
        /// <summary>位置偏移。</summary>
        public Vector3 Offset;
        /// <summary>缩放。</summary>
        public Vector3 Scale;
        /// <summary>存活时间 (秒, <=0 表示使用粒子系统自带寿命)。</summary>
        public float Lifetime;

        public static ParticleCueParams Default => new()
        {
            SystemName = "",
            AttachToOwner = true,
            AttachBoneName = "",
            Offset = Vector3.Zero,
            Scale = Vector3.One,
            Lifetime = 0f
        };
    }

    /// <summary>
    /// 音效参数 —— 定义触发哪种音效, 音量/音调/3D 位置。
    /// </summary>
    public struct SoundCueParams
    {
        /// <summary>音频片段名。</summary>
        public string ClipName;
        /// <summary>音量 (0~2)。</summary>
        public float Volume;
        /// <summary>音调 (0.5~2.0)。</summary>
        public float Pitch;
        /// <summary>是否 3D 空间化。</summary>
        public bool Spatial;
        /// <summary>是否循环。</summary>
        public bool Loop;

        public static SoundCueParams Default => new()
        {
            ClipName = "",
            Volume = 1f,
            Pitch = 1f,
            Spatial = true,
            Loop = false
        };
    }

    /// <summary>
    /// 相机震动参数 —— 定义震动幅度/频率/衰减。
    /// </summary>
    public struct CameraShakeParams
    {
        /// <summary>震动名 (用于在管理器中查找震动曲线)。</summary>
        public string ShakeName;
        /// <summary>振幅倍数。</summary>
        public float Amplitude;
        /// <summary>频率 (Hz)。</summary>
        public float Frequency;
        /// <summary>持续时间 (秒)。</summary>
        public float Duration;
        /// <summary>衰减时间 (秒)。</summary>
        public float FadeOutTime;
        /// <summary>位置偏移幅度。</summary>
        public Vector3 PositionAmplitude;
        /// <summary>旋转偏移幅度 (弧度)。</summary>
        public Vector3 RotationAmplitude;

        public static CameraShakeParams Default => new()
        {
            ShakeName = "",
            Amplitude = 1f,
            Frequency = 20f,
            Duration = 0.3f,
            FadeOutTime = 0.2f,
            PositionAmplitude = new Vector3(0.5f, 0.5f, 0f),
            RotationAmplitude = new Vector3(0.01f, 0.01f, 0.02f)
        };
    }

    /// <summary>
    /// 时间膨胀参数 —— 定义全局时间缩放, 用于慢动作/子弹时间效果。
    /// </summary>
    public struct TimeDilationParams
    {
        /// <summary>时间缩放系数 (1.0=正常, 0.2=慢动作, 2.0=加速)。</summary>
        public float TimeScale;
        /// <summary>持续时间 (秒), 到期后恢复 1.0。</summary>
        public float Duration;
        /// <summary>淡入时间 (秒)。</summary>
        public float FadeInTime;
        /// <summary>淡出时间 (秒)。</summary>
        public float FadeOutTime;

        public static TimeDilationParams Default => new()
        {
            TimeScale = 0.3f,
            Duration = 1f,
            FadeInTime = 0.1f,
            FadeOutTime = 0.3f
        };
    }

    /// <summary>
    /// GameplayCue —— 游戏提示定义。
    /// 描述在效果施加/移除/周期/持续时触发哪些粒子/音效/相机震动/时间膨胀。
    /// 由 GameplayCueManager 查找并执行, 通过回调委托对接实际子系统。
    /// </summary>
    public class GameplayCue
    {
        /// <summary>Cue 标签 (用于路由, 如 "Cue.Hit.Fireball")。</summary>
        public GameplayTag Tag { get; }
        /// <summary>触发事件类型。</summary>
        public ECueEventType EventType { get; set; } = ECueEventType.OnApply;
        /// <summary>粒子参数 (SystemName 为空则不触发粒子)。</summary>
        public ParticleCueParams Particle { get; set; } = ParticleCueParams.Default;
        /// <summary>音效参数 (ClipName 为空则不触发音效)。</summary>
        public SoundCueParams Sound { get; set; } = SoundCueParams.Default;
        /// <summary>相机震动参数 (ShakeName 为空则不震动)。</summary>
        public CameraShakeParams CameraShake { get; set; } = CameraShakeParams.Default;
        /// <summary>时间膨胀参数 (Duration <= 0 则不触发)。</summary>
        public TimeDilationParams TimeDilation { get; set; } = TimeDilationParams.Default;
        /// <summary>是否启用粒子。</summary>
        public bool EnableParticle { get; set; } = true;
        /// <summary>是否启用音效。</summary>
        public bool EnableSound { get; set; } = true;
        /// <summary>是否启用相机震动。</summary>
        public bool EnableCameraShake { get; set; } = true;
        /// <summary>是否启用时间膨胀。</summary>
        public bool EnableTimeDilation { get; set; }

        /// <summary>创建 Cue。</summary>
        public GameplayCue(GameplayTag tag)
        {
            Tag = tag;
        }
    }

    /// <summary>
    /// GameplayCue 管理器 —— 注册 Cue 定义, 按标签路由触发。
    /// 通过回调委托对接实际子系统 (粒子系统/音频系统/相机系统/时间管理器),
    /// 保持本系统与渲染/音频/镜头子系统解耦。
    /// </summary>
    public class GameplayCueManager
    {
        private readonly Dictionary<string, List<GameplayCue>> _cueTable = new(StringComparer.Ordinal);

        // ── 外部回调 (由引擎引导时注入) ──

        /// <summary>粒子触发回调: (系统名, 世界位置, 偏移, 缩放, 存活时间)。</summary>
        public Action<string, Vector3, Vector3, Vector3, float>? OnSpawnParticle;
        /// <summary>音效触发回调: (片段名, 世界位置, 音量, 音调, 是否3D, 是否循环)。</summary>
        public Action<string, Vector3, float, float, bool, bool>? OnPlaySound;
        /// <summary>停止音效回调: (片段名)。</summary>
        public Action<string>? OnStopSound;
        /// <summary>相机震动回调: (震动名, 振幅, 频率, 持续时间, 衰减时间, 位置幅度, 旋转幅度)。</summary>
        public Action<string, float, float, float, float, Vector3, Vector3>? OnCameraShake;
        /// <summary>时间膨胀回调: (时间缩放, 持续时间, 淡入, 淡出)。</summary>
        public Action<float, float, float, float>? OnTimeDilation;

        /// <summary>注册一个 Cue。</summary>
        public void RegisterCue(GameplayCue cue)
        {
            if (cue == null || !cue.Tag.IsValid) return;
            string key = cue.Tag.Name;
            if (!_cueTable.TryGetValue(key, out var list))
            {
                list = new List<GameplayCue>();
                _cueTable[key] = list;
            }
            list.Add(cue);
        }

        /// <summary>按标签名注册简单 Cue (快捷方法)。</summary>
        public void RegisterCue(string tag, ECueEventType evt,
            string particleSystem = "", string soundClip = "",
            string cameraShakeName = "", float timeScale = 0f, float timeScaleDuration = 0f)
        {
            var cue = new GameplayCue(GameplayTag.Parse(tag))
            {
                EventType = evt,
                Particle = new ParticleCueParams { SystemName = particleSystem ?? "" },
                Sound = new SoundCueParams { ClipName = soundClip ?? "" },
                CameraShake = new CameraShakeParams { ShakeName = cameraShakeName ?? "" },
                TimeDilation = new TimeDilationParams
                {
                    TimeScale = timeScale > 0f ? timeScale : 1f,
                    Duration = timeScaleDuration > 0f ? timeScaleDuration : 0f
                },
                EnableParticle = !string.IsNullOrEmpty(particleSystem),
                EnableSound = !string.IsNullOrEmpty(soundClip),
                EnableCameraShake = !string.IsNullOrEmpty(cameraShakeName),
                EnableTimeDilation = timeScale > 0f && timeScaleDuration > 0f
            };
            RegisterCue(cue);
        }

        /// <summary>
        /// 触发匹配指定标签的全部 Cue (考虑层级: 子标签也匹配)。
        /// </summary>
        /// <param name="tag">触发标签。</param>
        /// <param name="eventType">触发事件类型。</param>
        /// <param name="worldPosition">触发世界位置 (粒子/音效用)。</param>
        public void Trigger(GameplayTag tag, ECueEventType eventType, Vector3 worldPosition)
        {
            if (!tag.IsValid) return;
            foreach (var kvp in _cueTable)
            {
                // 标签层级匹配: 若注册的 Cue 标签是触发标签的父级或同级则命中
                var cueTag = new GameplayTag(kvp.Key);
                if (!tag.Matches(cueTag) && !cueTag.Matches(tag)) continue;

                foreach (var cue in kvp.Value)
                {
                    if (cue.EventType != eventType) continue;
                    ExecuteCue(cue, worldPosition);
                }
            }
        }

        /// <summary>执行单个 Cue 的全部已启用效果。</summary>
        private void ExecuteCue(GameplayCue cue, Vector3 worldPosition)
        {
            // 粒子触发
            if (cue.EnableParticle && !string.IsNullOrEmpty(cue.Particle.SystemName) && OnSpawnParticle != null)
            {
                OnSpawnParticle(cue.Particle.SystemName, worldPosition,
                    cue.Particle.Offset, cue.Particle.Scale, cue.Particle.Lifetime);
            }

            // 音效触发
            if (cue.EnableSound && !string.IsNullOrEmpty(cue.Sound.ClipName) && OnPlaySound != null)
            {
                OnPlaySound(cue.Sound.ClipName, worldPosition,
                    cue.Sound.Volume, cue.Sound.Pitch, cue.Sound.Spatial, cue.Sound.Loop);
            }

            // 相机震动
            if (cue.EnableCameraShake && !string.IsNullOrEmpty(cue.CameraShake.ShakeName) && OnCameraShake != null)
            {
                OnCameraShake(cue.CameraShake.ShakeName, cue.CameraShake.Amplitude,
                    cue.CameraShake.Frequency, cue.CameraShake.Duration, cue.CameraShake.FadeOutTime,
                    cue.CameraShake.PositionAmplitude, cue.CameraShake.RotationAmplitude);
            }

            // 时间膨胀
            if (cue.EnableTimeDilation && cue.TimeDilation.Duration > 0f && OnTimeDilation != null)
            {
                OnTimeDilation(cue.TimeDilation.TimeScale, cue.TimeDilation.Duration,
                    cue.TimeDilation.FadeInTime, cue.TimeDilation.FadeOutTime);
            }
        }

        /// <summary>停止匹配标签的全部持续音效 (WhileActive 时启动的循环音)。</summary>
        public void StopCueSounds(GameplayTag tag)
        {
            if (!tag.IsValid) return;
            foreach (var kvp in _cueTable)
            {
                var cueTag = new GameplayTag(kvp.Key);
                if (!tag.Matches(cueTag) && !cueTag.Matches(tag)) continue;
                foreach (var cue in kvp.Value)
                {
                    if (cue.EnableSound && cue.Sound.Loop && !string.IsNullOrEmpty(cue.Sound.ClipName))
                        OnStopSound?.Invoke(cue.Sound.ClipName);
                }
            }
        }

        /// <summary>已注册 Cue 数量。</summary>
        public int CueCount => _cueTable.Values.Sum(l => l.Count);

        /// <summary>清除全部注册。</summary>
        public void Clear() => _cueTable.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    // 6. AbilitySystemComponent —— 效果系统组件
    //    挂载在实体上, 管理属性集 / 活跃效果 / 标签 / Cue 路由。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 效果系统组件 —— 管理一个实体的属性集、活跃效果列表、动态标签,
    /// 并路由 GameplayCue 触发。与 ECS 通过 AbilitySystemComponentData 对接。
    /// </summary>
    public class AbilitySystemComponent
    {
        private readonly AttributeSet _attributes = new();
        private readonly List<ActiveEffect> _activeEffects = new();
        private readonly GameplayTagContainer _dynamicTags = new();
        private readonly GameplayTagContainer _grantedTags = new();
        private int _nextEffectHandle = 1;

        /// <summary>实体 Id (ECS)。</summary>
        public int EntityId { get; set; } = -1;
        /// <summary>世界位置 (用于 Cue 触发)。</summary>
        public Vector3 WorldPosition { get; set; } = Vector3.Zero;
        /// <summary>属性集。</summary>
        public AttributeSet Attributes => _attributes;
        /// <summary>Cue 管理器引用 (全局共享)。</summary>
        public GameplayCueManager? CueManager { get; set; }
        /// <summary>内部时间 (秒)。</summary>
        public float Time { get; private set; }

        // ── 事件 ──
        /// <summary>效果施加事件。</summary>
        public event Action<ActiveEffect>? OnEffectApplied;
        /// <summary>效果移除事件。</summary>
        public event Action<ActiveEffect>? OnEffectRemoved;
        /// <summary>标签变化事件: (标签, 是否添加)。</summary>
        public event Action<GameplayTag, bool>? OnTagChanged;

        // ── 属性 ──

        /// <summary>获取属性当前值。</summary>
        public float GetAttributeValue(string name) => _attributes.GetCurrentValue(name);

        /// <summary>获取属性基础值。</summary>
        public float GetAttributeBaseValue(string name) => _attributes.GetBaseValue(name);

        // ── 标签 ──

        /// <summary>构建当前全部标签容器 (动态 + 效果授予)。</summary>
        public GameplayTagContainer BuildTagContainer()
        {
            var c = _dynamicTags.Clone();
            c.AddTags(_grantedTags);
            return c;
        }

        /// <summary>是否拥有标签 (层级匹配)。</summary>
        public bool HasTag(GameplayTag tag) => BuildTagContainer().HasMatchingTag(tag);

        /// <summary>是否拥有全部标签。</summary>
        public bool HasAllTags(GameplayTagContainer? all) => BuildTagContainer().HasAllTags(all);

        /// <summary>是否拥有任一标签。</summary>
        public bool HasAnyTag(GameplayTagContainer? any) => BuildTagContainer().HasAnyTag(any);

        /// <summary>添加动态标签。</summary>
        public void AddDynamicTags(GameplayTagContainer tags)
        {
            if (tags == null) return;
            foreach (var t in tags)
            {
                if (!t.IsValid) continue;
                if (!_dynamicTags.HasTag(t))
                {
                    _dynamicTags.Add(t);
                    OnTagChanged?.Invoke(t, true);
                }
            }
        }

        /// <summary>移除动态标签。</summary>
        public void RemoveDynamicTags(GameplayTagContainer tags)
        {
            if (tags == null) return;
            foreach (var t in tags)
            {
                if (!t.IsValid) continue;
                if (_dynamicTags.Remove(t)) OnTagChanged?.Invoke(t, false);
            }
        }

        // ── 效果施加 ──

        /// <summary>
        /// 施加一个效果到自身。
        /// </summary>
        /// <param name="effect">效果定义。</param>
        /// <param name="level">级别缩放 (默认 1)。</param>
        /// <returns>效果句柄, 0 表示未施加。</returns>
        public int ApplyEffect(GameplayEffect effect, float level = 1f)
        {
            if (effect == null) return 0;

            // 概率检查
            if (effect.ChanceToApply < 1f)
            {
                var rng = new Random();
                if (rng.NextDouble() > effect.ChanceToApply) return 0;
            }

            // 即时效果: 直接修改基础值
            if (effect.DurationPolicy == EEffectDurationPolicy.Instant)
            {
                ApplyInstantModifiers(effect, level);
                TriggerCues(effect.CueTagsOnApply, ECueEventType.OnApply);
                return _nextEffectHandle++;
            }

            // 持续/无限效果: 创建 ActiveEffect, 添加修改器
            var active = FindStackable(effect);
            if (active != null)
            {
                // 叠加
                if (effect.StackingPolicy == EStackingPolicy.StackByCount)
                {
                    if (active.Stacks < effect.MaxStacks) active.Stacks++;
                    active.ElapsedTime = 0f; // 刷新持续时间
                }
            }
            else
            {
                active = new ActiveEffect(effect);
                _activeEffects.Add(active);

                // 添加修改器到属性
                foreach (var mod in effect.Modifiers)
                {
                    var am = new AttributeModifier(
                        mod.Op switch
                        {
                            AttributeModifierOp.Add => YamaEngine.Gameplay.AttributeModifierOp.Add,
                            AttributeModifierOp.Multiply => YamaEngine.Gameplay.AttributeModifierOp.Multiply,
                            AttributeModifierOp.Divide => YamaEngine.Gameplay.AttributeModifierOp.Divide,
                            AttributeModifierOp.Override => YamaEngine.Gameplay.AttributeModifierOp.Override,
                            _ => YamaEngine.Gameplay.AttributeModifierOp.Add
                        },
                        mod.Magnitude * level, active.Handle);
                    _attributes.AddModifier(mod.AttributeName, am);
                }

                // 授予标签
                if (effect.GrantedTags.Count > 0)
                {
                    active.GrantedTags.AddTags(effect.GrantedTags);
                    foreach (var t in effect.GrantedTags)
                    {
                        if (!_grantedTags.HasTag(t))
                        {
                            _grantedTags.Add(t);
                            OnTagChanged?.Invoke(t, true);
                        }
                    }
                }

                // 初始化周期
                if (effect.Period > 0f)
                    active.NextPeriodTime = Time + effect.Period;
            }

            _attributes.RecalculateAll();
            OnEffectApplied?.Invoke(active);
            TriggerCues(effect.CueTagsOnApply, ECueEventType.OnApply);

            return active.Handle;
        }

        /// <summary>施加即时修改器到基础值。</summary>
        private void ApplyInstantModifiers(GameplayEffect effect, float level)
        {
            foreach (var mod in effect.Modifiers)
            {
                var attr = _attributes.GetAttribute(mod.AttributeName);
                if (attr == null) continue;
                float mag = mod.Magnitude * level;
                switch (mod.Op)
                {
                    case AttributeModifierOp.Add:
                        attr.SetBaseValue(attr.BaseValue + mag);
                        break;
                    case AttributeModifierOp.Multiply:
                        attr.SetBaseValue(attr.BaseValue * mag);
                        break;
                    case AttributeModifierOp.Divide:
                        if (mag != 0f) attr.SetBaseValue(attr.BaseValue / mag);
                        break;
                    case AttributeModifierOp.Override:
                        attr.SetBaseValue(mag);
                        break;
                }
            }
        }

        /// <summary>查找可叠加的同类效果。</summary>
        private ActiveEffect? FindStackable(GameplayEffect effect)
        {
            if (effect.StackingPolicy == EStackingPolicy.NoStack) return null;
            foreach (var ae in _activeEffects)
            {
                if (ae.Def == effect) return ae;
            }
            return null;
        }

        /// <summary>按句柄移除效果。</summary>
        public bool RemoveEffect(int handle)
        {
            for (int i = _activeEffects.Count - 1; i >= 0; i--)
            {
                var ae = _activeEffects[i];
                if (ae.Handle != handle) continue;

                // 移除修改器
                _attributes.RemoveModifiersFrom(handle);

                // 撤销授予标签
                foreach (var t in ae.GrantedTags)
                {
                    if (_grantedTags.Remove(t)) OnTagChanged?.Invoke(t, false);
                }

                // 触发移除 Cue
                TriggerCues(ae.Def.CueTagsOnRemove, ECueEventType.OnRemove);

                _activeEffects.RemoveAt(i);
                _attributes.RecalculateAll();
                OnEffectRemoved?.Invoke(ae);
                return true;
            }
            return false;
        }

        /// <summary>移除全部效果。</summary>
        public void RemoveAllEffects()
        {
            while (_activeEffects.Count > 0)
                RemoveEffect(_activeEffects[0].Handle);
        }

        /// <summary>当前活跃效果数。</summary>
        public int ActiveEffectCount => _activeEffects.Count;

        /// <summary>获取活跃效果快照。</summary>
        public IReadOnlyList<ActiveEffect> ActiveEffects => _activeEffects;

        // ── Cue 路由 ──

        /// <summary>触发匹配标签的 Cue。</summary>
        private void TriggerCues(GameplayTagContainer tags, ECueEventType eventType)
        {
            if (CueManager == null || tags == null || tags.Count == 0) return;
            foreach (var t in tags)
                CueManager.Trigger(t, eventType, WorldPosition);
        }

        // ── 每帧更新 ──

        /// <summary>
        /// 每帧更新: 推进效果时间, 处理周期执行与过期移除。
        /// 由 AbilitySystem (ECS ISystem) 调用。
        /// </summary>
        public void Tick(float dt)
        {
            Time += dt;

            for (int i = _activeEffects.Count - 1; i >= 0; i--)
            {
                var ae = _activeEffects[i];

                // Duration 策略: 推进时间
                if (ae.Def.DurationPolicy == EEffectDurationPolicy.Duration)
                {
                    ae.ElapsedTime += dt;

                    // 周期执行
                    if (ae.Def.Period > 0f && Time >= ae.NextPeriodTime)
                    {
                        ApplyInstantModifiers(ae.Def, ae.Stacks);
                        _attributes.RecalculateAll();
                        ae.NextPeriodTime += ae.Def.Period;
                        TriggerCues(ae.Def.CueTagsOnApply, ECueEventType.OnPeriod);
                    }

                    // 过期移除
                    if (ae.IsExpired)
                    {
                        RemoveEffect(ae.Handle);
                        continue;
                    }
                }

                // WhileActive Cue (持续触发)
                if (ae.Def.CueTagsOnApply.Count > 0)
                    TriggerCues(ae.Def.CueTagsOnApply, ECueEventType.WhileActive);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. ECS 集成 —— 组件数据与更新系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ECS 组件 —— 标记实体拥有 ASC, 持有运行时引用。
    /// </summary>
    public struct AbilitySystemComponentData : IComponent
    {
        /// <summary>ASC 运行时实例 (由外部创建并注入)。</summary>
        public AbilitySystemComponent? ASC;
        /// <summary>是否启用 Tick。</summary>
        public bool TickEnabled;
    }

    /// <summary>
    /// ECS 更新系统 —— 每帧驱动所有拥有 ASC 的实体。
    /// </summary>
    public sealed class AbilitySystem : ISystem
    {
        /// <summary>系统名。</summary>
        public string Name => "EffectsAbilitySystem";

        private readonly GameplayCueManager _cueManager;

        /// <summary>创建系统。</summary>
        /// <param name="cueManager">全局 Cue 管理器。</param>
        public AbilitySystem(GameplayCueManager cueManager)
        {
            _cueManager = cueManager ?? throw new ArgumentNullException(nameof(cueManager));
        }

        /// <summary>每帧更新。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;
            var entities = ecs.QueryWithComponent<AbilitySystemComponentData>();
            foreach (var eid in entities)
            {
                var data = ecs.GetComponent<AbilitySystemComponentData>(eid);
                if (!data.TickEnabled || data.ASC == null) continue;

                // 同步实体世界位置到 ASC (用于 Cue 触发)
                if (ecs.HasComponent<TransformComponent>(eid))
                {
                    var tf = ecs.GetComponent<TransformComponent>(eid);
                    data.ASC.WorldPosition = tf.Position;
                    data.ASC.EntityId = eid;
                    ecs.SetComponent(eid, data);
                }

                data.ASC.Tick(dt);
            }
        }
    }
}
