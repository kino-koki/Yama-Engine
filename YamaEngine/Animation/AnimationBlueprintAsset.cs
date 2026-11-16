// ============================================================================
//  YamaEngine.Animation.AnimationBlueprintAsset.cs
//  yama引擎 -- 动画蓝图资产 (Data-driven Animation Blueprint)
// ----------------------------------------------------------------------------
//  闭合"动画蓝图运行时填充"缺口:
//    * AnimationBlueprintAsset : 可序列化资产数据 (参数/状态/混合树/转移)
//    * AnimationBlueprintRuntime: 运行时工厂 (资产 → 活的 AnimatorController)
//        - Build   : 资产 + Skeleton + 剪辑解析 → AnimatorController
//        - ApplyTo : 把控制器挂到 Animator.Controller (填充既有委托钩子)
//        - BuildAndApply: 一步完成
//    * BlendTreeRealMotion : 把 BlendTreeReal(BlendNode 图) 桥接为 IMotion,
//        使断开连接的 BlendTreeReal 图成为状态机 Motion (闭合断连缺口)
// 此前 AnimatorController/BlendTreeReal 从未被引擎内数据实例化 —— 本文件提供
// 首等数据驱动工厂与运行时桥, 让资源管线可填充 Animator.Controller。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Animation
{
    // ========================================================================
    //  一、动画蓝图资产数据 (可序列化)
    // ========================================================================

    /// <summary>蓝图参数数据。</summary>
    public sealed class AnimBlueprintParameterData
    {
        public string Name { get; set; } = string.Empty;
        public AnimatorParameterType Type { get; set; } = AnimatorParameterType.Float;
        public float DefaultFloat { get; set; }
        public bool DefaultBool { get; set; }
        public int DefaultInt { get; set; }

        public AnimBlueprintParameterData() { }
        public AnimBlueprintParameterData(string name, AnimatorParameterType type)
        {
            Name = name;
            Type = type;
        }
    }

    /// <summary>混合树子项数据 (引用一个剪辑 + 轴位置)。</summary>
    public sealed class AnimBlueprintBlendChildData
    {
        public string ClipPath { get; set; } = string.Empty;
        public Guid ClipId { get; set; }
        /// <summary>1D 混合轴位置。</summary>
        public float Threshold { get; set; }
        /// <summary>2D 混合位置。</summary>
        public float X { get; set; }
        public float Y { get; set; }

        public AnimBlueprintBlendChildData() { }
        public AnimBlueprintBlendChildData(string clipPath, float threshold)
        {
            ClipPath = clipPath;
            Threshold = threshold;
        }
    }

    /// <summary>混合树数据 (1D / 2D)。</summary>
    public sealed class AnimBlueprintBlendTreeData
    {
        public bool Is2D { get; set; }
        public string ParameterX { get; set; } = "Speed";
        public string ParameterY { get; set; } = string.Empty;
        public List<AnimBlueprintBlendChildData> Children { get; set; } = new();

        public AnimBlueprintBlendChildData AddChild(string clipPath, float threshold)
        {
            var c = new AnimBlueprintBlendChildData(clipPath, threshold);
            Children.Add(c);
            return c;
        }

        public AnimBlueprintBlendChildData AddChild(string clipPath, float x, float y)
        {
            var c = new AnimBlueprintBlendChildData(clipPath, 0f) { X = x, Y = y };
            Children.Add(c);
            return c;
        }
    }

    /// <summary>状态数据。</summary>
    public sealed class AnimBlueprintStateData
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>剪辑资产路径 (优先 ClipId 解析)。</summary>
        public string ClipPath { get; set; } = string.Empty;
        public Guid ClipId { get; set; }
        /// <summary>该状态使用混合树 Motion。</summary>
        public bool UseBlendTree { get; set; }
        public AnimBlueprintBlendTreeData BlendTree { get; set; }
        public float Speed { get; set; } = 1f;
        public bool Loop { get; set; } = true;

        public AnimBlueprintStateData() { }
        public AnimBlueprintStateData(string name)
        {
            Name = name;
        }
    }

    /// <summary>转移条件数据。</summary>
    public sealed class AnimBlueprintConditionData
    {
        public string Parameter { get; set; } = string.Empty;
        public AnimatorConditionMode Mode { get; set; } = AnimatorConditionMode.Greater;
        public float Threshold { get; set; }

        public AnimBlueprintConditionData() { }
        public AnimBlueprintConditionData(string parameter, AnimatorConditionMode mode, float threshold = 0f)
        {
            Parameter = parameter;
            Mode = mode;
            Threshold = threshold;
        }
    }

    /// <summary>状态转移数据。</summary>
    public sealed class AnimBlueprintTransitionData
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public bool HasExitTime { get; set; }
        public float ExitTime { get; set; } = 1f;
        public float Duration { get; set; } = 0.25f;
        public List<AnimBlueprintConditionData> Conditions { get; set; } = new();

        public AnimBlueprintTransitionData() { }
        public AnimBlueprintTransitionData(string from, string to)
        {
            From = from;
            To = to;
        }
    }

    /// <summary>
    /// 动画蓝图资产。描述一个多状态动画控制器图: 参数/状态(剪辑或混合树)/转移(条件)。
    /// 作为资产可序列化 (由编辑器/资源管线写入), 运行时经
    /// <see cref="AnimationBlueprintRuntime.Build"/> 转为活的 <see cref="AnimatorController"/>。
    /// </summary>
    public sealed class AnimationBlueprintAsset
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "AnimBlueprint";
        public string DefaultState { get; set; } = string.Empty;
        public List<AnimBlueprintParameterData> Parameters { get; set; } = new();
        public List<AnimBlueprintStateData> States { get; set; } = new();
        public List<AnimBlueprintTransitionData> Transitions { get; set; } = new();

        // ── 创作辅助 (编辑器/脚本构建) ─────────────────────────────

        public AnimBlueprintParameterData AddFloat(string name, float defaultValue = 0f)
        {
            var p = new AnimBlueprintParameterData(name, AnimatorParameterType.Float) { DefaultFloat = defaultValue };
            Parameters.Add(p);
            return p;
        }

        public AnimBlueprintParameterData AddBool(string name, bool defaultValue = false)
        {
            var p = new AnimBlueprintParameterData(name, AnimatorParameterType.Bool) { DefaultBool = defaultValue };
            Parameters.Add(p);
            return p;
        }

        public AnimBlueprintParameterData AddTrigger(string name)
        {
            var p = new AnimBlueprintParameterData(name, AnimatorParameterType.Trigger);
            Parameters.Add(p);
            return p;
        }

        public AnimBlueprintStateData AddClipState(string name, string clipPath, float speed = 1f, bool loop = true)
        {
            var s = new AnimBlueprintStateData(name) { ClipPath = clipPath, Speed = speed, Loop = loop };
            States.Add(s);
            return s;
        }

        public AnimBlueprintStateData AddClipState(string name, Guid clipId, float speed = 1f, bool loop = true)
        {
            var s = new AnimBlueprintStateData(name) { ClipId = clipId, Speed = speed, Loop = loop };
            States.Add(s);
            return s;
        }

        public AnimBlueprintStateData AddBlendTreeState1D(string name, string parameterX)
        {
            var s = new AnimBlueprintStateData(name)
            {
                UseBlendTree = true,
                BlendTree = new AnimBlueprintBlendTreeData { ParameterX = parameterX }
            };
            States.Add(s);
            return s;
        }

        public AnimBlueprintTransitionData AddTransition(string from, string to,
            AnimatorConditionMode mode, string parameter, float threshold = 0f, float duration = 0.25f)
        {
            var t = new AnimBlueprintTransitionData(from, to) { Duration = duration };
            t.Conditions.Add(new AnimBlueprintConditionData(parameter, mode, threshold));
            Transitions.Add(t);
            return t;
        }
    }

    // ========================================================================
    //  二、运行时工厂 (资产 → 活的 AnimatorController)
    // ========================================================================

    /// <summary>
    /// 动画蓝图运行时工厂。把 <see cref="AnimationBlueprintAsset"/> 数据转换为一个
    /// 活的 <see cref="AnimatorController"/> (含状态机/混合树/参数/转移), 并可挂到
    /// <see cref="Animator.Controller"/> —— 闭合"控制器钩子存在但从未被填充"的缺口。
    /// </summary>
    public static class AnimationBlueprintRuntime
    {
        /// <summary>
        /// 从资产构建一个活的 AnimatorController。
        /// </summary>
        /// <param name="asset">蓝图资产数据。</param>
        /// <param name="skeleton">目标骨骼 (运行时)。</param>
        /// <param name="resolveClip">按 Guid 解析 AnimationClip 的委托。</param>
        /// <param name="resolveClipByPath">按资产路径解析 AnimationClip 的委托 (可选)。</param>
        public static AnimatorController Build(AnimationBlueprintAsset asset, Skeleton skeleton,
            Func<Guid, AnimationClip> resolveClip, Func<string, AnimationClip> resolveClipByPath = null)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (resolveClip == null) throw new ArgumentNullException(nameof(resolveClip));

            var controller = new AnimatorController(skeleton) { Name = asset.Name };
            var sm = new AnimationStateMachine { Name = asset.Name, DefaultState = asset.DefaultState };

            // 1) 参数 —— 加入控制器 (条件评估/SetFloat 用) 与状态机 (sm 本地读用)
            foreach (var p in asset.Parameters)
            {
                switch (p.Type)
                {
                    case AnimatorParameterType.Float:
                        sm.AddFloat(p.Name, p.DefaultFloat);
                        controller.AddFloat(p.Name, p.DefaultFloat);
                        break;
                    case AnimatorParameterType.Bool:
                        sm.AddBool(p.Name, p.DefaultBool);
                        controller.AddBool(p.Name, p.DefaultBool);
                        break;
                    case AnimatorParameterType.Int:
                        sm.AddInt(p.Name, p.DefaultInt);
                        controller.AddInt(p.Name, p.DefaultInt);
                        break;
                    case AnimatorParameterType.Trigger:
                        sm.AddTrigger(p.Name);
                        controller.AddTrigger(p.Name);
                        break;
                }
            }

            // 2) 状态 (剪辑或混合树 Motion)
            foreach (var s in asset.States)
            {
                IMotion motion = null;
                if (s.UseBlendTree && s.BlendTree != null)
                    motion = BuildBlendTree(s.BlendTree, resolveClip, resolveClipByPath);
                else
                    motion = resolveClip(s.ClipId) ?? resolveClipByPath?.Invoke(s.ClipPath);

                var state = sm.AddState(s.Name, motion);
                state.Speed = s.Speed;
                if (string.IsNullOrEmpty(sm.DefaultState))
                    sm.DefaultState = s.Name;
            }

            // 3) 转移 (条件)
            foreach (var t in asset.Transitions)
            {
                var at = sm.AddTransition(t.From, t.To, t.HasExitTime, t.ExitTime, t.Duration);
                foreach (var c in t.Conditions)
                    at.Conditions.Add(new AnimatorCondition(c.Parameter, c.Mode, c.Threshold));
            }

            controller.AddLayer("Base", sm);
            ApplyDefaults(controller, asset);
            return controller;
        }

        /// <summary>把已构建的控制器挂到 Animator.Controller (填充委托钩子)。</summary>
        public static void ApplyTo(Animator animator, AnimatorController controller)
        {
            if (animator == null) throw new ArgumentNullException(nameof(animator));
            animator.Controller = controller;
        }

        /// <summary>构建并挂载, 一步完成。返回控制器。</summary>
        public static AnimatorController BuildAndApply(AnimationBlueprintAsset asset, Animator animator,
            Func<Guid, AnimationClip> resolveClip, Func<string, AnimationClip> resolveClipByPath = null)
        {
            var controller = Build(asset, animator.Skeleton, resolveClip, resolveClipByPath);
            ApplyTo(animator, controller);
            return controller;
        }

        private static void ApplyDefaults(AnimatorController controller, AnimationBlueprintAsset asset)
        {
            foreach (var p in asset.Parameters)
            {
                switch (p.Type)
                {
                    case AnimatorParameterType.Float: controller.SetFloat(p.Name, p.DefaultFloat); break;
                    case AnimatorParameterType.Bool: controller.SetBool(p.Name, p.DefaultBool); break;
                    case AnimatorParameterType.Int: controller.SetInt(p.Name, p.DefaultInt); break;
                }
            }
        }

        private static IMotion BuildBlendTree(AnimBlueprintBlendTreeData data,
            Func<Guid, AnimationClip> resolveClip, Func<string, AnimationClip> resolveClipByPath)
        {
            var tree = new BlendTree(data.Is2D ? "Blend2D" : "Blend1D",
                data.Is2D ? BlendTreeType.SimpleDirectional2D : BlendTreeType.Simple1D)
            {
                BlendParameter = data.ParameterX,
                BlendParameterY = data.ParameterY
            };

            foreach (var child in data.Children)
            {
                var clip = resolveClip(child.ClipId) ?? resolveClipByPath?.Invoke(child.ClipPath);
                if (clip == null) continue;
                if (data.Is2D)
                    tree.AddChild(clip, new Vector2(child.X, child.Y));
                else
                    tree.AddChild(clip, child.Threshold);
            }
            return tree;
        }
    }

    // ========================================================================
    //  三、BlendTreeReal → IMotion 桥 (闭合 BlendTreeReal 断连缺口)
    // ========================================================================

    /// <summary>
    /// 把 <see cref="BlendTreeReal"/> 的 <see cref="BlendNode"/> 图桥接为
    /// <see cref="IMotion"/>, 使真实混合节点图可作为状态机 Motion 参与
    /// AnimatorController 管线。此前 BlendTreeReal 完全断连, 现可被引擎使用。
    /// </summary>
    public sealed class BlendTreeRealMotion : IMotion
    {
        private readonly BlendNode _root;
        private readonly BlendContext _context = new BlendContext();
        private float _lastSampleTime = -1f;

        public string Name { get; }
        public float Length { get; set; }
        public bool IsLooping => true;

        /// <summary>底层真实混合上下文 (可设置 DeltaTime/参数)。</summary>
        public BlendContext Context => _context;

        /// <summary>被桥接的根节点。</summary>
        public BlendNode Root => _root;

        public BlendTreeRealMotion(BlendNode root, float length = 1f)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            Name = root.Name;
            Length = length;
        }

        /// <summary>设置混合参数 (如 Speed)。</summary>
        public void SetParameter(string name, float value) => _context.SetParameter(name, value);

        public SkeletonPose Sample(Skeleton skeleton, float timeSeconds)
        {
            _context.Skeleton = skeleton;
            _context.CurrentTime = timeSeconds;
            // ClipNode 以 context.DeltaTime 推进自身播放时间 —— 按相邻采样差提供
            _context.DeltaTime = _lastSampleTime >= 0f
                ? MathF.Max(0f, timeSeconds - _lastSampleTime)
                : 0f;
            _lastSampleTime = timeSeconds;
            return _root.Evaluate(_context);
        }

        public void Reset()
        {
            _root.Reset();
            _lastSampleTime = -1f;
        }
    }

    /// <summary>
    /// 把活 <see cref="AnimationClip"/> (动画运行时剪辑) 转换为
    /// <see cref="AnimationClipReal"/> (BlendTreeReal 图使用的剪辑)。
    /// 按目标骨骼的骨名→索引映射重建轨道。
    /// </summary>
    public static class AnimationClipRealConverter
    {
        public static AnimationClipReal Convert(AnimationClip clip, Skeleton skeleton)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));

            var real = new AnimationClipReal
            {
                Name = clip.Name,
                Duration = clip.LengthSeconds,
                Loop = clip.Looping,
                FrameRate = 30f
            };

            foreach (var track in clip.Tracks)
            {
                if (!skeleton.BoneNameToIndex.TryGetValue(track.BoneName, out int boneIndex))
                    continue;

                var realTrack = new BoneTrack { BoneName = track.BoneName, BoneIndex = boneIndex };

                foreach (var k in track.TranslationKeys)
                    realTrack.PositionKeys.Add(new Vector3Keyframe { Time = k.TimeSeconds, Value = k.Value });
                foreach (var k in track.RotationKeys)
                    realTrack.RotationKeys.Add(new QuaternionKeyframe { Time = k.TimeSeconds, Value = k.Value });
                foreach (var k in track.ScaleKeys)
                    realTrack.ScaleKeys.Add(new Vector3Keyframe { Time = k.TimeSeconds, Value = k.Value });

                real.Tracks.Add(realTrack);
            }

            return real;
        }
    }
}
