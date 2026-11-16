using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace YamaEngine.Tests
{
    /// <summary>
    /// xUnit 适配器：用反射发现并执行本程序集中所有 `public static void Test*(...)` 测试方法。
    /// 兼容两种签名：
    ///   - 无参方法 → 直接调用；
    ///   - (string tempRoot) 方法 → 创建唯一临时目录后调用（与 Program.cs 的 tempRoot 语义一致）。
    /// 保留原有自研测试方法不改动，让 `dotnet test` 能完整跑通全部用例。
    /// </summary>
    public class XunitAdapter
    {
        public static IEnumerable<object[]> AllTestCases()
        {
            var asm = Assembly.GetExecutingAssembly();
            var methods = asm.GetTypes()
                .Where(t => t.IsClass) // 静态类或实例类均可（方法本身是 public static）
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.Name.StartsWith("Test", StringComparison.Ordinal))
                .Where(m => m.ReturnType == typeof(void))
                .Where(m => IsSupportedSignature(m))
                .Select(m => new object[] { m.DeclaringType!.FullName!, m.Name })
                .OrderBy(x => x[0]).ThenBy(x => x[1])
                .ToArray();
            return methods;
        }

        private static bool IsSupportedSignature(MethodInfo m)
        {
            var ps = m.GetParameters();
            if (ps.Length == 0) return true;
            if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) return true;
            return false;
        }

        [Theory]
        [MemberData(nameof(AllTestCases))]
        public void RunTestCase(string typeName, string methodName)
        {
            var type = Type.GetType(typeName)!;
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
            var ps = method.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
            {
                var tempRoot = Path.Combine(Path.GetTempPath(), "YamaEngineTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                try { method.Invoke(null, new object[] { tempRoot }); }
                finally
                {
                    try { Directory.Delete(tempRoot, true); } catch { /* best effort */ }
                }
            }
            else
            {
                method.Invoke(null, null);
            }
        }
    }
}
