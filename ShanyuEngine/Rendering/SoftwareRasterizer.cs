using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 MicroCluster 软件光栅化器 (MicroCluster 核心)
    //
    // 子像素三角形 (< 1 pixel) 不适合硬件光栅化, 改由 Compute Shader 软件光栅化。
    // 采用分 bin (tile) 并行策略: 将屏幕划分为 16x16 tile, 三角形先分到 bin,
    // 再每 tile 内并行光栅化, 避免全屏扫描的浪费。
    //
    // 管线阶段:
    //   1. Bin 分配 Pass (Compute): 将三角形按屏幕 16x16 tile 分配到 bin, 每 bin 记录三角形列表。
    //   2. 光栅化 Pass (Compute): 每 tile 内并行光栅化三角形, HiZ 预测试加速深度测试,
    //      原子操作写入可见性缓冲 (clusterId/triangleId/barycentric)。
    //   3. 属性解码 Pass (Compute): 从可见性缓冲读取可见三角形, 插值顶点属性
    //      (Position/Normal/UV/Tangent), 输出 GeometryBuffer。
    //
    // 可见性缓冲格式 (8 bytes/pixel):
    //   R32G32_UINT:  .x = instanceId(20bit) | clusterId(24bit)... 实际见 PackVisibility
    //   实际: .x = (instanceId & 0xFFFFF) | ((triangleId & 0xFFF) << 20)
    //         .y = f16(bary.x) | (f16(bary.y) << 16)   = 8 bytes/pixel
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>软件光栅化常量: tile 大小与缓冲配置。</summary>
    public static class SoftwareRasterConstants
    {
        /// <summary>tile 边长 (像素), 16x16 = 256 像素/tile。</summary>
        public const int TileSize = 16;
        /// <summary>每 bin (tile) 最多记录的三角形数。</summary>
        public const int MaxTrianglesPerBin = 64;
        /// <summary>Bin 分配线程组大小 (X)。</summary>
        public const int BinAssignGroupSize = 64;
        /// <summary>光栅化线程组大小 (每 tile 一组)。</summary>
        public const int RasterGroupSize = 64;
        /// <summary>属性解码线程组大小。</summary>
        public const int DecodeGroupSize = 8;
    }

    /// <summary>
    /// 可见性缓冲像素: 每像素 8 字节。
    /// instanceId(20bit) + triangleId(12bit) + 重心坐标(f16x2)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VisibilityBufferPixel
    {
        /// <summary>.x = (instanceId & 0xFFFFF) | ((triangleId & 0xFFF) << 20)。</summary>
        public uint Packed0;
        /// <summary>.y = f32tof16(bary.x) | (f32tof16(bary.y) << 16)。</summary>
        public uint Packed1;

        /// <summary>空像素 (天空)。</summary>
        public static VisibilityBufferPixel Empty => new() { Packed0 = 0, Packed1 = 0 };

        /// <summary>是否为空像素。</summary>
        public readonly bool IsEmpty => Packed0 == 0 && Packed1 == 0;

        /// <summary>解包实例 ID。</summary>
        public readonly uint InstanceId => Packed0 & 0xFFFFF;
        /// <summary>解包三角形 ID (簇内)。</summary>
        public readonly uint TriangleId => (Packed0 >> 20) & 0xFFF;

        /// <summary>解包重心坐标。</summary>
        public readonly Vector2 Barycentric => new(HalfToFloat((ushort)(Packed1 & 0xFFFF)),
                                                    HalfToFloat((ushort)(Packed1 >> 16)));

        /// <summary>打包可见性数据。</summary>
        public static VisibilityBufferPixel Pack(uint instanceId, uint triangleId, Vector2 bary)
        {
            return new VisibilityBufferPixel
            {
                Packed0 = (instanceId & 0xFFFFF) | ((triangleId & 0xFFF) << 20),
                Packed1 = (uint)(FloatToHalf(bary.X) | (FloatToHalf(bary.Y) << 16))
            };
        }

        private static float HalfToFloat(ushort h)
        {
            uint sign = (h >> 15) & 0x1u;
            uint exp = (h >> 10) & 0x1Fu;
            uint mant = h & 0x3FFu;
            uint bits;
            if (exp == 0)
            {
                if (mant == 0) bits = sign << 31;
                else { mant <<= 1; while ((mant & 0x400) == 0) { mant <<= 1; exp++; } mant &= 0x3FF; bits = (sign << 31) | ((exp + 112 - 15) << 23) | (mant << 13); }
            }
            else if (exp == 31) bits = (sign << 31) | (0xFF << 23) | (mant << 13);
            else bits = (sign << 31) | ((exp + 112 - 15) << 23) | (mant << 13);
            return BitConverter.Int32BitsToSingle((int)bits);
        }

        private static ushort FloatToHalf(float f)
        {
            uint bits = BitConverter.SingleToInt32Bits(f);
            uint sign = (bits >> 16) & 0x8000;
            uint exp = (bits >> 23) & 0xFF;
            uint mant = bits & 0x7FFFFF;
            if (exp == 0) return (ushort)sign;
            if (exp >= 142) return (ushort)(sign | 0x7C00);
            if (exp <= 112) return (ushort)(sign | ((mant | 0x800000) >> (126 - exp)));
            return (ushort)(sign | ((exp - 112) << 10) | (mant >> 13));
        }
    }

    /// <summary>Bin (tile) 三角形记录: 三角形在 bin 列表中的索引。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BinTriangleRef
    {
        /// <summary>全局三角形索引 (在软件光栅簇三角形列表中)。</summary>
        public uint TriangleIndex;
        /// <summary>所属簇索引。</summary>
        public uint ClusterIndex;
        /// <summary>所属实例索引。</summary>
        public uint InstanceIndex;
        /// <summary>三角形屏幕包围盒 minXY (打包为 uint16x2)。</summary>
        public uint PackedMin;
    }

    /// <summary>Bin 头: 每个 tile 的三角形计数与列表起始偏移。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BinHeader
    {
        /// <summary>此 tile 内三角形数量。</summary>
        public uint TriangleCount;
        /// <summary>三角形列表在全局 bin buffer 中的起始偏移。</summary>
        public uint TriangleStart;
        /// <summary>tile X 坐标。</summary>
        public uint TileX;
        /// <summary>tile Y 坐标。</summary>
        public uint TileY;
    }

    /// <summary>软件光栅化常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SoftwareRasterCB
    {
        public Matrix4x4 ViewProjMatrix;
        public Vector3 CameraPos;
        public uint VisibleClusterCount;
        public Vector2 InvResolution;
        public uint TileCountX;
        public uint TileCountY;
        public uint FrameIndex;
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;
    }

    /// <summary>属性解码常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeDecodeCB
    {
        public Matrix4x4 InvViewProjMatrix;
        public Vector3 CameraPos;
        public float Pad0;
        public Vector2 InvResolution;
        public uint MaterialCount;
        public uint Pad1;
        public uint Pad2;
        public uint Pad3;
        public uint Pad4;
    }

    /// <summary>
    /// 软件光栅化器: MicroCluster 子像素三角形路径。
    /// 通过 Compute Shader 实现 bin 分配 -> tile 光栅化 -> 属性解码三阶段管线。
    /// 输出可见性缓冲 (Visibility Buffer) 与深度缓冲。
    /// </summary>
}
