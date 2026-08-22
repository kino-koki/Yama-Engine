// ════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 高级阴影管线系统 (AdvancedShadowPipeline)
//
// 实现 UE5 级别的高级阴影管线, 包含:
//   1. 级联阴影图 (CSM) 增强:
//      - 自适应级联分割: 基于场景深度分布
//      - 深度修复: liSPSM (Light-Space Perspective Shadow Map) 方向光
//      - 深度偏差自动调整
//   2. 软阴影:
//      - PCSS (Percentage-Closer Soft Shadows): 搜索 + 插值
//      - VSM (Variance Shadow Map): 切比雪夫不等式
//      - EVSM2 (Exponential VSM): 指数映射解决漏光
//      - MSM (Moment Shadow Map): 4 阶矩更精确
//   3. 点光源阴影:
//      - Cubemap 阴影 (6 面)
//      - 双抛物面阴影 (Dual Paraboloid): 优化选项
//      - 阴影数组层
//   4. 阴影过滤:
//      - 空间降噪: 深度感知滤波
//      - 时序累积: 多帧抖动累积
//      - 阴影接触硬化 (Contact Hardening)
//   5. 阴影 LOD: 根据距离切换 CSM → 点光 → 禁用
//
// 对接系统: RHI (RHIDevice/RHICommandList) / CascadeShadowMapRenderer / 场景渲染器
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    // ────────────────────────────────────────────────────────────────────────
    // 配置与枚举
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>高级阴影质量档位。</summary>
    public enum AdvancedShadowQuality { Low, Medium, High, Ultra, Cinematic }

    /// <summary>阴影 LOD 模式。</summary>
    public enum ShadowLODMode
    {
        /// <summary>使用 CSM (方向光近距离)。</summary>
        CSM,
        /// <summary>使用点光源阴影 (中距离)。</summary>
        PointLight,
        /// <summary>阴影禁用 (远距离)。</summary>
        Disabled
    }

    /// <summary>点光源阴影模式。</summary>
    public enum PointLightShadowMode
    {
        /// <summary>立方体贴图 (6 面, 精确)。</summary>
        Cubemap,
        /// <summary>双抛物面 (2 面, 优化)。</summary>
        DualParaboloid
    }

    /// <summary>高级阴影配置。</summary>
    public sealed class AdvancedShadowConfig
    {
        public AdvancedShadowQuality Quality = AdvancedShadowQuality.High;

        // --- CSM ---
        public int CascadeCount = 4;
        public int ShadowMapSize = 2048;
        public float ShadowDistance = 200.0f;
        public float LogSplitWeight = 0.8f;
        public bool EnableLiSPSM = true;
        public float LiSPSMWarpFactor = 0.8f;
        public bool EnableAutoBias = true;
        public float BaseDepthBias = 0.0005f;
        public float SlopeScaledBias = 1.5f;
        public float NormalOffset = 0.5f;
        public bool EnableTexelSnapping = true;

        // --- 软阴影 ---
        public ShadowFilterMethod SoftShadowMethod = ShadowFilterMethod.PCSS;
        public float PCSSSearchRadius = 8.0f;
        public float PCSSPenumbraScale = 1.0f;
        public float VSMLightBleedingReduction = 0.2f;
        public float EVSMPositiveExponent = 40.0f;
        public float EVSMNegativeExponent = 5.0f;
        public int MSMMomentCount = 4;

        // --- 点光源阴影 ---
        public PointLightShadowMode PointLightMode = PointLightShadowMode.Cubemap;
        public int PointLightShadowSize = 1024;
        public float PointLightShadowDistance = 50.0f;

        // --- 阴影过滤 ---
        public bool EnableSpatialFilter = true;
        public int SpatialFilterRadius = 2;
        public bool EnableTemporalAccumulation = true;
        public float TemporalAlpha = 0.9f;
        public bool EnableContactHardening = true;
        public float ContactHardeningDistance = 5.0f;

        // --- 阴影 LOD ---
        public float CSMDisableDistance = 150.0f;
        public float PointLightDisableDistance = 40.0f;
        public bool EnableShadowLOD = true;

        public void ApplyQualityPreset(AdvancedShadowQuality q)
        {
            Quality = q;
            switch (q)
            {
                case AdvancedShadowQuality.Low:
                    CascadeCount = 2; ShadowMapSize = 512;
                    SoftShadowMethod = ShadowFilterMethod.HardwarePCF;
                    EnableSpatialFilter = false; EnableTemporalAccumulation = false; break;
                case AdvancedShadowQuality.Medium:
                    CascadeCount = 3; ShadowMapSize = 1024;
                    SoftShadowMethod = ShadowFilterMethod.PCF5x5;
                    EnableSpatialFilter = true; EnableTemporalAccumulation = false; break;
                case AdvancedShadowQuality.High:
                    CascadeCount = 4; ShadowMapSize = 2048;
                    SoftShadowMethod = ShadowFilterMethod.PCSS;
                    EnableSpatialFilter = true; EnableTemporalAccumulation = true; break;
                case AdvancedShadowQuality.Ultra:
                    CascadeCount = 4; ShadowMapSize = 4096;
                    SoftShadowMethod = ShadowFilterMethod.EVSM2;
                    EnableSpatialFilter = true; EnableTemporalAccumulation = true; break;
                case AdvancedShadowQuality.Cinematic:
                    CascadeCount = 8; ShadowMapSize = 4096;
                    SoftShadowMethod = ShadowFilterMethod.MSM;
                    EnableSpatialFilter = true; EnableTemporalAccumulation = true; break;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 自适应级联分割
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 自适应级联分割: 基于场景深度分布自动计算级联边界,
    /// 结合对数划分 (远处精度好) 和线性划分 (近处精度好)。
    /// </summary>
    public static class AdaptiveCascadeSplitter
    {
        /// <summary>
        /// 计算自适应级联分割。
        /// </summary>
        /// <param name="cascadeCount">级联数量。</param>
        /// <param name="nearClip">相机近裁剪面。</param>
        /// <param name="shadowDistance">阴影最远距离。</param>
        /// <param name="logWeight">对数权重 (0=纯线性, 1=纯对数)。</param>
        /// <param name="depthHistogram">场景深度直方图 (可选, 用于自适应)。</param>
        /// <returns>每级级联的 [near, far] 边界。</returns>
        public static (float near, float far)[] ComputeSplits(
            int cascadeCount, float nearClip, float shadowDistance,
            float logWeight, float[]? depthHistogram = null)
        {
            var splits = new (float near, float far)[cascadeCount];
            float range = shadowDistance - nearClip;

            // 基础 Practical 分割 (Log + Linear 混合)
            for (int i = 0; i < cascadeCount; i++)
            {
                float p = (float)(i + 1) / cascadeCount;
                float logSplit = nearClip * MathF.Pow(shadowDistance / nearClip, p);
                float linearSplit = nearClip + range * p;
                float split = logWeight * logSplit + (1 - logWeight) * linearSplit;
                splits[i] = (i == 0 ? nearClip : splits[i - 1].far, split);
            }

            // 如果有深度直方图, 进行自适应调整
            if (depthHistogram != null && depthHistogram.Length > 0)
            {
                AdjustSplitsWithHistogram(splits, depthHistogram, nearClip, shadowDistance);
            }

            return splits;
        }

        /// <summary>基于深度直方图自适应调整级联边界 (在物体密集区域加密)。</summary>
        private static void AdjustSplitsWithHistogram(
            (float near, float far)[] splits, float[] histogram,
            float nearClip, float shadowDistance)
        {
            float range = shadowDistance - nearClip;
            int bins = histogram.Length;
            float[] cumulative = new float[bins + 1];
            for (int i = 0; i < bins; i++)
                cumulative[i + 1] = cumulative[i] + histogram[i];
            float total = cumulative[bins];
            if (total < 1e-6f) return;

            // 根据累积分布调整每级边界
            for (int i = 0; i < splits.Length; i++)
            {
                float targetPercentile = (float)(i + 1) / splits.Length;
                float target = targetPercentile * total;

                // 二分搜索累积分布
                int lo = 0, hi = bins;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (cumulative[mid] < target) lo = mid + 1;
                    else hi = mid;
                }

                float depth = nearClip + (float)lo / bins * range;
                // 平滑混合 (避免抖动)
                splits[i].far = splits[i].far * 0.8f + depth * 0.2f;
                if (i > 0) splits[i - 1].far = Math.Min(splits[i - 1].far, splits[i].far);
            }
        }

        /// <summary>计算 Texel Snapping 偏移 (稳定投影)。</summary>
        public static Vector3 ComputeTexelSnapOffset(in Vector3 lightPos, in Matrix4x4 lightView,
            float shadowMapSize, float texelWorldSize)
        {
            // 将光源位置变换到光空间
            Vector4 lightInView = Vector4.Transform(new Vector4(lightPos, 1), lightView);
            Vector3 snapped = new(lightInView.X, lightInView.Y, lightInView.Z);

            // 对齐到 texel 网格
            float texelSize = texelWorldSize;
            snapped.X = MathF.Floor(snapped.X / texelSize) * texelSize;
            snapped.Y = MathF.Floor(snapped.Y / texelSize) * texelSize;

            // 转回世界空间偏移
            Matrix4x4.Invert(lightView, out var invLightView);
            Vector4 snappedWorld = Vector4.Transform(new Vector4(snapped, 1), invLightView);
            return new Vector3(snappedWorld.X, snappedWorld.Y, snappedWorld.Z) - lightPos;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // liSPSM (Light-Space Perspective Shadow Map)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// liSPSM: 在光空间应用透视变换, 使近处物体获得更高的阴影贴图精度。
    /// 通过 warp 矩阵将深度分布不均匀问题转化为透视投影下的均匀分布。
    /// </summary>
    public static class LiSPSMCalculator
    {
        /// <summary>
        /// 构建 liSPSM 矩阵: 在标准光空间正交投影基础上叠加透视 warp。
        /// </summary>
        public static Matrix4x4 ComputeLiSPSMMatrix(
            in Matrix4x4 lightViewMatrix, in Matrix4x4 lightOrthoMatrix,
            in Vector3 cameraPos, in Vector3 cameraForward,
            float nearDist, float farDist, float warpFactor)
        {
            // 1. 计算光空间下的相机视锥体 AABB
            Vector3[] frustumCorners = ComputeFrustumCorners(cameraPos, cameraForward, nearDist, farDist);
            Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
            foreach (var corner in frustumCorners)
            {
                Vector4 lc = Vector4.Transform(new Vector4(corner, 1), lightViewMatrix);
                bmin = Vector3.Min(bmin, new Vector3(lc.X, lc.Y, lc.Z));
                bmax = Vector3.Max(bmax, new Vector3(lc.X, lc.Y, lc.Z));
            }

            // 2. 计算相机在光空间的位置与方向
            Vector4 camInLight = Vector4.Transform(new Vector4(cameraPos, 1), lightViewMatrix);
            Vector3 camPosLight = new(camInLight.X, camInLight.Y, camInLight.Z);
            Vector4 camFwdLight = Vector4.Transform(new Vector4(cameraForward, 0), lightViewMatrix);
            Vector3 camDirLight = Vector3.Normalize(new Vector3(camFwdLight.X, camFwdLight.Y, camFwdLight.Z));

            // 3. 计算 warp 参数 (近处拉伸, 远处压缩)
            float zRange = bmax.Z - bmin.Z;
            if (zRange < 1e-6f) return lightOrthoMatrix;

            float nopt = ComputeNopt(bmin.Z, bmax.Z, camPosLight, camDirLight, warpFactor);
            nopt = Math.Clamp(nopt, bmin.Z + 0.1f, bmax.Z - 0.1f);

            // 4. 构建透视 warp 矩阵
            float f = nopt + zRange;
            float n = nopt;
            float warpScale = f / (f - n);

            Matrix4x4 warpMatrix = new(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, warpScale, 1,
                0, 0, -n * warpScale, 0
            );

            // 5. 合成: LightView → Warp → LightOrtho
            return lightViewMatrix * warpMatrix * lightOrthoMatrix;
        }

        /// <summary>计算最优近平面距离 nopt。</summary>
        private static float ComputeNopt(float zNear, float zFar,
            in Vector3 camPos, in Vector3 camDir, float warpFactor)
        {
            // nopt = d * sin(theta) / (1 + sin(theta)) * warpFactor
            float d = zFar - zNear;
            float sinTheta = MathF.Abs(camDir.Z);
            float denom = 1 + sinTheta;
            if (denom < 1e-6f) return d * 0.5f;
            return d * sinTheta / denom * warpFactor + zNear;
        }

        /// <summary>计算视锥体 8 个角点。</summary>
        private static Vector3[] ComputeFrustumCorners(
            in Vector3 cameraPos, in Vector3 cameraForward,
            float nearDist, float farDist)
        {
            Vector3 right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
            Vector3 up = Vector3.Cross(right, cameraForward);
            float aspect = 1.778f;
            float fov = MathF.PI / 3;

            float nearH = MathF.Tan(fov * 0.5f) * nearDist;
            float nearW = nearH * aspect;
            float farH = MathF.Tan(fov * 0.5f) * farDist;
            float farW = farH * aspect;

            Vector3 nc = cameraPos + cameraForward * nearDist;
            Vector3 fc = cameraPos + cameraForward * farDist;

            return new Vector3[]
            {
                nc - right * nearW - up * nearH,
                nc + right * nearW - up * nearH,
                nc + right * nearW + up * nearH,
                nc - right * nearW + up * nearH,
                fc - right * farW - up * farH,
                fc + right * farW - up * farH,
                fc + right * farW + up * farH,
                fc - right * farW + up * farH
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 软阴影算法
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 软阴影算法实现: PCSS, VSM, EVSM2, MSM。
    /// 提供 CPU 侧参考实现, GPU 着色器使用相同公式。
    /// </summary>
    public static class SoftShadowAlgorithms
    {
        // --- PCSS (Percentage-Closer Soft Shadows) ---

        /// <summary>
        /// PCSS 三步算法:
        /// 1. Blocker Search: 在阴影图中搜索平均 blocker 深度
        /// 2. Penumbra Estimation: 根据 blocker 距离估算半影大小
        /// 3. PCF Filter: 用估算的半影大小做 PCF 过滤
        /// </summary>
        public static float PCSS(in float[,] shadowMap, int smSize,
            float depth, Vector2 uv, float searchRadius, float penumbraScale,
            float lightSize, float bias)
        {
            // Step 1: Blocker Search
            float blockerDepth = 0;
            int blockerCount = 0;
            int searchR = (int)MathF.Ceiling(searchRadius);

            for (int dy = -searchR; dy <= searchR; dy++)
            for (int dx = -searchR; dx <= searchR; dx++)
            {
                int sx = Math.Clamp((int)(uv.X * smSize) + dx, 0, smSize - 1);
                int sy = Math.Clamp((int)(uv.Y * smSize) + dy, 0, smSize - 1);
                float sampleDepth = shadowMap[sy, sx];
                if (sampleDepth < depth - bias)
                {
                    blockerDepth += sampleDepth;
                    blockerCount++;
                }
            }

            if (blockerCount == 0) return 1.0f; // 无 blocker, 无阴影
            if (blockerCount == (2 * searchR + 1) * (2 * searchR + 1)) return 0.0f; // 完全遮挡

            float avgBlockerDepth = blockerDepth / blockerCount;

            // Step 2: Penumbra Estimation
            // penumbra = lightSize * (receiverDepth - blockerDepth) / blockerDepth
            float penumbraRatio = (depth - avgBlockerDepth) / avgBlockerDepth;
            float penumbraSize = MathF.Max(1.0f, lightSize * penumbraRatio * penumbraScale);
            int filterRadius = (int)MathF.Ceiling(penumbraSize);

            // Step 3: PCF Filter
            return PCFFilter(in shadowMap, smSize, depth, uv, filterRadius, bias);
        }

        /// <summary>PCF (Percentage-Closer Filtering)。</summary>
        public static float PCFFilter(in float[,] shadowMap, int smSize,
            float depth, Vector2 uv, int radius, float bias)
        {
            float sum = 0;
            int count = 0;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int sx = Math.Clamp((int)(uv.X * smSize) + dx, 0, smSize - 1);
                int sy = Math.Clamp((int)(uv.Y * smSize) + dy, 0, smSize - 1);
                sum += shadowMap[sy, sx] < depth - bias ? 0.0f : 1.0f;
                count++;
            }
            return sum / count;
        }

        // --- VSM (Variance Shadow Map) ---

        /// <summary>VSM: 使用切比雪夫不等式计算上界。</summary>
        public static float VSM(float mean, float variance, float depth, float lightBleedingReduction)
        {
            if (depth <= mean) return 1.0f;

            float variancePlus = variance;
            float diff = depth - mean;
            float pMax = variancePlus / (variancePlus + diff * diff);

            // Light bleeding reduction
            pMax = MathF.Max(pMax, 0.0f);
            pMax = MathF.Pow(pMax, lightBleedingReduction);

            return Math.Clamp(pMax, 0.0f, 1.0f);
        }

        // --- EVSM2 (Exponential Variance Shadow Map) ---

        /// <summary>EVSM2: 指数映射解决漏光, 双通道 (正/负指数)。</summary>
        public static float EVSM2(float depth, float shadowDepth,
            float positiveExp, float negativeExp,
            float posMean, float posVariance,
            float negMean, float negVariance,
            float lightBleedingReduction)
        {
            // 正指数通道
            float expDepthPos = MathF.Exp(depth * positiveExp);
            float expShadowPos = MathF.Exp(shadowDepth * positiveExp);
            float visibilityPos = VSM(posMean, posVariance, expDepthPos, lightBleedingReduction);

            // 负指数通道
            float expDepthNeg = -MathF.Exp(-depth * negativeExp);
            float expShadowNeg = -MathF.Exp(-shadowDepth * negativeExp);
            float visibilityNeg = VSM(negMean, negVariance, expDepthNeg, lightBleedingReduction);

            // 取两通道最小值 (更保守)
            return MathF.Min(visibilityPos, visibilityNeg);
        }

        /// <summary>计算指数映射后的均值和方差。</summary>
        public static (float mean, float variance) ComputeExponentialMoments(
            in float[,] shadowMap, int smSize, Vector2 uv, int radius,
            float exponent, bool negative)
        {
            float sum = 0, sumSq = 0;
            int count = 0;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int sx = Math.Clamp((int)(uv.X * smSize) + dx, 0, smSize - 1);
                int sy = Math.Clamp((int)(uv.Y * smSize) + dy, 0, smSize - 1);
                float d = shadowMap[sy, sx];
                float mapped = negative ? -MathF.Exp(-d * exponent) : MathF.Exp(d * exponent);
                sum += mapped;
                sumSq += mapped * mapped;
                count++;
            }
            float mean = sum / count;
            float variance = MathF.Max(0, sumSq / count - mean * mean);
            return (mean, variance);
        }

        // --- MSM (Moment Shadow Map) ---

        /// <summary>MSM: 4 阶矩阴影图, 更精确的可见性近似。</summary>
        public static float MSM4(float depth, float m0, float m1, float m2, float m3,
            float lightBleedingReduction)
        {
            if (depth <= m1) return 1.0f;

            // 归一化矩
            float invM0 = m0 > 1e-6f ? 1.0f / m0 : 0;
            float b1 = m1 * invM0;
            float b2 = m2 * invM0;
            float b3 = m3 * invM0;

            // 归一化深度
            float z = depth * invM0;
            if (z <= b1) return 1.0f;

            // 构建三对角矩阵求解特征值
            // 4 阶矩算法 (Peters & Klein 2015)
            float[] b = { 1, b1, b2, b3 };

            // 计算偏置
            float bias = 1e-7f * b3;
            b[2] -= bias;
            b[3] -= 2 * bias * b[1];

            // 求解 3x3 线性方程组
            float[] c = SolveTridiagonal(b);
            if (c == null) return 0.0f;

            // 特征多项式系数
            float c0 = c[0], c1 = c[1], c2 = c[2];
            float d0 = c1 * c1 - c0 * c2;
            float d1 = 2 * c1 * c2 - b[3] * c0;
            float d2 = c2 * c2 - b[3] * c1;

            if (MathF.Abs(d2) < 1e-10f) return 0.0f;

            float za = (-d1 - MathF.Sqrt(MathF.Max(0, d1 * d1 - 4 * d0 * d2))) / (2 * d2);
            float zb = c0 / (za * c2);
            float zc = -c1 / c2 - za;

            // 排序特征值
            if (za > zb) (za, zb) = (zb, za);
            if (za > zc) (za, zc) = (zc, za);
            if (zb > zc) (zb, zc) = (zc, zb);

            // 计算可见性
            float visibility;
            if (z <= za)
                visibility = 1.0f;
            else if (z <= zb)
                visibility = 1.0f - (z - za) * (zc - zb) / ((zb - za) * (zc - za));
            else if (z <= zc)
                visibility = (zc - z) * (zb - za) / ((zc - zb) * (zc - za));
            else
                visibility = 0.0f;

            // Light bleeding reduction
            visibility = Math.Clamp(visibility, 0.0f, 1.0f);
            return MathF.Pow(visibility, lightBleedingReduction);
        }

        private static float[]? SolveTridiagonal(float[] b)
        {
            // 简化的三对角方程求解
            float denom = b[1] * b[1] - b[0] * b[2];
            if (MathF.Abs(denom) < 1e-10f) return null;

            float c1 = (b[2] * b[2] - b[1] * b[3]) / denom;
            float c2 = (b[1] * b[2] - b[0] * b[3]) / denom;
            float c0 = 1.0f - c1 - c2;
            return new float[] { c0, c1, c2 };
        }

        // --- 接触硬化 (Contact Hardening) ---

        /// <summary>接触硬化: 接触表面附近阴影更锐利。</summary>
        public static float ContactHardening(float baseShadow, float distance,
            float contactDistance, float hardness)
        {
            float proximity = MathF.Exp(-distance / contactDistance);
            float sharpened = MathF.Pow(baseShadow, 1.0f + hardness * proximity);
            return sharpened;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 点光源阴影
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 点光源阴影渲染: 支持 Cubemap (6 面) 和双抛物面 (2 面) 两种模式。
    /// </summary>
    public sealed class PointLightShadowRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly AdvancedShadowConfig _config;
        private RHITexture? _cubemapDepth;
        private RHITexture? _dualParaboloidDepth;
        private RHIBuffer? _shadowMatricesCB;

        /// <summary>Cubemap 深度纹理。</summary>
        public RHITexture? CubemapDepth => _cubemapDepth;
        /// <summary>双抛物面深度纹理。</summary>
        public RHITexture? DualParaboloidDepth => _dualParaboloidDepth;

        /// <summary>点光源阴影常量。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PointLightShadowConstants
        {
            public Matrix4x4 FaceViewMatrix0;
            public Matrix4x4 FaceViewMatrix1;
            public Matrix4x4 FaceViewMatrix2;
            public Matrix4x4 FaceViewMatrix3;
            public Matrix4x4 FaceViewMatrix4;
            public Matrix4x4 FaceViewMatrix5;
            public Vector3 LightPos;
            public float FarPlane;
            public float ShadowMapSize;
            float _pad0, _pad1, _pad2;
        }

        public PointLightShadowRenderer(RHIDevice device, AdvancedShadowConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化资源。</summary>
        public void Initialize()
        {
            int size = _config.PointLightShadowSize;

            if (_config.PointLightMode == PointLightShadowMode.Cubemap)
            {
                _cubemapDepth = _device.CreateTexture(new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D,
                    Format = RHIFormat.D32_Float,
                    Width = size, Height = size, DepthOrArraySize = 6,
                    MipLevels = 1, SampleCount = 1,
                    Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
                }, "PointLightCubemapDepth");
            }
            else
            {
                _dualParaboloidDepth = _device.CreateTexture(new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D,
                    Format = RHIFormat.D32_Float,
                    Width = size, Height = size, DepthOrArraySize = 2,
                    MipLevels = 1, SampleCount = 1,
                    Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
                }, "PointLightDualParaboloidDepth");
            }

            _shadowMatricesCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 1024,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            }, "PointLightShadowCB");
        }

        /// <summary>计算 Cubemap 6 面的 ViewMatrix。</summary>
        public static Matrix4x4[] ComputeCubemapViewMatrices(in Vector3 lightPos)
        {
            return new Matrix4x4[]
            {
                Matrix4x4.CreateLookAt(lightPos, lightPos + Vector3.UnitX, -Vector3.UnitY),  // +X
                Matrix4x4.CreateLookAt(lightPos, lightPos - Vector3.UnitX, -Vector3.UnitY),  // -X
                Matrix4x4.CreateLookAt(lightPos, lightPos + Vector3.UnitY, Vector3.UnitZ),   // +Y
                Matrix4x4.CreateLookAt(lightPos, lightPos - Vector3.UnitY, -Vector3.UnitZ),  // -Y
                Matrix4x4.CreateLookAt(lightPos, lightPos + Vector3.UnitZ, -Vector3.UnitY),  // +Z
                Matrix4x4.CreateLookAt(lightPos, lightPos - Vector3.UnitZ, -Vector3.UnitY)   // -Z
            };
        }

        /// <summary>双抛物面映射: 将方向向量编码到 2D UV + 面索引。</summary>
        public static (Vector2 uv, int face) DualParaboloidEncode(in Vector3 dir)
        {
            Vector3 d = Vector3.Normalize(dir);
            if (d.Z >= 0)
            {
                // 前面
                float denom = 1 + d.Z;
                return (new Vector2(d.X / denom, d.Y / denom) * 0.5f + 0.5f, 0);
            }
            else
            {
                // 后面
                float denom = 1 - d.Z;
                return (new Vector2(-d.X / denom, d.Y / denom) * 0.5f + 0.5f, 1);
            }
        }

        /// <summary>双抛物面解码: 从 UV + 面索引重建方向。</summary>
        public static Vector3 DualParaboloidDecode(Vector2 uv, int face)
        {
            Vector2 p = uv * 2 - 1;
            float lenSq = p.X * p.X + p.Y * p.Y;
            if (face == 0)
            {
                // 前面: z = (1 - x² - y²) / 2
                float z = (1 - lenSq) / 2;
                return Vector3.Normalize(new Vector3(p.X, p.Y, z));
            }
            else
            {
                // 后面: z = -(1 - x² - y²) / 2
                float z = -(1 - lenSq) / 2;
                return Vector3.Normalize(new Vector3(-p.X, p.Y, z));
            }
        }

        /// <summary>查询 Cubemap 阴影可见性 (CPU 参考)。</summary>
        public static float SampleCubemapShadow(in float[,,] shadowDepth, int size,
            in Vector3 lightPos, in Vector3 worldPos, float farPlane, float bias)
        {
            Vector3 dir = worldPos - lightPos;
            float distance = dir.Length();
            dir /= distance;

            // 采样对应面的深度
            Vector3 absDir = new(MathF.Abs(dir.X), MathF.Abs(dir.Y), MathF.Abs(dir.Z));
            int face;
            Vector2 uv;
            float maxComp = MathF.Max(absDir.X, MathF.Max(absDir.Y, absDir.Z));

            if (maxComp == absDir.X)
            {
                face = dir.X > 0 ? 0 : 1;
                uv = new Vector2(-dir.Z / absDir.X, -dir.Y / absDir.X) * 0.5f + 0.5f;
            }
            else if (maxComp == absDir.Y)
            {
                face = dir.Y > 0 ? 2 : 3;
                uv = new Vector2(dir.X / absDir.Y, face == 2 ? dir.Z / absDir.Y : -dir.Z / absDir.Y) * 0.5f + 0.5f;
            }
            else
            {
                face = dir.Z > 0 ? 4 : 5;
                uv = new Vector2(dir.X / absDir.Z, -dir.Y / absDir.Z) * 0.5f + 0.5f;
            }

            int sx = Math.Clamp((int)(uv.X * size), 0, size - 1);
            int sy = Math.Clamp((int)(uv.Y * size), 0, size - 1);
            float storedDepth = shadowDepth[face, sy, sx] * farPlane;
            return storedDepth + bias > distance ? 1.0f : 0.0f;
        }

        public void Dispose()
        {
            _cubemapDepth?.Dispose();
            _dualParaboloidDepth?.Dispose();
            _shadowMatricesCB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 阴影过滤
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 阴影过滤: 空间降噪 (深度感知), 时序累积 (多帧抖动), 接触硬化。
    /// </summary>
    public sealed class ShadowFilterEnhanced : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly AdvancedShadowConfig _config;
        private RHITexture? _filteredShadow;
        private RHITexture? _temporalShadowA;
        private RHITexture? _temporalShadowB;
        private bool _useTemporalA;
        private int _width, _height;

        public ShadowFilterEnhanced(RHIDevice device, AdvancedShadowConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化资源。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;

            _filteredShadow = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "FilteredShadow");

            _temporalShadowA = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TemporalShadowA");

            _temporalShadowB = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TemporalShadowB");
        }

        /// <summary>空间降噪: 深度感知双边滤波。</summary>
        public void SpatialFilter(RHICommandList cmdList, RHITexture inputShadow,
            RHITexture depthBuffer, RHITexture normalBuffer)
        {
            if (!_config.EnableSpatialFilter) return;

            cmdList.TransitionTo(inputShadow, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_filteredShadow, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(depthBuffer, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(normalBuffer, RHIResourceState.NonPixelShaderResource);

            int groupsX = (_width + 7) / 8;
            int groupsY = (_height + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... radius, sigmaDepth, sigmaNormal ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(_filteredShadow, RHIResourceState.ShaderResource);
        }

        /// <summary>时序累积: 多帧抖动累积。</summary>
        public void TemporalAccumulate(RHICommandList cmdList, RHITexture currentShadow,
            RHITexture motionVectors, RHITexture depthBuffer, uint frameIndex)
        {
            if (!_config.EnableTemporalAccumulation) return;

            var history = _useTemporalA ? _temporalShadowA : _temporalShadowB;
            var output = _useTemporalA ? _temporalShadowB : _temporalShadowA;

            cmdList.TransitionTo(currentShadow, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(history, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            int groupsX = (_width + 7) / 8;
            int groupsY = (_height + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... temporalAlpha, frameIndex ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
            _useTemporalA = !_useTemporalA;
        }

        /// <summary>空间降噪双边权重 (CPU 参考)。</summary>
        public static float SpatialBilateralWeight(float depthDiff, float normalDiff,
            float shadowDiff, float sigmaDepth, float sigmaNormal, float sigmaShadow)
        {
            float wd = MathF.Exp(-(depthDiff * depthDiff) / (2 * sigmaDepth * sigmaDepth));
            float wn = MathF.Pow(MathF.Max(0, 1 - normalDiff), 8);
            float ws = MathF.Exp(-(shadowDiff * shadowDiff) / (2 * sigmaShadow * sigmaShadow));
            return wd * wn * ws;
        }

        public void Dispose()
        {
            _filteredShadow?.Dispose();
            _temporalShadowA?.Dispose();
            _temporalShadowB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 阴影 LOD 系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 阴影 LOD: 根据距离切换 CSM → 点光阴影 → 禁用。
    /// 近距离使用高精度 CSM, 中距离使用点光源阴影, 远距离禁用阴影。
    /// </summary>
    public sealed class ShadowLODSystem
    {
        private readonly AdvancedShadowConfig _config;

        public ShadowLODSystem(AdvancedShadowConfig config)
        {
            _config = config;
        }

        /// <summary>根据距离决定阴影 LOD 模式。</summary>
        public ShadowLODMode DetermineLOD(float distance, bool hasDirectionalLight, bool hasPointLight)
        {
            if (!_config.EnableShadowLOD)
            {
                if (hasDirectionalLight) return ShadowLODMode.CSM;
                if (hasPointLight) return ShadowLODMode.PointLight;
                return ShadowLODMode.Disabled;
            }

            if (distance < _config.PointLightDisableDistance && hasPointLight)
                return ShadowLODMode.PointLight;

            if (distance < _config.CSMDisableDistance && hasDirectionalLight)
                return ShadowLODMode.CSM;

            return ShadowLODMode.Disabled;
        }

        /// <summary>计算阴影淡化因子。</summary>
        public float ComputeShadowFade(float distance, float fadeStart, float fadeEnd)
        {
            if (distance < fadeStart) return 1.0f;
            if (distance > fadeEnd) return 0.0f;
            return 1.0f - (distance - fadeStart) / (fadeEnd - fadeStart);
        }

        /// <summary>计算 LOD 间的平滑过渡权重。</summary>
        public float ComputeLODBlendWeight(float distance, float transitionStart, float transitionEnd)
        {
            if (distance < transitionStart) return 0.0f;
            if (distance > transitionEnd) return 1.0f;
            return (distance - transitionStart) / (transitionEnd - transitionStart);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统: 高级阴影管线
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 高级阴影管线主系统。组合 CSM 增强、软阴影、点光源阴影、阴影过滤和阴影 LOD,
    /// 提供完整的实时阴影解决方案。
    /// </summary>
    public sealed class AdvancedShadowPipeline : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly AdvancedShadowConfig _config;
        private readonly PointLightShadowRenderer _pointLightRenderer;
        private readonly ShadowFilterEnhanced _shadowFilter;
        private readonly ShadowLODSystem _lodSystem;

        // CSM 阴影贴图数组
        private RHITexture? _csmDepthArray;
        private RHIBuffer? _csmMatricesCB;
        private Matrix4x4[] _cascadeViewProj;
        private (float near, float far)[] _cascadeSplits;
        private int _shadowMapSize;
        private bool _initialized;

        /// <summary>高级阴影常量缓冲。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct AdvancedShadowConstants
        {
            public Matrix4x4 CascadeViewProj0;
            public Matrix4x4 CascadeViewProj1;
            public Matrix4x4 CascadeViewProj2;
            public Matrix4x4 CascadeViewProj3;
            public Matrix4x4 CascadeViewProj4;
            public Matrix4x4 CascadeViewProj5;
            public Matrix4x4 CascadeViewProj6;
            public Matrix4x4 CascadeViewProj7;
            public Vector4 CascadeSplits; // 最多 8 级, 每级一个 float (far)
            public Vector4 CascadeSplits2;
            public float ShadowMapSize;
            public float ShadowDistance;
            public int CascadeCount;
            public float DepthBias;
            public float SlopeScaledBias;
            public float NormalOffset;
            public int SoftShadowMethod;
            public float PCSSSearchRadius;
            public float PCSSPenumbraScale;
            public float VSMLightBleedingReduction;
            public float EVSMPositiveExponent;
            public float EVSMNegativeExponent;
            public int EnableContactHardening;
            public float ContactHardeningDistance;
            public int EnableTemporalAccumulation;
            public float TemporalAlpha;
            public uint FrameIndex;
            float _pad0, _pad1;
        }

        /// <summary>配置。</summary>
        public AdvancedShadowConfig Config => _config;
        /// <summary>点光源阴影渲染器。</summary>
        public PointLightShadowRenderer PointLightRenderer => _pointLightRenderer;
        /// <summary>阴影过滤器。</summary>
        public ShadowFilterEnhanced ShadowFilter => _shadowFilter;
        /// <summary>阴影 LOD 系统。</summary>
        public ShadowLODSystem LODSystem => _lodSystem;
        /// <summary>CSM 深度数组。</summary>
        public RHITexture? CSMDepthArray => _csmDepthArray;
        /// <summary>级联分割。</summary>
        public (float near, float far)[] CascadeSplits => _cascadeSplits;

        public AdvancedShadowPipeline(RHIDevice device, AdvancedShadowConfig? config = null)
        {
            _device = device;
            _config = config ?? new AdvancedShadowConfig();
            _pointLightRenderer = new PointLightShadowRenderer(device, _config);
            _shadowFilter = new ShadowFilterEnhanced(device, _config);
            _lodSystem = new ShadowLODSystem(_config);
            _cascadeViewProj = new Matrix4x4[_config.CascadeCount];
            _cascadeSplits = new (float near, float far)[_config.CascadeCount];
            _shadowMapSize = _config.ShadowMapSize;
        }

        /// <summary>初始化系统。</summary>
        public void Initialize(int renderWidth, int renderHeight)
        {
            _shadowMapSize = _config.ShadowMapSize;

            // CSM 深度数组
            _csmDepthArray = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = _shadowMapSize, Height = _shadowMapSize,
                DepthOrArraySize = _config.CascadeCount,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
            }, "AdvancedCSMDepthArray");

            _pointLightRenderer.Initialize();
            _shadowFilter.Initialize(renderWidth, renderHeight);

            _csmMatricesCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 2048,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            }, "AdvancedShadowCB");

            _initialized = true;
        }

        /// <summary>更新级联分割和矩阵。</summary>
        public void UpdateCascades(in CameraData camera, float[]? depthHistogram = null)
        {
            // 自适应级联分割
            _cascadeSplits = AdaptiveCascadeSplitter.ComputeSplits(
                _config.CascadeCount, camera.NearClip, _config.ShadowDistance,
                _config.LogSplitWeight, depthHistogram);

            // 计算每级级联的 ViewProj 矩阵
            Vector3 lightDir = Vector3.Normalize(camera.LightDirection != Vector3.Zero
                ? camera.LightDirection : new Vector3(-0.5f, -0.8f, -0.3f));

            for (int c = 0; c < _config.CascadeCount; c++)
            {
                // 计算级联视锥体角点
                var corners = ComputeCascadeFrustumCorners(
                    camera, _cascadeSplits[c].near, _cascadeSplits[c].far);

                // 计算光空间正交投影
                var (lightView, lightOrtho) = ComputeLightViewProj(corners, lightDir);

                // liSPSM 增强 (可选)
                if (_config.EnableLiSPSM)
                {
                    _cascadeViewProj[c] = LiSPSMCalculator.ComputeLiSPSMMatrix(
                        lightView, lightOrtho, camera.Position, camera.Forward,
                        _cascadeSplits[c].near, _cascadeSplits[c].far, _config.LiSPSMWarpFactor);
                }
                else
                {
                    _cascadeViewProj[c] = lightView * lightOrtho;
                }

                // Texel Snapping
                if (_config.EnableTexelSnapping)
                {
                    float texelWorldSize = ComputeTexelWorldSize(corners, _shadowMapSize);
                    Vector3 snapOffset = AdaptiveCascadeSplitter.ComputeTexelSnapOffset(
                        camera.Position, lightView, _shadowMapSize, texelWorldSize);
                    // 应用偏移到光视图
                }
            }
        }

        /// <summary>计算级联视锥体角点。</summary>
        private Vector3[] ComputeCascadeFrustumCorners(in CameraData camera, float nearDist, float farDist)
        {
            Vector3 right = Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up));
            Vector3 up = Vector3.Cross(right, camera.Forward);
            float fov = camera.FieldOfView;
            float aspect = camera.AspectRatio;

            float nearH = MathF.Tan(fov * 0.5f) * nearDist;
            float nearW = nearH * aspect;
            float farH = MathF.Tan(fov * 0.5f) * farDist;
            float farW = farH * aspect;

            Vector3 nc = camera.Position + camera.Forward * nearDist;
            Vector3 fc = camera.Position + camera.Forward * farDist;

            return new Vector3[]
            {
                nc - right * nearW - up * nearH,
                nc + right * nearW - up * nearH,
                nc + right * nearW + up * nearH,
                nc - right * nearW + up * nearH,
                fc - right * farW - up * farH,
                fc + right * farW - up * farH,
                fc + right * farW + up * farH,
                fc - right * farW + up * farH
            };
        }

        /// <summary>计算光空间 View + Ortho 矩阵。</summary>
        private (Matrix4x4 view, Matrix4x4 ortho) ComputeLightViewProj(Vector3[] frustumCorners, Vector3 lightDir)
        {
            // 光源视图矩阵
            Vector3 lightPos = Vector3.Zero; // 临时, 后面用 frustum 中心
            Vector3 frustumCenter = Vector3.Zero;
            foreach (var c in frustumCorners) frustumCenter += c;
            frustumCenter /= frustumCorners.Length;
            lightPos = frustumCenter;

            Vector3 lightUp = MathF.Abs(lightDir.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            Matrix4x4 lightView = Matrix4x4.CreateLookAt(lightPos, lightPos + lightDir, lightUp);

            // 计算 AABB
            Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
            foreach (var corner in frustumCorners)
            {
                Vector4 lc = Vector4.Transform(new Vector4(corner, 1), lightView);
                bmin = Vector3.Min(bmin, new Vector3(lc.X, lc.Y, lc.Z));
                bmax = Vector3.Max(bmax, new Vector3(lc.X, lc.Y, lc.Z));
            }

            float padding = 5.0f;
            Matrix4x4 lightOrtho = Matrix4x4.CreateOrthographicOffCenter(
                bmin.X - padding, bmax.X + padding,
                bmin.Y - padding, bmax.Y + padding,
                -bmax.Z - 50, -bmin.Z + 50);

            return (lightView, lightOrtho);
        }

        private float ComputeTexelWorldSize(Vector3[] frustumCorners, int shadowMapSize)
        {
            Vector3 extent = frustumCorners[3] - frustumCorners[0];
            return extent.Length() / shadowMapSize;
        }

        /// <summary>渲染 CSM 阴影贴图。</summary>
        public void RenderCSM(RHICommandList cmdList)
        {
            if (!_initialized || _csmDepthArray == null) return;

            for (int c = 0; c < _config.CascadeCount; c++)
            {
                var dsv = _device.CreateDSV(_csmDepthArray, 0, (uint)c);
                cmdList.ClearDepthStencilView(dsv, 1.0f, 0);
            }

            // 实际几何体渲染由场景渲染器调用
        }

        /// <summary>执行完整的阴影管线: 渲染 + 过滤。</summary>
        public void Render(RHICommandList cmdList, in CameraData camera,
            RHITexture depthBuffer, RHITexture normalBuffer, RHITexture motionVectors,
            RHITexture shadowOutput, bool hasPointLight, uint frameIndex)
        {
            if (!_initialized) return;

            // 1. 更新级联
            UpdateCascades(camera);

            // 2. 渲染 CSM 阴影贴图
            RenderCSM(cmdList);

            // 3. 阴影采样 (GPU compute shader)
            SampleShadows(cmdList, depthBuffer, normalBuffer, shadowOutput, camera, frameIndex);

            // 4. 空间过滤
            _shadowFilter.SpatialFilter(cmdList, shadowOutput, depthBuffer, normalBuffer);

            // 5. 时序累积
            _shadowFilter.TemporalAccumulate(cmdList, shadowOutput, motionVectors, depthBuffer, frameIndex);

            // 6. 上传常量
            UpdateConstants(camera, frameIndex);
        }

        /// <summary>GPU 阴影采样: 根据软阴影方法选择算法。</summary>
        private void SampleShadows(RHICommandList cmdList, RHITexture depthBuffer,
            RHITexture normalBuffer, RHITexture shadowOutput, in CameraData camera, uint frameIndex)
        {
            cmdList.TransitionTo(_csmDepthArray, RHIResourceState.PixelShaderResource);
            cmdList.TransitionTo(depthBuffer, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(normalBuffer, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(shadowOutput, RHIResourceState.UnorderedAccess);

            int width = 1920;
            int height = 1080;
            int groupsX = (width + 7) / 8;
            int groupsY = (height + 7) / 8;

            // 根据 SoftShadowMethod 选择:
            // - HardwarePCF / PCF3x3 / PCF5x5: 直接 PCF
            // - PCSS: Blocker search + penumbra estimation + PCF
            // - VSM: 切比雪夫不等式
            // - EVSM2: 双通道指数映射
            // - MSM: 4 阶矩
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRootConstantBufferView(0, _csmMatricesCB);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(shadowOutput, RHIResourceState.ShaderResource);
        }

        /// <summary>上传常量缓冲。</summary>
        public void UpdateConstants(in CameraData camera, uint frameIndex)
        {
            if (_csmMatricesCB == null) return;

            var constants = new AdvancedShadowConstants
            {
                ShadowMapSize = _shadowMapSize,
                ShadowDistance = _config.ShadowDistance,
                CascadeCount = _config.CascadeCount,
                DepthBias = _config.BaseDepthBias,
                SlopeScaledBias = _config.SlopeScaledBias,
                NormalOffset = _config.NormalOffset,
                SoftShadowMethod = (int)_config.SoftShadowMethod,
                PCSSSearchRadius = _config.PCSSSearchRadius,
                PCSSPenumbraScale = _config.PCSSPenumbraScale,
                VSMLightBleedingReduction = _config.VSMLightBleedingReduction,
                EVSMPositiveExponent = _config.EVSMPositiveExponent,
                EVSMNegativeExponent = _config.EVSMNegativeExponent,
                EnableContactHardening = _config.EnableContactHardening ? 1 : 0,
                ContactHardeningDistance = _config.ContactHardeningDistance,
                EnableTemporalAccumulation = _config.EnableTemporalAccumulation ? 1 : 0,
                TemporalAlpha = _config.TemporalAlpha,
                FrameIndex = frameIndex
            };

            // 填充级联矩阵和分割
            for (int c = 0; c < Math.Min(8, _config.CascadeCount); c++)
            {
                switch (c)
                {
                    case 0: constants.CascadeViewProj0 = _cascadeViewProj[c]; break;
                    case 1: constants.CascadeViewProj1 = _cascadeViewProj[c]; break;
                    case 2: constants.CascadeViewProj2 = _cascadeViewProj[c]; break;
                    case 3: constants.CascadeViewProj3 = _cascadeViewProj[c]; break;
                    case 4: constants.CascadeViewProj4 = _cascadeViewProj[c]; break;
                    case 5: constants.CascadeViewProj5 = _cascadeViewProj[c]; break;
                    case 6: constants.CascadeViewProj6 = _cascadeViewProj[c]; break;
                    case 7: constants.CascadeViewProj7 = _cascadeViewProj[c]; break;
                }
                float far = _cascadeSplits[c].far;
                switch (c)
                {
                    case 0: constants.CascadeSplits.X = far; break;
                    case 1: constants.CascadeSplits.Y = far; break;
                    case 2: constants.CascadeSplits.Z = far; break;
                    case 3: constants.CascadeSplits.W = far; break;
                    case 4: constants.CascadeSplits2.X = far; break;
                    case 5: constants.CascadeSplits2.Y = far; break;
                    case 6: constants.CascadeSplits2.Z = far; break;
                    case 7: constants.CascadeSplits2.W = far; break;
                }
            }

            _device.UploadToBuffer(_csmMatricesCB, new[] { constants });
        }

        /// <summary>自动深度偏差调整: 基于级联距离和分辨率。</summary>
        public float ComputeAutoBias(int cascadeIndex, float cascadeDistance)
        {
            if (!_config.EnableAutoBias) return _config.BaseDepthBias;

            // 近处级联用更小的偏差 (更高分辨率需要更小偏差)
            float resolutionFactor = _shadowMapSize / 2048.0f;
            float distanceFactor = 1.0f / (1.0f + cascadeIndex * 0.5f);
            return _config.BaseDepthBias / resolutionFactor * distanceFactor;
        }

        public void Dispose()
        {
            _csmDepthArray?.Dispose();
            _csmMatricesCB?.Dispose();
            _pointLightRenderer.Dispose();
            _shadowFilter.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
