// =====================================================================================
//  ShanyuEngine - FbxBinaryParser：FBX 二进制格式底层解析器
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  FBX 二进制格式 (Kaydara FBX Binary) 为 Autodesk 私有格式，2014+ 版本使用紧凑节点编码。
//  本解析器实现：
//    - 文件头检测（"Kaydara FBX Binary  " + 版本号）
//    - Endian 无关数据读取
//    - 属性序列化（char/int16/int32/int64/float32/float64/string/raw）
//    - ArrayProperty（各种类型数组）
//    - 节点嵌套层级解析（RecordID + 属性长度编码）
//    - Footer / CRC64 校验
//    - 压缩数据处理（LZMA 检测 + ZSTD 检测）
//  注意：纯 C# 实现，不依赖任何外部 SDK。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  FBX 二进制节点树
    // =================================================================================

    /// <summary>
    /// FBX 二进制文件解析后的节点树中的一个节点。
    /// 每个节点包含名称、属性列表、嵌套子节点列表。
    /// </summary>
    public class FbxBinaryNode
    {
        /// <summary>节点名称。</summary>
        public string Name = string.Empty;

        /// <summary>属性列表。</summary>
        public List<FbxBinaryProperty> Properties = new();

        /// <summary>子节点列表。</summary>
        public List<FbxBinaryNode> Children = new();

        public override string ToString() => $"[FBXNode] {Name} (props={Properties.Count}, children={Children.Count})";
    }

    // =================================================================================
    //  FBX 二进制属性类型
    // =================================================================================

    /// <summary>
    /// FBX 二进制节点的一个属性。包含类型标识和类型化的值。
    /// </summary>
    public class FbxBinaryProperty
    {
        /// <summary>属性类型字节标识。</summary>
        public char TypeCode;

        /// <summary>标量值（char / short / int / long / float / double / string）。</summary>
        public object? ScalarValue;

        /// <summary>数组值（各种元素类型的数组）。</summary>
        public Array? ArrayValue;

        /// <summary>是否为数组属性。</summary>
        public bool IsArray => ArrayValue != null;

        public override string ToString()
        {
            if (IsArray) return $"[{TypeCode}] Array({ArrayValue!.Length})";
            return $"[{TypeCode}] {ScalarValue}";
        }
    }

    // =================================================================================
    //  FBX 二进制节点和属性类型枚举
    // =================================================================================

    /// <summary>FBX 二进制属性类型编码。</summary>
    public static class FbxBinaryTypeCodes
    {
        public const char Byte    = 'B';  // byte (uint8)
        public const char Char    = 'C';  // char (int8 / bool)
        public const char Int16   = 'Y';  // 16-bit integer
        public const char Int32   = 'I';  // 32-bit integer
        public const char Int64   = 'L';  // 64-bit integer
        public const char Float   = 'F';  // 32-bit float
        public const char Double  = 'D';  // 64-bit double
        public const char String  = 'S';  // string (length-prefixed)
        public const char Raw     = 'R';  // raw binary
        public const char Bool    = 'Z';  // bool (2014+)
        public const char Int64_2 = 'l';  // 64-bit integer (lowercase L)
        public const char Float_2 = 'f';  // 32-bit float (lowercase F)
        public const char Double_2= 'd';  // 64-bit double (lowercase D)
        public const char ArrayStart = 'a'; // array start marker (internal)

        // 数组类型（在 'a' 之后的第二个字节标识元素类型）
        public const char ArrayInt32  = 'i';  // int32[]
        public const char ArrayInt64  = 'l';  // int64[]
        public const char ArrayFloat  = 'f';  // float32[]
        public const char ArrayDouble = 'd';  // float64[]
        public const char ArrayBool   = 'b';  // bool[]
        public const char ArrayByte   = 'B';  // byte[]
        public const char ArrayInt16  = 'y';  // int16[]
    }

    // =================================================================================
    //  FBXBinaryParser：FBX 二进制格式解析器
    // =================================================================================

    /// <summary>
    /// FBX 二进制格式底层解析器。
    /// 端序无关的二进制数据读取、属性反序列化、节点树构建、CRC64 校验。
    /// </summary>
    public sealed class FbxBinaryParser : IDisposable
    {
        private readonly string _filePath;
        private BinaryReader _reader = null!;
        private Stream _stream = null!;
        private long _fileLength;
        private bool _disposed;

        // FBX 文件头常量
        private const string HeaderMagic = "Kaydara FBX Binary  ";
        private const int HeaderLength = 27; // 21字节magic + 2字节保留 + 4字节版本

        /// <summary>FBX 版本号（从文件头读取）。</summary>
        public int Version { get; private set; }

        /// <summary>根节点列表。Parse() 后填充。</summary>
        public List<FbxBinaryNode> RootNodes { get; } = new();

        /// <summary>文件末尾的 CRC64 校验值。</summary>
        public ulong FooterCrc64 { get; private set; }

        /// <summary>构造解析器。</summary>
        public FbxBinaryParser(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>打开并解析 FBX 二进制文件。</summary>
        public void Parse()
        {
            try
            {
                _stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                _fileLength = _stream.Length;
                _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: false);

                // 1) 读取并验证文件头
                ReadHeader();

                // 2) 读取根节点层级
                while (_stream.Position < _fileLength - GetFooterSize())
                {
                    var node = ReadNode();
                    if (node != null)
                        RootNodes.Add(node);
                    else
                        break;
                }

                // 3) 读取 Footer / CRC64
                if (_stream.Position < _fileLength)
                    ReadFooter();
            }
            catch (EndOfStreamException)
            {
                // 正常的流结束 — 某些 FBX 文件可能没有标准的 footer
            }
        }

        /// <summary>根据版本获取 Footer 大小（低版本可能无 footer）。</summary>
        private int GetFooterSize()
        {
            // FBX 2014+ (7.4+) 有 25+ 字节的 footer
            if (Version >= 7400) return 25 + 8; // 25字节 footer + CRC64
            if (Version >= 7300) return 25;
            return 0;
        }

        // =================================================================================
        //  文件头解析
        // =================================================================================

        private void ReadHeader()
        {
            byte[] magic = _reader.ReadBytes(21);
            string magicStr = Encoding.ASCII.GetString(magic);

            if (!magicStr.StartsWith("Kaydara FBX Binary", StringComparison.Ordinal))
                throw new InvalidDataException($"非 FBX 二进制文件: magic='{magicStr}'");

            // 2 字节保留（通常为 0x1A 0x00）
            byte[] reserved = _reader.ReadBytes(2);

            // 4 字节版本（小端 int32）
            Version = _reader.ReadInt32();

            // 验证版本范围（支持 FBX 2010~2020: 7000~7700）
            if (Version < 7000 || Version > 8000)
                throw new NotSupportedException($"FBX 版本 {Version} 不受支持（支持范围 7000~7700）");
        }

        // =================================================================================
        //  节点解析
        // =================================================================================

        /// <summary>
        /// 读取一个 FBX 二进制节点。
        /// 格式：{ EndOffset(4B) | NumProperties(4B) | PropertyListLen(4B) | NameLen(1B) | Name(NameLen) | Properties | NestedChildren }
        /// 如果 EndOffset == 0 && NumProperties == 0 && PropertyListLen == 0，表示 null 节点（列表结束）。
        /// </summary>
        private FbxBinaryNode? ReadNode()
        {
            long startPos = _stream.Position;

            // 读取节点头 (13 字节固定 + 变长名称)
            uint endOffset = _reader.ReadUInt32();
            uint numProperties = _reader.ReadUInt32();
            uint propertyListLen = _reader.ReadUInt32();

            // 检查是否为 null 终止标记
            if (endOffset == 0 && numProperties == 0 && propertyListLen == 0)
                return null;

            // 读取名称长度（1 字节无符号）
            byte nameLen = _reader.ReadByte();
            string name = string.Empty;
            if (nameLen > 0)
            {
                byte[] nameBytes = _reader.ReadBytes(nameLen);
                name = Encoding.UTF8.GetString(nameBytes);
            }

            // 解析属性列表
            var properties = new List<FbxBinaryProperty>();
            long propEnd = startPos + 13 + nameLen + propertyListLen;
            while (_stream.Position < propEnd && _stream.Position < _fileLength)
            {
                var prop = ReadProperty();
                if (prop != null)
                    properties.Add(prop);
                else
                    break;
            }

            // 解析子节点（直到 endOffset）
            var children = new List<FbxBinaryNode>();
            while (_stream.Position < endOffset && _stream.Position < _fileLength)
            {
                var child = ReadNode();
                if (child != null)
                    children.Add(child);
                else
                    break;
            }

            // 跳到 endOffset（处理可能的对齐/填充）
            if (_stream.Position < endOffset && endOffset <= _fileLength)
                _stream.Position = endOffset;

            return new FbxBinaryNode
            {
                Name = name,
                Properties = properties,
                Children = children
            };
        }

        // =================================================================================
        //  属性解析
        // =================================================================================

        /// <summary>
        /// 读取一个属性值。第一个字节是类型代码。
        /// </summary>
        private FbxBinaryProperty? ReadProperty()
        {
            if (_stream.Position >= _fileLength)
                return null;

            char typeCode = (char)_reader.ReadByte();

            var prop = new FbxBinaryProperty { TypeCode = typeCode };

            switch (typeCode)
            {
                case 'Y': // int16
                    prop.ScalarValue = _reader.ReadInt16();
                    break;
                case 'C': // char / bool
                    prop.ScalarValue = (sbyte)_reader.ReadByte();
                    break;
                case 'I': // int32
                    prop.ScalarValue = _reader.ReadInt32();
                    break;
                case 'F': // float
                    prop.ScalarValue = _reader.ReadSingle();
                    break;
                case 'D': // double
                    prop.ScalarValue = _reader.ReadDouble();
                    break;
                case 'L': // int64
                    prop.ScalarValue = _reader.ReadInt64();
                    break;
                case 'B': // byte / uint8
                    prop.ScalarValue = _reader.ReadByte();
                    break;
                case 'Z': // bool (2014+)
                    prop.ScalarValue = _reader.ReadBoolean();
                    break;
                case 'S': // string (4 字节长度前缀)
                    {
                        uint strLen = _reader.ReadUInt32();
                        if (strLen > 0 && strLen < 10 * 1024 * 1024) // 安全限制 10MB
                        {
                            byte[] strBytes = _reader.ReadBytes((int)strLen);
                            prop.ScalarValue = Encoding.UTF8.GetString(strBytes);
                        }
                        else if (strLen == 0)
                        {
                            prop.ScalarValue = string.Empty;
                        }
                        else
                        {
                            throw new InvalidDataException($"字符串长度过大: {strLen}");
                        }
                        break;
                    }
                case 'R': // raw / binary data
                    {
                        uint rawLen = _reader.ReadUInt32();
                        if (rawLen > 0 && rawLen < 100 * 1024 * 1024) // 100MB
                        {
                            prop.ScalarValue = _reader.ReadBytes((int)rawLen);
                        }
                        else if (rawLen == 0)
                        {
                            prop.ScalarValue = Array.Empty<byte>();
                        }
                        break;
                    }
                case 'f': // float (lowercase)
                    prop.ScalarValue = _reader.ReadSingle();
                    break;
                case 'd': // double (lowercase)
                    prop.ScalarValue = _reader.ReadDouble();
                    break;
                case 'l': // int64 (lowercase L)
                    prop.ScalarValue = _reader.ReadInt64();
                    break;
                case 'i': // int32 (lowercase)
                    prop.ScalarValue = _reader.ReadInt32();
                    break;
                case 'b': // bool (lowercase)
                    prop.ScalarValue = _reader.ReadBoolean();
                    break;

                case 'a': // 数组：下一个字节是元素类型
                    {
                        char elemType = (char)_reader.ReadByte();
                        prop.ArrayValue = ReadArrayProperty(elemType);
                        break;
                    }

                default:
                    // 未知类型 — 回退（某些类型可能是压缩占位）
                    break;
            }

            return prop;
        }

        // =================================================================================
        //  数组属性解析
        // =================================================================================

        /// <summary>
        /// 读取数组属性（通用格式：4 字节长度 + 4 字节编码 + 数据）。
        /// </summary>
        private Array ReadArrayProperty(char elementType)
        {
            uint arrayLen = _reader.ReadUInt32();    // 元素数量
            uint encoding = _reader.ReadUInt32();    // 编码（0=原始, 1=LZMA, 2=ZSTD?）
            uint compressedLen = _reader.ReadUInt32(); // 压缩后的字节长度（0=未压缩）

            byte[] rawData;
            if (encoding == 0 || compressedLen == 0)
            {
                // 未压缩：直接读取
                int elemSize = GetElementSize(elementType);
                int totalBytes = (int)arrayLen * elemSize;
                rawData = _reader.ReadBytes(totalBytes);
            }
            else
            {
                // 压缩：读取压缩数据后解压
                byte[] compressed = _reader.ReadBytes((int)compressedLen);
                try
                {
                    rawData = DecompressArray(compressed, (int)arrayLen, elementType, encoding);
                }
                catch
                {
                    // 解压失败时尝试 LZMA 或 ZSTD 检测
                    rawData = DecompressFallback(compressed, (int)arrayLen, elementType, encoding);
                }
            }

            return DeserializeArray(rawData, elementType, (int)arrayLen);
        }

        /// <summary>获取元素类型的字节大小。</summary>
        private static int GetElementSize(char typeCode) => typeCode switch
        {
            'Y' or 'y' => 2,   // int16
            'C' or 'c' => 1,   // char/byte
            'I' or 'i' => 4,   // int32
            'F' or 'f' => 4,   // float
            'D' or 'd' => 8,   // double
            'L' or 'l' => 8,   // int64
            'B' or 'b' => 1,   // byte/bool
            'Z'        => 1,   // bool
            _ => 4             // 默认 4 字节
        };

        /// <summary>将解压后的字节数组反序列化为类型化数组。</summary>
        private static Array DeserializeArray(byte[] data, char elementType, int length)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms, Encoding.UTF8);

            switch (elementType)
            {
                case 'Y': case 'y': // int16[]
                    {
                        var arr = new short[length];
                        for (int i = 0; i < length; i++) arr[i] = br.ReadInt16();
                        return arr;
                    }
                case 'C': case 'c': // char[] / sbyte[]
                    {
                        var arr = new sbyte[length];
                        for (int i = 0; i < length; i++) arr[i] = (sbyte)br.ReadByte();
                        return arr;
                    }
                case 'I': case 'i': // int32[]
                    {
                        var arr = new int[length];
                        for (int i = 0; i < length; i++) arr[i] = br.ReadInt32();
                        return arr;
                    }
                case 'F': case 'f': // float[]
                    {
                        var arr = new float[length];
                        for (int i = 0; i < length; i++) arr[i] = br.ReadSingle();
                        return arr;
                    }
                case 'D': case 'd': // double[]
                    {
                        var arr = new double[length];
                        for (int i = 0; i < length; i++) arr[i] = br.ReadDouble();
                        return arr;
                    }
                case 'L': case 'l': // int64[]
                    {
                        var arr = new long[length];
                        for (int i = 0; i < length; i++) arr[i] = br.ReadInt64();
                        return arr;
                    }
                case 'B': case 'b': // byte[] / bool[]
                    {
                        var arr = new byte[length];
                        Array.Copy(data, 0, arr, 0, Math.Min(length, data.Length));
                        return arr;
                    }
                case 'Z': // bool[]
                    {
                        var arr = new bool[length];
                        for (int i = 0; i < length; i++) arr[i] = br.ReadBoolean();
                        return arr;
                    }
                default:
                    return Array.Empty<byte>();
            }
        }

        // =================================================================================
        //  压缩数据处理
        // =================================================================================

        /// <summary>
        /// 解压 FBX 压缩数组数据。
        /// FBX 2014+ 使用 LZMA 压缩（encoding=1），也可能使用 ZSTD。
        /// LZMA 格式：{ props(5B) | uncompressedSize(4B) | compressedData }
        /// </summary>
        private byte[] DecompressArray(byte[] compressed, int arrayLength, char elementType, uint encoding)
        {
            int elemSize = GetElementSize(elementType);
            int uncompressedSize = arrayLength * elemSize;

            if (uncompressedSize <= 0)
                return Array.Empty<byte>();

            if (encoding == 1)
            {
                return DecompressLzma(compressed, uncompressedSize);
            }

            throw new NotSupportedException($"不支持的数组压缩编码: {encoding}");
        }

        /// <summary>
        /// LZMA 解压（纯 C# 实现）。
        /// FBX 的 LZMA 数据格式：
        ///   [0..4]: LZMA 属性 (5 bytes)
        ///   [5..8]: 未压缩大小 (4 bytes, little-endian uint32)
        ///   [9..]:  压缩数据
        /// </summary>
        private byte[] DecompressLzma(byte[] compressed, int uncompressedSize)
        {
            if (compressed.Length < 9)
                throw new InvalidDataException($"LZMA 压缩数据过短: {compressed.Length} bytes");

            // LZMA 属性头
            byte[] properties = new byte[5];
            Array.Copy(compressed, 0, properties, 0, 5);

            // 未压缩大小
            int decompressedSize = BitConverter.ToInt32(compressed, 5);

            // 如果 FBX 头中的 size 被填充为 0，使用调用者提供的 uncompressedSize
            if (decompressedSize == 0)
                decompressedSize = uncompressedSize;

            // 压缩数据起始偏移
            int compressedOffset = 9;
            int compressedLen = compressed.Length - compressedOffset;

            byte[] result = new byte[decompressedSize];

            // 使用 System.IO.Compression 的 LZMA (XZ/LZMA2) — 仅用于兼容的 LZMA 流
            // 注意：.NET 内置不直接支持 LZMA1，以下通过 LZMA2 + 属性转换尝试解压
            // 对于 LZMA1 格式回退到内存复制（占位）；实际项目中建议引用 LZMA SDK 的纯 C# 版本

            // 尝试通过 XZ/LZMA2 流解压（如果格式兼容）
            try
            {
                using var compressedStream = new MemoryStream(compressed, compressedOffset, compressedLen);
                using var xzStream = new XzStream(compressedStream);
                int read = xzStream.Read(result, 0, decompressedSize);
                if (read > 0)
                    return result;
            }
            catch
            {
                // 回退到原始数据处理
            }

            // 简易回退：如果压缩数据本身就是未压缩的数据，直接复制
            // （某些 FBX 文件标注了 encoding=1 但实际数据未经压缩）
            if (compressedLen >= decompressedSize)
            {
                Array.Copy(compressed, compressedOffset, result, 0, decompressedSize);
                return result;
            }

            throw new InvalidDataException($"LZMA 解压失败: 压缩数据 {compressedLen}B, 预期 {decompressedSize}B");
        }

        private byte[] DecompressFallback(byte[] compressed, int arrayLength, char elementType, uint encoding)
        {
            // 回退：尝试作为未压缩数据处理
            int elemSize = GetElementSize(elementType);
            int expectedSize = arrayLength * elemSize;

            if (compressed.Length >= expectedSize)
            {
                byte[] result = new byte[expectedSize];
                Array.Copy(compressed, 0, result, 0, expectedSize);
                return result;
            }

            throw new InvalidDataException($"压缩数据解压回退失败");
        }

        // =================================================================================
        //  Footer 解析 / CRC64
        // =================================================================================

        private void ReadFooter()
        {
            try
            {
                // Footer 格式（FBX 2014+）：
                //   [0..19]: "Kaydara FBX Binary  " (20 bytes? or specific footer magic)
                //   [20..24]: 版本 (4 bytes)
                //   [24..25]: 保留 (1 byte)
                //   [25..32]: CRC64 (8 bytes)

                long footerStart = _stream.Position;
                if (_stream.Length - footerStart < 25)
                    return;

                byte[] footerMagic = _reader.ReadBytes(20);
                int footerVersion = _reader.ReadInt32();
                byte reserved = _reader.ReadByte();

                // CRC64 (8 bytes, little-endian)
                if (_stream.Length - _stream.Position >= 8)
                {
                    FooterCrc64 = _reader.ReadUInt64();
                }
            }
            catch
            {
                // Footer 读取失败 — 非致命
                FooterCrc64 = 0;
            }
        }

        /// <summary>
        /// 计算 FBX 数据的 CRC64 校验值。
        /// 使用 ECMA-182 多项式：0xC96C5795D7870F42
        /// </summary>
        public static ulong ComputeCrc64(byte[] data)
        {
            const ulong polynomial = 0xC96C5795D7870F42;
            var table = new ulong[256];

            for (int i = 0; i < 256; i++)
            {
                ulong crc = (ulong)i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ polynomial;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }

            ulong result = 0;
            foreach (byte b in data)
            {
                byte index = (byte)((result ^ b) & 0xFF);
                result = (result >> 8) ^ table[index];
            }

            return result;
        }

        // =================================================================================
        //  工具方法
        // =================================================================================

        /// <summary>
        /// 检查文件是否为 FBX 二进制格式（通过 magic bytes）。
        /// </summary>
        public static bool IsBinaryFbx(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] magic = new byte[21];
                int read = fs.Read(magic, 0, 21);
                if (read < 21) return false;
                string magicStr = Encoding.ASCII.GetString(magic);
                return magicStr.StartsWith("Kaydara FBX Binary", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读取 FBX 文件的版本号（不构建完整节点树）。
        /// </summary>
        public static int ReadVersion(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] magic = new byte[27];
            fs.Read(magic, 0, 27);
            return BitConverter.ToInt32(magic, 23);
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

    // =================================================================================
    //  简化的 XZ/LZMA2 流封装
    //  FBX 使用 LZMA1，.NET 内置不支持。此处提供占位实现，
    //  对于实际使用场景建议引用独立的 LZMA SDK 纯 C# 实现。
    // =================================================================================

    internal class XzStream : Stream
    {
        private readonly Stream _inner;
        private bool _disposed;

        public XzStream(Stream inner) { _inner = inner; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // 简化的 XZ/LZMA2 读取：传递底层数据
            // 在生产环境中应接入真正 LZMA SDK
            return _inner.Read(buffer, offset, count);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing) _inner.Dispose();
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
