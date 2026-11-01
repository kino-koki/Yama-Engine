// YamaEngine - yama游戏引擎
// GameplayAbilitySystemV2.cs - GAS V2 能力系统
// GAS这东西复杂得很 慢慢来 但功能是真的强
// 借鉴UE5的GAS设计 用C#重新实现一套

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Gameplay.Effects;

namespace YamaEngine.Gameplay
{
    #region 核心接口和枚举

    /// <summary>
    /// 能力网络同步模式 这个决定了能力怎么在网络里跑
    /// </summary>
    public enum AbilityNetSyncMode : byte
    {
        // 本地跑 不同步 适合纯客户端特效
        LocalOnly = 0,
        // 服务端跑 结果下发 适合伤害判定
        ServerOnly = 1,
        // 服务端授权 客户端预测 适合移动射击
        ServerAuthority = 2,
        // 全端跑 各自模拟 适合表现类能力
        AllClients = 3,
    }

    /// <summary>
    /// 能力激活结果 跟UE一样返回个枚举 方便调试
    /// </summary>
    public enum AbilityActivationResult : byte
    {
        // 激活成功
        Success = 0,
        // 能力在冷却中
        OnCooldown = 1,
        // 没蓝了 资源不足
        CostNotMet = 2,
        // 标签不满足 比如被沉默了
        TagsBlocked = 3,
        // 权限不够
        NoPermission = 4,
        // 能力没装到组件上
        NotGranted = 5,
        // 已经在跑了 不能重复激活
        AlreadyActive = 6,
        // 没有目标 这个也挺常见
        NoTarget = 7,
        // 内部出错了
        InternalError = 255,
    }

    /// <summary>
    /// 能力取消原因 停下来的理由各种各样
    /// </summary>
    public enum AbilityCancelReason : byte
    {
        // 自然结束
        NaturalCompletion = 0,
        // 被外部取消了 比如玩家手动取消
        ManualCancel = 1,
        // 被其他能力打断
        Interrupted = 2,
        // 宿主死了
        OwnerDead = 3,
        // 网络断开了
        NetworkDisconnect = 4,
        // 标签变了 不满足条件了
        TagConditionLost = 5,
    }

    /// <summary>
    /// 能力输入 这个对应玩家按键
    /// </summary>
    public enum AbilityInputId : byte
    {
        None = 0,
        Primary = 1,        // 鼠标左键 主攻击
        Secondary = 2,      // 鼠标右键 瞄准/格挡
        Ability1 = 3,
        Ability2 = 4,
        Ability3 = 5,
        Ability4 = 6,
        Ultimate = 7,       // 大招
        Interact = 8,       // 交互键E
        Jump = 9,
        Dash = 10,          // 闪避
    }

    /// <summary>
    /// 能力激活方式 怎么触发
    /// </summary>
    public enum AbilityTriggerType : byte
    {
        // 按下就触发
        OnInputPressed = 0,
        // 按住触发 持续型
        OnInputHeld = 1,
        // 松开触发 蓄力类
        OnInputReleased = 2,
        // 事件触发 比如被击中
        OnEvent = 3,
        // 自动触发 被动技能
        Passive = 4,
    }

    #endregion

    #region 能力规格定义

    /// <summary>
    /// 能力规格 一份能力的静态描述 跟UE的UGameplayAbility类似
    /// 这里装满了能力的元数据 各种参数都写在这
    /// </summary>
    public class AbilitySpec
    {
        // 能力唯一ID 配置表里给
        public int AbilityId { get; set; }

        // 能力名字 方便人看
        public string Name { get; set; } = string.Empty;

        // 描述 给UI显示的
        public string Description { get; set; } = string.Empty;

        // 网络同步模式
        public AbilityNetSyncMode NetSyncMode { get; set; } = AbilityNetSyncMode.ServerAuthority;

        // 触发方式
        public AbilityTriggerType TriggerType { get; set; } = AbilityTriggerType.OnInputPressed;

        // 绑定的输入
        public AbilityInputId InputId { get; set; } = AbilityInputId.None;

        // 冷却时间 秒 0表示没冷却
        public float CooldownDuration { get; set; } = 0f;

        // 冷却用的效果ID 留着扩展
        public int CooldownEffectId { get; set; } = 0;

        // 消耗属性 比如蓝量
        public Dictionary<string, float> Costs { get; set; } = new();

        // 激活需要的标签 没这些标签就别想放
        public List<string> ActivationRequiredTags { get; set; } = new();

        // 激活时禁止的标签 有这些标签也放不出
        public List<string> ActivationBlockedTags { get; set; } = new();

        // 激活后给自身加的标签
        public List<string> ActivationOwnedTags { get; set; } = new();

        // 能力持续时长 -1表示持续到手动取消
        public float Duration { get; set; } = 0f;

        // 优先级 高优先级可以打断低优先级
        public int Priority { get; set; } = 0;

        // 能力实例类名 用于反射创建实例
        public string InstanceTypeName { get; set; } = string.Empty;

        // 是否允许同时多个实例跑
        public bool AllowMultipleInstances { get; set; } = false;

        // 冷却开始时刻的偏移 用于动画对齐
        public float CooldownStartDelay { get; set; } = 0f;

        // 冷却共享组 同组的共享冷却
        public string CooldownGroup { get; set; } = string.Empty;

        // 施法距离 0表示无限制
        public float CastRange { get; set; } = 0f;

        // 施法半径 范围技能用
        public float CastRadius { get; set; } = 0f;

        // 目标选择器配置 跟TargetingSystem对接
        public string TargetingConfigName { get; set; } = "Self";

        // 触发的Cue列表 视觉音频反馈
        public List<int> CueIds { get; set; } = new();

        // 应用效果列表 激活时立即应用
        public List<int> ApplyEffectIds { get; set; } = new();

        // 能力图标资源路径 UI用
        public string IconPath { get; set; } = string.Empty;

        // 动画蒙太奇ID 激活时播放
        public int AnimMontageId { get; set; } = 0;

        // 是否在动画事件点触发实际效果
        public bool WaitAnimNotify { get; set; } = false;

        // 施法前摇时间 动画前的等待
        public float CastTime { get; set; } = 0f;

        // 后摇时间
        public float RecoveryTime { get; set; } = 0f;

        // 是否被移动打断
        public bool CancelOnMove { get; set; } = false;

        // 是否被伤害打断
        public bool CancelOnDamage { get; set; } = false;

        // 元数据 自定义键值对 扩展用
        public Dictionary<string, string> Metadata { get; set; } = new();

        // ── 运行时成员 (由 AbilitySystemComponent 管理) ──

        /// <summary>能力定义。</summary>
        public GameplayAbility? Def { get; set; }

        /// <summary>在 ASC 中的句柄。</summary>
        public int Handle { get; set; }

        /// <summary>赋予等级 (影响可缩放数值)。</summary>
        public float Level { get; set; } = 1f;

        /// <summary>当前运行状态。</summary>
        public EAbilityState State { get; set; } = EAbilityState.Idle;

        /// <summary>该能力当前活跃的任务列表。</summary>
        public List<AbilityTask> ActiveTasks { get; } = new();

        /// <summary>无参构造 (用于静态配置/克隆)。</summary>
        public AbilitySpec() { }

        /// <summary>构造一个能力运行时实例。</summary>
        public AbilitySpec(GameplayAbility def, int handle)
        {
            Def = def;
            Handle = handle;
        }

        /// <summary>尝试激活能力。</summary>
        public bool TryActivate(AbilitySystemComponent asc, EffectContext? context = null)
        {
            if (asc == null || Def == null) return false;
            if (State == EAbilityState.Active || State == EAbilityState.Activating) return false;
            if (!Def.CanActivateAbility(asc, context)) return false;
            State = EAbilityState.Activating;
            Def.ActivateAbility(asc, context);
            return true;
        }

        /// <summary>取消能力: 取消活跃任务并委托 ASC 清理与状态切换。</summary>
        public void Cancel(AbilitySystemComponent asc)
        {
            if (asc == null || Def == null) return;
            foreach (var t in ActiveTasks) t.Cancel();
            ActiveTasks.Clear();
            Def.CancelAbility(asc);
        }

        /// <summary>结束能力: 取消活跃任务并委托 ASC 清理与状态切换。</summary>
        public void End(AbilitySystemComponent asc)
        {
            if (asc == null || Def == null) return;
            foreach (var t in ActiveTasks) t.Cancel();
            ActiveTasks.Clear();
            Def.EndAbility(asc);
        }

        /// <summary>每帧 Tick 活跃任务, 并移除已完成的任务。</summary>
        public void TickTasks(float dt)
        {
            for (int i = ActiveTasks.Count - 1; i >= 0; i--)
            {
                var t = ActiveTasks[i];
                if (t.IsCompleted)
                {
                    ActiveTasks.RemoveAt(i);
                    continue;
                }
                if (t.IsActive) t.Tick(dt);
            }
        }

        // 从配置克隆一份 避免修改原始数据
        public AbilitySpec Clone()
        {
            var clone = new AbilitySpec
            {
                AbilityId = AbilityId,
                Name = Name,
                Description = Description,
                NetSyncMode = NetSyncMode,
                TriggerType = TriggerType,
                InputId = InputId,
                CooldownDuration = CooldownDuration,
                CooldownEffectId = CooldownEffectId,
                Duration = Duration,
                Priority = Priority,
                InstanceTypeName = InstanceTypeName,
                AllowMultipleInstances = AllowMultipleInstances,
                CooldownStartDelay = CooldownStartDelay,
                CooldownGroup = CooldownGroup,
                CastRange = CastRange,
                CastRadius = CastRadius,
                TargetingConfigName = TargetingConfigName,
                IconPath = IconPath,
                AnimMontageId = AnimMontageId,
                WaitAnimNotify = WaitAnimNotify,
                CastTime = CastTime,
                RecoveryTime = RecoveryTime,
                CancelOnMove = CancelOnMove,
                CancelOnDamage = CancelOnDamage,
            };

            foreach (var kv in Costs)
                clone.Costs[kv.Key] = kv.Value;

            foreach (var t in ActivationRequiredTags)
                clone.ActivationRequiredTags.Add(t);

            foreach (var t in ActivationBlockedTags)
                clone.ActivationBlockedTags.Add(t);

            foreach (var t in ActivationOwnedTags)
                clone.ActivationOwnedTags.Add(t);

            foreach (var c in CueIds)
                clone.CueIds.Add(c);

            foreach (var e in ApplyEffectIds)
                clone.ApplyEffectIds.Add(e);

            foreach (var kv in Metadata)
                clone.Metadata[kv.Key] = kv.Value;

            return clone;
        }

