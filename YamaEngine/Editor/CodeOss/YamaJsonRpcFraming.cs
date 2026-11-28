using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using YamaEngine.Reality;
namespace YamaEngine.Editor.CodeOss
{
    /// <summary>
    /// 自研 LSP / DAP 的 Content-Length 帧编解码（协议规范：LSP 3.17 base protocol）。
    /// 纯字节/字符串变换，不依赖任何第三方库、不启动进程。
    /// </summary>
    public static class YamaJsonRpcFraming
    {
        public const string HeaderName = "Content-Length";

        public static byte[] Encode(string jsonPayload)
        {
            if (jsonPayload == null) throw new ArgumentNullException(nameof(jsonPayload));
            byte[] body = Encoding.UTF8.GetBytes(jsonPayload);
            string header = HeaderName + ": " + body.Length.ToString() + "\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            var result = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, result, 0, head.Length);
            Buffer.BlockCopy(body, 0, result, head.Length, body.Length);
            return result;
        }

        /// <summary>从一帧字节里解析出 JSON 载荷；头部非法返回 false。</summary>
        public static bool TryDecode(byte[] frame, out string jsonPayload)
        {
            jsonPayload = string.Empty;
            if (frame == null) return false;
            string marker = "\r\n\r\n";
            int idx = IndexOf(frame, Encoding.ASCII.GetBytes(marker));
            if (idx < 0) return false;
            string header = Encoding.ASCII.GetString(frame, 0, idx);
            int colon = header.IndexOf(':');
            if (colon < 0) return false;
            if (!header.Substring(0, colon).Trim().Equals(HeaderName,
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!int.TryParse(header.Substring(colon + 1).Trim(), out int len)) return false;
            int bodyStart = idx + marker.Length;
            if (len < 0 || bodyStart + len > frame.Length) return false;
            jsonPayload = Encoding.UTF8.GetString(frame, bodyStart, len);
            return true;
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < needle.Length; k++)
                    if (hay[i + k] != needle[k]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        public static string JsonRpc(string method, int id, string paramsJson)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id)
              .Append(",\"method\":").Append(Quote(method))
              .Append(",\"params\":").Append(paramsJson).Append('}');
            return sb.ToString();
        }

        public static string Quote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
