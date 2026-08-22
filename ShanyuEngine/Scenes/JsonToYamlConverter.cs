// ==================================================================================================
//  JsonToYamlConverter.cs — 山雨引擎 JSON ↔ YAML 轻量转换器
// ==================================================================================================
//  命名空间: ShanyuEngine.Scenes
//  依赖:     System.Text.Json.Nodes (built-in .NET 6+)
//
//  功能:
//    将 System.Text.Json 序列化出的 JSON 字符串转换为人类可读的 YAML 格式，
//    并支持反向解析 YAML 回 JsonNode（简化版，覆盖引擎场景文件常用结构）。
//
//  限制:
//    - 不支持 YAML 锚点/别名 (&foo / *foo)
//    - 不支持多文档流 (---)
//    - 不支持标签 (!!str 等)
//    - 注释在 YAML→JSON 转换时会丢失
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShanyuEngine.Scenes
{
    /// <summary>
    /// JSON 与 YAML 之间的轻量级双向转换器。
    /// 用于将引擎场景文件以 YAML（人类可读）或 JSON（机器高效）两种格式存储。
    /// </summary>
    public static class JsonToYamlConverter
    {
        // ════════════════════════════════════════════════════════════════════════════
        //  JSON → YAML
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>将 JSON 字符串转换为 YAML 字符串。</summary>
        public static string JsonToYaml(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return string.Empty;

            var node = JsonNode.Parse(json);
            if (node == null) return string.Empty;

            var sb = new StringBuilder();
            WriteYamlNode(node, sb, 0);
            return sb.ToString();
        }

        /// <summary>将 <see cref="JsonNode"/> 转换为 YAML 字符串。</summary>
        public static string JsonNodeToYaml(JsonNode node)
        {
            if (node == null) return string.Empty;
            var sb = new StringBuilder();
            WriteYamlNode(node, sb, 0);
            return sb.ToString();
        }

        private static void WriteYamlNode(JsonNode? node, StringBuilder sb, int indent)
        {
            if (node == null)
            {
                sb.Append("null");
                return;
            }

            switch (node)
            {
                case JsonObject obj:
                    WriteYamlObject(obj, sb, indent);
                    break;
                case JsonArray arr:
                    WriteYamlArray(arr, sb, indent);
                    break;
                case JsonValue val:
                    WriteYamlValue(val, sb);
                    break;
                default:
                    sb.Append(node.ToJsonString());
                    break;
            }
        }

        private static void WriteYamlObject(JsonObject obj, StringBuilder sb, int indent)
        {
            if (obj.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            string prefix = new string(' ', indent * 2);
            bool first = true;

            foreach (var kv in obj)
            {
                if (!first) sb.AppendLine();
                first = false;

                sb.Append(prefix);
                sb.Append(EscapeYamlKey(kv.Key));
                sb.Append(": ");

                if (kv.Value is JsonObject childObj && childObj.Count > 0)
                {
                    sb.AppendLine();
                    WriteYamlObject(childObj, sb, indent + 1);
                }
                else if (kv.Value is JsonArray childArr && childArr.Count > 0)
                {
                    // 如果数组元素是简单值，尝试内联；否则换行
                    if (childArr.All(e => e is JsonValue))
                    {
                        WriteYamlArrayInline(childArr, sb);
                    }
                    else
                    {
                        sb.AppendLine();
                        WriteYamlArray(childArr, sb, indent + 1);
                    }
                }
                else
                {
                    WriteYamlNode(kv.Value, sb, indent + 1);
                }
            }
        }

        private static void WriteYamlArray(JsonArray arr, StringBuilder sb, int indent)
        {
            if (arr.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            string prefix = new string(' ', indent * 2);
            bool first = true;

            foreach (var item in arr)
            {
                if (!first) sb.AppendLine();
                first = false;

                sb.Append(prefix);
                sb.Append("- ");

                if (item is JsonObject itemObj && itemObj.Count > 0)
                {
                    // 对象数组：- 后面的键值对需要缩进一级
                    bool objFirst = true;
                    foreach (var kv in itemObj)
                    {
                        if (!objFirst)
                        {
                            sb.AppendLine();
                            sb.Append(prefix);
                            sb.Append("  ");
                        }
                        objFirst = false;

                        sb.Append(EscapeYamlKey(kv.Key));
                        sb.Append(": ");

                        if (kv.Value is JsonObject childObj && childObj.Count > 0)
                        {
                            sb.AppendLine();
                            WriteYamlObject(childObj, sb, indent + 2);
                        }
                        else if (kv.Value is JsonArray childArr && childArr.Count > 0)
                        {
                            if (childArr.All(e => e is JsonValue))
                            {
                                WriteYamlArrayInline(childArr, sb);
                            }
                            else
                            {
                                sb.AppendLine();
                                WriteYamlArray(childArr, sb, indent + 2);
                            }
                        }
                        else
                        {
                            WriteYamlNode(kv.Value, sb, indent + 2);
                        }
                    }
                }
                else if (item is JsonArray itemArr)
                {
                    sb.AppendLine();
                    WriteYamlArray(itemArr, sb, indent + 1);
                }
                else
                {
                    WriteYamlNode(item, sb, indent + 1);
                }
            }
        }

        private static void WriteYamlArrayInline(JsonArray arr, StringBuilder sb)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in arr)
            {
                if (!first) sb.Append(", ");
                first = false;
                WriteYamlValue(item as JsonValue, sb);
            }
            sb.Append(']');
        }

        private static void WriteYamlValue(JsonValue? val, StringBuilder sb)
        {
            if (val == null)
            {
                sb.Append("null");
                return;
            }

            var jsonValue = val.GetValue<object?>();
            if (jsonValue == null)
            {
                sb.Append("null");
                return;
            }

            if (jsonValue is string s)
            {
                // 需要引号的情况
                if (s == string.Empty ||
                    s.Contains('\n') || s.Contains('"') || s.Contains('\'') ||
                    s.StartsWith(" ") || s.EndsWith(" ") ||
                    s.StartsWith("#") || s == "true" || s == "false" || s == "null" ||
                    s == "~" || s == "|" || s == ">" ||
                    double.TryParse(s, out _) ||
                    s.Contains(": ") || s.Contains(" #"))
                {
                    sb.Append('"');
                    sb.Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n"));
                    sb.Append('"');
                }
                else
                {
                    sb.Append(s);
                }
            }
            else if (jsonValue is bool b)
            {
                sb.Append(b ? "true" : "false");
            }
            else if (jsonValue is float f)
            {
                sb.Append(f.ToString(CultureInfo.InvariantCulture));
            }
            else if (jsonValue is double d)
            {
                sb.Append(d.ToString(CultureInfo.InvariantCulture));
            }
            else if (jsonValue is decimal dec)
            {
                sb.Append(dec.ToString(CultureInfo.InvariantCulture));
            }
            else if (jsonValue is int i)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
            }
            else if (jsonValue is long l)
            {
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
            }
            else if (jsonValue is Guid g)
            {
                sb.Append('"');
                sb.Append(g.ToString());
                sb.Append('"');
            }
            else
            {
                sb.Append(JsonSerializer.Serialize(jsonValue));
            }
        }

        private static string EscapeYamlKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "\"\"";
            if (key.Contains(':') || key.Contains('#') || key.Contains('{') || key.Contains('}') ||
                key.Contains('[') || key.Contains(']') || key.Contains(',') || key.Contains('&') ||
                key.Contains('*') || key.Contains('?') || key.Contains('|') || key.Contains('-') ||
                key.Contains('<') || key.Contains('>') || key.Contains('=') || key.Contains('!') ||
                key.Contains('%') || key.Contains('@') || key.Contains('`') || key.Contains('"') ||
                key.Contains('\'') || key.StartsWith(" ") || key.EndsWith(" ") ||
                key == "true" || key == "false" || key == "null" || key == "yes" || key == "no" ||
                key == "on" || key == "off" ||
                double.TryParse(key, out _))
            {
                return $"\"{key.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
            }
            return key;
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  YAML → JSON (简化版)
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 将 YAML 字符串解析为 <see cref="JsonNode"/>。
        /// 这是简化版解析器，覆盖引擎场景文件常用结构。
        /// </summary>
        public static JsonNode? YamlToJsonNode(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml)) return null;
            var lines = yaml.Split('\n');
            var reader = new YamlLineReader(lines);
            return ParseYamlValue(ref reader, 0);
        }

        /// <summary>将 YAML 字符串转换为 JSON 字符串。</summary>
        public static string YamlToJson(string yaml)
        {
            var node = YamlToJsonNode(yaml);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
        }

        private static JsonNode? ParseYamlValue(ref YamlLineReader reader, int expectedIndent)
        {
            if (reader.IsEnd) return null;

            var (indent, line) = reader.Peek();
            if (indent < expectedIndent) return null;
            if (indent > expectedIndent) return null; // 缩进异常

            reader.Advance();

            // 空行或纯注释
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                return ParseYamlValue(ref reader, expectedIndent);

            string trimmed = line.TrimStart();

            // 数组元素
            if (trimmed.StartsWith("- ") || trimmed == "-")
            {
                // 回退，让上层数组解析处理
                reader.Rewind();
                return ParseYamlArray(ref reader, expectedIndent);
            }

            // 对象键值对
            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                reader.Rewind();
                return ParseYamlObject(ref reader, expectedIndent);
            }

            // 标量
            return ParseYamlScalar(trimmed);
        }

        private static JsonObject ParseYamlObject(ref YamlLineReader reader, int baseIndent)
        {
            var obj = new JsonObject();

            while (!reader.IsEnd)
            {
                var (indent, line) = reader.Peek();
                if (indent < baseIndent) break;
                if (indent > baseIndent)
                {
                    // 缩进异常，跳过
                    reader.Advance();
                    continue;
                }

                reader.Advance();

                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    continue;

                string trimmed = line.TrimStart();

                // 如果行以 - 开头，说明数组开始了，回退并返回
                if (trimmed.StartsWith("- ") || trimmed == "-")
                {
                    reader.Rewind();
                    break;
                }

                int colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                string key = UnescapeYamlKey(trimmed.Substring(0, colonIdx).TrimEnd());
                string valuePart = trimmed.Substring(colonIdx + 1).TrimStart();

                if (string.IsNullOrEmpty(valuePart))
                {
                    // 值在下一行（对象、数组或块标量）
                    var (nextIndent, nextLine) = reader.Peek();
                    if (reader.IsEnd)
                    {
                        obj[key] = null;
                        continue;
                    }

                    string nextTrimmed = nextLine.TrimStart();
                    if (nextTrimmed.StartsWith("- ") || nextTrimmed == "-")
                    {
                        obj[key] = ParseYamlArray(ref reader, nextIndent);
                    }
                    else if (nextTrimmed.Contains(':'))
                    {
                        obj[key] = ParseYamlObject(ref reader, nextIndent);
                    }
                    else
                    {
                        // 块标量（多行字符串）
                        obj[key] = ParseYamlBlockScalar(ref reader, nextIndent);
                    }
                }
                else
                {
                    obj[key] = ParseYamlScalar(valuePart);
                }
            }

            return obj;
        }

        private static JsonArray ParseYamlArray(ref YamlLineReader reader, int baseIndent)
        {
            var arr = new JsonArray();

            while (!reader.IsEnd)
            {
                var (indent, line) = reader.Peek();
                if (indent < baseIndent) break;
                if (indent > baseIndent)
                {
                    reader.Advance();
                    continue;
                }

                reader.Advance();

                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    continue;

                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("- ") && trimmed != "-")
                {
                    // 不是数组元素，回退并退出
                    reader.Rewind();
                    break;
                }

                string valuePart = trimmed.StartsWith("- ") ? trimmed.Substring(2).TrimStart() : string.Empty;

                if (string.IsNullOrEmpty(valuePart))
                {
                    // 值在下一行
                    var (nextIndent, nextLine) = reader.Peek();
                    if (reader.IsEnd)
                    {
                        arr.Add(null);
                        continue;
                    }

                    string nextTrimmed = nextLine.TrimStart();
                    if (nextTrimmed.StartsWith("- ") || nextTrimmed == "-")
                    {
                        // 子数组 — 但当前缩进应比 baseIndent 大
                        if (nextIndent > baseIndent)
                            arr.Add(ParseYamlArray(ref reader, nextIndent));
                        else
                            arr.Add(null);
                    }
                    else if (nextTrimmed.Contains(':'))
                    {
                        if (nextIndent > baseIndent)
                            arr.Add(ParseYamlObject(ref reader, nextIndent));
                        else
                            arr.Add(null);
                    }
                    else
                    {
                        arr.Add(ParseYamlBlockScalar(ref reader, nextIndent));
                    }
                }
                else
                {
                    // 内联值
                    if (valuePart.StartsWith("{") && valuePart.EndsWith("}"))
                    {
                        arr.Add(ParseYamlInlineObject(valuePart));
                    }
                    else if (valuePart.StartsWith("[") && valuePart.EndsWith("]"))
                    {
                        arr.Add(ParseYamlInlineArray(valuePart));
                    }
                    else if (valuePart.Contains(':') && !valuePart.StartsWith("\""))
                    {
                        // 可能是单行对象（如 - key: value）
                        int ci = valuePart.IndexOf(':');
                        string k = UnescapeYamlKey(valuePart.Substring(0, ci).TrimEnd());
                        string v = valuePart.Substring(ci + 1).TrimStart();
                        var singleObj = new JsonObject { [k] = ParseYamlScalar(v) };
                        arr.Add(singleObj);
                    }
                    else
                    {
                        arr.Add(ParseYamlScalar(valuePart));
                    }
                }
            }

            return arr;
        }

        private static JsonValue ParseYamlScalar(string text)
        {
            text = text.Trim();

            if (text == "null" || text == "~" || text == "")
                return JsonValue.Create((string?)null)!;
            if (text == "true" || text == "yes" || text == "on")
                return JsonValue.Create(true);
            if (text == "false" || text == "no" || text == "off")
                return JsonValue.Create(false);

            // 去掉引号
            if ((text.StartsWith("\"") && text.EndsWith("\"")) ||
                (text.StartsWith("'") && text.EndsWith("'")))
            {
                return JsonValue.Create(text.Substring(1, text.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n"))!;
            }

            if (int.TryParse(text, out int iVal))
                return JsonValue.Create(iVal);
            if (long.TryParse(text, out long lVal))
                return JsonValue.Create(lVal);
            if (float.TryParse(text, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out float fVal))
                return JsonValue.Create(fVal);
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double dVal))
                return JsonValue.Create(dVal);
            if (Guid.TryParse(text, out Guid gVal))
                return JsonValue.Create(gVal.ToString());

            return JsonValue.Create(text)!;
        }

        private static JsonValue ParseYamlBlockScalar(ref YamlLineReader reader, int baseIndent)
        {
            var sb = new StringBuilder();
            bool first = true;

            while (!reader.IsEnd)
            {
                var (indent, line) = reader.Peek();
                if (indent < baseIndent) break;

                // 遇到新键值对或数组元素，停止
                string trimmed = line.TrimStart();
                if (indent == baseIndent && (trimmed.Contains(':') || trimmed.StartsWith("- ")))
                    break;

                reader.Advance();

                if (string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine();
                    continue;
                }

                if (!first) sb.AppendLine();
                first = false;

                // 去掉相对 baseIndent 的缩进
                int strip = Math.Min(indent - baseIndent, line.Length);
                sb.Append(strip > 0 ? line.Substring(strip) : line);
            }

            string result = sb.ToString();
            if (result.EndsWith("\r\n")) result = result.Substring(0, result.Length - 2);
            else if (result.EndsWith("\n")) result = result.Substring(0, result.Length - 1);

            return JsonValue.Create(result)!;
        }

        private static JsonNode ParseYamlInlineObject(string text)
        {
            text = text.Trim();
            if (text.StartsWith("{")) text = text.Substring(1);
            if (text.EndsWith("}")) text = text.Substring(0, text.Length - 1);

            var obj = new JsonObject();
            var pairs = SplitInlineItems(text, ',');
            foreach (var pair in pairs)
            {
                int colonIdx = pair.IndexOf(':');
                if (colonIdx <= 0) continue;
                string k = UnescapeYamlKey(pair.Substring(0, colonIdx).Trim());
                string v = pair.Substring(colonIdx + 1).Trim();
                obj[k] = ParseYamlScalar(v);
            }
            return obj;
        }

        private static JsonArray ParseYamlInlineArray(string text)
        {
            text = text.Trim();
            if (text.StartsWith("[")) text = text.Substring(1);
            if (text.EndsWith("]")) text = text.Substring(0, text.Length - 1);

            var arr = new JsonArray();
            foreach (var item in SplitInlineItems(text, ','))
            {
                arr.Add(ParseYamlScalar(item.Trim()));
            }
            return arr;
        }

        private static List<string> SplitInlineItems(string text, char delimiter)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            char stringChar = '\0';

            foreach (char c in text)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (c == stringChar && sb.Length > 1 && sb[sb.Length - 2] != '\\')
                        inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    stringChar = c;
                    sb.Append(c);
                    continue;
                }

                if (c == '{' || c == '[') depth++;
                if (c == '}' || c == ']') depth--;

                if (c == delimiter && depth == 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }

        private static string UnescapeYamlKey(string key)
        {
            key = key.Trim();
            if ((key.StartsWith("\"") && key.EndsWith("\"")) ||
                (key.StartsWith("'") && key.EndsWith("'")))
            {
                return key.Substring(1, key.Length - 2).Replace("\\\"", "\"");
            }
            return key;
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  YAML 行读取器
        // ════════════════════════════════════════════════════════════════════════════

        private ref struct YamlLineReader
        {
            private readonly string[] _lines;
            private int _index;

            public YamlLineReader(string[] lines)
            {
                _lines = lines;
                _index = 0;
            }

            public bool IsEnd => _index >= _lines.Length;

            public (int Indent, string Line) Peek()
            {
                if (IsEnd) return (0, string.Empty);
                var line = _lines[_index];
                int indent = line.Length - line.TrimStart().Length;
                return (indent, line);
            }

            public void Advance() => _index++;

            public void Rewind() => _index = Math.Max(0, _index - 1);
        }
    }
}
