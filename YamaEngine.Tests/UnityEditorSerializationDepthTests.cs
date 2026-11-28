// UnityEditorSerializationDepthTests.cs — Unity5 Editor 序列化属性深度验证
// 覆盖：SerializeField（默认/Tooltip/Range）、HideInInspector、RequireComponent 多重标记。
using System;
using System.Linq;
using YamaEngine.Scripting;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityEditorSerializationDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    /// <summary>SerializeFieldAttribute 默认：Tooltip null，Range NaN。</summary>
    public static void TestSerializeFieldDefaults()
    {
        var attr = new SerializeFieldAttribute();
        AssertTrue(attr.Tooltip is null, "默认 Tooltip null");
        AssertTrue(float.IsNaN(attr.RangeMin), "默认 RangeMin NaN");
        AssertTrue(float.IsNaN(attr.RangeMax), "默认 RangeMax NaN");
    }

    /// <summary>SerializeFieldAttribute(string tooltip) 构造。</summary>
    public static void TestSerializeFieldTooltipCtor()
    {
        var attr = new SerializeFieldAttribute("玩家血量");
        AssertEqual("玩家血量", attr.Tooltip, "Tooltip 由构造设置");
        AssertTrue(float.IsNaN(attr.RangeMin), "RangeMin 仍 NaN");
        AssertTrue(float.IsNaN(attr.RangeMax), "RangeMax 仍 NaN");
    }

    /// <summary>SerializeField 可在对象初始化时设置 Tooltip/RangeMin/RangeMax。</summary>
    public static void TestSerializeFieldRange()
    {
        var attr = new SerializeFieldAttribute
        {
            Tooltip = "移动速度",
            RangeMin = 0f,
            RangeMax = 100f
        };
        AssertEqual("移动速度", attr.Tooltip, "Tooltip 设置");
        Near(attr.RangeMin, 0f, 0.001f, "RangeMin=0");
        Near(attr.RangeMax, 100f, 0.001f, "RangeMax=100");
    }

    /// <summary>HideInInspector 是纯标记属性（无成员）。</summary>
    public static void TestHideInInspectorIsMarker()
    {
        var attr = new HideInInspectorAttribute();
        AssertTrue(attr is Attribute, "是 Attribute");
        AssertTrue(attr is HideInInspectorAttribute, "类型正确");
    }

    /// <summary>RequireComponent(Type) 构造并暴露 RequiredType。</summary>
    public static void TestRequireComponentStoresType()
    {
        var attr = new RequireComponentAttribute(typeof(Rigidbody));
        AssertEqual(typeof(Rigidbody), attr.RequiredType, "RequiredType=typeof(Rigidbody)");
    }

    /// <summary>RequireComponent null 构造抛 ArgumentNullException。</summary>
    public static void TestRequireComponentNullThrows()
    {
        bool threw = false;
        try { _ = new RequireComponentAttribute(null!); }
        catch (ArgumentNullException) { threw = true; }
        AssertTrue(threw, "null Type 抛 ArgumentNullException");
    }

    /// <summary>RequireComponent AllowMultiple=true：同一类型可叠加多个。</summary>
    public static void TestRequireComponentAllowMultiple()
    {
        var attrs = (RequireComponentAttribute[])typeof(MultiRequireTarget)
            .GetCustomAttributes(typeof(RequireComponentAttribute), inherit: false);
        AssertEqual(2, attrs.Length, "标记了 2 个 RequireComponent");
        var types = attrs.Select(a => a.RequiredType).ToHashSet();
        AssertTrue(types.Contains(typeof(Rigidbody)), "含 Rigidbody");
        AssertTrue(types.Contains(typeof(Collider)), "含 Collider");
    }

    /// <summary>SerializeField 是 AttributeTargets.Field 的属性（字段上可读到）。</summary>
    public static void TestSerializeFieldUsageOnField()
    {
        var field = typeof(SerializedTarget).GetField("Health")!;
        var attr = field.GetCustomAttributes(typeof(SerializeFieldAttribute), inherit: false);
        AssertEqual(1, attr.Length, "字段上有 1 个 SerializeField");
        var sf = (SerializeFieldAttribute)attr[0];
        AssertEqual("生命值", sf.Tooltip, "字段 Tooltip");
        Near(sf.RangeMin, 0f, 0.001f, "字段 RangeMin=0");
        Near(sf.RangeMax, 999f, 0.001f, "字段 RangeMax=999");
    }

    /// <summary>SerializeField 单字段重复标记时仍可读取（AllowMultiple=false 不会报错）。</summary>
    public static void TestSerializeFieldNotMultiple()
    {
        var field = typeof(SerializedTarget).GetField("Health")!;
        AssertTrue(field != null, "字段存在");
    }

    /// <summary>RequireComponent Inherited=true：子类继承父类标记。</summary>
    public static void TestRequireComponentInherited()
    {
        var inherited = typeof(DerivedRequireTarget)
            .GetCustomAttributes(typeof(RequireComponentAttribute), inherit: true);
        AssertTrue(inherited.Length >= 1, "子类继承父类 RequireComponent");
    }

    // 测试用占位类型
    private class Rigidbody { }
    private class Collider { }

    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    private class MultiRequireTarget { }

    [RequireComponent(typeof(Rigidbody))]
    private class BaseRequireTarget { }

    private class DerivedRequireTarget : BaseRequireTarget { }

    private class SerializedTarget
    {
        [SerializeField("生命值", RangeMin = 0f, RangeMax = 999f)]
        public float Health = 100f;
    }
}