        public override string ToString()
        {
            return $"[AbilitySpec {AbilityId}:{Name}]";
        }
    }

    /// <summary>
    /// 能力配置仓库 管理所有能力的静态配置
    /// 用ID索引查找很快
    /// </summary>
    public class AbilitySpecRepository
    {
        // 所有能力规格 按ID索引
        private readonly Dictionary<int, AbilitySpec> _specs = new();

        // 按名字索引 方便查找
        private readonly Dictionary<string, int> _nameToId = new();

        // 按输入绑定索引 方便快速找能力
        private readonly Dictionary<AbilityInputId, List<int>> _inputBindings = new();

        // 读锁 防止并发修改
        private readonly ReaderWriterLockSlim _lock = new();

        /// <summary>
        /// 注册一个能力规格
        /// </summary>
        public bool Register(AbilitySpec spec)
        {
            if (spec == null) return false;
            if (spec.AbilityId <= 0) return false;

            _lock.EnterWriteLock();
            try
            {
                if (_specs.ContainsKey(spec.AbilityId))
                {
                    // 已经存在了 重复注册
                    return false;
                }

                _specs[spec.AbilityId] = spec;

                if (!string.IsNullOrEmpty(spec.Name))
                {
                    _nameToId[spec.Name] = spec.AbilityId;
                }

                if (spec.InputId != AbilityInputId.None)
                {
                    if (!_inputBindings.TryGetValue(spec.InputId, out var list))
                    {
                        list = new List<int>();
                        _inputBindings[spec.InputId] = list;
                    }
                    list.Add(spec.AbilityId);
                }

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 注销能力规格
        /// </summary>
        public bool Unregister(int abilityId)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_specs.TryGetValue(abilityId, out var spec))
                    return false;

                _specs.Remove(abilityId);

                if (!string.IsNullOrEmpty(spec.Name))
                    _nameToId.Remove(spec.Name);

                if (spec.InputId != AbilityInputId.None)
                {
                    if (_inputBindings.TryGetValue(spec.InputId, out var list))
                    {
                        list.Remove(abilityId);
                        if (list.Count == 0)
                            _inputBindings.Remove(spec.InputId);
                    }
                }

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 根据ID获取能力规格
        /// </summary>
        public AbilitySpec? GetById(int abilityId)
        {
            _lock.EnterReadLock();
            try
            {
                _specs.TryGetValue(abilityId, out var spec);
                return spec;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 根据名字获取能力规格
        /// </summary>
        public AbilitySpec? GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            _lock.EnterReadLock();
            try
            {
                if (!_nameToId.TryGetValue(name, out var id))
                    return null;
                _specs.TryGetValue(id, out var spec);
                return spec;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 获取绑定了某个输入的所有能力ID
        /// </summary>
        public List<int> GetAbilitiesByInput(AbilityInputId inputId)
        {
            _lock.EnterReadLock();
            try
            {
                if (_inputBindings.TryGetValue(inputId, out var list))
                    return new List<int>(list);
                return new List<int>();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 获取所有能力规格 这是个拷贝 避免外部修改
        /// </summary>
        public List<AbilitySpec> GetAll()
        {
            _lock.EnterReadLock();
            try
            {
                return _specs.Values.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 清空所有配置
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _specs.Clear();
                _nameToId.Clear();
                _inputBindings.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 当前注册的能力总数
        /// </summary>
        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _specs.Count;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }
    }

    #endregion

    #region 能力实例

    /// <summary>
    /// 能力实例 运行时的能力状态 每次激活创建一个
    /// 跟UE的FGameplayAbilitySpec差不多 但融合了实例逻辑
    /// </summary>
    public class AbilityInstance
    {
        // 唯一实例ID 全局自增
        public long InstanceId { get; internal set; }

        // 引用能力规格
        public AbilitySpec Spec { get; }

        // 拥有这个能力的组件
        public GameplayAbilityComponent? OwnerComponent { get; internal set; }

        // 当前等级 1级起步
        public int Level { get; set; } = 1;

        // 是否激活中
        public bool IsActive { get; internal set; }

        // 激活时间戳
        public float ActivationTime { get; internal set; }

        // 已经持续的时间
        public float ElapsedTime { get; internal set; }

        // 上次激活时间 用于计算冷却
        public float LastActivationTime { get; internal set; } = -9999f;

        // 实例自定义数据 扩展用
        public Dictionary<string, object?> CustomData { get; } = new();

        // 状态机 跟踪能力当前阶段
        public AbilityInstanceState State { get; internal set; } = AbilityInstanceState.Inactive;

        // 事件回调
        public event Action<AbilityInstance, AbilityActivationResult>? OnActivated;
        public event Action<AbilityInstance, AbilityCancelReason>? OnEnded;
        public event Action<AbilityInstance>? OnTick;

        // 是否已经结束 防止重复结束
        internal bool _ended;

        public AbilityInstance(AbilitySpec spec)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        }

        /// <summary>
        /// 当前是否在冷却中
        /// </summary>
        public bool IsOnCooldown(float currentTime)
        {
            if (Spec.CooldownDuration <= 0f)
                return false;

            float timeSinceLast = currentTime - LastActivationTime;
            return timeSinceLast < Spec.CooldownDuration;
        }

        /// <summary>
        /// 获取剩余冷却时间
        /// </summary>
        public float GetCooldownRemaining(float currentTime)
        {
            if (Spec.CooldownDuration <= 0f)
                return 0f;

            float timeSinceLast = currentTime - LastActivationTime;
            float remaining = Spec.CooldownDuration - timeSinceLast;
            return Math.Max(0f, remaining);
        }

        /// <summary>
        /// 获取冷却进度 0到1
        /// </summary>
        public float GetCooldownProgress(float currentTime)
        {
            if (Spec.CooldownDuration <= 0f)
                return 1f;

            float remaining = GetCooldownRemaining(currentTime);
            return 1f - (remaining / Spec.CooldownDuration);
        }

        /// <summary>
        /// 内部激活 调用回调
        /// </summary>
        internal void InvokeActivated(AbilityActivationResult result)
        {
            IsActive = (result == AbilityActivationResult.Success);
            OnActivated?.Invoke(this, result);
        }

        /// <summary>
        /// 内部结束 调用回调
        /// </summary>
        internal void InvokeEnded(AbilityCancelReason reason)
        {
            if (_ended) return;
            _ended = true;
            IsActive = false;
            State = AbilityInstanceState.Inactive;
            OnEnded?.Invoke(this, reason);
        }

        /// <summary>
        /// 内部Tick 调用回调
        /// </summary>
        internal void InvokeTick()
        {
            OnTick?.Invoke(this);
        }

        public override string ToString()
        {
            return $"[AbilityInstance {InstanceId} {Spec.Name} L{Level} {(IsActive ? "Active" : "Inactive")}]";
        }
    }

    /// <summary>
    /// 能力实例状态机
    /// 跟踪能力处于哪个阶段
    /// </summary>
    public enum AbilityInstanceState : byte
    {
        // 未激活
        Inactive = 0,
        // 前摇中
        Casting = 1,
        // 激活执行中
        Active = 2,
        // 后摇中
        Recovering = 3,
        // 等待动画事件
        WaitingAnimNotify = 4,
        // 已结束
        Ended = 5,
    }

    #endregion

    #region 能力组件

    /// <summary>
    /// 能力组件 挂在角色上的核心组件
    /// 跟UE的UAbilitySystemComponent对齐
    /// 这个组件管理所有能力的激活/取消/Tick
    /// </summary>
    public class GameplayAbilityComponent
    {
        // 组件所属的Actor引用 弱引用避免循环
        public object? OwnerActor { get; set; }

        // 组件ID 全局唯一
        public long ComponentId { get; internal set; }

        // 所有已授予的能力实例 按ID索引
        private readonly Dictionary<int, AbilityInstance> _grantedAbilities = new();

        // 正在激活中的能力实例 按实例ID索引
        private readonly Dictionary<long, AbilityInstance> _activeAbilities = new();

        // 标签容器 跟TagSystem对接
        public GameplayTagContainer Tags { get; } = new GameplayTagContainer();

        // 属性容器 跟AttributeSystem对接
        public GameplayAttributeSet? Attributes { get; set; }

        // 效果容器 跟EffectSystem对接
        public GameplayEffectContainer? Effects { get; set; }

        // 冷却组记录 跟CooldownGroup关联
        private readonly Dictionary<string, float> _cooldownGroupLastTime = new();

        // 实例ID自增器
        private static long _nextInstanceId = 1;

        // 组件ID自增器
        private static long _nextComponentId = 1;

        // 锁 保护并发激活
        private readonly object _lock = new();

        // 当前游戏时间 由外部更新
        public float CurrentTime { get; set; }

        // 能力激活事件 全局监听用
        public event Action<GameplayAbilityComponent, AbilityInstance, AbilityActivationResult>? AbilityActivated;

        // 能力结束事件
        public event Action<GameplayAbilityComponent, AbilityInstance, AbilityCancelReason>? AbilityEnded;

        // 能力被授予事件
        public event Action<GameplayAbilityComponent, AbilityInstance>? AbilityGranted;

        // 能力被移除事件
        public event Action<GameplayAbilityComponent, AbilityInstance>? AbilityRemoved;

        /// <summary>
        /// 构造 自动分配ID
        /// </summary>
        public GameplayAbilityComponent()
        {
            ComponentId = Interlocked.Increment(ref _nextComponentId);
        }

        /// <summary>
        /// 授予能力 把能力规格装到组件上
        /// </summary>
        public AbilityInstance? GrantAbility(AbilitySpec spec, int level = 1)
        {
            if (spec == null) return null;

            lock (_lock)
            {
                // 已经有相同能力了 看是否允许升级
                if (_grantedAbilities.TryGetValue(spec.AbilityId, out var existing))
                {
                    existing.Level = Math.Max(existing.Level, level);
                    return existing;
                }

                var instance = new AbilityInstance(spec.Clone())
                {
                    InstanceId = Interlocked.Increment(ref _nextInstanceId),
                    OwnerComponent = this,
                    Level = level,
                };

                _grantedAbilities[spec.AbilityId] = instance;
                AbilityGranted?.Invoke(this, instance);
                return instance;
            }
        }

        /// <summary>
        /// 移除能力
        /// </summary>
        public bool RemoveAbility(int abilityId)
        {
            lock (_lock)
            {
                if (!_grantedAbilities.TryGetValue(abilityId, out var instance))
                    return false;

                // 如果在激活中 先取消
                if (instance.IsActive)
                {
                    CancelAbility(instance, AbilityCancelReason.ManualCancel);
                }

                _grantedAbilities.Remove(abilityId);
                AbilityRemoved?.Invoke(this, instance);
                return true;
            }
        }

        /// <summary>
        /// 获取已授予的能力实例
        /// </summary>
        public AbilityInstance? GetGrantedAbility(int abilityId)
        {
            lock (_lock)
            {
                _grantedAbilities.TryGetValue(abilityId, out var inst);
                return inst;
            }
        }

        /// <summary>
        /// 获取所有已授予能力
        /// </summary>
        public List<AbilityInstance> GetAllGrantedAbilities()
        {
            lock (_lock)
            {
                return _grantedAbilities.Values.ToList();
            }
        }

        /// <summary>
        /// 获取所有激活中的能力
        /// </summary>
        public List<AbilityInstance> GetActiveAbilities()
        {
            lock (_lock)
            {
                return _activeAbilities.Values.ToList();
            }
        }

        /// <summary>
        /// 尝试激活能力
        /// 这是核心入口 会做各种检查
        /// </summary>
        public AbilityActivationResult TryActivateAbility(int abilityId)
        {
            lock (_lock)
            {
                if (!_grantedAbilities.TryGetValue(abilityId, out var instance))
                    return AbilityActivationResult.NotGranted;

                return ActivateInternal(instance);
            }
        }

        /// <summary>
        /// 通过输入ID激活能力
        /// 玩家按键时走这个
        /// </summary>
        public List<AbilityActivationResult> TryActivateByInput(AbilityInputId inputId, AbilityTriggerType triggerType)
        {
            var results = new List<AbilityActivationResult>();

            lock (_lock)
            {
                foreach (var inst in _grantedAbilities.Values)
                {
                    if (inst.Spec.InputId != inputId)
                        continue;

                    if (inst.Spec.TriggerType != triggerType)
                        continue;

                    results.Add(ActivateInternal(inst));
                }
            }

            return results;
        }

        /// <summary>
        /// 内部激活逻辑 各种检查都在这
        /// </summary>
        private AbilityActivationResult ActivateInternal(AbilityInstance instance)
        {
            // 已经在跑了 检查是否允许多实例
            if (instance.IsActive)
            {
                if (!instance.Spec.AllowMultipleInstances)
                    return AbilityActivationResult.AlreadyActive;
            }

            // 冷却检查
            if (instance.IsOnCooldown(CurrentTime))
                return AbilityActivationResult.OnCooldown;

            // 冷却组检查 同组共享冷却
            if (!string.IsNullOrEmpty(instance.Spec.CooldownGroup))
            {
                if (_cooldownGroupLastTime.TryGetValue(instance.Spec.CooldownGroup, out var lastTime))
                {
                    float elapsed = CurrentTime - lastTime;
                    if (elapsed < instance.Spec.CooldownDuration)
                        return AbilityActivationResult.OnCooldown;
                }
            }

            // 资源消耗检查 比如蓝量
            if (Attributes != null && instance.Spec.Costs.Count > 0)
            {
                foreach (var cost in instance.Spec.Costs)
                {
                    var attr = Attributes.GetAttribute(cost.Key);
                    if (attr == null || attr.CurrentValue < cost.Value)
                        return AbilityActivationResult.CostNotMet;
                }
            }

            // 标签检查 激活需要的标签
            if (instance.Spec.ActivationRequiredTags.Count > 0)
            {
                foreach (var tag in instance.Spec.ActivationRequiredTags)
                {
                    if (!Tags.HasTag(tag))
                        return AbilityActivationResult.TagsBlocked;
                }
            }

            // 标签检查 激活禁止的标签
            if (instance.Spec.ActivationBlockedTags.Count > 0)
            {
                foreach (var tag in instance.Spec.ActivationBlockedTags)
                {
                    if (Tags.HasTag(tag))
                        return AbilityActivationResult.TagsBlocked;
                }
            }

            // 检查通过 开始激活
            instance.IsActive = true;
            instance.ActivationTime = CurrentTime;
            instance.LastActivationTime = CurrentTime;
            instance.ElapsedTime = 0f;
            instance._ended = false;
            instance.State = instance.Spec.CastTime > 0
                ? AbilityInstanceState.Casting
                : AbilityInstanceState.Active;

            _activeAbilities[instance.InstanceId] = instance;

            // 扣除资源消耗
            if (Attributes != null && instance.Spec.Costs.Count > 0)
            {
                foreach (var cost in instance.Spec.Costs)
                {
                    Attributes.ModifyAttribute(cost.Key, -cost.Value);
                }
            }

            // 记录冷却组
            if (!string.IsNullOrEmpty(instance.Spec.CooldownGroup))
            {
                _cooldownGroupLastTime[instance.Spec.CooldownGroup] = CurrentTime;
            }

            // 给自身加标签
            if (instance.Spec.ActivationOwnedTags.Count > 0)
            {
                foreach (var tag in instance.Spec.ActivationOwnedTags)
                {
                    Tags.AddTag(tag);
                }
            }

            // 触发回调
            instance.InvokeActivated(AbilityActivationResult.Success);
            AbilityActivated?.Invoke(this, instance, AbilityActivationResult.Success);

            return AbilityActivationResult.Success;
        }

        /// <summary>
        /// 取消能力
        /// </summary>
        public bool CancelAbility(AbilityInstance instance, AbilityCancelReason reason)
        {
            if (instance == null) return false;

            lock (_lock)
            {
                if (!instance.IsActive) return false;

                // 移除激活时加的标签
                if (instance.Spec.ActivationOwnedTags.Count > 0)
                {
                    foreach (var tag in instance.Spec.ActivationOwnedTags)
                    {
                        Tags.RemoveTag(tag);
                    }
                }

                _activeAbilities.Remove(instance.InstanceId);
                instance.InvokeEnded(reason);
                AbilityEnded?.Invoke(this, instance, reason);
                return true;
            }
        }

        /// <summary>
        /// 取消所有激活中的能力
        /// </summary>
        public void CancelAllAbilities(AbilityCancelReason reason)
        {
            List<AbilityInstance> toCancel;
            lock (_lock)
            {
                toCancel = _activeAbilities.Values.ToList();
            }

            foreach (var inst in toCancel)
            {
                CancelAbility(inst, reason);
            }
        }

        /// <summary>
        /// 按优先级打断所有低优先级能力
        /// </summary>
        public int InterruptLowerPriority(int minPriority, AbilityCancelReason reason)
        {
            int count = 0;
            List<AbilityInstance> toCancel;

            lock (_lock)
            {
                toCancel = _activeAbilities.Values
                    .Where(a => a.Spec.Priority < minPriority)
                    .ToList();
            }

            foreach (var inst in toCancel)
            {
                CancelAbility(inst, reason);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Tick 更新所有激活中的能力
        /// 每帧调用
        /// </summary>
        public void Tick(float deltaTime)
        {
            CurrentTime += deltaTime;

            List<AbilityInstance> toTick;
            List<(AbilityInstance, AbilityCancelReason)> toEnd = new();

            lock (_lock)
            {
                toTick = _activeAbilities.Values.ToList();
            }

            foreach (var inst in toTick)
            {
                inst.ElapsedTime += deltaTime;

                // 状态机推进
                switch (inst.State)
                {
                    case AbilityInstanceState.Casting:
                        if (inst.ElapsedTime >= inst.Spec.CastTime)
                        {
                            inst.State = AbilityInstanceState.Active;
                            inst.ElapsedTime = 0f;
                        }
                        break;

                    case AbilityInstanceState.Active:
                        // 持续时间检查
                        if (inst.Spec.Duration > 0f && inst.ElapsedTime >= inst.Spec.Duration)
                        {
                            if (inst.Spec.RecoveryTime > 0f)
                            {
                                inst.State = AbilityInstanceState.Recovering;
                                inst.ElapsedTime = 0f;
                            }
                            else
                            {
                                toEnd.Add((inst, AbilityCancelReason.NaturalCompletion));
                            }
                        }
                        else if (inst.Spec.Duration < 0f)
                        {
                            // 持续到手动取消 不自动结束
                        }
                        else if (inst.Spec.Duration == 0f)
                        {
                            // 瞬发能力 立即结束
                            if (inst.Spec.RecoveryTime > 0f)
                            {
                                inst.State = AbilityInstanceState.Recovering;
                                inst.ElapsedTime = 0f;
                            }
                            else
                            {
                                toEnd.Add((inst, AbilityCancelReason.NaturalCompletion));
                            }
                        }
                        break;

                    case AbilityInstanceState.Recovering:
                        if (inst.ElapsedTime >= inst.Spec.RecoveryTime)
                        {
                            toEnd.Add((inst, AbilityCancelReason.NaturalCompletion));
                        }
                        break;

                    case AbilityInstanceState.WaitingAnimNotify:
                        // 等待动画事件 不自动推进
                        break;
                }

                inst.InvokeTick();
            }

            // 处理需要结束的能力
            foreach (var (inst, reason) in toEnd)
            {
                CancelAbility(inst, reason);
            }
        }

        /// <summary>
        /// 当Owner死亡时调用 取消所有能力
        /// </summary>
        public void OnOwnerDeath()
        {
            CancelAllAbilities(AbilityCancelReason.OwnerDead);
        }

        /// <summary>
        /// 当受到伤害时检查是否打断某些能力
        /// </summary>
        public void OnDamaged()
        {
            List<AbilityInstance> toCancel;
            lock (_lock)
            {
                toCancel = _activeAbilities.Values
                    .Where(a => a.Spec.CancelOnDamage)
                    .ToList();
            }

            foreach (var inst in toCancel)
            {
                CancelAbility(inst, AbilityCancelReason.Interrupted);
            }
        }

        /// <summary>
        /// 当移动时检查是否打断某些能力
        /// </summary>
        public void OnMove()
        {
            List<AbilityInstance> toCancel;
            lock (_lock)
            {
                toCancel = _activeAbilities.Values
                    .Where(a => a.Spec.CancelOnMove)
                    .ToList();
            }

            foreach (var inst in toCancel)
            {
                CancelAbility(inst, AbilityCancelReason.Interrupted);
            }
        }

        /// <summary>
        /// 获取某个能力的冷却剩余时间
        /// </summary>
        public float GetCooldownRemaining(int abilityId)
        {
            lock (_lock)
            {
                if (!_grantedAbilities.TryGetValue(abilityId, out var inst))
                    return 0f;
                return inst.GetCooldownRemaining(CurrentTime);
            }
        }

        /// <summary>
        /// 强制重置某个能力的冷却
        /// </summary>
        public void ResetCooldown(int abilityId)
        {
            lock (_lock)
            {
                if (!_grantedAbilities.TryGetValue(abilityId, out var inst))
                    return;

                inst.LastActivationTime = -9999f;

                if (!string.IsNullOrEmpty(inst.Spec.CooldownGroup))
                {
                    _cooldownGroupLastTime.Remove(inst.Spec.CooldownGroup);
                }
            }
        }

        /// <summary>
        /// 清空所有能力
        /// </summary>
        public void ClearAllAbilities()
        {
            CancelAllAbilities(AbilityCancelReason.ManualCancel);

            lock (_lock)
            {
                _grantedAbilities.Clear();
                _cooldownGroupLastTime.Clear();
            }
        }
    }

    #endregion

    #region 标签容器 占位类型 真实实现在GameplayTagSystem

    /// <summary>
    /// 标签容器 占位类型 真实实现在GameplayTagSystem.cs
    /// 这里只定义接口 避免循环依赖
    /// </summary>

    #endregion

    #region 属性集 占位类型 真实实现在GameplayAttributeSystem

    /// <summary>
    /// 属性集 占位类型 真实实现在GameplayAttributeSystem.cs
    /// </summary>
    public class GameplayAttributeSet
    {
        private readonly Dictionary<string, GameplayAttribute> _attributes = new();
        private readonly Dictionary<string, List<AttributeModifier>> _attributeModifiers = new();
        private readonly object _lock = new();

        public GameplayAttribute? GetAttribute(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _attributes.TryGetValue(name, out var attr);
                return attr;
            }
        }

        public void SetAttribute(string name, float baseValue)
        {
            lock (_lock)
            {
                if (_attributes.TryGetValue(name, out var existing))
                {
                    existing.BaseValue = baseValue;
                }
                else
                {
                    _attributes[name] = new GameplayAttribute(name, baseValue);
                }
            }
        }

        public bool ModifyAttribute(string name, float delta)
        {
            lock (_lock)
            {
                if (!_attributes.TryGetValue(name, out var attr))
                    return false;
                attr.CurrentValue += delta;
                return true;
            }
        }

        public List<GameplayAttribute> GetAllAttributes()
        {
            lock (_lock)
            {
                return _attributes.Values.ToList();
            }
        }

        /// <summary>
        /// 为指定属性添加一个修饰器 (记录来源, 并累加到当前值)。
        /// </summary>
        public void AddModifier(string attributeName, AttributeModifier mod)
        {
            if (string.IsNullOrEmpty(attributeName) || mod == null) return;
            lock (_lock)
            {
                if (!_attributeModifiers.TryGetValue(attributeName, out var list))
                {
                    list = new List<AttributeModifier>();
                    _attributeModifiers[attributeName] = list;
                }
                list.Add(mod);

                if (_attributes.TryGetValue(attributeName, out var attr))
                    attr.CurrentValue += mod.Magnitude;
            }
        }

        /// <summary>
        /// 按来源标识移除指定属性上的修饰器, 返回移除数量。
        /// </summary>
        public int RemoveModifiersBySource(string sourceName, string sourceKey)
        {
            int removed = 0;
            lock (_lock)
            {
                foreach (var kv in _attributeModifiers)
                {
                    removed += kv.Value.RemoveAll(m =>
                        m.SourceTag == sourceKey ||
                        (!string.IsNullOrEmpty(sourceName) && m.SourceTag == sourceName));
                }
            }
            return removed;
        }
    }

    /// <summary>
    /// 属性 占位类型
    /// </summary>

    #endregion

    #region 效果容器 占位类型 真实实现在GameplayEffectSystem

    /// <summary>
    /// 效果容器 占位类型 真实实现在GameplayEffectSystem.cs
    /// </summary>
    public class GameplayEffectContainer
    {
        private readonly List<int> _activeEffects = new();
        private readonly object _lock = new();

        public void ApplyEffect(int effectId)
        {
            lock (_lock)
            {
                _activeEffects.Add(effectId);
            }
        }

        public void RemoveEffect(int effectId)
        {
            lock (_lock)
            {
                _activeEffects.Remove(effectId);
            }
        }

        public List<int> GetActiveEffects()
        {
            lock (_lock)
            {
                return new List<int>(_activeEffects);
            }
        }

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
    }

    #endregion

    #region 能力系统管理器 全局单例

    /// <summary>
    /// 能力系统管理器 全局入口
    /// 管理所有AbilitySpec和AbilityComponent
    /// 提供能力查找/激活/取消的统一接口
    /// </summary>
    public class GameplayAbilitySystem
    {
        // 单例实例
        private static GameplayAbilitySystem? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// 单例访问
        /// </summary>
        public static GameplayAbilitySystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new GameplayAbilitySystem();
                    }
                }
                return _instance;
            }
        }

        // 能力规格仓库
        public AbilitySpecRepository Specs { get; } = new AbilitySpecRepository();

        // 所有注册的能力组件
        private readonly Dictionary<long, GameplayAbilityComponent> _components = new();
        private readonly object _componentsLock = new();

        // 全局事件 能力激活/结束
        public event Action<GameplayAbilityComponent, AbilityInstance, AbilityActivationResult>? GlobalAbilityActivated;
        public event Action<GameplayAbilityComponent, AbilityInstance, AbilityCancelReason>? GlobalAbilityEnded;

        // 全局时间
        private float _globalTime;

        /// <summary>
        /// 私有构造 单例
        /// </summary>
        private GameplayAbilitySystem() { }

        /// <summary>
        /// 注册能力组件
        /// </summary>
        public void RegisterComponent(GameplayAbilityComponent component)
        {
            if (component == null) return;

            lock (_componentsLock)
            {
                _components[component.ComponentId] = component;

                // 绑定事件 转发到全局
                component.AbilityActivated += (c, i, r) => GlobalAbilityActivated?.Invoke(c, i, r);
                component.AbilityEnded += (c, i, r) => GlobalAbilityEnded?.Invoke(c, i, r);
            }
        }

        /// <summary>
        /// 注销能力组件
        /// </summary>
        public void UnregisterComponent(GameplayAbilityComponent component)
        {
            if (component == null) return;

            lock (_componentsLock)
            {
                _components.Remove(component.ComponentId);
            }

            component.CancelAllAbilities(AbilityCancelReason.ManualCancel);
        }

        /// <summary>
        /// 获取所有组件
        /// </summary>
        public List<GameplayAbilityComponent> GetAllComponents()
        {
            lock (_componentsLock)
            {
                return _components.Values.ToList();
            }
        }

        /// <summary>
        /// 按ID获取组件
        /// </summary>
        public GameplayAbilityComponent? GetComponent(long componentId)
        {
            lock (_componentsLock)
            {
                _components.TryGetValue(componentId, out var c);
                return c;
            }
        }

        /// <summary>
        /// 全局Tick 更新所有组件
        /// </summary>
        public void Tick(float deltaTime)
        {
            _globalTime += deltaTime;

            List<GameplayAbilityComponent> components;
            lock (_componentsLock)
            {
                components = _components.Values.ToList();
            }

            foreach (var comp in components)
            {
                comp.Tick(deltaTime);
            }
        }

        /// <summary>
        /// 当前全局时间
        /// </summary>
        public float GlobalTime => _globalTime;

        /// <summary>
        /// 从配置表批量加载能力规格
        /// 配置表格式: List&lt;AbilitySpec&gt;
        /// </summary>
        public void LoadSpecsFromConfig(IEnumerable<AbilitySpec> specs)
        {
            if (specs == null) return;

            foreach (var spec in specs)
            {
                Specs.Register(spec);
            }
        }

        /// <summary>
        /// 清空所有数据
        /// </summary>
        public void Shutdown()
        {
            lock (_componentsLock)
            {
                foreach (var comp in _components.Values)
                {
                    comp.CancelAllAbilities(AbilityCancelReason.ManualCancel);
                }
                _components.Clear();
            }

            Specs.Clear();
        }
    }

    #endregion

    #region 能力任务系统 异步任务

    /// <summary>
    /// 能力任务 异步执行的能力逻辑
    /// 比如等待一段时间 等待输入 等待动画事件
    /// 借鉴UE的Kismet/AbilityTask
    /// </summary>
    public abstract class AbilityTask
    {
        // 任务所属的能力实例
        public AbilityInstance? OwnerAbility { get; internal set; }

        // 旧模型 ASC (由 Init 注入, 兼容 GameplayAbilitySystem.cs 的任务类)
        private YamaEngine.Gameplay.AbilitySystemComponent? _ownerAsc;

        /// <summary>所属 ASC(旧模型, 由 <see cref="Init"/> 注入)。</summary>
        protected YamaEngine.Gameplay.AbilitySystemComponent? OwnerASC => _ownerAsc;

        // 任务是否完成
        public bool IsCompleted { get; private set; }

        // 任务是否激活
        public bool IsActive { get; private set; }

        // 完成回调
        public event Action<AbilityTask>? OnCompleted;

        /// <summary>
        /// 激活任务
        /// </summary>
        public void Activate()
        {
            if (IsActive || IsCompleted) return;
            IsActive = true;
            OnActivate();
        }

        /// <summary>
        /// 子类实现 激活逻辑
        /// </summary>
        protected abstract void OnActivate();

        /// <summary>
        /// Tick更新
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!IsActive || IsCompleted) return;
            OnTick(deltaTime);
        }

        /// <summary>
        /// 子类实现 Tick逻辑
        /// </summary>
        protected virtual void OnTick(float deltaTime) { }

        /// <summary>
        /// 完成任务
        /// </summary>
        protected void Complete()
        {
            if (IsCompleted) return;
            IsCompleted = true;
            IsActive = false;
            OnCompleted?.Invoke(this);
        }

        /// <summary>
        /// 完成任务(与 <see cref="Complete"/> 等价)。
        /// </summary>
        protected void Finish() => Complete();

        /// <summary>
        /// 旧模型初始化(兼容 GameplayAbilitySystem.cs 的 StartTask/PlayMontageTask)。
        /// 默认仅注入 ASC 引用; 子类可重写以适配。
        /// </summary>
        public virtual void Init(YamaEngine.Gameplay.GameplayAbility ability, YamaEngine.Gameplay.AbilitySystemComponent asc)
        {
            _ownerAsc = asc;
        }

        /// <summary>
        /// 完成事件(兼容旧模型命名, 委托到 <see cref="OnCompleted"/>)。
        /// </summary>
        public event Action<AbilityTask>? Completed
        {
            add => OnCompleted += value;
            remove => OnCompleted -= value;
        }

        /// <summary>
        /// 取消任务
        /// </summary>
        public void Cancel()
        {
            if (IsCompleted) return;
            IsCompleted = true;
            IsActive = false;
            OnCancel();
            OnCompleted?.Invoke(this);
        }

        /// <summary>
        /// 子类实现 取消逻辑
        /// </summary>
        protected virtual void OnCancel() { }
    }

