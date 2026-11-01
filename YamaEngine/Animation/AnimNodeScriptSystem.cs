using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Skinning;

namespace YamaEngine.Animation
{
    // ============================================================================================
    //  AnimNodeScriptSystem.cs
    //
    //  动画蓝图系统（Animation NodeScript System）。
    //
    //  本系统提供类似 Unreal Engine Animation NodeScript 的完整动画蓝图管线：
    //    - AnimNodeScript       ：蓝图资产，包含 AnimGraph（动画图表）、EventGraph（事件图表）
    //                           和暴露的变量定义。
    //    - AnimInstance        ：运行时实例，每帧执行 NativeUpdateAnimation() ，
    //                           驱动变量评估，最终 EvaluateAnimation() 输出最终姿态。
    //    - AnimNodeBase        ：动画节点基类，定义 InputPose / OutputPose 与生命周期。
    //
    //  整个系统与现有的 AnimatorController / AnimationStateMachine 兼容，
    //  可作为高级动画管线替代或增强。
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  蓝图变量定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>蓝图变量类型。</summary>
    public enum AnimNodeScriptVariableType
    {
        Float,
        Int,
        Bool,
        Vector3,
        BoneReference,
        Transform,
    }

    /// <summary>蓝图变量定义，包含名称/类型/默认值。</summary>
    public sealed class AnimNodeScriptVariable
    {
        public string Name { get; set; } = string.Empty;
        public AnimNodeScriptVariableType Type { get; set; }

        // 值存储
        public float FloatValue { get; set; }
        public int IntValue { get; set; }
        public bool BoolValue { get; set; }
        public Vector3 Vector3Value { get; set; }
        /// <summary>骨骼引用（骨骼名称 -> 运行时解析为索引）。</summary>
        public string BoneName { get; set; } = string.Empty;
        public int BoneIndex { get; set; } = -1;

        public AnimNodeScriptVariable() { }

        public AnimNodeScriptVariable(string name, AnimNodeScriptVariableType type)
        {
            Name = name;
            Type = type;
        }

