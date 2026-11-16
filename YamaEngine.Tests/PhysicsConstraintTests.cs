// PhysicsConstraintTests.cs — RealPhysicsEngine 约束/关节系统验证
// 覆盖：HingeConstraint/SpringConstraint 创建与注册、Step 求解闭环。
// 全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using YamaEngine.Physics.Real;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>RealPhysicsEngine 约束/关节验证套件。</summary>
public sealed class PhysicsConstraintTests
{
    private static RealPhysicsEngine CreateEngineWithTwoBodies(out Rigidbody a, out Rigidbody b)
    {
        var engine = new RealPhysicsEngine(new PhysicsSettings());
        a = engine.CreateRigidbody(new RigidbodyDescription { Name = "A", Mass = 1f });
        b = engine.CreateRigidbody(new RigidbodyDescription { Name = "B", Mass = 1f });
        return engine;
    }

    /// <summary>CreateHingeConstraint/CreateSpringConstraint 应注册到引擎。</summary>
    public static void TestPhysicsEngineCreatesConstraints()
    {
        var engine = CreateEngineWithTwoBodies(out var a, out var b);

        var hinge = engine.CreateHingeConstraint(a, b, default, default, default);
        var spring = engine.CreateSpringConstraint(a, b, default, default, 50f, 2f);

        AssertTrue(hinge != null && spring != null, "约束创建应返回实例。");
        AssertTrue(engine.ConstraintCount == 2,
            $"应注册 2 个约束，实际 {engine.ConstraintCount}。");
    }

    /// <summary>Step 应在含约束时安全求解（不抛异常、可重复）。</summary>
    public static void TestPhysicsEngineStepWithConstraints()
    {
        var engine = CreateEngineWithTwoBodies(out var a, out var b);
        engine.CreateHingeConstraint(a, b, default, default, default);
        engine.CreateSpringConstraint(a, b, default, default, 50f, 2f);

        engine.Step(1f / 60f);
        engine.Step(1f / 60f);
        engine.Step(1f / 60f);

        AssertTrue(true, "含约束 Step 应可重复调用。");
    }

    /// <summary>Spring 约束在 Step 后应保持刚体有效（无 NaN 位置）。</summary>
    public static void TestPhysicsEngineSpringMaintainsFiniteState()
    {
        var engine = CreateEngineWithTwoBodies(out var a, out var b);
        engine.CreateSpringConstraint(a, b, default, default, 50f, 2f);

        engine.Step(1f / 60f);

        var pa = a.Position;
        var pb = b.Position;
        AssertTrue(float.IsFinite(pa.X) && float.IsFinite(pa.Y) && float.IsFinite(pa.Z),
            "刚体A 位置应为有限值。");
        AssertTrue(float.IsFinite(pb.X) && float.IsFinite(pb.Y) && float.IsFinite(pb.Z),
            "刚体B 位置应为有限值。");
    }
}
