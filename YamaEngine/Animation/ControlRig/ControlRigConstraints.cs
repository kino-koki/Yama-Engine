// ============================================================================
// ControlRigConstraints.cs
// yama游戏引擎 - Control Rig 完整约束求值器（对标 UE5 ControlRig）
// 在 ControlRigCore 数据层之上，补齐 UE5 深度级缺口的三大核心：
//   1. RigHierarchy   —— 元素层次 + 全局变换求值 + 空间切换（Local/Global/Parent/World）
//   2. 约束系统       —— Parent / Aim / IK / Transform 约束（可序列化、可权重混合）
//   3. 求值管线       —— ControlRigEvaluator：控制输入 → 前向求值 → 约束 → 骨骼写回
// 本层不依赖图形/编辑器，纯数据与算法，可独立单元测试。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Animation.ControlRig;

// ───────────────────────────────────────────────────────────────────────────
// 空间类型（对标 UE5 RigSpace）
// ───────────────────────────────────────────────────────────────────────────

public enum RigSpace
{
    /// <summary>相对父级（约束/元素空间）。</summary>
    Local,
    /// <summary>组件/骨架空间（UE 的 Global）。</summary>
    Global,
    /// <summary>直接父元素空间。</summary>
    Parent,
    /// <summary>世界空间（与 Global 在本引擎等价，因无独立场景根）。</summary>
    World
}

// ───────────────────────────────────────────────────────────────────────────
// 运行时元素姿态（局部 + 全局）
// ───────────────────────────────────────────────────────────────────────────

/// <summary>求值过程中的元素变换缓存（局部 + 全局）。</summary>
public struct RigElementPose
{
    public RigTransform Local;
    public RigTransform Global;
}

// ───────────────────────────────────────────────────────────────────────────
// 求值上下文（约束只读访问其它元素，不直接改字典）
// ───────────────────────────────────────────────────────────────────────────

public sealed class RigSolveContext
{
    /// <summary>可变的元素姿态数组（约束可直接写 Global，求值器据此回算局部并传播）。</summary>
    public RigElementPose[] Poses { get; init; } = Array.Empty<RigElementPose>();
    /// <summary>约束修改过的元素索引（求值器处理）。</summary>
    public List<int> Modified { get; } = new();
    public Func<int, int> GetParent { get; init; } = _ => -1;

    public RigTransform GetGlobal(int index)
    {
        if (index >= 0 && index < Poses.Length) return Poses[index].Global;
        return RigTransform.Identity;
    }

    public RigTransform GetParentGlobal(int index)
    {
        var p = GetParent(index);
        return p >= 0 ? GetGlobal(p) : RigTransform.Identity;
    }

    public void SetGlobal(int index, RigTransform global)
    {
        if (index < 0 || index >= Poses.Length) return;
        Poses[index].Global = global;
        if (!Modified.Contains(index)) Modified.Add(index);
    }
}

// ───────────────────────────────────────────────────────────────────────────
// 约束基类（可序列化）
// ───────────────────────────────────────────────────────────────────────────

/// <summary>Control Rig 约束基类。子类实现具体约束语义，权重混合由基类工具完成。</summary>
public abstract class RigConstraint
{
    public string Name = "";
    /// <summary>被约束元素在资产 Elements 中的索引。</summary>
    public int TargetElement = -1;
    /// <summary>混合权重 [0..1]。</summary>
    public float Weight = 1f;
    public bool Enabled = true;

    public abstract string TypeName { get; }

    /// <summary>把约束期望变换写入 ctx（经 ctx.SetGlobal 追踪被修改元素）。</summary>
    public abstract void Apply(RigSolveContext ctx);

    // ── 全局变换混合工具 ────────────────────────────────────────────────
    protected static RigTransform Blend(RigTransform from, RigTransform to, float w)
    {
        w = Math.Clamp(w, 0f, 1f);
        if (w <= 0f) return from;
        if (w >= 1f) return to;
        return new RigTransform(
            Vector3.Lerp(from.Translation, to.Translation, w),
            Quaternion.Slerp(from.Rotation, to.Rotation, w),
            Vector3.Lerp(from.Scale, to.Scale, w));
    }
}

/// <summary>父级约束：目标元素维持相对父元素（或世界）的偏移。</summary>
public sealed class ParentConstraint : RigConstraint
{
    public int ParentElement = -1;
    public RigSpace ParentSpace = RigSpace.Parent;
    /// <summary>保持父级初始偏移（否则用 Offset）。</summary>
    public bool MaintainOffset = true;
    public RigTransform Offset = RigTransform.Identity;

    public override string TypeName => "Parent";

