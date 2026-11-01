// =============================================================================
//  Yama Engine - ShaderGraphSerializer.cs
//  yama引擎 - 着色器图序列化系统
//
//  本文件实现着色器图的序列化/反序列化, 涵盖:
//    * ShaderGraphSerializer 类 —— 图 <-> JSON 序列化/反序列化
//    * 版本管理 (版本号/迁移函数/向后兼容)
//    * 子图引用解析 (子图路径/相对路径/绝对路径/GUID 引用)
//    * 纹理引用序列化 (资产路径/GUID/内嵌 Base64)
//    * 属性默认值序列化 (所有类型的默认值)
//    * 压缩 (可选 GZip 压缩)
//    * 导出格式 (.shgraph JSON / .hlsl 直接编译 / .cs 脚本化材质)
//
//  序列化格式 (.shgraph JSON):
//    {
//      "version": 3,
//      "guid": "...",
//      "name": "...",
//      "target": "Universal",
//      "precision": "Float",
//      "blendMode": "Opaque",
//      "shadingModel": "Lit",
//      "nodes": [ { "id", "type", "title", "position", "inputPins", "outputPins", "metadata" } ],
//      "connections": [ { "sourceNodeId", "sourcePinId", "targetNodeId", "targetPinId" } ],
//      "properties": [ { "name", "type", "defaultValue", ... } ],
//      "subGraphs": [ { "guid", "name", "filePath" } ],
//      "outputNodeId": "...",
//      "keywords": [ ... ]
//    }
//
//  命名空间: YamaEngine.ShaderGraph
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamaEngine.Rendering;

namespace YamaEngine.ShaderGraph
{
    // =========================================================================
    // 序列化版本
    // =========================================================================

    /// <summary>着色器图序列化格式版本。</summary>
    public static class ShaderGraphVersion
    {
        /// <summary>当前版本号。</summary>
        public const int Current = 3;

        /// <summary>支持的最低版本号。</summary>
        public const int Minimum = 1;

        /// <summary>版本历史。</summary>
        public static readonly int[] SupportedVersions = { 1, 2, 3 };
    }

    // =========================================================================
    // 序列化数据模型 (DTO)
    // =========================================================================

    /// <summary>着色器图序列化根对象。</summary>
    public sealed class ShaderGraphData
    {
        public int Version { get; set; } = ShaderGraphVersion.Current;
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Target { get; set; } = "Universal";
        public string Precision { get; set; } = "Float";
        public string BlendMode { get; set; } = "Opaque";
        public string ShadingModel { get; set; } = "Lit";
        public bool DoubleSided { get; set; }
        public bool Wireframe { get; set; }
        public float AlphaCutoff { get; set; } = 0.5f;
        public List<NodeData> Nodes { get; set; } = new List<NodeData>();
        public List<ConnectionData> Connections { get; set; } = new List<ConnectionData>();
        public List<PropertyData> Properties { get; set; } = new List<PropertyData>();
        public List<SubGraphRefData> SubGraphs { get; set; } = new List<SubGraphRefData>();
        public List<string> Keywords { get; set; } = new List<string>();
        public string? OutputNodeId { get; set; }
    }

    /// <summary>节点序列化数据。</summary>
    public sealed class NodeData
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float SizeX { get; set; } = 200;
        public float SizeY { get; set; } = 150;
        public bool Enabled { get; set; } = true;
        public bool Collapsed { get; set; }
        public string ColorTag { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public List<PinData> InputPins { get; set; } = new List<PinData>();
        public List<PinData> OutputPins { get; set; } = new List<PinData>();
        public Dictionary<string, JsonElement> Metadata { get; set; } = new Dictionary<string, JsonElement>();
    }

