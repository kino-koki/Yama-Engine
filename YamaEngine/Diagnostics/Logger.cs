// ============================================================================================
//  Logger.cs
//  yama引擎 (YamaEngine) 结构化日志系统
// ============================================================================================
//  支持多级别日志、分类过滤、多目标输出（控制台/文件/编辑器）。
//  设计参考 UE Logging + Serilog 的结构化思想。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace YamaEngine.Diagnostics
{
    // ====== 日志级别 ======
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Fatal = 5,

        /// <summary>关闭所有日志输出。</summary>
        Off = 6
    }

    // ====== 日志分类 ======
    public enum LogCategory
    {
        Default,
        Core,
        Rendering,
        Audio,
        Physics,
        Animation,
        Input,
        Networking,
        Scripting,
        Assets,
        Editor,
        AI,
        UI,
        Memory,
        JobSystem,
        Config,
        Platform,
        Plugin,
        SaveGame,
        Gameplay
    }

    // ====== 日志记录结构 ======
    public readonly struct LogEntry
    {
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public LogCategory Category { get; }
        public string Message { get; }
        public string SourceFile { get; }
        public int SourceLine { get; }
        public string SourceMember { get; }
        public int ThreadId { get; }

        public LogEntry(
            LogLevel level,
            LogCategory category,
            string message,
            string sourceFile,
            int sourceLine,
            string sourceMember)
        {
            Timestamp = DateTime.UtcNow;
            Level = level;
            Category = category;
            Message = message;
            SourceFile = sourceFile;
            SourceLine = sourceLine;
            SourceMember = sourceMember;
            ThreadId = Environment.CurrentManagedThreadId;
        }

        public string FormattedMessage
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append(Timestamp.ToString("HH:mm:ss.fff"));
                sb.Append(" [");
                sb.Append(Level.ToString().ToUpperInvariant().PadRight(5));
                sb.Append("] [");
                sb.Append(Category.ToString().PadRight(12));
                sb.Append("] ");
                sb.Append(Message);

                if (Level >= LogLevel.Error)
                {
                    sb.Append($" ({Path.GetFileName(SourceFile)}:{SourceLine})");
                }

                return sb.ToString();
            }
        }
    }

    // ====== 日志输出目标接口 ======
    public interface ILogSink
    {
        void Write(in LogEntry entry);
        void Flush();
        bool IsEnabled(LogLevel level);
    }

    // ====== 控制台输出 ======
    public class ConsoleLogSink : ILogSink
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

        public void Write(in LogEntry entry)
        {
            if (!IsEnabled(entry.Level)) return;

            var msg = entry.FormattedMessage;
            if (entry.Level >= LogLevel.Error)
            {
                Console.Error.WriteLine(msg);
            }
            else
            {
                Console.WriteLine(msg);
            }
        }

        public void Flush() => Console.Out.Flush();
    }

    // ====== 文件输出 ======
    public class FileLogSink : ILogSink, IDisposable
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;
        private readonly StreamWriter _writer;
        private readonly string _filePath;
        private readonly object _lock = new();

        public FileLogSink(string directory, string fileName = "YamaEngine.log")
        {
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, fileName);

            // 滚动日志：如果超过 10MB，重命名为 .old
            if (File.Exists(_filePath) && new FileInfo(_filePath).Length > 10 * 1024 * 1024)
            {
                string oldPath = _filePath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(_filePath, oldPath);
            }

            _writer = new StreamWriter(_filePath, append: true, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

        public void Write(in LogEntry entry)
        {
            if (!IsEnabled(entry.Level)) return;

            lock (_lock)
            {
                _writer.WriteLine(entry.FormattedMessage);
            }
        }

        public void Flush()
        {
            lock (_lock)
            {
                _writer.Flush();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer.Dispose();
            }
        }
    }

    // ====== 回调输出（编辑器集成用） ======
    public class CallbackLogSink : ILogSink
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;
        public event Action<LogEntry>? OnLog;

        public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

        public void Write(in LogEntry entry)
        {
            if (!IsEnabled(entry.Level)) return;
            OnLog?.Invoke(entry);
        }

        public void Flush() { }
    }

    // ====== 日志缓冲 ======
    public class BufferedLogSink : ILogSink
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;
        private readonly List<LogEntry> _buffer = new();
        private readonly int _maxEntries;
        private readonly object _lock = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_lock) return _buffer.ToArray(); }
        }

        public BufferedLogSink(int maxEntries = 1000)
        {
            _maxEntries = maxEntries;
        }

        public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

        public void Write(in LogEntry entry)
        {
            if (!IsEnabled(entry.Level)) return;
            lock (_lock)
            {
                _buffer.Add(entry);
                while (_buffer.Count > _maxEntries)
                    _buffer.RemoveAt(0);
            }
        }

        public void Flush() { }

        public void Clear() { lock (_lock) _buffer.Clear(); }
    }

    // ====== Logger 主类 ======
    /// <summary>
    /// yama引擎结构化日志系统。
    /// 全局可通过 Log.Info(...)/Log.Warning(...)/Log.Error(...) 等静态方法访问。
    /// </summary>
    public static class Log
    {
        // ====== 全局级别过滤器 ======
        public static LogLevel GlobalMinimumLevel { get; set; } = LogLevel.Trace;

        // ====== 输出目标列表 ======
        private static readonly List<ILogSink> _sinks = new();
        private static readonly object _sinkLock = new();

        // ====== 默认输出目标 ======
        static Log()
        {
            // 默认添加控制台输出
            _sinks.Add(new ConsoleLogSink());

            // 尝试添加文件输出
            try
            {
                string logDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Logs");
                _sinks.Add(new FileLogSink(logDir));
            }
            catch
            {
                // 文件输出不可用时忽略
            }
        }

        /// <summary>注册一个新的日志输出目标。</summary>
        public static void AddSink(ILogSink sink)
        {
            lock (_sinkLock)
            {
                _sinks.Add(sink);
            }
        }

        /// <summary>移除日志输出目标。</summary>
        public static bool RemoveSink(ILogSink sink)
        {
            lock (_sinkLock)
            {
                return _sinks.Remove(sink);
            }
        }

        /// <summary>清空所有日志输出目标。</summary>
        public static void ClearSinks()
        {
            lock (_sinkLock)
            {
                _sinks.Clear();
            }
        }

        // ====== 核心日志方法 ======

        private static void Write(
            LogLevel level,
            LogCategory category,
            string message,
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLine = 0,
            [CallerMemberName] string sourceMember = "")
        {
            if (level < GlobalMinimumLevel) return;

            var entry = new LogEntry(level, category, message, sourceFile, sourceLine, sourceMember);

            lock (_sinkLock)
            {
                foreach (var sink in _sinks)
                {
                    try
                    {
                        sink.Write(in entry);
                    }
                    catch
                    {
                        // 忽略单个 sink 的异常
                    }
                }
            }
        }

        // ====== 便捷静态方法 ======

        [Conditional("TRACE")]
        public static void Trace(string message, LogCategory category = LogCategory.Default,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
            => Write(LogLevel.Trace, category, message, file, line, member);

        [Conditional("DEBUG")]
        public static void Debug(string message, LogCategory category = LogCategory.Default,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
            => Write(LogLevel.Debug, category, message, file, line, member);

        public static void Info(string message, LogCategory category = LogCategory.Default,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
            => Write(LogLevel.Info, category, message, file, line, member);

        public static void Warning(string message, LogCategory category = LogCategory.Default,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
            => Write(LogLevel.Warn, category, message, file, line, member);

        public static void Error(string message, LogCategory category = LogCategory.Default,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
            => Write(LogLevel.Error, category, message, file, line, member);

        public static void Fatal(string message, LogCategory category = LogCategory.Default,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
            => Write(LogLevel.Fatal, category, message, file, line, member);

        // ====== 格式化版本 ======
        public static void InfoF(string format, LogCategory category, params object[] args)
            => Info(string.Format(format, args), category);

        public static void WarningF(string format, LogCategory category, params object[] args)
            => Warning(string.Format(format, args), category);

        public static void ErrorF(string format, LogCategory category, params object[] args)
            => Error(string.Format(format, args), category);

        // ====== 异常日志 ======
        public static void Exception(Exception ex, string context = "",
            LogCategory category = LogCategory.Default,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            string msg = string.IsNullOrEmpty(context)
                ? $"异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                : $"{context}: 异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            Write(LogLevel.Error, category, msg, file, line, "Exception");
        }

        // ====== 刷新 ======
        public static void Flush()
        {
            lock (_sinkLock)
            {
                foreach (var sink in _sinks)
                {
                    try { sink.Flush(); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// 日志便捷别名。部分模块以 <c>Logger</c> 作为入口调用，
    /// 本类型将 <c>Logger</c> 接口转发到 <c>Log</c>。
    /// </summary>
    public static class Logger
    {
        public static void Trace(string message, LogCategory category = LogCategory.Default)
            => Log.Trace(message, category);
        public static void Debug(string message, LogCategory category = LogCategory.Default)
            => Log.Debug(message, category);
        public static void Info(string message, LogCategory category = LogCategory.Default)
            => Log.Info(message, category);
        public static void Warning(string message, LogCategory category = LogCategory.Default)
            => Log.Warning(message, category);
        public static void Error(string message, LogCategory category = LogCategory.Default)
            => Log.Error(message, category);
        public static void Fatal(string message, LogCategory category = LogCategory.Default)
            => Log.Fatal(message, category);
        public static void Exception(Exception ex, string context = "", LogCategory category = LogCategory.Default)
            => Log.Exception(ex, context, category);
    }
}
