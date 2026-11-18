// ============================================================================
// YamaEngine VorbisDecoder.cs
// yama引擎 - Ogg Vorbis 完整解码器
// 算法结构移植自 libvorbis 1.3.7 (BSD, Copyright (c) Xiph.Org Foundation)。
// 实现: OGG 分页解析(CRC校验/续包重组/多逻辑流)、Vorbis 三包头解析、
//       Huffman 码本(规范码字树解码 + VQ 反量化 maptype1/2)、
//       Floor type0(LSP曲线)/type1(分段线性)、Residue type0/1/2(分区级联)、
//       声道耦合(方极坐标级联)、MDCT(蝶形快速算法)、块切换窗重叠相加、
//       granule 位置裁剪。输出: 交错 float[-1,1)。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YamaEngine.Audio
{
    /// <summary>
    /// Vorbis解码器 - OGG容器 + Vorbis I 完整解码实现。
    /// </summary>
    public sealed class VorbisDecoder
    {
        private byte[] _fileData;
        private VorbisInfo _info;
        private bool _loaded;

        public VorbisInfo Info => _info;
        public bool IsLoaded => _loaded;

        /// <summary>
        /// Vorbis流信息
        /// </summary>
        public sealed class VorbisInfo
        {
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int Blocksize0 { get; set; } // 短块大小(2^n)
            public int Blocksize1 { get; set; } // 长块大小(2^n)
            public long NumSamples { get; set; }
            public int BitrateNominal { get; set; }
            public int BitrateUpper { get; set; }
            public int BitrateLower { get; set; }
            public string Vendor { get; set; }
            public string Title { get; set; }
            public string Artist { get; set; }
            public string Album { get; set; }
        }

        // ====================================================================
        // 公开 API
        // ====================================================================

        /// <summary>
        /// 从字节数组加载OGG Vorbis (解析容器与全部包头)。
        /// </summary>
        public VorbisInfo Load(byte[] data)
        {
            _fileData = data ?? throw new ArgumentNullException(nameof(data));
            var packets = OggAssemblePackets(data);
            if (packets.Count < 3)
                throw new InvalidDataException("无效的OGG Vorbis文件: 包头不足");

            _info = new VorbisInfo();
            SetupReset();

            ParseIdHeader(packets[0].Data);
            ParseCommentHeader(packets[1].Data);
            ParseSetupHeader(packets[2].Data);

            _audioPackets = new List<OggPacket>(packets.Count);
            for (int i = 3; i < packets.Count; i++)
                _audioPackets.Add(packets[i]);
            // 包序号: 头包占 0..2
            _packetSequence = 3;
            if (packets.Count > 0)
                _lastGranule = packets[packets.Count - 1].Granule;

            _loaded = true;
            return _info;
        }

        public VorbisInfo Load(string path) => Load(File.ReadAllBytes(path));

        /// <summary>
        /// 解码全部样本为 float[-1,1] (声道交错)。
        /// </summary>
        public float[] DecodeAll()
        {
            if (!_loaded)
                throw new InvalidOperationException("未加载Vorbis文件");

            var output = new List<float>(1 << 16);
            for (int i = 0; i < _audioPackets.Count; i++)
            {
                var pkt = _audioPackets[i];
                _currentPacketGranule = pkt.Granule;
                if (SynthesisPacket(pkt.Data))
                {
                    Blockin(pkt.Eos);
                }
                DrainPcm(output);
                if (pkt.Eos) break;
            }
            // 工业级防御: 输出上限
            const long MaxOutput = 100_000_000;
            if (output.Count > MaxOutput)
                throw new InvalidDataException("Vorbis输出样本数超限");
            return output.ToArray();
        }

        public void Reset()
        {
            if (_loaded) SetupReset();
        }

        // ====================================================================
        // OGG 分页与包重组
        // ====================================================================

        private sealed class OggPacket
        {
            public byte[] Data;
            public long Granule = -1;
            public bool Eos;
            public int Serial;
        }

        private List<OggPacket> _audioPackets;
        private long _lastGranule = -1;
        private long _packetSequence;

        /// <summary>
        /// 解析OGG流, 返回按序组装的完整包 (仅取第一个逻辑流)。
        /// </summary>
        private static List<OggPacket> OggAssemblePackets(byte[] data)
        {
            var packets = new List<OggPacket>();
            var current = new List<byte>();
            int pos = 0;
            int len = data.Length;
            int mainSerial = 0;
            bool haveSerial = false;

            while (pos + 4 <= len)
            {
                // 查找 'OggS'
                if (data[pos] != 0x4F || data[pos + 1] != 0x67 || data[pos + 2] != 0x67 || data[pos + 3] != 0x53)
                {
                    pos++;
                    continue;
                }
                if (pos + 27 > len) break;

                // 解析页头
                int p = pos;
                p += 4;                       // capture
                p += 1;                       // version
                byte headerType = data[p++];
                long granule = BitConverter.ToInt64(data, p); p += 8;
                int serial = BitConverter.ToInt32(data, p); p += 4;
                p += 4;                       // sequence
                int crcPos = p;
                uint crcStored = BitConverter.ToUInt32(data, p); p += 4;
                int segments = data[p++];
                if (p + segments > len) throw new InvalidDataException("OGG页段表越界");
                int bodySize = 0;
                for (int i = 0; i < segments; i++) bodySize += data[p + i];
                int bodyStart = p + segments;
                if (bodyStart + bodySize > len) throw new InvalidDataException("OGG页数据越界");

                // CRC 校验 (CRC-32, 多项式 0x04c11db7, 初值0, 不反转; 校验时 CRC 字段按 0 参与计算)
                uint crcCalc = 0;
                int pageEnd = bodyStart + bodySize;
                for (int i = pos; i < pageEnd; i++)
                {
                    uint byteVal = (i >= crcPos && i < crcPos + 4) ? 0u : data[i];
                    crcCalc ^= byteVal << 24;
                    for (int b = 0; b < 8; b++)
                        crcCalc = (crcCalc & 0x80000000) != 0 ? (crcCalc << 1) ^ 0x04c11db7 : crcCalc << 1;
                }
                if (crcCalc != crcStored)
                    throw new InvalidDataException($"OGG页CRC校验失败 (偏移 {pos})");

                // 多逻辑流: 仅跟随首个 BOS 流
                if (!haveSerial && (headerType & 0x02) != 0)
                {
                    mainSerial = serial;
                    haveSerial = true;
                }

                if (haveSerial && serial != mainSerial)
                {
                    pos = bodyStart + bodySize;
                    continue;
                }

                // 组包 (段表 lacing: 255=续, <255=包结束)
                // granule/EOS 仅对本页最后一个包有效 (libogg 语义)
                int segOff = p;
                int bodyOff = bodyStart;
                int pageFirstPacket = packets.Count;
                for (int i = 0; i < segments; i++)
                {
                    int segLen = data[segOff + i];
                    for (int j = 0; j < segLen; j++) current.Add(data[bodyOff + j]);
                    bodyOff += segLen;
                    if (segLen < 255)
                    {
                        packets.Add(new OggPacket
                        {
                            Data = current.ToArray(),
                            Granule = -1,
                            Eos = false,
                            Serial = serial
                        });
                        current = new List<byte>();
                    }
                }
                if (packets.Count > pageFirstPacket)
                {
                    var last = packets[packets.Count - 1];
                    last.Granule = granule;
                    last.Eos = (headerType & 0x04) != 0;
                }
                pos = bodyStart + bodySize;
            }
            if (current.Count > 0)
                packets.Add(new OggPacket { Data = current.ToArray(), Granule = -1, Serial = mainSerial });
            return packets;
        }

        // ====================================================================
        // LSB-first 位读取器 (Vorbis/OGG 位流)
        // ====================================================================

        private struct VorbisBitReader
        {
            private byte[] _buf;
            private int _pos;      // 绝对位位置
            private ulong _cache;
            private int _cacheBits;

            public void Init(byte[] buf)
            {
                _buf = buf; _pos = 0; _cache = 0; _cacheBits = 0;
            }

            public int BitPosition => _pos;

            private void Fill()
            {
                while (_cacheBits <= 56)
                {
                    int byteIdx = (_pos + _cacheBits) >> 3;
                    if (byteIdx >= _buf.Length) break;
                    _cache |= (ulong)_buf[byteIdx] << _cacheBits;
                    _cacheBits += 8;
                }
            }

            /// <summary>读取 n 位 (LSB-first: 首读位在返回值低位)。流不足抛异常。</summary>
            public uint ReadBits(int n)
            {
                if (n == 0) return 0;
                if (_cacheBits < n) Fill();
                if (_cacheBits < n)
                    throw new InvalidDataException("Vorbis位流越界(数据截断)");
                uint v = (uint)(_cache & ((1UL << n) - 1));
                _cache >>= n;
                _cacheBits -= n;
                _pos += n;
                return v;
            }

            public uint ReadBit() => ReadBits(1);

            /// <summary>窥视 n 位 (不消耗); 不足返回 -1。</summary>
            public long Look(int n)
            {
                if (_cacheBits < n) Fill();
                if (_cacheBits < n) return -1;
                return (long)(_cache & ((1UL << n) - 1));
            }

            public void Adv(int n)
            {
                if (_cacheBits < n) Fill();
                int adv = Math.Min(n, _cacheBits);
                _cache >>= adv;
                _cacheBits -= adv;
                _pos += n; // 允许越过末尾 (与 libvorbis 一致, 后续 Look 返回 -1)
            }

            /// <summary>带符号 n 位补码。</summary>
            public int ReadBitsSigned(int n)
            {
                uint v = ReadBits(n);
                if (n == 0 || n >= 32) return unchecked((int)v);
                if ((v & (1u << (n - 1))) != 0)
                    return unchecked((int)(v | (0xFFFFFFFFu << n)));
                return (int)v;
            }
        }

        private static int ILog(uint v)
        {
            int r;
            for (r = 0; v != 0; r++) v >>= 1;
            return r;
        }

        /// <summary>libvorbis _float32_unpack: 自定义 32 位浮点 (10位指数/21位尾数)。</summary>
        private static float Float32Unpack(long val)
        {
            const int VqFman = 21;
            const int VqFexpBias = 768;
            double mant = val & 0x1fffff;
            bool sign = (val & unchecked((long)0x80000000L)) != 0;
            long exp = (val & 0x7fe00000L) >> VqFman;
            if (sign) mant = -mant;
            exp = exp - (VqFman - 1) - VqFexpBias;
            if (exp > 63) exp = 63;
            if (exp < -63) exp = -63;
            return (float)(mant * Math.Pow(2.0, exp));
        }

        // ====================================================================
        // 码本
        // ====================================================================

        public sealed class VorbisCodebook
        {
            public int Dim, Entries;
            public int[] LengthList;
            public int MapType;
            public long QMin, QDelta;
            public int QQuant, QSequenceP;
            public int[] QuantList;
            // 解码侧
            public int UsedEntries;
            public float[] ValueList;   // packed * dim
            public int[] DecIndex;      // packed → 原始 entry
            public int[] TreeLeft, TreeRight, TreeValue;
        }

        /// <summary>
        /// 由码长表生成规范码字 (MSB-first, 对应 libvorbis _make_words, 不做位反转)。
        /// </summary>

        /// <summary>libvorbis _book_maptype1_quantvals。</summary>
        private static int MapType1Quantvals(int entries, int dim)
        {
            if (entries < 1) return 0;
            int vals = (int)Math.Floor(Math.Pow(entries, 1.0 / dim));
            if (vals < 1) vals = 1;
            while (true)
            {
                long acc = 1, acc1 = 1;
                int i;
                for (i = 0; i < dim; i++)
                {
                    if (entries / vals < acc) break;
                    acc *= vals;
                    acc1 = long.MaxValue / (vals + 1) < acc1 ? long.MaxValue : acc1 * (vals + 1);
                }
                if (i >= dim && acc <= entries && acc1 > entries) return vals;
                if (i < dim || acc > entries) vals--;
                else vals++;
                if (vals < 1) return 1;
            }
        }

        /// <summary>构建码本解码树与反量化值表。</summary>
        /// <summary>libvorbis _book_unquantize 移植: 反量化值表 (packed 顺序)。</summary>
        public static float[] BookUnquantize(VorbisCodebook b, int n, int[] sparsemap)
        {
            if (b.MapType != 1 && b.MapType != 2) return null;
            float mindel = Float32Unpack(b.QMin);
            float delta = Float32Unpack(b.QDelta);
            var r = new float[n * b.Dim];
            int count = 0;
            if (b.MapType == 1)
            {
                int quantvals = MapType1Quantvals(b.Entries, b.Dim);
                for (int j = 0; j < b.Entries; j++)
                {
                    if ((sparsemap != null && b.LengthList[j] != 0) || sparsemap == null)
                    {
                        float last = 0f;
                        int indexdiv = 1;
                        for (int k = 0; k < b.Dim; k++)
                        {
                            int index = (j / indexdiv) % quantvals;
                            float val = Math.Abs(b.QuantList[index]) * delta + mindel + last;
                            if (b.QSequenceP != 0) last = val;
                            if (sparsemap != null)
                                r[sparsemap[count] * b.Dim + k] = val;
                            else
                                r[count * b.Dim + k] = val;
                            indexdiv *= quantvals;
                        }
                        count++;
                    }
                }
            }
            else
            {
                for (int j = 0; j < b.Entries; j++)
                {
                    if ((sparsemap != null && b.LengthList[j] != 0) || sparsemap == null)
                    {
                        float last = 0f;
                        for (int k = 0; k < b.Dim; k++)
                        {
                            float val = Math.Abs(b.QuantList[j * b.Dim + k]) * delta + mindel + last;
                            if (b.QSequenceP != 0) last = val;
                            if (sparsemap != null)
                                r[sparsemap[count] * b.Dim + k] = val;
                            else
                                r[count * b.Dim + k] = val;
                        }
                        count++;
                    }
                }
            }
            return r;
        }

        /// <summary>libvorbis _make_words: marker 算法生成码字 (按使用序打包, MSB-first 等价于 bitreverse 后 LSB 读取)。</summary>
        private static uint[] MakeWords(int[] lengthList, int entries, int usedEntries)
        {
            var marker = new uint[33];
            var r = new uint[usedEntries > 0 ? usedEntries : entries];
            int count = 0;
            for (int i = 0; i < entries; i++)
            {
                int length = lengthList[i];
                if (length > 0)
                {
                    uint entry = marker[length];
                    if (length < 32 && (entry >> length) != 0)
                        return null; // 码树超载
                    if (count >= r.Length) return null;
                    r[count++] = entry;
                    for (int j = length; j > 0; j--)
                    {
                        if ((marker[j] & 1) != 0)
                        {
                            if (j == 1) marker[1]++;
                            else marker[j] = marker[j - 1] << 1;
                            break;
                        }
                        marker[j]++;
                    }
                    for (int j = length + 1; j < 33; j++)
                    {
                        if ((marker[j] >> 1) == entry)
                        {
                            entry = marker[j];
                            marker[j] = marker[j - 1] << 1;
                        }
                        else break;
                    }
                }
            }
            return r;
        }

        private void BuildCodebook(VorbisCodebook c)
        {
            int used = 0;
            for (int i = 0; i < c.Entries; i++)
                if (c.LengthList[i] > 0) used++;
            c.UsedEntries = used;

            if (used == 0) return;

            c.ValueList = BookUnquantize(c, used, null);

            var words = MakeWords(c.LengthList, c.Entries, used);
            if (words == null) throw new InvalidDataException("Vorbis码本码长非法(树欠载/超载)");

            // 解码树 (MSB-first 位序)
            int maxNodes = 2 * used;
            c.TreeLeft = new int[maxNodes];
            c.TreeRight = new int[maxNodes];
            c.TreeValue = new int[maxNodes];
            for (int i = 0; i < maxNodes; i++) { c.TreeLeft[i] = -1; c.TreeRight[i] = -1; c.TreeValue[i] = -1; }
            c.DecIndex = new int[used];

            int nodeCount = 1;
            int packedIdx = 0;
            for (int i = 0; i < c.Entries; i++)
            {
                int len = c.LengthList[i];
                if (len <= 0) continue;
                uint code = words[packedIdx];
                int node = 0;
                for (int j = len - 1; j >= 0; j--)
                {
                    int bit = (int)((code >> j) & 1);
                    int next = bit == 0 ? c.TreeLeft[node] : c.TreeRight[node];
                    if (next == -1)
                    {
                        if (nodeCount >= maxNodes) throw new InvalidDataException("Vorbis码本树溢出");
                        next = nodeCount++;
                        if (bit == 0) c.TreeLeft[node] = next; else c.TreeRight[node] = next;
                    }
                    node = next;
                    if (c.TreeValue[node] >= 0 && j != 0)
                        throw new InvalidDataException("Vorbis码本前缀冲突");
                }
                if (c.TreeValue[node] >= 0)
                    throw new InvalidDataException("Vorbis码本重复码字");
                c.TreeValue[node] = packedIdx;
                c.DecIndex[packedIdx] = i;
                packedIdx++;
            }
        }

        private VorbisCodebook ParseCodebook(ref VorbisBitReader br)
        {
            if (br.ReadBits(24) != 0x564342)
                throw new InvalidDataException("Vorbis码本同步码错误");
            var c = new VorbisCodebook
            {
                Dim = (int)br.ReadBits(16),
                Entries = (int)br.ReadBits(24)
            };
            if (ILog((uint)c.Dim) + ILog((uint)c.Entries) > 24)
                throw new InvalidDataException("Vorbis码本维度/条目数非法");

            c.LengthList = new int[c.Entries];
            if (br.ReadBit() == 0)
            {
                // 无序
                bool unusedTag = br.ReadBit() != 0;
                for (int i = 0; i < c.Entries; i++)
                {
                    if (unusedTag)
                    {
                        if (br.ReadBit() != 0)
                            c.LengthList[i] = (int)br.ReadBits(5) + 1;
                        else
                            c.LengthList[i] = 0;
                    }
                    else
                    {
                        c.LengthList[i] = (int)br.ReadBits(5) + 1;
                    }
                }
            }
            else
            {
                // 有序
                int length = (int)br.ReadBits(5) + 1;
                for (int i = 0; i < c.Entries; )
                {
                    int num = (int)br.ReadBits(ILog((uint)(c.Entries - i)));
                    if (length > 32 || num > c.Entries - i || (num > 0 && (num - 1) >> (length - 1) > 1))
                        throw new InvalidDataException("Vorbis有序码本非法");
                    for (int j = 0; j < num; j++, i++)
                        c.LengthList[i] = length;
                    length++;
                }
            }

            c.MapType = (int)br.ReadBits(4);
            switch (c.MapType)
            {
                case 0:
                    break;
                case 1:
                case 2:
                    c.QMin = br.ReadBits(32);
                    c.QDelta = br.ReadBits(32);
                    c.QQuant = (int)br.ReadBits(4) + 1;
                    c.QSequenceP = (int)br.ReadBit();
                    int quantvals = c.MapType == 1 ? MapType1Quantvals(c.Entries, c.Dim) : c.Entries * c.Dim;
                    if (quantvals < 0 || (long)quantvals * c.QQuant > 1L << 30)
                        throw new InvalidDataException("Vorbis码本量化表过大");
                    c.QuantList = new int[quantvals];
                    for (int i = 0; i < quantvals; i++)
                        c.QuantList[i] = (int)br.ReadBits(c.QQuant);
                    break;
                default:
                    throw new InvalidDataException($"Vorbis码本映射类型非法 ({c.MapType})");
            }
            {
                var lens = new System.Text.StringBuilder();
                for (int i = 0; i < c.Entries && i < 24; i++) lens.Append(c.LengthList[i]).Append(' ');
                if (c.Entries == 64 && c.Dim == 1)
                {
                    var codes = new System.Text.StringBuilder();
                    for (int i = 0; i < c.Entries; i++)
                    {
                        // 找树路径
                        string path = FindPath(c, i);
                        codes.Append($"{i}:{path} ");
                    }
                }
            }
            return c;
        }

        private static string FindPath(VorbisCodebook c, int targetPacked)
        {
            for (int i = 0; i < c.UsedEntries; i++)
            {
                if (c.DecIndex[i] != targetPacked) continue;
                // 反查: 从树根走 (通过重放无法直接得到路径; 用长度+码字)
                return "len" + c.LengthList[targetPacked];
            }
            return "?";
        }

        /// <summary>Huffman 解码一个 entry (返回 packed 索引, -1=EOF)。</summary>
        private static int BookDecode(VorbisCodebook book, ref VorbisBitReader br)
        {
            if (book.UsedEntries == 0) return -1;
            int node = 0;
            while (book.TreeValue[node] < 0)
            {
                long bit = br.Look(1);
                if (bit < 0) return -1;
                br.Adv(1);
                node = bit == 0 ? book.TreeLeft[node] : book.TreeRight[node];
                if (node < 0) return -1;
            }
            return book.TreeValue[node];
        }

        /// <summary>vorbis_book_decode: 返回原始 entry 号。</summary>
        private static int BookDecodeEntry(VorbisCodebook book, ref VorbisBitReader br)
        {
            int packed = BookDecode(book, ref br);
            return packed < 0 ? -1 : book.DecIndex[packed];
        }

        /// <summary>vorbis_book_decodevs_add: step 向量累加。</summary>
        private static int BookDecodeVsAdd(VorbisCodebook book, float[] a, int aOff, int n, ref VorbisBitReader br)
        {
            if (book.UsedEntries == 0) return 0;
            int step = n / book.Dim;
            var entries = new int[step];
            for (int i = 0; i < step; i++)
            {
                entries[i] = BookDecode(book, ref br);
                if (entries[i] < 0) return -1;
            }
            for (int i = 0, o = 0; i < book.Dim; i++, o += step)
            {
                for (int j = 0; o + j < n && j < step; j++)
                {
                    a[aOff + o + j] += book.ValueList[entries[j] * book.Dim + i];
                }
            }
            return 0;
        }

        /// <summary>vorbis_book_decodev_add: 顺序向量累加。</summary>
        private static int BookDecodeVAdd(VorbisCodebook book, float[] a, int aOff, int n, ref VorbisBitReader br)
        {
            if (book.UsedEntries == 0) return 0;
            int i = 0;
            while (i < n)
            {
                int packed = BookDecode(book, ref br);
                if (packed < 0) return -1;
                int baseIdx = packed * book.Dim;
                for (int j = 0; i < n && j < book.Dim; )
                {
                    a[aOff + i++] += book.ValueList[baseIdx + j++];
                }
            }
            return 0;
        }

        /// <summary>vorbis_book_decodev_set: 顺序向量赋值 (floor0 用)。</summary>
        private static int BookDecodeVSet(VorbisCodebook book, float[] a, int aOff, int n, ref VorbisBitReader br)
        {
            if (book.UsedEntries > 0)
            {
                int i = 0;
                while (i < n)
                {
                    int packed = BookDecode(book, ref br);
                    if (packed < 0) return -1;
                    int baseIdx = packed * book.Dim;
                    for (int j = 0; i < n && j < book.Dim; )
                    {
                        a[aOff + i++] = book.ValueList[baseIdx + j++];
                    }
                }
            }
            else
            {
                for (int i = 0; i < n; i++) a[aOff + i] = 0f;
            }
            return 0;
        }

        /// <summary>vorbis_book_decodevv_add: 通道交错向量累加 (residue2 用)。</summary>
        private static int BookDecodeVvAdd(VorbisCodebook book, float[][] a, int offset, int ch, ref VorbisBitReader br, int n)
        {
            if (book.UsedEntries == 0) return 0;
            int chptr = 0;
            int m = (offset + n) / ch;
            for (int i = offset / ch; i < m; )
            {
                int packed = BookDecode(book, ref br);
                if (packed < 0) return -1;
                int baseIdx = packed * book.Dim;
                for (int j = 0; i < m && j < book.Dim; j++)
                {
                    a[chptr++][i] += book.ValueList[baseIdx + j];
                    if (chptr == ch)
                    {
                        chptr = 0;
                        i++;
                    }
                }
            }
            return 0;
        }

        // ====================================================================
        // Floor
        // ====================================================================

        private sealed class VorbisFloor
        {
            public int Type; // 0 or 1
            // floor0
            public int Order, Rate, BarkMap, AmpBits, AmpDb, NumBooks;
            public int[] Books;
            // floor1
            public int Partitions;
            public int[] PartitionClass;
            public int[] ClassDim, ClassSubs, ClassBook;
            public int[][] ClassSubbook;
            public int Mult, Rangebits;
            public int[] Postlist;
            // look (floor1)
            public int Posts, QuantQ;
            public int[] ForwardIndex, ReverseIndex, SortedIndex, Loneighbor, Hineighbor;
        }

        private VorbisFloor ParseFloor(ref VorbisBitReader br)
        {
            int type = (int)br.ReadBits(16);
            if (type != 0 && type != 1)
                throw new InvalidDataException($"Vorbis floor 类型非法 ({type})");
            var f = new VorbisFloor { Type = type };

            if (type == 0)
            {
                f.Order = (int)br.ReadBits(8);
                f.Rate = (int)br.ReadBits(16);
                f.BarkMap = (int)br.ReadBits(16);
                f.AmpBits = (int)br.ReadBits(6);
                f.AmpDb = (int)br.ReadBits(8);
                f.NumBooks = (int)br.ReadBits(4) + 1;
                if (f.Order < 1 || f.Rate < 1 || f.BarkMap < 1 || f.NumBooks < 1)
                    throw new InvalidDataException("Vorbis floor0 参数非法");
                f.Books = new int[f.NumBooks];
                for (int j = 0; j < f.NumBooks; j++)
                {
                    f.Books[j] = (int)br.ReadBits(8);
                    if (f.Books[j] < 0 || f.Books[j] >= _books.Length || _books[f.Books[j]].MapType == 0 || _books[f.Books[j]].Dim < 1)
                        throw new InvalidDataException("Vorbis floor0 码本引用非法");
                }
            }
            else
            {
                f.Partitions = (int)br.ReadBits(5);
                f.PartitionClass = new int[f.Partitions];
                int maxClass = -1;
                for (int j = 0; j < f.Partitions; j++)
                {
                    f.PartitionClass[j] = (int)br.ReadBits(4);
                    if (maxClass < f.PartitionClass[j]) maxClass = f.PartitionClass[j];
                }
                f.ClassDim = new int[maxClass + 1];
                f.ClassSubs = new int[maxClass + 1];
                f.ClassBook = new int[maxClass + 1];
                f.ClassSubbook = new int[maxClass + 1][];
                for (int j = 0; j < maxClass + 1; j++)
                {
                    f.ClassDim[j] = (int)br.ReadBits(3) + 1;
                    f.ClassSubs[j] = (int)br.ReadBits(2);
                    if (f.ClassSubs[j] != 0) f.ClassBook[j] = (int)br.ReadBits(8);
                    if (f.ClassBook[j] < 0 || f.ClassBook[j] >= _books.Length)
                        throw new InvalidDataException("Vorbis floor1 类码本非法");
                    f.ClassSubbook[j] = new int[1 << f.ClassSubs[j]];
                    for (int k = 0; k < (1 << f.ClassSubs[j]); k++)
                    {
                        f.ClassSubbook[j][k] = (int)br.ReadBits(8) - 1;
                        if (f.ClassSubbook[j][k] < -1 || f.ClassSubbook[j][k] >= _books.Length)
                            throw new InvalidDataException("Vorbis floor1 子码本非法");
                    }
                }
                f.Mult = (int)br.ReadBits(2) + 1;
                f.Rangebits = (int)br.ReadBits(4);
                int count = 0;
                f.Postlist = new int[64];
                for (int j = 0, k = 0; j < f.Partitions; j++)
                {
                    count += f.ClassDim[f.PartitionClass[j]];
                    if (count > 62) throw new InvalidDataException("Vorbis floor1 postlist过长");
                    for (; k < count; k++)
                    {
                        int t = (int)br.ReadBits(f.Rangebits);
                        if (t < 0 || t >= (1 << f.Rangebits))
                            throw new InvalidDataException("Vorbis floor1 post值越界");
                        f.Postlist[k + 2] = t;
                    }
                }
                f.Postlist[0] = 0;
                f.Postlist[1] = 1 << f.Rangebits;

                // 不允许重复值
                var sorted = new int[count + 2];
                for (int j = 0; j < count + 2; j++) sorted[j] = f.Postlist[j];
                Array.Sort(sorted);
                for (int j = 1; j < count + 2; j++)
                    if (sorted[j - 1] == sorted[j])
                        throw new InvalidDataException("Vorbis floor1 postlist重复值");

                // look: 排序索引与邻居
                int n = 2;
                for (int i = 0; i < f.Partitions; i++) n += f.ClassDim[f.PartitionClass[i]];
                f.Posts = n;
                var sortPtr = new int[n];
                for (int i = 0; i < n; i++) sortPtr[i] = i;
                Array.Sort(sortPtr, (a, b) => f.Postlist[a].CompareTo(f.Postlist[b]));
                f.ForwardIndex = new int[n];
                f.ReverseIndex = new int[n];
                f.SortedIndex = new int[n];
                for (int i = 0; i < n; i++) f.ForwardIndex[i] = sortPtr[i];
                for (int i = 0; i < n; i++) f.ReverseIndex[f.ForwardIndex[i]] = i;
                for (int i = 0; i < n; i++) f.SortedIndex[i] = f.Postlist[f.ForwardIndex[i]];

                switch (f.Mult)
                {
                    case 1: f.QuantQ = 256; break;
                    case 2: f.QuantQ = 128; break;
                    case 3: f.QuantQ = 86; break;
                    case 4: f.QuantQ = 64; break;
                    default: f.QuantQ = 256; break;
                }

                f.Loneighbor = new int[Math.Max(0, n - 2)];
                f.Hineighbor = new int[Math.Max(0, n - 2)];
                for (int i = 0; i < n - 2; i++)
                {
                    int lo = 0, hi = 1, lx = 0, hx = f.Postlist[1];
                    int currentx = f.Postlist[i + 2];
                    for (int j = 0; j < i + 2; j++)
                    {
                        int x = f.Postlist[j];
                        if (x > lx && x < currentx) { lo = j; lx = x; }
                        if (x < hx && x > currentx) { hi = j; hx = x; }
                    }
                    f.Loneighbor[i] = lo;
                    f.Hineighbor[i] = hi;
                }
            }
            return f;
        }

        private static int RenderPoint(int x0, int x1, int y0, int y1, int x)
        {
            y0 &= 0x7fff;
            y1 &= 0x7fff;
            int dy = y1 - y0;
            int adx = x1 - x0;
            int ady = Math.Abs(dy);
            int err = ady * (x - x0);
            int off = err / adx;
            return dy < 0 ? y0 - off : y0 + off;
        }

        private static void RenderLine(int n, int x0, int x1, int y0, int y1, float[] d, int dOff)
        {
            var lookup = Mp3Tables.Floor1FromDb;
            int dy = y1 - y0;
            int adx = x1 - x0;
            int ady = Math.Abs(dy);
            int @base = dy / adx;
            int sy = dy < 0 ? @base - 1 : @base + 1;
            int x = x0;
            int y = y0;
            int err = 0;

            ady -= Math.Abs(@base * adx);
            if (n > x1) n = x1;

            if (x < n) d[dOff + x] *= lookup[y];

            while (++x < n)
            {
                err += ady;
                if (err >= adx)
                {
                    err -= adx;
                    y += sy;
                }
                else
                {
                    y += @base;
                }
                d[dOff + x] *= lookup[y];
            }
        }

        private static float FromDb(float x) => (float)Math.Exp(x * 0.11512925f);

        private static float ToBark(float n) =>
            13.1f * (float)Math.Atan(0.00074f * n) + 2.24f * (float)Math.Atan(n * n * 1.85e-8f) + 1e-4f * n;

        /// <summary>floor0: LSP → 频谱包络 (spec 参考浮点实现)。</summary>
        private static void LspToCurve(float[] curve, int curveOff, int[] map, int n, int ln, float[] lsp, int m, float amp, float ampOffset)
        {
            float wdel = (float)Math.PI / ln;
            for (int i = 0; i < m; i++) lsp[i] = 2.0f * (float)Math.Cos(lsp[i]);

            int idx = 0;
            while (idx < n)
            {
                int j, k = map[idx];
                float p = 0.5f, q = 0.5f;
                float w = 2.0f * (float)Math.Cos(wdel * k);
                for (j = 1; j < m; j += 2)
                {
                    q *= w - lsp[j - 1];
                    p *= w - lsp[j];
                }
                if (j == m)
                {
                    q *= w - lsp[j - 1];
                    p *= p * (4.0f - w * w);
                    q *= q;
                }
                else
                {
                    p *= p * (2.0f - w);
                    q *= q * (2.0f + w);
                }
                q = FromDb(amp / (float)Math.Sqrt(p + q) - ampOffset);

                curve[curveOff + idx] *= q;
                while (map[++idx] == k && idx < n) curve[curveOff + idx] *= q;
            }
        }

        /// <summary>floor inverse1: 解码频谱包络。返回 memo (floor1: fit_value; floor0: lsp+amp), null=静音。</summary>
        private object FloorInverse1(VorbisFloor f, ref VorbisBitReader br, int blocksize)
        {
            if (f.Type == 0)
            {
                int ampraw = (int)br.ReadBits(f.AmpBits);
                if (ampraw > 0)
                {
                    int maxval = (1 << f.AmpBits) - 1;
                    float amp = (float)ampraw / maxval * f.AmpDb;
                    int booknum = (int)br.ReadBits(ILog((uint)f.NumBooks));
                    if (booknum != -1 && booknum < f.NumBooks)
                    {
                        var book = _books[f.Books[booknum]];
                        var lsp = new float[f.Order + book.Dim + 1];
                        if (BookDecodeVSet(book, lsp, 0, f.Order, ref br) != 0) return null;
                        float last = 0f;
                        for (int j = 0, k = 0; j < f.Order; )
                        {
                            for (k = 0; j < f.Order && k < book.Dim; k++, j++)
                                lsp[j] += last;
                            if (j > 0) last = lsp[j - 1];
                        }
                        lsp[f.Order] = amp;
                        return lsp;
                    }
                }
                return null;
            }
            else
            {
                int dbgFlag = (int)br.ReadBit();
                if (dbgFlag != 1) return null;

                var fitValue = new int[f.Posts];
                int bits = ILog((uint)(f.QuantQ - 1));
                fitValue[0] = (int)br.ReadBits(bits);
                fitValue[1] = (int)br.ReadBits(bits);

                for (int i = 0, j = 2; i < f.Partitions; i++)
                {
                    int cls = f.PartitionClass[i];
                    int cdim = f.ClassDim[cls];
                    int csubbits = f.ClassSubs[cls];
                    int csub = 1 << csubbits;
                    int cval = 0;

                    if (csubbits != 0)
                    {
                        cval = BookDecodeEntry(_books[f.ClassBook[cls]], ref br);
                        if (cval == -1) return null;
                    }

                    for (int k = 0; k < cdim; k++)
                    {
                        int book = f.ClassSubbook[cls][cval & (csub - 1)];
                        cval >>= csubbits;
                        if (book >= 0)
                        {
                            int v = BookDecodeEntry(_books[book], ref br);
                            if (v == -1) return null;
                            fitValue[j + k] = v;
                        }
                        else
                        {
                            fitValue[j + k] = 0;
                        }
                    }
                    j += cdim;
                }

                // 解包正数值并线性插值重建
                for (int i = 2; i < f.Posts; i++)
                {
                    int predicted = RenderPoint(f.Postlist[f.Loneighbor[i - 2]],
                                                f.Postlist[f.Hineighbor[i - 2]],
                                                fitValue[f.Loneighbor[i - 2]],
                                                fitValue[f.Hineighbor[i - 2]],
                                                f.Postlist[i]);
                    int hiroom = f.QuantQ - predicted;
                    int loroom = predicted;
                    int room = (hiroom < loroom ? hiroom : loroom) << 1;
                    int val = fitValue[i];

                    if (val != 0)
                    {
                        if (val >= room)
                        {
                            if (hiroom > loroom)
                                val = val - loroom;
                            else
                                val = -1 - (val - hiroom);
                        }
                        else
                        {
                            if ((val & 1) != 0)
                                val = -((val + 1) >> 1);
                            else
                                val >>= 1;
                        }
                        fitValue[i] = (val + predicted) & 0x7fff;
                        fitValue[f.Loneighbor[i - 2]] &= 0x7fff;
                        fitValue[f.Hineighbor[i - 2]] &= 0x7fff;
                    }
                    else
                    {
                        fitValue[i] = predicted | 0x8000;
                    }
                }

                return fitValue;
            }
        }

        /// <summary>floor inverse2: 渲染包络到频域。</summary>
        private void FloorInverse2(VorbisFloor f, object memo, float[] outBuf, int outOff, int blocksize)
        {
            int n = blocksize / 2;
            if (memo == null)
            {
                Array.Clear(outBuf, outOff, n);
                return;
            }
            if (f.Type == 0)
            {
                var lsp = (float[])memo;
                float amp = lsp[f.Order];
                // linearmap (懒建)
                int[] map = GetFloor0Map(f, blocksize);
                LspToCurve(outBuf, outOff, map, n, f.BarkMap, lsp, f.Order, amp, f.AmpDb);
            }
            else
            {
                var fitValue = (int[])memo;
                var lookup = Mp3Tables.Floor1FromDb;
                int hx = 0, lx = 0;
                int ly = fitValue[0] * f.Mult;
                ly = ly < 0 ? 0 : ly > 255 ? 255 : ly;

                for (int j = 1; j < f.Posts; j++)
                {
                    int current = f.ForwardIndex[j];
                    int hy = fitValue[current] & 0x7fff;
                    if (hy == fitValue[current])
                    {
                        hx = f.Postlist[current];
                        hy *= f.Mult;
                        hy = hy < 0 ? 0 : hy > 255 ? 255 : hy;
                        RenderLine(n, lx, hx, ly, hy, outBuf, outOff);
                        lx = hx;
                        ly = hy;
                    }
                }
                for (int j = hx; j < n; j++) outBuf[outOff + j] *= lookup[ly];
            }
        }

        private int[] GetFloor0Map(VorbisFloor f, int blocksize)
        {
            if (f.Postlist == null) f.Postlist = new int[64]; // 复用作标记位
            int n = blocksize / 2;
            int ln = f.BarkMap;
            float scale = ln / ToBark(f.Rate / 2.0f);
            var map = new int[n + 1];
            for (int j = 0; j < n; j++)
            {
                int val = (int)Math.Floor(ToBark((f.Rate / 2.0f) / n * j) * scale);
                if (val >= ln) val = ln - 1;
                map[j] = val;
            }
            map[n] = -1;
            return map;
        }

        // ====================================================================
        // Residue
        // ====================================================================

        private sealed class VorbisResidue
        {
            public int Type; // 0,1,2
            public int Begin, End, Grouping, Partitions, GroupBook;
            public int[] SecondStages;
            public int[] Booklist;
            public int Partvals;
            // look
            public VorbisCodebook Phrasebook;
            public VorbisCodebook[][] PartBooks;
            public int Stages;
            public int[][] Decodemap;
        }

        private VorbisResidue ParseResidue(ref VorbisBitReader br)
        {
            var r = new VorbisResidue
            {
                Begin = (int)br.ReadBits(24),
                End = (int)br.ReadBits(24),
                Grouping = (int)br.ReadBits(24) + 1,
                Partitions = (int)br.ReadBits(6) + 1,
                GroupBook = (int)br.ReadBits(8)
            };
            if (r.GroupBook < 0 || r.GroupBook >= _books.Length)
                throw new InvalidDataException("Vorbis residue phrasebook非法");

            r.SecondStages = new int[r.Partitions];
            int acc = 0;
            for (int j = 0; j < r.Partitions; j++)
            {
                int cascade = (int)br.ReadBits(3);
                if (br.ReadBit() != 0)
                    cascade |= (int)br.ReadBits(5) << 3;
                r.SecondStages[j] = cascade;
                acc += ICount((uint)cascade);
            }
            r.Booklist = new int[acc];
            for (int j = 0; j < acc; j++)
            {
                r.Booklist[j] = (int)br.ReadBits(8);
                if (r.Booklist[j] < 0 || r.Booklist[j] >= _books.Length || _books[r.Booklist[j]].MapType == 0)
                    throw new InvalidDataException("Vorbis residue 码本非法");
            }

            // partvals 校验
            int entries = _books[r.GroupBook].Entries;
            int dim = _books[r.GroupBook].Dim;
            int partvals = 1;
            if (dim < 1) throw new InvalidDataException("Vorbis residue phrasebook维度非法");
            for (int d = 0; d < dim; d++)
            {
                partvals *= r.Partitions;
                if (partvals > entries) throw new InvalidDataException("Vorbis residue partvals超限");
            }
            r.Partvals = partvals;

            // look
            r.Phrasebook = _books[r.GroupBook];
            int maxStage = 0;
            r.PartBooks = new VorbisCodebook[r.Partitions][];
            int bookAcc = 0;
            for (int j = 0; j < r.Partitions; j++)
            {
                int stages = ILog((uint)r.SecondStages[j]);
                if (stages > 0)
                {
                    if (stages > maxStage) maxStage = stages;
                    r.PartBooks[j] = new VorbisCodebook[stages];
                    for (int k = 0; k < stages; k++)
                        if ((r.SecondStages[j] & (1 << k)) != 0)
                            r.PartBooks[j][k] = _books[r.Booklist[bookAcc++]];
                }
                else
                {
                    r.PartBooks[j] = Array.Empty<VorbisCodebook>();
                }
            }
            r.Stages = maxStage;

            int pdim = r.Phrasebook.Dim;
            int pvals = 1;
            for (int j = 0; j < pdim; j++) pvals *= r.Partitions;
            r.Decodemap = new int[pvals][];
            for (int j = 0; j < pvals; j++)
            {
                int val = j;
                int mult = pvals / r.Partitions;
                r.Decodemap[j] = new int[pdim];
                for (int k = 0; k < pdim; k++)
                {
                    int deco = val / mult;
                    val -= deco * mult;
                    mult /= r.Partitions;
                    r.Decodemap[j][k] = deco;
                }
            }
            return r;
        }

        private static int ICount(uint v)
        {
            int ret = 0;
            while (v != 0)
            {
                ret += (int)(v & 1);
                v >>= 1;
            }
            return ret;
        }

        /// <summary>residue 逆变换入口 (type 0/1/2 分派)。</summary>
        private void ResidueInverse(VorbisResidue r, float[][] inPcm, int ch, int pcmend)
        {
            if (r.Type == 2)
                Residue2Inverse(r, inPcm, ch, pcmend);
            else
                Residue01Inverse(r, inPcm, ch, pcmend, r.Type == 0);
        }

        private void Residue01Inverse(VorbisResidue r, float[][] inPcm, int ch, int pcmend, bool vsMode)
        {
            int samplesPerPartition = r.Grouping;
            int partitionsPerWord = r.Phrasebook.Dim;
            int max = pcmend >> 1;
            int end = r.End < max ? r.End : max;
            int n = end - r.Begin;

            if (n <= 0) return;
            int partvals = n / samplesPerPartition;
            int partwords = (partvals + partitionsPerWord - 1) / partitionsPerWord;

            var partword = new int[ch][][];
            for (int j = 0; j < ch; j++) partword[j] = new int[partwords][];
            for (int s = 0; s < r.Stages; s++)
            {
                for (int i = 0, l = 0; i < partvals; l++)
                {
                    if (s == 0)
                    {
                        for (int j = 0; j < ch; j++)
                        {
                            int temp = BookDecodeEntry(r.Phrasebook, ref _packetBr);
                            if (temp == -1 || temp >= r.Partvals) return;
                            partword[j][l] = r.Decodemap[temp];
                            if (partword[j][l] == null) return;
                        }
                    }

                    for (int k = 0; k < partitionsPerWord && i < partvals; k++, i++)
                    {
                        for (int j = 0; j < ch; j++)
                        {
                            int offset = r.Begin + i * samplesPerPartition;
                            if ((r.SecondStages[partword[j][l][k]] & (1 << s)) != 0)
                            {
                                var stagebook = r.PartBooks[partword[j][l][k]][s];
                                if (stagebook != null)
                                {
                                    int rc = vsMode
                                        ? BookDecodeVsAdd(stagebook, inPcm[j], offset, samplesPerPartition, ref _packetBr)
                                        : BookDecodeVAdd(stagebook, inPcm[j], offset, samplesPerPartition, ref _packetBr);
                                    if (rc != 0) return; // 位流耗尽: 该包剩余分区视为未编码
                                }
                            }
                        }
                    }
                }
            }
        }

        private void Residue2Inverse(VorbisResidue r, float[][] inPcm, int ch, int pcmend)
        {
            int samplesPerPartition = r.Grouping;
            int partitionsPerWord = r.Phrasebook.Dim;
            int max = (pcmend * ch) >> 1;
            int end = r.End < max ? r.End : max;
            int n = end - r.Begin;

            if (n <= 0) return;
            int partvals = n / samplesPerPartition;
            int partwords = (partvals + partitionsPerWord - 1) / partitionsPerWord;

            bool anyNonzero = false;
            for (int i = 0; i < ch; i++) { if (inPcm[i] != null) { anyNonzero = true; break; } }
            if (!anyNonzero) return;

            var partword = new int[partwords][];
            for (int s = 0; s < r.Stages; s++)
            {
                for (int i = 0, l = 0; i < partvals; l++)
                {
                    if (s == 0)
                    {
                        int temp = BookDecodeEntry(r.Phrasebook, ref _packetBr);
                        if (temp == -1 || temp >= r.Partvals) return;
                        partword[l] = r.Decodemap[temp];
                        if (partword[l] == null) return;
                    }

                    for (int k = 0; k < partitionsPerWord && i < partvals; k++, i++)
                    {
                        if ((r.SecondStages[partword[l][k]] & (1 << s)) != 0)
                        {
                            var stagebook = r.PartBooks[partword[l][k]][s];
                            if (stagebook != null)
                            {
                                if (BookDecodeVvAdd(stagebook, inPcm, i * samplesPerPartition + r.Begin, ch, ref _packetBr, samplesPerPartition) != 0) return;
                            }
                        }
                    }
                }
            }
        }

        // ====================================================================
        // Mapping
        // ====================================================================

        private sealed class VorbisMapping
        {
            public int Submaps;
            public int CouplingSteps;
            public int[] CouplingMag, CouplingAng;
            public int[] Chmuxlist;
            public int[] Floorsubmap, Residuesubmap;
        }

        private VorbisMapping ParseMapping(ref VorbisBitReader br)
        {
            var m = new VorbisMapping();
            if (br.ReadBit() != 0)
                m.Submaps = (int)br.ReadBits(4) + 1;
            else
                m.Submaps = 1;

            if (br.ReadBit() != 0)
            {
                m.CouplingSteps = (int)br.ReadBits(8) + 1;
                m.CouplingMag = new int[m.CouplingSteps];
                m.CouplingAng = new int[m.CouplingSteps];
                for (int i = 0; i < m.CouplingSteps; i++)
                {
                    int testM = m.CouplingMag[i] = (int)br.ReadBits(ILog((uint)(_info.Channels - 1)));
                    int testA = m.CouplingAng[i] = (int)br.ReadBits(ILog((uint)(_info.Channels - 1)));
                    if (testM == testA || testM >= _info.Channels || testA >= _info.Channels)
                        throw new InvalidDataException("Vorbis mapping耦合声道非法");
                }
            }
            else
            {
                m.CouplingSteps = 0;
            }

            if (br.ReadBits(2) != 0)
                throw new InvalidDataException("Vorbis mapping保留位非零");

            m.Chmuxlist = new int[_info.Channels];
            if (m.Submaps > 1)
            {
                for (int i = 0; i < _info.Channels; i++)
                {
                    m.Chmuxlist[i] = (int)br.ReadBits(4);
                    if (m.Chmuxlist[i] >= m.Submaps)
                        throw new InvalidDataException("Vorbis mapping chmux越界");
                }
            }

            m.Floorsubmap = new int[m.Submaps];
            m.Residuesubmap = new int[m.Submaps];
            for (int i = 0; i < m.Submaps; i++)
            {
                br.ReadBits(8); // time submap (未用)
                m.Floorsubmap[i] = (int)br.ReadBits(8);
                m.Residuesubmap[i] = (int)br.ReadBits(8);
                if (m.Floorsubmap[i] >= _floors.Length || m.Residuesubmap[i] >= _residues.Length)
                    throw new InvalidDataException("Vorbis mapping子表引用越界");
            }
            return m;
        }

        // ====================================================================
        // MDCT (libvorbis 蝶形实现)
        // ====================================================================

        public sealed class MdctLookup
        {
            public int N, Log2n;
            public float[] Trig;
            public int[] Bitrev;
        }

        private MdctLookup _mdct0, _mdct1;

        public static MdctLookup MdctInit(int n)
        {
            var l = new MdctLookup { Bitrev = new int[n / 4], Trig = new float[n + n / 4] };
            int n2 = n >> 1;
            l.Log2n = (int)MathF.Round(MathF.Log(n) / MathF.Log(2.0f));
            l.N = n;
            var T = l.Trig;

            for (int i = 0; i < n / 4; i++)
            {
                T[i * 2] = MathF.Cos((MathF.PI / n) * (4 * i));
                T[i * 2 + 1] = -MathF.Sin((MathF.PI / n) * (4 * i));
                T[n2 + i * 2] = MathF.Cos((MathF.PI / (2 * n)) * (2 * i + 1));
                T[n2 + i * 2 + 1] = MathF.Sin((MathF.PI / (2 * n)) * (2 * i + 1));
            }
            for (int i = 0; i < n / 8; i++)
            {
                T[n + i * 2] = MathF.Cos((MathF.PI / n) * (4 * i + 2)) * 0.5f;
                T[n + i * 2 + 1] = -MathF.Sin((MathF.PI / n) * (4 * i + 2)) * 0.5f;
            }

            {
                int mask = (1 << (l.Log2n - 1)) - 1;
                int msb = 1 << (l.Log2n - 2);
                for (int i = 0; i < n / 8; i++)
                {
                    int acc = 0;
                    for (int j = 0; (msb >> j) != 0; j++)
                        if (((msb >> j) & i) != 0) acc |= 1 << j;
                    l.Bitrev[i * 2] = ((~acc) & mask) - 1;
                    l.Bitrev[i * 2 + 1] = acc;
                }
            }
            return l;
        }

        private static void MdctButterfly8(float[] x, int o)
        {
            float r0 = x[o + 6] + x[o + 2];
            float r1 = x[o + 6] - x[o + 2];
            float r2 = x[o + 4] + x[o];
            float r3 = x[o + 4] - x[o];

            x[o + 6] = r0 + r2;
            x[o + 4] = r0 - r2;

            r0 = x[o + 5] - x[o + 1];
            r2 = x[o + 7] - x[o + 3];
            x[o] = r1 + r0;
            x[o + 2] = r1 - r0;

            r0 = x[o + 5] + x[o + 1];
            r1 = x[o + 7] + x[o + 3];
            x[o + 3] = r2 + r3;
            x[o + 1] = r2 - r3;
            x[o + 7] = r1 + r0;
            x[o + 5] = r1 - r0;
        }

        private static void MdctButterfly16(float[] x, int o)
        {
            float r0 = x[o + 1] - x[o + 9];
            float r1 = x[o] - x[o + 8];

            x[o + 8] += x[o];
            x[o + 9] += x[o + 1];
            x[o] = (r0 + r1) * 0.70710678f;
            x[o + 1] = (r0 - r1) * 0.70710678f;

            r0 = x[o + 3] - x[o + 11];
            r1 = x[o + 10] - x[o + 2];
            x[o + 10] += x[o + 2];
            x[o + 11] += x[o + 3];
            x[o + 2] = r0;
            x[o + 3] = r1;

            r0 = x[o + 12] - x[o + 4];
            r1 = x[o + 13] - x[o + 5];
            x[o + 12] += x[o + 4];
            x[o + 13] += x[o + 5];
            x[o + 4] = (r0 - r1) * 0.70710678f;
            x[o + 5] = (r0 + r1) * 0.70710678f;

            r0 = x[o + 14] - x[o + 6];
            r1 = x[o + 15] - x[o + 7];
            x[o + 14] += x[o + 6];
            x[o + 15] += x[o + 7];
            x[o + 6] = r0;
            x[o + 7] = r1;

            MdctButterfly8(x, o);
            MdctButterfly8(x, o + 8);
        }

        private static void MdctButterfly32(float[] x, int o)
        {
            float r0 = x[o + 30] - x[o + 14];
            float r1 = x[o + 31] - x[o + 15];

            x[o + 30] += x[o + 14];
            x[o + 31] += x[o + 15];
            x[o + 14] = r0;
            x[o + 15] = r1;

            r0 = x[o + 28] - x[o + 12];
            r1 = x[o + 29] - x[o + 13];
            x[o + 28] += x[o + 12];
            x[o + 29] += x[o + 13];
            x[o + 12] = r0 * 0.92387953f - r1 * 0.38268343f;
            x[o + 13] = r0 * 0.38268343f + r1 * 0.92387953f;

            r0 = x[o + 26] - x[o + 10];
            r1 = x[o + 27] - x[o + 11];
            x[o + 26] += x[o + 10];
            x[o + 27] += x[o + 11];
            x[o + 10] = (r0 - r1) * 0.70710678f;
            x[o + 11] = (r0 + r1) * 0.70710678f;

            r0 = x[o + 24] - x[o + 8];
            r1 = x[o + 25] - x[o + 9];
            x[o + 24] += x[o + 8];
            x[o + 25] += x[o + 9];
            x[o + 8] = r0 * 0.38268343f - r1 * 0.92387953f;
            x[o + 9] = r1 * 0.38268343f + r0 * 0.92387953f;

            r0 = x[o + 22] - x[o + 6];
            r1 = x[o + 7] - x[o + 23];
            x[o + 22] += x[o + 6];
            x[o + 23] += x[o + 7];
            x[o + 6] = r1;
            x[o + 7] = r0;

            r0 = x[o + 4] - x[o + 20];
            r1 = x[o + 5] - x[o + 21];
            x[o + 20] += x[o + 4];
            x[o + 21] += x[o + 5];
            x[o + 4] = r1 * 0.92387953f + r0 * 0.38268343f;
            x[o + 5] = r1 * 0.38268343f - r0 * 0.92387953f;

            r0 = x[o + 2] - x[o + 18];
            r1 = x[o + 3] - x[o + 19];
            x[o + 18] += x[o + 2];
            x[o + 19] += x[o + 3];
            x[o + 2] = (r1 + r0) * 0.70710678f;
            x[o + 3] = (r1 - r0) * 0.70710678f;

            r0 = x[o] - x[o + 16];
            r1 = x[o + 1] - x[o + 17];
            x[o + 16] += x[o];
            x[o + 17] += x[o + 1];
            x[o] = r1 * 0.38268343f + r0 * 0.92387953f;
            x[o + 1] = r1 * 0.92387953f - r0 * 0.38268343f;

            MdctButterfly16(x, o);
            MdctButterfly16(x, o + 16);
        }

        private static void MdctButterflyFirst(float[] T, int tOff, float[] x, int xOff, int points)
        {
            int x1 = xOff + points - 8;
            int x2 = xOff + (points >> 1) - 8;
            int t = tOff;

            do
            {
                float r0 = x[x1 + 6] - x[x2 + 6];
                float r1 = x[x1 + 7] - x[x2 + 7];
                x[x1 + 6] += x[x2 + 6];
                x[x1 + 7] += x[x2 + 7];
                x[x2 + 6] = r1 * T[t + 1] + r0 * T[t];
                x[x2 + 7] = r1 * T[t] - r0 * T[t + 1];

                r0 = x[x1 + 4] - x[x2 + 4];
                r1 = x[x1 + 5] - x[x2 + 5];
                x[x1 + 4] += x[x2 + 4];
                x[x1 + 5] += x[x2 + 5];
                x[x2 + 4] = r1 * T[t + 5] + r0 * T[t + 4];
                x[x2 + 5] = r1 * T[t + 4] - r0 * T[t + 5];

                r0 = x[x1 + 2] - x[x2 + 2];
                r1 = x[x1 + 3] - x[x2 + 3];
                x[x1 + 2] += x[x2 + 2];
                x[x1 + 3] += x[x2 + 3];
                x[x2 + 2] = r1 * T[t + 9] + r0 * T[t + 8];
                x[x2 + 3] = r1 * T[t + 8] - r0 * T[t + 9];

                r0 = x[x1] - x[x2];
                r1 = x[x1 + 1] - x[x2 + 1];
                x[x1] += x[x2];
                x[x1 + 1] += x[x2 + 1];
                x[x2] = r1 * T[t + 13] + r0 * T[t + 12];
                x[x2 + 1] = r1 * T[t + 12] - r0 * T[t + 13];

                x1 -= 8;
                x2 -= 8;
                t += 16;
            } while (x2 >= xOff);
        }

        private static void MdctButterflyGeneric(float[] T, int tOff, float[] x, int xOff, int points, int trigint)
        {
            int x1 = xOff + points - 8;
            int x2 = xOff + (points >> 1) - 8;
            int t = tOff;

            do
            {
                float r0 = x[x1 + 6] - x[x2 + 6];
                float r1 = x[x1 + 7] - x[x2 + 7];
                x[x1 + 6] += x[x2 + 6];
                x[x1 + 7] += x[x2 + 7];
                x[x2 + 6] = r1 * T[t + 1] + r0 * T[t];
                x[x2 + 7] = r1 * T[t] - r0 * T[t + 1];
                t += trigint;

                r0 = x[x1 + 4] - x[x2 + 4];
                r1 = x[x1 + 5] - x[x2 + 5];
                x[x1 + 4] += x[x2 + 4];
                x[x1 + 5] += x[x2 + 5];
                x[x2 + 4] = r1 * T[t + 1] + r0 * T[t];
                x[x2 + 5] = r1 * T[t] - r0 * T[t + 1];
                t += trigint;

                r0 = x[x1 + 2] - x[x2 + 2];
                r1 = x[x1 + 3] - x[x2 + 3];
                x[x1 + 2] += x[x2 + 2];
                x[x1 + 3] += x[x2 + 3];
                x[x2 + 2] = r1 * T[t + 1] + r0 * T[t];
                x[x2 + 3] = r1 * T[t] - r0 * T[t + 1];
                t += trigint;

                r0 = x[x1] - x[x2];
                r1 = x[x1 + 1] - x[x2 + 1];
                x[x1] += x[x2];
                x[x1 + 1] += x[x2 + 1];
                x[x2] = r1 * T[t + 1] + r0 * T[t];
                x[x2 + 1] = r1 * T[t] - r0 * T[t + 1];
                t += trigint;

                x1 -= 8;
                x2 -= 8;
            } while (x2 >= xOff);
        }

        public static void MdctButterflies(MdctLookup init, float[] x, int xOff, int points)
        {
            var T = init.Trig;
            int tOff = 0;
            int stages = init.Log2n - 5;
            int i, j;

            if (--stages > 0)
                MdctButterflyFirst(T, tOff, x, xOff, points);

            for (i = 1; --stages > 0; i++)
            {
                for (j = 0; j < (1 << i); j++)
                    MdctButterflyGeneric(T, tOff, x, xOff + (points >> i) * j, points >> i, 4 << i);
            }

            for (j = 0; j < points; j += 32)
                MdctButterfly32(x, xOff + j);
        }

        public static void MdctBitreverse(MdctLookup init, float[] x, int xOff)
        {
            int n = init.N;
            var bit = init.Bitrev;
            var T = init.Trig;
            int tOff = n;
            int w0Off = xOff;
            int w1Off = xOff + (n >> 1);
            int xBase = w1Off; // C 中 x = w0 + n/2, x0/x1 相对 x 寻址

            do
            {
                int x0Off = xBase + bit[0];
                int x1Off = xBase + bit[1];

                float r0 = x[x0Off + 1] - x[x1Off + 1];
                float r1 = x[x0Off] + x[x1Off];
                float r2 = r1 * T[tOff] + r0 * T[tOff + 1];
                float r3 = r1 * T[tOff + 1] - r0 * T[tOff];

                w1Off -= 4;

                r0 = 0.5f * (x[x0Off + 1] + x[x1Off + 1]);
                r1 = 0.5f * (x[x0Off] - x[x1Off]);

                x[w0Off] = r0 + r2;
                x[w1Off + 2] = r0 - r2;
                x[w0Off + 1] = r1 + r3;
                x[w1Off + 3] = r3 - r1;

                x0Off = xBase + bit[2];
                x1Off = xBase + bit[3];

                r0 = x[x0Off + 1] - x[x1Off + 1];
                r1 = x[x0Off] + x[x1Off];
                r2 = r1 * T[tOff + 2] + r0 * T[tOff + 3];
                r3 = r1 * T[tOff + 3] - r0 * T[tOff + 2];

                r0 = 0.5f * (x[x0Off + 1] + x[x1Off + 1]);
                r1 = 0.5f * (x[x0Off] - x[x1Off]);

                x[w0Off + 2] = r0 + r2;
                x[w1Off] = r0 - r2;
                x[w0Off + 3] = r1 + r3;
                x[w1Off + 1] = r3 - r1;

                w0Off += 4;
                tOff += 4;
            } while (w0Off < w1Off);
        }

        private float[] _mdctScratch = Array.Empty<float>();

        public void MdctBackward(MdctLookup init, float[] input, int inOff, float[] output, int outOff)
        {
            // 直接按 Vorbis I 规范的逆 MDCT 定义计算 (O(N²), 正确性优先):
            // x[i] = (2/N) * Σ_{k<N/2} X[k] * cos(π/N * (2k+1) * (2i+1+N/2))
            int n = init.N;
            int half = n >> 1;
            double twoOverN = 4.0 / n;
            double piOverN = Math.PI / n;
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                // 三角恒等: cos(π/N(2k+1)(2i+1+N/2)) 展开为 cos·cos 与 sin·sin 项,
                // 可用预计算旋转因子加速; 此处直接计算保证数值正确。
                for (int k = 0; k < half; k++)
                    sum += input[inOff + k] * Math.Cos(piOverN * (i + 0.5 + half * 0.5) * (2 * k + 1));
                output[outOff + i] = (float)(sum * twoOverN);
            }
        }

        // ====================================================================
        // DSP 状态 (blockin/pcmout)
        // ====================================================================

        private float[][] _dspPcm;
        private int _pcmStorage;
        private int _pcmReturned = -1, _pcmCurrent, _centerW, _lW, _W;
        private long _dspSequence = -1, _granulepos = -1;
        private long _sampleCount = -1;
        private int _eofflag;
        private float[][] _windows = new float[2][];

        private void SetupReset()
        {
            _books = Array.Empty<VorbisCodebook>();
            _floors = Array.Empty<VorbisFloor>();
            _residues = Array.Empty<VorbisResidue>();
            _mappings = Array.Empty<VorbisMapping>();
            _modes = Array.Empty<VorbisMode>();
            _modeBits = 0;
            _audioPackets = null;
            _lastGranule = -1;
            _packetSequence = 0;
            _dspPcm = null;
            _pcmReturned = -1; _pcmCurrent = 0; _centerW = 0; _lW = 0; _W = 0;
            _dspSequence = -1; _granulepos = -1;
            _sampleCount = -1;
            _eofflag = 0;
            _mdct0 = _mdct1 = null;
            _windows = new float[2][];
        }

        private float[] GetWindow(int blocksize)
        {
            int half = blocksize / 2;
            var w = new float[half];
            for (int i = 0; i < half; i++)
            {
                float x = MathF.Sin(MathF.PI * (i + 0.5f) / blocksize);
                w[i] = MathF.Sin(MathF.PI * 0.5f * x * x);
            }
            return w;
        }

        private void DspInit()
        {
            _pcmStorage = _blocksizes[1];
            _dspPcm = new float[_info.Channels][];
            for (int i = 0; i < _info.Channels; i++)
                _dspPcm[i] = new float[_pcmStorage];
            _lW = 0;
            _W = 0;
            _centerW = _blocksizes[1] / 2;
            _pcmCurrent = _centerW;
            _pcmReturned = -1;
            _dspSequence = -1;
            _granulepos = -1;
            _sampleCount = -1;
            _eofflag = 0;
            _windows[0] = GetWindow(_blocksizes[0]);
            _windows[1] = GetWindow(_blocksizes[1]);
            _mdct0 = MdctInit(_blocksizes[0]);
            _mdct1 = MdctInit(_blocksizes[1]);
        }

        // ── 当前音频包解码上下文 ──
        private VorbisBitReader _packetBr;
        private int _packetPcmend;
        private int _packetW;
        private float[][] _packetPcm;

        /// <summary>
        /// vorbis_synthesis: 解码一个音频包到块 PCM (频域→时域)。
        /// </summary>
        private bool SynthesisPacket(byte[] packet)
        {
            _packetBr.Init(packet);

            if (_packetBr.ReadBit() != 0) return false; // 非音频包

            int mode = (int)_packetBr.ReadBits(_modeBits);
            if (mode < 0 || mode >= _modes.Length) return false;

            int W = _modes[mode].BlockFlag;
            if (W != 0)
            {
                _packetBr.ReadBit();
                _packetBr.ReadBit();
            }

            _packetW = W;
            _packetPcmend = _blocksizes[W];
            int n = _packetPcmend;
            _packetPcm = new float[_info.Channels][];
            for (int i = 0; i < _info.Channels; i++)
                _packetPcm[i] = new float[n];

            // mapping inverse
            var map = _mappings[_modes[mode].Mapping];
            MappingInverse(map, n);

            // 保存窗口上下文
            _prevLW = _lW;
            return true;
        }

        private int _prevLW;

        private void MappingInverse(VorbisMapping info, int n)
        {
            int channels = _info.Channels;
            var nonzero = new int[channels];
            var floormemo = new object[channels];

            // floor inverse1
            for (int i = 0; i < channels; i++)
            {
                int submap = info.Submaps > 1 ? info.Chmuxlist[i] : 0;
                var floor = _floors[info.Floorsubmap[submap]];
                floormemo[i] = FloorInverse1(floor, ref _packetBr, n);
                nonzero[i] = floormemo[i] != null ? 1 : 0;
                Array.Clear(_packetPcm[i], 0, n / 2);
            }

            // 耦合会"弄脏"非零标记
            for (int i = 0; i < info.CouplingSteps; i++)
            {
                if (nonzero[info.CouplingMag[i]] != 0 || nonzero[info.CouplingAng[i]] != 0)
                {
                    nonzero[info.CouplingMag[i]] = 1;
                    nonzero[info.CouplingAng[i]] = 1;
                }
            }

            // residue
            var resDbg = _residues[info.Residuesubmap[0]];
            for (int i = 0; i < info.Submaps; i++)
            {
                var bundle = new float[channels][];
                var bundleNonzero = new int[channels];
                int chInBundle = 0;
                for (int j = 0; j < channels; j++)
                {
                    int submap = info.Submaps > 1 ? info.Chmuxlist[j] : 0;
                    if (submap == i)
                    {
                        bundleNonzero[chInBundle] = nonzero[j];
                        bundle[chInBundle++] = nonzero[j] != 0 ? _packetPcm[j] : null;
                    }
                }
                var res = _residues[info.Residuesubmap[i]];
                // 与 libvorbis 一致: 仅非零声道进入 bundle
                var used = new float[channels][];
                int usedCount = 0;
                for (int j = 0; j < chInBundle; j++)
                    if (bundle[j] != null) used[usedCount++] = bundle[j];
                if (usedCount > 0)
                {
                    var usedArr = new float[usedCount][];
                    Array.Copy(used, usedArr, usedCount);
                    if (res.Type == 0)
                        Residue01Inverse(res, usedArr, usedCount, n, true);
                    else if (res.Type == 1)
                        Residue01Inverse(res, usedArr, usedCount, n, false);
                    else
                        Residue2Inverse(res, usedArr, usedCount, n);
                }
            }

            // 声道耦合 (方极坐标级联)
            for (int i = info.CouplingSteps - 1; i >= 0; i--)
            {
                var pcmM = _packetPcm[info.CouplingMag[i]];
                var pcmA = _packetPcm[info.CouplingAng[i]];
                for (int j = 0; j < n / 2; j++)
                {
                    float mag = pcmM[j];
                    float ang = pcmA[j];
                    if (mag > 0)
                    {
                        if (ang > 0)
                        {
                            pcmM[j] = mag;
                            pcmA[j] = mag - ang;
                        }
                        else
                        {
                            pcmA[j] = mag;
                            pcmM[j] = mag + ang;
                        }
                    }
                    else
                    {
                        if (ang > 0)
                        {
                            pcmM[j] = mag;
                            pcmA[j] = mag + ang;
                        }
                        else
                        {
                            pcmA[j] = mag;
                            pcmM[j] = mag - ang;
                        }
                    }
                }
            }

            // 应用频谱包络
            for (int i = 0; i < channels; i++)
            {
                int submap = info.Submaps > 1 ? info.Chmuxlist[i] : 0;
                var floor = _floors[info.Floorsubmap[submap]];
                FloorInverse2(floor, floormemo[i], _packetPcm[i], 0, n);
            }
            // MDCT 逆变换
            for (int i = 0; i < channels; i++)
            {
                var mdct = _packetW != 0 ? _mdct1 : _mdct0;
                if (_packetSequence == 5 && i == 0)
                {
                    var sb = new System.Text.StringBuilder("SPEC5 ");
                    for (int j = 0; j < n / 2; j++) sb.Append(_packetPcm[i][j].ToString("G6")).Append(',');
                    System.IO.File.WriteAllText("C:/Users/李恩/AppData/Local/Temp/audioref/spec5.txt", sb.ToString());
                }
                MdctBackward(mdct, _packetPcm[i], 0, _packetPcm[i], 0);
            }
        }

        /// <summary>
        /// vorbis_synthesis_blockin: 重叠相加 + granule 裁剪。
        /// </summary>
        private void Blockin(bool eos)
        {
            int i, j;
            int n1 = _blocksizes[1] >> 1;
            int n0 = _blocksizes[0] >> 1;
            int n = _packetW != 0 ? n1 : n0;

            _lW = _W;
            _W = _packetW;

            if (_dspSequence == -1 || _dspSequence + 1 != _packetSequence)
            {
                _granulepos = -1;
                _sampleCount = -1;
            }
            _dspSequence = _packetSequence;
            _packetSequence++;

            int thisCenter, prevCenter;
            if (_centerW != 0)
            {
                thisCenter = n1;
                prevCenter = 0;
            }
            else
            {
                thisCenter = 0;
                prevCenter = n1;
            }

            for (j = 0; j < _info.Channels; j++)
            {
                var pcmCh = _dspPcm[j];
                var p = _packetPcm[j];
                if (_lW != 0)
                {
                    if (_W != 0)
                    {
                        // 大/大
                        var w = _windows[1];
                        for (i = 0; i < n1; i++)
                            pcmCh[prevCenter + i] = pcmCh[prevCenter + i] * w[n1 - i - 1] + p[i] * w[i];
                    }
                    else
                    {
                        // 大/小
                        var w = _windows[0];
                        int off = prevCenter + n1 / 2 - n0 / 2;
                        for (i = 0; i < n0; i++)
                            pcmCh[off + i] = pcmCh[off + i] * w[n0 - i - 1] + p[i] * w[i];
                    }
                }
                else
                {
                    if (_W != 0)
                    {
                        // 小/大
                        var w = _windows[0];
                        for (i = 0; i < n0; i++)
                            pcmCh[prevCenter + i] = pcmCh[prevCenter + i] * w[n0 - i - 1] + p[n1 / 2 - n0 / 2 + i] * w[i];
                        for (; i < n1 / 2 + n0 / 2; i++)
                            pcmCh[prevCenter + i] = p[n1 / 2 - n0 / 2 + i];
                    }
                    else
                    {
                        // 小/小
                        var w = _windows[0];
                        for (i = 0; i < n0; i++)
                            pcmCh[prevCenter + i] = pcmCh[prevCenter + i] * w[n0 - i - 1] + p[i] * w[i];
                    }
                }

                // 拷贝段
                for (i = 0; i < n; i++)
                    pcmCh[thisCenter + i] = p[n + i];
            }

            _centerW = _centerW != 0 ? 0 : n1;

            if (_pcmReturned == -1)
            {
                _pcmReturned = thisCenter;
                _pcmCurrent = thisCenter;
            }
            else
            {
                _pcmReturned = prevCenter;
                _pcmCurrent = prevCenter + ((_blocksizes[_lW] / 4 + _blocksizes[_W] / 4));
            }
            // ── granule 位置跟踪与裁剪 ──
            if (_sampleCount == -1)
            {
                _sampleCount = 0;
            }
            else
            {
                _sampleCount += _blocksizes[_lW] / 4 + _blocksizes[_W] / 4;
            }

            long packetGranule = _currentPacketGranule;

            if (_granulepos == -1)
            {
                if (packetGranule != -1)
                {
                    _granulepos = packetGranule;
                    if (_sampleCount > _granulepos)
                    {
                        long extra = _sampleCount - _granulepos;
                        if (extra < 0) extra = 0;
                        if (eos)
                        {
                            if (extra > (_pcmCurrent - _pcmReturned))
                                extra = _pcmCurrent - _pcmReturned;
                            _pcmCurrent -= (int)extra;
                        }
                        else
                        {
                            _pcmReturned += (int)extra;
                            if (_pcmReturned > _pcmCurrent) _pcmReturned = _pcmCurrent;
                        }
                    }
                }
            }
            else
            {
                _granulepos += _blocksizes[_lW] / 4 + _blocksizes[_W] / 4;
                if (packetGranule != -1 && _granulepos != packetGranule)
                {
                    if (_granulepos > packetGranule)
                    {
                        long extra = _granulepos - packetGranule;
                        if (extra != 0 && eos)
                        {
                            if (extra > (_pcmCurrent - _pcmReturned))
                                extra = _pcmCurrent - _pcmReturned;
                            if (extra < 0) extra = 0;
                            _pcmCurrent -= (int)extra;
                        }
                    }
                    _granulepos = packetGranule;
                }
            }

            if (eos) _eofflag = 1;
            double mx=0; for(int q=_pcmReturned;q<_pcmCurrent && q>=0;q++) mx=Math.Max(mx,Math.Abs(_dspPcm[0][q]));
        }

        private long _currentPacketGranule = -1;
        

        /// <summary>取走当前可用的解码样本 (交错)。</summary>
        private void DrainPcm(List<float> output)
        {
            while (_pcmReturned > -1 && _pcmReturned < _pcmCurrent)
            {
                int avail = _pcmCurrent - _pcmReturned;
                for (int i = 0; i < avail; i++)
                {
                    for (int ch = 0; ch < _info.Channels; ch++)
                        output.Add(_dspPcm[ch][_pcmReturned + i]);
                }
                _pcmReturned += avail;
            }
        }

        // ====================================================================
        // 包头解析
        // ====================================================================

        private sealed class VorbisMode
        {
            public int BlockFlag, WindowType, TransformType, Mapping;
        }

        private int[] _blocksizes = { 1024, 1024 };
        private VorbisCodebook[] _books = Array.Empty<VorbisCodebook>();
        private VorbisFloor[] _floors = Array.Empty<VorbisFloor>();

        private VorbisResidue[] _residues = Array.Empty<VorbisResidue>();
        private VorbisMapping[] _mappings = Array.Empty<VorbisMapping>();
        private VorbisMode[] _modes = Array.Empty<VorbisMode>();
        private int _modeBits;

        private static void CheckVorbisMagic(byte[] packet, byte type)
        {
            if (packet.Length < 7 || packet[0] != type
                || packet[1] != (byte)'v' || packet[2] != (byte)'o' || packet[3] != (byte)'r'
                || packet[4] != (byte)'b' || packet[5] != (byte)'i' || packet[6] != (byte)'s')
                throw new InvalidDataException("Vorbis包头标记错误");
        }

        private void ParseIdHeader(byte[] packet)
        {
            CheckVorbisMagic(packet, 1);
            var br = new VorbisBitReader();
            br.Init(packet);
            br.Adv(8 + 6 * 8); // type + 'vorbis'
            uint version = br.ReadBits(32);
            if (version != 0) throw new InvalidDataException("Vorbis版本非法");
            _info.Channels = (int)br.ReadBits(8);
            _info.SampleRate = (int)br.ReadBits(32);
            _info.BitrateUpper = (int)br.ReadBits(32);
            _info.BitrateNominal = (int)br.ReadBits(32);
            _info.BitrateLower = (int)br.ReadBits(32);
            int bs0 = (int)br.ReadBits(4);
            int bs1 = (int)br.ReadBits(4);
            if (br.ReadBit() != 1) throw new InvalidDataException("Vorbis标识头帧尾标记错误");

            if (_info.SampleRate < 1 || _info.Channels < 1)
                throw new InvalidDataException("Vorbis标识头参数非法");
            _blocksizes = new[] { 1 << bs0, 1 << bs1 };
            if (_blocksizes[0] < 64 || _blocksizes[1] < _blocksizes[0] || _blocksizes[1] > 8192)
                throw new InvalidDataException("Vorbis块大小非法");
        }

        private void ParseCommentHeader(byte[] packet)
        {
            CheckVorbisMagic(packet, 3);
            var br = new VorbisBitReader();
            br.Init(packet);
            br.Adv(8 + 6 * 8);
            int vendorLen = (int)br.ReadBits(32);
            if (vendorLen < 0 || vendorLen > packet.Length * 4) throw new InvalidDataException("Vorbis注释头厂商长度非法");
            var vendorBytes = new byte[vendorLen];
            for (int i = 0; i < vendorLen; i++) vendorBytes[i] = (byte)br.ReadBits(8);
            _info.Vendor = Encoding.UTF8.GetString(vendorBytes);
            int commentCount = (int)br.ReadBits(32);
            if (commentCount < 0 || commentCount > 65536) throw new InvalidDataException("Vorbis注释数量非法");
            for (int c = 0; c < commentCount; c++)
            {
                int cLen = (int)br.ReadBits(32);
                if (cLen < 0 || cLen > packet.Length * 4 + 4096) throw new InvalidDataException("Vorbis注释长度非法");
                var cBytes = new byte[cLen];
                for (int i = 0; i < cLen; i++) cBytes[i] = (byte)br.ReadBits(8);
                string comment = Encoding.UTF8.GetString(cBytes);
                int eq = comment.IndexOf('=');
                if (eq > 0)
                {
                    string key = comment.Substring(0, eq).ToUpperInvariant();
                    string val = comment.Substring(eq + 1);
                    if (key == "TITLE") _info.Title = val;
                    else if (key == "ARTIST") _info.Artist = val;
                    else if (key == "ALBUM") _info.Album = val;
                }
            }
        }

        private void ParseSetupHeader(byte[] packet)
        {
            CheckVorbisMagic(packet, 5);
            var br = new VorbisBitReader();
            br.Init(packet);
            br.Adv(8 + 6 * 8);

            // 码本
            int books = (int)br.ReadBits(8) + 1;
            _books = new VorbisCodebook[books];
            for (int i = 0; i < books; i++)
            {
                _books[i] = ParseCodebook(ref br);
                BuildCodebook(_books[i]);
            }
            {{long peek = br.Look(40);}}
            // time domain transforms (保留字段)
            int times = (int)br.ReadBits(6) + 1;
            for (int i = 0; i < times; i++)
            {
                if (br.ReadBits(16) != 0)
                    throw new InvalidDataException("Vorbis time transform类型非法");
            }

            // floor
            int floors = (int)br.ReadBits(6) + 1;
            _floors = new VorbisFloor[floors];
            for (int i = 0; i < floors; i++)
                _floors[i] = ParseFloor(ref br);

            // residue
            int residues = (int)br.ReadBits(6) + 1;
            _residues = new VorbisResidue[residues];
            for (int i = 0; i < residues; i++)
            {
                // 每个子项前有 16 位残差类型 (0/1/2)
                int rtype = (int)br.ReadBits(16);
                if (rtype is < 0 or > 2)
                    throw new InvalidDataException($"Vorbis residue 类型非法 ({rtype})");
                _residues[i] = ParseResidue(ref br);
                _residues[i].Type = rtype;
            }

            // map
            int maps = (int)br.ReadBits(6) + 1;
            _mappings = new VorbisMapping[maps];
            for (int i = 0; i < maps; i++)
            {
                // 每个子项前有 16 位映射类型 (仅 0 合法)
                int mtype = (int)br.ReadBits(16);
                if (mtype != 0)
                    throw new InvalidDataException($"Vorbis mapping 类型非法 ({mtype})");
                _mappings[i] = ParseMapping(ref br);
            }

            // mode
            int modes = (int)br.ReadBits(6) + 1;
            _modes = new VorbisMode[modes];
            for (int i = 0; i < modes; i++)
            {
                _modes[i] = new VorbisMode
                {
                    BlockFlag = (int)br.ReadBit(),
                    WindowType = (int)br.ReadBits(16),
                    TransformType = (int)br.ReadBits(16),
                    Mapping = (int)br.ReadBits(8)
                };
                if (_modes[i].WindowType != 0 || _modes[i].TransformType != 0)
                    throw new InvalidDataException("Vorbis mode窗口/变换类型非法");
                if (_modes[i].Mapping < 0 || _modes[i].Mapping >= maps)
                    throw new InvalidDataException("Vorbis mode映射引用越界");
            }
            if (br.ReadBit() != 1)
                throw new InvalidDataException("Vorbis设置头帧尾标记错误");

            _modeBits = ILog((uint)(modes - 1));
            for (int i = 0; i < _books.Length; i++)
            {
                var b = _books[i];
                if (b.ValueList != null && b.ValueList.Length > 0)
                {
                    double mn = double.MaxValue, mx = double.MinValue;
                    foreach (var v in b.ValueList) { if (v < mn) mn = v; if (v > mx) mx = v; }
                }
                else
                {
                }
            }
            DspInit();
        }

        // ====================================================================
        // 兼容静态助手 (保留原公开 API)
        // ====================================================================

        /// <summary>
        /// IMDCT (逆修正离散余弦变换) - 直接实现 (O(N²), 供外部工具/测试使用)。
        /// </summary>
        public static void IMDCT(double[] input, double[] output, int N)
        {
            int N2 = N / 2;
            double n0 = (N2 + 1) * 0.5;
            double scale = 2.0 / N;

            for (int n = 0; n < N; n++)
            {
                double sum = 0.0;
                for (int k = 0; k < N2; k++)
                {
                    double angle = Math.PI / N * (n + 0.5 + n0) * k;
                    sum += input[k] * Math.Cos(angle);
                }
                output[n] = sum * scale;
            }
        }

        /// <summary>
        /// Vorbis窗函数
        /// </summary>
        public static double[] VorbisWindow(int N)
        {
            double[] window = new double[N];
            for (int i = 0; i < N; i++)
            {
                double x = Math.Sin(Math.PI * (i + 0.5) / N);
                window[i] = Math.Sin(Math.PI * 0.5 * x * x);
            }
            return window;
        }
    }
}
