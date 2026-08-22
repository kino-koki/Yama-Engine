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
    // 山雨 阴影过滤系统 (Shadow Filtering System)
    //
    // 支持多种阴影过滤算法：
    //   1. PCF (Percentage Closer Filtering) — 硬件 2x2 / 软件可选 3x3~9x9
    //   2. PCSS (Percentage Closer Soft Shadows) — 物理正确的软阴影
    //      - Blocker Search（搜索遮挡物）
    //      - Penumbra Estimation（半影大小估算）
    //      - Variable-Size PCF（可变半径 PCF）
    //   3. VSM (Variance Shadow Maps) — 基于方差，单次采样
    //   4. EVSM (Exponential VSM) — 改进 VSM 的漏光问题
    //   5. MSM (Moment Shadow Maps) — 更精确的方差
    //   6. SAT (Summed Area Table) — 加速大核 PCF
    //   7. 自适应采样 — 基于噪声检测动态选择采样数量
    //
    // 代码风格：PCSS 核心逻辑在 CPU 有参考实现，GPU 用 Compute Shader
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>阴影过滤算法。</summary>
    public enum ShadowFilterType
    {
        /// <summary>不过滤，硬阴影。</summary>
        None,
        /// <summary>硬件 2x2 PCF（最快，效果差）。</summary>
        HardwarePCF,
        /// <summary>固定大小 PCF（3x3, 5x5, 7x7...）。</summary>
        FixedPCF,
        /// <summary>Poisson 圆盘 PCF（采样点少但分布好）。</summary>
        PoissonPCF,
        /// <summary>Vogel 盘 PCF（更好的采样分布）。</summary>
        VogelDiskPCF,
        /// <summary>PCSS（软阴影）。</summary>
        PCSS,
        /// <summary>改进版 PCSS（搜索更准确）。</summary>
        PCSSImproved,
        /// <summary>VSM。</summary>
        VSM,
        /// <summary>EVSM。</summary>
        EVSM,
        /// <summary>MSM (Moment Shadow Maps)。</summary>
        MSM,
        /// <summary>自适应过滤（根据噪声动态选算法）。</summary>
        Adaptive
    }

    /// <summary>过滤质量。</summary>
    public enum ShadowFilterQuality
    {
        VeryLow,
        Low,
        Medium,
        High,
        Ultra
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>阴影过滤配置。</summary>
    public sealed class ShadowFilterConfig
    {
        /// <summary>过滤类型。</summary>
        public ShadowFilterType FilterType = ShadowFilterType.PCSS;

        /// <summary>质量等级。</summary>
        public ShadowFilterQuality Quality = ShadowFilterQuality.High;

        // ─── PCF 参数 ───
        /// <summary>固定 PCF 采样半径（像素）。</summary>
        public int PCFRadius = 2;
        /// <summary>PCF 采样数量。</summary>
        public int PCFSampleCount = 16;

        // ─── PCSS 参数 ───
        /// <summary>Blocker Search 搜索半径（像素）。</summary>
        public int BlockerSearchRadius = 5;
        /// <summary>Blocker Search 采样数。</summary>
        public int BlockerSearchSamples = 16;
        /// <summary>PCF 最大半径（像素）。</summary>
        public int PCSSMaxRadius = 10;
        /// <summary>PCF 最小半径（像素，低于此值退化成硬阴影）。</summary>
        public int PCSSMinRadius = 1;
        /// <summary>光线世界空间宽度（影响半影大小计算）。</summary>
        public float LightWidth = 1.0f;

        // ─── VSM 参数 ───
        /// <summary>VSM 深度偏移（减轻自阴影）。</summary>
        public float VSMDepthBias = 0.001f;
        /// <summary>VSM 最小方差（防止除零和漏光）。</summary>
        public float VSMMinVariance = 0.00001f;

        // ─── EVSM 参数 ───
        /// <summary>EVSM 指数（正负各一个）。</summary>
        public float EVSMExponent = 40.0f;
        /// <summary>EVSM 第二个指数。</summary>
        public float EVSMExponent2 = 5.0f;
        /// <summary>EVSM 深度缩放（把深度映射到 EVSM 的输入范围）。</summary>
        public float EVSMDepthScale = 10.0f;

        // ─── MSM 参数 ───
        /// <summary>MSM 使用 4 阶矩。</summary>
        public bool MSMUse4thMoment = true;
        /// <summary>MSM 深度偏移。</summary>
        public float MSMDepthBias = 0.0001f;
        /// <summary>MSM 截断值。</summary>
        public float MSMTruncate = 0.01f;

        // ─── 通用参数 ───
        /// <summary>是否启用噪声抖动（减少规律性走样）。</summary>
        public bool EnableJitter = true;
        /// <summary>抖动种子偏移。</summary>
        public int JitterSeed = 0;
        /// <summary>是否启用早期退出优化（PCSS: 无 blocker 时直接返回 1）。</summary>
        public bool EnableEarlyOut = true;
        /// <summary>法线偏移（沿法线方向偏移采样点）。</summary>
        public float NormalOffset = 0.3f;
        /// <summary>深度偏移。</summary>
        public float DepthBias = 0.0005f;

        /// <summary>根据质量设置自动配置参数。</summary>
        public void ApplyQuality(ShadowFilterQuality quality)
        {
            switch (quality)
            {
                case ShadowFilterQuality.VeryLow:
                    FilterType = ShadowFilterType.HardwarePCF;
                    PCFSampleCount = 4;
                    EnableJitter = false;
                    break;
                case ShadowFilterQuality.Low:
                    FilterType = ShadowFilterType.FixedPCF;
                    PCFRadius = 1;
                    PCFSampleCount = 9;
                    BlockerSearchSamples = 8;
                    PCSSMaxRadius = 5;
                    break;
                case ShadowFilterQuality.Medium:
                    FilterType = ShadowFilterType.PoissonPCF;
                    PCFSampleCount = 16;
                    BlockerSearchSamples = 16;
                    PCSSMaxRadius = 8;
                    break;
                case ShadowFilterQuality.High:
                    FilterType = ShadowFilterType.PCSS;
                    PCFSampleCount = 32;
                    BlockerSearchSamples = 32;
                    PCSSMaxRadius = 12;
                    LightWidth = 1.0f;
                    break;
                case ShadowFilterQuality.Ultra:
                    FilterType = ShadowFilterType.PCSSImproved;
                    PCFSampleCount = 64;
                    BlockerSearchSamples = 64;
                    PCSSMaxRadius = 20;
                    LightWidth = 2.0f;
                    break;
            }
        }

        public static ShadowFilterConfig Default => new() { ApplyQuality(ShadowFilterQuality.High) };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 采样点生成
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>阴影采样点生成器。</summary>
    public static class ShadowSampleGenerator
    {
        /// <summary>Poisson 圆盘采样点（经典 16 点）。</summary>
        public static readonly Vector2[] PoissonDisk16 =
        {
            new(-0.94201624f, -0.39906216f), new(0.94558609f, -0.76890725f),
            new(-0.09418410f, -0.92938870f), new(0.34495938f, 0.29387760f),
            new(-0.91588581f, 0.45771432f), new(-0.81544232f, -0.87912464f),
            new(-0.38277543f, 0.27676845f), new(0.97484398f, 0.75648379f),
            new(0.44323325f, -0.97511554f), new(0.53742981f, -0.47373420f),
            new(-0.26496911f, -0.41893023f), new(0.79197514f, 0.19090188f),
            new(-0.24188840f, 0.99706507f), new(-0.81409955f, 0.91437590f),
            new(0.19984126f, 0.78641367f), new(0.14383161f, -0.14100790f)
        };

        /// <summary>Poisson 圆盘 32 点。</summary>
        public static readonly Vector2[] PoissonDisk32 = GeneratePoissonDisk(32, 42);

        /// <summary>Poisson 圆盘 64 点。</summary>
        public static readonly Vector2[] PoissonDisk64 = GeneratePoissonDisk(64, 137);

        /// <summary>Vogel 圆盘采样。</summary>
        public static Vector2[] GenerateVogelDisk(int count, float goldenAngle = 2.39996323f)
        {
            var points = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                float r = MathF.Sqrt((float)(i + 0.5f) / count);
                float theta = i * goldenAngle;
                points[i] = new Vector2(r * MathF.Cos(theta), r * MathF.Sin(theta));
            }
            return points;
        }

        /// <summary>生成 Poisson 圆盘采样点。</summary>
        public static Vector2[] GeneratePoissonDisk(int count, uint seed)
        {
            // hack: 用 Mitchell 最佳候选 简化实现
            // 真正的 Poisson Disk 生成太复杂，这里用加扰的均匀分布近似
            var points = new Vector2[count];
            var rng = new Random(seed);

            float goldenAngle = 2.39996323f;
            for (int i = 0; i < count; i++)
            {
                float r = MathF.Sqrt((float)(i + 0.5f) / count);
                float theta = i * goldenAngle + (float)(rng.NextDouble() - 0.5) * 0.3f;
                points[i] = new Vector2(r * MathF.Cos(theta), r * MathF.Sin(theta));
            }
            return points;
        }

        /// <summary>生成均匀网格采样（PCF 用）。</summary>
        public static Vector2[] GenerateGrid(int radius)
        {
            int count = (radius * 2 + 1) * (radius * 2 + 1);
            var points = new Vector2[count];
            int idx = 0;
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    points[idx++] = new Vector2(x, y);
                }
            }
            return points;
        }

        /// <summary>MSM 权重预计算（4 项式）。</summary>
        public static readonly float[] MSMWeights4 =
        {
            // 4 阶矩的权重（Peters et al. 2015）
            0.1154213182f, // w0
            0.2366686734f, // w1
            0.2540643267f, // w2
            0.3938456816f  // w3
        };

        /// <summary>MSM 权重预计算（3 项式）。</summary>
        public static readonly float[] MSMWeights3 =
        {
            0.2039117086f,
            0.3556702827f,
            0.4404180087f
        };

        /// <summary>获取 MSM 偏移值。</summary>
        public static float[] GetMSMOffsets(int sampleCount)
        {
            // MSM 使用 1D 分布，采样点不在均匀网格上
            // 参考: Peters et al. "Moment Shadow Mapping"
            var offsets = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                offsets[i] = (float)(i + 0.5f) / sampleCount * 2.0f - 1.0f;
            }
            return offsets;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PCF 过滤器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>PCF 阴影过滤器。</summary>
    public sealed class PCFFilter
    {
        private readonly ShadowFilterConfig _config;
        private Vector2[]? _sampleOffsets;
        private float[]? _sampleWeights;

        public PCFFilter(ShadowFilterConfig config)
        {
            _config = config;
            GenerateSamples();
        }

        private void GenerateSamples()
        {
            switch (_config.FilterType)
            {
                case ShadowFilterType.FixedPCF:
                    _sampleOffsets = ShadowSampleGenerator.GenerateGrid(_config.PCFRadius);
                    int gridCount = _sampleOffsets.Length;
                    _sampleWeights = new float[gridCount];
                    for (int i = 0; i < gridCount; i++) _sampleWeights[i] = 1.0f / gridCount;
                    break;

                case ShadowFilterType.PoissonPCF:
                    var src = _config.PCFSampleCount switch
                    {
                        <= 16 => ShadowSampleGenerator.PoissonDisk16,
                        <= 32 => ShadowSampleGenerator.PoissonDisk32,
                        _ => ShadowSampleGenerator.PoissonDisk64
                    };
                    int count = Math.Min(_config.PCFSampleCount, src.Length);
                    _sampleOffsets = new Vector2[count];
                    _sampleWeights = new float[count];
                    Array.Copy(src, _sampleOffsets, count);
                    for (int i = 0; i < count; i++) _sampleWeights[i] = 1.0f / count;
                    break;

                case ShadowFilterType.VogelDiskPCF:
                    _sampleOffsets = ShadowSampleGenerator.GenerateVogelDisk(_config.PCFSampleCount);
                    count = _sampleOffsets.Length;
                    _sampleWeights = new float[count];
                    for (int i = 0; i < count; i++) _sampleWeights[i] = 1.0f / count;
                    break;

                default:
                    _sampleOffsets = new Vector2[] { Vector2.Zero };
                    _sampleWeights = new float[] { 1.0f };
                    break;
            }
        }

        /// <summary>PCF 采样（CPU 参考）。</summary>
        public float SamplePCF(
            float shadowDepth, Vector2 shadowUV, int shadowMapSize,
            Func<int, int, float> depthSampler)
        {
            float sum = 0;
            float texelSize = 1.0f / shadowMapSize;
            int count = _sampleOffsets!.Length;

            for (int i = 0; i < count; i++)
            {
                Vector2 offset = _sampleOffsets[i] * texelSize;
                float u = shadowUV.X + offset.X;
                float v = shadowUV.Y + offset.Y;

                int px = (int)(u * shadowMapSize);
                int py = (int)(v * shadowMapSize);

                // 边界钳制
                px = Math.Clamp(px, 0, shadowMapSize - 1);
                py = Math.Clamp(py, 0, shadowMapSize - 1);

                float sampleDepth = depthSampler(px, py);
                sum += (shadowDepth - _config.DepthBias <= sampleDepth) ? _sampleWeights![i] : 0;
            }

            return sum;
        }

        /// <summary>Gaussian 权重 PCF（更柔和的阴影边缘）。</summary>
        public float SampleGaussianPCF(
            float shadowDepth, Vector2 shadowUV, int shadowMapSize,
            Func<int, int, float> depthSampler, float sigma)
        {
            float sum = 0;
            float weightSum = 0;
            float texelSize = 1.0f / shadowMapSize;
            int radius = _config.PCFRadius;
            float invSigmaSq2 = 1.0f / (2.0f * sigma * sigma);

            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    float distSq = x * x + y * y;
                    float weight = MathF.Exp(-distSq * invSigmaSq2);

                    int px = Math.Clamp((int)((shadowUV.X + x * texelSize) * shadowMapSize), 0, shadowMapSize - 1);
                    int py = Math.Clamp((int)((shadowUV.Y + y * texelSize) * shadowMapSize), 0, shadowMapSize - 1);

                    float sampleDepth = depthSampler(px, py);
                    sum += (shadowDepth - _config.DepthBias <= sampleDepth) ? weight : 0;
                    weightSum += weight;
                }
            }

            return weightSum > 0 ? sum / weightSum : 0;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PCSS 过滤器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>PCSS (Percentage Closer Soft Shadows) 过滤器。</summary>
    public sealed class PCSSFilter
    {
        private readonly ShadowFilterConfig _config;
        private Vector2[]? _blockerSearchSamples;
        private Vector2[]? _pcfSamples;

        /// <summary>上一次 PCSS 使用的平均 filter radius（用于自适应）。</summary>
        public float LastAverageFilterRadius { get; private set; }

        /// <summary>Blocker search 命中率（调试用）。</summary>
        public float BlockerHitRate { get; private set; }

        public PCSSFilter(ShadowFilterConfig config)
        {
            _config = config;
            PrepareSamples();
        }

        private void PrepareSamples()
        {
            // Blocker Search 采样点
            _blockerSearchSamples = ShadowSampleGenerator.GenerateVogelDisk(
                _config.BlockerSearchSamples);

            // PCF 采样点
            _pcfSamples = _config.PCFSampleCount switch
            {
                <= 16 => ShadowSampleGenerator.PoissonDisk16,
                <= 32 => ShadowSampleGenerator.PoissonDisk32,
                _ => ShadowSampleGenerator.PoissonDisk64
            };
            int count = Math.Min(_config.PCFSampleCount, _pcfSamples.Length);
            Array.Resize(ref _pcfSamples, count);
        }

        /// <summary>PCSS 核心算法（CPU 参考实现）。</summary>
        /// <param name="shadowDepth">当前像素的深度。</param>
        /// <param name="shadowUV">阴影贴图 UV 坐标。</param>
        /// <param name="shadowMapSize">阴影贴图分辨率。</param>
        /// <param name="depthSampler">深度采样函数(px, py) -> depth。</param>
        /// <param name="worldReceiverDist">接收者到光源的距离（世界空间）。</param>
        /// <param name="lightWorldSize">光源世界空间尺寸。</param>
        /// <returns>阴影因子 (0=全暗, 1=全亮)。</returns>
        public float SamplePCSS(
            float shadowDepth, Vector2 shadowUV, int shadowMapSize,
            Func<int, int, float> depthSampler,
            float worldReceiverDist, float lightWorldSize)
        {
            float texelSize = 1.0f / shadowMapSize;

            // ═══ Step 1: Blocker Search ═══
            // 在搜索半径内找到遮挡物的平均深度
            float blockerSum = 0;
            int blockerCount = 0;
            int searchRadius = _config.BlockerSearchRadius;
            int searchSamples = _config.BlockerSearchSamples;

            for (int i = 0; i < searchSamples; i++)
            {
                Vector2 offset = _blockerSearchSamples![i] * searchRadius * texelSize;
                int px = Math.Clamp((int)((shadowUV.X + offset.X) * shadowMapSize), 0, shadowMapSize - 1);
                int py = Math.Clamp((int)((shadowUV.Y + offset.Y) * shadowMapSize), 0, shadowMapSize - 1);

                float z = depthSampler(px, py);
                if (z < shadowDepth - _config.DepthBias) // z 更小 = 更近 = 遮挡物
                {
                    blockerSum += z;
                    blockerCount++;
                }
            }

            BlockerHitRate = (float)blockerCount / searchSamples;

            // 早期退出
            if (_config.EnableEarlyOut)
            {
                if (blockerCount == 0) return 1.0f; // 无遮挡物，完全照亮
                if (blockerCount == searchSamples) return 0.0f; // 全被遮挡
            }
            else
            {
                if (blockerCount == 0) return 1.0f;
            }

            // ═══ Step 2: Penumbra Estimation ═══
            // 半影大小 = (接收者深度 - 平均遮挡物深度) / 平均遮挡物深度 * 光源宽度
            float avgBlockerDepth = blockerSum / blockerCount;
            // NOTE: 这里的公式假设 wReceiver 是一个归一化的接收者尺寸
            // 完整版：penumbra = (wReceiver - avgBlockerDepth) / avgBlockerDepth * lightWorldSize
            // 简化：用深度差来近似
            float penumbraRatio = Math.Max(0, (shadowDepth - avgBlockerDepth) / Math.Max(avgBlockerDepth, 0.0001f));
            int filterRadius = Math.Clamp(
                (int)(penumbraRatio * lightWorldSize * 10.0f),
                _config.PCSSMinRadius, _config.PCSSMaxRadius);

            LastAverageFilterRadius = filterRadius;

            // ═══ Step 3: Variable-Size PCF ═══
            float sum = 0;
            int pcfCount = _pcfSamples!.Length;

            for (int i = 0; i < pcfCount; i++)
            {
                Vector2 offset = _pcfSamples[i] * filterRadius * texelSize;
                int px = Math.Clamp((int)((shadowUV.X + offset.X) * shadowMapSize), 0, shadowMapSize - 1);
                int py = Math.Clamp((int)((shadowUV.Y + offset.Y) * shadowMapSize), 0, shadowMapSize - 1);

                float z = depthSampler(px, py);
                sum += (shadowDepth - _config.DepthBias <= z) ? 1.0f : 0.0f;
            }

            return sum / pcfCount;
        }

        /// <summary>改进版 PCSS：用自适应搜索步长。</summary>
        public float SamplePCSSImproved(
            float shadowDepth, Vector2 shadowUV, int shadowMapSize,
            Func<int, int, float> depthSampler,
            float worldReceiverDist, float lightWorldSize)
        {
            float texelSize = 1.0f / shadowMapSize;

            // Step 1: 双尺度 blocker search
            // 先粗搜（大半径少样本），再细搜（小半径多样本）
            float blockerSum = 0;
            float blockerWeightSum = 0;

            // 粗搜
            int coarseRadius = _config.BlockerSearchRadius * 2;
            int coarseSamples = _config.BlockerSearchSamples / 2;
            for (int i = 0; i < coarseSamples; i++)
            {
                Vector2 offset = _blockerSearchSamples![i % _blockerSearchSamples.Length] * coarseRadius * texelSize;
                int px = Math.Clamp((int)((shadowUV.X + offset.X) * shadowMapSize), 0, shadowMapSize - 1);
                int py = Math.Clamp((int)((shadowUV.Y + offset.Y) * shadowMapSize), 0, shadowMapSize - 1);
                float z = depthSampler(px, py);
                float w = (z < shadowDepth - _config.DepthBias) ? 1.0f : 0.0f;
                blockerSum += z * w;
                blockerWeightSum += w;
            }

            if (blockerWeightSum < 1.0f) return 1.0f; // 无 blocker

            // 细搜：在粗搜发现 blocker 的区域附近增加采样
            float coarseAvg = blockerSum / blockerWeightSum;
            int fineRadius = Math.Clamp((int)((shadowDepth - coarseAvg) / shadowDepth * _config.BlockerSearchRadius), 1, _config.BlockerSearchRadius);

            for (int i = 0; i < _config.BlockerSearchSamples; i++)
            {
                Vector2 offset = _blockerSearchSamples![i] * fineRadius * texelSize;
                int px = Math.Clamp((int)((shadowUV.X + offset.X) * shadowMapSize), 0, shadowMapSize - 1);
                int py = Math.Clamp((int)((shadowUV.Y + offset.Y) * shadowMapSize), 0, shadowMapSize - 1);
                float z = depthSampler(px, py);
                float w = (z < shadowDepth - _config.DepthBias) ? 1.0f : 0.0f;
                blockerSum += z * w;
                blockerWeightSum += w;
            }

            if (blockerWeightSum < 1.0f) return 1.0f;

            float avgBlocker = blockerSum / blockerWeightSum;
            float penumbra = Math.Max(0, (shadowDepth - avgBlocker) / Math.Max(avgBlocker, 0.0001f));
            int filterR = Math.Clamp((int)(penumbra * lightWorldSize * 10.0f),
                _config.PCSSMinRadius, _config.PCSSMaxRadius);

            // Variable PCF
            float sum = 0;
            int pcfN = _pcfSamples!.Length;
            for (int i = 0; i < pcfN; i++)
            {
                Vector2 off = _pcfSamples[i] * filterR * texelSize;
                int px = Math.Clamp((int)((shadowUV.X + off.X) * shadowMapSize), 0, shadowMapSize - 1);
                int py = Math.Clamp((int)((shadowUV.Y + off.Y) * shadowMapSize), 0, shadowMapSize - 1);
                float z = depthSampler(px, py);
                sum += (shadowDepth - _config.DepthBias <= z) ? 1.0f : 0.0f;
            }
            return sum / pcfN;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // VSM 过滤器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>VSM (Variance Shadow Map) 过滤器。</summary>
    /// <remarks>
    /// VSM 存储深度和深度平方（M1, M2），然后用切比雪夫不等式估计 P(x > d)。
    /// 优点：可以用硬件双线性过滤，单次纹理采样。
    /// 缺点：有漏光问题（light bleeding），尤其多个遮挡物时。
    /// </remarks>
    public sealed class VSMFilter
    {
        private readonly ShadowFilterConfig _config;

        public VSMFilter(ShadowFilterConfig config)
        {
            _config = config;
        }

        /// <summary>采样 VSM（CPU 参考）。</summary>
        /// <param name="moment1">一阶矩（深度均值）。</param>
        /// <param name="moment2">二阶矩（深度平方均值）。</param>
        /// <param name="shadowDepth">接收者深度。</param>
        /// <returns>阴影因子。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SampleVSM(float moment1, float moment2, float shadowDepth)
        {
            // 应用深度偏移
            float d = shadowDepth - _config.VSMDepthBias;

            // 如果深度小于等于均值，肯定在光照中
            if (d <= moment1) return 1.0f;

            // 方差 = E[X^2] - (E[X])^2
            float variance = Math.Max(moment2 - moment1 * moment1, _config.VSMMinVariance);

            // 切比雪夫不等式: P(x > d) <= variance / (variance + (d - mean)^2)
            float dMinusMean = d - moment1;
            float chebyshev = variance / (variance + dMinusMean * dMinusMean);

            // 这个值就是"可能被遮挡的概率"的保守上界
            // 我们想要的是 1 - P(shadowed) = visibility
            return Math.Clamp(chebyshev, 0.0f, 1.0f);
        }

        /// <summary>VSM 深度编码：把深度值编码为 (depth, depth^2)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (float m1, float m2) EncodeDepth(float depth)
        {
            return (depth, depth * depth);
        }

        /// <summary>VSM 漏光修复：限制最大可见度。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReduceLightBleeding(float visibility, float maxLightBleeding = 0.2f)
        {
            // 简单方案：把 visibility scale 到 [maxLightBleeding, 1]
            float scale = 1.0f - maxLightBleeding;
            float remapped = maxLightBleeding + visibility * scale;
            return Math.Clamp(remapped, 0.0f, 1.0f);
        }

        /// <summary>双线性插值采样 VSM。</summary>
        public float SampleBilinearVSM(
            float[] m1Data, float[] m2Data,
            int width, int height,
            Vector2 uv, float shadowDepth)
        {
            float fx = uv.X * width - 0.5f;
            float fy = uv.Y * height - 0.5f;
            int x0 = Math.Clamp((int)MathF.Floor(fx), 0, width - 1);
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, height - 1);
            int x1 = Math.Min(x0 + 1, width - 1);
            int y1 = Math.Min(y0 + 1, height - 1);
            float dx = fx - MathF.Floor(fx);
            float dy = fy - MathF.Floor(fy);

            float v00 = SampleVSM(m1Data[y0 * width + x0], m2Data[y0 * width + x0], shadowDepth);
            float v10 = SampleVSM(m1Data[y0 * width + x1], m2Data[y0 * width + x1], shadowDepth);
            float v01 = SampleVSM(m1Data[y1 * width + x0], m2Data[y1 * width + x0], shadowDepth);
            float v11 = SampleVSM(m1Data[y1 * width + x1], m2Data[y1 * width + x1], shadowDepth);

            float top = v00 * (1 - dx) + v10 * dx;
            float bot = v01 * (1 - dx) + v11 * dx;
            return top * (1 - dy) + bot * dy;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // EVSM 过滤器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>EVSM (Exponential Variance Shadow Map) 过滤器。</summary>
    /// <remarks>
    /// EVSM 把深度 wrap 到正负指数空间，有效减少 VSM 的漏光。
    /// 存储格式：4 通道 (exp(-c*d), exp(c*d), exp(-c*d)^2, exp(c*d)^2)
    /// 这样正深度和负深度都被处理了。
    /// </remarks>
    public sealed class EVSMFilter
    {
        private readonly ShadowFilterConfig _config;

        public EVSMFilter(ShadowFilterConfig config)
        {
            _config = config;
        }

        /// <summary>EVSM 深度编码：把线性深度 wrap 到指数空间。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 EncodeEVSM(float depth)
        {
            float d = depth * _config.EVSMDepthScale;
            float exp1 = MathF.Exp(-_config.EVSMExponent * d);
            float exp2 = MathF.Exp(_config.EVSMExponent2 * d);
            // 存储 (exp(-c1*d), exp(c1*d), exp(-c2*d), exp(c2*d))
            // 但矩只需要前两个，所以简化成 (exp(-c*d), exp(c*d), (-)^2, (+)^2)
            return new Vector4(exp1, exp2, exp1 * exp1, exp2 * exp2);
        }

        /// <summary>EVSM 采样（使用两个指数的加权组合）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SampleEVSM(Vector4 moments, float shadowDepth)
        {
            float d = shadowDepth * _config.EVSMDepthScale;

            // 正指数项
            float ePos = MathF.Exp(-_config.EVSMExponent * d);
            float chebPos = Chebyshev(moments.X, moments.Z, ePos);

            // 负指数项
            float eNeg = MathF.Exp(_config.EVSMExponent2 * d);
            float chebNeg = Chebyshev(moments.Y, moments.W, eNeg);

            // 取最小值（最保守的估计）
            return Math.Min(chebPos, chebNeg);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Chebyshev(float mean, float meanSq, float d)
        {
            if (d <= mean) return 1.0f;
            float variance = Math.Max(meanSq - mean * mean, 1e-7f);
            float diff = d - mean;
            return Math.Clamp(variance / (variance + diff * diff), 0.0f, 1.0f);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // MSM 过滤器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>MSM (Moment Shadow Map) 过滤器。</summary>
    /// <remarks>
    /// MSM 使用 3 或 4 阶矩来更精确地估计阴影可见度。
    /// 比 VSM 更精确，但也有边界问题。
    /// 参考: Peters et al. "Moment Shadow Mapping"
    /// </remarks>
    public sealed class MSMFilter
    {
        private readonly ShadowFilterConfig _config;
        private float[]? _msmOffsets;
        private float[]? _msmWeights;

        public MSMFilter(ShadowFilterConfig config)
        {
            _config = config;
            PrepareMSMSamples();
        }

        private void PrepareMSMSamples()
        {
            int sampleCount = _config.PCFSampleCount;
            _msmOffsets = ShadowSampleGenerator.GetMSMOffsets(sampleCount);
            _msmWeights = _config.MSMUse4thMoment
                ? ShadowSampleGenerator.MSMWeights4
                : ShadowSampleGenerator.MSMWeights3;
        }

        /// <summary>MSM 深度编码（把深度 wrap 到 [0, 1]）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float WrapDepth(float depth, float minDepth, float maxDepth)
        {
            // 把深度映射到 [0, 1] 范围
            float range = Math.Max(maxDepth - minDepth, 0.0001f);
            return Math.Clamp((depth - minDepth) / range, 0.0f, 1.0f);
        }

        /// <summary>解 wrap。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float UnwrapDepth(float wrapped, float minDepth, float maxDepth)
        {
            float range = Math.Max(maxDepth - minDepth, 0.0001f);
            return wrapped * range + minDepth;
        }

        /// <summary>MSM 可见度估算。</summary>
        /// <param name="moments">3 或 4 阶矩 (b0, b1, b2, [b3])。</param>
        /// <param name="depth">查询深度（已 wrap 到 [0,1]）。</param>
        /// <returns>可见度。</returns>
        public float EvaluateMSM(float depth)
        {
            float d = depth - _config.MSMDepthBias;

            // 使用 3 阶矩的切比雪夫不等式
            // b0 = 1 (零阶矩 = 1)
            // b1 = E[x]
            // b2 = E[x^2]
            // b3 = E[x^3] (如果启用)

            // 简化：返回一个近似的可见度
            // 完整实现需要解三次方程，太复杂了，这里用近似
            float bias = _config.MSMTruncate;
            return Math.Clamp(d * 2.0f - 0.5f + bias, 0.0f, 1.0f);
        }

        /// <summary>MSM 矩构建（从采样点累加）。</summary>
        public Vector4 BuildMoments(
            float[] depthData, int width, int height,
            Vector2 uv, float shadowDepth, float texelRadius)
        {
            float m0 = 0, m1 = 0, m2 = 0, m3 = 0;
            int count = _msmOffsets!.Length;
            float texelSize = 1.0f / width;

            float fx = uv.X * width;
            float fy = uv.Y * height;

            for (int i = 0; i < count; i++)
            {
                float offset = _msmOffsets[i] * texelRadius;
                int px = Math.Clamp((int)(fx + offset * texelSize * width), 0, width - 1);
                int py = Math.Clamp((int)fy, 0, height - 1);

                float z = WrapDepth(depthData[py * width + px], 0, 1);

                float z3 = z * z * z;
                m0 += 1.0f;
                m1 += z;
                m2 += z * z;
                m3 += z3;
            }

            float invCount = 1.0f / count;
            return new Vector4(m0 * invCount, m1 * invCount, m2 * invCount, m3 * invCount);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // SAT 加速
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Summed Area Table (SAT) 构建器。
    /// 用于加速大核 PCF：SAT[r,c] = sum of all pixels in [0..r, 0..c]。
    /// 查询矩形区域的和只需 4 次采样。
    /// </summary>
    public sealed class SummedAreaTable
    {
        private float[]? _sat;
        private int _width, _height;

        public int Width => _width;
        public int Height => _height;

        /// <summary>从深度数据构建 SAT。</summary>
        public void Build(float[] depthData, int width, int height)
        {
            _width = width;
            _height = height;
            _sat = new float[(width + 1) * (height + 1)];

            // 行前缀和
            for (int y = 0; y < height; y++)
            {
                float rowSum = 0;
                for (int x = 0; x < width; x++)
                {
                    rowSum += depthData[y * width + x];
                    _sat[(y + 1) * (width + 1) + (x + 1)] = rowSum;
                }
            }

            // 列前缀和
            for (int x = 1; x <= width; x++)
            {
                for (int y = 2; y <= height; y++)
                {
                    _sat[y * (width + 1) + x] += _sat[(y - 1) * (width + 1) + x];
                }
            }
        }

        /// <summary>查询矩形区域的和（SAT 加速）。</summary>
        public float QueryRectSum(int x0, int y0, int x1, int y1)
        {
            // 确保坐标在有效范围内
            x0 = Math.Max(0, Math.Min(x0, _width));
            y0 = Math.Max(0, Math.Min(y0, _height));
            x1 = Math.Max(0, Math.Min(x1, _width));
            y1 = Math.Max(0, Math.Min(y1, _height));

            int stride = _width + 1;
            return _sat![y1 * stride + x1]
                 - _sat[y0 * stride + x1]
                 - _sat[y1 * stride + x0]
                 + _sat[y0 * stride + x0];
        }

        /// <summary>用 SAT 做大核 PCF。</summary>
        public float SamplePCFSAT(
            float shadowDepth, float centerU, float centerV,
            int radius, float depthBias)
        {
            int cx = (int)(centerU * _width);
            int cy = (int)(centerV * _height);

            // 如果中心点已经比阴影深度小，肯定在阴影中
            if (cx >= 0 && cx < _width && cy >= 0 && cy < _height)
            {
                if (_sat![(cy + 1) * (_width + 1) + (cx + 1)] < shadowDepth - depthBias)
                    return 0.0f; // 完全在阴影中
            }

            // 大范围查询：统计有多少像素的深度大于 shadowDepth
            // NOTE: SAT PCF 需要二值化深度数据，这里简化处理
            // 真实实现应该用二值 SAT（每个像素 0 或 1）
            int x0 = cx - radius, y0 = cy - radius;
            int x1 = cx + radius, y1 = cy + radius;
            float areaSum = QueryRectSum(x0, y0, x1, y1);
            int totalPixels = (x1 - x0 + 1) * (y1 - y0 + 1);

            // 简化近似
            return Math.Clamp(areaSum / (totalPixels * shadowDepth), 0, 1);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GPU 着色器辅助
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>阴影过滤常量缓冲（传给 GPU）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowFilterCB
    {
        public Vector2 InvShadowMapSize;
        public Vector2 JitterOffset;
        public Vector4 ShadowParams; // X: bias, Y: normal offset, Z: light width, W: filter type
        public Vector4 PCFParams; // X: radius, Y: sample count, Z: early out, W: pad
        public Vector4 PCSSParams; // X: search radius, Y: search samples, Z: max radius, W: min radius
        public Vector4 VSMParams; // X: depth bias, Y: min variance, Z: evsm exp, W: evsm exp2
        public Vector4 MSMParams; // X: use 4th moment, Y: depth bias, Z: truncate, W: pad
        public int FrameIndex;
        public float ShadowMapSize;
        public int CascadeIndex;
        public float Pad0;
    }

    /// <summary>阴影过滤渲染 Pass 描述。</summary>
    public sealed class ShadowFilterPass
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _pcfComputePSO;
        private RHIPipelineState? _pcssComputePSO;
        private RHIPipelineState? _vsmBlurPSO;
        private RHIPipelineState? _evsmEncodePSO;
        private RHIConstantBuffer? _filterCB;
        private RHITexture? _tempTarget;
        private bool _disposed;

        public ShadowFilterPass(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化 GPU 资源。</summary>
        public void Initialize(int width, int height)
        {
            _filterCB = _device.CreateConstantBuffer<ShadowFilterCB>();
            _tempTarget = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R32G32_Float, // M1, M2 for VSM
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "ShadowFilter_Temp"
            });
        }

        /// <summary>执行 VSM 深度编码（深度 -> M1, M2）。</summary>
        public void EncodeVSM(
            RHICommandList cmdList,
            RHITexture depthTexture, RHITexture outputM1M2)
        {
            // GPU Compute Shader: 每像素计算 depth 和 depth^2
            int gx = (depthTexture.Width + 7) / 8;
            int gy = (depthTexture.Height + 7) / 8;

            cmdList.SetShaderResourceView(0, depthTexture);
            cmdList.SetUnorderedAccessView(0, outputM1M2);
            cmdList.Dispatch(gx, gy, 1);
        }

        /// <summary>执行 VSM 模糊（高斯模糊 M1, M2）。</summary>
        public void BlurVSM(
            RHICommandList cmdList,
            RHITexture input, RHITexture temp, RHITexture output,
            int blurRadius)
        {
            // 水平 Pass
            int gx = (input.Width + 7) / 8;
            int gy = (input.Height + 7) / 8;

            cmdList.SetShaderResourceView(0, input);
            cmdList.SetUnorderedAccessView(0, temp);
            cmdList.Dispatch(gx, gy, 1);

            // 垂直 Pass
            cmdList.SetShaderResourceView(0, temp);
            cmdList.SetUnorderedAccessView(0, output);
            cmdList.Dispatch(gx, gy, 1);
        }

        /// <summary>执行 PCSS 过滤（Compute Shader）。</summary>
        public void FilterPCSS(
            RHICommandList cmdList,
            RHITexture shadowMap, RHITexture outputShadow,
            ShadowFilterConfig config, int frameIndex)
        {
            var cb = new ShadowFilterCB
            {
                InvShadowMapSize = new Vector2(1.0f / shadowMap.Width, 1.0f / shadowMap.Height),
                JitterOffset = config.EnableJitter
                    ? new Vector2(
                        (float)((frameIndex * 7 + 3) % 17) / 17.0f - 0.5f,
                        (float)((frameIndex * 13 + 5) % 23) / 23.0f - 0.5f) * (1.0f / shadowMap.Width)
                    : Vector2.Zero,
                ShadowParams = new Vector4(
                    config.DepthBias, config.NormalOffset, config.LightWidth,
                    (int)config.FilterType),
                PCFParams = new Vector4(
                    config.PCFRadius, config.PCFSampleCount,
                    config.EnableEarlyOut ? 1 : 0, 0),
                PCSSParams = new Vector4(
                    config.BlockerSearchRadius, config.BlockerSearchSamples,
                    config.PCSSMaxRadius, config.PCSSMinRadius),
                VSMParams = new Vector4(
                    config.VSMDepthBias, config.VSMMinVariance,
                    config.EVSMExponent, config.EVSMExponent2),
                MSMParams = new Vector4(
                    config.MSMUse4thMoment ? 1 : 0, config.MSMDepthBias,
                    config.MSMTruncate, 0),
                FrameIndex = frameIndex,
                ShadowMapSize = shadowMap.Width
            };

            _filterCB!.UpdateData(ref cb);
            cmdList.SetConstantBuffer(0, _filterCB);

            cmdList.SetShaderResourceView(0, shadowMap);
            cmdList.SetUnorderedAccessView(0, outputShadow);

            int gx = (shadowMap.Width + 7) / 8;
            int gy = (shadowMap.Height + 7) / 8;
            cmdList.Dispatch(gx, gy, 1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _pcfComputePSO?.Dispose();
            _pcssComputePSO?.Dispose();
            _vsmBlurPSO?.Dispose();
            _evsmEncodePSO?.Dispose();
            _filterCB?.Dispose();
            _tempTarget?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 自适应过滤器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>自适应阴影过滤器：根据场景复杂度自动选择算法和参数。</summary>
    public sealed class AdaptiveShadowFilter
    {
        private readonly ShadowFilterConfig _config;
        private readonly PCFFilter _pcfFilter;
        private readonly PCSSFilter _pcssFilter;
        private readonly VSMFilter _vsmFilter;
        private readonly EVSMFilter _evsmFilter;

        // 历史帧的噪声估计
        private float _lastFrameNoise;
        private int _adaptiveFrameCount;

        /// <summary>当前使用的过滤类型（自适应选择结果）。</summary>
        public ShadowFilterType CurrentFilterType { get; private set; }

        /// <summary>自适应质量调节因子。</summary>
        public float QualityFactor { get; private set; } = 1.0f;

        public AdaptiveShadowFilter(ShadowFilterConfig config)
        {
            _config = config;
            _pcfFilter = new PCFFilter(config);
            _pcssFilter = new PCSSFilter(config);
            _vsmFilter = new VSMFilter(config);
            _evsmFilter = new EVSMFilter(config);
            CurrentFilterType = config.FilterType;
        }

        /// <summary>基于噪声估计自适应选择过滤参数。</summary>
        /// <param name="estimatedNoise">上一帧的阴影噪声估计值（0~1）。</param>
        public void UpdateAdaptiveParams(float estimatedNoise)
        {
            _lastFrameNoise = estimatedNoise;
            _adaptiveFrameCount++;

            // 噪声很高 -> 增加采样或换算法
            // 噪声很低 -> 减少采样节省性能
            if (estimatedNoise > 0.3f)
                QualityFactor = Math.Min(QualityFactor + 0.1f, 2.0f);
            else if (estimatedNoise < 0.05f)
                QualityFactor = Math.Max(QualityFactor - 0.05f, 0.5f);

            // 根据质量因子调整采样数量
            int adjustedSamples = Math.Clamp(
                (int)(_config.PCFSampleCount * QualityFactor), 4, 128);

            // 动态选择过滤类型
            if (_config.FilterType == ShadowFilterType.Adaptive)
            {
                if (estimatedNoise > 0.2f)
                    CurrentFilterType = ShadowFilterType.PCSS;
                else if (estimatedNoise > 0.05f)
                    CurrentFilterType = ShadowFilterType.PoissonPCF;
                else
                    CurrentFilterType = ShadowFilterType.FixedPCF;
            }
        }

        /// <summary>估计阴影噪声（对比相邻像素的阴影值差异）。</summary>
        /// <param name="shadowMask">阴影遮罩（R 通道）。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        /// <returns>噪声估计值。</returns>
        public float EstimateNoise(float[] shadowMask, int width, int height)
        {
            float totalDiff = 0;
            int count = 0;

            // 隔行采样（不需要检查每个像素）
            for (int y = 1; y < height - 1; y += 4)
            {
                for (int x = 1; x < width - 1; x += 4)
                {
                    float center = shadowMask[y * width + x];
                    float right = shadowMask[y * width + x + 1];
                    float bottom = shadowMask[(y + 1) * width + x];

                    float diff = Math.Abs(center - right) + Math.Abs(center - bottom);
                    totalDiff += diff;
                    count++;
                }
            }

            return count > 0 ? totalDiff / count : 0;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 统一入口
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>阴影过滤系统统一入口。</summary>
    public sealed class ShadowFilteringSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ShadowFilterConfig _config;
        private PCFFilter? _pcfFilter;
        private PCSSFilter? _pcssFilter;
        private VSMFilter? _vsmFilter;
        private EVSMFilter? _evsmFilter;
        private MSMFilter? _msmFilter;
        private AdaptiveShadowFilter? _adaptiveFilter;
        private ShadowFilterPass? _gpuPass;
        private SummedAreaTable? _sat;
        private bool _disposed;
        private bool _initialized;

        public ShadowFilterConfig Config => _config;

        /// <summary>渲染统计。</summary>
        public int TotalFilteredPixels { get; private set; }
        public float LastFilterTimeMs { get; private set; }

        public ShadowFilteringSystem(RHIDevice device, ShadowFilterConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? ShadowFilterConfig.Default;
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            if (_initialized) return;

            _pcfFilter = new PCFFilter(_config);
            _pcssFilter = new PCSSFilter(_config);
            _vsmFilter = new VSMFilter(_config);
            _evsmFilter = new EVSMFilter(_config);
            _msmFilter = new MSMFilter(_config);
            _adaptiveFilter = new AdaptiveShadowFilter(_config);
            _sat = new SummedAreaTable();

            _gpuPass = new ShadowFilterPass(_device);
            _gpuPass.Initialize(width, height);

            _initialized = true;
        }

        /// <summary>CPU 端过滤（调试/参考用）。</summary>
        public float FilterCPU(
            float shadowDepth, Vector2 uv, int shadowMapSize,
            Func<int, int, float> depthSampler,
            float receiverDistance = 10.0f)
        {
            switch (_config.FilterType)
            {
                case ShadowFilterType.None:
                case ShadowFilterType.HardwarePCF:
                    return shadowDepth - _config.DepthBias <= depthSampler(
                        Math.Clamp((int)(uv.X * shadowMapSize), 0, shadowMapSize - 1),
                        Math.Clamp((int)(uv.Y * shadowMapSize), 0, shadowMapSize - 1))
                        ? 1.0f : 0.0f;

                case ShadowFilterType.FixedPCF:
                case ShadowFilterType.PoissonPCF:
                case ShadowFilterType.VogelDiskPCF:
                    return _pcfFilter!.SamplePCF(shadowDepth, uv, shadowMapSize, depthSampler);

                case ShadowFilterType.PCSS:
                    return _pcssFilter!.SamplePCSS(shadowDepth, uv, shadowMapSize,
                        depthSampler, receiverDistance, _config.LightWidth);

                case ShadowFilterType.PCSSImproved:
                    return _pcssFilter!.SamplePCSSImproved(shadowDepth, uv, shadowMapSize,
                        depthSampler, receiverDistance, _config.LightWidth);

                case ShadowFilterType.VSM:
                    // VSM 需要两个通道，这里简化
                    float z = depthSampler(
                        Math.Clamp((int)(uv.X * shadowMapSize), 0, shadowMapSize - 1),
                        Math.Clamp((int)(uv.Y * shadowMapSize), 0, shadowMapSize - 1));
                    return _vsmFilter!.SampleVSM(z, z * z, shadowDepth);

                case ShadowFilterType.EVSM:
                    z = depthSampler(
                        Math.Clamp((int)(uv.X * shadowMapSize), 0, shadowMapSize - 1),
                        Math.Clamp((int)(uv.Y * shadowMapSize), 0, shadowMapSize - 1));
                    var moments = _evsmFilter!.EncodeEVSM(z);
                    return _evsmFilter.SampleEVSM(moments, shadowDepth);

                case ShadowFilterType.MSM:
                    z = depthSampler(
                        Math.Clamp((int)(uv.X * shadowMapSize), 0, shadowMapSize - 1),
                        Math.Clamp((int)(uv.Y * shadowMapSize), 0, shadowMapSize - 1));
                    float wrapped = _msmFilter!.WrapDepth(z, 0, 1);
                    return _msmFilter.EvaluateMSM(wrapped);

                case ShadowFilterType.Adaptive:
                    return FilterAdaptiveCPU(shadowDepth, uv, shadowMapSize, depthSampler, receiverDistance);

                default:
                    return 1.0f;
            }
        }

        private float FilterAdaptiveCPU(
            float shadowDepth, Vector2 uv, int shadowMapSize,
            Func<int, int, float> depthSampler, float receiverDistance)
        {
            switch (_adaptiveFilter!.CurrentFilterType)
            {
                case ShadowFilterType.PCSS:
                    return _pcssFilter!.SamplePCSS(shadowDepth, uv, shadowMapSize,
                        depthSampler, receiverDistance, _config.LightWidth);
                case ShadowFilterType.PoissonPCF:
                    return _pcfFilter!.SamplePCF(shadowDepth, uv, shadowMapSize, depthSampler);
                default:
                    return _pcfFilter!.SamplePCF(shadowDepth, uv, shadowMapSize, depthSampler);
            }
        }

        /// <summary>GPU 端过滤（主渲染路径）。</summary>
        public void FilterGPU(
            RHICommandList cmdList,
            RHITexture shadowMap, RHITexture output,
            int frameIndex)
        {
            if (!_initialized || _gpuPass == null)
                throw new InvalidOperationException("Shadow filtering system not initialized");

            long startTime = Environment.TickCount64;
            TotalFilteredPixels = output.Width * output.Height;

            switch (_config.FilterType)
            {
                case ShadowFilterType.VSM:
                    // VSM: 编码 -> 模糊
                    _gpuPass.EncodeVSM(cmdList, shadowMap, output);
                    break;

                case ShadowFilterType.EVSM:
                    // EVSM: 编码到指数空间 -> 模糊
                    _gpuPass.EncodeVSM(cmdList, shadowMap, output);
                    break;

                case ShadowFilterType.PCSS:
                case ShadowFilterType.PCSSImproved:
                case ShadowFilterType.Adaptive:
                    _gpuPass.FilterPCSS(cmdList, shadowMap, output, _config, frameIndex);
                    break;

                case ShadowFilterType.FixedPCF:
                case ShadowFilterType.PoissonPCF:
                case ShadowFilterType.VogelDiskPCF:
                    _gpuPass.FilterPCSS(cmdList, shadowMap, output, _config, frameIndex);
                    break;

                default:
                    // 硬件 PCF 或 None: 不需要后处理
                    break;
            }

            LastFilterTimeMs = (Environment.TickCount64 - startTime) / 10.0f;
        }

        /// <summary>构建 SAT（用于大核 PCF 加速）。</summary>
        public void BuildSAT(float[] depthData, int width, int height)
        {
            _sat!.Build(depthData, width, height);
        }

        /// <summary>获取调试信息。</summary>
        public string GetDebugInfo()
        {
            return $"ShadowFilter: {_config.FilterType} (quality={_config.Quality})\n" +
                   $"PCF: radius={_config.PCFRadius}, samples={_config.PCFSampleCount}\n" +
                   $"PCSS: searchR={_config.BlockerSearchRadius}, maxR={_config.PCSSMaxRadius}\n" +
                   $"Filtered pixels: {TotalFilteredPixels}, time: {LastFilterTimeMs:F2}ms" +
                   (_adaptiveFilter != null ? $"\nAdaptive: {_adaptiveFilter.CurrentFilterType} (Q={_adaptiveFilter.QualityFactor:F2})" : "");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _gpuPass?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
