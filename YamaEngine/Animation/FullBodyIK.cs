// ============================================================================
// FullBodyIK.cs
// yama游戏引擎 - Full Body IK + 脚部锁定 + 脚部放置（UE5.8 深度级 任务B）
// 在单链 IK（IKSolverReal）之上补齐三个 UE5 深度级能力：
//   1. FullBodyIKSolver —— 多链（腿/手臂/脊柱）FABRIK 求解，按优先级迭代
//   2. FootLockSystem    —— 脚部锁定曲线（锁定/解锁区间），锁定期间保持世界位置
//   3. FootPlacementSystem —— 程序化脚部放置（步频/步幅/周期，交替目标点）
// 本层不依赖图形/编辑器，纯数据与算法，可独立单元测试。
// 注：统一使用 YamaEngine.Skinning.Skeleton/SkeletonPose（全限定，避免命名空间遮蔽）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Animation.FullBodyIKSystem;

// ───────────────────────────────────────────────────────────────────────────
// Full Body IK（多链 FABRIK，按优先级）
// ───────────────────────────────────────────────────────────────────────────

/// <summary>一条 IK 链：根→末端骨骼索引序列 + 端到端目标 + 优先级。</summary>
public sealed class FullBodyIKChain
{
    /// <summary>链上骨骼索引（根→末端）。</summary>
    public int[] BoneIndices = Array.Empty<int>();
    /// <summary>世界空间端到端目标。</summary>
    public Vector3 TargetPosition;
    /// <summary>弯曲/极向偏好（世界空间，可选）。</summary>
    public Vector3 PoleVector = Vector3.Zero;
    /// <summary>混合权重 [0..1]。</summary>
    public float Weight = 1f;
    /// <summary>优先级（低先解，高后解覆盖）。</summary>
    public int Priority = 0;
    /// <summary>FABRIK 迭代次数。</summary>
    public int Iterations = 8;

    public bool IsValid => BoneIndices.Length >= 2;
}

/// <summary>
/// Full Body IK 求解器：多链 FABRIK，按优先级迭代，逐链解算后写回骨骼局部旋转。
/// 复用 Skinning.Skeleton/SkeletonPose。
/// </summary>
public sealed class FullBodyIKSolver
{
    public List<FullBodyIKChain> Chains { get; } = new();
    /// <summary>全局迭代次数（跨链收敛）。</summary>
    public int MaxPasses = 4;
    public float Damping = 0.001f;

    private readonly List<Vector3> _positions = new();
    private readonly List<float> _lengths = new();

    /// <summary>求解并把结果写入 pose（骨骼局部旋转）。</summary>
    public void Solve(YamaEngine.Skinning.Skeleton skeleton, YamaEngine.Skinning.SkeletonPose pose, float deltaTime)
    {
        if (skeleton == null || pose == null) return;

        // 按优先级升序排序（低先解）
        var sorted = new List<FullBodyIKChain>(Chains);
        sorted.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        foreach (var chain in sorted)
        {
            if (!chain.IsValid || chain.Weight <= 0f) continue;
            SolveChain(skeleton, pose, chain);
        }
    }

