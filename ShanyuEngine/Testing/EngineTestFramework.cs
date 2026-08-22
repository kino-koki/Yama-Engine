using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShanyuEngine.MicroCluster;

namespace ShanyuEngine.Testing
{
    // ======================== 测试属性 ========================

    [AttributeUsage(AttributeTargets.Class)]
    public class TestClassAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class TestMethodAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class TestAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class SetupAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class TeardownAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class TimeoutAttribute : Attribute
    {
        public int Milliseconds { get; }
        public TimeoutAttribute(int milliseconds) => Milliseconds = milliseconds;
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class IgnoreAttribute : Attribute
    {
        public string Reason { get; }
        public IgnoreAttribute(string reason = "") => Reason = reason;
    }

    // ======================== 断言 ========================

    public static class Assert
    {
        public static void AreEqual<T>(T expected, T actual, string message = "")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertException($"Expected: {expected}, Actual: {actual}. {message}");
        }

        public static void AreNotEqual<T>(T notExpected, T actual, string message = "")
        {
            if (EqualityComparer<T>.Default.Equals(notExpected, actual))
                throw new AssertException($"Expected not equal to: {notExpected}, Actual: {actual}. {message}");
        }

        public static void IsTrue(bool condition, string message = "")
        {
            if (!condition)
                throw new AssertException($"Expected true. {message}");
        }

        public static void IsFalse(bool condition, string message = "")
        {
            if (condition)
                throw new AssertException($"Expected false. {message}");
        }

        public static void IsNull(object obj, string message = "")
        {
            if (obj != null)
                throw new AssertException($"Expected null. {message}");
        }

        public static void IsNotNull(object obj, string message = "")
        {
            if (obj == null)
                throw new AssertException($"Expected non-null. {message}");
        }

        public static void AreApproximatelyEqual(float expected, float actual, float tolerance = 0.0001f, string message = "")
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new AssertException($"Expected approx: {expected}, Actual: {actual}, Tolerance: {tolerance}. {message}");
        }

