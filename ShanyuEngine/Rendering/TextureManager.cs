// ════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 纹理管理器 (Texture Manager)
//
// 命名空间: ShanyuEngine.Rendering
// 功能:
//   - TextureFormat: 纹理格式枚举
//   - TextureImportSettings: 纹理导入选项
//   - TextureManager: 从文件加载纹理、创建 GPU 纹理、Mipmap 生成、缓存管理
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    // ══════════════════════════════════════════════════════════════
    // TextureFormat — 引擎纹理格式枚举
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 支持的 GPU 纹理格式。
    /// </summary>
    public enum TextureFormat
    {
        R8G8B8A8_UNorm,
        R16G16B16A16_Float,
        BC1,
        BC3,
        BC5,
        BC7
    }

    // ══════════════════════════════════════════════════════════════
    // TextureImportSettings — 纹理导入选项
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 纹理导入设置。控制 Mipmap 生成、寻址模式、过滤模式等。
    /// </summary>
    public sealed class TextureImportSettings
    {
        /// <summary>是否生成 Mipmap 链</summary>
        public bool GenerateMips { get; set; } = true;

        /// <summary>最大 Mipmap 级别（0 = 不限）</summary>
        public int MaxMipLevel { get; set; } = 0;

        /// <summary>纹理寻址模式</summary>
        public RHITextureAddressMode WrapMode { get; set; } = RHITextureAddressMode.Wrap;

        /// <summary>纹理过滤模式</summary>
        public RHIFilter FilterMode { get; set; } = RHIFilter.Linear;

        /// <summary>各向异性过滤级别（1 ~ 16）</summary>
        public int AnisotropyLevel { get; set; } = 4;

        /// <summary>是否存储在 sRGB 空间（用于 Albedo/Color 纹理）</summary>
        public bool sRGB { get; set; } = true;

        /// <summary>默认导入设置</summary>
        public static TextureImportSettings Default => new();
    }

    // ══════════════════════════════════════════════════════════════
    // TextureManager — 纹理资源管理
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 纹理管理器。负责从磁盘加载图像、上传到 GPU、Mipmap 生成以及生命周期管理。
    /// </summary>
    public sealed class TextureManager : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<string, CachedTexture> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>未使用纹理释放阈值（秒）</summary>
        public float UnusedReleaseThreshold { get; set; } = 60.0f;

        /// <summary>缓存命中统计</summary>
        public int CacheHits { get; private set; }

        /// <summary>缓存未命中统计</summary>
        public int CacheMisses { get; private set; }

        public TextureManager(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        // ──────────────────────────────────────────────────────────
        // 核心加载接口
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 从文件路径加载纹理。支持 PNG/JPG/TGA/BMP/EXR/HDR 格式。
        /// 优先返回缓存中的纹理，避免重复加载。
        /// </summary>
        public RHITexture LoadFromFile(string path, TextureImportSettings? settings = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("纹理路径不能为空", nameof(path));

            settings ??= TextureImportSettings.Default;

            lock (_lock)
            {
                // 检查缓存
                if (_cache.TryGetValue(path, out var cached))
                {
                    cached.LastAccessTime = DateTime.UtcNow;
                    cached.RefCount++;
                    CacheHits++;
                    return cached.Texture;
                }
            }

            CacheMisses++;

            // 读取图像数据
            var (width, height, pixelData, hasAlpha) = ReadImagePixels(path);

            // 选择格式
            var rhiFormat = ChooseRHIFormat(settings, hasAlpha);

            // 创建 GPU 纹理
            var texture = CreateTexture2D(width, height, rhiFormat, pixelData, settings.GenerateMips, settings.MaxMipLevel, path);

            // 创建 SRV（用于着色器采样）
            RHIDescriptorHandle? srvHandle = null;
            try { srvHandle = _device.CreateSRV(texture); }
            catch { /* SRV 创建失败不影响纹理本身 */ }

            lock (_lock)
            {
                _cache[path] = new CachedTexture
                {
                    Path = path,
                    Texture = texture,
                    SrvHandle = srvHandle,
                    ImportSettings = settings,
                    LastAccessTime = DateTime.UtcNow,
                    RefCount = 1
                };
            }

            return texture;
        }

        /// <summary>
        /// 从内存创建纹理（例如程序化生成的纹理）。
        /// </summary>
        public RHITexture CreateFromData(int width, int height, byte[] rgbaData, TextureFormat format = TextureFormat.R8G8B8A8_UNorm, bool generateMips = true, string? name = null)
        {
            if (rgbaData == null) throw new ArgumentNullException(nameof(rgbaData));
            if (rgbaData.Length < width * height * 4) throw new ArgumentException("像素数据长度不足", nameof(rgbaData));

            var rhiFormat = ConvertToRHIFormat(format);
            return CreateTexture2D(width, height, rhiFormat, rgbaData, generateMips, 0, name ?? "ProceduralTexture");
        }

        // ──────────────────────────────────────────────────────────
        // GPU 纹理创建
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 创建 2D GPU 纹理并上传初始数据。可选生成 Mipmap 链。
        /// </summary>
        public RHITexture CreateTexture2D(int width, int height, RHIFormat format, byte[] initialData, bool generateMips = true, int maxMipLevel = 0, string? name = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TextureManager));

            int mipLevels = 1;
            if (generateMips)
            {
                mipLevels = CalculateMipLevels(width, height);
                if (maxMipLevel > 0 && mipLevels > maxMipLevel)
                    mipLevels = maxMipLevel;
            }

            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = format,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = mipLevels,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.ShaderResource,
            };

            var texture = _device.CreateTexture(desc, name ?? "Texture2D");

            // 上传主纹理数据
            _device.UploadToTexture(texture, initialData.AsSpan(), width, height, mipIndex: 0);

            // 生成并上传 Mipmap
            if (generateMips && mipLevels > 1)
            {
                GenerateAndUploadMipmaps(texture, width, height, format, initialData, mipLevels);
            }

            texture.CurrentState = RHIResourceState.PixelShaderResource;
            return texture;
        }

        // ──────────────────────────────────────────────────────────
        // Mipmap 生成
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 使用 BoxFilter 降采样生成 Mipmap 链并上传到 GPU。
        /// </summary>
        private void GenerateAndUploadMipmaps(RHITexture texture, int srcWidth, int srcHeight, RHIFormat format, byte[] srcData, int mipLevels)
        {
            if (format != RHIFormat.R8G8B8A8_UNorm && format != RHIFormat.R8G8B8A8_UNorm_SRgb)
            {
                // 当前仅支持 R8G8B8A8 格式的 CPU 端 Mipmap 生成
                return;
            }

            int prevW = srcWidth;
            int prevH = srcHeight;
            byte[] prevData = srcData;

            for (int mip = 1; mip < mipLevels; mip++)
            {
                int mipW = Math.Max(1, prevW / 2);
                int mipH = Math.Max(1, prevH / 2);

                byte[] mipData = BoxFilterDownsample(prevData, prevW, prevH, mipW, mipH);
                _device.UploadToTexture(texture, mipData.AsSpan(), mipW, mipH, mipIndex: mip);

                prevW = mipW;
                prevH = mipH;
                prevData = mipData;
            }
        }

        /// <summary>
        /// BoxFilter 降采样：将源图像降采样到目标尺寸。
        /// </summary>
        public static byte[] BoxFilterDownsample(byte[] src, int srcW, int srcH, int dstW, int dstH)
        {
            byte[] dst = new byte[dstW * dstH * 4];

            float xRatio = (float)srcW / dstW;
            float yRatio = (float)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                for (int x = 0; x < dstW; x++)
                {
                    int srcX = (int)(x * xRatio);
                    int srcY = (int)(y * yRatio);
                    int srcX2 = Math.Min(srcX + (int)xRatio, srcW - 1);
                    int srcY2 = Math.Min(srcY + (int)yRatio, srcH - 1);

                    int r = 0, g = 0, b = 0, a = 0;
                    int count = 0;

                    for (int sy = srcY; sy <= srcY2; sy++)
                    {
                        for (int sx = srcX; sx <= srcX2; sx++)
                        {
                            int idx = (sy * srcW + sx) * 4;
                            r += src[idx];
                            g += src[idx + 1];
                            b += src[idx + 2];
                            a += src[idx + 3];
                            count++;
                        }
                    }

                    int dstIdx = (y * dstW + x) * 4;
                    dst[dstIdx] = (byte)(r / count);
                    dst[dstIdx + 1] = (byte)(g / count);
                    dst[dstIdx + 2] = (byte)(b / count);
                    dst[dstIdx + 3] = (byte)(a / count);
                }
            }

            return dst;
        }

        // ──────────────────────────────────────────────────────────
        // 图像文件读取 (ImageSharp)
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 读取图像文件并返回像素数据。支持 PNG/JPG/TGA/BMP/GIF/WebP/EXR/HDR。
        /// </summary>
        private static (int width, int height, byte[] pixels, bool hasAlpha) ReadImagePixels(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".hdr" || ext == ".exr")
            {
                // HDR/EXR 通过 ImageSharp 的浮点解码读取
                // ImageSharp 3.x 对 HDR 支持有限，此处回退到 RGBA32
                using var image = Image.Load(path);
                image.Mutate(x => x.AutoOrient());
                return ExtractPixels(image);
            }

            using var img = Image.Load(path);
            img.Mutate(x => x.AutoOrient());
            return ExtractPixels(img);
        }

        private static (int width, int height, byte[] pixels, bool hasAlpha) ExtractPixels(Image image)
        {
            int width = image.Width;
            int height = image.Height;
            byte[] pixels = new byte[width * height * 4];
            bool hasAlpha = false;

            if (image is Image<Rgba32> rgbaImage)
            {
                rgbaImage.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var span = accessor.GetRowSpan(y);
                        for (int x = 0; x < accessor.Width; x++)
                        {
                            var p = span[x];
                            int idx = (y * width + x) * 4;
                            pixels[idx] = p.R;
                            pixels[idx + 1] = p.G;
                            pixels[idx + 2] = p.B;
                            pixels[idx + 3] = p.A;
                            if (p.A < 255) hasAlpha = true;
                        }
                    }
                });
            }
            else if (image is Image<Rgb24> rgbImage)
            {
                rgbImage.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var span = accessor.GetRowSpan(y);
                        for (int x = 0; x < accessor.Width; x++)
                        {
                            var p = span[x];
                            int idx = (y * width + x) * 4;
                            pixels[idx] = p.R;
                            pixels[idx + 1] = p.G;
                            pixels[idx + 2] = p.B;
                            pixels[idx + 3] = 255;
                        }
                    }
                });
            }
            else
            {
                // 通用回退：转换为 Rgba32
                using var rgba = image.CloneAs<Rgba32>();
                rgba.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var span = accessor.GetRowSpan(y);
                        for (int x = 0; x < accessor.Width; x++)
                        {
                            var p = span[x];
                            int idx = (y * width + x) * 4;
                            pixels[idx] = p.R;
                            pixels[idx + 1] = p.G;
                            pixels[idx + 2] = p.B;
                            pixels[idx + 3] = p.A;
                            if (p.A < 255) hasAlpha = true;
                        }
                    }
                });
            }

            return (width, height, pixels, hasAlpha);
        }

        // ──────────────────────────────────────────────────────────
        // 缓存管理
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 获取已缓存的纹理（如不存在返回 null）。
        /// </summary>
        public RHITexture? GetCached(string path)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(path, out var cached))
                {
                    cached.LastAccessTime = DateTime.UtcNow;
                    return cached.Texture;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取已缓存纹理的 SRV 句柄（如不存在返回 null）。
        /// </summary>
        public RHIDescriptorHandle? GetCachedSRV(string path)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(path, out var cached))
                {
                    cached.LastAccessTime = DateTime.UtcNow;
                    return cached.SrvHandle;
                }
            }
            return null;
        }

        /// <summary>
        /// 释放超过指定时间未使用的纹理资源。
        /// </summary>
        public int ReleaseUnused(float? thresholdSeconds = null)
        {
            float threshold = thresholdSeconds ?? UnusedReleaseThreshold;
            var cutoff = DateTime.UtcNow.AddSeconds(-threshold);
            var toRemove = new List<string>();

            lock (_lock)
            {
                foreach (var kv in _cache)
                {
                    if (kv.Value.LastAccessTime < cutoff && kv.Value.RefCount <= 0)
                    {
                        toRemove.Add(kv.Key);
                    }
                }

                foreach (var key in toRemove)
                {
                    if (_cache.TryGetValue(key, out var cached))
                    {
                        try { cached.Texture?.Dispose(); } catch { }
                        _cache.Remove(key);
                    }
                }
            }

            if (toRemove.Count > 0)
            {
                Console.WriteLine($"[TextureManager] 释放 {toRemove.Count} 个未使用纹理");
            }

            return toRemove.Count;
        }

        /// <summary>
        /// 增加纹理引用计数。
        /// </summary>
        public void AddRef(string path)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(path, out var cached))
                    cached.RefCount++;
            }
        }

        /// <summary>
        /// 减少纹理引用计数。
        /// </summary>
        public void ReleaseRef(string path)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(path, out var cached))
                    cached.RefCount = Math.Max(0, cached.RefCount - 1);
            }
        }

        /// <summary>
        /// 清空所有缓存纹理。
        /// </summary>
        public void ClearCache()
        {
            lock (_lock)
            {
                foreach (var kv in _cache)
                {
                    try { kv.Value.Texture?.Dispose(); } catch { }
                }
                _cache.Clear();
            }
        }

        /// <summary>
        /// 获取当前缓存统计信息。
        /// </summary>
        public (int count, long estimatedBytes) GetStats()
        {
            lock (_lock)
            {
                long bytes = 0;
                foreach (var kv in _cache)
                {
                    var tex = kv.Value.Texture;
                    if (tex != null && tex.TextureDesc.HasValue)
                    {
                        var desc = tex.TextureDesc.Value;
                        bytes += (long)desc.Width * desc.Height * 4;
                    }
                }
                return (_cache.Count, bytes);
            }
        }

        // ──────────────────────────────────────────────────────────
        // 辅助方法
        // ──────────────────────────────────────────────────────────

        private static int CalculateMipLevels(int width, int height)
        {
            int maxDim = Math.Max(width, height);
            int levels = 1;
            while (maxDim > 1)
            {
                maxDim /= 2;
                levels++;
            }
            return levels;
        }

        private static RHIFormat ChooseRHIFormat(TextureImportSettings settings, bool hasAlpha)
        {
            if (settings.sRGB)
                return hasAlpha ? RHIFormat.R8G8B8A8_UNorm_SRgb : RHIFormat.R8G8B8A8_UNorm_SRgb;
            return hasAlpha ? RHIFormat.R8G8B8A8_UNorm : RHIFormat.R8G8B8A8_UNorm;
        }

        private static RHIFormat ConvertToRHIFormat(TextureFormat format)
        {
            return format switch
            {
                TextureFormat.R8G8B8A8_UNorm => RHIFormat.R8G8B8A8_UNorm,
                TextureFormat.R16G16B16A16_Float => RHIFormat.R16G16B16A16_Float,
                _ => RHIFormat.R8G8B8A8_UNorm
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearCache();
        }

        // ──────────────────────────────────────────────────────────
        // 内部结构
        // ──────────────────────────────────────────────────────────

        private sealed class CachedTexture
        {
            public string Path = "";
            public RHITexture Texture = null!;
            public RHIDescriptorHandle? SrvHandle;
            public TextureImportSettings ImportSettings = null!;
            public DateTime LastAccessTime;
            public int RefCount;
        }
    }
}
