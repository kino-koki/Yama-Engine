using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Animation;
using YamaEngine.Animation.FootIK;
using Skeleton = YamaEngine.Animation.Skeleton;

namespace YamaEngine.NPC.AiNpc;

/// <summary>
/// Unity5 Mecanim 级别的 NPC 姿态后处理器。
///
/// 在 AnimationRuntime 的 <c>PosePostProcess</c> 阶段执行（动画采样后、ComputeBoneMatrices 前），
/// 依次叠加三层与 Unity 对应的能力：
///
///   1. 足部 IK（foot planting）—— 对应 Unity OnAnimatorIK 的 LeftFoot/RightFoot IKGoal，
///      由 Kimodo 的 foot_contacts 驱动落脚权重，接触时把脚踝贴合地面并补偿髋部下沉。
///   2. 头部注视 IK（look-at）—— 对应 Unity LookAt + Head Turn，把注视目标分配到
///      胸/颈/头链上，超出 FOV 时按角度衰减，避免出现"断脖子"式硬转。
///   3. 上半身手势层（AvatarMask 分层）—— 对应 Unity Base + Gesture 双层结构，
///      用上半身 AvatarMask 把说话/思考时的程序化微动作限制在骨盆以上，
///      不污染行走腿部的根运动。
///
/// 所有三层都通过全局权重门控，可独立关闭，退化时等价于纯采样姿态。
/// </summary>
public sealed class AiNpcMotionPostProcessor
{
    private readonly AiNpcRigConfig _config;
    private readonly Skeleton? _skeleton;

    // ── 足部 IK ──
    private readonly FootIKSolver _footSolver = new();
    private bool _footInitialized;

    // ── 注视 IK ──
    private readonly LookAtIKSolver _lookAt = new();
    private Vector3 _smoothedLookTarget;
    private bool _lookTargetInitialized;

    // ── 手势层 ──
    private AvatarMask? _upperBodyMask;
    private float _gesturePhase;
    private float _breathPhase;

