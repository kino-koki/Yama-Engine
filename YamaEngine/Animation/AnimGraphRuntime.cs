using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Skinning;

namespace YamaEngine.Animation.RuntimeGraph
{
    // ============================================================================================
    //  AnimGraphRuntime.cs
    //
    //  运行时动画图（Runtime Animation Graph）。
    //
    //  本系统提供一套自包含的运行时动画图求值管线，可与引擎现有 AnimNodeScript / AnimGraphEvaluator
    //  并存或独立使用。复用 AnimPoseOutput / AnimationClip / Skeleton / SkeletonPose 等既有数据类型，
    //  但节点体系（AnimGraphNode）独立设计，避免与既有 AnimNodeBase 体系冲突。
    //
    //  功能要点：
    //    - AnimGraphNode 体系：PoseNode / BlendNode / StateNode / SlotNode / LayerNode
    //    - 图求值器：拓扑排序求值，缓存中间姿势（同帧不重复求值）
    //    - 动画层：叠加层（Additive）/ 替换层（Replace）/ 加权层（Weighted）
    //    - 根运动提取与合成
    //    - 曲线权重驱动（RuntimeAnimCurve -> Bone 权重）
    //    - SyncGroup：多动画同步组（同组动画进度同步）
    //
    //  注：本文件置于子命名空间 YamaEngine.Animation.RuntimeGraph，与既有 AnimGraphSystem /
    //  AnimLayerSystem / AnimRootMotionSystem / AnimCurveSystem 等同名类型隔离，保证可编译。
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  求值上下文
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单帧求值上下文。在整张图求值期间共享，携带骨架、骨骼数、时间、同步组与根运动累加器。
    /// </summary>
    public sealed class EvaluationContext
    {
        /// <summary>目标骨架。</summary>
        public Skeleton Skeleton { get; }

        /// <summary>骨骼数（缓存以避免频繁访问）。</summary>
        public int BoneCount { get; }

        /// <summary>本帧增量时间（秒）。</summary>
        public float DeltaTime { get; set; }

        /// <summary>累计运行时间（秒）。</summary>
        public float CurrentTime { get; set; }

        /// <summary>当前帧 ID，用于节点姿势缓存判定。</summary>
        public int FrameId { get; set; }

        /// <summary>同步组管理器。</summary>
        public AnimSyncGroupManager SyncGroups { get; } = new();

        /// <summary>根运动累加器（各 PoseNode 提取后累加）。</summary>
        public RootMotionAccumulator RootMotion { get; } = new();

        public EvaluationContext(Skeleton skeleton)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            BoneCount = skeleton.Bones.Count;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  节点基类 AnimGraphNode
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 运行时动画图节点基类。所有节点（Pose/Blend/State/Slot/Layer）均继承此类。
    /// 提供：子节点列表、权重、启用开关、本帧姿势缓存与生命周期回调。
    /// </summary>
    public abstract class AnimGraphNode
    {
        private static int s_nextId = 1;

        /// <summary>节点唯一标识（自动分配）。</summary>
        public int Id { get; } = s_nextId++;

        /// <summary>节点名称（调试用）。</summary>
        public string Name { get; set; } = "Node";

        /// <summary>子节点列表。</summary>
        public List<AnimGraphNode> Children { get; } = new();

        /// <summary>父节点（拓扑排序与依赖追踪用）。</summary>
        public AnimGraphNode? Parent { get; set; }

        /// <summary>节点权重（混合时使用）。</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>本帧求值输出（缓存）。</summary>
        public AnimPoseOutput? CachedPose { get; protected set; }

        /// <summary>上次求值的帧 ID。</summary>
        protected int LastEvalFrame = -1;

        /// <summary>初始化（图初始化时调用一次）。</summary>
        public virtual void OnInitialize(Skeleton skeleton) { }

        /// <summary>每帧更新逻辑（在求值前调用）。</summary>
        public virtual void OnUpdate(float deltaTime, EvaluationContext ctx) { }

        /// <summary>求值：生成输出姿势并写入 <see cref="CachedPose"/>。</summary>
        public abstract void OnEvaluate(EvaluationContext ctx);

        /// <summary>重置节点状态。</summary>
        public virtual void OnReset() { LastEvalFrame = -1; }

        /// <summary>
        /// 带缓存的求值入口。同一帧内重复调用直接返回缓存结果。
        /// </summary>
        public AnimPoseOutput? Evaluate(EvaluationContext ctx)
        {
            if (!Enabled) return CachedPose;
            if (LastEvalFrame == ctx.FrameId) return CachedPose;
            OnEvaluate(ctx);
            LastEvalFrame = ctx.FrameId;
            return CachedPose;
        }