    private void SolveChain(YamaEngine.Skinning.Skeleton skeleton, YamaEngine.Skinning.SkeletonPose pose, FullBodyIKChain chain)
    {
        var bones = chain.BoneIndices;
        int n = bones.Length;
        if (n < 2) return;

        // 当前世界位置
        var world = skeleton.ComputeWorldMatrices(pose);
        _positions.Clear();
        _lengths.Clear();
        for (var i = 0; i < n; i++)
        {
            var idx = bones[i];
            var pos = world[idx].Translation;
            _positions.Add(pos);
            if (i > 0)
            {
                var prev = world[bones[i - 1]].Translation;
                _lengths.Add(Vector3.Distance(pos, prev));
            }
        }

        var rootStart = _positions[0];
        var target = chain.TargetPosition;

        // FABRIK 迭代
        for (var iter = 0; iter < chain.Iterations; iter++)
        {
            // 后向：末端到目标
            _positions[n - 1] = target;
            for (var i = n - 2; i >= 0; i--)
            {
                var dir = _positions[i] - _positions[i + 1];
                var len = dir.Length();
                if (len < Damping) dir = Vector3.UnitY;
                else dir /= len;
                _positions[i] = _positions[i + 1] + dir * _lengths[i];
            }
            // 前向：根回原位
            _positions[0] = rootStart;
            for (var i = 0; i < n - 1; i++)
            {
                var dir = _positions[i + 1] - _positions[i];
                var len = dir.Length();
                if (len < Damping) dir = Vector3.UnitY;
                else dir /= len;
                _positions[i + 1] = _positions[i] + dir * _lengths[i];
            }
        }

        // 写回：顺序调整每个父骨骼的旋转（父先子后，每步用最新世界矩阵），
        // 使其子骨骼位置到达 FABRIK 解算位置。
        for (var i = 1; i < n; i++)
        {
            world = skeleton.ComputeWorldMatrices(pose);   // 更新（父骨骼已调整）
            var pIdx = bones[i - 1];
            var cIdx = bones[i];
            var grandParent = skeleton.Bones[pIdx].ParentIndex;
            var gpRot = grandParent >= 0 ? IKMath.ExtractRotation(world[grandParent]) : Quaternion.Identity;
            var pRot = IKMath.ExtractRotation(world[pIdx]);

            // child 相对 parent 的绑定方向（父空间）
            var bindChild = skeleton.Bones[cIdx].LocalPosition;
            if (bindChild.LengthSquared() < 1e-10f) bindChild = new Vector3(0, 1, 0);
            else bindChild = Vector3.Normalize(bindChild);
            var bindChildWorld = Vector3.Transform(bindChild, pRot);

            var targetDir = _positions[i] - _positions[i - 1];
            if (targetDir.LengthSquared() < 1e-10f) continue;
            targetDir = Vector3.Normalize(targetDir);

            var delta = IKMath.RotationFromTo(bindChildWorld, targetDir);
            var newWorldRot = delta * pRot;
            var newLocalRot = Quaternion.Inverse(gpRot) * newWorldRot;
            pose.SetRotation(pIdx, Quaternion.Slerp(pose.GetRotation(pIdx), newLocalRot, Math.Clamp(chain.Weight, 0f, 1f)));
        }
    }
}

// ───────────────────────────────────────────────────────────────────────────
// 脚部锁定（Foot Locking）—— 锁定曲线 + 保持世界位置
// ───────────────────────────────────────────────────────────────────────────

/// <summary>脚部锁定关键帧：时间点 + 是否锁定。</summary>
public sealed class FootLockKey
{
    public float Time;
    public bool Locked;
    public FootLockKey() { }
    public FootLockKey(float time, bool locked) { Time = time; Locked = locked; }
}

/// <summary>单只脚的锁定轨道。</summary>
public sealed class FootLockTrack
{
    public int FootBoneIndex = -1;
    /// <summary>大腿-小腿-脚链（用于锁定期间保持位置）。</summary>
    public int ThighIndex = -1;
    public int CalfIndex = -1;
    public List<FootLockKey> Keys = new();
    /// <summary>锁定时的世界位置（由系统在锁定时捕获）。</summary>
    public Vector3 LockedWorldPosition;
    public bool IsLockedAt(float time)
    {
        if (Keys.Count == 0) return false;
        for (var i = 0; i < Keys.Count; i++)
            if (MathF.Abs(Keys[i].Time - time) < 1e-4f) return Keys[i].Locked;
        // 取最接近的时间点
        var closest = Keys[0];
        foreach (var k in Keys)
            if (MathF.Abs(k.Time - time) < MathF.Abs(closest.Time - time)) closest = k;
        return closest.Locked;
    }

    public bool HasKeys => Keys.Count > 0;
}