    /// <summary>引脚序列化数据。</summary>
    public sealed class PinData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = "Float";
        public float DefX { get; set; }
        public float DefY { get; set; }
        public float DefZ { get; set; }
        public float DefW { get; set; } = 1;
        public int IntDefault { get; set; }
        public bool BoolDefault { get; set; }
        public string StringDefault { get; set; } = string.Empty;
        public bool Required { get; set; }
        public float SliderMin { get; set; }
        public float SliderMax { get; set; } = 1;
    }

    /// <summary>连接序列化数据。</summary>
    public sealed class ConnectionData
    {
        public string Id { get; set; } = string.Empty;
        public string SourceNodeId { get; set; } = string.Empty;
        public string SourcePinId { get; set; } = string.Empty;
        public string TargetNodeId { get; set; } = string.Empty;
        public string TargetPinId { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    /// <summary>属性序列化数据。</summary>
    public sealed class PropertyData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Tooltip { get; set; } = string.Empty;
        public string PropertyType { get; set; } = "Float";
        public float ScalarValue { get; set; }
        public float VecX { get; set; }
        public float VecY { get; set; }
        public float VecZ { get; set; }
        public float VecW { get; set; } = 1;
        public string TexturePath { get; set; } = string.Empty;
        public string TextureGuid { get; set; } = string.Empty;
        public bool BoolValue { get; set; }
        public int IntValue { get; set; }
        public float MinValue { get; set; }
        public float MaxValue { get; set; } = 1;
        public bool UseRange { get; set; }
        public string Group { get; set; } = "Default";
        public int SortOrder { get; set; }
        public bool Visible { get; set; } = true;
    }

    /// <summary>子图引用序列化数据。</summary>
    public sealed class SubGraphRefData
    {
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string ReferenceType { get; set; } = "Relative"; // Relative/Absolute/Guid
    }

    // =========================================================================
    // 序列化器
    // =========================================================================

    /// <summary>
    /// 着色器图序列化器。负责图 <-> JSON 的转换, 支持版本迁移与压缩。
    /// </summary>
    public static class ShaderGraphSerializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ── 序列化 (图 -> JSON) ────────────────────────────────

        /// <summary>将着色器图序列化为 JSON 字符串。</summary>
        public static string ToJson(ShaderGraph graph, bool indented = true)
        {
            var data = SerializeGraph(graph);
            var options = indented
                ? new JsonSerializerOptions(JsonOptions) { WriteIndented = true }
                : new JsonSerializerOptions(JsonOptions) { WriteIndented = false };
            return JsonSerializer.Serialize(data, options);
        }

        /// <summary>将着色器图序列化到文件 (.shgraph)。</summary>
        public static void ToFile(ShaderGraph graph, string path, bool compress = false)
        {
            var json = ToJson(graph, !compress);
            if (compress)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                using var fs = File.Create(path);
                using var gz = new GZipStream(fs, CompressionLevel.Optimal);
                gz.Write(bytes, 0, bytes.Length);
            }
            else
            {
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }

        // ── 反序列化 (JSON -> 图) ──────────────────────────────

        /// <summary>从 JSON 字符串反序列化着色器图。</summary>
        public static ShaderGraph FromJson(string json)
        {
            var data = JsonSerializer.Deserialize<ShaderGraphData>(json, JsonOptions)
                ?? throw new InvalidDataException("无效的着色器图 JSON 数据。");

            // 版本迁移
            data = MigrateVersion(data);

            return DeserializeGraph(data);
        }

        /// <summary>从文件反序列化着色器图 (.shgraph)。</summary>
        public static ShaderGraph FromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"着色器图文件不存在: {path}", path);

            string json;
            // 检测是否为 GZip 压缩 (GZip 魔数 0x1F 0x8B)
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var ms = new MemoryStream(bytes);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                json = reader.ReadToEnd();
            }
            else
            {
                json = Encoding.UTF8.GetString(bytes);
            }
            return FromJson(json);
        }

        // ── 图 -> 数据模型 ────────────────────────────────────

        /// <summary>将 ShaderGraph 转换为可序列化的数据模型。</summary>
        private static ShaderGraphData SerializeGraph(ShaderGraph graph)
        {
            var data = new ShaderGraphData
            {
                Version = ShaderGraphVersion.Current,
                Guid = graph.Guid,
                Name = graph.Name,
                Description = graph.Description,
                Target = graph.Target.ToString(),
                Precision = graph.Precision.ToString(),
                BlendMode = graph.BlendMode.ToString(),
                ShadingModel = graph.ShadingModel.ToString(),
                DoubleSided = graph.DoubleSided,
                Wireframe = graph.Wireframe,
                AlphaCutoff = graph.AlphaCutoff,
                OutputNodeId = graph.OutputNodeId,
                Keywords = graph.Keywords.ToList()
            };

            // 节点
            foreach (var node in graph.Nodes)
            {
                var nd = new NodeData
                {
                    Id = node.Id,
                    Type = node.NodeType,
                    Title = node.Title,
                    Description = node.Description,
                    PosX = node.Position.X,
                    PosY = node.Position.Y,
                    SizeX = node.Size.X,
                    SizeY = node.Size.Y,
                    Enabled = node.Enabled,
                    Collapsed = node.Collapsed,
                    ColorTag = node.ColorTag,
                    Comment = node.Comment
                };
                foreach (var pin in node.InputPins)
                    nd.InputPins.Add(SerializePin(pin));
                foreach (var pin in node.OutputPins)
                    nd.OutputPins.Add(SerializePin(pin));
                // 元数据
                foreach (var kv in node.Metadata)
                {
                    try
                    {
                        nd.Metadata[kv.Key] = JsonSerializer.SerializeToElement(kv.Value, JsonOptions);
                    }
                    catch { /* 跳过不可序列化的元数据 */ }
                }
                data.Nodes.Add(nd);
            }

            // 连接
            foreach (var conn in graph.Connections)
            {
                data.Connections.Add(new ConnectionData
                {
                    Id = conn.Id,
                    SourceNodeId = conn.SourceNodeId,
                    SourcePinId = conn.SourcePinId,
                    TargetNodeId = conn.TargetNodeId,
                    TargetPinId = conn.TargetPinId,
                    Enabled = conn.Enabled
                });
            }

            // 属性
            foreach (var prop in graph.Properties)
            {
                data.Properties.Add(new PropertyData
                {
                    Id = prop.Id,
                    Name = prop.Name,
                    DisplayName = prop.DisplayName,
                    Tooltip = prop.Tooltip,
                    PropertyType = prop.PropertyType.ToString(),
                    ScalarValue = prop.ScalarValue,
                    VecX = prop.VectorValue.X,
                    VecY = prop.VectorValue.Y,
                    VecZ = prop.VectorValue.Z,
                    VecW = prop.VectorValue.W,
                    TexturePath = prop.TexturePath,
                    TextureGuid = GetStringMeta(prop, "Guid"),
                    BoolValue = prop.BoolValue,
                    IntValue = prop.IntValue,
                    MinValue = prop.MinValue,
                    MaxValue = prop.MaxValue,
                    UseRange = prop.UseRange,
                    Group = prop.Group,
                    SortOrder = prop.SortOrder,
                    Visible = prop.Visible
                });
            }

            // 子图
            foreach (var sub in graph.SubGraphs)
            {
                data.SubGraphs.Add(new SubGraphRefData
                {
                    Guid = sub.Guid,
                    Name = sub.Name,
                    Description = sub.Description,
                    FilePath = sub.FilePath,
                    ReferenceType = DetermineReferenceType(sub.FilePath)
                });
            }

            return data;
        }

        private static PinData SerializePin(ShaderGraphPin pin)
        {
            return new PinData
            {
                Id = pin.Id,
                Name = pin.Name,
                DataType = pin.DataType.ToString(),
                DefX = pin.DefaultValue.X,
                DefY = pin.DefaultValue.Y,
                DefZ = pin.DefaultValue.Z,
                DefW = pin.DefaultValue.W,
                IntDefault = pin.IntDefaultValue,
                BoolDefault = pin.BoolDefaultValue,
                StringDefault = pin.StringDefaultValue,
                Required = pin.Required,
                SliderMin = pin.SliderMin,
                SliderMax = pin.SliderMax
            };
        }

        private static string GetStringMeta(ShaderProperty prop, string key)
        {
            // 简单: 从纹理路径派生 (实际应存储 GUID)
            return string.IsNullOrEmpty(prop.TexturePath) ? string.Empty :
                Guid.NewGuid().ToString("N"); // 占位
        }

        // ── 数据模型 -> 图 ────────────────────────────────────

        /// <summary>将数据模型转换为 ShaderGraph。</summary>
        private static ShaderGraph DeserializeGraph(ShaderGraphData data)
        {
            var graph = new ShaderGraph
            {
                Guid = data.Guid,
                Name = data.Name,
                Description = data.Description,
                Target = Enum.TryParse<ShaderGraphTarget>(data.Target, out var target) ? target : ShaderGraphTarget.Universal,
                Precision = Enum.TryParse<ShaderGraphPrecision>(data.Precision, out var prec) ? prec : ShaderGraphPrecision.Float,
                BlendMode = Enum.TryParse<ShaderGraphBlendMode>(data.BlendMode, out var blend) ? blend : ShaderGraphBlendMode.Opaque,
                ShadingModel = Enum.TryParse<ShaderGraphShadingModel>(data.ShadingModel, out var sm) ? sm : ShaderGraphShadingModel.Lit,
                DoubleSided = data.DoubleSided,
                Wireframe = data.Wireframe,
                AlphaCutoff = data.AlphaCutoff,
                OutputNodeId = data.OutputNodeId,
                Keywords = data.Keywords ?? new List<string>()
            };

            // 节点: 先创建所有节点实例, 再恢复引脚
            var nodeMap = new Dictionary<string, ShaderGraphNode>();
            foreach (var nd in data.Nodes)
            {
                var node = ShaderNodeRegistry.CreateNode(nd.Type);
                if (node == null)
                {
                    // 未知节点类型: 创建通用节点
                    node = new ShaderGraphNode();
                }
                node.Id = nd.Id;
                node.NodeType = nd.Type;
                node.Title = nd.Title;
                node.Description = nd.Description;
                node.Position = new Vector2(nd.PosX, nd.PosY);
                node.Size = new Vector2(nd.SizeX, nd.SizeY);
                node.Enabled = nd.Enabled;
                node.Collapsed = nd.Collapsed;
                node.ColorTag = nd.ColorTag ?? string.Empty;
                node.Comment = nd.Comment ?? string.Empty;

                // 恢复引脚 (若节点已自带引脚则更新默认值, 否则重建)
                RestorePins(node.InputPins, nd.InputPins, node, ShaderGraphPinDirection.Input);
                RestorePins(node.OutputPins, nd.OutputPins, node, ShaderGraphPinDirection.Output);

                // 恢复元数据
                foreach (var kv in nd.Metadata)
                {
                    try
                    {
                        var value = DeserializeJsonValue(kv.Value);
                        if (value != null)
                            node.Metadata[kv.Key] = value;
                    }
                    catch { /* 跳过 */ }
                }

                graph.Nodes.Add(node);
                nodeMap[nd.Id] = node;
            }

            // 连接
            foreach (var cd in data.Connections)
            {
                var conn = new ShaderGraphConnection
                {
                    Id = cd.Id,
                    SourceNodeId = cd.SourceNodeId,
                    SourcePinId = cd.SourcePinId,
                    TargetNodeId = cd.TargetNodeId,
                    TargetPinId = cd.TargetPinId,
                    Enabled = cd.Enabled
                };
                graph.Connections.Add(conn);
                // 设置输入引脚的连接引用
                if (nodeMap.TryGetValue(cd.TargetNodeId, out var dstNode))
                {
                    var pin = dstNode.FindPinById(cd.TargetPinId);
                    if (pin != null) pin.Connection = conn;
                }
            }

            // 属性
            foreach (var pd in data.Properties)
            {
                var prop = new ShaderProperty
                {
                    Id = pd.Id,
                    Name = pd.Name,
                    DisplayName = pd.DisplayName,
                    Tooltip = pd.Tooltip,
                    PropertyType = Enum.TryParse<ShaderPropertyType>(pd.PropertyType, out var pt) ? pt : ShaderPropertyType.Float,
                    ScalarValue = pd.ScalarValue,
                    VectorValue = new Vector4(pd.VecX, pd.VecY, pd.VecZ, pd.VecW),
                    TexturePath = pd.TexturePath,
                    BoolValue = pd.BoolValue,
                    IntValue = pd.IntValue,
                    MinValue = pd.MinValue,
                    MaxValue = pd.MaxValue,
                    UseRange = pd.UseRange,
                    Group = pd.Group ?? "Default",
                    SortOrder = pd.SortOrder,
                    Visible = pd.Visible
                };
                graph.Properties.Add(prop);
            }

            // 子图
            foreach (var sd in data.SubGraphs)
            {
                var sub = new ShaderSubGraph
                {
                    Guid = sd.Guid,
                    Name = sd.Name,
                    Description = sd.Description,
                    FilePath = sd.FilePath
                };
                graph.SubGraphs.Add(sub);
            }

            return graph;
        }

        /// <summary>恢复引脚列表 (更新默认值或重建)。</summary>
        private static void RestorePins(List<ShaderGraphPin> existing, List<PinData> data,
            ShaderGraphNode owner, ShaderGraphPinDirection dir)
        {
            if (data == null || data.Count == 0) return;

            // 若节点已有引脚, 按名称匹配更新默认值
            if (existing.Count > 0)
            {
                foreach (var pd in data)
                {
                    var pin = existing.FirstOrDefault(p => p.Name == pd.Name || p.Id == pd.Id);
                    if (pin != null)
                    {
                        pin.DefaultValue = new Vector4(pd.DefX, pd.DefY, pd.DefZ, pd.DefW);
                        pin.IntDefaultValue = pd.IntDefault;
                        pin.BoolDefaultValue = pd.BoolDefault;
                        pin.StringDefaultValue = pd.StringDefault ?? string.Empty;
                        pin.Required = pd.Required;
                        pin.SliderMin = pd.SliderMin;
                        pin.SliderMax = pd.SliderMax;
                    }
                }
            }
            else
            {
                // 重建引脚
                foreach (var pd in data)
                {
                    var pin = new ShaderGraphPin
                    {
                        Id = pd.Id,
                        Name = pd.Name,
                        Direction = dir,
                        DataType = Enum.TryParse<ShaderGraphDataType>(pd.DataType, out var dt) ? dt : ShaderGraphDataType.Float,
                        DefaultValue = new Vector4(pd.DefX, pd.DefY, pd.DefZ, pd.DefW),
                        IntDefaultValue = pd.IntDefault,
                        BoolDefaultValue = pd.BoolDefault,
                        StringDefaultValue = pd.StringDefault ?? string.Empty,
                        Required = pd.Required,
                        SliderMin = pd.SliderMin,
                        SliderMax = pd.SliderMax
                    };
                    existing.Add(pin);
                }
            }
        }

        // ── 版本迁移 ──────────────────────────────────────────

        /// <summary>版本迁移: 将旧版本数据迁移到当前版本。</summary>
        private static ShaderGraphData MigrateVersion(ShaderGraphData data)
        {
            if (data.Version >= ShaderGraphVersion.Current) return data;
            if (data.Version < ShaderGraphVersion.Minimum)
                throw new InvalidDataException($"不支持的着色器图版本: {data.Version}");

            // v1 -> v2: 添加 Precision 字段
            if (data.Version < 2)
            {
                if (string.IsNullOrEmpty(data.Precision))
                    data.Precision = "Float";
                data.Version = 2;
            }

            // v2 -> v3: 添加 Keywords/SubGraphs 字段
            if (data.Version < 3)
            {
                if (data.Keywords == null) data.Keywords = new List<string>();
                if (data.SubGraphs == null) data.SubGraphs = new List<SubGraphRefData>();
                data.Version = 3;
            }

            return data;
        }

        // ── 子图引用解析 ──────────────────────────────────────

        /// <summary>解析子图引用路径 (相对/绝对/GUID)。</summary>
        public static string ResolveSubGraphPath(string referencePath, string baseDirectory,
            string referenceType = "Relative")
        {
            if (string.IsNullOrEmpty(referencePath)) return string.Empty;

            switch (referenceType)
            {
                case "Absolute":
                    return referencePath;
                case "Guid":
                    // GUID 引用: 在资产数据库中查找 (此处返回占位)
                    return Path.Combine(baseDirectory, "Generated", referencePath + ".shgraph");
                case "Relative":
                default:
                    return Path.GetFullPath(Path.Combine(baseDirectory, referencePath));
            }
        }

        /// <summary>确定引用类型。</summary>
        private static string DetermineReferenceType(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "Relative";
            if (Path.IsPathRooted(filePath)) return "Absolute";
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(filePath), out _)) return "Guid";
            return "Relative";
        }

        // ── 纹理引用序列化 ────────────────────────────────────

        /// <summary>将纹理引用序列化为 Base64 内嵌数据。</summary>
        public static string EmbedTextureAsBase64(string texturePath)
        {
            if (!File.Exists(texturePath)) return string.Empty;
            var bytes = File.ReadAllBytes(texturePath);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>从 Base64 内嵌数据提取纹理。</summary>
        public static byte[] ExtractTextureFromBase64(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return Array.Empty<byte>();
            try { return Convert.FromBase64String(base64); }
            catch { return Array.Empty<byte>(); }
        }

        // ── 导出格式 ──────────────────────────────────────────

        /// <summary>导出为 .shgraph JSON 文件。</summary>
        public static void ExportShGraph(ShaderGraph graph, string path, bool compress = false)
        {
            ToFile(graph, path, compress);
        }

        /// <summary>导出为 .hlsl 文件 (直接编译为 HLSL 源码)。</summary>
        public static void ExportHLSL(ShaderGraph graph, string path, HLSLGenerateOptions? options = null)
        {
            var generator = new HLSLGenerator(graph, options);
            var result = generator.Generate();
            var hlsl = result.CombinedSource ?? result.VertexShaderSource ?? string.Empty;
            File.WriteAllText(path, hlsl, Encoding.UTF8);
        }

        /// <summary>导出为 .cs 脚本化材质 (生成 C# 代码构建图)。</summary>
        public static void ExportCSharpScript(ShaderGraph graph, string path, string className = "GeneratedMaterial")
        {
            var sb = new StringBuilder();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine($"// yama引擎 - 脚本化材质 (自动生成)");
            sb.AppendLine($"// 源图: {graph.Name}");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Numerics;");
            sb.AppendLine("using YamaEngine.ShaderGraph;");
            sb.AppendLine();
            sb.AppendLine($"public static class {className}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static ShaderGraph Build()");
            sb.AppendLine("    {");
            sb.AppendLine($"        var graph = new ShaderGraph");
            sb.AppendLine("        {");
            sb.AppendLine($"            Name = \"{Escape(graph.Name)}\",");
            sb.AppendLine($"            Target = ShaderGraphTarget.{graph.Target},");
            sb.AppendLine($"            Precision = ShaderGraphPrecision.{graph.Precision},");
            sb.AppendLine($"            BlendMode = ShaderGraphBlendMode.{graph.BlendMode},");
            sb.AppendLine($"            ShadingModel = ShaderGraphShadingModel.{graph.ShadingModel},");
            sb.AppendLine($"            DoubleSided = {graph.DoubleSided.ToString().ToLower()},");
            sb.AppendLine($"            AlphaCutoff = {graph.AlphaCutoff:G9}f");
            sb.AppendLine("        };");
            sb.AppendLine();

            // 节点
            foreach (var node in graph.Nodes)
            {
                sb.AppendLine($"        // Node: {node.Title}");
                sb.AppendLine($"        var n_{node.Id.Substring(0, 6)} = new {node.GetType().Name}");
                sb.AppendLine("        {");
                sb.AppendLine($"            Id = \"{node.Id}\",");
                sb.AppendLine($"            NodeType = \"{node.NodeType}\",");
                sb.AppendLine($"            Title = \"{Escape(node.Title)}\",");
                sb.AppendLine($"            Position = new Vector2({node.Position.X:G9}f, {node.Position.Y:G9}f)");
                sb.AppendLine("        };");
                sb.AppendLine($"        graph.AddNode(n_{node.Id.Substring(0, 6)});");
            }
            sb.AppendLine();

            // 连接
            foreach (var conn in graph.Connections)
            {
                sb.AppendLine($"        graph.Connect(\"{conn.SourceNodeId}\", \"{GetPinName(graph, conn.SourceNodeId, conn.SourcePinId)}\", ");
                sb.AppendLine($"                     \"{conn.TargetNodeId}\", \"{GetPinName(graph, conn.TargetNodeId, conn.TargetPinId)}\");");
            }
            sb.AppendLine();

            // 输出节点
            if (!string.IsNullOrEmpty(graph.OutputNodeId))
                sb.AppendLine($"        graph.OutputNodeId = \"{graph.OutputNodeId}\";");

            sb.AppendLine("        return graph;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>导出为材质实例 JSON。</summary>
        public static string ExportMaterialInstance(MaterialInstance instance)
        {
            return JsonSerializer.Serialize(instance, JsonOptions);
        }

        /// <summary>导入材质实例 JSON。</summary>
        public static MaterialInstance ImportMaterialInstance(string json)
        {
            return JsonSerializer.Deserialize<MaterialInstance>(json, JsonOptions)
                ?? new MaterialInstance();
        }

        // ── 辅助方法 ──────────────────────────────────────────

        /// <summary>反序列化 JSON 值为 C# 对象。</summary>
        private static object? DeserializeJsonValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out var i)) return i;
                    if (element.TryGetSingle(out var f)) return f;
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    return element.GetRawText();
                default:
                    return null;
            }
        }

        /// <summary>获取引脚名称 (通过 ID)。</summary>
        private static string GetPinName(ShaderGraph graph, string nodeId, string pinId)
        {
            var node = graph.GetNode(nodeId);
            var pin = node?.FindPinById(pinId);
            return pin?.Name ?? string.Empty;
        }

        /// <summary>转义字符串中的特殊字符。</summary>
        private static string Escape(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? string.Empty;

        /// <summary>计算图的序列化哈希 (用于缓存)。</summary>
        public static string ComputeHash(ShaderGraph graph)
        {
            var json = ToJson(graph, indented: false);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash);
        }
    }
}
