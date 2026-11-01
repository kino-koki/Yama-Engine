// =============================================================================
//  PngWriter.cs — 最小 PNG 编码器（无外部依赖）
//
//  主引擎 RHI 的 SaveScreenshot 目前是占位实现（"暂未实现文件保存"），
//  因此本沙盒自实现一个最小的 RGBA→PNG 编码器，用于 headless 出图验证。
//
//  规范: 8-bit RGBA, 无隔行, 每行 filter 类型 0 (None), zlib 压缩 (内置 ZLibStream)。
// =============================================================================

using System.IO.Compression;

namespace YamaCel2DSandbox;

internal static class PngWriter
{
    // ── CRC32 (PNG 标准多项式 0xEDB88320) ──
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFFu)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// 将 RGBA 像素（每像素 4 字节，行序自顶向下）编码为 PNG 字节流。
    /// </summary>
    /// <param name="rgba">RGBA 像素数据，长度必须 = width*height*4。</param>
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();

        // ── 签名 ──
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // ── IHDR ──
        var ihdr = new byte[13];
        WriteU32(ihdr, 0, (uint)width);
        WriteU32(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: 6 = RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(ms, "IHDR", ihdr);

        // ── IDAT: 每行前加 filter byte 0 (None) ──
        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = y * (stride + 1);
            raw[dst] = 0; // filter type None
            Array.Copy(rgba, src, raw, dst + 1, stride);
        }

        byte[] zlibData;
        using (var zms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            zlibData = zms.ToArray();
        }
        WriteChunk(ms, "IDAT", zlibData);

        // ── IEND ──
        WriteChunk(ms, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteU32(len, 0, (uint)data.Length);
        s.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        // CRC 覆盖 type + data
        var crcSpan = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcSpan, 0);
        data.CopyTo(crcSpan, typeBytes.Length);
        var crcBytes = new byte[4];
        WriteU32(crcBytes, 0, Crc32(crcSpan));
        s.Write(crcBytes);
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset + 0] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }
}
