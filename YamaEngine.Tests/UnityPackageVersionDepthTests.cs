// UnityPackageVersionDepthTests.cs — Unity5 Package Manager SemVer 深度验证
// 覆盖：PackageVersion Parse（含 v 前缀/+build/-prerelease）、Satisfies（^/~/>=/<=/>/</=/\*）、
//       CompareTo（prerelease 排序在 release 前）、Equals、ToString。
using System;
using YamaEngine.Packages;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityPackageVersionDepthTests
{
    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    // ════════════════════════════════════════════════════════════════
    //  一、Parse 解析
    // ════════════════════════════════════════════════════════════════

    /// <summary>Parse 简单版本 "1.2.3"。</summary>
    public static void TestParseSimple()
    {
        var v = PackageVersion.Parse("1.2.3");
        AssertEqual(1, v.Major, "Major=1");
        AssertEqual(2, v.Minor, "Minor=2");
        AssertEqual(3, v.Patch, "Patch=3");
        AssertTrue(v.Prerelease is null, "无 prerelease");
        AssertTrue(v.Build is null, "无 build");
    }

    /// <summary>Parse 空白 → (0,0,0)。</summary>
    public static void TestParseEmpty()
    {
        var v1 = PackageVersion.Parse("");
        AssertEqual(0, v1.Major, "空串 Major=0");
        AssertEqual(0, v1.Minor, "空串 Minor=0");
        AssertEqual(0, v1.Patch, "空串 Patch=0");

        var v2 = PackageVersion.Parse("   ");
        AssertEqual(0, v2.Major, "空白 Major=0");
    }

    /// <summary>Parse 带 v 前缀 "v2.0.1"。</summary>
    public static void TestParseVPrefix()
    {
        var v = PackageVersion.Parse("v2.0.1");
        AssertEqual(2, v.Major, "Major=2");
        AssertEqual(0, v.Minor, "Minor=0");
        AssertEqual(1, v.Patch, "Patch=1");
    }

    /// <summary>Parse 带 prerelease "1.0.0-alpha"。</summary>
    public static void TestParsePrerelease()
    {
        var v = PackageVersion.Parse("1.0.0-alpha");
        AssertEqual(1, v.Major, "Major=1");
        AssertEqual(0, v.Minor, "Minor=0");
        AssertEqual(0, v.Patch, "Patch=0");
        AssertEqual("alpha", v.Prerelease, "Prerelease=alpha");
    }

    /// <summary>Parse 带 build 元数据 "1.0.0+build.123"。</summary>
    public static void TestParseBuild()
    {
        var v = PackageVersion.Parse("1.0.0+build.123");
        AssertEqual(1, v.Major, "Major=1");
        AssertEqual("build.123", v.Build, "Build=build.123");
        AssertTrue(v.Prerelease is null, "无 prerelease");
    }

    /// <summary>Parse 完整 "v1.2.3-rc.1+meta.456"。</summary>
    public static void TestParseFull()
    {
        var v = PackageVersion.Parse("v1.2.3-rc.1+meta.456");
        AssertEqual(1, v.Major, "Major=1");
        AssertEqual(2, v.Minor, "Minor=2");
        AssertEqual(3, v.Patch, "Patch=3");
        AssertEqual("rc.1", v.Prerelease, "Prerelease=rc.1");
        AssertEqual("meta.456", v.Build, "Build=meta.456");
    }

    /// <summary>Parse 部分版本 "1.2" → Patch 默认 0。</summary>
    public static void TestParsePartial()
    {
        var v = PackageVersion.Parse("1.2");
        AssertEqual(1, v.Major, "Major=1");
        AssertEqual(2, v.Minor, "Minor=2");
        AssertEqual(0, v.Patch, "Patch 默认 0");
    }

    // ════════════════════════════════════════════════════════════════
    //  二、Satisfies 范围匹配
    // ════════════════════════════════════════════════════════════════

    /// <summary>Satisfies("*") 和空串始终匹配。</summary>
    public static void TestSatisfiesAny()
    {
        var v = PackageVersion.Parse("1.2.3");
        AssertTrue(v.Satisfies("*"), "* 始终匹配");
        AssertTrue(v.Satisfies(""), "空串始终匹配");
        AssertTrue(v.Satisfies("   "), "空白始终匹配");
    }

    /// <summary>Satisfies 精确匹配 "1.2.3"。</summary>
    public static void TestSatisfiesExact()
    {
        var v = PackageVersion.Parse("1.2.3");
        AssertTrue(v.Satisfies("1.2.3"), "精确匹配");
        AssertFalse(v.Satisfies("1.2.4"), "不匹配不同 Patch");
        AssertFalse(v.Satisfies("2.0.0"), "不匹配不同 Major");
    }

    /// <summary>Satisfies "^1.2.3" 允许同主版本升级。</summary>
    public static void TestSatisfiesCaret()
    {
        var v1 = PackageVersion.Parse("1.2.3");
        AssertTrue(v1.Satisfies("^1.2.3"), "自身满足 ^1.2.3");
        AssertTrue(v1.Satisfies("^1.0.0"), "^1.0.0 允许 1.2.3");

        var v2 = PackageVersion.Parse("1.9.9");
        AssertTrue(v2.Satisfies("^1.2.3"), "^1.2.3 允许 1.9.9");

        var v3 = PackageVersion.Parse("2.0.0");
        AssertFalse(v3.Satisfies("^1.2.3"), "^1.2.3 不允许 2.0.0");

        var v4 = PackageVersion.Parse("1.2.2");
        AssertFalse(v4.Satisfies("^1.2.3"), "^1.2.3 不允许 1.2.2");
    }

    /// <summary>Satisfies "~1.2.3" 允许同次版本补丁升级。</summary>
    public static void TestSatisfiesTilde()
    {
        var v1 = PackageVersion.Parse("1.2.5");
        AssertTrue(v1.Satisfies("~1.2.3"), "~1.2.3 允许 1.2.5");

        var v2 = PackageVersion.Parse("1.3.0");
        AssertFalse(v2.Satisfies("~1.2.3"), "~1.2.3 不允许 1.3.0");

        var v3 = PackageVersion.Parse("1.2.2");
        AssertFalse(v3.Satisfies("~1.2.3"), "~1.2.3 不允许 1.2.2");
    }

    /// <summary>Satisfies 比较运算符 >=/<=/>/。</summary>
    public static void TestSatisfiesComparison()
    {
        var v = PackageVersion.Parse("1.5.0");
        AssertTrue(v.Satisfies(">=1.0.0"), ">=1.0.0 满足");
        AssertTrue(v.Satisfies("<=1.5.0"), "<=1.5.0 满足（等于）");
        AssertTrue(v.Satisfies(">1.4.9"), ">1.4.9 满足");
        AssertTrue(v.Satisfies("<1.5.1"), "<1.5.1 满足");
        AssertFalse(v.Satisfies(">=2.0.0"), ">=2.0.0 不满足");
        AssertFalse(v.Satisfies("<1.0.0"), "<1.0.0 不满足");
        AssertFalse(v.Satisfies(">1.5.0"), ">1.5.0 不满足（严格大于）");
    }

    /// <summary>Satisfies ^ 范围不允许 prerelease。</summary>
    public static void TestSatisfiesCaretNoPrerelease()
    {
        var v = PackageVersion.Parse("1.5.0-beta");
        AssertFalse(v.Satisfies("^1.0.0"), "^1.0.0 不允许 prerelease 版本");
    }

    // ════════════════════════════════════════════════════════════════
    //  三、CompareTo / 排序
    // ════════════════════════════════════════════════════════════════

    /// <summary>CompareTo 按 Major > Minor > Patch 优先级。</summary>
    public static void TestCompareToPrecedence()
    {
        var a = PackageVersion.Parse("1.0.0");
        var b = PackageVersion.Parse("2.0.0");
        var c = PackageVersion.Parse("1.1.0");
        var d = PackageVersion.Parse("1.0.1");

        AssertTrue(a < b, "1.0.0 < 2.0.0");
        AssertTrue(a < c, "1.0.0 < 1.1.0");
        AssertTrue(a < d, "1.0.0 < 1.0.1");
        AssertTrue(d < c, "1.0.1 < 1.1.0");
        AssertTrue(c < b, "1.1.0 < 2.0.0");
    }

    /// <summary>CompareTo prerelease 排在 release 前（SemVer 规范）。</summary>
    public static void TestCompareToPrereleaseBeforeRelease()
    {
        var pre = PackageVersion.Parse("1.0.0-alpha");
        var rel = PackageVersion.Parse("1.0.0");

        AssertTrue(pre < rel, "1.0.0-alpha < 1.0.0（prerelease 在前）");
        AssertTrue(rel > pre, "1.0.0 > 1.0.0-alpha");
    }

    /// <summary>CompareTo 不同 prerelease 字符串按字典序。</summary>
    public static void TestCompareToPrereleaseLexical()
    {
        var alpha = PackageVersion.Parse("1.0.0-alpha");
        var beta = PackageVersion.Parse("1.0.0-beta");

        AssertTrue(alpha < beta, "alpha < beta");
    }

    /// <summary>CompareTo null 返回 1（任何版本 > null）。</summary>
    public static void TestCompareToNull()
    {
        var v = PackageVersion.Parse("1.0.0");
        AssertEqual(1, v.CompareTo(null), "任何版本 > null");
    }

    // ════════════════════════════════════════════════════════════════
    //  四、Equals / GetHashCode / ToString
    // ════════════════════════════════════════════════════════════════

    /// <summary>Equals 比较 Major/Minor/Patch/Prerelease（忽略 Build）。</summary>
    public static void TestEqualsIgnoresBuild()
    {
        var a = new PackageVersion(1, 0, 0, null, "build.1");
        var b = new PackageVersion(1, 0, 0, null, "build.2");

        AssertTrue(a == b, "Equals 忽略 Build");
        AssertTrue(a.Equals(b), "Equals 方法忽略 Build");
    }

    /// <summary>Equals 区分 Prerelease。</summary>
    public static void TestEqualsDistinguishesPrerelease()
    {
        var a = PackageVersion.Parse("1.0.0-alpha");
        var b = PackageVersion.Parse("1.0.0");

        AssertTrue(a != b, "alpha != release");
        AssertFalse(a.Equals(b), "alpha 不等于 release");
    }

    /// <summary>ToString 还原 "M.m.p[-pre][+build]"。</summary>
    public static void TestToString()
    {
        AssertEqual("1.2.3", PackageVersion.Parse("1.2.3").ToString(), "无后缀");
        AssertEqual("1.2.3-alpha", PackageVersion.Parse("1.2.3-alpha").ToString(), "带 prerelease");
        AssertEqual("1.2.3+build.1", PackageVersion.Parse("1.2.3+build.1").ToString(), "带 build");
        AssertEqual("1.2.3-rc.1+meta",
            PackageVersion.Parse("1.2.3-rc.1+meta").ToString(), "完整");
    }

    /// <summary>构造函数直接构造版本。</summary>
    public static void TestCtor()
    {
        var v = new PackageVersion(2, 3, 4, "beta", "build.9");
        AssertEqual(2, v.Major, "Major=2");
        AssertEqual(3, v.Minor, "Minor=3");
        AssertEqual(4, v.Patch, "Patch=4");
        AssertEqual("beta", v.Prerelease, "Prerelease=beta");
        AssertEqual("build.9", v.Build, "Build=build.9");
    }

    /// <summary>比较运算符 ==/!=/</>/<=/>= 全套。</summary>
    public static void TestAllOperators()
    {
        var a = PackageVersion.Parse("1.0.0");
        var b = PackageVersion.Parse("1.0.0");
        var c = PackageVersion.Parse("2.0.0");

        AssertTrue(a == b, "1.0.0 == 1.0.0");
        AssertTrue(a != c, "1.0.0 != 2.0.0");
        AssertTrue(a < c, "1.0.0 < 2.0.0");
        AssertTrue(c > a, "2.0.0 > 1.0.0");
        AssertTrue(a <= b, "1.0.0 <= 1.0.0");
        AssertTrue(a >= b, "1.0.0 >= 1.0.0");
    }
}
