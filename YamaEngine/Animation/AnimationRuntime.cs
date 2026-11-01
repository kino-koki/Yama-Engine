using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Assets;
using YamaEngine.Skinning;

namespace YamaEngine.Animation
{
    // ════════════════════════════════════════════════════════════════════════
    //  AnimationRuntime — 动画系统运行时封装
    // ════════════════════════════════════════════════════════════════════════
    // 为非 ECS 的 SceneObject / IAssetComponent 系统提供动画播放接口。
    // 内部复用现有的 Animator、AnimationClip、SkeletonPose 等核心类。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个场景对象的动画运行状态。
    /// </summary>
    public sealed class AnimationInstance
    {
        /// <summary>所属场景对象。</summary>
        public SceneObject Owner { get; }

        /// <summary>骨骼骨架。</summary>
        public Skeleton Skeleton { get; }

        /// <summary>底层 Animator（Legacy API）。</summary>
        public Animator Animator { get; }

        /// <summary>当前采样得到的骨骼变换矩阵（World Space）。</summary>
        public Matrix4x4[]? BoneMatrices { get; private set; }

        public AnimationInstance(SceneObject owner, Skeleton skeleton)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            Animator = new Animator(skeleton);
        }

        /// <summary>
        /// 计算当前 Pose 对应的骨骼世界变换矩阵数组。
        /// 结果可直接用于 GPU Skinning（顶点着色器中的 boneMatrixPalette）。
        /// </summary>
        public void ComputeBoneMatrices()
        {
            var pose = Animator.CurrentPose;
            if (pose == null) return;

            int boneCount = Skeleton.Bones.Count;
            if (BoneMatrices == null || BoneMatrices.Length != boneCount)
                BoneMatrices = new Matrix4x4[boneCount];

            // 计算每个骨骼的本地矩阵并级联到世界空间
            var localMatrices = new Matrix4x4[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var bone = Skeleton.Bones[i];
                var t = pose.GetTranslationOffset(i);
                var r = pose.GetRotation(i);
                var s = pose.GetScaleOffset(i);

                localMatrices[i] =
                    Matrix4x4.CreateScale(s) *
                    Matrix4x4.CreateFromQuaternion(r) *
                    Matrix4x4.CreateTranslation(t);
            }

            for (int i = 0; i < boneCount; i++)
            {
                var bone = Skeleton.Bones[i];
                if (bone.ParentIndex >= 0 && bone.ParentIndex < boneCount)
                {
                    BoneMatrices[i] = localMatrices[i] * BoneMatrices[bone.ParentIndex];
                }
                else
                {
                    BoneMatrices[i] = localMatrices[i];
                }
            }
        }
    }

    /// <summary>
    /// 动画运行时管理器。封装 AnimationSystem，为 SceneObject 提供播放/更新接口。
    /// </summary>
    public sealed class AnimationRuntime
    {
        private readonly Dictionary<SceneObject, AnimationInstance> _instances = new();
        private readonly List<SceneObject> _removeQueue = new();

        /// <summary>
        /// 为指定场景对象播放动画。如果该对象已有动画状态则复用。
        /// </summary>
        /// <param name="owner">场景对象。</param>
        /// <param name="skeleton">骨骼骨架。</param>
        /// <param name="clip">动画片段。</param>
        /// <param name="stateName">状态名（默认 "Default"）。</param>
        public void PlayAnimation(SceneObject owner, Skeleton skeleton, AnimationClip clip, string stateName = "Default")
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (clip == null) throw new ArgumentNullException(nameof(clip));

            if (!_instances.TryGetValue(owner, out var instance))
            {
                instance = new AnimationInstance(owner, skeleton);
                _instances[owner] = instance;
            }

            instance.Animator.AddState(stateName, clip);
            instance.Animator.Play(stateName);
        }

        /// <summary>
        /// 获取指定对象的动画实例（如不存在返回 null）。
        /// </summary>
        public AnimationInstance? GetInstance(SceneObject owner)
        {
            return owner != null && _instances.TryGetValue(owner, out var inst) ? inst : null;
        }

        /// <summary>
        /// 停止指定对象的动画并移除实例。
        /// </summary>
        public void StopAnimation(SceneObject owner)
        {
            if (owner != null)
                _instances.Remove(owner);
        }

        /// <summary>
        /// 更新所有活跃动画实例。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间（秒）。</param>
        public void Update(float deltaTime)
        {
            _removeQueue.Clear();

            foreach (var kvp in _instances)
            {
                var instance = kvp.Value;

                // 如果 Owner 已被标记为销毁则排队清理
                if (instance.Owner == null)
                {
                    _removeQueue.Add(kvp.Key);
                    continue;
                }

                // 更新 Animator（采样动画、混合、根运动等）
                instance.Animator.Update(deltaTime);

                // 计算骨骼矩阵供渲染器使用
                instance.ComputeBoneMatrices();
            }

            foreach (var key in _removeQueue)
                _instances.Remove(key);
        }

        /// <summary>
        /// 当前活跃的动画实例数量。
        /// </summary>
        public int ActiveCount => _instances.Count;

        /// <summary>
        /// 清空所有动画实例。
        /// </summary>
        public void Clear()
        {
            _instances.Clear();
        }
    }
}
