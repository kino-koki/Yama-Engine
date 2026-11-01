// 在现有 Components.cs 文件中追加以下内容（AnimatorComponent）

using System;
using System.Numerics;
using System.Collections.Generic;
using YamaEngine.Animation;
using YamaEngine.Skinning;

using Skeleton = YamaEngine.Skinning.Skeleton;
namespace YamaEngine.ECS
{
    // ════════════════════════════════════════════════════════════════════════════
    //  AnimatorComponent — ECS 动画组件
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ECS 蒙皮动画组件。附加到 ECS 实体后，由 <see cref="EcsAnimationBridge"/> 驱动，
    /// 结果写入 <see cref="BoneMatrices"/> 供渲染器使用。
    /// </summary>
    public struct AnimatorComponent : IComponent
    {
        /// <summary>骨骼骨架资产 GUID。</summary>
        public Guid SkeletonId { get; set; }

        /// <summary>当前动画片段资产 GUID。</summary>
        public Guid AnimationClipId { get; set; }

        /// <summary>当前状态名称。</summary>
        public string CurrentState { get; set; }

        /// <summary>播放速度倍率（1.0 = 正常速度）。</summary>
        public float PlaybackSpeed { get; set; }

        /// <summary>是否自动播放。</summary>
        public bool IsPlaying { get; set; }

        /// <summary>循环播放。</summary>
        public bool Loop { get; set; }

        /// <summary>当前时间（秒）。运行时由桥接器更新。</summary>
        public float CurrentTime { get; set; }

        /// <summary>动画总时长（秒）。加载后由桥接器设置。</summary>
        public float Duration { get; set; }

        /// <summary>当前骨骼变换矩阵（World Space）。由桥接器每帧写入。</summary>
        public Matrix4x4[]? BoneMatrices { get; set; }

        /// <summary>运行时缓存的 Skeleton 引用（非序列化，运行时解析）。</summary>
        public Skeleton? RuntimeSkeleton { get; set; }

        /// <summary>运行时缓存的 AnimationClip 引用（非序列化）。</summary>
        public AnimationClip? RuntimeClip { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  AnimatorStateComponent — 动画状态机组件（可选）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ECS 动画状态机组件。定义一组状态和过渡规则，由 <see cref="EcsAnimationBridge"/> 驱动。
    /// </summary>
    public struct AnimatorStateComponent : IComponent
    {
        /// <summary>状态机资源 GUID。</summary>
        public Guid StateMachineId { get; set; }

        /// <summary>当前活跃状态名。</summary>
        public string CurrentState { get; set; }

        /// <summary>上一状态名（用于混合）。</summary>
        public string PreviousState { get; set; }

        /// <summary>混合权重（0=当前状态, 1=上一状态）。</summary>
        public float BlendWeight { get; set; }

        /// <summary>混合持续时间（秒）。</summary>
        public float BlendDuration { get; set; }

        /// <summary>混合当前时间（秒）。</summary>
        public float BlendTime { get; set; }

        /// <summary>触发参数 [参数名 → 值]。</summary>
        public Dictionary<string, float> FloatParameters { get; set; }

        /// <summary>布尔参数。</summary>
        public Dictionary<string, bool> BoolParameters { get; set; }

        /// <summary>触发器参数（单次触发后自动重置）。</summary>
        public HashSet<string> Triggers { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  TimelineComponent — 时间轴组件（用于 Sequencer/Cutscene）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ECS 时间轴组件。定义一组动画轨道和关键帧，用于过场动画或程序化动画。
    /// </summary>
    public struct TimelineComponent : IComponent
    {
        /// <summary>时间轴资源 GUID。</summary>
        public Guid TimelineId { get; set; }

        /// <summary>是否自动播放。</summary>
        public bool AutoPlay { get; set; }

        /// <summary>是否循环。</summary>
        public bool Loop { get; set; }

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime { get; set; }

        /// <summary>总时长（秒）。</summary>
        public float Duration { get; set; }

        /// <summary>播放速度。</summary>
        public float PlaybackSpeed { get; set; }

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; set; }
    }
}
