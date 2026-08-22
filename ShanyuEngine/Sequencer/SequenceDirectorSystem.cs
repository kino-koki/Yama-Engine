using System;
using System.Collections.Generic;

namespace ShanyuEngine.Sequencer
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 过场导演系统 (Sequence Director System)
    // --------------------------------------------------------------------------
    // 管理过场动画的高级控制逻辑：
    //   - SequenceDirector：导演控制器（自动播放/手动触发/排队/中断/优先级）
    //   - SequenceQueue：序列队列（多序列排队/交叉淡入淡出/条件触发）
    //   - SequenceBranch：序列分支（条件分支/随机分支/玩家选择分支）
    //   - InteractiveSequence：互动过场（玩家输入点/选项/限时选择→分支）
    //   - SequenceState：过场状态枚举
    //   - 过场跳过（Skip/一键跳过/部分跳过/不可跳过标记）
    //   - 过场中断（战斗中断→保存状态→返回）
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>过场动画状态。</summary>
    public enum SequenceState
    {
        /// <summary>空闲，无过场播放。</summary>
        Idle,
        /// <summary>正在播放。</summary>
        Playing,
        /// <summary>暂停。</summary>
        Paused,
        /// <summary>淡入/淡出中。</summary>
        Fading,
        /// <summary>在队列中等待。</summary>
        Queued,
        /// <summary>被中断（如战斗触发）。</summary>
        Interrupted,
        /// <summary>已完成。</summary>
        Completed
    }

    /// <summary>中断恢复策略。</summary>
    public enum InterruptResumePolicy
    {
        /// <summary>不恢复，被中断后丢弃。</summary>
        Discard,
        /// <summary>自动恢复（中断结束后继续播放）。</summary>
        AutoResume,
        /// <summary>从头重新播放。</summary>
        Restart,
        /// <summary>询问玩家是否恢复。</summary>
        AskPlayer
    }

    /// <summary>跳过策略。</summary>
    public enum SkipPolicy
    {
        /// <summary>完全可跳过。</summary>
        FullySkippable,
        /// <summary>可一键跳过全部。</summary>
        OneClickSkip,
        /// <summary>可部分跳过（跳到下一个章节标记）。</summary>
        PartialSkip,
        /// <summary>不可跳过。</summary>
        NotSkippable
    }

    /// <summary>分支类型。</summary>
    public enum BranchType
    {
        /// <summary>条件分支（满足条件则进入分支）。</summary>
        Conditional,
        /// <summary>随机分支。</summary>
        Random,
        /// <summary>玩家选择分支。</summary>
        PlayerChoice
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceDirector - 导演控制器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 过场导演控制器：管理过场动画的高级播放逻辑。
    /// 支持自动播放、手动触发、排队、中断和优先级管理。
    /// </summary>
    public class SequenceDirector
    {
        /// <summary>导演名称。</summary>
        public string Name { get; set; } = "Director";

        /// <summary>当前播放状态。</summary>
        public SequenceState CurrentState { get; private set; } = SequenceState.Idle;

        /// <summary>当前播放的序列实例。</summary>
        public SequenceInstance? CurrentInstance { get; private set; }

        /// <summary>序列队列。</summary>
        public SequenceQueue Queue { get; } = new();

        /// <summary>跳过策略。</summary>
        public SkipPolicy SkipPolicy { get; set; } = SkipPolicy.FullySkippable;

        /// <summary>中断恢复策略。</summary>
        public InterruptResumePolicy InterruptPolicy { get; set; } = InterruptResumePolicy.AutoResume;

        /// <summary>交叉淡入淡出时间（秒）。</summary>
        public double CrossFadeDuration { get; set; } = 0.5;

        /// <summary>自动播放下一个队列中的序列。</summary>
        public bool AutoPlayQueue { get; set; } = true;

        /// <summary>最大同时播放实例数（通常为 1）。</summary>
        public int MaxConcurrentInstances { get; set; } = 1;

        /// <summary>状态变化事件。</summary>
        public event Action<SequenceState, SequenceState>? OnStateChanged;

        /// <summary>过场开始事件。</summary>
        public event Action<SequenceInstance>? OnSequenceStarted;

        /// <summary>过场完成事件。</summary>
        public event Action<SequenceInstance>? OnSequenceCompleted;

        /// <summary>过场被跳过事件。</summary>
        public event Action<SequenceInstance>? OnSequenceSkipped;

        /// <summary>过场被中断事件。</summary>
        public event Action<SequenceInstance>? OnSequenceInterrupted;

        /// <summary>所有实例列表（含已完成）。</summary>
        private readonly List<SequenceInstance> _allInstances = new();

        /// <summary>被中断的实例（等待恢复）。</summary>
        private SequenceInstance? _interruptedInstance;

        /// <summary>中断时保存的播放时间。</summary>
        private double _interruptedTime;

        /// <summary>不可跳过的标记时间集合。</summary>
        private readonly HashSet<double> _noSkipTimes = new();

        /// <summary>获取所有实例。</summary>
        public IReadOnlyList<SequenceInstance> AllInstances => _allInstances;

        public SequenceDirector() { }
        public SequenceDirector(string name) { Name = name; }

        // ── 播放控制 ──────────────────────────────────────────────

        /// <summary>立即播放指定实例。</summary>
        public void Play(SequenceInstance instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            // 如果有当前实例，先停止
            if (CurrentInstance != null)
            {
                StopCurrent(false);
            }

            CurrentInstance = instance;
            _allInstances.Add(instance);

            SetState(SequenceState.Fading);
            instance.OnPlaybackCompleted += HandleInstanceCompleted;

            // 淡入后开始播放
            instance.Play();
            SetState(SequenceState.Playing);
            OnSequenceStarted?.Invoke(instance);
        }

        /// <summary>将实例加入队列。</summary>
        public void Enqueue(SequenceInstance instance, double crossFade = -1)
        {
            if (instance == null) return;
            Queue.Enqueue(instance, crossFade < 0 ? CrossFadeDuration : crossFade);
            instance.OnPlaybackCompleted += HandleInstanceCompleted;
        }

        /// <summary>停止当前播放的过场。</summary>
        public void StopCurrent(bool markCompleted = true)
        {
            if (CurrentInstance == null) return;

            if (markCompleted)
                SetState(SequenceState.Completed);
            else
                SetState(SequenceState.Idle);

            CurrentInstance.Stop();
            OnSequenceCompleted?.Invoke(CurrentInstance);
            CurrentInstance.OnPlaybackCompleted -= HandleInstanceCompleted;
            CurrentInstance = null;
        }

        /// <summary>跳过当前过场。</summary>
        public void Skip()
        {
            if (CurrentInstance == null) return;

            switch (SkipPolicy)
            {
                case SkipPolicy.FullySkippable:
                case SkipPolicy.OneClickSkip:
                    StopCurrent(false);
                    OnSequenceSkipped?.Invoke(CurrentInstance!);
                    break;

                case SkipPolicy.PartialSkip:
                    SkipToNextChapter();
                    break;

                case SkipPolicy.NotSkippable:
                    // 不可跳过，忽略
                    break;
            }
        }

        /// <summary>部分跳过：跳到下一个章节标记。</summary>
        private void SkipToNextChapter()
        {
            if (CurrentInstance?.Sequence == null) return;

            double currentTime = CurrentInstance.Sequence.CurrentTime;
            double nextChapter = double.MaxValue;

            foreach (var marker in CurrentInstance.Sequence.Markers)
            {
                if (marker.MarkerType == SequenceMarkerType.Chapter &&
                    marker.Time > currentTime &&
                    marker.Time < nextChapter)
                {
                    // 检查是否在不可跳过区域
                    if (!_noSkipTimes.Contains(marker.Time))
                    {
                        nextChapter = marker.Time;
                    }
                }
            }

            if (nextChapter < double.MaxValue)
            {
                CurrentInstance.Controller?.JumpTo(nextChapter);
            }
            else
            {
                StopCurrent(false);
            }
        }

        /// <summary>暂停当前过场。</summary>
        public void Pause()
        {
            if (CurrentInstance == null) return;
            CurrentInstance.Pause();
            SetState(SequenceState.Paused);
        }

        /// <summary>恢复当前过场。</summary>
        public void Resume()
        {
            if (CurrentInstance == null) return;
            CurrentInstance.Resume();
            SetState(SequenceState.Playing);
        }

        // ── 中断 ──────────────────────────────────────────────

        /// <summary>中断当前过场（如战斗触发）。</summary>
        public void Interrupt(string reason = "combat")
        {
            if (CurrentInstance == null || CurrentState != SequenceState.Playing) return;

            // 保存中断状态
            _interruptedInstance = CurrentInstance;
            _interruptedTime = CurrentInstance.Sequence?.CurrentTime ?? 0;

            SetState(SequenceState.Interrupted);
            CurrentInstance.Pause();
            OnSequenceInterrupted?.Invoke(CurrentInstance);
        }

        /// <summary>恢复被中断的过场。</summary>
        public void ResumeFromInterrupt()
        {
            if (_interruptedInstance == null) return;

            switch (InterruptPolicy)
            {
                case InterruptResumePolicy.Discard:
                    _interruptedInstance.Stop();
                    _interruptedInstance = null;
                    SetState(SequenceState.Idle);
                    break;

                case InterruptResumePolicy.AutoResume:
                    CurrentInstance = _interruptedInstance;
                    CurrentInstance.Controller?.JumpTo(_interruptedTime);
                    CurrentInstance.Resume();
                    _interruptedInstance = null;
                    SetState(SequenceState.Playing);
                    break;

                case InterruptResumePolicy.Restart:
                    CurrentInstance = _interruptedInstance;
                    CurrentInstance.Controller?.JumpTo(0);
                    CurrentInstance.Resume();
                    _interruptedInstance = null;
                    SetState(SequenceState.Playing);
                    break;

                case InterruptResumePolicy.AskPlayer:
                    // 触发询问玩家 UI 事件
                    SetState(SequenceState.Paused);
                    break;
            }
        }

        /// <summary>添加不可跳过的时间点。</summary>
        public void AddNoSkipPoint(double time)
        {
            _noSkipTimes.Add(time);
        }

        /// <summary>移除不可跳过的时间点。</summary>
        public void RemoveNoSkipPoint(double time)
        {
            _noSkipTimes.Remove(time);
        }

        // ── 帧更新 ──────────────────────────────────────────────

        /// <summary>每帧更新。</summary>
        public void Tick(double deltaTime)
        {
            // 更新当前实例
            CurrentInstance?.Tick(deltaTime);

            // 检查当前实例是否完成
            if (CurrentInstance != null && CurrentInstance.State == SequenceInstanceState.Stopped)
            {
                HandleInstanceCompleted();
            }

            // 自动播放队列
            if (CurrentInstance == null && AutoPlayQueue && Queue.Count > 0)
            {
                var next = Queue.Dequeue();
                if (next != null)
                {
                    Play(next);
                }
            }
        }

        /// <summary>实例完成回调。</summary>
        private void HandleInstanceCompleted()
        {
            if (CurrentInstance == null) return;
            OnSequenceCompleted?.Invoke(CurrentInstance);
            CurrentInstance.OnPlaybackCompleted -= HandleInstanceCompleted;
            CurrentInstance = null;
            SetState(SequenceState.Idle);
        }

        /// <summary>设置导演状态。</summary>
        private void SetState(SequenceState newState)
        {
            var old = CurrentState;
            CurrentState = newState;
            OnStateChanged?.Invoke(old, newState);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceQueue - 序列队列
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列队列：管理多个序列的排队播放。
    /// 支持优先级插入、交叉淡入淡出、条件触发。
    /// </summary>
    public class SequenceQueue
    {
        /// <summary>队列中的元素。</summary>
        private readonly List<QueueEntry> _entries = new();

        /// <summary>队列中的元素数量。</summary>
        public int Count => _entries.Count;

        /// <summary>队列是否为空。</summary>
        public bool IsEmpty => _entries.Count == 0;

        /// <summary>最大队列容量（0=无限）。</summary>
        public int MaxCapacity { get; set; }

        /// <summary>队列变更事件。</summary>
        public event Action? OnQueueChanged;

        /// <summary>入队回调。</summary>
        public event Action<SequenceInstance>? OnEnqueued;

        /// <summary>出队回调。</summary>
        public event Action<SequenceInstance>? OnDequeued;

        public SequenceQueue() { }

        /// <summary>将序列实例加入队列。</summary>
        public void Enqueue(SequenceInstance instance, double crossFadeDuration = 0.5, int priority = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            if (MaxCapacity > 0 && _entries.Count >= MaxCapacity)
            {
                // 队列已满，移除最低优先级的元素
                if (priority > GetLowestPriority())
                {
                    _entries.RemoveAt(_entries.Count - 1);
                }
                else
                {
                    return; // 新元素优先级不足，丢弃
                }
            }

            var entry = new QueueEntry
            {
                Instance = instance,
                CrossFadeDuration = crossFadeDuration,
                Priority = priority,
                EnqueuedAt = DateTime.UtcNow
            };

            _entries.Add(entry);
            _entries.Sort((a, b) => b.Priority.CompareTo(a.Priority)); // 高优先级在前
            OnEnqueued?.Invoke(instance);
            OnQueueChanged?.Invoke();
        }

        /// <summary>从队列头部取出下一个序列。</summary>
        public SequenceInstance? Dequeue()
        {
            if (_entries.Count == 0) return null;

            var entry = _entries[0];
            _entries.RemoveAt(0);
            OnDequeued?.Invoke(entry.Instance);
            OnQueueChanged?.Invoke();
            return entry.Instance;
        }

        /// <summary>查看队列头部但不移除。</summary>
        public SequenceInstance? Peek()
        {
            return _entries.Count > 0 ? _entries[0].Instance : null;
        }

        /// <summary>按条件过滤出队。</summary>
        public SequenceInstance? DequeueWhere(Predicate<QueueEntry> predicate)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (predicate(_entries[i]))
                {
                    var entry = _entries[i];
                    _entries.RemoveAt(i);
                    OnDequeued?.Invoke(entry.Instance);
                    OnQueueChanged?.Invoke();
                    return entry.Instance;
                }
            }
            return null;
        }

        /// <summary>清空队列。</summary>
        public void Clear()
        {
            _entries.Clear();
            OnQueueChanged?.Invoke();
        }

        /// <summary>移除指定实例。</summary>
        public bool Remove(SequenceInstance instance)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Instance == instance)
                {
                    _entries.RemoveAt(i);
                    OnQueueChanged?.Invoke();
                    return true;
                }
            }
            return false;
        }

        /// <summary>获取最低优先级。</summary>
        private int GetLowestPriority()
        {
            if (_entries.Count == 0) return int.MinValue;
            int min = int.MaxValue;
            foreach (var entry in _entries)
                if (entry.Priority < min) min = entry.Priority;
            return min;
        }

        /// <summary>队列条目。</summary>
        public class QueueEntry
        {
            public SequenceInstance Instance { get; set; } = null!;
            public double CrossFadeDuration { get; set; }
            public int Priority { get; set; }
            public DateTime EnqueuedAt { get; set; }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceBranch - 序列分支
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列分支：在过场中根据条件选择不同的后续序列。
    /// 支持条件分支、随机分支、玩家选择分支。
    /// </summary>
    public class SequenceBranch
    {
        /// <summary>分支名称。</summary>
        public string BranchName { get; set; } = "Branch";

        /// <summary>分支类型。</summary>
        public BranchType BranchType { get; set; } = BranchType.Conditional;

        /// <summary>分支选项列表。</summary>
        public List<BranchOption> Options { get; set; } = new();

        /// <summary>默认分支索引（条件不满足时使用）。</summary>
        public int DefaultOptionIndex { get; set; }

        /// <summary>条件评估回调。</summary>
        public Func<object?, int>? EvaluateCondition { get; set; }

        /// <summary>分支选择事件。</summary>
        public event Action<int>? OnBranchSelected;

        /// <summary>玩家选择请求事件（BranchType=PlayerChoice 时触发）。</summary>
        public event Action<List<BranchOption>>? OnPlayerChoiceRequested;

        public SequenceBranch() { }
        public SequenceBranch(string name) { BranchName = name; }

        // ── 评估分支 ──────────────────────────────────────────────

        /// <summary>评估分支，返回选中的选项索引。</summary>
        public int Evaluate(object? context)
        {
            switch (BranchType)
            {
                case BranchType.Conditional:
                    return EvaluateConditional(context);

                case BranchType.Random:
                    return EvaluateRandom();

                case BranchType.PlayerChoice:
                    return EvaluatePlayerChoice();

                default:
                    return DefaultOptionIndex;
            }
        }

        /// <summary>条件分支评估。</summary>
        private int EvaluateConditional(object? context)
        {
            if (EvaluateCondition != null)
            {
                int result = EvaluateCondition(context);
                if (result >= 0 && result < Options.Count)
                {
                    OnBranchSelected?.Invoke(result);
                    return result;
                }
            }
            OnBranchSelected?.Invoke(DefaultOptionIndex);
            return DefaultOptionIndex;
        }

        /// <summary>随机分支评估（支持权重）。</summary>
        private int EvaluateRandom()
        {
            if (Options.Count == 0) return DefaultOptionIndex;

            // 计算总权重
            float totalWeight = 0;
            foreach (var option in Options)
                totalWeight += option.Weight;

            if (totalWeight <= 0)
            {
                // 等权重随机
                var rand = new Random();
                int idx = rand.Next(Options.Count);
                OnBranchSelected?.Invoke(idx);
                return idx;
            }

            // 加权随机
            float roll = (float)new Random().NextDouble() * totalWeight;
            float cumulative = 0;
            for (int i = 0; i < Options.Count; i++)
            {
                cumulative += Options[i].Weight;
                if (roll <= cumulative)
                {
                    OnBranchSelected?.Invoke(i);
                    return i;
                }
            }

            OnBranchSelected?.Invoke(Options.Count - 1);
            return Options.Count - 1;
        }

        /// <summary>玩家选择分支（需要异步等待玩家输入）。</summary>
        private int EvaluatePlayerChoice()
        {
            // 触发 UI 事件，实际选择在玩家操作后通过 SelectPlayerChoice 设置
            OnPlayerChoiceRequested?.Invoke(Options);
            return -1; // 尚未选择
        }

        /// <summary>玩家做出选择后调用。</summary>
        public void SelectPlayerChoice(int selectedIndex)
        {
            if (selectedIndex >= 0 && selectedIndex < Options.Count)
            {
                OnBranchSelected?.Invoke(selectedIndex);
            }
        }

        // ── 选项管理 ──────────────────────────────────────────────

        /// <summary>添加分支选项。</summary>
        public void AddOption(BranchOption option)
        {
            Options.Add(option);
        }

        /// <summary>添加分支选项（简化）。</summary>
        public void AddOption(string name, SequenceAsset? sequenceAsset, float weight = 1.0f)
        {
            Options.Add(new BranchOption
            {
                Name = name,
                SequenceAsset = sequenceAsset,
                Weight = weight
            });
        }
    }

    /// <summary>分支选项定义。</summary>
    public class BranchOption
    {
        /// <summary>选项名称（显示给玩家的文本）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>选项描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>关联的序列资产。</summary>
        public SequenceAsset? SequenceAsset { get; set; }

        /// <summary>选择权重（随机分支时使用）。</summary>
        public float Weight { get; set; } = 1.0f;

        /// <summary>条件（条件分支时，此选项的触发条件）。</summary>
        public Func<object?, bool>? Condition { get; set; }

        /// <summary>自定义数据（用于脚本事件传递）。</summary>
        public object? UserData { get; set; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // InteractiveSequence - 互动过场
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 互动过场：在过场动画中插入玩家交互点。
    /// 支持限时选择、QTE（快速时间事件）、对话选项等。
    /// </summary>
    public class InteractiveSequence
    {
        /// <summary>互动过场名称。</summary>
        public string Name { get; set; } = "Interactive Sequence";

        /// <summary>关联的序列实例。</summary>
        public SequenceInstance? BaseInstance { get; set; }

        /// <summary>互动点列表。</summary>
        public List<InteractionPoint> InteractionPoints { get; set; } = new();

        /// <summary>互动超时处理策略。</summary>
        public TimeoutPolicy TimeoutPolicy { get; set; } = TimeoutPolicy.UseDefault;

        /// <summary>默认选择索引（超时时使用）。</summary>
        public int DefaultChoiceIndex { get; set; }

        /// <summary>当前活跃的互动点。</summary>
        public InteractionPoint? CurrentInteraction { get; private set; }

        /// <summary>互动开始事件。</summary>
        public event Action<InteractionPoint>? OnInteractionStarted;

        /// <summary>互动完成事件。</summary>
        public event Action<InteractionPoint, int>? OnInteractionCompleted;

        /// <summary>互动超时事件。</summary>
        public event Action<InteractionPoint>? OnInteractionTimeout;

        public InteractiveSequence() { }

        // ── 互动点管理 ──────────────────────────────────────────────

        /// <summary>添加互动点。</summary>
        public void AddInteractionPoint(InteractionPoint point)
        {
            InteractionPoints.Add(point);
            InteractionPoints.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime));
        }

        /// <summary>添加限时选择互动点。</summary>
        public void AddTimedChoice(double triggerTime, double timeout, List<string> choices, int defaultIndex = 0)
        {
            var options = new List<InteractionChoice>();
            for (int i = 0; i < choices.Count; i++)
            {
                options.Add(new InteractionChoice
                {
                    Text = choices[i],
                    ChoiceIndex = i
                });
            }

            InteractionPoints.Add(new InteractionPoint
            {
                TriggerTime = triggerTime,
                Timeout = timeout,
                InteractionType = InteractionType.TimedChoice,
                Choices = options,
                DefaultChoiceIndex = defaultIndex
            });
        }

        // ── 帧更新 ──────────────────────────────────────────────

        /// <summary>每帧更新互动过场。</summary>
        public void Tick(double deltaTime)
        {
            if (BaseInstance?.Sequence == null) return;

            double currentTime = BaseInstance.Sequence.CurrentTime;

            // 检查是否需要触发新的互动点
            if (CurrentInteraction == null)
            {
                foreach (var point in InteractionPoints)
                {
                    if (!point.IsTriggered &&
                        currentTime >= point.TriggerTime &&
                        currentTime < point.TriggerTime + 0.05)
                    {
                        TriggerInteraction(point);
                        break;
                    }
                }
            }
            else
            {
                // 更新当前互动的计时器
                CurrentInteraction.Update(deltaTime);

                if (CurrentInteraction.IsTimedOut)
                {
                    HandleTimeout();
                }
            }
        }

        /// <summary>触发互动点。</summary>
        private void TriggerInteraction(InteractionPoint point)
        {
            CurrentInteraction = point;
            point.IsTriggered = true;
            point.IsCompleted = false;

            // 根据类型处理
            switch (point.InteractionType)
            {
                case InteractionType.TimedChoice:
                    // 暂停序列，等待玩家输入
                    BaseInstance?.Pause();
                    OnInteractionStarted?.Invoke(point);
                    break;

                case InteractionType.QTE:
                    // QTE 仍然播放序列，但等待按键输入
                    OnInteractionStarted?.Invoke(point);
                    break;

                case InteractionType.PlayerInput:
                    BaseInstance?.Pause();
                    OnInteractionStarted?.Invoke(point);
                    break;
            }
        }

        /// <summary>玩家做出选择。</summary>
        public void MakeChoice(int choiceIndex)
        {
            if (CurrentInteraction == null) return;

            CurrentInteraction.IsCompleted = true;
            CurrentInteraction.SelectedChoiceIndex = choiceIndex;

            OnInteractionCompleted?.Invoke(CurrentInteraction, choiceIndex);

            // 恢复序列播放
            BaseInstance?.Resume();

            CurrentInteraction = null;
        }

        /// <summary>QTE 按键输入。</summary>
        public void SubmitQTEInput(string inputKey)
        {
            if (CurrentInteraction == null) return;

            bool success = false;
            if (CurrentInteraction.QTEKey != null &&
                inputKey.Equals(CurrentInteraction.QTEKey, StringComparison.OrdinalIgnoreCase))
            {
                success = true;
            }

            CurrentInteraction.QTEResult = success;
            CurrentInteraction.IsCompleted = true;

            OnInteractionCompleted?.Invoke(CurrentInteraction, success ? 0 : 1);
            CurrentInteraction = null;
        }

        /// <summary>处理超时。</summary>
        private void HandleTimeout()
        {
            if (CurrentInteraction == null) return;

            OnInteractionTimeout?.Invoke(CurrentInteraction);

            switch (TimeoutPolicy)
            {
                case TimeoutPolicy.UseDefault:
                    MakeChoice(CurrentInteraction.DefaultChoiceIndex);
                    break;

                case TimeoutPolicy.Fail:
                    MakeChoice(-1); // 失败
                    break;

                case TimeoutPolicy.WaitForever:
                    // 不处理，继续等待
                    break;
            }
        }
    }

    /// <summary>互动类型。</summary>
    public enum InteractionType
    {
        /// <summary>限时选择。</summary>
        TimedChoice,
        /// <summary>QTE（快速时间事件）。</summary>
        QTE,
        /// <summary>玩家输入。</summary>
        PlayerInput
    }

    /// <summary>超时策略。</summary>
    public enum TimeoutPolicy
    {
        /// <summary>使用默认选项。</summary>
        UseDefault,
        /// <summary>视为失败。</summary>
        Fail,
        /// <summary>永久等待。</summary>
        WaitForever
    }

    /// <summary>互动点：过场动画中的玩家交互位置。</summary>
    public class InteractionPoint
    {
        /// <summary>互动点唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>触发时间（序列中的时间点，秒）。</summary>
        public double TriggerTime { get; set; }

        /// <summary>互动类型。</summary>
        public InteractionType InteractionType { get; set; }

        /// <summary>超时时间（秒，0=无限制）。</summary>
        public double Timeout { get; set; } = 5.0;

        /// <summary>默认选择索引。</summary>
        public int DefaultChoiceIndex { get; set; }

        /// <summary>选项列表（限时选择类型）。</summary>
        public List<InteractionChoice> Choices { get; set; } = new();

        /// <summary>QTE 按键（QTE 类型）。</summary>
        public string? QTEKey { get; set; }

        /// <summary>QTE 结果。</summary>
        public bool QTEResult { get; set; }

        /// <summary>是否已触发。</summary>
        public bool IsTriggered { get; set; }

        /// <summary>是否已完成。</summary>
        public bool IsCompleted { get; set; }

        /// <summary>已选择的选项索引。</summary>
        public int SelectedChoiceIndex { get; set; } = -1;

        /// <summary>是否已超时。</summary>
        public bool IsTimedOut { get; private set; }

        /// <summary>超时计时器。</summary>
        private double _elapsed;

        /// <summary>每帧更新超时计时。</summary>
        public void Update(double deltaTime)
        {
            if (IsCompleted || IsTimedOut) return;

            if (Timeout > 0)
            {
                _elapsed += deltaTime;
                if (_elapsed >= Timeout)
                {
                    IsTimedOut = true;
                }
            }
        }
    }

    /// <summary>互动选项。</summary>
    public class InteractionChoice
    {
        /// <summary>选项文本。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>选项索引。</summary>
        public int ChoiceIndex { get; set; }

        /// <summary>选项描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>选项图标标识。</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>选项是否可用（根据条件动态变化）。</summary>
        public Func<object?, bool>? IsAvailable { get; set; }
    }
}
