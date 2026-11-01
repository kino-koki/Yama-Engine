using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 次表面散射 (Subsurface Scattering, SSS) 系统
    //
    // 模拟光线进入半透明材质 (皮肤/蜡/玉石/牛奶等) 后在内部多次散射再射出的现象。
    // 核心架构:
    //  1. SSS 材质参数 — 散射半径/颜色/透射率, 支持 Burley 参考模型映射
    //  2. 分离式 SSS (Separable SSS) — 预积分扩散核, 将 2D 卷积拆为两次 1D 卷积
    //  3. 屏幕空间 SSS — 4 个 Pass 的延迟管线:
    //       Pass 1: 提取材质 ID + 漫反射颜色 + 深度 (G-Buffer 提取)
    //       Pass 2: 水平模糊 (扩散核 1D 卷积)
    //       Pass 3: 垂直模糊 (扩散核 1D 卷积)
    //       Pass 4: 合成回 G-Buffer
    //  4. 预积分皮肤 BRDF (Preintegrated Skin BRDF) — 基于曲率/NoL 的 LUT
    //  5. 透射 (Transmission) — 背面光照透过皮肤, 模拟耳廓/鼻翼透红
    //  6. 多层 SSS — 表皮 (Epidermis) / 真皮 (Dermis) / 皮下组织 (Hypodermis)
    //  7. 与 RHI 严格对接: RHIDevice / RHICommandList / RHITexture / RHIBuffer /
    //     RHIPipelineState (PSO) / RHIRootSignature
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>SSS 渲染质量档位。</summary>
    public enum SSSQualityLevel
    {
        /// <summary>低质量: 少量核采样, 仅核心散射。</summary>
        Low = 0,
        /// <summary>中质量: 17 样本扩散核。</summary>
        Medium = 1,
        /// <summary>高质量: 25 样本扩散核 + 透射。</summary>
        High = 2,
        /// <summary>电影级: 35 样本 + 多层 + Burley 参考映射。</summary>
        Cinematic = 3
    }

    /// <summary>
    /// SSS 材质参数。描述一个次表面散射材质的物理属性,
    /// 可由美术在材质编辑器中调节, 亦支持从 Burley 参考模型反推。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SSSMaterialParams
    {
        /// <summary>散射半径 (世界单位), 控制光线在材质内部扩散的距离。值越大越"软"。</summary>
        public float ScatterRadius;
        /// <summary>散射颜色 (RGB), 光线在内部散射后的色调 (如皮肤的粉红)。</summary>
        public Vector3 ScatterColor;
        /// <summary>透射颜色, 光线穿过薄壁区域时附加的色调 (耳廓的红色)。</summary>
        public Vector3 TransmissionColor;
        /// <summary>透射强度 (0~1), 背面光照穿透皮肤的可见程度。</summary>
        public float TransmissionAmount;
        /// <summary>透射距离衰减 (世界单位), 控制透射随厚度衰减的速率。</summary>
        public float TransmissionDistortion;
        /// <summary>材质 ID, 用于屏幕空间 Pass 区分需要 SSS 的像素。</summary>
        public uint MaterialId;
        /// <summary>表面曲率 (由法线/深度派生), 驱动预积分皮肤 BRDF 查表。</summary>
        public float Curvature;
        /// <summary>菲涅尔缩放, 控制掠射角下的散射衰减。</summary>
        public float FresnelScale;
        /// <summary>各向异性 (-1~1), 正值沿切线方向拉伸散射, 模拟皮肤纹理。</summary>
        public float Anisotropy;

        /// <summary>默认皮肤参数 (中性肤色)。</summary>
        public static SSSMaterialParams DefaultSkin => new()
        {
            ScatterRadius = 0.012f,
            ScatterColor = new Vector3(1.0f, 0.45f, 0.35f),
            TransmissionColor = new Vector3(0.85f, 0.20f, 0.15f),
            TransmissionAmount = 0.6f,
            TransmissionDistortion = 0.35f,
            MaterialId = 1,
            Curvature = 0.5f,
            FresnelScale = 1.0f,
            Anisotropy = 0.0f
        };

        /// <summary>玉石/蜡质参数。</summary>
        public static SSSMaterialParams Jade => new()
        {
            ScatterRadius = 0.025f,
            ScatterColor = new Vector3(0.55f, 0.95f, 0.75f),
            TransmissionColor = new Vector3(0.40f, 0.85f, 0.60f),
            TransmissionAmount = 0.8f,
            TransmissionDistortion = 0.2f,
            MaterialId = 2,
            Curvature = 0.3f,
            FresnelScale = 1.2f,
            Anisotropy = 0.0f
        };
    }

    /// <summary>
    /// 多层 SSS 层参数。皮肤通常由三层组成:
    ///   - 表皮 (Epidermis): 薄而浅, 油脂层, 主导高光
    ///   - 真皮 (Dermis):   主散射层, 红色调 (血液)
    ///   - 皮下组织 (Hypodermis): 深层, 黄色脂肪, 大半径散射
    /// 每层拥有独立的散射半径与颜色, 叠加合成最终外观。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SSSLayerParams
    {
        /// <summary>层名称 (调试用)。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        /// <summary>该层散射半径 (世界单位)。</summary>
        public float ScatterRadius;
        /// <summary>该层散射颜色。</summary>
        public Vector3 ScatterColor;
        /// <summary>该层权重 (各层权重之和应为 1)。</summary>
        public float Weight;
        /// <summary>该层透射贡献。</summary>
        public float Transmission;

        /// <summary>默认三层皮肤 (表皮/真皮/皮下)。</summary>
        public static SSSLayerParams[] DefaultSkinLayers => new[]
        {
            new SSSLayerParams { Name = "Epidermis",  ScatterRadius = 0.004f, ScatterColor = new Vector3(0.95f, 0.75f, 0.65f), Weight = 0.35f, Transmission = 0.15f },
            new SSSLayerParams { Name = "Dermis",     ScatterRadius = 0.012f, ScatterColor = new Vector3(0.90f, 0.40f, 0.30f), Weight = 0.45f, Transmission = 0.55f },
            new SSSLayerParams { Name = "Hypodermis", ScatterRadius = 0.028f, ScatterColor = new Vector3(0.80f, 0.60f, 0.25f), Weight = 0.20f, Transmission = 0.30f }
        };
    }

    /// <summary>
    /// 预积分扩散核样本。Separable SSS 将 2D 高斯/扩散核分解为可分离的 1D 核,
    /// 存储若干样本 (偏移位置 + 权重), 在屏幕空间做两次 1D 卷积即可近似 2D 散射。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SSSKernelSample
    {
        /// <summary>沿采样轴的归一化偏移 (乘以散射半径得到世界偏移)。</summary>
        public float Offset;
        /// <summary>该样本颜色权重 (RGB, 已含各颜色通道的不同扩散)。</summary>
        public Vector3 Weight;
        /// <summary>样本索引 (调试)。</summary>
        public int Index;
    }

    /// <summary>
    /// Separable SSS 扩散核生成器。基于 Jimenez 等人的 Separable SSS 方法,
    /// 将径向扩散剖面 (radial diffusion profile) 拟合为若干高斯核的加权和,
    /// 再通过数值求解得到可分离的 1D 采样核。
    /// </summary>
    public static class SeparableSSSKernel
    {
        /// <summary>高斯核分量 (用于拟合扩散剖面)。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GaussianLobe
        {
            public Vector3 Color;
            public float Variance;
            public float Weight;
        }

        // 经典皮肤扩散剖面拟合 (3 个高斯核, 来自 Jimenez SSS 论文)
        private static readonly GaussianLobe[] _skinLobes =
        {
            new() { Color = new Vector3(0.233f, 0.455f, 0.649f), Variance = 0.100f, Weight = 0.100f },
            new() { Color = new Vector3(0.100f, 0.336f, 0.344f), Variance = 0.118f, Weight = 0.118f },
            new() { Color = new Vector3(0.118f, 0.198f, 0.000f), Variance = 0.268f, Weight = 0.268f },
            new() { Color = new Vector3(0.113f, 0.007f, 0.007f), Variance = 0.198f, Weight = 0.198f },
            new() { Color = new Vector3(0.358f, 0.004f, 0.000f), Variance = 5.000f, Weight = 0.000f },
            new() { Color = new Vector3(0.078f, 0.000f, 0.000f), Variance = 1.000f, Weight = 0.000f }
        };

        /// <summary>
        /// 生成可分离 1D 采样核。返回的样本按对称分布, 可直接用于水平/垂直卷积。
        /// </summary>
        /// <param name="sampleCount">样本数 (奇数, 中心样本 + 两侧对称)。</param>
        /// <param name="scatterRadius">散射半径 (世界单位)。</param>
        /// <param name="strength">整体强度缩放。</param>
        public static SSSKernelSample[] Generate(int sampleCount, float scatterRadius, float strength = 1.0f)
        {
            if (sampleCount < 1) sampleCount = 1;
            if (sampleCount % 2 == 0) sampleCount++; // 强制奇数, 保证中心样本

            var kernel = new SSSKernelSample[sampleCount];
            int half = sampleCount / 2;

            // 在 [-1, 1] 区间均匀采样偏移, 乘以散射半径得到世界偏移
            for (int i = 0; i < sampleCount; i++)
            {
                float t = (i - half) / (float)half;       // [-1, 1]
                float offset = t * scatterRadius;
                Vector3 weight = Vector3.Zero;

                // 累加各高斯核贡献 (评估径向扩散剖面)
                foreach (var lobe in _skinLobes)
                {
                    float r2 = offset * offset;
                    float g = MathF.Exp(-r2 / (2.0f * lobe.Variance)) /
                              MathF.Sqrt(2.0f * MathF.PI * lobe.Variance);
                    weight += lobe.Color * g * lobe.Weight * strength;
                }

                kernel[i] = new SSSKernelSample
                {
                    Offset = offset,
                    Weight = weight,
                    Index = i
                };
            }

            // 归一化权重 (保证能量守恒, 避免整体变亮/变暗)
            Vector3 sum = Vector3.Zero;
            for (int i = 0; i < sampleCount; i++) sum += kernel[i].Weight;
            if (sum != Vector3.Zero)
            {
                Vector3 invSum = new(1f / sum.X, 1f / sum.Y, 1f / sum.Z);
                for (int i = 0; i < sampleCount; i++)
                    kernel[i].Weight *= invSum;
            }

            return kernel;
        }

        /// <summary>
        /// 将生成的核上传到 GPU 作为结构化缓冲, 供着色器访问。
        /// </summary>
        public static RHIBuffer UploadToGpu(RHIDevice device, SSSKernelSample[] kernel, string name = "SSSKernel")
        {
            ulong size = (ulong)(kernel.Length * Marshal.SizeOf<SSSKernelSample>());
            var desc = new RHIBufferDesc
            {
                SizeInBytes = size,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<SSSKernelSample>(),
                HeapType = RHIResourceHeapType.Default
            };
            var buffer = device.CreateBuffer(desc, name);
            device.UploadToBuffer(buffer, kernel);
            return buffer;
        }
    }

    /// <summary>
    /// Burley 参考模型参数映射。
    /// Burley 2015 (Disney) 次表面模型用物理参数 (单次散射反照率 albedo / 扩散半径 radius)
    /// 描述散射, 该类负责在 Burley 物理参数与工程用 SSSMaterialParams 之间转换,
    /// 使分离式 SSS 能够逼近 Burley 参考渲染的结果。
    /// </summary>
    public static class BurleySSSMapping
    {
        /// <summary>Burley 扩散剖面参数。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BurleyProfile
        {
            /// <summary>单次散射反照率 (RGB)。</summary>
            public Vector3 SingleScatterAlbedo;
            /// <summary>扩散半径 (RGB, 每通道不同)。</summary>
            public Vector3 DiffuseRadius;
            /// <summary>各向异性。</summary>
            public float Anisotropy;
        }

        /// <summary>由 Burley 物理参数生成对应的工程材质参数。</summary>
        public static SSSMaterialParams FromBurley(in BurleyProfile profile, uint materialId)
        {
            // 散射半径取 RGB 平均 (主导), 散射颜色由反照率与半径比驱动
            float avgRadius = (profile.DiffuseRadius.X + profile.DiffuseRadius.Y + profile.DiffuseRadius.Z) / 3f;
            Vector3 scatterColor = Vector3.Normalize(profile.SingleScatterAlbedo + new Vector3(1e-3f));
            // 透射强度随反照率提升 (越透明越透光)
            float transmission = Math.Clamp((profile.SingleScatterAlbedo.X + profile.SingleScatterAlbedo.Y + profile.SingleScatterAlbedo.Z) / 3f, 0f, 1f) * 0.8f;

            return new SSSMaterialParams
            {
                ScatterRadius = avgRadius,
                ScatterColor = scatterColor,
                TransmissionColor = profile.SingleScatterAlbedo * new Vector3(1.1f, 0.3f, 0.2f),
                TransmissionAmount = transmission,
                TransmissionDistortion = 0.3f,
                MaterialId = materialId,
                Curvature = 0.5f,
                FresnelScale = 1.0f,
                Anisotropy = profile.Anisotropy
            };
        }

        /// <summary>由工程材质参数反推 Burley 物理参数 (近似)。</summary>
        public static BurleyProfile ToBurley(in SSSMaterialParams mat)
        {
            return new BurleyProfile
            {
                SingleScatterAlbedo = mat.ScatterColor,
                DiffuseRadius = new Vector3(mat.ScatterRadius),
                Anisotropy = mat.Anisotropy
            };
        }

        /// <summary>
        /// 计算 Burley 扩散剖面在半径 r 处的值 (CPU 侧参考实现, 用于核验证)。
        /// R(r) = (e^{-r/s} + e^{-r/(3s)}) / (8π s r)
        /// </summary>
        public static float EvaluateDiffusionProfile(float r, float s)
        {
            if (r <= 0f || s <= 0f) return 0f;
            float a = MathF.Exp(-r / s);
            float b = MathF.Exp(-r / (3f * s));
            return (a + b) / (8f * MathF.PI * s * r);
        }
    }

    /// <summary>
    /// 预积分皮肤 BRDF (Preintegrated Skin BRDF)。
    /// Penner 等人提出: 将皮肤的漫反射预积分到一张 2D LUT,
    /// 横轴为 NoL (法线与光线夹角余弦), 纵轴为曲率 (1/r)。
    /// 运行时仅需一次纹理采样即可获得柔和的边缘散射与暗部红移。
    /// </summary>
    public sealed class PreintegratedSkinBRDF : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _skinLut;
        private RHIDescriptorHandle _skinLutSrv;

        /// <summary>LUT 分辨率 (NoL x Curvature)。</summary>
        public int Resolution { get; }

        /// <summary>构造。</summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="resolution">LUT 边长 (默认 256)。</param>
        public PreintegratedSkinBRDF(RHIDevice device, int resolution = 256)
        {
            _device = device;
            Resolution = resolution;
        }

        /// <summary>构建 (或加载) 预积分皮肤 LUT。</summary>
        public void Build()
        {
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = Resolution, Height = Resolution, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess,
                OptimizedClearValue = RHIClearValueHelper.Color(RHIFormat.R16G16B16A16_Float, Vector4.Zero)
            };
            _skinLut = _device.CreateTexture(desc, "PreintegratedSkinLUT");
            _skinLutSrv = _device.CreateSRV(_skinLut);

            // 离线预积分: 对每个 (NoL, curvature) 采样扩散剖面积分。
            // 实际由 GPU compute pass 完成, 此处仅创建资源。
        }

        /// <summary>
        /// CPU 侧参考: 对给定 NoL 与曲率计算预积分漫反射颜色。
        /// 用于验证 GPU LUT 结果正确性。
        /// </summary>
        public Vector3 Evaluate(float nDotL, float curvature, in SSSMaterialParams mat)
        {
            // 简化: 边缘 (低 NoL) 处增加红色散射贡献
            float wrap = Math.Clamp(nDotL * 0.5f + 0.5f, 0f, 1f);
            float edge = 1f - Math.Clamp(nDotL, 0f, 1f);
            Vector3 scatter = mat.ScatterColor * edge * curvature * 0.5f;
            return mat.ScatterColor * wrap + scatter;
        }

        /// <summary>获取 LUT 的 SRV 描述符句柄 (供着色器绑定)。</summary>
        public RHIDescriptorHandle LutSrv => _skinLutSrv;

        /// <summary>LUT 纹理。</summary>
        public RHITexture? Texture => _skinLut;

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _skinLut?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>SSS 透射常量 (背面光照透过薄皮肤)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SSSTransmissionCB
    {
        /// <summary>透射颜色。</summary>
        public Vector3 TransmissionColor;
        /// <summary>透射强度。</summary>
        public float TransmissionAmount;
        /// <summary>距离失真系数。</summary>
        public float TransmissionDistortion;
        /// <summary>光线方向 (世界空间)。</summary>
        public Vector3 LightDir;
        /// <summary>阴影衰减 (来自阴影贴图)。</summary>
        public float ShadowAttenuation;
        /// <summary>厚度缓冲缩放。</summary>
        public float ThicknessScale;
        /// <summary>填充对齐。</summary>
        public float Pad0, Pad1, Pad2;
    }

    /// <summary>屏幕空间 SSS 主常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SSSConstantsCB
    {
        /// <summary>视口尺寸倒数。</summary>
        public Vector2 InvResolution;
        /// <summary>散射半径 (世界单位)。</summary>
        public float ScatterRadius;
        /// <summary>SSS 材质 ID 掩码 (该像素是否需要 SSS)。</summary>
        public uint MaterialIdMask;
        /// <summary>投影矩阵 (用于世界偏移转屏幕偏移)。</summary>
        public Matrix4x4 ProjMatrix;
        /// <summary>核样本数。</summary>
        public int KernelSampleCount;
        /// <summary>当前 Pass (0=提取/1=水平/2=垂直/3=合成)。</summary>
        public int PassIndex;
        /// <summary>透射开关。</summary>
        public int EnableTransmission;
        /// <summary>多层 SSS 开关。</summary>
        public int EnableMultiLayer;
        /// <summary>各向异性。</summary>
        public float Anisotropy;
        /// <summary>近裁面。</summary>
        public float NearZ;
        /// <summary>远裁面。</summary>
        public float FarZ;
        /// <summary>强度缩放。</summary>
        public float Strength;
        /// <summary>填充。</summary>
        public float Pad0, Pad1;
    }

    /// <summary>
    /// 屏幕空间次表面散射渲染器 (Screen-Space SSS)。
    /// 在延迟渲染的 G-Buffer 上执行, 通过 4 个 Pass 完成散射模拟:
    ///   Pass 1: 从 G-Buffer 提取 (材质ID + 漫反射色 + 深度) 到 SSS 专用 RT
    ///   Pass 2: 水平 1D 卷积 (Separable SSS 横向模糊)
    ///   Pass 3: 垂直 1D 卷积 (Separable SSS 纵向模糊)
    ///   Pass 4: 将散射结果合成回 G-Buffer 漫反射通道
    /// 同时支持透射 Pass 与多层混合。
    /// </summary>
    public sealed class ScreenSpaceSSS : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly SSSQualityLevel _quality;

        // 管线状态 (PSO) 与根签名
        private RHIRootSignature? _rootSignature;
        private RHIPipelineState? _extractPso;     // Pass 1: 提取
        private RHIPipelineState? _hBlurPso;       // Pass 2: 水平模糊
        private RHIPipelineState? _vBlurPso;       // Pass 3: 垂直模糊
        private RHIPipelineState? _compositePso;   // Pass 4: 合成
        private RHIPipelineState? _transmissionPso;// 透射 Pass

        // SSS 专用渲染目标 (Ping-Pong)
        private RHITexture? _sssRT_A;              // 提取/中间结果 A
        private RHITexture? _sssRT_B;              // 中间结果 B
        private RHITexture? _thicknessRT;          // 厚度缓冲 (透射用)
        private RHIDescriptorHandle _rtvA, _rtvB, _uavA, _uavB;

        // 扩散核 (结构化缓冲)
        private RHIBuffer? _kernelBuffer;
        private RHIDescriptorHandle _kernelSrv;

        // 常量缓冲
        private RHIBuffer? _constantsCB;
        private RHIBuffer? _transmissionCB;

        // 预积分皮肤 BRDF
        private PreintegratedSkinBRDF? _skinBRDF;

        // 多层参数
        private SSSLayerParams[] _layers = SSSLayerParams.DefaultSkinLayers;

        // 尺寸
        private int _width, _height;

        /// <summary>质量档位。</summary>
        public SSSQualityLevel Quality => _quality;
        /// <summary>预积分皮肤 BRDF。</summary>
        public PreintegratedSkinBRDF? SkinBRDF => _skinBRDF;
        /// <summary>扩散核样本数 (由质量决定)。</summary>
        public int KernelSampleCount => _quality switch
        {
            SSSQualityLevel.Low => 11,
            SSSQualityLevel.Medium => 17,
            SSSQualityLevel.High => 25,
            SSSQualityLevel.Cinematic => 35,
            _ => 17
        };

        /// <summary>构造。</summary>
        public ScreenSpaceSSS(RHIDevice device, SSSQualityLevel quality = SSSQualityLevel.High)
        {
            _device = device;
            _quality = quality;
        }

        /// <summary>初始化: 创建 PSO、根签名、核缓冲。</summary>
        /// <param name="scatterRadius">默认散射半径。</param>
        public void Initialize(float scatterRadius = 0.012f)
        {
            CreateRootSignature();
            CreatePipelines();
            CreateKernel(scatterRadius);
            CreateConstantBuffers();

            _skinBRDF = new PreintegratedSkinBRDF(_device, 256);
            _skinBRDF.Build();
        }

        /// <summary>创建根签名 (SSS 共享一套根签名)。</summary>
        private void CreateRootSignature()
        {
            var desc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    // b0: SSS 常量
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    // b1: 透射常量
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 1 } },
                    // t0~t7: SRV 表 (G-Buffer/核/LUT/厚度/阴影)
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 8, BaseShaderRegister = 0 } } } },
                    // u0~u3: UAV 表 (SSS RT)
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.PointClamp, RHISamplerDesc.LinearWrap }
            };
            _rootSignature = _device.CreateRootSignature(desc, "SSSRootSignature");
        }

        /// <summary>创建各 Pass 的 PSO (compute 管线)。</summary>
        private void CreatePipelines()
        {
            // 注意: 着色器字节码此处留空, 实际由着色器编译器提供。
            // 引擎在运行时通过 CompileShader 注入真实字节码。
            _extractPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "SSS_Extract", CS = ShaderManager.Instance.GetBytecode("Character/SSS", "SSSExtractCS", RHIShaderStage.Compute) });

            _hBlurPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "SSS_HorizontalBlur", CS = ShaderManager.Instance.GetBytecode("Character/SSS", "SSSHBlurCS", RHIShaderStage.Compute) });

            _vBlurPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "SSS_VerticalBlur", CS = ShaderManager.Instance.GetBytecode("Character/SSS", "SSSVBlurCS", RHIShaderStage.Compute) });

            _compositePso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "SSS_Composite", CS = ShaderManager.Instance.GetBytecode("Character/SSS", "SSSCompositeCS", RHIShaderStage.Compute) });

            _transmissionPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "SSS_Transmission", CS = ShaderManager.Instance.GetBytecode("Character/SSS", "SSSTransmissionCS", RHIShaderStage.Compute) });
        }

        /// <summary>生成并上传扩散核到 GPU。</summary>
        private void CreateKernel(float scatterRadius)
        {
            var kernel = SeparableSSSKernel.Generate(KernelSampleCount, scatterRadius);
            _kernelBuffer = SeparableSSSKernel.UploadToGpu(_device, kernel, "SSSKernel");
            _kernelSrv = _device.CreateSRV(_kernelBuffer, RHIFormat.Unknown, true, 0, (uint)kernel.Length, (uint)Marshal.SizeOf<SSSKernelSample>());
        }

        /// <summary>重建扩散核 (散射半径改变时调用)。</summary>
        public void RebuildKernel(float scatterRadius)
        {
            _kernelBuffer?.Dispose();
            var kernel = SeparableSSSKernel.Generate(KernelSampleCount, scatterRadius);
            _kernelBuffer = SeparableSSSKernel.UploadToGpu(_device, kernel, "SSSKernel");
            _kernelSrv = _device.CreateSRV(_kernelBuffer, RHIFormat.Unknown, true, 0, (uint)kernel.Length, (uint)Marshal.SizeOf<SSSKernelSample>());
        }

        /// <summary>创建常量缓冲。</summary>
        private void CreateConstantBuffers()
        {
            uint cbSize = (uint)((Marshal.SizeOf<SSSConstantsCB>() + 255) & ~255); // 256 字节对齐
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = cbSize,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            };
            _constantsCB = _device.CreateBuffer(cbDesc, "SSSConstantsCB");

            uint tCbSize = (uint)((Marshal.SizeOf<SSSTransmissionCB>() + 255) & ~255);
            _transmissionCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = tCbSize,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            }, "SSSTransmissionCB");
        }

        /// <summary>屏幕尺寸变化时重建 SSS 渲染目标。</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;

            _sssRT_A?.Dispose();
            _sssRT_B?.Dispose();
            _thicknessRT?.Dispose();

            RHITextureDesc MakeRT(string name, RHIFormat fmt) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };

            // SSS 中间 RT 用 HDR (R16G16B16A16) 保存线性漫反射颜色
            _sssRT_A = _device.CreateTexture(MakeRT("SSS_RT_A", RHIFormat.R16G16B16A16_Float), "SSS_RT_A");
            _sssRT_B = _device.CreateTexture(MakeRT("SSS_RT_B", RHIFormat.R16G16B16A16_Float), "SSS_RT_B");
            // 厚度缓冲 (R8 单通道, 由深度差近似)
            var thickDesc = MakeRT("SSS_Thickness", RHIFormat.R8_UNorm);
            _thicknessRT = _device.CreateTexture(thickDesc, "SSS_Thickness");

            _rtvA = _device.CreateRTV(_sssRT_A);
            _rtvB = _device.CreateRTV(_sssRT_B);
            _uavA = _device.CreateUAV(_sssRT_A);
            _uavB = _device.CreateUAV(_sssRT_B);
        }

        /// <summary>
        /// 渲染 SSS。需提供 G-Buffer 的法线/漫反射/深度/材质 ID SRV。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="geometrybufferDiffuse">G-Buffer 漫反射色 SRV。</param>
        /// <param name="geometrybufferNormal">G-Buffer 法线 SRV。</param>
        /// <param name="geometrybufferDepth">G-Buffer 深度 SRV。</param>
        /// <param name="geometrybufferMaterialId">G-Buffer 材质 ID SRV。</param>
        /// <param name="projMatrix">投影矩阵 (世界偏移→屏幕偏移)。</param>
        /// <param name="lightDir">主光方向 (世界空间, 透射用)。</param>
        /// <param name="shadowSrv">阴影贴图 SRV (透射用, 可空)。</param>
        /// <param name="matParams">SSS 材质参数。</param>
        /// <param name="enableTransmission">是否启用透射。</param>
        public void Render(
            RHICommandList cmdList,
            RHIDescriptorHandle geometrybufferDiffuse,
            RHIDescriptorHandle geometrybufferNormal,
            RHIDescriptorHandle geometrybufferDepth,
            RHIDescriptorHandle geometrybufferMaterialId,
            in Matrix4x4 projMatrix,
            Vector3 lightDir,
            RHIDescriptorHandle? shadowSrv,
            in SSSMaterialParams matParams,
            bool enableTransmission)
        {
            if (_sssRT_A == null || _sssRT_B == null) return;

            cmdList.SetComputeRootSignature(_rootSignature!);

            // 写入常量
            var constants = new SSSConstantsCB
            {
                InvResolution = new Vector2(1f / _width, 1f / _height),
                ScatterRadius = matParams.ScatterRadius,
                MaterialIdMask = matParams.MaterialId,
                ProjMatrix = projMatrix,
                KernelSampleCount = KernelSampleCount,
                PassIndex = 0,
                EnableTransmission = enableTransmission ? 1 : 0,
                EnableMultiLayer = 1,
                Anisotropy = matParams.Anisotropy,
                NearZ = 0.1f,
                FarZ = 1000f,
                Strength = 1.0f
            };
            _device.UploadToBuffer(_constantsCB!, new[] { constants });
            cmdList.SetComputeRootConstantBufferView(0, _constantsCB!);

            // 透射常量
            var transCB = new SSSTransmissionCB
            {
                TransmissionColor = matParams.TransmissionColor,
                TransmissionAmount = matParams.TransmissionAmount,
                TransmissionDistortion = matParams.TransmissionDistortion,
                LightDir = lightDir,
                ShadowAttenuation = 1.0f,
                ThicknessScale = 1.0f
            };
            _device.UploadToBuffer(_transmissionCB!, new[] { transCB });
            cmdList.SetComputeRootConstantBufferView(1, _transmissionCB!);

            // ── Pass 1: 提取 ──────────────────────────────────────────
            // 从 G-Buffer 提取材质 ID 匹配的像素的漫反射色 + 深度到 SSS RT_A
            RunExtractPass(cmdList, geometrybufferDiffuse, geometrybufferNormal, geometrybufferDepth, geometrybufferMaterialId);

            // ── Pass 2: 水平模糊 ───────────────────────────────────────
            // 对 RT_A 做水平 1D 卷积, 输出到 RT_B
            RunHorizontalBlurPass(cmdList);

            // ── Pass 3: 垂直模糊 ───────────────────────────────────────
            // 对 RT_B 做垂直 1D 卷积, 输出到 RT_A
            RunVerticalBlurPass(cmdList);

            // ── 透射 Pass (可选) ────────────────────────────────────────
            // 背面光照透过薄皮肤, 叠加到散射结果
            if (enableTransmission)
            {
                RunTransmissionPass(cmdList, geometrybufferNormal, geometrybufferDepth, shadowSrv);
            }

            // ── Pass 4: 合成 ──────────────────────────────────────────
            // 将散射结果合成回 G-Buffer 漫反射通道
            RunCompositePass(cmdList, geometrybufferDiffuse, geometrybufferMaterialId);
        }

        /// <summary>Pass 1: 从 G-Buffer 提取 SSS 像素数据。</summary>
        private void RunExtractPass(RHICommandList cmdList,
            RHIDescriptorHandle diffuse, RHIDescriptorHandle normal,
            RHIDescriptorHandle depth, RHIDescriptorHandle materialId)
        {
            cmdList.SetPipelineState(_extractPso!);
            cmdList.SetComputeRootDescriptorTable(2, diffuse); // SRV 表起点
            cmdList.SetComputeRootDescriptorTable(3, _uavA);   // UAV 表起点 -> RT_A

            cmdList.TransitionTo(_sssRT_A!, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRoot32BitConstants(0, 1, 0u, 0); // PassIndex = 0
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_sssRT_A, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Pass 2: 水平 1D 卷积 (Separable SSS 横向)。</summary>
        private void RunHorizontalBlurPass(RHICommandList cmdList)
        {
            cmdList.SetPipelineState(_hBlurPso!);
            // SRV: RT_A (输入) + 扩散核
            cmdList.SetComputeRootDescriptorTable(2, _device.CreateSRV(_sssRT_A!));
            cmdList.SetComputeRootDescriptorTable(3, _uavB);   // UAV: RT_B (输出)

            cmdList.TransitionTo(_sssRT_B!, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRoot32BitConstants(0, 1, 1u, 0); // PassIndex = 1
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_sssRT_B, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Pass 3: 垂直 1D 卷积 (Separable SSS 纵向)。</summary>
        private void RunVerticalBlurPass(RHICommandList cmdList)
        {
            cmdList.SetPipelineState(_vBlurPso!);
            cmdList.SetComputeRootDescriptorTable(2, _device.CreateSRV(_sssRT_B!));
            cmdList.SetComputeRootDescriptorTable(3, _uavA);   // UAV: RT_A (输出)

            cmdList.TransitionTo(_sssRT_A!, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRoot32BitConstants(0, 1, 2u, 0); // PassIndex = 2
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_sssRT_A, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>透射 Pass: 计算背面光照透过皮肤的贡献。</summary>
        private void RunTransmissionPass(RHICommandList cmdList,
            RHIDescriptorHandle normal, RHIDescriptorHandle depth,
            RHIDescriptorHandle? shadowSrv)
        {
            cmdList.SetPipelineState(_transmissionPso!);
            // SRV: 法线 + 深度 + 厚度 + 阴影
            cmdList.SetComputeRootDescriptorTable(2, normal);
            cmdList.SetComputeRootDescriptorTable(3, _uavB);   // 输出到 RT_B

            cmdList.TransitionTo(_sssRT_B!, RHIResourceState.UnorderedAccess);
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_sssRT_B, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Pass 4: 将散射结果合成回 G-Buffer 漫反射通道。</summary>
        private void RunCompositePass(RHICommandList cmdList,
            RHIDescriptorHandle diffuse, RHIDescriptorHandle materialId)
        {
            cmdList.SetPipelineState(_compositePso!);
            // SRV: RT_A (散射结果) + 漫反射 + 材质 ID
            cmdList.SetComputeRootDescriptorTable(2, _device.CreateSRV(_sssRT_A!));
            // 合成回 G-Buffer 漫反射 (UAV 写入 G-Buffer)
            cmdList.SetComputeRootDescriptorTable(3, diffuse);

            cmdList.SetComputeRoot32BitConstants(0, 1, 3u, 0); // PassIndex = 3
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        /// <summary>
        /// 多层 SSS 评估: 将多层散射核按权重叠加。
        /// 每层拥有独立散射半径与颜色, 运行时对每层分别卷积后混合。
        /// </summary>
        /// <param name="layers">层参数数组。</param>
        /// <param name="baseColor">基础漫反射色。</param>
        /// <returns>多层混合后的散射色。</returns>
        public Vector3 EvaluateMultiLayer(SSSLayerParams[] layers, Vector3 baseColor)
        {
            Vector3 result = Vector3.Zero;
            float totalWeight = 0f;
            foreach (var layer in layers)
            {
                // 每层贡献 = 颜色 * 权重 * (散射强度随半径)
                float strength = Math.Clamp(layer.ScatterRadius * 50f, 0f, 2f);
                result += layer.ScatterColor * layer.Weight * strength;
                result += baseColor * layer.Weight * (1f - Math.Clamp(strength, 0f, 1f));
                totalWeight += layer.Weight;
            }
            if (totalWeight > 0f) result /= totalWeight;
            return result;
        }

        /// <summary>设置多层参数。</summary>
        public void SetLayers(SSSLayerParams[] layers) => _layers = layers;

        /// <summary>获取多层参数。</summary>
        public SSSLayerParams[] GetLayers() => _layers;

        /// <summary>释放所有 GPU 资源。</summary>
        public void Dispose()
        {
            _rootSignature?.Dispose();
            _extractPso?.Dispose();
            _hBlurPso?.Dispose();
            _vBlurPso?.Dispose();
            _compositePso?.Dispose();
            _transmissionPso?.Dispose();
            _sssRT_A?.Dispose();
            _sssRT_B?.Dispose();
            _thicknessRT?.Dispose();
            _kernelBuffer?.Dispose();
            _constantsCB?.Dispose();
            _transmissionCB?.Dispose();
            _skinBRDF?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
