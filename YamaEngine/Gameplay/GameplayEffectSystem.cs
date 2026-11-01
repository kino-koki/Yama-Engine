// YamaEngine - yama游戏引擎
// GameplayEffectSystem.cs - 效果系统 buff/debuff核心
// 效果系统是GAS的灵魂 所有属性变化都通过效果来流转
// buff debuff 状态 都在这套系统里管理

using System;
using System.Collections.Generic;
using System.Linq;

namespace YamaEngine.Gameplay
{
    #region 效果枚举和基础类型

    /// <summary>
    /// 效果持续类型 决定效果怎么过期
    /// </summary>
    public enum EffectDurationType : byte
    {
        // 瞬时效果 立即应用后消失 比如伤害
        Instant = 0,
        // 持续时间效果 有时长 到时间自动消失 比如buff
        Duration = 1,
        // 无限效果 直到手动移除 比如被动
        Infinite = 2,
    }

    /// <summary>
    /// 效果周期类型 效果怎么周期触发
    /// </summary>
    public enum EffectPeriodType : byte
    {
        // 不周期触发
        None = 0,
        // 每帧触发
        EveryFrame = 1,
        // 固定间隔触发
        Interval = 2,
        // 触发N次后停止
        LimitedCount = 3,
    }

    /// <summary>
    /// 效果叠加模式 多个相同效果怎么处理
    /// </summary>
    public enum EffectStackingMode : byte
    {
        // 不叠加 后来的覆盖先来的
        NoStack = 0,
        // 叠加层数 每个独立计时
        StackByCount = 1,
        // 叠加强度 合并到一起
        StackByMagnitude = 2,
        // 刷新持续时间 重置timer
        RefreshDuration = 3,
        // 叠加层数且刷新时间
        StackAndRefresh = 4,
    }

    /// <summary>
    /// 效果操作符 怎么修改属性
    /// </summary>
    public enum EffectOperator : byte
    {
        // 加法 直接加到当前值
        Add = 0,
        // 减法
        Subtract = 1,
        // 乘法
        Multiply = 2,
        // 除法
        Divide = 3,
        // 直接设置 覆盖
        Set = 4,
        // 设置基础值
        SetBase = 5,
        // 最小值限制
        Min = 6,
        // 最大值限制
        Max = 7,
        // 百分比加成 按基础值的百分比
        PercentAdd = 8,
        // 百分比乘法
        PercentMultiply = 9,
    }

    /// <summary>
    /// 效果施加位置 在哪一端执行
    /// </summary>
    public enum EffectApplicationTarget : byte
    {
        // 自身
        Self = 0,
        // 目标
        Target = 1,
        // 来源
        Source = 2,
        // 范围内所有
        AreaAll = 3,
    }

    /// <summary>
    /// 效果来源类型 标记效果从哪来的
    /// </summary>
    public enum EffectSourceType : byte
    {
        // 能力
        Ability = 0,
        // 物品
        Item = 1,
        // 装备
        Equipment = 2,
        // 任务奖励
        Quest = 3,
        // 环境效果
        Environment = 4,
        // 系统效果
        System = 5,
    }

    #endregion

    #region 效果规格定义

    /// <summary>
    /// 属性修改器 描述怎么修改一个属性
    /// 一个效果可以有多个修改器 同时改多个属性
    /// </summary>
    public class GameplayModifier
    {
        // 修改的属性名字
        public string AttributeName { get; set; } = string.Empty;

        // 操作符
        public EffectOperator Operator { get; set; } = EffectOperator.Add;

        // 修改量 数值
        public float Magnitude { get; set; }

        // 是否基于另一个属性计算 比如伤害=攻击力*1.5
        public string? BasedOnAttribute { get; set; }

        // 基于属性的系数
        public float BasedOnCoefficient { get; set; } = 1f;

        // 修改器标签 过滤用
        public List<string> Tags { get; set; } = new();

        // 是否受属性影响 比如暴击抗性
        public bool ImmuneToCrit { get; set; } = false;

        // 元数据
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>
        /// 计算实际修改量
        /// </summary>
        public float CalculateMagnitude(GameplayAttributeSet? attributes)
        {
            float result = Magnitude;

            if (!string.IsNullOrEmpty(BasedOnAttribute) && attributes != null)
            {
                var baseAttr = attributes.GetAttribute(BasedOnAttribute);
                if (baseAttr != null)
                {
                    result = baseAttr.CurrentValue * BasedOnCoefficient + Magnitude;
                }
            }

            return result;
        }

        public GameplayModifier Clone()
        {
            var clone = new GameplayModifier
            {
                AttributeName = AttributeName,
                Operator = Operator,
                Magnitude = Magnitude,
                BasedOnAttribute = BasedOnAttribute,
                BasedOnCoefficient = BasedOnCoefficient,
                ImmuneToCrit = ImmuneToCrit,
            };

            foreach (var t in Tags)
                clone.Tags.Add(t);

            foreach (var kv in Metadata)
                clone.Metadata[kv.Key] = kv.Value;

            return clone;
        }
    }

    /// <summary>
    /// 效果规格 静态描述 一个buff/debuff的定义
    /// </summary>
    public class GameplayEffectSpec
    {
        // 效果唯一ID
        public int EffectId { get; set; }

        // 名字
        public string Name { get; set; } = string.Empty;

        // 描述
        public string Description { get; set; } = string.Empty;

        // 持续类型
        public EffectDurationType DurationType { get; set; } = EffectDurationType.Duration;

        // 持续时间 秒
        public float Duration { get; set; } = 0f;

        // 周期类型
        public EffectPeriodType PeriodType { get; set; } = EffectPeriodType.None;

        // 周期间隔 秒
        public float PeriodInterval { get; set; } = 1f;

        // 周期触发次数 -1表示无限
        public int PeriodCount { get; set; } = -1;

        // 叠加模式
        public EffectStackingMode StackingMode { get; set; } = EffectStackingMode.NoStack;

        // 最大叠加层数
        public int MaxStacks { get; set; } = 1;

        // 属性修改器列表
        public List<GameplayModifier> Modifiers { get; set; } = new();

        // 给目标加的标签 应用时加上
        public List<string> GrantedTags { get; set; } = new();

        // 应用需要的标签 没有就不能应用
        public List<string> RequiredTags { get; set; } = new();

        // 应用时禁止的标签
        public List<string> BlockedTags { get; set; } = new();

        // 移除需要的标签 移除时检查
        public List<string> RemoveTags { get; set; } = new();

