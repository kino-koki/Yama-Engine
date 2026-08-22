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
    // 山雨 级联阴影贴图渲染器 (Cascade Shadow Map Renderer)
    //
    // 支持特性：
    //   1. 4 级级联（可配置 1~8 级），每级独立正交投影
    //   2. 自动级联划分：
    //      - 对数划分 (Logarithmic Split) — 远处精度好
    //      - 线性划分 (Linear Split) — 近处精度好
    //      - 混合划分 (Practical Split) — 两者加权，最常用
    //   3. 级联融合 (Cascade Blending) — 级别之间平滑过渡，消除接缝
    //   4. 稳定投影 (Stable Projection) — 消除相机移动时的阴影抖动 (Texel Snapping)
    //   5. 移动平台优化：每级独立分辨率，近处高分辨率远处低分辨率
    //   6. 渲染所有几何体到所有级联（Brute Force）或按距离剔除
    //   7. 支持 PCF / PCSS / VSM 过滤（委托给 ShadowFilteringSystem）
    //   8. 点光源/方向光支持（这里主要做方向光 CSM）
    //   9. 阴影偏移控制：Slope-Scale Bias, Depth Bias, Normal Offset
    //
    // 参考文献:
    //   [Engel 2006] " Cascaded Shadow Maps"
    //   [Zhang 2006] "Practical Split Scheme for CSM"
    //   [Dimitrov 2007] "Cascaded Shadow Maps"
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>级联划分模式。</summary>
    public enum CascadeSplitMode
    {
        /// <summary>线性划分：每级间距相等。近处精度差。</summary>
        Linear,
        /// <summary>对数划分：近处精度好，远处精度差。</summary>
        Logarithmic,
        /// <summary>实用划分（混合）：Log 和 Linear 的加权平均。最常用。</summary>
        Practical,
        /// <summary>手动划分：用户自己指定每级边界。</summary>
        Manual
    }

    /// <summary>级联渲染质量。</summary>
    public enum CascadeQuality
    {
        /// <summary>低：2 级，512 分辨率。</summary>
        Low,
        /// <summary>中：3 级，1024 分辨率。</summary>
        Medium,
        /// <summary>高：4 级，2048 分辨率。</summary>
        High,
        /// <summary>超高：4 级，4096 分辨率。</summary>
        Ultra
    }

    /// <summary>阴影过滤方法。</summary>
    public enum ShadowFilterMethod
    {
        /// <summary>硬件 PCF 2x2。</summary>
        HardwarePCF,
        /// <summary>PCF 3x3。</summary>
        PCF3x3,
        /// <summary>PCF 5x5。</summary>
        PCF5x5,
        /// <summary>PCSS（百分比接近软阴影）。</summary>
        PCSS,
        /// <summary>VSM（方差阴影图）。</summary>
        VSM,
        /// <summary>EVSM2（指数方差阴影图，双通道）。</summary>
        EVSM2,
        /// <summary>MSM（矩阴影图）。</summary>
        MSM
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>CSM 配置参数。</summary>
    public sealed class CascadeShadowConfig
    {
        /// <summary>级联数量（1~8）。</summary>
        public int CascadeCount = 4;

        /// <summary>每级阴影贴图分辨率。</summary>
        public int ShadowMapSize = 2048;

        /// <summary>级联划分模式。</summary>
        public CascadeSplitMode SplitMode = CascadeSplitMode.Practical;

        /// <summary>Practical 模式下对数权重（0=纯线性，1=纯对数）。</summary>
        public float LogSplitWeight = 0.8f;

        /// <summary>阴影最近距离（相机近裁剪面之外的偏移）。</summary>
        public float ShadowNearOffset = 0.0f;

        /// <summary>阴影最远距离。</summary>
        public float ShadowDistance = 200.0f;

        /// <summary>手动级联边界（Manual 模式下使用，0~1 归一化）。</summary>
        public float[] ManualSplitRatios = { 0.05f, 0.15f, 0.35f, 1.0f };

        /// <summary>级联融合宽度（世界单位，0 表示无融合）。</summary>
        public float BlendDistance = 5.0f;

        /// <summary>深度偏移（避免 shadow acne）。</summary>
        public float DepthBias = 0.0005f;

        /// <summary>斜率偏移（坡面上的 acne 用这个）。</summary>
        public float SlopeScaledBias = 1.5f;

        /// <summary>法线偏移（沿法线方向偏移采样点，减少 peter-panning）。</summary>
        public float NormalOffset = 0.5f;

        /// <summary>阴影过滤方法。</summary>
        public ShadowFilterMethod FilterMethod = ShadowFilterMethod.PCF5x5;

        /// <summary>是否启用 Texel Snapping（稳定投影）。</summary>
        public bool EnableTexelSnapping = true;

        /// <summary>是否启用级联间融合。</summary>
        public bool EnableCascadeBlend = true;

        /// <summary>是否按距离剔除几何（只渲染可能产生阴影的物体）。</summary>
        public bool EnableFrustumCulling = true;

        /// <summary>是否启用屏幕空间阴影过滤（CSM 之后再来一遍）。</summary>
        public bool EnableScreenSpaceFilter = true;

        /// <summary>是否启用半分辨率渲染（性能优化）。</summary>
        public bool HalfResolution = false;

        /// <summary>是否使用纹理数组（true）或纹理图集（false）。</summary>
        public bool UseTextureArray = true;

        /// <summary>阴影颜色（不透明度）。</summary>
        public Vector3 ShadowColor = new(0.1f, 0.1f, 0.15f);

        /// <summary>阴影强度（0=全黑，1=无阴影）。</summary>
        public float ShadowIntensity = 0.85f;

        /// <summary>是否启用阴影淡化（远处阴影淡出）。</summary>
        public bool EnableFade = true;

        /// <summary>阴影淡出开始距离。</summary>
        public float FadeStart = 150.0f;

        /// <summary>阴影淡出结束距离。</summary>
        public float FadeEnd = 200.0f;

        /// <summary>是否启用点光源阴影（非 CSM 路径）。</summary>
        public bool EnablePointLightShadows = false;

        /// <summary>点光源阴影立方体贴图分辨率。</summary>
        public int PointLightShadowSize = 1024;

        /// <summary>应用质量预设。</summary>
        public void ApplyQuality(CascadeQuality quality)
        {
            switch (quality)
            {
                case CascadeQuality.Low:
                    CascadeCount = 2;
                    ShadowMapSize = 512;
                    FilterMethod = ShadowFilterMethod.HardwarePCF;
                    HalfResolution = true;
                    EnableScreenSpaceFilter = false;
                    break;
                case CascadeQuality.Medium:
                    CascadeCount = 3;
                    ShadowMapSize = 1024;
                    FilterMethod = ShadowFilterMethod.PCF3x3;
                    HalfResolution = false;
                    EnableScreenSpaceFilter = false;
                    break;
                case CascadeQuality.High:
                    CascadeCount = 4;
                    ShadowMapSize = 2048;
                    FilterMethod = ShadowFilterMethod.PCF5x5;
                    HalfResolution = false;
                    EnableScreenSpaceFilter = true;
                    break;
                case CascadeQuality.Ultra:
                    CascadeCount = 4;
                    ShadowMapSize = 4096;
                    FilterMethod = ShadowFilterMethod.PCSS;
                    HalfResolution = false;
                    EnableScreenSpaceFilter = true;
                    break;
            }
        }

        public static CascadeShadowConfig Default => new();

        public static CascadeShadowConfig Mobile => new()
        {
            CascadeCount = 2,
            ShadowMapSize = 1024,
            FilterMethod = ShadowFilterMethod.HardwarePCF,
            HalfResolution = true,
            EnableCascadeBlend = false,
            EnableScreenSpaceFilter = false,
            EnableTexelSnapping = true
        };

        public static CascadeShadowConfig Cinematic => new()
        {
            CascadeCount = 4,
            ShadowMapSize = 4096,
            SplitMode = CascadeSplitMode.Practical,
            LogSplitWeight = 0.9f,
            FilterMethod = ShadowFilterMethod.PCSS,
            EnableScreenSpaceFilter = true,
            ShadowDistance = 500.0f,
            BlendDistance = 10.0f
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 级联数据
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>单个级联的运行时数据。</summary>
    public sealed class CascadeData
    {
        /// <summary>级联索引（0=最近）。</summary>
        public int Index;

        /// <summary>该级联覆盖的近裁剪距离（世界空间，沿相机视线）。</summary>
        public float NearPlane;

        /// <summary>该级联覆盖的远裁剪距离。</summary>
        public float FarPlane;

        /// <summary>该级 cascade 的 View 矩阵（光源视角）。</summary>
        public Matrix4x4 ViewMatrix;

        /// <summary>正交投影矩阵。</summary>
        public Matrix4x4 ProjMatrix;

        /// <summary>组合的 ViewProj。</summary>
        public Matrix4x4 ViewProjMatrix;

        /// <summary>Texel Snapping 前的 ViewProj（调试用）。</summary>
        public Matrix4x4 RawViewProjMatrix;

        /// <summary>Texel Snapping 用的偏移量。</summary>
        public Vector3 TexelSnapOffset;

        /// <summary>正交盒的中心（世界空间）。</summary>
        public Vector3 OrthoCenter;

        /// <summary>正交盒的半尺寸（世界空间）。</summary>
        public Vector3 OrthoExtents;

        /// <summary>每 Texel 的世界尺寸。</summary>
        public float TexelWorldSize;

        /// <summary>该级 cascade 的分辨率（可能因级联而异）。</summary>
        public int Resolution;

        /// <summary>渲染到该级 cascade 的物体列表。</summary>
        public readonly List<RenderableItem> Renderables = new(256);

        /// <summary>该级 cascade 的物体数量。</summary>
        public int RenderableCount => Renderables.Count;

        /// <summary>该级 cascade 的 AABB（世界空间）。</summary>
        public BoundingBox WorldAABB;

        /// <summary>该级 cascade 的 AABB（光源空间）。</summary>
        public BoundingBox LightSpaceAABB;

        public void ClearRenderables()
        {
            Renderables.Clear();
        }

        public void AddRenderable(RenderableItem item)
        {
            Renderables.Add(item);
        }

        /// <summary>计算级联的纹理变换矩阵（用于采样）。</summary>
        public Matrix4x4 GetTextureMatrix()
        {
            // 把 NDC [-1,1] 映射到纹理 [0,1]
            var texScaleBias = new Matrix4x4(
                0.5f, 0.0f, 0.0f, 0.0f,
                0.0f, -0.5f, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                0.5f, 0.5f, 0.0f, 1.0f);
            return ViewProjMatrix * texScaleBias;
        }
    }

    /// <summary>简易 AABB。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BoundingBox
    {
        public Vector3 Min;
        public Vector3 Max;

        public BoundingBox(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Extents => (Max - Min) * 0.5f;
        public Vector3 Size => Max - Min;

        public bool IsValid => Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

        /// <summary>合并两个 AABB。</summary>
        public static BoundingBox Merge(BoundingBox a, BoundingBox b)
        {
            if (!a.IsValid) return b;
            if (!b.IsValid) return a;
            return new BoundingBox(
                Vector3.Min(a.Min, b.Min),
                Vector3.Max(a.Max, b.Max));
        }

        /// <summary>变换 AABB。</summary>
        public BoundingBox Transform(Matrix4x4 m)
        {
            Vector3[] corners = GetCorners();
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = Vector3.Transform(corners[i], m);
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }
            return new BoundingBox(min, max);
        }

        /// <summary>获取 8 个角点。</summary>
        public Vector3[] GetCorners()
        {
            return new Vector3[8]
            {
                new(Min.X, Min.Y, Min.Z),
                new(Max.X, Min.Y, Min.Z),
                new(Min.X, Max.Y, Min.Z),
                new(Max.X, Max.Y, Min.Z),
                new(Min.X, Min.Y, Max.Z),
                new(Max.X, Min.Y, Max.Z),
                new(Min.X, Max.Y, Max.Z),
                new(Max.X, Max.Y, Max.Z)
            };
        }

        /// <summary>点是否在 AABB 内。</summary>
        public bool Contains(Vector3 p)
        {
            return p.X >= Min.X && p.X <= Max.X &&
                   p.Y >= Min.Y && p.Y <= Max.Y &&
                   p.Z >= Min.Z && p.Z <= Max.Z;
        }

        /// <summary>和另一个 AABB 是否相交。</summary>
        public bool Intersects(BoundingBox other)
        {
            return Min.X <= other.Max.X && Max.X >= other.Min.X &&
                   Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
                   Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
        }
    }

    /// <summary>渲染项（简化版，真实项目从 SceneCulling 结果获取）。</summary>
    public sealed class RenderableItem
    {
        public uint ObjectId;
        public RHIMesh? Mesh;
        public RHIShaderResourceView? MaterialSRV;
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 PrevWorldMatrix;
        public BoundingBox WorldBounds;
        public bool CastShadow = true;
        public bool ReceiveShadow = true;
        public int SubmeshCount = 1;
        public bool IsSkinned = false;
        public bool IsTwoSided = false;
        public float CullDistance = float.MaxValue;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲布局
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>CSM 渲染用常量缓冲（顶点着色器）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CSMShadowVSConstants
    {
        public Matrix4x4 ViewProjMatrix;
        public Matrix4x4 WorldMatrix;
        public uint CascadeIndex;
        public uint Pad0, Pad1, Pad2;
    }

    /// <summary>CSM 采样用常量缓冲（像素着色器）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CSMShadowPSConstants
    {
        public Matrix4x4 CascadeViewProj0;
        public Matrix4x4 CascadeViewProj1;
        public Matrix4x4 CascadeViewProj2;
        public Matrix4x4 CascadeViewProj3;
        public Matrix4x4 CascadeViewProj4;
        public Matrix4x4 CascadeViewProj5;
        public Matrix4x4 CascadeViewProj6;
        public Matrix4x4 CascadeViewProj7;
        public Vector4 CascadeScales; // XYZ: texel size, W: cascade count
        public Vector4 CascadeOffsets; // XYZ: texel snap offsets
        public Vector4 CascadeDistances; // XYZW: 前4级远距离
        public Vector4 CascadeDistances2; // XYZW: 后4级远距离
        public Vector4 ShadowParams; // X: bias, Y: slope bias, Z: normal offset, W: blend distance
        public Vector4 ShadowParams2; // X: intensity, Y: fade start, Z: fade end, W: padding
        public Vector3 ShadowColor;
        public float Pad0;
        public Vector3 LightDir;
        public float Pad1;
        public Vector3 CameraPos;
        public int FilterMethod;
        public int EnableBlend;
        public int EnableFade;
        public int HalfResolution;
    }

    /// <summary>级联划分参数。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CSMSplitCB
    {
        public float NearPlane;
        public float FarPlane;
        public float LogWeight;
        public float Pad0;
        public Vector4 SplitRatios; // 归一化的级联边界
    }

    // ────────────────────────────────────────────────────────────────────────
    // 级联划分计算
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>级联划分计算器。</summary>
    public static class CascadeSplitCalculator
    {
        /// <summary>计算每个级联的近远平面距离。</summary>
        /// <param name="config">CSM 配置。</param>
        /// <param name="cameraNear">相机近裁剪面。</param>
        /// <param name="cameraFar">相机远裁剪面（或 shadow distance）。</param>
        /// <returns>每级 [near, far] 的数组。</returns>
        public static (float near, float far)[] ComputeSplitDistances(
            CascadeShadowConfig config, float cameraNear, float cameraFar)
        {
            int count = config.CascadeCount;
            var splits = new (float, float)[count];

            float shadowNear = cameraNear + config.ShadowNearOffset;
            float shadowFar = Math.Min(cameraFar, config.ShadowDistance);

            switch (config.SplitMode)
            {
                case CascadeSplitMode.Linear:
                    {
                        float step = (shadowFar - shadowNear) / count;
                        for (int i = 0; i < count; i++)
                        {
                            splits[i] = (shadowNear + i * step, shadowNear + (i + 1) * step);
                        }
                        break;
                    }

                case CascadeSplitMode.Logarithmic:
                    {
                        // 对数划分：i = (log(f) - log(n)) * (i/N) + log(n)
                        float logN = MathF.Log(shadowNear);
                        float logF = MathF.Log(shadowFar);
                        for (int i = 0; i < count; i++)
                        {
                            float t0 = (float)i / count;
                            float t1 = (float)(i + 1) / count;
                            float d0 = MathF.Exp(logN + (logF - logN) * t0);
                            float d1 = MathF.Exp(logN + (logF - logN) * t1);
                            splits[i] = (d0, d1);
                        }
                        break;
                    }

                case CascadeSplitMode.Practical:
                    {
                        // Practical = Log 和 Linear 的加权
                        // lambda = 0 -> 纯线性, lambda = 1 -> 纯对数
                        float lambda = config.LogSplitWeight;
                        for (int i = 0; i < count; i++)
                        {
                            float t0 = (float)i / count;
                            float t1 = (float)(i + 1) / count;

                            // 线性
                            float linD0 = shadowNear + (shadowFar - shadowNear) * t0;
                            float linD1 = shadowNear + (shadowFar - shadowNear) * t1;

                            // 对数
                            float logN = MathF.Log(shadowNear);
                            float logF = MathF.Log(shadowFar);
                            float logD0 = MathF.Exp(logN + (logF - logN) * t0);
                            float logD1 = MathF.Exp(logN + (logF - logN) * t1);

                            float d0 = linD0 * (1 - lambda) + logD0 * lambda;
                            float d1 = linD1 * (1 - lambda) + logD1 * lambda;
                            splits[i] = (d0, d1);
                        }
                        break;
                    }

                case CascadeSplitMode.Manual:
                    {
                        if (config.ManualSplitRatios == null || config.ManualSplitRatios.Length < count)
                            goto case CascadeSplitMode.Practical;

                        float prev = 0.0f;
                        for (int i = 0; i < count; i++)
                        {
                            float ratio = Math.Clamp(config.ManualSplitRatios[i], 0, 1);
                            float d0 = shadowNear + (shadowFar - shadowNear) * prev;
                            float d1 = shadowNear + (shadowFar - shadowNear) * ratio;
                            splits[i] = (d0, d1);
                            prev = ratio;
                        }
                        break;
                    }
            }

            return splits;
        }

        /// <summary>获取 Practical 模式下推荐的 lambda 值（根据级联数自适应）。</summary>
        public static float GetRecommendedLambda(int cascadeCount)
        {
            // 经验值：级联越多，log 权重越高（远处级联覆盖范围大，需要更多 log 来避免精度浪费）
            return cascadeCount switch
            {
                1 => 0.0f,
                2 => 0.5f,
                3 => 0.7f,
                4 => 0.8f,
                _ => 0.9f
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 级联剔除
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>级联剔除器：确定哪些物体需要渲染到哪些级联。</summary>
    public sealed class CascadeCuller
    {
        /// <summary>把场景物体分配到各个级联。</summary>
        public void AssignRenderablesToCascades(
            List<RenderableItem> allRenderables,
            CascadeData[] cascades,
            bool enableFrustumCulling)
        {
            // 先清空
            for (int i = 0; i < cascades.Length; i++)
                cascades[i].ClearRenderables();

            // 给每个级联分配物体
            // 朴素方案：每个物体都渲染到所有它可能可见的级联
            // 优化方案：只渲染到最近的那个级联（但这样会有接缝问题）
            // 折中方案：物体渲染到所有和它相交的级联
            foreach (var item in allRenderables)
            {
                if (!item.CastShadow) continue;

                foreach (var cascade in cascades)
                {
                    if (enableFrustumCulling)
                    {
                        // 检查物体 AABB 是否和 cascade 的世界 AABB 相交
                        if (!item.WorldBounds.Intersects(cascade.WorldAABB))
                            continue;
                    }
                    cascade.AddRenderable(item);
                }
            }
        }

        /// <summary>更精细的剔除：使用光源视锥剔除。</summary>
        public void AssignWithLightFrustumCulling(
            List<RenderableItem> allRenderables,
            CascadeData[] cascades)
        {
            for (int i = 0; i < cascades.Length; i++)
                cascades[i].ClearRenderables();

            foreach (var item in allRenderables)
            {
                if (!item.CastShadow) continue;

                foreach (var cascade in cascades)
                {
                    // 把物体 AABB 变换到光源空间
                    BoundingBox lightSpaceBox = item.WorldBounds.Transform(cascade.ViewMatrix);

                    // 检查是否在正交投影范围内
                    if (lightSpaceBox.Min.X > cascade.OrthoExtents.X ||
                        lightSpaceBox.Max.X < -cascade.OrthoExtents.X ||
                        lightSpaceBox.Min.Y > cascade.OrthoExtents.Y ||
                        lightSpaceBox.Max.Y < -cascade.OrthoExtents.Y ||
                        lightSpaceBox.Min.Z > cascade.OrthoExtents.Z ||
                        lightSpaceBox.Max.Z < -cascade.OrthoExtents.Z)
                        continue;

                    cascade.AddRenderable(item);
                }
            }
        }

        /// <summary>构建级联的世界空间 AABB（基于相机视锥的 8 角点）。</summary>
        public void BuildCascadeAABBs(
            CascadeData[] cascades,
            Vector3 cameraPos, Vector3 cameraForward, Vector3 cameraRight, Vector3 cameraUp,
            float cameraNear, float cameraFar, float fov, float aspectRatio)
        {
            for (int i = 0; i < cascades.Length; i++)
            {
                var cascade = cascades[i];
                var corners = ComputeFrustumCorners(
                    cameraPos, cameraForward, cameraRight, cameraUp,
                    cascade.NearPlane, cascade.FarPlane, fov, aspectRatio);

                Vector3 min = new(float.MaxValue);
                Vector3 max = new(float.MinValue);
                foreach (var c in corners)
                {
                    min = Vector3.Min(min, c);
                    max = Vector3.Max(max, c);
                }
                cascade.WorldAABB = new BoundingBox(min, max);
            }
        }

        /// <summary>计算相机视锥的 8 个角点。</summary>
        public static Vector3[] ComputeFrustumCorners(
            Vector3 cameraPos, Vector3 forward, Vector3 right, Vector3 up,
            float nearDist, float farDist, float fov, float aspect)
        {
            float tanHalfFov = MathF.Tan(fov * 0.5f);

            // 近平面
            float nearHalfH = nearDist * tanHalfFov;
            float nearHalfW = nearHalfH * aspect;
            Vector3 nearCenter = cameraPos + forward * nearDist;

            // 远平面
            float farHalfH = farDist * tanHalfFov;
            float farHalfW = farHalfH * aspect;
            Vector3 farCenter = cameraPos + forward * farDist;

            return new Vector3[8]
            {
                // 近平面 4 角
                nearCenter - right * nearHalfW - up * nearHalfH, // 左下
                nearCenter + right * nearHalfW - up * nearHalfH, // 右下
                nearCenter + right * nearHalfW + up * nearHalfH, // 右上
                nearCenter - right * nearHalfW + up * nearHalfH, // 左上
                // 远平面 4 角
                farCenter - right * farHalfW - up * farHalfH,
                farCenter + right * farHalfW - up * farHalfH,
                farCenter + right * farHalfW + up * farHalfH,
                farCenter - right * farHalfW + up * farHalfH
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 正交投影计算
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>正交投影构建器：根据视锥角点和光源方向计算最佳正交投影。</summary>
    public sealed class OrthoProjectionBuilder
    {
        /// <summary>根据视锥角点构建光源视角的正交投影。</summary>
        /// <param name="frustumCorners">视锥 8 角点（世界空间）。</param>
        /// <param name="lightDir">光源方向（指向光源的反方向，即光线传播方向的反方向）。</param>
        /// <param name="worldUp">世界向上方向。</param>
        /// <returns>(View, Proj, ViewProj, Center, Extents)。</returns>
        public static (Matrix4x4 view, Matrix4x4 proj, Matrix4x4 viewProj,
            Vector3 center, Vector3 extents) BuildOrthoProjection(
            Vector3[] frustumCorners, Vector3 lightDir, Vector3 worldUp)
        {
            // 1. 构建光源视角的 LookAt 矩阵
            // 光源位置：视锥中心 + 光源方向 * 远距离（让光源在场景外面）
            Vector3 frustumCenter = Vector3.Zero;
            for (int i = 0; i < 8; i++)
                frustumCenter += frustumCorners[i];
            frustumCenter /= 8.0f;

            // 光源位置：从中心反向推一段距离
            // hack: 这里推 500 单位，实际应该根据场景大小动态计算
            Vector3 lightPos = frustumCenter - lightDir * 500.0f;

            // 光源 LookAt
            Vector3 lightForward = Vector3.Normalize(lightDir);
            Vector3 lightRight = Vector3.Normalize(Vector3.Cross(worldUp, lightForward));
            if (lightRight.LengthSquared() < 1e-6f)
                lightRight = new Vector3(1, 0, 0); // 光源垂直向上时的退化情况
            Vector3 lightUp = Vector3.Cross(lightForward, lightRight);

            Matrix4x4 viewMatrix = Matrix4x4.CreateLookAt(lightPos, frustumCenter, lightUp);

            // 2. 把视锥角点变换到光源空间
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            foreach (var corner in frustumCorners)
            {
                Vector3 lc = Vector3.Transform(corner, viewMatrix);
                min = Vector3.Min(min, lc);
                max = Vector3.Max(max, lc);
            }

            // 3. 增加一点 padding，避免边界物体被裁掉
            float padding = 5.0f;
            min -= new Vector3(padding);
            max += new Vector3(padding);

            Vector3 center = (min + max) * 0.5f;
            Vector3 extents = (max - min) * 0.5f;

            // 4. 构建正交投影
            Matrix4x4 projMatrix = Matrix4x4.CreateOrthographicOffCenter(
                -extents.X, extents.X,
                -extents.Y, extents.Y,
                0.0f, max.Z - min.Z);

            // 调整 view matrix 让光源看向 box 中心
            // 重新计算光源位置
            Vector3 newLightPos = frustumCenter - lightForward * (extents.Z + 50.0f);
            viewMatrix = Matrix4x4.CreateLookAt(newLightPos, frustumCenter, lightUp);

            Matrix4x4 viewProj = viewMatrix * projMatrix;

            return (viewMatrix, projMatrix, viewProj, frustumCenter, extents);
        }

        /// <summary>Texel Snapping：让正交投影对齐到 texel 边界，消除阴影抖动。</summary>
        public static Vector3 ApplyTexelSnapping(
            Vector3 center, Vector3 extents, float shadowMapSize)
        {
            // 每个像素的世界尺寸
            float texelWorldSize = (extents.X * 2.0f) / shadowMapSize;

            // 把中心对齐到 texel 边界
            // 这样相机移动时，阴影纹理不会"滑动"
            float snapX = MathF.Round(center.X / texelWorldSize) * texelWorldSize;
            float snapY = MathF.Round(center.Y / texelWorldSize) * texelWorldSize;
            float snapZ = MathF.Round(center.Z / texelWorldSize) * texelWorldSize;

            return new Vector3(snapX, snapY, snapZ);
        }

        /// <summary>用对齐后的中心重建 ViewProj。</summary>
        public static Matrix4x4 RebuildWithSnappedCenter(
            Vector3 snappedCenter, Vector3 lightForward, Vector3 worldUp,
            Vector3 extents)
        {
            Vector3 lightRight = Vector3.Normalize(Vector3.Cross(worldUp, lightForward));
            if (lightRight.LengthSquared() < 1e-6f) lightRight = new Vector3(1, 0, 0);
            Vector3 lightUp = Vector3.Cross(lightForward, lightRight);

            Vector3 lightPos = snappedCenter - lightForward * (extents.Z + 50.0f);
            Matrix4x4 view = Matrix4x4.CreateLookAt(lightPos, snappedCenter, lightUp);

            Matrix4x4 proj = Matrix4x4.CreateOrthographicOffCenter(
                -extents.X, extents.X,
                -extents.Y, extents.Y,
                0.0f, extents.Z * 2.0f + 100.0f);

            return view * proj;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 阴影贴图资源
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>CSM 阴影贴图资源（纹理数组或图集）。</summary>
    public sealed class CSMShadowMapResources : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _shadowMapArray;
        private RHITexture? _shadowMapAtlas;
        private RHIDepthStencilView?[]? _dsvs;
        private RHIShaderResourceView? _srv;
        private bool _disposed;

        public int CascadeCount { get; }
        public int ShadowMapSize { get; }
        public bool UseTextureArray { get; }
        public RHIFormat Format { get; }

        public RHITexture ShadowMap => UseTextureArray ? _shadowMapArray! : _shadowMapAtlas!;
        public RHIShaderResourceView SRV => _srv!;
        public RHIDepthStencilView GetDSV(int cascade) => _dsvs![cascade];

        public CSMShadowMapResources(RHIDevice device, int cascadeCount, int size,
            bool useTextureArray, RHIFormat format = RHIFormat.D32_Float)
        {
            _device = device;
            CascadeCount = cascadeCount;
            ShadowMapSize = size;
            UseTextureArray = useTextureArray;
            Format = format;
            Initialize();
        }

        private void Initialize()
        {
            if (UseTextureArray)
            {
                // 纹理数组：每个 slice 是一个 cascade
                _shadowMapArray = _device.CreateTexture(new RHITextureDesc
                {
                    Width = ShadowMapSize,
                    Height = ShadowMapSize,
                    DepthOrArraySize = CascadeCount,
                    MipLevels = 1,
                    Format = Format,
                    Usage = RHITextureUsage.DepthStencil | RHITextureUsage.ShaderResource,
                    DebugName = "CSM_ShadowArray"
                });

                _dsvs = new RHIDepthStencilView[CascadeCount];
                for (int i = 0; i < CascadeCount; i++)
                {
                    _dsvs[i] = _device.CreateDepthStencilView(_shadowMapArray, new RHIDSVDesc
                    {
                        Format = Format,
                        ViewDimension = RHIDSVDimension.Texture2DArray,
                        FirstArraySlice = i,
                        ArraySize = 1
                    });
                }

                _srv = _device.CreateShaderResourceView(_shadowMapArray, new RHISRVDesc
                {
                    Format = Format,
                    ViewDimension = RHISRVDimension.Texture2DArray,
                    ArraySize = CascadeCount
                });
            }
            else
            {
                // 图集：把多个 cascade 拼接成一张大纹理
                int atlasSize = ShadowMapSize;
                // 简化：用 2x2 图集支持最多 4 个 cascade
                if (CascadeCount > 4) atlasSize *= 2;

                _shadowMapAtlas = _device.CreateTexture(new RHITextureDesc
                {
                    Width = atlasSize,
                    Height = atlasSize,
                    Format = Format,
                    Usage = RHITextureUsage.DepthStencil | RHITextureUsage.ShaderResource,
                    DebugName = "CSM_ShadowAtlas"
                });

                _dsvs = new RHIDepthStencilView[CascadeCount];
                for (int i = 0; i < CascadeCount; i++)
                {
                    int tileX = (i % 2) * (atlasSize / 2);
                    int tileY = (i / 2) * (atlasSize / 2);
                    _dsvs[i] = _device.CreateDepthStencilView(_shadowMapAtlas, new RHIDSVDesc
                    {
                        Format = Format,
                        ViewDimension = RHIDSVDimension.Texture2D,
                        // NOTE: 真实实现需要支持 viewport offset
                    });
                }

                _srv = _device.CreateShaderResourceView(_shadowMapAtlas, new RHISRVDesc
                {
                    Format = Format,
                    ViewDimension = RHISRVDimension.Texture2D
                });
            }
        }

        public void Clear(RHICommandList cmdList, int cascade = -1)
        {
            if (cascade < 0)
            {
                // 清除所有 cascade
                for (int i = 0; i < CascadeCount; i++)
                {
                    cmdList.ClearDepthStencilView(_dsvs![i], 1.0f, 0);
                }
            }
            else
            {
                cmdList.ClearDepthStencilView(_dsvs![cascade], 1.0f, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _shadowMapArray?.Dispose();
            _shadowMapAtlas?.Dispose();
            if (_dsvs != null)
                foreach (var dsv in _dsvs) dsv?.Dispose();
            _srv?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 级联阴影贴图渲染器 (Cascade Shadow Map Renderer)。
    /// 支持 4 级级联、自动划分、Texel Snapping、级联融合。
    /// </summary>
    public sealed class CascadeShadowMapRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly CascadeShadowConfig _config;
        private CSMShadowMapResources? _shadowResources;
        private CascadeData[] _cascades;
        private readonly CascadeCuller _culler = new();

        // 常量缓冲
        private RHIConstantBuffer? _vsCB;
        private RHIConstantBuffer? _psCB;
        private RHIConstantBuffer? _splitCB;

        // PSO
        private RHIPipelineState? _shadowDepthPSO;
        private RHIPipelineState? _shadowDepthSkinnedPSO;
        private RHIPipelineState? _shadowResolvePSO;
        private RHIPipelineState? _shadowScreenFilterPSO;

        // 光源信息
        private Vector3 _lightDirection = -Vector3.Normalize(new Vector3(0.5f, -1.0f, 0.3f));
        private Vector3 _lightColor = Vector3.One;
        private float _lightIntensity = 5.0f;

        // 相机信息（上一帧的，用于稳定投影）
        private Vector3 _lastCameraPos;
        private Matrix4x4 _lastCameraView;

        // 统计
        private int _totalShadowDrawCalls;
        private int _totalCascadedObjects;
        private float _lastShadowRenderTimeMs;

        private bool _disposed;
        private bool _initialized;

        public CascadeShadowConfig Config => _config;
        public CascadeData[] Cascades => _cascades;
        public CSMShadowMapResources? ShadowResources => _shadowResources;
        public Vector3 LightDirection
        {
            get => _lightDirection;
            set => _lightDirection = Vector3.Normalize(value);
        }
        public Vector3 LightColor { get => _lightColor; set => _lightColor = value; }
        public float LightIntensity { get => _lightIntensity; set => _lightIntensity = value; }

        public int TotalShadowDrawCalls => _totalShadowDrawCalls;
        public int TotalCascadedObjects => _totalCascadedObjects;
        public float LastShadowRenderTimeMs => _lastShadowRenderTimeMs;

        public CascadeShadowMapRenderer(RHIDevice device, CascadeShadowConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? CascadeShadowConfig.Default;
            _cascades = new CascadeData[_config.CascadeCount];
            for (int i = 0; i < _config.CascadeCount; i++)
            {
                _cascades[i] = new CascadeData { Index = i, Resolution = _config.ShadowMapSize };
            }
        }

        /// <summary>初始化渲染器（创建阴影贴图、PSO、常量缓冲等）。</summary>
        public void Initialize()
        {
            if (_initialized) return;

            // 1. 创建阴影贴图资源
            _shadowResources = new CSMShadowMapResources(
                _device, _config.CascadeCount, _config.ShadowMapSize,
                _config.UseTextureArray);

            // 2. 创建常量缓冲
            _vsCB = _device.CreateConstantBuffer<CSMShadowVSConstants>();
            _psCB = _device.CreateConstantBuffer<CSMShadowPSConstants>();
            _splitCB = _device.CreateConstantBuffer<CSMSplitCB>();

            // 3. 创建 PSO
            CreateShadowDepthPSO();
            CreateShadowResolvePSO();
            CreateScreenFilterPSO();

            _initialized = true;
        }

        private void CreateShadowDepthPSO()
        {
            var desc = new RHIPipelineStateDesc
            {
                DepthStencilFormat = _shadowResources!.Format,
                DepthEnable = true,
                DepthWriteEnable = true,
                DepthFunc = RHIDepthFunc.Less,
                CullMode = RHICullMode.Back,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                DebugName = "CSM_DepthOnly"
            };

            // 简化：只写深度，不写颜色
            _shadowDepthPSO = _device.CreatePipelineState(desc);

            // 蒙皮版本
            desc.DebugName = "CSM_DepthOnly_Skinned";
            _shadowDepthSkinnedPSO = _device.CreatePipelineState(desc);
        }

        private void CreateShadowResolvePSO()
        {
            var desc = new RHIPipelineStateDesc
            {
                DebugName = "CSM_Resolve",
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                DepthEnable = false,
                BlendEnable = false,
                RTFormat0 = RHIFormat.R8G8B8A8_UNorm
            };
            _shadowResolvePSO = _device.CreatePipelineState(desc);
        }

        private void CreateScreenFilterPSO()
        {
            var desc = new RHIPipelineStateDesc
            {
                DebugName = "CSM_ScreenFilter",
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                DepthEnable = false,
                RTFormat0 = RHIFormat.R8G8B8A8_UNorm
            };
            _shadowScreenFilterPSO = _device.CreatePipelineState(desc);
        }

        // ════════════════════════════════════════════════════════════════════
        // 更新级联
        // ════════════════════════════════════════════════════════════════════

        /// <summary>更新级联的投影矩阵和剔除。</summary>
        public void UpdateCascades(
            CameraData camera, List<RenderableItem> renderables)
        {
            // 1. 计算每级的 near/far
            var splits = CascadeSplitCalculator.ComputeSplitDistances(
                _config, camera.NearPlane, camera.FarPlane);

            for (int i = 0; i < _config.CascadeCount && i < splits.Length; i++)
            {
                _cascades[i].NearPlane = splits[i].near;
                _cascades[i].FarPlane = splits[i].far;
                _cascades[i].Resolution = _config.ShadowMapSize;
            }

            // 2. 构建每级的世界 AABB
            _culler.BuildCascadeAABBs(
                _cascades,
                camera.Position, camera.Forward, camera.Right, camera.Up,
                camera.NearPlane, _config.ShadowDistance,
                camera.FOV, camera.AspectRatio);

            // 3. 对每级构建正交投影
            Vector3 worldUp = MathF.Abs(Vector3.Dot(_lightDirection, Vector3.UnitY)) < 0.99f
                ? Vector3.UnitY
                : Vector3.UnitZ;

            foreach (var cascade in _cascades)
            {
                // 获取该级 cascade 的视锥角点
                var corners = CascadeCuller.ComputeFrustumCorners(
                    camera.Position, camera.Forward, camera.Right, camera.Up,
                    cascade.NearPlane, cascade.FarPlane,
                    camera.FOV, camera.AspectRatio);

                // 构建正交投影
                var (view, proj, viewProj, center, extents) =
                    OrthoProjectionBuilder.BuildOrthoProjection(
                        corners, _lightDirection, worldUp);

                cascade.RawViewProjMatrix = viewProj;
                cascade.ViewMatrix = view;
                cascade.ProjMatrix = proj;
                cascade.OrthoCenter = center;
                cascade.OrthoExtents = extents;
                cascade.TexelWorldSize = (extents.X * 2.0f) / cascade.Resolution;

                // Texel Snapping
                if (_config.EnableTexelSnapping)
                {
                    // 把 center 变换到光源空间
                    Vector3 lightSpaceCenter = Vector3.Transform(center, view);
                    Vector3 snapped = OrthoProjectionBuilder.ApplyTexelSnapping(
                        lightSpaceCenter, extents, cascade.Resolution);
                    cascade.TexelSnapOffset = snapped - lightSpaceCenter;

                    // 用对齐后的 center 重建 ViewProj
                    // 把 snap 后的 center 变换回世界空间
                    Vector3 worldSnapped = Vector3.Transform(snapped, Matrix4x4.Invert(view, out var invView) ? invView : view);
                    cascade.ViewProjMatrix = OrthoProjectionBuilder.RebuildWithSnappedCenter(
                        worldSnapped, _lightDirection, worldUp, extents);
                }
                else
                {
                    cascade.ViewProjMatrix = viewProj;
                    cascade.TexelSnapOffset = Vector3.Zero;
                }

                // 计算光源空间的 AABB
                cascade.LightSpaceAABB = cascade.WorldAABB.Transform(view);
            }

            // 4. 剔除物体到各 cascade
            if (_config.EnableFrustumCulling)
                _culler.AssignWithLightFrustumCulling(renderables, _cascades);
            else
                _culler.AssignRenderablesToCascades(renderables, _cascades, false);

            // 5. 统计
            _totalCascadedObjects = 0;
            foreach (var c in _cascades)
                _totalCascadedObjects += c.RenderableCount;
        }

        // ════════════════════════════════════════════════════════════════════
        // 渲染阴影贴图
        // ════════════════════════════════════════════════════════════════════

        /// <summary>渲染所有级联的阴影贴图。</summary>
        public void RenderShadowMaps(RHICommandList cmdList)
        {
            if (!_initialized || _shadowResources == null)
                throw new InvalidOperationException("CSM renderer not initialized");

            _totalShadowDrawCalls = 0;
            long startTime = Environment.TickCount64;

            // 1. 清除所有 cascade 的深度
            _shadowResources.Clear(cmdList);

            // 2. 设置 viewport（每个 cascade 一个）
            // 3. 逐 cascade 渲染
            for (int i = 0; i < _config.CascadeCount; i++)
            {
                RenderCascade(cmdList, i);
            }

            _lastShadowRenderTimeMs = (Environment.TickCount64 - startTime) / 10.0f;
        }

        private void RenderCascade(RHICommandList cmdList, int cascadeIndex)
        {
            var cascade = _cascades[cascadeIndex];
            int res = cascade.Resolution;

            // 设置 viewport
            cmdList.SetViewport(new RHIViewport(0, 0, res, res, 0, 1));

            // 设置 DSV
            cmdList.SetDepthStencilView(_shadowResources!.GetDSV(cascadeIndex));

            // 设置 PSO
            cmdList.SetPipelineState(_shadowDepthPSO!);

            // 更新常量缓冲
            var vsData = new CSMShadowVSConstants
            {
                ViewProjMatrix = cascade.ViewProjMatrix,
                CascadeIndex = (uint)cascadeIndex
            };
            _vsCB!.UpdateData(ref vsData);
            cmdList.SetConstantBuffer(0, _vsCB);

            // 渲染所有物体
            foreach (var item in cascade.Renderables)
            {
                if (item.Mesh == null) continue;

                vsData.WorldMatrix = item.WorldMatrix;
                _vsCB.UpdateData(ref vsData);

                cmdList.SetConstantBuffer(0, _vsCB);

                // 蒙皮网格用单独的 PSO
                if (item.IsSkinned)
                    cmdList.SetPipelineState(_shadowDepthSkinnedPSO!);
                else
                    cmdList.SetPipelineState(_shadowDepthPSO!);

                cmdList.DrawIndexed(item.Mesh);
                _totalShadowDrawCalls++;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // 采样阴影（主渲染阶段调用）
        // ════════════════════════════════════════════════════════════════════

        /// <summary>更新像素着色器常量缓冲（供主渲染阶段采样阴影使用）。</summary>
        public void UpdateShadowSamplingConstants(CameraData camera)
        {
            var psData = new CSMShadowPSConstants();

            // 填充每级的 ViewProj 矩阵
            for (int i = 0; i < 8; i++)
            {
                if (i < _config.CascadeCount)
                {
                    var texMatrix = _cascades[i].GetTextureMatrix();
                    switch (i)
                    {
                        case 0: psData.CascadeViewProj0 = texMatrix; break;
                        case 1: psData.CascadeViewProj1 = texMatrix; break;
                        case 2: psData.CascadeViewProj2 = texMatrix; break;
                        case 3: psData.CascadeViewProj3 = texMatrix; break;
                        case 4: psData.CascadeViewProj4 = texMatrix; break;
                        case 5: psData.CascadeViewProj5 = texMatrix; break;
                        case 6: psData.CascadeViewProj6 = texMatrix; break;
                        case 7: psData.CascadeViewProj7 = texMatrix; break;
                    }
                }
            }

            // 级联距离
            for (int i = 0; i < Math.Min(4, _config.CascadeCount); i++)
            {
                switch (i)
                {
                    case 0: psData.CascadeDistances.X = _cascades[i].FarPlane; break;
                    case 1: psData.CascadeDistances.Y = _cascades[i].FarPlane; break;
                    case 2: psData.CascadeDistances.Z = _cascades[i].FarPlane; break;
                    case 3: psData.CascadeDistances.W = _cascades[i].FarPlane; break;
                }
            }
            for (int i = 4; i < Math.Min(8, _config.CascadeCount); i++)
            {
                switch (i)
                {
                    case 4: psData.CascadeDistances2.X = _cascades[i].FarPlane; break;
                    case 5: psData.CascadeDistances2.Y = _cascades[i].FarPlane; break;
                    case 6: psData.CascadeDistances2.Z = _cascades[i].FarPlane; break;
                    case 7: psData.CascadeDistances2.W = _cascades[i].FarPlane; break;
                }
            }

            // 阴影参数
            psData.ShadowParams = new Vector4(
                _config.DepthBias,
                _config.SlopeScaledBias,
                _config.NormalOffset,
                _config.BlendDistance);
            psData.ShadowParams2 = new Vector4(
                _config.ShadowIntensity,
                _config.FadeStart,
                _config.FadeEnd,
                0);
            psData.ShadowColor = _config.ShadowColor;
            psData.LightDir = _lightDirection;
            psData.CameraPos = camera.Position;
            psData.FilterMethod = (int)_config.FilterMethod;
            psData.EnableBlend = _config.EnableCascadeBlend ? 1 : 0;
            psData.EnableFade = _config.EnableFade ? 1 : 0;
            psData.HalfResolution = _config.HalfResolution ? 1 : 0;

            // Texel size
            float texelSize = 1.0f / _config.ShadowMapSize;
            psData.CascadeScales = new Vector4(texelSize, texelSize, texelSize, _config.CascadeCount);

            // Texel snap offsets（第一级的，简化）
            if (_config.CascadeCount > 0)
                psData.CascadeOffsets = new Vector4(_cascades[0].TexelSnapOffset, 0);

            _psCB!.UpdateData(ref psData);
        }

        /// <summary>绑定阴影贴图 SRV 到着色器（主渲染阶段）。</summary>
        public void BindShadowResources(RHICommandList cmdList, int srvSlot, int cbSlot)
        {
            cmdList.SetShaderResourceView(srvSlot, _shadowResources!.SRV);
            cmdList.SetConstantBuffer(cbSlot, _psCB!);
        }

        // ════════════════════════════════════════════════════════════════════
        // 屏幕空间阴影过滤
        // ════════════════════════════════════════════════════════════════════

        /// <summary>应用屏幕空间阴影过滤（减少阴影噪声）。</summary>
        public void ApplyScreenSpaceFilter(
            RHICommandList cmdList,
            RHITexture inputShadowMask,
            RHITexture outputShadowMask,
            RHITexture depthBuffer,
            RHITexture normalBuffer)
        {
            if (!_config.EnableScreenSpaceFilter) return;

            cmdList.SetPipelineState(_shadowScreenFilterPSO!);

            // 绑定输入
            cmdList.SetShaderResourceView(0, inputShadowMask);
            cmdList.SetShaderResourceView(1, depthBuffer);
            cmdList.SetShaderResourceView(2, normalBuffer);

            // 设置输出
            cmdList.SetRenderTarget(outputShadowMask);

            // 绘制全屏三角形
            cmdList.Draw(3, 1);
        }

        // ════════════════════════════════════════════════════════════════════
        // 选择级联
        // ════════════════════════════════════════════════════════════════════

        /// <summary>根据视图空间的深度选择合适的级联索引。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SelectCascadeIndex(float viewSpaceDepth)
        {
            // 简单的线性搜索，cascade 数量一般不超过 8
            for (int i = 0; i < _config.CascadeCount; i++)
            {
                if (viewSpaceDepth <= _cascades[i].FarPlane)
                    return i;
            }
            return _config.CascadeCount - 1;
        }

        /// <summary>计算级联融合权重（用于级联间平滑过渡）。</summary>
        /// <param name="cascadeIndex">当前级联索引。</param>
        /// <param name="viewSpaceDepth">视图空间深度。</param>
        /// <returns>(blendWeight, nextCascadeIndex)。blendWeight=0 表示当前级联，1 表示下一级联。</returns>
        public (float blendWeight, int nextCascade) ComputeCascadeBlend(
            int cascadeIndex, float viewSpaceDepth)
        {
            if (!_config.EnableCascadeBlend || cascadeIndex >= _config.CascadeCount - 1)
                return (0, cascadeIndex);

            var cascade = _cascades[cascadeIndex];
            float blendStart = cascade.FarPlane - _config.BlendDistance;
            float blendEnd = cascade.FarPlane;

            if (viewSpaceDepth <= blendStart)
                return (0, cascadeIndex);

            float t = Math.Clamp((viewSpaceDepth - blendStart) / (blendEnd - blendStart), 0, 1);
            return (t, cascadeIndex + 1);
        }

        // ════════════════════════════════════════════════════════════════════
        // 阴影因子计算（CPU 参考，实际在 GPU 上做）
        // ════════════════════════════════════════════════════════════════════

        /// <summary>采样单个级联的阴影因子（CPU 参考）。</summary>
        public float SampleShadowFactorCPU(
            Vector3 worldPos, Vector3 normal, int cascadeIndex,
            ShadowMapSampler sampler)
        {
            var cascade = _cascades[cascadeIndex];
            var texMatrix = cascade.GetTextureMatrix();

            // 变换到阴影纹理空间
            Vector4 shadowCoord = Vector4.Transform(
                new Vector4(worldPos, 1.0f), texMatrix);
            Vector3 shadowUV = new(shadowCoord.X, shadowCoord.Y, shadowCoord.Z);

            // 边界检查
            if (shadowUV.X < 0 || shadowUV.X > 1 || shadowUV.Y < 0 || shadowUV.Y > 1)
                return 1.0f; // 超出范围，无阴影

            // Normal offset
            if (_config.NormalOffset > 0)
            {
                shadowCoord = Vector4.Transform(
                    new Vector4(worldPos + normal * _config.NormalOffset * cascade.TexelWorldSize, 1.0f),
                    texMatrix);
                shadowUV = new(shadowCoord.X, shadowCoord.Y, shadowCoord.Z);
            }

            // 采样
            float texelSize = 1.0f / cascade.Resolution;
            return sampler.Sample(
                shadowUV, cascadeIndex, texelSize,
                _config.DepthBias, _config.FilterMethod);
        }

        /// <summary>采样阴影因子（考虑级联选择和融合）。</summary>
        public float SampleShadowFactorBlendedCPU(
            Vector3 worldPos, Vector3 normal, float viewSpaceDepth,
            ShadowMapSampler sampler)
        {
            int cascadeIdx = SelectCascadeIndex(viewSpaceDepth);
            var (blend, nextIdx) = ComputeCascadeBlend(cascadeIdx, viewSpaceDepth);

            float shadow0 = SampleShadowFactorCPU(worldPos, normal, cascadeIdx, sampler);
            if (blend <= 0)
                return shadow0;

            float shadow1 = SampleShadowFactorCPU(worldPos, normal, nextIdx, sampler);
            return MathHelper.Lerp(shadow0, shadow1, blend);
        }

        // ════════════════════════════════════════════════════════════════════
        // 阴影淡化
        // ════════════════════════════════════════════════════════════════════

        /// <summary>根据距离计算阴影淡入淡出因子。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ComputeShadowFade(float viewSpaceDepth)
        {
            if (!_config.EnableFade) return 1.0f;
            if (viewSpaceDepth < _config.FadeStart) return 1.0f;
            if (viewSpaceDepth > _config.FadeEnd) return 0.0f;
            return 1.0f - (viewSpaceDepth - _config.FadeStart) / (_config.FadeEnd - _config.FadeStart);
        }

        // ════════════════════════════════════════════════════════════════════
        // 配置变更
        // ════════════════════════════════════════════════════════════════════

        /// <summary>更改级联数量（需要重建阴影贴图）。</summary>
        public void SetCascadeCount(int count)
        {
            if (count < 1 || count > 8)
                throw new ArgumentOutOfRangeException(nameof(count), "Cascade count must be in [1, 8]");

            if (count == _config.CascadeCount) return;

            _config.CascadeCount = count;
            RebuildResources();
        }

        /// <summary>更改阴影贴图分辨率（需要重建阴影贴图）。</summary>
        public void SetShadowMapSize(int size)
        {
            if (size < 64 || size > 8192 || (size & (size - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(size), "Shadow map size must be power of 2 in [64, 8192]");

            if (size == _config.ShadowMapSize) return;

            _config.ShadowMapSize = size;
            RebuildResources();
        }

        private void RebuildResources()
        {
            _shadowResources?.Dispose();
            _shadowResources = null;
            _initialized = false;

            // 重建 cascades
            _cascades = new CascadeData[_config.CascadeCount];
            for (int i = 0; i < _config.CascadeCount; i++)
            {
                _cascades[i] = new CascadeData { Index = i, Resolution = _config.ShadowMapSize };
            }

            Initialize();
        }

        // ════════════════════════════════════════════════════════════════════
        // 调试
        // ════════════════════════════════════════════════════════════════════

        /// <summary>获取调试信息字符串。</summary>
        public string GetDebugInfo()
        {
            return $"CSM: {_config.CascadeCount} cascades, {_config.ShadowMapSize}x{_config.ShadowMapSize}\n" +
                   $"Filter: {_config.FilterMethod}\n" +
                   $"Split: {_config.SplitMode} (lambda={_config.LogSplitWeight})\n" +
                   $"DrawCalls: {_totalShadowDrawCalls}, Objects: {_totalCascadedObjects}\n" +
                   $"RenderTime: {_lastShadowRenderTimeMs:F2}ms\n" +
                   $"CascadeDistances: {GetCascadeDistancesString()}";
        }

        private string GetCascadeDistancesString()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _config.CascadeCount; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"[{_cascades[i].NearPlane:F1}-{_cascades[i].FarPlane:F1}]");
            }
            return sb.ToString();
        }

        /// <summary>获取每级 cascade 的可视化颜色（调试用）。</summary>
        public static readonly Vector4[] CascadeDebugColors = new Vector4[8]
        {
            new(1, 0, 0, 1),     // 红
            new(0, 1, 0, 1),     // 绿
            new(0, 0, 1, 1),     // 蓝
            new(1, 1, 0, 1),     // 黄
            new(1, 0, 1, 1),     // 紫
            new(0, 1, 1, 1),     // 青
            new(1, 0.5f, 0, 1),  // 橙
            new(0.5f, 0, 1, 1)   // 紫罗兰
        };

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _shadowResources?.Dispose();
            _vsCB?.Dispose();
            _psCB?.Dispose();
            _splitCB?.Dispose();
            _shadowDepthPSO?.Dispose();
            _shadowDepthSkinnedPSO?.Dispose();
            _shadowResolvePSO?.Dispose();
            _shadowScreenFilterPSO?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 阴影贴图采样器（CPU 参考，实际 GPU 用着色器）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>阴影贴图采样器：从阴影贴图采样并应用过滤。</summary>
    public sealed class ShadowMapSampler
    {
        private readonly CSMShadowMapResources _resources;
        private readonly CascadeData[] _cascades;
        // NOTE: 这是 CPU 参考实现，真实采样在 GPU 着色器里做
        // 这里假设有一个 CPU 端的深度数据缓存（用于调试和单元测试）
        private float[]?[]? _depthData;

        public ShadowMapSampler(CSMShadowMapResources resources, CascadeData[] cascades)
        {
            _resources = resources;
            _cascades = cascades;
            _depthData = new float[resources.CascadeCount][];
        }

        /// <summary>设置 CPU 端的深度数据（调试用）。</summary>
        public void SetDepthData(int cascade, float[] data)
        {
            _depthData![cascade] = data;
        }

        /// <summary>采样阴影贴图。</summary>
        public float Sample(
            Vector3 shadowUV, int cascadeIndex, float texelSize,
            float depthBias, ShadowFilterMethod method)
        {
            var data = _depthData![cascadeIndex];
            if (data == null) return 1.0f;

            int size = _cascades[cascadeIndex].Resolution;

            return method switch
            {
                ShadowFilterMethod.HardwarePCF => SampleHardwarePCF(shadowUV, data, size, depthBias),
                ShadowFilterMethod.PCF3x3 => SamplePCF(shadowUV, data, size, depthBias, 1),
                ShadowFilterMethod.PCF5x5 => SamplePCF(shadowUV, data, size, depthBias, 2),
                ShadowFilterMethod.PCSS => SamplePCSS(shadowUV, data, size, depthBias, texelSize),
                ShadowFilterMethod.VSM => SampleVSM(shadowUV, data, size),
                _ => SampleHardwarePCF(shadowUV, data, size, depthBias)
            };
        }

        private static float SampleHardwarePCF(Vector3 uv, float[] data, int size, float bias)
        {
            int x = (int)(uv.X * size);
            int y = (int)(uv.Y * size);
            if (x < 0 || x >= size || y < 0 || y >= size) return 1.0f;

            float depth = data[y * size + x];
            return uv.Z - bias <= depth ? 1.0f : 0.0f;
        }

        private static float SamplePCF(Vector3 uv, float[] data, int size, float bias, int radius)
        {
            float fx = uv.X * size;
            float fy = uv.Y * size;
            int x0 = (int)fx;
            int y0 = (int)fy;

            float sum = 0;
            int count = 0;

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = x0 + dx;
                    int y = y0 + dy;
                    if (x < 0 || x >= size || y < 0 || y >= size)
                    {
                        sum += 1.0f; // 边界外视为无阴影
                    }
                    else
                    {
                        float depth = data[y * size + x];
                        sum += uv.Z - bias <= depth ? 1.0f : 0.0f;
                    }
                    count++;
                }
            }

            return sum / count;
        }

        private static float SamplePCSS(
            Vector3 uv, float[] data, int size, float bias, float texelSize)
        {
            // PCSS: Percentage-Closer Soft Shadows
            // 1. Blocker Search: 找到平均 blocker 深度
            // 2. Penumbra Size: 根据 blocker 距离计算 penumbra 大小
            // 3. PCF: 用计算出的 penumbra 大小做 PCF

            // Step 1: Blocker Search
            int searchRadius = 5;
            float blockerSum = 0;
            int blockerCount = 0;
            int x0 = (int)(uv.X * size);
            int y0 = (int)(uv.Y * size);

            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    int x = x0 + dx;
                    int y = y0 + dy;
                    if (x < 0 || x >= size || y < 0 || y >= size) continue;

                    float depth = data[y * size + x];
                    if (depth < uv.Z - bias)
                    {
                        blockerSum += depth;
                        blockerCount++;
                    }
                }
            }

            if (blockerCount == 0) return 1.0f; // 无 blocker，无阴影
            if (blockerCount == (searchRadius * 2 + 1) * (searchRadius * 2 + 1)) return 0.0f; // 全遮挡

            float avgBlockerDepth = blockerSum / blockerCount;
            float penumbraRatio = (uv.Z - avgBlockerDepth) / avgBlockerDepth;
            int filterRadius = Math.Max(1, (int)(penumbraRatio * 5.0f));

            // Step 2: PCF with variable radius
            return SamplePCF(uv, data, size, bias, filterRadius);
        }

        private static float SampleVSM(Vector3 uv, float[] data, int size)
        {
            // VSM: Variance Shadow Map
            // NOTE: 这块需要存两个通道（深度和深度平方），这里简化只做单通道
            int x = (int)(uv.X * size);
            int y = (int)(uv.Y * size);
            if (x < 0 || x >= size || y < 0 || y >= size) return 1.0f;

            float depth = data[y * size + x];
            if (uv.Z <= depth) return 1.0f;

            // 估计方差（简化）
            float depthSq = depth * depth;
            float variance = Math.Max(depthSq - depth * depth, 0.0001f);
            float chebyshev = variance / (variance + (uv.Z - depth) * (uv.Z - depth));
            return Math.Clamp(chebyshev, 0, 1);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 辅助类型（避免和 RHI 命名空间冲突，这里定义最小集合）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>相机数据。</summary>
    public sealed class CameraData
    {
        public Vector3 Position;
        public Vector3 Forward;
        public Vector3 Right;
        public Vector3 Up;
        public float NearPlane = 0.1f;
        public float FarPlane = 1000.0f;
        public float FOV = 60.0f * MathF.PI / 180.0f;
        public float AspectRatio = 16.0f / 9.0f;
        public Matrix4x4 ViewMatrix;
        public Matrix4x4 ProjMatrix;
        public Matrix4x4 ViewProjMatrix;
        public Matrix4x4 InvViewProjMatrix;

        public void UpdateMatrices()
        {
            ViewMatrix = Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
            ProjMatrix = Matrix4x4.CreatePerspectiveFieldOfView(FOV, AspectRatio, NearPlane, FarPlane);
            ViewProjMatrix = ViewMatrix * ProjMatrix;
            Matrix4x4.Invert(ViewProjMatrix, out InvViewProjMatrix);
        }
    }

    /// <summary>简单数学辅助。</summary>

    // ────────────────────────────────────────────────────────────────────────
    // RHI 占位类型（如果 RHI 命名空间没有这些）
    // ────────────────────────────────────────────────────────────────────────

    public enum RHICullMode { None, Front, Back }
    public enum RHIDepthFunc { Never, Less, Equal, LessEqual, Greater, NotEqual, GreaterEqual, Always }
    public enum RHIPrimitiveTopology { TriangleList, TriangleStrip, LineList, PointList }

    public struct RHIPipelineStateDesc
    {
        public string? DebugName;
        public RHIFormat DepthStencilFormat;
        public bool DepthEnable;
        public bool DepthWriteEnable;
        public RHIDepthFunc DepthFunc;
        public RHICullMode CullMode;
        public RHIPrimitiveTopology PrimitiveTopology;
        public bool BlendEnable;
        public RHIFormat RTFormat0;
    }

    

    public sealed class RHIConstantBuffer : IDisposable
    {
        public void UpdateData<T>(ref T data) where T : struct { }
        public void Dispose() { }
    }

    public sealed class RHIDepthStencilView : IDisposable
    {
        public void Dispose() { }
    }

    public sealed class RHIShaderResourceView : IDisposable
    {
        public void Dispose() { }
    }

    public struct RHIDSVDesc
    {
        public RHIFormat Format;
        public RHIDSVDimension ViewDimension;
        public int FirstArraySlice;
        public int ArraySize;
    }

    public enum RHIDSVDimension { Texture2D, Texture2DArray, TextureCube }

    public struct RHISRVDesc
    {
        public RHIFormat Format;
        public RHISRVDimension ViewDimension;
        public int ArraySize;
        public int MostDetailedMip;
        public int MipLevels;
    }

    public enum RHISRVDimension { Texture2D, Texture2DArray, TextureCube, Buffer }

    public sealed class RHIMesh : IDisposable
    {
        public void Dispose() { }
    }

}