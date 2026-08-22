using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 颜色分级系统 (Color Grading System)
    //
    // 一套完整的 DI 风格调色管线，支持：
    //   1. LUT（Look-Up Table）加载和应用，支持 1D/3D 两种
    //      - 3D LUT：32/64/256 三档分辨率，支持 .cube / .3dl / 自定义二进制格式
    //      - 1D LUT：Shaper LUT，HDR 专用（先 shaper 再 main，扩展动态范围）
    //   2. 全局调色：Lift/Gamma/Gain（ASC-CDL 风格）、Contrast、Saturation
    //   3. 色彩混合：原色 → 目标色的多段 blend，支持 HSL 空间调整
    //   4. 区域调色：基于亮度/luma mask 的局部调色（高光、阴影、中间调分别调）
    //   5. 白平衡：色温（Kelvin）+ 色调（Tint），模拟相机白平衡
    //   6. 通道混合：RGB 通道互相混合，做电影级调色
    //   7. 胶片模拟：Kodak/Fuji/Agfa 等胶片色调预设
    //   8. Grain：胶片颗粒，基于 hash 噪声 + luminance 加权
    //   9. Vignette：暗角，圆形/椭圆形 + 颜色偏移
    //
    // 这套东西基本上是把 DaVinci Resolve 的核心功能塞到引擎里了，
    // 美术调好 LUT 直接导进来用就行，不用等程序员改 shader。
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // 枚举
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>LUT 加载格式。</summary>
    public enum LUTFormat
    {
        /// <summary>Adobe .cube 格式（文本）。</summary>
        Cube,
        /// <summary>Autodesk .3dl 格式（文本）。</summary>
        ThreeDL,
        /// <summary>自定义二进制格式（最快）。</summary>
        Binary,
        /// <summary>从 PNG/DDS 加载的 3D LUT（贴图形式）。</summary>
        Texture
    }

    /// <summary>LUT 维度。</summary>
    public enum LUTDimension
    {
        /// <summary>3D LUT（N×N×N），最常用。</summary>
        LUT3D,
        /// <summary>1D LUT（Shaper，HDR 专用）。</summary>
        LUT1D
    }

    /// <summary>LUT 插值方式。</summary>
    public enum LUTInterpolation
    {
        /// <summary>最近邻，最快但有色块。</summary>
        Nearest,
        /// <summary>三线性插值，标准做法。</summary>
        Trilinear,
        /// <summary>四面体插值，质量最好但稍慢。</summary>
        Tetrahedral
    }

    /// <summary>调色工作空间。</summary>
    public enum ColorGradingSpace
    {
        /// <summary>线性 sRGB（推荐）。</summary>
        LinearSRGB,
        /// <summary>sRGB Gamma 空间。</summary>
        SRGBGamma,
        /// <summary>Rec.709。</summary>
        Rec709,
        /// <summary>Rec.2020（HDR）。</summary>
        Rec2020,
        /// <summary>ACEScg（HDR 调色用）。</summary>
        ACEScg,
        /// <summary>ACEScct（对数空间调色）。</summary>
        ACEScct
    }

    /// <summary>胶片模拟预设。</summary>
    public enum FilmEmulationPreset
    {
        None,
        /// <summary>Kodak 2383（电影胶片）。</summary>
        Kodak2383,
        /// <summary>Kodak 2393。</summary>
        Kodak2393,
        /// <summary>Fuji 3513。</summary>
        Fuji3513,
        /// <summary>Fuji Eterna。</summary>
        FujiEterna,
        /// <summary>Agfa XT。</summary>
        AgfaXT,
        /// <summary>Ilford HP5（黑白）。</summary>
        IlfordHP5
    }

    /// <summary>调色区域（基于亮度）。</summary>
    public enum TonalRange
    {
        Shadows,
        Midtones,
        Highlights,
        /// <summary>全局（所有亮度）。</summary>
        Global
    }

    // ────────────────────────────────────────────────────────────────────────
    // 数据结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>3D LUT 数据。</summary>
    public sealed class LUT3D : IDisposable
    {
        private float[] _data; // R + G*Size + B*Size*Size
        private bool _disposed;

        public int Size { get; }
        public LUTFormat SourceFormat { get; }
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public float DomainMin { get; set; } = 0.0f;
        public float DomainMax { get; set; } = 1.0f;

        public LUT3D(int size, LUTFormat format = LUTFormat.Cube)
        {
            if (size < 2 || size > 256)
                throw new ArgumentOutOfRangeException(nameof(size), "LUT size must be in [2, 256]");
            Size = size;
            SourceFormat = format;
            _data = new float[size * size * size * 3];
        }

        public Span<float> Data => _data;

        public Vector3 this[int r, int g, int b]
        {
            get
            {
                int idx = (r + g * Size + b * Size * Size) * 3;
                return new Vector3(_data[idx], _data[idx + 1], _data[idx + 2]);
            }
            set
            {
                int idx = (r + g * Size + b * Size * Size) * 3;
                _data[idx] = value.X;
                _data[idx + 1] = value.Y;
                _data[idx + 2] = value.Z;
            }
        }

        /// <summary>CPU 端三线性采样（调试用，生产环境走 GPU）。</summary>
        public Vector3 SampleTrilinear(Vector3 uvw)
        {
            // 这块先这样，回头再优化：其实可以加一个四面体插值，但 CPU 调用不多，先 trilinear 凑合
            Vector3 scaled = uvw * (Size - 1);
            int r0 = Math.Clamp((int)scaled.X, 0, Size - 2);
            int g0 = Math.Clamp((int)scaled.Y, 0, Size - 2);
            int b0 = Math.Clamp((int)scaled.Z, 0, Size - 2);
            int r1 = r0 + 1, g1 = g0 + 1, b1 = b0 + 1;

            Vector3 fr = new(scaled.X - r0, scaled.Y - g0, scaled.Z - b0);

            Vector3 c000 = this[r0, g0, b0];
            Vector3 c100 = this[r1, g0, b0];
            Vector3 c010 = this[r0, g1, b0];
            Vector3 c110 = this[r1, g1, b0];
            Vector3 c001 = this[r0, g0, b1];
            Vector3 c101 = this[r1, g0, b1];
            Vector3 c011 = this[r0, g1, b1];
            Vector3 c111 = this[r1, g1, b1];

            Vector3 c00 = Vector3.Lerp(c000, c100, fr.X);
            Vector3 c10 = Vector3.Lerp(c010, c110, fr.X);
            Vector3 c01 = Vector3.Lerp(c001, c101, fr.X);
            Vector3 c11 = Vector3.Lerp(c011, c111, fr.X);

            Vector3 c0 = Vector3.Lerp(c00, c10, fr.Y);
            Vector3 c1 = Vector3.Lerp(c01, c11, fr.Y);

            return Vector3.Lerp(c0, c1, fr.Z);
        }

        /// <summary>四面体插值采样（质量更好）。</summary>
        public Vector3 SampleTetrahedral(Vector3 uvw)
        {
            Vector3 scaled = uvw * (Size - 1);
            int r0 = Math.Clamp((int)scaled.X, 0, Size - 2);
            int g0 = Math.Clamp((int)scaled.Y, 0, Size - 2);
            int b0 = Math.Clamp((int)scaled.Z, 0, Size - 2);

            Vector3 f = scaled - new Vector3(r0, g0, b0);

            // 根据 f 的分量大小排序决定用哪个四面体
            Vector3 c000 = this[r0, g0, b0];
            Vector3 c100 = this[r0 + 1, g0, b0];
            Vector3 c010 = this[r0, g0 + 1, b0];
            Vector3 c001 = this[r0, g0, b0 + 1];
            Vector3 c110 = this[r0 + 1, g0 + 1, b0];
            Vector3 c101 = this[r0 + 1, g0, b0 + 1];
            Vector3 c011 = this[r0, g0 + 1, b0 + 1];
            Vector3 c111 = this[r0 + 1, g0 + 1, b0 + 1];

            if (f.X >= f.Y)
            {
                if (f.Y >= f.Z)
                    return c000 + f.X * (c100 - c000) + f.Y * (c110 - c100) + f.Z * (c111 - c110);
                else if (f.X >= f.Z)
                    return c000 + f.X * (c100 - c000) + f.Y * (c111 - c101) + f.Z * (c101 - c100);
                else
                    return c000 + f.X * (c101 - c001) + f.Y * (c111 - c101) + f.Z * (c001 - c000);
            }
            else
            {
                if (f.Y < f.Z)
                    return c000 + f.X * (c111 - c011) + f.Y * (c011 - c001) + f.Z * (c001 - c000);
                else if (f.X >= f.Z)
                    return c000 + f.X * (c110 - c010) + f.Y * (c010 - c000) + f.Z * (c111 - c110);
                else
                    return c000 + f.X * (c111 - c011) + f.Y * (c010 - c000) + f.Z * (c011 - c010);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _data = Array.Empty<float>();
                _disposed = true;
            }
        }
    }

    /// <summary>1D LUT（Shaper LUT）。</summary>
    public sealed class LUT1D : IDisposable
    {
        private float[] _data;
        private bool _disposed;

        public int Size { get; }
        public int ChannelCount { get; } // 1=单通道，3=RGB 分通道

        public LUT1D(int size, int channels = 3)
        {
            Size = size;
            ChannelCount = channels;
            _data = new float[size * channels];
        }

        public Span<float> Data => _data;

        public Vector3 Sample(float r, float g, float b)
        {
            float Sample1D(float v, int channelOffset)
            {
                float scaled = v * (Size - 1);
                int i0 = Math.Clamp((int)scaled, 0, Size - 2);
                float f = scaled - i0;
                return _data[i0 * ChannelCount + channelOffset] * (1 - f) +
                       _data[(i0 + 1) * ChannelCount + channelOffset] * f;
            }

            if (ChannelCount == 1)
            {
                float v = Sample1D((r + g + b) / 3.0f, 0);
                return new Vector3(v);
            }
            return new Vector3(Sample1D(r, 0), Sample1D(g, 1), Sample1D(b, 2));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _data = Array.Empty<float>();
                _disposed = true;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 调色参数
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>ASC-CDL 风格的调色参数（Slope/Offset/Power）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ASC_CDL
    {
        /// <summary>Slope（Gain，乘法）。</summary>
        public Vector3 Slope;
        /// <summary>Offset（Lift，加法）。</summary>
        public Vector3 Offset;
        /// <summary>Power（Gamma，幂）。</summary>
        public Vector3 Power;
        /// <summary>Saturation。</summary>
        public float Saturation;

        public static ASC_CDL Identity => new()
        {
            Slope = Vector3.One,
            Offset = Vector3.Zero,
            Power = Vector3.One,
            Saturation = 1.0f
        };

        /// <summary>应用 ASC-CDL。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 Apply(Vector3 color)
        {
            // CDL 公式：out = (in * slope + offset) ^ power
            Vector3 c = color * Slope + Offset;
            // NOTE: 负值要保护一下，MathF.Pow 对负数会 NaN
            c = new Vector3(
                c.X <= 0 ? 0 : MathF.Pow(c.X, Power.X),
                c.Y <= 0 ? 0 : MathF.Pow(c.Y, Power.Y),
                c.Z <= 0 ? 0 : MathF.Pow(c.Z, Power.Z));
            return ApplySaturation(c, Saturation);
        }

        /// <summary>饱和度调整（基于 luma）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ApplySaturation(Vector3 color, float sat)
        {
            float luma = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
            return new Vector3(luma) + (color - new Vector3(luma)) * sat;
        }
    }

    /// <summary>调色参数（美术可调的全部参数）。</summary>
    [Serializable]
    public sealed class ColorGradingParams
    {
        // ── 全局调色 ──
        /// <summary>白平衡色温（Kelvin，2800=暖黄，10000=冷蓝）。</summary>
        public float Temperature = 6500.0f;
        /// <summary>白平衡色调（绿/品红，-100~100）。</summary>
        public float Tint = 0.0f;
        /// <summary>对比度（0.5~2.0，1.0=无变化）。</summary>
        public float Contrast = 1.0f;
        /// <summary>全局饱和度。</summary>
        public float Saturation = 1.0f;
        /// <summary>全局亮度（曝光偏移，stop）。</summary>
        public float ExposureOffset = 0.0f;

        // ── ASC-CDL 分区域 ──
        public ASC_CDL Shadows = ASC_CDL.Identity;
        public ASC_CDL Midtones = ASC_CDL.Identity;
        public ASC_CDL Highlights = ASC_CDL.Identity;
        /// <summary>阴影/中间调分界（luma 0~1）。</summary>
        public float ShadowRange = 0.18f;
        /// <summary>中间调/高光分界。</summary>
        public float HighlightRange = 0.7f;
        /// <summary>区域过渡平滑度。</summary>
        public float TonalFeathering = 0.2f;

        // ── Lift/Gamma/Gain ──
        public Vector3 Lift = Vector3.Zero;     // 阴影偏移
        public Vector3 Gamma = Vector3.One;     // 中间调幂
        public Vector3 Gain = Vector3.One;      // 高光乘法

        // ── HSL 调色 ──
        /// <summary>色相偏移（-180~180度）。</summary>
        public float HueShift = 0.0f;
        /// <summary>HSL 饱和度（0~2）。</summary>
        public float HSL_Saturation = 1.0f;
        /// <summary>HSL 亮度（0~2）。</summary>
        public float HSL_Lightness = 1.0f;

        // ── 通道混合 ──
        /// <summary>RGB 通道混合矩阵（3x3，按行存：R=G_out_R, G_out_R, B_out_R）。</summary>
        public Matrix3x3 ChannelMixer = Matrix3x3.Identity;

        // ── LUT ──
        public bool EnableLUT = false;
        public string LUTPath = string.Empty;
        public float LUTBlend = 1.0f; // 0=原图，1=完全用 LUT

        // ── 胶片模拟 ──
        public FilmEmulationPreset FilmPreset = FilmEmulationPreset.None;
        public float FilmStrength = 1.0f;

        // ── 胶片颗粒 ──
        public bool EnableGrain = false;
        public float GrainAmount = 0.1f;
        public float GrainSize = 1.5f;
        /// <summary>颗粒亮度响应（0=均匀，1=暗部更多）。</summary>
        public float GrainLumaResponse = 0.5f;
        public int GrainSeed = 42;

        // ── 暗角 ──
        public bool EnableVignette = false;
        public float VignetteIntensity = 0.5f;
        public float VignetteRadius = 0.8f;
        public float VignetteSoftness = 0.5f;
        public Vector3 VignetteColor = new(0, 0, 0);
        public bool VignetteRoundness = true;

        // ── 调色空间 ──
        public ColorGradingSpace WorkingSpace = ColorGradingSpace.LinearSRGB;

        // ── 工厂方法 ──
        public static ColorGradingParams Default => new();

        public static ColorGradingParams Cinematic => new()
        {
            Contrast = 1.15f,
            Saturation = 0.92f,
            Temperature = 5800.0f,
            Tint = -5.0f,
            Lift = new Vector3(0.02f, 0.01f, -0.01f),
            Gamma = new Vector3(0.98f, 1.0f, 1.02f),
            Gain = new Vector3(1.05f, 1.0f, 0.95f),
            EnableGrain = true,
            GrainAmount = 0.08f,
            EnableVignette = true,
            VignetteIntensity = 0.3f,
            FilmPreset = FilmEmulationPreset.Kodak2383,
            FilmStrength = 0.6f
        };

        public static ColorGradingParams Vintage => new()
        {
            Contrast = 0.9f,
            Saturation = 0.7f,
            Temperature = 4500.0f,
            Tint = 10.0f,
            EnableGrain = true,
            GrainAmount = 0.2f,
            GrainSize = 2.5f,
            EnableVignette = true,
            VignetteIntensity = 0.6f,
            VignetteRadius = 0.7f
        };

        public static ColorGradingParams CoolTeal => new()
        {
            Contrast = 1.1f,
            Temperature = 8500.0f,
            Tint = -3.0f,
            Lift = new Vector3(-0.02f, 0.01f, 0.03f),
            Gain = new Vector3(0.95f, 1.0f, 1.05f),
            HSL_Saturation = 0.95f
        };

        public static ColorGradingParams WarmGoldenHour => new()
        {
            Contrast = 1.05f,
            Temperature = 4200.0f,
            Tint = 5.0f,
            Gain = new Vector3(1.08f, 1.02f, 0.92f),
            Saturation = 1.1f
        };

        /// <summary>克隆。</summary>
        public ColorGradingParams Clone()
        {
            return (ColorGradingParams)MemberwiseClone();
        }
    }

    /// <summary>简化的 3x3 矩阵（避免依赖 System.Numerics 4x4）。</summary>
    [Serializable]
    public struct Matrix3x3
    {
        public float M00, M01, M02;
        public float M10, M11, M12;
        public float M20, M21, M22;

        public static Matrix3x3 Identity => new()
        {
            M00 = 1, M01 = 0, M02 = 0,
            M10 = 0, M11 = 1, M12 = 0,
            M20 = 0, M21 = 0, M22 = 1
        };

        public Vector3 Transform(Vector3 v) => new(
            v.X * M00 + v.Y * M01 + v.Z * M02,
            v.X * M10 + v.Y * M11 + v.Z * M12,
            v.X * M20 + v.Y * M21 + v.Z * M22);

        public static Matrix3x3 operator *(Matrix3x3 a, Matrix3x3 b) => new()
        {
            M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // LUT 加载器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>LUT 文件加载器。</summary>
    public static class LUTLoader
    {
        /// <summary>从文件加载 3D LUT。</summary>
        public static LUT3D? Load3D(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[LUTLoader] 文件不存在: {path}");
                return null;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                return ext switch
                {
                    ".cube" => LoadCube(path),
                    ".3dl" => Load3DL(path),
                    ".bin" or ".lut" => LoadBinary(path),
                    _ => LoadCube(path) // 默认按 cube 解析
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LUTLoader] 加载失败 {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>加载 .cube 格式（Adobe）。</summary>
        public static LUT3D LoadCube(string path)
        {
            var lines = File.ReadAllLines(path);
            int size = 33; // 默认
            float domainMin = 0.0f, domainMax = 1.0f;
            var data = new List<float>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("//"))
                    continue;

                if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.AsSpan("LUT_3D_SIZE".Length), out size);
                }
                else if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("这是 1D LUT，请用 LUT1D 加载");
                }
                else if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4) float.TryParse(parts[1], out domainMin);
                }
                else if (line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4) float.TryParse(parts[1], out domainMax);
                }
                else if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
                {
                    // 忽略标题
                }
                else
                {
                    // 数据行：R G B
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            if (float.TryParse(parts[i], out float v))
                                data.Add(v);
                        }
                    }
                }
            }

            if (size < 2 || size > 256)
                throw new InvalidDataException($"无效的 LUT size: {size}");

            var lut = new LUT3D(size, LUTFormat.Cube)
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = path,
                DomainMin = domainMin,
                DomainMax = domainMax
            };

            int expected = size * size * size * 3;
            if (data.Count < expected)
            {
                Console.WriteLine($"[LUTLoader] 数据不足: {data.Count} < {expected}，不足部分填 0");
                data.AddRange(new float[expected - data.Count]);
            }

            // .cube 的数据顺序是 B 外层 G 中层 R 内层（R fastest varying）
            // 我们的存储是 r + g*size + b*size*size，正好对应
            Array.Copy(data.ToArray(), 0, lut.Data.ToArray(), 0, expected);

            return lut;
        }

        /// <summary>加载 .3dl 格式（Autodesk）。</summary>
        public static LUT3D Load3DL(string path)
        {
            var lines = File.ReadAllLines(path);
            int size = 0;
            var data = new List<float>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                if (line.StartsWith("Mesh"))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                        int.TryParse(parts[2], out size);
                }
                else
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        if (float.TryParse(p, out float v))
                            data.Add(v / 65535.0f); // .3dl 通常是 16-bit 整数
                    }
                }
            }

            if (size < 2)
                throw new InvalidDataException("无法解析 LUT size");

            var lut = new LUT3D(size, LUTFormat.ThreeDL)
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = path
            };

            int expected = size * size * size * 3;
            if (data.Count < expected)
                data.AddRange(new float[expected - data.Count]);

            var arr = lut.Data.ToArray();
            Array.Copy(data.ToArray(), 0, arr, 0, expected);
            return lut;
        }

        /// <summary>加载二进制格式（自定义，最快）。</summary>
        public static LUT3D LoadBinary(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            // 头部：magic(4) + version(4) + size(4) + domainMin(4) + domainMax(4)
            int magic = br.ReadInt32();
            if (magic != 0x54494C53) // "SLIT"
                throw new InvalidDataException("不是有效的二进制 LUT");

            int version = br.ReadInt32();
            int size = br.ReadInt32();
            float domainMin = br.ReadSingle();
            float domainMax = br.ReadSingle();

            var lut = new LUT3D(size, LUTFormat.Binary)
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = path,
                DomainMin = domainMin,
                DomainMax = domainMax
            };

            int count = size * size * size * 3;
            var arr = lut.Data.ToArray();
            for (int i = 0; i < count; i++)
                arr[i] = br.ReadSingle();

            return lut;
        }

        /// <summary>保存为二进制格式。</summary>
        public static void SaveBinary(LUT3D lut, string path)
        {
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write(0x54494C53); // "SLIT"
            bw.Write(1); // version
            bw.Write(lut.Size);
            bw.Write(lut.DomainMin);
            bw.Write(lut.DomainMax);
            var arr = lut.Data.ToArray();
            for (int i = 0; i < arr.Length; i++)
                bw.Write(arr[i]);
        }

        /// <summary>生成恒等 LUT（不做任何变换，调试用）。</summary>
        public static LUT3D GenerateIdentity(int size)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = "Identity" };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                        lut[r, g, b] = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
            return lut;
        }

        /// <summary>生成色温 LUT（暖色/冷色）。</summary>
        public static LUT3D GenerateTemperatureLUT(int size, float kelvin)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = $"Temp_{kelvin}" };
            // 简化的色温调整：暖色加红减蓝，冷色相反
            float temp = (kelvin - 6500.0f) / 6500.0f;
            float redAdjust = temp < 0 ? -temp * 0.15f : 0;
            float blueAdjust = temp > 0 ? temp * 0.15f : 0;

            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        float rv = (float)r / (size - 1);
                        float gv = (float)g / (size - 1);
                        float bv = (float)b / (size - 1);
                        lut[r, g, b] = new Vector3(
                            Math.Clamp(rv + redAdjust, 0, 1),
                            gv,
                            Math.Clamp(bv + blueAdjust, 0, 1));
                    }
            return lut;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 颜色空间转换
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>颜色空间转换工具。</summary>
    public static class ColorSpaceTransform
    {
        // sRGB <-> Linear
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SRGBToLinear(float v)
        {
            return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float LinearToSRGB(float v)
        {
            return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(MathF.Max(v, 0), 1.0f / 2.4f) - 0.055f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 SRGBToLinear(Vector3 c) =>
            new(SRGBToLinear(c.X), SRGBToLinear(c.Y), SRGBToLinear(c.Z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 LinearToSRGB(Vector3 c) =>
            new(LinearToSRGB(c.X), LinearToSRGB(c.Y), LinearToSRGB(c.Z));

        // Linear sRGB <-> ACEScg
        // ACEScg primaries: R=(0.713, 0.293), G=(0.165, 0.830), B=(0.128, 0.044)
        private static readonly Matrix3x3 sRGB_to_ACEScg = new()
        {
            M00 = 0.6131f, M01 = 0.3395f, M02 = 0.0474f,
            M10 = 0.0701f, M11 = 0.9164f, M12 = 0.0135f,
            M20 = 0.0209f, M21 = 0.1090f, M22 = 0.8701f
        };

        private static readonly Matrix3x3 ACEScg_to_sRGB = new()
        {
            M00 = 1.7050f, M01 = -0.6217f, M02 = -0.0833f,
            M10 = -0.1297f, M11 = 1.1408f, M12 = -0.0111f,
            M20 = -0.0241f, M21 = -0.1289f, M22 = 1.1530f
        };

        public static Vector3 LinearSRGBToACEScg(Vector3 c) => sRGB_to_ACEScg.Transform(c);
        public static Vector3 ACEScgToLinearSRGB(Vector3 c) => ACEScg_to_sRGB.Transform(c);

        // ACEScct (对数) <-> ACEScg
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ACEScctToACEScg(float v)
        {
            return v <= 0.078291f ?
                (v - 0.078291f) / 10.540237f + 0.004924f :
                MathF.Pow(2.0f, v * 17.52f - 9.72f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ACEScgToACEScct(float v)
        {
            return v <= 0.007824f ?
                10.540237f * (v - 0.004924f) + 0.078291f :
                (MathF.Log2(v) + 9.72f) / 17.52f;
        }

        public static Vector3 ToWorkingSpace(Vector3 linearSRGB, ColorGradingSpace space)
        {
            return space switch
            {
                ColorGradingSpace.LinearSRGB => linearSRGB,
                ColorGradingSpace.SRGBGamma => LinearToSRGB(linearSRGB),
                ColorGradingSpace.Rec709 => LinearToSRGB(linearSRGB), // 简化
                ColorGradingSpace.Rec2020 => linearSRGB, // 需要 PQ 转换，这里简化
                ColorGradingSpace.ACEScg => LinearSRGBToACEScg(linearSRGB),
                ColorGradingSpace.ACEScct => new Vector3(
                    ACEScgToACEScct(LinearSRGBToACEScg(linearSRGB).X),
                    ACEScgToACEScct(LinearSRGBToACEScg(linearSRGB).Y),
                    ACEScgToACEScct(LinearSRGBToACEScg(linearSRGB).Z)),
                _ => linearSRGB
            };
        }

        public static Vector3 FromWorkingSpace(Vector3 c, ColorGradingSpace space)
        {
            return space switch
            {
                ColorGradingSpace.LinearSRGB => c,
                ColorGradingSpace.SRGBGamma => SRGBToLinear(c),
                ColorGradingSpace.Rec709 => SRGBToLinear(c),
                ColorGradingSpace.Rec2020 => c,
                ColorGradingSpace.ACEScg => ACEScgToLinearSRGB(c),
                ColorGradingSpace.ACEScct => ACEScgToLinearSRGB(new Vector3(
                    ACEScctToACEScg(c.X),
                    ACEScctToACEScg(c.Y),
                    ACEScctToACEScg(c.Z))),
                _ => c
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 白平衡
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>白平衡计算器。</summary>
    public static class WhiteBalance
    {
        /// <summary>根据色温计算 RGB 调整系数。</summary>
        /// <param name="kelvin">色温（K）。</param>
        /// <param name="tint">色调偏移（-100~100）。</param>
        public static Vector3 ComputeBalance(float kelvin, float tint)
        {
            // 基于 Tanner Helland 算法的简化版
            float temp = kelvin / 100.0f;
            float r, g, b;

            if (temp <= 66)
            {
                r = 255;
                g = 99.4708025861f * MathF.Log(temp) - 161.1195681661f;
                b = temp <= 19 ? 0 : 138.5177312231f * MathF.Log(temp - 10) - 305.0447927307f;
            }
            else
            {
                r = 329.698727446f * MathF.Pow(temp - 60, -0.1332047592f);
                g = 288.1221695283f * MathF.Pow(temp - 60, -0.0755148492f);
                b = 255;
            }

            r = Math.Clamp(r, 0, 255) / 255.0f;
            g = Math.Clamp(g, 0, 255) / 255.0f;
            b = Math.Clamp(b, 0, 255) / 255.0f;

            // 相对于 6500K（D65）计算偏移
            var d65 = ComputeRawBalance(65);
            r = d65.X / MathF.Max(r, 0.01f);
            g = d65.Y / MathF.Max(g, 0.01f);
            b = d65.Z / MathF.Max(b, 0.01f);

            // 归一化
            float avg = (r + g + b) / 3.0f;
            r /= avg;
            g /= avg;
            b /= avg;

            // Tint 偏移：正值偏绿，负值偏品红
            float tintAmount = tint / 100.0f * 0.1f;
            g *= (1.0f + tintAmount);
            r *= (1.0f - tintAmount * 0.5f);
            b *= (1.0f - tintAmount * 0.5f);

            return new Vector3(r, g, b);
        }

        private static Vector3 ComputeRawBalance(float temp)
        {
            float r, g, b;
            if (temp <= 66)
            {
                r = 255;
                g = 99.4708025861f * MathF.Log(temp) - 161.1195681661f;
                b = temp <= 19 ? 0 : 138.5177312231f * MathF.Log(temp - 10) - 305.0447927307f;
            }
            else
            {
                r = 329.698727446f * MathF.Pow(temp - 60, -0.1332047592f);
                g = 288.1221695283f * MathF.Pow(temp - 60, -0.0755148492f);
                b = 255;
            }
            return new Vector3(
                Math.Clamp(r, 0, 255) / 255.0f,
                Math.Clamp(g, 0, 255) / 255.0f,
                Math.Clamp(b, 0, 255) / 255.0f);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 胶片颗粒生成器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>胶片颗粒噪声生成器。</summary>
    public static class FilmGrainGenerator
    {
        // hash 噪声（PCG 风格）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Hash(uint state)
        {
            uint result = state * 747796405u + 2891336453u;
            uint word = ((result >> ((result >> 28u) + 4u)) ^ result) * 277803737u;
            return (word >> 22u) ^ word;
        }

        /// <summary>生成一帧颗粒数据。</summary>
        public static float[] GenerateGrain(int width, int height, int seed, float size, float lumaResponse)
        {
            // 按 size 缩小分辨率生成，然后上采样
            int gw = Math.Max(1, (int)(width / size));
            int gh = Math.Max(1, (int)(height / size));
            var grain = new float[width * height];

            // 先在小尺寸上生成
            var smallGrain = new float[gw * gh];
            uint state = (uint)seed;
            for (int i = 0; i < smallGrain.Length; i++)
            {
                uint h = Hash(state++);
                // 转换到 [-1, 1] 的高斯近似
                float n = (h / (float)uint.MaxValue) * 2.0f - 1.0f;
                // 三角分布近似高斯
                float n2 = (Hash(state++) / (float)uint.MaxValue) * 2.0f - 1.0f;
                smallGrain[i] = (n + n2) * 0.5f;
            }

            // 双线性上采样
            for (int y = 0; y < height; y++)
            {
                float fy = (float)y * gh / height;
                int y0 = Math.Clamp((int)fy, 0, gh - 1);
                int y1 = Math.Min(y0 + 1, gh - 1);
                float ty = fy - y0;
                for (int x = 0; x < width; x++)
                {
                    float fx = (float)x * gw / width;
                    int x0 = Math.Clamp((int)fx, 0, gw - 1);
                    int x1 = Math.Min(x0 + 1, gw - 1);
                    float tx = fx - x0;

                    float g00 = smallGrain[y0 * gw + x0];
                    float g10 = smallGrain[y0 * gw + x1];
                    float g01 = smallGrain[y1 * gw + x0];
                    float g11 = smallGrain[y1 * gw + x1];
                    float g0 = g00 * (1 - tx) + g10 * tx;
                    float g1 = g01 * (1 - tx) + g11 * tx;
                    grain[y * width + x] = g0 * (1 - ty) + g1 * ty;
                }
            }

            return grain;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 胶片模拟 LUT 预设
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>胶片模拟预设生成器。</summary>
    public static class FilmEmulationPresets
    {
        /// <summary>生成指定胶片预设的 3D LUT。</summary>
        public static LUT3D Generate(FilmEmulationPreset preset, int size = 33)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = preset.ToString() };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        Vector3 c = new(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        lut[r, g, b] = ApplyFilmPreset(c, preset);
                    }
            return lut;
        }

        /// <summary>应用胶片预设到单个颜色。</summary>
        public static Vector3 ApplyFilmPreset(Vector3 c, FilmEmulationPreset preset)
        {
            return preset switch
            {
                FilmEmulationPreset.Kodak2383 => Kodak2383(c),
                FilmEmulationPreset.Kodak2393 => Kodak2393(c),
                FilmEmulationPreset.Fuji3513 => Fuji3513(c),
                FilmEmulationPreset.FujiEterna => FujiEterna(c),
                FilmEmulationPreset.AgfaXT => AgfaXT(c),
                FilmEmulationPreset.IlfordHP5 => IlfordHP5(c),
                _ => c
            };
        }

        // Kodak 2383：经典电影胶片，暖色+微青色阴影
        private static Vector3 Kodak2383(Vector3 c)
        {
            // S 曲线
            c = ApplySCurve(c, 0.55f);
            // 阴影偏青
            float luma = Luma(c);
            var shadowTint = new Vector3(-0.02f, 0.01f, 0.03f);
            c += shadowTint * (1.0f - luma);
            // 高光偏暖
            var highlightTint = new Vector3(0.03f, 0.01f, -0.02f);
            c += highlightTint * luma;
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }

        // Kodak 2393：更高对比度
        private static Vector3 Kodak2393(Vector3 c)
        {
            c = ApplySCurve(c, 0.65f);
            float luma = Luma(c);
            c.X += 0.02f * (1.0f - luma) + 0.03f * luma;
            c.Y -= 0.01f * (1.0f - luma);
            c.Z += 0.03f * (1.0f - luma) - 0.02f * luma;
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }

        // Fuji 3513：偏绿
        private static Vector3 Fuji3513(Vector3 c)
        {
            c = ApplySCurve(c, 0.5f);
            float luma = Luma(c);
            c.Y += 0.02f;
            c.Z += 0.01f * (1.0f - luma);
            c.X -= 0.01f * luma;
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }

        // Fuji Eterna：电影感，低饱和
        private static Vector3 FujiEterna(Vector3 c)
        {
            c = ApplySCurve(c, 0.45f);
            float luma = Luma(c);
            c = new Vector3(luma) + (c - new Vector3(luma)) * 0.85f;
            c.X += 0.02f * luma;
            c.Z += 0.02f * (1.0f - luma);
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }

        // Agfa XT：鲜艳
        private static Vector3 AgfaXT(Vector3 c)
        {
            c = ApplySCurve(c, 0.6f);
            float luma = Luma(c);
            c = new Vector3(luma) + (c - new Vector3(luma)) * 1.15f;
            c.X += 0.02f;
            c.Z -= 0.01f * luma;
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }

        // Ilford HP5：黑白
        private static Vector3 IlfordHP5(Vector3 c)
        {
            float luma = Luma(c);
            // 加对比度
            luma = ApplySCurve1D(luma, 0.6f);
            // 微微的色调（黑白胶片不是纯灰）
            float r = luma * 1.02f;
            float g = luma * 1.0f;
            float b = luma * 0.97f;
            return Vector3.Clamp(new Vector3(r, g, b), Vector3.Zero, Vector3.One);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Luma(Vector3 c) => c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ApplySCurve(Vector3 c, float strength)
        {
            return new Vector3(
                ApplySCurve1D(c.X, strength),
                ApplySCurve1D(c.Y, strength),
                ApplySCurve1D(c.Z, strength));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ApplySCurve1D(float v, float strength)
        {
            // smoothstep 风格的 S 曲线
            float s = Math.Clamp(strength, 0, 1);
            float v2 = v * v * (3 - 2 * v);
            return v * (1 - s) + v2 * s;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲结构
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorGradingCB
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float Pad0;
        public Vector2 InvResolution;
        public float ExposureOffset;
        public int EnableLUT;
        public int LUTSize;
        public float LUTBlend;
        public int LUTInterpolation;
        public Vector3 WhiteBalance;
        public float Contrast;
        public float Saturation;
        public int WorkingSpace;
        public Vector3 Lift;
        public float Pad1;
        public Vector3 Gamma;
        public float Pad2;
        public Vector3 Gain;
        public float Pad3;
        // 分区域 CDL
        public Vector3 ShadowSlope, ShadowOffset;
        public Vector3 ShadowPower, MidtoneSlope;
        public Vector3 MidtoneOffset, MidtonePower;
        public Vector3 HighlightSlope, HighlightOffset;
        public Vector3 HighlightPower;
        public float ShadowRange, HighlightRange, TonalFeathering;
        // HSL
        public float HueShift, HSL_Saturation, HSL_Lightness;
        // Channel mixer (3x3 = 9 floats)
        public Vector3 MixerRow0, MixerRow1, MixerRow2;
        // 颗粒
        public int EnableGrain;
        public float GrainAmount, GrainSize, GrainLumaResponse;
        public int GrainSeed;
        // 暗角
        public int EnableVignette;
        public float VignetteIntensity, VignetteRadius, VignetteSoftness;
        public Vector3 VignetteColor;
        public int VignetteRoundness;
        // 胶片
        public int FilmPreset;
        public float FilmStrength;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 颜色分级系统。
    /// 完整的 DI 风格调色管线，支持 LUT、白平衡、CDL、HSL、通道混合、胶片模拟、颗粒、暗角。
    /// </summary>
    public sealed class ColorGradingSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private ColorGradingParams _params;
        private LUT3D? _currentLUT;
        private LUT1D? _shaperLUT;
        private RHITexture? _lutTexture;
        private RHITexture? _grainTexture;
        private RHITexture? _outputTexture;
        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _gradePSO;
        private RHIConstantBuffer? _cb;
        private int _width, _height;
        private bool _disposed;
        private int _frameIndex;

        // 预生成的胶片 LUT 缓存
        private readonly Dictionary<FilmEmulationPreset, LUT3D> _filmLUTCache = new();

        /// <summary>调色参数。</summary>
        public ColorGradingParams Params
        {
            get => _params;
            set => _params = value ?? ColorGradingParams.Default;
        }

        public RHITexture? Output => _outputTexture;
        public LUT3D? CurrentLUT => _currentLUT;

        public ColorGradingSystem(RHIDevice device, int width, int height, ColorGradingParams? initialParams = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = width;
            _height = height;
            _params = initialParams ?? ColorGradingParams.Default;

            InitializeResources();
        }

        private void InitializeResources()
        {
            // 输出纹理
            _outputTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width,
                Height = _height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "ColorGradedOutput"
            });

            // 常量缓冲
            _cb = _device.CreateConstantBuffer(ColorGradingCB.SizeOf);

            // Root signature
            _rootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                ConstantBuffers = new[] { 0 },
                ShaderResources = new[] { 0, 1, 2, 3 }, // input, LUT, grain, depth(optional)
                UnorderedAccessViews = new[] { 0 }
            });

            // 颗粒纹理（预分配）
            _grainTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width,
                Height = _height,
                Format = RHIFormat.R8_UNorm,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "FilmGrain"
            });

            Console.WriteLine($"[ColorGradingSystem] 初始化完成，分辨率 {_width}x{_height}");
        }

        /// <summary>加载 3D LUT 文件。</summary>
        public bool LoadLUT(string path)
        {
            var lut = LUTLoader.Load3D(path);
            if (lut == null) return false;

            _currentLUT = lut;
            UploadLUTToGPU(lut);
            _params.LUTPath = path;
            _params.EnableLUT = true;
            Console.WriteLine($"[ColorGradingSystem] LUT 加载成功: {path} (size={lut.Size})");
            return true;
        }

        /// <summary>设置程序生成的 LUT。</summary>
        public void SetLUT(LUT3D lut)
        {
            _currentLUT = lut ?? throw new ArgumentNullException(nameof(lut));
            UploadLUTToGPU(lut);
            _params.EnableLUT = true;
        }

        /// <summary>加载 Shaper LUT（1D，HDR 用）。</summary>
        public bool LoadShaperLUT(string path)
        {
            // 简化：从 .cube 读取 1D 部分
            // NOTE: 完整的 shaper LUT 需要单独的文件格式，这里先支持手动构造
            return false;
        }

        /// <summary>设置 Shaper LUT。</summary>
        public void SetShaperLUT(LUT1D lut)
        {
            _shaperLUT = lut ?? throw new ArgumentNullException(nameof(lut));
        }

        private void UploadLUTToGPU(LUT3D lut)
        {
            _lutTexture?.Dispose();
            // 用 3D 纹理存 LUT（或者 2D atlas，这里简化用 3D）
            _lutTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = lut.Size,
                Height = lut.Size,
                Depth = lut.Size,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.ShaderResource,
                DebugName = $"LUT3D_{lut.Name}"
            });

            var data = lut.Data.ToArray();
            _device.UpdateTexture(_lutTexture, data);
        }

        /// <summary>执行颜色分级。</summary>
        public void Execute(RHITexture inputHDR, RHITexture? depthBuffer = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ColorGradingSystem));

            // 更新常量缓冲
            UpdateConstantBuffer();

            // 如果启用了胶片颗粒，更新颗粒纹理
            if (_params.EnableGrain && _grainTexture != null)
            {
                UpdateGrainTexture();
            }

            // 设置 PSO 和资源
            _device.SetPipelineState(_gradePSO!);
            _device.SetRootSignature(_rootSig!);

            _device.SetShaderResource(0, inputHDR);
            _device.SetShaderResource(1, _lutTexture!);
            _device.SetShaderResource(2, _grainTexture!);
            if (depthBuffer != null)
                _device.SetShaderResource(3, depthBuffer);

            _device.SetUnorderedAccessView(0, _outputTexture!);
            _device.SetConstantBuffer(0, _cb!);

            int gx = (_width + 7) / 8;
            int gy = (_height + 7) / 8;
            _device.Dispatch(gx, gy, 1);

            _frameIndex++;
        }

        private void UpdateConstantBuffer()
        {
            var wb = WhiteBalance.ComputeBalance(_params.Temperature, _params.Tint);
            var cb = new ColorGradingCB
            {
                CameraPos = Vector3.Zero,
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                ExposureOffset = _params.ExposureOffset,
                EnableLUT = _params.EnableLUT ? 1 : 0,
                LUTSize = _currentLUT?.Size ?? 33,
                LUTBlend = _params.LUTBlend,
                LUTInterpolation = (int)LUTInterpolation.Tetrahedral,
                WhiteBalance = wb,
                Contrast = _params.Contrast,
                Saturation = _params.Saturation,
                WorkingSpace = (int)_params.WorkingSpace,
                Lift = _params.Lift,
                Gamma = _params.Gamma,
                Gain = _params.Gain,
                // 分区域 CDL
                ShadowSlope = _params.Shadows.Slope,
                ShadowOffset = _params.Shadows.Offset,
                ShadowPower = _params.Shadows.Power,
                MidtoneSlope = _params.Midtones.Slope,
                MidtoneOffset = _params.Midtones.Offset,
                MidtonePower = _params.Midtones.Power,
                HighlightSlope = _params.Highlights.Slope,
                HighlightOffset = _params.Highlights.Offset,
                HighlightPower = _params.Highlights.Power,
                ShadowRange = _params.ShadowRange,
                HighlightRange = _params.HighlightRange,
                TonalFeathering = _params.TonalFeathering,
                HueShift = _params.HueShift,
                HSL_Saturation = _params.HSL_Saturation,
                HSL_Lightness = _params.HSL_Lightness,
                MixerRow0 = new Vector3(_params.ChannelMixer.M00, _params.ChannelMixer.M01, _params.ChannelMixer.M02),
                MixerRow1 = new Vector3(_params.ChannelMixer.M10, _params.ChannelMixer.M11, _params.ChannelMixer.M12),
                MixerRow2 = new Vector3(_params.ChannelMixer.M20, _params.ChannelMixer.M21, _params.ChannelMixer.M22),
                EnableGrain = _params.EnableGrain ? 1 : 0,
                GrainAmount = _params.GrainAmount,
                GrainSize = _params.GrainSize,
                GrainLumaResponse = _params.GrainLumaResponse,
                GrainSeed = _params.GrainSeed + _frameIndex,
                EnableVignette = _params.EnableVignette ? 1 : 0,
                VignetteIntensity = _params.VignetteIntensity,
                VignetteRadius = _params.VignetteRadius,
                VignetteSoftness = _params.VignetteSoftness,
                VignetteColor = _params.VignetteColor,
                VignetteRoundness = _params.VignetteRoundness ? 1 : 0,
                FilmPreset = (int)_params.FilmPreset,
                FilmStrength = _params.FilmStrength
            };

            _device.UpdateBuffer(_cb!, cb);
        }

        private void UpdateGrainTexture()
        {
            var grain = FilmGrainGenerator.GenerateGrain(
                _width, _height,
                _params.GrainSeed + _frameIndex,
                _params.GrainSize,
                _params.GrainLumaResponse);

            // 转成 byte
            var byteData = new byte[grain.Length];
            for (int i = 0; i < grain.Length; i++)
            {
                byteData[i] = (byte)(Math.Clamp(grain[i] * 0.5f + 0.5f, 0, 1) * 255);
            }

            _device.UpdateTexture(_grainTexture!, byteData);
        }

        /// <summary>获取或生成胶片模拟 LUT。</summary>
        public LUT3D GetFilmLUT(FilmEmulationPreset preset)
        {
            if (!_filmLUTCache.TryGetValue(preset, out var lut))
            {
                lut = FilmEmulationPresets.Generate(preset);
                _filmLUTCache[preset] = lut;
            }
            return lut;
        }

        /// <summary>CPU 端调色（调试/预览用）。</summary>
        public Vector3 GradeColorCPU(Vector3 linearHDR)
        {
            var p = _params;
            Vector3 c = linearHDR;

            // 1. 曝光偏移
            c *= MathF.Pow(2.0f, p.ExposureOffset);

            // 2. 白平衡
            var wb = WhiteBalance.ComputeBalance(p.Temperature, p.Tint);
            c *= wb;

            // 3. 转到工作空间
            c = ColorSpaceTransform.ToWorkingSpace(c, p.WorkingSpace);

            // 4. 分区域 CDL
            float luma = c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;
            float shadowWeight = 1.0f - Smoothstep(p.ShadowRange, p.ShadowRange + p.TonalFeathering, luma);
            float highlightWeight = Smoothstep(p.HighlightRange - p.TonalFeathering, p.HighlightRange, luma);
            float midtoneWeight = 1.0f - shadowWeight - highlightWeight;

            Vector3 shadowGraded = p.Shadows.Apply(c) - c;
            Vector3 midGraded = p.Midtones.Apply(c) - c;
            Vector3 highlightGraded = p.Highlights.Apply(c) - c;

            c += shadowGraded * shadowWeight + midGraded * midtoneWeight + highlightGraded * highlightWeight;

            // 5. Lift/Gamma/Gain
            c = c * p.Gain + p.Lift;
            c = new Vector3(
                c.X <= 0 ? 0 : MathF.Pow(c.X, p.Gamma.X),
                c.Y <= 0 ? 0 : MathF.Pow(c.Y, p.Gamma.Y),
                c.Z <= 0 ? 0 : MathF.Pow(c.Z, p.Gamma.Z));

            // 6. 对比度（围绕中灰 0.18）
            c = (c - new Vector3(0.18f)) * p.Contrast + new Vector3(0.18f);

            // 7. 饱和度
            c = ASC_CDL.ApplySaturation(c, p.Saturation);

            // 8. 通道混合
            c = p.ChannelMixer.Transform(c);

            // 9. HSL 调整
            if (p.HueShift != 0 || p.HSL_Saturation != 1 || p.HSL_Lightness != 1)
            {
                var hsl = RGBToHSL(c);
                hsl.X = (hsl.X + p.HueShift / 360.0f) % 1.0f;
                if (hsl.X < 0) hsl.X += 1.0f;
                hsl.Y *= p.HSL_Saturation;
                hsl.Z *= p.HSL_Lightness;
                c = HSLToRGB(hsl);
            }

            // 10. 胶片模拟
            if (p.FilmPreset != FilmEmulationPreset.None)
            {
                var filmColor = FilmEmulationPresets.ApplyFilmPreset(c, p.FilmPreset);
                c = Vector3.Lerp(c, filmColor, p.FilmStrength);
            }

            // 11. LUT
            if (p.EnableLUT && _currentLUT != null)
            {
                Vector3 lutColor = _currentLUT.SampleTetrahedral(c);
                c = Vector3.Lerp(c, lutColor, p.LUTBlend);
            }

            // 12. 转回线性 sRGB
            c = ColorSpaceTransform.FromWorkingSpace(c, p.WorkingSpace);

            // 13. 胶片颗粒
            if (p.EnableGrain)
            {
                float lumaFinal = c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;
                float grainAmount = p.GrainAmount * (1.0f - lumaFinal * p.GrainLumaResponse);
                float grain = (HashFloat(_frameIndex * 7919 + (int)(luma * 255)) - 0.5f) * 2.0f * grainAmount;
                c += new Vector3(grain);
            }

            // 14. 暗角
            // NOTE: 暗角是屏幕空间效果，CPU 调色里没法做，需要位置信息
            // 这里跳过

            return c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / MathF.Max(edge1 - edge0, 1e-6f), 0, 1);
            return t * t * (3 - 2 * t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 RGBToHSL(Vector3 rgb)
        {
            float max = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
            float min = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
            float l = (max + min) * 0.5f;
            float h, s;

            if (max == min)
            {
                h = 0;
                s = 0;
            }
            else
            {
                float d = max - min;
                s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
                if (max == rgb.X)
                    h = (rgb.Y - rgb.Z) / d + (rgb.Y < rgb.Z ? 6 : 0);
                else if (max == rgb.Y)
                    h = (rgb.Z - rgb.X) / d + 2;
                else
                    h = (rgb.X - rgb.Y) / d + 4;
                h /= 6;
            }
            return new Vector3(h, s, l);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 HSLToRGB(Vector3 hsl)
        {
            float h = hsl.X, s = hsl.Y, l = hsl.Z;
            if (s == 0) return new Vector3(l);

            float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
            float p = 2 * l - q;
            return new Vector3(
                HueToRGB(p, q, h + 1.0f / 3),
                HueToRGB(p, q, h),
                HueToRGB(p, q, h - 1.0f / 3));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float HueToRGB(float p, float q, float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0f / 6) return p + (q - p) * 6 * t;
            if (t < 1.0f / 2) return q;
            if (t < 2.0f / 3) return p + (q - p) * (2.0f / 3 - t) * 6;
            return p;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint HashState(uint state)
        {
            state = state * 747796405u + 2891336453u;
            uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
            return (word >> 22u) ^ word;
        }

        private static float HashFloat(int seed)
        {
            return HashState((uint)seed) / (float)uint.MaxValue;
        }

        /// <summary>调整分辨率。</summary>
        public void Resize(int width, int height)
        {
            if (_width == width && _height == height) return;
            _width = width;
            _height = height;

            _outputTexture?.Dispose();
            _grainTexture?.Dispose();

            _outputTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width,
                Height = _height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "ColorGradedOutput"
            });

            _grainTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width,
                Height = _height,
                Format = RHIFormat.R8_UNorm,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "FilmGrain"
            });

            Console.WriteLine($"[ColorGradingSystem] 调整分辨率到 {_width}x{_height}");
        }

        /// <summary>应用预设。</summary>
        public void ApplyPreset(ColorGradingParams preset)
        {
            _params = preset.Clone();
        }

        /// <summary>生成调色报告（调试用）。</summary>
        public string GenerateReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Color Grading Report ===");
            sb.AppendLine($"Working Space: {_params.WorkingSpace}");
            sb.AppendLine($"Temperature: {_params.Temperature}K (Tint: {_params.Tint})");
            sb.AppendLine($"Contrast: {_params.Contrast:F2}, Saturation: {_params.Saturation:F2}");
            sb.AppendLine($"Exposure Offset: {_params.ExposureOffset:F2} stops");
            sb.AppendLine($"Lift: ({_params.Lift.X:F3}, {_params.Lift.Y:F3}, {_params.Lift.Z:F3})");
            sb.AppendLine($"Gamma: ({_params.Gamma.X:F3}, {_params.Gamma.Y:F3}, {_params.Gamma.Z:F3})");
            sb.AppendLine($"Gain: ({_params.Gain.X:F3}, {_params.Gain.Y:F3}, {_params.Gain.Z:F3})");
            sb.AppendLine($"LUT: {(_params.EnableLUT ? _params.LUTPath : "disabled")}");
            sb.AppendLine($"Film Preset: {_params.FilmPreset} (strength: {_params.FilmStrength:F2})");
            sb.AppendLine($"Grain: {(_params.EnableGrain ? $"amount={_params.GrainAmount:F3}" : "disabled")}");
            sb.AppendLine($"Vignette: {(_params.EnableVignette ? $"intensity={_params.VignetteIntensity:F2}" : "disabled")}");
            sb.AppendLine($"Shadows CDL: S({_params.Shadows.Slope.X:F2},{_params.Shadows.Slope.Y:F2},{_params.Shadows.Slope.Z:F2}) O({_params.Shadows.Offset.X:F2},{_params.Shadows.Offset.Y:F2},{_params.Shadows.Offset.Z:F2})");
            sb.AppendLine($"Midtones CDL: S({_params.Midtones.Slope.X:F2},{_params.Midtones.Slope.Y:F2},{_params.Midtones.Slope.Z:F2}) O({_params.Midtones.Offset.X:F2},{_params.Midtones.Offset.Y:F2},{_params.Midtones.Offset.Z:F2})");
            sb.AppendLine($"Highlights CDL: S({_params.Highlights.Slope.X:F2},{_params.Highlights.Slope.Y:F2},{_params.Highlights.Slope.Z:F2}) O({_params.Highlights.Offset.X:F2},{_params.Highlights.Offset.Y:F2},{_params.Highlights.Offset.Z:F2})");
            sb.AppendLine($"Channel Mixer:");
            sb.AppendLine($"  [{_params.ChannelMixer.M00:F3} {_params.ChannelMixer.M01:F3} {_params.ChannelMixer.M02:F3}]");
            sb.AppendLine($"  [{_params.ChannelMixer.M10:F3} {_params.ChannelMixer.M11:F3} {_params.ChannelMixer.M12:F3}]");
            sb.AppendLine($"  [{_params.ChannelMixer.M20:F3} {_params.ChannelMixer.M21:F3} {_params.ChannelMixer.M22:F3}]");
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _currentLUT?.Dispose();
            _shaperLUT?.Dispose();
            _lutTexture?.Dispose();
            _grainTexture?.Dispose();
            _outputTexture?.Dispose();
            _rootSig?.Dispose();
            _gradePSO?.Dispose();
            _cb?.Dispose();
            foreach (var kvp in _filmLUTCache) kvp.Value.Dispose();
            _filmLUTCache.Clear();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 调色预设管理器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>调色预设管理器。</summary>
    public sealed class ColorGradingPresetManager
    {
        private readonly Dictionary<string, ColorGradingParams> _presets = new();
        private readonly Dictionary<string, string> _lutPaths = new();

        public ColorGradingPresetManager()
        {
            // 内置预设
            RegisterPreset("Default", ColorGradingParams.Default);
            RegisterPreset("Cinematic", ColorGradingParams.Cinematic);
            RegisterPreset("Vintage", ColorGradingParams.Vintage);
            RegisterPreset("CoolTeal", ColorGradingParams.CoolTeal);
            RegisterPreset("WarmGoldenHour", ColorGradingParams.WarmGoldenHour);
        }

        public void RegisterPreset(string name, ColorGradingParams preset)
        {
            _presets[name] = preset.Clone();
        }

        public ColorGradingParams? GetPreset(string name)
        {
            return _presets.TryGetValue(name, out var p) ? p.Clone() : null;
        }

        public bool RemovePreset(string name) => _presets.Remove(name);

        public IReadOnlyDictionary<string, ColorGradingParams> Presets => _presets;

        /// <summary>保存预设到文件。</summary>
        public void SavePreset(string name, string path)
        {
            if (!_presets.TryGetValue(name, out var p))
                throw new KeyNotFoundException($"Preset '{name}' not found");

            // 简化的 JSON 序列化（避免依赖 System.Text.Json）
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{name}\",");
            sb.AppendLine($"  \"temperature\": {p.Temperature},");
            sb.AppendLine($"  \"tint\": {p.Tint},");
            sb.AppendLine($"  \"contrast\": {p.Contrast},");
            sb.AppendLine($"  \"saturation\": {p.Saturation},");
            sb.AppendLine($"  \"exposureOffset\": {p.ExposureOffset},");
            sb.AppendLine($"  \"lift\": [{p.Lift.X}, {p.Lift.Y}, {p.Lift.Z}],");
            sb.AppendLine($"  \"gamma\": [{p.Gamma.X}, {p.Gamma.Y}, {p.Gamma.Z}],");
            sb.AppendLine($"  \"gain\": [{p.Gain.X}, {p.Gain.Y}, {p.Gain.Z}],");
            sb.AppendLine($"  \"enableLUT\": {(p.EnableLUT ? "true" : "false")},");
            sb.AppendLine($"  \"lutPath\": \"{p.LUTPath}\",");
            sb.AppendLine($"  \"lutBlend\": {p.LUTBlend},");
            sb.AppendLine($"  \"filmPreset\": \"{p.FilmPreset}\",");
            sb.AppendLine($"  \"filmStrength\": {p.FilmStrength},");
            sb.AppendLine($"  \"enableGrain\": {(p.EnableGrain ? "true" : "false")},");
            sb.AppendLine($"  \"grainAmount\": {p.GrainAmount},");
            sb.AppendLine($"  \"enableVignette\": {(p.EnableVignette ? "true" : "false")},");
            sb.AppendLine($"  \"vignetteIntensity\": {p.VignetteIntensity}");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>从文件加载预设。</summary>
        public ColorGradingParams? LoadPreset(string path)
        {
            if (!File.Exists(path)) return null;
            // 简化解析：只读几个关键值
            var content = File.ReadAllText(path);
            var p = ColorGradingParams.Default;
            p.Temperature = ParseFloat(content, "temperature", p.Temperature);
            p.Tint = ParseFloat(content, "tint", p.Tint);
            p.Contrast = ParseFloat(content, "contrast", p.Contrast);
            p.Saturation = ParseFloat(content, "saturation", p.Saturation);
            p.ExposureOffset = ParseFloat(content, "exposureOffset", p.ExposureOffset);
            p.LUTBlend = ParseFloat(content, "lutBlend", p.LUTBlend);
            p.GrainAmount = ParseFloat(content, "grainAmount", p.GrainAmount);
            p.VignetteIntensity = ParseFloat(content, "vignetteIntensity", p.VignetteIntensity);
            p.EnableLUT = content.Contains("\"enableLUT\": true");
            p.EnableGrain = content.Contains("\"enableGrain\": true");
            p.EnableVignette = content.Contains("\"enableVignette\": true");
            return p;
        }

        private static float ParseFloat(string content, string key, float defaultValue)
        {
            int idx = content.IndexOf($"\"{key}\":");
            if (idx < 0) return defaultValue;
            int start = idx + key.Length + 4;
            int end = start;
            while (end < content.Length && content[end] != ',' && content[end] != '}' && content[end] != '\n')
                end++;
            if (float.TryParse(content.AsSpan(start, end - start).Trim(), out float v))
                return v;
            return defaultValue;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 色调曲线编辑器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>色调曲线点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CurvePoint
    {
        public float X; // 输入 [0,1]
        public float Y; // 输出 [0,1]

        public CurvePoint(float x, float y) { X = x; Y = y; }
    }

    /// <summary>色调曲线（支持 R/G/B/Luma 四条曲线）。</summary>
    public sealed class ToneCurve
    {
        private List<CurvePoint> _master = new();
        private List<CurvePoint> _red = new();
        private List<CurvePoint> _green = new();
        private List<CurvePoint> _blue = new();
        private float[]? _masterLUT;
        private float[]? _redLUT;
        private float[]? _greenLUT;
        private float[]? _blueLUT;
        private const int LUT_SIZE = 256;
        private bool _dirty = true;

        public ToneCurve()
        {
            Reset();
        }

        public void Reset()
        {
            _master = new List<CurvePoint> { new(0, 0), new(1, 1) };
            _red = new List<CurvePoint> { new(0, 0), new(1, 1) };
            _green = new List<CurvePoint> { new(0, 0), new(1, 1) };
            _blue = new List<CurvePoint> { new(0, 0), new(1, 1) };
            _dirty = true;
        }

        public void AddPoint(TonalRange channel, CurvePoint point)
        {
            var list = channel switch
            {
                TonalRange.Shadows => _red,
                TonalRange.Midtones => _green,
                TonalRange.Highlights => _blue,
                _ => _master
            };
            list.Add(point);
            list.Sort((a, b) => a.X.CompareTo(b.X));
            _dirty = true;
        }

        public void RemovePoint(TonalRange channel, int index)
        {
            var list = channel switch
            {
                TonalRange.Shadows => _red,
                TonalRange.Midtones => _green,
                TonalRange.Highlights => _blue,
                _ => _master
            };
            if (index >= 0 && index < list.Count && list.Count > 2)
            {
                list.RemoveAt(index);
                _dirty = true;
            }
        }

        public float[]? GetLUT(TonalRange channel)
        {
            EnsureLUTs();
            return channel switch
            {
                TonalRange.Shadows => _redLUT,
                TonalRange.Midtones => _greenLUT,
                TonalRange.Highlights => _blueLUT,
                _ => _masterLUT
            };
        }

        private void EnsureLUTs()
        {
            if (!_dirty) return;
            _masterLUT = BuildLUT(_master);
            _redLUT = BuildLUT(_red);
            _greenLUT = BuildLUT(_green);
            _blueLUT = BuildLUT(_blue);
            _dirty = false;
        }

        private static float[] BuildLUT(List<CurvePoint> points)
        {
            var lut = new float[LUT_SIZE];
            for (int i = 0; i < LUT_SIZE; i++)
            {
                float x = (float)i / (LUT_SIZE - 1);
                lut[i] = SampleMonotonic(points, x);
            }
            return lut;
        }

        /// <summary>单调 Cubic Hermite 插值采样。</summary>
        private static float SampleMonotonic(List<CurvePoint> points, float x)
        {
            if (points.Count == 0) return x;
            if (x <= points[0].X) return points[0].Y;
            if (x >= points[^1].X) return points[^1].Y;

            int i = 0;
            while (i < points.Count - 1 && points[i + 1].X < x) i++;

            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(points.Count - 1, i + 2)];

            float t = (x - p1.X) / MathF.Max(p2.X - p1.X, 1e-6f);

            // Catmull-Rom
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                2 * p1.Y +
                (-p0.Y + p2.Y) * t +
                (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
        }

        /// <summary>应用曲线到颜色。</summary>
        public Vector3 Apply(Vector3 c)
        {
            EnsureLUTs();
            float r = SampleLUT(_masterLUT!, c.X);
            float g = SampleLUT(_masterLUT!, c.Y);
            float b = SampleLUT(_masterLUT!, c.Z);
            r = SampleLUT(_redLUT!, r);
            g = SampleLUT(_greenLUT!, g);
            b = SampleLUT(_blueLUT!, b);
            return new Vector3(r, g, b);
        }

        private static float SampleLUT(float[] lut, float v)
        {
            float scaled = Math.Clamp(v, 0, 1) * (LUT_SIZE - 1);
            int i0 = (int)scaled;
            int i1 = Math.Min(i0 + 1, LUT_SIZE - 1);
            float f = scaled - i0;
            return lut[i0] * (1 - f) + lut[i1] * f;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 调色体积（PostProcess Volume 风格）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>调色体积：在 3D 空间中应用不同调色参数。</summary>
    public sealed class ColorGradingVolume
    {
        /// <summary>体积中心（世界空间）。</summary>
        public Vector3 Center;
        /// <summary>体积范围。</summary>
        public Vector3 Extents = new(10, 10, 10);
        /// <summary>边界过渡距离。</summary>
        public float BlendDistance = 2.0f;
        /// <summary>优先级（高的覆盖低的）。</summary>
        public int Priority = 0;
        /// <summary>是否全局（无边界）。</summary>
        public bool IsGlobal = false;
        /// <summary>调色参数。</summary>
        public ColorGradingParams Params = ColorGradingParams.Default;
        /// <summary>启用。</summary>
        public bool Enabled = true;

        /// <summary>计算相机在体积内的权重（0~1）。</summary>
        public float CalculateWeight(Vector3 cameraPos)
        {
            if (!Enabled) return 0.0f;
            if (IsGlobal) return 1.0f;

            // 计算到体积边界的距离
            var delta = cameraPos - Center;
            var absDelta = new Vector3(
                Math.Abs(delta.X) - Extents.X * 0.5f,
                Math.Abs(delta.Y) - Extents.Y * 0.5f,
                Math.Abs(delta.Z) - Extents.Z * 0.5f);

            if (absDelta.X <= 0 && absDelta.Y <= 0 && absDelta.Z <= 0)
            {
                // 在体积内部
                float maxDelta = Math.Max(absDelta.X, Math.Max(absDelta.Y, absDelta.Z));
                // 越靠近中心权重越高
                return Math.Clamp(1.0f + maxDelta / BlendDistance, 0, 1);
            }

            // 在体积外部，但可能在 blend 距离内
            Vector3 positiveDelta = Vector3.Max(absDelta, Vector3.Zero);
            float dist = positiveDelta.Length();
            return Math.Clamp(1.0f - dist / BlendDistance, 0, 1);
        }
    }

    /// <summary>调色体积管理器。</summary>
    public sealed class ColorGradingVolumeManager
    {
        private readonly List<ColorGradingVolume> _volumes = new();

        public void AddVolume(ColorGradingVolume volume)
        {
            _volumes.Add(volume);
            _volumes.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public void RemoveVolume(ColorGradingVolume volume)
        {
            _volumes.Remove(volume);
        }

        public IReadOnlyList<ColorGradingVolume> Volumes => _volumes;

        /// <summary>根据相机位置计算混合后的调色参数。</summary>
        public ColorGradingParams Evaluate(Vector3 cameraPos)
        {
            var result = ColorGradingParams.Default;
            float totalWeight = 0;

            foreach (var vol in _volumes)
            {
                float w = vol.CalculateWeight(cameraPos);
                if (w <= 0) continue;
                totalWeight += w;

                // 线性混合（简化，实际应该按属性分别混合）
                result.Temperature = Lerp(result.Temperature, vol.Params.Temperature, w);
                result.Tint = Lerp(result.Tint, vol.Params.Tint, w);
                result.Contrast = Lerp(result.Contrast, vol.Params.Contrast, w);
                result.Saturation = Lerp(result.Saturation, vol.Params.Saturation, w);
                result.ExposureOffset = Lerp(result.ExposureOffset, vol.Params.ExposureOffset, w);
                result.Lift = Vector3.Lerp(result.Lift, vol.Params.Lift, w);
                result.Gamma = Vector3.Lerp(result.Gamma, vol.Params.Gamma, w);
                result.Gain = Vector3.Lerp(result.Gain, vol.Params.Gain, w);
                result.VignetteIntensity = Lerp(result.VignetteIntensity, vol.Params.VignetteIntensity, w);
                result.GrainAmount = Lerp(result.GrainAmount, vol.Params.GrainAmount, w);
            }

            return result;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 色域范围检测
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>色域范围检测工具。</summary>
    public static class GamutChecker
    {
        /// <summary>检测颜色是否在 sRGB 色域内。</summary>
        public static bool IsInSRGBGamut(Vector3 linearRGB)
        {
            return linearRGB.X >= 0 && linearRGB.X <= 1 &&
                   linearRGB.Y >= 0 && linearRGB.Y <= 1 &&
                   linearRGB.Z >= 0 && linearRGB.Z <= 1;
        }

        /// <summary>检测颜色是否在 Rec.2020 色域内。</summary>
        public static bool IsInRec2020Gamut(Vector3 linearRGB)
        {
            // Rec.2020 的色域比 sRGB 大，简化检测
            return linearRGB.X >= -0.1 && linearRGB.X <= 1.1 &&
                   linearRGB.Y >= -0.1 && linearRGB.Y <= 1.1 &&
                   linearRGB.Z >= -0.1 && linearRGB.Z <= 1.1;
        }

        /// <summary>软裁剪到 [0,1] 范围（保留超出色域的信息用于 LUT 查找）。</summary>
        public static Vector3 SoftClip(Vector3 c, float knee = 0.9f)
        {
            return new Vector3(
                SoftClip1D(c.X, knee),
                SoftClip1D(c.Y, knee),
                SoftClip1D(c.Z, knee));
        }

        private static float SoftClip1D(float v, float knee)
        {
            if (v <= knee) return v;
            if (v >= 1) return 1;
            float t = (v - knee) / (1 - knee);
            return knee + (1 - knee) * (1 - (1 - t) * (1 - t));
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 调色波形图（调试/可视化用）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>调色波形图生成器（用于 UI 显示）。</summary>
    public sealed class WaveformGenerator
    {
        private readonly int _width;
        private readonly int _height;
        private readonly float[] _buffer;

        public WaveformGenerator(int width, int height)
        {
            _width = width;
            _height = height;
            _buffer = new float[width * height * 4]; // RGBA
        }

        /// <summary>从纹理数据生成波形图。</summary>
        public float[] Generate(Span<float> imageData, int imgWidth, int imgHeight)
        {
            Array.Clear(_buffer, 0, _buffer.Length);

            // 把图像按列统计亮度分布
            for (int x = 0; x < imgWidth; x++)
            {
                int dstX = x * _width / imgWidth;
                for (int y = 0; y < imgHeight; y++)
                {
                    int srcIdx = (y * imgWidth + x) * 4;
                    float r = imageData[srcIdx];
                    float g = imageData[srcIdx + 1];
                    float b = imageData[srcIdx + 2];
                    float luma = r * 0.2126f + g * 0.7152f + b * 0.0722f;

                    int dstY = (int)((1 - luma) * (_height - 1));
                    dstY = Math.Clamp(dstY, 0, _height - 1);

                    int dstIdx = (dstY * _width + dstX) * 4;
                    _buffer[dstIdx] = Math.Min(1, _buffer[dstIdx] + 0.02f);
                    _buffer[dstIdx + 1] = Math.Min(1, _buffer[dstIdx + 1] + 0.02f);
                    _buffer[dstIdx + 2] = Math.Min(1, _buffer[dstIdx + 2] + 0.02f);
                    _buffer[dstIdx + 3] = 1.0f;
                }
            }

            return _buffer;
        }

        /// <summary>生成 RGB 分量波形图。</summary>
        public float[] GenerateRGB(Span<float> imageData, int imgWidth, int imgHeight)
        {
            Array.Clear(_buffer, 0, _buffer.Length);

            for (int x = 0; x < imgWidth; x++)
            {
                int dstX = x * _width / imgWidth;
                for (int y = 0; y < imgHeight; y++)
                {
                    int srcIdx = (y * imgWidth + x) * 4;
                    float r = imageData[srcIdx];
                    float g = imageData[srcIdx + 1];
                    float b = imageData[srcIdx + 2];

                    int dstYR = (int)((1 - r) * (_height - 1));
                    int dstYG = (int)((1 - g) * (_height - 1));
                    int dstYB = (int)((1 - b) * (_height - 1));

                    dstYR = Math.Clamp(dstYR, 0, _height - 1);
                    dstYG = Math.Clamp(dstYG, 0, _height - 1);
                    dstYB = Math.Clamp(dstYB, 0, _height - 1);

                    int idxR = (dstYR * _width + dstX) * 4;
                    _buffer[idxR] = Math.Min(1, _buffer[idxR] + 0.02f);

                    int idxG = (dstYG * _width + dstX) * 4 + 1;
                    _buffer[idxG] = Math.Min(1, _buffer[idxG] + 0.02f);

                    int idxB = (dstYB * _width + dstX) * 4 + 2;
                    _buffer[idxB] = Math.Min(1, _buffer[idxB] + 0.02f);

                    // Alpha
                    _buffer[(dstYR * _width + dstX) * 4 + 3] = 1;
                    _buffer[(dstYG * _width + dstX) * 4 + 3] = 1;
                    _buffer[(dstYB * _width + dstX) * 4 + 3] = 1;
                }
            }

            return _buffer;
        }

        /// <summary>生成矢量示波器（Vectorscope）。</summary>
        public float[] GenerateVectorscope(Span<float> imageData, int imgWidth, int imgHeight)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            float cx = _width * 0.5f, cy = _height * 0.5f;
            float radius = Math.Min(cx, cy) * 0.9f;

            for (int y = 0; y < imgHeight; y++)
            {
                for (int x = 0; x < imgWidth; x++)
                {
                    int srcIdx = (y * imgWidth + x) * 4;
                    float r = imageData[srcIdx];
                    float g = imageData[srcIdx + 1];
                    float b = imageData[srcIdx + 2];

                    // 转换到 YUV（简化）
                    float yComp = r * 0.299f + g * 0.587f + b * 0.114f;
                    float u = (b - yComp) * 0.493f;
                    float v = (r - yComp) * 0.877f;

                    int px = (int)(cx + u * radius);
                    int py = (int)(cy - v * radius);

                    if (px >= 0 && px < _width && py >= 0 && py < _height)
                    {
                        int idx = (py * _width + px) * 4;
                        _buffer[idx] = Math.Min(1, _buffer[idx] + 0.01f);
                        _buffer[idx + 1] = Math.Min(1, _buffer[idx + 1] + 0.01f);
                        _buffer[idx + 2] = Math.Min(1, _buffer[idx + 2] + 0.01f);
                        _buffer[idx + 3] = 1;
                    }
                }
            }

            return _buffer;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // LUT 生成器（程序化生成各种 LUT）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>程序化 LUT 生成器。</summary>
    public static class LUTGenerator
    {
        /// <summary>生成对比度 LUT。</summary>
        public static LUT3D GenerateContrastLUT(int size, float contrast, float pivot = 0.18f)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = $"Contrast_{contrast:F2}" };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        c = (c - new Vector3(pivot)) * contrast + new Vector3(pivot);
                        lut[r, g, b] = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
                    }
            return lut;
        }

        /// <summary>生成饱和度 LUT。</summary>
        public static LUT3D GenerateSaturationLUT(int size, float saturation)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = $"Saturation_{saturation:F2}" };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        lut[r, g, b] = ASC_CDL.ApplySaturation(c, saturation);
                    }
            return lut;
        }

        /// <summary>生成色温 LUT。</summary>
        public static LUT3D GenerateWhiteBalanceLUT(int size, float kelvin, float tint)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = $"WB_{kelvin}_{tint}" };
            var wb = WhiteBalance.ComputeBalance(kelvin, tint);
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        c *= wb;
                        lut[r, g, b] = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
                    }
            return lut;
        }

        /// <summary>生成 Lift/Gamma/Gain LUT。</summary>
        public static LUT3D GenerateLiftGammaGainLUT(int size, Vector3 lift, Vector3 gamma, Vector3 gain)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = "LGG" };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        c = c * gain + lift;
                        c = new Vector3(
                            c.X <= 0 ? 0 : MathF.Pow(c.X, gamma.X),
                            c.Y <= 0 ? 0 : MathF.Pow(c.Y, gamma.Y),
                            c.Z <= 0 ? 0 : MathF.Pow(c.Z, gamma.Z));
                        lut[r, g, b] = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
                    }
            return lut;
        }

        /// <summary>生成通道混合 LUT。</summary>
        public static LUT3D GenerateChannelMixerLUT(int size, Matrix3x3 mixer)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = "ChannelMixer" };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        lut[r, g, b] = Vector3.Clamp(mixer.Transform(c), Vector3.Zero, Vector3.One);
                    }
            return lut;
        }

        /// <summary>生成 HSL 调整 LUT。</summary>
        public static LUT3D GenerateHSLAdjustLUT(int size, float hueShift, float saturation, float lightness)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = $"HSL_{hueShift}_{saturation}_{lightness}" };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        // 简化版 HSL 调整
                        float luma = c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;
                        c = new Vector3(luma) + (c - new Vector3(luma)) * saturation;
                        c += new Vector3(lightness - 1.0f) * 0.5f;
                        lut[r, g, b] = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
                    }
            return lut;
        }

        /// <summary>组合多个 LUT 为一个（依次应用）。</summary>
        public static LUT3D Combine(params LUT3D[] luts)
        {
            if (luts.Length == 0) return GenerateIdentity(33);
            int size = luts[0].Size;
            var result = new LUT3D(size, LUTFormat.Binary) { Name = "Combined" };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        foreach (var lut in luts)
                            c = lut.SampleTrilinear(c);
                        result[r, g, b] = c;
                    }
            return result;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 调色 LUT 烘焙器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>把 ColorGradingParams 烘焙成单个 LUT（运行时更快）。</summary>
    public sealed class LUTBaker
    {
        private readonly ColorGradingSystem _system;

        public LUTBaker(ColorGradingSystem system)
        {
            _system = system;
        }

        /// <summary>烘焙当前调色参数为 3D LUT。</summary>
        public LUT3D Bake(int size = 33)
        {
            var lut = new LUT3D(size, LUTFormat.Binary) { Name = "Baked_" + DateTime.Now.Ticks };
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Vector3(
                            (float)r / (size - 1),
                            (float)g / (size - 1),
                            (float)b / (size - 1));
                        // 用 CPU 调色
                        lut[r, g, b] = _system.GradeColorCPU(c);
                    }
            return lut;
        }

        /// <summary>烘焙并保存。</summary>
        public void BakeAndSave(string outputPath, int size = 33)
        {
            var lut = Bake(size);
            LUTLoader.SaveBinary(lut, outputPath);
            Console.WriteLine($"[LUTBaker] 烘焙完成: {outputPath} (size={size})");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // HDR 显示器支持
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>HDR 显示模式。</summary>
    public enum HDRDisplayMode
    {
        SDR,
        HDR400,
        HDR600,
        HDR1000,
        DolbyVision
    }

    /// <summary>HDR 输出处理器。</summary>
    public static class HDROutput
    {
        /// <summary>根据 HDR 模式计算目标亮度。</summary>
        public static float GetPeakBrightness(HDRDisplayMode mode) => mode switch
        {
            HDRDisplayMode.SDR => 100.0f,
            HDRDisplayMode.HDR400 => 400.0f,
            HDRDisplayMode.HDR600 => 600.0f,
            HDRDisplayMode.HDR1000 => 1000.0f,
            HDRDisplayMode.DolbyVision => 4000.0f,
            _ => 100.0f
        };

        /// <summary>根据 HDR 模式计算目标 gamma。</summary>
        public static float GetTargetGamma(HDRDisplayMode mode) => mode switch
        {
            HDRDisplayMode.SDR => 2.2f,
            HDRDisplayMode.HDR400 => 2.2f,
            HDRDisplayMode.HDR600 => 2.4f,
            HDRDisplayMode.HDR1000 => 2.4f,
            HDRDisplayMode.DolbyVision => 2.2f,
            _ => 2.2f
        };

        /// <summary>PQ (Perceptual Quantizer) 编码。</summary>
        public static float EncodePQ(float linear, float peakBrightnessNits = 1000.0f)
        {
            // SMPTE ST 2084
            float m1 = 0.1593017578125f;
            float m2 = 78.84375f;
            float c1 = 0.8359375f;
            float c2 = 18.8515625f;
            float c3 = 18.6875f;

            float L = linear * peakBrightnessNits / 10000.0f;
            float Lm = MathF.Pow(L, m1);
            float numerator = c1 + c2 * Lm;
            float denominator = 1 + c3 * Lm;
            return MathF.Pow(numerator / denominator, m2);
        }

        /// <summary>PQ 解码。</summary>
        public static float DecodePQ(float pq, float peakBrightnessNits = 1000.0f)
        {
            float m1 = 0.1593017578125f;
            float m2 = 78.84375f;
            float c1 = 0.8359375f;
            float c2 = 18.8515625f;
            float c3 = 18.6875f;

            float pqM2 = MathF.Pow(pq, 1.0f / m2);
            float numerator = MathF.Max(pqM2 - c1, 0);
            float denominator = c2 - c3 * pqM2;
            float L = MathF.Pow(numerator / denominator, 1.0f / m1);
            return L * 10000.0f / peakBrightnessNits;
        }
    }
}
