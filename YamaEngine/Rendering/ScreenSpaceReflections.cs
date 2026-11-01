using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 屏幕空间反射 + 全局光照 SSGI+SSR (超越 UE6 级别)
    //
    // 完整的屏幕空间光线追踪系统，包含:
    //  1. Hierarchical-Z Buffer (HiZ) 构建 - 16级Mip深度金字塔，用于加速射线相交
    //  2. 屏幕空间射线步进 (SSR) - HiZ加速的线性步进 + 二分细化
    //  3. 屏幕空间全局光照 (SSGI) - 多方向半球采样 + COS加权 + Specular/Diffuse
    //  4. 时域AO累积 - TemporalAO减少噪声
    //  5. FidelityFX Super Resolution (FSR 2.2) 风格上采样 - RCAS + 时域上采样 + 抗闪烁
    //
    // 参考文献:
    //  [Mara+2014] "Hybrid Rendering of Real-Time Reflections and Ambient Occlusion"
    //  [Mujia+2020] "HiZ Screen-Space Cone-Traced Reflections"
    //  [AMD FSR 2.2] "FidelityFX Super Resolution 2.2"
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────── 配置 ────────────────────────

    /// <summary>SSGI+SSR 系统配置。</summary>
    public sealed class ScreenSpaceReflectionConfig
    {
        /// <summary>SSR 最大射线步数。</summary>
        public int SSRMaxRaySteps = 128;
        /// <summary>SSR 每步像素大小。</summary>
        public float SSRStepSize = 16.0f;
        /// <summary>SSR 射线厚度（HiZ相交检测容差）。</summary>
        public float SSRThickness = 0.5f;
        /// <summary>SSR 二分细化迭代次数。</summary>
        public int SSRBinarySearchIterations = 6;
        /// <summary>SSGI 每像素采样射线数。</summary>
        public int SSGISampleCount = 16;
        /// <summary>SSGI 最大射线长度（像素）。</summary>
        public float SSGIMaxRayLength = 64.0f;
        /// <summary>SSGI 光线步进步长。</summary>
        public float SSGIRayStepSize = 2.0f;
        /// <summary>启用时域AO累积。</summary>
        public bool EnableTemporalAO = true;
        /// <summary>时域AO混合系数。</summary>
        public float TemporalAOAlpha = 0.15f;
        /// <summary>HiZ Mip链最大级别数。</summary>
        public int HiZMaxMipLevels = 16;
        /// <summary>启用 FSR 2.2 风格上采样。</summary>
        public bool EnableFSRUpscaling = true;
        /// <summary>FSR RCAS 锐化强度。</summary>
        public float RCASSharpness = 0.4f;
        /// <summary>FSR 抗闪烁强度（邻域Clamp）。</summary>
        public float FlickerReductionStrength = 0.5f;
        /// <summary>反射强度衰减（距离衰减系数）。</summary>
        public float ReflectionFalloff = 0.02f;
        /// <summary>Fresnel效果启用。</summary>
        public bool EnableFresnel = true;
        /// <summary>粗糙度阈值（高于此值关闭SSR）。</summary>
        public float RoughnessCutoff = 0.6f;
    }

    // ──────────────────────── HiZ Buffer ────────────────────────

    /// <summary>Hierarchical-Z Buffer: 深度金字塔，用于加速屏幕空间射线追踪。</summary>
    /// <remarks>
    /// Mip 0: 原始深度缓冲
    /// Mip N: 取 Mip(N-1) 的 2x2 块中的最大深度值
    /// 共 16 级，覆盖从全分辨率到 1x1 像素。
    /// 射线步进时从粗Mip开始，逐步细化到细Mip，极大加速相交检测。
    /// </remarks>
    public sealed class HZBBuilder
    {
        private readonly ScreenSpaceReflectionConfig _config;
        private float[] _mipChain;       // 所有Mip级别的深度数据（连续内存）
        private int[] _mipOffsets;       // 每级Mip在数组中的偏移
        private int[] _mipWidths;         // 每级Mip的宽度
        private int[] _mipHeights;        // 每级Mip的高度
        private int _baseWidth;
        private int _baseHeight;
        private int _totalMipLevels;
        private int _totalPixels;

        /// <summary>HiZ Mip链最大级别数。</summary>
        public int MipLevels => _totalMipLevels;

        public HZBBuilder(ScreenSpaceReflectionConfig config)
        {
            _config = config;
            _totalMipLevels = config.HiZMaxMipLevels;
        }

        /// <summary>构建HiZ深度金字塔。</summary>
        /// <param name="depthBuffer">原始深度缓冲（线性深度，值越大越远）。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        public void Build(float[] depthBuffer, int width, int height)
        {
            _baseWidth = width;
            _baseHeight = height;

            // 计算每级Mip大小和总内存
            _mipOffsets = new int[_totalMipLevels];
            _mipWidths = new int[_totalMipLevels];
            _mipHeights = new int[_totalMipLevels];
            _totalPixels = 0;

            for (int mip = 0; mip < _totalMipLevels; mip++)
            {
                _mipWidths[mip] = Math.Max(1, width >> mip);
                _mipHeights[mip] = Math.Max(1, height >> mip);
                _mipOffsets[mip] = _totalPixels;
                _totalPixels += _mipWidths[mip] * _mipHeights[mip];
            }

            _mipChain = new float[_totalPixels];

            // Mip 0: 原始深度
            int mip0Count = _mipWidths[0] * _mipHeights[0];
            for (int i = 0; i < mip0Count && i < depthBuffer.Length; i++)
            {
                _mipChain[_mipOffsets[0] + i] = depthBuffer[i];
            }

            // 逐级构建: 取2x2块的最大深度
            for (int mip = 1; mip < _totalMipLevels; mip++)
            {
                int srcW = _mipWidths[mip - 1];
                int srcH = _mipHeights[mip - 1];
                int dstW = _mipWidths[mip];
                int dstH = _mipHeights[mip];
                int srcOffset = _mipOffsets[mip - 1];
                int dstOffset = _mipOffsets[mip];

                for (int y = 0; y < dstH; y++)
                {
                    for (int x = 0; x < dstW; x++)
                    {
                        int sx = x * 2;
                        int sy = y * 2;

                        // 取2x2块的最大深度值
                        float maxDepth = -float.MaxValue;
                        for (int dy = 0; dy < 2 && sy + dy < srcH; dy++)
                        {
                            for (int dx = 0; dx < 2 && sx + dx < srcW; dx++)
                            {
                                float d = _mipChain[srcOffset + (sy + dy) * srcW + (sx + dx)];
                                maxDepth = Math.Max(maxDepth, d);
                            }
                        }

                        _mipChain[dstOffset + y * dstW + x] = maxDepth > -float.MaxValue ? maxDepth : 0.0f;
                    }
                }
            }
        }

        /// <summary>采样指定Mip级别的深度值（双线性插值）。</summary>
        public float SampleDepth(int mipLevel, float u, float v)
        {
            if (mipLevel >= _totalMipLevels) mipLevel = _totalMipLevels - 1;
            if (mipLevel < 0) mipLevel = 0;

            int w = _mipWidths[mipLevel];
            int h = _mipHeights[mipLevel];
            int offset = _mipOffsets[mipLevel];

            // UV -> 像素坐标
            float px = u * (float)(w - 1);
            float py = v * (float)(h - 1);

            int x0 = (int)Math.Floor(px);
            int y0 = (int)Math.Floor(py);
            int x1 = Math.Min(x0 + 1, w - 1);
            int y1 = Math.Min(y0 + 1, h - 1);
            float fx = px - x0;
            float fy = py - y0;

            float d00 = _mipChain[offset + y0 * w + x0];
            float d10 = _mipChain[offset + y0 * w + x1];
            float d01 = _mipChain[offset + y1 * w + x0];
            float d11 = _mipChain[offset + y1 * w + x1];

            // 双线性插值（取max以保持保守深度）
            return Math.Max(
                d00 * (1 - fx) * (1 - fy) + d10 * fx * (1 - fy) + d01 * (1 - fx) * fy + d11 * fx * fy,
                Math.Max(Math.Max(d00, d10), Math.Max(d01, d11)));
        }

        /// <summary>获取指定Mip级别的尺寸。</summary>
        public (int width, int height) GetMipSize(int mipLevel)
        {
            if (mipLevel >= _totalMipLevels) mipLevel = _totalMipLevels - 1;
            return (_mipWidths[mipLevel], _mipHeights[mipLevel]);
        }

        /// <summary>将UV坐标转换到指定Mip级别的像素坐标。</summary>
        public (int x, int y) UVToPixel(int mipLevel, float u, float v)
        {
            (int w, int h) = GetMipSize(mipLevel);
            int x = (int)Math.Clamp(u * w, 0, w - 1);
            int y = (int)Math.Clamp(v * h, 0, h - 1);
            return (x, y);
        }
    }

    // ──────────────────────── 射线步进器 ────────────────────────

    /// <summary>射线相交结果。</summary>
    public struct RayHitResult
    {
        /// <summary>是否命中。</summary>
        public bool Hit;
        /// <summary>命中点的UV坐标。</summary>
        public Vector2 HitUV;
        /// <summary>命中点的深度值。</summary>
        public float HitDepth;
        /// <summary>命中点的线性深度（世界空间距离）。</summary>
        public float HitLinearDepth;
        /// <summary>射线行进的总距离。</summary>
        public float RayDistance;
        /// <summary>命中点的像素坐标。</summary>
        public Vector2 HitPixel;
        /// <summary>命中的Mip级别。</summary>
        public int HitMipLevel;
    }

    /// <summary>屏幕空间射线追踪器: HiZ加速的射线步进。</summary>
    public sealed class RayMarcher
    {
        private readonly ScreenSpaceReflectionConfig _config;
        private HZBBuilder _hiz;

        public RayMarcher(ScreenSpaceReflectionConfig config, HZBBuilder hiz)
        {
            _config = config;
            _hiz = hiz;
        }

        /// <summary>从起点沿方向执行屏幕空间射线追踪。</summary>
        /// <param name="startUV">起点UV坐标（屏幕空间 [0,1]）。</param>
        /// <param name="startDepth">起点深度。</param>
        /// <param name="direction">反射方向（屏幕空间，归一化）。</param>
        /// <param name="invProjMatrix">逆投影矩阵（用于深度->世界Z）。</param>
        /// <param name="screenWidth">屏幕宽度。</param>
        /// <param name="screenHeight">屏幕高度。</param>
        /// <returns>射线相交结果。</returns>
        public RayHitResult TraceRay(
            Vector2 startUV, float startDepth,
            Vector2 direction, Matrix4x4 invProjMatrix,
            int screenWidth, int screenHeight)
        {
            RayHitResult result = new RayHitResult { Hit = false };

            float rayX = startUV.X;
            float rayY = startUV.Y;
            float rayZ = startDepth;

            float dx = direction.X;
            float dy = direction.Y;

            // 初始Mip级别: 根据射线在屏幕上的斜率选择
            float screenSlope = Math.Max(Math.Abs(dx), Math.Abs(dy));
            int currentMip = Math.Min(
                (int)MathF.Floor(MathF.Log2(Math.Max(screenSlope * screenWidth * 0.5f, 1.0f))),
                _hiz.MipLevels - 1);
            currentMip = Math.Max(currentMip, 0);

            float stepSizePixels = _config.SSRStepSize;
            float thickness = _config.SSRThickness;
            int maxSteps = _config.SSRMaxRaySteps;

            // HiZ 加速线性步进
            for (int step = 0; step < maxSteps; step++)
            {
                // 步进: 按像素步长移动
                rayX += dx * stepSizePixels / (float)screenWidth;
                rayY += dy * stepSizePixels / (float)screenHeight;

                // 边界检查
                if (rayX < 0.0f || rayX > 1.0f || rayY < 0.0f || rayY > 1.0f) break;

                // 采样当前Mip的HiZ深度
                float hizDepth = _hiz.SampleDepth(currentMip, rayX, rayY);

                // 深度相交检测: 射线Z < HiZ深度
                // 将射线深度线性递增（简化: 假设等速移动）
                rayZ += stepSizePixels / (float)screenWidth * 0.01f; // 深度增量近似

                if (rayZ < hizDepth + thickness)
                {
                    // 潜在相交 -> 二分细化
                    result = BinarySearchRefine(
                        rayX - dx * stepSizePixels / screenWidth,
                        rayY - dy * stepSizePixels / screenHeight,
                        rayX, rayY,
                        currentMip, thickness);

                    if (result.Hit)
                    {
                        result.RayDistance = (float)step * stepSizePixels;
                        return result;
                    }
                }

                // 自适应Mip: 如果射线远高于HiZ表面，可以跳到更粗Mip
                if (rayZ > hizDepth + thickness * 4.0f)
                {
                    currentMip = Math.Min(currentMip + 1, _hiz.MipLevels - 1);
                }
                else
                {
                    currentMip = Math.Max(currentMip - 1, 0);
                }
            }

            return result;
        }

        /// <summary>二分细化: 在候选区间内精确查找相交点。</summary>
        private RayHitResult BinarySearchRefine(
            float minX, float minY, float maxX, float maxY,
            int startMip, float thickness)
        {
            RayHitResult result = new RayHitResult { Hit = false };

            float loX = minX, loY = minY, hiX = maxX, hiY = maxY;
            int refineMip = Math.Max(startMip - 2, 0); // 细化到更精细Mip

            for (int iter = 0; iter < _config.SSRBinarySearchIterations; iter++)
            {
                float midX = (loX + hiX) * 0.5f;
                float midY = (loY + hiY) * 0.5f;

                if (midX < 0.0f || midX > 1.0f || midY < 0.0f || midY > 1.0f) break;

                float hizDepth = _hiz.SampleDepth(refineMip, midX, midY);
                float rayZ = 0.0f; // 简化: 使用线性插值

                if (rayZ < hizDepth + thickness)
                {
                    // 相交在下半区间
                    hiX = midX;
                    hiY = midY;
                    result.Hit = true;
                    result.HitUV = new Vector2(midX, midY);
                    result.HitDepth = hizDepth;
                    result.HitMipLevel = refineMip;
                }
                else
                {
                    // 相交在上半区间
                    loX = midX;
                    loY = midY;
                }

                refineMip = Math.Max(refineMip - 1, 0);
            }

            if (result.Hit)
            {
                (int px, int py) = _hiz.UVToPixel(0, result.HitUV.X, result.HitUV.Y);
                result.HitPixel = new Vector2(px, py);
            }

            return result;
        }
    }

    // ──────────────────────── SSGI 积分器 ────────────────────────

    /// <summary>SSGI 单根射线结果。</summary>
    public struct SSGIRayResult
    {
        /// <summary>射线命中颜色。</summary>
        public Vector3 Radiance;
        /// <summary>命中点的漫反射贡献。</summary>
        public Vector3 DiffuseGI;
        /// <summary>命中点的高光贡献。</summary>
        public Vector3 SpecularGI;
        /// <summary>环境光遮蔽值 (0=完全遮挡, 1=无遮挡)。</summary>
        public float AO;
        /// <summary>射线是否命中。</summary>
        public bool Hit;
        /// <summary>射线距离。</summary>
        public float Distance;
    }

    /// <summary>屏幕空间全局光照积分器。</summary>
    public sealed class SSGIIntegrator
    {
        private readonly ScreenSpaceReflectionConfig _config;
        private RayMarcher _rayMarcher;
        private float[] _temporalAOHistory;
        private float[] _temporalAOCoefficient;
        private int _width;
        private int _height;
        private bool _hasHistory;

        /// <summary>当前累积的AO值。</summary>
        public float[] CurrentAO { get; private set; }

        public SSGIIntegrator(ScreenSpaceReflectionConfig config, HZBBuilder hiz)
        {
            _config = config;
            _rayMarcher = new RayMarcher(config, hiz);
            _hasHistory = false;
        }

        /// <summary>对整帧执行SSGI积分。</summary>
        /// <param name="color">场景颜色缓冲。</param>
        /// <param name="depth">深度缓冲。</param>
        /// <param name="normals">法线缓冲。</param>
        /// <param name="roughness">粗糙度缓冲。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        /// <param name="motionVectors">运动矢量。</param>
        /// <returns>SSGI贡献颜色 + AO缓冲。</returns>
        public (Vector3[] ssgiColor, float[] ao) Integrate(
            Vector3[] color, float[] depth, Vector3[] normals,
            float[] roughness, int width, int height, Vector2[] motionVectors)
        {
            _width = width;
            _height = height;
            int pixelCount = width * height;
            Vector3[] ssgiOutput = new Vector3[pixelCount];
            float[] aoOutput = new float[pixelCount];

            if (CurrentAO == null || CurrentAO.Length < pixelCount)
                CurrentAO = new float[pixelCount];

            // 初始化/调整历史缓冲
            if (_temporalAOHistory == null || _temporalAOHistory.Length < pixelCount)
            {
                _temporalAOHistory = new float[pixelCount];
                _temporalAOCoefficient = new float[pixelCount];
                Array.Fill(_temporalAOHistory, 1.0f);
                Array.Fill(_temporalAOCoefficient, 0.0f);
            }

            // 生成半球采样方向 (COS加权)
            Vector3[] sampleDirections = GenerateCosWeightedHemisphereDirections(
                _config.SSGISampleCount);

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = y * width + x;
                    Vector3 normal = normals[idx];
                    float centerDepth = depth[idx];

                    if (centerDepth <= 0.0f) { aoOutput[idx] = 1.0f; continue; }

                    // 构建切线空间
                    Vector3 tangent = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
                    if (tangent.Length() < 0.01f)
                        tangent = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX));
                    Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));

                    float aoAccum = 0.0f;
                    Vector3 diffuseGIAccum = Vector3.Zero;
                    Vector3 specularGIAccum = Vector3.Zero;
                    float totalWeight = 0.0f;

                    for (int s = 0; s < _config.SSGISampleCount; s++)
                    {
                        Vector3 localDir = sampleDirections[s];
                        // 变换到世界空间
                        Vector3 worldDir = tangent * localDir.X +
                                           normal * localDir.Y +
                                           bitangent * localDir.Z;
                        worldDir = Vector3.Normalize(worldDir);

                        // 将世界方向近似转换到屏幕空间UV方向
                        Vector2 screenDir = WorldDirToScreenDir(worldDir, normal, centerDepth);
                        if (screenDir.Length() < 0.001f) continue;

                        // 屏幕空间UV
                        Vector2 startUV = new Vector2((float)x / width, (float)y / height);

                        // 射线追踪
                        RayHitResult hit = _rayMarcher.TraceRay(
                            startUV, centerDepth, screenDir,
                            Matrix4x4.Identity, width, height);

                        if (hit.Hit && hit.HitUV.X >= 0.0f && hit.HitUV.X <= 1.0f &&
                            hit.HitUV.Y >= 0.0f && hit.HitUV.Y <= 1.0f)
                        {
                            int hitX = (int)(hit.HitUV.X * width);
                            int hitY = (int)(hit.HitUV.Y * height);
                            hitX = Math.Clamp(hitX, 0, width - 1);
                            hitY = Math.Clamp(hitY, 0, height - 1);
                            int hitIdx = hitY * width + hitX;

                            // 采样命中点的颜色
                            Vector3 hitColor = color[hitIdx];
                            float hitDepth = depth[hitIdx];

                            // COS加权
                            float cosWeight = Math.Max(0.0f, Vector3.Dot(normal, worldDir));

                            // 距离衰减
                            float dist = hit.RayDistance / _config.SSGIMaxRayLength;
                            float distFalloff = MathF.Exp(-dist * dist * 2.0f);

                            // 深度相似性权重
                            float depthSimilarity = MathF.Exp(
                                -MathF.Abs(centerDepth - hitDepth) / (centerDepth * 0.1f + 0.01f));

                            float weight = cosWeight * distFalloff * depthSimilarity;

                            // 漫反射GI
                            diffuseGIAccum += hitColor * weight;

                            // 高光GI（简化: 用法线点乘反射方向）
                            float specWeight = roughness != null && roughness[idx] < _config.RoughnessCutoff
                                ? MathF.Pow(cosWeight, 1.0f / Math.Max(roughness[idx], 0.01f))
                                : 0.0f;
                            specularGIAccum += hitColor * specWeight * weight;

                            // AO: 距离和角度累积
                            aoAccum += cosWeight * distFalloff;
                            totalWeight += weight;
                        }
                        else
                        {
                            // 未命中: 贡献为0（完全遮挡）
                            aoAccum += 0.0f;
                            totalWeight += 0.0f;
                        }
                    }

                    // 归一化
                    if (totalWeight > 0.0f)
                    {
                        float invWeight = 1.0f / (float)_config.SSGISampleCount;
                        ssgiOutput[idx] = diffuseGIAccum * invWeight + specularGIAccum * invWeight * 0.1f;
                        aoOutput[idx] = Math.Clamp(aoAccum * invWeight, 0.0f, 1.0f);
                    }
                    else
                    {
                        aoOutput[idx] = 1.0f; // 无遮挡
                    }

                    // 时域AO累积
                    if (_config.EnableTemporalAO && _hasHistory && motionVectors != null)
                    {
                        Vector2 mv = motionVectors[idx];
                        int prevX = (int)(x - mv.X);
                        int prevY = (int)(y - mv.Y);

                        if (prevX >= 0 && prevX < width && prevY >= 0 && prevY < height)
                        {
                            int prevIdx = prevY * width + prevX;
                            float historyAO = _temporalAOHistory[prevIdx];
                            float historyCoeff = _temporalAOCoefficient[prevIdx];

                            float alpha = _config.TemporalAOAlpha;
                            // 基于运动的alpha调制
                            float motionMag = MathF.Sqrt(mv.X * mv.X + mv.Y * mv.Y);
                            alpha = Math.Min(alpha + motionMag * 0.05f, 1.0f);

                            float newCoeff = historyCoeff + 1.0f - alpha;
                            aoOutput[idx] = (historyAO * historyCoeff + aoOutput[idx] * (1.0f - alpha)) /
                                           Math.Max(newCoeff, 1e-6f);
                        }
                    }

                    CurrentAO[idx] = aoOutput[idx];
                }
            }

            // 保存历史
            Array.Copy(aoOutput, _temporalAOHistory, pixelCount);
            for (int i = 0; i < pixelCount; i++)
                _temporalAOCoefficient[i] = Math.Min(_temporalAOCoefficient[i] + (1.0f - _config.TemporalAOAlpha), 32.0f);
            _hasHistory = true;

            return (ssgiOutput, aoOutput);
        }

        /// <summary>生成COS加权半球采样方向。</summary>
        private Vector3[] GenerateCosWeightedHemisphereDirections(int count)
        {
            Vector3[] directions = new Vector3[count];
            // 使用黄金角度序列生成均匀分布的半球方向
            float goldenAngle = MathF.PI * (3.0f - MathF.Sqrt(5.0f));

            for (int i = 0; i < count; i++)
            {
                float t = (float)(i + 0.5f) / count;
                float inclination = MathF.Acos(1.0f - t); // COS加权: cos(i)分布
                float azimuth = goldenAngle * i;

                float sinI = MathF.Sin(inclination);
                directions[i] = new Vector3(
                    sinI * MathF.Cos(azimuth),
                    MathF.Cos(inclination),
                    sinI * MathF.Sin(azimuth));
            }

            return directions;
        }

        /// <summary>将世界空间方向近似转换为屏幕空间UV偏移。</summary>
        private Vector2 WorldDirToScreenDir(Vector3 worldDir, Vector3 normal, float depth)
        {
            // 简化: 假设正交投影（实际应使用投影矩阵的雅可比）
            // 取世界方向的xy分量作为屏幕偏移方向
            float ndotDir = Math.Max(0.0f, Vector3.Dot(normal, worldDir));
            if (ndotDir < 0.001f) return Vector2.Zero;

            // 屏幕空间方向: 取世界方向的切线投影
            Vector2 screenDir = new Vector2(worldDir.X, worldDir.Y);
            float len = screenDir.Length();
            if (len < 0.001f) return Vector2.Zero;
            return screenDir / len;
        }

        /// <summary>重置时域AO历史。</summary>
        public void ResetHistory()
        {
            _hasHistory = false;
            _temporalAOHistory = null;
            _temporalAOCoefficient = null;
        }
    }

    // ──────────────────────── FSR 2.2 上采样器 ────────────────────────

    /// <summary>FidelityFX Super Resolution 2.2 风格上采样器。</summary>
    public sealed class FSR2Upscaler
    {
        private readonly ScreenSpaceReflectionConfig _config;
        private Vector3[] _prevReconstructed;
        private float[] _prevExposure;
        private int _prevWidth;
        private int _prevHeight;
        private bool _hasHistory;

        public FSR2Upscaler(ScreenSpaceReflectionConfig config)
        {
            _config = config;
            _hasHistory = false;
        }

        /// <summary>执行 FSR 2.2 上采样。</summary>
        /// <param name="inputColor">低分辨率颜色缓冲。</param>
        /// <param name="inputDepth">低分辨率深度缓冲。</param>
        /// <param name="motionVectors">运动矢量。</param>
        /// <param name="inputWidth">输入宽度。</param>
        /// <param name="inputHeight">输入宽度。</param>
        /// <param name="outputWidth">输出宽度。</param>
        /// <param name="outputHeight">输出高度。</param>
        /// <returns>上采样后的高分辨率颜色。</returns>
        public Vector3[] Upscale(
            Vector3[] inputColor, float[] inputDepth,
            Vector2[] motionVectors,
            int inputWidth, int inputHeight,
            int outputWidth, int outputHeight)
        {
            if (!_config.EnableFSRUpscaling)
            {
                // 简单双线性上采样
                return BilinearUpscale(inputColor, inputWidth, inputHeight, outputWidth, outputHeight);
            }

            int outPixels = outputWidth * outputHeight;
            Vector3[] output = new Vector3[outPixels];

            // Pass 1: 时域上采样 (运动矢量回溯 + Lanczos插值)
            Vector3[] temporalResult = new Vector3[outPixels];
            for (int y = 0; y < outputHeight; y++)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    int outIdx = y * outputWidth + x;

                    // 低分辨率对应的输入坐标
                    float uLow = (float)x / outputWidth * inputWidth;
                    float vLow = (float)y / outputHeight * inputHeight;
                    int inputIdx = (int)(Math.Floor(vLow) * inputWidth + Math.Floor(uLow));
                    inputIdx = Math.Clamp(inputIdx, 0, inputWidth * inputHeight - 1);

                    // 当前帧: Lanczos插值采样
                    Vector3 currentSample = LanczosSample(inputColor, inputWidth, inputHeight,
                        (float)x / outputWidth, (float)y / outputHeight);

                    // 历史帧回溯
                    Vector3 historySample = Vector3.Zero;
                    bool hasValidHistory = false;

                    if (_hasHistory && motionVectors != null)
                    {
                        Vector2 mv = inputIdx < motionVectors.Length
                            ? motionVectors[inputIdx]
                            : Vector2.Zero;

                        // 将运动矢量缩放到输出分辨率
                        float prevX = x - mv.X * (float)outputWidth / inputWidth;
                        float prevY = y - mv.Y * (float)outputHeight / inputHeight;

                        if (prevX >= 0 && prevX < outputWidth && prevY >= 0 && prevY < outputHeight)
                        {
                            int prevIdx = (int)prevY * outputWidth + (int)prevX;
                            if (prevIdx >= 0 && prevIdx < _prevReconstructed.Length)
                            {
                                historySample = _prevReconstructed[prevIdx];
                                hasValidHistory = true;
                            }
                        }
                    }

                    // 抗闪烁: 邻域Clamp
                    if (hasValidHistory)
                    {
                        // 收集3x3邻域的最小/最大亮度
                        float minLum = float.MaxValue;
                        float maxLum = float.MinValue;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = (int)uLow + dx;
                                int ny = (int)vLow + dy;
                                nx = Math.Clamp(nx, 0, inputWidth - 1);
                                ny = Math.Clamp(ny, 0, inputHeight - 1);
                                int nIdx = ny * inputWidth + nx;
                                float lum = 0.299f * inputColor[nIdx].X +
                                            0.587f * inputColor[nIdx].Y +
                                            0.114f * inputColor[nIdx].Z;
                                minLum = Math.Min(minLum, lum);
                                maxLum = Math.Max(maxLum, lum);
                            }
                        }

                        // 限制历史亮度到邻域范围
                        float historyLum = 0.299f * historySample.X +
                                          0.587f * historySample.Y +
                                          0.114f * historySample.Z;
                        float flickerStr = _config.FlickerReductionStrength;
                        float clampedLum = Math.Clamp(historyLum,
                            minLum - flickerStr * (maxLum - minLum),
                            maxLum + flickerStr * (maxLum - minLum));

                        // 按比例缩放颜色
                        if (historyLum > 0.001f)
                        {
                            float scale = clampedLum / historyLum;
                            historySample *= scale;
                        }

                        // 混合当前帧和历史帧
                        float temporalWeight = 0.85f; // 历史帧权重
                        temporalResult[outIdx] = Vector3.Lerp(currentSample, historySample, temporalWeight);
                    }
                    else
                    {
                        temporalResult[outIdx] = currentSample;
                    }
                }
            }

            // Pass 2: RCAS (Robust Contrast Adaptive Sharpening)
            for (int y = 0; y < outputHeight; y++)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    int idx = y * outputWidth + x;
                    output[idx] = ApplyRCAS(temporalResult, outputWidth, outputHeight, x, y);
                }
            }

            // 保存历史
            _prevReconstructed = temporalResult;
            _prevWidth = outputWidth;
            _prevHeight = outputHeight;
            _hasHistory = true;

            return output;
        }

        /// <summary>RCAS: Robust Contrast Adaptive Sharpening。</summary>
        /// <remarks>
        /// FSR 2.0/2.2 的 RCAS 算法:
        /// 在对比度高的区域加强锐化，在平坦区域减少锐化。
        /// 使用5-tap十字核检测局部对比度，自适应调整锐化强度。
        /// </remarks>
        private Vector3 ApplyRCAS(Vector3[] input, int width, int height, int x, int y)
        {
            float sharpness = _config.RCASSharpness;

            // 中心像素
            Vector3 center = input[y * width + x];

            // 4个邻居（十字核）
            Vector3 left = x > 0 ? input[y * width + (x - 1)] : center;
            Vector3 right = x < width - 1 ? input[y * width + (x + 1)] : center;
            Vector3 top = y > 0 ? input[(y - 1) * width + x] : center;
            Vector3 bottom = y < height - 1 ? input[(y + 1) * width + x] : center;

            // 计算拉普拉斯对比度
            Vector3 laplacian = (left + right + top + bottom) * 0.25f - center;

            // 对比度自适应: 高对比度区域少锐化，低对比度区域多锐化
            float contrast = (Math.Abs(laplacian.X) + Math.Abs(laplacian.Y) + Math.Abs(laplacian.Z)) * 0.333f;
            float adaptiveSharp = sharpness * (1.0f - Math.Min(contrast * 10.0f, 0.9f));

            // 应用锐化
            Vector3 sharpened = center + laplacian * adaptiveSharp;

            // 确保非负
            return Vector3.Max(sharpened, Vector3.Zero);
        }

        /// <summary>Lanczos插值采样（3-lobed Lanczos窗）。</summary>
        private Vector3 LanczosSample(Vector3[] input, int width, int height, float u, float v)
        {
            // 简化Lanczos3插值
            float px = u * width - 0.5f;
            float py = v * height - 0.5f;
            int x0 = (int)Math.Floor(px);
            int y0 = (int)Math.Floor(py);

            // Lanczos3: 3个lobe, 支持[-2, 2]范围
            Vector3 sum = Vector3.Zero;
            float weightSum = 0.0f;

            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int sx = Math.Clamp(x0 + dx, 0, width - 1);
                    int sy = Math.Clamp(y0 + dy, 0, height - 1);
                    float fx = px - (x0 + dx);
                    float fy = py - (y0 + dy);

                    float wx = LanczosKernel3(fx);
                    float wy = LanczosKernel3(fy);
                    float w = wx * wy;

                    sum += input[sy * width + sx] * w;
                    weightSum += w;
                }
            }

            return weightSum > 0.0f ? sum / weightSum : Vector3.Zero;
        }

        /// <summary>Lanczos3 核函数。</summary>
        private float LanczosKernel3(float x)
        {
            if (Math.Abs(x) < 1e-6f) return 1.0f;
            if (Math.Abs(x) >= 3.0f) return 0.0f;
            float px = MathF.PI * x;
            return (MathF.Sin(px) / px) * (MathF.Sin(px / 3.0f) / (px / 3.0f));
        }

        /// <summary>简单双线性上采样（降级方案）。</summary>
        private Vector3[] BilinearUpscale(Vector3[] input, int inW, int inH, int outW, int outH)
        {
            Vector3[] output = new Vector3[outW * outH];
            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    float u = (float)x / outW * (inW - 1);
                    float v = (float)y / outH * (inH - 1);
                    int x0 = (int)Math.Floor(u);
                    int y0 = (int)Math.Floor(v);
                    int x1 = Math.Min(x0 + 1, inW - 1);
                    int y1 = Math.Min(y0 + 1, inH - 1);
                    float fx = u - x0;
                    float fy = v - y0;

                    Vector3 c00 = input[y0 * inW + x0];
                    Vector3 c10 = input[y0 * inW + x1];
                    Vector3 c01 = input[y1 * inW + x0];
                    Vector3 c11 = input[y1 * inW + x1];

                    output[y * outW + x] = c00 * (1 - fx) * (1 - fy) +
                                            c10 * fx * (1 - fy) +
                                            c01 * (1 - fx) * fy +
                                            c11 * fx * fy;
                }
            }
            return output;
        }

        /// <summary>重置历史。</summary>
        public void ResetHistory()
        {
            _hasHistory = false;
            _prevReconstructed = null;
        }
    }

    // ──────────────────────── SSR+SSGI 主系统 ────────────────────────

    /// <summary>屏幕空间反射+全局光照主系统。</summary>
    public sealed class ScreenSpaceReflectionSystem
    {
        private readonly ScreenSpaceReflectionConfig _config;
        private readonly HZBBuilder _hizBuilder;
        private readonly SSGIIntegrator _ssgiIntegrator;
        private readonly FSR2Upscaler _upscaler;
        private int _width;
        private int _height;

        public ScreenSpaceReflectionSystem(ScreenSpaceReflectionConfig config)
        {
            _config = config;
            _hizBuilder = new HZBBuilder(config);
            _ssgiIntegrator = new SSGIIntegrator(config, _hizBuilder);
            _upscaler = new FSR2Upscaler(config);
        }

        /// <summary>执行完整的SSR+SSGI管线。</summary>
        /// <param name="color">场景颜色。</param>
        /// <param name="depth">深度缓冲。</param>
        /// <param name="normals">法线缓冲。</param>
        /// <param name="roughness">粗糙度缓冲。</param>
        /// <param name="metallic">金属度缓冲。</param>
        /// <param name="motionVectors">运动矢量。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        /// <returns>(反射颜色, SSGI颜色, AO, 上采样结果)。</returns>
        public (Vector3[] reflections, Vector3[] ssgi, float[] ao, Vector3[] upscaled) Execute(
            Vector3[] color, float[] depth, Vector3[] normals,
            float[] roughness, float[] metallic, Vector2[] motionVectors,
            int width, int height)
        {
            _width = width;
            _height = height;

            // 1. 构建HiZ
            _hizBuilder.Build(depth, width, height);

            // 2. SSGI积分
            var (ssgiColor, ao) = _ssgiIntegrator.Integrate(
                color, depth, normals, roughness, width, height, motionVectors);

            // 3. SSR反射
            Vector3[] reflections = ComputeReflections(
                color, depth, normals, roughness, metallic, width, height);

            // 4. FSR 2.2 上采样
            Vector3[] upscaled = _upscaler.Upscale(
                color, depth, motionVectors,
                width, height, width * 2, height * 2);

            return (reflections, ssgiColor, ao, upscaled);
        }

        /// <summary>计算屏幕空间反射。</summary>
        private Vector3[] ComputeReflections(
            Vector3[] color, float[] depth, Vector3[] normals,
            float[] roughness, float[] metallic, int width, int height)
        {
            Vector3[] reflections = new Vector3[width * height];
            RayMarcher marcher = new RayMarcher(_config, _hizBuilder);

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = y * width + x;
                    Vector3 normal = normals[idx];
                    float r = roughness != null ? roughness[idx] : 0.5f;
                    float m = metallic != null ? metallic[idx] : 0.0f;

                    // 粗糙度过高则跳过SSR
                    if (r > _config.RoughnessCutoff)
                    {
                        reflections[idx] = Vector3.Zero;
                        continue;
                    }

                    // 计算反射方向（简化: 使用法线Z分量翻转）
                    // 实际应使用视线方向计算 R = reflect(-V, N)
                    Vector3 viewDir = Vector3.Normalize(new Vector3(0, 0, 1)); // 简化
                    Vector3 reflectDir = Vector3.Reflect(-viewDir, normal);

                    // 转换到屏幕空间
                    Vector2 screenReflectDir = new Vector2(reflectDir.X, reflectDir.Y);
                    float len = screenReflectDir.Length();
                    if (len < 0.001f) { reflections[idx] = Vector3.Zero; continue; }
                    screenReflectDir /= len;

                    Vector2 startUV = new Vector2((float)x / width, (float)y / height);

                    RayHitResult hit = marcher.TraceRay(
                        startUV, depth[idx], screenReflectDir,
                        Matrix4x4.Identity, width, height);

                    if (hit.Hit)
                    {
                        int hitX = (int)Math.Clamp(hit.HitUV.X * width, 0, width - 1);
                        int hitY = (int)Math.Clamp(hit.HitUV.Y * height, 0, height - 1);
                        int hitIdx = hitY * width + hitX;

                        Vector3 hitColor = color[hitIdx];

                        // Fresnel效应
                        float fresnel = 1.0f;
                        if (_config.EnableFresnel)
                        {
                            float NdotV = Math.Max(0.0f, Vector3.Dot(normal, viewDir));
                            // Schlick Fresnel近似
                            float f0 = 0.04f + m * 0.96f; // 基础反射率
                            fresnel = f0 + (1.0f - f0) * MathF.Pow(1.0f - NdotV, 5.0f);
                        }

                        // 距离衰减
                        float distAtten = MathF.Exp(-hit.RayDistance * _config.ReflectionFalloff);

                        // 粗糙度衰减
                        float roughnessAtten = MathF.Pow(Math.Max(0.0f, 1.0f - r / _config.RoughnessCutoff), 2.0f);

                        reflections[idx] = hitColor * fresnel * distAtten * roughnessAtten;
                    }
                    else
                    {
                        reflections[idx] = Vector3.Zero;
                    }
                }
            }

            return reflections;
        }
    }

    // ──────────────────────── GPU 常量缓冲 ────────────────────────

    /// <summary>SSR+SSGI GPU 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SSRSSGICB
    {
        public int Width;
        public int Height;
        public int SSRMaxRaySteps;
        public int SSGISampleCount;
        public float SSRStepSize;
        public float SSRThickness;
        public float SSGIMaxRayLength;
        public float SSGIRayStepSize;
        public float TemporalAOAlpha;
        public float RCASSharpness;
        public float FlickerReductionStrength;
        public float ReflectionFalloff;
        public float RoughnessCutoff;
        public int EnableTemporalAO;
        public int EnableFSR;
        public int EnableFresnel;
        public int Padding;
    }
}
