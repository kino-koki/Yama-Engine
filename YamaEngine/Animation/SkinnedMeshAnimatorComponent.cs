using System;
using YamaEngine.Assets;
using YamaEngine.Skinning;

namespace YamaEngine.Animation
{
    // ════════════════════════════════════════════════════════════════════════
    //  SkinnedMeshAnimatorComponent — 蒙皮网格动画组件
    // ════════════════════════════════════════════════════════════════════════
    // 附加到 SceneObject 的 IAssetComponent，承载 Skeleton + AnimationClip
    // 数据，供 AnimationRuntime 在 Tick 时驱动。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 场景对象的蒙皮动画组件。包含骨骼骨架和当前播放的动画片段引用。
    /// 由 <see cref="AnimationRuntime"/> 统一更新，结果写入 <see cref="CurrentBoneMatrices"/>。
    /// </summary>
    public sealed class SkinnedMeshAnimatorComponent : IAssetComponent
    {
        public string Name => "SkinnedMeshAnimator";

        /// <summary>骨骼骨架（通常在模型导入时创建）。</summary>
        public Skeleton? Skeleton { get; set; }

        /// <summary>默认动画片段（可在运行时替换）。</summary>
        public AnimationClip? AnimationClip { get; set; }

        /// <summary>当前动画状态名。</summary>
        public string CurrentStateName { get; set; } = "Default";

        /// <summary>是否自动播放。</summary>
        public bool AutoPlay { get; set; } = true;

        /// <summary>是否已在运行时注册到 AnimationRuntime。</summary>
        public bool IsRegistered { get; set; }

        /// <summary>
        /// 当前帧计算出的骨骼变换矩阵（World Space）。
        /// 由 AnimationRuntime.Update 写入，渲染器可读取此数据上传 GPU。
        /// </summary>
        public System.Numerics.Matrix4x4[]? CurrentBoneMatrices { get; set; }

        /// <summary>
        /// IAssetComponent 更新接口。此处不直接更新动画，而是留给
        /// EngineApplication.TickAnimations 统一调用 AnimationRuntime.Update(dt)。
        /// </summary>
        public void Update(float deltaTime, SceneObject owner)
        {
            // 动画实际更新由 AnimationRuntime 批量处理，此处仅作占位。
            // 如需按组件单独更新，可在此调用 AnimationRuntime.PlayAnimation/Update。
        }
    }
}
