using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    // ============================================================================================
    //  AnimMontageSystem.cs
    //
    //  蒙太奇系统（Animation Montage System）。
    //
    //  提供类似 UE5 AnimMontage 的完整蒙太奇管线：
    //    - AnimMontage          ：蒙太奇资产，包含段（Section）、插槽（Slot）、段片段（Segment）
    //                            、分支点（BranchPoint）。
    //    - MontageInstance      ：运行时实例，管理播放进度、淡入/淡出、同步组。
    //    - MontageSection       ：蒙太奇段，定义一段连续动画。
    //    - MontageSlot          ：插槽定义，蒙太奇通过插槽注入动画图。
    //    - MontageSegment       ：段中的具体片段（动画剪辑 + 时间范围）。
    //    - BranchPoint          ：分支点（Notify 事件）。
    //    - MontageSyncGroup     ：蒙太奇同步组（多人同步）。
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  蒙太奇段（MontageSection）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 蒙太奇段。定义一段连续动画，由多个 Segment 组成。
    /// 段可以顺序播放或根据分支点跳转。
    /// </summary>
    public sealed class MontageSection
    {
        /// <summary>段名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>该段包含的片段索引列表。</summary>
        public List<int> SegmentIndices { get; } = new();

        /// <summary>段的总时长（秒）。</summary>
        public float LengthSeconds { get; set; }

        /// <summary>播放完成后自动过渡到的下一个段名称。</summary>
        public string NextSection { get; set; } = string.Empty;

        /// <summary>该段播放完成的 Notify 事件名称。</summary>
        public string EndNotifyName { get; set; } = "OnSectionEnd";
    }

    // ──────────────────────────────────────────────────────────────
    //  蒙太奇插槽（MontageSlot）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 蒙太奇插槽。蒙太奇通过插槽名称注入动画图的 Slot 节点。
    /// </summary>
    public sealed class MontageSlot
    {
        /// <summary>插槽名称。</summary>
        public string Name { get; set; } = "DefaultSlot";

        /// <summary>该插槽中的片段索引列表。</summary>
        public List<int> SegmentIndices { get; } = new();
    }

    // ──────────────────────────────────────────────────────────────
    //  蒙太奇片段（MontageSegment）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 蒙太奇中的单个动画片段。引用 AnimationClip 并在指定时间范围内播放。
    /// </summary>
    public sealed class MontageSegment
    {
        /// <summary>动画剪辑引用。</summary>
        public AnimationClip? Clip { get; set; }

        /// <summary>起始时间（秒，在剪辑内）。</summary>
        public float StartTime { get; set; }

        /// <summary>结束时间（秒，在剪辑内）。</summary>
        public float EndTime { get; set; }

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        /// <summary>所属插槽名称。</summary>
        public string SlotName { get; set; } = "DefaultSlot";

        /// <summary>片段时长。</summary>
        public float Length => MathF.Max(0f, EndTime - StartTime);

        /// <summary>在指定时间采样该片段。</summary>
        public SkeletonPose Sample(Skeleton skeleton, float localTime)
        {
            if (Clip == null) return skeleton.CreateBindPose();
            float clipTime = StartTime + localTime * PlayRate;
            return Clip.Sample(skeleton, clipTime);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  分支点（BranchPoint）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 蒙太奇分支点。在指定时间触发 Notify 事件。
    /// 可用于触发音效、粒子、游戏逻辑或段跳转。
    /// </summary>
    public sealed class BranchPoint
    {
        /// <summary>触发时间（秒，相对于片段起始）。</summary>
        public float TimeSeconds { get; set; }

        /// <summary>Notify 名称。</summary>
        public string NotifyName { get; set; } = string.Empty;

        /// <summary>触发条件。</summary>
        public Func<MontageInstance, bool>? TriggerCondition { get; set; }

        /// <summary>触发回调。</summary>
        public Action<MontageInstance>? OnTrigger { get; set; }

        /// <summary>是否已触发。</summary>
        internal bool HasTriggered;
    }

    // ──────────────────────────────────────────────────────────────
    //  蒙太奇资产（AnimMontage）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 蒙太奇资产。类似 UE5 的 UAnimMontage。
    /// 包含段（Sections）、插槽（Slots）、片段（Segments）、分支点（BranchPoints）。
    /// </summary>
    public sealed class AnimMontage
    {
        /// <summary>蒙太奇名称。</summary>
        public string Name { get; set; } = "AnimMontage";

        /// <summary>总时长（秒）。</summary>
        public float LengthSeconds { get; set; }

        /// <summary>段列表。</summary>
        public List<MontageSection> Sections { get; } = new();

        /// <summary>插槽列表。</summary>
        public List<MontageSlot> Slots { get; } = new();

        /// <summary>片段列表。</summary>
        public List<MontageSegment> Segments { get; } = new();

        /// <summary>分支点列表。</summary>
        public List<BranchPoint> BranchPoints { get; } = new();

        /// <summary>默认淡入时间（秒）。</summary>
        public float BlendInTime { get; set; } = 0.25f;

        /// <summary>默认淡出时间（秒）。</summary>
        public float BlendOutTime { get; set; } = 0.25f;

        /// <summary>默认插槽名称。</summary>
        public string DefaultSlotName { get; set; } = "DefaultSlot";

        // ──────────────────────────────────────────────
        //  段管理
        // ──────────────────────────────────────────────

        /// <summary>添加一个段。</summary>
        public MontageSection AddSection(string name, float length, string nextSection = "")
        {
            var section = new MontageSection
            {
                Name = name,
                LengthSeconds = length,
                NextSection = nextSection,
            };
            Sections.Add(section);
            LengthSeconds = MathF.Max(LengthSeconds, length);
            return section;
        }

        /// <summary>添加一个插槽。</summary>
        public MontageSlot AddSlot(string name)
        {
            var slot = new MontageSlot { Name = name };
            Slots.Add(slot);
            return slot;
        }

        /// <summary>添加一个片段。</summary>
        public MontageSegment AddSegment(AnimationClip clip, float startTime, float endTime, string slotName = "DefaultSlot")
        {
            var segment = new MontageSegment
            {
                Clip = clip,
                StartTime = startTime,
                EndTime = endTime,
                SlotName = slotName,
            };
            Segments.Add(segment);
            return segment;
        }

        /// <summary>添加一个分支点。</summary>
        public BranchPoint AddBranchPoint(float timeSeconds, string notifyName)
        {
            var bp = new BranchPoint
            {
                TimeSeconds = timeSeconds,
                NotifyName = notifyName,
            };
            BranchPoints.Add(bp);
            return bp;
        }

        /// <summary>查找段索引。</summary>
        public int FindSectionIndex(string name)
        {
            for (int i = 0; i < Sections.Count; i++)
                if (Sections[i].Name == name) return i;
            return -1;
        }

        /// <summary>获取指定插槽的所有片段。</summary>
        public List<MontageSegment> GetSegmentsForSlot(string slotName)
        {
            var result = new List<MontageSegment>();
            foreach (var seg in Segments)
                if (seg.SlotName == slotName)
                    result.Add(seg);
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  蒙太奇同步组（MontageSyncGroup）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 蒙太奇同步组。多个蒙太奇实例可加入同一同步组，
    /// 确保它们的播放进度在组内同步（用于多人游戏或镜像动画）。
    /// </summary>
    public sealed class MontageSyncGroup
    {
        /// <summary>组名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>组内实例列表。</summary>
        private readonly List<MontageInstance> _instances = new();

        /// <summary>添加实例到同步组。</summary>
        public void AddInstance(MontageInstance instance)
        {
            if (instance != null && !_instances.Contains(instance))
                _instances.Add(instance);
        }

        /// <summary>移除实例。</summary>
        public void RemoveInstance(MontageInstance instance)
        {
            _instances.Remove(instance);
        }

        /// <summary>
        /// 同步组内所有实例的播放进度。
        /// 以组内最慢/最快实例为基准（取决于 SyncMode）。
        /// </summary>
        public void Sync()
        {
            if (_instances.Count < 2) return;

            // 以第一个实例的标准化时间为基准
            float masterTime = _instances[0].NormalizedTime;

            for (int i = 1; i < _instances.Count; i++)
            {
                if (_instances[i].IsActive && !_instances[i].IsBlendingOut)
                {
                    _instances[i].SetNormalizedTime(masterTime);
                }
            }
        }

        /// <summary>同步组内的实例数量。</summary>
        public int Count => _instances.Count;

        /// <summary>清除所有实例。</summary>
        public void Clear() => _instances.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    //  蒙太奇实例（MontageInstance）
    // ──────────────────────────────────────────────────────────────

    /// <summary>蒙太奇实例状态。</summary>
    public enum MontageInstanceState
    {
        /// <summary>未开始。</summary>
        Inactive,
        /// <summary>淡入中。</summary>
        BlendingIn,
        /// <summary>正常播放中。</summary>
        Playing,
        /// <summary>淡出中。</summary>
        BlendingOut,
        /// <summary>已完成。</summary>
        Finished,
    }

    /// <summary>
    /// 蒙太奇运行时实例。管理蒙太奇的播放状态、进度、淡入淡出。
    /// </summary>
    public sealed class MontageInstance
    {
        /// <summary>所属蒙太奇资产。</summary>
        public AnimMontage Montage { get; }

        /// <summary>目标骨架。</summary>
        public Skeleton Skeleton { get; }

        // ── 播放控制 ──

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        /// <summary>淡入时间（秒）。</summary>
        public float BlendInTime { get; set; } = 0.25f;

        /// <summary>淡出时间（秒）。</summary>
        public float BlendOutTime { get; set; } = 0.25f;

        // ── 运行时状态 ──

        /// <summary>当前状态。</summary>
        public MontageInstanceState State { get; private set; } = MontageInstanceState.Inactive;

        /// <summary>播放进度（秒）。</summary>
        public float CurrentTime { get; private set; }

        /// <summary>标准化播放时间 [0, 1]。</summary>
        public float NormalizedTime => Montage.LengthSeconds > 0f
            ? Math.Clamp(CurrentTime / Montage.LengthSeconds, 0f, 1f)
            : 0f;

        /// <summary>当前段索引。</summary>
        public int CurrentSectionIndex { get; private set; }

        /// <summary>淡入淡出进度 [0, 1]。</summary>
        public float BlendProgress { get; private set; } = 1f;

        /// <summary>是否处于活动状态。</summary>
        public bool IsActive => State != MontageInstanceState.Inactive && State != MontageInstanceState.Finished;

        /// <summary>是否正在淡出。</summary>
        public bool IsBlendingOut => State == MontageInstanceState.BlendingOut;

        /// <summary>是否已完成。</summary>
        public bool IsFinished => State == MontageInstanceState.Finished;

        /// <summary>可选同步组。</summary>
        public MontageSyncGroup? SyncGroup { get; set; }

        // ── 内部 ──
        private float _blendElapsed;
        private float _sectionTime;
        private readonly Dictionary<string, float> _slotWeights = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>完成回调。</summary>
        public Action<MontageInstance>? OnFinished { get; set; }

        /// <summary>中断回调。</summary>
        public Action<MontageInstance>? OnInterrupted { get; set; }

        /// <summary>段结束回调。</summary>
        public Action<MontageInstance, string>? OnSectionEnd { get; set; }

        /// <summary>分支点触发回调。</summary>
        public Action<MontageInstance, BranchPoint>? OnBranchPoint { get; set; }

        /// <summary>构造函数。</summary>
        public MontageInstance(AnimMontage montage, Skeleton skeleton)
        {
            Montage = montage ?? throw new ArgumentNullException(nameof(montage));
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));

            // 初始化插槽权重
            foreach (var slot in montage.Slots)
                _slotWeights[slot.Name] = 0f;
            _slotWeights[montage.DefaultSlotName] = 0f;
        }

        // ──────────────────────────────────────────────
        //  生命周期
        // ──────────────────────────────────────────────

        /// <summary>开始淡入。</summary>
        public void StartBlendIn(float? blendTime = null)
        {
            if (blendTime.HasValue) BlendInTime = blendTime.Value;
            State = MontageInstanceState.BlendingIn;
            _blendElapsed = 0f;
            CurrentTime = 0f;
            CurrentSectionIndex = 0;
            _sectionTime = 0f;
        }

        /// <summary>开始淡出。</summary>
        public void StartBlendOut(float? blendTime = null)
        {
            if (blendTime.HasValue) BlendOutTime = blendTime.Value;
            if (State == MontageInstanceState.Playing || State == MontageInstanceState.BlendingIn)
            {
                State = MontageInstanceState.BlendingOut;
                _blendElapsed = 0f;
                OnInterrupted?.Invoke(this);
            }
        }

        /// <summary>每帧更新。</summary>
        public void Update(float deltaTime)
        {
            if (!IsActive) return;

            deltaTime = MathF.Max(0f, deltaTime);
            float effectiveDt = deltaTime * PlayRate;

            switch (State)
            {
                case MontageInstanceState.BlendingIn:
                    _blendElapsed += deltaTime;
                    if (_blendElapsed >= BlendInTime)
                    {
                        _blendElapsed = BlendInTime;
                        State = MontageInstanceState.Playing;
                    }
                    BlendProgress = BlendInTime > 0f ? Math.Clamp(_blendElapsed / BlendInTime, 0f, 1f) : 1f;
                    CurrentTime += effectiveDt;
                    break;

                case MontageInstanceState.Playing:
                    CurrentTime += effectiveDt;
                    BlendProgress = 1f;
                    break;

                case MontageInstanceState.BlendingOut:
                    _blendElapsed += deltaTime;
                    if (_blendElapsed >= BlendOutTime)
                    {
                        State = MontageInstanceState.Finished;
                        BlendProgress = 0f;
                        OnFinished?.Invoke(this);
                        return;
                    }
                    BlendProgress = BlendOutTime > 0f ? 1f - Math.Clamp(_blendElapsed / BlendOutTime, 0f, 1f) : 0f;
                    CurrentTime += effectiveDt;
                    break;
            }

            // 更新插槽权重
            foreach (var slotName in _slotWeights.Keys)
                _slotWeights[slotName] = BlendProgress;

            // 检查段边界
            AdvanceSection();

            // 检查分支点
            CheckBranchPoints(deltaTime);

            // 检查是否播放完毕
            if (State == MontageInstanceState.Playing && CurrentTime >= Montage.LengthSeconds)
            {
                // 如果有下一段则自动过渡
                var currentSection = GetCurrentSection();
                if (currentSection != null && !string.IsNullOrEmpty(currentSection.NextSection))
                {
                    int nextIdx = Montage.FindSectionIndex(currentSection.NextSection);
                    if (nextIdx >= 0)
                    {
                        JumpToSection(nextIdx);
                        OnSectionEnd?.Invoke(this, currentSection.Name);
                    }
                    else
                    {
                        State = MontageInstanceState.Finished;
                        OnFinished?.Invoke(this);
                    }
                }
                else
                {
                    State = MontageInstanceState.Finished;
                    OnFinished?.Invoke(this);
                }
            }

            // 同步组同步
            SyncGroup?.Sync();
        }

        /// <summary>跳转到指定段。</summary>
        public void JumpToSection(string sectionName)
        {
            int idx = Montage.FindSectionIndex(sectionName);
            if (idx >= 0) JumpToSection(idx);
        }

        /// <summary>跳转到指定段索引。</summary>
        public void JumpToSection(int sectionIndex)
        {
            if (sectionIndex < 0 || sectionIndex >= Montage.Sections.Count) return;
            CurrentSectionIndex = sectionIndex;
            _sectionTime = 0f;

            // 计算该段在蒙太奇中的起始时间
            float sectionStartTime = 0f;
            for (int i = 0; i < sectionIndex; i++)
                sectionStartTime += Montage.Sections[i].LengthSeconds;
            CurrentTime = sectionStartTime;
        }

        /// <summary>设置标准化时间。</summary>
        public void SetNormalizedTime(float normalizedTime)
        {
            float time = normalizedTime * Montage.LengthSeconds;
            CurrentTime = Math.Clamp(time, 0f, Montage.LengthSeconds);
        }

        /// <summary>采样当前帧的蒙太奇姿态。</summary>
        public AnimPoseOutput? SampleCurrentPose()
        {
            if (!IsActive || Skeleton == null) return null;

            var currentSection = GetCurrentSection();
            if (currentSection == null) return null;

            int boneCount = Skeleton.Bones.Count;
            var output = AnimPoseOutput.Create(boneCount);

            // 计算在当前段中的本地时间
            float sectionLocalTime = 0f;
            float sectionStart = 0f;
            for (int i = 0; i < CurrentSectionIndex; i++)
                sectionStart += Montage.Sections[i].LengthSeconds;
            sectionLocalTime = CurrentTime - sectionStart;

            // 采样段中的每个片段
            bool hasAnySegment = false;
            foreach (int segIdx in currentSection.SegmentIndices)
            {
                if (segIdx < 0 || segIdx >= Montage.Segments.Count) continue;
                var seg = Montage.Segments[segIdx];
                if (seg.Clip == null) continue;

                float segLocalTime = sectionLocalTime * seg.PlayRate;
                var pose = seg.Clip.Sample(Skeleton, seg.StartTime + segLocalTime);
                output.CopyFromSkeletonPose(Skeleton, pose);
                hasAnySegment = true;
                break; // 每个段目前只采样第一个片段（简化处理）
            }

            if (!hasAnySegment)
            {
                // 没有片段：输出绑定姿态
                var bindPose = Skeleton.CreateBindPose();
                output.CopyFromSkeletonPose(Skeleton, bindPose);
            }

            return output;
        }

        /// <summary>检查是否使用指定插槽。</summary>
        public bool UsesSlot(string slotName)
        {
            if (_slotWeights.ContainsKey(slotName)) return true;
            // 检查该蒙太奇的片段是否使用了此插槽
            foreach (var seg in Montage.Segments)
                if (seg.SlotName == slotName) return true;
            return false;
        }

        /// <summary>获取指定插槽的混合权重。</summary>
        public float GetSlotWeight(string slotName)
        {
            return _slotWeights.TryGetValue(slotName, out float w) ? w : 0f;
        }
    }
}