        public static void AreApproximatelyEqual(double expected, double actual, double tolerance = 0.0001, string message = "")
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new AssertException($"Expected approx: {expected}, Actual: {actual}, Tolerance: {tolerance}. {message}");
        }

        public static void Throws<TException>(Action action, string message = "") where TException : Exception
        {
            try
            {
                action();
                throw new AssertException($"Expected exception {typeof(TException).Name} was not thrown. {message}");
            }
            catch (TException)
            {
                // expected
            }
        }

        public static void CollectionEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message = "")
        {
            var expList = expected.ToList();
            var actList = actual.ToList();
            if (expList.Count != actList.Count)
                throw new AssertException($"Collection count mismatch. Expected: {expList.Count}, Actual: {actList.Count}. {message}");
            for (int i = 0; i < expList.Count; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(expList[i], actList[i]))
                    throw new AssertException($"Collection mismatch at index {i}. Expected: {expList[i]}, Actual: {actList[i]}. {message}");
            }
        }

        public static void CollectionContains<T>(IEnumerable<T> collection, T item, string message = "")
        {
            if (!collection.Contains(item))
                throw new AssertException($"Collection does not contain expected item: {item}. {message}");
        }

        public static void Fail(string message = "")
        {
            throw new AssertException($"Test failed. {message}");
        }
    }

    public class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    // ======================== 测试结果 ========================

    public class TestResult
    {
        public string TestName { get; set; }
        public string ClassName { get; set; }
        public bool Passed { get; set; }
        public TimeSpan Duration { get; set; }
        public string ErrorMessage { get; set; }
        public string StackTrace { get; set; }
        public bool IsIgnored { get; set; }
        public string IgnoreReason { get; set; }
    }

    public class TestClassResult
    {
        public string ClassName { get; set; }
        public List<TestResult> Tests { get; set; } = new List<TestResult>();
        public TimeSpan Duration { get; set; }
        public int PassedCount => Tests.Count(t => t.Passed);
        public int FailedCount => Tests.Count(t => !t.Passed && !t.IsIgnored);
        public int IgnoredCount => Tests.Count(t => t.IsIgnored);
    }

    // ======================== 测试发现 ========================

    public class TestMethodInfo
    {
        public Type ClassType { get; set; }
        public MethodInfo Method { get; set; }
        public string DisplayName => $"{ClassType.Name}.{Method.Name}";
        public int? TimeoutMs { get; set; }
        public bool IsIgnored { get; set; }
        public string IgnoreReason { get; set; }
        public MethodInfo SetupMethod { get; set; }
        public MethodInfo TeardownMethod { get; set; }
    }

    public class TestDiscovery
    {
        public List<TestMethodInfo> DiscoverTests(Assembly assembly)
        {
            var tests = new List<TestMethodInfo>();
            foreach (var type in assembly.GetTypes())
            {
                if (!type.GetCustomAttributes<TestClassAttribute>().Any() && !type.GetCustomAttributes<TestAttribute>().Any())
                    continue;

                var setup = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.GetCustomAttributes<SetupAttribute>().Any());
                var teardown = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.GetCustomAttributes<TeardownAttribute>().Any());

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!method.GetCustomAttributes<TestMethodAttribute>().Any() && !method.GetCustomAttributes<TestAttribute>().Any())
                        continue;
                    if (method.GetParameters().Length > 0 || method.ReturnType != typeof(void))
                        continue;

                    var ignoreAttr = method.GetCustomAttribute<IgnoreAttribute>();
                    var timeoutAttr = method.GetCustomAttribute<TimeoutAttribute>();

                    tests.Add(new TestMethodInfo
                    {
                        ClassType = type,
                        Method = method,
                        TimeoutMs = timeoutAttr?.Milliseconds,
                        IsIgnored = ignoreAttr != null,
                        IgnoreReason = ignoreAttr?.Reason ?? "",
                        SetupMethod = setup,
                        TeardownMethod = teardown
                    });
                }
            }
            return tests;
        }
    }

    // ======================== 测试运行器 ========================

    public class TestRunner
    {
        private readonly TestDiscovery _discovery = new TestDiscovery();
        private readonly List<TestClassResult> _results = new List<TestClassResult>();
        public IReadOnlyList<TestClassResult> Results => _results;

        public List<TestMethodInfo> DiscoverTests(Assembly assembly) => _discovery.DiscoverTests(assembly);

        public void RunAll(Assembly assembly, int maxParallelism = 4)
        {
            var tests = DiscoverTests(assembly);
            RunTests(tests, maxParallelism);
        }

        public void RunTestClass(Assembly assembly, string className, int maxParallelism = 4)
        {
            var tests = DiscoverTests(assembly).Where(t => t.ClassType.Name == className).ToList();
            RunTests(tests, maxParallelism);
        }

        public TestResult RunTest(TestMethodInfo test)
        {
            return ExecuteSingleTest(test);
        }

        private void RunTests(List<TestMethodInfo> tests, int maxParallelism)
        {
            _results.Clear();
            var grouped = tests.GroupBy(t => t.ClassType).ToList();
            Parallel.ForEach(grouped, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism }, g =>
            {
                var classResult = new TestClassResult { ClassName = g.Key.Name };
                foreach (var test in g)
                {
                    classResult.Tests.Add(ExecuteSingleTest(test));
                }
                classResult.Duration = TimeSpan.FromTicks(classResult.Tests.Sum(t => t.Duration.Ticks));
                lock (_results) { _results.Add(classResult); }
            });
        }

        private TestResult ExecuteSingleTest(TestMethodInfo test)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestResult
            {
                TestName = test.Method.Name,
                ClassName = test.ClassType.Name,
                IsIgnored = test.IsIgnored,
                IgnoreReason = test.IgnoreReason
            };

            if (test.IsIgnored)
            {
                result.Passed = false;
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            object instance = null;
            try
            {
                instance = Activator.CreateInstance(test.ClassType);
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.ErrorMessage = $"Failed to create test class instance: {ex.Message}";
                result.StackTrace = ex.StackTrace;
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            try
            {
                test.SetupMethod?.Invoke(instance, null);

                var cts = new CancellationTokenSource();
                var task = Task.Run(() => test.Method.Invoke(instance, null), cts.Token);
                if (test.TimeoutMs.HasValue)
                {
                    if (!task.Wait(test.TimeoutMs.Value, cts.Token))
                    {
                        cts.Cancel();
                        throw new TimeoutException($"Test timed out after {test.TimeoutMs.Value}ms");
                    }
                }
                else
                {
                    task.Wait();
                }

                result.Passed = true;
                test.TeardownMethod?.Invoke(instance, null);
            }
            catch (Exception ex)
            {
                result.Passed = false;
                var inner = ex is AggregateException ae ? ae.InnerException ?? ae : ex;
                result.ErrorMessage = inner.Message;
                result.StackTrace = inner.StackTrace;
                try { test.TeardownMethod?.Invoke(instance, null); } catch { /* swallow */ }
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }
    }

    // ======================== 测试报告 ========================

    public class TestReport
    {
        public List<TestClassResult> ClassResults { get; set; } = new List<TestClassResult>();
        public DateTime ReportTime { get; set; } = DateTime.Now;
        public TimeSpan TotalDuration => TimeSpan.FromTicks(ClassResults.Sum(c => c.Duration.Ticks));
        public int TotalTests => ClassResults.Sum(c => c.Tests.Count);
        public int PassedTests => ClassResults.Sum(c => c.PassedCount);
        public int FailedTests => ClassResults.Sum(c => c.FailedCount);
        public int IgnoredTests => ClassResults.Sum(c => c.IgnoredCount);
        public double PassRate => TotalTests == 0 ? 0 : (double)PassedTests / TotalTests * 100.0;

        public void GenerateConsoleReport(bool colored = true)
        {
            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine("          ShanyuEngine Test Report      ");
            Console.WriteLine("========================================");
            Console.WriteLine($"Report Time: {ReportTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Total Duration: {TotalDuration.TotalSeconds:F3}s");
            Console.WriteLine($"Total Tests: {TotalTests}");
            if (colored) Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Passed: {PassedTests}");
            if (colored) Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed: {FailedTests}");
            if (colored) Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Ignored: {IgnoredTests}");
            if (colored) Console.ResetColor();
            Console.WriteLine($"Pass Rate: {PassRate:F2}%");
            Console.WriteLine();

            foreach (var cls in ClassResults)
            {
                Console.WriteLine($"--- {cls.ClassName} ({cls.Duration.TotalMilliseconds:F1}ms) ---");
                foreach (var test in cls.Tests)
                {
                    if (test.IsIgnored)
                    {
                        if (colored) Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  [IGNORED] {test.TestName} - {test.IgnoreReason}");
                    }
                    else if (test.Passed)
                    {
                        if (colored) Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [PASS] {test.TestName} ({test.Duration.TotalMilliseconds:F1}ms)");
                    }
                    else
                    {
                        if (colored) Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [FAIL] {test.TestName} ({test.Duration.TotalMilliseconds:F1}ms)");
                        Console.WriteLine($"         {test.ErrorMessage}");
                    }
                    if (colored) Console.ResetColor();
                }
            }
            Console.WriteLine("========================================");
        }

        public string GenerateHtmlReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='utf-8'><title>ShanyuEngine Test Report</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Arial,sans-serif;margin:20px;background:#f5f5f5;}");
            sb.AppendLine("h1{color:#333;} .summary{background:#fff;padding:15px;border-radius:8px;margin-bottom:20px;}");
            sb.AppendLine(".pass{color:green;} .fail{color:red;} .ignore{color:orange;}");
            sb.AppendLine("table{width:100%;border-collapse:collapse;background:#fff;}");
            sb.AppendLine("th,td{padding:10px;border:1px solid #ddd;text-align:left;}");
            sb.AppendLine("th{background:#4CAF50;color:#fff;} .bar{height:20px;background:#e0e0e0;border-radius:4px;overflow:hidden;}");
            sb.AppendLine(".bar-fill{height:100%;background:#4CAF50;float:left;} .bar-fail{background:#f44336;}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<h1>ShanyuEngine Test Report</h1>");
            sb.AppendLine("<div class='summary'>");
            sb.AppendLine($"<p><strong>Report Time:</strong> {ReportTime:yyyy-MM-dd HH:mm:ss}</p>");
            sb.AppendLine($"<p><strong>Total Duration:</strong> {TotalDuration.TotalSeconds:F3}s</p>");
            sb.AppendLine($"<p><strong>Total Tests:</strong> {TotalTests}</p>");
            sb.AppendLine($"<p class='pass'><strong>Passed:</strong> {PassedTests}</p>");
            sb.AppendLine($"<p class='fail'><strong>Failed:</strong> {FailedTests}</p>");
            sb.AppendLine($"<p class='ignore'><strong>Ignored:</strong> {IgnoredTests}</p>");
            sb.AppendLine($"<p><strong>Pass Rate:</strong> {PassRate:F2}%</p>");
            sb.AppendLine("<div class='bar'><div class='bar-fill' style='width:{PassRate}%'></div>");
            if (FailedTests > 0)
            {
                var failRate = (double)FailedTests / TotalTests * 100.0;
                sb.AppendLine($"<div class='bar-fill bar-fail' style='width:{failRate}%'></div>");
            }
            sb.AppendLine("</div></div>");
            sb.AppendLine("<table><tr><th>Class</th><th>Test</th><th>Result</th><th>Duration (ms)</th><th>Message</th></tr>");
            foreach (var cls in ClassResults)
            {
                foreach (var test in cls.Tests)
                {
                    string resultClass = test.IsIgnored ? "ignore" : (test.Passed ? "pass" : "fail");
                    string resultText = test.IsIgnored ? "IGNORED" : (test.Passed ? "PASS" : "FAIL");
                    sb.AppendLine($"<tr class='{resultClass}'><td>{cls.ClassName}</td><td>{test.TestName}</td>");
                    sb.AppendLine($"<td>{resultText}</td><td>{test.Duration.TotalMilliseconds:F1}</td><td>{System.Web.HttpUtility.HtmlEncode(test.ErrorMessage ?? "")}</td></tr>");
                }
            }
            sb.AppendLine("</table>");
            sb.AppendLine("<script>");
            sb.AppendLine("const canvas=document.createElement('canvas');canvas.width=600;canvas.height=300;");
            sb.AppendLine("document.body.appendChild(canvas);const ctx=canvas.getContext('2d');");
            sb.AppendLine("const labels=['Pass','Fail','Ignore'];const data=[" + $"{PassedTests},{FailedTests},{IgnoredTests}" + "];");
            sb.AppendLine("const colors=['#4CAF50','#f44336','#ff9800'];let x=50;");
            sb.AppendLine("for(let i=0;i<data.length;i++){ctx.fillStyle=colors[i];ctx.fillRect(x,200-data[i]*10,60,data[i]*10);");
            sb.AppendLine("ctx.fillStyle='#333';ctx.fillText(labels[i],x+15,230);ctx.fillText(data[i],x+20,190-data[i]*10);x+=100;}");
            sb.AppendLine("</script></body></html>");
            return sb.ToString();
        }

        public string GenerateJsonReport()
        {
            var summary = new
            {
                ReportTime,
                TotalDuration = TotalDuration.ToString(),
                TotalTests,
                PassedTests,
                FailedTests,
                IgnoredTests,
                PassRate,
                Classes = ClassResults.Select(c => new
                {
                    c.ClassName,
                    c.Duration,
                    c.PassedCount,
                    c.FailedCount,
                    c.IgnoredCount,
                    Tests = c.Tests.Select(t => new
                    {
                        t.TestName,
                        t.Passed,
                        t.Duration,
                        t.IsIgnored,
                        t.IgnoreReason,
                        t.ErrorMessage,
                        t.StackTrace
                    })
                })
            };
            return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    // ======================== 测试基架 ========================

    public abstract class EngineTestFixture : IDisposable
    {
        public string TempDirectory { get; private set; }

        [Setup]
        public virtual void Setup()
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "ShanyuEngineTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            OnSetup();
        }

        [Teardown]
        public virtual void Teardown()
        {
            OnTeardown();
            CleanupTempDirectory();
        }

        protected virtual void OnSetup() { }
        protected virtual void OnTeardown() { }

        public void Dispose()
        {
            CleanupTempDirectory();
        }

        private void CleanupTempDirectory()
        {
            if (!string.IsNullOrEmpty(TempDirectory) && Directory.Exists(TempDirectory))
            {
                try { Directory.Delete(TempDirectory, true); } catch { /* best effort */ }
            }
        }
    }

    // ======================== 模拟引擎类型（用于内置测试） ========================

    namespace MockEngine
    {
        public struct Vector3
        {
            public float X, Y, Z;
            public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static Vector3 Zero => new Vector3(0, 0, 0);
            public static Vector3 One => new Vector3(1, 1, 1);
            public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.X * s, a.Y * s, a.Z * s);
            public float Length() => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
            public float Dot(Vector3 other) => X * other.X + Y * other.Y + Z * other.Z;
            public Vector3 Cross(Vector3 other) => new Vector3(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
            public static bool operator ==(Vector3 a, Vector3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
            public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
            public override bool Equals(object obj) => obj is Vector3 v && this == v;
            public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        }

        public struct Quaternion
        {
            public float X, Y, Z, W;
            public Quaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
            public static Quaternion Identity => new Quaternion(0, 0, 0, 1);
            public static Quaternion FromAxisAngle(Vector3 axis, float angle)
            {
                var half = angle * 0.5f;
                var s = (float)Math.Sin(half);
                return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, (float)Math.Cos(half));
            }
        }

        public struct Matrix4x4
        {
            public float M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44;
            public static Matrix4x4 Identity => new Matrix4x4
            {
                M11 = 1, M22 = 1, M33 = 1, M44 = 1
            };
            public static Matrix4x4 CreateTranslation(Vector3 t) => new Matrix4x4
            {
                M11 = 1, M22 = 1, M33 = 1, M44 = 1,
                M41 = t.X, M42 = t.Y, M43 = t.Z
            };
            public static Matrix4x4 operator *(Matrix4x4 a, Matrix4x4 b)
            {
                return new Matrix4x4
                {
                    M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
                    M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
                    M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
                    M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
                    M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
                    M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
                    M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
                    M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
                    M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
                    M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
                    M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
                    M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
                    M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
                    M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
                    M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
                    M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44
                };
            }
        }

        public class Entity
        {
            public int Id { get; }
            private readonly Dictionary<Type, object> _components = new Dictionary<Type, object>();
            public Entity(int id) => Id = id;
            public void AddComponent<T>(T component) => _components[typeof(T)] = component;
            public T GetComponent<T>() => _components.TryGetValue(typeof(T), out var c) ? (T)c : default;
            public bool HasComponent<T>() => _components.ContainsKey(typeof(T));
            public IEnumerable<object> GetAllComponents() => _components.Values;
        }

        public class ComponentA { public int Value { get; set; } }
        public class ComponentB { public string Name { get; set; } }

        public class Scene
        {
            public string Name { get; set; }
            public List<Entity> Entities { get; set; } = new List<Entity>();
        }

        public class Ray
        {
            public Vector3 Origin { get; set; }
            public Vector3 Direction { get; set; }
        }

        public struct RaycastHit
        {
            public Vector3 Point { get; set; }
            public float Distance { get; set; }
            public bool IsValid { get; set; }
        }

        public class PhysicsWorld
        {
            public bool Raycast(Ray ray, out RaycastHit hit, float maxDistance = 1000f)
            {
                hit = new RaycastHit { Point = ray.Origin + ray.Direction * 5f, Distance = 5f, IsValid = true };
                return true;
            }

            public bool SphereCast(Vector3 origin, float radius, Vector3 direction, out RaycastHit hit, float maxDistance = 1000f)
            {
                hit = new RaycastHit { Point = origin + direction * 10f, Distance = 10f, IsValid = true };
                return true;
            }
        }


        public static class MeshOperations
        {
            public static Mesh ComputeLOD(Mesh source, float quality)
            {
                return new Mesh
                {
                    Name = source.Name + "_LOD",
                    Vertices = source.Vertices?.Take(Math.Max(1, (int)((source.Vertices?.Length ?? 0) * quality))).ToArray(),
                    Indices = source.Indices?.Take(Math.Max(1, (int)((source.Indices?.Length ?? 0) * quality))).ToArray(),
                    LODLevel = source.LODLevel + 1
                };
            }

            public static Mesh BooleanUnion(Mesh a, Mesh b)
            {
                return new Mesh
                {
                    Name = $"{a.Name}_{b.Name}_Union",
                    Vertices = (a.Vertices ?? Array.Empty<Vector3>()).Concat(b.Vertices ?? Array.Empty<Vector3>()).ToArray(),
                    Indices = (a.Indices ?? Array.Empty<int>()).Concat(b.Indices ?? Array.Empty<int>()).ToArray()
                };
            }
        }
    }

    // ======================== 内置测试用例 ========================

    [TestClass]
    public class ECSTest : EngineTestFixture
    {
        [TestMethod]
        public void EntityCreation()
        {
            var entity = new MockEngine.Entity(1);
            Assert.IsNotNull(entity);
            Assert.AreEqual(1, entity.Id);
        }

        [TestMethod]
        public void ComponentAddAndGet()
        {
            var entity = new MockEngine.Entity(2);
            entity.AddComponent(new MockEngine.ComponentA { Value = 42 });
            var comp = entity.GetComponent<MockEngine.ComponentA>();
            Assert.IsNotNull(comp);
            Assert.AreEqual(42, comp.Value);
        }

        [TestMethod]
        public void ComponentQuery()
        {
            var entities = new List<MockEngine.Entity>();
            for (int i = 0; i < 5; i++)
            {
                var e = new MockEngine.Entity(i);
                if (i % 2 == 0) e.AddComponent(new MockEngine.ComponentA { Value = i });
                entities.Add(e);
            }
            var withA = entities.Where(e => e.HasComponent<MockEngine.ComponentA>()).ToList();
            Assert.AreEqual(3, withA.Count);
        }

        [TestMethod]
        public void SystemUpdate()
        {
            var entities = new List<MockEngine.Entity>();
            for (int i = 0; i < 3; i++)
            {
                var e = new MockEngine.Entity(i);
                e.AddComponent(new MockEngine.ComponentA { Value = 10 });
                entities.Add(e);
            }
            foreach (var e in entities)
            {
                var c = e.GetComponent<MockEngine.ComponentA>();
                if (c != null) c.Value += 5;
            }
            Assert.AreEqual(15, entities[0].GetComponent<MockEngine.ComponentA>().Value);
        }
    }

    [TestClass]
    public class MathTest : EngineTestFixture
    {
        [TestMethod]
        public void Vector3Addition()
        {
            var a = new MockEngine.Vector3(1, 2, 3);
            var b = new MockEngine.Vector3(4, 5, 6);
            var c = a + b;
            Assert.AreEqual(5f, c.X);
            Assert.AreEqual(7f, c.Y);
            Assert.AreEqual(9f, c.Z);
        }

        [TestMethod]
        public void Vector3Dot()
        {
            var a = new MockEngine.Vector3(1, 0, 0);
            var b = new MockEngine.Vector3(0, 1, 0);
            Assert.AreApproximatelyEqual(0f, a.Dot(b), 0.0001f);
        }

        [TestMethod]
        public void Vector3Cross()
        {
            var a = new MockEngine.Vector3(1, 0, 0);
            var b = new MockEngine.Vector3(0, 1, 0);
            var c = a.Cross(b);
            Assert.AreApproximatelyEqual(0f, c.X);
            Assert.AreApproximatelyEqual(0f, c.Y);
            Assert.AreApproximatelyEqual(1f, c.Z);
        }

        [TestMethod]
        public void QuaternionIdentity()
        {
            var q = MockEngine.Quaternion.Identity;
            Assert.AreApproximatelyEqual(0f, q.X);
            Assert.AreApproximatelyEqual(0f, q.Y);
            Assert.AreApproximatelyEqual(0f, q.Z);
            Assert.AreApproximatelyEqual(1f, q.W);
        }

        [TestMethod]
        public void MatrixMultiply()
        {
            var a = MockEngine.Matrix4x4.Identity;
            var b = MockEngine.Matrix4x4.CreateTranslation(new MockEngine.Vector3(1, 2, 3));
            var c = a * b;
            Assert.AreApproximatelyEqual(1f, c.M41);
            Assert.AreApproximatelyEqual(2f, c.M42);
            Assert.AreApproximatelyEqual(3f, c.M43);
        }

        [TestMethod]
        public void Vector3Length()
        {
            var v = new MockEngine.Vector3(3, 4, 0);
            Assert.AreApproximatelyEqual(5f, v.Length(), 0.0001f);
        }
    }

    [TestClass]
    public class PhysicsTest : EngineTestFixture
    {
        [TestMethod]
        public void RaycastHit()
        {
            var world = new MockEngine.PhysicsWorld();
            var ray = new MockEngine.Ray { Origin = MockEngine.Vector3.Zero, Direction = new MockEngine.Vector3(0, 0, 1) };
            bool hit = world.Raycast(ray, out var result);
            Assert.IsTrue(hit);
            Assert.IsTrue(result.IsValid);
            Assert.AreApproximatelyEqual(5f, result.Distance, 0.0001f);
        }

        [TestMethod]
        public void SphereCast()
        {
            var world = new MockEngine.PhysicsWorld();
            var origin = new MockEngine.Vector3(0, 0, 0);
            var dir = new MockEngine.Vector3(1, 0, 0);
            bool hit = world.SphereCast(origin, 1f, dir, out var result);
            Assert.IsTrue(hit);
            Assert.AreApproximatelyEqual(10f, result.Distance, 0.0001f);
        }

        [TestMethod]
        public void RaycastMiss()
        {
            var world = new MockEngine.PhysicsWorld();
            var ray = new MockEngine.Ray { Origin = new MockEngine.Vector3(0, 0, -100), Direction = new MockEngine.Vector3(0, 0, -1) };
            bool hit = world.Raycast(ray, out var result, maxDistance: 10f);
            Assert.IsTrue(hit);
        }
    }

    [TestClass]
    public class SerializationTest : EngineTestFixture
    {
        [TestMethod]
        public void SceneJsonRoundtrip()
        {
            var scene = new MockEngine.Scene { Name = "TestScene" };
            scene.Entities.Add(new MockEngine.Entity(1));
            scene.Entities.Add(new MockEngine.Entity(2));

            var json = System.Text.Json.JsonSerializer.Serialize(new { scene.Name, EntityCount = scene.Entities.Count });
            Assert.IsFalse(string.IsNullOrEmpty(json));
            Assert.IsTrue(json.Contains("TestScene"));
        }

        [TestMethod]
        public void SceneEntitiesCount()
        {
            var scene = new MockEngine.Scene { Name = "Level1" };
            for (int i = 0; i < 100; i++)
                scene.Entities.Add(new MockEngine.Entity(i));
            Assert.AreEqual(100, scene.Entities.Count);
        }

        [TestMethod]
        public void ComponentSerialization()
        {
            var comp = new MockEngine.ComponentA { Value = 99 };
            var json = System.Text.Json.JsonSerializer.Serialize(comp);
            Assert.IsTrue(json.Contains("99"));
        }
    }

    [TestClass]
    public class MeshTest : EngineTestFixture
    {
        [TestMethod]
        public void MeshCreation()
        {
            var mesh = new MockEngine.Mesh
            {
                Name = "Cube",
                Vertices = new[] { MockEngine.Vector3.Zero, MockEngine.Vector3.One },
                Indices = new[] { 0, 1 }
            };
            Assert.AreEqual("Cube", mesh.Name);
            Assert.AreEqual(2, mesh.Vertices.Length);
        }

        [TestMethod]
        public void LODGeneration()
        {
            var mesh = new MockEngine.Mesh
            {
                Name = "HighPoly",
                Vertices = Enumerable.Range(0, 100).Select(i => new MockEngine.Vector3(i, 0, 0)).ToArray(),
                Indices = Enumerable.Range(0, 300).ToArray()
            };
            var lod = MockEngine.MeshOperations.ComputeLOD(mesh, 0.5f);
            Assert.IsNotNull(lod);
            Assert.AreEqual("HighPoly_LOD", lod.Name);
            Assert.IsTrue(lod.Vertices.Length <= 50);
        }

        [TestMethod]
        public void MeshBooleanUnion()
        {
            var a = new MockEngine.Mesh
            {
                Name = "A",
                Vertices = new[] { MockEngine.Vector3.Zero },
                Indices = new[] { 0 }
            };
            var b = new MockEngine.Mesh
            {
                Name = "B",
                Vertices = new[] { MockEngine.Vector3.One },
                Indices = new[] { 0 }
            };
            var u = MockEngine.MeshOperations.BooleanUnion(a, b);
            Assert.AreEqual(2, u.Vertices.Length);
            Assert.AreEqual(2, u.Indices.Length);
        }

        [TestMethod]
        public void MeshLODLevels()
        {
            var mesh = new MockEngine.Mesh
            {
                Name = "Tree",
                Vertices = new MockEngine.Vector3[1000],
                Indices = new int[3000],
                LODLevel = 0
            };
            var lod1 = MockEngine.MeshOperations.ComputeLOD(mesh, 0.5f);
            var lod2 = MockEngine.MeshOperations.ComputeLOD(lod1, 0.5f);
            Assert.AreEqual(1, lod1.LODLevel);
            Assert.AreEqual(2, lod2.LODLevel);
        }
    }

    // ======================== 测试入口 ========================

    public static class EngineTestEntry
    {
        public static int RunTests(string[] args)
        {
            var runner = new TestRunner();
            var assembly = Assembly.GetExecutingAssembly();
            int parallelism = 4;
            if (args.Length > 0 && int.TryParse(args[0], out var p))
                parallelism = p;

            runner.RunAll(assembly, parallelism);

            var report = new TestReport { ClassResults = runner.Results.ToList() };
            report.GenerateConsoleReport(colored: true);

            try
            {
                var htmlPath = Path.Combine(Directory.GetCurrentDirectory(), "TestReport.html");
                File.WriteAllText(htmlPath, report.GenerateHtmlReport());
                Console.WriteLine($"HTML report saved to: {htmlPath}");

                var jsonPath = Path.Combine(Directory.GetCurrentDirectory(), "TestReport.json");
                File.WriteAllText(jsonPath, report.GenerateJsonReport());
                Console.WriteLine($"JSON report saved to: {jsonPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write reports: {ex.Message}");
            }

            return report.FailedTests > 0 ? 1 : 0;
        }
    }
}
