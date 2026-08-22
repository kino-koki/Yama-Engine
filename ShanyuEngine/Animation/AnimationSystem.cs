using System.Numerics;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    /// <summary>
    /// The legacy lightweight animator. Provides a simple named-state graph with instant
    /// playback and timed cross-fades, and is fully backward compatible with earlier
    /// engine code. It can also drive (or be driven by) a full <see cref="AnimatorController"/>
    /// for state-machine, blend-tree, layer, rigging and retargeting workflows.
    /// </summary>
    public sealed class Animator
    {
        private readonly Skeleton _skeleton;
        private readonly Dictionary<string, AnimationClip> _states = new(StringComparer.OrdinalIgnoreCase);
        private float _currentTime;
        private float _targetTime;
        private float _blendDuration;
        private float _blendElapsed;
        private float _blendWeight = 1f;

        public string CurrentStateName { get; private set; } = string.Empty;
        public string TargetStateName { get; private set; } = string.Empty;

        /// <summary>Cross-fade progress in [0,1] (1 when no transition is active).</summary>
        public float BlendWeight
        {
            get => _blendWeight;
            private set => _blendWeight = value;
        }

        public SkeletonPose CurrentPose { get; private set; }

        /// <summary>Optional full controller. When set, <see cref="Update"/> delegates to it.</summary>
        public AnimatorController? Controller { get; set; }

        /// <summary>Optional runtime rig applied after pose evaluation (legacy mode).</summary>
        public AnimationRig? Rig { get; set; }

        /// <summary>When true, per-frame root-bone motion deltas are tracked in <see cref="RootMotion"/>.</summary>
        public bool ApplyRootMotion { get; set; }
        public RootMotionData RootMotion { get; private set; }

        public Skeleton Skeleton => _skeleton;

        private Vector3 _lastRootPos;
        private Quaternion _lastRootRot = Quaternion.Identity;
        private bool _hasLastRoot;

        public Animator(Skeleton skeleton)
        {
            _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            CurrentPose = skeleton.CreateBindPose();
        }

        // ---------------------------------------------------------------------
        // Legacy state API
        // ---------------------------------------------------------------------

        public void AddState(string name, AnimationClip clip)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Animation state name cannot be empty.", nameof(name));
            _states[name] = clip ?? throw new ArgumentNullException(nameof(clip));
        }

        public AnimationClip? GetState(string name) =>
            _states.TryGetValue(name, out var clip) ? clip : null;

        public void Play(string stateName)
        {
            if (!_states.ContainsKey(stateName))
                throw new ArgumentException($"Animation state '{stateName}' not found.");
            CurrentStateName = stateName;
            TargetStateName = string.Empty;
            _currentTime = 0f;
            _targetTime = 0f;
            _blendDuration = 0f;
            _blendElapsed = 0f;
            _blendWeight = 1f;
            CurrentPose = _states[stateName].Sample(_skeleton, 0f);
            Rig?.Apply(_skeleton, CurrentPose);
        }

        public void CrossFade(string stateName, float durationSeconds)
        {
            if (!_states.ContainsKey(stateName))
                throw new ArgumentException($"Animation state '{stateName}' not found.");
            if (string.IsNullOrEmpty(CurrentStateName))
            {
                Play(stateName);
                return;
            }

            TargetStateName = stateName;
            _targetTime = 0f;
            _blendDuration = MathF.Max(0.0001f, durationSeconds);
            _blendElapsed = 0f;
        }

        // ---------------------------------------------------------------------
        // Update
        // ---------------------------------------------------------------------

        public void Update(float deltaTime)
        {
            // Controller mode: delegate to the full state machine pipeline.
            if (Controller != null)
            {
                UpdateWithController(deltaTime);
                return;
            }

            deltaTime = MathF.Max(0f, deltaTime);
            if (string.IsNullOrEmpty(CurrentStateName)) return;

            _currentTime += deltaTime;
            var currentPose = _states[CurrentStateName].Sample(_skeleton, _currentTime);

            if (string.IsNullOrEmpty(TargetStateName))
            {
                _blendWeight = 1f;
                CurrentPose = currentPose;
                Rig?.Apply(_skeleton, CurrentPose);
                if (ApplyRootMotion) RootMotion = ExtractRootMotion(CurrentPose);
                return;
            }

            _targetTime += deltaTime;
            _blendElapsed += deltaTime;
            _blendWeight = _blendDuration <= 0f ? 1f : Math.Clamp(_blendElapsed / _blendDuration, 0f, 1f);

            var targetPose = _states[TargetStateName].Sample(_skeleton, _targetTime);
            CurrentPose = AnimationPoseBlender.Blend(_skeleton, currentPose, targetPose, _blendWeight);

            Rig?.Apply(_skeleton, CurrentPose);

            if (_blendWeight >= 1f)
            {
                CurrentStateName = TargetStateName;
                TargetStateName = string.Empty;
                _currentTime = _targetTime;
                _targetTime = 0f;
                _blendDuration = 0f;
                _blendElapsed = 0f;
                _blendWeight = 1f;
                CurrentPose = _states[CurrentStateName].Sample(_skeleton, _currentTime);
                Rig?.Apply(_skeleton, CurrentPose);
            }

            if (ApplyRootMotion) RootMotion = ExtractRootMotion(CurrentPose);
        }

        private void UpdateWithController(float deltaTime)
        {
            // Forward the rig to the controller if the controller does not already have one.
            if (Controller!.Rig == null && Rig != null)
                Controller.Rig = Rig;

            Controller.Update(deltaTime);
            CurrentPose = Controller.CurrentPose;

            // Mirror the controller's base-layer state into the legacy properties so the
            // existing editor/tests can introspect the active animation.
            CurrentStateName = Controller.CurrentStateName;
            TargetStateName = Controller.NextStateName;
            _blendWeight = Controller.IsTransitioning ? Controller.TransitionProgress : 1f;

            if (ApplyRootMotion)
                RootMotion = Controller.RootMotion;
        }

        private RootMotionData ExtractRootMotion(SkeletonPose pose)
        {
            if (_skeleton.Bones.Count == 0)
            {
                _hasLastRoot = false;
                return RootMotionData.Identity;
            }

            var pos = pose.GetTranslationOffset(0);
            var rot = pose.GetRotation(0);

            if (!_hasLastRoot)
            {
                _lastRootPos = pos;
                _lastRootRot = rot;
                _hasLastRoot = true;
                return RootMotionData.Identity;
            }

            var posDelta = pos - _lastRootPos;
            var rotDelta = Quaternion.Normalize(Quaternion.Inverse(_lastRootRot) * rot);
            if (Quaternion.Dot(rotDelta, Quaternion.Identity) < 0f)
                rotDelta = -rotDelta;

            _lastRootPos = pos;
            _lastRootRot = rot;

            return new RootMotionData { PositionDelta = posDelta, RotationDelta = rotDelta };
        }

        public void ResetRootMotion()
        {
            _hasLastRoot = false;
            _lastRootPos = Vector3.Zero;
            _lastRootRot = Quaternion.Identity;
            RootMotion = RootMotionData.Identity;
        }
    }

    /// <summary>
    /// Static utilities for blending <see cref="SkeletonPose"/>s. Supports simple two-pose
    /// blends (with scale), masked blends, additive blends, multi-pose weighted blends and
    /// full layer blending used by the <see cref="AnimatorController"/>.
    /// </summary>
    public static class AnimationPoseBlender
    {
        /// <summary>
        /// Linearly blend two poses by <paramref name="weight"/> (0 = a, 1 = b). Translations
        /// and scales use vector lerp; rotations use slerp.
        /// </summary>
        public static SkeletonPose Blend(Skeleton skeleton, SkeletonPose a, SkeletonPose b, float weight)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            var w = Math.Clamp(weight, 0f, 1f);
            var pose = skeleton.CreateBindPose();
            var count = skeleton.Bones.Count;
            for (var i = 0; i < count; i++)
            {
                pose.SetTranslationOffset(i, Vector3.Lerp(a.GetTranslationOffset(i), b.GetTranslationOffset(i), w));
                pose.SetScaleOffset(i, Vector3.Lerp(a.GetScaleOffset(i), b.GetScaleOffset(i), w));

                var ra = a.GetRotation(i);
                var rb = b.GetRotation(i);
                // Shortest-path correction.
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                pose.SetRotation(i, Quaternion.Normalize(Quaternion.Slerp(ra, rb, w)));
            }
            return pose;
        }

        /// <summary>
        /// Blend two poses but only for bones that pass the <paramref name="mask"/>. Bones
        /// failing the mask keep the value from <paramref name="a"/>.
        /// </summary>
        public static SkeletonPose BlendMasked(Skeleton skeleton, SkeletonPose a, SkeletonPose b,
                                               float weight, AvatarMask? mask)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (mask == null) return Blend(skeleton, a, b, weight);

            var w = Math.Clamp(weight, 0f, 1f);
            var pose = skeleton.CreateBindPose();
            var count = skeleton.Bones.Count;
            for (var i = 0; i < count; i++)
            {
                if (!mask.GetBonePass(i, skeleton))
                {
                    pose.SetTranslationOffset(i, a.GetTranslationOffset(i));
                    pose.SetScaleOffset(i, a.GetScaleOffset(i));
                    pose.SetRotation(i, a.GetRotation(i));
                    continue;
                }

                pose.SetTranslationOffset(i, Vector3.Lerp(a.GetTranslationOffset(i), b.GetTranslationOffset(i), w));
                pose.SetScaleOffset(i, Vector3.Lerp(a.GetScaleOffset(i), b.GetScaleOffset(i), w));

                var ra = a.GetRotation(i);
                var rb = b.GetRotation(i);
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                pose.SetRotation(i, Quaternion.Normalize(Quaternion.Slerp(ra, rb, w)));
            }
            return pose;
        }

        /// <summary>
        /// Additively blend <paramref name="additive"/> (interpreted relative to the bind
        /// pose) onto <paramref name="base"/> by <paramref name="weight"/>, modifying
        /// <paramref name="base"/> in place.
        /// </summary>
        public static void BlendAdditive(Skeleton skeleton, SkeletonPose @base, SkeletonPose additive,
                                         float weight, AvatarMask? mask = null)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (additive == null) return;
            var w = Math.Clamp(weight, 0f, 1f);
            var count = skeleton.Bones.Count;
            for (var i = 0; i < count; i++)
            {
                if (mask != null && !mask.GetBonePass(i, skeleton))
                    continue;

                var bindRot = skeleton.Bones[i].LocalRotation;
                var baseRot = @base.GetRotation(i);
                var addRot = additive.GetRotation(i);

                // Rotation delta of the additive pose relative to bind.
                var delta = Quaternion.Inverse(bindRot) * addRot;
                var blended = Quaternion.Slerp(Quaternion.Identity, delta, w);
                @base.SetRotation(i, Quaternion.Normalize(baseRot * blended));

                // Translations/scales are already stored relative to bind (offsets).
                @base.SetTranslationOffset(i, @base.GetTranslationOffset(i) + additive.GetTranslationOffset(i) * w);
                var addScale = additive.GetScaleOffset(i);
                @base.SetScaleOffset(i, @base.GetScaleOffset(i) * Vector3.Lerp(Vector3.One, addScale, w));
            }
        }

        /// <summary>
        /// Blend a layer pose into a base pose in place, honouring the layer's mask and
        /// blending mode (override or additive). Used by <see cref="AnimatorController"/>.
        /// </summary>
        public static void BlendLayer(Skeleton skeleton, SkeletonPose @base, SkeletonPose layer,
                                      float weight, AvatarMask? mask, AnimatorLayerBlendingMode mode)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (layer == null) return;
            if (weight <= 0f) return;

            if (mode == AnimatorLayerBlendingMode.Additive)
            {
                BlendAdditive(skeleton, @base, layer, weight, mask);
                return;
            }

            // Override: lerp/slerp the masked bones toward the layer pose.
            var w = Math.Clamp(weight, 0f, 1f);
            var count = skeleton.Bones.Count;
            for (var i = 0; i < count; i++)
            {
                if (mask != null && !mask.GetBonePass(i, skeleton))
                    continue;

                @base.SetTranslationOffset(i, Vector3.Lerp(@base.GetTranslationOffset(i), layer.GetTranslationOffset(i), w));
                @base.SetScaleOffset(i, Vector3.Lerp(@base.GetScaleOffset(i), layer.GetScaleOffset(i), w));

                var ra = @base.GetRotation(i);
                var rb = layer.GetRotation(i);
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                @base.SetRotation(i, Quaternion.Normalize(Quaternion.Slerp(ra, rb, w)));
            }
        }

        /// <summary>
        /// Blend an arbitrary number of weighted poses. Weights are normalised internally.
        /// Rotations are accumulated with shortest-path sign correction and normalised.
        /// </summary>
        public static SkeletonPose BlendMany(Skeleton skeleton, IReadOnlyList<SkeletonPose> poses, IReadOnlyList<float> weights)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (poses == null || poses.Count == 0) return skeleton.CreateBindPose();
            if (weights == null || weights.Count != poses.Count)
                throw new ArgumentException("weights must match poses in length.", nameof(weights));

            var count = skeleton.Bones.Count;
            var transSum = new Vector3[count];
            var scaleSum = new Vector3[count];
            var rotSum = new Quaternion[count];
            var reference = new Quaternion[count];
            var hasRef = new bool[count];
            float total = 0f;

            for (var p = 0; p < poses.Count; p++)
            {
                var w = weights[p];
                if (w <= 0f) continue;
                var pose = poses[p];
                total += w;

                for (var i = 0; i < count; i++)
                {
                    transSum[i] += pose.GetTranslationOffset(i) * w;
                    scaleSum[i] += pose.GetScaleOffset(i) * w;

                    var q = pose.GetRotation(i);
                    if (!hasRef[i])
                    {
                        reference[i] = q;
                        hasRef[i] = true;
                    }
                    else if (Quaternion.Dot(reference[i], q) < 0f)
                    {
                        q = -q;
                    }
                    rotSum[i] += q * w;
                }
            }

            var result = skeleton.CreateBindPose();
            if (total <= 0f) return result;
            var inv = 1f / total;
            for (var i = 0; i < count; i++)
            {
                result.SetTranslationOffset(i, transSum[i] * inv);
                result.SetScaleOffset(i, scaleSum[i] * inv);
                var r = rotSum[i];
                var lenSq = r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W;
                if (lenSq > 1e-10f)
                    r *= 1f / MathF.Sqrt(lenSq);
                else
                    r = skeleton.Bones[i].LocalRotation;
                result.SetRotation(i, r);
            }
            return result;
        }
    }

    /// <summary>
    /// General-purpose helpers for converting between local and world bone transforms.
    /// These complement <see cref="ShanyuEngine.Skinning.Skeleton.ComputeWorldMatrices"/>
    /// by providing scale-safe rotation extraction and world-to-local writes.
    /// </summary>
    public static class PoseMath
    {
        public static Vector3 GetWorldPosition(Matrix4x4[] world, int index)
            => new(world[index].M41, world[index].M42, world[index].M43);

        /// <summary>
        /// World rotation of a bone, computed by walking the parent chain and composing
        /// local rotations. Avoids matrix-decomposition issues caused by non-uniform scale.
        /// </summary>
        public static Quaternion GetWorldRotation(Skeleton skeleton, SkeletonPose pose, int index)
        {
            var rot = pose.GetRotation(index);
            var p = skeleton.Bones[index].ParentIndex;
            while (p >= 0)
            {
                rot = pose.GetRotation(p) * rot;
                p = skeleton.Bones[p].ParentIndex;
            }
            return rot;
        }

        /// <summary>Set a bone's local rotation so its world rotation equals <paramref name="worldRot"/>.</summary>
        public static void SetWorldRotation(Skeleton skeleton, SkeletonPose pose, int index, Quaternion worldRot)
        {
            var parent = skeleton.Bones[index].ParentIndex;
            var local = parent >= 0
                ? Quaternion.Inverse(GetWorldRotation(skeleton, pose, parent)) * worldRot
                : worldRot;
            pose.SetRotation(index, Quaternion.Normalize(local));
        }

        /// <summary>Shortest-arc rotation mapping direction <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static Quaternion RotationFromTo(Vector3 from, Vector3 to)
        {
            var f = from.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(from);
            var t = to.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(to);

            var dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
            if (dot >= 0.999999f)
                return Quaternion.Identity;
            if (dot <= -0.999999f)
            {
                var axis = MathF.Abs(f.X) < 0.9f
                    ? Vector3.Normalize(Vector3.Cross(f, Vector3.UnitX))
                    : Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
                return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
            }

            var c = Vector3.Cross(f, t);
            var q = new Quaternion(c.X, c.Y, c.Z, 1f + dot);
            return Quaternion.Normalize(q);
        }

        /// <summary>Decompose a rotation into a swing (off-axis) and twist (around <paramref name="axis"/>) pair.</summary>
        public static (Quaternion swing, Quaternion twist) DecomposeSwingTwist(Quaternion q, Vector3 axis)
        {
            axis = Vector3.Normalize(axis);
            var v = new Vector3(q.X, q.Y, q.Z);
            var proj = axis * Vector3.Dot(v, axis);
            var twist = new Quaternion(proj.X, proj.Y, proj.Z, q.W);
            var lenSq = twist.X * twist.X + twist.Y * twist.Y + twist.Z * twist.Z + twist.W * twist.W;
            if (lenSq < 1e-10f)
                return (q, Quaternion.Identity);
            twist = Quaternion.Normalize(twist);
            var swing = q * Quaternion.Inverse(twist);
            return (swing, twist);
        }
    }

    // -------------------------------------------------------------------------
    // ECS integration
    // -------------------------------------------------------------------------

    /// <summary>
    /// ECS component binding an entity to an <see cref="Animator"/>. The animator (and any
    /// attached <see cref="AnimatorController"/>) is advanced each frame by the
    /// <see cref="AnimationUpdateSystem"/>.
    /// </summary>
    public struct AnimatorComponent : IComponent
    {
        public Animator Animator;
        /// <summary>When true, root-motion deltas are applied to the entity's TransformComponent.</summary>
        public bool ApplyRootMotionToTransform;
        public bool Enabled;
    }

    /// <summary>
    /// ECS system that ticks every <see cref="AnimatorComponent"/> each frame and optionally
    /// writes root motion into the owning entity's transform.
    /// </summary>
    public sealed class AnimationUpdateSystem : ISystem
    {
        public string Name => "AnimationSystem";

        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;
            var entities = ecs.QueryWithComponent<AnimatorComponent>();
            foreach (var id in entities)
            {
                var ac = ecs.GetComponent<AnimatorComponent>(id);
                if (!ac.Enabled || ac.Animator == null) continue;

                ac.Animator.Update(dt);

                if (ac.ApplyRootMotionToTransform && ac.Animator.ApplyRootMotion && ecs.HasComponent<TransformComponent>(id))
                {
                    var transform = ecs.GetComponent<TransformComponent>(id);
                    var rm = ac.Animator.RootMotion;
                    transform.Position += rm.PositionDelta;
                    transform.Rotation = Quaternion.Normalize(rm.RotationDelta * transform.Rotation);
                    ecs.SetComponent(id, transform);
                }

                ecs.SetComponent(id, ac);
            }
        }
    }

    /// <summary>
    /// 动画系统管理器。负责初始化和管理动画播放、混合树、状态机等子系统。
    /// 提供独立的 (非 ECS) Animator 注册与更新、动画剪辑引用计数管理、
    /// 全局动画时钟、以及 GPU 蒙皮资源生命周期控制。
    /// </summary>
    public sealed class AnimationSystem : IDisposable
    {
        private RHIDevice? _device;
        private RHICommandQueue? _graphicsQueue;

        /// <summary>已注册的独立 Animator (非 ECS 驱动)</summary>
        private readonly List<Animator> _standaloneAnimators = new();
        /// <summary>动画剪辑引用计数表 (用于内存管理)</summary>
        private readonly Dictionary<AnimationClip, int> _clipRefs = new();
        /// <summary>全局动画时钟 (秒)</summary>
        public float GlobalTime { get; private set; }
        /// <summary>是否已初始化</summary>
        public bool IsInitialized { get; private set; }
        /// <summary>是否已释放</summary>
        public bool IsDisposed { get; private set; }
        /// <summary>本帧处理的 Animator 数量 (调试用)</summary>
        public int LastUpdateAnimatorCount { get; private set; }
        /// <summary>本帧更新耗时 (毫秒, 调试用)</summary>
        public float LastUpdateMs { get; private set; }

        /// <summary>最大允许同时驻留的动画剪辑数 (超过时触发 LRU 淘汰)</summary>
        public int MaxResidentClips { get; set; } = 2048;

        /// <summary>
        /// 初始化动画系统。存储 RHI 设备引用, 为 GPU 蒙皮计算准备资源。
        /// </summary>
        public void Initialize(RHIDevice device, RHICommandQueue graphicsQueue)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (graphicsQueue == null) throw new ArgumentNullException(nameof(graphicsQueue));

            _device = device;
            _graphicsQueue = graphicsQueue;
            GlobalTime = 0f;
            IsInitialized = true;
            IsDisposed = false;
        }

        /// <summary>
        /// 注册一个独立 Animator, 使其每帧由本系统自动更新。
        /// </summary>
        public void RegisterAnimator(Animator animator)
        {
            if (animator == null) throw new ArgumentNullException(nameof(animator));
            if (!_standaloneAnimators.Contains(animator))
                _standaloneAnimators.Add(animator);
        }

        /// <summary>取消注册独立 Animator。</summary>
        public void UnregisterAnimator(Animator animator)
        {
            if (animator == null) return;
            _standaloneAnimators.Remove(animator);
        }

        /// <summary>
        /// 增加动画剪辑的引用计数。首次引用时注册到系统。
        /// </summary>
        public void RetainClip(AnimationClip clip)
        {
            if (clip == null) return;
            _clipRefs.TryGetValue(clip, out int count);
            _clipRefs[clip] = count + 1;
        }

        /// <summary>
        /// 减少动画剪辑的引用计数。计数降为 0 时从系统中移除, 允许 GC 回收。
        /// </summary>
        public void ReleaseClip(AnimationClip clip)
        {
            if (clip == null) return;
            if (!_clipRefs.TryGetValue(clip, out int count)) return;
            if (count <= 1)
                _clipRefs.Remove(clip);
            else
                _clipRefs[clip] = count - 1;
        }

        /// <summary>
        /// 每帧更新。推进全局时钟, 更新所有已注册的独立 Animator,
        /// 并在需要时执行剪辑内存淘汰。
        /// </summary>
        /// <param name="dt">帧间隔 (秒)</param>
        public void Update(float dt)
        {
            if (!IsInitialized || IsDisposed) return;
            if (dt <= 0f) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 推进全局动画时钟
            GlobalTime += dt;

            // 更新所有独立 Animator
            int count = 0;
            for (int i = 0; i < _standaloneAnimators.Count; i++)
            {
                var animator = _standaloneAnimators[i];
                if (animator == null) continue;
                animator.Update(dt);
                count++;
            }

            LastUpdateAnimatorCount = count;

            // 剪辑内存淘汰: 超过上限时移除引用计数为 0 的残留项
            if (_clipRefs.Count > MaxResidentClips)
            {
                var stale = new List<AnimationClip>();
                foreach (var kvp in _clipRefs)
                {
                    if (kvp.Value <= 0) stale.Add(kvp.Key);
                }
                foreach (var clip in stale)
                    _clipRefs.Remove(clip);
            }

            sw.Stop();
            LastUpdateMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// 释放动画系统占用的所有资源。清空 Animator 注册表、
        /// 剪辑引用计数表, 并解除 RHI 设备引用。
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed) return;

            _standaloneAnimators.Clear();
            _clipRefs.Clear();
            _device = null;
            _graphicsQueue = null;

            IsInitialized = false;
            IsDisposed = true;
        }
    }

    // -------------------------------------------------------------------------
    //  动画压缩 (AnimationCompression)
    //  提供基于误差阈值的关键帧抽取 (Keyframe Reduction) 和曲线拟合,
    //  支持平移/缩放轨道 (线性插值误差) 和旋转轨道 (SLERP 角度误差)。
    // -------------------------------------------------------------------------

    /// <summary>动画压缩配置参数。</summary>
    public sealed class AnimationCompressionSettings
    {
        /// <summary>平移轨道最大允许误差 (世界单位, 默认 0.1cm)</summary>
        public float MaxTranslationError { get; set; } = 0.001f;
        /// <summary>旋转轨道最大允许角度误差 (弧度, 默认 ~0.3°)</summary>
        public float MaxRotationError { get; set; } = 0.005f;
        /// <summary>缩放轨道最大允许误差</summary>
        public float MaxScaleError { get; set; } = 0.001f;
        /// <summary>是否保留首尾关键帧 (始终为 true 以保证边界精度)</summary>
        public bool PreserveEndKeys { get; set; } = true;
        /// <summary>最少关键帧数 (低于此数不压缩)</summary>
        public int MinKeysToCompress { get; set; } = 4;
    }

    /// <summary>动画压缩结果统计。</summary>
    public sealed class AnimationCompressionStats
    {
        public int OriginalTranslationKeys { get; set; }
        public int OriginalRotationKeys { get; set; }
        public int OriginalScaleKeys { get; set; }
        public int CompressedTranslationKeys { get; set; }
        public int CompressedRotationKeys { get; set; }
        public int CompressedScaleKeys { get; set; }

        public int TotalOriginal => OriginalTranslationKeys + OriginalRotationKeys + OriginalScaleKeys;
        public int TotalCompressed => CompressedTranslationKeys + CompressedRotationKeys + CompressedScaleKeys;
        public float CompressionRatio => TotalOriginal > 0 ? 1f - (float)TotalCompressed / TotalOriginal : 0f;

        public override string ToString() =>
            $"原始 {TotalOriginal} 关键帧 → 压缩后 {TotalCompressed} 关键帧 " +
            $"(压缩率 {CompressionRatio * 100f:F1}%)";
    }

    /// <summary>
    /// 动画压缩器。对 <see cref="AnimationClip"/> 的每条轨道执行关键帧抽取,
    /// 移除可由相邻关键帧线性/球面插值重建的冗余帧, 从而降低内存占用和采样开销。
    /// </summary>
    public static class AnimationCompression
    {
        // ---------------------------------------------------------------------
        //  公共 API
        // ---------------------------------------------------------------------

        /// <summary>
        /// 压缩动画剪辑的所有轨道。原地修改剪辑的关键帧列表。
        /// </summary>
        /// <param name="clip">要压缩的动画剪辑</param>
        /// <param name="settings">压缩参数 (null 使用默认值)</param>
        /// <returns>压缩统计信息</returns>
        public static AnimationCompressionStats Compress(AnimationClip clip,
            AnimationCompressionSettings? settings = null)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            settings ??= new AnimationCompressionSettings();

            var stats = new AnimationCompressionStats();

            foreach (var track in clip.Tracks)
            {
                // 平移轨道
                if (track.TranslationKeys.Count >= settings.MinKeysToCompress)
                {
                    stats.OriginalTranslationKeys += track.TranslationKeys.Count;
                    track.TranslationKeys = ReduceVectorKeys(
                        track.TranslationKeys, settings.MaxTranslationError, settings);
                    stats.CompressedTranslationKeys += track.TranslationKeys.Count;
                }
                else
                {
                    stats.OriginalTranslationKeys += track.TranslationKeys.Count;
                    stats.CompressedTranslationKeys += track.TranslationKeys.Count;
                }

                // 旋转轨道
                if (track.RotationKeys.Count >= settings.MinKeysToCompress)
                {
                    stats.OriginalRotationKeys += track.RotationKeys.Count;
                    track.RotationKeys = ReduceRotationKeys(
                        track.RotationKeys, settings.MaxRotationError, settings);
                    stats.CompressedRotationKeys += track.RotationKeys.Count;
                }
                else
                {
                    stats.OriginalRotationKeys += track.RotationKeys.Count;
                    stats.CompressedRotationKeys += track.RotationKeys.Count;
                }

                // 缩放轨道
                if (track.ScaleKeys.Count >= settings.MinKeysToCompress)
                {
                    stats.OriginalScaleKeys += track.ScaleKeys.Count;
                    track.ScaleKeys = ReduceScaleKeys(
                        track.ScaleKeys, settings.MaxScaleError, settings);
                    stats.CompressedScaleKeys += track.ScaleKeys.Count;
                }
                else
                {
                    stats.OriginalScaleKeys += track.ScaleKeys.Count;
                    stats.CompressedScaleKeys += track.ScaleKeys.Count;
                }
            }

            return stats;
        }

        // ---------------------------------------------------------------------
        //  平移 / 缩放轨道关键帧抽取 (线性插值误差)
        // ---------------------------------------------------------------------

        /// <summary>
        /// 对 Vector3 关键帧序列执行 Douglas-Peucker 风格的关键帧抽取。
        /// 保留首尾帧, 递归地移除可由线性插值在误差阈值内重建的中间帧。
        /// </summary>
        private static List<VectorKeyframe> ReduceVectorKeys(
            List<VectorKeyframe> keys, float maxError, AnimationCompressionSettings settings)
        {
            if (keys.Count <= 2) return new List<VectorKeyframe>(keys);

            // 保留标记: true = 保留该关键帧
            var keep = new bool[keys.Count];
            keep[0] = true;
            keep[keys.Count - 1] = true;

            // 递归标记需要保留的关键帧
            MarkKeepVector(keys, keep, 0, keys.Count - 1, maxError);

            // 构建压缩后的列表
            var result = new List<VectorKeyframe>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                if (keep[i])
                    result.Add(keys[i]);
            }

            // 确保首尾帧被保留
            if (settings.PreserveEndKeys)
            {
                if (result.Count == 0 || result[0].TimeSeconds != keys[0].TimeSeconds)
                    result.Insert(0, keys[0]);
                if (result.Count == 0 || result[^1].TimeSeconds != keys[^1].TimeSeconds)
                    result.Add(keys[^1]);
            }

            return result;
        }

        /// <summary>递归地标记区间 (start, end) 中需要保留的关键帧。</summary>
        private static void MarkKeepVector(
            List<VectorKeyframe> keys, bool[] keep, int start, int end, float maxError)
        {
            if (end - start <= 1) return;

            float maxDist = 0f;
            int maxIdx = start;

            var p0 = keys[start];
            var p1 = keys[end];

            float timeSpan = p1.TimeSeconds - p0.TimeSeconds;
            if (timeSpan <= 0f) return;

            // 遍历区间内的每个中间关键帧, 计算其到线性插值值的偏差
            for (int i = start + 1; i < end; i++)
            {
                var pk = keys[i];
                float alpha = (pk.TimeSeconds - p0.TimeSeconds) / timeSpan;

                // 线性插值重建值
                var interp = Vector3.Lerp(p0.Value, p1.Value, alpha);

                // 计算误差 (欧氏距离)
                float dist = Vector3.Distance(interp, pk.Value);

                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIdx = i;
                }
            }

            // 如果最大误差超过阈值, 保留该关键帧并递归处理左右子区间
            if (maxDist > maxError)
            {
                keep[maxIdx] = true;
                MarkKeepVector(keys, keep, start, maxIdx, maxError);
                MarkKeepVector(keys, keep, maxIdx, end, maxError);
            }
        }

        // ---------------------------------------------------------------------
        //  缩放轨道关键帧抽取 (复用 Vector 逻辑, 使用 ScaleKeyframe)
        // ---------------------------------------------------------------------

        private static List<ScaleKeyframe> ReduceScaleKeys(
            List<ScaleKeyframe> keys, float maxError, AnimationCompressionSettings settings)
        {
            if (keys.Count <= 2) return new List<ScaleKeyframe>(keys);

            var keep = new bool[keys.Count];
            keep[0] = true;
            keep[keys.Count - 1] = true;

            MarkKeepScale(keys, keep, 0, keys.Count - 1, maxError);

            var result = new List<ScaleKeyframe>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                if (keep[i])
                    result.Add(keys[i]);
            }

            if (settings.PreserveEndKeys)
            {
                if (result.Count == 0 || result[0].TimeSeconds != keys[0].TimeSeconds)
                    result.Insert(0, keys[0]);
                if (result.Count == 0 || result[^1].TimeSeconds != keys[^1].TimeSeconds)
                    result.Add(keys[^1]);
            }

            return result;
        }

        private static void MarkKeepScale(
            List<ScaleKeyframe> keys, bool[] keep, int start, int end, float maxError)
        {
            if (end - start <= 1) return;

            float maxDist = 0f;
            int maxIdx = start;

            var p0 = keys[start];
            var p1 = keys[end];
            float timeSpan = p1.TimeSeconds - p0.TimeSeconds;
            if (timeSpan <= 0f) return;

            for (int i = start + 1; i < end; i++)
            {
                var pk = keys[i];
                float alpha = (pk.TimeSeconds - p0.TimeSeconds) / timeSpan;
                var interp = Vector3.Lerp(p0.Value, p1.Value, alpha);
                float dist = Vector3.Distance(interp, pk.Value);

                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIdx = i;
                }
            }

            if (maxDist > maxError)
            {
                keep[maxIdx] = true;
                MarkKeepScale(keys, keep, start, maxIdx, maxError);
                MarkKeepScale(keys, keep, maxIdx, end, maxError);
            }
        }

        // ---------------------------------------------------------------------
        //  旋转轨道关键帧抽取 (SLERP 角度误差)
        // ---------------------------------------------------------------------

        /// <summary>
        /// 对四元数旋转关键帧序列执行关键帧抽取。
        /// 保留首尾帧, 递归地移除可由 SLERP 在角度误差阈值内重建的中间帧。
        /// </summary>
        private static List<RotationKeyframe> ReduceRotationKeys(
            List<RotationKeyframe> keys, float maxAngleError, AnimationCompressionSettings settings)
        {
            if (keys.Count <= 2) return new List<RotationKeyframe>(keys);

            var keep = new bool[keys.Count];
            keep[0] = true;
            keep[keys.Count - 1] = true;

            MarkKeepRotation(keys, keep, 0, keys.Count - 1, maxAngleError);

            var result = new List<RotationKeyframe>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                if (keep[i])
                    result.Add(keys[i]);
            }

            if (settings.PreserveEndKeys)
            {
                if (result.Count == 0 || result[0].TimeSeconds != keys[0].TimeSeconds)
                    result.Insert(0, keys[0]);
                if (result.Count == 0 || result[^1].TimeSeconds != keys[^1].TimeSeconds)
                    result.Add(keys[^1]);
            }

            return result;
        }

        /// <summary>递归地标记区间 (start, end) 中需要保留的旋转关键帧。</summary>
        private static void MarkKeepRotation(
            List<RotationKeyframe> keys, bool[] keep, int start, int end, float maxAngleError)
        {
            if (end - start <= 1) return;

            float maxAngle = 0f;
            int maxIdx = start;

            var q0 = keys[start].Value;
            var q1 = keys[end].Value;

            float timeSpan = keys[end].TimeSeconds - keys[start].TimeSeconds;
            if (timeSpan <= 0f) return;

            for (int i = start + 1; i < end; i++)
            {
                var pk = keys[i];
                float alpha = (pk.TimeSeconds - keys[start].TimeSeconds) / timeSpan;

                // SLERP 重建旋转
                var interp = Quaternion.Slerp(q0, q1, alpha);

                // 计算角度误差: 2 * acos(|dot|)
                float dot = MathF.Abs(Vector4.Dot(
                    new Vector4(interp.X, interp.Y, interp.Z, interp.W),
                    new Vector4(pk.Value.X, pk.Value.Y, pk.Value.Z, pk.Value.W)));
                dot = MathF.Min(1f, dot);
                float angle = 2f * MathF.Acos(dot);

                if (angle > maxAngle)
                {
                    maxAngle = angle;
                    maxIdx = i;
                }
            }

            if (maxAngle > maxAngleError)
            {
                keep[maxIdx] = true;
                MarkKeepRotation(keys, keep, start, maxIdx, maxAngleError);
                MarkKeepRotation(keys, keep, maxIdx, end, maxAngleError);
            }
        }
    }
}
