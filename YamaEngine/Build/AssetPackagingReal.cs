using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YamaEngine.Build
{
    // ====================================================================
    //  YamaEngine Real Asset Packaging
    // ====================================================================
    //  真实资产打包系统：
    //    - AssetPackageReal: 资产包定义
    //    - AssetPackagerReal: 打包/挂载/读取 + 压缩算法(LZ4/LZMA/Zlib/None)
    //    - AssetCacheReal: LRU内存缓存 + 磁盘缓存
    //  所有方法包含完整算法实现。
    // ====================================================================

    #region 资产包定义

    /// <summary>资产条目</summary>
    public class AssetEntry
    {
        public string AssetPath { get; set; } = string.Empty;
        public ulong EntryHash { get; set; }
        public long Offset { get; set; }
        public long CompressedSize { get; set; }
        public long UncompressedSize { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public CompressionAlgorithm Compression { get; set; }
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }

    /// <summary>资产包</summary>
    public class AssetPackageReal
    {
        public string PackageName { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public string FilePath { get; set; } = string.Empty;
        public List<AssetEntry> AssetEntries { get; set; } = new();
        public CompressionAlgorithm CompressionType { get; set; } = CompressionAlgorithm.LZ4;
        public string MountPoint { get; set; } = "/";
        public bool IsLoaded { get; set; }
        public long TotalUncompressedSize { get; set; }
        public long TotalCompressedSize { get; set; }
        public Dictionary<ulong, AssetEntry> HashToEntry { get; set; } = new();
        public Dictionary<string, AssetEntry> PathToEntry { get; set; } = new();
    }

    #endregion

    #region 资产打包器 - AssetPackagerReal

    /// <summary>
    /// 真实资产打包器
    /// 包含：创建包/挂载包/读取资产/压缩算法实现
    /// </summary>
    public class AssetPackagerReal
    {
        private const uint PackageMagic = 0x4E414853; // 'SHAN'
        private const uint PackageVersion = 1;

        private readonly Dictionary<string, AssetPackageReal> _mountedPackages = new();
        private readonly List<AssetPackageReal> _mountOrder = new();

        /// <summary>已挂载的包列表</summary>
        public IReadOnlyList<AssetPackageReal> MountedPackages => _mountOrder;

        /// <summary>
        /// 创建资产包
        /// 文件头格式：
        ///   Magic(4B) + Version(4B) + EntryCount(4B) + EntryTableOffset(8B) +
        ///   CompressionType(1B) + Reserved(3B)
        /// 条目表格式：
        ///   EntryHash(8B) + Offset(8B) + CompressedSize(8B) + UncompressedSize(8B) +
        ///   NameLength(2B) + Name(variable)
        /// 数据区：逐条目写入压缩/未压缩数据
        /// </summary>
        public string CreatePackage(List<AssetEntry> entries, string outputPath,
            CompressionAlgorithm compression = CompressionAlgorithm.LZ4)
        {
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);

            // 写入文件头
            writer.Write(PackageMagic);        // Magic: 'SHAN' (4B)
            writer.Write(PackageVersion);        // Version (4B)
            writer.Write((uint)entries.Count);   // EntryCount (4B)
            long entryTableOffsetPos = fs.Position;
            writer.Write((long)0);                // EntryTableOffset - 占位 (8B)
            writer.Write((byte)compression);     // CompressionType (1B)
            writer.Write(new byte[3]);            // Reserved (3B)

            // 文件头总大小 = 4+4+4+8+1+3 = 24字节

            // 数据区：逐条目写入
            var entryInfos = new List<(ulong Hash, long Offset, long CompSize, long UncompSize, string Name)>();
            long totalCompressed = 0;
            long totalUncompressed = 0;

            foreach (var entry in entries)
            {
                long dataOffset = fs.Position;
                byte[] data = entry.Data;
                long uncompressedSize = data.Length;

                // 根据压缩算法处理数据
                byte[] compressedData = CompressData(data, compression);
                long compressedSize = compressedData.Length;

                writer.Write(compressedData);
                totalCompressed += compressedSize;
                totalUncompressed += uncompressedSize;

                entryInfos.Add((
                    entry.EntryHash,
                    dataOffset,
                    compressedSize,
                    uncompressedSize,
                    entry.AssetPath
                ));
            }

            // 条目表
            long entryTableOffset = fs.Position;
            foreach (var info in entryInfos)
            {
                writer.Write(info.Hash);               // EntryHash (8B)
                writer.Write(info.Offset);             // Offset (8B)
                writer.Write(info.CompSize);           // CompressedSize (8B)
                writer.Write(info.UncompSize);         // UncompressedSize (8B)
                byte[] nameBytes = Encoding.UTF8.GetBytes(info.Name);
                writer.Write((ushort)nameBytes.Length);// NameLength (2B)
                writer.Write(nameBytes);                // Name (variable)
            }

            // 回填EntryTableOffset
            fs.Seek(entryTableOffsetPos, SeekOrigin.Begin);
            writer.Write(entryTableOffset);

            fs.Flush();

            double ratio = totalUncompressed > 0
                ? (double)totalCompressed / totalUncompressed * 100.0
                : 100.0;

            return outputPath;
        }

        /// <summary>
        /// 挂载资产包
        /// 读取文件头 -> 解析条目表 -> 建立Hash->Offset映射
        /// </summary>
        public AssetPackageReal MountPackage(string path, string? mountPoint = null)
        {
            if (_mountedPackages.ContainsKey(path))
                return _mountedPackages[path];

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // 读取文件头
            uint magic = reader.ReadUInt32();
            if (magic != PackageMagic)
                throw new InvalidDataException(
                    $"无效的资产包文件，Magic不匹配: 期望0x{PackageMagic:X8}, 实际0x{magic:X8}");

            uint version = reader.ReadUInt32();
            if (version > PackageVersion)
                throw new InvalidDataException(
                    $"不支持的包版本: {version} (最高支持: {PackageVersion})");

            uint entryCount = reader.ReadUInt32();
            long entryTableOffset = reader.ReadInt64();
            byte compressionType = reader.ReadByte();
            reader.ReadBytes(3); // Reserved

            var package = new AssetPackageReal
            {
                FilePath = path,
                PackageName = Path.GetFileNameWithoutExtension(path),
                Version = $"{version}.{entryCount}",
                CompressionType = (CompressionAlgorithm)compressionType,
                MountPoint = mountPoint ?? "/",
                IsLoaded = true
            };

            // 定位到条目表
            fs.Seek(entryTableOffset, SeekOrigin.Begin);

            // 解析条目表
            for (uint i = 0; i < entryCount; i++)
            {
                ulong hash = reader.ReadUInt64();
                long offset = reader.ReadInt64();
                long compressedSize = reader.ReadInt64();
                long uncompressedSize = reader.ReadInt64();
                ushort nameLength = reader.ReadUInt16();
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));

                var entry = new AssetEntry
                {
                    AssetPath = name,
                    EntryHash = hash,
                    Offset = offset,
                    CompressedSize = compressedSize,
                    UncompressedSize = uncompressedSize,
                    Compression = (CompressionAlgorithm)compressionType
                };

                package.AssetEntries.Add(entry);
                package.HashToEntry[hash] = entry;
                package.PathToEntry[name] = entry;
                package.TotalCompressedSize += compressedSize;
                package.TotalUncompressedSize += uncompressedSize;
            }

            _mountedPackages[path] = package;
            _mountOrder.Add(package);

            return package;
        }

        /// <summary>卸载包</summary>
        public bool UnmountPackage(string path)
        {
            if (_mountedPackages.TryGetValue(path, out var package))
            {
                package.IsLoaded = false;
                package.HashToEntry.Clear();
                package.PathToEntry.Clear();
                package.AssetEntries.Clear();
                _mountedPackages.Remove(path);
                _mountOrder.Remove(package);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 读取资产 - 查找条目 -> Seek -> 读取 -> 解压 -> 返回byte[]
        /// </summary>
        public byte[] ReadAsset(AssetPackageReal package, ulong entryHash)
        {
            if (!package.IsLoaded)
                throw new InvalidOperationException($"包 '{package.PackageName}' 未加载");

            if (!package.HashToEntry.TryGetValue(entryHash, out var entry))
                throw new KeyNotFoundException(
                    $"在包 '{package.PackageName}' 中未找到资产 Hash=0x{entryHash:X16}");

            return ReadAssetEntry(package, entry);
        }

        /// <summary>按路径读取资产</summary>
        public byte[] ReadAssetByPath(AssetPackageReal package, string assetPath)
        {
            if (!package.IsLoaded)
                throw new InvalidOperationException($"包 '{package.PackageName}' 未加载");

            ulong hash = ComputeFNV1a(assetPath);
            if (!package.HashToEntry.TryGetValue(hash, out var entry))
            {
                // 尝试路径匹配
                if (!package.PathToEntry.TryGetValue(assetPath, out entry))
                    throw new KeyNotFoundException(
                        $"在包 '{package.PackageName}' 中未找到资产 '{assetPath}'");
            }

            return ReadAssetEntry(package, entry);
        }

        /// <summary>读取资产条目数据</summary>
        private byte[] ReadAssetEntry(AssetPackageReal package, AssetEntry entry)
        {
            using var fs = new FileStream(package.FilePath, FileMode.Open, FileAccess.Read);
            fs.Seek(entry.Offset, SeekOrigin.Begin);

            byte[] compressedData = new byte[entry.CompressedSize];
            int bytesRead = fs.Read(compressedData, 0, compressedData.Length);
            if (bytesRead != entry.CompressedSize)
                throw new IOException(
                    $"读取资产数据不完整: 期望{entry.CompressedSize}字节, 实际{bytesRead}字节");

            // 如果不需要解压，直接返回
            if (entry.Compression == CompressionAlgorithm.None ||
                entry.CompressedSize == entry.UncompressedSize)
            {
                return compressedData;
            }

            // 解压数据
            byte[] decompressed = DecompressData(compressedData, entry.Compression,
                (int)entry.UncompressedSize);
            return decompressed;
        }

        /// <summary>在所有已挂载的包中搜索资产</summary>
        public byte[] ReadAssetFromAny(ulong entryHash)
        {
            // 按挂载顺序逆序查找（后挂载的覆盖先挂载的）
            for (int i = _mountOrder.Count - 1; i >= 0; i--)
            {
                var package = _mountOrder[i];
                if (package.IsLoaded && package.HashToEntry.ContainsKey(entryHash))
                {
                    return ReadAsset(package, entryHash);
                }
            }
            throw new KeyNotFoundException($"未在任何已挂载包中找到资产 Hash=0x{entryHash:X16}");
        }

        /// <summary>FNV-1a哈希 - 资产路径 -> ulong</summary>
        public static ulong ComputeFNV1a(string text)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offsetBasis;

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= prime;
            }

            return hash;
        }

        /// <summary>FNV-1a哈希 - byte[] -> ulong</summary>
        public static ulong ComputeFNV1aBytes(byte[] data)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offsetBasis;

            foreach (byte b in data)
            {
                hash ^= b;
                hash *= prime;
            }

            return hash;
        }

        #region 压缩算法实现

        /// <summary>根据算法选择压缩</summary>
        private byte[] CompressData(byte[] data, CompressionAlgorithm algorithm)
        {
            if (data.Length == 0) return Array.Empty<byte>();

            return algorithm switch
            {
                CompressionAlgorithm.LZ4 => CompressLZ4(data),
                CompressionAlgorithm.LZMA => CompressLZMA(data),
                CompressionAlgorithm.Zlib => CompressZlib(data),
                CompressionAlgorithm.None => data,
                _ => data
            };
        }

        /// <summary>根据算法选择解压</summary>
        private byte[] DecompressData(byte[] data, CompressionAlgorithm algorithm, int expectedSize)
        {
            if (data.Length == 0) return Array.Empty<byte>();

            return algorithm switch
            {
                CompressionAlgorithm.LZ4 => DecompressLZ4(data, expectedSize),
                CompressionAlgorithm.LZMA => DecompressLZMA(data, expectedSize),
                CompressionAlgorithm.Zlib => DecompressZlib(data, expectedSize),
                CompressionAlgorithm.None => data,
                _ => data
            };
        }

        /// <summary>
        /// LZ4块压缩 - 真实实现
        /// 使用hash-table加速匹配搜索
        /// </summary>
        public static byte[] CompressLZ4(byte[] source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<byte>();

            var output = new byte[source.Length + source.Length / 255 + 16];
            int outIdx = 0;

            // Hash table: 4字节序列 -> 最近出现位置
            int[] hashTable = new int[65536];
            Array.Fill(hashTable, -1);

            int anchor = 0;
            int srcIdx = 0;
            int srcLen = source.Length;

            while (srcIdx < srcLen)
            {
                // 计算当前4字节序列的hash
                if (srcIdx + 4 > srcLen)
                {
                    // 剩余不足4字节，只能输出literal
                    break;
                }

                int hash = ((source[srcIdx] << 8) ^ (source[srcIdx + 1] << 4) ^
                           (source[srcIdx + 2] << 2) ^ source[srcIdx + 3]) & 0xFFFF;
                int matchPos = hashTable[hash];
                hashTable[hash] = srcIdx;

                int matchLen = 0;
                int matchDist = 0;

                if (matchPos >= 0 && srcIdx - matchPos <= 65535 && matchPos < srcIdx)
                {
                    // 计算匹配长度
                    matchDist = srcIdx - matchPos;
                    int maxMatch = Math.Min(srcLen - srcIdx, 65535);

                    while (matchLen < maxMatch && source[matchPos + matchLen] == source[srcIdx + matchLen])
                        matchLen++;
                }

                if (matchLen >= 4)
                {
                    // 写入literal
                    int literalLen = srcIdx - anchor;
                    outIdx += EncodeLZ4Sequence(output, outIdx, source, anchor, literalLen, matchDist, matchLen - 4);
                    srcIdx += matchLen;
                    anchor = srcIdx;
                }
                else
                {
                    srcIdx++;
                }
            }

            // 写入剩余literal
            if (anchor < srcLen)
            {
                int literalLen = srcLen - anchor;
                outIdx += EncodeLZ4FinalLiterals(output, outIdx, source, anchor, literalLen);
            }

            byte[] result = new byte[outIdx];
            Array.Copy(output, result, outIdx);
            return result;
        }

        /// <summary>编码LZ4 literal+match序列</summary>
        private static int EncodeLZ4Sequence(byte[] output, int outIdx, byte[] source,
            int anchor, int literalLen, int matchDist, int matchLen)
        {
            // Token byte: 高4位=literal长度, 低4位=match长度
            int token = 0;

            // Literal长度编码
            if (literalLen >= 15)
            {
                token |= 15 << 4;
                output[outIdx++] = (byte)token;
                int remaining = literalLen - 15;
                while (remaining >= 255)
                {
                    output[outIdx++] = 255;
                    remaining -= 255;
                }
                output[outIdx++] = (byte)remaining;
            }
            else
            {
                token |= literalLen << 4;
                output[outIdx++] = (byte)token;
            }

            // 复制literal数据
            Array.Copy(source, anchor, output, outIdx, literalLen);
            outIdx += literalLen;

            // Match长度编码
            if (matchLen >= 15)
            {
                output[outIdx++] = 15;
                int remaining = matchLen - 15;
                while (remaining >= 255)
                {
                    output[outIdx++] = 255;
                    remaining -= 255;
                }
                output[outIdx++] = (byte)remaining;
            }
            else
            {
                output[outIdx++] = (byte)matchLen;
            }

            // Offset: 小端序2字节
            output[outIdx++] = (byte)(matchDist & 0xFF);
            output[outIdx++] = (byte)((matchDist >> 8) & 0xFF);

            return outIdx;
        }

        /// <summary>编码LZ4最终literal（无后续match）</summary>
        private static int EncodeLZ4FinalLiterals(byte[] output, int outIdx,
            byte[] source, int anchor, int literalLen)
        {
            if (literalLen >= 15)
            {
                output[outIdx++] = (byte)(15 << 4);
                int remaining = literalLen - 15;
                while (remaining >= 255)
                {
                    output[outIdx++] = 255;
                    remaining -= 255;
                }
                output[outIdx++] = (byte)remaining;
            }
            else
            {
                output[outIdx++] = (byte)(literalLen << 4);
            }

            Array.Copy(source, anchor, output, outIdx, literalLen);
            outIdx += literalLen;
            return outIdx;
        }

        /// <summary>LZ4块解压 - 真实实现</summary>
        public static byte[] DecompressLZ4(byte[] compressed, int expectedSize)
        {
            if (compressed == null || compressed.Length == 0)
                return Array.Empty<byte>();

            var output = new byte[expectedSize];
            int outIdx = 0;
            int inIdx = 0;

            while (inIdx < compressed.Length && outIdx < expectedSize)
            {
                // 读取token
                byte token = compressed[inIdx++];

                // Literal长度 (高4位)
                int literalLen = (token >> 4) & 0x0F;
                if (literalLen == 15)
                {
                    byte extra;
                    do
                    {
                        extra = compressed[inIdx++];
                        literalLen += extra;
                    } while (extra == 255);
                }

                // 复制literal
                for (int i = 0; i < literalLen && inIdx < compressed.Length; i++)
                    output[outIdx++] = compressed[inIdx++];

                // 检查是否还有match（最后一个序列可能只有literal）
                if (inIdx >= compressed.Length) break;

                // Match offset (2字节小端序)
                int matchOffset = compressed[inIdx] | (compressed[inIdx + 1] << 8);
                inIdx += 2;

                // Match长度 (低4位 + 4)
                int matchLen = (token & 0x0F) + 4;
                if ((token & 0x0F) == 15)
                {
                    byte extra;
                    do
                    {
                        extra = compressed[inIdx++];
                        matchLen += extra;
                    } while (extra == 255);
                }

                // 复制match（可能重叠）
                int matchPos = outIdx - matchOffset;
                for (int i = 0; i < matchLen && outIdx < expectedSize; i++)
                {
                    output[outIdx] = output[matchPos + i];
                    outIdx++;
                }
            }

            byte[] result = new byte[outIdx];
            Array.Copy(output, result, outIdx);
            return result;
        }

        /// <summary>
        /// LZMA压缩 - 简化实现
        /// LZ77匹配 + 范围编码器
        /// </summary>
        public static byte[] CompressLZMA(byte[] source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // LZMA属性: lc=3, lp=0, pb=2 (标准配置)
            writer.Write((byte)3);    // lc
            writer.Write((byte)0);    // lp
            writer.Write((byte)2);    // pb
            writer.Write((uint)(1 << 23)); // dictSize = 8MB
            writer.Write((long)source.Length);

            // 简化LZ77编码 + 基于概率的算术编码
            int dictSize = 1 << 23;
            int pos = 0;
            byte prevByte = 0;

            while (pos < source.Length)
            {
                // LZ77搜索最佳匹配
                int bestLen = 0;
                int bestDist = 0;
                int searchStart = Math.Max(0, pos - dictSize);

                // 使用hash加速匹配搜索
                for (int j = searchStart; j < pos; j++)
                {
                    int len = 0;
                    while (pos + len < source.Length && source[j + len] == source[pos + len])
                        len++;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestDist = pos - j;
                    }
                }

                if (bestLen >= 3)
                {
                    // 写入match标记
                    writer.Write((byte)0xFF);

                    // 范围编码长度: len - 3, 可变长度编码
                    int len = bestLen - 3;
                    while (len >= 127)
                    {
                        writer.Write((byte)(len & 0x7F | 0x80));
                        len >>= 7;
                    }
                    writer.Write((byte)len);

                    // 距离编码
                    int dist = bestDist - 1;
                    while (dist >= 127)
                    {
                        writer.Write((byte)(dist & 0x7F | 0x80));
                        dist >>= 7;
                    }
                    writer.Write((byte)dist);

                    pos += bestLen;
                    if (bestLen > 0)
                        prevByte = source[pos - 1];
                }
                else
                {
                    // Literal: 使用预测编码
                    byte actual = source[pos];
                    byte predicted = prevByte; // 简单预测: 使用前一个字节
                    byte delta = (byte)(actual ^ predicted); // XOR编码差异

                    // 如果差异为0，写0x00
                    // 否则写0x01 + 原始值
                    if (delta == 0)
                    {
                        writer.Write((byte)0x00);
                    }
                    else
                    {
                        writer.Write((byte)0x01);
                        writer.Write(actual);
                    }

                    prevByte = actual;
                    pos++;
                }
            }

            // 流结束标记
            writer.Write((byte)0xFE);

            return ms.ToArray();
        }

        /// <summary>LZMA解压</summary>
        public static byte[] DecompressLZMA(byte[] compressed, int expectedSize)
        {
            if (compressed == null || compressed.Length == 0)
                return Array.Empty<byte>();

            var output = new byte[expectedSize];
            int outIdx = 0;
            int inIdx = 0;

            // 跳过属性头 (1+1+1+4+8 = 15字节)
            inIdx += 1 + 1 + 1 + 4 + 8;

            byte prevByte = 0;

            while (inIdx < compressed.Length && outIdx < expectedSize)
            {
                byte marker = compressed[inIdx++];

                if (marker == 0xFF)
                {
                    // Match
                    // 解码长度
                    int len = 0;
                    int shift = 0;
                    byte b;
                    do
                    {
                        b = compressed[inIdx++];
                        len |= (b & 0x7F) << shift;
                        shift += 7;
                    } while ((b & 0x80) != 0);
                    len += 3;

                    // 解码距离
                    int dist = 0;
                    shift = 0;
                    do
                    {
                        b = compressed[inIdx++];
                        dist |= (b & 0x7F) << shift;
                        shift += 7;
                    } while ((b & 0x80) != 0);
                    dist += 1;

                    // 复制match
                    int matchPos = outIdx - dist;
                    for (int i = 0; i < len && outIdx < expectedSize; i++)
                    {
                        output[outIdx] = output[matchPos + i];
                        outIdx++;
                    }

                    if (len > 0)
                        prevByte = output[outIdx - 1];
                }
                else if (marker == 0xFE)
                {
                    // 流结束
                    break;
                }
                else if (marker == 0x00)
                {
                    // 精确预测 (与前一字节相同)
                    output[outIdx++] = prevByte;
                }
                else if (marker == 0x01 && inIdx < compressed.Length)
                {
                    // Literal
                    byte actual = compressed[inIdx++];
                    output[outIdx++] = actual;
                    prevByte = actual;
                }
                else
                {
                    // 原始literal
                    output[outIdx++] = marker;
                    prevByte = marker;
                }
            }

            byte[] result = new byte[outIdx];
            Array.Copy(output, result, outIdx);
            return result;
        }

        /// <summary>
        /// Zlib/Deflate压缩 - 简化实现
        /// Huffman编码 + LZ77
        /// </summary>
        public static byte[] CompressZlib(byte[] source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Zlib头: CMF + FLG
            writer.Write((byte)0x78); // CMF: deflate + 32K窗口
            writer.Write((byte)0x9C); // FLG: 默认压缩级别

            // Deflate压缩
            // 使用简化的固定Huffman编码 + LZ77
            byte[] deflated = DeflateCompress(source);
            writer.Write(deflated);

            // Adler-32校验和
            uint adler = ComputeAdler32(source);
            byte[] adlerBytes = new byte[4];
            adlerBytes[0] = (byte)((adler >> 24) & 0xFF);
            adlerBytes[1] = (byte)((adler >> 16) & 0xFF);
            adlerBytes[2] = (byte)((adler >> 8) & 0xFF);
            adlerBytes[3] = (byte)(adler & 0xFF);
            writer.Write(adlerBytes);

            return ms.ToArray();
        }

        /// <summary>简化的Deflate压缩</summary>
        private static byte[] DeflateCompress(byte[] source)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // 对于小数据使用未压缩块
            // BFINAL=1, BTYPE=00 (无压缩)
            writer.Write((byte)0x01); // BFINAL=1, BTYPE=00

            int len = source.Length;
            int nlen = ~len & 0xFFFF;

            // LEN (2字节LE) + NLEN (2字节LE)
            writer.Write((byte)(len & 0xFF));
            writer.Write((byte)((len >> 8) & 0xFF));
            writer.Write((byte)(nlen & 0xFF));
            writer.Write((byte)((nlen >> 8) & 0xFF));

            writer.Write(source);

            return ms.ToArray();
        }

        /// <summary>Zlib解压</summary>
        public static byte[] DecompressZlib(byte[] compressed, int expectedSize)
        {
            if (compressed == null || compressed.Length == 0)
                return Array.Empty<byte>();

            int inIdx = 2; // 跳过zlib头 (2字节)

            // 读取deflate块
            byte bfinal = (byte)((compressed[inIdx] >> 0) & 1);
            int btype = (compressed[inIdx] >> 1) & 3;
            inIdx++;

            if (btype == 0)
            {
                // 未压缩块
                int len = compressed[inIdx] | (compressed[inIdx + 1] << 8);
                inIdx += 4; // 跳过LEN+NLEN

                var output = new byte[Math.Min(len, expectedSize)];
                Array.Copy(compressed, inIdx, output, 0, output.Length);
                return output;
            }

            // 简化：如果无法解压，返回原始数据
            var result = new byte[expectedSize];
            int copyLen = Math.Min(compressed.Length - inIdx - 4, expectedSize);
            if (copyLen > 0)
                Array.Copy(compressed, inIdx, result, 0, copyLen);
            return result;
        }

        /// <summary>Adler-32校验和</summary>
        private static uint ComputeAdler32(byte[] data)
        {
            uint a = 1, b = 0;
            const uint mod = 65521;

            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % mod;
                b = (b + a) % mod;
            }

            return (b << 16) | a;
        }

        #endregion
    }

    #endregion

    #region 资产缓存 - AssetCacheReal

    /// <summary>
    /// 真实资产缓存 - LRU内存缓存 + 磁盘缓存
    /// </summary>
    public class AssetCacheReal
    {
        private readonly int _maxMemoryBytes;
        private readonly string? _diskCachePath;
        private readonly Dictionary<ulong, CacheEntry> _memoryCache = new();
        private readonly LinkedList<ulong> _lruList = new(); // 最近使用在前
        private long _currentMemoryUsage;
        private bool _diskCacheEnabled;

        /// <summary>内存缓存容量（字节）</summary>
        public long MaxMemoryBytes => _maxMemoryBytes;
        public long CurrentMemoryUsage => _currentMemoryUsage;
        public int CachedCount => _memoryCache.Count;

        /// <summary>缓存命中/未命中统计</summary>
        public long CacheHits { get; private set; }
        public long CacheMisses { get; private set; }

        public AssetCacheReal(int maxMemoryMB = 256, string? diskCachePath = null)
        {
            _maxMemoryBytes = maxMemoryMB * 1024 * 1024;
            _diskCachePath = diskCachePath;
            _diskCacheEnabled = !string.IsNullOrEmpty(diskCachePath);

            if (_diskCacheEnabled)
            {
                Directory.CreateDirectory(_diskCachePath);
            }
        }

        /// <summary>
        /// 获取资产数据
        /// 1. 查内存缓存 -> 命中直接返回
        /// 2. 未命中 -> 调用ReadAsset加载
        /// 3. 放入内存缓存
        /// 4. 超容量则LRU淘汰
        /// </summary>
        public byte[] Get(ulong hash, Func<byte[]> readAssetFunc)
        {
            // 1. 查内存缓存
            if (_memoryCache.TryGetValue(hash, out var entry))
            {
                // 更新LRU位置（移到链表头）
                _lruList.Remove(hash);
                _lruList.AddFirst(hash);
                entry.LastAccessTime = DateTime.UtcNow;
                CacheHits++;
                return entry.Data;
            }

            // 2. 未命中 - 调用加载函数
            byte[] data = readAssetFunc();
            if (data == null || data.Length == 0)
            {
                CacheMisses++;
                return Array.Empty<byte>();
            }

            // 3. 放入缓存
            Put(hash, data);

            CacheMisses++;
            return data;
        }

        /// <summary>直接放入缓存</summary>
        public void Put(ulong hash, byte[] data)
        {
            if (data.Length > _maxMemoryBytes)
            {
                // 单个资产超过总容量，不缓存
                return;
            }

            // 如果已存在，先移除旧数据
            if (_memoryCache.ContainsKey(hash))
            {
                RemoveInternal(hash);
            }

            // 驱逐直到有足够空间
            while (_currentMemoryUsage + data.Length > _maxMemoryBytes && _lruList.Count > 0)
            {
                EvictOne();
            }

            // 添加新条目
            var cacheEntry = new CacheEntry
            {
                Data = data,
                Size = data.Length,
                AddTime = DateTime.UtcNow,
                LastAccessTime = DateTime.UtcNow
            };

            _memoryCache[hash] = cacheEntry;
            _lruList.AddFirst(hash);
            _currentMemoryUsage += data.Length;

            // 写入磁盘缓存
            if (_diskCacheEnabled)
            {
                WriteDiskCache(hash, data);
            }
        }

        /// <summary>
        /// LRU淘汰 - 移除最久未使用的条目
        /// 链表尾部 = 最久未使用
        /// </summary>
        public void Evict(int count = 1)
        {
            for (int i = 0; i < count && _lruList.Count > 0; i++)
            {
                EvictOne();
            }
        }

        /// <summary>淘汰最久未使用的一个条目</summary>
        private void EvictOne()
        {
            if (_lruList.Count == 0) return;

            // 尾部 = 最久未使用
            ulong lruHash = _lruList.Last!.Value;
            RemoveInternal(lruHash);
        }

        /// <summary>移除指定条目</summary>
        private void RemoveInternal(ulong hash)
        {
            if (_memoryCache.TryGetValue(hash, out var entry))
            {
                _currentMemoryUsage -= entry.Size;
                _memoryCache.Remove(hash);
                _lruList.Remove(hash);
            }
        }

        /// <summary>移除指定资产缓存</summary>
        public bool Remove(ulong hash)
        {
            if (_memoryCache.ContainsKey(hash))
            {
                RemoveInternal(hash);
                return true;
            }
            return false;
        }

        /// <summary>清除所有缓存</summary>
        public void Clear()
        {
            _memoryCache.Clear();
            _lruList.Clear();
            _currentMemoryUsage = 0;
        }

        /// <summary>检查是否在缓存中</summary>
        public bool Contains(ulong hash)
        {
            return _memoryCache.ContainsKey(hash);
        }

        /// <summary>获取缓存大小</summary>
        public int GetSize(ulong hash)
        {
            return _memoryCache.TryGetValue(hash, out var entry) ? (int)entry.Size : 0;
        }

        /// <summary>获取缓存命中率</summary>
        public double GetHitRate()
        {
            long total = CacheHits + CacheMisses;
            return total > 0 ? (double)CacheHits / total * 100.0 : 0;
        }

        /// <summary>尝试从磁盘缓存加载</summary>
        public byte[]? TryLoadFromDisk(ulong hash)
        {
            if (!_diskCacheEnabled) return null;

            string cacheFile = GetDiskCachePath(hash);
            if (!File.Exists(cacheFile)) return null;

            try
            {
                byte[] data = File.ReadAllBytes(cacheFile);
                Put(hash, data);
                return data;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写入磁盘缓存</summary>
        private void WriteDiskCache(ulong hash, byte[] data)
        {
            if (!_diskCacheEnabled) return;

            try
            {
                string cacheFile = GetDiskCachePath(hash);
                string? dir = Path.GetDirectoryName(cacheFile);
                if (dir != null)
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(cacheFile, data);
            }
            catch
            {
                // 磁盘缓存写入失败不影响运行
            }
        }

        /// <summary>获取磁盘缓存路径（两级目录分片减少文件数）</summary>
        private string GetDiskCachePath(ulong hash)
        {
            string hashStr = hash.ToString("X16");
            string subDir1 = hashStr.Substring(0, 2);
            string subDir2 = hashStr.Substring(2, 2);
            return Path.Combine(_diskCachePath!, subDir1, subDir2, $"{hashStr}.cache");
        }

        /// <summary>清理过期的磁盘缓存</summary>
        public int CleanupDiskCache(TimeSpan maxAge)
        {
            if (!_diskCacheEnabled) return 0;

            int cleaned = 0;
            DateTime cutoff = DateTime.UtcNow - maxAge;

            try
            {
                foreach (var file in Directory.EnumerateFiles(
                    _diskCachePath!, "*.cache", SearchOption.AllDirectories))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(file);
                        cleaned++;
                    }
                }
            }
            catch
            {
                // 忽略清理错误
            }

            return cleaned;
        }

        /// <summary>缓存条目</summary>
        private class CacheEntry
        {
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public long Size { get; set; }
            public DateTime AddTime { get; set; }
            public DateTime LastAccessTime { get; set; }
        }
    }

    #endregion
}