/// <summary>
/// 脚部锁定系统：在锁定区间内保持脚部骨骼的世界位置不变（用腿链两骨 IK 达位）。
/// </summary>
public sealed class FootLockSystem
{
    public List<FootLockTrack> Tracks { get; } = new();
    /// <summary>IK 权重（锁定强度）。</summary>
    public float Weight = 1f;
    /// <summary>每次求解的 FABRIK 迭代次数。</summary>
    public int Iterations = 16;

    private readonly FullBodyIKSolver _ik = new();

    /// <summary>求解。animTime 为动画时间，用于查询锁定区间。</summary>
    public void Solve(YamaEngine.Skinning.Skeleton skeleton, YamaEngine.Skinning.SkeletonPose pose, float animTime)
    {
        if (skeleton == null || pose == null) return;
        foreach (var track in Tracks)
        {
            if (!track.HasKeys) continue;
            if (track.FootBoneIndex < 0) continue;
            if (!track.IsLockedAt(animTime)) continue;

            // 捕获锁定位置（首次锁定时）
            var world = skeleton.ComputeWorldMatrices(pose);
            if (track.LockedWorldPosition == Vector3.Zero)
                track.LockedWorldPosition = world[track.FootBoneIndex].Translation;

            // 用腿链 FABRIK 保持脚部在锁定位置（复用已验证的 FullBodyIKSolver 写回）
            if (track.ThighIndex >= 0 && track.CalfIndex >= 0)
            {
                _ik.Chains.Clear();
                _ik.Chains.Add(new FullBodyIKChain
                {
                    BoneIndices = new[] { track.ThighIndex, track.CalfIndex, track.FootBoneIndex },
                    TargetPosition = track.LockedWorldPosition,
                    Weight = Weight,
                    Iterations = Iterations
                });
                _ik.Solve(skeleton, pose, 1f / 60f);
            }
        }
    }
}

// ───────────────────────────────────────────────────────────────────────────
// 脚部放置（Foot Placement）—— 程序化步态目标点
// ───────────────────────────────────────────────────────────────────────────

/// <summary>单只脚的放置状态。</summary>
public sealed class FootPlacementState
{
    public int FootBoneIndex = -1;
    public Vector3 HomePosition;       // 绑定/原位
    public Vector3 CurrentTarget;      // 当前目标点
    public Vector3 PreviousTarget;     // 上一目标点
    public bool Placed;                // 本步已放置
    public float Phase;                // 步态相位 [0..1]
}

/// <summary>
/// 程序化脚部放置：按步频/步幅/周期生成交替脚部目标点（相对根运动）。
/// 用于无动画数据时的步态驱动，或作为 IK 目标来源。
/// </summary>
public sealed class FootPlacementSystem
{
    public List<FootPlacementState> Feet { get; } = new();
    /// <summary>步频（步/秒，每只脚）。</summary>
    public float StepFrequency = 2f;
    /// <summary>步幅（米）。</summary>
    public float StepLength = 0.6f;
    /// <summary>脚部抬升高度。</summary>
    public float LiftHeight = 0.1f;
    /// <summary>侧向间距（双足分开）。</summary>
    public float StanceWidth = 0.2f;
    public Vector3 Up = Vector3.UnitY;
    public Vector3 Forward = Vector3.UnitZ;

    private float _phase;
    private bool _leftPlaced;

    /// <summary>添加一只脚（footBoneIndex + 原位）。</summary>
    public FootPlacementState AddFoot(int footBoneIndex, Vector3 homePosition)
    {
        var s = new FootPlacementState { FootBoneIndex = footBoneIndex, HomePosition = homePosition, CurrentTarget = homePosition };
        Feet.Add(s);
        return s;
    }

