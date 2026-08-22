// =====================================================================================
//  ShanyuEngine - PLYImporter：PLY 多边形文件格式导入器
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  纯 C# 实现，支持 PLY（Polygon File Format / Stanford Triangle Format）：
//    - PLY 头解析（format / version / element / property / comment / obj_info）
//    - 元素类型（vertex / face / edge / 列表属性）
//    - 属性类型（char / uchar / short / ushort / int / uint / float / double）
//    - ASCII 格式读取（逐行解析）
//    - Binary 格式读取（小端 / 大端）
//    - 顶点属性（x/y/z / red/green/blue/alpha / normal_x/y/z / s/t/uv）
//    - 面属性（vertex_indices / vertex_index — 三角形 / 多边形）
//    - 多边形三角化
//    - 导入选项（颜色处理 / 法线处理 / UV 处理）
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  导入选项
    // =================================================================================

    /// <summary>PLY 导入选项。</summary>
    public sealed class PlyImportOptions
    {
        /// <summary>全局缩放比例。</summary>
        public float Scale = 1.0f;

        /// <summary>是否导入顶点颜色。</summary>
        public bool ImportColors = true;

        /// <summary>是否导入法线。</summary>
        public bool ImportNormals = true;

        /// <summary>是否导入 UV 坐标。</summary>
        public bool ImportUVs = true;

        /// <summary>缺少法线时是否自动计算。</summary>
        public bool GenerateNormals = true;

        /// <summary>颜色缩放因子（8 位颜色通常为 1/255）。</summary>
        public float ColorScale = 1f / 255f;
    }

    // =================================================================================
    //  PLY 格式枚举
    // =================================================================================

    /// <summary>PLY 格式类型。</summary>
    internal enum PlyFormat
    {
        Ascii,
        BinaryLittleEndian,
        BinaryBigEndian
    }

    /// <summary>PLY 属性数据类型。</summary>
    internal enum PlyDataType
    {
        Char,    // int8
        UChar,   // uint8
        Short,   // int16
        UShort,  // uint16
        Int,     // int32
        UInt,    // uint32
        Float,   // float32
        Double,  // float64
        List     // 列表类型
    }

    // =================================================================================
    //  PLY 头部数据结构
    // =================================================================================

    /// <summary>PLY 属性定义。</summary>
    internal sealed class PlyProperty
    {
        public string Name = string.Empty;
        public PlyDataType Type;
        // 列表属性专用
        public PlyDataType ListCountType;
        public PlyDataType ListElementType;
        public bool IsList;
    }

    /// <summary>PLY 元素定义。</summary>
    internal sealed class PlyElement
    {
        public string Name = string.Empty;
        public int Count;
        public List<PlyProperty> Properties = new();
    }

    /// <summary>PLY 顶点数据。</summary>
    internal struct PlyVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public Vector4 Color;
        public bool HasPosition;
        public bool HasNormal;
        public bool HasUV;
        public bool HasColor;
    }

    /// <summary>PLY 面数据。</summary>
    internal struct PlyFace
    {
        public int[] Indices;
    }

    // =================================================================================
    //  PLYImporter —— 主导入器
    // =================================================================================

    /// <summary>
    /// PLY 多边形文件格式导入器。支持 ASCII 和 Binary（小端/大端）格式。
    /// 输出为 InterchangeScene 统一格式。
    /// </summary>
    public sealed class PLYImporter
    {
        private readonly string _filePath;
        private readonly List<string> _messages = new();
        private PlyImportOptions _options = new();

        // PLY 头部信息
        private PlyFormat _format = PlyFormat.Ascii;
        private string _version = "1.0";
        private readonly List<PlyElement> _elements = new();
        private readonly List<string> _comments = new();

        /// <summary>解析产出的统一场景格式。</summary>
        public InterchangeScene? Scene { get; private set; }

        /// <summary>构造 PLY 导入器。</summary>
        public PLYImporter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>使用选项执行导入。</summary>
        public InterchangeScene Import(PlyImportOptions? options = null)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException($"PLY 源文件不存在: {_filePath}");

            _options = options ?? new PlyImportOptions();

            Scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(_filePath),
                SourceFilePath = _filePath,
                SourceFormat = "PLY"
            };

            try
            {
                // 读取文件全部字节（Binary 模式需要）
                byte[] fileData = File.ReadAllBytes(_filePath);

                // 解析头部
                int dataOffset = ParseHeader(fileData);

                _messages.Add($"PLY 格式: {_format}, 版本: {_version}");
                foreach (var elem in _elements)
                    _messages.Add($"  元素 {elem.Name}: {elem.Count} 个, {elem.Properties.Count} 属性");

                // 解析数据
                if (_format == PlyFormat.Ascii)
                {
                    ParseAsciiData(fileData, dataOffset);
                }
                else
                {
                    ParseBinaryData(fileData, dataOffset);
                }

                // 构建网格
                BuildMesh();

                PostProcessScene();
            }
            catch (Exception ex)
            {
                Scene.ImportMessages.Add($"导入 PLY 失败: {ex.Message}");
                throw;
            }

            Scene.ImportMessages.AddRange(_messages);
            return Scene;
        }

        // =================================================================================
        //  头部解析
        // =================================================================================

        /// <summary>解析 PLY 头部，返回数据起始偏移量。</summary>
        private int ParseHeader(byte[] fileData)
        {
            // 找到头部结束标记 "end_header\n"
            string headerText;
            int endOffset;

            // 将字节转为文本查找头部
            int lineEnd = FindHeaderEnd(fileData, out headerText, out endOffset);

            string[] lines = headerText.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0 || !lines[0].Trim().Equals("ply", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("无效的 PLY 文件：缺少 'ply' 标记");

            PlyElement? currentElement = null;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                switch (parts[0].ToLowerInvariant())
                {
                    case "format":
                        if (parts.Length >= 2)
                        {
                            _format = parts[1] switch
                            {
                                "ascii" => PlyFormat.Ascii,
                                "binary_little_endian" => PlyFormat.BinaryLittleEndian,
                                "binary_big_endian" => PlyFormat.BinaryBigEndian,
                                _ => PlyFormat.Ascii
                            };
                        }
                        if (parts.Length >= 3)
                            _version = parts[2];
                        break;

                    case "comment":
                        _comments.Add(line.Substring(7).Trim());
                        break;

                    case "obj_info":
                        // 对象信息（忽略）
                        break;

                    case "element":
                        if (parts.Length >= 3)
                        {
                            currentElement = new PlyElement
                            {
                                Name = parts[1],
                                Count = int.Parse(parts[2], CultureInfo.InvariantCulture)
                            };
                            _elements.Add(currentElement);
                        }
                        break;

                    case "property":
                        if (currentElement == null) break;

                        var prop = new PlyProperty();

                        if (parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
                        {
                            // 列表属性：property list <count_type> <element_type> <name>
                            prop.IsList = true;
                            if (parts.Length >= 5)
                            {
                                prop.ListCountType = ParseDataType(parts[2]);
                                prop.ListElementType = ParseDataType(parts[3]);
                                prop.Name = parts[4];
                            }
                        }
                        else
                        {
                            // 标量属性：property <type> <name>
                            if (parts.Length >= 3)
                            {
                                prop.Type = ParseDataType(parts[1]);
                                prop.Name = parts[2];
                            }
                        }

                        currentElement.Properties.Add(prop);
                        break;

                    case "end_header":
                        break;
                }
            }

            return endOffset;
        }

        /// <summary>查找 PLY 头部结束位置。</summary>
        private int FindHeaderEnd(byte[] data, out string headerText, out int endOffset)
        {
            // 查找 "end_header" 行
            var sb = new StringBuilder();
            int i = 0;
            while (i < data.Length)
            {
                // 读取一行
                int lineStart = i;
                while (i < data.Length && data[i] != '\n') i++;
                int lineLen = i - lineStart;
                if (i < data.Length) i++; // 跳过 \n

                string line = Encoding.ASCII.GetString(data, lineStart, lineLen).TrimEnd('\r');
                sb.AppendLine(line);

                if (line.Equals("end_header", StringComparison.OrdinalIgnoreCase))
                {
                    headerText = sb.ToString();
                    endOffset = i;
                    return endOffset;
                }
            }

            headerText = sb.ToString();
            endOffset = data.Length;
            return endOffset;
        }

        private static PlyDataType ParseDataType(string s)
        {
            return s.ToLowerInvariant() switch
            {
                "char" or "int8" => PlyDataType.Char,
                "uchar" or "uint8" => PlyDataType.UChar,
                "short" or "int16" => PlyDataType.Short,
                "ushort" or "uint16" => PlyDataType.UShort,
                "int" or "int32" => PlyDataType.Int,
                "uint" or "uint32" => PlyDataType.UInt,
                "float" or "float32" => PlyDataType.Float,
                "double" or "float64" => PlyDataType.Double,
                _ => PlyDataType.Float
            };
        }

        // =================================================================================
        //  ASCII 数据解析
        // =================================================================================

        private void ParseAsciiData(byte[] fileData, int dataOffset)
        {
            string allText = Encoding.ASCII.GetString(fileData, dataOffset, fileData.Length - dataOffset);
            string[] lines = allText.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            int lineIdx = 0;

            foreach (var element in _elements)
            {
                if (element.Name == "vertex")
                {
                    _vertices = new List<PlyVertex>(element.Count);
                    for (int i = 0; i < element.Count && lineIdx < lines.Length; i++, lineIdx++)
                    {
                        _vertices.Add(ParseAsciiVertex(lines[lineIdx], element));
                    }
                }
                else if (element.Name == "face")
                {
                    _faces = new List<PlyFace>(element.Count);
                    for (int i = 0; i < element.Count && lineIdx < lines.Length; i++, lineIdx++)
                    {
                        _faces.Add(ParseAsciiFace(lines[lineIdx], element));
                    }
                }
                else
                {
                    // 跳过其他元素
                    lineIdx += element.Count;
                }
            }
        }

        private List<PlyVertex> _vertices = new();
        private List<PlyFace> _faces = new();

        private PlyVertex ParseAsciiVertex(string line, PlyElement element)
        {
            var parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var vertex = new PlyVertex();

            int partIdx = 0;
            foreach (var prop in element.Properties)
            {
                if (partIdx >= parts.Length) break;

                if (prop.IsList)
                {
                    // 列表属性（顶点一般不使用）
                    int count = (int)ParseValue(parts[partIdx++], prop.ListCountType);
                    for (int i = 0; i < count && partIdx < parts.Length; i++)
                        partIdx++; // 跳过列表元素
                    continue;
                }

                float value = (float)ParseValue(parts[partIdx], prop.Type);
                partIdx++;

                switch (prop.Name.ToLowerInvariant())
                {
                    case "x":
                        vertex.Position = new Vector3(value, vertex.Position.Y, vertex.Position.Z);
                        vertex.HasPosition = true;
                        break;
                    case "y":
                        vertex.Position = new Vector3(vertex.Position.X, value, vertex.Position.Z);
                        break;
                    case "z":
                        vertex.Position = new Vector3(vertex.Position.X, vertex.Position.Y, value);
                        break;
                    case "nx":
                    case "normal_x":
                        vertex.Normal = new Vector3(value, vertex.Normal.Y, vertex.Normal.Z);
                        vertex.HasNormal = true;
                        break;
                    case "ny":
                    case "normal_y":
                        vertex.Normal = new Vector3(vertex.Normal.X, value, vertex.Normal.Z);
                        break;
                    case "nz":
                    case "normal_z":
                        vertex.Normal = new Vector3(vertex.Normal.X, vertex.Normal.Y, value);
                        break;
                    case "s":
                    case "u":
                    case "texture_u":
                        vertex.UV = new Vector2(value, vertex.UV.Y);
                        vertex.HasUV = true;
                        break;
                    case "t":
                    case "v":
                    case "texture_v":
                        vertex.UV = new Vector2(vertex.UV.X, value);
                        break;
                    case "red":
                    case "r":
                        vertex.Color = new Vector4(value * _options.ColorScale, vertex.Color.Y, vertex.Color.Z, vertex.Color.W);
                        vertex.HasColor = true;
                        break;
                    case "green":
                    case "g":
                        vertex.Color = new Vector4(vertex.Color.X, value * _options.ColorScale, vertex.Color.Z, vertex.Color.W);
                        break;
                    case "blue":
                    case "b":
                        vertex.Color = new Vector4(vertex.Color.X, vertex.Color.Y, value * _options.ColorScale, vertex.Color.W);
                        break;
                    case "alpha":
                    case "a":
                        vertex.Color = new Vector4(vertex.Color.X, vertex.Color.Y, vertex.Color.Z, value * _options.ColorScale);
                        break;
                }
            }

            vertex.Position *= _options.Scale;
            return vertex;
        }

        private PlyFace ParseAsciiFace(string line, PlyElement element)
        {
            var parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var face = new PlyFace();

            // 面通常使用列表属性存储索引
            // 格式: count idx0 idx1 idx2 ...
            if (parts.Length > 0)
            {
                int count = int.Parse(parts[0], CultureInfo.InvariantCulture);
                int actualCount = Math.Min(count, parts.Length - 1);
                face.Indices = new int[actualCount];

                for (int i = 0; i < actualCount; i++)
                    face.Indices[i] = int.Parse(parts[i + 1], CultureInfo.InvariantCulture);
            }

            return face;
        }

        private static double ParseValue(string s, PlyDataType type)
        {
            return type switch
            {
                PlyDataType.Float => double.Parse(s, CultureInfo.InvariantCulture),
                PlyDataType.Double => double.Parse(s, CultureInfo.InvariantCulture),
                PlyDataType.Int => int.Parse(s, CultureInfo.InvariantCulture),
                PlyDataType.UInt => uint.Parse(s, CultureInfo.InvariantCulture),
                PlyDataType.Short => short.Parse(s, CultureInfo.InvariantCulture),
                PlyDataType.UShort => ushort.Parse(s, CultureInfo.InvariantCulture),
                PlyDataType.Char => sbyte.Parse(s, CultureInfo.InvariantCulture),
                PlyDataType.UChar => byte.Parse(s, CultureInfo.InvariantCulture),
                _ => 0
            };
        }

        // =================================================================================
        //  Binary 数据解析
        // =================================================================================

        private void ParseBinaryData(byte[] data, int offset)
        {
            bool isLittleEndian = _format == PlyFormat.BinaryLittleEndian;

            foreach (var element in _elements)
            {
                if (element.Name == "vertex")
                {
                    _vertices = new List<PlyVertex>(element.Count);
                    for (int i = 0; i < element.Count; i++)
                    {
                        var (vertex, newOffset) = ReadBinaryVertex(data, offset, element, isLittleEndian);
                        _vertices.Add(vertex);
                        offset = newOffset;
                    }
                }
                else if (element.Name == "face")
                {
                    _faces = new List<PlyFace>(element.Count);
                    for (int i = 0; i < element.Count; i++)
                    {
                        var (face, newOffset) = ReadBinaryFace(data, offset, element, isLittleEndian);
                        _faces.Add(face);
                        offset = newOffset;
                    }
                }
                else
                {
                    // 跳过其他元素
                    for (int i = 0; i < element.Count; i++)
                        offset += GetElementSize(element, isLittleEndian, data, offset);
                }
            }
        }

        private (PlyVertex, int) ReadBinaryVertex(byte[] data, int offset, PlyElement element, bool isLittleEndian)
        {
            var vertex = new PlyVertex();

            foreach (var prop in element.Properties)
            {
                if (prop.IsList)
                {
                    // 列表属性：读取计数值，然后跳过元素
                    int count = (int)ReadBinaryValue(data, ref offset, prop.ListCountType, isLittleEndian);
                    for (int i = 0; i < count; i++)
                        ReadBinaryValue(data, ref offset, prop.ListElementType, isLittleEndian);
                    continue;
                }

                double value = ReadBinaryValue(data, ref offset, prop.Type, isLittleEndian);

                switch (prop.Name.ToLowerInvariant())
                {
                    case "x":
                        vertex.Position = new Vector3((float)value, vertex.Position.Y, vertex.Position.Z);
                        vertex.HasPosition = true;
                        break;
                    case "y":
                        vertex.Position = new Vector3(vertex.Position.X, (float)value, vertex.Position.Z);
                        break;
                    case "z":
                        vertex.Position = new Vector3(vertex.Position.X, vertex.Position.Y, (float)value);
                        break;
                    case "nx":
                    case "normal_x":
                        vertex.Normal = new Vector3((float)value, vertex.Normal.Y, vertex.Normal.Z);
                        vertex.HasNormal = true;
                        break;
                    case "ny":
                    case "normal_y":
                        vertex.Normal = new Vector3(vertex.Normal.X, (float)value, vertex.Normal.Z);
                        break;
                    case "nz":
                    case "normal_z":
                        vertex.Normal = new Vector3(vertex.Normal.X, vertex.Normal.Y, (float)value);
                        break;
                    case "s":
                    case "u":
                    case "texture_u":
                        vertex.UV = new Vector2((float)value, vertex.UV.Y);
                        vertex.HasUV = true;
                        break;
                    case "t":
                    case "v":
                    case "texture_v":
                        vertex.UV = new Vector2(vertex.UV.X, (float)value);
                        break;
                    case "red":
                    case "r":
                        vertex.Color = new Vector4((float)(value * _options.ColorScale), vertex.Color.Y, vertex.Color.Z, vertex.Color.W);
                        vertex.HasColor = true;
                        break;
                    case "green":
                    case "g":
                        vertex.Color = new Vector4(vertex.Color.X, (float)(value * _options.ColorScale), vertex.Color.Z, vertex.Color.W);
                        break;
                    case "blue":
                    case "b":
                        vertex.Color = new Vector4(vertex.Color.X, vertex.Color.Y, (float)(value * _options.ColorScale), vertex.Color.W);
                        break;
                    case "alpha":
                    case "a":
                        vertex.Color = new Vector4(vertex.Color.X, vertex.Color.Y, vertex.Color.Z, (float)(value * _options.ColorScale));
                        break;
                }
            }

            vertex.Position *= _options.Scale;
            return (vertex, offset);
        }

        private (PlyFace, int) ReadBinaryFace(byte[] data, int offset, PlyElement element, bool isLittleEndian)
        {
            var face = new PlyFace();

            foreach (var prop in element.Properties)
            {
                if (prop.IsList)
                {
                    // 面索引列表
                    int count = (int)ReadBinaryValue(data, ref offset, prop.ListCountType, isLittleEndian);
                    face.Indices = new int[count];
                    for (int i = 0; i < count; i++)
                        face.Indices[i] = (int)ReadBinaryValue(data, ref offset, prop.ListElementType, isLittleEndian);
                }
                else
                {
                    // 跳过非列表属性
                    ReadBinaryValue(data, ref offset, prop.Type, isLittleEndian);
                }
            }

            return (face, offset);
        }

        /// <summary>读取二进制值。</summary>
        private static double ReadBinaryValue(byte[] data, ref int offset, PlyDataType type, bool isLittleEndian)
        {
            double result = 0;

            switch (type)
            {
                case PlyDataType.Char:
                    result = (sbyte)data[offset]; offset += 1; break;
                case PlyDataType.UChar:
                    result = data[offset]; offset += 1; break;
                case PlyDataType.Short:
                    {
                        short val = isLittleEndian
                            ? (short)(data[offset] | (data[offset + 1] << 8))
                            : (short)((data[offset] << 8) | data[offset + 1]);
                        result = val; offset += 2; break;
                    }
                case PlyDataType.UShort:
                    {
                        ushort val = isLittleEndian
                            ? (ushort)(data[offset] | (data[offset + 1] << 8))
                            : (ushort)((data[offset] << 8) | data[offset + 1]);
                        result = val; offset += 2; break;
                    }
                case PlyDataType.Int:
                    {
                        int val = isLittleEndian
                            ? data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24)
                            : (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
                        result = val; offset += 4; break;
                    }
                case PlyDataType.UInt:
                    {
                        uint val = isLittleEndian
                            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
                            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
                        result = val; offset += 4; break;
                    }
                case PlyDataType.Float:
                    {
                        if (isLittleEndian)
                        {
                            result = BitConverter.ToSingle(data, offset);
                        }
                        else
                        {
                            byte[] temp = { data[offset + 3], data[offset + 2], data[offset + 1], data[offset] };
                            result = BitConverter.ToSingle(temp, 0);
                        }
                        offset += 4; break;
                    }
                case PlyDataType.Double:
                    {
                        if (isLittleEndian)
                        {
                            result = BitConverter.ToDouble(data, offset);
                        }
                        else
                        {
                            byte[] temp = new byte[8];
                            for (int j = 0; j < 8; j++)
                                temp[j] = data[offset + 7 - j];
                            result = BitConverter.ToDouble(temp, 0);
                        }
                        offset += 8; break;
                    }
            }

            return result;
        }

        /// <summary>获取元素大小（用于跳过未知元素）。</summary>
        private int GetElementSize(PlyElement element, bool isLittleEndian, byte[] data, int offset)
        {
            // 对于变长元素（含列表属性），需要实际读取来确定大小
            int startOffset = offset;
            foreach (var prop in element.Properties)
            {
                if (prop.IsList)
                {
                    int count = (int)ReadBinaryValue(data, ref offset, prop.ListCountType, isLittleEndian);
                    for (int i = 0; i < count; i++)
                        ReadBinaryValue(data, ref offset, prop.ListElementType, isLittleEndian);
                }
                else
                {
                    ReadBinaryValue(data, ref offset, prop.Type, isLittleEndian);
                }
            }
            return offset - startOffset;
        }

        // =================================================================================
        //  构建网格
        // =================================================================================

        private void BuildMesh()
        {
            var mesh = new InterchangeMesh { Name = Scene.Name };

            // 添加顶点
            foreach (var v in _vertices)
            {
                mesh.Vertices.Add(v.Position);

                if (_options.ImportNormals && v.HasNormal)
                    mesh.Normals.Add(v.Normal);

                if (_options.ImportUVs && v.HasUV)
                    mesh.UV0.Add(v.UV);

                if (_options.ImportColors && v.HasColor)
                    mesh.Colors.Add(v.Color);
            }

            // 添加面索引（三角化）
            foreach (var face in _faces)
            {
                if (face.Indices == null || face.Indices.Length < 3)
                    continue;

                if (face.Indices.Length == 3)
                {
                    mesh.Indices.Add(face.Indices[0]);
                    mesh.Indices.Add(face.Indices[1]);
                    mesh.Indices.Add(face.Indices[2]);
                }
                else
                {
                    // 扇形三角化
                    for (int i = 1; i < face.Indices.Length - 1; i++)
                    {
                        mesh.Indices.Add(face.Indices[0]);
                        mesh.Indices.Add(face.Indices[i]);
                        mesh.Indices.Add(face.Indices[i + 1]);
                    }
                }
            }

            // 如果没有面数据但有顶点数据，生成点云
            if (_faces.Count == 0 && _vertices.Count > 0)
            {
                _messages.Add("PLY 无面数据，生成为点云");
                for (int i = 0; i < _vertices.Count; i++)
                    mesh.Indices.Add(i);
            }

            Scene.Meshes.Add(mesh);

            // 添加默认材质
            Scene.Materials.Add(new InterchangeMaterial
            {
                Name = "DefaultMaterial",
                DiffuseColor = new Vector3(0.8f, 0.8f, 0.8f)
            });
            mesh.MaterialIndices.Add(0);

            // 创建场景节点
            Scene.RootNodes.Add(new InterchangeNode
            {
                Name = Scene.Name,
                MeshIndex = 0
            });

            _messages.Add($"PLY 网格: {mesh.Vertices.Count} 顶点, {mesh.TriangleCount} 三角形");
        }

        // =================================================================================
        //  后处理
        // =================================================================================

        private void PostProcessScene()
        {
            // 为缺少法线的网格生成法线
            if (_options.GenerateNormals)
            {
                foreach (var mesh in Scene.Meshes)
                {
                    if (mesh.Normals.Count == 0 && mesh.Vertices.Count > 0 && mesh.Indices.Count > 0)
                    {
                        GenerateNormalsForMesh(mesh);
                    }
                }
            }
        }

        private void GenerateNormalsForMesh(InterchangeMesh mesh)
        {
            mesh.Normals.Clear();
            for (int i = 0; i < mesh.Vertices.Count; i++)
                mesh.Normals.Add(Vector3.Zero);

            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int i0 = mesh.Indices[i];
                int i1 = mesh.Indices[i + 1];
                int i2 = mesh.Indices[i + 2];

                if (i0 >= mesh.Vertices.Count || i1 >= mesh.Vertices.Count || i2 >= mesh.Vertices.Count)
                    continue;

                Vector3 v0 = mesh.Vertices[i0];
                Vector3 v1 = mesh.Vertices[i1];
                Vector3 v2 = mesh.Vertices[i2];

                Vector3 normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));

                mesh.Normals[i0] += normal;
                mesh.Normals[i1] += normal;
                mesh.Normals[i2] += normal;
            }

            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                if (mesh.Normals[i] != Vector3.Zero)
                    mesh.Normals[i] = Vector3.Normalize(mesh.Normals[i]);
                else
                    mesh.Normals[i] = Vector3.UnitY;
            }
        }
    }

    // =================================================================================
    //  PLYImporter 适配器
    // =================================================================================

    /// <summary>PLY 导入器适配器（用于 AssetImportManager 注册）。</summary>
    public sealed class PLYImporterAdapter : IAssetImporter
    {
        /// <summary>支持的扩展名。</summary>
        public IEnumerable<string> SupportedExtensions => new[] { "ply" };

        /// <summary>导入 PLY 文件。</summary>
        public InterchangeScene Import(string filePath)
        {
            var importer = new PLYImporter(filePath);
            return importer.Import();
        }
    }
}
