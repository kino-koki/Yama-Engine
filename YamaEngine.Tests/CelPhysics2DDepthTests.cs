// CelPhysics2DDepthTests.cs — Cel2D 物理（形状/刚体/碰撞/世界/射线）深度验证
// 解析解对照 + 定性不变量。全部 CPU 确定性。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public sealed class CelPhysics2DDepthTests
{
    // ---- 形状：质量/惯量（解析解） ----

    /// <summary>圆形质量 = π·r²·ρ。</summary>
    public static void TestCircleMass()
    {
        var c = new CelCircleShape2D { Radius = 1f };
        float m = c.ComputeMass(1f);
        AssertTrue(MathF.Abs(m - MathF.PI) < 1e-4f,
            $"圆质量应=π·r²·ρ={MathF.PI:F4}，实际 {m:F4}。");
    }

    /// <summary>盒质量 = (2hw)·(2hh)·ρ。</summary>
    public static void TestBoxMass()
    {
        var b = new CelBoxShape2D { HalfWidth = 1f, HalfHeight = 2f };
        float m = b.ComputeMass(1f);
        AssertTrue(MathF.Abs(m - 8f) < 1e-4f,
            $"盒质量应=4·hw·hh·ρ=8，实际 {m:F4}。");
    }

    /// <summary>多边形质量（鞋带公式）：单位正方形面积=4。</summary>
    public static void TestPolygonMass()
    {
        var p = new CelPolygonShape2D();
        p.SetVertices(new[] { new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 2), new Vector2(0, 2) });
        float m = p.ComputeMass(1f);
        AssertTrue(MathF.Abs(m - 4f) < 1e-4f,
            $"正方形质量应=面积·ρ=4，实际 {m:F4}。");
    }

    /// <summary>盒转动惯量 = m·(hw²+hh²)/3（此处按实现 (2hw)²·(2hh)²/12 = m·(w²+h²)/12）。</summary>
    public static void TestBoxInertiaPositive()
    {
        var b = new CelBoxShape2D { HalfWidth = 0.5f, HalfHeight = 0.5f };
        float mass = b.ComputeMass(1f);
        float inertia = b.ComputeInertia(mass);
        AssertTrue(inertia > 0f,
            $"盒转动惯量应为正，实际 {inertia:F4}。");
    }

    // ---- 刚体：质量/静态/冲量 ----

    /// <summary>刚体重算质量：InverseMass = 1/Mass。</summary>
    public static void TestBodyRecomputeMass()
    {
        var shape = new CelCircleShape2D { Radius = 1f };
        var body = new CelBody2D(shape, Vector2.Zero, 1f);
        AssertTrue(MathF.Abs(body.Mass - MathF.PI) < 1e-4f,
            $"刚体质量应=π，实际 {body.Mass:F4}。");
        AssertTrue(MathF.Abs(body.InverseMass - 1f / MathF.PI) < 1e-4f,
            $"逆质量应=1/π，实际 {body.InverseMass:F4}。");
    }

    /// <summary>SetStatic(true) 后质量归零、逆质量为零（不可被力推动）。</summary>
    public static void TestSetStaticZeroMass()
    {
        var body = new CelBody2D(new CelCircleShape2D { Radius = 1f }, Vector2.Zero);
        body.SetStatic(true);
        AssertEqual(0f, body.Mass, "静态体质量应为 0。");
        AssertEqual(0f, body.InverseMass, "静态体逆质量应为 0。");
    }

    /// <summary>冲量-动量：v += J·invMass（J=2, m=π → Δv=2/π）。</summary>
    public static void TestApplyImpulseChangesVelocity()
    {
        var body = new CelBody2D(new CelCircleShape2D { Radius = 1f }, Vector2.Zero);
        body.ApplyImpulse(new Vector2(2f, 0), Vector2.Zero);
        float dv = body.LinearVelocity.X;
        AssertTrue(MathF.Abs(dv - 2f / MathF.PI) < 1e-4f,
            $"冲量后速度应=J/m={2f / MathF.PI:F4}，实际 {dv:F4}。");
    }

    /// <summary>静态体施加冲量无效果（invMass=0 直接返回）。</summary>
    public static void TestStaticImpulseNoEffect()
    {
        var body = new CelBody2D(new CelCircleShape2D { Radius = 1f }, Vector2.Zero);
        body.SetStatic(true);
        body.ApplyImpulse(new Vector2(5f, 0), Vector2.Zero);
        AssertEqual(Vector2.Zero, body.LinearVelocity, "静态体冲量后速度应为零。");
    }

    // ---- 世界：重力积分 ----

    /// <summary>自由落体：v += g·dt（半隐式欧拉对恒加速度精确）。</summary>
    public static void TestGravityIntegrationVelocity()
    {
        var world = new CelPhysicsWorld2D { Gravity = new Vector2(0, 100f), SubSteps = 1 };
        var body = world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(0, 0));
        world.Update(0.5f);
        float expected = 100f * 0.5f;
        AssertTrue(MathF.Abs(body.LinearVelocity.Y - expected) < 1e-3f,
            $"自由落体速度应=g·t={expected:F2}，实际 {body.LinearVelocity.Y:F4}。");
    }

    /// <summary>重力方向：默认 Gravity.Y 为正（向下，+Y）。</summary>
    public static void TestGravityDirectionDefault()
    {
        var world = new CelPhysicsWorld2D();
        AssertTrue(world.Gravity.Y > 0f,
            $"默认重力应向下（+Y 为正），实际 {world.Gravity.Y:F2}。");
    }

    /// <summary>零 deltaTime 不推进（位置/速度不变）。</summary>
    public static void TestZeroDeltaTimeNoStep()
    {
        var world = new CelPhysicsWorld2D { Gravity = new Vector2(0, 100f) };
        var body = world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(0, 0));
        world.Update(0f);
        AssertEqual(Vector2.Zero, body.LinearVelocity, "零 dt 速度应不变。");
    }

    // ---- 碰撞检测：圆-圆 / 圆-盒 / 盒-盒 ----

    private static CelBody2D MakeCircle(Vector2 pos, float r)
        => new CelBody2D(new CelCircleShape2D { Radius = r }, pos);

    private static CelBody2D MakeBox(Vector2 pos, float hw, float hh)
        => new CelBody2D(new CelBoxShape2D { HalfWidth = hw, HalfHeight = hh }, pos);

    /// <summary>两圆分离不碰撞。</summary>
    public static void TestCircleCircleSeparate()
    {
        var a = MakeCircle(new Vector2(0, 0), 1f);
        var b = MakeCircle(new Vector2(5, 0), 1f);
        bool hit = CelCollision2D.CircleCircle(a.Position, 1f, b.Position, 1f, out _, out _, out _);
        AssertTrue(!hit, "相距 5 半径各 1 的两圆不应碰撞。");
    }

    /// <summary>两圆重叠：深度 = ra+rb-dist，法线从 A 指向 B。</summary>
    public static void TestCircleCircleDepthAndNormal()
    {
        bool hit = CelCollision2D.CircleCircle(new Vector2(0, 0), 1f, new Vector2(1.5f, 0), 1f,
            out Vector2 normal, out float depth, out _);
        AssertTrue(hit, "相距 1.5 半径各 1 应碰撞。");
        AssertTrue(MathF.Abs(depth - 0.5f) < 1e-4f,
            $"深度应=ra+rb-dist=0.5，实际 {depth:F4}。");
        AssertTrue(normal.X > 0.99f,
            $"法线应从 A 指向 B（+X），实际 ({normal.X:F3},{normal.Y:F3})。");
    }

    /// <summary>Test() 派发：圆 vs 盒重叠命中，法线从 A(圆) 指向 B(盒)。</summary>
    public static void TestManifoldCircleBox()
    {
        var circle = MakeCircle(new Vector2(0.3f, 0f), 0.5f);
        var box = MakeBox(new Vector2(1f, 0f), 0.5f, 0.5f);
        bool hit = CelCollision2D.Test(circle, box, out var manifold);
        AssertTrue(hit, "圆与盒重叠应命中。");
        AssertTrue(manifold.Normal.X > 0.9f,
            $"法线应从圆(A)指向盒(B)即 +X，实际 ({manifold.Normal.X:F3},{manifold.Normal.Y:F3})。");
    }

    /// <summary>盒-盒 SAT：轴向对齐重叠命中。</summary>
    public static void TestBoxBoxOverlap()
    {
        var a = MakeBox(new Vector2(0, 0), 1f, 1f);
        var b = MakeBox(new Vector2(1.5f, 0), 1f, 1f);
        bool hit = CelCollision2D.BoxBox(a, b, out _, out float depth, out _);
        AssertTrue(hit, "两盒重叠应命中。");
        AssertTrue(depth > 0f && depth <= 0.5f + 1e-3f,
            $"重叠深度应在 (0, 0.5]，实际 {depth:F4}。");
    }

    /// <summary>盒-盒分离不命中。</summary>
    public static void TestBoxBoxSeparate()
    {
        var a = MakeBox(new Vector2(0, 0), 1f, 1f);
        var b = MakeBox(new Vector2(5f, 0), 1f, 1f);
        bool hit = CelCollision2D.BoxBox(a, b, out _, out _, out _);
        AssertTrue(!hit, "相距足够远两盒不应碰撞。");
    }

    /// <summary>凸多边形-凸多边形 SAT 命中。</summary>
    public static void TestPolygonPolygonOverlap()
    {
        var a = new CelBody2D(new CelPolygonShape2D(), new Vector2(0, 0));
        ((CelPolygonShape2D)a.Shape).SetVertices(new[] { new Vector2(0, 0), new Vector2(2, 0), new Vector2(1, 2) });
        a.RecomputeMass();
        var b = new CelBody2D(new CelPolygonShape2D(), new Vector2(1, 0.5f));
        ((CelPolygonShape2D)b.Shape).SetVertices(new[] { new Vector2(0, 0), new Vector2(2, 0), new Vector2(1, 2) });
        b.RecomputeMass();
        bool hit = CelCollision2D.PolygonPolygon(a, b, out _, out _, out _);
        AssertTrue(hit, "两凸多边形重叠应命中。");
    }

    // ---- 物理世界：碰撞求解 / 事件 / 射线 ----

    /// <summary>重叠圆被位置校正推开（深度减小）。重叠 1.0 &gt; slop 0.5 才校正。</summary>
    public static void TestWorldResolvesOverlap()
    {
        var world = new CelPhysicsWorld2D { Gravity = Vector2.Zero, SubSteps = 1 };
        var a = world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(0, 0));
        var b = world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(1.0f, 0)); // 重叠1.0>slop0.5
        float distBefore = Vector2.Distance(a.Position, b.Position);
        world.Update(1f / 60f);
        float distAfter = Vector2.Distance(a.Position, b.Position);
        AssertTrue(distAfter > distBefore,
            $"重叠圆应被推开：前 {distBefore:F4} 后 {distAfter:F4}。");
    }

    /// <summary>静态体不动：动态圆撞静态圆，静态圆位置不变。</summary>
    public static void TestStaticBodyImmovable()
    {
        var world = new CelPhysicsWorld2D { Gravity = Vector2.Zero, SubSteps = 1 };
        var stat = world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(3f, 0));
        stat.SetStatic(true);
        var dyn = world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(0, 0));
        dyn.LinearVelocity = new Vector2(5f, 0);
        world.Update(1f / 60f);
        AssertEqual(new Vector2(3f, 0), stat.Position, "静态体位置应不变。");
    }

    /// <summary>接触事件 OnContact 触发。</summary>
    public static void TestContactEventFires()
    {
        var world = new CelPhysicsWorld2D { Gravity = Vector2.Zero, SubSteps = 1 };
        world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(0, 0));
        world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(1.5f, 0));
        int fired = 0;
        world.OnContact += _ => fired++;
        world.Update(1f / 60f);
        AssertTrue(fired > 0, $"重叠圆应触发 OnContact，实际触发 {fired} 次。");
    }

    /// <summary>射线命中圆：距离 = 到圆心距 - 半径。</summary>
    public static void TestRaycastHitCircle()
    {
        var world = new CelPhysicsWorld2D { Gravity = Vector2.Zero };
        world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(5f, 0));
        var hit = world.Raycast(new Vector2(0, 0), new Vector2(1f, 0), 100f,
            out Vector2 hitPoint, out float dist);
        AssertTrue(hit != null, "射线应命中圆。");
        AssertTrue(MathF.Abs(dist - 4f) < 1e-3f,
            $"命中距离应=4，实际 {dist:F4}。");
        AssertTrue(MathF.Abs(hitPoint.X - 4f) < 1e-3f,
            $"命中点 X 应=4，实际 {hitPoint.X:F4}。");
    }

    /// <summary>射线未命中返回 null。</summary>
    public static void TestRaycastMiss()
    {
        var world = new CelPhysicsWorld2D { Gravity = Vector2.Zero };
        world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(5f, 5f));
        var hit = world.Raycast(new Vector2(0, 0), new Vector2(1f, 0), 100f, out _, out _);
        AssertTrue(hit == null, "射线偏离圆不应命中。");
    }

    /// <summary>射线取最近命中（多个圆取最小距离）。</summary>
    public static void TestRaycastNearest()
    {
        var world = new CelPhysicsWorld2D { Gravity = Vector2.Zero };
        world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(10f, 0));
        var near = world.CreateBody(new CelCircleShape2D { Radius = 1f }, new Vector2(3f, 0));
        var hit = world.Raycast(new Vector2(0, 0), new Vector2(1f, 0), 100f, out _, out float dist);
        AssertTrue(hit == near, "射线应命中最近的圆。");
        AssertTrue(MathF.Abs(dist - 2f) < 1e-3f,
            $"最近命中距离应=2，实际 {dist:F4}。");
    }
}