        // 效果施加位置
        public EffectApplicationTarget ApplicationTarget { get; set; } = EffectApplicationTarget.Target;

        // 来源类型
        public EffectSourceType SourceType { get; set; } = EffectSourceType.Ability;

        // 优先级 高优先级覆盖低优先级
        public int Priority { get; set; } = 0;

        // 触发的Cue列表 应用时触发
        public List<int> OnApplyCueIds { get; set; } = new();

        // 移除时触发的Cue
        public List<int> OnRemoveCueIds { get; set; } = new();

        // 周期触发的Cue
        public List<int> OnPeriodCueIds { get; set; } = new();

        // 是否可以被驱散
        public bool IsDispellable { get; set; } = true;

        // 驱散等级 越高越难驱散
        public int DispelLevel { get; set; } = 0;

        // 是否是增益 buff还是debuff
        public bool IsBeneficial { get; set; } = true;

        // 图标路径 UI用
        public string IconPath { get; set; } = string.Empty;

        // 是否每tick重新计算属性
        public bool RecalculatePerTick { get; set; } = false;

        // ── 兼容成员（AbilitySystem 等调用方使用）──
        public bool Stackable { get; set; } = false;
        public float Period { get => PeriodInterval; set => PeriodInterval = value; }
        public EffectType Type
        {
            get => DurationType == EffectDurationType.Instant ? EffectType.Instant
                 : (PeriodType != EffectPeriodType.None ? EffectType.Periodic : EffectType.Duration);
            set
            {
                DurationType = value == EffectType.Instant ? EffectDurationType.Instant : EffectDurationType.Duration;
            }
        }

        // 元数据
        public Dictionary<string, string> Metadata { get; set; } = new();

        // ── 运行时成员 (由 AbilitySystemComponent 的 ActiveEffect 管理) ──

        /// <summary>效果定义 (运行时, 指向 GameplayAbilitySystem 的 GameplayEffect)。</summary>
        public GameplayEffect? Def { get; set; }

        /// <summary>当前叠加层数。</summary>
        public int StackCount { get; set; } = 1;

        /// <summary>剩余持续时间 (秒)。</summary>
        public float DurationRemaining { get; set; }

        /// <summary>下次周期触发时间 (秒)。</summary>
        public float NextPeriodTime { get; set; }

        /// <summary>是否仍有效 (未被移除)。</summary>
        public bool IsValid { get; set; } = true;

        /// <summary>是否为周期效果。</summary>
        public bool IsPeriodic => Def != null && Def.Period > 0f;

        /// <summary>计算某修改器在当前等级下的量值。</summary>
        public float CalcMagnitude(EffectModifier mod)
            => mod.Magnitude.Evaluate(1f);

        // 深拷贝
        public GameplayEffectSpec Clone()
        {
            var clone = new GameplayEffectSpec
            {
                EffectId = EffectId,
                Name = Name,
                Description = Description,
                DurationType = DurationType,
                Duration = Duration,
                PeriodType = PeriodType,
                PeriodInterval = PeriodInterval,
                PeriodCount = PeriodCount,
                StackingMode = StackingMode,
                MaxStacks = MaxStacks,
                ApplicationTarget = ApplicationTarget,
                SourceType = SourceType,
                Priority = Priority,
                IsDispellable = IsDispellable,
                DispelLevel = DispelLevel,
                IsBeneficial = IsBeneficial,
                IconPath = IconPath,
                RecalculatePerTick = RecalculatePerTick,
            };

            foreach (var m in Modifiers)
                clone.Modifiers.Add(m.Clone());

            foreach (var t in GrantedTags) clone.GrantedTags.Add(t);
            foreach (var t in RequiredTags) clone.RequiredTags.Add(t);
            foreach (var t in BlockedTags) clone.BlockedTags.Add(t);
            foreach (var t in RemoveTags) clone.RemoveTags.Add(t);
            foreach (var c in OnApplyCueIds) clone.OnApplyCueIds.Add(c);
            foreach (var c in OnRemoveCueIds) clone.OnRemoveCueIds.Add(c);
            foreach (var c in OnPeriodCueIds) clone.OnPeriodCueIds.Add(c);
            foreach (var kv in Metadata) clone.Metadata[kv.Key] = kv.Value;

            return clone;
        }

        public override string ToString()
        {
            return $"[EffectSpec {EffectId}:{Name}]";
        }

        /// <summary>
        /// 从旧模型 <see cref="EffectSpec"/> 隐式转换 (兼容 AbilitySystem 的 ApplyEffectInternal)。
        /// </summary>
        public static implicit operator GameplayEffectSpec(EffectSpec spec)
        {
            if (spec == null) return null!;
            return new GameplayEffectSpec
            {
                Name = spec.Def?.Name ?? string.Empty,
                Duration = spec.DurationRemaining,
                StackCount = spec.StackCount,
                DurationRemaining = spec.DurationRemaining,
                NextPeriodTime = spec.NextPeriodTime,
                Def = spec.Def,
                IsValid = spec.IsValid,
            };
        }
    }

    /// <summary>
    /// 效果规格仓库 管理所有效果定义
    /// </summary>
    public class GameplayEffectSpecRepository
    {
        private readonly Dictionary<int, GameplayEffectSpec> _specs = new();
        private readonly Dictionary<string, int> _nameToId = new();
        private readonly object _lock = new();

        public bool Register(GameplayEffectSpec spec)
        {
            if (spec == null || spec.EffectId <= 0) return false;

            lock (_lock)
            {
                if (_specs.ContainsKey(spec.EffectId)) return false;
                _specs[spec.EffectId] = spec;
                if (!string.IsNullOrEmpty(spec.Name))
                    _nameToId[spec.Name] = spec.EffectId;
                return true;
            }
        }

        public bool Unregister(int effectId)
        {
            lock (_lock)
            {
                if (!_specs.TryGetValue(effectId, out var spec)) return false;
                _specs.Remove(effectId);
                if (!string.IsNullOrEmpty(spec.Name))
                    _nameToId.Remove(spec.Name);
                return true;
            }
        }

        public GameplayEffectSpec? GetById(int effectId)
        {
            lock (_lock)
            {
                _specs.TryGetValue(effectId, out var spec);
                return spec;
            }
        }