    /// <summary>
    /// 等待任务 等待指定时间后完成
    /// </summary>
    public class WaitTask : AbilityTask
    {
        private readonly float _duration;
        private float _elapsed;

        public WaitTask(float duration)
        {
            _duration = duration;
        }

        protected override void OnActivate() { }

        protected override void OnTick(float deltaTime)
        {
            _elapsed += deltaTime;
            if (_elapsed >= _duration)
            {
                Complete();
            }
        }
    }

    /// <summary>
    /// 等待输入任务 等待玩家按下某个输入
    /// </summary>
    public class WaitForInputTask : AbilityTask
    {
        private readonly AbilityInputId _inputId;
        private bool _inputReceived;

        public WaitForInputTask(AbilityInputId inputId)
        {
            _inputId = inputId;
        }

        public void NotifyInput(AbilityInputId inputId)
        {
            if (inputId == _inputId)
            {
                _inputReceived = true;
            }
        }

        protected override void OnActivate() { }

        protected override void OnTick(float deltaTime)
        {
            if (_inputReceived)
            {
                Complete();
            }
        }
    }

    /// <summary>
    /// 定时循环任务 每隔一段时间触发一次回调
    /// </summary>
    public class LoopTask : AbilityTask
    {
        private readonly float _interval;
        private float _elapsed;
        private readonly Action _callback;
        private readonly int _maxIterations;
        private int _currentIteration;

