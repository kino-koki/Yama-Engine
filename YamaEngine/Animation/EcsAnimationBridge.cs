// ==================================================================================================
//  EcsAnimationBridge.cs — yama引擎 ECS ↔ 动画系统桥接器
// ==================================================================================================
//  命名空间: YamaEngine.Animation
//  依赖:     YamaEngine.ECS, YamaEngine.Skinning
//
//  功能:
//    1. 每帧遍历所有带 AnimatorComponent 的 ECS 实体
//    2. 解析 Skeleton 和 AnimationClip 资产引用
//    3. 采样动画并计算骨骼世界变换矩阵
//    4. 结果写回 AnimatorComponent.BoneMatrices
//    5. 支持 AnimatorStateComponent 状态机驱动
//
//  架构:
//    ECS AnimatorComponent → 资产加载/缓存 → Animator 采样 → BoneMatrices → GPU Skinning
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Skinning;
using YamaEngine.Assets;

namespace YamaEngine.Animation
{
    /// <summary>
    /// ECS 动画系统桥接器。将 ECS 实体上的 <see cref="AnimatorComponent"/> 驱动为实际的骨骼动画，
    /// 结果矩阵写回组件供渲染器使用。
    /// </summary>
    public sealed class EcsAnimationBridge : IDisposable
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly AssetDatabase? _assetDb;

        // ── 运行时缓存 ────────────────────────────────────────────────────────────

        private readonly Dictionary<Guid, Skeleton> _skeletonCache = new();
        private readonly Dictionary<Guid, AnimationClip> _clipCache = new();
        private readonly Dictionary<int, Animator> _animatorCache = new();

        // ── 事件 ──────────────────────────────────────────────────────────────────

        /// <summary>动画采样异常事件。</summary>
        public event Action<int, string>? OnAnimationError;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造 / 释放
        // ─────────────────────────────────────────────────────────────────────────

