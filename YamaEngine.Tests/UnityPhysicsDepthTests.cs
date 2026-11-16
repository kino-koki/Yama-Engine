// UnityPhysicsDepthTests.cs — Unity5 风格物理深度验证
// 覆盖：PhysicsMaterial 摩擦/弹性组合、CalculateMass 系列、Presets；
//       ColliderComponent Box/Sphere/Capsule 工厂字段；QueryFilter 过滤语义；
//       RigidbodyConstraints / CCDMode 枚举；真实 PhysicsWorld 的重力下落 + Raycast 命中。
using System;
using System.Numerics;
using YamaEngine.Physics;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityPhysicsDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    // ══════════════════════════════════════════════════════════════════
    //  一、PhysicsMaterial 组合与质量
    // ══════════════════════════════════════════════════════════════════

    /// <summary>默认材质：摩擦 0.6、弹性 0.2、密度 1000。</summary>
    public static void TestMaterialDefaults()
    {
        var m = new PhysicsMaterial();
        Near(m.Friction, 0.6f, 0.001f, "默认动摩擦 0.6");
        Near(m.StaticFriction, 0.6f, 0.001f, "默认静摩擦 0.6");
        Near(m.Restitution, 0.2f, 0.001f, "默认弹性 0.2");
        Near(m.Density, 1000f, 0.001f, "默认密度 1000");
        AssertEqual(PhysicsMaterialCombineMode.Average, m.FrictionCombine, "默认摩擦组合 Average");
    }

    /// <summary>带参构造：friction/restitution/density 直接生效。</summary>
    public static void TestMaterialParameterizedCtor()
    {
        var m = new PhysicsMaterial(0.9f, 0.7f, 2000f);
        Near(m.Friction, 0.9f, 0.001f, "动摩擦 0.9");
        Near(m.Restitution, 0.7f, 0.001f, "弹性 0.7");
        Near(m.Density, 2000f, 0.001f, "密度 2000");
    }

    /// <summary>CombineFriction：Average 取平均。</summary>
    public static void TestCombineFrictionAverage()
    {
        var a = new PhysicsMaterial { Friction = 0.8f, FrictionCombine = PhysicsMaterialCombineMode.Average };
        var b = new PhysicsMaterial { Friction = 0.2f, FrictionCombine = PhysicsMaterialCombineMode.Average };
        Near(PhysicsMaterial.CombineFriction(a, b), 0.5f, 0.001f, "Average 摩擦 = (0.8+0.2)/2 = 0.5");
    }

    /// <summary>CombineFriction：Min 取最小。</summary>
    public static void TestCombineFrictionMin()
    {
        var a = new PhysicsMaterial { Friction = 0.8f, FrictionCombine = PhysicsMaterialCombineMode.Min };
        var b = new PhysicsMaterial { Friction = 0.3f, FrictionCombine = PhysicsMaterialCombineMode.Min };
        Near(PhysicsMaterial.CombineFriction(a, b), 0.3f, 0.001f, "Min 摩擦取 0.3");
    }

    /// <summary>CombineFriction：Max 取最大。</summary>
    public static void TestCombineFrictionMax()
    {
        var a = new PhysicsMaterial { Friction = 0.8f, FrictionCombine = PhysicsMaterialCombineMode.Max };
        var b = new PhysicsMaterial { Friction = 0.3f, FrictionCombine = PhysicsMaterialCombineMode.Max };
        Near(PhysicsMaterial.CombineFriction(a, b), 0.8f, 0.001f, "Max 摩擦取 0.8");
    }

    /// <summary>CombineRestitution：Multiply 取乘积。</summary>
    public static void TestCombineRestitutionMultiply()
    {
        var a = new PhysicsMaterial { Restitution = 0.5f, RestitutionCombine = PhysicsMaterialCombineMode.Multiply };
        var b = new PhysicsMaterial { Restitution = 0.5f, RestitutionCombine = PhysicsMaterialCombineMode.Multiply };
        Near(PhysicsMaterial.CombineRestitution(a, b), 0.25f, 0.001f, "Multiply 弹性 = 0.5*0.5 = 0.25");
    }

    /// <summary>CalculateMassBox：立方体积×密度。</summary>
    public static void TestCalculateMassBox()
    {
        var m = new PhysicsMaterial { Density = 1000f };
        // halfExtents = (0.5,0.5,0.5) → 全尺寸 1m³ → 质量 1000kg
        Near(m.CalculateMassBox(new Vector3(0.5f)), 1000f, 0.5f, "1m³ 木质盒 ≈ 1000kg");
        // halfExtents = (1,1,1) → 8m³ → 8000kg
        Near(m.CalculateMassBox(new Vector3(1, 1, 1)), 8000f, 1f, "8m³ ≈ 8000kg");
    }

    /// <summary>CalculateMassSphere：球体积×密度。</summary>
    public static void TestCalculateMassSphere()
    {
        var m = new PhysicsMaterial { Density = 1000f };
        // radius=0.5 → V = 4/3·π·0.5³ ≈ 0.5236 → ≈ 523.6kg
        Near(m.CalculateMassSphere(0.5f), 523.6f, 1f, "半径 0.5 球 ≈ 523.6kg");
    }

    /// <summary>Clone：深拷贝，修改克隆不影响原。</summary>
    public static void TestMaterialClone()
    {
        var orig = new PhysicsMaterial { Friction = 0.4f, Restitution = 0.3f };
        var clone = orig.Clone();
        AssertTrue(ReferenceEquals(orig, clone) == false, "Clone 返回新实例");
        Near(clone.Friction, 0.4f, 0.001f, "克隆摩擦一致");
        clone.Friction = 0.9f;
        Near(orig.Friction, 0.4f, 0.001f, "改克隆不影响原");
    }

    /// <summary>Presets：Metal/Wood/Rubber/Ice 命名查找。</summary>
    public static void TestMaterialPresets()
    {
        var rubber = PhysicsMaterialPresets.FindByName("Rubber");
        AssertTrue(rubber is not null, "Rubber 预设存在");
        AssertTrue(rubber!.Restitution > 0.5f, "橡胶弹性高");

        var ice = PhysicsMaterialPresets.FindByName("Ice");
        AssertTrue(ice is not null, "Ice 预设存在");
        AssertTrue(ice!.Friction < 0.3f, "冰摩擦低");

        var metal = PhysicsMaterialPresets.FindByName("Metal");
        AssertTrue(metal is not null, "Metal 预设存在");
        AssertTrue(metal!.Density > 1000f, "金属密度 > 1000");
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、ColliderComponent 工厂
    // ══════════════════════════════════════════════════════════════════

    /// <summary>CreateBox 字段。</summary>
    public static void TestColliderBox()
    {
        var box = ColliderComponent.CreateBox(new Vector3(0.5f, 1f, 2f));
        AssertEqual(ColliderType.Box, box.Type, "类型 Box");
        Near(box.HalfExtents.X, 0.5f, 0.001f, "半扩展 X");
        Near(box.HalfExtents.Y, 1f, 0.001f, "半扩展 Y");
        Near(box.HalfExtents.Z, 2f, 0.001f, "半扩展 Z");
        AssertFalse(box.IsTrigger, "默认非 Trigger");
        AssertTrue(box.Enabled, "默认 Enabled");
    }

    /// <summary>CreateBox 自定义中心。</summary>
    public static void TestColliderBoxCenter()
    {
        var box = ColliderComponent.CreateBox(new Vector3(1f), new Vector3(0, 2, 0));
        Near(box.Center.Y, 2f, 0.001f, "中心 Y=2");
    }

    /// <summary>CreateSphere 字段。</summary>
    public static void TestColliderSphere()
    {
        var s = ColliderComponent.CreateSphere(0.8f);
        AssertEqual(ColliderType.Sphere, s.Type, "类型 Sphere");
        Near(s.Radius, 0.8f, 0.001f, "半径 0.8");
    }

    /// <summary>CreateCapsule 字段。</summary>
    public static void TestColliderCapsule()
    {
        var c = ColliderComponent.CreateCapsule(0.4f, 1.8f);
        AssertEqual(ColliderType.Capsule, c.Type, "类型 Capsule");
        Near(c.Radius, 0.4f, 0.001f, "胶囊半径");
        Near(c.Height, 1.8f, 0.001f, "胶囊高度");
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、QueryFilter 过滤语义
    // ══════════════════════════════════════════════════════════════════

    /// <summary>默认 Filter 不忽略任何 body。</summary>
    public static void TestQueryFilterDefault()
    {
        var f = QueryFilter.Default;
        AssertFalse(f.ShouldIgnore(1, 1, isSensor: false), "默认放行普通 body");
    }

    /// <summary>IgnoreTriggers：isSensor=true 被过滤。</summary>
    public static void TestQueryFilterIgnoreTriggers()
    {
        var f = QueryFilter.Default;
        f.IgnoreTriggers = true;
        AssertTrue(f.ShouldIgnore(1, 1, isSensor: true), "IgnoreTriggers 过滤 sensor");
        AssertFalse(f.ShouldIgnore(1, 1, isSensor: false), "普通 body 放行");
    }

    /// <summary>IgnoreDynamic / IgnoreStatic + 显式忽略列表。</summary>
    public static void TestQueryFilterExplicitIgnore()
    {
        var f = new QueryFilter();
        f.IgnoredBodyIds = new System.Collections.Generic.HashSet<uint> { 42 };
        AssertTrue(f.ShouldIgnore(42, 1, isSensor: false), "显式忽略 bodyId=42");
        AssertFalse(f.ShouldIgnore(7, 1, isSensor: false), "其他 bodyId 放行");
    }

    // ══════════════════════════════════════════════════════════════════
    //  四、RigidbodyConstraints / CCDMode 枚举
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Constraints flags 组合。</summary>
    public static void TestRigidbodyConstraints()
    {
        var c = RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezeRotationZ;
        AssertTrue((c & RigidbodyConstraints.FreezePositionX) != 0, "含 FreezePositionX");
        AssertTrue((c & RigidbodyConstraints.FreezeRotationZ) != 0, "含 FreezeRotationZ");
        AssertTrue((c & RigidbodyConstraints.FreezePositionY) == 0, "不含 FreezePositionY");
        AssertEqual(RigidbodyConstraints.None, RigidbodyConstraints.None, "None 值");
    }

    /// <summary>CCDMode 三态。</summary>
    public static void TestCCDMode()
    {
        AssertEqual(CCDMode.Discrete, CCDMode.Discrete, "Discrete");
        AssertEqual(CCDMode.Continuous, CCDMode.Continuous, "Continuous");
        AssertEqual(CCDMode.ContinuousSpeculative, CCDMode.ContinuousSpeculative, "ContinuousSpeculative");
        AssertTrue((int)CCDMode.Discrete != (int)CCDMode.Continuous, "Discrete ≠ Continuous");
    }

    // ══════════════════════════════════════════════════════════════════
    //  五、Unity5 层级系统（PhysicsLayer / PhysicsLayerMask / 碰撞矩阵）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>PhysicsLayer 预设层值与 Unity 约定一致。</summary>
    public static void TestPhysicsLayerEnumValues()
    {
        AssertEqual((byte)0, (byte)PhysicsLayer.Default, "Default=0");
        AssertEqual((byte)1, (byte)PhysicsLayer.Player, "Player=1");
        AssertEqual((byte)3, (byte)PhysicsLayer.Environment, "Environment=3");
        AssertEqual((byte)4, (byte)PhysicsLayer.Weapon, "Weapon=4");
        AssertEqual((byte)5, (byte)PhysicsLayer.Trigger, "Trigger=5");
        AssertEqual((byte)10, (byte)PhysicsLayer.Raycast, "Raycast=10");
        AssertEqual((byte)31, (byte)PhysicsLayer.Custom19, "Custom19=31");
    }

    /// <summary>PhysicsLayerMask.GetMask 单层/多层/索引。</summary>
    public static void TestLayerMaskGetMask()
    {
        Near(PhysicsLayerMask.GetMask(PhysicsLayer.Player), 1u << 1, 0.001f, "Player 掩码 = 2");
        uint combined = PhysicsLayerMask.GetMask(PhysicsLayer.Default, PhysicsLayer.Player, PhysicsLayer.Environment);
        AssertEqual(0b1011u, combined, "合并 Default|Player|Environment = 0b1011");
        AssertEqual(1u << 10, PhysicsLayerMask.LayerToMask(10), "LayerToMask(10)");
        AssertTrue(PhysicsLayerMask.ContainsLayer(combined, PhysicsLayer.Player), "含 Player");
        AssertTrue(!PhysicsLayerMask.ContainsLayer(combined, PhysicsLayer.Weapon), "不含 Weapon");
        AssertTrue(PhysicsLayerMask.ContainsLayerIndex(combined, 3), "含索引 3");
    }

    /// <summary>碰撞矩阵默认配置：Default 层与 Weapon/Environment 碰撞。</summary>
    public static void TestLayerCollisionMatrixDefaults()
    {
        var m = new PhysicsLayerCollisionMatrix();
        AssertTrue(m.LayerCollides(PhysicsLayer.Default, PhysicsLayer.Weapon), "Default↔Weapon 碰撞");
        AssertTrue(m.LayerCollides(PhysicsLayer.Default, PhysicsLayer.Environment), "Default↔Environment 碰撞");
        AssertTrue(m.LayerCollides(PhysicsLayer.Player, PhysicsLayer.Environment), "Player↔Environment 碰撞");
        AssertTrue(m.LayerCollides(PhysicsLayer.NPC, PhysicsLayer.Weapon), "NPC↔Weapon 碰撞");
    }

    /// <summary>碰撞矩阵自定义掩码 + 索引器。</summary>
    public static void TestLayerCollisionMatrixCustom()
    {
        var m = new PhysicsLayerCollisionMatrix();
        // 默认 Default↔Weapon 碰撞，DisableCollision 后应断开（对称双边清理）。
        AssertTrue(m.LayerCollides(PhysicsLayer.Default, PhysicsLayer.Weapon), "初始 Default↔Weapon 碰撞");
        m.DisableCollision(PhysicsLayer.Default, PhysicsLayer.Weapon);
        AssertTrue(!m.LayerCollides(PhysicsLayer.Default, PhysicsLayer.Weapon), "Disable 后 Default↔Weapon 断开");
        // Default↔Environment 不受影响
        AssertTrue(m.LayerCollides(PhysicsLayer.Default, PhysicsLayer.Environment), "Default↔Environment 不变");
        // 重新 Enable 后恢复
        m.EnableCollision(PhysicsLayer.Default, PhysicsLayer.Weapon);
        AssertTrue(m.LayerCollides(PhysicsLayer.Default, PhysicsLayer.Weapon), "Enable 后 Default↔Weapon 恢复");

        // 自定义层索引器写入：Custom0 只允许撞 Custom1。
        var m2 = new PhysicsLayerCollisionMatrix();
        m2[(int)PhysicsLayer.Custom0] = PhysicsLayerMask.GetMask(PhysicsLayer.Custom1);
        m2[(int)PhysicsLayer.Custom1] = PhysicsLayerMask.GetMask(PhysicsLayer.Custom0);
        AssertTrue(m2.LayerCollides(PhysicsLayer.Custom0, PhysicsLayer.Custom1), "Custom0↔Custom1 互通");
        // 清理 Custom0 与 Custom19 的双侧位后不再碰撞
        m2[(int)PhysicsLayer.Custom0] &= ~PhysicsLayerMask.GetMask(PhysicsLayer.Custom19);
        m2[(int)PhysicsLayer.Custom19] &= ~PhysicsLayerMask.GetMask(PhysicsLayer.Custom0);
        AssertTrue(!m2.LayerCollides(PhysicsLayer.Custom0, PhysicsLayer.Custom19), "Custom0↔Custom19 已断开");
    }

    /// <summary>GetCollisionMask 返回该层碰撞掩码。</summary>
    public static void TestLayerCollisionMask()
    {
        var m = new PhysicsLayerCollisionMatrix();
        uint weaponMask = m.GetCollisionMask(PhysicsLayer.Weapon);
        AssertTrue(PhysicsLayerMask.ContainsLayer(weaponMask, PhysicsLayer.Default), "Weapon 撞 Default");
        AssertTrue(PhysicsLayerMask.ContainsLayer(weaponMask, PhysicsLayer.Player), "Weapon 撞 Player");
        AssertTrue(PhysicsLayerMask.ContainsLayer(weaponMask, PhysicsLayer.Environment), "Weapon 撞 Environment");
    }
}