        public LoopTask(float interval, Action callback, int maxIterations = -1)
        {
            _interval = interval;
            _callback = callback;
            _maxIterations = maxIterations;
        }

        protected override void OnActivate() { }

        protected override void OnTick(float deltaTime)
        {
            _elapsed += deltaTime;
            if (_elapsed >= _interval)
            {
                _elapsed = 0f;
                _callback?.Invoke();
                _currentIteration++;

                if (_maxIterations > 0 && _currentIteration >= _maxIterations)
                {
                    Complete();
                }
            }
        }
    }

    /// <summary>
    /// 能力任务管理器 跟踪一个能力实例的所有任务
    /// </summary>
    public class AbilityTaskManager
    {
        private readonly List<AbilityTask> _tasks = new();
        private readonly object _lock = new();

        /// <summary>
        /// 添加并激活任务
        /// </summary>
        public T AddTask<T>(T task) where T : AbilityTask
        {
            if (task == null) return task;

            lock (_lock)
            {
                _tasks.Add(task);
            }

            task.Activate();
            return task;
        }

        /// <summary>
        /// Tick所有任务
        /// </summary>
        public void Tick(float deltaTime)
        {
            List<AbilityTask> toRemove;

            lock (_lock)
            {
                foreach (var t in _tasks)
                {
                    t.Tick(deltaTime);
                }

                toRemove = _tasks.Where(t => t.IsCompleted).ToList();
                foreach (var t in toRemove)
                {
                    _tasks.Remove(t);
                }
            }
        }