    public AiNpcMotionPostProcessor(AiNpcRigConfig config, Skeleton? skeleton)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _skeleton = skeleton;
        if (skeleton != null)
        {
            ConfigureFootIk(skeleton);
            ConfigureLookAt(skeleton);
            ConfigureGestureMask(skeleton);
        }
    }

    /// <summary>足部 IK 求解器（暴露以便外部接入真实物理地面探测）。</summary>
    public FootIKSolver FootSolver => _footSolver;

    /// <summary>
    /// 后处理一帧姿态。
    /// </summary>
    /// <param name="instance">动画实例（Owner 与 Skeleton 用于世界变换）。</param>
    /// <param name="lookAtTargetWorld">注视目标世界坐标；null 时注视目标保持上一次。</param>
    /// <param name="leftFootContact">左脚接触权重 [0,1]，1=触地。</param>
    /// <param name="rightFootContact">右脚接触权重 [0,1]，1=触地。</param>
    /// <param name="gestureAmount">手势层激活量 [0,1]（说话/思考时升高）。</param>
    /// <param name="deltaTime">帧间隔（秒）。</param>
    public void Process(
        AnimationInstance instance,
        Vector3? lookAtTargetWorld,
        float leftFootContact,
        float rightFootContact,
        float gestureAmount,
        float deltaTime)
    {
        if (instance?.Skeleton == null || _skeleton == null) return;
        var pose = instance.Animator.CurrentPose;
        if (pose == null) return;

        var owner = instance.Owner;
        var componentTransform =
            Matrix4x4.CreateFromQuaternion(owner?.Rotation ?? Quaternion.Identity) *
            Matrix4x4.CreateTranslation(owner?.Position ?? Vector3.Zero);

        // 顺序：先手势层（改上半身姿态）→ 再足部 IK（改腿部+髋）→ 最后注视 IK（改头颈）。
        // 注视放最后保证头部朝向不被身体层级覆盖。
        if (_config.GestureLayerWeight > 0f && gestureAmount > 0f)
            ApplyGestureLayer(pose, componentTransform, gestureAmount, deltaTime);

        if (_config.EnableFootIk && _config.FootIkWeight > 0f)
            ApplyFootIk(instance.Skeleton, pose, leftFootContact, rightFootContact, deltaTime);

        if (_config.EnableLookAt && _config.LookAtWeight > 0f)
            ApplyLookAt(pose, componentTransform, lookAtTargetWorld, deltaTime);
    }

    // ───────────────────────── 足部 IK ─────────────────────────

    private void ConfigureFootIk(Skeleton skeleton)
    {
        _footSolver.TotalWeight = _config.FootIkWeight;
        _footSolver.FootOffset = _config.FootOffset;
        _footSolver.EnableHipAdjustment = _config.EnableHipAdjustment;
        // 无物理地面探测时用平地占位；接入真实物理时外部可替换 FootSolver.GroundProbe。
        _footSolver.GroundProbe = new FlatGroundProbe { GroundHeight = 0f };

        _footSolver.Legs.Clear();
        TryAddLeg(skeleton, _config.LeftFootBone, "LeftLowerLeg", "LeftUpperLeg");
        TryAddLeg(skeleton, _config.RightFootBone, "RightLowerLeg", "RightUpperLeg");
        _footInitialized = _footSolver.Legs.Count > 0;
    }

    private void TryAddLeg(Skeleton skeleton, string footName, string calfName, string thighName)
    {
        int foot = ResolveBone(skeleton, footName);
        int calf = ResolveBone(skeleton, calfName);
        int thigh = ResolveBone(skeleton, thighName);
        if (foot < 0 || calf < 0 || thigh < 0) return;
        // 要求 髋→膝→踝 的父子关系成立，否则 TwoBoneIK 无法求解。
        if (skeleton.Bones[calf].ParentIndex != thigh) return;
        if (skeleton.Bones[foot].ParentIndex != calf) return;

        _footSolver.Legs.Add(new FootIKLeg
        {
            Setup = new FootIKLegSetup
            {
                ThighIndex = thigh,
                CalfIndex = calf,
                FootIndex = foot,
                ToeIndex = -1
            },
            TargetWeight = 1f,
            FootOffset = 0f
        });
    }

    private void ApplyFootIk(Skeleton skeleton, SkeletonPose pose, float leftContact, float rightContact, float dt)
    {
        if (!_footInitialized || _footSolver.Legs.Count == 0) return;

        // 由 Kimodo foot_contacts 驱动每条腿的落脚权重；触地=1，腾空=0。
        if (_footSolver.Legs.Count > 0)
            _footSolver.Legs[0].TargetWeight = Math.Clamp(leftContact, 0f, 1f);
        if (_footSolver.Legs.Count > 1)
            _footSolver.Legs[1].TargetWeight = Math.Clamp(rightContact, 0f, 1f);

        _footSolver.TotalWeight = _config.FootIkWeight;
        _footSolver.Solve(skeleton, pose, dt);
    }

    /// <summary>从 Kimodo 已转换动作提取左右脚接触权重。</summary>
    public static (float Left, float Right) ExtractFootContacts(KimodoConvertedMotion? motion, float elapsed, float fps)
    {
        var contacts = motion?.FootContacts;
        if (contacts == null || contacts.Length == 0)
            return (1f, 1f); // 无接触数据时按全程触地处理。

        int frames = contacts.Length;
        int index = Math.Clamp((int)(elapsed * fps), 0, frames - 1);
        var frame = contacts[index];
        if (frame == null || frame.Length < 4) return (1f, 1f);

        // 布局：[左跟, 左掌, 右跟, 右掌]；任一接触即视为该脚触地。
        float left = Math.Max(frame[0], frame[1]);
        float right = Math.Max(frame[2], frame[3]);
        return (Math.Clamp(left, 0f, 1f), Math.Clamp(right, 0f, 1f));
    }

    // ───────────────────────── 注视 IK ─────────────────────────

    private void ConfigureLookAt(Skeleton skeleton)
    {
        var chain = new List<int>();
        // 显式链优先；否则用默认 胸→颈→头。
        var candidates = _config.LookAtChainBones is { Length: > 0 } custom
            ? custom
            : new[] { "Chest", "Neck", "Head" };

        foreach (var name in candidates)
        {
            int idx = ResolveBone(skeleton, name);
            if (idx >= 0) chain.Add(idx);
        }
        // 保证末端是头骨。
        int head = ResolveBone(skeleton, _config.HeadBone);
        if (head >= 0 && (chain.Count == 0 || chain[^1] != head)) chain.Add(head);
        if (chain.Count == 0) return;

        _lookAt.Chain = chain;
        _lookAt.ForwardAxis = Vector3.UnitZ;
        _lookAt.MaxAnglePerBone = _config.LookAtMaxAngleDegrees * (MathF.PI / 180f);
    }

    private void ApplyLookAt(SkeletonPose pose, Matrix4x4 componentTransform, Vector3? targetWorld, float dt)
    {
        if (_lookAt.Chain.Count == 0) return;

        // 平滑注视目标，避免目标频繁跳变导致头部抖动。
        if (targetWorld.HasValue)
        {
            var t = targetWorld.Value;
            if (!_lookTargetInitialized)
            {
                _smoothedLookTarget = t;
                _lookTargetInitialized = true;
            }
            else
            {
                float alpha = 1f - MathF.Exp(-8f * MathF.Max(dt, 1e-4f));
                _smoothedLookTarget = Vector3.Lerp(_smoothedLookTarget, t, alpha);
            }
        }
        else if (!_lookTargetInitialized)
        {
            return;
        }

        // FOV 衰减：目标超出视野半角时线性减弱权重，避免硬扭脖子。
        var headWorld = GetChainTipWorld(pose, componentTransform);
        var toTarget = _smoothedLookTarget - headWorld;
        float dist = toTarget.Length();
        if (dist < 1e-4f) return;

        var desired = toTarget / dist;
        var bodyForward = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, componentTransform));
        // 仅比较水平朝向，避免上下看时误判为超视野。
        var flatDesired = Vector3.Normalize(new Vector3(desired.X, 0f, desired.Z));
        var flatForward = Vector3.Normalize(new Vector3(bodyForward.X, 0f, bodyForward.Z));
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(flatDesired, flatForward), -1f, 1f));

        float fovHalf = Math.Clamp(_config.LookAtFovDegrees, 10f, 170f) * (MathF.PI / 180f);
        float fovFactor = 1f - Math.Clamp((angle - fovHalf * 0.75f) / (fovHalf * 0.25f), 0f, 1f);

        _lookAt.Weight = _config.LookAtWeight * fovFactor;
        _lookAt.Target = _smoothedLookTarget;
        _lookAt.Solve(pose, componentTransform);
    }

    private static Vector3 GetChainTipWorld(SkeletonPose pose, Matrix4x4 componentTransform)
    {
        var skeleton = pose.Skeleton!;
        var world = skeleton.ComputeWorldMatrices(pose);
        if (world.Length == 0) return Vector3.Zero;
        // 头骨世界位置 = 组件变换 * 头骨局部到世界。
        int head = skeleton.FindBone("Head");
        int tip = head >= 0 ? head : world.Length - 1;
        return Vector3.Transform(world[tip].Translation, componentTransform);
    }

    // ───────────────────────── 上半身手势层 ─────────────────────────

    private void ConfigureGestureMask(Skeleton skeleton)
    {
        int pelvis = ResolveBone(skeleton, _config.PelvisBone);
        if (pelvis < 0)
        {
            _upperBodyMask = null;
            return;
        }
        // 上半身掩码：禁用骨盆及其所有子骨骼（腿部/根），保留脊柱以上。
        _upperBodyMask = AvatarMask.UpperBody(skeleton, _config.PelvisBone);
    }

    private void ApplyGestureLayer(SkeletonPose pose, Matrix4x4 componentTransform, float amount, float dt)
    {
        if (_upperBodyMask == null || _skeleton == null) return;

        _gesturePhase += dt * 1.7f;
        _breathPhase += dt * (MathF.Tau / 3.4f); // ~3.4s 呼吸周期

        float weight = Math.Clamp(_config.GestureLayerWeight * amount, 0f, 1f);
        if (weight <= 1e-4f) return;

        // 程序化微动作：呼吸起伏（脊柱轻微俯仰）+ 手势相位（手臂轻微摆动）。
        // 这些都是叠加在小角度上的正弦，不会破坏根运动，且被 AvatarMask 限制在上半身。
        float breath = MathF.Sin(_breathPhase) * 0.035f * weight;
        float gestureSway = MathF.Sin(_gesturePhase) * 0.05f * weight;

        ApplyMaskedAdditive(pose, "Spine", Vector3.UnitX, breath, _upperBodyMask);
        ApplyMaskedAdditive(pose, "Chest", Vector3.UnitX, breath * 0.6f, _upperBodyMask);
        ApplyMaskedAdditive(pose, "Chest", Vector3.UnitZ, gestureSway * 0.5f, _upperBodyMask);
        // 手臂随说话轻微摆动，让"边说边僵"变成自然的手势。
        ApplyMaskedAdditive(pose, "LeftUpperArm", Vector3.UnitY, gestureSway, _upperBodyMask);
        ApplyMaskedAdditive(pose, "RightUpperArm", Vector3.UnitY, -gestureSway, _upperBodyMask);
    }

    private void ApplyMaskedAdditive(SkeletonPose pose, string boneName, Vector3 axis, float angleRad, AvatarMask mask)
    {
        int bone = ResolveBone(_skeleton!, boneName);
        if (bone < 0) return;
        if (!mask.GetBonePass(bone, _skeleton!)) return;

        var baseRot = pose.GetRotation(bone);
        pose.SetRotation(bone, baseRot * Quaternion.CreateFromAxisAngle(axis, angleRad));
    }

    // ───────────────────────── helpers ─────────────────────────

    private static int ResolveBone(Skeleton skeleton, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        return skeleton.FindBone(name);
    }
}
