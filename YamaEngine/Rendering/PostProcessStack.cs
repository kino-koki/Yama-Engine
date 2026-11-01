using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 后处理特效栈系统 (Post Process Effect Stack)
    //
    // 全功能电影级后处理管线管理系统，支持：
    //   1. 13种后处理特效类型的统一枚举与管理
    //   2. 体积化后处理：PostProcessVolume 支持无限范围/球体/盒体三种边界
    //   3. PostProcessProfile：可序列化的配置集合，含10种独立特效Settings
    //   4. PostProcessStack：多Volume叠加、优先级排序、权重混合、实时参数编辑
    //   5. PostProcessMaterial：全屏后处理材质系统，统一着色器/纹理绑定
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // 后处理特效类型枚举
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 后处理特效类型枚举。每种特效对应独立的 Pass 管线和配置结构。
    /// </summary>
    public enum PostProcessEffectType
    {
        /// <summary>泛光 (Bloom) - HDR亮区辉光扩散</summary>
        Bloom = 0,
        /// <summary>景深 (Depth of Field) - 模拟真实镜头散焦</summary>
        DOF = 1,
        /// <summary>运动模糊 (Motion Blur) - 物体/相机运动模糊</summary>
        MotionBlur = 2,
        /// <summary>色差 (Chromatic Aberration) - RGB通道径向偏移</summary>
        ChromaticAberration = 3,
        /// <summary>暗角 (Vignette) - 画面边缘暗化</summary>
        Vignette = 4,
        /// <summary>胶片颗粒 (Film Grain) - 模拟胶片噪点</summary>
        FilmGrain = 5,
        /// <summary>色调映射 (Tone Mapping) - HDR到LDR映射</summary>
        ToneMapping = 6,
        /// <summary>FXAA (Fast Approximate Anti-Aliasing) - 快速抗锯齿</summary>
        FXAA = 7,
        /// <summary>SMAA (Enhanced Subpixel Morphological AA) - 高质量抗锯齿</summary>
        SMAA = 8,
        /// <summary>SSAO (Screen Space Ambient Occlusion) - 屏幕空间环境光遮蔽</summary>
        SSAO = 9,
        /// <summary>上帝光线 (God Rays) - 体积光散射</summary>
        GodRays = 10,
        /// <summary>颜色分级 (Color Grading) - LUT色彩校正/CDL/分离调整</summary>
        ColorGrading = 11,
        /// <summary>LUT (Lookup Table) - 3D颜色查找表</summary>
        LUT = 12
    }

    // ────────────────────────────────────────────────────────────────────────
    // PostProcessVolume — 后处理体积
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>体积影响范围类型。</summary>
    public enum VolumeBoundsType
    {
        /// <summary>无界：全局生效。</summary>
        Unbounded,
        /// <summary>轴对齐包围盒 (AABB)。</summary>
        Box,
        /// <summary>球体包围。</summary>
        Sphere
    }

    /// <summary>
    /// 后处理体积：在世界空间中定义一个区域，摄像机进入该区域时，
    /// 该体积携带的 Profile 按权重混入最终渲染参数。
    /// </summary>

    // ────────────────────────────────────────────────────────────────────────
    // PostProcessProfile — 后处理配置集合
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 后处理配置文件：包含所有后处理特效的参数配置。
    /// 每种 Settings 是可序列化的值类型结构体。
    /// </summary>
    public sealed class PostProcessProfile : IDisposable
    {
        // ── 泛光 (Bloom) ──────────────────────────────────────────────────
        /// <summary>泛光设置。</summary>
        public BloomSettings Bloom = BloomSettings.Default;

        // ── 景深 (Depth of Field) ───────────────────────────────────────
        /// <summary>景深设置。</summary>
        public DepthOfFieldSettings DepthOfField = DepthOfFieldSettings.Default;

        // ── 运动模糊 (Motion Blur) ──────────────────────────────────────
        /// <summary>运动模糊设置。</summary>
        public MotionBlurSettings MotionBlur = MotionBlurSettings.Default;

        // ── 色差 (Chromatic Aberration) ──────────────────────────────────
        /// <summary>色差设置。</summary>
        public ChromaticAberrationSettings ChromaticAberration { get; set; } = ChromaticAberrationSettings.Default;

        // ── 暗角 (Vignette) ─────────────────────────────────────────────
        /// <summary>暗角设置。</summary>
        public VignetteSettings Vignette = VignetteSettings.Default;

        // ── 胶片颗粒 (Film Grain) ────────────────────────────────────────
        /// <summary>胶片颗粒设置。</summary>
        public FilmGrainSettings FilmGrain { get; set; } = FilmGrainSettings.Default;

        // ── 色调映射 (Tone Mapping) ─────────────────────────────────────
        /// <summary>色调映射设置。</summary>
        public ToneMappingSettings ToneMapping { get; set; } = ToneMappingSettings.Default;

        // ── FXAA ────────────────────────────────────────────────────────
        /// <summary>FXAA抗锯齿设置。</summary>
        public FXAASettings FXAA = FXAASettings.Default;

        // ── God Rays ────────────────────────────────────────────────────
        /// <summary>上帝光线设置。</summary>
        public GodRaySettings GodRays { get; set; } = GodRaySettings.Default;

        // ── 颜色分级 (Color Grading) ────────────────────────────────────
        /// <summary>颜色分级设置。</summary>
        public ColorGradingSettings ColorGrading = ColorGradingSettings.Default;

        /// <summary>获取指定特效类型的启用状态。</summary>
        public bool IsEnabled(PostProcessEffectType type)
        {
            return type switch
            {
                PostProcessEffectType.Bloom => Bloom.Enabled,
                PostProcessEffectType.DOF => DepthOfField.Enabled,
                PostProcessEffectType.MotionBlur => MotionBlur.Enabled,
                PostProcessEffectType.ChromaticAberration => ChromaticAberration.Enabled,
                PostProcessEffectType.Vignette => Vignette.Enabled,
                PostProcessEffectType.FilmGrain => FilmGrain.Enabled,
                PostProcessEffectType.ToneMapping => ToneMapping.Enabled,
                PostProcessEffectType.FXAA => FXAA.Enabled,
                PostProcessEffectType.SMAA => false, // SMAA 尚未独立配置
                PostProcessEffectType.SSAO => false, // SSAO 由独立系统管理
                PostProcessEffectType.GodRays => GodRays.Enabled,
                PostProcessEffectType.ColorGrading => ColorGrading.Enabled,
                PostProcessEffectType.LUT => ColorGrading.LutIntensity > 0.0f && ColorGrading.LutTexture != null,
                _ => false
            };
        }

        /// <summary>重置所有参数为默认值。</summary>
        public void ResetToDefaults()
        {
            Bloom = BloomSettings.Default;
            DepthOfField = DepthOfFieldSettings.Default;
            MotionBlur = MotionBlurSettings.Default;
            ChromaticAberration = ChromaticAberrationSettings.Default;
            Vignette = VignetteSettings.Default;
            FilmGrain = FilmGrainSettings.Default;
            ToneMapping = ToneMappingSettings.Default;
            FXAA = FXAASettings.Default;
            GodRays = GodRaySettings.Default;
            ColorGrading = ColorGradingSettings.Default;
        }

        /// <summary>深度拷贝。</summary>
        public PostProcessProfile Clone()
        {
            return new PostProcessProfile
            {
                Bloom = Bloom,
                DepthOfField = DepthOfField,
                MotionBlur = MotionBlur,
                ChromaticAberration = ChromaticAberration,
                Vignette = Vignette,
                FilmGrain = FilmGrain,
                ToneMapping = ToneMapping,
                FXAA = FXAA,
                GodRays = GodRays,
                ColorGrading = ColorGrading with
                {
                    LutTexture = ColorGrading.LutTexture // 纹理引用共享
                }
            };
        }

        public void Dispose()
        {
            // LutTexture 由外部管理，不在此释放
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PostProcessMaterial — 后处理材质
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 后处理材质：封装全屏后处理Pass的着色器和纹理绑定。
    /// 使用全屏三角形（3个顶点，无索引缓冲）进行绘制。
    /// </summary>
    public sealed class PostProcessMaterial : IDisposable
    {
        /// <summary>材质名称。</summary>
        public string Name { get; }

        /// <summary>顶点着色器字节码（全屏三角形）。</summary>
        public byte[]? VertexShaderBytecode { get; private set; }

        /// <summary>像素着色器字节码。</summary>
        public byte[]? PixelShaderBytecode { get; private set; }

        /// <summary>RHI 管线状态对象。</summary>
        private RHIPipelineState? _pipelineState;

        /// <summary>RHI 根签名。</summary>
        private RHIRootSignature? _rootSignature;

        /// <summary>常量缓冲区数据大小。</summary>
        public int ConstantBufferSize { get; private set; }

        /// <summary>输入纹理槽位绑定。</summary>
        private readonly Dictionary<int, RHITexture?> _inputTextures = new();

        /// <summary>输入纹理槽位枚举。</summary>
        public enum TextureSlot
        {
            /// <summary>场景颜色（HDR）。</summary>
            Color = 0,
            /// <summary>场景深度缓冲。</summary>
            Depth = 1,
            /// <summary>运动向量缓冲。</summary>
            MotionVector = 2,
            /// <summary>GeometryBuffer 0 (Albedo + AO)。</summary>
            GeometryBuffer0 = 3,
            /// <summary>GeometryBuffer 1 (Normal + Roughness)。</summary>
            GeometryBuffer1 = 4,
            /// <summary>GeometryBuffer 2 (Emission)。</summary>
            GeometryBuffer2 = 5,
            /// <summary>GeometryBuffer 3 (ShadingModel + MaterialID)。</summary>
            GeometryBuffer3 = 6,
            /// <summary>Bloom纹理。</summary>
            Bloom = 7,
            /// <summary>Lens Dirt纹理。</summary>
            LensDirt = 8,
            /// <summary>LUT 3D纹理。</summary>
            LUT = 9,
            /// <summary>自定义纹理A。</summary>
            CustomA = 10,
            /// <summary>自定义纹理B。</summary>
            CustomB = 11
        }

        /// <summary>RHI 设备引用。</summary>
        private readonly RHIDevice? _device;

        /// <summary>全屏三角形顶点数据（NDC空间3顶点）。</summary>
        private static readonly Vector4[] FullScreenTriangleVertices = new[]
        {
            new Vector4(-1, -1, 0, 1),  // 左下
            new Vector4( 3, -1, 0, 1),  // 右下（超出屏幕确保覆盖）
            new Vector4(-1,  3, 0, 1)   // 左上（超出屏幕确保覆盖）
        };

        /// <summary>全屏三角形顶点缓冲。</summary>
        private RHIBuffer? _vertexBuffer;

        /// <summary>构造后处理材质。</summary>
        public PostProcessMaterial(string name, RHIDevice? device = null)
        {
            Name = name;
            _device = device;
            ConstantBufferSize = 256; // 默认常量缓冲大小
        }

        /// <summary>设置着色器字节码。</summary>
        public void SetShaders(byte[]? vs, byte[]? ps)
        {
            VertexShaderBytecode = vs;
            PixelShaderBytecode = ps;
        }

        /// <summary>设置常量缓冲大小。</summary>
        public void SetConstantBufferSize(int size)
        {
            ConstantBufferSize = Math.Max(16, size);
        }

        /// <summary>绑定输入纹理到指定槽位。</summary>
        public void SetTexture(TextureSlot slot, RHITexture? texture)
        {
            _inputTextures[(int)slot] = texture;
        }

        /// <summary>获取指定槽位的输入纹理。</summary>
        public RHITexture? GetTexture(TextureSlot slot)
        {
            return _inputTextures.TryGetValue((int)slot, out var tex) ? tex : null;
        }

        /// <summary>创建全屏顶点缓冲。</summary>
        public void CreateFullScreenResources(RHIDevice device)
        {
            // 全屏三角形顶点缓冲
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(FullScreenTriangleVertices.Length * sizeof(float) * 4),
                Flags = RHIBufferFlags.VertexBuffer,
                HeapType = RHIResourceHeapType.Upload
            };
            _vertexBuffer = device.CreateBuffer(vbDesc, "PPFullscreenVB");
        }

        /// <summary>构建 RHI 管线。</summary>
        public void BuildPipeline(RHIDevice device, RHIFormat renderTargetFormat)
        {
            if (PixelShaderBytecode == null) return;

            // 根签名: CBV(b0) + SRV Table(t0-t11) + Sampler(s0-s2)
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new()
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
                    },
                    new()
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 12,
                                    BaseShaderRegister = 0
                                }
                            }
                        }
                    }
                },
                StaticSamplers = new[]
                {
                    RHISamplerDesc.LinearClamp,
                    RHISamplerDesc.PointClamp,
                    RHISamplerDesc.LinearWrap
                }
            };
            _rootSignature = device.CreateRootSignature(rootDesc);

            // 图形管线
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = $"PPMaterial_{Name}",
                VS = VertexShaderBytecode == null ? null : new RHIShaderBytecode(VertexShaderBytecode, "VSMain", RHIShaderStage.Vertex),
                PS = PixelShaderBytecode == null ? null : new RHIShaderBytecode(PixelShaderBytecode, "PSMain", RHIShaderStage.Pixel),
                RootSignature = _rootSignature,
                BlendState = RHIBlendDesc.AdditiveBlend,
                RasterizerState = RHIRasterizerState.CullNone,
                DepthStencilState = RHIDepthStencilState.Disabled,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                RenderTargetFormats = new[] { renderTargetFormat },
                NumRenderTargets = 1
            };
            _pipelineState = device.CreateGraphicsPipelineState(psoDesc);
        }

        /// <summary>获取管线状态（需先调用 BuildPipeline）。</summary>
        public RHIPipelineState? PipelineState => _pipelineState;

        /// <summary>获取根签名。</summary>
        public RHIRootSignature? RootSignature => _rootSignature;

        /// <summary>获取全屏顶点缓冲。</summary>
        public RHIBuffer? VertexBuffer => _vertexBuffer;

        /// <summary>获取所有绑定的纹理槽位。</summary>
        public IReadOnlyDictionary<int, RHITexture?> BoundTextures => _inputTextures;

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _pipelineState?.Dispose();
            _rootSignature?.Dispose();
            _vertexBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PostProcessStack — 后处理特效栈管理器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 后处理特效栈管理器：管理多个 PostProcessVolume 的叠加混合，
    /// 生成最终渲染参数，驱动所有后处理 Pass 的执行。
    /// </summary>
    public sealed class PostProcessStack : IDisposable
    {
        // ── 常量 ──
        private const int MaxVolumes = 32;
        private const int MaxMipLevels = 6;
        private const int TileSize = 16;

        // ── 管线阶段枚举 ──
        /// <summary>后处理管线执行阶段。</summary>
        public enum PipelineStage
        {
            /// <summary>自动曝光计算。</summary>
            AutoExposure,
            /// <summary>景深预处理（CoC计算）。</summary>
            DOFPreProcess,
            /// <summary>近场模糊。</summary>
            DOFNearBlur,
            /// <summary>远场模糊。</summary>
            DOFFarBlur,
            /// <summary>景深合成。</summary>
            DOFComposite,
            /// <summary>运动模糊 - TileMax。</summary>
            MotionBlurTileMax,
            /// <summary>运动模糊 - NeighborMax。</summary>
            MotionBlurNeighborMax,
            /// <summary>运动模糊 - 重建。</summary>
            MotionBlurReconstruct,
            /// <summary>泛光 - 亮度预过滤。</summary>
            BloomPrefilter,
            /// <summary>泛光 - 降采样。</summary>
            BloomDownSample,
            /// <summary>泛光 - 升采样+模糊。</summary>
            BloomUpSample,
            /// <summary>泛光 - 合成。</summary>
            BloomComposite,
            /// <summary>色差。</summary>
            ChromaticAberration,
            /// <summary>暗角。</summary>
            Vignette,
            /// <summary>胶片颗粒。</summary>
            FilmGrain,
            /// <summary>色调映射。</summary>
            ToneMapping,
            /// <summary>颜色分级/LUT。</summary>
            ColorGrading,
            /// <summary>FXAA。</summary>
            FXAA,
            /// <summary>God Rays。</summary>
            GodRays,
            /// <summary>最终输出。</summary>
            FinalOutput
        }

        // ── 成员变量 ──

        /// <summary>RHI 设备。</summary>
        private readonly RHIDevice _device;

        /// <summary>全局默认配置。</summary>
        public PostProcessProfile GlobalProfile { get; private set; }

        /// <summary>所有已注册的后处理体积。</summary>
        private readonly List<PostProcessVolume> _volumes = new();

        /// <summary>当前帧激活的体积（含权重和优先级）。</summary>
        private readonly List<(PostProcessVolume Volume, float Weight)> _activeVolumes = new();

        /// <summary>最终混合后的配置。</summary>
        private PostProcessProfile _blendedProfile;

        /// <summary>是否需要重新混合。</summary>
        private bool _dirty = true;

        /// <summary>屏幕分辨率。</summary>
        private int _width, _height;

        // ── 简化集成：激活特效列表 ──
        private readonly List<PostProcessEffectType> _activeEffects = new();

        /// <summary>是否启用后处理（全局开关）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>当前激活的特效列表。</summary>
        public IReadOnlyList<PostProcessEffectType> ActiveEffects => _activeEffects;

        /// <summary>切换指定特效的启用状态。</summary>
        public void ToggleEffect(PostProcessEffectType effect)
        {
            if (_activeEffects.Contains(effect)) _activeEffects.Remove(effect);
            else _activeEffects.Add(effect);
        }

        /// <summary>清空全部激活特效。</summary>
        public void ClearActiveEffects() => _activeEffects.Clear();

        // ── 特效管线 ──
        private BloomPipeline? _bloomPipeline;
        private DepthOfFieldPipeline? _dofPipeline;
        private MotionBlurPipeline? _motionBlurPipeline;
        private LensEffectPipeline? _lensPipeline;

        // ── 最终 Blit PSO（编辑器简化路径：HDR → LDR） ──
        private RHIPipelineState? _blitPso;
        private RHIRootSignature? _blitRootSig;

        // ── 渲染资源 ──

        /// <summary>Ping-Pong 中间纹理A。</summary>
        private RHITexture? _rtA;
        /// <summary>Ping-Pong 中间纹理B。</summary>
        private RHITexture? _rtB;

        /// <summary>Bloom Mip链纹理。</summary>
        private RHITexture? _bloomMipChain;

        /// <summary>运动模糊 Tile 速度缓冲。</summary>
        private RHITexture? _motionBlurTileMax;
        private RHITexture? _motionBlurNeighborMax;

        /// <summary>CoC (Circle of Confusion) 纹理。</summary>
        private RHITexture? _cocTexture;

        /// <summary>God Ray 临时纹理（ Occlusion+Scattering ）。</summary>
        private RHITexture? _godRayOcclusion;
        private RHITexture? _godRayScattering;

        /// <summary>自动曝光缓冲。</summary>
        private RHIBuffer? _exposureBuffer;
        private float _currentExposure = 0.0f;

        /// <summary>根签名。</summary>
        private RHIRootSignature? _rootSignature;

        // ── 时间 ──
        private float _time;
        private int _frameIndex;

        /// <summary>当前帧时间。</summary>
        public float Time => _time;
        /// <summary>当前帧索引。</summary>
        public int FrameIndex => _frameIndex;

        /// <summary>最终混合后的配置（只读）。</summary>
        public PostProcessProfile BlendedProfile => _blendedProfile;

        /// <summary>当前曝光值。</summary>
        public float CurrentExposure => _currentExposure;

        // ── 构造 ──

        /// <summary>构造后处理栈。</summary>
        public PostProcessStack(RHIDevice device, PostProcessProfile? globalProfile = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            GlobalProfile = globalProfile ?? new PostProcessProfile();
            _blendedProfile = GlobalProfile.Clone();
        }

        // ── 初始化 ──

        /// <summary>初始化栈：创建管线和资源。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            CreateRootSignature();
            AllocateResources();
            CreateEffectPipelines();
        }

        /// <summary>创建所有特效管线。</summary>
        private void CreateEffectPipelines()
        {
            _bloomPipeline = new BloomPipeline(_device, GlobalProfile.Bloom);
            _bloomPipeline.Initialize(_width, _height);

            _dofPipeline = new DepthOfFieldPipeline(_device, GlobalProfile.DepthOfField);
            _dofPipeline.Initialize(_width, _height);

            _motionBlurPipeline = new MotionBlurPipeline(_device, GlobalProfile.MotionBlur);
            _motionBlurPipeline.Initialize(_width, _height);

            _lensPipeline = new LensEffectPipeline(_device);
            _lensPipeline.Initialize(_width, _height);

            // 创建最终 Blit PSO（FullScreenTriangle，无顶点输入，SV_VertexID 生成）
            try
            {
                var vsBytes = ShaderManager.Instance.GetBytecode("FullScreenTriangle", "VSMain", RHIShaderStage.Vertex);
                var psBytes = ShaderManager.Instance.GetBytecode("FullScreenTriangle", "PSMain", RHIShaderStage.Pixel);
                if (vsBytes.IsValid && psBytes.IsValid)
                {
                    var blitRootDesc = new RHIRootSignatureDesc
                    {
                        Parameters = new[]
                        {
                            new RHIRootParameter
                            {
                                ParameterType = RHIRootParameterType.DescriptorTable,
                                ShaderVisibility = RHIShaderVisibility.Pixel,
                                DescriptorTable = new RHIRootDescriptorTable
                                {
                                    Ranges = new[]
                                    {
                                        new RHIRootDescriptorRange
                                        {
                                            RangeType = RHIDescriptorRangeType.SRV,
                                            NumDescriptors = 1,
                                            BaseShaderRegister = 0
                                        }
                                    }
                                }
                            }
                        },
                        StaticSamplers = new[] { RHISamplerDesc.PointClamp }
                    };
                    _blitRootSig = _device.CreateRootSignature(blitRootDesc, "BlitRootSig");

                    var blitPsoDesc = new RHIGraphicsPipelineDesc
                    {
                        Name = "BlitPSO",
                        VS = vsBytes,
                        PS = psBytes,
                        RootSignature = _blitRootSig,
                        BlendState = RHIBlendDesc.AdditiveBlend,
                        RasterizerState = RHIRasterizerState.CullNone,
                        DepthStencilState = RHIDepthStencilState.Disabled,
                        PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                        RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm }
                    };
                    _blitPso = _device.CreateGraphicsPipelineState(blitPsoDesc, "BlitPSO");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PostProcessStack] FinalBlit PSO 创建失败: {ex.Message}");
            }
        }

        /// <summary>创建根签名。</summary>
        private void CreateRootSignature()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new()
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
                    },
                    new()
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 12,
                                    BaseShaderRegister = 0
                                }
                            }
                        }
                    },
                    new()
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.UAV,
                                    NumDescriptors = 8,
                                    BaseShaderRegister = 0
                                }
                            }
                        }
                    }
                },
                StaticSamplers = new[]
                {
                    RHISamplerDesc.LinearClamp,
                    RHISamplerDesc.PointClamp,
                    RHISamplerDesc.LinearWrap
                }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);
        }

        /// <summary>分配所有渲染目标资源。</summary>
        private void AllocateResources()
        {
            RHITextureDesc MakeRT(string name, int w, int h, RHIFormat fmt = RHIFormat.R16G16B16A16_Float,
                int mipLevels = 1) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = Math.Max(1, w),
                Height = Math.Max(1, h),
                DepthOrArraySize = 1,
                MipLevels = mipLevels,
                SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };

            // Ping-Pong 纹理
            _rtA = _device.CreateTexture(MakeRT("PPStack_RTA", _width, _height), "PPStack_RTA");
            _rtB = _device.CreateTexture(MakeRT("PPStack_RTB", _width, _height), "PPStack_RTB");

            // Bloom Mip链（6级：1/2, 1/4, 1/8, 1/16, 1/32）
            int bloomW = Math.Max(1, _width / 2);
            int bloomH = Math.Max(1, _height / 2);
            _bloomMipChain = _device.CreateTexture(
                MakeRT("PPStack_BloomMip", bloomW, bloomH, mipLevels: MaxMipLevels), "PPStack_BloomMip");

            // 运动模糊 Tile 缓冲（16x16 Tile）
            int tileCountX = (_width + TileSize - 1) / TileSize;
            int tileCountY = (_height + TileSize - 1) / TileSize;
            _motionBlurTileMax = _device.CreateTexture(
                MakeRT("PPStack_MBTileMax", tileCountX, tileCountY, RHIFormat.R16G16_Float), "PPStack_MBTileMax");
            _motionBlurNeighborMax = _device.CreateTexture(
                MakeRT("PPStack_MBNeighbor", tileCountX, tileCountY, RHIFormat.R16G16_Float), "PPStack_MBNeighbor");

            // CoC 纹理
            _cocTexture = _device.CreateTexture(
                MakeRT("PPStack_CoC", _width, _height, RHIFormat.R16_Float), "PPStack_CoC");

            // God Ray 缓冲（半分辨率）
            int grW = Math.Max(1, _width / 2);
            int grH = Math.Max(1, _height / 2);
            _godRayOcclusion = _device.CreateTexture(
                MakeRT("PPStack_GROcclusion", grW, grH, RHIFormat.R8_UNorm), "PPStack_GROcclusion");
            _godRayScattering = _device.CreateTexture(
                MakeRT("PPStack_GRScattering", grW, grH, RHIFormat.R16G16B16A16_Float), "PPStack_GRScattering");

            // 曝光缓冲
            var expDesc = new RHIBufferDesc
            {
                SizeInBytes = 16,
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.Structured,
                Stride = 16,
                HeapType = RHIResourceHeapType.Default
            };
            _exposureBuffer = _device.CreateBuffer(expDesc, "PPStack_Exposure");
        }

        /// <summary>窗口尺寸变化时重新分配资源。</summary>
        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;

            _rtA?.Dispose(); _rtB?.Dispose();
            _bloomMipChain?.Dispose();
            _motionBlurTileMax?.Dispose(); _motionBlurNeighborMax?.Dispose();
            _cocTexture?.Dispose();
            _godRayOcclusion?.Dispose(); _godRayScattering?.Dispose();
            _exposureBuffer?.Dispose();

            AllocateResources();
        }

        // ── 体积管理 ──

        /// <summary>添加后处理体积。</summary>
        public void AddVolume(PostProcessVolume volume)
        {
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            if (_volumes.Count >= MaxVolumes)
                throw new InvalidOperationException($"最多支持 {MaxVolumes} 个后处理体积");
            if (!_volumes.Contains(volume))
            {
                _volumes.Add(volume);
                _dirty = true;
            }
        }

        /// <summary>移除后处理体积。</summary>
        public bool RemoveVolume(PostProcessVolume volume)
        {
            bool removed = _volumes.Remove(volume);
            if (removed) _dirty = true;
            return removed;
        }

        /// <summary>获取所有已注册体积的只读列表。</summary>
        public IReadOnlyList<PostProcessVolume> Volumes => _volumes;

        /// <summary>标记需要重新混合（编辑器参数修改时调用）。</summary>
        public void MarkDirty() => _dirty = true;

        // ── 每帧评估 ──

        /// <summary>
        /// 每帧评估：收集相机视锥体内的 Volume → 排序 → 权重混合 → 生成最终渲染参数。
        /// </summary>
        /// <param name="cameraPosition">相机世界位置。</param>
        /// <param name="cameraFrustumPlanes">相机视锥体平面（可选，用于更精确的剔除）。</param>
        public void Evaluate(Vector3 cameraPosition, IReadOnlyList<Vector4>? cameraFrustumPlanes = null)
        {
            _activeVolumes.Clear();

            // 1. 收集激活体积
            for (int i = 0; i < _volumes.Count; i++)
            {
                var vol = _volumes[i];
                if (!vol.IsEnabled) continue;

                float weight = vol.ComputeWeightAt(cameraPosition);
                if (weight <= 0.0f) continue;

                // 如果提供了视锥体平面，进一步剔除不在视锥内的体积
                if (cameraFrustumPlanes != null && cameraFrustumPlanes.Count >= 6)
                {
                    if (!IsVolumeInFrustum(vol, cameraFrustumPlanes))
                        continue;
                }

                _activeVolumes.Add((vol, weight));
            }

            // 2. 按优先级降序排序
            _activeVolumes.Sort((a, b) => b.Volume.Priority.CompareTo(a.Volume.Priority));

            // 3. 权重混合 → 生成最终参数
            if (_activeVolumes.Count > 0 || _dirty)
            {
                _blendedProfile = BlendProfiles(_activeVolumes);
                _dirty = false;
            }
        }

        /// <summary>
        /// 判断体积是否与视锥体相交。
        /// 使用体积的AABB或球体进行简单测试。
        /// </summary>
        private bool IsVolumeInFrustum(PostProcessVolume volume, IReadOnlyList<Vector4> frustumPlanes)
        {
            Vector3 center = volume.Bounds switch
            {
                VolumeBoundsType.Box => volume.BoxCenter,
                VolumeBoundsType.Sphere => volume.SphereCenter,
                _ => Vector3.Zero
            };

            float radius = volume.Bounds switch
            {
                VolumeBoundsType.Box => volume.BoxHalfExtents.Length(),
                VolumeBoundsType.Sphere => volume.SphereRadius,
                _ => float.MaxValue
            };

            for (int i = 0; i < Math.Min(frustumPlanes.Count, 6); i++)
            {
                var plane = frustumPlanes[i];
                var normal = new Vector3(plane.X, plane.Y, plane.Z);
                float dist = Vector3.Dot(normal, center) + plane.W;
                if (dist < -radius) return false;
            }
            return true;
        }

        // ── 参数混合算法 ──

        /// <summary>
        /// 将多个体积的Profile混合为最终Profile。
        /// 算法：按优先级从高到低遍历，每个参数值按体积权重加权混合。
        /// </summary>
        private PostProcessProfile BlendProfiles(List<(PostProcessVolume Volume, float Weight)> activeVolumes)
        {
            // 如果没有激活体积，使用全局默认
            if (activeVolumes.Count == 0)
                return GlobalProfile.Clone();

            var result = new PostProcessProfile
            {
                Bloom = BlendBloomSettings(activeVolumes),
                DepthOfField = BlendDOFSettings(activeVolumes),
                MotionBlur = BlendMotionBlurSettings(activeVolumes),
                ChromaticAberration = BlendCASettings(activeVolumes),
                Vignette = BlendVignetteSettings(activeVolumes),
                FilmGrain = BlendFilmGrainSettings(activeVolumes),
                ToneMapping = BlendToneMappingSettings(activeVolumes),
                FXAA = BlendFXAASettings(activeVolumes),
                GodRays = BlendGodRaySettings(activeVolumes),
                ColorGrading = BlendColorGradingSettings(activeVolumes)
            };

            return result;
        }

        /// <summary>辅助方法：标量参数混合。</summary>
        private static float BlendScalar<T>(List<T> volumes, float globalDefault,
            Func<T, float> getEnabled, Func<T, float> getValue) where T : struct
        {
            // 如果没有启用的体积，使用全局默认
            bool anyEnabled = false;
            foreach (var v in volumes)
            {
                if (getEnabled(v) > 0.5f) { anyEnabled = true; break; }
            }
            if (!anyEnabled) return globalDefault;

            float totalWeight = 0.0f;
            float weightedSum = 0.0f;
            foreach (var v in volumes)
            {
                // 这里 T 实际是 (PostProcessVolume, float)，我们需要用另一种方式
                break;
            }
            return globalDefault;
        }

        // ── 各特效设置混合 ──

        private static BloomSettings BlendBloomSettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = BloomSettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.Bloom;
                if (!s.Enabled) continue;
                result.Enabled = true;
                result.Intensity += s.Intensity * w;
                result.Threshold += s.Threshold * w;
                result.SoftKnee += s.SoftKnee * w;
                result.SpreadRadius += s.SpreadRadius * w;
                result.DirtIntensity += s.DirtIntensity * w;
                result.Tint = result.Tint + s.Tint * w;
                totalW += w;
            }
            if (totalW > 0.0f)
            {
                result.Intensity /= totalW;
                result.Threshold /= totalW;
                result.SoftKnee /= totalW;
                result.SpreadRadius /= totalW;
                result.DirtIntensity /= totalW;
                result.Tint /= totalW;
            }
            return result;
        }

        private static DepthOfFieldSettings BlendDOFSettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = DepthOfFieldSettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.DepthOfField;
                if (!s.Enabled) continue;
                result.Enabled = true;
                result.FocusDistance += s.FocusDistance * w;
                result.NearFocusRange += s.NearFocusRange * w;
                result.FarFocusRange += s.FarFocusRange * w;
                result.NearBlurSize += s.NearBlurSize * w;
                result.FarBlurSize += s.FarBlurSize * w;
                totalW += w;
            }
            if (totalW > 0.0f)
            {
                result.FocusDistance /= totalW;
                result.NearFocusRange /= totalW;
                result.FarFocusRange /= totalW;
                result.NearBlurSize /= totalW;
                result.FarBlurSize /= totalW;
            }
            return result;
        }

        private static MotionBlurSettings BlendMotionBlurSettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = MotionBlurSettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.MotionBlur;
                if (!s.Enabled) continue;
                result.Enabled = true;
                result.Intensity += s.Intensity * w;
                result.MaxVelocity += s.MaxVelocity * w;
                result.SampleCount += (int)(s.SampleCount * w);
                totalW += w;
            }
            if (totalW > 0.0f)
            {
                result.Intensity /= totalW;
                result.MaxVelocity /= totalW;
                result.SampleCount = (int)Math.Round(result.SampleCount / totalW);
            }
            return result;
        }

        private static ChromaticAberrationSettings BlendCASettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = ChromaticAberrationSettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.ChromaticAberration;
                if (!s.Enabled) continue;
                result.Enabled = true;
                result.Intensity += s.Intensity * w;
                result.StartOffset += s.StartOffset * w;
                totalW += w;
            }
            if (totalW > 0.0f) { result.Intensity /= totalW; result.StartOffset /= totalW; }
            return result;
        }

        private static VignetteSettings BlendVignetteSettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = VignetteSettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.Vignette;
                if (!s.Enabled) continue;
                result.Enabled = true;
                result.Intensity += s.Intensity * w;
                result.Smoothness += s.Smoothness * w;
                result.Roundness += s.Roundness * w;
                result.Color = result.Color + s.Color * w;
                totalW += w;
            }
            if (totalW > 0.0f)
            {
                result.Intensity /= totalW;
                result.Smoothness /= totalW;
                result.Roundness /= totalW;
                result.Color /= totalW;
            }
            return result;
        }

        private static FilmGrainSettings BlendFilmGrainSettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = FilmGrainSettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.FilmGrain;
                if (!s.Enabled) continue;
                result.Enabled = true;
                result.Intensity += s.Intensity * w;
                result.Size += s.Size * w;
                result.Response += s.Response * w;
                totalW += w;
            }
            if (totalW > 0.0f) { result.Intensity /= totalW; result.Size /= totalW; result.Response /= totalW; }
            return result;
        }

        private static ToneMappingSettings BlendToneMappingSettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = ToneMappingSettings.Default;
            // 色调映射取最高优先级的设置
            foreach (var (vol, _) in volumes)
            {
                var s = vol.Profile.ToneMapping;
                if (s.Enabled) { result = s; break; }
            }
            return result;
        }

        private static FXAASettings BlendFXAASettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = FXAASettings.Default;
            foreach (var (vol, _) in volumes)
            {
                var s = vol.Profile.FXAA;
                if (s.Enabled) { result = s; break; }
            }
            return result;
        }

        private static GodRaySettings BlendGodRaySettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = GodRaySettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.GodRays;
                if (!s.Enabled) continue;
                result.Enabled = true;
                result.Intensity += s.Intensity * w;
                result.Decay += s.Decay * w;
                result.Samples += (int)(s.Samples * w);
                result.Weight += s.Weight * w;
                totalW += w;
            }
            if (totalW > 0.0f)
            {
                result.Intensity /= totalW;
                result.Decay /= totalW;
                result.Samples = (int)Math.Round(result.Samples / totalW);
                result.Weight /= totalW;
            }
            return result;
        }

        private static ColorGradingSettings BlendColorGradingSettings(List<(PostProcessVolume Volume, float Weight)> volumes)
        {
            var result = ColorGradingSettings.Default;
            float totalW = 0.0f;
            foreach (var (vol, w) in volumes)
            {
                var s = vol.Profile.ColorGrading;
                result.Brightness += s.Brightness * w;
                result.Contrast += s.Contrast * w;
                result.Saturation += s.Saturation * w;
                result.HueShift += s.HueShift * w;
                result.Temperature += s.Temperature * w;
                result.Tint += s.Tint * w;
                result.Lift = result.Lift + s.Lift * w;
                result.Gamma = result.Gamma + s.Gamma * w;
                result.Gain = result.Gain + s.Gain * w;
                result.Shadows = result.Shadows + s.Shadows * w;
                result.Midtones = result.Midtones + s.Midtones * w;
                result.Highlights = result.Highlights + s.Highlights * w;
                totalW += w;
            }
            if (totalW > 0.0f)
            {
                result.Brightness /= totalW;
                result.Contrast /= totalW;
                result.Saturation /= totalW;
                result.HueShift /= totalW;
                result.Temperature /= totalW;
                result.Tint /= totalW;
                result.Lift /= totalW;
                result.Gamma /= totalW;
                result.Gain /= totalW;
                result.Shadows /= totalW;
                result.Midtones /= totalW;
                result.Highlights /= totalW;
            }
            // LUT 取最高优先级
            foreach (var (vol, _) in volumes)
            {
                if (vol.Profile.ColorGrading.LutTexture != null)
                {
                    result.LutTexture = vol.Profile.ColorGrading.LutTexture;
                    result.LutIntensity = vol.Profile.ColorGrading.LutIntensity;
                    break;
                }
            }
            return result;
        }

        // ── 运行时动态参数修改（编辑器实时预览支持） ──

        /// <summary>获取全局配置的可修改引用（编辑器滑块绑定）。</summary>
        public PostProcessProfile EditGlobalProfile => GlobalProfile;

        /// <summary>通过特效类型和参数名修改全局配置。</summary>
        public void SetGlobalParameter(PostProcessEffectType type, string parameterName, float value)
        {
            _dirty = true;
            switch (type)
            {
                case PostProcessEffectType.Bloom:
                {
                    var s = GlobalProfile.Bloom;
                    SetBloomParam(ref s, parameterName, value);
                    GlobalProfile.Bloom = s;
                    break;
                }
                case PostProcessEffectType.DOF:
                {
                    var s = GlobalProfile.DepthOfField;
                    SetDOFParam(ref s, parameterName, value);
                    GlobalProfile.DepthOfField = s;
                    break;
                }
                case PostProcessEffectType.MotionBlur:
                {
                    var s = GlobalProfile.MotionBlur;
                    SetMotionBlurParam(ref s, parameterName, value);
                    GlobalProfile.MotionBlur = s;
                    break;
                }
                case PostProcessEffectType.Vignette:
                {
                    var s = GlobalProfile.Vignette;
                    SetVignetteParam(ref s, parameterName, value);
                    GlobalProfile.Vignette = s;
                    break;
                }
                case PostProcessEffectType.FilmGrain:
                {
                    var s = GlobalProfile.FilmGrain;
                    SetFilmGrainParam(ref s, parameterName, value);
                    GlobalProfile.FilmGrain = s;
                    break;
                }
                case PostProcessEffectType.ColorGrading:
                {
                    var s = GlobalProfile.ColorGrading;
                    SetColorGradingParam(ref s, parameterName, value);
                    GlobalProfile.ColorGrading = s;
                    break;
                }
                case PostProcessEffectType.ToneMapping:
                {
                    var s = GlobalProfile.ToneMapping;
                    SetToneMappingParam(ref s, parameterName, value);
                    GlobalProfile.ToneMapping = s;
                    break;
                }
            }
        }

        private static void SetBloomParam(ref BloomSettings s, string name, float v)
        {
            switch (name)
            {
                case "Intensity": s.Intensity = v; break;
                case "Threshold": s.Threshold = v; break;
                case "SoftKnee": s.SoftKnee = v; break;
                case "SpreadRadius": s.SpreadRadius = v; break;
                case "DirtIntensity": s.DirtIntensity = v; break;
            }
        }

        private static void SetDOFParam(ref DepthOfFieldSettings s, string name, float v)
        {
            switch (name)
            {
                case "FocusDistance": s.FocusDistance = v; break;
                case "NearFocusRange": s.NearFocusRange = v; break;
                case "FarFocusRange": s.FarFocusRange = v; break;
                case "NearBlurSize": s.NearBlurSize = v; break;
                case "FarBlurSize": s.FarBlurSize = v; break;
            }
        }

        private static void SetMotionBlurParam(ref MotionBlurSettings s, string name, float v)
        {
            switch (name)
            {
                case "Intensity": s.Intensity = v; break;
                case "MaxVelocity": s.MaxVelocity = v; break;
                case "SampleCount": s.SampleCount = (int)v; break;
            }
        }

        private static void SetVignetteParam(ref VignetteSettings s, string name, float v)
        {
            switch (name)
            {
                case "Intensity": s.Intensity = v; break;
                case "Smoothness": s.Smoothness = v; break;
                case "Roundness": s.Roundness = v; break;
            }
        }

        private static void SetFilmGrainParam(ref FilmGrainSettings s, string name, float v)
        {
            switch (name)
            {
                case "Intensity": s.Intensity = v; break;
                case "Size": s.Size = v; break;
                case "Response": s.Response = v; break;
            }
        }

        private static void SetColorGradingParam(ref ColorGradingSettings s, string name, float v)
        {
            switch (name)
            {
                case "Brightness": s.Brightness = v; break;
                case "Contrast": s.Contrast = v; break;
                case "Saturation": s.Saturation = v; break;
                case "HueShift": s.HueShift = v; break;
                case "Temperature": s.Temperature = v; break;
                case "Tint": s.Tint = v; break;
            }
        }

        private static void SetToneMappingParam(ref ToneMappingSettings s, string name, float v)
        {
            switch (name)
            {
                case "Exposure": s.Exposure = v; break;
                case "Contrast": s.Contrast = v; break;
                case "Saturation": s.Saturation = v; break;
            }
        }

        // ── 资源访问器（供各特效 Pass 使用） ──

        /// <summary>获取 Ping-Pong 纹理对。</summary>
        public (RHITexture A, RHITexture B) PingPongTextures => (_rtA!, _rtB!);

        /// <summary>获取 Bloom Mip链纹理。</summary>
        public RHITexture? BloomMipChain => _bloomMipChain;

        /// <summary>获取运动模糊 Tile 缓冲。</summary>
        public (RHITexture? TileMax, RHITexture? NeighborMax) MotionBlurTiles =>
            (_motionBlurTileMax, _motionBlurNeighborMax);

        /// <summary>获取 CoC 纹理。</summary>
        public RHITexture? CoCTexture => _cocTexture;

        /// <summary>获取 God Ray 缓冲。</summary>
        public (RHITexture? Occlusion, RHITexture? Scattering) GodRayTextures =>
            (_godRayOcclusion, _godRayScattering);

        /// <summary>获取曝光缓冲。</summary>
        public RHIBuffer? ExposureBuffer => _exposureBuffer;

        /// <summary>获取根签名。</summary>
        public RHIRootSignature? RootSignature => _rootSignature;

        // ── 简化渲染入口 ──

        /// <summary>
        /// 简化的后处理渲染入口，直接处理输入纹理到输出纹理。
        /// 适配编辑器视口的后处理流程。
        /// </summary>
        public void RenderPostProcess(
            RHITexture sourceTexture,
            RHITexture? targetTexture,
            RHICommandList commandList,
            float deltaTime,
            RHITexture? depthTexture = null,
            RHITexture? velocityTexture = null,
            in Matrix4x4? invProjMatrix = null,
            RHIDescriptorHandle? targetRtv = null)
        {
            if (!Enabled) return;

            _time += deltaTime;
            _frameIndex++;

            // 更新各管线设置
            _bloomPipeline?.UpdateSettings(_blendedProfile.Bloom);
            _dofPipeline?.UpdateSettings(_blendedProfile.DepthOfField);
            _motionBlurPipeline?.UpdateSettings(_blendedProfile.MotionBlur);

            var (rtA, rtB) = PingPongTextures;
            RHITexture currentInput = sourceTexture;
            RHITexture currentOutput = rtA;

            // Bloom
            if (_activeEffects.Contains(PostProcessEffectType.Bloom) && _blendedProfile.Bloom.Enabled && _bloomPipeline != null)
            {
                _bloomPipeline.Execute(commandList, currentInput, currentOutput);
                (currentInput, currentOutput) = (currentOutput, currentInput);
            }

            // DOF
            if (_activeEffects.Contains(PostProcessEffectType.DOF) && _blendedProfile.DepthOfField.Enabled && _dofPipeline != null && depthTexture != null)
            {
                _dofPipeline.Execute(commandList, currentInput, depthTexture, currentOutput, invProjMatrix ?? Matrix4x4.Identity);
                (currentInput, currentOutput) = (currentOutput, currentInput);
            }

            // MotionBlur
            if (_activeEffects.Contains(PostProcessEffectType.MotionBlur) && _blendedProfile.MotionBlur.Enabled && _motionBlurPipeline != null && velocityTexture != null)
            {
                _motionBlurPipeline.Execute(commandList, currentInput, velocityTexture, currentOutput, Matrix4x4.Identity, Matrix4x4.Identity);
                (currentInput, currentOutput) = (currentOutput, currentInput);
            }

            // ColorGrading
            if (_activeEffects.Contains(PostProcessEffectType.ColorGrading) && _lensPipeline != null)
            {
                _lensPipeline.ExecuteColorGrading(commandList, currentInput, currentOutput, _blendedProfile.ColorGrading);
                (currentInput, currentOutput) = (currentOutput, currentInput);
            }

            // Vignette + FilmGrain + ChromaticAberration (LensEffects)
            bool hasLensEffects = _activeEffects.Contains(PostProcessEffectType.Vignette) && _blendedProfile.Vignette.Enabled
                || _activeEffects.Contains(PostProcessEffectType.FilmGrain) && _blendedProfile.FilmGrain.Enabled
                || _activeEffects.Contains(PostProcessEffectType.ChromaticAberration) && _blendedProfile.ChromaticAberration.Enabled;
            if (hasLensEffects && _lensPipeline != null)
            {
                _lensPipeline.ExecuteLensEffects(commandList, currentInput, currentOutput,
                    _blendedProfile.ChromaticAberration,
                    _blendedProfile.Vignette,
                    _blendedProfile.FilmGrain,
                    _time);
                (currentInput, currentOutput) = (currentOutput, currentInput);
            }

            // ToneMapping
            if (_activeEffects.Contains(PostProcessEffectType.ToneMapping) && _blendedProfile.ToneMapping.Enabled && _lensPipeline != null)
            {
                // 颜色分级管线同时处理色调映射
                _lensPipeline.ExecuteColorGrading(commandList, currentInput, currentOutput, _blendedProfile.ColorGrading);
                (currentInput, currentOutput) = (currentOutput, currentInput);
            }

            // 最终输出到 targetRtv（全屏三角形 Blit）或 targetTexture（CopyResource）
            if (targetRtv.HasValue)
            {
                // 使用全屏三角形 Blit 到 RTV（支持格式转换）
                FinalBlitToRTV(commandList, currentInput, targetRtv.Value, _width, _height);
            }
            else if (targetTexture != null && currentInput != targetTexture)
            {
                commandList.CopyResource(targetTexture, currentInput);
            }
        }

        /// <summary>使用全屏三角形将输入纹理 Blit 到 RTV（HDR → LDR）。</summary>
        private void FinalBlitToRTV(RHICommandList cmd, RHITexture source, RHIDescriptorHandle rtv, int width, int height)
        {
            if (_blitPso == null || _blitRootSig == null) return;

            cmd.TransitionTo(source, RHIResourceState.PixelShaderResource);

            cmd.SetGraphicsRootSignature(_blitRootSig);
            cmd.SetPipelineState(_blitPso);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmd.OMSetRenderTargets(new[] { rtv }, null);
            cmd.RSSetViewport(0, 0, width, height, 0, 1);
            cmd.RSSetScissorRect(0, 0, width, height);

            // 绑定 SourceTexture SRV (t0)
            var srv = _device.CreateSRV(source);
            cmd.SetGraphicsRootDescriptorTable(0, srv);

            // FullScreenTriangle.hlsl 使用 SV_VertexID，无需顶点缓冲
            cmd.DrawInstanced(3, 1, 0, 0);
        }

        // ── 释放 ──

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            _rtA?.Dispose();
            _rtB?.Dispose();
            _bloomMipChain?.Dispose();
            _motionBlurTileMax?.Dispose();
            _motionBlurNeighborMax?.Dispose();
            _cocTexture?.Dispose();
            _godRayOcclusion?.Dispose();
            _godRayScattering?.Dispose();
            _exposureBuffer?.Dispose();
            _rootSignature?.Dispose();

            _bloomPipeline?.Dispose();
            _dofPipeline?.Dispose();
            _motionBlurPipeline?.Dispose();
            _lensPipeline?.Dispose();
            _blitPso?.Dispose();
            _blitRootSig?.Dispose();

            foreach (var vol in _volumes)
                vol.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