        /// <summary>
        /// 取消所有任务
        /// </summary>
        public void CancelAll()
        {
            lock (_lock)
            {
                foreach (var t in _tasks.Where(t => !t.IsCompleted))
                {
                    t.Cancel();
                }
                _tasks.Clear();
            }
        }

        /// <summary>
        /// 当前任务数
        /// </summary>
        public int TaskCount
        {
            get
            {
                lock (_lock)
                {
                    return _tasks.Count;
                }
            }
        }
    }

    #endregion

    #region 能力事件系统

    /// <summary>
    /// 能力事件 用于事件触发的能力
    /// 比如被击中时触发的反击能力
    /// </summary>
    public class AbilityEvent
    {
        public string EventName { get; }
        public GameplayAbilityComponent? Source { get; set; }
        public GameplayAbilityComponent? Target { get; set; }
        public Dictionary<string, object?> Payload { get; } = new();

        public AbilityEvent(string eventName)
        {
            EventName = eventName ?? throw new ArgumentNullException(nameof(eventName));
        }

        public void SetPayload(string key, object? value)
        {
            Payload[key] = value;
        }

        public T? GetPayload<T>(string key)
        {
            if (Payload.TryGetValue(key, out var value) && value is T t)
                return t;
            return default;
        }
    }

    /// <summary>
    /// 能力事件分发器 管理事件监听
    /// </summary>
    public class AbilityEventDispatcher
    {
        private readonly Dictionary<string, List<Action<AbilityEvent>>> _listeners = new();
        private readonly object _lock = new();

        /// <summary>
        /// 注册监听
        /// </summary>
        public void Subscribe(string eventName, Action<AbilityEvent> callback)
        {
            if (string.IsNullOrEmpty(eventName) || callback == null) return;

            lock (_lock)
            {
                if (!_listeners.TryGetValue(eventName, out var list))
                {
                    list = new List<Action<AbilityEvent>>();
                    _listeners[eventName] = list;
                }
                list.Add(callback);
            }
        }

        /// <summary>
        /// 取消监听
        /// </summary>
        public void Unsubscribe(string eventName, Action<AbilityEvent> callback)
        {
            if (string.IsNullOrEmpty(eventName) || callback == null) return;

            lock (_lock)
            {
                if (_listeners.TryGetValue(eventName, out var list))
                {
                    list.Remove(callback);
                    if (list.Count == 0)
                        _listeners.Remove(eventName);
                }
            }
        }

        /// <summary>
        /// 派发事件
        /// </summary>
        public void Dispatch(AbilityEvent evt)
        {
            if (evt == null) return;

            List<Action<AbilityEvent>>? callbacks;
            lock (_lock)
            {
                if (!_listeners.TryGetValue(evt.EventName, out var list))
                    return;
                callbacks = new List<Action<AbilityEvent>>(list);
            }

            foreach (var cb in callbacks)
            {
                try
                {
                    cb(evt);
                }
                catch (Exception)
                {
                    // 吞掉异常 别让一个监听崩了整个分发
                }
            }
        }

