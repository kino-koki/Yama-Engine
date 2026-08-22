using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 后处理系统
    //
    // 类似 UE Post Process Volume / Unity Post Processing Stack:
    //   * PostProcessVolume: 后处理体积 (无限/盒体/球体, 优先级/权重/混合)
    //   * PostProcessEffect 基类 + 22 种内置效果
    //   * PostProcessChain: 效果链 (有序执行/中间缓冲/最终合成)
    //   * PostProcessManager: 全局管理 (体积收集/权重混合/参数覆盖)
    //   * 后处理配置文件 Profile (保存/加载/热切换)
    // ════════════════════════════════════════════════════════════════════════

    #region PostProcessVolume

    /// <summary>后处理体积形状。</summary>
    public enum VolumeShape
    {
        Infinite,
        Box,
        Sphere
    }

    /// <summary>后处理体积混合模式。</summary>
    public enum VolumeBlendMode
    {
        Normal,
        Additive,
        Multiply,
        Screen,
        Overlay
    }

    /// <summary>
    /// 后处理体积。定义后处理效果的作用区域和优先级。
    /// 类似 UE Post Process Volume / Unity Volume。
    /// </summary>

    #endregion

    #region PostProcessEffect 基类

    /// <summary>后处理效果渲染上下文。</summary>
    public class PostProcessRenderContext
    {
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
        public float DeltaTime { get; set; }
        public float SystemTime { get; set; }
        public Matrix4x4 ViewProjection { get; set; }
        public Matrix4x4 InverseViewProjection { get; set; }
        public Matrix4x4 PrevViewProjection { get; set; }
        public Vector3 CameraPosition { get; set; }
        public Vector2 CameraForward { get; set; }
        public float CurrentExposure { get; set; } = 1f;
        public float BlendWeight { get; set; } = 1f;
        public float GlobalIntensity { get; set; } = 1f;
        public object RenderDevice { get; set; }
    }

    /// <summary>纹理资源 (抽象)。</summary>
    public class RenderTexture
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Format { get; set; } = "RGBA32";
        public object NativeResource { get; set; }
        public bool IsTemporary { get; set; }
    }

    /// <summary>
    /// 后处理效果基类。
    /// </summary>
    public abstract class PostProcessEffect
    {
        public string EffectName { get; protected set; } = "BaseEffect";
        public bool Enabled { get; set; } = true;
        public int ExecutionOrder { get; set; } = 0;
        public float Intensity { get; set; } = 1f;
        public float BlendWeight { get; set; } = 1f;
        public bool RequiresHalfResolution { get; set; }
        public bool RequiresMotionVectors { get; set; }

        /// <summary>渲染效果。</summary>
        public abstract void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context);

        /// <summary>释放资源。</summary>
        public virtual void Release() { }

        /// <summary>获取配置字符串 (用于序列化)。</summary>
        public virtual string GetConfig() => $"{EffectName}: Enabled={Enabled}, Intensity={Intensity:F3}";
    }

    #endregion

    #region 内置效果: Bloom

    /// <summary>辉光效果 (多 Pass 降采样+升采样)。</summary>
    public class BloomEffect : PostProcessEffect
    {
        public float Threshold { get; set; } = 1f;
        public float Intensity { get; set; } = 0.8f;
        public float Scatter { get; set; } = 0.85f;
        public float DirtIntensity { get; set; } = 0f;
        public string DirtTexture { get; set; }
        public Vector3 BloomTint { get; set; } = new Vector3(1f, 0.95f, 0.9f);
        public float[] MipContributions { get; set; } = { 1f, 0.8f, 0.6f, 0.4f, 0.2f };
        public float MaxBlurRadius { get; set; } = 10f;
        public int DownsamplePasses { get; set; } = 5;
        public bool UseHQ { get; set; } = true;

        public BloomEffect()
        {
            EffectName = "Bloom";
            ExecutionOrder = 10;
            RequiresHalfResolution = true;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // Pass 1: 提取高亮区域 (阈值提取)
            // Pass 2..N: 逐级降采样模糊
            // Pass N+1: 逐级升采样合成
            // Final: 混合原始图像 + Bloom 结果
            float effectiveIntensity = Intensity * context.BlendWeight * context.GlobalIntensity;
            // GPU 实现: 提交到渲染管线
        }
    }

    #endregion

    #region 内置效果: ToneMapping

    /// <summary>色调映射操作符。</summary>
    public enum ToneMappingOperator
    {
        Reinhard,
        ACES,
        AcesFilmic,
        Uncharted2,
        AgX,
        Neutral,
        CustomCurve
    }

    /// <summary>色调映射效果。</summary>
    public class ToneMappingEffect : PostProcessEffect
    {
        public ToneMappingOperator Operator { get; set; } = ToneMappingOperator.ACES;
        public float ExposureBias { get; set; } = 0f;
        public bool ApplySRGB { get; set; } = true;
        public float CustomWhitePoint { get; set; } = 11.2f;
        public float CustomBlackPoint { get; set; } = 0f;
        public float Contrast { get; set; } = 1f;

        // Uncharted2 参数
        public float A = 2.51f, B = 0.03f, C = 2.43f, D = 0.59f, E = 0.14f;

        public ToneMappingEffect()
        {
            EffectName = "ToneMapping";
            ExecutionOrder = 100;
            Intensity = 1f;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            float exposure = context.CurrentExposure * (1f + ExposureBias);
            // GPU: 应用色调映射 + gamma 校正
        }

        /// <summary>CPU 端色调映射采样 (用于预览)。</summary>
        public Vector3 TonemapColor(Vector3 color, float exposure)
        {
            Vector3 exposed = color * exposure;

            return Operator switch
            {
                ToneMappingOperator.Reinhard => exposed / (1f + exposed),
                ToneMappingOperator.ACES => ACESFilmic(exposed),
                ToneMappingOperator.AcesFilmic => ACESFilmic(exposed),
                ToneMappingOperator.Uncharted2 => Uncharted2Filmic(exposed),
                ToneMappingOperator.AgX => AgXTonemap(exposed),
                ToneMappingOperator.Neutral => NeutralTonemap(exposed),
                _ => ACESFilmic(exposed)
            };
        }

        private static Vector3 ACESFilmic(Vector3 x)
        {
            float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
            return Math.Clamp((x * (a * x + b)) / (x * (c * x + d) + e), Vector3.Zero, Vector3.One);
        }

        private Vector3 Uncharted2Filmic(Vector3 x)
        {
            Vector3 num = x * (A * x + B);
            Vector3 den = x * (C * x + D) + E;
            Vector3 result = num / den;
            // 白平衡
            Vector3 white = new Vector3(11.2f);
            Vector3 whiteNum = white * (A * white + B);
            Vector3 whiteDen = white * (C * white + D) + E;
            return result * (whiteDen / whiteNum);
        }

        private static Vector3 AgXTonemap(Vector3 x)
        {
            // AgX 近似
            Vector3 a = Vector3.Multiply(x, Vector3.Multiply(x, x) + 0.0245786f);
            Vector3 b = Vector3.Multiply(x, (0.983729f * x + 0.432951f) + 0.238081f);
            return Math.Clamp(a / b, Vector3.Zero, Vector3.One);
        }

        private static Vector3 NeutralTonemap(Vector3 x)
        {
            Vector3 a = x * (2.51f * x + 0.03f);
            Vector3 b = x * (2.43f * x + 0.59f) + 0.14f;
            return Math.Clamp(a / b, Vector3.Zero, Vector3.One);
        }
    }

    #endregion

    #region 内置效果: Exposure

    /// <summary>曝光效果 (手动/自动曝光)。</summary>
    public class ExposureEffect : PostProcessEffect
    {
        public float ManualEV { get; set; } = 0f;
        public bool AutoExposure { get; set; } = true;
        public float TargetBrightness { get; set; } = 0.18f;
        public float AdaptationSpeed { get; set; } = 1.5f;
        public float MinEV { get; set; } = -8f;
        public float MaxEV { get; set; } = 8f;
        public float CurrentEV { get; private set; } = 0f;
        public float HistogramLowPercent { get; set; } = 60f;
        public float HistogramHighPercent { get; set; } = 95f;
        public float SpeedUp { get; set; } = 3f;
        public float SpeedDown { get; set; } = 1f;

        public ExposureEffect()
        {
            EffectName = "Exposure";
            ExecutionOrder = 5;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            context.CurrentExposure = MathF.Pow(2f, CurrentEV);
        }

        public void UpdateAutoExposure(float dt, float averageLuminance)
        {
            if (!AutoExposure)
            {
                CurrentEV = ManualEV;
                return;
            }

            float targetEV = MathF.Log2(Math.Max(0.001f, TargetBrightness / Math.Max(0.001f, averageLuminance)));
            targetEV = Math.Clamp(targetEV, MinEV, MaxEV);

            float speed = targetEV > CurrentEV ? SpeedUp : SpeedDown;
            CurrentEV += (targetEV - CurrentEV) * Math.Min(1f, speed * AdaptationSpeed * dt);
        }
    }

    #endregion

    #region 内置效果: ColorGrading

    /// <summary>颜色分级效果 (Lift/Gamma/Gain + 温度/饱和度/对比度)。</summary>
    public class ColorGradingEffect : PostProcessEffect
    {
        public Vector4 Lift { get; set; } = new Vector4(0, 0, 0, 0);
        public Vector4 Gamma { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 Gain { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 Offset { get; set; } = Vector4.Zero;
        public Vector4 Slope { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 Power { get; set; } = new Vector4(1, 1, 1, 1);
        public float Temperature { get; set; } = 0f;
        public float Tint { get; set; } = 0f;
        public float Saturation { get; set; } = 1f;
        public float Contrast { get; set; } = 1f;
        public float HueShift { get; set; } = 0f;
        public bool UseLegacyMode { get; set; }

        public ColorGradingEffect()
        {
            EffectName = "ColorGrading";
            ExecutionOrder = 110;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: ASC CDL / LGG 颜色校正
        }

        /// <summary>CPU 端颜色分级 (用于预览/LUT 生成)。</summary>
        public Vector4 GradeColor(Vector4 color)
        {
            Vector4 result = color;

            // CDL (Color Decision List): Slope * pow(max(Input, 0), Power) + Offset
            result.X = Slope.X * MathF.Pow(Math.Max(result.X, 0), Power.X) + Offset.X;
            result.Y = Slope.Y * MathF.Pow(Math.Max(result.Y, 0), Power.Y) + Offset.Y;
            result.Z = Slope.Z * MathF.Pow(Math.Max(result.Z, 0), Power.Z) + Offset.Z;
            result.W = Slope.W * MathF.Pow(Math.Max(result.W, 0), Power.W) + Offset.W;

            if (!UseLegacyMode)
            {
                // LGG (Lift/Gamma/Gain)
                Vector3 lifted = new Vector3(Lift.X, Lift.Y, Lift.Z);
                Vector3 gained = new Vector3(Gain.X, Gain.Y, Gain.Z);
                Vector3 gamma = new Vector3(
                    MathF.Max(0.001f, Gamma.X),
                    MathF.Max(0.001f, Gamma.Y),
                    MathF.Max(0.001f, Gamma.Z));
                result.X = MathF.Pow(Math.Max(result.X, 0), 1f / gamma.X) * gained.X + lifted.X;
                result.Y = MathF.Pow(Math.Max(result.Y, 0), 1f / gamma.Y) * gained.Y + lifted.Y;
                result.Z = MathF.Pow(Math.Max(result.Z, 0), 1f / gamma.Z) * gained.Z + lifted.Z;
            }

            // 饱和度
            float luminance = 0.2126f * result.X + 0.7152f * result.Y + 0.0722f * result.Z;
            result.X = luminance + Saturation * (result.X - luminance);
            result.Y = luminance + Saturation * (result.Y - luminance);
            result.Z = luminance + Saturation * (result.Z - luminance);

            // 对比度
            result.X = (result.X - 0.5f) * Contrast + 0.5f;
            result.Y = (result.Y - 0.5f) * Contrast + 0.5f;
            result.Z = (result.Z - 0.5f) * Contrast + 0.5f;

            // 色温
            if (Temperature != 0f)
            {
                result.X += Temperature * 0.1f;
                result.Z -= Temperature * 0.1f;
            }

            return result;
        }
    }

    #endregion

    #region 内置效果: Vignette

    /// <summary>暗角效果。</summary>
    public class VignetteEffect : PostProcessEffect
    {
        public float Intensity { get; set; } = 0.4f;
        public float Smoothness { get; set; } = 0.5f;
        public float Roundness { get; set; } = 1f;
        public Vector4 Color { get; set; } = new Vector4(0, 0, 0, 1);
        public bool Rounded { get; set; } = true;
        public bool Invert { get; set; }
        public string CustomMask { get; set; }

        public VignetteEffect()
        {
            EffectName = "Vignette";
            ExecutionOrder = 120;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: UV 距离中心 → Vignette 遮罩
        }
    }

    #endregion

    #region 内置效果: DepthOfField

    /// <summary>景深效果 (散景)。</summary>
    public class DepthOfFieldEffect : PostProcessEffect
    {
        public float FocusDistance { get; set; } = 10f;
        public float FocusRange { get; set; } = 5f;
        public float NearFocusStart { get; set; } = 0f;
        public float NearFocusEnd { get; set; } = 5f;
        public float FarFocusStart { get; set; } = 15f;
        public float FarFocusEnd { get; set; } = 100f;
        public float FNumber { get; set; } = 2.8f;
        public float FocalLength { get; set; } = 50f;
        public float MaxBokehRadius { get; set; } = 20f;
        public int BokehQuality { get; set; } = 3;
        public bool HexagonalBokeh { get; set; }
        public bool PhysicalCamera { get; set; }
        public float NearTransitionWidth { get; set; } = 1f;
        public float FarTransitionWidth { get; set; } = 2f;

        public DepthOfFieldEffect()
        {
            EffectName = "DepthOfField";
            ExecutionOrder = 80;
            RequiresHalfResolution = true;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // Pass 1: CoC (Circle of Confusion) 计算
            // Pass 2: 散景模糊
        }

        public float GetCircleOfConfusion(float depth)
        {
            if (depth < FocusDistance)
            {
                float nearFactor = 1f - Math.Clamp((FocusDistance - depth) / FocusRange, 0, 1);
                return (1f - nearFactor) * MaxBokehRadius;
            }
            float farFactor = Math.Clamp((depth - FocusDistance) / FocusRange, 0, 1);
            return farFactor * MaxBokehRadius;
        }
    }

    #endregion

    #region 内置效果: MotionBlur

    /// <summary>运动模糊效果。</summary>
    public class MotionBlurEffect : PostProcessEffect
    {
        public float Intensity { get; set; } = 0.5f;
        public float MaxRadius { get; set; } = 5f;
        public bool ObjectMotionBlur { get; set; } = true;
        public bool CameraMotionBlur { get; set; } = true;
        public int SampleCount { get; set; } = 12;

        public MotionBlurEffect()
        {
            EffectName = "MotionBlur";
            ExecutionOrder = 70;
            RequiresMotionVectors = true;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: 运动矢量 → 方向性模糊
        }
    }

    #endregion

    #region 内置效果: SSAO

    /// <summary>屏幕空间环境光遮蔽。</summary>
    public class SSAOEffect : PostProcessEffect
    {
        public float Radius { get; set; } = 0.5f;
        public float Bias { get; set; } = 0.025f;
        public float Intensity { get; set; } = 1.5f;
        public int SampleCount { get; set; } = 16;
        public float Falloff { get; set; } = 1f;
        public float Thickness { get; set; } = 0.5f;
        public int Quality { get; set; } = 2; // 0=Low, 1=Medium, 2=High
        public bool UseDepthNormals { get; set; } = true;
        public float DistanceScale { get; set; } = 1f;
        public bool TemporalFilter { get; set; } = true;
        public float BlurSharpness { get; set; } = 0.05f;

        public SSAOEffect()
        {
            EffectName = "SSAO";
            ExecutionOrder = 20;
            RequiresHalfResolution = true;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // Pass 1: AO 采样
            // Pass 2: 双边模糊
            // Pass 3: Upscale + 合成
        }
    }

    #endregion

    #region 内置效果: SSR

    /// <summary>屏幕空间反射。</summary>
    public class SSREffect : PostProcessEffect
    {
        public int MaxSteps { get; set; } = 64;
        public float MaxDistance { get; set; } = 100f;
        public float Thickness { get; set; } = 0.1f;
        public float RoughnessThreshold { get; set; } = 0.5f;
        public float Intensity { get; set; } = 0.6f;
        public bool TemporalFilter { get; set; } = true;
        public float StepSize { get; set; } = 0.5f;
        public int Quality { get; set; } = 2;
        public bool TraceBehind { get; set; }

        public SSREffect()
        {
            EffectName = "SSR";
            ExecutionOrder = 30;
            RequiresMotionVectors = true;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // Ray Marching → 反射颜色
        }
    }

    #endregion

    #region 内置效果: MotionVectors

    /// <summary>运动矢量生成。</summary>
    public class MotionVectorsEffect : PostProcessEffect
    {
        public bool GenerateObjectMotion { get; set; } = true;
        public bool GenerateCameraMotion { get; set; } = true;

        public MotionVectorsEffect()
        {
            EffectName = "MotionVectors";
            ExecutionOrder = 2;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: 重投影当前帧 → 上一帧, 计算运动矢量
        }
    }

    #endregion

    #region 内置效果: LensDistortion

    /// <summary>镜头畸变效果。</summary>
    public class LensDistortionEffect : PostProcessEffect
    {
        public float BarrelIntensity { get; set; } = 0f;
        public float PincushionIntensity { get; set; } = 0f;
        public float Scale { get; set; } = 1f;
        public Vector2 Center { get; set; } = Vector2.Zero;

        public LensDistortionEffect()
        {
            EffectName = "LensDistortion";
            ExecutionOrder = 95;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: UV 畸变映射
        }
    }

    #endregion

    #region 内置效果: LensFlare

    /// <summary>镜头光晕效果。</summary>
    public class LensFlareEffect : PostProcessEffect
    {
        public float Threshold { get; set; } = 1f;
        public float Intensity { get; set; } = 1f;
        public float GhostCount { get; set; } = 5;
        public float GhostSpread { get; set; } = 0.5f;
        public float ChromaticAberration { get; set; } = 0.1f;
        public float HaloIntensity { get; set; } = 0.5f;
        public float HaloSize { get; set; } = 0.3f;
        public bool HexagonalDiffraction { get; set; }
        public float FlareSize { get; set; } = 0.5f;
        public bool ScreenSpace { get; set; } = true;
        public string FlareTexture { get; set; }

        public LensFlareEffect()
        {
            EffectName = "LensFlare";
            ExecutionOrder = 115;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: 光源位置 → 光晕生成
        }
    }

    #endregion

    #region 内置效果: FilmGrain

    /// <summary>胶片颗粒效果。</summary>
    public class FilmGrainEffect : PostProcessEffect
    {
        public float Intensity { get; set; } = 0.3f;
        public float Size { get; set; } = 1f;
        public float LuminanceResponse { get; set; } = 0.8f;
        public float AnimationSpeed { get; set; } = 8f;
        public float MinIntensity { get; set; } = -0.5f;
        public float MaxIntensity { get; set; } = 0.5f;

        public FilmGrainEffect()
        {
            EffectName = "FilmGrain";
            ExecutionOrder = 130;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: 噪声纹理 + 时间偏移
        }
    }

    #endregion

    #region 内置效果: ChromaticAberration

    /// <summary>色差效果。</summary>
    public class ChromaticAberrationEffect : PostProcessEffect
    {
        public float Intensity { get; set; } = 0.2f;
        public Vector2 Direction { get; set; } = Vector2.Zero;
        public bool Radial { get; set; } = true;
        public bool FastMode { get; set; }

        public ChromaticAberrationEffect()
        {
            EffectName = "ChromaticAberration";
            ExecutionOrder = 105;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: RGB 通道分别偏移采样
        }
    }

    #endregion

    #region 内置效果: Sharpen

    /// <summary>锐化效果 (Unsharp Mask)。</summary>
    public class SharpenEffect : PostProcessEffect
    {
        public float Amount { get; set; } = 0.5f;
        public float Radius { get; set; } = 1f;
        public bool OnlyEdgeSharpen { get; set; }

        public SharpenEffect()
        {
            EffectName = "Sharpen";
            ExecutionOrder = 140;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: Unsharp Mask 卷积
        }
    }

    #endregion

    #region 内置效果: FXAA

    /// <summary>快速近似抗锯齿。</summary>
    public class FXAAEffect : PostProcessEffect
    {
        public float SubpixelQuality { get; set; } = 0.75f;
        public float EdgeThreshold { get; set; } = 0.166f;
        public float EdgeThresholdMin { get; set; } = 0.0833f;
        public float CornerRounding { get; set; } = 0.25f;
        public bool FastMode { get; set; }

        public FXAAEffect()
        {
            EffectName = "FXAA";
            ExecutionOrder = 150;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: Luma 边缘检测 → 混合
        }
    }

    #endregion

    #region 内置效果: TAA

    /// <summary>时间抗锯齿。</summary>
    public class TAAEffect : PostProcessEffect
    {
        public float JitterSpread { get; set; } = 1f;
        public float SharpenStrength { get; set; } = 0.25f;
        public float SharpenClamp { get; set; } = 0.25f;
        public int SampleCount { get; set; } = 8;
        public float BlendFactor { get; set; } = 0.1f;
        public bool GhostRejection { get; set; } = true;
        public float HistoryWeight { get; set; } = 0.9f;
        public bool UseMotionVectors { get; set; } = true;

        // Halton 序列抖动
        private int _frameIndex;
        private static readonly float[] HaltonSequence2 = { 0.5f, 0.25f, 0.75f, 0.125f, 0.625f, 0.375f, 0.875f, 0.0625f };

        public TAAEffect()
        {
            EffectName = "TAA";
            ExecutionOrder = 155;
            RequiresMotionVectors = true;
        }

        public Vector2 GetJitterOffset()
        {
            int idx = _frameIndex % HaltonSequence2.Length;
            _frameIndex++;
            return new Vector2(
                HaltonSequence2[idx] * JitterSpread,
                HaltonSequence2[(idx + 3) % HaltonSequence2.Length] * JitterSpread);
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: 历史帧混合 + 运动矢量重投影 + 邻域钳制
        }
    }

    #endregion

    #region 内置效果: CAS

    /// <summary>对比度自适应锐化 (AMD CAS)。</summary>
    public class CasEffect : PostProcessEffect
    {
        public float Sharpness { get; set; } = 0.4f;
        public bool CustomSharpeningPass { get; set; }

        public CasEffect()
        {
            EffectName = "CAS";
            ExecutionOrder = 160;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: AMD CAS 卷积核
        }
    }

    #endregion

    #region 内置效果: FSR

    /// <summary>保真度超分辨率 (AMD FSR 1.0)。</summary>
    public class FSREffect : PostProcessEffect
    {
        public float Sharpness { get; set; } = 0.2f;
        public float InputScale { get; set; } = 0.75f; // 0.67=Quality, 0.77=Balanced, 0.83=Performance
        public enum FSRQualityPreset
        {
            UltraQuality = 77,
            Quality = 67,
            Balanced = 77,
            Performance = 83
        }
        public FSRQualityPreset QualityPreset { get; set; } = FSRQualityPreset.Quality;

        public FSREffect()
        {
            EffectName = "FSR";
            ExecutionOrder = 200;
            InputScale = QualityPreset / 100f;
        }

        public void SetPreset(FSRQualityPreset preset)
        {
            QualityPreset = preset;
            InputScale = (int)preset / 100f;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // Pass 1: EASU (Edge Adaptive Spatial Upsampling)
            // Pass 2: RCAS (Robust Contrast Adaptive Sharpening)
        }
    }

    #endregion

    #region 内置效果: DLSS 占位

    /// <summary>DLSS 占位接口 (需要 NVIDIA SDK)。</summary>
    public class DLSSFakeEffect : PostProcessEffect
    {
        public enum DLSSQualityPreset
        {
            Quality = 0,
            Balanced = 1,
            Performance = 2,
            UltraPerformance = 3
        }
        public DLSSQualityPreset QualityPreset { get; set; } = DLSSQualityPreset.Quality;
        public bool IsAvailable { get; private set; } = false;
        public string DriverVersion { get; set; } = "";

        public DLSSFakeEffect()
        {
            EffectName = "DLSS";
            ExecutionOrder = 201;
            Enabled = false;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            if (!IsAvailable) return;
            // NVIDIA DLSS SDK 实现 (需单独集成)
        }
    }

    #endregion

    #region 内置效果: MotionVectorBlur

    /// <summary>运动矢量模糊。</summary>
    public class MotionVectorBlur : PostProcessEffect
    {
        public float Intensity { get; set; } = 0.5f;
        public float MaxVelocity { get; set; } = 100f;

        public MotionVectorBlur()
        {
            EffectName = "MotionVectorBlur";
            ExecutionOrder = 72;
            RequiresMotionVectors = true;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: 沿运动矢量方向模糊
        }
    }

    #endregion

    #region 内置效果: ColorLookup (LUT)

    /// <summary>颜色查找表 (3D LUT)。</summary>
    public class ColorLookupEffect : PostProcessEffect
    {
        public string LutTexture { get; set; }
        public float Contribution { get; set; } = 1f;
        public float LutSize { get; set; } = 32; // 32x32x32
        public Vector4 Tint { get; set; } = Vector4.One;
        public bool ConvertToLog { get; set; }

        public ColorLookupEffect()
        {
            EffectName = "ColorLookup";
            ExecutionOrder = 112;
        }

        public override void Render(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            // GPU: 3D LUT 纹理采样
        }
    }

    #endregion

    #region PostProcessChain

    /// <summary>后处理效果链。</summary>
    public class PostProcessChain
    {
        private readonly List<PostProcessEffect> _effects = new List<PostProcessEffect>();
        private readonly List<RenderTexture> _tempBuffers = new List<RenderTexture>();

        public string ChainName { get; set; } = "PostProcessChain";
        public bool Enabled { get; set; } = true;

        /// <summary>添加效果到链。</summary>
        public void AddEffect(PostProcessEffect effect)
        {
            _effects.Add(effect);
            _effects.Sort((a, b) => a.ExecutionOrder.CompareTo(b.ExecutionOrder));
        }

        /// <summary>移除效果。</summary>
        public bool RemoveEffect(PostProcessEffect effect) => _effects.Remove(effect);

        /// <summary>获取所有效果。</summary>
        public IReadOnlyList<PostProcessEffect> GetEffects() => _effects;

        /// <summary>查找效果。</summary>
        public T FindEffect<T>() where T : PostProcessEffect
        {
            foreach (var e in _effects)
                if (e is T t) return t;
            return null;
        }

        /// <summary>检查是否需要运动矢量。</summary>
        public bool RequiresMotionVectors()
        {
            foreach (var e in _effects)
                if (e.Enabled && e.RequiresMotionVectors) return true;
            return false;
        }

        /// <summary>检查是否需要半分辨率。</summary>
        public bool RequiresHalfResolution()
        {
            foreach (var e in _effects)
                if (e.Enabled && e.RequiresHalfResolution) return true;
            return false;
        }

        /// <summary>执行效果链。</summary>
        public void Execute(RenderTexture source, RenderTexture destination, PostProcessRenderContext context)
        {
            if (!Enabled || _effects.Count == 0)
            {
                // 直接拷贝
                return;
            }

            RenderTexture current = source;
            RenderTexture next = null;

            for (int i = 0; i < _effects.Count; i++)
            {
                var effect = _effects[i];
                if (!effect.Enabled) continue;

                // 获取/创建中间缓冲
                if (i < _effects.Count - 1)
                {
                    next = AcquireTempBuffer(source.Width, source.Height);
                }
                else
                {
                    next = destination;
                }

                context.BlendWeight = effect.BlendWeight;
                context.GlobalIntensity = effect.Intensity;
                effect.Render(current, next, context);

                if (current != source) ReleaseTempBuffer(current);
                current = next;
            }
        }

        private RenderTexture AcquireTempBuffer(int width, int height)
        {
            foreach (var buf in _tempBuffers)
            {
                if (!buf.IsTemporary) continue;
                buf.IsTemporary = false;
                return buf;
            }
            return new RenderTexture { Width = width, Height = height, IsTemporary = true };
        }

        private void ReleaseTempBuffer(RenderTexture buffer)
        {
            buffer.IsTemporary = true;
        }

        /// <summary>释放资源。</summary>
        public void Release()
        {
            foreach (var e in _effects)
                e.Release();
            _tempBuffers.Clear();
        }
    }

    #endregion

    #region PostProcessProfile

    /// <summary>后处理配置文件。</summary>
    public class PostProcessProfile
    {
        public string ProfileName { get; set; } = "Default";
        public Dictionary<string, Dictionary<string, object>> EffectSettings { get; } =
            new Dictionary<string, Dictionary<string, object>>();

        /// <summary>保存效果设置到 Profile。</summary>
        public void SaveEffectSettings(PostProcessEffect effect)
        {
            // 序列化效果参数
            var settings = new Dictionary<string, object>();
            settings[nameof(effect.Enabled)] = effect.Enabled;
            settings[nameof(effect.Intensity)] = effect.Intensity;
            settings[nameof(effect.BlendWeight)] = effect.BlendWeight;
            settings[nameof(effect.ExecutionOrder)] = effect.ExecutionOrder;
            EffectSettings[effect.EffectName] = settings;
        }

        /// <summary>加载效果设置。</summary>
        public bool LoadEffectSettings(PostProcessEffect effect)
        {
            if (!EffectSettings.TryGetValue(effect.EffectName, out var settings))
                return false;

            if (settings.TryGetValue(nameof(effect.Enabled), out var enabled))
                effect.Enabled = (bool)enabled;
            if (settings.TryGetValue(nameof(effect.Intensity), out var intensity))
                effect.Intensity = (float)intensity;
            if (settings.TryGetValue(nameof(effect.BlendWeight), out var blend))
                effect.BlendWeight = (float)blend;
            if (settings.TryGetValue(nameof(effect.ExecutionOrder), out var order))
                effect.ExecutionOrder = (int)order;

            return true;
        }

        /// <summary>克隆 Profile。</summary>
        public PostProcessProfile Clone()
        {
            var clone = new PostProcessProfile { ProfileName = ProfileName + "_Copy" };
            foreach (var kvp in EffectSettings)
            {
                clone.EffectSettings[kvp.Key] = new Dictionary<string, object>(kvp.Value);
            }
            return clone;
        }

        /// <summary>重置所有效果到默认值。</summary>
        public void Reset()
        {
            EffectSettings.Clear();
        }
    }

    #endregion

    #region PostProcessManager

    /// <summary>全局后处理管理器。</summary>
    public class PostProcessManager
    {
        // ── 体积 ──
        private readonly List<PostProcessVolume> _volumes = new List<PostProcessVolume>();
        private readonly List<PostProcessVolume> _activeVolumes = new List<PostProcessVolume>();

        // ── 效果链 ──
        public PostProcessChain MasterChain { get; } = new PostProcessChain { ChainName = "Master" };
        public PostProcessProfile CurrentProfile { get; set; } = new PostProcessProfile { ProfileName = "Default" };
        public PostProcessProfile PendingProfile { get; private set; }

        // ── 配置 ──
        public bool EnablePostProcessing { get; set; } = true;
        public float ProfileBlendTime { get; set; } = 1f;
        private float _profileBlendTimer;
        private float _profileBlendProgress;

        // ── 全局覆盖 ──
        public float GlobalIntensityMultiplier { get; set; } = 1f;
        public bool OverrideVolume { get; set; }
        public PostProcessVolume OverrideVolumeInstance { get; set; }

        // ── 渲染上下文 ──
        public PostProcessRenderContext RenderContext { get; } = new PostProcessRenderContext();

        public int VolumeCount => _volumes.Count;
        public int ActiveVolumeCount => _activeVolumes.Count;

        /// <summary>注册体积。</summary>
        public void RegisterVolume(PostProcessVolume volume)
        {
            _volumes.Add(volume);
        }

        /// <summary>注销体积。</summary>
        public bool UnregisterVolume(PostProcessVolume volume) => _volumes.Remove(volume);

        /// <summary>创建默认效果链。</summary>
        public void SetupDefaultChain()
        {
            MasterChain.AddEffect(new MotionVectorsEffect());
            MasterChain.AddEffect(new ExposureEffect());
            MasterChain.AddEffect(new BloomEffect());
            MasterChain.AddEffect(new SSAOEffect());
            MasterChain.AddEffect(new SSREffect());
            MasterChain.AddEffect(new MotionBlurEffect());
            MasterChain.AddEffect(new DepthOfFieldEffect());
            MasterChain.AddEffect(new LensDistortionEffect());
            MasterChain.AddEffect(new ChromaticAberrationEffect());
            MasterChain.AddEffect(new ToneMappingEffect());
            MasterChain.AddEffect(new ColorGradingEffect());
            MasterChain.AddEffect(new ColorLookupEffect());
            MasterChain.AddEffect(new VignetteEffect());
            MasterChain.AddEffect(new FilmGrainEffect());
            MasterChain.AddEffect(new LensFlareEffect());
            MasterChain.AddEffect(new SharpenEffect());
            MasterChain.AddEffect(new FXAAEffect());
            MasterChain.AddEffect(new TAAEffect());
            MasterChain.AddEffect(new CasEffect());
        }

        /// <summary>收集活动体积 (按优先级排序)。</summary>
        public void CollectActiveVolumes(Vector3 cameraPosition)
        {
            _activeVolumes.Clear();

            if (OverrideVolume && OverrideVolumeInstance != null)
            {
                _activeVolumes.Add(OverrideVolumeInstance);
                return;
            }

            foreach (var volume in _volumes)
            {
                if (!volume.IsEnabled) continue;
                if (volume.ContainsPoint(cameraPosition))
                {
                    _activeVolumes.Add(volume);
                }
            }

            // 按优先级排序 (高优先级在前)
            _activeVolumes.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>混合活动体积的效果参数到 MasterChain。</summary>
        public void BlendActiveVolumes()
        {
            // 从活动体积收集效果设置并按权重混合到 MasterChain
            foreach (var volume in _activeVolumes)
            {
                foreach (var volumeEffect in volume.Effects)
                {
                    var chainEffect = MasterChain.FindEffectByType(volumeEffect.GetType());
                    if (chainEffect != null)
                    {
                        // 按权重混合参数 (简化: 取加权平均)
                        float weight = volume.BlendWeight * volume.GlobalIntensity;
                        chainEffect.BlendWeight = Math.Clamp(chainEffect.BlendWeight + weight, 0, 1);
                    }
                }
            }
        }

        /// <summary>执行后处理。</summary>
        public void Render(RenderTexture source, RenderTexture destination, Vector3 cameraPosition)
        {
            if (!EnablePostProcessing)
            {
                // 直接拷贝
                return;
            }

            // 更新 Profile 过渡
            UpdateProfileTransition(RenderContext.DeltaTime);

            // 收集活动体积
            CollectActiveVolumes(cameraPosition);

            // 混合
            BlendActiveVolumes();

            // 设置渲染上下文
            RenderContext.GlobalIntensity = GlobalIntensityMultiplier;

            // 执行效果链
            MasterChain.Execute(source, destination, RenderContext);

            // 重置混合权重
            ResetBlendWeights();
        }

        private void ResetBlendWeights()
        {
            foreach (var effect in MasterChain.GetEffects())
            {
                effect.BlendWeight = 0f;
            }
        }

        /// <summary>切换 Profile (带过渡)。</summary>
        public void SwitchProfile(PostProcessProfile newProfile)
        {
            if (PendingProfile != null)
            {
                // 立即应用上一个待切换的 Profile
                CurrentProfile = PendingProfile;
            }
            PendingProfile = newProfile;
            _profileBlendTimer = ProfileBlendTime;
            _profileBlendProgress = 0f;
        }

        private void UpdateProfileTransition(float dt)
        {
            if (PendingProfile == null) return;

            _profileBlendTimer -= dt;
            _profileBlendProgress = 1f - Math.Clamp(_profileBlendTimer / ProfileBlendTime, 0f, 1f);

            if (_profileBlendTimer <= 0f)
            {
                CurrentProfile = PendingProfile;
                PendingProfile = null;
                _profileBlendProgress = 1f;
            }
        }

        /// <summary>从 Profile 应用效果设置。</summary>
        public void ApplyProfile(PostProcessProfile profile)
        {
            foreach (var effect in MasterChain.GetEffects())
            {
                profile.LoadEffectSettings(effect);
            }
        }

        /// <summary>保存当前设置到 Profile。</summary>
        public void SaveToProfile(PostProcessProfile profile)
        {
            foreach (var effect in MasterChain.GetEffects())
            {
                profile.SaveEffectSettings(effect);
            }
        }

        /// <summary>更新自动曝光。</summary>
        public void UpdateAutoExposure(float dt, float averageLuminance)
        {
            var exposure = MasterChain.FindEffect<ExposureEffect>();
            if (exposure != null)
                exposure.UpdateAutoExposure(dt, averageLuminance);
        }
    }

    /// <summary>PostProcessChain 扩展方法。</summary>
    public static class PostProcessChainExtensions
    {
        public static PostProcessEffect FindEffectByType(this PostProcessChain chain, Type effectType)
        {
            foreach (var e in chain.GetEffects())
                if (e.GetType() == effectType) return e;
            return null;
        }
    }

    #endregion
}