        public EcsAnimationBridge(EntityComponentSystem ecs, AssetDatabase? assetDb = null)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _assetDb = assetDb;
        }

        public void Dispose()
        {
            _skeletonCache.Clear();
            _clipCache.Clear();
            _animatorCache.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  每帧更新
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 更新所有 ECS 动画实体。应在每帧渲染前调用。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间（秒）。</param>
        public void Update(float deltaTime)
        {
            foreach (int entityId in _ecs.AllEntities)
            {
                if (!_ecs.HasComponent<AnimatorComponent>(entityId))
                    continue;

                try
                {
                    UpdateEntity(entityId, deltaTime);
                }
                catch (Exception ex)
                {
                    OnAnimationError?.Invoke(entityId, ex.Message);
                }
            }
        }

        private void UpdateEntity(int entityId, float deltaTime)
        {
            var anim = _ecs.GetComponent<AnimatorComponent>(entityId);

            // 解析 Skeleton
            if (anim.RuntimeSkeleton == null && anim.SkeletonId != Guid.Empty)
                anim.RuntimeSkeleton = ResolveSkeleton(anim.SkeletonId);

            // 解析 AnimationClip
            if (anim.RuntimeClip == null && anim.AnimationClipId != Guid.Empty)
                anim.RuntimeClip = ResolveClip(anim.AnimationClipId);

            var skeleton = anim.RuntimeSkeleton;
            var clip = anim.RuntimeClip;

            if (skeleton == null || clip == null)
                return;

            // 获取或创建 Animator
            if (!_animatorCache.TryGetValue(entityId, out var animator) ||
                animator.Skeleton != skeleton)
            {
                animator = new Animator(skeleton);
                _animatorCache[entityId] = animator;
            }

            // 同步当前状态
            if (!string.IsNullOrEmpty(anim.CurrentState) &&
                animator.GetState(anim.CurrentState) == null)
            {
                animator.AddState(anim.CurrentState, clip);
            }

            // 播放控制
            if (anim.IsPlaying)
            {
                if (animator.CurrentStateName != anim.CurrentState)
                    animator.Play(anim.CurrentState);

                animator.Update(deltaTime * anim.PlaybackSpeed);
                anim.CurrentTime = animator.CurrentTime;
                anim.Duration = clip.Duration;

                // 循环处理
                if (anim.Loop && anim.CurrentTime >= clip.Duration)
                {
                    animator.SetTime(0);
                    anim.CurrentTime = 0;
                }
            }

            // 计算骨骼矩阵
            ComputeBoneMatrices(skeleton, animator, ref anim);

            // 写回组件
            _ecs.SetComponent(entityId, anim);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  状态机更新
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 更新所有带 AnimatorStateComponent 的实体的状态机。
        /// 应在 Update 之后调用，用于处理状态过渡和混合。
        /// </summary>
        public void UpdateStateMachines(float deltaTime)
        {
            foreach (int entityId in _ecs.AllEntities)
            {
                if (!_ecs.HasComponent<AnimatorStateComponent>(entityId) ||
                    !_ecs.HasComponent<AnimatorComponent>(entityId))
                    continue;

                try
                {
                    UpdateStateMachine(entityId, deltaTime);
                }
                catch (Exception ex)
                {
                    OnAnimationError?.Invoke(entityId, $"StateMachine: {ex.Message}");
                }
            }
        }

        private void UpdateStateMachine(int entityId, float deltaTime)
        {
            var stateComp = _ecs.GetComponent<AnimatorStateComponent>(entityId);
            var animComp = _ecs.GetComponent<AnimatorComponent>(entityId);

            // 处理混合
            if (stateComp.BlendDuration > 0 && stateComp.BlendTime < stateComp.BlendDuration)
            {
                stateComp.BlendTime += deltaTime;
                float t = MathF.Min(stateComp.BlendTime / stateComp.BlendDuration, 1f);
                // 使用平滑曲线
                stateComp.BlendWeight = t * t * (3f - 2f * t);
            }
            else
            {
                stateComp.BlendWeight = 0f;
                stateComp.PreviousState = string.Empty;
            }

            // 触发器自动重置
            if (stateComp.Triggers != null)
                stateComp.Triggers.Clear();

            // 同步到 AnimatorComponent
            animComp.CurrentState = stateComp.CurrentState;
            _ecs.SetComponent(entityId, animComp);
            _ecs.SetComponent(entityId, stateComp);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  骨骼矩阵计算
        // ─────────────────────────────────────────────────────────────────────────

        private static void ComputeBoneMatrices(Skeleton skeleton, Animator animator, ref AnimatorComponent anim)
        {
            var pose = animator.CurrentPose;
            if (pose == null) return;

            int boneCount = skeleton.Bones.Count;
            if (anim.BoneMatrices == null || anim.BoneMatrices.Length != boneCount)
                anim.BoneMatrices = new Matrix4x4[boneCount];

            // 计算本地矩阵
            var localMats = new Matrix4x4[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var bone = skeleton.Bones[i];
                var t = pose.GetTranslationOffset(i);
                var r = pose.GetRotation(i);
                var s = pose.GetScaleOffset(i);

                localMats[i] =
                    Matrix4x4.CreateScale(s) *
                    Matrix4x4.CreateFromQuaternion(r) *
                    Matrix4x4.CreateTranslation(t);
            }

            // 级联到世界空间
            for (int i = 0; i < boneCount; i++)
            {
                var bone = skeleton.Bones[i];
                if (bone.ParentIndex >= 0 && bone.ParentIndex < boneCount)
                    anim.BoneMatrices[i] = localMats[i] * anim.BoneMatrices[bone.ParentIndex];
                else
                    anim.BoneMatrices[i] = localMats[i];
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  资产解析
        // ─────────────────────────────────────────────────────────────────────────

        private Skeleton? ResolveSkeleton(Guid id)
        {
            if (_skeletonCache.TryGetValue(id, out var skel))
                return skel;

            if (_assetDb != null)
            {
                // 通过 AssetDatabase 加载
                skel = _assetDb.GetAsset<Skeleton>(id);
                if (skel != null)
                {
                    _skeletonCache[id] = skel;
                    return skel;
                }
            }

            // 回退：尝试从已知资产加载
            return null;
        }

        private AnimationClip? ResolveClip(Guid id)
        {
            if (_clipCache.TryGetValue(id, out var clip))
                return clip;

            if (_assetDb != null)
            {
                clip = _assetDb.GetAsset<AnimationClip>(id);
                if (clip != null)
                {
                    _clipCache[id] = clip;
                    return clip;
                }
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  公共 API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>为指定实体播放动画状态。</summary>
        public void Play(int entityId, string stateName)
        {
            if (!_ecs.HasComponent<AnimatorComponent>(entityId)) return;
            var anim = _ecs.GetComponent<AnimatorComponent>(entityId);
            anim.CurrentState = stateName;
            anim.IsPlaying = true;
            _ecs.SetComponent(entityId, anim);
        }

        /// <summary>暂停指定实体的动画。</summary>
        public void Pause(int entityId)
        {
            if (!_ecs.HasComponent<AnimatorComponent>(entityId)) return;
            var anim = _ecs.GetComponent<AnimatorComponent>(entityId);
            anim.IsPlaying = false;
            _ecs.SetComponent(entityId, anim);
        }

        /// <summary>设置播放时间。</summary>
        public void SetTime(int entityId, float time)
        {
            if (!_ecs.HasComponent<AnimatorComponent>(entityId)) return;
            var anim = _ecs.GetComponent<AnimatorComponent>(entityId);
            anim.CurrentTime = time;
            _ecs.SetComponent(entityId, anim);

            if (_animatorCache.TryGetValue(entityId, out var animator))
                animator.SetTime(time);
        }

        /// <summary>设置动画参数（Float）。</summary>
        public void SetFloat(int entityId, string name, float value)
        {
            if (!_ecs.HasComponent<AnimatorStateComponent>(entityId)) return;
            var state = _ecs.GetComponent<AnimatorStateComponent>(entityId);
            state.FloatParameters ??= new Dictionary<string, float>();
            state.FloatParameters[name] = value;
            _ecs.SetComponent(entityId, state);
        }

        /// <summary>设置动画参数（Bool）。</summary>
        public void SetBool(int entityId, string name, bool value)
        {
            if (!_ecs.HasComponent<AnimatorStateComponent>(entityId)) return;
            var state = _ecs.GetComponent<AnimatorStateComponent>(entityId);
            state.BoolParameters ??= new Dictionary<string, bool>();
            state.BoolParameters[name] = value;
            _ecs.SetComponent(entityId, state);
        }

        /// <summary>触发器参数。</summary>
        public void SetTrigger(int entityId, string name)
        {
            if (!_ecs.HasComponent<AnimatorStateComponent>(entityId)) return;
            var state = _ecs.GetComponent<AnimatorStateComponent>(entityId);
            state.Triggers ??= new HashSet<string>();
            state.Triggers.Add(name);
            _ecs.SetComponent(entityId, state);
        }

        /// <summary>清理指定实体的动画缓存。</summary>
        public void ClearEntityCache(int entityId)
        {
            _animatorCache.Remove(entityId);
        }
    }
}