        /// <summary>
        /// 清空所有监听
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _listeners.Clear();
            }
        }
    }

    #endregion

    #region 能力上下文 激活时的上下文信息

    /// <summary>
    /// 能力激活上下文 包含激活时的所有环境信息
    /// 比如目标位置 朝向 输入状态等
    /// </summary>
    public class AbilityActivationContext
    {
        // 激活源组件
        public GameplayAbilityComponent? Source { get; set; }

        // 目标组件列表
        public List<GameplayAbilityComponent> Targets { get; } = new();

        // 目标位置 世界坐标
        public System.Numerics.Vector3 TargetLocation { get; set; }

        // 目标方向 归一化
        public System.Numerics.Vector3 TargetDirection { get; set; }

        // 激活时按住的输入
        public AbilityInputId InputId { get; set; }

        // 激活时是否按住某个修饰键
        public bool ShiftHeld { get; set; }
        public bool CtrlHeld { get; set; }
        public bool AltHeld { get; set; }

        // 自定义参数 扩展用
        public Dictionary<string, object?> Parameters { get; } = new();

        /// <summary>
        /// 添加目标
        /// </summary>
        public void AddTarget(GameplayAbilityComponent target)
        {
            if (target != null && !Targets.Contains(target))
            {
                Targets.Add(target);
            }
        }

        /// <summary>
        /// 设置参数
        /// </summary>
        public void SetParameter(string key, object? value)
        {
            Parameters[key] = value;
        }

        /// <summary>
        /// 获取参数
        /// </summary>
        public T? GetParameter<T>(string key)
        {
            if (Parameters.TryGetValue(key, out var value) && value is T t)
                return t;
            return default;
        }
    }

    #endregion

    #region 能力激活器 封装激活流程

    /// <summary>
    /// 能力激活器 封装完整的激活流程
    /// 包括目标选择 资源消耗 效果应用
    /// </summary>
    public class AbilityActivator
    {
        private readonly GameplayAbilitySystem _system;

        public AbilityActivator(GameplayAbilitySystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
        }

        /// <summary>
        /// 完整激活流程 带上下文
        /// </summary>
        public AbilityActivationResult Activate(
            GameplayAbilityComponent source,
            int abilityId,
            AbilityActivationContext? context = null)
        {
            if (source == null)
                return AbilityActivationResult.InternalError;

            var spec = _system.Specs.GetById(abilityId);
            if (spec == null)
                return AbilityActivationResult.NotGranted;

            // 走组件的激活检查
            var result = source.TryActivateAbility(abilityId);
            if (result != AbilityActivationResult.Success)
                return result;

            // 激活成功后 应用立即效果
            var instance = source.GetGrantedAbility(abilityId);
            if (instance != null && spec.ApplyEffectIds.Count > 0 && context != null)
            {
                foreach (var target in context.Targets)
                {
                    foreach (var effectId in spec.ApplyEffectIds)
                    {
                        target.Effects?.ApplyEffect(effectId);
                    }
                }

                // 如果没有目标 也对自己应用
                if (context.Targets.Count == 0 && source.Effects != null)
                {
                    foreach (var effectId in spec.ApplyEffectIds)
                    {
                        source.Effects.ApplyEffect(effectId);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 按输入激活 走标准流程
        /// </summary>
        public List<AbilityActivationResult> ActivateByInput(
            GameplayAbilityComponent source,
            AbilityInputId inputId,
            AbilityTriggerType triggerType)
        {
            return source.TryActivateByInput(inputId, triggerType);
        }
    }

    #endregion

    #region 能力调试和分析

    /// <summary>
    /// 能力系统调试器 提供运行时分析数据
    /// 用于性能监控和bug排查
    /// </summary>
    public class AbilitySystemDebugger
    {
        private readonly GameplayAbilitySystem _system;

        // 激活统计 按能力ID
        private readonly Dictionary<int, int> _activationCounts = new();
        private readonly Dictionary<int, int> _failureCounts = new();

        // 总激活次数
        private long _totalActivations;
        private long _totalFailures;

        public AbilitySystemDebugger(GameplayAbilitySystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));

            _system.GlobalAbilityActivated += OnAbilityActivated;
        }

        private void OnAbilityActivated(GameplayAbilityComponent comp, AbilityInstance inst, AbilityActivationResult result)
        {
            if (result == AbilityActivationResult.Success)
            {
                _totalActivations++;
                if (!_activationCounts.ContainsKey(inst.Spec.AbilityId))
                    _activationCounts[inst.Spec.AbilityId] = 0;
                _activationCounts[inst.Spec.AbilityId]++;
            }
            else
            {
                _totalFailures++;
                if (!_failureCounts.ContainsKey(inst.Spec.AbilityId))
                    _failureCounts[inst.Spec.AbilityId] = 0;
                _failureCounts[inst.Spec.AbilityId]++;
            }
        }

        /// <summary>
        /// 获取调试快照
        /// </summary>
        public string GetSnapshot()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Ability System Debugger Snapshot ===");
            sb.AppendLine($"Total Components: {_system.GetAllComponents().Count}");
            sb.AppendLine($"Total Specs: {_system.Specs.Count}");
            sb.AppendLine($"Total Activations: {_totalActivations}");
            sb.AppendLine($"Total Failures: {_totalFailures}");
            sb.AppendLine();
            sb.AppendLine("--- Top Activated Abilities ---");

            var sorted = _activationCounts.OrderByDescending(kv => kv.Value).Take(10);
            foreach (var kv in sorted)
            {
                var spec = _system.Specs.GetById(kv.Key);
                var name = spec?.Name ?? kv.Key.ToString();
                sb.AppendLine($"  {name}: {kv.Value} activations");
            }

            sb.AppendLine();
            sb.AppendLine("--- Top Failure Abilities ---");
            var failSorted = _failureCounts.OrderByDescending(kv => kv.Value).Take(10);
            foreach (var kv in failSorted)
            {
                var spec = _system.Specs.GetById(kv.Key);
                var name = spec?.Name ?? kv.Key.ToString();
                sb.AppendLine($"  {name}: {kv.Value} failures");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 重置统计
        /// </summary>
        public void ResetStats()
        {
            _activationCounts.Clear();
            _failureCounts.Clear();
            _totalActivations = 0;
            _totalFailures = 0;
        }
    }

    #endregion

    #region 配置加载器 从JSON加载

    /// <summary>
    /// 能力配置加载器 从JSON格式加载能力规格
    /// 简化版 不依赖Newtonsoft.Json 用手写解析
    /// 实际项目里用System.Text.Json或Newtonsoft
    /// </summary>
    public class AbilityConfigLoader
    {
        private readonly GameplayAbilitySystem _system;

        public AbilityConfigLoader(GameplayAbilitySystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
        }

        /// <summary>
        /// 从配置列表加载
        /// 实际项目里从文件读取JSON然后反序列化
        /// 这里用简化接口
        /// </summary>
        public int LoadFromSpecs(IEnumerable<AbilitySpec> specs)
        {
            int count = 0;
            foreach (var spec in specs)
            {
                if (_system.Specs.Register(spec))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 创建一个默认的能力规格 方便测试
        /// </summary>
        public AbilitySpec CreateDefaultSpec(int id, string name)
        {
            return new AbilitySpec
            {
                AbilityId = id,
                Name = name,
                Description = "默认能力",
                NetSyncMode = AbilityNetSyncMode.ServerAuthority,
                TriggerType = AbilityTriggerType.OnInputPressed,
                InputId = AbilityInputId.Ability1,
                CooldownDuration = 1.0f,
                Duration = 0f,
                Priority = 0,
                CastTime = 0f,
                RecoveryTime = 0.3f,
            };
        }

        /// <summary>
        /// 批量创建测试用能力
        /// </summary>
        public List<AbilitySpec> CreateTestSpecs(int count)
        {
            var specs = new List<AbilitySpec>();
            for (int i = 1; i <= count; i++)
            {
                specs.Add(CreateDefaultSpec(i, $"TestAbility_{i}"));
            }
            return specs;
        }
    }

    #endregion

    #region 能力权限系统

    /// <summary>
    /// 能力权限管理 控制谁能用哪些能力
    /// 比如职业限制 等级限制
    /// </summary>
    public class AbilityPermissionManager
    {
        // 角色类型到允许的能力ID列表
        private readonly Dictionary<string, HashSet<int>> _classAbilities = new();

        // 等级限制 能力ID到最低等级
        private readonly Dictionary<int, int> _levelRequirements = new();

        private readonly object _lock = new();

        /// <summary>
        /// 设置职业可用能力
        /// </summary>
        public void SetClassAbilities(string className, IEnumerable<int> abilityIds)
        {
            if (string.IsNullOrEmpty(className)) return;

            lock (_lock)
            {
                _classAbilities[className] = new HashSet<int>(abilityIds);
            }
        }

        /// <summary>
        /// 设置能力等级要求
        /// </summary>
        public void SetLevelRequirement(int abilityId, int minLevel)
        {
            lock (_lock)
            {
                _levelRequirements[abilityId] = minLevel;
            }
        }

        /// <summary>
        /// 检查是否允许使用能力
        /// </summary>
        public bool CanUseAbility(string className, int abilityId, int currentLevel)
        {
            lock (_lock)
            {
                // 检查职业限制
                if (!string.IsNullOrEmpty(className) && _classAbilities.TryGetValue(className, out var classSet))
                {
                    if (!classSet.Contains(abilityId))
                        return false;
                }

                // 检查等级限制
                if (_levelRequirements.TryGetValue(abilityId, out var minLevel))
                {
                    if (currentLevel < minLevel)
                        return false;
                }

                return true;
            }
        }

        /// <summary>
        /// 获取职业可用能力列表
        /// </summary>
        public List<int> GetClassAbilities(string className)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(className))
                    return new List<int>();

                if (_classAbilities.TryGetValue(className, out var set))
                    return set.ToList();
                return new List<int>();
            }
        }
    }

    #endregion

    #region 能力组合连招系统

    /// <summary>
    /// 连招节点 连招中的一个步骤
    /// </summary>
    public class ComboNode
    {
        public int AbilityId { get; set; }
        public AbilityInputId InputId { get; set; }
        public float WindowStart { get; set; }   // 连招窗口开始时间 相对上一步结束
        public float WindowEnd { get; set; }     // 连招窗口结束时间
        public int NextNodeId { get; set; }      // 下一个节点ID 0表示结束
    }

    /// <summary>
    /// 连招定义 一系列能力按顺序触发
    /// </summary>
    public class ComboDefinition
    {
        public int ComboId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<ComboNode> Nodes { get; } = new();
    }

    /// <summary>
    /// 连招状态 跟踪当前进度
    /// </summary>
    public class ComboState
    {
        public int ComboId { get; set; }
        public int CurrentNodeIndex { get; set; }
        public float WindowStartTime { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// 连招系统 管理连招的检测和推进
    /// </summary>
    public class ComboSystem
    {
        private readonly Dictionary<int, ComboDefinition> _combos = new();
        private readonly Dictionary<long, ComboState> _componentStates = new();
        private readonly object _lock = new();

        /// <summary>
        /// 注册连招
        /// </summary>
        public void RegisterCombo(ComboDefinition combo)
        {
            if (combo == null) return;
            lock (_lock)
            {
                _combos[combo.ComboId] = combo;
            }
        }

        /// <summary>
        /// 尝试推进连招
        /// </summary>
        public bool TryAdvanceCombo(GameplayAbilityComponent component, AbilityInputId input, float currentTime)
        {
            lock (_lock)
            {
                // 检查是否已有进行中的连招
                if (_componentStates.TryGetValue(component.ComponentId, out var state) && state.IsActive)
                {
                    // 找到当前连招定义
                    if (_combos.TryGetValue(state.ComboId, out var combo))
                    {
                        if (state.CurrentNodeIndex < combo.Nodes.Count)
                        {
                            var currentNode = combo.Nodes[state.CurrentNodeIndex];

                            // 检查输入是否匹配
                            if (currentNode.InputId == input)
                            {
                                // 检查时间窗口
                                float elapsed = currentTime - state.WindowStartTime;
                                if (elapsed >= currentNode.WindowStart && elapsed <= currentNode.WindowEnd)
                                {
                                    // 推进到下一个节点
                                    state.CurrentNodeIndex++;

                                    if (state.CurrentNodeIndex >= combo.Nodes.Count)
                                    {
                                        // 连招完成
                                        state.IsActive = false;
                                    }
                                    else
                                    {
                                        state.WindowStartTime = currentTime;
                                    }
                                    return true;
                                }
                            }
                        }
                    }
                }

                // 尝试启动新连招
                foreach (var combo in _combos.Values)
                {
                    if (combo.Nodes.Count > 0 && combo.Nodes[0].InputId == input)
                    {
                        var newState = new ComboState
                        {
                            ComboId = combo.ComboId,
                            CurrentNodeIndex = 0,
                            WindowStartTime = currentTime,
                            IsActive = true,
                        };
                        _componentStates[component.ComponentId] = newState;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Tick更新 检查连招超时
        /// </summary>
        public void Tick(float currentTime)
        {
            lock (_lock)
            {
                var toExpire = new List<long>();

                foreach (var kv in _componentStates)
                {
                    if (!kv.Value.IsActive) continue;

                    if (_combos.TryGetValue(kv.Value.ComboId, out var combo))
                    {
                        if (kv.Value.CurrentNodeIndex < combo.Nodes.Count)
                        {
                            var node = combo.Nodes[kv.Value.CurrentNodeIndex];
                            float elapsed = currentTime - kv.Value.WindowStartTime;
                            if (elapsed > node.WindowEnd)
                            {
                                toExpire.Add(kv.Key);
                            }
                        }
                    }
                }

                foreach (var key in toExpire)
                {
                    _componentStates.Remove(key);
                }
            }
        }
    }

    #endregion

    #region 能力冷却组管理

    /// <summary>
    /// 冷却组管理 共享冷却的能力组
    /// 比如所有火系法术共享一个冷却
    /// </summary>
    public class CooldownGroupManager
    {
        private readonly Dictionary<string, float> _groupLastUseTime = new();
        private readonly Dictionary<string, float> _groupCooldownDuration = new();
        private readonly object _lock = new();

        /// <summary>
        /// 定义冷却组
        /// </summary>
        public void DefineGroup(string groupName, float cooldownDuration)
        {
            if (string.IsNullOrEmpty(groupName)) return;

            lock (_lock)
            {
                _groupCooldownDuration[groupName] = cooldownDuration;
            }
        }

        /// <summary>
        /// 触发冷却组
        /// </summary>
        public void TriggerCooldown(string groupName, float currentTime)
        {
            if (string.IsNullOrEmpty(groupName)) return;

            lock (_lock)
            {
                _groupLastUseTime[groupName] = currentTime;
            }
        }

        /// <summary>
        /// 检查冷却组是否在冷却中
        /// </summary>
        public bool IsOnCooldown(string groupName, float currentTime)
        {
            if (string.IsNullOrEmpty(groupName)) return false;

            lock (_lock)
            {
                if (!_groupLastUseTime.TryGetValue(groupName, out var lastTime))
                    return false;

                if (!_groupCooldownDuration.TryGetValue(groupName, out var duration))
                    return false;

                return (currentTime - lastTime) < duration;
            }
        }

        /// <summary>
        /// 获取冷却剩余
        /// </summary>
        public float GetRemainingCooldown(string groupName, float currentTime)
        {
            if (string.IsNullOrEmpty(groupName)) return 0f;

            lock (_lock)
            {
                if (!_groupLastUseTime.TryGetValue(groupName, out var lastTime))
                    return 0f;

                if (!_groupCooldownDuration.TryGetValue(groupName, out var duration))
                    return 0f;

                float remaining = duration - (currentTime - lastTime);
                return Math.Max(0f, remaining);
            }
        }

        /// <summary>
        /// 重置冷却组
        /// </summary>
        public void ResetGroup(string groupName)
        {
            lock (_lock)
            {
                _groupLastUseTime.Remove(groupName);
            }
        }

        /// <summary>
        /// 重置所有冷却组
        /// </summary>
        public void ResetAll()
        {
            lock (_lock)
            {
                _groupLastUseTime.Clear();
            }
        }
    }

    #endregion

    #region 能力预测和回滚 客户端预测

    /// <summary>
    /// 能力预测数据 客户端预测激活时记录
    /// 用于服务端确认或回滚
    /// </summary>
    public class AbilityPredictionData
    {
        public long PredictionId { get; set; }
        public long ComponentId { get; set; }
        public int AbilityId { get; set; }
        public float PredictionTime { get; set; }
        public AbilityActivationResult PredictedResult { get; set; }
        public Dictionary<string, object?> StateSnapshot { get; } = new();
    }

    /// <summary>
    /// 预测管理器 客户端预测和服务端确认
    /// </summary>
    public class AbilityPredictionManager
    {
        private readonly Dictionary<long, AbilityPredictionData> _pendingPredictions = new();
        private long _nextPredictionId = 1;
        private readonly object _lock = new();

        /// <summary>
        /// 创建预测
        /// </summary>
        public AbilityPredictionData CreatePrediction(long componentId, int abilityId, float currentTime)
        {
            var data = new AbilityPredictionData
            {
                PredictionId = Interlocked.Increment(ref _nextPredictionId),
                ComponentId = componentId,
                AbilityId = abilityId,
                PredictionTime = currentTime,
                PredictedResult = AbilityActivationResult.Success,
            };

            lock (_lock)
            {
                _pendingPredictions[data.PredictionId] = data;
            }

            return data;
        }

        /// <summary>
        /// 服务端确认预测
        /// </summary>
        public bool ConfirmPrediction(long predictionId, AbilityActivationResult serverResult)
        {
            lock (_lock)
            {
                if (!_pendingPredictions.TryGetValue(predictionId, out var data))
                    return false;

                if (serverResult != data.PredictedResult)
                {
                    // 预测失败 需要回滚
                    Rollback(data);
                }

                _pendingPredictions.Remove(predictionId);
                return true;
            }
        }

        /// <summary>
        /// 回滚预测
        /// </summary>
        private void Rollback(AbilityPredictionData data)
        {
            // 这里应该恢复状态 快照
            // 简化版只记录日志
            // 实际项目里要恢复属性 标签 等
        }

        /// <summary>
        /// 清理超时预测
        /// </summary>
        public void CleanupExpired(float currentTime, float timeout = 5f)
        {
            lock (_lock)
            {
                var expired = _pendingPredictions
                    .Where(kv => (currentTime - kv.Value.PredictionTime) > timeout)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var id in expired)
                {
                    _pendingPredictions.Remove(id);
                }
            }
        }

        /// <summary>
        /// 当前待确认预测数
        /// </summary>
        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _pendingPredictions.Count;
                }
            }
        }
    }

    #endregion

    #region 能力修饰器 运行时修改能力

    /// <summary>
    /// 能力修饰器接口 运行时修改能力行为
    /// 比如减少冷却 增加伤害 范围扩大
    /// </summary>
    public interface IAbilityModifier
    {
        string ModifierName { get; }
        void ModifySpec(AbilitySpec spec);
    }

    /// <summary>
    /// 冷却缩减修饰器
    /// </summary>
    public class CooldownReductionModifier : IAbilityModifier
    {
        public string ModifierName => "CooldownReduction";
        private readonly float _reductionPercent;

        public CooldownReductionModifier(float reductionPercent)
        {
            _reductionPercent = Math.Clamp(reductionPercent, 0f, 0.9f);
        }

        public void ModifySpec(AbilitySpec spec)
        {
            spec.CooldownDuration *= (1f - _reductionPercent);
        }
    }

    /// <summary>
    /// 范围扩大修饰器
    /// </summary>
    public class RangeExpansionModifier : IAbilityModifier
    {
        public string ModifierName => "RangeExpansion";
        private readonly float _expansionPercent;

        public RangeExpansionModifier(float expansionPercent)
        {
            _expansionPercent = expansionPercent;
        }

        public void ModifySpec(AbilitySpec spec)
        {
            spec.CastRadius *= (1f + _expansionPercent);
            spec.CastRange *= (1f + _expansionPercent);
        }
    }

    /// <summary>
    /// 伤害加成修饰器
    /// </summary>
    public class DamageBoostModifier : IAbilityModifier
    {
        public string ModifierName => "DamageBoost";
        private readonly float _boostPercent;

        public DamageBoostModifier(float boostPercent)
        {
            _boostPercent = boostPercent;
        }

        public void ModifySpec(AbilitySpec spec)
        {
            // 通过元数据传递伤害加成
            spec.Metadata["DamageBoostPercent"] = _boostPercent.ToString();
        }
    }

    /// <summary>
    /// 修饰器管理器
    /// </summary>
    public class AbilityModifierManager
    {
        private readonly List<IAbilityModifier> _globalModifiers = new();
        private readonly Dictionary<int, List<IAbilityModifier>> _perAbilityModifiers = new();
        private readonly object _lock = new();

        /// <summary>
        /// 添加全局修饰器 影响所有能力
        /// </summary>
        public void AddGlobalModifier(IAbilityModifier modifier)
        {
            if (modifier == null) return;
            lock (_lock)
            {
                _globalModifiers.Add(modifier);
            }
        }

        /// <summary>
        /// 添加针对特定能力的修饰器
        /// </summary>
        public void AddAbilityModifier(int abilityId, IAbilityModifier modifier)
        {
            if (modifier == null) return;
            lock (_lock)
            {
                if (!_perAbilityModifiers.TryGetValue(abilityId, out var list))
                {
                    list = new List<IAbilityModifier>();
                    _perAbilityModifiers[abilityId] = list;
                }
                list.Add(modifier);
            }
        }

        /// <summary>
        /// 应用所有修饰器到能力规格
        /// </summary>
        public void ApplyModifiers(AbilitySpec spec)
        {
            if (spec == null) return;

            lock (_lock)
            {
                foreach (var mod in _globalModifiers)
                {
                    mod.ModifySpec(spec);
                }

                if (_perAbilityModifiers.TryGetValue(spec.AbilityId, out var list))
                {
                    foreach (var mod in list)
                    {
                        mod.ModifySpec(spec);
                    }
                }
            }
        }

        /// <summary>
        /// 清空所有修饰器
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _globalModifiers.Clear();
                _perAbilityModifiers.Clear();
            }
        }
    }

    #endregion

    #region 能力UI数据 提供给前端

    /// <summary>
    /// 能力UI数据 提供给UI层显示
    /// </summary>
    public class AbilityUIData
    {
        public int AbilityId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public int Level { get; set; }
        public bool IsActive { get; set; }
        public float CooldownProgress { get; set; }  // 0-1 1表示冷却完成
        public float CooldownRemaining { get; set; }
        public bool IsGranted { get; set; }
        public bool CanActivate { get; set; }
        public AbilityInputId InputId { get; set; }
    }

    /// <summary>
    /// 能力UI数据收集器 从组件收集UI数据
    /// </summary>
    public static class AbilityUIDataCollector
    {
        /// <summary>
        /// 从能力组件收集所有能力的UI数据
        /// </summary>
        public static List<AbilityUIData> Collect(GameplayAbilityComponent component)
        {
            var result = new List<AbilityUIData>();

            if (component == null) return result;

            foreach (var inst in component.GetAllGrantedAbilities())
            {
                var uiData = new AbilityUIData
                {
                    AbilityId = inst.Spec.AbilityId,
                    Name = inst.Spec.Name,
                    Description = inst.Spec.Description,
                    IconPath = inst.Spec.IconPath,
                    Level = inst.Level,
                    IsActive = inst.IsActive,
                    CooldownProgress = inst.GetCooldownProgress(component.CurrentTime),
                    CooldownRemaining = inst.GetCooldownRemaining(component.CurrentTime),
                    IsGranted = true,
                    CanActivate = !inst.IsActive && !inst.IsOnCooldown(component.CurrentTime),
                    InputId = inst.Spec.InputId,
                };

                result.Add(uiData);
            }

            return result;
        }
    }

    #endregion

    #region 能力工厂 创建能力实例

    /// <summary>
    /// 能力实例工厂 根据类型名创建实例
    /// 支持反射创建自定义能力逻辑
    /// </summary>
    public class AbilityInstanceFactory
    {
        // 类型缓存 避免每次反射查找
        private readonly Dictionary<string, Type> _typeCache = new();
        private readonly object _lock = new();

        /// <summary>
        /// 注册类型
        /// </summary>
        public void RegisterType(string typeName, Type type)
        {
            if (string.IsNullOrEmpty(typeName) || type == null) return;

            lock (_lock)
            {
                _typeCache[typeName] = type;
            }
        }

        /// <summary>
        /// 创建能力实例
        /// </summary>
        public AbilityInstance? CreateInstance(AbilitySpec spec)
        {
            if (spec == null) return null;

            // 如果没有指定类型名 用基类
            if (string.IsNullOrEmpty(spec.InstanceTypeName))
            {
                return new AbilityInstance(spec);
            }

            lock (_lock)
            {
                if (_typeCache.TryGetValue(spec.InstanceTypeName, out var type))
                {
                    try
                    {
                        var constructor = type.GetConstructor(new[] { typeof(AbilitySpec) });
                        if (constructor != null)
                        {
                            return constructor.Invoke(new object[] { spec }) as AbilityInstance;
                        }
                    }
                    catch
                    {
                        // 反射失败 回退到基类
                    }
                }
            }

            return new AbilityInstance(spec);
        }

        /// <summary>
        /// 扫描程序集自动注册所有能力子类
        /// </summary>
        public int AutoRegisterFromAssembly(System.Reflection.Assembly assembly)
        {
            if (assembly == null) return 0;

            int count = 0;
            var baseType = typeof(AbilityInstance);

            try
            {
                var types = assembly.GetTypes()
                    .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    RegisterType(type.Name, type);
                    count++;
                }
            }
            catch
            {
                // 扫描失败 忽略
            }

            return count;
        }
    }

    #endregion

    #region 系统集成测试辅助

    /// <summary>
    /// 系统集成辅助方法 用于测试和初始化
    /// </summary>
    public static class AbilitySystemBootstrap
    {
        /// <summary>
        /// 初始化能力系统 注册默认能力
        /// </summary>
        public static GameplayAbilitySystem Initialize()
        {
            var system = GameplayAbilitySystem.Instance;

            // 注册一些默认能力
            var defaultSpecs = new[]
            {
                new AbilitySpec
                {
                    AbilityId = 1001,
                    Name = "BasicAttack",
                    Description = "基础攻击",
                    InputId = AbilityInputId.Primary,
                    CooldownDuration = 0.5f,
                    CastTime = 0f,
                    RecoveryTime = 0.2f,
                    Priority = 10,
                },
                new AbilitySpec
                {
                    AbilityId = 1002,
                    Name = "HeavyAttack",
                    Description = "重击 蓄力攻击",
                    InputId = AbilityInputId.Secondary,
                    CooldownDuration = 2.0f,
                    CastTime = 0.8f,
                    RecoveryTime = 0.5f,
                    Priority = 20,
                    CancelOnMove = true,
                },
                new AbilitySpec
                {
                    AbilityId = 1003,
                    Name = "Dash",
                    Description = "闪避冲刺",
                    InputId = AbilityInputId.Dash,
                    CooldownDuration = 3.0f,
                    Duration = 0.3f,
                    Priority = 50,
                },
                new AbilitySpec
                {
                    AbilityId = 1004,
                    Name = "Heal",
                    Description = "治疗术 恢复生命值",
                    InputId = AbilityInputId.Ability1,
                    CooldownDuration = 15.0f,
                    Costs = new Dictionary<string, float> { { "Mana", 30f } },
                    CastTime = 1.0f,
                    RecoveryTime = 0.5f,
                    CancelOnMove = true,
                    CancelOnDamage = true,
                },
                new AbilitySpec
                {
                    AbilityId = 1005,
                    Name = "Fireball",
                    Description = "火球术 范围伤害",
                    InputId = AbilityInputId.Ability2,
                    CooldownDuration = 5.0f,
                    Costs = new Dictionary<string, float> { { "Mana", 50f } },
                    CastTime = 0.5f,
                    CastRange = 20f,
                    CastRadius = 3f,
                    RecoveryTime = 0.3f,
                },
                new AbilitySpec
                {
                    AbilityId = 1006,
                    Name = "Ultimate_StormBlade",
                    Description = "大招 风暴之刃",
                    InputId = AbilityInputId.Ultimate,
                    CooldownDuration = 120.0f,
                    Costs = new Dictionary<string, float> { { "Mana", 100f } },
                    Duration = 5f,
                    Priority = 100,
                    CastTime = 0.5f,
                },
            };

            system.LoadSpecsFromConfig(defaultSpecs);

            return system;
        }

        /// <summary>
        /// 创建一个带默认属性的测试组件
        /// </summary>
        public static GameplayAbilityComponent CreateTestComponent()
        {
            var component = new GameplayAbilityComponent();
            var system = GameplayAbilitySystem.Instance;

            // 设置属性
            component.Attributes = new GameplayAttributeSet();
            component.Attributes.SetAttribute("Health", 100f);
            component.Attributes.SetAttribute("Mana", 200f);
            component.Attributes.SetAttribute("Stamina", 100f);

            // 授予所有默认能力
            foreach (var spec in system.Specs.GetAll())
            {
                component.GrantAbility(spec);
            }

            system.RegisterComponent(component);
            return component;
        }

        /// <summary>
        /// 运行简单测试
        /// </summary>
        public static string RunSmokeTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== GAS V2 Smoke Test ===");

            var system = Initialize();
            sb.AppendLine($"System initialized with {system.Specs.Count} specs");

            var component = CreateTestComponent();
            sb.AppendLine($"Component created with ID {component.ComponentId}");

            // 尝试激活基础攻击
            var result = component.TryActivateAbility(1001);
            sb.AppendLine($"BasicAttack activation: {result}");

            // Tick几次
            for (int i = 0; i < 10; i++)
            {
                system.Tick(0.1f);
            }

            // 检查冷却
            var cooldown = component.GetCooldownRemaining(1001);
            sb.AppendLine($"BasicAttack cooldown remaining: {cooldown:F2}s");

            // 尝试激活治疗
            result = component.TryActivateAbility(1004);
            sb.AppendLine($"Heal activation: {result}");

            // 尝试激活火球
            result = component.TryActivateAbility(1005);
            sb.AppendLine($"Fireball activation: {result}");

            var activeAbilities = component.GetActiveAbilities();
            sb.AppendLine($"Active abilities: {activeAbilities.Count}");

            // 清理
            system.Shutdown();

            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

    #region 能力录制和回放

    /// <summary>
    /// 能力激活记录 用于回放
    /// </summary>
    public class AbilityActivationRecord
    {
        public float Timestamp { get; set; }
        public long ComponentId { get; set; }
        public int AbilityId { get; set; }
        public AbilityActivationResult Result { get; set; }
    }

    /// <summary>
    /// 能力录制器 记录所有激活事件
    /// </summary>
    public class AbilityRecorder
    {
        private readonly List<AbilityActivationRecord> _records = new();
        private readonly object _lock = new();
        private bool _recording;

        /// <summary>
        /// 开始录制
        /// </summary>
        public void StartRecording()
        {
            lock (_lock)
            {
                _records.Clear();
                _recording = true;
            }
        }

        /// <summary>
        /// 停止录制
        /// </summary>
        public void StopRecording()
        {
            lock (_lock)
            {
                _recording = false;
            }
        }

        /// <summary>
        /// 记录一次激活
        /// </summary>
        public void Record(AbilityActivationRecord record)
        {
            if (!_recording) return;

            lock (_lock)
            {
                _records.Add(record);
            }
        }

        /// <summary>
        /// 获取所有记录
        /// </summary>
        public List<AbilityActivationRecord> GetRecords()
        {
            lock (_lock)
            {
                return new List<AbilityActivationRecord>(_records);
            }
        }

        /// <summary>
        /// 记录数量
        /// </summary>
        public int RecordCount
        {
            get
            {
                lock (_lock)
                {
                    return _records.Count;
                }
            }
        }

        /// <summary>
        /// 绑定到能力系统 自动录制
        /// </summary>
        public void BindToSystem(GameplayAbilitySystem system)
        {
            system.GlobalAbilityActivated += (comp, inst, result) =>
            {
                Record(new AbilityActivationRecord
                {
                    Timestamp = system.GlobalTime,
                    ComponentId = comp.ComponentId,
                    AbilityId = inst.Spec.AbilityId,
                    Result = result,
                });
            };
        }
    }

    #endregion

    #region 能力数据序列化

    /// <summary>
    /// 能力系统序列化器 用于存档
    /// </summary>
    public class AbilitySystemSerializer
    {
        /// <summary>
        /// 序列化组件状态
        /// </summary>
        public string SerializeComponent(GameplayAbilityComponent component)
        {
            if (component == null) return "{}";

            var sb = new System.Text.StringBuilder();
            sb.Append("{");

            sb.Append($"\"componentId\":{component.ComponentId},");
            sb.Append($"\"currentTime\":{component.CurrentTime:F3},");

            // 已授予能力
            sb.Append("\"grantedAbilities\":[");
            var granted = component.GetAllGrantedAbilities();
            for (int i = 0; i < granted.Count; i++)
            {
                var inst = granted[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"abilityId\":{inst.Spec.AbilityId},");
                sb.Append($"\"level\":{inst.Level},");
                sb.Append($"\"lastActivationTime\":{inst.LastActivationTime:F3}");
                sb.Append("}");
            }
            sb.Append("],");

            // 标签
            sb.Append("\"tags\":[");
            var tags = component.Tags.ToList();
            for (int i = 0; i < tags.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{tags[i]}\"");
            }
            sb.Append("]");

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// 序列化能力规格到JSON
        /// </summary>
        public string SerializeSpec(AbilitySpec spec)
        {
            if (spec == null) return "{}";

            var sb = new System.Text.StringBuilder();
            sb.Append("{");
            sb.Append($"\"abilityId\":{spec.AbilityId},");
            sb.Append($"\"name\":\"{EscapeJson(spec.Name)}\",");
            sb.Append($"\"description\":\"{EscapeJson(spec.Description)}\",");
            sb.Append($"\"netSyncMode\":{(int)spec.NetSyncMode},");
            sb.Append($"\"triggerType\":{(int)spec.TriggerType},");
            sb.Append($"\"inputId\":{(int)spec.InputId},");
            sb.Append($"\"cooldownDuration\":{spec.CooldownDuration:F3},");
            sb.Append($"\"duration\":{spec.Duration:F3},");
            sb.Append($"\"priority\":{spec.Priority},");
            sb.Append($"\"castTime\":{spec.CastTime:F3},");
            sb.Append($"\"recoveryTime\":{spec.RecoveryTime:F3},");
            sb.Append($"\"castRange\":{spec.CastRange:F3},");
            sb.Append($"\"castRadius\":{spec.CastRadius:F3},");

            // 消耗
            sb.Append("\"costs\":{");
            int idx = 0;
            foreach (var kv in spec.Costs)
            {
                if (idx > 0) sb.Append(",");
                sb.Append($"\"{kv.Key}\":{kv.Value}");
                idx++;
            }
            sb.Append("},");

            // 需要的标签
            sb.Append("\"activationRequiredTags\":[");
            for (int i = 0; i < spec.ActivationRequiredTags.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{spec.ActivationRequiredTags[i]}\"");
            }
            sb.Append("],");

            // 禁止的标签
            sb.Append("\"activationBlockedTags\":[");
            for (int i = 0; i < spec.ActivationBlockedTags.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{spec.ActivationBlockedTags[i]}\"");
            }
            sb.Append("],");

            // 自有标签
            sb.Append("\"activationOwnedTags\":[");
            for (int i = 0; i < spec.ActivationOwnedTags.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{spec.ActivationOwnedTags[i]}\"");
            }
            sb.Append("]");

            sb.Append("}");
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }

    #endregion

} // namespace YamaEngine.Gameplay