        /// <summary>添加子节点并建立父子关系。</summary>
        public T AddChild<T>(T child) where T : AnimGraphNode
        {
            if (child != null && !Children.Contains(child))
            {
                Children.Add(child);
                child.Parent = this;
            }
            return child;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  PoseNode —— 动画序列播放叶子节点
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 姿势节点：播放一个 <see cref="AnimationClip"/>，是动画图的叶子节点。
    /// 支持播放速率、循环、同步组、根运动提取。
    /// </summary>
    public sealed class PoseNode : AnimGraphNode
    {
        /// <summary>动画剪辑引用。</summary>
        public AnimationClip? Clip { get; set; }

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        /// <summary>是否循环。</summary>
        public bool Loop { get; set; } = true;

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime { get; set; }

        /// <summary>同步组名称（非空时由同步组驱动时间）。</summary>
        public string? SyncGroupName { get; set; }

        /// <summary>根骨骼索引（-1 表示使用 0 号骨骼）。用于根运动提取。</summary>
        public int RootBoneIndex { get; set; } = -1;

        /// <summary>是否提取根运动。</summary>
        public bool ExtractRootMotion { get; set; } = true;

        /// <summary>本节点对根运动的贡献权重。</summary>
        public float RootMotionWeight { get; set; } = 1f;

        private float _prevTime;
        private bool _hasPrev;

        public PoseNode() { Name = "Pose"; }

        public PoseNode(AnimationClip clip) : this() { Clip = clip; }

        /// <summary>剪辑长度（秒），无剪辑时返回 0。</summary>
        public float Length => Clip?.LengthSeconds ?? 0f;

        public override void OnInitialize(Skeleton skeleton)
        {
            _prevTime = CurrentTime;
            _hasPrev = false;
        }

        public override void OnUpdate(float deltaTime, EvaluationContext ctx)
        {
            // 同步组节点的时间由 AnimSyncGroup 直接写入，此处不推进。
            if (!string.IsNullOrEmpty(SyncGroupName)) return;
            AdvanceTime(deltaTime);
        }

        private void AdvanceTime(float deltaTime)
        {
            float length = Length;
            if (length <= 0f) { CurrentTime = 0f; return; }
            _prevTime = CurrentTime;
            _hasPrev = true;
            CurrentTime += deltaTime * PlayRate;
            if (Loop)
            {
                CurrentTime %= length;
                if (CurrentTime < 0f) CurrentTime += length;
            }
            else if (CurrentTime > length)
            {
                CurrentTime = length;
            }
        }

        /// <summary>由同步组调用：直接设置当前时间（不改变 _prevTime 推进语义）。</summary>
        internal void SetSyncedTime(float time)
        {
            _prevTime = CurrentTime;
            _hasPrev = true;
            CurrentTime = time;
        }

        public override void OnEvaluate(EvaluationContext ctx)
        {
            CachedPose ??= AnimPoseOutput.Create(ctx.BoneCount);
            var clip = Clip;
            if (clip == null) return;

            float length = clip.LengthSeconds;
            float t = length > 0f ? CurrentTime : 0f;
            var pose = clip.Sample(ctx.Skeleton, t);
            CachedPose.CopyFromSkeletonPose(ctx.Skeleton, pose);
            CachedPose.RootMotionDeltaTranslation = Vector3.Zero;
            CachedPose.RootMotionDeltaRotation = Quaternion.Identity;

            // 根运动提取
            if (ExtractRootMotion)
            {
                var delta = RootMotionExtractor.Extract(clip, ctx.Skeleton, _hasPrev ? _prevTime : t, t, RootBoneIndex < 0 ? 0 : RootBoneIndex);
                ctx.RootMotion.Add(delta, RootMotionWeight * Weight);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  BlendNode —— 多姿势加权混合
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 混合节点：将多个子节点的输出按权重加权混合。
    /// 平移/缩放使用线性插值加权求和，旋转使用顺序球面插值（按归一化权重）。
    /// </summary>
    public sealed class BlendNode : AnimGraphNode
    {
        /// <summary>每个子节点对应的权重（与 <see cref="AnimGraphNode.Children"/> 一一对应）。</summary>
        public List<float> InputWeights { get; } = new();

        public BlendNode() { Name = "Blend"; }

        /// <summary>添加一个带权重的子节点。</summary>
        public void AddInput(AnimGraphNode child, float weight)
        {
            AddChild(child);
            InputWeights.Add(weight);
        }

        public override void OnEvaluate(EvaluationContext ctx)
        {
            CachedPose ??= AnimPoseOutput.Create(ctx.BoneCount);

            // 收集有效子姿势
            var poses = new List<(AnimPoseOutput pose, float w)>();
            float wsum = 0f;
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (!child.Enabled) continue;
                float w = i < InputWeights.Count ? InputWeights[i] : child.Weight;
                if (w <= 0f) continue;
                var p = child.Evaluate(ctx);
                if (p == null) continue;
                poses.Add((p, w));
                wsum += w;
            }

            if (poses.Count == 0) return;
            if (poses.Count == 1 || wsum <= 1e-8f)
            {
                var src = poses[0].pose;
                for (int i = 0; i < CachedPose.BoneCount; i++)
                {
                    CachedPose.Translations[i] = src.Translations[i];
                    CachedPose.Rotations[i] = src.Rotations[i];
                    CachedPose.Scales[i] = src.Scales[i];
                }
                return;
            }

            // 加权求和（平移/缩放）
            var trans = new Vector3[CachedPose.BoneCount];
            var scale = new Vector3[CachedPose.BoneCount];
            var rots = new Quaternion[CachedPose.BoneCount];
            for (int i = 0; i < CachedPose.BoneCount; i++)
            {
                trans[i] = Vector3.Zero;
                scale[i] = Vector3.Zero;
                rots[i] = Quaternion.Identity;
            }

            foreach (var (p, w) in poses)
            {
                float wn = w / wsum;
                for (int i = 0; i < CachedPose.BoneCount; i++)
                {
                    trans[i] += p.Translations[i] * wn;
                    scale[i] += p.Scales[i] * wn;
                }
            }

            // 旋转：按归一化权重顺序 slerp
            for (int i = 0; i < CachedPose.BoneCount; i++)
            {
                Quaternion acc = poses[0].pose.Rotations[i];
                float accW = poses[0].w;
                for (int k = 1; k < poses.Count; k++)
                {
                    float w = poses[k].w;
                    accW += w;
                    float t = w / accW;
                    acc = Quaternion.Slerp(acc, poses[k].pose.Rotations[i], t);
                }
                rots[i] = Quaternion.Normalize(acc);
            }

            for (int i = 0; i < CachedPose.BoneCount; i++)
            {
                CachedPose.Translations[i] = trans[i];
                CachedPose.Scales[i] = scale[i];
                CachedPose.Rotations[i] = rots[i];
            }

            // 曲线混合
            CachedPose.Curves.Clear();
            foreach (var (p, w) in poses)
            {
                float wn = w / wsum;
                foreach (var kv in p.Curves)
                {
                    CachedPose.Curves.TryGetValue(kv.Key, out var cur);
                    CachedPose.Curves[kv.Key] = cur + kv.Value * wn;
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  StateNode —— 简易状态机节点
    // ──────────────────────────────────────────────────────────────

    /// <summary>运行时动画图状态：名称 + 运动节点 + 播放速率。</summary>
    public sealed class RuntimeGraphState
    {
        public string Name { get; set; } = string.Empty;
        public AnimGraphNode Motion { get; set; } = null!;
        public float Speed { get; set; } = 1f;
    }

    /// <summary>状态转换：源 -> 目标 + 条件 + 混合时长。</summary>
    public sealed class RuntimeGraphTransition
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public Func<EvaluationContext, bool> Condition { get; set; } = _ => false;
        public float BlendDuration { get; set; } = 0.2f;
        public bool Triggered { get; set; }
    }

    /// <summary>
    /// 状态机节点：在多个状态间转换，转换时按时长混合上一状态与目标状态。
    /// </summary>
    public sealed class StateNode : AnimGraphNode
    {
        /// <summary>所有状态。</summary>
        public Dictionary<string, RuntimeGraphState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>转换规则。</summary>
        public List<RuntimeGraphTransition> Transitions { get; } = new();

        /// <summary>当前状态名。</summary>
        public string CurrentStateName { get; set; } = string.Empty;

        private string _prevStateName = string.Empty;
        private float _blendElapsed;
        private bool _transitioning;

        public StateNode() { Name = "StateMachine"; }

        public void AddState(RuntimeGraphState state)
        {
            States[state.Name] = state;
            if (States.Count == 1) CurrentStateName = state.Name;
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            foreach (var s in States.Values)
                s.Motion?.OnInitialize(skeleton);
        }

        /// <summary>强制跳转到指定状态（不经过混合）。</summary>
        public void GotoState(string name, bool instant = false)
        {
            if (!States.ContainsKey(name)) return;
            if (instant)
            {
                CurrentStateName = name;
                _transitioning = false;
            }
            else
            {
                _prevStateName = CurrentStateName;
                CurrentStateName = name;
                _blendElapsed = 0f;
                _transitioning = true;
            }
        }

        public override void OnUpdate(float deltaTime, EvaluationContext ctx)
        {
            // 更新当前状态运动
            if (States.TryGetValue(CurrentStateName, out var cur))
                cur.Motion?.OnUpdate(deltaTime * cur.Speed, ctx);

            // 转换混合计时
            if (_transitioning)
            {
                var tr = FindTransition(_prevStateName, CurrentStateName);
                float dur = tr?.BlendDuration ?? 0.2f;
                _blendElapsed += deltaTime;
                if (_blendElapsed >= dur) _transitioning = false;
                else if (States.TryGetValue(_prevStateName, out var prev))
                    prev.Motion?.OnUpdate(deltaTime * prev.Speed, ctx);
            }

            // 检查转换条件
            foreach (var t in Transitions)
            {
                if (t.Triggered) continue;
                if (!string.Equals(t.From, CurrentStateName, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Condition(ctx))
                {
                    t.Triggered = true;
                    GotoState(t.To, false);
                    break;
                }
            }
        }

        private RuntimeGraphTransition? FindTransition(string from, string to)
        {
            foreach (var t in Transitions)
                if (string.Equals(t.From, from, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.To, to, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        public override void OnEvaluate(EvaluationContext ctx)
        {
            CachedPose ??= AnimPoseOutput.Create(ctx.BoneCount);
            if (!States.TryGetValue(CurrentStateName, out var cur)) return;
            var curPose = cur.Motion?.Evaluate(ctx);
            if (curPose == null) return;

            if (!_transitioning)
            {
                CopyPose(CachedPose, curPose);
                return;
            }

            // 混合上一状态与当前状态
            var tr = FindTransition(_prevStateName, CurrentStateName);
            float dur = tr?.BlendDuration ?? 0.2f;
            float alpha = dur > 0f ? Math.Clamp(_blendElapsed / dur, 0f, 1f) : 1f;

            if (States.TryGetValue(_prevStateName, out var prev))
            {
                var prevPose = prev.Motion?.Evaluate(ctx);
                if (prevPose != null)
                {
                    BlendInto(CachedPose, prevPose, curPose, alpha);
                    return;
                }
            }
            CopyPose(CachedPose, curPose);
        }

        internal static void CopyPose(AnimPoseOutput dst, AnimPoseOutput src)
        {
            int n = Math.Min(dst.BoneCount, src.BoneCount);
            for (int i = 0; i < n; i++)
            {
                dst.Translations[i] = src.Translations[i];
                dst.Rotations[i] = src.Rotations[i];
                dst.Scales[i] = src.Scales[i];
            }
        }

        internal static void BlendInto(AnimPoseOutput dst, AnimPoseOutput a, AnimPoseOutput b, float alpha)
        {
            int n = Math.Min(dst.BoneCount, Math.Min(a.BoneCount, b.BoneCount));
            for (int i = 0; i < n; i++)
            {
                dst.Translations[i] = Vector3.Lerp(a.Translations[i], b.Translations[i], alpha);
                dst.Rotations[i] = Quaternion.Slerp(a.Rotations[i], b.Rotations[i], alpha);
                dst.Scales[i] = Vector3.Lerp(a.Scales[i], b.Scales[i], alpha);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  SlotNode —— 插槽节点（蒙太奇/外部姿势覆盖）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 插槽节点：默认输出基础子节点的姿势，可被外部设置的 OverridePose 按权重覆盖。
    /// 用于蒙太奇、上层动画注入等场景。
    /// </summary>
    public sealed class SlotNode : AnimGraphNode
    {
        /// <summary>插槽名称。</summary>
        public string SlotName { get; set; } = "DefaultSlot";

        /// <summary>外部覆盖姿势（由蒙太奇系统设置）。</summary>
        public AnimPoseOutput? OverridePose { get; set; }

        /// <summary>覆盖权重 [0,1]。</summary>
        public float OverrideWeight { get; set; }

        /// <summary>覆盖骨骼掩码（null 表示全身）。</summary>
        public bool[]? OverrideMask { get; set; }

        public SlotNode() { Name = "Slot"; }

        public override void OnEvaluate(EvaluationContext ctx)
        {
            CachedPose ??= AnimPoseOutput.Create(ctx.BoneCount);
            AnimGraphNode? baseChild = Children.Count > 0 ? Children[0] : null;
            var basePose = baseChild?.Evaluate(ctx);

            if (basePose == null && OverridePose == null) return;

            if (OverridePose == null || OverrideWeight <= 0f)
            {
                if (basePose != null) StateNode.CopyPose(CachedPose!, basePose);
                return;
            }
            if (basePose == null)
            {
                StateNode.CopyPose(CachedPose!, OverridePose);
                return;
            }

            float w = Math.Clamp(OverrideWeight, 0f, 1f);
            int n = CachedPose!.BoneCount;
            for (int i = 0; i < n; i++)
            {
                bool apply = OverrideMask == null || i >= OverrideMask.Length || OverrideMask[i];
                float aw = apply ? w : 0f;
                CachedPose.Translations[i] = Vector3.Lerp(basePose.Translations[i], OverridePose.Translations[i], aw);
                CachedPose.Rotations[i] = Quaternion.Slerp(basePose.Rotations[i], OverridePose.Rotations[i], aw);
                CachedPose.Scales[i] = Vector3.Lerp(basePose.Scales[i], OverridePose.Scales[i], aw);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  动画层（AnimLayer）与 LayerNode
    // ──────────────────────────────────────────────────────────────

    /// <summary>动画层混合模式。</summary>
    public enum AnimLayerBlendMode
    {
        /// <summary>替换：用层姿势按权重替换基础姿势（受掩码限制）。</summary>
        Replace,
        /// <summary>叠加：层姿势作为加性偏移叠加到基础姿势。</summary>
        Additive,
        /// <summary>加权：层姿势与基础姿势按权重加权混合（与 Replace 类似，但权重另参与归一）。</summary>
        Weighted
    }

    /// <summary>骨骼掩码：决定层作用于哪些骨骼。</summary>
    public sealed class BoneMask
    {
        /// <summary>每骨骼开关。</summary>
        public bool[] PerBone { get; }

        public BoneMask(int boneCount) { PerBone = new bool[boneCount]; }

        /// <summary>全身掩码（全部为 true）。</summary>
        public static BoneMask FullBody(int boneCount)
        {
            var m = new BoneMask(boneCount);
            for (int i = 0; i < boneCount; i++) m.PerBone[i] = true;
            return m;
        }

        /// <summary>空掩码（全部为 false）。</summary>
        public static BoneMask Empty(int boneCount) => new(boneCount);

        /// <summary>从骨骼名称集合构造掩码（含子骨骼）。</summary>
        public static BoneMask FromNames(Skeleton skeleton, IEnumerable<string> boneNames, bool includeChildren = true)
        {
            var m = new BoneMask(skeleton.Bones.Count);
            var set = new HashSet<string>(boneNames, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                if (set.Contains(skeleton.Bones[i].Name))
                {
                    m.PerBone[i] = true;
                    if (includeChildren) MarkChildren(skeleton, i, m);
                }
            }
            return m;
        }

        private static void MarkChildren(Skeleton skeleton, int parent, BoneMask m)
        {
            for (int i = parent + 1; i < skeleton.Bones.Count; i++)
                if (skeleton.Bones[i].ParentIndex == parent)
                {
                    m.PerBone[i] = true;
                    MarkChildren(skeleton, i, m);
                }
        }
    }

    /// <summary>动画层：源节点 + 掩码 + 权重 + 模式。</summary>
    public sealed class AnimLayer
    {
        public AnimGraphNode? Source { get; set; }
        public BoneMask? Mask { get; set; }
        public float Weight { get; set; } = 1f;
        public AnimLayerBlendMode Mode { get; set; } = AnimLayerBlendMode.Replace;
    }

    /// <summary>
    /// 分层混合节点：子节点 0 为基础姿势，<see cref="Layers"/> 为叠加其上的各动画层。
    /// 支持 Replace / Additive / Weighted 三种模式，并按骨骼掩码限制作用范围。
    /// </summary>
    public sealed class LayerNode : AnimGraphNode
    {
        /// <summary>动画层列表。</summary>
        public List<AnimLayer> Layers { get; } = new();

        public LayerNode() { Name = "LayeredBlend"; }

        public override void OnEvaluate(EvaluationContext ctx)
        {
            CachedPose ??= AnimPoseOutput.Create(ctx.BoneCount);
            AnimGraphNode? baseChild = Children.Count > 0 ? Children[0] : null;
            var basePose = baseChild?.Evaluate(ctx);
            if (basePose == null) return;
            StateNode.CopyPose(CachedPose!, basePose);

            foreach (var layer in Layers)
            {
                if (layer.Source == null || layer.Weight <= 0f) continue;
                var layerPose = layer.Source.Evaluate(ctx);
                if (layerPose == null) continue;
                ApplyLayer(CachedPose!, layerPose, layer, ctx.BoneCount);
            }
        }

        private static void ApplyLayer(AnimPoseOutput target, AnimPoseOutput layer, AnimLayer info, int boneCount)
        {
            int n = Math.Min(boneCount, Math.Min(target.BoneCount, layer.BoneCount));
            float w = Math.Clamp(info.Weight, 0f, 1f);
            for (int i = 0; i < n; i++)
            {
                bool apply = info.Mask == null || i >= info.Mask.PerBone.Length || info.Mask.PerBone[i];
                if (!apply) continue;

                switch (info.Mode)
                {
                    case AnimLayerBlendMode.Replace:
                    case AnimLayerBlendMode.Weighted:
                        target.Translations[i] = Vector3.Lerp(target.Translations[i], layer.Translations[i], w);
                        target.Rotations[i] = Quaternion.Slerp(target.Rotations[i], layer.Rotations[i], w);
                        target.Scales[i] = Vector3.Lerp(target.Scales[i], layer.Scales[i], w);
                        break;
                    case AnimLayerBlendMode.Additive:
                        // 加性：平移/缩放叠加，旋转按权重向层旋转 slerp 后相乘
                        target.Translations[i] += layer.Translations[i] * w;
                        target.Scales[i] *= Vector3.Lerp(Vector3.One, layer.Scales[i], w);
                        var addRot = Quaternion.Slerp(Quaternion.Identity, layer.Rotations[i], w);
                        target.Rotations[i] = Quaternion.Normalize(target.Rotations[i] * addRot);
                        break;
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  根运动提取与合成
    // ──────────────────────────────────────────────────────────────

    /// <summary>根运动增量（单帧）。</summary>
    public struct RootMotionDelta
    {
        public Vector3 Translation;
        public Quaternion Rotation;

        public static RootMotionDelta Identity => new()
        {
            Translation = Vector3.Zero,
            Rotation = Quaternion.Identity
        };

        public static RootMotionDelta operator *(RootMotionDelta d, float s) => new()
        {
            Translation = d.Translation * s,
            Rotation = Quaternion.Slerp(Quaternion.Identity, d.Rotation, s)
        };
    }

    /// <summary>
    /// 根运动提取器：从动画剪辑在两个时间点采样根骨骼，计算位移与旋转增量。
    /// 根骨骼的局部平移偏移 / 旋转变化即为根运动（根骨骼无父骨骼，世界=局部）。
    /// </summary>
    public static class RootMotionExtractor
    {
        /// <summary>提取 [prevTime, curTime] 区间内根骨骼的运动增量。</summary>
        public static RootMotionDelta Extract(AnimationClip clip, Skeleton skeleton, float prevTime, float curTime, int rootBoneIndex)
        {
            if (clip == null || skeleton == null) return RootMotionDelta.Identity;
            if (rootBoneIndex < 0 || rootBoneIndex >= skeleton.Bones.Count) rootBoneIndex = 0;

            var prevPose = clip.Sample(skeleton, prevTime);
            var curPose = clip.Sample(skeleton, curTime);

            var dT = curPose.GetTranslationOffset(rootBoneIndex) - prevPose.GetTranslationOffset(rootBoneIndex);
            var prevR = prevPose.GetRotation(rootBoneIndex);
            var curR = curPose.GetRotation(rootBoneIndex);
            var dR = Quaternion.Normalize(curR * Quaternion.Inverse(prevR));

            return new RootMotionDelta { Translation = dT, Rotation = dR };
        }
    }

    /// <summary>根运动累加器：按权重累加多个来源的根运动增量并合成最终结果。</summary>
    public sealed class RootMotionAccumulator
    {
        private Vector3 _translation;
        private Quaternion _rotation = Quaternion.Identity;
        private float _weightSum;

        /// <summary>累加一个来源的根运动增量。</summary>
        public void Add(RootMotionDelta delta, float weight)
        {
            if (weight <= 0f) return;
            _translation += delta.Translation * weight;
            // 旋转：按权重 slerp 累加
            _rotation = Quaternion.Slerp(_rotation, delta.Rotation, weight / (_weightSum + weight));
            _weightSum += weight;
        }

        /// <summary>当前合成的根运动增量。</summary>
        public RootMotionDelta Result => new()
        {
            Translation = _translation,
            Rotation = _rotation
        };

        /// <summary>重置（每帧求值前调用）。</summary>
        public void Reset()
        {
            _translation = Vector3.Zero;
            _rotation = Quaternion.Identity;
            _weightSum = 0f;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  曲线权重驱动（RuntimeAnimCurve -> Bone 权重）
    // ──────────────────────────────────────────────────────────────

    /// <summary>简单的浮点动画曲线（线性插值，键按时间升序）。</summary>
    public sealed class RuntimeAnimCurve
    {
        /// <summary>键列表（时间, 值）。</summary>
        public List<(float Time, float Value)> Keys { get; } = new();

        /// <summary>采样前 / 后的取值策略。</summary>
        public bool ClampExtrapolate { get; set; } = true;

        public void AddKey(float time, float value)
        {
            Keys.Add((time, value));
            Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        /// <summary>在时间 t 处求值。</summary>
        public float Evaluate(float t)
        {
            if (Keys.Count == 0) return 0f;
            if (Keys.Count == 1) return Keys[0].Value;
            if (t <= Keys[0].Time) return ClampExtrapolate ? Keys[0].Value : Extrapolate(Keys[0], Keys[1], t);
            if (t >= Keys[^1].Time) return ClampExtrapolate ? Keys[^1].Value : Extrapolate(Keys[^2], Keys[^1], t);

            for (int i = 0; i < Keys.Count - 1; i++)
            {
                if (t >= Keys[i].Time && t <= Keys[i + 1].Time)
                {
                    float span = Keys[i + 1].Time - Keys[i].Time;
                    float k = span > 1e-8f ? (t - Keys[i].Time) / span : 0f;
                    return Keys[i].Value + (Keys[i + 1].Value - Keys[i].Value) * k;
                }
            }
            return Keys[^1].Value;
        }

        private static float Extrapolate((float Time, float Value) a, (float Time, float Value) b, float t)
        {
            float span = b.Time - a.Time;
            if (MathF.Abs(span) < 1e-8f) return b.Value;
            return a.Value + (b.Value - a.Value) * ((t - a.Time) / span);
        }
    }

    /// <summary>
    /// 曲线骨骼权重驱动器：将一条 <see cref="RuntimeAnimCurve"/> 的求值结果映射为每骨骼权重。
    /// 可用于驱动 IK / 层 / 变形目标的影响权重。
    /// </summary>
    public sealed class CurveBoneWeightDriver
    {
        /// <summary>驱动曲线。</summary>
        public RuntimeAnimCurve Curve { get; set; } = new();

        /// <summary>曲线输入值（时间或任意参数）。</summary>
        public float InputValue { get; set; }

        /// <summary>作用骨骼掩码（null 表示全身）。</summary>
        public BoneMask? TargetBones { get; set; }

        /// <summary>权重下限（曲线值为 0 时的权重）。</summary>
        public float MinWeight { get; set; } = 0f;

        /// <summary>权重上限（曲线值为 1 时的权重）。</summary>
        public float MaxWeight { get; set; } = 1f;

        /// <summary>曲线值缩放（默认曲线输出 [0,1]）。</summary>
        public float CurveScale { get; set; } = 1f;

        /// <summary>计算每骨骼权重数组。</summary>
        public float[] ComputeBoneWeights(Skeleton skeleton)
        {
            int n = skeleton.Bones.Count;
            var arr = new float[n];
            float raw = Curve.Evaluate(InputValue) * CurveScale;
            float v = Math.Clamp(raw, 0f, 1f);
            float w = MinWeight + (MaxWeight - MinWeight) * v;
            for (int i = 0; i < n; i++)
            {
                bool on = TargetBones == null || i >= TargetBones.PerBone.Length || TargetBones.PerBone[i];
                arr[i] = on ? w : 0f;
            }
            return arr;
        }

        /// <summary>当前标量权重（曲线求值后映射到 [MinWeight, MaxWeight]）。</summary>
        public float CurrentScalarWeight
        {
            get
            {
                float raw = Curve.Evaluate(InputValue) * CurveScale;
                float v = Math.Clamp(raw, 0f, 1f);
                return MinWeight + (MaxWeight - MinWeight) * v;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  SyncGroup —— 多动画同步组
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画同步组：同组内的 <see cref="PoseNode"/> 共享同一归一化进度（0..1），
    /// 使不同长度的动画在相位上保持同步（例如上下半身共用一个周期）。
    /// </summary>
    public sealed class AnimSyncGroup
    {
        /// <summary>组名。</summary>
        public string Name { get; }

        /// <summary>组成员。</summary>
        public List<PoseNode> Members { get; } = new();

        /// <summary>组长（其长度作为同步基准；为 null 时使用最长成员）。</summary>
        public PoseNode? Leader { get; set; }

        /// <summary>当前归一化进度 [0,1)。</summary>
        public float NormalizedTime { get; set; }

        /// <summary>同步组播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        public AnimSyncGroup(string name) { Name = name; }

        /// <summary>添加成员。</summary>
        public void AddMember(PoseNode node)
        {
            if (node != null && !Members.Contains(node))
            {
                Members.Add(node);
                node.SyncGroupName = Name;
            }
        }

        /// <summary>设置组长。</summary>
        public void SetLeader(PoseNode node)
        {
            if (node != null && Members.Contains(node)) Leader = node;
        }

        /// <summary>基准长度（秒）。</summary>
        public float GroupLength
        {
            get
            {
                if (Leader != null) return Leader.Length;
                float max = 0f;
                foreach (var m in Members) max = MathF.Max(max, m.Length);
                return max;
            }
        }

        /// <summary>每帧推进：更新归一化进度并写入各成员的当前时间。</summary>
        public void Tick(float deltaTime)
        {
            if (Members.Count == 0) return;
            float len = GroupLength;

            if (Leader != null)
            {
                // 以组长当前时间为准
                float l = Leader.Length;
                NormalizedTime = l > 0f ? (Leader.CurrentTime / l) % 1f : 0f;
                if (NormalizedTime < 0f) NormalizedTime += 1f;
            }
            else
            {
                if (len <= 0f) { NormalizedTime = 0f; return; }
                NormalizedTime += (deltaTime * PlayRate) / len;
                NormalizedTime %= 1f;
                if (NormalizedTime < 0f) NormalizedTime += 1f;
            }

            // 将相位写入非组长成员
            foreach (var m in Members)
            {
                if (ReferenceEquals(m, Leader)) continue;
                float ml = m.Length;
                if (ml <= 0f) continue;
                float t = NormalizedTime * ml;
                m.SetSyncedTime(t);
            }
        }

        /// <summary>跳转到指定归一化时间（所有成员）。</summary>
        public void SetNormalizedTime(float t)
        {
            t %= 1f;
            if (t < 0f) t += 1f;
            NormalizedTime = t;
            foreach (var m in Members)
            {
                float ml = m.Length;
                if (ml <= 0f) continue;
                m.SetSyncedTime(t * ml);
            }
        }
    }

    /// <summary>同步组管理器。</summary>
    public sealed class AnimSyncGroupManager
    {
        private readonly Dictionary<string, AnimSyncGroup> _groups = new(StringComparer.OrdinalIgnoreCase);

        public AnimSyncGroup GetOrCreate(string name)
        {
            if (!_groups.TryGetValue(name, out var g))
            {
                g = new AnimSyncGroup(name);
                _groups[name] = g;
            }
            return g;
        }

        public AnimSyncGroup? Find(string name) =>
            _groups.TryGetValue(name, out var g) ? g : null;

        /// <summary>推进所有同步组。</summary>
        public void TickAll(float deltaTime)
        {
            foreach (var g in _groups.Values) g.Tick(deltaTime);
        }

        public IEnumerable<AnimSyncGroup> All => _groups.Values;
    }

    // ──────────────────────────────────────────────────────────────
    //  运行时动画图与求值器
    // ──────────────────────────────────────────────────────────────

    /// <summary>运行时动画图：持有根节点与全部节点。</summary>
    public sealed class RuntimeAnimGraph
    {
        /// <summary>根节点。</summary>
        public AnimGraphNode? Root { get; set; }

        /// <summary>全部节点（含根与所有子节点）。</summary>
        public List<AnimGraphNode> AllNodes { get; } = new();

        /// <summary>添加节点。</summary>
        public void AddNode(AnimGraphNode node)
        {
            if (node != null && !AllNodes.Contains(node)) AllNodes.Add(node);
        }

        /// <summary>递归收集以 root 为根的所有节点到 AllNodes。</summary>
        public void Collect(AnimGraphNode? root)
        {
            Root = root;
            AllNodes.Clear();
            if (root == null) return;
            CollectRecursive(root);
        }

        private void CollectRecursive(AnimGraphNode node)
        {
            AddNode(node);
            foreach (var c in node.Children) CollectRecursive(c);
        }

        /// <summary>初始化所有节点。</summary>
        public void Initialize(Skeleton skeleton)
        {
            foreach (var n in AllNodes) n.OnInitialize(skeleton);
        }

        /// <summary>重置所有节点。</summary>
        public void ResetAll()
        {
            foreach (var n in AllNodes) n.OnReset();
        }
    }

    /// <summary>
    /// 运行时图求值器：对图进行拓扑排序，按依赖顺序（叶子->根）求值，
    /// 同帧内节点结果缓存（<see cref="AnimGraphNode.Evaluate"/> 保证）。
    /// </summary>
    public sealed class RuntimeGraphEvaluator
    {
        private readonly Skeleton _skeleton;
        private readonly RuntimeAnimGraph _graph;
        private readonly EvaluationContext _ctx;
        private List<AnimGraphNode> _evalOrder = new();
        private int _frameId;

        public RuntimeAnimGraph Graph => _graph;
        public EvaluationContext Context => _ctx;

        public RuntimeGraphEvaluator(Skeleton skeleton, RuntimeAnimGraph graph)
        {
            _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _ctx = new EvaluationContext(_skeleton);
            Rebuild();
        }

        /// <summary>重建拓扑求值顺序（图结构变化后调用）。</summary>
        public void Rebuild()
        {
            _evalOrder = TopologicalSort(_graph.AllNodes);
        }

        /// <summary>每帧求值入口，返回根节点的输出姿势。</summary>
        public AnimPoseOutput? Evaluate(float deltaTime)
        {
            deltaTime = MathF.Max(0f, deltaTime);
            _frameId++;
            _ctx.FrameId = _frameId;
            _ctx.DeltaTime = deltaTime;
            _ctx.CurrentTime += deltaTime;
            _ctx.RootMotion.Reset();

            // 1. 同步组推进
            _ctx.SyncGroups.TickAll(deltaTime);

            // 2. 节点更新
            foreach (var node in _graph.AllNodes)
                node.OnUpdate(deltaTime, _ctx);

            // 3. 拓扑顺序求值（叶子优先）
            foreach (var node in _evalOrder)
                node.Evaluate(_ctx);

            return _graph.Root?.CachedPose;
        }

        /// <summary>当前帧合成的根运动增量。</summary>
        public RootMotionDelta GetRootMotion() => _ctx.RootMotion.Result;

        // ── 拓扑排序（Kahn 算法，叶子在前，根在后）──────────────
        private static List<AnimGraphNode> TopologicalSort(List<AnimGraphNode> nodes)
        {
            var order = new List<AnimGraphNode>(nodes.Count);
            if (nodes.Count == 0) return order;

            // 每个节点尚未处理的子节点数
            var remaining = new Dictionary<int, int>();
            var dependents = new Dictionary<int, List<AnimGraphNode>>();
            foreach (var n in nodes)
            {
                remaining[n.Id] = n.Children.Count;
                dependents[n.Id] = new List<AnimGraphNode>();
            }
            foreach (var n in nodes)
                foreach (var c in n.Children)
                    if (dependents.TryGetValue(c.Id, out var list))
                        list.Add(n);

            var queue = new Queue<AnimGraphNode>();
            foreach (var n in nodes)
                if (remaining[n.Id] == 0) queue.Enqueue(n);

            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                order.Add(n);
                if (!dependents.TryGetValue(n.Id, out var deps)) continue;
                foreach (var p in deps)
                {
                    if (--remaining[p.Id] == 0) queue.Enqueue(p);
                }
            }

            // 处理可能的环（不应发生）：把剩余节点追加
            if (order.Count < nodes.Count)
            {
                var placed = new HashSet<int>();
                foreach (var n in order) placed.Add(n.Id);
                foreach (var n in nodes)
                    if (!placed.Contains(n.Id)) order.Add(n);
            }

            return order;
        }
    }
}