        /// <summary>创建变量的深拷贝。</summary>
        public AnimNodeScriptVariable Clone()
        {
            return new AnimNodeScriptVariable
            {
                Name = Name,
                Type = Type,
                FloatValue = FloatValue,
                IntValue = IntValue,
                BoolValue = BoolValue,
                Vector3Value = Vector3Value,
                BoneName = BoneName,
                BoneIndex = BoneIndex,
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  动画节点基类
    // ──────────────────────────────────────────────────────────────

    /// <summary>动画节点评估输出，包含局部空间骨骼变换数组。</summary>
    public sealed class AnimPoseOutput
    {
        /// <summary>骨骼数。</summary>
        public int BoneCount;

        /// <summary>局部空间平移偏移（相对于绑定姿态）。</summary>
        public Vector3[] Translations;

        /// <summary>局部空间旋转。</summary>
        public Quaternion[] Rotations;

        /// <summary>局部空间缩放偏移。</summary>
        public Vector3[] Scales;

        /// <summary>曲线值（名称到浮点值的映射，用于 MorphTarget/材质参数）。</summary>
        public Dictionary<string, float> Curves = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>根骨骼运动增量（delta 位移 + 旋转）。</summary>
        public Vector3 RootMotionDeltaTranslation;
        public Quaternion RootMotionDeltaRotation = Quaternion.Identity;

        /// <summary>构建指定骨骼数的空输出。</summary>
        public static AnimPoseOutput Create(int boneCount)
        {
            var o = new AnimPoseOutput
            {
                BoneCount = boneCount,
                Translations = new Vector3[boneCount],
                Rotations = new Quaternion[boneCount],
                Scales = new Vector3[boneCount],
            };
            for (int i = 0; i < boneCount; i++)
            {
                o.Rotations[i] = Quaternion.Identity;
                o.Scales[i] = Vector3.One;
            }
            return o;
        }

        /// <summary>从 SkeletonPose 复制数据。</summary>
        public void CopyFromSkeletonPose(Skeleton skeleton, SkeletonPose pose)
        {
            int count = Math.Min(BoneCount, skeleton.Bones.Count);
            for (int i = 0; i < count; i++)
            {
                Translations[i] = pose.GetTranslationOffset(i);
                Rotations[i] = pose.GetRotation(i);
                Scales[i] = pose.GetScaleOffset(i);
            }
        }

        /// <summary>兼容重载：从以 object 形式返回的姿势复制（混合空间占位返回类型）。</summary>
        public void CopyFromSkeletonPose(Skeleton skeleton, object? pose)
        {
            if (pose is SkeletonPose sp)
            {
                CopyFromSkeletonPose(skeleton, sp);
                return;
            }
            int count = Math.Min(BoneCount, skeleton?.Bones?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                Translations[i] = Vector3.Zero;
                Rotations[i] = Quaternion.Identity;
                Scales[i] = Vector3.One;
            }
        }

        /// <summary>写入 SkeletonPose。</summary>
        public void WriteToSkeletonPose(Skeleton skeleton, SkeletonPose pose)
        {
            int count = Math.Min(BoneCount, skeleton.Bones.Count);
            for (int i = 0; i < count; i++)
            {
                pose.SetTranslationOffset(i, Translations[i]);
                pose.SetRotation(i, Rotations[i]);
                pose.SetScaleOffset(i, Scales[i]);
            }
        }

        /// <summary>深拷贝。</summary>
        public AnimPoseOutput Clone()
        {
            var o = new AnimPoseOutput
            {
                BoneCount = BoneCount,
                Translations = (Vector3[])Translations.Clone(),
                Rotations = (Quaternion[])Rotations.Clone(),
                Scales = (Vector3[])Scales.Clone(),
                RootMotionDeltaTranslation = RootMotionDeltaTranslation,
                RootMotionDeltaRotation = RootMotionDeltaRotation,
            };
            foreach (var kv in Curves)
                o.Curves[kv.Key] = kv.Value;
            return o;
        }
    }

    /// <summary>
    /// 动画节点基类。所有动画图的节点（状态机、混合空间、姿势节点等）都继承此类。
    /// 类似 UE4/UE5 的 FAnimNode_Base。
    /// </summary>
    public abstract class AnimNodeBase
    {
        /// <summary>节点名称（调试用）。</summary>
        public string NodeName { get; set; } = "AnimNode";

        /// <summary>节点的 LOD 阈值。当骨骼网格体的 LOD 级别大于此值时跳过评估。</summary>
        public int LODThreshold { get; set; } = -1;

        /// <summary>当前 LOD 级别（由评估器设置）。</summary>
        public int CurrentLOD { get; set; }

        /// <summary>是否已通过 LOD 检查。</summary>
        public bool LODPass => LODThreshold < 0 || CurrentLOD <= LODThreshold;

        /// <summary>输入连接列表（子节点）。</summary>
        public List<AnimNodeBase> InputLinks { get; } = new();

        /// <summary>评估输出。</summary>
        public AnimPoseOutput OutputPose { get; set; }

        /// <summary>惰性评估标记。</summary>
        internal bool _evaluatedThisFrame;

        /// <summary>初始化节点。</summary>
        public virtual void OnInitialize(Skeleton skeleton) { }

        /// <summary>每帧更新逻辑。</summary>
        public virtual void OnUpdate(float deltaTime) { }

        /// <summary>评估节点，生成输出姿态。</summary>
        public abstract void OnEvaluate(AnimPoseOutput output);

        /// <summary>重置节点状态（用于回放/重新初始化）。</summary>
        public virtual void OnReset() { }

        /// <summary>添加输入连接。</summary>
        public void AddInputLink(AnimNodeBase child)
        {
            if (child != null && !InputLinks.Contains(child))
                InputLinks.Add(child);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  动画图表（AnimGraph）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画图表。包含动画节点树（状态机 + 混合空间 + 姿势混合链）以及事件图表引用。
    /// AnimGraph 是 AnimNodeScript 的核心资产内容。
    /// </summary>
    public sealed class AnimGraph
    {
        /// <summary>根节点。通常是 AnimStateMachineNode 或 AnimPoseBlendNode。</summary>
        public AnimNodeBase RootNode { get; set; }

        /// <summary>所有节点列表（用于拓扑排序和生命周期管理）。</summary>
        public List<AnimNodeBase> AllNodes { get; } = new();

        /// <summary>添加节点并注册到图。</summary>
        public void AddNode(AnimNodeBase node)
        {
            if (node != null && !AllNodes.Contains(node))
                AllNodes.Add(node);
        }

        /// <summary>初始化所有节点。</summary>
        public void Initialize(Skeleton skeleton)
        {
            foreach (var node in AllNodes)
                node.OnInitialize(skeleton);
        }

        /// <summary>重置所有节点。</summary>
        public void ResetAll()
        {
            foreach (var node in AllNodes)
                node.OnReset();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  动画蓝图资产（AnimNodeScript）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画蓝图资产。类似 UE5 的 UAnimNodeScript。
    /// 包含动画图表（AnimGraph）、事件图表（EventGraph/C# 脚本化逻辑）和暴露的变量。
    /// </summary>
    public sealed class AnimNodeScript
    {
        /// <summary>蓝图名称。</summary>
        public string Name { get; set; } = "AnimNodeScript";

        /// <summary>动画图表（姿势评估图）。</summary>
        public AnimGraph AnimGraph { get; set; } = new();

        /// <summary>事件图表中的脚本动作列表（序列化委托）。</summary>
        public List<AnimNodeScriptEventAction> EventGraphActions { get; set; } = new();

        /// <summary>蓝图中暴露的参数定义。</summary>
        public List<AnimNodeScriptVariable> Variables { get; set; } = new();

        /// <summary>目标骨架的 GUID（用于资产验证）。</summary>
        public string TargetSkeletonGuid { get; set; } = string.Empty;

        /// <summary>添加变量。</summary>
        public AnimNodeScriptVariable AddVariable(string name, AnimNodeScriptVariableType type, object? defaultValue = null)
        {
            var v = new AnimNodeScriptVariable(name, type);
            if (defaultValue != null)
            {
                switch (type)
                {
                    case AnimNodeScriptVariableType.Float: v.FloatValue = (float)defaultValue; break;
                    case AnimNodeScriptVariableType.Int: v.IntValue = (int)defaultValue; break;
                    case AnimNodeScriptVariableType.Bool: v.BoolValue = (bool)defaultValue; break;
                    case AnimNodeScriptVariableType.Vector3: v.Vector3Value = (Vector3)defaultValue; break;
                    case AnimNodeScriptVariableType.BoneReference: v.BoneName = defaultValue.ToString() ?? string.Empty; break;
                }
            }
            Variables.Add(v);
            return v;
        }

        /// <summary>创建该动画蓝图的一个运行时实例。</summary>
        public AnimInstance CreateInstance(Skeleton skeleton)
        {
            var instance = new AnimInstance(this, skeleton);
            instance.NativeInitializeAnimation();
            return instance;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  事件图表动作
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 事件图表中的单个动作。可序列化为函数名+参数字典，
    /// 运行时由 AnimInstance 执行。
    /// </summary>
    public sealed class AnimNodeScriptEventAction
    {
        /// <summary>动作名称/函数签名。</summary>
        public string ActionName { get; set; } = string.Empty;

        /// <summary>参数列表（字符串键值对）。</summary>
        public Dictionary<string, string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>执行此动作。</summary>
        public void Execute(AnimInstance instance)
        {
            // 由子类重写或通过委托绑定
            OnExecute?.Invoke(instance, this);
        }

        /// <summary>执行回调。外部可注册此委托实现自定义脚本逻辑。</summary>
        internal Action<AnimInstance, AnimNodeScriptEventAction>? OnExecute;
    }

    // ──────────────────────────────────────────────────────────────
    //  运行时动画实例（AnimInstance）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 运行时动画实例。类似 UE5 的 UAnimInstance。
    ///
    /// 每帧流程：
    ///   1. NativeUpdateAnimation(dt) - 蓝图事件图更新（C# 脚本逻辑）
    ///   2. EvaluateAnimation(dt)     - 评估动画图表，输出最终姿态
    ///   3. 外部通过 CurrentPose 获取结果
    /// </summary>
    public class AnimInstance
    {
        /// <summary>所属动画蓝图资产。</summary>
        public AnimNodeScript NodeScript { get; }

        /// <summary>目标骨架。</summary>
        public Skeleton Skeleton { get; }

        /// <summary>骨架网格组件引用（外部设置）。</summary>
        public SkeletalMeshComponent? SkeletalMeshComponent { get; set; }

        /// <summary>拥有者 Pawn/Entity（外部设置，弱引用避免 GC 问题）。</summary>
        public WeakReference<object>? OwnerRef { get; set; }

        /// <summary>蓝图变量运行时值存储（名称到值）。</summary>
        public Dictionary<string, AnimNodeScriptVariable> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前评估出的局部空间姿态。</summary>
        public SkeletonPose CurrentPose { get; private set; }

        /// <summary>当前帧评估输出的根运动增量。</summary>
        public Vector3 RootMotionDeltaTranslation { get; private set; }
        public Quaternion RootMotionDeltaRotation { get; private set; } = Quaternion.Identity;

        /// <summary>蒙太奇实例列表。</summary>
        internal List<MontageInstance> _montageInstances = new();

        /// <summary>动画图表评估器。</summary>
        internal AnimGraphEvaluator _evaluator;

        /// <summary>上一帧的根骨骼世界位置/旋转（用于根运动提取）。</summary>
        private Vector3 _lastRootPos;
        private Quaternion _lastRootRot = Quaternion.Identity;
        private bool _hasLastRoot;

        /// <summary>初始化回调钩子。（子类可重写）</summary>
        public AnimInstance(AnimNodeScript nodescript, Skeleton skeleton)
        {
            NodeScript = nodescript ?? throw new ArgumentNullException(nameof(nodescript));
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            CurrentPose = skeleton.CreateBindPose();

            // 复制蓝图变量定义到运行时
            foreach (var v in nodescript.Variables)
                Variables[v.Name] = v.Clone();

            _evaluator = new AnimGraphEvaluator(skeleton, nodescript.AnimGraph);
        }

        /// <summary>初始化时调用。子类可在此钩子中设置初始变量值。</summary>
        public virtual void NativeInitializeAnimation()
        {
            // 事件图表初始化动作
            foreach (var action in NodeScript.EventGraphActions)
                action.Execute(this);
        }

        /// <summary>每帧更新（主线程）。由外部每帧调用。</summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        public virtual void NativeUpdateAnimation(float deltaTime)
        {
            // 事件图表更新动作
            foreach (var action in NodeScript.EventGraphActions)
                action.Execute(this);
        }

        /// <summary>工作线程安全更新（可选）。</summary>
        public virtual void NativeThreadSafeUpdateAnimation() { }

        /// <summary>评估动画图，输出最终姿态。由外部每帧调用。</summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        public virtual void EvaluateAnimation(float deltaTime)
        {
            deltaTime = MathF.Max(0f, deltaTime);

            // 1. 更新蒙太奇
            UpdateMontages(deltaTime);

            // 2. 评估动画图
            var output = _evaluator.Evaluate(deltaTime);

            // 3. 写入 CurrentPose
            output.WriteToSkeletonPose(Skeleton, CurrentPose);

            // 4. 提取根运动
            ExtractRootMotion();
        }

        // ──────────────────────────────────────────────
        //  蒙太奇控制
        // ──────────────────────────────────────────────

        /// <summary>播放蒙太奇。</summary>
        public MontageInstance? Montage_Play(AnimMontage montage, float blendInTime = -1f, float playRate = 1f)
        {
            if (montage == null) return null;
            var inst = new MontageInstance(montage, Skeleton)
            {
                PlayRate = playRate,
                BlendInTime = blendInTime >= 0f ? blendInTime : montage.BlendInTime,
                BlendOutTime = montage.BlendOutTime,
            };
            inst.StartBlendIn();
            _montageInstances.Add(inst);
            return inst;
        }

        /// <summary>停止蒙太奇。</summary>
        public void Montage_Stop(MontageInstance instance, float blendOutTime = -1f)
        {
            if (instance == null) return;
            if (blendOutTime >= 0f)
                instance.BlendOutTime = blendOutTime;
            instance.StartBlendOut();
        }

        /// <summary>跳转到蒙太奇的指定段。</summary>
        public void Montage_JumpToSection(MontageInstance instance, string sectionName)
        {
            if (instance == null || string.IsNullOrEmpty(sectionName)) return;
            instance.JumpToSection(sectionName);
        }

        /// <summary>停止所有蒙太奇。</summary>
        public void Montage_StopAll(float blendOutTime = 0.25f)
        {
            foreach (var inst in _montageInstances)
                inst.StartBlendOut(blendOutTime);
        }

        // ──────────────────────────────────────────────
        //  变量访问
        // ──────────────────────────────────────────────

        public float GetFloat(string name) =>
            Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Float ? v.FloatValue : 0f;

        public void SetFloat(string name, float value)
        {
            if (Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Float)
                v.FloatValue = value;
        }

        public int GetInt(string name) =>
            Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Int ? v.IntValue : 0;

        public void SetInt(string name, int value)
        {
            if (Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Int)
                v.IntValue = value;
        }

        public bool GetBool(string name) =>
            Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Bool && v.BoolValue;

        public void SetBool(string name, bool value)
        {
            if (Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Bool)
                v.BoolValue = value;
        }

        public Vector3 GetVector3(string name) =>
            Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Vector3 ? v.Vector3Value : Vector3.Zero;

        public void SetVector3(string name, Vector3 value)
        {
            if (Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.Vector3)
                v.Vector3Value = value;
        }

        public int GetBoneIndex(string name)
        {
            if (Variables.TryGetValue(name, out var v) && v.Type == AnimNodeScriptVariableType.BoneReference)
                return v.BoneIndex;
            return -1;
        }

        public void SetBoneReference(string name, string boneName)
        {
            if (!Variables.TryGetValue(name, out var v) || v.Type != AnimNodeScriptVariableType.BoneReference) return;
            v.BoneName = boneName;
            v.BoneIndex = Skeleton.FindBone(boneName);
        }

        // ──────────────────────────────────────────────
        //  内部方法
        // ──────────────────────────────────────────────

        private void UpdateMontages(float deltaTime)
        {
            // 推进并移除已完成的蒙太奇实例
            for (int i = _montageInstances.Count - 1; i >= 0; i--)
            {
                var inst = _montageInstances[i];
                if (inst.IsFinished)
                {
                    _montageInstances.RemoveAt(i);
                    continue;
                }
                inst.Update(deltaTime);
            }
        }

        private void ExtractRootMotion()
        {
            if (Skeleton.Bones.Count == 0) return;
            var rootPos = CurrentPose.GetTranslationOffset(0);
            var rootRot = CurrentPose.GetRotation(0);

            if (!_hasLastRoot)
            {
                _lastRootPos = rootPos;
                _lastRootRot = rootRot;
                _hasLastRoot = true;
                RootMotionDeltaTranslation = Vector3.Zero;
                RootMotionDeltaRotation = Quaternion.Identity;
                return;
            }

            RootMotionDeltaTranslation = rootPos - _lastRootPos;
            var deltaRot = Quaternion.Normalize(Quaternion.Inverse(_lastRootRot) * rootRot);
            if (Quaternion.Dot(deltaRot, Quaternion.Identity) < 0f) deltaRot = -deltaRot;
            RootMotionDeltaRotation = deltaRot;

            _lastRootPos = rootPos;
            _lastRootRot = rootRot;
        }

        /// <summary>重置根运动跟踪。</summary>
        public void ResetRootMotion()
        {
            _hasLastRoot = false;
            _lastRootPos = Vector3.Zero;
            _lastRootRot = Quaternion.Identity;
            RootMotionDeltaTranslation = Vector3.Zero;
            RootMotionDeltaRotation = Quaternion.Identity;
        }
    }

    /// <summary>
    /// 骨架网格体组件（轻量封装，用于 AnimInstance 引用）。
    /// 完整的 SkeletalMeshComponent 在引擎其他位置定义，此处仅为引用桥接。
    /// </summary>
    public sealed class SkeletalMeshComponent
    {
        public string Name { get; set; } = "SkeletalMesh";
        public Skeleton Skeleton { get; set; }
        public AnimInstance? AnimInstance { get; set; }

        public SkeletalMeshComponent(Skeleton skeleton)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        }
    }
}
