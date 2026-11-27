using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using YamaEngine.Reality;
namespace YamaEngine.Editor.CodeOss
{
    /// <summary>
    /// 自研 DAP 1.x 客户端（消息层）：断点 / 继续 / 调用栈。
    /// 与 YamaEngine 的 CodeDebugger 通过此协议对接；此处只负责帧构造与解析，
    /// 不引用任何调试器 SDK、不启动进程。
    /// </summary>
    public sealed class YamaDapClient
    {
        private int _seq = 1;
        public int LastSeq { get; private set; }

        private string Request(string command, string args)
        {
            LastSeq = _seq++;
            return "{\"seq\":" + LastSeq.ToString()
                + ",\"type\":\"request\",\"command\":" + YamaJsonRpcFraming.Quote(command)
                + ",\"arguments\":" + args + "}";
        }

        public string SetBreakpoints(string sourcePath, int[] lines)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"source\":{\"path\":").Append(YamaJsonRpcFraming.Quote(sourcePath))
              .Append("},\"breakpoints\":[");
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"line\":").Append(lines[i].ToString()).Append('}');
            }
            sb.Append("]}");
            return Request("setBreakpoints", sb.ToString());
        }

        public string Continue(int threadId)
            => Request("continue", "{\"threadId\":" + threadId.ToString() + "}");

        public string StackTrace(int threadId)
            => Request("stackTrace", "{\"threadId\":" + threadId.ToString()
                + ",\"startFrame\":0,\"levels\":64}");

        /// <summary>解析 stackTrace 响应，返回 (frameId, name, line) 列表。</summary>
        public static List<(int FrameId, string Name, int Line)> ParseStackTrace(string json)
        {
            var result = new List<(int, string, int)>();
            if (json == null) return result;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("body", out var body)) return result;
            if (!body.TryGetProperty("stackFrames", out var frames)) return result;
            foreach (var f in frames.EnumerateArray())
            {
                int id = f.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
                string name = f.TryGetProperty("name", out var n)
                    ? (n.GetString() ?? string.Empty) : string.Empty;
                int line = f.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                result.Add((id, name, line));
            }
            return result;
        }
    }
}
