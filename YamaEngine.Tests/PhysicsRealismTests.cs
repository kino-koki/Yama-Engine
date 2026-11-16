// PhysicsRealismTests.cs — RealPhysicsEngine 真实物理正确性验证
// 目标：把“游戏引擎里最真实的物理”变成可证伪、可回归的事实。
// 全部 CPU 级确定性，对照解析解（牛顿力学闭式公式），不依赖 GPU/场景。
// 覆盖：自由落体、伽利略、抛物线、冲量-动量定理、转动定律、角动量守恒、
//       能量守恒、库仑静/动摩擦、恢复系数、堆叠稳定性。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Physics.Real;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>RealPhysicsEngine 真实物理正确性验证套件（解析解对照）。</summary>
public sealed class PhysicsRealismTests
{
    private const float G = 9.81f;
    private const float DT = 1f / 60f;

    private static RealPhysicsEngine CreateEngine(Vector3 gravity, int iterations = 8)
    {
        return new RealPhysicsEngine(new PhysicsSettings
        {
            Gravity = gravity,
            SolverIterations = iterations,
            EnableSleep = false
        });
    }

    private static Rigidbody CreateBody(RealPhysicsEngine engine, string name,
        Vector3 pos, float mass, Vector3 vel = default, float damping = 0f)
    {
        return engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = name,
            Type = RigidbodyType.Dynamic,
            Position = pos,
            Velocity = vel,
            Mass = mass,
            LinearDamping = damping
        });
    }

    /// <summary>1. 自由落体末速度 = g·t（半隐式欧拉对恒加速度精确）。</summary>
    public static void TestFreeFallVelocityMatchesAnalytic()
    {
        var engine = CreateEngine(new Vector3(0, -G, 0));
        var body = CreateBody(engine, "Ball", new Vector3(0, 100, 0), 1f);

        for (int i = 0; i < 60; i++) engine.Step(DT); // t = 1s

        float expected = G * 1f;
        float actual = MathF.Abs(body.Velocity.Y);
        AssertTrue(MathF.Abs(actual - expected) < 1e-2f,
            $"自由落体速度应≈g·t={expected:F4} m/s，实际 {actual:F4}。");
        engine.Dispose();
    }

    /// <summary>2. 自由落体位移符合解析解（半隐式欧拉一阶上界 ½g·t·dt 内）。</summary>
    public static void TestFreeFallDisplacementMatchesAnalytic()
    {
        var engine = CreateEngine(new Vector3(0, -G, 0));
        var body = CreateBody(engine, "Ball", new Vector3(0, 100, 0), 1f);

        for (int i = 0; i < 60; i++) engine.Step(DT);

        float dropped = 100f - body.Position.Y;
        float analytic = 0.5f * G * 1f;                    // ½·g·t²
        float eulerBound = 0.5f * G * 1f * DT;             // 半隐式欧拉一阶上界
        AssertTrue(dropped >= analytic - 0.01f && dropped <= analytic + eulerBound + 0.01f,
            $"自由落体位移应在 [{analytic:F4}, {analytic + eulerBound:F4}]，实际 {dropped:F4}。");
        engine.Dispose();
    }

    /// <summary>3. 伽利略：质量无关的坠落（轻重物体同速）。</summary>
    public static void TestMassIndependentFall()
    {
        var engine = CreateEngine(new Vector3(0, -G, 0));
        var light = CreateBody(engine, "Light", new Vector3(0, 50, 0), 1f);
        var heavy = CreateBody(engine, "Heavy", new Vector3(0, 50, 0), 10f);

        for (int i = 0; i < 30; i++) engine.Step(DT);

        AssertTrue(MathF.Abs(light.Velocity.Y - heavy.Velocity.Y) < 1e-3f,
            $"轻重物体坠落速度应相同（伽利略），轻 {light.Velocity.Y:F4} vs 重 {heavy.Velocity.Y:F4}。");
        engine.Dispose();
    }

    /// <summary>4. 抛体运动符合抛物线（x=vx·t 精确，y 在半隐式欧拉界内）。</summary>
    public static void TestProjectileMatchesParabola()
    {
        var engine = CreateEngine(new Vector3(0, -G, 0));
        var body = CreateBody(engine, "Projectile", Vector3.Zero, 1f,
            new Vector3(10f, 8f, 0f));

        for (int i = 0; i < 60; i++) engine.Step(DT); // t = 1s

        float xExpected = 10f;                            // vx·t（恒速，精确）
        float yAnalytic = 8f - 0.5f * G;                  // vy·t - ½g·t²
        float yEulerBound = 0.5f * G * DT;
        AssertTrue(MathF.Abs(body.Position.X - xExpected) < 1e-2f,
            $"抛体水平位移应={xExpected:F4}，实际 {body.Position.X:F4}。");
        AssertTrue(body.Position.Y <= yAnalytic + 0.02f && body.Position.Y >= yAnalytic - yEulerBound - 0.02f,
            $"抛体垂直位移应在 [{yAnalytic - yEulerBound:F4}, {yAnalytic:F4}]，实际 {body.Position.Y:F4}。");
        engine.Dispose();
    }

    /// <summary>5. 冲量-动量定理：Δp = J（J=6, m=2 → v=3）。</summary>
    public static void TestImpulseMomentumTheorem()
    {
        var engine = CreateEngine(Vector3.Zero); // 无重力，纯动量
        var body = CreateBody(engine, "Puck", Vector3.Zero, 2f);

        body.ApplyImpulse(new Vector3(6f, 0, 0));
        engine.Step(DT);

        float momentum = body.Mass * body.Velocity.X;
        AssertTrue(MathF.Abs(momentum - 6f) < 1e-3f,
            $"冲量-动量定理：动量应=冲量 6，实际 {momentum:F4}。");
        AssertTrue(MathF.Abs(body.Velocity.X - 3f) < 1e-3f,
            $"速度应=J/m=3，实际 {body.Velocity.X:F4}。");
        engine.Dispose();
    }

    /// <summary>6. 转动定律：α = τ/I（球 I=0.4mr²，r=1, m=1 → I=0.4；τ=2 → α=5）。</summary>
    public static void TestTorqueAngularAcceleration()
    {
        var engine = CreateEngine(Vector3.Zero);
        var body = CreateBody(engine, "Wheel", Vector3.Zero, 1f);

        for (int i = 0; i < 60; i++)
        {
            body.ApplyTorque(new Vector3(0, 0, 2f));
            engine.Step(DT);
        }

        float expectedOmega = (2f / 0.4f) * 1f; // α·t = (τ/I)·t = 5
        AssertTrue(MathF.Abs(body.AngularVelocity.Z - expectedOmega) < 0.05f,
            $"角速度应≈(τ/I)·t={expectedOmega:F4} rad/s，实际 {body.AngularVelocity.Z:F4}。");
        engine.Dispose();
    }

    /// <summary>7. 角动量守恒：无外扭矩时 L=I·ω 恒定（偏心冲量后无碰撞）。</summary>
    public static void TestAngularMomentumConservation()
    {
        var engine = CreateEngine(Vector3.Zero);
        var body = CreateBody(engine, "Spinner", Vector3.Zero, 1f);

        // 偏心冲量产生角速度
        body.ApplyImpulseAtPoint(new Vector3(0, 1f, 0), new Vector3(0.5f, 0, 0));
        float omega0 = body.AngularVelocity.Z;
        AssertTrue(MathF.Abs(omega0) > 1e-3f, "偏心冲量应产生初始角速度。");

        for (int i = 0; i < 60; i++) engine.Step(DT);

        AssertTrue(MathF.Abs(body.AngularVelocity.Z - omega0) < 1e-3f,
            $"无外扭矩时角速度应守恒（{omega0:F4}），实际 {body.AngularVelocity.Z:F4}。");
        engine.Dispose();
    }

    /// <summary>8. 能量守恒：自由落体 ½mv² ≈ mgh（阻尼=0，半隐式欧拉一阶偏差）。</summary>
    public static void TestFreeFallEnergyConservation()
    {
        var engine = CreateEngine(new Vector3(0, -G, 0));
        var body = CreateBody(engine, "Ball", new Vector3(0, 100, 0), 1f);

        for (int i = 0; i < 60; i++) engine.Step(DT);

        float kinetic = 0.5f * body.Mass * body.Velocity.LengthSquared();
        float height = 100f - body.Position.Y;
        float potential = body.Mass * G * height;
        float dev = MathF.Abs(kinetic - potential);
        float bound = G * G * 1f * DT + 0.5f; // 一阶积分偏差上界
        AssertTrue(dev < bound,
            $"自由落体机械能应守恒：动能 {kinetic:F3} vs 势能 {potential:F3}（偏差 {dev:F3} < {bound:F3}）。");
        engine.Dispose();
    }

    // ---- 库仑摩擦：平面静/动摩擦（复用已验证的内部模式） ----

    private static (RealPhysicsEngine engine, Rigidbody ground, Rigidbody block)
        CreateFrictionWorld(float friction, Vector3 blockPos)
    {
        // 睡眠开启：与内部已验证的摩擦测试一致（静置块自然入睡，是真实引擎标准行为）。
        var engine = new RealPhysicsEngine(new PhysicsSettings
        {
            Gravity = new Vector3(0, -G, 0),
            SolverIterations = 8,
            EnableSleep = true
        });
        var ground = engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Ground", Type = RigidbodyType.Static, Position = Vector3.Zero,
            Material = new PhysicsMaterial { Name = "G", StaticFriction = friction, DynamicFriction = friction, Restitution = 0.1f }
        });
        engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(30, 1, 30) }, ground);
        var block = engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Block", Type = RigidbodyType.Dynamic, Position = blockPos, Mass = 1f,
            Material = new PhysicsMaterial { Name = "B", StaticFriction = friction, DynamicFriction = friction, Restitution = 0.1f }
        });
        engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(1, 1, 1) }, block);
        return (engine, ground, block);
    }

    /// <summary>9a. 诊断：盒子自然沉降于地面，无外力，应保持有限（隔离接触求解稳定性）。</summary>
    public static void TestBoxOnGroundSettlesFinite()
    {
        var (engine, _, block) = CreateFrictionWorld(0.8f, new Vector3(0, 2.0f, 0));
        for (int i = 0; i < 90; i++)
        {
            engine.Step(0.016f);
            var p = block.Position;
            if (!float.IsFinite(p.X))
            {
                // 报告 NaN 前一帧状态
                var prev = block.Velocity;
                throw new InvalidOperationException(
                    $"NaN at step {i}: pos=({p.X},{p.Y},{p.Z}) vel=({block.Velocity.X},{block.Velocity.Y},{block.Velocity.Z}) ang=({block.AngularVelocity.X},{block.AngularVelocity.Y},{block.AngularVelocity.Z})");
            }
        }
        var p0 = block.Position;
        AssertTrue(float.IsFinite(p0.X) && float.IsFinite(p0.Y) && float.IsFinite(p0.Z),
            $"静置盒子位置应为有限：({p0.X},{p0.Y},{p0.Z})。");
        engine.Dispose();
    }

    /// <summary>9. 静摩擦：水平力 &lt; μmg 时块保持静止（F=5 &lt; μmg≈7.85）。</summary>
    public static void TestStaticFrictionHoldsBelowThreshold()
    {
        var (engine, _, block) = CreateFrictionWorld(0.8f, new Vector3(0, 2.0f, 0));
        float deltaTime = 0.016f;
        for (int i = 0; i < 90; i++) engine.Step(deltaTime); // 自然沉降
        for (int i = 0; i < 150; i++)
        {
            block.ApplyForce(new Vector3(5f, 0, 0)); // < 0.8*1*9.81 ≈ 7.85N
            engine.Step(deltaTime);
        }
        AssertTrue(MathF.Abs(block.Position.X) < 0.1f && MathF.Abs(block.Velocity.X) < 0.5f,
            $"静摩擦应保持静止：位移 {block.Position.X:F4}m，速度 {block.Velocity.X:F4} m/s。");
        engine.Dispose();
    }

    /// <summary>10. 静摩擦：水平力 &gt; μmg 时块滑动（F=15 &gt; 7.85）。</summary>
    public static void TestStaticFrictionBreaksAboveThreshold()
    {
        var (engine, _, block) = CreateFrictionWorld(0.8f, new Vector3(0, 2.0f, 0));
        float deltaTime = 0.016f;
        for (int i = 0; i < 90; i++) engine.Step(deltaTime); // 自然沉降
        for (int i = 0; i < 150; i++)
        {
            block.ApplyForce(new Vector3(15f, 0, 0)); // > 7.85N
            engine.Step(deltaTime);
        }
        AssertTrue(block.Position.X > 0.2f,
            $"超过静摩擦阈值应滑动：位移 {block.Position.X:F4}m。");
        engine.Dispose();
    }

    /// <summary>11. 动摩擦：无外力时以 a=μg 减速（μ=0.5，v0=5 → t=0.48s 后 v≈2.6）。</summary>
    public static void TestKineticFrictionDeceleration()
    {
        var (engine, _, block) = CreateFrictionWorld(0.5f, new Vector3(0, 2.0f, 0));
        float deltaTime = 0.016f;
        for (int i = 0; i < 90; i++) engine.Step(deltaTime); // 自然沉降
        // 用冲量赋予初速度（5 m/s × m=1 = 5 N·s），避免直接赋值 Velocity 绕过物理模型
        block.ApplyImpulse(new Vector3(5f, 0, 0));

        int steps = 30; // ≈0.48s
        for (int i = 0; i < steps; i++) engine.Step(deltaTime);

        float t = steps * deltaTime;
        float decel = (5f - block.Velocity.X) / t; // 实测减速度
        float expected = 0.5f * G;                 // μ·g ≈ 4.905
        AssertTrue(decel > expected - 1.5f && decel < expected + 1.5f,
            $"动摩擦减速度应≈μ·g={expected:F3}，实际 {decel:F3}。");
        engine.Dispose();
    }

    /// <summary>12. 恢复系数：反弹高度比 e=√(h1/h0)（球 e=0.9 落至混凝土）。</summary>
    public static void TestRestitutionBounceHeightRatio()
    {
        var engine = CreateEngine(new Vector3(0, -G, 0), 8);
        var ball = engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Ball", Type = RigidbodyType.Dynamic, Position = new Vector3(0, 10, 0), Mass = 1f,
            Material = new PhysicsMaterial { Name = "SuperBall", StaticFriction = 0.1f, DynamicFriction = 0.1f, Restitution = 0.9f }
        });
        engine.CreateCollider(new ColliderDescription { Type = ColliderType.Sphere, Radius = 0.5f }, ball);
        var ground = engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Ground", Type = RigidbodyType.Static, Position = Vector3.Zero, Material = PhysicsMaterial.Concrete
        });
        engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(20, 1, 20) }, ground);

        var bounceHeights = new List<float>();
        float initialHeight = 10f;
        float previousHeight = initialHeight;
        float deltaTime = 0.016f;
        for (int i = 0; i < 600; i++)
        {
            float prevY = ball.Position.Y;
            engine.Step(deltaTime);
            float currentY = ball.Position.Y;
            if (prevY < currentY && currentY > 0.6f) previousHeight = currentY;
            else if (prevY > currentY && previousHeight > currentY + 0.1f)
            {
                float bounceHeight = currentY - 0.5f;
                if (bounceHeight > 0.1f && bounceHeight < initialHeight - 0.1f)
                    bounceHeights.Add(bounceHeight);
                previousHeight = currentY;
            }
        }

        AssertTrue(bounceHeights.Count >= 1, "应检测到至少一次反弹。");
        float actualE = MathF.Sqrt(bounceHeights[0] / initialHeight);
        AssertTrue(MathF.Abs(actualE - 0.9f) < 0.1f,
            $"恢复系数应≈0.9，实测 {actualE:F3}（反弹 {bounceHeights[0]:F3}m）。");
        engine.Dispose();
    }

    /// <summary>13. 堆叠稳定性：5 个方块堆叠在静力地面上不塌陷。</summary>
    public static void TestStackStability()
    {
        var engine = CreateEngine(new Vector3(0, -G, 0), 10);
        var ground = engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Ground", Type = RigidbodyType.Static, Position = Vector3.Zero,
            Material = new PhysicsMaterial { Name = "G", StaticFriction = 0.8f, DynamicFriction = 0.7f, Restitution = 0.0f }
        });
        engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(10, 1, 10) }, ground);

        var boxes = new List<Rigidbody>();
        for (int i = 0; i < 5; i++)
        {
            var box = engine.CreateRigidbody(new RigidbodyDescription
            {
                Name = $"Box{i}", Type = RigidbodyType.Dynamic,
                Position = new Vector3(0, 1.0f + i * 1.0f, 0), Mass = 1f,
                Material = new PhysicsMaterial { Name = "B", StaticFriction = 0.8f, DynamicFriction = 0.7f, Restitution = 0.0f }
            });
            engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(1f, 1f, 1f) }, box);
            boxes.Add(box);
        }

        for (int i = 0; i < 300; i++) engine.Step(DT);

        float top = boxes[4].Position.Y;
        AssertTrue(float.IsFinite(top), "堆叠顶部位置应为有限值。");
        AssertTrue(MathF.Abs(top - 5.0f) < 0.6f,
            $"5 层堆叠应稳定：顶部 y 应≈5.0，实际 {top:F3}。");
        for (int i = 0; i < 5; i++)
        {
            var p = boxes[i].Position;
            AssertTrue(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z),
                $"方块 {i} 位置应为有限值。");
        }
        engine.Dispose();
    }
}
