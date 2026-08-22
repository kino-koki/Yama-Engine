using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ShanyuEngine.ECS;
using ShanyuEngine.Animation;
using ShanyuEngine.Gameplay.Effects;

namespace ShanyuEngine.Gameplay
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 游戏能力系统 (Gameplay Ability System, GAS)
    //
    // 本文件对齐 Unreal Engine 5 的 Gameplay Ability System 插件, 实现一套
    // 数据驱动的玩法能力框架, 涵盖:
    //   1. Gameplay Tag 系统     —— 层级标签与父标签匹配查询
    //   2. Attribute 系统        —— 属性集合 / 修改器 / 变化事件
    //   3. Gameplay Effect 系统  —— 即时/持续/无限/周期效果, 叠加, 免疫, 标签要求
    //   4. Gameplay Ability 系统 —— 能力生命周期 / 异步任务 / 状态机 / 输入绑定
    //   5. Ability System Component (ASC) —— 每实体属性/效果/能力/标签管理
    //   6. Gameplay Event 系统   —— 事件数据 / 标签路由 / 预置事件
    //
    // 对接系统:
    //   * ECS: EntityComponentSystem / IComponent / ISystem (见 AbilitySystemComponentData / AbilitySystemUpdateSystem)
    //   * 动画: ShanyuEngine.Animation.Animator (见 IAbilityAnimationBridge / AnimatorAbilityBridge)
    //   * 网络: 通过 IAbilitySystemNetworkBridge 接口对接 Networking.NetworkReplicationSystem
    //   * Gameplay 框架: 通过 AbilitySystemActorComponent 挂载到 Actor
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. Gameplay Tag 系统
    //    层级标签以 '.' 分隔, 例如 "Status.Burning.DamageOverTime"。
    //    父标签匹配: 子标签 MatchesTag(父标签) 为 true, 例如
    //      "Status.Burning.DamageOverTime".MatchesTag("Status.Burning") == true
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Gameplay 标签 —— 一个对已注册层级标签节点的轻量引用。
    /// 标签字符串以 '.' 分隔层级, 不可为空、不可含空格、不可连续 '.'。
    /// </summary>

    /// <summary>
    /// 标签容器 —— 持有一组 GameplayTag, 支持 HasAll / HasAny / HasMatchingTag 查询。
    /// 内部用有序集合去重, 支持引用计数式批量增删。
    /// </summary>

    /// <summary>
    /// 标签树节点 —— 维护父子关系, 用于注册/查询/动态标签管理。
    /// </summary>

    /// <summary>
    /// 标签管理器 —— 全局静态标签树, 负责注册、查询与动态标签管理。
    /// 所有 GameplayTag 在使用前应通过 Register 注册 (Parse 会自动注册)。
    /// </summary>
    public static class GameplayTagManager
    {
        private static readonly Dictionary<string, GameplayTagNode> _nodes = new(StringComparer.Ordinal);
        private static readonly object _lock = new();

        /// <summary>注册一个原生标签 (带线程安全)。</summary>
        public static GameplayTag RegisterNative(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return GameplayTag.None;
            string trimmed = tagName.Trim();
            lock (_lock)
            {
                EnsureNodeRecursive(trimmed);
            }
            return new GameplayTag(trimmed);
        }

        /// <summary>批量注册标签。</summary>
        public static void RegisterMany(IEnumerable<string> tagNames)
        {
            if (tagNames == null) return;
            lock (_lock)
            {
                foreach (var n in tagNames)
                    if (!string.IsNullOrWhiteSpace(n)) EnsureNodeRecursive(n.Trim());
            }
        }

        /// <summary>查询标签是否已注册。</summary>
        public static bool IsRegistered(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return false;
            lock (_lock) { return _nodes.ContainsKey(tagName); }
        }

        /// <summary>请求已注册标签的节点。</summary>
        public static GameplayTagNode? FindNode(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return null;
            lock (_lock) { return _nodes.TryGetValue(tagName, out var n) ? n : null; }
        }

        /// <summary>获取指定标签的直接子标签 (不含后代)。</summary>
        public static List<GameplayTag> GetDirectChildren(string tagName)
        {
            var result = new List<GameplayTag>();
            var node = FindNode(tagName);
            if (node == null) return result;
            lock (_lock)
            {
                foreach (var c in node.Children) result.Add(new GameplayTag(c.CompleteName));
            }
            return result;
        }

        /// <summary>递归获取所有后代标签。</summary>
        public static List<GameplayTag> GetAllDescendants(string tagName)
        {
            var result = new List<GameplayTag>();
            var node = FindNode(tagName);
            if (node == null) return result;
            CollectDescendants(node, result);
            return result;
        }

        private static void CollectDescendants(GameplayTagNode node, List<GameplayTag> into)
        {
            foreach (var c in node.Children)
            {
                into.Add(new GameplayTag(c.CompleteName));
                CollectDescendants(c, into);
            }
        }

        /// <summary>递归创建标签节点链 (父节点不存在则一并创建)。</summary>
        private static GameplayTagNode EnsureNodeRecursive(string tagName)
        {
            if (_nodes.TryGetValue(tagName, out var existing)) return existing;
            int idx = tagName.LastIndexOf('.');
            GameplayTagNode? parent = null;
            string simple = tagName;
            if (idx > 0)
            {
                string parentName = tagName.Substring(0, idx);
                parent = EnsureNodeRecursive(parentName);
                simple = tagName.Substring(idx + 1);
            }
            var node = new GameplayTagNode(tagName, simple, parent);
            _nodes[tagName] = node;
            parent?.Children.Add(node);
            return node;
        }

        /// <summary>已注册标签总数。</summary>
        public static int RegisteredCount
        {
            get { lock (_lock) return _nodes.Count; }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 2. Attribute 系统
    //    属性集合包含 Health/MaxHealth/Mana/MaxMana/Stamina/Attack/
    //    Defense/Speed/CritRate/CritDamage 等常见战斗属性。
    //    修改器有 Add/Multiply/Override 三种运算, 变化时触发事件。
    // ──────────────────────────────────────────────────────────────

    /// <summary>属性修改器运算类型。</summary>
    public enum EAttributeModifierOp
    {
        /// <summary>加法: NewValue = CurrentValue + Magnitude。</summary>
        Add = 0,
        /// <summary>乘法: NewValue = CurrentValue * Magnitude。</summary>
        Multiply = 1,
        /// <summary>覆盖: NewValue = Magnitude (取最后施加者)。</summary>
        Override = 2
    }

    /// <summary>属性修改器: 描述对一个属性的运算与量值。</summary>

    /// <summary>单个属性的运行时数据。</summary>

    /// <summary>属性变化信息, 随事件分发。</summary>
    public struct AttributeChangeInfo
    {
        /// <summary>所属实体 Id。</summary>
        public int EntityId;
        /// <summary>属性名。</summary>
        public string AttributeName;
        /// <summary>旧当前值。</summary>
        public float OldValue;
        /// <summary>新当前值。</summary>
        public float NewValue;
        /// <summary>基础值 (变化后)。</summary>
        public float BaseValue;
        /// <summary>变化量。</summary>
        public float Delta => NewValue - OldValue;
    }

    /// <summary>
    /// 属性集合 —— 一个 ASC 可挂载多个 AttributeSet, 每个集合持有若干属性。
    /// 提供默认战斗属性, 并在属性变化时分发 <see cref="OnAttributeChanged"/> 事件。
    /// </summary>
    public class AttributeSet
    {
        private readonly Dictionary<string, GameplayAttribute> _attrs = new(StringComparer.Ordinal);

        /// <summary>属性变化事件。参数为变化信息。</summary>
        public event Action<AttributeChangeInfo>? OnAttributeChanged;

        /// <summary>所属 ASC (可为空, 便于独立测试)。</summary>
        public AbilitySystemComponent? Owner { get; set; }

        public AttributeSet() { InitDefaults(); }

        /// <summary>初始化默认战斗属性。子类可重写以定制。</summary>
        protected virtual void InitDefaults()
        {
            Add("Health", 100f);
            Add("MaxHealth", 100f);
            Add("Mana", 50f);
            Add("MaxMana", 50f);
            Add("Stamina", 100f);
            Add("MaxStamina", 100f);
            Add("Attack", 10f);
            Add("Defense", 5f);
            Add("Speed", 600f);
            Add("CritRate", 0.05f);
            Add("CritDamage", 1.5f);
            Add("MoveSpeed", 600f);
            Add("AttackSpeed", 1f);
        }

        /// <summary>添加一个属性 (初始基础值=当前值)。</summary>
        public void Add(string name, float baseValue)
        {
            if (string.IsNullOrEmpty(name)) return;
            _attrs[name] = new GameplayAttribute { Name = name, BaseValue = baseValue, CurrentValue = baseValue };
        }

        /// <summary>是否含某属性。</summary>
        public bool Has(string name) => !string.IsNullOrEmpty(name) && _attrs.ContainsKey(name);

        /// <summary>获取属性结构 (不存在返回默认)。</summary>
        public GameplayAttribute Get(string name)
            => _attrs.TryGetValue(name ?? string.Empty, out var a) ? a : default;

        /// <summary>获取当前值。</summary>
        public float GetCurrentValue(string name)
            => _attrs.TryGetValue(name ?? string.Empty, out var a) ? a.CurrentValue : 0f;

        /// <summary>获取基础值。</summary>
        public float GetBaseValue(string name)
            => _attrs.TryGetValue(name ?? string.Empty, out var a) ? a.BaseValue : 0f;

        /// <summary>直接设置基础值 (用于即时 Add 效果或初始化)。</summary>
        public void SetBaseValue(string name, float value)
        {
            if (!_attrs.TryGetValue(name ?? string.Empty, out var a)) return;
            a.BaseValue = value;
            _attrs[name] = a;
        }

        /// <summary>
        /// 设置当前值 (持续效果聚合后由 ASC 调用), 触发变化事件。
        /// </summary>
        public void SetCurrentValue(string name, float value, int entityId = 0)
        {
            if (!_attrs.TryGetValue(name ?? string.Empty, out var a))
            {
                FireChanged(entityId, name, 0f, value, 0f);
                return;
            }
            float old = a.CurrentValue;
            if (MathF.Abs(old - value) < 1e-7f) return;
            a.CurrentValue = value;
            _attrs[name] = a;
            FireChanged(entityId, name, old, value, a.BaseValue);
        }

        private void FireChanged(int entityId, string name, float old, float neu, float baseVal)
        {
            OnAttributeChanged?.Invoke(new AttributeChangeInfo
            {
                EntityId = entityId,
                AttributeName = name,
                OldValue = old,
                NewValue = neu,
                BaseValue = baseVal
            });
        }

        /// <summary>枚举所有属性名。</summary>
        public IEnumerable<string> AttributeNames => _attrs.Keys;

        /// <summary>属性数量。</summary>
        public int Count => _attrs.Count;
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Gameplay Effect 系统
    //    效果分三种持续策略: Instant(即时) / Duration(持续X秒) / Infinite(无限)。
    //    周期效果 (Periodic) 按固定间隔施加即时修改器 (如中毒每秒掉血)。
    //    叠加规则: NoStack / AggregateByTarget / RefreshDuration。
    //    标签要求/排斥/免疫决定效果能否施加到目标。
    // ──────────────────────────────────────────────────────────────

    /// <summary>效果持续策略。</summary>
    public enum EEffectDurationPolicy
    {
        /// <summary>即时: 立刻修改基础值, 不驻留。</summary>
        Instant = 0,
        /// <summary>持续: 驻留指定秒数后自动移除。</summary>
        HasDuration = 1,
        /// <summary>无限: 持续存在直到被显式移除。</summary>
        Infinite = 2
    }

    /// <summary>效果叠加策略。</summary>
    public enum EStackingPolicy
    {
        /// <summary>不叠加: 每次施加都是独立实例。</summary>
        NoStack = 0,
        /// <summary>按目标叠加: 同源同类效果叠加层数, 直到上限。</summary>
        AggregateByTarget = 1,
        /// <summary>刷新持续时间: 重置计时器但不增加层数。</summary>
        RefreshDuration = 2
    }

    /// <summary>
    /// 可缩放浮点 —— 量值可由等级查表 (曲线) 缩放, 用于随能力等级增长的效果。
    /// </summary>
    public struct ScalableFloat
    {
        /// <summary>基础量值。</summary>
        public float BaseValue;
        /// <summary>可选等级曲线 (等级->倍率); 为空则直接用 BaseValue。</summary>
        public Func<float, float>? Curve;

        public ScalableFloat(float baseValue) { BaseValue = baseValue; Curve = null; }
        public ScalableFloat(float baseValue, Func<float, float> curve) { BaseValue = baseValue; Curve = curve; }

        /// <summary>按等级求值。</summary>
        public float Evaluate(float level)
        {
            if (Curve == null) return BaseValue;
            return BaseValue * Curve(level);
        }
    }

    /// <summary>效果修改器: 作用属性 + 运算 + 可缩放量值。</summary>
    public struct EffectModifier
    {
        /// <summary>作用属性名。</summary>
        public string AttributeName;
        /// <summary>运算类型。</summary>
        public EAttributeModifierOp Op;
        /// <summary>量值 (随等级缩放)。</summary>
        public ScalableFloat Magnitude;

        public EffectModifier(string attr, EAttributeModifierOp op, float magnitude)
        {
            AttributeName = attr;
            Op = op;
            Magnitude = new ScalableFloat(magnitude);
        }
    }

    /// <summary>效果施加上下文 —— 记录来源/施法者/触发能力等元数据。</summary>
    public class EffectContext
    {
        /// <summary>施法者 ASC (产生效果的一方)。</summary>
        public AbilitySystemComponent? Instigator;
        /// <summary>效果来源 ASC (承载效果的一方, 通常等于 Instigator)。</summary>
        public AbilitySystemComponent? Source;
        /// <summary>触发该效果的 GameplayAbility (可为空)。</summary>
        public GameplayAbility? SourceAbility;
        /// <summary>施法者实体 Id。</summary>
        public int InstigatorEntityId;
        /// <summary>可选载荷。</summary>
        public object? OptionalPayload;
        /// <summary>世界空间作用位置 (可选)。</summary>
        public Vector3? Location;

        public static EffectContext Create(AbilitySystemComponent? instigator, GameplayAbility? ability = null)
        {
            return new EffectContext
            {
                Instigator = instigator,
                Source = instigator,
                SourceAbility = ability,
                InstigatorEntityId = instigator?.EntityId ?? 0
            };
        }
    }

    /// <summary>
    /// 效果标签集合 —— 区分授予标签、施加要求/排斥标签、免疫标签。
    /// </summary>
    public class GameplayEffectTags
    {
        /// <summary>效果激活期间授予目标的标签 (移除时回收)。</summary>
        public GameplayTagContainer Granted = new();
        /// <summary>施加时目标必须拥有的全部标签。</summary>
        public GameplayTagContainer ApplicationRequired = new();
        /// <summary>施加时目标必须不拥有其中任意标签。</summary>
        public GameplayTagContainer ApplicationBlocked = new();
        /// <summary>持续期间目标必须持续拥有的标签, 一旦失去则效果移除。</summary>
        public GameplayTagContainer OngoingRequired = new();
        /// <summary>免疫标签: 目标拥有其中任意标签则该效果被免疫。</summary>
        public GameplayTagContainer Immunity = new();
    }

    /// <summary>
    /// GameplayEffect —— 效果定义 (数据模板, 不可变)。
    /// 描述持续策略、修改器、周期、叠加、标签要求与免疫。
    /// </summary>
    public class GameplayEffect
    {
        /// <summary>效果名 (调试/堆叠分组用)。</summary>
        public string Name = "Effect";
        /// <summary>持续策略。</summary>
        public EEffectDurationPolicy DurationPolicy = EEffectDurationPolicy.HasDuration;
        /// <summary>持续时间 (秒, 仅 HasDuration 有效)。</summary>
        public float Duration = 5f;
        /// <summary>周期 (秒, >0 时为周期效果, 周期到达施加一次即时修改器)。</summary>
        public float Period = 0f;
        /// <summary>修改器列表。</summary>
        public List<EffectModifier> Modifiers = new();
        /// <summary>标签集合。</summary>
        public GameplayEffectTags Tags = new();
        /// <summary>叠加策略。</summary>
        public EStackingPolicy StackingPolicy = EStackingPolicy.NoStack;
        /// <summary>最大叠加层数 (AggregateByTarget 有效)。</summary>
        public int MaxStacks = 1;
        /// <summary>用于堆叠分组的标签 (同组才叠加)。</summary>
        public GameplayTag StackingTag = GameplayTag.None;
        /// <summary>效果优先级 (数值越大越后施加, 用于 Override 排序)。</summary>
        public float Priority;

        /// <summary>快速构造一个即时加法效果。</summary>
        public static GameplayEffect CreateInstantAdd(string attr, float magnitude, string name = "InstantAdd")
        {
            var e = new GameplayEffect { Name = name, DurationPolicy = EEffectDurationPolicy.Instant };
            e.Modifiers.Add(new EffectModifier(attr, EAttributeModifierOp.Add, magnitude));
            return e;
        }

        /// <summary>快速构造一个持续加法效果。</summary>
        public static GameplayEffect CreateDurationAdd(string attr, float magnitude, float duration, string name = "DurationAdd")
        {
            var e = new GameplayEffect { Name = name, DurationPolicy = EEffectDurationPolicy.HasDuration, Duration = duration };
            e.Modifiers.Add(new EffectModifier(attr, EAttributeModifierOp.Add, magnitude));
            return e;
        }

        /// <summary>快速构造一个周期伤害效果 (如中毒)。</summary>
        public static GameplayEffect CreatePeriodicDamage(string attr, float perTick, float period, float duration, string name = "Poison")
        {
            var e = new GameplayEffect { Name = name, DurationPolicy = EEffectDurationPolicy.HasDuration, Duration = duration, Period = period };
            e.Modifiers.Add(new EffectModifier(attr, EAttributeModifierOp.Add, perTick));
            e.Tags.Granted.Add("Status.Poisoned");
            return e;
        }
    }

    /// <summary>
    /// 效果实例 —— GameplayEffect 在施加时创建的快照, 含等级/上下文/运行时计时。
    /// </summary>
    public class EffectSpec
    {
        /// <summary>效果定义。</summary>
        public GameplayEffect Def;
        /// <summary>等级 (影响可缩放量值)。</summary>
        public float Level = 1f;
        /// <summary>上下文。</summary>
        public EffectContext Context;
        /// <summary>创建时间 (秒, 相对 ASC 时间轴)。</summary>
        public float StartTime;
        /// <summary>剩余持续时间 (秒)。</summary>
        public float DurationRemaining;
        /// <summary>下次周期触发时间 (秒)。</summary>
        public float NextPeriodTime;
        /// <summary>当前叠加层数。</summary>
        public int StackCount = 1;
        /// <summary>是否已被移除。</summary>
        public bool IsValid = true;

        public EffectSpec(GameplayEffect def, EffectContext context, float level = 1f)
        {
            Def = def;
            Context = context;
            Level = level;
            DurationRemaining = def.DurationPolicy == EEffectDurationPolicy.HasDuration ? def.Duration : 0f;
        }

        /// <summary>计算某修改器在该等级下的量值。</summary>
        public float CalcMagnitude(EffectModifier mod) => mod.Magnitude.Evaluate(Level);

        /// <summary>是否为持续/无限 (驻留型) 效果。</summary>
        public bool IsPersistent => Def.DurationPolicy != EEffectDurationPolicy.Instant;

        /// <summary>是否为周期效果。</summary>
        public bool IsPeriodic => Def.Period > 0f;
    }

    /// <summary>
    /// 活跃效果 —— ASC 持有的运行时效果包装, 管理计时与生命周期。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 4. Gameplay Ability 系统
    //    能力基类拥有 Activate/Cancel/EndAbility 生命周期, 内部可启动
    //    异步 AbilityTask。状态机: Idle/Activating/Active/Cooldown/WaitingConfirm。
    //    激活前检查标签要求、资源消耗与冷却。
    // ──────────────────────────────────────────────────────────────

    /// <summary>能力运行状态。</summary>
    public enum EAbilityState
    {
        /// <summary>空闲, 可激活。</summary>
        Idle = 0,
        /// <summary>正在激活 (Commit 前)。</summary>
        Activating = 1,
        /// <summary>激活中 (已 Commit, 正在执行)。</summary>
        Active = 2,
        /// <summary>冷却中。</summary>
        Cooldown = 3,
        /// <summary>等待确认 (目标选择/蓄力)。</summary>
        WaitingConfirm = 4,
        /// <summary>已结束 (过渡态, 下一帧回到 Idle)。</summary>
        Ended = 5
    }

    /// <summary>
    /// 能力标签集合 —— 控制能力的激活条件与互斥。
    /// </summary>
    public class AbilityTags
    {
        /// <summary>该能力本身的标签 (标识)。</summary>
        public GameplayTagContainer Ability = new();
        /// <summary>激活时授予拥有者的标签 (激活期间持有)。</summary>
        public GameplayTagContainer ActivationOwned = new();
        /// <summary>拥有者必须拥有的标签才能激活。</summary>
        public GameplayTagContainer ActivationRequired = new();
        /// <summary>拥有者拥有其中任意标签则不能激活。</summary>
        public GameplayTagContainer ActivationBlocked = new();
        /// <summary>与该能力互斥的能力标签 (拥有这些标签的能力不能同时激活)。</summary>
        public GameplayTagContainer BlockAbilitiesWithTag = new();
        /// <summary>该能力激活时取消拥有这些标签的其他能力。</summary>
        public GameplayTagContainer CancelAbilitiesWithTag = new();
    }

    /// <summary>
    /// 能力异步任务基类。任务由 ASC 每帧 Tick, 完成时分发 <see cref="Completed"/>。
    /// 任务可在能力结束时被一并取消。模仿 UE AbilityTask 的回调式异步。
    /// </summary>

    /// <summary>
    /// 等待目标数据任务: 等待外部提供目标数据 (如点击位置/选中实体)。
    /// </summary>
    public sealed class WaitTargetDataTask : AbilityTask
    {
        /// <summary>目标数据。</summary>
        public object? TargetData;
        private float _timeout;
        private float _elapsed;

        public WaitTargetDataTask(float timeoutSeconds = 5f) { _timeout = timeoutSeconds; }

        /// <summary>外部设置目标数据并完成任务。</summary>
        public void ProvideTargetData(object data)
        {
            TargetData = data;
            Finish();
        }

        protected override void OnActivate() { _elapsed = 0f; }

        public new void Tick(float dt)
        {
            if (!IsActive) return;
            _elapsed += dt;
            if (_timeout > 0 && _elapsed >= _timeout)
            {
                TargetData = null;
                Finish();
            }
        }
    }

    /// <summary>
    /// 等待输入释放任务: 等待指定输入按键被释放 (用于蓄力/长按)。
    /// </summary>
    public sealed class WaitInputReleaseTask : AbilityTask
    {
        private readonly int _inputId;
        private bool _released;
        private float _holdTime;

        public float TotalHoldTime => _holdTime;

        public WaitInputReleaseTask(int inputId) { _inputId = inputId; }

        /// <summary>外部通知输入已释放。</summary>
        public void NotifyReleased()
        {
            _released = true;
            Finish();
        }

        protected override void OnActivate() { _holdTime = 0f; _released = false; }

        public new void Tick(float dt)
        {
            if (!IsActive) return;
            _holdTime += dt;
            // 兜底: 若 ASC 标记该输入已释放, 也结束
            if (_released) Finish();
        }
    }

    /// <summary>
    /// 播放蒙太奇任务: 通过动画桥接播放一段蒙太奇 (动画状态), 完成时回调。
    /// </summary>
    public sealed class PlayMontageTask : AbilityTask
    {
        private readonly string _montageName;
        private readonly float _playRate;
        private readonly float _blendIn;
        private float _playedDuration;
        private float _montageLength;

        public PlayMontageTask(string montageName, float playRate = 1f, float blendIn = 0.2f)
        {
            _montageName = montageName;
            _playRate = playRate;
            _blendIn = blendIn;
        }

        protected override void OnActivate()
        {
            var bridge = OwnerASC.AnimationBridge;
            if (bridge == null) { Finish(); return; }
            _montageLength = bridge.PlayMontage(_montageName, _playRate, _blendIn);
            _playedDuration = 0f;
            if (_montageLength <= 0f) Finish();
        }

        public new void Tick(float dt)
        {
            if (!IsActive) return;
            _playedDuration += dt * _playRate;
            if (_playedDuration >= _montageLength) Finish();
        }

        protected override void OnCancel()
        {
            OwnerASC.AnimationBridge?.StopMontage(0.2f);
        }
    }

    /// <summary>
    /// 移动到目标任务: 每帧将拥有者向目标位置移动, 到达后完成。
    /// 通过 ECS 写回 TransformComponent。
    /// </summary>
    public sealed class MoveToTargetTask : AbilityTask
    {
        private readonly Vector3 _destination;
        private readonly float _speed;
        private readonly float _acceptance;

        public MoveToTargetTask(Vector3 destination, float speed, float acceptance = 0.1f)
        {
            _destination = destination;
            _speed = speed;
            _acceptance = acceptance;
        }

        protected override void OnActivate() { }

        public new void Tick(float dt)
        {
            if (!IsActive || OwnerASC.ECS == null) return;
            int eid = OwnerASC.EntityId;
            if (!OwnerASC.ECS.HasComponent<TransformComponent>(eid)) { Finish(); return; }
            var tf = OwnerASC.ECS.GetComponent<TransformComponent>(eid);
            Vector3 to = _destination - tf.Position;
            float dist = to.Length();
            if (dist <= _acceptance)
            {
                tf.Position = _destination;
                OwnerASC.ECS.SetComponent(eid, tf);
                Finish();
                return;
            }
            Vector3 step = Vector3.Normalize(to) * _speed * dt;
            if (step.Length() >= dist) tf.Position = _destination;
            else tf.Position = tf.Position + step;
            OwnerASC.ECS.SetComponent(eid, tf);
        }
    }

    /// <summary>
    /// GameplayAbility —— 能力基类。定义激活/取消/结束生命周期与条件检查。
    /// 子类重写 <see cref="OnActivateAbility"/> 实现具体逻辑。
    /// </summary>
    public abstract class GameplayAbility
    {
        /// <summary>能力名。</summary>
        public string Name = "Ability";
        /// <summary>能力标签。</summary>
        public AbilityTags Tags = new();
        /// <summary>冷却效果 (激活时施加到自身)。</summary>
        public GameplayEffect? CooldownEffect;
        /// <summary>消耗效果 (激活时即时施加, 通常扣蓝/扣耐力)。</summary>
        public GameplayEffect? CostEffect;
        /// <summary>默认输入 Id (绑定到 ASC 输入)。</summary>
        public int InputId = -1;
        /// <summary>是否需要确认 (目标选择类能力)。</summary>
        public bool NeedsConfirmation;

        /// <summary>检查能否激活: 标签要求 / 冷却 / 资源消耗。</summary>
        public virtual bool CanActivateAbility(AbilitySystemComponent asc, EffectContext? context = null)
        {
            if (asc == null) return false;
            // 标签要求: 拥有者必须拥有 ActivationRequired 全部标签
            if (!asc.HasAll(Tags.ActivationRequired)) return false;
            // 标签排斥: 拥有者拥有 ActivationBlocked 任一标签则不可
            if (asc.HasAny(Tags.ActivationBlocked)) return false;
            // 冷却检查: 若存在冷却标签则视为冷却中
            if (IsOnCooldown(asc)) return false;
            // 资源消耗检查: 预览 CostEffect 是否会使属性越界
            if (CostEffect != null && !CanAffordCost(asc)) return false;
            return true;
        }

        /// <summary>是否处于冷却 (拥有冷却标签)。</summary>
        public bool IsOnCooldown(AbilitySystemComponent asc)
        {
            if (CooldownEffect == null) return false;
            // 冷却效果授予的标签作为冷却标记
            foreach (var mod in CooldownEffect.Tags.Granted)
                if (asc.HasMatchingTag(mod)) return true;
            return false;
        }

        /// <summary>预览能否承受消耗 (简单: 不让属性跌破 0)。</summary>
        protected virtual bool CanAffordCost(AbilitySystemComponent asc)
        {
            if (CostEffect == null) return true;
            foreach (var m in CostEffect.Modifiers)
            {
                if (m.Op != EAttributeModifierOp.Add) continue;
                float cur = asc.GetAttributeValue(m.AttributeName);
                float cost = m.Magnitude.Evaluate(1f);
                if (cur + cost < 0f) return false;
            }
            return true;
        }

        /// <summary>激活能力入口 (由 ASC 调用)。</summary>
        public void ActivateAbility(AbilitySystemComponent asc, EffectContext? context = null)
        {
            var ctx = context ?? EffectContext.Create(asc, this);
            // 提交: 施加消耗与冷却, 授予激活标签
            CommitAbility(asc, ctx);
            OnActivateAbility(asc, ctx);
        }

        /// <summary>提交能力: 施加消耗与冷却效果, 授予激活标签。</summary>
        public virtual void CommitAbility(AbilitySystemComponent asc, EffectContext context)
        {
            if (CostEffect != null)
                asc.ApplyEffectToSelf(CostEffect, context, 1f);
            if (CooldownEffect != null)
                asc.ApplyEffectToSelf(CooldownEffect, context, 1f);
            asc.AddDynamicTags(Tags.ActivationOwned);
        }

        /// <summary>子类实现激活逻辑 (启动任务/施加效果等)。</summary>
        protected abstract void OnActivateAbility(AbilitySystemComponent asc, EffectContext context);

        /// <summary>取消能力 (先执行子类取消逻辑, 再委托 ASC 完成清理与状态切换)。</summary>
        public virtual void CancelAbility(AbilitySystemComponent asc, EffectContext? context = null)
        {
            OnCancelAbility(asc);
            asc.CancelAbilityByDef(this);
        }

        /// <summary>子类取消逻辑 (在 ASC 清理前调用)。</summary>
        protected virtual void OnCancelAbility(AbilitySystemComponent asc) { }

        /// <summary>结束能力 (正常结束或被取消时调用, 委托 ASC 完成清理与状态切换)。</summary>
        public virtual void EndAbility(AbilitySystemComponent asc, EffectContext? context = null)
        {
            asc.EndAbilityByDef(this);
        }

        /// <summary>由能力启动一个任务并注册到 ASC。</summary>
        protected T StartTask<T>(AbilitySystemComponent asc) where T : AbilityTask, new()
        {
            var task = new T();
            task.Init(this, asc);
            asc.RegisterAbilityTask(task);
            task.Activate();
            return task;
        }
    }

    /// <summary>
    /// 能力实例 —— 一个被赋予的能力的运行时数据, 含等级/输入/状态/活跃任务。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 5. Ability System Component (ASC)
    //    为每个拥有 ASC 的实体管理属性 / 效果 / 能力 / 标签。
    //    提供 ApplyEffectToTarget / ApplyEffectToSelf / TryActivateAbility,
    //    以及 OnAttributeChanged / OnGameplayEffectApplied / OnTagChanged 事件。
    //    通过接口与 ECS / 动画 / 网络对接。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画桥接接口 —— 能力系统通过它播放蒙太奇/获取根运动, 解耦具体动画实现。
    /// 默认实现 <see cref="AnimatorAbilityBridge"/> 包装 ShanyuEngine.Animation.Animator。
    /// </summary>
    public interface IAbilityAnimationBridge
    {
        /// <summary>播放蒙太奇, 返回其时长 (秒)。</summary>
        float PlayMontage(string montageName, float rate = 1f, float blendIn = 0.2f);
        /// <summary>停止当前蒙太奇。</summary>
        void StopMontage(float blendOut = 0.2f);
        /// <summary>指定蒙太奇是否正在播放。</summary>
        bool IsMontagePlaying(string montageName);
        /// <summary>获取当前蒙太奇播放进度 [0,1]。</summary>
        float GetMontageProgress(string montageName);
        /// <summary>消费并清空本帧根运动位移增量。</summary>
        Vector3 ConsumeRootMotionDelta();
    }

    /// <summary>
    /// 基于 Animator 的动画桥接实现。将蒙太奇名映射为 Animator 状态。
    /// </summary>
    public sealed class AnimatorAbilityBridge : IAbilityAnimationBridge
    {
        private readonly Animator _animator;
        private string _currentMontage = string.Empty;
        private float _currentDuration;

        public AnimatorAbilityBridge(Animator animator)
        {
            _animator = animator ?? throw new ArgumentNullException(nameof(animator));
        }

        public float PlayMontage(string montageName, float rate = 1f, float blendIn = 0.2f)
        {
            _currentMontage = montageName;
            // 利用 Animator 的 CrossFade 进行混合过渡
            _animator.CrossFade(montageName, MathF.Max(0.01f, blendIn));
            // Animator 未直接暴露片段长度, 这里用经验值占位, 实际可查 AnimationClip.Duration
            _currentDuration = 1.0f;
            return _currentDuration;
        }

        public void StopMontage(float blendOut = 0.2f)
        {
            // 回到 Idle 状态 (假设已注册 "Idle")
            _animator.CrossFade("Idle", MathF.Max(0.01f, blendOut));
            _currentMontage = string.Empty;
        }

        public bool IsMontagePlaying(string montageName)
            => string.Equals(_animator.CurrentStateName, montageName, StringComparison.OrdinalIgnoreCase);

        public float GetMontageProgress(string montageName)
            => IsMontagePlaying(montageName) ? 0f : 0f;

        public Vector3 ConsumeRootMotionDelta()
        {
            if (!_animator.ApplyRootMotion) return Vector3.Zero;
            var delta = _animator.RootMotion.PositionDelta;
            _animator.ResetRootMotion();
            return delta;
        }
    }

    /// <summary>
    /// 网络桥接接口 —— ASC 通过它发起 RPC (服务器激活 / 多播施加效果)。
    /// 由 Networking.NetworkReplicationSystem 提供实现。
    /// </summary>
    public interface IAbilitySystemNetworkBridge
    {
        /// <summary>本端是否为服务器权威端。</summary>
        bool IsServer { get; }
        /// <summary>客户端请求服务器激活能力。</summary>
        void Server_TryActivateAbility(int entityId, int abilityHandle, EffectContext context);
        /// <summary>服务器多播: 某实体被施加了效果。</summary>
        void Multicast_ApplyEffect(int targetEntityId, GameplayEffect effect, EffectContext context, float level);
        /// <summary>服务器多播: 某实体的属性发生变化。</summary>
        void Multicast_AttributeChanged(int entityId, string attr, float newValue);
        /// <summary>服务器多播: 某实体标签变化。</summary>
        void Multicast_TagChanged(int entityId, string tag, bool added);
    }

    /// <summary>
    /// Ability System Component —— 能力系统核心组件, 每实体一个。
    /// 管理属性集合、活跃效果、已赋予能力、动态标签, 驱动效果与能力每帧更新。
    /// </summary>
    public class AbilitySystemComponent
    {
        private readonly List<AttributeSet> _attributeSets = new();
        private readonly List<ActiveEffect> _activeEffects = new();
        private readonly Dictionary<int, AbilitySpec> _abilities = new();
        private readonly Dictionary<string, int> _tagRefCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _dynamicTagRefCounts = new(StringComparer.Ordinal);
        private readonly List<AbilityTask> _looseTasks = new();
        private int _nextEffectHandle = 1;
        private int _nextAbilityHandle = 1;
        private float _time;

        /// <summary>所属实体 Id (ECS)。</summary>
        public int EntityId { get; private set; }
        /// <summary>关联的 ECS (可空)。</summary>
        public EntityComponentSystem? ECS { get; private set; }
        /// <summary>动画桥接。</summary>
        public IAbilityAnimationBridge? AnimationBridge { get; set; }
        /// <summary>网络桥接。</summary>
        public IAbilitySystemNetworkBridge? NetworkBridge { get; set; }

        // ── 事件 ──────────────────────────────────────────────────
        /// <summary>属性变化事件。</summary>
        public event Action<AttributeChangeInfo>? OnAttributeChanged;
        /// <summary>效果被施加事件。</summary>
        public event Action<ActiveEffect>? OnGameplayEffectApplied;
        /// <summary>效果被移除事件。</summary>
        public event Action<ActiveEffect>? OnGameplayEffectRemoved;
        /// <summary>标签变化事件 (tag, added)。</summary>
        public event Action<GameplayTag, bool>? OnTagChanged;
        /// <summary>能力激活事件。</summary>
        public event Action<GameplayAbility>? OnAbilityActivated;
        /// <summary>能力结束事件。</summary>
        public event Action<GameplayAbility>? OnAbilityEnded;

        public AbilitySystemComponent(int entityId = 0, EntityComponentSystem? ecs = null)
        {
            EntityId = entityId;
            ECS = ecs;
        }

        /// <summary>绑定 ECS 实体 (可后绑定)。</summary>
        public void BindEntity(int entityId, EntityComponentSystem ecs)
        {
            EntityId = entityId;
            ECS = ecs;
        }

        // ── 属性 ──────────────────────────────────────────────────

        /// <summary>添加属性集合。</summary>
        public void AddAttributeSet(AttributeSet set)
        {
            set.Owner = this;
            _attributeSets.Add(set);
            set.OnAttributeChanged += info =>
            {
                info.EntityId = EntityId;
                OnAttributeChanged?.Invoke(info);
                NetworkBridge?.Multicast_AttributeChanged(EntityId, info.AttributeName, info.NewValue);
            };
        }

        /// <summary>获取属性当前值 (跨所有集合)。</summary>
        public float GetAttributeValue(string name)
        {
            foreach (var s in _attributeSets)
                if (s.Has(name)) return s.GetCurrentValue(name);
            return 0f;
        }

        /// <summary>获取属性基础值。</summary>
        public float GetAttributeBaseValue(string name)
        {
            foreach (var s in _attributeSets)
                if (s.Has(name)) return s.GetBaseValue(name);
            return 0f;
        }

        /// <summary>查找包含该属性的集合。</summary>
        private AttributeSet? FindSet(string name)
        {
            foreach (var s in _attributeSets) if (s.Has(name)) return s;
            return null;
        }

        // ── 标签 ──────────────────────────────────────────────────

        /// <summary>是否拥有某标签 (考虑层级)。</summary>
        public bool HasMatchingTag(GameplayTag tag) => BuildTagContainer().HasMatchingTag(tag);

        /// <summary>是否拥有某标签 (考虑层级) 的字符串重载。</summary>
        public bool HasMatchingTag(string tagName) => HasMatchingTag(GameplayTag.Parse(tagName));

        /// <summary>是否同时拥有全部标签 (考虑层级)。</summary>
        public bool HasAll(GameplayTagContainer all) => BuildTagContainer().HasAll(all);

        /// <summary>是否拥有其中任意标签 (考虑层级)。</summary>
        public bool HasAny(GameplayTagContainer any) => BuildTagContainer().HasAny(any);

        /// <summary>添加动态标签 (来自能力激活等, 非效果授予)。</summary>
        public void AddDynamicTags(GameplayTagContainer tags)
        {
            if (tags == null) return;
            foreach (var t in tags)
            {
                if (!t.IsValid) continue;
                string key = t.Name;
                if (_dynamicTagRefCounts.TryGetValue(key, out var c)) _dynamicTagRefCounts[key] = c + 1;
                else
                {
                    _dynamicTagRefCounts[key] = 1;
                    OnTagChanged?.Invoke(t, true);
                    NetworkBridge?.Multicast_TagChanged(EntityId, key, true);
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
                string key = t.Name;
                if (!_dynamicTagRefCounts.TryGetValue(key, out var c)) continue;
                if (c <= 1)
                {
                    _dynamicTagRefCounts.Remove(key);
                    OnTagChanged?.Invoke(t, false);
                    NetworkBridge?.Multicast_TagChanged(EntityId, key, false);
                }
                else _dynamicTagRefCounts[key] = c - 1;
            }
        }

        /// <summary>添加效果授予标签 (引用计数)。</summary>
        private void AddGrantedTags(GameplayTagContainer tags, ActiveEffect ae)
        {
            foreach (var t in tags)
            {
                if (!t.IsValid) continue;
                string key = t.Name;
                ae.GrantedTags.Add(t);
                if (_tagRefCounts.TryGetValue(key, out var c)) _tagRefCounts[key] = c + 1;
                else
                {
                    _tagRefCounts[key] = 1;
                    OnTagChanged?.Invoke(t, true);
                    NetworkBridge?.Multicast_TagChanged(EntityId, key, true);
                }
            }
        }

        /// <summary>移除效果授予标签 (引用计数)。</summary>
        private void RemoveGrantedTags(ActiveEffect ae)
        {
            foreach (var t in ae.GrantedTags)
            {
                string key = t.Name;
                if (!_tagRefCounts.TryGetValue(key, out var c)) continue;
                if (c <= 1)
                {
                    _tagRefCounts.Remove(key);
                    OnTagChanged?.Invoke(t, false);
                    NetworkBridge?.Multicast_TagChanged(EntityId, key, false);
                }
                else _tagRefCounts[key] = c - 1;
            }
            ae.GrantedTags.Clear();
        }

        /// <summary>构建当前所有标签的容器快照 (动态 + 授予)。</summary>
        public GameplayTagContainer BuildTagContainer()
        {
            var c = new GameplayTagContainer();
            foreach (var k in _tagRefCounts.Keys) c.Add(new GameplayTag(k));
            foreach (var k in _dynamicTagRefCounts.Keys) c.Add(new GameplayTag(k));
            return c;
        }

        // ── 效果应用 ──────────────────────────────────────────────

        /// <summary>向自身施加效果, 返回活跃效果句柄 (0 表示被免疫/失败)。</summary>
        public int ApplyEffectToSelf(GameplayEffect effect, EffectContext? context = null, float level = 1f)
        {
            return ApplyEffectInternal(effect, context ?? EffectContext.Create(this), level);
        }

        /// <summary>向目标 ASC 施加效果。</summary>
        public static int ApplyEffectToTarget(GameplayEffect effect, AbilitySystemComponent target,
                                              EffectContext? context = null, float level = 1f)
        {
            if (target == null) return 0;
            return target.ApplyEffectToSelf(effect, context, level);
        }

        /// <summary>效果施加内部实现: 免疫/标签检查 -> 即时或驻留 -> 叠加处理。</summary>
        private int ApplyEffectInternal(GameplayEffect effect, EffectContext context, float level)
        {
            // 标签要求: 目标必须拥有 ApplicationRequired 全部
            if (!BuildTagContainer().HasAll(effect.Tags.ApplicationRequired)) return 0;
            // 标签排斥: 目标拥有 ApplicationBlocked 任一则失败
            if (BuildTagContainer().HasAny(effect.Tags.ApplicationBlocked)) return 0;
            // 免疫: 目标拥有 Immunity 任一标签则免疫
            if (BuildTagContainer().HasAny(effect.Tags.Immunity)) return 0;

            // 即时效果: 直接修改基础值, 不驻留
            if (effect.DurationPolicy == EEffectDurationPolicy.Instant)
            {
                ApplyInstantModifiers(effect, context, level);
                return _nextEffectHandle++; // 即时效果不驻留, 仍返回唯一句柄以表"已施加"
            }

            // 驻留效果: 处理叠加
            var spec = new EffectSpec(effect, context, level) { StartTime = _time };
            var existing = FindStackable(effect);
            if (existing != null)
            {
                switch (effect.StackingPolicy)
                {
                    case EStackingPolicy.AggregateByTarget:
                        existing.Spec.StackCount = Math.Min(existing.Spec.StackCount + 1, effect.MaxStacks);
                        RecomputeAttributesForEffect(existing);
                        return existing.Handle;
                    case EStackingPolicy.RefreshDuration:
                        existing.Spec.DurationRemaining = effect.Duration;
                        return existing.Handle;
                    case EStackingPolicy.NoStack:
                    default:
                        break; // 落到下面新建
                }
            }

            var ae = new ActiveEffect(_nextEffectHandle++, spec);
            ae.Spec.NextPeriodTime = _time + effect.Period;
            _activeEffects.Add(ae);
            AddGrantedTags(effect.Tags.Granted, ae);
            RecomputeAttributesForEffect(ae);
            OnGameplayEffectApplied?.Invoke(ae);
            NetworkBridge?.Multicast_ApplyEffect(EntityId, effect, context, level);
            return ae.Handle;
        }

        /// <summary>查找可叠加的现存同类效果。</summary>
        private ActiveEffect? FindStackable(GameplayEffect effect)
        {
            if (effect.StackingPolicy == EStackingPolicy.NoStack) return null;
            foreach (var ae in _activeEffects)
            {
                if (ae.Spec.Def != effect) continue;
                if (effect.StackingTag.IsValid && !ae.GrantedTags.HasTagExact(effect.StackingTag)) continue;
                return ae;
            }
            return null;
        }

        /// <summary>施加即时修改器到基础值。</summary>
        private void ApplyInstantModifiers(GameplayEffect effect, EffectContext context, float level)
        {
            var spec = new EffectSpec(effect, context, level);
            foreach (var mod in effect.Modifiers)
            {
                var set = FindSet(mod.AttributeName);
                if (set == null) continue;
                float mag = spec.CalcMagnitude(mod);
                float baseVal = set.GetBaseValue(mod.AttributeName);
                switch (mod.Op)
                {
                    case EAttributeModifierOp.Add:
                        set.SetBaseValue(mod.AttributeName, baseVal + mag);
                        break;
                    case EAttributeModifierOp.Multiply:
                        set.SetBaseValue(mod.AttributeName, baseVal * mag);
                        break;
                    case EAttributeModifierOp.Override:
                        set.SetBaseValue(mod.AttributeName, mag);
                        break;
                }
            }
            RecomputeAllAttributes();
        }

        /// <summary>重算受某效果影响的所有属性当前值。</summary>
        private void RecomputeAttributesForEffect(ActiveEffect ae)
        {
            var affected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in ae.Spec.Def.Modifiers) affected.Add(m.AttributeName);
            foreach (var name in affected) RecomputeAttribute(name);
        }

        /// <summary>重算单个属性当前值 = aggregate(BaseValue, 所有活跃效果修改器)。</summary>
        public void RecomputeAttribute(string name)
        {
            var set = FindSet(name);
            if (set == null) return;
            float baseVal = set.GetBaseValue(name);
            float sumAdd = 0f;
            float productMul = 1f;
            float? overrideVal = null;
            float overridePriority = float.MinValue;

            foreach (var ae in _activeEffects)
            {
                if (!ae.Spec.IsValid) continue;
                foreach (var m in ae.Spec.Def.Modifiers)
                {
                    if (m.AttributeName != name) continue;
                    float mag = ae.Spec.CalcMagnitude(m) * ae.Spec.StackCount;
                    switch (m.Op)
                    {
                        case EAttributeModifierOp.Add: sumAdd += mag; break;
                        case EAttributeModifierOp.Multiply: productMul *= mag; break;
                        case EAttributeModifierOp.Override:
                            if (ae.Spec.Def.Priority >= overridePriority)
                            {
                                overrideVal = mag;
                                overridePriority = ae.Spec.Def.Priority;
                            }
                            break;
                    }
                }
            }

            float current = overrideVal ?? ((baseVal + sumAdd) * productMul);
            set.SetCurrentValue(name, current, EntityId);
        }

        /// <summary>重算全部属性。</summary>
        public void RecomputeAllAttributes()
        {
            foreach (var s in _attributeSets)
                foreach (var name in s.AttributeNames) RecomputeAttribute(name);
        }

        /// <summary>移除指定句柄的活跃效果。</summary>
        public bool RemoveActiveEffect(int handle)
        {
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                if (_activeEffects[i].Handle != handle) continue;
                var ae = _activeEffects[i];
                ae.Spec.IsValid = false;
                RemoveGrantedTags(ae);
                _activeEffects.RemoveAt(i);
                RecomputeAttributesForEffect(ae);
                OnGameplayEffectRemoved?.Invoke(ae);
                return true;
            }
            return false;
        }

        /// <summary>移除所有拥有指定标签的活跃效果。</summary>
        public int RemoveEffectsWithTag(GameplayTag tag)
        {
            int removed = 0;
            for (int i = _activeEffects.Count - 1; i >= 0; i--)
            {
                var ae = _activeEffects[i];
                if (ae.GrantedTags.HasMatchingTag(tag) || ae.Spec.Def.Tags.Granted.HasMatchingTag(tag))
                {
                    ae.Spec.IsValid = false;
                    RemoveGrantedTags(ae);
                    _activeEffects.RemoveAt(i);
                    RecomputeAttributesForEffect(ae);
                    OnGameplayEffectRemoved?.Invoke(ae);
                    removed++;
                }
            }
            return removed;
        }

        // ── 能力 ──────────────────────────────────────────────────

        /// <summary>赋予一个能力, 返回其句柄。</summary>
        public int GiveAbility(GameplayAbility def, float level = 1f)
        {
            int handle = _nextAbilityHandle++;
            var spec = new AbilitySpec(def, handle) { Level = level };
            _abilities[handle] = spec;
            return handle;
        }

        /// <summary>按句柄取能力实例。</summary>
        public AbilitySpec? GetAbilitySpec(int handle)
            => _abilities.TryGetValue(handle, out var s) ? s : null;

        /// <summary>按输入 Id 查找能力实例。</summary>
        public AbilitySpec? FindAbilityByInput(int inputId)
        {
            foreach (var s in _abilities.Values) if (s.InputId == inputId) return s;
            return null;
        }

        /// <summary>移除能力。</summary>
        public bool RemoveAbility(int handle) => _abilities.Remove(handle);

        /// <summary>尝试激活能力 (按句柄)。</summary>
        public bool TryActivateAbility(int handle, EffectContext? context = null)
        {
            if (!_abilities.TryGetValue(handle, out var spec)) return false;
            // 客户端: 通过网络请求服务器
            if (NetworkBridge != null && !NetworkBridge.IsServer)
            {
                NetworkBridge.Server_TryActivateAbility(EntityId, handle, context ?? EffectContext.Create(this, spec.Def));
                return false;
            }
            bool ok = spec.TryActivate(this, context);
            if (ok) OnAbilityActivated?.Invoke(spec.Def);
            return ok;
        }

        /// <summary>按输入 Id 尝试激活。</summary>
        public bool TryActivateAbilityByInput(int inputId, EffectContext? context = null)
        {
            var spec = FindAbilityByInput(inputId);
            return spec != null && TryActivateAbility(spec.Handle, context);
        }

        /// <summary>取消能力 (按句柄)。</summary>
        public void CancelAbility(int handle)
        {
            if (_abilities.TryGetValue(handle, out var spec)) spec.Cancel(this);
        }

        /// <summary>结束能力 (按句柄)。</summary>
        public void EndAbility(int handle)
        {
            if (_abilities.TryGetValue(handle, out var spec)) spec.End(this);
        }

        /// <summary>
        /// 按能力定义结束其运行时实例: 取消任务 / 回收激活标签 / 切换状态 / 触发事件。
        /// 不再回调 <see cref="GameplayAbility.EndAbility"/>, 避免递归。
        /// </summary>
        internal void EndAbilityByDef(GameplayAbility def)
        {
            AbilitySpec? spec = FindSpecByDef(def);
            if (spec == null) return;
            if (spec.State == EAbilityState.Ended || spec.State == EAbilityState.Idle) return;
            RemoveDynamicTags(def.Tags.ActivationOwned);
            foreach (var t in spec.ActiveTasks) t.Cancel();
            spec.ActiveTasks.Clear();
            spec.State = def.IsOnCooldown(this) ? EAbilityState.Cooldown : EAbilityState.Ended;
            OnAbilityEnded?.Invoke(def);
        }

        /// <summary>
        /// 按能力定义取消其运行时实例 (在子类 OnCancelAbility 之后调用)。
        /// </summary>
        internal void CancelAbilityByDef(GameplayAbility def)
        {
            AbilitySpec? spec = FindSpecByDef(def);
            if (spec == null) return;
            if (spec.State == EAbilityState.Ended || spec.State == EAbilityState.Idle) return;
            RemoveDynamicTags(def.Tags.ActivationOwned);
            foreach (var t in spec.ActiveTasks) t.Cancel();
            spec.ActiveTasks.Clear();
            spec.State = def.IsOnCooldown(this) ? EAbilityState.Cooldown : EAbilityState.Ended;
            OnAbilityEnded?.Invoke(def);
        }

        /// <summary>按能力定义查找其运行时实例 (引用相等)。</summary>
        private AbilitySpec? FindSpecByDef(GameplayAbility def)
        {
            foreach (var s in _abilities.Values)
                if (ReferenceEquals(s.Def, def)) return s;
            return null;
        }

        /// <summary>注册一个能力任务 (由 Ability.StartTask 调用)。</summary>
        internal void RegisterAbilityTask(AbilityTask task)
        {
            // 关联到当前激活的能力实例
            foreach (var spec in _abilities.Values)
            {
                if (spec.State == EAbilityState.Active || spec.State == EAbilityState.Activating ||
                    spec.State == EAbilityState.WaitingConfirm)
                {
                    spec.ActiveTasks.Add(task);
                    return;
                }
            }
            _looseTasks.Add(task);
        }

        // ── 事件路由 ──────────────────────────────────────────────

        /// <summary>
        /// 处理一个 GameplayEvent: 将事件分发给所有监听该事件标签的能力。
        /// </summary>
        public void HandleGameplayEvent(GameplayTag eventTag, GameplayEventData data)
        {
            // 简化: 遍历所有能力, 若其 Ability 标签匹配事件标签或能力实现了事件触发, 则激活
            foreach (var spec in _abilities.Values)
            {
                if (!spec.Def.Tags.Ability.HasMatchingTag(eventTag)) continue;
                if (spec.Def.CanActivateAbility(this, data.Instigator != null
                        ? EffectContext.Create(data.Instigator, spec.Def) : null))
                {
                    TryActivateAbility(spec.Handle, EffectContext.Create(data.Instigator, spec.Def));
                }
            }
        }

        // ── 每帧更新 ──────────────────────────────────────────────

        /// <summary>每帧驱动效果计时 / 周期 / 能力任务 / 冷却恢复。</summary>
        public void Tick(float dt)
        {
            _time += MathF.Max(0f, dt);

            // 活跃效果: 计时、周期触发、Ongoing 标签检查
            for (int i = _activeEffects.Count - 1; i >= 0; i--)
            {
                var ae = _activeEffects[i];
                if (!ae.Spec.IsValid) continue;

                // Ongoing 标签检查: 失去必需标签则移除
                if (ae.Spec.Def.Tags.OngoingRequired.Count > 0 &&
                    !BuildTagContainer().HasAll(ae.Spec.Def.Tags.OngoingRequired))
                {
                    RemoveActiveEffect(ae.Handle);
                    continue;
                }

                // 周期触发
                if (ae.Spec.IsPeriodic)
                {
                    while (_time >= ae.Spec.NextPeriodTime)
                    {
                        ae.Spec.NextPeriodTime += ae.Spec.Def.Period;
                        // 周期触发: 以即时方式施加一次修改器 (作用于当前值, 不改基础值)
                        ApplyPeriodicTick(ae);
                    }
                }

                // 持续时间递减
                if (ae.Spec.Def.DurationPolicy == EEffectDurationPolicy.HasDuration)
                {
                    ae.Spec.DurationRemaining -= dt;
                    if (ae.Spec.DurationRemaining <= 0f)
                    {
                        RemoveActiveEffect(ae.Handle);
                    }
                }
            }

            // 能力任务 Tick
            foreach (var spec in _abilities.Values)
            {
                spec.TickTasks(dt);
                // 冷却恢复: 若状态为 Cooldown 且冷却结束, 回到 Idle
                if (spec.State == EAbilityState.Cooldown && !spec.Def.IsOnCooldown(this))
                    spec.State = EAbilityState.Idle;
                if (spec.State == EAbilityState.Ended)
                    spec.State = spec.Def.IsOnCooldown(this) ? EAbilityState.Cooldown : EAbilityState.Idle;
            }

            // 散落任务 (未关联到能力)
            for (int i = _looseTasks.Count - 1; i >= 0; i--)
            {
                _looseTasks[i].Tick(dt);
                if (!_looseTasks[i].IsActive) _looseTasks.RemoveAt(i);
            }
        }

        /// <summary>
        /// 周期触发: 以即时方式施加一次修改器 (修改基础值, 符合 UE 周期效果语义)。
        /// </summary>
        private void ApplyPeriodicTick(ActiveEffect ae)
        {
            var affected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in ae.Spec.Def.Modifiers)
            {
                var set = FindSet(m.AttributeName);
                if (set == null) continue;
                affected.Add(m.AttributeName);
                float mag = ae.Spec.CalcMagnitude(m) * ae.Spec.StackCount;
                float baseVal = set.GetBaseValue(m.AttributeName);
                switch (m.Op)
                {
                    case EAttributeModifierOp.Add: set.SetBaseValue(m.AttributeName, baseVal + mag); break;
                    case EAttributeModifierOp.Multiply: set.SetBaseValue(m.AttributeName, baseVal * mag); break;
                    case EAttributeModifierOp.Override: set.SetBaseValue(m.AttributeName, mag); break;
                }
            }
            foreach (var name in affected) RecomputeAttribute(name);
        }

        /// <summary>当前 ASC 时间。</summary>
        public float CurrentTime => _time;
        /// <summary>活跃效果数量。</summary>
        public int ActiveEffectCount => _activeEffects.Count;
        /// <summary>已赋予能力数量。</summary>
        public int AbilityCount => _abilities.Count;
    }

    // ──────────────────────────────────────────────────────────────
    // ECS 对接: 将 ASC 作为 ECS 组件挂到实体, 由系统每帧 Tick。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ECS 组件 —— 持有对 ASC 的引用, 使能力系统融入 EntityComponentSystem。
    /// </summary>
    public struct AbilitySystemComponentData : IComponent
    {
        /// <summary>该实体的能力系统组件。</summary>
        public AbilitySystemComponent Component;
        /// <summary>是否启用每帧更新。</summary>
        public bool Enabled;
    }

    /// <summary>
    /// ECS 系统 —— 每帧 Tick 所有拥有 AbilitySystemComponentData 的实体。
    /// 对接 ECS.ISystem, 注册到 EntityComponentSystem.AddSystem 即可工作。
    /// </summary>
    public sealed class AbilitySystemUpdateSystem : ISystem
    {
        /// <summary>系统名。</summary>
        public string Name => "AbilitySystem";

        /// <summary>每帧更新所有 ASC。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;
            var entities = ecs.QueryWithComponent<AbilitySystemComponentData>();
            foreach (var id in entities)
            {
                var data = ecs.GetComponent<AbilitySystemComponentData>(id);
                if (!data.Enabled || data.Component == null) continue;
                data.Component.Tick(dt);
                ecs.SetComponent(id, data);
            }
        }
    }

    /// <summary>
    /// Actor 桥接组件 —— 将 ASC 挂载到 Gameplay 框架的 Actor 上,
    /// 使 Pawn/Actor 同时具备 ECS 与 OOP 玩法层接入。
    /// </summary>
    public sealed class AbilitySystemActorComponent : ActorComponent
    {
        /// <summary>关联的 ASC。</summary>
        public AbilitySystemComponent ASC { get; }

        public AbilitySystemActorComponent() : this(0, null) { }

        public AbilitySystemActorComponent(int entityId, EntityComponentSystem? ecs)
        {
            ASC = new AbilitySystemComponent(entityId, ecs);
            ASC.AddAttributeSet(new AttributeSet());
        }

        protected override void OnTick(float deltaTime)
        {
            ASC.Tick(deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. Gameplay Event 系统
    //    事件数据含事件标签 / 目标 / 来源 / 载荷, 通过标签路由到能力。
    //    预置事件: OnHit / OnKill / OnDamaged / OnHealed / OnStunned。
    // ──────────────────────────────────────────────────────────────

    /// <summary>Gameplay 事件数据。</summary>
    public struct GameplayEventData
    {
        /// <summary>事件标签 (路由键)。</summary>
        public GameplayTag EventTag;
        /// <summary>目标 ASC (受事件影响的一方)。</summary>
        public AbilitySystemComponent? Target;
        /// <summary>施法者 ASC (产生事件的一方)。</summary>
        public AbilitySystemComponent? Instigator;
        /// <summary>来源 ASC。</summary>
        public AbilitySystemComponent? Source;
        /// <summary>事件量值 (如伤害数值)。</summary>
        public float Magnitude;
        /// <summary>可选载荷对象。</summary>
        public object? OptionalPayload;
        /// <summary>世界空间位置 (可选)。</summary>
        public Vector3? Location;
        /// <summary>触发时间。</summary>
        public float Timestamp;
    }

    /// <summary>
    /// 事件路由器 —— 维护事件标签到处理委托的映射, 可广播事件给多个监听者。
    /// 既可路由到 ASC.HandleGameplayEvent, 也可路由到自定义处理器。
    /// </summary>
    public static class GameplayEventRouter
    {
        private static readonly Dictionary<string, List<Action<GameplayEventData>>> _handlers = new(StringComparer.Ordinal);

        /// <summary>注册一个事件标签的处理器。</summary>
        public static void Register(string eventTag, Action<GameplayEventData> handler)
        {
            if (string.IsNullOrEmpty(eventTag) || handler == null) return;
            if (!_handlers.TryGetValue(eventTag, out var list))
            {
                list = new List<Action<GameplayEventData>>();
                _handlers[eventTag] = list;
            }
            list.Add(handler);
        }

        /// <summary>注销处理器。</summary>
        public static void Unregister(string eventTag, Action<GameplayEventData> handler)
        {
            if (string.IsNullOrEmpty(eventTag) || handler == null) return;
            if (_handlers.TryGetValue(eventTag, out var list)) list.Remove(handler);
        }

        /// <summary>广播事件: 调用所有匹配标签 (含层级) 的处理器, 并转发到目标 ASC。</summary>
        public static void Broadcast(GameplayEventData data)
        {
            // 精确匹配处理器
            if (data.EventTag.IsValid && _handlers.TryGetValue(data.EventTag.Name, out var list))
            {
                foreach (var h in list.ToArray()) h(data);
            }
            // 转发到目标 ASC 的能力事件路由
            data.Target?.HandleGameplayEvent(data.EventTag, data);
        }

        /// <summary>清空所有处理器。</summary>
        public static void Clear() => _handlers.Clear();
    }

    /// <summary>
    /// 预置 Gameplay 事件标签。提供常见战斗事件的统一标签常量,
    /// 便于在效果/能力/系统中复用。
    /// </summary>
    public static class GameplayEventPresets
    {
        /// <summary>命中事件。</summary>
        public static readonly GameplayTag OnHit = GameplayTag.Parse("GameplayEvent.OnHit");
        /// <summary>击杀事件。</summary>
        public static readonly GameplayTag OnKill = GameplayTag.Parse("GameplayEvent.OnKill");
        /// <summary>受击/受伤事件。</summary>
        public static readonly GameplayTag OnDamaged = GameplayTag.Parse("GameplayEvent.OnDamaged");
        /// <summary>治疗事件。</summary>
        public static readonly GameplayTag OnHealed = GameplayTag.Parse("GameplayEvent.OnHealed");
        /// <summary>眩晕事件。</summary>
        public static readonly GameplayTag OnStunned = GameplayTag.Parse("GameplayEvent.OnStunned");
        /// <summary>死亡事件。</summary>
        public static readonly GameplayTag OnDeath = GameplayTag.Parse("GameplayEvent.OnDeath");
        /// <summary>出生事件。</summary>
        public static readonly GameplayTag OnSpawn = GameplayTag.Parse("GameplayEvent.OnSpawn");

        /// <summary>便捷构造伤害事件数据。</summary>
        public static GameplayEventData Damage(AbilitySystemComponent target, AbilitySystemComponent instigator,
                                               float amount, Vector3? location = null)
        {
            return new GameplayEventData
            {
                EventTag = OnDamaged,
                Target = target,
                Instigator = instigator,
                Source = instigator,
                Magnitude = amount,
                Location = location
            };
        }

        /// <summary>便捷构造治疗事件数据。</summary>
        public static GameplayEventData Heal(AbilitySystemComponent target, AbilitySystemComponent instigator,
                                             float amount, Vector3? location = null)
        {
            return new GameplayEventData
            {
                EventTag = OnHealed,
                Target = target,
                Instigator = instigator,
                Source = instigator,
                Magnitude = amount,
                Location = location
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 便捷扩展: 预置战斗效果与能力示例
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 一个示例伤害能力: 激活时对自身播放攻击蒙太奇, 并对目标施加即时伤害效果。
    /// 演示 Ability + Task + Effect + Event 的组合用法。
    /// </summary>
    public sealed class MeleeAttackAbility : GameplayAbility
    {
        /// <summary>目标 ASC (外部设置)。</summary>
        public AbilitySystemComponent? Target;
        /// <summary>伤害量。</summary>
        public float Damage = 15f;
        /// <summary>攻击蒙太奇名。</summary>
        public string MontageName = "Attack";

        public MeleeAttackAbility()
        {
            Name = "MeleeAttack";
            Tags.Ability.Add("Ability.Attack.Melee");
            Tags.ActivationOwned.Add("State.Attacking");
            Tags.CancelAbilitiesWithTag.Add("State.Attacking");
        }

        protected override void OnActivateAbility(AbilitySystemComponent asc, EffectContext context)
        {
            // 1) 对目标施加即时伤害效果
            if (Target != null)
            {
                var dmgEffect = GameplayEffect.CreateInstantAdd("Health", -Damage, "MeleeDamage");
                AbilitySystemComponent.ApplyEffectToTarget(dmgEffect, Target, context, 1f);

                // 2) 广播 OnHit / OnDamaged 事件 (事件系统对接)
                GameplayEventRouter.Broadcast(new GameplayEventData
                {
                    EventTag = GameplayEventPresets.OnHit,
                    Target = Target,
                    Instigator = asc,
                    Source = asc,
                    Magnitude = Damage
                });
                GameplayEventRouter.Broadcast(GameplayEventPresets.Damage(Target, asc, Damage));
            }

            // 3) 播放攻击蒙太奇 (动画对接), 完成后结束能力; 无动画桥接时立即结束
            if (asc.AnimationBridge != null)
            {
                var montageTask = new PlayMontageTask(MontageName, 1f, 0.15f);
                montageTask.Init(this, asc);
                asc.RegisterAbilityTask(montageTask);
                montageTask.Completed += _ => EndAbility(asc, context);
                montageTask.Activate();
            }
            else
            {
                EndAbility(asc, context);
            }
        }
    }

    /// <summary>
    /// 一个示例中毒效果施加器: 演示周期持续效果与免疫/标签要求。
    /// </summary>
    public static class StatusEffectPresets
    {
        /// <summary>燃烧: 每秒掉血, 持续 5 秒, 授予 "Status.Burning" 标签。</summary>
        public static GameplayEffect CreateBurning(float dps = 5f, float duration = 5f)
        {
            var e = GameplayEffect.CreatePeriodicDamage("Health", -dps, 1f, duration, "Burning");
            e.Tags.Granted.Add("Status.Burning");
            e.Tags.Granted.Add("Status.Burning.DamageOverTime");
            e.StackingPolicy = EStackingPolicy.RefreshDuration;
            e.StackingTag = GameplayTag.Parse("Status.Burning");
            return e;
        }

        /// <summary>眩晕: 无限持续, 授予 "State.Stunned" 阻止行动。</summary>
        public static GameplayEffect CreateStun()
        {
            var e = new GameplayEffect
            {
                Name = "Stun",
                DurationPolicy = EEffectDurationPolicy.Infinite,
                StackingPolicy = EStackingPolicy.RefreshDuration
            };
            e.Tags.Granted.Add("State.Stunned");
            // 拥有 State.Stunned 时阻止其他移动类能力 (能力侧在 ActivationBlocked 检查)
            return e;
        }

        /// <summary>护盾: 持续 10 秒, +50 防御, 授予 "Buff.Shielded"。</summary>
        public static GameplayEffect CreateShield(float defense = 50f, float duration = 10f)
        {
            var e = GameplayEffect.CreateDurationAdd("Defense", defense, duration, "Shield");
            e.Tags.Granted.Add("Buff.Shielded");
            return e;
        }

        /// <summary>火抗免疫: 持续期间拥有 "Immunity.Burning", 阻止燃烧效果施加。</summary>
        public static GameplayEffect CreateFireImmunity(float duration = 8f)
        {
            var e = new GameplayEffect
            {
                Name = "FireImmunity",
                DurationPolicy = EEffectDurationPolicy.HasDuration,
                Duration = duration
            };
            e.Tags.Granted.Add("Immunity.Burning");
            return e;
        }
    }
}
