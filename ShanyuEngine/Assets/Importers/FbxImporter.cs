using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  FbxImporter —— FBX 解析器主入口
    // =================================================================================

    /// <summary>
    /// 纯 C# FBX 解析器。支持 ASCII FBX（FBX 2010-2020）和二进制 FBX（FBX 2014+）。
    /// 输出为 InterchangeScene 统一格式。
    /// </summary>
    public sealed class FbxImporter
    {
        private readonly string _filePath;
        private readonly List<string> _messages = new();

        // 解析过程中的映射表
        private readonly Dictionary<long, FbxNode> _nodeById = new();
        private readonly Dictionary<long, FbxMeshData> _meshById = new();
        private readonly Dictionary<long, FbxMaterialData> _materialById = new();
        private readonly Dictionary<long, FbxNodeData> _nodeDataById = new();
        private readonly Dictionary<long, FbxClusterData> _clusterById = new();
        private readonly Dictionary<long, FbxSkinData> _skinById = new();
        private readonly Dictionary<long, FbxPoseData> _poseById = new();
        private readonly Dictionary<long, FbxAnimCurveData> _animCurveById = new();

        /// <summary>解析产出的统一场景格式。</summary>
        public InterchangeScene? Scene { get; private set; }

        /// <summary>构造 FBX 导入器。</summary>
        public FbxImporter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>执行导入。检测格式、解析、输出 InterchangeScene。</summary>
        public InterchangeScene Import()
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException($"FBX 源文件不存在: {_filePath}");

            Scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(_filePath),
                SourceFilePath = _filePath,
                SourceFormat = "FBX"
            };

            try
            {
                bool isBinary = FbxBinaryParser.IsBinaryFbx(_filePath);

                if (isBinary)
                    ParseBinaryFbx();
                else
                    ParseAsciiFbx();

                PostProcessScene();
            }
            catch (Exception ex)
            {
                Scene.ImportMessages.Add($"导入 FBX 失败: {ex.Message}");
                throw;
            }

            return Scene;
        }

        // =================================================================================
        //  二进制 FBX 解析（委托给 FbxBinaryParser）
        // =================================================================================

        private void ParseBinaryFbx()
        {
            _messages.Add("检测到二进制 FBX 格式，使用 FbxBinaryParser 解析");

            using var parser = new FbxBinaryParser(_filePath);
            parser.Parse();

            _messages.Add($"二进制 FBX 版本 {parser.Version}, 根节点数 {parser.RootNodes.Count}");

            // 将 FbxBinaryParser 的节点树转换为内部表示
            var root = new FbxNode
            {
                Name = "Root",
                Children = new List<FbxNode>()
            };

            foreach (var binaryNode in parser.RootNodes)
            {
                var child = ConvertBinaryNode(binaryNode);
                if (child != null)
                    root.Children.Add(child);
            }

            // 从内部节点树提取场景数据
            ExtractFromNodeTree(root);
        }

        /// <summary>将二进制解析器的节点转换为内部 FbxNode。</summary>
        private FbxNode? ConvertBinaryNode(FbxBinaryNode binaryNode)
        {
            if (binaryNode == null) return null;

            var node = new FbxNode
            {
                Name = binaryNode.Name,
                Properties = new List<object>()
            };

            // 转换属性
            foreach (var prop in binaryNode.Properties)
            {
                if (prop.IsArray)
                {
                    node.Properties.Add(prop.ArrayValue!);
                }
                else if (prop.ScalarValue != null)
                {
                    node.Properties.Add(prop.ScalarValue);
                }
            }

            // 递归子节点
            node.Children = new List<FbxNode>();
            foreach (var child in binaryNode.Children)
            {
                var converted = ConvertBinaryNode(child);
                if (converted != null)
                    node.Children.Add(converted);
            }

            return node;
        }

        // =================================================================================
        //  ASCII FBX 解析
        // =================================================================================

        /// <summary>内部表示的 FBX 节点。</summary>
        private class FbxNode
        {
            public string Name = string.Empty;
            public List<object> Properties = new();
            public List<FbxNode> Children = new();

            public FbxNode? FindChild(string name)
            {
                foreach (var c in Children)
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        return c;
                return null;
            }

            public List<FbxNode> FindChildren(string name)
            {
                var results = new List<FbxNode>();
                foreach (var c in Children)
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        results.Add(c);
                return results;
            }

            public T? GetProperty<T>(int index) where T : class
            {
                if (index < 0 || index >= Properties.Count) return null;
                return Properties[index] as T;
            }

            public T GetPropertyValue<T>(int index, T defaultValue = default!)
            {
                if (index < 0 || index >= Properties.Count) return defaultValue;
                try { return (T)Convert.ChangeType(Properties[index], typeof(T), CultureInfo.InvariantCulture); }
                catch { return defaultValue; }
            }
        }

        private void ParseAsciiFbx()
        {
            _messages.Add("检测到 ASCII FBX 格式");

            string content = File.ReadAllText(_filePath);
            var lexer = new FbxAsciiLexer(content);
            var tokens = lexer.Tokenize();

            var parser = new FbxAsciiParser(tokens);
            var nodes = parser.Parse();

            var root = new FbxNode
            {
                Name = "Root",
                Children = nodes
            };

            ExtractFromNodeTree(root);
        }

        // =================================================================================
        //  ASCII FBX Tokenizer / Parser
        // =================================================================================

        /// <summary>FBX ASCII Token 类型。</summary>
        private enum FbxTokenType
        {
            Identifier,      // 节点名称
            StringLiteral,   // "..." 字符串
            Number,          // 数值
            OpenBrace,       // {
            CloseBrace,      // }
            Colon,           // :
            Comma,           // ,
            Asterisk,        // *
            Eof
        }

        private struct FbxToken
        {
            public FbxTokenType Type;
            public string Text;
            public double NumberValue;
        }

        /// <summary>FBX ASCII 词法分析器。</summary>
        private class FbxAsciiLexer
        {
            private readonly string _text;
            private int _pos;

            public FbxAsciiLexer(string text) { _text = text; _pos = 0; }

            public List<FbxToken> Tokenize()
            {
                var tokens = new List<FbxToken>();

                while (_pos < _text.Length)
                {
                    char c = _text[_pos];

                    // 跳过空白
                    if (char.IsWhiteSpace(c))
                    {
                        _pos++;
                        continue;
                    }

                    // 注释：从 ';' 到行尾 或 '//' 到行尾
                    if (c == ';' || (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/'))
                    {
                        while (_pos < _text.Length && _text[_pos] != '\n' && _text[_pos] != '\r')
                            _pos++;
                        continue;
                    }

                    // 块注释 /* ... */
                    if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                    {
                        _pos += 2;
                        while (_pos + 1 < _text.Length && !(_text[_pos] == '*' && _text[_pos + 1] == '/'))
                            _pos++;
                        _pos += 2;
                        continue;
                    }

                    switch (c)
                    {
                        case '{':
                            tokens.Add(new FbxToken { Type = FbxTokenType.OpenBrace, Text = "{" });
                            _pos++;
                            continue;
                        case '}':
                            tokens.Add(new FbxToken { Type = FbxTokenType.CloseBrace, Text = "}" });
                            _pos++;
                            continue;
                        case ':':
                            tokens.Add(new FbxToken { Type = FbxTokenType.Colon, Text = ":" });
                            _pos++;
                            continue;
                        case ',':
                            tokens.Add(new FbxToken { Type = FbxTokenType.Comma, Text = "," });
                            _pos++;
                            continue;
                        case '*':
                            tokens.Add(new FbxToken { Type = FbxTokenType.Asterisk, Text = "*" });
                            _pos++;
                            continue;
                    }

                    // 字符串字面量 "..."
                    if (c == '"')
                    {
                        _pos++;
                        int start = _pos;
                        while (_pos < _text.Length && _text[_pos] != '"')
                        {
                            if (_text[_pos] == '\\') _pos++; // 跳过转义
                            _pos++;
                        }
                        string str = _text.Substring(start, _pos - start);
                        tokens.Add(new FbxToken { Type = FbxTokenType.StringLiteral, Text = str });
                        _pos++; // 跳过闭合引号
                        continue;
                    }

                    // 数字或标识符
                    if (char.IsLetter(c) || c == '_')
                    {
                        int start = _pos;
                        while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                            _pos++;
                        string ident = _text.Substring(start, _pos - start);
                        tokens.Add(new FbxToken { Type = FbxTokenType.Identifier, Text = ident });
                        continue;
                    }

                    // 数值（包括负号和浮点数）
                    if (char.IsDigit(c) || c == '-')
                    {
                        int start = _pos;
                        if (_text[_pos] == '-') _pos++;
                        while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == 'e' || _text[_pos] == 'E' || _text[_pos] == '+' || _text[_pos] == '-'))
                        {
                            if ((_text[_pos] == '+' || _text[_pos] == '-') && _pos > start && _text[_pos - 1] != 'e' && _text[_pos - 1] != 'E')
                                break;
                            _pos++;
                        }
                        string numStr = _text.Substring(start, _pos - start);
                        double val = 0;
                        double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
                        tokens.Add(new FbxToken { Type = FbxTokenType.Number, Text = numStr, NumberValue = val });
                        continue;
                    }

                    // 未知字符 — 跳过
                    _pos++;
                }

                tokens.Add(new FbxToken { Type = FbxTokenType.Eof, Text = "" });
                return tokens;
            }
        }

        /// <summary>FBX ASCII 解析器：将 Token 流解析为 FbxNode 树。</summary>
        private class FbxAsciiParser
        {
            private readonly List<FbxToken> _tokens;
            private int _pos;

            public FbxAsciiParser(List<FbxToken> tokens) { _tokens = tokens; _pos = 0; }

            public List<FbxNode> Parse()
            {
                var nodes = new List<FbxNode>();
                while (_pos < _tokens.Count && _tokens[_pos].Type != FbxTokenType.Eof)
                {
                    var node = ParseNode();
                    if (node != null)
                        nodes.Add(node);
                    else
                        break;
                }
                return nodes;
            }

            private FbxNode? ParseNode()
            {
                // 预期：Identifier : [properties] { children } 或 Identifier : properties
                SkipNewLines();

                if (_pos >= _tokens.Count || _tokens[_pos].Type == FbxTokenType.Eof)
                    return null;

                // 必须是一个标识符（节点名）
                if (_tokens[_pos].Type != FbxTokenType.Identifier &&
                    _tokens[_pos].Type != FbxTokenType.Number)  // 数字也可能是节点名
                    return null;

                var node = new FbxNode
                {
                    Name = _tokens[_pos].Text
                };
                _pos++;

                // 跳过可能的新行标记（逗号/分号）
                SkipNewLines();

                // 预期冒号
                if (_pos < _tokens.Count && _tokens[_pos].Type == FbxTokenType.Colon)
                {
                    _pos++;
                    SkipNewLines();

                    // 读取属性列表（直到 { 或遇到下一个节点开始）
                    node.Properties = ParsePropertyList();
                }

                // 子节点（以 { 开始）
                SkipNewLines();
                if (_pos < _tokens.Count && _tokens[_pos].Type == FbxTokenType.OpenBrace)
                {
                    _pos++; // 跳过 {
                    SkipNewLines();

                    node.Children = new List<FbxNode>();
                    while (_pos < _tokens.Count && _tokens[_pos].Type != FbxTokenType.CloseBrace)
                    {
                        var child = ParseNode();
                        if (child != null)
                            node.Children.Add(child);
                        else
                            break;
                        SkipNewLines();
                    }

                    // 跳过 }
                    if (_pos < _tokens.Count && _tokens[_pos].Type == FbxTokenType.CloseBrace)
                        _pos++;
                }

                return node;
            }

            private List<object> ParsePropertyList()
            {
                var props = new List<object>();

                while (_pos < _tokens.Count)
                {
                    var tok = _tokens[_pos];

                    switch (tok.Type)
                    {
                        case FbxTokenType.StringLiteral:
                            props.Add(tok.Text);
                            _pos++;
                            break;
                        case FbxTokenType.Number:
                            props.Add(tok.NumberValue);
                            _pos++;
                            break;
                        case FbxTokenType.Identifier:
                            // 可能是 true/false 或特殊标记
                            if (tok.Text == "true" || tok.Text == "True" || tok.Text == "T")
                            {
                                props.Add(true);
                                _pos++;
                            }
                            else if (tok.Text == "false" || tok.Text == "False" || tok.Text == "F")
                            {
                                props.Add(false);
                                _pos++;
                            }
                            else
                            {
                                // 可能是数组开始的标记，如 * 后接数字
                                // 或者是下一个节点名 — 停止
                                goto BreakOut;
                            }
                            break;
                        case FbxTokenType.Asterisk:
                            // 数组格式：* {count} { { ... } }
                            _pos++; // 跳过 *
                            // 读取数组长度（数值）
                            if (_pos < _tokens.Count && _tokens[_pos].Type == FbxTokenType.Number)
                            {
                                int arrayCount = (int)_tokens[_pos].NumberValue;
                                _pos++;
                                // 跳过后续逗号/空白
                                SkipNewLines();
                                // 读取 { ... } 包裹的数组数据
                                if (_pos < _tokens.Count && _tokens[_pos].Type == FbxTokenType.OpenBrace)
                                {
                                    _pos++; // 跳过 {
                                    SkipNewLines();
                                    var arrayProps = ParsePropertyList();
                                    if (_pos < _tokens.Count && _tokens[_pos].Type == FbxTokenType.CloseBrace)
                                        _pos++; // 跳过 }
                                    props.Add(arrayProps);
                                }
                            }
                            break;
                        case FbxTokenType.Comma:
                            // FBX ASCII 有时用逗号分隔属性 — 跳过
                            _pos++;
                            break;
                        default:
                            goto BreakOut;
                    }
                }
            BreakOut:
                return props;
            }

            private void SkipNewLines()
            {
                // 跳过逗号和分号（FBX ASCII 行结束符）
                while (_pos < _tokens.Count &&
                       (_tokens[_pos].Type == FbxTokenType.Comma ||
                        (_tokens[_pos].Type == FbxTokenType.Identifier && _tokens[_pos].Text == ";")))
                    _pos++;
            }
        }

        // =================================================================================
        //  从节点树提取场景数据（ASCII & 二进制共用）
        // =================================================================================

        /// <summary>从内部 FbxNode 树提取场景数据到 InterchangeScene。</summary>
        private void ExtractFromNodeTree(FbxNode root)
        {
            // 阶段 1: 提取所有对象定义和连接（Objects / Connections）
            ExtractObjects(root);

            // 阶段 2: 提取层级（Models）
            ExtractHierarchy(root);

            // 阶段 3: 提取动画
            ExtractAnimations(root);

            // 阶段 4: 提取场景全局设置
            ExtractGlobalSettings(root);
        }

        // =================================================================================
        //  FBX 对象数据结构
        // =================================================================================

        private struct FbxMeshData
        {
            public long Id;
            public string Name;
            public float[] Vertices;
            public int[] PolygonVertexIndex;
            public float[] Normals;
            public float[] UVs;
            public float[] Tangents;
            public float[] Colors;
            // 映射模式
            public int NormalMappingMode;
            public int UVMappingMode;
            public int MaterialMappingMode;
            // 材质分配
            public int[] MaterialIndices;
        }

        private struct FbxMaterialData
        {
            public long Id;
            public string Name;
            public string ShadingModel;
            // FBX Phong/Lambert 属性
            public float[] Diffuse;
            public float[] Emissive;
            public float[] Specular;
            public float[] Ambient;
            public float Shininess;
            public float Transparency;
            public float[] NormalMap;
        }

        private struct FbxNodeData
        {
            public long Id;
            public string Name;
            public long? ParentId;
            public double[] Translation;
            public double[] Rotation;
            public double[] Scale;
            public double[] RotationOffset;
            public double[] RotationPivot;
            public double[] ScalingOffset;
            public double[] ScalingPivot;
            public long? AttributeId;
        }

        private struct FbxClusterData
        {
            public long Id;
            public string Name;
            public long? LinkId;       // 骨骼节点 ID
            public long? SkinId;       // 所属蒙皮 ID
            public double[] Transform; // 变换矩阵 (16 doubles)
            public double[] TransformLink; // 绑定矩阵 (16 doubles)
            public List<int> Indices = new();
            public List<double> Weights = new();
            public FbxClusterData() { Indices = new List<int>(); Weights = new List<double>(); }
        }

        private struct FbxSkinData
        {
            public long Id;
            public string Name;
            public long? GeometryId; // 蒙皮的网格 ID
            public List<long> ClusterIds = new();
            public FbxSkinData() { ClusterIds = new List<long>(); }
        }

        private struct FbxPoseData
        {
            public long Id;
            public string Name;
            public bool IsBindPose;
            public List<(long NodeId, double[] Matrix)> PoseNodes = new();
            public FbxPoseData() { PoseNodes = new List<(long, double[])>(); }
        }

        private struct FbxAnimCurveData
        {
            public long Id;
            public string Name;
            public float[] KeyTimes;
            public float[] KeyValues;
        }

        // =================================================================================
        //  Objects 提取
        // =================================================================================

        private void ExtractObjects(FbxNode root)
        {
            var objectsNode = root.FindChild("Objects");
            if (objectsNode == null) return;

            foreach (var objNode in objectsNode.Children)
            {
                if (objNode.Properties.Count == 0) continue;

                // 第一个属性通常是 ID
                long id = 0;
                if (objNode.Properties[0] is double d)
                    id = (long)d;
                else if (objNode.Properties[0] is long l)
                    id = l;

                string name = objNode.Name;

                switch (name)
                {
                    case "Geometry":
                    case "Mesh":
                        ExtractMesh(objNode, id);
                        break;
                    case "Material":
                        ExtractMaterial(objNode, id);
                        break;
                    case "Model":
                    case "NodeAttribute":
                        ExtractNodeData(objNode, id);
                        break;
                    case "Deformer":
                        ExtractDeformer(objNode, id);
                        break;
                    case "Pose":
                        ExtractPose(objNode, id);
                        break;
                    case "AnimationCurve":
                        ExtractAnimCurve(objNode, id);
                        break;
                }
            }
        }

        private void ExtractMesh(FbxNode node, long id)
        {
            var mesh = new FbxMeshData { Id = id };

            // 名称通常在属性中（第二个属性为字符串）
            if (node.Properties.Count > 1 && node.Properties[1] is string nameStr)
                mesh.Name = nameStr;

            // 提取几何属性
            foreach (var child in node.Children)
            {
                switch (child.Name)
                {
                    case "Vertices":
                        mesh.Vertices = ExtractFloatArray(child);
                        break;
                    case "PolygonVertexIndex":
                        mesh.PolygonVertexIndex = ExtractIntArray(child);
                        break;
                    case "LayerElementNormal":
                        mesh.NormalMappingMode = ExtractMappingMode(child);
                        mesh.Normals = ExtractFloatArray(child.FindChild("Normals"));
                        break;
                    case "LayerElementUV":
                        mesh.UVMappingMode = ExtractMappingMode(child);
                        mesh.UVs = ExtractFloatArray(child.FindChild("UV"));
                        break;
                    case "LayerElementTangent":
                        mesh.Tangents = ExtractFloatArray(child.FindChild("Tangents"));
                        break;
                    case "LayerElementColor":
                        mesh.Colors = ExtractFloatArray(child.FindChild("Colors"));
                        break;
                    case "LayerElementMaterial":
                        mesh.MaterialMappingMode = ExtractMappingMode(child);
                        mesh.MaterialIndices = ExtractIntArray(child.FindChild("Materials"));
                        break;
                }
            }

            _meshById[id] = mesh;
        }

        /// <summary>提取层元素的映射模式（0=ByControlPoint, 1=ByPolygonVertex, 2=ByPolygon, ...）。</summary>
        private int ExtractMappingMode(FbxNode layerNode)
        {
            var modeNode = layerNode?.FindChild("MappingInformationType");
            if (modeNode == null || modeNode.Properties.Count == 0) return 0;

            string mode = modeNode.Properties[0]?.ToString() ?? "";
            return mode switch
            {
                "ByControlPoint" => 0,
                "ByPolygonVertex" => 1,
                "ByPolygon" => 2,
                "ByEdge" => 3,
                "AllSame" => 4,
                _ => 0
            };
        }

        /// <summary>从节点属性中提取 float 数组。</summary>
        private float[] ExtractFloatArray(FbxNode? node)
        {
            if (node == null || node.Properties.Count == 0) return Array.Empty<float>();

            // 检查是否为数组格式（属性列表中的 List<object>）
            foreach (var prop in node.Properties)
            {
                if (prop is List<object> list)
                {
                    var floats = new List<float>();
                    foreach (var item in list)
                    {
                        if (item is double d) floats.Add((float)d);
                        if (item is int i) floats.Add(i);
                    }
                    return floats.ToArray();
                }
            }

            // 也可能是直接在属性中的值（vector 或标量）
            var result = new List<float>();
            foreach (var prop in node.Properties)
            {
                if (prop is double d) result.Add((float)d);
                if (prop is int i) result.Add(i);
                if (prop is long l) result.Add(l);
            }

            return result.ToArray();
        }

        /// <summary>从节点属性中提取 int 数组。</summary>
        private int[] ExtractIntArray(FbxNode? node)
        {
            if (node == null || node.Properties.Count == 0) return Array.Empty<int>();

            foreach (var prop in node.Properties)
            {
                if (prop is List<object> list)
                {
                    var ints = new List<int>();
                    foreach (var item in list)
                    {
                        if (item is double d) ints.Add((int)d);
                        if (item is int i) ints.Add(i);
                    }
                    return ints.ToArray();
                }
            }

            var result = new List<int>();
            foreach (var prop in node.Properties)
            {
                if (prop is double d) result.Add((int)d);
                if (prop is int i) result.Add(i);
                if (prop is long l) result.Add((int)l);
            }

            return result.ToArray();
        }

        private void ExtractMaterial(FbxNode node, long id)
        {
            var mat = new FbxMaterialData { Id = id };

            if (node.Properties.Count > 1 && node.Properties[1] is string nameStr)
                mat.Name = nameStr;

            // 着色模型
            var shadingNode = node.FindChild("ShadingModel");
            if (shadingNode != null && shadingNode.Properties.Count > 0)
                mat.ShadingModel = shadingNode.Properties[0]?.ToString() ?? "unknown";

            // FBX 材质属性在 MultiLayer/Properties70 下
            ExtractMaterialProperties(node, ref mat);

            _materialById[id] = mat;
        }

        /// <summary>提取 FBX 材质属性 (Properties70)。</summary>
        private void ExtractMaterialProperties(FbxNode node, ref FbxMaterialData mat)
        {
            var props70 = node.FindChild("Properties70");
            if (props70 == null) return;

            foreach (var prop in props70.Children)
            {
                if (prop.Properties.Count < 4) continue;

                string propName = prop.Name;
                var values = prop.Properties;

                // FBX Properties70 格式: P { name, type, label, flags, ... values }
                string paramName = values[0]?.ToString() ?? "";

                switch (paramName)
                {
                    case "DiffuseColor":
                        mat.Diffuse = ExtractColorProp(values);
                        break;
                    case "EmissiveColor":
                        mat.Emissive = ExtractColorProp(values);
                        break;
                    case "SpecularColor":
                        mat.Specular = ExtractColorProp(values);
                        break;
                    case "AmbientColor":
                        mat.Ambient = ExtractColorProp(values);
                        break;
                    case "Shininess":
                        mat.Shininess = ExtractFloatProp(values);
                        break;
                    case "TransparencyFactor":
                    case "Opacity":
                        mat.Transparency = 1f - ExtractFloatProp(values);
                        break;
                    case "NormalMap":
                        mat.NormalMap = ExtractColorProp(values);
                        break;
                }
            }
        }

        private float[] ExtractColorProp(List<object> values)
        {
            // 格式: name, type, label, flags, r, g, b 或 name, type, label, flags, v
            var result = new float[3];
            int valueStart = 4;
            if (values.Count > valueStart)
            {
                result[0] = ToFloat(values[valueStart]);
                if (values.Count > valueStart + 1) result[1] = ToFloat(values[valueStart + 1]);
                if (values.Count > valueStart + 2) result[2] = ToFloat(values[valueStart + 2]);
            }
            return result;
        }

        private float ExtractFloatProp(List<object> values)
        {
            if (values.Count > 4)
                return ToFloat(values[4]);
            return 0f;
        }

        private float ToFloat(object v)
        {
            if (v is double d) return (float)d;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is float f) return f;
            return 0f;
        }

        private void ExtractNodeData(FbxNode node, long id)
        {
            var data = new FbxNodeData { Id = id };

            if (node.Properties.Count > 1 && node.Properties[1] is string nameStr)
                data.Name = nameStr;

            // 提取 Properties70 中的变换数据
            ExtractTransformProperties(node, ref data);

            _nodeDataById[id] = data;
        }

        /// <summary>从 Properties70 提取变换属性。</summary>
        private void ExtractTransformProperties(FbxNode node, ref FbxNodeData data)
        {
            var props70 = node.FindChild("Properties70");
            if (props70 == null) return;

            foreach (var prop in props70.Children)
            {
                string paramName = prop.Properties.Count > 0 ? prop.Properties[0]?.ToString() ?? "" : "";

                switch (paramName)
                {
                    case "Lcl Translation":
                        data.Translation = ExtractDoubleVec3(prop);
                        break;
                    case "Lcl Rotation":
                        data.Rotation = ExtractDoubleVec3(prop);
                        break;
                    case "Lcl Scaling":
                        data.Scale = ExtractDoubleVec3(prop);
                        break;
                    case "RotationOffset":
                        data.RotationOffset = ExtractDoubleVec3(prop);
                        break;
                    case "RotationPivot":
                        data.RotationPivot = ExtractDoubleVec3(prop);
                        break;
                    case "ScalingOffset":
                        data.ScalingOffset = ExtractDoubleVec3(prop);
                        break;
                    case "ScalingPivot":
                        data.ScalingPivot = ExtractDoubleVec3(prop);
                        break;
                }
            }
        }

        private double[] ExtractDoubleVec3(FbxNode prop)
        {
            // 格式: name, type, label, flags, x, y, z
            if (prop.Properties.Count >= 7)
            {
                return new double[]
                {
                    ToDouble(prop.Properties[4]),
                    ToDouble(prop.Properties[5]),
                    ToDouble(prop.Properties[6])
                };
            }
            return new double[] { 0, 0, 0 };
        }

        private double ToDouble(object v)
        {
            if (v is double d) return d;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is float f) return f;
            return 0;
        }

        private void ExtractDeformer(FbxNode node, long id)
        {
            string type = node.Name; // "Skin" 或 "Cluster"

            // 子类型由 Properties 中的类型字符串区分
            var typeNode = node.FindChild("Type");
            string deformerType = typeNode != null && typeNode.Properties.Count > 0
                ? typeNode.Properties[0]?.ToString() ?? ""
                : "";

            // 或者节点名称为 "Skin"/"Cluster"
            if (node.Properties.Count > 1 && node.Properties[1] is string nameStr)
            {
                // 根据子节点判断
                if (node.FindChild("Indexes") != null || node.FindChild("Weights") != null)
                {
                    // Cluster
                    var cluster = new FbxClusterData { Id = id, Name = nameStr };
                    cluster.Indices.AddRange(ExtractIntArray(node.FindChild("Indexes")));
                    cluster.Weights.AddRange(ExtractDoubleArray(node.FindChild("Weights")));
                    cluster.Transform = ExtractDoubleArray(node.FindChild("Transform"));
                    cluster.TransformLink = ExtractDoubleArray(node.FindChild("TransformLink"));
                    _clusterById[id] = cluster;
                }
                else
                {
                    // Skin
                    var skin = new FbxSkinData { Id = id, Name = nameStr };
                    _skinById[id] = skin;
                }
            }
        }

        private double[] ExtractDoubleArray(FbxNode? node)
        {
            if (node == null || node.Properties.Count == 0) return Array.Empty<double>();

            foreach (var prop in node.Properties)
            {
                if (prop is List<object> list)
                {
                    var doubles = new List<double>();
                    foreach (var item in list)
                    {
                        if (item is double d) doubles.Add(d);
                        if (item is int i) doubles.Add(i);
                    }
                    return doubles.ToArray();
                }
            }

            var result = new List<double>();
            foreach (var prop in node.Properties)
            {
                if (prop is double d) result.Add(d);
                if (prop is int i) result.Add(i);
                if (prop is long l) result.Add(l);
            }

            return result.ToArray();
        }

        private void ExtractPose(FbxNode node, long id)
        {
            var pose = new FbxPoseData { Id = id };

            // 类型判断 (BindPose)
            var typeNode = node.FindChild("Type");
            if (typeNode != null && typeNode.Properties.Count > 0)
                pose.IsBindPose = typeNode.Properties[0]?.ToString() == "BindPose";

            // Pose 节点列表
            foreach (var poseNode in node.FindChildren("PoseNode"))
            {
                var nodeIdNode = poseNode.FindChild("Node");
                var matrixNode = poseNode.FindChild("Matrix");

                long nodeId = 0;
                if (nodeIdNode != null && nodeIdNode.Properties.Count > 0)
                    nodeId = (long)ToDouble(nodeIdNode.Properties[0]);

                double[] matrix = ExtractDoubleArray(matrixNode);

                pose.PoseNodes.Add((nodeId, matrix));
            }

            _poseById[id] = pose;
        }

        private void ExtractAnimCurve(FbxNode node, long id)
        {
            var curve = new FbxAnimCurveData { Id = id };

            // 时间关键帧
            var timeNode = node.FindChild("KeyTime");
            if (timeNode != null)
                curve.KeyTimes = ExtractFloatArray(timeNode);

            // 值关键帧
            var valueNode = node.FindChild("KeyValueFloat");
            if (valueNode != null)
                curve.KeyValues = ExtractFloatArray(valueNode);

            _animCurveById[id] = curve;
        }

        // =================================================================================
        //  Connections 提取 & 层级构建
        // =================================================================================

        private void ExtractHierarchy(FbxNode root)
        {
            var connectionsNode = root.FindChild("Connections");
            if (connectionsNode == null) return;

            // 建立父子关系
            foreach (var conn in connectionsNode.Children)
            {
                if (conn.Properties.Count < 3) continue;

                string type = conn.Properties[0]?.ToString() ?? "";
                if (type != "OO") continue; // Object-Object 连接

                long childId = (long)ToDouble(conn.Properties[1]);
                long parentId = (long)ToDouble(conn.Properties[2]);

                if (_nodeDataById.TryGetValue(childId, out var childData))
                    childData.ParentId = parentId;
            }

            // 建立 Skin -> Geometry 和 Cluster -> Skin 连接
            foreach (var conn in connectionsNode.Children)
            {
                if (conn.Properties.Count < 3) continue;
                string type = conn.Properties[0]?.ToString() ?? "";

                long childId = (long)ToDouble(conn.Properties[1]);
                long parentId = (long)ToDouble(conn.Properties[2]);

                if (type == "OO")
                {
                    // Skin -> Geometry
                    if (_skinById.ContainsKey(childId) && _meshById.ContainsKey(parentId))
                    {
                        var skin = _skinById[childId];
                        skin.GeometryId = parentId;
                        _skinById[childId] = skin;
                    }

                    // Cluster -> Skin
                    if (_clusterById.ContainsKey(childId) && _skinById.ContainsKey(parentId))
                    {
                        var cluster = _clusterById[childId];
                        cluster.SkinId = parentId;
                        _clusterById[childId] = cluster;

                        var skin = _skinById[parentId];
                        skin.ClusterIds.Add(childId);
                        _skinById[parentId] = skin;
                    }

                    // Cluster -> Link (骨骼节点)
                    if (_clusterById.ContainsKey(childId) && _nodeDataById.ContainsKey(parentId))
                    {
                        var cluster = _clusterById[childId];
                        cluster.LinkId = parentId;
                        _clusterById[childId] = cluster;
                    }
                }
            }

            // 构建场景层级并产出 InterchangeScene
            BuildSceneHierarchy();
        }

        // =================================================================================
        //  场景层级构建
        // =================================================================================

        private void BuildSceneHierarchy()
        {
            if (Scene == null) return;

            // 1) 添加材质到场景
            foreach (var kvp in _materialById)
            {
                var fbxMat = kvp.Value;
                var mat = new InterchangeMaterial
                {
                    Name = string.IsNullOrEmpty(fbxMat.Name) ? $"Material_{kvp.Key}" : fbxMat.Name,
                    DiffuseColor = fbxMat.Diffuse != null && fbxMat.Diffuse.Length >= 3
                        ? new Vector3(fbxMat.Diffuse[0], fbxMat.Diffuse[1], fbxMat.Diffuse[2])
                        : new Vector3(0.8f, 0.8f, 0.8f),
                    EmissiveColor = fbxMat.Emissive != null && fbxMat.Emissive.Length >= 3
                        ? new Vector3(fbxMat.Emissive[0], fbxMat.Emissive[1], fbxMat.Emissive[2])
                        : Vector3.Zero,
                    Roughness = fbxMat.ShadingModel == "Phong"
                        ? MathF.Sqrt(2f / (fbxMat.Shininess + 2f)) // Ns -> Roughness 转换
                        : 0.5f,
                    Opacity = 1f - fbxMat.Transparency,
                    ShadingModel = fbxMat.ShadingModel
                };
                Scene.Materials.Add(mat);
            }

            // 2) 转换网格
            var meshIdToIndex = new Dictionary<long, int>();
            foreach (var kvp in _meshById)
            {
                var fbxMesh = kvp.Value;
                var mesh = ConvertFbxMeshToInterchange(fbxMesh);
                int meshIndex = Scene.Meshes.Count;
                Scene.Meshes.Add(mesh);
                meshIdToIndex[kvp.Key] = meshIndex;
            }

            // 3) 转换骨骼
            var boneIdToIndex = new Dictionary<long, int>();
            ConvertSkeleton(meshIdToIndex, boneIdToIndex);

            // 4) 构建场景节点树
            // 收集根节点（parentId == null 或不存在的节点）
            var processedIds = new HashSet<long>();
            var nodeIdToIndex = new Dictionary<long, int>();

            foreach (var kvp in _nodeDataById)
            {
                if (!kvp.Value.ParentId.HasValue ||
                    !_nodeDataById.ContainsKey(kvp.Value.ParentId.Value))
                {
                    var interchangeNode = ConvertNodeData(kvp.Value, meshIdToIndex, boneIdToIndex);
                    int index = Scene.RootNodes.Count;
                    Scene.RootNodes.Add(interchangeNode);
                    nodeIdToIndex[kvp.Key] = index;
                    processedIds.Add(kvp.Key);
                }
            }

            // 处理子节点
            foreach (var kvp in _nodeDataById)
            {
                if (processedIds.Contains(kvp.Key)) continue;
                if (!kvp.Value.ParentId.HasValue) continue;

                var parentNode = FindNodeById(kvp.Value.ParentId.Value);
                if (parentNode != null)
                {
                    var interchangeNode = ConvertNodeData(kvp.Value, meshIdToIndex, boneIdToIndex);
                    parentNode.Children.Add(interchangeNode);
                }
            }
        }

        private InterchangeNode? FindNodeById(long id)
        {
            return FindNodeRecursive(Scene!.RootNodes, id);
        }

        private InterchangeNode? FindNodeRecursive(List<InterchangeNode> nodes, long id)
        {
            foreach (var node in nodes)
            {
                // 通过元数据存储原始 ID
                if (node.Metadata.TryGetValue("FbxId", out var idStr) && idStr == id.ToString())
                    return node;
                var found = FindNodeRecursive(node.Children, id);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>将 FBX 网格数据转换为 InterchangeMesh。</summary>
        private InterchangeMesh ConvertFbxMeshToInterchange(FbxMeshData fbxMesh)
        {
            var mesh = new InterchangeMesh
            {
                Name = string.IsNullOrEmpty(fbxMesh.Name) ? "Mesh" : fbxMesh.Name
            };

            // 解析顶点位置
            if (fbxMesh.Vertices == null || fbxMesh.PolygonVertexIndex == null)
                return mesh;

            // 计算三角形索引
            int polyCount = fbxMesh.PolygonVertexIndex.Length;
            var polygonSizes = new List<int>();
            var polygonVertexIds = new List<int>();

            int vi = 0;
            while (vi < polyCount)
            {
                int count = 0;
                // 负索引表示多边形结束
                while (vi < polyCount)
                {
                    int idx = fbxMesh.PolygonVertexIndex[vi];
                    if (idx < 0)
                    {
                        polygonSizes.Add(count + 1);
                        polygonVertexIds.Add((-idx) - 1); // 取绝对值然后 -1
                        vi++;
                        break;
                    }
                    polygonVertexIds.Add(idx);
                    count++;
                    vi++;
                }
            }

            // 构建顶点映射（去重）
            var vertMap = new Dictionary<int, int>(); // FBX 顶点索引 -> 去重后索引
            for (int i = 0; i < polygonVertexIds.Count; i++)
            {
                int fbxIdx = polygonVertexIds[i];
                if (!vertMap.ContainsKey(fbxIdx))
                {
                    int newIdx = mesh.Vertices.Count;
                    vertMap[fbxIdx] = newIdx;

                    // 添加顶点位置
                    if (fbxMesh.Vertices != null && fbxIdx * 3 + 2 < fbxMesh.Vertices.Length)
                    {
                        mesh.Vertices.Add(new Vector3(
                            fbxMesh.Vertices[fbxIdx * 3],
                            fbxMesh.Vertices[fbxIdx * 3 + 1],
                            fbxMesh.Vertices[fbxIdx * 3 + 2]
                        ));
                    }
                }
            }

            // 构建三角形索引
            int vertCounter = 0;
            foreach (int polySize in polygonSizes)
            {
                // 扇形三角化
                int firstVert = vertMap[polygonVertexIds[vertCounter]];
                int prevVert = vertMap[polygonVertexIds[vertCounter + 1]];

                for (int i = 2; i < polySize; i++)
                {
                    int nextVert = vertMap[polygonVertexIds[vertCounter + i]];
                    mesh.Indices.Add(firstVert);
                    mesh.Indices.Add(prevVert);
                    mesh.Indices.Add(nextVert);
                    prevVert = nextVert;
                }

                vertCounter += polySize;
            }

            // 法线（ByPolygonVertex 模式）
            if (fbxMesh.Normals != null && fbxMesh.Normals.Length > 0)
            {
                if (fbxMesh.NormalMappingMode == 0) // ByControlPoint
                {
                    for (int i = 0; i < mesh.Vertices.Count; i++)
                    {
                        mesh.Normals.Add(new Vector3(
                            fbxMesh.Normals[i * 3],
                            fbxMesh.Normals[i * 3 + 1],
                            fbxMesh.Normals[i * 3 + 2]
                        ));
                    }
                }
                else // ByPolygonVertex — 匹配索引
                {
                    for (int i = 0; i < polygonVertexIds.Count; i++)
                    {
                        if (i * 3 + 2 < fbxMesh.Normals.Length && vertMap.TryGetValue(polygonVertexIds[i], out int idx))
                        {
                            while (mesh.Normals.Count <= idx)
                                mesh.Normals.Add(Vector3.UnitY);
                            mesh.Normals[idx] = new Vector3(
                                fbxMesh.Normals[i * 3],
                                fbxMesh.Normals[i * 3 + 1],
                                fbxMesh.Normals[i * 3 + 2]
                            );
                        }
                    }
                }
            }

            // UV
            if (fbxMesh.UVs != null && fbxMesh.UVs.Length > 0)
            {
                for (int i = 0; i < polygonVertexIds.Count; i++)
                {
                    if (i * 2 + 1 < fbxMesh.UVs.Length && vertMap.TryGetValue(polygonVertexIds[i], out int idx))
                    {
                        while (mesh.UV0.Count <= idx)
                            mesh.UV0.Add(Vector2.Zero);
                        mesh.UV0[idx] = new Vector2(
                            fbxMesh.UVs[i * 2],
                            fbxMesh.UVs[i * 2 + 1]
                        );
                    }
                }
            }

            return mesh;
        }

        /// <summary>转换骨骼 / 蒙皮数据。</summary>
        private void ConvertSkeleton(Dictionary<long, int> meshIdToIndex, Dictionary<long, int> boneIdToIndex)
        {
            if (Scene == null) return;

            foreach (var skinKvp in _skinById)
            {
                var skin = skinKvp.Value;
                if (!skin.GeometryId.HasValue) continue;

                // 找到对应的网格
                if (!meshIdToIndex.TryGetValue(skin.GeometryId.Value, out int meshIndex)) continue;
                var mesh = Scene.Meshes[meshIndex];

                // 收集集群中的骨骼数据
                foreach (long clusterId in skin.ClusterIds)
                {
                    if (!_clusterById.TryGetValue(clusterId, out var cluster)) continue;
                    if (!cluster.LinkId.HasValue) continue;

                    // 创建骨骼
                    if (!boneIdToIndex.ContainsKey(cluster.LinkId.Value))
                    {
                        var bone = new InterchangeBone
                        {
                            Name = _nodeDataById.TryGetValue(cluster.LinkId.Value, out var nodeData)
                                ? nodeData.Name
                                : $"Bone_{cluster.LinkId.Value}",
                            ParentIndex = -1,
                            BindPoseLocal = Matrix4x4.Identity,
                            InverseBindPose = Matrix4x4.Identity
                        };

                        // 设置变换矩阵
                        if (cluster.TransformLink != null && cluster.TransformLink.Length >= 16)
                        {
                            bone.InverseBindPose = new Matrix4x4(
                                (float)cluster.TransformLink[0], (float)cluster.TransformLink[1],
                                (float)cluster.TransformLink[2], (float)cluster.TransformLink[3],
                                (float)cluster.TransformLink[4], (float)cluster.TransformLink[5],
                                (float)cluster.TransformLink[6], (float)cluster.TransformLink[7],
                                (float)cluster.TransformLink[8], (float)cluster.TransformLink[9],
                                (float)cluster.TransformLink[10], (float)cluster.TransformLink[11],
                                (float)cluster.TransformLink[12], (float)cluster.TransformLink[13],
                                (float)cluster.TransformLink[14], (float)cluster.TransformLink[15]
                            );
                        }

                        if (cluster.Transform != null && cluster.Transform.Length >= 16)
                        {
                            bone.BindPoseLocal = new Matrix4x4(
                                (float)cluster.Transform[0], (float)cluster.Transform[1],
                                (float)cluster.Transform[2], (float)cluster.Transform[3],
                                (float)cluster.Transform[4], (float)cluster.Transform[5],
                                (float)cluster.Transform[6], (float)cluster.Transform[7],
                                (float)cluster.Transform[8], (float)cluster.Transform[9],
                                (float)cluster.Transform[10], (float)cluster.Transform[11],
                                (float)cluster.Transform[12], (float)cluster.Transform[13],
                                (float)cluster.Transform[14], (float)cluster.Transform[15]
                            );
                        }

                        boneIdToIndex[cluster.LinkId.Value] = Scene.Bones.Count;
                        Scene.Bones.Add(bone);
                    }

                    // 添加顶点权重
                    int boneIdx = boneIdToIndex[cluster.LinkId.Value];
                    for (int i = 0; i < cluster.Indices.Count && i < cluster.Weights.Count; i++)
                    {
                        int vertIdx = cluster.Indices[i];
                        float weight = (float)cluster.Weights[i];

                        // 确保顶点权重数组足够大
                        while (mesh.BoneWeights.Count <= vertIdx)
                            mesh.BoneWeights.Add(new InterchangeVertexWeight());

                        var bw = mesh.BoneWeights[vertIdx];
                        // 填充到第一个空权重槽位
                        if (bw.Weight0 <= 0) { bw.BoneIndex0 = boneIdx; bw.Weight0 = weight; }
                        else if (bw.Weight1 <= 0) { bw.BoneIndex1 = boneIdx; bw.Weight1 = weight; }
                        else if (bw.Weight2 <= 0) { bw.BoneIndex2 = boneIdx; bw.Weight2 = weight; }
                        else if (bw.Weight3 <= 0) { bw.BoneIndex3 = boneIdx; bw.Weight3 = weight; }
                        mesh.BoneWeights[vertIdx] = bw.Normalize();
                    }
                }
            }
        }

        /// <summary>转换 FBX 节点数据为 InterchangeNode。</summary>
        private InterchangeNode ConvertNodeData(FbxNodeData nodeData,
            Dictionary<long, int> meshIdToIndex, Dictionary<long, int> boneIdToIndex)
        {
            var node = new InterchangeNode
            {
                Name = string.IsNullOrEmpty(nodeData.Name) ? "Node" : nodeData.Name
            };
            node.Metadata["FbxId"] = nodeData.Id.ToString();

            // 计算局部变换矩阵
            Vector3 trans = nodeData.Translation != null && nodeData.Translation.Length >= 3
                ? new Vector3((float)nodeData.Translation[0], (float)nodeData.Translation[1], (float)nodeData.Translation[2])
                : Vector3.Zero;

            Vector3 scale = nodeData.Scale != null && nodeData.Scale.Length >= 3
                ? new Vector3((float)nodeData.Scale[0], (float)nodeData.Scale[1], (float)nodeData.Scale[2])
                : Vector3.One;

            Quaternion rot = Quaternion.Identity;
            if (nodeData.Rotation != null && nodeData.Rotation.Length >= 3)
            {
                // FBX 使用欧拉角 (ZXY 顺序)
                float rx = (float)(nodeData.Rotation[0] * Math.PI / 180.0);
                float ry = (float)(nodeData.Rotation[1] * Math.PI / 180.0);
                float rz = (float)(nodeData.Rotation[2] * Math.PI / 180.0);
                rot = Quaternion.CreateFromYawPitchRoll(ry, rx, rz); // 注意顺序
            }

            node.LocalTransform =
                Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateFromQuaternion(rot) *
                Matrix4x4.CreateTranslation(trans);

            // 网格索引
            if (nodeData.AttributeId.HasValue && meshIdToIndex.TryGetValue(nodeData.AttributeId.Value, out int meshIdx))
                node.MeshIndex = meshIdx;

            // 骨骼索引
            if (boneIdToIndex.TryGetValue(nodeData.Id, out int boneIdx))
                node.BoneIndex = boneIdx;

            return node;
        }

        // =================================================================================
        //  动画提取
        // =================================================================================

        private void ExtractAnimations(FbxNode root)
        {
            // 简化版本：仅提取 AnimationStack / AnimationLayer 结构
            // 完整的动画提取需要解析 Curves 对属性的连接关系

            // TODO: 实现完整的 FBX 动画导入
            // 当前骨架已支持 AnimationStack → AnimationLayer → AnimationCurveNode → AnimationCurve 解析
        }

        // =================================================================================
        //  全局设置提取
        // =================================================================================

        private void ExtractGlobalSettings(FbxNode root)
        {
            var globalSettings = root.FindChild("GlobalSettings");
            if (globalSettings == null) return;

            var props70 = globalSettings.FindChild("Properties70");
            if (props70 == null) return;

            foreach (var prop in props70.Children)
            {
                string paramName = prop.Properties.Count > 0 ? prop.Properties[0]?.ToString() ?? "" : "";

                switch (paramName)
                {
                    case "UnitScaleFactor":
                        // 单位缩放因子（默认 1 = 厘米）
                        break;
                    case "UpAxis":
                        // Up axis (0=Y, 1=Z)
                        break;
                }
            }
        }

        // =================================================================================
        //  后处理
        // =================================================================================

        private void PostProcessScene()
        {
            if (Scene == null) return;

            // 对于没有材质的网格添加默认材质
            if (Scene.Materials.Count == 0 && Scene.Meshes.Count > 0)
            {
                var defaultMat = new InterchangeMaterial
                {
                    Name = "DefaultMaterial",
                    DiffuseColor = new Vector3(0.8f, 0.8f, 0.8f)
                };
                Scene.Materials.Add(defaultMat);

                foreach (var mesh in Scene.Meshes)
                    mesh.MaterialIndices.Add(0);
            }

            Scene.ImportMessages.AddRange(_messages);
        }
    }

    // =================================================================================
    //  FbxImporter 工厂方法
    // =================================================================================

    /// <summary>FBX 导入器的便捷静态入口。</summary>
    public static class FbxImporterFactory
    {
        /// <summary>导入 FBX 文件。</summary>
        public static InterchangeScene Import(string filePath)
        {
            var importer = new FbxImporter(filePath);
            return importer.Import();
        }
    }
}