    public override void Apply(RigSolveContext ctx)
    {
        if (TargetElement < 0 || TargetElement >= ctx.Poses.Length) return;
        var parentGlobal = ctx.GetGlobal(ParentElement);
        var expected = RigSpaceUtil.ToWorld(Offset, ParentSpace, parentGlobal);
        var blended = Blend(ctx.Poses[TargetElement].Global, expected, Weight);
        ctx.SetGlobal(TargetElement, blended);
    }
}

/// <summary>瞄准约束：目标元素的 AimAxis 指向另一元素。</summary>
public sealed class AimConstraint : RigConstraint
{
    public int AimTarget = -1;
    public RigSpace AimSpace = RigSpace.World;
    public Vector3 AimAxis = Vector3.UnitX;
    public bool MaintainPosition = true;

    public override string TypeName => "Aim";

    public override void Apply(RigSolveContext ctx)
    {
        if (TargetElement < 0 || TargetElement >= ctx.Poses.Length) return;
        var aimPos = ctx.GetGlobal(AimTarget).Translation;
        var worldAim = RigSpaceUtil.ToWorldPosition(aimPos, AimSpace, ctx.GetParentGlobal(AimTarget));
        var targetPos = ctx.Poses[TargetElement].Global.Translation;

        var dir = worldAim - targetPos;
        if (dir.LengthSquared() < 1e-10f) return;

        // 世界空间下的 AimAxis
        var rot = ctx.Poses[TargetElement].Global.Rotation;
        var axisWorld = Vector3.Transform(AimAxis, rot);
        var delta = RigMath.RotationFromTo(axisWorld, Vector3.Normalize(dir));

        var expected = ctx.Poses[TargetElement].Global;
        expected.Rotation = delta * rot;
        ctx.SetGlobal(TargetElement, Blend(ctx.Poses[TargetElement].Global, expected, Weight));
    }
}

/// <summary>变换约束：匹配另一元素的变换（按通道 + 权重）。</summary>
public sealed class TransformConstraint : RigConstraint
{
    public int SourceElement = -1;
    public RigSpace SourceSpace = RigSpace.Parent;
    public bool MatchTranslation = true;
    public bool MatchRotation = true;
    public bool MatchScale = false;

    public override string TypeName => "Transform";

    public override void Apply(RigSolveContext ctx)
    {
        if (TargetElement < 0 || TargetElement >= ctx.Poses.Length) return;
        var src = ctx.GetGlobal(SourceElement);
        var sourceWorld = RigSpaceUtil.ToWorld(src, SourceSpace, ctx.GetParentGlobal(SourceElement));

        var expected = ctx.Poses[TargetElement].Global;
        if (MatchTranslation) expected.Translation = sourceWorld.Translation;
        if (MatchRotation) expected.Rotation = sourceWorld.Rotation;
        if (MatchScale) expected.Scale = sourceWorld.Scale;
        ctx.SetGlobal(TargetElement, Blend(ctx.Poses[TargetElement].Global, expected, Weight));
    }
}

/// <summary>IK 约束：末端元素经两骨链解算到目标位置（复用 TwoBoneIKSolverReal）。</summary>
public sealed class IKConstraint : RigConstraint
{
    /// <summary>目标位置（在 TargetSpace 中）。</summary>
    public Vector3 TargetPosition;
    public RigSpace TargetSpace = RigSpace.World;
    /// <summary>极向目标元素（弯曲方向）；-1 时用原始中间关节方向。</summary>
    public int PoleTarget = -1;

    public override string TypeName => "IK";

    public override void Apply(RigSolveContext ctx)
    {
        if (TargetElement < 0 || TargetElement >= ctx.Poses.Length) return;
        // 元素链：end = TargetElement, mid = parent(end), root = parent(mid)
        var end = TargetElement;
        var mid = ctx.GetParent(end);
        var root = mid >= 0 ? ctx.GetParent(mid) : -1;
        if (root < 0 || mid < 0) return;

        var rootGlobal = ctx.GetGlobal(root);
        var midGlobal = ctx.GetGlobal(mid);
        var endGlobal = ctx.GetGlobal(end);

        // 世界目标
        var worldTarget = RigSpaceUtil.ToWorldPosition(TargetPosition, TargetSpace, ctx.GetParentGlobal(end));

        // 极向位置
        var polePos = PoleTarget >= 0 ? ctx.GetGlobal(PoleTarget).Translation : midGlobal.Translation;

        var solved = TwoBoneIKSolverReal.Solve(
            rootGlobal.Translation, midGlobal.Translation, endGlobal.Translation,
            worldTarget, polePos);

        // 写回中间与末端位置（保持旋转，权重混合）
        var midExpected = midGlobal;
        midExpected.Translation = solved[1];
        ctx.SetGlobal(mid, Blend(midGlobal, midExpected, Weight));

        var endExpected = endGlobal;
        endExpected.Translation = solved[2];
        ctx.SetGlobal(end, Blend(endGlobal, endExpected, Weight));
    }
}

