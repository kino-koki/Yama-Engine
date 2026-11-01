// ============================================================================
// YamaEngine - MicroCluster Data Compression
// yama引擎 - MicroCluster 数据压缩系统
// ============================================================================
// 描述: 提供 MicroCluster 虚拟化几何体所需的数据压缩与解压缩功能。
//       包含位置量化(16bit)、法线八面体编码(16bit)、UV 16bit 编码、
//       位流读写器、Cluster 级别压缩、以及 .microcluster 文件格式的
//       序列化与反序列化。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace YamaEngine.MicroCluster
{
    // ========================================================================
    // MicroClusterCluster - MicroCluster 聚类数据结构
    // ========================================================================

    /// <summary>
    /// 表示一个 MicroCluster 聚类，包含一组连续的三角形及其顶点数据。
    /// 聚类是 MicroCluster 管线中视锥剔除和 LOD 切换的基本单位。
    /// </summary>
    public class MicroClusterCluster
    {
        /// <summary>聚类唯一标识符。</summary>
        public int ClusterId { get; set; }

        /// <summary>聚类中的顶点数据列表 (Position, Normal, UV)。</summary>
        public List<ClusterVertex> Vertices { get; set; } = new List<ClusterVertex>();

        /// <summary>聚类中的三角形索引列表（每3个索引构成一个三角形）。</summary>
        public List<ushort> Indices { get; set; } = new List<ushort>();

        /// <summary>聚类的本地空间包围盒最小值。</summary>
        public Vector3 BoundsMin { get; set; }

        /// <summary>聚类的本地空间包围盒最大值。</summary>
        public Vector3 BoundsMax { get; set; }

        /// <summary>聚类所属的 LOD 层级（0=最高精度）。</summary>
        public int LODLevel { get; set; }

        /// <summary>父聚类ID（-1表示根聚类）。</summary>
        public int ParentClusterId { get; set; } = -1;

        /// <summary>父聚类（运行时由可见性/LOD 系统填充）。</summary>
        public MicroClusterCluster? ParentCluster { get; set; }

        /// <summary>聚类的世界/本地空间包围盒。</summary>
        public YamaEngine.Rendering.BoundingBox Bounds
            => new YamaEngine.Rendering.BoundingBox(BoundsMin, BoundsMax);

        /// <summary>子聚类ID列表。</summary>
        public List<int> ChildClusterIds { get; set; } = new List<int>();

        /// <summary>材质索引。</summary>
        public int MaterialIndex { get; set; }

        /// <summary>该聚类的平均屏幕空间误差。</summary>
        public float ScreenSpaceError { get; set; }

        /// <summary>几何误差（LOD 简化误差，用于层级选择）。</summary>
        public float GeometricError { get; set; }

        /// <summary>LOD 层级别名（与 <see cref="LODLevel"/> 互通，兼容上层命名）。</summary>
        public int LodLevel
        {
            get => LODLevel;
            set => LODLevel = value;
        }

        /// <summary>聚类 ID 别名（与 <see cref="ClusterId"/> 互通）。</summary>
        public int Id
        {
            get => ClusterId;
            set => ClusterId = value;
        }

        /// <summary>该聚类在全局三角形缓冲中的起始三角形索引。</summary>
        public int FirstTriangle { get; set; }

        /// <summary>父分组标识。</summary>
        public int ParentGroup { get; set; } = -1;

        /// <summary>聚类持有的三角形集合（聚类器/虚拟几何体使用）。</summary>
        public List<MicroCluster.Triangle> Triangles { get; set; } = new List<MicroCluster.Triangle>();

        /// <summary>根据当前三角形/顶点信息重算包围盒。</summary>
        public void CalculateBounds()
        {
            if (Vertices == null || Vertices.Count == 0) return;
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            foreach (var v in Vertices)
            {
                mn = Vector3.Min(mn, v.Position);
                mx = Vector3.Max(mx, v.Position);
            }
            BoundsMin = mn; BoundsMax = mx;
        }

        /// <summary>计算父节点几何误差（占位实现）。</summary>
        public void CalculateParentError() { }

        private int _triangleCount = -1;

        /// <summary>获取三角形数量（可被上层重写赋值）。</summary>
        public int TriangleCount
        {
            get => _triangleCount >= 0 ? _triangleCount : (Indices != null ? Indices.Count / 3 : 0);
            set => _triangleCount = value;
        }

        private Vector3? _boundsCenterOverride;

        /// <summary>获取/设置聚类包围盒中心。</summary>
        public Vector3 BoundsCenter
        {
            get => _boundsCenterOverride
                ?? new Vector3(
                    (BoundsMin.X + BoundsMax.X) * 0.5f,
                    (BoundsMin.Y + BoundsMax.Y) * 0.5f,
                    (BoundsMin.Z + BoundsMax.Z) * 0.5f);
            set => _boundsCenterOverride = value;
        }

        private float? _boundsRadiusOverride;

        /// <summary>获取/设置聚类包围盒半径 (set 时覆盖计算值)。</summary>
        public float BoundsRadius
        {
            get
            {
                if (_boundsRadiusOverride.HasValue) return _boundsRadiusOverride.Value;
                Vector3 ext = new Vector3(
                    BoundsMax.X - BoundsMin.X,
                    BoundsMax.Y - BoundsMin.Y,
                    BoundsMax.Z - BoundsMin.Z);
                return ext.Length() * 0.5f;
            }
            set => _boundsRadiusOverride = value;
        }

        private Vector3 _normalConeAxis;

        /// <summary>法线锥轴 (归一化平均法线)。</summary>
        public Vector3 NormalConeAxis
        {
            get => _normalConeAxis;
            set => _normalConeAxis = value;
        }

        private float _normalConeCos = 1f;

        /// <summary>法线锥最大半角的余弦。</summary>
        public float NormalConeCos
        {
            get => _normalConeCos;
            set => _normalConeCos = value;
        }
    }

    // ========================================================================
    // ClusterVertex - 聚类顶点数据
    // ========================================================================

    /// <summary>
    /// 聚类内的单个顶点数据。
    /// </summary>
    public struct ClusterVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;

        public ClusterVertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            UV = uv;
        }
    }

    // ========================================================================
    // Mesh - 简化的网格数据结构
    // ========================================================================

    /// <summary>
    /// 简化的网格数据结构，用于压缩/解压缩接口。
    /// </summary>
    public class Mesh
    {
        /// <summary>网格名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>顶点位置列表。</summary>
        public List<Vector3> Positions { get; set; } = new List<Vector3>();

        /// <summary>顶点位置别名（兼容聚类器命名）。</summary>
        public List<Vector3> Vertices => Positions;

        /// <summary>顶点法线列表。</summary>
        public List<Vector3> Normals { get; set; } = new List<Vector3>();

        /// <summary>纹理坐标列表。</summary>
        public List<Vector2> UVs { get; set; } = new List<Vector2>();

        /// <summary>三角形索引列表。</summary>
        public List<int> Indices { get; set; } = new List<int>();

        /// <summary>材质索引列表（每个三角形对应一个材质）。</summary>
        public List<int> MaterialIndices { get; set; } = new List<int>();

        /// <summary>包围盒最小值。</summary>
        public Vector3 BoundsMin { get; set; }

        /// <summary>包围盒最大值。</summary>
        public Vector3 BoundsMax { get; set; }

        /// <summary>获取顶点数量。</summary>
        public int VertexCount => Positions.Count;

        /// <summary>获取三角形数量。</summary>
        public int TriangleCount => Indices.Count / 3;
    }

    // ========================================================================
    // 向量类型统一使用 System.Numerics.Vector3 / Vector2
    // (项目全局 using System.Numerics，无需在此定义重复类型)
    // ========================================================================

    // ========================================================================
    // BitStream - 位流读写器
    // ========================================================================

    /// <summary>
    /// 位流读写器，支持按位写入和读取数据。
    /// 用于 MicroCluster 聚类数据的高效压缩。
    /// </summary>
    public class BitStream
    {
        private byte[] _buffer;
        private int _bitPosition; // 当前读写位位置
        private int _capacityBits;

        /// <summary>获取位流中的总位数。</summary>
        public int LengthBits => _bitPosition;

        /// <summary>获取位流中的总字节数（向上取整）。</summary>
        public int LengthBytes => (_bitPosition + 7) / 8;

        /// <summary>
        /// 创建一个新的 BitStream 实例，指定初始容量（字节数）。
        /// </summary>
        public BitStream(int initialCapacityBytes = 256)
        {
            _buffer = new byte[initialCapacityBytes];
            _bitPosition = 0;
            _capacityBits = initialCapacityBytes * 8;
        }

        /// <summary>
        /// 从已有的字节数组创建 BitStream（读取模式）。
        /// </summary>
        public BitStream(byte[] data)
        {
            _buffer = data;
            _bitPosition = 0;
            _capacityBits = data.Length * 8;
        }

        /// <summary>确保缓冲区有足够的容量。</summary>
        private void EnsureCapacity(int bitsNeeded)
        {
            if (_bitPosition + bitsNeeded <= _capacityBits)
                return;

            int newCapacityBytes = _buffer.Length * 2;
            while (newCapacityBytes * 8 < _bitPosition + bitsNeeded)
                newCapacityBytes *= 2;

            byte[] newBuffer = new byte[newCapacityBytes];
            Array.Copy(_buffer, newBuffer, _buffer.Length);
            _buffer = newBuffer;
            _capacityBits = newCapacityBytes * 8;
        }

        /// <summary>
        /// 写入指定位数的值。
        /// </summary>
        /// <param name="value">要写入的值。</param>
        /// <param name="bitCount">写入的位数（1-32）。</param>
        public void WriteBits(uint value, int bitCount)
        {
            if (bitCount <= 0 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount), "bitCount必须在1-32之间");

            EnsureCapacity(bitCount);

            for (int i = 0; i < bitCount; i++)
            {
                int byteIndex = _bitPosition / 8;
                int bitIndex = _bitPosition % 8;
                int bitValue = (int)((value >> i) & 1);

                if (bitValue != 0)
                    _buffer[byteIndex] |= (byte)(1 << bitIndex);
                else
                    _buffer[byteIndex] &= (byte)~(1 << bitIndex);

                _bitPosition++;
            }
        }

        /// <summary>
        /// 读取指定位数的值。
        /// </summary>
        /// <param name="bitCount">读取的位数（1-32）。</param>
        /// <returns>读取到的值。</returns>
        public uint ReadBits(int bitCount)
        {
            if (bitCount <= 0 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount), "bitCount必须在1-32之间");

            if (_bitPosition + bitCount > _capacityBits)
                throw new InvalidOperationException("读取超出位流边界");

            uint result = 0;

            for (int i = 0; i < bitCount; i++)
            {
                int byteIndex = _bitPosition / 8;
                int bitIndex = _bitPosition % 8;
                int bitValue = (_buffer[byteIndex] >> bitIndex) & 1;

                result |= (uint)(bitValue << i);
                _bitPosition++;
            }

            return result;
        }

        /// <summary>
        /// 以大端序写入16位无符号整数。
        /// </summary>
        public void WriteUInt16BE(ushort value)
        {
            WriteBits((uint)(value >> 8), 8);
            WriteBits((uint)(value & 0xFF), 8);
        }

        /// <summary>
        /// 以大端序写入32位无符号整数。
        /// </summary>
        public void WriteUInt32BE(uint value)
        {
            WriteBits((value >> 24) & 0xFF, 8);
            WriteBits((value >> 16) & 0xFF, 8);
            WriteBits((value >> 8) & 0xFF, 8);
            WriteBits(value & 0xFF, 8);
        }

        /// <summary>
        /// 以大端序读取16位无符号整数。
        /// </summary>
        public ushort ReadUInt16BE()
        {
            uint hi = ReadBits(8);
            uint lo = ReadBits(8);
            return (ushort)((hi << 8) | lo);
        }

        /// <summary>
        /// 以大端序读取32位无符号整数。
        /// </summary>
        public uint ReadUInt32BE()
        {
            uint b0 = ReadBits(8);
            uint b1 = ReadBits(8);
            uint b2 = ReadBits(8);
            uint b3 = ReadBits(8);
            return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        }

        /// <summary>
        /// 获取位流的有效字节数组（截断到实际使用的长度）。
        /// </summary>
        public byte[] ToArray()
        {
            int byteCount = LengthBytes;
            byte[] result = new byte[byteCount];
            Array.Copy(_buffer, result, byteCount);
            return result;
        }
    }

    // ========================================================================
    // PositionQuantizer - 位置量化器
    // ========================================================================

    /// <summary>
    /// 位置量化器，将 float3 位置量化到 16bit 精度。
    /// 基于本地原点（包围盒最小值）和量化缩放因子，
    /// 将 32 位浮点位置压缩为 3 x 16bit = 48bit。
    /// </summary>
    public class PositionQuantizer
    {
        private readonly Vector3 _origin;
        private readonly Vector3 _scale;
        private readonly float _quantRange;

        /// <summary>量化范围的最大值（ushort.MaxValue）。</summary>
        private const float MAX_QUANT = 65535.0f;

        /// <summary>
        /// 创建位置量化器，基于给定的包围盒范围。
        /// </summary>
        /// <param name="boundsMin">包围盒最小值（量化原点）。</param>
        /// <param name="boundsMax">包围盒最大值。</param>
        public PositionQuantizer(Vector3 boundsMin, Vector3 boundsMax)
        {
            _origin = boundsMin;
            _quantRange = MAX_QUANT;

            float dx = boundsMax.X - boundsMin.X;
            float dy = boundsMax.Y - boundsMin.Y;
            float dz = boundsMax.Z - boundsMin.Z;

            // 避免除零：如果某轴范围为零，设为1
            float safeDx = Math.Max(dx, 1e-6f);
            float safeDy = Math.Max(dy, 1e-6f);
            float safeDz = Math.Max(dz, 1e-6f);

            _scale = new Vector3(
                _quantRange / safeDx,
                _quantRange / safeDy,
                _quantRange / safeDz);
        }

        /// <summary>
        /// 将 float3 位置量化为三个 16-bit 值。
        /// </summary>
        /// <param name="position">世界空间或本地空间位置。</param>
        /// <param name="x">量化后的X分量（0-65535）。</param>
        /// <param name="y">量化后的Y分量（0-65535）。</param>
        /// <param name="z">量化后的Z分量（0-65535）。</param>
        public void Quantize(Vector3 position, out ushort x, out ushort y, out ushort z)
        {
            float rx = (position.X - _origin.X) * _scale.X;
            float ry = (position.Y - _origin.Y) * _scale.Y;
            float rz = (position.Z - _origin.Z) * _scale.Z;

            x = (ushort)Math.Clamp((int)Math.Round(rx), 0, 65535);
            y = (ushort)Math.Clamp((int)Math.Round(ry), 0, 65535);
            z = (ushort)Math.Clamp((int)Math.Round(rz), 0, 65535);
        }

        /// <summary>
        /// 将三个 16-bit 量化值反量化为 float3 位置。
        /// </summary>
        public Vector3 Dequantize(ushort x, ushort y, ushort z)
        {
            float px = (float)x / _scale.X + _origin.X;
            float py = (float)y / _scale.Y + _origin.Y;
            float pz = (float)z / _scale.Z + _origin.Z;
            return new Vector3(px, py, pz);
        }

        /// <summary>获取量化原点（包围盒最小值）。</summary>
        public Vector3 Origin => _origin;
    }

    // ========================================================================
    // NormalOctahedralEncoder - 法线八面体编码器
    // ========================================================================

    /// <summary>
    /// 法线八面体编码/解码器。
    /// 将单位法线向量（3 x float32 = 96bit）编码为
    /// 八面体坐标（2 x float16 = 32bit），压缩比 3:1。
    /// 
    /// 八面体编码原理:
    /// 1. 将单位球面投影到正八面体表面
    /// 2. 展开八面体到二维平面
    /// 3. 将 (x,y) 坐标量化到 16bit
    /// </summary>
    public static class NormalOctahedralEncoder
    {
        /// <summary>
        /// 将法线向量编码为八面体坐标，量化为两个 16-bit 值。
        /// </summary>
        /// <param name="normal">输入法线（必须为单位向量）。</param>
        /// <param name="octX">编码后的X坐标（0-65535）。</param>
        /// <param name="octY">编码后的Y坐标（0-65535）。</param>
        public static void Encode(Vector3 normal, out ushort octX, out ushort octY)
        {
            // 步骤1: 归一化（确保是单位向量）
            float len = normal.Length();
            if (len < 1e-6f)
            {
                octX = 32768; // 对应 (0, 0, 1) 方向
                octY = 32768;
                return;
            }

            float nx = normal.X / len;
            float ny = normal.Y / len;
            float nz = normal.Z / len;

            // 步骤2: 八面体投影
            // 投影到xy平面，根据z分量折叠下半球到上半球
            float absSum = Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz);
            float invLen = 1.0f / absSum;

            float ox = nx * invLen;
            float oy = ny * invLen;

            // 步骤3: 下半球折叠
            // 如果z < 0，将点从(t,1-t)映射到(1-|t|, sign(t)*(1-|t|))
            if (nz < 0.0f)
            {
                float tx = ox;
                float ty = oy;

                // 将菱形下半部分翻折到上半部分
                ox = (1.0f - Math.Abs(ty)) * Math.Sign(tx);
                oy = (1.0f - Math.Abs(tx)) * Math.Sign(ty);
            }

            // 步骤4: 映射到 [0, 1] 并量化到 16-bit
            // ox, oy 范围 [-1, 1] -> [0, 65535]
            octX = (ushort)Math.Clamp(
                (int)Math.Round((ox * 0.5f + 0.5f) * 65535.0f), 0, 65535);
            octY = (ushort)Math.Clamp(
                (int)Math.Round((oy * 0.5f + 0.5f) * 65535.0f), 0, 65535);
        }

        /// <summary>
        /// 将两个 16-bit 八面体坐标解码为法线向量。
        /// </summary>
        /// <param name="octX">编码的X坐标（0-65535）。</param>
        /// <param name="octY">编码的Y坐标（0-65535）。</param>
        /// <returns>解码后的单位法线向量。</returns>
        public static Vector3 Decode(ushort octX, ushort octY)
        {
            // 步骤1: 反量化到 [-1, 1]
            float ox = (float)octX / 65535.0f * 2.0f - 1.0f;
            float oy = (float)octY / 65535.0f * 2.0f - 1.0f;

            // 步骤2: 还原下半球折叠
            float nx, ny, nz;

            // 检查是否在折叠区域
            // 折叠条件: |ox| + |oy| > 1
            if (Math.Abs(ox) + Math.Abs(oy) > 1.0f)
            {
                // 反折叠
                float tx = (1.0f - Math.Abs(oy)) * Math.Sign(ox);
                float ty = (1.0f - Math.Abs(ox)) * Math.Sign(oy);
                nx = tx;
                ny = ty;
                nz = -1.0f;
            }
            else
            {
                nx = ox;
                ny = oy;
                nz = 1.0f;
            }

            // 步骤3: 反投影回单位球面
            // 从八面体表面恢复到球面: n = (x, y, 1-|x|-|y|) 然后归一化
            nz = 1.0f - Math.Abs(nx) - Math.Abs(ny);

            float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f)
                return new Vector3(0, 0, 1);

            nx /= len;
            ny /= len;
            nz /= len;

            // 恢复原始z方向
            if (nz < 0.0f)
            {
                // 此情况在折叠处理中已经覆盖
            }

            return new Vector3(nx, ny, nz);
        }

        /// <summary>
        /// 将法线编码为单个 32-bit 值（低16位=X，高16位=Y）。
        /// 适用于直接存入缓冲区。
        /// </summary>
        public static uint EncodeToUInt32(Vector3 normal)
        {
            Encode(normal, out ushort octX, out ushort octY);
            return ((uint)octY << 16) | octX;
        }

        /// <summary>
        /// 从单个 32-bit 值解码法线。
        /// </summary>
        public static Vector3 DecodeFromUInt32(uint packed)
        {
            ushort octX = (ushort)(packed & 0xFFFF);
            ushort octY = (ushort)((packed >> 16) & 0xFFFF);
            return Decode(octX, octY);
        }
    }

    // ========================================================================
    // MicroClusterFileHeader - .microcluster 文件头
    // ========================================================================

    /// <summary>
    /// MicroCluster 文件格式的全局文件头。
    /// 每个有效的 .microcluster 文件必须以此头部开始。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MicroClusterFileHeader
    {
        /// <summary>文件魔数标识，固定为 "NANI" (0x494E414E)。</summary>
        public uint Magic;

        /// <summary>文件格式版本号。当前为 1。</summary>
        public uint Version;

        /// <summary>文件中包含的网格总数。</summary>
        public uint MeshCount;

        /// <summary>文件中包含的聚类总数。</summary>
        public uint ClusterCount;

        /// <summary>文件的总数据大小（不含此头部）。</summary>
        public uint TotalDataSize;

        /// <summary>标志位（保留扩展用）。</summary>
        public uint Flags;

        /// <summary>文件头大小（字节），固定为 32。</summary>
        public static readonly int HeaderSize = 28;

        /// <summary>文件魔数常量: "NANI" (little-endian)。</summary>
        public static readonly uint NANITE_MAGIC = 0x494E414E;

        /// <summary>当前文件格式版本。</summary>
        public static readonly uint CURRENT_VERSION = 1;

        /// <summary>标志位: 启用法线压缩。</summary>
        public const uint FLAG_COMPRESSED_NORMALS = 0x01;

        /// <summary>标志位: 启用位置量化。</summary>
        public const uint FLAG_QUANTIZED_POSITIONS = 0x02;

        /// <summary>标志位: 启用索引Delta编码。</summary>
        public const uint FLAG_DELTA_INDICES = 0x04;

        /// <summary>
        /// 创建默认的文件头。
        /// </summary>
        public static MicroClusterFileHeader CreateDefault(uint meshCount, uint clusterCount, uint totalSize, uint flags = 0)
        {
            return new MicroClusterFileHeader
            {
                Magic = NANITE_MAGIC,
                Version = CURRENT_VERSION,
                MeshCount = meshCount,
                ClusterCount = clusterCount,
                TotalDataSize = totalSize,
                Flags = flags
            };
        }

        /// <summary>
        /// 从二进制读取器读取文件头。
        /// </summary>
        public static MicroClusterFileHeader Read(BinaryReader reader)
        {
            MicroClusterFileHeader header;
            header.Magic = reader.ReadUInt32();
            header.Version = reader.ReadUInt32();
            header.MeshCount = reader.ReadUInt32();
            header.ClusterCount = reader.ReadUInt32();
            header.TotalDataSize = reader.ReadUInt32();
            header.Flags = reader.ReadUInt32();
            return header;
        }

        /// <summary>
        /// 将文件头写入二进制写入器。
        /// </summary>
        public void Write(BinaryWriter writer)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(MeshCount);
            writer.Write(ClusterCount);
            writer.Write(TotalDataSize);
            writer.Write(Flags);
        }

        /// <summary>
        /// 验证文件头有效性。
        /// </summary>
        public bool IsValid()
        {
            return Magic == NANITE_MAGIC && Version <= CURRENT_VERSION;
        }
    }

    // ========================================================================
    // MicroClusterMeshHeader - 每个Mesh的头部信息
    // ========================================================================

    /// <summary>
    /// 每个网格在 .microcluster 文件中的头部信息。
    /// 紧跟在 MicroClusterFileHeader 之后，按网格数量重复。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MicroClusterMeshHeader
    {
        /// <summary>网格名称的长度（字节，UTF-8编码）。</summary>
        public uint NameLength;

        /// <summary>网格中的顶点数量。</summary>
        public uint VertexCount;

        /// <summary>网格中的三角形数量。</summary>
        public uint TriangleCount;

        /// <summary>网格中的聚类数量。</summary>
        public uint ClusterCount;

        /// <summary>网格的 LOD 层级数量。</summary>
        public uint LODCount;

        /// <summary>该网格的压缩数据在文件中的偏移量。</summary>
        public uint DataOffset;

        /// <summary>该网格的压缩数据大小（字节）。</summary>
        public uint DataSize;

        /// <summary>网格的包围盒最小值X。</summary>
        public float BoundsMinX;

        /// <summary>网格的包围盒最小值Y。</summary>
        public float BoundsMinY;

        /// <summary>网格的包围盒最小值Z。</summary>
        public float BoundsMinZ;

        /// <summary>网格的包围盒最大值X。</summary>
        public float BoundsMaxX;

        /// <summary>网格的包围盒最大值Y。</summary>
        public float BoundsMaxY;

        /// <summary>网格的包围盒最大值Z。</summary>
        public float BoundsMaxZ;

        /// <summary>头部的固定大小（字节）。</summary>
        public static readonly int HeaderSize = 56;

        /// <summary>
        /// 从二进制读取器读取网格头部。
        /// </summary>
        public static MicroClusterMeshHeader Read(BinaryReader reader)
        {
            MicroClusterMeshHeader header;
            header.NameLength = reader.ReadUInt32();
            header.VertexCount = reader.ReadUInt32();
            header.TriangleCount = reader.ReadUInt32();
            header.ClusterCount = reader.ReadUInt32();
            header.LODCount = reader.ReadUInt32();
            header.DataOffset = reader.ReadUInt32();
            header.DataSize = reader.ReadUInt32();
            header.BoundsMinX = reader.ReadSingle();
            header.BoundsMinY = reader.ReadSingle();
            header.BoundsMinZ = reader.ReadSingle();
            header.BoundsMaxX = reader.ReadSingle();
            header.BoundsMaxY = reader.ReadSingle();
            header.BoundsMaxZ = reader.ReadSingle();
            return header;
        }

        /// <summary>
        /// 将网格头部写入二进制写入器。
        /// </summary>
        public void Write(BinaryWriter writer)
        {
            writer.Write(NameLength);
            writer.Write(VertexCount);
            writer.Write(TriangleCount);
            writer.Write(ClusterCount);
            writer.Write(LODCount);
            writer.Write(DataOffset);
            writer.Write(DataSize);
            writer.Write(BoundsMinX);
            writer.Write(BoundsMinY);
            writer.Write(BoundsMinZ);
            writer.Write(BoundsMaxX);
            writer.Write(BoundsMaxY);
            writer.Write(BoundsMaxZ);
        }

        /// <summary>获取包围盒最小值。</summary>
        public Vector3 GetBoundsMin() => new Vector3(BoundsMinX, BoundsMinY, BoundsMinZ);

        /// <summary>获取包围盒最大值。</summary>
        public Vector3 GetBoundsMax() => new Vector3(BoundsMaxX, BoundsMaxY, BoundsMaxZ);
    }

    // ========================================================================
    // MicroClusterDataCompression - 核心压缩/解压缩类
    // ========================================================================

    /// <summary>
    /// MicroCluster 数据压缩核心类。
    /// 提供网格级别的压缩/解压缩、聚类级别的压缩/解压缩、
    /// 误差计算、以及 .microcluster 文件的流式读写功能。
    /// </summary>
    public class MicroClusterDataCompression
    {
        /// <summary>压缩标志位：启用全部压缩选项。</summary>
        private const uint DefaultFlags =
            MicroClusterFileHeader.FLAG_COMPRESSED_NORMALS |
            MicroClusterFileHeader.FLAG_QUANTIZED_POSITIONS |
            MicroClusterFileHeader.FLAG_DELTA_INDICES;

        // ====================================================================
        // 公共方法: 网格级别压缩/解压缩
        // ====================================================================

        /// <summary>
        /// 压缩一个 Mesh 对象为字节数组。
        /// 使用位置量化(16bit)、法线八面体编码(16bit)、UV 16bit。
        /// 
        /// 数据布局:
        ///   [4 bytes] 顶点数量
        ///   [4 bytes] 三角形数量
        ///   [12 bytes] BoundsMin (float3)
        ///   [12 bytes] BoundsMax (float3)
        ///   [N bytes] 顶点数据 (每顶点 8 bytes: PosX(2)+PosY(2)+PosZ(2)+Normal(4)=10 -> 实际8bytes紧凑)
        ///   [M bytes] 索引数据 (Delta编码)
        /// </summary>
        /// <param name="mesh">要压缩的网格。</param>
        /// <returns>压缩后的字节数组。</returns>
        public byte[] CompressMesh(Mesh mesh)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // 写入基本统计信息
                writer.Write(mesh.Positions.Count);
                writer.Write(mesh.Indices.Count);

                // 写入包围盒
                writer.Write(mesh.BoundsMin.X);
                writer.Write(mesh.BoundsMin.Y);
                writer.Write(mesh.BoundsMin.Z);
                writer.Write(mesh.BoundsMax.X);
                writer.Write(mesh.BoundsMax.Y);
                writer.Write(mesh.BoundsMax.Z);

                // 创建位置量化器
                var quantizer = new PositionQuantizer(mesh.BoundsMin, mesh.BoundsMax);

                // 压缩顶点数据
                // 每个顶点: Position(3x16bit=6bytes) + Normal(2x16bit=4bytes) + UV(2x16bit=4bytes) = 14 bytes
                for (int i = 0; i < mesh.Positions.Count; i++)
                {
                    Vector3 pos = mesh.Positions[i];
                    Vector3 nor = i < mesh.Normals.Count ? mesh.Normals[i] : new Vector3(0, 1, 0);
                    Vector2 uv = i < mesh.UVs.Count ? mesh.UVs[i] : new Vector2(0, 0);

                    // 量化位置 (3 x 16-bit)
                    quantizer.Quantize(pos, out ushort px, out ushort py, out ushort pz);
                    writer.Write(px);
                    writer.Write(py);
                    writer.Write(pz);

                    // 八面体编码法线 (2 x 16-bit)
                    NormalOctahedralEncoder.Encode(nor, out ushort nox, out ushort noy);
                    writer.Write(nox);
                    writer.Write(noy);

                    // 量化UV (2 x 16-bit)
                    ushort uvx = (ushort)Math.Clamp((int)Math.Round(uv.X * 65535.0f), 0, 65535);
                    ushort uvy = (ushort)Math.Clamp((int)Math.Round(uv.Y * 65535.0f), 0, 65535);
                    writer.Write(uvx);
                    writer.Write(uvy);
                }

                // 压缩索引数据（Delta编码）
                // 将32-bit索引压缩为Delta值序列
                int prevIndex = 0;
                for (int i = 0; i < mesh.Indices.Count; i++)
                {
                    int delta = mesh.Indices[i] - prevIndex;
                    prevIndex = mesh.Indices[i];

                    // 使用变长编码: 如果Delta在0-65535范围内，用2字节；否则用4字节
                    if (delta >= 0 && delta <= 65535)
                    {
                        writer.Write((byte)0); // 标记: 16-bit Delta
                        writer.Write((ushort)delta);
                    }
                    else
                    {
                        writer.Write((byte)1); // 标记: 32-bit Delta
                        writer.Write(delta);
                    }
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// 从压缩字节数组还原 Mesh 对象。
        /// </summary>
        /// <param name="data">压缩的字节数组。</param>
        /// <returns>还原后的 Mesh 对象。</returns>
        public Mesh DecompressMesh(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var mesh = new Mesh();

                // 读取基本统计信息
                int vertexCount = reader.ReadInt32();
                int indexCount = reader.ReadInt32();

                // 读取包围盒
                float bminX = reader.ReadSingle();
                float bminY = reader.ReadSingle();
                float bminZ = reader.ReadSingle();
                float bmaxX = reader.ReadSingle();
                float bmaxY = reader.ReadSingle();
                float bmaxZ = reader.ReadSingle();

                mesh.BoundsMin = new Vector3(bminX, bminY, bminZ);
                mesh.BoundsMax = new Vector3(bmaxX, bmaxY, bmaxZ);

                // 创建位置反量化器
                var quantizer = new PositionQuantizer(mesh.BoundsMin, mesh.BoundsMax);

                // 解压顶点数据
                for (int i = 0; i < vertexCount; i++)
                {
                    // 反量化位置
                    ushort px = reader.ReadUInt16();
                    ushort py = reader.ReadUInt16();
                    ushort pz = reader.ReadUInt16();
                    Vector3 pos = quantizer.Dequantize(px, py, pz);
                    mesh.Positions.Add(pos);

                    // 八面体解码法线
                    ushort nox = reader.ReadUInt16();
                    ushort noy = reader.ReadUInt16();
                    Vector3 nor = NormalOctahedralEncoder.Decode(nox, noy);
                    mesh.Normals.Add(nor);

                    // 反量化UV
                    ushort uvx = reader.ReadUInt16();
                    ushort uvy = reader.ReadUInt16();
                    Vector2 uv = new Vector2((float)uvx / 65535.0f, (float)uvy / 65535.0f);
                    mesh.UVs.Add(uv);
                }

                // 解压索引数据（Delta解码）
                int prevIndex = 0;
                for (int i = 0; i < indexCount; i++)
                {
                    byte marker = reader.ReadByte();

                    int index;
                    if (marker == 0)
                    {
                        // 16-bit Delta
                        ushort delta = reader.ReadUInt16();
                        index = prevIndex + delta;
                    }
                    else
                    {
                        // 32-bit Delta
                        int delta = reader.ReadInt32();
                        index = prevIndex + delta;
                    }

                    mesh.Indices.Add(index);
                    prevIndex = index;
                }

                return mesh;
            }
        }

        // ====================================================================
        // 公共方法: 聚类级别压缩/解压缩
        // ====================================================================

        /// <summary>
        /// 压缩一个 MicroClusterCluster 为字节数组。
        /// 
        /// 数据布局:
        ///   [4 bytes] ClusterId
        ///   [2 bytes] VertexCount
        ///   [2 bytes] IndexCount
        ///   [4 bytes] LODLevel
        ///   [4 bytes] ParentClusterId
        ///   [2 bytes] MaterialIndex
        ///   [4 bytes] ScreenSpaceError (float)
        ///   [12 bytes] BoundsMin
        ///   [12 bytes] BoundsMax
        ///   [N bytes] 顶点数据 (每顶点14bytes: 6+4+4)
        ///   [M bytes] 索引数据 (2 bytes each, 16-bit直接索引)
        /// </summary>
        /// <param name="cluster">要压缩的聚类。</param>
        /// <returns>压缩后的字节数组。</returns>
        public byte[] CompressCluster(MicroClusterCluster cluster)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // 写入聚类元数据
                writer.Write(cluster.ClusterId);
                writer.Write((ushort)cluster.Vertices.Count);
                writer.Write((ushort)cluster.Indices.Count);
                writer.Write(cluster.LODLevel);
                writer.Write(cluster.ParentClusterId);
                writer.Write((ushort)cluster.MaterialIndex);
                writer.Write(cluster.ScreenSpaceError);

                // 写入包围盒
                writer.Write(cluster.BoundsMin.X);
                writer.Write(cluster.BoundsMin.Y);
                writer.Write(cluster.BoundsMin.Z);
                writer.Write(cluster.BoundsMax.X);
                writer.Write(cluster.BoundsMax.Y);
                writer.Write(cluster.BoundsMax.Z);

                // 创建位置量化器
                var quantizer = new PositionQuantizer(cluster.BoundsMin, cluster.BoundsMax);

                // 压缩顶点数据
                foreach (var vert in cluster.Vertices)
                {
                    // 量化位置 (3 x 16-bit = 6 bytes)
                    quantizer.Quantize(vert.Position, out ushort px, out ushort py, out ushort pz);
                    writer.Write(px);
                    writer.Write(py);
                    writer.Write(pz);

                    // 八面体编码法线 (2 x 16-bit = 4 bytes)
                    NormalOctahedralEncoder.Encode(vert.Normal, out ushort nox, out ushort noy);
                    writer.Write(nox);
                    writer.Write(noy);

                    // 量化UV (2 x 16-bit = 4 bytes)
                    ushort uvx = (ushort)Math.Clamp(
                        (int)Math.Round(vert.UV.X * 65535.0f), 0, 65535);
                    ushort uvy = (ushort)Math.Clamp(
                        (int)Math.Round(vert.UV.Y * 65535.0f), 0, 65535);
                    writer.Write(uvx);
                    writer.Write(uvy);
                }

                // 写入索引数据 (16-bit直接索引，聚类内顶点数不超过65535)
                foreach (var idx in cluster.Indices)
                {
                    writer.Write(idx);
                }

                // 写入子聚类ID列表
                writer.Write(cluster.ChildClusterIds.Count);
                foreach (var childId in cluster.ChildClusterIds)
                {
                    writer.Write(childId);
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// 从压缩字节数组还原 MicroClusterCluster。
        /// </summary>
        /// <param name="data">压缩的字节数组。</param>
        /// <returns>还原后的 MicroClusterCluster 对象。</returns>
        public MicroClusterCluster DecompressCluster(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var cluster = new MicroClusterCluster();

                // 读取聚类元数据
                cluster.ClusterId = reader.ReadInt32();
                ushort vertexCount = reader.ReadUInt16();
                ushort indexCount = reader.ReadUInt16();
                cluster.LODLevel = reader.ReadInt32();
                cluster.ParentClusterId = reader.ReadInt32();
                cluster.MaterialIndex = reader.ReadUInt16();
                cluster.ScreenSpaceError = reader.ReadSingle();

                // 读取包围盒
                float bminX = reader.ReadSingle();
                float bminY = reader.ReadSingle();
                float bminZ = reader.ReadSingle();
                float bmaxX = reader.ReadSingle();
                float bmaxY = reader.ReadSingle();
                float bmaxZ = reader.ReadSingle();

                cluster.BoundsMin = new Vector3(bminX, bminY, bminZ);
                cluster.BoundsMax = new Vector3(bmaxX, bmaxY, bmaxZ);

                // 创建位置反量化器
                var quantizer = new PositionQuantizer(cluster.BoundsMin, cluster.BoundsMax);

                // 解压顶点数据
                for (int i = 0; i < vertexCount; i++)
                {
                    // 反量化位置
                    ushort px = reader.ReadUInt16();
                    ushort py = reader.ReadUInt16();
                    ushort pz = reader.ReadUInt16();
                    Vector3 pos = quantizer.Dequantize(px, py, pz);

                    // 八面体解码法线
                    ushort nox = reader.ReadUInt16();
                    ushort noy = reader.ReadUInt16();
                    Vector3 nor = NormalOctahedralEncoder.Decode(nox, noy);

                    // 反量化UV
                    ushort uvx = reader.ReadUInt16();
                    ushort uvy = reader.ReadUInt16();
                    Vector2 uv = new Vector2((float)uvx / 65535.0f, (float)uvy / 65535.0f);

                    cluster.Vertices.Add(new ClusterVertex(pos, nor, uv));
                }

                // 读取索引数据
                for (int i = 0; i < indexCount; i++)
                {
                    cluster.Indices.Add(reader.ReadUInt16());
                }

                // 读取子聚类ID列表
                int childCount = reader.ReadInt32();
                for (int i = 0; i < childCount; i++)
                {
                    cluster.ChildClusterIds.Add(reader.ReadInt32());
                }

                return cluster;
            }
        }

        // ====================================================================
        // 公共方法: 误差计算
        // ====================================================================

        /// <summary>
        /// 计算子聚类相对于父聚类的边界增量误差。
        /// 该误差用于判断 LOD 切换时引入的几何偏差。
        /// 
        /// 误差计算方法:
        /// 1. 对子聚类的每个顶点，找到其在父聚类中的最近三角形
        /// 2. 计算顶点到最近三角形的距离
        /// 3. 取所有顶点距离的最大值作为误差上界
        /// 4. 归一化到父聚类包围盒尺寸
        /// </summary>
        /// <param name="parent">父聚类（高精度）。</param>
        /// <param name="child">子聚类（低精度/简化后）。</param>
        /// <returns>归一化后的误差值（0.0 = 无误差，越大偏差越大）。</returns>
        public float ComputeClusterBoundsDelta(MicroClusterCluster parent, MicroClusterCluster child)
        {
            if (child.Vertices.Count == 0 || parent.Vertices.Count == 0)
                return float.MaxValue;

            float maxDistance = 0.0f;
            float parentDiag = parent.BoundsMax.X - parent.BoundsMin.X +
                               parent.BoundsMax.Y - parent.BoundsMin.Y +
                               parent.BoundsMax.Z - parent.BoundsMin.Z;

            // 避免除零
            if (parentDiag < 1e-6f)
                return 0.0f;

            // 对子聚类的每个顶点，计算到父聚类包围盒表面的最大距离
            // 简化版本：使用包围盒偏差作为误差近似
            Vector3 parentCenter = parent.BoundsCenter;
            Vector3 childCenter = child.BoundsCenter;

            // 中心偏移误差
            float centerDist = (childCenter - parentCenter).Length();

            // 包围盒尺寸差异误差
            float parentSizeX = parent.BoundsMax.X - parent.BoundsMin.X;
            float parentSizeY = parent.BoundsMax.Y - parent.BoundsMin.Y;
            float parentSizeZ = parent.BoundsMax.Z - parent.BoundsMin.Z;
            float childSizeX = child.BoundsMax.X - child.BoundsMin.X;
            float childSizeY = child.BoundsMax.Y - child.BoundsMin.Y;
            float childSizeZ = child.BoundsMax.Z - child.BoundsMin.Z;

            float sizeDeltaX = Math.Abs(parentSizeX - childSizeX);
            float sizeDeltaY = Math.Abs(parentSizeY - childSizeY);
            float sizeDeltaZ = Math.Abs(parentSizeZ - childSizeZ);

            // 综合误差 = 中心偏移 + 尺寸差异
            float sizeDelta = (float)Math.Sqrt(
                sizeDeltaX * sizeDeltaX +
                sizeDeltaY * sizeDeltaY +
                sizeDeltaZ * sizeDeltaZ);

            float totalError = centerDist + sizeDelta * 0.5f;

            // 对子聚类顶点采样，计算更精确的几何误差
            // 使用步进采样避免O(N*M)的完整最近点搜索
            int sampleStep = Math.Max(1, child.Vertices.Count / 32);
            int sampleCount = 0;

            for (int i = 0; i < child.Vertices.Count; i += sampleStep)
            {
                Vector3 childVert = child.Vertices[i].Position;

                // 快速排除：如果顶点在父包围盒内，误差贡献较小
                bool insideParent =
                    childVert.X >= parent.BoundsMin.X && childVert.X <= parent.BoundsMax.X &&
                    childVert.Y >= parent.BoundsMin.Y && childVert.Y <= parent.BoundsMax.Y &&
                    childVert.Z >= parent.BoundsMin.Z && childVert.Z <= parent.BoundsMax.Z;

                if (!insideParent)
                {
                    // 计算到父包围盒的最近点距离
                    float dx = Math.Max(
                        Math.Max(parent.BoundsMin.X - childVert.X, childVert.X - parent.BoundsMax.X), 0.0f);
                    float dy = Math.Max(
                        Math.Max(parent.BoundsMin.Y - childVert.Y, childVert.Y - parent.BoundsMax.Y), 0.0f);
                    float dz = Math.Max(
                        Math.Max(parent.BoundsMin.Z - childVert.Z, childVert.Z - parent.BoundsMax.Z), 0.0f);
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    maxDistance = Math.Max(maxDistance, dist);
                }

                sampleCount++;
            }

            // 取包围盒误差和顶点采样的较大值
            totalError = Math.Max(totalError, maxDistance);

            // 归一化到父聚类对角线长度
            return totalError / parentDiag;
        }

        // ====================================================================
        // 公共方法: .microcluster 文件流式读写
        // ====================================================================

        /// <summary>
        /// 将一组 Mesh 压缩并写入 .microcluster 格式的流。
        /// 
        /// 文件格式:
        ///   [MicroClusterFileHeader]         - 全局文件头 (28 bytes)
        ///   [MicroClusterMeshHeader x N]     - 每个Mesh的头部 (56 bytes each)
        ///   [MeshName0][MeshData0]     - 第1个Mesh的名称(UTF-8)和压缩数据
        ///   [MeshName1][MeshData1]     - 第2个Mesh的名称(UTF-8)和压缩数据
        ///   ...
        /// </summary>
        /// <param name="stream">输出流。</param>
        /// <param name="meshes">要压缩的网格列表。</param>
        public void CompressToStream(Stream stream, List<Mesh> meshes)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                // 步骤1: 预计算所有网格的压缩数据
                var compressedData = new byte[meshes.Count][];
                var meshNames = new byte[meshes.Count][];
                uint totalDataSize = 0;
                uint totalClusterCount = 0;

                for (int i = 0; i < meshes.Count; i++)
                {
                    compressedData[i] = CompressMesh(meshes[i]);
                    meshNames[i] = Encoding.UTF8.GetBytes(meshes[i].Name);
                    totalDataSize += (uint)compressedData[i].Length;
                    totalDataSize += (uint)meshNames[i].Length;
                    // 每个mesh的聚类数估计（简化：假设128三角形/聚类）
                    totalClusterCount += (uint)((meshes[i].TriangleCount + 127) / 128);
                }

                // 步骤2: 写入全局文件头
                var fileHeader = MicroClusterFileHeader.CreateDefault(
                    (uint)meshes.Count,
                    totalClusterCount,
                    totalDataSize,
                    DefaultFlags
                );
                fileHeader.Write(writer);

                // 步骤3: 写入每个Mesh的头部
                uint currentOffset = (uint)(MicroClusterFileHeader.HeaderSize +
                    MicroClusterMeshHeader.HeaderSize * meshes.Count);

                for (int i = 0; i < meshes.Count; i++)
                {
                    var meshHeader = new MicroClusterMeshHeader
                    {
                        NameLength = (uint)meshNames[i].Length,
                        VertexCount = (uint)meshes[i].VertexCount,
                        TriangleCount = (uint)meshes[i].TriangleCount,
                        ClusterCount = (uint)((meshes[i].TriangleCount + 127) / 128),
                        LODCount = 1, // 简化: 单LOD
                        DataOffset = currentOffset,
                        DataSize = (uint)(meshNames[i].Length + compressedData[i].Length),
                        BoundsMinX = meshes[i].BoundsMin.X,
                        BoundsMinY = meshes[i].BoundsMin.Y,
                        BoundsMinZ = meshes[i].BoundsMin.Z,
                        BoundsMaxX = meshes[i].BoundsMax.X,
                        BoundsMaxY = meshes[i].BoundsMax.Y,
                        BoundsMaxZ = meshes[i].BoundsMax.Z
                    };
                    meshHeader.Write(writer);
                    currentOffset += meshHeader.DataSize;
                }

                // 步骤4: 写入每个Mesh的名称和压缩数据
                for (int i = 0; i < meshes.Count; i++)
                {
                    writer.Write(meshNames[i]);
                    writer.Write(compressedData[i]);
                }

                writer.Flush();
            }
        }

        /// <summary>
        /// 从 .microcluster 格式的流中读取并解压缩所有网格。
        /// </summary>
        /// <param name="stream">输入流。</param>
        /// <returns>解压缩后的网格列表。</returns>
        public List<Mesh> DecompressFromStream(Stream stream)
        {
            var meshes = new List<Mesh>();

            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                // 步骤1: 读取全局文件头
                var fileHeader = MicroClusterFileHeader.Read(reader);

                if (!fileHeader.IsValid())
                {
                    throw new InvalidDataException(
                        $"无效的.microcluster文件: Magic=0x{fileHeader.Magic:X8}, Version={fileHeader.Version}");
                }

                // 步骤2: 读取每个Mesh的头部
                var meshHeaders = new MicroClusterMeshHeader[fileHeader.MeshCount];
                for (int i = 0; i < fileHeader.MeshCount; i++)
                {
                    meshHeaders[i] = MicroClusterMeshHeader.Read(reader);
                }

                // 步骤3: 读取并解压每个Mesh
                for (int i = 0; i < fileHeader.MeshCount; i++)
                {
                    var header = meshHeaders[i];

                    // 定位到数据偏移位置
                    long currentPos = stream.Position;
                    stream.Seek(header.DataOffset, SeekOrigin.Begin);

                    // 读取网格名称
                    byte[] nameBytes = reader.ReadBytes((int)header.NameLength);
                    string meshName = Encoding.UTF8.GetString(nameBytes);

                    // 读取压缩的网格数据
                    byte[] compressedData = reader.ReadBytes((int)header.DataSize - (int)header.NameLength);

                    // 解压缩
                    Mesh mesh = DecompressMesh(compressedData);
                    mesh.Name = meshName;

                    meshes.Add(mesh);

                    // 恢复流位置
                    stream.Position = currentPos;
                }
            }

            return meshes;
        }

        // ====================================================================
        // 工具方法: 辅助计算
        // ====================================================================

        /// <summary>
        /// 计算网格的包围盒。
        /// </summary>
        public static void ComputeBounds(List<Vector3> positions, out Vector3 boundsMin, out Vector3 boundsMax)
        {
            if (positions == null || positions.Count == 0)
            {
                boundsMin = new Vector3(0, 0, 0);
                boundsMax = new Vector3(0, 0, 0);
                return;
            }

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            foreach (var p in positions)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            boundsMin = new Vector3(minX, minY, minZ);
            boundsMax = new Vector3(maxX, maxY, maxZ);
        }

        /// <summary>
        /// 计算聚类的包围盒。
        /// </summary>
        public static void ComputeClusterBounds(
            List<ClusterVertex> vertices,
            out Vector3 boundsMin,
            out Vector3 boundsMax)
        {
            if (vertices == null || vertices.Count == 0)
            {
                boundsMin = new Vector3(0, 0, 0);
                boundsMax = new Vector3(0, 0, 0);
                return;
            }

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            foreach (var v in vertices)
            {
                if (v.Position.X < minX) minX = v.Position.X;
                if (v.Position.Y < minY) minY = v.Position.Y;
                if (v.Position.Z < minZ) minZ = v.Position.Z;
                if (v.Position.X > maxX) maxX = v.Position.X;
                if (v.Position.Y > maxY) maxY = v.Position.Y;
                if (v.Position.Z > maxZ) maxZ = v.Position.Z;
            }

            boundsMin = new Vector3(minX, minY, minZ);
            boundsMax = new Vector3(maxX, maxY, maxZ);
        }

        /// <summary>
        /// 计算单个网格的压缩率统计信息。
        /// </summary>
        /// <param name="mesh">原始网格。</param>
        /// <returns>包含原始大小、压缩大小和压缩比的元组。</returns>
        public (long originalSize, long compressedSize, float compressionRatio) GetCompressionStats(Mesh mesh)
        {
            // 计算未压缩大小
            // 每顶点: Position(12) + Normal(12) + UV(8) = 32 bytes
            // 每索引: 4 bytes (int32)
            long originalVertexSize = (long)mesh.VertexCount * 32;
            long originalIndexSize = (long)mesh.Indices.Count * 4;
            long originalSize = originalVertexSize + originalIndexSize;

            // 计算压缩大小
            byte[] compressed = CompressMesh(mesh);
            long compressedSize = compressed.Length;

            float ratio = originalSize > 0 ? (float)compressedSize / originalSize : 0.0f;

            return (originalSize, compressedSize, ratio);
        }
    }
}