// =====================================================================================
//  ShanyuEngine - UsdCrateParser：USD Crate (.usdc) 格式底层解析器
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  USD Crate 是 Pixar 为 USD 设计的二进制序列化格式。
//  本解析器实现简化版本：
//    - 文件头识别（"PXR-USDC" magic bytes）
//    - 分区索引解析（Sections / TOC）
//    - Fields / Path / Spec 解析
//    - Crate data section 读取
//    - 基本属性值反序列化（支持 float3, float[], int[], string, matrix4d, token 等）
//  注意：纯 C# 实现，不依赖任何外部 SDK。
//    USD Crate 格式文档参考：https://graphics.pixar.com/usd/release/crate_format.html
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  USD Crate 文件头
    // =================================================================================

    /// <summary>USD Crate 文件头信息。</summary>
    public struct UsdCrateHeader
    {
        /// <summary>Magic number (8 bytes): "PXR-USDC"</summary>
        public string Magic;
        /// <summary>版本号（小端 int32）。</summary>
        public int Version;
        /// <summary>分区表偏移（小端 int64）。</summary>
        public long TocOffset;
    }

    // =================================================================================
    //  USD Crate 分区
    // =================================================================================

    /// <summary>Crate 分区类型。</summary>
    public enum UsdCrateSectionType : byte
    {
        Unknown = 0,
        Paths = 1,         // 路径表
        Tokens = 2,        // Token 表
        Strings = 3,       // 字符串表
        Fields = 4,        // 字段数据
        Specs = 5,         // Spec 定义
        FieldIndex = 6,    // 字段索引
        Payloads = 7,      // Payload 数据
        FieldInfo = 8,     // 字段元信息
        Vector = 9,        // 向量数据
        Compressed = 10    // 压缩数据块
    }

    /// <summary>Crate 分区索引条目。</summary>
    public struct UsdCrateSection
    {
        /// <summary>分区类型。</summary>
        public UsdCrateSectionType Type;
        /// <summary>分区数据起始偏移（文件内绝对位置）。</summary>
        public long Offset;
        /// <summary>分区数据大小（字节）。</summary>
        public long Size;
    }

    // =================================================================================
    //  USD Crate 解析的中间结果
    // =================================================================================

    /// <summary>
    /// USD Crate 解析后的扁平数据表。
    /// 包含路径、Token、字符串、字段、Spec 的反序列化结果。
    /// </summary>
    public sealed class UsdCrateData
    {
        /// <summary>文件头信息。</summary>
        public UsdCrateHeader Header;

        /// <summary>路径表（每个路径对应一个 Prim 或属性的分层路径）。</summary>
        public List<string> Paths = new();

        /// <summary>Token 表（重复使用的标识符字符串）。</summary>
        public List<string> Tokens = new();

        /// <summary>字符串表（长字符串数据）。</summary>
        public List<string> Strings = new();

        /// <summary>字段条目（字段名 -> 值列表）。</summary>
        public List<UsdCrateField> Fields = new();

        /// <summary>Spec 表格（每个 Spec = 一个 Prim 或属性定义）。</summary>
        public List<UsdCrateSpec> Specs = new();

        /// <summary>字段索引（Spec -> Fields 的映射关系）。</summary>
        public List<UsdCrateFieldIndex> FieldIndices = new();
    }

    /// <summary>Crate 字段：一个命名的数据值。</summary>
    public sealed class UsdCrateField
    {
        /// <summary>字段名（Token 表中的索引）。</summary>
        public int NameTokenIndex;

        /// <summary>字段值类型。</summary>
        public string ValueType = "unknown";

        /// <summary>字段值（反序列化后的 .NET 对象）。</summary>
        public object? Value;

        public override string ToString() => $"[Field] tok={NameTokenIndex} type={ValueType} val={Value}";
    }

    /// <summary>Crate Spec：一个 Prim 或属性定义。</summary>
    public sealed class UsdCrateSpec
    {
        /// <summary>Spec 类型标识。</summary>
        public int SpecType;

        /// <summary>路径表中的索引。</summary>
        public int PathIndex;

        /// <summary>关联的字段索引列表。</summary>
        public List<int> FieldIndices = new();
    }

    /// <summary>字段索引条目（Spec -> Fields 映射）。</summary>
    public struct UsdCrateFieldIndex
    {
        /// <summary>Spec 索引（在 Specs 列表中的位置）。</summary>
        public int SpecIndex;
        /// <summary>字段索引（在 Fields 列表中的位置）。</summary>
        public int FieldIndex;
    }

    // =================================================================================
    //  UsdCrateParser：主解析器
    // =================================================================================

    /// <summary>
    /// USD Crate 二进制格式解析器（简化版）。
    /// 读取 .usdc 文件并提取路径、Token、字段和 Spec 数据。
    /// </summary>
    public sealed class UsdCrateParser : IDisposable
    {
        private readonly string _filePath;
        private BinaryReader _reader = null!;
        private Stream _stream = null!;
        private bool _disposed;

        /// <summary>解析后的 Crate 数据。</summary>
        public UsdCrateData Data { get; } = new();

        /// <summary>构造解析器。</summary>
        public UsdCrateParser(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>打开并解析 USD Crate 文件。</summary>
        public void Parse()
        {
            _stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: false);

            // 1) 读取文件头
            ReadHeader();

            // 2) 读取分区表 (TOC)
            var sections = ReadToc();

            // 3) 按顺序读取各个分区
            foreach (var section in sections)
            {
                ReadSection(section);
            }
        }

        // =================================================================================
        //  文件头读取
        // =================================================================================

        private void ReadHeader()
        {
            // 8 bytes magic
            byte[] magicBytes = _reader.ReadBytes(8);
            string magic = Encoding.ASCII.GetString(magicBytes);

            if (magic != "PXR-USDC")
                throw new InvalidDataException($"非 USD Crate 文件: magic='{magic}'");

            // 4 bytes version (little-endian int32)
            int version = _reader.ReadInt32();

            // 8 bytes TOC offset (little-endian int64)
            long tocOffset = _reader.ReadInt64();

            Data.Header = new UsdCrateHeader
            {
                Magic = magic,
                Version = version,
                TocOffset = tocOffset
            };

            // 验证版本
            if (version < 1 || version > 10)
                throw new NotSupportedException($"USD Crate 版本 {version} 不受支持");
        }

        // =================================================================================
        //  分区表 (TOC) 读取
        // =================================================================================

        private List<UsdCrateSection> ReadToc()
        {
            var sections = new List<UsdCrateSection>();

            // 跳转到 TOC 偏移
            _stream.Position = Data.Header.TocOffset;

            // TOC 格式：
            //   [0..3]: 分区数量 (int32, little-endian)
            //   每个分区: [ type(1B) | offset(8B) | size(8B) ]

            int count = _reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                byte typeByte = _reader.ReadByte();
                long offset = _reader.ReadInt64();
                long size = _reader.ReadInt64();

                sections.Add(new UsdCrateSection
                {
                    Type = (UsdCrateSectionType)typeByte,
                    Offset = offset,
                    Size = size
                });
            }

            return sections;
        }

        // =================================================================================
        //  分区读取
        // =================================================================================

        private void ReadSection(UsdCrateSection section)
        {
            // 跳转到分区起始
            _stream.Position = section.Offset;
            long sectionEnd = section.Offset + section.Size;

            switch (section.Type)
            {
                case UsdCrateSectionType.Paths:
                    ReadPathsSection(section);
                    break;
                case UsdCrateSectionType.Tokens:
                    ReadTokensSection(section);
                    break;
                case UsdCrateSectionType.Strings:
                    ReadStringsSection(section);
                    break;
                case UsdCrateSectionType.Fields:
                    ReadFieldsSection(section);
                    break;
                case UsdCrateSectionType.Specs:
                    ReadSpecsSection(section);
                    break;
                case UsdCrateSectionType.FieldIndex:
                    ReadFieldIndexSection(section);
                    break;
                case UsdCrateSectionType.FieldInfo:
                    // 字段元信息 — 简化版本跳过
                    break;
                case UsdCrateSectionType.Payloads:
                    // Payload 数据 — 简化版本跳过
                    break;
                case UsdCrateSectionType.Compressed:
                    // 压缩数据块 — 简化版本跳过
                    break;
                default:
                    // 未知分区 — 跳过
                    break;
            }

            // 确保流位置在分区结束后
            if (_stream.Position < sectionEnd)
                _stream.Position = sectionEnd;
        }

        // =================================================================================
        //  Paths 分区
        // =================================================================================

        private void ReadPathsSection(UsdCrateSection section)
        {
            // Paths 分区格式：
            //   每个路径: [ elementCount(1B) | ... 元素序列（每个元素为 token 索引）]
            long end = section.Offset + section.Size;

            while (_stream.Position < end)
            {
                byte elementCount = _reader.ReadByte();
                var pathComponents = new List<string>();

                for (int i = 0; i < elementCount; i++)
                {
                    int tokenIndex = _reader.ReadInt32(); // token 表中的索引
                    string token = tokenIndex < Data.Tokens.Count
                        ? Data.Tokens[tokenIndex]
                        : $"<tok_{tokenIndex}>";
                    pathComponents.Add(token);
                }

                string path = string.Join("/", pathComponents);
                if (path.StartsWith("/"))
                    Data.Paths.Add(path);
                else
                    Data.Paths.Add("/" + path);
            }
        }

        // =================================================================================
        //  Tokens 分区
        // =================================================================================

        private void ReadTokensSection(UsdCrateSection section)
        {
            // Tokens 分区格式：
            //   [0..3]: token 数量 (int32)
            //   每个 token: [ length(4B) | data(length bytes) ]

            int count = _reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int length = _reader.ReadInt32();
                byte[] tokenBytes = _reader.ReadBytes(length);
                string token = Encoding.UTF8.GetString(tokenBytes);
                Data.Tokens.Add(token);
            }
        }

        // =================================================================================
        //  Strings 分区
        // =================================================================================

        private void ReadStringsSection(UsdCrateSection section)
        {
            // Strings 分区格式与 Tokens 分区类似
            long end = section.Offset + section.Size;
            long pos = _stream.Position;

            // 如果还有剩余数据则读取
            while (_stream.Position < end)
            {
                if (end - _stream.Position < 4) break;
                int length = _reader.ReadInt32();
                if (length > 0 && length <= end - _stream.Position)
                {
                    byte[] strBytes = _reader.ReadBytes(length);
                    Data.Strings.Add(Encoding.UTF8.GetString(strBytes));
                }
                else if (length == 0)
                {
                    Data.Strings.Add(string.Empty);
                }
                else
                {
                    break;
                }
            }
        }

        // =================================================================================
        //  Fields 分区
        // =================================================================================

        private void ReadFieldsSection(UsdCrateSection section)
        {
            // Fields 分区格式：
            //   每个字段: [ nameTokenIndex(4B) | valueType(1B) | ... 值数据 ]
            long end = section.Offset + section.Size;

            while (_stream.Position < end)
            {
                if (end - _stream.Position < 5) break;

                int nameTokenIndex = _reader.ReadInt32();
                byte valueTypeCode = _reader.ReadByte();

                var field = new UsdCrateField
                {
                    NameTokenIndex = nameTokenIndex,
                    ValueType = GetValueTypeName(valueTypeCode)
                };

                // 根据类型代码读取值
                field.Value = ReadFieldValue(valueTypeCode);

                Data.Fields.Add(field);
            }
        }

        /// <summary>
        /// 根据类型代码读取字段值。
        /// USD Crate 支持的类型包括：
        ///  float, float2, float3, float4, double, double2, double3, double4,
        ///  int, int2, int3, int4, uint, uint2, uint3, uint4,
        ///  bool, token, string, matrix4d, quatd,
        ///  以及对应的数组类型。
        /// </summary>
        private object? ReadFieldValue(byte typeCode)
        {
            // 简化版本：支持最常用的类型
            switch (typeCode)
            {
                // --- 标量类型 ---
                case 1: // bool
                    return _reader.ReadBoolean();
                case 2: // int (int32)
                    return _reader.ReadInt32();
                case 3: // uint (uint32)
                    return _reader.ReadUInt32();
                case 4: // float
                    return _reader.ReadSingle();
                case 5: // double
                    return _reader.ReadDouble();
                case 6: // token (字符串标识符，读为 4 字节 token 表索引)
                    {
                        int tokenIdx = _reader.ReadInt32();
                        return tokenIdx >= 0 && tokenIdx < Data.Tokens.Count
                            ? Data.Tokens[tokenIdx]
                            : $"<tok_{tokenIdx}>";
                    }
                case 7: // string (4 字节长度 + 数据)
                    {
                        int length = _reader.ReadInt32();
                        if (length > 0 && length < 10 * 1024 * 1024)
                        {
                            byte[] strBytes = _reader.ReadBytes(length);
                            return Encoding.UTF8.GetString(strBytes);
                        }
                        return string.Empty;
                    }
                case 8: // matrix4d (64 bytes = 16 * float64)
                    {
                        var m = new double[16];
                        for (int i = 0; i < 16; i++)
                            m[i] = _reader.ReadDouble();
                        return m;
                    }
                case 9: // quatd (32 bytes = 4 * float64)
                    {
                        return new double[] {
                            _reader.ReadDouble(),
                            _reader.ReadDouble(),
                            _reader.ReadDouble(),
                            _reader.ReadDouble()
                        };
                    }

                // --- 向量类型 ---
                case 11: // float2 (2 * float32)
                    return new float[] { _reader.ReadSingle(), _reader.ReadSingle() };
                case 12: // float3 (3 * float32)
                    return new float[] { _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle() };
                case 13: // float4 (4 * float32)
                    return new float[] { _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle() };
                case 14: // double2
                    return new double[] { _reader.ReadDouble(), _reader.ReadDouble() };
                case 15: // double3
                    return new double[] { _reader.ReadDouble(), _reader.ReadDouble(), _reader.ReadDouble() };
                case 16: // double4
                    return new double[] { _reader.ReadDouble(), _reader.ReadDouble(), _reader.ReadDouble(), _reader.ReadDouble() };
                case 17: // int2
                    return new int[] { _reader.ReadInt32(), _reader.ReadInt32() };
                case 18: // int3
                    return new int[] { _reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32() };
                case 19: // int4
                    return new int[] { _reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32() };

                // --- 数组类型 (类型代码 >= 128) ---
                case 128: // bool[]
                    return ReadArrayValue(() => (object)_reader.ReadBoolean());
                case 129: // int[]
                    return ReadArrayValue(() => _reader.ReadInt32());
                case 130: // uint[]
                    return ReadArrayValue(() => _reader.ReadUInt32());
                case 131: // float[]
                    return ReadArrayValue(() => _reader.ReadSingle());
                case 132: // double[]
                    return ReadArrayValue(() => _reader.ReadDouble());
                case 133: // token[]
                    return ReadArrayValue(() => {
                        int ti = _reader.ReadInt32();
                        return ti >= 0 && ti < Data.Tokens.Count ? Data.Tokens[ti] : $"<tok_{ti}>";
                    });
                case 134: // string[]
                    return ReadArrayValue(() => {
                        int len = _reader.ReadInt32();
                        if (len > 0 && len < 1024 * 1024)
                        {
                            byte[] b = _reader.ReadBytes(len);
                            return Encoding.UTF8.GetString(b);
                        }
                        return string.Empty;
                    });
                case 135: // float3[]
                    {
                        int count = _reader.ReadInt32();
                        var arr = new float[count * 3][];
                        for (int i = 0; i < count; i++)
                            arr[i] = new float[] { _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle() };
                        return arr;
                    }

                default:
                    // 未知类型 — 跳过
                    break;
            }

            return null;
        }

        /// <summary>读取一个数组值（通用格式：4 字节元素数量 + 元素序列）。</summary>
        private T[] ReadArrayValue<T>(Func<T> readElement)
        {
            int count = _reader.ReadInt32();
            var arr = new T[count];
            for (int i = 0; i < count; i++)
                arr[i] = readElement();
            return arr;
        }

        /// <summary>获取类型代码的名称字符串。</summary>
        private static string GetValueTypeName(byte code) => code switch
        {
            1 => "bool",
            2 => "int",
            3 => "uint",
            4 => "float",
            5 => "double",
            6 => "token",
            7 => "string",
            8 => "matrix4d",
            9 => "quatd",
            11 => "float2",
            12 => "float3",
            13 => "float4",
            14 => "double2",
            15 => "double3",
            16 => "double4",
            17 => "int2",
            18 => "int3",
            19 => "int4",
            128 => "bool[]",
            129 => "int[]",
            130 => "uint[]",
            131 => "float[]",
            132 => "double[]",
            133 => "token[]",
            134 => "string[]",
            135 => "float3[]",
            _ => $"unknown({code})"
        };

        // =================================================================================
        //  Specs 分区
        // =================================================================================

        private void ReadSpecsSection(UsdCrateSection section)
        {
            // Specs 分区格式：
            //   每个 spec: [ specType(4B) | pathIndex(4B) | fieldCount(4B) | fieldIndices(fieldCount * 4B) ]
            long end = section.Offset + section.Size;

            while (_stream.Position < end)
            {
                if (end - _stream.Position < 12) break;

                int specType = _reader.ReadInt32();
                int pathIndex = _reader.ReadInt32();
                int fieldCount = _reader.ReadInt32();

                var spec = new UsdCrateSpec
                {
                    SpecType = specType,
                    PathIndex = pathIndex
                };

                for (int i = 0; i < fieldCount; i++)
                {
                    if (end - _stream.Position < 4) break;
                    spec.FieldIndices.Add(_reader.ReadInt32());
                }

                Data.Specs.Add(spec);
            }
        }

        // =================================================================================
        //  FieldIndex 分区
        // =================================================================================

        private void ReadFieldIndexSection(UsdCrateSection section)
        {
            // FieldIndex 分区：Spec -> Fields 的映射
            // 格式：每个索引条目 [ specIndex(4B) | fieldIndex(4B) ]
            long end = section.Offset + section.Size;

            while (_stream.Position + 8 <= end)
            {
                int specIndex = _reader.ReadInt32();
                int fieldIndex = _reader.ReadInt32();

                Data.FieldIndices.Add(new UsdCrateFieldIndex
                {
                    SpecIndex = specIndex,
                    FieldIndex = fieldIndex
                });
            }
        }

        // =================================================================================
        //  工具方法
        // =================================================================================

        /// <summary>
        /// 检查文件是否为 USD Crate 格式（通过 magic bytes）。
        /// </summary>
        public static bool IsUsdCrate(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] magic = new byte[8];
                int read = fs.Read(magic, 0, 8);
                if (read < 8) return false;
                string magicStr = Encoding.ASCII.GetString(magic);
                return magicStr == "PXR-USDC";
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _reader?.Dispose();
                _stream?.Dispose();
                _disposed = true;
            }
        }
    }
}