    /// <summary>推进步态并更新每只脚的目标点。deltaTime 秒。</summary>
    public void Update(float deltaTime, Vector3 rootWorldPosition)
    {
        _phase += deltaTime * StepFrequency;
        if (_phase >= 1f)
        {
            _phase -= 1f;
            // 周期结束：交换左右相位
            _leftPlaced = !_leftPlaced;
            foreach (var f in Feet) f.Placed = false;
        }

        var lateral = Vector3.Cross(Forward, Up);
        if (lateral.LengthSquared() < 1e-8f) lateral = Vector3.UnitX;
        lateral = Vector3.Normalize(lateral);

        for (var i = 0; i < Feet.Count; i++)
        {
            var foot = Feet[i];
            var side = i == 0 ? -0.5f : 0.5f;
            // 本脚的目标 = 根位置 + 前向步幅（步态相位偏移）+ 侧向
            var offset = Forward * (StepLength * (0.5f - _phase)) * (i == 0 ? 1f : -1f)
                         + lateral * side * StanceWidth
                         + Up * (LiftHeight * MathF.Sin(_phase * MathF.PI) * (i == 0 ? 1f : 1f));
            foot.CurrentTarget = rootWorldPosition + offset;
        }
    }

    /// <summary>当前某只脚的步态相位（供动画同步）。</summary>
    public float GetPhase(int footIndex) => footIndex == 0 ? _phase : 1f - _phase;
}

/// <summary>复用 FootIKSystem 的两骨 IK 解算（纯几何，返回关节位置）。</summary>
public static class TwoBoneIK
{
    public static Vector3[] Solve(Vector3 rootPos, Vector3 midPos, Vector3 endPos, Vector3 target, Vector3 poleTarget)
    {
        float lenA = Vector3.Distance(rootPos, midPos);
        float lenB = Vector3.Distance(midPos, endPos);
        float targetDist = Vector3.Distance(rootPos, target);
        float minDist = MathF.Abs(lenA - lenB) + 1e-6f;
        float maxDist = lenA + lenB - 1e-6f;
        float clampedDist = Math.Clamp(targetDist, minDist, maxDist);

        Vector3 clampedTarget = target;
        if (targetDist > 1e-6f)
            clampedTarget = rootPos + (target - rootPos) / targetDist * clampedDist;

        float cosA = (lenA * lenA + clampedDist * clampedDist - lenB * lenB) / (2f * lenA * MathF.Max(clampedDist, 1e-6f));
        cosA = Math.Clamp(cosA, -1f, 1f);
        float angleA = MathF.Acos(cosA);

        Vector3 rootToTarget = clampedTarget - rootPos;
        float rttLen = rootToTarget.Length();
        if (rttLen < 1e-6f) return new[] { rootPos, midPos, clampedTarget };
        Vector3 dir = rootToTarget / rttLen;

        Vector3 rootToPole = poleTarget - rootPos;
        Vector3 poleProj = rootToPole - dir * Vector3.Dot(rootToPole, dir);
        Vector3 bendDir;
        float poleLen = poleProj.Length();
        if (poleLen > 1e-6f) bendDir = poleProj / poleLen;
        else
        {
            Vector3 origDir = midPos - rootPos;
            Vector3 origProj = origDir - dir * Vector3.Dot(origDir, dir);
            float origLen = origProj.Length();
            bendDir = origLen > 1e-6f ? origProj / origLen : Vector3.UnitY;
        }

        Vector3 mid = rootPos + dir * (MathF.Cos(angleA) * lenA) + bendDir * (MathF.Sin(angleA) * lenA);
        Vector3 end = clampedTarget;
        return new[] { rootPos, mid, end };
    }
}

/// <summary>共享 IK 数学工具（矩阵旋转提取 + 方向间旋转）。</summary>
internal static class IKMath
{
    public static Quaternion ExtractRotation(Matrix4x4 m)
    {
        Matrix4x4.Decompose(m, out _, out var r, out _);
        return r;
    }

    public static Quaternion RotationFromTo(Vector3 from, Vector3 to)
    {
        var f = from.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(from);
        var t = to.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(to);
        var dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
        if (dot >= 0.999999f) return Quaternion.Identity;
        if (dot <= -0.999999f)
        {
            var axis = MathF.Abs(f.X) < 0.9f
                ? Vector3.Normalize(Vector3.Cross(f, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }
        var c = Vector3.Cross(f, t);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + dot));
    }
}
