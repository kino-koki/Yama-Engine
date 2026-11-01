using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.Diagnostics
{
    // ======================== 崩溃报告数据模型 ========================

    public class CrashReport
    {
        public string ReportId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Platform { get; set; }
        public string EngineVersion { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
        public string StackTrace { get; set; }
        public string InnerException { get; set; }

        public SystemInfo System { get; set; } = new SystemInfo();
        public EngineStateInfo EngineState { get; set; } = new EngineStateInfo();
        public List<string> RecentLogs { get; set; } = new List<string>();
        public List<ThreadSnapshot> ThreadSnapshots { get; set; } = new List<ThreadSnapshot>();
        public Dictionary<string, string> CustomData { get; set; } = new Dictionary<string, string>();

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        public string ToPlainText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("        YamaEngine Crash Report       ");
            sb.AppendLine("========================================");
            sb.AppendLine($"Report ID: {ReportId}");
            sb.AppendLine($"Timestamp: {Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC");
            sb.AppendLine($"Platform:  {Platform}");
            sb.AppendLine($"Engine:    {EngineVersion}");
            sb.AppendLine($"Exception: {ExceptionType}");
            sb.AppendLine($"Message:   {ExceptionMessage}");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(StackTrace);
            if (!string.IsNullOrEmpty(InnerException))
            {
                sb.AppendLine("----------------------------------------");
                sb.AppendLine("Inner Exception:");
                sb.AppendLine(InnerException);
            }
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("System Info:");
            sb.AppendLine($"  OS:           {System.OS}");
            sb.AppendLine($"  OS Version:   {System.OSVersion}");
            sb.AppendLine($"  CPU:          {System.CPU}");
            sb.AppendLine($"  CPU Cores:    {System.CPUCores}");
            sb.AppendLine($"  GPU:          {System.GPU}");
            sb.AppendLine($"  GPU Driver:   {System.GPUDriver}");
            sb.AppendLine($"  Total Memory: {System.TotalMemoryMB} MB");
            sb.AppendLine($"  Free Memory:  {System.FreeMemoryMB} MB");
            sb.AppendLine($"  Process Memory: {System.ProcessMemoryMB} MB");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("Engine State:");
            sb.AppendLine($"  Scene:        {EngineState.CurrentScene}");
            sb.AppendLine($"  Entity Count: {EngineState.EntityCount}");
            sb.AppendLine($"  FPS:          {EngineState.LastFPS:F1}");
            sb.AppendLine($"  Frame:        {EngineState.CurrentFrame}");
            sb.AppendLine($"  Uptime:       {EngineState.Uptime}");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("Recent Logs:");
            foreach (var log in RecentLogs.Take(50))
                sb.AppendLine($"  {log}");
            sb.AppendLine("========================================");
            return sb.ToString();
        }
    }

    public class SystemInfo
    {
        public string OS { get; set; }
        public string OSVersion { get; set; }
        public string Architecture { get; set; }
        public string CPU { get; set; }
        public int CPUCores { get; set; }
        public string GPU { get; set; }
        public string GPUDriver { get; set; }
        public ulong TotalMemoryMB { get; set; }
        public ulong FreeMemoryMB { get; set; }
        public ulong ProcessMemoryMB { get; set; }
    }

    public class EngineStateInfo
    {
        public string CurrentScene { get; set; } = "Unknown";
        public int EntityCount { get; set; }
        public double LastFPS { get; set; }
        public ulong CurrentFrame { get; set; }
        public TimeSpan Uptime { get; set; }
        public bool IsPlaying { get; set; }
        public string ActiveSystem { get; set; }
    }

    public class ThreadSnapshot
    {
        public int ManagedThreadId { get; set; }
        public string Name { get; set; }
        public bool IsBackground { get; set; }
        public string StackTrace { get; set; }
    }

    // ======================== 迷你转储写入器 ========================

    /// <summary>
    /// 简化版 minidump 生成器。捕获线程信息与托管调用栈。
    /// </summary>
    public static class MinidumpWriter
    {
        public static void WriteMinidump(string outputPath, Exception primaryException = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("MINIDUMP");
            sb.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
            sb.AppendLine($"Process: {Process.GetCurrentProcess().ProcessName}");
            sb.AppendLine($"PID: {Process.GetCurrentProcess().Id}");
            sb.AppendLine();

            var process = Process.GetCurrentProcess();
            sb.AppendLine("=== THREADS ===");
            foreach (ProcessThread pt in process.Threads)
            {
                sb.AppendLine($"Thread ID: {pt.Id}");
                sb.AppendLine($"  Priority: {pt.PriorityLevel}");
                sb.AppendLine($"  StartTime: {pt.StartTime}");
                sb.AppendLine($"  UserTime: {pt.UserProcessorTime}");
            }

            sb.AppendLine();
            sb.AppendLine("=== MANAGED THREADS ===");
            var threads = new List<ThreadSnapshot>();
            try
            {
                foreach (var t in System.Diagnostics.Process.GetCurrentProcess().Threads)
                {
                    // Managed thread info is best-effort via reflection or diagnostics
                }
            }
            catch { }

            // Capture current thread stack traces for all threads (best effort)
            sb.AppendLine("=== STACK TRACES ===");
            if (primaryException != null)
            {
                sb.AppendLine("[Primary Exception]");
                sb.AppendLine(primaryException.StackTrace);
                sb.AppendLine();
            }
            sb.AppendLine("[Current Thread]");
            sb.AppendLine(new StackTrace(0, true).ToString());

            sb.AppendLine();
            sb.AppendLine("=== MODULES ===");
            foreach (ProcessModule mod in process.Modules)
            {
                sb.AppendLine($"{mod.ModuleName} @ {mod.FileName} ({mod.FileVersionInfo.FileVersion})");
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }
    }

    // ======================== 日志环形缓冲区 ========================

    public class CircularLogBuffer
    {
        private readonly Queue<string> _logs = new Queue<string>();
        private readonly int _capacity;
        private readonly object _lock = new object();

        public CircularLogBuffer(int capacity = 500)
        {
            _capacity = capacity;
        }

        public void Append(string message)
        {
            lock (_lock)
            {
                _logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
                while (_logs.Count > _capacity)
                    _logs.Dequeue();
            }
        }

        public List<string> GetRecent(int count)
        {
            lock (_lock)
            {
                return _logs.TakeLast(count).ToList();
            }
        }
    }

    // ======================== 恢复系统 ========================

    /// <summary>
    /// 自动保存与恢复系统。在检测到不稳定状态时触发自动保存。
    /// </summary>
    public class RecoverySystem
    {
        private readonly string _recoveryDirectory;
        private readonly Timer _autoSaveTimer;
        private bool _isEnabled;
        private Func<string> _captureStateCallback;

        public RecoverySystem(string recoveryDirectory, int autoSaveIntervalMs = 30000)
        {
            _recoveryDirectory = recoveryDirectory;
            Directory.CreateDirectory(_recoveryDirectory);
            _autoSaveTimer = new Timer(OnAutoSave, null, autoSaveIntervalMs, autoSaveIntervalMs);
        }

        public void SetCaptureCallback(Func<string> callback) => _captureStateCallback = callback;

        public void Enable() => _isEnabled = true;
        public void Disable() => _isEnabled = false;

        private void OnAutoSave(object state)
        {
            if (!_isEnabled || _captureStateCallback == null) return;
            try
            {
                var data = _captureStateCallback();
                var path = Path.Combine(_recoveryDirectory, $"autosave_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(path, data);
                CleanupOldRecoveries(5);
            }
            catch { /* swallow auto-save failures */ }
        }

        public string GetLatestRecoveryPath()
        {
            if (!Directory.Exists(_recoveryDirectory)) return null;
            return Directory.GetFiles(_recoveryDirectory, "autosave_*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        public string LoadLatestRecovery()
        {
            var path = GetLatestRecoveryPath();
            if (path == null) return null;
            return File.ReadAllText(path);
        }

        public void CleanupOldRecoveries(int keepCount)
        {
            var files = Directory.GetFiles(_recoveryDirectory, "autosave_*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(keepCount);
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { }
            }
        }

        public void Dispose()
        {
            _autoSaveTimer?.Dispose();
        }
    }

    // ======================== 崩溃处理器 ========================

    public class CrashHandler : IDisposable
    {
        private readonly string _crashesDirectory;
        private readonly string _engineVersion;
        private readonly CircularLogBuffer _logBuffer;
        private readonly RecoverySystem _recovery;
        private readonly HttpClient _httpClient;
        private string _remoteEndpoint;
        private bool _showDialog;
        private bool _registered;
        private Func<EngineStateInfo> _getEngineState;

        public event Action<CrashReport> OnCrashReportGenerated;

        public CrashHandler(string crashesDirectory, string engineVersion = "1.0.0")
        {
            _crashesDirectory = crashesDirectory;
            _engineVersion = engineVersion;
            _logBuffer = new CircularLogBuffer(500);
            _recovery = new RecoverySystem(Path.Combine(crashesDirectory, "recovery"));
            _httpClient = new HttpClient();
            Directory.CreateDirectory(_crashesDirectory);
        }

        public void SetRemoteEndpoint(string endpoint) => _remoteEndpoint = endpoint;
        public void SetShowDialog(bool show) => _showDialog = show;
        public void SetEngineStateCallback(Func<EngineStateInfo> callback) => _getEngineState = callback;
        public RecoverySystem Recovery => _recovery;
        public CircularLogBuffer LogBuffer => _logBuffer;

        public void Register()
        {
            if (_registered) return;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            _registered = true;
        }

        public void Unregister()
        {
            if (!_registered) return;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            _registered = false;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("Unknown fatal exception");
            HandleCrash(ex, e.IsTerminating);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            HandleCrash(e.Exception, false);
            e.SetObserved();
        }

        public void HandleCrash(Exception ex, bool isTerminating)
        {
            try
            {
                var report = BuildReport(ex);
                SaveReport(report);
                WriteMinidump(report);
                UploadReportAsync(report).ConfigureAwait(false);
                OnCrashReportGenerated?.Invoke(report);
                if (_showDialog)
                    ShowCrashDialog(report, isTerminating);
            }
            catch (Exception handlerEx)
            {
                Console.Error.WriteLine($"Crash handler failed: {handlerEx}");
            }
        }

        private CrashReport BuildReport(Exception ex)
        {
            var report = new CrashReport
            {
                EngineVersion = _engineVersion,
                Platform = RuntimeInformation.OSDescription,
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = ex.Message,
                StackTrace = ex.StackTrace,
                InnerException = ex.InnerException?.ToString(),
                RecentLogs = _logBuffer.GetRecent(100),
                System = GatherSystemInfo(),
                EngineState = _getEngineState?.Invoke() ?? new EngineStateInfo()
            };

            report.ThreadSnapshots = CaptureThreadSnapshots();
            return report;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        private SystemInfo GatherSystemInfo()
        {
            var proc = Process.GetCurrentProcess();
            ulong totalMemory = 0;
            try
            {
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref mem))
                    totalMemory = mem.ullAvailPhys / (1024UL * 1024UL);
            }
            catch { }

            return new SystemInfo
            {
                OS = RuntimeInformation.OSDescription,
                OSVersion = Environment.OSVersion.ToString(),
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                CPU = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown",
                CPUCores = Environment.ProcessorCount,
                GPU = "Unknown",
                GPUDriver = "Unknown",
                TotalMemoryMB = totalMemory,
                FreeMemoryMB = totalMemory,
                ProcessMemoryMB = (ulong)(proc.WorkingSet64 / (1024 * 1024))
            };
        }

        private List<ThreadSnapshot> CaptureThreadSnapshots()
        {
            var list = new List<ThreadSnapshot>();
            try
            {
                var current = Thread.CurrentThread;
                list.Add(new ThreadSnapshot
                {
                    ManagedThreadId = current.ManagedThreadId,
                    Name = current.Name,
                    IsBackground = current.IsBackground,
                    StackTrace = new StackTrace(0, false).ToString()
                });
            }
            catch { }
            return list;
        }

        private void SaveReport(CrashReport report)
        {
            var fileName = $"crash_{report.ReportId}_{report.Timestamp:yyyyMMdd_HHmmss}";
            var jsonPath = Path.Combine(_crashesDirectory, fileName + ".json");
            var txtPath = Path.Combine(_crashesDirectory, fileName + ".txt");
            try
            {
                File.WriteAllText(jsonPath, report.ToJson(), Encoding.UTF8);
                File.WriteAllText(txtPath, report.ToPlainText(), Encoding.UTF8);
                Console.WriteLine($"Crash report saved to: {jsonPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to save crash report: {ex.Message}");
            }
        }

        private void WriteMinidump(CrashReport report)
        {
            var dumpPath = Path.Combine(_crashesDirectory, $"crash_{report.ReportId}.dmp.txt");
            try
            {
                MinidumpWriter.WriteMinidump(dumpPath, new Exception(report.ExceptionMessage));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to write minidump: {ex.Message}");
            }
        }

        private async Task UploadReportAsync(CrashReport report)
        {
            if (string.IsNullOrEmpty(_remoteEndpoint)) return;
            try
            {
                var json = report.ToJson();
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(_remoteEndpoint, content);
                if (response.IsSuccessStatusCode)
                    Console.WriteLine("Crash report uploaded successfully.");
                else
                    Console.WriteLine($"Crash report upload failed: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Crash report upload error: {ex.Message}");
            }
        }

        private void ShowCrashDialog(CrashReport report, bool isTerminating)
        {
            var sb = new StringBuilder();
            sb.AppendLine("YamaEngine has encountered a fatal error.");
            sb.AppendLine();
            sb.AppendLine($"Exception: {report.ExceptionType}");
            sb.AppendLine($"Message: {report.ExceptionMessage}");
            sb.AppendLine();
            if (isTerminating)
                sb.AppendLine("The application will now terminate.");
            else
                sb.AppendLine("The application may be unstable.");
            sb.AppendLine();
            sb.AppendLine($"Report saved to: {_crashesDirectory}");

            try
            {
                Console.WriteLine(sb.ToString());
                // In a real engine, show a native/system dialog here
            }
            catch { }
        }

        public void Log(string message)
        {
            _logBuffer.Append(message);
        }

        public void Dispose()
        {
            Unregister();
            _httpClient?.Dispose();
            _recovery?.Dispose();
        }
    }

    // ======================== 便捷入口 ========================

    public static class CrashReporter
    {
        private static CrashHandler _handler;

        public static void Initialize(string crashesDirectory, string engineVersion = "1.0.0")
        {
            _handler = new CrashHandler(crashesDirectory, engineVersion);
            _handler.Register();
        }

        public static CrashHandler Handler => _handler;

        public static void Log(string message) => _handler?.Log(message);

        public static void SetRemoteEndpoint(string endpoint) => _handler?.SetRemoteEndpoint(endpoint);
        public static void SetShowDialog(bool show) => _handler?.SetShowDialog(show);
        public static void SetEngineStateCallback(Func<EngineStateInfo> callback) => _handler?.SetEngineStateCallback(callback);

        public static void ForceReport(Exception ex) => _handler?.HandleCrash(ex, false);

        public static void Shutdown()
        {
            _handler?.Dispose();
            _handler = null;
        }
    }
}
