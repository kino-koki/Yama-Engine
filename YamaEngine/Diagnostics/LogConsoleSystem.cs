// ============================================================================================
//  LogConsoleSystem.cs
//  yama引擎 (YamaEngine) 编辑器控制台日志输出
// ============================================================================================
//  将日志输出重定向到编辑器控制台面板。
//  此文件已重构为新 Logger 系统 (Diagnostics/Logger.cs)，此处保留为兼容层。
// ============================================================================================

using System;

namespace YamaEngine.Diagnostics
{
    /// <summary>
    /// 编辑器控制台日志输出。将 Logger 的事件转发到编辑器 UI。
    /// </summary>
    public static class LogConsoleSystem
    {
        /// <summary>是否启用控制台输出。</summary>
        public static bool IsEnabled { get; set; } = true;

        /// <summary>最大保留条目数。</summary>
        public static int MaxEntries { get; set; } = 500;

        /// <summary>初始化控制台。</summary>
        public static void Initialize()
        {
            var sink = new CallbackLogSink { MinimumLevel = LogLevel.Trace };
            sink.OnLog += entry =>
            {
                if (!IsEnabled) return;
                // 编辑器面板通过此处接收日志
                System.Diagnostics.Debug.WriteLine(entry.FormattedMessage);
            };
            Log.AddSink(sink);
        }

        /// <summary>清空控制台。</summary>
        public static void Clear() { }
    }
}
