// YamaEngine - yama游戏引擎
// QuestSystem.cs - 任务系统
// 任务系统管理所有主线支线日常任务
// 包括任务接取 进度跟踪 完成提交 奖励发放
// 任务条件各种各样 杀怪收集护送到达位置

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YamaEngine.Gameplay
{
    #region 任务枚举

    /// <summary>
    /// 任务类型
    /// </summary>
    public enum QuestType : byte
    {
        // 主线任务
        MainQuest = 0,
        // 支线任务
        SideQuest = 1,
        // 日常任务
        DailyQuest = 2,
        // 周常任务
        WeeklyQuest = 3,
        // 事件任务 限时
        EventQuest = 4,
        // 公会任务
        GuildQuest = 5,
        // 成就
        Achievement = 6,
    }

    /// <summary>
    /// 任务状态
    /// </summary>
    public enum QuestState : byte
    {
        // 未接取 不可见
        Inactive = 0,
        // 可接取 可见但没接
        Available = 1,
        // 已接取 进行中
        Active = 2,
        // 已完成目标 待提交
        Completed = 3,
        // 已提交 已领奖
        TurnedIn = 4,
        // 已失败
        Failed = 5,
        // 已放弃
        Abandoned = 6,
        // 已锁定 需要前置任务
        Locked = 7,
    }

    /// <summary>
    /// 任务目标类型
    /// </summary>
    public enum QuestObjectiveType : byte
    {
        // 击杀怪物
        Kill = 0,
        // 收集物品
        Collect = 1,
        // 到达位置
        ReachLocation = 2,
        // 与NPC对话
        TalkToNPC = 3,
        // 护送NPC
        Escort = 4,
        // 使用物品
        UseItem = 5,
        // 完成副本
        CompleteDungeon = 6,
        // 胜利PVP
        WinPVP = 7,
        // 自定义
        Custom = 255,
    }

    /// <summary>
    /// 任务操作结果
    /// </summary>
    public enum QuestOperationResult : byte
    {
        Success = 0,
        QuestNotFound = 1,
        AlreadyActive = 2,
        AlreadyCompleted = 3,
        NotActive = 4,
        PrerequisitesNotMet = 5,
        LevelTooLow = 6,
        ClassMismatch = 7,
        ObjectiveNotComplete = 8,
        AlreadyTurnedIn = 9,
        InternalError = 255,
    }

    #endregion

    #region 任务目标

    /// <summary>
    /// 任务目标定义
    /// </summary>
    public class QuestObjective
    {
        // 目标ID
        public int ObjectiveId { get; set; }

        // 目标描述
        public string Description { get; set; } = string.Empty;

        // 目标类型
        public QuestObjectiveType Type { get; set; } = QuestObjectiveType.Kill;

        // 目标ID 怪物ID 物品ID NPC ID等
        public int TargetId { get; set; }

        // 目标名字 显示用
        public string TargetName { get; set; } = string.Empty;

        // 需要数量
        public int RequiredCount { get; set; } = 1;

        // 目标位置 到达类目标用
        public System.Numerics.Vector3? TargetLocation { get; set; }

        // 到达半径
        public float ReachRadius { get; set; } = 5f;

        // 是否是可选目标
        public bool IsOptional { get; set; } = false;

        // 完成后给的奖励ID
        public List<int> BonusRewardIds { get; set; } = new();
    }

    /// <summary>
    /// 任务目标进度 运行时
    /// </summary>
    public class QuestObjectiveProgress
    {
        // 引用目标定义
        public QuestObjective Objective { get; }

        // 当前进度
        public int CurrentCount { get; set; }

        // 是否完成
        public bool IsCompleted { get; set; }

        public QuestObjectiveProgress(QuestObjective objective)
        {
            Objective = objective ?? throw new ArgumentNullException(nameof(objective));
        }

        /// <summary>
        /// 进度百分比
        /// </summary>
        public float Progress
        {
            get
            {
                if (Objective.RequiredCount <= 0) return 1f;
                return Math.Min(1f, (float)CurrentCount / Objective.RequiredCount);
            }
        }

        /// <summary>
        /// 增加进度
        /// </summary>
        public void AddProgress(int count)
        {
            if (IsCompleted) return;
            CurrentCount = Math.Min(Objective.RequiredCount, CurrentCount + count);
            if (CurrentCount >= Objective.RequiredCount)
            {
                IsCompleted = true;
            }
        }

        /// <summary>
        /// 设置完成
        /// </summary>
        public void SetCompleted()
        {
            CurrentCount = Objective.RequiredCount;
            IsCompleted = true;
        }

        /// <summary>
        /// 重置
        /// </summary>
        public void Reset()
        {
            CurrentCount = 0;
            IsCompleted = false;
        }

        public override string ToString()
        {
            return $"{Objective.Description}: {CurrentCount}/{Objective.RequiredCount}";
        }
    }

    #endregion

    #region 任务奖励

    /// <summary>
    /// 任务奖励类型
    /// </summary>
    public enum QuestRewardType : byte
    {
        // 经验值
        Experience = 0,
        // 金币
        Gold = 1,
        // 物品
        Item = 2,
        // 属性点
        AttributePoint = 3,
        // 技能点
        SkillPoint = 4,
        // 解锁能力
        Ability = 5,
        // 解锁区域
        Area = 6,
        // 声望
        Reputation = 7,
        // 成就
        Achievement = 8,
        // 自定义
        Custom = 255,
    }

    /// <summary>
    /// 任务奖励
    /// </summary>
    public class QuestReward
    {
        public int RewardId { get; set; }
        public QuestRewardType Type { get; set; }
        public int TargetId { get; set; }
        public int Amount { get; set; } = 1;
        public string Description { get; set; } = string.Empty;
        public bool IsOptional { get; set; } = false; // 可选奖励
    }

    #endregion

    #region 任务定义

    /// <summary>
    /// 任务定义 静态描述
    /// </summary>
    public class QuestDefinition
    {
        // 任务唯一ID
        public int QuestId { get; set; }

        // 任务名字
        public string Name { get; set; } = string.Empty;

        // 任务描述
        public string Description { get; set; } = string.Empty;

        // 任务类型
        public QuestType Type { get; set; } = QuestType.SideQuest;

        // 接取任务的最小等级
        public int MinLevel { get; set; } = 1;

        // 最大等级 超过不能接
        public int MaxLevel { get; set; } = 999;

        // 职业限制
        public List<string> RequiredClasses { get; set; } = new();

        // 前置任务ID 必须完成才能接
        public List<int> PrerequisiteQuestIds { get; set; } = new();

        // 前置任务必须全部完成
        public bool RequireAllPrerequisites { get; set; } = true;

        // 排斥任务 有这些任务就不能接
        public List<int> ExclusiveQuestIds { get; set; } = new();

        // 任务目标列表
        public List<QuestObjective> Objectives { get; set; } = new();

        // 基础奖励
        public List<QuestReward> BaseRewards { get; set; } = new();

        // 可选奖励 玩家选一个
        public List<QuestReward> OptionalRewards { get; set; } = new();

        // 接取NPC ID
        public int AcceptNPCId { get; set; } = 0;

        // 提交NPC ID 0表示接取NPC
        public int TurnInNPCId { get; set; } = 0;

        // 是否可放弃
        public bool IsAbandonable { get; set; } = true;

        // 是否可重复
        public bool IsRepeatable { get; set; } = false;

        // 重复冷却时间 小时
        public float RepeatCooldownHours { get; set; } = 0f;

        // 时间限制 秒 0表示无限制
        public float TimeLimit { get; set; } = 0f;

        // 自动提交 不需要找NPC
        public bool AutoComplete { get; set; } = false;

        // 自动接取
        public bool AutoAccept { get; set; } = false;

        // 共享给队友
        public bool IsShareable { get; set; } = true;

        // 后续任务ID
        public List<int> NextQuestIds { get; set; } = new();

        // 奖励经验
        public int RewardExperience { get; set; } = 0;

        // 奖励金币
        public int RewardGold { get; set; } = 0;

        // 元数据
        public Dictionary<string, string> Metadata { get; set; } = new();

        public override string ToString()
        {
            return $"[QuestDef {QuestId}:{Name} {Type}]";
        }
    }

    /// <summary>
    /// 任务定义仓库
    /// </summary>
    public class QuestDefinitionRepository
    {
        private readonly Dictionary<int, QuestDefinition> _quests = new();
        private readonly object _lock = new();

        public bool Register(QuestDefinition quest)
        {
            if (quest == null || quest.QuestId <= 0) return false;
            lock (_lock)
            {
                _quests[quest.QuestId] = quest;
                return true;
            }
        }

        public QuestDefinition? GetById(int questId)
        {
            lock (_lock)
            {
                _quests.TryGetValue(questId, out var q);
                return q;
            }
        }

        public List<QuestDefinition> GetByType(QuestType type)
        {
            lock (_lock)
            {
                return _quests.Values.Where(q => q.Type == type).ToList();
            }
        }

        public List<QuestDefinition> GetByNPC(int npcId)
        {
            lock (_lock)
            {
                return _quests.Values.Where(q => q.AcceptNPCId == npcId || q.TurnInNPCId == npcId).ToList();
            }
        }

        public List<QuestDefinition> GetAll()
        {
            lock (_lock)
            {
                return _quests.Values.ToList();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _quests.Clear();
            }
        }

        public int Count
        {
            get { lock (_lock) { return _quests.Count; } }
        }
    }

    #endregion

    #region 任务实例 运行时

    /// <summary>
    /// 任务实例 玩家接取的任务
    /// </summary>
    public class QuestInstance
    {
        // 唯一实例ID
        public long InstanceId { get; internal set; }

        // 引用定义
        public QuestDefinition Definition { get; }

        // 当前状态
        public QuestState State { get; internal set; } = QuestState.Inactive;

        // 目标进度列表
        private readonly List<QuestObjectiveProgress> _objectiveProgress = new();

        // 接取时间
        public DateTime AcceptTime { get; internal set; }

        // 完成时间
        public DateTime? CompleteTime { get; internal set; }

        // 提交时间
        public DateTime? TurnInTime { get; internal set; }

        // 选择的可选奖励ID
        public int? SelectedOptionalRewardId { get; set; }

        // 所属玩家ID
        public long PlayerId { get; set; }

        // 剩余时间 限时任务用
        public float RemainingTime { get; set; }

        // 实例ID自增
        private static long _nextId = 1;

        public QuestInstance(QuestDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            InstanceId = System.Threading.Interlocked.Increment(ref _nextId);

            // 初始化目标进度
            foreach (var obj in definition.Objectives)
            {
                _objectiveProgress.Add(new QuestObjectiveProgress(obj));
            }

            if (definition.TimeLimit > 0)
            {
                RemainingTime = definition.TimeLimit;
            }
        }

        /// <summary>
        /// 获取所有目标进度
        /// </summary>
        public List<QuestObjectiveProgress> GetObjectiveProgress()
        {
            return new List<QuestObjectiveProgress>(_objectiveProgress);
        }

        /// <summary>
        /// 获取指定目标的进度
        /// </summary>
        public QuestObjectiveProgress? GetObjectiveProgress(int objectiveId)
        {
            return _objectiveProgress.FirstOrDefault(p => p.Objective.ObjectiveId == objectiveId);
        }

        /// <summary>
        /// 增加目标进度
        /// </summary>
        public bool AddObjectiveProgress(QuestObjectiveType type, int targetId, int count)
        {
            bool changed = false;

            foreach (var progress in _objectiveProgress)
            {
                if (progress.IsCompleted) continue;
                if (progress.Objective.Type != type) continue;
                if (progress.Objective.TargetId != targetId) continue;

                progress.AddProgress(count);
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// 设置目标完成
        /// </summary>
        public bool SetObjectiveCompleted(QuestObjectiveType type, int targetId)
        {
            bool changed = false;

            foreach (var progress in _objectiveProgress)
            {
                if (progress.IsCompleted) continue;
                if (progress.Objective.Type != type) continue;
                if (progress.Objective.TargetId != targetId) continue;

                progress.SetCompleted();
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// 检查所有必需目标是否完成
        /// </summary>
        public bool AreAllObjectivesCompleted()
        {
            foreach (var progress in _objectiveProgress)
            {
                if (!progress.Objective.IsOptional && !progress.IsCompleted)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 完成进度百分比
        /// </summary>
        public float GetOverallProgress()
        {
            var required = _objectiveProgress.Where(p => !p.Objective.IsOptional).ToList();
            if (required.Count == 0) return 1f;

            float sum = 0;
            foreach (var p in required)
            {
                sum += p.Progress;
            }

            return sum / required.Count;
        }

        /// <summary>
        /// 获取完成的可选目标数
        /// </summary>
        public int GetCompletedOptionalCount()
        {
            return _objectiveProgress.Count(p => p.Objective.IsOptional && p.IsCompleted);
        }

        /// <summary>
        /// Tick更新 限时任务用
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (State != QuestState.Active) return;
            if (Definition.TimeLimit <= 0) return;

            RemainingTime -= deltaTime;
            if (RemainingTime <= 0f)
            {
                RemainingTime = 0f;
                State = QuestState.Failed;
            }
        }

        /// <summary>
        /// 是否过期
        /// </summary>
        public bool IsExpired => State == QuestState.Failed;

        /// <summary>
        /// 获取所有奖励 包括基础和选择的可选
        /// </summary>
        public List<QuestReward> GetRewards()
        {
            var rewards = new List<QuestReward>(Definition.BaseRewards);

            if (SelectedOptionalRewardId.HasValue)
            {
                var optional = Definition.OptionalRewards.FirstOrDefault(r => r.RewardId == SelectedOptionalRewardId.Value);
                if (optional != null)
                    rewards.Add(optional);
            }

            return rewards;
        }

        public override string ToString()
        {
            return $"[Quest {Definition.Name} {State}]";
        }
    }

    #endregion

    #region 玩家任务日志

    /// <summary>
    /// 玩家任务日志 管理一个玩家的所有任务
    /// </summary>
    public class PlayerQuestLog
    {
        // 所属玩家ID
        public long PlayerId { get; set; }

        // 玩家等级
        public int PlayerLevel { get; set; } = 1;

        // 玩家职业
        public string PlayerClass { get; set; } = string.Empty;

        // 已完成任务ID集合
        private readonly HashSet<int> _completedQuestIds = new();

        // 活跃任务实例 按任务ID索引
        private readonly Dictionary<int, QuestInstance> _activeQuests = new();

        // 锁
        private readonly object _lock = new();

        // 事件
        public event Action<PlayerQuestLog, QuestInstance>? QuestAccepted;
        public event Action<PlayerQuestLog, QuestInstance>? QuestCompleted;
        public event Action<PlayerQuestLog, QuestInstance>? QuestTurnedIn;
        public event Action<PlayerQuestLog, QuestInstance>? QuestAbandoned;
        public event Action<PlayerQuestLog, QuestInstance>? QuestFailed;
        public event Action<PlayerQuestLog, QuestInstance, QuestObjectiveType, int, int>? ObjectiveProgressChanged;

        public PlayerQuestLog(long playerId)
        {
            PlayerId = playerId;
        }

        /// <summary>
        /// 活跃任务数量
        /// </summary>
        public int ActiveQuestCount
        {
            get
            {
                lock (_lock)
                {
                    return _activeQuests.Count;
                }
            }
        }

        /// <summary>
        /// 检查是否可以接取任务
        /// </summary>
        public QuestOperationResult CanAcceptQuest(QuestDefinition quest)
        {
            if (quest == null) return QuestOperationResult.QuestNotFound;

            // 等级检查
            if (PlayerLevel < quest.MinLevel || PlayerLevel > quest.MaxLevel)
                return QuestOperationResult.LevelTooLow;

            // 职业检查
            if (quest.RequiredClasses.Count > 0 && !quest.RequiredClasses.Contains(PlayerClass))
                return QuestOperationResult.ClassMismatch;

            // 已完成检查
            lock (_lock)
            {
                if (_completedQuestIds.Contains(quest.QuestId))
                {
                    if (!quest.IsRepeatable)
                        return QuestOperationResult.AlreadyTurnedIn;
                }

                // 已活跃检查
                if (_activeQuests.ContainsKey(quest.QuestId))
                    return QuestOperationResult.AlreadyActive;

                // 前置任务检查
                if (quest.PrerequisiteQuestIds.Count > 0)
                {
                    if (quest.RequireAllPrerequisites)
                    {
                        foreach (var preId in quest.PrerequisiteQuestIds)
                        {
                            if (!_completedQuestIds.Contains(preId))
                                return QuestOperationResult.PrerequisitesNotMet;
                        }
                    }
                    else
                    {
                        bool anyCompleted = false;
                        foreach (var preId in quest.PrerequisiteQuestIds)
                        {
                            if (_completedQuestIds.Contains(preId))
                            {
                                anyCompleted = true;
                                break;
                            }
                        }
                        if (!anyCompleted)
                            return QuestOperationResult.PrerequisitesNotMet;
                    }
                }

                // 排斥任务检查
                foreach (var exId in quest.ExclusiveQuestIds)
                {
                    if (_activeQuests.ContainsKey(exId) || _completedQuestIds.Contains(exId))
                        return QuestOperationResult.PrerequisitesNotMet;
                }
            }

            return QuestOperationResult.Success;
        }

        /// <summary>
        /// 接取任务
        /// </summary>
        public QuestOperationResult AcceptQuest(QuestDefinition quest)
        {
            var result = CanAcceptQuest(quest);
            if (result != QuestOperationResult.Success) return result;

            var instance = new QuestInstance(quest)
            {
                State = QuestState.Active,
                AcceptTime = DateTime.UtcNow,
                PlayerId = PlayerId,
            };

            lock (_lock)
            {
                _activeQuests[quest.QuestId] = instance;
            }

            QuestAccepted?.Invoke(this, instance);
            return QuestOperationResult.Success;
        }

        /// <summary>
        /// 放弃任务
        /// </summary>
        public QuestOperationResult AbandonQuest(int questId)
        {
            lock (_lock)
            {
                if (!_activeQuests.TryGetValue(questId, out var instance))
                    return QuestOperationResult.NotActive;

                if (!instance.Definition.IsAbandonable)
                    return QuestOperationResult.InternalError;

                _activeQuests.Remove(questId);
                instance.State = QuestState.Abandoned;
                QuestAbandoned?.Invoke(this, instance);
                return QuestOperationResult.Success;
            }
        }

        /// <summary>
        /// 更新任务进度
        /// </summary>
        public bool UpdateObjectiveProgress(QuestObjectiveType type, int targetId, int count)
        {
            bool anyChanged = false;

            List<QuestInstance> snapshot;
            lock (_lock)
            {
                snapshot = _activeQuests.Values.Where(q => q.State == QuestState.Active).ToList();
            }

            foreach (var quest in snapshot)
            {
                bool changed = quest.AddObjectiveProgress(type, targetId, count);
                if (changed)
                {
                    anyChanged = true;
                    ObjectiveProgressChanged?.Invoke(this, quest, type, targetId, count);

                    // 检查是否所有目标完成
                    if (quest.AreAllObjectivesCompleted())
                    {
                        quest.State = QuestState.Completed;
                        quest.CompleteTime = DateTime.UtcNow;
                        QuestCompleted?.Invoke(this, quest);

                        // 自动提交
                        if (quest.Definition.AutoComplete)
                        {
                            TurnInQuest(quest.Definition.QuestId);
                        }
                    }
                }
            }

            return anyChanged;
        }

        /// <summary>
        /// 设置目标完成
        /// </summary>
        public bool SetObjectiveCompleted(QuestObjectiveType type, int targetId)
        {
            bool anyChanged = false;

            List<QuestInstance> snapshot;
            lock (_lock)
            {
                snapshot = _activeQuests.Values.Where(q => q.State == QuestState.Active).ToList();
            }

            foreach (var quest in snapshot)
            {
                bool changed = quest.SetObjectiveCompleted(type, targetId);
                if (changed)
                {
                    anyChanged = true;
                    ObjectiveProgressChanged?.Invoke(this, quest, type, targetId, 1);

                    if (quest.AreAllObjectivesCompleted())
                    {
                        quest.State = QuestState.Completed;
                        quest.CompleteTime = DateTime.UtcNow;
                        QuestCompleted?.Invoke(this, quest);

                        if (quest.Definition.AutoComplete)
                        {
                            TurnInQuest(quest.Definition.QuestId);
                        }
                    }
                }
            }

            return anyChanged;
        }

        /// <summary>
        /// 提交任务 领取奖励
        /// </summary>
        public QuestOperationResult TurnInQuest(int questId, int? selectedOptionalRewardId = null)
        {
            lock (_lock)
            {
                if (!_activeQuests.TryGetValue(questId, out var instance))
                    return QuestOperationResult.NotActive;

                if (instance.State != QuestState.Completed)
                    return QuestOperationResult.ObjectiveNotComplete;

                instance.SelectedOptionalRewardId = selectedOptionalRewardId;
                instance.State = QuestState.TurnedIn;
                instance.TurnInTime = DateTime.UtcNow;

                _activeQuests.Remove(questId);
                _completedQuestIds.Add(questId);

                QuestTurnedIn?.Invoke(this, instance);
                return QuestOperationResult.Success;
            }
        }

        /// <summary>
        /// 获取活跃任务
        /// </summary>
        public QuestInstance? GetActiveQuest(int questId)
        {
            lock (_lock)
            {
                _activeQuests.TryGetValue(questId, out var q);
                return q;
            }
        }

        /// <summary>
        /// 获取所有活跃任务
        /// </summary>
        public List<QuestInstance> GetAllActiveQuests()
        {
            lock (_lock)
            {
                return _activeQuests.Values.ToList();
            }
        }

        /// <summary>
        /// 获取按状态过滤的任务
        /// </summary>
        public List<QuestInstance> GetQuestsByState(QuestState state)
        {
            lock (_lock)
            {
                return _activeQuests.Values.Where(q => q.State == state).ToList();
            }
        }

        /// <summary>
        /// 是否已完成任务
        /// </summary>
        public bool HasCompletedQuest(int questId)
        {
            lock (_lock)
            {
                return _completedQuestIds.Contains(questId);
            }
        }

        /// <summary>
        /// 是否有活跃任务
        /// </summary>
        public bool HasActiveQuest(int questId)
        {
            lock (_lock)
            {
                return _activeQuests.ContainsKey(questId);
            }
        }

        /// <summary>
        /// 获取已完成的任务ID列表
        /// </summary>
        public List<int> GetCompletedQuestIds()
        {
            lock (_lock)
            {
                return _completedQuestIds.ToList();
            }
        }

        /// <summary>
        /// 获取可接取的任务列表
        /// </summary>
        public List<QuestDefinition> GetAvailableQuests(QuestDefinitionRepository repo)
        {
            var result = new List<QuestDefinition>();

            foreach (var def in repo.GetAll())
            {
                if (CanAcceptQuest(def) == QuestOperationResult.Success)
                {
                    result.Add(def);
                }
            }

            return result;
        }

        /// <summary>
        /// 获取可提交的任务列表
        /// </summary>
        public List<QuestInstance> GetTurnInReadyQuests()
        {
            lock (_lock)
            {
                return _activeQuests.Values.Where(q => q.State == QuestState.Completed).ToList();
            }
        }

        /// <summary>
        /// Tick更新 限时任务
        /// </summary>
        public void Tick(float deltaTime)
        {
            List<QuestInstance> snapshot;
            lock (_lock)
            {
                snapshot = _activeQuests.Values.ToList();
            }

            List<int> toRemove = new();

            foreach (var quest in snapshot)
            {
                quest.Tick(deltaTime);

                if (quest.IsExpired)
                {
                    toRemove.Add(quest.Definition.QuestId);
                    QuestFailed?.Invoke(this, quest);
                }
            }

            if (toRemove.Count > 0)
            {
                lock (_lock)
                {
                    foreach (var id in toRemove)
                    {
                        _activeQuests.Remove(id);
                    }
                }
            }
        }

        /// <summary>
        /// 清空所有任务
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _activeQuests.Clear();
                _completedQuestIds.Clear();
            }
        }
    }

    #endregion

    #region 任务系统管理器

    /// <summary>
    /// 任务系统管理器 全局入口
    /// </summary>
    public class QuestSystem
    {
        // 单例
        private static QuestSystem? _instance;
        private static readonly object _instanceLock = new();

        public static QuestSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new QuestSystem();
                    }
                }
                return _instance;
            }
        }

        // 任务定义仓库
        public QuestDefinitionRepository Definitions { get; } = new();

        // 所有玩家的任务日志
        private readonly Dictionary<long, PlayerQuestLog> _playerLogs = new();
        private readonly object _logsLock = new();

        private QuestSystem() { }

        /// <summary>
        /// 获取或创建玩家任务日志
        /// </summary>
        public PlayerQuestLog GetPlayerLog(long playerId)
        {
            lock (_logsLock)
            {
                if (!_playerLogs.TryGetValue(playerId, out var log))
                {
                    log = new PlayerQuestLog(playerId);
                    _playerLogs[playerId] = log;
                }
                return log;
            }
        }

        /// <summary>
        /// 移除玩家任务日志
        /// </summary>
        public void RemovePlayerLog(long playerId)
        {
            lock (_logsLock)
            {
                _playerLogs.Remove(playerId);
            }
        }

        /// <summary>
        /// 注册任务定义
        /// </summary>
        public bool RegisterQuest(QuestDefinition quest)
        {
            return Definitions.Register(quest);
        }

        /// <summary>
        /// 全局通知任务进度 击杀 收集等
        /// </summary>
        public void NotifyObjectiveProgress(long playerId, QuestObjectiveType type, int targetId, int count)
        {
            var log = GetPlayerLog(playerId);
            log.UpdateObjectiveProgress(type, targetId, count);
        }

        /// <summary>
        /// 全局Tick
        /// </summary>
        public void Tick(float deltaTime)
        {
            List<PlayerQuestLog> snapshot;
            lock (_logsLock)
            {
                snapshot = _playerLogs.Values.ToList();
            }

            foreach (var log in snapshot)
            {
                log.Tick(deltaTime);
            }
        }

        /// <summary>
        /// 关闭系统
        /// </summary>
        public void Shutdown()
        {
            lock (_logsLock)
            {
                foreach (var log in _playerLogs.Values)
                {
                    log.Clear();
                }
                _playerLogs.Clear();
            }
            Definitions.Clear();
        }
    }

    #endregion

    #region 任务工厂 预设任务

    /// <summary>
    /// 任务工厂 创建常用任务
    /// </summary>
    public static class QuestFactory
    {
        /// <summary>
        /// 创建杀怪任务
        /// </summary>
        public static QuestDefinition CreateKillQuest(int questId, string name, int monsterId, string monsterName, int count)
        {
            return new QuestDefinition
            {
                QuestId = questId,
                Name = name,
                Description = $"击败{count}个{monsterName}",
                Type = QuestType.SideQuest,
                MinLevel = 1,
                Objectives = new List<QuestObjective>
                {
                    new QuestObjective
                    {
                        ObjectiveId = 1,
                        Description = $"击败{monsterName}",
                        Type = QuestObjectiveType.Kill,
                        TargetId = monsterId,
                        TargetName = monsterName,
                        RequiredCount = count,
                    }
                },
                RewardExperience = count * 50,
                RewardGold = count * 10,
                AutoComplete = true,
            };
        }

        /// <summary>
        /// 创建收集任务
        /// </summary>
        public static QuestDefinition CreateCollectQuest(int questId, string name, int itemId, string itemName, int count)
        {
            return new QuestDefinition
            {
                QuestId = questId,
                Name = name,
                Description = $"收集{count}个{itemName}",
                Type = QuestType.SideQuest,
                MinLevel = 1,
                Objectives = new List<QuestObjective>
                {
                    new QuestObjective
                    {
                        ObjectiveId = 1,
                        Description = $"收集{itemName}",
                        Type = QuestObjectiveType.Collect,
                        TargetId = itemId,
                        TargetName = itemName,
                        RequiredCount = count,
                    }
                },
                RewardExperience = count * 30,
                RewardGold = count * 15,
            };
        }

        /// <summary>
        /// 创建对话任务
        /// </summary>
        public static QuestDefinition CreateTalkQuest(int questId, string name, int npcId, string npcName)
        {
            return new QuestDefinition
            {
                QuestId = questId,
                Name = name,
                Description = $"与{npcName}对话",
                Type = QuestType.MainQuest,
                MinLevel = 1,
                Objectives = new List<QuestObjective>
                {
                    new QuestObjective
                    {
                        ObjectiveId = 1,
                        Description = $"与{npcName}对话",
                        Type = QuestObjectiveType.TalkToNPC,
                        TargetId = npcId,
                        TargetName = npcName,
                        RequiredCount = 1,
                    }
                },
                RewardExperience = 100,
                RewardGold = 50,
                AutoComplete = true,
            };
        }

        /// <summary>
        /// 创建护送任务
        /// </summary>
        public static QuestDefinition CreateEscortQuest(int questId, string name, int npcId, string npcName)
        {
            return new QuestDefinition
            {
                QuestId = questId,
                Name = name,
                Description = $"护送{npcName}到达目的地",
                Type = QuestType.SideQuest,
                MinLevel = 5,
                Objectives = new List<QuestObjective>
                {
                    new QuestObjective
                    {
                        ObjectiveId = 1,
                        Description = $"护送{npcName}",
                        Type = QuestObjectiveType.Escort,
                        TargetId = npcId,
                        TargetName = npcName,
                        RequiredCount = 1,
                    }
                },
                RewardExperience = 500,
                RewardGold = 200,
                TimeLimit = 600f,
            };
        }

        /// <summary>
        /// 创建主线任务
        /// </summary>
        public static QuestDefinition CreateMainQuest(int questId, string name, string description, int minLevel)
        {
            return new QuestDefinition
            {
                QuestId = questId,
                Name = name,
                Description = description,
                Type = QuestType.MainQuest,
                MinLevel = minLevel,
                IsAbandonable = false,
                IsShareable = false,
                Objectives = new List<QuestObjective>(),
                RewardExperience = 1000,
                RewardGold = 500,
            };
        }

        /// <summary>
        /// 创建日常任务
        /// </summary>
        public static QuestDefinition CreateDailyQuest(int questId, string name, int monsterId, string monsterName, int count)
        {
            return new QuestDefinition
            {
                QuestId = questId,
                Name = name,
                Description = $"每日击败{count}个{monsterName}",
                Type = QuestType.DailyQuest,
                MinLevel = 10,
                IsRepeatable = true,
                RepeatCooldownHours = 24f,
                Objectives = new List<QuestObjective>
                {
                    new QuestObjective
                    {
                        ObjectiveId = 1,
                        Description = $"击败{monsterName}",
                        Type = QuestObjectiveType.Kill,
                        TargetId = monsterId,
                        TargetName = monsterName,
                        RequiredCount = count,
                    }
                },
                RewardExperience = 200,
                RewardGold = 100,
                AutoComplete = true,
            };
        }

        /// <summary>
        /// 获取所有预设
        /// </summary>
        public static List<QuestDefinition> GetAllPresets()
        {
            return new List<QuestDefinition>
            {
                CreateKillQuest(5001, "ClearTheWolves", 1, "ForestWolf", 10),
                CreateCollectQuest(5002, "HerbGathering", 1001, "HealthHerb", 5),
                CreateTalkQuest(5003, "MeetTheElder", 101, "VillageElder"),
                CreateEscortQuest(5004, "EscortTheMerchant", 102, "Merchant"),
                CreateMainQuest(5005, "TheBeginning", "踏上冒险之旅", 1),
                CreateDailyQuest(5006, "DailyPatrol", 1, "ForestWolf", 20),
            };
        }

        /// <summary>
        /// 注册所有预设
        /// </summary>
        public static void RegisterAll(QuestSystem system)
        {
            foreach (var quest in GetAllPresets())
            {
                system.RegisterQuest(quest);
            }
        }
    }

    #endregion

    #region 任务UI数据

    /// <summary>
    /// 任务UI数据
    /// </summary>
    public class QuestUIData
    {
        public int QuestId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public QuestType Type { get; set; }
        public QuestState State { get; set; }
        public float OverallProgress { get; set; }
        public List<ObjectiveUIData> Objectives { get; set; } = new();
        public int RewardExperience { get; set; }
        public int RewardGold { get; set; }
        public List<string> RewardItems { get; set; } = new();
        public float RemainingTime { get; set; }
        public bool CanTurnIn { get; set; }
        public bool CanAbandon { get; set; }
    }

    /// <summary>
    /// 目标UI数据
    /// </summary>
    public class ObjectiveUIData
    {
        public string Description { get; set; } = string.Empty;
        public int CurrentCount { get; set; }
        public int RequiredCount { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsOptional { get; set; }
    }

    /// <summary>
    /// 任务UI数据收集器
    /// </summary>
    public static class QuestUIDataCollector
    {
        public static QuestUIData Collect(QuestInstance instance)
        {
            var data = new QuestUIData
            {
                QuestId = instance.Definition.QuestId,
                Name = instance.Definition.Name,
                Description = instance.Definition.Description,
                Type = instance.Definition.Type,
                State = instance.State,
                OverallProgress = instance.GetOverallProgress(),
                RewardExperience = instance.Definition.RewardExperience,
                RewardGold = instance.Definition.RewardGold,
                RemainingTime = instance.RemainingTime,
                CanTurnIn = instance.State == QuestState.Completed,
                CanAbandon = instance.Definition.IsAbandonable && instance.State == QuestState.Active,
            };

            foreach (var progress in instance.GetObjectiveProgress())
            {
                data.Objectives.Add(new ObjectiveUIData
                {
                    Description = progress.Objective.Description,
                    CurrentCount = progress.CurrentCount,
                    RequiredCount = progress.Objective.RequiredCount,
                    IsCompleted = progress.IsCompleted,
                    IsOptional = progress.Objective.IsOptional,
                });
            }

            return data;
        }

        public static List<QuestUIData> CollectAll(PlayerQuestLog log)
        {
            var result = new List<QuestUIData>();

            foreach (var instance in log.GetAllActiveQuests())
            {
                result.Add(Collect(instance));
            }

            return result;
        }
    }

    #endregion

    #region 任务事件桥接 与其他系统对接

    /// <summary>
    /// 任务事件桥接 把游戏事件转换为任务进度
    /// </summary>
    public class QuestEventBridge
    {
        private readonly QuestSystem _system;

        public QuestEventBridge(QuestSystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
        }

        /// <summary>
        /// 通知击杀怪物
        /// </summary>
        public void OnMonsterKilled(long playerId, int monsterId, int count = 1)
        {
            _system.NotifyObjectiveProgress(playerId, QuestObjectiveType.Kill, monsterId, count);
        }

        /// <summary>
        /// 通知收集物品
        /// </summary>
        public void OnItemCollected(long playerId, int itemId, int count = 1)
        {
            _system.NotifyObjectiveProgress(playerId, QuestObjectiveType.Collect, itemId, count);
        }

        /// <summary>
        /// 通知与NPC对话
        /// </summary>
        public void OnNPCInteraction(long playerId, int npcId)
        {
            _system.NotifyObjectiveProgress(playerId, QuestObjectiveType.TalkToNPC, npcId, 1);
        }

        /// <summary>
        /// 通知到达位置
        /// </summary>
        public void OnReachLocation(long playerId, int locationId)
        {
            _system.NotifyObjectiveProgress(playerId, QuestObjectiveType.ReachLocation, locationId, 1);
        }

        /// <summary>
        /// 通知使用物品
        /// </summary>
        public void OnItemUsed(long playerId, int itemId, int count = 1)
        {
            _system.NotifyObjectiveProgress(playerId, QuestObjectiveType.UseItem, itemId, count);
        }

        /// <summary>
        /// 通知完成副本
        /// </summary>
        public void OnDungeonCompleted(long playerId, int dungeonId)
        {
            _system.NotifyObjectiveProgress(playerId, QuestObjectiveType.CompleteDungeon, dungeonId, 1);
        }

        /// <summary>
        /// 通知PVP胜利
        /// </summary>
        public void OnPVPVictory(long playerId, int count = 1)
        {
            _system.NotifyObjectiveProgress(playerId, QuestObjectiveType.WinPVP, 0, count);
        }
    }

    #endregion

    #region 任务链管理

    /// <summary>
    /// 任务链 一系列相关任务
    /// </summary>
    public class QuestChain
    {
        public int ChainId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<int> QuestIds { get; set; } = new();

        /// <summary>
        /// 获取链中下一个任务
        /// </summary>
        public int? GetNextQuest(int currentQuestId)
        {
            int index = QuestIds.IndexOf(currentQuestId);
            if (index < 0 || index >= QuestIds.Count - 1) return null;
            return QuestIds[index + 1];
        }

        /// <summary>
        /// 获取链进度
        /// </summary>
        public float GetChainProgress(PlayerQuestLog log)
        {
            if (QuestIds.Count == 0) return 1f;

            int completed = 0;
            foreach (var id in QuestIds)
            {
                if (log.HasCompletedQuest(id))
                    completed++;
            }

            return (float)completed / QuestIds.Count;
        }
    }

    /// <summary>
    /// 任务链仓库
    /// </summary>
    public class QuestChainRepository
    {
        private readonly Dictionary<int, QuestChain> _chains = new();
        private readonly object _lock = new();

        public bool Register(QuestChain chain)
        {
            if (chain == null || chain.ChainId <= 0) return false;
            lock (_lock)
            {
                _chains[chain.ChainId] = chain;
                return true;
            }
        }

        public QuestChain? GetById(int chainId)
        {
            lock (_lock)
            {
                _chains.TryGetValue(chainId, out var c);
                return c;
            }
        }

        public List<QuestChain> GetAll()
        {
            lock (_lock)
            {
                return _chains.Values.ToList();
            }
        }
    }

    #endregion

    #region 任务系统初始化辅助

    /// <summary>
    /// 任务系统启动器
    /// </summary>
    public static class QuestSystemBootstrap
    {
        public static QuestSystem Initialize()
        {
            var system = QuestSystem.Instance;
            QuestFactory.RegisterAll(system);
            return system;
        }

        public static string RunSmokeTest()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Quest System Smoke Test ===");

            var system = Initialize();
            sb.AppendLine($"System initialized with {system.Definitions.Count} quests");

            // 创建玩家日志
            var log = system.GetPlayerLog(1);
            log.PlayerLevel = 10;
            log.PlayerClass = "Warrior";

            // 接取任务
            var killQuest = system.Definitions.GetById(5001);
            if (killQuest != null)
            {
                var result = log.AcceptQuest(killQuest);
                sb.AppendLine($"Accept {killQuest.Name}: {result}");
            }

            var collectQuest = system.Definitions.GetById(5002);
            if (collectQuest != null)
            {
                var result = log.AcceptQuest(collectQuest);
                sb.AppendLine($"Accept {collectQuest.Name}: {result}");
            }

            sb.AppendLine($"Active quests: {log.ActiveQuestCount}");

            // 更新进度 击杀怪物
            log.UpdateObjectiveProgress(QuestObjectiveType.Kill, 1, 5);
            sb.AppendLine($"After killing 5 wolves:");

            var activeQuests = log.GetAllActiveQuests();
            foreach (var q in activeQuests)
            {
                sb.AppendLine($"  {q.Definition.Name}: {q.GetOverallProgress() * 100:F0}%");
                foreach (var p in q.GetObjectiveProgress())
                {
                    sb.AppendLine($"    {p}");
                }
            }

            // 继续击杀完成
            log.UpdateObjectiveProgress(QuestObjectiveType.Kill, 1, 5);

            sb.AppendLine($"After killing 5 more wolves:");
            foreach (var q in log.GetAllActiveQuests())
            {
                sb.AppendLine($"  {q.Definition.Name}: state={q.State}");
            }

            // 收集物品
            log.UpdateObjectiveProgress(QuestObjectiveType.Collect, 1001, 5);

            sb.AppendLine($"After collecting 5 herbs:");
            foreach (var q in log.GetAllActiveQuests())
            {
                sb.AppendLine($"  {q.Definition.Name}: state={q.State}");
            }

            // 检查可提交
            var turnInReady = log.GetTurnInReadyQuests();
            sb.AppendLine($"Turn-in ready: {turnInReady.Count}");

            system.Shutdown();
            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

} // namespace YamaEngine.Gameplay
