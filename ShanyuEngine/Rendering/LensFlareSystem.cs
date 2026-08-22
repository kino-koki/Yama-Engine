using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 镜头眩光系统 (Lens Flare System)
    //
    // 模拟真实摄影机镜头在强光下产生的光学伪影，包括光晕、光线、星芒、
    // 光环、圆环、尖刺、鬼影、变形宽银幕光带等。系统在屏幕空间执行：
    //   1. 将世界空间光源投影到屏幕空间，计算屏幕坐标。
    //   2. 通过 HZB(层次化 Z 缓冲)多级 mip 渐进式遮挡查询，判断光源是否被
    //      场景几何遮挡，并对部分遮挡做边缘渐变衰减。
    //   3. 沿"屏幕中心 -> 光源"的轴线分布各眩光元素，按类型绘制叠加。
    //   4. 色散: 不同波长(红/绿/蓝)沿轴线产生偏移，形成彩虹色鬼影。
    //   5. Bloom 联动: 光源的 Bloom 强度动态影响眩光整体强度。
    //   6. 镜头污渍纹理(LensDirtTexture)作为全屏蒙版，调制眩光扩散形态。
    //   7. 变形宽银幕眩光(AnamorphicFlare): 水平拉伸的蓝色光带。
    //
    // 对接:
    //   - PostProcessingStack: 复用 PostProcessingSettings 中的 LensFlare 开关与参数，
    //     在后处理管线的镜头特效阶段调用本系统渲染。
    //   - RHI: 使用 RHIDevice 创建纹理/缓冲/管线，RHICommandList 录制绘制。
    //   - ECS: 通过 LensFlareComponent 组件查询需要产生眩光的实体(光源)。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>镜头眩光元素类型。</summary>
    public enum LensFlareElementType
    {
        /// <summary>光晕: 光源周围的柔和发光圆盘。</summary>
        Glow,
        /// <summary>光线: 从光源放射状延伸的长光束。</summary>
        Ray,
        /// <summary>星芒: 十字或六角星形衍射尖刺。</summary>
        Star,
        /// <summary>光环: 围绕光源的圆环光圈。</summary>
        Halo,
        /// <summary>圆环: 比光环更细的明亮圆环。</summary>
        Ring,
        /// <summary>尖刺: 光源处的短锐利衍射刺。</summary>
        Spikes,
        /// <summary>鬼影: 沿轴线分布的半透明色斑(镜头内部反射)。</summary>
        Ghost,
        /// <summary>变形: 水平拉伸的变形宽银幕光带(蓝色为主)。</summary>
        Anamorphic
    }

    /// <summary>
    /// 镜头眩光元素: 描述单个眩光图形的外观与沿轴线的分布位置。
    /// 所有元素沿"屏幕中心 -> 光源"的轴线分布，position 参数为沿该轴线的
    /// 归一化位置(0=屏幕中心, 1=光源位置, >1=越过光源, &lt;0=光源对侧)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public sealed class LensFlareElement
    {
        /// <summary>元素类型。</summary>
        public LensFlareElementType Type = LensFlareElementType.Glow;

        /// <summary>沿轴线的归一化位置(0=屏幕中心, 1=光源)。</summary>
        public float Position = 0.5f;

        /// <summary>基础尺寸(屏幕短边的归一化比例)。</summary>
        public float Size = 0.15f;

        /// <summary>颜色(HDR 线性, 可超过1.0)。</summary>
        public Vector3 Color = new(1.0f, 0.9f, 0.8f);

        /// <summary>不透明度(0~1)。</summary>
        public float Opacity = 0.8f;

        /// <summary>旋转角度(弧度)。</summary>
        public float Rotation = 0.0f;

        /// <summary>宽高比拉伸(>1 横向拉伸, &lt;1 纵向拉伸)。</summary>
        public float AspectStretch = 1.0f;

        /// <summary>色散强度(不同波长偏移量, 0=无色散)。</summary>
        public float Dispersion = 0.0f;

        /// <summary>纹理引用(可为 null, 使用程序化形状)。</summary>
        public RHITexture? Texture;

        /// <summary>纹理平铺次数。</summary>
        public float TextureTiling = 1.0f;

        /// <summary>是否随光源距离衰减。</summary>
        public bool DistanceAttenuation = true;

        /// <summary>元素独立的强度乘数。</summary>
        public float IntensityMultiplier = 1.0f;
    }

    /// <summary>
    /// 镜头眩光资产: 一组眩光元素与全局参数的集合，可挂载到光源实体上。
    /// 一个资产描述某类光源(如太阳、聚光灯)的完整眩光外观。
    /// </summary>
    public sealed class LensFlareAsset
    {
        /// <summary>资产名称。</summary>
        public string Name = "LensFlareAsset";

        /// <summary>眩光元素列表。</summary>
        public List<LensFlareElement> Elements = new();

        /// <summary>全局强度乘数。</summary>
        public float GlobalIntensity = 1.0f;

        /// <summary>全局色散强度(影响所有 Ghost/Ring 元素)。</summary>
        public float GlobalDispersion = 0.3f;

        /// <summary>光源到屏幕中心距离的最大影响半径(归一化)。</summary>
        public float MaxScreenRadius = 1.5f;

        /// <summary>光源在屏幕外时的衰减起始(归一化屏幕半径)。</summary>
        public float OffscreenFalloff = 0.3f;

        /// <summary>是否启用距离衰减。</summary>
        public bool EnableDistanceAttenuation = true;

        /// <summary>创建一个默认的太阳眩光资产。</summary>
        public static LensFlareAsset CreateDefaultSunFlare()
        {
            var asset = new LensFlareAsset { Name = "SunFlare", GlobalIntensity = 1.0f };
            // 光源处明亮光晕
            asset.Elements.Add(new LensFlareElement
            {
                Type = LensFlareElementType.Glow, Position = 1.0f, Size = 0.25f,
                Color = new Vector3(1.0f, 0.95f, 0.85f), Opacity = 1.0f
            });
            // 放射光线
            asset.Elements.Add(new LensFlareElement
            {
                Type = LensFlareElementType.Ray, Position = 1.0f, Size = 0.6f,
                Color = new Vector3(1.0f, 0.9f, 0.7f), Opacity = 0.5f
            });
            // 星芒
            asset.Elements.Add(new LensFlareElement
            {
                Type = LensFlareElementType.Star, Position = 1.0f, Size = 0.4f,
                Color = new Vector3(1.0f, 0.85f, 0.6f), Opacity = 0.4f
            });
            // 中间鬼影序列
            for (int i = 0; i < 5; i++)
            {
                float t = 0.2f + i * 0.12f;
                asset.Elements.Add(new LensFlareElement
                {
                    Type = LensFlareElementType.Ghost, Position = t,
                    Size = 0.08f + i * 0.02f,
                    Color = new Vector3(0.6f + i * 0.05f, 0.5f, 0.4f),
                    Opacity = 0.3f - i * 0.04f,
                    Dispersion = 0.2f + i * 0.05f
                });
            }
            // 光环
            asset.Elements.Add(new LensFlareElement
            {
                Type = LensFlareElementType.Halo, Position = 0.8f, Size = 0.3f,
                Color = new Vector3(0.8f, 0.7f, 0.5f), Opacity = 0.3f
            });
            // 圆环
            asset.Elements.Add(new LensFlareElement
            {
                Type = LensFlareElementType.Ring, Position = 0.4f, Size = 0.12f,
                Color = new Vector3(0.5f, 0.6f, 0.9f), Opacity = 0.4f,
                Dispersion = 0.4f
            });
            return asset;
        }
    }

    /// <summary>
    /// 镜头眩光组件(ECS): 挂载到光源实体上，声明该光源产生眩光及其参数。
    /// 通过 ECS 查询所有携带此组件的实体即可获得需要渲染眩光的光源列表。
    /// </summary>
    public struct LensFlareComponent : IComponent
    {
        /// <summary>关联的光源实体 Id(通常与挂载本组件的实体相同)。</summary>
        public int LightEntityId;

        /// <summary>眩光资产引用(可为 null, 使用默认)。</summary>
        public LensFlareAsset? Asset;

        /// <summary>眩光强度乘数(与资产全局强度、Bloom 联动相乘)。</summary>
        public float Intensity;

        /// <summary>是否可见(可被遮挡/开关)。</summary>
        public bool IsVisible;

        /// <summary>是否使用光源自身的颜色作为眩光颜色。</summary>
        public bool UseLightColor;

        /// <summary>最大可见距离(世界单位, 超过则不渲染)。</summary>
        public float MaxDistance;

        /// <summary>是否启用遮挡检测。</summary>
        public bool EnableOcclusion;

        /// <summary>遮挡采样半径(屏幕空间像素, 用于部分遮挡渐变)。</summary>
        public float OcclusionSampleRadius;
    }

    /// <summary>
    /// 镜头污渍纹理: 全屏屏幕蒙版，模拟镜头表面灰尘/水渍/划痕。
    /// 眩光元素会以污渍纹理为权重进行扩散，使眩光呈现不规则形态而非均匀圆盘。
    /// 通常是一张低分辨率灰度纹理，在屏幕空间与眩光叠加。
    /// </summary>
    public sealed class LensDirtTexture : IDisposable
    {
        /// <summary>污渍纹理(RHI 纹理)。</summary>
        public RHITexture? Texture { get; private set; }

        /// <summary>污渍强度(0=无污渍, 1=完全污渍)。</summary>
        public float Intensity { get; set; } = 0.5f;

        /// <summary>污渍影响眩光的扩散范围乘数。</summary>
        public float SpreadScale { get; set; } = 2.0f;

        private readonly RHIDevice _device;

        /// <summary>构造。</summary>
        public LensDirtTexture(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>从已有纹理设置污渍。</summary>
        public void SetTexture(RHITexture texture)
        {
            Texture?.Dispose();
            Texture = texture;
        }

        /// <summary>程序化生成默认污渍纹理(低分辨率噪声)。</summary>
        public void GenerateDefault(int width = 256, int height = 256)
        {
            Texture?.Dispose();
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            };
            Texture = _device.CreateTexture(desc, "LensDirtTexture");
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            Texture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 变形宽银幕眩光: 水平拉伸的蓝色光带，模拟变形宽银幕(anamorphic)镜头的
    /// 柱面光学畸变产生的横向光条。常用于电影感画面(如 JJ Abrams 风格)。
    /// </summary>
    public sealed class AnamorphicFlare
    {
        /// <summary>光带水平拉伸倍数(越大越细长)。</summary>
        public float HorizontalStretch = 8.0f;

        /// <summary>光带颜色(默认偏蓝)。</summary>
        public Vector3 Color = new(0.3f, 0.5f, 1.0f);

        /// <summary>光带强度。</summary>
        public float Intensity = 0.6f;

        /// <summary>光带垂直宽度(归一化)。</summary>
        public float VerticalWidth = 0.02f;

        /// <summary>光带柔和度(高斯模糊半径)。</summary>
        public float Softness = 3.0f;

        /// <summary>是否启用次级光带(蓝/青/品红三色错位)。</summary>
        public bool EnableSecondaryBand = true;

        /// <summary>将变形眩光参数转换为可着色器使用的结构。</summary>
        public AnamorphicFlareParams GetParams() => new()
        {
            HorizontalStretch = HorizontalStretch,
            Color = Color,
            Intensity = Intensity,
            VerticalWidth = VerticalWidth,
            Softness = Softness,
            EnableSecondary = EnableSecondaryBand ? 1 : 0
        };
    }

    /// <summary>变形眩光着色器参数(对齐 16 字节)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AnamorphicFlareParams
    {
        public float HorizontalStretch;
        public float Intensity;
        public float VerticalWidth;
        public float Softness;
        public Vector3 Color;
        public int EnableSecondary;
    }

    /// <summary>
    /// 镜头眩光遮挡查询: 使用层次化 Z 缓冲(HZB)的多级 mip 进行渐进式精度遮挡检测。
    /// 思路: 先在低分辨率 mip(粗粒度)快速判断光源是否完全可见/完全遮挡，
    /// 若处于边缘(部分遮挡)则逐级提升到高分辨率 mip 精确采样，并对边缘做渐变衰减。
    /// 这样在大多数情况下仅需 1~2 次采样即可判定，性能远优于全分辨率逐像素检测。
    /// </summary>
    public sealed class LensFlareOcclusion
    {
        /// <summary>HZB 纹理(多级 mip 的层次化深度)。</summary>
        public RHITexture? HzbTexture { get; set; }

        /// <summary>深度纹理(全分辨率)。</summary>
        public RHITexture? DepthTexture { get; set; }

        /// <summary>最大 mip 级别数。</summary>
        public int MaxMipLevels { get; set; } = 8;

        /// <summary>初始粗粒度 mip(从最粗开始)。</summary>
        public int CoarseMipStart { get; set; } = 6;

        /// <summary>部分遮挡渐变宽度(归一化)。</summary>
        public float EdgeFalloff { get; set; } = 0.15f;

        /// <summary>采样次数(沿光源周围环形采样)。</summary>
        public int SampleCount { get; set; } = 8;

        /// <summary>采样半径(屏幕空间, 归一化)。</summary>
        public float SampleRadius { get; set; } = 0.01f;

        /// <summary>
        /// CPU 侧遮挡查询(占位实现): 实际由 GPU 计算着色器完成，
        /// 此处提供逻辑框架供理解。返回光源可见度(0=完全遮挡, 1=完全可见)。
        /// </summary>
        /// <param name="screenPos">光源屏幕位置(NDC, [-1,1])。</param>
        /// <returns>可见度 [0,1]。</returns>
        public float QueryVisibility(Vector2 screenPos)
        {
            if (HzbTexture == null) return 1.0f; // 无 HZB 则视为可见

            float visibility = 1.0f;
            // 渐进式: 从粗 mip 到细 mip
            for (int mip = CoarseMipStart; mip >= 0; mip--)
            {
                // 在光源周围环形采样 HZB, 比较深度
                int occludedSamples = 0;
                for (int s = 0; s < SampleCount; s++)
                {
                    float angle = (s / (float)SampleCount) * MathF.PI * 2.0f;
                    var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * SampleRadius;
                    var samplePos = screenPos + offset;
                    // 采样 HZB mip 层的最小深度, 与光源深度比较
                    // (实际由 GPU 着色器: sampleDepth = HZB.SampleLevel(pos, mip))
                    float hzbDepth = SampleHzb(samplePos, mip);
                    float lightDepth = ComputeLightDepth(screenPos);
                    if (hzbDepth < lightDepth) occludedSamples++;
                }

                float ratio = occludedSamples / (float)SampleCount;
                // 完全可见或完全遮挡时提前退出
                if (ratio <= 0.0f) { visibility = 1.0f; break; }
                if (ratio >= 1.0f) { visibility = 0.0f; break; }

                // 部分遮挡: 继续提升精度
                visibility = 1.0f - ratio;
            }

            // 边缘渐变衰减: 避免可见度突变
            visibility = Math.Clamp(visibility / EdgeFalloff, 0.0f, 1.0f);
            return visibility;
        }

        /// <summary>采样 HZB(CPU 占位, 实际 GPU 完成)。</summary>
        private float SampleHzb(Vector2 pos, int mip) => 1.0f;

        /// <summary>计算光源在屏幕空间的深度(CPU 占位)。</summary>
        private float ComputeLightDepth(Vector2 screenPos) => 0.5f;
    }

    /// <summary>镜头眩光渲染常量缓冲(每光源)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LensFlarePerLightCB
    {
        /// <summary>光源屏幕位置(NDC, [-1,1])。</summary>
        public Vector2 ScreenPos;
        /// <summary>屏幕中心到光源的方向(归一化)。</summary>
        public Vector2 ScreenDir;
        /// <summary>光源到屏幕中心的距离(归一化)。</summary>
        public float ScreenDist;
        /// <summary>整体强度。</summary>
        public float Intensity;
        /// <summary>可见度(遮挡)。</summary>
        public float Visibility;
        /// <summary>色散全局强度。</summary>
        public float Dispersion;
        /// <summary>Bloom 联动强度。</summary>
        public float BloomContribution;
        /// <summary>逆分辨率。</summary>
        public Vector2 InvResolution;
        /// <summary>光源颜色。</summary>
        public Vector3 LightColor;
        /// <summary>元素数量。</summary>
        public int ElementCount;
        /// <summary>是否启用污渍纹理。</summary>
        public int HasDirtTexture;
        /// <summary>污渍强度。</summary>
        public float DirtIntensity;
        /// <summary>污渍扩散。</summary>
        public float DirtSpread;
        /// <summary>填充。</summary>
        public float Pad0, Pad1;
    }

    /// <summary>单个眩光元素的着色器参数。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LensFlareElementCB
    {
        public int Type;
        public float Position;
        public float Size;
        public float Opacity;
        public Vector3 Color;
        public float Rotation;
        public float AspectStretch;
        public float Dispersion;
        public float IntensityMultiplier;
        public float TextureTiling;
        public int HasTexture;
        public int DistanceAttenuation;
        public float Pad0;
    }

    /// <summary>
    /// 镜头眩光渲染器: 核心系统，负责收集光源、计算屏幕位置、遮挡检测、
    /// 绘制眩光元素、色散、Bloom 联动与污渍调制。每帧由 PostProcessingStack 调用。
    /// </summary>
    public sealed class LensFlareRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIRootSignature? _rootSignature;
        private RHIPipelineState? _flarePipeline;
        private RHIPipelineState? _anamorphicPipeline;
        private RHIPipelineState? _dirtPipeline;

        private RHIBuffer? _perLightCB;
        private RHIBuffer? _elementCB;
        private RHITexture? _flareAccum;

        private readonly LensFlareOcclusion _occlusion = new();
        private LensDirtTexture? _dirtTexture;
        private readonly AnamorphicFlare _anamorphic = new();

        private int _width, _height;
        private float _currentBloomIntensity = 0.8f;

        /// <summary>遮挡查询器。</summary>
        public LensFlareOcclusion Occlusion => _occlusion;
        /// <summary>镜头污渍纹理。</summary>
        public LensDirtTexture? DirtTexture
        {
            get => _dirtTexture;
            set => _dirtTexture = value;
        }
        /// <summary>变形宽银幕眩光。</summary>
        public AnamorphicFlare Anamorphic => _anamorphic;
        /// <summary>当前 Bloom 强度(由 PostProcessingStack 反馈)。</summary>
        public float CurrentBloomIntensity
        {
            get => _currentBloomIntensity;
            set => _currentBloomIntensity = value;
        }
        /// <summary>眩光累积纹理。</summary>
        public RHITexture? FlareAccumTexture => _flareAccum;

        /// <summary>构造。</summary>
        public LensFlareRenderer(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化 GPU 资源与管线。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            CreatePipelines();
            AllocateResources();
        }

        /// <summary>创建计算管线。</summary>
        private void CreatePipelines()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 1 } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 8, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            _flarePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "LensFlareRender", CS = ShaderManager.Instance.GetBytecode("PostProcess/LensFlare", "LensFlareCS", RHIShaderStage.Compute) });

            _anamorphicPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "LensFlareAnamorphic", CS = ShaderManager.Instance.GetBytecode("PostProcess/LensFlare", "AnamorphicFlareCS", RHIShaderStage.Compute) });

            _dirtPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "LensFlareDirt", CS = ShaderManager.Instance.GetBytecode("PostProcess/LensFlare", "LensDirtCS", RHIShaderStage.Compute) });
        }

        /// <summary>分配 GPU 资源。</summary>
        private void AllocateResources()
        {
            // 眩光累积纹理(HDR)
            var accumDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = _width, Height = _height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _flareAccum = _device.CreateTexture(accumDesc, "LensFlareAccum");

            // 常量缓冲
            _perLightCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 256, Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            }, "LensFlarePerLightCB");

            _elementCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 4096, Flags = RHIBufferFlags.ConstantBuffer | RHIBufferFlags.Structured,
                Stride = 64, HeapType = RHIResourceHeapType.Upload
            }, "LensFlareElementCB");
        }

        /// <summary>窗口尺寸变化。</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
            _flareAccum?.Dispose();
            AllocateResources();
        }

        /// <summary>
        /// 计算光源在屏幕空间的位置(NDC [-1,1])。
        /// 将光源世界位置通过视图投影矩阵变换到裁剪空间，再透视除法到 NDC。
        /// </summary>
        /// <param name="lightWorldPos">光源世界位置。</param>
        /// <param name="viewProj">视图投影矩阵。</param>
        /// <param name="screenPos">输出的 NDC 屏幕位置。</param>
        /// <param name="depth">输出的 NDC 深度([-1,1] 或 [0,1])。</param>
        /// <param name="isOnScreen">是否在屏幕内。</param>
        /// <returns>是否在摄像机前方(可见方向)。</returns>
        public bool ComputeScreenPosition(Vector3 lightWorldPos, in Matrix4x4 viewProj,
            out Vector2 screenPos, out float depth, out bool isOnScreen)
        {
            // 世界 -> 裁剪空间
            var clip = Vector4.Transform(new Vector4(lightWorldPos, 1.0f), viewProj);
            screenPos = Vector2.Zero;
            depth = clip.Z;
            isOnScreen = false;

            // 光源在摄像机后方(w<=0)则不可见
            if (clip.W <= 0.0f) return false;

            // 透视除法 -> NDC
            float invW = 1.0f / clip.W;
            screenPos = new Vector2(clip.X * invW, clip.Y * invW);
            depth = clip.Z * invW;

            // 判断是否在屏幕范围内(带一定扩展容差, 屏幕外光源仍产生眩光)
            isOnScreen = MathF.Abs(screenPos.X) <= 1.0f && MathF.Abs(screenPos.Y) <= 1.0f;
            return true;
        }

        /// <summary>
        /// 渲染所有眩光。由 PostProcessingStack 在镜头特效阶段调用。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="ecs">ECS 世界。</param>
        /// <param name="viewProj">当前视图投影矩阵。</param>
        /// <param name="cameraPos">摄像机位置。</param>
        /// <param name="settings">后处理设置(读取 LensFlare 开关与参数)。</param>
        /// <param name="bloomIntensity">当前 Bloom 强度(联动)。</param>
        /// <param name="sceneDepth">场景深度纹理(遮挡检测)。</param>
        /// <param name="hzbTexture">HZB 纹理(遮挡检测, 可为 null)。</param>
        public void Render(RHICommandList cmdList, EntityComponentSystem ecs,
            in Matrix4x4 viewProj, Vector3 cameraPos, PostProcessingSettings settings,
            float bloomIntensity, RHITexture sceneDepth, RHITexture? hzbTexture)
        {
            // 后处理设置未启用眩光则跳过
            if (!settings.EnableLensFlare) return;

            _currentBloomIntensity = bloomIntensity;
            _occlusion.DepthTexture = sceneDepth;
            _occlusion.HzbTexture = hzbTexture;

            // 清空累积纹理: 通过首帧标志位由着色器在写入前清零
            // (实际由 LensFlareCS 的 bClearAccum 常量控制, 首次分派清零 UAV)
            cmdList.TransitionTo(_flareAccum!, RHIResourceState.UnorderedAccess);

            cmdList.SetComputeRootSignature(_rootSignature!);

            // 查询所有携带 LensFlareComponent 的实体
            var flareEntities = ecs.QueryWithComponent<LensFlareComponent>();
            foreach (var entityId in flareEntities)
            {
                var flare = ecs.GetComponent<LensFlareComponent>(entityId);
                if (!flare.IsVisible) continue;

                // 获取光源变换
                if (!ecs.HasComponent<TransformComponent>(entityId)) continue;
                var transform = ecs.GetComponent<TransformComponent>(entityId);
                var lightPos = transform.Position;

                // 距离衰减
                float distToCam = Vector3.Distance(lightPos, cameraPos);
                if (flare.MaxDistance > 0 && distToCam > flare.MaxDistance) continue;

                // 计算屏幕位置
                if (!ComputeScreenPosition(lightPos, viewProj, out var screenPos, out var lightDepth, out var onScreen))
                    continue;

                // 遮挡检测
                float visibility = flare.EnableOcclusion
                    ? _occlusion.QueryVisibility(screenPos)
                    : 1.0f;
                if (visibility <= 0.001f) continue;

                // 获取光源颜色
                Vector3 lightColor = Vector3.One;
                if (flare.UseLightColor && ecs.HasComponent<LightComponent>(entityId))
                {
                    lightColor = ecs.GetComponent<LightComponent>(entityId).Color;
                }

                // 获取眩光资产
                var asset = flare.Asset ?? LensFlareAsset.CreateDefaultSunFlare();

                // 计算屏幕中心到光源的方向与距离
                Vector2 screenDir = screenPos; // 屏幕中心为 (0,0)
                float screenDist = screenDir.Length();
                if (screenDist > 0.0001f) screenDir /= screenDist;

                // 屏幕外衰减
                float offscreenFactor = 1.0f;
                if (!onScreen)
                {
                    float excess = MathF.Max(0, MathF.Abs(screenPos.X) - 1.0f);
                    excess = MathF.Max(excess, MathF.Abs(screenPos.Y) - 1.0f);
                    offscreenFactor = Math.Clamp(1.0f - excess / asset.OffscreenFalloff, 0.0f, 1.0f);
                    if (offscreenFactor <= 0.0f) continue;
                }

                // 距离衰减
                float distAtten = 1.0f;
                if (asset.EnableDistanceAttenuation)
                {
                    distAtten = 1.0f / (1.0f + distToCam * 0.001f);
                }

                // Bloom 联动: 光源区域 Bloom 强度影响眩光强度
                float bloomContrib = Math.Clamp(bloomIntensity / Math.Max(0.001f, settings.BloomThreshold), 0.0f, 4.0f);

                // 综合强度
                float finalIntensity = flare.Intensity * asset.GlobalIntensity *
                                       visibility * offscreenFactor * distAtten *
                                       settings.LensFlareIntensity *
                                       (1.0f + bloomContrib * 0.5f);

                // 渲染该光源的眩光元素
                RenderFlareForLight(cmdList, asset, screenPos, screenDir, screenDist,
                    lightColor, finalIntensity, visibility, settings);
            }

            // 应用镜头污渍纹理(调制眩光扩散)
            if (_dirtTexture != null && _dirtTexture.Texture != null)
            {
                ApplyDirtTexture(cmdList, settings);
            }

            // 渲染变形宽银幕眩光(对最亮光源)
            if (_anamorphic.Intensity > 0.0f)
            {
                RenderAnamorphicFlare(cmdList, settings);
            }

            cmdList.TransitionTo(_flareAccum!, RHIResourceState.PixelShaderResource);
        }

        /// <summary>渲染单个光源的所有眩光元素。</summary>
        private void RenderFlareForLight(RHICommandList cmdList, LensFlareAsset asset,
            Vector2 screenPos, Vector2 screenDir, float screenDist,
            Vector3 lightColor, float intensity, float visibility,
            PostProcessingSettings settings)
        {
            // 计算每个元素沿轴线的实际屏幕位置
            // 轴线: 从屏幕中心(0,0)到光源 screenPos
            // 元素位置 t: actualPos = screenPos * t (t=1 在光源处, t=0 在中心)
            foreach (var element in asset.Elements)
            {
                // 元素沿轴线的屏幕位置
                var elementScreenPos = screenPos * element.Position;

                // 色散: 红/绿/蓝三通道沿垂直于轴线的方向偏移
                float dispersion = element.Dispersion * asset.GlobalDispersion * screenDist;

                // 元素强度
                float elemIntensity = element.IntensityMultiplier * element.Opacity * intensity;
                if (elemIntensity <= 0.001f) continue;

                // 填充元素常量
                var elemCB = new LensFlareElementCB
                {
                    Type = (int)element.Type,
                    Position = element.Position,
                    Size = element.Size,
                    Opacity = element.Opacity,
                    Color = element.Color * lightColor,
                    Rotation = element.Rotation,
                    AspectStretch = element.AspectStretch,
                    Dispersion = dispersion,
                    IntensityMultiplier = element.IntensityMultiplier,
                    TextureTiling = element.TextureTiling,
                    HasTexture = element.Texture != null ? 1 : 0,
                    DistanceAttenuation = element.DistanceAttenuation ? 1 : 0
                };

                // 填充每光源常量
                var perLightCB = new LensFlarePerLightCB
                {
                    ScreenPos = screenPos,
                    ScreenDir = screenDir,
                    ScreenDist = screenDist,
                    Intensity = intensity,
                    Visibility = visibility,
                    Dispersion = asset.GlobalDispersion,
                    BloomContribution = _currentBloomIntensity,
                    InvResolution = new Vector2(1f / _width, 1f / _height),
                    LightColor = lightColor,
                    ElementCount = asset.Elements.Count,
                    HasDirtTexture = (_dirtTexture?.Texture != null) ? 1 : 0,
                    DirtIntensity = _dirtTexture?.Intensity ?? 0f,
                    DirtSpread = _dirtTexture?.SpreadScale ?? 1f
                };

                // 上传常量并分派
                cmdList.SetComputeRoot32BitConstants(0, 32, in perLightCB, 0);
                cmdList.SetComputeRoot32BitConstants(1, 16, in elemCB, 0);
                cmdList.SetPipelineState(_flarePipeline!);
                cmdList.TransitionTo(_flareAccum!, RHIResourceState.UnorderedAccess);
                cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            }
        }

        /// <summary>应用镜头污渍纹理调制眩光扩散。</summary>
        private void ApplyDirtTexture(RHICommandList cmdList, PostProcessingSettings settings)
        {
            cmdList.SetPipelineState(_dirtPipeline!);
            cmdList.TransitionTo(_flareAccum!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_dirtTexture!.Texture!, RHIResourceState.NonPixelShaderResource);

            // 污渍强度受后处理设置的光晕强度影响
            float dirtIntensity = _dirtTexture.Intensity * settings.LensFlareIntensity;
            var cb = new LensFlarePerLightCB
            {
                InvResolution = new Vector2(1f / _width, 1f / _height),
                DirtIntensity = dirtIntensity,
                DirtSpread = _dirtTexture.SpreadScale,
                HasDirtTexture = 1,
                Intensity = settings.LensFlareIntensity
            };
            cmdList.SetComputeRoot32BitConstants(0, 32, in cb, 0);
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        /// <summary>渲染变形宽银幕眩光(水平蓝色光带)。</summary>
        private void RenderAnamorphicFlare(RHICommandList cmdList, PostProcessingSettings settings)
        {
            cmdList.SetPipelineState(_anamorphicPipeline!);
            cmdList.TransitionTo(_flareAccum!, RHIResourceState.UnorderedAccess);

            var params_ = _anamorphic.GetParams();
            var cb = new LensFlarePerLightCB
            {
                InvResolution = new Vector2(1f / _width, 1f / _height),
                Intensity = _anamorphic.Intensity * settings.LensFlareIntensity,
                LightColor = _anamorphic.Color
            };
            cmdList.SetComputeRoot32BitConstants(0, 32, in cb, 0);
            cmdList.SetComputeRoot32BitConstants(1, 8, in params_, 0);
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        /// <summary>
        /// 从 ECS 注册一个光源实体产生眩光。
        /// </summary>
        public static void RegisterFlare(EntityComponentSystem ecs, int entityId,
            LensFlareAsset? asset = null, float intensity = 1.0f, float maxDistance = 5000.0f)
        {
            var comp = new LensFlareComponent
            {
                LightEntityId = entityId,
                Asset = asset,
                Intensity = intensity,
                IsVisible = true,
                UseLightColor = true,
                MaxDistance = maxDistance,
                EnableOcclusion = true,
                OcclusionSampleRadius = 8.0f
            };
            ecs.AddComponent(entityId, comp);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _rootSignature?.Dispose();
            _flarePipeline?.Dispose();
            _anamorphicPipeline?.Dispose();
            _dirtPipeline?.Dispose();
            _perLightCB?.Dispose();
            _elementCB?.Dispose();
            _flareAccum?.Dispose();
            _dirtTexture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