// ───────────────────────────────────────────────────────────────────────────
// 空间工具
// ───────────────────────────────────────────────────────────────────────────

public static class RigSpaceUtil
{
    /// <summary>把局部/父/世界空间变换转为世界空间。</summary>
    public static RigTransform ToWorld(RigTransform t, RigSpace space, RigTransform parentGlobal)
    {
        switch (space)
        {
            case RigSpace.World:
            case RigSpace.Global:
                return t;
            case RigSpace.Local:
            case RigSpace.Parent:
                // world = local * parentWorld（行向量约定）
                return new RigTransform(
                    Vector3.Transform(t.Translation, parentGlobal.Rotation) + parentGlobal.Translation,
                    Quaternion.Normalize(t.Rotation * parentGlobal.Rotation),
                    t.Scale * parentGlobal.Scale);
            default:
                return t;
        }
    }

    /// <summary>把位置从指定空间转换到世界。</summary>
    public static Vector3 ToWorldPosition(Vector3 p, RigSpace space, RigTransform parentGlobal)
    {
        if (space is RigSpace.World or RigSpace.Global) return p;
        return Vector3.Transform(p, parentGlobal.Rotation) + parentGlobal.Translation;
    }

    /// <summary>把世界变换转到父局部空间。</summary>
    public static RigTransform ToLocal(RigTransform world, RigTransform parentGlobal)
    {
        var inv = parentGlobal.Inverse();
        return new RigTransform(
            Vector3.Transform(world.Translation - parentGlobal.Translation, Quaternion.Inverse(parentGlobal.Rotation)),
            Quaternion.Normalize(Quaternion.Inverse(parentGlobal.Rotation) * world.Rotation),
            world.Scale / SafeScale(parentGlobal.Scale));
    }

    private static Vector3 SafeScale(Vector3 s)
        => new(MathF.Abs(s.X) > 1e-8f ? s.X : 1f,
               MathF.Abs(s.Y) > 1e-8f ? s.Y : 1f,
               MathF.Abs(s.Z) > 1e-8f ? s.Z : 1f);
}

// ───────────────────────────────────────────────────────────────────────────
// RigHierarchy —— 元素层次 + 全局变换求值 + 空间切换
// ───────────────────────────────────────────────────────────────────────────

public sealed class RigHierarchy
{
    private readonly List<RigElement> _elements = new();

    public IReadOnlyList<RigElement> Elements => _elements;

    public int AddElement(RigElement element)
    {
        _elements.Add(element);
        return _elements.Count - 1;
    }

    public int FindElement(string name)
    {
        for (var i = 0; i < _elements.Count; i++)
            if (_elements[i].Name == name) return i;
        return -1;
    }

    public RigElement? GetElement(string name)
    {
        var idx = FindElement(name);
        return idx >= 0 ? _elements[idx] : null;
    }

    public int GetParentIndex(int index)
        => index >= 0 && index < _elements.Count ? _elements[index].ParentIndex : -1;

    /// <summary>
    /// 前向遍历（父先于子）计算每个元素的全局变换。
    /// 输入 locals 为每元素局部变换，输出 globals。
    /// </summary>
    public void ComputeGlobals(IReadOnlyList<RigTransform> locals, List<RigTransform> globals)
    {
        globals.Clear();
        for (var i = 0; i < _elements.Count; i++) globals.Add(RigTransform.Identity);
        for (var i = 0; i < _elements.Count; i++)
        {
            var local = locals[i];
            var parent = _elements[i].ParentIndex;
            if (parent >= 0 && parent < _elements.Count)
                globals[i] = RigSpaceUtil.ToWorld(local, RigSpace.Parent, globals[parent]);
            else
                globals[i] = local;
        }
    }

    /// <summary>从第 index 个元素开始前向传播子树（用于约束修正后重算）。</summary>
    public void Propagate(int index, IReadOnlyList<RigTransform> locals, List<RigTransform> globals)
    {
        var parent = _elements[index].ParentIndex;
        globals[index] = parent >= 0
            ? RigSpaceUtil.ToWorld(locals[index], RigSpace.Parent, globals[parent])
            : locals[index];
        for (var i = index + 1; i < _elements.Count; i++)
        {
            if (_elements[i].ParentIndex != index && _elements[i].ParentIndex >= 0)
                continue; // 仅传播该节点直接子树 —— 通过后续遍历补齐
        }
        // 简单方案：重新前向遍历该节点及之后（父索引必须小于自身，保证拓扑序）
        for (var i = index + 1; i < _elements.Count; i++)
        {
            var p = _elements[i].ParentIndex;
            if (p < 0) continue;
            globals[i] = RigSpaceUtil.ToWorld(locals[i], RigSpace.Parent, globals[p]);
        }
    }
}

