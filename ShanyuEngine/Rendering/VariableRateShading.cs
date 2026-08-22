using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 可变速率着色 (Variable Rate Shading / VRS)
    //
    // VRS 允许对画面不同区域使用不同的像素着色频率：
    //   - 视觉中心（眼睛注视处）用 1x1 全分辨率
    //   - 边缘/角落用 2x2 或 4x4 降低着色成本
    //   - 配合 foveated rendering 效果更佳
    //
    // 支持的着色率：
    //   - 1x1: 每像素着色（最高质量）
    //   - 1x2: 水平每像素，垂直每 2 像素着色一次
    //   - 2x1: 垂直每像素，水平每 2 像素着色一次
    //   - 2x2: 每 2x2 像素块着色一次（最常用）
    //   - 2x4: 每 2x4 块
    //   - 4x2: 每 4x2 块
    //   - 4x4: 每 4x4 像素块着色一次（最大节省）
    //
    // VRS 驱动方式：
    //   1. Per-draw：整个 draw call 统一 shading rate
    //   2. Per-primitive：从 VS 输出的 SV_ShadingRate 决定
    //   3. Screen-space texture：一张 R8 纹理控制每块的着色率
    //
    // 本实现包含：
    //   - VRS 纹理生成（基于运动/深度/内容/注视点）
    //   - Foveated rendering 集成
    //   - 着色率组合策略
    //   - 质量自适应
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>着色率枚举（和 DX12/Vulkan 对齐）。</summary>
    public enum ShadingRate : byte
    {
        /// <summary>1x1：每像素着色。</summary>
        Full = 0,

        /// <summary>1x2：水平 1x，垂直 2x。</summary>
        OneByTwo = 1,

        /// <summary>2x1：水平 2x，垂直 1x。</summary>
        TwoByOne = 4,

        /// <summary>2x2：每 2x2 块着色一次。</summary>
        TwoByTwo = 5,

        /// <summary>2x4：每 2x4 块着色一次。</summary>
        TwoByFour = 6,

        /// <summary>4x2：每 4x2 块着色一次。</summary>
        FourByTwo = 9,

        /// <summary>4x4：每 4x4 块着色一次（最大节省）。</summary>
        FourByFour = 10
    }

    /// <summary>VRS 组合精度（两个着色率组合时的行为）。</summary>
    public enum ShadingRateCombiner
    {
        /// <summary>保留前者。</summary>
        Keep = 0,
        /// <summary>取最小（更精细的着色）。</summary>
        Min = 1,
        /// <summary>取最大（更粗的着色，节省性能）。</summary>
        Max = 2,
        /// <summary>相乘（面积相乘）。</summary>
        Mul = 3
    }

    /// <summary>VRS 驱动模式。</summary>
    public enum VRSDriverMode
    {
        /// <summary>禁用 VRS。</summary>
        Disabled,
        /// <summary>全屏统一着色率。</summary>
        Uniform,
        /// <summary>纹理驱动（screen-space VRS texture）。</summary>
        TextureDriven,
        /// <summary>Foveated（基于注视点/鼠标位置）。</summary>
        Foveated,
        /// <summary>自动（综合运动/深度/内容/注视点）。</summary>
        Auto
    }

    /// <summary>VRS 配置。</summary>
    public sealed class VRSConfig
    {
        /// <summary>驱动模式。</summary>
        public VRSDriverMode Mode = VRSDriverMode.Auto;

        /// <summary>统一模式下的固定着色率。</summary>
        public ShadingRate UniformShadingRate = ShadingRate.TwoByTwo;

        /// <summary>Per-draw combiner。</summary>
        public ShadingRateCombiner PerDrawCombiner = ShadingRateCombiner.Keep;

        /// <summary>Per-primitive combiner。</summary>
        public ShadingRateCombiner PerPrimitiveCombiner = ShadingRateCombiner.Min;

        /// <summary>纹理驱动 combiner。</summary>
        public ShadingRateCombiner TextureCombiner = ShadingRateCombiner.Max;

        /// <summary>VRS 纹理分辨率比例（相对于屏幕）。</summary>
        /// <remarks>1.0 = 每像素一个 shading rate，实际通常用 0.25（1/4 分辨率）就够了。</remarks>
        public float TextureResolutionScale = 0.25f;

        /// <summary>是否基于运动矢量降低运动区域的着色率。</summary>
        public bool MotionBased = true;

        /// <summary>运动矢量的阈值（像素/帧），超过此值降低着色率。</summary>
        public float MotionThreshold = 2.0f;

        /// <summary>是否基于深度不连续性保护边缘。</summary>
        public bool DepthEdgeProtection = true;

        /// <summary>深度差阈值。</summary>
        public float DepthEdgeThreshold = 0.02f;

        /// <summary>是否基于亮度/内容复杂度降低着色率。</summary>
        public bool ContentBased = true;

        /// <summary>亮度变化阈值。</summary>
        public float LuminanceChangeThreshold = 0.05f;

        /// <summary>是否基于色差保护（VRS 会在低着色率区域产生色差）。</summary>
        public bool ChromaProtection = true;

        /// <summary>Foveated 设置。</summary>
        public FoveatedVRSConfig Foveated = new();

        /// <summary>最小着色率（不允许更低）。</summary>
        public ShadingRate MinShadingRate = ShadingRate.TwoByTwo;

        /// <summary>最大着色率（不允许更高）。</summary>
        public ShadingRate MaxShadingRate = ShadingRate.Full;

        /// <summary>目标帧率（自适应 VRS 用）。</summary>
        public float TargetFrameRate = 60.0f;

        /// <summary>性能预设。</summary>
        public static VRSConfig Performance => new()
        {
            Mode = VRSDriverMode.Foveated,
            UniformShadingRate = ShadingRate.FourByFour,
            TextureResolutionScale = 0.125f,
            MotionBased = true,
            ContentBased = false,
            MinShadingRate = ShadingRate.TwoByTwo,
            Foveated = FoveatedVRSConfig.Aggressive
        };

        /// <summary>平衡预设。</summary>
        public static VRSConfig Balanced => new()
        {
            Mode = VRSDriverMode.Auto,
            UniformShadingRate = ShadingRate.TwoByTwo,
            TextureResolutionScale = 0.25f,
            MotionBased = true,
            DepthEdgeProtection = true,
            ContentBased = true,
            MinShadingRate = ShadingRate.OneByTwo,
            Foveated = FoveatedVRSConfig.Balanced
        };

        /// <summary>质量预设。</summary>
        public static VRSConfig Quality => new()
        {
            Mode = VRSDriverMode.Foveated,
            UniformShadingRate = ShadingRate.OneByTwo,
            TextureResolutionScale = 0.5f,
            MotionBased = true,
            DepthEdgeProtection = true,
            ContentBased = true,
            ChromaProtection = true,
            MinShadingRate = ShadingRate.Full,
            Foveated = FoveatedVRSConfig.Quality
        };
    }

    /// <summary>Foveated VRS 配置。</summary>
    public sealed class FoveatedVRSConfig
    {
        /// <summary>注视点 X（归一化 0-1）。</summary>
        public float GazeX = 0.5f;

        /// <summary>注视点 Y（归一化 0-1）。</summary>
        public float GazeY = 0.5f;

        /// <summary>内圈半径（归一化 0-0.5），使用 1x1 着色率。</summary>
        public float InnerRadius = 0.15f;

        /// <summary>中圈半径，使用 2x2 着色率。</summary>
        public float MiddleRadius = 0.35f;

        /// <summary>外圈半径，使用 4x4 着色率。</summary>
        public float OuterRadius = 0.5f;

        /// <summary>内外圈之间的过渡区域宽度。</summary>
        public float TransitionWidth = 0.05f;

        /// <summary>是否用鼠标位置驱动注视点（无眼动追踪时）。</summary>
        public bool UseMouseAsGaze = true;

        /// <summary>注视点平滑插值速度（0-1，越大越快跟随）。</summary>
        public float GazeSmoothing = 0.15f;

        /// <summary>内圈着色率。</summary>
        public ShadingRate InnerRate = ShadingRate.Full;

        /// <summary>中圈着色率。</summary>
        public ShadingRate MiddleRate = ShadingRate.TwoByTwo;

        /// <summary>外圈着色率。</summary>
        public ShadingRate OuterRate = ShadingRate.FourByFour;

        /// <summary>激进预设（更小的内圈）。</summary>
        public static FoveatedVRSConfig Aggressive => new()
        {
            InnerRadius = 0.08f,
            MiddleRadius = 0.2f,
            OuterRadius = 0.4f,
            InnerRate = ShadingRate.TwoByTwo,
            MiddleRate = ShadingRate.TwoByTwo,
            OuterRate = ShadingRate.FourByFour
        };

        /// <summary>平衡预设。</summary>
        public static FoveatedVRSConfig Balanced => new()
        {
            InnerRadius = 0.15f,
            MiddleRadius = 0.35f,
            OuterRadius = 0.5f,
            InnerRate = ShadingRate.Full,
            MiddleRate = ShadingRate.TwoByTwo,
            OuterRate = ShadingRate.FourByFour
        };

        /// <summary>质量预设（更大的内圈）。</summary>
        public static FoveatedVRSConfig Quality => new()
        {
            InnerRadius = 0.25f,
            MiddleRadius = 0.45f,
            OuterRadius = 0.7f,
            InnerRate = ShadingRate.Full,
            MiddleRate = ShadingRate.OneByTwo,
            OuterRate = ShadingRate.TwoByTwo
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // VRS 着色率工具
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>VRS 工具函数。</summary>
    public static class VRSTool
    {
        /// <summary>获取着色率的水平/垂直子像素数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int x, int y) GetShadingRateDimensions(ShadingRate rate) => rate switch
        {
            ShadingRate.Full => (1, 1),
            ShadingRate.OneByTwo => (1, 2),
            ShadingRate.TwoByOne => (2, 1),
            ShadingRate.TwoByTwo => (2, 2),
            ShadingRate.TwoByFour => (2, 4),
            ShadingRate.FourByTwo => (4, 2),
            ShadingRate.FourByFour => (4, 4),
            _ => (1, 1)
        };

        /// <summary>获取着色率的像素面积。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetShadingRateArea(ShadingRate rate)
        {
            var (x, y) = GetShadingRateDimensions(rate);
            return x * y;
        }

        /// <summary>组合两个着色率。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ShadingRate Combine(ShadingRate a, ShadingRate b, ShadingRateCombiner combiner) => combiner switch
        {
            ShadingRateCombiner.Keep => a,
            ShadingRateCombiner.Min => CombineMin(a, b),
            ShadingRateCombiner.Max => CombineMax(a, b),
            ShadingRateCombiner.Mul => CombineMul(a, b),
            _ => a
        };

        private static ShadingRate CombineMin(ShadingRate a, ShadingRate b)
        {
            int areaA = GetShadingRateArea(a);
            int areaB = GetShadingRateArea(b);
            return areaA <= areaB ? a : b;
        }

        private static ShadingRate CombineMax(ShadingRate a, ShadingRate b)
        {
            int areaA = GetShadingRateArea(a);
            int areaB = GetShadingRateArea(b);
            return areaA >= areaB ? a : b;
        }

        private static ShadingRate CombineMul(ShadingRate a, ShadingRate b)
        {
            var (ax, ay) = GetShadingRateDimensions(a);
            var (bx, by) = GetShadingRateDimensions(b);
            int rx = Math.Min(ax * bx, 4);
            int ry = Math.Min(ay * by, 4);
            return (rx, ry) switch
            {
                (1, 1) => ShadingRate.Full,
                (1, 2) => ShadingRate.OneByTwo,
                (2, 1) => ShadingRate.TwoByOne,
                (2, 2) => ShadingRate.TwoByTwo,
                (2, 4) => ShadingRate.TwoByFour,
                (4, 2) => ShadingRate.FourByTwo,
                (4, 4) => ShadingRate.FourByFour,
                _ => ShadingRate.FourByFour
            };
        }

        /// <summary>计算距离注视点的归一化距离（0 = 中心，1 = 角落）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DistanceToGaze(float px, float py, float gazeX, float gazeY, int width, int height)
        {
            // 用对数距离让中间区域更平滑
            float nx = px / width;
            float ny = py / height;
            float dx = nx - gazeX;
            float dy = ny - gazeY;
            return MathF.Sqrt(dx * dx + dy * dy) * 2.0f; // *2 因为 gaze 在 0.5
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // VRS 纹理生成器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    // VRS 纹理生成器：根据多种输入源生成 VRS shading rate 纹理。
    // 纹理格式：R8_UNorm，每像素一个着色率值。
    /// </summary>
    public sealed class VRSTextureGenerator
    {
        private readonly VRSConfig _config;
        private byte[] _rateTexture;
        private float[] _motionMap;
        private float[] _depthMap;
        private float[] _luminanceMap;
        private int _width, _height;
        private int _vrsWidth, _vrsHeight;

        // 平滑后的注视点
        private float _smoothGazeX = 0.5f;
        private float _smoothGazeY = 0.5f;

        public int Width => _width;
        public int Height => _height;
        public int VRSWidth => _vrsWidth;
        public int VRSHeight => _vrsHeight;
        public ReadOnlySpan<byte> RateTexture => _rateTexture;

        public VRSTextureGenerator(int width, int height, VRSConfig? config = null)
        {
            _config = config ?? new VRSConfig();
            _width = width;
            _height = height;
            _vrsWidth = Math.Max(1, (int)(width * _config.TextureResolutionScale));
            _vrsHeight = Math.Max(1, (int)(height * _config.TextureResolutionScale));
            _rateTexture = new byte[_vrsWidth * _vrsHeight];
            _motionMap = new float[_vrsWidth * _vrsHeight];
            _depthMap = new float[_vrsWidth * _vrsHeight];
            _luminanceMap = new float[_vrsWidth * _vrsHeight];
        }

        /// <summary>调整大小。</summary>
        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;
            _vrsWidth = Math.Max(1, (int)(width * _config.TextureResolutionScale));
            _vrsHeight = Math.Max(1, (int)(height * _config.TextureResolutionScale));
            _rateTexture = new byte[_vrsWidth * _vrsHeight];
            _motionMap = new float[_vrsWidth * _vrsHeight];
            _depthMap = new float[_vrsWidth * _vrsHeight];
            _luminanceMap = new float[_vrsWidth * _vrsHeight];
        }

        /// <summary>更新运动矢量图（从运动矢量纹理降采样）。</summary>
        public void UpdateMotionMap(ReadOnlySpan<Vector2> motionVectors, int srcWidth, int srcHeight)
        {
            for (int y = 0; y < _vrsHeight; y++)
            {
                for (int x = 0; x < _vrsWidth; x++)
                {
                    // 降采样：取对应区域的平均运动量
                    float srcX = x / (float)_vrsWidth * srcWidth;
                    float srcY = y / (float)_vrsHeight * srcHeight;
                    int sx = Math.Clamp((int)srcX, 0, srcWidth - 1);
                    int sy = Math.Clamp((int)srcY, 0, srcHeight - 1);
                    int idx = sy * srcWidth + sx;

                    float motionLen = 0.0f;
                    if (idx < motionVectors.Length)
                    {
                        var mv = motionVectors[idx];
                        motionLen = mv.Length();
                    }

                    // 对周围 2x2 取平均
                    // NOTE: 简化版，只取单点
                    _motionMap[y * _vrsWidth + x] = motionLen;
                }
            }
        }

        /// <summary>更新深度图（降采样）。</summary>
        public void UpdateDepthMap(ReadOnlySpan<float> depth, int srcWidth, int srcHeight)
        {
            for (int y = 0; y < _vrsHeight; y++)
            {
                for (int x = 0; x < _vrsWidth; x++)
                {
                    float srcX = x / (float)_vrsWidth * srcWidth;
                    float srcY = y / (float)_vrsHeight * srcHeight;
                    int sx = Math.Clamp((int)srcX, 0, srcWidth - 1);
                    int sy = Math.Clamp((int)srcY, 0, srcHeight - 1);
                    int idx = sy * srcWidth + sx;

                    float d = idx < depth.Length ? depth[idx] : 0.0f;
                    _depthMap[y * _vrsWidth + x] = d;
                }
            }
        }

        /// <summary>更新亮度图（从 HDR 颜色计算）。</summary>
        public void UpdateLuminanceMap(ReadOnlySpan<Vector4> hdrColor, int srcWidth, int srcHeight)
        {
            for (int y = 0; y < _vrsHeight; y++)
            {
                for (int x = 0; x < _vrsWidth; x++)
                {
                    float srcX = x / (float)_vrsWidth * srcWidth;
                    float srcY = y / (float)_vrsHeight * srcHeight;
                    int sx = Math.Clamp((int)srcX, 0, srcWidth - 1);
                    int sy = Math.Clamp((int)srcY, 0, srcHeight - 1);
                    int idx = sy * srcWidth + sx;

                    float lum = 0.0f;
                    if (idx < hdrColor.Length)
                    {
                        var c = hdrColor[idx];
                        lum = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
                    }
                    _luminanceMap[y * _vrsWidth + x] = lum;
                }
            }
        }

        /// <summary>生成 VRS 纹理。</summary>
        public void Generate(float mouseX = -1.0f, float mouseY = -1.0f)
        {
            // 更新注视点
            if (_config.Mode == VRSDriverMode.Foveated || _config.Mode == VRSDriverMode.Auto)
            {
                float targetX = _config.Foveated.GazeX;
                float targetY = _config.Foveated.GazeY;

                if (_config.Foveated.UseMouseAsGaze && mouseX >= 0.0f && mouseY >= 0.0f)
                {
                    targetX = mouseX / _width;
                    targetY = 1.0f - mouseY / _height;
                }

                // 平滑插值
                float smoothing = _config.Foveated.GazeSmoothing;
                _smoothGazeX += (targetX - _smoothGazeX) * smoothing;
                _smoothGazeY += (targetY - _smoothGazeY) * smoothing;
            }

            switch (_config.Mode)
            {
                case VRSDriverMode.Disabled:
                    FillUniform(ShadingRate.Full);
                    break;

                case VRSDriverMode.Uniform:
                    FillUniform(_config.UniformShadingRate);
                    break;

                case VRSDriverMode.TextureDriven:
                    GenerateTextureDriven();
                    break;

                case VRSDriverMode.Foveated:
                    GenerateFoveated();
                    break;

                case VRSDriverMode.Auto:
                    GenerateAuto();
                    break;
            }

            // Clamp 到最小/最大着色率
            ClampRates(_config.MinShadingRate, _config.MaxShadingRate);
        }

        private void FillUniform(ShadingRate rate)
        {
            byte r = (byte)rate;
            Array.Fill(_rateTexture, r);
        }

        /// <summary>纹理驱动模式：基于运动、深度边缘、亮度变化决定着色率。</summary>
        private void GenerateTextureDriven()
        {
            for (int y = 0; y < _vrsHeight; y++)
            {
                for (int x = 0; x < _vrsWidth; x++)
                {
                    int idx = y * _vrsWidth + x;
                    ShadingRate rate = ShadingRate.Full;

                    // 运动矢量：运动大 -> 降低着色率（人眼对运动模糊区域不敏感）
                    if (_config.MotionBased)
                    {
                        float motion = _motionMap[idx];
                        if (motion > _config.MotionThreshold * 4.0f)
                            rate = VRSTool.Combine(rate, ShadingRate.FourByFour, ShadingRateCombiner.Max);
                        else if (motion > _config.MotionThreshold)
                            rate = VRSTool.Combine(rate, ShadingRate.TwoByTwo, ShadingRateCombiner.Max);
                    }

                    // 深度边缘：深度变化大 -> 保持高着色率
                    if (_config.DepthEdgeProtection && rate != ShadingRate.Full)
                    {
                        float depth = _depthMap[idx];
                        bool isEdge = false;
                        if (x > 0 && MathF.Abs(depth - _depthMap[idx - 1]) > _config.DepthEdgeThreshold) isEdge = true;
                        if (y > 0 && MathF.Abs(depth - _depthMap[idx - _vrsWidth]) > _config.DepthEdgeThreshold) isEdge = true;
                        if (x < _vrsWidth - 1 && MathF.Abs(depth - _depthMap[idx + 1]) > _config.DepthEdgeThreshold) isEdge = true;
                        if (y < _vrsHeight - 1 && MathF.Abs(depth - _depthMap[idx + _vrsWidth]) > _config.DepthEdgeThreshold) isEdge = true;

                        if (isEdge)
                            rate = VRSTool.Combine(rate, ShadingRate.Full, ShadingRateCombiner.Min);
                    }

                    // 亮度/内容：亮度变化大 -> 保持高着色率
                    if (_config.ContentBased && rate != ShadingRate.Full)
                    {
                        float lum = _luminanceMap[idx];
                        bool isComplex = false;
                        if (x > 0 && MathF.Abs(lum - _luminanceMap[idx - 1]) > _config.LuminanceChangeThreshold) isComplex = true;
                        if (y > 0 && MathF.Abs(lum - _luminanceMap[idx - _vrsWidth]) > _config.LuminanceChangeThreshold) isComplex = true;

                        if (isComplex)
                            rate = VRSTool.Combine(rate, ShadingRate.OneByTwo, ShadingRateCombiner.Min);
                    }

                    _rateTexture[idx] = (byte)rate;
                }
            }
        }

        /// <summary>Foveated 模式：基于注视点。</summary>
        private void GenerateFoveated()
        {
            float gazeX = _smoothGazeX;
            float gazeY = _smoothGazeY;
            float innerR = _config.Foveated.InnerRadius;
            float middleR = _config.Foveated.MiddleRadius;
            float outerR = _config.Foveated.OuterRadius;
            float transition = _config.Foveated.TransitionWidth;

            for (int y = 0; y < _vrsHeight; y++)
            {
                for (int x = 0; x < _vrsWidth; x++)
                {
                    float nx = (x + 0.5f) / _vrsWidth;
                    float ny = (y + 0.5f) / _vrsHeight;
                    float dist = VRSTool.DistanceToGaze(nx, ny, gazeX, gazeY, 1, 1);

                    ShadingRate rate;
                    if (dist < innerR)
                    {
                        rate = _config.Foveated.InnerRate;
                    }
                    else if (dist < innerR + transition)
                    {
                        // 内圈到中圈的过渡
                        float t = (dist - innerR) / transition;
                        rate = t < 0.5f ? _config.Foveated.InnerRate : _config.Foveated.MiddleRate;
                    }
                    else if (dist < middleR)
                    {
                        rate = _config.Foveated.MiddleRate;
                    }
                    else if (dist < middleR + transition)
                    {
                        float t = (dist - middleR) / transition;
                        rate = t < 0.5f ? _config.Foveated.MiddleRate : _config.Foveated.OuterRate;
                    }
                    else if (dist < outerR)
                    {
                        rate = _config.Foveated.OuterRate;
                    }
                    else
                    {
                        rate = _config.Foveated.OuterRate;
                    }

                    _rateTexture[y * _vrsWidth + x] = (byte)rate;
                }
            }
        }

        /// <summary>自动模式：综合 foveated + 运动 + 深度 + 内容。</summary>
        private void GenerateAuto()
        {
            float gazeX = _smoothGazeX;
            float gazeY = _smoothGazeY;

            for (int y = 0; y < _vrsHeight; y++)
            {
                for (int x = 0; x < _vrsWidth; x++)
                {
                    int idx = y * _vrsWidth + x;
                    float nx = (x + 0.5f) / _vrsWidth;
                    float ny = (y + 0.5f) / _vrsHeight;

                    // 1. Foveated 基础着色率
                    float dist = VRSTool.DistanceToGaze(nx, ny, gazeX, gazeY, 1, 1);
                    ShadingRate foveatedRate;
                    if (dist < _config.Foveated.InnerRadius)
                        foveatedRate = _config.Foveated.InnerRate;
                    else if (dist < _config.Foveated.MiddleRadius)
                        foveatedRate = _config.Foveated.MiddleRate;
                    else
                        foveatedRate = _config.Foveated.OuterRate;

                    ShadingRate rate = foveatedRate;

                    // 2. 运动调整：运动大可以更激进
                    if (_config.MotionBased)
                    {
                        float motion = _motionMap[idx];
                        if (motion > _config.MotionThreshold * 2.0f)
                            rate = VRSTool.Combine(rate, ShadingRate.FourByFour, ShadingRateCombiner.Max);
                    }

                    // 3. 深度边缘保护
                    if (_config.DepthEdgeProtection)
                    {
                        float depth = _depthMap[idx];
                        bool isEdge = false;
                        if (x > 0 && MathF.Abs(depth - _depthMap[idx - 1]) > _config.DepthEdgeThreshold) isEdge = true;
                        if (y > 0 && MathF.Abs(depth - _depthMap[idx - _vrsWidth]) > _config.DepthEdgeThreshold) isEdge = true;

                        if (isEdge)
                            rate = VRSTool.Combine(rate, ShadingRate.Full, ShadingRateCombiner.Min);
                    }

                    // 4. 内容复杂度
                    if (_config.ContentBased)
                    {
                        float lum = _luminanceMap[idx];
                        bool isComplex = false;
                        if (x > 0 && MathF.Abs(lum - _luminanceMap[idx - 1]) > _config.LuminanceChangeThreshold) isComplex = true;
                        if (y > 0 && MathF.Abs(lum - _luminanceMap[idx - _vrsWidth]) > _config.LuminanceChangeThreshold) isComplex = true;

                        if (isComplex)
                            rate = VRSTool.Combine(rate, ShadingRate.OneByTwo, ShadingRateCombiner.Min);
                    }

                    _rateTexture[idx] = (byte)rate;
                }
            }
        }

        /// <summary>Clamp 着色率到配置的范围内。</summary>
        private void ClampRates(ShadingRate minRate, ShadingRate maxRate)
        {
            int minArea = VRSTool.GetShadingRateArea(minRate);
            int maxArea = VRSTool.GetShadingRateArea(maxRate);

            for (int i = 0; i < _rateTexture.Length; i++)
            {
                var rate = (ShadingRate)_rateTexture[i];
                int area = VRSTool.GetShadingRateArea(rate);

                if (area < minArea)
                    _rateTexture[i] = (byte)minRate;
                else if (area > maxArea)
                    _rateTexture[i] = (byte)maxRate;
            }
        }

        /// <summary>获取着色率分布统计。</summary>
        public (int full, int oneByTwo, int twoByOne, int twoByTwo, int fourByFour) GetRateDistribution()
        {
            int full = 0, oneByTwo = 0, twoByOne = 0, twoByTwo = 0, fourByFour = 0;
            foreach (var b in _rateTexture)
            {
                switch ((ShadingRate)b)
                {
                    case ShadingRate.Full: full++; break;
                    case ShadingRate.OneByTwo: oneByTwo++; break;
                    case ShadingRate.TwoByOne: twoByOne++; break;
                    case ShadingRate.TwoByTwo: twoByTwo++; break;
                    case ShadingRate.TwoByFour: twoByTwo++; break;
                    case ShadingRate.FourByTwo: twoByTwo++; break;
                    case ShadingRate.FourByFour: fourByFour++; break;
                }
            }
            return (full, oneByTwo, twoByOne, twoByTwo, fourByFour);
        }

        /// <summary>估算 VRS 节省的像素着色百分比。</summary>
        public float EstimateSavings()
        {
            var (full, oneByTwo, twoByOne, twoByTwo, fourByFour) = GetRateDistribution();
            int total = _rateTexture.Length;
            if (total == 0) return 0.0f;

            // 每种着色率的节省比例
            float savings = 0.0f;
            savings += (1.0f - 1.0f / 1.0f) * (full / (float)total);      // 1x1: 0% 节省
            savings += (1.0f - 1.0f / 2.0f) * (oneByTwo / (float)total);   // 1x2: 50%
            savings += (1.0f - 1.0f / 2.0f) * (twoByOne / (float)total);   // 2x1: 50%
            savings += (1.0f - 1.0f / 4.0f) * (twoByTwo / (float)total);   // 2x2: 75%
            savings += (1.0f - 1.0f / 16.0f) * (fourByFour / (float)total); // 4x4: 93.75%

            return savings;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // VRS 管理器（和 GPU 交互）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    // VRS 管理器：整合纹理生成 + GPU VRS API 调用。
    /// </summary>
    public sealed class VariableRateShadingManager : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly VRSConfig _config;
        private readonly VRSTextureGenerator _generator;
        private RHITexture? _vrsTexture;
        private RHISampler? _vrsSampler;
        private bool _disposed;
        private bool _hwSupported;

        public VRSConfig Config => _config;
        public VRSTextureGenerator Generator => _generator;
        public bool HardwareSupported => _hwSupported;
        public bool Enabled => _config.Mode != VRSDriverMode.Disabled;
        public float CurrentSavings => _generator.EstimateSavings();

        public VariableRateShadingManager(RHIDevice device, int width, int height, VRSConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new VRSConfig();
            _hwSupported = device.Capabilities?.SupportsVRS ?? false;
            _generator = new VRSTextureGenerator(width, height, _config);

            if (_hwSupported)
            {
                AllocateGPUResources();
            }
        }

        private void AllocateGPUResources()
        {
            _vrsTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = _generator.VRSWidth,
                Height = _generator.VRSHeight,
                Format = RHIFormat.R8_UNorm,
                BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.ShadingRate,
                MipLevels = 1,
                ArraySize = 1
            });

            _vrsSampler = _device.CreateSampler(new RHISamplerDesc
            {
                Filter = RHIFilter.MinMagMipPoint,
                AddressU = RHITextureAddressMode.Clamp,
                AddressV = RHITextureAddressMode.Clamp,
                AddressW = RHITextureAddressMode.Clamp
            });
        }

        /// <summary>更新 VRS（每帧调用）。</summary>
        public void Update(float mouseX = -1.0f, float mouseY = -1.0f)
        {
            if (!Enabled) return;
            _generator.Generate(mouseX, mouseY);

            if (_hwSupported && _vrsTexture != null)
            {
                _device.UpdateTextureData(_vrsTexture, _generator.RateTexture);
            }
        }

        /// <summary>在绘制前调用：设置 VRS 状态。</summary>
        public void ApplyShadingRate()
        {
            if (!Enabled || !_hwSupported) return;

            if (_config.Mode == VRSDriverMode.Uniform)
            {
                _device.SetShadingRate(_config.UniformShadingRate);
            }
            else if (_vrsTexture != null)
            {
                _device.SetShadingRateImage(_vrsTexture, _config.TextureCombiner);
            }
        }

        /// <summary>在绘制后调用：重置 VRS 状态。</summary>
        public void ResetShadingRate()
        {
            if (!_hwSupported) return;
            _device.SetShadingRate(ShadingRate.Full);
            _device.SetShadingRateImage(null, ShadingRateCombiner.Keep);
        }

        /// <summary>自适应调整：根据帧率自动降低/提高 VRS 等级。</summary>
        public void AdaptQuality(float currentFrameTimeMs)
        {
            if (_config.Mode != VRSDriverMode.Auto) return;

            float targetMs = 1000.0f / _config.TargetFrameRate;
            if (currentFrameTimeMs > targetMs * 1.1f)
            {
                // 帧率过低 -> 更激进的 VRS
                _config.Foveated.InnerRadius = Math.Max(0.05f, _config.Foveated.InnerRadius - 0.01f);
                _config.Foveated.MiddleRadius = Math.Max(0.15f, _config.Foveated.MiddleRadius - 0.02f);
                _config.MinShadingRate = ShadingRate.TwoByTwo;
            }
            else if (currentFrameTimeMs < targetMs * 0.8f)
            {
                // 帧率有余量 -> 提高 VRS 质量
                _config.Foveated.InnerRadius = Math.Min(0.3f, _config.Foveated.InnerRadius + 0.005f);
                _config.Foveated.MiddleRadius = Math.Min(0.5f, _config.Foveated.MiddleRadius + 0.01f);
            }
        }

        /// <summary>调整渲染目标大小。</summary>
        public void Resize(int width, int height)
        {
            _generator.Resize(width, height);
            if (_hwSupported)
            {
                _vrsTexture?.Dispose();
                AllocateGPUResources();
            }
        }

        /// <summary>获取 VRS 统计信息。</summary>
        public string GetStats()
        {
            if (!Enabled) return "VRS: Disabled";
            var dist = _generator.GetRateDistribution();
            float savings = _generator.EstimateSavings();
            return $"VRS: Mode={_config.Mode}, HW={_hwSupported}, " +
                   $"Full={dist.full} 1x2={dist.oneByTwo} 2x1={dist.twoByOne} " +
                   $"2x2={dist.twoByTwo} 4x4={dist.fourByFour}, Savings={savings:P1}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _vrsTexture?.Dispose();
            _vrsSampler?.Dispose();
            _disposed = true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // VRS 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct VRSPassCB
    {
        public Vector2 InvScreenSize;
        public float MotionThreshold;
        public float DepthEdgeThreshold;
        public float LuminanceThreshold;
        public float GazeX;
        public float GazeY;
        public int Mode;
        public int InnerRadius;
        public int MiddleRadius;
        public int OuterRadius;
        public int MinRate;
        public int MaxRate;
        public Vector2 Pad;
    }
}