        public GameplayEffectSpec? GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                if (!_nameToId.TryGetValue(name, out var id)) return null;
                _specs.TryGetValue(id, out var spec);
                return spec;
            }
        }

        public List<GameplayEffectSpec> GetAll()
        {
            lock (_lock)
            {
                return _specs.Values.ToList();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _specs.Clear();
                _nameToId.Clear();
            }
        }

        public int Count
        {
            get
            {
                lock (_lock) { return _specs.Count; }
            }
        }
    }

    #endregion

    #region 效果实例 运行时

    /// <summary>
    /// 效果实例 运行时的效果
    /// 一个效果规格被应用后创建一个实例
    /// 跟踪持续时间 层数 周期
    /// </summary>
    public class GameplayEffectInstance
    {
        // 唯一实例ID
        public long InstanceId { get; internal set; }

        // 引用规格
        public GameplayEffectSpec Spec { get; }

        // 当前层数
        public int StackCount { get; internal set; } = 1;

        // 剩余持续时间
        public float RemainingDuration { get; internal set; }

        // 已经持续时间
        public float ElapsedDuration { get; internal set; }

        // 周期计时器
        public float PeriodTimer { get; internal set; }

        // 已周期触发次数
        public int PeriodTriggeredCount { get; internal set; }

        // 应用时间戳
        public float AppliedTime { get; internal set; }

        // 来源组件 引用
        public GameplayAbilityComponent? SourceComponent { get; set; }

        // 目标组件 所属的组件
        public GameplayAbilityComponent? TargetComponent { get; internal set; }

        // 是否激活中
        public bool IsActive { get; internal set; } = true;

        // 是否已经被移除
        public bool IsRemoved { get; internal set; }

        // 自定义数据
        public Dictionary<string, object?> CustomData { get; } = new();

        // 实例ID自增器
        private static long _nextInstanceId = 1;

        public GameplayEffectInstance(GameplayEffectSpec spec)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            InstanceId = System.Threading.Interlocked.Increment(ref _nextInstanceId);
            RemainingDuration = spec.Duration;
        }

        /// <summary>
        /// 是否是瞬时效果
        /// </summary>
        public bool IsInstant => Spec.DurationType == EffectDurationType.Instant;

        /// <summary>
        /// 是否已过期
        /// </summary>
        public bool IsExpired
        {
            get
            {
                if (Spec.DurationType == EffectDurationType.Infinite) return false;
                if (Spec.DurationType == EffectDurationType.Instant) return true;
                return RemainingDuration <= 0f;
            }
        }

        /// <summary>
        /// 获取剩余时间百分比
        /// </summary>
        public float GetDurationProgress()
        {
            if (Spec.DurationType != EffectDurationType.Duration || Spec.Duration <= 0f)
                return 1f;
            return 1f - (RemainingDuration / Spec.Duration);
        }

        /// <summary>
        /// 刷新持续时间
        /// </summary>
        public void RefreshDuration()
        {
            RemainingDuration = Spec.Duration;
            ElapsedDuration = 0f;
        }

        /// <summary>
        /// 增加层数
        /// </summary>
        public bool AddStack()
        {
            if (StackCount >= Spec.MaxStacks) return false;
            StackCount++;
            return true;
        }

        /// <summary>
        /// 减少层数
        /// </summary>
        public bool RemoveStack()
        {
            if (StackCount <= 1) return false;
            StackCount--;
            return true;
        }

        public override string ToString()
        {
            return $"[EffectInstance {InstanceId} {Spec.Name} x{StackCount} {RemainingDuration:F1}s]";
        }
    }

    #endregion

    #region 效果容器 运行时管理

    /// <summary>
    /// 效果容器 替换GAS V2里的占位类型
    /// 这是完整的实现 挂在能力组件上
    /// 管理所有运行中的效果实例
    /// </summary>
    public class GameplayEffectContainerFull
    {
        // 所属的能力组件
        public GameplayAbilityComponent? OwnerComponent { get; internal set; }

        // 所有活跃的效果实例 按实例ID索引
        private readonly Dictionary<long, GameplayEffectInstance> _activeEffects = new();

        // 按效果规格ID索引 方便查找同类效果
        private readonly Dictionary<int, List<GameplayEffectInstance>> _effectsBySpecId = new();

        // 锁
        private readonly object _lock = new();

        // 效果应用事件
        public event Action<GameplayEffectContainerFull, GameplayEffectInstance>? EffectApplied;

        // 效果移除事件
        public event Action<GameplayEffectContainerFull, GameplayEffectInstance, EffectRemoveReason>? EffectRemoved;

        // 效果层数变化事件
        public event Action<GameplayEffectContainerFull, GameplayEffectInstance>? EffectStackChanged;

        // 效果周期触发事件
        public event Action<GameplayEffectContainerFull, GameplayEffectInstance>? EffectPeriodTriggered;

        /// <summary>
        /// 应用一个效果
        /// </summary>
        public GameplayEffectInstance? ApplyEffect(
            GameplayEffectSpec spec,
            GameplayAbilityComponent? source = null,
            int initialStacks = 1)
        {
            if (spec == null) return null;

            lock (_lock)
            {
                // 检查标签条件
                if (OwnerComponent != null)
                {
                    // 需要的标签
                    if (spec.RequiredTags.Count > 0)
                    {
                        foreach (var tag in spec.RequiredTags)
                        {
                            if (!OwnerComponent.Tags.HasTag(tag))
                                return null;
                        }
                    }

                    // 禁止的标签
                    if (spec.BlockedTags.Count > 0)
                    {
                        foreach (var tag in spec.BlockedTags)
                        {
                            if (OwnerComponent.Tags.HasTag(tag))
                                return null;
                        }
                    }
                }

                // 处理叠加逻辑
                if (spec.StackingMode != EffectStackingMode.NoStack)
                {
                    if (_effectsBySpecId.TryGetValue(spec.EffectId, out var existingList) && existingList.Count > 0)
                    {
                        var existing = existingList[0];

                        switch (spec.StackingMode)
                        {
                            case EffectStackingMode.StackByCount:
                                if (existing.AddStack())
                                {
                                    EffectStackChanged?.Invoke(this, existing);
                                    return existing;
                                }
                                break;

                            case EffectStackingMode.RefreshDuration:
                                existing.RefreshDuration();
                                EffectStackChanged?.Invoke(this, existing);
                                return existing;

                            case EffectStackingMode.StackAndRefresh:
                                existing.AddStack();
                                existing.RefreshDuration();
                                EffectStackChanged?.Invoke(this, existing);
                                return existing;

                            case EffectStackingMode.StackByMagnitude:
                                // 合并量 不增加实例
                                existing.RefreshDuration();
                                return existing;
                        }
                    }
                }

                // 创建新实例
                var instance = new GameplayEffectInstance(spec.Clone())
                {
                    SourceComponent = source,
                    TargetComponent = OwnerComponent,
                    StackCount = Math.Max(1, initialStacks),
                };

                _activeEffects[instance.InstanceId] = instance;

                if (!_effectsBySpecId.TryGetValue(spec.EffectId, out var list))
                {
                    list = new List<GameplayEffectInstance>();
                    _effectsBySpecId[spec.EffectId] = list;
                }
                list.Add(instance);

                // 应用属性修改器
                ApplyModifiers(instance);

                // 添加标签
                if (OwnerComponent != null && spec.GrantedTags.Count > 0)
                {
                    foreach (var tag in spec.GrantedTags)
                    {
                        OwnerComponent.Tags.AddTag(tag);
                    }
                }

                EffectApplied?.Invoke(this, instance);
                return instance;
            }
        }

        /// <summary>
        /// 应用属性修改器
        /// </summary>
        private void ApplyModifiers(GameplayEffectInstance instance)
        {
            if (OwnerComponent?.Attributes == null) return;

            foreach (var modifier in instance.Spec.Modifiers)
            {
                float magnitude = modifier.CalculateMagnitude(OwnerComponent.Attributes);
                magnitude *= instance.StackCount; // 按层数放大

                ApplyModifierToAttribute(OwnerComponent.Attributes, modifier, magnitude);
            }
        }

        /// <summary>
        /// 反向应用属性修改器 移除效果时用
        /// </summary>
        private void RemoveModifiers(GameplayEffectInstance instance)
        {
            if (OwnerComponent?.Attributes == null) return;

            foreach (var modifier in instance.Spec.Modifiers)
            {
                float magnitude = modifier.CalculateMagnitude(OwnerComponent.Attributes);
                magnitude *= instance.StackCount;

                ApplyModifierReversed(OwnerComponent.Attributes, modifier, magnitude);
            }
        }

        /// <summary>
        /// 把修改器应用到属性
        /// </summary>
        private void ApplyModifierToAttribute(GameplayAttributeSet attributes, GameplayModifier modifier, float magnitude)
        {
            var attr = attributes.GetAttribute(modifier.AttributeName);
            if (attr == null)
            {
                // 属性不存在 创建一个
                attributes.SetAttribute(modifier.AttributeName, 0f);
                attr = attributes.GetAttribute(modifier.AttributeName);
                if (attr == null) return;
            }

            switch (modifier.Operator)
            {
                case EffectOperator.Add:
                    attr.CurrentValue += magnitude;
                    break;
                case EffectOperator.Subtract:
                    attr.CurrentValue -= magnitude;
                    break;
                case EffectOperator.Multiply:
                    attr.CurrentValue *= magnitude;
                    break;
                case EffectOperator.Divide:
                    if (magnitude != 0f)
                        attr.CurrentValue /= magnitude;
                    break;
                case EffectOperator.Set:
                    attr.CurrentValue = magnitude;
                    break;
                case EffectOperator.SetBase:
                    attr.BaseValue = magnitude;
                    break;
                case EffectOperator.Min:
                    attr.CurrentValue = Math.Min(attr.CurrentValue, magnitude);
                    break;
                case EffectOperator.Max:
                    attr.CurrentValue = Math.Max(attr.CurrentValue, magnitude);
                    break;
                case EffectOperator.PercentAdd:
                    attr.CurrentValue += attr.BaseValue * magnitude;
                    break;
                case EffectOperator.PercentMultiply:
                    attr.CurrentValue *= (1f + magnitude);
                    break;
            }
        }

        /// <summary>
        /// 反向应用 用于移除效果
        /// </summary>
        private void ApplyModifierReversed(GameplayAttributeSet attributes, GameplayModifier modifier, float magnitude)
        {
            var attr = attributes.GetAttribute(modifier.AttributeName);
            if (attr == null) return;

            switch (modifier.Operator)
            {
                case EffectOperator.Add:
                    attr.CurrentValue -= magnitude;
                    break;
                case EffectOperator.Subtract:
                    attr.CurrentValue += magnitude;
                    break;
                case EffectOperator.Multiply:
                    if (magnitude != 0f)
                        attr.CurrentValue /= magnitude;
                    break;
                case EffectOperator.Divide:
                    attr.CurrentValue *= magnitude;
                    break;
                case EffectOperator.Set:
                case EffectOperator.SetBase:
                    // 无法反向 恢复到基础值
                    attr.CurrentValue = attr.BaseValue;
                    break;
                case EffectOperator.PercentAdd:
                    attr.CurrentValue -= attr.BaseValue * magnitude;
                    break;
                case EffectOperator.PercentMultiply:
                    attr.CurrentValue /= (1f + magnitude);
                    break;
                // Min/Max无法反向
            }
        }

        /// <summary>
        /// 移除效果
        /// </summary>
        public bool RemoveEffect(long instanceId, EffectRemoveReason reason)
        {
            lock (_lock)
            {
                if (!_activeEffects.TryGetValue(instanceId, out var instance))
                    return false;

                // 反向应用修改器
                RemoveModifiers(instance);

                // 移除标签
                if (OwnerComponent != null && instance.Spec.GrantedTags.Count > 0)
                {
                    foreach (var tag in instance.Spec.GrantedTags)
                    {
                        OwnerComponent.Tags.RemoveTag(tag);
                    }
                }

                instance.IsActive = false;
                instance.IsRemoved = true;
                _activeEffects.Remove(instanceId);

                if (_effectsBySpecId.TryGetValue(instance.Spec.EffectId, out var list))
                {
                    list.Remove(instance);
                    if (list.Count == 0)
                        _effectsBySpecId.Remove(instance.Spec.EffectId);
                }

                EffectRemoved?.Invoke(this, instance, reason);
                return true;
            }
        }

        /// <summary>
        /// 移除所有指定规格的效果
        /// </summary>
        public int RemoveAllBySpecId(int specId, EffectRemoveReason reason)
        {
            List<long> toRemove;
            lock (_lock)
            {
                if (!_effectsBySpecId.TryGetValue(specId, out var list))
                    return 0;
                toRemove = list.Select(e => e.InstanceId).ToList();
            }

            int count = 0;
            foreach (var id in toRemove)
            {
                if (RemoveEffect(id, reason))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 移除所有可驱散的效果
        /// </summary>
        public int DispelAll(int maxDispelLevel, EffectRemoveReason reason)
        {
            List<long> toRemove;
            lock (_lock)
            {
                toRemove = _activeEffects.Values
                    .Where(e => e.Spec.IsDispellable && e.Spec.DispelLevel <= maxDispelLevel)
                    .Select(e => e.InstanceId)
                    .ToList();
            }

            int count = 0;
            foreach (var id in toRemove)
            {
                if (RemoveEffect(id, reason))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 移除所有debuff
        /// </summary>
        public int RemoveAllDebuffs(EffectRemoveReason reason)
        {
            List<long> toRemove;
            lock (_lock)
            {
                toRemove = _activeEffects.Values
                    .Where(e => !e.Spec.IsBeneficial)
                    .Select(e => e.InstanceId)
                    .ToList();
            }

            int count = 0;
            foreach (var id in toRemove)
            {
                if (RemoveEffect(id, reason))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 移除所有buff
        /// </summary>
        public int RemoveAllBuffs(EffectRemoveReason reason)
        {
            List<long> toRemove;
            lock (_lock)
            {
                toRemove = _activeEffects.Values
                    .Where(e => e.Spec.IsBeneficial)
                    .Select(e => e.InstanceId)
                    .ToList();
            }

            int count = 0;
            foreach (var id in toRemove)
            {
                if (RemoveEffect(id, reason))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 清空所有效果
        /// </summary>
        public void ClearAllEffects(EffectRemoveReason reason)
        {
            List<long> toRemove;
            lock (_lock)
            {
                toRemove = _activeEffects.Keys.ToList();
            }

            foreach (var id in toRemove)
            {
                RemoveEffect(id, reason);
            }
        }

        /// <summary>
        /// 获取所有活跃效果
        /// </summary>
        public List<GameplayEffectInstance> GetAllActiveEffects()
        {
            lock (_lock)
            {
                return _activeEffects.Values.ToList();
            }
        }

        /// <summary>
        /// 获取指定规格的所有效果
        /// </summary>
        public List<GameplayEffectInstance> GetEffectsBySpecId(int specId)
        {
            lock (_lock)
            {
                if (!_effectsBySpecId.TryGetValue(specId, out var list))
                    return new List<GameplayEffectInstance>();
                return new List<GameplayEffectInstance>(list);
            }
        }

        /// <summary>
        /// 是否有指定规格的效果
        /// </summary>
        public bool HasEffect(int specId)
        {
            lock (_lock)
            {
                return _effectsBySpecId.ContainsKey(specId) && _effectsBySpecId[specId].Count > 0;
            }
        }

        /// <summary>
        /// 活跃效果数量
        /// </summary>
        public int ActiveEffectCount
        {
            get
            {
                lock (_lock)
                {
                    return _activeEffects.Count;
                }
            }
        }

        /// <summary>
        /// Tick更新所有效果
        /// </summary>
        public void Tick(float deltaTime)
        {
            List<(GameplayEffectInstance, EffectRemoveReason)> toRemove = new();
            List<GameplayEffectInstance> toPeriodTrigger = new();

            List<GameplayEffectInstance> snapshot;
            lock (_lock)
            {
                snapshot = _activeEffects.Values.ToList();
            }

            foreach (var instance in snapshot)
            {
                // 持续时间效果 减少剩余时间
                if (instance.Spec.DurationType == EffectDurationType.Duration)
                {
                    instance.RemainingDuration -= deltaTime;
                    instance.ElapsedDuration += deltaTime;

                    if (instance.IsExpired)
                    {
                        toRemove.Add((instance, EffectRemoveReason.DurationExpired));
                        continue;
                    }
                }

                // 周期触发
                if (instance.Spec.PeriodType != EffectPeriodType.None)
                {
                    bool shouldTrigger = false;

                    switch (instance.Spec.PeriodType)
                    {
                        case EffectPeriodType.EveryFrame:
                            shouldTrigger = true;
                            break;

                        case EffectPeriodType.Interval:
                        case EffectPeriodType.LimitedCount:
                            instance.PeriodTimer += deltaTime;
                            if (instance.PeriodTimer >= instance.Spec.PeriodInterval)
                            {
                                instance.PeriodTimer = 0f;

                                if (instance.Spec.PeriodType == EffectPeriodType.LimitedCount &&
                                    instance.PeriodTriggeredCount >= instance.Spec.PeriodCount)
                                {
                                    // 达到次数限制 不再触发
                                }
                                else
                                {
                                    shouldTrigger = true;
                                    instance.PeriodTriggeredCount++;
                                }
                            }
                            break;
                    }

                    if (shouldTrigger)
                    {
                        toPeriodTrigger.Add(instance);
                    }
                }
            }

            // 触发周期效果
            foreach (var instance in toPeriodTrigger)
            {
                // 周期触发时重新应用修改器
                if (instance.Spec.RecalculatePerTick)
                {
                    ApplyModifiers(instance);
                }
                else
                {
                    // 瞬时效果周期触发 直接应用
                    if (instance.Spec.DurationType == EffectDurationType.Instant)
                    {
                        ApplyModifiers(instance);
                    }
                }

                EffectPeriodTriggered?.Invoke(this, instance);
            }

            // 移除过期效果
            foreach (var (instance, reason) in toRemove)
            {
                RemoveEffect(instance.InstanceId, reason);
            }
        }
    }

    /// <summary>
    /// 效果移除原因
    /// </summary>
    public enum EffectRemoveReason : byte
    {
        Manual = 0,             // 手动移除
        DurationExpired = 1,    // 持续时间到期
        Dispelled = 2,          // 被驱散
        StackDepleted = 3,      // 层数耗尽
        OwnerDeath = 4,         // 宿主死亡
        TagConditionLost = 5,   // 标签条件丢失
        ForceRemoved = 6,       // 强制移除
    }

    #endregion

    #region 效果系统管理器

    /// <summary>
    /// 效果系统管理器 全局入口
    /// 管理效果规格仓库和所有效果容器
    /// </summary>
    public class GameplayEffectSystem
    {
        // 单例
        private static GameplayEffectSystem? _instance;
        private static readonly object _instanceLock = new();

        public static GameplayEffectSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new GameplayEffectSystem();
                    }
                }
                return _instance;
            }
        }

        // 效果规格仓库
        public GameplayEffectSpecRepository Specs { get; } = new();

        // 所有注册的容器 跟踪便于全局更新
        private readonly List<GameplayEffectContainerFull> _containers = new();
        private readonly object _containersLock = new();

        // 全局事件
        public event Action<GameplayEffectContainerFull, GameplayEffectInstance>? GlobalEffectApplied;
        public event Action<GameplayEffectContainerFull, GameplayEffectInstance, EffectRemoveReason>? GlobalEffectRemoved;

        private GameplayEffectSystem() { }

        /// <summary>
        /// 创建效果容器 挂到能力组件上
        /// </summary>
        public GameplayEffectContainerFull CreateContainer(GameplayAbilityComponent owner)
        {
            var container = new GameplayEffectContainerFull
            {
                OwnerComponent = owner,
            };

            container.EffectApplied += (c, i) => GlobalEffectApplied?.Invoke(c, i);
            container.EffectRemoved += (c, i, r) => GlobalEffectRemoved?.Invoke(c, i, r);

            lock (_containersLock)
            {
                _containers.Add(container);
            }

            return container;
        }

        /// <summary>
        /// 注销容器
        /// </summary>
        public void DestroyContainer(GameplayEffectContainerFull container)
        {
            lock (_containersLock)
            {
                _containers.Remove(container);
            }
            container.ClearAllEffects(EffectRemoveReason.ForceRemoved);
        }

        /// <summary>
        /// 全局Tick
        /// </summary>
        public void Tick(float deltaTime)
        {
            List<GameplayEffectContainerFull> snapshot;
            lock (_containersLock)
            {
                snapshot = _containers.ToList();
            }

            foreach (var container in snapshot)
            {
                container.Tick(deltaTime);
            }
        }

        /// <summary>
        /// 批量加载效果规格
        /// </summary>
        public void LoadSpecsFromConfig(IEnumerable<GameplayEffectSpec> specs)
        {
            if (specs == null) return;
            foreach (var spec in specs)
            {
                Specs.Register(spec);
            }
        }

        /// <summary>
        /// 关闭系统
        /// </summary>
        public void Shutdown()
        {
            lock (_containersLock)
            {
                foreach (var container in _containers)
                {
                    container.ClearAllEffects(EffectRemoveReason.ForceRemoved);
                }
                _containers.Clear();
            }
            Specs.Clear();
        }
    }

    #endregion

    #region 效果工厂和预设

    /// <summary>
    /// 效果工厂 创建常用效果
    /// </summary>
    public static class GameplayEffectFactory
    {
        /// <summary>
        /// 创建伤害效果
        /// </summary>
        public static GameplayEffectSpec CreateDamageEffect(int effectId, string name, float damage)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"造成{damage}点伤害",
                DurationType = EffectDurationType.Instant,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = false,
                Modifiers = new List<GameplayModifier>
                {
                    new GameplayModifier
                    {
                        AttributeName = "Health",
                        Operator = EffectOperator.Subtract,
                        Magnitude = damage,
                    }
                },
            };
        }

        /// <summary>
        /// 创建治疗效果
        /// </summary>
        public static GameplayEffectSpec CreateHealEffect(int effectId, string name, float heal)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"恢复{heal}点生命值",
                DurationType = EffectDurationType.Instant,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = true,
                Modifiers = new List<GameplayModifier>
                {
                    new GameplayModifier
                    {
                        AttributeName = "Health",
                        Operator = EffectOperator.Add,
                        Magnitude = heal,
                    }
                },
            };
        }

        /// <summary>
        /// 创建持续伤害效果 DOT
        /// </summary>
        public static GameplayEffectSpec CreateDotEffect(int effectId, string name, float damagePerTick, float interval, float duration)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"每{interval}秒受到{damagePerTick}点伤害 持续{duration}秒",
                DurationType = EffectDurationType.Duration,
                Duration = duration,
                PeriodType = EffectPeriodType.Interval,
                PeriodInterval = interval,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = false,
                IsDispellable = true,
                DispelLevel = 1,
                Modifiers = new List<GameplayModifier>
                {
                    new GameplayModifier
                    {
                        AttributeName = "Health",
                        Operator = EffectOperator.Subtract,
                        Magnitude = damagePerTick,
                    }
                },
            };
        }

        /// <summary>
        /// 创建持续治疗效果 HOT
        /// </summary>
        public static GameplayEffectSpec CreateHotEffect(int effectId, string name, float healPerTick, float interval, float duration)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"每{interval}秒恢复{healPerTick}点生命 持续{duration}秒",
                DurationType = EffectDurationType.Duration,
                Duration = duration,
                PeriodType = EffectPeriodType.Interval,
                PeriodInterval = interval,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = true,
                Modifiers = new List<GameplayModifier>
                {
                    new GameplayModifier
                    {
                        AttributeName = "Health",
                        Operator = EffectOperator.Add,
                        Magnitude = healPerTick,
                    }
                },
            };
        }

        /// <summary>
        /// 创建属性buff效果 比如攻击力提升
        /// </summary>
        public static GameplayEffectSpec CreateStatBuffEffect(
            int effectId, string name, string attributeName,
            EffectOperator op, float magnitude, float duration)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"{attributeName} {op} {magnitude} 持续{duration}秒",
                DurationType = EffectDurationType.Duration,
                Duration = duration,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = magnitude > 0,
                StackingMode = EffectStackingMode.RefreshDuration,
                Modifiers = new List<GameplayModifier>
                {
                    new GameplayModifier
                    {
                        AttributeName = attributeName,
                        Operator = op,
                        Magnitude = magnitude,
                    }
                },
            };
        }

        /// <summary>
        /// 创建眩晕效果
        /// </summary>
        public static GameplayEffectSpec CreateStunEffect(int effectId, string name, float duration)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"眩晕{duration}秒 无法行动",
                DurationType = EffectDurationType.Duration,
                Duration = duration,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = false,
                IsDispellable = true,
                DispelLevel = 2,
                GrantedTags = new List<string> { "State.Stunned" },
                BlockedTags = new List<string> { "State.Immune.Stun" },
            };
        }

        /// <summary>
        /// 创建减速效果
        /// </summary>
        public static GameplayEffectSpec CreateSlowEffect(int effectId, string name, float slowPercent, float duration)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"移动速度降低{slowPercent*100}% 持续{duration}秒",
                DurationType = EffectDurationType.Duration,
                Duration = duration,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = false,
                IsDispellable = true,
                StackingMode = EffectStackingMode.RefreshDuration,
                GrantedTags = new List<string> { "State.Slowed" },
                Modifiers = new List<GameplayModifier>
                {
                    new GameplayModifier
                    {
                        AttributeName = "MoveSpeed",
                        Operator = EffectOperator.PercentMultiply,
                        Magnitude = -slowPercent,
                    }
                },
            };
        }

        /// <summary>
        /// 创建护盾效果 吸收伤害
        /// </summary>
        public static GameplayEffectSpec CreateShieldEffect(int effectId, string name, float shieldAmount, float duration)
        {
            return new GameplayEffectSpec
            {
                EffectId = effectId,
                Name = name,
                Description = $"吸收{shieldAmount}点伤害 持续{duration}秒",
                DurationType = EffectDurationType.Duration,
                Duration = duration,
                SourceType = EffectSourceType.Ability,
                IsBeneficial = true,
                GrantedTags = new List<string> { "State.Shielded" },
                Modifiers = new List<GameplayModifier>
                {
                    new GameplayModifier
                    {
                        AttributeName = "Shield",
                        Operator = EffectOperator.Add,
                        Magnitude = shieldAmount,
                    }
                },
            };
        }
    }

    #endregion

    #region 效果计算上下文

    /// <summary>
    /// 效果计算上下文 包含计算修改量时的所有信息
    /// </summary>
    public class EffectCalculationContext
    {
        public GameplayAbilityComponent? Source { get; set; }
        public GameplayAbilityComponent? Target { get; set; }
        public GameplayEffectSpec? EffectSpec { get; set; }
        public int StackCount { get; set; } = 1;
        public Dictionary<string, object?> CustomData { get; } = new();

        public float SourceLevel { get; set; } = 1f;
        public float TargetLevel { get; set; } = 1f;

        /// <summary>
        /// 获取源属性值
        /// </summary>
        public float GetSourceAttribute(string name)
        {
            return Source?.Attributes?.GetAttribute(name)?.CurrentValue ?? 0f;
        }

        /// <summary>
        /// 获取目标属性值
        /// </summary>
        public float GetTargetAttribute(string name)
        {
            return Target?.Attributes?.GetAttribute(name)?.CurrentValue ?? 0f;
        }
    }

    /// <summary>
    /// 效果计算器 自定义计算逻辑
    /// </summary>
    public interface IEffectCalculator
    {
        float CalculateMagnitude(GameplayModifier modifier, EffectCalculationContext context);
    }

    /// <summary>
    /// 简单线性计算 magnitude = a * level + b
    /// </summary>
    public class LinearCalculator : IEffectCalculator
    {
        private readonly float _coefficient;
        private readonly float _baseValue;

        public LinearCalculator(float coefficient, float baseValue)
        {
            _coefficient = coefficient;
            _baseValue = baseValue;
        }

        public float CalculateMagnitude(GameplayModifier modifier, EffectCalculationContext context)
        {
            return _coefficient * context.SourceLevel + _baseValue + modifier.Magnitude;
        }
    }

    /// <summary>
    /// 百分比计算 magnitude = base * percent
    /// </summary>
    public class PercentCalculator : IEffectCalculator
    {
        private readonly string _baseAttributeName;
        private readonly float _percent;

        public PercentCalculator(string baseAttributeName, float percent)
        {
            _baseAttributeName = baseAttributeName;
            _percent = percent;
        }

        public float CalculateMagnitude(GameplayModifier modifier, EffectCalculationContext context)
        {
            float baseValue = context.GetSourceAttribute(_baseAttributeName);
            return baseValue * _percent + modifier.Magnitude;
        }
    }

    /// <summary>
    /// 计算器注册表
    /// </summary>
    public class EffectCalculatorRegistry
    {
        private readonly Dictionary<string, IEffectCalculator> _calculators = new();
        private readonly object _lock = new();

        public void Register(string name, IEffectCalculator calculator)
        {
            if (string.IsNullOrEmpty(name) || calculator == null) return;
            lock (_lock)
            {
                _calculators[name] = calculator;
            }
        }

        public IEffectCalculator? Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _calculators.TryGetValue(name, out var calc);
                return calc;
            }
        }
    }

    #endregion

    #region 效果条件系统

    /// <summary>
    /// 效果应用条件接口
    /// </summary>
    public interface IEffectCondition
    {
        bool CanApply(EffectCalculationContext context);
    }

    /// <summary>
    /// 属性条件 检查属性是否满足要求
    /// </summary>
    public class AttributeCondition : IEffectCondition
    {
        private readonly string _attributeName;
        private readonly float _minValue;
        private readonly float _maxValue;

        public AttributeCondition(string attributeName, float minValue = float.MinValue, float maxValue = float.MaxValue)
        {
            _attributeName = attributeName;
            _minValue = minValue;
            _maxValue = maxValue;
        }

        public bool CanApply(EffectCalculationContext context)
        {
            float value = context.GetTargetAttribute(_attributeName);
            return value >= _minValue && value <= _maxValue;
        }
    }

    /// <summary>
    /// 标签条件 检查标签
    /// </summary>
    public class TagCondition : IEffectCondition
    {
        private readonly List<string> _requiredTags = new();
        private readonly List<string> _blockedTags = new();

        public TagCondition(IEnumerable<string>? required = null, IEnumerable<string>? blocked = null)
        {
            if (required != null) _requiredTags.AddRange(required);
            if (blocked != null) _blockedTags.AddRange(blocked);
        }

        public bool CanApply(EffectCalculationContext context)
        {
            if (context.Target == null) return false;

            foreach (var tag in _requiredTags)
            {
                if (!context.Target.Tags.HasTag(tag))
                    return false;
            }

            foreach (var tag in _blockedTags)
            {
                if (context.Target.Tags.HasTag(tag))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 概率条件 按概率应用
    /// </summary>
    public class ChanceCondition : IEffectCondition
    {
        private readonly float _probability;
        private readonly Random _random;

        public ChanceCondition(float probability, int? seed = null)
        {
            _probability = Math.Clamp(probability, 0f, 1f);
            _random = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        public bool CanApply(EffectCalculationContext context)
        {
            return _random.NextDouble() < _probability;
        }
    }

    /// <summary>
    /// 组合条件 AND
    /// </summary>
    public class AndCondition : IEffectCondition
    {
        private readonly List<IEffectCondition> _conditions;

        public AndCondition(params IEffectCondition[] conditions)
        {
            _conditions = conditions.ToList();
        }

        public bool CanApply(EffectCalculationContext context)
        {
            foreach (var cond in _conditions)
            {
                if (!cond.CanApply(context))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 组合条件 OR
    /// </summary>
    public class OrCondition : IEffectCondition
    {
        private readonly List<IEffectCondition> _conditions;

        public OrCondition(params IEffectCondition[] conditions)
        {
            _conditions = conditions.ToList();
        }

        public bool CanApply(EffectCalculationContext context)
        {
            foreach (var cond in _conditions)
            {
                if (cond.CanApply(context))
                    return true;
            }
            return false;
        }
    }

    #endregion

    #region 效果预制和组合

    /// <summary>
    /// 效果预制体 一组效果的组合
    /// 比如一个技能同时造成伤害+减速+灼烧
    /// </summary>
    public class EffectPreset
    {
        public int PresetId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<int> EffectIds { get; set; } = new();
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// 预制体仓库
    /// </summary>
    public class EffectPresetRepository
    {
        private readonly Dictionary<int, EffectPreset> _presets = new();
        private readonly object _lock = new();

        public bool Register(EffectPreset preset)
        {
            if (preset == null || preset.PresetId <= 0) return false;
            lock (_lock)
            {
                _presets[preset.PresetId] = preset;
                return true;
            }
        }

        public EffectPreset? GetById(int presetId)
        {
            lock (_lock)
            {
                _presets.TryGetValue(presetId, out var p);
                return p;
            }
        }

        /// <summary>
        /// 应用预制体到目标
        /// </summary>
        public int ApplyPreset(int presetId, GameplayEffectContainerFull target, GameplayAbilityComponent? source = null)
        {
            var preset = GetById(presetId);
            if (preset == null) return 0;

            int count = 0;
            foreach (var effectId in preset.EffectIds)
            {
                var spec = GameplayEffectSystem.Instance.Specs.GetById(effectId);
                if (spec != null)
                {
                    target.ApplyEffect(spec, source);
                    count++;
                }
            }
            return count;
        }
    }

    #endregion

    #region 效果UI数据

    /// <summary>
    /// 效果UI数据 提供给前端显示buff/debuff条
    /// </summary>
    public class EffectUIData
    {
        public long InstanceId { get; set; }
        public int EffectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public int StackCount { get; set; }
        public float RemainingDuration { get; set; }
        public float DurationProgress { get; set; }
        public bool IsBeneficial { get; set; }
        public bool IsDispellable { get; set; }
        public EffectDurationType DurationType { get; set; }
    }

    /// <summary>
    /// 效果UI数据收集器
    /// </summary>
    public static class EffectUIDataCollector
    {
        public static List<EffectUIData> Collect(GameplayEffectContainerFull container)
        {
            var result = new List<EffectUIData>();

            if (container == null) return result;

            foreach (var inst in container.GetAllActiveEffects())
            {
                var uiData = new EffectUIData
                {
                    InstanceId = inst.InstanceId,
                    EffectId = inst.Spec.EffectId,
                    Name = inst.Spec.Name,
                    Description = inst.Spec.Description,
                    IconPath = inst.Spec.IconPath,
                    StackCount = inst.StackCount,
                    RemainingDuration = inst.RemainingDuration,
                    DurationProgress = inst.GetDurationProgress(),
                    IsBeneficial = inst.Spec.IsBeneficial,
                    IsDispellable = inst.Spec.IsDispellable,
                    DurationType = inst.Spec.DurationType,
                };

                result.Add(uiData);
            }

            return result;
        }
    }

    #endregion

    #region 效果系统初始化辅助

    /// <summary>
    /// 效果系统启动器 注册默认效果
    /// </summary>
    public static class EffectSystemBootstrap
    {
        public static GameplayEffectSystem Initialize()
        {
            var system = GameplayEffectSystem.Instance;

            int id = 2001;
            system.Specs.Register(GameplayEffectFactory.CreateDamageEffect(id++, "BasicDamage", 10f));
            system.Specs.Register(GameplayEffectFactory.CreateHealEffect(id++, "BasicHeal", 20f));
            system.Specs.Register(GameplayEffectFactory.CreateDotEffect(id++, "Poison", 5f, 1f, 5f));
            system.Specs.Register(GameplayEffectFactory.CreateHotEffect(id++, "Regen", 8f, 2f, 10f));
            system.Specs.Register(GameplayEffectFactory.CreateStunEffect(id++, "Stun", 2f));
            system.Specs.Register(GameplayEffectFactory.CreateSlowEffect(id++, "Slow", 0.3f, 3f));
            system.Specs.Register(GameplayEffectFactory.CreateShieldEffect(id++, "Shield", 50f, 10f));
            system.Specs.Register(GameplayEffectFactory.CreateStatBuffEffect(id++, "AttackUp", "Attack", EffectOperator.PercentAdd, 0.5f, 10f));
            system.Specs.Register(GameplayEffectFactory.CreateStatBuffEffect(id++, "DefenseUp", "Defense", EffectOperator.Add, 20f, 10f));

            return system;
        }

        public static string RunSmokeTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Effect System Smoke Test ===");

            var system = Initialize();
            sb.AppendLine($"System initialized with {system.Specs.Count} specs");

            // 创建组件和容器
            var component = new GameplayAbilityComponent();
            component.Attributes = new GameplayAttributeSet();
            component.Attributes.SetAttribute("Health", 100f);
            component.Attributes.SetAttribute("Attack", 20f);
            component.Attributes.SetAttribute("MoveSpeed", 5f);

            var container = system.CreateContainer(component);

            // 应用伤害
            var damageSpec = system.Specs.GetByName("BasicDamage");
            if (damageSpec != null)
            {
                container.ApplyEffect(damageSpec);
                var health = component.Attributes.GetAttribute("Health");
                sb.AppendLine($"After damage: Health = {health?.CurrentValue}");
            }

            // 应用治疗
            var healSpec = system.Specs.GetByName("BasicHeal");
            if (healSpec != null)
            {
                container.ApplyEffect(healSpec);
                var health = component.Attributes.GetAttribute("Health");
                sb.AppendLine($"After heal: Health = {health?.CurrentValue}");
            }

            // 应用持续伤害
            var poisonSpec = system.Specs.GetByName("Poison");
            if (poisonSpec != null)
            {
                container.ApplyEffect(poisonSpec);
                sb.AppendLine($"Poison applied, active effects: {container.ActiveEffectCount}");
            }

            // Tick几次
            for (int i = 0; i < 10; i++)
            {
                system.Tick(1f);
            }

            var healthAfter = component.Attributes.GetAttribute("Health");
            sb.AppendLine($"After 10 ticks of poison: Health = {healthAfter?.CurrentValue:F1}");
            sb.AppendLine($"Active effects: {container.ActiveEffectCount}");

            system.Shutdown();
            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

} // namespace YamaEngine.Gameplay
