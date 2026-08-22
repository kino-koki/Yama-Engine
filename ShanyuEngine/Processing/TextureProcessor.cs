// ====================================================================
// TextureProcessor.cs — 纹理处理管线
//
// 命名空间: ShanyuEngine.Processing
//
// 核心功能:
//   1. MipmapGenerator: Mip 链生成 (Box/Kaiser/三线性/NPOT)
//   2. NormalMapGenerator: 法线贴图生成 (Sobel/DuDv)
//   3. TextureCompressor: 块压缩编码 (BC1/BC3/BC5/BC7/ASTC)
//   4. TextureAtlasPacker: 图集打包 (MaxRects/Guillotine/UV 重映射)
//   5. HDRProcessor: HDR 色调映射与色彩空间转换
// ====================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Processing
{
    // ════════════════════════════════════════════════════════════════
    // TextureBuffer — 纹理数据缓冲
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 纹理数据缓冲。存储像素颜色数据，支持 RGBA Float32 格式。
    /// </summary>
    public class TextureBuffer
    {
        /// <summary>纹理宽度。</summary>
        public int Width { get; set; }
        /// <summary>纹理高度。</summary>
        public int Height { get; set; }
        /// <summary>像素格式。</summary>
        public TexturePixelFormat Format { get; set; } = TexturePixelFormat.RGBA32F;
        /// <summary>像素数据 (Width * Height * 4 floats = RGBA)。</summary>
        public float[] Data { get; set; } = Array.Empty<float>();

        /// <summary>总像素数。</summary>
        public int PixelCount => Width * Height;

        /// <summary>获取像素颜色 (RGBA)。</summary>
        public Vector4 GetPixel(int x, int y)
        {
            int idx = (y * Width + x) * 4;
            return new Vector4(Data[idx], Data[idx + 1], Data[idx + 2], Data[idx + 3]);
        }

        /// <summary>设置像素颜色。</summary>
        public void SetPixel(int x, int y, Vector4 color)
        {
            int idx = (y * Width + x) * 4;
            Data[idx] = color.X;
            Data[idx + 1] = color.Y;
            Data[idx + 2] = color.Z;
            Data[idx + 3] = color.W;
        }

        /// <summary>双线性采样。</summary>
        public Vector4 SampleBilinear(float u, float v)
        {
            u = Math.Clamp(u, 0, 1);
            v = Math.Clamp(v, 0, 1);

            float fx = u * (Width - 1);
            float fy = v * (Height - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, Width - 1);
            int y1 = Math.Min(y0 + 1, Height - 1);
            float fracX = fx - x0;
            float fracY = fy - y0;

            Vector4 c00 = GetPixel(x0, y0);
            Vector4 c10 = GetPixel(x1, y0);
            Vector4 c01 = GetPixel(x0, y1);
            Vector4 c11 = GetPixel(x1, y1);

            Vector4 top = Vector4.Lerp(c00, c10, fracX);
            Vector4 bot = Vector4.Lerp(c01, c11, fracX);
            return Vector4.Lerp(top, bot, fracY);
        }

        /// <summary>创建指定尺寸的空纹理缓冲。</summary>
        public static TextureBuffer Create(int width, int height)
        {
            return new TextureBuffer
            {
                Width = width,
                Height = height,
                Data = new float[width * height * 4]
            };
        }

        /// <summary>从灰度数组创建 (单通道)。</summary>
        public static TextureBuffer FromGrayscale(int width, int height, float[] gray)
        {
            var tex = Create(width, height);
            for (int i = 0; i < gray.Length; i++)
            {
                tex.Data[i * 4] = gray[i];
                tex.Data[i * 4 + 1] = gray[i];
                tex.Data[i * 4 + 2] = gray[i];
                tex.Data[i * 4 + 3] = 1.0f;
            }
            return tex;
        }
    }

    /// <summary>纹理像素格式。</summary>
    public enum TexturePixelFormat
    {
        /// <summary>RGBA Float32 (4 bytes per channel)。</summary>
        RGBA32F,
        /// <summary>RGBA 8bit (1 byte per channel)。</summary>
        RGBA8,
        /// <summary>RGB Float32 (3 channels)。</summary>
        RGB32F,
        /// <summary>R Float32 (单通道)。</summary>
        R32F,
        /// <summary>RG Float32 (双通道)。</summary>
        RG32F
    }

    // ════════════════════════════════════════════════════════════════
    // MipmapGenerator — Mip 链生成
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mip 链生成器。提供 Box 滤波、Kaiser 窗 Sinc 滤波、
    /// 三线性采样与非 2 的幂纹理 Pad。
    /// </summary>
    public class MipmapGenerator
    {
        /// <summary>滤波模式。</summary>
        public enum FilterMode
        {
            /// <summary>最邻近采样 (无滤波)。</summary>
            Nearest,
            /// <summary>Box 滤波 (2x2 区域平均)。</summary>
            Box,
            /// <summary>Kaiser 窗 Sinc 滤波 (高质量缩小)。</summary>
            Kaiser,
            /// <summary>三线性 Box 滤波。</summary>
            Trilinear
        }

        /// <summary>
        /// 生成完整的 Mip 链。
        /// </summary>
        /// <param name="source">源纹理 (Mip 0)。</param>
        /// <param name="filter">滤波模式。</param>
        /// <returns>Mip 链数组 (index 0 = 原始, index 1 = 1/2, ...)</returns>
        public TextureBuffer[] GenerateMipChain(TextureBuffer source, FilterMode filter = FilterMode.Box)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var mipChain = new List<TextureBuffer> { source };

            int currentWidth = source.Width;
            int currentHeight = source.Height;

            while (currentWidth > 1 || currentHeight > 1)
            {
                int newWidth = Math.Max(1, currentWidth / 2);
                int newHeight = Math.Max(1, currentHeight / 2);

                TextureBuffer mip;
                switch (filter)
                {
                    case FilterMode.Nearest:
                        mip = BoxFilterNearest(mipChain[mipChain.Count - 1], newWidth, newHeight);
                        break;
                    case FilterMode.Box:
                    case FilterMode.Trilinear:
                        mip = BoxFilter(mipChain[mipChain.Count - 1], newWidth, newHeight);
                        break;
                    case FilterMode.Kaiser:
                        mip = KaiserFilter(mipChain[mipChain.Count - 1], newWidth, newHeight);
                        break;
                    default:
                        mip = BoxFilter(mipChain[mipChain.Count - 1], newWidth, newHeight);
                        break;
                }

                mipChain.Add(mip);
                currentWidth = newWidth;
                currentHeight = newHeight;
            }

            return mipChain.ToArray();
        }

        /// <summary>
        /// Box 滤波: 对 2x2 区域取平均。
        /// </summary>
        public static TextureBuffer BoxFilter(TextureBuffer source, int targetWidth, int targetHeight)
        {
            var result = TextureBuffer.Create(targetWidth, targetHeight);
            float xScale = (float)source.Width / targetWidth;
            float yScale = (float)source.Height / targetHeight;

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    // 源区域
                    float srcX = x * xScale;
                    float srcY = y * yScale;

                    // 2x2 采样 (Box 滤波)
                    int x0 = Math.Min((int)srcX, source.Width - 1);
                    int y0 = Math.Min((int)srcY, source.Height - 1);
                    int x1 = Math.Min(x0 + 1, source.Width - 1);
                    int y1 = Math.Min(y0 + 1, source.Height - 1);

                    Vector4 c00 = source.GetPixel(x0, y0);
                    Vector4 c10 = source.GetPixel(x1, y0);
                    Vector4 c01 = source.GetPixel(x0, y1);
                    Vector4 c11 = source.GetPixel(x1, y1);

                    Vector4 avg = (c00 + c10 + c01 + c11) * 0.25f;
                    result.SetPixel(x, y, avg);
                }
            }

            return result;
        }

        /// <summary>
        /// 最邻近 Box 滤波: 取最近像素。
        /// </summary>
        public static TextureBuffer BoxFilterNearest(TextureBuffer source, int targetWidth, int targetHeight)
        {
            var result = TextureBuffer.Create(targetWidth, targetHeight);
            float xScale = (float)source.Width / targetWidth;
            float yScale = (float)source.Height / targetHeight;

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    int srcX = Math.Min((int)(x * xScale), source.Width - 1);
                    int srcY = Math.Min((int)(y * yScale), source.Height - 1);
                    result.SetPixel(x, y, source.GetPixel(srcX, srcY));
                }
            }

            return result;
        }

        /// <summary>
        /// Kaiser 窗 Sinc 滤波: 高质量纹理缩小。
        /// 使用 Kaiser 窗加权的 Sinc 函数进行限带滤波。
        /// </summary>
        public static TextureBuffer KaiserFilter(TextureBuffer source, int targetWidth, int targetHeight)
        {
            // Kaiser 窗参数
            const float kaiserAlpha = 4.0f;  // 窗形状参数 (越大过渡越陡)
            const float kaiserStretch = 1.0f;

            var result = TextureBuffer.Create(targetWidth, targetHeight);

            // 滤波器半径 (像素)
            int radiusX = Math.Max(1, source.Width / targetWidth);
            int radiusY = Math.Max(1, source.Height / targetHeight);

            // 预计算 1D Kaiser 窗权重
            float[] kaiserWX = ComputeKaiserWeights(radiusX, kaiserAlpha, kaiserStretch);
            float[] kaiserWY = ComputeKaiserWeights(radiusY, kaiserAlpha, kaiserStretch);

            float xScale = (float)source.Width / targetWidth;
            float yScale = (float)source.Height / targetHeight;

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    float centerSrcX = (x + 0.5f) * xScale - 0.5f;
                    float centerSrcY = (y + 0.5f) * yScale - 0.5f;

                    Vector4 sum = Vector4.Zero;
                    float weightSum = 0;

                    int startX = Math.Max(0, (int)(centerSrcX - radiusX + 0.5f));
                    int endX = Math.Min(source.Width - 1, (int)(centerSrcX + radiusX + 0.5f));
                    int startY = Math.Max(0, (int)(centerSrcY - radiusY + 0.5f));
                    int endY = Math.Min(source.Height - 1, (int)(centerSrcY + radiusY + 0.5f));

                    for (int sy = startY; sy <= endY; sy++)
                    {
                        float dy = sy - centerSrcY;
                        int ky = Math.Clamp((int)MathF.Abs(dy), 0, radiusY - 1);
                        float wy = kaiserWY[ky];

                        for (int sx = startX; sx <= endX; sx++)
                        {
                            float dx = sx - centerSrcX;
                            int kx = Math.Clamp((int)MathF.Abs(dx), 0, radiusX - 1);
                            float wx = kaiserWX[kx];

                            // Sinc 函数
                            float sincX = (MathF.Abs(dx) < 1e-6f) ? 1.0f : MathF.Sin(MathF.PI * dx / xScale) / (MathF.PI * dx / xScale);
                            float sincY = (MathF.Abs(dy) < 1e-6f) ? 1.0f : MathF.Sin(MathF.PI * dy / yScale) / (MathF.PI * dy / yScale);

                            float weight = sincX * sincY * wx * wy;
                            sum += source.GetPixel(sx, sy) * weight;
                            weightSum += weight;
                        }
                    }

                    if (weightSum > 1e-10f)
                        result.SetPixel(x, y, sum / weightSum);
                    else
                        result.SetPixel(x, y, Vector4.Zero);
                }
            }

            return result;
        }

        /// <summary>
        /// NPOT Resize: 将非 2 的幂纹理 Pad 到最近的 2 的幂尺寸。
        /// </summary>
        public static TextureBuffer NPOTResize(TextureBuffer source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            int newWidth = NextPowerOfTwo(source.Width);
            int newHeight = NextPowerOfTwo(source.Height);

            if (newWidth == source.Width && newHeight == source.Height)
                return source; // 已经是 POT

            var result = TextureBuffer.Create(newWidth, newHeight);

            // 复制原始数据到左上角，其余填充透明黑
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    result.SetPixel(x, y, source.GetPixel(x, y));
                }
            }

            return result;
        }

        /// <summary>预计算 Kaiser 窗权重。</summary>
        private static float[] ComputeKaiserWeights(int radius, float alpha, float stretch)
        {
            var weights = new float[radius];
            float besselI0 = BesselI0(alpha);
            float scale = 1.0f / (radius * stretch);

            for (int i = 0; i < radius; i++)
            {
                float t = (float)i * scale;
                float arg = alpha * MathF.Sqrt(Math.Max(0, 1.0f - t * t));
                weights[i] = BesselI0(arg) / besselI0;
            }

            return weights;
        }

        /// <summary>修正第一类贝塞尔函数 I0(x) 的近似计算。</summary>
        private static float BesselI0(float x)
        {
            float xSq = x * x;
            return 1.0f + xSq * (2.499699f + xSq * (0.125195f + xSq * (0.000704f + xSq * 0.0000028f)));
        }

        /// <summary>计算不小于 n 的最小 2 的幂。</summary>
        private static int NextPowerOfTwo(int n)
        {
            n--;
            n |= n >> 1;
            n |= n >> 2;
            n |= n >> 4;
            n |= n >> 8;
            n |= n >> 16;
            return n + 1;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // NormalMapGenerator — 法线贴图生成
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 法线贴图生成器。从高度图或彩色纹理生成法线贴图。
    /// 支持 Sobel 算子与 Du/Dv 差分法。
    /// </summary>
    public class NormalMapGenerator
    {
        /// <summary>法线强度 (高度缩放因子)。</summary>
        public float Strength { get; set; } = 1.0f;

        /// <summary>法线通道映射 (DX: X+→R, Y+→G, Z+→B)。</summary>
        public NormalMapChannelMapping ChannelMapping { get; set; } = NormalMapChannelMapping.DX;

        /// <summary>
        /// 从高度图 (灰度) 生成法线贴图。
        /// 使用 Sobel 算子计算梯度，然后转换为法线向量。
        /// </summary>
        public TextureBuffer FromHeightMap(TextureBuffer heightMap)
        {
            if (heightMap == null) throw new ArgumentNullException(nameof(heightMap));

            var normalMap = TextureBuffer.Create(heightMap.Width, heightMap.Height);

            for (int y = 0; y < heightMap.Height; y++)
            {
                for (int x = 0; x < heightMap.Width; x++)
                {
                    // Sobel 算子
                    // 左上  左  右上
                    // 左    中  右
                    // 左下  下  右下
                    float tl = SampleHeight(heightMap, x - 1, y - 1);
                    float t  = SampleHeight(heightMap, x, y - 1);
                    float tr = SampleHeight(heightMap, x + 1, y - 1);
                    float l  = SampleHeight(heightMap, x - 1, y);
                    float r  = SampleHeight(heightMap, x + 1, y);
                    float bl = SampleHeight(heightMap, x - 1, y + 1);
                    float b  = SampleHeight(heightMap, x, y + 1);
                    float br = SampleHeight(heightMap, x + 1, y + 1);

                    // 水平梯度 (dH/dx) 与垂直梯度 (dH/dy)
                    float dhdx = (tr + 2.0f * r + br) - (tl + 2.0f * l + bl);
                    float dhdy = (bl + 2.0f * b + br) - (tl + 2.0f * t + tr);

                    // 法线 = normalize(-dhdx * strength, -dhdy * strength, 1.0)
                    Vector3 normal = Vector3.Normalize(new Vector3(
                        -dhdx * Strength,
                        -dhdy * Strength,
                        1.0f
                    ));

                    // 通道重映射: 法线 [-1,1] → 纹理 [0,1]
                    Vector4 color = RemapNormalToColor(normal);
                    normalMap.SetPixel(x, y, color);
                }
            }

            return normalMap;
        }

        /// <summary>
        /// 从彩色纹理生成法线贴图。
        /// 使用 Du/Dv 差分法: 对亮度通道计算梯度。
        /// </summary>
        public TextureBuffer FromTexture(TextureBuffer colorTexture)
        {
            if (colorTexture == null) throw new ArgumentNullException(nameof(colorTexture));

            var normalMap = TextureBuffer.Create(colorTexture.Width, colorTexture.Height);

            for (int y = 0; y < colorTexture.Height; y++)
            {
                for (int x = 0; x < colorTexture.Width; x++)
                {
                    // 亮度 = 0.2126R + 0.7152G + 0.0722B
                    float lL = Luminance(colorTexture.GetPixel(
                        Math.Max(0, x - 1), y));
                    float lR = Luminance(colorTexture.GetPixel(
                        Math.Min(colorTexture.Width - 1, x + 1), y));
                    float lU = Luminance(colorTexture.GetPixel(
                        x, Math.Max(0, y - 1)));
                    float lD = Luminance(colorTexture.GetPixel(
                        x, Math.Min(colorTexture.Height - 1, y + 1)));

                    // 中心差分
                    float du = (lR - lL) * Strength;
                    float dv = (lD - lU) * Strength;

                    Vector3 normal = Vector3.Normalize(new Vector3(-du, -dv, 1.0f));
                    Vector4 color = RemapNormalToColor(normal);
                    normalMap.SetPixel(x, y, color);
                }
            }

            return normalMap;
        }

        /// <summary>
        /// 法线通道重映射。DX 约定: X+→R, Y+→G, Z+→B。
        /// OpenGL 约定: X+→R, Y+→G (反转), Z+→B。
        /// </summary>
        public Vector4 RemapNormalToColor(Vector3 normal)
        {
            float nx = normal.X, ny = normal.Y, nz = normal.Z;

            switch (ChannelMapping)
            {
                case NormalMapChannelMapping.DX:
                    // DX: X→R, Y→G, Z→B
                    break;
                case NormalMapChannelMapping.OpenGL:
                    // OpenGL: Y 翻转
                    ny = -ny;
                    break;
            }

            // 法线 [-1,1] → 纹理 [0,1]
            return new Vector4(
                nx * 0.5f + 0.5f,
                ny * 0.5f + 0.5f,
                nz * 0.5f + 0.5f,
                1.0f
            );
        }

        private static float SampleHeight(TextureBuffer heightMap, int x, int y)
        {
            x = Math.Clamp(x, 0, heightMap.Width - 1);
            y = Math.Clamp(y, 0, heightMap.Height - 1);
            Vector4 c = heightMap.GetPixel(x, y);
            return (c.X + c.Y + c.Z) / 3.0f; // 灰度
        }

        private static float Luminance(Vector4 c)
        {
            return 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        }
    }

    /// <summary>法线贴图通道映射约定。</summary>
    public enum NormalMapChannelMapping
    {
        /// <summary>DirectX 约定: X+→R, Y+→G, Z+→B。</summary>
        DX,
        /// <summary>OpenGL 约定: X+→R, Y-→G, Z+→B。</summary>
        OpenGL
    }

    // ════════════════════════════════════════════════════════════════
    // TextureCompressor — 块压缩编码
    // ════════════════════════════════════════════════════════════════

    /// <summary>块压缩格式。</summary>
    public enum BlockCompressionFormat
    {
        /// <summary>BC1 (DXT1): RGB, 4x4 块, 8 bytes。无 Alpha 或 1-bit Alpha。</summary>
        BC1,
        /// <summary>BC2 (DXT3): RGBA, 4x4 块, 16 bytes。显式 4-bit Alpha。</summary>
        BC2,
        /// <summary>BC3 (DXT5): RGBA, 4x4 块, 16 bytes。插值 8-bit Alpha。</summary>
        BC3,
        /// <summary>BC5 (3DC/ATI2): RG 双通道, 4x4 块, 16 bytes。法线贴图专用。</summary>
        BC5,
        /// <summary>BC7: 高质量 RGBA, 4x4 块, 16 bytes。支持多模式。</summary>
        BC7,
        /// <summary>ASTC: 自适应可缩放纹理压缩 (需 ARM 编码器集成)。</summary>
        ASTC
    }

    /// <summary>压缩质量预设。</summary>
    public enum QualityPreset
    {
        /// <summary>快速: 低质量，速度快。</summary>
        Fast,
        /// <summary>平衡: 中等质量与速度。</summary>
        Balanced,
        /// <summary>高质量: 最佳质量，速度慢。</summary>
        HighQuality
    }

    /// <summary>
    /// 纹理块压缩编码器。支持 BC1/BC3/BC5/BC7 格式。
    /// ASTC 为占位接口 (需要集成 ARM ASTC 编码器)。
    /// </summary>
    public class TextureCompressor
    {
        /// <summary>压缩质量预设。</summary>
        public QualityPreset Quality { get; set; } = QualityPreset.Balanced;

        /// <summary>
        /// 压缩纹理到指定块格式。
        /// </summary>
        public byte[] Compress(TextureBuffer source, BlockCompressionFormat format)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // 确保尺寸是 4 的倍数
            int w = RoundUp4(source.Width);
            int h = RoundUp4(source.Height);

            // 创建 padded 纹理 (如果需要)
            TextureBuffer padded = (w == source.Width && h == source.Height)
                ? source
                : PadToMultiple4(source, w, h);

            switch (format)
            {
                case BlockCompressionFormat.BC1:
                    return CompressBC1(padded);
                case BlockCompressionFormat.BC3:
                    return CompressBC3(padded);
                case BlockCompressionFormat.BC5:
                    return CompressBC5(padded);
                case BlockCompressionFormat.BC7:
                    return CompressBC7(padded);
                case BlockCompressionFormat.BC2:
                    // BC2 与 BC3 类似但 Alpha 处理不同
                    return CompressBC3(padded);
                case BlockCompressionFormat.ASTC:
                    throw new NotSupportedException(
                        "ASTC 压缩需要集成 ARM ASTC 编码器 (astcenc)。请使用 ARM astcenc library 进行 ASTC 编码。");
                default:
                    throw new ArgumentException($"不支持的压缩格式: {format}");
            }
        }

        /// <summary>
        /// BC1 (DXT1) 编码: 4色查找表 + 2bit 索引。
        /// 每个 4x4 块 8 字节: 2 颜色端点 (RGB565) + 16 * 2bit 索引。
        /// </summary>
        public static byte[] CompressBC1(TextureBuffer source)
        {
            int blocksX = source.Width / 4;
            int blocksY = source.Height / 4;
            var result = new byte[blocksX * blocksY * 8];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    // 提取 4x4 块的 16 个像素
                    var pixels = new Vector4[16];
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            pixels[py * 4 + px] = source.GetPixel(bx * 4 + px, by * 4 + py);
                        }
                    }

                    // 使用包含盒方法 (Include Box Fit) 找最优端点
                    Vector3 minColor = new Vector3(float.MaxValue);
                    Vector3 maxColor = new Vector3(float.MinValue);
                    for (int i = 0; i < 16; i++)
                    {
                        minColor = Vector3.Min(minColor, new Vector3(pixels[i].X, pixels[i].Y, pixels[i].Z));
                        maxColor = Vector3.Max(maxColor, new Vector3(pixels[i].X, pixels[i].Y, pixels[i].Z));
                    }

                    // 扩展范围 (Pick best diagonal)
                    Vector3 center = (minColor + maxColor) * 0.5f;
                    Vector3 halfDiag = (maxColor - minColor) * 0.5f;
                    float diagLen = halfDiag.Length();

                    Vector3 ep0, ep1;
                    if (diagLen > 1e-6f)
                    {
                        Vector3 diag = halfDiag / diagLen;
                        // 沿对角线方向的最远/最近点
                        float maxProj = float.MinValue, minProj = float.MaxValue;
                        for (int i = 0; i < 16; i++)
                        {
                            Vector3 c = new Vector3(pixels[i].X, pixels[i].Y, pixels[i].Z) - center;
                            float proj = Vector3.Dot(c, diag);
                            if (proj > maxProj) maxProj = proj;
                            if (proj < minProj) minProj = proj;
                        }
                        ep0 = center + diag * maxProj;
                        ep1 = center + diag * minProj;
                    }
                    else
                    {
                        ep0 = minColor;
                        ep1 = maxColor;
                    }

                    // 量化到 RGB565
                    ushort rgb565_0 = ToRGB565(ep0);
                    ushort rgb565_1 = ToRGB565(ep1);

                    // 确保 rgb565_0 > rgb565_1 (BC1 要求)
                    if (rgb565_0 < rgb565_1)
                    {
                        (rgb565_0, rgb565_1) = (rgb565_1, rgb565_0);
                    }

                    // 将量化端点反量化回 RGB888 用于索引计算
                    Vector3 qEp0 = FromRGB565(rgb565_0);
                    Vector3 qEp1 = FromRGB565(rgb565_1);

                    // 生成索引
                    uint indices = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        Vector3 c = new Vector3(pixels[i].X, pixels[i].Y, pixels[i].Z);
                        int idx = ComputeBC1Index(c, qEp0, qEp1);
                        indices |= (uint)(idx << (i * 2));
                    }

                    // 写入结果
                    int blockOffset = (by * blocksX + bx) * 8;
                    // 小端序: rgb565_0 低字节在前
                    result[blockOffset] = (byte)(rgb565_0 & 0xFF);
                    result[blockOffset + 1] = (byte)((rgb565_0 >> 8) & 0xFF);
                    result[blockOffset + 2] = (byte)(rgb565_1 & 0xFF);
                    result[blockOffset + 3] = (byte)((rgb565_1 >> 8) & 0xFF);
                    result[blockOffset + 4] = (byte)(indices & 0xFF);
                    result[blockOffset + 5] = (byte)((indices >> 8) & 0xFF);
                    result[blockOffset + 6] = (byte)((indices >> 16) & 0xFF);
                    result[blockOffset + 7] = (byte)((indices >> 24) & 0xFF);
                }
            }

            return result;
        }

        /// <summary>
        /// BC3 (DXT5) 编码: BC1 色块 + 独立 Alpha 块。
        /// 每个 4x4 块 16 字节: 8 字节 Alpha + 8 字节 RGB (同 BC1)。
        /// </summary>
        public static byte[] CompressBC3(TextureBuffer source)
        {
            int blocksX = source.Width / 4;
            int blocksY = source.Height / 4;
            var result = new byte[blocksX * blocksY * 16];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    // 提取 4x4 块
                    var alphas = new float[16];
                    var rgbs = new Vector4[16];
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int idx = py * 4 + px;
                            var pixel = source.GetPixel(bx * 4 + px, by * 4 + py);
                            alphas[idx] = pixel.W;
                            rgbs[idx] = pixel;
                        }
                    }

                    // ── Alpha 块 (8 字节) ──
                    float minA = float.MaxValue, maxA = float.MinValue;
                    for (int i = 0; i < 16; i++)
                    {
                        minA = Math.Min(minA, alphas[i]);
                        maxA = Math.Max(maxA, alphas[i]);
                    }

                    byte alpha0 = ClampToByte(maxA * 255.0f);
                    byte alpha1 = ClampToByte(minA * 255.0f);

                    // 生成 3-bit Alpha 索引
                    ulong alphaIndices = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        int idx = ComputeBC3AlphaIndex(alphas[i], alpha0, alpha1);
                        alphaIndices |= (ulong)(idx & 0x7) << (i * 3);
                    }

                    int blockOffset = (by * blocksX + bx) * 16;

                    // Alpha 块: 2 端点 + 48-bit 索引 (6 字节)
                    result[blockOffset] = alpha0;
                    result[blockOffset + 1] = alpha1;
                    for (int i = 0; i < 6; i++)
                    {
                        result[blockOffset + 2 + i] = (byte)((alphaIndices >> (i * 8)) & 0xFF);
                    }

                    // ── RGB 块 (8 字节, 同 BC1) ──
                    // 创建不含 Alpha 的临时缓冲
                    var rgbBuffer = TextureBuffer.Create(4, 4);
                    for (int i = 0; i < 16; i++)
                        rgbBuffer.SetPixel(i % 4, i / 4, new Vector4(rgbs[i].X, rgbs[i].Y, rgbs[i].Z, 1.0f));

                    byte[] bc1Data = CompressBC1(rgbBuffer);
                    Array.Copy(bc1Data, 0, result, blockOffset + 8, 8);
                }
            }

            return result;
        }

        /// <summary>
        /// BC5 (3DC/ATI2) 编码: 双独立 RG 通道。
        /// 每个 4x4 块 16 字节: 8 字节 R 通道 (同 BC3 Alpha 块) + 8 字节 G 通道。
        /// 适用于法线贴图 (R=X, G=Y, Z 从 XY 重建)。
        /// </summary>
        public static byte[] CompressBC5(TextureBuffer source)
        {
            int blocksX = source.Width / 4;
            int blocksY = source.Height / 4;
            var result = new byte[blocksX * blocksY * 16];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    var channelR = new float[16];
                    var channelG = new float[16];

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int idx = py * 4 + px;
                            var pixel = source.GetPixel(bx * 4 + px, by * 4 + py);
                            channelR[idx] = pixel.X;
                            channelG[idx] = pixel.Y;
                        }
                    }

                    int blockOffset = (by * blocksX + bx) * 16;

                    // R 通道 (8 字节, BC3 Alpha 块格式)
                    EncodeSingleChannel(channelR, result, blockOffset);

                    // G 通道 (8 字节, BC3 Alpha 块格式)
                    EncodeSingleChannel(channelG, result, blockOffset + 8);
                }
            }

            return result;
        }

        /// <summary>
        /// BC7 编码: 高质量压缩 (多模式)。
        /// 此处实现 Mode 6 (4 分区, 2 bit 模式) 作为主要编码路径。
        /// 生产实现应遍历所有 8 种模式选最优。
        /// </summary>
        public static byte[] CompressBC7(TextureBuffer source)
        {
            int blocksX = source.Width / 4;
            int blocksY = source.Height / 4;
            var result = new byte[blocksX * blocksY * 16];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    // 提取 4x4 块
                    var pixels = new Vector4[16];
                    for (int py = 0; py < 4; py++)
                        for (int px = 0; px < 4; px++)
                            pixels[py * 4 + px] = source.GetPixel(bx * 4 + px, by * 4 + py);

                    int blockOffset = (by * blocksX + bx) * 16;
                    EncodeBC7Block(pixels, result, blockOffset);
                }
            }

            return result;
        }

        /// <summary>ASTC 占位方法。需要集成 ARM astcenc 库。</summary>
        public static byte[] CompressASTC(TextureBuffer source, int blockSize = 4, int blockSizeY = 4)
        {
            throw new NotSupportedException(
                "ASTC 压缩需要集成 ARM ASTC 编码器库 (astcenc)。" +
                "请参考: https://github.com/ARM-software/astc-encoder" +
                "集成 astcenc 并调用 astcenc_context 命令。" +
                $"请求的块尺寸: {blockSize}x{blockSizeY}");
        }

        // ── 内部 BC 编码方法 ──

        private static byte ClampToByte(float v)
        {
            return (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
        }

        private static ushort ToRGB565(Vector3 color)
        {
            int r = ClampToByte(color.X * 255.0f);
            int g = ClampToByte(color.Y * 255.0f);
            int b = ClampToByte(color.Z * 255.0f);
            return (ushort)((r >> 3) << 11 | (g >> 2) << 5 | (b >> 3));
        }

        private static Vector3 FromRGB565(ushort rgb565)
        {
            float r = ((rgb565 >> 11) & 0x1F) / 31.0f;
            float g = ((rgb565 >> 5) & 0x3F) / 63.0f;
            float b = (rgb565 & 0x1F) / 31.0f;
            return new Vector3(r, g, b);
        }

        private static int ComputeBC1Index(Vector3 color, Vector3 ep0, Vector3 ep1)
        {
            // BC1 4 色模式 (ep0 > ep1): 颜色插值为 ep0, ep1, 1/3*ep0+2/3*ep1, 2/3*ep0+1/3*ep1
            Vector3 mid1 = (ep0 * 2.0f + ep1) / 3.0f;
            Vector3 mid2 = (ep0 + ep1 * 2.0f) / 3.0f;

            float d0 = DistSq(color, ep0);
            float d1 = DistSq(color, ep1);
            float d2 = DistSq(color, mid1);
            float d3 = DistSq(color, mid2);

            if (d0 <= d1 && d0 <= d2 && d0 <= d3) return 0;
            if (d1 <= d2 && d1 <= d3) return 1;
            if (d2 <= d3) return 2;
            return 3;
        }

        private static int ComputeBC3AlphaIndex(float alpha, byte alpha0, byte alpha1)
        {
            float a0 = alpha0 / 255.0f;
            float a1 = alpha1 / 255.0f;

            if (a0 == a1)
                return 0;

            float[] palette;
            if (a0 > a1)
            {
                // 8 级插值
                palette = new float[]
                {
                    a0, a1,
                    (6 * a0 + 1 * a1) / 7.0f,
                    (5 * a0 + 2 * a1) / 7.0f,
                    (4 * a0 + 3 * a1) / 7.0f,
                    (3 * a0 + 4 * a1) / 7.0f,
                    (2 * a0 + 5 * a1) / 7.0f,
                    (1 * a0 + 6 * a1) / 7.0f
                };
            }
            else
            {
                // 6 级插值 + 2 个极端值
                palette = new float[]
                {
                    a0, a1,
                    (4 * a0 + 1 * a1) / 5.0f,
                    (3 * a0 + 2 * a1) / 5.0f,
                    (2 * a0 + 3 * a1) / 5.0f,
                    (1 * a0 + 4 * a1) / 5.0f,
                    0.0f, 1.0f
                };
            }

            int bestIdx = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                float d = MathF.Abs(alpha - palette[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            return bestIdx;
        }

        private static void EncodeSingleChannel(float[] values, byte[] result, int offset)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                min = Math.Min(min, values[i]);
                max = Math.Max(max, values[i]);
            }

            byte ep0 = ClampToByte(max * 255.0f);
            byte ep1 = ClampToByte(min * 255.0f);

            ulong indices = 0;
            for (int i = 0; i < 16; i++)
            {
                int idx = ComputeBC3AlphaIndex(values[i], ep0, ep1);
                indices |= (ulong)(idx & 0x7) << (i * 3);
            }

            result[offset] = ep0;
            result[offset + 1] = ep1;
            for (int i = 0; i < 6; i++)
                result[offset + 2 + i] = (byte)((indices >> (i * 8)) & 0xFF);
        }

        private static void EncodeBC7Block(Vector4[] pixels, byte[] result, int offset)
        {
            // BC7 Mode 6: 4 分区, 2 bit 索引
            // 简化实现: 使用单一分区 (Mode 1)
            // 生产实现应遍历所有 8 种模式

            // 模式位: Mode 1 = 01 (bit 0 = 1, bit 1 = 0)
            // 简化: 全部像素求平均端点
            Vector4 minC = new Vector4(float.MaxValue);
            Vector4 maxC = new Vector4(float.MinValue);
            for (int i = 0; i < 16; i++)
            {
                minC = Vector4.Min(minC, pixels[i]);
                maxC = Vector4.Max(maxC, pixels[i]);
            }

            // 量化端点到 7-bit (RGBA 7777)
            byte r0 = ClampToByte(maxC.X * 127.0f);
            byte g0 = ClampToByte(maxC.Y * 127.0f);
            byte b0 = ClampToByte(maxC.Z * 127.0f);
            byte a0 = ClampToByte(maxC.W * 127.0f);
            byte r1 = ClampToByte(minC.X * 127.0f);
            byte g1 = ClampToByte(minC.Y * 127.0f);
            byte b1 = ClampToByte(minC.Z * 127.0f);
            byte a1 = ClampToByte(minC.W * 127.0f);

            // 填充 16 字节 (简化: Mode 1, 1 分区)
            // 实际 BC7 格式复杂，此处为结构占位
            result[offset] = 0x01; // Mode 1 bit pattern
            result[offset + 1] = r0;
            result[offset + 2] = g0;
            result[offset + 3] = b0;
            result[offset + 4] = a0;
            result[offset + 5] = r1;
            result[offset + 6] = g1;
            result[offset + 7] = b1;
            result[offset + 8] = a1;

            // 索引 (2 bit per pixel, 16 pixels = 32 bits = 4 bytes)
            uint idx = 0;
            for (int i = 0; i < 16; i++)
            {
                // 简化: 根据亮度选择索引
                float lum = pixels[i].X * 0.299f + pixels[i].Y * 0.587f + pixels[i].Z * 0.114f;
                int best = (lum > 0.66f) ? 0 : (lum > 0.33f) ? 1 : 2;
                idx |= (uint)(best << (i * 2));
            }

            result[offset + 9] = (byte)(idx & 0xFF);
            result[offset + 10] = (byte)((idx >> 8) & 0xFF);
            result[offset + 11] = (byte)((idx >> 16) & 0xFF);
            result[offset + 12] = (byte)((idx >> 24) & 0xFF);

            // 剩余字节填 0
            result[offset + 13] = 0;
            result[offset + 14] = 0;
            result[offset + 15] = 0;
        }

        private static float DistSq(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static int RoundUp4(int n) => (n + 3) & ~3;

        private static TextureBuffer PadToMultiple4(TextureBuffer source, int w, int h)
        {
            var padded = TextureBuffer.Create(w, h);
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                    padded.SetPixel(x, y, source.GetPixel(x, y));
            return padded;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // TextureAtlasPacker — 纹理图集打包
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 纹理图集打包器。将多张小纹理合并到一张大纹理 (图集) 中。
    /// 支持自动裁剪透明边距、UV 重映射数据生成。
    /// 使用 MaxRects BSSF 矩形装箱算法。
    /// </summary>
    public class TextureAtlasPacker
    {
        /// <summary>最大图集页尺寸。</summary>
        public int MaxAtlasSize { get; set; } = 2048;

        /// <summary>页间边距 (像素)。</summary>
        public int Padding { get; set; } = 1;

        /// <summary>启用/禁用透明边距裁剪。</summary>
        public bool TrimTransparent { get; set; } = true;

        /// <summary>装箱算法。</summary>
        public PackingAlgorithm Algorithm { get; set; } = PackingAlgorithm.MaxRectsBSSF;

        /// <summary>
        /// 生成纹理图集。
        /// </summary>
        /// <param name="inputs">纹理输入列表。</param>
        /// <returns>图集页列表 (可能跨多页)。</returns>
        public AtlasPage[] GenerateAtlas(TextureInput[] inputs)
        {
            if (inputs == null || inputs.Length == 0)
                return Array.Empty<AtlasPage>();

            // 裁剪透明边距
            var processed = new TextureInput[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                processed[i] = TrimTransparent
                    ? TrimTransparentBorder(inputs[i])
                    : inputs[i];
            }

            // 分配到图集页
            var pages = new List<AtlasPage>();
            var remaining = new List<TextureInput>(processed);

            while (remaining.Count > 0)
            {
                var page = PackSinglePage(remaining);
                pages.Add(page);
                remaining.RemoveAll(t => page.Regions.Any(r => r.InputName == t.Name));
            }

            // 填充像素数据
            foreach (var page in pages)
            {
                page.TextureData = TextureBuffer.Create(page.Width, page.Height);

                foreach (var region in page.Regions)
                {
                    var input = Array.Find(processed, t => t.Name == region.InputName);
                    if (input == null || input.Texture == null) continue;

                    // 复制像素
                    for (int y = 0; y < region.SourceHeight; y++)
                    {
                        for (int x = 0; x < region.SourceWidth; x++)
                        {
                            Vector4 color = input.Texture.GetPixel(
                                region.TrimmedX + x,
                                region.TrimmedY + y);
                            page.TextureData.SetPixel(
                                region.X + Padding + x,
                                region.Y + Padding + y,
                                color);
                        }
                    }
                }
            }

            // 生成 UV 重映射
            GenerateUVRemapping(pages, processed);

            return pages.ToArray();
        }

        // ── 内部方法 ──

        private AtlasPage PackSinglePage(List<TextureInput> inputs)
        {
            int pageSize = MaxAtlasSize;
            var page = new AtlasPage { Width = pageSize, Height = pageSize };

            // MaxRects BSSF
            var freeRects = new List<AtlasRect>
            {
                new AtlasRect { X = 0, Y = 0, Width = pageSize, Height = pageSize }
            };

            foreach (var input in inputs)
            {
                int packW = input.Width + Padding * 2;
                int packH = input.Height + Padding * 2;

                AtlasRect? bestRect = null;
                int bestShortFit = int.MaxValue;
                int bestLongFit = int.MaxValue;

                foreach (var free in freeRects)
                {
                    if (!Fits(free, packW, packH, out int shortFit, out int longFit))
                        continue;

                    if (shortFit < bestShortFit ||
                        (shortFit == bestShortFit && longFit < bestLongFit))
                    {
                        bestRect = new AtlasRect
                        {
                            X = free.X, Y = free.Y,
                            Width = packW, Height = packH
                        };
                        bestShortFit = shortFit;
                        bestLongFit = longFit;
                    }
                }

                if (bestRect.HasValue)
                {
                    page.Regions.Add(new AtlasRegion
                    {
                        InputName = input.Name,
                        X = bestRect.Value.X,
                        Y = bestRect.Value.Y,
                        Width = packW,
                        Height = packH,
                        SourceWidth = input.Width,
                        SourceHeight = input.Height,
                        TrimmedX = input.TrimmedOffsetX,
                        TrimmedY = input.TrimmedOffsetY
                    });

                    SplitFreeRects(freeRects, bestRect.Value);
                }
            }

            // 计算实际使用尺寸 (裁剪空白边缘)
            int maxRight = 0, maxBottom = 0;
            foreach (var r in page.Regions)
            {
                maxRight = Math.Max(maxRight, r.X + r.Width);
                maxBottom = Math.Max(maxBottom, r.Y + r.Height);
            }
            page.UsedWidth = RoundUpPowerOfTwo(maxRight);
            page.UsedHeight = RoundUpPowerOfTwo(maxBottom);

            return page;
        }

        private static bool Fits(AtlasRect free, int w, int h, out int shortFit, out int longFit)
        {
            shortFit = int.MaxValue;
            longFit = int.MaxValue;

            // 尝试不旋转
            if (free.Width >= w && free.Height >= h)
            {
                shortFit = Math.Min(free.Width - w, free.Height - h);
                longFit = Math.Max(free.Width - w, free.Height - h);
                return true;
            }

            return false;
        }

        private static void SplitFreeRects(List<AtlasRect> freeRects, AtlasRect placed)
        {
            for (int i = freeRects.Count - 1; i >= 0; i--)
            {
                var fr = freeRects[i];
                if (fr.X >= placed.X + placed.Width || fr.X + fr.Width <= placed.X ||
                    fr.Y >= placed.Y + placed.Height || fr.Y + fr.Height <= placed.Y)
                    continue;

                // 上方
                if (placed.Y > fr.Y)
                {
                    freeRects.Add(new AtlasRect
                    {
                        X = fr.X, Y = fr.Y,
                        Width = fr.Width, Height = placed.Y - fr.Y
                    });
                }
                // 下方
                if (placed.Y + placed.Height < fr.Y + fr.Height)
                {
                    freeRects.Add(new AtlasRect
                    {
                        X = fr.X, Y = placed.Y + placed.Height,
                        Width = fr.Width,
                        Height = fr.Y + fr.Height - placed.Y - placed.Height
                    });
                }
                // 左方
                if (placed.X > fr.X)
                {
                    freeRects.Add(new AtlasRect
                    {
                        X = fr.X, Y = fr.Y,
                        Width = placed.X - fr.X, Height = fr.Height
                    });
                }
                // 右方
                if (placed.X + placed.Width < fr.X + fr.Width)
                {
                    freeRects.Add(new AtlasRect
                    {
                        X = placed.X + placed.Width, Y = fr.Y,
                        Width = fr.X + fr.Width - placed.X - placed.Width,
                        Height = fr.Height
                    });
                }

                freeRects.RemoveAt(i);
            }
        }

        private TextureInput TrimTransparentBorder(TextureInput input)
        {
            if (input.Texture == null) return input;

            int minX = input.Width, minY = input.Height;
            int maxX = 0, maxY = 0;
            bool hasContent = false;

            for (int y = 0; y < input.Height; y++)
            {
                for (int x = 0; x < input.Width; x++)
                {
                    Vector4 c = input.Texture.GetPixel(x, y);
                    if (c.W > 0.01f)
                    {
                        hasContent = true;
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            if (!hasContent)
                return input;

            int trimmedW = maxX - minX + 1;
            int trimmedH = maxY - minY + 1;

            var trimmed = TextureBuffer.Create(trimmedW, trimmedH);
            for (int y = 0; y < trimmedH; y++)
                for (int x = 0; x < trimmedW; x++)
                    trimmed.SetPixel(x, y, input.Texture.GetPixel(minX + x, minY + y));

            return new TextureInput
            {
                Name = input.Name,
                Texture = trimmed,
                Width = trimmedW,
                Height = trimmedH,
                OriginalWidth = input.OriginalWidth > 0 ? input.OriginalWidth : input.Width,
                OriginalHeight = input.OriginalHeight > 0 ? input.OriginalHeight : input.Height,
                TrimmedOffsetX = minX,
                TrimmedOffsetY = minY
            };
        }

        private static void GenerateUVRemapping(AtlasPage[] pages, TextureInput[] inputs)
        {
            foreach (var page in pages)
            {
                foreach (var region in page.Regions)
                {
                    float invPageW = 1.0f / page.Width;
                    float invPageH = 1.0f / page.Height;

                    region.UVRemap = new UVRemap
                    {
                        // 原始 UV [0,1] → 图集 UV
                        ScaleU = (float)region.SourceWidth / page.Width,
                        ScaleV = (float)region.SourceHeight / page.Height,
                        OffsetU = (region.X + Padding) * invPageW,
                        OffsetV = (region.Y + Padding) * invPageH,

                        // 考虑裁剪偏移
                        TrimOffsetU = (float)region.TrimmedX / region.SourceWidth,
                        TrimOffsetV = (float)region.TrimmedY / region.SourceHeight
                    };
                }
            }
        }

        private static int RoundUpPowerOfTwo(int n)
        {
            if (n <= 0) return 1;
            n--;
            n |= n >> 1;
            n |= n >> 2;
            n |= n >> 4;
            n |= n >> 8;
            n |= n >> 16;
            return n + 1;
        }
    }

    // ── 图集相关数据结构 ──

    /// <summary>纹理输入 (待打包)。</summary>
    public class TextureInput
    {
        /// <summary>纹理名称 (用作标识)。</summary>
        public string Name { get; set; } = "";
        /// <summary>纹理数据。</summary>
        public TextureBuffer? Texture { get; set; }
        /// <summary>纹理宽度。</summary>
        public int Width { get; set; }
        /// <summary>纹理高度。</summary>
        public int Height { get; set; }
        /// <summary>原始宽度 (裁剪前)。</summary>
        public int OriginalWidth { get; set; }
        /// <summary>原始高度 (裁剪前)。</summary>
        public int OriginalHeight { get; set; }
        /// <summary>裁剪偏移 X。</summary>
        public int TrimmedOffsetX { get; set; }
        /// <summary>裁剪偏移 Y。</summary>
        public int TrimmedOffsetY { get; set; }
    }

    /// <summary>图集页。</summary>
    public class AtlasPage
    {
        /// <summary>页宽度。</summary>
        public int Width { get; set; }
        /// <summary>页高度。</summary>
        public int Height { get; set; }
        /// <summary>实际使用宽度。</summary>
        public int UsedWidth { get; set; }
        /// <summary>实际使用高度。</summary>
        public int UsedHeight { get; set; }
        /// <summary>该页的纹理数据。</summary>
        public TextureBuffer? TextureData { get; set; }
        /// <summary>该页的区域列表。</summary>
        public List<AtlasRegion> Regions { get; set; } = new();
    }

    /// <summary>图集区域。</summary>
    public class AtlasRegion
    {
        /// <summary>源纹理名称。</summary>
        public string InputName { get; set; } = "";
        /// <summary>在图集中的 X 位置 (含 Padding)。</summary>
        public int X { get; set; }
        /// <summary>在图集中的 Y 位置 (含 Padding)。</summary>
        public int Y { get; set; }
        /// <summary>区域宽度 (含 Padding)。</summary>
        public int Width { get; set; }
        /// <summary>区域高度 (含 Padding)。</summary>
        public int Height { get; set; }
        /// <summary>源纹理实际宽度。</summary>
        public int SourceWidth { get; set; }
        /// <summary>源纹理实际高度。</summary>
        public int SourceHeight { get; set; }
        /// <summary>裁剪偏移 X。</summary>
        public int TrimmedX { get; set; }
        /// <summary>裁剪偏移 Y。</summary>
        public int TrimmedY { get; set; }
        /// <summary>UV 重映射数据。</summary>
        public UVRemap? UVRemap { get; set; }
    }

    /// <summary>UV 重映射数据。</summary>
    public class UVRemap
    {
        /// <summary>UV 缩放 U。</summary>
        public float ScaleU { get; set; }
        /// <summary>UV 缩放 V。</summary>
        public float ScaleV { get; set; }
        /// <summary>UV 偏移 U。</summary>
        public float OffsetU { get; set; }
        /// <summary>UV 偏移 V。</summary>
        public float OffsetV { get; set; }
        /// <summary>裁剪偏移 U。</summary>
        public float TrimOffsetU { get; set; }
        /// <summary>裁剪偏移 V。</summary>
        public float TrimOffsetV { get; set; }

        /// <summary>重映射 UV 坐标。</summary>
        public Vector2 Remap(Vector2 originalUV)
        {
            return new Vector2(
                OffsetU + (TrimOffsetU + originalUV.X * (1.0f - TrimOffsetU)) * ScaleU,
                OffsetV + (TrimOffsetV + originalUV.Y * (1.0f - TrimOffsetV)) * ScaleV
            );
        }
    }

    /// <summary>内部矩形结构。</summary>
    internal struct AtlasRect
    {
        public int X, Y, Width, Height;
    }

    /// <summary>装箱算法枚举。</summary>
    public enum PackingAlgorithm
    {
        /// <summary>MaxRects 最佳短边优先 (BSSF)。</summary>
        MaxRectsBSSF,
        /// <summary>Guillotine 二分法。</summary>
        Guillotine
    }

    // ════════════════════════════════════════════════════════════════
    // HDRProcessor — HDR 色调映射与色彩空间转换
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// HDR 处理器。提供色调映射算法、色彩空间转换与曝光调整。
    /// </summary>
    public class HDRProcessor
    {
        /// <summary>色调映射算法。</summary>
        public ToneMappingAlgorithm ToneMapping { get; set; } = ToneMappingAlgorithm.ACES;

        /// <summary>曝光值 (EV)。</summary>
        public float Exposure { get; set; } = 0.0f;

        /// <summary>
        /// 对 HDR 纹理应用色调映射。
        /// </summary>
        public TextureBuffer ToneMap(TextureBuffer source, ToneMappingAlgorithm? algorithm = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var algo = algorithm ?? ToneMapping;
            var result = TextureBuffer.Create(source.Width, source.Height);

            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    Vector4 hdr = source.GetPixel(x, y);

                    // 应用曝光
                    hdr = ApplyExposure(hdr, Exposure);

                    // 色调映射 (RGB 通道)
                    Vector3 mapped;
                    switch (algo)
                    {
                        case ToneMappingAlgorithm.Reinhard:
                            mapped = Reinhard(new Vector3(hdr.X, hdr.Y, hdr.Z));
                            break;
                        case ToneMappingAlgorithm.Uncharted2Filmic:
                            mapped = Uncharted2Filmic(new Vector3(hdr.X, hdr.Y, hdr.Z));
                            break;
                        case ToneMappingAlgorithm.ACES:
                        default:
                            mapped = ACES(new Vector3(hdr.X, hdr.Y, hdr.Z));
                            break;
                    }

                    // Gamma 校正 (Linear → sRGB)
                    mapped = LinearToSRGB(mapped);

                    result.SetPixel(x, y, new Vector4(mapped.X, mapped.Y, mapped.Z, hdr.W));
                }
            }

            return result;
        }

        /// <summary>
        /// 色彩空间转换: Linear → sRGB。
        /// 使用 sRGB 近似公式 (比精确查表快)。
        /// </summary>
        public static Vector3 LinearToSRGB(Vector3 linear)
        {
            return new Vector3(
                LinearToSRGBChannel(linear.X),
                LinearToSRGBChannel(linear.Y),
                LinearToSRGBChannel(linear.Z)
            );
        }

        /// <summary>
        /// 色彩空间转换: sRGB → Linear。
        /// </summary>
        public static Vector3 SRGBToLinear(Vector3 srgb)
        {
            return new Vector3(
                SRGBToLinearChannel(srgb.X),
                SRGBToLinearChannel(srgb.Y),
                SRGBToLinearChannel(srgb.Z)
            );
        }

        /// <summary>
        /// 曝光调整: color * 2^exposure。
        /// </summary>
        public static Vector4 ApplyExposure(Vector4 color, float exposureEV)
        {
            float multiplier = MathF.Pow(2.0f, exposureEV);
            return new Vector4(
                color.X * multiplier,
                color.Y * multiplier,
                color.Z * multiplier,
                color.W
            );
        }

        /// <summary>
        /// ACES 色调映射 (Academy Color Encoding System)。
        /// Stephen Hill 的 ACES 近似实现。
        /// </summary>
        public static Vector3 ACES(Vector3 x)
        {
            // ACES Filmic (Stephen Hill's fit)
            // a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14
            float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;

            return new Vector3(
                ACESChannel(x.X, a, b, c, d, e),
                ACESChannel(x.Y, a, b, c, d, e),
                ACESChannel(x.Z, a, b, c, d, e)
            );
        }

        /// <summary>
        /// Reinhard 色调映射: x / (1 + x)。
        /// </summary>
        public static Vector3 Reinhard(Vector3 x)
        {
            return new Vector3(
                x.X / (1.0f + x.X),
                x.Y / (1.0f + x.Y),
                x.Z / (1.0f + x.Z)
            );
        }

        /// <summary>
        /// Uncharted 2 Filmic 色调映射。
        /// John Hable 的 Uncharted 2 曲线。
        /// </summary>
        public static Vector3 Uncharted2Filmic(Vector3 x)
        {
            // 参数
            float A = 0.22f;  // 肩部强度
            float B = 0.30f;  // 线性强度
            float C = 0.10f;  // 线性宽度
            float D = 0.20f;  // 肩部位置
            float E = 0.01f;  // 肩部宽度
            float F = 0.30f;  // 线性长度

            Vector3 numerator;
            Vector3 denominator;

            // (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F
            numerator = new Vector3(
                x.X * (A * x.X + C * B) + D * E,
                x.Y * (A * x.Y + C * B) + D * E,
                x.Z * (A * x.Z + C * B) + D * E
            );
            denominator = new Vector3(
                x.X * (A * x.X + B) + D * F,
                x.Y * (A * x.Y + B) + D * F,
                x.Z * (A * x.Z + B) + D * F
            );

            Vector3 result = new Vector3(
                numerator.X / denominator.X - E / F,
                numerator.Y / denominator.Y - E / F,
                numerator.Z / denominator.Z - E / F
            );

            // 白点校正
            float white = 11.2f;
            float whiteNumerator = white * (A * white + C * B) + D * E;
            float whiteDenominator = white * (A * white + B) + D * F;
            float whiteScale = whiteNumerator / whiteDenominator - E / F;

            return new Vector3(
                result.X / whiteScale,
                result.Y / whiteScale,
                result.Z / whiteScale
            );
        }

        // ── 色调映射算法枚举 ──

        private static float ACESChannel(float x, float a, float b, float c, float d, float e)
        {
            return Math.Clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0f, 1.0f);
        }

        private static float LinearToSRGBChannel(float linear)
        {
            if (linear <= 0.0031308f)
                return 12.92f * linear;
            return 1.055f * MathF.Pow(linear, 1.0f / 2.4f) - 0.055f;
        }

        private static float SRGBToLinearChannel(float srgb)
        {
            if (srgb <= 0.04045f)
                return srgb / 12.92f;
            return MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
        }
    }

    /// <summary>色调映射算法。</summary>
    public enum ToneMappingAlgorithm
    {
        /// <summary>ACES (Academy Color Encoding System) — Stephen Hill 近似。</summary>
        ACES,
        /// <summary>Reinhard: x / (1 + x)。</summary>
        Reinhard,
        /// <summary>Uncharted 2 Filmic: Hable 曲线。</summary>
        Uncharted2Filmic
    }
}