// ───────────────────────────────────────────────────────────────────────────
// ControlRigEvaluator —— 求值管线
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// Control Rig 求值管线。把控制输入 + 约束应用到骨骼姿态。
/// 流程：基线局部 → 全局 → 逐约束修正（父先子）→ 传播 → 骨骼写回。
/// </summary>
public sealed class ControlRigEvaluator
{
    private readonly RigHierarchy _hierarchy;
    private readonly List<RigTransform> _locals = new();
    private readonly List<RigTransform> _globals = new();

    public ControlRigEvaluator(ControlRigAsset asset)
    {
        _hierarchy = new RigHierarchy();
        foreach (var e in asset.Elements) _hierarchy.AddElement(e);
    }

    public RigHierarchy Hierarchy => _hierarchy;

    /// <summary>
    /// 求值。controls 为控制名→变换；约束来自 asset.Constraints。
    /// 结果写入 pose（骨骼绑定元素 → 旋转/平移偏移）。
    /// </summary>
    public void Evaluate(ControlRigAsset asset, YamaEngine.Skinning.Skeleton skeleton, YamaEngine.Skinning.SkeletonPose pose,
                         IReadOnlyDictionary<string, RigTransform>? controls = null)
    {
        _locals.Clear();
        _globals.Clear();
        var count = _hierarchy.Elements.Count;

        for (var i = 0; i < count; i++)
        {
            var e = _hierarchy.Elements[i];
            RigTransform local;
            if (e is ControlRigControl c)
                local = controls != null && controls.TryGetValue(c.Name, out var v) ? v : c.Current;
            else
                local = e.InitialTransform;
            _locals.Add(local);
            _globals.Add(RigTransform.Identity);
        }

        // 基线全局
        _hierarchy.ComputeGlobals(_locals, _globals);

        // 逐约束修正（按目标元素深度升序，保证父先子）
        var constraints = new List<RigConstraint>(asset.Constraints);
        constraints.Sort((a, b) => Depth(a.TargetElement).CompareTo(Depth(b.TargetElement)));

        foreach (var constraint in constraints)
        {
            if (constraint == null || !constraint.Enabled) continue;
            if (constraint.TargetElement < 0 || constraint.TargetElement >= count) continue;

            var ctx = new RigSolveContext
            {
                Poses = ToPosesArray(),
                GetParent = _hierarchy.GetParentIndex
            };
            constraint.Apply(ctx);
            foreach (var idx in ctx.Modified)
            {
                var parentGlobal = _hierarchy.GetParentIndex(idx) >= 0
                    ? _globals[_hierarchy.GetParentIndex(idx)]
                    : RigTransform.Identity;
                _globals[idx] = ctx.Poses[idx].Global;
                _locals[idx] = RigSpaceUtil.ToLocal(_globals[idx], parentGlobal);
                _hierarchy.Propagate(idx, _locals, _globals);
            }
        }

        // 骨骼写回
        WriteToPose(skeleton, pose);
    }

    private int Depth(int index)
    {
        var d = 0;
        var i = index;
        while (i >= 0 && _hierarchy.GetParentIndex(i) >= 0 && d < 64)
        {
            i = _hierarchy.GetParentIndex(i);
            d++;
        }
        return d;
    }

    private RigElementPose[] ToPosesArray()
    {
        var arr = new RigElementPose[_globals.Count];
        for (var i = 0; i < _globals.Count; i++)
            arr[i] = new RigElementPose { Local = _locals[i], Global = _globals[i] };
        return arr;
    }

    /// <summary>把元素全局变换写回骨骼姿态（元素名 == 骨骼名时）。</summary>
    public void WriteToPose(YamaEngine.Skinning.Skeleton skeleton, YamaEngine.Skinning.SkeletonPose pose)
    {
        for (var i = 0; i < _hierarchy.Elements.Count; i++)
        {
            var e = _hierarchy.Elements[i];
            if (e.Type != RigElementType.Bone) continue;
            var boneIdx = skeleton.FindBone(e.Name);
            if (boneIdx < 0) continue;

            var world = _globals[i];
            var parentGlobal = _hierarchy.GetParentIndex(i) >= 0
                ? _globals[_hierarchy.GetParentIndex(i)]
                : RigTransform.Identity;
            var local = RigSpaceUtil.ToLocal(world, parentGlobal);
            var bind = skeleton.Bones[boneIdx];
            // 骨骼局部旋转 = 约束后的局部旋转；平移偏移 = 局部位置 - 绑定局部位置
            pose.SetRotation(boneIdx, local.Rotation);
            pose.SetTranslationOffset(boneIdx, local.Translation - bind.LocalPosition);
        }
    }

    /// <summary>便捷：返回某元素的全局变换。</summary>
    public RigTransform GetGlobal(int index) => index >= 0 && index < _globals.Count ? _globals[index] : RigTransform.Identity;
}
