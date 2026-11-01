// ============================================================================================
//  Assert.cs
//  yama引擎 (YamaEngine) 断言与契约检查系统
// ============================================================================================
//  提供编译期与运行时的契约验证机制，包括前置条件/后置条件/不可变条件。
//  设计参考 UE check()/verify() + .NET Code Contracts + C++ assert 的最佳实践。
// ============================================================================================

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Diagnostics
{
    /// <summary>
    /// 断言的行为模式。
    /// </summary>
    public enum AssertionMode
    {
        /// <summary>仅 Debug 模式下检查，Release 跳过。</summary>
        DebugOnly,

        /// <summary>所有配置下检查，失败时抛出异常。</summary>
        AlwaysThrow,

        /// <summary>所有配置下检查，失败时触发中断（Attach 调试器）。</summary>
        AlwaysBreak,

        /// <summary>所有配置下检查，仅在日志记录后继续执行。</summary>
        Silent
    }

    /// <summary>
    /// 断言结果处理策略。
    /// </summary>
    public static class AssertSettings
    {
        /// <summary>全局断言模式。默认 DebugOnly。</summary>
        public static AssertionMode Mode { get; set; } = AssertionMode.DebugOnly;

        /// <summary>是否在断言失败时记录日志。</summary>
        public static bool LogOnFailure { get; set; } = true;

        /// <summary>是否捕获断言中产生的异常。</summary>
        public static bool CatchExceptions { get; set; } = true;

        /// <summary>是否启用慢速断言（用于 UI/Editor 弹窗等）。</summary>
        public static bool EnableSlowAssertions { get; set; } = false;

        /// <summary>断言失败时触发的事件（编辑器可订阅以弹窗）。</summary>
        public static event Action<string, string, int>? OnAssertionFailed;

        internal static void FireAssertionFailed(string message, string file, int line)
        {
            OnAssertionFailed?.Invoke(message, file, line);
        }
    }

    /// <summary>
    /// 核心断言类。提供 check()/verify() 风格的契约验证 API。
    /// 用法：
    /// <code>
    /// Assert.That(ptr != null, "指针不应为空");
    /// Assert.IsNotNull(obj, nameof(obj));
    /// Assert.AreEqual(5, result, "结果必须为 5");
    /// Assert.LessThan(value, maxValue, "值超出上限");
    /// </code>
    /// </summary>
    public static class Assert
    {
        // ====== 通用断言 ======

        /// <summary>断言条件为真。</summary>
        [Conditional("DEBUG")]
        public static void That(bool condition, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (condition) return;
            HandleFailure($"Assert.That 失败: {message}", file, line);
        }

        /// <summary>始终运行的断言（Release 也生效）。</summary>
        public static void ThatAlways(bool condition, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (condition) return;
            HandleFailure($"Assert.ThatAlways 失败: {message}", file, line, forceBreak: true);
        }

        // ====== 空值断言 ======

        [Conditional("DEBUG")]
        public static void IsNull(object? obj, string name = "value",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (obj == null) return;
            HandleFailure($"Assert.IsNull 失败: 期望 {name} 为 null，实际非 null", file, line);
        }

        [Conditional("DEBUG")]
        public static void IsNotNull(object? obj, string name = "value",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (obj != null) return;
            HandleFailure($"Assert.IsNotNull 失败: 期望 {name} 非 null", file, line);
        }

        // ====== 相等断言 ======

        [Conditional("DEBUG")]
        public static void AreEqual<T>(T expected, T actual, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IEquatable<T>
        {
            if (expected.Equals(actual)) return;
            HandleFailure(
                $"Assert.AreEqual 失败: 期望 {expected}，实际 {actual}。{message}",
                file, line);
        }

        [Conditional("DEBUG")]
        public static void AreNotEqual<T>(T notExpected, T actual, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IEquatable<T>
        {
            if (!notExpected.Equals(actual)) return;
            HandleFailure(
                $"Assert.AreNotEqual 失败: 不应为 {notExpected}。{message}",
                file, line);
        }

        // ====== 关系断言 ======

        [Conditional("DEBUG")]
        public static void LessThan<T>(T value, T max, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IComparable<T>
        {
            if (value.CompareTo(max) < 0) return;
            HandleFailure($"Assert.LessThan 失败: {value} >= {max}。{message}", file, line);
        }

        [Conditional("DEBUG")]
        public static void LessThanOrEqual<T>(T value, T max, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IComparable<T>
        {
            if (value.CompareTo(max) <= 0) return;
            HandleFailure($"Assert.LessThanOrEqual 失败: {value} > {max}。{message}", file, line);
        }

        [Conditional("DEBUG")]
        public static void GreaterThan<T>(T value, T min, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IComparable<T>
        {
            if (value.CompareTo(min) > 0) return;
            HandleFailure($"Assert.GreaterThan 失败: {value} <= {min}。{message}", file, line);
        }

        [Conditional("DEBUG")]
        public static void GreaterThanOrEqual<T>(T value, T min, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IComparable<T>
        {
            if (value.CompareTo(min) >= 0) return;
            HandleFailure($"Assert.GreaterThanOrEqual 失败: {value} < {min}。{message}", file, line);
        }

        // ====== 范围断言 ======

        [Conditional("DEBUG")]
        public static void InRange<T>(T value, T min, T max, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IComparable<T>
        {
            if (value.CompareTo(min) >= 0 && value.CompareTo(max) <= 0) return;
            HandleFailure($"Assert.InRange 失败: {value} 不在 [{min}, {max}] 范围内。{message}", file, line);
        }

        // ====== 浮点数断言（带精度容差） ======

        [Conditional("DEBUG")]
        public static void AreApproximatelyEqual(float expected, float actual, float tolerance = 1e-6f,
            string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            float diff = Math.Abs(expected - actual);
            if (diff <= tolerance) return;
            HandleFailure(
                $"Assert.AreApproximatelyEqual 失败: 期望 {expected} ± {tolerance}，实际 {actual}（diff={diff}）。{message}",
                file, line);
        }

        [Conditional("DEBUG")]
        public static void AreApproximatelyEqual(double expected, double actual, double tolerance = 1e-12,
            string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            double diff = Math.Abs(expected - actual);
            if (diff <= tolerance) return;
            HandleFailure(
                $"Assert.AreApproximatelyEqual 失败: 期望 {expected} ± {tolerance}，实际 {actual}（diff={diff}）。{message}",
                file, line);
        }

        // ====== 布尔断言 ======

        [Conditional("DEBUG")]
        public static void IsTrue(bool condition, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => That(condition, message, file, line);

        [Conditional("DEBUG")]
        public static void IsFalse(bool condition, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => That(!condition, message, file, line);

        // ====== 类型断言 ======

        [Conditional("DEBUG")]
        public static void IsType<T>(object obj, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (obj is T) return;
            HandleFailure(
                $"Assert.IsType 失败: 期望类型 {typeof(T).Name}，实际 {obj.GetType().Name}。{message}",
                file, line);
        }

        // ====== 操作抛出断言 ======

        [Conditional("DEBUG")]
        public static void Throws<TException>(Action action, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                HandleFailure(
                    $"Assert.Throws 失败: 期望异常 {typeof(TException).Name}，抛出 {ex.GetType().Name}。{message}",
                    file, line);
                return;
            }

            HandleFailure(
                $"Assert.Throws 失败: 期望异常 {typeof(TException).Name}，但未抛出任何异常。{message}",
                file, line);
        }

        // ====== 不会抛出断言 ======

        [Conditional("DEBUG")]
        public static void DoesNotThrow(Action action, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HandleFailure(
                    $"Assert.DoesNotThrow 失败: 抛出异常 {ex.GetType().Name}: {ex.Message}。{message}",
                    file, line);
            }
        }

        // ====== 不可达断言 ======

        [Conditional("DEBUG")]
        public static void Unreachable(string message = "到达了不可达的代码路径",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            HandleFailure($"Assert.Unreachable: {message}", file, line);
        }

        // ====== 内部处理 ======

        private static void HandleFailure(string message, string file, int line, bool forceBreak = false)
        {
            if (AssertSettings.LogOnFailure)
            {
                Log.Error(message, LogCategory.Core);
            }

            AssertSettings.FireAssertionFailed(message, file, line);

            var mode = forceBreak ? AssertionMode.AlwaysBreak : AssertSettings.Mode;

            switch (mode)
            {
                case AssertionMode.AlwaysThrow:
                    throw new AssertionException(message);

                case AssertionMode.AlwaysBreak:
                    Debugger.Break();
                    break;

                case AssertionMode.Silent:
                    // 仅记录日志，继续执行
                    break;

                case AssertionMode.DebugOnly:
                default:
#if DEBUG
                    Debugger.Break();
#endif
                    break;
            }
        }
    }

    // ====== 断言异常 ======
    public class AssertionException : Exception
    {
        public AssertionException(string message)
            : base($"[yama引擎断言失败] {message}")
        {
        }
    }

    // ====== 契约系统 ======
    /// <summary>
    /// 前置条件检查。用于方法开头的参数验证。
    /// </summary>
    public static class Requires
    {
        [Conditional("DEBUG")]
        public static void NotNull(object? obj, string paramName = "parameter",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (obj == null)
            {
                Assert.That(false,
                    $"前置条件失败: 参数 '{paramName}' 不应为 null",
                    file, line);
            }
        }

        [Conditional("DEBUG")]
        public static void Range<T>(T value, T min, T max, string paramName = "parameter",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0) where T : IComparable<T>
        {
            if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
            {
                Assert.That(false,
                    $"前置条件失败: 参数 '{paramName}' 值 {value} 不在 [{min}, {max}] 范围内",
                    file, line);
            }
        }

        [Conditional("DEBUG")]
        public static void True(bool condition, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            Assert.That(condition, $"前置条件失败: {message}", file, line);
        }
    }

    /// <summary>
    /// 后置条件检查。用于方法返回前的契约验证。
    /// </summary>
    public static class Ensures
    {
        [Conditional("DEBUG")]
        public static void NotNull(object? obj, string name = "返回值",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (obj == null)
            {
                Assert.That(false,
                    $"后置条件失败: {name} 不应为 null",
                    file, line);
            }
        }

        [Conditional("DEBUG")]
        public static void True(bool condition, string message = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            Assert.That(condition, $"后置条件失败: {message}", file, line);
        }
    }
}
